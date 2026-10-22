using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using DurableHarness = Pi.Durable.Harness.Harness;

/// <summary>
/// <c>settings.afterFailedRun</c>（spec §6）：失败的运行（<c>model_error</c> / <c>no_model</c>）对它后面排队的
/// 输入做什么——留着（缺省）、用它们启动下一个运行，或以 <c>run_failed</c> 撤回。
/// 对应 TS <c>test/harness-failed-run-inbox.test.ts</c>。
/// </summary>
public class HarnessFailedRunTests
{
    private static readonly Context Ctx = HarnessTestSupport.Ctx;

    [Fact]
    public async Task KeepsTheQueuedInputBehindAFailedRunByDefault()
    {
        var (harness, root, second) = await FailWithQueuedInputAsync(null);
        try
        {
            await harness.WaitForIdleAsync(Ctx);
            var queued = Assert.IsType<SubmissionRecord.InputRecord>(await second.StatusAsync(Ctx));
            Assert.Equal(SubmissionStatus.Queued, queued.Status);

            // 下一个输入运行队列，行为与失败前一致。
            var third = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("anything else?")] }, Ctx);
            Assert.Equal(
                SubmissionStatus.Done,
                Assert.IsType<SubmissionRecord.InputRecord>((await second.WaitAsync(Ctx)).Record).Status);
            Assert.Equal(
                SubmissionStatus.Done,
                Assert.IsType<SubmissionRecord.InputRecord>((await third.WaitAsync(Ctx)).Record).Status);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task StartsTheNextRunWithTheQueuedInputWhenContinue()
    {
        var (harness, root, second) = await FailWithQueuedInputAsync(AfterFailedRun.Continue);
        try
        {
            // 失败的运行用排队的输入开了下一个运行：它由第二次应答作答。
            var settled = Assert.IsType<SubmissionRecord.InputRecord>((await second.WaitAsync(Ctx)).Record);
            Assert.Equal(SubmissionStatus.Done, settled.Status);
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            var answer = entries.Last(entry => entry.Kind == "pi.assistant");
            Assert.Equal("second answer", TextOf(Assert.IsType<AssistantMessage>(Assert.Single(answer.Model!))));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task WithdrawsTheQueuedInputBehindAFailedRunWhenWithdraw()
    {
        var (harness, _, second) = await FailWithQueuedInputAsync(AfterFailedRun.Withdraw);
        try
        {
            var settled = Assert.IsType<SubmissionRecord.InputRecord>((await second.WaitAsync(Ctx)).Record);
            Assert.Equal(SubmissionStatus.Unanswered, settled.Status);
            Assert.Equal("run_failed", settled.Reason);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    /// <summary>
    /// 排队写入不受失败策略影响地保留在收件箱：<c>continue</c> 在失败运行的边界把它放置，
    /// <c>withdraw</c> 只撤回用户输入。对应 TS <c>place queued writes, and keep them with withdraw</c>。
    /// </summary>
    [Fact]
    public async Task PlacesQueuedWritesAndKeepsThemWithWithdraw()
    {
        foreach (var policy in new[] { AfterFailedRun.Continue, AfterFailedRun.Withdraw })
        {
            var provider = new HarnessTestSupport.ScriptedProvider();
            var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
            setup.Settings = setup.Settings with { AfterFailedRun = policy };
            var (failing, reached, release) = FailingOnce();
            provider.SetResponses(
                failing, HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("second answer")));
            var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
            try
            {
                var first = await root.SubmitAsync(
                    new SubmissionDraft.Input { Content = [new TextContent("first")] }, Ctx);
                await reached.WaitAsync(TimeSpan.FromSeconds(5));
                var write = await root.SubmitAsync(new SubmissionDraft.Write
                {
                    Entry = new EntryDraft
                    {
                        Kind = "test.note",
                        Model = [new UserMessage([new TextContent("a note the model sees")], 1)],
                    },
                }, Ctx);
                Assert.Equal(
                    SubmissionStatus.Queued,
                    Assert.IsType<SubmissionRecord.WriteRecord>(await write.StatusAsync(Ctx)).Status);

                release();
                await first.WaitAsync(Ctx);
                await harness.WaitForIdleAsync(Ctx);

                var settled = Assert.IsType<SubmissionRecord.WriteRecord>(await write.StatusAsync(Ctx));
                Assert.Equal(
                    policy == AfterFailedRun.Continue ? SubmissionStatus.Done : SubmissionStatus.Queued,
                    settled.Status);
            }
            finally
            {
                await HarnessTestSupport.CloseQuietlyAsync(harness);
            }
        }
    }

    // ─── 脚手架 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 一个「永久失败」的应答：请求到达后停在门闩上，放行时以 <c>error</c> 结束（<c>Invalid request</c>）。
    /// 对应 TS <c>failingOnce()</c>。
    /// </summary>
    private static (HarnessTestSupport.FakeStep Step, Task Reached, Action Release) FailingOnce()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var step = HarnessTestSupport.FakeStep.Of(async _ =>
        {
            reached.TrySetResult();
            await gate.Task.ConfigureAwait(false);
            return new AssistantMessage([])
            {
                StopReason = StopReason.Error,
                ErrorMessage = "Invalid request",
                Model = "faux-1",
                Provider = "faux",
                UsageStats = new Pi.Ai.Types.Usage(0, 0),
            };
        });
        return (step, reached.Task, () => gate.TrySetResult());
    }

    /// <summary>在 <paramref name="policy"/> 下制造「运行失败且其后有一个排队输入」，返回该输入。</summary>
    private static async Task<(HarnessImpl Harness, IConversation Root, ISubmission Second)>
        FailWithQueuedInputAsync(AfterFailedRun? policy)
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        if (policy is { } decided) setup.Settings = setup.Settings with { AfterFailedRun = decided };
        var (failing, reached, release) = FailingOnce();
        provider.SetResponses(
            failing,
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("second answer")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("third answer")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        var first = await root.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("analyze this report")] }, Ctx);
        await reached.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await root.SubmitAsync(
            new SubmissionDraft.Input
            {
                Content = [new TextContent("email me the summary")],
                WhenBusy = "followUp",
            }, Ctx);
        release();
        var settled = Assert.IsType<SubmissionRecord.InputRecord>((await first.WaitAsync(Ctx)).Record);
        Assert.Equal(SubmissionStatus.Unanswered, settled.Status);
        Assert.Equal("model_error", settled.Reason);
        return (harness, root, second);
    }

    private static string TextOf(AssistantMessage message)
        => string.Concat(message.Content.OfType<TextContent>().Select(block => block.Text));
}
