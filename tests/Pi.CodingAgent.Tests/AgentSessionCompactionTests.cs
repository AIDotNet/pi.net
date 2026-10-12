using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 4e-3e-9c：<c>agent-session.ts</c> 中 compaction / retry / bash 的纯决策逻辑。
/// 逐条对照 TS 参考实现（<c>D:\AI\参考项目\pi\packages\coding-agent\src\core\agent-session.ts</c>）。
/// </summary>
public sealed class AgentSessionCompactionTests
{
    // ---------------------------------------------------------------------
    // 构造辅助
    // ---------------------------------------------------------------------

    private static AssistantMessage Assistant(
        StopReason stopReason = StopReason.Stop,
        string? errorMessage = null,
        Usage? usage = null,
        string? provider = null,
        string? model = null)
        => new([new TextContent("x")], stopReason, errorMessage, usage, model, null, provider);

    private static SessionMessageEntry MessageEntry(string id, ChatMessage message)
        => new() { Id = id, Message = message, Timestamp = "2026-01-01T00:00:00.000Z" };

    private static ContextEditEntry EditEntry(string id, string targetId, bool removes = false)
        => new()
        {
            Id = id,
            TargetId = targetId,
            Replacement = removes ? null : new ContextEditReplacement { Content = new JsonObject { ["type"] = "text" } },
            Timestamp = "2026-01-01T00:00:00.000Z",
        };

    private static SessionProjection Projection(params ProjectedSessionEntry[] entries)
        => new(entries, [.. entries.SelectMany(entry => entry.Messages)], "medium", null);

    private static ProjectedSessionEntry Projected(SessionEntry source, params ChatMessage[] messages)
        => new(source, messages);

    private static BashResult Bash(string output = "hi", int? exitCode = 0, string? fullOutputPath = null)
        => new()
        {
            Output = output,
            ExitCode = exitCode,
            Cancelled = false,
            Truncated = false,
            FullOutputPath = fullOutputPath,
        };

    private static readonly long CompactionMs =
        DateTimeOffset.Parse("2026-06-01T00:00:00.000Z", null).ToUnixTimeMilliseconds();

    // ---------------------------------------------------------------------
    // 时间戳
    // ---------------------------------------------------------------------

    [Fact]
    public void ParseEntryTimestampMs_ParsesIsoAndReturnsNullOnGarbage()
    {
        Assert.Equal(
            DateTimeOffset.Parse("2026-06-01T00:00:00.000Z", null).ToUnixTimeMilliseconds(),
            AgentSession.ParseEntryTimestampMs("2026-06-01T00:00:00.000Z"));

        // TS 的 new Date("...").getTime() 为 NaN —— 端口用 null 表示同一语义。
        Assert.Null(AgentSession.ParseEntryTimestampMs("not a date"));
        Assert.Null(AgentSession.ParseEntryTimestampMs(""));
    }

    [Fact]
    public void IsAtOrBeforeCompaction_RequiresCompactionTimestamp()
    {
        // 无压缩条目 → TS 的 compactionEntry !== null 为假 → 恒假。
        Assert.False(AgentSession.IsAtOrBeforeCompaction(CompactionMs - 1000, null));
        Assert.False(AgentSession.IsAtOrBeforeCompaction(CompactionMs + 1000, null));

        // 相等算「在压缩之前或同一时刻」（TS 用 <=）。
        Assert.True(AgentSession.IsAtOrBeforeCompaction(CompactionMs, CompactionMs));
        Assert.True(AgentSession.IsAtOrBeforeCompaction(CompactionMs - 1, CompactionMs));
        Assert.False(AgentSession.IsAtOrBeforeCompaction(CompactionMs + 1, CompactionMs));
    }

    [Fact]
    public void IsStaleUsageSource_RequiresAssistantUsageAndAtOrBeforeCompaction()
    {
        // usage 来源不是助手消息 → 不算 stale（TS 要求 usageMsg.role === "assistant"）。
        Assert.False(AgentSession.IsStaleUsageSource(false, CompactionMs - 1, CompactionMs));

        // 时间戳解析失败（null）→ 不算 stale。
        Assert.False(AgentSession.IsStaleUsageSource(true, null, CompactionMs));

        Assert.True(AgentSession.IsStaleUsageSource(true, CompactionMs, CompactionMs));
        Assert.False(AgentSession.IsStaleUsageSource(true, CompactionMs + 1, CompactionMs));
        // 无压缩条目 → 不算 stale。
        Assert.False(AgentSession.IsStaleUsageSource(true, CompactionMs - 1, null));
    }

