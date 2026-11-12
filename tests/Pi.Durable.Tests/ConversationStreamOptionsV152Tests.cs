using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Types;
using Pi.Durable.Harness;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 上游 1.5.2 #10410：<c>ConversationStreamOptions</c> 新增
/// <c>websocketConnectTimeoutMs</c> 与 <c>thinkingBudgets</c>，并可透传到 pi-ai 请求选项。
/// </summary>
public class ConversationStreamOptionsV152Tests
{
    [Fact]
    public void StreamOptionsToRequest_MapsTheTwoV152Fields()
    {
        var budgets = new ThinkingBudgets(Minimal: 1024, High: 8192);
        var request = Provider.StreamOptionsToRequest(new ConversationStreamOptions
        {
            TimeoutMs = 30_000,
            WebsocketConnectTimeoutMs = 15_000,
            ThinkingBudgets = budgets,
        });

        Assert.Equal(30_000L, request["timeoutMs"]);
        Assert.Equal(15_000L, request["websocketConnectTimeoutMs"]);
        Assert.Same(budgets, request["thinkingBudgets"]);
    }

    [Fact]
    public void StreamOptionsToRequest_OmitsAbsentV152Fields()
    {
        var request = Provider.StreamOptionsToRequest(new ConversationStreamOptions { TimeoutMs = 1000 });

        Assert.False(request.ContainsKey("websocketConnectTimeoutMs"));
        Assert.False(request.ContainsKey("thinkingBudgets"));
        Assert.Equal(1000L, request["timeoutMs"]);
    }

    [Fact]
    public void RoundTripsTheTwoV152FieldsThroughJsonTrees()
    {
        var original = new ConversationStreamOptions
        {
            WebsocketConnectTimeoutMs = 12_345,
            ThinkingBudgets = new ThinkingBudgets(Minimal: 512, Low: 1024, Medium: 2048, High: 4096),
        };

        // 对应 Generation.cs / Compaction.cs 从持久化 checkpoint 读回选项的路径。
        var tree = JsonTrees.ToTree(original);
        var back = JsonTrees.Deserialize<ConversationStreamOptions>(tree);

        Assert.Equal(12_345, back.WebsocketConnectTimeoutMs);
        Assert.NotNull(back.ThinkingBudgets);
        Assert.Equal(512, back.ThinkingBudgets!.Minimal);
        Assert.Equal(1024, back.ThinkingBudgets.Low);
        Assert.Equal(2048, back.ThinkingBudgets.Medium);
        Assert.Equal(4096, back.ThinkingBudgets.High);
    }

    [Fact]
    public void JsonTreesUsesTheUpstreamCamelCaseWireNames()
    {
        var tree = (Dictionary<string, object?>)JsonTrees.ToTree(new ConversationStreamOptions
        {
            WebsocketConnectTimeoutMs = 7000,
            ThinkingBudgets = new ThinkingBudgets(Low: 256),
        })!;

        Assert.True(tree.ContainsKey("websocketConnectTimeoutMs"));
        Assert.True(tree.ContainsKey("thinkingBudgets"));
        var budgets = Assert.IsType<Dictionary<string, object?>>(tree["thinkingBudgets"]);
        Assert.True(budgets.ContainsKey("low"));
    }

    [Fact]
    public void ProviderStreamOptions_FromDictionary_ReadsTheV152Keys()
    {
        // 装配后的请求字典最终由 Models 门面经 FromDictionary 消费；确认两个键被识别。
        var budgets = new ThinkingBudgets(Minimal: 128, High: 8192);
        var options = ProviderStreamOptions.FromDictionary(new Dictionary<string, object?>
        {
            ["websocketConnectTimeoutMs"] = 15_000,
            ["thinkingBudgets"] = budgets,
        });

        Assert.NotNull(options);
        Assert.Equal(15_000, options!.WebsocketConnectTimeoutMs);
        Assert.Same(budgets, options.ThinkingBudgets);
    }

    [Fact]
    public void ProviderStreamOptions_FromJson_ReadsTheV152Keys()
    {
        var options = ProviderStreamOptions.FromJson(new JsonObject
        {
            ["websocketConnectTimeoutMs"] = 15_000,
            ["thinkingBudgets"] = new JsonObject { ["minimal"] = 128, ["high"] = 8192 },
        });

        Assert.NotNull(options);
        Assert.Equal(15_000, options!.WebsocketConnectTimeoutMs);
        Assert.NotNull(options.ThinkingBudgets);
        Assert.Equal(128, options.ThinkingBudgets!.Minimal);
        Assert.Equal(8192, options.ThinkingBudgets.High);
    }
}
