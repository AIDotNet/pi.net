using System.Text.Json.Serialization;

namespace Pi.Ai.Types;

/// <summary>
/// 文本内容块，对应 TS <c>TextContent</c>；<c>TextSignature</c> 持 provider 的
/// 文本回执签名（openai-responses 的 TextSignatureV1 JSON）用于同模型重放。
/// </summary>
public sealed record TextContent(string Text) : ContentBlock
{
    [JsonPropertyName("textSignature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextSignature { get; set; }
}

/// <summary>图片内容块（base64 数据），对应 TS <c>ImageContent</c>。</summary>
public sealed record ImageContent(string Data, string? MimeType = null) : ContentBlock;

/// <summary>思维链内容块，对应 TS <c>ThinkingContent</c>；<c>Signature</c> 为 provider 签名，
/// <c>Redacted</c> 标记加密不可回放的思维块（anthropic redacted thinking）。</summary>
public sealed record ThinkingContent(string Thinking, string? Signature = null) : ContentBlock
{
    /// <summary>加密思维块：内容不透明，仅同模型可回放。对应 TS <c>redacted</c>。</summary>
    [JsonPropertyName("redacted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Redacted { get; set; }
}

/// <summary>助手发起的工具调用块，对应 TS <c>ToolCall</c>。</summary>
public sealed record ToolCallContent(string Id, string Name, object? Arguments) : ContentBlock
{
    /// <summary>Google 系模型的 thought signature（跨模型回放时须剥离）。对应 TS <c>thoughtSignature</c>。</summary>
    [JsonPropertyName("thoughtSignature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThoughtSignature { get; set; }

    /// <summary>工具命名空间（openai-responses custom tool）。对应 TS <c>namespace</c>。</summary>
    [JsonPropertyName("namespace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Namespace { get; set; }

    /// <summary>
    /// 模型给出的完整参数文本不是合法 JSON 对象时置位，此时 <see cref="Arguments"/> 为空对象。
    /// 内容含失败原因与模型实际发送的原文，工具参数校验会用它向模型报错。对应 TS <c>argumentsError</c>（上游 #10897）。
    /// </summary>
    [JsonPropertyName("argumentsError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArgumentsError { get; set; }
}

/// <summary>
/// 内容块判别基类。对应 TS <c>TextContent | ImageContent | ThinkingContent | ToolCall</c> 联合类型，
/// 以 <c>type</c> 字段作为 JSON 序列化的判别符（wire 兼容）。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(ThinkingContent), "thinking")]
[JsonDerivedType(typeof(ToolCallContent), "toolCall")]
public abstract record ContentBlock
{
    private protected ContentBlock() { }
}

/// <summary>助手回合停止原因。对应 TS <c>StopReason</c> 字符串联合。</summary>
/// <remarks>
/// TS 的线名是小写驼峰字面量；<see cref="JsonStringEnumMemberNameAttribute"/> 逐个钉死，
/// 以免依赖命名策略（<c>XHigh</c> 这类名字在任何策略下都得不到 TS 的 <c>"xhigh"</c>）。
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<StopReason>))]
public enum StopReason
{
    /// <summary>流处理中（尚未到达停止原因）。对应 TS <c>pending</c>。</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>自然结束。</summary>
    [JsonStringEnumMemberName("stop")]
    Stop,

    /// <summary>输出被 token 上限截断。</summary>
    [JsonStringEnumMemberName("length")]
    Length,

    /// <summary>请求了工具调用。</summary>
    [JsonStringEnumMemberName("toolUse")]
    ToolUse,

    /// <summary>请求/运行时失败（错误详情见 <c>ErrorMessage</c>）。</summary>
    [JsonStringEnumMemberName("error")]
    Error,

    /// <summary>被用户中止。</summary>
    [JsonStringEnumMemberName("aborted")]
    Aborted,

    /// <summary>请求被延后，<c>deferred</c> 句柄可续取最终结果。对应 TS <c>deferred</c>。</summary>
    [JsonStringEnumMemberName("deferred")]
    Deferred,
}

/// <summary>思考/推理档位。对应 TS <c>ThinkingLevel</c>；xhigh/max 仅部分模型家族支持。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThinkingLevel>))]
public enum ThinkingLevel
{
    [JsonStringEnumMemberName("off")]
    Off,

    [JsonStringEnumMemberName("minimal")]
    Minimal,

    [JsonStringEnumMemberName("low")]
    Low,

    [JsonStringEnumMemberName("medium")]
    Medium,

    [JsonStringEnumMemberName("high")]
    High,

    [JsonStringEnumMemberName("xhigh")]
    XHigh,

    [JsonStringEnumMemberName("max")]
    Max,
}

/// <summary>
/// 用量成本明细（美元）。对应 TS <c>Usage["cost"]</c>：provider 定价后逐桶写入，
/// <see cref="Total"/> 恒等于四桶之和。TS 侧是可变的 <c>usage.cost</c> 对象，端口用
/// 不可变记录 + <c>with</c> 表达（provider 重建 <see cref="Usage"/>）。
/// </summary>
public sealed record UsageCost(
    [property: JsonPropertyName("input")] double Input = 0,
    [property: JsonPropertyName("output")] double Output = 0,
    [property: JsonPropertyName("cacheRead")] double CacheRead = 0,
    [property: JsonPropertyName("cacheWrite")] double CacheWrite = 0,
    [property: JsonPropertyName("total")] double Total = 0)
{
    /// <summary>
    /// <see cref="Total"/> 是否为 provider 上报的实际计费，而非按目录价算出来的。
    /// OpenRouter 在流式 usage 里附带实际计费（BYOK 时含上游推理成本），命中即覆盖总价并置真；
    /// 目录价重算（<see cref="Pi.Ai.Utils.ModelOperations.CalculateCost"/>）会把它重置为 false。
    /// 对应 TS <c>cost.totalFromProvider?</c>（缺省不写）。
    /// </summary>
    [JsonPropertyName("totalFromProvider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TotalFromProvider { get; init; }

    /// <summary>全零成本：provider 初始化用量时的起点（TS 的 <c>{ input: 0, …, total: 0 }</c>）。</summary>
    public static UsageCost Zero { get; } = new();

    /// <summary>按倍率缩放四桶与总价（服务档位加价）。对应 TS 侧对 <c>cost.total</c> 的乘法。</summary>
    public UsageCost Scale(double multiplier) => this with
    {
        Input = Input * multiplier,
        Output = Output * multiplier,
        CacheRead = CacheRead * multiplier,
        CacheWrite = CacheWrite * multiplier,
        Total = Total * multiplier,
    };

    /// <summary>按四桶重算 <see cref="Total"/>。对应 TS <c>calculateCost</c> 的末行。</summary>
    public UsageCost WithRecomputedTotal() => this with { Total = Input + Output + CacheRead + CacheWrite };
}

/// <summary>
/// 用量统计。对应 TS <c>Usage</c>。
/// </summary>
/// <remarks>
/// <see cref="TotalTokens"/> 是**存储字段**而非计算属性：TS 各 provider 定义不一致
/// （anthropic/google 用全桶之和，bedrock 用 input+output，mistral 用 wire 的 total_tokens，
/// openai 用全桶之和），故端口不再用 <c>Input + Output</c> 推断。需要「无上报则回退」的调用点
/// 应显式写 <c>usage.TotalTokens != 0 ? usage.TotalTokens : usage.Input + usage.Output +
/// usage.CacheRead + usage.CacheWrite</c>（对应 TS <c>utils/estimate.ts</c> 的 <c>||</c> 回退）。
/// </remarks>
public sealed record Usage(long Input, long Output, long CacheRead = 0, long CacheWrite = 0)
{
    /// <summary>1h 保留期的缓存写子集；仅 Anthropic 上报。对应 TS <c>cacheWrite1h?</c>。</summary>
    [JsonPropertyName("cacheWrite1h")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CacheWrite1h { get; init; }

    /// <summary>
    /// 推理/思维 token，<see cref="Output"/> 的子集；provider 未上报时为 null。
    /// 对应 TS <c>reasoning?</c>。
    /// </summary>
    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Reasoning { get; init; }

    /// <summary>总 token（provider 上报值）。对应 TS <c>totalTokens</c>。</summary>
    [JsonPropertyName("totalTokens")]
    public long TotalTokens { get; init; }

    /// <summary>成本明细。对应 TS <c>cost</c>。</summary>
    [JsonPropertyName("cost")]
    public UsageCost Cost { get; init; } = UsageCost.Zero;
}

/// <summary>
/// 聊天消息判别基类。对应 TS <c>Message = SystemMessage | UserMessage | AssistantMessage |
/// ToolResultMessage</c>，以 <c>role</c> 字段判别。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "role")]
[JsonDerivedType(typeof(SystemMessage), "system")]
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(AssistantMessage), "assistant")]
[JsonDerivedType(typeof(ToolResultMessage), "toolResult")]
public abstract record ChatMessage
{
    // protected（而非 private protected）：coding-agent 的 4 个自定义 role
    // （bashExecution / custom / branchSummary / compactionSummary）在 Pi.CodingAgent 中派生，
    // 对应 TS 侧 pi-agent-core 的 CustomAgentMessages declaration merging。
    protected ChatMessage() { }
}

/// <summary>
/// 系统消息。对应 TS <c>SystemMessage</c>：transcript 的工具/prompt 增量载体。
/// 首条 system 消息是系统提示；后续的 <c>Content</c> 追加提示、<c>Sections</c>
/// 按名修补（value 为 null 表示删除该段）、<c>ToolsAdded</c>/<c>ToolsRemoved</c>
/// 增量变更工具集。按序回放全部 system 消息得到当前 prompt 与工具。
/// </summary>
/// <remarks>
/// <see cref="Content"/> 是 TS 的 <c>string | TextContent[]</c> 联合（见 <see cref="MessageContent"/>）。
/// TS 侧该字段**必填**，构造点一律给 <c>""</c>；端口的 <c>null</c> 由
/// <see cref="MessageContentJsonConverter"/> 在序列化时写成 <c>""</c>，故不会漏写字段。
/// </remarks>
public sealed record SystemMessage(
    [property: JsonPropertyName("content")] MessageContent? Content = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string?>? Sections = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ToolDefinition>? ToolsAdded = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? ToolsRemoved = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Timestamp = null) : ChatMessage;

/// <summary>用户消息。对应 TS <c>UserMessage</c>。</summary>
/// <remarks>
/// <see cref="Content"/> 是 TS 的 <c>string | (TextContent | ImageContent)[]</c> 联合
/// （见 <see cref="MessageContent"/>）：TS 的 <c>Agent.Prompt</c> 走数组分支，
/// 而扩展 / 旧会话文件可能写裸字符串。端口两者都能往返。
/// </remarks>
public sealed record UserMessage : ChatMessage
{
    [JsonConstructor]
    public UserMessage(MessageContent content, long timestamp)
    {
        Content = content;
        Timestamp = timestamp;
    }

    /// <summary>块列表的便捷构造，等价于 <c>MessageContent.BlocksOf(content)</c>。</summary>
    public UserMessage(IReadOnlyList<ContentBlock> content, long timestamp)
        : this(MessageContent.BlocksOf(content), timestamp)
    {
    }

    [JsonPropertyName("content")]
    public MessageContent Content { get; init; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }
}

/// <summary>助手消息。对应 TS <c>AssistantMessage</c>。</summary>
/// <remarks>
/// 可空字段一律「null 即省略」，对齐 TS 里 <c>undefined</c> 不参与 JSON 序列化的行为。
/// </remarks>
public sealed record AssistantMessage(
    IReadOnlyList<ContentBlock> Content,
    StopReason StopReason = Types.StopReason.Stop,
    [property: JsonPropertyName("errorMessage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorMessage = null,
    [property: JsonPropertyName("usage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Usage? UsageStats = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Api = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ThinkingLevel? ThinkingLevel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Timestamp = null) : ChatMessage
{
    /// <summary>provider 原始停止原因（如 "incomplete.max_output_tokens"）。对应 TS <c>rawStopReason</c>。</summary>
    [JsonPropertyName("rawStopReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RawStopReason { get; set; }

    /// <summary>provider 报告的实际模型（与请求的 <c>model</c> 不同时）。对应 TS <c>responseModel</c>。</summary>
    [JsonPropertyName("responseModel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseModel { get; set; }

    /// <summary>响应 id（openai-responses 等）。对应 TS <c>responseId</c>。</summary>
    [JsonPropertyName("responseId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseId { get; set; }

    /// <summary>流处理附加的诊断条目（传输降级等）。对应 TS <c>diagnostics</c>。</summary>
    [JsonPropertyName("diagnostics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Utils.AssistantMessageDiagnostic>? Diagnostics { get; set; }

    /// <summary>模型是否自然说完（codex end_turn）。对应 TS <c>endTurn</c>。</summary>
    [JsonPropertyName("endTurn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EndTurn { get; set; }

    /// <summary>provider 侧实际使用的思考档位（pi-messages 回传）。对应 TS <c>providerThinkingLevel</c>。</summary>
    [JsonPropertyName("providerThinkingLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderThinkingLevel { get; set; }

    /// <summary>延后响应句柄（StopReason=Deferred 时携带）。对应 TS <c>deferred</c>。</summary>
    [JsonPropertyName("deferred")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeferredHandle? Deferred { get; set; }

    /// <summary>从 <c>timestamp</c> 到响应结束的毫秒数（单调时钟）。对应 TS <c>durationMs</c>。</summary>
    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; set; }

    /// <summary>本条消息中的全部工具调用块（按出现顺序）。</summary>
    [JsonIgnore]
    public IEnumerable<ToolCallContent> ToolCalls =>
        Content.OfType<ToolCallContent>();
}

/// <summary>工具运行期间发起的嵌套工具调用（例如 codemode 脚本里的调用）。对应 TS <c>NestedToolCallRecord</c>。</summary>
public sealed record NestedToolCallRecord
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>超尺寸限制时省略，此时 <see cref="ArgumentsBytes"/> 给出字节数。</summary>
    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? Arguments { get; set; }

    /// <summary>arguments 的 JSON UTF-8 字节数（省略 arguments 时设置）。</summary>
    [JsonPropertyName("argumentsBytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ArgumentsBytes { get; set; }

    /// <summary><c>unfinished</c>：调用工具结束时嵌套调用仍在运行。对应 TS <c>"ok" | "error" | "unfinished"</c>。</summary>
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; set; }

    /// <summary>截断后的错误文本。</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>工具发起的嵌套调用的有界记录（不记录结果）。对应 TS <c>NestedToolCalls</c>。</summary>
public sealed record NestedToolCalls
{
    [JsonPropertyName("calls")]
    public required IReadOnlyList<NestedToolCallRecord> Calls { get; init; }

    /// <summary>false 表示调用被丢弃、arguments 被省略或有调用未完成。</summary>
    [JsonPropertyName("complete")]
    public required bool Complete { get; init; }
}

/// <summary>工具结果消息。对应 TS <c>ToolResultMessage</c>。</summary>
public sealed record ToolResultMessage(
    string ToolCallId,
    string ToolName,
    IReadOnlyList<ContentBlock> Content,
    bool IsError = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Details = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Timestamp = null) : ChatMessage
{
    /// <summary>
    /// 工具执行自身的用量（不计入主 LLM 上下文账）。对应 TS <c>usage?</c>。
    /// </summary>
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Usage? UsageStats { get; set; }

    /// <summary>工具执行耗时（毫秒，单调时钟）。对应 TS <c>durationMs</c>。</summary>
    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; set; }

    /// <summary>该工具运行期间发起的嵌套调用（保留在会话记录里；不发给模型）。对应 TS <c>nestedCalls?</c>。</summary>
    [JsonPropertyName("nestedCalls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NestedToolCalls? NestedCalls { get; set; }
}

/// <summary>静态构造辅助（对齐 TS 侧消息工厂的易用性）。</summary>
public static class Messages
{
    public static UserMessage User(params ContentBlock[] content)
        => new(content, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static UserMessage UserText(string text)
        => User(new TextContent(text));

    public static AssistantMessage AssistantText(string text, StopReason stopReason = Types.StopReason.Stop)
        => new([new TextContent(text)], stopReason,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public static ToolResultMessage ToolResult(
        string toolCallId, string toolName, string text, bool isError = false)
        => new(toolCallId, toolName, [new TextContent(text)], isError,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
