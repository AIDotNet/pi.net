using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests;

// ============================================================================
// 上游 v1.5.1 #11679 / 6382d8706（packages/ai/src/models.ts）：
// provider 新增 `refreshModelsAuth: "credential" | "configured"`。
//   - "credential"（默认）：网络刷新阶段解析有效凭据，过期 OAuth 先刷新；
//   - "configured"：目录由 provider 之外的源提供（如 pi.dev），只要 provider 已配置
//     就刷新，但**不刷新 OAuth token**、`context.credential` 为 undefined，
//     于是 stale 登录（invalid_grant）不再让目录刷新失败。
//
// 端口落点：IProvider.RefreshModelsAuth / Models.RefreshAsync 的
// configured 分支 / RemoteCatalogProvider("configured") /
// PersonalizedCatalogProvider 转发 / ProviderComposer 组合判定。
//
// 本测试用 fake provider + 过期 OAuth 凭据钉住两种模式的可观察差异：
// configured 模式网络阶段 Credential==null 且 OAuth 不被刷新；
// 默认模式过期 OAuth 会触发刷新并把有效凭据交给网络阶段。
// ============================================================================

public sealed class RefreshModelsAuthTests
{
    /// <summary>记录刷新次数的 OAuth 实现。</summary>
    private sealed class CountingOAuth : IOAuthAuth
    {
        public int RefreshCalls { get; private set; }
        public string Name => "Counting OAuth";
        public bool IsSubscription => true;

        public Task<Credential.OAuth> LoginAsync(ProviderAuthInteraction interaction,
            LoginOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new Credential.OAuth("r", "a", long.MaxValue));

        public Task<Credential.OAuth> RefreshAsync(Credential.OAuth credential,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(new Credential.OAuth("r2", "a2", long.MaxValue));
        }

        public Task<ModelAuth> ToAuthAsync(Credential.OAuth credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelAuth { ApiKey = credential.Access });
    }

    /// <summary>fake 动态目录 provider：捕获网络阶段（AllowNetwork）的 context.Credential。</summary>
    private sealed class FakeCatalogProvider(string? refreshModelsAuth, CountingOAuth oauth) : IProvider
    {
        public List<Credential?> NetworkCredentials { get; } = [];
        public string Id => "fake-catalog";
        public string Name => "Fake Catalog";
        public string? BaseUrl => null;
        public string? RefreshModelsAuth => refreshModelsAuth;
        public ProviderAuth? Auth => new() { OAuth = oauth };
        public IReadOnlyList<ModelSpec> GetModels() => [];

        public Func<RefreshModelsContext, Task>? RefreshModels => context =>
        {
            if (context.AllowNetwork)
            {
                NetworkCredentials.Add(context.Credential);
            }

            return Task.CompletedTask;
        };

        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null) => new AssistantMessageEventStream();
        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null) => new AssistantMessageEventStream();
    }

    private static async Task<(FakeCatalogProvider Provider, CountingOAuth OAuth)> DriveAsync(
        string? refreshModelsAuth)
    {
        var oauth = new CountingOAuth();
        var provider = new FakeCatalogProvider(refreshModelsAuth, oauth);
        var credentials = new InMemoryCredentialStore();
        // 过期 OAuth 凭据（expires 在过去）——默认模式会触发刷新。
        await credentials.ModifyAsync(provider.Id,
            _ => Task.FromResult<Credential?>(new Credential.OAuth("stale-refresh", "stale-access", 1)));

        var models = new Models.Models(new CreateModelsOptions
        {
            Credentials = credentials,
            ModelsStore = new InMemoryModelsStore(),
        });
        models.SetProvider(provider);
        await models.RefreshAsync(new ModelsRefreshOptions { AllowNetwork = true });
        return (provider, oauth);
    }

    [Fact]
    public async Task ConfiguredCatalog_DoesNotRefreshOAuth_AndPassesNoCredential()
    {
        var (provider, oauth) = await DriveAsync("configured");

        // 网络阶段发生了，但既不刷新过期的 OAuth，也不把凭据交给目录刷新。
        Assert.Single(provider.NetworkCredentials);
        Assert.Null(provider.NetworkCredentials[0]);
        Assert.Equal(0, oauth.RefreshCalls);
    }

    [Fact]
    public async Task DefaultCatalog_RefreshesExpiredOAuth_AndPassesCredential()
    {
        var (provider, oauth) = await DriveAsync(null);

        Assert.Single(provider.NetworkCredentials);
        Assert.NotNull(provider.NetworkCredentials[0]);
        Assert.Equal(1, oauth.RefreshCalls);
    }
}
