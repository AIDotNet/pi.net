// ============================================================================
// packages/mcp/src/auth-provider.ts — HTTP 获取委托、401 上下文、认证提供者
// ============================================================================

namespace Pi.Mcp;

/// <summary>
/// HTTP 获取委托。对应 TS <c>McpFetch</c>。
/// 与 TS 一致：调用方每次传入**新建的** <see cref="HttpRequestMessage"/>（HttpRequestMessage 不可重发），
/// 返回的响应由调用方负责释放。
/// </summary>
public delegate Task<HttpResponseMessage> McpFetch(HttpRequestMessage request, CancellationToken cancellationToken);

/// <summary>
/// 401（或 403 <c>insufficient_scope</c>）上下文，交给认证提供者去刷新或重新授权。
/// 对应 TS <c>UnauthorizedContext</c>。
/// </summary>
public sealed record UnauthorizedContext
{
    /// <summary>401 响应，或带 <c>insufficient_scope</c> 挑战的 403 响应。</summary>
    public required HttpResponseMessage Response { get; init; }

    /// <summary>被拒请求所指向的服务器 URL。</summary>
    public required Uri ServerUrl { get; init; }

    /// <summary>供认证提供者自行发起请求（例如刷新令牌）的获取委托。</summary>
    public required McpFetch Fetch { get; init; }

    /// <summary>
    /// 被拒请求携带的 access token（若有）。若当前令牌已不同，说明另一个请求已刷新过，
    /// 无需再次刷新（轮转 refresh token 下二次刷新会丢掉新的授权）。
    /// </summary>
    public string? Token { get; init; }
}

/// <summary>
/// 向 MCP HTTP 传输提供 bearer 令牌，并可在 401 之后刷新。对应 TS <c>AuthProvider</c>。
/// </summary>
public interface AuthProvider
{
    /// <summary>
    /// 下一次请求要用的 access token（无则 null）。实现里可能先做一次网络刷新，所以传输只在
    /// 「会等应答」的请求前调用它；关闭会话时复用上一次请求的令牌，不再走这里（上游 f10993bc7）。
    /// </summary>
    Task<string?> Token(CancellationToken cancellationToken = default);

    /// <summary>
    /// 收到 401（或 403 <c>insufficient_scope</c>）后调用一次；返回后传输会用新凭据重试一次。
    /// <c>null</c> 表示该提供者没有刷新能力（例如直接用某个 pi provider 的令牌），此时 401 不重试——
    /// 与 TS 里 <c>onUnauthorized?</c> 可选方法同义。
    /// </summary>
    Func<UnauthorizedContext, CancellationToken, Task>? OnUnauthorized => null;
}
