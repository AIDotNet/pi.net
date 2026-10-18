// ============================================================================
// Coordinator process — port of experimental/coordinator.ts, process half (4g-3c-3)
// ============================================================================
//
// This process is intentionally a transport shim. It depends only on sockets and never interprets
// Pi, session, worker, or lifecycle payloads: it owns two Unix domain sockets — a control socket
// where exactly one "server" and any number of "peers" register, and a public socket it proxies to
// the current server's endpoint — and it routes opaque JSON lines between them.
//
// Lifecycle: a fresh coordinator schedules an empty shutdown 30s after startup; every registration
// or proxied connection cancels it; when nothing is left it shuts down 250ms later, closes both
// listeners, removes both socket files, and exits 0. A second server registering replaces the first:
// the proxied public connections are dropped and the old server is told it was replaced.
//
// TS keeps all of this in module-level state behind functions; the port makes it an instance so each
// test can run its own coordinator on its own socket paths.

using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Pi.CodingAgent.Experimental;

/// <summary>Options of <see cref="CoordinatorProcess"/>. The exit seam exists for tests.</summary>
public sealed record CoordinatorProcessOptions
{
    /// <summary>Called with 0 once the coordinator has shut down. Defaults to <see cref="Environment.Exit(int)"/>.</summary>
    public Action<int>? Exit { get; init; }
}

/// <summary>Port of <c>runCoordinatorProcess</c> and the router it drives.</summary>
public sealed class CoordinatorProcess
{
    private const int EmptyStartupGraceMs = 30_000;
    private const int EmptyShutdownGraceMs = 250;
    private const int SocketProbeTimeoutMs = 1_000;
    private const int ReceiveBufferSize = 16 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, RoutedPeer> _peers = new(StringComparer.Ordinal);
    private readonly HashSet<CoordinatorSocket> _controlConnections = [];
    private readonly Dictionary<CoordinatorSocket, Socket> _publicConnections = new();
    private readonly Action<int> _exit;
    private ServerPeer? _currentServer;
    private bool _shuttingDown;
    private bool _running;
    private CancellationTokenSource? _emptyShutdown;
    private Socket? _controlServer;
    private Socket? _publicServer;
    private string _publicPath = "";
    private string _controlPath = "";
    private PosixSignalRegistration? _sigterm;

    /// <summary>The public socket path this process bound; empty until <see cref="RunAsync"/>.</summary>
    public string PublicPath
    {
        get { lock (_gate) return _publicPath; }
    }

    /// <summary>The control socket path this process bound; empty until <see cref="RunAsync"/>.</summary>
    public string ControlPath
    {
        get { lock (_gate) return _controlPath; }
    }

    public CoordinatorProcess(CoordinatorProcessOptions? options = null)
    {
        _exit = options?.Exit ?? (code => Environment.Exit(code));
    }

    /// <summary>TS <c>runCoordinatorProcess</c>.</summary>
    public static Task RunCoordinatorAsync(IReadOnlyList<string> args, CoordinatorProcessOptions? options = null)
        => new CoordinatorProcess(options).RunAsync(args);

