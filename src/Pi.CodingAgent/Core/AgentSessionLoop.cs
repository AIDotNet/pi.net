using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils.Image;

namespace Pi.CodingAgent.Core;

/// <summary>TS <c>PromptDisposition</c> 字符串字面量联合。</summary>
public static class PromptDisposition
{
    public const string Handled = "handled";

    public const string Queued = "queued";

    public const string Started = "started";
}

/// <summary>TS <c>QueuedInputDisposition</c> 字符串字面量联合。</summary>
public static class QueuedInputDisposition
{
    public const string Handled = "handled";

    public const string Queued = "queued";
}

/// <summary>
/// TS 边界事件的两个取值（<c>turn_end</c> / <c>agent_before_settle</c>）。TS 里是内联字面量，
/// 端口收成常量以便 <c>_buildBoundaryContext</c> 的 <c>canContinue</c> 分支可读。
/// </summary>
public static class SessionBoundaryKind
{
    public const string TurnEnd = "turn_end";

    public const string AgentBeforeSettle = "agent_before_settle";
}

/// <summary>TS <c>finishTurn</c> 钩子的 <c>{ action }</c> 取值。</summary>
public static class FinishTurnAction
{
    public const string End = "end";

    public const string Continue = "continue";
}

/// <summary>TS <c>resolveModel</c> 的 <c>reason</c> 取值。</summary>
public static class RouteReason
{
    public const string Retry = "retry";

    public const string User = "user";

    public const string Continuation = "continuation";
}

/// <summary><c>_runAgentPrompt</c> 的 <c>while</c> 循环单次迭代的下一步动作。</summary>
public enum PromptLoopAction
{
    /// <summary>退出循环（TS <c>break</c>）。</summary>
    Stop,

    /// <summary>调用 <c>agent.continue()</c> 并进入下一轮（TS <c>continue</c>）。</summary>
    ContinueRun,
}

/// <summary>
/// <c>_runAgentPrompt</c> 的 <c>while</c> 循环一次迭代的输入快照。
/// 对应 TS：
/// <code>
/// while (!this._agentRunAbortRequested) {
///   if (await this._handlePostAgentRun()) {
///     if (this._agentRunAbortRequested) break;
///     await this.agent.continue(); continue;
///   }
///   if (this._agentRunAbortRequested || !(await this._runBeforeSettleBoundary())) break;
///   if (this._agentRunAbortRequested) break;
///   await this.agent.continue();
/// }
/// </code>
/// </summary>
public sealed record PromptLoopIteration
{
    /// <summary>进入本轮前的 <c>_agentRunAbortRequested</c>（即 TS 的 <c>while</c> 条件）。</summary>
    public bool AbortRequestedAtStart { get; init; }

    /// <summary><c>_handlePostAgentRun()</c> 的返回值；<c>null</c> 表示未调用（起始即已中止）。</summary>
    public bool? PostAgentRunContinued { get; init; }

    /// <summary><c>_handlePostAgentRun()</c> 返回后的 <c>_agentRunAbortRequested</c>。</summary>
    public bool AbortRequestedAfterPostRun { get; init; }

    /// <summary><c>_runBeforeSettleBoundary()</c> 的返回值（未调用时无意义）。</summary>
    public bool BeforeSettleContinued { get; init; }

    /// <summary><c>_runBeforeSettleBoundary()</c> 返回后的 <c>_agentRunAbortRequested</c>。</summary>
    public bool AbortRequestedAfterBeforeSettle { get; init; }
}

/// <summary><c>_runInputHandlers</c> 的结果；<c>null</c> 表示被扩展接管（TS 返回 <c>undefined</c>）。</summary>
public sealed record InputHandlerOutcome(string Text, IReadOnlyList<ImageContent>? Images);

/// <summary><c>clearQueue()</c> 的返回值。</summary>
public sealed record ClearedQueue(
    IReadOnlyList<string> Steering,
    IReadOnlyList<string> FollowUp);

