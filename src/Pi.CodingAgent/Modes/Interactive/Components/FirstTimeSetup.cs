// ============================================================================
// First-time setup dialog: theme choice and analytics opt-in.
// Port of modes/interactive/components/first-time-setup.ts
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>TS <c>FirstTimeSetupResult</c>.</summary>
public sealed record FirstTimeSetupResult(string Theme, bool ShareAnalytics);

/// <summary>TS <c>FirstTimeSetupOptions</c>.</summary>
public sealed record FirstTimeSetupOptions
{
    public required Action<string> OnThemePreview { get; init; }

    public required Action<FirstTimeSetupResult> OnSubmit { get; init; }

    public required Action OnCancel { get; init; }
}

/// <summary>Port of <c>first-time-setup.ts</c>.</summary>
public sealed class FirstTimeSetupComponent : Container
{
    /// <summary>TS <c>step: "theme" | "analytics"</c>.</summary>
    private enum SetupStep
    {
        Theme,
        Analytics,
    }

    /// <summary>TS <c>THEME_OPTIONS</c>.</summary>
    private static readonly (string Value, string Label)[] ThemeOptions =
    [
        (SystemTheme.SystemThemeName, "System (matches your terminal colors)"),
        ("dark", "Dark"),
        ("light", "Light"),
    ];

    /// <summary>TS <c>ANALYTICS_OPTIONS</c>.</summary>
    private static readonly (bool Value, string Label)[] AnalyticsOptions =
    [
        (true, "Share anonymous usage data"),
        (false, "Don't share"),
    ];

    /// <summary>TS <c>SETUP_LOGO_LINES</c>.</summary>
    private static readonly string[] SetupLogoLines = ["██████", "██  ██", "████  ██", "██    ██"];

    private readonly FirstTimeSetupOptions _options;
    private SetupStep _step = SetupStep.Theme;
    private int _themeIndex;
    private int _analyticsIndex;

    public FirstTimeSetupComponent(FirstTimeSetupOptions options)
    {
        _options = options;
        _themeIndex = 0;
        Update();
    }

    /// <summary>TS <c>step</c> (test seam; TS keeps the string union private).</summary>
    internal string StepName => _step == SetupStep.Theme ? "theme" : "analytics";

    /// <summary>Index of the highlighted theme row (test seam).</summary>
    internal int ThemeIndex => _themeIndex;

    /// <summary>Index of the highlighted analytics row (test seam).</summary>
    internal int AnalyticsIndex => _analyticsIndex;

    /// <summary>Rebuild on theme changes, e.g. when the system theme receives the terminal's colors.</summary>
    public override void Invalidate()
    {
        Update();
        base.Invalidate();
    }

    // Rebuild the whole dialog on every change so theme previews recolor all text.
    private void Update()
    {
        var theme = ThemeRuntime.Current;
        Clear();
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("accent", string.Join("\n", SetupLogoLines)), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new Text(
            theme.Fg("accent", theme.Bold($"Welcome to {Config.AppName}, the minimal coding agent.")),
            1,
            0));
        AddChild(new Spacer(1));

        if (_step == SetupStep.Theme)
        {
            AddChild(new Text(theme.Fg("text", "Pick a theme."), 1, 0));
            AddChild(new Spacer(1));
            AddOptionList([.. ThemeOptions.Select(option => option.Label)], _themeIndex);
        }
        else
        {
            AddChild(new Text(theme.Fg("text", "Opt-in to anonymous usage data sharing?"), 1, 0));
            AddChild(new Text(
                theme.Fg(
                    "muted",
                    "Opting in stores a tracking identifier in settings.json and enables anonymous\n"
                    + "usage analytics. This helps us to better debug, reproduce, and resolve issues\n"
                    + "and bugs within Pi. You can observe what is shared using /privacy and make\n"
                    + "changes anytime in settings.json."),
                1,
                0));
            AddChild(new Spacer(1));
            AddOptionList([.. AnalyticsOptions.Select(option => option.Label)], _analyticsIndex);
        }

        AddChild(new Spacer(1));
        AddChild(new Text(
            KeybindingHints.RawKeyHint("↑↓", "navigate")
            + "  "
            + KeybindingHints.KeyHint(
                TuiKeybindingIds.SelectConfirm,
                _step == SetupStep.Theme ? "continue" : "finish")
            + "  "
            + KeybindingHints.KeyHint(TuiKeybindingIds.SelectCancel, "skip setup"),
            1,
            0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
    }

    private void AddOptionList(IReadOnlyList<string> labels, int selectedIndex)
    {
        var theme = ThemeRuntime.Current;
        for (var index = 0; index < labels.Count; index++)
        {
            var isSelected = index == selectedIndex;
            var prefix = isSelected ? theme.Fg("accent", "→ ") : "  ";
            var label = isSelected ? theme.Fg("accent", labels[index]) : theme.Fg("text", labels[index]);
            AddChild(new Text($"{prefix}{label}", 1, 0));
        }
    }

    private void MoveSelection(int delta)
    {
        if (_step == SetupStep.Theme)
        {
            var next = Math.Max(0, Math.Min(ThemeOptions.Length - 1, _themeIndex + delta));
            if (next != _themeIndex)
            {
                _themeIndex = next;
                _options.OnThemePreview(ThemeOptions[_themeIndex].Value);
            }
        }
        else
        {
            _analyticsIndex = Math.Max(0, Math.Min(AnalyticsOptions.Length - 1, _analyticsIndex + delta));
        }

        Update();
    }

    /// <summary>
    /// Port of <c>FirstTimeSetupComponent.handleInput</c>. Plain public method: the class inherits
    /// <see cref="IComponent"/>'s default no-op <c>HandleInput</c> through <see cref="Container"/>, so
    /// this implicitly implements it (C221).
    /// </summary>
    public void HandleInput(string data)
    {
        var keybindings = GlobalKeybindings.Get();
        if (keybindings.Matches(data, TuiKeybindingIds.SelectUp) || data == "k")
        {
            MoveSelection(-1);
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectDown) || data == "j")
        {
            MoveSelection(1);
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectConfirm) || data == "\n")
        {
            if (_step == SetupStep.Theme)
            {
                _step = SetupStep.Analytics;
                Update();
            }
            else
            {
                _options.OnSubmit(new FirstTimeSetupResult(
                    ThemeOptions[_themeIndex].Value,
                    AnalyticsOptions[_analyticsIndex].Value));
            }
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectCancel))
        {
            _options.OnCancel();
        }
    }
}