    // ---------------------------------------------------------------------
    // _checkCompaction 准入与投影判定
    // ---------------------------------------------------------------------

    [Fact]
    public void ShouldSkipCompactionCheck_RespectsEnabledAndAborted()
    {
        // 未启用压缩 → 一律跳过。
        Assert.True(AgentSession.ShouldSkipCompactionCheck(false, true, StopReason.Stop));
        Assert.True(AgentSession.ShouldSkipCompactionCheck(false, false, StopReason.Error));

        // 启用 + 需跳过中止检查 + 被中止 → 跳过。
        Assert.True(AgentSession.ShouldSkipCompactionCheck(true, true, StopReason.Aborted));
        // skipAbortedCheck 为 false（prompt 前置检查）时不跳过中止消息。
        Assert.False(AgentSession.ShouldSkipCompactionCheck(true, false, StopReason.Aborted));

        Assert.False(AgentSession.ShouldSkipCompactionCheck(true, true, StopReason.Error));
        Assert.False(AgentSession.ShouldSkipCompactionCheck(true, true, StopReason.Stop));
    }

    [Fact]
    public void IsAssistantProjected_TrueWhenEntryUnpersisted()
    {
        var projection = Projection();
        Assert.True(AgentSession.IsAssistantProjected(null, projection));
    }

    [Fact]
    public void IsAssistantProjected_RequiresProjectedAssistantFromSameSource()
    {
        var assistant = Assistant();
        var entry = MessageEntry("e1", assistant);
        var projection = Projection(Projected(entry, assistant));

        Assert.True(AgentSession.IsAssistantProjected("e1", projection));

        // 源条目存在，但它贡献的消息里没有助手消息（例如已被 context_edit 剔除）。
        var userOnly = Projection(Projected(entry, new UserMessage("u", 0)));
        Assert.False(AgentSession.IsAssistantProjected("e1", userOnly));

        // 投影里根本没有该源条目。
        Assert.False(AgentSession.IsAssistantProjected("e2", projection));
    }

    [Fact]
    public void HasContextEditEntry_DetectsContextEdits()
    {
        var entries = new List<SessionEntry>
        {
            MessageEntry("e1", new UserMessage("u", 0)),
            EditEntry("e2", "e1"),
        };

        Assert.True(AgentSession.HasContextEditEntry(entries));
        Assert.False(AgentSession.HasContextEditEntry(entries.Take(1)));
        Assert.False(AgentSession.HasContextEditEntry([]));
    }

    [Fact]
    public void FindLatestContextEditFor_ReturnsLastMatchingTarget()
    {
        var entries = new List<SessionEntry>
        {
            EditEntry("e1", "target"),
            EditEntry("e2", "other"),
            EditEntry("e3", "target"),
            EditEntry("e4", "target"),
        };

        // TS 用 .at(-1)：取最后一条命中。
        Assert.Equal("e4", AgentSession.FindLatestContextEditFor(entries, "target")?.Id);
        Assert.Equal("e2", AgentSession.FindLatestContextEditFor(entries, "other")?.Id);
        Assert.Null(AgentSession.FindLatestContextEditFor(entries, "missing"));
        Assert.Null(AgentSession.FindLatestContextEditFor(entries, null));
    }

    [Fact]
    public void IsAssistantRetainedForExplicitRecovery_HandlesUnpersistedAndRemovedCases()
    {
        // 未落盘（entryId 为 undefined）→ 恒为 true。
        Assert.True(AgentSession.IsAssistantRetainedForExplicitRecovery(null, true, true));

        // 之后有 compaction 条目 → 不再保留。
        Assert.False(AgentSession.IsAssistantRetainedForExplicitRecovery("e1", true, false));
        // 最近一次 context_edit 把助手剔除 → 不再保留。
        Assert.False(AgentSession.IsAssistantRetainedForExplicitRecovery("e1", false, true));
        // 没有 compaction、也没有剔除性编辑 → 保留。
        Assert.True(AgentSession.IsAssistantRetainedForExplicitRecovery("e1", false, false));
    }

    // ---------------------------------------------------------------------
    // 溢出分类
    // ---------------------------------------------------------------------

