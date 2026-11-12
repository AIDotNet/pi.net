using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// 上游 1.5.1 #11545 消息线程：状态机（create → continue → 拒绝回退 / 关闭）+ 真实 HTTP wire 行为。
/// 对应 TS <c>test/anthropic-message-threads.test.ts</c>。
/// </summary>
[Collection("http-fixtures")]
public class AnthropicMessageThreadsTests
{
    private static int TcpPort() => TestPorts.Next();

    private static ModelSpec Spec(JsonObject? compat = null, string baseUrl = "https://api.anthropic.com")
        => new()
        {
            Id = "claude-test",
            Name = "claude-test",
            Api = "anthropic-messages",
            Provider = "anthropic",
            BaseUrl = baseUrl,
            Reasoning = false,
            ContextWindow = 200_000,
            MaxTokens = 8_192,
            Input = [ModelInput.Text],
            Compat = compat ?? new JsonObject { ["supportsMessageThreads"] = true },
        };

    private static JsonObject Params(params string[] messageTexts)
    {
        var messages = new JsonArray();
        foreach (var text in messageTexts)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            });
        }
        return new JsonObject
        {
            ["model"] = "claude-test",
            ["messages"] = messages,
            ["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "sys" }),
            ["tools"] = new JsonArray(),
            ["max_tokens"] = 8192,
            ["stream"] = true,
        };
    }

    private static AssistantMessage Reply(string id = "msg_1") => new(
        [new TextContent("ok")], StopReason.Stop)
    { ResponseId = id };

    // ------------------------------------------------------------------ pure state machine

    [Fact]
    public void FirstRequestCreatesThread()
    {
        AnthropicMessageThreads.ResetForTests();
        var prepared = AnthropicMessageThreads.PrepareThreadRequest(Spec(), "s1", Params("hi"));
        Assert.NotNull(prepared);
        Assert.Equal("create", prepared!.Body["thread"]?["type"]?.GetValue<string>());
        Assert.Equal("s1", prepared.SessionId);
    }

    [Fact]
    public void UnchangedPrefixContinuesAndSlicesMessages()
    {
        AnthropicMessageThreads.ResetForTests();
        var model = Spec();
        var first = Params("hi");
        var created = AnthropicMessageThreads.PrepareThreadRequest(model, "s2", first)!;
        // 回复后由真实路径记录续接位置。
        AnthropicMessageThreads.RecordThreadReply(model, created, first, Reply("msg_42"), false, false);

        // 下一次请求：原消息 + 与 recordThreadReply 相同形状转换出的 assistant 回复 + 一条新 user 消息。
        var reply = AnthropicMessages.ConvertReplyForThread(Reply("msg_42"), model, false, false);
        var next = (JsonObject)first.DeepClone();
        foreach (var m in reply) next["messages"]!.AsArray().Add(m?.DeepClone());
        next["messages"]!.AsArray().Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "more" }),
        });

        var continued = AnthropicMessageThreads.PrepareThreadRequest(model, "s2", next);
        Assert.NotNull(continued);
        Assert.Equal("continue", continued!.Body["thread"]?["type"]?.GetValue<string>());
        Assert.Equal("msg_42", continued.Body["thread"]?["previous_message_id"]?.GetValue<string>());
        // continue 只发新增消息，且不带 system/tools。
        Assert.Null(continued.Body["system"]);
        Assert.Null(continued.Body["tools"]);
        Assert.Single(continued.Body["messages"]!.AsArray());
        Assert.Equal("more", continued.Body["messages"]![0]?["content"]?[0]?["text"]?.GetValue<string>());
    }

    [Fact]
    public void ChangedSystemForcesNewThread()
    {
        AnthropicMessageThreads.ResetForTests();
        var model = Spec();
        var first = Params("hi");
        var created = AnthropicMessageThreads.PrepareThreadRequest(model, "s3", first)!;
        AnthropicMessageThreads.RecordThreadReply(model, created, first, Reply(), false, false);

        var reply = AnthropicMessages.ConvertReplyForThread(Reply(), model, false, false);
        var next = (JsonObject)first.DeepClone();
        foreach (var m in reply) next["messages"]!.AsArray().Add(m?.DeepClone());
        next["messages"]!.AsArray().Add(new JsonObject { ["role"] = "user", ["content"] = "more" });
        next["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "CHANGED" });

        var prepared = AnthropicMessageThreads.PrepareThreadRequest(model, "s3", next);
        Assert.Equal("create", prepared!.Body["thread"]?["type"]?.GetValue<string>());
    }

    [Fact]
    public void HashThreadPrefixIgnoresCacheControlAndStringVsBlockContent()
    {
        var model = Spec();
        var withMarker = Params("hi");
        ((JsonObject)withMarker["messages"]![0]!["content"]![0]!)["cache_control"] =
            new JsonObject { ["type"] = "ephemeral" };
        var withoutMarker = (JsonObject)withMarker.DeepClone();
        ((JsonObject)withoutMarker["messages"]![0]!["content"]![0]!).Remove("cache_control");

        Assert.Equal(
            AnthropicMessageThreads.HashThreadPrefix(model, withMarker, withMarker["messages"]!.AsArray()),
            AnthropicMessageThreads.HashThreadPrefix(model, withoutMarker, withoutMarker["messages"]!.AsArray()));

        // 字符串 content 与 [{type:text,text}] 等价。
        var asString = (JsonObject)withoutMarker.DeepClone();
        asString["messages"]![0]!["content"] = "hi";
        var asBlock = (JsonObject)withoutMarker.DeepClone();
        Assert.Equal(
            AnthropicMessageThreads.HashThreadPrefix(model, asString, asString["messages"]!.AsArray()),
            AnthropicMessageThreads.HashThreadPrefix(model, asBlock, asBlock["messages"]!.AsArray()));
    }

    [Fact]
    public void GetThreadErrorCodeReadsErrorDetails()
    {
        var error = new ProviderHttpException(400,
            """{"type":"error","error":{"details":{"error_code":"thread_not_found"}}}""");
        Assert.Equal("thread_not_found", AnthropicMessageThreads.GetThreadErrorCode(error));
    }

    [Fact]
    public async Task NewThreadRejectionResendsAsCreate()
    {
        AnthropicMessageThreads.ResetForTests();
        var model = Spec();
        var parameters = Params("hi");
        var prepared = AnthropicMessageThreads.PrepareThreadRequest(model, "s5", parameters)!;
        var bodies = new List<JsonObject>();
        var diagnostics = new List<AssistantMessageDiagnostic>();
        var call = 0;
        Task<HttpResponseMessage> Send(JsonObject body)
        {
            bodies.Add((JsonObject)body.DeepClone());
            call++;
            if (call == 1)
            {
                throw new ProviderHttpException(400,
                    """{"type":"error","error":{"details":{"error_code":"thread_not_found"}}}""");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        var (response, thread) = await AnthropicMessageThreads.SendThreadRequestAsync(
            prepared, parameters, Send, diagnostics.Add, default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(thread);
        Assert.Equal("create", thread!.Body["thread"]?["type"]?.GetValue<string>());
        Assert.Equal(2, bodies.Count);
        Assert.Single(diagnostics);
        Assert.Equal("anthropic_thread_fallback", diagnostics[0].Type);
    }

    [Fact]
    public async Task UnsupportedEndpointSendsWithoutThreadAndTurnsOff()
    {
        AnthropicMessageThreads.ResetForTests();
        var model = Spec();
        var parameters = Params("hi");
        var prepared = AnthropicMessageThreads.PrepareThreadRequest(model, "s6", parameters)!;
        var bodies = new List<JsonObject>();
        var diagnostics = new List<AssistantMessageDiagnostic>();
        var call = 0;
        Task<HttpResponseMessage> Send(JsonObject body)
        {
            bodies.Add((JsonObject)body.DeepClone());
            call++;
            if (call == 1)
            {
                throw new ProviderHttpException(400,
                    """{"type":"error","error":{"message":"thread: Extra inputs are not permitted"}}""",
                    "400: thread: Extra inputs are not permitted");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        var (response, thread) = await AnthropicMessageThreads.SendThreadRequestAsync(
            prepared, parameters, Send, diagnostics.Add, default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(thread);
        Assert.Equal(2, bodies.Count);
        Assert.Null(bodies[1]["thread"]); // 重发是不带 thread 的正常请求
        // 会话被标记为 off，之后 prepare 返回 null。
        Assert.Null(AnthropicMessageThreads.PrepareThreadRequest(model, "s6", parameters));
    }

    // ------------------------------------------------------------------ wire

    [Fact]
    public async Task StreamAddsThreadCreateAndThreadsBetaOnFirstRequest()
    {
        AnthropicMessageThreads.ResetForTests();
        using var fixture = new MessagesFixture();
        var context = new TranscriptContext([Messages.UserText("hi")], null);
        var options = new SimpleStreamOptions(ApiKey: "sk-ant", BaseUrl: fixture.BaseUrl, SessionId: "wire-1");

        await DrainAsync(AnthropicMessages.Stream(Spec(), context, options));

        var body = Assert.Single(fixture.Bodies);
        Assert.Equal("create", body["thread"]?["type"]?.GetValue<string>());
        Assert.Contains(AnthropicMessageThreads.Beta, fixture.BetaHeader ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisableContinuationOmitsThreadFieldButKeepsBeta()
    {
        AnthropicMessageThreads.ResetForTests();
        using var fixture = new MessagesFixture();
        var context = new TranscriptContext([Messages.UserText("hi")], null);
        var options = new SimpleStreamOptions(
            ApiKey: "sk-ant", BaseUrl: fixture.BaseUrl, SessionId: "wire-2", DisableContinuation: true);

        await DrainAsync(AnthropicMessages.Stream(Spec(), context, options));

        var body = Assert.Single(fixture.Bodies);
        Assert.Null(body["thread"]);
        Assert.Contains(AnthropicMessageThreads.Beta, fixture.BetaHeader ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThreadsDisabledForNonAnthropicEndpoint()
    {
        AnthropicMessageThreads.ResetForTests();
        using var fixture = new MessagesFixture();
        var context = new TranscriptContext([Messages.UserText("hi")], null);
        var spec = Spec(new JsonObject { ["supportsMessageThreads"] = false });
        var options = new SimpleStreamOptions(ApiKey: "sk-ant", BaseUrl: fixture.BaseUrl, SessionId: "wire-3");

        await DrainAsync(AnthropicMessages.Stream(spec, context, options));

        var body = Assert.Single(fixture.Bodies);
        Assert.Null(body["thread"]);
        Assert.DoesNotContain(AnthropicMessageThreads.Beta, fixture.BetaHeader ?? "", StringComparison.Ordinal);
    }

    private static async Task DrainAsync(IAssistantMessageEventStream stream)
    {
        await foreach (var _ in stream) { }
    }

    /// <summary>记录请求体与 anthropic-beta 头，返回一条最小可用 SSE 流。</summary>
    private sealed class MessagesFixture : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<JsonObject> _bodies = [];
        public string BaseUrl { get; }
        public string? BetaHeader { get; private set; }
        public IReadOnlyList<JsonObject> Bodies { get { lock (_bodies) return [.. _bodies]; } }

        public MessagesFixture()
        {
            var port = TcpPort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/v1/messages/");
            _listener.Start();
            _ = ServeAsync();
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
                    var raw = await reader.ReadToEndAsync();
                    lock (_bodies) _bodies.Add(JsonNode.Parse(raw) as JsonObject ?? new JsonObject());
                    BetaHeader = context.Request.Headers["anthropic-beta"];
                    var response = context.Response;
                    response.StatusCode = 200;
                    response.ContentType = "text/event-stream";
                    foreach (var @event in new[]
                    {
                        """event: message_start""" + "\n" + """data: {"type":"message_start","message":{"id":"msg_wire","usage":{"input_tokens":1,"output_tokens":0}}}""" + "\n\n",
                        """event: content_block_start""" + "\n" + """data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""" + "\n\n",
                        """event: content_block_delta""" + "\n" + """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}""" + "\n\n",
                        """event: content_block_stop""" + "\n" + """data: {"type":"content_block_stop","index":0}""" + "\n\n",
                        """event: message_delta""" + "\n" + """data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}""" + "\n\n",
                        """event: message_stop""" + "\n" + """data: {"type":"message_stop"}""" + "\n\n",
                    })
                    {
                        await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(@event));
                    }
                    response.Close();
                }
                catch { /* 客户端断开 */ }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            GC.SuppressFinalize(this);
        }
    }
}
