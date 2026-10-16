using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// Faux 测试 provider。对应 TS <c>providers/faux.ts</c>：以脚本化的响应序列
/// 回放"助手回合"，不访问网络。用于测试 agent 循环与上层消费方。
/// </summary>
public static class Faux
{
    /// <summary>默认 faux 模型描述。</summary>
    public static Model DefaultModel { get; } = new(
        Id: "faux-1",
        Name: "Faux Model",
        Api: "faux",
        Provider: "faux");

    // ---------- 内容块工厂（对应 fauxText / fauxThinking / fauxToolCall） ----------

    public static TextContent Text(string text) => new(text);

    public static ThinkingContent Thinking(string thinking) => new(thinking);

    public static ToolCallContent ToolCall(string name, object? arguments, string? id = null)
        => new(id ?? $"tool_{Guid.NewGuid():N}", name, arguments);

    /// <summary>脚本条目：一个待回放的助手回合（内容块 + 停止原因）。</summary>
    /// <remarks>
    /// TS 的脚本条目就是一条 <c>AssistantMessage</c>，其中 <c>usage</c> 会被
    /// <c>withUsageEstimate</c> **无条件覆盖**，故端口不提供 <c>Usage</c> 字段——最终用量一律由
    /// <see cref="WithUsageEstimate"/> 估算。
    /// </remarks>
    public sealed record Response(
        IReadOnlyList<ContentBlock> Content,
        StopReason StopReason = StopReason.Stop,
        string? ErrorMessage = null)
    {
        /// <summary>纯文本回复。</summary>
        public static Response Text(string text) => new([new TextContent(text)]);

        /// <summary>工具调用回复（停止原因 ToolUse）。</summary>
        public static Response ToolUse(params ToolCallContent[] calls)
            => new(calls, StopReason.ToolUse);
    }

    // ---------- 提示文本与用量估算（对应 TS faux.ts 的 messageToText / withUsageEstimate 区） ----------

    /// <summary>
    /// faux 自己的粗估：固定 4 字符/token。对应 TS faux.ts 的 <c>estimateTokens</c>。
    /// 注意它与 <c>Utils/Estimate</c> 的 3.5 字符/token 不同——faux 是测试 provider 的近似，
    /// 上游漂移里 estimate.ts 改成 3.5 时**没有**改动 faux 的 <c>/4</c>。
    /// </summary>
    private static long EstimateTokens(string text) => (long)Math.Ceiling(text.Length / 4.0);

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

