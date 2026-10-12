using Pi.Ai.Auth;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 测试用认证上下文：把环境查询完全收敛到调用方注入的 <paramref name="env"/> 委托，
/// 文件存在性一律报告 false。对应 TS 侧「在干净机器上跑测试」的隐含前提——TS 的
/// <c>defaultAuthContext()</c> 读进程真实 <c>process.env</c>，CI 上没有
/// <c>ANTHROPIC_AUTH_TOKEN</c> / <c>OPENAI_API_KEY</c> 等变量；本机开发 shell 带这些变量，
/// 会把 builtin provider 误判为「已配置」并污染可用性快照断言，故移植侧测试显式注入本类自密封。
/// </summary>
/// <remarks>
/// 与 <see cref="CreateModelRuntimeOptions.Env"/> 配套：后者只影响 <c>PI_OFFLINE</c>
/// （ TS 同款语义 ），前者覆盖认证解析读到的全部环境变量与凭据文件。
/// </remarks>
internal sealed class StubAuthContext(Func<string, string?> env) : IAuthContext
{
    public Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default)
    {
        var value = env(name);
        return Task.FromResult(value is { Length: > 0 } ? value : null);
    }

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
