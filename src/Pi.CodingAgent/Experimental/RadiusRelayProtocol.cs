// ============================================================================
// Radius relay protocol — port of experimental/radius-relay.ts, protocol core (4g-3d-2)
// ============================================================================
//
// The relay multiplexes byte connections over one authenticated WebSocket: text frames carry JSON
// control messages (ping/pong, connection_open/connection_close), binary frames carry a fixed
// 18-byte header (version, type, the 16 bytes of a UUIDv4 connection id) followed by the payload.
// The connection id doubles as the frame's routing key, so a frame for an unknown connection is
// answered with a close instead of being dropped silently.
//
// TS speaks the browser WebSocket API (binaryType / bufferedAmount / readyState / event listeners).
// This port keeps the same surface as an interface — the host and client halves stay testable
// against a fake socket, exactly as TS tests them against a fake undici WebSocket.

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.Protocol;

namespace Pi.CodingAgent.Experimental;

/// <summary>The relay's wire constants. TS keeps them at module scope.</summary>
public static class RadiusRelay
{
    /// <summary>TS <c>RADIUS_RELAY_HOST_SUBPROTOCOL</c>.</summary>
    public const string HostSubprotocol = "pi-session-relay.host.v1";

    /// <summary>TS <c>RADIUS_RELAY_CLIENT_SUBPROTOCOL</c>.</summary>
    public const string ClientSubprotocol = "pi-session-relay.client.v1";

    /// <summary>TS <c>MAX_PENDING_BYTES</c>.</summary>
    public static long MaxPendingBytes { get; } = Frame.DefaultMaxFrameLength * 4;

    /// <summary>TS <c>DRAIN_THRESHOLD_BYTES</c>.</summary>
    public static long DrainThresholdBytes { get; } = 1024 * 1024;

    /// <summary>TS <c>HOST_RETRY_INITIAL_MS</c>.</summary>
    public const int HostRetryInitialMs = 1_000;

    /// <summary>TS <c>HOST_RETRY_MAX_MS</c>.</summary>
    public const int HostRetryMaxMs = 30_000;

    /// <summary>TS <c>MISSING_AUTH_RETRY_MS</c>.</summary>
    public const int MissingAuthRetryMs = 30_000;

    /// <summary>TS <c>CLIENT_RETRY_INITIAL_MS</c>.</summary>
    public const int ClientRetryInitialMs = 1_000;

    /// <summary>TS <c>CLIENT_RETRY_MAX_MS</c>.</summary>
    public const int ClientRetryMaxMs = 30_000;

    /// <summary>
    /// TS <c>LOCAL_PROTOCOL_ERROR_CLOSE_CODE</c>: an application code, because the browser WebSocket
    /// API refuses to send RFC codes such as 1002.
    /// </summary>
    public const int LocalProtocolErrorCloseCode = 4000;

    /// <summary>TS <c>LOCAL_TRANSPORT_ERROR_CLOSE_CODE</c>.</summary>
    public const int LocalTransportErrorCloseCode = 4001;

    /// <summary>TS <c>relayWebSocketUrl</c>: the gateway origin with its scheme swapped for the WebSocket one.</summary>
    public static string WebSocketUrl(string gateway, string serverId)
    {
        var url = new Uri(new Uri(gateway), $"/v1/session-relays/{serverId}/connect");
        var scheme = url.Scheme switch
        {
            "https" => "wss",
            "http" => "ws",
            _ => throw new InvalidOperationException($"Unsupported Radius gateway protocol: {url.Scheme}"),
        };

        // TS prints url.toString(), which drops a default port and keeps an explicit one.
        var defaultPort = scheme == "wss" ? 443 : 80;
        var authority = url.Port == defaultPort ? url.Host : $"{url.Host}:{url.Port}";
        return $"{scheme}://{authority}{url.AbsolutePath}";
    }
}

/// <summary>TS <c>RadiusRelayHostStatus</c>.</summary>
public abstract record RadiusRelayHostStatus
{
    public sealed record NotAuthenticated : RadiusRelayHostStatus;
    public sealed record Connecting : RadiusRelayHostStatus;
    public sealed record Connected : RadiusRelayHostStatus;
    public sealed record Retrying(string Error) : RadiusRelayHostStatus;
}

/// <summary>One relay error event. TS <c>RadiusRelayErrorEvent</c>.</summary>
public sealed record RadiusRelayWebSocketError(Exception? Error, string? Message);

/// <summary>
/// One relay message event: either text (control JSON) or binary (a data frame).
/// TS carries <c>unknown</c> and switches on the runtime type.
/// </summary>
public sealed record RadiusRelayMessage(byte[]? Binary, string? Text)
{
    public static RadiusRelayMessage BinaryMessage(byte[] data) => new(data, null);

