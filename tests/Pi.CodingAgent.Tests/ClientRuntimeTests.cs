using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Pi.Client;
using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental;
using Pi.CodingAgent.Experimental.Services;
using Pi.Durable.Harness;
using Pi.Durable.Types;
using Pi.Protocol;
using Pi.Server.Transports.Unix;
using Xunit;
using ServerClient = Pi.Client.Client;
// Pi.Durable.Harness declares its own ModelRef; the experimental service one is what the facades use.
using ServicesModelRef = Pi.CodingAgent.Experimental.Services.ModelRef;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <see cref="ClientRuntimes"/> end to end and in isolation: the command validations and the route
/// selection against a real in-process server (a real coordinator, a real socket, a real client),
/// plus the typed facade adapters and the management wrapper over a loopback transport. The C#
/// mirror of the client half of TS's <c>experimental-remote-runtime.test.ts</c>.
/// </summary>
public class ClientRuntimeTests : IAsyncDisposable
{
    private const string ServerId = "3f0d1c2a-9b4e-4d1f-8a77-5c6b7d8e9f02";
    private const string SecondServerId = "3f0d1c2a-9b4e-4d1f-8a77-5c6b7d8e9f03";

    private readonly string _root;
    private readonly string _agentDir;
    private readonly string? _previousAgentDir;
    private readonly List<CoordinatorProcess> _coordinators = [];
    private readonly List<RunningServer> _runtimes = [];
    private readonly List<ClientRuntime> _clientRuntimes = [];

