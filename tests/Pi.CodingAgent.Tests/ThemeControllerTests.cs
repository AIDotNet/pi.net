// ============================================================================
// Tests for modes/interactive/theme/theme-controller.ts (4f-2b).
//
// Upstream has no test file for this module. The vectors are derived from the source: the theme
// runtime calls are captured through IThemeControllerRuntime and the TUI through FakeTui, so every
// branch (auto-sync gating, fallback on a failed theme load, color merging, timeout handling) is
// observable without touching real theme files or starting a watcher.
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class ThemeControllerTests : SelectorTestFixture
{
    // ---------------------------------------------------------------- fixtures

    private sealed class FakeRuntime : IThemeControllerRuntime
    {
        public string? InitThemeName { get; private set; }

        public bool InitWatcher { get; private set; }

        public bool MarkedPending { get; private set; }

        public TerminalColorsSnapshot? AppliedColors { get; private set; }

        public int ColorsSetCount { get; private set; }

        public string? AppliedScheme { get; private set; }

        public bool InstanceApplied { get; private set; }

        public List<(string Name, bool Watcher)> ThemeSets { get; } = [];

        public List<(string? Setting, string TerminalTheme)> ResolveCalls { get; } = [];

        /// <summary>The appearance the fake terminal reports; the applied scheme wins once set.</summary>
        public string DefaultTerminalTheme { get; set; } = "dark";

        /// <summary>What <c>resolveThemeSetting</c> returns; null means "unresolvable".</summary>
        public string? ResolvedName { get; set; } = "dark";

        /// <summary>What <c>parseAutoThemeSetting</c> returns; non-null means "this is a theme pair".</summary>
        public (string LightTheme, string DarkTheme)? AutoPair { get; set; }

        public HashSet<string> FailingThemes { get; } = new(StringComparer.Ordinal);

        public void InitTheme(string? themeName, bool enableWatcher)
        {
            InitThemeName = themeName;
            InitWatcher = enableWatcher;
        }

        public SetThemeResult SetTheme(string name, bool enableWatcher)
        {
            ThemeSets.Add((name, enableWatcher));
            return FailingThemes.Contains(name)
                ? new SetThemeResult(false, "boom")
                : new SetThemeResult(true);
        }

        public void SetThemeInstance(Theme theme) => InstanceApplied = true;

        public void SetTerminalColors(TerminalColorsSnapshot colors)
        {
            AppliedColors = colors;
            ColorsSetCount += 1;
        }

        public void SetTerminalColorScheme(string? scheme) => AppliedScheme = scheme;

        public void MarkTerminalColorsPending() => MarkedPending = true;

        public (string LightTheme, string DarkTheme)? ParseAutoThemeSetting(string? themeSetting) => AutoPair;

        public string? ResolveThemeSetting(string? themeSetting, string terminalTheme)
        {
            ResolveCalls.Add((themeSetting, terminalTheme));
            return ResolvedName;
        }

        public string GetTerminalTheme() => AppliedScheme ?? DefaultTerminalTheme;
    }

    private sealed class Harness
    {
        public FakeTui Tui { get; } = new();

        public FakeRuntime Runtime { get; } = new();

        public List<string> Errors { get; } = [];

        public int ChangedCount { get; private set; }

        public InteractiveThemeController Controller { get; }

        public Harness(string? settingsTheme = "dark", string? initialThemeSetting = null)
        {
            var settings = SettingsManager.InMemory(new Settings { Theme = settingsTheme });
            Controller = new InteractiveThemeController(
                Tui,
                new InteractiveThemeControllerOptions
                {
                    GetSettingsManager = () => settings,
                    ShowError = Errors.Add,
                    OnChanged = () => ChangedCount += 1,
                    InitialThemeSetting = initialThemeSetting,
                },
                Runtime);
        }
    }

    // ---------------------------------------------------------------- construction

    [Fact]
    public void ConstructorMarksColorsPendingAndInstallsTheResolvedTheme()
    {
        var harness = new Harness();

        Assert.True(harness.Runtime.MarkedPending);
        Assert.Equal("dark", harness.Runtime.InitThemeName);
        Assert.True(harness.Runtime.InitWatcher);
        Assert.NotNull(harness.Tui.ColorSchemeListener);
    }

    [Fact]
    public void ConstructorFallsBackToTheSystemThemeWhenNothingResolves()
    {
        var harness = new Harness();
        harness.Runtime.ResolvedName = null;

        var fresh = new InteractiveThemeController(
            new FakeTui(),
            new InteractiveThemeControllerOptions
            {
                GetSettingsManager = () => SettingsManager.InMemory(),
                ShowError = _ => { },
                OnChanged = () => { },
            },
            harness.Runtime);

        Assert.NotNull(fresh);
        Assert.Equal("system", harness.Runtime.InitThemeName);
    }

    // ---------------------------------------------------------------- applying settings

    [Fact]
    public async Task ApplyFromSettingsInstallsTheThemeAndRecordsTheTerminalColors()
    {
        var harness = new Harness();
        var foreground = new RgbColor(1, 2, 3);
        harness.Tui.QueryTerminalColorsHandler = _ =>
            Task.FromResult(new TerminalColorsSnapshot(foreground));

        harness.Controller.ApplyFromSettings();
        await harness.Controller.WaitForTerminalColorsAsync();

        Assert.Equal([("dark", true)], harness.Runtime.ThemeSets);
        Assert.Equal(foreground, harness.Runtime.AppliedColors?.Foreground);
        Assert.Equal(1, harness.ChangedCount);
    }

    [Fact]
    public void SystemThemeEnablesColorSchemeNotifications()
    {
        var harness = new Harness();
        harness.Runtime.ResolvedName = "system";

        harness.Controller.ApplyFromSettings();

        Assert.True(harness.Tui.ColorSchemeNotifications);
    }

    [Fact]
    public void AThemePairEnablesColorSchemeNotifications()
    {
        var harness = new Harness(settingsTheme: "auto");
        harness.Runtime.AutoPair = ("light", "dark");

        harness.Controller.ApplyFromSettings();

        Assert.True(harness.Tui.ColorSchemeNotifications);
    }

    [Fact]
    public void APlainThemeLeavesColorSchemeNotificationsUntouched()
    {
        var harness = new Harness();

        harness.Controller.ApplyFromSettings();

        // SetAutoSync returns early when the flag is already false, so the TUI is never told.
        Assert.Null(harness.Tui.ColorSchemeNotifications);
    }

    // ---------------------------------------------------------------- setting the theme

    [Fact]
    public void SetThemeNameUpdatesTheSelectionOnSuccess()
    {
        var harness = new Harness();

        var result = harness.Controller.SetThemeName("light");

        Assert.True(result.Success);
        Assert.Equal("light", harness.Controller.GetThemeSelection());
    }

    [Fact]
    public void SetThemeNameReportsAFailedLoadAndFallsBackToTheSystemTheme()
    {
        var harness = new Harness(settingsTheme: null);
        harness.Runtime.FailingThemes.Add("bogus");

        var result = harness.Controller.SetThemeName("bogus", showError: true);

        Assert.False(result.Success);
        var error = Assert.Single(harness.Errors);
        Assert.Contains("Failed to load theme \"bogus\"", error);
        Assert.Contains("boom", error);
        Assert.Contains("Fell back to the system theme.", error);
        // The selection falls through the setting to the active theme, which is now the fallback.
        Assert.Equal("system", harness.Controller.GetThemeSelection());
    }

    [Fact]
    public void SetThemeNameStaysSilentWithoutShowError()
    {
        var harness = new Harness();
        harness.Runtime.FailingThemes.Add("bogus");

        harness.Controller.SetThemeName("bogus");

        Assert.Empty(harness.Errors);
    }

    [Fact]
    public void SetThemeNameDisablesAutoSyncForAPlainTheme()
    {
        var harness = new Harness();
        harness.Runtime.ResolvedName = "system";
        harness.Controller.ApplyFromSettings();
        Assert.True(harness.Tui.ColorSchemeNotifications);

        harness.Controller.SetThemeName("dark");

        Assert.False(harness.Tui.ColorSchemeNotifications);
    }

    [Fact]
    public void SetThemeSettingAppliesThroughTheSettingPath()
    {
        var harness = new Harness();
        harness.Runtime.ResolvedName = "light";

        harness.Controller.SetThemeSetting("light");

        Assert.Contains(("light", true), harness.Runtime.ThemeSets);
        Assert.Contains(("light", "dark"), harness.Runtime.ResolveCalls);
    }

    [Fact]
    public void SetThemeInstanceMarksTheActiveThemeInMemory()
    {
        // No theme in settings, so the selection falls through to the active theme name.
        var harness = new Harness(settingsTheme: null);
        harness.Runtime.ResolvedName = "system";
        harness.Controller.ApplyFromSettings();

        var result = harness.Controller.SetThemeInstance(ThemeRuntime.Current);

        Assert.True(result.Success);
        Assert.True(harness.Runtime.InstanceApplied);
        Assert.False(harness.Tui.ColorSchemeNotifications);
        // "<in-memory>" wins over the setting, so the selection reports it.
        Assert.Equal("<in-memory>", harness.Controller.GetThemeSelection());
    }

    [Fact]
    public void ThemeSelectionPrefersTheExplicitSettingOverTheSettingsManager()
    {
        var harness = new Harness(settingsTheme: "dark", initialThemeSetting: "light");

        Assert.Equal("light", harness.Controller.GetThemeSelection());
    }

    // ---------------------------------------------------------------- preview

    [Fact]
    public void PreviewAppliesTheThemeAndReRenders()
    {
        var harness = new Harness();
        harness.Runtime.ResolvedName = "light";
        var invalidations = harness.Tui.Invalidations;

        harness.Controller.Preview("light");

        Assert.Contains(("light", true), harness.Runtime.ThemeSets);
        Assert.Equal(invalidations + 1, harness.Tui.Invalidations);
        Assert.Equal(1, harness.Tui.RenderRequests);
    }

    [Fact]
    public void PreviewFallsBackToTheActiveThemeWhenNothingResolves()
    {
        var harness = new Harness();
        harness.Runtime.ResolvedName = null;

        harness.Controller.Preview("nonsense");

        // The constructor resolved the active theme to "dark" before the name stopped resolving.
        Assert.Contains(("dark", true), harness.Runtime.ThemeSets);
    }

    // ---------------------------------------------------------------- terminal colors

    [Fact]
    public async Task QueryUsesTheHundredMillisecondTimeoutAndAcceptsLateReplies()
    {
        var harness = new Harness();

        harness.Controller.ApplyFromSettings();
        await harness.Controller.WaitForTerminalColorsAsync();

        Assert.Equal(100, harness.Tui.LastColorQuery?.TimeoutMs);
        Assert.NotNull(harness.Tui.LastColorQuery?.OnLateReply);
    }

    [Fact]
    public async Task AFailedQueryAppliesNoColors()
    {
        var harness = new Harness();
        harness.Tui.QueryTerminalColorsHandler = _ => throw new InvalidOperationException("no tty");

        harness.Controller.ApplyFromSettings();
        await harness.Controller.WaitForTerminalColorsAsync();

        Assert.NotNull(harness.Runtime.AppliedColors);
        Assert.Null(harness.Runtime.AppliedColors!.Foreground);
        Assert.Null(harness.Runtime.AppliedColors.Background);
        Assert.Null(harness.Runtime.AppliedColors.Palette);
    }

    [Fact]
    public async Task ARejectedQueryAppliesNoColors()
    {
        var harness = new Harness();
        harness.Tui.QueryTerminalColorsHandler = _ => Task.FromException<TerminalColorsSnapshot>(
            new InvalidOperationException("query failed"));

        harness.Controller.ApplyFromSettings();
        await harness.Controller.WaitForTerminalColorsAsync();

        Assert.Null(harness.Runtime.AppliedColors!.Foreground);
    }

    [Fact]
    public async Task RepeatedIdenticalColorsDoNotReapply()
    {
        var harness = new Harness();
        harness.Tui.QueryTerminalColorsHandler = _ =>
            Task.FromResult(new TerminalColorsSnapshot(new RgbColor(1, 2, 3)));

        harness.Controller.ApplyFromSettings();
        await harness.Controller.WaitForTerminalColorsAsync();
        harness.Controller.ApplyFromSettings();
        await harness.Controller.WaitForTerminalColorsAsync();

        // Re-rendering rebuilds every component, so an unchanged report must not apply twice.
        Assert.Equal(1, harness.Runtime.ColorsSetCount);
    }

    [Fact]
    public async Task APaletteWithEqualContentCountsAsUnchanged()
    {
        var harness = new Harness();
        harness.Tui.QueryTerminalColorsHandler = _ =>
            Task.FromResult(new TerminalColorsSnapshot(
                null,
                null,
                [new RgbColor(1, 2, 3), new RgbColor(4, 5, 6)]));

        harness.Controller.ApplyFromSettings();
        await harness.Controller.WaitForTerminalColorsAsync();
        // A fresh list instance with the same content: TS compares elements, not identity, once the
        // palette arrays differ.
        harness.Controller.ApplyFromSettings();
        await harness.Controller.WaitForTerminalColorsAsync();

        Assert.Equal(1, harness.Runtime.ColorsSetCount);
    }

    [Fact]
    public async Task AMissingFieldInTheReportKeepsThePreviousValue()
    {
        var harness = new Harness();
        var foreground = new RgbColor(9, 9, 9);
        harness.Tui.QueryTerminalColorsHandler = _ =>
            Task.FromResult(new TerminalColorsSnapshot(foreground));
        harness.Controller.ApplyFromSettings();
        await harness.Controller.WaitForTerminalColorsAsync();

        // A later report without a foreground keeps the one already known.
        harness.Tui.QueryTerminalColorsHandler = _ => Task.FromResult(new TerminalColorsSnapshot());
        harness.Controller.ApplyFromSettings();
        await harness.Controller.WaitForTerminalColorsAsync();

        Assert.Equal(foreground, harness.Runtime.AppliedColors?.Foreground);
    }

    // ---------------------------------------------------------------- terminal appearance

    [Fact]
    public void AColorSchemeChangeReappliesWhenTheThemeFlips()
    {
        var harness = new Harness();
        harness.Runtime.ResolvedName = "system";
        harness.Controller.ApplyFromSettings();
        var setsBefore = harness.Runtime.ThemeSets.Count;

        harness.Tui.ColorSchemeListener!(TerminalColorScheme.Light);

        Assert.Equal("light", harness.Runtime.AppliedScheme);
        // GetTerminalTheme now reports "light", so the system theme is regenerated.
        Assert.True(harness.Runtime.ThemeSets.Count > setsBefore);
    }

    [Fact]
    public void AColorSchemeChangeIsIgnoredWithoutAutoSync()
    {
        var harness = new Harness();

        harness.Tui.ColorSchemeListener!(TerminalColorScheme.Light);

        Assert.Null(harness.Runtime.AppliedScheme);
    }

    // ---------------------------------------------------------------- lifecycle

    [Fact]
    public void DisableAutoSyncTurnsNotificationsOff()
    {
        var harness = new Harness();
        harness.Runtime.ResolvedName = "system";
        harness.Controller.ApplyFromSettings();
        Assert.True(harness.Tui.ColorSchemeNotifications);

        harness.Controller.DisableAutoSync();

        Assert.False(harness.Tui.ColorSchemeNotifications);
    }

    [Fact]
    public void RebindTuiReinstallsTheListenerAndTheNotificationFlag()
    {
        var harness = new Harness();
        harness.Runtime.ResolvedName = "system";
        harness.Controller.ApplyFromSettings();
        harness.Tui.ClearColorSchemeListener();

        harness.Controller.RebindTui();

        Assert.NotNull(harness.Tui.ColorSchemeListener);
        Assert.True(harness.Tui.ColorSchemeNotifications);
    }

    [Fact]
    public void DisposeUnsubscribesTheColorSchemeListener()
    {
        var harness = new Harness();
        Assert.NotNull(harness.Tui.ColorSchemeListener);

        harness.Controller.Dispose();

        Assert.Null(harness.Tui.ColorSchemeListener);
    }

    [Fact]
    public void GetTerminalThemeDelegatesToTheRuntime()
    {
        var harness = new Harness();
        harness.Runtime.DefaultTerminalTheme = "light";

        Assert.Equal("light", harness.Controller.GetTerminalTheme());
    }
}
