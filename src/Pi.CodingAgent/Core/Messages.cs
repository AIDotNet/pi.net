using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Pi.Ai.Types;

namespace Pi.CodingAgent.Core;

// ============================================================================
// messages.ts — 4e-2b
//
// coding-agent 在 pi-ai 的 Message 联合之外追加 4 个自定义 role
// （bashExecution / custom / branchSummary / compactionSummary）。TS 用 declaration
// merging 把它们并进 pi-agent-core 的 CustomAgentMessages，AgentMessage 因此等于
// `Message | CustomAgentMessages[keyof CustomAgentMessages]`。
//
// C# 没有 declaration merging，且 Pi.Ai 不能反向引用 Pi.CodingAgent，所以端口分两步：
//   1) 4 个自定义消息直接派生自 Pi.Ai 的 ChatMessage（基类判别符就是 TS 的 `role`）；
//   2) 用运行时 type-info 修饰器把 4 个自定义 role 追加进 ChatMessage 的多态表
//      （见 AgentMessageJson），使 JSON 线路与 TS 的 role 判别完全一致。
//
// 消息内容联合 `string | (TextContent | ImageContent)[]` 由 Pi.Ai.Types.MessageContent 承载
// （4e-2c-0 下沉到 Pi.Ai：SystemMessage.content / UserMessage.content / CustomMessage.content
// 与扩展 API 的 sendUserMessage 共用同一个类型）。
// ============================================================================

/// <summary>
/// <c>!</c> 命令触发的 bash 执行消息。对应 TS <c>BashExecutionMessage</c>（role <c>bashExecution</c>）。
/// </summary>
public sealed record BashExecutionMessage(
    string Command,
    string Output,
    int? ExitCode,
    bool Cancelled,
    bool Truncated,
    long Timestamp) : ChatMessage
{
    /// <summary>输出被截断时完整输出的落盘路径。对应 TS <c>fullOutputPath?</c>。</summary>
    [JsonPropertyName("fullOutputPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FullOutputPath { get; init; }

    /// <summary>true 时该消息不进 LLM 上下文（<c>!!</c> 前缀）。对应 TS <c>excludeFromContext?</c>。</summary>
    [JsonPropertyName("excludeFromContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ExcludeFromContext { get; init; }
}

/// <summary>
/// 扩展经 <c>sendMessage()</c> 注入的自定义消息。对应 TS <c>CustomMessage&lt;T = unknown&gt;</c>
/// （role <c>custom</c>）。
/// </summary>
/// <remarks>
/// TS 的 <c>details?: T</c> 泛型只是编译期便利：运行时它就是任意 JSON。端口按仓库既有约定
/// （见 C88）不带泛型，用 <c>object?</c> 承载。
/// </remarks>
public sealed record CustomMessage(
    string CustomType,
    MessageContent Content,
    bool Display,
    long Timestamp) : ChatMessage
{
    /// <summary>扩展私有负载。对应 TS <c>details?</c>。</summary>
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Details { get; init; }
}

/// <summary>
/// 分支回溯摘要。对应 TS <c>BranchSummaryMessage</c>（role <c>branchSummary</c>）。
/// </summary>
/// <remarks>
/// <see cref="FromId"/> 是可空的**必填**字段：TS 写 <c>null</c> 而非省略，故不加
/// <c>WhenWritingNull</c> 忽略。
/// </remarks>
public sealed record BranchSummaryMessage(
    string Summary,
    string? FromId,
    long Timestamp) : ChatMessage;

/// <summary>
/// 上下文压缩摘要。对应 TS <c>CompactionSummaryMessage</c>（role <c>compactionSummary</c>）。
/// </summary>
public sealed record CompactionSummaryMessage(
    string Summary,
    long TokensBefore,
    long Timestamp) : ChatMessage;

/// <summary>
/// v2 会话文件里的历史 role（<c>hookMessage</c>）。**仅用于解析旧会话文件**：v2→v3 迁移会把它
/// 改写成 <see cref="CustomMessage"/>（见 <see cref="SessionEntries.MigrateSessionEntries"/>）。
/// </summary>
/// <remarks>
/// TS 侧这个 role 不属于 <c>AgentMessage</c> 联合（迁移之后才变成 <c>custom</c>），所以它**不**注册进
/// <see cref="AgentMessageJson.Options"/>；只有会话线路（<see cref="SessionEntries.JsonOptions"/>）认识它。
/// <para>
/// 字段全部可空：TS 的历史数据不做校验，迁移只改名、不补字段。真实 v2 文件里的 hookMessage 由
/// 扩展 <c>sendMessage</c> 产出，字段齐全；缺字段的只可能是手工构造的数据，此时迁移后端口会写出
/// 默认值（<c>""</c>/<c>false</c>/<c>0</c>），而 TS 仍省略这些字段——已记为文档化差异。
/// </para>
/// </remarks>
public sealed record HookMessage : ChatMessage
{
    [JsonPropertyName("customType")]
    public string? CustomType { get; init; }

    [JsonPropertyName("content")]
    public MessageContent? Content { get; init; }

    [JsonPropertyName("display")]
    public bool? Display { get; init; }

    [JsonPropertyName("details")]
    public object? Details { get; init; }

    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }

    /// <summary>迁移为当前形态的 <see cref="CustomMessage"/>（v2→v3 的 role 改名）。</summary>
    internal CustomMessage ToCustomMessage() => new(
        CustomType ?? "",
        Content ?? MessageContent.BlocksOf([]),
        Display ?? false,
        Timestamp ?? 0)
    {
        Details = Details,
    };
}

/// <summary>
/// AgentMessage 的 JSON 线路配置。在 <see cref="ChatMessage"/> 的 4 个基础 role 之上，
/// 追加 coding-agent 的 4 个自定义 role（TS 的 declaration merging 在 C# 侧的等价物）。
/// </summary>
public static class AgentMessageJson
{
    /// <summary>camelCase 属性名 + role 多态（8 个 role）。</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo => AddCodingAgentRoles(typeInfo));
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = resolver,
        };
    }

    /// <summary>
    /// 把 coding-agent 的 4 个自定义 role 追加进 <see cref="ChatMessage"/> 的多态表。
    /// </summary>
    /// <remarks>
    /// 独立成方法是为了让会话线路（<see cref="SessionEntries.JsonOptions"/>）复用同一张表，再在其上
    /// 叠加只属于会话文件的遗留 role（<c>hookMessage</c>）。
    /// </remarks>
    internal static void AddCodingAgentRoles(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(ChatMessage))
        {
            return;
        }

        var polymorphism = typeInfo.PolymorphismOptions ??= new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "role",
        };

        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(BashExecutionMessage), "bashExecution"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(CustomMessage), "custom"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(BranchSummaryMessage), "branchSummary"));
        polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(CompactionSummaryMessage), "compactionSummary"));
    }
}

