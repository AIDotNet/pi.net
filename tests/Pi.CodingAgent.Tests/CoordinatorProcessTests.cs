using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Experimental;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the coordinator process (4g-3c-3). The coordinator is a transport shim, so these are
/// integration tests over real Unix domain sockets: registration, routing, replacement, the public
/// proxy, and the empty shutdown.
/// </summary>
public class CoordinatorProcessTests
{
    private static string TempPath(string name)
        => Path.Combine(Path.GetTempPath(), $"pi-coordproc-{Guid.NewGuid():N}-{name}");

    private static (CoordinatorProcess Process, string PublicPath, string ControlPath, List<int> Exits) Start()
    {
        var exits = new List<int>();
        var process = new CoordinatorProcess(new CoordinatorProcessOptions { Exit = code => exits.Add(code) });
        return (process, TempPath("public"), TempPath("control"), exits);
    }

    private static async Task<CoordinatorProcess> RunAsync()
    {
        var (process, publicPath, controlPath, _) = Start();
        await process.RunAsync([publicPath, controlPath]);
        return process;
    }

    private static JsonObject RegisterServer(string serverConnectionId, string endpoint) => new()
    {
        ["type"] = "register_server",
        ["protocol"] = 3,
        ["serverConnectionId"] = serverConnectionId,
        ["endpoint"] = endpoint,
    };

    private static JsonObject RegisterPeer(string peerId) => new()
    {
        ["type"] = "register_peer",
        ["protocol"] = 3,
        ["peerId"] = peerId,
    };

    // ---------------------------------------------------------------- registration

