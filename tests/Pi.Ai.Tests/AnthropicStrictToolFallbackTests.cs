// ============================================================================
// Anthropic strict tool capacity fallback (#10409 / upstream d4d53612b).
//
// Anthropic rejects a request with 400 when the strict tool set exceeds its capacity (compiled
// grammar too large, too many strict tools, too many union-typed parameters). The request is
// retried once with `strict: "prefer"` tools sent on their original non-strict schemas, while
// `strict: "require"` tools stay strict. A stream that already started is never retried.
// ============================================================================

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests;

public sealed class AnthropicStrictToolFallbackTests : IDisposable
{
    private const string StrictLimitBody =
        """{"type":"error","error":{"type":"invalid_request_error","message":"Too many strict tools (27) for this model"}}""";

    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _bodies = [];

    public AnthropicStrictToolFallbackTests()
    {
        var port = TcpPort();
        // 端口层自己拼 /v1/messages，BaseUrl 不能再带 /v1。
        _baseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/v1/messages/");
        _listener.Start();
        _ = ServeAsync();
    }

    /// <summary>收到的请求体（按顺序）。</summary>
    public IReadOnlyList<string> Bodies
    {
        get { lock (_bodies) return [.. _bodies]; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        GC.SuppressFinalize(this);
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
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                int index;
                lock (_bodies)
                {
                    index = _bodies.Count;
                    _bodies.Add(body);
                }

                var response = context.Response;
                if (index == 0)
                {
                    response.StatusCode = 400;
                    response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(StrictLimitBody);
                    await response.OutputStream.WriteAsync(bytes);
                    response.Close();
                    continue;
                }

                response.StatusCode = 200;
                response.ContentType = "text/event-stream";
                foreach (var @event in new[]
                {
                    "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}\n\n",
                    "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n",
                    "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n",
                    "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n",
                    "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n",
                    "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
                })
                {
                    await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(@event));
                }
                response.Close();
            }
            catch
            {
                // 客户端断开不影响断言。
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static ModelSpec Spec(JsonObject? compat = null) => new()
    {
        Id = "claude-test",
        Name = "claude-test",
        Api = "anthropic-messages",
        Provider = "anthropic",
        BaseUrl = "https://api.anthropic.com",
        ContextWindow = 200_000,
        MaxTokens = 8_192,
        Input = [ModelInput.Text],
        Cost = new ModelCostRates(0, 0, 0, 0),
        Compat = compat ?? new JsonObject { ["supportsStrictTools"] = true },
    };

    /// <summary>一个带 constrainedSampling 声明的工具。</summary>
    private static ToolDefinition Tool(string name, string strict) => new(
        name,
        $"tool {name}",
        new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }))
    {
        ConstrainedSampling = new JsonObject { ["type"] = "json_schema", ["strict"] = strict },
    };

    private static TranscriptContext Context(params ToolDefinition[] tools) => new(
        [
            new SystemMessage("", ToolsAdded: [.. tools]),
            Messages.UserText("hi"),
        ],
        null);

    private async Task<AssistantMessage> RunAsync(ModelSpec spec, TranscriptContext context)
    {
        var stream = AnthropicMessages.Stream(
            spec, context, new SimpleStreamOptions(ApiKey: "sk-ant", BaseUrl: _baseUrl));
        AssistantMessage? final = null;
        await foreach (var @event in stream)
        {
            if (@event is AssistantMessageEvent.Done done) final = done.Message;
        }

        return Assert.IsType<AssistantMessage>(final);
    }

    private static JsonArray ToolsOf(string body)
        => Assert.IsType<JsonArray>(JsonNode.Parse(body)!["tools"]);

    private static JsonObject? ToolNamed(string body, string name)
        => ToolsOf(body).OfType<JsonObject>()
            .FirstOrDefault(tool => tool["name"]?.GetValue<string>() == name);

    // ------------------------------------------------------------------ cases

    [Fact]
    public async Task StrictToolLimitRejectionRetriesOnceWithPreferredToolsNonStrict()
    {
        var spec = Spec();
        var context = Context(Tool("preferred", "prefer"), Tool("required", "require"));

        var final = await RunAsync(spec, context);

        var bodies = Bodies;
        Assert.Equal(2, bodies.Count);
        // 第一次：prefer 与 require 都以 strict 发出。
        Assert.True(ToolNamed(bodies[0], "preferred")!["strict"]!.GetValue<bool>());
        Assert.True(ToolNamed(bodies[0], "required")!["strict"]!.GetValue<bool>());
        // 第二次：prefer 退回原始 schema，require 仍是 strict。
        Assert.Null(ToolNamed(bodies[1], "preferred")!["strict"]);
        Assert.True(ToolNamed(bodies[1], "required")!["strict"]!.GetValue<bool>());

        Assert.Equal(StopReason.Stop, final.StopReason);
        var diagnostic = Assert.Single(final.Diagnostics ?? []);
        Assert.Equal("anthropic_strict_tools_fallback", diagnostic.Type);
        Assert.Equal("strict_tool_limits_exceeded", diagnostic.Details!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithoutPreferredToolsTheRejectionSurfacesAsTheProvidersError()
    {
        // 只有 require 工具：没有可降级的对象，400 原样抛出（上游同样不重试）。
        var spec = Spec();
        var stream = AnthropicMessages.Stream(
            spec, Context(Tool("required", "require")),
            new SimpleStreamOptions(ApiKey: "sk-ant", BaseUrl: _baseUrl));

        AssistantMessage? failed = null;
        await foreach (var @event in stream)
        {
            if (@event is AssistantMessageEvent.Error error) failed = error.Message;
        }

        Assert.Single(Bodies);
        Assert.NotNull(failed);
        Assert.Equal(StopReason.Error, failed!.StopReason);
        Assert.Contains("Too many strict tools", failed.ErrorMessage, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ unit level

    [Theory]
    [InlineData(400, "invalid_request_error", "The compiled grammar is too large", true)]
    [InlineData(400, "invalid_request_error", "Too many strict tools (27)", true)]
    [InlineData(400, "invalid_request_error", "Schemas contains too many parameters with union types (3)", true)]
    [InlineData(400, "invalid_request_error", "max_tokens: too large", false)]
    [InlineData(429, "invalid_request_error", "Too many strict tools (27)", false)]
    [InlineData(400, "rate_limit_error", "Too many strict tools (27)", false)]
    public void OnlyKnownStrictCapacityRejectionsAreDowngradeable(
        int status, string errorType, string message, bool expected)
    {
        var body = new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = errorType, ["message"] = message },
        }.ToJsonString();

        var error = new ProviderHttpException(status, body, $"{status}: {body}");

        Assert.Equal(expected, AnthropicMessages.IsStrictToolLimitRejection(error));
    }

    [Fact]
    public void ANonJsonErrorBodyIsNotADowngradeableRejection()
    {
        var error = new ProviderHttpException(400, "<html>bad gateway</html>");

        Assert.False(AnthropicMessages.IsStrictToolLimitRejection(error));
    }

    private static int TcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
