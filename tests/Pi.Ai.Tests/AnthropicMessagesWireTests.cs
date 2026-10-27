using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// Anthropic Messages 纯函数层 wire 对照测试。对应 TS <c>anthropic-messages.ts</c> 的
/// compat 解析、cache_control、thinking 分支、中段 system/工具变更、OAuth 身份、
/// server-side fallback 与 stop reason 映射。
/// </summary>
public class AnthropicMessagesWireTests
{
    private static ModelSpec Spec(
        string id = "claude-test",
        string provider = "anthropic",
        string baseUrl = "https://api.anthropic.com",
        bool reasoning = true,
        ThinkingLevelMap? thinking = null,
        JsonObject? compat = null)
        => new()
        {
            Id = id,
            Name = id,
            Api = "anthropic-messages",
            Provider = provider,
            BaseUrl = baseUrl,
            Reasoning = reasoning,
            ThinkingLevelMap = thinking,
            Compat = compat,
            ContextWindow = 200_000,
            MaxTokens = 8_192,
            Input = [ModelInput.Text, ModelInput.Image],
        };

    private static ToolDefinition ReadTool(string name = "read")
        => new(name, "Read a file", new ToolSchema(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
            ["required"] = new[] { "path" },
        }));

    private static SystemMessage SystemWithTools(string text = "", params ToolDefinition[] tools)
        => new(text, ToolsAdded: tools.Length > 0 ? tools : [ReadTool()], Timestamp: 0);

    private static JsonObject Obj(JsonNode? node) => Assert.IsType<JsonObject>(node);

    private static JsonArray Arr(JsonNode? node) => Assert.IsType<JsonArray>(node);

    private static string? Str(JsonNode? node, string name)
        => node?[name]?.GetValue<string>();

    [Fact]
    public void CompatDefaultsAndOverrides()
    {
        var defaults = AnthropicMessages.GetCompat(Spec());
        Assert.True(defaults.SupportsEagerToolInputStreaming);
        Assert.True(defaults.SupportsLongCacheRetention);
        Assert.True(defaults.SupportsCacheControlOnTools);
        Assert.True(defaults.SupportsTemperature);
        Assert.False(defaults.ForceAdaptiveThinking);
        Assert.False(defaults.SendSessionAffinityHeaders);
        Assert.Null(defaults.AllowedFallbackModels);

        var openRouter = AnthropicMessages.GetCompat(
            Spec(provider: "openrouter", baseUrl: "https://openrouter.ai/api"));
        Assert.True(openRouter.SendSessionAffinityHeaders);
        Assert.Equal("openrouter", openRouter.SessionAffinityFormat);

        var overridden = AnthropicMessages.GetCompat(Spec(compat: JsonNode.Parse("""
            {
              "supportsTemperature": false,
              "forceAdaptiveThinking": true,
              "supportsLongCacheRetention": false,
              "allowedFallbackModels": [
                { "provider": "anthropic", "model": "claude-backup",
                  "cost": { "input": 1.5, "output": 7.5 } }
              ]
            }
            """)!.AsObject()));
        Assert.False(overridden.SupportsTemperature);
        Assert.True(overridden.ForceAdaptiveThinking);
        Assert.False(overridden.SupportsLongCacheRetention);
        var fallback = Assert.Single(overridden.AllowedFallbackModels!);
        Assert.Equal("claude-backup", fallback.Model);
        Assert.Equal(1.5, fallback.Cost.Input);
        Assert.Equal(7.5, fallback.Cost.Output);
    }

    [Fact]
    public void AddsCacheControlToSystemToolsAndLastMessage()
    {
        var options = new SimpleStreamOptions(ApiKey: "sk-ant") { CacheRetention = "short" };
        var parameters = AnthropicMessages.BuildParams(
            Spec(),
            new TranscriptContext([SystemWithTools("be brief"), Messages.UserText("hi")]),
            false,
            options);

        var system = Arr(parameters["system"]);
        Assert.Equal("ephemeral", Str(system[0]!["cache_control"], "type"));
        var tools = Arr(parameters["tools"]);
        Assert.Equal("ephemeral", Str(tools[tools.Count - 1]!["cache_control"], "type"));
        var messages = Arr(parameters["messages"]);
        var lastContent = Arr(messages[messages.Count - 1]!["content"]);
        Assert.Equal("ephemeral", Str(lastContent[lastContent.Count - 1]!["cache_control"], "type"));
        Assert.True(tools[0]!["eager_input_streaming"]!.GetValue<bool>());
    }

    [Fact]
    public void LongRetentionAddsOneHourTtlAndNoneOmitsMarkers()
    {
        var longParams = AnthropicMessages.BuildParams(
            Spec(),
            new TranscriptContext([SystemWithTools("be brief"), Messages.UserText("hi")]),
            false,
            new SimpleStreamOptions(ApiKey: "sk-ant") { CacheRetention = "long" });
        Assert.Equal("1h", Str(Arr(longParams["system"])[0]!["cache_control"], "ttl"));
        var longMessages = Arr(longParams["messages"]);
        var longContent = Arr(longMessages[longMessages.Count - 1]!["content"]);
        Assert.Equal("1h", Str(longContent[longContent.Count - 1]!["cache_control"], "ttl"));

        var noneParams = AnthropicMessages.BuildParams(
            Spec(),
            new TranscriptContext([SystemWithTools("be brief"), Messages.UserText("hi")]),
            false,
            new SimpleStreamOptions(ApiKey: "sk-ant") { CacheRetention = "none" });
        Assert.Null(Arr(noneParams["system"])[0]!["cache_control"]);
        Assert.Null(Arr(noneParams["tools"])[0]!["cache_control"]);
        var noneMessages = Arr(noneParams["messages"]);
        var noneContent = Arr(noneMessages[noneMessages.Count - 1]!["content"]);
        Assert.Null(noneContent[noneContent.Count - 1]!["cache_control"]);
    }

    [Fact]
    public void LegacyThinkingUsesBudgetAndSuppressesTemperature()
    {
        var options = new SimpleStreamOptions(
            ApiKey: "sk-ant", Temperature: 0.5)
        {
            ThinkingEnabled = true,
            ThinkingBudgetTokens = 2_048,
            ThinkingDisplay = "omitted",
        };
        var parameters = AnthropicMessages.BuildParams(
            Spec(), new TranscriptContext([Messages.UserText("think")]), false, options);

        var thinking = Obj(parameters["thinking"]);
        Assert.Equal("enabled", Str(thinking, "type"));
        Assert.Equal(2_048, thinking["budget_tokens"]!.GetValue<int>());
        Assert.Equal("omitted", Str(thinking, "display"));
        Assert.Null(parameters["temperature"]);
    }

    [Fact]
    public void AdaptiveThinkingUsesEffortAndOutputConfig()
    {
        var spec = Spec(compat: JsonNode.Parse("""{ "forceAdaptiveThinking": true }""")!.AsObject());
        var options = new SimpleStreamOptions(ApiKey: "sk-ant")
        {
            ThinkingEnabled = true,
            ThinkingDisplay = "summarized",
            Effort = "low",
        };
        var parameters = AnthropicMessages.BuildParams(
            spec, new TranscriptContext([Messages.UserText("think")]), false, options);

        var thinking = Obj(parameters["thinking"]);
        Assert.Equal("adaptive", Str(thinking, "type"));
        Assert.Equal("summarized", Str(thinking, "display"));
        Assert.Equal("low", Str(parameters["output_config"], "effort"));
        // forceAdaptiveThinking 抑制 interleaved beta；本用例无工具，其余 beta 条件均不成立。
        Assert.Null(parameters["betas"]);
    }

    [Fact]
    public void ThinkingDisabledEmitsDisabledType()
    {
        var options = new SimpleStreamOptions(ApiKey: "sk-ant") { ThinkingEnabled = false };
        var parameters = AnthropicMessages.BuildParams(
            Spec(), new TranscriptContext([Messages.UserText("hi")]), false, options);
        Assert.Equal("disabled", Str(Obj(parameters["thinking"]), "type"));
    }

    [Fact]
    public void ManagedEffortInsertsHistoricalAndActiveOutputConfig()
    {
        var spec = Spec(compat: JsonNode.Parse("""
            { "supportsMidConvoEffort": true }
            """)!.AsObject());
        var assistant = new AssistantMessage(
            [new TextContent("hello")], StopReason.Stop,
            Model: spec.Id, Api: spec.Api, Provider: spec.Provider)
        {
            ProviderThinkingLevel = "medium",
        };
        var parameters = AnthropicMessages.BuildParams(
            spec,
            new TranscriptContext([Messages.UserText("hi"), assistant, Messages.UserText("again")]),
            false,
            new SimpleStreamOptions(ApiKey: "sk-ant"));

        var thinking = Obj(parameters["thinking"]);
        Assert.Equal("adaptive", Str(thinking, "type"));
        Assert.Equal("summarized", Str(thinking, "display"));
        Assert.Equal("drop_block", Str(thinking["block_binding"], "prefix_mismatch_behavior"));
        // 顶层 effort 固定 low：组织可以设上限，高于上限的档位会被整条请求拒掉；
        // 逐条消息的 effort 标记仍带真实档位（见下方的 medium / high）。
        Assert.Equal("low", Str(parameters["output_config"], "effort"));

        var messages = Arr(parameters["messages"]);
        var efforts = messages
            .Select(message => message?["output_config"] is JsonObject config ? Str(config, "effort") : null)
            .Where(value => value is not null)
            .ToList();
        Assert.Equal(["medium", "high"], efforts);
        var betas = Arr(parameters["betas"]).Select(node => node!.GetValue<string>()).ToList();
        Assert.Contains("mid-conversation-output-config-2026-07-01", betas);
        Assert.Contains("thinking-binding-controls-2026-08-01", betas);
    }

    [Fact]
    public void NativeToolChangesEmitRemovalAdditionAndPlaceholder()
    {
        var spec = Spec(compat: JsonNode.Parse("""
            {
              "supportsMidConvoSystemMessages": true,
              "supportsMidConvoToolChanges": true
            }
            """)!.AsObject());
        var messages = new List<ChatMessage>
        {
            SystemWithTools("be brief", ReadTool()),
            Messages.UserText("hi"),
            new SystemMessage("", ToolsRemoved: ["read"], ToolsAdded: [ReadTool("grep")], Timestamp: 1),
        };

        var parameters = AnthropicMessages.BuildParams(
            spec, new TranscriptContext(messages), false,
            new SimpleStreamOptions(ApiKey: "sk-ant"));

        var tools = Arr(parameters["tools"]);
        Assert.Equal(2, tools.Count);
        Assert.Equal("read", Str(tools[0], "name"));
        Assert.Equal("__pi_deferred_placeholder__", Str(tools[1], "name"));
        Assert.True(tools[1]!["defer_loading"]!.GetValue<bool>());

        var messageList = Arr(parameters["messages"]);
        var content = Arr(messageList[messageList.Count - 1]!["content"]);
        var types = content.Select(block => Str(block, "type")).ToList();
        Assert.Contains("tool_removal", types);
        Assert.Contains("tool_addition", types);
        var removal = content.First(block => Str(block, "type") == "tool_removal")!;
        Assert.Equal("read", Str(removal["tool"], "name"));
        var betas = Arr(parameters["betas"]).Select(node => node!.GetValue<string>()).ToList();
        Assert.Contains("inline-tools-2026-09-15", betas);
    }

    [Fact]
    public void OAuthUsesClaudeCodeIdentityAndToolNames()
    {
        var parameters = AnthropicMessages.BuildParams(
            Spec(),
            new TranscriptContext([SystemWithTools("be brief", ReadTool()), Messages.UserText("hi")]),
            true,
            new SimpleStreamOptions(ApiKey: "sk-ant-oat-test"));

        var system = Arr(parameters["system"]);
        Assert.Equal("You are Claude Code, Anthropic's official CLI for Claude.", Str(system[0], "text"));
        Assert.Equal("be brief", Str(system[1], "text"));
        Assert.Equal("Read", Str(Arr(parameters["tools"])[0], "name"));
        var betas = Arr(parameters["betas"]).Select(node => node!.GetValue<string>()).ToList();
        Assert.Contains("claude-code-20250219", betas);
        Assert.Contains("oauth-2025-04-20", betas);
    }

    [Fact]
    public void EmitsServerFallbacks()
    {
        var spec = Spec(compat: JsonNode.Parse("""
            {
              "allowedFallbackModels": [
                { "provider": "anthropic", "model": "claude-backup",
                  "cost": { "input": 1, "output": 2 } }
              ]
            }
            """)!.AsObject());
        var parameters = AnthropicMessages.BuildParams(
            spec, new TranscriptContext([Messages.UserText("hi")]), false,
            new SimpleStreamOptions(ApiKey: "sk-ant"));

        var fallbacks = Arr(parameters["fallbacks"]);
        Assert.Equal("claude-backup", Str(fallbacks[0], "model"));
        var betas = Arr(parameters["betas"]).Select(node => node!.GetValue<string>()).ToList();
        Assert.Contains("server-side-fallback-2026-07-01", betas);
    }
}
