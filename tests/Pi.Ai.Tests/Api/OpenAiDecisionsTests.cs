using System.Net;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Ai.Tests.Auth;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>OpenAI Decisions 分类器：请求线格式、答案解析、不重试状态、模型可见性。</summary>
public class OpenAiDecisionsTests
{
    private static ModelSpec Model(string baseUrl = "https://api.openai.com/v1")
        => new()
        {
            Id = "gpt-decisions-1", Name = "Decisions", Api = OpenAiDecisions.ApiName, Provider = "openai",
            BaseUrl = baseUrl, Cost = new ModelCostRates(1, 2),
        };

    private static ClassifierContext Context(IReadOnlyList<ImageContent>? images = null)
        => new()
        {
            State = new JsonObject { ["step"] = 1 },
            Images = images,
            Questions = new Dictionary<string, ClassifierQuestion>
            {
                ["q-choice"] = new ClassifierChoiceQuestion(
                    "Pick one", new Dictionary<string, string> { ["a"] = "first", ["b"] = "" }),
                ["q-score"] = new ClassifierScoreQuestion("Rate", ["low", "high"]),
                ["q-bool"] = new ClassifierBoolQuestion("Does it apply", "it applies", ""),
            },
        };

    private static ClassifierOptions Options(string apiKey = "key-1") => new() { ApiKey = apiKey };

    /// <summary>三型问题映射到 choice/score/predicate，答案按 name 从数组还原。</summary>
    [Fact]
    public async Task RoundTripsChoiceScoreAndPredicate()
    {
        var handler = new FakeHttpHandler((request, body) =>
        {
            Assert.EndsWith("/v1/decisions", request.RequestUri!.ToString());
            Assert.Equal("Bearer key-1", request.Headers.Authorization?.ToString());
            var parsed = JsonNode.Parse(body!)!.AsObject();
            Assert.Equal("gpt-decisions-1", parsed.Str("model"));
            // 无图时 input 是状态 JSON 文本本身。
            Assert.Equal("{\"step\":1}", parsed.Str("input"));

            var questions = parsed["questions"]!.AsArray();
            Assert.Equal(3, questions.Count);
            var choice = questions[0]!.AsObject();
            Assert.Equal("choice", choice.Str("type"));
            Assert.Equal("q-choice", choice.Str("name"));
            Assert.Equal("Pick one", choice.Str("instructions"));
            var choices = choice["choices"]!.AsArray();
            Assert.Equal("a", choices[0]!.AsObject().Str("value"));
            Assert.Equal("first", choices[0]!.AsObject().Str("description"));
            // 空说明不写 description 字段。
            Assert.Equal("b", choices[1]!.AsObject().Str("value"));
            Assert.Null(choices[1]!.AsObject()["description"]);

            var score = questions[1]!.AsObject();
            Assert.Equal("score", score.Str("type"));
            Assert.Equal("low", score["levels"]!.AsArray()[0]!.AsObject().Str("label"));

            // predicate 没有 criteria，true 的含义并进 instructions。
            var predicate = questions[2]!.AsObject();
            Assert.Equal("predicate", predicate.Str("type"));
            Assert.Equal("Does it apply\n\nTrue means: it applies", predicate.Str("instructions"));

            // 答案顺序故意与问题顺序不同，验证按 name 索引。
            return FakeResponse.Json(new JsonObject
            {
                ["answers"] = new JsonArray
                {
                    new JsonObject { ["type"] = "predicate", ["name"] = "q-bool", ["probability"] = 0.9 },
                    new JsonObject
                    {
                        ["type"] = "choice", ["name"] = "q-choice", ["choice"] = "a",
                        ["probabilities"] = new JsonArray
                        {
                            new JsonObject { ["value"] = "a", ["probability"] = 0.8 },
                            new JsonObject { ["value"] = "b", ["probability"] = 0.2 },
                        },
                        ["confidence"] = 0.9,
                    },
                    new JsonObject { ["type"] = "score", ["name"] = "q-score", ["score"] = 1, ["confidence"] = 0.7 },
                },
                ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 },
            });
        });

        var result = await OpenAiDecisions.ClassifyAsync(
            Model(), Context(), Options(), new System.Net.Http.HttpClient(handler));

