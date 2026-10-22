using System.Net;
using System.Text;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Ai.Tests.Auth;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests.Providers;

/// <summary>
/// OpenRouter 按 key 的目录个性化测试。对应 TS <c>test/openrouter-user-models.test.ts</c>：
/// <c>GET /models/user</c> 决定该 key 能用哪些 chat 模型，以及它们的限额与价格。
/// </summary>
public class OpenRouterCatalogTests
{
    private static ModelSpec ChatModel(string id) => new()
    {
        Id = id,
        Name = id,
        Api = "openai-completions",
        Provider = "openrouter",
        BaseUrl = "https://openrouter.ai/api/v1",
        Cost = new ModelCostRates(1, 2, 0.1, 0),
        ContextWindow = 200_000,
        MaxTokens = 50_000,
    };

    private static readonly ModelSpec ImageModel = new()
    {
        Id = "image",
        Name = "image",
        Api = "openrouter-images",
        Provider = "openrouter",
        BaseUrl = "https://openrouter.ai/api/v1",
        Type = ModelType.Image,
        Cost = new ModelCostRates(0, 0, 0, 0),
    };

    private static readonly ModelSpec ClassifierModel = new()
    {
        Id = "classifier",
        Name = "classifier",
        Api = "typesafe-system-one",
        Provider = "openrouter",
        BaseUrl = "https://openrouter.ai/api/v1",
        Type = ModelType.Classifier,
        Cost = new ModelCostRates(0, 0, 0, 0),
        ContextWindow = 64_000,
    };

    private static RefreshModelsContext RefreshContext(string? baseUrl = null) => new()
    {
        Credential = new Credential.ApiKey("sk-or-test"),
        Publish = _ => Task.FromResult(true),
        AllowNetwork = true,
        Signal = CancellationToken.None,
        BaseUrl = baseUrl,
    };

    private static HttpClient Client(HttpStatusCode status, string body)
        => new(new FakeHttpHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    private const string UserModelsBody = """
        {"data":[
          {"id":"listed","context_length":1000000,
           "top_provider":{"context_length":262144,"max_completion_tokens":32768},
           "pricing":{"prompt":"0.000003","completion":"0.000015"}},
          {"id":"no-limits","context_length":0,
           "top_provider":{"context_length":null,"max_completion_tokens":null}},
          {"id":"router","pricing":{"prompt":"-1","completion":"-1"}}
        ]}
        """;

    private static readonly IReadOnlyList<ModelSpec> Catalog =
    [
        ChatModel("listed"),
        ChatModel("no-limits"),
        ChatModel("router"),
        ChatModel("blocked"),
        ImageModel,
        ClassifierModel,
    ];

    [Fact]
    public async Task KeepsOnlyTheKeysChatModelsWithTheKeysLimitsAndPrices()
    {
        using var client = Client(HttpStatusCode.OK, UserModelsBody);

        var result = await OpenRouterCatalog.PersonalizeAsync(Catalog, RefreshContext(), client);

        Assert.Equal(
            ["listed", "no-limits", "router", "image", "classifier"],
            result.Select(model => model.Id));
        // top_provider 优先于 context_length。
        Assert.Equal(262_144, result[0].ContextWindow);
        Assert.Equal(32_768, result[0].MaxTokens);
        Assert.Equal(new ModelCostRates(3, 15, 0, 0), result[0].Cost);
        // 缺失限额沿用目录值：生成器针对 OpenRouter 缺数据的修补得以保留。
        Assert.Equal(200_000, result[1].ContextWindow);
        Assert.Equal(50_000, result[1].MaxTokens);
        Assert.Equal(ChatModel("x").Cost, result[1].Cost);
        // 路由模型报 -1：价格取决于它最终选了谁，一律归零。
        Assert.Equal(new ModelCostRates(0, 0, 0, 0), result[2].Cost);
        // /models/user 只列文本模型，图片与分类模型不参与过滤。
        Assert.Same(ImageModel, result[3]);
        Assert.Same(ClassifierModel, result[4]);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task ReturnsTheCatalogUnchangedWhenTheEndpointRejectsTheKeyOrIsMissing(int status)
    {
        // 例如 models.json 里配的代理拒绝该 key，或者根本没有 /models/user：
        // 此时保留完整目录，而不是让刷新整体失败。
        using var client = Client((HttpStatusCode)status, "");

        var catalog = new[] { ChatModel("a") };
        var result = await OpenRouterCatalog.PersonalizeAsync(catalog, RefreshContext(), client);

        // 原样返回同一实例（对应上游 toBe(catalog)），不复制、不过滤。
        Assert.Same(catalog, result);
        Assert.Equal("a", Assert.Single(result).Id);
    }

    [Fact]
    public async Task FollowsTheConfiguredBaseUrl()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(UserModelsBody, Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);

        await OpenRouterCatalog.PersonalizeAsync(Catalog, RefreshContext("https://us.openrouter.ai/api/v1/"), client);

        Assert.Single(handler.Requests);
        Assert.Equal("https://us.openrouter.ai/api/v1/models/user", handler.Requests[0].Url);
        Assert.Equal("Bearer sk-or-test", handler.Requests[0].Headers["authorization"]);
    }

    [Fact]
    public async Task UsesTheOAuthAccessTokenWhenThatIsTheCredential()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(UserModelsBody, Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);
        var context = RefreshContext() with
        {
            Credential = new Credential.OAuth("refresh", "oauth-access", 0),
        };

        await OpenRouterCatalog.PersonalizeAsync(Catalog, context, client);

        Assert.Equal("Bearer oauth-access", handler.Requests[0].Headers["authorization"]);
    }

