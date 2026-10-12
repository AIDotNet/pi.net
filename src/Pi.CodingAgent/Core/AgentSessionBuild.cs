using System.Runtime.CompilerServices;
using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Utils;
// `ToolDefinition` 在 Pi.Ai.Types 与 Pi.CodingAgent.Core.Extensions 各有一个，本文件语义一律是后者。
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
// AgentSession 有同名 string 属性 SystemPrompt，类内引用静态类须走别名。
using SystemPromptApi = Pi.CodingAgent.Core.SystemPrompt;

namespace Pi.CodingAgent.Core;

/// <summary>
/// 扩展绑定的宿主侧入口。对应 TS <c>ExtensionBindings</c>（agent-session.ts:291-298）——
/// 交互/打印模式把 UI 与会话控制回调挂进扩展运行时。
/// </summary>
public sealed record ExtensionBindings
{
    public IExtensionUIContext? UiContext { get; init; }

    public string? Mode { get; init; }

    public ExtensionCommandContextActions? CommandContextActions { get; init; }

    public Action? AbortHandler { get; init; }

    public ShutdownHandler? ShutdownHandler { get; init; }

    public ExtensionErrorListener? OnError { get; init; }
}

// ============================================================================
// AgentSession 的运行时装配 — TS `agent-session.ts` 的构造器字段区（368-473）、
// `_buildRuntime`（3592）、`_refreshToolRegistry`（3481）、`_bindExtensionCore`（3346）、
// `_applyExtensionBindings`（3322）与其直接依赖的实例成员。
// ============================================================================
//
// 纯决策半边已在 9a～9e 落地为静态叶子（AgentSessionCore / AgentSessionExtension）；
// 本文件是实例接线层。凡是本批未落地的 TS 方法都在显式「缝」小节里声明并抛
// NotSupportedException（附批次号），不静默吞掉；对应关系记录在
// docs/coding-agent-porting-status.md 的 9h-2c 小节。

/// <summary>Agent 侧读取当前 ExtensionRunner 的可变引用。TS <c>{ current?: ExtensionRunner }</c>。</summary>
public sealed class ExtensionRunnerRef
{
    public ExtensionRunner? Current { get; set; }
}

/// <summary>对应 TS <c>_buildRuntime(options)</c> 的匿名形参。</summary>
public sealed record BuildRuntimeOptions
{
    public IReadOnlyList<string>? ActiveToolNames { get; init; }

    /// <summary>reload 时携带上一轮 runner 的 flagValues（TS <c>Map&lt;string, boolean | string&gt;</c>）。</summary>
    public IReadOnlyDictionary<string, object>? FlagValues { get; init; }

    public bool IncludeAllExtensionTools { get; init; }
}

/// <summary>对应 TS <c>_refreshToolRegistry(options?)</c> 的匿名形参。</summary>
public sealed record RefreshToolRegistryOptions
{
    public IReadOnlyList<string>? ActiveToolNames { get; init; }

    public bool IncludeAllExtensionTools { get; init; }
}

/// <summary>
/// AgentSession 的实例状态与运行时装配（4e-3e-9h-2c）。
/// </summary>
/// <remarks>
/// <para>
/// <b>差异 C148（模型状态二分）</b>：TS 的 <c>agent.state.model</c> 是唯一模型真相；端口的
/// <see cref="Pi.Agent.Agent"/> 持有瘦形状 <see cref="Pi.Ai.Types.Model"/>，而会话/目录/扩展层
/// 流通的是 <see cref="ModelSpec"/>。会话以 <c>_currentModel</c>（ModelSpec）为真相，每次变更时
/// 经 <see cref="CompatShims.ToModel"/> 镜像到 <c>_agent.Model</c>。扩展拿到的是活的 ModelSpec
/// （引用同一性，与 TS 行为一致）。
/// </para>
/// <para>
/// <b>差异 C149（WeakMap/WeakSet）</b>：TS 的 <c>_entryIdsByMessage</c> /
/// <c>_boundaryDispatchedMessages</c> 是弱引用容器（引用同一性）。端口用
/// <see cref="ConditionalWeakTable{TKey, TValue}"/>，GC 语义与同一性语义都与 TS 对齐。
/// </para>
/// </remarks>
public partial class AgentSession
{
    // =========================================================================
    // TS 构造器字段区（agent-session.ts:369-472），按 TS 顺序排列
    // =========================================================================
    // 注：其中一批字段（重试 / 边界 / 压缩进行态等）的**消费点**在 9h-3 / 9h-4 的
    // installer 与事件处理里，本批先照 TS 全量落地；项目把警告当错误，故该区域
    // 显式抑制「未使用 / 未赋值」诊断，随消费点落地逐个移除。
    #pragma warning disable CS0169 // 字段已声明但未使用（9h-3/9h-4 消费）
    #pragma warning disable CS0649 // 字段从未赋值（9h-3/9h-4 赋值）
    #pragma warning disable CS0414 // 字段已赋值但从未读取（9h-3/9h-4 读取）

    public Pi.Agent.Agent Agent => _agent;

    public SessionManager SessionManager { get; }

    public SettingsManager SettingsManager { get; }

    private readonly Pi.Agent.Agent _agent;
    private List<ScopedModel> _scopedModels;

    // Event subscription state
    private IDisposable? _unsubscribeAgent;                 // 9h-3 接线
    private readonly List<AgentSessionEventListener> _eventListeners = [];
    private bool _isAgentRunActive;
    private bool _agentRunAbortRequested;
    private TaskCompletionSource? _idleWaitTcs;

    /// <summary>Tracks pending steering messages for UI display. Removed when delivered.</summary>
    private readonly List<string> _steeringMessages = [];

    /// <summary>Tracks pending follow-up messages for UI display. Removed when delivered.</summary>
    private readonly List<string> _followUpMessages = [];

    /// <summary>Messages queued to be included with the next user prompt as context ("asides").</summary>
    private readonly List<CustomMessage> _pendingNextTurnMessages = [];

    /// <summary>Context-only custom messages queued during a run, flushed once the current turn's tool results are in.</summary>
    private readonly List<CustomMessage> _pendingCustomMessages = [];

    // Compaction state
    private CancellationTokenSource? _compactionAbortController;
    private CancellationTokenSource? _autoCompactionAbortController;
    private bool _overflowRecoveryAttempted;

    // Branch summarization state
    private CancellationTokenSource? _branchSummaryAbortController;

    // Retry state
    private CancellationTokenSource? _retryAbortController;
    private int _retryAttempt;

    /// <summary>Failed response that the next request repeats (auto-retry / overflow recovery).</summary>
    private AssistantMessage? _failedResponse;

    // Bash execution state
    private readonly HashSet<CancellationTokenSource> _bashAbortControllers = [];
    private readonly List<BashExecutionMessage> _pendingBashMessages = [];

    // Extension system
    private ExtensionRunner _extensionRunner = null!;
    private int _turnIndex;
    private readonly ConditionalWeakTable<object, EntryIdBox> _entryIdsByMessage = new();
    private readonly ConditionalWeakTable<object, object> _boundaryDispatchedMessages = new();
    private AssistantMessage? _lastAssistantMessage;
    private readonly List<ChatMessage> _lastAssistantToolResults = [];
    private string _lastActivityOutcome = "completed";
    private bool _isBeforeSettle;
    private bool _abortDuringBeforeSettle;
    private bool _isEmittingAgentSettled;
    private readonly List<Func<Task>> _deferredSettledActions = [];