    public static RadiusRelayMessage TextMessage(string text) => new(null, text);
}

/// <summary>
/// The WebSocket surface the relay needs. Port of the TS <c>RadiusRelayWebSocket</c> interface —
/// the browser API TS gets from undici, expressed here so a production adapter and test fakes are
/// interchangeable.
/// </summary>
public interface IRadiusRelayWebSocket
{
    /// <summary>
    /// Inert in .NET (there is no blob/arraybuffer distinction): messages always arrive as bytes.
    /// Kept because TS sets it before reading anything.
    /// </summary>
    string BinaryType { get; set; }

    /// <summary>Bytes handed to the transport but not yet written. Drives the writer's drain loop.</summary>
    long BufferedAmount { get; }

    /// <summary>The subprotocol the server selected.</summary>
    string Protocol { get; }

    /// <summary>The socket's ready state; compare against <see cref="Open"/>.</summary>
    int ReadyState { get; }

    /// <summary>The OPEN ready state (the browser WebSocket constant).</summary>
    int Open => 1;

    void Send(byte[] data);

    void Send(string data);

    /// <summary>Initiate a closing handshake. TS allows omitting the code.</summary>
    void Close(int? code = null, string? reason = null);

    event Action? Opened;

    event Action<RadiusRelayMessage>? MessageReceived;

    /// <summary>The close code and reason, as the browser close event reports them.</summary>
    event Action<int, string>? Closed;

    event Action<RadiusRelayWebSocketError>? Errored;
}

/// <summary>TS <c>RadiusRelayWebSocketFactory</c>.</summary>
public delegate IRadiusRelayWebSocket RadiusRelayWebSocketFactory(string url, string protocol, string authorization);

/// <summary>
/// Port of the framing half of <c>radius-relay.ts</c>: the data frame codec, the control message
/// grammar, and the gateway URL shape.
/// </summary>
public static class RadiusRelayFrames
{
    /// <summary>TS <c>RELAY_DATA_HEADER_BYTES</c>: version + type + the 16 connection-id bytes.</summary>
    public const int HeaderBytes = 18;

    private const byte FrameVersion = 1;
    private const byte FrameType = 1;

