using System.Text.RegularExpressions;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
// `ToolDefinition` 在 Pi.Ai.Types（AI 层的元数据声明）与 Pi.CodingAgent.Core.Extensions（可执行工具定义）
// 各有一个，本文件的语义一律是后者。
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Core;

/// <summary>定义 + 溯源。对应 TS <c>ToolDefinitionEntry</c>（<c>agent-session.ts</c> 私有接口）。</summary>
public sealed record ToolDefinitionEntry
{
    public required ToolDefinition Definition { get; init; }

    public required SourceInfo SourceInfo { get; init; }
}

/// <summary>
/// <c>prepareLoadout</c> 钩子抛错时上报的载荷。对应 TS <c>_extensionRunner.emitError</c> 实参的子集
/// （完整事件面随 4e-3e-9d 的扩展绑定落地）。
/// </summary>
public sealed record LoadoutHookError(string ExtensionPath, string Event, string Error, string? Stack);

/// <summary><c>_applyToolLoadout</c> 的结果。</summary>
/// <param name="Declared">声明给模型的工具（可能已应用 <c>prepareLoadout</c> 的描述覆盖）。</param>
/// <param name="HiddenDeclarations">活跃但请求里不声明其声明的工具名。</param>
public sealed record ToolLoadoutApplication(
    IReadOnlyList<AgentTool> Declared,
    IReadOnlySet<string> HiddenDeclarations);

/// <summary>
/// <c>core/agent-session.ts</c> 中**不依赖 <c>AgentSession</c> 实例状态**的纯逻辑（4e-3e-9a）。
/// 已落地：会话统计（<c>getSessionStats</c> / <c>getContextUsage</c> / <c>estimateMessagesTokens</c>）、
/// 工具装载（<c>getCallableToolNames</c> / <c>_isAllowedTool</c> / <c>_getToolExposure</c> /
/// <c>_applyToolLoadout</c> / <c>_isDeclarable</c> / <c>_isActivatedOnRegistration</c>）、
/// 消息溯源（<c>_findPersistedMessageEntryId</c> / <c>_omitRecoveryAttempt</c> 的判定）与
/// 提示词归一化（<c>_normalizePromptSnippet</c> / <c>_normalizePromptGuidelines</c>）。
/// </summary>
/// <remarks>
/// 需要实例状态的部分（构造器 / 生命周期 / prompt 循环 / compaction / 扩展绑定）随
/// 4e-3e-9b～4e-3e-9e 落地；本文件按仓库既有的「叶子先行」惯例，先把算法与投影抽成显式入参的
/// 静态函数，实例层再接线。
/// </remarks>
public static partial class AgentSession
{
    private static readonly Regex PromptSnippetNewlines = new(@"[\r\n]+", RegexOptions.Compiled);

    private static readonly Regex PromptSnippetWhitespace =
        new(JsRegex.WhitespaceClass + "+", RegexOptions.Compiled);

    // =========================================================================
    // 会话统计
    // =========================================================================

    /// <summary>对应 TS <c>estimateMessagesTokens</c>。</summary>
    public static long EstimateMessagesTokens(IEnumerable<ChatMessage> messages)
    {
        long tokens = 0;
        foreach (var message in messages)
        {
            tokens += Compaction.EstimateTokens(message);
        }

        return tokens;
    }

