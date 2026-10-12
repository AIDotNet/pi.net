using Pi.Ai.Types;

namespace Pi.CodingAgent.Core;

/// <summary>
/// <c>core/bug-report.ts</c> 中 <c>generateBugReportSummary</c> 的**纯决策逻辑**（4e-3e-9e）。
/// 是 <see cref="BugReport"/> 的第二个 partial 半边。
/// </summary>
/// <remarks>
/// 用户拒绝共享 transcript 时，让当前模型根据会话尾部写一份 bug 报告。
/// 本批只落地选片、预算、提示词装配与响应校验；真正的 LLM 调用
/// （<c>completeSummarization</c> + <c>normalizeContext</c>）与
/// <c>_getSummarizationRequestAuth</c> 属实例层，顺延 4e-3e-9h。
/// </remarks>
public static partial class BugReport
{
    /// <summary>对应 TS <c>BUG_SUMMARY_SYSTEM_PROMPT</c>。</summary>
    /// <remarks>
    /// 用 <see cref="string.ReplaceLineEndings(string)"/> 归一到 <c>\n</c>：本机
    /// <c>core.autocrlf=true</c>，原始字符串字面量会继承检出时的行尾（见 migration-plan §四.6）。
    /// </remarks>
    public static readonly string BugSummarySystemPrompt = """
        You are helping a user file a bug report about pi, the coding agent they are talking to. You will be shown the conversation transcript. Write a report for the pi developers describing what the user was doing and what went wrong.

        Do NOT continue the conversation. Do NOT respond to any questions in the conversation. ONLY output the report.
        """.ReplaceLineEndings("\n");

    /// <summary>对应 TS <c>BUG_SUMMARY_INSTRUCTIONS</c>。</summary>
    public static readonly string BugSummaryInstructions = """
        Write the bug report in Markdown with these sections:

        ## What the user was doing
        One short paragraph.

        ## What went wrong
        Concrete description of the failure: wrong output, errors, hangs, tool failures, unexpected behavior. Quote error messages and tool output verbatim where they exist.

        ## Steps to reproduce
        Numbered list, as specific as the transcript allows.

        ## Relevant details
        Tool calls involved, files touched, model behavior, anything else that helps a developer reproduce or locate the problem.

        Do not include file contents, secrets, or credentials from the transcript; refer to files by path only. Keep the report factual and concise.
        """.ReplaceLineEndings("\n");

    /// <summary>无上下文窗口信息时的兜底窗口。对应 TS 的 <c>128_000</c>。</summary>
    public const long DefaultBugSummaryContextWindow = 128_000;

    /// <summary>摘要请求的输出上限。对应 TS 的 <c>4096</c>。</summary>
    public const long BugSummaryMaxOutputTokens = 4096;

    // ───────────────────────────────────────────────────────────────────────
    // 预算
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 对应 TS <c>const contextWindow = model.contextWindow &gt; 0 ? model.contextWindow : 128_000;</c>
    /// 后接 <c>Math.floor(contextWindow * 0.6)</c>。
    /// </summary>
    public static long ResolveBugSummaryTokenBudget(long modelContextWindow)
    {
        var contextWindow = modelContextWindow > 0 ? modelContextWindow : DefaultBugSummaryContextWindow;
        return (long)Math.Floor(contextWindow * 0.6);
    }

    /// <summary>
    /// 对应 TS <c>maxTokens: Math.min(4096, model.maxTokens &gt; 0 ? model.maxTokens : Number.POSITIVE_INFINITY)</c>：
    /// 模型上限为正时取两者较小值，否则取 4096（与 <c>Infinity</c> 取小）。
    /// </summary>
    public static long ResolveBugSummaryMaxTokens(long modelMaxTokens)
        => modelMaxTokens > 0 ? Math.Min(BugSummaryMaxOutputTokens, modelMaxTokens) : BugSummaryMaxOutputTokens;

