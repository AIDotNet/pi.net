using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.CodingAgent.Core;

/// <summary>
/// compaction/branch-summarization 与 provider 兼容层之间的桥接辅助。
/// </summary>
public static class CompatShims
{
    private static readonly JsonSerializerOptions PlainTextOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>任意值的紧凑 JSON 文本（JSON.stringify 等价；undefined/缺失 → "null"）。</summary>
    public static string SerializeToPlainText(object? value) => value switch
    {
        null => "null",
        string text => System.Text.Json.JsonSerializer.Serialize(text, PlainTextOptions),
        JsonNode node => node.ToJsonString(PlainTextOptions),
        JsonElement { ValueKind: JsonValueKind.Undefined } => "null",
        JsonElement element => JsonSerializer.Serialize(element, PlainTextOptions),
        _ => JsonSerializer.Serialize(value, value.GetType(), PlainTextOptions),
    };

    /// <summary>瘦 Model 视图（StreamFn delegate 的参数形态）。</summary>
    public static Model ToModel(this ModelSpec spec) => new(
        spec.Id,
        spec.Name,
        spec.Api,
        spec.Provider,
        Reasoning: spec.Reasoning ? "true" : null,
        Metadata: null);

    /// <summary>
    /// 把 <see cref="SimpleStreamOptions"/> 展开为 compat 字典（键名与
    /// <see cref="Pi.Ai.Api.ProviderStreamOptions.FromDictionary"/> 的约定一致）。
    /// </summary>
    /// <remarks>
    /// 必须与 <c>FromDictionary</c> 严格互逆：TS 的 <c>streamFn</c> 收的就是一个选项对象，
    /// 原样转发给 <c>streamSimple</c>；端口在该边界上过一次字典，漏写任何键都等于把选项吞掉
    /// （<c>reasoning</c> 曾因此永远到不了 provider）。可空字段只在有值时写入，
    /// <c>deferred</c> 与 TS 一样只在为真时出现。
    /// </remarks>
    public static IReadOnlyDictionary<string, object?> ToDictionary(SimpleStreamOptions options)
    {
        var dict = new Dictionary<string, object?>();
        if (options.ApiKey is not null) dict["apiKey"] = options.ApiKey;
        if (options.BaseUrl is not null) dict["baseUrl"] = options.BaseUrl;
        if (options.MaxTokens is not null) dict["maxTokens"] = options.MaxTokens;
        if (options.Temperature is not null) dict["temperature"] = options.Temperature;
        if (options.StopSequences is not null) dict["stopSequences"] = options.StopSequences;
        if (options.SessionId is not null) dict["sessionId"] = options.SessionId;
        if (options.Headers is not null) dict["headers"] = options.Headers;
        if (options.SamplingParams is not null) dict["samplingParams"] = options.SamplingParams;
        if (options.MaxRetries is not null) dict["maxRetries"] = options.MaxRetries;
        if (options.MaxRetryDelayMs is not null) dict["maxRetryDelayMs"] = options.MaxRetryDelayMs;
        if (options.TimeoutMs is not null) dict["timeoutMs"] = options.TimeoutMs;
        if (options.WebsocketConnectTimeoutMs is not null) dict["websocketConnectTimeoutMs"] = options.WebsocketConnectTimeoutMs;
        if (options.Transport is not null) dict["transport"] = options.Transport;
        if (options.CacheRetention is not null) dict["cacheRetention"] = options.CacheRetention;
        if (options.Metadata is not null) dict["metadata"] = options.Metadata;
        if (options.Env is not null) dict["env"] = options.Env;
        if (options.Signal.CanBeCanceled) dict["signal"] = options.Signal;
        if (options.OnPayload is not null) dict["onPayload"] = options.OnPayload;
        if (options.OnResponse is not null) dict["onResponse"] = options.OnResponse;
        if (options.OnProviderStreamEvent is not null) dict["onProviderStreamEvent"] = options.OnProviderStreamEvent;
        if (options.ToolChoice is not null) dict["toolChoice"] = options.ToolChoice;
        if (options.Reasoning is not null) dict["reasoning"] = options.Reasoning;
        if (options.Deferred) dict["deferred"] = true;
        if (options.ThinkingBudgets is not null) dict["thinkingBudgets"] = options.ThinkingBudgets;
        return dict;
    }
}