    [Fact]
    public void IsExplicitOverflow_RequiresErrorStopReason()
    {
        Assert.True(AgentSession.IsExplicitOverflow(StopReason.Error, true));
        // 非错误停止原因下，即便文案像溢出也不算 explicit。
        Assert.False(AgentSession.IsExplicitOverflow(StopReason.Stop, true));
        Assert.False(AgentSession.IsExplicitOverflow(StopReason.Length, true));
        Assert.False(AgentSession.IsExplicitOverflow(StopReason.Error, false));
    }

    [Fact]
    public void IsContextOverflowDetected_RequiresSameModelAndOneOfTwoPaths()
    {
        // 不同模型 → 恒假（切模型后旧模型的溢出不该触发压缩）。
        Assert.False(AgentSession.IsContextOverflowDetected(false, true, true, true, true));

        // 路径一：explicit overflow + 助手被保留以便显式恢复。
        Assert.True(AgentSession.IsContextOverflowDetected(true, true, true, false, false));
        // explicit overflow 但助手已被剔除 → 路径一不成立。
        Assert.False(AgentSession.IsContextOverflowDetected(true, true, false, false, false));

        // 路径二：usage 与投影一致 + 带窗口的溢出判定命中。
        Assert.True(AgentSession.IsContextOverflowDetected(true, false, false, true, true));
        // usage 与投影不一致 → 路径二不成立。
        Assert.False(AgentSession.IsContextOverflowDetected(true, false, false, false, true));

        // 两条路径都不成立。
        Assert.False(AgentSession.IsContextOverflowDetected(true, false, false, false, false));
    }

    [Fact]
    public void IsRecoverableLengthDetected_RequiresSameModelAndProjection()
    {
        Assert.True(AgentSession.IsRecoverableLengthDetected(true, true, true));
        Assert.False(AgentSession.IsRecoverableLengthDetected(false, true, true));
        Assert.False(AgentSession.IsRecoverableLengthDetected(true, false, true));
        Assert.False(AgentSession.IsRecoverableLengthDetected(true, true, false));
    }

    [Fact]
    public void ShouldRetryAfterOverflow_AnythingButStop()
    {
        // TS：willRetry = stopReason !== "stop"。只有自然结束的响应不可 continue()。
        Assert.False(AgentSession.ShouldRetryAfterOverflow(StopReason.Stop));
        Assert.True(AgentSession.ShouldRetryAfterOverflow(StopReason.Error));
        Assert.True(AgentSession.ShouldRetryAfterOverflow(StopReason.Length));
        Assert.True(AgentSession.ShouldRetryAfterOverflow(StopReason.Aborted));
        Assert.True(AgentSession.ShouldRetryAfterOverflow(StopReason.ToolUse));
    }

    [Fact]
    public void FormatOverflowRecoveryFailureMessage_DistinguishesOverflowFromTruncation()
    {
        Assert.Equal(
            "Context overflow recovery failed after one compact-and-retry attempt. Try reducing context or switching to a larger-context model.",
            AgentSession.FormatOverflowRecoveryFailureMessage(contextOverflow: true));
        Assert.Equal(
            "Truncated response recovery failed after one compact-and-retry attempt.",
            AgentSession.FormatOverflowRecoveryFailureMessage(contextOverflow: false));
    }

    [Fact]
    public void ResolveCompactionTokenSource_PrefersEditsThenEstimateThenDirectUsage()
    {
        // 有 context_edit → 投影估算（优先级最高，与 stopReason 无关）。
        Assert.Equal(
            CompactionTokenSource.ProjectedEstimate,
            AgentSession.ResolveCompactionTokenSource(true, StopReason.Error, 0));
        Assert.Equal(
            CompactionTokenSource.ProjectedEstimate,
            AgentSession.ResolveCompactionTokenSource(true, StopReason.Stop, 12345));

        // 错误响应 → 消息估算。
        Assert.Equal(
            CompactionTokenSource.MessageEstimate,
            AgentSession.ResolveCompactionTokenSource(false, StopReason.Error, 12345));

        // 零用量 → 消息估算（全零 usage 会破坏上下文记账，必须退回估算）。
        Assert.Equal(
            CompactionTokenSource.MessageEstimate,
            AgentSession.ResolveCompactionTokenSource(false, StopReason.Stop, 0));

        // 其余 → 直接取 usage。
        Assert.Equal(
            CompactionTokenSource.DirectUsage,
            AgentSession.ResolveCompactionTokenSource(false, StopReason.Stop, 12345));
        Assert.Equal(
            CompactionTokenSource.DirectUsage,
            AgentSession.ResolveCompactionTokenSource(false, StopReason.Aborted, 12345));
    }

