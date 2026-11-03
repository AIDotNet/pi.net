// ============================================================================
// pi-messages contentIndex hardening (upstream 89c921d8b).
//
// `contentIndex` comes from the wire and keys the content array. `__proto__` would reach
// Array.prototype and a huge index would make a sparse array of billions of holes, so a `*_start`
// may only add the next block and every other event must point at an existing one.
// ============================================================================

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Tests.Auth;
using Xunit;

namespace Pi.Ai.Tests.Api;

public class PiMessagesContentIndexTests
{
    private static ModelSpec Spec() => new()
    {
        Id = "pi-messages-test",
        Name = "pi-messages-test",
        Api = "pi-messages",
        Provider = "pi",
        BaseUrl = "https://pi.example.com",
        ContextWindow = 200_000,
        MaxTokens = 8_192,
        Input = [ModelInput.Text],
        Cost = new ModelCostRates(0, 0, 0, 0),
    };

    /// <summary>把若干事件拼成 SSE 体（`data: {json}` + 空行）。</summary>
    private static string Sse(params string[] events)
    {
        var builder = new StringBuilder();
        foreach (var @event in events)
        {
            builder.Append("data: ").Append(@event).Append("\n\n");
        }
        return builder.ToString();
    }

    private static HttpResponseMessage Response(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static async Task<AssistantMessage> RunAsync(string body)
    {
        var handler = new FakeHttpHandler(_ => Response(body));
        var stream = PiMessages.Stream(
            Spec(),
            new TranscriptContext([new UserMessage([new TextContent("hi")], 1)]),
            new PiMessagesOptions { ApiKey = "test-key" },
            new HttpClient(handler));
        AssistantMessage? final = null;
        await foreach (var @event in stream)
        {
            switch (@event)
            {
                case AssistantMessageEvent.Done done: final = done.Message; break;
                case AssistantMessageEvent.Error error: final = error.Message; break;
            }
        }

        return Assert.IsType<AssistantMessage>(final);
    }

    [Theory]
    // 稀疏数组：几十亿个空洞。
    [InlineData("""{"type":"text_start","contentIndex":4294967000}""")]
    // 原型污染：下标会被当对象键用。
    [InlineData("""{"type":"text_start","contentIndex":"__proto__"}""")]
    // JSON null 同样不是合法下标。
    [InlineData("""{"type":"text_start","contentIndex":null}""")]
    // 非整数。
    [InlineData("""{"type":"text_start","contentIndex":0.5}""")]
    // 负数。
    [InlineData("""{"type":"text_start","contentIndex":-1}""")]
    public async Task RejectsIndexesThatAreNotTheNextBlock(string badEvent)
    {
        var final = await RunAsync(Sse("""{"type":"start"}""", badEvent, """{"type":"done","reason":"stop"}"""));

        Assert.Equal(StopReason.Error, final.StopReason);
        Assert.Contains("Invalid contentIndex", final.ErrorMessage);
        Assert.True(final.Content.Count <= 1, $"content 不应被撑开，实际 {final.Content.Count} 块");
    }

    [Fact]
    public async Task RejectsANonStartEventThatPointsAtNoBlock()
    {
        // text_delta 只能落在已存在的块上；此时一个块都没有。
        var final = await RunAsync(Sse(
            """{"type":"start"}""",
            """{"type":"text_delta","contentIndex":0,"delta":"x"}""",
            """{"type":"done","reason":"stop"}"""));

        Assert.Equal(StopReason.Error, final.StopReason);
        Assert.Contains("Invalid contentIndex", final.ErrorMessage);
        Assert.Empty(final.Content);
    }

    [Fact]
    public async Task RejectsAToolCallEndIndexedByANonNumberAfterARealBlock()
    {
        var final = await RunAsync(Sse(
            """{"type":"start"}""",
            """{"type":"text_start","contentIndex":0}""",
            """{"type":"toolcall_end","contentIndex":"__proto__","toolCall":{"polluted":true}}""",
            """{"type":"done","reason":"stop"}"""));

        Assert.Equal(StopReason.Error, final.StopReason);
        Assert.Contains("Invalid contentIndex", final.ErrorMessage);
        Assert.True(final.Content.Count <= 1, $"content 不应被撑开，实际 {final.Content.Count} 块");
    }

    [Fact]
    public async Task AcceptsSequentialStartsAndTheirDeltas()
    {
        // 正控：合法下标照常工作，说明上面的拒绝不是「一律拒绝」。
        var final = await RunAsync(Sse(
            """{"type":"start"}""",
            """{"type":"text_start","contentIndex":0}""",
            """{"type":"text_delta","contentIndex":0,"delta":"晚"}""",
            """{"type":"text_delta","contentIndex":0,"delta":"安"}""",
            """{"type":"text_start","contentIndex":1}""",
            """{"type":"text_delta","contentIndex":1,"delta":"!"}""",
            """{"type":"done","reason":"stop"}"""));

        Assert.Equal(StopReason.Stop, final.StopReason);
        Assert.Equal(["晚安", "!"], final.Content.OfType<TextContent>().Select(block => block.Text));
    }
}
