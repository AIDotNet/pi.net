using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils.Image;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 4e-3e-9b：<c>agent-session.ts</c> prompt 主循环的纯决策逻辑——循环控制流、turn_end /
/// agent_before_settle 边界、prompt 前置与输入归一化、重试与压缩门。
/// 逐条对照 TS 参考实现（<c>D:\AI\参考项目\pi\packages\coding-agent\src\core\agent-session.ts</c>）。
/// </summary>
public sealed class AgentSessionLoopTests
{
    // ---------------------------------------------------------------------
    // 构造辅助
    // ---------------------------------------------------------------------

    private static UserMessage User(string text) => new(text, 0);

    private static AssistantMessage Assistant(params ContentBlock[] content)
        => new(content.Length == 0 ? [new TextContent("ok")] : content);

    private static AssistantMessage AssistantWith(StopReason stopReason)
        => new([new TextContent("x")], stopReason);

    private static SystemMessage System(string text) => new(text);

    private static ToolResultMessage ToolResult(string id = "call-1")
        => new(id, "read", [new TextContent("done")]);

    private static PromptLoopIteration Iteration(
        bool abortAtStart = false,
        bool? postRun = null,
        bool abortAfterPostRun = false,
        bool beforeSettle = false,
        bool abortAfterBeforeSettle = false)
        => new()
        {
            AbortRequestedAtStart = abortAtStart,
            PostAgentRunContinued = postRun,
            AbortRequestedAfterPostRun = abortAfterPostRun,
            BeforeSettleContinued = beforeSettle,
            AbortRequestedAfterBeforeSettle = abortAfterBeforeSettle,
        };

    // ---------------------------------------------------------------------
    // 消息 role
    // ---------------------------------------------------------------------

    [Fact]
    public void GetMessageRole_MapsEveryVariantToTsRoleLiteral()
    {
        Assert.Equal("system", AgentSession.GetMessageRole(System("s")));
        Assert.Equal("user", AgentSession.GetMessageRole(User("u")));
        Assert.Equal("assistant", AgentSession.GetMessageRole(Assistant()));
        Assert.Equal("toolResult", AgentSession.GetMessageRole(ToolResult()));
        Assert.Equal("custom", AgentSession.GetMessageRole(new CustomMessage("note", "c", true, 0)));
        Assert.Equal("bashExecution", AgentSession.GetMessageRole(
            new BashExecutionMessage("ls", "", 0, false, false, 0)));
        Assert.Equal("branchSummary", AgentSession.GetMessageRole(
            new BranchSummaryMessage("summary", null, 0)));
        Assert.Equal("compactionSummary", AgentSession.GetMessageRole(
            new CompactionSummaryMessage("summary", 10, 0)));
    }

    // ---------------------------------------------------------------------
    // prompt 循环控制流
    // ---------------------------------------------------------------------

    [Fact]
    public void PostAgentRunLeadsToContinue_RequiresBothFlags()
    {
        Assert.True(AgentSession.PostAgentRunLeadsToContinue(true, false));
        Assert.False(AgentSession.PostAgentRunLeadsToContinue(true, true));
        Assert.False(AgentSession.PostAgentRunLeadsToContinue(false, false));
        Assert.False(AgentSession.PostAgentRunLeadsToContinue(false, true));
    }

    [Fact]
    public void BeforeSettleLeadsToContinue_RequiresAllThreeConditions()
    {
        Assert.True(AgentSession.BeforeSettleLeadsToContinue(false, true, false));
        // TS 的 `abortRequested || !(...)` 短路：起始已中止时不再调用 before_settle。
        Assert.False(AgentSession.BeforeSettleLeadsToContinue(true, true, false));
        // 边界钩子自身拒绝了继续。
        Assert.False(AgentSession.BeforeSettleLeadsToContinue(false, false, false));
        // 边界钩子期间发生了中止（例如钩子里调用了 abort()）。
        Assert.False(AgentSession.BeforeSettleLeadsToContinue(false, true, true));
    }