/// <summary>
/// <c>core/messages.ts</c> 的常量、工厂与转换器。
/// </summary>
public static class AgentMessages
{
    /// <summary>压缩摘要的前缀（含尾部换行）。对应 TS <c>COMPACTION_SUMMARY_PREFIX</c>。</summary>
    public const string CompactionSummaryPrefix =
        "The conversation history before this point was compacted into the following summary:\n\n<summary>\n";

    /// <summary>压缩摘要的后缀（含前导换行）。对应 TS <c>COMPACTION_SUMMARY_SUFFIX</c>。</summary>
    public const string CompactionSummarySuffix = "\n</summary>";

    /// <summary>分支摘要的前缀（含尾部换行）。对应 TS <c>BRANCH_SUMMARY_PREFIX</c>。</summary>
    public const string BranchSummaryPrefix =
        "The following is a summary of a branch that this conversation came back from:\n\n<summary>\n";

    /// <summary>分支摘要的后缀（无换行）。对应 TS <c>BRANCH_SUMMARY_SUFFIX</c>。</summary>
    public const string BranchSummarySuffix = "</summary>";

    /// <summary>
    /// 把 bash 执行消息渲染为 LLM 上下文里的用户文本。对应 TS <c>bashExecutionToText</c>。
    /// </summary>
    /// <remarks>
    /// TS 用真值判断 <c>msg.output</c> / <c>msg.fullOutputPath</c>，空串等同缺失；
    /// <c>exitCode</c> 的判空同时排除了 <c>null</c> / <c>undefined</c> / <c>0</c>。
    /// </remarks>
    public static string BashExecutionToText(BashExecutionMessage msg)
    {
        var text = $"Ran `{msg.Command}`\n";
        text += string.IsNullOrEmpty(msg.Output)
            ? "(no output)"
            : $"```\n{msg.Output}\n```";

        if (msg.Cancelled)
        {
            text += "\n\n(command cancelled)";
        }
        else if (msg.ExitCode is not null and not 0)
        {
            text += $"\n\nCommand exited with code {msg.ExitCode}";
        }

        if (msg.Truncated && !string.IsNullOrEmpty(msg.FullOutputPath))
        {
            text += $"\n\n[Output truncated. Full output: {msg.FullOutputPath}]";
        }

        return text;
    }