/// <summary>
/// <c>core/agent-session.ts</c> 中 prompt 主循环的**纯决策逻辑**（4e-3e-9b）。
/// 与 <see cref="AgentSessionCore"/> 同属 <c>AgentSession</c> 的 partial 静态半边。
/// </summary>
/// <remarks>
/// 实例层（构造器 / <c>_eventListeners</c> / <c>_extensionRunner</c>）要等 4e-3e-9d 的扩展绑定与
/// 4e-3e-9g 的扩展运行时落地；本批沿用仓库既有的「叶子先行」惯例（同 4e-3e-9a），把循环控制流、
/// 边界判定、输入归一化、重试/压缩门抽成显式入参的静态函数，实例层再接线。
/// </remarks>
public partial class AgentSession
{
    // =========================================================================
    // 消息 role
    // =========================================================================

    /// <summary>
    /// 还原 TS 的 <c>message.role</c>。TS 用单一字符串判别符，端口的 <see cref="ChatMessage"/>
    /// 用运行时类型判别，故此处按类型映射回字面量。
    /// </summary>
    public static string GetMessageRole(ChatMessage message) => message switch
    {
        SystemMessage => "system",
        UserMessage => "user",
        AssistantMessage => "assistant",
        ToolResultMessage => "toolResult",
        BashExecutionMessage => "bashExecution",
        BranchSummaryMessage => "branchSummary",
        CompactionSummaryMessage => "compactionSummary",
        HookMessage => "hookMessage",
        CustomMessage => "custom",
        _ => "custom",
    };

    // =========================================================================
    // prompt 循环控制流
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>if (await this._handlePostAgentRun()) { if (abortRequested) break; await continue(); }</c>：
    /// <c>_handlePostAgentRun()</c> 返回 <see langword="true"/> 且**此后未请求中止**才继续。
    /// </summary>
    public static bool PostAgentRunLeadsToContinue(bool postAgentRunContinued, bool abortRequested)
        => postAgentRunContinued && !abortRequested;

    /// <summary>
    /// 对应 TS <c>if (abortRequested || !(await _runBeforeSettleBoundary())) break; if (abortRequested) break;</c>。
    /// </summary>
    /// <param name="abortRequestedBefore">调用 <c>_runBeforeSettleBoundary()</c> 之前的 <c>_agentRunAbortRequested</c>。</param>
    /// <param name="beforeSettleContinued"><c>_runBeforeSettleBoundary()</c> 的返回值。</param>
    /// <param name="abortRequestedAfter">
    /// 调用之后的 <c>_agentRunAbortRequested</c>。第二次判定的存在意义在于边界钩子里可能调用了
    /// <c>abort()</c>（此时 <c>_isAgentRunActive</c> 仍为真，会置上该标志）。
    /// </param>
    public static bool BeforeSettleLeadsToContinue(
        bool abortRequestedBefore,
        bool beforeSettleContinued,
        bool abortRequestedAfter)
        => !abortRequestedBefore && beforeSettleContinued && !abortRequestedAfter;

    /// <summary>
    /// 把 <see cref="PromptLoopIteration"/> 的各判定合成为本轮的动作，逐字对应 TS 的分支顺序。
    /// </summary>
    /// <remarks>
    /// TS 的 <c>||</c> 短路（起始已中止时不调用 <c>_runBeforeSettleBoundary()</c>）由
    /// <see cref="PromptLoopIteration.AbortRequestedAfterPostRun"/> 承担：
    /// <see cref="PromptLoopIteration.PostAgentRunContinued"/> 为 <c>false</c> 时该标志即为
    /// before_settle 的准入条件。<see cref="PromptLoopIteration.PostAgentRunContinued"/> 为
    /// <c>null</c> 表示调用方未执行该阶段（端口为便于逐分支测试而保留），此时直接落到 before_settle 分支。
    /// </remarks>
    public static PromptLoopAction ResolvePromptLoopStep(PromptLoopIteration iteration)
    {
        if (iteration.AbortRequestedAtStart)
        {
            return PromptLoopAction.Stop;
        }

        if (iteration.PostAgentRunContinued is { } postRun)
        {
            if (PostAgentRunLeadsToContinue(postRun, iteration.AbortRequestedAfterPostRun))
            {
                return PromptLoopAction.ContinueRun;
            }

            if (postRun)
            {
                // `_handlePostAgentRun()` 为真但此后请求了中止：直接 break，不进 before_settle。
                return PromptLoopAction.Stop;
            }
        }

        return BeforeSettleLeadsToContinue(
            iteration.AbortRequestedAfterPostRun,
            iteration.BeforeSettleContinued,
            iteration.AbortRequestedAfterBeforeSettle)
            ? PromptLoopAction.ContinueRun
            : PromptLoopAction.Stop;
    }

