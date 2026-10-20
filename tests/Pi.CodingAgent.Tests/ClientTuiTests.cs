// ============================================================================
// Experimental client TUI — the C# mirror of TS experimental-client-tui.test.ts (4e-3c-3)
// ============================================================================
//
// The TS suite drives `ExperimentalClientTui.create` against loopback service sources backed by a
// real in-memory durable Harness whose answers come from the faux provider. Most tests keep that
// shape: one loopback provider per source (server-scoped and session-scoped), a faux-driven root
// conversation, and a `FakeTui` so they do not schedule render timers. A focused fullscreen test
// also mounts the client on a real `TuiAltScreen` backed by `StringTerminal`.
//
// Two adaptations the port needs:
// - The transcript state crosses the loopback transport as a chord value tree (the client adapter
//   decodes one — C365/C375), so the fixture republishes the durable view's tree on every commit.
// - The TS test reloads a real JavaScript bundle; the C# plugin host refuses to execute JavaScript
//   (C335), so the reload data carries an empty artifact list — the counters and the status line
//   still observe the whole reload round trip.

using System.Text.Json.Nodes;
using AiFaux = Pi.Ai.Providers.Faux;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental;
using Pi.CodingAgent.Experimental.Plugins;
using Pi.CodingAgent.Experimental.Services;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
// The experimental layer declares its own Models/SlashCommands handles; alias the ones the
// fixture wires so they never collide with Pi.Ai.Models.Models.
using ExperimentalModels = Pi.CodingAgent.Experimental.Services.Models;
using ServicesModelRef = Pi.CodingAgent.Experimental.Services.ModelRef;
using DurableModelRef = Pi.Durable.Harness.ModelRef;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <see cref="ExperimentalClientTui"/> end to end: the session selection of
/// <c>prepareClientSession</c>, the facet host over the loopback sources, the prompt / reload /
/// reconnect lifecycle, and the two built-in selectors — the port of the TS suite of the same name.
/// </summary>
public sealed class ClientTuiTests : IAsyncDisposable
{
    private const string ServerId = "00000000-0000-4000-8000-000000000001";
    private const string RadiusServerId = "00000000-0000-4000-8000-000000000009";

    private readonly bool? _previousChalk = Chalk.EnabledOverride;
    private readonly Pi.Tui.KeybindingsManager _previousGlobalKeybindings = GlobalKeybindings.Get();

    public ClientTuiTests()
    {
        Chalk.EnabledOverride = true;
        ThemeRuntime.ResetCachesForTests();
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
        GlobalKeybindings.Set(new Pi.Tui.KeybindingsManager(AppKeybindings.Definitions));
    }

    public async ValueTask DisposeAsync()
    {
        GlobalKeybindings.Set(_previousGlobalKeybindings);
        Chalk.EnabledOverride = _previousChalk;
        ThemeRuntime.ResetCachesForTests();
        await Task.CompletedTask;
    }