    [Fact]
    public void ResolvePromptLoopStep_StopsWhenAbortRequestedAtStart()
    {
        // TS 的 while 条件先行：起始已中止时连 _handlePostAgentRun() 都不调用。
        var step = AgentSession.ResolvePromptLoopStep(Iteration(abortAtStart: true, postRun: true));
        Assert.Equal(PromptLoopAction.Stop, step);
    }

    [Fact]
    public void ResolvePromptLoopStep_ContinuesWhenPostAgentRunContinues()
    {
        var step = AgentSession.ResolvePromptLoopStep(Iteration(postRun: true));
        Assert.Equal(PromptLoopAction.ContinueRun, step);
    }

    [Fact]
    public void ResolvePromptLoopStep_StopsWhenAbortArrivesDuringPostRun()
    {
        // postRun 为真但此后请求中止：直接 break，不进 before_settle。
        var step = AgentSession.ResolvePromptLoopStep(
            Iteration(postRun: true, abortAfterPostRun: true));
        Assert.Equal(PromptLoopAction.Stop, step);
    }

    [Fact]
    public void ResolvePromptLoopStep_FallsThroughToBeforeSettleWhenPostRunFalse()
    {
        var step = AgentSession.ResolvePromptLoopStep(
            Iteration(postRun: false, beforeSettle: true));
        Assert.Equal(PromptLoopAction.ContinueRun, step);
    }

    [Fact]
    public void ResolvePromptLoopStep_StopsWhenAbortPrecedesBeforeSettle()
    {
        var step = AgentSession.ResolvePromptLoopStep(
            Iteration(postRun: false, abortAfterPostRun: true));
        Assert.Equal(PromptLoopAction.Stop, step);
    }

    [Fact]
    public void ResolvePromptLoopStep_StopsWhenBeforeSettleDeclines()
    {
        var step = AgentSession.ResolvePromptLoopStep(
            Iteration(postRun: false, beforeSettle: false));
        Assert.Equal(PromptLoopAction.Stop, step);
    }

    [Fact]
    public void ResolvePromptLoopStep_StopsWhenAbortArrivesDuringBeforeSettle()
    {
        var step = AgentSession.ResolvePromptLoopStep(
            Iteration(postRun: false, beforeSettle: true, abortAfterBeforeSettle: true));
        Assert.Equal(PromptLoopAction.Stop, step);
    }

    [Fact]
    public void ResolvePromptLoopStep_PostRunNullFallsThroughToBeforeSettle()
    {
        // postRun 为 null（未调用）时也应落到 before_settle 分支——覆盖「首轮无助手消息」的路径。
        var step = AgentSession.ResolvePromptLoopStep(
            Iteration(postRun: null, beforeSettle: true));
        Assert.Equal(PromptLoopAction.ContinueRun, step);
    }

    // ---------------------------------------------------------------------
    // turn_end / agent_before_settle 边界
    // ---------------------------------------------------------------------

    [Fact]
    public void OutcomeFromStopReason_MapsAbortedAndErrorOtherwiseCompleted()
    {
        Assert.Equal(AgentActivityOutcome.Aborted, AgentSession.OutcomeFromStopReason(StopReason.Aborted));
        Assert.Equal(AgentActivityOutcome.Error, AgentSession.OutcomeFromStopReason(StopReason.Error));
        Assert.Equal(AgentActivityOutcome.Completed, AgentSession.OutcomeFromStopReason(StopReason.Stop));
        Assert.Equal(AgentActivityOutcome.Completed, AgentSession.OutcomeFromStopReason(StopReason.Length));
        Assert.Equal(AgentActivityOutcome.Completed, AgentSession.OutcomeFromStopReason(StopReason.ToolUse));
        Assert.Equal(AgentActivityOutcome.Completed, AgentSession.OutcomeFromStopReason(StopReason.Pending));
    }

