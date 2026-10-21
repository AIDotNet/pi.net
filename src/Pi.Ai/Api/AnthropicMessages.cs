using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>服务端 fallback 模型的成本。对应 TS <c>compat.allowedFallbackModels[].cost</c>。</summary>
public sealed record AnthropicFallbackModel(string Provider, string Model, ModelCostRates Cost);

/// <summary>
/// Anthropic 兼容矩阵（compat 缺省值解析结果）。对应 TS <c>getAnthropicCompat</c>
/// 以及 buildParams/stream 直接读取的 <c>model.compat</c> 字段。
/// </summary>
public sealed record AnthropicMessagesCompat
{
    public bool SupportsEagerToolInputStreaming { get; init; } = true;

    public bool SupportsLongCacheRetention { get; init; } = true;

    public bool SendSessionAffinityHeaders { get; init; }

    public string? SessionAffinityFormat { get; init; }

    public bool SupportsCacheControlOnTools { get; init; } = true;

    public bool SupportsTemperature { get; init; } = true;

    public bool AllowEmptySignature { get; init; }

    public bool SupportsStrictTools { get; init; }

    public bool SupportsMidConvoSystemMessages { get; init; }

    public bool SupportsMidConvoToolChanges { get; init; }

    /// <summary>由服务端管理 adaptive effort（中段 output_config）。对应 TS <c>supportsMidConvoEffort</c>。</summary>
    public bool SupportsMidConvoEffort { get; init; }

    /// <summary>只支持 adaptive thinking。对应 TS <c>forceAdaptiveThinking</c>。</summary>
    public bool ForceAdaptiveThinking { get; init; }

    /// <summary>服务端 fallback 列表。对应 TS <c>allowedFallbackModels</c>。</summary>
    public IReadOnlyList<AnthropicFallbackModel>? AllowedFallbackModels { get; init; }
}

/// <summary>
/// Anthropic Messages provider。对应 TS <c>api/anthropic-messages.ts</c>：
/// system 独立字段、content 块协议（thinking / redacted_thinking / tool_use / tool_result /
/// image）、beta 头、cache_control、adaptive/budget thinking、中段 system 与工具变更、
/// 服务端 fallback、SSE（content_block_* 事件模型）→ 统一流事件。
/// 直接以 HttpClient 调 REST（SDK 只是在 <c>baseURL</c> 后追加 <c>/v1/messages</c>）。
/// </summary>
public static class AnthropicMessages
{
    private const string ApiVersion = "2023-06-01";

    private const string DefaultBaseUrl = "https://api.anthropic.com";

    // Stealth mode: mimic Claude Code's tool naming exactly.
    private const string ClaudeCodeVersion = "2.1.280";

    private static readonly string[] ClaudeCodeTools =
    [
        "Read", "Write", "Edit", "Bash", "Grep", "Glob", "AskUserQuestion",
        "EnterPlanMode", "ExitPlanMode", "KillShell", "NotebookEdit", "Skill",
        "Task", "TaskOutput", "TodoWrite", "WebFetch", "WebSearch",
    ];

    private static readonly Dictionary<string, string> ClaudeCodeToolLookup =
        ClaudeCodeTools.ToDictionary(name => name.ToLowerInvariant(), name => name);

    private const string FineGrainedToolStreamingBeta = "fine-grained-tool-streaming-2025-05-14";
    private const string InterleavedThinkingBeta = "interleaved-thinking-2025-05-14";
    private const string ServerSideFallbackBeta = "server-side-fallback-2026-07-01";
    private const string MidConversationOutputConfigBeta = "mid-conversation-output-config-2026-07-01";
    private const string ThinkingBindingControlsBeta = "thinking-binding-controls-2026-08-01";
    private const string InlineToolsBeta = "inline-tools-2026-09-15";

    private static readonly HashSet<string> AnthropicMessageEvents =
    [
        "message_start", "message_delta", "message_stop",
        "content_block_start", "content_block_delta", "content_block_stop",
    ];

    // Keywords Anthropic strict tool use rejects with a 400 for the whole request.
    private static readonly HashSet<string> AnthropicStrictUnsupportedKeywords =
    [
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "maxItems", "uniqueItems", "minContains", "maxContains", "minProperties", "maxProperties",
    ];

    private static readonly HashSet<string> AnthropicStrictStringFormats =
    [
        "date-time", "time", "date", "duration", "email", "hostname", "uri", "ipv4", "ipv6", "uuid",
    ];