    /// <summary>
    /// 对应 TS <c>getSessionStats</c>。聚合**全部**会话条目（含被压缩掉的历史），
    /// 因此 token / 成本反映整个会话实际计费量。
    /// </summary>
    public static SessionStats GetSessionStats(
        IReadOnlyList<SessionEntry> entries,
        string? sessionFile,
        string sessionId,
        ContextUsage? contextUsage)
    {
        var userMessages = 0;
        var assistantMessages = 0;
        var toolResults = 0;
        var totalMessages = 0;
        var toolCalls = 0;
        var totals = UsageTotalsApi.CreateUsageTotals();

        foreach (var entry in entries)
        {
            // TS 先做用量归集，再 `if (entry.type !== "message") continue`——两步互不短路。
            switch (entry)
            {
                case UsageEntry usageEntry:
                    UsageTotalsApi.AddUsageToTotals(totals, usageEntry.Usage);
                    break;
                case BranchSummaryEntry { UsageStats: { } branchUsage }:
                    UsageTotalsApi.AddUsageToTotals(totals, branchUsage);
                    break;
                case CompactionEntry { UsageStats: { } compactionUsage }:
                    UsageTotalsApi.AddUsageToTotals(totals, compactionUsage);
                    break;
            }

            if (entry is not SessionMessageEntry messageEntry)
            {
                continue;
            }

            totalMessages++;
            switch (messageEntry.Message)
            {
                case UserMessage:
                    userMessages++;
                    break;

                case ToolResultMessage toolResult:
                    toolResults++;
                    if (toolResult.UsageStats is { } toolUsage)
                    {
                        UsageTotalsApi.AddUsageToTotals(totals, toolUsage);
                    }

                    break;

                case AssistantMessage assistant:
                    assistantMessages++;
                    toolCalls += assistant.Content.OfType<ToolCallContent>().Count();
                    if (assistant.UsageStats is { } assistantUsage)
                    {
                        UsageTotalsApi.AddUsageToTotals(totals, assistantUsage);
                    }

                    break;
            }
        }

        return new SessionStats
        {
            SessionFile = sessionFile,
            SessionId = sessionId,
            UserMessages = userMessages,
            AssistantMessages = assistantMessages,
            ToolCalls = toolCalls,
            ToolResults = toolResults,
            TotalMessages = totalMessages,
            Tokens = new SessionTokenStats
            {
                Input = totals.Input,
                Output = totals.Output,
                CacheRead = totals.CacheRead,
                CacheWrite = totals.CacheWrite,
                Total = totals.Input + totals.Output + totals.CacheRead + totals.CacheWrite,
            },
            Cost = totals.Cost,
            ContextUsage = contextUsage,
        };
    }

    /// <summary>
    /// 对应 TS <c>getContextUsage</c>。压缩之后最后一条助手 usage 反映的是压缩**前**的上下文规模，
    /// 故只信任「最近一次压缩之后」的助手 usage；没有这样的助手时 token 数未知。
    /// </summary>
    /// <remarks>
    /// 差异 C86：TS 直接读 <c>message.usage</c>（类型上必填，缺失会在 <c>calculateContextTokens</c> 里抛错），
    /// C# 的 <see cref="AssistantMessage.UsageStats"/> 可空，故按「无 usage 即不计入」处理。
    /// 差异 C87：<c>_limitsModel()</c> 的取值由调用方算好后以 <paramref name="contextWindow"/> 传入。
    /// </remarks>
    public static ContextUsage? GetContextUsage(
        long contextWindow,
        SessionProjection projection,
        IReadOnlyList<SessionEntry> branch)
    {
        if (contextWindow <= 0)
        {
            return null;
        }

        var latestCompaction = SessionEntries.GetLatestCompactionEntry(branch);
        if (latestCompaction is not null)
        {
            var projectedAssistants = new HashSet<string?>(StringComparer.Ordinal);
            foreach (var entry in projection.Entries)
            {
                if (entry.Messages.Any(message =>
                        message is AssistantMessage assistant
                        && assistant.StopReason != StopReason.Aborted
                        && assistant.StopReason != StopReason.Error
                        && assistant.UsageStats is { } usage
                        && Compaction.CalculateContextTokens(usage) > 0))
                {
                    projectedAssistants.Add(entry.SourceEntry.Id);
                }
            }

            var compactionIndex = -1;
            for (var i = 0; i < branch.Count; i++)
            {
                if (branch[i].Id == latestCompaction.Id)
                {
                    compactionIndex = i;
                    break;
                }
            }

            var hasPostCompactionUsage = false;
            for (var i = compactionIndex + 1; i < branch.Count; i++)
            {
                if (projectedAssistants.Contains(branch[i].Id))
                {
                    hasPostCompactionUsage = true;
                    break;
                }
            }

            if (!hasPostCompactionUsage)
            {
                return new ContextUsage { Tokens = null, ContextWindow = contextWindow, Percent = null };
            }
        }

        var estimate = Compaction.EstimateProjectedContextTokens(projection, branch);
        var percent = (double)estimate.Tokens / contextWindow * 100;
        return new ContextUsage { Tokens = estimate.Tokens, ContextWindow = contextWindow, Percent = percent };
    }

