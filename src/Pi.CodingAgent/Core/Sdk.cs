using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
// `ToolDefinition` 在 Pi.Ai.Types（AI 层的元数据声明）与 Pi.CodingAgent.Core.Extensions（可执行工具定义）
// 各有一个，本文件一律指后者（与 AgentSession 同款）。
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Core;

/// <summary>
/// 对应 TS <c>CreateAgentSessionOptions.noTools</c> 的 <c>"all" | "builtin"</c>。
/// </summary>
public enum NoToolsMode
{
    /// <summary>对应 <c>"all"</c>：从零工具开始。</summary>
    All,

    /// <summary>
    /// 对应 <c>"builtin"</c>：停用内置默认工具（read/bash/edit/write），但保留扩展 / 自定义工具。
    /// </summary>
    Builtin,
}

/// <summary>对应 TS <c>CreateAgentSessionOptions</c> 的工具选择子集。</summary>
public sealed record CreateAgentSessionOptions
{
    /// <summary>工具名或模式（<c>*</c> 匹配任意字符）的 allowlist。</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>工具名或模式的 denylist；<see cref="Tools"/> 之后生效。</summary>
    public IReadOnlyList<string>? ExcludeTools { get; init; }

    /// <summary>无显式 allowlist 时的默认工具抑制模式。</summary>
    public NoToolsMode? NoTools { get; init; }

    // =====================================================================
    // createAgentSession 的装配入参（4e-3e-9h-4b）
    // =====================================================================

    /// <summary>项目本地发现的起点；缺省用会话管理器的 cwd，再退到进程 cwd（TS <c>cwd?</c>）。</summary>
    public string? Cwd { get; init; }

    /// <summary>全局配置目录；缺省 <c>Config.GetAgentDir()</c>（TS <c>agentDir?</c>）。</summary>
    public string? AgentDir { get; init; }

    /// <summary>规范模型/认证运行时；缺省按 <see cref="AgentDir"/> 现造一个（TS <c>modelRuntime?</c>）。</summary>
    public ModelRuntime? ModelRuntime { get; init; }

    /// <summary>指定模型；缺省从设置或会话存档恢复（TS <c>model?</c>）。</summary>
    public ModelSpec? Model { get; init; }

    /// <summary>思考档位；缺省从设置或会话存档恢复（TS <c>thinkingLevel?</c>）。</summary>
    public ThinkingLevel? ThinkingLevel { get; init; }

    /// <summary>Ctrl+P 循环用的模型组（TS <c>scopedModels?</c>）。</summary>
    public IReadOnlyList<ScopedModel>? ScopedModels { get; init; }

    /// <summary>资源加载器；缺省现造 <see cref="DefaultResourceLoader"/> 并 <c>reload</c>（TS <c>resourceLoader?</c>）。</summary>
    public DefaultResourceLoader? ResourceLoader { get; init; }

    /// <summary>会话管理器；缺省 <c>SessionManager.create(cwd, getDefaultSessionDir(cwd, agentDir))</c>。</summary>
    public SessionManager? SessionManager { get; init; }

    /// <summary>设置管理器；缺省 <c>SettingsManager.create(cwd, agentDir)</c>。</summary>
    public SettingsManager? SettingsManager { get; init; }

    /// <summary>扩展之外注册的自定义工具（TS <c>customTools?</c>）。</summary>
    public IReadOnlyList<ToolDefinition>? CustomTools { get; init; }

    /// <summary>扩展绑定会话时发出的 session_start 元数据（TS <c>sessionStartEvent?</c>）。</summary>
    public ExtensionEvent.SessionStart? SessionStartEvent { get; init; }
}

/// <summary>对应 TS <c>CreateAgentSessionResult</c>。</summary>
public sealed record CreateAgentSessionResult
{
    /// <summary>创建出的会话。</summary>
    public required AgentSession Session { get; init; }

    /// <summary>扩展加载结果（交互模式据此建立 UI 上下文）。</summary>
    public required LoadExtensionsResult ExtensionsResult { get; init; }

    /// <summary>会话按存档模型恢复失败时的警告；否则 <c>null</c>。</summary>
    public string? ModelFallbackMessage { get; init; }
}

/// <summary>
/// 对应 TS <c>createAgentSession</c> 里 tools 解析的结果。
/// </summary>
/// <param name="InitialActiveToolNames">
/// <c>initialActiveToolNames</c>：起始活跃工具（allowlist 或默认集，再按 denylist 过滤）。
/// </param>
/// <param name="AllowedToolNames">
/// <c>allowedToolNames</c>：<c>options.tools ?? (noTools === "all" ? [] : undefined)</c>。
/// 为 <c>null</c> 表示「没有 allowlist」（而不是空 allowlist）。
/// </param>
/// <param name="ExcludedToolNames"><c>excludedToolNames</c>：原样透传的 denylist。</param>
/// <param name="UsesDefaultTools"><c>usesDefaultTools</c>：<c>tools === undefined &amp;&amp; !noTools</c>。</param>
public sealed record ResolvedSessionTools
{
    public required IReadOnlyList<string> InitialActiveToolNames { get; init; }

    public IReadOnlyList<string>? AllowedToolNames { get; init; }

    public IReadOnlyList<string>? ExcludedToolNames { get; init; }

