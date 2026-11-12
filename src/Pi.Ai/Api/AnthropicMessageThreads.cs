using System.Net.Http;
using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>
/// Anthropic 消息线程（上游 1.5.1 #11545）：服务端保存会话，后续请求可从上一条回复续接、只发新增消息。
/// 端口仍构建完整请求；<see cref="PrepareThreadRequest"/> 与本会话上一次请求的前缀比较，未变化时改成
/// <c>thread:continue</c> 并裁掉前缀；被拒/前缀变化时退回 <c>thread:create</c> 或不带 thread 的正常请求。
/// 对应 TS <c>anthropic-messages.ts</c> 的 threadState / hashThreadPrefix / prepareThreadRequest /
/// sendThreadRequest / recordThreadReply。
/// </summary>
public static class AnthropicMessageThreads
{
    public const string Beta = "message-threads-2026-08-12";
    private const int MaxStateEntries = 10_000;

    /// <summary>回复后下一次请求可续接的位置。</summary>
    private sealed record ThreadEntry(int PrefixCount, string PrefixHash, string ReplyId);

    /// <summary>一次线程请求：会话 id 与要发出的请求体。</summary>
    public sealed record PreparedThreadRequest(string SessionId, JsonObject Body);

    private static readonly string[] NewThreadErrors =
    [
        "thread_not_found",
        "thread_already_continued",
        "thread_continue_in_flight",
        "thread_fingerprint_mismatch",
    ];

    private static readonly object Gate = new();
    // value == null 表示该会话已被端点拒绝线程（"off"）。
    private static readonly Dictionary<string, ThreadEntry?> States = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> Order = new();
    private static readonly Dictionary<string, LinkedListNode<string>> Nodes = new(StringComparer.Ordinal);

    static AnthropicMessageThreads()
    {
        // 会话结束（或进程退出）时释放状态，对应 TS registerSessionResourceCleanup。
        SessionResources.Register(sessionId =>
        {
            lock (Gate)
            {
                if (sessionId is null)
                {
                    States.Clear();
                    Order.Clear();
                    Nodes.Clear();
                    return;
                }
                if (Nodes.Remove(sessionId, out var node)) Order.Remove(node);
                States.Remove(sessionId);
            }
        });
    }

    private static ThreadEntry? Get(string sessionId)
    {
        lock (Gate) return States.TryGetValue(sessionId, out var entry) ? entry : null;
    }

    private static bool Contains(string sessionId)
    {
        lock (Gate) return States.ContainsKey(sessionId);
    }

    private static void Set(string sessionId, ThreadEntry? value)
    {
        lock (Gate)
        {
            // 重新插入以维持「最旧在前」的插入序，便于容量淘汰。
            if (Nodes.Remove(sessionId, out var old)) Order.Remove(old);
            var node = Order.AddLast(sessionId);
            Nodes[sessionId] = node;
            States[sessionId] = value;
            while (Order.Count > MaxStateEntries)
            {
                var oldest = Order.First!;
                Order.RemoveFirst();
                Nodes.Remove(oldest.Value);
                States.Remove(oldest.Value);
            }
        }
    }

    private static void Delete(string sessionId)
    {
        lock (Gate)
        {
            if (Nodes.Remove(sessionId, out var node)) Order.Remove(node);
            States.Remove(sessionId);
        }
    }

    /// <summary>是否默认对直连 Anthropic 端点启用消息线程（可由 compat.supportsMessageThreads 覆盖）。</summary>
    public static bool IsEnabledByDefault(ModelSpec model)
        => model.Provider == "anthropic"
           && model.BaseUrl.Contains("api.anthropic.com", StringComparison.Ordinal);

    /// <summary>
    /// 线程续接所依赖内容的短哈希：必须在一个线程内保持不变的设置，加上服务端已持有的消息。
    /// 归一化会去掉 cache_control 标记、把字符串 content 折成 [{type:text,text}]，使「同一含义」的消息哈希一致。
    /// 对应 TS <c>hashThreadPrefix</c>。
    /// </summary>
    public static string HashThreadPrefix(ModelSpec model, JsonObject parameters, JsonArray messages)
    {
        var settings = new JsonArray();
        foreach (var key in new[] { "model", "system", "tools", "thinking", "output_config", "tool_choice", "betas", "fallbacks" })
        {
            settings.Add(parameters[key]?.DeepClone());
        }
        var settingsPart = new JsonArray
        {
            model.Provider,
            model.BaseUrl,
            settings,
            NormalizeForHash(messages),
        };
        return Hash.ShortHash(settingsPart.ToJsonString());
    }

    /// <summary>JSON.stringify 的 replacer：去 cache_control；字符串 content → [{type:"text",text}]。</summary>
    private static JsonNode? NormalizeForHash(JsonNode? node)
        => node switch
        {
            JsonObject obj => NormalizeObject(obj),
            JsonArray arr => new JsonArray(arr.Select(NormalizeForHash).ToArray()),
            _ => node?.DeepClone(),
        };