    /// <summary>
    /// Stable deferred tool declared whenever native tool changes are in use. Anthropic adds
    /// hidden prompt scaffolding for mid-conversation tool changes; declaring this placeholder
    /// from the first request keeps that scaffolding in the cached prefix.
    /// </summary>
    private static JsonObject DeferredToolPlaceholder() => new()
    {
        ["name"] = "__pi_deferred_placeholder__",
        ["description"] = "Reserved placeholder. Never available. Never call this.",
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray(),
        },
        ["defer_loading"] = true,
    };

    /// <summary>compat 缺省值解析。对应 TS <c>getAnthropicCompat</c> + 直接读取的 compat 字段。</summary>
    public static AnthropicMessagesCompat GetCompat(ModelSpec model)
    {
        var compat = model.Compat ?? [];
        bool? Opt(string key, bool fallback)
            => compat[key] is JsonValue { } value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

        var isOpenRouter = model.Provider == "openrouter"
            || model.BaseUrl.Contains("openrouter.ai", StringComparison.Ordinal);

        return new AnthropicMessagesCompat
        {
            SupportsEagerToolInputStreaming = Opt("supportsEagerToolInputStreaming", true) ?? true,
            SupportsLongCacheRetention = Opt("supportsLongCacheRetention", true) ?? true,
            SendSessionAffinityHeaders = Opt("sendSessionAffinityHeaders", isOpenRouter) ?? isOpenRouter,
            SessionAffinityFormat = compat.Str("sessionAffinityFormat") ?? (isOpenRouter ? "openrouter" : null),
            SupportsCacheControlOnTools = Opt("supportsCacheControlOnTools", true) ?? true,
            SupportsTemperature = Opt("supportsTemperature", true) ?? true,
            AllowEmptySignature = Opt("allowEmptySignature", false) ?? false,
            SupportsStrictTools = Opt("supportsStrictTools", false) ?? false,
            SupportsMidConvoSystemMessages = Opt("supportsMidConvoSystemMessages", false) ?? false,
            SupportsMidConvoToolChanges = Opt("supportsMidConvoToolChanges", false) ?? false,
            SupportsMidConvoEffort = Opt("supportsMidConvoEffort", false) ?? false,
            ForceAdaptiveThinking = Opt("forceAdaptiveThinking", false) ?? false,
            AllowedFallbackModels = ParseFallbackModels(compat["allowedFallbackModels"]),
        };
    }

    private static IReadOnlyList<AnthropicFallbackModel>? ParseFallbackModels(JsonNode? node)
    {
        if (node is not JsonArray array) return null;
        var result = new List<AnthropicFallbackModel>();
        foreach (var item in array)
        {
            if (item is not JsonObject entry) continue;
            var provider = entry.Str("provider");
            var model = entry.Str("model");
            if (provider is null || model is null) continue;
            if (entry.Obj("cost") is not { } cost) continue;
            result.Add(new AnthropicFallbackModel(provider, model, ModelCatalog.ParseCost(cost)));
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>构建请求参数。对应 TS <c>buildParams</c>（<c>betas</c> 以数组返回，HTTP 层转成头）。</summary>
    public static JsonObject BuildParams(
        ModelSpec model,
        TranscriptContext context,
        bool isOAuthToken,
        SimpleStreamOptions? options,
        AnthropicMessagesCompat? compat = null)
    {
        compat ??= GetCompat(model);
        var cacheControl = GetCacheControl(model, options?.CacheRetention, options?.Env, compat);
        var initialSystemMessage = Transcript.GetInitialSystemMessage(context.Messages);
        var initialSystemText = initialSystemMessage is not null
            ? Text.GetSystemMessageText(initialSystemMessage)
            : "";
        var transformedMessages = TransformMessages.Transform(
            context.Messages, model, (id, _, _) => NormalizeToolCallId(id));
        var conversationMessages = initialSystemMessage is not null
            ? transformedMessages.Skip(1).ToList()
            : transformedMessages;

        // Native tool changes keep the request-level tool list fixed and define every later tool
        // by value in a `tool_addition` block. Anthropic rejects a list where every tool is
        // deferred, so there must be an initial active tool to anchor the placeholder.
        var initialTools = initialSystemMessage?.ToolsAdded ?? [];
        var nativeToolChanges = compat.SupportsMidConvoSystemMessages
            && compat.SupportsMidConvoToolChanges
            && initialTools.Count > 0;

        var converted = ConvertMessagesCore(
            conversationMessages,
            isOAuthToken,
            cacheControl,
            compat.AllowEmptySignature,
            compat.SupportsMidConvoEffort ? model.Provider : null,
            nativeToolChanges
                ? tools => ConvertTools(tools, isOAuthToken, compat.SupportsEagerToolInputStreaming,
                    compat.SupportsStrictTools, null)
                : null);

        var activeEffort = options?.Effort ?? "high";
        var parameters = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = compat.SupportsMidConvoEffort
                ? InsertThinkingLevelMessages(converted, activeEffort)
                : converted.Messages,
            ["max_tokens"] = options?.MaxTokens ?? model.MaxTokens,
            ["stream"] = true,
        };

        var betaFeatures = GetBetaFeatures(model, context, isOAuthToken, nativeToolChanges, options, compat);
        if (betaFeatures.Count > 0)
        {
            parameters["betas"] = new JsonArray(betaFeatures.Select(feature => (JsonNode)feature).ToArray());
        }

        // For OAuth tokens, we MUST include Claude Code identity.
        if (isOAuthToken)
        {
            var system = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = "You are Claude Code, Anthropic's official CLI for Claude.",
            });
            if (cacheControl is not null)
            {
                ((JsonObject)system[0]!)["cache_control"] = cacheControl.DeepClone();
            }
            if (initialSystemText.Length > 0)
            {
                var block = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = SanitizeUnicode.SanitizeSurrogates(initialSystemText),
                };
                if (cacheControl is not null) block["cache_control"] = cacheControl.DeepClone();
                system.Add(block);
            }
            parameters["system"] = system;
        }
        else if (initialSystemText.Length > 0)
        {
            var block = new JsonObject
            {
                ["type"] = "text",
                ["text"] = SanitizeUnicode.SanitizeSurrogates(initialSystemText),
            };
            if (cacheControl is not null) block["cache_control"] = cacheControl.DeepClone();
            parameters["system"] = new JsonArray(block);
        }

        // Temperature is incompatible with extended thinking and unsupported on Claude Opus 4.7+.
        if (options?.Temperature is { } temperature
            && options.ThinkingEnabled != true
            && !compat.SupportsMidConvoEffort
            && compat.SupportsTemperature)
        {
            parameters["temperature"] = temperature;
        }

        var toolCacheControl = compat.SupportsCacheControlOnTools ? cacheControl : null;
        if (nativeToolChanges)
        {
            var tools = ConvertTools(
                initialTools, isOAuthToken, compat.SupportsEagerToolInputStreaming,
                compat.SupportsStrictTools, toolCacheControl);
            tools.Add(DeferredToolPlaceholder());
            parameters["tools"] = tools;
        }
        else
        {
            var tools = Transcript.GetCurrentTools(context.Messages);
            if (tools.Count > 0)
            {
                parameters["tools"] = ConvertTools(
                    tools, isOAuthToken, compat.SupportsEagerToolInputStreaming,
                    compat.SupportsStrictTools, toolCacheControl);
            }
        }

        // Managed effort models always use adaptive thinking so prefix mismatches can be
        // dropped instead of surfacing as persistent 400 responses.
        if (compat.SupportsMidConvoEffort)
        {
            parameters["thinking"] = new JsonObject
            {
                ["type"] = "adaptive",
                ["display"] = options?.ThinkingDisplay ?? "summarized",
                ["block_binding"] = new JsonObject { ["prefix_mismatch_behavior"] = "drop_block" },
            };
            parameters["output_config"] = new JsonObject { ["effort"] = "high" };
        }
        else if (model.Reasoning)
        {
            if (options?.ThinkingEnabled == true)
            {
                var display = options.ThinkingDisplay ?? "summarized";
                if (compat.ForceAdaptiveThinking)
                {
                    parameters["thinking"] = new JsonObject
                    {
                        ["type"] = "adaptive",
                        ["display"] = display,
                    };
                    if (options.Effort is { } effort)
                    {
                        parameters["output_config"] = new JsonObject { ["effort"] = effort };
                    }
                }
                else
                {
                    parameters["thinking"] = new JsonObject
                    {
                        ["type"] = "enabled",
                        ["budget_tokens"] = options.ThinkingBudgetTokens is > 0 ? options.ThinkingBudgetTokens : 1024,
                        ["display"] = display,
                    };
                }
            }
            else if (options?.ThinkingEnabled == false
                     && (model.ThinkingLevelMap is not { } levelMap
                         || !levelMap.Has("off")
                         || levelMap["off"] is not null))
            {
                parameters["thinking"] = new JsonObject { ["type"] = "disabled" };
            }
        }

        if (options?.Metadata?["user_id"] is JsonValue userIdValue
            && userIdValue.TryGetValue<string>(out var userId))
        {
            parameters["metadata"] = new JsonObject { ["user_id"] = userId };
        }

        if (options?.ToolChoice is { } toolChoice)
        {
            if (toolChoice is JsonValue choiceValue && choiceValue.TryGetValue<string>(out var choice))
            {
                parameters["tool_choice"] = new JsonObject { ["type"] = choice };
            }
            else
            {
                parameters["tool_choice"] = toolChoice.DeepClone();
            }
        }

        if (compat.AllowedFallbackModels is { Count: > 0 } fallbacks)
        {
            parameters["fallbacks"] = new JsonArray(fallbacks
                .Select(fallback => (JsonNode)new JsonObject { ["model"] = fallback.Model })
                .ToArray());
        }

        return parameters;
    }

    /// <summary>
    /// 消息转换。对应 TS <c>convertMessages</c> 的公开薄封装（无 OAuth / 缓存 / 中段 system）。
    /// </summary>
    public static JsonArray ConvertMessages(IReadOnlyList<ChatMessage> messages)
        => ConvertMessagesCore(messages, false, null, false, null, null).Messages;

    private sealed record ConvertedMessages(JsonArray Messages, Dictionary<int, string> AssistantLevels);

    private static ConvertedMessages ConvertMessagesCore(
        IReadOnlyList<ChatMessage> transformedMessages,
        bool isOAuthToken,
        JsonObject? cacheControl,
        bool allowEmptySignature,
        string? managedProvider,
        Func<IReadOnlyList<ToolDefinition>, JsonArray>? convertToolDefinitions)
    {
        var parameters = new JsonArray();
        var assistantLevels = new Dictionary<int, string>();
        // Later system messages are held back and emitted directly before the next assistant
        // message (or at the end of the transcript). Anthropic requires `tool_result` blocks to
        // immediately follow their `tool_use`.
        var pendingSystemMessages = new List<JsonObject>();

        void FlushPendingSystemMessages()
        {
            foreach (var message in pendingSystemMessages) parameters.Add(message);
            pendingSystemMessages.Clear();
        }

        for (var i = 0; i < transformedMessages.Count; i++)
        {
            var message = transformedMessages[i];

            if (message is SystemMessage system)
            {
                var text = Text.RenderSystemMessageUpdate(system);
                var blocks = new JsonArray();
                if (text.Length > 0)
                {
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = SanitizeUnicode.SanitizeSurrogates(text),
                    });
                }
                if (convertToolDefinitions is not null)
                {
                    var added = system.ToolsAdded ?? [];
                    var redefined = added.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
                    foreach (var name in system.ToolsRemoved ?? [])
                    {
                        // A new definition under the same name replaces the old one.
                        if (redefined.Contains(name)) continue;
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_removal",
                            ["tool"] = new JsonObject
                            {
                                ["type"] = "tool_reference",
                                ["name"] = isOAuthToken ? ToClaudeCodeName(name) : name,
                            },
                        });
                    }
                    foreach (var definition in convertToolDefinitions(added))
                    {
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_addition",
                            ["tool"] = new JsonObject
                            {
                                ["type"] = "tool_definition",
                                ["definition"] = definition!.DeepClone(),
                            },
                        });
                    }
                }
                if (blocks.Count > 0)
                {
                    pendingSystemMessages.Add(new JsonObject { ["role"] = "system", ["content"] = blocks });
                }
            }
            else if (message is UserMessage user)
            {
                if (user.Content is MessageContent.Text textContent)
                {
                    if (textContent.Value.Trim().Length > 0)
                    {
                        parameters.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = SanitizeUnicode.SanitizeSurrogates(textContent.Value),
                        });
                    }
                }
                else if (user.Content is MessageContent.Blocks blocksContent)
                {
                    var blocks = new JsonArray();
                    foreach (var item in blocksContent.Value)
                    {
                        switch (item)
                        {
                            case TextContent text:
                                if (text.Text.Trim().Length == 0) continue;
                                blocks.Add(new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = SanitizeUnicode.SanitizeSurrogates(text.Text),
                                });
                                break;
                            case ImageContent image:
                                blocks.Add(new JsonObject
                                {
                                    ["type"] = "image",
                                    ["source"] = new JsonObject
                                    {
                                        ["type"] = "base64",
                                        ["media_type"] = image.MimeType ?? "image/png",
                                        ["data"] = image.Data,
                                    },
                                });
                                break;
                        }
                    }
                    if (blocks.Count == 0) continue;
                    parameters.Add(new JsonObject { ["role"] = "user", ["content"] = blocks });
                }
            }
            else if (message is AssistantMessage assistant)
            {
                FlushPendingSystemMessages();
                var blocks = new JsonArray();
                foreach (var block in assistant.Content)
                {
                    switch (block)
                    {
                        case TextContent text:
                            if (text.Text.Trim().Length == 0) continue;
                            blocks.Add(new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = SanitizeUnicode.SanitizeSurrogates(text.Text),
                            });
                            break;

                        case ThinkingContent thinking:
                            // Redacted thinking: pass the opaque payload back as redacted_thinking.
                            if (thinking.Redacted == true)
                            {
                                blocks.Add(new JsonObject
                                {
                                    ["type"] = "redacted_thinking",
                                    ["data"] = thinking.Signature ?? "",
                                });
                                continue;
                            }
                            var signature = thinking.Signature;
                            var hasSignature = !string.IsNullOrEmpty(signature) && signature.Trim().Length > 0;
                            if (thinking.Thinking.Trim().Length == 0 && !hasSignature) continue;
                            // A missing/empty signature (e.g. from an aborted stream) becomes plain
                            // text unless the model is marked as accepting empty signatures.
                            if (!hasSignature)
                            {
                                if (allowEmptySignature)
                                {
                                    blocks.Add(new JsonObject
                                    {
                                        ["type"] = "thinking",
                                        ["thinking"] = SanitizeUnicode.SanitizeSurrogates(thinking.Thinking),
                                        ["signature"] = "",
                                    });
                                }
                                else
                                {
                                    blocks.Add(new JsonObject
                                    {
                                        ["type"] = "text",
                                        ["text"] = SanitizeUnicode.SanitizeSurrogates(thinking.Thinking),
                                    });
                                }
                            }
                            else
                            {
                                blocks.Add(new JsonObject
                                {
                                    ["type"] = "thinking",
                                    ["thinking"] = SanitizeUnicode.SanitizeSurrogates(thinking.Thinking),
                                    ["signature"] = signature,
                                });
                            }
                            break;

                        case ToolCallContent toolCall:
                            blocks.Add(new JsonObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = toolCall.Id,
                                ["name"] = isOAuthToken ? ToClaudeCodeName(toolCall.Name) : toolCall.Name,
                                ["input"] = ToJsonNode(toolCall.Arguments),
                            });
                            break;
                    }
                }
                if (blocks.Count == 0) continue;
                var messageIndex = parameters.Count;
                parameters.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
                if (managedProvider is not null
                    && assistant.Api == "anthropic-messages"
                    && assistant.Provider == managedProvider
                    && IsAnthropicEffort(assistant.ProviderThinkingLevel))
                {
                    assistantLevels[messageIndex] = assistant.ProviderThinkingLevel!;
                }
            }
            else if (message is ToolResultMessage)
            {
                // Collect all consecutive toolResult messages (needed for z.ai's Anthropic endpoint).
                var toolResults = new JsonArray();
                var j = i;
                while (j < transformedMessages.Count && transformedMessages[j] is ToolResultMessage)
                {
                    toolResults.Add(ConvertToolResult((ToolResultMessage)transformedMessages[j]));
                    j++;
                }
                i = j - 1;
                parameters.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
            }
        }

        FlushPendingSystemMessages();

        // Add cache_control to the last user or system message to cache conversation history.
        if (cacheControl is not null && parameters.Count > 0)
        {
            var lastMessage = (JsonObject)parameters[^1]!;
            var role = lastMessage.Str("role");
            if (role is "user" or "system")
            {
                if (lastMessage["content"] is JsonArray content)
                {
                    if (content.Count > 0 && content[^1] is JsonObject lastBlock
                        && lastBlock.Str("type") is "text" or "image" or "tool_result" or "tool_addition" or "tool_removal")
                    {
                        lastBlock["cache_control"] = cacheControl.DeepClone();
                    }
                }
                else if (lastMessage["content"] is JsonValue contentValue
                         && contentValue.TryGetValue<string>(out var stringContent))
                {
                    lastMessage["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = stringContent,
                        ["cache_control"] = cacheControl.DeepClone(),
                    });
                }
            }
        }

        return new ConvertedMessages(parameters, assistantLevels);
    }

    private static JsonObject ConvertToolResult(ToolResultMessage message) => new()
    {
        ["type"] = "tool_result",
        ["tool_use_id"] = message.ToolCallId,
        ["content"] = ConvertContentBlocks(message.Content),
        ["is_error"] = message.IsError,
    };

    private static JsonNode ConvertContentBlocks(IReadOnlyList<ContentBlock> content)
    {
        var hasImages = content.Any(block => block is ImageContent);
        if (!hasImages)
        {
            return JsonValue.Create(SanitizeUnicode.SanitizeSurrogates(string.Join("\n",
                content.OfType<TextContent>().Select(text => text.Text))))!;
        }

        var blocks = new JsonArray();
        var hasText = false;
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent text:
                    hasText = true;
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = SanitizeUnicode.SanitizeSurrogates(text.Text),
                    });
                    break;
                case ImageContent image:
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = image.MimeType ?? "image/png",
                            ["data"] = image.Data,
                        },
                    });
                    break;
            }
        }
        if (!hasText)
        {
            blocks.Insert(0, new JsonObject { ["type"] = "text", ["text"] = "(see attached image)" });
        }
        return blocks;
    }

    private static JsonArray InsertThinkingLevelMessages(ConvertedMessages converted, string activeEffort)
    {
        var messages = new JsonArray();
        for (var index = 0; index < converted.Messages.Count; index++)
        {
            if (converted.AssistantLevels.TryGetValue(index, out var historicalEffort))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = new JsonArray(),
                    ["output_config"] = new JsonObject { ["effort"] = historicalEffort },
                });
            }
            messages.Add(converted.Messages[index]!.DeepClone());
        }
        messages.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = new JsonArray(),
            ["output_config"] = new JsonObject { ["effort"] = activeEffort },
        });
        return messages;
    }

    private static bool IsAnthropicEffort(string? value)
        => value is "low" or "medium" or "high" or "xhigh" or "max";

    private static string ToClaudeCodeName(string name)
        => ClaudeCodeToolLookup.TryGetValue(name.ToLowerInvariant(), out var canonical) ? canonical : name;

    private static string FromClaudeCodeName(string name, IReadOnlyList<ToolDefinition>? tools)
    {
        if (tools is { Count: > 0 })
        {
            var matched = tools.FirstOrDefault(tool =>
                string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));
            if (matched is not null) return matched.Name;
        }
        return name;
    }

    private static string? ResolveCacheRetention(
        string? cacheRetention, IReadOnlyDictionary<string, string>? env)
        => cacheRetention
           ?? (ProviderEnvValue.Get("PI_CACHE_RETENTION", env) == "long" ? "long" : "short");

    private static JsonObject? GetCacheControl(
        ModelSpec model,
        string? cacheRetention,
        IReadOnlyDictionary<string, string>? env,
        AnthropicMessagesCompat compat)
    {
        var retention = ResolveCacheRetention(cacheRetention, env);
        if (retention == "none") return null;
        var cacheControl = new JsonObject { ["type"] = "ephemeral" };
        if (retention == "long" && compat.SupportsLongCacheRetention)
        {
            cacheControl["ttl"] = "1h";
        }
        return cacheControl;
    }

    private static List<string> GetBetaFeatures(
        ModelSpec model,
        TranscriptContext context,
        bool isOAuthToken,
        bool nativeToolChanges,
        SimpleStreamOptions? options,
        AnthropicMessagesCompat compat)
    {
        string? configuredFeatures = null;
        var configuredExplicitly = false;
        void Scan(IEnumerable<KeyValuePair<string, string?>>? headers)
        {
            if (headers is null) return;
            foreach (var (name, value) in headers)
            {
                if (!string.Equals(name, "anthropic-beta", StringComparison.OrdinalIgnoreCase)) continue;
                configuredFeatures = value;
                configuredExplicitly = true;
            }
        }
        if (model.Headers is not null)
        {
            Scan(model.Headers.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value)));
        }
        Scan(options?.Headers);

        if (configuredExplicitly)
        {
            if (configuredFeatures is null) return [];
            return configuredFeatures
                .Split(',')
                .Select(feature => feature.Trim())
                .Where(feature => feature.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var features = new List<string>();
        if (isOAuthToken) features.AddRange(["claude-code-20250219", "oauth-2025-04-20"]);
        if (Transcript.GetCurrentTools(context.Messages).Count > 0
            && !compat.SupportsEagerToolInputStreaming)
        {
            features.Add(FineGrainedToolStreamingBeta);
        }
        if (model.Reasoning
            && options?.ThinkingEnabled == true
            && (options.InterleavedThinking ?? true)
            && !compat.ForceAdaptiveThinking)
        {
            features.Add(InterleavedThinkingBeta);
        }
        if (compat.AllowedFallbackModels is { Count: > 0 }) features.Add(ServerSideFallbackBeta);
        if (compat.SupportsMidConvoEffort)
        {
            features.Add(MidConversationOutputConfigBeta);
            features.Add(ThinkingBindingControlsBeta);
        }
        if (nativeToolChanges) features.Add(InlineToolsBeta);
        return features.Distinct(StringComparer.Ordinal).ToList();
    }

    private static JsonArray ConvertTools(
        IReadOnlyList<ToolDefinition>? tools,
        bool isOAuthToken,
        bool supportsEagerToolInputStreaming,
        bool supportsStrictTools,
        JsonObject? cacheControl)
    {
        var result = new JsonArray();
        if (tools is null) return result;

        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            var strict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(
                tool, supportsStrictTools, IsAnthropicStrictUnsupportedKeyword);
            var parameters = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict) ?? new JsonObject();

            JsonObject inputSchema;
            if (strict == true)
            {
                inputSchema = (JsonObject)parameters.DeepClone();
                inputSchema["type"] = "object";
                inputSchema["properties"] = parameters["properties"]?.DeepClone() ?? new JsonObject();
                inputSchema["required"] = parameters["required"]?.DeepClone() ?? new JsonArray();
            }
            else
            {
                inputSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = parameters["properties"]?.DeepClone() ?? new JsonObject(),
                    ["required"] = parameters["required"]?.DeepClone() ?? new JsonArray(),
                };
            }

            var definition = new JsonObject
            {
                ["name"] = isOAuthToken ? ToClaudeCodeName(tool.Name) : tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = inputSchema,
            };
            if (supportsEagerToolInputStreaming) definition["eager_input_streaming"] = true;
            if (strict == true) definition["strict"] = true;
            if (cacheControl is not null && index == tools.Count - 1)
            {
                definition["cache_control"] = cacheControl.DeepClone();
            }
            result.Add(definition);
        }
        return result;
    }

    private static bool IsAnthropicStrictUnsupportedKeyword(string key, JsonNode? value)
    {
        if (AnthropicStrictUnsupportedKeywords.Contains(key)) return true;
        if (key == "minItems")
        {
            if (value is JsonValue number)
            {
                if (number.TryGetValue<double>(out var asDouble)) return asDouble != 0 && asDouble != 1;
                if (number.TryGetValue<long>(out var asLong)) return asLong != 0 && asLong != 1;
            }
            return true;
        }
        if (key == "format")
        {
            return value is not JsonValue formatValue
                || !formatValue.TryGetValue<string>(out var format)
                || !AnthropicStrictStringFormats.Contains(format);
        }
        return false;
    }

    /// <summary>Normalize tool call IDs to Anthropic's required pattern and length.</summary>
    public static string NormalizeToolCallId(string id)
    {
        var builder = new StringBuilder(id.Length);
        foreach (var ch in id)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        }
        var normalized = builder.ToString();
        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static JsonNode ToJsonNode(object? value)
    {
        if (value is null) return new JsonObject();
        if (value is JsonNode node) return node.DeepClone();
        if (value is JsonElement element) return JsonNode.Parse(element.GetRawText()) ?? new JsonObject();
        return JsonSerializer.SerializeToNode(value) ?? new JsonObject();
    }

    /// <summary>stop_reason → 停止原因（宽松版，未知值按 stop 处理）。保留旧公开契约。</summary>
    public static StopReason MapStopReason(string stop) => stop switch
    {
        "end_turn" or "stop_sequence" or "pause_turn" => StopReason.Stop,
        "max_tokens" => StopReason.Length,
        "tool_use" => StopReason.ToolUse,
        "refusal" or "sensitive" => StopReason.Error,
        _ => StopReason.Stop,
    };

    private static (StopReason StopReason, string? ErrorMessage) MapStopReasonDetailed(
        string reason, JsonObject? stopDetails)
        => reason switch
        {
            "end_turn" => (StopReason.Stop, null),
            "max_tokens" => (StopReason.Length, null),
            "tool_use" => (StopReason.ToolUse, null),
            "refusal" => (StopReason.Error,
                stopDetails?.Str("explanation") is { Length: > 0 } explanation
                    ? explanation
                    : "The model refused to complete the request"),
            // Stop is good enough -> resubmit.
            "pause_turn" => (StopReason.Stop, null),
            // We don't supply stop sequences, so this should never happen.
            "stop_sequence" => (StopReason.Stop, null),
            "sensitive" => (StopReason.Error, "Provider stopped with: sensitive"),
            _ => throw new InvalidOperationException($"Unhandled stop reason: {reason}"),
        };

    /// <summary>Anthropic 流式生成。对应 TS <c>stream()</c>。</summary>
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
        var providerThinkingLevel = compat.SupportsMidConvoEffort ? options?.Effort ?? "high" : null;
        var client = httpClient ?? OAuthHttp.Shared;
        var signal = options?.Signal ?? default;

        _ = Task.Run(async () =>
        {
            var output = new OpenAiResponsesShared.MutableAssistantMessage(
                model.Id, model.Api, model.Provider)
            {
                Usage = new Usage(0, 0) { TotalTokens = 0, Cost = UsageCost.Zero },
                ProviderThinkingLevel = providerThinkingLevel,
            };
            var blocks = new List<StreamBlock>();

            try
            {
                var apiKey = options?.ApiKey;
                var hasAuth = HasRequestAuth(apiKey, options?.Headers);
                var federation = model.Provider == "anthropic" && !hasAuth
                    ? GetFederationConfig(options?.Env)
                    : null;
                if (!hasAuth && federation is null)
                {
                    throw new InvalidOperationException($"No API key for provider: {model.Provider}");
                }

                var isOAuth = IsOAuthToken(apiKey);
                var isCopilot = model.Provider == "github-copilot";
                var cacheRetention = ResolveCacheRetention(options?.CacheRetention, options?.Env);
                var cacheSessionId = cacheRetention == "none" ? null : options?.SessionId;
                var currentTools = Transcript.GetCurrentTools(normalizedContext.Messages);

                var payload = BuildParams(model, normalizedContext, isOAuth, options, compat);
                if (options?.OnPayload is { } onPayload)
                {
                    var transformed = await onPayload(payload, model).ConfigureAwait(false);
                    if (transformed is not null) payload = transformed;
                    payload["stream"] = true;
                }

                var headers = BuildDefaultHeaders(
                    model, normalizedContext, options, isOAuth, isCopilot, compat, cacheSessionId);
                if (!string.IsNullOrEmpty(apiKey))
                {
                    if (isOAuth || isCopilot)
                    {
                        headers["Authorization"] = $"Bearer {apiKey}";
                    }
                    else
                    {
                        headers["x-api-key"] = apiKey;
                    }
                }
                if (!IsHeaderSuppressed(options?.Headers, "anthropic-version"))
                {
                    headers["anthropic-version"] = ApiVersion;
                }
                if (payload["betas"] is JsonArray betaArray)
                {
                    payload.Remove("betas");
                    var features = betaArray
                        .OfType<JsonValue>()
                        .Select(value => value.TryGetValue<string>(out var feature) ? feature : null)
                        .Where(feature => !string.IsNullOrEmpty(feature))
                        .ToList();
                    if (features.Count > 0 && !IsHeaderSuppressed(options?.Headers, "anthropic-beta"))
                    {
                        headers["anthropic-beta"] = string.Join(",", features!);
                    }
                }

                var baseUrl = (options?.BaseUrl ?? model.BaseUrl).TrimEnd('/');
                if (federation is not null)
                {
                    // SDK 在发请求前用身份令牌换取 Bearer token，并追加 OAuth beta；
                    // 令牌缓存（含 120s/30s 两档预刷新）由 AnthropicFederation 负责。
                    var accessToken = await AnthropicFederation.GetAccessTokenAsync(
                        client, baseUrl, federation, signal).ConfigureAwait(false);
                    headers["Authorization"] = $"Bearer {accessToken}";
                    AppendBetaFeature(headers, AnthropicFederation.OAuthBetaHeader);
                }
                var url = $"{baseUrl}/v1/messages";

                var response = await ProviderRetry.RetryAsync(async () =>
                {
                    using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(signal);
                    if (options?.TimeoutMs is { } timeout) perCallCts.CancelAfter(timeout);
                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                    };
                    foreach (var (key, value) in headers)
                    {
                        request.Headers.TryAddWithoutValidation(key, value);
                    }
                    try
                    {
                        var httpResponse = await client.SendAsync(
                            request, HttpCompletionOption.ResponseHeadersRead, perCallCts.Token)
                            .ConfigureAwait(false);
                        if (!httpResponse.IsSuccessStatusCode)
                        {
                            var body = await httpResponse.Content.ReadAsStringAsync(perCallCts.Token)
                                .ConfigureAwait(false);
                            throw new ProviderHttpException(
                                (int)httpResponse.StatusCode, body,
                                $"{(int)httpResponse.StatusCode}: {body}",
                                headers: ResponseHeaders(httpResponse));
                        }
                        return httpResponse;
                    }
                    catch (OperationCanceledException) when (signal.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("Request aborted", signal);
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
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(signal, cancellationToken);

                var sawMessageStart = false;
                var sawMessageEnd = false;
                JsonArray? inputTransformations = null;
                var usageModel = model;

                await foreach (var sse in AiSse.EventsAsync(contentStream, lifetime.Token).ConfigureAwait(false))
                {
                    if (sse.Event == "error")
                    {
                        throw new InvalidOperationException(sse.Data);
                    }
                    if (sse.Event is null || !AnthropicMessageEvents.Contains(sse.Event)) continue;

                    JsonObject eventObject;
                    try
                    {
                        eventObject = JsonParse.ParseJsonWithRepair(sse.Data) as JsonObject
                            ?? throw new JsonException("SSE data is not a JSON object");
                    }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException(
                            $"Could not parse Anthropic SSE event {sse.Event}: {error.Message}; data={sse.Data}");
                    }

                    if (options?.OnProviderStreamEvent is { } observer)
                    {
                        await observer(eventObject, model).ConfigureAwait(false);
                    }

                    var eventType = eventObject.Str("type");
                    if (eventType == "message_start") sawMessageStart = true;
                    else if (eventType == "message_stop") sawMessageEnd = true;

                    switch (eventType)
                    {
                        case "message_start":
                        {
                            var message = eventObject.Obj("message");
                            output.ResponseId = message?.Str("id");
                            if (message?["input_transformations"] is JsonArray transformations)
                            {
                                inputTransformations = (JsonArray)transformations.DeepClone();
                            }
                            var responseModel = message?.Str("model");
                            if (responseModel is not null && responseModel != model.Id)
                            {
                                output.ResponseModel = responseModel;
                                var fallbackCost = compat.AllowedFallbackModels?
                                    .FirstOrDefault(fallback => fallback.Provider == model.Provider
                                        && fallback.Model == responseModel)?.Cost;
                                usageModel = fallbackCost is not null
                                    ? model with { Id = responseModel, Cost = fallbackCost }
                                    : model;
                            }
                            if (message?.Obj("usage") is { } usage)
                            {
                                var input = usage.Num("input_tokens") ?? 0;
                                var outputTokens = usage.Num("output_tokens") ?? 0;
                                var cacheRead = usage.Num("cache_read_input_tokens") ?? 0;
                                var cacheWrite = usage.Num("cache_creation_input_tokens") ?? 0;
                                var cacheWrite1h = usage.Obj("cache_creation")?.Num("ephemeral_1h_input_tokens") ?? 0;
                                output.Usage = new Usage(
                                    (long)input, (long)outputTokens, (long)cacheRead, (long)cacheWrite)
                                {
                                    CacheWrite1h = (long)cacheWrite1h,
                                    TotalTokens = (long)(input + outputTokens + cacheRead + cacheWrite),
                                };
                                output.Usage = ModelOperations.CalculateCost(usageModel, output.Usage);
                            }
                            break;
                        }

                        case "content_block_start":
                        {
                            var wireIndex = ReadIndex(eventObject);
                            if (eventObject.Obj("content_block") is not { } contentBlock) break;
                            var blockType = contentBlock.Str("type");

                            if (blockType == "fallback")
                            {
                                if (output.Content.Count > 0)
                                {
                                    throw new InvalidOperationException(
                                        "Anthropic performed an unsupported mid-output model fallback");
                                }
                                break;
                            }

                            var contentIndex = output.Content.Count;
                            switch (blockType)
                            {
                                case "text":
                                    blocks.Add(new StreamBlock
                                    {
                                        WireIndex = wireIndex,
                                        Kind = "text",
                                        ContentIndex = contentIndex,
                                        Text = new StringBuilder(contentBlock.Str("text") ?? ""),
                                    });
                                    Rebuild(output, blocks);
                                    stream.Push(new AssistantMessageEvent.TextStart(
                                        contentIndex, output.Snapshot()));
                                    break;

                                case "thinking":
                                    blocks.Add(new StreamBlock
                                    {
                                        WireIndex = wireIndex,
                                        Kind = "thinking",
                                        ContentIndex = contentIndex,
                                        Text = new StringBuilder(contentBlock.Str("thinking") ?? ""),
                                        Signature = contentBlock.Str("signature") ?? "",
                                    });
                                    Rebuild(output, blocks);
                                    stream.Push(new AssistantMessageEvent.ThinkingStart(
                                        contentIndex, output.Snapshot()));
                                    break;

                                case "redacted_thinking":
                                    blocks.Add(new StreamBlock
                                    {
                                        WireIndex = wireIndex,
                                        Kind = "thinking",
                                        ContentIndex = contentIndex,
                                        Text = new StringBuilder("[Reasoning redacted]"),
                                        Signature = contentBlock.Str("data") ?? "",
                                        Redacted = true,
                                    });
                                    Rebuild(output, blocks);
                                    stream.Push(new AssistantMessageEvent.ThinkingStart(
                                        contentIndex, output.Snapshot()));
                                    break;

                                case "tool_use":
                                    blocks.Add(new StreamBlock
                                    {
                                        WireIndex = wireIndex,
                                        Kind = "toolCall",
                                        ContentIndex = contentIndex,
                                        ToolId = contentBlock.Str("id") ?? "",
                                        ToolName = isOAuth
                                            ? FromClaudeCodeName(contentBlock.Str("name") ?? "", currentTools)
                                            : contentBlock.Str("name") ?? "",
                                        Arguments = contentBlock.Obj("input")?.DeepClone() as JsonObject
                                            ?? new JsonObject(),
                                    });
                                    Rebuild(output, blocks);
                                    stream.Push(new AssistantMessageEvent.ToolCallStart(
                                        contentIndex, output.Snapshot()));
                                    break;
                            }
                            break;
                        }

                        case "content_block_delta":
                        {
                            var wireIndex = ReadIndex(eventObject);
                            if (eventObject.Obj("delta") is not { } delta) break;
                            var block = blocks.FirstOrDefault(candidate => candidate.WireIndex == wireIndex);
                            if (block is null) break;

                            switch (delta.Str("type"))
                            {
                                case "text_delta" when delta.Str("text") is { } textDelta:
                                    if (block.Kind != "text") break;
                                    block.Text.Append(textDelta);
                                    Rebuild(output, blocks);
                                    stream.Push(new AssistantMessageEvent.TextDelta(
                                        block.ContentIndex, textDelta, 0, output.Snapshot()));
                                    break;

                                case "thinking_delta" when delta.Str("thinking") is { } thinkingDelta:
                                    if (block.Kind != "thinking") break;
                                    block.Text.Append(thinkingDelta);
                                    Rebuild(output, blocks);
                                    stream.Push(new AssistantMessageEvent.ThinkingDelta(
                                        block.ContentIndex, thinkingDelta, 0, block.Signature,
                                        output.Snapshot()));
                                    break;

                                case "input_json_delta" when delta.Str("partial_json") is { } jsonDelta:
                                    if (block.Kind != "toolCall") break;
                                    block.PartialJson.Append(jsonDelta);
                                    block.Arguments = JsonParse.ParseStreamingJson(block.PartialJson.ToString());
                                    Rebuild(output, blocks);
                                    stream.Push(new AssistantMessageEvent.ToolCallDelta(
                                        block.ContentIndex, block.ContentIndex, jsonDelta, output.Snapshot()));
                                    break;

                                case "signature_delta":
                                    if (block.Kind != "thinking") break;
                                    block.Signature = (block.Signature ?? "") + (delta.Str("signature") ?? "");
                                    Rebuild(output, blocks);
                                    break;
                            }
                            break;
                        }

                        case "content_block_stop":
                        {
                            var wireIndex = ReadIndex(eventObject);
                            var block = blocks.FirstOrDefault(candidate => candidate.WireIndex == wireIndex);
                            if (block is null) break;
                            if (block.Finalized) break;
                            block.Finalized = true;

                            switch (block.Kind)
                            {
                                case "text":
                                    Rebuild(output, blocks);
                                    stream.Push(new AssistantMessageEvent.TextEnd(
                                        block.ContentIndex, block.Text.ToString(), output.Snapshot()));
                                    break;

                                case "thinking":
                                    Rebuild(output, blocks);
                                    stream.Push(new AssistantMessageEvent.ThinkingEnd(
                                        block.ContentIndex, block.Text.ToString(), output.Snapshot()));
                                    break;

                                case "toolCall":
                                    block.Arguments = JsonParse.ParseStreamingJson(block.PartialJson.ToString());
                                    Rebuild(output, blocks);
                                    var toolCall = (ToolCallContent)output.Content[block.ContentIndex];
                                    stream.Push(new AssistantMessageEvent.ToolCallEnd(
                                        block.ContentIndex, toolCall, output.Snapshot()));
                                    break;
                            }
                            break;
                        }

                        case "message_delta":
                        {
                            if (eventObject["input_transformations"] is JsonArray transformations)
                            {
                                inputTransformations = (JsonArray)transformations.DeepClone();
                            }
                            if (eventObject.Obj("delta") is { } delta && delta.Str("stop_reason") is { } stopReason)
                            {
                                output.RawStopReason = stopReason;
                                var (mapped, errorMessage) = MapStopReasonDetailed(
                                    stopReason, delta.Obj("stop_details"));
                                output.StopReason = mapped;
                                if (errorMessage is not null) output.ErrorMessage = errorMessage;
                            }
                            if (eventObject.Obj("usage") is { } usage)
                            {
                                var current = output.Usage ?? new Usage(0, 0);
                                var input = current.Input;
                                var outputTokens = current.Output;
                                var cacheRead = current.CacheRead;
                                var cacheWrite = current.CacheWrite;
                                var cacheWrite1h = current.CacheWrite1h;
                                var reasoning = current.Reasoning;

                                // Only update fields present in the delta; proxies may omit
                                // input/cache counts there and the message_start values stand.
                                if (usage["input_tokens"] is not null)
                                    input = (long)(usage.Num("input_tokens") ?? 0);
                                if (usage["output_tokens"] is not null)
                                    outputTokens = (long)(usage.Num("output_tokens") ?? 0);
                                if (usage["cache_read_input_tokens"] is not null)
                                    cacheRead = (long)(usage.Num("cache_read_input_tokens") ?? 0);
                                if (usage["cache_creation_input_tokens"] is not null)
                                    cacheWrite = (long)(usage.Num("cache_creation_input_tokens") ?? 0);

                                var cacheCreation = usage.Obj("cache_creation");
                                if (cacheCreation?["ephemeral_1h_input_tokens"] is not null)
                                    cacheWrite1h = (long)(cacheCreation.Num("ephemeral_1h_input_tokens") ?? 0);
                                var thinkingTokens = usage.Obj("output_tokens_details")?.Num("thinking_tokens");
                                if (thinkingTokens is not null) reasoning = (long)thinkingTokens;

                                output.Usage = new Usage(input, outputTokens, cacheRead, cacheWrite)
                                {
                                    CacheWrite1h = cacheWrite1h,
                                    Reasoning = reasoning,
                                };
                            }
                            // Anthropic doesn't provide total_tokens, compute from components.
                            var usageNow = output.Usage ?? new Usage(0, 0);
                            output.Usage = ModelOperations.CalculateCost(usageModel, usageNow with
                            {
                                TotalTokens = usageNow.Input + usageNow.Output
                                    + usageNow.CacheRead + usageNow.CacheWrite,
                            });
                            break;
                        }
                    }
                }

                if (signal.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Request was aborted", signal);
                }
                if (sawMessageStart && !sawMessageEnd)
                {
                    throw new InvalidOperationException("Anthropic stream ended before message_stop");
                }
                if (output.StopReason == StopReason.Pending)
                {
                    throw new InvalidOperationException("Anthropic stream ended without a stop reason");
                }
                if (output.StopReason is StopReason.Aborted or StopReason.Error)
                {
                    throw new InvalidOperationException(output.ErrorMessage ?? "An unknown error occurred");
                }
                if (inputTransformations is { Count: > 0 } transformationList)
                {
                    output.Diagnostics.Add(BuildInputTransformationsDiagnostic(transformationList));
                }

                var final = output.Snapshot();
                stream.Push(new AssistantMessageEvent.Done(output.StopReason, final));
                stream.End(final);
            }
            catch (Exception error)
            {
                var aborted = signal.IsCancellationRequested || cancellationToken.IsCancellationRequested;
                output.StopReason = aborted ? StopReason.Aborted : StopReason.Error;
                output.ErrorMessage = error is OperationCanceledException && aborted
                    ? "Request aborted"
                    : Diagnostics.FormatThrownValue(error);
                var failed = output.Snapshot();
                stream.Push(new AssistantMessageEvent.Error(
                    output.StopReason, output.ErrorMessage, failed));
                stream.End(failed);
            }
        }, CancellationToken.None);

        return stream;
    }

    /// <summary>
    /// 简单入口流式生成。对应 TS <c>streamSimple</c>：reasoning 档位映射到 adaptive effort
    /// 或旧式 thinking budget；未请求 reasoning 时显式关闭 thinking。
    /// </summary>
    public static IAssistantMessageEventStream StreamSimple(
        ModelSpec model,
        TranscriptContext context,
        SimpleStreamOptions? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var compat = GetCompat(model);
        var hasAuth = HasRequestAuth(options?.ApiKey, options?.Headers);
        var federation = model.Provider == "anthropic" && !hasAuth
            ? GetFederationConfig(options?.Env)
            : null;
        if (!hasAuth && federation is null)
        {
            throw new InvalidOperationException($"No API key for provider: {model.Provider}");
        }

        var baseOptions = FromSimple(model, context, options);
        if (string.IsNullOrEmpty(options?.Reasoning))
        {
            return Stream(model, context, baseOptions with { ThinkingEnabled = false },
                httpClient, cancellationToken);
        }

        if (compat.ForceAdaptiveThinking)
        {
            var effort = MapThinkingLevelToEffort(model, options!.Reasoning);
            return Stream(model, context, baseOptions with
            {
                ThinkingEnabled = true,
                Effort = effort,
            }, httpClient, cancellationToken);
        }

        // Undefined means the caller did not request an output cap; let the helper use the
        // model cap. Do not coerce to 0 here, or the thinking budget would consume max_tokens.
        var adjusted = SimpleOptions.AdjustMaxTokensForThinking(
            baseOptions.MaxTokens, model.MaxTokens, options!.Reasoning, options.ThinkingBudgets);
        var maxTokens = SimpleOptions.ClampMaxTokensToContext(model, context, adjusted.MaxTokens);
        return Stream(model, context, baseOptions with
        {
            MaxTokens = maxTokens,
            ThinkingEnabled = true,
            ThinkingBudgetTokens = (int)Math.Min(adjusted.ThinkingBudget, Math.Max(0, maxTokens - 1024)),
        }, httpClient, cancellationToken);
    }

    /// <summary>由 <see cref="SimpleStreamOptions"/> 装配公共字段。对应 TS <c>buildBaseOptions</c>。</summary>
    public static SimpleStreamOptions FromSimple(
        ModelSpec model, TranscriptContext context, SimpleStreamOptions? options)
    {
        var samplingParams = SimpleOptions.ResolveSamplingParams(
            model, options?.Reasoning ?? "off", options?.SamplingParams);
        var requestedMaxTokens = options?.MaxTokens ?? (int)model.MaxTokens;
        var maxTokens = SimpleOptions.ClampMaxTokensToContext(model, context, requestedMaxTokens);
        return new SimpleStreamOptions(
            ApiKey: options?.ApiKey,
            BaseUrl: options?.BaseUrl,
            MaxTokens: maxTokens,
            Temperature: options?.Temperature,
            StopSequences: options?.StopSequences,
            SessionId: options?.SessionId)
        {
            Headers = options?.Headers,
            SamplingParams = samplingParams,
            MaxRetries = options?.MaxRetries,
            MaxRetryDelayMs = options?.MaxRetryDelayMs,
            TimeoutMs = options?.TimeoutMs,
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
            ThinkingBudgets = options?.ThinkingBudgets,
            ThinkingEnabled = options?.ThinkingEnabled,
            ThinkingBudgetTokens = options?.ThinkingBudgetTokens,
            Effort = options?.Effort,
            ThinkingDisplay = options?.ThinkingDisplay,
            InterleavedThinking = options?.InterleavedThinking,
        };
    }

    private static string MapThinkingLevelToEffort(ModelSpec model, string? level)
    {
        var mapped = level is null ? null : model.ThinkingLevelMap?[level];
        if (mapped is not null) return mapped;
        return level switch
        {
            "minimal" or "low" => "low",
            "medium" => "medium",
            _ => "high",
        };
    }

    private static bool IsOAuthToken(string? apiKey)
        => apiKey is not null && apiKey.Contains("sk-ant-oat", StringComparison.Ordinal);

    private static bool HasRequestAuth(
        string? apiKey, IReadOnlyDictionary<string, string?>? headers)
        => !string.IsNullOrEmpty(apiKey)
           || HasHeader(headers, "authorization")
           || HasHeader(headers, "x-api-key")
           || HasHeader(headers, "cf-aig-authorization");

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

    private static bool IsHeaderSuppressed(
        IReadOnlyDictionary<string, string?>? headers, string name)
        => headers is not null
           && headers.Any(entry => entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
               && entry.Value is null);

    /// <summary>把 beta 特性追加到已有的 <c>anthropic-beta</c> 头（对应 SDK prepareRequest）。</summary>
    private static void AppendBetaFeature(Dictionary<string, string> headers, string feature)
    {
        headers.TryGetValue("anthropic-beta", out var existing);
        var features = string.IsNullOrWhiteSpace(existing)
            ? new List<string>()
            : existing.Split(',')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToList();
        if (!features.Contains(feature, StringComparer.Ordinal)) features.Add(feature);
        headers["anthropic-beta"] = string.Join(",", features);
    }

    private static AnthropicFederationConfig? GetFederationConfig(
        IReadOnlyDictionary<string, string>? env)
    {
        var ruleId = ProviderEnvValue.Get(AnthropicEnv.FederationRuleId, env);
        var organizationId = ProviderEnvValue.Get(AnthropicEnv.OrganizationId, env);
        var identityTokenFile = ProviderEnvValue.Get(AnthropicEnv.IdentityTokenFile, env);
        if (string.IsNullOrEmpty(ruleId)
            || string.IsNullOrEmpty(organizationId)
            || string.IsNullOrEmpty(identityTokenFile))
        {
            return null;
        }
        return new AnthropicFederationConfig(
            organizationId,
            ruleId,
            ProviderEnvValue.Get(AnthropicEnv.ServiceAccountId, env),
            ProviderEnvValue.Get(AnthropicEnv.WorkspaceId, env),
            identityTokenFile);
    }

    private static Dictionary<string, string> BuildDefaultHeaders(
        ModelSpec model,
        TranscriptContext context,
        SimpleStreamOptions? options,
        bool isOAuth,
        bool isCopilot,
        AnthropicMessagesCompat compat,
        string? sessionId)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = PiUserAgent.Get(),
            ["accept"] = "application/json",
            ["anthropic-dangerous-direct-browser-access"] = "true",
        };

        if (isCopilot)
        {
            foreach (var (key, value) in GitHubCopilotHeaders.BuildDynamicHeaders(
                context.Messages, GitHubCopilotHeaders.HasVisionInput(context.Messages)))
            {
                merged[key] = value;
            }
        }
        else if (isOAuth)
        {
            merged["user-agent"] = $"claude-cli/{ClaudeCodeVersion}";
            merged["x-app"] = "cli";
        }
        else if (sessionId is not null && compat.SendSessionAffinityHeaders)
        {
            var header = compat.SessionAffinityFormat == "openrouter"
                ? "x-session-id"
                : "x-session-affinity";
            merged[header] = sessionId;
        }

        void Apply(IEnumerable<KeyValuePair<string, string?>>? source)
        {
            if (source is null) return;
            foreach (var (key, value) in source)
            {
                merged.Remove(key);
                if (value is not null) merged[key] = value;
            }
        }

        if (model.Headers is not null)
        {
            Apply(model.Headers.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value)));
        }
        Apply(options?.Headers);
        return merged;
    }

    private static IReadOnlyDictionary<string, string> ResponseHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in response.Headers)
        {
            headers[key] = string.Join(",", value);
        }
        foreach (var (key, value) in response.Content.Headers)
        {
            headers[key] = string.Join(",", value);
        }
        return headers;
    }

    private static int ReadIndex(JsonObject payload)
        => payload["index"] is JsonValue value && value.TryGetValue<int>(out var index) ? index : 0;

    private static AssistantMessageDiagnostic BuildInputTransformationsDiagnostic(JsonArray transformations)
    {
        var items = new JsonArray();
        foreach (var transformation in transformations.OfType<JsonObject>())
        {
            var item = new JsonObject();
            if (transformation.Str("type") is { } type) item["type"] = type;
            if (transformation.Str("path") is { } path) item["path"] = path;
            if (transformation.Str("reason") is { } reason) item["reason"] = reason;
            items.Add(item);
        }
        return new AssistantMessageDiagnostic(
            "anthropic_input_transformations",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Details: new JsonObject { ["transformations"] = items });
    }

    private sealed class StreamBlock
    {
        public int WireIndex { get; init; }

        public required string Kind { get; init; }

        public int ContentIndex { get; init; }

        public StringBuilder Text { get; init; } = new();

        public string? Signature { get; set; }

        public bool Redacted { get; init; }

        public string? ToolId { get; init; }

        public string? ToolName { get; init; }

        public StringBuilder PartialJson { get; } = new();

        public JsonObject Arguments { get; set; } = [];

        public bool Finalized { get; set; }
    }

    private static void Rebuild(
        OpenAiResponsesShared.MutableAssistantMessage output, List<StreamBlock> blocks)
    {
        var content = new List<ContentBlock>();
        foreach (var block in blocks.OrderBy(candidate => candidate.ContentIndex))
        {
            switch (block.Kind)
            {
                case "text":
                    content.Add(new TextContent(block.Text.ToString()));
                    break;
                case "thinking":
                    content.Add(new ThinkingContent(block.Text.ToString(), block.Signature)
                    {
                        Redacted = block.Redacted ? true : null,
                    });
                    break;
                case "toolCall":
                    content.Add(new ToolCallContent(
                        block.ToolId ?? "", block.ToolName ?? "", block.Arguments));
                    break;
            }
        }
        output.Content.Clear();
        output.Content.AddRange(content);
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
        var contextToUse = context.Tools is { Count: > 0 }
            ? Transcript.NormalizeContext(null, context.Tools, context.Messages)
            : context;
        // 完整实现自行拼接 /v1/messages；旧调用方传入的 baseUrl 可能已带 /v1。
        var normalizedOptions = options is null ? null : options with { BaseUrl = null };
        return Task.FromResult<IAssistantMessageEventStream>(
            Stream(spec, contextToUse, normalizedOptions, httpClient: null, cancellationToken));
    }

    /// <summary>旧版入口：仅从消息与工具构建请求体。</summary>
    public static JsonObject BuildParams(
        Model model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        SimpleStreamOptions options)
    {
        var spec = ToModelSpec(model, options);
        var context = Transcript.ResolveTranscript(
            Transcript.NormalizeContext(null, tools, messages), false);
        return BuildParams(spec, context, IsOAuthToken(options.ApiKey), options);
    }

    private static ModelSpec ToModelSpec(Model model, SimpleStreamOptions? options) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Api = model.Api,
        Provider = model.Provider,
        BaseUrl = NormalizeLegacyBaseUrl(options?.BaseUrl ?? DefaultBaseUrl),
        MaxTokens = options?.MaxTokens ?? 4096,
        Reasoning = model.Reasoning is not null,
    };

    private static string NormalizeLegacyBaseUrl(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/');
        if (normalized.EndsWith("/v1", StringComparison.Ordinal))
        {
            normalized = normalized[..^3];
        }
        return normalized;
    }
}
