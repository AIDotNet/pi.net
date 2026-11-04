using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 延迟落盘（`6c2fd73b0`）：生成 partial 的提交只在内存采纳（<see cref="ObservedCommit.IsMemory"/>），
/// 宿主的 <see cref="ISession.SubscribeCommits"/> 只在该文档真正落盘之后才看得到（窗口到期、下一次读/写或关闭）。
/// </summary>
public class DeferredPersistenceTests
{
    private static readonly Context Ctx = Context.Background;

    [Fact]
    public async Task PartialCommitsAreDeferredAndEventuallyStored()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        setup.Settings = setup.Settings with
        {
            // 每个增量都冲刷；窗口拉到 10 分钟，让这段时间里落盘只可能发生在「刷新提交顺带取走持有」或关闭时。
            Progress = AgentDocs.DefaultProgressPolicy with
            {
                PartialIntervalMs = 0,
                PersistWithinMs = 600_000,
            },
        };
        const string answer = "hello";
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        // 每个增量等到它出现在 live 文档里再发下一个：否则整段流会在第一次冲刷之前结束，一次延迟提交都不会发生。
        provider.SetResponses(HarnessTestSupport.FakeStep.AfterEachTextDelta(
            HarnessTestSupport.AssistantReply(answer), async index =>
                await HarnessTestSupport.WaitForAsync(async () =>
                    LivePartialText(await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx))
                        is { } text && text.Length >= index + 1)));

        var memoryCommits = 0;
        var hostedLiveChanges = 0;
        using var memory = harness.ObserveCommits((observed, _) =>
        {
            if (observed.IsMemory) Interlocked.Increment(ref memoryCommits);
        });
        using var host = harness.SubscribeCommits((publication, _) =>
        {
            foreach (var change in publication.Changes)
            {
                if (change is CommitChange.DocumentChanged { Change.Record.Kind: "pi.live" })
                    Interlocked.Increment(ref hostedLiveChanges);
            }
        });

        try
        {
            var run = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            await run.WaitAsync(Ctx);
            // 流式期间至少有一批 partial 是「只在内存采纳」的。
            Assert.True(Volatile.Read(ref memoryCommits) > 0, "expected deferred (memory-only) commits");
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }

        // 关闭把还没落盘的持有内容写下去：宿主监听器看到了 pi.live。
        Assert.True(Volatile.Read(ref hostedLiveChanges) > 0, "stored copies should reach host listeners");
    }

    /// <summary>live 文档里流式文本的当前内容。</summary>
    private static string? LivePartialText(IReadOnlyDictionary<string, object?>? live)
        => live is not null
            && live.TryGetValue("generation", out var generation)
            && generation is IReadOnlyDictionary<string, object?> generationMap
            && generationMap.TryGetValue("message", out var message)
            && message is not null
                ? string.Concat(JsonTrees.Deserialize<AssistantMessage>(message).Content
                    .OfType<TextContent>().Select(block => block.Text))
                : null;
}
