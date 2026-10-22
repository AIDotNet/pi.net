// ============================================================================
// MCP OAuth sign-in — port of extensions/mcp/oauth.ts (P4b-2)
// ============================================================================
//
// OAuth sign-in for remote MCP servers.
//
// Connections never start a browser flow on their own. They send the stored access token and, after
// a 401, try the stored refresh token. When that is not possible they fail with
// `McpOAuthAuthorizationRequiredError`, and the user signs in through `/mcp`, which runs the
// authorization code flow (PKCE, a Client ID Metadata Document or dynamic client registration)
// against a loopback callback.
//
// Credentials live in `<agent-dir>/mcp-auth.json`, keyed by server name and URL.

using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Pi.Mcp;
using Pi.Mcp.OAuth;

namespace Pi.CodingAgent.Extensions.Mcp;

/// <summary>How an OAuth client is identified: dynamic client registration or a Client ID Metadata Document.</summary>
public enum McpClientRegistration
{
    /// <summary>Dynamic client registration (RFC 7591).</summary>
    Dcr,

    /// <summary>Client ID Metadata Document.</summary>
    Cimd,
}

/// <summary>Where the loopback callback server listens and the redirect URI it serves. Port of <c>CallbackSettings</c>.</summary>
internal sealed record McpCallbackSettings(
    string Host,
    string RedirectHost,
    int? Port,
    string Path,
    string? FixedRedirectUrl);

/// <summary>The <c>code</c> (and <c>iss</c>) of an authorization response. Port of <c>AuthorizationResponse</c>.</summary>
internal sealed record AuthorizationResponse(string Code, string? Iss);

/// <summary>Resolved OAuth settings of one MCP server. Port of <c>McpOAuthSettings</c>.</summary>
public sealed record McpOAuthSettings
{
    public string? ClientId { get; init; }

    /// <summary>Already resolved.</summary>
    public string? ClientSecret { get; init; }

    public int? CallbackPort { get; init; }

    /// <summary>Loopback redirect URI; see <c>McpOAuthConfig.callbackUrl</c>.</summary>
    public string? CallbackUrl { get; init; }

    /// <summary>Scopes to request, separated by spaces.</summary>
    public string? Scope { get; init; }

    /// <summary><c>client_name</c> for dynamic client registration. Default: <see cref="Config.AppName"/>.</summary>
    public string? ClientName { get; init; }

    /// <summary>See <c>McpOAuthConfig.clientRegistration</c>.</summary>
    public McpClientRegistration? ClientRegistration { get; init; }

    /// <summary>See <c>McpOAuthConfig.authServerMetadataUrl</c>.</summary>
    public Uri? AuthServerMetadataUrl { get; init; }
}

/// <summary>Per-server OAuth state store plus the refresh lock. Port of <c>McpOAuthServerStore</c>.</summary>
public interface McpOAuthServerStore : McpOAuthStateStore
{
    /// <summary>Run <paramref name="fn"/> while no other process refreshes the server's tokens.</summary>
    Task<T> WithRefreshLockAsync<T>(Func<Task<T>> fn, CancellationToken cancellationToken = default);
}

/// <summary>Auth provider for MCP connections. Port of <c>McpAuthProvider</c>.</summary>
public interface McpAuthProvider : AuthProvider
{
    /// <summary>Resolves when no refresh is running, so shutdown does not drop rotated tokens before they are saved.</summary>
    Task SettledAsync(CancellationToken cancellationToken = default);
}

/// <summary>Show the authorization URL and read a pasted redirect URL. Port of <c>McpSignInPrompt</c>.</summary>
public interface IMcpSignInPrompt
{
    /// <summary>Show the authorization URL to the user and open it in a browser.</summary>
    void ShowAuthorizationUrl(Uri url);

    /// <summary>
    /// Ask for the redirect URL from the browser address bar, for when the browser cannot reach the
    /// loopback callback (for example over SSH). Resolves to <c>null</c> or an empty string when the user cancels.
    /// </summary>
    Task<string?> PromptForRedirectUrlAsync(CancellationToken cancellationToken);
}

/// <summary>Raised when the user cancels a sign-in. Port of <c>McpSignInCancelledError</c>.</summary>
public sealed class McpSignInCancelledError : Exception
{
    public McpSignInCancelledError()
        : base("Sign-in cancelled")
    {
    }
}

/// <summary>Options of <see cref="McpOAuth.CreateAuthProvider"/>. Port of the <c>createMcpAuthProvider</c> options.</summary>
public sealed record McpAuthProviderOptions
{
    public required string ServerUrl { get; init; }

    public required McpOAuthServerStore Store { get; init; }

