// ============================================================================
// Coordinator client — port of experimental/coordinator.ts, client half (4g-3c-3)
// ============================================================================
//
// The server-side endpoint of the coordinator's intentionally opaque message router. A connection
// registers itself as "the server" over the coordinator's control socket, learns which peers are
// attached, and exchanges payloads with them; it never interprets them. A second server registering
// replaces this one — the connection is told so and marks itself replaced, and the coordinator drops
// the public connections it proxied.
//
// The message vocabulary is a closed union (TS validates it with a typebox schema and destroys the
// connection on anything else), so <see cref="CoordinatorMessage.TryParse"/> accepts exactly the five
// shapes and nothing more. TS keeps the wire union unexported; only the connection event and the
// options are public API.

using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Experimental;

/// <summary>What a coordinator connection observes. TS <c>CoordinatorConnectionEvent</c>.</summary>
public abstract record CoordinatorConnectionEvent;

public sealed record PeerConnected(string PeerId) : CoordinatorConnectionEvent;

public sealed record PeerDisconnected(string PeerId) : CoordinatorConnectionEvent;

public sealed record ConnectionMessage(string From, JsonNode? Payload) : CoordinatorConnectionEvent;

/// <summary>TS <c>CoordinatorConnectionOptions</c>.</summary>
public sealed record CoordinatorConnectionOptions(
    string ControlPath,
    string Endpoint,
    string? ServerConnectionId = null);

/// <summary>The server-side endpoint of the coordinator's message router. TS <c>CoordinatorConnection</c>.</summary>
public sealed class CoordinatorConnection
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _peerIds = [];
    private readonly HashSet<Action<CoordinatorConnectionEvent>> _listeners = [];
    private readonly string _controlPath;
    private readonly string _endpoint;
    private CoordinatorSocket? _socket;
    private bool _registered;
    private bool _closed;
    private bool _replaced;
    private TaskCompletionSource? _registration;
    private readonly TaskCompletionSource _replacedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CoordinatorConnection(CoordinatorConnectionOptions options)
    {
        ServerConnectionId = options.ServerConnectionId ?? Guid.NewGuid().ToString();
        _controlPath = options.ControlPath;
        _endpoint = options.Endpoint;
    }

    /// <summary>The identity this connection registers under; a generated UUID when none was given.</summary>
    public string ServerConnectionId { get; }

    /// <summary>Resolves once this server has been replaced (or its connection dropped).</summary>
    public Task Replaced => _replacedSignal.Task;

    /// <summary>The peers the coordinator currently reports as attached.</summary>
    /// <remarks>TS exposes the live set; the port hands out a snapshot instead of its own state.</remarks>
    public IReadOnlySet<string> PeerIds
    {
        get { lock (_gate) return _peerIds.ToHashSet(StringComparer.Ordinal); }
    }

    public string ControlPath => _controlPath;

    /// <summary>Whether this server was replaced by a newer registration.</summary>
    public bool WasReplaced
    {
        get { lock (_gate) return _replaced; }
    }

    /// <summary>Subscribe to connection events; the returned action unsubscribes.</summary>
    public Action OnEvent(Action<CoordinatorConnectionEvent> listener)
    {
        lock (_gate) _listeners.Add(listener);
        return () =>
        {
            lock (_gate) _listeners.Remove(listener);
        };
    }

    /// <summary>Connect to the coordinator and register as the server.</summary>
    public async Task ConnectAsync()
    {
        lock (_gate)
        {
            if (_socket is not null)
            {
                throw new InvalidOperationException("Coordinator server is already connected");
            }
        }

        var socket = await CoordinatorSocket.ConnectAsync(_controlPath).ConfigureAwait(false);

        TaskCompletionSource registration;
        lock (_gate)
        {
            if (_closed)
            {
                // 连接在握手中被关闭：丢弃这条迟到连接，不泄漏 socket。
                throw new InvalidOperationException("Coordinator server closed");
            }

            _socket = socket;
            _registration = registration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        socket.Closed += error => Disconnected(error ?? new InvalidOperationException("Coordinator connection closed"));
        socket.StartReading(HandleMessage);
        await socket.WriteLineAsync(new JsonObject
        {
            ["type"] = "register_server",
            ["protocol"] = Coordinator.ProtocolVersion,
            ["serverConnectionId"] = ServerConnectionId,
            ["endpoint"] = _endpoint,
        }).ConfigureAwait(false);
        await registration.Task.ConfigureAwait(false);
    }

    /// <summary>Send a payload to one peer.</summary>
    public async Task SendAsync(string peerId, JsonNode? payload)
    {
        await WriteAsync(new JsonObject
        {
            ["type"] = "send",
            ["to"] = peerId,
            ["payload"] = payload,
        }).ConfigureAwait(false);
    }

    /// <summary>Send a payload to every attached peer.</summary>
    public async Task BroadcastAsync(JsonNode? payload)
    {
        await WriteAsync(new JsonObject
        {
            ["type"] = "broadcast",
            ["payload"] = payload,
        }).ConfigureAwait(false);
    }

    /// <summary>Close the connection. A registration still in flight is rejected.</summary>
    public void Close()
    {
        CoordinatorSocket? socket;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            socket = _socket;
            _socket = null;
            _peerIds.Clear();
            RejectRegistration(new InvalidOperationException("Coordinator server closed"));
        }

        socket?.Destroy();
    }

    private async Task WriteAsync(JsonNode message)
    {
        CoordinatorSocket socket;
        lock (_gate)
        {
            if (!_registered || _socket is null || _closed)
            {
                throw new InvalidOperationException("Coordinator server is not connected");
            }

            socket = _socket;
        }

        await socket.WriteLineAsync(message).ConfigureAwait(false);
    }

    private void HandleMessage(JsonNode? value)
    {
        switch (CoordinatorMessage.TryParse(value))
        {
            case null:
                // TS destroys with this error rather than surfacing it to the caller.
                DestroyCurrent(new InvalidOperationException("Coordinator sent an invalid message"));
                return;

            case CoordinatorMessage.ServerRegistered registered:
                if (registered.ServerConnectionId != ServerConnectionId)
                {
                    DestroyCurrent(new InvalidOperationException("Coordinator returned an invalid server registration"));
                    return;
                }

                lock (_gate)
                {
                    foreach (var peerId in registered.Peers) _peerIds.Add(peerId);
                    _registered = true;
                    SettleRegistration();
                }

                return;

            case CoordinatorMessage.ServerReplaced:
                MarkReplaced();
                return;

            case CoordinatorMessage.Routed routed:
                switch (routed.Event)
                {
                    case PeerConnected connected:
                        lock (_gate) _peerIds.Add(connected.PeerId);
                        Emit(connected);
                        return;

                    case PeerDisconnected disconnected:
                        lock (_gate) _peerIds.Remove(disconnected.PeerId);
                        Emit(disconnected);
                        return;

                    default:
                        Emit(routed.Event);
                        return;
                }

            default:
                DestroyCurrent(new InvalidOperationException("Coordinator sent an unsupported message"));
                return;
        }
    }

    private void Emit(CoordinatorConnectionEvent connectionEvent)
    {
        foreach (var listener in SnapshotListeners())
        {
            // TS iterates the live set: a listener removed while emitting is skipped.
            if (IsListening(listener)) listener(connectionEvent);
        }
    }

    private Action<CoordinatorConnectionEvent>[] SnapshotListeners()
    {
        lock (_gate) return [.. _listeners];
    }

    private bool IsListening(Action<CoordinatorConnectionEvent> listener)
    {
        lock (_gate) return _listeners.Contains(listener);
    }

    private void Disconnected(Exception error)
    {
        lock (_gate)
        {
            _socket = null;
            if (_closed) return;
            RejectRegistration(error);
        }

        MarkReplaced();
    }

    private void MarkReplaced()
    {
        lock (_gate)
        {
            if (_replaced) return;
            _replaced = true;
        }

        _replacedSignal.TrySetResult();
    }

    private void DestroyCurrent(Exception error)
    {
        CoordinatorSocket? socket;
        lock (_gate) socket = _socket;
        socket?.Destroy(error);
    }

    /// <summary>Settle the outstanding registration, if any. Caller holds the gate.</summary>
    private void SettleRegistration()
    {
        var pending = _registration;
        _registration = null;
        pending?.TrySetResult();
    }

    /// <summary>Reject the outstanding registration, if any. Caller holds the gate.</summary>
    private void RejectRegistration(Exception error)
    {
        var pending = _registration;
        _registration = null;
        pending?.TrySetException(error);
    }
}

