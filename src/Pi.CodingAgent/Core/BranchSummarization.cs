using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.CodingAgent.Core;

// ============================================================================
// core/compaction/branch-summarization.ts — 树导航的分支摘要（4e-2d）
//
// 在会话树中导航到另一位置时，为被离开的分支生成摘要以免上下文丢失。
// ============================================================================

/// <summary>分支摘要生成结果。对应 TS <c>BranchSummaryResult</c>。</summary>
public sealed record BranchSummaryResult(
    string? Summary = null,
    Usage? Usage = null,
    IReadOnlyList<string>? ReadFiles = null,
    IReadOnlyList<string>? ModifiedFiles = null,
    bool? Aborted = null,
    string? Error = null);

/// <summary>BranchSummaryEntry.details 里存的文件跟踪信息。对应 TS <c>BranchSummaryDetails</c>。</summary>
public sealed record BranchSummaryDetails(IReadOnlyList<string> ReadFiles, IReadOnlyList<string> ModifiedFiles);

/// <summary>为摘要准备好的条目。对应 TS <c>BranchPreparation</c>。</summary>
public sealed record BranchPreparation(
    IReadOnlyList<ChatMessage> Messages,
    FileOperations FileOps,
    long TotalTokens);

/// <summary>条目收集结果。对应 TS <c>CollectEntriesResult</c>。</summary>
public sealed record CollectEntriesResult(IReadOnlyList<SessionEntry> Entries, string? CommonAncestorId);

/// <summary>分支摘要生成选项。对应 TS <c>GenerateBranchSummaryOptions</c>。</summary>
public sealed record GenerateBranchSummaryOptions
{
    public required ModelSpec Model { get; init; }

    public string? ApiKey { get; init; }

    public IReadOnlyDictionary<string, string?>? Headers { get; init; }

    /// <summary>provider 作用域的环境变量值。</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public CancellationToken Signal { get; init; }

    public string? CustomInstructions { get; init; }

    /// <summary>true 时 customInstructions 替换默认提示而非追加。</summary>
    public bool ReplaceInstructions { get; init; }

    /// <summary>选择分支历史时保留的 token（默认 16384）。</summary>
    public long ReserveTokens { get; init; } = 16384;

    /// <summary>可选的会话流函数；保持 SDK 请求行为而不改动 agent 状态。</summary>
    public StreamFn? StreamFn { get; init; }

    /// <summary>瞬时摘要错误的重试策略（复用 settings.retry）。</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>重试回调（如 TUI 的重试指示）。</summary>
    public RetryCallbacks? Callbacks { get; init; }
}

/// <summary>分支摘要。对应 TS <c>core/compaction/branch-summarization.ts</c>。</summary>
public static class BranchSummarization
{
    private const string BranchSummaryPreamble =
        "The user explored a different conversation branch before returning here.\nSummary of that exploration:\n\n";

    private const string BranchSummaryPrompt =
        "Create a structured summary of this conversation branch for context when returning later.\n\nUse this EXACT format:\n\n## Goal\n[What was the user trying to accomplish in this branch?]\n\n## Constraints & Preferences\n- [Any constraints, preferences, or requirements mentioned]\n- [Or \"(none)\" if none were mentioned]\n\n## Progress\n### Done\n- [x] [Completed tasks/changes]\n\n### In Progress\n- [ ] [Work that was started but not finished]\n\n### Blocked\n- [Issues preventing progress, if any]\n\n## Key Decisions\n- **[Decision]**: [Brief rationale]\n\n## Next Steps\n1. [What should happen next to continue this work]\n\nKeep each section concise. Preserve exact file paths, function names, and error messages.";

