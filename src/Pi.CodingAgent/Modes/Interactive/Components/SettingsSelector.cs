// ============================================================================
// Port of modes/interactive/components/settings-selector.ts
// ============================================================================

using System.Globalization;
using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.CodingAgent.Core;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

// Design differences (porting ledger):
// - C242: TS string unions (Transport, CacheWarmingMode, MermaidRenderingMode, TuiMode,
//   FullscreenExitOutput, doubleEscapeAction, treeFilterMode, steeringMode, followUpMode,
//   TerminalTheme, ThinkingLevel, QuietStartup) are carried as `string`, matching the shapes the
//   SettingsManager getters already produce (`GetQuietStartup()` yields "true"/"header"/"false").
//   No union validation happens at this layer; the values cycle over the TS arrays verbatim.
// - C243: `WarningSettings` is carried as `JsonObject` (SettingsManager.GetWarnings() shape); the
//   TS `{ ...warnings }` shallow copy becomes DeepClone and the optional flag defaults to true when
//   absent, exactly like the TS `?? true`.
// - C244: `Model` → `ModelSpec`, `getSupportedThinkingLevels(model)` → `ThinkingLevels.GetSupported`,
//   `model.reasoning` → `model.Reasoning`.
// - Not numbered: `a.provider.localeCompare(b.provider)` → ordinal compare (providers are lowercase
//   ASCII, so both agree); `parseInt(newValue, 10)` → int.Parse; `new Map(...)` keeps the last entry
//   on duplicate keys via indexer assignment; `String(bool)` is identity because QuietStartup is
//   already a tri-state string; `WheelScrollLines` struct replaces the TS `number | "auto"` union.

/// <summary>TS <c>SettingsConfig</c>. Properties carry defaults so the component can be constructed
/// partially (the upstream tests build it with <c>as unknown as SettingsConfig</c>); the production
/// caller (4f-4 interactive-mode) always supplies every field.</summary>
public sealed class SettingsConfig
{
    public bool AutoCompact { get; init; }
    public string DefaultModel { get; init; } = "";
    public ModelSpec? CurrentModel { get; init; }
    public IReadOnlyList<ModelSpec> AvailableDefaultModels { get; init; } = [];
    public bool ShowImages { get; init; }
    public int ImageWidthCells { get; init; }
    public bool AutoResizeImages { get; init; }
    public bool BlockImages { get; init; }
    public bool EnableSkillCommands { get; init; }
    public string SteeringMode { get; init; } = "one-at-a-time";
    public string FollowUpMode { get; init; } = "one-at-a-time";
    public string Transport { get; init; } = "auto";
    public long HttpIdleTimeoutMs { get; init; } = HttpDispatcher.DefaultHttpIdleTimeoutMs;
    public string CacheWarmingMode { get; init; } = "streaming";
    public string ThinkingLevel { get; init; } = "off";
    public IReadOnlyList<string> AvailableThinkingLevels { get; init; } = [];
    public IReadOnlyDictionary<string, string> ModelThinkingLevels { get; init; } =
        new Dictionary<string, string>();
    public string CurrentTheme { get; init; } = "";
    public string TerminalTheme { get; init; } = "dark";
    public IReadOnlyList<string> AvailableThemes { get; init; } = [];
    public bool HideThinkingBlock { get; init; }
    public string MermaidRenderingMode { get; init; } = "off";
    public bool ShowCacheMissNotices { get; init; }
    public bool CollapseChangelog { get; init; }
    public bool EnableInstallTelemetry { get; init; }
    public string DoubleEscapeAction { get; init; } = "tree";
    public string TreeFilterMode { get; init; } = "default";
    public bool ShowHardwareCursor { get; init; }
    public int EditorPaddingX { get; init; }
    public int OutputPad { get; init; }
    public int AutocompleteMaxVisible { get; init; }
    public string QuietStartup { get; init; } = "false";
    public string DefaultProjectTrust { get; init; } = "ask";
    public bool ClearOnShrink { get; init; }
    public bool ShowTerminalProgress { get; init; }
    public string TuiMode { get; init; } = "regular";
    public string FullscreenExitOutput { get; init; } = "transcript";
    public ScrollViewScrollbar FullscreenScrollbar { get; init; } = ScrollViewScrollbar.Auto;
    public bool FullscreenCopyOnSelect { get; init; }
    public WheelScrollLines FullscreenWheelScrollLines { get; init; } = WheelScrollLines.Auto;
    public JsonObject Warnings { get; init; } = [];
}

