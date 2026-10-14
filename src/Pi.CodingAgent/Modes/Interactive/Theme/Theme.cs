// ============================================================================
// The Theme class — port of the `Theme` class in modes/interactive/theme/theme.ts
// ============================================================================

using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.Tui;
using TuiColors = Pi.Tui.Colors;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>Construction options for <see cref="Theme"/>. TS inline options type.</summary>
public sealed record ThemeOptions
{
    public string? Name { get; init; }

    public string? SourcePath { get; init; }

    public SourceInfo? SourceInfo { get; init; }

    /// <summary>The background the theme is designed for; detected from its colors when omitted.</summary>
    public string? Appearance { get; init; }

    /// <summary>Foreground tokens to render faint (SGR 2).</summary>
    public IReadOnlyList<string>? Dim { get; init; }
}

/// <summary>
/// A theme: precomputed ANSI escape sequences per token plus the concrete colors behind them.
/// Port of the TS <c>Theme</c> class.
/// </summary>
/// <remarks>
/// Divergence C195: TS models <c>ThemeStyle.fg</c>/<c>bg</c> as <c>ThemeColor | Color</c> unions. The port
/// splits each into a token field and a color field, because a C# type cannot be "a string or a Color".
/// </remarks>
public sealed class Theme
{
    private readonly TerminalColorMode _mode;

    // Precomputed escape sequences keep fg()/bg() on the render hot path to a lookup and concat.
    private readonly Dictionary<string, string> _fgAnsi = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _bgAnsi = new(StringComparer.Ordinal);

    // Tokens set to "" have no color of their own; `colors` fills them from the terminal defaults.
    private readonly Dictionary<string, Color> _concreteColors = new(StringComparer.Ordinal);
    private readonly List<string> _defaultForegroundTokens = [];
    private readonly List<string> _defaultBackgroundTokens = [];

    // Foreground tokens rendered faint (SGR 2) on top of their color.
    private readonly HashSet<string> _dimTokens;
    private readonly string? _ownAppearance;
    private (TerminalColorsSnapshot Terminal, IReadOnlyDictionary<string, Color> Colors)? _resolvedColors;

    public Theme(
        IReadOnlyDictionary<string, ThemeJsonColorValue> fgColors,
        IReadOnlyDictionary<string, ThemeJsonColorValue> bgColors,
        TerminalColorMode mode,
        ThemeOptions? options = null)
    {
        options ??= new ThemeOptions();
        Name = options.Name;
        SourcePath = options.SourcePath;
        SourceInfo = options.SourceInfo;
        _mode = mode;
        _dimTokens = new HashSet<string>(options.Dim ?? [], StringComparer.Ordinal);

        var foregrounds = new Dictionary<string, ThemeJsonColorValue>(fgColors, StringComparer.Ordinal)
        {
            [ThemeColors.ScrollbarTrack] = fgColors.TryGetValue(ThemeColors.ScrollbarTrack, out var track)
                ? track
                : fgColors[ThemeColors.Muted],
            [ThemeColors.ScrollbarThumb] = fgColors.TryGetValue(ThemeColors.ScrollbarThumb, out var thumb)
                ? thumb
                : fgColors[ThemeColors.Text],
            [ThemeColors.ThinkingMax] = fgColors.TryGetValue(ThemeColors.ThinkingMax, out var thinkingMax)
                ? thinkingMax
                : fgColors[ThemeColors.ThinkingXhigh],
            [ThemeColors.SearchMatchText] = fgColors.TryGetValue(ThemeColors.SearchMatchText, out var searchMatchText)
                ? searchMatchText
                : fgColors[ThemeColors.Text],
        };
        var backgrounds = new Dictionary<string, ThemeJsonColorValue>(bgColors, StringComparer.Ordinal)
        {
            [ThemeBackgrounds.SearchMatchBg] = bgColors.TryGetValue(ThemeBackgrounds.SearchMatchBg, out var searchMatchBg)
                ? searchMatchBg
                : bgColors[ThemeBackgrounds.SelectedBg],
        };

        var concreteForegrounds = new List<Color>();
        var concreteBackgrounds = new List<Color>();

        // Returns the escape sequence for the token's own slot.
        string AddToken(string token, ThemeJsonColorValue value, bool isBackground)
        {
            if (value.IsText && value.Text!.Length == 0)
            {
                (isBackground ? _defaultBackgroundTokens : _defaultForegroundTokens).Add(token);
                return isBackground ? "\x1b[49m" : "\x1b[39m";
            }
            var color = value.IsText ? TuiColors.ParseColor(value.Text!) : TuiColors.ParseColor(value.Index!.Value);
            _concreteColors[token] = color;
            (isBackground ? concreteBackgrounds : concreteForegrounds).Add(color);
            return isBackground ? TuiColors.BackgroundAnsi(color, _mode) : TuiColors.ForegroundAnsi(color, _mode);
        }

        foreach (var (token, value) in foregrounds)
        {
            _fgAnsi[token] = AddToken(token, value, false);
        }
        foreach (var (token, value) in backgrounds)
        {
            _bgAnsi[token] = AddToken(token, value, true);
        }
        _ownAppearance = options.Appearance ?? ThemeRuntime.DetectAppearance(concreteForegrounds, concreteBackgrounds);
    }

    public string? Name { get; }

    public string? SourcePath { get; }

    /// <summary>Set by the resource loader so a theme can report where it came from.</summary>
    public SourceInfo? SourceInfo { get; set; }

    /// <summary>
    /// The background the theme is designed for: declared in the theme JSON, detected from its colors,
    /// or, for themes without usable colors, the terminal's appearance.
    /// </summary>
    public string Appearance => _ownAppearance ?? ThemeRuntime.GetTerminalTheme();