    [Theory]
    // TS: the three command shapes whose startup selection differs.
    [InlineData("new")]
    [InlineData("continued")]
    [InlineData("plugin-selected")]
    public async Task OpensTheSessionDirectly(string kind)
    {
        ClientCommand command = kind switch
        {
            "new" => new ClientCommand(),
            "continued" => new ClientCommand { Continue = true },
            _ => new ClientCommand { PluginPackages = ["./example-plugin"] },
        };
        // TS's table: only the continued shape reuses the newest existing session; the other two
        // create one (a plugin-selected run has no session of its own to continue).
        var expectedSessionId = kind == "continued" ? "one" : "two";
        var expectedCreates = kind == "continued" ? 0 : 1;

        var fixture = new ClientTuiFixture();
        await using var _disposal = fixture;
        var ui = new FakeTui();
        var finished = 0;
        var component = await ExperimentalClientTui.CreateAsync(
            new ExperimentalClientTui.CreateExperimentalClientTuiOptions
            {
                Command = command,
                Ui = ui,
                Servers = [fixture.Server],
                RequestRender = () => { },
                Finish = () => finished += 1,
            });

        try
        {
            Assert.Equal(expectedCreates,
                fixture.ManagementLog.Count(entry => entry.StartsWith("create:", StringComparison.Ordinal)));
            Assert.Contains($"prepare:{expectedSessionId}", fixture.ManagementLog);
            // The plugin-selected shape resolves its package paths (TS: `resolve(packagePath)`).
            Assert.Equal(kind == "plugin-selected" ? 1 : 0, fixture.PluginPackageCount);
            Assert.Equal(new SessionAttachmentState.Attached(expectedSessionId), fixture.Attachment);
            Assert.Empty(fixture.ModelsLog);

            var render = Render(component);
            Assert.Contains($"Server: {ServerId}", render, StringComparison.Ordinal);
            Assert.Contains($"Session: {expectedSessionId}", render, StringComparison.Ordinal);
            // The footer reads the conversation's `pi.agent` document.
            Assert.Contains("faux/faux-1", render, StringComparison.Ordinal);
            Assert.DoesNotContain("Experimental Sessions", render, StringComparison.Ordinal);
            Assert.DoesNotContain("Experimental Models", render, StringComparison.Ordinal);

            // Startup selection is the only behavior specific to continue and plugin-selected Sessions.
            if (kind != "new") return;

            // ---- a prompt: queued, answered, and rendered ----
            component.HandleInput("hello");
            component.HandleInput("\r");
            await WaitUntilAsync(() => fixture.Prompts.Count == 1);
            Assert.Equal(["hello"], fixture.Prompts);
            await WaitUntilAsync(() => Render(component).Contains("Working...", StringComparison.Ordinal));
            fixture.ReleasePrompt();
            await WaitUntilAsync(() => Render(component).Contains("remote answer", StringComparison.Ordinal));
            Assert.Contains("hello", Render(component), StringComparison.Ordinal);
            Assert.DoesNotContain("Working...", Render(component), StringComparison.Ordinal);

            // ---- /reload: both plugin halves reload and the status reports it ----
            component.HandleInput("/reload");
            component.HandleInput("\u001b");
            component.HandleInput("\r");
            await WaitUntilAsync(() => fixture.PresentationReloads == 1 && fixture.SessionReloads == 1);
            await WaitUntilAsync(() => Render(component).Contains("Reloaded plugins.", StringComparison.Ordinal));

            // ---- a radius drop: busy, then a reconnect reopens the lane ----
            fixture.DropAttachment();
            fixture.DropConnection();
            await WaitUntilAsync(() => Render(component).Contains("retrying", StringComparison.Ordinal));
            component.HandleInput("\u0003");
            Assert.Equal(1, finished);
            component.HandleInput("\u0004");
            Assert.Equal(2, finished);
            finished = 0;
            fixture.Reconnect();
            await WaitUntilAsync(() => !Render(component).Contains("Reattaching", StringComparison.Ordinal));

            // ---- the two built-in selectors ----
            component.HandleInput("/model");
            component.HandleInput("\u001b");
            component.HandleInput("\r");
            await WaitUntilAsync(() => Render(component).Contains("Select model:", StringComparison.Ordinal));
            component.HandleInput("\u001b[B");
            component.HandleInput("\r");
            await WaitUntilAsync(() => fixture.ModelsLog.Contains("select:test/two"));
            await WaitUntilAsync(() => !Render(component).Contains("Select model:", StringComparison.Ordinal));

            component.HandleInput("/thinking");
            component.HandleInput("\u001b");
            component.HandleInput("\r");
            await WaitUntilAsync(() => Render(component).Contains("Select thinking level:", StringComparison.Ordinal));
            component.HandleInput("\u001b[B");
            component.HandleInput("\r");
            await WaitUntilAsync(() => fixture.ModelsLog.Contains("selectThinking:high"));

            component.HandleInput("\u0003");
            await WaitUntilAsync(() => finished == 1);

            // ---- a closed component stops reacting ----
            await component.CloseAsync();
            var settled = Render(component);
            fixture.PublishDirectoryChange();
            fixture.DropAttachment();
            fixture.RefreshModels();
            Assert.Equal(settled, Render(component));
        }
        finally
        {
            await component.CloseAsync();
        }
    }

