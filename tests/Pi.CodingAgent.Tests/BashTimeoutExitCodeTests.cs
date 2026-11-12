using Pi.CodingAgent.Core.Tools;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 1.5.1 #11613：bash 命令 shell 已先于超时退出、但它启动的后台进程持续写输出直到超时时，
/// 超时文案除「Command timed out after N seconds」外还要报告 shell 的退出码与退出耗时；
/// shell 仍在运行（或自定义 BashOperations 抛 timeout:N）时保持普通文案。
/// </summary>
public class BashTimeoutExitCodeTests
{
    [Fact]
    public void PlainTimeout_ReportsOnlyTheDeadline()
    {
        var message = BashTool.BuildTimeoutMessage("3", null);
        Assert.Equal("Command timed out after 3 seconds", message);
    }

    [Fact]
    public void ShellExitedBeforeDeadline_ReportsExitCodeAndElapsed()
    {
        var message = BashTool.BuildTimeoutMessage("3", new ShellExitInfo(1, 1.1));
        Assert.Equal(
            "Command timed out after 3 seconds. The shell exited with code 1 after 1.1 seconds, " +
            "but background processes it started kept writing output until the timeout.",
            message);
    }

    [Fact]
    public void ShellExitSecondsAreFormattedInvariantly()
    {
        // 0 秒 / 整数秒不应出现本地化逗号或多余小数。
        Assert.Contains("after 2 seconds", BashTool.BuildTimeoutMessage("3", new ShellExitInfo(0, 2.0)));
        Assert.Contains("after 0.5 seconds", BashTool.BuildTimeoutMessage("3", new ShellExitInfo(7, 0.5)));
    }
}