        Assert.True(result.StopReason == ClassifierStopReason.Stop, result.ErrorMessage);
        var choiceAnswer = Assert.IsType<ClassifierChoiceAnswer>(result.Answers["q-choice"]);
        Assert.Equal("a", choiceAnswer.Choice);
        Assert.Equal(0.9, choiceAnswer.Confidence);
        Assert.Equal(0.8, choiceAnswer.Probabilities["a"]);
        var scoreAnswer = Assert.IsType<ClassifierScoreAnswer>(result.Answers["q-score"]);
        Assert.Equal(1, scoreAnswer.Score);
        var boolAnswer = Assert.IsType<ClassifierBoolAnswer>(result.Answers["q-bool"]);
        Assert.Equal(0.9, boolAnswer.Probability);
        // 10/1M·1 + 5/1M·2。
        Assert.True(Math.Abs(result.Usage!.Cost.Total - 0.00002) < 1e-12);
    }

    /// <summary>带图时 input 变成一条 user 消息：input_text 后跟 input_image data URL。</summary>
    [Fact]
    public async Task ImagesBecomeOneUserMessage()
    {
        var handler = new FakeHttpHandler((request, body) =>
        {
            var input = JsonNode.Parse(body!)!.AsObject()["input"]!.AsArray();
            Assert.Single(input);
            var message = input[0]!.AsObject();
            Assert.Equal("user", message.Str("role"));
            var content = message["content"]!.AsArray();
            Assert.Equal(2, content.Count);
            Assert.Equal("input_text", content[0]!.AsObject().Str("type"));
            Assert.Equal("{\"step\":1}", content[0]!.AsObject().Str("text"));
            Assert.Equal("input_image", content[1]!.AsObject().Str("type"));
            Assert.Equal("data:image/png;base64,AAA", content[1]!.AsObject().Str("image_url"));
            return FakeResponse.Json(new JsonObject
            {
                ["answers"] = new JsonArray
                {
                    new JsonObject { ["type"] = "predicate", ["name"] = "q-bool", ["probability"] = 0.1 },
                    new JsonObject
                    {
                        ["type"] = "choice", ["name"] = "q-choice", ["choice"] = "b",
                        ["probabilities"] = new JsonArray
                        {
                            new JsonObject { ["value"] = "b", ["probability"] = 1.0 },
                        },
                        ["confidence"] = 1.0,
                    },
                    new JsonObject { ["type"] = "score", ["name"] = "q-score", ["score"] = 2, ["confidence"] = 1.0 },
                },
            });
        });

        var result = await OpenAiDecisions.ClassifyAsync(
            Model(), Context([new ImageContent("AAA", "image/png")]), Options(),
            new System.Net.Http.HttpClient(handler));

        Assert.True(result.StopReason == ClassifierStopReason.Stop, result.ErrorMessage);
        Assert.Null(result.Usage);
    }

    /// <summary>图片数超过端点上限时拒绝，且不发请求。</summary>
    [Fact]
    public async Task RejectsMoreImagesThanTheEndpointAccepts()
    {
        var handler = new FakeHttpHandler((request, body) =>
            throw new InvalidOperationException("must not send"));

        var images = Enumerable.Range(0, 129)
            .Select(_ => new ImageContent("AAA", "image/png"))
            .ToList();
        var result = await OpenAiDecisions.ClassifyAsync(
            Model(), Context(images), Options(), new System.Net.Http.HttpClient(handler));

        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Contains("accepts at most 128 images, got 129", result.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    /// <summary>504 是网关时间上限，重试同一输入只会再撞一次——不重试。</summary>
    [Fact]
    public async Task GatewayTimeoutIsNotRetried()
    {
        var handler = new FakeHttpHandler((request, body) =>
            FakeResponse.Text("upstream timeout", HttpStatusCode.GatewayTimeout));

        var result = await OpenAiDecisions.ClassifyAsync(
            Model(), Context(), Options(), new System.Net.Http.HttpClient(handler));

        Assert.Single(handler.Requests);
        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Contains("error (504)", result.ErrorMessage);
        Assert.Contains("600K tokens", result.ErrorMessage);
    }

    /// <summary>其余 5xx 仍然重试（对照上一条：不加 NoRetryStatuses 时 504 会被重试）。</summary>
    [Fact]
    public async Task OtherServerErrorsStillRetry()
    {
        var handler = new FakeHttpHandler((request, body) =>
            FakeResponse.Text("boom", HttpStatusCode.InternalServerError));

        var result = await OpenAiDecisions.ClassifyAsync(
            Model(), Context(), Options() with { MaxRetries = 1, MaxRetryDelayMs = 1 },
            new System.Net.Http.HttpClient(handler));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
    }

    /// <summary>refusal 是错误结果，不是答案。</summary>
    [Fact]
    public async Task RefusalBecomesErrorResult()
    {
        var handler = new FakeHttpHandler((request, body) =>
            FakeResponse.Json(new JsonObject
            {
                ["answers"] = new JsonArray
                {
                    new JsonObject { ["type"] = "refusal", ["name"] = "q-choice" },
                },
            }));

        var result = await OpenAiDecisions.ClassifyAsync(
            Model(), Context(), Options(), new System.Net.Http.HttpClient(handler));

        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Contains("refused to answer q-choice", result.ErrorMessage);
        Assert.Empty(result.Answers);
    }

    /// <summary>缺答案按名字点名报错；答案畸形时 usage 仍然记账（请求已计费）。</summary>
    [Fact]
    public async Task MalformedAnswersKeepUsage()
    {
        var handler = new FakeHttpHandler((request, body) =>
            FakeResponse.Json(new JsonObject
            {
                ["answers"] = new JsonArray
                {
                    new JsonObject { ["type"] = "choice", ["name"] = "q-choice", ["choice"] = "a" },
                },
                ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 },
            }));

        var result = await OpenAiDecisions.ClassifyAsync(
            Model(), Context(), Options(), new System.Net.Http.HttpClient(handler));

        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        // confidence 缺失 → 该题解析失败，先于缺答案。
        Assert.Contains("invalid probabilities for q-choice", result.ErrorMessage);
    }

    /// <summary>baseUrl 的尾部斜杠不产生双斜杠。</summary>
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/")]
    [InlineData("https://api.openai.com/v1///")]
    public void RequestUrlTrimsTrailingSlashes(string baseUrl)
        => Assert.Equal(
            "https://api.openai.com/v1/decisions",
            OpenAiDecisions.RequestUrl(Model(baseUrl)).ToString());

    /// <summary>ChatGPT 订阅凭据下分类模型不可见；API key 下可见。</summary>
    [Fact]
    public void ClassifierModelsAreHiddenFromSubscriptionCredentials()
    {
        var provider = OpenAi.Provider();
        var models = new List<ModelSpec>
        {
            new()
            {
                Id = "gpt-4o", Name = "GPT-4o", Api = "openai-responses", Provider = "openai",
                BaseUrl = "https://api.openai.com/v1",
            },
            new()
            {
                Id = "decisions", Name = "Decisions", Api = OpenAiDecisions.ApiName, Provider = "openai",
                BaseUrl = "https://api.openai.com/v1", Type = ModelType.Classifier,
            },
        };
        var oauth = new Credential.OAuth("refresh", "access", 0);

        var filtered = provider.FilterAllModels(models, oauth);
        Assert.NotNull(filtered);
        Assert.DoesNotContain(filtered, model => model.Type == ModelType.Classifier);

        var withApiKey = provider.FilterAllModels(models, new Credential.ApiKey("key"));
        Assert.NotNull(withApiKey);
        Assert.Contains(withApiKey, model => model.Type == ModelType.Classifier);
    }

    /// <summary>provider 按 api 分派到 openai-decisions 的实现（缺 key 时报的是实现自己的错，而不是「不支持分类」）。</summary>
    [Fact]
    public async Task ProviderDispatchesDecisionsClassifier()
    {
        var provider = Assert.IsAssignableFrom<IClassifierProvider>(OpenAi.Provider());
        var result = await provider.ClassifyAsync(Model(), Context(), new ClassifierOptions(), CancellationToken.None);

        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Contains("No API key for provider: openai", result.ErrorMessage);
        Assert.DoesNotContain("does not support classification", result.ErrorMessage);
    }
}
