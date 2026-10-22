using System.Text;
using System.Text.RegularExpressions;

namespace Pi.Tui.Marked;

/// <summary>
/// Port of the <c>marked-cjk-friendly</c> v0.1.2 extension, which <c>components/markdown.ts</c>
/// installs through <c>use({ tokenizer: { emStrong } })</c>.
/// </summary>
/// <remarks>
/// <para>
/// CommonMark decides whether a delimiter run can open/close emphasis from the characters on both
/// sides (`*` is flanked by punctuation / whitespace). CJK text carries most of its meaning in
/// ideographs that are Unicode <c>Lo</c>, while the punctuation that follows them (U+3002 IDEOGRAPHIC
/// FULL STOP, U+FF1A FULLWIDTH COLON, U+300C/U+300D corner brackets …) is <c>Ps</c>/<c>Pe</c>; the
/// closing run of <c>**这是测试。**后面</c> is therefore "right-flanking: no", because it is preceded by
/// punctuation and followed by a letter-like ideograph, and the emphasis renders literally. The
/// extension treats CJK code points as punctuation for the flanking tests, so the run closes.
/// </para>
/// <para>
/// What the extension replaces is only the <c>emStrong</c> tokenizer. Its <c>del</c> half never takes
/// effect in this port: <c>markdown.ts</c> calls <c>setOptions({ tokenizer: StrictStrikethroughTokenizer })</c>
/// after <c>use()</c> and before the first parse, and the strict tokenizer replaces the whole tokenizer,
/// so <see cref="MarkedTokenizer.Del"/> stays the strict regex (see its remarks).
/// </para>
/// <para>
/// Deviation: .NET character classes are UTF-16 code units, so the astral part of the class
/// (U+16FE0 … U+3FFFD, emoji and CJK ext. B and beyond) cannot live inside the delimiter regexes.
/// The two checks that look at a single neighbouring code point (
/// <see cref="IsPrevCharCjk"/> / <see cref="IsCjkAdjacentToDelimiter"/>) are rune-based and cover the
/// full set; only the delimiter-run classification treats an astral CJK neighbour as an ordinary
/// word character, i.e. it behaves exactly like the port did before this batch.
/// </para>
/// </remarks>
public static class MarkedCjk
{
    // The package's CJK class as ranges, verbatim (BMP and astral alike). This table is the single
    // source of truth: the delimiter regexes are built from the BMP half of it.
    private static readonly (int Start, int End)[] Ranges =
    [
        (0x1100, 0x11FF), (0x20A9, 0x20A9), (0x2329, 0x232A), (0x2630, 0x2637),
        (0x268A, 0x268F), (0x2E80, 0x2E99), (0x2E9B, 0x2EF3), (0x2F00, 0x2FD5),
        (0x2FF0, 0x303E), (0x3041, 0x3096), (0x3099, 0x30FF), (0x3105, 0x312F),
        (0x3131, 0x318E), (0x3190, 0x31E5), (0x31EF, 0x321E), (0x3220, 0x3247),
        (0x3250, 0xA48C), (0xA490, 0xA4C6), (0xA960, 0xA97C), (0xAC00, 0xD7A3),
        (0xD7B0, 0xD7C6), (0xD7CB, 0xD7FB), (0xF900, 0xFAFF), (0xFE10, 0xFE19),
        (0xFE30, 0xFE52), (0xFE54, 0xFE66), (0xFE68, 0xFE6B), (0xFF01, 0xFFBE),
        (0xFFC2, 0xFFC7), (0xFFCA, 0xFFCF), (0xFFD2, 0xFFD7), (0xFFDA, 0xFFDC),
        (0xFFE0, 0xFFE6), (0xFFE8, 0xFFEE),

        (0x16FE0, 0x16FE4), (0x16FF0, 0x16FF6), (0x17000, 0x18CDA), (0x18CFF, 0x18D20),
        (0x18D80, 0x18DF2), (0x18E00, 0x19191), (0x191A0, 0x191D2), (0x1AFF0, 0x1AFF3),
        (0x1AFF5, 0x1AFFB), (0x1AFFD, 0x1AFFE), (0x1B000, 0x1B128), (0x1B132, 0x1B132),
        (0x1B150, 0x1B152), (0x1B155, 0x1B155), (0x1B164, 0x1B168), (0x1B170, 0x1B2FB),
        (0x1D300, 0x1D356), (0x1D360, 0x1D376), (0x1F1AE, 0x1F1AE), (0x1F200, 0x1F200),
        (0x1F202, 0x1F202), (0x1F210, 0x1F219), (0x1F21B, 0x1F22E), (0x1F230, 0x1F231),
        (0x1F237, 0x1F237), (0x1F23B, 0x1F23B), (0x1F240, 0x1F248), (0x1F260, 0x1F265),
        (0x1F7DA, 0x1F7DA), (0x20000, 0x3FFFD),
    ];

