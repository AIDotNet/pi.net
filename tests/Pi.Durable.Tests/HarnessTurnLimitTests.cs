using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using JsonDict = IReadOnlyDictionary<string, object?>;
using TaskStatus = Pi.Durable.Types.TaskStatus;

/// <summary>
/// 每会话轮次上限（harness-turn-limit.test.ts）：<c>AgentState.MaxTurns</c> 只数工具轮，达到上限的运行像失败一样
/// 结束（<c>afterFailedRun</c> 决定排队项），而 <c>onYield</c> 续延会把计数清零。
/// </summary>
public class HarnessTurnLimitTests
{
    private static readonly Context Ctx = Context.Background;

    /// <summary>一次带一个 <c>noop</c> 调用的工具轮。对应 TS <c>round()</c>。</summary>
    private static AssistantMessage Round(string callId) => new(
        [new ToolCallContent(callId, "noop", new Dictionary<string, object?>())],
        StopReason.ToolUse);

    private static AssistantMessage Done(string text) => HarnessTestSupport.AssistantReply(text);

    private static (HarnessTestSupport.ScriptedProvider Provider, HarnessTestSupport.ChatSetup Setup) Limited(long? maxTurns)
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        HarnessTestSupport.AddTool(setup.Registry, new HarnessTestSupport.ScriptedTool(
            "noop", "noop", (_, _) => Task.FromResult(new ToolExecutionResult())));
        _ = maxTurns;
        return (provider, setup);
    }

    [Fact]
    public async Task EndsARunAfterTheToolRoundThatReachesTheLimitLikeAFailure()
    {
        var (provider, setup) = Limited(1);
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(Round("c1")),
            HarnessTestSupport.FakeStep.Of(Done("done")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            await root.ConfigureAsync(new AgentChange { MaxTurns = 1 }, Ctx);
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            var record = Assert.IsType<SubmissionRecord.InputRecord>((await submission.WaitAsync(Ctx)).Record);

            Assert.Equal(SubmissionStatus.Unanswered, record.Status);
            Assert.Equal("turn_limit", record.Reason);
            Assert.Equal("1 turn", record.Detail);
            // 达到上限就不再请求模型：只有工具轮那一次。
            Assert.Equal(1, provider.CallCount);
            // 转录以本轮的 tool result 收尾（生成任务本身 completed，所以没有上报）。
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            Assert.Equal(DurableEntries.ToolResultEntry.Kind, entries[^1].Kind);
            Assert.Empty(setup.Reports);

            // 运行已经结束：live 里没有 run，但留下了 runFailure（结束它的那次提交写的）。
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx);
            Assert.False(live!.ContainsKey("run"));
            var failure = Assert.IsAssignableFrom<JsonDict>(live["runFailure"]);
            Assert.Equal("turn_limit", failure["reason"]);
            Assert.Equal("1 turn", failure["detail"]);
            Assert.Equal(submission.Id.Value,
                Convert.ToInt64(((IReadOnlyList<object?>)failure["inputs"]!).Single()));
            Assert.Equal(submission.Id, record.Id);

            var tasks = await harness.CommitAsync(
                tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = root.Id }, 20), Ctx);
            var generation = tasks.Items.Single(task => task.Kind == "pi.generation");
            Assert.Equal(TaskStatus.Terminal, generation.State.Status);
            Assert.Equal(TaskOutcomeStatus.Completed, generation.State.Outcome!.Status);
            // runFailure 记的是结束这次运行的那个任务。
            Assert.Equal(generation.Id.Value, Convert.ToInt64(failure["taskId"]));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task CountsToolRoundsOnlySoAnAnswerBelowTheLimitSettlesDone()
    {
        var (provider, setup) = Limited(2);
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(Round("c1")),
            HarnessTestSupport.FakeStep.Of(Done("done")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            await root.ConfigureAsync(new AgentChange { MaxTurns = 2 }, Ctx);
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            var record = Assert.IsType<SubmissionRecord.InputRecord>((await submission.WaitAsync(Ctx)).Record);

            // 一轮工具后仍在上限之下：模型被再问一次，应答照常结算 done。
            Assert.Equal(SubmissionStatus.Done, record.Status);
            Assert.Equal(2, provider.CallCount);
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            Assert.Equal("pi.assistant", entries[^1].Kind);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task StartingALaterRunClearsTheFailure()
    {
        var (provider, setup) = Limited(1);
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(Round("c1")),
            HarnessTestSupport.FakeStep.Of(Done("second")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            await root.ConfigureAsync(new AgentChange { MaxTurns = 1 }, Ctx);
            await (await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx)).WaitAsync(Ctx);
            Assert.True((await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx))!.ContainsKey("runFailure"));

            // 抬到两轮再提交：新的运行一开始就清掉上一次的失败记录（它不是在结束失败的同一次提交里开始的）。
            await root.ConfigureAsync(new AgentChange { MaxTurns = 2 }, Ctx);
            var secondSubmission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("again")] }, Ctx);
            var second = Assert.IsType<SubmissionRecord.InputRecord>((await secondSubmission.WaitAsync(Ctx)).Record);
            Assert.Equal(SubmissionStatus.Done, second.Status);
            Assert.False((await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx))!.ContainsKey("runFailure"));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task RejectsALimitBelowOne()
    {
        var (_, setup) = Limited(null);
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                root.ConfigureAsync(new AgentChange { MaxTurns = 0 }, Ctx));
            // 清掉上限是允许的。
            await root.ConfigureAsync(new AgentChange { ClearMaxTurns = true }, Ctx);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