/// <summary>TS <c>SettingsCallbacks</c>. Every change callback receives the parsed value.</summary>
public sealed class SettingsCallbacks
{
    public Action<bool>? OnAutoCompactChange { get; init; }
    public Action<bool>? OnShowImagesChange { get; init; }
    public Action<int>? OnImageWidthCellsChange { get; init; }
    public Action<bool>? OnAutoResizeImagesChange { get; init; }
    public Action<bool>? OnBlockImagesChange { get; init; }
    public Action<bool>? OnEnableSkillCommandsChange { get; init; }
    public Action<string>? OnSteeringModeChange { get; init; }
    public Action<string>? OnFollowUpModeChange { get; init; }
    public Action<string>? OnTransportChange { get; init; }
    public Action<long>? OnHttpIdleTimeoutMsChange { get; init; }
    public Action<string>? OnCacheWarmingModeChange { get; init; }
    public Action<string, string, string>? OnModelThinkingLevelChange { get; init; }
    public Action<string, string>? OnModelThinkingLevelRemove { get; init; }
    public Action<string>? OnThemeChange { get; init; }
    public Action<string>? OnThemePreview { get; init; }
    public Action<bool>? OnHideThinkingBlockChange { get; init; }
    public Action<string>? OnMermaidRenderingModeChange { get; init; }
    public Action<bool>? OnShowCacheMissNoticesChange { get; init; }
    public Action<bool>? OnCollapseChangelogChange { get; init; }
    public Action<bool>? OnEnableInstallTelemetryChange { get; init; }
    public Action<string>? OnDoubleEscapeActionChange { get; init; }
    public Action<string>? OnTreeFilterModeChange { get; init; }
    public Action<bool>? OnShowHardwareCursorChange { get; init; }
    public Action<int>? OnEditorPaddingXChange { get; init; }
    public Action<int>? OnOutputPadChange { get; init; }
    public Action<int>? OnAutocompleteMaxVisibleChange { get; init; }
    public Action<string>? OnQuietStartupChange { get; init; }
    public Action<string>? OnDefaultProjectTrustChange { get; init; }
    public Action<bool>? OnClearOnShrinkChange { get; init; }
    public Action<bool>? OnShowTerminalProgressChange { get; init; }
    public Action<string>? OnTuiModeChange { get; init; }
    public Action<string>? OnFullscreenExitOutputChange { get; init; }
    public Action<ScrollViewScrollbar>? OnFullscreenScrollbarChange { get; init; }
    public Action<bool>? OnFullscreenCopyOnSelectChange { get; init; }
    public Action<WheelScrollLines>? OnFullscreenWheelScrollLinesChange { get; init; }
    public Action<JsonObject>? OnWarningsChange { get; init; }
    public Action? OnCancel { get; init; }
}

// ---------------------------------------------------------------------------
// Private helpers and submenus (module-private in TS)
// ---------------------------------------------------------------------------

internal static class SettingsSelectorStatics
{
    public static readonly SelectListLayoutOptions ModelPickerLayout = new()
    {
        MinPrimaryColumnWidth = 12,
        MaxPrimaryColumnWidth = 46,
    };

    public static readonly IReadOnlyDictionary<string, string> ThinkingDescriptions =
        new Dictionary<string, string>
        {
            ["off"] = "No reasoning",
            ["minimal"] = "Very brief reasoning (~1k tokens)",
            ["low"] = "Light reasoning (~2k tokens)",
            ["medium"] = "Moderate reasoning (~8k tokens)",
            ["high"] = "Deep reasoning (~16k tokens)",
            ["xhigh"] = "Extra-high reasoning (~32k tokens)",
            ["max"] = "Maximum reasoning",
        };

    public static readonly IReadOnlyDictionary<string, string> DefaultProjectTrustLabels =
        new Dictionary<string, string>
        {
            ["ask"] = "Ask",
            ["always"] = "Always trust",
            ["never"] = "Never trust",
        };

    public static readonly IReadOnlyDictionary<string, string> DefaultProjectTrustByLabel =
        DefaultProjectTrustLabels.ToDictionary(pair => pair.Value, pair => pair.Key);

    public const string ClearOverrideValue = "__clear__";
    public const string AutomaticThemeValue = "/";

    public static string ModelSettingKey(ModelSpec model) => $"{model.Provider}/{model.Id}";

    public static string ModelDisplayLabel(ModelSpec model) => $"{model.Id} [{model.Provider}]";

    public static string ModelThinkingOverridesSummary(IReadOnlyDictionary<string, string> overrides) =>
        overrides.Count == 0 ? "none" : $"{overrides.Count} configured";

    public static string ModelItemLabel(ModelSpec model) =>
        $"{model.Id} {ThemeRuntime.Current.Fg("muted", $"[{model.Provider}]")}";

    public static List<SelectItem> ThemeItems(IReadOnlyList<string> availableThemes, string currentTheme) =>
    [
        .. availableThemes.Select(name => new SelectItem
        {
            Value = name,
            Label = $"{(name == currentTheme ? "✓ " : "  ")}{name}",
            Description = name == SystemTheme.SystemThemeName
                ? "Theme created from your terminal's colors"
                : null,
        }),
    ];

    /// <summary>The system theme comes first, then automatic mode, then the remaining themes.</summary>
    public static List<SelectItem> SingleModeThemeItems(IReadOnlyList<string> availableThemes, string currentTheme)
    {
        var items = ThemeItems(availableThemes, currentTheme);
        var systemIndex = items.FindIndex(item => item.Value == SystemTheme.SystemThemeName);
        var system = systemIndex == -1 ? [] : items.Skip(systemIndex).Take(1).ToList();
        if (systemIndex != -1)
        {
            items.RemoveAt(systemIndex);
        }
        return
        [
            .. system,
            new SelectItem
            {
                Value = AutomaticThemeValue,
                Label = "  automatic",
                Description = "Use separate themes for light and dark terminal appearance",
            },
            .. items,
        ];
    }

    public static string PreferredTheme(IReadOnlyList<string> availableThemes, string? preferred, string fallback)
    {
        if (preferred is { Length: > 0 } && availableThemes.Contains(preferred, StringComparer.Ordinal))
        {
            return preferred;
        }
        return availableThemes.Contains(fallback, StringComparer.Ordinal) ? fallback : availableThemes.FirstOrDefault() ?? fallback;
    }