    public ClientRuntimeTests()
    {
        _root = MakeRoot();
        Directory.CreateDirectory(_root);
        // Nothing in the client's path may reach the real agent directory; pin it, the way the
        // server e2e does, so a future facet cannot touch the user's files.
        _agentDir = Path.Combine(_root, "agent");
        _previousAgentDir = Environment.GetEnvironmentVariable(Config.EnvAgentDir);
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, _agentDir);
    }

    /// <summary>
    /// AF_UNIX paths are capped at 108 bytes on every platform, and the per-generation socket name
    /// alone is 61 of them — so the server directory has to be short (same budget the server e2e
    /// uses).
    /// </summary>
    private static string MakeRoot()
    {
        var name = "pcv" + Guid.NewGuid().ToString("N")[..8];
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
        foreach (var runtime in _clientRuntimes)
        {
            try
            {
                await runtime.Dispose();
            }
            catch
            {
                // Teardown must not mask the test result.
            }
        }

        foreach (var runtime in _runtimes)
        {
            try
            {
                await runtime.Close();
            }
            catch
            {
                // Ditto.
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

    // ---------------------------------------------------------------- command validations

    [Fact]
    public async Task RejectsAuthenticationWithoutARadiusConnection()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClientRuntimes.OpenClientRuntimeAsync(new ClientCommand
            {
                Auth = new AuthInput.Token("token"),
            }));
        Assert.Equal("Authentication is only supported for experimental Radius connections", error.Message);
    }

    [Fact]
    public async Task RejectsAProviderWithoutAModel()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClientRuntimes.OpenClientRuntimeAsync(new ClientCommand { Provider = "anthropic" }));
        Assert.Equal("Server model provider requires a model", error.Message);
    }

    [Fact]
    public async Task RejectsAModelSelectionWithAnExplicitConnection()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClientRuntimes.OpenClientRuntimeAsync(new ClientCommand
            {
                Connect = new TransportAddress.Unix($"/tmp/{ServerId}.sock"),
                Model = "claude-sonnet-4-5",
            }));
        Assert.Equal("Model selection is only valid when automatically activating a new server", error.Message);
    }

    [Fact]
    public async Task RejectsPluginPackagesForARadiusConnection()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClientRuntimes.OpenClientRuntimeAsync(new ClientCommand
            {
                Connect = new TransportAddress.Radius(ServerId),
                PluginPackages = ["/pkg/a"],
            }));
        Assert.Equal("Plugin package paths can only be configured on a local Unix server", error.Message);
    }

    [Theory]
    [InlineData("/tmp/server.sock")] // the stem is not a server id
    [InlineData("/tmp/socket")] // no .sock suffix at all
    public async Task RejectsAnExplicitPathThatDoesNotNameAServer(string path)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClientRuntimes.OpenClientRuntimeAsync(new ClientCommand
            {
                Connect = new TransportAddress.Unix(path),
            }));
        Assert.Equal("--connect path must end with <uuidv4-server-id>.sock", error.Message);
    }

    [Fact]
    public async Task RejectsAModelSelectionWhenDiscoveryFindsAServer()
    {
        await StartServerAsync(ServerId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClientRuntimes.OpenClientRuntimeAsync(new ClientCommand { Model = "claude-sonnet-4-5" },
                new OpenClientRuntimeOptions { Directory = _root }));
        Assert.Equal("Model selection is only valid when automatically activating a new server", error.Message);
    }

    [Fact]
    public async Task RequiresASingleLocalServerForPluginPackages()
    {
        await StartServerAsync(ServerId);
        await StartServerAsync(SecondServerId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClientRuntimes.OpenClientRuntimeAsync(new ClientCommand { PluginPackages = ["/pkg/a"] },
                new OpenClientRuntimeOptions { Directory = _root }));
        Assert.Equal("Plugin selection requires exactly one local server", error.Message);
    }

    // ---------------------------------------------------------------- routes over a real server

    [Fact]
    public async Task DiscoversTheRunningServerAndActivatesItsServices()
    {
        var running = await StartServerAsync(ServerId);

        var runtime = await TrackAsync(ClientRuntimes.OpenClientRuntimeAsync(
            new ClientCommand(), new OpenClientRuntimeOptions { Directory = _root }));

        var single = Assert.Single(runtime.Servers);
        Assert.IsType<ClientRuntimeRoute.Unix>(single.Route);
        Assert.Equal(ServerId, single.Route.ServerId);
        Assert.Equal(UnixSocketAddress.GetUnixSocketPath(ServerId, _root),
            Assert.IsType<ClientRuntimeRoute.Unix>(single.Route).Path);

        // The built-in facades open against the discovered route: the directory replicates over the
        // wire and a create lands in the durable session directory.
        var activated = await ClientRuntimes.ActivateBuiltinClientServicesAsync(single);

        // The state arrived as a value tree and decoded into its record (the ready gate hydrated it).
        var directory = activated.Directory.State.Value;
        Assert.NotNull(directory);
        Assert.Empty(directory.Sessions);

        var created = await activated.Management.CreateAsync(
            new SessionCreateOptions { Id = "s1" }, Context.Background);
        Assert.Equal(ServerId, created.ServerId);
        Assert.Equal("s1", created.SessionId);
        Assert.True(File.Exists(Path.Combine(running.SessionDir, "s1", "meta.json")));

        // The create refreshed the replicated directory (the publish lands asynchronously).
        await WaitUntilAsync(() => activated.Directory.State.Value?.Sessions.Count == 1);
        var session = Assert.Single(activated.Directory.State.Value!.Sessions);
        Assert.Equal("s1", session.SessionId);

        // prepareSession answers the presentation facet data itself (not a wrapper around it).
        var plugins = await activated.Plugins.PrepareSessionAsync(
            new PrepareSessionRequest("s1", null), Context.Background);
        Assert.NotNull(plugins);
    }

    [Fact]
    public async Task ConnectsToAnExplicitlyGivenPath()
    {
        await StartServerAsync(ServerId);

        var runtime = await TrackAsync(ClientRuntimes.OpenClientRuntimeAsync(
            new ClientCommand
            {
                Connect = new TransportAddress.Unix(
                    UnixSocketAddress.GetUnixSocketPath(ServerId, _root)),
            },
            new OpenClientRuntimeOptions { Directory = _root }));

        var single = Assert.Single(runtime.Servers);
        var unix = Assert.IsType<ClientRuntimeRoute.Unix>(single.Route);
        Assert.Equal(ServerId, unix.ServerId);
        Assert.True(single.Client.Connected);
    }

    [Fact]
    public async Task RechecksADiscoveredServerAfterAVersionMismatch()
    {
        await StartServerAsync(ServerId);

        // The first connect is rejected as a stale generation; the recheck finds the running
        // server (activation connects instead of spawning, since something answers).
        var attempts = 0;
        var runtime = await TrackAsync(ClientRuntimes.OpenClientRuntimeAsync(
            new ClientCommand(),
            new OpenClientRuntimeOptions
            {
                Directory = _root,
                ClientFactory = clientOptions =>
                {
                    attempts += 1;
                    if (attempts == 1)
                    {
                        return Task.FromException<ServerClient>(new ServerError(
                            new ProtocolError("version", "stale server")));
                    }

                    return ServerClient.ConnectAsync(clientOptions);
                },
            }));

        Assert.Equal(1, attempts);
        var single = Assert.Single(runtime.Servers);
        Assert.Equal(ServerId, single.Route.ServerId);
        // The recheck connected through the activation path (its own client factory), so the runtime
        // holds a live client to the running server.
        Assert.True(single.Client.Connected);
    }

    [Fact]
    public async Task PropagatesAVersionMismatchOnAnExplicitConnection()
    {
        await StartServerAsync(ServerId);

        // An explicit --connect is never re-checked: the version error is the caller's to see.
        var error = await Assert.ThrowsAsync<ServerError>(() =>
            ClientRuntimes.OpenClientRuntimeAsync(
                new ClientCommand
                {
                    Connect = new TransportAddress.Unix(
                        UnixSocketAddress.GetUnixSocketPath(ServerId, _root)),
                },
                new OpenClientRuntimeOptions
                {
                    Directory = _root,
                    ClientFactory = _ => Task.FromException<ServerClient>(new ServerError(
                        new ProtocolError("version", "stale server"))),
                }));
        Assert.Equal("version", error.Code);
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        await StartServerAsync(ServerId);
        var runtime = await TrackAsync(ClientRuntimes.OpenClientRuntimeAsync(
            new ClientCommand(), new OpenClientRuntimeOptions { Directory = _root }));

        await runtime.Dispose();
        await runtime.Dispose();

        Assert.False(Assert.Single(runtime.Servers).Client.Connected);
    }

    // ---------------------------------------------------------------- loopback: facades + wrapper

    [Fact]
    public async Task ActivatesTheBuiltinFacadesOverALoopbackTransport()
    {
        var harness = new LoopbackHarness();
        var server = new ClientRuntimeServer(
            new ClientRuntimeRoute.Unix("3f0d1c2a-9b4e-4d1f-8a77-5c6b7d8e9f01", "/tmp/server-1.sock"),
            new ServerClient(new ClientOptions
            {
                ServerId = "3f0d1c2a-9b4e-4d1f-8a77-5c6b7d8e9f01",
                TransportFactory = _ => throw new InvalidOperationException("never connected"),
            }),
            harness.ServerSource,
            harness.SessionSource);

        var activated = await ClientRuntimes.ActivateBuiltinClientServicesAsync(server);

        // The directory state crossed as a value tree and decoded into its record.
        var directory = activated.Directory.State.Value;
        Assert.NotNull(directory);
        Assert.Equal(4L, directory.Revision);
        var summary = Assert.Single(directory.Sessions);
        Assert.Equal("one", summary.SessionId);

        // create round-trips the summary over the wire vocabulary.
        var created = await activated.Management.CreateAsync(
            new SessionCreateOptions { Id = "two" }, Context.Background);
        Assert.Equal("two", created.SessionId);
        Assert.Contains("create:two", harness.ManagementLog);

        // prepareSession answers the facet data; the package paths stay server-side.
        var plugins = await activated.Plugins.PrepareSessionAsync(
            new PrepareSessionRequest("two", ["/pkg/a"]), Context.Background);
        Assert.Equal("prepared", plugins);

        // The models state decodes, and a selection writes its ModelRef.
        var models = activated.Models.State.Value;
        Assert.NotNull(models);
        Assert.Equal(2, models.Catalog.AvailableModels.Count);
        Assert.Equal("one", models.Configuration.Model?.ModelId);
        Assert.Equal(ThinkingLevel.Off, models.Configuration.ThinkingLevel);
        await activated.Models.SelectAsync(new ServicesModelRef("test", "two"), Context.Background);
        Assert.Contains("select:test/two", harness.ModelsLog);
        Assert.Equal(
            [ThinkingLevel.Off, ThinkingLevel.High],
            await activated.Models.GetThinkingLevelsAsync(Context.Background));

        // The agent replies decode into their record variants.
        var prompt = await activated.Agent.PromptAsync(
            new AgentPromptRequest("hello", null), Context.Background);
        var accepted = Assert.IsType<AgentOperationResponse.Accepted>(prompt);
        Assert.Equal("op-1", accepted.OperationId);
        var answer = await activated.Agent.WaitForPromptAsync("op-1", Context.Background);
        Assert.Equal("remote answer", Assert.IsType<AgentPromptResult.Done>(answer).Text);

        // The transcript's durable view decodes with its typed halves and its document trees.
        var view = activated.Transcript.State.Value;
        Assert.NotNull(view);
        Assert.Equal("pi.user", Assert.Single(view.Entries).Kind);
        Assert.Equal("hello", Assert.IsType<MessageContent.Text>(
            Assert.IsType<UserMessage>(Assert.Single(Assert.Single(view.Entries).Model!)).Content).Value);
        Assert.True(view.Docs.ContainsKey("pi.live"));

        // The management wrapper adds the attachment bookkeeping around the remote calls.
        await activated.Management.AttachAsync("one", Context.Background);
        Assert.Contains("attach:one", harness.ManagementLog);
        Assert.Contains("one", harness.SessionSource.Attached);

        await activated.Management.RemoveAsync("other", Context.Background);
        Assert.Contains("remove:other", harness.ManagementLog);
        Assert.Empty(harness.SessionSource.Detached);

        await activated.Management.DetachAsync(Context.Background);
        Assert.Contains("detach", harness.ManagementLog);
        Assert.Contains("detached", harness.SessionSource.Detached);
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

    private async Task<ClientRuntime> TrackAsync(Task<ClientRuntime> open)
    {
        var runtime = await open;
        _clientRuntimes.Add(runtime);
        return runtime;
    }

    /// <summary>Poll a replicated value until it settles (TS's <c>vi.waitFor</c>).</summary>
    private static async Task WaitUntilAsync(Func<bool> settled)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!settled())
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new InvalidOperationException("Timed out waiting for the replicated state");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// The loopback counterpart of the presentation service sources: real chord bindings over one
    /// in-process provider, with the attachment bookkeeping recorded. The TS suite builds the same
    /// shape for its client-TUI tests.
    /// </summary>
    private sealed class LoopbackHarness
    {
        public List<string> ManagementLog { get; } = [];

        public List<string> ModelsLog { get; } = [];

        public LoopbackServerSource ServerSource { get; }

        public LoopbackSessionSource SessionSource { get; }

        public LoopbackHarness()
        {
            var directory = new MutableReplicatedState<Dictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["revision"] = 4L,
                ["sessions"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["serverId"] = "server-1",
                        ["sessionId"] = "one",
                        ["createdAt"] = 1L,
                    },
                },
            });
            var models = new MutableReplicatedState<Dictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["catalog"] = new Dictionary<string, object?>
                {
                    ["revision"] = 1L,
                    ["availableModels"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["provider"] = "test", ["modelId"] = "one", ["name"] = "Model One", ["reasoning"] = false,
                        },
                        new Dictionary<string, object?>
                        {
                            ["provider"] = "test", ["modelId"] = "two", ["name"] = "Model Two", ["reasoning"] = true,
                        },
                    },
                },
                ["configuration"] = new Dictionary<string, object?>
                {
                    ["model"] = new Dictionary<string, object?> { ["provider"] = "test", ["modelId"] = "one" },
                    ["thinkingLevel"] = "off",
                },
                ["refresh"] = new Dictionary<string, object?> { ["status"] = "idle" },
            });
            var view = new ConversationView
            {
                Conversation = new ConversationRecord { Id = ConversationId.From(1) },
                Entries =
                [
                    new EntryRecord
                    {
                        Id = EntryId.From(1),
                        ConversationId = ConversationId.From(1),
                        Kind = "pi.user",
                        Model = [new UserMessage(new MessageContent.Text("hello"), 1)],
                    },
                ],
                Docs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
                {
                    ["pi.live"] = new Dictionary<string, object?> { ["run"] = null },
                },
            };
            var transcript = new MutableReplicatedState<Dictionary<string, object?>>(
                (Dictionary<string, object?>)JsonTrees.ToTree(view)!);

            var serverProvider = new RemoteServiceProvider(
            [
                new RemoteServiceProvider.ProviderDefinition(SessionDirectory.Service.Id),
                new RemoteServiceProvider.ProviderDefinition(SessionManagement.Service.Id),
                new RemoteServiceProvider.ProviderDefinition(PresentationPlugins.Service.Id),
            ]);
            serverProvider.Provide(SessionDirectory.Service.Id, new Dictionary<string, object>
            {
                ["state"] = MutableReplicatedState<Dictionary<string, object?>>.GetInternals(directory)!,
            });
            serverProvider.Provide(SessionManagement.Service.Id, new Dictionary<string, object>
            {
                ["create"] = (Func<object?[], object?>)(args =>
                {
                    var options = ServiceWireValues.Read<SessionCreateOptions>(args[0]);
                    ManagementLog.Add($"create:{options.Id ?? "auto"}");
                    return ServiceWireValues.SessionSummary(
                        new SessionSummary("server-1", options.Id ?? "auto", 2));
                }),
                ["remove"] = (Func<object?[], object?>)(args =>
                {
                    ManagementLog.Add($"remove:{ServiceWireValues.Text(args[0])}");
                    return null;
                }),
                ["attach"] = (Func<object?[], object?>)(args =>
                {
                    ManagementLog.Add($"attach:{ServiceWireValues.Text(args[0])}");
                    return null;
                }),
                ["detach"] = (Func<object?[], object?>)(args =>
                {
                    ManagementLog.Add("detach");
                    return null;
                }),
            });
            serverProvider.Provide(PresentationPlugins.Service.Id, new Dictionary<string, object>
            {
                ["prepareSession"] = (Func<object?[], object?>)(args =>
                {
                    var request = ServiceWireValues.Read<PrepareSessionRequest>(args[0]);
                    ManagementLog.Add($"prepare:{request.SessionId}");
                    return "prepared";
                }),
                ["reload"] = (Func<object?[], object?>)(args => "reloaded"),
            });

            var sessionProvider = new RemoteServiceProvider(
            [
                new RemoteServiceProvider.ProviderDefinition(Models.Service.Id),
                new RemoteServiceProvider.ProviderDefinition(AgentController.Service.Id),
                new RemoteServiceProvider.ProviderDefinition(Transcript.Service.Id),
            ]);
            sessionProvider.Provide(Models.Service.Id, new Dictionary<string, object>
            {
                ["state"] = MutableReplicatedState<Dictionary<string, object?>>.GetInternals(models)!,
                ["cycleThinking"] = (Func<object?[], object?>)(args => null),
                ["getThinkingLevels"] = (Func<object?[], object?>)(args =>
                    ServiceWireValues.ThinkingLevels([ThinkingLevel.Off, ThinkingLevel.High])),
                ["refresh"] = (Func<object?[], object?>)(args => null),
                ["select"] = (Func<object?[], object?>)(args =>
                {
                    var model = ServiceWireValues.Read<ServicesModelRef>(args[0]);
                    ModelsLog.Add($"select:{model.Provider}/{model.ModelId}");
                    return null;
                }),
                ["selectThinking"] = (Func<object?[], object?>)(args =>
                {
                    ModelsLog.Add($"selectThinking:{ServiceWireValues.Text(args[0])}");
                    return null;
                }),
            });
            sessionProvider.Provide(AgentController.Service.Id, new Dictionary<string, object>
            {
                ["prompt"] = (Func<object?[], object?>)(args =>
                {
                    var request = ServiceWireValues.Read<AgentPromptRequest>(args[0]);
                    ManagementLog.Add($"prompt:{request.Message}");
                    return ServiceWireValues.OperationResponse(true, "op-1", null);
                }),
                ["steer"] = (Func<object?[], object?>)(args => ServiceWireValues.QueueResponse(true, "e1", null)),
                ["followUp"] = (Func<object?[], object?>)(args => ServiceWireValues.QueueResponse(true, "e2", null)),
                ["cancelQueued"] = (Func<object?[], object?>)(args =>
                    new Dictionary<string, object?> { ["outcome"] = "cancelled" }),
                ["abort"] = (Func<object?[], object?>)(args => null),
                ["compact"] = (Func<object?[], object?>)(args => ServiceWireValues.OperationResponse(true, "op-2", null)),
                ["waitForPrompt"] = (Func<object?[], object?>)(args =>
                    ServiceWireValues.PromptResult("done", "remote answer", null)),
            });
            sessionProvider.Provide(Transcript.Service.Id, new Dictionary<string, object>
            {
                ["state"] = MutableReplicatedState<Dictionary<string, object?>>.GetInternals(transcript)!,
            });

            ServerSource = new LoopbackServerSource(serverProvider);
            SessionSource = new LoopbackSessionSource(sessionProvider);
        }
    }

    private sealed class LoopbackServerSource(RemoteServiceProvider provider) : IServerServiceSource
    {
        public IReplicatedState<ServerConnectionState> Connection { get; } =
            new MutableReplicatedState<ServerConnectionState>(new ServerConnectionState.Connected("now"));

        public bool AcceptsUnavailableServices => false;

        public Task<IReadOnlyList<ServiceCatalogueEntry>> CatalogueAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(provider.Catalogue);

        public Task<IReadOnlyList<ServiceCatalogueEntry>> CatalogueAsync(Context context)
            => Task.FromResult(provider.Catalogue);

        public IRemoteServices Open(RemoteServicesOptions options)
            => new RemoteServiceBinding(new RemoteServiceBindingOptions
            {
                Services = options.Services,
                Transport = LoopbackServiceTransport.Create(provider),
                Bound = true,
                AssertAccess = options.AssertAccess,
                OnError = options.OnError,
            });

        public Task DisposeAsync(Context context) => Task.CompletedTask;
    }

    private sealed class LoopbackSessionSource(RemoteServiceProvider provider) : ISessionServiceSource
    {
        public List<string> Attached { get; } = [];

        public List<string> Detached { get; } = [];

        public IReplicatedState<SessionAttachmentState> Attachment { get; } =
            new MutableReplicatedState<SessionAttachmentState>(new SessionAttachmentState.Detached());

        public bool AcceptsUnavailableServices => true;

        public Task<IReadOnlyList<ServiceCatalogueEntry>> CatalogueAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(provider.Catalogue);

        public Task<IReadOnlyList<ServiceCatalogueEntry>> CatalogueAsync(Context context)
            => Task.FromResult(provider.Catalogue);

        public IRemoteServices Open(RemoteServicesOptions options)
            => new RemoteServiceBinding(new RemoteServiceBindingOptions
            {
                Services = options.Services,
                Transport = LoopbackServiceTransport.Create(provider),
                Bound = true,
                AssertAccess = options.AssertAccess,
                OnError = options.OnError,
            });

        public Task WhenAttachedAsync(string sessionId, Context context)
        {
            Attached.Add(sessionId);
            ((MutableReplicatedState<SessionAttachmentState>)Attachment).Replace(
                context, new SessionAttachmentState.Attached(sessionId));
            return Task.CompletedTask;
        }

        public Task WhenDetachedAsync(Context context)
        {
            Detached.Add("detached");
            ((MutableReplicatedState<SessionAttachmentState>)Attachment).Replace(
                context, new SessionAttachmentState.Detached());
            return Task.CompletedTask;
        }

        public Task DisposeAsync(Context context) => Task.CompletedTask;
    }
}
