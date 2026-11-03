using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.Ai.Utils.Schema;

namespace Pi.Ai.Utils;

/// <summary>
/// 工具调用参数校验。对应 TS <c>utils/validation.ts</c>：每个工具 schema 编译一次 pi-schema 校验器
/// （<c>compile(schema, { formats, coerce: true, unresolvedRefs: "reject" })</c>），先按 schema 强制转换
/// 参数（模型常把数字写成字符串、把单值当成单元素数组、把省略的可选属性写成 <c>null</c>），再校验；
/// 失败时按 <c>路径: 文案</c> 逐条列出并附上收到的参数。
/// </summary>
public static class Validation
{
    /// <summary>
    /// 每个工具 schema 一份编译结果。上游用 <c>WeakMap</c> 挂在 schema 对象上；这里挂在
    /// <see cref="ToolSchema"/> 实例上，工具被回收时条目随之消失。
    /// </summary>
    private static readonly ConditionalWeakTable<ToolSchema, ToolSchemaValidator> Validators = new();

    /// <summary>
    /// 按名字找工具并校验参数。对应 TS <c>validateToolCall</c>：找不到工具或校验失败抛错。
    /// </summary>
    public static JsonNode? ValidateToolCall(IReadOnlyList<ToolDefinition> tools, ToolCallContent toolCall)
    {
        var tool = tools.FirstOrDefault(candidate => candidate.Name == toolCall.Name)
            ?? throw new InvalidOperationException($"Tool \"{toolCall.Name}\" not found");
        return ValidateToolArguments(tool, toolCall);
    }

    /// <summary>
    /// 校验并（必要时）强制转换工具调用参数。对应 TS <c>validateToolArguments</c>。
    /// 校验失败抛 <see cref="InvalidOperationException"/>，消息含逐条错误与收到的参数。
    /// </summary>
    public static JsonNode? ValidateToolArguments(ToolDefinition tool, ToolCallContent toolCall)
    {
        // 参数文本本身不是合法 JSON 时，直接把原因与模型原文回给模型，而不是让工具带着
        // 残缺字段执行（上游 #10897）。
        if (!string.IsNullOrEmpty(toolCall.ArgumentsError))
        {
            throw new InvalidOperationException(
                $"Invalid arguments for tool \"{toolCall.Name}\": {toolCall.ArgumentsError}");
        }

        // 参数先深拷贝一份，工具改自己的参数时不会改到 transcript 里的那次调用。
        var arguments = Clone(ToNode(toolCall.Arguments));
        var result = ValidatorFor(tool.Parameters).Parse(arguments);
        if (result.Ok) return result.Value;

        var errors = string.Join("\n", result.Errors.Select(error => $"  - {FormatPath(error.Path)}: {error.Message}"));
        var errorText = errors.Length > 0 ? errors : "  - root: invalid";

        throw new InvalidOperationException(
            $"Validation failed for tool \"{toolCall.Name}\":\n{errorText}\n\nReceived arguments:\n" +
            $"{SchemaJson.StringifyIndented(ToNode(toolCall.Arguments))}");
    }

    /// <summary>编译（或取缓存的）<paramref name="schema"/> 校验器。</summary>
    public static ToolSchemaValidator ValidatorFor(ToolSchema schema) =>
        Validators.GetValue(schema, static key => ToolSchemaValidator.Compile(ToSchemaObject(key)));

    /// <summary>把 <see cref="ToolSchema"/> 的字典形状转成 <see cref="JsonObject"/>。</summary>
    public static JsonObject ToSchemaObject(ToolSchema schema)
        => JsonSerializer.SerializeToNode(schema.JsonSchema) as JsonObject ?? new JsonObject();

    /// <summary>
    /// 错误路径格式化：JSON Pointer 转成模型看到的点号路径（<c>/edits/0/oldText</c> → <c>edits.0.oldText</c>）。
    /// 对应 TS <c>formatValidationPath</c>。
    /// </summary>
    private static string FormatPath(string pointer)
    {
        if (pointer.Length == 0) return "root";
        return string.Join(".", pointer[1..].Split('/')
            .Select(segment => segment.Replace("~1", "/").Replace("~0", "~")));
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        _ => JsonSerializer.SerializeToNode(value),
    };

    private static JsonNode? Clone(JsonNode? node) => node?.DeepClone();
}
