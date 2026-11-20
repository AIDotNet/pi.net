using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>
/// 上游 #12148：Mistral 的 strict 工具是 opt-in。Mistral Large 4 带 strict: true 时可能沉默
/// 数分钟才发起工具调用，默认必须发 strict: false，只有 compat.supportsStrictMode 才发 strict。
/// </summary>
public class MistralToolSchemaTests
{
    private static ModelSpec Spec(JsonObject? compat = null)
        => new()
        {
            Id = "mistral-large-4",
            Name = "Mistral Large 4",
            Api = "mistral-conversations",
            Provider = "mistral",
            BaseUrl = "https://api.mistral.ai",
            Reasoning = false,
            ContextWindow = 128_000,
            MaxTokens = 32_000,
            Input = [ModelInput.Text],
            Compat = compat,
        };

    private static ToolDefinition ReadTool()
        => new("read", "read", new ToolSchema(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
            ["required"] = new List<object?> { "path" },
            ["additionalProperties"] = false,
        }))
        {
            ConstrainedSampling = new JsonObject { ["type"] = "json_schema", ["strict"] = "prefer" },
        };

    private static bool? SentStrict(JsonObject payload)
        => payload["tools"]?[0]?["function"]?["strict"]?.GetValue<bool>();

    private static JsonObject Build(JsonObject? compat)
    {
        var system = new SystemMessage("be brief", ToolsAdded: [ReadTool()], Timestamp: 0);
        var user = new UserMessage([new TextContent("hi")], 1);
        return MistralConversations.BuildChatPayload(
            Spec(compat), new TranscriptContext([system, user]), [system, user], null);
    }

    [Fact]
    public void StrictToolsAreOffByDefault()
        => Assert.False(SentStrict(Build(null)));

    [Fact]
    public void StrictToolsFollowCompatSupportsStrictMode()
        => Assert.True(SentStrict(Build(new JsonObject { ["supportsStrictMode"] = true })));
}
