// ============================================================================
// Theme loading, the global theme registry and terminal appearance — port of the
// module-level half of modes/interactive/theme/theme.ts
// ============================================================================

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>A theme on disk (or in memory). TS <c>ThemeInfo</c>.</summary>
/// <param name="Name">The theme name.</param>
/// <param name="Path">The file it was loaded from; <see langword="null"/> for the generated system theme.</param>
public sealed record ThemeInfo(string Name, string? Path);

/// <summary>Result of installing a theme. TS <c>{ success: boolean; error?: string }</c>.</summary>
public sealed record SetThemeResult(bool Success, string? Error = null);

/// <summary>
/// Test seams for the theme module. Production callers pass nothing and get the real behaviour; the
/// seam-taking overloads are <see langword="internal"/> so the public surface stays TS-identical.
/// </summary>
internal sealed record ThemeSeams
{
    /// <summary>Replacement for <see cref="Config.GetCustomThemesDir()"/>.</summary>
    public string? CustomThemesDir { get; init; }

    /// <summary>Replacement for the terminal color-mode probe.</summary>
    public TerminalColorMode? ColorMode { get; init; }

    /// <summary>Replacement for the theme-file watcher, so tests never touch a real one.</summary>
    public Func<string, Action<string, string?>, Action, IDisposable?>? Watch { get; init; }
}

/// <summary>Port of the module-level half of <c>theme.ts</c>.</summary>
public static partial class ThemeRuntime
{
    // ------------------------------------------------------------------
    // Appearance & terminal default colors
    // ------------------------------------------------------------------

    // The terminal's reported colors. Replaced (never mutated) on update, so themes can cache resolved
    // colors by identity.
    private static TerminalColorsSnapshot _terminalColors = new();

    // While the terminal color query is in flight, the system theme renders in grayscale.
    private static bool _terminalColorsPending;

    // The terminal's last light/dark report (mode 2031). Only used while it has not reported a background.
    private static string? _terminalColorScheme;

    /// <summary>The terminal's reported colors, as most recently recorded.</summary>
    internal static TerminalColorsSnapshot TerminalColors => _terminalColors;

    /// <summary>
    /// Record the terminal's reported colors. Themes use the default colors for tokens set to <c>""</c>
    /// (terminal default); the system theme is generated from all of them. Ends the pending state.
    /// </summary>
    public static void SetTerminalColors(TerminalColorsSnapshot colors)
    {
        // A fresh instance each time: `Theme.colors` keys its cache off this object's identity.
        _terminalColors = new TerminalColorsSnapshot(colors.Foreground, colors.Background, colors.Palette);
        _terminalColorsPending = false;
    }

    /// <summary>Record the terminal's light/dark report, the fallback for terminals that do not report their background.</summary>
    public static void SetTerminalColorScheme(string? scheme) => _terminalColorScheme = scheme;

    /// <summary>Render the system theme in grayscale until <see cref="SetTerminalColors"/> reports the terminal's colors.</summary>
    public static void MarkTerminalColorsPending() => _terminalColorsPending = true;

    /// <summary>Assumed terminal default colors when the terminal does not report them.</summary>
    internal static (Color Foreground, Color Background) GuessedDefaultColors(string appearance) =>
        appearance == ThemeAppearance.Dark
            ? (Colors.ParseColor("#e5e5e7"), Colors.ParseColor("#000000"))
            : (Colors.ParseColor("#000000"), Colors.ParseColor("#ffffff"));

    private static double? AverageLightness(IReadOnlyList<Color> colors)
    {
        // Palette colors 0-15 follow the user's terminal palette, so they say nothing about the theme.
        var fixedColors = colors.Where(color => color is not Color.Indexed indexed || indexed.Index >= 16).ToList();
        if (fixedColors.Count == 0)
        {
            return null;
        }
        return fixedColors.Sum(color => Colors.ColorToOklch(color).L) / fixedColors.Count;
    }

