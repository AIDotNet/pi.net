// ============================================================================
// AgentSession.summarizeForBugReport — the session entry of the /bug flow
// (core/bug-report.ts's generateBugReportSummary + agent-session.ts L4321-4335).
// ============================================================================

using Pi.Agent.Types;
using Pi.Ai.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>AgentSession 的 bug 报告摘要入口。对应 TS <c>summarizeForBugReport</c>。</summary>
public partial class AgentSession
{
    /// <summary>
    /// 让当前模型描述本会话出了什么问题——用户不分享 transcript 时改用摘要。
    /// 对应 TS <c>summarizeForBugReport</c>（agent-session.ts L4321-4335）。
    /// </summary>
    public async Task<string> SummarizeForBugReportAsync(string? hint, CancellationToken signal)
    {
        var model = Model ?? throw new InvalidOperationException("No model selected");
        // 与压缩同一条认证解析路径（虚拟模型先路由）。
        var request = await GetSummarizationRequestAuthAsync(model, signal).ConfigureAwait(true);
        var retrySettings = SettingsManager.GetRetrySettings();
        return await BugReport.GenerateBugReportSummaryAsync(new GenerateBugReportSummaryOptions
        {
            Messages = _agent.Messages,
            Hint = hint,
            Model = request.Model,
            ApiKey = request.ApiKey,
            Headers = request.Headers,
            Env = request.Env,
            ThinkingLevel = request.ThinkingLevel,
            Signal = signal,
            StreamFn = Agent.StreamFunction,
            Retry = new RetryPolicy
            {
                Enabled = retrySettings.Enabled,
                MaxRetries = retrySettings.MaxRetries,
                BaseDelayMs = retrySettings.BaseDelayMs,
                MaxAgentDelayMs = retrySettings.MaxAgentDelayMs,
            },
            SessionId = SessionId,
        }).ConfigureAwait(true);
    }
}
