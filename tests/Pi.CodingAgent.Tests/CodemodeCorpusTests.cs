using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Extensions.Codemode;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using ToolNamespace = Pi.CodingAgent.Core.Extensions.ToolNamespace;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the codemode extension (port of
/// <c>extensions/codemode/{tool.ts}</c> plus <c>core/usage-totals.ts</c> and the exported
/// <c>readCodemodeStore</c> from <c>execute.ts</c>, batch 4d-5). Replays
/// <c>codemode-corpus.json</c>, which the real TS sources produced under
/// <c>--experimental-strip-types</c> (see <c>tools/gen-coding-agent-codemode-corpus.mjs</c>): the
/// schema and constants, the schema-identity guard, script declarations, the model-facing
/// description with its catalog budget, the loadout preparation, the store reader, and the usage
/// totals (combine / accumulate / cost breakdown). The <c>executeCodemode</c> orchestration itself
/// is covered by the scripted-engine integration tests.
/// </summary>
public class CodemodeCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "codemode-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The TS generator stubs <c>config.ts</c>'s <c>getDocsPath</c> to a synthetic prefix, and
    /// <c>join</c> emits the host separator, so the docs path is normalized to a token on both
    /// sides; the C# raw-string literals keep their CRLF line endings where the TS emits LF, so
    /// newlines are unified too (the <c>PromptCorpusTests</c> convention).
    /// </summary>
    private static string Normalize(string text) => text
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace(CodemodeToolApi.CodemodeDocsPath, "<DOCS_PATH>")
        .Replace(Corpus.GetProperty("constants").GetProperty("docsPath").GetString()!, "<DOCS_PATH>");

    // ------------------------------------------------------------------ constants

    [Fact]
    public void Constants_MatchTypeScript()
    {
        var constants = Corpus.GetProperty("constants");
        Assert.Equal(CodemodeToolApi.CodemodeToolName, constants.GetProperty("codemodeToolName").GetString());
        Assert.Equal(CodemodeToolApi.CodemodeStoreEntryType, constants.GetProperty("codemodeStoreEntryType").GetString());
        Assert.Equal(CodemodeToolApi.DefaultCodemodeInlineBudget, constants.GetProperty("defaultInlineBudget").GetDouble());

        // The docs path ends with the file name and the join separator matches the host's
        // (the corpus pins the synthetic prefix, the port the real one; see NormalizeDocsPath).
        var expectedDocsPath = constants.GetProperty("docsPath").GetString()!;
        Assert.EndsWith("codemode.md", expectedDocsPath, StringComparison.Ordinal);
        Assert.EndsWith(Path.GetFileName(expectedDocsPath), CodemodeToolApi.CodemodeDocsPath, StringComparison.Ordinal);
        Assert.Equal(
            Path.DirectorySeparatorChar,
            expectedDocsPath[expectedDocsPath.Length - "codemode.md".Length - 1]);

        var systemPrompt = constants.GetProperty("systemPrompt");
        Assert.Equal(CodemodeToolApi.PromptSnippet, systemPrompt.GetProperty("snippet").GetString());
        Assert.Equal(
            systemPrompt.GetProperty("guidelines").EnumerateArray().Select(g => g.GetString()!).ToList(),
            CodemodeToolApi.PromptGuidelines);
    }

    // ------------------------------------------------------------------ schema

    [Fact]
    public void Schema_MatchesTypeScript()
    {
        var expectedSchema = JsonNode.Parse(Corpus.GetProperty("schema").GetRawText());
        var definition = CodemodeToolApi.CreateCodemodeToolDefinition();
        var actualSchema = JsonNode.Parse(JsonSerializer.Serialize(definition.Parameters.JsonSchema));
        Assert.True(JsonNode.DeepEquals(expectedSchema, actualSchema), $"schema mismatch: {actualSchema?.ToJsonString()}");
    }

    // ------------------------------------------------------------------ isCodemodeTool

    [Fact]
    public void IsCodemodeTool_UsesSchemaIdentity()
    {
        // The port compares the parameter schema by reference (C113's identity device), so the
        // corpus's matching-name case is replayed with the definition's own schema instance.
        var definition = CodemodeToolApi.CreateCodemodeToolDefinition();
        var vectors = Corpus.GetProperty("isCodemodeTool").EnumerateArray().ToList();

        var matching = ReadToolInfo(vectors[0], definition.Parameters);
        Assert.True(vectors[0].GetProperty("expected").GetBoolean());
        Assert.True(CodemodeToolApi.IsCodemodeTool(matching));

        var nameOnly = ReadToolInfo(vectors[1], new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }));
        Assert.False(vectors[1].GetProperty("expected").GetBoolean());
        Assert.False(CodemodeToolApi.IsCodemodeTool(nameOnly));

        var otherName = ReadToolInfo(vectors[2], definition.Parameters);
        Assert.False(vectors[2].GetProperty("expected").GetBoolean());
        Assert.False(CodemodeToolApi.IsCodemodeTool(otherName));
    }

    // ------------------------------------------------------------------ toCodemodeDeclaration

    public static IEnumerable<object[]> DeclarationKeys() =>
        Corpus.GetProperty("declaration").EnumerateArray()
            .Select(vector => new object[] { vector.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(DeclarationKeys))]
    public void ToCodemodeDeclaration_MatchesTypeScript(string label)
    {
        var vector = FindByProperty(Corpus.GetProperty("declaration"), "name", label);
        AssertDeclaration(vector.GetProperty("expected"), ToDeclaration(vector.GetProperty("input")));
    }

    [Fact]
    public void ToCodemodeDeclaration_NoGuidelines_MatchesTypeScript()
    {
        var vector = Corpus.GetProperty("declarationNoGuidelines");
        AssertDeclaration(vector.GetProperty("expected"), ToDeclaration(vector.GetProperty("input")));
    }

    private static CodemodeDeclaration ToDeclaration(JsonElement input)
    {
        var tool = ReadAgentTool(input.GetProperty("tool"));
        var guidelines = input.GetProperty("guidelines");
        return CodemodeToolApi.ToCodemodeDeclaration(
            tool,
            guidelines.ValueKind == JsonValueKind.Null
                ? null
                : guidelines.EnumerateArray().Select(g => g.GetString()!).ToList());
    }

    private static void AssertDeclaration(JsonElement expected, CodemodeDeclaration actual)
    {
        Assert.Equal(expected.GetProperty("name").GetString(), actual.Name);
        Assert.Equal(expected.GetProperty("description").GetString(), actual.Description);
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(expected.GetProperty("inputSchema").GetRawText()),
                actual.InputSchema),
            $"inputSchema mismatch: {actual.InputSchema?.ToJsonString()}");
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(expected.GetProperty("outputSchema").GetRawText()),
                actual.OutputSchema),
            $"outputSchema mismatch: {actual.OutputSchema?.ToJsonString()}");
    }

    // ------------------------------------------------------------------ callable tools

    [Fact]
    public void GetCodemodeCallableTools_MatchesTypeScript()
    {
        var vector = Corpus.GetProperty("callableTools");
        var tools = vector.GetProperty("input").EnumerateArray().Select(ReadAgentTool).ToList();
        Assert.Equal(
            vector.GetProperty("expected").EnumerateArray().Select(name => name.GetString()!).ToList(),
            CodemodeToolApi.GetCodemodeCallableTools(tools).Select(tool => tool.Name).ToList());
    }

    // ------------------------------------------------------------------ description

    public static IEnumerable<object[]> DescriptionKeys() =>
        Corpus.GetProperty("description").EnumerateArray()
            .Select(vector => new object[] { vector.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(DescriptionKeys))]
    public void CreateCodemodeDescription_MatchesTypeScript(string label)
    {
        var vector = FindByProperty(Corpus.GetProperty("description"), "name", label);
        var input = vector.GetProperty("input");
        var tools = input.GetProperty("tools").EnumerateArray().Select(ReadAgentTool).ToList();
        var options = ReadDescriptionOptions(input.GetProperty("options"));

        var actual = CodemodeToolApi.CreateCodemodeDescription(tools, options);

        Assert.Equal(
            Normalize(vector.GetProperty("expected").GetString()!),
            Normalize(actual));
    }

    private static CodemodeToolApi.CodemodeDescriptionOptions ReadDescriptionOptions(JsonElement options)
    {
        var result = new CodemodeToolApi.CodemodeDescriptionOptions();
        if (options.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.True)
        {
            result = result with { Models = true };
        }

        if (options.TryGetProperty("namespaces", out var namespaces) && namespaces.ValueKind == JsonValueKind.Object)
        {
            result = result with
            {
                Namespaces = namespaces.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => ReadNamespace(property.Value)),
            };
        }

        if (options.TryGetProperty("deferred", out var deferred) && deferred.ValueKind == JsonValueKind.Array)
        {
            result = result with
            {
                Deferred = new HashSet<string>(
                    deferred.EnumerateArray().Select(name => name.GetString()!),
                    StringComparer.Ordinal),
            };
        }

        if (options.TryGetProperty("guidelines", out var guidelines) && guidelines.ValueKind == JsonValueKind.Object)
        {
            result = result with
            {
                Guidelines = guidelines.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => (IReadOnlyList<string>)property.Value.EnumerateArray()
                        .Select(g => g.GetString()!).ToList()),
            };
        }

        if (options.TryGetProperty("inlineBudget", out var budget) && budget.ValueKind == JsonValueKind.Number)
        {
            result = result with { InlineBudget = budget.GetDouble() };
        }

        return result;
    }

    // ------------------------------------------------------------------ loadout

    public static IEnumerable<object[]> LoadoutKeys() =>
        Corpus.GetProperty("loadout").EnumerateArray()
            .Select(vector => new object[] { vector.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(LoadoutKeys))]
    public void CreateCodemodeToolDefinition_MatchesTypeScript(string label)
    {
        var vector = FindByProperty(Corpus.GetProperty("loadout"), "name", label);
        var input = vector.GetProperty("input");

        var definitionInput = input.GetProperty("definition");
        var options = new CodemodeToolApi.CodemodeToolOptions();
        if (definitionInput.GetProperty("models").GetBoolean())
        {
            options = options with { Models = true };
        }

        if (definitionInput.GetProperty("mode").ValueKind == JsonValueKind.String)
        {
            var mode = definitionInput.GetProperty("mode").GetString()!;
            options = options with { GetMode = () => mode };
        }

        if (definitionInput.GetProperty("hasGetInlineBudget").GetBoolean())
        {
            options = options with { GetInlineBudget = () => 60 };
        }

        var definition = CodemodeToolApi.CreateCodemodeToolDefinition(options);

        var expectedDefinition = vector.GetProperty("definition");
        Assert.Equal(expectedDefinition.GetProperty("name").GetString(), definition.Name);
        Assert.Equal(expectedDefinition.GetProperty("label").GetString(), definition.Label);
        Assert.Equal(expectedDefinition.GetProperty("exposure").GetString(), definition.Exposure);
        Assert.Equal(
            expectedDefinition.GetProperty("defaultActive").ValueKind == JsonValueKind.Null
                ? null
                : expectedDefinition.GetProperty("defaultActive").GetBoolean(),
            definition.DefaultActive);
        Assert.Equal(expectedDefinition.GetProperty("promptSnippet").GetString(), definition.PromptSnippet);
        Assert.Equal(
            expectedDefinition.GetProperty("promptGuidelines").EnumerateArray().Select(g => g.GetString()!).ToList(),
            definition.PromptGuidelines);
        Assert.Equal(
            Normalize(expectedDefinition.GetProperty("description").GetString()!),
            Normalize(definition.Description));
        Assert.True(
            JsonNode.DeepEquals(
                NormalizeJson(JsonNode.Parse(expectedDefinition.GetProperty("constrainedSampling").GetRawText())),
                NormalizeJson(definition.ConstrainedSampling)),
            $"constrainedSampling mismatch: {definition.ConstrainedSampling?.ToJsonString()}");

        var changes = definition.PrepareLoadout!(ReadLoadout(input.GetProperty("loadout")));
        var expectedChanges = vector.GetProperty("changes");
        var expectedDescriptions = expectedChanges.GetProperty("descriptions");
        Assert.NotNull(changes);
        Assert.Equal(expectedDescriptions.EnumerateObject().Count(), changes!.Descriptions!.Count);
        foreach (var property in expectedDescriptions.EnumerateObject())
        {
            Assert.True(
                changes.Descriptions.TryGetValue(property.Name, out var actualDescription),
                $"missing description for {property.Name}");
            Assert.Equal(
                Normalize(property.Value.GetString()!),
                Normalize(actualDescription));
        }

        var expectedHidden = expectedChanges.GetProperty("hiddenDeclarations");
        Assert.Equal(
            expectedHidden.EnumerateArray().Select(name => name.GetString()!).ToList(),
            changes.HiddenDeclarations);
    }

    private static IToolLoadout ReadLoadout(JsonElement loadout) => new FakeLoadout
    {
        Declared = ReadToolList(loadout.GetProperty("declared")),
        Callable = ReadToolList(loadout.GetProperty("callable")),
        Registered = ReadToolList(loadout.GetProperty("registered")),
        Exposures = ReadStringDictionary(loadout.GetProperty("exposures")),
        Namespaces = loadout.GetProperty("namespaces").EnumerateObject().ToDictionary(
            property => property.Name,
            property => ReadNamespace(property.Value)),
        Guidelines = loadout.GetProperty("guidelines").EnumerateObject().ToDictionary(
            property => property.Name,
            property => (IReadOnlyList<string>)property.Value.EnumerateArray()
                .Select(g => g.GetString()!).ToList()),
    };

    private sealed class FakeLoadout : IToolLoadout
    {
        public required IReadOnlyList<AgentTool> Declared { get; init; }

        public required IReadOnlyList<AgentTool> Callable { get; init; }

        public required IReadOnlyList<AgentTool> Registered { get; init; }

        public required IReadOnlyDictionary<string, string> Exposures { get; init; }

        public required IReadOnlyDictionary<string, ToolNamespace> Namespaces { get; init; }

        public required IReadOnlyDictionary<string, IReadOnlyList<string>> Guidelines { get; init; }

        // The TS loadout fixtures fall back to "direct" for unknown tools.
        public string GetExposure(string name) => Exposures.TryGetValue(name, out var exposure) ? exposure : ToolExposure.Direct;

        public ToolNamespace? GetNamespace(string name) => Namespaces.TryGetValue(name, out var ns) ? ns : null;

        public IReadOnlyList<string> GetPromptGuidelines(string name) =>
            Guidelines.TryGetValue(name, out var guidelines) ? guidelines : [];
    }

    // ------------------------------------------------------------------ readCodemodeStore

    [Fact]
    public void ReadCodemodeStore_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("readCodemodeStore").EnumerateArray())
        {
            var branch = ReadFileEntries(vector.GetProperty("branch"));
            var actual = CodemodeExecute.ReadCodemodeStore(branch);
            var expected = vector.GetProperty("expected");

            Assert.Equal(expected.EnumerateObject().Count(), actual.Count);
            foreach (var property in expected.EnumerateObject())
            {
                Assert.True(
                    actual.TryGetValue(property.Name, out var actualValue),
                    $"{vector.GetProperty("name").GetString()}: missing key {property.Name}");
                Assert.True(
                    JsonNode.DeepEquals(
                        JsonNode.Parse(property.Value.GetRawText()),
                        actualValue),
                    $"{vector.GetProperty("name").GetString()}: value mismatch for {property.Name}");
            }
        }
    }

    // ------------------------------------------------------------------ combineUsage

    [Fact]
    public void CombineUsage_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("combineUsage").EnumerateArray())
        {
            var label = vector.GetProperty("name").GetString();
            var first = JsonSerializer.Deserialize<Usage>(vector.GetProperty("first").GetRawText(), SessionEntries.JsonOptions)!;
            var second = JsonSerializer.Deserialize<Usage>(vector.GetProperty("second").GetRawText(), SessionEntries.JsonOptions)!;
            var actual = UsageTotalsApi.CombineUsage(first, second);
            var expected = vector.GetProperty("expected");

            Assert.Equal(expected.GetProperty("input").GetInt64(), actual.Input);
            Assert.Equal(expected.GetProperty("output").GetInt64(), actual.Output);
            Assert.Equal(expected.GetProperty("cacheRead").GetInt64(), actual.CacheRead);
            Assert.Equal(expected.GetProperty("cacheWrite").GetInt64(), actual.CacheWrite);
            AssertEqualNullableLong(label, "cacheWrite1h", TryGet(expected, "cacheWrite1h"), actual.CacheWrite1h);
            AssertEqualNullableLong(label, "reasoning", TryGet(expected, "reasoning"), actual.Reasoning);
            Assert.Equal(expected.GetProperty("totalTokens").GetInt64(), actual.TotalTokens);

            var expectedCost = expected.GetProperty("cost");
            Assert.True(
                Math.Abs(expectedCost.GetProperty("input").GetDouble() - actual.Cost.Input) < 1e-9
                && Math.Abs(expectedCost.GetProperty("output").GetDouble() - actual.Cost.Output) < 1e-9
                && Math.Abs(expectedCost.GetProperty("cacheRead").GetDouble() - actual.Cost.CacheRead) < 1e-9
                && Math.Abs(expectedCost.GetProperty("cacheWrite").GetDouble() - actual.Cost.CacheWrite) < 1e-9
                && Math.Abs(expectedCost.GetProperty("total").GetDouble() - actual.Cost.Total) < 1e-9,
                $"{label}: cost mismatch ({actual.Cost.Total})");
        }
    }

    // ------------------------------------------------------------------ addUsageToTotals

    [Fact]
    public void AddUsageToTotals_MatchesTypeScript()
    {
        var expected = Corpus.GetProperty("addUsageToTotals").GetProperty("expected");
        var totals = UsageTotalsApi.CreateUsageTotals();
        var usage = new Usage(
            expected.GetProperty("input").GetInt64() / 2,
            expected.GetProperty("output").GetInt64() / 2,
            expected.GetProperty("cacheRead").GetInt64() / 2,
            expected.GetProperty("cacheWrite").GetInt64() / 2)
        {
            TotalTokens = expected.GetProperty("input").GetInt64() / 2
                + expected.GetProperty("output").GetInt64() / 2
                + expected.GetProperty("cacheRead").GetInt64() / 2
                + expected.GetProperty("cacheWrite").GetInt64() / 2,
            Cost = new UsageCost(
                0.01, 0.02, 0.001, 0.002,
                expected.GetProperty("cost").GetDouble() / 2),
        };

        UsageTotalsApi.AddUsageToTotals(totals, usage);
        UsageTotalsApi.AddUsageToTotals(totals, usage);

        Assert.Equal(expected.GetProperty("input").GetInt64(), totals.Input);
        Assert.Equal(expected.GetProperty("output").GetInt64(), totals.Output);
        Assert.Equal(expected.GetProperty("cacheRead").GetInt64(), totals.CacheRead);
        Assert.Equal(expected.GetProperty("cacheWrite").GetInt64(), totals.CacheWrite);
        Assert.True(
            Math.Abs(expected.GetProperty("cost").GetDouble() - totals.Cost) < 1e-9,
            $"cost mismatch: {totals.Cost}");
    }

    // ------------------------------------------------------------------ usage cost breakdown

    [Fact]
    public void GetUsageCostBreakdown_MatchesTypeScript()
    {
        foreach (var vector in Corpus.GetProperty("usageCostBreakdown").EnumerateArray())
        {
            var label = vector.GetProperty("name").GetString();
            var entries = ReadFileEntries(vector.GetProperty("entries"));
            var actual = UsageTotalsApi.GetUsageCostBreakdown(entries);
            var expected = vector.GetProperty("expected").EnumerateArray().ToList();

            Assert.Equal(expected.Count, actual.Count);
            for (var index = 0; index < expected.Count; index++)
            {
                Assert.Equal(expected[index].GetProperty("key").GetString(), actual[index].Key);
                Assert.True(
                    Math.Abs(expected[index].GetProperty("cost").GetDouble() - actual[index].Cost) < 1e-9,
                    $"{label} #{index}: cost mismatch ({actual[index].Cost})");
                Assert.Equal(expected[index].GetProperty("tokens").GetInt64(), actual[index].Tokens);
            }
        }
    }

    // ------------------------------------------------------------------ guards

    [Fact]
    public void Corpus_IsComplete()
    {
        // Lower bounds, so a truncated fixture cannot silently turn the suite green.
        Assert.Equal(3, Corpus.GetProperty("isCodemodeTool").GetArrayLength());
        Assert.Equal(3, Corpus.GetProperty("declaration").GetArrayLength());
        Assert.Equal(10, Corpus.GetProperty("description").GetArrayLength());
        Assert.Equal(3, Corpus.GetProperty("loadout").GetArrayLength());
        Assert.Equal(4, Corpus.GetProperty("readCodemodeStore").GetArrayLength());
        Assert.Equal(3, Corpus.GetProperty("combineUsage").GetArrayLength());
        Assert.Equal(4, Corpus.GetProperty("usageCostBreakdown").GetArrayLength());

        // The budget cases must exercise truncation (zero and tiny budgets hide tools).
        Assert.Contains(Corpus.GetProperty("description").EnumerateArray(),
            vector => vector.GetProperty("name").GetString() == "budget-zero");
        Assert.Contains(Corpus.GetProperty("description").EnumerateArray(),
            vector => vector.GetProperty("name").GetString() == "budget-tiny");
    }

    // ------------------------------------------------------------------ helpers

    private static ToolInfo ReadToolInfo(JsonElement vector, ToolSchema parameters) => new()
    {
        Name = vector.GetProperty("name").GetString()!,
        Description = "test",
        Parameters = parameters,
        Exposure = ToolExposure.Direct,
        SourceInfo = SourceInfos.CreateSynthetic("builtin:test", "builtin"),
    };

    private static IReadOnlyList<AgentTool> ReadToolList(JsonElement array) =>
        array.EnumerateArray().Select(ReadAgentTool).ToList();

    private static AgentTool ReadAgentTool(JsonElement element) => new(
        element.GetProperty("name").GetString()!,
        element.GetProperty("description").GetString()!,
        new ToolSchema((Dictionary<string, object?>)ToPlain(element.GetProperty("parameters"))!),
        "test",
        (_, _, _, _) => Task.FromResult(new AgentToolResult([])))
    {
        OutputSchema = element.TryGetProperty("outputSchema", out var outputSchema)
                && outputSchema.ValueKind == JsonValueKind.Object
            ? new ToolSchema((Dictionary<string, object?>)ToPlain(outputSchema)!)
            : null,
    };

    private static ToolNamespace ReadNamespace(JsonElement element) => new()
    {
        Name = element.GetProperty("name").GetString()!,
        Description = element.TryGetProperty("description", out var description) ? description.GetString() : null,
        Instructions = element.TryGetProperty("instructions", out var instructions) ? instructions.GetString() : null,
    };

    private static IReadOnlyDictionary<string, string> ReadStringDictionary(JsonElement element) =>
        element.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!);

    private static IReadOnlyList<FileEntry> ReadFileEntries(JsonElement array) =>
        JsonSerializer.Deserialize<List<FileEntry>>(array.GetRawText(), SessionEntries.JsonOptions)!;

    private static void AssertEqualNullableLong(string? label, string name, JsonElement expected, long? actual)
    {
        // The key is absent when the TS fixture leaves the optional split undefined.
        var expectedValue = expected.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : (long?)expected.GetInt64();
        Assert.True(expectedValue == actual, $"{label}: {name} mismatch (expected {expectedValue}, got {actual})");
    }

    private static JsonElement TryGet(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : default;

    /// <summary>Replaces newlines in every string value, for CRLF raw-string literals (see Normalize).</summary>
    private static JsonNode? NormalizeJson(JsonNode? node) => node switch
    {
        JsonObject @object => new JsonObject([.. @object.Select(property => new KeyValuePair<string, JsonNode?>(property.Key, NormalizeJson(property.Value)))]),
        JsonArray array => new JsonArray([.. array.Select(NormalizeJson)]),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Normalize(text)),
        _ => node?.DeepClone(),
    };

    private static JsonElement FindByProperty(JsonElement array, string property, string value) =>
        array.EnumerateArray().First(vector => vector.GetProperty(property).GetString() == value);

    private static object? ToPlain(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => ToPlain(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ToPlain).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.GetDouble(),
        _ => null,
    };
}
