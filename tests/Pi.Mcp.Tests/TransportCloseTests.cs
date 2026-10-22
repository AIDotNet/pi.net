using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// 关闭会话时的认证语义（上游 f10993bc7 / #10565）：DELETE 复用「最近一次请求的令牌」，
/// 不再去问认证提供者——它会顺带做网络刷新，关闭流程不能等一次刷新。
/// </summary>
public class TransportCloseTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _url;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>每次请求的 (方法, Authorization, Mcp-Session-Id)。</summary>
    private readonly List<(string Method, string? Authorization, string? SessionId)> _requests = [];

    public TransportCloseTests()
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
            catch (Exception) { return; }
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            lock (_requests)
            {
                _requests.Add((
                    request.HttpMethod,
                    request.Headers["Authorization"],
                    request.Headers["Mcp-Session-Id"]));
            }

            if (request.HttpMethod == "DELETE")
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var message = JsonRpc.ParseText(await reader.ReadToEndAsync());
            response.StatusCode = 200;
            response.ContentType = "application/json";
            // 会话 id 只在 initialize 响应头里下发。
            if (message is JsonRpcMessage.Request { Method: "initialize" })
                response.Headers.Add("Mcp-Session-Id", "session-1");
            if (message is JsonRpcMessage.Request jsonRpcRequest)
            {
                await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(
                    JsonRpc.Serialize(new JsonRpcMessage.SuccessResponse(jsonRpcRequest.Id, new JsonObject()))
                        .ToJsonString()));
            }

            response.Close();
        }
        catch
        {
            // 客户端提前断开等：忽略。
        }
    }

    private IReadOnlyList<(string Method, string? Authorization, string? SessionId)> Requests()
    {
        lock (_requests)
        {
            return [.. _requests];
        }
    }

    /// <summary>每次取令牌都 +1，便于看出关闭有没有再问一次。</summary>
    private sealed class CountingAuthProvider : AuthProvider
    {
        public int Calls { get; private set; }

        public Task<string?> Token(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>($"token-{++Calls}");
    }

    [Fact]
    public async Task CloseReusesTheLastRequestTokenWithoutAskingTheProvider()
    {
        var provider = new CountingAuthProvider();
        var transport = new StreamableHttpTransport(new StreamableHttpTransportOptions
        {
            Url = _url,
            OpenGetStream = false,
            AuthProvider = provider,
        });
        await transport.StartAsync();
        var messages = new List<JsonRpcMessage>();
        using var subscription = transport.OnMessage(messages.Add);

        await transport.SendAsync(new JsonRpcMessage.Request(
            JsonRpcId.FromNumber(1), "initialize", JsonNode.Parse("""{"clientInfo":{"name":"c","version":"1"}}""")));
        var before = provider.Calls;
        Assert.Single(messages);

        await transport.CloseAsync();

        Assert.Equal(before, provider.Calls);
        var delete = Assert.Single(Requests(), entry => entry.Method == "DELETE");
        Assert.Equal($"Bearer token-{before}", delete.Authorization);
        Assert.Equal("session-1", delete.SessionId);
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