/// <summary>
/// The message vocabulary the coordinator sends a server. TS keeps this union unexported
/// (<c>type CoordinatorMessage = Static&lt;…&gt;</c>), so the port keeps it internal too.
/// </summary>
internal abstract record CoordinatorMessage
{
    public sealed record ServerRegistered(string ServerConnectionId, IReadOnlyList<string> Peers) : CoordinatorMessage;

    public sealed record ServerReplaced : CoordinatorMessage;

    public sealed record Routed(CoordinatorConnectionEvent Event) : CoordinatorMessage;

    /// <summary>
    /// Parse one wire message, or <see langword="null"/> when it is not one of the five shapes.
    /// Typebox's <c>Check</c> allows unknown extra properties, and so does this.
    /// </summary>
    public static CoordinatorMessage? TryParse(JsonNode? value)
    {
        if (value is not JsonObject message
            || !IsStringValue(message["type"]))
        {
            return null;
        }

        var type = message["type"]!.GetValue<string>();
        switch (type)
        {
            case "server_registered":
                if (!IsNonEmptyString(message["serverConnectionId"])
                    || message["peers"] is not JsonArray peers
                    || peers.Any(peer => !IsStringValue(peer)))
                {
                    return null;
                }

                return new ServerRegistered(
                    message["serverConnectionId"]!.GetValue<string>(),
                    [.. peers.Select(peer => peer!.GetValue<string>())]);

            case "server_replaced":
                return new ServerReplaced();

            case "peer_connected":
            case "peer_disconnected":
                if (!IsNonEmptyString(message["peerId"]))
                {
                    return null;
                }

                var peer = message["peerId"]!.GetValue<string>();
                return new Routed(type == "peer_connected"
                    ? new PeerConnected(peer)
                    : new PeerDisconnected(peer));

            case "message":
                if (!message.ContainsKey("payload")
                    || !IsNonEmptyString(message["from"]))
                {
                    return null;
                }

                return new Routed(new ConnectionMessage(message["from"]!.GetValue<string>(), message["payload"]));

            default:
                return null;
        }
    }

