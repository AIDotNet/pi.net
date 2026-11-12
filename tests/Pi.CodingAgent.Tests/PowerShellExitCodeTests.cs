using Pi.CodingAgent.Core.Tools;
using Xunit;

namespace Pi.CodingAgent.Tests;

// ============================================================================
// 上游 v1.5.1 #11964 / #8842（core/tools/powershell.ts）：
// Windows PowerShell 5.1（powershell.exe）把「stderr 被重定向的 native 命令」写成 error
// record 并把 $? 置 false，-Command 于是从 $? 推出退出码 1，即使 git 真的 exit 0。
// 修复：仅对 powershell.exe 追加 WINDOWS_POWERSHELL_EXIT_SUFFIX——当最后的失败只是
// NativeCommandError 且 $LASTEXITCODE -eq 0 时 exit 0，否则沿用 -Command 规则；
// 命令以反引号结尾（未写完的管道）时不追加，避免后缀续上执行；pwsh.exe（7）不加。
//
// 端口 PowerShellTool.BuildCommand 为可测纯叶子，本测试逐字对照上游后缀与分支。
// ============================================================================

public sealed class PowerShellExitCodeTests
{
    private const string Utf8Prefix =
        "try { [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 } catch {}\n";

    [Fact]
    public void WindowsPowerShell51_AppendsExitSuffix()
    {
        var command = PowerShellTool.BuildCommand("git push 2>&1 | Select-Object -Last 3", "powershell.exe");
        Assert.Equal(
            Utf8Prefix + "git push 2>&1 | Select-Object -Last 3" + PowerShellTool.WindowsPowerShellExitSuffix,
            command);
        // 后缀逐字含三条规则。
        Assert.Contains("if ($?) { exit 0 }", command);
        Assert.Contains("-like \"NativeCommandError*\"", command);
        Assert.Contains("exit 1", command);
        Assert.StartsWith("\n;\n", PowerShellTool.WindowsPowerShellExitSuffix);
    }

    [Fact]
    public void PowerShell7_DoesNotAppendSuffix()
    {
        var command = PowerShellTool.BuildCommand("git push 2>&1", "pwsh.exe");
        Assert.Equal(Utf8Prefix + "git push 2>&1", command);
        Assert.DoesNotContain("NativeCommandError", command);
    }

    [Fact]
    public void NonWindowsShell_DoesNotAppendSuffix()
    {
        var command = PowerShellTool.BuildCommand("echo hi", "bash");
        Assert.Equal(Utf8Prefix + "echo hi", command);
    }

    [Fact]
    public void TrailingBacktick_SkipsSuffixToKeepParseError()
    {
        // 最后一个反引号会把命令续到后缀里；上游明确「leave such commands unchanged」。
        var command = PowerShellTool.BuildCommand("Get-ChildItem |`", "powershell.exe");
        Assert.Equal(Utf8Prefix + "Get-ChildItem |`", command);
        Assert.DoesNotContain("NativeCommandError", command);
    }

    [Fact]
    public void SuffixText_MatchesUpstreamExactly()
    {
        Assert.Equal(
            "\n;\nif ($?) { exit 0 }\n"
            + "if (($LASTEXITCODE -eq 0) -and (\"$($Error[0].FullyQualifiedErrorId)\" -like \"NativeCommandError*\")) { exit 0 }\n"
            + "exit 1",
            PowerShellTool.WindowsPowerShellExitSuffix);
    }
}