    // =========================================================================
    // 工具装载
    // =========================================================================

    /// <summary>对应 TS <c>_getToolExposure</c>：未注册或未声明 exposure 时回落到 <c>direct</c>。</summary>
    public static string GetToolExposure(
        IReadOnlyDictionary<string, ToolDefinitionEntry> definitions,
        string name)
        => definitions.TryGetValue(name, out var entry) && entry.Definition.Exposure is { } exposure
            ? exposure
            : ToolExposure.Direct;

    /// <summary>对应 TS <c>_isAllowedTool</c>。</summary>
    public static bool IsAllowedTool(string name, IReadOnlySet<string>? allowed, IReadOnlySet<string>? excluded)
        => (allowed is null || allowed.Contains(name)) && !(excluded is not null && excluded.Contains(name));

    /// <summary>
    /// 对应 TS <c>_getCallableTools</c>：活跃的 <c>direct</c> 工具，加上**全部**已注册的
    /// <c>codemode</c> / <c>deferred</c> 工具。<c>model-only</c> 与 <c>hidden</c> 不可调用。
    /// </summary>
    /// <remarks>
    /// 差异 C88：TS 返回 <c>AgentTool[]</c>，端口只返回名字——唯一的消费点
    /// （<c>getCallableToolNames</c>）取的就是名字，而 <c>_applyToolLoadout</c> 里的 <c>callable</c>
    /// 字段需要工具对象，改由 <see cref="ApplyToolLoadout"/> 内部按同一规则重建。
    /// </remarks>
    public static IReadOnlyList<string> GetCallableToolNames(
        IEnumerable<string> registeredNames,
        IReadOnlyDictionary<string, ToolDefinitionEntry> definitions,
        IReadOnlySet<string> active)
    {
        var result = new List<string>();
        foreach (var name in registeredNames)
        {
            var exposure = GetToolExposure(definitions, name);
            if (exposure == ToolExposure.Codemode
                || exposure == ToolExposure.Deferred
                || (exposure == ToolExposure.Direct && active.Contains(name)))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    /// 对应 TS <c>_applyToolLoadout</c>：把请求的活跃工具名解析成声明给模型的工具集，
    /// 运行活跃工具的 <c>prepareLoadout</c> 钩子（可覆盖描述、可隐藏声明），并返回隐藏集合。
    /// </summary>
    /// <param name="toolNames">请求的活跃工具名；重复项按首次出现去重（TS <c>[...new Set(...)]</c>）。</param>
    /// <param name="registry">已注册工具，按注册顺序（TS <c>Map.values()</c> 的插入序 load-bearing）。</param>
    /// <param name="definitions">工具定义表。</param>
    /// <param name="onHookError">钩子抛错时的上报回调；<c>null</c> 表示吞掉（TS 会经扩展运行时报错）。</param>
    public static ToolLoadoutApplication ApplyToolLoadout(
        IReadOnlyList<string> toolNames,
        IReadOnlyDictionary<string, AgentTool> registry,
        IReadOnlyDictionary<string, ToolDefinitionEntry> definitions,
        Action<LoadoutHookError>? onHookError = null)
    {
        var requested = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in toolNames)
        {
            if (seen.Add(name))
            {
                requested.Add(name);
            }
        }

        var tools = new List<AgentTool>();
        foreach (var name in requested)
        {
            if (registry.TryGetValue(name, out var tool) && GetToolExposure(definitions, name) != ToolExposure.Hidden)
            {
                tools.Add(tool);
            }
        }

        var hooks = new List<(AgentTool Tool, ToolDefinitionEntry Entry)>();
        foreach (var tool in tools)
        {
            if (definitions.TryGetValue(tool.Name, out var entry) && entry.Definition.PrepareLoadout is not null)
            {
                hooks.Add((tool, entry));
            }
        }

        var hidden = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<AgentTool> declared = tools;

        if (hooks.Count > 0)
        {
            var activeNames = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            var view = new LoadoutView(
                tools,
                BuildCallableTools(registry, definitions, activeNames),
                [.. registry.Values],
                definitions);

            var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (_, entry) in hooks)
            {
                try
                {
                    var changes = entry.Definition.PrepareLoadout!(view);
                    foreach (var (name, description) in changes?.Descriptions ?? new Dictionary<string, string>())
                    {
                        descriptions[name] = description;
                    }

                    foreach (var name in changes?.HiddenDeclarations ?? [])
                    {
                        hidden.Add(name);
                    }
                }
                catch (Exception error)
                {
                    onHookError?.Invoke(new LoadoutHookError(
                        entry.SourceInfo.Path,
                        "prepare_loadout",
                        error.Message,
                        error.StackTrace));
                }
            }

            declared = [.. tools.Select(tool =>
                descriptions.TryGetValue(tool.Name, out var description)
                    ? tool with { Description = description }
                    : tool)];
        }

        return new ToolLoadoutApplication(declared, hidden);
    }