    /// <summary>TS <c>CONNECTION_ID_PATTERN</c>: a canonical lowercase UUIDv4.</summary>
    private static readonly Regex ConnectionIdPattern = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>TS <c>encodeRelayDataFrame</c>.</summary>
    public static byte[] EncodeDataFrame(string connectionId, byte[] payload)
    {
        if (!ConnectionIdPattern.IsMatch(connectionId))
        {
            throw new ArgumentException("Invalid Radius relay connection ID");
        }

        var frame = new byte[HeaderBytes + payload.Length];
        frame[0] = FrameVersion;
        frame[1] = FrameType;
        var hex = connectionId.Replace("-", "", StringComparison.Ordinal);
        for (var index = 0; index < 16; index++)
        {
            frame[index + 2] = byte.Parse(hex.AsSpan(index * 2, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        payload.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    /// <summary>TS <c>parseRelayDataFrame</c>: <see langword="null"/> when the frame is not one of ours.</summary>
    public static (string ConnectionId, byte[] Payload)? ParseDataFrame(byte[] frame)
    {
        if (frame.Length < HeaderBytes) return null;
        if (frame[0] != FrameVersion || frame[1] != FrameType) return null;
        var hex = new StringBuilder(32);
        for (var index = 2; index < HeaderBytes; index++)
        {
            hex.Append(frame[index].ToString("x2"));
        }

        var text = hex.ToString();
        var connectionId = $"{text[..8]}-{text[8..12]}-{text[12..16]}-{text[16..20]}-{text[20..]}";
        if (!ConnectionIdPattern.IsMatch(connectionId)) return null;
        return (connectionId, frame[HeaderBytes..]);
    }

    /// <summary>TS <c>parseHostControlMessage</c>; throws on anything the grammar does not allow.</summary>
    public static HostControlMessage ParseHostControlMessage(string value)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Invalid Radius relay control message");
        }

        if (parsed is not JsonObject message)
        {
            throw new InvalidOperationException("Invalid Radius relay control message");
        }

        var versionNode = message["version"];
        if (versionNode is not JsonValue versionValue
            || versionValue.GetValueKind() != JsonValueKind.Number
            || versionValue.GetValue<double>() != 1)
        {
            throw new InvalidOperationException("Unsupported Radius relay control version");
        }

        var typeNode = message["type"];
        var type = typeNode is JsonValue typeValue && typeValue.GetValueKind() == JsonValueKind.String
            ? typeValue.GetValue<string>()
            : null;
        if (type is "ping" or "pong")
        {
            return new HostControlMessage(1, type, null, null);
        }

        if (type is "connection_open" or "connection_close")
        {
            var idNode = message["connection_id"];
            var connectionId = idNode is JsonValue idValue && idValue.GetValueKind() == JsonValueKind.String
                ? idValue.GetValue<string>()
                : null;
            var codeNode = message["code"];
            var code = codeNode is JsonValue codeValue && codeValue.GetValueKind() == JsonValueKind.Number
                && codeValue.TryGetValue<int>(out var closeCode)
                ? closeCode
                : (int?)null;
            var validCode = codeNode is null || (code is not null && code.Value is >= 1000 and <= 4999);
            if (connectionId is not null && ConnectionIdPattern.IsMatch(connectionId) && validCode)
            {
                // TS returns a connection_open literal without the code, even when one arrived.
                return type == "connection_close"
                    ? new HostControlMessage(1, type, connectionId, code)
                    : new HostControlMessage(1, type, connectionId, null);
            }
        }

        throw new InvalidOperationException("Invalid Radius relay control message");
    }

    /// <summary>TS <c>HostInputControlMessage</c>.</summary>
    /// <param name="ConnectionId">Set for connection_open / connection_close.</param>
    /// <param name="Code">Set only when the sender included a code.</param>
    public sealed record HostControlMessage(int Version, string Type, string? ConnectionId, int? Code);

    /// <summary>
    /// Serialize one host→relay control line. TS builds the object literal and lets
    /// <c>JSON.stringify</c> drop <c>undefined</c> values, so a missing code leaves no key behind.
    /// </summary>
    public static string EncodeHostControlMessage(string type, string? connectionId = null, int? code = null)
    {
        var message = new JsonObject { ["version"] = 1, ["type"] = type };
        if (connectionId is not null) message["connection_id"] = connectionId;
        if (code is not null) message["code"] = code;
        return message.ToJsonString(CompactJson);
    }

    /// <summary>TS <c>pong</c> and friends are serialized with compact JSON.</summary>
    public static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// Port of <c>OrderedWebSocketWriter</c>: one write at a time, a cap on queued bytes, and a drain
/// wait while the socket's own buffer is above the threshold.
/// </summary>
public sealed class OrderedWebSocketWriter
{
    private readonly IRadiusRelayWebSocket _socket;
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;
    private long _pendingBytes;
    private bool _closed;

    public OrderedWebSocketWriter(IRadiusRelayWebSocket socket)
    {
        _socket = socket;
    }

    /// <summary>TS <c>send</c>: a control line, queued behind the previous write.</summary>
    public Task SendAsync(string value)
    {
        // TS keeps string sends as text frames; only the byte accounting is UTF-8 based.
        var byteLength = Encoding.UTF8.GetByteCount(value);
        lock (_gate)
        {
            if (_closed)
            {
                return Closed();
            }

            if (_pendingBytes + byteLength > RadiusRelay.MaxPendingBytes)
            {
                return Task.FromException(
                    new InvalidOperationException("Radius relay exceeded its pending byte limit"));
            }

            return Enqueue(value, byteLength);
        }
    }

    /// <summary>TS <c>send</c>: a data frame, queued behind the previous write.</summary>
    public Task SendAsync(byte[] data)
    {
        // TS copies ArrayBuffer inputs (value.slice(0)) so later caller mutation cannot corrupt a queued frame.
        var copied = (byte[])data.Clone();
        var byteLength = copied.Length;
        lock (_gate)
        {
            if (_closed)
            {
                return Closed();
            }

            if (_pendingBytes + byteLength > RadiusRelay.MaxPendingBytes)
            {
                return Task.FromException(
                    new InvalidOperationException("Radius relay exceeded its pending byte limit"));
            }

            return Enqueue(copied, byteLength);
        }
    }

    /// <summary>TS <c>close</c>: later sends are rejected; queued ones still run.</summary>
    public void Close()
    {
        lock (_gate) _closed = true;
    }

    private static Task Closed()
        => Task.FromException(new InvalidOperationException("Radius relay WebSocket is closed"));

    private Task Enqueue(object payload, long byteLength)
    {
        _pendingBytes += byteLength;
        var tracked = _tail.ContinueWith(_ => WriteAsync(payload, byteLength), TaskScheduler.Default).Unwrap();
        _tail = tracked.ContinueWith(_ => { }, TaskScheduler.Default);
        return tracked;
    }

    /// <summary>
    /// TS wraps the send in <c>operation.finally(...)</c>: the queued bytes are always released, and
    /// the rejection survives. A plain <c>ContinueWith</c> would swallow it, hence the try/finally.
    /// </summary>
    private async Task WriteAsync(object payload, long byteLength)
    {
        try
        {
            await SendNowAsync(payload).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Add(ref _pendingBytes, -byteLength);
        }
    }

    private async Task SendNowAsync(object payload)
    {
        if (_closed || _socket.ReadyState != _socket.Open)
        {
            throw new InvalidOperationException("Radius relay WebSocket is closed");
        }

        if (payload is string text)
        {
            _socket.Send(text);
        }
        else
        {
            _socket.Send((byte[])payload);
        }

        while (_socket.BufferedAmount > RadiusRelay.DrainThresholdBytes)
        {
            if (_closed || _socket.ReadyState != _socket.Open)
            {
                throw new InvalidOperationException("Radius relay WebSocket closed during write");
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}

/// <summary>Shared relay plumbing: the opening handshake, close/error helpers, and the delay.</summary>
public static class RadiusRelaySockets
{
    /// <summary>TS <c>openRadiusRelayWebSocket</c>: resolve on open with the expected subprotocol.</summary>
    public static async Task<IRadiusRelayWebSocket> OpenAsync(
        string url,
        string protocol,
        string token,
        RadiusRelayWebSocketFactory factory,
        CancellationToken signal = default)
    {
        signal.ThrowIfCancellationRequested();
        var socket = factory(url, protocol, $"Bearer {token}");
        socket.BinaryType = "arraybuffer";
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = false;

        void Fail(Exception error)
        {
            if (settled) return;
            settled = true;
            Close(socket, 1000, "Radius relay connection failed");
            failure.TrySetException(error);
        }

        void OnOpened()
        {
            if (socket.Protocol != protocol)
            {
                Fail(new InvalidOperationException(
                    $"Radius relay selected unexpected WebSocket protocol {JsonSerializer.Serialize(socket.Protocol)}"));
                return;
            }

            if (settled) return;
            settled = true;
            opened.TrySetResult();
        }

        void OnClosed(int code, string reason)
            => Fail(new InvalidOperationException($"Radius relay closed before connecting ({code})"));

        void OnErrored(RadiusRelayWebSocketError error) => Fail(WebSocketError(error));

        socket.Opened += OnOpened;
        socket.Closed += OnClosed;
        socket.Errored += OnErrored;
        using var registration = signal.Register(
            () => Fail(new InvalidOperationException("Radius relay connection cancelled")));
        try
        {
            var completed = await Task.WhenAny(opened.Task, failure.Task).ConfigureAwait(false);
            if (completed == failure.Task)
            {
                throw await failure.Task.ConfigureAwait(false);
            }

            await opened.Task.ConfigureAwait(false);
            return socket;
        }
        finally
        {
            socket.Opened -= OnOpened;
            socket.Closed -= OnClosed;
            socket.Errored -= OnErrored;
        }
    }

    /// <summary>TS <c>closeWebSocket</c>: best effort, because a handshake may still be pending.</summary>
    public static void Close(IRadiusRelayWebSocket socket, int code, string reason)
    {
        try
        {
            socket.Close(code, reason);
        }
        catch (Exception)
        {
            // A WebSocket can reject close() while its opening handshake is still pending.
        }
    }

    /// <summary>TS <c>webSocketError</c>.</summary>
    public static Exception WebSocketError(RadiusRelayWebSocketError error)
    {
        if (error.Error is { Message: { Length: > 0 } inner } && inner.Trim().Length > 0)
        {
            return error.Error;
        }

        return new InvalidOperationException(
            error.Message?.Trim() is { Length: > 0 } text ? text : "Radius WebSocket connection failed");
    }

    /// <summary>
    /// TS <c>delay</c>: cancellable. The <c>unref</c> flag has no .NET equivalent — a background task
    /// never keeps the process alive — so it is accepted and ignored.
    /// </summary>
    public static Task DelayAsync(int milliseconds, CancellationToken signal, bool unref)
    {
        signal.ThrowIfCancellationRequested();
        return Task.Delay(milliseconds, signal);
    }

    /// <summary>TS <c>errorMessage</c>.</summary>
    public static string ErrorMessage(Exception error) => error.Message;

    /// <summary>TS <c>toError</c>.</summary>
    public static Exception ToError(object? error)
        => error as Exception ?? new InvalidOperationException(error?.ToString() ?? "null");
}