    private readonly DefaultResourceLoader _resourceLoader;
    private readonly List<ToolDefinition> _customTools;
    private Dictionary<string, ToolDefinition> _baseToolDefinitions = new(StringComparer.Ordinal);
    private readonly string _cwd;
    private readonly ExtensionRunnerRef? _extensionRunnerRef;
    private readonly IReadOnlyList<string>? _initialActiveToolNames;

    /// <summary>
    /// Tools of the restored or reloaded loadout that are not registered yet, such as tools of MCP
    /// servers that are still connecting. They are activated when they are registered, and dropped
    /// when <c>SetActiveToolsByName</c> deactivates a tool or the next agent run starts.
    /// </summary>
    private readonly HashSet<string> _pendingToolNames = new(StringComparer.Ordinal);

    private readonly bool _usesDefaultTools;

    /// <summary>Matches the <c>--tools</c> entries: tool names or patterns.</summary>
    private readonly Func<string, bool>? _allowedTools;

    /// <summary>
    /// Whether the allowlist filters MCP tools: it is empty (<c>--no-tools</c>) or names an MCP tool
    /// (<c>mcp__*</c>). Otherwise it keeps MCP tools registered for codemode and tool_search.
    /// </summary>
    private readonly bool _allowlistFiltersMcp;

    private readonly Func<string, bool>? _excludedTools;
    private readonly IReadOnlyDictionary<string, AgentTool>? _baseToolsOverride;
    private ExtensionEvent.SessionStart _sessionStartEvent;
    private IExtensionUIContext? _extensionUIContext;
    private string _extensionMode = ExtensionMode.Print;
    private ExtensionCommandContextActions? _extensionCommandContextActions;
    private Action? _extensionAbortHandler;
    private ShutdownHandler? _extensionShutdownHandler;
    private ExtensionErrorListener? _extensionErrorListener;
    private Action? _extensionErrorUnsubscriber;

    private readonly ModelRuntime _modelRuntime;
    private readonly CacheWarmer? _cacheWarmer;

    // Tool registry for extension getTools/setTools
    private Dictionary<string, AgentTool> _toolRegistry = new(StringComparer.Ordinal);

    /// <summary>Created on the first <c>ctx.executeTool()</c> call.</summary>
    private NestedToolCallRunner? _nestedToolCalls;

    /// <summary>Declared tools whose declarations requests leave out, from <c>prepareLoadout</c> hooks.</summary>
    private IReadOnlySet<string> _hiddenDeclarations = new HashSet<string>(StringComparer.Ordinal);

    private Dictionary<string, ToolDefinitionEntry> _toolDefinitions = new(StringComparer.Ordinal);
    private Dictionary<string, string> _toolPromptSnippets = new(StringComparer.Ordinal);
    private Dictionary<string, IReadOnlyList<string>> _toolPromptGuidelines = new(StringComparer.Ordinal);

    private NormalizedBuildSystemPromptOptions _baseSystemPromptOptions = null!;

    /// <summary>Prompt options after before_agent_start mutations for the active run.</summary>
    private NormalizedBuildSystemPromptOptions? _runSystemPromptOptions;

    /// <summary>会话的模型真相（差异 C148）。TS 的 <c>agent.state.model</c>。</summary>
    private ModelSpec? _currentModel;

    private sealed class EntryIdBox
    {
        public string? Id;
    }

    #pragma warning restore CS0169
    #pragma warning restore CS0649
    #pragma warning restore CS0414

    // =========================================================================
    // 构造器（TS 474-514）
    // =========================================================================

    /// <summary>对应 TS <c>constructor(config: AgentSessionConfig)</c>。</summary>
    /// <remarks>
    /// <b>缝（9h-3）</b>：TS 构造器在 <c>_buildRuntime</c> 前调用 6 个 installer
    /// （<c>_installAgentToolHooks</c> / <c>_installAgentNextTurnRefresh</c> /
    /// <c>_installAgentRequestProjection</c> / <c>_installAgentBoundaryHooks</c> /
    /// <c>_installHiddenDeclarationsProjection</c> / <c>_installAgentForcedPromptProjection</c>，
    /// TS 502-507）并订阅 <c>_handleAgentEvent</c>（TS 501）；在 <c>_buildRuntime</c> 后当
    /// <c>initialActiveToolNames === undefined</c> 时调用 <c>_restoreToolsFromTranscript</c>
    /// （TS 513）。以上四者随 9h-3 落地，本批调用点以注释占位——本批测试不经过 agent 循环，
    /// 故无观测差异。
    /// </remarks>
    public AgentSession(AgentSessionConfig config)
    {
        _agent = config.Agent;
        SessionManager = config.SessionManager;
        SettingsManager = config.SettingsManager;
        _scopedModels = config.ScopedModels is not null ? [.. config.ScopedModels] : [];
        _resourceLoader = config.ResourceLoader;
        _customTools = config.CustomTools is not null ? [.. config.CustomTools] : [];
        _cwd = config.Cwd;
        _modelRuntime = config.ModelRuntime;
        _cacheWarmer = config.CacheWarmer;
        // TS: this._cacheWarmer.onWarmed = (entry) => this._emit({ type: "entry_appended", entry });
        // 端口 CacheWarmer 尚无 onWarmed 字段（9h-1 勘察记录），接线随刷新流批次。
        _extensionRunnerRef = config.ExtensionRunnerRef;
        _initialActiveToolNames = config.InitialActiveToolNames;
        _usesDefaultTools = config.UsesDefaultTools;

        var filter = ToolFilter.From(config.AllowedToolNames, config.ExcludedToolNames);
        _allowedTools = filter.Allowed;
        _allowlistFiltersMcp = filter.AllowlistFiltersMcp;
        _excludedTools = filter.Excluded;

        _baseToolsOverride = config.BaseToolsOverride;
        _sessionStartEvent = config.SessionStartEvent
            ?? new ExtensionEvent.SessionStart(SessionEventReason.Startup);

        // TS 501: this._unsubscribeAgent = this.agent.subscribe(this._handleAgentEvent);   → 9h-3
        // TS 502-507: 6 个 installer 调用                                                  → 9h-3

        BuildRuntime(new BuildRuntimeOptions
        {
            ActiveToolNames = config.InitialActiveToolNames,
            IncludeAllExtensionTools = true,
        });
        // TS 513: if (this._initialActiveToolNames === undefined) this._restoreToolsFromTranscript();
        // → 9h-3（该方法本身也随 9h-3 落地；9h-2c 批次的测试一律显式传 InitialActiveToolNames）。
    }

    // =========================================================================
    // 模型状态（差异 C148 的镜像写入点）
    // =========================================================================

    /// <summary>TS <c>set model</c> 的唯一入口：会话真相 + Agent 镜像一起改。</summary>
    private void SetCurrentModel(ModelSpec? model)
    {
        _currentModel = model;
        _agent.Model = model is not null ? CompatShims.ToModel(model) : new Pi.Ai.Types.Model("unknown", "unknown", "unknown", "unknown");
    }

    /// <summary>TS <c>get model()</c>：当前模型（未选择时 undefined）。</summary>
    public ModelSpec? Model => _currentModel;

    /// <summary>TS <c>get thinkingLevel()</c>。</summary>
    public ThinkingLevel ThinkingLevel => _agent.ThinkingLevel;

