using System.Reflection;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.CodingAgent.Core.Tools;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the extension contract layer (port of TS
/// <c>core/extensions/types.ts</c>, batch 4d-1).
/// </summary>
/// <remarks>
/// The TS module is type-only (no runtime behaviour), so these tests pin the parts that can drift
/// silently: the wire names of every event, the literal-union values, the marker-interface
/// groupings, the type guards, and the public surface of the API/context interfaces. Each
/// expectation is transcribed from the TS source.
/// </remarks>
public class ExtensionContractTests
{
    // ------------------------------------------------------------------ wire names

    /// <summary>
    /// The 35 event names TS dispatches on, in TS source order. A rename here is a wire break.
    /// </summary>
    private static readonly string[] TsEventNames =
    [
        "project_trust", "resources_discover", "mcp_servers_change",
        "session_start", "session_info_changed", "session_before_switch", "session_before_fork",
        "session_before_compact", "session_compact", "session_compact_failed", "session_shutdown",
        "session_before_tree", "session_tree",
        "context", "context_with_system", "cache_warming_decision",
        "before_provider_request", "before_provider_headers", "after_provider_response",
        "provider_stream_event",
        "before_agent_start", "agent_start", "agent_end", "agent_before_settle", "agent_settled",
        "ui_prompt_start", "ui_prompt_end",
        "turn_start", "turn_end",
        "message_start", "message_update", "message_end",
        "tool_execution_start", "tool_execution_update", "tool_execution_end",
        "model_select", "thinking_level_select",
        "user_bash",
        "input",
        "tool_call", "tool_result",
    ];

    [Fact]
    public void ExtensionEventNames_MatchTsWireNamesExactly()
    {
        var actual = typeof(ExtensionEventNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, FieldType.Name: "String" })
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(TsEventNames.OrderBy(n => n, StringComparer.Ordinal), actual);
    }

    [Fact]
    public void EveryEventVariantMapsToAKnownWireName()
    {
        var variants = typeof(ExtensionEvent)
            .GetNestedTypes(BindingFlags.Public)
            .Where(t => t.IsSubclassOf(typeof(ExtensionEvent)))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Every TS member of the ExtensionEvent union. cache_warming_decision used to be excluded here
        // while it was still a 4e placeholder; 4e-3e-9g gave it a real nested variant, so it is in.
        Assert.Equal(
            ["AfterProviderResponse", "AgentBeforeSettle", "AgentEnd", "AgentSettled", "AgentStart",
                "BashToolCall", "BashToolResult", "BeforeAgentStart", "BeforeProviderHeaders",
                "BeforeProviderRequest", "CacheWarmingDecision", "ContextEvent", "ContextWithSystemEvent",
                "CustomToolCall", "CustomToolResult", "EditToolCall", "EditToolResult", "FindToolCall",
                "FindToolResult", "GrepToolCall", "GrepToolResult", "InputEvent", "LsToolCall", "LsToolResult",
                "McpServersChange", "MessageEnd", "MessageStart", "MessageUpdate", "ModelSelect",
                "PowerShellToolCall", "PowerShellToolResult", "ProjectTrust", "ProviderStream",
                "ReadToolCall", "ReadToolResult", "ResourcesDiscover", "SessionBeforeCompact",
                "SessionBeforeFork", "SessionBeforeSwitch", "SessionBeforeTree", "SessionCompact",
                "SessionCompactFailed", "SessionInfoChanged", "SessionShutdown", "SessionStart",
                "SessionTree", "ThinkingLevelSelect", "ToolExecutionEnd", "ToolExecutionStart",
                "ToolExecutionUpdate", "TurnEnd", "TurnStart", "UiPromptEnd", "UiPromptStart",
                "UserBash", "WriteToolCall", "WriteToolResult"],
            variants);
    }

    // ------------------------------------------------------------------ literal unions

    [Fact]
    public void LiteralUnionValues_MatchTs()
    {
        Assert.Equal("tui", ExtensionMode.Tui);
        Assert.Equal("rpc", ExtensionMode.Rpc);
        Assert.Equal("json", ExtensionMode.Json);
        Assert.Equal("print", ExtensionMode.Print);

        Assert.Equal("aboveEditor", WidgetPlacement.AboveEditor);
        Assert.Equal("belowEditor", WidgetPlacement.BelowEditor);

        Assert.Equal("info", NotifyType.Info);
        Assert.Equal("warning", NotifyType.Warning);
        Assert.Equal("error", NotifyType.Error);

        Assert.Equal("before", ForkPosition.Before);
        Assert.Equal("at", ForkPosition.At);

        Assert.Equal("steer", DeliverAs.Steer);
        Assert.Equal("followUp", DeliverAs.FollowUp);
        Assert.Equal("nextTurn", DeliverAs.NextTurn);

        Assert.Equal("select", UiPromptKind.Select);
        Assert.Equal("confirm", UiPromptKind.Confirm);
        Assert.Equal("input", UiPromptKind.Input);
        Assert.Equal("editor", UiPromptKind.Editor);
        Assert.Equal("custom", UiPromptKind.Custom);

        Assert.Equal("set", ModelSelectSource.Set);
        Assert.Equal("cycle", ModelSelectSource.Cycle);
        Assert.Equal("restore", ModelSelectSource.Restore);

        Assert.Equal("interactive", InputSource.Interactive);
        Assert.Equal("rpc", InputSource.Rpc);
        Assert.Equal("extension", InputSource.Extension);

        Assert.Equal("completed", AgentActivityOutcome.Completed);
        Assert.Equal("aborted", AgentActivityOutcome.Aborted);
        Assert.Equal("error", AgentActivityOutcome.Error);

        Assert.Equal("yes", ProjectTrustDecision.Yes);
        Assert.Equal("no", ProjectTrustDecision.No);
        Assert.Equal("undecided", ProjectTrustDecision.Undecided);

        Assert.Equal("boolean", FlagType.Boolean);
        Assert.Equal("string", FlagType.String);
    }

    // ------------------------------------------------------------------ marker groups

    [Fact]
    public void MarkerInterfaces_CoverExactlyTheTsGroups()
    {
        var session = VariantsImplementing<ISessionEvent>();
        var toolCall = VariantsImplementing<IToolCallEvent>();
        var toolResult = VariantsImplementing<IToolResultEvent>();

        Assert.Equal(
            ["SessionBeforeCompact", "SessionBeforeFork", "SessionBeforeSwitch", "SessionBeforeTree",
                "SessionCompact", "SessionCompactFailed", "SessionInfoChanged", "SessionShutdown",
                "SessionStart", "SessionTree"],
            session.OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal(
            ["BashToolCall", "CustomToolCall", "EditToolCall", "FindToolCall", "GrepToolCall",
                "LsToolCall", "PowerShellToolCall", "ReadToolCall", "WriteToolCall"],
            toolCall.OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal(
            ["BashToolResult", "CustomToolResult", "EditToolResult", "FindToolResult", "GrepToolResult",
                "LsToolResult", "PowerShellToolResult", "ReadToolResult", "WriteToolResult"],
            toolResult.OrderBy(n => n, StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------ type guards

    [Fact]
    public void IsToolCallEventType_MatchesTsGuard()
    {
        var bash = new ExtensionEvent.BashToolCall("t1", new Dictionary<string, object?>());
        var custom = new ExtensionEvent.CustomToolCall("t1", "my_tool", new Dictionary<string, object?>());
        var result = new ExtensionEvent.BashToolResult(
            "t1", new Dictionary<string, object?>(), [], false);

        Assert.True(ExtensionEventGuards.IsToolCallEventType("bash", bash));
        Assert.False(ExtensionEventGuards.IsToolCallEventType("read", bash));
        Assert.True(ExtensionEventGuards.IsToolCallEventType("my_tool", custom));
        Assert.False(ExtensionEventGuards.IsToolCallEventType("other", custom));
        Assert.False(ExtensionEventGuards.IsToolCallEventType("bash", result));

        Assert.True(ExtensionEventGuards.IsBashToolResult(result));
        Assert.False(ExtensionEventGuards.IsBashToolResult(bash));
    }

    // ------------------------------------------------------------------ event shapes

    [Fact]
    public void SessionEvents_KeepTsOptionalFields()
    {
        var start = new ExtensionEvent.SessionStart("new", "prev.jsonl");
        Assert.Equal("new", start.Reason);
        Assert.Equal("prev.jsonl", start.PreviousSessionFile);

        var plain = new ExtensionEvent.SessionStart("startup");
        Assert.Null(plain.PreviousSessionFile);

        var tree = new ExtensionEvent.SessionTree("newLeaf", "oldLeaf");
        Assert.Null(tree.SummaryEntry);
        Assert.False(tree.FromExtension);

        var failed = new ExtensionEvent.SessionCompactFailed(
            "threshold", "boom", Aborted: true, WillRetry: false, FromExtension: true);
        Assert.True(failed.Aborted);
        Assert.True(failed.FromExtension);
    }

    [Fact]
    public void InputEvent_UsesTsFieldNames()
    {
        var e = new ExtensionEvent.InputEvent("hi", null, "interactive");
        Assert.Equal("hi", e.Text);
        Assert.Null(e.Images);
        Assert.Equal("interactive", e.Source);
        Assert.Null(e.StreamingBehavior);
    }

    [Fact]
    public void BoundaryState_DefaultsToContinueCompleted()
    {
        var state = new BoundaryState();
        Assert.True(state.Continue);
        Assert.Equal(AgentActivityOutcome.Completed, state.Outcome);
        Assert.Empty(state.Entries);
        Assert.Null(state.Context);
    }

    [Fact]
    public void BoundaryDrafts_CarryTsFields()
    {
        SessionBoundaryDraft draft = new SessionBoundaryDraft.Compaction(
            new CompactionEntryDraft { Summary = "s", FirstKeptEntryId = "e1" });
        var compaction = Assert.IsType<SessionBoundaryDraft.Compaction>(draft);
        Assert.Equal("s", compaction.Draft.Summary);
        Assert.Equal("e1", compaction.Draft.FirstKeptEntryId);

        var nullKept = new CompactionEntryDraft { Summary = "s", FirstKeptEntryId = null };
        Assert.Null(nullKept.FirstKeptEntryId);
    }

    [Fact]
    public void EventResults_KeepTsFieldShapes()
    {
        var blocked = new ToolCallEventResult { Block = true, Reason = "nope", Terminate = true };
        Assert.True(blocked.Block);
        Assert.Equal("nope", blocked.Reason);
        Assert.True(blocked.Terminate);

        var trust = new ProjectTrustEventResult { Trusted = ProjectTrustDecision.Yes, Remember = true };
        Assert.Equal("yes", trust.Trusted);

        var fork = new SessionBeforeForkResult { Cancel = true, SkipConversationRestore = true };
        Assert.True(fork.SkipConversationRestore);

        var input = new InputEventResult.Transform("rewritten");
        Assert.Equal("rewritten", Assert.IsType<InputEventResult.Transform>(input).Text);
    }

    // ------------------------------------------------------------------ placeholders

    [Fact]
    public void ProviderHeaders_DeleteOnNullAssignment()
    {
        var headers = new ProviderHeaders();
        headers["X-A"] = "1";
        headers["X-B"] = "2";
        Assert.Equal("1", headers["X-A"]);

        headers["X-A"] = null;
        Assert.Null(headers["X-A"]);
        Assert.Equal("2", headers["X-B"]);
        Assert.Single(headers.AsDictionary());
    }

    [Fact]
    public void OverlayHandle_ForwardsControlToTuiHandle()
    {
        var underlying = new RecordingOverlayHandle();
        var handle = new OverlayHandle(underlying);

        handle.SetHidden(true);
        Assert.True(handle.IsHidden());
        handle.SetHidden(false);
        Assert.False(handle.IsHidden());

        handle.Focus();
        Assert.True(handle.IsFocused());

        var target = new Text("target", 0, 0);
        handle.Unfocus(new OverlayUnfocusOptions { Target = target });
        Assert.Same(target, underlying.UnfocusTarget);
        Assert.False(handle.IsFocused());

        var bounds = Assert.IsType<OverlayBounds>(handle.GetBounds());
        Assert.Equal(1, bounds.Row);
        Assert.Equal(2, bounds.Col);
        Assert.Equal(3, bounds.Width);
        Assert.Equal(4, bounds.Height);

        handle.Hide();
        Assert.True(underlying.Disposed);
        // TS hide() 对已移除的 overlay 是空操作，包装层重复调用不得抛出。
        handle.Hide();
        handle.SetHidden(true);
    }

    [Fact]
    public void PlaceholderTypes_AreConstructible()
    {
        Assert.NotNull(SourceInfos.CreateSynthetic("builtin:read", "builtin"));
        Assert.NotNull(EventBusController.CreateEventBus());
        Assert.NotNull(new OverlayHandle(new RecordingOverlayHandle()));
        Assert.NotNull(new CustomMessage("t", new MessageContent.Text("c"), false, 0));
        Assert.NotNull(BashTool.CreateLocalBashOperations());
        // 4e-3e-9g 移除了两个重复占位：`Provider`（真实类型是 Pi.Ai.Models.IProvider，由
        // CoreModelRegistryTests 覆盖）与 `BashOperations`（真实类型是 IBashToolOperations）。
        // 4f-2a 移除了 `Theme` 占位：真实类型是 Modes.Interactive.Theme，构造需要 fg/bg 色表
        // 与终端配色模式，可构造性由 ThemeTests 覆盖，不再属于「占位类型」清单。
    }

    // ------------------------------------------------------------------ API surface

    [Fact]
    public void ExtensionApi_SurfaceMatchesTsMethodList()
    {
        var actual = PublicMemberNames(typeof(IExtensionApi));
        Assert.Equal(Expected("on", "registerTool", "registerCommand", "registerShortcut",
            "registerFlag", "getFlag", "registerMessageRenderer", "registerMarkdownTransformer",
            "registerEntryRenderer", "registerToolRenderer", "sendMessage", "sendUserMessage",
            "appendEntry", "setSessionName", "getSessionName", "setLabel", "exec", "getActiveTools",
            "getAllTools", "getSettings", "setActiveTools", "getCommands", "setModel",
            "getThinkingLevel", "setThinkingLevel", "registerProvider", "unregisterProvider",
            "registerMcpServer", "unregisterMcpServer", "getMcpServers", "registerVirtualModel",
            "unregisterVirtualModel", "events"), actual);
    }

    [Fact]
    public void ExtensionContexts_SurfaceMatchesTsMethodList()
    {
        Assert.Equal(Expected("ui", "mode", "hasUI", "cwd", "sessionManager", "modelRegistry", "model",
            "scopedModels", "thinkingLevel", "isIdle", "isProjectTrusted", "signal", "abort",
            "hasPendingMessages", "shutdown", "getContextUsage", "compact", "getSystemPrompt"),
            PublicMemberNames(typeof(IExtensionContext)));

        Assert.Equal(Expected("select", "confirm", "input", "notify", "onTerminalInput", "setStatus",
            "setWorkingMessage", "setWorkingVisible", "setWorkingIndicator", "setHiddenThinkingLabel",
            "setWidget", "setFooter", "setHeader", "setTitle", "custom", "customAsync", "pasteToEditor",
            "setEditorText", "getEditorText", "editor", "addAutocompleteProvider", "setEditorComponent",
            "getEditorComponent", "theme", "getAllThemes", "getTheme", "setTheme", "getToolsExpanded",
            "setToolsExpanded"),
            PublicMemberNames(typeof(IExtensionUIContext), includeInheritedInterfaces: true));

        Assert.Equal(Expected("tools", "executeTool"), PublicMemberNames(typeof(IExtensionToolContext)));

        Assert.Equal(Expected("getSystemPromptOptions", "waitForIdle", "newSession", "fork",
            "navigateTree", "switchSession", "reload"),
            PublicMemberNames(typeof(IExtensionCommandContext)));

        Assert.Equal(Expected("sendMessage", "sendUserMessage"),
            PublicMemberNames(typeof(IReplacedSessionContext)));
    }

    [Fact]
    public void ContextUsage_ComputesPercentLikeTs()
    {
        var usage = new ContextUsage { Tokens = 500, ContextWindow = 1000, Percent = 50 };
        Assert.Equal(50, usage.Percent);

        var unknown = new ContextUsage { Tokens = null, ContextWindow = 1000 };
        Assert.Null(unknown.Tokens);
        Assert.Null(unknown.Percent);
    }

    // ------------------------------------------------------------------ helpers

    private static List<string> VariantsImplementing<TMarker>()
        => typeof(ExtensionEvent)
            .GetNestedTypes(BindingFlags.Public)
            .Where(t => t.IsSubclassOf(typeof(ExtensionEvent)) && typeof(TMarker).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

    /// <summary>Distinct public method/property names, PascalCased and ordered.</summary>
    private static List<string> PublicMemberNames(Type type)
        => PublicMemberNames(type, includeInheritedInterfaces: false);

    /// <summary>
    /// <paramref name="includeInheritedInterfaces"/> 为真时把基接口成员也展开——用于
    /// <see cref="IExtensionUIContext"/>：它的 <c>select/confirm/input/notify</c> 四个成员被移到
    /// <see cref="IProjectTrustUiContext"/>（对齐 TS 的 <c>Pick&lt;…&gt;</c>），但 TS 的 surface 仍算它们。
    /// 其余接口不能一概展开：<see cref="IExtensionToolContext"/> 继承 <see cref="IExtensionContext"/>，
    /// 但 TS 里那是两个独立接口，展开会把 <c>abort</c> 之类混进来。
    /// </summary>
    private static List<string> PublicMemberNames(Type type, bool includeInheritedInterfaces)
    {
        var surfaces = new List<Type> { type };
        if (includeInheritedInterfaces)
        {
            surfaces.AddRange(type.GetInterfaces());
        }

        return surfaces
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            .Where(m => m is MethodInfo { IsSpecialName: false } or PropertyInfo)
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>TS camelCase names → PascalCase, ordered.</summary>
    private static List<string> Expected(params string[] tsNames)
        => tsNames.Select(n => char.ToUpperInvariant(n[0]) + n[1..])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private sealed class RecordingOverlayHandle : IOverlayHandle
    {
        public bool Hidden { get; private set; }

        public bool Focused { get; private set; }

        public bool Disposed { get; private set; }

        public IComponent? UnfocusTarget { get; private set; }

        public void Hide() => this.Disposed = true;

        public void SetHidden(bool hidden) => this.Hidden = hidden;

        public bool IsHidden() => this.Hidden;

        public void Focus() => this.Focused = true;

        public void Unfocus(OverlayUnfocusOptions? options = null)
        {
            this.Focused = false;
            this.UnfocusTarget = options?.Target;
        }

        public bool IsFocused() => this.Focused;

        public OverlayBounds? GetBounds() => new() { Row = 1, Col = 2, Width = 3, Height = 4 };
    }
}