    // =========================================================================
    // turn_end / agent_before_settle 边界
    // =========================================================================

    /// <summary>对应 TS <c>_dispatchTurnEndBoundary</c> 对 <c>_lastActivityOutcome</c> 的赋值。</summary>
    public static string OutcomeFromStopReason(StopReason stopReason) => stopReason switch
    {
        StopReason.Aborted => AgentActivityOutcome.Aborted,
        StopReason.Error => AgentActivityOutcome.Error,
        _ => AgentActivityOutcome.Completed,
    };

    /// <summary>
    /// 对应 TS <c>_dispatchTurnEndBoundary</c> 的收尾：扩展请求继续、但当前上下文已无法继续时，
    /// 上报 <c>turn_end requested continuation without runnable model context</c> 并放弃继续。
    /// </summary>
    /// <returns><c>Continue</c> 为最终是否继续；<c>ReportInvalid</c> 为是否需要上报。</returns>
    public static (bool Continue, bool ReportInvalid) ResolveTurnEndBoundaryResult(
        bool boundaryContinue,
        bool canContinue)
        => boundaryContinue && !canContinue
            ? (false, true)
            : (boundaryContinue, false);

    /// <summary>
    /// 对应 TS <c>_buildBoundaryContext</c> 的 <c>canContinue</c> 计算。TS 里的
    /// <c>llmMessages</c> 由 <c>convertToLlm(projection.messages)</c> 产出，只含四种标准 role。
    /// </summary>
    /// <param name="llmMessages">已转换的 LLM 消息（顺序即 TS 的数组顺序）。</param>
    /// <param name="hasPendingCustomMessages">对应 TS <c>this._pendingCustomMessages.length > 0</c>。</param>
    /// <param name="hasQueuedMessages">对应 TS <c>this.agent.hasQueuedMessages()</c>。</param>
    /// <param name="boundary">见 <see cref="SessionBoundaryKind"/>。</param>
    /// <remarks>
    /// 两条边界的差别只在「<c>contextCanContinue</c> 与 <c>hasPendingCustomMessages</c> 都为假」时显现：
    /// 此时 <c>turn_end</c> 认队列（队列消息本身就构成新上下文），<c>agent_before_settle</c> 还额外要求
    /// 尾条是 <c>assistant</c>。若尾条不是 <c>assistant</c> 且存在非 system 上下文，
    /// <c>contextCanContinue</c> 已为真，两条边界等价。
    /// </remarks>
    public static bool ComputeBoundaryCanContinue(
        IReadOnlyList<ChatMessage> llmMessages,
        bool hasPendingCustomMessages,
        bool hasQueuedMessages,
        string boundary)
    {
        var finalRole = llmMessages.Count > 0 ? GetMessageRole(llmMessages[^1]) : null;
        var hasNonSystemContext = llmMessages.Any(message => GetMessageRole(message) != "system");
        var contextCanContinue = hasNonSystemContext && finalRole != "assistant";

        if (contextCanContinue || hasPendingCustomMessages)
        {
            return true;
        }

        return boundary == SessionBoundaryKind.TurnEnd
            ? hasQueuedMessages
            : finalRole == "assistant" && hasQueuedMessages;
    }

