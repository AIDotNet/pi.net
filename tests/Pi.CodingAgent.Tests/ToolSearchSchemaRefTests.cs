using Pi.Ai.Types;
using Pi.CodingAgent.Extensions.ToolSearch;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 1.5.1 #11803：tool_search 的搜索文本要跟随本地 <c>$ref</c>（<c>#/$defs/...</c>），
/// 把藏在定义里的属性名与描述也纳入；递归/共享定义只读一次；未引用定义、远程/缺失/畸形
/// 引用都不展开。向量逐字对照上游 <c>tool-search.test.ts</c>「schema references in the search text」。
/// </summary>
public class ToolSearchSchemaRefTests
{
    private static string Text(Dictionary<string, object?> parameters) =>
        ToolSearch.CreateToolSearchDocument("mcp__ledger__inspect", "Inspect records", new ToolSchema(parameters)).Text;

    [Fact]
    public void ReadsNamesAndDescriptionsBehindLocalReferences()
    {
        var text = Text(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["options"] = new Dictionary<string, object?> { ["$ref"] = "#/$defs/options" },
            },
            ["$defs"] = new Dictionary<string, object?>
            {
                ["options"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["mode"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = "reconciliation",
                        },
                    },
                },
            },
        });
        Assert.Equal("Inspect records options mode reconciliation", text);
    }

    [Fact]
    public void FollowsEscapedPointerSegments()
    {
        var text = Text(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["range"] = new Dictionary<string, object?> { ["$ref"] = "#/$defs/date~1range" },
            },
            ["$defs"] = new Dictionary<string, object?>
            {
                ["date/range"] = new Dictionary<string, object?> { ["description"] = "fiscal period" },
            },
        });
        Assert.Equal("Inspect records range fiscal period", text);
    }

    [Fact]
    public void ReadsRecursiveAndSharedDefinitionsOnce()
    {
        var node = new Dictionary<string, object?>
        {
            ["description"] = "tree node",
            ["properties"] = new Dictionary<string, object?>
            {
                ["children"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?> { ["$ref"] = "#/$defs/node" },
                },
            },
        };
        var text = Text(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["root"] = new Dictionary<string, object?> { ["$ref"] = "#/$defs/node" },
                ["other"] = new Dictionary<string, object?> { ["$ref"] = "#/$defs/node" },
                ["self"] = new Dictionary<string, object?> { ["$ref"] = "#" },
            },
            ["$defs"] = new Dictionary<string, object?> { ["node"] = node },
        });
        Assert.Equal("Inspect records root tree node children other self", text);
    }

    [Fact]
    public void LeavesOutUnreferencedRemoteMissingAndMalformedReferences()
    {
        var text = Text(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["remote"] = new Dictionary<string, object?> { ["$ref"] = "https://example.com/schema.json#/$defs/x" },
                ["missing"] = new Dictionary<string, object?> { ["$ref"] = "#/$defs/missing" },
                ["malformed"] = new Dictionary<string, object?> { ["$ref"] = "#/$defs/%E0%A4%A" },
            },
            ["$defs"] = new Dictionary<string, object?>
            {
                ["unused"] = new Dictionary<string, object?> { ["description"] = "reconciliation" },
            },
        });
        Assert.Equal("Inspect records remote missing malformed", text);
    }
}
