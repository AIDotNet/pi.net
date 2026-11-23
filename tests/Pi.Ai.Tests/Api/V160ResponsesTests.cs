using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>
/// v1.6.0 增量：configuration_update 思考档位（openai-responses / openai-codex-responses）、
/// Codex 的 max_output_tokens、OpenAI 扣减 reasoning 的诊断，以及 openai-completions 的
/// developer 角色默认值。
/// </summary>
public class V160ResponsesTests
{
    private static ModelSpec ResponsesModel(JsonObject? compat = null) => new()
    {
        Id = "gpt-6.2",
        Name = "GPT-6.2",
        Api = "openai-responses",
        Provider = "openai",
        BaseUrl = "https://api.openai.com/v1",
        Input = ["text", "image"],
        Reasoning = true,
        ContextWindow = 1_000_000,
        MaxTokens = 128_000,
        Compat = compat,
    };

    private static ModelSpec CodexModel(JsonObject? compat = null) => new()
    {
        Id = "gpt-6.2-sol",
        Name = "GPT-6.2 Sol",
        Api = "openai-codex-responses",
        Provider = "openai-codex",
        BaseUrl = "https://chatgpt.com/backend-api",
        Input = ["text", "image"],
        Reasoning = true,
        ContextWindow = 500_000,
        MaxTokens = 128_000,
        Compat = compat,
    };

    private static TranscriptContext Context(params ChatMessage[] messages) => new(messages);

    private static AssistantMessage Reply(string model, string? providerThinkingLevel) => new(
        [new TextContent("ok")],
        StopReason.Stop,
        Model: model,
        Api: "openai-responses",
        Provider: "openai")
    {
        ProviderThinkingLevel = providerThinkingLevel,
    };

    private static List<JsonObject> Items(JsonArray array, string typeOrRole, bool byType = true)
        => array.OfType<JsonObject>()
            .Where(item => byType ? item.Str("type") == typeOrRole : item.Str("role") == typeOrRole)
            .ToList();

    // ---------------------------------------------------------------------------------------------
    // convertResponsesMessages: configuration_update items
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ConfigurationUpdatesBracketReplayedRepliesAndEndWithTheRequestedLevel()
    {
        var model = ResponsesModel(new JsonObject
        {
            ["supportsMidConvoEffort"] = true,
            ["supportsMidConvoSystemMessages"] = true,
            ["supportsAdditionalTools"] = true,
        }) with
        {
            ThinkingLevelMap = ThinkingLevelMap.FromJsonObject(new JsonObject { ["high"] = "xhigh" }),
        };

        var messages = OpenAiResponsesShared.ConvertResponsesMessages(
            model,
            Context(
                new UserMessage([new TextContent("one")], 1),
                Reply(model.Id, "high"),
                new UserMessage([new TextContent("two")], 2)),
            OpenAiResponses.ProviderIdSet,
            new ConvertResponsesMessagesOptions { ConfigurationUpdateEffort = "low" });

        var updates = Items(messages, "configuration_update");
        Assert.Equal(2, updates.Count);
        // 重放的回复带着 provider 侧档位 → 在它之前放一个同档位项。
        Assert.Equal("high", updates[0].Obj("reasoning")?.Str("effort"));
        // 末尾一项是本次请求的档位。
        Assert.Equal("low", updates[1].Obj("reasoning")?.Str("effort"));

        var indexOfUpdate = messages.IndexOf(updates[0]);
        var indexOfAssistant = messages.IndexOf(Items(messages, "assistant", byType: false)[0]);
        Assert.Equal(indexOfAssistant - 1, indexOfUpdate);
        Assert.Same(updates[1], messages[^1]);
    }

    [Fact]
    public void ConfigurationUpdateBeforeAReplayedReplyIsSkippedForAnotherModel()
    {
        var model = ResponsesModel(new JsonObject { ["supportsMidConvoEffort"] = true });
        var messages = OpenAiResponsesShared.ConvertResponsesMessages(
            model,
            Context(
                new UserMessage([new TextContent("one")], 1),
                Reply("gpt-5.1", "high"),
                new UserMessage([new TextContent("two")], 2)),
            OpenAiResponses.ProviderIdSet,
            new ConvertResponsesMessagesOptions { ConfigurationUpdateEffort = "low" });

        var updates = Items(messages, "configuration_update");
        Assert.Single(updates);
        Assert.Equal("low", updates[0].Obj("reasoning")?.Str("effort"));
    }

