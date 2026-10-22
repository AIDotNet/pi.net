using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>
/// System One 请求体（不含传输层信封）。对应 TS <c>SystemOneWireRequest</c>。带图片时
/// <see cref="State"/> 变成「图片或其资源名」在前、原状态在后的数组，并填 <see cref="Assets"/> 或
/// <see cref="Images"/>。
/// </summary>
public sealed class SystemOneWireRequest
{
    public required JsonNode State { get; set; }

    public required JsonObject Questions { get; init; }

    /// <summary>以 System One 资源形式承载的图片，键为状态里引用的名字。</summary>
    public JsonObject? Assets { get; set; }

    /// <summary>以 data URL 承载的图片（Clef 对 System One 的扩展）。</summary>
    public List<string>? Images { get; set; }

    /// <summary>按 wire 顺序（state、questions、assets、images）投影成 JSON 对象。</summary>
    public JsonObject ToJsonObject()
    {
        var value = new JsonObject
        {
            ["state"] = State.DeepClone(),
            ["questions"] = Questions.DeepClone(),
        };
        if (Assets is not null) value["assets"] = Assets.DeepClone();
        if (Images is not null) value["images"] = new JsonArray([.. Images.Select(image => (JsonNode)image)]);
        return value;
    }
}

/// <summary>
/// 服务接收分类图片的方式（<c>SystemOneCompat.imageInput</c>）。对应 TS <c>SystemOneImageInput</c>。
/// </summary>
public enum SystemOneImageInput
{
    /// <summary>TypeSafe 的 <c>assets</c> 表：图片作为资源、其名字放在状态之前。仅 PNG / JPEG。</summary>
    Assets,

    /// <summary>Clef 的 <c>images</c> 列表（data URL），模型先读图片再读状态。PNG / JPEG / WebP，最多四张。</summary>
    Images,

    /// <summary>OpenRouter 的形式：<c>image_url</c> 片段放在状态之前。OpenRouter 会静默丢弃 <c>assets</c>。</summary>
    StateImageUrls,
}

/// <summary>字段全部落定的 System One 兼容设置。对应 TS <c>ResolvedSystemOneCompat</c>。</summary>
public sealed record SystemOneCompat
{
    public required SystemOneImageInput ImageInput { get; init; }

    public required bool SendImageOptInHeader { get; init; }
}

/// <summary>各服务承载 System One 模型的传输差异。对应 TS <c>SystemOneTransport</c>。</summary>
public sealed record SystemOneTransport
{
    /// <summary>该传输实现的分类器 API。</summary>
    public required string Api { get; init; }

    /// <summary>错误信息里的服务名。</summary>
    public required string Label { get; init; }

    /// <summary>模型未在 <c>compat</c> 里指定时使用的兼容设置。</summary>
    public required Func<ModelSpec, SystemOneCompat> DetectCompat { get; init; }

    /// <summary>绝对请求 URL。</summary>
    public required Func<ModelSpec, Uri> Url { get; init; }

    /// <summary>把 System One 请求包装进服务的请求信封。</summary>
    public required Func<ModelSpec, SystemOneWireRequest, JsonObject> Payload { get; init; }

    /// <summary>从服务响应信封提取 System One 输出（{answers, usage}）。</summary>
    public required Func<JsonObject, JsonObject> Output { get; init; }
}

/// <summary>
/// System One 结构化分类协议（TypeSafe 专有；Cloudflare/OpenRouter 转发同一协议）。
/// 对应 TS <c>api/system-one-shared.ts</c>。公共 <c>bool</c> 值映射为 wire 级 <c>noul</c>。
/// </summary>
public static class SystemOne
{
    /// <summary>值是否为对象（非数组）。对应 TS <c>isRecord</c>。</summary>
    public static bool IsRecord(JsonNode? value)
        => value is not null && value.GetValueKind() == System.Text.Json.JsonValueKind.Object;

    /// <summary>运行一次 System One 分类。对应 TS <c>classifySystemOne</c>。</summary>
    public static async Task<ClassifierResult> ClassifyAsync(
        SystemOneTransport transport,
        ModelSpec model,
        ClassifierContext context,
        ClassifierOptions? options,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var output = new ClassifierResult
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        };

