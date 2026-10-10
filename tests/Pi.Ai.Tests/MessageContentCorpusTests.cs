using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// Replays the differential corpus for the message-content union
/// (<c>string | (TextContent | ImageContent)[]</c>, ported as <see cref="MessageContent"/>).
/// Regenerate with
/// <c>node --experimental-strip-types tools/gen-ai-message-content-corpus.mjs &gt;
/// tests/Pi.Ai.Tests/message-content-corpus.json</c>.
/// </summary>
/// <remarks>
/// The union shows up in <c>SystemMessage.content</c>, <c>UserMessage.content</c>,
/// coding-agent's <c>CustomMessage.content</c> and the extension <c>sendUserMessage</c> API, so the
/// vectors cover the text renderers, the estimators, the image downgrade in <c>transformMessages</c>,
/// and the JSON wire shape (a system message's <c>content</c> is always present — <c>""</c> when
/// empty — because TS writes it from every construction site).
/// </remarks>
public class MessageContentCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    /// <summary>项目通行的 wire 约定：camelCase（各 provider 的解析端也按此读取）。</summary>
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "message-content-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static JsonElement[] Section(string name) => Corpus.GetProperty(name).EnumerateArray().ToArray();

    // -----------------------------------------------------------------------
    // Corpus → port readers
    // -----------------------------------------------------------------------

    private static MessageContent ReadContent(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => new MessageContent.Text(content.GetString()!),
        // TS's transformMessages normalises `content == null` to `[]`; the converter does the same.
        JsonValueKind.Null => new MessageContent.Blocks([]),
        JsonValueKind.Array => new MessageContent.Blocks(ReadBlocks(content)),
        _ => throw new InvalidOperationException($"Unsupported content token {content.ValueKind}."),
    };

    private static List<ContentBlock> ReadBlocks(JsonElement array)
    {
        var blocks = new List<ContentBlock>();
        foreach (var block in array.EnumerateArray())
        {
            blocks.Add(block.GetProperty("type").GetString() switch
            {
                "text" => new TextContent(block.GetProperty("text").GetString()!),
                "thinking" => new ThinkingContent(block.GetProperty("thinking").GetString()!),
                _ => new ImageContent(
                    block.GetProperty("data").GetString()!,
                    block.TryGetProperty("mimeType", out var mimeType) ? mimeType.GetString() : null),
            });
        }

        return blocks;
    }

    private static SystemMessage ReadSystemMessage(JsonElement message, bool nullContent = false)
    {
        IReadOnlyDictionary<string, string?>? sections = null;
        if (message.TryGetProperty("sections", out var sectionJson))
        {
            var map = new Dictionary<string, string?>();
            foreach (var property in sectionJson.EnumerateObject())
            {
                map[property.Name] = property.Value.ValueKind == JsonValueKind.Null
                    ? null
                    : property.Value.GetString();
            }

            sections = map;
        }

        IReadOnlyList<ToolDefinition>? toolsAdded = null;
        if (message.TryGetProperty("toolsAdded", out var toolsJson))
        {
            toolsAdded = toolsJson.EnumerateArray().Select(tool => new ToolDefinition(
                tool.GetProperty("name").GetString()!,
                tool.GetProperty("description").GetString()!,
                new ToolSchema(new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>(),
                }))).ToList();
        }

        return new SystemMessage(
            nullContent ? null : ReadContent(message.GetProperty("content")),
            sections,
            toolsAdded,
            null,
            message.GetProperty("timestamp").GetInt64());
    }

    private static ChatMessage ReadMessage(JsonElement message) => message.GetProperty("role").GetString() switch
    {
        "system" => ReadSystemMessage(message),
        "user" => new UserMessage(
            ReadContent(message.GetProperty("content")),
            message.GetProperty("timestamp").GetInt64()),
        var role => throw new InvalidOperationException($"Unsupported corpus role '{role}'."),
    };

    private static ModelSpec Model(JsonElement model) => new()
    {
        Id = model.GetProperty("id").GetString()!,
        Name = model.GetProperty("id").GetString()!,
        Api = model.GetProperty("api").GetString()!,
        Provider = model.GetProperty("provider").GetString()!,
        BaseUrl = "https://corpus.invalid/v1",
        Input = model.GetProperty("input").EnumerateArray().Select(i => i.GetString()!).ToList(),
    };

    private static JsonNode Serialize(ChatMessage message)
        => JsonNode.Parse(JsonSerializer.Serialize(message, typeof(ChatMessage), WireOptions))!;

    private static string Quote(string value) => value
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Corpus_IsComplete()
    {
        Assert.Equal(12, Section("contentText").Length);
        Assert.Equal(7, Section("systemText").Length);
        Assert.Equal(9, Section("estimate").Length);
        Assert.Equal(3, Section("systemEstimate").Length);
        Assert.Equal(4, Section("transform").Length);
        Assert.Equal(9, Section("wire").Length);
    }

    /// <summary>TS <c>contentText(content: string | readonly Content[], separator = "\n")</c>.</summary>
    [Fact]
    public void ContentText_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Section("contentText"))
        {
            var name = vector.GetProperty("name").GetString()!;
            var content = ReadContent(vector.GetProperty("content"));
            var separatorJson = vector.GetProperty("separator");
            var actual = separatorJson.ValueKind == JsonValueKind.Null
                ? Text.ContentText(content)
                : Text.ContentText(content, separatorJson.GetString()!);
            var expected = vector.GetProperty("text").GetString()!;
            if (expected != actual) failures.Add($"{name}: expected {Quote(expected)}, got {Quote(actual)}");
        }

        Assert.True(failures.Count == 0, "contentText mismatches:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>TS <c>getSystemMessageText</c> / <c>renderSystemMessageUpdate</c> over the union content.</summary>
    [Fact]
    public void SystemMessageText_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Section("systemText"))
        {
            var name = vector.GetProperty("name").GetString()!;
            var message = ReadSystemMessage(vector.GetProperty("message"));
            var expectedText = vector.GetProperty("text").GetString()!;
            var expectedUpdate = vector.GetProperty("update").GetString()!;

            var actualText = Text.GetSystemMessageText(message);
            if (expectedText != actualText)
                failures.Add($"{name}.text: expected {Quote(expectedText)}, got {Quote(actualText)}");

            var actualUpdate = Text.RenderSystemMessageUpdate(message);
            if (expectedUpdate != actualUpdate)
                failures.Add($"{name}.update: expected {Quote(expectedUpdate)}, got {Quote(actualUpdate)}");
        }

        Assert.True(failures.Count == 0, "system message text mismatches:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void EstimateTextAndImageContentTokens_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Section("estimate"))
        {
            var name = vector.GetProperty("name").GetString()!;
            var content = ReadContent(vector.GetProperty("content"));
            var expected = vector.GetProperty("tokens").GetInt64();
            var actual = Estimate.EstimateTextAndImageContentTokens(content);
            if (expected != actual) failures.Add($"{name}: expected {expected}, got {actual}");
        }

        Assert.True(failures.Count == 0,
            "estimateTextAndImageContentTokens mismatches:\n  " + string.Join("\n  ", failures));
    }

    /// <summary><c>estimateMessageTokens</c> for user messages (union content) and system messages.</summary>
    [Fact]
    public void EstimateMessageTokens_MatchesTypeScript()
    {
        var failures = new List<string>();

        foreach (var vector in Section("estimate"))
        {
            var name = vector.GetProperty("name").GetString()!;
            var message = new UserMessage(ReadContent(vector.GetProperty("content")), 1);
            var expected = vector.GetProperty("messageTokens").GetInt64();
            var actual = Estimate.EstimateMessageTokens(message);
            if (expected != actual) failures.Add($"user {name}: expected {expected}, got {actual}");
        }

        foreach (var vector in Section("systemEstimate"))
        {
            var name = vector.GetProperty("name").GetString()!;
            var message = ReadSystemMessage(vector.GetProperty("message"));
            var expected = vector.GetProperty("tokens").GetInt64();
            var actual = Estimate.EstimateMessageTokens(message);
            if (expected != actual) failures.Add($"system {name}: expected {expected}, got {actual}");
        }

        Assert.True(failures.Count == 0, "estimateMessageTokens mismatches:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// TS's <c>downgradeUnsupportedImages</c> only rewrites the array branch
    /// (<c>Array.isArray(msg.content)</c>); a bare string content passes through untouched.
    /// </summary>
    [Fact]
    public void TransformMessages_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Section("transform"))
        {
            var name = vector.GetProperty("name").GetString()!;
            var messages = vector.GetProperty("messages").EnumerateArray().Select(ReadMessage).ToList();
            var model = Model(vector.GetProperty("model"));
            var actual = JsonNode.Parse(JsonSerializer.Serialize(
                TransformMessages.Transform(messages, model),
                typeof(IReadOnlyList<ChatMessage>),
                WireOptions))!;
            var expected = JsonNode.Parse(vector.GetProperty("result").GetRawText())!;
            if (!JsonNode.DeepEquals(expected, actual))
                failures.Add($"{name}:\n    expected {expected}\n    actual   {actual}");
        }

        Assert.True(failures.Count == 0, "transformMessages mismatches:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// Session JSONL stores messages verbatim. <c>content</c> must round-trip in both directions and
    /// must always be present on a system message — TS writes <c>""</c> rather than omitting it
    /// (<c>createInitialSystemMessage</c> / <c>getCurrentSystemMessage</c> / <c>declareToolChanges</c>).
    /// </summary>
    [Fact]
    public void MessageContentJson_MatchesTypeScriptWireShape()
    {
        var failures = new List<string>();
        foreach (var vector in Section("wire"))
        {
            var name = vector.GetProperty("name").GetString()!;
            var raw = vector.GetProperty("message").GetRawText();
            var expectedContent = vector.TryGetProperty("readAsBlocks", out var asBlocks)
                ? asBlocks.GetRawText()
                // TS's transformMessages normalises `content == null` to `[]`; the port's converter
                // does the same on read, so the round-trip below must expect `[]` too.
                : vector.GetProperty("message").GetProperty("content").GetRawText();

            // 1. Serialising the port's message keeps `content` present and equal to the TS value.
            var nullContent = vector.TryGetProperty("constructNullContent", out _);
            var serialized = Serialize(nullContent
                ? ReadSystemMessage(vector.GetProperty("message"), nullContent: true)
                : ReadMessage(vector.GetProperty("message")));
            if (!serialized.AsObject().ContainsKey("content"))
            {
                failures.Add($"{name}: `content` was omitted");
                continue;
            }

            if (!JsonNode.DeepEquals(JsonNode.Parse(expectedContent), serialized["content"]))
            {
                failures.Add($"{name}: expected content {expectedContent}, got {serialized["content"]}");
            }

            // 2. Reading the TS-side JSON back yields the same content.
            var roundTripped = JsonSerializer.Deserialize<ChatMessage>(raw, WireOptions);
            if (roundTripped is null)
            {
                failures.Add($"{name}: could not be deserialized");
                continue;
            }

            var reSerialized = Serialize(roundTripped);
            if (!JsonNode.DeepEquals(serialized["content"], reSerialized["content"]))
            {
                failures.Add($"{name}: round-trip content {serialized["content"]} → {reSerialized["content"]}");
            }
        }

        Assert.True(failures.Count == 0, "message content wire mismatches:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>
    /// <see cref="MessageContent.ToBlocks"/> is the port's stand-in for the
    /// <c>typeof content === "string"</c> narrowing every TS provider performs: the text branch
    /// becomes a single <see cref="TextContent"/>, the array branch is returned as-is.
    /// </summary>
    [Fact]
    public void ToBlocks_FoldsTextBranchLikeProvidersDo()
    {
        var text = MessageContent.From("hi");
        var blocks = text.ToBlocks(out var wasText);
        Assert.True(wasText);
        Assert.Equal([new TextContent("hi")], blocks);

        var list = new List<ContentBlock> { new TextContent("a"), new ImageContent("QQ==", "image/png") };
        var passthrough = MessageContent.BlocksOf(list).ToBlocks(out var wasBlocks);
        Assert.False(wasBlocks);
        Assert.Same(list, passthrough);
    }

    /// <summary>
    /// <see cref="UserMessage"/>'s block-list constructor is sugar for
    /// <c>MessageContent.BlocksOf(content)</c> — both must produce the same message.
    /// </summary>
    [Fact]
    public void UserMessage_BlocksConstructor_MatchesUnionConstructor()
    {
        ContentBlock[] content = [new TextContent("hi"), new ImageContent("QQ==", "image/png")];
        var viaBlocks = new UserMessage(content, 7);
        var viaUnion = new UserMessage(MessageContent.BlocksOf(content), 7);
        Assert.Equal(viaUnion, viaBlocks);
        Assert.Equal(content, viaBlocks.Content.ToBlocks());
    }
}
