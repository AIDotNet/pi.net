using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Utils;

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
}

/// <summary>
/// SDK 工厂。对应 TS <c>core/sdk.ts</c>。
/// 4e-3e-2：工具 allowlist / denylist 解析。
/// 4e-3e-9f：<c>createAgentSession</c> 的装配叶子——cwd / agentDir 解析、模型恢复、
/// 思考档位回退链、<c>blockImages</c> 上下文过滤。
/// </summary>
/// <remarks>
/// <para>
/// <b>2026-10-12 基线对齐</b>：本类此前混用了两个 TS 修订版，已按 <c>origin/main</c>（<c>428a12bc7</c>）重写。
/// 三处修正：
/// </para>
/// <list type="number">
/// <item>
/// <c>GetToolListError</c> 被**移除**——TS 没有这个校验，<c>resolveDefaultTools</c> 对畸形输入只是静默过滤。
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
/// <c>CreateToolNameMatcher</c> 已下沉到 <see cref="McpServers"/>（TS 里它定义在 <c>mcp-servers.ts</c>）。
/// </para>
/// </remarks>
public static class Sdk
{
    /// <summary>对应 TS <c>createAgentSession</c> 的 tools 解析。</summary>
    public static ResolvedSessionTools ResolveSessionTools(
        CreateAgentSessionOptions options,
        IReadOnlyList<string>? settingsDefaultTools = null)
    {
        var allowedToolNames = options.Tools
            ?? (options.NoTools == NoToolsMode.All ? [] : null);
        var excludedToolNames = options.ExcludeTools;
        var isExcludedTool = excludedToolNames is not null
            ? McpServers.CreateToolNameMatcher(excludedToolNames)
            : null;
        var initialActiveToolNames = (
            options.Tools
            ?? (options.NoTools is not null
                ? []
                : settingsDefaultTools ?? SettingsManager.DefaultToolNames))
            .Where(name => isExcludedTool is null || !isExcludedTool(name))
            .ToArray();

        return new ResolvedSessionTools
        {
            InitialActiveToolNames = initialActiveToolNames,
            AllowedToolNames = allowedToolNames,
            ExcludedToolNames = excludedToolNames,
            UsesDefaultTools = options.Tools is null && options.NoTools is null,
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
}
