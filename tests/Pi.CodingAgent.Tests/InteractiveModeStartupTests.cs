// ============================================================================
// Tests for the startup-listing and changelog helpers of interactive-mode.ts (4f-4f).
//
// Upstream has no test file for these. The changelog regex cases were checked against the TS
// expression directly: /##\s+\[?(\d+\.\d+\.\d+)\]?/ with no flags.
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class InteractiveModeStartupTests : IDisposable
{
    private readonly bool? _previousChalkOverride = Chalk.EnabledOverride;
    private readonly Theme _theme;

    public InteractiveModeStartupTests()
    {
        Chalk.EnabledOverride = true;
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
        _theme = ThemeRuntime.Current;
    }

    public void Dispose()
    {
        Chalk.EnabledOverride = _previousChalkOverride;
        ThemeRuntime.ResetCachesForTests();
    }

    // ---------------------------------------------------------------- section header

    [Fact]
    public void SectionHeaderWrapsTheNameInBrackets()
    {
        Assert.Equal(
            _theme.Fg(ThemeColors.MdHeading, "[Skills]"),
            InteractiveModeStartup.FormatSectionHeader("Skills", _theme));
    }

    [Fact]
    public void SectionHeaderAcceptsAnExplicitColor()
    {
        Assert.Equal(
            _theme.Fg(ThemeColors.Warning, "[Context]"),
            InteractiveModeStartup.FormatSectionHeader("Context", _theme, ThemeColors.Warning));
    }

    // ---------------------------------------------------------------- compact list

    [Fact]
    public void CompactListTrimsDropsEmptiesSortsAndIndents()
    {
        Assert.Equal(
            _theme.Fg(ThemeColors.Dim, "  a, b"),
            InteractiveModeStartup.FormatCompactList([" b ", "a", "", "   "], _theme));
    }

    [Fact]
    public void CompactListCanKeepTheInputOrder()
    {
        Assert.Equal(
            _theme.Fg(ThemeColors.Dim, "  b, a"),
            InteractiveModeStartup.FormatCompactList([" b ", "a"], _theme, sort: false));
    }

    [Fact]
    public void AnEmptyCompactListIsJustTheIndent()
    {
        // TS joins an empty array to "", so the two-space indent survives.
        Assert.Equal(_theme.Fg(ThemeColors.Dim, "  "), InteractiveModeStartup.FormatCompactList([], _theme));
        Assert.Equal(_theme.Fg(ThemeColors.Dim, "  "), InteractiveModeStartup.FormatCompactList(["  "], _theme));
    }

    // ---------------------------------------------------------------- changelog version

    [Theory]
    [InlineData("## [1.2.3]\n## 2.0.0", "1.2.3")]
    [InlineData("## 1.2.3", "1.2.3")]
    [InlineData("intro\n\n## 4.5.6\nmore", "4.5.6")]
    [InlineData("## [1.2.3-beta.1]", "1.2.3")] // the trailing \]? is optional and -beta is simply not consumed
    [InlineData("##   9.9.9", "9.9.9")]         // \s+ takes any run of whitespace
    [InlineData("no headings here", null)]
    [InlineData("## v1.2.3", null)]            // `\[?` cannot swallow the "v"
    [InlineData("## 1.2", null)]               // three components required
    [InlineData("", null)]
    [InlineData(null, null)]
    public void LatestChangelogVersionTakesTheFirstThreePartHeading(string? markdown, string? expected)
    {
        Assert.Equal(expected, InteractiveModeStartup.ExtractLatestChangelogVersion(markdown));
    }

    [Fact]
    public void CondensedNoticeNamesTheDetectedVersion()
    {
        Assert.Equal(
            $"Updated to v1.2.3. Use {_theme.Bold("/changelog")} to view full changelog.",
            InteractiveModeStartup.BuildCondensedChangelogNotice("## [1.2.3]", "9.9.9", _theme));
    }

    [Fact]
    public void CondensedNoticeFallsBackToTheRunningVersion()
    {
        Assert.Equal(
            $"Updated to v9.9.9. Use {_theme.Bold("/changelog")} to view full changelog.",
            InteractiveModeStartup.BuildCondensedChangelogNotice("no headings", "9.9.9", _theme));
    }

    // ---------------------------------------------------------------- extension diagnostics

    [Fact]
    public void LoadErrorsBecomeErrorDiagnostics()
    {
        var diagnostics = InteractiveModeStartup.CollectExtensionDiagnostics(
            [new ExtensionLoadError("/tmp/a.ts", "boom")]);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(ResourceDiagnosticType.Error, diagnostic.Type);
        Assert.Equal("boom", diagnostic.Message);
        Assert.Equal("/tmp/a.ts", diagnostic.Path);
    }

    [Fact]
    public void LoadWarningsBecomeWarningDiagnosticsAfterTheErrors()
    {
        var diagnostics = InteractiveModeStartup.CollectExtensionDiagnostics(
            [new ExtensionLoadError("/tmp/a.ts", "boom")],
            [new ExtensionLoadWarning("/tmp/b.ts", "careful")]);

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(ResourceDiagnosticType.Error, diagnostics[0].Type);
        Assert.Equal(ResourceDiagnosticType.Warning, diagnostics[1].Type);
        Assert.Equal("careful", diagnostics[1].Message);
        Assert.Equal("/tmp/b.ts", diagnostics[1].Path);
    }

    [Fact]
    public void AWarningListOfNullMeansNone()
    {
        // TS: `for (const warning of extensionsResult.warnings ?? [])`.
        Assert.Empty(InteractiveModeStartup.CollectExtensionDiagnostics([], null));
    }
}