    public static (string LightTheme, string DarkTheme) DefaultAutomaticThemes(
        string currentThemeSetting, IReadOnlyList<string> availableThemes)
    {
        if (ThemeRuntime.ParseAutoThemeSetting(currentThemeSetting) is { } autoTheme)
        {
            return autoTheme;
        }
        var currentFixedTheme = currentThemeSetting.Contains('/') ? null : currentThemeSetting;
        var themeName = PreferredTheme(availableThemes, currentFixedTheme, SystemTheme.SystemThemeName);
        return (themeName, themeName);
    }
}

/// <summary>Submenu for the warnings entry (TS module-private <c>WarningSettingsSubmenu</c>).</summary>
internal sealed class WarningSettingsSubmenu : Container
{
    private readonly SettingsList _settingsList;
    private JsonObject _state;

    public WarningSettingsSubmenu(
        JsonObject warnings, Action<JsonObject> onChange, Action onCancel)
    {
        _state = (JsonObject)warnings.DeepClone();

        var items = new List<SettingItem>
        {
            new()
            {
                Id = "anthropic-extra-usage",
                Label = "Anthropic extra usage",
                Description = "Warn when Anthropic subscription auth may use paid extra usage",
                CurrentValue = GetAnthropicExtraUsage(_state) ? "true" : "false",
                Values = ["true", "false"],
            },
        };

        _settingsList = new SettingsList(
            items,
            Math.Min(items.Count, 10),
            ThemeHelpers.GetSettingsListTheme(),
            (id, newValue) =>
            {
                switch (id)
                {
                    case "anthropic-extra-usage":
                        _state["anthropicExtraUsage"] = newValue == "true";
                        onChange((JsonObject)_state.DeepClone());
                        break;
                }
            },
            onCancel);
        AddChild(_settingsList);
    }

    /// <summary>TS <c>this.state.anthropicExtraUsage ?? true</c>.</summary>
    private static bool GetAnthropicExtraUsage(JsonObject state) =>
        state.TryGetPropertyValue("anthropicExtraUsage", out var value)
        && value is JsonValue { } jsonValue
        && jsonValue.TryGetValue<bool>(out var flag)
            ? flag
            : true;

    public override void HandleInput(string data) => _settingsList.HandleInput(data);
}

/// <summary>Submenu for the theme entry (TS module-private <c>ThemeSubmenu</c>).</summary>
internal sealed class ThemeSubmenu : Container
{
    private IComponent? _inputComponent;
    private readonly SettingsCallbacks _callbacks;
    private readonly IReadOnlyList<string> _availableThemes;
    private readonly string _terminalTheme;
    private readonly Action<string?> _onDone;
    private readonly string _originalThemeSetting;
    private string _mode; // "single" | "automatic"
    private string _singleTheme;
    private string _lightTheme;
    private string _darkTheme;

    public ThemeSubmenu(
        string currentThemeSetting,
        string terminalTheme,
        IReadOnlyList<string> availableThemes,
        SettingsCallbacks callbacks,
        Action<string?> onDone)
    {
        _callbacks = callbacks;
        _availableThemes = availableThemes;
        _terminalTheme = terminalTheme;
        _onDone = onDone;
        _originalThemeSetting = currentThemeSetting;
        var autoTheme = ThemeRuntime.ParseAutoThemeSetting(currentThemeSetting);
        var automaticThemes = SettingsSelectorStatics.DefaultAutomaticThemes(currentThemeSetting, availableThemes);
        var fixedTheme = autoTheme is not null || currentThemeSetting.Contains('/') ? null : currentThemeSetting;
        _mode = autoTheme is not null ? "automatic" : "single";
        _lightTheme = automaticThemes.LightTheme;
        _darkTheme = automaticThemes.DarkTheme;
        _singleTheme = SettingsSelectorStatics.PreferredTheme(
            availableThemes,
            fixedTheme ?? (autoTheme is not null ? GetActiveAutomaticTheme() : null),
            SystemTheme.SystemThemeName);

        if (_mode == "automatic")
        {
            ShowAutomaticMenu();
        }
        else
        {
            ShowSingleMenu();
        }
    }

    public override void HandleInput(string data) => _inputComponent?.HandleInput(data);

    private void SetContent(IComponent renderComponent, IComponent? inputComponent = null)
    {
        Clear();
        AddChild(renderComponent);
        _inputComponent = inputComponent ?? renderComponent;
    }

    private void ShowSingleMenu()
    {
        _mode = "single";
        var menu = new SelectSubmenu(
            "Theme",
            "Select a theme, or choose automatic to follow terminal appearance.",
            SettingsSelectorStatics.SingleModeThemeItems(_availableThemes, _singleTheme),
            _singleTheme,
            value =>
            {
                if (value == SettingsSelectorStatics.AutomaticThemeValue)
                {
                    _mode = "automatic";
                    _callbacks.OnThemePreview?.Invoke(GetThemeSetting());
                    ShowAutomaticMenu();
                    return;
                }

                _singleTheme = value;
                Apply(value);
            },
            Cancel,
            value => _callbacks.OnThemePreview?.Invoke(
                value == SettingsSelectorStatics.AutomaticThemeValue ? GetAutomaticThemeSetting() : value));
        SetContent(menu);
    }

