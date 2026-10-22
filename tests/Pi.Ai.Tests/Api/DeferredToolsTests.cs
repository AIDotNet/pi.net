using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>
/// 延迟工具（deferred tools）的落法（上游 e47b8e37a / #7709）：模型支持
/// <c>additional_tools</c> 输入项时就地插入，否则退回客户端 tool search，
/// 两者都不支持则迟到的工具直接进顶层 <c>tools</c>。
/// </summary>
public class DeferredToolsTests
{
    private static ModelSpec ResponsesModel(string provider = "openai") => new()
    {
        Id = "gpt-5.4", Name = "GPT-5.4", Api = "openai-responses", Provider = provider,
        BaseUrl = "https://api.openai.com/v1", Input = ["text"], Reasoning = true,
        ContextWindow = 400_000, MaxTokens = 128_000,
        Cost = new ModelCostRates(1.25, 10.00),
    };

    private static ToolDefinition Tool(string name) => new(name, "do it", new ToolSchema(
        new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(),
        }));

    /// <summary>基础工具在首条系统消息里；<paramref name="late"/> 之后再声明，形成「就地增量」。</summary>
    private static TranscriptContext Context(bool late = true)
    {
        var messages = new List<ChatMessage>
        {
            new SystemMessage("base", Timestamp: 0, ToolsAdded: [Tool("base_tool")]),
            new UserMessage([new TextContent("hi")], 1),
        };
        if (late) messages.Add(new SystemMessage("", Timestamp: 2, ToolsAdded: [Tool("late_tool")]));
        return new TranscriptContext(messages);
    }

    private static JsonArray Convert(
        TranscriptContext context, bool additionalTools, bool toolSearch, string provider = "openai")
        => OpenAiResponsesShared.ConvertResponsesMessages(
            ResponsesModel(provider), context, OpenAiResponses.ProviderIdSet,
            new ConvertResponsesMessagesOptions
            {
                // 就地增量要求系统消息能留在原处；否则 Transcript 会把它们折叠进首条，
                // 迟到的工具变成「请求级变更」，不再走 additional_tools / tool search。
                SupportsMidConvoSystemMessages = true,
                SupportsAdditionalTools = additionalTools,
                SupportsToolSearch = toolSearch,
            });

    private static List<JsonObject> Items(JsonArray messages)
        => [.. messages.Select(item => (JsonObject)item!)];

    private static List<string?> Types(JsonArray messages) => [.. Items(messages).Select(item => item.Str("type"))];

    [Fact]
    public void AdditionalToolsCarriesTheLateToolInsteadOfToolSearch()
    {
        var messages = Convert(Context(), additionalTools: true, toolSearch: false);
        var items = Items(messages);
        var additional = Assert.Single(items, item => item.Str("type") == "additional_tools");

        Assert.Equal("developer", additional.Str("role"));
        var tools = additional["tools"]!.AsArray();
        var tool = Assert.Single(tools.Select(entry => (JsonObject)entry!));
        Assert.Equal("late_tool", tool.Str("name"));
        Assert.Null(tool["defer_loading"]);

        Assert.DoesNotContain("tool_search_call", Types(messages));
        Assert.DoesNotContain("tool_search_output", Types(messages));
    }

    [Fact]
    public void FallsBackToClientToolSearchWhenAdditionalToolsIsUnsupported()
    {
        var messages = Convert(Context(), additionalTools: false, toolSearch: true);
        var items = Items(messages);
        var call = Assert.Single(items, item => item.Str("type") == "tool_search_call");
        var output = Assert.Single(items, item => item.Str("type") == "tool_search_output");

        Assert.Equal("client", call.Str("execution"));
        Assert.Equal("completed", call.Str("status"));
        Assert.Equal(call.Str("call_id"), output.Str("call_id"));
        var tool = Assert.Single(output["tools"]!.AsArray().Select(entry => (JsonObject)entry!));
        Assert.Equal("late_tool", tool.Str("name"));
        Assert.True(tool["defer_loading"]!.GetValue<bool>());
        Assert.DoesNotContain("additional_tools", Types(messages));
    }

    [Fact]
    public void NeitherSupportedPutsTheLateToolAtTheTopLevel()
    {
        var messages = Convert(Context(), additionalTools: false, toolSearch: false);

        Assert.DoesNotContain("additional_tools", Types(messages));
        Assert.DoesNotContain("tool_search_call", Types(messages));
        // 顶层 tools 由 BuildParams 发出；这里确认没有就地插入的标记项。
        Assert.DoesNotContain(Items(messages), item => item.Str("role") == "developer" && item["tools"] is not null);
    }

    [Fact]
    public void AdditionalToolsMarkerStaysBeforeTheLateCallAfterTheToolIsUsed()
    {
        var context = Context();
        var messages = new List<ChatMessage>(context.Messages)
        {
            new AssistantMessage(
                [
                    new ToolCallContent("call_late|fc_late", "late_tool", new JsonObject()),
                ],
                Provider: "openai", Api: "openai-responses", Model: "gpt-5.4", Timestamp: 3),
            new ToolResultMessage("call_late|fc_late", "late_tool", [new TextContent("done")], IsError: false, Timestamp: 4),
        };

        var converted = Convert(new TranscriptContext(messages), additionalTools: true, toolSearch: false);
        var items = Items(converted);
        var markerIndex = items.FindIndex(item => item.Str("type") == "additional_tools");
        var callIndex = items.FindIndex(item =>
            item.Str("type") == "function_call" && item.Str("name") == "late_tool");

        Assert.True(markerIndex >= 0);
        Assert.True(callIndex >= 0);
        Assert.True(markerIndex < callIndex);
        // 迟到的工具仍不在顶层请求工具里：只有 base_tool。
        var (requestTools, anchorsAdditions) = Transcript.ResolveTranscriptTools(messages, supportsToolAdditions: true);
        Assert.True(anchorsAdditions);
        Assert.Equal(["base_tool"], requestTools.Select(tool => tool.Name).ToList());
    }
}
