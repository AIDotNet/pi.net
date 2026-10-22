using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>transcript 回放工具集测试。对应 TS transcript.test 语义。</summary>
public class TranscriptTests
{
    private static ToolDefinition Tool(string name, string description = "d") => new(
        name, description,
        new ToolSchema(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(),
        }));

    [Fact]
    public void GetCurrentToolsAppliesAddAndRemove()
    {
        var messages = new List<ChatMessage>
        {
            new SystemMessage(ToolsAdded: [Tool("a"), Tool("b")]),
            new SystemMessage(ToolsRemoved: ["a"]),
            new SystemMessage(ToolsAdded: [Tool("c")]),
        };
        var tools = Transcript.GetCurrentTools(messages);
        Assert.Equal(["b", "c"], tools.Select(t => t.Name).ToList());
    }

    [Fact]
    public void GetCurrentSystemMessagePatchesSectionsByName()
    {
        var messages = new List<ChatMessage>
        {
            new SystemMessage("base prompt", Timestamp: 0, Sections: new Dictionary<string, string?> { ["style"] = "brief" }),
            new SystemMessage("extra", Timestamp: 1, Sections: new Dictionary<string, string?> { ["style"] = null, ["tone"] = "warm" }),
        };
        var replayed = Transcript.GetCurrentSystemMessage(messages);
        Assert.NotNull(replayed);
        Assert.Equal("base prompt\n\nextra", replayed!.Content);
        Assert.NotNull(replayed.Sections);
        Assert.False(replayed.Sections!.ContainsKey("style")); // null 值 = 删除
        Assert.Equal("warm", replayed.Sections["tone"]);
    }

    [Fact]
    public void CollapseSystemMessagesReplaysAndDropsLater()
    {
        var context = new TranscriptContext(new List<ChatMessage>
        {
            new SystemMessage("base", Timestamp: 0),
            new UserMessage([new TextContent("hi")], 1),
            new SystemMessage("update", Timestamp: 2),
        });
        var collapsed = Transcript.CollapseSystemMessages(context);
        Assert.Equal(2, collapsed.Messages.Count);
        var head = Assert.IsType<SystemMessage>(collapsed.Messages[0]);
        Assert.Contains("update", Pi.Ai.Utils.Text.ContentText(head)); // 回放合并
        Assert.IsType<UserMessage>(collapsed.Messages[1]);
    }

    [Fact]
    public void ResolveTranscriptToolsAnchorsOnlyAdditiveHistory()
    {
        var additive = new List<ChatMessage>
        {
            new SystemMessage(ToolsAdded: [Tool("a")]),
            new SystemMessage(ToolsAdded: [Tool("b")]),
        };
        var (requestTools, anchorsAdditions) = Transcript.ResolveTranscriptTools(additive, supportsToolAdditions: true);
        Assert.True(anchorsAdditions);
        Assert.Equal(["a"], requestTools.Select(t => t.Name).ToList()); // 首条声明

        var nonAdditive = new List<ChatMessage>
        {
            new SystemMessage(ToolsAdded: [Tool("a")]),
            new SystemMessage(ToolsRemoved: ["a"]),
        };
        var (requestTools2, anchors2) = Transcript.ResolveTranscriptTools(nonAdditive, supportsToolAdditions: true);
        Assert.False(anchors2);
        Assert.Empty(requestTools2); // 删除后无工具
    }

    [Fact]
    public void CreateInitialSystemMessageOmittedWhenEmpty()
    {
        Assert.Null(Transcript.CreateInitialSystemMessage(null, null));
        var created = Transcript.CreateInitialSystemMessage("prompt", [Tool("a")]);
        Assert.NotNull(created);
        Assert.Equal("prompt", created!.Content);
        Assert.Single(created.ToolsAdded!);
    }
}

/// <summary>shortHash 测试（JS Math.imul 语义一致性）。</summary>
public class HashTests
{
    [Fact]
    public void MatchesJavaScriptImplementation()
    {
        // 期望值由 JS 参照实现计算。
        Assert.Equal("bliydt151m3se", Hash.ShortHash("pi"));
    }

    [Fact]
    public void IsDeterministicAndShort()
    {
        var a = Hash.ShortHash("system:0:tool-a,tool-b");
        var b = Hash.ShortHash("system:0:tool-a,tool-b");
        Assert.Equal(a, b);
        Assert.True(a.Length <= 14);
    }
}

/// <summary>流式部分 JSON 解析测试。对应 TS json-parse 测试语义。</summary>
public class JsonParseTests
{
    [Fact]
    public void ParsesCompleteJson()
    {
        var result = JsonParse.ParseStreamingJson("""{"name":"read","path":"a.txt"}""");
        Assert.Equal("read", result.Str("name"));
    }

    [Fact]
    public void RepairsControlCharacters()
    {
        var result = JsonParse.ParseStreamingJson("{\"text\":\"line1\nline2\"}");
        Assert.NotNull(result.Str("text"));
    }

    [Fact]
    public void ParsesPartialJsonWithOpenBrace()
    {
        var result = JsonParse.ParseStreamingJson("""{"name":"read","path":"a.txt""");
        Assert.Equal("read", result.Str("name"));
        Assert.Equal("a.txt", result.Str("path"));
    }

    [Fact]
    public void ParsesPartialWithTrailingComma()
    {
        var result = JsonParse.ParseStreamingJson("""{"a":1,"b":2,"c":""");
        System.IO.File.WriteAllText("D:/AI/参考项目/pi.net/diag-partial.txt",
            result?.ToJsonString() ?? "NULL");
        Assert.NotNull(result);
        Assert.Equal(1, result.Num("a"));
    }

    [Fact]
    public void ReturnsEmptyObjectOnGarbage()
    {
        var result = JsonParse.ParseStreamingJson("not json at all {{{");
        Assert.Empty(result);
    }

    [Fact]
    public void EmptyInputYieldsEmptyObject()
    {
        Assert.Empty(JsonParse.ParseStreamingJson(""));
        Assert.Empty(JsonParse.ParseStreamingJson(null));
    }

    [Fact]
    public void InvalidUnicodeEscapeIsKeptAsLiteralText()
    {
        // 上游 #10868：\usepackage 这类非法 \u 转义曾被当成合法转义，导致整段 JSON 解析失败、
        // partial-json 回退丢掉该字段及其后的所有字段。
        var result = JsonParse.ParseStreamingJson("""{"path":"a\usepackage{x}b","n":1}""");
        Assert.Equal(@"a\usepackage{x}b", result.Str("path"));
        Assert.Equal(1, result.Num("n"));
    }

    [Fact]
    public void ValidUnicodeEscapeStillDecodes()
    {
        var result = JsonParse.ParseStreamingJson("""{"path":"a\u4e2db"}""");
        Assert.Equal("a中b", result.Str("path"));
    }
}
