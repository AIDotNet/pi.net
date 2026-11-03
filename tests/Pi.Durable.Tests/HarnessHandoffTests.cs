using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 工具交接后运行继续（38ff99177）：handoff 的重置总是先于每个排队项写入，然后只有 terminate 才在 final 边界结束
/// 运行——否则运行在 handoff 的新上下文里交给下一个生成继续。
/// </summary>
public class HarnessHandoffTests
{
    private static readonly Context Ctx = Context.Background;

    private static AssistantMessage Round(string callId) => new(
        [new ToolCallContent(callId, "hop", new Dictionary<string, object?>())],
        StopReason.ToolUse);

    [Fact]
    public async Task AHandoffContinuesTheRunInItsNewContext()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        provider.SetResponses(
            HarnessTestSupport.FakeStep.Of(Round("c1")),
            HarnessTestSupport.FakeStep.Of(HarnessTestSupport.AssistantReply("after")));
        HarnessTestSupport.AddTool(setup.Registry, new HarnessTestSupport.ScriptedTool(
            "hop", "hop", (_, _) => Task.FromResult(new ToolExecutionResult
            {
                Content = [new TextContent("hopped")],
                // 交接但不终止：运行应在新上下文里继续。
                Control = new ToolControl { Handoff = "switch" },
            })));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            var record = Assert.IsType<SubmissionRecord.InputRecord>((await submission.WaitAsync(Ctx)).Record);
            Assert.Equal(SubmissionStatus.Done, record.Status);

            // 交接不结束运行：模型在新上下文里又被问了一次并回答。
            Assert.Equal(2, provider.CallCount);

            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            // 条目序（系统提示段随上下文重建插在中间，过滤后再比）：
            // user → assistant(toolcall) → toolResult → pi.reset（head=self）→ assistant(after)。
            Assert.Equal(
                ["pi.user", "pi.assistant", DurableEntries.ToolResultEntry.Kind, DurableEntries.ResetEntry.Kind, "pi.assistant"],
                entries.Select(entry => entry.Kind).Where(kind => kind != DurableEntries.SystemEntry.Kind));
            var reset = entries.Single(entry => entry.Kind == DurableEntries.ResetEntry.Kind);
            Assert.NotNull(reset.Head);
            var resetMessage = Assert.IsType<UserMessage>(Assert.Single(reset.Model!));
            Assert.Equal("switch",
                Assert.IsType<TextContent>(Assert.Single(resetMessage.Content.ToBlocks())).Text);
            // 最后的回答落在新上下文里：重置之后。
            Assert.True(entries[^1].Id.Value > reset.Id.Value);
            Assert.Equal("after",
                Assert.IsType<TextContent>(Assert.Single(
                    Assert.IsType<AssistantMessage>(entries[^1].Model!.First()).Content)).Text);

            // 运行正常结束：没有 run、没有 runFailure。
            var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx);
            Assert.False(live!.ContainsKey("run"));
            Assert.False(live.ContainsKey("runFailure"));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