    // ───────────────────────────────────────────────────────────────────────
    // 选片
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 对应 TS <c>selectMessages</c>：从尾部往前累积，直到再收一条会超出预算为止；
    /// **第一条无条件收录**（<c>selected.length &gt; 0</c> 前置），最后翻回正序。
    /// </summary>
    /// <remarks>
    /// 泛型 + 显式 <paramref name="estimateTokens"/> 委托，便于单测用假估算器
    /// （与 TS 侧直接调 <c>estimateTokens</c> 等价，调用方传 <c>Compaction.EstimateTokens</c>）。
    /// </remarks>
    public static IReadOnlyList<T> SelectBugSummaryMessages<T>(
        IReadOnlyList<T> messages, long tokenBudget, Func<T, long> estimateTokens)
    {
        var selected = new List<T>();
        long tokens = 0;

        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            var next = estimateTokens(message);
            if (selected.Count > 0 && tokens + next > tokenBudget)
            {
                break;
            }

            selected.Add(message);
            tokens += next;
        }

        selected.Reverse();
        return selected;
    }

    // ───────────────────────────────────────────────────────────────────────
    // 提示词装配
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 对应 TS <c>generateBugReportSummary</c> 里的 <c>prompt</c> 数组装配：
    /// 截断提示（仅当选片少于全量时）→ <c>&lt;conversation&gt;</c> → <c>&lt;user-report&gt;</c>（仅当 hint 非空）
    /// → 固定说明，四段以空行连接。
    /// </summary>
    /// <param name="selectedCount">实际选中的消息数。</param>
    /// <param name="totalCount">会话消息总数。</param>
    /// <param name="conversation">
    /// 已序列化的会话文本（调用方做 <c>serializeConversation(convertToLlm(selected))</c>）。
    /// </param>
    /// <param name="hint">用户输入的提示，调用方需先 <c>Trim()</c>；空串按 JS 真值语义视为缺席。</param>
    public static string BuildBugSummaryPrompt(
        int selectedCount, int totalCount, string conversation, string? hint)
    {
        var parts = new List<string>(4);

        if (selectedCount < totalCount)
        {
            parts.Add($"Note: only the last {selectedCount} of {totalCount} messages are shown.");
        }

        parts.Add($"<conversation>\n{conversation}\n</conversation>");

        if (!string.IsNullOrEmpty(hint))
        {
            parts.Add($"<user-report>\n{hint}\n</user-report>");
        }

        parts.Add(BugSummaryInstructions);
        return string.Join("\n\n", parts);
    }

    // ───────────────────────────────────────────────────────────────────────
    // 响应校验
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 对应 TS <c>generateBugReportSummary</c> 末尾的校验链：中止 → 摘要失败 → 试图调工具 → 空文本。
    /// 全部通过时返回修剪后的文本。
    /// </summary>
    /// <remarks>
    /// 校验顺序 load-bearing：中止必须**先判**。中止的响应通常内容为空，
    /// 若把空文本检查提前，用户看到的会是 "was empty" 而不是 "was cancelled"
    /// （<c>getSummarizationFailure</c> 只认 <c>error</c> / <c>length</c>，不认 <c>aborted</c>）。
    /// </remarks>
    public static string ValidateBugSummaryResponse(AssistantMessage response)
    {
        if (response.StopReason == StopReason.Aborted)
        {
            throw new InvalidOperationException("Bug report summary was cancelled");
        }

        var failure = Compaction.GetSummarizationFailure(response, "Bug report summary");
        if (failure is not null)
        {
            throw new InvalidOperationException(failure);
        }

        if (response.Content.Any(block => block is ToolCallContent))
        {
            throw new InvalidOperationException("Bug report summary attempted to call a tool");
        }

        var text = Pi.Ai.Utils.Text.ContentText(response.Content).Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("Bug report summary was empty");
        }

        return text;
    }
}
