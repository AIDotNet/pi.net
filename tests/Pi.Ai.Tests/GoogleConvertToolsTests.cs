using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// Gemini 工具声明里的 <c>const</c> 改写（上游 <c>7314819f0</c> + <c>a7b94a03f</c>，#11308）：
/// Gemini 在 <c>parametersJsonSchema</c> 里忽略 <c>const</c> 但认 <c>enum</c>，所以字符串/数字的
/// <c>const</c> 改写成单值 <c>enum</c>；改写只下钻含子 schema 的关键字，字面量数据原样保留。
/// </summary>
public class GoogleConvertToolsTests
{
    private static ToolDefinition Tool(string parametersJson) =>
        new("t", "d", new ToolSchema(
            JsonSerializer.Deserialize<Dictionary<string, object?>>(parametersJson)
            ?? throw new InvalidOperationException("bad parameters")));

    private static JsonNode? Converted(string parametersJson)
    {
        var declarations = GoogleShared.ConvertTools([Tool(parametersJson)]);
        return declarations![0]!["functionDeclarations"]![0]!["parametersJsonSchema"];
    }

    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual),
            $"expected {expected}, got {actual?.ToJsonString() ?? "null"}");

    [Fact]
    public void RewritesStringAndNumberConstAsSingleValueEnum()
    {
        AssertJson(
            """
            {"type":"object","properties":{
              "kind":{"enum":["a"]},
              "count":{"enum":[1]},
              "flag":{"const":true}
            }}
            """,
            Converted("""
                {"type":"object","properties":{
                  "kind":{"const":"a"},
                  "count":{"const":1},
                  "flag":{"const":true}
                }}
                """));
    }

    [Fact]
    public void KeepsAnExistingEnum()
    {
        AssertJson(
            """{"type":"object","properties":{"kind":{"const":"a","enum":["a","b"]}}}""",
            Converted("""{"type":"object","properties":{"kind":{"const":"a","enum":["a","b"]}}}"""));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("examples")]
    [InlineData("enum")]
    [InlineData("const")]
    [InlineData("x-custom")]
    public void PreservesLiteralDataUnderNonSchemaKeywords(string key)
    {
        // 关键字下的字面量数据不是 schema：里面的 const/properties 必须原样保留。
        const string literal = "{\"const\":\"literal-data\",\"properties\":{\"nested\":{\"const\":1}}}";
        var value = key is "enum" or "examples" ? "[" + literal + "]" : literal;
        var parameters = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"object\",\"" + key + "\":" + value + "}}}";
        AssertJson(parameters, Converted(parameters));
    }

    [Theory]
    [InlineData("properties")]
    [InlineData("patternProperties")]
    [InlineData("$defs")]
    [InlineData("definitions")]
    [InlineData("dependentSchemas")]
    [InlineData("dependencies")]
    public void PreservesNamesWhileRewritingSchemasInNameMaps(string key)
    {
        // 名字映射里的键是名字，值是 schema —— 包括叫 const / properties 的名字。
        var expected = "{\"type\":\"object\",\"" + key + "\":{\"__proto__\":{\"enum\":[\"x\"]},"
            + "\"const\":{\"enum\":[\"y\"]},\"properties\":{\"enum\":[\"z\"]}}}";
        var parameters = "{\"type\":\"object\",\"" + key + "\":{\"__proto__\":{\"const\":\"x\"},"
            + "\"const\":{\"const\":\"y\"},\"properties\":{\"const\":\"z\"}}}";
        AssertJson(expected, Converted(parameters));
    }

    [Fact]
    public void RewritesOnlySchemaBearingKeywords()
    {
        AssertJson(
            """
            {"type":"object",
             "additionalProperties":{"enum":["x"]},
             "unevaluatedProperties":false,
             "propertyNames":{"enum":["name"]},
             "allOf":[{"if":{"enum":["x"]},"then":{"enum":["y"]},"else":{"not":{"enum":["z"]}}}],
             "properties":{"list":{
               "type":"array",
               "items":[{"enum":[1]}],
               "prefixItems":[{"enum":[2]}],
               "additionalItems":{"enum":[3]},
               "contains":{"enum":[4]},
               "unevaluatedItems":{"enum":[5]}
             }},
             "dependencies":{"list":["other"]}}
            """,
            Converted("""
                {"type":"object",
                 "additionalProperties":{"const":"x"},
                 "unevaluatedProperties":false,
                 "propertyNames":{"const":"name"},
                 "allOf":[{"if":{"const":"x"},"then":{"const":"y"},"else":{"not":{"const":"z"}}}],
                 "properties":{"list":{
                   "type":"array",
                   "items":[{"const":1}],
                   "prefixItems":[{"const":2}],
                   "additionalItems":{"const":3},
                   "contains":{"const":4},
                   "unevaluatedItems":{"const":5}
                 }},
                 "dependencies":{"list":["other"]}}
                """));
    }

    [Fact]
    public void LeavesTheToolSchemaUntouched()
    {
        // 改写是纯函数：工具自己的 schema 不能被改。
        var tool = Tool("""{"type":"object","properties":{"kind":{"const":"a"}}}""");
        var before = JsonSerializer.Serialize(tool.Parameters.JsonSchema);
        _ = GoogleShared.ConvertTools([tool]);
        Assert.Equal(before, JsonSerializer.Serialize(tool.Parameters.JsonSchema));
    }
}