    [Fact]
    public void ConfigurationUpdateIsNotSentWithoutTheOption()
    {
        var model = ResponsesModel(new JsonObject { ["supportsMidConvoEffort"] = true });
        var messages = OpenAiResponsesShared.ConvertResponsesMessages(
            model,
            Context(
                new UserMessage([new TextContent("one")], 1),
                Reply(model.Id, "high")),
            OpenAiResponses.ProviderIdSet,
            new ConvertResponsesMessagesOptions());

        Assert.Empty(Items(messages, "configuration_update"));
    }

    // ---------------------------------------------------------------------------------------------
    // openai-responses: effort resolution
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ConfigurationUpdateEffortRequiresCompatFlagAndMapsThinkingLevel()
    {
        var compat = new JsonObject { ["supportsMidConvoEffort"] = true };
        var options = new OpenAiResponsesOptions { ReasoningEffort = "high", ApiKey = "sk-test" };
        var model = ResponsesModel(new JsonObject { ["supportsMidConvoEffort"] = true });

        Assert.Equal("high", OpenAiResponses.GetConfigurationUpdateEffort(model, options, compat));

        var mapped = model with
        {
            ThinkingLevelMap = ThinkingLevelMap.FromJsonObject(new JsonObject { ["high"] = "xhigh" }),
        };
        Assert.Equal("xhigh", OpenAiResponses.GetConfigurationUpdateEffort(mapped, options, compat));

        Assert.Null(OpenAiResponses.GetConfigurationUpdateEffort(model, options, new JsonObject()));
        Assert.Null(OpenAiResponses.GetConfigurationUpdateEffort(model, null, compat));
        // Sign in with ChatGPT 的令牌不接受 configuration_update 项。
        Assert.Null(OpenAiResponses.GetConfigurationUpdateEffort(
            model, options with { ApiKey = "chatgpt-access-token" }, compat));
    }

