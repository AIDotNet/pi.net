using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// OpenAI Chat Completions 纯函数层 wire 对照测试。对应 TS <c>openai-completions.ts</c> 的
/// <c>detectCompat</c>/<c>getCompat</c>/<c>buildParams</c>/<c>convertMessages</c>：
/// 兼容矩阵、thinkingFormat 分支、Anthropic 风格缓存标记、usage 与 stop reason 语义。
/// </summary>
public class OpenAiCompletionsWireTests
{
    private static ModelSpec Spec(
        string id = "gpt-test",
        string provider = "openai",
        string baseUrl = "https://api.openai.com/v1",
        bool reasoning = false,
        ThinkingLevelMap? thinking = null,
        JsonObject? compat = null,
        ModelCostRates? cost = null,
        IReadOnlyList<string>? input = null)
        => new()
        {
            Id = id,
            Name = id,
            Api = "openai-completions",
            Provider = provider,
            BaseUrl = baseUrl,
            Reasoning = reasoning,
            ThinkingLevelMap = thinking,
            Compat = compat,
            Cost = cost,
            ContextWindow = 128_000,
            MaxTokens = 8_192,
            Input = input ?? [ModelInput.Text],
        };

    private static ThinkingLevelMap ThinkingMap(string json)
        => ThinkingLevelMap.FromJsonObject(JsonNode.Parse(json)!.AsObject());

