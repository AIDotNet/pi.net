using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Extensions.Mcp;
using Pi.CodingAgent.Utils;
using Pi.Mcp.OAuth;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Pi.Tui;
using Xunit;
// Both namespaces bind these two names; the extension contract is the one the production code uses.
using KeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;
using NewSessionOptions = Pi.CodingAgent.Core.Extensions.NewSessionOptions;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the built-in MCP extension (port of <c>extensions/mcp/index.ts</c> and the
/// non-TUI half of <c>ui.ts</c>, batch P4b-6).
/// </summary>
/// <remarks>
/// The TS test file only covers the pure leaves; the extension closure itself has no TS coverage, so
/// the vectors here come from two sources: the three <c>renderServersSection</c> cases transcribed
/// from <c>mcp-extension.test.ts</c>, and the remaining expectations read off the TS source
/// (<c>formatStatus</c>, <c>describeState</c>, <c>exposureOf</c>, the section intro, the
/// <c>/mcp</c> argument grammar, and the <c>before_agent_start</c> section write).
/// </remarks>
public class McpExtensionTests
{
    // ------------------------------------------------------------------ fixtures

    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "pi-mcp-extension-" + Guid.NewGuid().ToString("N")[..8]);

    private const string StdioConfig = """{"command":"unused"}""";

    /// <summary>TS <c>server(name, description?, exposure?)</c>: a config with a command and optional keys.</summary>
    private static McpServerEntry Entry(string name, string? description = null, string? exposure = null)
    {
        var config = new JsonObject { ["command"] = "x" };
        if (description is not null)
        {
            config["description"] = description;
        }

        if (exposure is not null)
        {
            config["exposure"] = exposure;
        }

        return new McpServerEntry(name, new McpServerConfig(config), "test");
    }

    private static McpServerEntry RawEntry(string name, string configJson) =>
        new(name, new McpServerConfig(JsonNode.Parse(configJson)!.AsObject()), "test");

    private static McpServerListing Listing(string name, string? description = null, string? exposure = null, string? instructions = null) =>
        new(Entry(name, description, exposure), instructions);

    private static McpServer Server(McpServerEntry entry, McpServerConnection? connection = null) =>
        new() { Entry = entry, Connection = connection };

    /// <summary>
    /// A scripted MCP server, as <c>mcp-extension.test.ts</c> builds with
    /// <c>createInMemoryTransportPair</c>: answers by method, can fail one request with an injected
    /// exception, and can drop the connection like a stdio server that exited.
    /// </summary>
    private sealed class FakeMcpServer : TransportEvents
    {
        private readonly Dictionary<string, Func<JsonRpcMessage.Request, JsonNode?>> _handlers =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, Queue<Exception>> _failures = new(StringComparer.Ordinal);

        /// <summary>Never answers <c>initialize</c>, as a server that is slow to start does.</summary>
        public bool HoldInitialize { get; init; }

        /// <summary>Set once the handshake was answered.</summary>
        public bool Connected { get; private set; }

        public void On(string method, Func<JsonRpcMessage.Request, JsonNode?> handler) => _handlers[method] = handler;

        /// <summary>Fails the next request of <paramref name="method"/> with <paramref name="error"/>.</summary>
        public void FailNext(string method, Exception error)
        {
            if (!_failures.TryGetValue(method, out var queue))
            {
                queue = new Queue<Exception>();
                _failures[method] = queue;
            }

            queue.Enqueue(error);
        }

        /// <summary>The transport dropped, as a stdio server that exited does.</summary>
        public void Drop() => EmitClose();

        public override Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            if (message is not JsonRpcMessage.Request request)
            {
                return Task.CompletedTask;
            }

            if (HoldInitialize && request.Method == "initialize")
            {
                return Task.CompletedTask;
            }

            if (_failures.TryGetValue(request.Method, out var queue) && queue.Count > 0)
            {
                throw queue.Dequeue();
            }

            if (_handlers.TryGetValue(request.Method, out var handler))
            {
                if (request.Method == "initialize")
                {
                    Connected = true;
                }

                EmitMessage(new JsonRpcMessage.SuccessResponse(request.Id, handler(request)));
            }
            else
            {
                EmitMessage(new JsonRpcMessage.ErrorResponse(request.Id,
                    new JsonRpcErrorObject(JsonRpcErrorCodes.MethodNotFound, "Method not found")));
            }

            return Task.CompletedTask;
        }

        public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>The answers a healthy server gives, before a test overrides one of them.</summary>
    private static void AnswerDefaults(FakeMcpServer server)
    {
        server.On("initialize", _ => new JsonObject
        {
            ["protocolVersion"] = "2025-11-25",
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject(), ["resources"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "fake", ["version"] = "1.0.0" },
        });
        server.On("tools/list", _ => JsonNode.Parse(
            """{"tools":[{"name":"read_file","description":"read","inputSchema":{"type":"object"}}]}"""));
        server.On("resources/list", _ => JsonNode.Parse("""{"resources":[]}"""));
    }

    /// <summary>Build a connection over <paramref name="server"/>, with the in-memory credential store.</summary>
    private static McpServerConnection Connection(string name, FakeMcpServer server, string config = StdioConfig) =>
        new(new McpServerConnectionOptions
        {
            Entry = RawEntry(name, config),
            Cwd = Root,
            CreateTransport = (_, _, _) => server,
            Credentials = new McpOAuth.McpOAuthCredentialStore(new InMemoryAuthStorageBackend()),
            OnTools = _ => { },
        });

    /// <summary>A healthy connection that finished its handshake, plus the transport behind it.</summary>
    private static async Task<(McpServerConnection Connection, FakeMcpServer Server)> ConnectedAsync(
        string name,
        Action<FakeMcpServer>? configure = null)
    {
        var server = new FakeMcpServer();
        AnswerDefaults(server);
        configure?.Invoke(server);
        var connection = Connection(name, server);
        await connection.GetClientAsync();
        return (connection, server);
    }

    /// <summary>Polls until <paramref name="condition"/> holds, so background work can land.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(5);
        }
    }

    // ------------------------------------------------------------------ runner harness

    private static ExtensionActions DefaultActions() => new()
    {
        SendMessage = (_, _) => { },
        SendUserMessage = (_, _) => { },
        AppendEntry = (_, _) => { },
        SetSessionName = _ => { },
        GetSessionName = () => null,
        SetLabel = (_, _) => { },
        GetActiveTools = () => [],
        GetAllTools = () => [],
        GetSettings = () => new Settings(),
        SetActiveTools = _ => { },
        RefreshTools = () => { },
        GetCommands = () => [],
        SetModel = _ => Task.FromResult(true),
        GetThinkingLevel = () => ThinkingLevel.Off,
        SetThinkingLevel = _ => { },
    };

    private static ExtensionContextActions DefaultContextActions() => new()
    {
        GetModel = () => null,
        GetScopedModels = () => [],
        IsIdle = () => true,
        IsProjectTrusted = () => true,
        GetSignal = () => null,
        Abort = () => { },
        HasPendingMessages = () => false,
        Shutdown = () => { },
        GetContextUsage = () => null,
        Compact = _ => { },
        GetSystemPrompt = () => "system prompt",
    };

    private sealed class StubSessionManager : IReadonlySessionManager
    {
        public string SessionId => "session";

        public string? SessionFile => null;

        public IReadOnlyList<SessionEntry> Entries => [];
    }

    /// <summary>
    /// The loaded extension plus the runner it is bound to. The runner is the only way to reach the
    /// closure's events and commands, so every behavioural test goes through it.
    /// </summary>
    private sealed class Harness
    {
        private Harness(Extension extension, ExtensionRunner runner)
        {
            Extension = extension;
            Runner = runner;
        }

        public Extension Extension { get; }

        public ExtensionRunner Runner { get; }

        public static async Task<Harness> CreateAsync(McpExtensionOptions? options = null, string? cwd = null)
        {
            var root = cwd ?? Root;
            var runtime = ExtensionLoader.CreateExtensionRuntime();
            var extension = await ExtensionLoader.LoadExtensionFromFactory(
                McpExtension.CreateMcpExtension(options ?? TestOptions()),
                root,
                EventBusController.CreateEventBus(),
                runtime);
            var modelRuntime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
            {
                Credentials = new InMemoryCredentialStore(),
                ModelsPathDisabled = true,
                ModelsStore = new InMemoryCodingAgentModelsStore(),
                RefreshOnCreate = false,
                Env = _ => null,
                AuthContext = new StubAuthContext(_ => null),
            });
            var runner = new ExtensionRunner(
                [extension],
                runtime,
                root,
                new StubSessionManager(),
                new ModelRegistry(modelRuntime));
            runner.BindCore(DefaultActions(), DefaultContextActions());
            return new Harness(extension, runner);
        }

        /// <summary>Options that never touch the user's <c>mcp-auth.json</c> or <c>mcp.log</c>.</summary>
        public static McpExtensionOptions TestOptions(LoadedMcpConfig? config = null) => new()
        {
            LoadConfig = _ => config ?? new LoadedMcpConfig([], []),
            Credentials = new McpOAuth.McpOAuthCredentialStore(new InMemoryAuthStorageBackend()),
            LogPath = Path.Combine(Root, "mcp.log"),
            OpenUrl = _ => { },
        };

        public Task StartAsync() => Runner.EmitAsync(new ExtensionEvent.SessionStart("startup"));

        /// <summary>Run <c>before_agent_start</c> and hand back the options the handler left behind.</summary>
        public async Task<NormalizedBuildSystemPromptOptions> PromptAsync(BuildSystemPromptOptions? options = null)
        {
            var result = await Runner.EmitBeforeAgentStartAsync(
                "hi",
                null,
                options ?? new BuildSystemPromptOptions { Cwd = Root });
            return result.SystemPromptOptions;
        }
    }

    // ==================================================================
    // mcp_servers section (vectors transcribed from mcp-extension.test.ts)
    // ==================================================================

    [Fact]
    public void RenderServersSection_ListsServersWithReachAndFirstDescriptionLine()
    {
        var section = McpExtension.RenderServersSection(
        [
            Listing("docs", "Docs search.\nMore."),
            Listing("later", null, "deferred"),
            Listing("direct", "Declared.", "direct"),
            Listing("plain", null, null, "From instructions."),
        ]);

        Assert.NotNull(section);
        var lines = section.Split("\n");
        Assert.Equal(
            "MCP servers whose tools are not declared to you."
            + " Call the tools of `codemode` servers from codemode scripts."
            + " Load the tools of `tool_search` servers with `tool_search`.",
            lines[0]);
        Assert.Equal(
            [
                "- mcp__docs (codemode): Docs search.",
                "- mcp__later (tool_search)",
                "- mcp__plain (codemode): From instructions.",
            ],
            lines[1..]);

        // A server whose tools are declared to the model is not listed at all.
        Assert.Null(McpExtension.RenderServersSection([Listing("direct", "Declared.", "direct")]));
    }

    [Fact]
    public void RenderServersSection_ShortensDescriptionsToFitTheSizeLimit()
    {
        var servers = Enumerable.Range(0, 40).Select(index => Listing($"server{index}", new string('x', 400))).ToList();

        var section = McpExtension.RenderServersSection(servers) ?? "";

        Assert.True(section.Length <= McpExtension.MaxServersSectionChars);
        Assert.Equal(41, section.Split("\n").Length);
        Assert.Contains("- mcp__server39 (codemode): x", section, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderServersSection_LeavesOutTheLastServersWhenNamesAloneDoNotFit()
    {
        var servers = Enumerable.Range(0, 200)
            .Select(index => Listing($"server-with-a-long-name-{index}", "desc"))
            .ToList();

        var section = McpExtension.RenderServersSection(servers) ?? "";

        Assert.True(section.Length <= McpExtension.MaxServersSectionChars);
        var lines = section.Split("\n");
        Assert.Matches(@"^- … \d+ more servers; find their tools with searchTools\(\)$", lines[^1]);
        var omitted = int.Parse(Regex.Match(lines[^1], @"(\d+) more").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(200, lines.Length - 2 + omitted);
    }

    // ==================================================================
    // Leaf helpers
    // ==================================================================

    [Fact]
    public void ServersSectionIntro_ExplainsOnlyTheReachesInUse()
    {
        var baseIntro = "MCP servers whose tools are not declared to you.";
        Assert.Equal(baseIntro, McpExtension.ServersSectionIntro(new HashSet<string>(StringComparer.Ordinal)));
        Assert.Equal(
            baseIntro + " Call the tools of `codemode` servers from codemode scripts.",
            McpExtension.ServersSectionIntro(new HashSet<string>(StringComparer.Ordinal) { "codemode" }));
        Assert.Equal(
            baseIntro + " Load the tools of `tool_search` servers with `tool_search`.",
            McpExtension.ServersSectionIntro(new HashSet<string>(StringComparer.Ordinal) { "tool_search" }));
        Assert.Equal(
            baseIntro
            + " Call the tools of `codemode` servers from codemode scripts."
            + " Load the tools of `tool_search` servers with `tool_search`.",
            McpExtension.ServersSectionIntro(new HashSet<string>(StringComparer.Ordinal) { "codemode", "tool_search" }));
    }

    [Theory]
    [InlineData("abc", 5, "abc")]
    [InlineData("abcdef", 6, "abcdef")]
    [InlineData("abcdef", 4, "abc…")]
    [InlineData("ab   cdef", 5, "ab…")]
    [InlineData("abcdef", 1, "")]
    [InlineData("abcdef", 0, "")]
    public void Truncate_KeepsTheHeadAndDropsTrailingSpaceBeforeTheEllipsis(string text, int max, string expected) =>
        Assert.Equal(expected, McpExtension.Truncate(text, max));

    [Theory]
    [InlineData("a\nb", "a")]
    [InlineData("a", "a")]
    [InlineData("", "")]
    [InlineData("\n", "")]
    public void FirstLine_StopsAtTheFirstNewline(string text, string expected) =>
        Assert.Equal(expected, McpExtension.FirstLine(text));

    [Fact]
    public void ServerSummary_PrefersTheConfiguredDescriptionAndFallsBackToInstructions()
    {
        Assert.Equal("Docs search.", McpExtension.ServerSummary(Listing("a", "Docs search.\nMore.")));
        Assert.Equal("trimmed", McpExtension.ServerSummary(Listing("a", "  trimmed  ")));
        Assert.Equal("From instructions.", McpExtension.ServerSummary(Listing("a", null, null, "From instructions.")));
        // A whitespace-only description trims to nothing, so the instructions win (TS `||`).
        Assert.Equal("Instr", McpExtension.ServerSummary(Listing("a", "   ", null, "Instr")));
        Assert.Equal("", McpExtension.ServerSummary(Listing("a")));
        Assert.Equal("", McpExtension.ServerSummary(Listing("a", null, null, "   ")));
    }

    [Fact]
    public void ExposureHelpers_ReadTheConfigAndItsPerToolOverrides()
    {
        Assert.Equal("codemode", McpExtension.ExposureOf(Entry("a")));
        Assert.Equal("deferred", McpExtension.ExposureOf(Entry("a", null, "deferred")));

        var mixed = RawEntry("a", """{"command":"x","exposure":"deferred","toolExposure":{"a":"direct","b":"hidden"}}""");
        Assert.Equal(
            ["deferred", "direct", "hidden"],
            McpExtension.ConfiguredExposures(mixed).OrderBy(value => value, StringComparer.Ordinal));

        // The default exposure is codemode: reachable from scripts, not declared.
        Assert.False(McpExtension.HasDirectTools(Entry("a")));
        Assert.True(McpExtension.HasIndirectTools(Entry("a")));

        Assert.True(McpExtension.HasDirectTools(Entry("a", null, "direct")));
        Assert.False(McpExtension.HasIndirectTools(Entry("a", null, "direct")));

        Assert.False(McpExtension.HasDirectTools(Entry("a", null, "hidden")));
        Assert.False(McpExtension.HasIndirectTools(Entry("a", null, "hidden")));

        // One direct tool is enough to make the first prompt wait, even under a codemode server.
        var overridden = RawEntry("a", """{"command":"x","toolExposure":{"a":"direct"}}""");
        Assert.True(McpExtension.HasDirectTools(overridden));
        Assert.True(McpExtension.HasIndirectTools(overridden));
    }

    [Fact]
    public void IsEnabled_OnlyAnExplicitFalseDisablesAServer()
    {
        Assert.True(McpExtension.IsEnabled(Server(Entry("a"))));
        Assert.False(McpExtension.IsEnabled(Server(RawEntry("a", """{"command":"x","enabled":false}"""))));
        Assert.True(McpExtension.IsEnabled(Listing("a")));
        Assert.False(McpExtension.IsEnabled(new McpServerListing(
            RawEntry("a", """{"command":"x","enabled":false}"""))));
    }

    [Fact]
    public void ScriptNeedsServer_MatchesTheCodemodeApiAndTheServerNamespace()
    {
        // Any of the four codemode entry points may name the server in another form.
        Assert.True(McpExtension.ScriptNeedsServer("const r = searchTools('x')", "docs"));
        Assert.True(McpExtension.ScriptNeedsServer("describeNamespace('mcp__docs')", "docs"));
        Assert.True(McpExtension.ScriptNeedsServer("describeTool('t')", "docs"));
        Assert.True(McpExtension.ScriptNeedsServer("ALL_TOOLS", "docs"));

        Assert.True(McpExtension.ScriptNeedsServer("mcp__docs.read_file()", "docs"));
        Assert.False(McpExtension.ScriptNeedsServer("mcp__docs.read_file()", "other"));
        Assert.False(McpExtension.ScriptNeedsServer("const x = 1", "docs"));

        // Namespaces replace "-" with "_", so a hyphenated name is found through its namespace.
        Assert.True(McpExtension.ScriptNeedsServer("mcp__my_server.tool()", "my-server"));
    }

    [Theory]
    [InlineData("""{"url":"https://example.com/mcp"}""", "https://example.com/mcp")]
    [InlineData("""{"command":"cmd","args":["a","b"]}""", "cmd a b")]
    [InlineData("""{"command":"cmd"}""", "cmd")]
    public void DescribeTransport_ReadsTheUrlOrTheCommandLine(string config, string expected) =>
        Assert.Equal(expected, McpExtension.DescribeTransport(RawEntry("a", config)));

    [Theory]
    [InlineData("codemode", "called from codemode scripts, which find them with searchTools()")]
    [InlineData("deferred", "not declared until tool_search loads them, then called directly; no codemode needed")]
    [InlineData("direct", "declared to the model like built-in tools")]
    [InlineData("hidden", "hidden")]
    [InlineData("weird", "weird")]
    public void ExposureDescription_UsesTheManagerTextOrFallsBackToTheExposure(string exposure, string expected) =>
        Assert.Equal(expected, McpExtension.ExposureDescription(exposure));

    [Theory]
    [InlineData(McpServerState.Connecting, "connecting")]
    [InlineData(McpServerState.Connected, "connected")]
    [InlineData(McpServerState.Disconnected, "disconnected")]
    [InlineData(McpServerState.NeedsAuth, "needs-auth")]
    [InlineData(McpServerState.Failed, "failed")]
    [InlineData(McpServerState.Closed, "closed")]
    public void McpServerStates_Name_MatchesTheTypeScriptLiterals(McpServerState state, string expected) =>
        Assert.Equal(expected, McpServerStates.Name(state));

    // ==================================================================
    // formatStatus
    // ==================================================================

    [Fact]
    public void FormatStatus_EmptyStateNamesTheConfigFiles()
    {
        var expected = $"No MCP servers configured. Add them to "
            + $"{NodePath.Resolve(Config.GetAgentDir(), "mcp.json")} or .pi/mcp.json.";
        Assert.Equal(expected, McpExtension.FormatStatus([], [], []));
    }

    [Fact]
    public void FormatStatus_ListsEachServerThenTheConfigErrorsAndOverrides()
    {
        var lines = McpExtension.FormatStatus(
            [Server(Entry("fresh")), Server(RawEntry("parked", """{"command":"x","enabled":false}"""))],
            ["bad: needs either \"command\" or \"url\""],
            ["shared: overrides a registered server"]).Split("\n");

        Assert.Equal(
            [
                "fresh: starting (codemode)",
                "parked: disabled (codemode)",
                "config error: bad: needs either \"command\" or \"url\"",
                "overridden: shared: overrides a registered server",
            ],
            lines);
    }

    [Fact]
    public async Task FormatStatus_NeedsAuthSaysHowToSignIn()
    {
        var server = new FakeMcpServer();
        AnswerDefaults(server);
        server.FailNext("initialize", new McpAuthRequiredError());
        var connection = Connection("oauth", server, """{"url":"https://example.com/mcp"}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.GetClientAsync());

        Assert.Equal(McpServerState.NeedsAuth, connection.State);
        Assert.Equal(
            "oauth: needs sign-in, run /mcp login oauth (codemode)",
            McpExtension.FormatStatus(
                [Server(RawEntry("oauth", """{"url":"https://example.com/mcp"}"""), connection)], [], []));
    }

    [Fact]
    public async Task FormatStatus_ConnectedReportsTheToolCountAndDisconnectedSaysItReconnects()
    {
        var (connected, _) = await ConnectedAsync("docs");
        Assert.Equal(
            "docs: connected, 1 tools (codemode)",
            McpExtension.FormatStatus([Server(RawEntry("docs", StdioConfig), connected)], [], []));

        var (dropped, transport) = await ConnectedAsync("docs");
        transport.Drop();
        await WaitUntilAsync(() => dropped.State == McpServerState.Disconnected, "the drop to land");
        Assert.Equal(
            "docs: disconnected, reconnects on next call (codemode)\n    Connection closed",
            McpExtension.FormatStatus([Server(RawEntry("docs", StdioConfig), dropped)], [], []));
    }

    // ==================================================================
    // registration
    // ==================================================================

    [Fact]
    public async Task Factory_RegistersTheMcpCommandAndTheSixEventHandlers()
    {
        var harness = await Harness.CreateAsync();

        var command = Assert.Single(harness.Extension.Commands).Value;
        Assert.Equal("mcp", command.Name);
        Assert.Equal(
            "Manage MCP servers: sign in, reconnect, enable or disable, and change exposure",
            command.Description);

        Assert.Equal(
            [
                ExtensionEventNames.BeforeAgentStart,
                ExtensionEventNames.McpServersChange,
                ExtensionEventNames.SessionShutdown,
                ExtensionEventNames.SessionStart,
                ExtensionEventNames.ToolCall,
                ExtensionEventNames.TurnStart,
            ],
            harness.Extension.Handlers.Keys.OrderBy(name => name, StringComparer.Ordinal));

        // The renderer for `mcp__<server>__<tool>` calls of servers that never connected.
        Assert.Single(harness.Extension.ToolRenderers);
    }

    // ==================================================================
    // `/mcp` (no TUI)
    // ==================================================================

    private static RegisteredCommand Command(Harness harness) =>
        Assert.Single(harness.Extension.Commands).Value;

    [Fact]
    public async Task McpCommand_WithoutArgumentsNotifiesTheStatus()
    {
        var harness = await Harness.CreateAsync();
        var ui = new RecordingUi();
        harness.Runner.SetUIContext(ui, ExtensionMode.Print);
        var command = Command(harness);

        await command.Handler("", harness.Runner.CreateCommandContext());

        var notification = Assert.Single(ui.Notifications);
        Assert.Equal(NotifyType.Info, notification.Type);
        Assert.StartsWith("No MCP servers configured. Add them to ", notification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpCommand_RejectsUnknownNamesAndExtraArguments()
    {
        var harness = await Harness.CreateAsync();
        var ui = new RecordingUi();
        harness.Runner.SetUIContext(ui, ExtensionMode.Print);
        var command = Command(harness);
        var ctx = harness.Runner.CreateCommandContext();

        await command.Handler("login ghost", ctx);
        Assert.Equal(
            ("No MCP server named \"ghost\".", NotifyType.Error),
            ui.Notifications[^1]);

        await command.Handler("login", ctx);
        Assert.Equal(
            ("No enabled MCP server uses OAuth. Only HTTP servers without an Authorization header do.",
                NotifyType.Info),
            ui.Notifications[^1]);

        await command.Handler("login a b", ctx);
        Assert.Equal(
            ("Usage: /mcp, /mcp login [server], /mcp logout [server], /mcp reconnect [server]",
                NotifyType.Warning),
            ui.Notifications[^1]);

        await command.Handler("wat", ctx);
        Assert.Equal(
            ("Usage: /mcp, /mcp login [server], /mcp logout [server], /mcp reconnect [server]",
                NotifyType.Warning),
            ui.Notifications[^1]);
    }

    [Fact]
    public async Task McpCommand_CompletionsFollowTheArgumentGrammar()
    {
        var harness = await Harness.CreateAsync(Harness.TestOptions(new LoadedMcpConfig(
            [
                RawEntry("oauth", """{"url":"https://example.com/mcp"}"""),
                RawEntry("stdio", StdioConfig),
                RawEntry("parked", """{"command":"unused","enabled":false}"""),
            ],
            [])) with { CreateTransport = (_, _, _) => Answered() });
        var command = Command(harness);
        var complete = command.GetArgumentCompletions!;

        // A bare prefix offers the three subcommands, filtered by what was typed.
        var all = await complete("");
        Assert.NotNull(all);
        Assert.Equal(["login ", "logout ", "reconnect "], all.Select(item => item.Value));
        Assert.Equal(["login", "logout", "reconnect"], all.Select(item => item.Label));
        var filtered = await complete("log");
        Assert.NotNull(filtered);
        // "log" prefixes both "login" and "logout".
        Assert.Equal(["login ", "logout "], filtered.Select(item => item.Value));

        // Too many words, or an unknown subcommand, complete to nothing.
        Assert.Null(await complete("login a b"));
        Assert.Null(await complete("bogus x"));

        await harness.StartAsync();
        // Only servers that connected can be reconnected; only OAuth servers can be signed in or out.
        var reconnects = await WaitForAsync(
            () => complete("reconnect "),
            items => items?.Count == 2,
            "both servers to connect");
        Assert.NotNull(reconnects);
        Assert.Equal(["reconnect oauth", "reconnect stdio"], reconnects.Select(item => item.Value));
        // The description is `describeState`, i.e. the state without the server name.
        Assert.Equal(
            ["connected · 1 tool", "connected · 1 tool"],
            reconnects.Select(item => item.Description));

        var logins = await WaitForAsync(
            () => complete("login "),
            items => items?.Count == 1,
            "the OAuth server to be listed");
        Assert.NotNull(logins);
        Assert.Equal(["login oauth"], logins.Select(item => item.Value));
    }

    // ==================================================================
    // `/mcp` (TUI seam)
    // ==================================================================

    [Fact]
    public async Task McpCommand_ShowsTheManagerThroughTheSeamWhenTheModeIsTui()
    {
        ScriptedManagerUi.Reset();
        var harness = await Harness.CreateAsync(Harness.TestOptions() with
        {
            ShowManager = async (_, manage) =>
            {
                await manage(new ScriptedManagerUi());
            },
        });
        var ui = new RecordingUi();
        harness.Runner.SetUIContext(ui, ExtensionMode.Tui);
        var command = Command(harness);

        await command.Handler("", harness.Runner.CreateCommandContext());

        // The manager was driven, so the status text was not: the seam replaced the TUI view.
        Assert.Empty(ui.Notifications);
        Assert.Equal(1, ScriptedManagerUi.Menus);
        var menu = ScriptedManagerUi.LastMenu!;
        Assert.Equal("MCP servers", menu.Title);
        Assert.Equal("manage", menu.ConfirmLabel);
        Assert.Equal("close", menu.CancelLabel);
        Assert.Empty(menu.Items);
        Assert.StartsWith("No MCP servers configured. Add them to ", menu.Empty, StringComparison.Ordinal);
    }

    // ==================================================================
    // before_agent_start writes the section
    // ==================================================================

    [Fact]
    public async Task BeforeAgentStart_AddsTheServersSectionForIndirectServers()
    {
        var harness = await Harness.CreateAsync(Harness.TestOptions(new LoadedMcpConfig(
            [RawEntry("docs", """{"command":"unused","description":"Docs search."}""")],
            [])) with { CreateTransport = (_, _, _) => Answered() });

        await harness.StartAsync();
        var prompt = await harness.PromptAsync();

        Assert.True(prompt.Sections.ContainsKey("mcp_servers"));
        var lines = prompt.Sections["mcp_servers"].Split("\n");
        Assert.Equal(
            "MCP servers whose tools are not declared to you."
            + " Call the tools of `codemode` servers from codemode scripts.",
            lines[0]);
        Assert.Equal("- mcp__docs (codemode): Docs search.", lines[1]);
    }

    [Fact]
    public async Task BeforeAgentStart_LeavesTheSectionOutWhenEveryServerDeclaresItsTools()
    {
        var harness = await Harness.CreateAsync(Harness.TestOptions(new LoadedMcpConfig(
            [RawEntry("api", """{"command":"unused","exposure":"direct"}""")],
            [])) with { CreateTransport = (_, _, _) => Answered() });

        await harness.StartAsync();
        var prompt = await harness.PromptAsync();

        Assert.False(prompt.Sections.ContainsKey("mcp_servers"));
    }

    [Fact]
    public async Task BeforeAgentStart_RemovesASectionAnEarlierPromptLeftBehind()
    {
        var harness = await Harness.CreateAsync(Harness.TestOptions() with
        {
            CreateTransport = (_, _, _) => Answered(),
        });

        await harness.StartAsync();
        var sections = new System.Collections.Generic.OrderedDictionary<string, string> { ["mcp_servers"] = "stale" };
        var prompt = await harness.PromptAsync(new BuildSystemPromptOptions { Cwd = Root, Sections = sections });

        Assert.False(prompt.Sections.ContainsKey("mcp_servers"));
    }

    // ==================================================================
    // the first prompt waits for direct servers
    // ==================================================================

    [Fact]
    public async Task TheFirstPromptWaitsForAServerWithDirectTools()
    {
        var server = new FakeMcpServer();
        AnswerDefaults(server);
        var harness = await Harness.CreateAsync(Harness.TestOptions(new LoadedMcpConfig(
            [RawEntry("api", """{"command":"unused","exposure":"direct"}""")],
            [])) with { CreateTransport = (_, _, _) => server });

        await harness.StartAsync();
        await harness.PromptAsync();

        // The handler awaited the connection, so its handshake and discovery are already done.
        Assert.True(server.Connected);
    }

    [Fact]
    public async Task TheFirstPromptGivesUpOnASlowDirectServerAndWarns()
    {
        var server = new FakeMcpServer { HoldInitialize = true };
        var harness = await Harness.CreateAsync(Harness.TestOptions(new LoadedMcpConfig(
            [RawEntry("api", """{"command":"unused","exposure":"direct"}""")],
            [])) with { CreateTransport = (_, _, _) => server, StartupWaitMs = 50 });
        var ui = new RecordingUi();
        harness.Runner.SetUIContext(ui, ExtensionMode.Print);

        await harness.StartAsync();
        await harness.PromptAsync();

        Assert.False(server.Connected);
        Assert.Contains(
            ui.Notifications,
            notification => notification.Message
                == "MCP servers are still connecting; their tools become available once connected.");
    }

    // ==================================================================
    // helpers
    // ==================================================================

    private static FakeMcpServer Answered()
    {
        var server = new FakeMcpServer();
        AnswerDefaults(server);
        return server;
    }

    /// <summary>Poll an async probe until it reports ready, failing instead of hanging.</summary>
    private static async Task<T> WaitForAsync<T>(Func<Task<T>> probe, Func<T, bool> ready, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var value = await probe();
            if (ready(value))
            {
                return value;
            }

            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// A UI context that records notifications and answers dialogs. Only the members the MCP
    /// extension reaches carry behaviour; the rest are inert, matching the no-op context.
    /// </summary>
    private sealed class RecordingUi : IExtensionUIContext
    {
        public List<(string Message, string? Type)> Notifications { get; } = [];

        public Task<string?> Select(string title, IReadOnlyList<string> options, ExtensionUIDialogOptions? opts = null) =>
            Task.FromResult<string?>(options.Count > 0 ? options[0] : null);

        public Task<bool> Confirm(string title, string message, ExtensionUIDialogOptions? opts = null) =>
            Task.FromResult(true);

        public Task<string?> Input(string title, string? placeholder = null, ExtensionUIDialogOptions? opts = null) =>
            Task.FromResult<string?>("input");

        public void Notify(string message, string? type = null) => Notifications.Add((message, type));

        public Action OnTerminalInput(TerminalInputHandler handler) => () => { };

        public void SetStatus(string key, string? text)
        {
        }

        public void SetWorkingMessage(string? message = null)
        {
        }

        public void SetWorkingVisible(bool visible)
        {
        }

        public void SetWorkingIndicator(WorkingIndicatorOptions? options = null)
        {
        }

        public void SetHiddenThinkingLabel(string? label = null)
        {
        }

        public void SetWidget(string key, IReadOnlyList<string>? content, ExtensionWidgetOptions? options = null)
        {
        }

        public void SetWidget(string key, Func<TuiBase, Theme, IComponent>? factory, ExtensionWidgetOptions? options = null)
        {
        }

        public void SetFooter(Func<TuiBase, Theme, IReadonlyFooterDataProvider, IComponent>? factory)
        {
        }

        public void SetHeader(Func<TuiBase, Theme, IComponent>? factory)
        {
        }

        public void SetTitle(string title)
        {
        }

        public Task<T> Custom<T>(
            Func<TuiBase, Theme, KeybindingsManager, Action<T>, IComponent> factory,
            ExtensionCustomOptions? options = null) => Task.FromResult(default(T)!);

        public Task<T> CustomAsync<T>(
            Func<TuiBase, Theme, KeybindingsManager, Action<T>, Task<IComponent>> factory,
            ExtensionCustomOptions? options = null) => Task.FromResult(default(T)!);

        public void PasteToEditor(string text)
        {
        }

        public void SetEditorText(string text)
        {
        }

        public string GetEditorText() => "";

        public Task<string?> Editor(string title, string? prefill = null) => Task.FromResult<string?>(null);

        public void AddAutocompleteProvider(AutocompleteProviderFactory factory)
        {
        }

        public void SetEditorComponent(EditorFactory? factory)
        {
        }

        public EditorFactory? GetEditorComponent() => null;

        public Theme Theme => ThemeRuntime.Current;

        public IReadOnlyList<ThemeInfo> GetAllThemes() => [];

        public Theme? GetTheme(string name) => null;

        public SetThemeResult SetTheme(string name) => new(true, null);

        public SetThemeResult SetTheme(Theme theme) => new(true, null);

        public bool GetToolsExpanded() => false;

        public void SetToolsExpanded(bool expanded)
        {
        }
    }

    /// <summary>
    /// The <c>IMcpUi</c> the manager view runs on. It records the first menu and answers every prompt
    /// with "cancel", so <c>manage</c> returns after the first screen.
    /// </summary>
    private sealed class ScriptedManagerUi : IMcpUi
    {
        public static int Menus { get; private set; }

        public static McpMenu? LastMenu { get; private set; }

        public static void Reset()
        {
            Menus = 0;
            LastMenu = null;
        }

        public Task<string?> MenuAsync(Func<McpMenu> build, Func<Action, Action>? subscribe = null)
        {
            Menus++;
            LastMenu ??= build();
            return Task.FromResult<string?>(null);
        }

        public void Status(string title, string message)
        {
        }

        public Task<string?> RedirectUrlAsync(string title, string authorizationUrl, CancellationToken signal) =>
            Task.FromResult<string?>(null);
    }
}
