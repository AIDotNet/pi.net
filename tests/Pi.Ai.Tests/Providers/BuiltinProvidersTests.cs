using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests.Providers;

/// <summary>内建 provider 全家桶（42 家）与 all.ts 目录读取测试。</summary>
public class BuiltinProvidersTests
{

    /// <summary>按 id 从内建全集取 provider（对应 TS 各家工厂的调用）。</summary>
    private static IProvider Create(string id) => All.BuiltinProviders().First(provider => provider.Id == id);
    [Fact]
    public void CatalogHasAllFortyTwoProviders()
    {
        var ids = All.GetBuiltinProviders();
        Assert.Equal(42, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ids, All.GetBuiltinProviders());
        Assert.Contains("amazon-bedrock", ids);
        Assert.Contains("radius", ids);
        Assert.Contains("typesafe", ids);
        Assert.Contains("zai-coding-cn", ids);
    }

    [Fact]
    public void EveryProviderConstructsWithMetadataAndAuth()
    {
        foreach (var id in All.GetBuiltinProviders())
        {
            var provider = Create(id);
            Assert.Equal(id, provider.Id);
            Assert.False(string.IsNullOrWhiteSpace(provider.Name));
            Assert.NotNull(provider.Auth);
            // 每个 provider 至少有 api 或 classifiers 之一。
            Assert.True(provider.Auth!.ApiKey is not null || provider.Auth.OAuth is not null);
        }
    }

    [Fact]
    public void KnownBaseUrlsMatchUpstreamDefinitions()
    {
        Assert.Equal("https://api.deepseek.com", Create("deepseek").BaseUrl);
        Assert.Equal("https://api.anthropic.com", Create("anthropic").BaseUrl);
        Assert.Equal("https://api.mistral.ai", Create("mistral").BaseUrl);
        Assert.Equal("https://api.z.ai/api/coding/paas/v4", Create("zai").BaseUrl);
        Assert.Equal("https://open.bigmodel.cn/api/coding/paas/v4", Create("zai-coding-cn").BaseUrl);
        Assert.Equal("https://api.individual.githubcopilot.com", Create("github-copilot").BaseUrl);
        // 无静态 baseUrl 的家（Bedrock / Vertex / Azure 部署名由模型携带）。
        Assert.Null(Create("amazon-bedrock").BaseUrl);
        Assert.Null(Create("google-vertex").BaseUrl);
        Assert.Null(Create("azure").BaseUrl);
    }

    [Fact]
    public void EmbeddedCatalogsAreReadThroughAll()
    {
        // 嵌入资源里有 anthropic / deepseek / openai 三家目录。
        Assert.Equal(2, All.GetBuiltinModels("deepseek").Count);
        Assert.NotNull(All.GetBuiltinModel("deepseek", "deepseek-v4-pro"));
        Assert.Equal("openai-completions", All.GetBuiltinModel("deepseek", "deepseek-v4-pro")!.Api);

        Assert.Equal("anthropic-messages", All.GetBuiltinModel("anthropic", "claude-sonnet-4-5")!.Api);
        Assert.NotNull(All.GetBuiltinModel("openai", "gpt-5"));

        // 未知 provider → 空目录（不是抛错）。
        Assert.Empty(All.GetBuiltinModels("no-such-provider"));
        Assert.Null(All.GetBuiltinModel("no-such-provider", "whatever"));
        // 随包清单带生成时间，用于判断目录新旧。
        Assert.NotNull(All.GetBuiltinModelDataGeneratedAt());
    }

    [Fact]
    public void BuiltinModelsRegistersEveryProvider()
    {
        var models = All.BuiltinModels();
        Assert.Equal(42, models.GetProviders().Count);
        Assert.NotNull(models.GetProvider("deepseek"));
        Assert.NotNull(models.GetProvider("radius"));
        Assert.NotNull(models.GetModel("deepseek", "deepseek-v4-pro"));
    }

