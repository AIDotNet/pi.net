using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class AgentSessionLeafTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-asess-" + Guid.NewGuid().ToString("N")[..8]);

    public AgentSessionLeafTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ParseSkillBlock_ExtractsNameLocationContentAndUserMessage()
    {
        var text = """
            <skill name="review" location="/skills/review.md">
            body
            </skill>

            please run it
            """;
        var parsed = AgentSession.ParseSkillBlock(text);
        Assert.NotNull(parsed);
        Assert.Equal("review", parsed!.Name);
        Assert.Equal("/skills/review.md", parsed.Location);
        Assert.Equal("body", parsed.Content);
        Assert.Equal("please run it", parsed.UserMessage);
    }

    [Fact]
    public void ParseSkillBlock_ReturnsNullWhenUnmatched()
        => Assert.Null(AgentSession.ParseSkillBlock("plain text"));

    [Fact]
    public void WithoutDeletedHeaders_DropsNullValues()
    {
        var headers = AgentSession.WithoutDeletedHeaders(new Dictionary<string, string?>
        {
            ["Keep"] = "1",
            ["Drop"] = null,
        });
        Assert.NotNull(headers);
        Assert.Single(headers);
        Assert.Equal("1", headers!["Keep"]);
    }

    [Fact]
    public void ExtractUserMessageText_JoinsTextBlocks()
    {
        Assert.Equal("hello", AgentSessionRuntime.ExtractUserMessageText("hello"));
        Assert.Equal(
            "ab",
            AgentSessionRuntime.ExtractUserMessageText([new TextContent("a"), new ImageContent("x", "image/png"), new TextContent("b")]));
        var json = JsonNode.Parse("""[{"type":"text","text":"hi"},{"type":"image"}]""");
        Assert.Equal("hi", AgentSessionRuntime.ExtractUserMessageText(json));
    }

    [Fact]
    public void UniqueJsonlDestinationPath_AddsNumericSuffix()
    {
        var destDir = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(destDir);
        var source = Path.Combine(_root, "chat.jsonl");
        File.WriteAllText(source, "{}");
        File.WriteAllText(Path.Combine(destDir, "chat.jsonl"), "{}");
        var unique = AgentSessionRuntime.UniqueJsonlDestinationPath(destDir, source);
        Assert.Equal(Path.Combine(destDir, "chat-1.jsonl"), unique);
    }

    [Fact]
    public void RequireImportSource_ThrowsTypedError()
    {
        var missing = Path.Combine(_root, "missing.jsonl");
        var ex = Assert.Throws<SessionImportFileNotFoundError>(() => AgentSessionRuntime.RequireImportSource(missing));
        Assert.Contains("File not found:", ex.Message);
    }
}
