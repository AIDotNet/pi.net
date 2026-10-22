// ============================================================================
// `/compact` 的自定义指令必须同时落到「拆分回合」的回合前缀摘要上。
//
// 上游 adfc14461（#11058）：compact() 把 customInstructions 传给主摘要，却漏了
// generateTurnPrefixSummary，于是拆分回合时用户写的关注点只作用于历史部分。
// ============================================================================

using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class CompactionSummaryPromptTests
{
    private static ModelSpec Model() => new()
    {
        Id = "stub-chat",
        Name = "stub-chat",
        Api = "stub-api",
        Provider = "stub",
        BaseUrl = "https://stub.invalid",
        ContextWindow = 100_000,
        MaxTokens = 4096,
    };

    /// <summary>记录每次摘要请求的消息、并以一句话收尾的流。</summary>
    private static (StreamFn Stream, List<TranscriptContext> Requests) CapturingStream()
    {
        var requests = new List<TranscriptContext>();
        StreamFn stream = (_, context, _, _) =>
        {
            requests.Add(context);
            var message = new AssistantMessage(
                [new TextContent("SUMMARY")],
                StopReason.Stop,
                Model: "stub-chat",
                Api: "stub-api",
                Provider: "stub");
            var eventStream = new AssistantMessageEventStream();
            eventStream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
            eventStream.End(message);
            return Task.FromResult<IAssistantMessageEventStream>(eventStream);
        };
        return (stream, requests);
    }

    private static string PromptText(TranscriptContext context)
        => string.Join(
            "\n",
            context.Messages.Select(message => message switch
            {
                UserMessage user when user.Content is { } content => Text.ContentText(content),
                SystemMessage system => Text.ContentText(system),
                _ => "",
            }));

    /// <summary>
    /// 拆分回合、没有要压缩的历史时只有一次请求（回合前缀），它必须带上自定义指令。
    /// 上游 <c>applies custom instructions to the turn prefix summary of a split turn</c>。
    /// </summary>
    [Fact]
    public async Task AppliesCustomInstructionsToTheTurnPrefixSummaryOfASplitTurn()
    {
        var (stream, requests) = CapturingStream();
        var preparation = new CompactionPreparation(
            FirstKeptEntryId: "entry-keep",
            MessagesToSummarize: [],
            TurnPrefixMessages: [new UserMessage("hello", 1)],
            IsSplitTurn: true,
            TokensBefore: 100,
            PreviousSummary: "previous checkpoint",
            FileOps: CompactionUtils.CreateFileOps(),
            Settings: new CompactionSettings(true, 2000, 20));

        await Compaction.CompactAsync(
            preparation,
            Model(),
            "test-key",
            customInstructions: "keep migration decisions",
            streamFn: stream);

        Assert.Single(requests);
        Assert.Contains("Additional focus: keep migration decisions", PromptText(requests[0]));
    }

    /// <summary>没有自定义指令时不出现该段，避免空片段混进提示。</summary>
    [Fact]
    public async Task OmitsTheFocusParagraphWithoutCustomInstructions()
    {
        var (stream, requests) = CapturingStream();
        var preparation = new CompactionPreparation(
            FirstKeptEntryId: "entry-keep",
            MessagesToSummarize: [],
            TurnPrefixMessages: [new UserMessage("hello", 1)],
            IsSplitTurn: true,
            TokensBefore: 100,
            PreviousSummary: null,
            FileOps: CompactionUtils.CreateFileOps(),
            Settings: new CompactionSettings(true, 2000, 20));

        await Compaction.CompactAsync(preparation, Model(), "test-key", streamFn: stream);

        Assert.Single(requests);
        Assert.DoesNotContain("Additional focus:", PromptText(requests[0]));
    }
}
