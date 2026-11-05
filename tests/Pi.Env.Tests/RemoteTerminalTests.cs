using System.Text;
using Xunit;

namespace Pi.Env.Tests;

/// <summary>
/// 伪终端（<c>packages/env/src/terminal.ts</c>）：用上游 Rust 守护进程跑一个真程序，验证打开、输出与退出码。
/// </summary>
public class RemoteTerminalTests
{
    [Fact]
    public async Task RunsAProgramAndReportsItsExitCode()
    {
        using var directory = new TempDir();
        using var connection = new Connection(TestPaths.Daemon);
        var output = new List<byte>();

        RemoteTerminal terminal;
        try
        {
            terminal = await Terminals.OpenAsync(connection, new TerminalOptions
            {
                Cwd = directory.Path,
                Argv = OperatingSystem.IsWindows()
                    ? ["cmd.exe", "/c", "echo hello"]
                    : ["/bin/sh", "-c", "echo hello"],
                Cols = 80,
                Rows = 24,
                OnData = bytes =>
                {
                    lock (output) output.AddRange(bytes);
                },
            });
        }
        catch (RemoteError error) when (error.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase))
        {
            // 守护进程自身在某些平台上还没有 pty（上游同一限制），此时这条用例无从验证。
            Assert.Skip("the pi-env daemon has no pty support on this platform: " + error.Message);
            return;
        }

        Assert.True(terminal.Pid > 0);
        Assert.Equal(0, await terminal.Exited);
        Assert.Contains("hello", Encoding.UTF8.GetString([.. output]));
    }

}
