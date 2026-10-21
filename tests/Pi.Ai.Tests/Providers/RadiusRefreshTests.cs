using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests.Providers;

/// <summary>
/// RadiusProvider 刷新钩子接入 ModelsStore 管道的测试。对应 TS <c>radius.ts</c> 的
/// <c>refreshModels</c> 三分支：存储快照恢复 / legacy 凭据缓存导入落库 / 网络刷新无条件覆盖。
/// </summary>
public class RadiusRefreshTests
{
    /// <summary>构造一个非默认网关的 provider（基线目录为空，替换语义可直接观察）。</summary>
    private static RadiusProvider CreateProvider(string? gateway = "https://gateway.example.test",
        HttpMessageHandler? handler = null)
        => new("radius", "Radius", gateway, handler is null ? null : new HttpClient(handler));

    private static ModelSpec Model(string id, string provider = "radius") => new()
    {
        Id = id,
        Name = id,
        Api = "pi-messages",
        Provider = provider,
        BaseUrl = "https://api.example.test",
    };

    /// <summary>捕获发布的上下文工厂；<paramref name="accept"/> 为 false 时模拟发布被更新的刷新取代。</summary>
    private static (RefreshModelsContext Context, List<ModelsPublication> Published) Context(
        Credential? credential = null, ModelsStoreEntry? stored = null, bool allowNetwork = false,
        bool accept = true)
    {
        var published = new List<ModelsPublication>();
        var context = new RefreshModelsContext
        {
            Credential = credential,
            Stored = stored,
            Publish = publication =>
            {
                published.Add(publication);
                return Task.FromResult(accept);
            },
            AllowNetwork = allowNetwork,
            Signal = default,
        };
        return (context, published);
    }

    // ------------------------------------------------------------------ 分支 1：存储快照恢复

    [Fact]
    public async Task RefreshModels_RestoresStoredCatalogForOwnProviderOnly()
    {
        var provider = CreateProvider();
        var stored = new ModelsStoreEntry
        {
            Models =
            [
                Model("gw-a"),
                Model("gw-b"),
                Model("foreign", provider: "other"),
            ],
            CheckedAt = 1000,
        };
        var (context, published) = Context(stored: stored);

        await provider.RefreshModels!(context);

        // 只更新内存，不写存储（存储已持有这份目录，TS 的 publish({ update }) 不带 persist）。
        var publication = Assert.Single(published);
        Assert.Null(publication.Persist);
        Assert.False(publication.PersistDeleted);
        publication.Update!();

        // 只恢复本 provider 的条目。
        Assert.Equal(["gw-a", "gw-b"], provider.GetModels().Select(model => model.Id).ToList());
    }

    [Fact]
    public async Task RefreshModels_PublishRejectedAfterRestoreStopsRefresh()
    {
        var handler = new StubConfigHandler("""{"baseUrl":"https://api.example.test","models":[]}""");
        var provider = CreateProvider(handler: handler);
        var stored = new ModelsStoreEntry { Models = [Model("gw-a")], CheckedAt = 1000 };
        var (context, published) = Context(stored: stored, accept: false);

        await provider.RefreshModels!(context);

        Assert.Single(published);
        Assert.Equal(0, handler.RequestCount); // 发布被取代后不再走网络（TS 同分支 return）。
    }

    // ------------------------------------------------------------------ 分支 2：legacy 凭据缓存导入

    [Fact]
    public async Task RefreshModels_LegacyOAuthCacheIsImportedAndPersisted()
    {
        var provider = CreateProvider();
        var oauth = new Credential.OAuth("refresh", "access", long.MaxValue)
        {
            GatewayConfig = new RadiusGatewayConfig
            {
                BaseUrl = "https://api.example.test",
                Models =
                [
                    new RadiusGatewayModel
                    {
                        Id = "legacy-1",
                        Name = "Legacy 1",
                        Reasoning = false,
                        Input = ["text"],
                        Cost = new ModelCostRates(1, 2, null, null),
                        ContextWindow = 128_000,
                        MaxTokens = 4_096,
                    },
                ],
            },
        };
        var (context, published) = Context(credential: oauth);

        await provider.RefreshModels!(context);

        var publication = Assert.Single(published);
        Assert.NotNull(publication.Persist);
        var persist = publication.Persist!;
        var entry = Assert.Single(persist.Models);
        Assert.Equal("legacy-1", entry.Id);
        Assert.Equal("pi-messages", entry.Api);
        Assert.Equal("radius", entry.Provider);
        Assert.NotNull(persist.CheckedAt);

        publication.Update!();
        Assert.Equal(["legacy-1"], provider.GetModels().Select(model => model.Id).ToList());
    }