    /// <summary>
    /// 创建脚本化 <see cref="StreamFn"/>：每次调用按顺序回放一条 <see cref="Response"/>。
    /// 文本按小块切分以模拟流式；脚本耗尽时返回 <see cref="StopReason.Error"/> 终态（不抛异常，
    /// 符合 <see cref="StreamFn"/> 的「错误必须编码为终态事件」契约），测试应保证脚本长度匹配。
    /// </summary>
    /// <remarks>
    /// 这是端口为测试提供的静态便捷入口，TS 侧没有对应导出（TS 走 <c>fauxProvider()</c> /
    /// <c>createFauxCore()</c>）。提示缓存挂在**这个 StreamFn 实例**上——TS 挂在
    /// <c>createFauxCore</c> 的闭包上，两者生命周期等价（一个内核一份缓存）。
    /// </remarks>
    public static StreamFn CreateStreamFn(IReadOnlyList<Response> script)
    {
        var cursor = 0;
        var promptCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        return (model, context, options, cancellationToken) =>
        {
            if (cursor >= script.Count)
            {
                var exhausted = new AssistantMessageEventStream();
                var msg = WithUsageEstimate(
                    new AssistantMessage([], StopReason.Error, ErrorMessage: "Faux script exhausted",
                        Model: model.Id, Api: model.Api, Provider: model.Provider,
                        Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    context, options, promptCache);
                exhausted.Push(new AssistantMessageEvent.Error(msg.StopReason, "Faux script exhausted", msg));
                exhausted.End(msg);
                return Task.FromResult<IAssistantMessageEventStream>(exhausted);
            }

            var response = script[cursor++];
            var stream = new AssistantMessageEventStream();
            _ = Task.Run(
                () => Replay(stream, response, model.Id, model.Api, model.Provider,
                    context, options, promptCache, cancellationToken),
                cancellationToken);
            return Task.FromResult<IAssistantMessageEventStream>(stream);
        };
    }

    /// <summary>
    /// 可变脚本的 faux 内核：注册进 api-registry 后按序回放响应，支持追加。
    /// 对应 TS <c>providers/faux.ts</c> 的 <c>createFauxCore</c> 核心（响应队列 + 状态）。
    /// </summary>
    public sealed class FauxCore
    {
        private readonly List<Response> _responses = [];
        private int _cursor;

        /// <summary>
        /// 按 <c>sessionId</c> 键控的提示缓存：每条消息一份提示文本。
        /// 对应 TS <c>createFauxCore</c> 里的 <c>const promptCache = new Map&lt;string, readonly string[]&gt;()</c>。
        /// </summary>
        private readonly Dictionary<string, IReadOnlyList<string>> _promptCache = new(StringComparer.Ordinal);

        public FauxCore(string? api = null, string? provider = null)
        {
            Api = api ?? $"faux-{Guid.NewGuid():N}"[..12];
            Provider = provider ?? "faux";
        }

        /// <summary>本内核注册的 api id（唯一，避免并发测试互相覆盖）。</summary>
        public string Api { get; }

        /// <summary>
        /// provider id。对应 TS <c>createFauxCore</c> 的 <c>const provider = options.provider ?? DEFAULT_PROVIDER</c>。
        /// 它会被写进每条回放消息的 <c>provider</c> 字段（TS <c>cloneMessage</c>）。
        /// </summary>
        public string Provider { get; }

        /// <summary>尚未回放的响应数。对应 TS <c>getPendingResponseCount()</c>。</summary>
        public int PendingResponseCount => Math.Max(0, _responses.Count - _cursor);

        /// <summary>替换全部脚本并重置游标。对应 TS <c>setResponses()</c>。</summary>
        public void SetResponses(IEnumerable<Response> responses)
        {
            _responses.Clear();
            _responses.AddRange(responses);
            _cursor = 0;
        }

        /// <summary>追加脚本（不改动游标）。对应 TS <c>appendResponses()</c>。</summary>
        public void AppendResponses(IEnumerable<Response> responses) => _responses.AddRange(responses);

        /// <summary>构造该 api 下的模型描述。对应 TS 的 faux 模型表。</summary>
        public ModelSpec CreateModel(string id = "faux-1", string name = "Faux Model")
            => new()
            {
                Id = id,
                Name = name,
                Api = Api,
                Provider = Provider,
                BaseUrl = "https://faux.invalid",
            };

        /// <summary>按序回放一条响应；脚本耗尽以 error 终态终止。</summary>
        public IAssistantMessageEventStream Stream(
            ModelSpec model,
            TranscriptContext context,
            SimpleStreamOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var stream = new AssistantMessageEventStream();
            if (_cursor >= _responses.Count)
            {
                var message = WithUsageEstimate(
                    new AssistantMessage([], StopReason.Error,
                        ErrorMessage: "Faux script exhausted",
                        Model: model.Id, Api: Api, Provider: Provider,
                        Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    context, options, _promptCache);
                stream.Push(new AssistantMessageEvent.Error(message.StopReason, "Faux script exhausted", message));
                stream.End(message);
                return stream;
            }

            var response = _responses[_cursor++];
            _ = Task.Run(
                () => Replay(stream, response, model.Id, Api, Provider,
                    context, options, _promptCache, cancellationToken),
                cancellationToken);
            return stream;
        }
    }

    /// <summary>
    /// 回放一条脚本响应。对应 TS <c>resolveResponse</c> + <c>streamWithDeltas</c> 的合体：
    /// 先 <c>cloneMessage</c> 语义补 api/provider/model/timestamp，再 <c>withUsageEstimate</c> 覆盖 usage，
    /// 最后按内容块逐块发增量事件。
    /// </summary>
    /// <param name="modelId">
    /// 对应 TS <c>cloneMessage(resolved, api, provider, requestModel.id)</c> 的 <c>requestModel.id</c>——
    /// 必须取请求模型自身的 id，而不是内建的 <c>"faux-1"</c>：<c>FauxCore.CreateModel(id)</c> 允许自定义 id。
    /// </param>
    /// <param name="api">对应 TS <c>createFauxCore</c> 闭包里的 <c>api</c>（可被 <c>options.api</c> 覆盖）。</param>
    /// <param name="provider">对应 TS <c>createFauxCore</c> 闭包里的 <c>provider</c>（可被 <c>options.provider</c> 覆盖）。</param>
    private static async Task Replay(
        AssistantMessageEventStream stream,
        Response response,
        string modelId,
        string api,
        string provider,
        TranscriptContext context,
        SimpleStreamOptions? options,
        Dictionary<string, IReadOnlyList<string>> promptCache,
        CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sequence = 0L;

        // TS：先 cloneMessage 补 api/provider/model/timestamp，再 withUsageEstimate 覆盖 usage。
        var resolved = new AssistantMessage(response.Content, response.StopReason, response.ErrorMessage,
            Model: modelId, Api: api, Provider: provider, Timestamp: timestamp);
        var message = WithUsageEstimate(resolved, context, options, promptCache);

        // 先发 start（空内容，但已带估算用量）。
        var partial = message with { Content = [] };
        stream.Push(new AssistantMessageEvent.Start(partial));

        foreach (var block in response.Content)
        {
            ct.ThrowIfCancellationRequested();
            switch (block)
            {
                case TextContent text:
                {
                    // 按小块切分文本模拟流式增量。
                    const int chunkSize = 5;
                    var chunks = Enumerable.Range(0, (int)Math.Ceiling(text.Text.Length / (double)chunkSize))
                        .Select(i => text.Text.Substring(i * chunkSize, Math.Min(chunkSize, text.Text.Length - i * chunkSize)))
                        .ToList();
                    stream.Push(new AssistantMessageEvent.TextStart(0, partial));
                    var accumulated = string.Empty;
                    foreach (var chunk in chunks)
                    {
                        accumulated += chunk;
                        sequence++;
                        partial = ReplaceOrAppendText(partial, accumulated);
                        stream.Push(new AssistantMessageEvent.TextDelta(0, chunk, sequence, partial));
                    }
                    stream.Push(new AssistantMessageEvent.TextEnd(0, partial.Content.OfType<TextContent>().LastOrDefault()?.Text ?? "", partial));
                    break;
                }
                case ThinkingContent thinking:
                {
                    sequence++;
                    partial = AppendBlock(partial, new ThinkingContent(thinking.Thinking, thinking.Signature));
                    stream.Push(new AssistantMessageEvent.ThinkingDelta(0, thinking.Thinking, sequence, thinking.Signature, partial));
                    break;
                }
                case ToolCallContent toolCall:
                {
                    // 工具调用一次性给出（参数增量解析留待真实 provider 移植时实现）。
                    partial = AppendBlock(partial, toolCall);
                    stream.Push(new AssistantMessageEvent.ToolCallDelta(
                        partial.Content.OfType<ToolCallContent>().ToList().IndexOf(toolCall),
                        partial.Content.OfType<ToolCallContent>().ToList().IndexOf(toolCall), "", partial));
                    break;
                }
            }
        }

        var final = partial with
        {
            StopReason = response.StopReason,
            ErrorMessage = response.ErrorMessage,
            UsageStats = message.UsageStats,
            Model = modelId,
            Api = api,
            Provider = provider,
            ThinkingLevel = null,
            Timestamp = timestamp,
        };

        // TS streamWithDeltas：error / aborted 只发错误终态并 return，**不再**发 done。
        if (response.StopReason is StopReason.Error or StopReason.Aborted)
        {
            stream.Push(new AssistantMessageEvent.Error(final.StopReason, response.ErrorMessage ?? "unknown", final));
            stream.End(final);
            return;
        }

        stream.Push(new AssistantMessageEvent.Done(final.StopReason, final));
        stream.End(final);
    }

    private static AssistantMessage ReplaceOrAppendText(AssistantMessage message, string fullText)
    {
        var blocks = message.Content.ToList();
        var index = blocks.FindIndex(b => b is TextContent);
        if (index >= 0) blocks[index] = new TextContent(fullText);
        else blocks.Add(new TextContent(fullText));
        return message with { Content = blocks };
    }

    private static AssistantMessage AppendBlock(AssistantMessage message, ContentBlock block)
    {
        var blocks = message.Content.ToList();
        blocks.Add(block);
        return message with { Content = blocks };
    }
}