    /// <summary>Only called when a refresh is needed, so a secret that fails to resolve fails the refresh instead of the whole connection setup.</summary>
    public required Func<McpOAuthSettings> Settings { get; init; }

    /// <summary>Receives the server's <c>WWW-Authenticate</c> challenge so sign-in can use its resource metadata URL and scope.</summary>
    public required Action<OAuthTypes.Challenge> OnChallenge { get; init; }
}

/// <summary>Options of <see cref="McpOAuth.SignInServerAsync"/>. Port of the <c>signInMcpServer</c> options.</summary>
public sealed record McpSignInOptions
{
    public required string ServerUrl { get; init; }

    public required McpOAuthStateStore Store { get; init; }

    public required McpOAuthSettings Settings { get; init; }

    public OAuthTypes.Challenge? Challenge { get; init; }

    public required IMcpSignInPrompt Prompt { get; init; }
}

/// <summary>
/// Module-level constants and the entry points of the MCP OAuth layer. Port of the module scope of
/// <c>extensions/mcp/oauth.ts</c> plus its <c>createMcpAuthProvider</c> / <c>signInMcpServer</c>.
/// </summary>
public static class McpOAuth
{
    private const string CallbackHost = "127.0.0.1";
    private const string CallbackPath = "/callback";

    /// <summary>Where pi.dev serves pi's Client ID Metadata Documents: <c>client.json</c> and <c>&lt;callback ID&gt;/client.json</c>.</summary>
    public const string ClientMetadataBaseUrl = "https://pi.dev/oauth";

    /// <summary>Redirect URI for refreshes when none is stored. Refreshing never redirect the user.</summary>
    public const string FallbackRedirectUrl = "http://127.0.0.1/callback";

    /// <summary>Access tokens this close to expiry are refreshed before they are sent.</summary>
    public const long RefreshSkewMs = 30_000;

    /// <summary>
    /// 每一次发往授权服务器的请求的上限：服务器不响应时既不能长期占着刷新锁、拖住关闭，
    /// 也不能让一次登录无限等待（上游 f10993bc7 把原 <c>RefreshRequestTimeoutMs</c> 拓宽到全部请求）。
    /// </summary>
    public const long OAuthRequestTimeoutMs = 15_000;

    /// <summary>A refresh lock that its holder stopped renewing (the process was killed) is taken over after this.</summary>
    public const long RefreshLockStaleMs = 20_000;

    /// <summary>How long to wait for another process's refresh: longer than a stale lock lives.</summary>
    public const long RefreshLockWaitMs = 25_000;

    public const long RefreshLockRetryMs = 100;

    private static readonly HttpClient SharedHttpClient = new();

    /// <summary>Stand-in for <c>globalThis.fetch</c>: one shared client, like the browser reuses connections.</summary>
    private static readonly McpFetch DefaultFetch = (request, cancellationToken) =>
        SharedHttpClient.SendAsync(request, cancellationToken);

    // JSON.stringify always writes "\n"; .NET 9+ defaults the writer to Environment.NewLine.
    private static readonly JsonSerializerOptions StatesJsonOptions = new() { WriteIndented = true, NewLine = "\n" };

    // ─── Pure helpers (TS module-level functions) ─────────────────────────────

    /// <summary>Port of <c>callbackSettings</c>.</summary>
    internal static McpCallbackSettings CallbackSettingsFrom(McpOAuthSettings settings)
    {
        var url = JsUrl.TryParse(settings.CallbackUrl ?? $"http://{CallbackHost}{CallbackPath}")
            ?? throw new ArgumentException("Invalid callback URL", nameof(settings));
        var address = url.Hostname;
        var port = url.Port.Length > 0 ? ParsePort(url.Port) : settings.CallbackPort;
        string? fixedRedirectUrl = null;
        // A configured URI with a port is sent exactly as written, since servers compare it as a string.
        if (url.Port.Length > 0) fixedRedirectUrl = settings.CallbackUrl;
        else if (port is not null)
        {
            url = url with { Port = port.Value.ToString(CultureInfo.InvariantCulture) };
            fixedRedirectUrl = JsUrl.Serialize(url);
        }

        return new McpCallbackSettings(
            // `localhost` is served on 127.0.0.1; browsers fall back to it when ::1 refuses.
            Host: address == "localhost" ? CallbackHost : address,
            RedirectHost: address,
            Port: port,
            Path: url.Pathname,
            FixedRedirectUrl: fixedRedirectUrl);
    }