    [Fact]
    public void ResolveTurnEndBoundaryResult_ReportsInvalidContinuationWithoutRunnableContext()
    {
        // 扩展请求继续且上下文可继续 → 继续，不上报。
        Assert.Equal((true, false), AgentSession.ResolveTurnEndBoundaryResult(true, true));
        // 扩展请求继续但上下文不可继续 → 放弃并上报。
        Assert.Equal((false, true), AgentSession.ResolveTurnEndBoundaryResult(true, false));
        // 扩展没请求继续 → 不上报（无论上下文如何）。
        Assert.Equal((false, false), AgentSession.ResolveTurnEndBoundaryResult(false, false));
        Assert.Equal((false, false), AgentSession.ResolveTurnEndBoundaryResult(false, true));
    }

    [Fact]
    public void ComputeBoundaryCanContinue_RequiresNonSystemContextAndNonAssistantTail()
    {
        // 只有 system 消息 → 无上下文可继续。
        Assert.False(AgentSession.ComputeBoundaryCanContinue(
            [System("s")], hasPendingCustomMessages: false, hasQueuedMessages: false,
            SessionBoundaryKind.TurnEnd));

        // 有 user 消息、尾条不是 assistant → 可继续。
        Assert.True(AgentSession.ComputeBoundaryCanContinue(
            [System("s"), User("u")], false, false, SessionBoundaryKind.TurnEnd));

        // 尾条是 assistant → 不可继续（即便前面有 user）。
        Assert.False(AgentSession.ComputeBoundaryCanContinue(
            [System("s"), User("u"), Assistant()], false, false, SessionBoundaryKind.TurnEnd));

        // 尾条是 toolResult → 可继续。
        Assert.True(AgentSession.ComputeBoundaryCanContinue(
            [System("s"), User("u"), Assistant(), ToolResult()], false, false,
            SessionBoundaryKind.TurnEnd));
    }

    [Fact]
    public void ComputeBoundaryCanContinue_QueuedMessagesOnlyCountInTurnEnd()
    {
        // 尾条是 assistant：两条边界都额外看队列。
        var messages = new List<ChatMessage> { System("s"), User("u"), Assistant() };
        Assert.True(AgentSession.ComputeBoundaryCanContinue(
            messages, false, hasQueuedMessages: true, SessionBoundaryKind.TurnEnd));
        Assert.True(AgentSession.ComputeBoundaryCanContinue(
            messages, false, hasQueuedMessages: true, SessionBoundaryKind.AgentBeforeSettle));

        // 无队列时两条边界都不可继续。
        Assert.False(AgentSession.ComputeBoundaryCanContinue(
            messages, false, false, SessionBoundaryKind.TurnEnd));
        Assert.False(AgentSession.ComputeBoundaryCanContinue(
            messages, false, false, SessionBoundaryKind.AgentBeforeSettle));

        // 分叉只在「没有任何非 system 上下文」时出现：此时 contextCanContinue 为假、
        // 尾条也不是 assistant，故 turn_end 认队列（队列本身构成新上下文），before_settle 不认。
        var systemOnly = new List<ChatMessage> { System("s") };
        Assert.True(AgentSession.ComputeBoundaryCanContinue(
            systemOnly, false, true, SessionBoundaryKind.TurnEnd));
        Assert.False(AgentSession.ComputeBoundaryCanContinue(
            systemOnly, false, true, SessionBoundaryKind.AgentBeforeSettle));

        // 空消息列表同理（finalRole 为 null）。
        Assert.True(AgentSession.ComputeBoundaryCanContinue(
            [], false, true, SessionBoundaryKind.TurnEnd));
        Assert.False(AgentSession.ComputeBoundaryCanContinue(
            [], false, true, SessionBoundaryKind.AgentBeforeSettle));
    }

    [Fact]
    public void ComputeBoundaryCanContinue_PendingCustomContextAlwaysContinues()
    {
        // 待发自定义消息独立构成可继续理由，即便上下文只有 system 且尾条是 assistant。
        Assert.True(AgentSession.ComputeBoundaryCanContinue(
            [System("s"), Assistant()], hasPendingCustomMessages: true, hasQueuedMessages: false,
            SessionBoundaryKind.AgentBeforeSettle));
        Assert.True(AgentSession.ComputeBoundaryCanContinue(
            [], hasPendingCustomMessages: true, hasQueuedMessages: false,
            SessionBoundaryKind.TurnEnd));
    }