    [Fact]
    public async Task RendersAndScrollsTheClientTuiInTheFullscreenViewport()
    {
        var fixture = new ClientTuiFixture();
        await using var _disposal = fixture;
        var terminal = new StringTerminal(80, 12);
        var ui = new TuiAltScreen(terminal, showHardwareCursor: false, new TuiAltScreenOptions
        {
            Mouse = false,
            WheelScrollLines = 1,
        });
        ExperimentalClientTui? component = null;

        try
        {
            component = await ExperimentalClientTui.CreateAsync(
                new ExperimentalClientTui.CreateExperimentalClientTuiOptions
                {
                    Command = new ClientCommand(),
                    Ui = ui,
                    Servers = [fixture.Server],
                    RequestRender = () => ui.RequestRender(),
                    Finish = () => { },
                });
            ui.AddChild(component);
            ui.SetLayoutRoot(component.LayoutRoot);
            ui.SetFocus(component);
            ui.Start();
            ui.RenderNow(force: true);

            var initialFrame = ui.GetScreenLines();
            Assert.Equal(terminal.Rows, initialFrame.Length);
            Assert.Contains($"Server: {ServerId}",
                AnsiText.StripAnsi(string.Join("\n", initialFrame)), StringComparison.Ordinal);

            var prompt = string.Join(' ', Enumerable.Repeat("scroll-content", 400));
            component.HandleInput(prompt);
            component.HandleInput("\r");
            await WaitUntilAsync(() => fixture.Prompts.Count == 1);
            ui.RenderNow(force: true);

            var endPosition = ui.ViewportTop;
            Assert.True(endPosition > 0, "The long transcript should extend beyond the fullscreen viewport.");

            terminal.SendInput("\x1b[5~");
            ui.RenderNow(force: true);
            Assert.True(ui.ViewportTop < endPosition, "Page Up should reveal earlier transcript content.");

            terminal.SendInput("\x1b[6~");
            ui.RenderNow(force: true);
            Assert.True(ui.IsFollowingOutput, "Page Down should restore follow-end mode.");

            fixture.ReleasePrompt();
            await WaitUntilAsync(() => Render(component).Contains("remote answer", StringComparison.Ordinal));
        }
        finally
        {
            fixture.ReleasePrompt();
            if (component is not null) await component.CloseAsync();
            if (terminal.Started) ui.Stop();
        }
    }

    [Fact]
    public async Task RejectsAnAmbiguousSessionSelection()
    {
        var fixture = new ClientTuiFixture();
        await using var _disposal = fixture;
        var second = fixture.SecondServer();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExperimentalClientTui.CreateAsync(
            new ExperimentalClientTui.CreateExperimentalClientTuiOptions
            {
                Command = new ClientCommand { SessionId = "one" },
                Ui = new FakeTui(),
                Servers = [fixture.Server, second],
                RequestRender = () => { },
                Finish = () => { },
            }));

