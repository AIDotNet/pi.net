using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>
/// pi-image API：把 pi 自己的图片生成协议直连后端——单次 POST <c>{ model, context, options }</c>
/// 到 <c>&lt;baseUrl&gt;/image</c>，200 响应是完成的 <c>{ output, usage?, responseId? }</c> JSON 对象。
/// 错误用非 2xx 状态与 <c>{ error: { message, code?, details? } }</c> 信封，与 pi-messages / pi-decision 一致。
/// 对应 TS <c>api/pi-image.ts</c>。
/// </summary>
public static class PiImage
{
    /// <summary>分类器 API 名。</summary>
    public const string ApiName = "pi-image";

    private const string Label = "Pi Image";

    /// <summary>图片生成入口。对应 TS <c>generateImages</c>。</summary>
    public static async Task<AssistantImages> GenerateImages(
        ModelSpec model, ImagesContext context, ImagesOptions? options,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var output = new AssistantImages
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Output = [],
            StopReason = ImagesStopReason.Stop,
            Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        };

        try
        {
            if (model.Api != ApiName)
            {
                throw new InvalidOperationException($"Unsupported image API: {model.Api}");
            }
            if (context.Input.Any(block => block is ImageContent) && !model.Input.Contains("image"))
            {
                throw new InvalidOperationException($"{Label} model {model.Id} does not support image input");
            }

            var payload = Payload(model, context, options);
            if (options?.OnPayload is { } onPayload)
            {
                var transformed = await onPayload(payload, model).ConfigureAwait(false);
                if (transformed is not null) payload = transformed;
            }

            var headers = Headers.Merge(
                Identity.GetIdentityHeaders(Identity.TargetOf(model), options?.Identity)
                    .ToDictionary(pair => pair.Key, pair => (string?)pair.Value),
                new Dictionary<string, string?> { ["authorization"] = options?.ApiKey is { } key ? $"Bearer {key}" : null },
                ModelHeaders(model),
                options?.Headers);

            var client = httpClient ?? OAuthHttp.Shared;
            var signal = options?.Signal ?? default;
            var (status, responseHeaders, body) = await ProviderRetry.RetryAsync(async () =>
            {
                using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(signal, cancellationToken);
                if (options?.TimeoutMs is { } timeout) perCallCts.CancelAfter(timeout);
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"{model.BaseUrl.TrimEnd('/')}/image")
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                if (headers is not null)
                {
                    foreach (var (key, value) in headers) request.Headers.TryAddWithoutValidation(key, value);
                }
                try
                {
                    var httpResponse = await client.SendAsync(request, perCallCts.Token).ConfigureAwait(false);
                    var responseHeaders = httpResponse.Headers.ToDictionary(
                        kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase);
                    var text = await httpResponse.Content.ReadAsStringAsync(perCallCts.Token).ConfigureAwait(false);
                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        var error = JsonNode.Parse(text) as JsonObject;
                        var message = error?.Obj("error")?.Str("message") ?? text;
                        throw new ProviderHttpException((int)httpResponse.StatusCode, message, headers: responseHeaders);
                    }
                    var parsed = JsonNode.Parse(text) as JsonObject
                        ?? throw new InvalidOperationException($"{Label} returned an unexpected response");
                    return ((int)httpResponse.StatusCode, responseHeaders, parsed);
                }
                catch (OperationCanceledException) when (!signal.IsCancellationRequested)
                {
                    throw new TimeoutException($"Request timed out after {options?.TimeoutMs}ms");
                }
            }, new ProviderRetryOptions
            {
                MaxRetries = options?.MaxRetries,
                MaxRetryDelayMs = options?.MaxRetryDelayMs,
                Signal = signal,
            }).ConfigureAwait(false);

            if (options?.OnResponse is { } onResponse)
            {
                await onResponse(new ProviderResponse
                {
                    Status = status,
                    Headers = responseHeaders,
                }, model).ConfigureAwait(false);
            }

            return BuildOutput(model, body);
        }
        catch (Exception error)
        {
            return output with
            {
                Output = [],
                StopReason = options is { Signal.IsCancellationRequested: true }
                    ? ImagesStopReason.Aborted
                    : ImagesStopReason.Error,
                ErrorMessage = ProviderError.Format(ProviderError.Normalize(error)),
            };
        }
    }

    private static AssistantImages BuildOutput(ModelSpec model, JsonObject body)
    {
        var output = new AssistantImages
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Output = ParseOutput(body["output"] as JsonArray),
            StopReason = ImagesStopReason.Stop,
            Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        };
        if (body.Str("responseId") is { Length: > 0 } responseId) output = output with { ResponseId = responseId };
        if (ParseUsage(body.Obj("usage"), model) is { } usage) output = output with { Usage = usage };
        return output;
    }

    /// <summary>请求体。对应 TS <c>PiImageRequest</c>。</summary>
    private static JsonObject Payload(ModelSpec model, ImagesContext context, ImagesOptions? options)
    {
        var wireOptions = new JsonObject();
        if (options?.AspectRatio is { } aspectRatio) wireOptions["aspectRatio"] = aspectRatio;
        if (options?.Size is { } size) wireOptions["size"] = size;
        if (options?.SessionId is { } sessionId) wireOptions["sessionId"] = sessionId;
        if (options?.Metadata is { } metadata) wireOptions["metadata"] = metadata.DeepClone();

        var payload = new JsonObject
        {
            ["model"] = model.Id,
            ["context"] = new JsonObject
            {
                ["input"] = WireContent(context.Input),
            },
        };
        if (wireOptions.Count > 0) payload["options"] = wireOptions;
        return payload;
    }

    /// <summary>内容块 → 线路 JSON（camelCase，与 TS ImagesInputContent 一致）。</summary>
    internal static JsonArray WireContent(IEnumerable<ContentBlock> blocks)
    {
        var array = new JsonArray();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextContent text:
                    array.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;
                case ImageContent image:
                    array.Add(new JsonObject
                    {
                        ["type"] = "image", ["data"] = image.Data, ["mimeType"] = image.MimeType,
                    });
                    break;
                default:
                    throw new InvalidOperationException($"{Label} cannot send content block {block.GetType().Name}");
            }
        }
        return array;
    }

    /// <summary>输出块解析。对应 TS <c>parseOutput</c>。</summary>
    private static List<ContentBlock> ParseOutput(JsonArray? output)
    {
        if (output is null) throw new InvalidOperationException($"{Label} returned an unexpected response");
        var result = new List<ContentBlock>();
        for (var index = 0; index < output.Count; index++)
        {
            if (output[index] is not JsonObject item)
            {
                throw new InvalidOperationException($"{Label} returned an invalid output block at index {index}");
            }
            switch (item.Str("type"))
            {
                case "text":
                    if (item.Str("text") is not { } text)
                    {
                        throw new InvalidOperationException($"{Label} returned an invalid text block at index {index}");
                    }
                    result.Add(new TextContent(text));
                    break;
                case "image":
                    var data = item.Str("data");
                    var mimeType = item.Str("mimeType");
                    if (data is null or "" || mimeType is null or "")
                    {
                        throw new InvalidOperationException($"{Label} returned an invalid image block at index {index}");
                    }
                    result.Add(new ImageContent(data, mimeType));
                    break;
                default:
                    throw new InvalidOperationException($"{Label} returned an unknown output block type at index {index}");
            }
        }
        return result;
    }

    /// <summary>用量解析。对应 TS <c>parsePiUsage</c>（v1.7.0 增加 imageOutput 拆分）。</summary>
    internal static Usage? ParseUsage(JsonObject? value, ModelSpec model)
    {
        if (value is null) return null;
        var hasTokens = value["input"] is not null || value["output"] is not null;
        var rawCost = value.Obj("cost");
        var hasProviderCost = rawCost is not null && rawCost.Num("total") is { } total && double.IsFinite(total);
        if (!hasTokens && !hasProviderCost) return null;

        static long NonNegative(JsonObject owner, string key)
        {
            var number = owner.Num(key);
            return number is { } parsed && double.IsFinite(parsed) && parsed >= 0 ? (long)parsed : 0;
        }

        var input = NonNegative(value, "input");
        var output = NonNegative(value, "output");
        var cacheRead = NonNegative(value, "cacheRead");
        var cacheWrite = NonNegative(value, "cacheWrite");
        var usage = new Usage(input, output, cacheRead, cacheWrite)
        {
            Reasoning = value.Num("reasoning") is { } reasoning && double.IsFinite(reasoning) && reasoning >= 0
                ? (long)reasoning : null,
            ImageOutput = value.Num("imageOutput") is { } imageOutput && double.IsFinite(imageOutput) && imageOutput >= 0
                ? (long)imageOutput : null,
            TotalTokens = value.Num("totalTokens") is { } tokens && double.IsFinite(tokens) && tokens >= 0
                ? (long)tokens : input + output + cacheRead + cacheWrite,
        };
        if (hasProviderCost && rawCost is not null)
        {
            usage = usage with
            {
                Cost = new UsageCost(
                    NonNegative(rawCost, "input"), NonNegative(rawCost, "output"),
                    NonNegative(rawCost, "cacheRead"), NonNegative(rawCost, "cacheWrite"),
                    NonNegative(rawCost, "total"))
                {
                    TotalFromProvider = rawCost["totalFromProvider"] is JsonValue flag
                        && flag.TryGetValue<bool>(out var fromProvider) ? fromProvider : null,
                },
            };
        }
        else
        {
            usage = ModelOperations.CalculateCost(model, usage);
        }
        return usage;
    }

    /// <summary>model.headers 存于目录条目的扩展字段（TS BaseModel.headers）。</summary>
    private static IReadOnlyDictionary<string, string?>? ModelHeaders(ModelSpec model)
    {
        if (model.Extra?["headers"] is not JsonObject headers) return null;
        var result = new Dictionary<string, string?>();
        foreach (var (key, value) in headers)
        {
            result[key] = value is JsonValue { } primitive && primitive.TryGetValue<string>(out var text)
                ? text
                : null;
        }
        return result;
    }
}