    [Fact]
    public async Task RegisterServer_AnswersTheRegistration()
    {
        var process = await RunAsync();
        try
        {
            var server = await WireChannel.ConnectAsync(ControlPathOf(process));
            await server.SendAsync(RegisterServer("srv-1", "/endpoint"));

            var reply = await server.NextAsync("server_registered");
            Assert.Equal("server_registered", reply["type"]!.GetValue<string>());
            Assert.Equal("srv-1", reply["serverConnectionId"]!.GetValue<string>());
            Assert.Empty(reply["peers"]!.AsArray());
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task RegisterPeer_AnnouncesBothSides()
    {
        var process = await RunAsync();
        try
        {
            var server = await WireChannel.ConnectAsync(ControlPathOf(process));
            await server.SendAsync(RegisterServer("srv-1", "/endpoint"));
            await server.NextAsync("server_registered");

            var peer = await WireChannel.ConnectAsync(ControlPathOf(process));
            await peer.SendAsync(RegisterPeer("peer-1"));

            var registration = await peer.NextAsync("peer_registered");
            Assert.Equal("peer-1", registration["peerId"]!.GetValue<string>());
            Assert.Equal("srv-1", registration["serverConnectionId"]!.GetValue<string>());

            var announced = await server.NextAsync("peer_connected");
            Assert.Equal("peer_connected", announced["type"]!.GetValue<string>());
            Assert.Equal("peer-1", announced["peerId"]!.GetValue<string>());

            // A second server registration hands the peer the new identity.
            var replacement = await WireChannel.ConnectAsync(ControlPathOf(process));
            await replacement.SendAsync(RegisterServer("srv-2", "/endpoint"));
            var registered = await replacement.NextAsync("server_registered");
            Assert.Equal("srv-2", registered["serverConnectionId"]!.GetValue<string>());
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task UnregisteredConnection_IsDestroyed()
    {
        var process = await RunAsync();
        try
        {
            var wire = await WireChannel.ConnectAsync(ControlPathOf(process));
            await wire.SendAsync(new JsonObject { ["type"] = "send", ["to"] = "server", ["payload"] = 1 });

            // 未注册角色就被销毁：错误原因只存在于协调器本地（TS 的 destroy(error) 同理），
            // 线上能观察到的就是连接关闭。
            await wire.ClosedAsync();
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task PeerIdConflicts_AreRejected()
    {
        var process = await RunAsync();
        try
        {
            var first = await WireChannel.ConnectAsync(ControlPathOf(process));
            await first.SendAsync(RegisterPeer("peer-1"));
            await first.NextAsync("peer_registered");

            var duplicate = await WireChannel.ConnectAsync(ControlPathOf(process));
            await duplicate.SendAsync(RegisterPeer("peer-1"));
            await duplicate.ClosedAsync();

            var reserved = await WireChannel.ConnectAsync(ControlPathOf(process));
            await reserved.SendAsync(RegisterPeer("server"));
            await reserved.ClosedAsync();

            // 原 peer 不受影响：消息照常路由。
            var server = await WireChannel.ConnectAsync(ControlPathOf(process));
            await server.SendAsync(RegisterServer("srv-1", "/endpoint"));
            await server.NextAsync("server_registered");
            await first.SendAsync(new JsonObject { ["type"] = "send", ["to"] = "server", ["payload"] = "alive" });
            var atServer = await server.NextAsync("message");
            Assert.Equal("alive", atServer["payload"]!.GetValue<string>());
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task RegistrationValidation_RejectsBadFields()
    {
        var process = await RunAsync();
        try
        {
            var wrongProtocol = await WireChannel.ConnectAsync(ControlPathOf(process));
            await wrongProtocol.SendAsync(new JsonObject
            {
                ["type"] = "register_server",
                ["protocol"] = 2,
                ["serverConnectionId"] = "srv-1",
                ["endpoint"] = "/endpoint",
            });
            await wrongProtocol.ClosedAsync();

            var noEndpoint = await WireChannel.ConnectAsync(ControlPathOf(process));
            await noEndpoint.SendAsync(new JsonObject
            {
                ["type"] = "register_server",
                ["protocol"] = 3,
                ["serverConnectionId"] = "srv-1",
            });
            await noEndpoint.ClosedAsync();

            var notAType = await WireChannel.ConnectAsync(ControlPathOf(process));
            await notAType.SendAsync(JsonValue.Create(42));
            await notAType.ClosedAsync();

            // 三种畸形注册都只留下关闭；协调器本身仍在服务。
            var healthy = await WireChannel.ConnectAsync(ControlPathOf(process));
            await healthy.SendAsync(RegisterPeer("peer-1"));
            await healthy.NextAsync("peer_registered");
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    // ---------------------------------------------------------------- routing

    [Fact]
    public async Task Send_RoutesBetweenServerAndPeer()
    {
        var process = await RunAsync();
        try
        {
            var server = await WireChannel.ConnectAsync(ControlPathOf(process));
            await server.SendAsync(RegisterServer("srv-1", "/endpoint"));
            await server.NextAsync("server_registered");

            var peer = await WireChannel.ConnectAsync(ControlPathOf(process));
            await peer.SendAsync(RegisterPeer("peer-1"));
            await peer.NextAsync("peer_registered");
            await server.NextAsync("peer_connected");

            await peer.SendAsync(new JsonObject { ["type"] = "send", ["to"] = "server", ["payload"] = "ping" });
            var atServer = await server.NextAsync("message");
            Assert.Equal("peer-1", atServer["from"]!.GetValue<string>());
            Assert.Equal("ping", atServer["payload"]!.GetValue<string>());

            await server.SendAsync(new JsonObject { ["type"] = "send", ["to"] = "peer-1", ["payload"] = "pong" });
            var atPeer = await peer.NextAsync("message");
            // server 侧的路由身份是字面量 "server"（TS 同：to === "server" 寻的就是它）。
            Assert.Equal("server", atPeer["from"]!.GetValue<string>());
            Assert.Equal("pong", atPeer["payload"]!.GetValue<string>());

            // 未知目标静默丢弃，连接保持可用。
            await server.SendAsync(new JsonObject { ["type"] = "send", ["to"] = "ghost", ["payload"] = 1 });
            await server.SendAsync(new JsonObject { ["type"] = "send", ["to"] = "peer-1", ["payload"] = "still-here" });
            var afterGhost = await peer.NextAsync("message after the dropped send");
            Assert.Equal("still-here", afterGhost["payload"]!.GetValue<string>());
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Broadcast_OnlyFromTheServer()
    {
        var process = await RunAsync();
        try
        {
            var server = await WireChannel.ConnectAsync(ControlPathOf(process));
            await server.SendAsync(RegisterServer("srv-1", "/endpoint"));
            await server.NextAsync("server_registered");

            var peer = await WireChannel.ConnectAsync(ControlPathOf(process));
            await peer.SendAsync(RegisterPeer("peer-1"));
            await peer.NextAsync("peer_registered");
            await server.NextAsync("peer_connected");

            await server.SendAsync(new JsonObject { ["type"] = "broadcast", ["payload"] = "note" });
            var atPeer = await peer.NextAsync("broadcast");
            Assert.Equal("server", atPeer["from"]!.GetValue<string>());
            Assert.Equal("note", atPeer["payload"]!.GetValue<string>());

            await peer.SendAsync(new JsonObject { ["type"] = "broadcast", ["payload"] = "note" });
            await peer.ClosedAsync();
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task UnknownRoutingMessage_IsDestroyed()
    {
        var process = await RunAsync();
        try
        {
            var peer = await WireChannel.ConnectAsync(ControlPathOf(process));
            await peer.SendAsync(RegisterPeer("peer-1"));
            await peer.NextAsync("peer_registered");

            await peer.SendAsync(new JsonObject { ["type"] = "frobnicate" });
            await peer.ClosedAsync();
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ReplacedServer_IsToldItWasReplaced()
    {
        var process = await RunAsync();
        try
        {
            var peer = await WireChannel.ConnectAsync(ControlPathOf(process));
            await peer.SendAsync(RegisterPeer("peer-1"));
            await peer.NextAsync("peer_registered");

            var first = await WireChannel.ConnectAsync(ControlPathOf(process));
            await first.SendAsync(RegisterServer("srv-1", "/endpoint"));
            await first.NextAsync("server_registered");
            await peer.NextAsync("server_connected");

            var second = await WireChannel.ConnectAsync(ControlPathOf(process));
            await second.SendAsync(RegisterServer("srv-2", "/endpoint"));
            await second.NextAsync("server_registered");

            var disconnected = await peer.NextAsync("server_disconnected");
            Assert.Equal("srv-1", disconnected["serverConnectionId"]!.GetValue<string>());
            var connected = await peer.NextAsync("server_connected");
            Assert.Equal("srv-2", connected["serverConnectionId"]!.GetValue<string>());

            var replaced = await first.NextAsync("server_replaced");
            Assert.Equal("server_replaced", replaced["type"]!.GetValue<string>());
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task DisconnectingTheServer_NotifiesThePeers()
    {
        var process = await RunAsync();
        try
        {
            var server = await WireChannel.ConnectAsync(ControlPathOf(process));
            await server.SendAsync(RegisterServer("srv-1", "/endpoint"));
            await server.NextAsync("server_registered");

            var peer = await WireChannel.ConnectAsync(ControlPathOf(process));
            await peer.SendAsync(RegisterPeer("peer-1"));
            await peer.NextAsync("peer_registered");
            await server.NextAsync("peer_connected");

            server.Dispose();
            var disconnected = await peer.NextAsync("server_disconnected");
            Assert.Equal("srv-1", disconnected["serverConnectionId"]!.GetValue<string>());

            // A peer registering afterwards is told there is no server.
            var late = await WireChannel.ConnectAsync(ControlPathOf(process));
            await late.SendAsync(RegisterPeer("peer-2"));
            var registration = await late.NextAsync("peer_registered");
            Assert.False(registration.ContainsKey("serverConnectionId"));
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    // ---------------------------------------------------------------- public proxy

    [Fact]
    public async Task PublicProxy_PipesBytesToTheServerEndpoint()
    {
        var (process, publicPath, controlPath, _) = Start();
        await process.RunAsync([publicPath, controlPath]);
        var endpointPath = TempPath("endpoint");
        var endpoint = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        endpoint.Bind(new UnixDomainSocketEndPoint(endpointPath));
        endpoint.Listen(backlog: 1);
        try
        {
            var server = await WireChannel.ConnectAsync(controlPath);
            await server.SendAsync(RegisterServer("srv-1", endpointPath));
            await server.NextAsync("server_registered");

            var accept = Task.Run(() => endpoint.AcceptAsync());
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(publicPath));
            var upstream = await accept;

            var buffer = new byte[64];
            await client.SendAsync(Encoding.UTF8.GetBytes("ping"));
            var read = await upstream.ReceiveAsync(buffer);
            Assert.Equal("ping", Encoding.UTF8.GetString(buffer, 0, read));

            await upstream.SendAsync(Encoding.UTF8.GetBytes("pong"));
            read = await client.ReceiveAsync(buffer);
            Assert.Equal("pong", Encoding.UTF8.GetString(buffer, 0, read));

            // 客户端断开会拆掉这一对。
            client.Dispose();
            read = await upstream.ReceiveAsync(buffer);
            Assert.Equal(0, read);
        }
        finally
        {
            endpoint.Dispose();
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task PublicProxy_WithoutAServer_RefusesTheConnection()
    {
        var (process, publicPath, controlPath, _) = Start();
        await process.RunAsync([publicPath, controlPath]);
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(publicPath));

            // 没有 server 时公开连接被直接销毁：对端看到 EOF。
            var buffer = new byte[8];
            var read = await client.ReceiveAsync(buffer);
            Assert.Equal(0, read);
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    // ---------------------------------------------------------------- lifecycle

    [Fact]
    public async Task RunAsync_RequiresBothSocketPaths()
    {
        var (process, _, _, _) = Start();

        var empty = await Assert.ThrowsAsync<InvalidOperationException>(() => process.RunAsync([]));
        Assert.Contains("requires public and control socket paths", empty.Message, StringComparison.Ordinal);

        var partial = await Assert.ThrowsAsync<InvalidOperationException>(
            () => process.RunAsync([TempPath("public")]));
        Assert.Contains("requires public and control socket paths", partial.Message, StringComparison.Ordinal);

        await process.RunAsync([TempPath("public"), TempPath("control")]);
        var twice = await Assert.ThrowsAsync<InvalidOperationException>(
            () => process.RunAsync([TempPath("public"), TempPath("control")]));
        Assert.Contains("already running", twice.Message, StringComparison.Ordinal);
        await process.ShutdownAsync();
    }

    [Fact]
    public async Task EmptyShutdown_ExitsAfterTheGrace()
    {
        var (process, publicPath, controlPath, exits) = Start();
        await process.RunAsync([publicPath, controlPath]);
        try
        {
            var peer = await WireChannel.ConnectAsync(controlPath);
            await peer.SendAsync(RegisterPeer("peer-1"));
            await peer.NextAsync("peer_registered");

            peer.Dispose();
            await WaitAsync(() => exits.Count > 0);
            Assert.Equal([0], exits);
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task AttachedServer_KeepsTheCoordinatorAlive()
    {
        var (process, publicPath, controlPath, exits) = Start();
        await process.RunAsync([publicPath, controlPath]);
        try
        {
            var server = await WireChannel.ConnectAsync(controlPath);
            await server.SendAsync(RegisterServer("srv-1", "/endpoint"));
            await server.NextAsync("server_registered");

            // 空窗关闭被注册取消：等过 250ms 的宽限仍不应退出。
            await Task.Delay(400);
            Assert.Empty(exits);

            server.Dispose();
            await WaitAsync(() => exits.Count > 0);
            Assert.Equal([0], exits);
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Shutdown_ClosesTheListenersAndExits()
    {
        var (process, publicPath, controlPath, exits) = Start();
        await process.RunAsync([publicPath, controlPath]);

        await process.ShutdownAsync();
        await process.ShutdownAsync();

        Assert.Equal([0], exits);
        await Assert.ThrowsAsync<SocketException>(
            () => CoordinatorSocket.ConnectAsync(controlPath));
    }

    [Fact]
    public async Task LiveCoordinator_BlocksASecondOne()
    {
        var (process, publicPath, controlPath, _) = Start();
        await process.RunAsync([publicPath, controlPath]);
        try
        {
            var rival = new CoordinatorProcess(new CoordinatorProcessOptions { Exit = _ => { } });
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => rival.RunAsync([publicPath, controlPath]));
            Assert.Contains("already active", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    /// <summary>The control path of a process started by <see cref="RunAsync"/>.</summary>
    private static string ControlPathOf(CoordinatorProcess process) => process.ControlPath;

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new InvalidOperationException("condition was not met in time");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>A control connection that records every line it receives and its close reason.</summary>
    private sealed class WireChannel
    {
        private readonly CoordinatorSocket _socket;
        private readonly Lock _gate = new();
        private readonly Queue<JsonNode?> _messages = new();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _arrival = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private WireChannel(CoordinatorSocket socket)
        {
            _socket = socket;
            socket.Closed += error =>
            {
                Closed = error ?? new InvalidOperationException("connection closed");
                Arrive();
                _closed.TrySetResult();
            };
            socket.StartReading(message =>
            {
                lock (_gate) _messages.Enqueue(message);
                Arrive();
            });
        }

        public Exception? Closed { get; private set; }

        public static async Task<WireChannel> ConnectAsync(string path)
            => new(await CoordinatorSocket.ConnectAsync(path));

        public Task SendAsync(JsonNode? message) => _socket.WriteLineAsync(message);

        public async Task<JsonObject> NextAsync(string description)
        {
            var timeout = Task.Delay(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task arrival;
                JsonNode? message;
                lock (_gate)
                {
                    message = _messages.Count > 0 ? _messages.Dequeue() : null;
                    arrival = _arrival.Task;
                }

                if (message is not null)
                {
                    return message as JsonObject
                        ?? throw new InvalidOperationException($"{description}: expected an object, got {message}");
                }

                var completed = await Task.WhenAny(arrival, timeout);
                if (completed == timeout)
                {
                    throw new InvalidOperationException(
                        $"{description}: timed out (closed={Closed?.Message ?? "no"})");
                }

                await arrival;
            }
        }

        public Task ClosedAsync() => _closed.Task;

        public void Dispose() => _socket.Dispose();

        private void Arrive()
        {
            lock (_gate)
            {
                var pending = _arrival;
                _arrival = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.TrySetResult();
            }
        }
    }
}
