using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>
/// OpenAI Chat Completions 兼容矩阵（compat 缺省值解析结果）。对应 TS
/// <c>ResolvedOpenAICompletionsCompat</c>（<c>openai-completions.ts</c>）。
/// </summary>
public sealed record OpenAiCompletionsCompat
{
    public bool SupportsStore { get; init; } = true;

    public bool SupportsDeveloperRole { get; init; }

    public bool SupportsReasoningEffort { get; init; } = true;

    public bool SupportsUsageInStreaming { get; init; } = true;

    public bool SupportsFinishReason { get; init; } = true;

    /// <summary><c>max_tokens</c> 或 <c>max_completion_tokens</c>。对应 TS <c>maxTokensField</c>。</summary>
    public string MaxTokensField { get; init; } = "max_completion_tokens";

    public bool RequiresToolResultName { get; init; }

    public bool RequiresAssistantAfterToolResult { get; init; }

    public bool RequiresThinkingAsText { get; init; }

    public bool RequiresReasoningContentOnAssistantMessages { get; init; }

    /// <summary>openai / openrouter / deepseek / together / baseten / zai / qwen / qwen-chat-template / chat-template / string-thinking / ant-ling。</summary>
    public string ThinkingFormat { get; init; } = "openai";

    public JsonObject? OpenRouterRouting { get; init; }

    public JsonObject? VercelGatewayRouting { get; init; }

    public JsonObject? ChatTemplateKwargs { get; init; }

    public JsonObject? ChatTemplateArgs { get; init; }

    public bool ZaiToolStream { get; init; }

    public bool SupportsThinkingTokenBudget { get; init; }

    /// <summary>顶层思考预算字段名。对应 TS <c>thinkingTokenBudgetField</c>。</summary>
    public string? ThinkingTokenBudgetField { get; init; }

    public bool SupportsStrictMode { get; init; }

    public bool SupportsOpenAIGrammarTools { get; init; }

    public bool SupportsMidConvoSystemMessages { get; init; }

    public bool SupportsMidConvoToolAdditions { get; init; }

    /// <summary><c>anthropic</c> 时在 system / 最后消息 / 最后工具上打 cache_control。</summary>
    public string? CacheControlFormat { get; init; }

    public bool SendSessionAffinityHeaders { get; init; }

    public string? SessionAffinityFormat { get; init; }

    public bool SupportsLongCacheRetention { get; init; } = true;

    public JsonNode? VllmPriority { get; init; }
}

/// <summary>
/// OpenAI Chat Completions provider。对应 TS <c>api/openai-completions.ts</c>：
/// compat 探测矩阵、消息转换（工具 ID 归一化 / reasoning_details 回放 / Kimi 中段工具）、
/// thinkingFormat 分支、Anthropic 风格 cache_control、grammar 自定义工具、
/// SSE delta 聚合、用量/成本与 finish_reason 语义。
/// 直接以 HttpClient 调 REST（SDK 仅在 <c>baseURL</c> 后追加 <c>/chat/completions</c>）。
/// </summary>
public static class OpenAiCompletions
{
    private const string DefaultBaseUrl = "https://api.openai.com/v1";

    private static readonly string[] ReasoningFields = ["reasoning", "reasoning_content", "reasoning_text"];

    private static readonly string[] StreamReasoningFields = ["reasoning_content", "reasoning", "reasoning_text"];

    private static bool HasHeader(IReadOnlyDictionary<string, string?>? headers, string name)
    {
        if (headers is null) return false;
        foreach (var (key, value) in headers)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)
                && value is not null && value.Trim().Length > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static string GetClientApiKey(
        string provider, string? apiKey, IReadOnlyDictionary<string, string?>? headers)
    {
        if (!string.IsNullOrEmpty(apiKey)) return apiKey;
        if (HasHeader(headers, "authorization") || HasHeader(headers, "cf-aig-authorization")) return "unused";
        throw new InvalidOperationException($"No API key for provider: {provider}");
    }