    // ───────────────────────────────────────────────────────────────────────
    // 条目收集
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 收集从一个位置导航到另一位置时应摘要的条目：从旧叶回溯到与目标的公共祖先。
    /// 不在压缩边界停下——它们被包含进来、其摘要成为上下文。
    /// 对应 TS <c>collectEntriesForBranchSummary</c>。
    /// </summary>
    public static CollectEntriesResult CollectEntriesForBranchSummary(
        SessionManager session, string? oldLeafId, string targetId)
    {
        if (oldLeafId is null)
        {
            return new CollectEntriesResult([], null);
        }

        // 找公共祖先（两条路径上都存在的最深节点）。
        var oldPath = session.GetBranch(oldLeafId).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var targetPath = session.GetBranch(targetId);

        // targetPath 是根优先的，倒序找最深公共祖先。
        string? commonAncestorId = null;
        for (var i = targetPath.Count - 1; i >= 0; i--)
        {
            if (targetPath[i].Id is { } id && oldPath.Contains(id))
            {
                commonAncestorId = id;
                break;
            }
        }

        // 从旧叶收集回公共祖先。
        var entries = new List<SessionEntry>();
        string? current = oldLeafId;
        while (current is not null && current != commonAncestorId)
        {
            var entry = session.GetEntry(current);
            if (entry is null) break;
            entries.Add(entry);
            current = entry.ParentId;
        }

        entries.Reverse();
        return new CollectEntriesResult(entries, commonAncestorId);
    }

    // ───────────────────────────────────────────────────────────────────────
    // 条目 → 消息
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>从会话条目提取 AgentMessage；压缩条目也处理。对应 TS <c>getMessageFromEntry</c>。</summary>
    private static ChatMessage? GetMessageFromEntry(SessionEntry entry) => entry switch
    {
        // 跳过 tool result——上下文在助手的工具调用里。
        SessionMessageEntry { Message: ToolResultMessage } => null,
        SessionMessageEntry messageEntry => messageEntry.Message,
        CustomMessageEntry custom => AgentMessages.CreateCustomMessage(
            custom.CustomType, custom.Content ?? MessageContent.From(""), custom.Display, custom.Details, custom.Timestamp),
        BranchSummaryEntry branchSummary => AgentMessages.CreateBranchSummaryMessage(
            branchSummary.Summary, branchSummary.FromId, branchSummary.Timestamp),
        CompactionEntry compaction => AgentMessages.CreateCompactionSummaryMessage(
            compaction.Summary, compaction.TokensBefore, compaction.Timestamp),
        // 这些不贡献会话内容。
        _ => null,
    };

    /// <summary>
    /// 按 token 预算准备要摘要的条目。从新到旧走、装消息直到预算。
    /// 同时收集文件操作（助手工具调用 + 既有 branch_summary 的 details 累计跟踪）。
    /// 对应 TS <c>prepareBranchEntries</c>。
    /// </summary>
    public static BranchPreparation PrepareBranchEntries(IReadOnlyList<SessionEntry> entries, long tokenBudget = 0)
    {
        var messages = new List<ChatMessage>();
        var fileOps = CompactionUtils.CreateFileOps();
        long totalTokens = 0;

        // 第一遍：从全部条目收集文件操作（哪怕装不进预算）——保证嵌套分支摘要的累计跟踪。
        // 只从 pi 生成的摘要提取（fromHook !== true），扩展生成的不算。
        foreach (var entry in entries)
        {
            if (entry is BranchSummaryEntry { FromHook: not true, Details: { } detailsObject })
            {
                var details = Compaction.DeserializeDetails(detailsObject);
                if (details is not null)
                {
                    foreach (var file in details.ReadFiles) fileOps.Read.Add(file);
                    foreach (var file in details.ModifiedFiles) fileOps.Edited.Add(file);
                }
            }
        }

        // 第二遍：从新到旧装消息直到 token 预算。
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            var message = GetMessageFromEntry(entry);
            if (message is null) continue;

            // 从助手消息（工具调用）提取文件操作。
            CompactionUtils.ExtractFileOpsFromMessage(message, fileOps);

            var tokens = Compaction.EstimateTokens(message);

            // 装入前先检查预算。
            if (tokenBudget > 0 && totalTokens + tokens > tokenBudget)
            {
                // 摘要类条目尽量塞进去（是重要的上下文）。
                if (entry is CompactionEntry or BranchSummaryEntry && totalTokens < (long)(tokenBudget * 0.9))
                {
                    messages.Insert(0, message);
                    totalTokens += tokens;
                }

                // 停——预算已满。
                break;
            }

            messages.Insert(0, message);
            totalTokens += tokens;
        }