    [Fact]
    public void ResolveFinishTurnDecision_PreviousEndWins()
    {
        // 上一个 finishTurn 明确要求结束 → 直接沿用，扩展的继续请求不作数。
        Assert.Equal(FinishTurnAction.End,
            AgentSession.ResolveFinishTurnDecision(FinishTurnAction.End, extensionContinue: true));
        Assert.Equal(FinishTurnAction.End,
            AgentSession.ResolveFinishTurnDecision(FinishTurnAction.End, extensionContinue: false));
    }

    [Fact]
    public void ResolveFinishTurnDecision_ExtensionContinueWinsOverNothing()
    {
        Assert.Equal(FinishTurnAction.Continue,
            AgentSession.ResolveFinishTurnDecision(null, extensionContinue: true));
        Assert.Equal(FinishTurnAction.Continue,
            AgentSession.ResolveFinishTurnDecision(FinishTurnAction.Continue, extensionContinue: false));
        Assert.Equal(FinishTurnAction.Continue,
            AgentSession.ResolveFinishTurnDecision(FinishTurnAction.Continue, extensionContinue: true));

        // 两边都不要求继续 → TS 返回 undefined。
        Assert.Null(AgentSession.ResolveFinishTurnDecision(null, extensionContinue: false));
    }

    [Fact]
    public void GetPendingBoundaryMessages_QueuedBeforePendingCustom()
    {
        var queued = new List<ChatMessage> { User("q1"), User("q2") };
        var pending = new List<ChatMessage> { new CustomMessage("n", "c", true, 0) };

        var result = AgentSession.GetPendingBoundaryMessages(queued, pending);

        Assert.Equal(3, result.Count);
        Assert.Same(queued[0], result[0]);
        Assert.Same(queued[1], result[1]);
        Assert.Same(pending[0], result[2]);
    }

    // ---------------------------------------------------------------------
    // 空闲等待
    // ---------------------------------------------------------------------

    [Fact]
    public void CanResolveIdleWait_RequiresIdleAndWaiter()
    {
        Assert.True(AgentSession.CanResolveIdleWait(isIdle: true, hasResolveCallback: true));
        Assert.False(AgentSession.CanResolveIdleWait(isIdle: false, hasResolveCallback: true));
        Assert.False(AgentSession.CanResolveIdleWait(isIdle: true, hasResolveCallback: false));
        Assert.False(AgentSession.CanResolveIdleWait(isIdle: false, hasResolveCallback: false));
    }

    // ---------------------------------------------------------------------
    // prompt 前置
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolvePromptDisposition_OrdersHandledBeforeQueuedBeforeStarted()
    {
        Assert.Equal(PromptDisposition.Handled,
            AgentSession.ResolvePromptDisposition(extensionCommandHandled: true, inputHandled: false,
                streamingQueued: false));
        Assert.Equal(PromptDisposition.Handled,
            AgentSession.ResolvePromptDisposition(false, inputHandled: true, streamingQueued: true));
        Assert.Equal(PromptDisposition.Queued,
            AgentSession.ResolvePromptDisposition(false, false, streamingQueued: true));
        Assert.Equal(PromptDisposition.Started,
            AgentSession.ResolvePromptDisposition(false, false, false));
    }

    [Fact]
    public void ShouldTryExecuteExtensionCommand_RequiresExpansionAndSlash()
    {
        Assert.True(AgentSession.ShouldTryExecuteExtensionCommand(true, "/review"));
        Assert.False(AgentSession.ShouldTryExecuteExtensionCommand(false, "/review"));
        Assert.False(AgentSession.ShouldTryExecuteExtensionCommand(true, "review"));
        Assert.False(AgentSession.ShouldTryExecuteExtensionCommand(true, ""));
    }

