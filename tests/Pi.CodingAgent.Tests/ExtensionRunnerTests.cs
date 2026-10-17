using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

// The extension contract lives in Pi.CodingAgent.Core.Extensions and binds several names that also exist
// in Pi.CodingAgent.Core (imported above). Inside that namespace the enclosing Pi.CodingAgent.Core wins,
// so these aliases reproduce the binding the production code actually gets.
using KeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;
using NewSessionOptions = Pi.CodingAgent.Core.Extensions.NewSessionOptions;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the extension runner (port of TS <c>core/extensions/runner.ts</c>,
/// batch 4e-3e-9g).
/// </summary>
/// <remarks>
/// The TS module is the only place where the extension contract meets real behaviour: which handler
/// wins a conflict, what a handler sees after an earlier handler patched the same event, which
/// failures are swallowed into <c>emitError</c> and which propagate, and when a captured context
/// goes stale. Each expectation below is transcribed from the TS source.
/// </remarks>
public sealed class ExtensionRunnerTests
{
    // ------------------------------------------------------------------ shortcuts

    [Fact]
    public async Task GetShortcuts_ReservedBuiltInConflict_SkipsWithWarning()
    {
        // app.interrupt is in RESERVED_KEYBINDINGS_FOR_EXTENSION_CONFLICTS, so the extension loses.
        var extension = NewExtension("<inline>");
        extension.Shortcuts["ctrl+c"] = Shortcut("<inline>");

        var runner = await CreateRunnerAsync([extension]);
        var shortcuts = runner.GetShortcuts(new KeybindingsConfig { ["app.interrupt"] = "ctrl+c" });

        Assert.Empty(shortcuts);
        var diagnostic = Assert.Single(runner.GetShortcutDiagnostics());
        Assert.Equal(ResourceDiagnosticType.Warning, diagnostic.Type);
        Assert.Equal("<inline>", diagnostic.Path);
        Assert.Contains("conflicts with built-in shortcut. Skipping.", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetShortcuts_NonReservedBuiltInConflict_KeepsExtensionAndWarns()
    {
        // "app.tools.somethingElse" is not reserved, so the extension wins and only a warning is added.
        var extension = NewExtension("<inline>");
        extension.Shortcuts["ctrl+t"] = Shortcut("<inline>");

        var runner = await CreateRunnerAsync([extension]);
        var shortcuts = runner.GetShortcuts(new KeybindingsConfig { ["app.notReserved"] = "ctrl+t" });

        Assert.True(shortcuts.ContainsKey("ctrl+t"));
        var diagnostic = Assert.Single(runner.GetShortcutDiagnostics());
        Assert.Contains("is built-in shortcut for app.notReserved", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetShortcuts_ReservedWins_RegardlessOfIterationOrder()
    {
        // Two actions claim the same key; the reserved one must win whichever comes first, otherwise
        // an extension could shadow a reserved shortcut by registering after the reserved action.
        var extension = NewExtension("<inline>");
        extension.Shortcuts["ctrl+c"] = Shortcut("<inline>");

        var runner = await CreateRunnerAsync([extension]);
        var resolved = new KeybindingsConfig
        {
            ["app.notReserved"] = "ctrl+c",
            ["app.interrupt"] = "ctrl+c",
        };

        Assert.Empty(runner.GetShortcuts(resolved));
    }

    [Fact]
    public async Task GetShortcuts_ExtensionConflict_LastWinsAndWarns()
    {
        var first = NewExtension("first.ts");
        first.Shortcuts["ctrl+g"] = Shortcut("first.ts");
        var second = NewExtension("second.ts");
        second.Shortcuts["CTRL+G"] = Shortcut("second.ts");

        var runner = await CreateRunnerAsync([first, second]);
        var shortcuts = runner.GetShortcuts(new KeybindingsConfig());

        // Key ids are lowercased, so both land on the same slot and the later extension wins.
        var shortcut = Assert.Single(shortcuts);
        Assert.Equal("ctrl+g", shortcut.Key);
        Assert.Equal("second.ts", shortcut.Value.ExtensionPath);
        Assert.Contains("registered by both first.ts and second.ts", Assert.Single(runner.GetShortcutDiagnostics()).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ commands

    [Fact]
    public async Task GetRegisteredCommands_UniqueNamesKeepPlainInvocationName()
    {
        var extension = NewExtension("<inline>");
        extension.Commands["alpha"] = Command("alpha");
        extension.Commands["beta"] = Command("beta");

        var runner = await CreateRunnerAsync([extension]);
        var commands = runner.GetRegisteredCommands();

        Assert.Equal(["alpha", "beta"], commands.Select(command => command.InvocationName));
        Assert.NotNull(runner.GetCommand("beta"));
        Assert.Null(runner.GetCommand("beta:1"));
    }

    [Fact]
    public async Task GetRegisteredCommands_DuplicateNamesGetOccurrenceSuffix()
    {
        var first = NewExtension("first.ts");
        first.Commands["dup"] = Command("dup");
        var second = NewExtension("second.ts");
        second.Commands["dup"] = Command("dup");

        var runner = await CreateRunnerAsync([first, second]);

        Assert.Equal(["dup:1", "dup:2"], runner.GetRegisteredCommands().Select(command => command.InvocationName));
    }

    [Fact]
    public async Task GetRegisteredCommands_SuffixSkipsAlreadyTakenName()
    {
        // "dup:2" is claimed by the second "dup"; a third registration of a command literally named
        // "dup:2" must be bumped past it rather than colliding.
        var extension = NewExtension("<inline>");
        extension.Commands["dup"] = Command("dup");
        extension.Commands["dup2"] = Command("dup");
        extension.Commands["dup:2"] = Command("dup:2");

        var runner = await CreateRunnerAsync([extension]);
        var names = runner.GetRegisteredCommands().Select(command => command.InvocationName).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("dup:1", names, StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ registries

    [Fact]
    public async Task GetFlags_FirstRegistrationPerNameWins()
    {
        var first = NewExtension("first.ts");
        first.Flags["verbose"] = Flag("verbose", "first.ts");
        var second = NewExtension("second.ts");
        second.Flags["verbose"] = Flag("verbose", "second.ts");
        second.Flags["other"] = Flag("other", "second.ts");

        var runner = await CreateRunnerAsync([first, second]);
        var flags = runner.GetFlags();

        Assert.Equal(2, flags.Count);
        Assert.Equal("first.ts", flags["verbose"].ExtensionPath);
    }

    [Fact]
    public async Task FlagValues_RoundTripThroughRuntime()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var runner = await CreateRunnerAsync([], runtime);

        runner.SetFlagValue("count", 3);
        runner.SetFlagValue("name", "pi");

        var values = runner.GetFlagValues();
        Assert.Equal(3, values["count"]);
        Assert.Equal("pi", values["name"]);

        // The returned map is detached (TS: `new Map(this.runtime.flagValues)`), so a snapshot taken
        // before a later runtime write must not see it.
        var snapshot = runner.GetFlagValues();
        runtime.FlagValues["count"] = 99;
        Assert.Equal(3, snapshot["count"]);
    }

    [Fact]
    public async Task GetAllRegisteredTools_FirstRegistrationPerNameWins()
    {
        var first = NewExtension("first.ts");
        first.Tools["read"] = RegisteredToolFor("read", "first.ts");
        var second = NewExtension("second.ts");
        second.Tools["read"] = RegisteredToolFor("read", "second.ts");
        second.Tools["write"] = RegisteredToolFor("write", "second.ts");

        var runner = await CreateRunnerAsync([first, second]);

        Assert.Equal(2, runner.GetAllRegisteredTools().Count);
        Assert.Equal("first.ts", runner.GetToolDefinition("read")!.Description);
        Assert.Null(runner.GetToolDefinition("missing"));
    }

    [Fact]
    public async Task ResolveToolRenderers_ExtensionResolversRunInLoadOrderThenBase()
    {
        var calls = new List<string>();
        var first = NewExtension("first.ts");
        first.ToolRenderers.Add((toolName, next) =>
        {
            calls.Add($"first:{toolName}");
            return next();
        });
        var second = NewExtension("second.ts");
        second.ToolRenderers.Add((toolName, next) =>
        {
            calls.Add($"second:{toolName}");
            return new ToolRenderers { RenderShell = "shell" };
        });

        var runner = await CreateRunnerAsync([first, second]);
        var resolved = runner.ResolveToolRenderers("bash", () =>
        {
            calls.Add("base");
            return null;
        });

        Assert.Equal("shell", resolved!.RenderShell);
        Assert.Equal(["first:bash", "second:bash"], calls);
    }

    [Fact]
    public async Task RenderersAndTransformers_ResolveByRegistration()
    {
        var transformer = new MarkdownTransformer((markdown, _) => markdown.ToUpperInvariant());
        var extension = NewExtension("<inline>");
        extension.MarkdownTransformer = transformer;
        extension.MessageRenderers["note"] = new MessageRenderer((_, _, _) => null);
        extension.EntryRenderers["entry"] = new EntryRenderer((_, _, _) => null);

        var runner = await CreateRunnerAsync([extension]);

        Assert.Single(runner.GetMarkdownTransformers());
        Assert.NotNull(runner.GetMessageRenderer("note"));
        Assert.Null(runner.GetMessageRenderer("missing"));
        Assert.NotNull(runner.GetEntryRenderer("entry"));
        Assert.Null(runner.GetEntryRenderer("missing"));
    }

    // ------------------------------------------------------------------ contexts

    [Fact]
    public async Task CreateContext_ReflectsBoundActions()
    {
        var runner = await CreateRunnerAsync([], cwd: "/work");
        runner.BindCore(DefaultActions(), DefaultContextActions());

        var ctx = runner.CreateContext();

        Assert.Equal("/work", ctx.Cwd);
        Assert.Equal(ExtensionMode.Print, ctx.Mode);
        Assert.False(ctx.HasUI);
        // The live runtime model, by reference — same type ModelRuntime.GetModel returns.
        Assert.Same(BoundModel, ctx.Model);
        Assert.True(ctx.IsIdle());
        Assert.True(ctx.IsProjectTrusted());
        Assert.Equal(ThinkingLevel.Off, ctx.ThinkingLevel);
        Assert.Equal("system prompt", ctx.GetSystemPrompt());
    }

    [Fact]
    public async Task CreateContext_AfterInvalidate_ThrowsStaleMessage()
    {
        var runner = await CreateRunnerAsync([]);
        runner.BindCore(DefaultActions(), DefaultContextActions());
        var ctx = runner.CreateContext();

        runner.Invalidate();

        var thrown = Assert.Throws<InvalidOperationException>(() => _ = ctx.Cwd);
        Assert.Equal(ExtensionRunner.StaleMessage, thrown.Message);

        // Idempotent: a second message never replaces the first.
        runner.Invalidate("other");
        Assert.Equal(ExtensionRunner.StaleMessage, Assert.Throws<InvalidOperationException>(() => ctx.IsIdle()).Message);
    }

    [Fact]
    public async Task CreateContext_CustomStaleMessageIsUsed()
    {
        var runner = await CreateRunnerAsync([]);
        runner.BindCore(DefaultActions(), DefaultContextActions());
        var ctx = runner.CreateContext();

        runner.Invalidate("custom");

        Assert.Equal("custom", Assert.Throws<InvalidOperationException>(() => _ = ctx.Cwd).Message);
    }

    [Fact]
    public async Task CreateToolContext_WithoutExecuteTool_ReturnsNestedCallError()
    {
        var runner = await CreateRunnerAsync([]);
        runner.BindCore(DefaultActions(), DefaultContextActions());

        var ctx = runner.CreateToolContext("call-1", null);
        var outcome = await ctx.ExecuteTool("read", new JsonObject());

        Assert.True(outcome.IsError);
        Assert.Equal("call-1/0", outcome.ToolCall.Id);
        Assert.Equal("read", outcome.ToolCall.Name);
        Assert.Equal("Nested tool calls are not available in this context", ((TextContent)outcome.Result.Content[0]).Text);
    }

    [Fact]
    public async Task CreateToolContext_ExecuteTool_DefaultsToCallerSignal()
    {
        using var callerSignal = new CancellationTokenSource();
        ExecuteToolOptions? captured = null;
        var actions = DefaultActions();
        var contextActions = DefaultContextActions() with
        {
            ExecuteTool = (_, _, _, options) =>
            {
                captured = options;
                return Task.FromResult(new AgentToolCallOutcome(
                    new ToolCallContent("id", "read", null),
                    new AgentToolResult([]),
                    IsError: false));
            },
            GetCallableTools = () => [Tool("read")],
        };

        var runner = await CreateRunnerAsync([]);
        runner.BindCore(actions, contextActions);

        var ctx = runner.CreateToolContext("call-1", callerSignal.Token);
        Assert.Single(ctx.Tools);
        await ctx.ExecuteTool("read", null);

        Assert.Equal(callerSignal.Token, captured!.Signal);
    }

    [Fact]
    public async Task CreateCommandContext_WithoutBindings_ReturnsInertDefaults()
    {
        var runner = await CreateRunnerAsync([]);
        runner.BindCore(DefaultActions(), DefaultContextActions());

        var ctx = runner.CreateCommandContext();

        Assert.False(await ctx.NewSession());
        Assert.False(await ctx.Fork("entry"));
        Assert.False(await ctx.NavigateTree("target"));
        Assert.False(await ctx.SwitchSession("/tmp/s.jsonl"));
        await ctx.WaitForIdle();
        await ctx.Reload();
        Assert.Equal("/work", ctx.GetSystemPromptOptions().Cwd);
    }

    [Fact]
    public async Task CreateCommandContext_DelegatesToBoundActions()
    {
        var calls = new List<string>();
        var runner = await CreateRunnerAsync([]);
        runner.BindCore(DefaultActions(), DefaultContextActions());
        runner.BindCommandContext(new ExtensionCommandContextActions
        {
            WaitForIdle = () => { calls.Add("wait"); return Task.CompletedTask; },
            NewSession = options => { calls.Add($"new:{options?.ParentSession}"); return Task.FromResult(true); },
            Fork = (entryId, _) => { calls.Add($"fork:{entryId}"); return Task.FromResult(true); },
            NavigateTree = (targetId, _) => { calls.Add($"tree:{targetId}"); return Task.FromResult(true); },
            SwitchSession = (path, _) => { calls.Add($"switch:{path}"); return Task.FromResult(true); },
            Reload = () => { calls.Add("reload"); return Task.CompletedTask; },
        });

        var ctx = runner.CreateCommandContext();
        await ctx.WaitForIdle();
        Assert.True(await ctx.NewSession(new NewSessionOptions { ParentSession = "p" }));
        Assert.True(await ctx.Fork("e"));
        Assert.True(await ctx.NavigateTree("t"));
        Assert.True(await ctx.SwitchSession("/s"));
        await ctx.Reload();

        Assert.Equal(["wait", "new:p", "fork:e", "tree:t", "switch:/s", "reload"], calls);
    }

    // ------------------------------------------------------------------ UI context

    [Fact]
    public async Task SetUIContext_NonNullMakesHasUI_AndWrapsDialogs()
    {
        var startSeen = new TaskCompletionSource<ExtensionEvent.UiPromptStart>();
        var endSeen = new TaskCompletionSource<ExtensionEvent.UiPromptEnd>();
        var extension = NewExtension(
            "<inline>",
            (ExtensionEventNames.UiPromptStart, Handler(args =>
            {
                startSeen.TrySetResult((ExtensionEvent.UiPromptStart)args[0]!);
                return Task.FromResult<object?>(null);
            })),
            (ExtensionEventNames.UiPromptEnd, Handler(args =>
            {
                endSeen.TrySetResult((ExtensionEvent.UiPromptEnd)args[0]!);
                return Task.FromResult<object?>(null);
            })));

        var runner = await CreateRunnerAsync([extension]);
        runner.BindCore(DefaultActions(), DefaultContextActions());
        runner.SetUIContext(new StubUIContext(), ExtensionMode.Tui);

        Assert.True(runner.HasUI());
        Assert.Equal(ExtensionMode.Tui, runner.CreateContext().Mode);

        var chosen = await runner.GetUIContext().Select("Pick", ["a", "b"]);
        Assert.Equal("a", chosen);

        await AwaitWithTimeout(startSeen.Task);
        await AwaitWithTimeout(endSeen.Task);

        var start = await startSeen.Task;
        var end = await endSeen.Task;

        Assert.Equal(UiPromptKind.Select, start.Kind);
        Assert.Equal("Pick", start.Title);
        Assert.Equal(UiPromptKind.Select, end.Kind);
    }

    [Fact]
    public async Task SetUIContext_NullRestoresNoOp()
    {
        var runner = await CreateRunnerAsync([]);
        runner.BindCore(DefaultActions(), DefaultContextActions());
        runner.SetUIContext(new StubUIContext(), ExtensionMode.Tui);
        Assert.True(runner.HasUI());

        runner.SetUIContext();

        Assert.False(runner.HasUI());
        Assert.Null(await runner.GetUIContext().Select("Pick", ["a"]));
        Assert.False(await runner.GetUIContext().Confirm("Sure?", "body"));
        Assert.Equal("", runner.GetUIContext().GetEditorText());
        Assert.False(runner.GetUIContext().SetTheme("dark").Success);
        Assert.Equal("UI not available", runner.GetUIContext().SetTheme("dark").Error);
        Assert.Empty(runner.GetUIContext().GetAllThemes());
    }

    // ------------------------------------------------------------------ generic emit

    [Fact]
    public async Task Emit_HandlerErrorIsReportedAndDoesNotPropagate()
    {
        var reached = false;
        var extension = NewExtension(
            "boom.ts",
            (ExtensionEventNames.AgentStart, Handler(_ => throw new InvalidOperationException("handler failed"))),
            (ExtensionEventNames.AgentStart, Handler(_ =>
            {
                reached = true;
                return Task.FromResult<object?>(null);
            })));

        var runner = await CreateRunnerAsync([extension]);
        var errors = new List<ExtensionError>();
        runner.OnError(errors.Add);

        await runner.EmitAsync(new ExtensionEvent.AgentStart());

        Assert.True(reached);
        var error = Assert.Single(errors);
        Assert.Equal("boom.ts", error.ExtensionPath);
        Assert.Equal(ExtensionEventNames.AgentStart, error.Event);
        Assert.Equal("handler failed", error.Error);
    }

    [Fact]
    public async Task Emit_SessionBeforeSwitch_CancelShortCircuits()
    {
        var reached = false;
        var cancelling = NewExtension("a.ts", (ExtensionEventNames.SessionBeforeSwitch, Handler(_ =>
            Task.FromResult<object?>(new SessionBeforeSwitchResult { Cancel = true }))));
        var later = NewExtension("b.ts", (ExtensionEventNames.SessionBeforeSwitch, Handler(_ =>
        {
            reached = true;
            return Task.FromResult<object?>(null);
        })));

        var runner = await CreateRunnerAsync([cancelling, later]);
        var result = await runner.EmitAsync(new ExtensionEvent.SessionBeforeSwitch("user"));

        Assert.True(result!.Cancel);
        Assert.False(reached);
    }

    [Fact]
    public async Task Emit_SessionBeforeFork_WithoutCancelKeepsLastResult()
    {
        var first = NewExtension("a.ts", (ExtensionEventNames.SessionBeforeFork, Handler(_ =>
            Task.FromResult<object?>(new SessionBeforeForkResult { SkipConversationRestore = true }))));
        var second = NewExtension("b.ts", (ExtensionEventNames.SessionBeforeFork, Handler(_ =>
            Task.FromResult<object?>(new SessionBeforeForkResult { SkipConversationRestore = false }))));

        var runner = await CreateRunnerAsync([first, second]);
        var result = await runner.EmitAsync(new ExtensionEvent.SessionBeforeFork("entry", ForkPosition.Before));

        Assert.False(result!.SkipConversationRestore);
    }

    [Fact]
    public async Task Emit_NonSessionEventDiscardsHandlerResult()
    {
        var extension = NewExtension("a.ts", (ExtensionEventNames.AgentStart, Handler(_ =>
            Task.FromResult<object?>(new SessionBeforeSwitchResult { Cancel = true }))));

        var runner = await CreateRunnerAsync([extension]);
        await runner.EmitAsync(new ExtensionEvent.AgentStart());

        // No exception, no result: the generic path only returns results for session_before_* events.
        Assert.True(true);
    }

    [Fact]
    public async Task EmitSessionShutdownEvent_ReportsWhetherHandlersExist()
    {
        // No handlers: TS returns false without emitting.
        var bare = await CreateRunnerAsync([]);
        Assert.False(await ExtensionRunner.EmitSessionShutdownEventAsync(
            bare, new ExtensionEvent.SessionShutdown(SessionEventReason.Quit)));

        // A handler present: the event is emitted once and the helper reports true.
        var seen = false;
        var withHandler = await CreateRunnerAsync([NewExtension("<inline>", (ExtensionEventNames.SessionShutdown, Handler(_ =>
        {
            seen = true;
            return Task.FromResult<object?>(null);
        })))]);

        Assert.True(await ExtensionRunner.EmitSessionShutdownEventAsync(
            withHandler, new ExtensionEvent.SessionShutdown(SessionEventReason.Quit)));
        Assert.True(seen);
    }

    // ------------------------------------------------------------------ cache warming

    [Fact]
    public async Task EmitCacheWarmingDecision_LastOverrideWins()
    {
        var extension = NewExtension(
            "a.ts",
            (ExtensionEventNames.CacheWarmingDecision, Handler(_ =>
                Task.FromResult<object?>(new CacheWarmingDecisionEventResult { Action = CacheWarmingAction.Stop }))),
            (ExtensionEventNames.CacheWarmingDecision, Handler(_ =>
                Task.FromResult<object?>(new CacheWarmingDecisionEventResult { Action = CacheWarmingAction.Warm }))));

        var runner = await CreateRunnerAsync([extension]);
        var action = await runner.EmitCacheWarmingDecisionAsync(
            new ExtensionEvent.CacheWarmingDecision(0.1, 0.2, 0.3, CacheWarmingAction.Stop));

        Assert.Equal(CacheWarmingAction.Warm, action);
    }

    [Fact]
    public async Task EmitCacheWarmingDecision_KeepsEventActionWhenHandlersReturnNothing()
    {
        var extension = NewExtension("a.ts", (ExtensionEventNames.CacheWarmingDecision, Handler(_ =>
            Task.FromResult<object?>(null))));

        var runner = await CreateRunnerAsync([extension]);
        var action = await runner.EmitCacheWarmingDecisionAsync(
            new ExtensionEvent.CacheWarmingDecision(0.1, 0.2, 0.3, CacheWarmingAction.Warm));

        Assert.Equal(CacheWarmingAction.Warm, action);
    }

    // ------------------------------------------------------------------ message_end

    [Fact]
    public async Task EmitMessageEnd_ReturnsReplacementOnlyWhenChanged()
    {
        var original = new UserMessage("hi", 0);
        var replacement = new UserMessage("edited", 0);
        var extension = NewExtension("a.ts", (ExtensionEventNames.MessageEnd, Handler(_ =>
            Task.FromResult<object?>(new MessageEndEventResult { Message = replacement }))));

        var runner = await CreateRunnerAsync([extension]);
        Assert.Same(replacement, await runner.EmitMessageEndAsync(new ExtensionEvent.MessageEnd(original)));

        var noop = await CreateRunnerAsync([NewExtension("b.ts", (ExtensionEventNames.MessageEnd, Handler(_ => Task.FromResult<object?>(null))))]);
        Assert.Null(await noop.EmitMessageEndAsync(new ExtensionEvent.MessageEnd(original)));
    }

    [Fact]
    public async Task EmitMessageEnd_RejectsRoleChangeAndReports()
    {
        var original = new UserMessage("hi", 0);
        var extension = NewExtension("a.ts", (ExtensionEventNames.MessageEnd, Handler(_ =>
            Task.FromResult<object?>(new MessageEndEventResult { Message = new AssistantMessage([]) }))));

        var runner = await CreateRunnerAsync([extension]);
        var errors = new List<ExtensionError>();
        runner.OnError(errors.Add);

        Assert.Null(await runner.EmitMessageEndAsync(new ExtensionEvent.MessageEnd(original)));
        Assert.Equal("message_end handlers must return a message with the same role", Assert.Single(errors).Error);
    }

    [Fact]
    public async Task EmitMessageEnd_LaterHandlerSeesEarlierReplacement()
    {
        var original = new UserMessage("hi", 0);
        var first = new UserMessage("one", 0);
        var second = new UserMessage("two", 0);
        UserMessage? seen = null;

        var extension = NewExtension(
            "a.ts",
            (ExtensionEventNames.MessageEnd, Handler(_ =>
                Task.FromResult<object?>(new MessageEndEventResult { Message = first }))),
            (ExtensionEventNames.MessageEnd, Handler(args =>
            {
                seen = (UserMessage)((ExtensionEvent.MessageEnd)args[0]!).Message;
                return Task.FromResult<object?>(new MessageEndEventResult { Message = second });
            })));

        var runner = await CreateRunnerAsync([extension]);
        var result = await runner.EmitMessageEndAsync(new ExtensionEvent.MessageEnd(original));

        Assert.Same(first, seen);
        Assert.Same(second, result);
    }

    // ------------------------------------------------------------------ tool_result

    [Fact]
    public async Task EmitToolResult_ReplacingContentDropsUnmatchedStructuredContent()
    {
        var extension = NewExtension("a.ts", (ExtensionEventNames.ToolResult, Handler(_ =>
            Task.FromResult<object?>(new ToolResultEventResult
            {
                Content = [new TextContent("replaced")],
            }))));

        var runner = await CreateRunnerAsync([extension]);
        var @event = new ExtensionEvent.ReadToolResult(
            "call",
            new Dictionary<string, object?>(),
            [new TextContent("original")],
            IsError: false,
            StructuredContent: JsonValue.Create("stale"));

        var result = await runner.EmitToolResultAsync(@event);

        Assert.Equal("replaced", ((TextContent)result!.Content![0]).Text);
        Assert.Null(result.StructuredContent);
    }

    [Fact]
    public async Task EmitToolResult_ReturnsNullWhenNothingChanged()
    {
        var extension = NewExtension("a.ts", (ExtensionEventNames.ToolResult, Handler(_ =>
            Task.FromResult<object?>(new ToolResultEventResult()))));

        var runner = await CreateRunnerAsync([extension]);
        var @event = new ExtensionEvent.ReadToolResult(
            "call", new Dictionary<string, object?>(), [new TextContent("original")], IsError: false);

        Assert.Null(await runner.EmitToolResultAsync(@event));
    }

    [Fact]
    public async Task EmitToolResult_LaterHandlerSeesEarlierPatch()
    {
        IToolResultEvent? seen = null;
        var extension = NewExtension(
            "a.ts",
            (ExtensionEventNames.ToolResult, Handler(_ =>
                Task.FromResult<object?>(new ToolResultEventResult { IsError = true }))),
            (ExtensionEventNames.ToolResult, Handler(args =>
            {
                seen = (IToolResultEvent)args[0]!;
                return Task.FromResult<object?>(null);
            })));

        var runner = await CreateRunnerAsync([extension]);
        var @event = new ExtensionEvent.ReadToolResult(
            "call", new Dictionary<string, object?>(), [new TextContent("original")], IsError: false);

        var result = await runner.EmitToolResultAsync(@event);

        Assert.True(seen!.IsError);
        Assert.True(result!.IsError);
    }

    // ------------------------------------------------------------------ tool_call

    [Fact]
    public async Task EmitToolCall_BlockShortCircuits()
    {
        var reached = false;
        var extension = NewExtension(
            "a.ts",
            (ExtensionEventNames.ToolCall, Handler(_ =>
                Task.FromResult<object?>(new ToolCallEventResult { Block = true, Reason = "no" }))),
            (ExtensionEventNames.ToolCall, Handler(_ =>
            {
                reached = true;
                return Task.FromResult<object?>(null);
            })));

        var runner = await CreateRunnerAsync([extension]);
        var result = await runner.EmitToolCallAsync(new ExtensionEvent.BashToolCall("call", new Dictionary<string, object?>()));

        Assert.True(result!.Block);
        Assert.Equal("no", result.Reason);
        Assert.False(reached);
    }

    [Fact]
    public async Task EmitToolCall_HandlerErrorPropagates()
    {
        var extension = NewExtension("a.ts", (ExtensionEventNames.ToolCall, Handler(_ =>
            throw new InvalidOperationException("blocked by hook"))));

        var runner = await CreateRunnerAsync([extension]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.EmitToolCallAsync(new ExtensionEvent.BashToolCall("call", new Dictionary<string, object?>())));
    }

    // ------------------------------------------------------------------ user_bash

    [Fact]
    public async Task EmitUserBash_ReturnsFirstValidResult()
    {
        var withResult = new UserBashEventResult.WithResult(new BashResult
        {
            Output = "hi",
            ExitCode = 0,
            Cancelled = false,
            Truncated = false,
        });
        var extension = NewExtension(
            "a.ts",
            (ExtensionEventNames.UserBash, Handler(_ => Task.FromResult<object?>(null))),
            (ExtensionEventNames.UserBash, Handler(_ => Task.FromResult<object?>(withResult))));

        var runner = await CreateRunnerAsync([extension]);
        var result = await runner.EmitUserBashAsync(new ExtensionEvent.UserBash("ls", false, "/work"));

        Assert.Same(withResult, result);
    }

    [Fact]
    public async Task EmitUserBash_InvalidResultIsReportedAndRethrown()
    {
        var extension = NewExtension("a.ts", (ExtensionEventNames.UserBash, Handler(_ =>
            Task.FromResult<object?>("not a result"))));

        var runner = await CreateRunnerAsync([extension]);
        var errors = new List<ExtensionError>();
        runner.OnError(errors.Add);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.EmitUserBashAsync(new ExtensionEvent.UserBash("ls", false, "/work")));

        Assert.Contains("Invalid user_bash handler result", Assert.Single(errors).Error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ context

    [Fact]
    public async Task EmitContext_UnchangedConversationKeepsSystemMessages()
    {
        var system = new SystemMessage(new MessageContent.Text("prompt"), Timestamp: 0);
        var user = new UserMessage("hi", 0);
        var extension = NewExtension("a.ts", (ExtensionEventNames.Context, Handler(_ => Task.FromResult<object?>(null))));

        var runner = await CreateRunnerAsync([extension]);
        var result = await runner.EmitContextAsync([system, user]);

        Assert.Equal(2, result.Count);
        Assert.Same(system, result[0]);
        Assert.Same(user, result[1]);
    }

    [Fact]
    public async Task EmitContext_ChangedConversationGetsReplayedSystemHead()
    {
        var system = new SystemMessage(new MessageContent.Text("prompt"), Timestamp: 0);
        var user = new UserMessage("hi", 0);
        var edited = new UserMessage("edited", 0);
        var extension = NewExtension("a.ts", (ExtensionEventNames.Context, Handler(_ =>
            Task.FromResult<object?>(new ContextEventResult { Messages = [edited] }))));

        var runner = await CreateRunnerAsync([extension]);
        var result = await runner.EmitContextAsync([system, user]);

        Assert.Equal(2, result.Count);
        // The head is replayed, not reused: TS rebuilds it from the transcript
        // (`getCurrentSystemMessage`), so only its value is preserved.
        Assert.Equal(system, result[0]);
        Assert.IsType<SystemMessage>(result[0]);
        // The handler's own message object is carried through by reference.
        Assert.Same(edited, result[1]);
    }

    [Fact]
    public async Task EmitContext_HandlersOnlySeeConversation()
    {
        IReadOnlyList<ChatMessage>? seen = null;
        var system = new SystemMessage(new MessageContent.Text("prompt"), Timestamp: 0);
        var user = new UserMessage("hi", 0);
        var extension = NewExtension("a.ts", (ExtensionEventNames.Context, Handler(args =>
        {
            seen = ((ExtensionEvent.ContextEvent)args[0]!).Messages;
            return Task.FromResult<object?>(null);
        })));

        var runner = await CreateRunnerAsync([extension]);
        await runner.EmitContextAsync([system, user]);

        Assert.Same(user, Assert.Single(seen!));
    }

    [Fact]
    public async Task EmitContext_ContextWithSystemSeesFullTranscript()
    {
        IReadOnlyList<ChatMessage>? seen = null;
        var system = new SystemMessage(new MessageContent.Text("prompt"), Timestamp: 0);
        var user = new UserMessage("hi", 0);
        var extension = NewExtension("a.ts", (ExtensionEventNames.ContextWithSystem, Handler(args =>
        {
            seen = ((ExtensionEvent.ContextWithSystemEvent)args[0]!).Messages;
            return Task.FromResult<object?>(null);
        })));

        var runner = await CreateRunnerAsync([extension]);
        await runner.EmitContextAsync([system, user]);

        Assert.Equal(2, seen!.Count);
    }

    [Fact]
    public async Task EmitContext_DroppingLeadingSystemMessageIsReported()
    {
        var system = new SystemMessage(new MessageContent.Text("prompt"), Timestamp: 0);
        var user = new UserMessage("hi", 0);
        var extension = NewExtension("a.ts", (ExtensionEventNames.ContextWithSystem, Handler(_ =>
            Task.FromResult<object?>(new ContextEventResult { Messages = [user] }))));

        var runner = await CreateRunnerAsync([extension]);
        var errors = new List<ExtensionError>();
        runner.OnError(errors.Add);

        var result = await runner.EmitContextAsync([system, user]);

        // The handler's output is honored; the loss is reported, not prevented.
        Assert.Single(result);
        Assert.Contains("removed the leading system message", Assert.Single(errors).Error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ provider hooks

    [Fact]
    public async Task EmitBeforeProviderRequest_ChainsReplacements()
    {
        var extension = NewExtension(
            "a.ts",
            (ExtensionEventNames.BeforeProviderRequest, Handler(_ => Task.FromResult<object?>("second"))),
            (ExtensionEventNames.BeforeProviderRequest, Handler(args =>
            {
                var payload = ((ExtensionEvent.BeforeProviderRequest)args[0]!).Payload;
                return Task.FromResult<object?>($"{payload}+third");
            })));

        var runner = await CreateRunnerAsync([extension]);
        var result = await runner.EmitBeforeProviderRequestAsync("first");

        Assert.Equal("second+third", result);
    }

    [Fact]
    public async Task EmitBeforeProviderHeaders_HandlersMutateInPlace()
    {
        var extension = NewExtension("a.ts", (ExtensionEventNames.BeforeProviderHeaders, Handler(args =>
        {
            var headers = ((ExtensionEvent.BeforeProviderHeaders)args[0]!).Headers;
            headers["X-Test"] = "1";
            return Task.FromResult<object?>(null);
        })));

        var runner = await CreateRunnerAsync([extension]);
        var result = await runner.EmitBeforeProviderHeadersAsync(new ProviderHeaders());

        Assert.Equal("1", result["X-Test"]);
    }

    // ------------------------------------------------------------------ before_agent_start

    [Fact]
    public async Task EmitBeforeAgentStart_AccumulatesMessagesAndPromptOverrides()
    {
        var firstMessage = new CustomMessageDraft { CustomType = "a", Content = new MessageContent.Text("a") };
        var secondMessage = new CustomMessageDraft { CustomType = "b", Content = new MessageContent.Text("b") };
        var extension = NewExtension(
            "a.ts",
            (ExtensionEventNames.BeforeAgentStart, Handler(_ =>
                Task.FromResult<object?>(new BeforeAgentStartEventResult
                {
                    Message = firstMessage,
                    SystemPrompt = "forced",
                }))),
            (ExtensionEventNames.BeforeAgentStart, Handler(args =>
            {
                // The second handler sees the first one's forced prompt.
                var @event = (ExtensionEvent.BeforeAgentStart)args[0]!;
                Assert.Equal("forced", @event.SystemPrompt);
                return Task.FromResult<object?>(new BeforeAgentStartEventResult { Message = secondMessage });
            })));

        var runner = await CreateRunnerAsync([extension]);
        var result = await runner.EmitBeforeAgentStartAsync(
            "hello",
            null,
            new BuildSystemPromptOptions { Cwd = "/work" });

        Assert.Equal([firstMessage, secondMessage], result.Messages);
        Assert.Equal("forced", result.SystemPromptOptions.ForceSystemPrompt);
    }

    [Fact]
    public async Task EmitBeforeAgentStart_ContextRendersCurrentPrompt()
    {
        string? observed = null;
        var extension = NewExtension("a.ts", (ExtensionEventNames.BeforeAgentStart, Handler(args =>
        {
            observed = ((IExtensionContext)args[1]!).GetSystemPrompt();
            return Task.FromResult<object?>(null);
        })));

        var runner = await CreateRunnerAsync([extension]);
        await runner.EmitBeforeAgentStartAsync("hello", null, new BuildSystemPromptOptions { Cwd = "/work" });

        Assert.False(string.IsNullOrEmpty(observed));
    }

    // ------------------------------------------------------------------ resources_discover

    [Fact]
    public async Task EmitResourcesDiscover_AttributesPathsToContributingExtension()
    {
        var first = NewExtension("first.ts", (ExtensionEventNames.ResourcesDiscover, Handler(_ =>
            Task.FromResult<object?>(new ResourcesDiscoverResult { SkillPaths = ["/a"], ThemePaths = ["/t"] }))));
        var second = NewExtension("second.ts", (ExtensionEventNames.ResourcesDiscover, Handler(_ =>
            Task.FromResult<object?>(new ResourcesDiscoverResult { PromptPaths = ["/p"] }))));

        var runner = await CreateRunnerAsync([first, second]);
        var result = await runner.EmitResourcesDiscoverAsync("/work", "startup");

        Assert.Equal(new AttributedResourcePath("/a", "first.ts"), Assert.Single(result.SkillPaths));
        Assert.Equal(new AttributedResourcePath("/t", "first.ts"), Assert.Single(result.ThemePaths));
        Assert.Equal(new AttributedResourcePath("/p", "second.ts"), Assert.Single(result.PromptPaths));
    }

    // ------------------------------------------------------------------ input

    [Fact]
    public async Task EmitInput_TransformsChainAndHandledShortCircuits()
    {
        var reached = false;
        var extension = NewExtension(
            "a.ts",
            (ExtensionEventNames.Input, Handler(_ =>
                Task.FromResult<object?>(new InputEventResult.Transform("upper")))),
            (ExtensionEventNames.Input, Handler(args =>
            {
                var text = ((ExtensionEvent.InputEvent)args[0]!).Text;
                Assert.Equal("upper", text);
                return Task.FromResult<object?>(new InputEventResult.Handled());
            })),
            (ExtensionEventNames.Input, Handler(_ =>
            {
                reached = true;
                return Task.FromResult<object?>(null);
            })));

        var runner = await CreateRunnerAsync([extension]);
        var result = await runner.EmitInputAsync("original", null, InputSource.Interactive);

        Assert.IsType<InputEventResult.Handled>(result);
        Assert.False(reached);
    }

    [Fact]
    public async Task EmitInput_NoChangeReportsContinue()
    {
        var extension = NewExtension("a.ts", (ExtensionEventNames.Input, Handler(_ => Task.FromResult<object?>(null))));

        var runner = await CreateRunnerAsync([extension]);
        var result = await runner.EmitInputAsync("original", null, InputSource.Interactive);

        Assert.IsType<InputEventResult.Continue>(result);
    }

    [Fact]
    public async Task EmitInput_HandlerErrorIsReportedAndSkipped()
    {
        var extension = NewExtension(
            "a.ts",
            (ExtensionEventNames.Input, Handler(_ => throw new InvalidOperationException("bad input hook"))),
            (ExtensionEventNames.Input, Handler(_ =>
                Task.FromResult<object?>(new InputEventResult.Transform("ok")))));

        var runner = await CreateRunnerAsync([extension]);
        var errors = new List<ExtensionError>();
        runner.OnError(errors.Add);

        var result = await runner.EmitInputAsync("original", null, InputSource.Interactive);

        Assert.Equal("bad input hook", Assert.Single(errors).Error);
        var transform = Assert.IsType<InputEventResult.Transform>(result);
        Assert.Equal("ok", transform.Text);
    }

    // ------------------------------------------------------------------ boundary

    [Fact]
    public async Task EmitBoundary_AccumulatesEntriesAndContinue()
    {
        var draft = new SessionBoundaryDraft.Custom(new CustomEntryDraft { CustomType = "note" });
        var extension = NewExtension("a.ts", (ExtensionEventNames.TurnEnd, Handler(args =>
        {
            var @event = (ExtensionEvent.TurnEnd)args[0]!;
            // TS: `let shouldContinue = false` before the loop, so the first handler sees false.
            Assert.Empty(@event.Entries);
            Assert.False(@event.Continue);
            return Task.FromResult<object?>(new BoundaryResult { Entries = [draft], Continue = false });
        })));

        var runner = await CreateRunnerAsync([extension]);
        var rebuilds = 0;
        var result = await runner.EmitBoundaryAsync(new ExtensionEvent.TurnEnd(), _ =>
        {
            rebuilds++;
            return Task.FromResult(new BoundaryContextPreview());
        });

        Assert.True(result.Valid);
        Assert.False(result.Continue);
        Assert.Equal(draft, Assert.Single(result.Entries));
        // Once for the initial build, once after the handler.
        Assert.Equal(2, rebuilds);
    }

    [Fact]
    public async Task EmitBoundary_InvalidEntriesReportAndInvalidate()
    {
        var extension = NewExtension("a.ts", (ExtensionEventNames.TurnEnd, Handler(_ =>
            Task.FromResult<object?>(new BoundaryResult { Entries = [] }))));

        var runner = await CreateRunnerAsync([extension]);
        var errors = new List<ExtensionError>();
        runner.OnError(errors.Add);
        var calls = 0;

        var result = await runner.EmitBoundaryAsync(new ExtensionEvent.TurnEnd(), _ =>
        {
            calls++;
            if (calls > 1)
            {
                throw new InvalidOperationException("bad draft");
            }

            return Task.FromResult(new BoundaryContextPreview());
        });

        Assert.False(result.Valid);
        Assert.Empty(result.Entries);
        Assert.False(result.Continue);
        Assert.Contains("Invalid boundary entries: bad draft", Assert.Single(errors).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmitBoundary_HandlerErrorIsReported()
    {
        var extension = NewExtension("a.ts", (ExtensionEventNames.AgentBeforeSettle, Handler(_ =>
            throw new InvalidOperationException("boundary failed"))));

        var runner = await CreateRunnerAsync([extension]);
        var errors = new List<ExtensionError>();
        runner.OnError(errors.Add);

        var result = await runner.EmitBoundaryAsync(
            new ExtensionEvent.AgentBeforeSettle(),
            _ => Task.FromResult(new BoundaryContextPreview()));

        Assert.True(result.Valid);
        Assert.Equal("boundary failed", Assert.Single(errors).Error);
    }

    // ------------------------------------------------------------------ project trust

    [Fact]
    public async Task EmitProjectTrustEvent_FirstDecidedAnswerWins()
    {
        var undecided = NewExtension("a.ts", (ExtensionEventNames.ProjectTrust, Handler(_ =>
            Task.FromResult<object?>(new ProjectTrustEventResult { Trusted = ProjectTrustDecision.Undecided }))));
        var decided = NewExtension("b.ts", (ExtensionEventNames.ProjectTrust, Handler(_ =>
            Task.FromResult<object?>(new ProjectTrustEventResult { Trusted = ProjectTrustDecision.Yes }))));
        var never = NewExtension("c.ts", (ExtensionEventNames.ProjectTrust, Handler(_ =>
            throw new InvalidOperationException("must not be reached"))));

        var result = await ExtensionRunner.EmitProjectTrustEventAsync(
            new LoadExtensionsResult([undecided, decided, never], [], [], ExtensionLoader.CreateExtensionRuntime()),
            new ExtensionEvent.ProjectTrust("/work"),
            TrustContext());

        Assert.Equal(ProjectTrustDecision.Yes, result.Result!.Trusted);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task EmitProjectTrustEvent_CollectsErrorsAndReturnsNullWhenUndecided()
    {
        var failing = NewExtension("a.ts", (ExtensionEventNames.ProjectTrust, Handler(_ =>
            throw new InvalidOperationException("trust hook failed"))));

        var result = await ExtensionRunner.EmitProjectTrustEventAsync(
            new LoadExtensionsResult([failing], [], [], ExtensionLoader.CreateExtensionRuntime()),
            new ExtensionEvent.ProjectTrust("/work"),
            TrustContext());

        Assert.Null(result.Result);
        Assert.Equal("trust hook failed", Assert.Single(result.Errors).Error);
    }

    // ------------------------------------------------------------------ MCP reporting

    [Fact]
    public async Task ReportUnhandledMcpServers_ReportsOncePerServer()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        runtime.McpServers.Register(new RegisteredMcpServer("files", new McpServerConfig(new JsonObject()), "mcp.ts"));

        var runner = await CreateRunnerAsync([], runtime);
        var errors = new List<ExtensionError>();
        runner.OnError(errors.Add);

        runner.ReportUnhandledMcpServers();
        runner.ReportUnhandledMcpServers();

        var error = Assert.Single(errors);
        Assert.Equal("mcp.ts", error.ExtensionPath);
        Assert.Contains("no loaded extension connects MCP servers", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportUnhandledMcpServers_SkipsWhenHandled()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        runtime.McpServers.Register(new RegisteredMcpServer("files", new McpServerConfig(new JsonObject()), "mcp.ts"));

        var handler = NewExtension("mcp.ts", (ExtensionEventNames.McpServersChange, Handler(_ =>
            Task.FromResult<object?>(null))));

        var runner = await CreateRunnerAsync([handler], runtime);
        var errors = new List<ExtensionError>();
        runner.OnError(errors.Add);

        runner.ReportUnhandledMcpServers();

        Assert.Empty(errors);
    }

    // ------------------------------------------------------------------ misc

    [Fact]
    public async Task GetExtensionPaths_AndHasHandlers()
    {
        var runner = await CreateRunnerAsync([
            NewExtension("a.ts", (ExtensionEventNames.AgentStart, Handler(_ => Task.FromResult<object?>(null)))),
            NewExtension("b.ts"),
        ]);

        Assert.Equal(["a.ts", "b.ts"], runner.GetExtensionPaths());
        Assert.True(runner.HasHandlers(ExtensionEventNames.AgentStart));
        Assert.False(runner.HasHandlers(ExtensionEventNames.AgentEnd));
    }

    [Fact]
    public async Task Shutdown_InvokesBoundHandler()
    {
        var shutdown = false;
        var runner = await CreateRunnerAsync([]);
        runner.BindCore(
            DefaultActions(),
            DefaultContextActions() with { Shutdown = () => shutdown = true });

        runner.Shutdown();
        runner.CreateContext().Shutdown();

        Assert.True(shutdown);
    }

    [Fact]
    public async Task OnError_UnsubscribeStopsDelivery()
    {
        var runner = await CreateRunnerAsync([]);
        var errors = new List<ExtensionError>();
        var unsubscribe = runner.OnError(errors.Add);

        runner.EmitError(new ExtensionError("a.ts", "agent_start", "one"));
        unsubscribe();
        runner.EmitError(new ExtensionError("a.ts", "agent_start", "two"));

        Assert.Equal("one", Assert.Single(errors).Error);
    }

    // ------------------------------------------------------------------ wrapper
    //
    // Port of core/extensions/wrapper.ts. The module is four lines of TS, but it is the seam where an
    // extension-registered ToolDefinition becomes an AgentTool, so the tests pin the two things the
    // seam exists to guarantee: the tool receives the *runner's* context (not a fresh one), and that
    // context is the full ExtensionToolContext, i.e. it can run a nested tool.

    [Fact]
    public async Task Wrapper_WrapRegisteredTool_UsesRunnerToolContext()
    {
        IExtensionToolContext? captured = null;
        var entry = RegisteredToolFor("echo", "echo");
        entry = entry with
        {
            Definition = entry.Definition with
            {
                Execute = (_, _, _, _, context) =>
                {
                    captured = context;
                    return Task.FromResult(new AgentToolResult([]));
                },
            },
        };

        var runner = await CreateRunnerAsync([]);
        runner.BindCore(
            DefaultActions(),
            DefaultContextActions() with { GetCallableTools = () => [Tool("read")] });

        var wrapped = Wrapper.WrapRegisteredTool(entry, runner);
        await wrapped.Execute("call-1", new Dictionary<string, object?>(), CancellationToken.None, null);

        var context = Assert.IsAssignableFrom<IExtensionToolContext>(captured);
        Assert.Equal("/work", context.Cwd);
        Assert.Equal("read", Assert.Single(context.Tools).Name);
    }

    [Fact]
    public async Task Wrapper_WrapRegisteredTool_WithoutContextFactoryPassesNullContext()
    {
        // wrapToolDefinition tolerates a missing factory, and every built-in tool declares its context
        // optional. Pinned because the port made the parameter non-optional in ToolDefinition.Execute.
        var seen = new List<IExtensionToolContext?>();
        var definition = new ToolDefinition
        {
            Name = "bare",
            Label = "bare",
            Description = "bare",
            Parameters = new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }),
            Execute = (_, _, _, _, context) =>
            {
                seen.Add(context);
                return Task.FromResult(new AgentToolResult([]));
            },
        };

        var wrapped = Pi.CodingAgent.Core.Tools.ToolDefinitionWrapper.WrapToolDefinition(definition);
        await wrapped.Execute("call-1", new Dictionary<string, object?>(), CancellationToken.None, null);

        Assert.Null(Assert.Single(seen));
    }

    [Fact]
    public async Task Wrapper_WrapRegisteredTools_PreservesOrder()
    {
        var runner = await CreateRunnerAsync([]);

        var wrapped = Wrapper.WrapRegisteredTools(
            [RegisteredToolFor("alpha", "a"), RegisteredToolFor("beta", "b")],
            runner);

        Assert.Equal(["alpha", "beta"], wrapped.Select(tool => tool.Name));
    }

    [Fact]
    public async Task Wrapper_WrappedTool_CanRunNestedTool()
    {
        // Regression guard for the IToolContext seam that 4e-3e-9h-2 retired. That interface carried
        // only the five members the built-in tools read, so a tool registered through registerTool()
        // had no executeTool() and could not run a nested tool — something TS's ExtensionToolContext
        // always allows. This test fails against the old seam.
        string? nestedName = null;
        object? nestedArgs = null;

        var entry = RegisteredToolFor("outer", "outer");
        entry = entry with
        {
            Definition = entry.Definition with
            {
                Execute = async (_, _, _, _, context) =>
                {
                    var outcome = await context!.ExecuteTool(
                        "read",
                        new Dictionary<string, object?> { ["path"] = "a.txt" });
                    return outcome.Result;
                },
            },
        };

        var runner = await CreateRunnerAsync([]);
        runner.BindCore(
            DefaultActions(),
            DefaultContextActions() with
            {
                GetCallableTools = () => [Tool("read")],
                ExecuteTool = (callId, name, args, options) =>
                {
                    nestedName = name;
                    nestedArgs = args;
                    return Task.FromResult(new AgentToolCallOutcome(
                        new ToolCallContent($"{callId}/0", name, new Dictionary<string, object?>()),
                        new AgentToolResult([new TextContent("nested")]),
                        IsError: false));
                },
            });

        var wrapped = Wrapper.WrapRegisteredTool(entry, runner);
        var result = await wrapped.Execute("call-1", new Dictionary<string, object?>(), CancellationToken.None, null);

        Assert.Equal("read", nestedName);
        Assert.Equal("a.txt", Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(nestedArgs)["path"]);
        Assert.Equal("nested", Assert.IsType<TextContent>(Assert.Single(result.Content)).Text);
    }

    // ------------------------------------------------------------------ fixtures

    private static Extension NewExtension(
        string path,
        params (string Event, ExtensionEventHandler Handler)[] handlers)
    {
        var extension = new Extension
        {
            Path = path,
            ResolvedPath = path,
            SourceInfo = SourceInfos.CreateSynthetic(path, "test"),
        };

        foreach (var (eventName, handler) in handlers)
        {
            if (!extension.Handlers.TryGetValue(eventName, out var list))
            {
                list = [];
                extension.Handlers[eventName] = list;
            }

            list.Add(handler);
        }

        return extension;
    }

    /// <summary>A handler whose body receives the event and the context, and returns its result.</summary>
    private static ExtensionEventHandler Handler(Func<object?[], Task<object?>> body) => args => body(args);

    private static ExtensionShortcut Shortcut(string extensionPath) => new()
    {
        Shortcut = "key",
        Handler = _ => Task.CompletedTask,
        ExtensionPath = extensionPath,
    };

    private static RegisteredCommand Command(string name) => new()
    {
        Name = name,
        SourceInfo = SourceInfos.CreateSynthetic($"cmd:{name}", "test"),
        Handler = (_, _) => Task.CompletedTask,
    };

    private static ExtensionFlag Flag(string name, string extensionPath) => new()
    {
        Name = name,
        Type = FlagType.Boolean,
        ExtensionPath = extensionPath,
    };

    private static RegisteredTool RegisteredToolFor(string name, string description) => new()
    {
        Definition = new ToolDefinition
        {
            Name = name,
            Label = name,
            Description = description,
            Parameters = new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }),
            Execute = (_, _, _, _, _) => Task.FromResult(new AgentToolResult([])),
        },
        SourceInfo = SourceInfos.CreateSynthetic($"tool:{name}", "test"),
    };

    private static AgentTool Tool(string name) => new(
        name,
        "",
        new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }),
        name,
        (_, _, _, _) => Task.FromResult(new AgentToolResult([])));

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

    /// <summary>
    /// The runtime model the bound context actions report. TS's <c>ExtensionContext.model</c> is the
    /// live <c>Model&lt;any&gt;</c>, i.e. the port's <see cref="ModelSpec"/>, so the context must hand back
    /// that same instance rather than a copy or a description.
    /// </summary>
    private static readonly ModelSpec BoundModel = new()
    {
        Id = "claude-test",
        Name = "Claude Test",
        Api = "anthropic-messages",
        Provider = "anthropic",
        BaseUrl = "https://api.anthropic.com",
    };

    private static ExtensionContextActions DefaultContextActions() => new()
    {
        GetModel = () => BoundModel,
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

    private static async Task<ExtensionRunner> CreateRunnerAsync(
        IReadOnlyList<Extension> extensions,
        IExtensionRuntime? runtime = null,
        string cwd = "/work")
    {
        var modelRuntime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new InMemoryCredentialStore(),
            ModelsPathDisabled = true,
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            RefreshOnCreate = false,
            Env = _ => null,
            AuthContext = new StubAuthContext(_ => null),
        });

        return new ExtensionRunner(
            extensions,
            runtime ?? ExtensionLoader.CreateExtensionRuntime(),
            cwd,
            new StubSessionManager(),
            new ModelRegistry(modelRuntime));
    }

    /// <summary>Await a task the runner dispatches on the thread pool, failing instead of hanging.</summary>
    private static async Task AwaitWithTimeout(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(task, completed);
        await task;
    }

    private sealed class StubSessionManager : IReadonlySessionManager
    {
        public string SessionId => "session";

        public string? SessionFile => null;

        public IReadOnlyList<SessionEntry> Entries => [];
    }

    /// <summary>
    /// The <c>project_trust</c> context. TS declares one <c>ProjectTrustContext</c>
    /// (<c>{ cwd, mode, hasUI, ui: Pick&lt;ExtensionUIContext, "select"|"confirm"|"input"|"notify"&gt; }</c>);
    /// C# cannot express <c>Pick</c>, so the port passes the full <see cref="IExtensionUIContext"/>.
    /// </summary>
    private static ProjectTrustContext TrustContext() => new()
    {
        Cwd = "/work",
        Mode = ExtensionMode.Tui,
        HasUI = true,
        Ui = new StubUIContext(),
    };

    /// <summary>
    /// A UI context that answers every dialog. Only the members the tests exercise carry behaviour;
    /// the rest are inert, matching the no-op context they replace.
    /// </summary>
    private sealed class StubUIContext : IExtensionUIContext
    {
        public Task<string?> Select(string title, IReadOnlyList<string> options, ExtensionUIDialogOptions? opts = null) =>
            Task.FromResult<string?>(options.Count > 0 ? options[0] : null);

        public Task<bool> Confirm(string title, string message, ExtensionUIDialogOptions? opts = null) =>
            Task.FromResult(true);

        public Task<string?> Input(string title, string? placeholder = null, ExtensionUIDialogOptions? opts = null) =>
            Task.FromResult<string?>("input");

        public void Notify(string message, string? type = null)
        {
        }

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

        public void SetWidget(string key, Func<ITui, Theme, IComponent>? factory, ExtensionWidgetOptions? options = null)
        {
        }

        public void SetFooter(Func<ITui, Theme, IReadonlyFooterDataProvider, IComponent>? factory)
        {
        }

        public void SetHeader(Func<ITui, Theme, IComponent>? factory)
        {
        }

        public void SetTitle(string title)
        {
        }

        public Task<T> Custom<T>(
            Func<ITui, Theme, KeybindingsManager, Action<T>, IComponent> factory,
            ExtensionCustomOptions? options = null) =>
            Task.FromResult(default(T)!);

        public Task<T> CustomAsync<T>(
            Func<ITui, Theme, KeybindingsManager, Action<T>, Task<IComponent>> factory,
            ExtensionCustomOptions? options = null) =>
            Task.FromResult(default(T)!);

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
}