    public required bool UsesDefaultTools { get; init; }

    /// <summary>
    /// <c>defaultToolModifiers</c>：<c>options.tools</c> 全为 <c>+name</c>/<c>-name</c> 时的那份列表，
    /// 否则 <c>null</c>。会话 reload 时用它重新作用到刷新后的 <c>defaultTools</c> 上。
    /// </summary>
    public IReadOnlyList<string>? DefaultToolModifiers { get; init; }
}

/// <summary>
/// SDK 工厂。对应 TS <c>core/sdk.ts</c>。
/// 4e-3e-2：工具 allowlist / denylist 解析。
/// 4e-3e-9f：<c>createAgentSession</c> 的装配叶子——cwd / agentDir 解析、模型恢复、
/// 思考档位回退链、<c>blockImages</c> 上下文过滤。
/// </summary>
/// <remarks>
/// <para>
/// <b>2026-10-12 基线对齐</b>：本类此前混用了两个 TS 修订版，已按当时的 <c>origin/main</c>（<c>428a12bc7</c>）重写。
/// 三处修正：
/// </para>
/// <list type="number">
/// <item>
/// <c>GetToolListError</c> 被**移除**——当时 TS 没有这个校验，<c>resolveDefaultTools</c> 对畸形输入只是静默过滤。
/// </item>
/// <item>
/// <c>options.tools</c> 不再被当作 <c>+name</c>/<c>-name</c> 修饰符。TS 里 <c>+</c>/<c>-</c> 只属于
/// <c>defaultTools</c> **设置项**（见 <see cref="SettingsManager.ResolveDefaultTools"/>），
/// <c>options.tools</c> 是纯 allowlist（支持 <c>*</c> 模式）。
/// </item>
/// <item>
/// <c>NoTools</c> 由 <c>bool</c> 改为 <see cref="NoToolsMode"/>，恢复 TS 的 <c>"all" | "builtin"</c> 区分——
/// 二者的 <c>allowedToolNames</c> 不同（<c>[]</c> vs <c>undefined</c>），会走
/// <c>_refreshToolRegistry</c> 的不同分支。
/// </item>
/// </list>
/// <para>
/// <b>2026-10-16（R2 漂移 <c>ddaa0a034</c>）撤销前两条中的两条</b>：上游把
/// <c>getToolListError</c> 与 <c>+name</c>/<c>-name</c> 修饰符加到了 <c>options.tools</c> 上。
/// 现在 <c>tools</c> 全为修饰符时不再是 allowlist，而是叠加在 <c>defaultTools</c> 之上的选择变更；
/// 混用两者会抛 <c>Invalid tools option: …</c>。第三条（<c>NoToolsMode</c>）不变。
/// </para>
/// <para>
/// <c>CreateToolNameMatcher</c> 已下沉到 <see cref="McpServers"/>（TS 里它定义在 <c>mcp-servers.ts</c>）。
/// </para>
/// </remarks>
public static class Sdk
{
    /// <summary>对应 TS <c>createAgentSession</c> 的 tools 解析。</summary>
    /// <remarks>
    /// <b>2026-10-16（R2 漂移 <c>ddaa0a034</c>）</b>：恢复「<c>tools</c> 全为 <c>+name</c>/<c>-name</c>
    /// 时不是 allowlist，而是叠加在 <c>defaultTools</c> 之上的修饰符」这一语义，并加回
    /// <see cref="SettingsManager.GetToolListError"/> 的前置校验。
    /// </remarks>
    public static ResolvedSessionTools ResolveSessionTools(
        CreateAgentSessionOptions options,
        IReadOnlyList<string>? settingsDefaultTools = null)
    {
        var toolListError = options.Tools is not null
            ? SettingsManager.GetToolListError(options.Tools)
            : null;
        if (toolListError is not null)
        {
            throw new ArgumentException($"Invalid tools option: {toolListError}", nameof(options));
        }

        var defaultToolNames = options.NoTools is not null
            ? []
            : settingsDefaultTools ?? SettingsManager.DefaultToolNames;

        // 全为 +name/-name 的 tools 列表改变默认选择，而不是替换它（与 defaultTools 设置项同语义）。
        var toolModifiers = options.Tools?.Any(SettingsManager.IsToolModifier) == true ? options.Tools : null;
        var selectedToolNames = toolModifiers is not null
            ? SettingsManager.ApplyToolModifiers(defaultToolNames, toolModifiers)
            : options.Tools;

        var allowedToolNames = toolModifiers is not null
            ? (options.NoTools == NoToolsMode.All ? selectedToolNames : null)
            : (options.Tools ?? (options.NoTools == NoToolsMode.All ? [] : null));

        var excludedToolNames = options.ExcludeTools;
        var isExcludedTool = excludedToolNames is not null
            ? McpServers.CreateToolNameMatcher(excludedToolNames)
            : null;
        var initialActiveToolNames = (selectedToolNames ?? defaultToolNames)
            .Where(name => isExcludedTool is null || !isExcludedTool(name))
            .ToArray();

        return new ResolvedSessionTools
        {
            InitialActiveToolNames = initialActiveToolNames,
            AllowedToolNames = allowedToolNames,
            ExcludedToolNames = excludedToolNames,
            UsesDefaultTools = options.Tools is null && options.NoTools is null,
            DefaultToolModifiers = toolModifiers,
        };
    }