    /// <summary>
    /// Under a virtual selection, the physical model and thinking level of the latest successful
    /// response. TS <c>get routedModel()</c>。
    /// </summary>
    public ScopedModel? RoutedModel
    {
        get
        {
            if (_currentModel is null || !VirtualModels.IsVirtualModel(_currentModel))
            {
                return null;
            }

            var latest = VirtualModels.FindLatestResponse(_agent.Messages);
            var physical = latest is null
                ? null
                : _modelRuntime.GetPhysicalModel(latest.Provider ?? string.Empty, latest.Model ?? string.Empty);
            return physical is null ? null : new ScopedModel { Model = physical, ThinkingLevel = latest?.ThinkingLevel };
        }
    }

    // =========================================================================
    // 会话级只读投影（TS 1445-1670 的相关子集）
    // =========================================================================

    /// <summary>TS <c>get isStreaming()</c>：正在跑 agent run 或 run 后续。</summary>
    public bool IsStreaming => _isAgentRunActive;

    /// <summary>TS <c>get isCompacting()</c>：压缩或分支摘要进行中。</summary>
    public bool IsCompacting =>
        _autoCompactionAbortController is not null
        || _compactionAbortController is not null
        || _branchSummaryAbortController is not null;

    /// <summary>TS <c>get isIdle()</c>：无活跃 run、压缩、分支摘要或排队后续。</summary>
    public bool IsIdle => !_isAgentRunActive && !IsCompacting;

    /// <summary>TS <c>get systemPrompt()</c>：含未发给模型的变更的当前有效系统提示词。</summary>
    public string SystemPrompt =>
        SystemPromptApi.BuildSystemPrompt(
            SystemPromptApi.ToBuildSystemPromptOptions(_runSystemPromptOptions ?? _baseSystemPromptOptions));

    /// <summary>TS <c>get retryAttempt()</c>。</summary>
    public int RetryAttempt => _retryAttempt;

    /// <summary>TS <c>get messages()</c>：含 bash / custom 等全部消息。</summary>
    public IReadOnlyList<ChatMessage> Messages => _agent.Messages;

    /// <summary>TS <c>get steeringMode()</c>。</summary>
    public string SteeringMode => _agent.SteeringMode.ToString();

    /// <summary>TS <c>get followUpMode()</c>。</summary>
    public string FollowUpMode => _agent.FollowUpMode.ToString();

    /// <summary>TS <c>get sessionFile()</c>。</summary>
    public string? SessionFile => SessionManager.GetSessionFile();

    /// <summary>TS <c>get sessionId()</c>。</summary>
    public string SessionId => SessionManager.GetSessionId();

    /// <summary>TS <c>get sessionName()</c>。</summary>
    public string? SessionName => SessionManager.GetSessionName();

    /// <summary>TS <c>get scopedModels()</c>。</summary>
    public IReadOnlyList<ScopedModel> ScopedModels => _scopedModels;

    /// <summary>TS <c>setScopedModels()</c>。</summary>
    public void SetScopedModels(IEnumerable<ScopedModel> scopedModels) => _scopedModels = [.. scopedModels];

    /// <summary>TS <c>get promptTemplates()</c>。</summary>
    public IReadOnlyList<PromptTemplate> PromptTemplates => _resourceLoader.Prompts;

    /// <summary>TS <c>get modelRuntime()</c>。</summary>
    public ModelRuntime ModelRuntime => _modelRuntime;

    /// <summary>TS <c>get resourceLoader()</c>。</summary>
    public DefaultResourceLoader ResourceLoader => _resourceLoader;

    /// <summary>TS <c>get pendingMessageCount()</c>（含 steering 与 follow-up）。</summary>
    public int PendingMessageCount => _steeringMessages.Count + _followUpMessages.Count;

    /// <summary>TS <c>getSteeringMessages()</c>。</summary>
    public IReadOnlyList<string> GetSteeringMessages() => _steeringMessages;

    /// <summary>TS <c>getFollowUpMessages()</c>。</summary>
    public IReadOnlyList<string> GetFollowUpMessages() => _followUpMessages;

    /// <summary>TS <c>get cacheWarmer?()</c>（端口以可空属性暴露）。</summary>
    public CacheWarmer? CacheWarmer => _cacheWarmer;

    /// <summary>TS <c>getToolExposure</c> 的实例面：查会话定义表。</summary>
    private string GetToolExposure(string name) =>
        GetToolExposure(_toolDefinitions, name);

    /// <summary>TS <c>_isAllowedTool</code> 的实例面。</summary>
    private bool IsAllowedTool(string name) =>
        IsAllowedTool(name, new ToolFilter(_allowedTools, _excludedTools, _allowlistFiltersMcp));

    /// <summary>TS <c>_isActivatable</c> 的实例面。</summary>
    private bool IsActivatable(string name) =>
        IsActivatable(
            name,
            new ToolFilter(_allowedTools, _excludedTools, _allowlistFiltersMcp),
            _toolDefinitions,
            (IReadOnlySet<string>)_toolRegistry.Keys.ToHashSet(StringComparer.Ordinal));

    /// <summary>TS <c>_isDeclarable</c> 的实例面。</summary>
    private bool IsDeclarable(string name) => IsDeclarable(_toolDefinitions, name);

    /// <summary>TS <c>_isActivatedOnRegistration</c> 的实例面。</summary>
    private bool IsActivatedOnRegistration(string name) =>
        IsActivatedOnRegistration(_toolDefinitions, name);

    // =========================================================================
    // 事件订阅（TS 1027 / 1355）
    // =========================================================================

    /// <summary>TS <c>_emit(event)</c>。</summary>
    private void Emit(AgentSessionEvent @event)
    {
        foreach (var listener in _eventListeners)
        {
            listener(@event);
        }
    }

    /// <summary>TS <c>_emitQueueUpdate()</c>。</summary>
    private void EmitQueueUpdate() =>
        Emit(new AgentSessionEvent.QueueUpdateEvent([.. _steeringMessages], [.. _followUpMessages]));

    /// <summary>TS <c>subscribe(listener)</c>：返回退订函数。</summary>
    public IDisposable Subscribe(AgentSessionEventListener listener)
    {
        _eventListeners.Add(listener);
        return new ListenerUnsubscribe(this, listener);
    }

    private sealed class ListenerUnsubscribe(AgentSession session, AgentSessionEventListener listener) : IDisposable
    {
        public void Dispose() => session._eventListeners.Remove(listener);
    }

    // =========================================================================
    // 空闲等待（TS 1047-1061 + 2432-2437）
    // =========================================================================

    /// <summary>TS <c>_getIdleWaitPromise()</c>。</summary>
    private Task GetIdleWaitTask()
    {
        _idleWaitTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _idleWaitTcs.Task;
    }

    /// <summary>TS <c>_resolveIdleWaitIfIdle()</c>。</summary>
    private void ResolveIdleWaitIfIdle()
    {
        if (!IsIdle || _idleWaitTcs is null)
        {
            return;
        }

        var tcs = _idleWaitTcs;
        _idleWaitTcs = null;
        _ = tcs.TrySetResult();
    }

    /// <summary>TS <c>waitForIdle()</c>。</summary>
    public Task WaitForIdleAsync() => IsIdle ? Task.CompletedTask : GetIdleWaitTask();

    // =========================================================================
    // 工具注册表（TS 1469-1606 的实例面）
    // =========================================================================

    /// <summary>
    /// Get the names of currently active tools, which are the tools declared to the model.
    /// Tools with <c>codemode</c> or <c>deferred</c> exposure are callable from other tools without
    /// being active. TS <c>getActiveToolNames()</c>。
    /// </summary>
    public IReadOnlyList<string> GetActiveToolNames() => [.. _agent.Tools.Select(tool => tool.Name)];

