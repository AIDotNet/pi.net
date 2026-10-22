using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Utils;

namespace Pi.Ai.Auth;

/// <summary>
/// Anthropic 工作负载身份联合配置。对应 TS <c>getAnthropicFederation()</c> 交给
/// Anthropic SDK 的 <c>config</c> 对象（仅 anthropic provider，且无其它认证时生效）。
/// </summary>
internal sealed record AnthropicFederationConfig(
    string OrganizationId,
    string FederationRuleId,
    string? ServiceAccountId,
    string? WorkspaceId,
    string IdentityTokenFile);

/// <summary>令牌交换失败。对应 SDK 的 <c>WorkloadIdentityError</c>。</summary>
internal sealed class AnthropicFederationException : Exception
{
    public AnthropicFederationException(string message, int? status = null, string? requestId = null)
        : base(message)
    {
        Status = status;
        RequestId = requestId;
    }

    public int? Status { get; }

    public string? RequestId { get; }
}

/// <summary>
/// 用 OIDC 身份令牌经 RFC 7523 jwt-bearer 换取 Anthropic 短期访问令牌。
/// 对应 <c>@anthropic-ai/sdk</c> 的 <c>lib/credentials</c>：每次换取都重新读取身份
/// 令牌文件（支持轮换），联合授权不返回 refresh token，到期后重新交换。
/// 令牌缓存按 HttpClient + baseUrl + 配置共享，对应 Pi TS 复用的 SDK client。
/// </summary>
internal static class AnthropicFederation
{
    internal const string TokenEndpoint = "/v1/oauth/token";
    internal const string OAuthBetaHeader = "oauth-2025-04-20";
    internal const string FederationBetaHeader = "oidc-federation-2026-04-01";
    internal const string GrantTypeJwtBearer = "urn:ietf:params:oauth:grant-type:jwt-bearer";

    private const int MaxAssertionBytes = 16 * 1024;
    private const int MaxTokenResponseBytes = 1 << 20;
    private const int MaxErrorBodyChars = 2000;

    private static readonly ConditionalWeakTable<HttpClient, ProviderCache> Caches = new();

    /// <summary>取（必要时交换/刷新）访问令牌。并发的强制刷新会合并为一次交换。</summary>
    public static Task<string> GetAccessTokenAsync(
        HttpClient client,
        string baseUrl,
        AnthropicFederationConfig config,
        CancellationToken cancellationToken = default)
    {
        var cache = Caches.GetValue(client, static _ => new ProviderCache());
        return cache.GetOrAdd(client, baseUrl, config).GetTokenAsync(cancellationToken);
    }

    private sealed record CacheKey(
        string BaseUrl,
        string OrganizationId,
        string FederationRuleId,
        string? ServiceAccountId,
        string? WorkspaceId,
        string IdentityTokenFile);

    private sealed class ProviderCache
    {
        private readonly object _gate = new();
        private readonly Dictionary<CacheKey, TokenCache> _caches = new();

        public TokenCache GetOrAdd(HttpClient client, string baseUrl, AnthropicFederationConfig config)
        {
            var key = new CacheKey(
                baseUrl.TrimEnd('/'),
                config.OrganizationId,
                config.FederationRuleId,
                config.ServiceAccountId,
                config.WorkspaceId,
                config.IdentityTokenFile);
            lock (_gate)
            {
                if (!_caches.TryGetValue(key, out var cache))
                {
                    cache = new TokenCache(client, key.BaseUrl, config);
                    _caches[key] = cache;
                }
                return cache;
            }
        }
    }

    private sealed record CachedToken(string AccessToken, double? ExpiresAt);

    /// <summary>
    /// 令牌缓存。刷新策略对应 SDK <c>TokenCache</c>：剩余 &gt;120s 直接用；30–120s
    /// 先返回旧令牌并后台刷新（失败后退避 5s）；≤30s 或已过期则阻塞刷新。
    /// </summary>
    private sealed class TokenCache
    {
        private const double AdvisoryRefreshThresholdSeconds = 120;
        private const double MandatoryRefreshThresholdSeconds = 30;
        private const double AdvisoryRefreshBackoffSeconds = 5;

        private readonly string _baseUrl;
        private readonly AnthropicFederationConfig _config;
        private readonly HttpClient _client;
        private readonly object _gate = new();
        private CachedToken? _cached;
        private Task<CachedToken>? _pending;
        private double _lastAdvisoryErrorAt = double.NegativeInfinity;

