using System.Text.Json.Nodes;
using Pi.Mcp.OAuth;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// <c>OAuthFlow.AdaptOAuthProvider</c>（TS <c>adaptOAuthProvider</c>）的行为：
/// 令牌直读、令牌已被别人替换时短路不刷新。完整刷新/重定向路径由 coding-agent 侧的
/// <c>createMcpAuthProvider</c> 测试覆盖。
/// </summary>
public class AdaptOAuthProviderTests
{
    /// <summary>最小 provider：只实现令牌读写，其余成员按需抛错。</summary>
    private sealed class FakeProvider(OAuthTypes.OAuthTokens? tokens) : OAuthFlow.IOAuthClientProvider
    {
        public OAuthTypes.OAuthTokens? TokensValue { get; set; } = tokens;

        public string RedirectUrl => "http://localhost:1/callback";

        public OAuthTypes.ClientMetadata ClientMetadata { get; } = new([]);

        public Task<OAuthTypes.OAuthTokens?> Tokens(CancellationToken cancellationToken = default)
            => Task.FromResult(TokensValue);

        public Task<OAuthTypes.ClientInformation?> ClientInformation(CancellationToken cancellationToken = default)
            => Task.FromResult<OAuthTypes.ClientInformation?>(null);

        public Task SaveClientInformation(OAuthTypes.ClientInformation information,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveTokens(OAuthTypes.OAuthTokens value, CancellationToken cancellationToken = default)
        {
            TokensValue = value;
            return Task.CompletedTask;
        }

        public Task RedirectToAuthorization(Uri url, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("refresh must not start a redirect flow here");

        public Task SaveCodeVerifier(string verifier, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string> CodeVerifier(CancellationToken cancellationToken = default)
            => Task.FromResult("verifier");
    }

    [Fact]
    public async Task Token_ReturnsAccessToken()
    {
        var provider = new FakeProvider(new OAuthTypes.OAuthTokens("at-1", "Bearer"));
        var adapted = OAuthFlow.AdaptOAuthProvider(provider);
        Assert.Equal("at-1", await adapted.Token());
    }

    [Fact]
    public async Task Token_ReturnsNullWithoutTokens()
    {
        var adapted = OAuthFlow.AdaptOAuthProvider(new FakeProvider(null));
        Assert.Null(await adapted.Token());
    }

    [Fact]
    public async Task OnUnauthorized_ShortCircuitsWhenTokenWasAlreadyReplaced()
    {
        // 当前令牌已是 "new"，被拒请求带的是 "old" → 别的请求已经刷新过，不应再刷新。
        var provider = new FakeProvider(new OAuthTypes.OAuthTokens("new", "Bearer"));
        var adapted = OAuthFlow.AdaptOAuthProvider(provider);
        Assert.NotNull(adapted.OnUnauthorized);

        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
        var context = new UnauthorizedContext
        {
            Response = response,
            ServerUrl = new Uri("https://mcp.example.com/mcp"),
            Fetch = (_, _) => throw new InvalidOperationException("no network expected"),
            Token = "old",
        };
        // 若短路失效，AuthorizeMcp 会走到网络/重定向并抛错。
        await adapted.OnUnauthorized!(context, CancellationToken.None);
    }

    [Fact]
    public async Task OnUnauthorized_WithoutPriorToken_IsExposedForRetry()
    {
        var adapted = OAuthFlow.AdaptOAuthProvider(new FakeProvider(null));
        // onUnauthorized 存在 ⇒ 传输层会在 401 后重试（而不是直接失败）。
        Assert.NotNull(adapted.OnUnauthorized);
        Assert.Null(await adapted.Token());
    }
}