    // =====================================================================
    // createAgentSession 的装配叶子（4e-3e-9f）
    // =====================================================================

    /// <summary>模型恢复结果。对应 TS <c>createAgentSession</c> 里的 <c>model</c> + <c>modelFallbackMessage</c>。</summary>
    /// <param name="Model">最终选定的模型；<c>null</c> 表示仍需 <c>findInitialModel</c> 兜底。</param>
    /// <param name="FallbackMessage">会话存档模型无法恢复时的警告；否则 <c>null</c>。</param>
    public sealed record RestoredSessionModel(ModelSpec? Model, string? FallbackMessage);

    /// <summary>对应 TS <c>createAgentSession</c> 首行的 cwd 解析。</summary>
    /// <remarks>
    /// TS：<c>options.cwd ?? options.sessionManager?.getCwd() ?? process.cwd()</c>，
    /// 再经 <c>resolvePath</c>。端口把 <c>process.cwd()</c> 显式传入，避免静态依赖进程状态。
    /// </remarks>
    public static string ResolveSessionCwd(string? optionsCwd, string? sessionManagerCwd, string processCwd)
        => Paths.ResolvePath(optionsCwd ?? sessionManagerCwd ?? processCwd);

    /// <summary>对应 TS <c>createAgentSession</c> 的 agentDir 解析。</summary>
    /// <remarks>只有显式传入时才 <c>resolvePath</c>；缺省直接用默认 agentDir（TS 的 <c>getDefaultAgentDir()</c>）。</remarks>
    public static string ResolveSessionAgentDir(string? optionsAgentDir, string defaultAgentDir)
        => optionsAgentDir is null ? defaultAgentDir : Paths.ResolvePath(optionsAgentDir);

    /// <summary>
    /// 从会话存档恢复模型。对应 TS <c>createAgentSession</c> 的「restore model from session」段。
    /// </summary>
    /// <remarks>
    /// TS 要求两件事同时成立才采用存档模型：<c>modelRuntime.getModel</c> 认得它，
    /// 且 <c>hasConfiguredAuth</c> 认为该 provider 有可用凭据。任一不成立就回退到
    /// <c>findInitialModel</c>，并留下 <c>Could not restore model …</c> 警告。
    /// </remarks>
    public static RestoredSessionModel ResolveSessionModel(
        ModelSpec? explicitModel,
        bool hasExistingSession,
        BranchModelSelection? sessionModel,
        IModelResolverRuntime modelRuntime)
    {
        var model = explicitModel;
        string? fallbackMessage = null;

        if (model is null && hasExistingSession && sessionModel is not null)
        {
            var restored = modelRuntime.GetModel(sessionModel.Provider, sessionModel.ModelId);
            if (restored is not null && modelRuntime.HasConfiguredAuth(restored.Provider))
            {
                model = restored;
            }

            if (model is null)
            {
                fallbackMessage = $"Could not restore model {sessionModel.Provider}/{sessionModel.ModelId}";
            }
        }

        return new RestoredSessionModel(model, fallbackMessage);
    }

    /// <summary>
    /// <c>findInitialModel</c> 兜底成功后，把实际选中的模型追加到既有警告尾部。
    /// 对应 TS <c>modelFallbackMessage += `. Using ${model.provider}/${model.id}`</c>。
    /// </summary>
    /// <remarks>JS 的 <c>else if (modelFallbackMessage)</c> 是真值判断，空串不追加。</remarks>
    public static string? AppendFallbackModel(string? fallbackMessage, ModelSpec model)
        => string.IsNullOrEmpty(fallbackMessage)
            ? fallbackMessage
            : $"{fallbackMessage}. Using {model.Provider}/{model.Id}";

    /// <summary>分支上是否存在 <c>thinking_level_change</c> 条目。对应 TS <c>hasThinkingEntry</c>。</summary>
    public static bool HasThinkingLevelChangeEntry(IReadOnlyList<SessionEntry> branch)
        => branch.Any(entry => entry is ThinkingLevelChangeEntry);