    /// <summary>Get the names of the tools that tools can call through <c>ctx.executeTool()</c>. TS <c>getCallableToolNames()</c>.</summary>
    public IReadOnlyList<string> GetCallableToolNames() => [.. GetCallableTools().Select(tool => tool.Name)];

    /// <summary>
    /// Get all configured tools with name, description, parameter schema, prompt guidelines, and
    /// source metadata. TS <c>getAllTools()</c>。
    /// </summary>
    public IReadOnlyList<ToolInfo> GetAllTools()
    {
        var result = new List<ToolInfo>(_toolDefinitions.Count);
        foreach (var entry in _toolDefinitions.Values)
        {
            var definition = entry.Definition;
            result.Add(new ToolInfo
            {
                Name = definition.Name,
                Description = definition.Description,
                Parameters = definition.Parameters,
                PromptGuidelines = definition.PromptGuidelines,
                Exposure = GetToolExposure(definition.Name),
                Namespace = definition.Namespace,
                Annotations = definition.Annotations,
                SourceInfo = entry.SourceInfo,
            });
        }

        return result;
    }

    /// <summary>TS <c>getToolDefinition(name)</c>。</summary>
    public ToolDefinition? GetToolDefinition(string name) =>
        _toolDefinitions.TryGetValue(name, out var entry) ? entry.Definition : null;

    /// <summary>
    /// Set active tools by name. Only tools in the registry can be enabled. Unknown and hidden tool
    /// names are ignored. Also rebuilds the system prompt to reflect the new tool set. Changes take
    /// effect on the next agent turn. TS <c>setActiveToolsByName()</c>。
    /// </summary>
    public void SetActiveToolsByName(IReadOnlyList<string> toolNames)
    {
        var previous = GetActiveToolNames();
        SetActiveTools(toolNames);
        // A loadout that deactivates a tool replaces the restored one, whose pending tools are
        // dropped. One that only adds tools, like activating tool_search, keeps them.
        var active = new HashSet<string>(GetActiveToolNames(), StringComparer.Ordinal);
        if (previous.Any(name => !active.Contains(name)))
        {
            _pendingToolNames.Clear();
        }
    }

    /// <summary>TS <c>_setActiveTools()</c>。</summary>
    private void SetActiveTools(IReadOnlyList<string> toolNames)
    {
        var tools = ApplyToolLoadout(toolNames);
        foreach (var tool in tools)
        {
            _pendingToolNames.Remove(tool.Name);
        }

        RebuildSystemPrompt([.. tools.Select(tool => tool.Name)]);
    }

    /// <summary>
    /// Tools callable through <c>ctx.executeTool()</c>: the active <c>direct</c> tools and every
    /// registered <c>codemode</c> or <c>deferred</c> tool. TS <c>_getCallableTools()</c>。
    /// </summary>
    private IReadOnlyList<AgentTool> GetCallableTools()
    {
        var active = new HashSet<string>(GetActiveToolNames(), StringComparer.Ordinal);
        var result = new List<AgentTool>();
        foreach (var tool in _toolRegistry.Values)
        {
            var exposure = GetToolExposure(tool.Name);
            if (exposure == ToolExposure.Codemode
                || exposure == ToolExposure.Deferred
                || (exposure == ToolExposure.Direct && active.Contains(tool.Name)))
            {
                result.Add(tool);
            }
        }

        return result;
    }

    /// <summary>
    /// Set the agent's tools for the given active tool names and return them. The active tools are
    /// the registered, non-hidden ones; they are declared to the model. Active tools with a
    /// <c>prepareLoadout</c> hook can change the declared descriptions and hide declarations from
    /// requests. TS <c>_applyToolLoadout()</c>。
    /// </summary>
    /// <remarks>
    /// 复用 9a 的纯决策 <see cref="ApplyToolLoadout"/>；差异只有两处实例副作用：
    /// <c>_hiddenDeclarations</c> 与 <c>agent.state.tools</c>（差异 C148 的 Agent 镜像）。
    /// </remarks>
    private IReadOnlyList<AgentTool> ApplyToolLoadout(IReadOnlyList<string> toolNames)
    {
        var application = ApplyToolLoadout(
            toolNames,
            _toolRegistry,
            _toolDefinitions,
            new ToolFilter(_allowedTools, _excludedTools, _allowlistFiltersMcp),
            error => _extensionRunner.EmitError(new ExtensionError(error.ExtensionPath, error.Event, error.Error, error.Stack)));
        _hiddenDeclarations = application.HiddenDeclarations;
        _agent.Tools = application.Declared;
        return application.Declared;
    }

    // =========================================================================
    // 系统提示词（TS 1686-1711）
    // =========================================================================

    /// <summary>TS <c>_rebuildSystemPrompt(toolNames)</c>。</summary>
    /// <remarks>
    /// <b>缝（loader 侧顺延）</b>：TS 读 <c>resourceLoader.getSystemPrompt()</c> 与
    /// <c>getAppendSystemPrompt()</c>；端口的 <see cref="DefaultResourceLoader"/> 尚未落地这两条
    /// 发现通路（9h-2b-2 顺延项），本批以 <c>null</c> / 空串传入——与 loader 在两者为空时的
    /// TS 结果一致。
    /// </remarks>
    private void RebuildSystemPrompt(IReadOnlyList<string> toolNames)
    {
        var validToolNames = new List<string>();
        foreach (var name in toolNames)
        {
            if (_toolRegistry.ContainsKey(name))
            {
                validToolNames.Add(name);
            }
        }

        var toolSnippets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in _toolRegistry.Keys)
        {
            // Tools without a snippet are not listed.
            if (_toolPromptSnippets.TryGetValue(name, out var snippet))
            {
                toolSnippets[name] = snippet;
            }
        }

        // 缝：loader 的 systemPrompt / appendSystemPrompt 发现随 loader 批次落地（见 remarks）。
        const string? loaderSystemPrompt = null;
        const string appendSystemPrompt = "";
        var loadedSkills = _resourceLoader.Skills;
        var loadedContextFiles = _resourceLoader.AgentsFiles;

