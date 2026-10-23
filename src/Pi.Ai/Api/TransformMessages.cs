using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Api;

/// <summary>
/// 跨 provider 的消息转录变换。对应 TS <c>api/transform-messages.ts</c>：
/// ① 不支持图片的模型把图片降级为占位文本；② 跨模型重放时思维块/签名规范化；
/// ③ 工具调用 ID 归一化；④ 为孤儿的工具调用插入合成结果，error/aborted 的
/// 助手回合整体跳过。
/// </summary>
public static class TransformMessages
{
    private const string NonVisionUserImagePlaceholder = "(image omitted: model does not support images)";
    private const string NonVisionToolImagePlaceholder = "(tool image omitted: model does not support images)";
    private const string ErrorToolImagePlaceholder = "(image attached after the tool results)";

    /// <summary>
    /// 错误工具结果拆成「纯文本结果」与「随后发送的图片」。对应 TS <c>SplitErrorToolResult</c>。
    /// </summary>
    public sealed record SplitErrorToolResult(
        IReadOnlyList<ContentBlock> Content,
        string Caption,
        IReadOnlyList<ImageContent> Images);

    /// <summary>
    /// 把错误工具结果里的图片拆出来，供拒收「错误结果带图片」的 API 使用（Anthropic，以及 Bedrock 上的
    /// Claude）：返回的 <see cref="SplitErrorToolResult.Content"/> 作为工具结果本身，
    /// <see cref="SplitErrorToolResult.Caption"/> 与图片放到该回合所有工具结果之后、同一个 user 消息里。
    /// 无需拆分时返回 null（上游 #10794）。
    /// </summary>
    public static SplitErrorToolResult? SplitErrorToolResultImages(ToolResultMessage msg)
    {
        if (!msg.IsError) return null;

        var images = msg.Content.OfType<ImageContent>().ToList();
        if (images.Count == 0) return null;

        return new SplitErrorToolResult(
            ReplaceImagesWithPlaceholder(msg.Content, ErrorToolImagePlaceholder),
            $"Images from the error result of tool call {msg.ToolCallId} ({msg.ToolName}):",
            images);
    }

    /// <summary>把连续图片块折叠成一个占位文本块。</summary>
    private static List<ContentBlock> ReplaceImagesWithPlaceholder(
        IReadOnlyList<ContentBlock> content, string placeholder)
    {
        var result = new List<ContentBlock>();
        var previousWasPlaceholder = false;

        foreach (var block in content)
        {
            if (block is ImageContent)
            {
                if (!previousWasPlaceholder)
                {
                    result.Add(new TextContent(placeholder));
                }
                previousWasPlaceholder = true;
                continue;
            }

            result.Add(block);
            previousWasPlaceholder = block is TextContent { Text: { } text } && text == placeholder;
        }

        return result;
    }

    private static bool SupportsImages(ModelSpec model) => model.Input.Contains("image");

    private static IReadOnlyList<ChatMessage> DowngradeUnsupportedImages(
        IReadOnlyList<ChatMessage> messages, ModelSpec model)
    {
        if (SupportsImages(model)) return messages;

        return messages.Select(msg => msg switch
        {
            // TS 只降级数组分支（`Array.isArray(msg.content)`），裸字符串原样通过。
            UserMessage user => user.Content switch
            {
                MessageContent.Blocks blocks => user with
                {
                    Content = MessageContent.BlocksOf(
                        ReplaceImagesWithPlaceholder(blocks.Value, NonVisionUserImagePlaceholder)),
                },
                _ => user,
            },
            ToolResultMessage toolResult => toolResult with
            {
                Content = ReplaceImagesWithPlaceholder(toolResult.Content, NonVisionToolImagePlaceholder),
            },
            _ => msg,
        }).ToList();
    }

