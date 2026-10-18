using Pi.Client;
using Pi.CodingAgent.Experimental;
using Pi.Protocol;
using Pi.Server;

// Pi.Server declares its own ConnectionState; the client one is what the reconnect drives.
using ClientConnectionState = Pi.Client.ConnectionState;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// A relay WebSocket that records everything and raises its events on demand. Shared by the
/// protocol and host/client test classes; the browser API never fires inside the constructor, so
/// tests raise after the code under test has subscribed.
/// </summary>
internal sealed class FakeRelayWebSocket : IRadiusRelayWebSocket
{
    public string BinaryType { get; set; } = "arraybuffer";

    public long BufferedAmount { get; private set; }

    public string Protocol { get; init; } = "";

    public int ReadyState { get; set; } = 1;

    public int Open => 1;

    private readonly TaskCompletionSource _firstSend = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the first binary frame reaches the socket, however the write ends.</summary>
    public Task FirstSend => _firstSend.Task;

    public List<string> SentText { get; } = [];

    public List<byte[]> Sent { get; } = [];

    public List<(int? Code, string? Reason)> Closes { get; } = [];

    public event Action? Opened;

    public event Action<RadiusRelayMessage>? MessageReceived;

    public event Action<int, string>? Closed;

    public event Action<RadiusRelayWebSocketError>? Errored;

    public void Send(byte[] data)
    {
        Sent.Add(data);
        BufferedAmount += data.Length;
        _firstSend.TrySetResult();
    }

    public void Send(string data) => SentText.Add(data);

    public void Close(int? code = null, string? reason = null)
    {
        Closes.Add((code, reason));
        ReadyState = 3;
        // A browser fires the close event from the event loop once the closing handshake
        // completes — never synchronously inside close(). Scheduling it keeps the re-entrant
        // ordering faithful: a close issued from an error path still lets the original error
        // win the terminal slot first.
        var closeCode = code ?? 1000;
        var closeReason = reason ?? "";
        _ = Task.Run(() => Closed?.Invoke(closeCode, closeReason));
    }

    public void DrainAll() => BufferedAmount = 0;

    /// <summary>
    /// Whether the code under test has subscribed to the message, close and error events — the
    /// host subscribes inside ServeAsync, which runs after the handshake resolves. Tests poll this
    /// before raising: an event raised with no subscriber is lost, exactly as in a browser.
    /// </summary>
    public bool Serving => MessageReceived is not null && Closed is not null && Errored is not null;

    /// <summary>
    /// Whether the opening handshake is subscribed — <see cref="RadiusRelaySockets.OpenAsync"/>
    /// hooks the open, close and error events before awaiting. On a retry the loop resumes from a
    /// real backoff delay, so the subscription lands asynchronously and tests must poll this
    /// before raising <see cref="RaiseOpened"/>.
    /// </summary>
    public bool OpenPending => Opened is not null && Closed is not null && Errored is not null;

    public void RaiseOpened() => Opened?.Invoke();

    public void RaiseMessage(RadiusRelayMessage message) => MessageReceived?.Invoke(message);

    public void RaiseClosed(int code, string reason) => Closed?.Invoke(code, reason);

    public void RaiseErrored(Exception? error, string? message)
        => Errored?.Invoke(new RadiusRelayWebSocketError(error, message));
}

/// <summary>Records the callbacks the relay host delivers for one accepted connection.</summary>
internal sealed class RecordingByteConnectionHandler : IByteConnectionHandler
{
    public List<byte[]> Data { get; } = [];

    public int CloseCount { get; private set; }

    public List<Exception> Errors { get; } = [];

    public void OnData(byte[] chunk) => Data.Add(chunk);

    public void OnClose() => CloseCount++;

    public void OnError(Exception error) => Errors.Add(error);
}

/// <summary>
/// The client surface <see cref="RadiusClientReconnect"/> drives: a scripted stand-in with
/// controllable connection state, reattach behaviour and change events.
/// </summary>
internal sealed class StubReconnectClient : IRadiusReconnectClient
{
    private readonly List<Action<ConnectionStateChange>> _connectionListeners = [];
    private readonly List<AttachmentChangeListener> _attachmentListeners = [];
    private bool _connected;

    public RpcTarget.SessionTarget? Attachment { get; set; }

    public bool Connected
    {
        get => _connected;
        set
        {
            _connected = value;
            RaiseClientConnectionState(value ? ClientConnectionState.Connected : ClientConnectionState.Disconnected);
        }
    }

    public ClientConnectionState ConnectionState { get; private set; } = ClientConnectionState.Disconnected;

    public List<string> DisconnectReasons { get; } = [];

    public int ReconnectCalls { get; private set; }

    /// <summary>Thrown by <see cref="ReconnectAsync"/> until the script lets it through.</summary>
    public Exception? ReconnectError { get; set; }

    /// <summary>When set, a failing reconnect still reports the client as connected.</summary>
    public bool StayConnectedOnFailure { get; set; }

    public List<string> Reattached { get; } = [];

    public void Disconnect(string reason)
    {
        DisconnectReasons.Add(reason);
        _connected = false;
        ConnectionState = ClientConnectionState.Disconnected;
        RaiseClientConnectionState(ClientConnectionState.Disconnected);
    }

    public Unsubscribe OnConnectionStateChange(Action<ConnectionStateChange> listener)
    {
        _connectionListeners.Add(listener);
        return () => _connectionListeners.Remove(listener);
    }

    public Unsubscribe OnAttachmentChange(AttachmentChangeListener listener)
    {
        _attachmentListeners.Add(listener);
        return () => _attachmentListeners.Remove(listener);
    }

    public Task ReconnectAsync()
    {
        ReconnectCalls++;
        if (ReconnectError is not null)
        {
            if (StayConnectedOnFailure)
            {
                _connected = true;
            }

            return Task.FromException(ReconnectError);
        }

        _connected = true;
        ConnectionState = ClientConnectionState.Connected;
        RaiseClientConnectionState(ClientConnectionState.Connected);
        return Task.CompletedTask;
    }

    public void RaiseAttachment(RpcTarget.SessionTarget? attachment)
    {
        Attachment = attachment;
        foreach (var listener in _attachmentListeners.ToArray())
        {
            listener(attachment);
        }
    }

    public void RaiseClientConnectionState(ClientConnectionState state)
    {
        this.ConnectionState = state;
        foreach (var listener in _connectionListeners.ToArray())
        {
            listener(new ConnectionStateChange(state));
        }
    }
}
