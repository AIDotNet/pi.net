using System.Text.RegularExpressions;
using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
// `ToolDefinition` 在 Pi.Ai.Types（AI 层的元数据声明）与 Pi.CodingAgent.Core.Extensions（可执行工具定义）
// 各有一个，本文件的语义一律是后者。
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>ParsedSkillBlock</c>。</summary>
public sealed record ParsedSkillBlock(string Name, string Location, string Content, string? UserMessage);

/// <summary>对应 TS <c>_tryExecuteExtensionCommand</c> 的 name/args 解析。</summary>
public sealed record ParsedSlashCommand(string Name, string Args);

/// <summary>对应 TS <c>_expandSkillCommand</c> 的查找输入。</summary>
public sealed record SkillCommandSource(string Name, string FilePath, string BaseDir);

/// <summary>对应 TS <c>PromptOptions</c>。</summary>
public sealed record PromptOptions
{
    public bool ExpandPromptTemplates { get; init; } = true;

    public IReadOnlyList<ImageContent>? Images { get; init; }

    public string? StreamingBehavior { get; init; }

    public string Source { get; init; } = "interactive";

    /// <summary>
    /// 对应 TS <c>PromptOptions.preflightResult</c>：提交前各分支的处置回调
    /// （<see cref="PromptDisposition"/> 之一）。
    /// </summary>
    public Action<string>? PreflightResult { get; init; }
}

/// <summary>对应 TS <c>ModelMutationOptions</c>。</summary>
public sealed record ModelMutationOptions
{
    public bool Persist { get; init; }
}

/// <summary>对应 TS <c>ModelCycleResult</c>。</summary>
public sealed record ModelCycleResult
{
    public required ModelSpec Model { get; init; }

    public required ThinkingLevel ThinkingLevel { get; init; }

    public required bool IsScoped { get; init; }
}

/// <summary>对应 TS <c>SessionStats.tokens</c>。</summary>
public sealed record SessionTokenStats
{
    public long Input { get; init; }

    public long Output { get; init; }

    public long CacheRead { get; init; }

    public long CacheWrite { get; init; }

    public long Total { get; init; }
}

/// <summary>对应 TS <c>SessionStats</c>。</summary>
public sealed record SessionStats
{
    public string? SessionFile { get; init; }

    public required string SessionId { get; init; }

    public int UserMessages { get; init; }

    public int AssistantMessages { get; init; }

    public int ToolCalls { get; init; }

    public int ToolResults { get; init; }

    public int TotalMessages { get; init; }

    public required SessionTokenStats Tokens { get; init; }

    public double Cost { get; init; }

    /// <summary>上下文用量；无法判定时为 <c>null</c>。对应 TS <c>SessionStats.contextUsage?</c>。</summary>
    public Pi.CodingAgent.Core.Extensions.ContextUsage? ContextUsage { get; init; }
}

/// <summary>对应 TS <c>AgentSessionConfig</c>。</summary>
/// <remarks>
/// 差异 C148：TS 的 <c>agent</c> 是 pi-agent-core 的 <see cref="Pi.Agent.Agent"/> 实例；
/// TS 的 <c>Model&lt;any&gt;</c> 在端口分成 <see cref="Pi.Ai.Models.ModelSpec"/>（会话/目录层流通的
/// 运行时形状）与 <see cref="Pi.Ai.Types.Model"/>（Agent 低层持有的瘦形状，经
/// <see cref="CompatShims.ToModel"/> 桥接），会话自己的模型状态（<c>Model</c> getter / 扩展
/// <c>getModel</c>）以 <see cref="Pi.Ai.Models.ModelSpec"/> 为准——与 9h-2 的
/// <c>IExtensionContext.Model</c> 统一口径一致。
/// </remarks>
public sealed record AgentSessionConfig
{
    public required Pi.Agent.Agent Agent { get; init; }

    public required SessionManager SessionManager { get; init; }

    public required SettingsManager SettingsManager { get; init; }

    public required string Cwd { get; init; }

    public required DefaultResourceLoader ResourceLoader { get; init; }

    public required ModelRuntime ModelRuntime { get; init; }

    /// <summary>
    /// 会话初始模型。对应 TS 的 <c>agent.state.model</c>——TS 的会话模型状态就住在 agent 上，
    /// 构造会话时已由 <c>createAgentSession</c> 写入；端口因模型状态二分（差异 C148 / C161）
    /// 需要显式带进来，否则 <see cref="AgentSession.Model"/> 在首次 <c>setModel</c> 前为空。
    /// </summary>
    public ModelSpec? Model { get; init; }

    /// <summary>Ctrl+P 循环用的模型组（TS <c>scopedModels</c>，<c>--models</c> 旗标）。</summary>
    public IReadOnlyList<ScopedModel>? ScopedModels { get; init; }

    /// <summary>SDK 在扩展之外注册的自定义工具（TS <c>customTools</c>）。</summary>
    public IReadOnlyList<ToolDefinition>? CustomTools { get; init; }