    /// <summary>
    /// 对应 TS <c>_installAgentBoundaryHooks</c> 里 <c>finishTurn</c> 的返回值合成。
    /// </summary>
    /// <param name="previousAction">上一个 <c>finishTurn</c> 的 <c>action</c>；<c>null</c> 表示无上一个或未给出 action。</param>
    /// <param name="extensionContinue"><c>_dispatchTurnEndBoundary</c> 的返回值。</param>
    /// <returns>见 <see cref="FinishTurnAction"/>；<c>null</c> 表示 TS 返回 <c>undefined</c>。</returns>
    public static string? ResolveFinishTurnDecision(string? previousAction, bool extensionContinue)
        => previousAction == FinishTurnAction.End
            ? FinishTurnAction.End
            : extensionContinue || previousAction == FinishTurnAction.Continue
                ? FinishTurnAction.Continue
                : null;

    /// <summary>
    /// 对应 TS <c>_getPendingBoundaryMessages</c>：<c>agent</c> 队列在前、待发自定义消息在后。
    /// </summary>
    public static IReadOnlyList<ChatMessage> GetPendingBoundaryMessages(
        IReadOnlyList<ChatMessage> queuedMessages,
        IReadOnlyList<ChatMessage> pendingCustomMessages)
        => [.. queuedMessages, .. pendingCustomMessages];

    // =========================================================================
    // 空闲等待
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>_resolveIdleWaitIfIdle</c> 的守卫：既已空闲、又确有等待者时才兑现等待。
    /// </summary>
    /// <remarks>
    /// TS 的 <c>_emitAgentSettled</c> 在「有 deferred 动作」与「无 deferred 动作」两条分支上都会
    /// 调用 <c>_resolveIdleWaitIfIdle()</c>（前者在 <c>finally</c> 里），故该判定与 deferred 数量无关。
    /// </remarks>
    public static bool CanResolveIdleWait(bool isIdle, bool hasResolveCallback)
        => isIdle && hasResolveCallback;

    // =========================================================================
    // prompt 前置与输入归一化
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>prompt</c> 各分支对 <c>preflightResult</c> 的调用。分支顺序即 TS 的求值顺序：
    /// 扩展命令 > input 处理器接管 > 流式排队 > 正常启动。
    /// </summary>
    public static string ResolvePromptDisposition(
        bool extensionCommandHandled,
        bool inputHandled,
        bool streamingQueued)
        => extensionCommandHandled || inputHandled
            ? PromptDisposition.Handled
            : streamingQueued
                ? PromptDisposition.Queued
                : PromptDisposition.Started;

    /// <summary>对应 TS <c>prompt</c> 的 <c>if (expandPromptTemplates &amp;&amp; text.startsWith("/"))</c>。</summary>
    public static bool ShouldTryExecuteExtensionCommand(bool expandPromptTemplates, string text)
        => expandPromptTemplates && text.StartsWith('/');

    /// <summary>
    /// 对应 TS <c>_runInputHandlers</c>：无 <c>input</c> 处理器、或处理器返回 <c>continue</c> 时
    /// 原样透传；<c>transform</c> 时用返回文本，图片为「返回的图片 ?? 原图片」；<c>handled</c> 时返回
    /// <c>null</c>（TS <c>undefined</c>），调用方据此中止提交。
    /// </summary>
    public static InputHandlerOutcome? ResolveInputHandlerOutcome(
        bool hasInputHandlers,
        string action,
        string transformedText,
        IReadOnlyList<ImageContent>? transformedImages,
        string originalText,
        IReadOnlyList<ImageContent>? originalImages)
    {
        if (!hasInputHandlers)
        {
            return new InputHandlerOutcome(originalText, originalImages);
        }

        return action switch
        {
            "handled" => null,
            "transform" => new InputHandlerOutcome(transformedText, transformedImages ?? originalImages),
            _ => new InputHandlerOutcome(originalText, originalImages),
        };
    }

