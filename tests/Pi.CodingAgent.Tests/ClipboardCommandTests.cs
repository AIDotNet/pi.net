// ============================================================================
// 剪贴板命令的子进程不能留在 pi 的进程组里。
//
// 上游 f8ab362cc（#11179）：剪贴板写入器常会自我守护化（如 wl-copy 常驻），若它与 pi 同一个
// 进程组，Ctrl+Z（向整个组发 SIGTSTP）会把真正的剪贴板属主一起停掉。上游在非 Windows 上
// `detached: true`，端口对应 `SpawnOptions.Detached`（Unix 经 setsid）。
// ============================================================================

using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

public class ClipboardCommandTests
{
    [Fact]
    public async Task ReadsTheCommandsStandardOutput()
    {
        var output = OperatingSystem.IsWindows()
            ? await ClipboardCommand.RunAsync("cmd.exe", ["/d", "/s", "/c", "echo clipboard"])
            : await ClipboardCommand.RunAsync("sh", ["-c", "printf clipboard"]);

        Assert.Equal("clipboard", System.Text.Encoding.UTF8.GetString(output ?? []).Trim());
    }

    /// <summary>子进程的进程组与 pi 自己的不同。上游同名用例在 Windows 上跳过（那里没有进程组）。</summary>
    [Fact]
    public async Task RunsCommandsOutsideTheParentProcessGroup()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Process groups are a POSIX concept; upstream detaches only off Windows as well.");
        }

        var childPgid = Read((await ClipboardCommand.RunAsync("sh", ["-c", "ps -o pgid= -p $$"]))!);
        var ownPgid = Read((await ClipboardCommand.RunAsync("ps", ["-o", "pgid=", "-p", Environment.ProcessId.ToString()]))!);

        Assert.Matches("^\\d+$", childPgid);
        Assert.Matches("^\\d+$", ownPgid);
        Assert.NotEqual(ownPgid, childPgid);
    }

    private static string Read(byte[] output) => System.Text.Encoding.UTF8.GetString(output).Trim();
}
