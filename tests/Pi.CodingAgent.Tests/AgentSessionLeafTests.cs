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

    [Fact]
    public void GetLastAssistantText_SkipsAbortedEmptyAndJoinsText()
    {
        ChatMessage[] messages =
        [
            new AssistantMessage([new TextContent("old")], StopReason.Stop),
            new AssistantMessage([], StopReason.Aborted),
            new AssistantMessage([new TextContent("  new  "), new ToolCallContent("1", "bash", null)], StopReason.Stop),
        ];
        Assert.Equal("new", AgentSession.GetLastAssistantText(messages));
    }

    [Fact]
    public void GetLastAssistantText_ReturnsNullWhenBlank()
        => Assert.Null(AgentSession.GetLastAssistantText([new AssistantMessage([new TextContent("  ")])]));

    [Fact]
    public void NextCycleIndex_WrapsForwardAndBackward()
    {
        Assert.Equal(0, AgentSession.NextCycleIndex(2, 3, forward: true));
        Assert.Equal(2, AgentSession.NextCycleIndex(0, 3, forward: false));
        Assert.Equal(1, AgentSession.NextCycleIndex(-1, 3, forward: true));
    }

    [Fact]
    public void NextThinkingLevel_WrapsAndTreatsMissingAsFirst()
    {
        ThinkingLevel[] levels = [ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium];
        Assert.Equal(ThinkingLevel.Off, AgentSession.NextThinkingLevel(ThinkingLevel.Medium, levels));
        Assert.Equal(ThinkingLevel.Off, AgentSession.NextThinkingLevel(ThinkingLevel.High, levels));
        Assert.Null(AgentSession.NextThinkingLevel(ThinkingLevel.Off, []));
    }

    [Fact]
    public void ResolveForkTarget_AtKeepsEntryId()
    {
        var entry = new SessionMessageEntry
        {
            Id = "leaf",
            ParentId = "root",
            Message = new UserMessage(new MessageContent.Text("hi"), 1),
        };
        var target = AgentSessionRuntime.ResolveForkTarget(entry, "at");
        Assert.Equal("leaf", target.TargetLeafId);
        Assert.Null(target.SelectedText);
    }

    [Fact]
    public void ResolveForkTarget_BeforeUserExtractsTextAndParent()
    {
        var entry = new SessionMessageEntry
        {
            Id = "leaf",
            ParentId = "root",
            Message = new UserMessage(new MessageContent.Text("fork me"), 1),
        };
        var target = AgentSessionRuntime.ResolveForkTarget(entry);
        Assert.Equal("root", target.TargetLeafId);
        Assert.Equal("fork me", target.SelectedText);
    }

    [Fact]
    public void ResolveForkTarget_RejectsNonUserBefore()
    {
        var entry = new SessionMessageEntry
        {
            Id = "leaf",
            Message = new AssistantMessage([new TextContent("no")]),
        };
        var ex = Assert.Throws<InvalidOperationException>(() => AgentSessionRuntime.ResolveForkTarget(entry));
        Assert.Equal("Invalid entry ID for forking", ex.Message);
    }

    [Fact]
    public void RequirePersistedSessionFile_RejectsMissingPath()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentSessionRuntime.RequirePersistedSessionFile(null, persisted: true));
        Assert.Equal("Persisted session is missing a session file", ex.Message);
    }

    [Fact]
    public void RequireSavedSessionFile_RejectsUnsaved()
    {
        var missing = Path.Combine(_root, "unsaved.jsonl");
        var ex = Assert.Throws<InvalidOperationException>(() => AgentSessionRuntime.RequireSavedSessionFile(missing));
        Assert.Contains("has not been saved yet", ex.Message);
    }
}
