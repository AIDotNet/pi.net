using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// Anthropic 工作负载身份联合的 provider 环境解析。对应 TS
/// <c>anthropic-federation.test.ts</c>：所需变量、可选变量、错误优先级。
/// </summary>
public class AnthropicFederationAuthTests
{
    private sealed class EnvContext(IReadOnlyDictionary<string, string> env) : IAuthContext
    {
        public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(env.TryGetValue(name, out var value) ? value : null);

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private static readonly Dictionary<string, string> FederationEnv = new()
    {
        ["ANTHROPIC_FEDERATION_RULE_ID"] = "fdrl_test",
        ["ANTHROPIC_ORGANIZATION_ID"] = "org-test",
        ["ANTHROPIC_SERVICE_ACCOUNT_ID"] = "svac_test",
        ["ANTHROPIC_IDENTITY_TOKEN_FILE"] = "/tmp/identity.jwt",
    };

    private static Task<AuthResult?> Resolve(IReadOnlyDictionary<string, string> env)
        => new AnthropicApiKeyAuth().ResolveAsync(
            null, new EnvContext(env), CancellationToken.None);

    [Fact]
    public async Task ResolvesFederationVariablesAsProviderEnvWithNoRequestAuth()
    {
        var result = await Resolve(FederationEnv);

        Assert.NotNull(result);
        Assert.Null(result!.Auth.ApiKey);
        Assert.Null(result.Auth.Headers);
        Assert.Equal("workload identity federation", result.Source);
        Assert.NotNull(result.Env);
        foreach (var (name, value) in FederationEnv) Assert.Equal(value, result.Env![name]);
    }

    [Fact]
    public async Task PassesOptionalWorkspaceThrough()
    {
        var env = new Dictionary<string, string>(FederationEnv)
        {
            ["ANTHROPIC_WORKSPACE_ID"] = "wrkspc_test",
        };

        var result = await Resolve(env);

        Assert.Equal("wrkspc_test", result!.Env!["ANTHROPIC_WORKSPACE_ID"]);
    }

    [Fact]
    public async Task IsNotConfiguredWhenARequiredVariableIsMissing()
    {
        foreach (var omitted in new[]
                 {
                     "ANTHROPIC_FEDERATION_RULE_ID",
                     "ANTHROPIC_ORGANIZATION_ID",
                     "ANTHROPIC_IDENTITY_TOKEN_FILE",
                 })
        {
            var env = new Dictionary<string, string>(FederationEnv);
            env.Remove(omitted);

            Assert.Null(await Resolve(env));
        }
    }

    [Fact]
    public async Task TreatsServiceAccountAsOptional()
    {
        var env = new Dictionary<string, string>(FederationEnv);
        env.Remove("ANTHROPIC_SERVICE_ACCOUNT_ID");

        var result = await Resolve(env);

        Assert.NotNull(result);
        Assert.Equal("workload identity federation", result!.Source);
        Assert.False(result.Env!.ContainsKey("ANTHROPIC_SERVICE_ACCOUNT_ID"));
    }

    [Fact]
    public async Task KeepsApiKeyAndAuthTokenPrecedenceOverFederation()
    {
        var withKey = await Resolve(new Dictionary<string, string>(FederationEnv)
        {
            ["ANTHROPIC_API_KEY"] = "api-key",
        });
        Assert.Equal("api-key", withKey!.Auth.ApiKey);
        Assert.Equal("ANTHROPIC_API_KEY", withKey.Source);

        var withToken = await Resolve(new Dictionary<string, string>(FederationEnv)
        {
            ["ANTHROPIC_AUTH_TOKEN"] = "auth-token",
        });
        Assert.Null(withToken!.Auth.ApiKey);
        Assert.Equal("Bearer auth-token", withToken.Auth.Headers!["Authorization"]);
        Assert.Equal("ANTHROPIC_AUTH_TOKEN", withToken.Source);
    }
}

/// <summary>
/// 联邦令牌交换的 wire 测试：模拟 <c>/v1/oauth/token</c> 与 <c>/v1/messages</c>，
/// 对照 TS <c>anthropic-federation-sdk.test.ts</c> 的交换次数、Bearer 头与缓存语义。
/// </summary>
public sealed class AnthropicFederationWireTests : IDisposable
{
    private readonly string _directory;
    private readonly string _identityFile;

