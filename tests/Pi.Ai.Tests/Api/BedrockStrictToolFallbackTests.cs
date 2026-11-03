// ============================================================================
// Bedrock strict tool capacity fallback (#11416 / upstream 842c73170).
//
// Bedrock rejects a request with ValidationException (HTTP 400) when the strict tool set exceeds
// Claude's capacity (compiled grammar too large, too many strict tools, too many union-typed
// parameters). The request is retried once with `strict: "prefer"` tools sent on their original
// non-strict schemas, while `strict: "require"` tools stay strict. Bedrock validates the tool
// schemas before it starts the stream, so a retry never repeats streamed output.
// ============================================================================

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

public class BedrockStrictToolFallbackTests
{
    /// <summary>从真实 Bedrock 观测：20 个跨 strict 工具的 union 参数。</summary>
    private const string UnionLimitMessage =
        "The model returned the following errors: Schemas contains too many parameters with union types "
        + "(20 parameters with type arrays or anyOf). This causes exponential compilation cost. Reduce the "
        + "number of nullable or union-typed parameters (limit: 16 parameters with unions).";

    // ------------------------------------------------------------------ fake Bedrock

    /// <summary>本地 ConverseStream 端点：按第 n 次请求（从 0 起）决定回什么。</summary>
    private sealed class FakeBedrock : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<string> _bodies = [];
        private readonly Func<int, (int Status, string Body)> _respond;

        public FakeBedrock(Func<int, (int Status, string Body)> respond)
        {
            _respond = respond;
            var port = TcpPort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{BaseUrl}/");
            _listener.Start();
            _ = ServeAsync();
        }

        public string BaseUrl { get; }

