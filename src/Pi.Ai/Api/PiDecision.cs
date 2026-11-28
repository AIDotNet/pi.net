using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>
/// pi-decision API：把 pi 自己的决策协议直连后端——单次 POST <c>{ model, context, options }</c>
/// 到 <c>&lt;baseUrl&gt;/decision</c>，200 响应是 <c>{ answers, usage?, responseId? }</c>。
/// 对应 TS <c>api/pi-decision.ts</c>（v1.6.1 新增）。
/// </summary>
public static class PiDecision
{
    /// <summary>分类器 API 名。</summary>
    public const string ApiName = "pi-decision";

    private const string Label = "Pi Decision";

    /// <summary>分类入口。对应 TS <c>classify</c>。</summary>
    public static async Task<ClassifierResult> ClassifyAsync(
        ModelSpec model, ClassifierContext context, ClassifierOptions? options,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var output = new ClassifierResult
        {
            Api = model.Api,
            Provider = model.Provider,
            Model = model.Id,
            Answers = new Dictionary<string, ClassifierAnswer>(),
            StopReason = ClassifierStopReason.Stop,
            Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        };

        try
        {
            if (model.Api != ApiName)
            {
                throw new InvalidOperationException($"Unsupported classifier API: {model.Api}");
            }
            if (context.Images is { Count: > 0 } && !model.Input.Contains("image"))
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
                    HttpMethod.Post, $"{model.BaseUrl.TrimEnd('/')}/decision")
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

            if (body.Str("responseId") is { Length: > 0 } responseId)
            {
                output = output with { ResponseId = responseId };
            }
            if (PiImage.ParseUsage(body.Obj("usage"), model) is { } usage)
            {
                output = output with { Usage = usage };
            }
            return output with { Answers = ParseAnswers(body.Obj("answers"), context) };
        }
        catch (Exception error)
        {
            return output with
            {
                Answers = new Dictionary<string, ClassifierAnswer>(),
                StopReason = options is { Signal.IsCancellationRequested: true }
                    ? ClassifierStopReason.Aborted
                    : ClassifierStopReason.Error,
                ErrorMessage = ProviderError.Format(ProviderError.Normalize(error)),
            };
        }
    }

    /// <summary>请求体。对应 TS <c>PiDecisionRequest</c>。</summary>
    private static JsonObject Payload(ModelSpec model, ClassifierContext context, ClassifierOptions? options)
    {
        var questions = new JsonObject();
        foreach (var (id, question) in context.Questions) questions[id] = WireQuestion(question);
        var wireContext = new JsonObject
        {
            ["state"] = context.State.DeepClone(),
            ["questions"] = questions,
        };
        if (context.Images is { Count: > 0 })
        {
            wireContext["images"] = PiImage.WireContent(context.Images);
        }
        var payload = new JsonObject
        {
            ["model"] = model.Id,
            ["context"] = wireContext,
        };
        if (options?.SessionId is { } sessionId)
        {
            payload["options"] = new JsonObject { ["sessionId"] = sessionId };
        }
        return payload;
    }

    /// <summary>问题 → 线路 JSON（TS <c>ClassifierQuestion</c> 的字段形状）。</summary>
    private static JsonObject WireQuestion(ClassifierQuestion question) => question switch
    {
        ClassifierChoiceQuestion choice => new JsonObject
        {
            ["type"] = "choice", ["instructions"] = choice.Question,
            ["criteria"] = new JsonObject(choice.Criteria.Select(pair =>
                new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value)))),
        },
        ClassifierScoreQuestion score => new JsonObject
        {
            ["type"] = "score", ["instructions"] = score.Question,
            ["criteria"] = new JsonArray(score.Criteria.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        },
        ClassifierBoolQuestion boolean => new JsonObject
        {
            ["type"] = "bool", ["instructions"] = boolean.Question,
            ["criteria"] = new JsonObject { ["true"] = boolean.TrueMeaning, ["false"] = boolean.FalseMeaning },
        },
        _ => throw new InvalidOperationException($"{Label} cannot send question {question.GetType().Name}"),
    };

    /// <summary>答案解析。对应 TS <c>parseAnswers</c>。</summary>
    private static Dictionary<string, ClassifierAnswer> ParseAnswers(JsonObject? answers, ClassifierContext context)
    {
        if (answers is null)
        {
            throw new InvalidOperationException($"{Label} returned an unexpected response");
        }
        var result = new Dictionary<string, ClassifierAnswer>();
        foreach (var (id, question) in context.Questions)
        {
            if (answers[id] is not JsonObject answer)
            {
                throw new InvalidOperationException($"{Label} did not return an answer for {id}");
            }
            result[id] = question switch
            {
                ClassifierChoiceQuestion choice => ParseChoice(id, answer, choice),
                ClassifierScoreQuestion score => ParseScore(id, answer, score),
                ClassifierBoolQuestion => new ClassifierBoolAnswer(UnitInterval(answer, "probability", id)),
                _ => throw new InvalidOperationException($"{Label} returned an invalid answer for {id}"),
            };
        }
        return result;
    }

    private static ClassifierChoiceAnswer ParseChoice(string id, JsonObject answer, ClassifierChoiceQuestion question)
    {
        if (answer.Str("type") != "choice" || answer.Str("choice") is not { } choice)
        {
            throw new InvalidOperationException($"{Label} did not return a choice answer for {id}");
        }
        if (!question.Criteria.ContainsKey(choice))
        {
            throw new InvalidOperationException($"{Label} returned unknown choice \"{choice}\" for {id}");
        }
        var probabilities = new Dictionary<string, double>();
        if (answer.Obj("probabilities") is { } rawProbabilities)
        {
            foreach (var (key, value) in rawProbabilities)
            {
                if (!question.Criteria.ContainsKey(key))
                {
                    throw new InvalidOperationException($"{Label} returned unknown choice \"{key}\" in probabilities for {id}");
                }
                probabilities[key] = UnitIntervalValue(value, $"probability for {id}.{key}");
            }
        }
        return new ClassifierChoiceAnswer(choice, probabilities, UnitInterval(answer, "confidence", id));
    }

    private static ClassifierScoreAnswer ParseScore(string id, JsonObject answer, ClassifierScoreQuestion question)
    {
        if (answer.Str("type") != "score")
        {
            throw new InvalidOperationException($"{Label} did not return a score answer for {id}");
        }
        var score = answer.Num("score") ?? throw new InvalidOperationException($"{Label} returned invalid score for {id}");
        var maxScore = Math.Max(0, question.Criteria.Count - 1);
        if (score < 0 || score > maxScore)
        {
            throw new InvalidOperationException($"{Label} returned out-of-range score for {id}: {score}");
        }
        return new ClassifierScoreAnswer(score, UnitInterval(answer, "confidence", id));
    }

    private static double UnitInterval(JsonObject answer, string field, string id)
        => UnitIntervalValue(answer[field], $"{field} for {id}");

    private static double UnitIntervalValue(JsonNode? value, string field)
    {
        var number = value is JsonValue primitive && primitive.TryGetValue<double>(out var d) ? d
            : value is JsonValue p2 && p2.TryGetValue<long>(out var l) ? l
            : throw new InvalidOperationException($"{Label} returned invalid {field}");
        if (number < 0 || number > 1)
        {
            throw new InvalidOperationException($"{Label} returned out-of-range {field}: {number}");
        }
        return number;
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
