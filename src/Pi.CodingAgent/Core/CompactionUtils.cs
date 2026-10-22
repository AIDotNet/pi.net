using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.CodingAgent.Core;

// ============================================================================
// core/compaction/utils.ts — 压缩与分支摘要的共享工具（4e-2d）
// ============================================================================

/// <summary>会话期间的文件操作集合。对应 TS <c>FileOperations</c>。</summary>
public sealed class FileOperations
{
    public HashSet<string> Read { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Written { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Edited { get; } = new(StringComparer.Ordinal);
}

/// <summary>压缩/摘要共享工具。对应 TS <c>core/compaction/utils.ts</c>。</summary>
public static class CompactionUtils
{
    /// <summary>摘要序列化里单个工具结果的最大字符数。对应 TS <c>TOOL_RESULT_MAX_CHARS</c>。</summary>
    private const int ToolResultMaxChars = 2000;

    public static FileOperations CreateFileOps() => new();

    /// <summary>
    /// 从助手消息的工具调用、或工具结果上记录的嵌套调用中提取文件操作。
    /// 对应 TS <c>extractFileOpsFromMessage</c>。
    /// </summary>
    public static void ExtractFileOpsFromMessage(ChatMessage message, FileOperations fileOps)
    {
        if (message is ToolResultMessage toolResult)
        {
            // codemode 脚本发起的调用记录在脚本的结果上。
            foreach (var call in toolResult.NestedCalls?.Calls ?? [])
            {
                AddFileOp(call.Name, call.Arguments, fileOps);
            }

            return;
        }

        if (message is not AssistantMessage assistant) return;

        foreach (var block in assistant.Content)
        {
            if (block is not ToolCallContent toolCall) continue;
            AddFileOp(toolCall.Name, toolCall.Arguments, fileOps);
        }
    }

    private static void AddFileOp(string toolName, object? arguments, FileOperations fileOps)
    {
        // arguments 在 C# 侧是反序列化残留的 object?（JsonObject / JsonElement）——两种形态都取 path。
        string? path = arguments switch
        {
            System.Text.Json.Nodes.JsonObject obj => obj.TryGetPropertyValue("path", out var node)
                ? node is JsonValue { } value && value.TryGetValue<string>(out var text) ? text : null
                : null,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } element
                => element.TryGetProperty("path", out var property) && property.ValueKind == System.Text.Json.JsonValueKind.String
                    ? property.GetString()
                    : null,
            _ => null,
        };
        if (string.IsNullOrEmpty(path)) return;

        switch (toolName)
        {
            case "read":
                fileOps.Read.Add(path);
                break;
            case "write":
                fileOps.Written.Add(path);
                break;
            case "edit":
                fileOps.Edited.Add(path);
                break;
        }
    }

    /// <summary>
    /// 从文件操作计算最终清单：readFiles（只读未改）与 modifiedFiles。
    /// 对应 TS <c>computeFileLists</c>（两侧都按字典序排序）。
    /// </summary>
    public static (IReadOnlyList<string> ReadFiles, IReadOnlyList<string> ModifiedFiles) ComputeFileLists(
        FileOperations fileOps)
    {
        var modified = new HashSet<string>(fileOps.Edited, StringComparer.Ordinal);
        modified.UnionWith(fileOps.Written);
        var readOnly = fileOps.Read.Where(file => !modified.Contains(file)).OrderBy(file => file, StringComparer.Ordinal).ToList();
        var modifiedFiles = modified.OrderBy(file => file, StringComparer.Ordinal).ToList();
        return (readOnly, modifiedFiles);
    }

    /// <summary>把文件操作格式化为 XML 段（追加到摘要）。对应 TS <c>formatFileOperations</c>。</summary>
    public static string FormatFileOperations(IReadOnlyList<string> readFiles, IReadOnlyList<string> modifiedFiles)
    {
        var sections = new List<string>();
        if (readFiles.Count > 0)
        {
            sections.Add($"<read-files>\n{string.Join("\n", readFiles)}\n</read-files>");
        }

        if (modifiedFiles.Count > 0)
        {
            sections.Add($"<modified-files>\n{string.Join("\n", modifiedFiles)}\n</modified-files>");
        }

        return sections.Count == 0 ? "" : $"\n\n{string.Join("\n\n", sections)}";
    }

    /// <summary>为摘要把文本截断到最大字符数（保头部 + 截断标记）。对应 TS <c>truncateForSummary</c>。</summary>
    private static string TruncateForSummary(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var truncatedChars = text.Length - maxChars;
        return $"{text[..maxChars]}\n\n[... {truncatedChars} more characters truncated]";
    }

    /// <summary>
    /// 把 LLM 消息序列化为文本供摘要——避免模型把它当成要继续的对话。
    /// 先调 <c>ConvertToLlm</c> 处理自定义消息类型。对应 TS <c>serializeConversation</c>。
    /// </summary>
    /// <remarks>
    /// 工具结果按 <c>ToolResultMaxChars</c> 截断。**思维链整段省略**：provider 返回的本来就是真实推理的
    /// 摘要，而回放的思维块常常不在模型活上下文里（与其请求不匹配的块会被丢掉），一起塞进去反而可能
    /// 让摘要请求比它所摘要的上下文还大。可见文本、工具调用与工具结果已经记录了发生了什么。
    /// </remarks>
    public static string SerializeConversation(IReadOnlyList<ChatMessage> messages)
    {
        var parts = new List<string>();

        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                {
                    var content = Text.ContentText(user.Content, "");
                    if (content.Length > 0) parts.Add($"[User]: {content}");
                    break;
                }
                case AssistantMessage assistant:
                {
                    var toolCalls = new List<string>();

                    foreach (var block in assistant.Content)
                    {
                        switch (block)
                        {
                            case ToolCallContent toolCall:
                            {
                                // TS 用 JSON.stringify(key=value) 列举参数；参数对象键序用字面键序近似。
                                var argsText = DescribeArguments(toolCall.Arguments);
                                toolCalls.Add($"{toolCall.Name}({argsText})");
                                break;
                            }
                        }
                    }

                    if (assistant.Content.Any(block => block is TextContent))
                    {
                        parts.Add($"[Assistant]: {Text.ContentText(assistant.Content)}");
                    }

                    if (toolCalls.Count > 0)
                    {
                        parts.Add($"[Assistant tool calls]: {string.Join("; ", toolCalls)}");
                    }

                    break;
                }
                case ToolResultMessage toolResult:
                {
                    var content = Text.ContentText(toolResult.Content, "");
                    if (content.Length > 0)
                    {
                        parts.Add($"[Tool result]: {TruncateForSummary(content, ToolResultMaxChars)}");
                    }

                    break;
                }
            }
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// 工具调用参数的 <c>k=JSON.stringify(v)</c> 逗号串。TS 直接枚举 arguments 对象；
    /// C# 参数在会话线路是 JsonObject、在 agent 内存可能是 JsonElement，两种形态都按字面键序展开。
    /// </summary>
    private static string DescribeArguments(object? arguments)
    {
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
        return arguments switch
        {
            System.Text.Json.Nodes.JsonObject obj => string.Join(", ", obj.Select(pair =>
                $"{pair.Key}={System.Text.Json.JsonSerializer.Serialize(pair.Value, options)}")),
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } element =>
                string.Join(", ", element.EnumerateObject().Select(property =>
                    $"{property.Name}={System.Text.Json.JsonSerializer.Serialize(property.Value, options)}")),
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Undefined } => "",
            null => "",
            _ => $"arguments={System.Text.Json.JsonSerializer.Serialize(arguments, options)}",
        };
    }

    /// <summary>摘要 system 提示。对应 TS <c>SUMMARIZATION_SYSTEM_PROMPT</c>。</summary>
    public const string SummarizationSystemPrompt =
        "You are a context summarization assistant. Your task is to read a conversation between a user and an AI assistant, then produce a structured summary following the exact format specified.\n\nDo NOT continue the conversation. Do NOT respond to any questions in the conversation. ONLY output the structured summary.";
}