    // ---------------------------------------------------------------------
    // _runAutoCompaction / compact
    // ---------------------------------------------------------------------

    [Fact]
    public void ShouldContinueAfterAutoCompaction_ContinuesOnRetryOrQueued()
    {
        Assert.True(AgentSession.ShouldContinueAfterAutoCompaction(willRetry: true, hasQueuedMessages: false));
        Assert.True(AgentSession.ShouldContinueAfterAutoCompaction(willRetry: false, hasQueuedMessages: true));
        Assert.True(AgentSession.ShouldContinueAfterAutoCompaction(true, true));
        Assert.False(AgentSession.ShouldContinueAfterAutoCompaction(false, false));
    }

    [Fact]
    public void FormatAutoCompactionFailureMessage_UndefinedWhenAborted()
    {
        // 中止 → TS 给 undefined（用户主动取消不是失败）。
        Assert.Null(AgentSession.FormatAutoCompactionFailureMessage(true, CompactionReason.Overflow, "boom"));
        Assert.Null(AgentSession.FormatAutoCompactionFailureMessage(true, CompactionReason.Threshold, "boom"));

        Assert.Equal(
            "Context overflow recovery failed: boom",
            AgentSession.FormatAutoCompactionFailureMessage(false, CompactionReason.Overflow, "boom"));
        Assert.Equal(
            "Auto-compaction failed: boom",
            AgentSession.FormatAutoCompactionFailureMessage(false, CompactionReason.Threshold, "boom"));
        // manual 走的是 compact() 的另一条文案分支。
        Assert.Equal(
            "Auto-compaction failed: boom",
            AgentSession.FormatAutoCompactionFailureMessage(false, CompactionReason.Manual, "boom"));
    }

    [Fact]
    public void ShouldClearAutoCompactionController_UsesReferenceIdentity()
    {
        var controller = new object();
        var other = new object();

        Assert.True(AgentSession.ShouldClearAutoCompactionController(controller, controller));
        // 内容无关的两个对象不相等（TS 的 === 是引用同一性）。
        Assert.False(AgentSession.ShouldClearAutoCompactionController(other, controller));
        Assert.False(AgentSession.ShouldClearAutoCompactionController(null, controller));
        Assert.True(AgentSession.ShouldClearAutoCompactionController(null, null));
    }

    [Fact]
    public void FormatManualCompactionPreparationFailure_DistinguishesAlreadyCompacted()
    {
        Assert.Equal("Already compacted", AgentSession.FormatManualCompactionPreparationFailure(true));
        Assert.Equal(
            "Nothing to compact (session too small)",
            AgentSession.FormatManualCompactionPreparationFailure(false));
    }

    [Fact]
    public void IsManualCompactionAborted_OrOfTwoSources()
    {
        Assert.True(AgentSession.IsManualCompactionAborted(true, false));
        Assert.True(AgentSession.IsManualCompactionAborted(false, true));
        Assert.True(AgentSession.IsManualCompactionAborted(true, true));
        Assert.False(AgentSession.IsManualCompactionAborted(false, false));
    }

    [Fact]
    public void FormatManualCompactionFailureMessage_UndefinedWhenAborted()
    {
        Assert.Null(AgentSession.FormatManualCompactionFailureMessage(true, "boom"));
        Assert.Equal("Compaction failed: boom", AgentSession.FormatManualCompactionFailureMessage(false, "boom"));
        Assert.Equal("Compaction cancelled", AgentSession.CompactionCancelledMessage);
    }

    // ---------------------------------------------------------------------
    // 模型溯源与选择记录
    // ---------------------------------------------------------------------

    [Fact]
    public void IsModelForMessage_MatchesProviderAndId()
    {
        Assert.True(AgentSession.IsModelForMessage("anthropic", "claude-x", "anthropic", "claude-x"));

        Assert.False(AgentSession.IsModelForMessage(null, "claude-x", "anthropic", "claude-x"));
        Assert.False(AgentSession.IsModelForMessage("anthropic", "claude-x", "openai", "claude-x"));
        Assert.False(AgentSession.IsModelForMessage("anthropic", "claude-x", "anthropic", "claude-y"));
        // message.model 缺失（undefined）时 TS 的 `model.id === undefined` 恒假。
        Assert.False(AgentSession.IsModelForMessage("anthropic", "claude-x", "anthropic", null));
    }