    /// <summary>Detect the background a theme is designed for from the lightness of its own colors.</summary>
    internal static string? DetectAppearance(IReadOnlyList<Color> foregrounds, IReadOnlyList<Color> backgrounds)
    {
        var fg = AverageLightness(foregrounds);
        var bg = AverageLightness(backgrounds);
        if (fg is not null && bg is not null)
        {
            return bg < fg ? ThemeAppearance.Dark : ThemeAppearance.Light;
        }
        if (bg is not null)
        {
            return bg < 0.5 ? ThemeAppearance.Dark : ThemeAppearance.Light;
        }
        if (fg is not null)
        {
            return fg > 0.5 ? ThemeAppearance.Dark : ThemeAppearance.Light;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Color utilities
    // ------------------------------------------------------------------

    [GeneratedRegex(@"\Aok(lch|hsl)\(", RegexOptions.IgnoreCase)]
    private static partial Regex OkLchOrOkHsl();

    [GeneratedRegex(@"\Aokhsl\(", RegexOptions.IgnoreCase)]
    private static partial Regex OkHslPrefix();

    [GeneratedRegex(@"\A[0-9]{1,2}\z")]
    private static partial Regex ColorFgBgIndex();

    private static ThemeJsonColorValue ResolveVarRefs(
        ThemeJsonColorValue value,
        IReadOnlyDictionary<string, ThemeJsonColorValue> vars,
        HashSet<string>? visited = null)
    {
        if (!value.IsText)
        {
            return value;
        }
        var text = value.Text!;
        if (text.Length == 0 || text.StartsWith('#') || OkLchOrOkHsl().IsMatch(text))
        {
            return value;
        }
        visited ??= new HashSet<string>(StringComparer.Ordinal);
        if (visited.Contains(text))
        {
            throw new InvalidOperationException($"Circular variable reference detected: {text}");
        }
        if (!vars.TryGetValue(text, out var referenced))
        {
            throw new InvalidOperationException($"Variable reference not found: {text}");
        }
        visited.Add(text);
        return ResolveVarRefs(referenced, vars, visited);
    }

    private static Dictionary<string, ThemeJsonColorValue> ResolveThemeColors(
        IReadOnlyDictionary<string, ThemeJsonColorValue> colors,
        IReadOnlyDictionary<string, ThemeJsonColorValue>? vars = null)
    {
        vars ??= new Dictionary<string, ThemeJsonColorValue>(StringComparer.Ordinal);
        var resolved = new Dictionary<string, ThemeJsonColorValue>(StringComparer.Ordinal);
        foreach (var (key, value) in colors)
        {
            resolved[key] = ResolveVarRefs(value, vars);
        }
        return resolved;
    }

    /// <summary>Fill the optional tokens from the tokens they fall back to. TS <c>withThemeColorFallbacks</c>.</summary>
    private static Dictionary<string, ThemeJsonColorValue> WithThemeColorFallbacks(ValidatedThemeJson themeJson)
    {
        var colors = new Dictionary<string, ThemeJsonColorValue>(themeJson.Colors, StringComparer.Ordinal);
        ThemeJsonColorValue Fallback(string token, string from) =>
            colors.TryGetValue(token, out var value) ? value : colors[from];

        // Every fallback is read before any is written, so none can see a half-filled map.
        var scrollbarTrack = Fallback(ThemeColors.ScrollbarTrack, ThemeColors.Muted);
        var scrollbarThumb = Fallback(ThemeColors.ScrollbarThumb, ThemeColors.Text);
        var thinkingMax = Fallback(ThemeColors.ThinkingMax, ThemeColors.ThinkingXhigh);
        var searchMatchBg = Fallback(ThemeBackgrounds.SearchMatchBg, ThemeBackgrounds.SelectedBg);
        var searchMatchText = Fallback(ThemeColors.SearchMatchText, ThemeColors.Text);

        return new Dictionary<string, ThemeJsonColorValue>(colors, StringComparer.Ordinal)
        {
            [ThemeColors.ScrollbarTrack] = scrollbarTrack,
            [ThemeColors.ScrollbarThumb] = scrollbarThumb,
            [ThemeColors.ThinkingMax] = thinkingMax,
            [ThemeBackgrounds.SearchMatchBg] = searchMatchBg,
            [ThemeColors.SearchMatchText] = searchMatchText,
        };
    }

    /// <summary>Split resolved colors into the foreground and background slots. TS <c>splitThemeColors</c>.</summary>
    private static (Dictionary<string, ThemeJsonColorValue> Fg, Dictionary<string, ThemeJsonColorValue> Bg)
        SplitThemeColors(IReadOnlyDictionary<string, ThemeJsonColorValue> colors)
    {
        var fg = new Dictionary<string, ThemeJsonColorValue>(StringComparer.Ordinal);
        var bg = new Dictionary<string, ThemeJsonColorValue>(StringComparer.Ordinal);
        foreach (var (key, value) in colors)
        {
            if (ThemeTokens.IsBackground(key))
            {
                bg[key] = value;
            }
            else
            {
                fg[key] = value;
            }
        }
        return (fg, bg);
    }

    // ------------------------------------------------------------------
    // Theme JSON validation
    // ------------------------------------------------------------------

    private static ThemeJsonValidator? _themeJsonValidator;

    /// <summary>
    /// Install full theme validation. Without it, documents are accepted as-is, which is what built-in
    /// themes already do: validating user-authored JSON is a separate concern, and a presentation that
    /// only uses built-in themes should not pay for it.
    /// </summary>
    public static void SetThemeJsonValidator(ThemeJsonValidator validator) => _themeJsonValidator = validator;

    private static ValidatedThemeJson ParseThemeJson(string label, JsonNode? json)
    {
        if (_themeJsonValidator is { } validator)
        {
            return validator(label, json);
        }
        if (json is not JsonObject document || !document.ContainsKey("colors"))
        {
            throw new InvalidOperationException($"Invalid theme \"{label}\": expected an object with a \"colors\" map.");
        }
        return ThemeJsonReader.Read(document);
    }

    private static ValidatedThemeJson ParseThemeJsonContent(string label, string content)
    {
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(Text.StripBom(content));
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Failed to parse theme {label}: {error.Message}");
        }
        return ParseThemeJson(label, json);
    }

    // ------------------------------------------------------------------
    // Theme loading
    // ------------------------------------------------------------------

    private static IReadOnlyDictionary<string, ValidatedThemeJson>? _builtinThemes;

    private static IReadOnlyDictionary<string, ValidatedThemeJson> GetBuiltinThemes()
    {
        if (_builtinThemes is null)
        {
            var themesDir = Config.GetThemesDir();
            _builtinThemes = new Dictionary<string, ValidatedThemeJson>(StringComparer.Ordinal)
            {
                ["dark"] = ReadBuiltinTheme(Path.Combine(themesDir, "dark.json")),
                ["light"] = ReadBuiltinTheme(Path.Combine(themesDir, "light.json")),
            };
        }
        return _builtinThemes;
    }

    /// <summary>Built-in themes are shipped, not authored, so they skip validation. TS casts here.</summary>
    private static ValidatedThemeJson ReadBuiltinTheme(string path) =>
        ThemeJsonReader.Read(JsonNode.Parse(Text.StripBom(File.ReadAllText(path))));

    /// <summary>The names of every available theme, system theme first.</summary>
    public static IReadOnlyList<string> GetAvailableThemes() =>
        GetAvailableThemesWithPaths().Select(info => info.Name).ToList();

    /// <summary>Every available theme with the file it comes from, system theme first.</summary>
    public static IReadOnlyList<ThemeInfo> GetAvailableThemesWithPaths() => GetAvailableThemesWithPaths(null);

    internal static IReadOnlyList<ThemeInfo> GetAvailableThemesWithPaths(ThemeSeams? seams)
    {
        var themesDir = Config.GetThemesDir();
        var result = new List<ThemeInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddTheme(ThemeInfo themeInfo)
        {
            if (seen.Add(themeInfo.Name))
            {
                result.Add(themeInfo);
            }
        }

        // Built-in themes. The system theme is generated, so it has no file.
        AddTheme(new ThemeInfo(SystemTheme.SystemThemeName, null));
        foreach (var name in GetBuiltinThemes().Keys)
        {
            AddTheme(new ThemeInfo(name, Path.Combine(themesDir, $"{name}.json")));
        }

        foreach (var themeInfo in GetCustomThemeInfos(seams))
        {
            AddTheme(themeInfo);
        }

        foreach (var (name, theme) in RegisteredThemes)
        {
            AddTheme(new ThemeInfo(name, theme.SourcePath));
        }

        // The system theme comes first: it is the default and adapts to every terminal. `OrderBy` is
        // stable, matching `Array.prototype.sort`; the culture comparer matches `localeCompare` (C79).
        return result
            .OrderBy(info => info.Name == SystemTheme.SystemThemeName ? 0 : 1)
            .ThenBy(info => info.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static IReadOnlyList<ThemeInfo> GetCustomThemeInfos(ThemeSeams? seams)
    {
        var customThemesDir = CustomThemesDir(seams);
        var result = new List<ThemeInfo>();
        if (!Directory.Exists(customThemesDir))
        {
            return result;
        }

        foreach (var file in Directory.GetFiles(customThemesDir))
        {
            if (!file.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }
            try
            {
                var customTheme = LoadThemeFromPath(file, null, seams);
                if (!string.IsNullOrEmpty(customTheme.Name))
                {
                    result.Add(new ThemeInfo(customTheme.Name, file));
                }
            }
            catch
            {
                // Invalid themes are ignored here; the resource loader reports them
                // during normal startup/reload.
            }
        }
        return result;
    }

    private static void AssertThemeNameIsValid(string name)
    {
        if (name.Contains('/'))
        {
            throw new InvalidOperationException(
                $"Invalid theme name \"{name}\": theme names cannot contain \"/\" because it is "
                + "reserved for automatic light/dark theme settings.");
        }
    }

    private static ValidatedThemeJson LoadThemeJson(string name)
    {
        var builtinThemes = GetBuiltinThemes();
        if (builtinThemes.TryGetValue(name, out var builtin))
        {
            return builtin;
        }
        if (RegisteredThemes.TryGetValue(name, out var registeredTheme) && registeredTheme.SourcePath is { } sourcePath)
        {
            return ParseThemeJsonContent(sourcePath, File.ReadAllText(sourcePath));
        }
        if (registeredTheme is not null)
        {
            throw new InvalidOperationException($"Theme \"{name}\" does not have a source path for export");
        }
        var themePath = Path.Combine(Config.GetCustomThemesDir(), $"{name}.json");
        if (!File.Exists(themePath))
        {
            throw new InvalidOperationException($"Theme not found: {name}");
        }
        return ParseThemeJsonContent(name, File.ReadAllText(themePath));
    }

    private static Theme CreateTheme(
        ValidatedThemeJson themeJson,
        TerminalColorMode? mode = null,
        string? sourcePath = null,
        ThemeSeams? seams = null)
    {
        var colorMode = mode ?? seams?.ColorMode ?? TerminalImage.GetTerminalColorMode();
        var resolvedColors = ResolveThemeColors(WithThemeColorFallbacks(themeJson), themeJson.Vars);
        var (fgColors, bgColors) = SplitThemeColors(resolvedColors);
        return new Theme(fgColors, bgColors, colorMode, new ThemeOptions
        {
            Name = themeJson.Name,
            SourcePath = sourcePath,
            Appearance = themeJson.Appearance,
        });
    }

    /// <summary>Generate the system theme from the terminal's reported colors (grayscale while they are pending).</summary>
    private static Theme CreateSystemTheme(TerminalColorMode? mode = null, ThemeSeams? seams = null)
    {
        var generated = SystemTheme.GenerateColors(new SystemThemeInput
        {
            Foreground = _terminalColors.Foreground,
            Background = _terminalColors.Background,
            Palette = _terminalColors.Palette,
            Saturation = _terminalColorsPending ? 0 : 1,
            AppearanceHint = GetTerminalTheme(),
        });
        var (fgColors, bgColors) = SplitThemeColors(generated.Colors);
        return new Theme(
            fgColors,
            bgColors,
            mode ?? seams?.ColorMode ?? TerminalImage.GetTerminalColorMode(),
            new ThemeOptions
            {
                Name = SystemTheme.SystemThemeName,
                Appearance = generated.Appearance,
                Dim = generated.Dim,
            });
    }

    /// <summary>Load a theme from an explicit file path, skipping the registry.</summary>
    public static Theme LoadThemeFromPath(string themePath, TerminalColorMode? mode = null) =>
        LoadThemeFromPath(themePath, mode, null);

    internal static Theme LoadThemeFromPath(string themePath, TerminalColorMode? mode, ThemeSeams? seams)
    {
        var content = File.ReadAllText(themePath);
        var themeJson = ParseThemeJsonContent(themePath, content);
        return CreateTheme(themeJson, mode, themePath, seams);
    }

    private static Theme LoadTheme(string name, TerminalColorMode? mode = null, ThemeSeams? seams = null)
    {
        // The system theme name is reserved: it takes precedence over custom themes of the same name.
        if (name == SystemTheme.SystemThemeName)
        {
            return CreateSystemTheme(mode, seams);
        }
        if (RegisteredThemes.TryGetValue(name, out var registeredTheme))
        {
            return registeredTheme;
        }
        var themeJson = LoadThemeJson(name);
        return CreateTheme(themeJson, mode, null, seams);
    }

    /// <summary>Load a theme by name, or <see langword="null"/> when it cannot be loaded.</summary>
    public static Theme? GetThemeByName(string name) => GetThemeByName(name, null);

    internal static Theme? GetThemeByName(string name, ThemeSeams? seams)
    {
        try
        {
            return LoadTheme(name, null, seams);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Global theme instance
    // ------------------------------------------------------------------

    private static Theme? _current;
    private static string? _currentThemeName;
    private static IDisposable? _themeWatcher;
    private static Timer? _themeReloadTimer;
    private static Action? _onThemeChangeCallback;

    private static readonly Dictionary<string, Theme> RegisteredThemeMap = new(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, Theme> RegisteredThemes => RegisteredThemeMap;

    /// <summary>
    /// The process-wide current theme. TS exposes a <c>Proxy</c> reading a <c>globalThis</c> symbol so
    /// every module loader (node, jiti) sees one theme; a .NET process has a single module instance, so
    /// a static field is equivalent (divergence C196).
    /// </summary>
    /// <exception cref="InvalidOperationException">No theme has been installed yet.</exception>
    public static Theme Current =>
        _current ?? throw new InvalidOperationException("Theme not initialized. Call initTheme() first.");

    /// <summary>Whether a theme has been installed, without throwing.</summary>
    public static bool HasCurrent => _current is not null;

    /// <summary>The name of the theme currently installed, or <see langword="null"/>.</summary>
    public static string? CurrentThemeName => _currentThemeName;

    /// <summary>Register in-memory themes, replacing any previously registered set.</summary>
    public static void SetRegisteredThemes(IReadOnlyList<Theme> themes)
    {
        RegisteredThemeMap.Clear();
        foreach (var theme in themes)
        {
            if (!string.IsNullOrEmpty(theme.Name))
            {
                AssertThemeNameIsValid(theme.Name);
                RegisteredThemeMap[theme.Name] = theme;
            }
        }
    }

    /// <summary>Install a theme by name, falling back to the system theme when it cannot be loaded.</summary>
    public static void InitTheme(string? themeName = null, bool enableWatcher = false) =>
        InitTheme(themeName, enableWatcher, null);

    internal static void InitTheme(string? themeName, bool enableWatcher, ThemeSeams? seams)
    {
        var name = themeName ?? SystemTheme.SystemThemeName;
        _currentThemeName = name;
        try
        {
            SetGlobalTheme(LoadTheme(name, null, seams));
            if (enableWatcher)
            {
                StartThemeWatcher(seams);
            }
        }
        catch
        {
            // Theme is invalid - fall back to the system theme silently.
            _currentThemeName = SystemTheme.SystemThemeName;
            SetGlobalTheme(LoadTheme(SystemTheme.SystemThemeName, null, seams));
            // Don't start watcher for fallback theme.
        }
    }

    /// <summary>Switch the current theme, reporting why it failed instead of throwing.</summary>
    public static SetThemeResult SetTheme(string name, bool enableWatcher = false) =>
        SetTheme(name, enableWatcher, null);

    internal static SetThemeResult SetTheme(string name, bool enableWatcher, ThemeSeams? seams)
    {
        _currentThemeName = name;
        try
        {
            SetGlobalTheme(LoadTheme(name, null, seams));
            if (enableWatcher)
            {
                StartThemeWatcher(seams);
            }
            _onThemeChangeCallback?.Invoke();
            return new SetThemeResult(true);
        }
        catch (Exception error)
        {
            // Theme is invalid - fall back to the system theme.
            _currentThemeName = SystemTheme.SystemThemeName;
            SetGlobalTheme(LoadTheme(SystemTheme.SystemThemeName, null, seams));
            // Don't start watcher for fallback theme.
            return new SetThemeResult(false, error.Message);
        }
    }

    /// <summary>Install a theme instance directly, bypassing name lookup.</summary>
    public static void SetThemeInstance(Theme themeInstance)
    {
        SetGlobalTheme(themeInstance);
        _currentThemeName = "<in-memory>";
        StopThemeWatcher(); // Can't watch a direct instance.
        _onThemeChangeCallback?.Invoke();
    }

    /// <summary>Install the single change listener. TS keeps one callback, not a set.</summary>
    public static void OnThemeChange(Action callback) => _onThemeChangeCallback = callback;

    private static void SetGlobalTheme(Theme theme) => _current = theme;

    private static void StartThemeWatcher(ThemeSeams? seams)
    {
        StopThemeWatcher();

        // Only watch if it's a custom theme (not built-in).
        if (_currentThemeName is null or "dark" or "light" || _currentThemeName == SystemTheme.SystemThemeName)
        {
            return;
        }

        var customThemesDir = CustomThemesDir(seams);
        var watchedThemeName = _currentThemeName;
        var watchedFileName = $"{watchedThemeName}.json";
        var themeFile = Path.Combine(customThemesDir, watchedFileName);

        // Only watch if the file exists.
        if (!File.Exists(themeFile))
        {
            return;
        }

        void ScheduleReload()
        {
            _themeReloadTimer?.Dispose();
            _themeReloadTimer = new Timer(
                _ =>
                {
                    _themeReloadTimer = null;

                    // Ignore stale timers after switching themes or stopping the watcher.
                    if (_currentThemeName != watchedThemeName)
                    {
                        return;
                    }

                    // Keep the last successfully loaded theme active if the file is temporarily missing.
                    if (!File.Exists(themeFile))
                    {
                        return;
                    }

                    try
                    {
                        // Reload the theme from disk and refresh the registry cache.
                        var reloadedTheme = LoadThemeFromPath(themeFile, null, seams);
                        RegisteredThemeMap[watchedThemeName] = reloadedTheme;
                        SetGlobalTheme(reloadedTheme);
                        _onThemeChangeCallback?.Invoke();
                    }
                    catch
                    {
                        // Ignore errors (file might be in invalid state while being edited).
                    }
                },
                null,
                100,
                Timeout.Infinite);
        }

        var watch = seams?.Watch
                    ?? ((path, listener, onError) => FsWatch.WatchWithErrorHandler(path, listener, onError));
        _themeWatcher = watch(
            customThemesDir,
            (_, filename) =>
            {
                if (_currentThemeName != watchedThemeName)
                {
                    return;
                }
                if (filename is null)
                {
                    ScheduleReload();
                    return;
                }
                if (filename != watchedFileName)
                {
                    return;
                }
                ScheduleReload();
            },
            () =>
            {
                _themeWatcher?.Dispose();
                _themeWatcher = null;
            });
    }

    /// <summary>Stop watching the current theme file and cancel any pending reload.</summary>
    public static void StopThemeWatcher()
    {
        _themeReloadTimer?.Dispose();
        _themeReloadTimer = null;
        _themeWatcher?.Dispose();
        _themeWatcher = null;
    }

    private static string CustomThemesDir(ThemeSeams? seams) =>
        seams?.CustomThemesDir ?? Config.GetCustomThemesDir();

    // ------------------------------------------------------------------
    // Theme setting resolution
    // ------------------------------------------------------------------

    /// <summary>
    /// Parse an automatic light/dark theme setting (<c>light/dark</c>), or <see langword="null"/> when the
    /// setting is not one.
    /// </summary>
    public static (string LightTheme, string DarkTheme)? ParseAutoThemeSetting(string? themeSetting)
    {
        if (string.IsNullOrEmpty(themeSetting))
        {
            return null;
        }
        var slashIndex = themeSetting.IndexOf('/');
        if (slashIndex == -1 || themeSetting.IndexOf('/', slashIndex + 1) != -1)
        {
            return null;
        }

        var lightTheme = themeSetting[..slashIndex].Trim();
        var darkTheme = themeSetting[(slashIndex + 1)..].Trim();
        if (lightTheme.Length == 0 || darkTheme.Length == 0)
        {
            return null;
        }
        return (lightTheme, darkTheme);
    }

    /// <summary>Resolve a theme setting against the terminal's appearance.</summary>
    public static string? ResolveThemeSetting(string? themeSetting, string terminalTheme)
    {
        if (ParseAutoThemeSetting(themeSetting) is { } autoTheme)
        {
            return terminalTheme == ThemeAppearance.Light ? autoTheme.LightTheme : autoTheme.DarkTheme;
        }
        if (themeSetting?.Contains('/') ?? false)
        {
            return null;
        }
        return themeSetting;
    }

    /// <summary>
    /// Dark or light from the <c>COLORFGBG</c> environment variable some terminals set, or
    /// <see langword="null"/> without a usable background index.
    /// </summary>
    /// <remarks>
    /// The value is <c>fg;bg</c> or <c>fg;xpm;bg</c> (rxvt), where a field is an ANSI color index or
    /// <c>default</c> when the color is not in the palette. The index refers to the terminal's own palette,
    /// whose colors are unknown here, so it is classified by index like Vim does: 0-6 and 8 (bright black,
    /// e.g. Solarized Dark's background) are dark, 7 and 9-15 are light.
    /// </remarks>
    public static string? DetectColorFgBgTheme(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var background = env("COLORFGBG")?.Split(';').LastOrDefault()?.Trim();
        if (string.IsNullOrEmpty(background) || !ColorFgBgIndex().IsMatch(background))
        {
            return null;
        }
        var index = int.Parse(background, System.Globalization.CultureInfo.InvariantCulture);
        if (index > 15)
        {
            return null;
        }
        return index <= 6 || index == 8 ? ThemeAppearance.Dark : ThemeAppearance.Light;
    }

    /// <summary>
    /// Whether the terminal is dark or light. The background it renders decides, classified the same way
    /// the system theme does. Without a reported background: the terminal's light/dark report, then
    /// <c>COLORFGBG</c>, then dark.
    /// </summary>
    public static string DetectTerminalTheme(
        TerminalColorsSnapshot? colors = null,
        string? reportedScheme = null,
        Func<string, string?>? env = null)
    {
        colors ??= new TerminalColorsSnapshot();
        if (colors.Background is { } background)
        {
            return SystemTheme.TerminalAppearance(background, colors.Foreground);
        }
        return reportedScheme ?? DetectColorFgBgTheme(env) ?? ThemeAppearance.Dark;
    }

    /// <summary>Whether the terminal is dark or light, from everything it reported so far.</summary>
    public static string GetTerminalTheme() => DetectTerminalTheme(_terminalColors, _terminalColorScheme);

    // ------------------------------------------------------------------
    // HTML export helpers
    // ------------------------------------------------------------------

    /// <summary>Resolved theme colors as CSS-compatible hex strings, for HTML export.</summary>
    public static IReadOnlyDictionary<string, string> GetResolvedThemeColors(string? themeName = null) =>
        GetResolvedThemeColors(themeName, null);

    internal static IReadOnlyDictionary<string, string> GetResolvedThemeColors(string? themeName, ThemeSeams? seams)
    {
        var colors = LoadTheme(themeName ?? _currentThemeName ?? SystemTheme.SystemThemeName, null, seams).Colors;
        return colors.ToDictionary(pair => pair.Key, pair => Colors.ColorToHex(pair.Value), StringComparer.Ordinal);
    }

    /// <summary>Whether a theme is a light theme (for CSS that needs light/dark variants).</summary>
    public static bool IsLightTheme(string? themeName = null) => IsLightTheme(themeName, null);

    internal static bool IsLightTheme(string? themeName, ThemeSeams? seams) =>
        LoadTheme(themeName ?? _currentThemeName ?? SystemTheme.SystemThemeName, null, seams).Appearance
        == ThemeAppearance.Light;

    /// <summary>
    /// Explicit export colors from the theme JSON, if specified. Missing entries are
    /// <see langword="null"/>.
    /// </summary>
    public static (string? PageBg, string? CardBg, string? InfoBg) GetThemeExportColors(string? themeName = null) =>
        GetThemeExportColors(themeName, null);

    internal static (string? PageBg, string? CardBg, string? InfoBg) GetThemeExportColors(
        string? themeName,
        ThemeSeams? seams)
    {
        var name = themeName ?? _currentThemeName ?? SystemTheme.SystemThemeName;
        if (name == SystemTheme.SystemThemeName)
        {
            return (null, null, null);
        }
        try
        {
            var themeJson = LoadThemeJson(name);
            if (themeJson.Export is not { } exportSection)
            {
                return (null, null, null);
            }

            var vars = themeJson.Vars ?? new Dictionary<string, ThemeJsonColorValue>(StringComparer.Ordinal);

            // Export colors end up in CSS, which understands hex and oklch() values directly but not okhsl().
            string? Resolve(ThemeJsonColorValue? value)
            {
                if (value is not { } colorValue)
                {
                    return null;
                }
                var resolved = ResolveVarRefs(colorValue, vars);
                if (!resolved.IsText)
                {
                    return Colors.ColorToHex(Colors.Indexed(resolved.Index!.Value));
                }
                var text = resolved.Text!;
                if (text.Length == 0)
                {
                    return null;
                }
                return OkHslPrefix().IsMatch(text) ? Colors.ColorToHex(Colors.ParseColor(text)) : text;
            }

            return (Resolve(exportSection.PageBg), Resolve(exportSection.CardBg), Resolve(exportSection.InfoBg));
        }
        catch
        {
            return (null, null, null);
        }
    }

    /// <summary>
    /// Drop every module-level cache. Not part of TS; the module-level caches are process-lifetime there
    /// and tests here need a clean slate.
    /// </summary>
    internal static void ResetCachesForTests()
    {
        _builtinThemes = null;
        _themeJsonValidator = null;
        RegisteredThemeMap.Clear();
        _current = null;
        _currentThemeName = null;
        _onThemeChangeCallback = null;
        _terminalColors = new TerminalColorsSnapshot();
        _terminalColorsPending = false;
        _terminalColorScheme = null;
        StopThemeWatcher();
    }
}