    [Fact]
    public void ResolveInputHandlerOutcome_PassesThroughWithoutHandlers()
    {
        var images = new List<ImageContent> { new("aGk=", "image/png") };

        var outcome = AgentSession.ResolveInputHandlerOutcome(
            hasInputHandlers: false, "handled", "ignored", null, "original", images);

        Assert.NotNull(outcome);
        Assert.Equal("original", outcome!.Text);
        Assert.Same(images, outcome.Images);
    }

    [Fact]
    public void ResolveInputHandlerOutcome_ReturnsNullWhenHandled()
    {
        Assert.Null(AgentSession.ResolveInputHandlerOutcome(
            hasInputHandlers: true, "handled", "t", null, "original", null));

        // 未知 action 按「透传」处理（TS 的兜底 return）。
        var passthrough = AgentSession.ResolveInputHandlerOutcome(
            true, "continue", "t", null, "original", null);
        Assert.NotNull(passthrough);
        Assert.Equal("original", passthrough!.Text);
    }

    [Fact]
    public void ResolveInputHandlerOutcome_UsesTransformedTextAndFallsBackToOriginalImages()
    {
        var original = new List<ImageContent> { new("b3JpZ2lu", "image/png") };
        var transformed = new List<ImageContent> { new("dHJhbg==", "image/png") };

        var keepOriginal = AgentSession.ResolveInputHandlerOutcome(
            true, "transform", "rewritten", null, "original", original);
        Assert.Equal("rewritten", keepOriginal!.Text);
        Assert.Same(original, keepOriginal.Images);

        var replaced = AgentSession.ResolveInputHandlerOutcome(
            true, "transform", "rewritten", transformed, "original", original);
        Assert.Same(transformed, replaced!.Images);
    }

    // ---------------------------------------------------------------------
    // 图片与用户消息归一化
    // ---------------------------------------------------------------------

    [Fact]
    public async Task NormalizePromptImagesAsync_ReturnsEmptyForUndefinedImages()
    {
        var (images, hints) = await AgentSession.NormalizePromptImagesAsync(
            null,
            _ => throw new InvalidOperationException("不应被调用"));

        Assert.Empty(images);
        Assert.Empty(hints);
    }

    [Fact]
    public async Task NormalizePromptImagesAsync_RecordsHintsFromFailuresAndSuccesses()
    {
        var input = new List<ImageContent>
        {
            new("b2s=", "image/png"),
            new("YmFk", "image/png"),
            new("b2sy", "image/jpeg"),
        };

        var (images, hints) = await AgentSession.NormalizePromptImagesAsync(input, image => Task.FromResult<ProcessImageResult>(
            image.Data switch
            {
                "YmFk" => new ProcessImageResult.Fail("too large"),
                "b2s=" => new ProcessImageResult.Ok("cmVzaXplZA==", "image/png", ["downscaled"]),
                _ => new ProcessImageResult.Ok("cmVzaXplZDI=", "image/jpeg", []),
            }));

        // 失败的那张不产出图片，只留 hint；成功的两张各带自己的 hints。
        Assert.Equal(2, images.Count);
        Assert.Equal("cmVzaXplZA==", images[0].Data);
        Assert.Equal("image/png", images[0].MimeType);
        Assert.Equal("cmVzaXplZDI=", images[1].Data);
        Assert.Equal(new[] { "downscaled", "too large" }, hints);
    }

    [Fact]
    public void NormalizeUserMessageContent_SplitsTextAndImagesAndDropsEmptyImageList()
    {
        // 字符串分支：无图片。
        var textOnly = AgentSession.NormalizeUserMessageContent(MessageContent.From("hello"));
        Assert.Equal("hello", textOnly.Text);
        Assert.Null(textOnly.Images);

        // 块数组分支：文本按 "\n" 拼接，图片单独收集。
        var blocks = MessageContent.BlocksOf(
        [
            new TextContent("line1"),
            new ImageContent("aGk=", "image/png"),
            new TextContent("line2"),
        ]);
        var mixed = AgentSession.NormalizeUserMessageContent(blocks);
        Assert.Equal("line1\nline2", mixed.Text);
        Assert.NotNull(mixed.Images);
        Assert.Single(mixed.Images!);
        Assert.Equal("aGk=", mixed.Images![0].Data);

        // 一张图片都没有 → 回到 null（TS 把 [] 重置为 undefined）。
        var noImages = AgentSession.NormalizeUserMessageContent(
            MessageContent.BlocksOf([new TextContent("only text")]));
        Assert.Equal("only text", noImages.Text);
        Assert.Null(noImages.Images);
    }

