// ============================================================================
// Tests for the module-level half of modes/interactive/theme/theme.ts
// ============================================================================
//
// Transcribed from packages/coding-agent/test/{theme-detection,theme-export,scrollbar-theme,
// system-theme}.test.ts. The TS suite isolates itself with PI_CODING_AGENT_DIR, which is what the
// name-based loader reads (both there and here), so the port uses the same lever — the ThemeSeams
// record only replaces the colour-mode probe and the file watcher, which TS stubs differently.

using System.Text.Json.Nodes;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class ThemeRuntimeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-theme-runtime-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _agentDir;
    private readonly string _customThemesDir;
    private readonly string? _previousAgentDir;

    public ThemeRuntimeTests()
    {
        _agentDir = Path.Combine(_root, "agent");
        _customThemesDir = Path.Combine(_agentDir, "themes");
        Directory.CreateDirectory(_customThemesDir);

        _previousAgentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _agentDir);

        ThemeRuntime.ResetCachesForTests();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _previousAgentDir);
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
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static JsonObject DarkThemeJson() => (JsonObject)JsonNode.Parse(
        File.ReadAllText(Path.Combine(Config.GetThemesDir(), "dark.json")))!;

    /// <summary>Write a copy of dark.json into the custom themes directory under a new name.</summary>
    private string WriteCustomTheme(string name, Action<JsonObject>? edit = null)
    {
        var json = DarkThemeJson();
        json["name"] = name;
        edit?.Invoke(json);
        var path = Path.Combine(_customThemesDir, $"{name}.json");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    private static ThemeSeams Seams(
        TerminalColorMode mode = TerminalColorMode.Truecolor,
        Func<string, Action<string, string?>, Action, IDisposable?>? watch = null) =>
        new() { ColorMode = mode, Watch = watch };

    private static void AssertExportColors(
        (string? PageBg, string? CardBg, string? InfoBg) actual,
        string? pageBg,
        string? cardBg,
        string? infoBg)
    {
        Assert.Equal(pageBg, actual.PageBg);
        Assert.Equal(cardBg, actual.CardBg);
        Assert.Equal(infoBg, actual.InfoBg);
    }

    private static RgbColor Rgb(string hex) => Colors.ColorToRgb(Colors.ParseColor(hex));

    private static TerminalColorsSnapshot Dracula => new(
        Foreground: Rgb("#f8f8f2"),
        Background: Rgb("#282a36"),
        Palette:
        [
            Rgb("#21222c"), Rgb("#ff5555"), Rgb("#50fa7b"), Rgb("#f1fa8c"),
            Rgb("#bd93f9"), Rgb("#ff79c6"), Rgb("#8be9fd"), Rgb("#f8f8f2"),
            Rgb("#6272a4"), Rgb("#ff6e6e"), Rgb("#69ff94"), Rgb("#ffffa5"),
            Rgb("#d6acff"), Rgb("#ff92df"), Rgb("#a4ffff"), Rgb("#ffffff"),
        ]);

    private sealed class ActionDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    // ---------------------------------------------------------------------------------------------
    // detectColorFgBgTheme (theme-detection.test.ts)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void DetectColorFgBgTheme_ClassifiesTheLastFieldByPaletteIndexLikeVim()
    {
        Assert.Equal(ThemeAppearance.Dark, ThemeRuntime.DetectColorFgBgTheme(Env(("COLORFGBG", "15;0"))));
        Assert.Equal(ThemeAppearance.Light, ThemeRuntime.DetectColorFgBgTheme(Env(("COLORFGBG", "0;7;15"))));
        // Solarized Dark's background is bright black.
        Assert.Equal(ThemeAppearance.Dark, ThemeRuntime.DetectColorFgBgTheme(Env(("COLORFGBG", "12;8"))));
        // rxvt writes "default" when the background is not a palette color.
        Assert.Null(ThemeRuntime.DetectColorFgBgTheme(Env(("COLORFGBG", "15;default"))));
        Assert.Null(ThemeRuntime.DetectColorFgBgTheme(Env()));
    }

    [Theory]
    [InlineData("0", ThemeAppearance.Dark)]
    [InlineData("6", ThemeAppearance.Dark)]
    [InlineData("7", ThemeAppearance.Light)]
    [InlineData("8", ThemeAppearance.Dark)]
    [InlineData("9", ThemeAppearance.Light)]
    [InlineData("15", ThemeAppearance.Light)]
    public void DetectColorFgBgTheme_ClassifiesEveryIndexInRange(string background, string expected) =>
        Assert.Equal(expected, ThemeRuntime.DetectColorFgBgTheme(Env(("COLORFGBG", $"7;{background}"))));

    [Theory]
    [InlineData("7;16")]
    [InlineData("7;999")]
    [InlineData("7;")]
    [InlineData("7;abc")]
    [InlineData("7;0x8")]
    public void DetectColorFgBgTheme_RejectsAnUnusableIndex(string value) =>
        Assert.Null(ThemeRuntime.DetectColorFgBgTheme(Env(("COLORFGBG", value))));

    private static Func<string, string?> Env(params (string Name, string Value)[] entries)
    {
        var map = entries.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    // ---------------------------------------------------------------------------------------------
    // detectTerminalTheme
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void DetectTerminalTheme_PrefersTheBackgroundThenTheReportedSchemeThenColorFgBgThenDark()
    {
        var env = Env(("COLORFGBG", "0;15"));
        Assert.Equal(
            ThemeAppearance.Dark,
            ThemeRuntime.DetectTerminalTheme(new TerminalColorsSnapshot(Background: new RgbColor(8, 8, 8)), "light", env));
        Assert.Equal(ThemeAppearance.Dark, ThemeRuntime.DetectTerminalTheme(new TerminalColorsSnapshot(), "dark", env));
        Assert.Equal(ThemeAppearance.Light, ThemeRuntime.DetectTerminalTheme(new TerminalColorsSnapshot(), null, env));
        Assert.Equal(ThemeAppearance.Dark, ThemeRuntime.DetectTerminalTheme(new TerminalColorsSnapshot(), null, Env()));
    }

    [Fact]
    public void DetectTerminalTheme_FollowsTheForegroundWhenTextIsReadableThatWay()
    {
        var background = new RgbColor(118, 118, 118);
        Assert.Equal(
            ThemeAppearance.Light,
            ThemeRuntime.DetectTerminalTheme(new TerminalColorsSnapshot(Background: background)));
        Assert.Equal(
            ThemeAppearance.Dark,
            ThemeRuntime.DetectTerminalTheme(
                new TerminalColorsSnapshot(Background: background, Foreground: new RgbColor(255, 255, 255))));
        // White text cannot reach 4.5:1 on mid-gray.
        Assert.Equal(
            ThemeAppearance.Light,
            ThemeRuntime.DetectTerminalTheme(new TerminalColorsSnapshot(
                Background: new RgbColor(128, 128, 128),
                Foreground: new RgbColor(255, 255, 255))));
    }

    [Fact]
    public void GetTerminalTheme_ReadsTheRecordedState()
    {
        ThemeRuntime.SetTerminalColors(new TerminalColorsSnapshot(Background: Rgb("#fdf6e3")));
        Assert.Equal(ThemeAppearance.Light, ThemeRuntime.GetTerminalTheme());

        ThemeRuntime.SetTerminalColors(new TerminalColorsSnapshot());
        ThemeRuntime.SetTerminalColorScheme(ThemeAppearance.Light);
        Assert.Equal(ThemeAppearance.Light, ThemeRuntime.GetTerminalTheme());
    }

    // ---------------------------------------------------------------------------------------------
    // Theme setting helpers
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("light/dark", "light", "dark")]
    [InlineData("  light  /  dark  ", "light", "dark")]
    [InlineData("custom/other", "custom", "other")]
    public void ParseAutoThemeSetting_ParsesLightSlashDark(string setting, string light, string dark)
    {
        var parsed = ThemeRuntime.ParseAutoThemeSetting(setting);
        Assert.NotNull(parsed);
        Assert.Equal(light, parsed!.Value.LightTheme);
        Assert.Equal(dark, parsed.Value.DarkTheme);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dark")]
    [InlineData("/dark")]
    [InlineData("light/")]
    [InlineData("light/dark/extra")]
    public void ParseAutoThemeSetting_RejectsAnythingElse(string? setting) =>
        Assert.Null(ThemeRuntime.ParseAutoThemeSetting(setting));

    [Fact]
    public void ResolveThemeSetting_ResolvesAutoSettingsAgainstTheTerminalTheme()
    {
        Assert.Equal("dark", ThemeRuntime.ResolveThemeSetting("dark", ThemeAppearance.Light));
        Assert.Equal("light", ThemeRuntime.ResolveThemeSetting("light/dark", ThemeAppearance.Light));
        Assert.Equal("dark", ThemeRuntime.ResolveThemeSetting("light/dark", ThemeAppearance.Dark));
        Assert.Null(ThemeRuntime.ResolveThemeSetting("light/dark/extra", ThemeAppearance.Dark));
    }

    // ---------------------------------------------------------------------------------------------
    // Available themes
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void GetAvailableThemes_ListsTheSystemThemeFirst()
    {
        var themes = ThemeRuntime.GetAvailableThemes();
        Assert.Equal(SystemTheme.SystemThemeName, themes[0]);
        Assert.Contains("dark", themes);
        Assert.Contains("light", themes);
    }

    [Fact]
    public void GetAvailableThemesWithPaths_IncludesCustomThemesSortedAfterTheSystemTheme()
    {
        var aaa = WriteCustomTheme("aaa-custom");
        var zzz = WriteCustomTheme("zzz-custom");

        var themes = ThemeRuntime.GetAvailableThemesWithPaths(Seams());
        Assert.Equal(
            [SystemTheme.SystemThemeName, "aaa-custom", "dark", "light", "zzz-custom"],
            themes.Select(info => info.Name));

        var system = themes[0];
        Assert.Null(system.Path);
        Assert.Equal(aaa, themes.Single(info => info.Name == "aaa-custom").Path);
        Assert.Equal(zzz, themes.Single(info => info.Name == "zzz-custom").Path);
    }

    [Fact]
    public void GetAvailableThemesWithPaths_SkipsUnreadableCustomThemes()
    {
        WriteCustomTheme("good-custom");
        File.WriteAllText(Path.Combine(_customThemesDir, "broken-custom.json"), "{ not json");
        File.WriteAllText(Path.Combine(_customThemesDir, "ignored.txt"), "{}");

        var names = ThemeRuntime.GetAvailableThemesWithPaths(Seams()).Select(info => info.Name).ToList();
        Assert.Contains("good-custom", names);
        Assert.DoesNotContain("broken-custom", names);
        Assert.DoesNotContain("ignored", names);
    }

    [Fact]
    public void GetThemeByName_ReturnsNullForAnUnknownTheme() =>
        Assert.Null(ThemeRuntime.GetThemeByName("nope", Seams()));

    [Fact]
    public void GetThemeByName_LoadsABuiltinTheme()
    {
        var theme = ThemeRuntime.GetThemeByName("dark", Seams());
        Assert.NotNull(theme);
        Assert.Equal("dark", theme!.Name);
        Assert.Equal(ThemeAppearance.Dark, theme.Appearance);
    }

    // ---------------------------------------------------------------------------------------------
    // Colour mode (theme-detection.test.ts)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void GetThemeByName_UsesTheReportedColourMode()
    {
        var ansi256 = ThemeRuntime.GetThemeByName("dark", Seams(TerminalColorMode.Color256));
        Assert.NotNull(ansi256);
        Assert.Equal(TerminalColorMode.Color256, ansi256!.GetColorMode());
        Assert.Matches(@"^\x1b\[38;5;[0-9]+m$", ansi256.GetFgAnsi(ThemeColors.Accent));

        var truecolor = ThemeRuntime.GetThemeByName("dark", Seams(TerminalColorMode.Truecolor));
        Assert.NotNull(truecolor);
        Assert.Equal(TerminalColorMode.Truecolor, truecolor!.GetColorMode());
        Assert.Matches(@"^\x1b\[38;2;[0-9]+;[0-9]+;[0-9]+m$", truecolor.GetFgAnsi(ThemeColors.Accent));
    }

    // ---------------------------------------------------------------------------------------------
    // loadThemeFromPath: optional-token fallbacks (scrollbar-theme.test.ts)
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ThemeColors.ScrollbarTrack, ThemeColors.Muted)]
    [InlineData(ThemeColors.ScrollbarThumb, ThemeColors.Text)]
    public void LoadThemeFromPath_FallsBackForOmittedScrollbarColors(string token, string fallback)
    {
        ThemeRuntime.SetThemeJsonValidator(ThemeJson.ValidateThemeJson);
        var json = DarkThemeJson();
        ((JsonObject)json["colors"]!).Remove(token);
        var path = Path.Combine(_root, $"missing-{token}.json");
        File.WriteAllText(path, json.ToJsonString());

        var theme = ThemeRuntime.LoadThemeFromPath(path, TerminalColorMode.Truecolor);
        Assert.Equal(theme.GetFgAnsi(fallback), theme.GetFgAnsi(token));
    }

    [Fact]
    public void LoadThemeFromPath_UsesExplicitlyConfiguredScrollbarColors()
    {
        ThemeRuntime.SetThemeJsonValidator(ThemeJson.ValidateThemeJson);
        var json = DarkThemeJson();
        ((JsonObject)json["colors"]!)[ThemeColors.ScrollbarTrack] = "#654321";
        ((JsonObject)json["colors"]!)[ThemeColors.ScrollbarThumb] = "#123456";
        var path = Path.Combine(_root, "custom-scrollbar.json");
        File.WriteAllText(path, json.ToJsonString());

        var theme = ThemeRuntime.LoadThemeFromPath(path, TerminalColorMode.Truecolor);
        Assert.Equal("\x1b[38;2;101;67;33m", theme.GetFgAnsi(ThemeColors.ScrollbarTrack));
        Assert.Equal("\x1b[38;2;18;52;86m", theme.GetFgAnsi(ThemeColors.ScrollbarThumb));
    }

    [Fact]
    public void LoadThemeFromPath_FallsBackToExistingSelectionAndTextColorsForSearchHighlights()
    {
        ThemeRuntime.SetThemeJsonValidator(ThemeJson.ValidateThemeJson);
        var json = DarkThemeJson();
        ((JsonObject)json["colors"]!).Remove(ThemeBackgrounds.SearchMatchBg);
        ((JsonObject)json["colors"]!).Remove(ThemeColors.SearchMatchText);
        var path = Path.Combine(_root, "legacy-search.json");
        File.WriteAllText(path, json.ToJsonString());

        var theme = ThemeRuntime.LoadThemeFromPath(path, TerminalColorMode.Truecolor);
        Assert.Equal(theme.GetBgAnsi(ThemeBackgrounds.SelectedBg), theme.GetBgAnsi(ThemeBackgrounds.SearchMatchBg));
        Assert.Equal(theme.GetFgAnsi(ThemeColors.Text), theme.GetFgAnsi(ThemeColors.SearchMatchText));
    }

    [Fact]
    public void LoadThemeFromPath_UsesExplicitlyConfiguredSearchHighlightColors()
    {
        ThemeRuntime.SetThemeJsonValidator(ThemeJson.ValidateThemeJson);
        var json = DarkThemeJson();
        ((JsonObject)json["colors"]!)[ThemeBackgrounds.SearchMatchBg] = "#112233";
        ((JsonObject)json["colors"]!)[ThemeColors.SearchMatchText] = "#223344";
        var path = Path.Combine(_root, "custom-search.json");
        File.WriteAllText(path, json.ToJsonString());

        var theme = ThemeRuntime.LoadThemeFromPath(path, TerminalColorMode.Truecolor);
        Assert.Equal("\x1b[48;2;17;34;51m", theme.GetBgAnsi(ThemeBackgrounds.SearchMatchBg));
        Assert.Equal("\x1b[38;2;34;51;68m", theme.GetFgAnsi(ThemeColors.SearchMatchText));
    }

    [Fact]
    public void LoadThemeFromPath_WithoutAValidatorOnlyRequiresAColorsMap()
    {
        // Built-in themes skip validation; only interactive-mode installs the validator.
        var path = Path.Combine(_root, "no-colors.json");
        File.WriteAllText(path, "{\"name\": \"no-colors\"}");
        var error = Assert.Throws<InvalidOperationException>(
            () => ThemeRuntime.LoadThemeFromPath(path, TerminalColorMode.Truecolor));
        Assert.Contains("expected an object with a \"colors\" map", error.Message);
    }

    [Fact]
    public void LoadThemeFromPath_ReportsAParseFailureWithThePath()
    {
        var path = Path.Combine(_root, "broken.json");
        File.WriteAllText(path, "{ not json");
        var error = Assert.Throws<InvalidOperationException>(
            () => ThemeRuntime.LoadThemeFromPath(path, TerminalColorMode.Truecolor));
        Assert.StartsWith($"Failed to parse theme {path}:", error.Message);
    }

    [Fact]
    public void LoadThemeFromPath_RejectsCircularVariableReferences()
    {
        var json = DarkThemeJson();
        var vars = (JsonObject)json["vars"]!;
        vars["a"] = "b";
        vars["b"] = "a";
        ((JsonObject)json["colors"]!)[ThemeColors.Text] = "a";
        var path = Path.Combine(_root, "circular.json");
        File.WriteAllText(path, json.ToJsonString());

        var error = Assert.Throws<InvalidOperationException>(
            () => ThemeRuntime.LoadThemeFromPath(path, TerminalColorMode.Truecolor));
        Assert.Equal("Circular variable reference detected: a", error.Message);
    }

    [Fact]
    public void LoadThemeFromPath_RejectsAMissingVariableReference()
    {
        var json = DarkThemeJson();
        ((JsonObject)json["colors"]!)[ThemeColors.Text] = "nope";
        var path = Path.Combine(_root, "missing-var.json");
        File.WriteAllText(path, json.ToJsonString());

        var error = Assert.Throws<InvalidOperationException>(
            () => ThemeRuntime.LoadThemeFromPath(path, TerminalColorMode.Truecolor));
        Assert.Equal("Variable reference not found: nope", error.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Export colours (theme-export.test.ts)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void GetThemeExportColors_ResolvesExportVariableReferencesWithTheSameSyntaxAsColors()
    {
        WriteCustomTheme("custom-export-vars", json =>
        {
            var vars = (JsonObject)json["vars"]!;
            vars["pageBgVar"] = "#112233";
            vars["pageBgAlias"] = "pageBgVar";
            vars["infoBgVar"] = "#445566";
            vars["cardBgVar"] = "#223344";
            json["export"] = JsonNode.Parse(
                "{\"pageBg\": \"pageBgAlias\", \"cardBg\": \"cardBgVar\", \"infoBg\": \"infoBgVar\"}");
        });

        AssertExportColors(
            ThemeRuntime.GetThemeExportColors("custom-export-vars"),
            "#112233",
            "#223344",
            "#445566");
    }

    [Fact]
    public void GetThemeExportColors_ConvertsOkhslExportColorsToHexBecauseCssDoesNotSupportThem()
    {
        WriteCustomTheme("custom-export-okhsl", json =>
        {
            json["vars"] = JsonNode.Parse("{\"card\": \"okhsl(250 20% 20%)\"}");
            json["export"] = JsonNode.Parse(
                "{\"pageBg\": \"okhsl(250 20% 15%)\", \"cardBg\": \"card\", \"infoBg\": \"oklch(30% 0.05 80)\"}");
        });

        AssertExportColors(
            ThemeRuntime.GetThemeExportColors("custom-export-okhsl"),
            Colors.ColorToHex(Colors.Okhsl(250, 0.2, 0.15)),
            Colors.ColorToHex(Colors.Okhsl(250, 0.2, 0.2)),
            "oklch(30% 0.05 80)");
    }

    [Fact]
    public void GetThemeExportColors_ResolvesRecursiveVarsAndConvertsPaletteIndicesToHex()
    {
        WriteCustomTheme("custom-export-recursive", json =>
        {
            var vars = (JsonObject)json["vars"]!;
            vars["deepPageBg"] = "#abcdef";
            vars["pageBgAlias"] = "deepPageBg";
            vars["cardBgAnsi"] = 24;
            json["export"] = JsonNode.Parse("{\"pageBg\": \"pageBgAlias\", \"cardBg\": \"cardBgAnsi\", \"infoBg\": \"\"}");
        });

        AssertExportColors(
            ThemeRuntime.GetThemeExportColors("custom-export-recursive"),
            "#abcdef",
            "#005f87",
            null);
    }

    [Fact]
    public void GetThemeExportColors_ReturnsAllNullForTheSystemTheme() =>
        AssertExportColors(ThemeRuntime.GetThemeExportColors(SystemTheme.SystemThemeName), null, null, null);

    [Fact]
    public void GetThemeExportColors_ReturnsAllNullWithoutAnExportSection()
    {
        WriteCustomTheme("no-export", json => json.Remove("export"));
        AssertExportColors(ThemeRuntime.GetThemeExportColors("no-export"), null, null, null);
    }

    [Fact]
    public void GetThemeExportColors_ReturnsAllNullForAMissingTheme() =>
        AssertExportColors(ThemeRuntime.GetThemeExportColors("does-not-exist"), null, null, null);

    [Fact]
    public void GetResolvedThemeColors_ReportsEveryTokenAsHex()
    {
        var colors = ThemeRuntime.GetResolvedThemeColors("dark", Seams());
        Assert.Equal(ThemeTokens.All.Count, colors.Count);
        foreach (var (token, hex) in colors)
        {
            Assert.Matches("^#[0-9a-f]{6}$", hex);
            Assert.True(ThemeTokens.IsToken(token));
        }
    }

    [Fact]
    public void IsLightTheme_ReadsTheResolvedAppearance()
    {
        Assert.False(ThemeRuntime.IsLightTheme("dark", Seams()));
        Assert.True(ThemeRuntime.IsLightTheme("light", Seams()));
    }

    // ---------------------------------------------------------------------------------------------
    // The system theme (system-theme.test.ts)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SystemTheme_IsGeneratedFromTheTerminalColors()
    {
        ThemeRuntime.SetTerminalColors(Dracula);
        var theme = ThemeRuntime.GetThemeByName(SystemTheme.SystemThemeName, Seams());
        Assert.NotNull(theme);
        Assert.Equal(SystemTheme.SystemThemeName, theme!.Name);
        Assert.Null(theme.SourcePath);
        Assert.Equal(ThemeAppearance.Dark, theme.Appearance);
        Assert.Equal("\x1b[39m", theme.GetFgAnsi(ThemeColors.Text));
        Assert.StartsWith("\x1b[38;", theme.GetFgAnsi(ThemeColors.Error));
    }

    [Fact]
    public void SystemTheme_RendersFaintTokensWithSgrTwoAndClosesIt()
    {
        ThemeRuntime.SetTerminalColorScheme(ThemeAppearance.Dark);
        var theme = ThemeRuntime.GetThemeByName(SystemTheme.SystemThemeName, Seams());
        Assert.NotNull(theme);
        Assert.Equal("\x1b[39m\x1b[2mx\x1b[22;39m", theme!.Fg(ThemeColors.Muted, "x"));
        Assert.Equal(
            "\x1b[39m\x1b[2mx\x1b[22m\x1b[39m",
            theme.Style("x", new ThemeStyle { FgToken = ThemeColors.Muted }));
    }

    [Fact]
    public void SystemTheme_RendersGrayscaleWhileTheTerminalColorsArePending()
    {
        ThemeRuntime.SetTerminalColors(Dracula);
        var colored = ThemeRuntime.GetThemeByName(SystemTheme.SystemThemeName, Seams());
        Assert.NotNull(colored);

        ThemeRuntime.MarkTerminalColorsPending();
        var grayscale = ThemeRuntime.GetThemeByName(SystemTheme.SystemThemeName, Seams());
        Assert.NotNull(grayscale);

        // Grayscale means no chroma, while the reported frame takes the palette's hues.
        Assert.True(Colors.ColorToOklch(grayscale!.Colors[ThemeColors.Error]).C < 0.005);
        Assert.True(Colors.ColorToOklch(colored!.Colors[ThemeColors.Error]).C > 0.005);
    }

    // ---------------------------------------------------------------------------------------------
    // The global registry
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Current_ThrowsBeforeAThemeIsInstalled()
    {
        Assert.False(ThemeRuntime.HasCurrent);
        Assert.Equal(
            "Theme not initialized. Call initTheme() first.",
            Assert.Throws<InvalidOperationException>(() => ThemeRuntime.Current).Message);
    }

    [Fact]
    public void InitTheme_InstallsTheNamedTheme()
    {
        ThemeRuntime.InitTheme("dark", false, Seams());
        Assert.True(ThemeRuntime.HasCurrent);
        Assert.Equal("dark", ThemeRuntime.CurrentThemeName);
        Assert.Equal("dark", ThemeRuntime.Current.Name);
    }

    [Fact]
    public void InitTheme_DefaultsToTheSystemTheme()
    {
        ThemeRuntime.InitTheme(null, false, Seams());
        Assert.Equal(SystemTheme.SystemThemeName, ThemeRuntime.CurrentThemeName);
    }

    [Fact]
    public void InitTheme_FallsBackToTheSystemThemeWhenTheNameIsUnknown()
    {
        ThemeRuntime.InitTheme("nope", false, Seams());
        Assert.Equal(SystemTheme.SystemThemeName, ThemeRuntime.CurrentThemeName);
        Assert.Equal(SystemTheme.SystemThemeName, ThemeRuntime.Current.Name);
    }

    [Fact]
    public void SetTheme_ReportsFailureAndFallsBackInsteadOfThrowing()
    {
        ThemeRuntime.InitTheme("dark", false, Seams());
        var result = ThemeRuntime.SetTheme("nope", false, Seams());
        Assert.False(result.Success);
        Assert.Equal("Theme not found: nope", result.Error);
        Assert.Equal(SystemTheme.SystemThemeName, ThemeRuntime.CurrentThemeName);
    }

    [Fact]
    public void SetTheme_ReportsSuccessAndNotifiesTheListener()
    {
        ThemeRuntime.InitTheme("dark", false, Seams());
        var changes = 0;
        ThemeRuntime.OnThemeChange(() => changes++);

        var result = ThemeRuntime.SetTheme("light", false, Seams());
        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal("light", ThemeRuntime.CurrentThemeName);
        Assert.Equal(1, changes);

        // A failed switch falls back but does not notify.
        Assert.False(ThemeRuntime.SetTheme("nope", false, Seams()).Success);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SetThemeInstance_InstallsTheInstanceDirectly()
    {
        ThemeRuntime.InitTheme("dark", false, Seams());
        var changes = 0;
        ThemeRuntime.OnThemeChange(() => changes++);

        var instance = ThemeRuntime.LoadThemeFromPath(
            Path.Combine(Config.GetThemesDir(), "light.json"), TerminalColorMode.Truecolor);
        ThemeRuntime.SetThemeInstance(instance);

        Assert.Same(instance, ThemeRuntime.Current);
        Assert.Equal("<in-memory>", ThemeRuntime.CurrentThemeName);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SetRegisteredThemes_ReplacesTheRegisteredSetAndFeedsTheLookup()
    {
        // Written outside the custom themes directory, so the registry is the only way to reach it.
        var json = DarkThemeJson();
        json["name"] = "registered-theme";
        var path = Path.Combine(_root, "registered-theme.json");
        File.WriteAllText(path, json.ToJsonString());
        var theme = ThemeRuntime.LoadThemeFromPath(path, TerminalColorMode.Truecolor);

        ThemeRuntime.SetRegisteredThemes([theme]);
        Assert.Same(theme, ThemeRuntime.GetThemeByName("registered-theme", Seams()));
        Assert.Contains("registered-theme", ThemeRuntime.GetAvailableThemesWithPaths(Seams()).Select(i => i.Name));

        ThemeRuntime.SetRegisteredThemes([]);
        Assert.Null(ThemeRuntime.GetThemeByName("registered-theme", Seams()));
    }

    [Fact]
    public void SetRegisteredThemes_RejectsANameWithASlash()
    {
        var theme = ThemeRuntime.LoadThemeFromPath(
            Path.Combine(Config.GetThemesDir(), "dark.json"), TerminalColorMode.Truecolor);
        // `Theme.Name` comes from the document, so a theme whose name carries a slash cannot register.
        var json = DarkThemeJson();
        json["name"] = "light/dark";
        var path = Path.Combine(_root, "slash-name.json");
        File.WriteAllText(path, json.ToJsonString());
        var slashNamed = ThemeRuntime.LoadThemeFromPath(path, TerminalColorMode.Truecolor);

        Assert.Equal("dark", theme.Name);
        Assert.Equal("light/dark", slashNamed.Name);
        Assert.Throws<InvalidOperationException>(() => ThemeRuntime.SetRegisteredThemes([slashNamed]));
    }

    // ---------------------------------------------------------------------------------------------
    // The theme watcher
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void StartWatcher_WatchesTheCustomThemeFileAndReloadsOnChange()
    {
        WriteCustomTheme("watched", json => ((JsonObject)json["colors"]!)[ThemeColors.Accent] = "#111111");
        Action<string, string?>? listener = null;
        var disposed = 0;
        var seams = Seams(watch: (path, onChange, onError) =>
        {
            Assert.Equal(_customThemesDir, path);
            listener = onChange;
            return new ActionDisposable(() => disposed++);
        });

        ThemeRuntime.InitTheme("watched", true, seams);
        Assert.Equal("watched", ThemeRuntime.CurrentThemeName);
        Assert.Equal("#111111", Colors.ColorToHex(ThemeRuntime.Current.Colors[ThemeColors.Accent]));
        Assert.NotNull(listener);

        var changes = 0;
        ThemeRuntime.OnThemeChange(() => changes++);
        WriteCustomTheme("watched", json => ((JsonObject)json["colors"]!)[ThemeColors.Accent] = "#222222");
        // The listener is (eventType, filename), matching fs.watch.
        listener!("change", "watched.json");

        // The reload is debounced by 100 ms.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && changes == 0)
        {
            Thread.Sleep(20);
        }
        Assert.Equal(1, changes);
        Assert.Equal("#222222", Colors.ColorToHex(ThemeRuntime.Current.Colors[ThemeColors.Accent]));

        ThemeRuntime.StopThemeWatcher();
        Assert.Equal(1, disposed);
    }

    [Fact]
    public void StartWatcher_IgnoresChangesToOtherFiles()
    {
        WriteCustomTheme("watched-other", json => ((JsonObject)json["colors"]!)[ThemeColors.Accent] = "#111111");
        Action<string, string?>? listener = null;
        var seams = Seams(watch: (_, onChange, _) =>
        {
            listener = onChange;
            return new ActionDisposable(() => { });
        });

        ThemeRuntime.InitTheme("watched-other", true, seams);
        Assert.NotNull(listener);
        WriteCustomTheme("watched-other", json => ((JsonObject)json["colors"]!)[ThemeColors.Accent] = "#222222");

        listener!("change", "something-else.json");
        Thread.Sleep(250);
        Assert.Equal("#111111", Colors.ColorToHex(ThemeRuntime.Current.Colors[ThemeColors.Accent]));
    }

    [Fact]
    public void StartWatcher_ReloadsWhenTheWatcherCannotNameTheChangedFile()
    {
        WriteCustomTheme("watched-unnamed", json => ((JsonObject)json["colors"]!)[ThemeColors.Accent] = "#111111");
        Action<string, string?>? listener = null;
        var seams = Seams(watch: (_, onChange, _) =>
        {
            listener = onChange;
            return new ActionDisposable(() => { });
        });

        ThemeRuntime.InitTheme("watched-unnamed", true, seams);
        Assert.NotNull(listener);

        var changes = 0;
        ThemeRuntime.OnThemeChange(() => changes++);
        WriteCustomTheme("watched-unnamed", json => ((JsonObject)json["colors"]!)[ThemeColors.Accent] = "#333333");
        // A null filename means the watcher could not attribute the change; reload anyway.
        listener!("change", null);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && changes == 0)
        {
            Thread.Sleep(20);
        }
        Assert.Equal(1, changes);
        Assert.Equal("#333333", Colors.ColorToHex(ThemeRuntime.Current.Colors[ThemeColors.Accent]));
    }

    [Fact]
    public void StartWatcher_KeepsTheLoadedThemeWhenTheFileDisappears()
    {
        WriteCustomTheme("watched-gone", json => ((JsonObject)json["colors"]!)[ThemeColors.Accent] = "#111111");
        Action<string, string?>? listener = null;
        var seams = Seams(watch: (_, onChange, _) =>
        {
            listener = onChange;
            return new ActionDisposable(() => { });
        });

        ThemeRuntime.InitTheme("watched-gone", true, seams);
        var changes = 0;
        ThemeRuntime.OnThemeChange(() => changes++);

        File.Delete(Path.Combine(_customThemesDir, "watched-gone.json"));
        listener!("change", "watched-gone.json");
        Thread.Sleep(250);

        Assert.Equal(0, changes);
        Assert.Equal("#111111", Colors.ColorToHex(ThemeRuntime.Current.Colors[ThemeColors.Accent]));
    }

    [Fact]
    public void StartWatcher_DoesNotWatchBuiltInOrSystemThemes()
    {
        var watched = 0;
        var seams = Seams(watch: (_, _, _) =>
        {
            watched++;
            return new ActionDisposable(() => { });
        });

        ThemeRuntime.InitTheme("dark", true, seams);
        ThemeRuntime.InitTheme(SystemTheme.SystemThemeName, true, seams);
        ThemeRuntime.InitTheme(null, true, seams);
        Assert.Equal(0, watched);
    }

    [Fact]
    public void StartWatcher_DoesNotWatchWhenTheThemeFallsBackToTheSystemTheme()
    {
        // A name that cannot be loaded falls back silently, and the fallback never gets a watcher.
        var watched = 0;
        var seams = Seams(watch: (_, _, _) =>
        {
            watched++;
            return new ActionDisposable(() => { });
        });

        ThemeRuntime.InitTheme("not-on-disk", true, seams);
        Assert.Equal(SystemTheme.SystemThemeName, ThemeRuntime.CurrentThemeName);
        Assert.Equal(0, watched);
    }

    [Fact]
    public void StopThemeWatcher_IsIdempotent()
    {
        var disposed = 0;
        var seams = Seams(watch: (_, _, _) => new ActionDisposable(() => disposed++));
        WriteCustomTheme("stop-test");
        ThemeRuntime.InitTheme("stop-test", true, seams);

        ThemeRuntime.StopThemeWatcher();
        ThemeRuntime.StopThemeWatcher();
        Assert.Equal(1, disposed);
    }
}