    /// <summary>
    /// 对应 TS <c>_normalizePromptImages</c>：逐张处理，失败只记 hint 不产出图片；成功则产出
    /// <c>{ type: "image", data, mimeType }</c> 并把该次处理自身的 hints 追加到同一列表。
    /// </summary>
    /// <param name="images"><c>undefined</c>（<c>null</c>）直接返回空结果，对应 TS 的提前返回。</param>
    /// <param name="processImage">对应 <c>processImage(Buffer.from(image.data, "base64"), image.mimeType, options)</c>。</param>
    public static async Task<(IReadOnlyList<ImageContent> Images, List<string> Hints)> NormalizePromptImagesAsync(
        IReadOnlyList<ImageContent>? images,
        Func<ImageContent, Task<ProcessImageResult>> processImage)
    {
        var normalizedImages = new List<ImageContent>();
        var hints = new List<string>();
        if (images is null)
        {
            return (normalizedImages, hints);
        }

        foreach (var image in images)
        {
            var processed = await processImage(image).ConfigureAwait(false);
            if (processed is ProcessImageResult.Fail fail)
            {
                hints.Add(fail.Message);
                continue;
            }

            var ok = (ProcessImageResult.Ok)processed;
            normalizedImages.Add(new ImageContent(ok.Data, ok.MimeType));
            hints.AddRange(ok.Hints);
        }

        return (normalizedImages, hints);
    }

    /// <summary>
    /// 对应 TS <c>sendUserMessage</c> 对入参的归一化：字符串直取文本、无图片；块数组按
    /// <c>"\n"</c> 拼接文本块，图片块收集为图片列表，**一张都没有时回到 <c>undefined</c>**。
    /// </summary>
    public static (string Text, IReadOnlyList<ImageContent>? Images) NormalizeUserMessageContent(
        MessageContent content)
    {
        if (content is MessageContent.Text text)
        {
            return (text.Value, null);
        }

        var blocks = ((MessageContent.Blocks)content).Value;
        var textParts = new List<string>();
        var images = new List<ImageContent>();
        foreach (var block in blocks)
        {
            if (block is TextContent textBlock)
            {
                textParts.Add(textBlock.Text);
            }
            else if (block is ImageContent imageBlock)
            {
                images.Add(imageBlock);
            }
        }

        return (string.Join("\n", textParts), images.Count == 0 ? null : images);
    }

