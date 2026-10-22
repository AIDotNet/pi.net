// ============================================================================
// Mistral request timeout only covers waiting for response headers (#10609 /
// upstream 6b07b4e57).
//
// Long Mistral streams (extended thinking) used to be cut off once timeoutMs elapsed even while
// data was still arriving. The timeout now covers only the header wait; body stalls are left to
// the HTTP client's idle timeout.
// ============================================================================

using System.Net;
using System.Text;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests;

public sealed class MistralStreamTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>响应头写出后，正文第一片与最后一片之间的间隔。</summary>
    private const int BodyGapMs = 400;

    public MistralStreamTests()
    {
        var port = TcpPort();
        _baseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/v1/chat/completions/");
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
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                await reader.ReadToEndAsync();

                var response = context.Response;
                response.StatusCode = 200;
                response.ContentType = "text/event-stream";
                response.SendChunked = true;
                await response.OutputStream.FlushAsync();

                // 第一片先发出去，让响应头落地；随后故意停顿超过调用方给的 timeoutMs。
                await WriteAsync(response,
                    "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":null}]}\n\n");
                await Task.Delay(BodyGapMs, _cts.Token);
                await WriteAsync(response,
                    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
                await WriteAsync(response, "data: [DONE]\n\n");
                response.Close();
            }
            catch
            {
                // 客户端断开或取消不影响断言。
            }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, string chunk)
    {
        await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(chunk));
        await response.OutputStream.FlushAsync();
    }

    private static ModelSpec Spec(string baseUrl) => new()
    {
        Id = "mistral-test",
        Name = "mistral-test",
        Api = "mistral-conversations",
        Provider = "mistral",
        BaseUrl = baseUrl,
        ContextWindow = 128_000,
        MaxTokens = 8_192,
        Input = [ModelInput.Text],
        Cost = new ModelCostRates(0, 0, 0, 0),
    };

    [Fact]
    public async Task AStreamThatOutlivesTheTimeoutKeepsGoing()
    {
        // timeoutMs 只覆盖「等响应头」：正文还在到达时不能被砍断。
        var stream = MistralConversations.Stream(
            Spec(_baseUrl),
            new TranscriptContext([Messages.UserText("hi")], null),
            new MistralOptions { ApiKey = "sk-mistral", TimeoutMs = 100 });

        var deltas = new List<string>();
        AssistantMessage? final = null;
        await foreach (var @event in stream)
        {
            switch (@event)
            {
                case AssistantMessageEvent.TextDelta delta: deltas.Add(delta.Delta); break;
                case AssistantMessageEvent.Done done: final = done.Message; break;
            }
        }

        Assert.Equal(["hi"], deltas);
        Assert.NotNull(final);
        Assert.Equal(StopReason.Stop, final!.StopReason);
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
