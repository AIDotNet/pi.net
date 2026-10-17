// ============================================================================
// Theme application controller.
// Port of modes/interactive/theme/theme-controller.ts (253 lines).
//
// The controller keeps the theme setting in sync with the terminal: the theme applies
// immediately, and the terminal's reported colors refine it when they arrive. The system theme
// renders in grayscale until then.
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>Options for <see cref="InteractiveThemeController"/>. TS inline options object.</summary>
public sealed record InteractiveThemeControllerOptions
{
    public required Func<SettingsManager> GetSettingsManager { get; init; }

    public required Action<string> ShowError { get; init; }

    public required Action OnChanged { get; init; }

    public string? InitialThemeSetting { get; init; }
}

/// <summary>
/// The theme-module calls the controller makes. TS calls the <c>theme.ts</c> module functions
/// directly; this seam keeps the controller testable without touching real theme files or
/// starting a file watcher (the same reason <c>ThemeRuntime</c> has <c>ThemeSeams</c>).
/// </summary>
internal interface IThemeControllerRuntime
{
    void InitTheme(string? themeName, bool enableWatcher);

    SetThemeResult SetTheme(string name, bool enableWatcher);

    void SetThemeInstance(Theme theme);

    void SetTerminalColors(TerminalColorsSnapshot colors);

    void SetTerminalColorScheme(string? scheme);

    void MarkTerminalColorsPending();

    (string LightTheme, string DarkTheme)? ParseAutoThemeSetting(string? themeSetting);

    string? ResolveThemeSetting(string? themeSetting, string terminalTheme);

    string GetTerminalTheme();
}

/// <summary>Default <see cref="IThemeControllerRuntime"/>: the real <see cref="ThemeRuntime"/>.</summary>
internal sealed class ThemeControllerRuntime : IThemeControllerRuntime
{
    public void InitTheme(string? themeName, bool enableWatcher) => ThemeRuntime.InitTheme(themeName, enableWatcher);

    public SetThemeResult SetTheme(string name, bool enableWatcher) => ThemeRuntime.SetTheme(name, enableWatcher);

    public void SetThemeInstance(Theme theme) => ThemeRuntime.SetThemeInstance(theme);

    public void SetTerminalColors(TerminalColorsSnapshot colors) => ThemeRuntime.SetTerminalColors(colors);

    public void SetTerminalColorScheme(string? scheme) => ThemeRuntime.SetTerminalColorScheme(scheme);

    public void MarkTerminalColorsPending() => ThemeRuntime.MarkTerminalColorsPending();

    public (string LightTheme, string DarkTheme)? ParseAutoThemeSetting(string? themeSetting) =>
        ThemeRuntime.ParseAutoThemeSetting(themeSetting);

    public string? ResolveThemeSetting(string? themeSetting, string terminalTheme) =>
        ThemeRuntime.ResolveThemeSetting(themeSetting, terminalTheme);

    public string GetTerminalTheme() => ThemeRuntime.GetTerminalTheme();
}

/// <summary>
/// Applies the theme setting and keeps it in sync with the terminal.
/// Port of <c>InteractiveThemeController</c>.
/// </summary>
public sealed class InteractiveThemeController : IDisposable
{
    /// <summary>
    /// How long the system theme stays grayscale before falling back to palette indices. Terminals
    /// answer the trailing DA1 request right after the color replies, so this only matters for
    /// terminals that answer neither. Replies arriving later still apply.
    /// </summary>
    private const int TerminalQueryTimeoutMs = 100;

    private readonly ITui _ui;
    private readonly Func<SettingsManager> _getSettingsManager;
    private readonly Action<string> _showError;
    private readonly Action _onChanged;
    private readonly IThemeControllerRuntime _runtime;

    private string? _currentThemeSetting;

    // Last reported colors; a query that times out keeps them instead of erasing them.
    private TerminalColorsSnapshot? _terminalColors;
    private string? _activeThemeName;
    private bool _autoSyncEnabled;
    private Action? _terminalColorSchemeUnsubscribe;

    // Settles when the latest color query completed or timed out, and its colors applied.
    private Task _terminalColorQuery = Task.CompletedTask;

    public InteractiveThemeController(ITui ui, InteractiveThemeControllerOptions options)
        : this(ui, options, null)
    {
    }

