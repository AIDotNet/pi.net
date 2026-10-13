using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// Streamable HTTP 传输的认证接线（对应 TS streamable-http.ts 的 authorizedFetch）：
/// Bearer 头下发、401/403-insufficient_scope 交给认证提供者一次后重试一次、
/// 提供者无 onUnauthorized 时不重试、只重试一次。
/// </summary>
public class TransportAuthTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _url;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string?> _authorizations = [];

    /// <summary>服务器要求的当前有效令牌。</summary>
    private volatile string _validToken = "tok-1";

    /// <summary>可选的拒绝钩子：返回 true 表示该请求已被钩子应答完毕（不进入正常鉴权）。</summary>
    private Func<HttpListenerContext, bool>? _reject;

    public TransportAuthTests()
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

    public IReadOnlyList<string?> Authorizations => _authorizations;

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
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var message = JsonRpc.ParseText(body);
            var response = context.Response;
            var authorization = context.Request.Headers["Authorization"];
            lock (_authorizations) _authorizations.Add(authorization);

            var reject = _reject;
            if (reject is not null && reject(context))
            {
                await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{}"));
                response.Close();
                return;
            }

            if (authorization != $"Bearer {_validToken}")
            {
                response.StatusCode = 401;
                response.Headers.Add("WWW-Authenticate", "Bearer resource_metadata=\"http://x/meta\"");
                await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("unauthorized"));
                response.Close();
                return;
            }

            response.StatusCode = 200;
            response.ContentType = "application/json";
            if (message is JsonRpcMessage.Request request)
            {
                await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(
                    JsonRpc.Serialize(new JsonRpcMessage.SuccessResponse(request.Id, new JsonObject())).ToJsonString()));
            }
            response.Close();
        }
        catch
        {
            // 连接被客户端提前关闭等：忽略。
        }
    }

    /// <summary>令牌可变、并记录 onUnauthorized 调用次数的提供者。</summary>
    private sealed class MutableAuthProvider(
        Func<string?> token, Func<UnauthorizedContext, CancellationToken, Task>? onUnauthorized = null) : AuthProvider
    {
        public int UnauthorizedCalls { get; private set; }

        public Task<string?> Token(CancellationToken cancellationToken = default) => Task.FromResult(token());

        public Func<UnauthorizedContext, CancellationToken, Task>? OnUnauthorized => onUnauthorized is null
            ? null
            : async (context, ct) =>
            {
                UnauthorizedCalls++;
                await onUnauthorized(context, ct);
            };
    }

    /// <summary>发一个请求；返回是否收到应答、抛出的异常、应答本身。</summary>
    private static async Task<(bool Ok, Exception? Error, JsonRpcMessage? Reply)> SendOnceAsync(
        StreamableHttpTransport transport)
    {
        var reply = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = transport.OnMessage(message => reply.TrySetResult(message));
        try
        {
            await transport.SendAsync(new JsonRpcMessage.Request(JsonRpcId.FromNumber(1), "tools/list", null));
        }
        catch (Exception error)
        {
            return (false, error, null);
        }
        var completed = await Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        return completed == reply.Task ? (true, null, reply.Task.Result) : (false, new TimeoutException(), null);
    }

    private StreamableHttpTransport Build(AuthProvider? provider)
        => new(new StreamableHttpTransportOptions { Url = _url, OpenGetStream = false, AuthProvider = provider });

    [Fact]
    public async Task SendsBearerToken()
    {
        var transport = Build(new MutableAuthProvider(() => "tok-1"));
        await transport.StartAsync();
        var (ok, error, _) = await SendOnceAsync(transport);
        Assert.Null(error);
        Assert.True(ok);
        Assert.Contains("Bearer tok-1", Authorizations);
    }

    [Fact]
    public async Task RefreshesOnceAfter401AndRetries()
    {
        _validToken = "fresh";
        var token = "stale";
        var provider = new MutableAuthProvider(() => token, (_, _) => { token = "fresh"; return Task.CompletedTask; });
        var transport = Build(provider);
        await transport.StartAsync();
        var (ok, error, _) = await SendOnceAsync(transport);
        Assert.Null(error);
        Assert.True(ok);
        Assert.Equal(1, provider.UnauthorizedCalls);
        // 第一次用旧令牌被 401，刷新后用新令牌成功。
        Assert.Equal("Bearer stale", Authorizations[0]);
        Assert.Equal("Bearer fresh", Authorizations[1]);
    }

    [Fact]
    public async Task NoRetryWhenProviderHasNoOnUnauthorized()
    {
        var provider = new MutableAuthProvider(() => "wrong");
        var transport = Build(provider);
        await transport.StartAsync();
        var (ok, error, _) = await SendOnceAsync(transport);
        Assert.False(ok);
        Assert.IsType<McpAuthRequiredError>(error);
        Assert.Single(Authorizations);
    }

    [Fact]
    public async Task RetriesOn403InsufficientScope()
    {
        _validToken = "fresh";
        _reject = context =>
        {
            if (context.Request.Headers["Authorization"] == "Bearer fresh") return false;
            context.Response.StatusCode = 403;
            context.Response.Headers.Add("WWW-Authenticate", "Bearer error=\"insufficient_scope\", scope=\"files\"");
            return true;
        };
        var token = "limited";
        var provider = new MutableAuthProvider(() => token, (_, _) => { token = "fresh"; return Task.CompletedTask; });
        var transport = Build(provider);
        await transport.StartAsync();
        var (ok, error, _) = await SendOnceAsync(transport);
        Assert.Null(error);
        Assert.True(ok);
        Assert.Equal(1, provider.UnauthorizedCalls);
    }

    [Fact]
    public async Task DoesNotRetryOn403WithoutChallenge()
    {
        _reject = context =>
        {
            context.Response.StatusCode = 403;
            return true;
        };
        var provider = new MutableAuthProvider(() => "tok-1", (_, _) => Task.CompletedTask);
        var transport = Build(provider);
        await transport.StartAsync();
        var (ok, error, _) = await SendOnceAsync(transport);
        Assert.False(ok);
        Assert.IsType<McpHttpError>(error);
        Assert.Equal(0, provider.UnauthorizedCalls);
        Assert.Single(Authorizations);
    }

    [Fact]
    public async Task RetriesOnlyOnce()
    {
        // 服务器永远 401；提供者每次刷新都换新令牌，但传输只重试一次。
        _validToken = "never-issued";
        var counter = 0;
        var provider = new MutableAuthProvider(() => $"tok-{counter}", (_, _) =>
        {
            counter++;
            return Task.CompletedTask;
        });
        var transport = Build(provider);
        await transport.StartAsync();
        var (ok, error, _) = await SendOnceAsync(transport);
        Assert.False(ok);
        Assert.IsType<McpAuthRequiredError>(error);
        Assert.Equal(2, Authorizations.Count);
        Assert.Equal(1, provider.UnauthorizedCalls);
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