    /// <summary>
    /// 思考档位的四级回退链。对应 TS <c>createAgentSession</c> 的 thinking level 解析段。
    /// </summary>
    /// <param name="optionsThinkingLevel"><c>options.thinkingLevel</c>（显式指定优先）。</param>
    /// <param name="hasExistingSession">会话是否已有消息（决定是否从存档恢复）。</param>
    /// <param name="hasThinkingEntry">分支上是否有 <c>thinking_level_change</c>。</param>
    /// <param name="existingSessionThinkingLevel">存档上下文里的档位（原始字符串）。</param>
    /// <param name="settingsDefaultThinkingLevel"><c>settings.defaultThinkingLevel</c>。</param>
    /// <param name="perModelThinkingLevel"><c>settings.modelThinkingLevels[provider/id]</c>。</param>
    /// <param name="model">最终模型；<c>null</c> 时强制 <c>off</c>。</param>
    /// <remarks>
    /// <para>
    /// 顺序严格照搬 TS：显式 → 存档（有 thinking 条目用存档值，否则用全局默认）→ 模型级覆盖 →
    /// 全局默认 → 无模型则 <c>off</c> → <c>clampThinkingLevel</c>。
    /// </para>
    /// <para>
    /// 差异 C110：TS 全程在字符串域运算（<c>ThinkingLevel</c> 是字符串联合），端口在边界处经
    /// <see cref="Pi.CodingAgent.Core.ThinkingLevels"/> 与枚举互转，中间仍保留字符串以便承载
    /// 旧会话文件里的未知档位（<c>Clamp</c> 会把它收敛到首个可用档位，与 TS 一致）。
    /// </para>
    /// </remarks>
    public static ThinkingLevel ResolveThinkingLevel(
        ThinkingLevel? optionsThinkingLevel,
        bool hasExistingSession,
        bool hasThinkingEntry,
        string? existingSessionThinkingLevel,
        string? settingsDefaultThinkingLevel,
        string? perModelThinkingLevel,
        ModelSpec? model)
    {
        string? level = optionsThinkingLevel is { } explicitLevel
            ? ThinkingLevels.ToText(explicitLevel)
            : null;

        if (level is null && hasExistingSession)
        {
            level = hasThinkingEntry
                ? existingSessionThinkingLevel
                : settingsDefaultThinkingLevel ?? ThinkingLevels.ToText(Defaults.DefaultThinkingLevel);
        }

        if (level is null && model is not null && !string.IsNullOrEmpty(perModelThinkingLevel))
        {
            level = perModelThinkingLevel;
        }

        if (level is null)
        {
            level = settingsDefaultThinkingLevel ?? ThinkingLevels.ToText(Defaults.DefaultThinkingLevel);
        }

        if (model is null)
        {
            return ThinkingLevel.Off;
        }

        return ThinkingLevels.Parse(Pi.Ai.Models.ThinkingLevels.Clamp(model, level)) ?? ThinkingLevel.Off;
    }

    /// <summary>
    /// <c>buildRequestOptions</c> 里超时 / 重试四个字段的解析结果。
    /// </summary>
    /// <remarks>
    /// TS 直接把这些值摊进返回的 <c>ModelsSimpleStreamOptions</c>；端口把「取值链」抽成显式入参的
    /// 叶子，余下字段（<c>transformHeaders</c> 闭包、扩展钩子）需要 <c>ExtensionRunner</c>，随 4e-3e-9h 装配。
    /// </remarks>
    public sealed record ResolvedRequestTimeouts(
        long? TimeoutMs,
        long? WebsocketConnectTimeoutMs,
        int? MaxRetries,
        long MaxRetryDelayMs);

    /// <summary>
    /// <c>httpIdleTimeoutMs === 0</c> 时使用的哨兵超时。对应 TS 字面量 <c>2147483647</c>（int32 上限，
    /// 表示「几乎不超时」）。
    /// </summary>
    public const long MaxHttpIdleTimeoutMs = 2147483647L;

    /// <summary>
    /// 超时 / 重试的三级回退链。对应 TS <c>buildRequestOptions</c> 的前四行。
    /// </summary>
    /// <param name="optionsTimeoutMs">调用方已设的 <c>options.timeoutMs</c>。</param>
    /// <param name="optionsWebsocketConnectTimeoutMs">调用方的 <c>options.websocketConnectTimeoutMs</c>。</param>
    /// <param name="optionsMaxRetries">调用方的 <c>options.maxRetries</c>。</param>
    /// <param name="optionsMaxRetryDelayMs">调用方的 <c>options.maxRetryDelayMs</c>。</param>
    /// <param name="providerRetrySettings">
    /// <c>settingsManager.getProviderRetrySettings()</c>：<c>timeoutMs?</c> / <c>maxRetries?</c> /
    /// <c>maxRetryDelayMs</c>（恒有值，缺省 60000）。
    /// </param>
    /// <param name="httpIdleTimeoutMs"><c>settingsManager.getHttpIdleTimeoutMs()</c>；<c>0</c> 表示哨兵。</param>
    /// <param name="websocketConnectTimeoutMs"><c>settingsManager.getWebSocketConnectTimeoutMs()</c>。</param>
    public static ResolvedRequestTimeouts ResolveRequestTimeouts(
        long? optionsTimeoutMs,
        long? optionsWebsocketConnectTimeoutMs,
        int? optionsMaxRetries,
        long? optionsMaxRetryDelayMs,
        (long? TimeoutMs, int? MaxRetries, long MaxRetryDelayMs) providerRetrySettings,
        long httpIdleTimeoutMs,
        long? websocketConnectTimeoutMs)
    {
        var effectiveTimeoutMs = httpIdleTimeoutMs == 0 ? MaxHttpIdleTimeoutMs : httpIdleTimeoutMs;
        return new ResolvedRequestTimeouts(
            optionsTimeoutMs ?? providerRetrySettings.TimeoutMs ?? effectiveTimeoutMs,
            optionsWebsocketConnectTimeoutMs ?? websocketConnectTimeoutMs,
            optionsMaxRetries ?? providerRetrySettings.MaxRetries,
            optionsMaxRetryDelayMs ?? providerRetrySettings.MaxRetryDelayMs);
    }

