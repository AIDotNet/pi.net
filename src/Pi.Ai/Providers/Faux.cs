using System.Text;
using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// Faux 测试 provider。对应 TS <c>providers/faux.ts</c>（710 行）：以脚本化的响应序列
/// 回放"助手回合"，不访问网络。用于测试 agent 循环、coding-agent 会话与上层消费方。
/// </summary>
/// <remarks>
/// 两条入口与 TS 一致：
/// <list type="bullet">
///   <item><see cref="FauxCore"/>（= TS <c>createFauxCore</c>）：可变脚本 + 状态 + 延后句柄，
///   由 <c>CompatApi.RegisterFauxProvider()</c> 注册进 api-registry；</item>
///   <item><see cref="CreateProvider"/>（= TS <c>fauxProvider</c>）：经 <c>createProvider</c> 装配出
///   带认证语义的真实 <see cref="IProvider"/> 句柄，供 <c>Models.SetProvider</c> 使用。</item>
/// </list>
/// 另有端口专有的 <see cref="CreateStreamFn"/> 便捷入口（TS 无对应导出），供 <c>Pi.Agent</c> 的
/// <c>StreamFn</c> 边界直接使用。
/// </remarks>
public static class Faux
{
    /// <summary>默认 api id。对应 TS <c>DEFAULT_API</c>。</summary>
    public const string DefaultApi = "faux";

    /// <summary>默认 provider id。对应 TS <c>DEFAULT_PROVIDER</c>。</summary>
    public const string DefaultProvider = "faux";

    /// <summary>默认模型 id。对应 TS <c>DEFAULT_MODEL_ID</c>。</summary>
    public const string DefaultModelId = "faux-1";

    /// <summary>默认模型显示名。对应 TS <c>DEFAULT_MODEL_NAME</c>。</summary>
    public const string DefaultModelName = "Faux Model";

    /// <summary>默认 base url。对应 TS <c>DEFAULT_BASE_URL</c>。</summary>
    public const string DefaultBaseUrl = "http://localhost:0";

    /// <summary>默认最小 token 块。对应 TS <c>DEFAULT_MIN_TOKEN_SIZE</c>。</summary>
    internal const int DefaultMinTokenSize = 3;

    /// <summary>默认最大 token 块。对应 TS <c>DEFAULT_MAX_TOKEN_SIZE</c>。</summary>
    internal const int DefaultMaxTokenSize = 5;

    /// <summary>
    /// 全零用量：脚本条目与错误 / 延后消息的起点。
    /// 对应 TS <c>DEFAULT_USAGE</c>（<c>cost</c> 四桶与 <c>total</c> 全零）。
    /// </summary>
    public static Usage DefaultUsage { get; } = new(0, 0, 0, 0)
    {
        TotalTokens = 0,
        Cost = UsageCost.Zero,
    };

    /// <summary>默认 faux 模型描述（<see cref="StreamFn"/> 边界用；非 <see cref="ModelSpec"/>）。</summary>
    public static Model DefaultModel { get; } = new(DefaultModelId, DefaultModelName, DefaultApi, DefaultProvider);

    // ---------- 内容块工厂（对应 fauxText / fauxThinking / fauxToolCall） ----------

    public static TextContent Text(string text) => new(text);

    public static ThinkingContent Thinking(string thinking) => new(thinking);

    /// <summary>工具调用块。缺省 id 走 <see cref="RandomId"/>（对应 TS <c>randomId("tool")</c>）。</summary>
    public static ToolCallContent ToolCall(string name, object? arguments, string? id = null)
        => new(id ?? RandomId("tool"), name, arguments);

    /// <summary>
    /// 助手脚本内容归一化。对应 TS <c>normalizeFauxAssistantContent</c>：
    /// 裸字符串 → 单个文本块；单块 → 单元素列表；列表原样返回。
    /// </summary>
    public static IReadOnlyList<ContentBlock> NormalizeFauxAssistantContent(string content) => [new TextContent(content)];

    public static IReadOnlyList<ContentBlock> NormalizeFauxAssistantContent(ContentBlock content) => [content];

    public static IReadOnlyList<ContentBlock> NormalizeFauxAssistantContent(IReadOnlyList<ContentBlock> content)
        => content;

    /// <summary>
    /// 构造一条脚本化助手消息。对应 TS <c>fauxAssistantMessage</c>。
    /// </summary>
    /// <remarks>
    /// <c>usage</c> 一律写 <see cref="DefaultUsage"/>——与 TS 相同，它会在回放时被
    /// <see cref="WithUsageEstimate"/> 无条件覆盖，所以这里给什么都不影响最终结果。
    /// </remarks>
    public static AssistantMessage ScriptedMessage(
        string content,
        StopReason stopReason = StopReason.Stop,
        DeferredHandle? deferred = null,
        string? errorMessage = null,
        string? responseId = null,
        long? timestamp = null)
        => ScriptedMessage(NormalizeFauxAssistantContent(content), stopReason, deferred, errorMessage, responseId, timestamp);

    public static AssistantMessage ScriptedMessage(
        ContentBlock content,
        StopReason stopReason = StopReason.Stop,
        DeferredHandle? deferred = null,
        string? errorMessage = null,
        string? responseId = null,
        long? timestamp = null)
        => ScriptedMessage([content], stopReason, deferred, errorMessage, responseId, timestamp);

    public static AssistantMessage ScriptedMessage(
        IReadOnlyList<ContentBlock> content,
        StopReason stopReason = StopReason.Stop,
        DeferredHandle? deferred = null,
        string? errorMessage = null,
        string? responseId = null,
        long? timestamp = null)
    {
        var message = new AssistantMessage(
            content,
            stopReason,
            ErrorMessage: errorMessage,
            UsageStats: DefaultUsage,
            Model: DefaultModelId,
            Api: DefaultApi,
            Provider: DefaultProvider,
            Timestamp: timestamp);
        message.Deferred = deferred;
        message.ResponseId = responseId;
        return message;
    }

    // ---------- 脚本条目类型 ----------

    /// <summary>
    /// 脚本条目：静态响应或响应工厂。对应 TS <c>FauxResponseStep = AssistantMessage | FauxResponseFactory</c>。
    /// </summary>
    /// <remarks>
    /// 端口用**继承**表达这个联合（而非 <c>OneOf</c> 之类的包装）：<see cref="Response"/> 直接派生自它，
    /// 于是既有的 <c>IEnumerable&lt;Response&gt;</c> 实参能靠 <c>IEnumerable&lt;out T&gt;</c> 的协变
    /// 直接喂给 <c>IEnumerable&lt;FauxResponseStep&gt;</c> 形参，无需在调用点做转换。
    /// </remarks>
    public abstract record FauxResponseStep
    {
        private protected FauxResponseStep() { }
    }

