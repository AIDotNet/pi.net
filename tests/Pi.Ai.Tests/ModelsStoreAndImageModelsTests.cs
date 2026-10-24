using Pi.Ai;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>根级模块（models-store.ts / image-models.ts）测试。</summary>
public class ModelsStoreAndImageModelsTests
{
    private static ModelSpec Model(string id, ModelType type = ModelType.Chat)
        => new()
        {
            Id = id,
            Name = id,
            Api = "openai-completions",
            Provider = "p",
            BaseUrl = "https://example.invalid",
            Type = type,
        };

    [Fact]
    public async Task InMemoryStoreRoundTripsAndDeepCopies()
    {
        var store = new InMemoryModelsStore();
        Assert.Null(await store.ReadAsync("radius"));

        var entry = new ModelsStoreEntry
        {
            Models = [Model("r1"), Model("r2", ModelType.Image)],
            CheckedAt = 1_700_000_000_000,
            Etag = "\"abc\"",
        };
        await store.WriteAsync("radius", entry);

        var read = await store.ReadAsync("radius");
        Assert.NotNull(read);
        Assert.Equal(2, read!.Models.Count);
        Assert.Equal("r1", read.Models[0].Id);
        Assert.Equal(ModelType.Image, read.Models[1].Type);
        Assert.Equal(1_700_000_000_000, read.CheckedAt);
        Assert.Equal("\"abc\"", read.Etag);

        // 深拷贝：改写入参不影响已存条目。
        var mutated = entry with { Models = [Model("r3")] };
        Assert.Equal("r3", mutated.Models[0].Id);
        Assert.Equal(2, (await store.ReadAsync("radius"))!.Models.Count);

        await store.DeleteAsync("radius");
        Assert.Null(await store.ReadAsync("radius"));
    }

    [Fact]
    public async Task StoreHonorsCancellation()
    {
        var store = new InMemoryModelsStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync("p", null, cts.Token));
    }

    [Fact]
    public void ImageModelsCompatReadsBuiltinCatalog()
    {
        // 目录取自上游 providers/data：openrouter 带 63 个图片模型。
        Assert.Null(ImageModels.GetImageModel("openrouter", "anything"));
        var flux = ImageModels.GetImageModel("openrouter", "black-forest-labs/flux-3-image");
        Assert.NotNull(flux);
        Assert.Equal("openrouter-images", flux!.Api);
        Assert.NotEmpty(ImageModels.GetImageModels("openrouter"));
        Assert.Contains("openrouter", ImageModels.GetImageProviders());
        Assert.Equal(42, All.GetBuiltinProviders().Count);
    }

    [Fact]
    public void EveryProviderFileProducesDistinctId()
    {
        var providers = All.BuiltinProviders();
        var ids = providers.Select(provider => provider.Id).ToList();
        Assert.Equal(42, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ids, All.GetBuiltinProviders());
        // 每个 provider 都有认证语义（apiKey 或 oauth 至少其一）。
        Assert.All(providers, provider =>
            Assert.True(provider.Auth?.ApiKey is not null || provider.Auth?.OAuth is not null));
    }

    [Fact]
    public void CompatOAuthTypeEntryPointExposesLegacyShapes()
    {
        Assert.Equal(typeof(Pi.Ai.Auth.Credential.OAuth), OAuthCompat.Credentials);
        Assert.Equal(typeof(Pi.Ai.Compat.OAuthPrompt), OAuthCompat.Prompt);
        Assert.Equal(typeof(Pi.Ai.Compat.IOAuthLoginCallbacks), OAuthCompat.LoginCallbacks);
    }
}