    /// <summary>
    /// 构造分支摘要消息。对应 TS <c>createBranchSummaryMessage(summary, fromId, timestamp)</c>：
    /// <paramref name="timestamp"/> 是 ISO 字符串，被 <c>new Date(...).getTime()</c> 换算为毫秒。
    /// </summary>
    public static BranchSummaryMessage CreateBranchSummaryMessage(string summary, string? fromId, string timestamp)
        => new(summary, fromId, ParseTimestamp(timestamp));

    /// <summary>构造压缩摘要消息。对应 TS <c>createCompactionSummaryMessage</c>。</summary>
    public static CompactionSummaryMessage CreateCompactionSummaryMessage(
        string summary, long tokensBefore, string timestamp)
        => new(summary, tokensBefore, ParseTimestamp(timestamp));

    /// <summary>构造自定义消息。对应 TS <c>createCustomMessage</c>。</summary>
    public static CustomMessage CreateCustomMessage(
        string customType,
        MessageContent content,
        bool display,
        object? details,
        string timestamp)
        => new(customType, content, display, ParseTimestamp(timestamp)) { Details = details };

    /// <summary>
    /// 把 AgentMessage 列表转换为 LLM 可用的 Message 列表。对应 TS <c>convertToLlm</c>。
    /// </summary>
    /// <remarks>
    /// 4 个自定义 role 折叠成 user 消息；<c>excludeFromContext</c> 的 bash 消息被丢弃；
    /// 4 个基础 role 原样透传。
    /// </remarks>
    public static IReadOnlyList<ChatMessage> ConvertToLlm(IReadOnlyList<ChatMessage> messages)
    {
        var converted = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            var result = ConvertOne(message);
            if (result is not null)
            {
                converted.Add(result);
            }
        }

        return converted;
    }

    private static ChatMessage? ConvertOne(ChatMessage message) => message switch
    {
        // 排除上下文的消息（!! 前缀）不进入 LLM 上下文。
        BashExecutionMessage bash when bash.ExcludeFromContext == true => null,
        BashExecutionMessage bash => new UserMessage(
            [new TextContent(BashExecutionToText(bash))], bash.Timestamp),

        CustomMessage custom => new UserMessage(custom.Content.ToBlocks(), custom.Timestamp),

        BranchSummaryMessage branch => new UserMessage(
            [new TextContent(BranchSummaryPrefix + branch.Summary + BranchSummarySuffix)], branch.Timestamp),

        CompactionSummaryMessage compaction => new UserMessage(
            [new TextContent(CompactionSummaryPrefix + compaction.Summary + CompactionSummarySuffix)],
            compaction.Timestamp),

        // 基础 role 原样透传（TS 的 `return m`）。
        SystemMessage or UserMessage or AssistantMessage or ToolResultMessage => message,

        // TS 的 default 分支是 never 穷尽检查，运行时返回 undefined 并被 filter 掉。
        _ => null,
    };

    /// <summary>
    /// 等价于 JS 的 <c>new Date(value).getTime()</c>。
    /// </summary>
    /// <remarks>
    /// 与 JS 的已知差异：JS 对无法解析的输入返回 <c>NaN</c>，端口抛
    /// <see cref="FormatException"/>。真实调用点的时间戳全部来自 <c>Date.toISOString()</c>，
    /// 不会走到该分支。日期型（无时间部分、无偏移）输入按 JS 语义解释为 UTC。
    /// </remarks>
    internal static long ParseTimestamp(string timestamp)
    {
        // JS 把 "YYYY-MM-DD" 这类纯日期按 UTC 解释；.NET 默认按本地时区。
        var isDateOnly = timestamp.Length == 10 && timestamp[4] == '-' && timestamp[7] == '-';
        if (isDateOnly
            && DateTimeOffset.TryParseExact(
                timestamp, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dateOnly))
        {
            return dateOnly.ToUnixTimeMilliseconds();
        }

        if (DateTimeOffset.TryParse(
                timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.ToUnixTimeMilliseconds();
        }

        throw new FormatException($"Cannot parse '{timestamp}' as a JavaScript date.");
    }
}