    private static bool IsStringValue(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String;

    private static bool IsNonEmptyString(JsonNode? node)
        => IsStringValue(node) && node!.GetValue<string>() is { Length: > 0 };
}

/// <summary>TS <c>CoordinatorStartupLease</c>: owns the socket the startup probe ended on.</summary>
public sealed class CoordinatorStartupLease(Action close)
{
    /// <summary>Release the lease. TS closes the socket it probed or spawned through.</summary>
    public void Close() => close();
}

/// <summary>Options of <see cref="Coordinator.EnsureCoordinatorAsync"/>. The two seams exist for tests.</summary>
public sealed record EnsureCoordinatorOptions
{
    /// <summary>Spawn the coordinator process; defaults to <see cref="InternalProcess.SpawnInternalProcess"/>.</summary>
    public Func<ChildProcessHandle>? Spawn { get; init; }

    /// <summary>Probe the control socket; defaults to a real Unix domain connect.</summary>
    public Func<string, Task<CoordinatorSocket?>>? Connect { get; init; }

    /// <summary>How long to wait for a spawned coordinator to listen. TS 10s.</summary>
    public int StartTimeoutMs { get; init; } = 10_000;

    /// <summary>How long to wait between probes. TS 10ms.</summary>
    public int RetryMs { get; init; } = 10;
}

/// <summary>Port of <c>ensureCoordinator</c> and the coordinator entrypoint.</summary>
public static class Coordinator
{
    /// <summary>TS <c>COORDINATOR_PROTOCOL_VERSION</c>.</summary>
    public const int ProtocolVersion = 3;

    /// <summary>
    /// Make sure a coordinator is listening on <paramref name="controlPath"/>, spawning one when it is
    /// not. The lease owns the probe socket; the coordinator itself keeps running until it empties.
    /// </summary>
    public static async Task<CoordinatorStartupLease> EnsureCoordinatorAsync(
        string publicPath, string controlPath, EnsureCoordinatorOptions? options = null)
    {
        options ??= new EnsureCoordinatorOptions();
        var connect = options.Connect ?? (path => TryConnectAsync(path));
        var spawn = options.Spawn ?? (() => InternalProcess.SpawnInternalProcess(
            InternalProcessRole.Coordinator, [publicPath, controlPath]));

        var existing = await connect(controlPath).ConfigureAwait(false);
        if (existing is not null)
        {
            return new CoordinatorStartupLease(() => existing.Dispose());
        }

        var child = spawn();
        var deadline = Environment.TickCount64 + options.StartTimeoutMs;
        while (true)
        {
            var socket = await connect(controlPath).ConfigureAwait(false);
            if (socket is not null)
            {
                return new CoordinatorStartupLease(() => socket.Dispose());
            }

            if (child.ExitCode is not null || child.SignalCode is not null)
            {
                throw new InvalidOperationException("Coordinator exited during startup");
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new InvalidOperationException("Timed out waiting for coordinator startup");
            }

            await Task.Delay(options.RetryMs).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// TS <c>coordinator-entry.ts</c>: consume the internal role, require it to be the coordinator,
    /// and run the process. The CLI dispatches here for a coordinator invocation.
    /// </summary>
    public static Task RunCoordinatorEntryAsync(IReadOnlyList<string> args, CoordinatorProcessOptions? options = null)
    {
        var role = InternalProcess.ConsumeInternalProcessRole();
        if (role != InternalProcessRole.Coordinator)
        {
            throw new InvalidOperationException("Coordinator entrypoint requires an internal coordinator invocation");
        }

        return CoordinatorProcess.RunCoordinatorAsync(args, options);
    }

    /// <summary>
    /// TS <c>tryConnect</c>: a socket when something is listening, null for ENOENT / ECONNREFUSED,
    /// and the error itself otherwise.
    /// </summary>
    internal static async Task<CoordinatorSocket?> TryConnectAsync(string path)
    {
        try
        {
            return await CoordinatorSocket.ConnectAsync(path).ConfigureAwait(false);
        }
        catch (SocketException error) when (IsAbsent(error))
        {
            return null;
        }
    }

    /// <summary>
    /// Windows reports a missing or stale Unix socket path as ECONNREFUSED; Linux reports ENOENT for a
    /// missing path, which .NET surfaces through the native error code.
    /// </summary>
    private static bool IsAbsent(SocketException error)
        => error.SocketErrorCode == SocketError.ConnectionRefused || error.ErrorCode == 2;
}
