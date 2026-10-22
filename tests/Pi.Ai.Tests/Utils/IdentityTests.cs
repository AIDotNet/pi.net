using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Utils;

/// <summary>
/// <c>utils/identity.ts</c> 测试（客户身份、UA 形态、provider 归属头，以及与 Models 请求选项的分层）。
/// </summary>
/// <remarks>
/// 详版 UA 的运行时段与平台取自运行时（上游是 <c>node/&lt;v&gt;</c>，端口是 <c>dotnet/&lt;v&gt;</c>），
/// 故对形状做断言而不钉死字面量；归属头与 OpenCode 会话头是可移植的，逐字断言。
/// </remarks>
public class IdentityTests
{
    private static ClientIdentityTarget Target(string provider, string baseUrl) => new(provider, baseUrl);

    private static readonly ClientIdentityTarget OpenRouter = Target("openrouter", "https://openrouter.ai/api/v1");
    private static readonly ClientIdentityTarget Nvidia = Target("nvidia", "https://example.test/v1");
    private static readonly ClientIdentityTarget OpenCode = Target("opencode", "https://opencode.ai/zen/v1");

    private static ClientIdentity WithPi => new() { Attribution = Identity.PiAttribution };
    private static ClientIdentity WithAcme(params string[] categories) => new()
    {
        Attribution = new ClientAttribution("acme", "https://acme.example", categories),
    };

    [Fact]
    public void FormatsUserAgents()
    {
        Assert.Matches(@"^pi/\d+\.\d+\.\d+ \(.+; dotnet/.+; .+\)$", Identity.FormatUserAgent());
        Assert.StartsWith("pi/9.9.9 (", Identity.FormatUserAgent(new ClientIdentity { Version = "9.9.9" }));
        Assert.Equal("pi", Identity.FormatUserAgent(new ClientIdentity { UserAgent = UserAgentMode.Short, Version = "9.9.9" }));
        Assert.Equal("acme/1.0", Identity.FormatUserAgent(new ClientIdentity { UserAgent = UserAgentMode.Custom("acme/1.0") }));
    }

    [Fact]
    public void MergesIdentitiesFieldByField()
    {
        Assert.Null(Identity.MergeClientIdentity(null, null));

        var merged = Identity.MergeClientIdentity(
            WithPi with { UserAgent = UserAgentMode.Short },
            new ClientIdentity { Version = "1.0.0" });
        Assert.Same(Identity.PiAttribution, merged!.Attribution);
        Assert.Same(UserAgentMode.Short, merged.UserAgent);
        Assert.Equal("1.0.0", merged.Version);

        // 显式 null 关闭归属：这是「未设置」与「置空」必须可区分的唯一原因。
        var disabled = Identity.MergeClientIdentity(WithPi, new ClientIdentity { Attribution = null });
        Assert.True(disabled!.AttributionSet);
        Assert.Null(disabled.Attribution);
    }

    [Fact]
    public void SendsNoAttributionUnlessEnabled()
    {
        Assert.Empty(Identity.GetAttributionHeaders(OpenRouter));
        Assert.Empty(Identity.GetAttributionHeaders(OpenRouter, new ClientIdentity { Attribution = null }));

        var headers = Identity.GetIdentityHeaders(OpenRouter);
        Assert.Equal(Identity.FormatUserAgent(), headers["User-Agent"]);
        Assert.Single(headers);
    }

