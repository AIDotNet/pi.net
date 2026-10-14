// ============================================================================
// Tests for modes/interactive/theme/system-theme.ts
// ============================================================================
//
// Transcribed from packages/coding-agent/test/system-theme.test.ts. The recipe itself (families,
// levels, rules) is data; what these tests pin is the behaviour it is supposed to produce: readable
// body text, foreground roles ordered by contrast, panels that stay near the background, hue taken
// from the terminal's palette, and the three report tiers the terminal can land in.

using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class SystemThemeTests
{
    // ---------------------------------------------------------------------------------------------
    // Fixtures (test/system-theme.test.ts)
    // ---------------------------------------------------------------------------------------------

    private static RgbColor Rgb(string hex) => Colors.ColorToRgb(Colors.ParseColor(hex));

    private static OklchChannels Oklch(RgbColor color) => Colors.ColorToOklch(Colors.Rgb(color.R, color.G, color.B));

    private static double Lightness(RgbColor color) => Oklch(color).L;

    private static double Hue(RgbColor color) => Oklch(color).H;

    private static double Chroma(RgbColor color) => Oklch(color).C;

    private static readonly SystemThemeInput Dracula = new()
    {
        Background = Rgb("#282a36"),
        Foreground = Rgb("#f8f8f2"),
        Palette =
        [
            Rgb("#21222c"), Rgb("#ff5555"), Rgb("#50fa7b"), Rgb("#f1fa8c"),
            Rgb("#bd93f9"), Rgb("#ff79c6"), Rgb("#8be9fd"), Rgb("#f8f8f2"),
            Rgb("#6272a4"), Rgb("#ff6e6e"), Rgb("#69ff94"), Rgb("#ffffa5"),
            Rgb("#d6acff"), Rgb("#ff92df"), Rgb("#a4ffff"), Rgb("#ffffff"),
        ],
    };

    /// <summary>Dark with a palette, light with an unreadable foreground, background only, and mid-gray.</summary>
    private static readonly IReadOnlyDictionary<string, SystemThemeInput> Terminals =
        new Dictionary<string, SystemThemeInput>(StringComparer.Ordinal)
        {
            ["dracula"] = Dracula,
            ["solarizedLight"] = new() { Background = Rgb("#fdf6e3"), Foreground = Rgb("#657b83") },
            ["backgroundOnly"] = new() { Background = Rgb("#1e1e1e") },
            ["midGray"] = new() { Background = Rgb("#808080"), Foreground = Rgb("#ffffff") },
        };

    private static readonly string[] Panels =
        ["userMessageBg", "toolPendingBg", "toolSuccessBg", "toolErrorBg", "selectedBg"];

    /// <summary>The concrete color behind a token, resolving the terminal defaults the recipe leaves as <c>""</c>.</summary>
    private static RgbColor Resolved(SystemThemeInput input, string token)
    {
        var value = SystemTheme.GenerateColors(input).Colors[token];
        if (value.IsText && value.Text!.Length == 0)
        {
            return Panels.Contains(token) ? input.Background!.Value : input.Foreground!.Value;
        }
        return Rgb(value.Text!);
    }

    // ---------------------------------------------------------------------------------------------
    // generateSystemThemeColors
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void GenerateColors_KeepsBodyTextReadableOnTheBackgroundAndItsPanels()
    {
        foreach (var (name, input) in Terminals)
        {
            var text = Resolved(input, ThemeColors.Text);
            foreach (var surface in new[] { input.Background!.Value, Resolved(input, ThemeBackgrounds.SelectedBg) })
            {
                Assert.True(
                    SystemTheme.WcagContrast(text, surface) >= 4.5,
                    $"{name}: text contrast {SystemTheme.WcagContrast(text, surface):F3} on {surface}");
            }
            var title = SystemTheme.WcagContrast(
                Resolved(input, ThemeColors.ToolTitle),
                Resolved(input, ThemeBackgrounds.ToolErrorBg));
            Assert.True(title >= 4.5, $"{name}: toolTitle on toolErrorBg was {title:F3}");
        }
    }

    [Fact]
    public void GenerateColors_OrdersForegroundRolesByContrastAndKeepsPanelsNearTheBackground()
    {
        foreach (var (name, input) in Terminals)
        {
            var background = Lightness(input.Background!.Value);
            double Offset(string token) => Lightness(Resolved(input, token)) - background;

            // On mid-gray the levels collapse to the strongest reachable color.
            if (name != "midGray")
            {
                Assert.True(Math.Abs(Offset(ThemeColors.Text)) > Math.Abs(Offset(ThemeColors.Muted)),
                    $"{name}: text is not further from the background than muted");
                Assert.True(Math.Abs(Offset(ThemeColors.Muted)) > Math.Abs(Offset(ThemeColors.Dim)),
                    $"{name}: muted is not further from the background than dim");
            }

            var lighter = SystemTheme.GenerateColors(input).Appearance == ThemeAppearance.Dark;
            foreach (var panel in Panels)
            {
                var contrast = SystemTheme.WcagContrast(Resolved(input, panel), input.Background!.Value);
                Assert.True(contrast < 2, $"{name} {panel}: contrast {contrast:F3} is not below 2");
                Assert.True(Offset(panel) > 0 == lighter, $"{name} {panel}: panel moved the wrong way");
            }
        }
    }

    [Fact]
    public void GenerateColors_UsesTheTerminalForegroundAndPaletteHues()
    {
        Assert.Equal("", SystemTheme.GenerateColors(Dracula).Colors[ThemeColors.Text].Text);

        // Solarized's foreground is below 4.5:1 on its own background, so text is darkened.
        Assert.NotEqual("", SystemTheme.GenerateColors(Terminals["solarizedLight"]).Colors[ThemeColors.Text].Text);

        var errorHue = Hue(Resolved(Dracula, ThemeColors.Error));
        var paletteHue = Hue(Dracula.Palette![1]);
        Assert.True(Math.Abs(errorHue - paletteHue) < 8, $"error hue {errorHue:F2} vs palette {paletteHue:F2}");
    }

    [Fact]
    public void GenerateColors_KeepsPastelPaletteColorsPastelAtOtherLightnesses()
    {
        // https://github.com/earendil-works/pi/issues/10255
        var frappe = new SystemThemeInput
        {
            Background = Rgb("#303446"),
            Foreground = Rgb("#c6d0f5"),
            Palette =
            [
                Rgb("#51576d"), Rgb("#e78284"), Rgb("#a6d189"), Rgb("#e5c890"),
                Rgb("#8caaee"), Rgb("#f4b8e4"), Rgb("#81c8be"), Rgb("#b5bfe2"),
                Rgb("#626880"), Rgb("#e67172"), Rgb("#8ec772"), Rgb("#d9ba73"),
                Rgb("#7b9ef0"), Rgb("#f2a4db"), Rgb("#5abfb5"), Rgb("#a5adce"),
            ],
        };
        var pink = frappe.Palette![5];
        var accent = Resolved(frappe, ThemeColors.Accent);

        // The accent is darker than the pink, but must not gain chroma (it was 2x before the cap).
        Assert.True(Lightness(accent) < Lightness(pink) - 0.05,
            $"accent lightness {Lightness(accent):F4} vs pink {Lightness(pink):F4}");
        Assert.True(Chroma(accent) <= Chroma(pink) * 1.03,
            $"accent chroma {Chroma(accent):F4} vs pink {Chroma(pink):F4}");
        foreach (var panel in new[] { ThemeBackgrounds.UserMessageBg, ThemeBackgrounds.CustomMessageBg })
        {
            Assert.True(Chroma(Resolved(frappe, panel)) <= 0.1, $"{panel} is too saturated");
        }
    }

    [Fact]
    public void GenerateColors_RendersGrayscaleAtZeroSaturation()
    {
        var colors = SystemTheme.GenerateColors(Dracula with { Saturation = 0 }).Colors;
        Assert.True(Chroma(Rgb(colors[ThemeColors.Error].Text!)) < 0.005);
    }

    [Fact]
    public void GenerateColors_FallsBackToPaletteIndicesAndFaintTextWithoutABackground()
    {
        var generated = SystemTheme.GenerateColors(new SystemThemeInput { AppearanceHint = ThemeAppearance.Light });
        Assert.Equal(ThemeAppearance.Light, generated.Appearance);
        Assert.Equal(1, generated.Colors[ThemeColors.Error].Index);
        Assert.Equal("", generated.Colors[ThemeColors.Text].Text);
        Assert.Equal("", generated.Colors[ThemeBackgrounds.UserMessageBg].Text);
        Assert.Contains(ThemeColors.Muted, generated.Dim);
        Assert.Equal("", SystemTheme.GenerateColors(new SystemThemeInput { Saturation = 0 }).Colors[ThemeColors.Error].Text);
    }

    [Fact]
    public void GenerateColors_IgnoresAPaletteThatIsNotTheFullSixteen()
    {
        // TS requires `palette.length === 16`; a short palette is dropped entirely, so the families'
        // own hues are used instead and the result is the same as reporting no palette at all.
        static string Digest(SystemThemeColors colors) => string.Join(
            ";",
            colors.Colors
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

        var shortPalette = SystemTheme.GenerateColors(Dracula with { Palette = [Rgb("#ff5555"), Rgb("#50fa7b")] });
        var noPalette = SystemTheme.GenerateColors(Dracula with { Palette = null });
        Assert.Equal(Digest(noPalette), Digest(shortPalette));

        // A complete palette does change the result, so the equality above is not vacuous.
        Assert.NotEqual(Digest(SystemTheme.GenerateColors(Dracula)), Digest(shortPalette));
    }

    [Fact]
    public void GenerateColors_DefaultsSaturationToOneAndClampsIt()
    {
        Assert.Equal(
            SystemTheme.GenerateColors(Dracula).Colors[ThemeColors.Accent].Text,
            SystemTheme.GenerateColors(Dracula with { Saturation = 1 }).Colors[ThemeColors.Accent].Text);
        Assert.Equal(
            SystemTheme.GenerateColors(Dracula with { Saturation = 5 }).Colors[ThemeColors.Accent].Text,
            SystemTheme.GenerateColors(Dracula with { Saturation = 1 }).Colors[ThemeColors.Accent].Text);
        Assert.Equal(
            SystemTheme.GenerateColors(Dracula with { Saturation = -1 }).Colors[ThemeColors.Accent].Text,
            SystemTheme.GenerateColors(Dracula with { Saturation = 0 }).Colors[ThemeColors.Accent].Text);
    }

    [Fact]
    public void GenerateColors_PublishesEveryToken()
    {
        var generated = SystemTheme.GenerateColors(Dracula);
        Assert.Equal(ThemeTokens.All.Count, generated.Colors.Count);
        foreach (var token in ThemeTokens.All)
        {
            Assert.True(generated.Colors.ContainsKey(token), $"missing {token}");
        }
    }

    [Fact]
    public void GenerateColors_ReportsTheAppearanceItUsed()
    {
        Assert.Equal(ThemeAppearance.Dark, SystemTheme.GenerateColors(Dracula).Appearance);
        Assert.Equal(ThemeAppearance.Light, SystemTheme.GenerateColors(Terminals["solarizedLight"]).Appearance);
        Assert.Equal(ThemeAppearance.Dark, SystemTheme.GenerateColors(Terminals["backgroundOnly"]).Appearance);
    }

    // ---------------------------------------------------------------------------------------------
    // wcagContrast / terminalAppearance
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void WcagContrast_MatchesTheWcagTwoDefinition()
    {
        Assert.Equal(21, SystemTheme.WcagContrast(Rgb("#ffffff"), Rgb("#000000")), 6);
        Assert.Equal(1, SystemTheme.WcagContrast(Rgb("#123456"), Rgb("#123456")), 6);
        // Order-independent.
        Assert.Equal(
            SystemTheme.WcagContrast(Rgb("#ffffff"), Rgb("#808080")),
            SystemTheme.WcagContrast(Rgb("#808080"), Rgb("#ffffff")), 12);
    }

    [Theory]
    [InlineData("#000000", 0.0)]
    [InlineData("#ffffff", 1.0)]
    public void RelativeLuminance_IsZeroForBlackAndOneForWhite(string hex, double expected) =>
        Assert.Equal(expected, SystemTheme.RelativeLuminance(Rgb(hex)), 6);

    [Fact]
    public void TerminalAppearance_FollowsTheForegroundWhenTextIsReadableThatWay()
    {
        var background = new RgbColor(118, 118, 118);
        Assert.Equal(ThemeAppearance.Light, SystemTheme.TerminalAppearance(background));
        Assert.Equal(
            ThemeAppearance.Dark,
            SystemTheme.TerminalAppearance(background, new RgbColor(255, 255, 255)));
        // White text cannot reach 4.5:1 on mid-gray.
        Assert.Equal(
            ThemeAppearance.Light,
            SystemTheme.TerminalAppearance(new RgbColor(128, 128, 128), new RgbColor(255, 255, 255)));
    }

    [Fact]
    public void TerminalAppearance_FallsBackToWhichOfBlackOrWhiteContrastsMore()
    {
        Assert.Equal(ThemeAppearance.Dark, SystemTheme.TerminalAppearance(Rgb("#101010")));
        Assert.Equal(ThemeAppearance.Light, SystemTheme.TerminalAppearance(Rgb("#f0f0f0")));
        // A foreground too close in lightness to the background is ignored.
        Assert.Equal(
            ThemeAppearance.Dark,
            SystemTheme.TerminalAppearance(Rgb("#101010"), Rgb("#121212")));
    }

    [Fact]
    public void SystemThemeName_IsTheReservedName() => Assert.Equal("system", SystemTheme.SystemThemeName);
}