    [Fact]
    public void BuildParamsSendsMediumEffortWithATrailingConfigurationUpdate()
    {
        var model = ResponsesModel(new JsonObject { ["supportsMidConvoEffort"] = true });
        var compat = OpenAiResponses.GetCompat(model);
        var options = new OpenAiResponsesOptions { ReasoningEffort = "high", ApiKey = "sk-test" };
        var effort = OpenAiResponses.GetConfigurationUpdateEffort(model, options, compat);

        var body = OpenAiResponses.BuildParams(
            model,
            Context(new UserMessage([new TextContent("hi")], 1)),
            options,
            compat,
            configurationUpdateEffort: effort);

        // 档位改由 configuration_update 承载，请求里的 effort 固定在 medium。
        Assert.Equal("medium", body.Obj("reasoning")?.Str("effort"));
        var last = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(body["input"])[^1]);
        Assert.Equal("configuration_update", last.Str("type"));
        Assert.Equal("high", last.Obj("reasoning")?.Str("effort"));
    }

    [Fact]
    public void BuildParamsKeepsTheOffEffortWhenNoLevelIsRequested()
    {
        // 没有请求档位时不得因为 supportsMidConvoEffort 而改成 medium：此时根本不发
        // configuration_update，effort 仍取 thinkingLevelMap.off（缺省 none）。
        var model = ResponsesModel(new JsonObject { ["supportsMidConvoEffort"] = true });
        var compat = OpenAiResponses.GetCompat(model);
        var options = new OpenAiResponsesOptions { ApiKey = "sk-test" };
        var effort = OpenAiResponses.GetConfigurationUpdateEffort(model, options, compat);
        Assert.Null(effort);

        var body = OpenAiResponses.BuildParams(
            model,
            Context(new UserMessage([new TextContent("hi")], 1)),
            options,
            compat,
            configurationUpdateEffort: effort);

        Assert.Equal("none", body.Obj("reasoning")?.Str("effort"));
        var input = Assert.IsType<JsonArray>(body["input"]);
        Assert.DoesNotContain(input.OfType<JsonObject>(), item => item.Str("type") == "configuration_update");
    }

    [Fact]
    public void ConfigurationUpdateGoesBeforeEveryItemOfTheReplayedReply()
    {
        var model = ResponsesModel(new JsonObject { ["supportsMidConvoEffort"] = true });
        var reply = new AssistantMessage(
            [new TextContent("answer"), new ToolCallContent("call-1", "read", new JsonObject())],
            StopReason.ToolUse,
            Model: model.Id,
            Api: "openai-responses",
            Provider: "openai")
        {
            ProviderThinkingLevel = "high",
        };

        var messages = OpenAiResponsesShared.ConvertResponsesMessages(
            model,
            Context(new UserMessage([new TextContent("go")], 1), reply),
            OpenAiResponses.ProviderIdSet,
            new ConvertResponsesMessagesOptions { ConfigurationUpdateEffort = "low" });

        var updateIndex = messages.IndexOf(Items(messages, "configuration_update")[0]);
        var messageIndex = messages.IndexOf(Items(messages, "assistant", byType: false)[0]);
        var callIndex = messages.IndexOf(Items(messages, "function_call")[0]);
        Assert.True(updateIndex < messageIndex);
        Assert.True(messageIndex < callIndex);
    }

    [Fact]
    public void CodexWithoutTheCompatFlagKeepsTheRequestedEffort()
    {
        var model = CodexModel();
        var options = new OpenAiCodexResponsesOptions { ReasoningEffort = "high" };
        Assert.Null(OpenAiCodexResponses.GetConfigurationUpdateEffort(model, options));

        var body = OpenAiCodexResponses.BuildRequestBody(
            model, Context(new UserMessage([new TextContent("hi")], 1)), options, null);
        Assert.Equal("high", body.Obj("reasoning")?.Str("effort"));
        Assert.DoesNotContain(
            Assert.IsType<JsonArray>(body["input"]).OfType<JsonObject>(),
            item => item.Str("type") == "configuration_update");
    }

    // ---------------------------------------------------------------------------------------------
    // openai-codex-responses
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CodexConfigurationUpdateEffortHonorsCompatAndOff()
    {
        var options = new OpenAiCodexResponsesOptions { ReasoningEffort = "high" };
        var model = CodexModel(new JsonObject { ["supportsMidConvoEffort"] = true });
        Assert.Equal("high", OpenAiCodexResponses.GetConfigurationUpdateEffort(model, options));

        Assert.Null(OpenAiCodexResponses.GetConfigurationUpdateEffort(
            CodexModel(), options));
        Assert.Null(OpenAiCodexResponses.GetConfigurationUpdateEffort(
            model, new OpenAiCodexResponsesOptions()));

        Assert.Equal("none", OpenAiCodexResponses.ResolveReasoningEffort(CodexModel(), "none"));
        var offMap = CodexModel() with
        {
            ThinkingLevelMap = ThinkingLevelMap.FromJsonObject(new JsonObject { ["off"] = "low" }),
        };
        Assert.Equal("low", OpenAiCodexResponses.ResolveReasoningEffort(offMap, "none"));
    }

    [Fact]
    public void CodexBuildRequestBodySendsFixedEffortAndConfigurationUpdates()
    {
        var model = CodexModel(new JsonObject
        {
            ["supportsMidConvoEffort"] = true,
            ["supportsMidConvoSystemMessages"] = true,
        });
        var options = new OpenAiCodexResponsesOptions { ReasoningEffort = "high" };
        var effort = OpenAiCodexResponses.GetConfigurationUpdateEffort(model, options);

        var body = OpenAiCodexResponses.BuildRequestBody(
            model,
            Context(new UserMessage([new TextContent("hi")], 1)),
            options,
            null,
            configurationUpdateEffort: effort);

        Assert.Equal("medium", body.Obj("reasoning")?.Str("effort"));
        var input = Assert.IsType<JsonArray>(body["input"]);
        var last = Assert.IsType<JsonObject>(input[^1]);
        Assert.Equal("configuration_update", last.Str("type"));
        Assert.Equal("high", last.Obj("reasoning")?.Str("effort"));
    }

    [Fact]
    public void CodexForwardsMaxOutputTokensAboveTheResponsesMinimum()
    {
        var model = CodexModel();
        var context = Context(new UserMessage([new TextContent("hi")], 1));

        var clamped = OpenAiCodexResponses.BuildRequestBody(
            model, context, new OpenAiCodexResponsesOptions { MaxTokens = 8 }, null);
        Assert.Equal(16, clamped.Num("max_output_tokens"));

        var forwarded = OpenAiCodexResponses.BuildRequestBody(
            model, context, new OpenAiCodexResponsesOptions { MaxTokens = 4096 }, null);
        Assert.Equal(4096, forwarded.Num("max_output_tokens"));

        var unsupported = model with
        {
            Compat = new JsonObject { ["supportsMaxOutputTokens"] = false },
        };
        var omitted = OpenAiCodexResponses.BuildRequestBody(
            unsupported, context, new OpenAiCodexResponsesOptions { MaxTokens = 4096 }, null);
        Assert.Null(omitted["max_output_tokens"]);
    }

    // ---------------------------------------------------------------------------------------------
    // OpenAI reasoning removals
    // ---------------------------------------------------------------------------------------------

    private static async IAsyncEnumerable<AiSseEvent> Sse(params string[] payloads)
    {
        foreach (var payload in payloads) yield return new AiSseEvent(payload);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CompletedResponseRecordsReasoningRemovalsButNotTurnBoundary()
    {
        var completed = new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = new JsonObject
            {
                ["id"] = "resp_1",
                ["status"] = "completed",
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 10,
                    ["output_tokens"] = 2,
                    ["total_tokens"] = 12,
                },
                ["input_transformations"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "reasoning",
                        ["id"] = "rs_1",
                        ["action"] = "removed",
                        ["reason"] = "prefix_mismatch",
                    },
                    new JsonObject
                    {
                        ["type"] = "reasoning",
                        ["id"] = "rs_2",
                        ["action"] = "removed",
                        ["reason"] = "turn_boundary",
                    },
                    new JsonObject { ["type"] = "text", ["action"] = "removed" }),
            },
        };

        var stream = new AssistantMessageEventStream();
        var output = new OpenAiResponsesShared.MutableAssistantMessage(
            "gpt-6.2", "openai-responses", "openai");
        await OpenAiResponsesShared.ProcessResponsesStream(
            Sse(completed.ToJsonString()), output, stream, ResponsesModel());
        stream.End(output.Snapshot());

        var diagnostic = Assert.Single(output.Snapshot().Diagnostics!);
        Assert.Equal("openai_input_transformations", diagnostic.Type);
        var transformations = Assert.IsType<JsonArray>(diagnostic.Details!["transformations"]);
        var removal = Assert.Single(transformations.OfType<JsonObject>());
        Assert.Equal("rs_1", removal.Str("id"));
        Assert.Equal("removed", removal.Str("action"));
        Assert.Equal("prefix_mismatch", removal.Str("reason"));
    }

    // ---------------------------------------------------------------------------------------------
    // openai-completions: developer role default
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void DeveloperRoleDefaultsToOpenAiOnly()
    {
        static ModelSpec Spec(string provider, string baseUrl) => new()
        {
            Id = "m", Name = "M", Api = "openai-completions", Provider = provider, BaseUrl = baseUrl,
        };

        Assert.True(OpenAiCompletions.GetCompat(Spec("openai", "https://api.openai.com/v1")).SupportsDeveloperRole);
        // 自定义 provider 指向 OpenAI 自己的端点时同样享受 developer 角色。
        Assert.True(OpenAiCompletions.GetCompat(Spec("custom", "https://api.openai.com/v1")).SupportsDeveloperRole);
        Assert.False(OpenAiCompletions.GetCompat(Spec("openrouter", "https://openrouter.ai/api/v1")).SupportsDeveloperRole);
        Assert.False(OpenAiCompletions.GetCompat(Spec("groq", "https://api.groq.com/openai/v1")).SupportsDeveloperRole);
        Assert.False(OpenAiCompletions.GetCompat(Spec("azure", "https://x.openai.azure.com")).SupportsDeveloperRole);
        // 显式覆盖仍然优先。
        var overridden = Spec("groq", "https://api.groq.com/openai/v1") with
        {
            Compat = new JsonObject { ["supportsDeveloperRole"] = true },
        };
        Assert.True(OpenAiCompletions.GetCompat(overridden).SupportsDeveloperRole);
    }
}