    /// <summary>动态响应工厂：按上下文 / 选项 / 状态 / 模型现场产出助手消息。对应 TS <c>FauxResponseFactory</c>。</summary>
    public delegate Task<AssistantMessage> FauxResponseFactory(
        TranscriptContext context,
        SimpleStreamOptions? options,
        FauxProviderState state,
        ModelSpec model);

    /// <summary>工厂型脚本条目。对应 TS 里直接放进脚本数组的函数。</summary>
    public sealed record FactoryStep(FauxResponseFactory Factory) : FauxResponseStep;

    /// <summary>脚本条目：一个待回放的助手回合（内容块 + 停止原因）。</summary>
    /// <remarks>
    /// TS 的脚本条目就是一条 <c>AssistantMessage</c>，其中 <c>usage</c> 会被
    /// <c>withUsageEstimate</c> **无条件覆盖**，故端口不提供 <c>Usage</c> 字段——最终用量一律由
    /// <see cref="WithUsageEstimate"/> 估算。
    /// </remarks>
    public sealed record Response(
        IReadOnlyList<ContentBlock> Content,
        StopReason StopReason = StopReason.Stop,
        string? ErrorMessage = null,
        DeferredHandle? Deferred = null,
        string? ResponseId = null,
        long? Timestamp = null) : FauxResponseStep
    {
        /// <summary>纯文本回复。</summary>
        public static Response Text(string text) => new([new TextContent(text)]);

        /// <summary>工具调用回复（停止原因 ToolUse）。</summary>
        public static Response ToolUse(params ToolCallContent[] calls)
            => new(calls, StopReason.ToolUse);

        /// <summary>延后回复（停止原因 Deferred，携带续取句柄）。</summary>
        public static Response DeferredResponse(DeferredHandle handle)
            => new([], StopReason.Deferred, Deferred: handle);
    }

    /// <summary>
    /// provider 运行时状态。对应 TS <c>FauxProviderState</c>：调用计数、续取计数、已取消句柄。
    /// </summary>
    public sealed class FauxProviderState
    {
        /// <summary>已处理的 <c>stream</c> 调用数（含脚本耗尽的那次）。对应 TS <c>callCount</c>。</summary>
        public int CallCount { get; internal set; }

        /// <summary>已处理的 <c>fetchDeferred</c> 调用数。对应 TS <c>deferredFetchCount</c>。</summary>
        public int DeferredFetchCount { get; internal set; }

        /// <summary>被取消的句柄快照（按取消顺序）。对应 TS <c>cancelledDeferred</c>。</summary>
        public List<DeferredHandle> CancelledDeferred { get; } = [];
    }

    // ---------- 注册选项（对应 RegisterFauxProviderOptions / FauxModelDefinition） ----------

    /// <summary>自定义模型定义。对应 TS <c>FauxModelDefinition</c>。</summary>
    public sealed record FauxModelDefinition
    {
        public required string Id { get; init; }

        public string? Name { get; init; }

        public bool? Reasoning { get; init; }

        /// <summary>输入模态（text/image）；缺省两者皆可。</summary>
        public IReadOnlyList<string>? Input { get; init; }

        public ModelInputLimits? InputLimits { get; init; }

        public ModelCostRates? Cost { get; init; }

        public long? ContextWindow { get; init; }

        public long? MaxTokens { get; init; }
    }

    /// <summary>延后响应选项。对应 TS <c>RegisterFauxProviderOptions.deferred</c>。</summary>
    public sealed record FauxDeferredOptions
    {
        /// <summary>脚本响应就绪前仍返回原句柄的续取次数。对应 TS <c>pendingFetches</c>。</summary>
        public int? PendingFetches { get; init; }

        /// <summary>写进句柄的建议轮询间隔（毫秒）。对应 TS <c>pollAfterMs</c>。</summary>
        public long? PollAfterMs { get; init; }
    }

    /// <summary>随机切块尺寸（token）。对应 TS <c>tokenSize</c>。</summary>
    public sealed record FauxTokenSize
    {
        public int? Min { get; init; }

        public int? Max { get; init; }
    }

    /// <summary>faux 注册选项。对应 TS <c>RegisterFauxProviderOptions</c>。</summary>
    public sealed record FauxProviderOptions
    {
        /// <summary>api id；缺省为随机 <c>faux-&lt;hex&gt;</c>（避免并发测试互相覆盖）。</summary>
        public string? Api { get; init; }

        /// <summary>provider id；缺省 <see cref="DefaultProvider"/>。</summary>
        public string? Provider { get; init; }

        /// <summary>自定义模型目录；缺省单模型 <c>faux-1</c>。</summary>
        public IReadOnlyList<FauxModelDefinition>? Models { get; init; }

        /// <summary>延后响应行为。</summary>
        public FauxDeferredOptions? Deferred { get; init; }

        /// <summary>token 速率（token/秒）；设置后流式增量按估算 token 数真实延迟。</summary>
        public double? TokensPerSecond { get; init; }

        /// <summary>随机切块尺寸范围。</summary>
        public FauxTokenSize? TokenSize { get; init; }
    }

    // ---------- 提示文本与用量估算（对应 TS faux.ts 的 messageToText / withUsageEstimate 区） ----------

    /// <summary>
    /// faux 自己的粗估：固定 4 字符/token。对应 TS faux.ts 的 <c>estimateTokens</c>。
    /// 注意它与 <c>Utils/Estimate</c> 的 3.5 字符/token 不同——faux 是测试 provider 的近似，
    /// 上游漂移里 estimate.ts 改成 3.5 时**没有**改动 faux 的 <c>/4</c>。
    /// </summary>
    private static long EstimateTokens(string text) => (long)Math.Ceiling(text.Length / 4.0);

    /// <summary>
    /// 随机 id。对应 TS <c>randomId(prefix)</c>：
    /// <c>`${prefix}:${Date.now()}:${Math.random().toString(36).slice(2)}`</c>。
    /// </summary>
    private static string RandomId(string prefix)
        => $"{prefix}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{RandomBase36Fraction()}";

