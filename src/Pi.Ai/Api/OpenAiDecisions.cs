using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>
/// OpenAI Decisions API：<c>POST /v1/decisions</c>，请求体 <c>{ model, input, questions }</c>。
/// 对应 TS <c>api/openai-decisions.ts</c>。
/// <para>状态以 JSON 文本发送。带图片时 input 变成一条 user 消息：<c>input_text</c> 后跟若干
/// <c>input_image</c> data URL。问题映射到 Decisions 类型：<c>choice</c> → <c>choice</c>、
/// <c>score</c> → <c>score</c>、<c>bool</c> → <c>predicate</c>。predicate 没有 criteria 字段，
/// 因此 true/false 的含义追加到 instructions 里。</para>
/// <para>只接受 OpenAI API key：Sign in with ChatGPT 的 token 在这条路由上会被拒绝。</para>
/// </summary>
public static class OpenAiDecisions
{
    /// <summary>分类器 API 名。</summary>
    public const string ApiName = "openai-decisions";

    private const string Label = "OpenAI Decisions";

    /// <summary>端点每次请求接受的图片上限。</summary>
    private const int MaxImages = 128;

    /// <summary>
    /// api.openai.com 前的 Cloudflare 在请求超过约 5 秒时以 HTML 页回 504。超大输入（目前约
    /// 60 万 token 以上）会撞上这条时间上限，重试同一份输入只会再撞一次，故 504 不重试。
    /// </summary>
    private static readonly int[] NoRetryStatuses = [504];

    /// <summary>与 TS <c>JSON.stringify</c> 一致：紧凑、不转义非 ASCII。</summary>
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>分类入口。对应 TS <c>classify</c>。</summary>
    public static async Task<ClassifierResult> ClassifyAsync(
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
            if (model.Api != ApiName)
            {
                throw new InvalidOperationException($"Unsupported classifier API: {model.Api}");
            }
            if (string.IsNullOrEmpty(options?.ApiKey))
            {
                throw new InvalidOperationException($"No API key for provider: {model.Provider}");
            }
            var apiKey = options.ApiKey;
            var payload = Payload(model, context);
            if (options.OnPayload is { } onPayload)
            {
                var transformed = await onPayload(payload, model).ConfigureAwait(false);
                if (transformed is not null) payload = transformed;
            }

            var client = httpClient ?? OAuthHttp.Shared;
            var signal = options.Signal;
            HttpResponseMessage response;
            JsonNode? body;
            (response, body) = await ProviderRetry.RetryAsync(async () =>
            {
                using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(signal, cancellationToken);
                if (options.TimeoutMs is { } timeout) perCallCts.CancelAfter(timeout);
                using var request = new HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, RequestUrl(model))
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                foreach (var (key, value) in RequestHeaders(model, apiKey, options.Headers, options.Identity))
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
                    return (httpResponse, JsonNode.Parse(text));
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
                NoRetryStatuses = NoRetryStatuses,
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

            if (body is not JsonObject bodyObject)
            {
                throw new InvalidOperationException($"{Label} returned an unexpected response");
            }
            // 先解析 usage：答案畸形或被拒的请求同样已计费。
            var usage = SystemOne.ParseUsage(bodyObject["usage"], model);
            output = output with
            {
                Usage = usage,
                Answers = ParseAnswers(bodyObject["answers"], context),
            };
            return output;
        }
        catch (Exception error)
        {
            return output with
            {
                Answers = new Dictionary<string, ClassifierAnswer>(),
                StopReason = options?.Signal.IsCancellationRequested == true
                    ? ClassifierStopReason.Aborted
                    : ClassifierStopReason.Error,
                ErrorMessage = ErrorMessage(error),
            };
        }
    }

    /// <summary>请求 URL：baseUrl 去掉尾部斜杠后追加 <c>/decisions</c>。对应 TS 的 <c>new URL("decisions", base)</c>。</summary>
    public static Uri RequestUrl(ModelSpec model)
        => new($"{model.BaseUrl.TrimEnd('/')}/decisions");

    private static JsonObject Payload(ModelSpec model, ClassifierContext context)
    {
        var questions = new JsonArray();
        foreach (var (id, question) in context.Questions)
        {
            questions.Add(WireQuestion(id, question));
        }
        return new JsonObject
        {
            ["model"] = model.Id,
            ["input"] = WireInput(context),
            ["questions"] = questions,
        };
    }

