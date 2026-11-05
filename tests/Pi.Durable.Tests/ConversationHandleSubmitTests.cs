using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 对话句柄的提交面（`ConversationHandle.submit()`）：与 <see cref="IConversation"/> 一样收任意草稿，
/// 不只是输入草稿——子代理对话用 continue、宿主用 write 都走这里。
/// </summary>
public class ConversationHandleSubmitTests
{
    private static readonly Context Ctx = Context.Background;

    [Fact]
    public async Task AcceptsADraftThatIsNotInput()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var handle = await harness.ConversationAsync(root.Id, Ctx);
            Assert.NotNull(handle);

            var submission = await handle!.SubmitAsync(new SubmissionDraft.Write
            {
                Entry = new EntryDraft { Kind = "note", Model = [new UserMessage([new TextContent("hi")], 1L)] },
            }, Ctx);

            var settled = await submission.WaitAsync(Ctx);
            Assert.True(settled.Record is SubmissionRecord.WriteRecord { Status: SubmissionStatus.Done });

            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            Assert.Contains(entries, entry => entry.Kind == "note");
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
