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

    [Fact]
    public void ResolveNewSessionKind_MapsPersistedFlag()
    {
        Assert.Equal(NewSessionKind.Persisted, AgentSessionRuntime.ResolveNewSessionKind(true));
        Assert.Equal(NewSessionKind.InMemory, AgentSessionRuntime.ResolveNewSessionKind(false));
    }

    [Fact]
    public void CopyImportedJsonl_CopiesUnlessAlreadyStored()
    {
        var source = Path.Combine(_root, "src.jsonl");
        var dest = Path.Combine(_root, "sessions", "src.jsonl");
        File.WriteAllText(source, "{\"id\":\"1\"}\n");
        AgentSessionRuntime.EnsureSessionDirectory(Path.GetDirectoryName(dest)!);
        AgentSessionRuntime.CopyImportedJsonl(source, dest, sourceAlreadyStored: false);
        Assert.Equal("{\"id\":\"1\"}\n", File.ReadAllText(dest));

        AgentSessionRuntime.CopyImportedJsonl(source, dest, sourceAlreadyStored: true);
        Assert.True(AgentSessionRuntime.IsSourceAlreadyStored(dest, dest));
        Assert.Throws<IOException>(() => AgentSessionRuntime.CopyImportedJsonl(source, dest, sourceAlreadyStored: false));
    }

    [Fact]
    public void ParseExtensionCommand_SplitsNameAndUntrimmedArgs()
    {
        Assert.Null(AgentSession.ParseExtensionCommand("plain"));
        var parsed = AgentSession.ParseExtensionCommand("/cmd  arg ");
        Assert.Equal("cmd", parsed!.Name);
        Assert.Equal(" arg ", parsed.Args);
    }

    [Fact]
    public void ExpandSkillCommand_UnknownPassesThroughAndKnownExpands()
    {
        Assert.Equal("/skill:missing", AgentSession.ExpandSkillCommand("/skill:missing", _ => null, _ => ""));

        var source = new SkillCommandSource("demo", Path.Combine(_root, "SKILL.md"), _root);
        File.WriteAllText(source.FilePath, "---\nname: demo\n---\nbody");
        var expanded = AgentSession.ExpandSkillCommand(
            "/skill:demo extra",
            name => name == "demo" ? source : null,
            File.ReadAllText);
        Assert.Contains("<skill name=\"demo\"", expanded);
        Assert.Contains($"References are relative to {_root}.", expanded);
        Assert.Contains("\n\nextra", expanded);
        Assert.Contains("body", expanded);
    }

    [Fact]
    public void ExpandSkillCommand_ReadFailureReturnsOriginal()
    {
        var source = new SkillCommandSource("demo", Path.Combine(_root, "missing.md"), _root);
        Assert.Equal(
            "/skill:demo",
            AgentSession.ExpandSkillCommand("/skill:demo", _ => source, File.ReadAllText));
    }

    [Fact]
    public void AppendPromptHints_JoinsWhenPresent()
    {
        Assert.Equal("hi", AgentSession.AppendPromptHints("hi", []));
        Assert.Equal("hi\n\na\nb", AgentSession.AppendPromptHints("hi", ["a", "b"]));
    }
}
