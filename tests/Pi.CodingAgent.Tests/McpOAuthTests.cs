using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Extensions.Mcp;
using Pi.Mcp;
using Pi.Mcp.OAuth;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the MCP OAuth layer (port of <c>extensions/mcp/oauth.ts</c>, batch P4b-2). Vectors are
/// transcribed from the TS behaviour: callback settings, scope merging, store keys, the legacy state
/// takeover, redirect URL validation, the Client ID Metadata Document rules, the refresh lock wiring,
/// and the auth provider's refresh paths that need no network.
/// </summary>
public class McpOAuthTests : IDisposable
{
    private const string ServerUrl = "https://example.com/mcp";
    private const string Redirect = "http://127.0.0.1:5000/callback";

    private readonly List<string> tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup.
            }
        }
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pi-oauth-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        return dir;
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static McpOAuthState State(
        string accessToken, long? tokensExpireAt = null, string? refreshToken = null, string? scope = null)
        => new()
        {
            ServerUrl = ServerUrl,
            Tokens = new OAuthTypes.OAuthTokens(accessToken, "Bearer", Scope: scope, RefreshToken: refreshToken),
            TokensExpireAt = tokensExpireAt,
        };

    private static (McpOAuth.McpOAuthCredentialStore Store, InMemoryAuthStorageBackend Backend) NewStore()
    {
        var backend = new InMemoryAuthStorageBackend();
        return (new McpOAuth.McpOAuthCredentialStore(backend), backend);
    }

    private static string? ReadContent(InMemoryAuthStorageBackend backend)
        => backend.WithLock(current => new LockResult<string?>(current));

    // ─── callbackSettings ────────────────────────────────────────────────────

    [Fact]
    public void CallbackSettings_DefaultLoopback()
    {
        var settings = McpOAuth.CallbackSettingsFrom(new McpOAuthSettings());
        Assert.Equal("127.0.0.1", settings.Host);
        Assert.Equal("127.0.0.1", settings.RedirectHost);
        Assert.Null(settings.Port);
        Assert.Equal("/callback", settings.Path);
        Assert.Null(settings.FixedRedirectUrl);
    }

    [Fact]
    public void CallbackSettings_FixedPortFromSettings()
    {
        var settings = McpOAuth.CallbackSettingsFrom(new McpOAuthSettings { CallbackPort = 8080 });
        Assert.Equal(8080, settings.Port);
        Assert.Equal("http://127.0.0.1:8080/callback", settings.FixedRedirectUrl);
        Assert.Equal("/callback", settings.Path);
    }

    [Fact]
    public void CallbackSettings_LocalhostIsServedOnLoopback()
    {
        var settings = McpOAuth.CallbackSettingsFrom(new McpOAuthSettings { CallbackUrl = "http://localhost:9999/cb" });
        Assert.Equal("127.0.0.1", settings.Host);
        Assert.Equal("localhost", settings.RedirectHost);
        Assert.Equal(9999, settings.Port);
        Assert.Equal("/cb", settings.Path);
        // A configured URI with a port is sent exactly as written.
        Assert.Equal("http://localhost:9999/cb", settings.FixedRedirectUrl);
    }

    [Fact]
    public void CallbackSettings_UrlWithoutPortTakesCallbackPort()
    {
        var settings = McpOAuth.CallbackSettingsFrom(
            new McpOAuthSettings { CallbackUrl = "http://127.0.0.1/cb", CallbackPort = 8080 });
        Assert.Equal(8080, settings.Port);
        Assert.Equal("/cb", settings.Path);
        Assert.Equal("http://127.0.0.1:8080/cb", settings.FixedRedirectUrl);
    }

    [Fact]
    public void CallbackSettings_Ipv6BracketsAreStripped()
    {
        var settings = McpOAuth.CallbackSettingsFrom(new McpOAuthSettings { CallbackUrl = "http://[::1]:7000/callback" });
        Assert.Equal("::1", settings.Host);
        Assert.Equal("::1", settings.RedirectHost);
        Assert.Equal(7000, settings.Port);
        Assert.Equal("http://[::1]:7000/callback", settings.FixedRedirectUrl);
    }

    // ─── mergeScopes ─────────────────────────────────────────────────────────

    [Fact]
    public void MergeScopes_DeduplicatesPreservingOrder()
    {
        Assert.Equal("a b c", McpOAuth.MergeScopes(null, "a b", "b c"));
        Assert.Equal("a", McpOAuth.MergeScopes("a", null));
        Assert.Equal("a b", McpOAuth.MergeScopes("  a   b  ", "b"));
        Assert.Null(McpOAuth.MergeScopes(null, null));
        Assert.Null(McpOAuth.MergeScopes());
    }

    // ─── callbackId / clientMetadataDocument ─────────────────────────────────

    [Fact]
    public void CallbackId_KnownVectors()
    {
        // base64url of the first 9 bytes of sha256, like Codex does.
        Assert.Equal("w9gKTtkB7gWy", McpOAuth.CallbackId(ServerUrl));
        Assert.Equal("pggt81oqV940", McpOAuth.CallbackId("https://mcp.example.com:8443/api"));
        // The fragment is cleared before hashing (`url.hash = ""`).
        Assert.Equal(McpOAuth.CallbackId(ServerUrl), McpOAuth.CallbackId(ServerUrl + "#frag"));
    }

    [Fact]
    public void ClientMetadataDocument_DefaultDocument_WhenIssSupported()
    {
        var metadata = new OAuthTypes.AuthorizationServerMetadata(
            "https://auth.example.com",
            "https://auth.example.com/authorize",
            "https://auth.example.com/token",
            TokenEndpointAuthMethodsSupported: ["none"],
            ClientIdMetadataDocumentSupported: true,
            AuthorizationResponseIssParameterSupported: true);
        var document = McpOAuth.ClientMetadataDocumentFor(ServerUrl, Redirect, metadata);
        Assert.Equal("https://pi.dev/oauth/client.json", document.Url);
        Assert.Equal(Redirect, document.RedirectUrl);
    }

    [Fact]
    public void ClientMetadataDocument_ServerSpecific_WithoutIss()
    {
        var metadata = new OAuthTypes.AuthorizationServerMetadata(
            "https://auth.example.com",
            "https://auth.example.com/authorize",
            "https://auth.example.com/token",
            TokenEndpointAuthMethodsSupported: ["none"],
            ClientIdMetadataDocumentSupported: true);
        var id = McpOAuth.CallbackId(ServerUrl);
        var document = McpOAuth.ClientMetadataDocumentFor(ServerUrl, Redirect, metadata);
        Assert.Equal($"https://pi.dev/oauth/{id}/client.json", document.Url);
        Assert.Equal($"{Redirect}/{id}", document.RedirectUrl);
    }

    [Fact]
    public void ClientMetadataDocument_UnsupportedServers_Throw()
    {
        var withoutCimd = new OAuthTypes.AuthorizationServerMetadata(
            "https://auth.example.com",
            "https://auth.example.com/authorize",
            "https://auth.example.com/token",
            TokenEndpointAuthMethodsSupported: ["none"]);
        var error = Assert.Throws<InvalidOperationException>(
            () => McpOAuth.ClientMetadataDocumentFor(ServerUrl, Redirect, withoutCimd));
        Assert.Equal(
            "The authorization server does not support Client ID Metadata Documents for public clients; remove oauth.clientRegistration \"cimd\"",
            error.Message);
        var withoutNone = new OAuthTypes.AuthorizationServerMetadata(
            "https://auth.example.com",
            "https://auth.example.com/authorize",
            "https://auth.example.com/token",
            TokenEndpointAuthMethodsSupported: ["client_secret_basic"],
            ClientIdMetadataDocumentSupported: true);
        Assert.Throws<InvalidOperationException>(
            () => McpOAuth.ClientMetadataDocumentFor(ServerUrl, Redirect, withoutNone));
        Assert.Throws<InvalidOperationException>(
            () => McpOAuth.ClientMetadataDocumentFor(ServerUrl, Redirect, metadata: null));
    }

    // ─── responseFromRedirectUrl ─────────────────────────────────────────────

    [Fact]
    public void ResponseFromRedirectUrl_CodeAndIss()
    {
        var response = McpOAuth.ResponseFromRedirectUrl($"{Redirect}?code=abc&state=s1", "s1", Redirect);
        Assert.Equal("abc", response.Code);
        Assert.Null(response.Iss);
        var withIss = McpOAuth.ResponseFromRedirectUrl($"{Redirect}?code=abc&state=s1&iss=https://auth", "s1", Redirect);
        Assert.Equal("https://auth", withIss.Iss);
        // Query decoding: `+` is a space, `%2B` a plus.
        Assert.Equal("a+b", McpOAuth.ResponseFromRedirectUrl($"{Redirect}?code=a%2Bb&state=s", "s", Redirect).Code);
    }

    [Fact]
    public void ResponseFromRedirectUrl_Rejects()
    {
        Assert.Equal(
            "Expected the full redirect URL from the browser address bar",
            Assert.Throws<InvalidOperationException>(
                () => McpOAuth.ResponseFromRedirectUrl("not a url", "s1", Redirect)).Message);
        foreach (var url in new[]
                 {
                     "http://127.0.0.1:5001/callback?code=a&state=s1",
                     "http://127.0.0.1:5000/other?code=a&state=s1",
                     "http://evil.example.com/callback?code=a&state=s1",
                 })
        {
            Assert.Equal(
                "The redirect URL does not match this sign-in's redirect URI",
                Assert.Throws<InvalidOperationException>(
                    () => McpOAuth.ResponseFromRedirectUrl(url, "s1", Redirect)).Message);
        }

        Assert.Equal(
            "User denied",
            Assert.Throws<InvalidOperationException>(
                () => McpOAuth.ResponseFromRedirectUrl($"{Redirect}?error=access_denied&error_description=User+denied&state=s1", "s1", Redirect)).Message);
        Assert.Equal(
            "access_denied",
            Assert.Throws<InvalidOperationException>(
                () => McpOAuth.ResponseFromRedirectUrl($"{Redirect}?error=access_denied&state=s1", "s1", Redirect)).Message);
        Assert.Equal(
            "The redirect URL belongs to a different sign-in",
            Assert.Throws<InvalidOperationException>(
                () => McpOAuth.ResponseFromRedirectUrl($"{Redirect}?code=a&state=other", "s1", Redirect)).Message);
        foreach (var query in new[] { "state=s1", "state=s1&code=" })
        {
            Assert.Equal(
                "The redirect URL does not contain an authorization code",
                Assert.Throws<InvalidOperationException>(
                    () => McpOAuth.ResponseFromRedirectUrl($"{Redirect}?{query}", "s1", Redirect)).Message);
        }
    }

    // ─── states serialization ───────────────────────────────────────────────

    [Fact]
    public void ParseStates_NonObjectsAreEmpty()
    {
        Assert.Empty(McpOAuth.ParseStates(null));
        Assert.Empty(McpOAuth.ParseStates("   "));
        Assert.Empty(McpOAuth.ParseStates("[]"));
        Assert.Empty(McpOAuth.ParseStates("17"));
        Assert.ThrowsAny<JsonException>(() => McpOAuth.ParseStates("{"));
    }

    [Fact]
    public void SerializeStates_TwoSpaceIndentAndTrailingNewline()
    {
        var states = new JsonObject { ["k"] = JsonSerializer.SerializeToNode(State("tok")) };
        var text = McpOAuth.SerializeStates(states);
        Assert.EndsWith("\n", text);
        Assert.Contains("\n  \"k\": {\n", text);
        Assert.Contains("\"serverUrl\": \"https://example.com/mcp\"", text);
        Assert.True(McpOAuth.ParseStates(text).ContainsKey("k"));
    }

    // ─── credential store ────────────────────────────────────────────────────

    [Fact]
    public async Task CredentialStore_SaveLoadRoundTrip_UsesNameAndUrlKey()
    {
        var (store, backend) = NewStore();
        var server = store.ForServer("srv", ServerUrl);
        await server.SaveAsync(State("tok", tokensExpireAt: NowMs() + 3_600_000, refreshToken: "ref"));
        var loaded = await server.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal("tok", loaded!.Tokens!.AccessToken);
        Assert.Equal("ref", loaded.Tokens!.RefreshToken);
        Assert.Contains("mcp__srv|https://example.com/mcp", ReadContent(backend));
    }

    [Fact]
    public async Task CredentialStore_LegacyState_TakenOverByFirstServerOnly()
    {
        var (store, backend) = NewStore();
        const string Legacy = "{\"https://example.com/mcp\":{\"serverUrl\":\"https://example.com/mcp\",\"tokens\":{\"access_token\":\"legacy\",\"token_type\":\"Bearer\"}}}";
        backend.WithLock(_ => new LockResult<object?>(null, Legacy));
        var loaded = await store.ForServer("srv", ServerUrl).LoadAsync();
        Assert.Equal("legacy", loaded!.Tokens!.AccessToken);
        var content = ReadContent(backend)!;
        Assert.Contains("mcp__srv|https://example.com/mcp", content);
        Assert.DoesNotContain("\"https://example.com/mcp\":", content);
        // A second server with the same URL signs in again: the legacy entry is gone.
        Assert.Null(await store.ForServer("other", ServerUrl).LoadAsync());
    }

    [Fact]
    public async Task CredentialStore_NameKey_WinsOverLegacyWithoutRewrite()
    {
        var (store, backend) = NewStore();
        backend.WithLock(_ => new LockResult<object?>(null, "{\"mcp__srv|https://example.com/mcp\":{\"serverUrl\":\"https://example.com/mcp\",\"tokens\":{\"access_token\":\"new\",\"token_type\":\"Bearer\"}},\"https://example.com/mcp\":{\"serverUrl\":\"https://example.com/mcp\",\"tokens\":{\"access_token\":\"legacy\",\"token_type\":\"Bearer\"}}}"));
        var before = ReadContent(backend);
        var loaded = await store.ForServer("srv", ServerUrl).LoadAsync();
        Assert.Equal("new", loaded!.Tokens!.AccessToken);
        Assert.Equal(before, ReadContent(backend));
    }

    [Fact]
    public async Task CredentialStore_Tokens_DoesNotTakeOverLegacy()
    {
        var (store, backend) = NewStore();
        const string Legacy = "{\"https://example.com/mcp\":{\"serverUrl\":\"https://example.com/mcp\",\"tokens\":{\"access_token\":\"legacy\",\"token_type\":\"Bearer\"}}}";
        backend.WithLock(_ => new LockResult<object?>(null, Legacy));
        var tokens = await store.TokensAsync("srv", ServerUrl);
        Assert.Equal("legacy", tokens!.AccessToken);
        Assert.Equal(Legacy, ReadContent(backend));
    }

    [Fact]
    public async Task CredentialStore_Remove_ReportsPresence()
    {
        var (store, backend) = NewStore();
        Assert.False(await store.RemoveAsync("srv", ServerUrl));
        await store.ForServer("srv", ServerUrl).SaveAsync(State("tok"));
        Assert.True(await store.RemoveAsync("srv", ServerUrl));
        Assert.DoesNotContain("mcp__srv|", ReadContent(backend));
    }

    [Fact]
    public async Task CredentialStore_Remove_CleansLegacyKey()
    {
        var (store, backend) = NewStore();
        backend.WithLock(_ => new LockResult<object?>(null, "{\"https://example.com/mcp\":{\"serverUrl\":\"https://example.com/mcp\"}}"));
        Assert.True(await store.RemoveAsync("srv", ServerUrl));
        Assert.DoesNotContain("example.com/mcp", ReadContent(backend));
    }

    [Fact]
    public async Task WithRefreshLock_FileArtifactIsHeldAndReleased()
    {
        var backend = new InMemoryAuthStorageBackend();
        var root = NewTempDir();
        var locked = new McpOAuth.McpOAuthCredentialStore(backend, root);
        var server = locked.ForServer("srv", ServerUrl);
        Assert.Equal(7, await server.WithRefreshLockAsync(() => Task.FromResult(7)));
        Assert.Empty(Directory.GetFiles(root));
        // The artifact is `<lockDir>/mcp-auth-refresh-<sha256(key) hex[0:16]>.lock`.
        List<string> held = [];
        await server.WithRefreshLockAsync(() =>
        {
            held.AddRange(Directory.GetFiles(root).Select(path => Path.GetFileName(path)!));
            return Task.FromResult(0);
        });
        Assert.Equal("mcp-auth-refresh-c1f97386acd7b397.lock", Assert.Single(held));
        Assert.Empty(Directory.GetFiles(root));
    }

    // ─── stored state preparation (signInMcpServer lines 481-496) ────────────

    [Fact]
    public void PrepareStoredState_KeepsRegisteredClientOnSameRedirect()
    {
        var stored = new McpOAuthState
        {
            ServerUrl = ServerUrl,
            ClientInformation = new OAuthTypes.ClientInformation(
                "cid", RedirectUris: ["http://127.0.0.1:5000/callback"]),
            Tokens = new OAuthTypes.OAuthTokens("tok", "Bearer", RefreshToken: "ref"),
            TokensExpireAt = NowMs() + 1_000,
            OAuthState = "stale",
        };
        var prepared = McpOAuth.PrepareStoredStateForSignIn(
            stored, new McpOAuthSettings(), "http://127.0.0.1:5000/callback", cimd: false);
        Assert.NotNull(prepared!.ClientInformation);
        Assert.Equal("tok", prepared.Tokens!.AccessToken);
        // The expiry is carried over untouched; compare against the stored value directly so a
        // millisecond tick between setup and assertion cannot flip the result (was flaky).
        Assert.Equal(stored.TokensExpireAt, prepared.TokensExpireAt);
        // Every sign-in gets a fresh state parameter.
        Assert.Null(prepared.OAuthState);
    }

    [Fact]
    public void PrepareStoredState_DropsClientOnOtherRedirect()
    {
        var stored = new McpOAuthState
        {
            ServerUrl = ServerUrl,
            ClientInformation = new OAuthTypes.ClientInformation(
                "cid", RedirectUris: ["http://127.0.0.1:5000/callback"]),
            Tokens = new OAuthTypes.OAuthTokens("tok", "Bearer"),
            TokensExpireAt = NowMs() + 1_000,
        };
        var prepared = McpOAuth.PrepareStoredStateForSignIn(
            stored, new McpOAuthSettings(), "http://127.0.0.1:5001/callback", cimd: false);
        Assert.Null(prepared!.ClientInformation);
        Assert.Null(prepared.Tokens);
        Assert.Null(prepared.TokensExpireAt);
    }

    [Fact]
    public void PrepareStoredState_CimdDropsStoredClient_ConfiguredClientIdKeeps()
    {
        var stored = new McpOAuthState
        {
            ServerUrl = ServerUrl,
            ClientInformation = new OAuthTypes.ClientInformation("cid"),
            Tokens = new OAuthTypes.OAuthTokens("tok", "Bearer"),
        };
        var cimd = McpOAuth.PrepareStoredStateForSignIn(
            stored, new McpOAuthSettings { ClientRegistration = McpClientRegistration.Cimd }, Redirect, cimd: true);
        Assert.Null(cimd!.ClientInformation);
        Assert.Null(cimd.Tokens);
        var withId = McpOAuth.PrepareStoredStateForSignIn(
            stored, new McpOAuthSettings { ClientId = "cfg" }, "http://localhost:1234/cb", cimd: false);
        Assert.NotNull(withId!.ClientInformation);
        Assert.Null(McpOAuth.PrepareStoredStateForSignIn(null, new McpOAuthSettings(), Redirect, cimd: false));
    }

    // ─── auth provider ───────────────────────────────────────────────────────

    private static (McpAuthProvider Provider, FakeStore Store) NewProvider()
    {
        var store = new FakeStore();
        var provider = McpOAuth.CreateAuthProvider(new McpAuthProviderOptions
        {
            ServerUrl = ServerUrl,
            Store = store,
            Settings = () => throw new InvalidOperationException("settings must not be called"),
            OnChallenge = _ => { },
        });
        return (provider, store);
    }

    [Fact]
    public async Task Token_FreshToken_IsSentWithoutSettings()
    {
        var (provider, store) = NewProvider();
        store.Enqueue(State("tok", tokensExpireAt: NowMs() + 3_600_000, refreshToken: "ref"));
        Assert.Equal("tok", await provider.Token());
        Assert.Equal(0, store.LockEntries);
    }

    [Fact]
    public async Task Token_ExpiredWithoutRefreshToken_FallsThrough()
    {
        var (provider, store) = NewProvider();
        store.Enqueue(State("old", tokensExpireAt: NowMs() - 60_000));
        // The old token goes out and a 401 decides what happens.
        Assert.Equal("old", await provider.Token());
        Assert.Equal(0, store.LockEntries);
    }

    [Fact]
    public async Task Token_ExpiredWithReplacedToken_UsesItWithoutRefreshing()
    {
        var (provider, store) = NewProvider();
        store.Enqueue(State("old", tokensExpireAt: NowMs() - 60_000, refreshToken: "ref"));
        store.Enqueue(State("new", tokensExpireAt: NowMs() + 3_600_000, refreshToken: "ref"));
        store.Enqueue(State("new", tokensExpireAt: NowMs() + 3_600_000, refreshToken: "ref"));
        Assert.Equal("new", await provider.Token());
        Assert.Equal(1, store.LockEntries);
    }

    [Fact]
    public async Task Settled_WaitsForInFlightRefresh()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeStore { Gate = () => gate.Task };
        var provider = McpOAuth.CreateAuthProvider(new McpAuthProviderOptions
        {
            ServerUrl = ServerUrl,
            Store = store,
            Settings = () => throw new InvalidOperationException("settings must not be called"),
            OnChallenge = _ => { },
        });
        store.Enqueue(State("old", tokensExpireAt: NowMs() - 60_000, refreshToken: "ref"));
        store.Enqueue(State("new", tokensExpireAt: NowMs() + 3_600_000, refreshToken: "ref"));
        store.Enqueue(State("new", tokensExpireAt: NowMs() + 3_600_000, refreshToken: "ref"));
        var tokenTask = provider.Token();
        Assert.False(tokenTask.IsCompleted);
        var settled = provider.SettledAsync();
        Assert.False(settled.IsCompleted);
        gate.SetResult();
        await settled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("new", await tokenTask.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Settled_WithoutRefresh_Completes()
    {
        var (provider, _) = NewProvider();
        Assert.True(provider.SettledAsync().IsCompleted);
    }

    private static UnauthorizedContext Context(string? token, string wwwAuthenticate)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.TryAddWithoutValidation("WWW-Authenticate", wwwAuthenticate);
        return new UnauthorizedContext
        {
            Response = response,
            ServerUrl = new Uri(ServerUrl),
            Fetch = (_, _) => throw new InvalidOperationException("fetch must not be called"),
            Token = token,
        };
    }

    [Fact]
    public async Task OnUnauthorized_InsufficientScope_NeedsNewSignIn()
    {
        var store = new FakeStore();
        var challenges = new List<OAuthTypes.Challenge>();
        var provider = McpOAuth.CreateAuthProvider(new McpAuthProviderOptions
        {
            ServerUrl = ServerUrl,
            Store = store,
            Settings = () => throw new InvalidOperationException("settings must not be called"),
            OnChallenge = challenge => challenges.Add(challenge),
        });
        store.Enqueue(State("old", refreshToken: "ref"));
        await Assert.ThrowsAsync<McpOAuthAuthorizationRequiredError>(
            () => provider.OnUnauthorized!(Context("old", "Bearer error=\"insufficient_scope\""), CancellationToken.None));
        // The challenge is delivered, but a refresh is never attempted: a refresh keeps the granted scope.
        Assert.Equal("insufficient_scope", Assert.Single(challenges).Error);
        Assert.Equal(0, store.LockEntries);
    }

    [Fact]
    public async Task OnUnauthorized_ReplacedToken_DoesNotRefresh()
    {
        var (provider, store) = NewProvider();
        store.Enqueue(State("new", tokensExpireAt: NowMs() + 3_600_000, refreshToken: "ref"));
        await provider.OnUnauthorized!(Context("old", "Bearer realm=\"x\""), CancellationToken.None);
        Assert.Equal(1, store.LockEntries);
    }

    [Fact]
    public async Task OnUnauthorized_WithoutRefreshToken_NeedsSignIn()
    {
        var (provider, store) = NewProvider();
        store.Enqueue(State("old"));
        await Assert.ThrowsAsync<McpOAuthAuthorizationRequiredError>(
            () => provider.OnUnauthorized!(Context("old", "Bearer realm=\"x\""), CancellationToken.None));
        Assert.Equal(1, store.LockEntries);
    }

    [Fact]
    public async Task OnUnauthorized_DeliversChallenge()
    {
        var store = new FakeStore();
        var challenges = new List<OAuthTypes.Challenge>();
        var provider = McpOAuth.CreateAuthProvider(new McpAuthProviderOptions
        {
            ServerUrl = ServerUrl,
            Store = store,
            Settings = () => throw new InvalidOperationException("settings must not be called"),
            OnChallenge = challenge => challenges.Add(challenge),
        });
        await Assert.ThrowsAsync<McpOAuthAuthorizationRequiredError>(() => provider.OnUnauthorized!(
            Context("old", "Bearer resource_metadata=\"https://example.com/.well-known/oauth-protected-resource\", scope=\"mcp\", error=\"insufficient_scope\""),
            CancellationToken.None));
        var challenge = Assert.Single(challenges);
        Assert.Equal("insufficient_scope", challenge.Error);
        Assert.Equal("https://example.com/.well-known/oauth-protected-resource", challenge.ResourceMetadataUrl);
        Assert.Equal("mcp", challenge.Scope);
    }

    // ─── waitForAuthorizationResponse ────────────────────────────────────────

    [Fact]
    public async Task WaitForAuthorization_PromptWins_BrowserIsCancelled()
    {
        var browser = new FakeBrowser();
        var prompt = new FakePrompt();
        var wait = McpOAuth.WaitForAuthorizationResponseAsync(
            browser.Wait, "s1", Redirect, "/callback", prompt, CancellationToken.None);
        prompt.Answer($"{Redirect}?code=xyz&state=s1");
        var response = await wait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("xyz", response.Code);
        Assert.Null(response.Iss);
        Assert.True(browser.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task WaitForAuthorization_BrowserWins_PromptIsAborted()
    {
        var browser = new FakeBrowser();
        browser.Callback("abc", "s1", "https://issuer.example.com");
        var prompt = new FakePrompt();
        var response = await McpOAuth.WaitForAuthorizationResponseAsync(
            browser.Wait, "s1", Redirect, "/callback", prompt, CancellationToken.None);
        Assert.Equal("abc", response.Code);
        Assert.Equal("https://issuer.example.com", response.Iss);
        Assert.True(prompt.PromptAborted);
        Assert.Null(prompt.Shown);
    }

    [Fact]
    public async Task WaitForAuthorization_CancelledPrompt()
    {
        var prompt = new FakePrompt();
        var wait = McpOAuth.WaitForAuthorizationResponseAsync(
            _ => new TaskCompletionSource<OAuthCallback>(TaskCreationOptions.RunContinuationsAsynchronously).Task,
            "s1", Redirect, "/callback", prompt, CancellationToken.None);
        prompt.Answer("   ");
        await Assert.ThrowsAsync<McpSignInCancelledError>(async () => await wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task WaitForAuthorization_ErrorRedirect_AndMismatch()
    {
        var prompt = new FakePrompt();
        var wait = McpOAuth.WaitForAuthorizationResponseAsync(
            _ => new TaskCompletionSource<OAuthCallback>(TaskCreationOptions.RunContinuationsAsynchronously).Task,
            "s1", Redirect, "/callback", prompt, CancellationToken.None);
        prompt.Answer($"{Redirect}?error=access_denied&error_description=User+denied&state=s1");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await wait.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("User denied", error.Message);

        var second = new FakePrompt();
        var wait2 = McpOAuth.WaitForAuthorizationResponseAsync(
            _ => new TaskCompletionSource<OAuthCallback>(TaskCreationOptions.RunContinuationsAsynchronously).Task,
            "s1", Redirect, "/callback", second, CancellationToken.None);
        second.Answer("http://127.0.0.1:5001/callback?code=a&state=s1");
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await wait2.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("The redirect URL does not match this sign-in's redirect URI", mismatch.Message);
    }

    // ─── Test doubles ────────────────────────────────────────────────────────

    /// <summary>A store whose loads are scripted and whose refresh lock can be gated.</summary>
    private sealed class FakeStore : McpOAuthServerStore
    {
        private readonly Queue<McpOAuthState?> loads = [];

        public Func<Task>? Gate { get; init; }

        public int LockEntries { get; private set; }

        public void Enqueue(McpOAuthState? state) => loads.Enqueue(state);

        public Task<McpOAuthState?> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(loads.Count > 0 ? loads.Dequeue() : null);

        public Task SaveAsync(McpOAuthState state, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task<T> WithRefreshLockAsync<T>(
            Func<Task<T>> fn, CancellationToken cancellationToken = default)
        {
            LockEntries++;
            if (Gate is not null) await Gate().ConfigureAwait(false);
            return await fn().ConfigureAwait(false);
        }
    }

    private sealed class FakePrompt : IMcpSignInPrompt
    {
        private readonly TaskCompletionSource<string?> answer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri? Shown { get; private set; }

        public bool PromptAborted { get; private set; }

        public void ShowAuthorizationUrl(Uri url) => Shown = url;

        public Task<string?> PromptForRedirectUrlAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken.Register(() =>
            {
                PromptAborted = true;
                answer.TrySetResult(null);
            });
            return answer.Task;
        }

        public void Answer(string? value) => answer.TrySetResult(value);
    }

    private sealed class FakeBrowser
    {
        private readonly TaskCompletionSource<OAuthCallback> callback =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token { get; private set; }

        public Task<OAuthCallback> Wait(CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            return callback.Task;
        }

        public void Callback(string code, string state, string? iss = null)
            => callback.TrySetResult(new OAuthCallback(code, state, iss));
    }
}
