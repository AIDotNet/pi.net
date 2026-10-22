using System.Globalization;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Core.Extensions;

namespace Pi.CodingAgent.Core;

/// <summary>TS <c>_runDefaultCompaction</c> / <c>_checkCompaction</c> / <c>_runAutoCompaction</c> 的 reason 取值。</summary>
public static class CompactionReason
{
    public const string Manual = "manual";

    public const string Threshold = "threshold";

    public const string Overflow = "overflow";
}

/// <summary>TS <c>_summarizationRetryCallbacks</c> 的 <c>source</c> 取值。</summary>
public static class SummarizationRetrySourceKind
{
    public const string Compaction = "compaction";

    public const string BranchSummary = "branchSummary";
}

/// <summary>
/// 对应 TS <c>_summarizationRetryCallbacks</c> 的 <c>source</c> 入参
/// （<c>{ source: "branchSummary" } | { source: "compaction"; reason: ... }</c>）。
/// </summary>
public sealed record SummarizationRetrySource
{
    /// <summary>见 <see cref="SummarizationRetrySourceKind"/>。</summary>
    public required string Source { get; init; }

    /// <summary>见 <see cref="CompactionReason"/>；<c>branchSummary</c> 分支下为 <c>null</c>。</summary>
    public string? Reason { get; init; }
}

/// <summary>分支上记录的模型选择。对应 TS <c>getBranchSelection()</c> 的返回值。</summary>
public sealed record BranchModelSelection(string Provider, string ModelId);

/// <summary><c>_checkCompaction</c> 案例 3 里 <c>contextTokens</c> 的取值来源。</summary>
public enum CompactionTokenSource
{
    /// <summary>有 context_edit → 走投影估算（<c>estimateProjectedContextTokens</c>）。</summary>
    ProjectedEstimate,

    /// <summary>错误响应或零用量 → 走消息估算（<c>estimateContextTokens</c>），另需 stale-usage 短路。</summary>
    MessageEstimate,

    /// <summary>直接取助手 usage（<c>calculateContextTokens</c>）。</summary>
    DirectUsage,
}

/// <summary>
/// <c>core/agent-session.ts</c> 中 compaction / retry / bash 的**纯决策逻辑**（4e-3e-9c）。
/// 与 <see cref="AgentSessionCore"/> / <see cref="AgentSessionLoop"/> 同属 <c>AgentSession</c> 的 partial 静态半边。
/// </summary>
/// <remarks>
/// 实例层（`_compactionAbortController` / `_autoCompactionAbortController` / `_retryAbortController` /
/// `_bashAbortControllers` / `_pendingBashMessages` / `_emit`）要等 4e-3e-9g 的扩展运行时与 4e-3e-9h 的接线；
/// 本批沿用「叶子先行」惯例（同 4e-3e-9a / 9b），把判定与文案抽成显式入参的静态函数。
/// </remarks>
public partial class AgentSession
{
    /// <summary>TS <c>compact()</c> 与 <c>_runAutoCompaction()</c> 的取消文案。</summary>
    public const string CompactionCancelledMessage = "Compaction cancelled";

    // =========================================================================
    // 时间戳
    // =========================================================================

    /// <summary>
    /// 把会话条目的 ISO 时间戳解析成毫秒；无法解析时返回 <c>null</c>。
    /// 对应 TS <c>new Date(entry.timestamp).getTime()</c>——解析失败为 <c>NaN</c>，一切比较恒假。
    /// </summary>
    public static long? ParseEntryTimestampMs(string timestamp)
        => DateTimeOffset.TryParse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;

    /// <summary>
    /// 对应 TS 的两处「时间戳早于/等于最近一次压缩」判定：
    /// <c>assistantMessage.timestamp &lt;= new Date(compactionEntry.timestamp).getTime()</c>。
    /// </summary>
    /// <param name="latestCompactionTimestamp">最近压缩条目的时间戳（毫秒）；无压缩条目时为 <c>null</c>。</param>
    public static bool IsAtOrBeforeCompaction(long timestamp, long? latestCompactionTimestamp)
        => latestCompactionTimestamp is { } compactionTimestamp && timestamp <= compactionTimestamp;

    /// <summary>
    /// 对应 TS <c>_checkCompaction</c> 案例 3 的 stale-usage 短路：仅当 usage 来源是一条助手消息、
    /// 且其时间戳不晚于最近一次压缩时，才放弃整个检查（否则会拿压缩**前**的大上下文反复触发压缩）。
    /// </summary>
    public static bool IsStaleUsageSource(
        bool usageMessageIsAssistant,
        long? usageTimestamp,
        long? latestCompactionTimestamp)
        => usageMessageIsAssistant
            && usageTimestamp is { } timestamp
            && IsAtOrBeforeCompaction(timestamp, latestCompactionTimestamp);

    // =========================================================================
    // _checkCompaction 的准入与投影判定
    // =========================================================================

    /// <summary>对应 TS <c>_checkCompaction</c> 开头的两处早退：未启用压缩、或被中止且需跳过中止检查。</summary>
    public static bool ShouldSkipCompactionCheck(
        bool compactionEnabled,
        bool skipAbortedCheck,
        StopReason stopReason)
        => !compactionEnabled || (skipAbortedCheck && stopReason == StopReason.Aborted);