    private static readonly string CjkClass = BuildBmpClass();

    /// <summary>
    /// <c>punctuationCjk</c>: whitespace, punctuation, symbols and CJK, but never a delimiter run.
    /// Applied to a single <c>prevChar</c>.
    /// </summary>
    private static readonly Regex PunctuationCjk = new(@"^((?![*_])[\s\p{P}\p{S}" + CjkClass + "])", RegexOptions.Compiled);

    public static Regex EmStrongRDelimAst { get; } = BuildAst();

    public static Regex EmStrongRDelimUnd { get; } = BuildUnd();

    /// <summary>True when the string starts with a code point the package classifies as CJK.</summary>
    public static bool IsCjk(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        var codePoint = char.IsHighSurrogate(value[0]) && value.Length > 1 && char.IsLowSurrogate(value[1])
            ? char.ConvertToUtf32(value[0], value[1])
            : value[0];
        return IsCjkCodePoint(codePoint);
    }

    private static bool IsCjkCodePoint(int codePoint)
    {
        foreach (var (start, end) in Ranges)
        {
            if (codePoint >= start && codePoint <= end)
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildBmpClass()
    {
        var builder = new StringBuilder();
        foreach (var (start, end) in Ranges)
        {
            if (start > 0xFFFF)
            {
                continue;
            }

            if (start == end)
            {
                builder.Append($"\\u{start:X4}");
            }
            else
            {
                builder.Append($"\\u{start:X4}-\\u{end:X4}");
            }
        }

        return builder.ToString();
    }

    // Builders mirroring the package's buildRDelimAst / buildRDelimUnd templates. The GFM variants
    // take a `(?!~)` guard so a `~` cannot be swallowed as a flanking character by the `*` run, and
    // an ordinary character is `(?:[^\s\p{P}\p{S}CJK]|~)` rather than the plain negated class.
    private static Regex BuildAst()
    {
        var punct = "(?!~)[\\p{P}\\p{S}" + CjkClass + "]";
        var punctSpace = "(?!~)[\\s\\p{P}\\p{S}" + CjkClass + "]";
        var notPunctSpace = "(?:[^\\s\\p{P}\\p{S}" + CjkClass + "]|~)";
        return new Regex(
            "^[^_*]*?__[^_*]*?\\*[^_*]*?(?=__)|[^*]+(?=[^*])"
            + $"|(?!\\*){punct}(\\*+)(?=[\\s]|$)"
            + $"|{notPunctSpace}(\\*+)(?!\\*)(?={punctSpace}|$)"
            + $"|(?!\\*){punctSpace}(\\*+)(?={notPunctSpace})"
            + "|[\\s](\\*+)(?!\\*)(?=" + punct + ")"
            + $"|(?!\\*){punct}(\\*+)(?!\\*)(?={punct})"
            + $"|{notPunctSpace}(\\*+)(?={notPunctSpace})",
            RegexOptions.Compiled);
    }

    private static Regex BuildUnd()
    {
        // The `_` run keeps the non-GFM variants: `_` cannot collide with `~`, so the package does
        // not add the guard here either.
        var punct = "(?![*_])[\\p{P}\\p{S}" + CjkClass + "]";
        var punctSpace = "[\\s\\p{P}\\p{S}" + CjkClass + "]";
        var notPunctSpace = "[^\\s\\p{P}\\p{S}" + CjkClass + "]";
        return new Regex(
            "^[^_*]*?\\*\\*[^_*]*?_[^_*]*?(?=\\*\\*)|[^_]+(?=[^_])"
            + $"|(?!_){punct}(_+)(?=[\\s]|$)"
            + $"|{notPunctSpace}(_+)(?!_)(?={punctSpace}|$)"
            + $"|(?!_){punctSpace}(_+)(?={notPunctSpace})"
            + "|[\\s](_+)(?!_)(?=" + punct + ")"
            + $"|(?!_){punct}(_+)(?!_)(?={punct})"
            + $"|{notPunctSpace}(_+)(?={notPunctSpace})",
            RegexOptions.Compiled);
    }

    /// <summary>
    /// <c>isPrevCharCjk</c>: whether the character before <paramref name="src"/> counts as CJK.
    /// </summary>
    /// <remarks>
    /// marked hands the tokenizer a single UTF-16 code unit as <c>prevChar</c>, so a supplementary
    /// code point arrives as a lone surrogate and a variation sequence may arrive as the selector
    /// rather than the base character. The source recovers the real code point from
    /// <c>maskedSrc</c>; so does this.
    /// </remarks>
    public static bool IsPrevCharCjk(string previous, string masked, string src)
    {
        if (previous.Length > 0 && PunctuationCjk.IsMatch(previous))
        {
            return true;
        }

        if (previous.Length == 0)
        {
            return false;
        }

        var previousIndex = masked.Length - src.Length;
        if (previousIndex < 1)
        {
            return false;
        }

        var index = previousIndex - 1;
        var code = masked[index];

        // FE00-FE0E variation selectors belong to the base character before them.
        if (code is >= '\uFE00' and <= '\uFE0E' && index >= 1)
        {
            index--;
            code = masked[index];
        }

        if ((code & 0xFC00) == 0xDC00 && index >= 1)
        {
            var codePoint = char.IsHighSurrogate(masked[index - 1])
                ? char.ConvertToUtf32(masked[index - 1], masked[index])
                : masked[index - 1];

            if (codePoint > 0xFFFF)
            {
                // E0100-E01EF, the variation selectors supplement, count as CJK on their own.
                return codePoint is >= 0xE0100 and <= 0xE01EF || IsCjkCodePoint(codePoint);
            }

            return false;
        }

        return IsCjkCodePoint(code);
    }

    /// <summary>
    /// <c>isCjkAdjacentToDelimiter</c>: a right delimiter run that sits next to CJK is treated as
    /// "both" instead of "left only", which is what lets the run close across CJK punctuation.
    /// </summary>
    public static bool IsCjkAdjacentToDelimiter(Match match, string clipped)
    {
        if (match.Groups[1].Value.Length == 0 && match.Groups[2].Value.Length == 0 &&
            match.Groups[3].Value.Length == 0 && match.Groups[4].Value.Length == 0)
        {
            return false;
        }

        var after = match.Index + match.Value.Length;
        var charAfter = after < clipped.Length ? FirstCodePoint(clipped[after..]) : "";
        return IsCjk(FirstCodePoint(match.Value)) || (charAfter.Length > 0 && IsCjk(charAfter));
    }

    private static string FirstCodePoint(string value)
    {
        if (value.Length == 0)
        {
            return "";
        }

        return char.IsHighSurrogate(value[0]) && value.Length > 1 && char.IsLowSurrogate(value[1])
            ? value[..2]
            : value[..1];
    }
}