    /// <summary>保持上一条请求的提示缓存温热（TS <c>cacheWarmer</c>）。</summary>
    public CacheWarmer? CacheWarmer { get; init; }

    /// <summary>初始活跃内置工具名。默认 read/bash/edit/write（TS <c>initialActiveToolNames</c>）。</summary>
    public IReadOnlyList<string>? InitialActiveToolNames { get; init; }

    /// <summary>初始工具是否来自 <c>defaultTools</code> 设置（TS <c>usesDefaultTools</c>）。</summary>
    public bool UsesDefaultTools { get; init; }

    /// <summary>
    /// 叠加在 <c>defaultTools</c> 设置之上的 <c>+name</c>/<c>-name</c> 条目（来自 <c>--tools</c>）。
    /// reload 会把它们作用到刷新后的设置上，使「被移除的工具保持移除」。TS <c>defaultToolModifiers</c>。
    /// </summary>
    public IReadOnlyList<string>? DefaultToolModifiers { get; init; }

    /// <summary><c>--tools</c> allowlist：工具名或 <c>*</c> 模式（TS <c>allowedToolNames</c>）。</summary>
    public IReadOnlyList<string>? AllowedToolNames { get; init; }

    /// <summary><c>--exclude-tools</c> denylist（TS <c>excludedToolNames</c>）。</summary>
    public IReadOnlyList<string>? ExcludedToolNames { get; init; }

    /// <summary>覆盖内置工具（自定义运行时用；TS <c>baseToolsOverride</c>）。</summary>
    public IReadOnlyDictionary<string, AgentTool>? BaseToolsOverride { get; init; }

    /// <summary>Agent 侧读取当前 ExtensionRunner 的可变引用（TS <c>extensionRunnerRef</c>）。</summary>
    public ExtensionRunnerRef? ExtensionRunnerRef { get; init; }

    /// <summary>扩展绑定本运行时发出的 session_start 元数据（TS <c>sessionStartEvent</c>）。</summary>
    public ExtensionEvent.SessionStart? SessionStartEvent { get; init; }
}

/// <summary>
/// 会话核心。对应 TS <c>core/agent-session.ts</c>。
/// 4e-3e-1：parseSkillBlock。4e-3e-3：getLastAssistantText / cycle。4e-3e-6：slash/skill 展开。
/// 4e-3e-7：agent_end 重试判定。4e-3e-8：prompt 预检（压缩/流式排队/出队）。prompt 循环主体顺延 4e-3e-9。
/// </summary>
public partial class AgentSession
{
    private static readonly Regex SkillBlockPattern = new(
        @"^<skill name=""([^""]+)"" location=""([^""]+)"">\n([\s\S]*?)\n</skill>(?:\n\n([\s\S]+))?$",
        RegexOptions.Compiled);

    /// <summary>对应 TS <c>parseSkillBlock</c>。</summary>
    public static ParsedSkillBlock? ParseSkillBlock(string text)
    {
        var match = SkillBlockPattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var userMessage = match.Groups[4].Success ? match.Groups[4].Value.Trim() : null;
        if (userMessage is { Length: 0 })
        {
            userMessage = null;
        }

        return new ParsedSkillBlock(
            match.Groups[1].Value,
            match.Groups[2].Value,
            match.Groups[3].Value,
            userMessage);
    }