        public TokenCache(HttpClient client, string baseUrl, AnthropicFederationConfig config)
        {
            _client = client;
            _baseUrl = baseUrl;
            _config = config;
        }

        public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            CachedToken? cached;
            lock (_gate) cached = _cached;

            if (cached is null)
            {
                return (await RefreshAsync(cancellationToken).ConfigureAwait(false)).AccessToken;
            }
            if (cached.ExpiresAt is not { } expiresAt)
            {
                return cached.AccessToken;
            }

            var remaining = expiresAt - NowSeconds();
            if (remaining > AdvisoryRefreshThresholdSeconds) return cached.AccessToken;
            if (remaining > MandatoryRefreshThresholdSeconds)
            {
                StartAdvisoryRefresh();
                return cached.AccessToken;
            }
            return (await RefreshAsync(cancellationToken).ConfigureAwait(false)).AccessToken;
        }

        private Task<CachedToken> RefreshAsync(CancellationToken cancellationToken)
        {
            Task<CachedToken> pending;
            lock (_gate)
            {
                pending = _pending ??= StartExchange(advisory: false);
            }
            return pending.WaitAsync(cancellationToken);
        }

        private void StartAdvisoryRefresh()
        {
            lock (_gate)
            {
                if (_pending is not null) return;
                if (NowSeconds() - _lastAdvisoryErrorAt < AdvisoryRefreshBackoffSeconds) return;
                _pending = StartExchange(advisory: true);
            }
        }

        /// <summary>在锁内调用；返回的任务完成后清除 <see cref="_pending"/>。</summary>
        private Task<CachedToken> StartExchange(bool advisory)
        {
            var task = ExchangeAndCacheAsync(advisory);
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsFaulted) _ = completed.Exception;
                lock (_gate)
                {
                    if (ReferenceEquals(_pending, completed)) _pending = null;
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return task;
        }

