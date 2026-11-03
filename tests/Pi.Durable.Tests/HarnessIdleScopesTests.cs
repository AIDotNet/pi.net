using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 空闲检查按脏集合走（35dfa7573）：一次提交只重估「其下有任务变为存活或结束」的对话的空闲性，而不是每次提交
/// 全量扫。全量检查仍有两条路径：链加载解出未知后的下一次检查、以及过期定时器的重估。
/// </summary>
public class HarnessIdleScopesTests
{
    private static readonly Context Ctx = Context.Background;

    [Fact]
    public async Task ACommitRechecksOnlyTheConversationsItsTaskChangesCanChange()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("done")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            // 先跑到稳定：基线之后每次提交都该是「脏集合」路径，不产生新的全量检查。
            await (await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("one")] }, Ctx)).WaitAsync(Ctx);
            await HarnessTestSupport.FlushAsync();
            var baseline = harness.IdleFullChecks;

            await (await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("two")] }, Ctx)).WaitAsync(Ctx);
            await HarnessTestSupport.FlushAsync();

            // 单对话的运行没有触发链加载回退，也没有过期重估：全量检查次数不变。
            Assert.Equal(baseline, harness.IdleFullChecks);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