    /// <summary>图片被禁用时替换成的占位文本。对应 TS 字面量 <c>"Image reading is disabled."</c>。</summary>
    public const string ImageReadingDisabledText = "Image reading is disabled.";    /// <summary>
    /// <c>convertToLlm</c> + <c>blockImages</c> 过滤的组合包装。
    /// 对应 TS <c>convertToLlmWithBlockImages</c>。
    /// </summary>
    /// <remarks>
    /// TS 在每次调用时读 <c>settingsManager.getBlockImages()</c>，好让会话中途改设置立即生效；
    /// 端口把该值作为参数传入，由调用方（4e-3e-9h 的 streamFn 装配）在每次转换时取当前值。
    /// </remarks>
    public static IReadOnlyList<ChatMessage> ConvertToLlmWithBlockImages(
        IReadOnlyList<ChatMessage> messages,
        bool blockImages)
        => FilterBlockedImages(AgentMessages.ConvertToLlm(messages), blockImages);

    /// <summary>
    /// 把 user / toolResult 消息里的图片块替换成占位文本，并去掉相邻重复占位。
    /// 对应 TS <c>convertToLlmWithBlockImages</c> 的 <c>map</c> + <c>filter</c> 段。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只在 <paramref name="blockImages"/> 为真时处理；无图片的消息原样返回（TS 返回同一个对象）。
    /// </para>
    /// <para>
    /// 去重规则严格照搬 TS：占位块在前一个位置也是同样的占位文本时才被丢弃
    /// （比较对象是**替换后**的数组，因此「图片 + 紧跟一段同样的文字」也会被去重）。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ChatMessage> FilterBlockedImages(
        IReadOnlyList<ChatMessage> messages,
        bool blockImages)
    {
        if (!blockImages)
        {
            return messages;
        }

        var result = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            switch (message)
            {
                // user 消息的 content 是 `string | Block[]` 联合：只有数组分支才可能含图片。
                case UserMessage user when user.Content is MessageContent.Blocks blocks
                    && blocks.Value.Any(block => block is ImageContent):
                    result.Add(user with { Content = MessageContent.BlocksOf(ReplaceImages(blocks.Value)) });
                    break;

                case ToolResultMessage toolResult
                    when toolResult.Content.Any(block => block is ImageContent):
                    result.Add(toolResult with { Content = ReplaceImages(toolResult.Content) });
                    break;

                default:
                    result.Add(message);
                    break;
            }
        }

        return result;
    }

    private static IReadOnlyList<ContentBlock> ReplaceImages(IReadOnlyList<ContentBlock> content)
    {
        var mapped = new List<ContentBlock>(content.Count);
        foreach (var block in content)
        {
            mapped.Add(block is ImageContent ? new TextContent(ImageReadingDisabledText) : block);
        }

        var filtered = new List<ContentBlock>(mapped.Count);
        for (var index = 0; index < mapped.Count; index++)
        {
            var isDuplicatePlaceholder = mapped[index] is TextContent text
                && text.Text == ImageReadingDisabledText
                && index > 0
                && mapped[index - 1] is TextContent previous
                && previous.Text == ImageReadingDisabledText;

            if (!isDuplicatePlaceholder)
            {
                filtered.Add(mapped[index]);
            }
        }

        return filtered;
    }

    // =====================================================================
    // createAgentSession 装配本体（4e-3e-9h-4b）
    // =====================================================================

    /// <summary>
    /// 创建 <see cref="AgentSession"/>。对应 TS <c>createAgentSession(options = {})</c>
    /// （<c>core/sdk.ts:181-468</c>）。
    /// </summary>
    /// <param name="options">装配选项；<c>null</c> 等价 TS 的 <c>{}</c>。</param>
    /// <param name="processCwd">
    /// TS 的 <c>process.cwd()</c>。端口显式传入以避免静态依赖进程状态（同
    /// <see cref="ResolveSessionCwd"/>）；缺省取 <see cref="Directory.GetCurrentDirectory"/>。
    /// </param>
    /// <param name="cancellationToken">贯穿资源加载与模型运行时创建的取消令牌。</param>
    /// <remarks>
    /// <para>
    /// 求值顺序严格照搬 TS：cwd/agentDir 解析 → <see cref="ModelRuntime"/> → 设置/会话管理器 →
    /// 资源加载器 reload → 会话存档检查 → 模型恢复 → <c>findInitialModel</c> 兜底 →
    /// 思考档位四级回退 → 工具解析 → <c>Agent</c> 装配（含 <c>streamFn</c> / 缓存温热 / 扩展钩子）→
    /// 补写会话元数据 → 构造 <see cref="AgentSession"/>。
    /// </para>
    /// <para>
    /// <b>差异 C161</b>：TS 的会话模型状态住在 <c>agent.state.model</c>，构造会话时已由本函数写入，
    /// 故 <c>AgentSessionConfig</c> 不含模型字段。端口因模型状态二分（差异 C148）另加
    /// <see cref="AgentSessionConfig.Model"/>，把同一个 <see cref="ModelSpec"/> 交给会话做真相，
    /// 再镜像到 <c>agent.Model</c>。
    /// </para>
    /// <para>
    /// <b>已知缺口（顺延 4e-3e-9h-4b-2）</b>：TS 的 <c>CacheWarmer</c> 构造器还要
    /// <c>modelRuntime</c> / <c>sessionManager</c> / <c>decide</c> 三个入参，并自带定时重放
    /// （<c>schedule</c> / <c>refresh</c> / 经济学判定）。端口当前只有门闩 + 状态那一半
    /// （<c>CacheWarmer(Func&lt;string&gt;)</c>），<c>Start</c> 之后不会真的重发请求。
    /// 本函数按 TS 的位置与时机调用 <c>Start</c>，等 <c>CacheWarmer</c> 完整化后无需改动此处。
    /// </para>
    /// </remarks>
    public static async Task<CreateAgentSessionResult> CreateAgentSessionAsync(
        CreateAgentSessionOptions? options = null,
        string? processCwd = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CreateAgentSessionOptions();

        var cwd = ResolveSessionCwd(
            options.Cwd,
            options.SessionManager?.GetCwd(),
            processCwd ?? Directory.GetCurrentDirectory());
        var agentDir = ResolveSessionAgentDir(options.AgentDir, Config.GetAgentDir());
        var resourceLoader = options.ResourceLoader;

        // TS 只在调用方显式给了 agentDir 时才拼这两个路径，否则交给 ModelRuntime.create 用默认值。
        var filePaths = AgentSessionServicesBuilder.ResolveModelRuntimeFilePaths(
            options.AgentDir is not null, agentDir);
        var modelRuntime = options.ModelRuntime ?? await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            AuthPath = filePaths.AuthPath,
            ModelsPath = filePaths.ModelsPath,
        }).ConfigureAwait(false);

        var settingsManager = options.SettingsManager ?? SettingsManager.Create(cwd, agentDir);
        var sessionManager = options.SessionManager
            ?? SessionManager.Create(cwd, SessionFiles.GetDefaultSessionDir(cwd, agentDir));

        if (resourceLoader is null)
        {
            resourceLoader = new DefaultResourceLoader(cwd, agentDir, settingsManager);
            await resourceLoader.ReloadAsync(null, cancellationToken).ConfigureAwait(false);
            Timings.Time("resourceLoader.reload");
        }

        // 会话是否已有可恢复的数据。
        var existingSession = sessionManager.BuildSessionContext();
        var hasExistingSession = existingSession.Messages.Count > 0;
        var branch = sessionManager.GetBranch();
        var hasThinkingEntry = HasThinkingLevelChangeEntry(branch);

        // 助手消息记的是真正作答的物理模型，虚拟选择只出现在 model_change 条目里。
        var branchSelection = VirtualModels.GetBranchSelection(branch, modelRuntime.GetModel);
        var sessionModel = branchSelection is { } selection
            ? new BranchModelSelection(selection.Provider, selection.ModelId)
            : null;
        var restored = ResolveSessionModel(options.Model, hasExistingSession, sessionModel, modelRuntime);
        var model = restored.Model;
        var modelFallbackMessage = restored.FallbackMessage;

        if (model is null)
        {
            var initial = await ModelResolver.FindInitialModelAsync(new InitialModelOptions
            {
                ScopedModels = [],
                IsContinuing = hasExistingSession,
                DefaultProvider = settingsManager.GetDefaultProvider(),
                DefaultModelId = settingsManager.GetDefaultModel(),
                DefaultThinkingLevel = ThinkingLevels.Parse(settingsManager.GetDefaultThinkingLevel()),
                ModelThinkingLevels = ToThinkingLevelMap(settingsManager.GetAllModelThinkingLevels()),
                Runtime = modelRuntime,
            }).ConfigureAwait(false);
            model = initial.Model;
            if (model is null)
            {
                modelFallbackMessage = AuthGuidance.FormatNoModelsAvailableMessage();
            }
            else if (modelFallbackMessage is not null)
            {
                modelFallbackMessage = AppendFallbackModel(modelFallbackMessage, model);
            }
        }

        var thinkingLevel = ResolveThinkingLevel(
            options.ThinkingLevel,
            hasExistingSession,
            hasThinkingEntry,
            existingSession.ThinkingLevel,
            settingsManager.GetDefaultThinkingLevel(),
            model is null ? null : settingsManager.GetModelThinkingLevel(model.Provider, model.Id),
            model);

        var resolvedTools = ResolveSessionTools(options, settingsManager.GetDefaultTools());

        // `convertToLlm` 包装：blockImages 开着时把图片换成占位文本（纵深防御）。
        // TS 每次调用现读设置，好让会话中途改设置立即生效。
        Task<IReadOnlyList<ChatMessage>> ConvertToLlm(IReadOnlyList<ChatMessage> messages)
            => Task.FromResult(ConvertToLlmWithBlockImages(messages, settingsManager.GetBlockImages()));

        var extensionRunnerRef = new ExtensionRunnerRef();
        var cacheWarmer = new CacheWarmer(
            (warmModel, warmContext, warmOptions) => modelRuntime.StreamSimple(warmModel, warmContext, warmOptions),
            sessionManager.AppendUsage,
            () => sessionManager.GetBranch(),
            () => settingsManager.GetCacheWarmingMode(),
            async decisionEvent =>
            {
                var runner = extensionRunnerRef.Current;
                return runner is null
                    ? decisionEvent.Action
                    : await runner.EmitCacheWarmingDecisionAsync(new ExtensionEvent.CacheWarmingDecision(
                        decisionEvent.WarmCost,
                        decisionEvent.MissCost,
                        decisionEvent.ContinuationProbability,
                        decisionEvent.Action)).ConfigureAwait(false);
            });

        Dictionary<string, object?> BuildRequestOptions(
            ModelSpec requestModel, SimpleStreamOptions? streamOptions)
        {
            var providerRetrySettings = settingsManager.GetProviderRetrySettings();
            var timeouts = ResolveRequestTimeouts(
                streamOptions?.TimeoutMs,
                streamOptions?.WebsocketConnectTimeoutMs,
                streamOptions?.MaxRetries,
                streamOptions?.MaxRetryDelayMs,
                providerRetrySettings,
                settingsManager.GetHttpIdleTimeoutMs(),
                settingsManager.GetWebSocketConnectTimeoutMs());

            var requestOptions = new Dictionary<string, object?>(
                CompatShims.ToDictionary(streamOptions ?? new SimpleStreamOptions()),
                StringComparer.Ordinal);
            if (timeouts.TimeoutMs is { } timeoutMs) requestOptions["timeoutMs"] = timeoutMs;
            if (timeouts.WebsocketConnectTimeoutMs is { } websocketTimeoutMs)
                requestOptions["websocketConnectTimeoutMs"] = websocketTimeoutMs;
            if (timeouts.MaxRetries is { } maxRetries) requestOptions["maxRetries"] = maxRetries;
            requestOptions["maxRetryDelayMs"] = timeouts.MaxRetryDelayMs;
            requestOptions[Pi.Ai.Models.Models.TransformHeadersOptionKey] =
                new Func<IReadOnlyDictionary<string, string?>, Task<IReadOnlyDictionary<string, string?>>>(
                    async requestHeaders =>
                    {
                        var headers = ProviderAttribution.MergeHeaders(
                            requestModel, settingsManager, streamOptions?.SessionId, requestHeaders);
                        var headerRunner = extensionRunnerRef.Current;
                        if (headerRunner is null
                            || !headerRunner.HasHandlers(ExtensionEventNames.BeforeProviderHeaders))
                        {
                            return headers ?? new Dictionary<string, string?>(StringComparer.Ordinal);
                        }

                        var mutable = new Pi.CodingAgent.Core.Extensions.Types.ProviderHeaders();
                        foreach (var (name, value) in headers ?? new Dictionary<string, string?>(StringComparer.Ordinal))
                        {
                            mutable[name] = value;
                        }

                        await headerRunner.EmitBeforeProviderHeadersAsync(mutable).ConfigureAwait(false);
                        return mutable.AsDictionary();
                    });
            return requestOptions;
        }

        // 只温热选中模型的请求：虚拟选择路由过的、或被扩展改道的请求，下一次未必重复。
        // TS 在 `agent` 之前定义本闭包、靠调用期再读 `agent.state.messages`；端口显式把 agent
        // 传进来（局部函数体里 `agent` 尚未赋值，只能晚绑定）。
        Func<bool> CacheContextIsCurrent(Pi.Agent.Agent owner, Pi.Ai.Types.Model requestModel)
        {
            // TS 捕获 `agent.state.messages` 的**当前数组**；端口 Agent 的 Messages 是同一个活 List，
            // 故显式取快照，才能复现 TS「前缀仍在」的判定。
            var messages = owner.Messages.ToArray();
            return () =>
            {
                var currentModel = owner.Model;
                var currentMessages = owner.Messages;
                if (currentModel.Provider != requestModel.Provider || currentModel.Id != requestModel.Id)
                {
                    return false;
                }

                if (messages.Length > currentMessages.Count)
                {
                    return false;
                }

                for (var index = 0; index < messages.Length; index++)
                {
                    if (!ReferenceEquals(currentMessages[index], messages[index]))
                    {
                        return false;
                    }
                }

                return true;
            };
        }

        Task<object?> TransformProviderPayloadAsync(object? payload)
        {
            var runner = extensionRunnerRef.Current;
            if (runner is null || !runner.HasHandlers(ExtensionEventNames.BeforeProviderRequest))
            {
                return Task.FromResult(payload);
            }

            return runner.EmitBeforeProviderRequestAsync(payload);
        }

        async Task HandleProviderResponseAsync(ProviderResponse response)
        {
            var runner = extensionRunnerRef.Current;
            if (runner is null || !runner.HasHandlers(ExtensionEventNames.AfterProviderResponse))
            {
                return;
            }

            await runner.EmitAsync(new ExtensionEvent.AfterProviderResponse(response.Status, response.Headers))
                .ConfigureAwait(false);
        }

        async Task HandleProviderStreamEventAsync(JsonObject data, ModelSpec eventModel)
        {
            var runner = extensionRunnerRef.Current;
            if (runner is null || !runner.HasHandlers(ExtensionEventNames.ProviderStreamEvent))
            {
                return;
            }

            await runner.EmitAsync(new ExtensionEvent.ProviderStream(
                eventModel.Provider, eventModel.Api, eventModel.Id, data)).ConfigureAwait(false);
        }

        Pi.Agent.Agent agent = null!;
        agent = new Pi.Agent.Agent(new Pi.Agent.AgentOptions
        {
            SystemPrompt = string.Empty,
            Model = model is null ? null : CompatShims.ToModel(model),
            ThinkingLevel = thinkingLevel,
            Tools = [],
            Messages = existingSession.Messages,
            ConvertToLlm = ConvertToLlm,
            StreamFn = async (streamModel, context, streamOptions, signal) =>
            {
                // 差异 C148 的反向桥：agent 持有的是瘦 Model，会话/目录层流通的是 ModelSpec，
                // 故按 provider/id 回查目录（与 InstallAgentRequestProjection 同一约定）。
                var spec = modelRuntime.GetModel(streamModel.Provider, streamModel.Id)
                    ?? throw new InvalidOperationException(
                        $"Model {streamModel.Provider}/{streamModel.Id} is not registered.");
                var requestOptions = BuildRequestOptions(spec, streamOptions);
                // TS 的 signal 住在 options 里；端口的 StreamFn 把它单列成第 4 个入参，故回填。
                if (signal.CanBeCanceled)
                {
                    requestOptions["signal"] = signal;
                }

                if (streamOptions?.SessionId == sessionManager.GetSessionId())
                {
                    cacheWarmer.Start(
                        new CacheWarmRequest
                        {
                            Model = spec,
                            Context = context,
                            Options = streamOptions ?? new SimpleStreamOptions(),
                            RequestOptions = requestOptions,
                        },
                        CacheContextIsCurrent(agent, streamModel));
                }

                return modelRuntime.StreamSimple(spec, context.Messages, requestOptions);
            },
            OnPayload = async (payload, _) =>
                await TransformProviderPayloadAsync(payload).ConfigureAwait(false) as JsonObject ?? payload,
            OnResponse = (response, _) => HandleProviderResponseAsync(response),
            OnProviderStreamEvent = HandleProviderStreamEventAsync,
            SessionId = sessionManager.GetSessionId(),
            TransformContext = (messages, _) =>
            {
                var runner = extensionRunnerRef.Current;
                return runner is null ? Task.FromResult(messages) : runner.EmitContextAsync(messages);
            },
            SteeringMode = QueueModes.Parse(settingsManager.GetSteeringMode()),
            FollowUpMode = QueueModes.Parse(settingsManager.GetFollowUpMode()),
            Transport = settingsManager.GetTransport(),
            ThinkingBudgets = ToThinkingBudgets(settingsManager.GetThinkingBudgets()),
            MaxRetryDelayMs = (int)settingsManager.GetProviderRetrySettings().MaxRetryDelayMs,
        });

        // 给旧会话补上缺失的设置元数据。
        if (hasExistingSession)
        {
            if (!hasThinkingEntry)
            {
                sessionManager.AppendThinkingLevelChange(ThinkingLevels.ToText(thinkingLevel));
            }
        }
        else
        {
            // 新会话记录初始模型与思考档位，便于下次恢复。
            if (model is not null)
            {
                sessionManager.AppendModelChange(model.Provider, model.Id);
            }

            sessionManager.AppendThinkingLevelChange(ThinkingLevels.ToText(thinkingLevel));
        }

        var session = new AgentSession(new AgentSessionConfig
        {
            Agent = agent,
            SessionManager = sessionManager,
            SettingsManager = settingsManager,
            Cwd = cwd,
            Model = model,
            ScopedModels = options.ScopedModels,
            ResourceLoader = resourceLoader,
            CustomTools = options.CustomTools,
            ModelRuntime = modelRuntime,
            CacheWarmer = cacheWarmer,
            InitialActiveToolNames = resolvedTools.InitialActiveToolNames,
            UsesDefaultTools = resolvedTools.UsesDefaultTools,
            DefaultToolModifiers = resolvedTools.DefaultToolModifiers,
            AllowedToolNames = resolvedTools.AllowedToolNames,
            ExcludedToolNames = resolvedTools.ExcludedToolNames,
            ExtensionRunnerRef = extensionRunnerRef,
            SessionStartEvent = options.SessionStartEvent,
        });

        return new CreateAgentSessionResult
        {
            Session = session,
            ExtensionsResult = resourceLoader.GetExtensions(),
            ModelFallbackMessage = modelFallbackMessage,
        };
    }

    /// <summary><c>settings.modelThinkingLevels</c>（文本值）→ <see cref="InitialModelOptions"/> 的强类型映射。</summary>
    private static IReadOnlyDictionary<string, ThinkingLevel> ToThinkingLevelMap(
        IReadOnlyDictionary<string, string> rawLevels)
    {
        var result = new Dictionary<string, ThinkingLevel>(StringComparer.Ordinal);
        foreach (var (key, text) in rawLevels)
        {
            if (ThinkingLevels.Parse(text) is { } level)
            {
                result[key] = level;
            }
        }

        return result;
    }

    /// <summary><c>settings.thinkingBudgets</c> 的 JSON 对象 → 强类型 <see cref="ThinkingBudgets"/>。</summary>
    /// <remarks>TS 直接把该对象交给流选项；端口在设置边界做一次字段映射。缺失 / 非数字字段留空。</remarks>
    private static ThinkingBudgets? ToThinkingBudgets(System.Text.Json.Nodes.JsonObject? raw)
    {
        if (raw is null)
        {
            return null;
        }

        long? Read(string name)
            => raw[name] is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<long>(out var number)
                ? number
                : null;

        return new ThinkingBudgets(
            Minimal: Read("minimal"),
            Low: Read("low"),
            Medium: Read("medium"),
            High: Read("high"));
    }
}