    [Fact]
    public void ShouldRecordSelection_RequiresDifferenceAndVirtualInvolvement()
    {
        var recorded = new BranchModelSelection("anthropic", "claude-x");

        // 分支上没有记录 → 不写。
        Assert.False(AgentSession.ShouldRecordSelection(null, "anthropic", "claude-y", false, null));

        // 记录与当前选择相同 → 不写。
        Assert.False(AgentSession.ShouldRecordSelection(
            recorded, "anthropic", "claude-x", false, false));

        // 选择不同但两边都不是虚拟模型 → 不写（否则每次 prompt 都会写 model_change）。
        Assert.False(AgentSession.ShouldRecordSelection(
            recorded, "anthropic", "claude-y", false, false));
        // 记录解析不出模型 → 同样不写。
        Assert.False(AgentSession.ShouldRecordSelection(
            recorded, "anthropic", "claude-y", false, null));

        // 当前选择是虚拟模型 → 写。
        Assert.True(AgentSession.ShouldRecordSelection(
            recorded, "anthropic", "claude-y", true, false));
        // 记录解析出的模型是虚拟模型 → 写。
        Assert.True(AgentSession.ShouldRecordSelection(
            recorded, "anthropic", "claude-y", false, true));
    }

    // ---------------------------------------------------------------------
    // Retry
    // ---------------------------------------------------------------------

    [Fact]
    public void ShouldEmitCancelledRetry_SkipsZero()
    {
        Assert.False(AgentSession.ShouldEmitCancelledRetry(0));
        Assert.True(AgentSession.ShouldEmitCancelledRetry(1));
        Assert.True(AgentSession.ShouldEmitCancelledRetry(3));
    }

    [Fact]
    public void AdvanceRetryAttempt_RespectsEnabledAndBudget()
    {
        // 未启用 → 计数不动。
        Assert.Equal((false, 0), AgentSession.AdvanceRetryAttempt(false, 0, 3));
        Assert.Equal((false, 2), AgentSession.AdvanceRetryAttempt(false, 2, 3));

        // 预算内 → 递增。
        Assert.Equal((true, 1), AgentSession.AdvanceRetryAttempt(true, 0, 3));
        Assert.Equal((true, 3), AgentSession.AdvanceRetryAttempt(true, 2, 3));

        // 超出预算 → 回退到原值（供 post-run 报出最终失败次数）。
        Assert.Equal((false, 3), AgentSession.AdvanceRetryAttempt(true, 3, 3));
        Assert.Equal((false, 4), AgentSession.AdvanceRetryAttempt(true, 4, 3));

        // maxRetries 为 0 → 一次都不重试，且计数保持 0。
        Assert.Equal((false, 0), AgentSession.AdvanceRetryAttempt(true, 0, 0));
    }

    [Fact]
    public void ResolveRetryErrorMessage_TreatsEmptyAsUnknown()
    {
        Assert.Equal("Unknown error", AgentSession.ResolveRetryErrorMessage(null));
        // TS 的 `||` 把空串也视为假。
        Assert.Equal("Unknown error", AgentSession.ResolveRetryErrorMessage(""));
        Assert.Equal("boom", AgentSession.ResolveRetryErrorMessage("boom"));
    }

    [Fact]
    public void IsRetryableError_ExcludesContextOverflow()
    {
        // 同时命中「可重试」与「上下文溢出」：溢出优先，不重试。
        var both = Assistant(StopReason.Error, "429 too many tokens");
        Assert.True(Overflow.IsContextOverflow(both));
        Assert.True(Retry.IsRetryableAssistantError(both));
        Assert.False(AgentSession.IsRetryableError(both, 200_000));

        // 纯溢出文案 → 不重试。
        var overflow = Assistant(StopReason.Error, "prompt is too long: 300000 tokens > 200000 maximum");
        Assert.True(Overflow.IsContextOverflow(overflow));
        Assert.False(AgentSession.IsRetryableError(overflow, 200_000));

        // 瞬时限流（非溢出）→ 重试。
        var transient = Assistant(StopReason.Error, "503 Service Unavailable");
        Assert.False(Overflow.IsContextOverflow(transient, 200_000));
        Assert.True(AgentSession.IsRetryableError(transient, 200_000));

        // 账户限额 → 不可重试。
        var quota = Assistant(StopReason.Error, "insufficient_quota");
        Assert.False(AgentSession.IsRetryableError(quota, 200_000));

        // 静默溢出（stop 但输入超窗）：带窗口的溢出判定命中 → 不重试。
        var silent = Assistant(StopReason.Stop, usage: new Usage(300_000, 0));
        Assert.True(Overflow.IsContextOverflow(silent, 200_000));
        Assert.False(AgentSession.IsRetryableError(silent, 200_000));
    }

