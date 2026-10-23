using System.Text.RegularExpressions;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Port of <c>utils/ansi.ts</c>, which itself derives from <c>ansi-regex</c> / <c>strip-ansi</c>
/// (MIT, Sindre Sorhus).
/// </summary>
/// <remarks>
/// Renamed from the TS module's <c>stripAnsi</c> home to <c>AnsiText</c> because <see cref="Pi.Tui.Ansi"/>
/// already exists and is imported alongside this namespace; the TS package has the same two
/// independent implementations (tui's <c>utils.ts</c> vs coding-agent's <c>utils/ansi.ts</c>).
/// </remarks>
public static class AnsiText
{
    /// <summary>Longest unfinished sequence held back while streaming. Longer ones are processed as-is.</summary>
    public const int MaxPendingAnsiLength = 256;

    // Valid string terminator sequences are BEL, ESC\, and 0x9c.
    private const string St = @"(?:\u0007|\u001B\\|\u009C)";

    // OSC sequences: ESC ] ... ST
    private const string OscStart = @"\u001B\]";

    // CSI and related: ESC/C1, optional intermediates, optional params (supports ; and :), then final byte.
    // JS `\d` is [0-9]; .NET `\d` would also match other Unicode decimal digits.
    private const string CsiStart = @"[\u001B\u009B][\[\]()#;?]*(?:[0-9]{1,4}(?:[;:][0-9]{0,4})*)?";
    private const string CsiFinal = @"[0-9A-PR-TZcf-nq-uy=><~]";

    // Complete sequences. OSC is non-greedy until the first ST.
    private static readonly Regex Pattern = new(
        "(?:" + OscStart + @"[\s\S]*?" + St + ")|" + CsiStart + CsiFinal,
        RegexOptions.Compiled);

    // Unfinished sequence at the end of the text: OSC without its ST (a trailing ESC may start ESC\),
    // or CSI without its final byte. JS `$` anchors at end-of-input only; .NET `$` would also match
    // before a trailing newline, so the anchor is `\z`.
    private static readonly Regex UnfinishedAtEndPattern = new(
        "(?:" + OscStart + @"(?:[^\u0007\u009C\u001B]|\u001B(?!\\))*|" + CsiStart + @")\z",
        RegexOptions.Compiled);

    /// <summary>
    /// Split streamed text into a part that is safe to pass to <see cref="StripAnsi"/> now and a trailing
    /// unfinished escape sequence that should be prepended to the next chunk.
    /// </summary>
    /// <remarks>
    /// Streaming output arrives in arbitrary chunks, so an escape sequence can straddle a chunk boundary
    /// (#10504). Stripping each chunk in isolation leaves the tail behind as literal text — e.g. a colour
    /// reset split as <c>ESC[0</c> + <c>m</c> renders a stray <c>m</c>.
    /// </remarks>
    public static (string Complete, string Pending) SplitIncompleteAnsiSuffix(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Fast path: ANSI codes require an ESC (7-bit) or CSI (8-bit) introducer.
        if (!value.Contains('\u001B') && !value.Contains('\u009B'))
        {
            return (value, string.Empty);
        }

        var windowStart = Math.Max(0, value.Length - MaxPendingAnsiLength);
        var match = UnfinishedAtEndPattern.Match(value[windowStart..]);
        if (!match.Success)
        {
            return (value, string.Empty);
        }

        var splitAt = windowStart + match.Index;
        return (value[..splitAt], value[splitAt..]);
    }

    /// <summary>Remove ANSI escape sequences from <paramref name="value"/>.</summary>
    public static string StripAnsi(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Fast path: ANSI codes require an ESC (7-bit) or CSI (8-bit) introducer.
        if (!value.Contains('\u001B') && !value.Contains('\u009B'))
        {
            return value;
        }

        return Pattern.Replace(value, string.Empty);
    }
}