    [Fact]
    public async Task RefreshModels_LegacyCacheEmptySkipsImportWithoutNetwork()
    {
        var provider = CreateProvider();
        var oauth = new Credential.OAuth("refresh", "access", long.MaxValue); // GatewayConfig 缺省 → 空目录。
        var (context, published) = Context(credential: oauth);

        await provider.RefreshModels!(context);

        Assert.Empty(published); // TS 的 `legacy.length > 0` 判定：空目录不发布。
        Assert.Empty(provider.GetModels()); // 非默认网关基线为空，且无动态目录。
    }

    // ------------------------------------------------------------------ 分支 3：网络刷新无条件覆盖

    [Fact]
    public async Task RefreshModels_NetworkRefreshPersistsAndReplacesBaselineIncludingEmpty()
    {
        var handler = new StubConfigHandler("""{"baseUrl":"https://api.example.test","models":[]}""");
        var provider = CreateProvider(handler: handler);
        var stored = new ModelsStoreEntry { Models = [Model("gw-a")], CheckedAt = 1000 };
        var (context, published) = Context(stored: stored, allowNetwork: true);

        await provider.RefreshModels!(context);

        Assert.Equal(2, published.Count); // 恢复 + 网络覆盖。
        Assert.NotNull(published[1].Persist);
        var persist = published[1].Persist!;
        Assert.Empty(persist.Models); // 组织停用全部模型时生效目录就是空——无条件覆盖。
        Assert.NotNull(persist.CheckedAt);

        published[0].Update!();
        Assert.Equal(["gw-a"], provider.GetModels().Select(model => model.Id).ToList());
        published[1].Update!();
        Assert.Empty(provider.GetModels()); // 空目录也是目录（不是回落基线）。
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RefreshModels_NetworkRefreshPersistsFetchedCatalog()
    {
        var body = """
            {"baseUrl":"https://api.example.test","models":[
              {"id":"gw-x","name":"GW X","reasoning":true,"input":["text"],
               "cost":{"input":3,"output":4},"contextWindow":200000,"maxTokens":8192}
            ]}
            """;
        var handler = new StubConfigHandler(body);
        var provider = CreateProvider(handler: handler);
        var oauth = new Credential.OAuth("refresh", "oauth-access", long.MaxValue);
        var (context, published) = Context(credential: oauth, allowNetwork: true);

        await provider.RefreshModels!(context);

        // OAuth 凭据无 GatewayConfig → legacy 分支不发布；网络发布是唯一一次。
        var publication = Assert.Single(published);
        Assert.NotNull(publication.Persist);
        var persist = publication.Persist!;
        var entry = Assert.Single(persist.Models);
        Assert.Equal("gw-x", entry.Id);
        Assert.Equal("pi-messages", entry.Api);
        Assert.True(entry.Reasoning);
        Assert.Equal(200_000, entry.ContextWindow);

        publication.Update!();
        Assert.Equal(["gw-x"], provider.GetModels().Select(model => model.Id).ToList());
    }

    [Fact]
    public async Task RefreshModels_OfflineSkipsNetwork()
    {
        var provider = CreateProvider();
        var oauth = new Credential.OAuth("refresh", "access", long.MaxValue)
        {
            GatewayConfig = new RadiusGatewayConfig
            {
                BaseUrl = "https://api.example.test",
                Models = [],
            },
        };
        var (context, published) = Context(credential: oauth, allowNetwork: false);

        await provider.RefreshModels!(context);

        Assert.Empty(published); // 空目录不导入；AllowNetwork=false 不再走网络。
    }

    [Fact]
    public async Task RefreshModels_ApiKeyCredentialSendsBearerToken()
    {
        var handler = new StubConfigHandler("""{"baseUrl":"https://api.example.test","models":[]}""");
        var provider = CreateProvider(handler: handler);
        var apiKey = new Credential.ApiKey("sk-test", null);
        var (context, published) = Context(credential: apiKey, allowNetwork: true);

        await provider.RefreshModels!(context);

        Assert.Equal("Bearer sk-test", handler.LastAuthorization);
    }

    [Fact]
    public void RefreshModelsHookIsExposed()
    {
        // 钩子存在才能被 ProviderComposer 接进 store 支撑的刷新链（本收口的前提）。
        Assert.NotNull(CreateProvider().RefreshModels);
    }

    /// <summary><c>/v1/config</c> 的桩：返回固定正文并记录请求。</summary>
    private sealed class StubConfigHandler(string body) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastAuthorization = request.Headers.TryGetValues("authorization", out var values)
                ? values.FirstOrDefault()
                : null;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