    [Fact]
    public void AttributesOpenRouterRequests()
    {
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["HTTP-Referer"] = "https://pi.dev",
                ["X-OpenRouter-Title"] = "pi",
                ["X-OpenRouter-Categories"] = "cli-agent",
            },
            Identity.GetAttributionHeaders(OpenRouter, WithPi));

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["HTTP-Referer"] = "https://acme.example",
                ["X-OpenRouter-Title"] = "acme",
                ["X-OpenRouter-Categories"] = "cli-agent,cloud-agent",
            },
            Identity.GetAttributionHeaders(OpenRouter, WithAcme("cli-agent", "cloud-agent")));

        // 空分类不写该头。
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["HTTP-Referer"] = "https://acme.example",
                ["X-OpenRouter-Title"] = "acme",
            },
            Identity.GetAttributionHeaders(OpenRouter, WithAcme()));

        // 指向 OpenRouter 的自定义 provider，含不是合法 URL 的旧式 base URL。
        Assert.Equal(
            "pi",
            Identity.GetAttributionHeaders(Target("custom", "not-a-url-openrouter.ai"), WithPi)["X-OpenRouter-Title"]);
    }

    [Fact]
    public void AttributesNvidiaWithCapitalizedNameAndOpenCodeWithName()
    {
        Assert.Equal(
            new Dictionary<string, string> { ["X-BILLING-INVOKE-ORIGIN"] = "Pi" },
            Identity.GetAttributionHeaders(Nvidia, WithPi));
        Assert.Equal(
            new Dictionary<string, string> { ["X-BILLING-INVOKE-ORIGIN"] = "Acme" },
            Identity.GetAttributionHeaders(Target("custom-nim", "https://integrate.api.nvidia.com/v1"), WithAcme()));

        Assert.Equal(
            new Dictionary<string, string> { ["x-opencode-client"] = "pi" },
            Identity.GetAttributionHeaders(OpenCode, WithPi));
        Assert.Equal(
            new Dictionary<string, string> { ["x-opencode-client"] = "acme" },
            Identity.GetAttributionHeaders(Target("custom", "https://opencode.ai/zen/v1"), WithAcme()));
    }

    [Fact]
    public void DoesNotAttributeNvidiaModelsRoutedThroughGateways()
    {
        Assert.False(Identity.GetAttributionHeaders(OpenRouter, WithPi).ContainsKey("X-BILLING-INVOKE-ORIGIN"));
        Assert.Empty(Identity.GetAttributionHeaders(Target("vercel-ai-gateway", "https://ai-gateway.vercel.test/v1"), WithPi));
        // 恶意后缀不当作 NVIDIA 主机。
        Assert.Empty(Identity.GetAttributionHeaders(
            Target("other", "https://integrate.api.nvidia.com.evil.test/v1"), WithPi));
    }

    [Fact]
    public void OpenCodeSessionHeaderRequiresOpenCodeTargetAndSession()
    {
        Assert.Equal(
            new Dictionary<string, string> { ["x-opencode-session"] = "sess-1" },
            Identity.GetOpenCodeSessionHeaders(OpenCode, "sess-1"));
        Assert.Null(Identity.GetOpenCodeSessionHeaders(OpenCode, null));
        Assert.Null(Identity.GetOpenCodeSessionHeaders(OpenCode, string.Empty));
        Assert.Null(Identity.GetOpenCodeSessionHeaders(Target("other", "https://example.test/v1"), "sess-1"));
        Assert.NotNull(Identity.GetOpenCodeSessionHeaders(Target("opencode-go", "https://example.test/v1"), "sess-1"));
    }

    [Fact]
    public void ParsesPersistedIdentityJson()
    {
        var parsed = Identity.ParseClientIdentity(JsonNode.Parse(
            """{"userAgent":"short","attribution":{"name":"acme","url":"https://acme.example","categories":["cli-agent"]},"version":"2.0.0"}"""));
        Assert.Same(UserAgentMode.Short, parsed!.UserAgent);
        Assert.Equal("acme", parsed.Attribution!.Name);
        Assert.Equal(["cli-agent"], parsed.Attribution.Categories!);
        Assert.Equal("2.0.0", parsed.Version);

        Assert.Equal(
            "acme/1.0",
            Identity.FormatUserAgent(Identity.ParseClientIdentity(JsonNode.Parse("""{"userAgent":{"custom":"acme/1.0"}}"""))));

        var disabled = Identity.ParseClientIdentity(JsonNode.Parse("""{"attribution":null}"""));
        Assert.True(disabled!.AttributionSet);
        Assert.Null(disabled.Attribution);

        Assert.Null(Identity.ParseClientIdentity(null));
        Assert.Null(Identity.ParseClientIdentity(JsonNode.Parse("{}")));
    }

    // ------------------------------------------------------------------ 请求上的分层

    private static HttpResponseMessage SseResponse()
    {
        const string body = """
            data: {"id":"c1","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":null}]}

            data: {"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}

            data: [DONE]

            """;
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "text/event-stream"),
        };
    }

    private static async Task<Dictionary<string, string>> SendAsync(
        ModelSpec model, SimpleStreamOptions options)
    {
        var handler = new Pi.Ai.Tests.Auth.FakeHttpHandler(_ => SseResponse());
        using var client = new HttpClient(handler);
        var stream = Pi.Ai.Api.OpenAiCompletions.StreamSimple(
            model, new TranscriptContext([new UserMessage([new TextContent("hi")], 1)]), options, client);
        await foreach (var _ in stream) { }
        Assert.Single(handler.Requests);
        return handler.Requests[0].Headers;
    }

    [Fact]
    public async Task IdentitySitsBelowModelAndRequestHeaders()
    {
        var model = OpenRouterModel with { Headers = new Dictionary<string, string> { ["X-OpenRouter-Title"] = "model-title" } };
        var headers = await SendAsync(model, new SimpleStreamOptions(ApiKey: "key")
        {
            Identity = WithPi with { UserAgent = UserAgentMode.Short },
            Headers = new Dictionary<string, string?> { ["HTTP-Referer"] = null },
        });

        Assert.Equal("pi", headers["User-Agent"]);
        Assert.Equal("model-title", headers["X-OpenRouter-Title"]);
        // 请求头里的 null 表示删除身份写的归属头。
        Assert.False(headers.ContainsKey("HTTP-Referer"));
        Assert.Equal("cli-agent", headers["X-OpenRouter-Categories"]);
    }

    [Fact]
    public async Task ModelUserAgentWinsOverTheIdentityUserAgent()
    {
        var model = new ModelSpec
        {
            Id = "test-model",
            Name = "Test Model",
            Api = "openai-completions",
            Provider = "github-copilot",
            BaseUrl = "https://api.githubcopilot.test",
            Headers = new Dictionary<string, string> { ["User-Agent"] = "GitHubCopilotChat/0.35.0" },
        };
        var headers = await SendAsync(model, new SimpleStreamOptions(ApiKey: "key")
        {
            Identity = new ClientIdentity { UserAgent = UserAgentMode.Custom("acme") },
        });

        Assert.Equal("GitHubCopilotChat/0.35.0", headers["User-Agent"]);
    }

    // ------------------------------------------------------------------ Models 层身份分层

    /// <summary>记录收到的请求选项，并按最小契约产生终态。</summary>
    private sealed class CapturingProvider : IProvider
    {
        public string Id => "openrouter";

        public string Name => "openrouter";

        public string? BaseUrl => null;

        public IReadOnlyDictionary<string, object?>? Options { get; private set; }

        public IReadOnlyList<ModelSpec> GetModels() => [];

        public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
            => StreamSimple(model, context, options);

        public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
            IReadOnlyDictionary<string, object?>? options = null)
        {
            Options = options;
            var stream = new AssistantMessageEventStream();
            var message = new AssistantMessage(
                [new TextContent("ok")],
                Types.StopReason.Stop,
                UsageStats: new Usage(1, 1),
                Model: model.Id,
                Api: model.Api,
                Provider: model.Provider,
                Timestamp: 1);
            stream.Push(new AssistantMessageEvent.Start(message));
            stream.Push(new AssistantMessageEvent.Done(Types.StopReason.Stop, message));
            stream.End(message);
            return stream;
        }
    }

    private static ModelSpec OpenRouterModel => new()
    {
        Id = "test-model",
        Name = "Test Model",
        Api = "openai-completions",
        Provider = "openrouter",
        BaseUrl = "https://openrouter.ai/api/v1",
    };

    private static ClientIdentity? SentIdentity(CapturingProvider provider)
        => provider.Options is not null && provider.Options.TryGetValue("identity", out var value)
            ? value as ClientIdentity
            : null;

    [Fact]
    public async Task ModelsIdentityAppliesAndRequestIdentityWins()
    {
        var provider = new CapturingProvider();
        var models = new Models.Models(new CreateModelsOptions
        {
            Identity = new ClientIdentity
            {
                UserAgent = UserAgentMode.Short,
                Attribution = new ClientAttribution("models", "https://acme.example"),
            },
        });
        models.SetProvider(provider);

        await models.CompleteSimpleAsync(OpenRouterModel, []);
        Assert.Same(UserAgentMode.Short, SentIdentity(provider)!.UserAgent);
        Assert.Equal("models", SentIdentity(provider)!.Attribution!.Name);

        // 请求级身份逐字段覆盖 Models 的默认身份。
        await models.CompleteSimpleAsync(OpenRouterModel, [],
            new Dictionary<string, object?> { ["identity"] = new ClientIdentity { Attribution = null } });
        Assert.Null(SentIdentity(provider)!.Attribution);
        Assert.Same(UserAgentMode.Short, SentIdentity(provider)!.UserAgent);
    }

    [Fact]
    public async Task WithClientIdentityWrapsAndStaysMutable()
    {
        var provider = new CapturingProvider();
        var models = new Models.Models(new CreateModelsOptions
        {
            Identity = new ClientIdentity { UserAgent = UserAgentMode.Short },
        });
        models.SetProvider(provider);

        var wrapped = Identity.WithClientIdentity(models, new ClientIdentity
        {
            Attribution = new ClientAttribution("wrapper", "https://acme.example"),
        });
        Assert.NotNull(wrapped.Identity);
        Assert.Same(UserAgentMode.Short, wrapped.Identity!.UserAgent);
        Assert.Equal("wrapper", wrapped.Identity.Attribution!.Name);

        await wrapped.CompleteSimpleAsync(OpenRouterModel, []);
        Assert.Equal("wrapper", SentIdentity(provider)!.Attribution!.Name);
        Assert.Same(UserAgentMode.Short, SentIdentity(provider)!.UserAgent);

        // 请求级身份仍然最优先。
        await wrapped.CompleteSimpleAsync(OpenRouterModel, [],
            new Dictionary<string, object?> { ["identity"] = WithAcme() });
        Assert.Equal("acme", SentIdentity(provider)!.Attribution!.Name);

        // 视图与原对象共用 provider 表：视图上的增删互相可见。
        Assert.Same(provider, wrapped.GetProvider("openrouter"));
        wrapped.DeleteProvider("openrouter");
        Assert.Null(models.GetProvider("openrouter"));
    }
}
