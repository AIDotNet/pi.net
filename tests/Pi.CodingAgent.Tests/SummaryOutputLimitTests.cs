using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 摘要请求不再按 <c>reserveTokens</c> 折算固定输出上限（上游 #9512/#9075/#10908）。
/// 固定上限会在思考与输出共享预算时让压缩直接撞 token 上限；改由 provider 层按实际请求定尺寸。
/// </summary>
public sealed class SummaryOutputLimitTests
{
    private static ModelSpec SummarizationModel() => new()
    {
        Id = "stub-summary",
        Name = "stub-summary",
        Api = "stub-api",
        Provider = "stub",
        BaseUrl = "https://stub.invalid",
        Type = ModelType.Chat,
        Input = [ModelInput.Text],
        Cost = new ModelCostRates(0, 0, 0, 0),
        ContextWindow = 200_000,
        MaxTokens = 64_000,
        Reasoning = true,
    };

    private static StreamFn CapturingStream(List<SimpleStreamOptions?> captured, string text)
        => (model, _, options, _) =>
        {
            captured.Add(options);
            var stream = new AssistantMessageEventStream();
            var message = new AssistantMessage(
                [new TextContent(text)],
                StopReason.Stop,
                Model: model.Id,
                Api: model.Api,
                Provider: model.Provider);
            stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
            stream.End(message);
            return Task.FromResult<IAssistantMessageEventStream>(stream);
        };

    [Fact]
    public async Task GenerateSummaryDoesNotCapOutputTokens()
    {
        var captured = new List<SimpleStreamOptions?>();
        var messages = new List<ChatMessage> { new UserMessage([new TextContent("hi")], 1) };

        var text = await Compaction.GenerateSummaryAsync(
            messages,
            SummarizationModel(),
            reserveTokens: 16_384,
            apiKey: "k",
            streamFn: CapturingStream(captured, "summarized"));

        Assert.Equal("summarized", text);
        var options = Assert.Single(captured);
        Assert.NotNull(options);
        // 关键回归点：不再出现 floor(0.8 * reserveTokens) = 13107 一类的固定上限。
        Assert.Null(options!.MaxTokens);
    }

    [Fact]
    public async Task SummaryKeepsReasoningLevelOfTheSession()
    {
        var captured = new List<SimpleStreamOptions?>();
        var messages = new List<ChatMessage> { new UserMessage([new TextContent("hi")], 1) };

        await Compaction.GenerateSummaryAsync(
            messages,
            SummarizationModel(),
            reserveTokens: 16_384,
            apiKey: "k",
            thinkingLevel: "high",
            streamFn: CapturingStream(captured, "summarized"));

        Assert.Equal("high", Assert.Single(captured)!.Reasoning);
    }
}
