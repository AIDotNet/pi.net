// ============================================================================
// core/bug-report.ts's generateBugReportSummary — the I/O wrapper around the
// pure leaves in BugReportSummary.cs (4e-3e-9e ported the decision logic; this
// batch lands the call itself).
// ============================================================================

using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>GenerateBugReportSummaryOptions</c>。</summary>
public sealed record GenerateBugReportSummaryOptions
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    public string? Hint { get; init; }

    public required ModelSpec Model { get; init; }

    public string? ApiKey { get; init; }

    public IReadOnlyDictionary<string, string?>? Headers { get; init; }

    /// <summary>provider 作用域的环境变量值。</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public CancellationToken Signal { get; init; }

    public ThinkingLevel? ThinkingLevel { get; init; }

    /// <summary>可选的会话流函数；保持 SDK 请求行为。</summary>
    public StreamFn? StreamFn { get; init; }

    /// <summary>瞬时摘要错误的重试策略（复用 settings.retry）。</summary>
    public RetryPolicy? Retry { get; init; }

    public string? SessionId { get; init; }
}

/// <summary>
/// 让当前模型为本会话写一份 bug 报告——用户不愿分享 transcript 时使用。
/// 对应 TS <c>generateBugReportSummary</c>（core/bug-report.ts L318-357）。
/// </summary>
public static partial class BugReport
{
    /// <summary>对应 TS <c>generateBugReportSummary</c>。</summary>
    public static async Task<string> GenerateBugReportSummaryAsync(GenerateBugReportSummaryOptions options)
    {
        var model = options.Model;
        var messages = SelectBugSummaryMessages(
            options.Messages, ResolveBugSummaryTokenBudget(model.ContextWindow), Compaction.EstimateTokens);
        var hint = options.Hint?.Trim();
        var conversation = CompactionUtils.SerializeConversation(AgentMessages.ConvertToLlm(messages));
        var prompt = BuildBugSummaryPrompt(messages.Count, options.Messages.Count, conversation, hint);
        var maxTokens = ResolveBugSummaryMaxTokens(model.MaxTokens);

        var requestOptions = new SimpleStreamOptions
        {
            ApiKey = options.ApiKey,
            Headers = options.Headers,
            Env = options.Env,
            Signal = options.Signal,
            MaxTokens = maxTokens > int.MaxValue ? int.MaxValue : (int)maxTokens,
            SessionId = options.SessionId,
        };
        // TS: `model.reasoning && thinkingLevel && thinkingLevel !== "off"`.
        if (model.Reasoning && options.ThinkingLevel is { } level && level != ThinkingLevel.Off)
        {
            requestOptions = requestOptions with { Reasoning = ThinkingLevelText.ToText(level) };
        }

        var context = Transcript.NormalizeContext(
            BugSummarySystemPrompt,
            null,
            [new UserMessage(
                MessageContent.BlocksOf([new TextContent(prompt)]),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);
        var response = await Compaction.CompleteSummarizationAsync(
            model, context, requestOptions, options.StreamFn, options.Retry, null, options.Signal).ConfigureAwait(false);
        return ValidateBugSummaryResponse(response);
    }
}