    [Fact]
    public async Task ImagesAndClassifierCapabilityDistinguishedByErrorText()
    {
        // deepseek 无 images 映射 → 「does not support image generation」。
        var deepseek = Create("deepseek");
        var deepseekImage = await ((IImagesProvider)deepseek).GenerateImagesAsync(
            new ModelSpec { Id = "i", Name = "i", Api = "openrouter-images", Provider = "deepseek", BaseUrl = "" },
            new ImagesContext { Input = [] }, null, default);
        Assert.Equal(ImagesStopReason.Error, deepseekImage.StopReason);
        Assert.Contains("does not support image generation", deepseekImage.ErrorMessage);

        // openrouter 有 images 映射但 api 不匹配 → 「has no image generation implementation」。
        var openrouter = Create("openrouter");
        var openrouterImage = await ((IImagesProvider)openrouter).GenerateImagesAsync(
            new ModelSpec { Id = "i", Name = "i", Api = "unknown-images", Provider = "openrouter", BaseUrl = "" },
            new ImagesContext { Input = [] }, null, default);
        Assert.Equal(ImagesStopReason.Error, openrouterImage.StopReason);
        Assert.Contains("has no image generation implementation", openrouterImage.ErrorMessage);

        // typesafe 只有分类器：chat 流走「无 api 实现」错误路径。
        var typesafe = Create("typesafe");
        var typesafeStream = await typesafe.StreamSimple(
            new ModelSpec { Id = "c", Name = "c", Api = "typesafe-system-one", Provider = "typesafe", BaseUrl = "" },
            [], null).WaitForDoneAsync();
        Assert.Equal(StopReason.Error, typesafeStream.StopReason);
        Assert.Contains("has no API implementation", typesafeStream.ErrorMessage);
    }

    [Fact]
    public void GitHubCopilotFiltersModelsByCredential()
    {
        var provider = Create("github-copilot");
        var models = new List<ModelSpec>
        {
            new() { Id = "gpt-4o", Name = "GPT-4o", Api = "openai-completions", Provider = "github-copilot", BaseUrl = "" },
            new() { Id = "claude-sonnet", Name = "Claude", Api = "anthropic-messages", Provider = "github-copilot", BaseUrl = "" },
        };

        Assert.Equal(2, provider.FilterModels(models, null).Count);
        var filtered = provider.FilterModels(models,
            new Credential.OAuth("r", "a", long.MaxValue) { AvailableModelIds = ["claude-sonnet"] });
        Assert.Single(filtered);
        Assert.Equal("claude-sonnet", filtered[0].Id);
    }

    [Fact]
    public void RadiusProviderReplacesBaselineWithDynamicCatalog()
    {
        var provider = new RadiusProvider();
        // 默认网关：随包基线目录就在（radius.json 的 32 个 chat 模型）。
        Assert.NotEmpty(provider.GetModels());

        var config = new RadiusGatewayConfig
        {
            BaseUrl = "https://radius.example",
            Models =
            [
                new RadiusGatewayModel
                {
                    Id = "radius-1",
                    Name = "Radius One",
                    Reasoning = true,
                    Input = ["text"],
                    Cost = new ModelCostRates(1, 2),
                    ContextWindow = 128000,
                    MaxTokens = 8192,
                    ThinkingLevelMap = new Dictionary<string, long> { ["high"] = 16384 },
                },
            ],
        };

        var mapped = RadiusProviderConfig.ModelsFromConfig("radius", config);
        Assert.Single(mapped);
        Assert.Equal("pi-messages", mapped[0].Api);
        Assert.Equal("radius", mapped[0].Provider);
        Assert.Equal("https://radius.example", mapped[0].BaseUrl);
        Assert.Equal("16384", mapped[0].ThinkingLevelMap!["high"]);

        // 自定义网关 → 无基线目录。
        var custom = new RadiusProvider(gateway: "https://other.example");
        Assert.Empty(custom.GetModels());
        Assert.Equal("https://other.example", custom.Gateway);
    }

    /// <summary>
    /// 上游 <c>43d376399</c>（R2 漂移）：网关目录**替换**随包基线目录，而不是叠加。
    /// Radius 组织管理员可以停用模型，一旦取到目录，被停用的模型就不该从随包目录里重新冒出来。
    /// </summary>
    [Fact]
    public void RadiusResolveModelsReplacesInsteadOfOverlaying()
    {
        var baseline = new List<ModelSpec>
        {
            Spec("balanced"), Spec("disabled-by-org"),
        };
        var dynamic = new List<ModelSpec> { Spec("balanced"), Spec("organization-only") };

        // 尚未取得目录（null）→ 基线。
        Assert.Equal(["balanced", "disabled-by-org"],
            RadiusProvider.ResolveModels(baseline, null).Select(model => model.Id));

        // 已取得目录 → 只留目录里的条目；基线独有的 "disabled-by-org" 不得回归。
        Assert.Equal(["balanced", "organization-only"],
            RadiusProvider.ResolveModels(baseline, dynamic).Select(model => model.Id));

        // 组织停用了全部模型 → 空目录（不是基线）。
        Assert.Empty(RadiusProvider.ResolveModels(baseline, []));

        static ModelSpec Spec(string id)
            => new() { Id = id, Name = id, Api = "pi-messages", Provider = "radius", BaseUrl = "" };
    }

    [Fact]
    public void ModelsGetAllModelsCoversEveryType()
    {
        var models = new Pi.Ai.Models.Models();
        models.SetProvider(Create("deepseek"));
        var chat = models.GetModels("deepseek");
        Assert.Equal(chat.Count, models.GetAllModels("deepseek").Count);
    }
}
