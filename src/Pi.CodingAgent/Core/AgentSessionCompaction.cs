using System.Globalization;
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
}
