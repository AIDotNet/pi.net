using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// tools/call 结果的承载与内容转换。覆盖上游 #10416：<c>structuredContent</c> 可以是任意 JSON 值
/// （MCP 2026-07-28 允许数组、标量与 <c>null</c>，更早的修订只允许对象），非对象值不得让整个调用失败。
/// </summary>
public class ProtocolContentTests
{
    [Fact]
    public void CallToolResultAcceptsNonObjectStructuredContent()
    {
        foreach (var structured in new[] { "[1,2,3]", "\"text\"", "42", "true" })
        {
            var value = new JsonObject
            {
                ["content"] = new JsonArray(),
                ["structuredContent"] = JsonNode.Parse(structured),
            };

            var result = McpProtocol.ValidateCallToolResult(value);

            Assert.Empty(result.Content);
            Assert.NotNull(result.StructuredContent);
            Assert.Equal(structured, result.StructuredContent!.ToJsonString());
        }
    }

    [Fact]
    public void CallToolResultStillRejectsMalformedContent()
    {
        // content 不是数组仍然非法：放开的是 structuredContent 的类型，不是报文结构。
        var value = new JsonObject { ["content"] = "nope" };
        Assert.Throws<McpError>(() => McpProtocol.ValidateCallToolResult(value));
    }

    [Fact]
    public void ArrayStructuredContentBecomesTheModelText()
    {
        // 没有 content 块时，结构化内容转成 JSON 文本交给模型——数组同样适用。
        var result = McpProtocol.ValidateCallToolResult(new JsonObject
        {
            ["content"] = new JsonArray(),
            ["structuredContent"] = JsonNode.Parse("[1,2,3]"),
        });

        var content = McpContent.ToLlmContent(result.Content, result.StructuredContent);
        var text = Assert.IsType<LlmTextContent>(Assert.Single(content));
        Assert.Contains("[", text.Text);
        Assert.Contains("3", text.Text);
    }

    [Fact]
    public void ScalarStructuredContentBecomesTheModelText()
    {
        var result = McpProtocol.ValidateCallToolResult(new JsonObject
        {
            ["content"] = new JsonArray(),
            ["structuredContent"] = JsonNode.Parse("42"),
        });

        var text = Assert.IsType<LlmTextContent>(Assert.Single(
            McpContent.ToLlmContent(result.Content, result.StructuredContent)));
        Assert.Equal("42", text.Text.Trim());
    }
}