    [Fact]
    public void HandlerEditedSelectedTools_DetectsLengthAndElementDifferences()
    {
        Assert.False(AgentSession.HandlerEditedSelectedTools(["a", "b"], ["a", "b"]));
        Assert.False(AgentSession.HandlerEditedSelectedTools([], []));

        // 长度不同。
        Assert.True(AgentSession.HandlerEditedSelectedTools(["a"], ["a", "b"]));
        // 同长度但某位置元素不同。
        Assert.True(AgentSession.HandlerEditedSelectedTools(["a", "b"], ["b", "a"]));
        // 顺序相同但大小写不同（Ordinal 比较）。
        Assert.True(AgentSession.HandlerEditedSelectedTools(["Read"], ["read"]));
    }

    [Fact]
    public void ResolveSelectedTools_PrefersLiveActiveToolsUnlessHandlerEdited()
    {
        // 处理器未编辑 → 以实时活跃工具为准（setActiveTools() 的结果不被覆盖）。
        Assert.Equal(new[] { "live" },
            AgentSession.ResolveSelectedTools(["a"], ["a"], ["live"]));

        // 处理器显式编辑 → 用编辑结果。
        Assert.Equal(new[] { "edited" },
            AgentSession.ResolveSelectedTools(["a"], ["edited"], ["live"]));

        // 空列表对空列表不算编辑。
        Assert.Equal(new[] { "live" }, AgentSession.ResolveSelectedTools([], [], new[] { "live" }));
    }

    [Fact]
    public void FormatQueuedExtensionCommandError_MatchesTsWording()
    {
        Assert.Equal(
            "Extension command \"/review\" cannot be queued. Use prompt() or execute the command when not streaming.",
            AgentSession.FormatQueuedExtensionCommandError("review"));
    }

    [Fact]
    public void ResolveQueueStreamingBehavior_OnlyPassesBehaviorWhileStreaming()
    {
        Assert.Equal("steer", AgentSession.ResolveQueueStreamingBehavior(true, "steer"));
        Assert.Equal("followUp", AgentSession.ResolveQueueStreamingBehavior(true, "followUp"));
        Assert.Null(AgentSession.ResolveQueueStreamingBehavior(false, "steer"));
    }

    [Fact]
    public void ClearQueue_ReturnsCopiesAndEmptiesInputs()
    {
        var steering = new List<string> { "s1", "s2" };
        var followUp = new List<string> { "f1" };

        var cleared = AgentSession.ClearQueue(steering, followUp);

        Assert.Equal(new[] { "s1", "s2" }, cleared.Steering);
        Assert.Equal(new[] { "f1" }, cleared.FollowUp);
        Assert.Empty(steering);
        Assert.Empty(followUp);

        // 返回的是副本：清空之后往入参队列里再塞东西，不影响已返回的结果。
        steering.Add("later");
        followUp.Add("later");
        Assert.Equal(new[] { "s1", "s2" }, cleared.Steering);
        Assert.Equal(new[] { "f1" }, cleared.FollowUp);
    }

    // ---------------------------------------------------------------------
    // 重试 / 压缩门
    // ---------------------------------------------------------------------

    [Fact]
    public void ShouldConsiderRetryAfterAgentEnd_RespectsAbortEnabledAndBudget()
    {
        Assert.True(AgentSession.ShouldConsiderRetryAfterAgentEnd(false, true, 0, 3));
        Assert.True(AgentSession.ShouldConsiderRetryAfterAgentEnd(false, true, 2, 3));

        Assert.False(AgentSession.ShouldConsiderRetryAfterAgentEnd(true, true, 0, 3));
        Assert.False(AgentSession.ShouldConsiderRetryAfterAgentEnd(false, false, 0, 3));
        // retryAttempt >= maxRetries。
        Assert.False(AgentSession.ShouldConsiderRetryAfterAgentEnd(false, true, 3, 3));
        Assert.False(AgentSession.ShouldConsiderRetryAfterAgentEnd(false, true, 4, 3));
    }

