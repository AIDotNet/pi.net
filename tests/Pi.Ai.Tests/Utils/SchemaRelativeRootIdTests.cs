using System.Text.Json.Nodes;
using Pi.Ai.Utils.Schema;
using Xunit;

namespace Pi.Ai.Tests.Utils;

/// <summary>
/// 上游 1.5.2 #11726（schema）：相对根 <c>$id</c> 在解析本地 <c>$ref</c> 时只能应用一次。
/// 端口 <c>ToolSchemaValidator</c> 构造时 eager <c>Warm()</c> 先建索引，<c>BaseFor</c> 因而返回
/// 已索引的 base（只应用一次）；本组用例镜像上游 validator.test.ts，钉住这个等价行为。
/// 注：上游同批用例里的 <c>unevaluatedProperties</c> 场景在端口是文档化的忽略关键字
/// （见 SchemaChecker 头部注释），不属于本次 #11726 的 $id 语义，故不纳入。
/// </summary>
public class SchemaRelativeRootIdTests
{
    private static JsonObject ToolSchema()
        => JsonNode.Parse("""
            {
              "$id": "schemas/tool.json",
              "type": "object",
              "properties": { "count": { "$ref": "#/$defs/count" } },
              "required": ["count"],
              "$defs": { "count": { "type": "integer" } }
            }
            """)!.AsObject();

    [Fact]
    public void AppliesARelativeRootIdOnceWhenResolvingLocalReferences()
    {
        var validator = ToolSchemaValidator.Compile(ToolSchema());

        Assert.True(validator.Check(JsonNode.Parse("""{"count":2}""")));
        var errors = validator.Errors(JsonNode.Parse("""{"count":"a"}"""));
        var error = Assert.Single(errors);
        Assert.Equal("/count", error.Path);
        Assert.Equal("type", error.Keyword);
        Assert.Equal("expected integer, got string", error.Message);
    }

    [Fact]
    public void CoercesAcrossARelativeRootId()
    {
        var result = ToolSchemaValidator.Compile(ToolSchema()).Parse(JsonNode.Parse("""{"count":"2"}"""));

        Assert.True(result.Ok);
        // coerce 后的整数在 JSON 树里是 double（与上游 parse 的数值形状一致）。
        Assert.Equal(2d, result.Value!["count"]!.GetValue<double>());
    }

    [Fact]
    public void NestedBranchWithItsOwnRelativeIdResolvesItsLocalRef()
    {
        // 分支自带相对 $id，#/$defs/n 必须在该分支自己的 document 下解析；
        // 若 $id 被应用两次，引用会落到不存在的 document 而校验失败。
        var schema = JsonNode.Parse("""
            {
              "$id": "https://example.com/root.json",
              "anyOf": [
                { "$id": "a/b.json", "$ref": "#/$defs/n", "$defs": { "n": { "type": "integer" } } },
                { "type": "boolean" }
              ]
            }
            """)!.AsObject();
        var validator = ToolSchemaValidator.Compile(schema);

        Assert.True(validator.Check(JsonValue.Create(5)));
        Assert.False(validator.Check(JsonNode.Parse("{}")));
    }
}