    public AnthropicFederationWireTests()
    {
        _directory = Directory.CreateTempSubdirectory("pi-anthropic-federation-").FullName;
        _identityFile = Path.Combine(_directory, "identity.jwt");
        File.WriteAllText(_identityFile, "header.payload.signature\n");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private Dictionary<string, string> FederationEnv(string? workspace = null, string? serviceAccount = null)
    {
        var env = new Dictionary<string, string>
        {
            ["ANTHROPIC_FEDERATION_RULE_ID"] = "fdrl_test",
            ["ANTHROPIC_ORGANIZATION_ID"] = "org-test",
            ["ANTHROPIC_IDENTITY_TOKEN_FILE"] = _identityFile,
        };
        if (workspace is not null) env["ANTHROPIC_WORKSPACE_ID"] = workspace;
        if (serviceAccount is not null) env["ANTHROPIC_SERVICE_ACCOUNT_ID"] = serviceAccount;
        return env;
    }

    private static ModelSpec Spec(
        string provider = "anthropic", string baseUrl = "https://api.anthropic.com") => new()
    {
        Id = "claude-test",
        Name = "Claude Test",
        Api = "anthropic-messages",
        Provider = provider,
        BaseUrl = baseUrl,
        Reasoning = false,
        Input = [ModelInput.Text],
        ContextWindow = 100_000,
        MaxTokens = 4_096,
    };

    private static async Task<AssistantMessage> RunAsync(
        ModelSpec model,
        HttpClient client,
        IReadOnlyDictionary<string, string>? env = null,
        string? apiKey = null,
        IReadOnlyDictionary<string, string?>? headers = null)
    {
        var stream = AnthropicMessages.Stream(
            model,
            new TranscriptContext([Messages.UserText("Hello")], null),
            new SimpleStreamOptions(ApiKey: apiKey)
            {
                Env = env,
                Headers = headers,
                MaxRetries = 0,
            },
            client);
        return await stream.WaitForDoneAsync();
    }

    [Fact]
    public async Task ExchangesTheIdentityTokenOnceAcrossRequests()
    {
        using var handler = new FederationHandler();
        using var client = new HttpClient(handler);

        for (var i = 0; i < 3; i++)
        {
            var message = await RunAsync(Spec(), client, FederationEnv());
            Assert.Equal(StopReason.Stop, message.StopReason);
        }

        Assert.Equal(1, handler.TokenExchanges);
        var messages = handler.Requests.Where(request => request.Path == "/v1/messages").ToList();
        Assert.Equal(3, messages.Count);
        foreach (var request in messages)
        {
            Assert.Equal("Bearer federated-token-1", request.Headers["Authorization"]);
            Assert.False(request.Headers.ContainsKey("x-api-key"));
        }
    }

    [Fact]
    public async Task SendsTheJwtBearerExchangeShape()
    {
        using var handler = new FederationHandler();
        using var client = new HttpClient(handler);

        var message = await RunAsync(
            Spec(), client, FederationEnv(workspace: "wrkspc_test", serviceAccount: "svac_test"));

        Assert.Equal(StopReason.Stop, message.StopReason);
        var exchange = Assert.Single(handler.Requests, request => request.Path == "/v1/oauth/token");
        Assert.Equal("POST", exchange.Method);
        Assert.Contains("application/json", exchange.Headers["Content-Type"]);
        Assert.Equal("oauth-2025-04-20,oidc-federation-2026-04-01", exchange.Headers["anthropic-beta"]);

        var body = JsonNode.Parse(exchange.Body!)!.AsObject();
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", body["grant_type"]!.GetValue<string>());
        Assert.Equal("header.payload.signature", body["assertion"]!.GetValue<string>());
        Assert.Equal("fdrl_test", body["federation_rule_id"]!.GetValue<string>());
        Assert.Equal("org-test", body["organization_id"]!.GetValue<string>());
        Assert.Equal("svac_test", body["service_account_id"]!.GetValue<string>());
        Assert.Equal("wrkspc_test", body["workspace_id"]!.GetValue<string>());

        var messageRequest = Assert.Single(handler.Requests, request => request.Path == "/v1/messages");
        Assert.Equal("Bearer federated-token-1", messageRequest.Headers["Authorization"]);
        var features = messageRequest.Headers["anthropic-beta"].Split(',');
        Assert.Contains("oauth-2025-04-20", features);
        Assert.DoesNotContain("claude-code-20250219", features);
        Assert.False(messageRequest.Headers.ContainsKey("x-app"));
        Assert.False(JsonNode.Parse(messageRequest.Body!)!.AsObject().ContainsKey("betas"));
    }

    [Fact]
    public async Task OmitsOptionalIdentifiersWhenAbsent()
    {
        using var handler = new FederationHandler();
        using var client = new HttpClient(handler);

        await RunAsync(Spec(), client, FederationEnv());

        var exchange = Assert.Single(handler.Requests, request => request.Path == "/v1/oauth/token");
        var body = JsonNode.Parse(exchange.Body!)!.AsObject();
        Assert.False(body.ContainsKey("service_account_id"));
        Assert.False(body.ContainsKey("workspace_id"));
    }

    [Fact]
    public async Task ApiKeyWinsOverFederation()
    {
        using var handler = new FederationHandler();
        using var client = new HttpClient(handler);

        var message = await RunAsync(Spec(), client, FederationEnv(), apiKey: "sk-ant-api");

        Assert.Equal(StopReason.Stop, message.StopReason);
        Assert.Equal(0, handler.TokenExchanges);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("sk-ant-api", request.Headers["x-api-key"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.False(request.Headers.ContainsKey("anthropic-beta"));
    }

    [Fact]
    public async Task HeaderOwnedAuthWinsOverFederation()
    {
        using var handler = new FederationHandler();
        using var client = new HttpClient(handler);

        var message = await RunAsync(
            Spec(), client, FederationEnv(),
            headers: new Dictionary<string, string?> { ["Authorization"] = "Bearer auth-token" });

        Assert.Equal(StopReason.Stop, message.StopReason);
        Assert.Equal(0, handler.TokenExchanges);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer auth-token", request.Headers["Authorization"]);
        Assert.False(request.Headers.ContainsKey("x-api-key"));
    }

    [Fact]
    public async Task DoesNotFederateOtherAnthropicMessagesProviders()
    {
        using var handler = new FederationHandler();
        using var client = new HttpClient(handler);

        var message = await RunAsync(
            Spec(provider: "kimi-coding", baseUrl: "https://api.kimi.com/coding"),
            client,
            FederationEnv());

        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains("No API key for provider: kimi-coding", message.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReExchangesTheIdentityTokenAfterExpiry()
    {
        using var handler = new FederationHandler
        {
            TokenResponder = number => TokenResponse($"federated-token-{number}", expiresIn: 0),
        };
        using var client = new HttpClient(handler);

        var first = await RunAsync(Spec(), client, FederationEnv());
        var second = await RunAsync(Spec(), client, FederationEnv());

        Assert.Equal(StopReason.Stop, first.StopReason);
        Assert.Equal(StopReason.Stop, second.StopReason);
        Assert.Equal(2, handler.TokenExchanges);
        var messages = handler.Requests.Where(request => request.Path == "/v1/messages").ToList();
        Assert.Equal("Bearer federated-token-1", messages[0].Headers["Authorization"]);
        Assert.Equal("Bearer federated-token-2", messages[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task ReReadsARotatedIdentityTokenFileOnEveryExchange()
    {
        using var handler = new FederationHandler
        {
            TokenResponder = number => TokenResponse($"federated-token-{number}", expiresIn: 0),
        };
        using var client = new HttpClient(handler);

        await RunAsync(Spec(), client, FederationEnv());
        File.WriteAllText(_identityFile, "rotated.payload.signature");
        await RunAsync(Spec(), client, FederationEnv());

        var exchanges = handler.Requests.Where(request => request.Path == "/v1/oauth/token").ToList();
        Assert.Equal(2, exchanges.Count);
        Assert.Equal("header.payload.signature",
            JsonNode.Parse(exchanges[0].Body!)!["assertion"]!.GetValue<string>());
        Assert.Equal("rotated.payload.signature",
            JsonNode.Parse(exchanges[1].Body!)!["assertion"]!.GetValue<string>());
    }

    [Fact]
    public async Task CoalescesConcurrentExchanges()
    {
        using var handler = new FederationHandler { GateTokenEndpoint = true };
        using var client = new HttpClient(handler);

        var runs = Enumerable.Range(0, 3)
            .Select(_ => RunAsync(Spec(), client, FederationEnv()))
            .ToList();
        await handler.FirstTokenRequest.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        handler.ReleaseTokenEndpoint();

        var messages = await Task.WhenAll(runs);

        Assert.All(messages, message => Assert.Equal(StopReason.Stop, message.StopReason));
        Assert.Equal(1, handler.TokenExchanges);
        var requests = handler.Requests.Where(request => request.Path == "/v1/messages").ToList();
        Assert.Equal(3, requests.Count);
        Assert.All(requests, request =>
            Assert.Equal("Bearer federated-token-1", request.Headers["Authorization"]));
    }

    [Fact]
    public async Task SurfacesTokenEndpointErrorsWithoutEchoingTheAssertion()
    {
        using var handler = new FederationHandler
        {
            TokenResponder = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"error":"invalid_grant","error_description":"rule mismatch","access_token":"leak"}""",
                    Encoding.UTF8,
                    "application/json"),
            },
        };
        using var client = new HttpClient(handler);

        var message = await RunAsync(Spec(), client, FederationEnv());

        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains("Token exchange failed with status 401", message.ErrorMessage);
        Assert.Contains("invalid_grant", message.ErrorMessage);
        Assert.Contains("ANTHROPIC_WORKSPACE_ID", message.ErrorMessage);
        Assert.DoesNotContain("header.payload.signature", message.ErrorMessage);
        Assert.DoesNotContain("leak", message.ErrorMessage);
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/v1/messages");
    }

    [Theory]
    [InlineData("not-json", "non-JSON response")]
    [InlineData("""{"expires_in":3600}""", "missing access_token")]
    [InlineData("""{"access_token":"token","expires_in":3600,"token_type":"mac"}""", "unsupported token_type")]
    [InlineData("""{"access_token":"token"}""", "missing required fields")]
    public async Task RejectsMalformedTokenResponses(string body, string expected)
    {
        using var handler = new FederationHandler
        {
            TokenResponder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            },
        };
        using var client = new HttpClient(handler);

        var message = await RunAsync(Spec(), client, FederationEnv());

        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains(expected, message.ErrorMessage);
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/v1/messages");
    }

    [Fact]
    public async Task AcceptsStringExpiryAndBearerTokenType()
    {
        using var handler = new FederationHandler
        {
            TokenResponder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"token","expires_in":"3600","token_type":"Bearer"}""",
                    Encoding.UTF8,
                    "application/json"),
            },
        };
        using var client = new HttpClient(handler);

        var message = await RunAsync(Spec(), client, FederationEnv());

        Assert.Equal(StopReason.Stop, message.StopReason);
        var request = Assert.Single(handler.Requests, item => item.Path == "/v1/messages");
        Assert.Equal("Bearer token", request.Headers["Authorization"]);
    }

    [Fact]
    public async Task RejectsCleartextTokenEndpointsOutsideLoopback()
    {
        using var handler = new FederationHandler();
        using var client = new HttpClient(handler);

        var message = await RunAsync(Spec(baseUrl: "http://api.example.com"), client, FederationEnv());

        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains("Refusing to send credential over non-https token endpoint", message.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RejectsAnEmptyIdentityTokenFile()
    {
        File.WriteAllText(_identityFile, "  \n");
        using var handler = new FederationHandler();
        using var client = new HttpClient(handler);

        var message = await RunAsync(Spec(), client, FederationEnv());

        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains("is empty", message.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RejectsIdentityTokensLargerThanTheAssertionLimit()
    {
        File.WriteAllText(_identityFile, new string('a', 16 * 1024 + 1));
        using var handler = new FederationHandler();
        using var client = new HttpClient(handler);

        var message = await RunAsync(Spec(), client, FederationEnv());

        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Contains("exceeds the 16 KiB assertion limit", message.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage TokenResponse(string token, double expiresIn)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["access_token"] = token,
                    ["expires_in"] = expiresIn,
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage SseResponse()
    {
        var body = string.Join(
            "\n\n",
            "event: message_start\ndata: "
            + """{"type":"message_start","message":{"usage":{"input_tokens":1,"output_tokens":0}}}""",
            "event: message_delta\ndata: "
            + """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}""",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}") + "\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };
    }

    private sealed record RecordedRequest(
        string Method,
        string Path,
        string? Body,
        Dictionary<string, string> Headers);

    private sealed class FederationHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int TokenExchanges { get; private set; }

        public List<RecordedRequest> Requests { get; } = [];

        public Func<int, HttpResponseMessage> TokenResponder { get; init; } =
            number => TokenResponse($"federated-token-{number}", expiresIn: 3600);

        public bool GateTokenEndpoint { get; init; }

        public TaskCompletionSource FirstTokenRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseTokenEndpoint() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers)
            {
                headers[name] = string.Join(",", values);
            }
            if (request.Content?.Headers is { } contentHeaders)
            {
                foreach (var (name, values) in contentHeaders) headers[name] = string.Join(",", values);
            }

            var path = request.RequestUri!.AbsolutePath;
            int exchangeNumber;
            lock (_gate)
            {
                Requests.Add(new RecordedRequest(request.Method.Method, path, body, headers));
                exchangeNumber = path == "/v1/oauth/token" ? ++TokenExchanges : 0;
            }

            if (path == "/v1/oauth/token")
            {
                FirstTokenRequest.TrySetResult();
                if (GateTokenEndpoint) await _release.Task.ConfigureAwait(false);
                return TokenResponder(exchangeNumber);
            }
            return SseResponse();
        }
    }
}

/// <summary>
/// Models 认证编排的端到端验证：<c>authContext</c> 解析出的联邦 env 必须一路传到
/// Anthropic 适配器。对应 TS <c>threads authContext federation variables through Models</c>。
/// </summary>
public sealed class AnthropicFederationModelsTests : IDisposable
{
    private sealed class EnvContext(IReadOnlyDictionary<string, string> env) : IAuthContext
    {
        public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(env.TryGetValue(name, out var value) ? value : null);

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private readonly string _directory;
    private readonly string _identityFile;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _baseUrl;
    private int _tokenExchanges;

    public AnthropicFederationModelsTests()
    {
        _directory = Directory.CreateTempSubdirectory("pi-anthropic-federation-models-").FullName;
        _identityFile = Path.Combine(_directory, "identity.jwt");
        File.WriteAllText(_identityFile, "header.payload.signature");

        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _baseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{_baseUrl}/v1/oauth/token/");
        _listener.Prefixes.Add($"{_baseUrl}/v1/messages/");
        _listener.Start();
        _ = ServeAsync();
    }

    public string? MessagesAuthorization { get; private set; }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }
            try
            {
                var path = context.Request.Url!.AbsolutePath;
                if (path == "/v1/oauth/token")
                {
                    Interlocked.Increment(ref _tokenExchanges);
                    var json = """{"access_token":"federated-token","expires_in":3600}""";
                    var bytes = Encoding.UTF8.GetBytes(json);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                else
                {
                    MessagesAuthorization = context.Request.Headers["Authorization"];
                    var events = new[]
                    {
                        "event: message_start\ndata: "
                        + """{"type":"message_start","message":{"usage":{"input_tokens":1,"output_tokens":0}}}""" + "\n\n",
                        "event: message_delta\ndata: "
                        + """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}""" + "\n\n",
                        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
                    };
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/event-stream";
                    foreach (var @event in events)
                    {
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(@event));
                    }
                }
                context.Response.Close();
            }
            catch
            {
                // 客户端断开或监听器关闭。
            }
        }
    }

    [Fact]
    public async Task ThreadsFederationEnvThroughModels()
    {
        var models = new Pi.Ai.Models.Models(new CreateModelsOptions
        {
            AuthContext = new EnvContext(new Dictionary<string, string>
            {
                ["ANTHROPIC_FEDERATION_RULE_ID"] = "fdrl_test",
                ["ANTHROPIC_ORGANIZATION_ID"] = "org-test",
                ["ANTHROPIC_IDENTITY_TOKEN_FILE"] = _identityFile,
            }),
        });
        models.SetProvider(Anthropic.Provider());

        var model = new ModelSpec
        {
            Id = "claude-test",
            Name = "Claude Test",
            Api = "anthropic-messages",
            Provider = "anthropic",
            BaseUrl = _baseUrl,
            Reasoning = false,
            Input = [ModelInput.Text],
            ContextWindow = 100_000,
            MaxTokens = 4_096,
        };

        var message = await models.CompleteSimpleAsync(model, [Messages.UserText("Hello")]);

        Assert.Equal(StopReason.Stop, message.StopReason);
        Assert.Equal(1, Volatile.Read(ref _tokenExchanges));
        Assert.Equal("Bearer federated-token", MessagesAuthorization);
    }
}