    [Fact]
    public async Task CreateSummarizationRetryCallbacks_WiresAllThreeEvents()
    {
        var source = new SummarizationRetrySource
        {
            Source = SummarizationRetrySourceKind.Compaction,
            Reason = CompactionReason.Threshold,
        };

        var scheduled = new List<(int Attempt, int MaxAttempts, long DelayMs, string Error)>();
        var attemptStarts = new List<SummarizationRetrySource>();
        var finished = 0;

        var callbacks = AgentSession.CreateSummarizationRetryCallbacks(
            source,
            (attempt, maxAttempts, delayMs, errorMessage) =>
                scheduled.Add((attempt, maxAttempts, delayMs, errorMessage)),
            attemptStarts.Add,
            () => finished++);

        await callbacks.OnRetryScheduled!(2, 3, 500, "boom");
        await callbacks.OnRetryAttemptStart!();
        await callbacks.OnRetryFinished!(false, 3, "boom");

        Assert.Equal([(2, 3, 500L, "boom")], scheduled);
        // attempt_start 携带调用方给的 source（compaction + reason）。
        Assert.Equal([source], attemptStarts);
        Assert.Equal(1, finished);
    }

    // ---------------------------------------------------------------------
    // Bash
    // ---------------------------------------------------------------------

    [Fact]
    public void ApplyShellCommandPrefix_SkipsEmptyPrefix()
    {
        Assert.Equal("shopt -s expand_aliases\nls", AgentSession.ApplyShellCommandPrefix("shopt -s expand_aliases", "ls"));
        // TS 的空串为假 → 不拼接（也不留换行）。
        Assert.Equal("ls", AgentSession.ApplyShellCommandPrefix("", "ls"));
        Assert.Equal("ls", AgentSession.ApplyShellCommandPrefix(null, "ls"));
    }

    [Fact]
    public void CreateBashExecutionMessage_CarriesResultFields()
    {
        var result = Bash("output text", exitCode: 2, fullOutputPath: "/tmp/full.txt");

        var message = AgentSession.CreateBashExecutionMessage("ls", result, excludeFromContext: true, 1234);

        Assert.Equal("ls", message.Command);
        Assert.Equal("output text", message.Output);
        Assert.Equal(2, message.ExitCode);
        Assert.False(message.Cancelled);
        Assert.False(message.Truncated);
        Assert.Equal("/tmp/full.txt", message.FullOutputPath);
        Assert.True(message.ExcludeFromContext);
        Assert.Equal(1234, message.Timestamp);

        // 未给 options → excludeFromContext 为 null（序列化时省略该字段）。
        var noOptions = AgentSession.CreateBashExecutionMessage("ls", result, null, 0);
        Assert.Null(noOptions.ExcludeFromContext);
    }

    [Fact]
    public void ShouldDeferBashMessage_FollowsStreaming()
    {
        Assert.True(AgentSession.ShouldDeferBashMessage(true));
        Assert.False(AgentSession.ShouldDeferBashMessage(false));
    }

    [Fact]
    public void TakePendingBashMessages_ReturnsCopiesAndEmpties()
    {
        var pending = new List<BashExecutionMessage>
        {
            AgentSession.CreateBashExecutionMessage("a", Bash(), null, 1),
            AgentSession.CreateBashExecutionMessage("b", Bash(), null, 2),
        };

        var taken = AgentSession.TakePendingBashMessages(pending);

        Assert.Equal(2, taken.Count);
        Assert.Equal("a", taken[0].Command);
        Assert.Equal("b", taken[1].Command);
        Assert.Empty(pending);

        // 取出后再往入参塞东西，不影响已返回的结果。
        pending.Add(AgentSession.CreateBashExecutionMessage("c", Bash(), null, 3));
        Assert.Equal(2, taken.Count);

        // 空列表返回空（对应 TS 的提前 return）。
        var empty = new List<BashExecutionMessage>();
        Assert.Empty(AgentSession.TakePendingBashMessages(empty));
    }
}
