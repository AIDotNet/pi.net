using Pi.Client;
using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental;
using Pi.CodingAgent.Experimental.Services;
using Pi.Protocol;
using Pi.Server.Transports.Unix;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <see cref="ExperimentalClient.RunClientAsync"/> against real in-process servers: the list path
/// and every selection error. The attached and prompted paths need a live session worker (the
/// session-scoped services hydrate through it), so they are deferred to the batch that wires the
/// worker process (C362) — TS covers them in <c>experimental-remote-runtime.test.ts</c> with a faux
/// worker.
/// </summary>
public class ClientTests : IAsyncDisposable
{
    private const string ServerId = "3f0d1c2a-9b4e-4d1f-8a77-5c6b7d8e9f02";
    private const string SecondServerId = "3f0d1c2a-9b4e-4d1f-8a77-5c6b7d8e9f03";

    private readonly string _root;
    private readonly string _agentDir;
    private readonly string? _previousAgentDir;
    private readonly List<CoordinatorProcess> _coordinators = [];
    private readonly List<RunningServer> _runtimes = [];

    public ClientTests()
    {
        _root = MakeRoot();
        Directory.CreateDirectory(_root);
        // Nothing in the client's path may reach the real agent directory; pin it, the way the
        // server e2e does, so a future facet cannot touch the user's files.
        _agentDir = Path.Combine(_root, "agent");
        _previousAgentDir = Environment.GetEnvironmentVariable(Config.EnvAgentDir);
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, _agentDir);

        // TS seeds the durable session directory in beforeEach — before any server starts, so the
        // startup refresh sees the sessions. A session created later is invisible until a refresh.
        var sessionDir = Path.Combine(_root, "sessions");
        CreateSession(sessionDir, "demo-1");
        CreateSession(sessionDir, "demo-2");
    }

    /// <summary>AF_UNIX paths are capped at 108 bytes; the per-generation socket name alone is 61.</summary>
    private static string MakeRoot()
    {
        var name = "pcl" + Guid.NewGuid().ToString("N")[..8];
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
    public async Task ListsEverySessionOfEveryDiscoveredServer()
    {
        // Two logical servers share one directory and one session directory, so discovery finds
        // both and the list carries every session of each, ordered by server then session.
        await StartServerAsync(ServerId);
        await StartServerAsync(SecondServerId);

        var result = await ExperimentalClient.RunClientAsync(
            new ClientCommand(), new RunClientOptions { Directory = _root });

        var list = Assert.IsType<ClientResult.List>(result);
        Assert.Equal(
            [
                new SessionAddress(ServerId, "demo-1"),
                new SessionAddress(ServerId, "demo-2"),
                new SessionAddress(SecondServerId, "demo-1"),
                new SessionAddress(SecondServerId, "demo-2"),
            ],
            list.Sessions);
    }

    [Fact]
    public async Task RejectsAMissingSessionSelection()
    {
        await StartServerAsync(ServerId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExperimentalClient.RunClientAsync(
                new ClientCommand { SessionId = "missing" }, new RunClientOptions { Directory = _root }));
        Assert.Equal("No discovered server contains session missing", error.Message);
    }

    [Fact]
    public async Task RejectsAnAmbiguousSessionSelection()
    {
        // Both servers host the same session directory, so the selection matches twice.
        await StartServerAsync(ServerId);
        await StartServerAsync(SecondServerId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExperimentalClient.RunClientAsync(
                new ClientCommand { SessionId = "demo-1" }, new RunClientOptions { Directory = _root }));
        Assert.Equal("Session demo-1 is available from more than one server", error.Message);
    }

    [Fact]
    public async Task RequiresExactlyOneServerToCreateASession()
    {
        await StartServerAsync(ServerId);
        await StartServerAsync(SecondServerId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExperimentalClient.RunClientAsync(
                new ClientCommand { Prompt = "hello" }, new RunClientOptions { Directory = _root }));
        Assert.Equal(
            "Client prompt requires exactly one discovered server to create a Session", error.Message);
    }

    [Fact]
    public async Task RejectsAMissingSessionSelectionWithoutAPrompt()
    {
        // Without a prompt there is nothing to create, so a missing session is an error rather
        // than an attach.
        await StartServerAsync(ServerId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExperimentalClient.RunClientAsync(
                new ClientCommand { SessionId = "missing" }, new RunClientOptions { Directory = _root }));
        Assert.Equal("No discovered server contains session missing", error.Message);
    }

    // ---------------------------------------------------------------- harness

    private async Task StartCoordinatorAsync(string serverId)
    {
        var publicPath = UnixSocketAddress.GetUnixSocketPath(serverId, _root);
        var controlPath = Path.Combine(_root, $"control-{serverId}.sock");
        var process = new CoordinatorProcess(new CoordinatorProcessOptions { Exit = _ => { } });
        await process.RunAsync([publicPath, controlPath]);
        _coordinators.Add(process);
    }

    private async Task<RunningServer> StartServerAsync(string serverId)
    {
        await StartCoordinatorAsync(serverId);
        var running = await ExperimentalServer.StartServerAsync(new StartServerOptions
        {
            Directory = _root,
            ServerId = serverId,
            SessionDir = Path.Combine(_root, "sessions"),
            KeepAlive = true,
        });
        _runtimes.Add(running);
        return running;
    }

    /// <summary>A session directory as <see cref="SessionCatalog.CreateSessionAsync"/> writes it.</summary>
    private static void CreateSession(string sessionDir, string id)
    {
        var path = Path.Combine(sessionDir, id);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "meta.json"), $"{{\n\t\"createdAt\": 1,\n\t\"cwd\": \"/tmp\"\n}}\n");
    }
}
