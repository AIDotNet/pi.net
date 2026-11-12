using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// 上游 1.5.1 #11335：Streamable HTTP 的 <c>application/json</c> 应答受 maxMessageBytes 约束、
/// 超限即取消剩余响应体；错误体只读前 8 KiB 且取消其余，不再整体缓冲。
/// </summary>
public class StreamableHttpBodyLimitTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _url;
    private readonly CancellationTokenSource _cts = new();
    private volatile string _mode = "small-json";

    public StreamableHttpBodyLimitTests()
    {
        var port = TcpPort();
        _url = $"http://127.0.0.1:{port}/mcp";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
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
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var message = JsonRpc.ParseText(body);
            var response = context.Response;

            switch (_mode)
            {
                case "small-json":
                    response.StatusCode = 200;
                    response.ContentType = "application/json";
                    response.Headers.Add("Mcp-Session-Id", "session-123");
                    await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(
                        JsonRpc.Serialize(new JsonRpcMessage.SuccessResponse(
                            ((JsonRpcMessage.Request)message).Id,
                            JsonNode.Parse("""{"ok":true}"""))).ToJsonString()));
                    break;

                case "big-json":
                    response.StatusCode = 200;
                    response.ContentType = "application/json";
                    response.Headers.Add("Mcp-Session-Id", "session-123");
                    // 合法 JSON-RPC 应答，但整体远超客户端的 maxMessageBytes。
                    var big = JsonRpc.Serialize(new JsonRpcMessage.SuccessResponse(
                        ((JsonRpcMessage.Request)message).Id,
                        JsonNode.Parse("{\"blob\":\"" + new string('x', 4000) + "\"}"))).ToJsonString();
                    await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(big));
                    break;

                case "big-error":
                    response.StatusCode = 400;
                    response.ContentType = "text/plain";
                    await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(new string('e', 20_000)));
                    break;
            }
            response.Close();
        }
        catch
        {
            // 客户端提前取消响应体：忽略。
        }
    }

    private StreamableHttpTransport MakeTransport(int? maxMessageBytes = null) =>
        new(new StreamableHttpTransportOptions
        {
            Url = _url,
            OpenGetStream = false,
            MaxMessageBytes = maxMessageBytes,
        });

    private static JsonRpcMessage.Request Request() =>
        new(JsonRpcId.FromNumber(1), "initialize", JsonNode.Parse("""{"clientInfo":{"name":"c","version":"1"}}"""));

    [Fact]
    public async Task JsonResponseUnderLimit_IsParsed()
    {
        _mode = "small-json";
        var transport = MakeTransport();
        await transport.StartAsync();
        var messages = new List<JsonRpcMessage>();
        using var subscription = transport.OnMessage(messages.Add);

        await transport.SendAsync(Request());

        var response = Assert.IsType<JsonRpcMessage.SuccessResponse>(Assert.Single(messages));
        Assert.True(((JsonObject)response.Result!)["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task JsonResponseOverLimit_IsRejected()
    {
        _mode = "big-json";
        var transport = MakeTransport(maxMessageBytes: 256);
        await transport.StartAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(Request()));
        Assert.Contains("exceeds", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ErrorBody_IsTruncatedToFirstEightKiB()
    {
        _mode = "big-error";
        var transport = MakeTransport();
        await transport.StartAsync();

        var error = await Assert.ThrowsAsync<McpHttpError>(() => transport.SendAsync(Request()));
        Assert.Equal(400, (int)error.Status);
        // 只读前 8 KiB，其余被取消：绝不能把 20000 字节整体缓冲进错误。
        Assert.True(error.Body.Length <= 8 * 1024, $"error body was {error.Body.Length} chars");
        Assert.True(error.Body.Length > 0, "the readable prefix should still be kept");
        Assert.True(error.Body.Length < 20_000, "the full error body must not be buffered");
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
