using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pi.Ai.Types;

/// <summary>
/// 消息内容联合：纯文本，或内容块数组。对应 TS <c>string | (TextContent | ImageContent)[]</c>。
/// </summary>
/// <remarks>
/// TS 侧这是匿名结构类型，出现在 <c>SystemMessage.content</c>、<c>UserMessage.content</c>、
/// coding-agent 的 <c>CustomMessage.content</c>，以及扩展 API 的 <c>sendUserMessage</c>。
/// 端口用**同一个**具名 C# 类型承载，避免三处各自造轮子。
/// <para>
/// 两个分支的 JSON 形状不同（字符串 vs 数组）且没有判别字段，故由
/// <see cref="MessageContentJsonConverter"/> 按 JSON token 类型分派。
/// </para>
/// <para>
/// <c>HandleNull</c> 为 true 且 <c>Write</c> 把 null 写成 <c>""</c>：TS 的 <c>SystemMessage.content</c>
/// 是必填字段，凡构造点都给 <c>""</c>（<c>createInitialSystemMessage</c> / <c>getCurrentSystemMessage</c> /
/// <c>declareToolChanges</c>），端口因此也不能漏写该字段。读取方向同时容忍缺失与 <c>null</c>
/// （TS 的 <c>transformMessages</c> 会把 <c>content == null</c> 归一化为 <c>[]</c>，旧会话文件里有这种数据）。
/// </para>
/// </remarks>
[JsonConverter(typeof(MessageContentJsonConverter))]
public abstract record MessageContent
{
    /// <summary>纯文本分支。TS 的 <c>string</c>。</summary>
    public sealed record Text(string Value) : MessageContent;

    /// <summary>内容块数组分支。TS 的 <c>(TextContent | ImageContent)[]</c>（端口放宽为任意块）。</summary>
    public sealed record Blocks(IReadOnlyList<ContentBlock> Value) : MessageContent;

    /// <summary>文本分支的取值；数组分支为 null。</summary>
    [JsonIgnore]
    public string? TextValue => this is Text text ? text.Value : null;

    /// <summary>
    /// 块视图：文本分支折叠为单个文本块（等价于 TS provider 里的
    /// <c>typeof content === "string"</c> 分支），数组分支原样返回。
    /// </summary>
    public IReadOnlyList<ContentBlock> ToBlocks() => this switch
    {
        Text text => [new TextContent(text.Value)],
        Blocks blocks => blocks.Value,
        _ => throw new InvalidOperationException($"Unsupported MessageContent variant {GetType().Name}."),
    };

    /// <summary>与 <see cref="ToBlocks()"/> 相同，另报告是否来自文本分支（provider 需要区分时用）。</summary>
    public IReadOnlyList<ContentBlock> ToBlocks(out bool wasText)
    {
        wasText = this is Text;
        return ToBlocks();
    }

    public static MessageContent From(string text) => new Text(text);

    public static MessageContent From(IReadOnlyList<ContentBlock> blocks) => new Blocks(blocks);

    /// <summary>块数组分支的便捷构造。</summary>
    public static MessageContent BlocksOf(IReadOnlyList<ContentBlock> blocks) => new Blocks(blocks);

    /// <summary>
    /// 字符串分支的隐式转换。TS 的联合入参里 <c>string</c> 是常态
    /// （<c>SystemMessage.content</c> 的构造点全部给字符串），故保留隐式。
    /// </summary>
    /// <remarks>
    /// 数组分支**故意不做隐式转换**：<c>UserMessage</c> 有收 <c>IReadOnlyList&lt;ContentBlock&gt;</c>
    /// 的便捷构造，若同时存在数组→<see cref="MessageContent"/> 的隐式转换，重载决议会因
    /// 「两个转换目标互不可转」而二义（见 C# 规范的 better-conversion-target 规则）。
    /// 数组形态请显式写 <see cref="BlocksOf"/> / <c>new Blocks(...)</c>。
    /// </remarks>
    public static implicit operator MessageContent(string text) => new Text(text);
}

/// <summary>
/// <see cref="MessageContent"/> 的线路转换器。两个分支的 JSON 形状不同（字符串 vs 数组）
/// 且没有判别字段，只能按 JSON token 类型分派。
/// </summary>
internal sealed class MessageContentJsonConverter : JsonConverter<MessageContent>
{
    /// <summary>让 null 也走本转换器：<c>Write</c> 会把它写成 <c>""</c>（见类型注释）。</summary>
    public override bool HandleNull => true;

    public override MessageContent Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => new MessageContent.Text(reader.GetString()!),
            JsonTokenType.StartArray => new MessageContent.Blocks(
                JsonSerializer.Deserialize<List<ContentBlock>>(ref reader, options) ?? []),
            // TS 的 transformMessages 把 content == null 归一化为 []（旧会话文件 / 手写历史）。
            JsonTokenType.Null => new MessageContent.Blocks([]),
            _ => throw new JsonException(
                $"MessageContent expects a string or an array, got {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, MessageContent value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                // 见类型注释：SystemMessage.content 在 TS 侧恒存在，缺失时写空串。
                writer.WriteStringValue("");
                break;
            case MessageContent.Text text:
                writer.WriteStringValue(text.Value);
                break;
            case MessageContent.Blocks blocks:
                JsonSerializer.Serialize(writer, blocks.Value, options);
                break;
            default:
                throw new JsonException($"Unsupported MessageContent variant {value.GetType().Name}.");
        }
    }
}
