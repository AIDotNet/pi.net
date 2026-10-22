using Pi.Mcp.OAuth;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// 取消语义（上游 f10993bc7 / #10565）：授权流程中途取消必须直接失败，**不能**退化成一次新的
/// 授权重定向——那条路会把已轮转的 refresh token 丢掉，还会让关闭流程多等一次网络往返。
/// </summary>
public class OAuthFlowCancelTests
{
    /// <summary>接受请求但永不应答的授权服务器；每次命中都记下路径。</summary>
    private sealed class StallingServer
    {
        public List<string> HitPaths { get; } = [];

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (HitPaths)
            {
                HitPaths.Add(request.RequestUri!.AbsolutePath);
            }

            // 只有取消能结束：模拟不响应的服务器。
            return Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith<HttpResponseMessage>(
                _ => throw new OperationCanceledException(cancellationToken), cancellationToken,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public int Count
        {
            get
            {
                lock (HitPaths)
                {
                    return HitPaths.Count;
                }
            }
        }
    }

    private sealed class TestProvider : OAuthFlow.IOAuthClientProvider
    {
        public string RedirectUrl { get; } = "http://127.0.0.1/callback";

        public OAuthTypes.ClientMetadata ClientMetadata { get; } = new([]);

        public Uri? RedirectedTo { get; private set; }

        public OAuthTypes.ClientInformation? Client { get; set; }

        public OAuthTypes.OAuthTokens? StoredTokens { get; set; }

        public OAuthTypes.DiscoveryState? Discovery { get; set; }

        public Task<OAuthTypes.ClientInformation?> ClientInformation(CancellationToken cancellationToken = default)
            => Task.FromResult(Client);

        public Task SaveClientInformation(
            OAuthTypes.ClientInformation information, CancellationToken cancellationToken = default)
        {
            Client = information;
            return Task.CompletedTask;
        }

        public Task<OAuthTypes.OAuthTokens?> Tokens(CancellationToken cancellationToken = default)
            => Task.FromResult(StoredTokens);

        public Task SaveTokens(OAuthTypes.OAuthTokens tokens, CancellationToken cancellationToken = default)
        {
            StoredTokens = tokens;
            return Task.CompletedTask;
        }

        public Task RedirectToAuthorization(Uri url, CancellationToken cancellationToken = default)
        {
            RedirectedTo = url;
            return Task.CompletedTask;
        }

        public Task SaveCodeVerifier(string verifier, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string> CodeVerifier(CancellationToken cancellationToken = default)
            => Task.FromResult("verifier");

        public Task SaveDiscoveryState(OAuthTypes.DiscoveryState state, CancellationToken cancellationToken = default)
        {
            Discovery = state;
            return Task.CompletedTask;
        }

        public Task<OAuthTypes.DiscoveryState?> DiscoveryState(CancellationToken cancellationToken = default)
            => Task.FromResult(Discovery);
    }

    [Fact]
    public async Task CancellingDuringDiscoveryFailsInsteadOfRedirecting()
    {
        var server = new StallingServer();
        var provider = new TestProvider();
        using var cancel = new CancellationTokenSource();

        var flow = OAuthFlow.AuthorizeMcp(provider, new OAuthFlow.FlowOptions
        {
            ServerUrl = "http://127.0.0.1/mcp",
            Fetch = new McpFetch(server.SendAsync),
            SkipIssuerValidation = true,
        }, cancel.Token);

        await WaitForAsync(() => server.Count > 0);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow);
        Assert.Null(provider.RedirectedTo);
    }

    [Fact]
    public async Task CancellingDuringRefreshFailsInsteadOfStartingANewAuthorization()
    {
        var server = new StallingServer();
        var provider = new TestProvider
        {
            Client = new OAuthTypes.ClientInformation(ClientId: "client"),
            StoredTokens = new OAuthTypes.OAuthTokens(
                AccessToken: "a1", TokenType: "Bearer", RefreshToken: "r1"),
            // 发现结果已缓存：流程直接走到换令牌那一步。
            Discovery = new OAuthTypes.DiscoveryState(
                "http://127.0.0.1",
                new OAuthTypes.AuthorizationServerMetadata(
                    Issuer: "http://127.0.0.1",
                    AuthorizationEndpoint: "http://127.0.0.1/authorize",
                    TokenEndpoint: "http://127.0.0.1/token",
                    ResponseTypesSupported: ["code"]),
                ResourceMetadata: null,
                ResourceMetadataUrl: null),
        };
        using var cancel = new CancellationTokenSource();

        var flow = OAuthFlow.AuthorizeMcp(provider, new OAuthFlow.FlowOptions
        {
            ServerUrl = "http://127.0.0.1/mcp",
            Fetch = new McpFetch(server.SendAsync),
            SkipIssuerValidation = true,
        }, cancel.Token);

        await WaitForAsync(() => server.Count > 0);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow);
        Assert.Equal("/token", server.HitPaths[^1]);
        Assert.Null(provider.RedirectedTo);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException("condition was never met");
    }
}
