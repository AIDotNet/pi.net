// ============================================================================
// Radius relay host/client — port of experimental/radius-relay.ts, session half (4g-3d-2b)
// ============================================================================
//
// The host side turns the relay's multiplexed WebSocket into byte connections the server can
// accept: connection_open spawns a RelayServerByteConnection, binary frames are routed by their
// connection id, and an unknown id is answered with a close instead of being dropped. The client
// side is a ByteTransportFactory over the same wire, plus a reconnect helper that restores the
// client's last attached Session after a drop.
//
// Everything network-shaped goes through IRadiusRelayWebSocket (see RadiusRelayProtocol.cs), so
// the host, the transport and the reconnect logic are all testable against a fake socket — the
// same way TS tests them against a fake undici WebSocket.

using Pi.Client;
using Pi.Protocol;
using Pi.Server;

// Pi.Server declares its own ConnectionState; the client one is what the reconnect drives.
using ClientConnectionState = Pi.Client.ConnectionState;

namespace Pi.CodingAgent.Experimental;

/// <summary>
/// One relay-owned byte connection handed to the server. Port of TS <c>RelayServerByteConnection</c>;
/// implements the C# <see cref="IByteConnection"/> surface directly (same shape as TS <c>ByteConnection</c>).
/// </summary>
public sealed class RelayServerByteConnection : IByteConnection
{
    private readonly Func<byte[], Task> _sendChunk;
    private readonly Func<byte[]?, Task> _closeConnection;
    private bool _closed;

    public RelayServerByteConnection(Func<byte[], Task> sendChunk, Func<byte[]?, Task> closeConnection)
    {
        _sendChunk = sendChunk;
        _closeConnection = closeConnection;
    }

    public bool Closed => _closed;

    public Task SendAsync(byte[] chunk, CancellationToken cancellationToken = default)
    {
        if (_closed)
        {
            return Task.FromException(new InvalidOperationException("Radius relay connection is closed"));
        }

        return _sendChunk(chunk);
    }

    public async Task CloseAsync(byte[]? finalChunk = null)
    {
        if (_closed) return;
        _closed = true;
        await _closeConnection(finalChunk).ConfigureAwait(false);
    }

    /// <summary>TS <c>markClosed</c>: closed without the close handshake (the peer already left).</summary>
    public void MarkClosed() => _closed = true;
}

/// <summary>TS <c>RadiusRelayHostOptions</c>. The server surface is reduced to <c>accept</c>.</summary>
public sealed class RadiusRelayHostOptions
{
    public required string ServerId { get; init; }

    public required Func<IByteConnection, IByteConnectionHandler> Accept { get; init; }

    public required RadiusRelayAuthResolver Auth { get; init; }

    public RadiusRelayWebSocketFactory? WebSocketFactory { get; init; }

    public Action<RadiusRelayHostStatus>? OnStatus { get; init; }
}

/// <summary>
/// Maintain the experimental server's multiplexed, authenticated Radius host connection.
/// Port of TS <c>RadiusRelayHost</c>.
/// </summary>
public sealed class RadiusRelayHost
{
    private readonly RadiusRelayHostOptions _options;
    private readonly CancellationTokenSource _abortController = new();
    private readonly Dictionary<string, (RelayServerByteConnection Connection, IByteConnectionHandler Handler)> _connections = [];
    private readonly Lock _connectionsGate = new();
    private IRadiusRelayWebSocket? _socket;
    private OrderedWebSocketWriter? _writer;
    private Task? _loop;
    private bool _closed;

    public RadiusRelayHost(RadiusRelayHostOptions options)
    {
        _options = options;
    }