    /// <summary>
    /// <c>Math.random().toString(36).slice(2)</c> 的等价物：把 <c>[0,1)</c> 的小数逐位转成 base36 数字。
    /// </summary>
    private static string RandomBase36Fraction()
    {
        var value = Random.Shared.NextDouble();
        var builder = new StringBuilder();
        for (var index = 0; index < 11 && value > 0; index++)
        {
            value *= 36;
            var digit = (int)Math.Floor(value);
            value -= digit;
            builder.Append(digit < 10 ? (char)('0' + digit) : (char)('a' + digit - 10));
        }
        return builder.Length > 0 ? builder.ToString() : "0";
    }

    /// <summary>user 内容 → 文本。对应 TS <c>contentToText</c>。</summary>
    private static string ContentToText(MessageContent content) => content switch
    {
        MessageContent.Text text => text.Value,
        MessageContent.Blocks blocks => ContentToText(blocks.Value),
        _ => throw new InvalidOperationException(
            $"faux contentToText does not support {content.GetType().Name}."),
    };

    private static string ContentToText(IReadOnlyList<ContentBlock> blocks)
        => string.Join("\n", blocks.Select(BlockToText));

    /// <summary>TS：<c>block.type === "text" ? block.text : `[image:${mimeType}:${data.length}]`</c>。</summary>
    private static string BlockToText(ContentBlock block) => block switch
    {
        TextContent text => text.Text ?? "",
        ImageContent image => $"[image:{image.MimeType}:{image.Data.Length}]",
        // TS 的联合类型只给 text/image；其它变体会在 `block.data.length` 上抛 TypeError。
        _ => throw new InvalidOperationException(
            $"faux contentToText only accepts text and image blocks, got {block.GetType().Name}."),
    };

    /// <summary>助手内容 → 文本（toolCall 取 <c>name:JSON(arguments)</c>）。对应 TS <c>assistantContentToText</c>。</summary>
    private static string AssistantContentToText(IReadOnlyList<ContentBlock> content)
        => string.Join("\n", content.Select(block => block switch
        {
            TextContent text => text.Text ?? "",
            ThinkingContent thinking => thinking.Thinking,
            ToolCallContent toolCall => $"{toolCall.Name}:{JsonStringify(toolCall.Arguments)}",
            // TS 的兜底分支读 block.name / block.arguments；联合类型里只剩 toolCall，
            // 其它类型两者皆为 undefined，故字面量就是 "undefined:undefined"。
            _ => "undefined:undefined",
        }));

    /// <summary>工具结果 → 文本：<c>toolName</c> 后接每个块的文本。对应 TS <c>toolResultToText</c>。</summary>
    private static string ToolResultToText(ToolResultMessage message)
        => string.Join("\n",
            new[] { message.ToolName }.Concat(message.Content.Select(block => ContentToText([block]))));

    /// <summary>消息 → 提示文本。对应 TS <c>messageToText</c>。</summary>
    private static string MessageToText(ChatMessage message) => message switch
    {
        SystemMessage system => SystemMessageToText(system),
        UserMessage user => ContentToText(user.Content),
        AssistantMessage assistant => AssistantContentToText(assistant.Content),
        ToolResultMessage toolResult => ToolResultToText(toolResult),
        // TS 的兜底分支把未知 role 当 toolResult 处理，对自定义 role 会读到 undefined 的 content
        // 并抛 TypeError。端口的 provider 只见到 convertToLlm 归一化后的标准消息，该分支不可达；
        // 这里返回空串而不是抛错，免得测试 provider 因上下文里出现自定义消息而整体失败。
        _ => string.Empty,
    };

    private static string SystemMessageToText(SystemMessage message)
        => string.Join("\n", new[] { Pi.Ai.Utils.Text.GetSystemMessageText(message) }
            .Concat((message.ToolsRemoved ?? []).Select(tool => $"tool-:{JsonStringify(tool)}"))
            .Concat((message.ToolsAdded ?? []).Select(tool => $"tool+:{JsonStringify(tool)}"))
            .Where(part => part.Length > 0));

    /// <summary>把若干条提示文本按空行拼接后的字符数。对应 TS <c>joinedLength</c>。</summary>
    /// <param name="messages">逐条消息的提示文本。</param>
    /// <param name="count">只计前 <paramref name="count"/> 条；缺省为全部。</param>
    private static long JoinedLength(IReadOnlyList<string> messages, int? count = null)
    {
        var length = count ?? messages.Count;
        long total = length > 0 ? (length - 1) * 2 : 0;
        for (var index = 0; index < length; index++) total += messages[index].Length;
        return total;
    }

    /// <summary>两个字符串的公共前缀长度。对应 TS <c>commonPrefixLength</c>。</summary>
    private static long CommonPrefixLength(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        var index = 0;
        while (index < length && a[index] == b[index]) index++;
        return index;
    }

    /// <summary>
    /// 两个提示的公共前缀长度：整条相等的消息直接跳过，只从第一条不同的消息开始逐字符比较。
    /// 对应 TS <c>commonPromptPrefixLength</c>。
    /// </summary>
    private static long CommonPromptPrefixLength(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        var index = 0;
        while (index < previous.Count && index < current.Count && previous[index] == current[index]) index++;

        // TS：`index === messages.length ? "" : (index > 0 ? "\n\n" : "") + messages.slice(index).join("\n\n")`
        static string Rest(IReadOnlyList<string> messages, int from)
            => from == messages.Count
                ? ""
                : (from > 0 ? "\n\n" : "") + string.Join("\n\n", messages.Skip(from));

        return JoinedLength(previous, index)
            + CommonPrefixLength(Rest(previous, index), Rest(current, index));
    }

