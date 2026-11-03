using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Utils.Schema;
using Xunit;

namespace Pi.Ai.Tests.Utils;

/// <summary>
/// P0：pi-schema 校验/转换/错误文案与上游 TS 的差分测试。
/// 语料由 <c>tools/schema-oracle/gen.mjs</c> 跑上游 <c>packages/schema</c> 生成（同一 schema、同一 value，
/// 记录 <c>parse()</c> 的 <c>{ ok, value }</c> 或 <c>errors</c>）；这里逐条比对，确保 coerce 三遍管线、
/// 错误路径与文案和 1.5.0 完全一致。
/// </summary>
public class SchemaValidationCorpusTests
{
    private sealed record Vector(string Name, JsonObject Schema, JsonNode? Value, bool Ok, JsonNode? Coerced,
        IReadOnlyList<SchemaValidationError> Errors);

    private static readonly Lazy<IReadOnlyList<Vector>> Corpus = new(Load);
    private static readonly Lazy<Dictionary<string, Vector>> ByName = new(
        () => Corpus.Value.ToDictionary(vector => vector.Name, StringComparer.Ordinal));

    private static IReadOnlyList<Vector> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "schema-validation-corpus.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var vectors = new List<Vector>();
        foreach (var entry in root["vectors"]!.AsArray())
        {
            var record = entry!.AsObject();
            var errors = new List<SchemaValidationError>();
            if (record["errors"] is JsonArray list)
            {
                foreach (var error in list)
                {
                    var item = error!.AsObject();
                    errors.Add(new SchemaValidationError(
                        item["path"]!.GetValue<string>(),
                        item["keyword"]!.GetValue<string>(),
                        item["message"]!.GetValue<string>()));
                }
            }
            vectors.Add(new Vector(
                record["name"]!.GetValue<string>(),
                record["schema"]!.AsObject(),
                record["value"]?.DeepClone(),
                record["ok"]!.GetValue<bool>(),
                record["coerced"]?.DeepClone(),
                errors));
        }
        return vectors;
    }

    public static IEnumerable<object[]> VectorNames() => Corpus.Value.Select(vector => new object[] { vector.Name });

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void MatchesTheReferenceImplementation(string name)
    {
        var vector = ByName.Value[name];
        var result = ToolSchemaValidator.Compile(vector.Schema).Parse(vector.Value?.DeepClone());

        Assert.Equal(vector.Ok, result.Ok);
        if (vector.Ok)
        {
            Assert.True(JsonEquals(vector.Coerced, result.Value),
                $"{name}: expected {vector.Coerced?.ToJsonString() ?? "null"}, got {result.Value?.ToJsonString() ?? "null"}");
            Assert.Empty(result.Errors);
            return;
        }

        var expected = vector.Errors.Select(error => $"{error.Path}|{error.Keyword}|{error.Message}").ToList();
        var actual = result.Errors.Select(error => $"{error.Path}|{error.Keyword}|{error.Message}").ToList();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CorpusCoversBothOutcomesAndEveryMessageShape()
    {
        // 守卫：语料被截断或生成器失效时立刻失败，而不是静默变绿。
        Assert.True(Corpus.Value.Count >= 240, $"corpus has only {Corpus.Value.Count} vectors");
        Assert.True(Corpus.Value.Count(vector => vector.Ok) >= 140);
        Assert.True(Corpus.Value.Count(vector => !vector.Ok) >= 100);

        var keywords = Corpus.Value.SelectMany(vector => vector.Errors).Select(error => error.Keyword).ToHashSet();
        foreach (var keyword in new[]
                 {
                     "type", "enum", "const", "required", "additionalProperties", "propertyNames", "minProperties",
                     "maxProperties", "dependentRequired", "minLength", "maxLength", "pattern", "format", "minimum",
                     "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minItems", "maxItems",
                     "uniqueItems", "contains", "not", "oneOf", "$ref", "false",
                 })
        {
            Assert.Contains(keyword, keywords);
        }
    }

    /// <summary>结构相等：数字按数值比较（两侧的 JSON 数字渲染不同），其余逐项比对。</summary>
    private static bool JsonEquals(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is JsonObject leftObject)
        {
            if (right is not JsonObject rightObject || leftObject.Count != rightObject.Count) return false;
            foreach (var (key, value) in leftObject)
            {
                if (!rightObject.TryGetPropertyValue(key, out var other) || !JsonEquals(value, other)) return false;
            }
            return true;
        }
        if (left is JsonArray leftArray)
        {
            if (right is not JsonArray rightArray || leftArray.Count != rightArray.Count) return false;
            for (var index = 0; index < leftArray.Count; index++)
            {
                if (!JsonEquals(leftArray[index], rightArray[index])) return false;
            }
            return true;
        }
        var leftKind = left.GetValueKind();
        var rightKind = right.GetValueKind();
        if (leftKind == JsonValueKind.Number && rightKind == JsonValueKind.Number)
        {
            return left.GetValue<double>() == right.GetValue<double>();
        }
        return leftKind == rightKind && leftKind switch
        {
            JsonValueKind.String => left.GetValue<string>() == right.GetValue<string>(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false,
        };
    }
}
