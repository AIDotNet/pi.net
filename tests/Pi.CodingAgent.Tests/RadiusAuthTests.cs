using Pi.Ai;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using CoreRadius = Pi.CodingAgent.Core.Radius;
using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the Radius relay auth resolver (4g-3d-1): the explicit token wins over the stored
/// credential, offline mode short-circuits before both, and the model runtime is created once and
/// shared across attempts.
/// </summary>
public class RadiusAuthTests
{
    private static string TempFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pi-radius-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, contents);
        return path;
    }

    // ---------------------------------------------------------------- gateway

    [Fact]
    public void Gateway_NormalizesAnExplicitOverride()
    {
        // 补协议、去尾部斜杠（对齐 TS 的 normalizeRadiusGatewayUrl）。
        Assert.Equal("https://radius.example.com",
            new RadiusRelayAuthResolver(gateway: "radius.example.com/").Gateway);
        Assert.Equal("http://localhost:8787",
            new RadiusRelayAuthResolver(gateway: "http://localhost:8787//").Gateway);
        Assert.Equal("https://radius.pi.dev",
            new RadiusRelayAuthResolver(gateway: RadiusConfig.DefaultRadiusGateway).Gateway);
    }

    [Fact]
    public void Gateway_DefaultsToTheConfiguredOrigin()
    {
        var previous = CoreRadius.Env;
        try
        {
            CoreRadius.Env = name => name == CoreRadius.EnvRadiusGateway ? null : previous(name);
            Assert.Equal("https://radius.pi.dev", new RadiusRelayAuthResolver().Gateway);
        }
        finally
        {
            CoreRadius.Env = previous;
        }
    }

    // ---------------------------------------------------------------- explicit token

    [Fact]
    public async Task Resolve_PrefersTheExplicitInlineToken()
    {
        var runtimeCalls = 0;
        var resolver = new RadiusRelayAuthResolver(new AuthInput.Token("inline-token"), "radius.example.com")
        {
            RuntimeFactory = () =>
            {
                runtimeCalls += 1;
                return CreateRadiusRuntimeAsync("stored-radius-token");
            },
        };

        var auth = await resolver.ResolveAsync(new ResolveRadiusRelayAuthOptions { Required = true });

        Assert.NotNull(auth);
        Assert.Equal("https://radius.example.com", auth.Gateway);
        Assert.Equal("inline-token", auth.Token);
        Assert.Equal(0, runtimeCalls);
    }

    [Fact]
    public async Task Resolve_ReadsTheTokenFromAFileAndTrimsIt()
    {
        var path = TempFile("  file-token\n\n");
        try
        {
            var resolver = new RadiusRelayAuthResolver(new AuthInput.File(path), "radius.example.com");

            var auth = await resolver.ResolveAsync(new ResolveRadiusRelayAuthOptions { Required = true });

            Assert.Equal("file-token", auth!.Token);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Resolve_RejectsAnEmptyToken()
    {
        var path = TempFile("  \n");
        try
        {
            var resolver = new RadiusRelayAuthResolver(new AuthInput.File(path), "radius.example.com");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => resolver.ResolveAsync(new ResolveRadiusRelayAuthOptions { Required = true }));

            Assert.Contains("must not be empty", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- offline mode

    [Fact]
    public async Task Resolve_OfflineRejectsARequiredConnection()
    {
        var resolver = new RadiusRelayAuthResolver(new AuthInput.Token("inline-token"), "radius.example.com")
        {
            Env = name => name == "PI_OFFLINE" ? "1" : null,
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(new ResolveRadiusRelayAuthOptions { Required = true }));

        // 离线检查先于显式 token：TS 的顺序也是如此。
        Assert.Contains("unavailable in offline mode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_OfflineDegradesAnOptionalConnection()
    {
        var resolver = new RadiusRelayAuthResolver(new AuthInput.Token("inline-token"), "radius.example.com")
        {
            Env = name => name == "PI_OFFLINE" ? "1" : null,
        };

        Assert.Null(await resolver.ResolveAsync(new ResolveRadiusRelayAuthOptions { Required = false }));
    }

    // ---------------------------------------------------------------- stored credential

    [Fact]
    public async Task Resolve_FallsBackToTheStoredCredential()
    {
        var resolver = new RadiusRelayAuthResolver(gateway: "radius.example.com")
        {
            RuntimeFactory = () => CreateRadiusRuntimeAsync("stored-radius-token"),
        };

        var auth = await resolver.ResolveAsync(new ResolveRadiusRelayAuthOptions { Required = true });

        Assert.NotNull(auth);
        Assert.Equal("stored-radius-token", auth.Token);
        Assert.Equal("https://radius.example.com", auth.Gateway);
    }

    [Fact]
    public async Task Resolve_WithoutAnyCredentialReturnsNullWhenOptional()
    {
        var resolver = new RadiusRelayAuthResolver(gateway: "radius.example.com")
        {
            RuntimeFactory = () => CreateRadiusRuntimeAsync(null),
        };

        Assert.Null(await resolver.ResolveAsync(new ResolveRadiusRelayAuthOptions { Required = false }));
    }

    [Fact]
    public async Task Resolve_WithoutAnyCredentialThrowsWhenRequired()
    {
        var resolver = new RadiusRelayAuthResolver(gateway: "radius.example.com")
        {
            RuntimeFactory = () => CreateRadiusRuntimeAsync(null),
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(new ResolveRadiusRelayAuthOptions { Required = true }));

        Assert.Contains("Radius authentication is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_CreatesTheRuntimeOnceAndSharesIt()
    {
        var runtimeCalls = 0;
        var resolver = new RadiusRelayAuthResolver(gateway: "radius.example.com")
        {
            RuntimeFactory = () =>
            {
                runtimeCalls += 1;
                return CreateRadiusRuntimeAsync("stored-radius-token");
            },
        };

        var first = await resolver.ResolveAsync(new ResolveRadiusRelayAuthOptions { Required = true });
        var second = await resolver.ResolveAsync(new ResolveRadiusRelayAuthOptions { Required = true });

        Assert.Equal("stored-radius-token", first!.Token);
        Assert.Equal("stored-radius-token", second!.Token);
        Assert.Equal(1, runtimeCalls);
    }

    [Fact]
    public async Task Resolve_ThrowsWhenAlreadyAborted()
    {
        var resolver = new RadiusRelayAuthResolver(gateway: "radius.example.com")
        {
            RuntimeFactory = () => throw new InvalidOperationException("must not be reached"),
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(new ResolveRadiusRelayAuthOptions
            {
                Required = true,
                Signal = new CancellationToken(canceled: true),
            }));
    }

    // ---------------------------------------------------------------- test doubles

    private static async Task<ModelRuntime> CreateRadiusRuntimeAsync(string? apiKey)
    {
        Func<string, string?> env = name => name == "PI_OFFLINE" ? "1" : null;
        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new InMemoryCredentialStore(),
            ModelsPathDisabled = true,
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            RefreshOnCreate = false,
            Env = env,
            AuthContext = new StubAuthContext(env),
        });
        runtime.RegisterNativeProvider(new StubProvider
        {
            Id = CoreRadius.ProviderId,
            Models = [],
            AuthOverride = new ProviderAuth
            {
                ApiKey = apiKey is null ? new UnconfiguredApiKeyAuth() : new StubApiKeyAuth(apiKey),
            },
        });
        if (runtime.PendingBackgroundRefresh is { } pending) await pending;
        return runtime;
    }

    private sealed class StubProvider : IProvider
    {
        public required string Id { get; init; }

        public required IReadOnlyList<ModelSpec> Models { get; init; }

        public ProviderAuth? AuthOverride { get; init; }

        public string Name => Id;

        public string? BaseUrl => null;

        public ProviderAuth? Auth => AuthOverride;

        public IReadOnlyList<ModelSpec> GetModels() => Models;

        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null) => throw new NotSupportedException();

        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null) => throw new NotSupportedException();
    }

    private sealed class StubApiKeyAuth(string? apiKey) : IApiKeyAuth
    {
        public string Name => "stub-api-key";

        public Task<AuthResult?> ResolveAsync(Credential.ApiKey? credential, IAuthContext ctx,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AuthResult?>(apiKey is null
                ? null
                : new AuthResult
                {
                    Auth = new ModelAuth { ApiKey = apiKey },
                    Source = "stub-env",
                });
    }

    private sealed class UnconfiguredApiKeyAuth : IApiKeyAuth
    {
        public string Name => "unconfigured";

        public Task<AuthResult?> ResolveAsync(Credential.ApiKey? credential, IAuthContext ctx,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AuthResult?>(null);
    }

    private sealed class StubAuthContext(Func<string, string?> env) : IAuthContext
    {
        public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(env(name));

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
