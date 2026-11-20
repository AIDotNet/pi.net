using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>
/// 上游 #12049：OpenAI strict 模式拒绝没有 type 的 schema 节点（如 items: {} 或裸 const）。
/// prefer 工具回退为非 strict，require 工具在本地报错；裸 enum 仍算有类型。
/// </summary>
public class ConstrainedSamplingUntypedTests
{
    private static ToolDefinition Tool(Dictionary<string, object?> schema, string strict)
        => new("t", "t", new ToolSchema(schema))
        {
            ConstrainedSampling = new JsonObject { ["type"] = "json_schema", ["strict"] = strict },
        };

    private static Dictionary<string, object?> UntypedItemsSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["choice"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object?>(),
            },
        },
        ["required"] = new List<object?> { "choice" },
    };

    [Fact]
    public void PreferFallsBackFromUntypedNodes()
        => Assert.Null(ConstrainedSampling.ResolveJsonSchemaStrictSampling(Tool(UntypedItemsSchema(), "prefer"), true));

    [Fact]
    public void RequireFailsLocallyFromUntypedNodes()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ConstrainedSampling.ResolveJsonSchemaStrictSampling(Tool(UntypedItemsSchema(), "require"), true));
        Assert.Contains("schemas without a type are unsupported", error.Message);
    }

    [Fact]
    public void EnumOnlyNodesStayStrict()
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["kind"] = new Dictionary<string, object?>
                {
                    ["enum"] = new List<object?> { "a", "b" },
                },
            },
            ["required"] = new List<object?> { "kind" },
        };
        Assert.True(ConstrainedSampling.ResolveJsonSchemaStrictSampling(Tool(schema, "prefer"), true));
    }
}