    /// <summary>The body, with the theme runtime injected (test seam).</summary>
    internal InteractiveThemeController(
        ITui ui,
        InteractiveThemeControllerOptions options,
        IThemeControllerRuntime? runtime)
    {
        _ui = ui;
        _runtime = runtime ?? new ThemeControllerRuntime();
        _getSettingsManager = options.GetSettingsManager;
        _showError = options.ShowError;
        _onChanged = options.OnChanged;
        _currentThemeSetting = options.InitialThemeSetting;
        _activeThemeName = ResolveThemeName();
        // The system theme starts in grayscale; color follows once the terminal reports its colors.
        _runtime.MarkTerminalColorsPending();
        _runtime.InitTheme(_activeThemeName, true);
        BindTerminalColorSchemeListener();
    }

    /// <summary>
    /// Query the terminal's colors and pass them to <paramref name="apply"/> when the query completes
    /// or times out, and again if the terminal answers after the timeout. A failed query applies no
    /// colors. Settles after the first apply.
    /// </summary>
    public static async Task RequestTerminalColorsAsync(ITui ui, Action<TerminalColorsSnapshot> apply)
    {
        Task<TerminalColorsSnapshot> query;
        try
        {
            query = ui.QueryTerminalColorsAsync(new TerminalColorQueryOptions
            {
                TimeoutMs = TerminalQueryTimeoutMs,
                OnLateReply = apply,
            });
        }
        catch
        {
            // Treat a failed query like a terminal that does not report colors.
            query = Task.FromResult(new TerminalColorsSnapshot());
        }

        TerminalColorsSnapshot colors;
        try
        {
            colors = await query.ConfigureAwait(false);
        }
        catch
        {
            colors = new TerminalColorsSnapshot();
        }

        apply(colors);
    }

    public void RebindTui()
    {
        _terminalColorSchemeUnsubscribe?.Invoke();
        BindTerminalColorSchemeListener();
        _ui.SetTerminalColorSchemeNotifications(_autoSyncEnabled);
    }

    /// <summary>
    /// Apply the theme setting now and query the terminal's colors, which update the theme when they
    /// arrive. Theme pairs and the system theme follow terminal appearance changes.
    /// </summary>
    public void ApplyFromSettings()
    {
        var themeSetting = GetThemeSetting();
        var themeName = ResolveThemeName();
        SetAutoSync(_runtime.ParseAutoThemeSetting(themeSetting) is not null ||
            themeName == SystemTheme.SystemThemeName);
        ApplyThemeName(themeName, themeSetting is not null);
        QueryTerminalColors();
    }

    /// <summary>
    /// Wait until the latest color query completed or timed out. Content that bakes theme colors into
    /// strings, such as the startup header, should be built after this.
    /// </summary>
    public Task WaitForTerminalColorsAsync() => _terminalColorQuery;

    public string? GetThemeSelection() =>
        _currentThemeSetting ?? _getSettingsManager().GetThemeSetting() ?? _activeThemeName;

    public SetThemeResult SetThemeName(string themeName, bool showError = false)
    {
        SetAutoSync(themeName == SystemTheme.SystemThemeName);
        var result = ApplyThemeName(themeName, showError);
        if (result.Success)
        {
            _currentThemeSetting = themeName;
        }

        return result;
    }

    public void SetThemeSetting(string themeSetting)
    {
        _currentThemeSetting = themeSetting;
        ApplyFromSettings();
    }

    public SetThemeResult SetThemeInstance(Theme themeInstance)
    {
        SetAutoSync(false);
        _runtime.SetThemeInstance(themeInstance);
        _activeThemeName = "<in-memory>";
        NotifyChanged();
        return new SetThemeResult(true);
    }

    public void Preview(string themeSettingOrName)
    {
        var themeName = _runtime.ResolveThemeSetting(themeSettingOrName, _runtime.GetTerminalTheme()) ??
            _activeThemeName;
        if (themeName is null)
        {
            return;
        }

        if (_runtime.SetTheme(themeName, true).Success)
        {
            _ui.Invalidate();
            _ui.RequestRender();
        }
    }

    public void DisableAutoSync() => SetAutoSync(false);

    public void Dispose()
    {
        SetAutoSync(false);
        _terminalColorSchemeUnsubscribe?.Invoke();
        _terminalColorSchemeUnsubscribe = null;
    }

    public string GetTerminalTheme() => _runtime.GetTerminalTheme();

    // ---------------------------------------------------------------- internals

    private string? GetThemeSetting() => _currentThemeSetting ?? _getSettingsManager().GetThemeSetting();

    /// <summary>
    /// The theme for the current setting and terminal appearance. Without a setting, pi uses the
    /// system theme.
    /// </summary>
    private string ResolveThemeName() =>
        _runtime.ResolveThemeSetting(GetThemeSetting(), _runtime.GetTerminalTheme()) ??
        SystemTheme.SystemThemeName;

