// ============================================================================
// Tests for modes/interactive/components/dynamic-border.ts
// ============================================================================

using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class DynamicBorderTests : IDisposable
{
    private readonly bool? _previousChalk = Chalk.EnabledOverride;

    public DynamicBorderTests()
    {
        ThemeRuntime.ResetCachesForTests();
        Chalk.EnabledOverride = true;
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
    }

    public void Dispose()
    {
        Chalk.EnabledOverride = _previousChalk;
        ThemeRuntime.ResetCachesForTests();
    }

    [Fact]
    public void Render_DrawsARuleSpanningTheWidth()
    {
        var border = new DynamicBorder(str => str);
        var lines = border.Render(5);
        Assert.Single(lines);
        Assert.Equal(new string('─', 5), lines[0]);
    }

    [Fact]
    public void Render_ClampsToAtLeastOneColumn()
    {
        var border = new DynamicBorder(str => str);
        Assert.Equal("─", border.Render(0)[0]);
        Assert.Equal("─", border.Render(-3)[0]);
    }

    [Fact]
    public void Render_UsesTheThemeBorderTokenByDefault()
    {
        var lines = new DynamicBorder().Render(4);
        Assert.Equal(ThemeRuntime.Current.Fg("border", new string('─', 4)), lines[0]);
    }

    [Fact]
    public void Render_UsesAnExplicitColorFunction()
    {
        var border = new DynamicBorder(str => $"[{str}]");
        Assert.Equal($"[{new string('─', 3)}]", border.Render(3)[0]);
    }

    [Fact]
    public void Invalidate_IsANoOp()
    {
        var border = new DynamicBorder(str => str);
        var before = border.Render(3);
        border.Invalidate();
        Assert.Equal(before, border.Render(3));
    }
}