    [Fact]
    public void FindLastAssistantMessage_ReturnsLastAssistantIncludingAborted()
    {
        Assert.Null(AgentSession.FindLastAssistantMessage([System("s"), User("u")]));

        var aborted = AssistantWith(StopReason.Aborted);
        var last = Assistant();
        var messages = new List<ChatMessage> { User("u"), aborted, User("u2"), last, ToolResult() };

        // 尾部 toolResult 之后无助手 → 取到的是最后一个助手。
        Assert.Same(last, AgentSession.FindLastAssistantMessage(messages));
        // 被中止的助手也参与查找。
        Assert.Same(aborted, AgentSession.FindLastAssistantMessage([User("u"), aborted, User("u2")]));
    }

    [Fact]
    public void ExceedsCompactionThreshold_FalseForNonPositiveContextWindow()
    {
        var settings = new CompactionSettings(true, 100, 200);

        // contextWindow <= 0 直接为假，与 settings 无关。
        Assert.False(AgentSession.ExceedsCompactionThreshold(0, 10_000, settings));
        Assert.False(AgentSession.ExceedsCompactionThreshold(-1, 10_000, settings));

        // 正常窗口走 Compaction.ShouldCompact：tokens > window - reserve 才算超。
        Assert.True(AgentSession.ExceedsCompactionThreshold(1000, 1000, settings));
        Assert.False(AgentSession.ExceedsCompactionThreshold(1000, 900, settings));

        // 未启用压缩时恒为假。
        Assert.False(AgentSession.ExceedsCompactionThreshold(
            1000, 1000, new CompactionSettings(false, 100, 200)));
    }

    [Fact]
    public void ShouldCompactBeforeNextAssistantResponse_RequiresRealModelOverThreshold()
    {
        Assert.True(AgentSession.ShouldCompactBeforeNextAssistantResponse(true, false, true));

        Assert.False(AgentSession.ShouldCompactBeforeNextAssistantResponse(false, false, true));
        Assert.False(AgentSession.ShouldCompactBeforeNextAssistantResponse(true, true, true));
        Assert.False(AgentSession.ShouldCompactBeforeNextAssistantResponse(true, false, false));
    }

    [Fact]
    public void HasUserTurnAfterLastAssistant_ScansOnlyMessagesAfterLastAssistant()
    {
        // 无助手消息 → 扫描全部消息（TS 的 slice(0)）。
        Assert.True(AgentSession.HasUserTurnAfterLastAssistant([System("s"), User("u")]));
        Assert.False(AgentSession.HasUserTurnAfterLastAssistant([System("s")]));

        // 助手之后的 user 才算「用户回合」。
        Assert.True(AgentSession.HasUserTurnAfterLastAssistant([User("u"), Assistant(), User("u2")]));
        Assert.False(AgentSession.HasUserTurnAfterLastAssistant([User("u"), Assistant()]));
        Assert.False(AgentSession.HasUserTurnAfterLastAssistant(
            [User("u"), Assistant(), ToolResult()]));

        // 只看最后一个助手之后：更早的 user 不计。
        Assert.False(AgentSession.HasUserTurnAfterLastAssistant(
            [User("u"), Assistant(), Assistant()]));
    }

    [Fact]
    public void ResolveRouteReason_PrefersRetryThenUserThenContinuation()
    {
        Assert.Equal(RouteReason.Retry, AgentSession.ResolveRouteReason(failed: true, userTurn: true));
        Assert.Equal(RouteReason.Retry, AgentSession.ResolveRouteReason(failed: true, userTurn: false));
        Assert.Equal(RouteReason.User, AgentSession.ResolveRouteReason(failed: false, userTurn: true));
        Assert.Equal(RouteReason.Continuation, AgentSession.ResolveRouteReason(false, false));
    }
}