    /// <summary>
    /// Concrete colors for all tokens. Tokens set to <c>""</c> (terminal default) use the terminal's
    /// reported default colors, or a guess based on <see cref="Appearance"/> when the terminal did not
    /// report them. Faint tokens are approximated by mixing their color toward the background.
    /// </summary>
    public IReadOnlyDictionary<string, Color> Colors
    {
        get
        {
            var terminal = ThemeRuntime.TerminalColors;
            if (_resolvedColors is null || !ReferenceEquals(_resolvedColors.Value.Terminal, terminal))
            {
                var guess = ThemeRuntime.GuessedDefaultColors(Appearance);
                var foreground = ToColor(terminal.Foreground, guess.Foreground);
                var background = ToColor(terminal.Background, guess.Background);
                var colors = new Dictionary<string, Color>(_concreteColors, StringComparer.Ordinal);
                foreach (var token in _defaultForegroundTokens)
                {
                    colors[token] = foreground;
                }
                foreach (var token in _defaultBackgroundTokens)
                {
                    colors[token] = background;
                }
                foreach (var token in _dimTokens)
                {
                    if (colors.TryGetValue(token, out var color))
                    {
                        colors[token] = TuiColors.MixColors(color, background, 0.4);
                    }
                }
                _resolvedColors = (terminal, colors);
            }
            return _resolvedColors.Value.Colors;

            static Color ToColor(RgbColor? rgb, Color fallback) =>
                rgb is { } value ? TuiColors.Rgb(value.R, value.G, value.B) : fallback;
        }
    }

    public string Style(string text, ThemeStyle options)
    {
        var dim = options.Dim;
        if (options.FgToken is { } fgToken && _dimTokens.Contains(fgToken))
        {
            dim = true;
        }
        var fgAnsi = options.FgToken is { } token
            ? TokenAnsi(_fgAnsi, token)
            : options.Fg is { } fgColor
                ? TuiColors.ForegroundAnsi(fgColor, _mode)
                : null;
        var bgAnsi = options.BgToken is { } backgroundToken
            ? TokenAnsi(_bgAnsi, backgroundToken)
            : options.Bg is { } bgColor
                ? TuiColors.BackgroundAnsi(bgColor, _mode)
                : null;
        return TuiColors.StyleTextWithAnsi(text, fgAnsi, bgAnsi, options with { Dim = dim });
    }

    public string Fg(string color, string text)
    {
        var ansi = TokenAnsi(_fgAnsi, color);
        if (_dimTokens.Contains(color))
        {
            return $"{ansi}\x1b[2m{text}\x1b[22;39m";
        }
        return $"{ansi}{text}\x1b[39m";
    }

    public string Bg(string color, string text)
    {
        var ansi = TokenAnsi(_bgAnsi, color);
        return $"{ansi}{text}\x1b[49m";
    }

    private static string TokenAnsi(IReadOnlyDictionary<string, string> ansi, string token)
    {
        if (!ansi.TryGetValue(token, out var value))
        {
            throw new InvalidOperationException($"Unknown theme color: {token}");
        }
        return value;
    }

    public string Bold(string text) => Utils.Chalk.Bold(text);

    public string Italic(string text) => Utils.Chalk.Italic(text);

    public string Underline(string text) => Utils.Chalk.Underline(text);

    public string Inverse(string text) => Utils.Chalk.Inverse(text);

    public string Strikethrough(string text) => Utils.Chalk.Strikethrough(text);

    /// <summary>Opening escape sequence for a foreground token. Faint tokens include SGR 2, which <c>\x1b[22m</c> closes.</summary>
    public string GetFgAnsi(string color)
    {
        var ansi = TokenAnsi(_fgAnsi, color);
        return _dimTokens.Contains(color) ? $"{ansi}\x1b[2m" : ansi;
    }

    public string GetBgAnsi(string color) => TokenAnsi(_bgAnsi, color);

    public TerminalColorMode GetColorMode() => _mode;

    public Func<string, string> GetThinkingBorderColor(ThinkingLevel level) => level switch
    {
        // Map thinking levels to dedicated theme colors.
        ThinkingLevel.Off => str => Fg(ThemeColors.ThinkingOff, str),
        ThinkingLevel.Minimal => str => Fg(ThemeColors.ThinkingMinimal, str),
        ThinkingLevel.Low => str => Fg(ThemeColors.ThinkingLow, str),
        ThinkingLevel.Medium => str => Fg(ThemeColors.ThinkingMedium, str),
        ThinkingLevel.High => str => Fg(ThemeColors.ThinkingHigh, str),
        ThinkingLevel.XHigh => str => Fg(ThemeColors.ThinkingXhigh, str),
        ThinkingLevel.Max => str => Fg(ThemeColors.ThinkingMax, str),
        _ => str => Fg(ThemeColors.ThinkingOff, str),
    };

    public Func<string, string> GetBashModeBorderColor() => str => Fg(ThemeColors.BashMode, str);
}

/// <summary>
/// A theme style: text attributes plus a foreground and a background, each either a token or a direct
/// color. TS <c>ThemeStyle</c>.
/// </summary>
public sealed record ThemeStyle : TextAttributes
{
    /// <summary>A foreground token, which takes precedence over <see cref="Fg"/>.</summary>
    public string? FgToken { get; init; }

    /// <summary>A direct foreground color, used when <see cref="FgToken"/> is not set.</summary>
    public Color? Fg { get; init; }

    /// <summary>A background token, which takes precedence over <see cref="Bg"/>.</summary>
    public string? BgToken { get; init; }

    /// <summary>A direct background color, used when <see cref="BgToken"/> is not set.</summary>
    public Color? Bg { get; init; }
}
