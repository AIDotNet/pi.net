using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Experimental;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the coordinator client (4g-3c-3): registration, routing, replacement, and the startup
/// lease. The protocol tests run against the real coordinator process; the startup seams keep the
/// spawn/probe behaviour observable without one.
/// </summary>
public class CoordinatorTests
{
    private static string TempPath(string name)
        => Path.Combine(Path.GetTempPath(), $"pi-coordclient-{Guid.NewGuid():N}-{name}");

    private static async Task<Socket> ListenAsync(string path)
    {
        var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(path));
        server.Listen(backlog: 4);
        await Task.CompletedTask;
        return server;
    }

    private static async Task<(CoordinatorProcess Process, string PublicPath, string ControlPath)> StartAsync()
    {
        var process = new CoordinatorProcess(new CoordinatorProcessOptions { Exit = _ => { } });
        var publicPath = TempPath("public");
        var controlPath = TempPath("control");
        await process.RunAsync([publicPath, controlPath]);
        return (process, publicPath, controlPath);
    }

    // ---------------------------------------------------------------- message vocabulary

    [Fact]
    public void CoordinatorMessage_AcceptsTheFiveShapes()
    {
        var registered = Assert.IsType<CoordinatorMessage.ServerRegistered>(CoordinatorMessage.TryParse(
            JsonNode.Parse("""
            { "type": "server_registered", "serverConnectionId": "srv-1", "peers": ["a", "b"], "extra": 1 }
            """)));
        Assert.Equal("srv-1", registered.ServerConnectionId);
        Assert.Equal(["a", "b"], registered.Peers);

        Assert.IsType<CoordinatorMessage.ServerReplaced>(CoordinatorMessage.TryParse(
            JsonNode.Parse("""{ "type": "server_replaced" }""")));

        var connected = Assert.IsType<CoordinatorMessage.Routed>(CoordinatorMessage.TryParse(
            JsonNode.Parse("""{ "type": "peer_connected", "peerId": "a" }""")));
        var disconnected = Assert.IsType<CoordinatorMessage.Routed>(CoordinatorMessage.TryParse(
            JsonNode.Parse("""{ "type": "peer_disconnected", "peerId": "a" }""")));
        Assert.IsType<PeerConnected>(connected.Event);
        Assert.IsType<PeerDisconnected>(disconnected.Event);

        var message = Assert.IsType<CoordinatorMessage.Routed>(CoordinatorMessage.TryParse(
            JsonNode.Parse("""{ "type": "message", "from": "a", "payload": { "x": 1 } }""")));
        var payload = Assert.IsType<ConnectionMessage>(message.Event).Payload;
        Assert.Equal(1, payload!["x"]!.GetValue<int>());
    }

    [Fact]
    public void CoordinatorMessage_RejectsAnythingElse()
    {
        // 未知类型、缺字段、错类型、空串 id、peers 含非字符串、payload 键缺失。
        Assert.Null(CoordinatorMessage.TryParse(JsonNode.Parse("""{ "type": "bogus" }""")));
        Assert.Null(CoordinatorMessage.TryParse(JsonNode.Parse("""{ "type": "peer_connected" }""")));
        Assert.Null(CoordinatorMessage.TryParse(JsonNode.Parse("""{ "type": "peer_connected", "peerId": "" }""")));
        Assert.Null(CoordinatorMessage.TryParse(JsonNode.Parse("""{ "type": "peer_connected", "peerId": 3 }""")));
        Assert.Null(CoordinatorMessage.TryParse(JsonNode.Parse(
            """{ "type": "server_registered", "serverConnectionId": "s", "peers": ["a", 2] }""")));
        Assert.Null(CoordinatorMessage.TryParse(JsonNode.Parse("""{ "type": "message", "from": "a" }""")));
        Assert.Null(CoordinatorMessage.TryParse(JsonNode.Parse("""{ "type": "message", "payload": 1 }""")));
        Assert.Null(CoordinatorMessage.TryParse(JsonNode.Parse("""["message"]""")));
        Assert.Null(CoordinatorMessage.TryParse(JsonValue.Create("message")));
        Assert.Null(CoordinatorMessage.TryParse(null));
    }

    // ---------------------------------------------------------------- connection

    [Fact]
    public async Task Connect_RegistersAndReportsTheAttachedPeers()
    {
        var (process, _, controlPath) = await StartAsync();
        try
        {
            var server = new CoordinatorConnection(new CoordinatorConnectionOptions(controlPath, "/endpoint")
            {
                ServerConnectionId = "srv-1",
            });
            await server.ConnectAsync();

            Assert.Equal("srv-1", server.ServerConnectionId);
            Assert.Empty(server.PeerIds);

            // A peer attaching is reported to the server and lands in its peer set.
            var peer = await CoordinatorSocket.ConnectAsync(controlPath);
            await peer.WriteLineAsync(new JsonObject
            {
                ["type"] = "register_peer",
                ["protocol"] = 3,
                ["peerId"] = "peer-1",
            });
            await WaitAsync(() => server.PeerIds.Contains("peer-1"));

            server.Close();
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SendAndBroadcast_ReachThePeers()
    {
        var (process, _, controlPath) = await StartAsync();
        try
        {
            var server = new CoordinatorConnection(new CoordinatorConnectionOptions(controlPath, "/endpoint"));
            await server.ConnectAsync();

            var first = await CoordinatorSocket.ConnectAsync(controlPath);
            var second = await CoordinatorSocket.ConnectAsync(controlPath);

            var seen = new List<JsonObject>();
            void Observe(JsonNode? message)
            {
                var line = (JsonObject)message!;
                if (line["type"]!.GetValue<string>() == "message") seen.Add(line);
            }

            first.StartReading(Observe);
            second.StartReading(Observe);
            foreach (var (channel, peerId) in new[] { (first, "peer-1"), (second, "peer-2") })
            {
                await channel.WriteLineAsync(new JsonObject
                {
                    ["type"] = "register_peer",
                    ["protocol"] = 3,
                    ["peerId"] = peerId,
                });
            }

            await server.SendAsync("peer-1", JsonValue.Create("only-you"));
            await server.BroadcastAsync(JsonValue.Create("everyone"));

            await WaitAsync(() => seen.Count == 3);
            var toFirst = Assert.IsType<JsonObject>(seen[0]);
            Assert.Equal("message", toFirst["type"]!.GetValue<string>());
            Assert.Equal("only-you", toFirst["payload"]!.GetValue<string>());
            var broadcastFirst = Assert.IsType<JsonObject>(seen[1]);
            Assert.Equal("everyone", broadcastFirst["payload"]!.GetValue<string>());
            var broadcastSecond = Assert.IsType<JsonObject>(seen[2]);
            Assert.Equal("everyone", broadcastSecond["payload"]!.GetValue<string>());

            server.Close();
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ReplacedServer_IsToldItWasReplaced()
    {
        var (process, _, controlPath) = await StartAsync();
        try
        {
            var first = new CoordinatorConnection(new CoordinatorConnectionOptions(controlPath, "/endpoint"));
            await first.ConnectAsync();

            var second = new CoordinatorConnection(new CoordinatorConnectionOptions(controlPath, "/endpoint"));
            await second.ConnectAsync();

            await first.Replaced;
            Assert.True(first.WasReplaced);
            Assert.False(second.WasReplaced);

            first.Close();
            second.Close();
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Events_UnsubscribeStopsDelivery()
    {
        var (process, _, controlPath) = await StartAsync();
        try
        {
            var server = new CoordinatorConnection(new CoordinatorConnectionOptions(controlPath, "/endpoint"));
            await server.ConnectAsync();

            var seen = 0;
            var unsubscribe = server.OnEvent(_ => seen += 1);

            var peer = await CoordinatorSocket.ConnectAsync(controlPath);
            await peer.WriteLineAsync(new JsonObject
            {
                ["type"] = "register_peer",
                ["protocol"] = 3,
                ["peerId"] = "peer-1",
            });
            await WaitAsync(() => seen == 1);

            unsubscribe();
            await peer.WriteLineAsync(new JsonObject { ["type"] = "send", ["to"] = "server", ["payload"] = 1 });
            await Task.Delay(50);
            Assert.Equal(1, seen);

            server.Close();
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task InvalidLine_DestroysTheConnection()
    {
        var path = TempPath("control");
        var listener = await ListenAsync(path);
        try
        {
            var accept = Task.Run(() => listener.AcceptAsync());
            var connection = new CoordinatorConnection(new CoordinatorConnectionOptions(path, "/endpoint"));
            var connect = connection.ConnectAsync();

            var coordinator = await accept;
            await coordinator.SendAsync(Encoding.UTF8.GetBytes("not json\n"));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connect);
            Assert.Contains("Coordinator sent invalid JSON", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public async Task InvalidMessage_DestroysTheConnection()
    {
        var path = TempPath("control");
        var listener = await ListenAsync(path);
        try
        {
            var accept = Task.Run(() => listener.AcceptAsync());
            var connection = new CoordinatorConnection(new CoordinatorConnectionOptions(path, "/endpoint"));
            var connect = connection.ConnectAsync();

            var coordinator = await accept;
            await coordinator.SendAsync(Encoding.UTF8.GetBytes("""{"type":"bogus"}""" + "\n"));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connect);
            Assert.Contains("Coordinator sent an invalid message", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public async Task Close_RejectsThePendingRegistration()
    {
        var path = TempPath("control");
        var listener = await ListenAsync(path);
        try
        {
            var accept = Task.Run(() => listener.AcceptAsync());
            var connection = new CoordinatorConnection(new CoordinatorConnectionOptions(path, "/endpoint"));
            var connect = connection.ConnectAsync();

            await accept;
            // 假协调器永不回复：注册一直挂起，Close 必须让它失败而不是永久挂住。
            connection.Close();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connect);
            Assert.Contains("Coordinator server closed", error.Message, StringComparison.Ordinal);

            var send = await Assert.ThrowsAsync<InvalidOperationException>(
                () => connection.SendAsync("peer-1", JsonValue.Create("hi")));
            Assert.Contains("Coordinator server is not connected", send.Message, StringComparison.Ordinal);
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public async Task Send_WithoutARegistration_Rejects()
    {
        var (process, _, controlPath) = await StartAsync();
        try
        {
            var connection = new CoordinatorConnection(new CoordinatorConnectionOptions(controlPath, "/endpoint"));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => connection.SendAsync("peer-1", JsonValue.Create("hi")));
            Assert.Contains("Coordinator server is not connected", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Connect_Twice_Rejects()
    {
        var (process, _, controlPath) = await StartAsync();
        try
        {
            var connection = new CoordinatorConnection(new CoordinatorConnectionOptions(controlPath, "/endpoint"));
            await connection.ConnectAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.ConnectAsync());
            Assert.Contains("already connected", error.Message, StringComparison.Ordinal);
            connection.Close();
        }
        finally
        {
            await process.ShutdownAsync();
        }
    }

    // ---------------------------------------------------------------- startup lease

    [Fact]
    public async Task EnsureCoordinator_ReusesALiveCoordinator()
    {
        var path = TempPath("control");
        var listener = await ListenAsync(path);
        try
        {
            var spawned = false;
            var lease = await Coordinator.EnsureCoordinatorAsync("/public", path, new EnsureCoordinatorOptions
            {
                Spawn = () =>
                {
                    spawned = true;
                    throw new InvalidOperationException("must not spawn");
                },
            });

            lease.Close();
            Assert.False(spawned);
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public async Task EnsureCoordinator_SpawnsAndWaitsForTheListener()
    {
        var path = TempPath("control");
        var listener = await ListenAsync(path);
        try
        {
            var attempts = 0;
            var lease = await Coordinator.EnsureCoordinatorAsync("/public", path, new EnsureCoordinatorOptions
            {
                // 一个永远不"退出"的子进程占位（启动失败句柄的 ExitCode 恒为 null）。
                Spawn = () => ChildProcess.SpawnProcess("pi-coordinator-not-installed", []),
                Connect = _ =>
                {
                    attempts += 1;
                    return attempts < 3
                        ? Task.FromResult<CoordinatorSocket?>(null)
                        : ConnectRealAsync(path);
                },
                RetryMs = 5,
            });

            lease.Close();
            Assert.Equal(3, attempts);
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public async Task EnsureCoordinator_TimesOutWhenNothingListens()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Coordinator.EnsureCoordinatorAsync("/public", TempPath("control"), new EnsureCoordinatorOptions
            {
                Spawn = () => ChildProcess.SpawnProcess("pi-coordinator-not-installed", []),
                Connect = _ => Task.FromResult<CoordinatorSocket?>(null),
                StartTimeoutMs = 80,
                RetryMs = 10,
            }));

        Assert.Contains("Timed out waiting for coordinator startup", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureCoordinator_ReportsAChildThatExited()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Coordinator.EnsureCoordinatorAsync("/public", TempPath("control"), new EnsureCoordinatorOptions
            {
                // 本测试程序带一个不存在的过滤器参数启动：秒退并带退出码。
                Spawn = () => ChildProcess.SpawnProcess(
                    Environment.ProcessPath!, ["--help"]),
                Connect = _ => Task.FromResult<CoordinatorSocket?>(null),
                RetryMs = 10,
            }));

        Assert.Contains("Coordinator exited during startup", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCoordinatorEntry_RequiresTheCoordinatorRole()
    {
        Environment.SetEnvironmentVariable(InternalProcess.InternalProcessEnv, null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Coordinator.RunCoordinatorEntryAsync(["/public", "/control"]));

        Assert.Contains("requires an internal coordinator invocation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCoordinatorEntry_RunsTheCoordinatorAndExitsWhenEmpty()
    {
        var exits = new List<int>();
        var publicPath = TempPath("public");
        var controlPath = TempPath("control");
        Environment.SetEnvironmentVariable(InternalProcess.InternalProcessEnv, "coordinator");
        try
        {
            await Coordinator.RunCoordinatorEntryAsync([publicPath, controlPath],
                new CoordinatorProcessOptions { Exit = code => exits.Add(code) });

            // 一个 peer 连上再断开：空窗期到点后协调器自行退出。
            var peer = await CoordinatorSocket.ConnectAsync(controlPath);
            await peer.WriteLineAsync(new JsonObject
            {
                ["type"] = "register_peer",
                ["protocol"] = 3,
                ["peerId"] = "peer-1",
            });
            peer.Dispose();

            await WaitAsync(() => exits.Count > 0);
            Assert.Equal([0], exits);
        }
        finally
        {
            Environment.SetEnvironmentVariable(InternalProcess.InternalProcessEnv, null);
        }
    }

    [Fact]
    public async Task HandlerException_DestroysWithThatError()
    {
        var path = TempPath("control");
        var listener = await ListenAsync(path);
        try
        {
            var accept = Task.Run(() => listener.AcceptAsync());
            var peer = await CoordinatorSocket.ConnectAsync(path);
            var coordinator = CoordinatorSocket.Accept(await accept);
            var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.Closed += error => closed.TrySetResult(error);

            // TS 的 reader 在处理器抛错时以该错误销毁连接（协调器的注册/路由校验即依赖此路径）。
            coordinator.StartReading(_ => throw new InvalidOperationException("handler exploded"));
            await peer.WriteLineAsync(JsonValue.Create("hello"));

            var error = await closed.Task;
            Assert.Contains("handler exploded", error!.Message, StringComparison.Ordinal);
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public async Task Writes_KeepTheirOrderOnOneConnection()
    {
        var path = TempPath("control");
        var listener = await ListenAsync(path);
        try
        {
            var accept = Task.Run(() => listener.AcceptAsync());
            var peer = await CoordinatorSocket.ConnectAsync(path);
            var coordinator = CoordinatorSocket.Accept(await accept);

            var received = new List<string>();
            peer.StartReading(message => received.Add(message!.ToJsonString()));

            // 同一连接上连续三次发射后忘：Node 的 socket.write 队列保证按序到达。
            coordinator.WriteLine(JsonValue.Create("first"));
            coordinator.WriteLine(JsonValue.Create("second"));
            coordinator.WriteLine(JsonValue.Create("third"));

            await WaitAsync(() => received.Count == 3);
            Assert.Equal(["\"first\"", "\"second\"", "\"third\""], received);
        }
        finally
        {
            listener.Dispose();
        }
    }

    private static async Task<CoordinatorSocket?> ConnectRealAsync(string path)
        => await CoordinatorSocket.ConnectAsync(path);

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
}