    /// <summary>把状态（必要时连同图片）写成 Decisions 的 <c>input</c>。对应 TS <c>wireInput</c>。</summary>
    public static JsonNode WireInput(ClassifierContext context)
    {
        var state = context.State.ToJsonString(StateJsonOptions);
        var images = context.Images ?? [];
        if (images.Count == 0) return JsonValue.Create(state);
        if (images.Count > MaxImages)
        {
            throw new InvalidOperationException(
                $"{Label} accepts at most {MaxImages} images, got {images.Count}");
        }
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = state,
            },
        };
        foreach (var image in images)
        {
            content.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = $"data:{image.MimeType ?? string.Empty};base64,{image.Data}",
            });
        }
        return new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = content,
            },
        };
    }

    /// <summary>把一道问题写成 Decisions 的问题对象。对应 TS <c>wireQuestion</c>。</summary>
    public static JsonObject WireQuestion(string name, ClassifierQuestion question)
    {
        switch (question)
        {
            case ClassifierChoiceQuestion choice:
            {
                var choices = new JsonArray();
                foreach (var (value, description) in choice.Criteria)
                {
                    choices.Add(string.IsNullOrEmpty(description)
                        ? new JsonObject { ["value"] = value }
                        : new JsonObject { ["value"] = value, ["description"] = description });
                }
                return new JsonObject
                {
                    ["type"] = "choice",
                    ["name"] = name,
                    ["instructions"] = choice.Instructions,
                    ["choices"] = choices,
                };
            }
            case ClassifierScoreQuestion score:
            {
                var levels = new JsonArray();
                foreach (var level in score.Criteria) levels.Add(new JsonObject { ["label"] = level });
                return new JsonObject
                {
                    ["type"] = "score",
                    ["name"] = name,
                    ["instructions"] = score.Instructions,
                    ["levels"] = levels,
                };
            }
            default:
            {
                var boolean = (ClassifierBoolQuestion)question;
                return new JsonObject
                {
                    ["type"] = "predicate",
                    ["name"] = name,
                    ["instructions"] = PredicateInstructions(boolean),
                };
            }
        }
    }

    /// <summary>predicate 没有 criteria 字段，true/false 的含义并进 instructions。对应 TS <c>predicateInstructions</c>。</summary>
    private static string PredicateInstructions(ClassifierBoolQuestion question)
    {
        var meanings = new List<string>();
        if (question.TrueMeaning.Length > 0) meanings.Add($"True means: {question.TrueMeaning}");
        if (question.FalseMeaning.Length > 0) meanings.Add($"False means: {question.FalseMeaning}");
        return meanings.Count > 0
            ? $"{question.Instructions}\n\n{string.Join("\n", meanings)}"
            : question.Instructions;
    }

    /// <summary>答案列表（按 name 索引）还原成上下文的答案表。对应 TS <c>parseAnswers</c>。</summary>
    public static IReadOnlyDictionary<string, ClassifierAnswer> ParseAnswers(
        JsonNode? value, ClassifierContext context)
    {
        if (value is not JsonArray array)
        {
            throw new InvalidOperationException($"{Label} returned an unexpected response");
        }
        var byName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var entry in array)
        {
            if (entry is JsonObject answer && answer.Str("name") is { } name) byName[name] = answer;
        }
        var answers = new Dictionary<string, ClassifierAnswer>();
        foreach (var (id, question) in context.Questions)
        {
            if (!byName.TryGetValue(id, out var answer))
            {
                throw new InvalidOperationException($"{Label} did not return an answer for {id}");
            }
            answers[id] = ParseAnswer(id, question, answer);
        }
        return answers;
    }

    private static ClassifierAnswer ParseAnswer(string id, ClassifierQuestion question, JsonObject answer)
    {
        var kind = answer.Str("type");
        if (kind == "refusal") throw new InvalidOperationException($"{Label} refused to answer {id}");
        switch (question)
        {
            case ClassifierChoiceQuestion:
            {
                if (kind != "choice" || answer.Str("choice") is not { } choice)
                {
                    throw new InvalidOperationException($"{Label} did not return a choice answer for {id}");
                }
                return new ClassifierChoiceAnswer(
                    choice,
                    ChoiceProbabilities(answer["probabilities"], id),
                    RequiredNumber(answer["confidence"], $"confidence for {id}"));
            }
            case ClassifierScoreQuestion:
            {
                if (kind != "score")
                {
                    throw new InvalidOperationException($"{Label} did not return a score answer for {id}");
                }
                return new ClassifierScoreAnswer(
                    RequiredNumber(answer["score"], $"score for {id}"),
                    RequiredNumber(answer["confidence"], $"confidence for {id}"));
            }
            default:
            {
                if (kind != "predicate")
                {
                    throw new InvalidOperationException($"{Label} did not return a predicate answer for {id}");
                }
                return new ClassifierBoolAnswer(RequiredNumber(answer["probability"], $"probability for {id}"));
            }
        }
    }

    /// <summary>Decisions 的概率是 <c>[{value, probability}]</c> 数组而非对象。对应 TS <c>choiceProbabilities</c>。</summary>
    private static Dictionary<string, double> ChoiceProbabilities(JsonNode? value, string id)
    {
        if (value is not JsonArray array)
        {
            throw new InvalidOperationException($"{Label} returned invalid probabilities for {id}");
        }
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var entry in array)
        {
            if (entry is not JsonObject item || item.Str("value") is not { } choice)
            {
                throw new InvalidOperationException($"{Label} returned invalid probabilities for {id}");
            }
            probabilities[choice] = RequiredNumber(item["probability"], $"probability for {id}.{choice}");
        }
        return probabilities;
    }

    private static double RequiredNumber(JsonNode? value, string field)
        => value is JsonValue { } primitive && primitive.TryGetValue<double>(out var number)
            && !double.IsNaN(number) && !double.IsInfinity(number)
            ? number
            : throw new InvalidOperationException($"{Label} returned an invalid {field}");

    /// <summary>
    /// 身份头 → 认证头 → 模型头 → 调用方头，后者覆盖前者。对应 TS <c>requestHeaders</c>
    /// （<c>providerHeadersToRecord</c> 的合并次序）。
    /// </summary>
    private static Dictionary<string, string?> RequestHeaders(
        ModelSpec model,
        string apiKey,
        IReadOnlyDictionary<string, string?>? optionsHeaders,
        ClientIdentity? identity)
    {
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Identity.GetIdentityHeaders(Identity.TargetOf(model), identity))
        {
            headers[key] = value;
        }
        headers["authorization"] = $"Bearer {apiKey}";
        headers["content-type"] = "application/json";
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

    /// <summary>504 给出固定的超时文案；其余走通用的 provider 错误格式化。对应 TS <c>errorMessage</c>。</summary>
    private static string ErrorMessage(Exception error)
    {
        if (error is ProviderHttpException { Status: 504 })
        {
            return $"{Label} error (504): the request timed out at the gateway. " +
                "Very large inputs (above roughly 600K tokens) currently exceed its time limit.";
        }
        return ProviderError.Format(ProviderError.Normalize(error), $"{Label} error");
    }
}
