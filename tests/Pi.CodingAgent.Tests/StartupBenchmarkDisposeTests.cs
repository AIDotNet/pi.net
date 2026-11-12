using Pi.CodingAgent.Cli;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 1.5.2 #11736：PI_STARTUP_BENCHMARK 退出路径不走 RunAsync/ShutdownAsync，
/// 必须在停掉 TUI 后显式释放运行时（否则 session_shutdown 等资源结算被跳过）。
/// </summary>
public sealed class StartupBenchmarkDisposeTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousAgentDir;

    public StartupBenchmarkDisposeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-bench-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _previousAgentDir = Environment.GetEnvironmentVariable(Config.EnvAgentDir);
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, Path.Combine(_root, "agent"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, _previousAgentDir);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task BenchmarkExitDisposesTheRuntime()
    {
        var shutdownObserved = false;
        var factory = new InlineExtension.Described(
            "bench-dispose-probe",
            api =>
            {
                api.On<ExtensionEvent.SessionShutdown>("session_shutdown", (_, _) =>
                {
                    shutdownObserved = true;
                    return Task.CompletedTask;
                });
                return Task.CompletedTask;
            },
            Builtin: true);

        var output = new StringWriter();
        var error = new StringWriter();
        var options = new MainOptions
        {
            ExtensionFactories = [factory],
            Cwd = _root,
            AgentDir = Path.Combine(_root, "agent"),
            Out = output,
            Error = error,
            StdinIsTty = true,
            StdoutIsTty = true,
            ReadPipedStdin = () => Task.FromResult<string?>(null),
            GetEnv = name =>
            {
                if (name == "PI_STARTUP_BENCHMARK") return "1";
                if (name == Config.EnvSessionDir) return null;
                return Environment.GetEnvironmentVariable(name);
            },
        };

        int code;
        try
        {
            code = await Main.MainAsync(["-e", "builtin:bench-dispose-probe"], options);
        }
        catch (MainExitException ex)
        {
            code = ex.ExitCode;
        }

        Assert.Equal(0, code);
        Assert.True(shutdownObserved,
            "benchmark 退出路径没有释放运行时（未发出 session_shutdown）");
    }
}