    /// <summary>
    /// 对应 TS <c>prompt</c> 的 <c>handlerEditedTools</c>：长度不同，或同长度下存在位置不等的元素。
    /// 注意 TS 用的是 <c>some</c> 而非深比较——只要有一个位置不等即为「被编辑过」。
    /// </summary>
    public static bool HandlerEditedSelectedTools(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after)
    {
        if (before.Count != after.Count)
        {
            return true;
        }

        for (var i = 0; i < before.Count; i++)
        {
            if (!string.Equals(before[i], after[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 对应 TS <c>prompt</c> 里 <c>selectedTools</c> 的最终取值：扩展显式编辑过就用编辑结果，
    /// 否则以**实时活跃工具**为准（这样 <c>setActiveTools()</c> 的调用不会被这里覆盖回去）。
    /// </summary>
    public static IReadOnlyList<string> ResolveSelectedTools(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after,
        IReadOnlyList<string> liveActiveToolNames)
        => HandlerEditedSelectedTools(before, after) ? after : liveActiveToolNames;

    /// <summary>对应 TS <c>_throwIfExtensionCommand</c> 的错误文案。</summary>
    public static string FormatQueuedExtensionCommandError(string commandName)
        => $"Extension command \"/{commandName}\" cannot be queued. Use prompt() or execute the command when not streaming.";

    /// <summary>
    /// 对应 TS <c>_queueUserInput</c> 传给 <c>_runInputHandlers</c> 的 <c>streamingBehavior</c>：
    /// 非流式时为 <c>undefined</c>。
    /// </summary>
    public static string? ResolveQueueStreamingBehavior(bool isStreaming, string behavior)
        => isStreaming ? behavior : null;

    /// <summary>
    /// 对应 TS <c>clearQueue()</c>：先取两个队列的副本，再把它们清空（TS 直接把字段重置为 <c>[]</c>）。
    /// </summary>
    public static ClearedQueue ClearQueue(List<string> steeringMessages, List<string> followUpMessages)
    {
        var cleared = new ClearedQueue([.. steeringMessages], [.. followUpMessages]);
        steeringMessages.Clear();
        followUpMessages.Clear();
        return cleared;
    }

    // =========================================================================
    // 重试 / 压缩门
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>_willRetryAfterAgentEnd</c> 的前置门：已请求中止、未启用重试、或已用满次数时一律不重试。
    /// </summary>
    /// <remarks>
    /// 与 4e-3e-7 的 <see cref="WillRetryAfterAgentEnd"/> 互补：那个只覆盖 <c>agent_end</c> 事件类型的
    /// 单条判定，此处补齐 <c>settings.enabled</c> / <c>retryAttempt &gt;= maxRetries</c> 两档。
    /// </remarks>
    public static bool ShouldConsiderRetryAfterAgentEnd(
        bool abortRequested,
        bool retryEnabled,
        int retryAttempt,
        int maxRetries)
        => !abortRequested && retryEnabled && retryAttempt < maxRetries;

    /// <summary>对应 TS <c>_findLastAssistantMessage</c>：从尾向头找第一条助手消息（含被中止的）。</summary>
    public static AssistantMessage? FindLastAssistantMessage(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is AssistantMessage assistant)
            {
                return assistant;
            }
        }

        return null;
    }

    /// <summary>
    /// 对应 TS <c>_exceedsCompactionThreshold</c>：<c>contextWindow &lt;= 0</c> 时直接为假（不压缩），
    /// 否则交给 <see cref="Compaction.ShouldCompact"/>。
    /// </summary>
    public static bool ExceedsCompactionThreshold(
        long contextWindow,
        long projectedTokens,
        CompactionSettings settings)
        => contextWindow > 0 && Compaction.ShouldCompact(projectedTokens, contextWindow, settings);

    /// <summary>
    /// 对应 TS <c>_compactBeforeNextAssistantResponse</c> 的早退条件：无模型、虚拟模型、或未超阈值时
    /// 直接返回当前投影消息，不触发自动压缩。
    /// </summary>
    public static bool ShouldCompactBeforeNextAssistantResponse(
        bool hasModel,
        bool isVirtualModel,
        bool exceedsThreshold)
        => hasModel && !isVirtualModel && exceedsThreshold;

    /// <summary>
    /// 对应 TS <c>_installAgentRequestProjection</c> 里
    /// <c>const lastResponse = messages.findLastIndex(m =&gt; m.role === "assistant"); const userTurn = messages.slice(lastResponse + 1).some(m =&gt; m.role === "user");</c>。
    /// 无助手消息时 <c>findLastIndex</c> 为 <c>-1</c>，<c>slice(0)</c> 即全量消息。
    /// </summary>
    public static bool HasUserTurnAfterLastAssistant(IReadOnlyList<ChatMessage> messages)
    {
        var lastAssistant = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is AssistantMessage)
            {
                lastAssistant = i;
                break;
            }
        }

        for (var i = lastAssistant + 1; i < messages.Count; i++)
        {
            if (messages[i] is UserMessage)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 对应 TS <c>reason: failed ? "retry" : userTurn ? "user" : "continuation"</c>。
    /// </summary>
    public static string ResolveRouteReason(bool failed, bool userTurn)
        => failed ? RouteReason.Retry : userTurn ? RouteReason.User : RouteReason.Continuation;
}