    /// <summary>Scopes of both lists, each once. Port of <c>mergeScopes</c>.</summary>
    internal static string? MergeScopes(params string?[] scopes)
    {
        var merged = new List<string>();
        foreach (var scope in scopes)
        {
            if (scope is null) continue;
            foreach (var part in scope.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!merged.Contains(part)) merged.Add(part);
            }
        }

        return merged.Count > 0 ? string.Join(' ', merged) : null;
    }

    /// <summary>
    /// Keys of a server's state: by name and URL, so servers sharing a URL keep separate accounts, and the
    /// legacy key by URL alone, written by older versions. Port of <c>storeKeys</c>.
    /// </summary>
    internal static (string Key, string LegacyKey) StoreKeys(string name, string serverUrl)
    {
        var url = JsUrl.TryParse(serverUrl)
            ?? throw new ArgumentException($"Invalid MCP server URL: {serverUrl}", nameof(serverUrl));
        var legacyKey = JsUrl.Serialize(url);
        return ($"{McpServers.Namespace(name)}|{legacyKey}", legacyKey);
    }

    /// <summary>Port of <c>parseStates</c>: empty input, and JSON that is not an object, are no states.</summary>
    internal static JsonObject ParseStates(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new JsonObject();
        var parsed = JsonNode.Parse(content);
        return parsed as JsonObject ?? new JsonObject();
    }

    /// <summary>Port of <c>serializeStates</c>: two-space JSON and a trailing newline.</summary>
    internal static string SerializeStates(JsonObject states)
        => JsonSerializer.Serialize(states, StatesJsonOptions).ReplaceLineEndings("\n") + "\n";

    /// <summary>12 characters identifying an MCP server URL in callback paths, computed like Codex does. Port of <c>callbackId</c>.</summary>
    internal static string CallbackId(string serverUrl)
    {
        var url = JsUrl.TryParse(serverUrl)
            ?? throw new ArgumentException($"Invalid MCP server URL: {serverUrl}", nameof(serverUrl));
        var href = JsUrl.Serialize(url with { Hash = string.Empty });
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(href));
        return Base64Url(digest.AsSpan(0, 9).ToArray());
    }

    /// <summary>base64url without padding, like Node's <c>toString("base64url")</c>.</summary>
    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// pi's Client ID Metadata Document, for <see cref="McpClientRegistration.Cimd"/>, chosen like Codex chooses its own.
    /// Port of <c>clientMetadataDocument</c>.
    /// </summary>
    internal static OAuthFlow.ClientMetadataDocument ClientMetadataDocumentFor(
        string serverUrl, string redirectUrl, OAuthTypes.AuthorizationServerMetadata? metadata)
    {
        if (metadata is null
            || metadata.ClientIdMetadataDocumentSupported is not true
            || metadata.TokenEndpointAuthMethodsSupported?.Contains("none") is not true)
        {
            throw new InvalidOperationException(
                "The authorization server does not support Client ID Metadata Documents for public clients; remove oauth.clientRegistration \"cimd\"");
        }

        if (metadata.AuthorizationResponseIssParameterSupported is true)
            return new OAuthFlow.ClientMetadataDocument($"{ClientMetadataBaseUrl}/client.json", redirectUrl);
        var id = CallbackId(serverUrl);
        var redirect = new UriBuilder(redirectUrl) { Path = $"{CallbackPath}/{id}" };
        return new OAuthFlow.ClientMetadataDocument($"{ClientMetadataBaseUrl}/{id}/client.json", redirect.Uri.ToString());
    }

    /// <summary>Port of <c>registeredRedirectUrls</c>.</summary>
    internal static IReadOnlyList<string> RegisteredRedirectUrls(OAuthTypes.ClientInformation? client)
        => client?.RedirectUris ?? [];

    /// <summary>Port of <c>responseFromRedirectUrl</c>.</summary>
    internal static AuthorizationResponse ResponseFromRedirectUrl(string input, string state, string redirectUrl)
    {
        var url = JsUrl.TryParse(input.Trim())
            ?? throw new InvalidOperationException("Expected the full redirect URL from the browser address bar");
        var redirect = JsUrl.TryParse(redirectUrl)
            ?? throw new InvalidOperationException($"Invalid redirect URI: {redirectUrl}");
        // A server-specific redirect URI tells authorization servers apart, so it must match exactly.
        if (Origin(url) != Origin(redirect) || url.Pathname != redirect.Pathname)
            throw new InvalidOperationException("The redirect URL does not match this sign-in's redirect URI");
        var error = QueryParam(url.Query, "error");
        if (!string.IsNullOrEmpty(error))
            throw new InvalidOperationException(QueryParam(url.Query, "error_description") ?? error);
        if (QueryParam(url.Query, "state") != state)
            throw new InvalidOperationException("The redirect URL belongs to a different sign-in");
        var code = QueryParam(url.Query, "code");
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("The redirect URL does not contain an authorization code");
        return new AuthorizationResponse(code, QueryParam(url.Query, "iss"));
    }

    /// <summary>The origin of a parsed URL, like the WHATWG <c>origin</c> getter.</summary>
    internal static string Origin(JsUrlValue url)
    {
        var host = url.IsIpv6 ? $"[{url.Hostname}]" : url.Hostname;
        return $"{url.Protocol}//{host}{(url.Port.Length > 0 ? $":{url.Port}" : string.Empty)}";
    }

    /// <summary>One query parameter, decoded like <c>URLSearchParams.get</c>.</summary>
    internal static string? QueryParam(string query, string name)
    {
        var span = query.AsSpan();
        if (span.Length > 0 && span[0] == '?') span = span[1..];
        while (span.Length > 0)
        {
            string pair;
            var amp = span.IndexOf('&');
            if (amp < 0)
            {
                pair = span.ToString();
                span = default;
            }
            else
            {
                pair = span[..amp].ToString();
                span = span[(amp + 1)..];
            }

            var eq = pair.IndexOf('=');
            var key = DecodeQueryPart(eq < 0 ? pair : pair[..eq]);
            if (key == name) return DecodeQueryPart(eq < 0 ? string.Empty : pair[(eq + 1)..]);
        }

        return null;
    }

    private static string DecodeQueryPart(string value)
        => Uri.UnescapeDataString(value.Replace('+', ' '));

    /// <summary>
    /// The stored state prepared for a new sign-in (TS lines 481-496 of <c>signInMcpServer</c>): every
    /// sign-in gets a fresh <c>state</c> parameter, and a client that cannot use this redirect URI (or,
    /// with CIMD, any stored client) is dropped together with its tokens.
    /// </summary>
    internal static McpOAuthState? PrepareStoredStateForSignIn(
        McpOAuthState? stored, McpOAuthSettings settings, string redirectUrl, bool cimd)
    {
        if (stored is null) return null;
        var next = stored with { OAuthState = null };
        var keepClient = !string.IsNullOrEmpty(settings.ClientId)
            || (cimd
                ? next.ClientInformation is null
                : RegisteredRedirectUrls(next.ClientInformation).Contains(redirectUrl));
        if (!keepClient) next = next with { ClientInformation = null, Tokens = null, TokensExpireAt = null };
        return next;
    }

    /// <summary>The port of a URL as a number, or <c>null</c> when the URL has no port, like <c>Number(url.port) || undefined</c>.</summary>
    internal static int? JsUrlPort(string url)
    {
        var parsed = JsUrl.TryParse(url);
        if (parsed is null || parsed.Port.Length == 0) return null;
        return ParsePort(parsed.Port) is { } port && port != 0 ? port : null;
    }

    private static int? ParsePort(string port)
        => int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary>A stored state node, treating a JSON <c>null</c> as absent (TS truthiness of <c>states[key]</c>).</summary>
    private static JsonNode? StateNode(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.Null ? null : node;

    private static McpOAuthState? DeserializeState(JsonNode? node)
        => node is null ? null : node.Deserialize<McpOAuthState>();

    // ─── Credential store ─────────────────────────────────────────────────────

    /// <summary>
    /// Per-server OAuth state (client registration, tokens, pending PKCE verifier) in <c>mcp-auth.json</c>.
    /// Port of <c>McpOAuthCredentialStore</c>.
    /// </summary>
    public sealed class McpOAuthCredentialStore
    {
        private readonly IAuthStorageBackend _backend;

        /// <summary>Directory for the refresh lock files. Without one, refreshes are only serialized in this process.</summary>
        private readonly string? _lockDir;

        public McpOAuthCredentialStore(IAuthStorageBackend? backend = null, string? lockDir = null)
        {
            _backend = backend ?? new FileAuthStorageBackend(NodePath.Join(Config.GetAgentDir(), "mcp-auth.json"));
            _lockDir = backend is not null ? lockDir : Config.GetAgentDir();
        }

        public McpOAuthServerStore ForServer(string name, string serverUrl)
        {
            var (key, legacyKey) = StoreKeys(name, serverUrl);
            return new ServerStore(this, key, legacyKey);
        }

        /// <summary>
        /// The stored tokens of a server, for noticing sign-ins done by another process.
        /// Does not take over legacy state. Port of <c>McpOAuthCredentialStore.tokens</c>.
        /// </summary>
        public Task<OAuthTypes.OAuthTokens?> TokensAsync(
            string name, string serverUrl, CancellationToken cancellationToken = default)
        {
            var (key, legacyKey) = StoreKeys(name, serverUrl);
            return _backend.WithLockAsync(current =>
            {
                var states = ParseStates(current);
                return Task.FromResult(new LockResult<OAuthTypes.OAuthTokens?>(
                    DeserializeState(StateNode(states[key]) ?? StateNode(states[legacyKey]))?.Tokens));
            }, cancellationToken);
        }

        /// <summary>
        /// Returns whether credentials were stored for the server. Removes legacy state the server would
        /// take over. Port of <c>McpOAuthCredentialStore.remove</c>.
        /// </summary>
        public Task<bool> RemoveAsync(string name, string serverUrl, CancellationToken cancellationToken = default)
        {
            var (key, legacyKey) = StoreKeys(name, serverUrl);
            return _backend.WithLockAsync(current =>
            {
                var states = ParseStates(current);
                var stored = states.ContainsKey(key) ? key : states.ContainsKey(legacyKey) ? legacyKey : null;
                if (stored is null) return Task.FromResult(new LockResult<bool>(false));
                states.Remove(stored);
                return Task.FromResult(new LockResult<bool>(true, SerializeStates(states)));
            }, cancellationToken);
        }

        /// <summary>
        /// A lock file per server. When the process exits, the lock is removed; when it is killed, the lock
        /// goes stale because it is no longer renewed, and the next process takes it over.
        /// Port of <c>McpOAuthCredentialStore.withRefreshLock</c>.
        /// </summary>
        private async Task<T> WithRefreshLockAsync<T>(
            string key, Func<Task<T>> fn, CancellationToken cancellationToken)
        {
            if (_lockDir is null) return await fn().ConfigureAwait(false);
            ModeFile.CreateDirectory(_lockDir, ModeFile.OwnerAll);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
            var lockPath = NodePath.Join(_lockDir, $"mcp-auth-refresh-{hash}");
            var release = await NodeLock.AcquireAsync(
                lockPath, cancellationToken, onCompromised: null, staleMs: (int)RefreshLockStaleMs).ConfigureAwait(false);
            try
            {
                return await fn().ConfigureAwait(false);
            }
            finally
            {
                await release().ConfigureAwait(false);
            }
        }

        private sealed class ServerStore : McpOAuthServerStore
        {
            private readonly McpOAuthCredentialStore _owner;
            private readonly string _key;
            private readonly string _legacyKey;

            public ServerStore(McpOAuthCredentialStore owner, string key, string legacyKey)
            {
                _owner = owner;
                _key = key;
                _legacyKey = legacyKey;
            }

            public Task<McpOAuthState?> LoadAsync(CancellationToken cancellationToken = default)
                => _owner._backend.WithLockAsync(current =>
                {
                    var states = ParseStates(current);
                    var keyState = StateNode(states[_key]);
                    var legacyState = StateNode(states[_legacyKey]);
                    // The first server to load legacy state takes it over; others with the same URL sign in again.
                    if (keyState is not null || legacyState is null)
                        return Task.FromResult(new LockResult<McpOAuthState?>(DeserializeState(keyState)));
                    states[_key] = legacyState!.DeepClone();
                    states.Remove(_legacyKey);
                    return Task.FromResult(new LockResult<McpOAuthState?>(
                        DeserializeState(states[_key]), SerializeStates(states)));
                }, cancellationToken);

            public Task SaveAsync(McpOAuthState state, CancellationToken cancellationToken = default)
                => _owner._backend.WithLockAsync(current =>
                {
                    var states = ParseStates(current);
                    states[_key] = JsonSerializer.SerializeToNode(state);
                    return Task.FromResult(new LockResult<object?>(null, SerializeStates(states)));
                }, cancellationToken);

            public Task<T> WithRefreshLockAsync<T>(
                Func<Task<T>> fn, CancellationToken cancellationToken = default)
                => _owner.WithRefreshLockAsync(_key, fn, cancellationToken);
        }
    }

    // ─── Auth provider ────────────────────────────────────────────────────────

    /// <summary>Port of <c>createProvider</c>.</summary>
    internal static McpOAuthProvider CreateProvider(
        string serverUrl,
        McpOAuthStateStore store,
        McpOAuthSettings settings,
        string redirectUrl,
        Func<Uri, CancellationToken, Task> onRedirect)
        => new(
            serverUrl,
            redirectUrl,
            new OAuthTypes.ClientMetadata(RedirectUris: [], ClientName: settings.ClientName ?? Config.AppName),
            onRedirect,
            clientId: settings.ClientId,
            clientSecret: settings.ClientSecret,
            store: store,
            clientMetadataDocument: settings.ClientRegistration == McpClientRegistration.Cimd
                ? metadata => ClientMetadataDocumentFor(serverUrl, redirectUrl, metadata)
                : null);

    /// <summary>
    /// Auth provider for MCP connections: sends the stored access token and refreshes it when it is about
    /// to expire or after a 401. Throws <see cref="McpOAuthAuthorizationRequiredError"/> when the user has
    /// to sign in, including when the server asks for more scope (<c>insufficient_scope</c>).
    /// <para>
    /// Many servers rotate refresh tokens, so two refreshes with the same refresh token lose the grant.
    /// Requests in this process share one refresh, and other processes are kept out by the store's
    /// refresh lock, held from reading the tokens to saving new ones. Tokens that changed meanwhile
    /// (another process refreshed them, or the user signed in) are used without refreshing.
    /// </para>
    /// Port of <c>createMcpAuthProvider</c>.
    /// </summary>
    public static McpAuthProvider CreateAuthProvider(McpAuthProviderOptions options)
        => new McpAuthProviderImpl(options);

    private sealed class McpAuthProviderImpl(McpAuthProviderOptions options) : McpAuthProvider
    {
        private readonly object _gate = new();
        private Task? _refreshing;

        public async Task<string?> Token(CancellationToken cancellationToken = default)
        {
            await SettleRefreshingAsync(cancellationToken).ConfigureAwait(false);
            var state = await options.Store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var token = state?.Tokens?.AccessToken;
            var expired = state?.TokensExpireAt is { } tokensExpireAt
                && tokensExpireAt - RefreshSkewMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!expired || state?.Tokens?.RefreshToken is null) return token;
            // Failures fall through: the request goes out with the old token and a 401 decides what happens.
            try
            {
                await RefreshAsync(token, null, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // TS: refresh(token).catch(() => undefined)
            }

            return (await options.Store.LoadAsync(cancellationToken).ConfigureAwait(false))?.Tokens?.AccessToken;
        }

        public Func<UnauthorizedContext, CancellationToken, Task>? OnUnauthorized => HandleUnauthorizedAsync;

        public Task SettledAsync(CancellationToken cancellationToken = default)
            => SettleRefreshingAsync(cancellationToken);

        /// <summary>TS: <c>await refreshing?.catch(() =&gt; undefined)</c>.</summary>
        private async Task SettleRefreshingAsync(CancellationToken cancellationToken)
        {
            Task? refreshing;
            lock (_gate) refreshing = _refreshing;
            if (refreshing is null) return;
            try
            {
                await refreshing.ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A failed refresh is reported to the request that started it, not to waiters.
            }
        }

        private async Task HandleUnauthorizedAsync(UnauthorizedContext context, CancellationToken cancellationToken)
        {
            var header = context.Response.Headers.TryGetValues("WWW-Authenticate", out var values)
                ? values.FirstOrDefault()
                : null;
            var challenge = OAuthDiscovery.ParseWwwAuthenticate(header);
            options.OnChallenge(challenge);
            // A refresh keeps the granted scope, so more scope needs a new sign-in.
            if (challenge.Error == "insufficient_scope") throw new McpOAuthAuthorizationRequiredError();
            await RefreshAsync(context.Token, context.Fetch, challenge, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>TS: <c>refreshing ??= store.withRefreshLock(...).finally(() =&gt; refreshing = undefined)</c>.</summary>
        private Task RefreshAsync(
            string? staleToken, McpFetch? fetch, OAuthTypes.Challenge? challenge, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_refreshing is null) _refreshing = RunAsync();
                return _refreshing;
            }

            async Task RunAsync()
            {
                try
                {
                    await RefreshCoreAsync(staleToken, fetch, challenge, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate) _refreshing = null;
                }
            }
        }

        /// <summary>Replace <paramref name="staleToken"/>, the access token that expired or was rejected.</summary>
        private Task RefreshCoreAsync(
            string? staleToken, McpFetch? fetch, OAuthTypes.Challenge? challenge, CancellationToken cancellationToken)
            => options.Store.WithRefreshLockAsync(
                () => RefreshWithinLockAsync(staleToken, fetch, challenge, cancellationToken), cancellationToken);

        private async Task<bool> RefreshWithinLockAsync(
            string? staleToken, McpFetch? fetch, OAuthTypes.Challenge? challenge, CancellationToken cancellationToken)
        {
            {
                var state = await options.Store.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (state?.Tokens?.AccessToken != staleToken) return false;
                if (state?.Tokens?.RefreshToken is null) throw new McpOAuthAuthorizationRequiredError();
                var settings = options.Settings();
                var callback = CallbackSettingsFrom(settings);
                var registered = RegisteredRedirectUrls(state.ClientInformation);
                var redirectUrl = callback.FixedRedirectUrl
                    ?? (registered.Count > 0 ? registered[0] : null)
                    ?? FallbackRedirectUrl;
                var provider = CreateProvider(
                    options.ServerUrl, options.Store, settings, redirectUrl, (_, _) => Task.CompletedTask);
                var effectiveFetch = fetch ?? DefaultFetch;
                // Refreshes the tokens, or reports that a new sign-in is needed. 不可取消：服务器已经作答的刷新
                // 可能轮转了 refresh token，那个结果必须被保存下来。
                var result = await OAuthFlow.AuthorizeMcp(provider, new OAuthFlow.FlowOptions
                {
                    ServerUrl = options.ServerUrl,
                    ResourceMetadataUrl = challenge?.ResourceMetadataUrl,
                    AuthorizationServerMetadataUrl = settings.AuthServerMetadataUrl?.ToString(),
                    Scope = challenge?.Scope,
                    Fetch = TimedFetch(effectiveFetch),
                }, cancellationToken).ConfigureAwait(false);
                if (result == OAuthFlow.FlowResult.Redirect) throw new McpOAuthAuthorizationRequiredError();
                return true;
            }
        }
    }

    /// <summary>
    /// 给每次请求加 <see cref="OAuthRequestTimeoutMs"/> 上限（调用方自己的令牌继续生效）。
    /// 对应 TS <c>timedFetch</c>；刷新与登录共用同一份实现。
    /// </summary>
    internal static McpFetch TimedFetch(McpFetch? fetch = null)
    {
        var effective = fetch ?? DefaultFetch;
        return async (request, cancellationToken) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(OAuthRequestTimeoutMs));
            return await effective(request, timeout.Token).ConfigureAwait(false);
        };
    }

    // ─── Sign-in ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Sign in to an MCP server. Uses the stored refresh token when possible; otherwise runs the browser
    /// authorization code flow. Tokens are saved to <paramref name="options"/>.Store.
    /// Port of <c>signInMcpServer</c>.
    /// </summary>
    public static async Task SignInServerAsync(
        McpSignInOptions options, CancellationToken cancellationToken = default)
    {
        var serverUrl = options.ServerUrl;
        var store = options.Store;
        var settings = options.Settings;
        // 已经取消的登录立即失败，不必先开回调端口（上游 f10993bc7）。
        if (cancellationToken.IsCancellationRequested) throw new McpSignInCancelledError();
        var stored = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var stepUp = options.Challenge?.Error == "insufficient_scope";
        var callbackOptions = CallbackSettingsFrom(settings);
        // Reuse the port of the registered redirect URI so the registered client stays valid.
        var registered = RegisteredRedirectUrls(stored?.ClientInformation);
        var preferredPort = callbackOptions.Port
            ?? (registered.Count > 0 ? JsUrlPort(registered[0]) : null);
        var cimd = settings.ClientRegistration == McpClientRegistration.Cimd;
        IReadOnlyList<string> extraPaths = cimd
            ? [$"{CallbackPath}/{CallbackId(serverUrl)}"]
            : [];
        var callback = await ListenForCallbackAsync(
            callbackOptions, extraPaths, preferredPort, callbackOptions.Port is not null, cancellationToken)
            .ConfigureAwait(false);
        var redirectUrl = callbackOptions.FixedRedirectUrl ?? callback.RedirectUrl;
        try
        {
            var prepared = PrepareStoredStateForSignIn(stored, settings, redirectUrl, cimd);
            if (prepared is not null) await store.SaveAsync(prepared, cancellationToken).ConfigureAwait(false);

            Uri? authorizationUrl = null;
            var provider = CreateProvider(serverUrl, store, settings, redirectUrl,
                (url, _) =>
                {
                    authorizationUrl = url;
                    return Task.CompletedTask;
                });
            var flow = new OAuthFlow.FlowOptions
            {
                ServerUrl = serverUrl,
                ResourceMetadataUrl = options.Challenge?.ResourceMetadataUrl,
                AuthorizationServerMetadataUrl = settings.AuthServerMetadataUrl?.ToString(),
                // A server asking for more scope gets it on top of the configured scope and, since the challenge
                // may list only the missing scopes, on top of the scope granted so far.
                Scope = MergeScopes(
                    settings.Scope,
                    stepUp
                        ? OAuthFlow.StepUpScope(stored?.Tokens?.Scope, options.Challenge?.Scope)
                        : options.Challenge?.Scope),
                Fetch = TimedFetch(),
            };
            // A refresh keeps the granted scope; a server asking for more needs the browser flow.
            var skipRefresh = stepUp;
            if (await OAuthFlow.AuthorizeMcp(provider, flow with { SkipRefresh = skipRefresh }, cancellationToken)
                    .ConfigureAwait(false)
                == OAuthFlow.FlowResult.Authorized)
            {
                return;
            }

            if (authorizationUrl is null)
                throw new InvalidOperationException("OAuth flow did not produce an authorization URL");

            var state = await provider.State(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("OAuth flow did not produce a state parameter");
            // The flow picks the redirect URI, which may be specific to the MCP server.
            var authorizationRedirectUrl = QueryParam(authorizationUrl.Query, "redirect_uri") ?? redirectUrl;
            var redirectPath = JsUrl.TryParse(authorizationRedirectUrl)?.Pathname
                ?? throw new InvalidOperationException($"Invalid redirect URI: {authorizationRedirectUrl}");
            options.Prompt.ShowAuthorizationUrl(authorizationUrl);
            var response = await WaitForAuthorizationResponseAsync(
                token => callback.WaitForCallbackAsync(state, redirectPath, token),
                state, authorizationRedirectUrl, redirectPath, options.Prompt, cancellationToken)
                .ConfigureAwait(false);
            await OAuthFlow.AuthorizeMcp(
                provider, flow with { AuthorizationCode = response.Code, Iss = response.Iss }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // 被取消的请求以取消原因为失败原因；统一报成「登录已取消」（上游 f10993bc7）。
            throw new McpSignInCancelledError();
        }
        finally
        {
            await callback.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Listen on <paramref name="port"/>, or on a free port when it is taken and not <paramref name="required"/>. Port of <c>listenForCallback</c>.</summary>
    private static async Task<OAuthCallbackServer> ListenForCallbackAsync(
        McpCallbackSettings settings,
        IReadOnlyList<string> extraPaths,
        int? port,
        bool required,
        CancellationToken cancellationToken)
    {
        var options = new OAuthCallbackServerOptions
        {
            Host = settings.Host,
            RedirectHost = settings.RedirectHost,
            Path = settings.Path,
            ExtraPaths = extraPaths,
            RenderPage = page => page.Ok
                ? OAuthPage.SuccessHtml("Signed in to the MCP server. You may now close this page.")
                : OAuthPage.ErrorHtml(page.Message ?? string.Empty, page.Details),
        };
        try
        {
            return await OAuthCallbackServer.ListenAsync(options with { Port = port ?? 0 }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!required && port is not null)
        {
            return await OAuthCallbackServer.ListenAsync(options, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Wait for the browser callback or a pasted redirect URL, whichever comes first. Port of <c>waitForAuthorizationResponse</c>.</summary>
    internal static async Task<AuthorizationResponse> WaitForAuthorizationResponseAsync(
        Func<CancellationToken, Task<OAuthCallback>> waitForBrowser,
        string state,
        string redirectUrl,
        string redirectPath,
        IMcpSignInPrompt prompt,
        CancellationToken cancellationToken)
    {
        using var controller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // TS races the two promises directly; the callback carries the extra `state` nobody reads.
        var fromBrowser = FromBrowserAsync(waitForBrowser, controller.Token);
        var fromUser = PromptForRedirectUrlAsync(prompt, controller.Token, state, redirectUrl);
        try
        {
            var finished = await Task.WhenAny<AuthorizationResponse>(fromBrowser, fromUser)
                .ConfigureAwait(false);
            return await finished.ConfigureAwait(false);
        }
        finally
        {
            await controller.CancelAsync().ConfigureAwait(false);
            // The losing side rejects once the prompt is aborted or the callback server closes.
            Observe(fromBrowser);
            Observe(fromUser);
        }

        static async Task<AuthorizationResponse> FromBrowserAsync(
            Func<CancellationToken, Task<OAuthCallback>> waitForBrowser, CancellationToken cancellationToken)
        {
            var callback = await waitForBrowser(cancellationToken).ConfigureAwait(false);
            return new AuthorizationResponse(callback.Code, callback.Iss);
        }

        static async Task<AuthorizationResponse> PromptForRedirectUrlAsync(
            IMcpSignInPrompt prompt, CancellationToken cancellationToken, string state, string redirectUrl)
        {
            var input = await prompt.PromptForRedirectUrlAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(input)) throw new McpSignInCancelledError();
            return ResponseFromRedirectUrl(input, state, redirectUrl);
        }

        static void Observe(Task task) => _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