    /// <summary>
    /// 用估算值覆盖助手消息的 <c>usage</c>，并维护按 <c>sessionId</c> 键控的提示缓存。
    /// 对应 TS <c>withUsageEstimate</c>。
    /// </summary>
    /// <remarks>
    /// 逐条消息各留一份提示文本，整段提示用空行拼接；缓存命中时按**逐条比较**算公共前缀
    /// （上游 <c>fe11328b0</c> 把原先「整段拼接成一个大字符串再比」改成这个形态，
    /// 以免消息边界不同却字符相同的两段提示被误判成同前缀）。
    /// 脚本自带的 usage 会被整体取代——TS 侧 <c>withUsageEstimate</c> 也是无条件覆盖。
    /// </remarks>
    private static AssistantMessage WithUsageEstimate(
        AssistantMessage message,
        TranscriptContext context,
        SimpleStreamOptions? options,
        Dictionary<string, IReadOnlyList<string>> promptCache)
    {
        var prompt = context.Messages
            .Select(item => $"{RoleOf(item)}:{MessageToText(item)}")
            .ToList();
        var promptLength = JoinedLength(prompt);
        var promptTokens = (long)Math.Ceiling(promptLength / 4.0);
        var outputTokens = EstimateTokens(AssistantContentToText(message.Content));

        var input = promptTokens;
        long cacheRead = 0;
        long cacheWrite = 0;
        var sessionId = options?.SessionId;

        if (!string.IsNullOrEmpty(sessionId) && options?.CacheRetention != "none")
        {
            if (promptCache.TryGetValue(sessionId, out var previousPrompt))
            {
                var cachedChars = CommonPromptPrefixLength(previousPrompt, prompt);
                cacheRead = (long)Math.Ceiling(cachedChars / 4.0);
                cacheWrite = (long)Math.Ceiling((promptLength - cachedChars) / 4.0);
                input = Math.Max(0, promptTokens - cacheRead);
            }
            else
            {
                cacheWrite = promptTokens;
            }

            promptCache[sessionId] = prompt;
        }

        return message with
        {
            UsageStats = new Usage(input, outputTokens, cacheRead, cacheWrite)
            {
                TotalTokens = input + outputTokens + cacheRead + cacheWrite,
                Cost = UsageCost.Zero,
            },
        };
    }

    /// <summary>TS 消息的 <c>role</c> 判别值（提示文本里逐条加前缀用）。</summary>
    private static string RoleOf(ChatMessage message) => message switch
    {
        SystemMessage => "system",
        UserMessage => "user",
        AssistantMessage => "assistant",
        ToolResultMessage => "toolResult",
        _ => "unknown",
    };

