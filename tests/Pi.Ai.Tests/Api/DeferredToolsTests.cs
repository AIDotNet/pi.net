using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>
/// 会话中工具变化如何落到协议上（上游 e47b8e37a / #7709 与 caa6967b6 / #11142，对应
/// <c>transcript-tool-changes.test.ts</c>）。
/// <para>三种传输能力：<c>changes</c>（所有工具都在就地声明，删除改成文本通报）、
/// <c>additions</c>（只能就地新增，删除/重定义把顶层列表前移一次）、
/// <c>none</c>（顶层列表就是当前工具集）。目标是让请求前缀尽量稳定，缓存命中不因后续加载而失效。</para>
/// </summary>
public class DeferredToolsTests
{
    private static ModelSpec Model(string provider = "openai") => new()
    {
        Id = "gpt-5.4", Name = "GPT-5.4", Api = "openai-responses", Provider = provider,
        BaseUrl = "https://api.openai.com/v1", Input = ["text"], Reasoning = true,
        ContextWindow = 400_000, MaxTokens = 128_000,
        Cost = new ModelCostRates(1.25, 10.00),
    };

    private static ToolDefinition Tool(string name, string description = "do it") => new(name, description,
        new ToolSchema(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(),
        }));

    private static JsonArray Convert(
        IReadOnlyList<ChatMessage> messages,
        bool midConvo,
        bool additionalTools,
        bool toolSearch)
    {
        var options = new ConvertResponsesMessagesOptions
        {
            SupportsMidConvoSystemMessages = midConvo,
            SupportsAdditionalTools = additionalTools,
            SupportsToolSearch = toolSearch,
        };
        return OpenAiResponsesShared.ConvertResponsesMessages(
            Model(), new TranscriptContext(messages.ToList()), OpenAiResponses.ProviderIdSet, options);
    }

    private static List<JsonObject> Items(JsonArray messages)
        => [.. messages.Select(item => (JsonObject)item!)];

    /// <summary>每个 <c>additional_tools</c> 项里第一个工具的名字，按出现顺序。</summary>
    private static List<string> Names(List<JsonObject> items) => [.. items
        .Where(item => item.Str("type") == "additional_tools")
        .Select(item => item["tools"]!.AsArray()[0]!.AsObject().Str("name")!)];

    private static List<string> Content(List<JsonObject> items) => [.. items
        .Where(item => item.Str("content") is not null)
        .Select(item => item.Str("content")!)];

    [Fact]
    public void ChangesModeDeclaresEveryToolInAdditionalTools()
    {
        var messages = new List<ChatMessage>
        {
            new SystemMessage("base prompt", Timestamp: 0, ToolsAdded: [Tool("base_tool")]),
            new UserMessage([new TextContent("before")], 1),
            new SystemMessage("updated guidance", Timestamp: 2, ToolsAdded: [Tool("late_tool")]),
        };

        var items = Items(Convert(messages, midConvo: true, additionalTools: true, toolSearch: false));

        // 顶层声明为空：后续 additional_tools 项无法替换顶层工具，所以一个工具都不放顶层。
        Assert.Empty(Transcript.ResolveTranscriptTools(messages, Transcript.TranscriptToolSupport.Changes).RequestTools);
        Assert.Equal(["base_tool", "late_tool"], Names(items));

        // 第二个 additional_tools 之前先声明「这是增量」，否则模型会把最新一项当成完整工具集。
        var noticeIndex = items.FindIndex(item => item.Str("content")?.Contains(
            "incremental tools update", StringComparison.Ordinal) == true);
        var secondToolsIndex = items.FindLastIndex(item => item.Str("type") == "additional_tools");
        Assert.True(noticeIndex >= 0);
        Assert.True(noticeIndex < secondToolsIndex);
    }

    [Fact]
    public void AdditionsModeUsesSyntheticToolSearch()
    {
        var messages = new List<ChatMessage>
        {
            new SystemMessage("base prompt", Timestamp: 0, ToolsAdded: [Tool("base_tool")]),
            new UserMessage([new TextContent("before")], 1),
            new SystemMessage("updated guidance", Timestamp: 2, ToolsAdded: [Tool("late_tool")]),
        };

        var items = Items(Convert(messages, midConvo: true, additionalTools: false, toolSearch: true));

        var requestTools = Transcript.ResolveTranscriptTools(
            messages, Transcript.TranscriptToolSupport.Additions).RequestTools;
        Assert.Equal(["base_tool"], requestTools.Select(tool => tool.Name).ToList());
        var call = Assert.Single(items, item => item.Str("type") == "tool_search_call");
        var output = Assert.Single(items, item => item.Str("type") == "tool_search_output");
        Assert.Equal(call.Str("call_id"), output.Str("call_id"));
        var tool = Assert.Single(output["tools"]!.AsArray().Select(entry => (JsonObject)entry!));
        Assert.Equal("late_tool", tool.Str("name"));
        Assert.True(tool["defer_loading"]!.GetValue<bool>());
        Assert.Empty(Names(items));
    }

    [Fact]
    public void NoneModeUsesTheCurrentToolList()
    {
        var messages = new List<ChatMessage>
        {
            new SystemMessage("base prompt", Timestamp: 0, ToolsAdded: [Tool("base_tool")]),
            new UserMessage([new TextContent("before")], 1),
            new SystemMessage("updated guidance", Timestamp: 2, ToolsAdded: [Tool("late_tool")]),
        };

        var items = Items(Convert(messages, midConvo: false, additionalTools: false, toolSearch: false));

        var requestTools = Transcript.ResolveTranscriptTools(
            messages, Transcript.TranscriptToolSupport.None).RequestTools;
        Assert.Equal(["base_tool", "late_tool"], requestTools.Select(tool => tool.Name).ToList());
        Assert.DoesNotContain("tool_search_call", items.Select(item => item.Str("type")));
        Assert.Empty(Names(items));
    }

    [Fact]
    public void AnnouncesOpenAiRemovalsAsText()
    {
        var messages = new List<ChatMessage>
        {
            new SystemMessage("base prompt", Timestamp: 0, ToolsAdded: [Tool("base_tool")]),
            new UserMessage([new TextContent("before")], 1),
            new SystemMessage("updated guidance", Timestamp: 2,
                ToolsRemoved: ["base_tool"], ToolsAdded: [Tool("late_tool")]),
        };

        var items = Items(Convert(messages, midConvo: true, additionalTools: true, toolSearch: false));

        Assert.Equal(["base_tool", "late_tool"], Names(items));
        var update = Content(items)[^1];
        Assert.Contains("updated guidance", update, StringComparison.Ordinal);
        Assert.Contains(
            "The following tools are no longer available. Do not call them:\n- base_tool",
            update, StringComparison.Ordinal);
    }

    [Fact]
    public void RedefinitionDoesNotAnnounceARemoval()
    {
        var messages = new List<ChatMessage>
        {
            new SystemMessage("base prompt", Timestamp: 0, ToolsAdded: [Tool("base_tool")]),
            new UserMessage([new TextContent("before")], 1),
            new SystemMessage("", Timestamp: 2,
                ToolsRemoved: ["base_tool"], ToolsAdded: [Tool("base_tool", "changed")]),
        };

        var items = Items(Convert(messages, midConvo: true, additionalTools: true, toolSearch: false));

        // 重定义 = 删同名 + 加同名：只发新的 additional_tools，不通报删除。
        var last = items.Last(item => item.Str("type") == "additional_tools");
        var tool = Assert.Single(last["tools"]!.AsArray().Select(entry => (JsonObject)entry!));
        Assert.Equal("changed", tool.Str("description"));
        Assert.DoesNotContain(Content(items), text => text.Contains("no longer available", StringComparison.Ordinal));
        Assert.Contains(Content(items),
            text => text.Contains("latest definition replaces the earlier one", StringComparison.Ordinal));
    }

    [Fact]
    public void KeepsTheRequestPrefixAcrossARemovalAndLaterLoads()
    {
        // 逐步转发同一段对话：初始工具、删掉 drop_tool、两次加载。每次请求的 input 必须以
        // 上一次的 input 为前缀，否则缓存整段失效（#10940）。
        var messages = new List<ChatMessage>
        {
            new SystemMessage("base prompt", Timestamp: 0,
                ToolsAdded: [Tool("base_tool"), Tool("drop_tool")]),
            new UserMessage([new TextContent("first")], 1),
            new SystemMessage("", Timestamp: 2, ToolsRemoved: ["drop_tool"]),
            new UserMessage([new TextContent("after removal")], 3),
            new SystemMessage("", Timestamp: 4, ToolsAdded: [Tool("late_tool")]),
            new UserMessage([new TextContent("after first load")], 5),
            new SystemMessage("", Timestamp: 6, ToolsAdded: [Tool("later_tool")]),
            new UserMessage([new TextContent("after second load")], 7),
        };

        var payloads = new List<List<JsonObject>>();
        for (var count = 2; count <= messages.Count; count += 2)
        {
            payloads.Add(Items(Convert(messages.Take(count).ToList(),
                midConvo: true, additionalTools: true, toolSearch: false)));
        }

        for (var index = 1; index < payloads.Count; index++)
        {
            var previous = payloads[index - 1];
            var current = payloads[index];
            Assert.True(current.Count >= previous.Count);
            Assert.Equal(
                previous.Select(item => item.ToJsonString()).ToList(),
                current.Take(previous.Count).Select(item => item.ToJsonString()).ToList());
        }

        var last = payloads[^1];
        Assert.Equal(
            [["base_tool", "drop_tool"], ["late_tool"], ["later_tool"]],
            last.Where(item => item.Str("type") == "additional_tools")
                .Select(item => item["tools"]!.AsArray().Select(entry => entry!.AsObject().Str("name")!).ToList())
                .ToList());
        Assert.Contains(last, item => item.Str("content")?.Contains(
            "no longer available. Do not call them:\n- drop_tool", StringComparison.Ordinal) == true);
        // 后续每次加载都重申「仍然不可用」的工具。
        var notices = last.Where(item => item.Str("content")?.Contains(
            "incremental tools update", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(2, notices.Count);
        Assert.All(notices, notice => Assert.Contains(
            "Still unavailable: drop_tool.", notice.Str("content"), StringComparison.Ordinal));
    }

    /// <summary>
    /// 同一条消息既新增又删除：增量提示排在它自己的删除通报**之前**，因此不能提前把该工具
    /// 算作「仍然不可用」（上游 530015d93）。
    /// </summary>
    [Fact]
    public void DoesNotListAToolAsStillUnavailableBeforeItsRemovalIsAnnounced()
    {
        var messages = new List<ChatMessage>
        {
            new SystemMessage("base prompt", Timestamp: 0, ToolsAdded: [Tool("base_tool"), Tool("drop_tool")]),
            new SystemMessage("", Timestamp: 1, ToolsAdded: [Tool("late_tool")], ToolsRemoved: ["drop_tool"]),
            new UserMessage([new TextContent("go")], 2),
        };

        var items = Items(Convert(messages, midConvo: true, additionalTools: true, toolSearch: false));

        // 顺序同 Codex：提示、工具，然后才是删除通报。
        var notice = Assert.Single(items, item => item.Str("content")?.Contains(
            "incremental tools update", StringComparison.Ordinal) == true);
        Assert.DoesNotContain("Still unavailable", notice.Str("content"), StringComparison.Ordinal);

        Assert.Equal("additional_tools", items[^3].Str("type"));
        Assert.Equal("late_tool", items[^3]["tools"]!.AsArray()[0]!.AsObject().Str("name"));
        Assert.Contains(
            "no longer available. Do not call them:\n- drop_tool",
            items[^2].Str("content"), StringComparison.Ordinal);
        Assert.Equal("user", items[^1].Str("role"));
    }
}