    /// <summary>
    /// 跨 provider 兼容的消息变换。对应 TS <c>transformMessages</c>：
    /// OpenAI Responses 生成的工具调用 ID 是 450+ 字符且含 | 等特殊字符，
    /// Anthropic 系 API 要求 ^[a-zA-Z0-9_-]+$（最长 64）——由可选的
    /// <paramref name="normalizeToolCallId"/> 跨模型归一化。
    /// </summary>
    public static IReadOnlyList<ChatMessage> Transform(
        IReadOnlyList<ChatMessage> messages,
        ModelSpec model,
        Func<string, ModelSpec, AssistantMessage, string>? normalizeToolCallId = null)
    {
        // 原始工具调用 ID → 归一化 ID。
        var toolCallIdMap = new Dictionary<string, string>();
        var imageAwareMessages = DowngradeUnsupportedImages(messages, model);

        // 第一遍：变换消息（图片降级、思维块、工具调用 ID 归一化）。
        var transformed = imageAwareMessages.Select(msg =>
        {
            // system/user 原样通过。
            if (msg is SystemMessage or UserMessage) return msg;

            // toolResult：按映射归一化 toolCallId。
            if (msg is ToolResultMessage toolResult)
            {
                if (toolCallIdMap.TryGetValue(toolResult.ToolCallId, out var normalizedId)
                    && normalizedId != toolResult.ToolCallId)
                {
                    return toolResult with { ToolCallId = normalizedId };
                }
                return toolResult;
            }

            if (msg is not AssistantMessage assistantMsg) return msg;
            var isSameModel =
                assistantMsg.Provider == model.Provider
                && assistantMsg.Api == model.Api
                && assistantMsg.Model == model.Id;

            var transformedContent = new List<ContentBlock>();
            foreach (var block in assistantMsg.Content)
            {
                switch (block)
                {
                    case ThinkingContent thinking:
                    {
                        // 其它模型的思维块直接丢弃：签名与加密推理只对产它的模型有效，
                        // 而把可读思维当纯文本发过去会让新模型把它当成自身回复的一部分（上游 #10995）。
                        if (!isSameModel) break;
                        // 同模型保留加密块与带签名的块（回放需要），即使思考文本为空
                        // （OpenAI 加密推理）。
                        if (thinking.Redacted == true || thinking.Signature is not null)
                        {
                            transformedContent.Add(thinking);
                            break;
                        }
                        if (string.IsNullOrWhiteSpace(thinking.Thinking)) break;
                        transformedContent.Add(thinking);
                        break;
                    }
                    case TextContent text:
                        transformedContent.Add(isSameModel ? text : new TextContent(text.Text));
                        break;
                    case ToolCallContent toolCall:
                    {
                        var normalizedToolCall = toolCall;
                        if (!isSameModel && toolCall.ThoughtSignature is not null)
                        {
                            normalizedToolCall = toolCall with { ThoughtSignature = null };
                        }
                        if (!isSameModel && normalizeToolCallId is not null)
                        {
                            var normalized = normalizeToolCallId(toolCall.Id, model, assistantMsg);
                            if (normalized != toolCall.Id)
                            {
                                toolCallIdMap[toolCall.Id] = normalized;
                                normalizedToolCall = normalizedToolCall with { Id = normalized };
                            }
                        }
                        transformedContent.Add(normalizedToolCall);
                        break;
                    }
                    default:
                        transformedContent.Add(block);
                        break;
                }
            }

            return assistantMsg with { Content = transformedContent };
        }).ToList();

        // 第二遍：为孤儿的工具调用插入合成空结果——保留思维签名并满足 API 要求。
        var result = new List<ChatMessage>();
        var pendingToolCalls = new List<ToolCallContent>();
        var existingToolResultIds = new HashSet<string>();
        // system 消息对工具调用记账是透明的：落在工具调用与其结果之间的 system
        // 被扣住，等到结果（含合成的）之后发出，避免对稍后才应答的调用产生重复结果。
        var heldSystemMessages = new List<ChatMessage>();

        void ClosePendingToolCalls()
        {
            if (pendingToolCalls.Count > 0)
            {
                foreach (var toolCall in pendingToolCalls)
                {
                    if (!existingToolResultIds.Contains(toolCall.Id))
                    {
                        result.Add(new ToolResultMessage(
                            toolCall.Id, toolCall.Name,
                            [new TextContent("No result provided")],
                            IsError: true,
                            Timestamp: DateTimeOffset.Now.ToUnixTimeMilliseconds()));
                    }
                }
                pendingToolCalls = [];
                existingToolResultIds = new HashSet<string>();
            }
            result.AddRange(heldSystemMessages);
            heldSystemMessages.Clear();
        }

        foreach (var msg in transformed)
        {
            switch (msg)
            {
                case AssistantMessage assistantMsg:
                    // 上一条助手的孤儿工具调用在此插入合成结果。
                    ClosePendingToolCalls();

                    // error/aborted 的助手消息整体跳过：它们是不完整回合，重放会
                    // 造成 API 错误（如 OpenAI "reasoning without following item"）；
                    // 模型应从上一个有效状态重试。
                    if (assistantMsg.StopReason is StopReason.Error or StopReason.Aborted) continue;

                    var toolCalls = assistantMsg.Content
                        .OfType<ToolCallContent>()
                        .ToList();
                    if (toolCalls.Count > 0)
                    {
                        pendingToolCalls = toolCalls;
                        existingToolResultIds = new HashSet<string>();
                    }
                    result.Add(assistantMsg);
                    break;
                case ToolResultMessage toolResult:
                    existingToolResultIds.Add(toolResult.ToolCallId);
                    result.Add(toolResult);
                    break;
                case SystemMessage:
                    if (pendingToolCalls.Count > 0) heldSystemMessages.Add(msg);
                    else result.Add(msg);
                    break;
                case UserMessage:
                    // 新的用户回合打断工具流——为孤儿调用插入合成结果。
                    ClosePendingToolCalls();
                    result.Add(msg);
                    break;
                default:
                    result.Add(msg);
                    break;
            }
        }

        // 会话以未应答的工具调用收尾时，在此合成结果。
        ClosePendingToolCalls();

        return result;
    }
}
