using Pi.CodingAgent.Core.Tools;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// grep 工具的 ripgrep 参数契约。首项必须是 <c>--no-config</c>，否则
/// <c>RIPGREP_CONFIG_PATH</c> 里的 <c>--fixed-strings</c> 会静默改变搜索语义（上游 #11203）。
/// </summary>
public sealed class GrepArgsTests
{
    [Fact]
    public void ArgsStartWithNoConfig()
    {
        var args = GrepTool.BuildRgArgs(false, false, null, "pattern", "/tmp");
        Assert.Equal("--no-config", args[0]);
        Assert.Contains("--json", args);
    }

    [Fact]
    public void ArgsPreserveFlagOrderAndTerminator()
    {
        var args = GrepTool.BuildRgArgs(true, true, "*.cs", "Foo", "/tmp");
        Assert.Equal(
            ["--no-config", "--json", "--line-number", "--color=never", "--hidden",
             "--ignore-case", "--fixed-strings", "--glob", "*.cs", "--", "Foo", "/tmp"],
            args);
    }

    [Fact]
    public void ArgsOmitOptionalFlagsWhenUnset()
    {
        var args = GrepTool.BuildRgArgs(false, false, null, "Foo", "/tmp");
        Assert.DoesNotContain("--ignore-case", args);
        Assert.DoesNotContain("--fixed-strings", args);
        Assert.DoesNotContain("--glob", args);
        // `--` 终止符后紧跟 pattern 与搜索路径。
        Assert.Equal(["Foo", "/tmp"], args.TakeLast(2));
    }
}
