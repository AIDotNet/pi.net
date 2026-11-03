using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <c>pi.exec</c> hardening from the 1.5.0 window: an output cap that terminates the command
/// (TS <c>c466a3595</c>), and a spawn/wait failure that keeps its message visible through stderr
/// (TS <c>c60de42e9</c>).
/// </summary>
public sealed class ExecWindowTests
{
    private static string Shell => OperatingSystem.IsWindows() ? "cmd" : "sh";

    private static IReadOnlyList<string> EchoArgs(string text) =>
        OperatingSystem.IsWindows() ? ["/c", $"echo {text}"] : ["-c", $"printf '%s' '{text}'"];

    [Fact]
    public async Task ExecCommand_CapturesOutputUnderTheCap()
    {
        var result = await Exec.ExecCommand(Shell, EchoArgs("hello"), Path.GetTempPath());

        Assert.Equal(0, result.Code);
        Assert.Contains("hello", result.Stdout);
        Assert.False(result.Killed);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task ExecCommand_TruncatesAndKillsWhenOutputPassesMaxBuffer()
    {
        // 64 KiB of output against a 1 KiB cap: the pump kills the child.
        string[] args = OperatingSystem.IsWindows()
            ? ["/c", "for /L %i in (1,1,20000) do @echo 0123456789"]
            : ["-c", "i=0; while [ $i -lt 20000 ]; do echo 0123456789; i=$((i+1)); done"];

        var result = await Exec.ExecCommand(
            Shell,
            args,
            Path.GetTempPath(),
            new ExecOptions { MaxBuffer = 1024 });

        Assert.True(result.Truncated);
        Assert.True(result.Killed);
        Assert.NotEqual(0, result.Code);
        Assert.True(result.Stdout.Length <= 1024 + 8192, "captured output must stop near the cap");
    }

    [Fact]
    public async Task ExecCommand_ReportsTheOutputCapDefaultAsUnlimitedForSmallOutput()
    {
        var result = await Exec.ExecCommand(Shell, EchoArgs("cap"), Path.GetTempPath(), new ExecOptions());

        Assert.False(result.Truncated);
        Assert.Equal(0, result.Code);
    }
}
