using Pi.Durable.Env;
using Pi.Durable.Testing;
using Xunit;

namespace Pi.Env.Tests;

/// <summary>
/// 环境契约测试。对应 TS <c>packages/env/test/conformance.test.ts</c>：把 Durable 的 env conformance 全套用例
/// 跑在 <see cref="RemoteExecutionEnv"/> 上——守护进程在同一台机器上执行系统调用，客户端施加 Node 的规则。
/// <para>宿主差异：与 TS 一致，Windows 上把 Git Bash 当作 <c>sh</c> 并关闭符号链接断言
/// （Git Bash 的 <c>ln -s</c> 在未启用原生符号链接时是复制）。TS 把 Git Bash 路径写死；
/// 这里允许用 <c>PI_ENV_BASH</c> 指到非标准安装位置。</para>
/// </summary>
public class RemoteEnvConformanceTests
{
    /// <summary>与 TS 相同：一个连接服务全部用例，每个用例一个全新目录。</summary>
    private static readonly Lazy<Connection> Shared = new(() => new Connection(TestPaths.Daemon));

    private static IReadOnlyList<EnvConformanceCase> Cases() =>
        EnvConformance.CreateEnvConformance(new EnvConformanceOptions
        {
            Assertions = ConformanceAssertions.CreateEnv(),
            WithEnv = async use =>
            {
                using var directory = new TempDir();
                var env = new RemoteExecutionEnv(new RemoteExecutionEnvOptions
                {
                    Connection = Shared.Value,
                    Id = "pi-env:test",
                    Cwd = directory.Path,
                    Watch = new RemoteWatchOptions { PollIntervalMs = 100 },
                });
                try
                {
                    await use(env);
                }
                finally
                {
                    await env.CleanupAsync(Pi.Chord.Context.Context.Background);
                }
            },
            Shell = TestCommands.HasPosixShell ? TestCommands.PosixShell : null,
            Symlinks = OperatingSystem.IsWindows() ? false : null,
        });

    public static IEnumerable<object[]> CaseNames() =>
        Cases().Select(@case => new object[] { @case.Name });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task PassesEnvConformanceSuite(string caseKey)
    {
        var @case = Cases().Single(candidate => candidate.Name == caseKey);
        await @case.Run();
    }
}
