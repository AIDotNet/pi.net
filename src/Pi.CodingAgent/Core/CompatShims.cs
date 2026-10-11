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
    public static IReadOnlyDictionary<string, object?> ToDictionary(SimpleStreamOptions options)
    {
        var dict = new Dictionary<string, object?>();
        if (options.ApiKey is not null) dict["apiKey"] = options.ApiKey;
        if (options.BaseUrl is not null) dict["baseUrl"] = options.BaseUrl;
        if (options.MaxTokens is not null) dict["maxTokens"] = options.MaxTokens;
        if (options.Temperature is not null) dict["temperature"] = options.Temperature;
        if (options.StopSequences is not null) dict["stopSequences"] = options.StopSequences;
        if (options.SessionId is not null) dict["sessionId"] = options.SessionId;
        if (options.ChainOfThought is not null) dict["chainOfThought"] = options.ChainOfThought;
        if (options.Headers is not null) dict["headers"] = options.Headers;
        if (options.SamplingParams is not null) dict["samplingParams"] = options.SamplingParams;
        if (options.MaxRetries is not null) dict["maxRetries"] = options.MaxRetries;
        if (options.MaxRetryDelayMs is not null) dict["maxRetryDelayMs"] = options.MaxRetryDelayMs;
        if (options.TimeoutMs is not null) dict["timeoutMs"] = options.TimeoutMs;
        if (options.Transport is not null) dict["transport"] = options.Transport;
        if (options.CacheRetention is not null) dict["cacheRetention"] = options.CacheRetention;
        if (options.Metadata is not null) dict["metadata"] = options.Metadata;
        if (options.Env is not null) dict["env"] = options.Env;
        if (options.Signal.CanBeCanceled) dict["signal"] = options.Signal;
        if (options.OnPayload is not null) dict["onPayload"] = options.OnPayload;
        return dict;
    }
}