    private void ShowAutomaticMenu()
    {
        _mode = "automatic";
        var content = new Container();
        content.AddChild(new Text(ThemeRuntime.Current.Bold(ThemeRuntime.Current.Fg("accent", "Automatic Theme")), 0, 0));
        content.AddChild(new Spacer(1));
        content.AddChild(new Text(ThemeRuntime.Current.Fg("muted", "Choose themes for terminal light and dark appearance."), 0, 0));
        content.AddChild(new Text(ThemeRuntime.Current.Fg("muted", "Light/dark detection requires terminal support."), 0, 0));
        content.AddChild(new Spacer(1));

        var items = new List<SettingItem>
        {
            new()
            {
                Id = "light-theme",
                Label = "Light theme",
                Description = "Theme to use in automatic mode when the terminal is light",
                CurrentValue = _lightTheme,
                Submenu = (currentValue, done) =>
                    CreateThemeSelect(
                        "Light Theme",
                        "Select the theme to use for light terminal appearance",
                        currentValue,
                        value =>
                        {
                            _lightTheme = value;
                            _callbacks.OnThemePreview?.Invoke(GetThemeSetting());
                            done(value, null);
                        },
                        () => done(null, null)),
            },
            new()
            {
                Id = "dark-theme",
                Label = "Dark theme",
                Description = "Theme to use in automatic mode when the terminal is dark",
                CurrentValue = _darkTheme,
                Submenu = (currentValue, done) =>
                    CreateThemeSelect(
                        "Dark Theme",
                        "Select the theme to use for dark terminal appearance",
                        currentValue,
                        value =>
                        {
                            _darkTheme = value;
                            _callbacks.OnThemePreview?.Invoke(GetThemeSetting());
                            done(value, null);
                        },
                        () => done(null, null)),
            },
            new()
            {
                Id = "apply",
                Label = "Apply",
                Description = "Save and go back",
                CurrentValue = "save and go back",
                Values = ["save and go back"],
            },
            new()
            {
                Id = "single-mode",
                Label = "Change mode",
                Description = "Switch to one theme for light and dark",
                CurrentValue = "switch to single theme",
                Values = ["switch to single theme"],
            },
        };

        var settingsList = new SettingsList(
            items,
            Math.Min(items.Count, 10),
            ThemeHelpers.GetSettingsListTheme(),
            (id, _) =>
            {
                switch (id)
                {
                    case "single-mode":
                        _mode = "single";
                        _singleTheme = GetActiveAutomaticTheme();
                        _callbacks.OnThemePreview?.Invoke(_singleTheme);
                        ShowSingleMenu();
                        break;
                    case "apply":
                        Apply(GetAutomaticThemeSetting());
                        break;
                }
            },
            Cancel);
        content.AddChild(settingsList);
        SetContent(content, settingsList);
    }

    /// <summary>TS <c>createThemeSelect</c>: onSelect and onCancel are separate callbacks there; the
    /// C# <see cref="SettingsListSubmenuFactory"/> merges them into a single <c>done</c>, so the two
    /// closures are rebuilt here.</summary>
    private SelectSubmenu CreateThemeSelect(
        string title,
        string description,
        string currentValue,
        Action<string> onSelect,
        Action onCancel)
    {
        return new SelectSubmenu(
            title,
            description,
            SettingsSelectorStatics.ThemeItems(_availableThemes, currentValue),
            currentValue,
            onSelect,
            onCancel,
            value => _callbacks.OnThemePreview?.Invoke(value));
    }

    private string GetThemeSetting() => _mode == "automatic" ? GetAutomaticThemeSetting() : _singleTheme;

    private string GetActiveAutomaticTheme() => _terminalTheme == "light" ? _lightTheme : _darkTheme;

    private string GetAutomaticThemeSetting() => $"{_lightTheme}/{_darkTheme}";

    private void Apply(string themeSetting) => _onDone(themeSetting);

    private void Cancel()
    {
        _callbacks.OnThemePreview?.Invoke(_originalThemeSetting);
        _onDone(null);
    }
}

/// <summary>Main settings selector component (TS <c>SettingsSelectorComponent</c>).</summary>
public sealed class SettingsSelectorComponent : Container
{
    private readonly SettingsList _settingsList;