        _baseSystemPromptOptions = SystemPromptApi.NormalizeBuildSystemPromptOptions(new BuildSystemPromptOptions
        {
            Cwd = _cwd,
            Skills = loadedSkills,
            ContextFiles = loadedContextFiles,
            CustomPrompt = loaderSystemPrompt,
            AppendSystemPrompt = appendSystemPrompt,
            SelectedTools = validToolNames,
            HiddenTools = [.. _hiddenDeclarations],
            ToolSnippets = toolSnippets,
            ToolGuidelines = _toolPromptGuidelines,
        });
    }

    // =========================================================================
    // 运行时装配（TS 3592-3644 / 3481-3579 / 3322-3344）
    // =========================================================================

    /// <summary>TS <c>_buildRuntime(options)</c>：所有实例方法的汇聚前置。</summary>
    private void BuildRuntime(BuildRuntimeOptions options)
    {
        var autoResizeImages = SettingsManager.GetImageAutoResize();
        var shellCommandPrefix = SettingsManager.GetShellCommandPrefix();
        var shellPath = SettingsManager.GetShellPath();
        Dictionary<string, ToolDefinition> baseToolDefinitions;
        if (_baseToolsOverride is not null)
        {
            baseToolDefinitions = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);
            foreach (var (name, tool) in _baseToolsOverride)
            {
                baseToolDefinitions[name] = ToolDefinitionWrapper.CreateToolDefinitionFromAgentTool(tool);
            }
        }
        else
        {
            baseToolDefinitions = ToolIndex.CreateAllToolDefinitions(_cwd, new ToolsOptions
            {
                Read = new ReadToolOptions { AutoResizeImages = autoResizeImages },
                Bash = new BashToolOptions { CommandPrefix = shellCommandPrefix, ShellPath = shellPath },
            });
        }

        _baseToolDefinitions = baseToolDefinitions;

        var extensionsResult = _resourceLoader.GetExtensions();
        if (options.FlagValues is not null)
        {
            foreach (var (name, value) in options.FlagValues)
            {
                extensionsResult.Runtime.FlagValues[name] = value;
            }
        }

        _extensionRunner = new ExtensionRunner(
            extensionsResult.Extensions,
            extensionsResult.Runtime,
            _cwd,
            SessionManager,
            new ModelRegistry(_modelRuntime));
        if (_extensionRunnerRef is not null)
        {
            _extensionRunnerRef.Current = _extensionRunner;
        }

        BindExtensionCore(_extensionRunner);
        ApplyExtensionBindings(_extensionRunner);

        var baseActiveToolNames = ResolveBaseActiveToolNames(
            options.ActiveToolNames,
            _baseToolsOverride is not null ? [.. _baseToolsOverride.Keys] : null);
        RefreshToolRegistry(new RefreshToolRegistryOptions
        {
            ActiveToolNames = baseActiveToolNames,
            IncludeAllExtensionTools = options.IncludeAllExtensionTools,
        });
    }

    /// <summary>TS <c>_refreshToolRegistry(options?)</c>。</summary>
    private void RefreshToolRegistry(RefreshToolRegistryOptions? options = null)
    {
        // Tools that were already activated on registration. A tool whose exposure changes to
        // `direct` or `model-only` (for example from `hidden`) is activated like a new tool.
        var previousActivatedOnRegistration = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in _toolRegistry.Keys)
        {
            if (IsActivatedOnRegistration(name))
            {
                previousActivatedOnRegistration.Add(name);
            }
        }

        var previousActiveToolNames = GetActiveToolNames();
        var filter = new ToolFilter(_allowedTools, _excludedTools, _allowlistFiltersMcp);

        var registeredTools = _extensionRunner.GetAllRegisteredTools();
        var allCustomTools = new List<RegisteredTool>(registeredTools.Count + _customTools.Count);
        foreach (var registered in registeredTools)
        {
            allCustomTools.Add(registered);
        }

        foreach (var definition in _customTools)
        {
            allCustomTools.Add(new RegisteredTool
            {
                Definition = definition,
                SourceInfo = SourceInfos.CreateSynthetic($"<sdk:{definition.Name}>", "sdk"),
            });
        }

        allCustomTools.RemoveAll(tool => !IsAllowedTool(tool.Definition.Name));

        var customEntries = new List<ToolDefinitionEntry>(allCustomTools.Count);
        foreach (var tool in allCustomTools)
        {
            customEntries.Add(new ToolDefinitionEntry { Definition = tool.Definition, SourceInfo = tool.SourceInfo });
        }

        var definitionRegistry = (Dictionary<string, ToolDefinitionEntry>)BuildDefinitionRegistry(
            _baseToolDefinitions,
            customEntries,
            filter);
        _toolDefinitions = definitionRegistry;
        _toolPromptSnippets = (Dictionary<string, string>)BuildPromptSnippets(definitionRegistry.Values);
        _toolPromptGuidelines = (Dictionary<string, IReadOnlyList<string>>)BuildPromptGuidelines(
            definitionRegistry.Values);

        var wrappedExtensionTools = Wrapper.WrapRegisteredTools(allCustomTools, _extensionRunner);
        var builtinEntries = new List<RegisteredTool>(_baseToolDefinitions.Count);
        foreach (var (name, definition) in _baseToolDefinitions)
        {
            if (IsAllowedTool(name))
            {
                builtinEntries.Add(new RegisteredTool
                {
                    Definition = definition,
                    SourceInfo = SourceInfos.CreateSynthetic(SourceInfos.BuiltinPathPrefix + name, "builtin"),
                });
            }
        }

        var wrappedBuiltInTools = Wrapper.WrapRegisteredTools(builtinEntries, _extensionRunner);

        var toolRegistry = new Dictionary<string, AgentTool>(StringComparer.Ordinal);
        foreach (var tool in wrappedBuiltInTools)
        {
            toolRegistry[tool.Name] = tool;
        }

        foreach (var tool in wrappedExtensionTools)
        {
            toolRegistry[tool.Name] = tool;
        }

        _toolRegistry = toolRegistry;

        var nextActiveToolNames = ResolveNextActiveToolNames(
            options?.ActiveToolNames,
            previousActiveToolNames,
            [.. toolRegistry.Keys],
            [.. wrappedExtensionTools.Select(tool => tool.Name)],
            filter,
            options?.IncludeAllExtensionTools ?? false,
            previousActivatedOnRegistration,
            [.. _pendingToolNames],
            _toolDefinitions);

        SetActiveTools(nextActiveToolNames);
    }

    /// <summary>TS <c>_applyExtensionBindings(runner)</c>。</summary>
    private void ApplyExtensionBindings(ExtensionRunner runner)
    {
        runner.SetUIContext(_extensionUIContext, _extensionMode);
        runner.BindCommandContext(_extensionCommandContextActions);

        _extensionErrorUnsubscriber?.Invoke();
        _extensionErrorUnsubscriber = _extensionErrorListener is not null
            ? runner.OnError(_extensionErrorListener)
            : null;
    }

    /// <summary>TS <c>_refreshCurrentModelFromRegistry()</c>：注册表变化后把当前模型换回活对象。</summary>
    private void RefreshCurrentModelFromRegistry()
    {
        var currentModel = Model;
        if (currentModel is null)
        {
            return;
        }

        var refreshedModel = _modelRuntime.GetModel(currentModel.Provider, currentModel.Id);
        if (!ShouldRefreshModelFromRegistry(
                currentModel is not null,
                refreshedModel is not null,
                ReferenceEquals(refreshedModel, currentModel)))
        {
            return;
        }

        SetCurrentModel(refreshedModel);
    }

    // =========================================================================
    // 扩展核心绑定（TS 3346-3479）
    // =========================================================================

    /// <summary>TS <c>_bindExtensionCore(runner)</c>：把会话能力装进扩展运行时的三组上下文。</summary>
    private void BindExtensionCore(ExtensionRunner runner)
    {
        IReadOnlyList<SlashCommandInfo> GetCommands()
        {
            var extensionCommands = new List<RegisteredExtensionCommand>();
            foreach (var command in runner.GetRegisteredCommands())
            {
                extensionCommands.Add(new RegisteredExtensionCommand(
                    command.InvocationName,
                    command.Description,
                    command.SourceInfo));
            }

            return BuildSlashCommandInfos(extensionCommands, _resourceLoader.Prompts, _resourceLoader.Skills);
        }

        runner.BindCore(
            new ExtensionActions
            {
                SendMessage = (message, sendMessageOptions) =>
                {
                    _ = SendCustomMessageAsync(message, sendMessageOptions).ContinueWith(
                        task => runner.EmitError(new ExtensionError(
                            "<runtime>",
                            "send_message",
                            task.Exception?.GetBaseException().Message ?? "unknown")),
                        TaskScheduler.Default);
                },
                SendUserMessage = (content, sendUserMessageOptions) =>
                {
                    _ = SendUserMessageAsync(content, sendUserMessageOptions).ContinueWith(
                        task => runner.EmitError(new ExtensionError(
                            "<runtime>",
                            "send_user_message",
                            task.Exception?.GetBaseException().Message ?? "unknown")),
                        TaskScheduler.Default);
                },
                AppendEntry = (customType, data) =>
                {
                    var entryId = SessionManager.AppendCustomEntry(customType, data);
                    var entry = SessionManager.GetEntry(entryId);
                    if (entry is not null)
                    {
                        Emit(new AgentSessionEvent.EntryAppendedEvent(entry));
                    }
                },
                SetSessionName = SetSessionName,
                GetSessionName = () => SessionManager.GetSessionName(),
                SetLabel = (entryId, label) => SessionManager.AppendLabelChange(entryId, label),
                GetActiveTools = GetActiveToolNames,
                GetAllTools = GetAllTools,
                GetSettings = () => SettingsManager.GetSettings(),
                SetActiveTools = SetActiveToolsByName,
                RefreshTools = () => RefreshToolRegistry(),
                GetCommands = GetCommands,
                SetModel = async model =>
                {
                    if (!_modelRuntime.HasConfiguredAuth(model.Provider))
                    {
                        return false;
                    }

                    // 差异 C150：ExtensionApi 的 SetModelHandler 携带瘦 Model（Pi.Ai.Types.Model），
                    // 而会话的模型真相是 ModelSpec（差异 C148）。扩展的模型来自 ctx.getModel()（返回
                    // 目录中的活 ModelSpec），故此处按 provider/id 回查目录换回 ModelSpec，主链路上
                    // 与 TS 的同对象语义等价；目录中不存在时显式抛错。
                    var spec = _modelRuntime.GetModel(model.Provider, model.Id)
                        ?? throw new InvalidOperationException($"Unknown model: {model.Provider}/{model.Id}");
                    await SetModelAsync(spec);
                    return true;
                },
                GetThinkingLevel = () => ThinkingLevel,
                SetThinkingLevel = level => SetThinkingLevel(level),
            },
            new ExtensionContextActions
            {
                GetModel = () => Model,
                GetScopedModels = () => _scopedModels,
                IsIdle = () => IsIdle,
                IsProjectTrusted = () => SettingsManager.IsProjectTrusted(),
                GetSignal = () => _agent.Signal,
                Abort = () =>
                {
                    if (_extensionAbortHandler is not null)
                    {
                        _extensionAbortHandler();
                        return;
                    }

                    _ = AbortAsync();
                },
                HasPendingMessages = () => PendingMessageCount > 0,
                Shutdown = () => _extensionShutdownHandler?.Invoke(),
                GetContextUsage = () => GetContextUsage(),
                Compact = compactOptions => _ = CompactAsync(compactOptions),
                GetSystemPrompt = () => SystemPrompt,
                GetSystemPromptOptions = () => SystemPromptApi.ToBuildSystemPromptOptions(_baseSystemPromptOptions),
                ExecuteTool = (callerId, name, args, executeOptions) => ExecuteNestedToolCallAsync(callerId, name, args, executeOptions),
                GetCallableTools = GetCallableTools,
            },
            new ProviderRegistrationActions
            {
                RegisterProvider = (name, providerConfig) =>
                {
                    _modelRuntime.RegisterProvider(name, providerConfig);
                    RefreshCurrentModelFromRegistry();
                },
                RegisterNativeProvider = provider =>
                {
                    _modelRuntime.RegisterNativeProvider(provider);
                    RefreshCurrentModelFromRegistry();
                },
                UnregisterProvider = name =>
                {
                    _modelRuntime.UnregisterProvider(name);
                    RefreshCurrentModelFromRegistry();
                },
                RegisterVirtualModel = definition =>
                {
                    _modelRuntime.RegisterVirtualModel(definition);
                    RefreshCurrentModelFromRegistry();
                },
                UnregisterVirtualModel = (provider, id) =>
                {
                    _modelRuntime.UnregisterVirtualModel(provider, id);
                    RefreshCurrentModelFromRegistry();
                },
            });
    }

    // =========================================================================
    // 扩展绑定（TS 3244-3292）
    // =========================================================================

    /// <summary>TS <c>bindExtensions(bindings)</c>。</summary>
    public async Task BindExtensionsAsync(ExtensionBindings bindings)
    {
        if (bindings.UiContext is not null)
        {
            _extensionUIContext = bindings.UiContext;
        }

        if (bindings.Mode is not null)
        {
            _extensionMode = bindings.Mode;
        }

        if (bindings.CommandContextActions is not null)
        {
            _extensionCommandContextActions = bindings.CommandContextActions;
        }

        if (bindings.AbortHandler is not null)
        {
            _extensionAbortHandler = bindings.AbortHandler;
        }

        if (bindings.ShutdownHandler is not null)
        {
            _extensionShutdownHandler = bindings.ShutdownHandler;
        }

        if (bindings.OnError is not null)
        {
            _extensionErrorListener = bindings.OnError;
        }

        ApplyExtensionBindings(_extensionRunner);
        await _extensionRunner.EmitAsync(_sessionStartEvent);
        _extensionRunner.ReportUnhandledMcpServers();
        await ExtendResourcesFromExtensions(_sessionStartEvent.Reason == SessionEventReason.Reload ? "reload" : "startup");
    }

    /// <summary>TS <c>extendResourcesFromExtensions(reason)</c>。</summary>
    private async Task ExtendResourcesFromExtensions(string reason)
    {
        if (!_extensionRunner.HasHandlers("resources_discover"))
        {
            return;
        }

        var discovered = await _extensionRunner.EmitResourcesDiscoverAsync(_cwd, reason);
        if (discovered.SkillPaths.Count == 0 && discovered.PromptPaths.Count == 0 && discovered.ThemePaths.Count == 0)
        {
            return;
        }

        var extensionPaths = new ResourceExtensionPaths
        {
            SkillPaths = BuildExtensionResourcePaths(ToResourceEntries(discovered.SkillPaths)),
            PromptPaths = BuildExtensionResourcePaths(ToResourceEntries(discovered.PromptPaths)),
            ThemePaths = BuildExtensionResourcePaths(ToResourceEntries(discovered.ThemePaths)),
        };

        _resourceLoader.ExtendResources(extensionPaths);
        RebuildSystemPrompt(GetActiveToolNames());
    }

    /// <summary>
    /// <see cref="AttributedResourcePath"/>（runner 侧形状）→ <see cref="ExtensionResourceEntry"/>
    /// （资源路径映射的入参形状）；两者字段一致（path / extensionPath），TS 靠结构类型直通。
    /// </summary>
    private static IReadOnlyList<ExtensionResourceEntry> ToResourceEntries(
        IReadOnlyList<AttributedResourcePath> paths)
        => [.. paths.Select(path => new ExtensionResourceEntry(path.Path, path.ExtensionPath))];

    // =========================================================================
    // 会话名 / 队列（TS 3927-3932 / 2383-2411）
    // =========================================================================

    /// <summary>TS <c>setSessionName(name)</c>。</summary>
    public void SetSessionName(string name)
    {
        SessionManager.AppendSessionInfo(name);
        var @event = new AgentSessionEvent.SessionInfoChangedEvent(SessionManager.GetSessionName());
        Emit(@event);
        _ = _extensionRunner.EmitAsync(new ExtensionEvent.SessionInfoChanged(SessionManager.GetSessionName()));
    }

    /// <summary>
    /// Clear all queued messages and return them. Useful for restoring to editor when user aborts.
    /// TS <c>clearQueue()</c>。
    /// </summary>
    public (IReadOnlyList<string> Steering, IReadOnlyList<string> FollowUp) ClearQueue()
    {
        var steering = _steeringMessages.ToArray();
        var followUp = _followUpMessages.ToArray();
        _steeringMessages.Clear();
        _followUpMessages.Clear();
        _agent.ClearAllQueues();
        EmitQueueUpdate();
        return (steering, followUp);
    }

    // =========================================================================
    // 模型管理（TS 2443-2497 / 2598-2676）
    // =========================================================================

    /// <summary>TS <c>_emitModelSelect(nextModel, previousModel, source)</c>。</summary>
    private async Task EmitModelSelectAsync(ModelSpec nextModel, ModelSpec? previousModel, string source)
    {
        if (ModelOperations.ModelsAreEqual(previousModel, nextModel))
        {
            return;
        }

        await _extensionRunner.EmitAsync(new ExtensionEvent.ModelSelect(
            CompatShims.ToModel(nextModel),
            previousModel is null ? null : CompatShims.ToModel(previousModel),
            source));
    }

    /// <summary>
    /// Set model directly. Validates that auth is configured and saves to the session transcript.
    /// Persists to global defaults only when options.persist is true. TS <c>setModel()</c>。
    /// </summary>
    /// <exception cref="InvalidOperationException">No auth is configured for the model.</exception>
    public async Task SetModelAsync(ModelSpec model, ModelMutationOptions? options = null)
    {
        if (await _modelRuntime.CheckAuthAsync(model.Provider) is null)
        {
            throw new InvalidOperationException($"No API key for {model.Provider}/{model.Id}");
        }

        var previousModel = Model;
        var thinkingLevel = GetThinkingLevelForModelSwitch(model);
        SetCurrentModel(model);
        SessionManager.AppendModelChange(model.Provider, model.Id);
        if (options?.Persist == true)
        {
            SettingsManager.SetDefaultModelAndProvider(model.Provider, model.Id);
            AddPersistedDefaultToNonEmptyScope(model);
        }

        // Apply thinking level for the new model.
        // Per-model thinking level overrides take priority over the global default.
        // Model persistence does not implicitly rewrite the global thinking default.
        SetThinkingLevel(thinkingLevel);

        await EmitModelSelectAsync(model, previousModel, "set");
    }

    /// <summary>TS <c>_addPersistedDefaultToNonEmptyScope(model)</c>。</summary>
    private void AddPersistedDefaultToNonEmptyScope(ModelSpec model)
    {
        if (_scopedModels.Count == 0)
        {
            return;
        }

        if (_scopedModels.Any(scoped => ModelOperations.ModelsAreEqual(scoped.Model, model)))
        {
            return;
        }

        _scopedModels = [.. _scopedModels, new ScopedModel { Model = model }];

        var enabledModels = SettingsManager.GetEnabledModels();
        if (enabledModels is not { Count: > 0 })
        {
            return;
        }

        var modelReference = $"{model.Provider}/{model.Id}";
        if (enabledModels.Any(pattern => string.Equals(pattern, modelReference, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        SettingsManager.SetEnabledModels([.. enabledModels, modelReference]);
    }

    /// <summary>TS <c>setThinkingLevel(level, options)</c>。</summary>
    public void SetThinkingLevel(ThinkingLevel level, ModelMutationOptions? options = null)
    {
        var availableLevels = GetAvailableThinkingLevels();
        var effectiveLevel = availableLevels.Contains(level) ? level : ClampThinkingLevel(level, availableLevels);

        // Only persist if actually changing
        var previousLevel = _agent.ThinkingLevel;
        var isChanging = effectiveLevel != previousLevel;

        _agent.ThinkingLevel = effectiveLevel;

        if (options?.Persist == true)
        {
            SettingsManager.SetDefaultThinkingLevel(level);
        }

        if (isChanging)
        {
            SessionManager.AppendThinkingLevelChange(Core.ThinkingLevels.ToText(effectiveLevel));
            Emit(new AgentSessionEvent.ThinkingLevelChangedEvent(effectiveLevel));
            _ = _extensionRunner.EmitAsync(new ExtensionEvent.ThinkingLevelSelect(effectiveLevel, previousLevel));
        }
    }

    /// <summary>
    /// Get available thinking levels for current model. The provider will clamp to what the specific
    /// model supports internally. TS <c>getAvailableThinkingLevels()</c>。
    /// </summary>
    public IReadOnlyList<ThinkingLevel> GetAvailableThinkingLevels()
    {
        if (_currentModel is null)
        {
            return Defaults.ThinkingLevelOptions;
        }

        var supported = Pi.Ai.Models.ThinkingLevels.GetSupported(_currentModel);
        var result = new List<ThinkingLevel>(supported.Count);
        foreach (var text in supported)
        {
            if (Core.ThinkingLevels.Parse(text) is { } level)
            {
                result.Add(level);
            }
        }

        return result;
    }

    /// <summary>Check if current model supports thinking/reasoning. TS <c>supportsThinking()</c>。</summary>
    public bool SupportsThinking() => _currentModel?.Reasoning == true;

    /// <summary>TS <c>_getThinkingLevelForModelSwitch(targetModel?, explicitLevel?)</c>。</summary>
    private ThinkingLevel GetThinkingLevelForModelSwitch(ModelSpec? targetModel, ThinkingLevel? explicitLevel = null)
    {
        if (explicitLevel is not null)
        {
            return explicitLevel.Value;
        }

        // Per-model default takes priority when switching to a model that has one
        if (targetModel is not null)
        {
            var perModel = Core.ThinkingLevels.Parse(SettingsManager.GetModelThinkingLevel(targetModel.Provider, targetModel.Id));
            if (perModel is not null)
            {
                return perModel.Value;
            }
        }

        return ThinkingLevels.Parse(SettingsManager.GetDefaultThinkingLevel())
            ?? _agent.ThinkingLevel;
    }

    /// <summary>TS <c>_clampThinkingLevel(level, _availableLevels)</c>。</summary>
    private ThinkingLevel ClampThinkingLevel(ThinkingLevel level, IReadOnlyList<ThinkingLevel> _)
    {
        if (_currentModel is null)
        {
            return ThinkingLevel.Off;
        }

        return Core.ThinkingLevels.Parse(Pi.Ai.Models.ThinkingLevels.Clamp(_currentModel, Core.ThinkingLevels.ToText(level)))
            ?? ThinkingLevel.Off;
    }

    // =========================================================================
    // 上下文用量（TS getContextUsage 实例面）
    // =========================================================================

    /// <summary>TS <c>getContextUsage()</c>：以 <c>_limitsModel()</c> 的上下文窗口为基准。</summary>
    public ContextUsage? GetContextUsage()
    {
        var limitsModel = RoutedModel?.Model ?? Model;
        var contextWindow = limitsModel?.ContextWindow ?? 0;
        return GetContextUsage(
            contextWindow,
            SessionManager.BuildSessionProjection(),
            SessionManager.GetBranch());
    }

    // =========================================================================
    // 中止与空闲（TS 2417-2437 / 3792-3796 / 2900-2910）
    // =========================================================================

    /// <summary>Abort current operation and wait for agent to become idle. TS <c>abort()</c>。</summary>
    public async Task AbortAsync()
    {
        if (_isAgentRunActive)
        {
            _agentRunAbortRequested = true;
        }

        AbortRetry();
        AbortCompaction();
        AbortBranchSummary();
        if (_isBeforeSettle)
        {
            _abortDuringBeforeSettle = true;
        }

        _agent.Abort();
        await WaitForIdleAsync();
    }

    /// <summary>Cancel in-progress auto-retry. TS <c>abortRetry()</c>。</summary>
    public void AbortRetry() => _retryAbortController?.Cancel();

    /// <summary>Cancel in-progress compaction (manual or auto). TS <c>abortCompaction()</c>。</summary>
    public void AbortCompaction()
    {
        _compactionAbortController?.Cancel();
        _autoCompactionAbortController?.Cancel();
    }

    /// <summary>Cancel in-progress branch summarization. TS <c>abortBranchSummary()</c>。</summary>
    public void AbortBranchSummary() => _branchSummaryAbortController?.Cancel();

    // =========================================================================
    // 缝（后续批次落地；调用即显式抛错，不静默）
    // =========================================================================

    /// <summary>
    /// <b>缝（9h-3）</b>：TS <c>_executeNestedToolCall()</c>（TS 718-746）。其 <c>runToolCall</c> 回调
    /// 依赖 <c>_beforeToolCall</c> / <c>_afterToolCall</c>（TS 640-776，installer 系），随 9h-3 落地。
    /// </summary>
    internal Task<AgentToolCallOutcome> ExecuteNestedToolCallAsync(
        string callerId,
        string name,
        object? args,
        ExecuteToolOptions options)
        => throw new NotSupportedException("AgentSession._executeNestedToolCall lands with 4e-3e-9h-3 (installer family).");

    /// <summary>
    /// <b>缝（9h-4）</b>：TS <c>compact()</c>（TS 2750-2895）。其执行半边依赖
    /// <c>_runDefaultCompaction</c> / <c>_getSummarizationRequestAuth</c> 请求链，随 9h-4 落地。
    /// </summary>
    internal Task<CompactionResult> CompactAsync(CompactOptions? options)
        => throw new NotSupportedException("AgentSession.compact lands with 4e-3e-9h-4 (orchestration body).");

    /// <summary>
    /// <b>缝（9h-4）</b>：TS <c>_runAgentPrompt()</c>（TS 1808-1837）与 <c>prompt()</c>
    /// （TS 1954-2096），随 9h-4 编排本体落地。
    /// </summary>
    internal Task RunAgentPromptAsync(params ChatMessage[] messages)
        => throw new NotSupportedException("AgentSession._runAgentPrompt / prompt land with 4e-3e-9h-4.");

    /// <summary><b>缝（9h-4）</b>：TS <c>prompt(text, options?)</c>。</summary>
    public Task PromptAsync(string text, PromptOptions? options = null)
        => RunAgentPromptAsync();

    /// <summary>
    /// Send a user message to the agent. Always triggers a turn. TS <c>sendUserMessage()</c>
    /// （TS 2351-2381）——归一化半边已落地，触发半边经 <see cref="PromptAsync"/>（9h-4 缝）。
    /// </summary>
    public Task SendUserMessageAsync(MessageContent content, SendUserMessageOptions? options = null)
    {
        // Normalize content to text string + optional images
        string text;
        IReadOnlyList<ImageContent>? images;
        if (content is MessageContent.Text single)
        {
            text = single.Value;
            images = null;
        }
        else
        {
            var textParts = new List<string>();
            var imageParts = new List<ImageContent>();
            foreach (var part in content is MessageContent.Blocks blocks ? blocks.Value : [])
            {
                if (part is TextContent textPart)
                {
                    textParts.Add(textPart.Text);
                }
                else if (part is ImageContent imagePart)
                {
                    imageParts.Add(imagePart);
                }
            }

            text = string.Join("\n", textParts);
            images = imageParts.Count == 0 ? null : imageParts;
        }

        return PromptAsync(text, new PromptOptions
        {
            ExpandPromptTemplates = options?.ExpandPromptTemplates ?? false,
            StreamingBehavior = options?.DeliverAs,
            Images = images,
            Source = "extension",
        });
    }

    /// <summary>
    /// Send a custom message. TS <c>sendCustomMessage()</c>（TS 2279-2315）——除 triggerTurn 分支外
    /// 全部落；triggerTurn 分支经 <see cref="RunAgentPromptAsync"/>（9h-4 缝）。
    /// </summary>
    public Task SendCustomMessageAsync(CustomMessageDraft message, SendMessageOptions? options = null)
    {
        var appMessage = new CustomMessage(
            message.CustomType,
            message.Content,
            message.Display,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            Details = message.Details,
        };
        if (options?.DeliverAs == "nextTurn")
        {
            _pendingNextTurnMessages.Add(appMessage);
            return Task.CompletedTask;
        }

        if (IsStreaming && options?.TriggerTurn != false)
        {
            if (options?.DeliverAs == "followUp")
            {
                _agent.FollowUp(appMessage);
            }
            else
            {
                _agent.Steer(appMessage);
            }

            return Task.CompletedTask;
        }

        if (options?.TriggerTurn == true)
        {
            if (_isEmittingAgentSettled)
            {
                _deferredSettledActions.Add(() => RunAgentPromptAsync(appMessage));
                return Task.CompletedTask;
            }

            return RunAgentPromptAsync(appMessage);
        }

        if (IsStreaming)
        {
            // Appending now would put the message between an assistant tool call and its result,
            // which providers that validate message order reject on replay. Defer to the end of the
            // turn. Nothing is emitted yet: message events must not describe messages the session
            // tree does not contain.
            _pendingCustomMessages.Add(appMessage);
            return Task.CompletedTask;
        }

        AppendCustomMessage(appMessage);
        return Task.CompletedTask;
    }

    /// <summary>TS <c>_appendCustomMessage(appMessage)</c>。</summary>
    private void AppendCustomMessage(CustomMessage appMessage)
    {
        SessionManager.AppendCustomMessageEntry(
            appMessage.CustomType,
            appMessage.Content,
            appMessage.Display,
            appMessage.Details);
        RefreshFinalizedContext();
        Emit(new AgentSessionEvent.MessageStartEvent(appMessage));
        Emit(new AgentSessionEvent.MessageEndEvent(appMessage));
    }

    /// <summary>TS <c>_refreshFinalizedContext()</c>。</summary>
    private void RefreshFinalizedContext()
    {
        var projection = SessionManager.BuildSessionProjection();
        foreach (var entry in projection.Entries)
        {
            foreach (var message in entry.Messages)
            {
                _entryIdsByMessage.GetOrCreateValue(message).Id = entry.SourceEntry.Id;
            }
        }

        _agent.Messages = projection.Messages;
    }

    /// <summary>
    /// Append custom messages queued while the agent was running. Called once the current turn's
    /// tool results are in agent state and session history. TS <c>_flushPendingCustomMessages()</c>。
    /// </summary>
    private void FlushPendingCustomMessages()
    {
        if (_pendingCustomMessages.Count == 0)
        {
            return;
        }

        var pending = new List<CustomMessage>(_pendingCustomMessages);
        _pendingCustomMessages.Clear();
        foreach (var appMessage in pending)
        {
            AppendCustomMessage(appMessage);
        }
    }
}