    /// <summary>
    /// 对应 TS <c>_checkCompaction</c> 的 <c>assistantIsProjected</c>：助手条目未落盘（<c>undefined</c>）时
    /// 视为「在投影里」；否则要求投影中确实存在该源条目且它贡献了助手消息。
    /// </summary>
    public static bool IsAssistantProjected(string? assistantEntryId, SessionProjection projection)
    {
        if (assistantEntryId is null)
        {
            return true;
        }

        return projection.Entries.Any(entry =>
            entry.SourceEntry.Id == assistantEntryId
            && entry.Messages.Any(message => message is AssistantMessage));
    }

    /// <summary>对应 TS <c>entriesAfterAssistant.some((entry) =&gt; entry.type === "context_edit")</c>。</summary>
    public static bool HasContextEditEntry(IEnumerable<SessionEntry> entries)
        => entries.Any(entry => entry is ContextEditEntry);

    /// <summary>
    /// 对应 TS <c>entriesAfterAssistant.filter(...).at(-1)</c>：最后一条以
    /// <paramref name="targetId"/> 为目标的 <c>context_edit</c>。
    /// </summary>
    public static ContextEditEntry? FindLatestContextEditFor(IEnumerable<SessionEntry> entries, string? targetId)
    {
        ContextEditEntry? latest = null;
        foreach (var entry in entries)
        {
            if (entry is ContextEditEntry edit && edit.TargetId == targetId)
            {
                latest = edit;
            }
        }

        return latest;
    }

    /// <summary>
    /// 对应 TS <c>_checkCompaction</c> 的 <c>assistantRetainedForExplicitRecovery</c>。
    /// </summary>
    /// <param name="latestEditRemovedAssistant">
    /// 最近一条针对该助手的 <c>context_edit</c> 是否把内容剔除（<c>replacement === null</c>）。
    /// 无此类编辑时为 <c>false</c>——TS 的 <c>latestAssistantEdit?.replacement !== null</c> 在
    /// 「没有编辑」时因 <c>undefined !== null</c> 而为 <c>true</c>。
    /// </param>
    public static bool IsAssistantRetainedForExplicitRecovery(
        string? assistantEntryId,
        bool hasCompactionAfterAssistant,
        bool latestEditRemovedAssistant)
        => assistantEntryId is null || (!hasCompactionAfterAssistant && !latestEditRemovedAssistant);

    /// <summary>
    /// 对应 TS <c>const explicitOverflow = stopReason === "error" &amp;&amp; isContextOverflow(message)</c>。
    /// </summary>
    /// <param name="isContextOverflowWithoutWindow">
    /// 不带 contextWindow 的 <c>isContextOverflow(message)</c> 结果（错误码/文案形态判定）。
    /// </param>
    public static bool IsExplicitOverflow(StopReason stopReason, bool isContextOverflowWithoutWindow)
        => stopReason == StopReason.Error && isContextOverflowWithoutWindow;

    /// <summary>对应 TS <c>_checkCompaction</c> 的 <c>contextOverflow</c> 合成。</summary>
    public static bool IsContextOverflowDetected(
        bool sameModel,
        bool explicitOverflow,
        bool retainedForExplicitRecovery,
        bool usageMatchesProjection,
        bool isContextOverflowWithWindow)
        => sameModel
            && ((explicitOverflow && retainedForExplicitRecovery)
                || (usageMatchesProjection && isContextOverflowWithWindow));

    /// <summary>对应 TS <c>_checkCompaction</c> 的 <c>recoverableLength</c> 合成。</summary>
    public static bool IsRecoverableLengthDetected(
        bool sameModel,
        bool assistantIsProjected,
        bool isRecoverableLength)
        => sameModel && assistantIsProjected && isRecoverableLength;

    /// <summary>对应 TS <c>const willRetry = assistantMessage.stopReason !== "stop"</c>。</summary>
    public static bool ShouldRetryAfterOverflow(StopReason stopReason) => stopReason != StopReason.Stop;

    /// <summary>对应 TS <c>_checkCompaction</c> 里 <c>_overflowRecoveryAttempted</c> 已置位时的失败文案。</summary>
    public static string FormatOverflowRecoveryFailureMessage(bool contextOverflow)
        => contextOverflow
            ? "Context overflow recovery failed after one compact-and-retry attempt. Try reducing context or switching to a larger-context model."
            : "Truncated response recovery failed after one compact-and-retry attempt.";

    /// <summary>对应 TS <c>_checkCompaction</c> 案例 3 的 <c>contextTokens</c> 取值来源选择。</summary>
    public static CompactionTokenSource ResolveCompactionTokenSource(
        bool hasContextEdits,
        StopReason stopReason,
        long directContextTokens)
        => hasContextEdits
            ? CompactionTokenSource.ProjectedEstimate
            : stopReason == StopReason.Error || directContextTokens == 0
                ? CompactionTokenSource.MessageEstimate
                : CompactionTokenSource.DirectUsage;

    // =========================================================================
    // _runAutoCompaction / compact 的返回与文案
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>_runAutoCompaction</c> 的收尾：<c>willRetry</c> 为真时继续本轮；否则若队列里有
    /// follow-up / steering / 自定义消息也继续一次，好让它们被投递。
    /// </summary>
    public static bool ShouldContinueAfterAutoCompaction(bool willRetry, bool hasQueuedMessages)
        => willRetry || hasQueuedMessages;