    /// <summary>TS <c>start</c>: idempotent — the loop is created once.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        _loop = RunAsync();
    }

    /// <summary>TS <c>close</c>: idempotent; awaits the loop once it has unwound.</summary>
    public async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        await _abortController.CancelAsync().ConfigureAwait(false);
        _writer?.Close();
        if (_socket is not null)
        {
            // TS calls the raw close here (not the guarded helper); the adapter never throws.
            _socket.Close(1000, "Pi server stopped");
        }

        DropConnections(null);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }
    }

    private async Task RunAsync()
    {
        var retryMs = RadiusRelay.HostRetryInitialMs;
        while (!_closed)
        {
            try
            {
                var auth = await _options.Auth.ResolveAsync(new ResolveRadiusRelayAuthOptions
                {
                    Required = false,
                    Signal = _abortController.Token,
                }).ConfigureAwait(false);
                if (auth is null)
                {
                    _options.OnStatus?.Invoke(new RadiusRelayHostStatus.NotAuthenticated());
                    await RadiusRelaySockets.DelayAsync(
                        RadiusRelay.MissingAuthRetryMs, _abortController.Token, unref: true).ConfigureAwait(false);
                    continue;
                }

                _options.OnStatus?.Invoke(new RadiusRelayHostStatus.Connecting());
                var socket = await RadiusRelaySockets.OpenAsync(
                    RadiusRelay.WebSocketUrl(auth.Gateway, _options.ServerId),
                    RadiusRelay.HostSubprotocol,
                    auth.Token,
                    _options.WebSocketFactory ?? RadiusRelaySockets.DefaultWebSocketFactory,
                    _abortController.Token).ConfigureAwait(false);
                retryMs = RadiusRelay.HostRetryInitialMs;
                _options.OnStatus?.Invoke(new RadiusRelayHostStatus.Connected());
                await ServeAsync(socket).ConfigureAwait(false);
                if (!_closed)
                {
                    throw new InvalidOperationException("Radius relay host disconnected");
                }
            }
            catch (Exception error) when (!_closed && !_abortController.IsCancellationRequested)
            {
                _options.OnStatus?.Invoke(new RadiusRelayHostStatus.Retrying(
                    RadiusRelaySockets.ErrorMessage(error)));
                try
                {
                    await RadiusRelaySockets.DelayAsync(
                        retryMs, _abortController.Token, unref: true).ConfigureAwait(false);
                }
                catch
                {
                    // The abort signal fired during the backoff: leave the loop.
                    break;
                }

                retryMs = Math.Min(retryMs * 2, RadiusRelay.HostRetryMaxMs);
            }
            catch
            {
                break;
            }
        }
    }

    private Task ServeAsync(IRadiusRelayWebSocket socket)
    {
        _socket = socket;
        _writer = new OrderedWebSocketWriter(socket);
        var served = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = false;

        void Cleanup()
        {
            socket.MessageReceived -= OnMessage;
            socket.Closed -= OnClose;
            socket.Errored -= OnErrored;
            _writer?.Close();
            if (ReferenceEquals(_socket, socket))
            {
                _socket = null;
                _writer = null;
            }
        }

        void Finish(Exception? error)
        {
            if (terminal) return;
            terminal = true;
            Cleanup();
            DropConnections(error);
            if (error is not null)
            {
                served.TrySetException(error);
            }
            else
            {
                served.TrySetResult();
            }
        }

        void OnMessage(RadiusRelayMessage message)
        {
            try
            {
                HandleHostMessage(message);
            }
            catch (Exception error)
            {
                // TS issues the close first and then calls finish, but Node's event loop guarantees
                // the synchronous finish wins the terminal slot over the close event it queues.
                // .NET delivers a socket's close from another thread, so the port settles the serve
                // first and closes afterwards: the original error is the one the retry loop reports,
                // whatever the delivery timing. (The reordering was a real flake under full-suite
                // load — the close event could win the slot between the two statements.)
                Finish(RadiusRelaySockets.ToError(error));
                RadiusRelaySockets.Close(socket, RadiusRelay.LocalProtocolErrorCloseCode, "Radius relay protocol error");
            }
        }

        void OnClose(int code, string reason)
        {
            // TS: a 1000 close is an orderly shutdown and resolves the serve promise.
            var orderly = code == 1000;
            Finish(orderly
                ? null
                : new InvalidOperationException(
                    $"Radius relay host closed ({code}{(string.IsNullOrEmpty(reason) ? "" : $": {reason}")})"));
        }

        void OnErrored(RadiusRelayWebSocketError error) => Finish(RadiusRelaySockets.WebSocketError(error));

        socket.MessageReceived += OnMessage;
        socket.Closed += OnClose;
        socket.Errored += OnErrored;
        return served.Task;
    }

    private void HandleHostMessage(RadiusRelayMessage message)
    {
        if (message.Text is not null)
        {
            var control = RadiusRelayFrames.ParseHostControlMessage(message.Text);
            switch (control.Type)
            {
                case "ping":
                    SendControlDetached("pong");
                    return;
                case "pong":
                    return;
                case "connection_open":
                    OpenConnection(control.ConnectionId!);
                    return;
                case "connection_close":
                    RemoteCloseConnection(control.ConnectionId!);
                    return;
            }
        }

        if (message.Binary is null)
        {
            throw new InvalidOperationException("Radius relay data message must be binary");
        }

        var frame = RadiusRelayFrames.ParseDataFrame(message.Binary);
        if (frame is null)
        {
            throw new InvalidOperationException("Invalid Radius relay data frame");
        }

        (RelayServerByteConnection Connection, IByteConnectionHandler Handler) active;
        lock (_connectionsGate)
        {
            if (!_connections.TryGetValue(frame.Value.ConnectionId, out active))
            {
                // TS fires this without a catch: nothing happens if the write fails.
                _ = SendControlAsync("connection_close", frame.Value.ConnectionId, 1000);
                return;
            }
        }

        active.Handler.OnData(frame.Value.Payload);
    }

    private void OpenConnection(string connectionId)
    {
        lock (_connectionsGate)
        {
            if (_connections.ContainsKey(connectionId))
            {
                throw new InvalidOperationException("Radius relay reused a connection ID");
            }
        }

        var connection = new RelayServerByteConnection(
            chunk => SendDataAsync(connectionId, chunk),
            finalChunk => ServerCloseConnectionAsync(connectionId, finalChunk));
        var handler = _options.Accept(connection);
        var refused = false;
        lock (_connectionsGate)
        {
            if (connection.Closed)
            {
                refused = true;
            }
            else
            {
                _connections[connectionId] = (connection, handler);
            }
        }

        if (refused)
        {
            // The server refused the connection synchronously; tell the relay with a protocol error.
            _ = SendControlAsync("connection_close", connectionId, 1012);
        }
    }

    private void RemoteCloseConnection(string connectionId)
    {
        (RelayServerByteConnection Connection, IByteConnectionHandler Handler) active;
        lock (_connectionsGate)
        {
            if (!_connections.Remove(connectionId, out active)) return;
        }

        active.Connection.MarkClosed();
        active.Handler.OnClose();
    }

    private async Task ServerCloseConnectionAsync(string connectionId, byte[]? finalChunk)
    {
        lock (_connectionsGate)
        {
            if (!_connections.Remove(connectionId)) return;
        }
        if (finalChunk is not null)
        {
            await RequireWriter()
                .SendAsync(RadiusRelayFrames.EncodeDataFrame(connectionId, finalChunk)).ConfigureAwait(false);
        }

        await SendControlAsync("connection_close", connectionId, 1000).ConfigureAwait(false);
    }

    private Task SendDataAsync(string connectionId, byte[] chunk)
    {
        lock (_connectionsGate)
        {
            if (!_connections.ContainsKey(connectionId))
            {
                return Task.FromException(new InvalidOperationException("Radius relay connection is closed"));
            }
        }

        return RequireWriter().SendAsync(RadiusRelayFrames.EncodeDataFrame(connectionId, chunk));
    }

    private Task SendControlAsync(string type, string? connectionId = null, int? code = null)
        => RequireWriter().SendAsync(RadiusRelayFrames.EncodeHostControlMessage(type, connectionId, code));

    /// <summary>
    /// TS <c>void this.#sendControl(...).catch(...)</c>: a detached pong whose failure closes the
    /// socket with a transport error. Other detached sends have no catch and are simply dropped.
    /// </summary>
    private void SendControlDetached(string type)
    {
        var sent = SendControlAsync(type);
        if (sent.IsCompletedSuccessfully) return;
        _ = sent.ContinueWith(
            static (task, state) =>
            {
                var host = (RadiusRelayHost)state!;
                if (host._socket is not null)
                {
                    RadiusRelaySockets.Close(
                        host._socket, RadiusRelay.LocalTransportErrorCloseCode, "Radius relay send failed");
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private OrderedWebSocketWriter RequireWriter()
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("Radius relay host is disconnected");
        }

        return _writer;
    }

    private void DropConnections(Exception? error)
    {
        List<(RelayServerByteConnection Connection, IByteConnectionHandler Handler)> dropped;
        lock (_connections)
        {
            dropped = [.. _connections.Values];
            _connections.Clear();
        }

        foreach (var active in dropped)
        {
            active.Connection.MarkClosed();
            if (error is not null)
            {
                active.Handler.OnError(error);
            }
            else
            {
                active.Handler.OnClose();
            }
        }
    }
}

/// <summary>TS <c>createRadiusClientTransportFactory</c> options.</summary>
public sealed class RadiusClientTransportOptions
{
    public required string ServerId { get; init; }

    public required RadiusRelayAuthResolver Auth { get; init; }

    public RadiusRelayWebSocketFactory? WebSocketFactory { get; init; }
}

/// <summary>The client half of the relay: a <see cref="ByteTransportFactory"/> over the relay wire.</summary>
public static class RadiusRelayClients
{
    /// <summary>TS <c>createRadiusClientTransportFactory</c>.</summary>
    public static ByteTransportFactory CreateTransportFactory(RadiusClientTransportOptions options)
        => handlers => CreateTransportAsync(options, handlers);

    private static async Task<IByteTransport> CreateTransportAsync(
        RadiusClientTransportOptions options, IByteTransportHandlers handlers)
    {
        var auth = await options.Auth.ResolveAsync(new ResolveRadiusRelayAuthOptions { Required = true })
            .ConfigureAwait(false);
        if (auth is null)
        {
            throw new InvalidOperationException("Radius authentication is required");
        }

        var socket = await RadiusRelaySockets.OpenAsync(
            RadiusRelay.WebSocketUrl(auth.Gateway, options.ServerId),
            RadiusRelay.ClientSubprotocol,
            auth.Token,
            options.WebSocketFactory ?? RadiusRelaySockets.DefaultWebSocketFactory).ConfigureAwait(false);
        return new RadiusClientByteTransport(socket, handlers);
    }
}

/// <summary>
/// One relay client connection as a <see cref="IByteTransport"/>. Port of TS <c>RadiusClientByteTransport</c>.
/// </summary>
public sealed class RadiusClientByteTransport : IByteTransport
{
    private readonly IRadiusRelayWebSocket _socket;
    private readonly IByteTransportHandlers _handlers;
    private readonly OrderedWebSocketWriter _writer;
    private bool _closed;

    public RadiusClientByteTransport(IRadiusRelayWebSocket socket, IByteTransportHandlers handlers)
    {
        _socket = socket;
        _handlers = handlers;
        _writer = new OrderedWebSocketWriter(socket);
        socket.MessageReceived += OnMessage;
        socket.Closed += OnClose;
        socket.Errored += OnErrored;
    }

    public Task SendAsync(byte[] chunk)
    {
        if (_closed)
        {
            return Task.FromException(new InvalidOperationException("Radius relay client is closed"));
        }

        // TS copies at the call site (chunk.slice().buffer) and again inside the writer; the
        // writer's clone already covers the queued frame, so the redundant copy is dropped.
        return _writer.SendAsync(chunk);
    }

    public void Close()
    {
        if (!MarkClosed()) return;
        _socket.Close(1000, "Pi client closed");
    }

    private void OnMessage(RadiusRelayMessage message)
    {
        if (_closed) return;
        if (message.Binary is null)
        {
            Fail(new InvalidOperationException("Radius relay client received a non-binary message"));
            return;
        }

        _handlers.OnData(message.Binary);
    }

    private void OnClose(int code, string reason)
    {
        if (MarkClosed())
        {
            _handlers.OnClose();
        }
    }

    private void OnErrored(RadiusRelayWebSocketError error) => Fail(RadiusRelaySockets.WebSocketError(error));

    private void Fail(Exception error)
    {
        if (!MarkClosed()) return;
        RadiusRelaySockets.Close(_socket, RadiusRelay.LocalTransportErrorCloseCode, "Radius relay transport error");
        _handlers.OnError(error);
    }

    private bool MarkClosed()
    {
        if (_closed) return false;
        _closed = true;
        _writer.Close();
        _socket.MessageReceived -= OnMessage;
        _socket.Closed -= OnClose;
        _socket.Errored -= OnErrored;
        return true;
    }
}

/// <summary>
/// The slice of the client <see cref="RadiusClientReconnect"/> drives. Port of the TS
/// <c>RadiusReconnectClient</c> pick; <see cref="Client"/> satisfies it member for member.
/// </summary>
public interface IRadiusReconnectClient
{
    RpcTarget.SessionTarget? Attachment { get; }

    bool Connected { get; }

    ClientConnectionState ConnectionState { get; }

    void Disconnect(string reason);

    Unsubscribe OnConnectionStateChange(Action<ConnectionStateChange> listener);

    Unsubscribe OnAttachmentChange(AttachmentChangeListener listener);

    Task ReconnectAsync();
}

/// <summary>
/// Reconnect one established Radius client and restore its last selected Session.
/// Port of TS <c>RadiusClientReconnect</c>.
/// </summary>
public sealed class RadiusClientReconnect : IAsyncDisposable
{
    private readonly IRadiusReconnectClient _client;
    private readonly Func<string, Task> _reattach;
    private readonly CancellationTokenSource _abortController = new();
    private readonly Unsubscribe _removeConnectionListener;
    private readonly Unsubscribe _removeAttachmentListener;
    private string? _desiredSessionId;
    private Task? _reconnecting;
    private bool _disposed;

    public RadiusClientReconnect(IRadiusReconnectClient client, Func<string, Task> reattach)
    {
        _client = client;
        _reattach = reattach;
        _desiredSessionId = client.Attachment?.SessionId;
        _removeAttachmentListener = client.OnAttachmentChange(attachment =>
        {
            if (attachment is not null)
            {
                _desiredSessionId = attachment.SessionId;
            }
            else if (client.Connected)
            {
                _desiredSessionId = null;
            }
        });
        _removeConnectionListener = client.OnConnectionStateChange(change =>
        {
            if (change.State == ClientConnectionState.Disconnected && !_disposed)
            {
                StartReconnect();
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _removeConnectionListener();
        _removeAttachmentListener();
        await _abortController.CancelAsync().ConfigureAwait(false);
        if (_client.ConnectionState != ClientConnectionState.Disconnected)
        {
            _client.Disconnect("Radius reconnect stopped");
        }

        var pending = _reconnecting;
        if (pending is not null)
        {
            await pending.ConfigureAwait(false);
        }
    }

    private void StartReconnect()
    {
        if (_reconnecting is not null) return;
        // TS: #reconnecting = run().finally(() => #reconnecting = undefined) — the slot frees
        // itself when the loop ends, so a later disconnect starts a fresh one.
        _reconnecting = RunSlotAsync();
    }

    private async Task RunSlotAsync()
    {
        try
        {
            await RunReconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            _reconnecting = null;
        }
    }

    private async Task RunReconnectAsync()
    {
        var retryMs = RadiusRelay.ClientRetryInitialMs;
        while (!_disposed && !_client.Connected)
        {
            try
            {
                await RadiusRelaySockets.DelayAsync(
                    retryMs, _abortController.Token, unref: false).ConfigureAwait(false);
                await _client.ReconnectAsync().ConfigureAwait(false);
                var sessionId = _desiredSessionId;
                if (sessionId is not null)
                {
                    await _reattach(sessionId).ConfigureAwait(false);
                }

                return;
            }
            catch (Exception error) when (!_disposed && !_abortController.IsCancellationRequested)
            {
                if (_client.Connected)
                {
                    _client.Disconnect(RadiusRelaySockets.ErrorMessage(error));
                }

                retryMs = Math.Min(retryMs * 2, RadiusRelay.ClientRetryMaxMs);
            }
            catch
            {
                return;
            }
        }
    }
}