        return new BranchPreparation(messages, fileOps, totalTokens);
    }

    // ───────────────────────────────────────────────────────────────────────
    // 摘要生成
    // ───────────────────────────────────────────────────────────────────────

    /// <summary>为被放弃的分支条目生成摘要。对应 TS <c>generateBranchSummary</c>。</summary>
    public static async Task<BranchSummaryResult> GenerateBranchSummaryAsync(
        IReadOnlyList<SessionEntry> entries, GenerateBranchSummaryOptions options)
    {
        // token 预算 = 上下文窗口减去提示 + 响应的保留空间。
        var contextWindow = options.Model.ContextWindow != 0 ? options.Model.ContextWindow : 128000;
        var tokenBudget = contextWindow - options.ReserveTokens;

        var preparation = PrepareBranchEntries(entries, tokenBudget);
        var fileOps = preparation.FileOps;

        if (preparation.Messages.Count == 0)
        {
            return new BranchSummaryResult(Summary: "No content to summarize");
        }

        // 先转 LLM 消息再序列化为文本——避免模型把它当成要继续的对话。
        var llmMessages = AgentMessages.ConvertToLlm(preparation.Messages);
        var conversationText = CompactionUtils.SerializeConversation(llmMessages);

        string instructions;
        if (options.ReplaceInstructions && options.CustomInstructions is not null)
        {
            instructions = options.CustomInstructions;
        }
        else if (options.CustomInstructions is not null)
        {
            instructions = $"{BranchSummaryPrompt}\n\nAdditional focus: {options.CustomInstructions}";
        }
        else
        {
            instructions = BranchSummaryPrompt;
        }

        var promptText = $"<conversation>\n{conversationText}\n</conversation>\n\n{instructions}";

        var summarizationMessages = new List<ChatMessage>
        {
            new UserMessage(
                MessageContent.BlocksOf([new TextContent(promptText)]),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        };

        // 不再固定 4096 上限：思考与输出共享预算时，固定上限会让分支摘要撞 token 上限（上游 #9512）。

        // 优先用会话流函数（SDK 请求行为一致）；经 completeSummarization 重试。
        var context = Transcript.NormalizeContext(
            CompactionUtils.SummarizationSystemPrompt, null, summarizationMessages);
        var requestOptions = new SimpleStreamOptions
        {
            ApiKey = options.ApiKey,
            Headers = options.Headers,
            Env = options.Env,
            Signal = options.Signal,
        };
        var response = await Compaction.CompleteSummarizationAsync(
            options.Model, context, requestOptions, options.StreamFn, options.Retry, options.Callbacks,
            options.Signal).ConfigureAwait(false);

        // 检查中止或错误。
        if (response.StopReason == StopReason.Aborted)
        {
            return new BranchSummaryResult(Aborted: true);
        }

        var failure = Compaction.GetSummarizationFailure(response, "Branch summarization");
        if (failure is not null)
        {
            return new BranchSummaryResult(Error: failure);
        }

        if (response.Content.Any(block => block is ToolCallContent))
        {
            return new BranchSummaryResult(Error: "Branch summarization attempted to call a tool");
        }

        var summary = Text.ContentText(response.Content);

        // 前置导语说明这是分支摘要。
        summary = BranchSummaryPreamble + summary;

        // 计算文件清单并追加到摘要。
        var (readFiles, modifiedFiles) = CompactionUtils.ComputeFileLists(fileOps);
        summary += CompactionUtils.FormatFileOperations(readFiles, modifiedFiles);

        return new BranchSummaryResult(
            Summary: summary.Length > 0 ? summary : "No summary generated",
            Usage: response.UsageStats,
            ReadFiles: readFiles,
            ModifiedFiles: modifiedFiles);
    }
}
