using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Path = Pi.Chord.Delta.Path;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 流式 partial 的提交按「追加」写入文本（`da412b039` 的 durable 侧文本部分）：一次提交只花新增文本的长度，
/// 不再把整段消息重新赋值。工具调用参数仍整体赋值——端口没有 pi-ai 的增量 JSON 解析器（见 `PartialChanges`）。
/// </summary>
public class HarnessPartialAppendTests
{
    private static readonly Context Ctx = Context.Background;
    private static readonly Path MessagePath = Path.Root.Append(Seg.Key("generation")).Append(Seg.Key("message"));
    private static readonly Path TextPath = MessagePath
        .Append(Seg.Key("content")).Append(Seg.Index(0)).Append(Seg.Key("text"));

    [Fact]
    public async Task StreamsTextAsAppendsInsteadOfReassigningTheMessage()
    {
        const string answer = "hello";
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewScriptedChatSetup(provider);
        // 每个增量都冲刷：这里看的是写入形状，不是节流。
        setup.Settings = setup.Settings with
        {
            Progress = AgentDocs.DefaultProgressPolicy with { PartialIntervalMs = 0 },
        };
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        // 每个增量等到它落盘再发下一个：这样每次冲刷都只带一个字符，写入形状不依赖并行负载下的合并。
        provider.SetResponses(HarnessTestSupport.FakeStep.AfterEachTextDelta(
            HarnessTestSupport.AssistantReply(answer), async index =>
                await HarnessTestSupport.WaitForAsync(async () =>
                    LivePartialText(await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx))
                        is { } text && text.Length >= index + 1)));
        var appends = new List<string>();
        var messageAssignments = 0;
        using var subscription = harness.SubscribeCommits((publication, _) =>
        {
            foreach (var change in publication.Changes)
            {
                if (change is not CommitChange.DocumentChanged
                    {
                        Change: { Record.Kind: "pi.live" } document,
                    })
                {
                    continue;
                }

                foreach (var op in document.Ops)
                {
                    switch (op)
                    {
                        case DeltaOp.Append appended when SamePath(appended.Path, TextPath):
                            appends.Add(appended.Text);
                            break;
                        // 整条消息重新赋值正是这次要避免的写法。
                        case DeltaOp.Set assigned when SamePath(assigned.Path, MessagePath):
                            messageAssignments += 1;
                            break;
                    }
                }
            }
        });
        try
        {
            var run = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("go")] }, Ctx);
            Assert.Equal(
                SubmissionStatus.Done, ((SubmissionRecord.InputRecord)(await run.WaitAsync(Ctx)).Record).Status);
            // 全程只追加，不整条重赋：首字符随整段 content 写入，其余每个字符各一次追加。
            Assert.Equal(answer[1..], string.Concat(appends));
            Assert.Equal(0, messageAssignments);
            var assistant = (await HarnessTestSupport.AllEntriesAsync(root))
                .Last(entry => entry.Kind == "pi.assistant").Model!
                .OfType<AssistantMessage>().Last();
            Assert.Equal(answer, assistant.Content.OfType<TextContent>().First().Text);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    private static bool SamePath(Path path, Path expected) => path.Segments.SequenceEqual(expected.Segments);

    /// <summary>live 文档里进行中 partial 的文本（<c>generation.message</c> 的助手消息）。</summary>
    private static string? LivePartialText(IReadOnlyDictionary<string, object?>? live)
        => live is not null
            && live.TryGetValue("generation", out var generation)
            && generation is IReadOnlyDictionary<string, object?> generationMap
            && generationMap.TryGetValue("message", out var message)
            && message is not null
                ? string.Concat(JsonTrees.Deserialize<AssistantMessage>(message).Content.OfType<TextContent>()
                    .Select(block => block.Text))
                : null;
}
