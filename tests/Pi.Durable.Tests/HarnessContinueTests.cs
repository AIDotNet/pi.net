using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using JsonDict = IReadOnlyDictionary<string, object?>;

/// <summary>
/// continue 提交（harness-continue.test.ts）：不追加用户消息、直接在现有上下文上起一次运行。它从不排队，忙碌对话以
/// <see cref="ConversationBusy"/> 拒绝；上下文没有可回答的东西时不发请求就以 <c>nothing_to_answer</c> 结算。
/// </summary>
public class HarnessContinueTests
{
    private static readonly Context Ctx = Context.Background;

    [Fact]
    public async Task ContinueOnAContextEndingInAnAnswerFailsWithoutARequest()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("done")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            // 先答一次，上下文就以 assistant 结尾了。
            await (await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx)).WaitAsync(Ctx);
            Assert.Equal(1, provider.CallCount);

            var continuation = await root.SubmitAsync(new SubmissionDraft.Continue(), Ctx);
            var record = Assert.IsType<SubmissionRecord.ContinueRecord>((await continuation.WaitAsync(Ctx)).Record);
            Assert.Equal(SubmissionStatus.Unanswered, record.Status);
            Assert.Equal("nothing_to_answer", record.Reason);
            Assert.Equal("The conversation has nothing to answer", record.Detail);
            // 没有发出请求。
            Assert.Equal(1, provider.CallCount);

            // 运行结束但留有原因：下一次运行开始前它一直在。
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx);
            var failure = Assert.IsAssignableFrom<JsonDict>(live!["runFailure"]);
            Assert.Equal("nothing_to_answer", failure["reason"]);
            Assert.Equal(continuation.Id.Value,
                Convert.ToInt64(((IReadOnlyList<object?>)failure["inputs"]!).Single()));
            // 不落条目：条目仍只有那一问一答。
            Assert.Equal(
                ["pi.user", "pi.assistant"],
                (await HarnessTestSupport.AllEntriesAsync(root)).Select(entry => entry.Kind));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task ContinueKeepsTheFailedRunsFirstEntryForItsAfterRunHooks()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        // 第一次回答以错误收场：运行没有回答就结束，留下 runFailure。
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(new AssistantMessage([])
        {
            StopReason = StopReason.Error,
            ErrorMessage = "503 Service Unavailable",
            Model = "faux-1",
            Provider = "faux",
            UsageStats = new Pi.Ai.Types.Usage(0, 0),
        }));
        var asked = new List<RunSummary>();
        HarnessTestSupport.AddHooks(
            setup.Registry, "pi.generation", new HarnessTestSupport.RunEndHooksImpl(run =>
            {
                asked.Add(run);
                return [];
            }));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var failed = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
            var record = Assert.IsType<SubmissionRecord.InputRecord>((await failed.WaitAsync(Ctx)).Record);
            Assert.Equal(SubmissionStatus.Unanswered, record.Status);

            var userEntry = (await HarnessTestSupport.AllEntriesAsync(root)).First(entry => entry.Kind == "pi.user");
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx);
            var failure = Assert.IsAssignableFrom<JsonDict>(live!["runFailure"]);
            // 失败运行记下自己的第一个条目，供 continue 起的运行接着看它的历史。
            Assert.Equal(userEntry.Id.Value, Convert.ToInt64(failure["first"]));

            // 续跑：这一次回答了，afterRun 钩子拿到的 first 仍是失败那段历史的起点，而不是本次运行的答案。
            provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("done")));
            var continuation = await root.SubmitAsync(new SubmissionDraft.Continue(), Ctx);
            var continued = Assert.IsType<SubmissionRecord.ContinueRecord>((await continuation.WaitAsync(Ctx)).Record);
            Assert.Equal(SubmissionStatus.Done, continued.Status);
            await harness.WaitForIdleAsync(Ctx);

            var run = Assert.Single(asked);
            Assert.Equal("answer", run.Reason);
            Assert.Equal(userEntry.Id, run.First);
            Assert.NotEqual(run.Answer, run.First);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task ContinueOnABusyConversationIsRejectedWithoutWriting()
    {
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("never"), block: true);
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("slow")] }, Ctx);
            await provider.Reached;
            var before = await HarnessTestSupport.AllEntriesAsync(root);

            // 它从不排队：忙碌时直接拒绝，且不写任何东西。
            var busy = await Assert.ThrowsAsync<ConversationBusy>(() =>
                root.SubmitAsync(new SubmissionDraft.Continue(), Ctx));
            Assert.Equal(root.Id, busy.ConversationId);
            Assert.Equal(before.Count, (await HarnessTestSupport.AllEntriesAsync(root)).Count);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