    /// <summary>Bind both sockets and serve until the coordinator empties or is signalled.</summary>
    public async Task RunAsync(IReadOnlyList<string> args)
    {
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException("Coordinator process is already running");
            }
        }

        var publicPath = args.Count > 0 ? args[0] : null;
        var controlPath = args.Count > 1 ? args[1] : null;
        if (string.IsNullOrEmpty(publicPath) || string.IsNullOrEmpty(controlPath))
        {
            throw new InvalidOperationException("Coordinator requires public and control socket paths");
        }

        lock (_gate)
        {
            _publicPath = publicPath;
            _controlPath = controlPath;
        }

        _running = true;
        RegisterSignals();
        await MainAsync().ConfigureAwait(false);
    }

    /// <summary>TS <c>shutdownCoordinator</c>: idempotent, and exits the process when done.</summary>
    public async Task ShutdownAsync()
    {
        lock (_gate)
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
        }

        UnregisterSignals();
        CancelEmptyShutdown();
        ClosePublicConnections();
        foreach (var connection in ControlConnections())
        {
            connection.Destroy();
        }

        await CloseServerAsync(_publicServer).ConfigureAwait(false);
        await CloseServerAsync(_controlServer).ConfigureAwait(false);
        await CleanupSocketAsync(_publicPath).ConfigureAwait(false);
        await CleanupSocketAsync(_controlPath).ConfigureAwait(false);
        _exit(0);
    }

    // ---------------------------------------------------------------- lifecycle

    private async Task MainAsync()
    {
        await RemoveStaleSocketAsync(_controlPath).ConfigureAwait(false);
        await RemoveStaleSocketAsync(_publicPath).ConfigureAwait(false);
        try
        {
            _controlServer = await ListenAsync(_controlPath, control: true).ConfigureAwait(false);
            await RestrictSocketAsync(_controlPath).ConfigureAwait(false);
            _publicServer = await ListenAsync(_publicPath, control: false).ConfigureAwait(false);
            await RestrictSocketAsync(_publicPath).ConfigureAwait(false);
        }
        catch
        {
            await Task.WhenAll(CleanupSocketAsync(_controlPath), CleanupSocketAsync(_publicPath))
                .ConfigureAwait(false);
            throw;
        }

        ScheduleEmptyShutdown(EmptyStartupGraceMs);
    }

    private async Task<Socket> ListenAsync(string path, bool control)
    {
        var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            server.Bind(new UnixDomainSocketEndPoint(path));
            server.Listen(backlog: 128);
        }
        catch
        {
            server.Dispose();
            throw;
        }

        _ = Task.Run(() => AcceptLoopAsync(server, control), CancellationToken.None);
        await Task.CompletedTask.ConfigureAwait(false);
        return server;
    }

    private async Task AcceptLoopAsync(Socket server, bool control)
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await server.AcceptAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is ObjectDisposedException or SocketException)
            {
                // 监听器已关闭（或平台报告了 accept 错误）：停止接收。
                return;
            }

            var connection = CoordinatorSocket.Accept(socket);
            if (control)
            {
                AcceptControlConnection(connection);
            }
            else
            {
                AcceptPublicConnection(connection);
            }
        }
    }

    private static async Task CloseServerAsync(Socket? server)
    {
        if (server is null) return;
        try
        {
            server.Dispose();
        }
        catch (Exception)
        {
            // 关闭失败不掩盖其余清理。
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async Task RestrictSocketAsync(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception error) when (error is PlatformNotSupportedException or FileNotFoundException or DirectoryNotFoundException)
        {
            // 平台不支持或文件已消失：TS 的 chmod 失败同样不会中止启动。
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task RemoveStaleSocketAsync(string path)
    {
        if (!File.Exists(path)) return;
        if (IsSocketLive(path))
        {
            throw new InvalidOperationException($"Coordinator socket is already active: {path}");
        }

        await CleanupSocketAsync(path).ConfigureAwait(false);
    }

    private static async Task CleanupSocketAsync(string path)
    {
        // TS skips Windows entirely: there is no socket file to unlink there.
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // 已不存在。
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static bool IsSocketLive(string path)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var connect = probe.ConnectAsync(new UnixDomainSocketEndPoint(path));
            return connect.Wait(TimeSpan.FromMilliseconds(SocketProbeTimeoutMs));
        }
        catch (Exception error) when (error is SocketException or AggregateException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void RegisterSignals()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                _ = ShutdownAsync();
            });
        }
        catch (Exception error) when (error is PlatformNotSupportedException or IOException or ArgumentException)
        {
            // 平台不提供该信号（如 Windows 的 SIGTERM）：仅靠 Ctrl+C 关闭。
            _sigterm = null;
        }
    }

    private void UnregisterSignals()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        try
        {
            _sigterm?.Dispose();
        }
        catch (Exception error) when (error is ObjectDisposedException or IOException)
        {
            // 已释放。
        }

        _sigterm = null;
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // TS 的信号处理器接管了默认行为，进程不再被信号直接终止。
        e.Cancel = true;
        _ = ShutdownAsync();
    }

    // ---------------------------------------------------------------- control socket

    private void AcceptControlConnection(CoordinatorSocket socket)
    {
        var destroy = false;
        lock (_gate)
        {
            if (_shuttingDown)
            {
                destroy = true;
            }
            else
            {
                _controlConnections.Add(socket);
            }
        }

        if (destroy)
        {
            socket.Dispose();
            return;
        }

        var connection = new ControlConnection { Socket = socket };
        socket.Closed += _ => Disconnect(connection);
        socket.StartReading(message => HandleControlMessage(connection, message));
    }

    private void HandleControlMessage(ControlConnection connection, JsonNode? raw)
    {
        var message = ControlMessage.TryParse(raw)
            ?? throw new InvalidOperationException("Coordinator message must have a type");
        if (connection.Server is null && connection.Peer is null)
        {
            if (message.Type == "register_server")
            {
                connection.Server = RegisterServer(connection.Socket, message);
                return;
            }

            if (message.Type == "register_peer")
            {
                connection.Peer = RegisterPeer(connection.Socket, message);
                return;
            }

            throw new InvalidOperationException("Coordinator connection did not register a role");
        }

        if (connection.Server is not null)
        {
            // 已被替换的 server 的消息一律丢弃。
            lock (_gate)
            {
                if (ReferenceEquals(connection.Server, _currentServer))
                {
                    HandleRoutedMessage("server", message);
                }
            }

            return;
        }

        HandleRoutedMessage(connection.Peer!.PeerId, message);
    }

    private ServerPeer RegisterServer(CoordinatorSocket socket, ControlMessage message)
    {
        if (!IsProtocolVersion(message.Protocol))
        {
            throw new InvalidOperationException("Unsupported coordinator protocol");
        }

        var serverConnectionId = NonEmptyString(message.ServerConnectionId)
            ?? throw new InvalidOperationException("Coordinator serverConnectionId must be a string");
        var endpoint = NonEmptyString(message.Endpoint)
            ?? throw new InvalidOperationException("Coordinator endpoint must be a string");
        var server = new ServerPeer(serverConnectionId, endpoint, socket);

        ServerPeer? previous;
        lock (_gate)
        {
            previous = _currentServer;
            _currentServer = server;
        }

        CancelEmptyShutdown();
        socket.WriteLine(new JsonObject
        {
            ["type"] = "server_registered",
            ["serverConnectionId"] = serverConnectionId,
            ["peers"] = new JsonArray([.. PeerIds()]),
        });
        if (previous is not null && !ReferenceEquals(previous, server))
        {
            ClosePublicConnections();
            NotifyPeers(new JsonObject
            {
                ["type"] = "server_disconnected",
                ["serverConnectionId"] = previous.ServerConnectionId,
            });
            previous.Socket.WriteLine(new JsonObject { ["type"] = "server_replaced" });
        }

        NotifyPeers(new JsonObject
        {
            ["type"] = "server_connected",
            ["serverConnectionId"] = serverConnectionId,
        });
        return server;
    }

    private RoutedPeer RegisterPeer(CoordinatorSocket socket, ControlMessage message)
    {
        if (!IsProtocolVersion(message.Protocol))
        {
            throw new InvalidOperationException("Unsupported coordinator protocol");
        }

        var peerId = NonEmptyString(message.PeerId)
            ?? throw new InvalidOperationException("Coordinator peerId must be a string");
        if (peerId == "server" || PeerExists(peerId))
        {
            throw new InvalidOperationException($"Coordinator peer is already connected: {peerId}");
        }

        var peer = new RoutedPeer(peerId, socket);
        lock (_gate) _peers[peerId] = peer;
        CancelEmptyShutdown();
        var registration = new JsonObject
        {
            ["type"] = "peer_registered",
            ["peerId"] = peerId,
        };
        var server = CurrentServer();
        if (server is not null)
        {
            registration["serverConnectionId"] = server.ServerConnectionId;
        }

        socket.WriteLine(registration);
        if (server is not null)
        {
            server.Socket.WriteLine(new JsonObject { ["type"] = "peer_connected", ["peerId"] = peerId });
        }

        return peer;
    }

    private void Disconnect(ControlConnection connection)
    {
        lock (_gate)
        {
            _controlConnections.Remove(connection.Socket);
            if (connection.Server is not null && ReferenceEquals(connection.Server, _currentServer))
            {
                _currentServer = null;
                NotifyPeersUnsafe(new JsonObject
                {
                    ["type"] = "server_disconnected",
                    ["serverConnectionId"] = connection.Server.ServerConnectionId,
                });
            }

            if (connection.Peer is not null
                && _peers.TryGetValue(connection.Peer.PeerId, out var current)
                && ReferenceEquals(current, connection.Peer))
            {
                _peers.Remove(connection.Peer.PeerId);
                _currentServer?.Socket.WriteLine(new JsonObject
                {
                    ["type"] = "peer_disconnected",
                    ["peerId"] = connection.Peer.PeerId,
                });
            }
        }

        CheckEmpty();
    }

    private void HandleRoutedMessage(string from, ControlMessage message)
    {
        if (message.Type == "send")
        {
            var to = NonEmptyString(message.To)
                ?? throw new InvalidOperationException("Coordinator message target must be a string");
            CoordinatorSocket? target;
            lock (_gate)
            {
                target = to == "server"
                    ? _currentServer?.Socket
                    : _peers.TryGetValue(to, out var peer) ? peer.Socket : null;
            }

            target?.WriteLine(RoutedPayload(from, message));
            return;
        }

        if (message.Type == "broadcast")
        {
            if (from != "server")
            {
                throw new InvalidOperationException("Only the current server may broadcast");
            }

            foreach (var peer in Peers())
            {
                peer.Socket.WriteLine(RoutedPayload(from, message));
            }

            return;
        }

        throw new InvalidOperationException($"Unknown coordinator routing message: {message.Type}");
    }

    /// <summary>
    /// The routed payload keeps TS's key shape: the payload key is written only when the sender
    /// actually sent one (<c>JSON.stringify</c> drops <c>undefined</c> values), and the payload is
    /// cloned because a <see cref="JsonNode"/> cannot be re-parented into another object.
    /// </summary>
    private static JsonObject RoutedPayload(string from, ControlMessage message)
    {
        var routed = new JsonObject
        {
            ["type"] = "message",
            ["from"] = from,
        };
        if (message.HasPayload)
        {
            routed["payload"] = message.Payload?.DeepClone();
        }

        return routed;
    }

    private void NotifyPeers(JsonObject message)
    {
        foreach (var peer in Peers())
        {
            peer.Socket.WriteLine(message);
        }
    }

    private void NotifyPeersUnsafe(JsonObject message)
    {
        foreach (var peer in _peers.Values)
        {
            peer.Socket.WriteLine(message);
        }
    }

    // ---------------------------------------------------------------- public socket

    private void AcceptPublicConnection(CoordinatorSocket client)
    {
        Socket? upstream = null;
        lock (_gate)
        {
            var serverEndpoint = _currentServer?.Endpoint;
            if (!_shuttingDown && serverEndpoint is not null)
            {
                // 先登记这一对，再连接上游（TS 同序）。
                upstream = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _publicConnections[client] = upstream;
            }
        }

        if (upstream is null)
        {
            client.Dispose();
            return;
        }

        var server = upstream;
        var endpoint = CurrentEndpoint()!;
        CancelEmptyShutdown();
        _ = Task.Run(async () =>
        {
            try
            {
                await server.ConnectAsync(new UnixDomainSocketEndPoint(endpoint)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 上游连不上：TS 的 upstream error 处理器销毁客户端。
                try
                {
                    server.Dispose();
                }
                catch (Exception)
                {
                    // 已销毁。
                }

                client.Destroy();
                FinalizePublic(client);
                return;
            }

            StartPublicPair(client, server);
        }, CancellationToken.None);
    }

    /// <summary>
    /// Pipe both directions and tear the pair down once both pumps have ended. TS wires four handlers
    /// ('connect' pipes, error/close on both ends) whose net effect is this; the explicit pair
    /// bookkeeping keeps .NET from closing a socket that still has a pending read, which would reset
    /// the connection instead of ending it gracefully.
    /// </summary>
    private void StartPublicPair(CoordinatorSocket client, Socket upstream)
    {
        var reads = new CancellationTokenSource();
        var remaining = 2;
        void PumpEnded()
        {
            if (Interlocked.Decrement(ref remaining) > 0) return;
            try
            {
                // 先发 FIN 再释放：对端看到流结束，而不是连接被重置。
                upstream.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
                // 已关闭。
            }

            try
            {
                upstream.Dispose();
            }
            catch (Exception)
            {
                // 已销毁。
            }

            client.Destroy();
            FinalizePublic(client);
        }

        _ = PumpAsync(upstream, client, reads.Token, onEnded: () =>
        {
            // 上游先结束：拆掉客户端，另一个泵随取消收尾。
            client.Destroy();
            PumpEnded();
        });
        _ = PumpAsync(client, upstream, reads.Token, onEnded: () =>
        {
            // 客户端先结束：取消上游侧的挂起读取。
            try
            {
                reads.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已取消。
            }

            PumpEnded();
        });
    }

    private static async Task PumpAsync(Socket from, CoordinatorSocket to, CancellationToken cancellationToken,
        Action onEnded)
    {
        var buffer = new byte[ReceiveBufferSize];
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await to.WriteRawAsync(buffer[..read]).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // 对端已销毁或本对已取消。
        }

        onEnded();
    }

    private static async Task PumpAsync(CoordinatorSocket from, Socket to, CancellationToken cancellationToken,
        Action onEnded)
    {
        var buffer = new byte[ReceiveBufferSize];
        try
        {
            while (true)
            {
                var read = await from.ReceiveRawAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await to.SendAsync(buffer[..read], SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // 对端已销毁或本对已取消。
        }

        onEnded();
    }

    private void FinalizePublic(CoordinatorSocket client)
    {
        lock (_gate) _publicConnections.Remove(client);
        CheckEmpty();
    }

    private void ClosePublicConnections()
    {
        List<KeyValuePair<CoordinatorSocket, Socket>> pairs;
        lock (_gate)
        {
            pairs = [.. _publicConnections];
            _publicConnections.Clear();
        }

        foreach (var (client, upstream) in pairs)
        {
            client.Destroy();
            try
            {
                upstream.Dispose();
            }
            catch (Exception)
            {
                // 已销毁。
            }
        }
    }

    // ---------------------------------------------------------------- empty shutdown

    private void CheckEmpty()
    {
        lock (_gate)
        {
            if (_shuttingDown
                || _currentServer is not null
                || _peers.Count > 0
                || _publicConnections.Count > 0
                || _controlConnections.Count > 0)
            {
                CancelEmptyShutdown();
                return;
            }
        }

        ScheduleEmptyShutdown(EmptyShutdownGraceMs);
    }

    private void ScheduleEmptyShutdown(int delayMs)
    {
        CancellationTokenSource timer;
        lock (_gate)
        {
            if (_emptyShutdown is not null || _shuttingDown) return;
            timer = _emptyShutdown = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, timer.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                timer.Dispose();
            }

            lock (_gate)
            {
                // 到点即释放槽位：之后再有 checkEmpty 可以重新排程。
                if (ReferenceEquals(_emptyShutdown, timer)) _emptyShutdown = null;
            }

            if (IsEmpty())
            {
                await ShutdownAsync().ConfigureAwait(false);
            }
        }, CancellationToken.None);
    }

    private void CancelEmptyShutdown()
    {
        CancellationTokenSource? timer;
        lock (_gate)
        {
            timer = _emptyShutdown;
            _emptyShutdown = null;
        }

        if (timer is null) return;
        try
        {
            timer.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放。
        }
    }

    private bool IsEmpty()
    {
        lock (_gate)
        {
            return _currentServer is null
                && _peers.Count == 0
                && _publicConnections.Count == 0
                && _controlConnections.Count == 0;
        }
    }

    // ---------------------------------------------------------------- state helpers

    private List<string> PeerIds()
    {
        lock (_gate) return [.. _peers.Keys];
    }

    private bool PeerExists(string peerId)
    {
        lock (_gate) return _peers.ContainsKey(peerId);
    }

    private List<RoutedPeer> Peers()
    {
        lock (_gate) return [.. _peers.Values];
    }

    private ServerPeer? CurrentServer()
    {
        lock (_gate) return _currentServer;
    }

    private string? CurrentEndpoint()
    {
        lock (_gate) return _currentServer?.Endpoint;
    }

    private List<CoordinatorSocket> ControlConnections()
    {
        lock (_gate) return [.. _controlConnections];
    }

    private static bool IsProtocolVersion(JsonNode? value)
        => value is JsonValue number
            && number.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && number.GetValue<double>() == Coordinator.ProtocolVersion;

    private static string? NonEmptyString(JsonNode? value)
        => value is JsonValue text
            && text.GetValueKind() == System.Text.Json.JsonValueKind.String
            && text.GetValue<string>() is { Length: > 0 } content
                ? content
                : null;

    private sealed record ServerPeer(string ServerConnectionId, string Endpoint, CoordinatorSocket Socket);

    private sealed record RoutedPeer(string PeerId, CoordinatorSocket Socket);

    private sealed class ControlConnection
    {
        public required CoordinatorSocket Socket { get; init; }

        public ServerPeer? Server { get; set; }

        public RoutedPeer? Peer { get; set; }
    }

    /// <summary>One control line, parsed. TS reads the same loose shape off <c>unknown</c>.</summary>
    private sealed record ControlMessage(string Type, JsonObject Raw)
    {
        public JsonNode? Protocol => Raw["protocol"];

        public JsonNode? ServerConnectionId => Raw["serverConnectionId"];

        public JsonNode? Endpoint => Raw["endpoint"];

        public JsonNode? PeerId => Raw["peerId"];

        public JsonNode? To => Raw["to"];

        public JsonNode? Payload => Raw["payload"];

        /// <summary>Whether the sender included a payload key at all.</summary>
        public bool HasPayload => Raw.ContainsKey("payload");

        public static ControlMessage? TryParse(JsonNode? node)
        {
            if (node is not JsonObject obj
                || obj["type"] is not JsonValue type
                || type.GetValueKind() != System.Text.Json.JsonValueKind.String)
            {
                return null;
            }

            return new ControlMessage(type.GetValue<string>(), obj);
        }
    }
}