    /// <summary>对应 TS <c>_isDeclarable</c>：激活该工具会把它声明给模型。</summary>
    public static bool IsDeclarable(IReadOnlyDictionary<string, ToolDefinitionEntry> definitions, string name)
    {
        var exposure = GetToolExposure(definitions, name);
        return exposure == ToolExposure.Direct || exposure == ToolExposure.ModelOnly;
    }

    /// <summary>对应 TS <c>_isActivatedOnRegistration</c>。</summary>
    public static bool IsActivatedOnRegistration(
        IReadOnlyDictionary<string, ToolDefinitionEntry> definitions,
        string name)
        => IsDeclarable(definitions, name)
            && (!definitions.TryGetValue(name, out var entry) || entry.Definition.DefaultActive != false);

    private static IReadOnlyList<AgentTool> BuildCallableTools(
        IReadOnlyDictionary<string, AgentTool> registry,
        IReadOnlyDictionary<string, ToolDefinitionEntry> definitions,
        IReadOnlySet<string> active)
    {
        var callable = new List<AgentTool>();
        foreach (var (name, tool) in registry)
        {
            var exposure = GetToolExposure(definitions, name);
            if (exposure == ToolExposure.Codemode
                || exposure == ToolExposure.Deferred
                || (exposure == ToolExposure.Direct && active.Contains(name)))
            {
                callable.Add(tool);
            }
        }

        return callable;
    }

    /// <summary><see cref="IToolLoadout"/> 的只读实现，对应 TS <c>_applyToolLoadout</c> 里的字面量对象。</summary>
    private sealed class LoadoutView(
        IReadOnlyList<AgentTool> declared,
        IReadOnlyList<AgentTool> callable,
        IReadOnlyList<AgentTool> registered,
        IReadOnlyDictionary<string, ToolDefinitionEntry> definitions) : IToolLoadout
    {
        public IReadOnlyList<AgentTool> Declared { get; } = declared;

        public IReadOnlyList<AgentTool> Callable { get; } = callable;

        public IReadOnlyList<AgentTool> Registered { get; } = registered;

        public string GetExposure(string name) => GetToolExposure(definitions, name);

        public ToolNamespace? GetNamespace(string name)
            => definitions.TryGetValue(name, out var entry) ? entry.Definition.Namespace : null;

        public IReadOnlyList<string> GetPromptGuidelines(string name)
            => definitions.TryGetValue(name, out var entry) ? entry.Definition.PromptGuidelines ?? [] : [];
    }