    /// <summary>
    /// 对应 TS <c>_runAutoCompaction</c> 的失败文案：
    /// <c>aborted ? undefined : reason === "overflow" ? `Context overflow recovery failed: ${m}` : `Auto-compaction failed: ${m}`</c>。
    /// </summary>
    public static string? FormatAutoCompactionFailureMessage(bool aborted, string reason, string message)
        => aborted
            ? null
            : reason == CompactionReason.Overflow
                ? $"Context overflow recovery failed: {message}"
                : $"Auto-compaction failed: {message}";

    /// <summary>
    /// 对应 TS <c>_runAutoCompaction</c> 的 <c>finally</c>：
    /// <c>if (this._autoCompactionAbortController === abortController)</c>。
    /// </summary>
    /// <remarks>差异 C97：TS 用 <c>===</c>（引用同一性），故端口用 <see cref="object.ReferenceEquals"/>（同 C89）。</remarks>
    public static bool ShouldClearAutoCompactionController(object? current, object? controller)
        => ReferenceEquals(current, controller);

    /// <summary>
    /// 对应 TS <c>compact()</c> 在 <c>prepareCompaction()</c> 返回 <c>null</c> 时的两种错误文案。
    /// </summary>
    /// <param name="lastEntryIsCompaction">分支末条是否为 <c>compaction</c> 条目。</param>
    public static string FormatManualCompactionPreparationFailure(bool lastEntryIsCompaction)
        => lastEntryIsCompaction
            ? "Already compacted"
            : "Nothing to compact (session too small)";

    /// <summary>
    /// 对应 TS <c>compact()</c> 的 <c>aborted = signal.aborted || cancelledByExtension</c>。
    /// </summary>
    public static bool IsManualCompactionAborted(bool signalAborted, bool cancelledByExtension)
        => signalAborted || cancelledByExtension;

    /// <summary>
    /// 对应 TS <c>compact()</c> 的失败文案：<c>aborted ? undefined : `Compaction failed: ${message}`</c>。
    /// </summary>
    public static string? FormatManualCompactionFailureMessage(bool aborted, string message)
        => aborted ? null : $"Compaction failed: {message}";

    // =========================================================================
    // 模型溯源与选择记录
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>_modelForMessage</c> 的非虚拟分支：
    /// <c>model?.provider === message.provider &amp;&amp; model.id === message.model ? model : undefined</c>。
    /// </summary>
    /// <remarks>虚拟模型分支需要 <c>modelRuntime.getPhysicalModel</c>，由调用方在实例层处理。</remarks>
    public static bool IsModelForMessage(
        string? modelProvider,
        string? modelId,
        string messageProvider,
        string? messageModel)
        => modelProvider is not null
            && string.Equals(modelProvider, messageProvider, StringComparison.Ordinal)
            && string.Equals(modelId, messageModel, StringComparison.Ordinal);

    /// <summary>
    /// 对应 TS <c>_recordSelection</c> 的判定链：分支上已有记录且与当前选择不同，且
    /// 「当前是虚拟模型」或「记录解析出的模型是虚拟模型」时才把选择写回分支。
    /// </summary>
    /// <param name="recorded">分支上记录的模型选择；无则 <c>null</c>。</param>
    /// <param name="recordedModelIsVirtual">
    /// 记录的选择解析出的模型是否为虚拟模型；解析不到模型时为 <c>null</c>
    /// （对应 TS 的 <c>recordedModel &amp;&amp; isVirtualModel(recordedModel)</c> 为假）。
    /// </param>
    public static bool ShouldRecordSelection(
        BranchModelSelection? recorded,
        string modelProvider,
        string modelId,
        bool modelIsVirtual,
        bool? recordedModelIsVirtual)
    {
        if (recorded is not { } selection)
        {
            return false;
        }

        if (string.Equals(selection.Provider, modelProvider, StringComparison.Ordinal)
            && string.Equals(selection.ModelId, modelId, StringComparison.Ordinal))
        {
            return false;
        }

        return modelIsVirtual || recordedModelIsVirtual == true;
    }

    // =========================================================================
    // Retry
    // =========================================================================

    /// <summary>对应 TS <c>_finishCancelledRetry</c> 的开头：<c>if (this._retryAttempt === 0) return;</c>。</summary>
    public static bool ShouldEmitCancelledRetry(int retryAttempt) => retryAttempt != 0;

    /// <summary>
    /// 对应 TS <c>_prepareRetry</c> 的计数推进：
    /// <c>if (!settings.enabled) return false; this._retryAttempt++; if (this._retryAttempt &gt; settings.maxRetries) { this._retryAttempt--; return false; }</c>。
    /// </summary>
    /// <returns>
    /// <c>ShouldRetry</c> 为是否继续重试；<c>NextAttempt</c> 为更新后的 <c>_retryAttempt</c>
    /// （超出预算时回退到原值，以便 post-run 阶段仍能报出最终失败次数）。
    /// </returns>
    public static (bool ShouldRetry, int NextAttempt) AdvanceRetryAttempt(
        bool retryEnabled,
        int retryAttempt,
        int maxRetries)
    {
        if (!retryEnabled)
        {
            return (false, retryAttempt);
        }

        var next = retryAttempt + 1;
        return next > maxRetries ? (false, next - 1) : (true, next);
    }

    /// <summary>
    /// 对应 TS <c>errorMessage: message.errorMessage || "Unknown error"</c>——
    /// JS 的 <c>||</c> 把空串也视为假，故此处用 <see cref="string.IsNullOrEmpty"/>。
    /// </summary>
    public static string ResolveRetryErrorMessage(string? errorMessage)
        => string.IsNullOrEmpty(errorMessage) ? "Unknown error" : errorMessage;