    private static ToolDefinition ReadTool(ToolSchema? schema = null)
    {
        schema ??= new ToolSchema(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
            ["required"] = new[] { "path" },
        });
        return new ToolDefinition("read", "Read a file", schema);
    }

    /// <summary>transcript 形状的前导 system 消息：工具声明挂在消息上，而非独立上下文字段。</summary>
    private static SystemMessage SystemWithTools(string text = "")
        => new(text, ToolsAdded: [ReadTool()], Timestamp: 0);

    private static JsonObject Obj(JsonNode? node) => Assert.IsType<JsonObject>(node);

    private static JsonArray Arr(JsonNode? node) => Assert.IsType<JsonArray>(node);

    private static string? Text(JsonNode? node, string name)
        => node?[name]?.GetValue<string>();

    // -----------------------------------------------------------------
    // detectCompat / getCompat
    // -----------------------------------------------------------------

    [Fact]
    public void DetectsCompatDefaultsForKnownProviders()
    {
        var openAi = OpenAiCompletions.GetCompat(Spec());
        Assert.Equal("openai", openAi.ThinkingFormat);
        Assert.Equal("max_completion_tokens", openAi.MaxTokensField);
        Assert.True(openAi.SupportsStore);
        Assert.True(openAi.SupportsDeveloperRole);
        Assert.True(openAi.SupportsReasoningEffort);
        Assert.True(openAi.SupportsLongCacheRetention);
        Assert.False(openAi.SendSessionAffinityHeaders);
        Assert.Null(openAi.CacheControlFormat);

        var deepSeek = OpenAiCompletions.GetCompat(
            Spec(id: "deepseek-chat", provider: "deepseek", baseUrl: "https://api.deepseek.com/v1", reasoning: true));
        Assert.Equal("deepseek", deepSeek.ThinkingFormat);
        Assert.Equal("max_tokens", deepSeek.MaxTokensField);
        Assert.False(deepSeek.SupportsStore);
        Assert.False(deepSeek.SupportsDeveloperRole);
        Assert.True(deepSeek.RequiresReasoningContentOnAssistantMessages);

        var openRouter = OpenAiCompletions.GetCompat(
            Spec(id: "anthropic/claude-test", provider: "openrouter",
                baseUrl: "https://openrouter.ai/api/v1", reasoning: true));
        Assert.Equal("openrouter", openRouter.ThinkingFormat);
        Assert.Equal("anthropic", openRouter.CacheControlFormat);
        Assert.True(openRouter.SupportsDeveloperRole);
        Assert.True(openRouter.SupportsStore);
        Assert.True(openRouter.SupportsReasoningEffort);
        Assert.True(openRouter.SendSessionAffinityHeaders);
        Assert.Equal("openrouter", openRouter.SessionAffinityFormat);

        var zai = OpenAiCompletions.GetCompat(
            Spec(id: "glm-5", provider: "zai", baseUrl: "https://api.z.ai/api/paas/v4", reasoning: true));
        Assert.Equal("zai", zai.ThinkingFormat);
        Assert.Equal("max_tokens", zai.MaxTokensField);
        Assert.False(zai.SupportsStore);
        Assert.False(zai.SupportsReasoningEffort);

        var together = OpenAiCompletions.GetCompat(
            Spec(id: "qwen3", provider: "together", baseUrl: "https://api.together.xyz/v1", reasoning: true));
        Assert.Equal("together", together.ThinkingFormat);
        Assert.Equal("max_tokens", together.MaxTokensField);
        Assert.False(together.SupportsReasoningEffort);
        Assert.False(together.SupportsLongCacheRetention);
    }

    [Fact]
    public void ExplicitCompatEntriesOverrideDetection()
    {
        var compat = OpenAiCompletions.GetCompat(Spec(
            provider: "openai",
            compat: JsonNode.Parse("""
            {
              "thinkingFormat": "zai",
              "supportsStore": false,
              "maxTokensField": "max_tokens",
              "supportsReasoningEffort": false,
              "cacheControlFormat": "anthropic",
              "vllmPriority": 17
            }
            """)!.AsObject()));

        Assert.Equal("zai", compat.ThinkingFormat);
        Assert.False(compat.SupportsStore);
        Assert.Equal("max_tokens", compat.MaxTokensField);
        Assert.False(compat.SupportsReasoningEffort);
        Assert.Equal("anthropic", compat.CacheControlFormat);
        Assert.Equal(17, compat.VllmPriority!.GetValue<int>());

        var parameters = OpenAiCompletions.BuildParams(
            Spec(compat: JsonNode.Parse("""{"vllmPriority":17}""")!.AsObject()),
            new TranscriptContext([Messages.UserText("hi")]));
        Assert.Equal(17, parameters["priority"]!.GetValue<int>());
    }

    // -----------------------------------------------------------------
    // cacheControlFormat: anthropic
    // -----------------------------------------------------------------

    [Fact]
    public void AppliesAnthropicCacheMarkersToSystemToolsAndLastMessage()
    {
        var model = Spec(id: "anthropic/claude-test", provider: "openrouter",
            baseUrl: "https://openrouter.ai/api/v1", reasoning: true);
        var context = new TranscriptContext(
            [SystemWithTools("System prompt"), Messages.UserText("Hello")]);

        var parameters = OpenAiCompletions.BuildParams(model, context,
            new SimpleStreamOptions(ApiKey: "test-key"));
        var messages = Arr(parameters["messages"]);

        var instruction = Obj(messages[0]);
        Assert.Equal("developer", Text(instruction, "role"));
        var instructionParts = Arr(instruction["content"]);
        Assert.Equal("ephemeral", Text(instructionParts[0]!["cache_control"], "type"));

        var tools = Arr(parameters["tools"]);
        Assert.Equal("ephemeral", Text(tools[0]!["cache_control"], "type"));

        var last = Obj(messages[^1]);
        Assert.Equal("user", Text(last, "role"));
        var lastParts = Arr(last["content"]);
        Assert.Equal("ephemeral", Text(lastParts[0]!["cache_control"], "type"));
    }

    [Fact]
    public void MovesConversationCacheMarkerToToolResult()
    {
        var model = Spec(id: "anthropic/claude-test", provider: "openrouter",
            baseUrl: "https://openrouter.ai/api/v1", reasoning: true);
        var messages = new List<ChatMessage>
        {
            SystemWithTools(),
            new UserMessage("Read the file", 0),
            new AssistantMessage(
                [new ToolCallContent("call_1", "read", new JsonObject { ["path"] = "README.md" })],
                StopReason.ToolUse),
            Messages.ToolResult("call_1", "read", "file contents"),
        };

        var parameters = OpenAiCompletions.BuildParams(model,
            new TranscriptContext(messages),
            new SimpleStreamOptions(ApiKey: "test-key"));
        var wireMessages = Arr(parameters["messages"]);

        var user = Obj(wireMessages[0]);
        Assert.Equal("Read the file", user["content"]!.GetValue<string>());

        var toolMessage = Obj(wireMessages[^1]);
        Assert.Equal("tool", Text(toolMessage, "role"));
        var parts = Arr(toolMessage["content"]);
        Assert.Equal("file contents", Text(parts[0], "text"));
        Assert.Equal("ephemeral", Text(parts[0]!["cache_control"], "type"));
    }

    [Fact]
    public void OmitsCacheMarkersWhenRetentionIsNone()
    {
        var model = Spec(id: "anthropic/claude-test", provider: "openrouter",
            baseUrl: "https://openrouter.ai/api/v1", reasoning: true);
        var context = new TranscriptContext(
            [SystemWithTools("System prompt"), new UserMessage("Hello", 0)]);

        var parameters = OpenAiCompletions.BuildParams(model, context,
            new SimpleStreamOptions(ApiKey: "test-key") { CacheRetention = "none" });

        var messages = Arr(parameters["messages"]);
        Assert.False(messages[0]!["content"] is JsonArray);
        Assert.Equal("System prompt", messages[0]!["content"]!.GetValue<string>());
        var tools = Arr(parameters["tools"]);
        Assert.Null(tools[0]!["cache_control"]);
        Assert.False(messages[^1]!["content"] is JsonArray);
        Assert.Equal("Hello", messages[^1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void LongRetentionAddsOneHourTtlToCacheMarkers()
    {
        var model = Spec(id: "anthropic/claude-test", provider: "openrouter",
            baseUrl: "https://openrouter.ai/api/v1", reasoning: true);
        var context = new TranscriptContext(
            [SystemWithTools("System prompt"), Messages.UserText("Hello")]);

        var parameters = OpenAiCompletions.BuildParams(model, context,
            new SimpleStreamOptions(ApiKey: "test-key") { CacheRetention = "long" });

        var tools = Arr(parameters["tools"]);
        Assert.Equal("1h", Text(tools[0]!["cache_control"], "ttl"));
        Assert.Equal("24h", parameters["prompt_cache_retention"]!.GetValue<string>());
    }

    // -----------------------------------------------------------------
    // thinkingFormat 分支
    // -----------------------------------------------------------------

    private static JsonObject ParamsFor(ModelSpec model, string? reasoning, SimpleStreamOptions? extra = null)
    {
        var options = (extra ?? new SimpleStreamOptions(ApiKey: "test"))
            with { ReasoningEffort = reasoning };
        return OpenAiCompletions.BuildParams(model, new TranscriptContext([Messages.UserText("hi")]), options);
    }

    [Fact]
    public void DeepSeekThinkingEnablesEffortAndDisablesByDefault()
    {
        var model = Spec(id: "deepseek-reasoner", provider: "deepseek",
            baseUrl: "https://api.deepseek.com/v1", reasoning: true);

        var enabled = ParamsFor(model, "high");
        Assert.Equal("enabled", Text(enabled["thinking"], "type"));
        Assert.Equal("high", enabled["reasoning_effort"]!.GetValue<string>());

        var disabled = ParamsFor(model, null);
        Assert.Equal("disabled", Text(disabled["thinking"], "type"));
        Assert.False(disabled.ContainsKey("reasoning_effort"));
    }

    [Fact]
    public void DeepSeekExplicitOffKeepsThinkingFieldAbsent()
    {
        var model = Spec(id: "deepseek-reasoner", provider: "deepseek",
            baseUrl: "https://api.deepseek.com/v1", reasoning: true,
            thinking: ThinkingMap("""{"off":null}"""));

        var parameters = ParamsFor(model, null);
        Assert.False(parameters.ContainsKey("thinking"));
    }

    [Fact]
    public void OpenRouterThinkingUsesNestedReasoningEffort()
    {
        var model = Spec(id: "google/gemini-test", provider: "openrouter",
            baseUrl: "https://openrouter.ai/api/v1", reasoning: true,
            thinking: ThinkingMap("""{"off":"none","high":"high"}"""));

        var enabled = ParamsFor(model, "high");
        Assert.Equal("high", Text(enabled["reasoning"], "effort"));

        var off = ParamsFor(model, null);
        Assert.Equal("none", Text(off["reasoning"], "effort"));
    }

    [Fact]
    public void OpenRouterExplicitOffOmitsReasoningObject()
    {
        var model = Spec(id: "google/gemini-test", provider: "openrouter",
            baseUrl: "https://openrouter.ai/api/v1", reasoning: true,
            thinking: ThinkingMap("""{"off":null}"""));

        var parameters = ParamsFor(model, null);
        Assert.False(parameters.ContainsKey("reasoning"));
    }

    [Fact]
    public void ZaiThinkingEnabledCarriesClearThinkingFalse()
    {
        var model = Spec(id: "glm-5", provider: "zai",
            baseUrl: "https://api.z.ai/api/paas/v4", reasoning: true,
            thinking: ThinkingMap("""{"high":"high"}"""));

        var enabled = ParamsFor(model, "high");
        Assert.Equal("enabled", Text(enabled["thinking"], "type"));
        Assert.False(enabled["thinking"]!["clear_thinking"]!.GetValue<bool>());
        // zai 默认不支持 reasoning_effort。
        Assert.False(enabled.ContainsKey("reasoning_effort"));

        var disabled = ParamsFor(model, null);
        Assert.Equal("disabled", Text(disabled["thinking"], "type"));
    }

    [Fact]
    public void TogetherThinkingUsesEnabledFlag()
    {
        var model = Spec(id: "qwen3", provider: "together",
            baseUrl: "https://api.together.xyz/v1", reasoning: true,
            thinking: ThinkingMap("""{"high":"high"}"""));

        var enabled = ParamsFor(model, "high");
        Assert.True(enabled["reasoning"]!["enabled"]!.GetValue<bool>());
        // together 默认不支持 reasoning_effort。
        Assert.False(enabled.ContainsKey("reasoning_effort"));

        var disabled = ParamsFor(model, null);
        Assert.False(disabled["reasoning"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void OpenAiThinkingUsesReasoningEffortAndOffMapping()
    {
        var plain = Spec(reasoning: true);
        Assert.Equal("medium", ParamsFor(plain, "medium")["reasoning_effort"]!.GetValue<string>());
        Assert.False(ParamsFor(plain, null).ContainsKey("reasoning_effort"));

        var mapped = Spec(reasoning: true, thinking: ThinkingMap("""{"off":"minimal","high":"high"}"""));
        Assert.Equal("minimal", ParamsFor(mapped, null)["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("high", ParamsFor(mapped, "high")["reasoning_effort"]!.GetValue<string>());
    }

    // -----------------------------------------------------------------
    // tools / 载荷
    // -----------------------------------------------------------------

    [Fact]
    public void OmitsToolsWhenEmptyButKeepsEmptyArrayWithToolHistory()
    {
        var model = Spec();
        var noTools = OpenAiCompletions.BuildParams(model,
            new TranscriptContext([Messages.UserText("hi")]),
            new SimpleStreamOptions(ApiKey: "test"));
        Assert.False(noTools.ContainsKey("tools"));

        var history = new List<ChatMessage>
        {
            Messages.UserText("use the tool"),
            new AssistantMessage([new ToolCallContent("t1", "noop", new JsonObject())], StopReason.ToolUse),
            Messages.ToolResult("t1", "noop", "done"),
        };
        var withHistory = OpenAiCompletions.BuildParams(model,
            new TranscriptContext(history),
            new SimpleStreamOptions(ApiKey: "test"));
        Assert.Empty(Arr(withHistory["tools"]));
    }

    [Fact]
    public void SelectsMaxTokensFieldFromCompat()
    {
        var deepSeek = OpenAiCompletions.BuildParams(
            Spec(id: "deepseek-chat", provider: "deepseek", baseUrl: "https://api.deepseek.com/v1"),
            new TranscriptContext([Messages.UserText("hi")]),
            new SimpleStreamOptions(ApiKey: "test", MaxTokens: 1234));
        Assert.Equal(1234, deepSeek["max_tokens"]!.GetValue<int>());
        Assert.False(deepSeek.ContainsKey("max_completion_tokens"));

        var openAi = OpenAiCompletions.BuildParams(
            Spec(),
            new TranscriptContext([Messages.UserText("hi")]),
            new SimpleStreamOptions(ApiKey: "test", MaxTokens: 1234));
        Assert.Equal(1234, openAi["max_completion_tokens"]!.GetValue<int>());
        Assert.False(openAi.ContainsKey("max_tokens"));
    }

    [Fact]
    public void GrammarConstrainedToolUsesCustomToolShape()
    {
        var tool = ReadTool();
        tool.ConstrainedSampling = JsonNode.Parse("""
        {"type":"grammar","variants":{"openai_lark":"start: /[a-z]+/"}}
        """)!.AsObject();

        var model = Spec(compat: JsonNode.Parse("""{"supportsOpenAIGrammarTools":true}""")!.AsObject());
        var parameters = OpenAiCompletions.BuildParams(model,
            new TranscriptContext([new SystemMessage("", ToolsAdded: [tool], Timestamp: 0), Messages.UserText("hi")]),
            new SimpleStreamOptions(ApiKey: "test"));

        var tools = Arr(parameters["tools"]);
        var custom = Obj(tools[0]);
        Assert.Equal("custom", Text(custom, "type"));
        Assert.Equal("read", Text(custom["custom"], "name"));
        Assert.Equal("lark", Text(custom["custom"]!["format"]!["grammar"], "syntax"));
        Assert.Equal("start: /[a-z]+/", Text(custom["custom"]!["format"]!["grammar"], "definition"));
    }

    [Fact]
    public void ParsesChunkUsageIntoCachedAndWrittenBuckets()
    {
        var model = Spec(cost: new ModelCostRates(Input: 3, Output: 15, CacheRead: 0.3, CacheWrite: 3.75));
        var raw = JsonNode.Parse("""
        {
          "prompt_tokens": 1000,
          "completion_tokens": 50,
          "prompt_tokens_details": { "cached_tokens": 200, "cache_write_tokens": 100 },
          "completion_tokens_details": { "reasoning_tokens": 10 }
        }
        """)!.AsObject();

        var usage = OpenAiCompletions.ParseChunkUsage(raw, model);

        Assert.Equal(700, usage.Input);
        Assert.Equal(50, usage.Output);
        Assert.Equal(200, usage.CacheRead);
        Assert.Equal(100, usage.CacheWrite);
        Assert.Equal(10, usage.Reasoning);
        Assert.Equal(1_050, usage.TotalTokens);
        Assert.Equal(0.0021, usage.Cost.Input, 10);
        Assert.Equal(0.00075, usage.Cost.Output, 10);
        Assert.Equal(0.00006, usage.Cost.CacheRead, 10);
        Assert.Equal(0.000375, usage.Cost.CacheWrite, 10);
        Assert.Equal(0.003285, usage.Cost.Total, 10);
    }

    [Fact]
    public void UsesDocumentedCacheReadFallbacks()
    {
        var deepSeek = JsonNode.Parse("""{"prompt_tokens":10,"completion_tokens":1,"prompt_cache_hit_tokens":4}""")!.AsObject();
        Assert.Equal(4, OpenAiCompletions.ParseChunkUsage(deepSeek, Spec()).CacheRead);

        var kimi = JsonNode.Parse("""{"prompt_tokens":10,"completion_tokens":1,"cached_tokens":3}""")!.AsObject();
        Assert.Equal(3, OpenAiCompletions.ParseChunkUsage(kimi, Spec()).CacheRead);
    }

    [Theory]
    [InlineData("stop", StopReason.Stop, null)]
    [InlineData("end", StopReason.Stop, null)]
    [InlineData("length", StopReason.Length, null)]
    [InlineData("function_call", StopReason.ToolUse, null)]
    [InlineData("tool_calls", StopReason.ToolUse, null)]
    [InlineData("content_filter", StopReason.Error, "Provider finish_reason: content_filter")]
    [InlineData("network_error", StopReason.Error, "Provider finish_reason: network_error")]
    [InlineData("mystery", StopReason.Error, "Provider finish_reason: mystery")]
    public void MapsStopReasons(string reason, StopReason expected, string? message)
    {
        var (stopReason, errorMessage) = OpenAiCompletions.MapStopReasonDetailed(reason);
        Assert.Equal(expected, stopReason);
        Assert.Equal(message, errorMessage);
    }

    // -----------------------------------------------------------------
    // convertMessages
    // -----------------------------------------------------------------

    [Fact]
    public void ReplaysReasoningDetailsFromThinkingSignature()
    {
        const string detail = """{"type":"reasoning.encrypted","id":"call_1","data":"encrypted-signature"}""";
        var model = Spec(id: "google/gemini-test", provider: "openrouter",
            baseUrl: "https://openrouter.ai/api/v1", reasoning: true);
        var messages = new List<ChatMessage>
        {
            SystemWithTools(),
            Messages.UserText("read it"),
            new AssistantMessage(
            [
                new ThinkingContent("", $"[{detail}]"),
                new ToolCallContent("call_1", "read", new JsonObject { ["path"] = "README.md" }),
            ], StopReason.ToolUse,
                Model: model.Id, Api: model.Api, Provider: model.Provider),
            Messages.ToolResult("call_1", "read", "ok"),
        };

        var converted = OpenAiCompletions.ConvertMessages(model, new TranscriptContext(messages),
            OpenAiCompletions.GetCompat(model));
        var assistant = Obj(converted[1]);
        var details = Arr(assistant["reasoning_details"]);
        Assert.Equal("reasoning.encrypted", Text(details[0], "type"));
        Assert.Equal("encrypted-signature", Text(details[0], "data"));
        Assert.Null(assistant["reasoning"]);
    }

    [Fact]
    public void FallsBackToEncryptedToolCallSignatureForOlderMessages()
    {
        const string detail = """{"type":"reasoning.encrypted","id":"call_1","data":"encrypted-signature"}""";
        var model = Spec(id: "google/gemini-test", provider: "openrouter",
            baseUrl: "https://openrouter.ai/api/v1", reasoning: true);
        var toolCall = new ToolCallContent("call_1", "read", new JsonObject { ["path"] = "README.md" })
        {
            ThoughtSignature = detail,
        };
        var messages = new List<ChatMessage>
        {
            SystemWithTools(),
            Messages.UserText("read it"),
            new AssistantMessage([toolCall], StopReason.ToolUse,
                Model: model.Id, Api: model.Api, Provider: model.Provider),
            Messages.ToolResult("call_1", "read", "ok"),
        };

        var converted = OpenAiCompletions.ConvertMessages(model, new TranscriptContext(messages),
            OpenAiCompletions.GetCompat(model));
        var assistant = Obj(converted[1]);
        Assert.Equal("reasoning.encrypted",
            Text(Arr(assistant["reasoning_details"])[0], "type"));
    }

    [Fact]
    public void NormalizesForeignToolCallIds()
    {
        var model = Spec();
        var messages = new List<ChatMessage>
        {
            SystemWithTools(),
            Messages.UserText("read it"),
            new AssistantMessage(
                [new ToolCallContent("call_1|item-2", "read", new JsonObject { ["path"] = "a.txt" })],
                StopReason.ToolUse),
            Messages.ToolResult("call_1|item-2", "read", "ok"),
        };

        var converted = OpenAiCompletions.ConvertMessages(model, new TranscriptContext(messages),
            OpenAiCompletions.GetCompat(model));

        var assistant = Obj(converted[1]);
        var id = Text(Arr(assistant["tool_calls"])[0], "id");
        Assert.Equal("call_1_item-2", id);
        var tool = Obj(converted[2]);
        Assert.Equal(id, Text(tool, "tool_call_id"));
    }

    [Fact]
    public void AppendsToolResultImagesAsUserMessage()
    {
        var model = Spec(input: [ModelInput.Text, ModelInput.Image]);
        var messages = new List<ChatMessage>
        {
            SystemWithTools(),
            Messages.UserText("read it"),
            new AssistantMessage(
                [new ToolCallContent("call_1", "read", new JsonObject { ["path"] = "a.png" })],
                StopReason.ToolUse),
            new ToolResultMessage("call_1", "read",
                [new TextContent("screenshot"), new ImageContent("QUJD", "image/png")]),
        };

        var converted = OpenAiCompletions.ConvertMessages(model, new TranscriptContext(messages),
            OpenAiCompletions.GetCompat(model));

        Assert.Equal(4, converted.Count);
        var toolMessage = Obj(converted[2]);
        Assert.Equal("tool", Text(toolMessage, "role"));
        Assert.Equal("screenshot", Text(toolMessage, "content"));
        var imageMessage = Obj(converted[3]);
        Assert.Equal("user", Text(imageMessage, "role"));
        var parts = Arr(imageMessage["content"]);
        Assert.Equal("Attached image(s) from tool result:", Text(parts[0], "text"));
        Assert.Equal("data:image/png;base64,QUJD", Text(parts[1]!["image_url"], "url"));
    }

    [Fact]
    public void AddsReasoningContentForDeepSeekAssistantMessages()
    {
        var model = Spec(id: "deepseek-reasoner", provider: "deepseek",
            baseUrl: "https://api.deepseek.com/v1", reasoning: true);
        var messages = new List<ChatMessage>
        {
            SystemWithTools(),
            Messages.UserText("read it"),
            new AssistantMessage(
                [new ToolCallContent("call_1", "read", new JsonObject { ["path"] = "a.txt" })],
                StopReason.ToolUse),
            Messages.ToolResult("call_1", "read", "ok"),
        };

        var converted = OpenAiCompletions.ConvertMessages(model, new TranscriptContext(messages),
            OpenAiCompletions.GetCompat(model));
        var assistant = Obj(converted[1]);
        Assert.Equal("", Text(assistant, "reasoning_content"));
    }

    [Fact]
    public void UsesDeveloperRoleForReasoningModelsWhenSupported()
    {
        var reasoningModel = Spec(reasoning: true);
        var developer = OpenAiCompletions.ConvertMessages(reasoningModel,
            new TranscriptContext([new SystemMessage("be brief", Timestamp: 0), Messages.UserText("hi")]),
            OpenAiCompletions.GetCompat(reasoningModel));
        Assert.Equal("developer", Text(developer[0], "role"));

        var plainModel = Spec();
        var system = OpenAiCompletions.ConvertMessages(plainModel,
            new TranscriptContext([new SystemMessage("be brief", Timestamp: 0), Messages.UserText("hi")]),
            OpenAiCompletions.GetCompat(plainModel));
        Assert.Equal("system", Text(system[0], "role"));
    }
}