    /// <summary>对应 TS <c>withoutDeletedHeaders</c>。</summary>
    public static IReadOnlyDictionary<string, string>? WithoutDeletedHeaders(
        IReadOnlyDictionary<string, string?>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        return headers
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.Ordinal);
    }

    /// <summary>对应 TS <c>getLastAssistantText</c>。</summary>
    public static string? GetLastAssistantText(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not AssistantMessage assistant)
            {
                continue;
            }

            if (assistant.StopReason == StopReason.Aborted && assistant.Content.Count == 0)
            {
                continue;
            }

            var text = string.Concat(assistant.Content.OfType<TextContent>().Select(block => block.Text)).Trim();
            return text.Length == 0 ? null : text;
        }

        return null;
    }

    /// <summary>对应 TS <c>cycleModel</c> 的环形下标。</summary>
    public static int NextCycleIndex(int currentIndex, int length, bool forward)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        return forward ? (currentIndex + 1) % length : (currentIndex - 1 + length) % length;
    }

    /// <summary>对应 TS <c>cycleThinkingLevel</c> 的下一档。</summary>
    public static ThinkingLevel? NextThinkingLevel(ThinkingLevel current, IReadOnlyList<ThinkingLevel> levels)
    {
        if (levels.Count == 0)
        {
            return null;
        }

        var currentIndex = -1;
        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i] == current)
            {
                currentIndex = i;
                break;
            }
        }

        return levels[(currentIndex + 1) % levels.Count];
    }

    /// <summary>对应 TS <c>_tryExecuteExtensionCommand</c> 的 name/args 切分。</summary>
    public static ParsedSlashCommand? ParseExtensionCommand(string text)
    {
        if (!text.StartsWith('/'))
        {
            return null;
        }

        var spaceIndex = text.IndexOf(' ');
        var name = spaceIndex == -1 ? text[1..] : text[1..spaceIndex];
        var args = spaceIndex == -1 ? string.Empty : text[(spaceIndex + 1)..];
        return new ParsedSlashCommand(name, args);
    }

    /// <summary>对应 TS <c>_expandSkillCommand</c> 的 /skill:name 解析。</summary>
    public static ParsedSlashCommand? ParseSkillCommand(string text)
    {
        const string prefix = "/skill:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var spaceIndex = text.IndexOf(' ');
        var name = spaceIndex == -1 ? text[prefix.Length..] : text[prefix.Length..spaceIndex];
        var args = spaceIndex == -1 ? string.Empty : text[(spaceIndex + 1)..].Trim();
        return new ParsedSlashCommand(name, args);
    }

    /// <summary>对应 TS <c>_expandSkillCommand</c>。查找失败或读文件失败时返回原文。</summary>
    public static string ExpandSkillCommand(
        string text,
        Func<string, SkillCommandSource?> findSkill,
        Func<string, string> readFile)
    {
        var parsed = ParseSkillCommand(text);
        if (parsed is null)
        {
            return text;
        }

        var skill = findSkill(parsed.Name);
        if (skill is null)
        {
            return text;
        }

        try
        {
            var body = Frontmatter.Strip(readFile(skill.FilePath)).Trim();
            var skillBlock =
                $"<skill name=\"{skill.Name}\" location=\"{skill.FilePath}\">\nReferences are relative to {skill.BaseDir}.\n\n{body}\n</skill>";
            return parsed.Args.Length > 0 ? $"{skillBlock}\n\n{parsed.Args}" : skillBlock;
        }
        catch
        {
            return text;
        }
    }

    /// <summary>对应 TS <c>prompt</c> 把 <c>normalized.hints</c> 拼到用户文本。</summary>
    public static string AppendPromptHints(string expandedText, IReadOnlyList<string> hints)
        => hints.Count == 0 ? expandedText : $"{expandedText}\n\n{string.Join("\n", hints)}";

    /// <summary>对应 TS <c>_willRetryAfterAgentEnd</c>。</summary>
    public static bool WillRetryAfterAgentEnd(
        string eventType,
        IReadOnlyList<ChatMessage> messages,
        bool retryEnabled)
    {
        if (eventType != "agent_end")
        {
            return false;
        }

        if (!retryEnabled)
        {
            return false;
        }

        if (messages.Count == 0)
        {
            return false;
        }

        return messages[^1] is AssistantMessage last
            && last.StopReason == StopReason.Error
            && Retry.IsRetryableAssistantError(last);
    }

    /// <summary>对应 TS <c>agent_before_settle</c> 的 continue / queued / canContinue 判定。</summary>
    public static bool ShouldContinueAfterBeforeSettle(bool resultContinue, bool hasQueuedMessages, bool canContinue)
    {
        var shouldContinue = resultContinue || hasQueuedMessages;
        return shouldContinue && canContinue;
    }

    /// <summary>对应 TS prompt 在 compaction 进行中拒绝提交。</summary>
    public static void ThrowIfCompactionInProgress(bool compactionInProgress)
    {
        if (compactionInProgress)
        {
            throw new InvalidOperationException(
                "Cannot submit a prompt while compaction is in progress. Wait for compaction to finish and retry.");
        }
    }

    /// <summary>对应 TS prompt 在 <c>isStreaming</c> 时选择 steer / followUp，否则走主循环。</summary>
    public static string ResolveStreamingQueueAction(bool isStreaming, string? streamingBehavior)
    {
        if (!isStreaming)
        {
            return "prompt";
        }

        if (string.IsNullOrEmpty(streamingBehavior))
        {
            throw new InvalidOperationException(
                "Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.");
        }

        return streamingBehavior == "followUp" ? "followUp" : "steer";
    }

    /// <summary>对应 TS <c>message_start</c> 用户文本从 steering 优先、否则 followUp 出队。</summary>
    public static (List<string> Steering, List<string> FollowUp) DequeueMatchingUserText(
        IEnumerable<string> steering,
        IEnumerable<string> followUp,
        string messageText)
    {
        var steeringList = steering.ToList();
        var followUpList = followUp.ToList();
        if (messageText.Length == 0)
        {
            return (steeringList, followUpList);
        }

        var steeringIndex = steeringList.IndexOf(messageText);
        if (steeringIndex >= 0)
        {
            steeringList.RemoveAt(steeringIndex);
            return (steeringList, followUpList);
        }

        var followUpIndex = followUpList.IndexOf(messageText);
        if (followUpIndex >= 0)
        {
            followUpList.RemoveAt(followUpIndex);
        }

        return (steeringList, followUpList);
    }

    /// <summary>对应 TS prompt 在 OAuth 凭证失效时的错误文案。</summary>
    public static string FormatExpiredOAuthMessage(string provider)
        => $"Authentication failed for \"{provider}\". Credentials may have expired or network is unavailable. Run '/login {provider}' to re-authenticate.";
}