    // =========================================================================
    // 消息溯源
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>_findPersistedMessageEntryId</c>：先查记忆表，再按**引用同一性**在分支里反查，
    /// 最后退到投影下标对齐。
    /// </summary>
    /// <remarks>
    /// 差异 C89：TS 用 <c>===</c> / <c>indexOf</c>（引用同一性），而 C# 的 record 默认是**值相等**——
    /// 两条内容相同的消息会互相误判，故此处一律用 <see cref="object.ReferenceEquals"/>。
    /// 差异 C90：记忆表由调用方持有，命中项以 <paramref name="memoizedId"/> 传入。
    /// </remarks>
    public static string? FindPersistedMessageEntryId(
        ChatMessage message,
        string? memoizedId,
        IReadOnlyList<SessionEntry> branch,
        IReadOnlyList<ChatMessage> agentMessages,
        SessionProjection projection)
    {
        // TS `if (mapped) return mapped`：空串按 JS 真值语义视为未命中。
        if (!string.IsNullOrEmpty(memoizedId))
        {
            return memoizedId;
        }

        for (var i = branch.Count - 1; i >= 0; i--)
        {
            if (branch[i] is SessionMessageEntry entry && ReferenceEquals(entry.Message, message))
            {
                return entry.Id;
            }
        }

        var messageIndex = IndexOfReference(agentMessages, message);
        if (messageIndex < 0)
        {
            return null;
        }

        var projectedIndex = 0;
        foreach (var entry in projection.Entries)
        {
            foreach (var projected in entry.Messages)
            {
                if (projectedIndex == messageIndex)
                {
                    return entry.SourceEntry.Id;
                }

                projectedIndex++;
            }
        }

        return null;
    }

    /// <summary>
    /// 对应 TS <c>_omitRecoveryAttempt</c> 的前置判定：存在「投影消息没有源条目」时，
    /// TS 抛 <c>Cannot persist recovery omission because a projected message has no source entry</c>。
    /// 返回 <see langword="true"/> 表示应抛错。
    /// </summary>
    public static bool HasUnresolvedProjectedTarget(
        IReadOnlyList<string?> targetIds,
        IReadOnlyList<ChatMessage> targets,
        IReadOnlyList<ChatMessage> agentMessages)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            if (targetIds[i] is null && IndexOfReference(agentMessages, targets[i]) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>对应 TS 的 <c>Array.prototype.indexOf</c>：按引用同一性定位。</summary>
    private static int IndexOfReference(IReadOnlyList<ChatMessage> messages, ChatMessage target)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (ReferenceEquals(messages[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    // =========================================================================
    // 提示词归一化
    // =========================================================================

    /// <summary>对应 TS <c>_normalizePromptSnippet</c>：折成单行，空白压成单个空格，空串回 null。</summary>
    /// <remarks>
    /// 差异 C91：TS 的 <c>.trim()</c> 用 JS 空白集，此处用 .NET <c>Trim()</c>（多含 U+0085 等少数码点）。
    /// 前置的 <c>\s+</c> 折叠已把 JS 空白集压成普通空格，故实际差异仅在「整串只由 .NET 特有空白构成」时出现。
    /// </remarks>
    public static string? NormalizePromptSnippet(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var oneLine = PromptSnippetWhitespace
            .Replace(PromptSnippetNewlines.Replace(text, " "), " ")
            .Trim();
        return oneLine.Length > 0 ? oneLine : null;
    }

    /// <summary>对应 TS <c>_normalizePromptGuidelines</c>：逐条 trim、丢空、按首次出现去重。</summary>
    public static IReadOnlyList<string> NormalizePromptGuidelines(IReadOnlyList<string>? guidelines)
    {
        if (guidelines is null || guidelines.Count == 0)
        {
            return [];
        }

        var unique = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var guideline in guidelines)
        {
            var normalized = guideline.Trim();
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                unique.Add(normalized);
            }
        }

        return unique;
    }
}