    /// <summary>
    /// 对应 TS <c>_isRetryableError</c>：上下文溢出交给压缩处理，**不**重试；其余走
    /// <c>isRetryableAssistantError</c>。
    /// </summary>
    /// <remarks>差异 C98：TS 的 <c>(this._modelForMessage(message) ?? this.model)?.contextWindow ?? 0</c>
    /// 由调用方算好后以 <paramref name="contextWindow"/> 传入（同 C87 / C96）。</remarks>
    public static bool IsRetryableError(AssistantMessage message, long contextWindow)
        => !Overflow.IsContextOverflow(message, contextWindow)
            && Retry.IsRetryableAssistantError(message);

    /// <summary>
    /// 对应 TS <c>_summarizationRetryCallbacks</c>：把三次回调分别接到
    /// <c>summarization_retry_scheduled</c> / <c>summarization_retry_attempt_start</c> /
    /// <c>summarization_retry_finished</c> 三类事件上。
    /// </summary>
    public static RetryCallbacks CreateSummarizationRetryCallbacks(
        SummarizationRetrySource source,
        Action<int, int, long, string> onRetryScheduled,
        Action<SummarizationRetrySource> onRetryAttemptStart,
        Action onRetryFinished)
        => new()
        {
            OnRetryScheduled = (attempt, maxAttempts, delayMs, errorMessage) =>
            {
                onRetryScheduled(attempt, maxAttempts, delayMs, errorMessage);
                return Task.CompletedTask;
            },
            OnRetryAttemptStart = () =>
            {
                onRetryAttemptStart(source);
                return Task.CompletedTask;
            },
            OnRetryFinished = (_, _, _) =>
            {
                onRetryFinished();
                return Task.CompletedTask;
            },
        };

    // =========================================================================
    // Bash
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>const resolvedCommand = prefix ? `${prefix}\n${command}` : command</c>——
    /// JS 的空串为假，故空前缀不拼接。
    /// </summary>
    public static string ApplyShellCommandPrefix(string? prefix, string command)
        => string.IsNullOrEmpty(prefix) ? command : $"{prefix}\n{command}";

    /// <summary>
    /// 对应 TS <c>recordBashResult</c> 构造的 <c>BashExecutionMessage</c>。
    /// </summary>
    /// <param name="excludeFromContext">
    /// 对应 TS 的 <c>options?.excludeFromContext</c>：未给 options 时为 <c>null</c>（序列化时省略该字段）。
    /// </param>
    public static BashExecutionMessage CreateBashExecutionMessage(
        string command,
        BashResult result,
        bool? excludeFromContext,
        long timestamp)
        => new(command, result.Output, result.ExitCode, result.Cancelled, result.Truncated, timestamp)
        {
            FullOutputPath = result.FullOutputPath,
            ExcludeFromContext = excludeFromContext,
        };

    /// <summary>
    /// 对应 TS <c>recordBashResult</c> 的分流：流式期间延后入队（避免插在 tool_use / tool_result 之间），
    /// 否则直接落库。
    /// </summary>
    public static bool ShouldDeferBashMessage(bool isStreaming) => isStreaming;

    /// <summary>
    /// 对应 TS <c>_flushPendingBashMessages</c>：待发列表非空时整体取出并清空，供调用方逐条落库后刷新上下文。
    /// </summary>
    public static IReadOnlyList<BashExecutionMessage> TakePendingBashMessages(
        List<BashExecutionMessage> pending)
    {
        if (pending.Count == 0)
        {
            return [];
        }

        var taken = pending.ToList();
        pending.Clear();
        return taken;
    }

    // =========================================================================
    // 压缩编排（4e-3e-9h-4）— TS `_checkCompaction`（2923-3050）/ `_runAutoCompaction`（3050-3230）/
    // `compact`（2750-2895）/ `_runDefaultCompaction`（2697-2720）/
    // `_getSummarizationRequestAuth`（560-597）/ `_getRequiredRequestAuth`（516-556）/
    // `_clearManualCompactionState`（2726-2729）。
    // =========================================================================