    private static string JsonStringify(object? value)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(value);
        }
        catch
        {
            return "[unserializable]";
        }
    }

    // ---------- 切块与限速（对应 splitStringByTokenSize / scheduleChunk） ----------

    /// <summary>默认切块采样：<c>min + floor(random * (max - min + 1))</c>。对应 TS 的 <c>Math.random()</c> 一行。</summary>
    private static int SampleTokenSize(int min, int max) => min + Random.Shared.Next(max - min + 1);

    /// <summary>
    /// 按随机 token 尺寸把文本切成小块（1 token ≈ 4 字符）。对应 TS <c>splitStringByTokenSize</c>。
    /// </summary>
    /// <remarks>空串返回 <c>[""]</c>（TS 的 <c>chunks.length &gt; 0 ? chunks : [""]</c>）。</remarks>
    private static List<string> SplitStringByTokenSize(
        string text, int minTokenSize, int maxTokenSize, Func<int, int, int> sampleTokenSize)
    {
        var chunks = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var tokenSize = sampleTokenSize(minTokenSize, maxTokenSize);
            var charSize = Math.Max(1, tokenSize * 4);
            chunks.Add(text.Substring(index, Math.Min(charSize, text.Length - index)));
            index += charSize;
        }
        return chunks.Count > 0 ? chunks : [""];
    }

    /// <summary>
    /// 流式形状：切块尺寸、速率与两个可注入的缝（采样器 / 延迟器）。
    /// 对应 TS <c>createFauxCore</c> 里被 <c>streamWithDeltas</c> 捕获的那几个闭包变量。
    /// </summary>
    private sealed record StreamShape(
        int MinTokenSize,
        int MaxTokenSize,
        double? TokensPerSecond,
        Func<int, int, int> SampleTokenSize,
        Func<double, Task> DelayAsync)
    {
        public static StreamShape Default { get; } = new(
            DefaultMinTokenSize,
            DefaultMaxTokenSize,
            null,
            Faux.SampleTokenSize,
            DefaultDelayAsync);

        /// <summary>对应 TS <c>scheduleChunk</c>：速率缺省/非正 → 不延迟。</summary>
        public Task DelayForChunkAsync(string chunk)
        {
            if (TokensPerSecond is not > 0) return Task.CompletedTask;
            var delayMs = EstimateTokens(chunk) / TokensPerSecond.Value * 1000;
            return DelayAsync(delayMs);
        }
    }

    private static Task DefaultDelayAsync(double delayMs)
        => delayMs <= 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromMilliseconds(delayMs));

    // ---------- 消息构造（对应 cloneMessage / createDeferredMessage / createErrorMessage / createAbortedMessage） ----------

    /// <summary>
    /// 把脚本给出的消息身份字段替换为**请求上下文**的值。对应 TS <c>cloneMessage</c>。
    /// </summary>
    /// <remarks>
    /// TS 先 <c>structuredClone</c> 再覆盖 <c>api</c> / <c>provider</c> / <c>model</c> / <c>timestamp</c> / <c>usage</c>。
    /// 端口的 <see cref="ContentBlock"/> 全是不可变 record，回放过程只替换列表元素、从不就地改写，
    /// 所以无需深拷贝。
    /// </remarks>
    private static AssistantMessage CloneMessage(AssistantMessage message, string api, string provider, string modelId)
        => message with
        {
            Api = api,
            Provider = provider,
            Model = modelId,
            Timestamp = message.Timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UsageStats = message.UsageStats ?? DefaultUsage,
        };

    /// <summary>延后占位消息：空内容 + <c>deferred</c> 停止原因 + 句柄。对应 TS <c>createDeferredMessage</c>。</summary>
    private static AssistantMessage CreateDeferredMessage(ModelSpec model, DeferredHandle handle)
    {
        var message = new AssistantMessage(
            [],
            StopReason.Deferred,
            UsageStats: DefaultUsage,
            Model: model.Id,
            Api: model.Api,
            Provider: model.Provider,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        message.Deferred = handle;
        return message;
    }

    /// <summary>错误消息。对应 TS <c>createErrorMessage</c>（非 <c>Error</c> 取 <c>String(error)</c>）。</summary>
    private static AssistantMessage CreateErrorMessage(object? error, string api, string provider, string modelId)
        => new(
            [],
            StopReason.Error,
            ErrorMessage: error is Exception exception ? exception.Message : error?.ToString() ?? "undefined",
            UsageStats: DefaultUsage,
            Model: modelId,
            Api: api,
            Provider: provider,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>中止消息：保留已累计的部分内容。对应 TS <c>createAbortedMessage</c>。</summary>
    private static AssistantMessage CreateAbortedMessage(AssistantMessage partial)
        => partial with
        {
            StopReason = StopReason.Aborted,
            ErrorMessage = "Request was aborted",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    /// <summary>
    /// 判断本次请求是否已被中止。对应 TS 的 <c>signal?.aborted</c>：
    /// 端口的 <c>options.signal</c> 与 <c>StreamFn</c> 的取消令牌都是中止源（agent 运行时传的是后者）。
    /// </summary>
    private static bool IsAborted(SimpleStreamOptions? options, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested || options?.Signal.IsCancellationRequested == true;

    // ---------- 流式回放（对应 streamWithDeltas） ----------

    /// <summary>
    /// 逐块回放助手消息并推送增量事件。对应 TS <c>streamWithDeltas</c>。
    /// </summary>
    /// <remarks>
    /// 逐条对齐 TS 的语义：<c>signal</c> 预中止 → 只发 error 终态；每块与每个 chunk 前各查一次中止；
    /// <c>pending</c> 停止原因视为脚本错误（抛出，由调用方的 catch 转成 error 终态）；
    /// <c>error</c> / <c>aborted</c> 只发 error 并 return，**不发 done**。
    /// </remarks>
    private static async Task StreamWithDeltasAsync(
        AssistantMessageEventStream stream,
        AssistantMessage message,
        StreamShape shape,
        Func<bool> isAborted)
    {
        var blocks = new List<ContentBlock>();
        var partial = message with { Content = blocks, StopReason = StopReason.Pending };

        AssistantMessage Snapshot() => partial with { Content = blocks.ToList() };

        void Abort()
        {
            var aborted = CreateAbortedMessage(Snapshot());
            stream.Push(new AssistantMessageEvent.Error(aborted.StopReason, aborted.ErrorMessage ?? "", aborted));
            stream.End(aborted);
        }

        if (isAborted())
        {
            Abort();
            return;
        }

        stream.Push(new AssistantMessageEvent.Start(Snapshot()));
        var sequence = 0L;

        for (var index = 0; index < message.Content.Count; index++)
        {
            if (isAborted())
            {
                Abort();
                return;
            }

            var block = message.Content[index];

            if (block is ThinkingContent thinking)
            {
                blocks.Add(new ThinkingContent(""));
                stream.Push(new AssistantMessageEvent.ThinkingStart(index, Snapshot()));
                foreach (var chunk in SplitStringByTokenSize(
                             thinking.Thinking, shape.MinTokenSize, shape.MaxTokenSize, shape.SampleTokenSize))
                {
                    await shape.DelayForChunkAsync(chunk).ConfigureAwait(false);
                    if (isAborted())
                    {
                        Abort();
                        return;
                    }

                    var current = (ThinkingContent)blocks[index];
                    blocks[index] = current with { Thinking = current.Thinking + chunk };
                    stream.Push(new AssistantMessageEvent.ThinkingDelta(
                        index, chunk, ++sequence, current.Signature, Snapshot()));
                }

                stream.Push(new AssistantMessageEvent.ThinkingEnd(index, thinking.Thinking, Snapshot()));
                continue;
            }

            if (block is TextContent text)
            {
                blocks.Add(new TextContent(""));
                stream.Push(new AssistantMessageEvent.TextStart(index, Snapshot()));
                foreach (var chunk in SplitStringByTokenSize(
                             text.Text, shape.MinTokenSize, shape.MaxTokenSize, shape.SampleTokenSize))
                {
                    await shape.DelayForChunkAsync(chunk).ConfigureAwait(false);
                    if (isAborted())
                    {
                        Abort();
                        return;
                    }

                    var current = (TextContent)blocks[index];
                    blocks[index] = current with { Text = current.Text + chunk };
                    stream.Push(new AssistantMessageEvent.TextDelta(index, chunk, ++sequence, Snapshot()));
                }

                stream.Push(new AssistantMessageEvent.TextEnd(index, text.Text, Snapshot()));
                continue;
            }

            if (block is ToolCallContent toolCall)
            {
                // TS：先放一个 arguments 为 `{}` 的占位块，参数以 JSON 文本增量流出，最后一次性落定。
                blocks.Add(new ToolCallContent(toolCall.Id, toolCall.Name, new Dictionary<string, object?>()));
                stream.Push(new AssistantMessageEvent.ToolCallStart(index, Snapshot()));
                foreach (var chunk in SplitStringByTokenSize(
                             JsonStringify(toolCall.Arguments), shape.MinTokenSize, shape.MaxTokenSize,
                             shape.SampleTokenSize))
                {
                    await shape.DelayForChunkAsync(chunk).ConfigureAwait(false);
                    if (isAborted())
                    {
                        Abort();
                        return;
                    }

                    stream.Push(new AssistantMessageEvent.ToolCallDelta(index, index, chunk, Snapshot()));
                }

                blocks[index] = toolCall;
                stream.Push(new AssistantMessageEvent.ToolCallEnd(index, toolCall, Snapshot()));
                continue;
            }

            // TS 的 `block.type` 三路之外不会有第四种（联合类型穷尽）；端口若出现未知块，
            // 等价于 TS 在 `(partial.content[index] as TextContent).text` 上抛 TypeError。
            throw new InvalidOperationException($"faux streamWithDeltas does not support {block.GetType().Name}.");
        }

        if (message.StopReason == StopReason.Pending)
        {
            throw new InvalidOperationException("Faux response ended without a stop reason");
        }

        // TS：error / aborted 只发错误终态并 return，**不再**发 done。
        if (message.StopReason is StopReason.Error or StopReason.Aborted)
        {
            stream.Push(new AssistantMessageEvent.Error(
                message.StopReason, message.ErrorMessage ?? "unknown", message));
            stream.End(message);
            return;
        }

        stream.Push(new AssistantMessageEvent.Done(message.StopReason, message));
        stream.End(message);
    }

    // ---------- 端口专有的静态便捷入口 ----------

    /// <summary>
    /// 创建脚本化 <see cref="StreamFn"/>：每次调用按顺序回放一条脚本条目。
    /// </summary>
    /// <remarks>
    /// 这是端口为测试提供的静态便捷入口，TS 侧没有对应导出（TS 走 <c>fauxProvider()</c> /
    /// <c>createFauxCore()</c>）。提示缓存挂在**这个 StreamFn 实例**上——TS 挂在
    /// <c>createFauxCore</c> 的闭包上，两者生命周期等价（一个内核一份缓存）。
    /// 脚本耗尽时以 <see cref="StopReason.Error"/> 终态收尾（文案与 TS 一致：<c>No more faux responses queued</c>）。
    /// </remarks>
    public static StreamFn CreateStreamFn(IReadOnlyList<FauxResponseStep> script)
    {
        var core = new FauxCore();
        core.SetResponses(script);
        return (model, context, options, cancellationToken) => Task.FromResult(
            core.Stream(ToModelSpec(model), context, options, cancellationToken));
    }

    /// <summary>
    /// <see cref="StreamFn"/> 边界的 <see cref="Model"/> → 内核的 <see cref="ModelSpec"/> 适配。
    /// TS 只有一个 <c>Model</c> 类型，端口把「边界模型」与「目录模型」分开，故需要这一次转换。
    /// </summary>
    private static ModelSpec ToModelSpec(Model model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Api = model.Api,
        Provider = model.Provider,
        BaseUrl = DefaultBaseUrl,
    };

    // ---------- fauxProvider() ----------

    /// <summary>真实 provider 句柄。对应 TS <c>FauxProviderHandle</c>。</summary>
    public sealed record FauxProviderHandle
    {
        /// <summary>经 <c>createProvider</c> 装配出的 provider（含认证语义）。</summary>
        public required IProvider Provider { get; init; }

        public required string Api { get; init; }

        public required IReadOnlyList<ModelSpec> Models { get; init; }

        public required FauxProviderState State { get; init; }

        /// <summary>按 id 取模型；<c>null</c> 取第一个。对应 TS 的重载 <c>getModel()</c>。</summary>
        public required Func<string?, ModelSpec?> GetModel { get; init; }

        public required Action<IEnumerable<FauxResponseStep>> SetResponses { get; init; }

        public required Action<IEnumerable<FauxResponseStep>> AppendResponses { get; init; }

        public required Func<int> GetPendingResponseCount { get; init; }
    }

    /// <summary>
    /// 装配一个带认证语义的 faux provider。对应 TS <c>fauxProvider()</c>。
    /// </summary>
    /// <remarks>
    /// TS：
    /// <code>
    /// const faux = fauxProvider();
    /// const models = createModels();
    /// models.setProvider(faux.provider);
    /// faux.setResponses([fauxAssistantMessage("hi")]);
    /// </code>
    /// 与 <c>CompatApi.RegisterFauxProvider()</c> 的区别：那条路走 api-registry（按 <c>model.api</c>
    /// 分派，不需要 <c>Models.SetProvider</c>），这条路走 provider 注册表（<c>model.provider</c> 分派）。
    /// 认证按 TS 给一个恒成功的 <c>apiKey.resolve</c>（返回空 <c>ModelAuth</c>），所以 faux 模型永远"已配置"。
    /// </remarks>
    public static FauxProviderHandle CreateProvider(FauxProviderOptions? options = null)
    {
        var core = new FauxCore(options);
        var provider = ProviderFactory.Create(new CreateProviderOptions
        {
            Id = core.Provider,
            Auth = new ProviderAuth { ApiKey = new FauxApiKeyAuth() },
            Models = core.Models,
            Api = new ProviderStreams
            {
                Stream = (model, context, streamOptions) => core.Stream(
                    model, context, ProviderStreamOptions.FromDictionary(streamOptions)),
                StreamSimple = (model, context, streamOptions) => core.Stream(
                    model, context, ProviderStreamOptions.FromDictionary(streamOptions)),
                FetchDeferred = (model, handle, streamOptions) => core.FetchDeferred(
                    model, handle, ProviderStreamOptions.FromDictionary(streamOptions)),
                CancelDeferred = (model, handle, streamOptions, cancellationToken) => core.CancelDeferredAsync(
                    model, handle, ProviderStreamOptions.FromDictionary(streamOptions), cancellationToken),
            },
        });

        return new FauxProviderHandle
        {
            Provider = provider,
            Api = core.Api,
            Models = core.Models,
            State = core.State,
            GetModel = core.GetModel,
            SetResponses = core.SetResponses,
            AppendResponses = core.AppendResponses,
            GetPendingResponseCount = () => core.PendingResponseCount,
        };
    }

    /// <summary>TS <c>auth: { apiKey: { name: "Faux", resolve: async () =&gt; ({ auth: {} }) } }</c>。</summary>
    private sealed class FauxApiKeyAuth : IApiKeyAuth
    {
        public string Name => "Faux";

        public Task<AuthResult?> ResolveAsync(Credential.ApiKey? credential, IAuthContext ctx,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AuthResult?>(new AuthResult { Auth = new ModelAuth() });
    }

    /// <summary>
    /// 可变脚本的 faux 内核：注册进 api-registry 后按序回放响应，支持追加。
    /// 对应 TS <c>providers/faux.ts</c> 的 <c>createFauxCore</c>（响应队列 + 状态 + 延后句柄）。
    /// </summary>
    public sealed class FauxCore
    {
        private readonly StreamShape _shape;
        private readonly FauxDeferredOptions? _deferredOptions;
        private readonly List<FauxResponseStep> _responses = [];
        private int _cursor;

        /// <summary>
        /// 按 <c>sessionId</c> 键控的提示缓存：每条消息一份提示文本。
        /// 对应 TS <c>createFauxCore</c> 里的 <c>const promptCache = new Map&lt;string, readonly string[]&gt;()</c>。
        /// </summary>
        private readonly Dictionary<string, IReadOnlyList<string>> _promptCache = new(StringComparer.Ordinal);

        /// <summary>延后响应登记表，键为句柄 id。对应 TS <c>deferredResponses</c>。</summary>
        private readonly Dictionary<string, DeferredEntry> _deferredResponses = new(StringComparer.Ordinal);

        /// <summary>延后响应登记条目。对应 TS <c>deferredResponses</c> 的 value 形状。</summary>
        private sealed class DeferredEntry
        {
            public required DeferredHandle Handle { get; init; }

            public required FauxResponseStep Step { get; init; }

            public required TranscriptContext Context { get; init; }

            public required SimpleStreamOptions? Options { get; init; }

            public required ModelSpec Model { get; init; }

            public int PendingFetches { get; set; }

            public bool Cancelled { get; set; }

            public AssistantMessage? Final { get; set; }
        }

        /// <summary>按选项装配内核。对应 TS <c>createFauxCore(options)</c>。</summary>
        public FauxCore(FauxProviderOptions? options = null)
        {
            options ??= new FauxProviderOptions();
            Api = options.Api ?? $"faux-{Guid.NewGuid():N}"[..12];
            Provider = options.Provider ?? DefaultProvider;

            // TS：min = max(1, min(tokenSize.min ?? 3, tokenSize.max ?? 5))；max = max(min, tokenSize.max ?? 5)
            var minTokenSize = Math.Max(
                1,
                Math.Min(options.TokenSize?.Min ?? DefaultMinTokenSize, options.TokenSize?.Max ?? DefaultMaxTokenSize));
            var maxTokenSize = Math.Max(minTokenSize, options.TokenSize?.Max ?? DefaultMaxTokenSize);
            _shape = StreamShape.Default with
            {
                MinTokenSize = minTokenSize,
                MaxTokenSize = maxTokenSize,
                TokensPerSecond = options.TokensPerSecond,
            };
            _deferredOptions = options.Deferred;

            var definitions = options.Models is { Count: > 0 }
                ? options.Models
                : [new FauxModelDefinition
                {
                    Id = DefaultModelId,
                    Name = DefaultModelName,
                    Reasoning = false,
                    Input = [ModelInput.Text, ModelInput.Image],
                    Cost = new ModelCostRates(0, 0, 0, 0),
                    ContextWindow = 128000,
                    MaxTokens = 16384,
                }];
            Models = definitions.Select(definition => new ModelSpec
            {
                Id = definition.Id,
                Name = definition.Name ?? definition.Id,
                Api = Api,
                Provider = Provider,
                BaseUrl = DefaultBaseUrl,
                Reasoning = definition.Reasoning ?? false,
                Input = definition.Input ?? [ModelInput.Text, ModelInput.Image],
                InputLimits = definition.InputLimits,
                Cost = definition.Cost ?? new ModelCostRates(0, 0, 0, 0),
                ContextWindow = definition.ContextWindow ?? 128000,
                MaxTokens = definition.MaxTokens ?? 16384,
            }).ToList();
        }

        /// <summary>
        /// 位置参数构造（端口专有，保留以兼容既有测试夹具）。
        /// TS 侧只有 <c>createFauxCore(options)</c>；新代码请改用 <see cref="FauxProviderOptions"/>。
        /// </summary>
        public FauxCore(string? api, string? provider)
            : this(new FauxProviderOptions { Api = api, Provider = provider })
        {
        }

        /// <summary>本内核注册的 api id（唯一，避免并发测试互相覆盖）。</summary>
        public string Api { get; }

        /// <summary>
        /// provider id。对应 TS <c>createFauxCore</c> 的 <c>const provider = options.provider ?? DEFAULT_PROVIDER</c>。
        /// 它会被写进每条回放消息的 <c>provider</c> 字段（TS <c>cloneMessage</c>）。
        /// </summary>
        public string Provider { get; }

        /// <summary>本内核的模型目录。对应 TS <c>createFauxCore</c> 返回的 <c>models</c>。</summary>
        public IReadOnlyList<ModelSpec> Models { get; }

        /// <summary>运行时状态（调用计数 / 续取计数 / 已取消句柄）。对应 TS <c>state</c>。</summary>
        public FauxProviderState State { get; } = new();

        /// <summary>尚未回放的响应数。对应 TS <c>getPendingResponseCount()</c>。</summary>
        public int PendingResponseCount => Math.Max(0, _responses.Count - _cursor);

        /// <summary>
        /// 切块采样器缝（端口专有，测试注入用）：<c>(min, max) =&gt; tokenSize</c>。
        /// 缺省走 <c>Random.Shared</c>（对应 TS 的 <c>Math.random()</c>）。
        /// </summary>
        internal Func<int, int, int>? SampleTokenSizeOverride { get; set; }

        /// <summary>
        /// 延迟器缝（端口专有，测试注入用）。缺省 <c>Task.Delay</c>（对应 TS 的 <c>setTimeout</c>）。
        /// </summary>
        internal Func<double, Task>? DelayAsyncOverride { get; set; }

        /// <summary>
        /// 当前生效的流式形状：静态配置 + 两个**在使用时**求值的缝
        /// （缝必须在构造之后设置，所以不能像普通字段那样在构造函数里定值）。
        /// </summary>
        private StreamShape CurrentShape => _shape with
        {
            SampleTokenSize = SampleTokenSizeOverride ?? SampleTokenSize,
            DelayAsync = DelayAsyncOverride ?? DefaultDelayAsync,
        };

        /// <summary>替换全部脚本并重置游标。对应 TS <c>setResponses()</c>。</summary>
        public void SetResponses(IEnumerable<FauxResponseStep> responses)
        {
            _responses.Clear();
            _responses.AddRange(responses);
            _cursor = 0;
        }

        /// <summary>追加脚本（不改动游标）。对应 TS <c>appendResponses()</c>。</summary>
        public void AppendResponses(IEnumerable<FauxResponseStep> responses) => _responses.AddRange(responses);

        /// <summary>按 id 取模型；<c>null</c> 取第一个。对应 TS <c>getModel()</c> 的两个重载。</summary>
        public ModelSpec? GetModel(string? id = null)
            => string.IsNullOrEmpty(id) ? Models[0] : Models.FirstOrDefault(model => model.Id == id);

        /// <summary>
        /// 构造该 api 下的模型描述（端口专有便捷入口，保留以兼容既有夹具）。
        /// 目录内的模型请走 <see cref="Models"/> / <see cref="GetModel"/>。
        /// </summary>
        public ModelSpec CreateModel(string id = DefaultModelId, string name = DefaultModelName)
            => new()
            {
                Id = id,
                Name = name,
                Api = Api,
                Provider = Provider,
                BaseUrl = DefaultBaseUrl,
                Input = [ModelInput.Text, ModelInput.Image],
                Cost = new ModelCostRates(0, 0, 0, 0),
                ContextWindow = 128000,
                MaxTokens = 16384,
            };

        /// <summary>
        /// 按序回放一条响应；脚本耗尽以 error 终态终止。
        /// 对应 TS <c>createFauxCore</c> 返回的 <c>stream</c>。
        /// </summary>
        public IAssistantMessageEventStream Stream(
            ModelSpec model,
            TranscriptContext context,
            SimpleStreamOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var outer = new AssistantMessageEventStream();
            var step = _responses.Count > _cursor ? _responses[_cursor++] : null;
            State.CallCount++;

            // TS 用 queueMicrotask 让事件流先返回；端口用 Task.Run 达到同样效果。
            // 注意**不能**把 cancellationToken 交给 Task.Run——令牌已取消时任务根本不会启动，
            // 流就永远不会收尾。TS 的语义是「已取消 → 发一个 aborted 终态」，由流内部处理。
            _ = Task.Run(async () =>
            {
                try
                {
                    await NotifyResponseAsync(options, model).ConfigureAwait(false);
                    if (step is null)
                    {
                        var exhausted = WithUsageEstimate(
                            CreateErrorMessage(
                                new InvalidOperationException("No more faux responses queued"),
                                Api, Provider, model.Id),
                            context, options, _promptCache);
                        outer.Push(new AssistantMessageEvent.Error(
                            exhausted.StopReason, exhausted.ErrorMessage ?? "unknown", exhausted));
                        outer.End(exhausted);
                        return;
                    }

                    if (options?.Deferred == true)
                    {
                        var handle = new DeferredHandle
                        {
                            Provider = model.Provider,
                            ModelId = model.Id,
                            Api = model.Api,
                            Id = RandomId("deferred"),
                            PollAfterMs = _deferredOptions?.PollAfterMs,
                        };
                        _deferredResponses[handle.Id] = new DeferredEntry
                        {
                            Handle = handle,
                            Step = step,
                            Context = context,
                            Options = options,
                            Model = model,
                            PendingFetches = Math.Max(0, _deferredOptions?.PendingFetches ?? 0),
                        };
                        await StreamWithDeltasAsync(
                            outer,
                            CreateDeferredMessage(model, handle),
                            CurrentShape,
                            () => IsAborted(options, cancellationToken)).ConfigureAwait(false);
                        return;
                    }

                    var message = await ResolveResponseAsync(step, context, options, model).ConfigureAwait(false);
                    await StreamWithDeltasAsync(
                        outer, message, CurrentShape, () => IsAborted(options, cancellationToken)).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    var message = CreateErrorMessage(error, Api, Provider, model.Id);
                    outer.Push(new AssistantMessageEvent.Error(
                        message.StopReason, message.ErrorMessage ?? "unknown", message));
                    outer.End(message);
                }
            });

            return outer;
        }

        /// <summary>
        /// 续取延后响应。对应 TS <c>createFauxCore</c> 返回的 <c>fetchDeferred</c>。
        /// </summary>
        public IAssistantMessageEventStream FetchDeferred(
            ModelSpec model,
            DeferredHandle handle,
            SimpleStreamOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var outer = new AssistantMessageEventStream();
            State.DeferredFetchCount++;

            _ = Task.Run(async () =>
            {
                try
                {
                    await NotifyResponseAsync(options, model).ConfigureAwait(false);
                    if (!_deferredResponses.TryGetValue(handle.Id, out var entry)
                        || entry.Handle.Provider != handle.Provider
                        || entry.Handle.ModelId != handle.ModelId
                        || entry.Handle.Api != handle.Api)
                    {
                        throw new InvalidOperationException($"Unknown faux deferred response: {handle.Id}");
                    }
                    if (entry.Cancelled)
                    {
                        throw new InvalidOperationException($"Faux deferred response was cancelled: {handle.Id}");
                    }

                    var isAborted = () => IsAborted(options, cancellationToken);

                    if (entry.PendingFetches > 0)
                    {
                        entry.PendingFetches--;
                        await StreamWithDeltasAsync(
                            outer, CreateDeferredMessage(model, entry.Handle), CurrentShape, isAborted).ConfigureAwait(false);
                        return;
                    }

                    if (entry.Final is null)
                    {
                        // TS 在提交最终请求前剥掉 deferred / signal / onResponse 三个字段，
                        // 其余（sessionId / cacheRetention 等）保留，用量估算据此照常命中缓存。
                        var submissionOptions = entry.Options is null
                            ? null
                            : entry.Options with { Deferred = false, Signal = default, OnResponse = null };
                        try
                        {
                            entry.Final = await ResolveResponseAsync(
                                entry.Step, entry.Context, submissionOptions, entry.Model).ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            entry.Final = CreateErrorMessage(error, Api, Provider, entry.Model.Id);
                        }
                    }

                    await StreamWithDeltasAsync(outer, entry.Final, CurrentShape, isAborted).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    var message = CreateErrorMessage(error, Api, Provider, model.Id);
                    outer.Push(new AssistantMessageEvent.Error(
                        message.StopReason, message.ErrorMessage ?? "unknown", message));
                    outer.End(message);
                }
            });

            return outer;
        }

        /// <summary>尽力取消延后响应。对应 TS <c>createFauxCore</c> 返回的 <c>cancelDeferred</c>。</summary>
        public async Task CancelDeferredAsync(
            ModelSpec model,
            DeferredHandle handle,
            SimpleStreamOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // TS：`state.cancelledDeferred.push(structuredClone(handle))`——存的是**快照**，
            // 之后调用方改写句柄对象也不会影响已记录的历史。
            State.CancelledDeferred.Add(handle with { });
            if (_deferredResponses.TryGetValue(handle.Id, out var entry)) entry.Cancelled = true;
            await NotifyResponseAsync(options, model).ConfigureAwait(false);
        }

        /// <summary>解析脚本条目 → 助手消息（工厂求值 + 身份字段 + 用量估算）。对应 TS <c>resolveResponse</c>。</summary>
        private async Task<AssistantMessage> ResolveResponseAsync(
            FauxResponseStep step,
            TranscriptContext context,
            SimpleStreamOptions? options,
            ModelSpec requestModel)
        {
            var resolved = step switch
            {
                FactoryStep factory => await factory.Factory(context, options, State, requestModel).ConfigureAwait(false),
                Response response => ToMessage(response),
                _ => throw new InvalidOperationException($"Unsupported faux response step: {step.GetType().Name}"),
            };
            return WithUsageEstimate(
                CloneMessage(resolved, Api, Provider, requestModel.Id), context, options, _promptCache);
        }

        /// <summary>把静态脚本条目转成助手消息（尚未补身份字段与用量）。</summary>
        private static AssistantMessage ToMessage(Response response)
        {
            var message = new AssistantMessage(
                response.Content,
                response.StopReason,
                ErrorMessage: response.ErrorMessage,
                Timestamp: response.Timestamp);
            message.Deferred = response.Deferred;
            message.ResponseId = response.ResponseId;
            return message;
        }

        /// <summary>对应 TS 每次请求开头与取消时都调的 <c>options.onResponse?.({status:200,headers:{}}, model)</c>。</summary>
        private static async Task NotifyResponseAsync(SimpleStreamOptions? options, ModelSpec model)
        {
            if (options?.OnResponse is not { } onResponse) return;
            await onResponse(
                new ProviderResponse { Status = 200, Headers = new Dictionary<string, string>() }, model)
                .ConfigureAwait(false);
        }
    }
}