        Assert.Equal("Session one is available from more than one server", error.Message);
    }

    [Fact]
    public async Task RequiresExactlyOneServerToCreateASession()
    {
        var fixture = new ClientTuiFixture();
        await using var _disposal = fixture;
        var second = fixture.SecondServer();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExperimentalClientTui.CreateAsync(
            new ExperimentalClientTui.CreateExperimentalClientTuiOptions
            {
                Command = new ClientCommand(),
                Ui = new FakeTui(),
                Servers = [fixture.Server, second],
                RequestRender = () => { },
                Finish = () => { },
            }));

        Assert.Equal("Starting a Session requires exactly one server", error.Message);
    }

    [Fact]
    public async Task RejectsAMissingSessionSelectionOnARadiusServer()
    {
        var fixture = new ClientTuiFixture();
        await using var _disposal = fixture;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ExperimentalClientTui.CreateAsync(
            new ExperimentalClientTui.CreateExperimentalClientTuiOptions
            {
                // A radius --connect names the remote server, so a missing session cannot be created.
                Command = new ClientCommand
                {
                    SessionId = "missing",
                    Connect = new TransportAddress.Radius("radius.example.com"),
                },
                Ui = new FakeTui(),
                Servers = [fixture.Server],
                RequestRender = () => { },
                Finish = () => { },
            }));

        Assert.Equal("Remote server does not contain Session missing", error.Message);
    }

    [Fact]
    public void ChatViewRendersTheTranscriptTheLiveDocumentsDescribe()
    {
        // The chat view is driven by the replicated view alone: entries, the streaming partial,
        // the tool round, the inbox and the status line all come from the value trees.
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
                    Model = [new UserMessage(new MessageContent.Text("hello there"), 1)],
                },
                new EntryRecord
                {
                    Id = EntryId.From(2),
                    ConversationId = ConversationId.From(1),
                    Kind = "pi.assistant",
                    Model = [new AssistantMessage(
                        [new ToolCallContent("call-1", "bash", new Dictionary<string, object?> { ["command"] = "ls" })],
                        StopReason.ToolUse)],
                },
                new EntryRecord
                {
                    Id = EntryId.From(3),
                    ConversationId = ConversationId.From(1),
                    Kind = "pi.tool-result",
                    Model = [new ToolResultMessage("call-1", "bash", [new TextContent("a.txt")])],
                },
                new EntryRecord
                {
                    Id = EntryId.From(4),
                    ConversationId = ConversationId.From(1),
                    Kind = "pi.compaction",
                    Model = [new UserMessage(new MessageContent.Text("The train route is confirmed."), 4)],
                },
            ],
            Docs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["pi.live"] = new Dictionary<string, object?>
                {
                    ["run"] = new Dictionary<string, object?> { ["taskId"] = 7L },
                    ["tools"] = new List<object?>(),
                },
                ["pi.inbox"] = new Dictionary<string, object?>
                {
                    ["items"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = 1L,
                            ["mode"] = "steer",
                            ["content"] = "queued  follow-up",
                        },
                        new Dictionary<string, object?>
                        {
                            ["id"] = 2L,
                            ["mode"] = "write",
                            ["entry"] = new Dictionary<string, object?> { ["kind"] = "pi.reset" },
                        },
                    },
                },
            },
        };

        var chat = new ExperimentalChatView(new FakeTui(), "/tmp");
        chat.Apply(view);
        var transcript = string.Join("\n", chat.Transcript.Render(80));
        var pending = string.Join("\n", chat.PendingMessages.Render(80));

        Assert.Contains("hello there", transcript, StringComparison.Ordinal);
        Assert.Contains("ls", transcript, StringComparison.Ordinal);
        Assert.Contains("a.txt", transcript, StringComparison.Ordinal);
        Assert.Contains("Earlier context summarized", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("The train route is confirmed", transcript, StringComparison.Ordinal);
        // TS collapses every whitespace run (leading and trailing included) to one space.
        Assert.Contains("[steer] queued follow-up", pending, StringComparison.Ordinal);
        Assert.Contains("[write] <pi.reset>", pending, StringComparison.Ordinal);

        chat.SetExpanded(true);
        var expandedTranscript = string.Join("\n", chat.Transcript.Render(80));
        Assert.Contains("The train route is confirmed", expandedTranscript, StringComparison.Ordinal);

        // The status line owns a spinner timer; disposing stops it (TS's dispose()).
        chat.Dispose();
    }

    private static string Render(ExperimentalClientTui component)
        => string.Join("\n", component.Render(80));

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
    /// The loopback counterpart of the TS fixture: two remote service providers over one in-process
    /// transport, a faux-driven durable root conversation behind the agent controller, and the
    /// replicated connection/attachment states the reconnect path observes.
    /// </summary>
    private sealed class ClientTuiFixture : IAsyncDisposable
    {
        private readonly HarnessImpl _harness;
        private readonly IConversation _root;
        private readonly MutableReplicatedState<Dictionary<string, object?>> _directory;
        private readonly MutableReplicatedState<Dictionary<string, object?>> _models;
        private readonly MutableReplicatedState<Dictionary<string, object?>> _transcript;
        private readonly MutableReplicatedState<SessionAttachmentState> _attachment;
        private readonly MutableReplicatedState<ServerConnectionState> _connection;
        private readonly TaskCompletionSource _promptGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ClientTuiFixture()
        {
            // A faux provider whose single response captures the user message, then waits on the
            // gate: the prompt stays "Working..." until the test releases it (TS's promptFinished).
            var faux = AiFaux.CreateProvider();
            faux.SetResponses(
            [
                new AiFaux.FactoryStep(async (context, _, _, _) =>
                {
                    var last = context.Messages.LastOrDefault(message => message is UserMessage);
                    if (last is UserMessage user)
                    {
                        Prompts.Add(string.Join("", user.Content.ToBlocks().OfType<TextContent>().Select(block => block.Text)));
                    }

                    await _promptGate.Task;
                    return AiFaux.ScriptedMessage("remote answer");
                }),
            ]);
            var models = new Pi.Ai.Models.Models();
            models.SetProvider(faux.Provider);
            _harness = Harness.OpenAsync(new MemoryStorage(), new HarnessOptions
            {
                Models = models,
                Registry = Registry.CreateRegistry(),
                SettingsSource = () => new HarnessSettings
                {
                    Extensions = [],
                    Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
                },
            }, Context.Background).GetAwaiter().GetResult();
            _root = _harness.RootAsync(
                Context.Background, new AgentChange { Model = new DurableModelRef("faux", "faux-1") })
                .GetAwaiter().GetResult();

            // The durable view is republished as the value tree the wire carries.
            var view = _root.ViewStateAsync(Context.Background).GetAwaiter().GetResult();
            _transcript = new MutableReplicatedState<Dictionary<string, object?>>(Tree(view.Value));
            view.Subscribe((value, _, _) =>
            {
                _transcript.Replace(Context.Background, Tree(value));
                return (Task?)null;
            });

            _directory = new MutableReplicatedState<Dictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["revision"] = 1L,
                ["sessions"] = new List<object?> { Summary("one", 1) },
            });
            _models = new MutableReplicatedState<Dictionary<string, object?>>(new Dictionary<string, object?>
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
            _attachment = new MutableReplicatedState<SessionAttachmentState>(
                new SessionAttachmentState.Detached());
            _connection = new MutableReplicatedState<ServerConnectionState>(
                new ServerConnectionState.Connected("now"));

            var serverProvider = new RemoteServiceProvider(
            [
                new RemoteServiceProvider.ProviderDefinition(SessionDirectory.Service.Id),
                new RemoteServiceProvider.ProviderDefinition(SessionManagement.Service.Id),
                new RemoteServiceProvider.ProviderDefinition(PresentationPlugins.Service.Id),
            ]);
            serverProvider.Provide(SessionDirectory.Service.Id, new Dictionary<string, object>
            {
                ["state"] = MutableReplicatedState<Dictionary<string, object?>>.GetInternals(_directory)!,
            });
            serverProvider.Provide(PresentationPlugins.Service.Id, new Dictionary<string, object>
            {
                ["prepareSession"] = (Func<object?[], object?>)(args =>
                {
                    var request = ServiceWireValues.Read<PrepareSessionRequest>(args[0]);
                    ManagementLog.Add($"prepare:{request.SessionId}");
                    PluginPackageCount = request.PackagePaths?.Count ?? 0;
                    // An empty artifact list: the C# plugin host cannot execute the TS bundle (C335).
                    return PluginBundles.CreatePresentationFacetData([]);
                }),
                ["reload"] = (Func<object?[], object?>)(args =>
                {
                    PresentationReloads += 1;
                    return PluginBundles.CreatePresentationFacetData([]);
                }),
            });
            serverProvider.Provide(SessionManagement.Service.Id, new Dictionary<string, object>
            {
                ["create"] = (Func<object?[], object?>)(args =>
                {
                    var options = ServiceWireValues.Read<SessionCreateOptions>(args[0]);
                    // TS's mock answers with a fixed summary regardless of the requested id.
                    var created = new SessionSummary(ServerId, options.Id ?? "two", 2);
                    ManagementLog.Add($"create:{options.Id ?? "auto"}");
                    _directory.Replace(Context.Background, new Dictionary<string, object?>
                    {
                        ["revision"] = 2L,
                        ["sessions"] = new List<object?> { Summary("one", 1), Summary("two", 2) },
                    });
                    return ServiceWireValues.SessionSummary(created);
                }),
                ["remove"] = (Func<object?[], object?>)(args => null),
                ["attach"] = (Func<object?[], object?>)(args =>
                {
                    ManagementLog.Add($"attach:{ServiceWireValues.Text(args[0])}");
                    _attachment.Replace(Context.Background,
                        new SessionAttachmentState.Attaching(ServiceWireValues.Text(args[0])));
                    return null;
                }),
                ["detach"] = (Func<object?[], object?>)(args => null),
            });

            var sessionProvider = new RemoteServiceProvider(
            [
                new RemoteServiceProvider.ProviderDefinition(ExperimentalModels.Service.Id),
                new RemoteServiceProvider.ProviderDefinition(AgentController.Service.Id),
                new RemoteServiceProvider.ProviderDefinition(SessionPlugins.Service.Id),
                new RemoteServiceProvider.ProviderDefinition(Transcript.Service.Id),
            ]);
            sessionProvider.Provide(ExperimentalModels.Service.Id, new Dictionary<string, object>
            {
                ["state"] = MutableReplicatedState<Dictionary<string, object?>>.GetInternals(_models)!,
                ["cycleThinking"] = (Func<object?[], object?>)(args => null),
                ["getThinkingLevels"] = (Func<object?[], object?>)(args =>
                    ServiceWireValues.ThinkingLevels([ThinkingLevel.Off, ThinkingLevel.High])),
                ["refresh"] = (Func<object?[], object?>)(args => null),
                ["select"] = (Func<object?[], object?>)(args =>
                {
                    var model = ServiceWireValues.Read<ServicesModelRef>(args[0]);
                    ModelsLog.Add($"select:{model.Provider}/{model.ModelId}");
                    _models.Replace(Context.Background, WithConfiguration(model, "off"));
                    return null;
                }),
                ["selectThinking"] = (Func<object?[], object?>)(args =>
                {
                    var level = ServiceWireValues.Text(args[0]);
                    ModelsLog.Add($"selectThinking:{level}");
                    _models.Replace(Context.Background, WithConfiguration(null, level));
                    return null;
                }),
            });
            sessionProvider.Provide(AgentController.Service.Id,
                (IReadOnlyDictionary<string, object>)AgentControllerProvider.Create(_harness, _root));
            sessionProvider.Provide(SessionPlugins.Service.Id, new Dictionary<string, object>
            {
                ["reload"] = (Func<object?[], object?>)(args =>
                {
                    SessionReloads += 1;
                    return null;
                }),
            });
            sessionProvider.Provide(Transcript.Service.Id, new Dictionary<string, object>
            {
                ["state"] = MutableReplicatedState<Dictionary<string, object?>>.GetInternals(_transcript)!,
            });

            Server = new ClientTuiServer(
                ServerId,
                // TS's fixture marks the loopback server as radius: the bridge facet only watches
                // the connection/attachment states for a radius server, and the reconnect
                // lifecycle below observes exactly those publications.
                Radius: true,
                new LoopbackServerSource(serverProvider, _connection),
                new LoopbackSessionSource(sessionProvider, _attachment));
        }

        public List<string> ManagementLog { get; } = [];

        public List<string> ModelsLog { get; } = [];

        public List<string> Prompts { get; } = [];

        public int PresentationReloads { get; private set; }

        public int SessionReloads { get; private set; }

        public int PluginPackageCount { get; private set; }

        public ClientTuiServer Server { get; }

        public SessionAttachmentState Attachment => _attachment.Value;

        /// <summary>A second server offering the same session id, for the ambiguity guard.</summary>
        public ClientTuiServer SecondServer()
        {
            var directory = new MutableReplicatedState<Dictionary<string, object?>>(
                new Dictionary<string, object?>
                {
                    ["revision"] = 1L,
                    ["sessions"] = new List<object?> { Summary("one", 1) },
                });
            var provider = new RemoteServiceProvider(
            [
                new RemoteServiceProvider.ProviderDefinition(SessionDirectory.Service.Id),
                new RemoteServiceProvider.ProviderDefinition(SessionManagement.Service.Id),
                new RemoteServiceProvider.ProviderDefinition(PresentationPlugins.Service.Id),
            ]);
            provider.Provide(SessionDirectory.Service.Id, new Dictionary<string, object>
            {
                ["state"] = MutableReplicatedState<Dictionary<string, object?>>.GetInternals(directory)!,
            });
            provider.Provide(PresentationPlugins.Service.Id, new Dictionary<string, object>
            {
                ["prepareSession"] = (Func<object?[], object?>)(args => new JsonObject()),
                ["reload"] = (Func<object?[], object?>)(args => new JsonObject()),
            });
            provider.Provide(SessionManagement.Service.Id, new Dictionary<string, object>
            {
                ["create"] = (Func<object?[], object?>)(args =>
                    ServiceWireValues.SessionSummary(new SessionSummary(RadiusServerId, "two", 2))),
                ["remove"] = (Func<object?[], object?>)(args => null),
                ["attach"] = (Func<object?[], object?>)(args => null),
                ["detach"] = (Func<object?[], object?>)(args => null),
            });
            return new ClientTuiServer(
                RadiusServerId,
                Radius: true,
                new LoopbackServerSource(provider, _connection),
                new LoopbackSessionSource(new RemoteServiceProvider(
                [
                    new RemoteServiceProvider.ProviderDefinition(ExperimentalModels.Service.Id),
                    new RemoteServiceProvider.ProviderDefinition(AgentController.Service.Id),
                    new RemoteServiceProvider.ProviderDefinition(SessionPlugins.Service.Id),
                    new RemoteServiceProvider.ProviderDefinition(Transcript.Service.Id),
                ]), _attachment));
        }

        /// <summary>TS <c>finishPrompt()</c>: let the held response complete.</summary>
        public void ReleasePrompt() => _promptGate.TrySetResult();

        /// <summary>TS <c>publishReplacement(attachment, { status: "detached" })</c>.</summary>
        public void DropAttachment()
            => _attachment.Replace(Context.Background, new SessionAttachmentState.Detached());

        /// <summary>TS <c>publishReplacement(connectionState, { status: "disconnected", … })</c>.</summary>
        public void DropConnection()
            => _connection.Replace(Context.Background,
                new ServerConnectionState.Disconnected("later", "network lost", null));

        /// <summary>TS the reconnect pair: connected, then the session attached again.</summary>
        public void Reconnect()
        {
            _connection.Replace(Context.Background, new ServerConnectionState.Connecting(1));
            _connection.Replace(Context.Background, new ServerConnectionState.Connected("reconnected"));
            _attachment.Replace(Context.Background, new SessionAttachmentState.Attached("two"));
        }

        /// <summary>TS the post-close directory change.</summary>
        public void PublishDirectoryChange()
            => _directory.Replace(Context.Background, new Dictionary<string, object?>
            {
                ["revision"] = 3L,
                ["sessions"] = new List<object?>(),
            });

        /// <summary>TS the post-close models refresh.</summary>
        public void RefreshModels()
            => _models.Replace(Context.Background, new Dictionary<string, object?>(_models.Value)
            {
                ["refresh"] = new Dictionary<string, object?> { ["status"] = "refreshing" },
            });

        public async ValueTask DisposeAsync()
        {
            await _harness.CloseAsync(Context.Background);
        }

        private static Dictionary<string, object?> Summary(string sessionId, double createdAt)
            => new(StringComparer.Ordinal)
            {
                ["serverId"] = ServerId,
                ["sessionId"] = sessionId,
                ["createdAt"] = createdAt,
            };

        private static Dictionary<string, object?> Tree(ConversationView? view)
            => (Dictionary<string, object?>)JsonTrees.ToTree(view)!;

        private Dictionary<string, object?> WithConfiguration(ServicesModelRef? model, string thinkingLevel)
        {
            var next = new Dictionary<string, object?>(_models.Value);
            next["configuration"] = new Dictionary<string, object?>
            {
                ["model"] = model is null
                    ? null
                    : new Dictionary<string, object?> { ["provider"] = model.Provider, ["modelId"] = model.ModelId },
                ["thinkingLevel"] = thinkingLevel,
            };
            return next;
        }
    }

    /// <summary>The server-scoped source over one loopback provider (TS's serverServices).</summary>
    private sealed class LoopbackServerSource(
        RemoteServiceProvider provider, MutableReplicatedState<ServerConnectionState> connection)
        : IServerServiceSource
    {
        public IReplicatedState<ServerConnectionState> Connection => connection;

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

    /// <summary>The session-scoped source over one loopback provider (TS's sessionServices).</summary>
    private sealed class LoopbackSessionSource(
        RemoteServiceProvider provider, MutableReplicatedState<SessionAttachmentState> attachment)
        : ISessionServiceSource
    {
        public IReplicatedState<SessionAttachmentState> Attachment => attachment;

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
            attachment.Replace(context, new SessionAttachmentState.Attached(sessionId));
            return Task.CompletedTask;
        }

        public Task WhenDetachedAsync(Context context)
        {
            attachment.Replace(context, new SessionAttachmentState.Detached());
            return Task.CompletedTask;
        }

        public Task DisposeAsync(Context context) => Task.CompletedTask;
    }
}
