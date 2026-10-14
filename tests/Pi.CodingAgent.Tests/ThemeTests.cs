// ============================================================================
// Tests for the Theme class in modes/interactive/theme/theme.ts
// ============================================================================
//
// Transcribed from packages/coding-agent/test/theme-style.test.ts. Every case loads a copy of a
// built-in theme with one edit applied, which is how the TS suite keeps the fixture in sync with the
// shipped palette while still exercising variable resolution, slot splitting and appearance detection.

using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class ThemeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-theme-style-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly bool? _previousChalkOverride = Chalk.EnabledOverride;

    public ThemeTests()
    {
        Directory.CreateDirectory(_root);
        ThemeRuntime.ResetCachesForTests();
        // theme.bold()/italic()/… delegate to chalk, which disables itself when stdout is redirected.
        Chalk.EnabledOverride = true;
    }

    public void Dispose()
    {
        Chalk.EnabledOverride = _previousChalkOverride;
        ThemeRuntime.ResetCachesForTests();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------------------------------

    /// <summary>Load a copy of a built-in theme, modified by <paramref name="edit"/>.</summary>
    private Theme LoadTheme(string baseName, Action<JsonObject>? edit = null)
    {
        var json = (JsonObject)JsonNode.Parse(
            File.ReadAllText(Path.Combine(Config.GetThemesDir(), $"{baseName}.json")))!;
        edit?.Invoke(json);
        var path = Path.Combine(_root, $"{json["name"]!.GetValue<string>()}.json");
        File.WriteAllText(path, json.ToJsonString());
        return ThemeRuntime.LoadThemeFromPath(path, TerminalColorMode.Truecolor);
    }

    private static JsonObject ColorsOf(JsonObject theme) => (JsonObject)theme["colors"]!;

    // ---------------------------------------------------------------------------------------------
    // Styling
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Style_RendersThemeTokensTheSameAsTheGenericTextStyler()
    {
        var theme = LoadTheme("dark");
        var actual = theme.Style(
            "Ready",
            new ThemeStyle
            {
                FgToken = ThemeColors.Success,
                BgToken = ThemeBackgrounds.ToolSuccessBg,
                Bold = true,
            });
        var expected = Colors.StyleText(
            "Ready",
            new TextStyle
            {
                Fg = theme.Colors[ThemeColors.Success],
                Bg = theme.Colors[ThemeBackgrounds.ToolSuccessBg],
                Bold = true,
            },
            TerminalColorMode.Truecolor);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Style_PrefersTheTokenOverADirectColor()
    {
        var theme = LoadTheme("dark");
        var withToken = theme.Style("x", new ThemeStyle { FgToken = ThemeColors.Success });
        var withColor = theme.Style("x", new ThemeStyle { Fg = theme.Colors[ThemeColors.Success] });
        Assert.Equal(withColor, withToken);
    }

    [Fact]
    public void Style_RejectsUnknownTokensAndTokensInTheWrongSlot()
    {
        var theme = LoadTheme("dark");
        Assert.Equal(
            "Unknown theme color: notAToken",
            Assert.Throws<InvalidOperationException>(
                () => theme.Style("x", new ThemeStyle { FgToken = "notAToken" })).Message);
        // Background tokens are not foreground colors; use theme.Colors[token] instead.
        Assert.Equal(
            "Unknown theme color: userMessageBg",
            Assert.Throws<InvalidOperationException>(
                () => theme.Style("x", new ThemeStyle { FgToken = ThemeBackgrounds.UserMessageBg })).Message);
        // And the other way round.
        Assert.Equal(
            "Unknown theme color: accent",
            Assert.Throws<InvalidOperationException>(
                () => theme.Style("x", new ThemeStyle { BgToken = ThemeColors.Accent })).Message);
    }

    [Fact]
    public void FgAndBg_CloseWithTheDefaultCodes()
    {
        var theme = LoadTheme("dark");
        Assert.Equal(theme.GetFgAnsi(ThemeColors.Accent) + "x\x1b[39m", theme.Fg(ThemeColors.Accent, "x"));
        Assert.Equal(theme.GetBgAnsi(ThemeBackgrounds.SelectedBg) + "x\x1b[49m",
            theme.Bg(ThemeBackgrounds.SelectedBg, "x"));
    }

    [Fact]
    public void TextAttributes_DelegateToChalk()
    {
        var theme = LoadTheme("dark");
        Assert.Equal("\x1b[1mx\x1b[22m", theme.Bold("x"));
        Assert.Equal("\x1b[3mx\x1b[23m", theme.Italic("x"));
        Assert.Equal("\x1b[4mx\x1b[24m", theme.Underline("x"));
        Assert.Equal("\x1b[7mx\x1b[27m", theme.Inverse("x"));
        Assert.Equal("\x1b[9mx\x1b[29m", theme.Strikethrough("x"));
    }

    [Fact]
    public void ThinkingBorderColor_MapsEveryLevelToItsToken()
    {
        var theme = LoadTheme("dark");
        Assert.Equal(
            theme.Fg(ThemeColors.ThinkingOff, "x"),
            theme.GetThinkingBorderColor(ThinkingLevel.Off)("x"));
        Assert.Equal(
            theme.Fg(ThemeColors.ThinkingMinimal, "x"),
            theme.GetThinkingBorderColor(ThinkingLevel.Minimal)("x"));
        Assert.Equal(
            theme.Fg(ThemeColors.ThinkingLow, "x"),
            theme.GetThinkingBorderColor(ThinkingLevel.Low)("x"));
        Assert.Equal(
            theme.Fg(ThemeColors.ThinkingMedium, "x"),
            theme.GetThinkingBorderColor(ThinkingLevel.Medium)("x"));
        Assert.Equal(
            theme.Fg(ThemeColors.ThinkingHigh, "x"),
            theme.GetThinkingBorderColor(ThinkingLevel.High)("x"));
        Assert.Equal(
            theme.Fg(ThemeColors.ThinkingXhigh, "x"),
            theme.GetThinkingBorderColor(ThinkingLevel.XHigh)("x"));
        Assert.Equal(
            theme.Fg(ThemeColors.ThinkingMax, "x"),
            theme.GetThinkingBorderColor(ThinkingLevel.Max)("x"));
    }

    [Fact]
    public void BashModeBorderColor_UsesTheBashModeToken()
    {
        var theme = LoadTheme("dark");
        Assert.Equal(theme.Fg(ThemeColors.BashMode, "x"), theme.GetBashModeBorderColor()("x"));
    }

    [Fact]
    public void GetColorMode_ReportsTheModeItWasBuiltWith()
    {
        Assert.Equal(TerminalColorMode.Truecolor, LoadTheme("dark").GetColorMode());

        var json = (JsonObject)JsonNode.Parse(
            File.ReadAllText(Path.Combine(Config.GetThemesDir(), "dark.json")))!;
        var path = Path.Combine(_root, "ansi256.json");
        File.WriteAllText(path, json.ToJsonString());
        var ansi256 = ThemeRuntime.LoadThemeFromPath(path, TerminalColorMode.Color256);
        Assert.Equal(TerminalColorMode.Color256, ansi256.GetColorMode());
        Assert.Matches(@"^\x1b\[38;5;[0-9]+m$", ansi256.GetFgAnsi(ThemeColors.Accent));
    }

    // ---------------------------------------------------------------------------------------------
    // Color values
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void LoadsOklchThemeValues()
    {
        var theme = LoadTheme("dark", json => ColorsOf(json)[ThemeColors.Accent] = "oklch(62% 0.1 200)");
        Assert.Equal(Colors.Oklch(0.62, 0.1, 200), theme.Colors[ThemeColors.Accent]);
    }

    [Fact]
    public void LoadsOkhslThemeValuesIncludingThroughVariables()
    {
        var theme = LoadTheme("dark", json =>
        {
            ((JsonObject)json["vars"]!)["brand"] = "okhsl(250 60% 55%)";
            ColorsOf(json)[ThemeColors.Accent] = "brand";
            ColorsOf(json)[ThemeColors.Error] = "okhsl(20 90% 60%)";
        });
        Assert.Equal(
            Colors.ColorToHex(Colors.Okhsl(250, 0.6, 0.55)),
            Colors.ColorToHex(theme.Colors[ThemeColors.Accent]));
        Assert.Equal(
            Colors.ColorToHex(Colors.Okhsl(20, 0.9, 0.6)),
            Colors.ColorToHex(theme.Colors[ThemeColors.Error]));
    }

    [Fact]
    public void LoadsNumericPaletteIndices()
    {
        var theme = LoadTheme("dark", json => ColorsOf(json)[ThemeColors.Accent] = 24);
        Assert.Equal(Colors.Indexed(24), theme.Colors[ThemeColors.Accent]);
    }

    [Fact]
    public void NameAndSourcePath_ComeFromTheDocumentAndTheFilePath()
    {
        var theme = LoadTheme("dark");
        Assert.Equal("dark", theme.Name);
        Assert.Equal(Path.Combine(_root, "dark.json"), theme.SourcePath);
    }

    [Fact]
    public void SourceInfo_IsSettable()
    {
        var theme = LoadTheme("dark");
        Assert.Null(theme.SourceInfo);
        theme.SourceInfo = SourceInfos.CreateSynthetic("builtin:dark", "builtin");
        Assert.Equal("builtin:dark", theme.SourceInfo.Path);
        Assert.Equal("builtin", theme.SourceInfo.Source);
    }

    // ---------------------------------------------------------------------------------------------
    // Appearance
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Appearance_IsDetectedUnlessItIsDeclared()
    {
        Assert.Equal(ThemeAppearance.Dark, LoadTheme("dark").Appearance);
        Assert.Equal(ThemeAppearance.Light, LoadTheme("light").Appearance);

        // Without a declaration, the appearance is detected from the theme's own colors.
        foreach (var baseName in new[] { "dark", "light" })
        {
            Assert.Equal(baseName, LoadTheme(baseName, json => json.Remove("appearance")).Appearance);
        }

        Assert.Equal(
            ThemeAppearance.Light,
            LoadTheme("dark", json => json["appearance"] = ThemeAppearance.Light).Appearance);
    }

    [Fact]
    public void Appearance_FollowsTheTerminalBackgroundForPaletteOnlyThemes()
    {
        // Palette colors 0-15 follow the terminal palette, so such themes follow the terminal background.
        var paletteOnly = LoadTheme("dark", json =>
        {
            json.Remove("appearance");
            var colors = ColorsOf(json);
            foreach (var key in colors.Select(pair => pair.Key).ToList())
            {
                colors[key] = key.EndsWith("Bg", StringComparison.Ordinal) ? 0 : 7;
            }
        });

        Assert.Equal(ThemeAppearance.Dark, paletteOnly.Appearance);

        ThemeRuntime.SetTerminalColors(new TerminalColorsSnapshot(Background: new RgbColor(250, 250, 250)));
        Assert.Equal(ThemeAppearance.Light, paletteOnly.Appearance);
    }

    // ---------------------------------------------------------------------------------------------
    // Terminal defaults
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RendersEmptyTokensAsTerminalDefaultsAndReportsConcreteColorsForThem()
    {
        var theme = LoadTheme("dark", json =>
        {
            ColorsOf(json)[ThemeColors.Text] = "";
            ColorsOf(json)[ThemeBackgrounds.UserMessageBg] = "";
        });

        Assert.Equal("\x1b[39mx\x1b[39m", theme.Fg(ThemeColors.Text, "x"));
        Assert.Equal("\x1b[49mx\x1b[49m", theme.Bg(ThemeBackgrounds.UserMessageBg, "x"));
        // Nothing reported yet, so the guessed defaults for a dark appearance apply.
        Assert.Equal("#e5e5e7", Colors.ColorToHex(theme.Colors[ThemeColors.Text]));
        Assert.Equal("#000000", Colors.ColorToHex(theme.Colors[ThemeBackgrounds.UserMessageBg]));

        ThemeRuntime.SetTerminalColors(new TerminalColorsSnapshot(
            Foreground: new RgbColor(200, 210, 220),
            Background: new RgbColor(10, 20, 30)));
        Assert.Equal("#c8d2dc", Colors.ColorToHex(theme.Colors[ThemeColors.Text]));
        Assert.Equal("#0a141e", Colors.ColorToHex(theme.Colors[ThemeBackgrounds.UserMessageBg]));
    }

    [Fact]
    public void Colors_AreCachedUntilTheTerminalColorsChange()
    {
        var theme = LoadTheme("dark", json => ColorsOf(json)[ThemeColors.Text] = "");
        var first = theme.Colors;
        Assert.Same(first, theme.Colors);

        // SetTerminalColors installs a fresh snapshot, which is the cache key.
        ThemeRuntime.SetTerminalColors(new TerminalColorsSnapshot(Foreground: new RgbColor(1, 2, 3)));
        Assert.NotSame(first, theme.Colors);
        Assert.Equal("#010203", Colors.ColorToHex(theme.Colors[ThemeColors.Text]));
    }

    [Fact]
    public void Colors_ReportsEveryToken()
    {
        var theme = LoadTheme("dark");
        Assert.Equal(ThemeTokens.All.Count, theme.Colors.Count);
    }

    [Fact]
    public void DimTokens_RenderFaintAndMixTheirColorTowardTheBackground()
    {
        // Only the system theme sets Dim, so the fixture is built directly. The constructor fills the
        // optional tokens from muted/text/thinkingXhigh/selectedBg, so those have to be present.
        var theme = new Theme(
            new Dictionary<string, ThemeJsonColorValue>(StringComparer.Ordinal)
            {
                [ThemeColors.Text] = "#ffffff",
                [ThemeColors.Muted] = "#cccccc",
                [ThemeColors.ThinkingXhigh] = "#dddddd",
            },
            new Dictionary<string, ThemeJsonColorValue>(StringComparer.Ordinal)
            {
                [ThemeBackgrounds.SelectedBg] = "#111111",
            },
            TerminalColorMode.Truecolor,
            new ThemeOptions
            {
                Name = "dim-test",
                Appearance = ThemeAppearance.Dark,
                Dim = [ThemeColors.Muted],
            });

        // The escape sequence opens SGR 2, which \x1b[22m closes.
        Assert.Equal("\x1b[38;2;204;204;204m\x1b[2m", theme.GetFgAnsi(ThemeColors.Muted));
        Assert.Equal("\x1b[38;2;204;204;204m\x1b[2mx\x1b[22;39m", theme.Fg(ThemeColors.Muted, "x"));
        Assert.Equal("\x1b[38;2;255;255;255m", theme.GetFgAnsi(ThemeColors.Text));

        // The resolved color is mixed 40% toward the background (the guessed #000000 for dark).
        Assert.Equal(
            Colors.ColorToHex(Colors.MixColors(Colors.ParseColor("#cccccc"), Colors.ParseColor("#000000"), 0.4)),
            Colors.ColorToHex(theme.Colors[ThemeColors.Muted]));
    }
}
