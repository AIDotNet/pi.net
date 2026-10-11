using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays the 4e-2c-1 slice (<c>core/session-manager.ts</c> types and pure tree/projection logic)
/// against <c>session-corpus.json</c>, which was captured by running the original TypeScript module in
/// Node.
/// </summary>
/// <remarks>
/// Covers <c>CURRENT_SESSION_VERSION</c>, <c>assertValidSessionId</c>, <c>parseSessionEntries</c>,
/// <c>migrateSessionEntries</c> (v1→v2→v3), <c>getLatestCompactionEntry</c>,
/// <c>sessionEntryToContextMessages</c>, <c>buildContextEntries</c>, <c>buildSessionProjection</c>,
/// <c>buildSessionContext</c> and <c>getDefaultSessionDir</c>. Every comparison goes through
/// <see cref="SessionEntries.JsonOptions"/>, so the vectors pin the JSON wire shape as well as the logic.
/// </remarks>
public class SessionCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "session-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static JsonSerializerOptions Wire => SessionEntries.JsonOptions;

    /// <summary>Serialize using the argument's static type, so polymorphic bases emit their discriminator.</summary>
    private static JsonNode Serialize<T>(T value)
        => JsonNode.Parse(JsonSerializer.Serialize(value, Wire))!;

    private static JsonNode Expected(JsonElement element) => JsonNode.Parse(element.GetRawText())!;

    private static IReadOnlyList<FileEntry> ReadFileEntries(JsonElement array)
        => JsonSerializer.Deserialize<List<FileEntry>>(array.GetRawText(), Wire)!;

    private static IReadOnlyList<SessionEntry> ReadEntries(JsonElement array)
        => JsonSerializer.Deserialize<List<SessionEntry>>(array.GetRawText(), Wire)!;

    private static SessionEntry ReadEntry(JsonElement element)
        => JsonSerializer.Deserialize<SessionEntry>(element.GetRawText(), Wire)!;

    /// <summary>
    /// Remap every distinct entry id to <c>id0</c>, <c>id1</c>, … by first appearance.
    /// </summary>
    /// <remarks>
    /// <c>migrateV1ToV2</c> mints random 8-hex ids, so the exact values are not comparable. Both sides
    /// assign ids in entry order, which makes this projection stable. Must stay in lock-step with
    /// <c>normalizeIds</c> in <c>tools/gen-coding-agent-session-corpus.mjs</c>.
    /// </remarks>
    private static JsonArray NormalizeIds(JsonArray entries)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var counter = 0;
        foreach (var node in entries)
        {
            if (node is not JsonObject obj || IsHeader(obj))
            {
                continue;
            }

            if (obj["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id) && !map.ContainsKey(id))
            {
                map[id] = $"id{counter++}";
            }
        }

        foreach (var node in entries)
        {
            if (node is not JsonObject obj || IsHeader(obj))
            {
                continue;
            }

            if (obj["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var id))
            {
                obj["id"] = map.GetValueOrDefault(id);
            }

            if (obj["parentId"] is JsonValue parentValue && parentValue.TryGetValue<string>(out var parentId))
            {
                obj["parentId"] = map.GetValueOrDefault(parentId, "<missing>");
            }

            if (obj["type"]?.GetValue<string>() == "compaction"
                && obj["firstKeptEntryId"] is JsonValue keptValue
                && keptValue.TryGetValue<string>(out var keptId))
            {
                obj["firstKeptEntryId"] = map.GetValueOrDefault(keptId, "<missing>");
            }
        }

        return entries;
    }

    private static bool IsHeader(JsonObject obj) => obj["type"]?.GetValue<string>() == "session";

    private static bool TryReadLeafId(JsonElement vector, out string? leafId)
    {
        if (vector.TryGetProperty("leafId", out var value))
        {
            leafId = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
            return true;
        }

        leafId = null;
        return false;
    }

    private static void Compare(JsonNode? expected, JsonNode? actual, string label, List<string> failures)
    {
        var same = expected is null && actual is null
            || expected is not null && actual is not null && JsonNode.DeepEquals(expected, actual);
        if (!same)
        {
            failures.Add($"{label}:\n  expected {expected?.ToJsonString() ?? "null"}\n  actual   {actual?.ToJsonString() ?? "null"}");
        }
    }

    // ------------------------------------------------------------------ corpus shape

    [Fact]
    public void Corpus_IsComplete()
    {
        Assert.Equal(3, Corpus.GetProperty("constants").GetProperty("CURRENT_SESSION_VERSION").GetInt32());
        Assert.Equal(19, Corpus.GetProperty("assertValidSessionId").GetArrayLength());
        Assert.Equal(11, Corpus.GetProperty("parseSessionEntries").GetArrayLength());
        Assert.Equal(9, Corpus.GetProperty("migrateSessionEntries").GetArrayLength());
        Assert.Equal(4, Corpus.GetProperty("getLatestCompactionEntry").GetArrayLength());
        Assert.Equal(23, Corpus.GetProperty("sessionEntryToContextMessages").GetArrayLength());
        Assert.Equal(28, Corpus.GetProperty("buildContextEntries").GetArrayLength());
        Assert.Equal(52, Corpus.GetProperty("buildSessionProjection").GetArrayLength());
        Assert.Equal(52, Corpus.GetProperty("buildSessionContext").GetArrayLength());
        Assert.Equal(3, Corpus.GetProperty("defaultSessionDir").GetArrayLength());
    }

    // ------------------------------------------------------------------ constants

    [Fact]
    public void CurrentSessionVersion_MatchesTypeScript()
    {
        var corpusVersion = Corpus.GetProperty("constants").GetProperty("CURRENT_SESSION_VERSION").GetInt32();
        Assert.Equal(SessionEntries.CurrentVersion, corpusVersion);
    }

    // ------------------------------------------------------------------ assertValidSessionId

    [Fact]
    public void AssertValidSessionId_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("assertValidSessionId").EnumerateArray())
        {
            var id = vector.GetProperty("id").GetString()!;
            var expectedValid = vector.GetProperty("valid").GetBoolean();
            var expectedMessage = vector.GetProperty("message").ValueKind == JsonValueKind.Null
                ? null
                : vector.GetProperty("message").GetString();

            var valid = true;
            string? message = null;
            try
            {
                SessionEntries.AssertValidSessionId(id);
            }
            catch (Exception error)
            {
                valid = false;
                message = error.Message;
            }

            if (valid != expectedValid || !string.Equals(message, expectedMessage, StringComparison.Ordinal))
            {
                failures.Add($"{JsonSerializer.Serialize(id)}: expected valid={expectedValid} msg={expectedMessage}"
                    + $", actual valid={valid} msg={message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    // ------------------------------------------------------------------ parseSessionEntries

    [Fact]
    public void ParseSessionEntries_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("parseSessionEntries").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var content = vector.GetProperty("content").GetString()!;
            var parsed = SessionEntries.ParseSessionEntries(content);
            Compare(
                Expected(vector.GetProperty("entries")),
                Serialize<IReadOnlyList<FileEntry>>(parsed),
                name,
                failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    // ------------------------------------------------------------------ migrateSessionEntries

    [Fact]
    public void MigrateSessionEntries_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("migrateSessionEntries").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var input = ReadFileEntries(vector.GetProperty("input"));
            var migrated = SessionEntries.MigrateSessionEntries(input);

            var actual = NormalizeIds(Serialize<IReadOnlyList<FileEntry>>(migrated).AsArray());
            var expected = NormalizeIds(Expected(vector.GetProperty("output")).AsArray());
            Compare(expected, actual, name, failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    [Fact]
    public void MigrateSessionEntries_IsIdempotent_ForCurrentVersion()
    {
        // v3 输入原样返回（TS `version >= CURRENT_SESSION_VERSION` 直接 return false）。
        var input = ReadFileEntries(
            Corpus.GetProperty("migrateSessionEntries").EnumerateArray()
                .Single(v => v.GetProperty("name").GetString() == "v3-is-idempotent")
                .GetProperty("input"));

        var migrated = SessionEntries.MigrateSessionEntries(input);
        Compare(
            Serialize<IReadOnlyList<FileEntry>>(input),
            Serialize<IReadOnlyList<FileEntry>>(migrated),
            "v3-is-idempotent",
            []);
    }

    // ------------------------------------------------------------------ getLatestCompactionEntry

    [Fact]
    public void GetLatestCompactionEntry_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("getLatestCompactionEntry").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var entries = ReadEntries(vector.GetProperty("entries"));
            var result = SessionEntries.GetLatestCompactionEntry(entries);
            var expected = vector.GetProperty("result");

            JsonNode? actual = result is null ? null : Serialize<SessionEntry>(result);
            JsonNode? expectedNode = expected.ValueKind == JsonValueKind.Null ? null : Expected(expected);
            Compare(expectedNode, actual, name, failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    // ------------------------------------------------------------------ sessionEntryToContextMessages

    [Fact]
    public void SessionEntryToContextMessages_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("sessionEntryToContextMessages").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var entry = ReadEntry(vector.GetProperty("entry"));
            var messages = SessionEntries.SessionEntryToContextMessages(entry);
            Compare(
                Expected(vector.GetProperty("messages")),
                Serialize<IReadOnlyList<ChatMessage>>(messages),
                name,
                failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    [Fact]
    public void SessionEntryToContextMessages_SystemNullContent_ProducesEmptyString()
    {
        // 线路层面观察不到这个分支（MessageContent 转换器把 null 也写成 ""），所以这里直接断言
        // 内存中的结果确实是 Text("")——即 TS 的 `{ ...message, content: "" }` 分支真的被走到。
        var entry = new SessionMessageEntry
        {
            Id = "e1",
            ParentId = null,
            Timestamp = "2026-01-01T00:00:01.000Z",
            Message = new SystemMessage(Content: null, Timestamp: 1),
        };

        var system = Assert.IsType<SystemMessage>(Assert.Single(SessionEntries.SessionEntryToContextMessages(entry)));
        var text = Assert.IsType<MessageContent.Text>(system.Content);
        Assert.Equal("", text.Value);
    }

    // ------------------------------------------------------------------ buildContextEntries

    [Fact]
    public void BuildContextEntries_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("buildContextEntries").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var entries = ReadEntries(vector.GetProperty("entries"));
            var specified = TryReadLeafId(vector, out var leafId);
            var result = specified
                ? SessionEntries.BuildContextEntries(entries, leafId)
                : SessionEntries.BuildContextEntries(entries);
            Compare(
                Expected(vector.GetProperty("result")),
                Serialize<IReadOnlyList<SessionEntry>>(result),
                name,
                failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    // ------------------------------------------------------------------ buildSessionProjection

    [Fact]
    public void BuildSessionProjection_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("buildSessionProjection").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var entries = ReadEntries(vector.GetProperty("entries"));
            var specified = TryReadLeafId(vector, out var leafId);
            var projection = specified
                ? SessionEntries.BuildSessionProjection(entries, leafId)
                : SessionEntries.BuildSessionProjection(entries);
            Compare(Expected(vector.GetProperty("projection")), Serialize(projection), name, failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    [Fact]
    public void BuildSessionProjection_MessagesFlattenEntries()
    {
        // TS 的 `projectedEntries.flatMap(entry => entry.messages)`：messages 是逐条目消息的拼接。
        var vector = Corpus.GetProperty("buildSessionProjection").EnumerateArray()
            .Single(v => v.GetProperty("name").GetString() == "compaction/default");
        var projection = SessionEntries.BuildSessionProjection(ReadEntries(vector.GetProperty("entries")));

        var flattened = projection.Entries.SelectMany(entry => entry.Messages).ToList();
        Assert.Equal(flattened.Count, projection.Messages.Count);
        for (var index = 0; index < flattened.Count; index++)
        {
            Assert.Equal(
                JsonSerializer.Serialize<ChatMessage>(flattened[index], Wire),
                JsonSerializer.Serialize<ChatMessage>(projection.Messages[index], Wire));
        }
    }

    // ------------------------------------------------------------------ buildSessionContext

    [Fact]
    public void BuildSessionContext_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("buildSessionContext").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var entries = ReadEntries(vector.GetProperty("entries"));
            var specified = TryReadLeafId(vector, out var leafId);
            var context = specified
                ? SessionEntries.BuildSessionContext(entries, leafId)
                : SessionEntries.BuildSessionContext(entries);
            Compare(Expected(vector.GetProperty("context")), Serialize(context), name, failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    // ------------------------------------------------------------------ getDefaultSessionDir

    [Fact]
    public void DefaultSessionDir_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("defaultSessionDir").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var cwd = vector.GetProperty("cwd").GetString()!;
            var agentDir = vector.GetProperty("agentDir").GetString()!;
            var expected = vector.GetProperty("path").GetString()!;
            var actual = SessionEntries.GetDefaultSessionDirPath(cwd, agentDir);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                failures.Add($"{name}:\n  expected {expected}\n  actual   {actual}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    // ------------------------------------------------------------------ wire shape

    [Fact]
    public void FileEntry_RoundTripsAllThirteenTypes()
    {
        var samples = new (FileEntry Entry, string Type)[]
        {
            (new SessionHeader { Id = "s", Timestamp = "t", Cwd = "/c", Version = 3 }, "session"),
            (new SessionMessageEntry { Id = "e", ParentId = null, Timestamp = "t", Message = new UserMessage([new TextContent("hi")], 1) }, "message"),
            (new ThinkingLevelChangeEntry { Id = "e", ParentId = null, Timestamp = "t", ThinkingLevel = "high" }, "thinking_level_change"),
            (new ModelChangeEntry { Id = "e", ParentId = null, Timestamp = "t", Provider = "p", ModelId = "m" }, "model_change"),
            (new UsageEntry { Id = "e", ParentId = null, Timestamp = "t", Kind = "k", Provider = "p", Model = "m", Usage = new Usage(1, 2) }, "usage"),
            (new CompactionEntry { Id = "e", ParentId = null, Timestamp = "t", Summary = "s", FirstKeptEntryId = "e", TokensBefore = 1 }, "compaction"),
            (new BranchSummaryEntry { Id = "e", ParentId = null, Timestamp = "t", FromId = "f", Summary = "s" }, "branch_summary"),
            (new CustomEntry { Id = "e", ParentId = null, Timestamp = "t", CustomType = "c" }, "custom"),
            (new CustomMessageEntry { Id = "e", ParentId = null, Timestamp = "t", CustomType = "c", Content = new MessageContent.Text("hi"), Display = true }, "custom_message"),
            (new ContextEditEntry { Id = "e", ParentId = null, Timestamp = "t", TargetId = "x", Replacement = new ContextEditReplacement { Content = JsonValue.Create("r") } }, "context_edit"),
            (new LabelEntry { Id = "e", ParentId = null, Timestamp = "t", TargetId = "x", Label = "l" }, "label"),
            (new SessionInfoEntry { Id = "e", ParentId = null, Timestamp = "t", Name = "n" }, "session_info"),
        };

        foreach (var (entry, type) in samples)
        {
            var json = JsonSerializer.Serialize<FileEntry>(entry, Wire);
            Assert.Equal(type, JsonNode.Parse(json)!["type"]!.GetValue<string>());

            var roundTripped = JsonSerializer.Deserialize<FileEntry>(json, Wire);
            Assert.NotNull(roundTripped);
            Assert.Equal(entry.GetType(), roundTripped!.GetType());
            Assert.Equal(json, JsonSerializer.Serialize<FileEntry>(roundTripped, Wire));
        }
    }

    [Fact]
    public void SessionEntry_RootWritesNullParentId()
    {
        // TS 的 SessionEntryBase.parentId 恒写出（根条目为 null），不加 WhenWritingNull。
        var entry = new SessionInfoEntry { Id = "e", ParentId = null, Timestamp = "t", Name = "n" };
        var json = JsonNode.Parse(JsonSerializer.Serialize<SessionEntry>(entry, Wire))!.AsObject();
        Assert.True(json.ContainsKey("parentId"));
        // JsonNode 把 JSON null 建模成 C# null，故键存在但取值为 null。
        Assert.Null(json["parentId"]);
    }

    [Fact]
    public void CompactionEntry_SystemMessage_KeepsRoleDiscriminator()
    {
        // TS 的 systemMessage 是普通 SystemMessage（自带 role 字段）；端口声明为 ChatMessage 基类，
        // 以确保 STJ 写出 role 判别符。
        var entry = new CompactionEntry
        {
            Id = "c",
            ParentId = null,
            Timestamp = "t",
            Summary = "s",
            FirstKeptEntryId = "c",
            TokensBefore = 1,
            SystemMessage = new SystemMessage("checkpoint", Timestamp: 1700000000000),
        };

        var json = JsonNode.Parse(JsonSerializer.Serialize<SessionEntry>(entry, Wire))!.AsObject();
        var systemMessage = json["systemMessage"]!.AsObject();
        Assert.Equal("system", systemMessage["role"]!.GetValue<string>());
        Assert.Equal("checkpoint", systemMessage["content"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ documented divergences

    [Fact]
    public void NullContent_IsNormalizedToEmptyBlocks_DivergesAsDocumented()
    {
        // Pi.Ai 的 MessageContent 转换器把显式 `"content": null` 归一化为 `[]`（4e-2c-0 对齐
        // pi-ai 的 transformMessages）。TS 的 parseSessionEntries 不做任何归一化，保留原始 null。
        var vector = Corpus.GetProperty("divergence").GetProperty("nullContentUserParse");

        // TS 侧确实保留了 null。
        using (var document = JsonDocument.Parse(
            vector.GetProperty("tsEntries")[0].GetProperty("message").GetRawText()))
        {
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("content").ValueKind);
        }

        var parsed = SessionEntries.ParseSessionEntries(vector.GetProperty("content").GetString()!);
        var message = Assert.IsType<UserMessage>(Assert.IsType<SessionMessageEntry>(parsed[0]).Message);
        var blocks = Assert.IsType<MessageContent.Blocks>(message.Content);
        Assert.Empty(blocks.Value);
    }

    [Fact]
    public void SystemNullContent_BecomesEmptyBlocks_DivergesAsDocumented()
    {
        // TS 的 sessionEntryToContextMessages 对 system 的 content == null 产出 `""`；端口因为
        // content 已被归一化成空块数组，走了原样透传分支，产出 `[]`。
        var vector = Corpus.GetProperty("divergence").GetProperty("systemNullContentContext");

        using (var document = JsonDocument.Parse(vector.GetProperty("tsMessages").GetRawText()))
        {
            Assert.Equal("", document.RootElement[0].GetProperty("content").GetString());
        }

        var entry = ReadEntry(vector.GetProperty("entry"));
        var messages = SessionEntries.SessionEntryToContextMessages(entry);
        var system = Assert.IsType<SystemMessage>(Assert.Single(messages));
        var blocks = Assert.IsType<MessageContent.Blocks>(system.Content);
        Assert.Empty(blocks.Value);
    }

    [Fact]
    public void UnknownEntryType_IsSkipped_DivergesAsDocumented()
    {
        // TS 把「合法 JSON 但 type 未知」的行保留成普通对象；端口的判别符没有对应派生类型，整行跳过。
        var vector = Corpus.GetProperty("divergence").GetProperty("unknownTypeParse");
        Assert.Equal(2, vector.GetProperty("tsEntries").GetArrayLength());

        var parsed = SessionEntries.ParseSessionEntries(vector.GetProperty("content").GetString()!);
        Assert.Single(parsed);
        Assert.IsType<SessionHeader>(parsed[0]);
    }
}