        /// <summary>收到的请求体（按顺序）。</summary>
        public IReadOnlyList<string> Bodies
        {
            get { lock (_bodies) return [.. _bodies]; }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
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

                    var (status, responseBody) = _respond(index);
                    var response = context.Response;
                    response.StatusCode = status;
                    response.ContentType = "application/json";
                    await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(responseBody));
                    response.Close();
                }
                catch
                {
                    // 客户端断开不影响断言。
                }
            }
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

    // ------------------------------------------------------------------ helpers

    private static ModelSpec Spec(string baseUrl) => new()
    {
        Id = "us.anthropic.claude-opus-5-5",
        Name = "Claude Opus 5.5 (US)",
        Api = "bedrock-converse-stream",
        Provider = "amazon-bedrock",
        BaseUrl = baseUrl,
        Reasoning = false,
        ContextWindow = 200_000,
        MaxTokens = 100,
        Input = [ModelInput.Text],
        Cost = new ModelCostRates(0, 0, 0, 0),
        Compat = new JsonObject { ["supportsStrictMode"] = true },
    };

    /// <summary>一个带 <c>value</c>（必填）与 <c>optional</c>（可选）属性的对象工具。</summary>
    private static ToolDefinition Tool(string name, string? strict = null) => new(
        name,
        "Look up a value",
        new ToolSchema(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["value"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["optional"] = new Dictionary<string, object?> { ["type"] = "number" },
            },
            ["required"] = new List<object?> { "value" },
        }))
    {
        ConstrainedSampling = strict is null
            ? null
            : new JsonObject { ["type"] = "json_schema", ["strict"] = strict },
    };

    private static TranscriptContext Context(params ToolDefinition[] tools) => new(
        [
            new SystemMessage("", ToolsAdded: [.. tools]),
            Messages.UserText("Hello"),
        ],
        null);

    private static string ValidationBody(string message) =>
        new JsonObject { ["message"] = message }.ToJsonString();

    /// <summary>一帧 AWS event-stream（prelude + headers + payload + 4 字节 message CRC）。</summary>
    private static byte[] BuildFrame(string eventType, string payloadJson)
    {
        using var headers = new MemoryStream();
        headers.WriteByte((byte)":event-type".Length);
        headers.Write(Encoding.UTF8.GetBytes(":event-type"));
        headers.WriteByte(7); // STRING
        var valueBytes = Encoding.UTF8.GetBytes(eventType);
        headers.Write(new[] { (byte)(valueBytes.Length >> 24), (byte)(valueBytes.Length >> 16),
            (byte)(valueBytes.Length >> 8), (byte)valueBytes.Length });
        headers.Write(valueBytes);
        var headerBytes = headers.ToArray();

        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var totalLength = 12 + headerBytes.Length + payloadBytes.Length + 4;
        using var frame = new MemoryStream();
        frame.Write(new[] { (byte)(totalLength >> 24), (byte)(totalLength >> 16),
            (byte)(totalLength >> 8), (byte)totalLength });
        frame.Write(new[] { (byte)(headerBytes.Length >> 24), (byte)(headerBytes.Length >> 16),
            (byte)(headerBytes.Length >> 8), (byte)headerBytes.Length });
        frame.Write(new byte[4]); // prelude CRC（解码器当前不校验）
        frame.Write(headerBytes);
        frame.Write(payloadBytes);
        frame.Write(new byte[4]); // message CRC
        return frame.ToArray();
    }

    private static string SuccessBody()
    {
        using var stream = new MemoryStream();
        stream.Write(BuildFrame("messageStart", """{"messageStart":{"role":"assistant"}}"""));
        stream.Write(BuildFrame("messageStop", """{"messageStop":{"stopReason":"end_turn"}}"""));
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>只走 Bearer，不读进程里的 AWS 凭据（否则本机会真的去签 SigV4）。</summary>
    private static BedrockOptions Options(string? bearer = "bedrock-key") => new()
    {
        ApiKey = bearer,
        Env = new Dictionary<string, string> { ["AWS_REGION"] = "us-east-1" },
    };

    private static async Task<AssistantMessage> RunAsync(
        string baseUrl, TranscriptContext context, BedrockOptions? options = null)
    {
        var stream = BedrockConverseStream.Stream(Spec(baseUrl), context, options ?? Options());
        AssistantMessage? final = null;
        await foreach (var @event in stream)
        {
            switch (@event)
            {
                case AssistantMessageEvent.Done done: final = done.Message; break;
                case AssistantMessageEvent.Error error: final = error.Message; break;
            }
        }

        return Assert.IsType<AssistantMessage>(final);
    }

    private static JsonArray ToolSpecs(string body)
        => Assert.IsType<JsonArray>(JsonNode.Parse(body)!["toolConfig"]!["tools"]);

    private static JsonObject SpecOf(string body, int index)
        => Assert.IsType<JsonObject>(ToolSpecs(body)[index]!["toolSpec"]);

    // ------------------------------------------------------------------ retry cases

    [Fact]
    public async Task RetriesOnceWithPreferredToolsNonStrictAndKeepsRequiredToolsStrict()
    {
        using var server = new FakeBedrock(
            attempt => attempt == 0 ? (400, ValidationBody(UnionLimitMessage)) : (200, SuccessBody()));
        var payloads = 0;
        var options = new BedrockOptions
        {
            ApiKey = "bedrock-key",
            Env = new Dictionary<string, string> { ["AWS_REGION"] = "us-east-1" },
            OnPayload = (payload, _) =>
            {
                payloads++;
                payload["requestMetadata"] = new JsonObject { ["team"] = "a" };
                return Task.FromResult<JsonObject?>(payload);
            },
        };

        var final = await RunAsync(server.BaseUrl,
            Context(Tool("preferred", "prefer"), Tool("required", "require"), Tool("ordinary")), options);

        Assert.Equal(StopReason.Stop, final.StopReason);
        Assert.Equal(2, payloads);

        var bodies = server.Bodies;
        Assert.Equal(2, bodies.Count);

        // 第一次：prefer 工具真的以 strict 发出——可选属性被包成 anyOf + null 并进 required。
        var firstPreferred = SpecOf(bodies[0], 0);
        Assert.True(firstPreferred["strict"]!.GetValue<bool>());
        var firstJson = firstPreferred["inputSchema"]!["json"]!;
        Assert.Equal("""["value","optional"]""", firstJson["required"]!.ToJsonString());
        Assert.Equal(
            """[{"type":"number"},{"type":"null"}]""",
            firstJson["properties"]!["optional"]!["anyOf"]!.ToJsonString());

        // 第二次：prefer 工具退回原始 schema，不再声明 strict。
        var secondPreferred = SpecOf(bodies[1], 0);
        Assert.Null(secondPreferred["strict"]);
        Assert.Equal(
            """{"type":"object","properties":{"value":{"type":"string"},"optional":{"type":"number"}},"required":["value"]}""",
            secondPreferred["inputSchema"]!["json"]!.ToJsonString());

        // require 与普通工具两次完全一致；require 仍是 strict。
        Assert.Equal(SpecOf(bodies[0], 1).ToJsonString(), SpecOf(bodies[1], 1).ToJsonString());
        Assert.True(SpecOf(bodies[1], 1)["strict"]!.GetValue<bool>());
        Assert.Equal(SpecOf(bodies[0], 2).ToJsonString(), SpecOf(bodies[1], 2).ToJsonString());

        // onPayload 的改写两次都生效（重试走的是同一条载荷构建路径）。
        Assert.Equal("a", JsonNode.Parse(bodies[1])!["requestMetadata"]!["team"]!.GetValue<string>());

        var diagnostic = Assert.Single(final.Diagnostics ?? []);
        Assert.Equal("bedrock_strict_tools_fallback", diagnostic.Type);
        Assert.Equal("strict_tool_limits_exceeded", diagnostic.Details!["reason"]!.GetValue<string>());
        Assert.Equal(UnionLimitMessage, diagnostic.Error!.Message);
    }

    [Fact]
    public async Task WithoutPreferredToolsTheRejectionSurfacesWithoutRetrying()
    {
        using var server = new FakeBedrock(_ => (400, ValidationBody(UnionLimitMessage)));

        var final = await RunAsync(server.BaseUrl, Context(Tool("required", "require"), Tool("ordinary")));

        Assert.Single(server.Bodies);
        Assert.Equal(StopReason.Error, final.StopReason);
        Assert.Contains("Schemas contains too many parameters with union types", final.ErrorMessage);
    }

    [Fact]
    public async Task OtherValidationErrorsAreNotRetried()
    {
        using var server = new FakeBedrock(_ => (400, ValidationBody("messages: field required")));

        var final = await RunAsync(server.BaseUrl, Context(Tool("preferred", "prefer")));

        Assert.Single(server.Bodies);
        Assert.Equal(StopReason.Error, final.StopReason);
        Assert.Contains("messages: field required", final.ErrorMessage);
    }

    [Fact]
    public async Task RetriesOnlyOnce()
    {
        using var server = new FakeBedrock(_ => (400, ValidationBody(UnionLimitMessage)));

        var final = await RunAsync(server.BaseUrl, Context(Tool("preferred", "prefer")));

        Assert.Equal(2, server.Bodies.Count);
        Assert.Equal(StopReason.Error, final.StopReason);
        // 第二次的 prefer 工具已经降级，说明重试确实换了载荷。
        Assert.Null(SpecOf(server.Bodies[1], 0)["strict"]);
    }

    // ------------------------------------------------------------------ unit level

    [Theory]
    [InlineData("The compiled grammar is too large", true)]
    [InlineData("Too many strict tools (27) for this model", true)]
    [InlineData("Schemas contains too many parameters with union types (20)", true)]
    [InlineData("messages: field required", false)]
    [InlineData("", false)]
    public void OnlyKnownStrictCapacityMessagesCountAsRejections(string message, bool expected)
        => Assert.Equal(expected, ConstrainedSampling.IsStrictToolLimitErrorMessage(message));

    [Theory]
    [InlineData(400, UnionLimitMessage, true)]
    [InlineData(400, "messages: field required", false)]
    // ValidationException 之外的状态码不降级：429/5xx 是限流与服务端问题，重发无用。
    [InlineData(429, UnionLimitMessage, false)]
    [InlineData(500, UnionLimitMessage, false)]
    public void OnlyBadRequestsCarryingTheLimitMessageAreDowngradeable(int status, string message, bool expected)
    {
        var error = new ProviderHttpException(status, ValidationBody(message));
        Assert.Equal(expected, BedrockConverseStream.IsStrictToolLimitRejection(error));
    }

    [Fact]
    public void ANonJsonErrorBodyIsNotADowngradeableRejection()
    {
        var error = new ProviderHttpException(400, "<html>bad gateway</html>");
        Assert.False(BedrockConverseStream.IsStrictToolLimitRejection(error));
    }

    [Fact]
    public void PreferredStrictConfigIsRecognisedByShapeOnly()
    {
        Assert.True(ConstrainedSampling.PrefersJsonSchemaStrictSampling(Tool("a", "prefer")));
        Assert.False(ConstrainedSampling.PrefersJsonSchemaStrictSampling(Tool("b", "require")));
        Assert.False(ConstrainedSampling.PrefersJsonSchemaStrictSampling(Tool("c")));
    }

    [Fact]
    public void ConvertToolConfigReportsWhetherAnyPreferredToolWasSentStrict()
    {
        const bool supportsStrict = true;

        var preferred = BedrockConverseStream.ConvertToolConfig(
            [Tool("preferred", "prefer"), Tool("required", "require")], null, supportsStrict);
        Assert.True(preferred.HasPreferredStrictTools);

        // 只有 require：没有可降级的对象。
        var requiredOnly = BedrockConverseStream.ConvertToolConfig(
            [Tool("required", "require")], null, supportsStrict);
        Assert.False(requiredOnly.HasPreferredStrictTools);

        // 降级后再转换：prefer 已不再是 strict，require 仍是。
        var downgraded = BedrockConverseStream.ConvertToolConfig(
            [Tool("preferred", "prefer"), Tool("required", "require")], null, supportsStrict,
            disablePreferredStrictTools: true);
        Assert.False(downgraded.HasPreferredStrictTools);
        Assert.Null(SpecOfTool(downgraded.Config!, 0)["strict"]);
        Assert.True(SpecOfTool(downgraded.Config!, 1)["strict"]!.GetValue<bool>());

        // toolChoice 为 none 时整块不下发。
        var none = BedrockConverseStream.ConvertToolConfig(
            [Tool("preferred", "prefer")], JsonValue.Create("none"), supportsStrict);
        Assert.Null(none.Config);
        Assert.False(none.HasPreferredStrictTools);
    }

    private static JsonObject SpecOfTool(JsonObject config, int index)
        => Assert.IsType<JsonObject>(config["tools"]![index]!["toolSpec"]);
}
