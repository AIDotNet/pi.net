using System.Text.Json.Nodes;
using Pi.CodingAgent.Extensions.Codemode;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 1.5.1 #11803（codemode 半部）：output schema 里的本地 <c>$ref</c>（把模型放在 <c>$defs</c>
/// 的 MCP output schema）也要能被 codemode 摘要出字段名，而不是只剩 <c>unknown</c>。
/// schema 逐字对照上游 <c>agent-session-codemode.test.ts</c>「summarizes output fields behind local
/// schema references」，但直接驱动 <c>DescribeOutput</c> 测试缝，避免整套 harness。
/// </summary>
public class CodemodeSchemaRefTests
{
    [Fact]
    public void DescribeOutput_ResolvesObjectFieldsBehindLocalRefs()
    {
        var schema = JsonNode.Parse("""
        {
          "$ref": "#/$defs/Report",
          "$defs": {
            "Report": {
              "type": "object",
              "properties": { "kind": { "$ref": "#/$defs/Kind" }, "summary": { "$ref": "#/$defs/Summary" } },
              "required": ["kind", "summary"]
            },
            "Kind": { "const": "ledger" },
            "Summary": { "type": "object", "properties": { "total": { "type": "number" } }, "required": ["total"] }
          }
        }
        """);
        var result = CodemodeToolApi.DescribeOutput("report", schema);
        Assert.Equal("`{ kind: \"ledger\", summary: { total } }`", result);
    }

    [Fact]
    public void DescribeOutput_ResolvesUnionVariantsBehindLocalRefs()
    {
        var schema = JsonNode.Parse("""
        {
          "anyOf": [{ "$ref": "#/$defs/Ok" }, { "$ref": "#/$defs/Failed" }],
          "$defs": {
            "Ok": { "type": "object", "properties": { "value": { "type": "string" } }, "required": ["value"] },
            "Failed": { "type": "object", "properties": { "error": { "type": "string" } }, "required": ["error"] }
          }
        }
        """);
        var result = CodemodeToolApi.DescribeOutput("result", schema);
        Assert.Equal("`{ value } | { error }`", result);
    }
}