    private static bool HasToolHistory(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message is ToolResultMessage) return true;
            if (message is AssistantMessage assistant
                && assistant.Content.Any(block => block is ToolCallContent))
            {
                return true;
            }
        }
        return false;
    }

    private static string ResolveCacheRetention(string? cacheRetention, IReadOnlyDictionary<string, string>? env)
        => cacheRetention
           ?? (ProviderEnvValue.Get("PI_CACHE_RETENTION", env) == "long" ? "long" : "short");

    private static bool HasReasoningField(string? field)
        => field is not null && ReasoningFields.Contains(field, StringComparer.Ordinal);

    private static bool IsJsonString(JsonNode? node, out string value)
    {
        if (node is JsonValue primitive && primitive.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }
        value = "";
        return false;
    }

    // ---------------------------------------------------------------------
    // compat：探测 + 显式覆盖
    // ---------------------------------------------------------------------

    /// <summary>按 provider / baseUrl 探测 compat 缺省值。对应 TS <c>detectCompat</c>。</summary>
    private static OpenAiCompletionsCompat DetectCompat(ModelSpec model)
    {
        var provider = model.Provider;
        var baseUrl = model.BaseUrl;

        bool Has(string value) => baseUrl.Contains(value, StringComparison.Ordinal);

        var isZai = provider is "zai" or "zai-coding-cn"
            || Has("api.z.ai") || Has("open.bigmodel.cn");
        var isTogether = provider == "together" || Has("api.together.ai") || Has("api.together.xyz");
        var isMoonshot = provider is "moonshotai" or "moonshotai-cn" || Has("api.moonshot.");
        var isOpenRouter = provider == "openrouter" || Has("openrouter.ai");
        var isCloudflareWorkersAi = provider == "cloudflare-workers-ai" || Has("api.cloudflare.com");
        var isCloudflareAiGateway = provider == "cloudflare-ai-gateway" || Has("gateway.ai.cloudflare.com");
        var isNvidia = provider == "nvidia" || Has("integrate.api.nvidia.com");
        var isAntLing = provider == "ant-ling" || Has("api.ant-ling.com");
        var isCerebras = provider == "cerebras" || Has("cerebras.ai");
        var isDeepSeek = provider == "deepseek"
            || baseUrl.ToLowerInvariant().Contains("deepseek.com", StringComparison.Ordinal);

        var isNonStandard = isNvidia
            || isCerebras
            || provider == "xai"
            || Has("api.x.ai")
            || isTogether
            || Has("chutes.ai")
            || isDeepSeek
            || isZai
            || isMoonshot
            || provider == "opencode"
            || Has("opencode.ai")
            || isCloudflareWorkersAi
            || isCloudflareAiGateway
            || isAntLing;

        var useMaxTokens = Has("chutes.ai")
            || isDeepSeek
            || isMoonshot
            || isCloudflareAiGateway
            || isTogether
            || isNvidia
            || isAntLing
            || isZai;

        var isGrok = provider == "xai" || Has("api.x.ai");
        var isOpenRouterDeveloperRoleModel = isOpenRouter
            && (model.Id.StartsWith("anthropic/", StringComparison.Ordinal)
                || model.Id.StartsWith("openai/", StringComparison.Ordinal));
        var cacheControlFormat = provider == "openrouter"
            && model.Id.StartsWith("anthropic/", StringComparison.Ordinal)
            ? "anthropic"
            : null;

        var thinkingFormat = isDeepSeek
            ? "deepseek"
            : isZai
                ? "zai"
                : isTogether
                    ? "together"
                    : isAntLing
                        ? "ant-ling"
                        : isOpenRouter
                            ? "openrouter"
                            : "openai";

        return new OpenAiCompletionsCompat
        {
            SupportsStore = !isNonStandard,
            SupportsDeveloperRole = isOpenRouterDeveloperRoleModel || (!isNonStandard && !isOpenRouter),
            SupportsReasoningEffort = !isGrok && !isZai && !isMoonshot && !isTogether
                && !isCloudflareAiGateway && !isNvidia && !isAntLing,
            SupportsUsageInStreaming = true,
            SupportsFinishReason = true,
            MaxTokensField = useMaxTokens ? "max_tokens" : "max_completion_tokens",
            RequiresToolResultName = false,
            RequiresAssistantAfterToolResult = false,
            RequiresThinkingAsText = false,
            RequiresReasoningContentOnAssistantMessages = isDeepSeek,
            ThinkingFormat = thinkingFormat,
            OpenRouterRouting = [],
            VercelGatewayRouting = [],
            ChatTemplateKwargs = [],
            ChatTemplateArgs = [],
            ZaiToolStream = false,
            SupportsThinkingTokenBudget = false,
            ThinkingTokenBudgetField = null,
            SupportsStrictMode = false,
            SupportsOpenAIGrammarTools = false,
            SupportsMidConvoSystemMessages = false,
            SupportsMidConvoToolAdditions = false,
            CacheControlFormat = cacheControlFormat,
            SendSessionAffinityHeaders = isOpenRouter,
            SessionAffinityFormat = isOpenRouter ? "openrouter" : "openai",
            SupportsLongCacheRetention = !(isTogether
                || isCloudflareWorkersAi
                || isCloudflareAiGateway
                || isNvidia
                || isAntLing),
        };
    }

    /// <summary>探测缺省值并以 <see cref="ModelSpec.Compat"/> 的显式条目覆盖。对应 TS <c>getCompat</c>。</summary>
    public static OpenAiCompletionsCompat GetCompat(ModelSpec model)
    {
        var detected = DetectCompat(model);
        var compat = model.Compat;
        if (compat is null) return detected;

        return new OpenAiCompletionsCompat
        {
            SupportsStore = compat.Bool("supportsStore") ?? detected.SupportsStore,
            SupportsDeveloperRole = compat.Bool("supportsDeveloperRole") ?? detected.SupportsDeveloperRole,
            SupportsReasoningEffort = compat.Bool("supportsReasoningEffort") ?? detected.SupportsReasoningEffort,
            SupportsUsageInStreaming = compat.Bool("supportsUsageInStreaming") ?? detected.SupportsUsageInStreaming,
            SupportsFinishReason = compat.Bool("supportsFinishReason") ?? detected.SupportsFinishReason,
            MaxTokensField = compat.Str("maxTokensField") ?? detected.MaxTokensField,
            RequiresToolResultName = compat.Bool("requiresToolResultName") ?? detected.RequiresToolResultName,
            RequiresAssistantAfterToolResult = compat.Bool("requiresAssistantAfterToolResult")
                ?? detected.RequiresAssistantAfterToolResult,
            RequiresThinkingAsText = compat.Bool("requiresThinkingAsText") ?? detected.RequiresThinkingAsText,
            RequiresReasoningContentOnAssistantMessages =
                compat.Bool("requiresReasoningContentOnAssistantMessages")
                ?? detected.RequiresReasoningContentOnAssistantMessages,
            ThinkingFormat = compat.Str("thinkingFormat") ?? detected.ThinkingFormat,
            OpenRouterRouting = compat.Obj("openRouterRouting") ?? detected.OpenRouterRouting,
            VercelGatewayRouting = compat.Obj("vercelGatewayRouting") ?? detected.VercelGatewayRouting,
            ChatTemplateKwargs = compat.Obj("chatTemplateKwargs") ?? detected.ChatTemplateKwargs,
            ChatTemplateArgs = compat.Obj("chatTemplateArgs") ?? detected.ChatTemplateArgs,
            ZaiToolStream = compat.Bool("zaiToolStream") ?? detected.ZaiToolStream,
            SupportsThinkingTokenBudget = compat.Bool("supportsThinkingTokenBudget")
                ?? detected.SupportsThinkingTokenBudget,
            ThinkingTokenBudgetField = compat.Str("thinkingTokenBudgetField") ?? detected.ThinkingTokenBudgetField,
            SupportsStrictMode = compat.Bool("supportsStrictMode") ?? detected.SupportsStrictMode,
            SupportsOpenAIGrammarTools = compat.Bool("supportsOpenAIGrammarTools")
                ?? detected.SupportsOpenAIGrammarTools,
            SupportsMidConvoSystemMessages = compat.Bool("supportsMidConvoSystemMessages")
                ?? detected.SupportsMidConvoSystemMessages,
            SupportsMidConvoToolAdditions = compat.Bool("supportsMidConvoToolAdditions")
                ?? detected.SupportsMidConvoToolAdditions,
            CacheControlFormat = compat.Str("cacheControlFormat") ?? detected.CacheControlFormat,
            SendSessionAffinityHeaders = compat.Bool("sendSessionAffinityHeaders")
                ?? detected.SendSessionAffinityHeaders,
            SessionAffinityFormat = compat.Str("sessionAffinityFormat") ?? detected.SessionAffinityFormat,
            SupportsLongCacheRetention = compat.Bool("supportsLongCacheRetention")
                ?? detected.SupportsLongCacheRetention,
            VllmPriority = compat.TryGetPropertyValue("vllmPriority", out var priority)
                ? priority?.DeepClone()
                : null,
        };
    }

    // ---------------------------------------------------------------------
    // reasoning_details 回放辅助
    // ---------------------------------------------------------------------

    private static bool IsReasoningDetail(JsonNode? detail)
    {
        if (detail is not JsonObject candidate) return false;

        if (candidate.TryGetPropertyValue("id", out var id)
            && id is not null && !IsJsonString(id, out _))
        {
            return false;
        }
        if (candidate.TryGetPropertyValue("format", out var format)
            && !IsJsonString(format, out _))
        {
            return false;
        }
        if (candidate.TryGetPropertyValue("index", out var index)
            && !(index is JsonValue number && number.TryGetValue<double>(out _)))
        {
            return false;
        }

        return candidate.Str("type") switch
        {
            "reasoning.summary" => IsJsonString(candidate["summary"], out _),
            "reasoning.encrypted" => IsJsonString(candidate["data"], out _),
            "reasoning.text" => IsJsonString(candidate["text"], out _)
                && (candidate["signature"] is null || IsJsonString(candidate["signature"], out _)),
            _ => false,
        };
    }

    private static List<JsonObject>? ParseReasoningDetails(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        try
        {
            if (JsonNode.Parse(signature) is not JsonArray array || array.Count == 0) return null;
            var details = new List<JsonObject>();
            foreach (var entry in array)
            {
                if (entry is not JsonObject detail || !IsReasoningDetail(detail)) return null;
                details.Add((JsonObject)detail.DeepClone());
            }
            return details;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? ParseLegacyEncryptedReasoningDetail(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        try
        {
            if (JsonNode.Parse(signature) is not JsonObject detail || !IsReasoningDetail(detail)) return null;
            if (detail.Str("type") != "reasoning.encrypted") return null;
            if (detail.Str("id") is not { Length: > 0 }) return null;
            if (detail.Str("data") is not { Length: > 0 }) return null;
            return detail;
        }
        catch
        {
            return null;
        }
    }

    private static void FillMissingReasoningDetailFields(JsonObject target, JsonObject source)
    {
        if (target["id"] is null && source["id"] is { } id) target["id"] = id.DeepClone();
        if (string.IsNullOrEmpty(target.Str("format")) && source.Str("format") is { } format)
        {
            target["format"] = format;
        }
        if (target["index"] is null && source["index"] is { } index) target["index"] = index.DeepClone();
    }

    private static void AppendReasoningDetail(List<JsonObject> details, JsonObject detail)
    {
        var last = details.Count > 0 ? details[^1] : null;
        var detailType = detail.Str("type");
        if (detailType == "reasoning.text" && last?.Str("type") == "reasoning.text")
        {
            last["text"] = (last.Str("text") ?? "") + (detail.Str("text") ?? "");
            if (string.IsNullOrEmpty(last.Str("signature")) && detail.Str("signature") is { } signature)
            {
                last["signature"] = signature;
            }
            FillMissingReasoningDetailFields(last, detail);
            return;
        }
        if (detailType == "reasoning.summary" && last?.Str("type") == "reasoning.summary")
        {
            last["summary"] = (last.Str("summary") ?? "") + (detail.Str("summary") ?? "");
            FillMissingReasoningDetailFields(last, detail);
            return;
        }
        details.Add((JsonObject)detail.DeepClone());
    }

    private static string SerializeReasoningDetails(List<JsonObject> details)
        => new JsonArray(details.Select(detail => (JsonNode)detail.DeepClone()).ToArray()).ToJsonString();

    // ---------------------------------------------------------------------
    // 缓存控制（OpenRouter 的 Anthropic 模型）
    // ---------------------------------------------------------------------

    private static JsonObject? GetCompatCacheControl(OpenAiCompletionsCompat compat, string cacheRetention)
    {
        if (compat.CacheControlFormat != "anthropic" || cacheRetention == "none") return null;
        var cacheControl = new JsonObject { ["type"] = "ephemeral" };
        if (cacheRetention == "long" && compat.SupportsLongCacheRetention)
        {
            cacheControl["ttl"] = "1h";
        }
        return cacheControl;
    }

    private static void ApplyAnthropicCacheControl(JsonArray messages, JsonArray? tools, JsonObject cacheControl)
    {
        AddCacheControlToSystemPrompt(messages, cacheControl);
        AddCacheControlToLastTool(tools, cacheControl);
        AddCacheControlToLastConversationMessage(messages, cacheControl);
    }

    private static void AddCacheControlToSystemPrompt(JsonArray messages, JsonObject cacheControl)
    {
        foreach (var node in messages)
        {
            if (node is not JsonObject message) continue;
            var role = message.Str("role");
            if (role is not ("system" or "developer")) continue;
            AddCacheControlToTextContent(message, cacheControl);
            return;
        }
    }

    private static void AddCacheControlToLastConversationMessage(JsonArray messages, JsonObject cacheControl)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index] is not JsonObject message) continue;
            var role = message.Str("role");
            if (role is not ("user" or "assistant" or "tool")) continue;
            if (AddCacheControlToTextContent(message, cacheControl)) return;
        }
    }

    private static void AddCacheControlToLastTool(JsonArray? tools, JsonObject cacheControl)
    {
        if (tools is not { Count: > 0 }) return;
        if (tools[^1] is JsonObject lastTool)
        {
            lastTool["cache_control"] = cacheControl.DeepClone();
        }
    }

    private static bool AddCacheControlToTextContent(JsonObject message, JsonObject cacheControl)
    {
        var content = message["content"];
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (text.Length == 0) return false;
            message["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
                ["cache_control"] = cacheControl.DeepClone(),
            });
            return true;
        }

        if (content is not JsonArray parts) return false;
        for (var index = parts.Count - 1; index >= 0; index--)
        {
            if (parts[index] is JsonObject part && part.Str("type") == "text")
            {
                part["cache_control"] = cacheControl.DeepClone();
                return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------------
    // 工具与消息转换
    // ---------------------------------------------------------------------

    /// <summary>工具声明转换（grammar 自定义工具 / strict JSON schema）。对应 TS <c>convertTools</c>。</summary>
    private static JsonArray ConvertTools(IReadOnlyList<ToolDefinition> tools, OpenAiCompletionsCompat compat)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            var grammar = ConstrainedSampling.ResolveGrammarConstrainedSampling(
                tool, compat.SupportsOpenAIGrammarTools);
            if (grammar is not null)
            {
                result.Add(new JsonObject
                {
                    ["type"] = "custom",
                    ["custom"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["format"] = new JsonObject
                        {
                            ["type"] = "grammar",
                            ["grammar"] = new JsonObject
                            {
                                ["syntax"] = grammar.Format,
                                ["definition"] = grammar.Definition,
                            },
                        },
                    },
                });
                continue;
            }

            var strict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(
                tool, compat.SupportsStrictMode);
            var function = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict),
            };
            if (compat.SupportsStrictMode)
            {
                function["strict"] = strict ?? false;
            }
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = function,
            });
        }
        return result;
    }

    private static string NormalizeToolCallId(ModelSpec model, string id)
    {
        if (id.Contains('|'))
        {
            var separatorIndex = id.IndexOf('|');
            var callId = SanitizeIdPart(id[..separatorIndex]);
            var itemId = SanitizeIdPart(id[(separatorIndex + 1)..]);
            var combinedId = itemId.Length > 0 ? $"{callId}_{itemId}" : callId;
            if (combinedId.Length <= 40) return combinedId;

            var hash = Hash.ShortHash(id);
            if (hash.Length > 8) hash = hash[..8];
            var prefixLength = Math.Max(1, 40 - hash.Length - 1);
            var prefix = callId.Length > prefixLength ? callId[..prefixLength] : callId;
            return $"{prefix}_{hash}";
        }

        if (model.Provider == "openai" && id.Length > 40) return id[..40];
        return id;
    }

    private static string SanitizeIdPart(string value)
        => new(value.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray());

    private static string StringifyArguments(object? arguments) => arguments switch
    {
        null => "{}",
        JsonNode node => node.ToJsonString(),
        JsonElement element => element.GetRawText(),
        string text => JsonSerializer.Serialize(text),
        _ => JsonSerializer.Serialize(arguments),
    };

    /// <summary>
    /// 消息转换。对应 TS <c>convertMessages</c>：工具 ID 归一化、Kimi 中段工具 system 消息、
    /// reasoning_details / reasoning 字段回放、tool result 分组与图片转用户消息。
    /// </summary>
    public static JsonArray ConvertMessages(
        ModelSpec model,
        TranscriptContext context,
        OpenAiCompletionsCompat compat,
        IReadOnlyDictionary<string, string>? grammarToolInputProperties = null)
    {
        var normalizedContext = Transcript.ResolveTranscript(context, compat.SupportsMidConvoSystemMessages);
        var parameters = new JsonArray();

        var transformedMessages = TransformMessages.Transform(
            normalizedContext.Messages, model, (id, _, _) => NormalizeToolCallId(model, id));
        var transcriptTools = Transcript.ResolveTranscriptTools(
            normalizedContext.Messages,
            compat.SupportsMidConvoSystemMessages && compat.SupportsMidConvoToolAdditions
                ? Transcript.TranscriptToolSupport.Additions
                : Transcript.TranscriptToolSupport.None);
        var instructionRole = model.Reasoning && compat.SupportsDeveloperRole ? "developer" : "system";

        string? lastRole = null;
        var systemIndex = 0;

        for (var i = 0; i < transformedMessages.Count; i++)
        {
            var msg = transformedMessages[i];

            if (compat.RequiresAssistantAfterToolResult && lastRole == "toolResult" && msg is UserMessage)
            {
                parameters.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = "I have processed the tool results.",
                });
            }

            switch (msg)
            {
                case SystemMessage systemMessage:
                {
                    // TransformMessages 保持系统消息顺序，所以它们的序号与 transcript 一致。
                    var addedTools = systemIndex++ >= transcriptTools.FoldedSystemMessages
                        ? systemMessage.ToolsAdded ?? []
                        : [];
                    if (addedTools.Count > 0)
                    {
                        parameters.Add(new JsonObject
                        {
                            ["role"] = "system",
                            ["tools"] = ConvertTools(addedTools, compat),
                        });
                    }

                    var text = i == 0
                        ? Text.GetSystemMessageText(systemMessage)
                        : Text.RenderSystemMessageUpdate(systemMessage);
                    if (text.Length > 0)
                    {
                        parameters.Add(new JsonObject
                        {
                            ["role"] = instructionRole,
                            ["content"] = SanitizeUnicode.SanitizeSurrogates(text),
                        });
                    }
                    lastRole = "system";
                    continue;
                }

                case UserMessage userMessage:
                {
                    var blocks = userMessage.Content.ToBlocks(out var wasText);
                    if (wasText)
                    {
                        parameters.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = SanitizeUnicode.SanitizeSurrogates(
                                userMessage.Content.TextValue ?? ""),
                        });
                        lastRole = "user";
                        continue;
                    }

                    var content = new JsonArray();
                    foreach (var block in blocks)
                    {
                        switch (block)
                        {
                            case TextContent text when text.Text.Length > 0:
                                content.Add(new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = SanitizeUnicode.SanitizeSurrogates(text.Text),
                                });
                                break;
                            case ImageContent image:
                                content.Add(new JsonObject
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new JsonObject
                                    {
                                        ["url"] = $"data:{image.MimeType ?? "image/png"};base64,{image.Data}",
                                    },
                                });
                                break;
                        }
                    }
                    if (content.Count == 0) continue;
                    parameters.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = content,
                    });
                    lastRole = "user";
                    continue;
                }

                case AssistantMessage assistant:
                {
                    var assistantMessage = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = compat.RequiresAssistantAfterToolResult ? "" : null,
                    };

                    var assistantTextParts = new List<JsonObject>();
                    foreach (var text in assistant.Content.OfType<TextContent>())
                    {
                        if (text.Text.Trim().Length == 0) continue;
                        assistantTextParts.Add(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = SanitizeUnicode.SanitizeSurrogates(text.Text),
                        });
                    }
                    var assistantText = string.Concat(
                        assistantTextParts.Select(part => part.Str("text") ?? ""));

                    var thinkingBlocks = assistant.Content.OfType<ThinkingContent>().ToList();
                    var toolCalls = assistant.Content.OfType<ToolCallContent>().ToList();

                    List<JsonObject>? signedReasoningDetails = null;
                    foreach (var block in thinkingBlocks)
                    {
                        signedReasoningDetails = ParseReasoningDetails(block.Signature);
                        if (signedReasoningDetails is not null) break;
                    }
                    var legacyReasoningDetails = toolCalls
                        .Select(call => ParseLegacyEncryptedReasoningDetail(call.ThoughtSignature))
                        .Where(detail => detail is not null)
                        .Select(detail => detail!)
                        .ToList();
                    var preservedReasoningDetails = signedReasoningDetails
                        ?? (legacyReasoningDetails.Count > 0 ? legacyReasoningDetails : null);

                    var nonEmptyThinking = thinkingBlocks
                        .Where(block => block.Thinking.Trim().Length > 0)
                        .ToList();
                    if (nonEmptyThinking.Count > 0)
                    {
                        if (compat.RequiresThinkingAsText)
                        {
                            var thinkingText = SanitizeUnicode.SanitizeSurrogates(
                                string.Join("\n\n", nonEmptyThinking.Select(block => block.Thinking)));
                            var content = new JsonArray(new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = thinkingText,
                            });
                            foreach (var part in assistantTextParts) content.Add(part);
                            assistantMessage["content"] = content;
                        }
                        else
                        {
                            if (assistantText.Length > 0)
                            {
                                assistantMessage["content"] = assistantText;
                            }

                            if (preservedReasoningDetails is null)
                            {
                                var signature = nonEmptyThinking[0].Signature;
                                if (model.Provider == "opencode-go" && signature == "reasoning")
                                {
                                    signature = "reasoning_content";
                                }
                                if (HasReasoningField(signature))
                                {
                                    assistantMessage[signature!] = string.Join(
                                        "\n", nonEmptyThinking.Select(block => block.Thinking));
                                }
                            }
                        }
                    }
                    else if (assistantText.Length > 0)
                    {
                        assistantMessage["content"] = assistantText;
                    }

                    if (toolCalls.Count > 0)
                    {
                        var toolCallArray = new JsonArray();
                        foreach (var toolCall in toolCalls)
                        {
                            string? customInputProperty = null;
                            if (grammarToolInputProperties is not null
                                && grammarToolInputProperties.TryGetValue(toolCall.Name, out var property))
                            {
                                customInputProperty = property;
                            }

                            if (customInputProperty is not null)
                            {
                                toolCallArray.Add(new JsonObject
                                {
                                    ["id"] = toolCall.Id,
                                    ["type"] = "custom",
                                    ["custom"] = new JsonObject
                                    {
                                        ["name"] = toolCall.Name,
                                        ["input"] = SanitizeUnicode.SanitizeSurrogates(
                                            ConstrainedSampling.GetGrammarToolInput(
                                                toolCall.Name, toolCall.Arguments, customInputProperty) ?? ""),
                                    },
                                });
                            }
                            else
                            {
                                toolCallArray.Add(new JsonObject
                                {
                                    ["id"] = toolCall.Id,
                                    ["type"] = "function",
                                    ["function"] = new JsonObject
                                    {
                                        ["name"] = toolCall.Name,
                                        ["arguments"] = StringifyArguments(toolCall.Arguments),
                                    },
                                });
                            }
                        }
                        assistantMessage["tool_calls"] = toolCallArray;
                    }

                    if (preservedReasoningDetails is not null)
                    {
                        assistantMessage["reasoning_details"] = new JsonArray(
                            preservedReasoningDetails
                                .Select(detail => (JsonNode)detail.DeepClone())
                                .ToArray());
                    }

                    if (compat.RequiresReasoningContentOnAssistantMessages
                        && model.Reasoning
                        && !assistantMessage.ContainsKey("reasoning_content"))
                    {
                        assistantMessage["reasoning_content"] = "";
                    }

                    var contentNode = assistantMessage["content"];
                    var hasContent = contentNode switch
                    {
                        JsonValue value when value.TryGetValue<string>(out var text) => text.Length > 0,
                        JsonArray array => array.Count > 0,
                        _ => false,
                    };
                    if (!hasContent
                        && (assistantMessage["tool_calls"] is not JsonArray { Count: > 0 }))
                    {
                        continue;
                    }
                    parameters.Add(assistantMessage);
                    lastRole = "assistant";
                    continue;
                }

                case ToolResultMessage:
                {
                    var imageBlocks = new JsonArray();
                    var j = i;
                    for (; j < transformedMessages.Count
                           && transformedMessages[j] is ToolResultMessage; j++)
                    {
                        var toolMessage = (ToolResultMessage)transformedMessages[j];
                        var textResult = string.Join("\n",
                            toolMessage.Content.OfType<TextContent>().Select(block => block.Text));
                        var hasImages = toolMessage.Content.Any(block => block is ImageContent);
                        // 仅空白的工具输出（如 bash 的裸 \r\n）按空处理，否则部分 provider 会 400（上游 #8720）。
                        var toolResultText = textResult.Trim().Length > 0
                            ? textResult
                            : hasImages
                                ? "(see attached image)"
                                : "(no tool output)";

                        var toolResultMessage = new JsonObject
                        {
                            ["role"] = "tool",
                            ["content"] = SanitizeUnicode.SanitizeSurrogates(toolResultText),
                            ["tool_call_id"] = toolMessage.ToolCallId,
                        };
                        if (compat.RequiresToolResultName && toolMessage.ToolName.Length > 0)
                        {
                            toolResultMessage["name"] = toolMessage.ToolName;
                        }
                        parameters.Add(toolResultMessage);

                        if (hasImages && model.Input.Contains(ModelInput.Image))
                        {
                            foreach (var image in toolMessage.Content.OfType<ImageContent>())
                            {
                                imageBlocks.Add(new JsonObject
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new JsonObject
                                    {
                                        ["url"] = $"data:{image.MimeType ?? "image/png"};base64,{image.Data}",
                                    },
                                });
                            }
                        }
                    }

                    i = j - 1;

                    if (imageBlocks.Count > 0)
                    {
                        if (compat.RequiresAssistantAfterToolResult)
                        {
                            parameters.Add(new JsonObject
                            {
                                ["role"] = "assistant",
                                ["content"] = "I have processed the tool results.",
                            });
                        }

                        var content = new JsonArray(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Attached image(s) from tool result:",
                        });
                        foreach (var image in imageBlocks) content.Add(image!.DeepClone()!);
                        parameters.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = content,
                        });
                        lastRole = "user";
                    }
                    else
                    {
                        lastRole = "toolResult";
                    }
                    continue;
                }

                default:
                    lastRole = msg is SystemMessage ? "system" : msg is UserMessage ? "user" : "assistant";
                    continue;
            }
        }

        return parameters;
    }

    // ---------------------------------------------------------------------
    // buildParams
    // ---------------------------------------------------------------------

    private static string? ResolveThinkingTokenBudgetField(OpenAiCompletionsCompat compat)
        => compat.ThinkingTokenBudgetField
           ?? (compat.SupportsThinkingTokenBudget ? "thinking_token_budget" : null);

    private static long? ResolveClampedThinkingBudget(
        ModelSpec model, SimpleStreamOptions? options, JsonObject parameters)
    {
        if (options?.ReasoningEffort is null || !model.Reasoning) return null;
        var ceiling = parameters.Num("max_tokens")
            ?? parameters.Num("max_completion_tokens")
            ?? model.MaxTokens;
        var budget = SimpleOptions.ClampThinkingBudgetToAnswerRoom(
            SimpleOptions.ThinkingBudgetForLevel(options.ReasoningEffort, options.ThinkingBudgets),
            (long)ceiling);
        return budget > 0 ? budget : null;
    }

    private static JsonObject? BuildChatTemplateValues(
        ModelSpec model,
        SimpleStreamOptions? options,
        JsonObject? values,
        long? thinkingBudget)
    {
        if (values is null || values.Count == 0) return null;
        var result = new JsonObject();

        foreach (var (key, value) in values)
        {
            if (value is not JsonObject config)
            {
                result[key] = value?.DeepClone();
                continue;
            }

            var reasoningEffort = options?.ReasoningEffort;
            if (reasoningEffort is null && config.Bool("omitWhenOff") == true) continue;

            var variable = config.Str("$var");
            if (variable == "thinking.enabled")
            {
                result[key] = reasoningEffort is not null;
                continue;
            }
            if (variable == "thinking.budget")
            {
                if (thinkingBudget is { } budget) result[key] = budget;
                continue;
            }

            var level = reasoningEffort ?? "off";
            var hasMapping = model.ThinkingLevelMap is { } map && map.Has(level);
            var mappedValue = hasMapping ? model.ThinkingLevelMap![level] : null;

            if (reasoningEffort is not null)
            {
                if (!hasMapping)
                {
                    result[key] = reasoningEffort;
                }
                else if (mappedValue is not null)
                {
                    result[key] = mappedValue;
                }
            }
            else if (hasMapping && mappedValue is not null)
            {
                result[key] = mappedValue;
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static bool OffExplicitlyDisabled(ModelSpec model)
        => model.ThinkingLevelMap is { } map && map.Has("off") && map["off"] is null;

    private static string? MappedOrRequested(ModelSpec model, string level)
    {
        if (model.ThinkingLevelMap is not { } map) return level;
        return map.Has(level) ? map[level] : level;
    }

    private static string? MappedCoalesce(ModelSpec model, string level)
        => model.ThinkingLevelMap is { } map && map.Has(level) ? map[level] ?? level : level;

    /// <summary>构建 /chat/completions 请求载荷。对应 TS <c>buildParams</c>。</summary>
    public static JsonObject BuildParams(
        ModelSpec model,
        TranscriptContext context,
        SimpleStreamOptions? options = null,
        OpenAiCompletionsCompat? compat = null,
        string? cacheRetention = null,
        IReadOnlyDictionary<string, string>? grammarToolInputProperties = null)
    {
        compat ??= GetCompat(model);
        grammarToolInputProperties ??= ConstrainedSampling.CreateGrammarToolInputProperties(
            Transcript.GetDeclaredTools(context.Messages), compat.SupportsOpenAIGrammarTools);
        cacheRetention ??= ResolveCacheRetention(options?.CacheRetention, options?.Env);

        var transcriptTools = Transcript.ResolveTranscriptTools(
            context.Messages,
            compat.SupportsMidConvoSystemMessages && compat.SupportsMidConvoToolAdditions
                ? Transcript.TranscriptToolSupport.Additions
                : Transcript.TranscriptToolSupport.None);
        var messages = ConvertMessages(model, context, compat, grammarToolInputProperties);
        var cacheControl = GetCompatCacheControl(compat, cacheRetention);

        var parameters = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = messages,
            ["stream"] = true,
        };

        var usePromptCacheKey =
            (model.BaseUrl.Contains("api.openai.com", StringComparison.Ordinal) && cacheRetention != "none")
            || (cacheRetention == "long" && compat.SupportsLongCacheRetention);
        if (usePromptCacheKey)
        {
            var promptCacheKey = OpenAiPromptCache.ClampPromptCacheKey(options?.SessionId);
            if (promptCacheKey is not null) parameters["prompt_cache_key"] = promptCacheKey;
        }
        if (cacheRetention == "long" && compat.SupportsLongCacheRetention)
        {
            parameters["prompt_cache_retention"] = "24h";
        }

        if (compat.SupportsUsageInStreaming)
        {
            parameters["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        if (compat.SupportsStore)
        {
            parameters["store"] = false;
        }
        if (options?.MaxTokens is { } maxTokens && maxTokens > 0)
        {
            if (compat.MaxTokensField == "max_tokens")
            {
                parameters["max_tokens"] = maxTokens;
            }
            else
            {
                parameters["max_completion_tokens"] = maxTokens;
            }
        }
        if (options?.Temperature is { } temperature)
        {
            parameters["temperature"] = temperature;
        }

        JsonArray? tools = null;
        if (transcriptTools.RequestTools.Count > 0)
        {
            tools = ConvertTools(transcriptTools.RequestTools, compat);
            parameters["tools"] = tools;
            if (compat.ZaiToolStream)
            {
                parameters["tool_stream"] = true;
            }
        }
        else if (HasToolHistory(context.Messages))
        {
            // Anthropic（经 LiteLLM/代理）在会话含 tool_calls/tool_result 时要求存在 tools 字段。
            parameters["tools"] = new JsonArray();
        }

        if (cacheControl is not null)
        {
            ApplyAnthropicCacheControl(messages, tools, cacheControl);
        }

        if (options?.ToolChoice is { } toolChoice)
        {
            parameters["tool_choice"] = toolChoice.DeepClone();
        }

        var rawCompat = model.Compat;
        if (rawCompat is not null
            && rawCompat.TryGetPropertyValue("vllmPriority", out var priority)
            && rawCompat.ContainsKey("vllmPriority"))
        {
            parameters["priority"] = priority?.DeepClone();
        }

        var thinkingTokenBudgetField = ResolveThinkingTokenBudgetField(compat);
        var thinkingBudget = ResolveClampedThinkingBudget(model, options, parameters);
        var reasoningEffort = options?.ReasoningEffort;

        if (compat.ThinkingFormat == "zai" && model.Reasoning)
        {
            parameters["thinking"] = reasoningEffort is not null
                ? new JsonObject { ["type"] = "enabled", ["clear_thinking"] = false }
                : new JsonObject { ["type"] = "disabled" };
            if (reasoningEffort is not null && compat.SupportsReasoningEffort)
            {
                var effort = MappedOrRequested(model, reasoningEffort);
                if (effort is not null) parameters["reasoning_effort"] = effort;
            }
        }
        else if (compat.ThinkingFormat == "qwen" && model.Reasoning)
        {
            parameters["enable_thinking"] = reasoningEffort is not null;
            if (reasoningEffort is not null && compat.SupportsReasoningEffort)
            {
                parameters["reasoning_effort"] = MappedCoalesce(model, reasoningEffort);
            }
        }
        else if (compat.ThinkingFormat == "qwen-chat-template" && model.Reasoning)
        {
            parameters["chat_template_kwargs"] = new JsonObject
            {
                ["enable_thinking"] = reasoningEffort is not null,
                ["preserve_thinking"] = true,
            };
        }
        else if (compat.ThinkingFormat == "chat-template" && model.Reasoning)
        {
            var chatTemplateValues = BuildChatTemplateValues(
                model, options, compat.ChatTemplateKwargs, thinkingBudget);
            if (chatTemplateValues is not null)
            {
                parameters["chat_template_kwargs"] = chatTemplateValues;
            }
        }
        else if (compat.ThinkingFormat == "baseten" && model.Reasoning)
        {
            var chatTemplateArgs = BuildChatTemplateValues(
                model, options, compat.ChatTemplateArgs, thinkingBudget);
            if (chatTemplateArgs is not null)
            {
                parameters["chat_template_args"] = chatTemplateArgs;
            }
            if (compat.SupportsReasoningEffort)
            {
                var effort = reasoningEffort is not null
                    ? MappedOrRequested(model, reasoningEffort)
                    : model.ThinkingLevelMap is { } offMap && offMap.Has("off")
                        ? offMap["off"]
                        : null;
                if (effort is not null) parameters["reasoning_effort"] = effort;
            }
        }
        else if (compat.ThinkingFormat == "deepseek" && model.Reasoning)
        {
            if (reasoningEffort is not null)
            {
                parameters["thinking"] = new JsonObject { ["type"] = "enabled" };
            }
            else if (!OffExplicitlyDisabled(model))
            {
                parameters["thinking"] = new JsonObject { ["type"] = "disabled" };
            }
            if (reasoningEffort is not null && compat.SupportsReasoningEffort)
            {
                parameters["reasoning_effort"] = MappedCoalesce(model, reasoningEffort);
            }
        }
        else if (compat.ThinkingFormat == "openrouter" && model.Reasoning)
        {
            if (reasoningEffort is not null)
            {
                parameters["reasoning"] = new JsonObject
                {
                    ["effort"] = MappedCoalesce(model, reasoningEffort),
                };
            }
            else if (!OffExplicitlyDisabled(model))
            {
                parameters["reasoning"] = new JsonObject
                {
                    ["effort"] = model.ThinkingLevelMap is { } map && map.Has("off")
                        ? map["off"] ?? "none"
                        : "none",
                };
            }
        }
        else if (compat.ThinkingFormat == "ant-ling" && model.Reasoning && reasoningEffort is not null)
        {
            var effort = model.ThinkingLevelMap is { } map && map.Has(reasoningEffort)
                ? map[reasoningEffort]
                : null;
            if (effort is not null)
            {
                parameters["reasoning"] = new JsonObject { ["effort"] = effort };
            }
        }
        else if (compat.ThinkingFormat == "together" && model.Reasoning)
        {
            parameters["reasoning"] = new JsonObject { ["enabled"] = reasoningEffort is not null };
            if (reasoningEffort is not null && compat.SupportsReasoningEffort)
            {
                parameters["reasoning_effort"] = MappedCoalesce(model, reasoningEffort);
            }
        }
        else if (compat.ThinkingFormat == "string-thinking" && model.Reasoning)
        {
            if (reasoningEffort is not null)
            {
                parameters["thinking"] = MappedCoalesce(model, reasoningEffort);
            }
            else if (!OffExplicitlyDisabled(model))
            {
                parameters["thinking"] = model.ThinkingLevelMap is { } map && map.Has("off")
                    ? map["off"] ?? "none"
                    : "none";
            }
        }
        else if (reasoningEffort is not null && model.Reasoning && compat.SupportsReasoningEffort)
        {
            parameters["reasoning_effort"] = MappedCoalesce(model, reasoningEffort);
        }
        else if (reasoningEffort is null && model.Reasoning && compat.SupportsReasoningEffort)
        {
            if (model.ThinkingLevelMap is { } map && map.Has("off") && map["off"] is { } offValue)
            {
                parameters["reasoning_effort"] = offValue;
            }
        }

        if (thinkingTokenBudgetField is not null && thinkingBudget is { } budgetValue)
        {
            parameters[thinkingTokenBudgetField] = budgetValue;
        }

        if (rawCompat is not null)
        {
            if (rawCompat.TryGetPropertyValue("openRouterRouting", out var openRouterNode)
                && openRouterNode is JsonObject openRouterRouting)
            {
                parameters["provider"] = openRouterRouting.DeepClone();
            }

            if (rawCompat.TryGetPropertyValue("vercelGatewayRouting", out var gatewayNode)
                && gatewayNode is JsonObject gatewayRouting)
            {
                var only = gatewayRouting["only"] as JsonArray;
                var order = gatewayRouting["order"] as JsonArray;
                if (only is not null || order is not null)
                {
                    var gatewayOptions = new JsonObject();
                    if (only is not null) gatewayOptions["only"] = only.DeepClone();
                    if (order is not null) gatewayOptions["order"] = order.DeepClone();
                    parameters["providerOptions"] = new JsonObject
                    {
                        ["gateway"] = gatewayOptions,
                    };
                }
            }
        }

        if (SimpleOptions.ResolveSamplingParams(
                model, reasoningEffort ?? "off", options?.SamplingParams) is { } samplingParams)
        {
            foreach (var (key, value) in samplingParams)
            {
                parameters[key] = value?.DeepClone();
            }
        }

        return parameters;
    }

    // ---------------------------------------------------------------------
    // usage / stop reason
    // ---------------------------------------------------------------------

    /// <summary>
    /// 解析流式 usage。对应 TS <c>parseChunkUsage</c>：<c>input</c> 扣掉缓存读/写，
    /// 缓存读取值链为 <c>prompt_tokens_details.cached_tokens ?? prompt_cache_hit_tokens ??
    /// cached_tokens ?? 0</c>，输出已含 reasoning，并按目录费率折算成本。
    /// </summary>
    public static Usage ParseChunkUsage(JsonObject rawUsage, ModelSpec model)
    {
        var promptTokens = rawUsage.Num("prompt_tokens") ?? 0;
        var details = rawUsage.Obj("prompt_tokens_details");
        var cacheReadTokens = details?.Num("cached_tokens")
            ?? rawUsage.Num("prompt_cache_hit_tokens")
            ?? rawUsage.Num("cached_tokens")
            ?? 0;
        var cacheWriteTokens = details?.Num("cache_write_tokens") ?? 0;
        var input = Math.Max(0, promptTokens - cacheReadTokens - cacheWriteTokens);
        var outputTokens = rawUsage.Num("completion_tokens") ?? 0;
        var reasoningTokens = rawUsage.Obj("completion_tokens_details")?.Num("reasoning_tokens") ?? 0;

        var usage = new Usage(
            (long)input,
            (long)outputTokens,
            (long)cacheReadTokens,
            (long)cacheWriteTokens)
        {
            Reasoning = (long)reasoningTokens,
            TotalTokens = (long)(input + outputTokens + cacheReadTokens + cacheWriteTokens),
        };
        var costed = ModelOperations.CalculateCost(model, usage);
        // OpenRouter 在 usage 里附带实际计费，命中即覆盖目录价总价。
        return OpenRouterCost.IsOpenRouterModel(model)
            ? OpenRouterCost.ApplyReportedCost(costed, rawUsage)
            : costed;
    }

    /// <summary>finish_reason → StopReason + 错误文本。对应 TS <c>mapStopReason</c>。</summary>
    public static (StopReason StopReason, string? ErrorMessage) MapStopReasonDetailed(string? reason)
    {
        if (reason is null) return (StopReason.Stop, null);
        return reason switch
        {
            "stop" or "end" => (StopReason.Stop, null),
            "length" => (StopReason.Length, null),
            "function_call" or "tool_calls" => (StopReason.ToolUse, null),
            "content_filter" => (StopReason.Error, "Provider finish_reason: content_filter"),
            "network_error" => (StopReason.Error, "Provider finish_reason: network_error"),
            _ => (StopReason.Error, $"Provider finish_reason: {reason}"),
        };
    }

    /// <summary>finish_reason → StopReason（宽松入口）。</summary>
    public static StopReason MapStopReason(string? reason) => MapStopReasonDetailed(reason).StopReason;

    /// <summary>旧版 finish_reason 映射入口。</summary>
    public static StopReason MapFinishReason(string finish) => MapStopReason(finish);

    // ---------------------------------------------------------------------
    // 流式实现
    // ---------------------------------------------------------------------

    private sealed class StreamBlock
    {
        public required string Kind { get; init; }

        public StringBuilder Text { get; } = new();

        public string? Signature { get; set; }

        public string? ToolId { get; set; }

        public string? ToolName { get; set; }

        public StringBuilder PartialArgs { get; } = new();

        public JsonObject Arguments { get; set; } = [];

        public string? ArgumentsError { get; set; }

        public string? CustomInputProperty { get; set; }

        public GrammarToolInputJsonBuffer? CustomInputBuffer { get; set; }

        public int? StreamIndex { get; set; }

        public bool Finalized { get; set; }
    }

    private static void Rebuild(
        OpenAiResponsesShared.MutableAssistantMessage output, List<StreamBlock> blocks)
    {
        var content = new List<ContentBlock>();
        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case "text":
                    content.Add(new TextContent(block.Text.ToString()));
                    break;
                case "thinking":
                    content.Add(new ThinkingContent(block.Text.ToString(), block.Signature));
                    break;
                case "toolCall":
                    content.Add(new ToolCallContent(
                        block.ToolId ?? "", block.ToolName ?? "", block.Arguments)
                    {
                        ArgumentsError = block.ArgumentsError,
                    });
                    break;
            }
        }
        output.Content.Clear();
        output.Content.AddRange(content);
    }

    private static string GetCustomInput(StreamBlock block)
    {
        if (block.CustomInputProperty is not { } property) return "";
        return block.Arguments[property] is JsonValue value
            && value.TryGetValue<string>(out var input)
            ? input
            : "";
    }

    private static string? AppendCustomInput(StreamBlock block, string nextInput, bool close)
    {
        if (block.CustomInputBuffer is not { } buffer
            || block.CustomInputProperty is not { } property)
        {
            return null;
        }
        var delta = ConstrainedSampling.AppendGrammarToolInputJsonDelta(
            buffer, property, nextInput, close);
        block.Arguments = new JsonObject { [property] = nextInput };
        return delta;
    }

    private static string? RawMetadata(Exception error)
    {
        if (error is not ProviderHttpException httpException) return null;
        try
        {
            if (JsonNode.Parse(httpException.Body) is not JsonObject body) return null;
            var raw = body.Obj("error")?.Obj("metadata")?["raw"];
            return raw is null ? null : raw is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : raw.ToJsonString();
        }
        catch
        {
            return null;
        }
    }

    private static string FormatError(Exception error)
    {
        var message = ProviderError.Format(ProviderError.Normalize(error));
        var raw = RawMetadata(error);
        if (!string.IsNullOrEmpty(raw) && !message.Contains(raw, StringComparison.Ordinal))
        {
            message += $"\n{raw}";
        }
        return message;
    }

    private static Dictionary<string, string> BuildHeaders(
        ModelSpec model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        string apiKey,
        string? sessionId,
        OpenAiCompletionsCompat compat)
    {
        // 身份头在最底层（UA + provider 归属），下面的 model / provider / 请求头都可覆盖它。
        var headers = Identity.GetIdentityHeaders(Identity.TargetOf(model), options?.Identity);

        if (model.Headers is not null)
        {
            foreach (var (key, value) in model.Headers)
            {
                headers.Remove(key);
                headers[key] = value;
            }
        }

        if (model.Provider == "github-copilot")
        {
            foreach (var (key, value) in GitHubCopilotHeaders.BuildDynamicHeaders(
                context.Messages, GitHubCopilotHeaders.HasVisionInput(context.Messages)))
            {
                headers[key] = value;
            }
        }

        if (sessionId is not null && compat.SendSessionAffinityHeaders)
        {
            if (compat.SessionAffinityFormat == "openrouter")
            {
                headers["x-session-id"] = sessionId;
            }
            else
            {
                if (compat.SessionAffinityFormat == "openai")
                {
                    headers["session_id"] = sessionId;
                }
                headers["x-client-request-id"] = sessionId;
                headers["x-session-affinity"] = sessionId;
            }
        }

        if (apiKey != "unused")
        {
            headers["Authorization"] = $"Bearer {apiKey}";
        }

        void Apply(IEnumerable<KeyValuePair<string, string?>>? source)
        {
            if (source is null) return;
            foreach (var (key, value) in source)
            {
                headers.Remove(key);
                if (value is not null) headers[key] = value;
            }
        }

        Apply(options?.Headers);
        return headers;
    }

    private static IReadOnlyDictionary<string, string> ResponseHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in response.Headers)
        {
            headers[key] = string.Join(",", values);
        }
        foreach (var (key, values) in response.Content.Headers)
        {
            headers[key] = string.Join(",", values);
        }
        return headers;
    }

    /// <summary>
    /// 流式生成：POST {baseUrl}/chat/completions（stream:true），SSE 消费。
    /// 对应 TS <c>stream()</c>。错误以 <see cref="AssistantMessageEvent.Error"/> 编码。
    /// </summary>
    public static IAssistantMessageEventStream Stream(
        ModelSpec model,
        TranscriptContext context,
        SimpleStreamOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var stream = new AssistantMessageEventStream();
        var compat = GetCompat(model);
        var normalizedContext = Transcript.ResolveTranscript(context, compat.SupportsMidConvoSystemMessages);
        var grammarToolInputProperties = ConstrainedSampling.CreateGrammarToolInputProperties(
            Transcript.GetDeclaredTools(normalizedContext.Messages),
            compat.SupportsOpenAIGrammarTools);
        var client = httpClient ?? OAuthHttp.Shared;
        var signal = options?.Signal ?? default;

        _ = Task.Run(async () =>
        {
            var output = new OpenAiResponsesShared.MutableAssistantMessage(
                model.Id, model.Api, model.Provider)
            {
                Usage = new Usage(0, 0) { TotalTokens = 0 },
            };

            var blocks = new List<StreamBlock>();
            StreamBlock? textBlock = null;
            StreamBlock? thinkingBlock = null;
            List<JsonObject>? streamedReasoningDetails = null;
            var toolCallBlocksByIndex = new Dictionary<int, StreamBlock>();
            var toolCallBlocksById = new Dictionary<string, StreamBlock>();

            void ApplyStreamedReasoningDetails(StreamBlock block)
            {
                if (streamedReasoningDetails is not null)
                {
                    block.Signature = SerializeReasoningDetails(streamedReasoningDetails);
                }
            }

            int ContentIndex(StreamBlock block) => blocks.IndexOf(block);

            StreamBlock EnsureTextBlock()
            {
                if (textBlock is null)
                {
                    textBlock = new StreamBlock { Kind = "text" };
                    blocks.Add(textBlock);
                    stream.Push(new AssistantMessageEvent.TextStart(ContentIndex(textBlock), output.Snapshot()));
                }
                return textBlock;
            }

            StreamBlock EnsureThinkingBlock(string signature)
            {
                if (thinkingBlock is null)
                {
                    thinkingBlock = new StreamBlock { Kind = "thinking", Signature = signature };
                    blocks.Add(thinkingBlock);
                    stream.Push(new AssistantMessageEvent.ThinkingStart(
                        ContentIndex(thinkingBlock), output.Snapshot()));
                }
                return thinkingBlock;
            }

            StreamBlock EnsureToolCallBlock(JsonObject toolCall)
            {
                var streamIndex = toolCall.Num("index") is { } index ? (int)index : (int?)null;
                var function = toolCall.Obj("function");
                var custom = toolCall.Obj("custom");
                var name = function?.Str("name") ?? custom?.Str("name") ?? "";
                var id = toolCall.Str("id") ?? "";

                StreamBlock? block = null;
                if (streamIndex is { } byIndex) toolCallBlocksByIndex.TryGetValue(byIndex, out block);
                if (block is null && id.Length > 0) toolCallBlocksById.TryGetValue(id, out block);

                if (block is null)
                {
                    string? customInputProperty = null;
                    if (custom is not null && function is null)
                    {
                        customInputProperty = grammarToolInputProperties.TryGetValue(name, out var property)
                            ? property
                            : "input";
                    }

                    block = new StreamBlock
                    {
                        Kind = "toolCall",
                        ToolId = id,
                        ToolName = name,
                        Arguments = customInputProperty is null
                            ? []
                            : new JsonObject { [customInputProperty] = "" },
                        CustomInputProperty = customInputProperty,
                        CustomInputBuffer = customInputProperty is null
                            ? null
                            : new GrammarToolInputJsonBuffer(),
                        StreamIndex = streamIndex,
                    };
                    if (streamIndex is { } newIndex) toolCallBlocksByIndex[newIndex] = block;
                    if (id.Length > 0) toolCallBlocksById[id] = block;
                    blocks.Add(block);
                    stream.Push(new AssistantMessageEvent.ToolCallStart(
                        ContentIndex(block), output.Snapshot()));
                }

                if (streamIndex is { } currentIndex && block.StreamIndex is null)
                {
                    block.StreamIndex = currentIndex;
                    toolCallBlocksByIndex[currentIndex] = block;
                }
                if (id.Length > 0) toolCallBlocksById[id] = block;
                if (block.ToolName!.Length == 0 && name.Length > 0) block.ToolName = name;

                if (custom is not null && function is null && block.CustomInputProperty is null)
                {
                    var customInputProperty = grammarToolInputProperties.TryGetValue(block.ToolName, out var property)
                        ? property
                        : "input";
                    block.Arguments = new JsonObject { [customInputProperty] = "" };
                    block.CustomInputProperty = customInputProperty;
                    block.CustomInputBuffer = new GrammarToolInputJsonBuffer();
                    block.PartialArgs.Clear();
                }
                return block;
            }

            void FinishBlock(StreamBlock block)
            {
                var contentIndex = ContentIndex(block);
                if (contentIndex == -1 || block.Finalized) return;
                block.Finalized = true;

                switch (block.Kind)
                {
                    case "text":
                        Rebuild(output, blocks);
                        stream.Push(new AssistantMessageEvent.TextEnd(
                            contentIndex, block.Text.ToString(), output.Snapshot()));
                        break;
                    case "thinking":
                        ApplyStreamedReasoningDetails(block);
                        Rebuild(output, blocks);
                        stream.Push(new AssistantMessageEvent.ThinkingEnd(
                            contentIndex, block.Text.ToString(), output.Snapshot()));
                        break;
                    case "toolCall":
                        if (block.CustomInputProperty is not null)
                        {
                            var delta = AppendCustomInput(block, GetCustomInput(block), true);
                            if (delta is not null)
                            {
                                Rebuild(output, blocks);
                                stream.Push(new AssistantMessageEvent.ToolCallDelta(
                                    contentIndex, contentIndex, delta, output.Snapshot()));
                            }
                        }
                        else
                        {
                            // 已完成的调用严格解析：非法 JSON 不再保留解析前缀（上游 #10897）。
                            var finalArguments = JsonParse.ParseToolCallArguments(block.PartialArgs.ToString());
                            block.Arguments = finalArguments.Arguments;
                            block.ArgumentsError = finalArguments.ArgumentsError;
                        }
                        block.PartialArgs.Clear();
                        block.CustomInputBuffer = null;
                        block.CustomInputProperty = null;
                        block.StreamIndex = null;
                        Rebuild(output, blocks);
                        stream.Push(new AssistantMessageEvent.ToolCallEnd(
                            contentIndex,
                            (ToolCallContent)output.Content[contentIndex],
                            output.Snapshot()));
                        break;
                }
            }

            try
            {
                var apiKey = GetClientApiKey(model.Provider, options?.ApiKey, options?.Headers);
                var cacheRetention = ResolveCacheRetention(options?.CacheRetention, options?.Env);
                var cacheSessionId = cacheRetention == "none" ? null : options?.SessionId;

                var payload = BuildParams(
                    model, normalizedContext, options, compat, cacheRetention, grammarToolInputProperties);
                if (options?.OnPayload is { } onPayload)
                {
                    var transformed = await onPayload(payload, model).ConfigureAwait(false);
                    if (transformed is not null) payload = transformed;
                }

                var headers = BuildHeaders(model, normalizedContext, options, apiKey, cacheSessionId, compat);
                var url = $"{(options?.BaseUrl ?? model.BaseUrl).TrimEnd('/')}/chat/completions";

                var response = await ProviderRetry.RetryAsync(async () =>
                {
                    using var perCall = CancellationTokenSource.CreateLinkedTokenSource(
                        signal, cancellationToken);
                    if (options?.TimeoutMs is { } timeout) perCall.CancelAfter(timeout);
                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(
                            payload.ToJsonString(), Encoding.UTF8, "application/json"),
                    };
                    foreach (var (key, value) in headers)
                    {
                        request.Headers.TryAddWithoutValidation(key, value);
                    }
                    try
                    {
                        var httpResponse = await client.SendAsync(
                            request, HttpCompletionOption.ResponseHeadersRead, perCall.Token)
                            .ConfigureAwait(false);
                        if (!httpResponse.IsSuccessStatusCode)
                        {
                            var body = await httpResponse.Content.ReadAsStringAsync(perCall.Token)
                                .ConfigureAwait(false);
                            throw new ProviderHttpException(
                                (int)httpResponse.StatusCode, body,
                                $"{(int)httpResponse.StatusCode}: {body}",
                                headers: ResponseHeaders(httpResponse));
                        }
                        return httpResponse;
                    }
                    catch (OperationCanceledException) when (
                        signal.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("Request was aborted", signal);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TimeoutException($"Request timed out after {options?.TimeoutMs}ms");
                    }
                }, new ProviderRetryOptions
                {
                    MaxRetries = options?.MaxRetries,
                    MaxRetryDelayMs = options?.MaxRetryDelayMs,
                    Signal = signal,
                }).ConfigureAwait(false);

                if (options?.OnResponse is { } onResponse)
                {
                    await onResponse(new ProviderResponse
                    {
                        Status = (int)response.StatusCode,
                        Headers = ResponseHeaders(response),
                    }, model).ConfigureAwait(false);
                }

                stream.Push(new AssistantMessageEvent.Start(output.Snapshot()));

                await using var contentStream = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                    signal, cancellationToken);

                var hasFinishReason = false;

                await foreach (var sse in AiSse.EventsAsync(contentStream, lifetime.Token).ConfigureAwait(false))
                {
                    if (sse.Data == "[DONE]") continue;
                    if (string.IsNullOrWhiteSpace(sse.Data)) continue;

                    var chunk = JsonParse.ParseJsonWithRepair(sse.Data) as JsonObject
                        ?? throw new InvalidOperationException("OpenAI SSE data is not a JSON object");

                    if (options?.OnProviderStreamEvent is { } observer)
                    {
                        await observer(chunk, model).ConfigureAwait(false);
                    }

                    if (string.IsNullOrEmpty(output.ResponseId) && chunk.Str("id") is { } responseId)
                    {
                        output.ResponseId = responseId;
                    }
                    if (chunk.Str("model") is { Length: > 0 } responseModel && responseModel != model.Id)
                    {
                        if (string.IsNullOrEmpty(output.ResponseModel))
                        {
                            output.ResponseModel = responseModel;
                        }
                    }

                    var hasChunkUsage = chunk["usage"] is JsonObject;
                    if (chunk["usage"] is JsonObject usage)
                    {
                        output.Usage = ParseChunkUsage(usage, model);
                    }

                    if (chunk["choices"] is not JsonArray { Count: > 0 } choices
                        || choices[0] is not JsonObject choice)
                    {
                        continue;
                    }

                    // 部分 provider（如 Moonshot）把 usage 放在 choice.usage。
                    if (!hasChunkUsage && choice["usage"] is JsonObject choiceUsage)
                    {
                        output.Usage = ParseChunkUsage(choiceUsage, model);
                    }

                    if (choice.Str("finish_reason") is { Length: > 0 } finishReason)
                    {
                        output.RawStopReason = finishReason;
                        var mapped = MapStopReasonDetailed(finishReason);
                        output.StopReason = mapped.StopReason;
                        if (mapped.ErrorMessage is not null)
                        {
                            output.ErrorMessage = mapped.ErrorMessage;
                        }
                        hasFinishReason = true;
                    }

                    if (choice.Obj("delta") is not { } delta) continue;

                    // Handle reasoning before content: some proxies (e.g. LiteLLM) aggregate reasoning and
                    // answer into one delta, and the thinking block must precede the text block.
                    // Some endpoints return reasoning in reasoning_content (llama.cpp), or reasoning (other
                    // openai compatible endpoints). Use the first non-empty reasoning field to avoid
                    // duplication (e.g. chutes.ai returns both with the same content).
                    string? foundReasoningField = null;
                    string? foundReasoningDelta = null;
                    foreach (var field in StreamReasoningFields)
                    {
                        if (delta.Str(field) is { Length: > 0 } reasoningDelta)
                        {
                            foundReasoningField = field;
                            foundReasoningDelta = reasoningDelta;
                            break;
                        }
                    }
                    if (foundReasoningField is not null && foundReasoningDelta is not null)
                    {
                        var signature = model.Provider == "opencode-go"
                            && foundReasoningField == "reasoning"
                            ? "reasoning_content"
                            : foundReasoningField;
                        var block = EnsureThinkingBlock(signature);
                        block.Text.Append(foundReasoningDelta);
                        Rebuild(output, blocks);
                        stream.Push(new AssistantMessageEvent.ThinkingDelta(
                            ContentIndex(block), foundReasoningDelta, 0, block.Signature,
                            output.Snapshot()));
                    }

                    if (delta["reasoning_details"] is JsonArray reasoningDetails)
                    {
                        foreach (var detail in reasoningDetails)
                        {
                            if (detail is not JsonObject reasoningDetail
                                || !IsReasoningDetail(reasoningDetail))
                            {
                                continue;
                            }
                            EnsureThinkingBlock("");
                            streamedReasoningDetails ??= [];
                            AppendReasoningDetail(streamedReasoningDetails, reasoningDetail);
                        }
                    }

                    if (delta.Str("content") is { Length: > 0 } contentDelta)
                    {
                        var block = EnsureTextBlock();
                        block.Text.Append(contentDelta);
                        Rebuild(output, blocks);
                        stream.Push(new AssistantMessageEvent.TextDelta(
                            ContentIndex(block), contentDelta, 0, output.Snapshot()));
                    }

                    if (delta["tool_calls"] is JsonArray toolCalls)
                    {
                        foreach (var node in toolCalls)
                        {
                            if (node is not JsonObject toolCallDelta) continue;
                            var block = EnsureToolCallBlock(toolCallDelta);
                            var id = toolCallDelta.Str("id");
                            if (block.ToolId!.Length == 0 && !string.IsNullOrEmpty(id))
                            {
                                block.ToolId = id;
                                toolCallBlocksById[id] = block;
                            }
                            var function = toolCallDelta.Obj("function");
                            var custom = toolCallDelta.Obj("custom");
                            var name = function?.Str("name") ?? custom?.Str("name");
                            if (block.ToolName!.Length == 0 && !string.IsNullOrEmpty(name))
                            {
                                block.ToolName = name;
                            }

                            var toolDelta = "";
                            if (function?.Str("arguments") is { Length: > 0 } argumentsDelta)
                            {
                                toolDelta = argumentsDelta;
                                block.PartialArgs.Append(argumentsDelta);
                                block.Arguments = JsonParse.ParseStreamingJson(block.PartialArgs.ToString());
                            }
                            else if (custom?.Str("input") is { Length: > 0 } inputDelta)
                            {
                                var nextInput = GetCustomInput(block) + inputDelta;
                                toolDelta = AppendCustomInput(block, nextInput, false) ?? "";
                            }

                            Rebuild(output, blocks);
                            stream.Push(new AssistantMessageEvent.ToolCallDelta(
                                ContentIndex(block), ContentIndex(block), toolDelta,
                                output.Snapshot()));
                        }
                    }
                }

                foreach (var block in blocks)
                {
                    FinishBlock(block);
                }

                if (signal.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Request was aborted");
                }
                if (output.StopReason == StopReason.Aborted)
                {
                    throw new OperationCanceledException("Request was aborted");
                }
                if (!hasFinishReason && !compat.SupportsFinishReason)
                {
                    output.StopReason = blocks.Any(block => block.Kind == "toolCall")
                        ? StopReason.ToolUse
                        : StopReason.Stop;
                }
                if (output.StopReason == StopReason.Error)
                {
                    throw new InvalidOperationException(
                        output.ErrorMessage ?? "Provider returned an error stop reason");
                }
                if ((compat.SupportsFinishReason && !hasFinishReason)
                    || output.StopReason == StopReason.Pending)
                {
                    throw new InvalidOperationException("Stream ended without finish_reason");
                }

                var final = output.Snapshot();
                stream.Push(new AssistantMessageEvent.Done(output.StopReason, final));
                stream.End(final);
            }
            catch (Exception error)
            {
                var aborted = signal.IsCancellationRequested || cancellationToken.IsCancellationRequested;
                foreach (var block in blocks)
                {
                    if (block.Kind == "thinking") ApplyStreamedReasoningDetails(block);
                }
                Rebuild(output, blocks);

                output.StopReason = aborted ? StopReason.Aborted : StopReason.Error;
                output.ErrorMessage = aborted
                    ? "Request was aborted"
                    : FormatError(error);
                var failed = output.Snapshot();
                stream.Push(new AssistantMessageEvent.Error(
                    output.StopReason, output.ErrorMessage, failed));
                stream.End(failed);
            }
        }, CancellationToken.None);

        return stream;
    }

    /// <summary>
    /// 简单入口。对应 TS <c>streamSimple</c>：装配公共选项并把 reasoning 档位收敛后
    /// 作为 reasoningEffort 交给完整实现。
    /// </summary>
    public static IAssistantMessageEventStream StreamSimple(
        ModelSpec model,
        TranscriptContext context,
        SimpleStreamOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        // TS 在返回流之前同步校验凭据（失败直接抛出，而非编码为错误事件）。
        GetClientApiKey(model.Provider, options?.ApiKey, options?.Headers);

        var baseOptions = BuildBaseOptions(model, context, options);
        var clampedReasoning = options?.Reasoning is { } reasoning
            ? ThinkingLevels.Clamp(model, reasoning)
            : null;
        var reasoningEffort = options?.Reasoning is not null
            ? clampedReasoning == "off" ? null : clampedReasoning
            : options?.ReasoningEffort;

        var effectiveOptions = baseOptions with
        {
            ReasoningEffort = reasoningEffort,
            ThinkingBudgets = options?.ThinkingBudgets,
        };
        return Stream(model, context, effectiveOptions, httpClient, cancellationToken);
    }

    private static SimpleStreamOptions BuildBaseOptions(
        ModelSpec model, TranscriptContext context, SimpleStreamOptions? options)
    {
        var samplingParams = SimpleOptions.ResolveSamplingParams(
            model, options?.Reasoning ?? "off", options?.SamplingParams);
        var maxTokens = options?.MaxTokens
            ?? (int)Math.Min(int.MaxValue, Math.Max(0, model.MaxTokens));

        return new SimpleStreamOptions(
            ApiKey: options?.ApiKey,
            BaseUrl: options?.BaseUrl,
            MaxTokens: SimpleOptions.ClampMaxTokensToContext(model, context, maxTokens),
            Temperature: options?.Temperature,
            StopSequences: options?.StopSequences,
            SessionId: options?.SessionId)
        {
            Headers = options?.Headers,
            Identity = options?.Identity,
            SamplingParams = samplingParams,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            TimeoutMs = options?.TimeoutMs,
            WebsocketConnectTimeoutMs = options?.WebsocketConnectTimeoutMs,
            Transport = options?.Transport,
            CacheRetention = options?.CacheRetention,
            Metadata = options?.Metadata,
            Env = options?.Env,
            Signal = options?.Signal ?? default,
            OnPayload = options?.OnPayload,
            OnResponse = options?.OnResponse,
            OnProviderStreamEvent = options?.OnProviderStreamEvent,
            ToolChoice = options?.ToolChoice,
            Reasoning = options?.Reasoning,
            ReasoningEffort = options?.ReasoningEffort,
            Deferred = options?.Deferred ?? false,
            DeferredWindow = options?.DeferredWindow,
            ThinkingBudgets = options?.ThinkingBudgets,
            ThinkingEnabled = options?.ThinkingEnabled,
            ThinkingBudgetTokens = options?.ThinkingBudgetTokens,
            Effort = options?.Effort,
            ThinkingDisplay = options?.ThinkingDisplay,
            InterleavedThinking = options?.InterleavedThinking,
        };
    }

    // ---------------------------------------------------------------------
    // 旧版入口：供尚未迁移到 ModelSpec 的调用点与既有测试使用。
    // ---------------------------------------------------------------------

    /// <summary>旧版入口：以 <see cref="Model"/> 描述调用完整流实现。</summary>
    public static Task<IAssistantMessageEventStream> StreamSimple(
        Model model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        CancellationToken cancellationToken = default)
    {
        var spec = ToModelSpec(model, options);
        return Task.FromResult<IAssistantMessageEventStream>(
            StreamSimple(spec, context, options, httpClient: null, cancellationToken));
    }

    /// <summary>旧版入口：仅从消息与工具构建请求体。</summary>
    public static JsonObject BuildParams(
        Model model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        SimpleStreamOptions options)
    {
        var spec = ToModelSpec(model, options);
        var context = Transcript.NormalizeContext(null, tools, messages);
        return BuildParams(spec, context, options);
    }

    /// <summary>旧版入口：仅转换消息（无模型上下文时按 openai 缺省 compat）。</summary>
    public static JsonArray ConvertMessages(IReadOnlyList<ChatMessage> messages)
    {
        var spec = new ModelSpec
        {
            Id = "openai-completions",
            Name = "OpenAI Completions",
            Api = "openai-completions",
            Provider = "openai",
            BaseUrl = DefaultBaseUrl,
            MaxTokens = 4096,
        };
        return ConvertMessages(spec, new TranscriptContext(messages), GetCompat(spec));
    }

    private static ModelSpec ToModelSpec(Model model, SimpleStreamOptions? options) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Api = model.Api,
        Provider = model.Provider,
        BaseUrl = (options?.BaseUrl ?? DefaultBaseUrl).TrimEnd('/'),
        MaxTokens = options?.MaxTokens ?? 4096,
        Reasoning = !string.IsNullOrEmpty(model.Reasoning),
    };
}