    public SettingsSelectorComponent(SettingsConfig config, SettingsCallbacks callbacks)
    {
        var supportsImages = TerminalImage.GetCapabilities().Images != ImageProtocol.None;
        var followUpKey = KeybindingHints.KeyDisplayText("app.message.followUp");
        var cycleThinkingKey = KeybindingHints.KeyDisplayText("app.thinking.cycle");
        var currentWarnings = (JsonObject)config.Warnings.DeepClone();
        var currentModelThinkingLevels = new Dictionary<string, string>(config.ModelThinkingLevels);
        var defaultModelByValue = new Dictionary<string, ModelSpec>();
        foreach (var model in config.AvailableDefaultModels)
        {
            // A TS Map keeps the last entry for duplicate keys.
            defaultModelByValue[SettingsSelectorStatics.ModelSettingKey(model)] = model;
        }
        var currentDefaultModelKey = defaultModelByValue.ContainsKey(config.DefaultModel)
            ? config.DefaultModel
            : null;
        var currentModelKey = config.CurrentModel is not null
            ? SettingsSelectorStatics.ModelSettingKey(config.CurrentModel)
            : null;

        var items = new List<SettingItem>
        {
            new()
            {
                Id = "autocompact",
                Label = "Auto-compact",
                Description = "Automatically compact context when it gets too large",
                CurrentValue = config.AutoCompact ? "true" : "false",
                Values = ["true", "false"],
            },
            new()
            {
                Id = "steering-mode",
                Label = "Steering mode",
                Description =
                    "Enter while streaming queues steering messages. 'one-at-a-time': deliver one, wait for response. 'all': deliver all at once.",
                CurrentValue = config.SteeringMode,
                Values = ["one-at-a-time", "all"],
            },
            new()
            {
                Id = "follow-up-mode",
                Label = "Follow-up mode",
                Description = $"{followUpKey} queues follow-up messages until agent stops. 'one-at-a-time': deliver one, wait for response. 'all': deliver all at once.",
                CurrentValue = config.FollowUpMode,
                Values = ["one-at-a-time", "all"],
            },
            new()
            {
                Id = "transport",
                Label = "Transport",
                Description = "Preferred transport for providers that support multiple transports",
                CurrentValue = config.Transport,
                Values = ["sse", "websocket", "websocket-cached", "auto"],
            },
            new()
            {
                Id = "http-idle-timeout",
                Label = "HTTP idle timeout",
                Description =
                    "Maximum idle gap while waiting for HTTP headers or body chunks. Disable for local models that pause longer than five minutes.",
                CurrentValue = HttpDispatcher.FormatHttpIdleTimeoutMs(config.HttpIdleTimeoutMs),
                Values = [.. HttpDispatcher.HttpIdleTimeoutChoices.Select(choice => choice.Label)],
            },
            new()
            {
                Id = "cache-warming-mode",
                Label = "Cache warming",
                Description =
                    "off; streaming while the agent runs; idle also between runs while continuation stays profitable",
                CurrentValue = config.CacheWarmingMode,
                Values = ["off", "streaming", "idle"],
            },
            new()
            {
                Id = "hide-thinking",
                Label = "Hide thinking",
                Description = "Hide thinking blocks in assistant responses",
                CurrentValue = config.HideThinkingBlock ? "true" : "false",
                Values = ["true", "false"],
            },
            new()
            {
                Id = "mermaid-rendering",
                Label = "Mermaid diagrams",
                Description = "Render Mermaid code blocks as Unicode diagrams",
                CurrentValue = config.MermaidRenderingMode,
                Values = ["off", "final", "streaming"],
            },
            new()
            {
                Id = "cache-miss-notices",
                Label = "Cache miss notices",
                Description = "Show transcript notices for cache costs and provider recovery diagnostics",
                CurrentValue = config.ShowCacheMissNotices ? "true" : "false",
                Values = ["true", "false"],
            },
            new()
            {
                Id = "collapse-changelog",
                Label = "Collapse changelog",
                Description = "Show condensed changelog after updates",
                CurrentValue = config.CollapseChangelog ? "true" : "false",
                Values = ["true", "false"],
            },
            new()
            {
                Id = "quiet-startup",
                Label = "Quiet startup",
                Description = "Disable verbose printing at startup (header: keep only the startup header)",
                CurrentValue = config.QuietStartup,
                Values = ["true", "header", "false"],
            },
            new()
            {
                Id = "install-telemetry",
                Label = "Install telemetry",
                Description = "Send an anonymous version/update ping after changelog-detected updates",
                CurrentValue = config.EnableInstallTelemetry ? "true" : "false",
                Values = ["true", "false"],
            },
            new()
            {
                Id = "default-project-trust",
                Label = "Default project trust",
                Description = "Fallback behavior when no extension or saved trust decision decides project trust",
                CurrentValue = SettingsSelectorStatics.DefaultProjectTrustLabels[config.DefaultProjectTrust],
                Values = [.. SettingsSelectorStatics.DefaultProjectTrustLabels.Values],
            },
            new()
            {
                Id = "double-escape-action",
                Label = "Double-escape action",
                Description = "Action when pressing Escape twice with empty editor",
                CurrentValue = config.DoubleEscapeAction,
                Values = ["tree", "fork", "none"],
            },
            new()
            {
                Id = "tree-filter-mode",
                Label = "Tree filter mode",
                Description = "Default filter when opening /tree",
                CurrentValue = config.TreeFilterMode,
                Values = ["default", "no-tools", "user-only", "labeled-only", "all"],
            },
            new()
            {
                Id = "warnings",
                Label = "Warnings",
                Description = "Enable or disable individual warnings",
                CurrentValue = "configure",
                Submenu = (_currentValue, done) =>
                    new WarningSettingsSubmenu(
                        currentWarnings,
                        warnings =>
                        {
                            currentWarnings = warnings;
                            callbacks.OnWarningsChange?.Invoke(warnings);
                        },
                        () => done(null, null)),
            },
            new()
            {
                Id = "model-thinking",
                Label = "Default thinking level per model",
                Description = $"Override the default thinking level for specific models. {cycleThinkingKey} cycles in-session.",
                CurrentValue = SettingsSelectorStatics.ModelThinkingOverridesSummary(currentModelThinkingLevels),
                Submenu = (_currentValue, done) =>
                {
                    var steps = new List<SteppedSubmenuStep>
                    {
                        new()
                        {
                            Key = "model",
                            Title = "Per-Model Thinking Level",
                            Description = "Select a model to configure",
                            Options = _ =>
                            {
                                var sorted = config.AvailableDefaultModels
                                    .OrderBy(model =>
                                    {
                                        var key = SettingsSelectorStatics.ModelSettingKey(model);
                                        if (key == currentModelKey) return 0;
                                        if (key == currentDefaultModelKey) return 1;
                                        return 2;
                                    })
                                    .ThenBy(model => model.Provider, StringComparer.Ordinal)
                                    .ToList();
                                // TS's comparator sorts by provider only when neither model is the
                                // current/default one; OrderBy(rank).ThenBy(provider) produces the same
                                // order because the rank groups are disjoint.
                                var selectItems = new List<SelectItem>();
                                foreach (var model in sorted)
                                {
                                    var key = SettingsSelectorStatics.ModelSettingKey(model);
                                    currentModelThinkingLevels.TryGetValue(key, out var overrideLevel);
                                    selectItems.Add(new SelectItem
                                    {
                                        Value = key,
                                        Label = SettingsSelectorStatics.ModelItemLabel(model),
                                        Description = overrideLevel,
                                    });
                                }
                                if (selectItems.Count == 0)
                                {
                                    selectItems.Add(new SelectItem
                                    {
                                        Value = "__none__",
                                        Label = "No models available",
                                        Description = "Log in to a provider or configure an API key first",
                                    });
                                }
                                return selectItems;
                            },
                            Preselect = _ => currentModelKey ?? currentDefaultModelKey,
                            Searchable = true,
                            Layout = SettingsSelectorStatics.ModelPickerLayout,
                        },
                        new()
                        {
                            Key = "level",
                            Title = SubmenuText.FromFactory(ctx =>
                            {
                                var model = defaultModelByValue.GetValueOrDefault(ctx["model"]);
                                return $"Thinking Level for {(model is not null ? SettingsSelectorStatics.ModelDisplayLabel(model) : ctx["model"])}";
                            }),
                            Description = "Select default thinking level for this model",
                            Options = ctx =>
                            {
                                var model = defaultModelByValue.GetValueOrDefault(ctx["model"]);
                                if (model is null) return [];
                                var levels = model.Reasoning
                                    ? Pi.Ai.Models.ThinkingLevels.GetSupported(model)
                                    : (IReadOnlyList<string>)["off"];
                                var activeLevel = currentModelThinkingLevels.GetValueOrDefault(ctx["model"]);
                                var selectItems = levels.Select(level => new SelectItem
                                {
                                    Value = level,
                                    Label = $"{(level == activeLevel ? "✓ " : "  ")}{level}",
                                    Description = SettingsSelectorStatics.ThinkingDescriptions[level],
                                }).ToList();
                                if (currentModelThinkingLevels.ContainsKey(ctx["model"]))
                                {
                                    selectItems.Add(new SelectItem
                                    {
                                        Value = SettingsSelectorStatics.ClearOverrideValue,
                                        Label = "  (clear override)",
                                        Description = $"Revert to global default ({config.ThinkingLevel})",
                                    });
                                }
                                return selectItems;
                            },
                            Preselect = ctx => currentModelThinkingLevels.GetValueOrDefault(ctx["model"]),
                        },
                    };

                    string Summary() => SettingsSelectorStatics.ModelThinkingOverridesSummary(currentModelThinkingLevels);

                    return new SteppedSubmenu(
                        steps,
                        selections =>
                        {
                            var model = defaultModelByValue.GetValueOrDefault(selections["model"]);
                            if (model is null) return;
                            if (selections["level"] == SettingsSelectorStatics.ClearOverrideValue)
                            {
                                callbacks.OnModelThinkingLevelRemove?.Invoke(model.Provider, model.Id);
                                currentModelThinkingLevels.Remove(selections["model"]);
                            }
                            else
                            {
                                callbacks.OnModelThinkingLevelChange?.Invoke(
                                    model.Provider, model.Id, selections["level"]);
                                currentModelThinkingLevels[selections["model"]] = selections["level"];
                            }
                        },
                        () => done(Summary(), null),
                        new SteppedSubmenuOptions { Loop = true });
                },
            },
            new()
            {
                Id = "tui-mode",
                Label = "TUI mode",
                Description = "Interface layout; regular mode uses the terminal's normal scrollback",
                CurrentValue = config.TuiMode,
                Values = ["regular", "fullscreen"],
            },
            new()
            {
                Id = "fullscreen-exit-output",
                Label = "Fullscreen exit output",
                Description = "Print the transcript or only a session resume hint when exiting fullscreen mode",
                CurrentValue = config.FullscreenExitOutput,
                Values = ["transcript", "resume-hint"],
            },
            new()
            {
                Id = "fullscreen-scrollbar",
                Label = "Fullscreen scrollbar",
                Description = "Scrollbar behavior in fullscreen mode; has no effect in regular mode",
                CurrentValue = config.FullscreenScrollbar.ToString().ToLowerInvariant() switch
                {
                    "always" => "always",
                    "hidden" => "hidden",
                    _ => "auto",
                },
                Values = ["auto", "always", "hidden"],
            },
            new()
            {
                Id = "fullscreen-copy-on-select",
                Label = "Fullscreen copy on select",
                Description = "Automatically copy selected text in fullscreen mode; disable to copy selections with Ctrl+X",
                CurrentValue = config.FullscreenCopyOnSelect ? "true" : "false",
                Values = ["true", "false"],
            },
            new()
            {
                Id = "fullscreen-wheel-scroll-lines",
                Label = "Fullscreen wheel scrolling",
                Description =
                    "Lines per mouse-wheel event in fullscreen mode; 'auto' speeds up fast wheel spins where the terminal does not",
                CurrentValue = config.FullscreenWheelScrollLines.IsAuto
                    ? "auto"
                    : ((int)config.FullscreenWheelScrollLines.Lines).ToString(CultureInfo.InvariantCulture),
                Values = WheelScrollValues(config.FullscreenWheelScrollLines),
            },
            new()
            {
                Id = "theme",
                Label = "Theme",
                Description = "Color theme for the interface",
                CurrentValue = config.CurrentTheme,
                Submenu = (currentValue, done) =>
                    new ThemeSubmenu(currentValue, config.TerminalTheme, config.AvailableThemes, callbacks,
                        selectedValue => done(selectedValue, null)),
            },
        };

        // Only show image toggle if terminal supports it
        if (supportsImages)
        {
            // Insert after autocompact
            items.Insert(1, new SettingItem
            {
                Id = "show-images",
                Label = "Show images",
                Description = "Render images inline in terminal",
                CurrentValue = config.ShowImages ? "true" : "false",
                Values = ["true", "false"],
            });
            items.Insert(2, new SettingItem
            {
                Id = "image-width-cells",
                Label = "Image width",
                Description = "Preferred inline image width in terminal cells",
                CurrentValue = config.ImageWidthCells.ToString(CultureInfo.InvariantCulture),
                Values = ["60", "80", "120"],
            });
        }

        // Image auto-resize toggle (always available, affects both attached and read images)
        items.Insert(supportsImages ? 3 : 1, new SettingItem
        {
            Id = "auto-resize-images",
            Label = "Auto-resize images",
            Description = "Resize large images to 2000x2000 max for better model compatibility",
            CurrentValue = config.AutoResizeImages ? "true" : "false",
            Values = ["true", "false"],
        });

        // Block images toggle (always available, insert after auto-resize-images)
        var autoResizeIndex = items.FindIndex(item => item.Id == "auto-resize-images");
        items.Insert(autoResizeIndex + 1, new SettingItem
        {
            Id = "block-images",
            Label = "Block images",
            Description = "Prevent images from being sent to LLM providers",
            CurrentValue = config.BlockImages ? "true" : "false",
            Values = ["true", "false"],
        });

        // Skill commands toggle (insert after block-images)
        var blockImagesIndex = items.FindIndex(item => item.Id == "block-images");
        items.Insert(blockImagesIndex + 1, new SettingItem
        {
            Id = "skill-commands",
            Label = "Skill commands",
            Description = "Register skills as /skill:name commands",
            CurrentValue = config.EnableSkillCommands ? "true" : "false",
            Values = ["true", "false"],
        });

        // Hardware cursor toggle (insert after skill-commands)
        var skillCommandsIndex = items.FindIndex(item => item.Id == "skill-commands");
        items.Insert(skillCommandsIndex + 1, new SettingItem
        {
            Id = "show-hardware-cursor",
            Label = "Show hardware cursor",
            Description = "Show the terminal cursor while still positioning it for IME support",
            CurrentValue = config.ShowHardwareCursor ? "true" : "false",
            Values = ["true", "false"],
        });

        // Editor padding toggle (insert after show-hardware-cursor)
        var hardwareCursorIndex = items.FindIndex(item => item.Id == "show-hardware-cursor");
        items.Insert(hardwareCursorIndex + 1, new SettingItem
        {
            Id = "editor-padding",
            Label = "Editor padding",
            Description = "Horizontal padding for input editor (0-3)",
            CurrentValue = config.EditorPaddingX.ToString(CultureInfo.InvariantCulture),
            Values = ["0", "1", "2", "3"],
        });

        // Output padding toggle (insert after editor-padding)
        var editorPaddingIndex = items.FindIndex(item => item.Id == "editor-padding");
        items.Insert(editorPaddingIndex + 1, new SettingItem
        {
            Id = "output-padding",
            Label = "Output padding",
            Description = "Horizontal padding for messages, tool output, and command output",
            CurrentValue = config.OutputPad.ToString(CultureInfo.InvariantCulture),
            Values = ["0", "1"],
        });

        // Autocomplete max visible toggle (insert after output-padding)
        var outputPaddingIndex = items.FindIndex(item => item.Id == "output-padding");
        items.Insert(outputPaddingIndex + 1, new SettingItem
        {
            Id = "autocomplete-max-visible",
            Label = "Autocomplete max items",
            Description = "Max visible items in autocomplete dropdown (3-20)",
            CurrentValue = config.AutocompleteMaxVisible.ToString(CultureInfo.InvariantCulture),
            Values = ["3", "5", "7", "10", "15", "20"],
        });

        // Clear on shrink toggle (insert after autocomplete-max-visible)
        var autocompleteIndex = items.FindIndex(item => item.Id == "autocomplete-max-visible");
        items.Insert(autocompleteIndex + 1, new SettingItem
        {
            Id = "clear-on-shrink",
            Label = "Clear on shrink",
            Description = "Clear empty rows when content shrinks (may cause flicker)",
            CurrentValue = config.ClearOnShrink ? "true" : "false",
            Values = ["true", "false"],
        });

        // Terminal progress toggle (insert after clear-on-shrink)
        var clearOnShrinkIndex = items.FindIndex(item => item.Id == "clear-on-shrink");
        items.Insert(clearOnShrinkIndex + 1, new SettingItem
        {
            Id = "terminal-progress",
            Label = "Terminal progress",
            Description = "Show OSC 9;4 progress indicators in the terminal tab bar",
            CurrentValue = config.ShowTerminalProgress ? "true" : "false",
            Values = ["true", "false"],
        });

        // Add borders
        AddChild(new DynamicBorder());

        _settingsList = new SettingsList(
            items,
            10,
            ThemeHelpers.GetSettingsListTheme(),
            (id, newValue) =>
            {
                switch (id)
                {
                    case "autocompact":
                        callbacks.OnAutoCompactChange?.Invoke(newValue == "true");
                        break;
                    case "show-images":
                        callbacks.OnShowImagesChange?.Invoke(newValue == "true");
                        break;
                    case "image-width-cells":
                        callbacks.OnImageWidthCellsChange?.Invoke(int.Parse(newValue, CultureInfo.InvariantCulture));
                        break;
                    case "auto-resize-images":
                        callbacks.OnAutoResizeImagesChange?.Invoke(newValue == "true");
                        break;
                    case "block-images":
                        callbacks.OnBlockImagesChange?.Invoke(newValue == "true");
                        break;
                    case "skill-commands":
                        callbacks.OnEnableSkillCommandsChange?.Invoke(newValue == "true");
                        break;
                    case "steering-mode":
                        callbacks.OnSteeringModeChange?.Invoke(newValue);
                        break;
                    case "follow-up-mode":
                        callbacks.OnFollowUpModeChange?.Invoke(newValue);
                        break;
                    case "transport":
                        callbacks.OnTransportChange?.Invoke(newValue);
                        break;
                    case "http-idle-timeout":
                    {
                        var choice = HttpDispatcher.HttpIdleTimeoutChoices.FirstOrDefault(item => item.Label == newValue);
                        if (choice is not null)
                        {
                            callbacks.OnHttpIdleTimeoutMsChange?.Invoke(choice.TimeoutMs);
                        }
                        break;
                    }
                    case "cache-warming-mode":
                        callbacks.OnCacheWarmingModeChange?.Invoke(newValue);
                        break;
                    case "hide-thinking":
                        callbacks.OnHideThinkingBlockChange?.Invoke(newValue == "true");
                        break;
                    case "mermaid-rendering":
                        callbacks.OnMermaidRenderingModeChange?.Invoke(newValue);
                        break;
                    case "cache-miss-notices":
                        callbacks.OnShowCacheMissNoticesChange?.Invoke(newValue == "true");
                        break;
                    case "collapse-changelog":
                        callbacks.OnCollapseChangelogChange?.Invoke(newValue == "true");
                        break;
                    case "quiet-startup":
                        callbacks.OnQuietStartupChange?.Invoke(newValue == "header" ? "header" : newValue == "true" ? "true" : "false");
                        break;
                    case "install-telemetry":
                        callbacks.OnEnableInstallTelemetryChange?.Invoke(newValue == "true");
                        break;
                    case "default-project-trust":
                    {
                        if (SettingsSelectorStatics.DefaultProjectTrustByLabel.TryGetValue(newValue, out var defaultProjectTrust))
                        {
                            callbacks.OnDefaultProjectTrustChange?.Invoke(defaultProjectTrust);
                        }
                        break;
                    }
                    case "double-escape-action":
                        callbacks.OnDoubleEscapeActionChange?.Invoke(newValue);
                        break;
                    case "tree-filter-mode":
                        callbacks.OnTreeFilterModeChange?.Invoke(newValue);
                        break;
                    case "show-hardware-cursor":
                        callbacks.OnShowHardwareCursorChange?.Invoke(newValue == "true");
                        break;
                    case "editor-padding":
                        callbacks.OnEditorPaddingXChange?.Invoke(int.Parse(newValue, CultureInfo.InvariantCulture));
                        break;
                    case "output-padding":
                        callbacks.OnOutputPadChange?.Invoke(newValue == "0" ? 0 : 1);
                        break;
                    case "autocomplete-max-visible":
                        callbacks.OnAutocompleteMaxVisibleChange?.Invoke(int.Parse(newValue, CultureInfo.InvariantCulture));
                        break;
                    case "clear-on-shrink":
                        callbacks.OnClearOnShrinkChange?.Invoke(newValue == "true");
                        break;
                    case "terminal-progress":
                        callbacks.OnShowTerminalProgressChange?.Invoke(newValue == "true");
                        break;
                    case "tui-mode":
                        callbacks.OnTuiModeChange?.Invoke(newValue);
                        break;
                    case "fullscreen-exit-output":
                        callbacks.OnFullscreenExitOutputChange?.Invoke(newValue);
                        break;
                    case "fullscreen-scrollbar":
                        callbacks.OnFullscreenScrollbarChange?.Invoke(newValue switch
                        {
                            "always" => ScrollViewScrollbar.Always,
                            "hidden" => ScrollViewScrollbar.Hidden,
                            _ => ScrollViewScrollbar.Auto,
                        });
                        break;
                    case "fullscreen-copy-on-select":
                        callbacks.OnFullscreenCopyOnSelectChange?.Invoke(newValue == "true");
                        break;
                    case "fullscreen-wheel-scroll-lines":
                        callbacks.OnFullscreenWheelScrollLinesChange?.Invoke(newValue == "auto"
                            ? WheelScrollLines.Auto
                            : new WheelScrollLines(int.Parse(newValue, CultureInfo.InvariantCulture)));
                        break;
                    case "theme":
                        callbacks.OnThemeChange?.Invoke(newValue);
                        break;
                }
            },
            callbacks.OnCancel ?? (() => { }),
            new SettingsListOptions { EnableSearch = true });

        AddChild(_settingsList);
        AddChild(new DynamicBorder());
    }

    /// <summary>TS <c>fullscreen-wheel-scroll-lines</c> values: <c>["auto", ...new Set([1, 2, 3, 5, 10,
    /// config.fullscreenWheelScrollLines]) sorted numerically]</c> — the configured value stays in the
    /// cycle (upstream #9758).</summary>
    private static string[] WheelScrollValues(WheelScrollLines configured)
    {
        var numbers = new List<double> { 1, 2, 3, 5, 10 };
        if (!configured.IsAuto)
        {
            numbers.Add(configured.Lines);
        }
        return
        [
            "auto",
            .. numbers
                .Distinct()
                .OrderBy(number => number)
                .Select(number => ((int)number).ToString(CultureInfo.InvariantCulture)),
        ];
    }

    public SettingsList GetSettingsList() => _settingsList;
}