        try
        {
            if (model.Api != transport.Api)
            {
                throw new InvalidOperationException($"Unsupported classifier API: {model.Api}");
            }
            if (string.IsNullOrEmpty(options?.ApiKey))
            {
                throw new InvalidOperationException($"No API key for provider: {model.Provider}");
            }
            var apiKey = options.ApiKey;
            var request = WireRequest(context);
            var headers = options.Headers;
            if (context.Images is { Count: > 0 } images)
            {
                if (!model.Input.Contains("image", StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"{transport.Label} does not support image input for {model.Id}");
                }
                var (imageInput, sendOptIn) = ResolveCompat(model, transport.DetectCompat(model));
                AddImages(transport.Label, model, imageInput, images, request);
                if (sendOptIn)
                {
                    var merged = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        [ImageOptInHeader] = "true",
                    };
                    if (options.Headers is not null)
                    {
                        foreach (var (key, value) in options.Headers) merged[key] = value;
                    }
                    headers = merged;
                }
            }
            var payload = transport.Payload(model, request);
            if (options.OnPayload is { } onPayload)
            {
                var transformed = await onPayload(payload, model).ConfigureAwait(false);
                if (transformed is not null) payload = transformed;
            }
            var client = httpClient ?? OAuthHttp.Shared;
            var signal = options.Signal;
            HttpResponseMessage response;
            JsonObject? body;
            (response, body) = await ProviderRetry.RetryAsync(async () =>
            {
                using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(signal, cancellationToken);
                if (options.TimeoutMs is { } timeout) perCallCts.CancelAfter(timeout);
                var request = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, transport.Url(model))
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                foreach (var (key, value) in RequestHeaders(model, apiKey, headers))
                {
                    if (value is not null) request.Headers.TryAddWithoutValidation(key, value);
                }
                try
                {
                    var httpResponse = await client.SendAsync(request, perCallCts.Token).ConfigureAwait(false);
                    var text = await httpResponse.Content.ReadAsStringAsync(perCallCts.Token).ConfigureAwait(false);
                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        throw new ProviderHttpException(
                            (int)httpResponse.StatusCode, text,
                            headers: httpResponse.Headers.ToDictionary(
                                kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase));
                    }
                    return (httpResponse, JsonNode.Parse(text) as JsonObject);
                }
                catch (OperationCanceledException) when (!signal.IsCancellationRequested)
                {
                    throw new TimeoutException($"Request timed out after {options.TimeoutMs}ms");
                }
            }, new ProviderRetryOptions
            {
                MaxRetries = options.MaxRetries ?? 2,
                MaxRetryDelayMs = options.MaxRetryDelayMs,
                Signal = signal,
            }).ConfigureAwait(false);

            if (options.OnResponse is { } onResponse)
            {
                await onResponse(new ProviderResponse
                {
                    Status = (int)response.StatusCode,
                    Headers = response.Headers.ToDictionary(
                        kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase),
                }, model).ConfigureAwait(false);
            }
            var result = transport.Output(body ?? []);
            // 先解析 usage：答案畸形时请求同样已计费。
            var usage = ParseUsage(result["usage"], model);
            output = output with
            {
                Usage = usage,
                Answers = ParseAnswers(transport.Label, result["answers"], context),
            };
            return output;
        }
        catch (Exception error)
        {
            return output with
            {
                StopReason = options?.Signal.IsCancellationRequested == true
                    ? ClassifierStopReason.Aborted
                    : ClassifierStopReason.Error,
                ErrorMessage = ProviderError.Format(
                    ProviderError.Normalize(error), $"{transport.Label} error"),
            };
        }
    }

    /// <summary>公共 bool 问题 → wire 级 noul。对应 TS <c>wireRequest</c>。</summary>
    private static SystemOneWireRequest WireRequest(ClassifierContext context)
    {
        var questions = new JsonObject();
        foreach (var (id, question) in context.Questions)
        {
            // JsonPolymorphic 判别标注由默认序列化器处理。
            var serialized = JsonSerializer.SerializeToNode(question) is JsonObject questionObject
                ? questionObject
                : [];
            if (question.Kind == "bool") serialized["type"] = "noul";
            questions[id] = serialized;
        }
        return new SystemOneWireRequest
        {
            State = context.State.DeepClone(),
            Questions = questions,
        };
    }

    /// <summary>TypeSafe 的实验性图片输入所需的 opt-in 头。</summary>
    private const string ImageOptInHeader = "TypeSafe-Danger-Zone-Images";

    private static readonly Dictionary<string, string> AssetFormats = new(StringComparer.Ordinal)
    {
        ["image/png"] = "png",
        ["image/jpeg"] = "jpeg",
    };

    private static readonly HashSet<string> DataUrlTypes = new(StringComparer.Ordinal)
    {
        "image/png", "image/jpeg", "image/webp",
    };

    /// <summary>Clef 每次请求最多接收的图片数。</summary>
    private const int MaxDataUrlImages = 4;

    /// <summary>按服务的形态把上下文图片加进 <paramref name="request"/>。对应 TS <c>addImages</c>。</summary>
    private static void AddImages(
        string label,
        ModelSpec model,
        SystemOneImageInput input,
        IReadOnlyList<ImageContent> images,
        SystemOneWireRequest request)
    {
        if (input == SystemOneImageInput.Images)
        {
            if (images.Count > MaxDataUrlImages)
            {
                throw new InvalidOperationException(
                    $"{label} accepts at most {MaxDataUrlImages} images for {model.Id}, got {images.Count}");
            }
            request.Images = [.. images.Select(image =>
            {
                var mimeType = image.MimeType ?? string.Empty;
                if (!DataUrlTypes.Contains(mimeType))
                {
                    throw new InvalidOperationException(
                        $"{label} accepts PNG, JPEG, and WebP images for {model.Id}, got {mimeType}");
                }
                return $"data:{mimeType};base64,{image.Data}";
            })];
            return;
        }

        if (input == SystemOneImageInput.StateImageUrls)
        {
            // OpenRouter validates the images against the model that serves the request.
            var parts = new JsonArray();
            foreach (var image in images)
            {
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:{image.MimeType ?? string.Empty};base64,{image.Data}",
                    },
                });
            }
            parts.Add(request.State.DeepClone());
            request.State = parts;
            return;
        }

        var assets = new JsonObject();
        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            if (!AssetFormats.TryGetValue(image.MimeType ?? string.Empty, out var format))
            {
                throw new InvalidOperationException(
                    $"{label} accepts PNG and JPEG images for {model.Id}, got {image.MimeType ?? string.Empty}");
            }
            assets[$"<image-{index + 1}>"] = new JsonObject
            {
                ["type"] = "image",
                ["format"] = format,
                ["data_base64"] = image.Data,
            };
        }
        request.Assets = assets;
        // Every asset must be referenced from the request. The names go before the state, where Clef
        // also places its images.
        var state = new JsonArray();
        foreach (var (name, _) in assets) state.Add(name);
        state.Add(request.State.DeepClone());
        request.State = state;
    }

    /// <summary>读取 <c>compat.imageInput</c> / <c>compat.sendImageOptInHeader</c>，缺省回落到检测值。</summary>
    private static (SystemOneImageInput ImageInput, bool SendImageOptInHeader) ResolveCompat(
        ModelSpec model, SystemOneCompat detected)
    {
        var imageInput = model.Compat?["imageInput"]?.GetValue<string>() switch
        {
            "images" => SystemOneImageInput.Images,
            "state-image-urls" => SystemOneImageInput.StateImageUrls,
            "assets" => SystemOneImageInput.Assets,
            _ => detected.ImageInput,
        };
        var optIn = model.Compat?["sendImageOptInHeader"] is JsonValue value
            && value.TryGetValue<bool>(out var enabled)
                ? enabled
                : detected.SendImageOptInHeader;
        return (imageInput, optIn);
    }

    private static Dictionary<string, string?> RequestHeaders(
        ModelSpec model, string apiKey, IReadOnlyDictionary<string, string?>? optionsHeaders)
    {
        var headers = new Dictionary<string, string?>
        {
            ["authorization"] = $"Bearer {apiKey}",
            ["content-type"] = "application/json",
        };
        if (model.Headers is not null)
        {
            foreach (var (key, value) in model.Headers) headers[key] = value;
        }
        if (optionsHeaders is not null)
        {
            foreach (var (key, value) in optionsHeaders) headers[key] = value;
        }
        return headers;
    }

    private static double RequiredNumber(string label, JsonNode? value, string field)
        => value is JsonValue { } primitive && primitive.TryGetValue<double>(out var number)
            && !double.IsNaN(number) && !double.IsInfinity(number)
            ? number
            : throw new InvalidOperationException($"{label} returned an invalid {field}");

    private static Dictionary<string, double> Probabilities(string label, JsonNode? value, string id)
    {
        if (value is not JsonObject objectValue)
        {
            throw new InvalidOperationException($"{label} returned invalid probabilities for {id}");
        }
        var probabilities = new Dictionary<string, double>();
        foreach (var (key, probability) in objectValue)
        {
            probabilities[key] = RequiredNumber(label, probability, $"probability for {id}.{key}");
        }
        return probabilities;
    }

    /// <summary>按上下文问题集解析答案。对应 TS <c>parseAnswers</c>。</summary>
    public static IReadOnlyDictionary<string, ClassifierAnswer> ParseAnswers(
        string label, JsonNode? value, ClassifierContext context)
    {
        if (value is not JsonObject answersObject)
        {
            throw new InvalidOperationException($"{label} returned an unexpected response");
        }
        var answers = new Dictionary<string, ClassifierAnswer>();
        foreach (var (id, question) in context.Questions)
        {
            if (answersObject[id] is not JsonObject answer)
            {
                throw new InvalidOperationException($"{label} did not return an answer for {id}");
            }
            switch (question.Kind)
            {
                case "choice":
                    if (answer.Str("type") != "choice" || answer.Str("choice") is not { } choice)
                    {
                        throw new InvalidOperationException($"{label} did not return a choice answer for {id}");
                    }
                    answers[id] = new ClassifierChoiceAnswer(
                        choice,
                        Probabilities(label, answer["probabilities"], id),
                        RequiredNumber(label, answer["confidence"], $"confidence for {id}"));
                    break;
                case "score":
                    if (answer.Str("type") != "score")
                    {
                        throw new InvalidOperationException($"{label} did not return a score answer for {id}");
                    }
                    answers[id] = new ClassifierScoreAnswer(
                        RequiredNumber(label, answer["score"], $"score for {id}"),
                        RequiredNumber(label, answer["confidence"], $"confidence for {id}"));
                    break;
                default:
                    // wire 级 noul 映射回公共 bool。
                    if (answer.Str("type") != "noul")
                    {
                        throw new InvalidOperationException($"{label} did not return a bool answer for {id}");
                    }
                    answers[id] = new ClassifierBoolAnswer(
                        RequiredNumber(label, answer["noul"], $"probability for {id}"));
                    break;
            }
        }
        return answers;
    }

    private static long TokenCount(JsonNode? value)
        => value is JsonValue { } primitive && primitive.TryGetValue<double>(out var number)
            && !double.IsNaN(number) && !double.IsInfinity(number) && number > 0
            ? (long)number
            : 0;

    /// <summary>System One 的 {input_tokens, output_tokens} 按目录报价计费；缺失/畸形时返回 null 不失败。对应 TS <c>parseUsage</c>。</summary>
    public static Usage? ParseUsage(JsonNode? value, ModelSpec model)
    {
        if (value is not JsonObject usage || (!usage.Has("input_tokens") && !usage.Has("output_tokens")))
        {
            return null;
        }
        var input = TokenCount(usage["input_tokens"]);
        var output = TokenCount(usage["output_tokens"]);
        // TS：totalTokens = input + output（该 provider 不区分缓存桶）。
        return ModelOperations.CalculateCost(model, new Usage(input, output) { TotalTokens = input + output });
    }
}
