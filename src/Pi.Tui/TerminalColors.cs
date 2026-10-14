using System.Globalization;
using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>
/// An RGB color reported by the terminal. Channels are doubles (not bytes) to match the TS source:
/// colors can carry fractional values, e.g. after mixing in sRGB space.
/// </summary>
public readonly record struct RgbColor(double R, double G, double B);

/// <summary>Terminal color-scheme preference.</summary>
public enum TerminalColorScheme
{
    Dark,
    Light,
}

/// <summary>
/// Colors the terminal reports for its current theme. Port of the TS <c>TerminalColors</c> interface;
/// renamed because the port keeps the static <see cref="TerminalColors"/> parser class under that name.
/// </summary>
/// <param name="Foreground">Default foreground (OSC 10).</param>
/// <param name="Background">Default background (OSC 11).</param>
/// <param name="Palette">ANSI colors 0-15 (OSC 4); only set when the terminal reported all 16.</param>
public sealed record TerminalColorsSnapshot(
    RgbColor? Foreground = null,
    RgbColor? Background = null,
    IReadOnlyList<RgbColor>? Palette = null);

/// <summary>Options for <see cref="ITui.QueryTerminalColorsAsync"/>. TS inline options type.</summary>
public sealed record TerminalColorQueryOptions
{
    /// <summary>Query timeout in milliseconds, for terminals that do not answer DA1 either.</summary>
    public required int TimeoutMs { get; init; }

    /// <summary>
    /// Receives the replies if the query completes after the timeout, e.g. over slow links.
    /// </summary>
    public Action<TerminalColorsSnapshot>? OnLateReply { get; init; }
}

/// <summary>
/// What an OSC color reply reports: the default foreground (OSC 10), the default background (OSC 11),
/// or a palette index (OSC 4). Port of the TS <c>OscColorTarget</c> union.
/// </summary>
public abstract record OscColorTarget
{
    private protected OscColorTarget()
    {
    }

    /// <summary>OSC 10: the terminal's default foreground.</summary>
    public sealed record Foreground : OscColorTarget;

    /// <summary>OSC 11: the terminal's default background.</summary>
    public sealed record Background : OscColorTarget;

    /// <summary>OSC 4: ANSI palette color 0-15.</summary>
    public sealed record Palette(int Index) : OscColorTarget;
}

/// <summary>
/// One parsed OSC color reply: what it reports, and the color. Port of the TS inline return type
/// <c>{ target: OscColorTarget; rgb: RgbColor | undefined }</c>; <see cref="Rgb"/> is
/// <see langword="null"/> when the reply carried an unparseable color.
/// </summary>
public readonly record struct OscColorResponse(OscColorTarget Target, RgbColor? Rgb);

/// <summary>Port of <c>terminal-colors.ts</c>: OSC color replies and DSR color-scheme reports.</summary>
/// <remarks>
/// Two regex details differ from the TS source on purpose, both to keep the same match set:
/// <c>[0-9]</c> replaces <c>\d</c> (.NET's <c>\d</c> also matches Unicode decimal digits), and
/// <c>\z</c> replaces <c>$</c> (.NET's <c>$</c> also matches before a trailing newline, JS's does not).
/// </remarks>
public static partial class TerminalColors
{
    [GeneratedRegex(@"\A\x1b\](?:(1[01])|4;([0-9]{1,3}));([^\x07\x1b]*)(?:\x07|\x1b\\)\z", RegexOptions.IgnoreCase)]
    private static partial Regex OscColorResponseRegex();

    [GeneratedRegex(@"\A(?:\x1b\[\?997;(1|2)n)+\z")]
    private static partial Regex ColorSchemeReportRegex();

    [GeneratedRegex(@"\A[0-9a-f]{6}\z", RegexOptions.IgnoreCase)]
    private static partial Regex SixDigitHexRegex();

    [GeneratedRegex(@"\A[0-9a-f]{12}\z", RegexOptions.IgnoreCase)]
    private static partial Regex TwelveDigitHexRegex();

    [GeneratedRegex(@"\A[0-9a-f]+\z", RegexOptions.IgnoreCase)]
    private static partial Regex HexChannelRegex();

    [GeneratedRegex(@"\Argba?:", RegexOptions.IgnoreCase)]
    private static partial Regex RgbPrefixRegex();

    /// <summary>
    /// Parse an OSC 10, 11 or 4 color reply. Returns <see langword="null"/> when <paramref name="data"/>
    /// is not such a reply; <see cref="OscColorResponse.Rgb"/> is <see langword="null"/> when it is a
    /// reply with an unparseable color.
    /// </summary>
    public static OscColorResponse? ParseOscColorResponse(string data)
    {
        var match = OscColorResponseRegex().Match(data);
        if (!match.Success)
        {
            return null;
        }

        OscColorTarget target = match.Groups[1].Value switch
        {
            "10" => new OscColorTarget.Foreground(),
            "11" => new OscColorTarget.Background(),
            _ => new OscColorTarget.Palette(
                int.Parse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture)),
        };
        return new OscColorResponse(target, ParseOscColorValue(match.Groups[3].Value));
    }

    public static TerminalColorScheme? ParseTerminalColorSchemeReport(string data)
    {
        var match = ColorSchemeReportRegex().Match(data);
        if (!match.Success)
        {
            return null;
        }
        return match.Groups[1].Value == "2" ? TerminalColorScheme.Light : TerminalColorScheme.Dark;
    }

    /// <summary>The color an OSC reply carries: <c>#</c>-hex or <c>rgb:</c>-prefixed channels.</summary>
    private static RgbColor? ParseOscColorValue(string rawValue)
    {
        var value = rawValue.Trim();
        if (value.StartsWith('#'))
        {
            var hex = value.Substring(1);
            if (SixDigitHexRegex().IsMatch(hex))
            {
                return HexToRgb(value);
            }
            if (TwelveDigitHexRegex().IsMatch(hex))
            {
                var r = ParseOscHexChannel(hex.Substring(0, 4));
                var g = ParseOscHexChannel(hex.Substring(4, 4));
                var b = ParseOscHexChannel(hex.Substring(8, 4));
                return r is not null && g is not null && b is not null
                    ? new RgbColor(r.Value, g.Value, b.Value)
                    : null;
            }
            return null;
        }

        var rgbValue = RgbPrefixRegex().Replace(value, "");
        var parts = rgbValue.Split('/');
        if (parts.Length < 3)
        {
            return null;
        }
        var red = ParseOscHexChannel(parts[0]);
        var green = ParseOscHexChannel(parts[1]);
        var blue = ParseOscHexChannel(parts[2]);
        return red is not null && green is not null && blue is not null
            ? new RgbColor(red.Value, green.Value, blue.Value)
            : null;
    }

    private static RgbColor HexToRgb(string hex)
    {
        var normalized = hex.StartsWith('#') ? hex.Substring(1) : hex;
        return new RgbColor(
            Convert.ToInt32(normalized.Substring(0, 2), 16),
            Convert.ToInt32(normalized.Substring(2, 2), 16),
            Convert.ToInt32(normalized.Substring(4, 2), 16));
    }

    private static int? ParseOscHexChannel(string channel)
    {
        if (!HexChannelRegex().IsMatch(channel))
        {
            return null;
        }
        var max = Math.Pow(16, channel.Length) - 1;
        if (max <= 0)
        {
            return null;
        }
        return (int)JsMath.Round(Convert.ToInt32(channel, 16) / max * 255);
    }
}