    private SetThemeResult ApplyThemeName(string themeName, bool showError = false)
    {
        var result = _runtime.SetTheme(themeName, true);
        _activeThemeName = result.Success ? themeName : SystemTheme.SystemThemeName;
        NotifyChanged();
        if (!result.Success && showError)
        {
            _showError($"Failed to load theme \"{themeName}\": {result.Error}\nFell back to the system theme.");
        }

        return result;
    }

    /// <summary>
    /// Query the terminal's colors without waiting for them;
    /// <see cref="WaitForTerminalColorsAsync"/> waits for this query.
    /// </summary>
    private void QueryTerminalColors() =>
        _terminalColorQuery = RequestTerminalColorsAsync(_ui, ApplyTerminalColors);

    /// <summary>
    /// Record reported colors: themes use the default colors for tokens set to "", the system theme
    /// is generated from all of them, and light/dark detection uses them. Re-renders only when they
    /// changed.
    /// </summary>
    private void ApplyTerminalColors(TerminalColorsSnapshot reported)
    {
        var previous = _terminalColors;
        var next = new TerminalColorsSnapshot(
            reported.Foreground ?? previous?.Foreground,
            reported.Background ?? previous?.Background,
            reported.Palette ?? previous?.Palette);
        // Re-rendering rebuilds every component, so skip it when nothing changed (including timeouts).
        if (previous is not null && SameTerminalColors(previous, next))
        {
            return;
        }

        _terminalColors = next;
        _runtime.SetTerminalColors(next);
        ReapplyForTerminal();
        _ui.Invalidate();
        _ui.RequestRender();
    }

    /// <summary>
    /// Re-apply the setting after the terminal's colors or appearance changed: regenerate the system
    /// theme, or switch the theme of a pair. Themes set through extensions or previews are left alone.
    /// </summary>
    private void ReapplyForTerminal()
    {
        if (_activeThemeName == "<in-memory>")
        {
            return;
        }

        var themeName = ResolveThemeName();
        if (themeName == SystemTheme.SystemThemeName || themeName != _activeThemeName)
        {
            ApplyThemeName(themeName);
        }
    }

    private void SetAutoSync(bool enabled)
    {
        if (_autoSyncEnabled == enabled)
        {
            return;
        }

        _autoSyncEnabled = enabled;
        _ui.SetTerminalColorSchemeNotifications(enabled);
    }

    private void BindTerminalColorSchemeListener() =>
        _terminalColorSchemeUnsubscribe = _ui.OnTerminalColorSchemeChange(ApplyTerminalColorSchemeChange);

    /// <summary>
    /// The terminal reported a light/dark switch. Its colors changed too, so query them again: they
    /// decide the appearance. The reported scheme only matters for terminals that do not report
    /// their background.
    /// </summary>
    private void ApplyTerminalColorSchemeChange(TerminalColorScheme terminalTheme)
    {
        if (!_autoSyncEnabled)
        {
            return;
        }

        var previous = _runtime.GetTerminalTheme();
        _runtime.SetTerminalColorScheme(terminalTheme == TerminalColorScheme.Light ? "light" : "dark");
        if (_runtime.GetTerminalTheme() != previous)
        {
            ReapplyForTerminal();
        }

        QueryTerminalColors();
    }

    private void NotifyChanged()
    {
        _ui.Invalidate();
        _onChanged();
    }

    // ---------------------------------------------------------------- color comparison

    /// <summary>TS <c>sameRgb(a, b)</c>; <see cref="RgbColor"/> is a value type, so nullability is the identity test.</summary>
    private static bool SameRgb(RgbColor? a, RgbColor? b) =>
        a is null || b is null ? a is null && b is null : a.Value == b.Value;

    /// <summary>TS <c>sameTerminalColors(a, b)</c>.</summary>
    private static bool SameTerminalColors(TerminalColorsSnapshot a, TerminalColorsSnapshot b)
    {
        if (!SameRgb(a.Foreground, b.Foreground) || !SameRgb(a.Background, b.Background))
        {
            return false;
        }

        // TS compares palette identity first: an untouched palette is the same array.
        if (ReferenceEquals(a.Palette, b.Palette))
        {
            return true;
        }

        if (a.Palette is null || b.Palette is null || a.Palette.Count != b.Palette.Count)
        {
            return false;
        }

        for (var index = 0; index < a.Palette.Count; index += 1)
        {
            if (!SameRgb(a.Palette[index], b.Palette[index]))
            {
                return false;
            }
        }

        return true;
    }
}