    /// <summary>TS <c>_checkCompaction(message, skipAbortedCheck)</c>（TS 3008-3135）。</summary>
    private async Task<bool> CheckCompactionAsync(
        AssistantMessage assistantMessage,
        bool skipAbortedCheck = true)
    {
        var settings = CompactionSettingsForSessionModel();
        if (ShouldSkipCompactionCheck(settings.Enabled, skipAbortedCheck, assistantMessage.StopReason))
        {
            return false;
        }

        // 消息来自另一个模型时跳过溢出检查：用户从更小上下文的模型（opus）切到更大上下文（codex）时，
        // 旧模型的溢出错误不应触发新模型的压缩。虚拟选择下，产出该消息的物理模型提供限额。
        var messageModel = ModelForMessage(assistantMessage);
        var sameModel = messageModel is not null;
        var contextWindow = (messageModel ?? Model)?.ContextWindow ?? 0;

        // 助手消息早于最近一次压缩边界时跳过：避免压缩**前**的陈旧用量/错误在压缩后第一个提示词上重新触发压缩。
        var branch = SessionManager.GetBranch();
        var compactionEntry = SessionEntries.GetLatestCompactionEntry(branch);
        var latestCompactionTs = compactionEntry is null
            ? (long?)null
            : ParseEntryTimestampMs(compactionEntry.Timestamp);
        // TS 的 assistantMessage.timestamp 是必填 number；端口可空（C86 同类），缺失时按「不在压缩之前」处理。
        var assistantIsFromBeforeCompaction = assistantMessage.Timestamp is { } assistantTs
            && IsAtOrBeforeCompaction(assistantTs, latestCompactionTs);
        if (assistantIsFromBeforeCompaction)
        {
            return false;
        }

        // 自动案例 1 / 2：上下文溢出。
        var currentProjection = SessionManager.BuildSessionProjection();
        var assistantEntryId = FindPersistedMessageEntryIdFor(assistantMessage);
        var assistantIsProjected = IsAssistantProjected(assistantEntryId, currentProjection);
        var assistantIndex = -1;
        if (assistantEntryId is not null)
        {
            for (var index = 0; index < branch.Count; index++)
            {
                if (branch[index].Id == assistantEntryId)
                {
                    assistantIndex = index;
                    break;
                }
            }
        }

        var entriesAfterAssistant = assistantIndex >= 0
            ? branch.Skip(assistantIndex + 1).ToList()
            : new List<SessionEntry>();
        var hasPostAssistantContextEdit = HasContextEditEntry(entriesAfterAssistant);
        var latestAssistantEdit = FindLatestContextEditFor(entriesAfterAssistant, assistantEntryId);
        var assistantRetainedForExplicitRecovery = IsAssistantRetainedForExplicitRecovery(
            assistantEntryId,
            entriesAfterAssistant.Any(entry => entry is CompactionEntry),
            latestAssistantEdit is not null && latestAssistantEdit.Replacement is null);
        var assistantUsageMatchesProjection = assistantIsProjected && !hasPostAssistantContextEdit;
        var explicitOverflow = IsExplicitOverflow(
            assistantMessage.StopReason, Overflow.IsContextOverflow(assistantMessage));
        var contextOverflow = IsContextOverflowDetected(
            sameModel,
            explicitOverflow,
            assistantRetainedForExplicitRecovery,
            assistantUsageMatchesProjection,
            Overflow.IsContextOverflow(assistantMessage, contextWindow));
        var recoverableLength = IsRecoverableLengthDetected(
            sameModel,
            assistantIsProjected,
            messageModel is not null && Overflow.IsRecoverableLength(assistantMessage, messageModel.MaxTokens));

        if (contextOverflow || recoverableLength)
        {
            var willRetry = ShouldRetryAfterOverflow(assistantMessage.StopReason);

            // 案例 2：响应成功完成。压缩，但不重试——agent.continue() 无法从已完成的助手响应继续。
            if (!willRetry)
            {
                return await RunAutoCompactionAsync(CompactionReason.Overflow, false).ConfigureAwait(false);
            }

            if (_overflowRecoveryAttempted)
            {
                var errorMessage = FormatOverflowRecoveryFailureMessage(contextOverflow);
                Emit(new AgentSessionEvent.CompactionEndEvent(
                    CompactionReason.Overflow,
                    Result: null,
                    Aborted: false,
                    WillRetry: false,
                    ErrorMessage: errorMessage));
                await EmitSessionCompactFailedAsync(
                    CompactionReason.Overflow, errorMessage, aborted: false, willRetry: false, fromExtension: false)
                    .ConfigureAwait(false);
                return false;
            }

            // 在 post-run 恢复性压缩之前，把选定的最后一次尝试持久地省略掉。
            _overflowRecoveryAttempted = true;
            OmitRecoveryAttempt(assistantMessage);
            var retry = await RunAutoCompactionAsync(CompactionReason.Overflow, willRetry).ConfigureAwait(false);
            if (retry)
            {
                _failedResponse = assistantMessage;
            }

            return retry;
        }

        // 案例 3：不重试的阈值压缩。
        var projection = currentProjection;
        var hasContextEdits = projection.Entries.Any(entry => entry.SourceEntry is ContextEditEntry);
        var directContextTokens = assistantMessage.UsageStats is { } usage
            ? Compaction.CalculateContextTokens(usage)
            : 0;
        long contextTokens;
        switch (ResolveCompactionTokenSource(hasContextEdits, assistantMessage.StopReason, directContextTokens))
        {
            case CompactionTokenSource.ProjectedEstimate:
                contextTokens = Compaction.EstimateProjectedContextTokens(projection, branch).Tokens;
                break;
            case CompactionTokenSource.MessageEstimate:
                var messages = _agent.Messages;
                var estimate = Compaction.EstimateContextTokens(messages);
                if (estimate.LastUsageIndex is { } lastUsageIndex)
                {
                    // 校验用量来源在压缩**之后**：被保留的压缩前消息带的是旧（更大）上下文的陈旧用量，
                    // 会在刚压缩完就误触发下一次压缩。
                    var usageMessage = messages[lastUsageIndex];
                    if (IsStaleUsageSource(
                            usageMessage is AssistantMessage,
                            (usageMessage as AssistantMessage)?.Timestamp,
                            latestCompactionTs))
                    {
                        return false;
                    }
                }

                contextTokens = estimate.Tokens;
                break;
            default:
                contextTokens = directContextTokens;
                break;
        }

        if (Compaction.ShouldCompact(contextTokens, contextWindow, settings))
        {
            return await RunAutoCompactionAsync(CompactionReason.Threshold, false).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>TS <c>_runAutoCompaction(reason, willRetry)</c>（TS 3050-3230）。</summary>
    private async Task<bool> RunAutoCompactionAsync(string reason, bool willRetry)
    {
        var model = Model;
        var settings = CompactionSettingsForSessionModel();
        CancellationTokenSource? abortController = null;
        var started = false;
        var fromExtension = false;
        var cancelledByExtension = false;

        try
        {
            if (model is null)
            {
                return false;
            }

            var pathEntries = SessionManager.GetBranch();
            var preparation = Compaction.PrepareCompaction(pathEntries, settings);
            if (preparation is null)
            {
                return false;
            }

            abortController = new CancellationTokenSource();
            _autoCompactionAbortController = abortController;
            started = true;
            Emit(new AgentSessionEvent.CompactionStartEvent(reason));
            abortController.Token.ThrowIfCancellationRequested();

            CompactionResult? extensionCompaction = null;
            if (_extensionRunner.HasHandlers(ExtensionEventNames.SessionBeforeCompact))
            {
                var extensionResult = await _extensionRunner.EmitAsync(new ExtensionEvent.SessionBeforeCompact(
                        preparation,
                        pathEntries,
                        CustomInstructions: null,
                        reason,
                        willRetry,
                        abortController.Token))
                    .ConfigureAwait(false);

                if (extensionResult?.Cancel == true)
                {
                    cancelledByExtension = true;
                    throw new InvalidOperationException(CompactionCancelledMessage);
                }

                if (extensionResult?.Compaction is not null)
                {
                    extensionCompaction = extensionResult.Compaction;
                    fromExtension = true;
                }
            }

            abortController.Token.ThrowIfCancellationRequested();

            var applied = extensionCompaction ?? await RunDefaultCompactionAsync(
                    preparation, model, customInstructions: null, abortController.Token, reason)
                .ConfigureAwait(false);
            abortController.Token.ThrowIfCancellationRequested();

            SessionManager.AppendCompaction(
                applied.Summary, applied.FirstKeptEntryId, applied.TokensBefore, applied.Details, fromExtension, applied.Usage);
            var newEntries = SessionManager.GetEntries();
            RefreshFinalizedContext();
            var estimatedTokensAfter = EstimateMessagesTokens(SessionManager.BuildSessionProjection().Messages);

            // 取落库后的压缩条目，供扩展事件携带。
            var savedCompactionEntry = newEntries
                .OfType<CompactionEntry>()
                .FirstOrDefault(entry => entry.Summary == applied.Summary);

            if (savedCompactionEntry is not null)
            {
                await _extensionRunner.EmitAsync(new ExtensionEvent.SessionCompact(
                        savedCompactionEntry, fromExtension, reason, willRetry))
                    .ConfigureAwait(false);
            }

            var result = new CompactionResult(
                applied.Summary,
                applied.FirstKeptEntryId,
                applied.TokensBefore,
                estimatedTokensAfter,
                applied.Usage,
                applied.Details);
            Emit(new AgentSessionEvent.CompactionEndEvent(reason, result, Aborted: false, WillRetry: willRetry));

            if (willRetry)
            {
                return true;
            }

            // 自动压缩可能在 follow-up / steering / 自定义消息排队等待时完成；继续一次让它们被投递。
            return _agent.HasQueuedMessages();
        }
        catch (Exception error)
        {
            var message = error.Message;
            var aborted = abortController?.IsCancellationRequested == true || cancelledByExtension;
            if (started)
            {
                var errorMessage = FormatAutoCompactionFailureMessage(aborted, reason, message);
                Emit(new AgentSessionEvent.CompactionEndEvent(
                    reason, Result: null, aborted, WillRetry: false, ErrorMessage: errorMessage));
                await EmitSessionCompactFailedAsync(reason, errorMessage, aborted, willRetry: false, fromExtension)
                    .ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            if (ShouldClearAutoCompactionController(_autoCompactionAbortController, abortController))
            {
                _autoCompactionAbortController = null;
            }

            ResolveIdleWaitIfIdle();
        }
    }

    /// <summary>TS <c>compact(customInstructions?)</c>（TS 2750-2895）。</summary>
    /// <remarks>
    /// 公开成员：TS 的 <c>compact()</c> 是 <c>AgentSession</c> 的公共入口（/compact、RPC 与扩展
    /// 三条路径共用），RPC 批（4f-5）与 SDK 消费方都要经它进入。
    /// </remarks>
    public async Task<CompactionResult> CompactAsync(CompactOptions? options)
    {
        await AbortAsync().ConfigureAwait(false);
        _compactionAbortController = new CancellationTokenSource();
        var signal = _compactionAbortController.Token;
        Emit(new AgentSessionEvent.CompactionStartEvent(CompactionReason.Manual));
        var fromExtension = false;
        var cancelledByExtension = false;

        try
        {
            var model = Model;
            if (model is null)
            {
                throw new InvalidOperationException(AuthGuidance.FormatNoModelSelectedMessage());
            }

            var settings = CompactionSettingsForSessionModel();
            var pathEntries = SessionManager.GetBranch();

            var preparation = Compaction.PrepareCompaction(pathEntries, settings);
            if (preparation is null)
            {
                var lastEntry = pathEntries.Count > 0 ? pathEntries[^1] : null;
                throw new InvalidOperationException(
                    FormatManualCompactionPreparationFailure(lastEntry is CompactionEntry));
            }

            CompactionResult? extensionCompaction = null;
            if (_extensionRunner.HasHandlers(ExtensionEventNames.SessionBeforeCompact))
            {
                var result = await _extensionRunner.EmitAsync(new ExtensionEvent.SessionBeforeCompact(
                        preparation,
                        pathEntries,
                        options?.CustomInstructions,
                        CompactionReason.Manual,
                        WillRetry: false,
                        signal))
                    .ConfigureAwait(false);

                if (result?.Cancel == true)
                {
                    cancelledByExtension = true;
                    throw new InvalidOperationException(CompactionCancelledMessage);
                }

                if (result?.Compaction is not null)
                {
                    extensionCompaction = result.Compaction;
                    fromExtension = true;
                }
            }

            var applied = extensionCompaction ?? await RunDefaultCompactionAsync(
                    preparation, model, options?.CustomInstructions, signal, CompactionReason.Manual)
                .ConfigureAwait(false);

            if (signal.IsCancellationRequested)
            {
                throw new InvalidOperationException(CompactionCancelledMessage);
            }

            SessionManager.AppendCompaction(
                applied.Summary, applied.FirstKeptEntryId, applied.TokensBefore, applied.Details, fromExtension, applied.Usage);
            var newEntries = SessionManager.GetEntries();
            RefreshFinalizedContext();
            var estimatedTokensAfter = EstimateMessagesTokens(SessionManager.BuildSessionProjection().Messages);

            var savedCompactionEntry = newEntries
                .OfType<CompactionEntry>()
                .FirstOrDefault(entry => entry.Summary == applied.Summary);

            if (savedCompactionEntry is not null)
            {
                await _extensionRunner.EmitAsync(new ExtensionEvent.SessionCompact(
                        savedCompactionEntry, fromExtension, CompactionReason.Manual, WillRetry: false))
                    .ConfigureAwait(false);
            }

            var compactionResult = new CompactionResult(
                applied.Summary,
                applied.FirstKeptEntryId,
                applied.TokensBefore,
                estimatedTokensAfter,
                applied.Usage,
                applied.Details);

            // compaction_end 监听器可能提交排队的提示词，故先暴露空闲态再通知它们。
            ClearManualCompactionState();
            Emit(new AgentSessionEvent.CompactionEndEvent(
                CompactionReason.Manual, compactionResult, Aborted: false, WillRetry: false));
            return compactionResult;
        }
        catch (Exception error)
        {
            var message = error.Message;
            var aborted = IsManualCompactionAborted(
                _compactionAbortController?.IsCancellationRequested == true, cancelledByExtension);
            var errorMessage = FormatManualCompactionFailureMessage(aborted, message);
            ClearManualCompactionState();
            Emit(new AgentSessionEvent.CompactionEndEvent(
                CompactionReason.Manual, Result: null, aborted, WillRetry: false, ErrorMessage: errorMessage));
            await EmitSessionCompactFailedAsync(
                CompactionReason.Manual, errorMessage, aborted, willRetry: false, fromExtension)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            ClearManualCompactionState();
        }
    }

    /// <summary>TS <c>_clearManualCompactionState()</c>（TS 2726-2729）。</summary>
    private void ClearManualCompactionState()
    {
        _compactionAbortController = null;
        ResolveIdleWaitIfIdle();
    }

    /// <summary>TS <c>_runDefaultCompaction(preparation, model, customInstructions, signal, reason)</c>。</summary>
    private async Task<CompactionResult> RunDefaultCompactionAsync(
        CompactionPreparation preparation,
        ModelSpec model,
        string? customInstructions,
        CancellationToken signal,
        string reason)
    {
        // 仅在 Pi 自己摘要时才解析请求：路由可能调模型或失败。
        var request = await GetSummarizationRequestAuthAsync(model, signal).ConfigureAwait(false);
        var retrySettings = SettingsManager.GetRetrySettings();
        var retryPolicy = new RetryPolicy
        {
            Enabled = retrySettings.Enabled,
            MaxRetries = retrySettings.MaxRetries,
            BaseDelayMs = retrySettings.BaseDelayMs,
            MaxAgentDelayMs = retrySettings.MaxAgentDelayMs,
        };
        var callbacks = CreateSummarizationRetryCallbacks(
            new SummarizationRetrySource { Source = SummarizationRetrySourceKind.Compaction, Reason = reason },
            onRetryScheduled: (attempt, maxAttempts, delayMs, errorMessage) => Emit(
                new AgentSessionEvent.SummarizationRetryScheduledEvent(
                    attempt, maxAttempts, (int)delayMs, errorMessage)),
            onRetryAttemptStart: source => Emit(
                new AgentSessionEvent.SummarizationRetryAttemptStartEvent(source.Source, source.Reason)),
            onRetryFinished: () => Emit(new AgentSessionEvent.SummarizationRetryFinishedEvent()));

        return await Compaction.CompactAsync(
                preparation,
                request.Model,
                request.ApiKey,
                request.Headers,
                customInstructions,
                signal,
                Core.ThinkingLevels.ToText(request.ThinkingLevel),
                _agent.StreamFunction,
                request.Env,
                retryPolicy,
                callbacks,
                sessionId: null)
            .ConfigureAwait(false);
    }

    /// <summary>TS <c>_getSummarizationRequestAuth(selectedModel, signal)</c>（TS 560-597）。</summary>
    private async Task<SummarizationRequestAuth> GetSummarizationRequestAuthAsync(
        ModelSpec selectedModel, CancellationToken signal)
    {
        // 虚拟模型先路由：摘要的输入与输出尺寸取决于拿到的模型。
        ModelSpec model;
        ThinkingLevel thinkingLevel;
        if (VirtualModels.IsVirtualModel(selectedModel))
        {
            var route = await _modelRuntime.ResolveModelAsync(
                    selectedModel,
                    AgentMessages.ConvertToLlm(_agent.Messages),
                    new ResolveModelOptions
                    {
                        Reason = VirtualModels.RouteReasons.Direct,
                        ThinkingLevel = Core.ThinkingLevels.ToText(ThinkingLevel),
                        Signal = signal,
                    })
                .ConfigureAwait(false);
            model = route.Model;
            thinkingLevel = Core.ThinkingLevels.Parse(route.ThinkingLevel) ?? ThinkingLevel;
        }
        else
        {
            model = selectedModel;
            thinkingLevel = ThinkingLevel;
        }

        if (UsesDefaultStreamFunction())
        {
            var required = await GetRequiredRequestAuthAsync(model, signal).ConfigureAwait(false);
            return new SummarizationRequestAuth(
                required.Model, required.ApiKey, required.Headers, required.Env, thinkingLevel);
        }

        try
        {
            var result = await _modelRuntime.GetAuthAsync(model, new ModelRuntimeAuthOverrides { Signal = signal })
                .ConfigureAwait(false);
            if (result is null)
            {
                return new SummarizationRequestAuth(model, null, null, null, thinkingLevel);
            }

            var requestModel = result.Auth.BaseUrl is { } baseUrl ? model with { BaseUrl = baseUrl } : model;
            return new SummarizationRequestAuth(
                requestModel,
                result.Auth.ApiKey,
                ToNullableHeaders(WithoutDeletedHeaders(result.Auth.Headers)),
                result.Env,
                thinkingLevel);
        }
        catch (Exception)
        {
            if (signal.IsCancellationRequested)
            {
                throw;
            }

            return new SummarizationRequestAuth(model, null, null, null, thinkingLevel);
        }
    }

    /// <summary>
    /// 对应 TS <c>this.agent.streamFunction === streamSimple</c>：agent 用的是**默认**流式函数
    /// （即 SDK 没有注入自定义 streamFn）。
    /// </summary>
    /// <remarks>
    /// 差异 C160：TS 的 <c>streamSimple</c> 是 <c>@pi/ai</c> 的模块常量，恒可比较；端口的默认流式函数
    /// 由 <c>DefaultStreamFn.Set()</c> 注入（<c>AgentLoop</c> 在 streamFn 为 null 时取它），
    /// 故未配置默认值时该恒等比较无意义——按「不是默认」处理。
    /// </remarks>
    private bool UsesDefaultStreamFunction()
        => Pi.Agent.DefaultStreamFn.IsConfigured && _agent.StreamFunction == Pi.Agent.DefaultStreamFn.Get();

    /// <summary>
    /// 把 <see cref="WithoutDeletedHeaders"/> 的结果（值非空）转成可空值字典——即
    /// <c>Compaction.CompactAsync</c> 的 <c>headers</c> 形参形状。C# 的泛型不变性不允许
    /// <c>IReadOnlyDictionary&lt;string, string&gt;</c> 直接当 <c>IReadOnlyDictionary&lt;string, string?&gt;</c> 用。
    /// </summary>
    private static IReadOnlyDictionary<string, string?>? ToNullableHeaders(
        IReadOnlyDictionary<string, string>? headers)
        => headers?.ToDictionary(entry => entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);

    /// <summary>TS <c>_getRequiredRequestAuth(model, signal)</c>（TS 516-556）。</summary>
    private async Task<RequiredRequestAuth> GetRequiredRequestAuthAsync(ModelSpec model, CancellationToken signal)
    {
        AuthResult? result;
        try
        {
            result = await _modelRuntime.GetAuthAsync(model, new ModelRuntimeAuthOverrides { Signal = signal })
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var cause = error.InnerException;
            if (cause is not null && cause.Message == "authHeader requires a resolved API key")
            {
                throw new InvalidOperationException(AuthGuidance.FormatNoApiKeyFoundMessage(model.Provider));
            }

            throw;
        }

        if (result is not null && (result.Auth.ApiKey is not null || result.Auth.Headers is not null))
        {
            var requestModel = result.Auth.BaseUrl is { } baseUrl ? model with { BaseUrl = baseUrl } : model;
            return new RequiredRequestAuth(
                requestModel, result.Auth.ApiKey, ToNullableHeaders(WithoutDeletedHeaders(result.Auth.Headers)), result.Env);
        }

        if (_modelRuntime.IsUsingOAuth(model.Provider))
        {
            throw new InvalidOperationException(
                $"Authentication failed for \"{model.Provider}\". "
                + "Credentials may have expired or network is unavailable. "
                + $"Run '/login {model.Provider}' to re-authenticate.");
        }

        throw new InvalidOperationException(AuthGuidance.FormatNoApiKeyFoundMessage(model.Provider));
    }
}

/// <summary>TS <c>_getRequiredRequestAuth</c> 的返回值。</summary>
public sealed record RequiredRequestAuth(
    ModelSpec Model,
    string? ApiKey,
    IReadOnlyDictionary<string, string?>? Headers,
    IReadOnlyDictionary<string, string>? Env);

/// <summary>TS <c>_getSummarizationRequestAuth</c> 的返回值（多一个 thinkingLevel）。</summary>
public sealed record SummarizationRequestAuth(
    ModelSpec Model,
    string? ApiKey,
    IReadOnlyDictionary<string, string?>? Headers,
    IReadOnlyDictionary<string, string>? Env,
    ThinkingLevel ThinkingLevel);