        private async Task<CachedToken> ExchangeAndCacheAsync(bool advisory)
        {
            try
            {
                var token = await ExchangeAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_gate) _cached = token;
                return token;
            }
            catch (Exception)
            {
                if (advisory)
                {
                    // 顾问式刷新失败时继续供应旧令牌。
                    lock (_gate) _lastAdvisoryErrorAt = NowSeconds();
                }
                throw;
            }
        }

        private async Task<CachedToken> ExchangeAsync(CancellationToken cancellationToken)
        {
            RequireSecureTokenEndpoint(_baseUrl);
            var assertion = await ReadIdentityTokenAsync(cancellationToken).ConfigureAwait(false);
            if (assertion.Length > MaxAssertionBytes)
            {
                throw new AnthropicFederationException(
                    $"Identity token is {Math.Ceiling(assertion.Length / 1024d)} KiB, exceeds the 16 KiB assertion limit");
            }

            var body = new JsonObject
            {
                ["grant_type"] = GrantTypeJwtBearer,
                ["assertion"] = assertion,
                ["federation_rule_id"] = _config.FederationRuleId,
                ["organization_id"] = _config.OrganizationId,
            };
            if (!string.IsNullOrEmpty(_config.ServiceAccountId))
            {
                body["service_account_id"] = _config.ServiceAccountId;
            }
            if (!string.IsNullOrEmpty(_config.WorkspaceId))
            {
                body["workspace_id"] = _config.WorkspaceId;
            }

            var url = _baseUrl + TokenEndpoint;
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(
                "anthropic-beta", $"{OAuthBetaHeader},{FederationBetaHeader}");
            request.Headers.TryAddWithoutValidation("User-Agent", Identity.FormatUserAgent());

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new AnthropicFederationException(
                    $"Failed to reach token endpoint {url}: {error.Message}");
            }

            using (response)
            {
                var requestId = response.Headers.TryGetValues("Request-Id", out var values)
                    ? values.FirstOrDefault()
                    : null;
                var text = await ReadLimitedTextAsync(response, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    var redacted = RedactSensitive(text);
                    var hint = "";
                    if (status == (int)HttpStatusCode.Unauthorized)
                    {
                        var middle = string.IsNullOrEmpty(_config.WorkspaceId)
                            ? "If your federation rule is scoped to multiple workspaces, set the "
                              + "ANTHROPIC_WORKSPACE_ID environment variable, the 'workspace_id' config key, "
                              + "or the `workspaceId` option. "
                            : "";
                        hint = " Ensure your federation rule matches your identity token. " + middle
                            + "View your authentication events in the Workload identity page of Claude Console "
                            + "for more details.";
                    }
                    var requestIdSuffix = requestId is null ? "" : $" (request-id {requestId})";
                    throw new AnthropicFederationException(
                        $"Token exchange failed with status {status}{requestIdSuffix}: {redacted}{hint}",
                        status, requestId);
                }
                return ParseTokenResponse(text, (int)response.StatusCode, requestId);
            }
        }

        private async Task<string> ReadIdentityTokenAsync(CancellationToken cancellationToken)
        {
            string content;
            try
            {
                content = await File.ReadAllTextAsync(_config.IdentityTokenFile, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new AnthropicFederationException(
                    $"Failed to read identity token file at {_config.IdentityTokenFile}: {error.Message}");
            }

            var token = content.Trim();
            if (token.Length == 0)
            {
                throw new AnthropicFederationException(
                    $"Identity token file at {_config.IdentityTokenFile} is empty");
            }
            return token;
        }
    }

    private static CachedToken ParseTokenResponse(string text, int status, string? requestId)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new AnthropicFederationException(
                $"Token endpoint returned non-JSON response (status {status})", status, requestId);
        }

        if (node is not JsonObject data)
        {
            throw new AnthropicFederationException(
                $"Token endpoint response missing access_token: {RedactNode(node)}", status, requestId);
        }

        var accessToken = data["access_token"] is JsonValue accessValue
            && accessValue.TryGetValue<string>(out var value) ? value : null;
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new AnthropicFederationException(
                $"Token endpoint response missing access_token: {RedactNode(data)}", status, requestId);
        }

        var tokenType = data["token_type"] is JsonValue typeValue
            && typeValue.TryGetValue<string>(out var type) ? type : null;
        if (tokenType is not null && !tokenType.Equals("bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new AnthropicFederationException(
                $"Token endpoint response: unsupported token_type \"{tokenType}\" (want Bearer)",
                status, requestId);
        }

        if (!TryReadNumber(data["expires_in"], out var expiresIn) || !double.IsFinite(expiresIn))
        {
            throw new AnthropicFederationException(
                $"Token endpoint response missing required fields: {RedactNode(data)}", status, requestId);
        }

        return new CachedToken(accessToken, NowSeconds() + expiresIn);
    }

    /// <summary>拒绝把 JWT 发往明文端点；环回地址允许（本地开发）。对应 SDK 同名检查。</summary>
    private static void RequireSecureTokenEndpoint(string baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl)) return;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new AnthropicFederationException(
                $"Invalid token endpoint base URL \"{baseUrl}\"");
        }
        if (uri.Scheme == Uri.UriSchemeHttps) return;

        var host = uri.Host.Trim('[', ']').ToLowerInvariant();
        if (uri.Scheme == Uri.UriSchemeHttp
            && host is "localhost" or "127.0.0.1" or "::1")
        {
            return;
        }
        throw new AnthropicFederationException(
            $"Refusing to send credential over non-https token endpoint \"{baseUrl}\"");
    }

    private static async Task<string> ReadLimitedTextAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0;
        while (total < MaxTokenResponseBytes)
        {
            var read = await stream.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, MaxTokenResponseBytes - total)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
            total += read;
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>错误响应只保留 RFC 6749 §5.2 字段，避免回显 assertion/token。</summary>
    private static string RedactSensitive(string body)
    {
        try
        {
            return RedactNode(JsonNode.Parse(body));
        }
        catch (JsonException)
        {
            return body.Length <= MaxErrorBodyChars
                ? body
                : body[..MaxErrorBodyChars] + $"... <{body.Length - MaxErrorBodyChars} more chars>";
        }
    }

    private static string RedactNode(JsonNode? node)
    {
        if (node is not JsonObject obj) return "null";
        var safe = new JsonObject();
        foreach (var key in new[] { "error", "error_description", "error_uri" })
        {
            if (obj[key] is { } value) safe[key] = value.DeepClone();
        }
        return safe.ToJsonString();
    }

    private static bool TryReadNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue jsonValue) return false;
        if (jsonValue.TryGetValue<double>(out value)) return true;
        return jsonValue.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static double NowSeconds()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
}
