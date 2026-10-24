// Anthropic 的 message_delta 里，OpenRouter 会额外塞 cost / is_byok / cost_details。
// 这条接入点是流解析内部的一步，没有纯函数可测，所以起一个最小 SSE server 走真实链路。

using System.Net;
using System.Text;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>
/// <c>AnthropicMessages</c> 的 OpenRouter 成本接入：报文的 message_delta 带账户计费时，
/// 最终 usage 的总价应被覆盖并标记为来自 provider。对应上游
/// <c>anthropic-messages.ts</c> 里 <c>isOpenRouterModel(model) &amp;&amp; event.usage</c> 那一段。
/// </summary>
public sealed class AnthropicOpenRouterCostTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>下一条 message_delta 的 usage；测试在发请求前写好。</summary>
    private string _deltaUsage = """{"output_tokens":5}""";

    public AnthropicOpenRouterCostTests()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        // 端口层自己拼 /v1/messages，BaseUrl 不能再带 /v1。
        _baseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/v1/messages/");
        _listener.Start();
        _ = ServeAsync();
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
                var body = string.Join(
                    "\n\n",
                    "event: message_start\ndata: "
                    + """{"type":"message_start","message":{"usage":{"input_tokens":10,"output_tokens":0}}}""",
                    "event: message_delta\ndata: "
                    + $$"""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{{_deltaUsage}}}""",
                    "event: message_stop\ndata: {\"type\":\"message_stop\"}") + "\n\n";
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = "text/event-stream";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, _cts.Token);
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private async Task<AssistantMessage> RunAsync(string provider, string baseUrl = "https://openrouter.ai/api/v1")
    {
        var model = new ModelSpec
        {
            Id = "m",
            Name = "M",
            Api = "anthropic-messages",
            Provider = provider,
            BaseUrl = baseUrl,
            Input = [ModelInput.Text, ModelInput.Image],
            // 目录价：10 输入 + 5 输出 = 0.01 + 0.01 = 0.02。
            Cost = new ModelCostRates(1000, 2000),
        };
        var stream = AnthropicMessages.Stream(
            model,
            new TranscriptContext([Messages.UserText("hi")]),
            new SimpleStreamOptions(ApiKey: "k", BaseUrl: _baseUrl));

        AssistantMessage? final = null;
        await foreach (var @event in stream)
        {
            if (@event is AssistantMessageEvent.Done done) final = done.Message;
        }
        return Assert.IsType<AssistantMessage>(final);
    }

    [Fact]
    public async Task NonByokDeltaReplacesTheCatalogTotal()
    {
        _deltaUsage = """{"output_tokens":5,"cost":0.0042,"is_byok":false}""";

        var message = await RunAsync("openrouter");

        Assert.Equal(0.0042, message.UsageStats!.Cost.Total, 12);
        Assert.True(message.UsageStats.Cost.TotalFromProvider);
    }

    [Fact]
    public async Task ByokDeltaAddsTheUpstreamCost()
    {
        _deltaUsage = """{"output_tokens":5,"cost":0.1,"is_byok":true,"cost_details":{"upstream_inference_cost":0.9}}""";

        var message = await RunAsync("openrouter");

        Assert.Equal(1, message.UsageStats!.Cost.Total, 12);
        Assert.True(message.UsageStats.Cost.TotalFromProvider);
    }

    /// <summary>非 OpenRouter（provider 与 baseUrl 都不命中）：同样的成本字段不能改总价。</summary>
    [Fact]
    public async Task OtherProvidersKeepTheCatalogTotal()
    {
        _deltaUsage = """{"output_tokens":5,"cost":0.0042,"is_byok":false}""";

        var message = await RunAsync("deepseek", "https://api.deepseek.com");

        Assert.Equal(0.02, message.UsageStats!.Cost.Total, 12);
        Assert.False(message.UsageStats.Cost.TotalFromProvider);
    }
}