    [Fact]
    public async Task WithoutACredentialTheCatalogPassesThrough()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(UserModelsBody, Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);

        var result = await OpenRouterCatalog.PersonalizeAsync(Catalog, RefreshContext() with { Credential = null }, client);

        Assert.Equal(Catalog, result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AFailedRequestIsAnErrorAndAnInvalidBodyToo()
    {
        using (var failing = Client(HttpStatusCode.InternalServerError, ""))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => OpenRouterCatalog.PersonalizeAsync(Catalog, RefreshContext(), failing));
            Assert.Contains("500", error.Message, StringComparison.Ordinal);
        }

        using var badBody = Client(HttpStatusCode.OK, """{"models":[]}""");
        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OpenRouterCatalog.PersonalizeAsync(Catalog, RefreshContext(), badBody));
        Assert.Contains("Invalid OpenRouter model list response", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertsPricesAndPromptLengthTiersPromptLength()
    {
        var pricing = new OpenRouterPricing
        {
            Prompt = "0.000003",
            Completion = "0.000015",
            InputCacheRead = "0.0000003",
            Overrides =
            [
                // 按 prompt 长度：成为请求级阶梯。
                new OpenRouterPricingOverride { MinPromptTokens = 200_000, Prompt = "0.000006", Completion = "0.00003" },
                // 按时段：成本模型表达不了，跳过。
                new OpenRouterPricingOverride { MinPromptTokens = 1, UtcStart = 0, UtcEnd = 6 },
                // 没有阈值：不是阶梯。
                new OpenRouterPricingOverride { Prompt = "0.000001" },
            ],
        };

        var cost = OpenRouterCatalog.OpenRouterCost(pricing);

        Assert.Equal(3, cost.Input);
        Assert.Equal(15, cost.Output);
        Assert.Equal(0.3, cost.CacheRead);
        Assert.Equal(0, cost.CacheWrite);
        var tier = Assert.Single(cost.Tiers!);
        Assert.Equal(200_000, tier.InputTokensAbove);
        Assert.Equal(6, tier.Input);
        Assert.Equal(30, tier.Output);
        // 阶梯没列的费率沿用基础价。
        Assert.Equal(0.3, tier.CacheRead);
        Assert.Equal(0, tier.CacheWrite);
    }
}