    private static JsonObject NormalizeObject(JsonObject obj)
    {
        var result = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (key == "cache_control") continue;
            if (key == "content" && value is JsonValue textValue && textValue.TryGetValue<string>(out var text))
            {
                result[key] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
            }
            else
            {
                result[key] = NormalizeForHash(value);
            }
        }
        return result;
    }

    /// <summary>从头创建线程。</summary>
    public static PreparedThreadRequest CreateThreadRequest(string sessionId, JsonObject parameters)
    {
        var body = (JsonObject)parameters.DeepClone();
        body["thread"] = new JsonObject { ["type"] = "create" };
        return new PreparedThreadRequest(sessionId, body);
    }

    /// <summary>
    /// 准备线程请求：会话被端点拒绝过（"off"）返回 null；前缀未变且消息变多时改成 continue 并裁掉前缀，
    /// 否则从头 create。每条回复只能续接一次，命中即删除状态避免并发请求重复续接。对应 TS <c>prepareThreadRequest</c>。
    /// </summary>
    public static PreparedThreadRequest? PrepareThreadRequest(ModelSpec model, string sessionId, JsonObject parameters)
    {
        if (Contains(sessionId) && Get(sessionId) is null) return null; // "off"
        var state = Get(sessionId);
        var messages = parameters["messages"] as JsonArray;
        if (state is not null && messages is not null && messages.Count > state.PrefixCount)
        {
            var prefix = new JsonArray(messages.Take(state.PrefixCount).Select(m => m?.DeepClone()).ToArray());
            if (HashThreadPrefix(model, parameters, prefix) == state.PrefixHash)
            {
                Delete(sessionId);
                var body = (JsonObject)parameters.DeepClone();
                body.Remove("system");
                body.Remove("tools");
                body["messages"] = new JsonArray(messages.Skip(state.PrefixCount).Select(m => m?.DeepClone()).ToArray());
                body["thread"] = new JsonObject
                {
                    ["type"] = "continue",
                    ["previous_message_id"] = state.ReplyId,
                };
                return new PreparedThreadRequest(sessionId, body);
            }
        }
        return CreateThreadRequest(sessionId, parameters);
    }

    /// <summary>线程拒绝响应体里的 error.details.error_code。对应 TS <c>getThreadErrorCode</c>。</summary>
    public static string? GetThreadErrorCode(ProviderHttpException error)
    {
        try
        {
            return JsonNode.Parse(error.Body)?["error"]?["details"]?["error_code"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>可用「新线程」修复的拒绝码。</summary>
    public static bool ShouldStartNewThread(string? code)
        => code is not null && NewThreadErrors.Contains(code, StringComparer.Ordinal);

    /// <summary>端点不支持 thread 字段：显式 unsupported，或 400 且文案提到 thread 但没有 error_code。</summary>
    public static bool ThreadsUnsupported(ProviderHttpException error, string? code)
        => code == "thread_unsupported_request"
           || (code is null && error.Status == 400
               && error.Message.Contains("thread", StringComparison.OrdinalIgnoreCase));

    /// <summary>把会话标记为 "off"（端点不支持线程）。</summary>
    public static void SetOff(string sessionId) => Set(sessionId, null);

    /// <summary>
    /// 发送线程请求：仅线程相关拒绝会被吞掉并以「新线程」或「不带 thread 的正常请求」重发一次；
    /// 其它错误（提示过长、strict 工具超限等）原样抛出，交给外层的 strict 工具降级逻辑。对应 TS <c>sendThreadRequest</c>。
    /// </summary>
    public static async Task<(HttpResponseMessage Response, PreparedThreadRequest? Thread)> SendThreadRequestAsync(
        PreparedThreadRequest thread,
        JsonObject parameters,
        Func<JsonObject, Task<HttpResponseMessage>> send,
        Action<AssistantMessageDiagnostic> addDiagnostic,
        CancellationToken signal)
    {
        try
        {
            return (await send(thread.Body).ConfigureAwait(false), thread);
        }
        catch (ProviderHttpException error) when (!signal.IsCancellationRequested)
        {
            var errorCode = GetThreadErrorCode(error);
            var startNewThread = ShouldStartNewThread(errorCode);
            var threadsUnsupported = ThreadsUnsupported(error, errorCode);
            if (!startNewThread && !threadsUnsupported) throw;

            addDiagnostic(new AssistantMessageDiagnostic(
                "anthropic_thread_fallback",
                DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                new DiagnosticErrorInfo(null, error.Message, null, null),
                new JsonObject { ["reason"] = errorCode ?? error.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

            if (startNewThread)
            {
                var created = CreateThreadRequest(thread.SessionId, parameters);
                return (await send(created.Body).ConfigureAwait(false), created);
            }

            // 带 threads beta 但不带 thread 字段的请求就是普通请求；端点不支持，关闭该会话的线程。
            var response = await send(parameters).ConfigureAwait(false);
            SetOff(thread.SessionId);
            return (response, null);
        }
    }

    /// <summary>
    /// 成功后记录下一次可续接的位置：把回复转换成下一次请求会用的形状，取 assistant 消息，
    /// 拼到本次请求消息末尾作为前缀。fallback 模型回答的回复服务端不保存，不记录。对应 TS <c>recordThreadReply</c>。
    /// </summary>
    public static void RecordThreadReply(
        ModelSpec model,
        PreparedThreadRequest thread,
        JsonObject parameters,
        AssistantMessage output,
        bool isOAuth,
        bool allowEmptySignature)
    {
        if (output.ResponseId is null || output.ResponseModel is not null) return;
        var reply = AnthropicMessages.ConvertReplyForThread(output, model, isOAuth, allowEmptySignature);
        if (parameters["messages"] is not JsonArray original) return;
        var prefix = new JsonArray(original.Select(m => m?.DeepClone()).ToArray());
        foreach (var message in reply) prefix.Add(message?.DeepClone());
        Set(thread.SessionId, new ThreadEntry(
            prefix.Count,
            HashThreadPrefix(model, parameters, prefix),
            output.ResponseId));
    }

    /// <summary>仅供测试：清空全部会话状态。</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            States.Clear();
            Order.Clear();
            Nodes.Clear();
        }
    }
}
