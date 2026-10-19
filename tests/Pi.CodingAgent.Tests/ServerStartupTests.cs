using Pi.Chord.Services;
using Pi.Client;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental;
using Pi.Protocol;
using Pi.Server.Transports.Unix;
using Xunit;
using DeltaOp = Pi.Chord.Delta.DeltaOp;
using ServerClient = Pi.Client.Client;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <see cref="ExperimentalServer.StartServerAsync"/> end to end: a real in-process coordinator, a
/// real per-generation Unix socket, the real session services, and a real client over the public
/// endpoint. Covers the startup → serve → close path, the replacement path, and the default
/// identity.
/// </summary>
public class ServerStartupTests : IAsyncDisposable
{
    private const string ServerId = "3f0d1c2a-9b4e-4d1f-8a77-5c6b7d8e9f02";
    private const string SecondServerId = "3f0d1c2a-9b4e-4d1f-8a77-5c6b7d8e9f03";

    private readonly string _root;
    private readonly string _agentDir;
    private readonly string? _previousAgentDir;
    private readonly List<CoordinatorProcess> _coordinators = [];
    private readonly List<RunningServer> _runtimes = [];

    public ServerStartupTests()
    {
        _root = MakeRoot();
        Directory.CreateDirectory(_root);
        // Nothing in the server's path may reach the real agent directory; pin it, the way the
        // worker e2e does, so a future facet cannot touch the user's files.
        _agentDir = Path.Combine(_root, "agent");
        _previousAgentDir = Environment.GetEnvironmentVariable(Config.EnvAgentDir);
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, _agentDir);
    }

    /// <summary>
    /// AF_UNIX paths are capped at 108 bytes on every platform, and the per-generation socket name
    /// alone (<c>server-&lt;serverId&gt;-&lt;nonce&gt;.sock</c>) is 61 of them — so the server
    /// directory has to be short. The default temp directory on Windows is deep enough to matter,
    /// so the first base whose socket path fits wins.
    /// </summary>
    private static string MakeRoot()
    {
        var name = "psv" + Guid.NewGuid().ToString("N")[..8];
        var probeSocket = $"server-{ServerId}-0123456789ab.sock";
        foreach (var baseDirectory in new[]
                 {
                     Path.GetTempPath(), Path.GetPathRoot(Directory.GetCurrentDirectory()) ?? ".",
                 })
        {
            var candidate = Path.Combine(baseDirectory, name);
            if (Path.Combine(candidate, probeSocket).Length <= 108) return candidate;
        }

        throw new InvalidOperationException(
            "No directory short enough for a Unix socket path; set TEMP to a shallow directory");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var runtime in _runtimes)
        {
            try
            {
                await runtime.Close();
            }
            catch
            {
                // Teardown must not mask the test result.
            }
        }

        foreach (var process in _coordinators)
        {
            try
            {
                await process.ShutdownAsync();
            }
            catch
            {
                // Ditto.
            }
        }

        Environment.SetEnvironmentVariable(Config.EnvAgentDir, _previousAgentDir);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A stubborn artifact (e.g. a socket the OS has not reaped) is not a failure.
        }
    }

    [Fact]
    public async Task StartServer_ServesSessionsBehindTheCoordinatorEndpoint()
    {
        await StartCoordinatorAsync(ServerId);
        var sessionDir = Path.Combine(_root, "sessions");

        var running = await TrackAsync(ExperimentalServer.StartServerAsync(new StartServerOptions
        {
            Directory = _root,
            ServerId = ServerId,
            SessionDir = sessionDir,
            KeepAlive = true,
        }));

        Assert.Equal(ServerId, running.ServerId);
        Assert.Equal(sessionDir, running.SessionDir);
        Assert.Equal(UnixSocketAddress.GetUnixSocketPath(ServerId, _root), running.SocketPath);
        Assert.True(File.Exists(running.SocketPath), "the coordinator proxies the stable endpoint");
        Assert.Empty(running.WorkerPids);

        await using var client = ConnectClient(running.SocketPath, ServerId);
        var hello = await client.ConnectAsync();
        Assert.Equal(ServerId, hello.ServerId);

        // The durable directory starts empty. The startup refresh already advanced the revision
        // past its initial 1 (every refresh increments before publishing).
        await using (var initial = await SubscribeDirectoryAsync(client, ServerId))
        {
            var empty = DirectoryState(initial);
            Assert.Equal(2L, empty["revision"]);
            Assert.Empty(Assert.IsType<List<object?>>(empty["sessions"]));
        }

        // A create round-trips over the wire, lands in the durable directory, and refreshes the
        // replicated session list.
        var created = await client.RequestAsync(
            new RpcTarget.ServerTarget(ServerId),
            new ServiceCall("pi.session-management", "create",
                [new Dictionary<string, object?> { ["id"] = "s1" }]));
        var summary = Assert.IsType<Dictionary<string, object?>>(created);
        Assert.Equal(ServerId, summary["serverId"]);
        Assert.Equal("s1", summary["sessionId"]);
        Assert.True(File.Exists(Path.Combine(sessionDir, "s1", "meta.json")));

        await using (var subscription = await SubscribeDirectoryAsync(client, ServerId))
        {
            var state = DirectoryState(subscription);
            Assert.Equal(3L, state["revision"]);
            var sessions = Assert.IsType<List<object?>>(state["sessions"]);
            var single = Assert.IsType<Dictionary<string, object?>>(Assert.Single(sessions));
            Assert.Equal("s1", single["sessionId"]);
            Assert.Equal(ServerId, single["serverId"]);
        }

        // Closing the server releases the endpoint and completes the closed task.
        await running.Close();
        await running.Closed.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task StartServer_IsReplacedByANewerGeneration()
    {
        await StartCoordinatorAsync(ServerId);
        var sessionDir = Path.Combine(_root, "sessions");

        var first = await TrackAsync(ExperimentalServer.StartServerAsync(new StartServerOptions
        {
            Directory = _root,
            ServerId = ServerId,
            SessionDir = sessionDir,
            KeepAlive = true,
        }));

        // The second generation registers with the same coordinator; the first one is replaced and
        // unwinds (detaching rather than shutting its workers down).
        var second = await TrackAsync(ExperimentalServer.StartServerAsync(new StartServerOptions
        {
            Directory = _root,
            ServerId = ServerId,
            SessionDir = sessionDir,
            KeepAlive = true,
        }));

        await first.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(second.Closed.IsCompleted, "the new generation keeps running");

        // The new generation serves the same stable endpoint.
        await using var client = ConnectClient(second.SocketPath, ServerId);
        await client.ConnectAsync();
        var created = await client.RequestAsync(
            new RpcTarget.ServerTarget(ServerId),
            new ServiceCall("pi.session-management", "create",
                [new Dictionary<string, object?> { ["id"] = "s2" }]));
        var summary = Assert.IsType<Dictionary<string, object?>>(created);
        Assert.Equal("s2", summary["sessionId"]);
        Assert.True(File.Exists(Path.Combine(sessionDir, "s2", "meta.json")));
    }

    [Fact]
    public async Task StartServer_MintsTheDefaultIdentity()
    {
        // The mint itself is covered by the profile tests; here the wiring under test is that the
        // server adopts the directory's stored default identity (the file the first mint wrote).
        var defaultId = "3f0d1c2a-9b4e-4d1f-8a77-5c6b7d8e9f04";
        await File.WriteAllTextAsync(Path.Combine(_root, "default-server-id"), defaultId);
        await StartCoordinatorAsync(defaultId);
        var sessionDir = Path.Combine(_root, "sessions");

        var running = await TrackAsync(ExperimentalServer.StartServerAsync(new StartServerOptions
        {
            Directory = _root,
            SessionDir = sessionDir,
            KeepAlive = true,
        }));

        var stored = (await File.ReadAllTextAsync(Path.Combine(_root, "default-server-id"))).Trim();
        Assert.Equal(stored, running.ServerId);
        Assert.True(ServerIds.IsServerId(running.ServerId));
        Assert.Equal(UnixSocketAddress.GetUnixSocketPath(stored, _root), running.SocketPath);
    }

    [Fact]
    public async Task StartForegroundServer_SerializesAgainstColdActivation()
    {
        await StartCoordinatorAsync(SecondServerId);
        var sessionDir = Path.Combine(_root, "sessions");

        // The foreground dance takes the profile lock, releases it, and holds the activation lock
        // across the server's startup only (TS releases it in the finally, once startServer
        // resolves). A second start in the same directory therefore proceeds once the first is up,
        // and — both being the same logical generation — the newer one replaces it.
        var first = await TrackAsync(ExperimentalServer.StartForegroundServerAsync(new StartServerOptions
        {
            Directory = _root,
            ServerId = SecondServerId,
            SessionDir = sessionDir,
        }));

        var second = ExperimentalServer.StartForegroundServerAsync(new StartServerOptions
        {
            Directory = _root,
            ServerId = SecondServerId,
            SessionDir = sessionDir,
        });
        // A held launcher lock (the profile was never released) would surface here as a
        // LockHeldException after the 30s wait.
        var started = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(second, started);
        await TrackAsync(second);

        await first.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False((await second).Closed.IsCompleted, "the newer generation keeps running");
    }

    [Fact]
    public async Task ActivateServer_ReturnsTheRunningServerWithoutSpawning()
    {
        await StartCoordinatorAsync(ServerId);
        var sessionDir = Path.Combine(_root, "sessions");
        await TrackAsync(ExperimentalServer.StartServerAsync(new StartServerOptions
        {
            Directory = _root,
            ServerId = ServerId,
            SessionDir = sessionDir,
            KeepAlive = true,
        }));

        // The server is already reachable, so activation connects instead of spawning: the route
        // points at the stable public socket.
        var activated = await ExperimentalServer.ActivateServerAsync(new ActivateServerOptions
        {
            Directory = _root,
            RequestedServerId = ServerId,
            SessionDir = sessionDir,
        });

        await using (activated.Client)
        {
            Assert.Equal(ServerId, activated.Route.ServerId);
            Assert.Equal(UnixSocketAddress.GetUnixSocketPath(ServerId, _root), activated.Route.Path);
            Assert.True(activated.Client.Connected);
        }
    }

    [Fact]
    public async Task ActivateServer_RefusesAModelSelectionForAnExistingServer()
    {
        await StartCoordinatorAsync(ServerId);
        var sessionDir = Path.Combine(_root, "sessions");
        await TrackAsync(ExperimentalServer.StartServerAsync(new StartServerOptions
        {
            Directory = _root,
            ServerId = ServerId,
            SessionDir = sessionDir,
            KeepAlive = true,
        }));

        // A model selection is startup-only; another activator won the race, so it can no longer
        // be applied and the connection it opened is disposed.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExperimentalServer.ActivateServerAsync(new ActivateServerOptions
            {
                Directory = _root,
                RequestedServerId = ServerId,
                SessionDir = sessionDir,
                Provider = "openai",
                Model = "gpt-5",
            }));
        Assert.Contains("Model selection is only valid when automatically activating a new server",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivateServer_RequiresAModelWithAProvider()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExperimentalServer.ActivateServerAsync(new ActivateServerOptions
            {
                Directory = _root,
                RequestedServerId = ServerId,
                SessionDir = Path.Combine(_root, "sessions"),
                Provider = "openai",
            }));
        Assert.Contains("Server model provider requires a model", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Start an in-process coordinator at the paths one server generation derives for itself: the
    /// stable public socket and the <c>control-&lt;serverId&gt;.sock</c> beside it. With it already
    /// listening, <see cref="Coordinator.EnsureCoordinatorAsync"/> probes instead of spawning.
    /// </summary>
    private async Task StartCoordinatorAsync(string serverId)
    {
        var publicPath = UnixSocketAddress.GetUnixSocketPath(serverId, _root);
        var controlPath = Path.Combine(_root, $"control-{serverId}.sock");
        var process = new CoordinatorProcess(new CoordinatorProcessOptions { Exit = _ => { } });
        await process.RunAsync([publicPath, controlPath]);
        _coordinators.Add(process);
    }

    private async Task<RunningServer> TrackAsync(Task<RunningServer> start)
    {
        var running = await start;
        _runtimes.Add(running);
        return running;
    }

    private static ServerClient ConnectClient(string path, string serverId)
        => new(new ClientOptions
        {
            ServerId = serverId,
            TransportFactory = UnixTransport.CreateUnixTransportFactory(new UnixTransportOptions { Path = path }),
        });

    private static Task<IServiceSubscription> SubscribeDirectoryAsync(ServerClient client, string serverId)
        => client.SubscribeServiceAsync(
            new RpcTarget.ServerTarget(serverId),
            "pi.session-directory",
            ServiceMode.Singleton,
            _ => Task.CompletedTask);

    /// <summary>The replicated session directory state: <c>{ revision, sessions }</c>.</summary>
    private static Dictionary<string, object?> DirectoryState(IServiceSubscription subscription)
    {
        var instance = Assert.Single(subscription.Snapshot.Instances);
        var member = Assert.IsType<ServiceMemberSnapshot.State>(
            instance.Members.OfType<ServiceMemberSnapshot.State>().Single());
        var op = Assert.IsType<DeltaOp.Replace>(Assert.Single(member.Ops));
        return Assert.IsType<Dictionary<string, object?>>(op.Value);
    }
}
