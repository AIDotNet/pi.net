// ============================================================================
// Compaction/skills/system-prompt differential corpus replay — 4e-2d
// ============================================================================
//
// Replays `compaction-corpus.json`, captured from the original TypeScript modules by
// `tools/gen-coding-agent-compaction-corpus.mjs` (see that file for the tokenization rules:
// "#N" uuids, "<millis>", "<tsN>", "<TMP>", "<DOCS*>" docs paths and "<str:len>" folds, with
// per-section keepLen exceptions that pin byte-exact prompt formatting).

using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

public class CompactionCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "compaction-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static JsonElement Section(string name) => Corpus.GetProperty(name).Clone();

    // ─── helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>{"__pad": {p, n, s?}} → the expanded string, recursively.</summary>
    private static JsonNode? ExpandPads(JsonNode? node)
    {
        if (node is JsonObject obj && obj["__pad"] is JsonObject pad)
        {
            var prefix = pad["p"]?.GetValue<string>() ?? "";
            var count = pad["n"]?.GetValue<int>() ?? 0;
            var text = prefix.Length > 0 ? string.Concat(Enumerable.Repeat(prefix, count)) : "";
            if (pad["s"] is { } suffix)
            {
                text += suffix.GetValue<string>();
            }
            return JsonValue.Create(text);
        }

        if (node is JsonObject jsonObject)
        {
            foreach (var key in jsonObject.Select(pair => pair.Key).ToList())
            {
                var expanded = ExpandPads(jsonObject[key]);
                if (!ReferenceEquals(expanded, jsonObject[key]))
                {
                    jsonObject[key] = expanded;
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var expanded = ExpandPads(array[i]);
                if (!ReferenceEquals(expanded, array[i]))
                {
                    array[i] = expanded;
                }
            }
        }

        return node;
    }

    private static ChatMessage ToMessage(JsonElement element) =>
        JsonSerializer.Deserialize<ChatMessage>(element.GetRawText(), AgentMessageJson.Options)!;

    private static SessionEntry ToEntry(JsonElement element) =>
        (SessionEntry)JsonSerializer.Deserialize<FileEntry>(element.GetRawText(), SessionEntries.JsonOptions)!;

    private static JsonNode ToNode(object? value, JsonSerializerOptions options) =>
        JsonSerializer.SerializeToNode(value, value?.GetType() ?? typeof(object), options)!;

    /// <summary>Drop the C#-only <c>cost</c> member of usage-shaped objects and normalize millis.</summary>
    private static JsonNode NormalizeForCompare(JsonNode node, bool normalizeTimestamps)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("input") && obj.ContainsKey("output") && obj.ContainsKey("cacheRead"))
            {
                obj.Remove("cost");
            }

            if (normalizeTimestamps
                && obj.TryGetPropertyValue("timestamp", out var timestamp)
                && timestamp is JsonValue
                && timestamp.GetValueKind() == JsonValueKind.Number
                && timestamp.GetValue<long>() != 0)
            {
                obj["timestamp"] = "<millis>";
            }

            foreach (var key in obj.Select(pair => pair.Key).ToList())
            {
                if (obj[key] is { } child)
                {
                    var normalized = NormalizeForCompare(child, normalizeTimestamps);
                    if (!ReferenceEquals(normalized, child))
                    {
                        obj[key] = normalized;
                    }
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is { } child)
                {
                    var normalized = NormalizeForCompare(child, normalizeTimestamps);
                    if (!ReferenceEquals(normalized, child))
                    {
                        array[i] = normalized;
                    }
                }
            }
        }

        return node;
    }

    /// <summary>JSON-ify a compat options dictionary (string/number/string[] values only).</summary>
    private static JsonNode DictToJsonNode(IReadOnlyDictionary<string, object?> dict)
    {
        var node = new JsonObject();
        foreach (var (key, value) in dict)
        {
            node[key] = value switch
            {
                null => null,
                string text => JsonValue.Create(text),
                int number => JsonValue.Create(number),
                long number => JsonValue.Create(number),
                IReadOnlyDictionary<string, string?> headers => new JsonObject(
                    headers.Select(pair => new KeyValuePair<string, JsonNode?>(
                        pair.Key, pair.Value is null ? null : JsonValue.Create(pair.Value)))),
                _ => JsonValue.Create(value.ToString()),
            };
        }

        return node;
    }

    private static List<ChatMessage> ToMessages(JsonElement element) =>
        element.EnumerateArray().Select(ToMessage).ToList();

    private static List<SessionEntry> ToEntries(JsonElement element) =>
        element.EnumerateArray().Select(ToEntry).ToList();

    private static List<string> ToStringList(JsonElement element) =>
        element.EnumerateArray().Select(e => e.GetString()!).ToList();

    private static IReadOnlyDictionary<string, string> ToStringMap(JsonElement element) =>
        element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string?> ToNullableStringMap(JsonElement element) =>
        element.EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString(),
            StringComparer.Ordinal);

    private static Skill ToSkill(JsonElement element)
    {
        var sourceInfo = element.GetProperty("sourceInfo");
        return new Skill
        {
            Name = element.GetProperty("name").GetString()!,
            Description = element.GetProperty("description").GetString()!,
            FilePath = element.GetProperty("filePath").GetString()!,
            BaseDir = element.GetProperty("baseDir").GetString()!,
            SourceInfo = SourceInfos.CreateSynthetic(
                sourceInfo.GetProperty("path").GetString()!,
                sourceInfo.GetProperty("source").GetString()!,
                scope: sourceInfo.TryGetProperty("scope", out var scope) ? scope.GetString() : null,
                origin: sourceInfo.TryGetProperty("origin", out var origin) ? origin.GetString() : null,
                baseDir: sourceInfo.TryGetProperty("baseDir", out var baseDir) ? baseDir.GetString() : null),
            DisableModelInvocation = element.TryGetProperty("disableModelInvocation", out var flag) && flag.GetBoolean(),
        };
    }

    private static void AssertJsonEquals(JsonNode? expected, JsonNode? actual, bool normalizeTimestamps = false)
    {
        var expectedNode = normalizeTimestamps ? NormalizeForCompare(expected!.DeepClone(), true) : expected;
        var actualNode = normalizeTimestamps ? NormalizeForCompare(actual!.DeepClone(), true) : actual;
        Assert.True(JsonNode.DeepEquals(expectedNode, actualNode),
            $"JSON mismatch.\nexpected: {expectedNode?.ToJsonString()}\nactual:   {actualNode?.ToJsonString()}");
    }

    // ─── calcContextTokens ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CalculateContextTokens_MatchesTs()
    {
        foreach (var vector in Section("calcContextTokens").EnumerateArray())
        {
            var usageElement = vector.GetProperty("usage");
            var usage = new Usage(
                usageElement.GetProperty("input").GetInt64(),
                usageElement.GetProperty("output").GetInt64(),
                usageElement.TryGetProperty("cacheRead", out var read) ? read.GetInt64() : 0,
                usageElement.TryGetProperty("cacheWrite", out var write) ? write.GetInt64() : 0)
            {
                TotalTokens = usageElement.TryGetProperty("totalTokens", out var total) ? total.GetInt64() : 0,
            };
            Assert.Equal(vector.GetProperty("out").GetInt64(), Compaction.CalculateContextTokens(usage));
        }
    }

    // ─── estimateTokens ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EstimateTokens_MatchesTs()
    {
        var index = 0;
        foreach (var vector in Section("estimateTokens").EnumerateArray())
        {
            var message = ToMessage(vector.GetProperty("message"));
            var expected = vector.GetProperty("out").GetInt64();
            Assert.True(expected == Compaction.EstimateTokens(message),
                $"estimateTokens[{index}] expected {expected} but got {Compaction.EstimateTokens(message)}");
            index++;
        }
    }

    // ─── shouldCompact ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShouldCompact_MatchesTs()
    {
        foreach (var vector in Section("shouldCompact").EnumerateArray())
        {
            var settingsElement = vector.GetProperty("settings");
            var settings = new CompactionSettings(
                settingsElement.GetProperty("enabled").GetBoolean(),
                settingsElement.GetProperty("reserveTokens").GetInt64(),
                settingsElement.TryGetProperty("keepRecentTokens", out var keep) ? keep.GetInt64() : 0);
            Assert.Equal(
                vector.GetProperty("out").GetBoolean(),
                Compaction.ShouldCompact(
                    vector.GetProperty("contextTokens").GetInt64(),
                    vector.GetProperty("contextWindow").GetInt64(),
                    settings));
        }
    }

    // ─── estimateContextTokens ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void EstimateContextTokens_MatchesTs()
    {
        foreach (var vector in Section("estimateContext").EnumerateArray())
        {
            var messages = ToMessages(vector.GetProperty("messages"));
            var estimate = Compaction.EstimateContextTokens(messages);
            var expected = vector.GetProperty("out");
            Assert.Equal(expected.GetProperty("tokens").GetInt64(), estimate.Tokens);
            Assert.Equal(expected.GetProperty("usageTokens").GetInt64(), estimate.UsageTokens);
            Assert.Equal(expected.GetProperty("trailingTokens").GetInt64(), estimate.TrailingTokens);
            var lastIndex = expected.TryGetProperty("lastUsageIndex", out var index)
                && index.ValueKind != JsonValueKind.Null
                ? index.GetInt32()
                : (int?)null;
            Assert.Equal(lastIndex, estimate.LastUsageIndex);
        }
    }

    // ─── serializeConversation ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void SerializeConversation_MatchesTs()
    {
        var vectors = Section("serializeConversation").EnumerateArray().ToList();
        var messages = ToMessages(vectors[1].GetProperty("messages"));
        Assert.Equal(vectors[1].GetProperty("out").GetString(), CompactionUtils.SerializeConversation(messages));
    }

    [Fact]
    public void SerializeConversation_OmitsThinking()
    {
        // 上游 15e7bdc14：回放的思维块常常不在模型活上下文里（与其请求不匹配的块会被丢掉），
        // 带上它会让摘要请求比它所摘要的上下文还大。可见文本、工具调用与工具结果已经记录了发生了什么。
        ChatMessage[] messages =
        [
            new UserMessage([new TextContent("check status")], 0),
            new AssistantMessage([
                new ThinkingContent("hidden reasoning"),
                new TextContent("running it"),
                new ToolCallContent("call", "bash", new JsonObject { ["command"] = "echo" }),
            ]),
            new ToolResultMessage("call", "bash", [new TextContent("ok")]),
        ];

        var serialized = CompactionUtils.SerializeConversation(messages);

        Assert.DoesNotContain("hidden reasoning", serialized);
        Assert.DoesNotContain("[Assistant thinking]", serialized);
        Assert.Contains("[Assistant]: running it", serialized);
        Assert.Contains("[Assistant tool calls]: bash(command=\"echo\")", serialized);
        Assert.Contains("[Tool result]: ok", serialized);
    }

    // ─── fileOps ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FileOps_MatchTs()
    {
        foreach (var vector in Section("fileOps").EnumerateArray())
        {
            var fileOps = CompactionUtils.CreateFileOps();
            foreach (var messageElement in vector.GetProperty("messages").EnumerateArray())
            {
                var message = ToMessage(JsonDocument.Parse(ExpandPads(messageElement.Deserialize<JsonNode>())!.ToJsonString()).RootElement);
                CompactionUtils.ExtractFileOpsFromMessage(message, fileOps);
            }

            var lists = vector.GetProperty("lists");
            var (readFiles, modifiedFiles) = CompactionUtils.ComputeFileLists(fileOps);
            Assert.Equal(ToStringList(lists.GetProperty("readFiles")), readFiles);
            Assert.Equal(ToStringList(lists.GetProperty("modifiedFiles")), modifiedFiles);

            if (vector.TryGetProperty("formatted", out var formatted))
            {
                Assert.Equal(formatted.GetString(), CompactionUtils.FormatFileOperations(readFiles, modifiedFiles));
            }
        }
    }

    // ─── getSummarizationFailure ───────────────────────────────────────────────────────────────────

    [Fact]
    public void GetSummarizationFailure_MatchesTs()
    {
        foreach (var vector in Section("summarizationFailure").EnumerateArray())
        {
            var response = (AssistantMessage)ToMessage(vector.GetProperty("response"));
            Assert.Equal(
                vector.TryGetProperty("out", out var failure) && failure.ValueKind != JsonValueKind.Null
                    ? failure.GetString()
                    : null,
                Compaction.GetSummarizationFailure(response, "Summarization"));
        }
    }

    // ─── cut points ────────────────────────────────────────────────────────────────────────────────

    private static void AssertCutPoint(JsonElement expected, CutPointResult actual)
    {
        Assert.Equal(expected.GetProperty("firstKeptEntryIndex").GetInt32(), actual.FirstKeptEntryIndex);
        Assert.Equal(expected.GetProperty("turnStartIndex").GetInt32(), actual.TurnStartIndex);
        Assert.Equal(expected.GetProperty("isSplitTurn").GetBoolean(), actual.IsSplitTurn);
    }

    [Fact]
    public void FindCutPoint_MatchesTs()
    {
        var entries = ToEntries(Section("findCutPointVectors").GetProperty("entries"));
        foreach (var vector in Section("findCutPoint").EnumerateArray())
        {
            AssertCutPoint(
                vector.GetProperty("out"),
                Compaction.FindCutPoint(entries, 0, entries.Count, vector.GetProperty("keepRecentTokens").GetInt64()));
        }

        foreach (var vector in Section("findTurnStartIndex").EnumerateArray())
        {
            Assert.Equal(
                vector.GetProperty("out").GetInt32(),
                Compaction.FindTurnStartIndex(entries, vector.GetProperty("entryIndex").GetInt32(), 0));
        }
    }

    [Fact]
    public void FindCutPoint_PostCompaction_MatchesTs()
    {
        var entries = ToEntries(Section("findCutPointPostCompactionVectors").GetProperty("entries"));
        foreach (var vector in Section("findCutPointPostCompaction").EnumerateArray())
        {
            AssertCutPoint(
                vector.GetProperty("out"),
                Compaction.FindCutPoint(entries, 0, entries.Count, vector.GetProperty("keepRecentTokens").GetInt64()));
        }
    }

    // ─── generateSummaryWithUsage ──────────────────────────────────────────────────────────────────

    private sealed class FakeStream : IAssistantMessageEventStream
    {
        private readonly AssistantMessage _response;

        public FakeStream(AssistantMessage response) => _response = response;

        public AssistantMessage? Partial => null;

        public Task<AssistantMessage> WaitForDoneAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_response);

        public IAsyncEnumerator<AssistantMessageEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return EmptyEnumerator(cancellationToken);
        }

        private static async IAsyncEnumerator<AssistantMessageEvent> EmptyEnumerator(CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static ModelSpec ToModelSpec(JsonElement element) => new()
    {
        Id = element.GetProperty("id").GetString()!,
        Name = element.GetProperty("id").GetString()!,
        Api = "test-api",
        Provider = "test-provider",
        BaseUrl = "http://localhost",
        Reasoning = element.GetProperty("reasoning").GetBoolean(),
        MaxTokens = element.GetProperty("maxTokens").GetInt64(),
        ContextWindow = element.GetProperty("contextWindow").GetInt64(),
    };

    [Fact]
    public async Task GenerateSummaryWithUsage_MatchesTs()
    {
        foreach (var vector in Section("generateSummary").EnumerateArray())
        {
            var messages = ToMessages(vector.GetProperty("messages"));
            var scenario = vector.GetProperty("scenario");
            var responseElement = scenario.GetProperty("response");
            var response = (AssistantMessage)ToMessage(responseElement);

            Model? capturedModel = null;
            TranscriptContext? capturedContext = null;
            SimpleStreamOptions? capturedOptions = null;
            StreamFn streamFn = (model, context, options, _) =>
            {
                capturedModel = model;
                capturedContext = context;
                capturedOptions = options;
                IAssistantMessageEventStream stream = new FakeStream(response);
                return Task.FromResult(stream);
            };

            var model = ToModelSpec(scenario.GetProperty("model"));
            Exception? thrown = null;
            (string Text, Usage Usage) result = default;
            try
            {
                result = await Compaction.GenerateSummaryWithUsageAsync(
                    messages,
                    model,
                    scenario.GetProperty("reserveTokens").GetInt64(),
                    apiKey: null,
                    headers: null,
                    signal: default,
                    customInstructions: scenario.TryGetProperty("customInstructions", out var custom)
                        ? custom.GetString()
                        : null,
                    previousSummary: scenario.TryGetProperty("previousSummary", out var previous)
                        ? previous.GetString()
                        : null,
                    thinkingLevel: null,
                    streamFn: streamFn,
                    env: null,
                    retry: null,
                    callbacks: null,
                    sessionId: scenario.TryGetProperty("sessionId", out var sessionId)
                        ? sessionId.GetString()
                        : null);
            }
            catch (Exception error)
            {
                thrown = error;
            }

            // The captured request context must match the TS byte-for-byte (system prompt constant,
            // prompt assembly, options routing).
            var capturedVector = vector.GetProperty("captured")[0];
            Assert.Equal(
                capturedVector.GetProperty("model").GetProperty("id").GetString(),
                capturedModel!.Id);
            Assert.Equal(
                capturedVector.GetProperty("model").GetProperty("reasoning").GetBoolean()
                    ? "true" : null,
                capturedModel.Reasoning);

            var contextNode = new JsonObject
            {
                ["messages"] = ToNode(capturedContext!.Messages, AgentMessageJson.Options),
            };
            AssertJsonEquals(
                ExpandPads(capturedVector.GetProperty("context").Deserialize<JsonNode>()),
                contextNode,
                normalizeTimestamps: true);

            var optionsNode = DictToJsonNode(CompatShims.ToDictionary(capturedOptions!));
            AssertJsonEquals(capturedVector.GetProperty("options").Deserialize<JsonNode>(), optionsNode);

            var errorText = vector.GetProperty("error");
            if (errorText.ValueKind != JsonValueKind.Null)
            {
                Assert.NotNull(thrown);
                Assert.Equal(errorText.GetString(), thrown?.Message);
            }
            else
            {
                Assert.Null(thrown);
                var (text, usageValue) = result;
                Assert.NotNull(usageValue);
                var resultVector = vector.GetProperty("result");
                Assert.Equal(resultVector.GetProperty("text").GetString(), text);
                var usage = resultVector.GetProperty("usage");
                Assert.Equal(usage.GetProperty("input").GetInt64(), usageValue.Input);
                Assert.Equal(usage.GetProperty("output").GetInt64(), usageValue.Output);
                Assert.Equal(usage.GetProperty("cacheRead").GetInt64(), usageValue.CacheRead);
                Assert.Equal(usage.GetProperty("cacheWrite").GetInt64(), usageValue.CacheWrite);
                Assert.Equal(usage.GetProperty("totalTokens").GetInt64(), usageValue.TotalTokens);
            }
        }
    }

    // ─── branch summarization ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void CollectEntriesForBranchSummary_MatchesTs()
    {
        var vectorsElement = Section("branchCollectVectors");
        var entries = ToEntries(vectorsElement.GetProperty("entries"));
        var session = SessionManager.InMemory(entries: [.. entries.Cast<FileEntry>()]);

        foreach (var vector in Section("branchCollect").EnumerateArray())
        {
            var oldLeaf = vector.GetProperty("oldLeafId");
            var result = BranchSummarization.CollectEntriesForBranchSummary(
                session,
                oldLeaf.ValueKind == JsonValueKind.Null ? null : oldLeaf.GetString(),
                vector.GetProperty("targetId").GetString()!);

            var expected = vector.GetProperty("out");
            Assert.Equal(
                expected.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList(),
                result.Entries.Select(entry => entry.Id).ToList());
            var ancestor = expected.GetProperty("commonAncestorId");
            Assert.Equal(
                ancestor.ValueKind == JsonValueKind.Null ? null : ancestor.GetString(),
                result.CommonAncestorId);
        }
    }

    [Fact]
    public void PrepareBranchEntries_MatchesTs()
    {
        foreach (var vector in Section("branchPrepare").EnumerateArray())
        {
            var entries = ToEntries(vector.GetProperty("entries"));
            var preparation = BranchSummarization.PrepareBranchEntries(
                entries, vector.GetProperty("tokenBudget").GetInt64());

            var expected = vector.GetProperty("out");
            Assert.Equal(
                expected.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()).ToList(),
                preparation.Messages.Select(message => RoleOf(message)).ToList());
            AssertJsonEquals(
                expected.GetProperty("messages").Deserialize<JsonNode>(),
                ToNode(preparation.Messages, AgentMessageJson.Options));

            var lists = expected.GetProperty("fileOpsLists");
            var (readFiles, modifiedFiles) = CompactionUtils.ComputeFileLists(preparation.FileOps);
            Assert.Equal(ToStringList(lists.GetProperty("readFiles")), readFiles);
            Assert.Equal(ToStringList(lists.GetProperty("modifiedFiles")), modifiedFiles);
            Assert.Equal(expected.GetProperty("totalTokens").GetInt64(), preparation.TotalTokens);
        }
    }

    private static string? RoleOf(ChatMessage message) => message switch
    {
        SystemMessage => "system",
        UserMessage => "user",
        AssistantMessage => "assistant",
        ToolResultMessage => "toolResult",
        BashExecutionMessage => "bashExecution",
        CustomMessage => "custom",
        BranchSummaryMessage => "branchSummary",
        CompactionSummaryMessage => "compactionSummary",
        _ => null,
    };

    // ─── formatSkillsForPrompt ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void FormatSkillsForPrompt_MatchesTs()
    {
        var section = Section("skillsFormatVectors");
        var skills = section.GetProperty("skills").EnumerateArray().Select(ToSkill).ToList();
        var vectors = Section("skillsFormat").EnumerateArray().ToList();

        Assert.Equal(5, vectors.Count);
        Assert.Equal(vectors[0].GetProperty("out").GetString(),
            Skills.FormatSkillsForPrompt(skills, SkillFileReadTool.Read));
        Assert.Equal(vectors[1].GetProperty("out").GetString(),
            Skills.FormatSkillsForPrompt(skills, SkillFileReadTool.Bash));
        Assert.Equal(vectors[2].GetProperty("out").GetString(),
            Skills.FormatSkillsForPrompt(skills, SkillFileReadTool.Indirect));
        Assert.Equal(vectors[3].GetProperty("out").GetString(),
            Skills.FormatSkillsForPrompt([skills[1]], SkillFileReadTool.Read));
        Assert.Equal(vectors[4].GetProperty("out").GetString(),
            Skills.FormatSkillsForPrompt([], SkillFileReadTool.Read));
    }
}
