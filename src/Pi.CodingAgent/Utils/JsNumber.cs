using System.Globalization;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// The JavaScript <c>Number()</c> conversion and the <c>Number.isSafeInteger</c> predicate.
/// Ported because the llama.cpp provider reads <c>--ctx-size</c> style CLI arguments out of
/// <c>status.args</c> and the TS original gates the result on <c>Number.isSafeInteger</c>; .NET's
/// <c>double.Parse</c> accepts inputs JS rejects (<c>"NaN"</c>, <c>"1,5"</c> under a loose culture)
/// and rejects inputs JS accepts (<c>"0x10"</c>, <c>"1e3"</c> only via NumberStyles.Float).
/// </summary>
public static class JsNumber
{
    /// <summary>The largest integer JS represents exactly (<c>Number.MAX_SAFE_INTEGER</c>).</summary>
    public const double MaxSafeInteger = 9007199254740991d;

    /// <summary>The TS <c>Number(value)</c>. Never throws; unparseable input yields NaN.</summary>
    public static double Parse(string? value)
    {
        if (value is null)
        {
            return 0;
        }

        // JS StringToNumber trims StrWhiteSpace: tab, LF, VT, FF, CR, space, NBSP, BOM and the
        // Unicode space separators. String.Trim() covers all of them for the ASCII subset we see.
        var text = value.Trim(' ', '\t', '\n', '\r', '\v', '\f', '\u00a0', '\ufeff');
        if (text.Length == 0)
        {
            return 0;
        }

        var signed = text[0] is '+' or '-';
        var sign = signed && text[0] == '-' ? -1d : 1d;
        var body = signed ? text[1..] : text;

        if (body is "Infinity")
        {
            return sign * double.PositiveInfinity;
        }

        // Non-decimal integer literals: 0x / 0o / 0b. JS only accepts them unsigned, so a sign turns
        // them into NaN ("+0x10" is NaN while "0x10" is 16).
        if (body.Length > 2 && body[0] == '0')
        {
            var radix = body[1] switch
            {
                'x' or 'X' => 16,
                'o' or 'O' => 8,
                'b' or 'B' => 2,
                _ => 0,
            };
            if (radix != 0)
            {
                return signed ? double.NaN : ParseRadix(body[2..], radix);
            }
        }

        // Decimal: JS allows a leading/trailing dot, an exponent, but no grouping separators and no
        // trailing junk. NumberStyles.Float with InvariantCulture matches that set.
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.NaN;
    }

    /// <summary>The TS <c>Number.isSafeInteger(value)</c>.</summary>
    public static bool IsSafeInteger(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value)
            && value == Math.Floor(value) && Math.Abs(value) <= MaxSafeInteger;

    /// <summary>
    /// The TS <c>String(number)</c> for a whole-number-friendly value. Whole values inside the safe
    /// range print without a decimal point (<c>String(128) == "128"</c>); everything else uses the
    /// round-trip format.
    /// </summary>
    public static string ToJsString(double value) =>
        value == Math.Floor(value) && !double.IsInfinity(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The TS <c>Number.prototype.toFixed(digits)</c>.</summary>
    public static string ToFixed(double value, int digits) =>
        value.ToString($"F{digits}", CultureInfo.InvariantCulture);

    private static double ParseRadix(string digits, int radix)
    {
        if (digits.Length == 0)
        {
            return double.NaN;
        }

        double result = 0;
        foreach (var character in digits)
        {
            var digit = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'f' => character - 'a' + 10,
                >= 'A' and <= 'F' => character - 'A' + 10,
                _ => -1,
            };
            if (digit < 0 || digit >= radix)
            {
                return double.NaN;
            }

            result = result * radix + digit;
        }

        return result;
    }
}
