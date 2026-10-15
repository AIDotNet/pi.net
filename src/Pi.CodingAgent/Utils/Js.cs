// ============================================================================
// JavaScript semantics that differ from .NET's.
// ============================================================================

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// The small set of JavaScript coercions the port needs and .NET spells differently: the <c>\s</c>
/// class, the <c>String.prototype.trim</c> family, and numeric truthiness.
/// </summary>
/// <remarks>
/// <para>
/// ECMAScript <c>WhiteSpace</c> plus <c>LineTerminator</c> contains <c>U+FEFF</c> (a zero-width no-break
/// space, which is also the byte-order mark) and does <em>not</em> contain <c>U+0085</c> (NEL) or the
/// C0 separators <c>U+001C</c>-<c>U+001F</c>; .NET's <c>char.IsWhiteSpace</c> is the other way round on
/// both counts. The difference is observable in rendered tool output: a file read with a byte-order
/// mark keeps that character through <c>string.Trim()</c> but not through JS <c>trim()</c>.
/// </para>
/// <para>
/// <c>Core/Tools/JsDiff.cs</c> carries a private copy of the whitespace table, because it landed in
/// 4f-3b before this module existed and its own mutation anchors sit on the call sites. Folding the two
/// together is a deferred cleanup; the table itself is identical.
/// </para>
/// </remarks>
public static class Js
{
    /// <summary>Whether the code point is ECMAScript <c>\s</c>.</summary>
    public static bool IsWhitespace(int codePoint) => codePoint switch
    {
        0x0009 or 0x000A or 0x000B or 0x000C or 0x000D or 0x0020 => true,
        0x00A0 or 0x1680 => true,
        >= 0x2000 and <= 0x200A => true,
        0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 => true,
        0xFEFF => true,
        _ => false,
    };

    /// <summary>Whether the character is ECMAScript <c>\s</c>.</summary>
    public static bool IsWhitespace(char ch) => IsWhitespace((int)ch);

    /// <summary>The JS <c>String.prototype.trim</c>.</summary>
    public static string Trim(string value)
    {
        var start = TrimStartOffset(value);
        var end = TrimEndOffset(value);
        return end <= start ? "" : value[start..end];
    }

    /// <summary>The JS <c>String.prototype.trimStart</c>.</summary>
    public static string TrimStart(string value) => value[TrimStartOffset(value)..];

    /// <summary>The JS <c>String.prototype.trimEnd</c>.</summary>
    public static string TrimEnd(string value) => value[..TrimEndOffset(value)];

    /// <summary>The index of the first character JS <c>trimStart</c> keeps.</summary>
    public static int TrimStartOffset(string value)
    {
        var start = 0;
        while (start < value.Length && IsWhitespace(value[start]))
        {
            start++;
        }

        return start;
    }

    /// <summary>One past the last character JS <c>trimEnd</c> keeps.</summary>
    public static int TrimEndOffset(string value)
    {
        var end = value.Length;
        while (end > 0 && IsWhitespace(value[end - 1]))
        {
            end--;
        }

        return end;
    }

    /// <summary>
    /// The JS truthiness of an optional number, as used by guards such as
    /// <c>if (result.details?.entryLimitReached)</c>: absent and zero are both falsy, and any other
    /// value (including a negative one) is truthy.
    /// </summary>
    public static bool IsTruthy(int? value) => value is { } number && number != 0;

    /// <summary>The JS truthiness of a number: zero and <c>NaN</c> are falsy.</summary>
    public static bool IsTruthy(double value) => value != 0 && !double.IsNaN(value);

    /// <summary>
    /// The JS truthiness of a raw argument value, for the guards the renderers write as
    /// <c>if (args?.timeout)</c>. Objects and arrays are always truthy, the empty string and zero are
    /// not, and a JSON null is falsy just like the absent member it usually stands for.
    /// </summary>
    public static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        string text => text.Length > 0,
        int i => i != 0,
        long l => l != 0,
        double d => IsTruthy(d),
        float f => IsTruthy(f),
        decimal m => m != 0,
        JsonValue json => json.GetValueKind() switch
        {
            JsonValueKind.Null => false,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => json.ToString().Length > 0,
            JsonValueKind.Number => IsTruthy(double.Parse(json.ToString(), CultureInfo.InvariantCulture)),
            _ => true,
        },
        _ => true,
    };

    // ------------------------------------------------------------------ numbers

    /// <summary>
    /// The ECMAScript <c>Number::toString</c>, i.e. what a JS template literal prints for a number.
    /// </summary>
    /// <remarks>
    /// .NET's default formatting agrees with JS on the digits — both print the shortest decimal that
    /// round-trips — but not on the placement: .NET switches to exponential notation from about
    /// <c>1e15</c> down to <c>1e-5</c>, while JS keeps plain decimal notation for the whole range
    /// <c>[1e-6, 1e21)</c>. This applies the ECMAScript <c>Number::toString</c> placement rules to the
    /// shortest digits.
    /// </remarks>
    public static string NumberToString(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        if (value == 0)
        {
            // Covers -0 as well, which JS also prints as "0".
            return "0";
        }

        if (value < 0)
        {
            return $"-{NumberToString(-value)}";
        }

        var (digits, exponent) = ShortestDigits(value);
        var k = digits.Length;
        var n = exponent;

        // ECMAScript Number::toString, steps 6-10.
        if (k <= n && n <= 21)
        {
            return digits + new string('0', n - k);
        }

        if (0 < n && n <= 21)
        {
            return $"{digits[..n]}.{digits[n..]}";
        }

        if (-6 < n && n <= 0)
        {
            return $"0.{new string('0', -n)}{digits}";
        }

        var mantissa = k == 1 ? digits : $"{digits[..1]}.{digits[1..]}";
        var e = n - 1;
        return e >= 0 ? $"{mantissa}e+{e}" : $"{mantissa}e{e}";
    }

    /// <summary>
    /// The ECMAScript <c>Number.prototype.toFixed</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The specification picks the integer <c>n</c> minimising <c>|n / 10^f - x|</c> on the
    /// <em>exact</em> value of <c>x</c>, breaking ties toward the larger <c>n</c>. .NET's
    /// <c>double.ToString("F…")</c> and <c>Math.Round</c> both break ties toward the even digit and
    /// operate on a scaled value, so neither agrees: <c>0.25</c> formats as <c>0.2</c> where JS gives
    /// <c>0.3</c>, and <c>0.35</c> (whose exact value is just below 0.35) rounds up where JS rounds
    /// down. The exact decimal expansion is therefore computed with integer arithmetic.
    /// </para>
    /// <para>
    /// The sign comes from the original value, not from the rounded magnitude, so JS prints
    /// <c>-0.0</c> for <c>(-0.04).toFixed(1)</c>.
    /// </para>
    /// </remarks>
    public static string ToFixed(double value, int digits)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        if (Math.Abs(value) >= 1e21)
        {
            return NumberToString(value);
        }

        var rounded = ScaleAndRoundHalfAwayFromZero(Math.Abs(value), digits);
        var text = InsertDecimalPoint(rounded, digits);
        return value < 0 ? $"-{text}" : text;
    }

    /// <summary>
    /// <c>round(|value| × 10^digits)</c> on the double's exact value, with ties away from zero, as an
    /// exact integer.
    /// </summary>
    private static BigInteger ScaleAndRoundHalfAwayFromZero(double magnitude, int digits)
    {
        // Decompose the double: value = mantissa × 2^exponent, with exponent in [-1074, 971].
        var bits = (ulong)BitConverter.DoubleToInt64Bits(magnitude);
        var mantissa = bits & 0xF_FFFF_FFFF_FFFFUL;
        var exponent = (int)((bits >> 52) & 0x7FFUL);
        if (exponent == 0)
        {
            exponent = 1; // Subnormal: no implicit leading bit.
        }
        else
        {
            mantissa |= 1UL << 52;
        }

        exponent -= 1075;

        var numerator = new BigInteger(mantissa) * BigInteger.Pow(10, digits);
        if (exponent >= 0)
        {
            // value × 10^digits is already an integer.
            return numerator << exponent;
        }

        // value × 10^digits = mantissa × 10^digits / 2^-exponent = mantissa × 5^-exponent / 10^-exponent.
        numerator *= BigInteger.Pow(5, -exponent);
        var denominator = BigInteger.Pow(10, -exponent);
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        return remainder * 2 >= denominator ? quotient + BigInteger.One : quotient;
    }

    /// <summary>Place the decimal point <paramref name="digits"/> positions from the right.</summary>
    private static string InsertDecimalPoint(BigInteger scaled, int digits)
    {
        var text = scaled.ToString(CultureInfo.InvariantCulture);
        if (digits == 0)
        {
            return text;
        }

        if (text.Length <= digits)
        {
            return $"0.{new string('0', digits - text.Length)}{text}";
        }

        return $"{text[..^digits]}.{text[^digits..]}";
    }

    /// <summary>
    /// The shortest decimal digits <c>d</c> and exponent <c>n</c> for which
    /// <c>value = d × 10^(n - d.Length)</c>, matching the ECMAScript definition of the value's
    /// "shortest" representation. .NET's round-trip format supplies the digits; this normalises them
    /// out of whatever notation it chose.
    /// </summary>
    private static (string Digits, int Exponent) ShortestDigits(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var eIndex = text.IndexOfAny(['e', 'E']);
        var mantissa = eIndex < 0 ? text : text[..eIndex];
        var exponent = eIndex < 0 ? 0 : int.Parse(text[(eIndex + 1)..], CultureInfo.InvariantCulture);

        var point = mantissa.IndexOf('.');
        var fractionDigits = point < 0 ? 0 : mantissa.Length - point - 1;
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        // value = digits × 10^(exponent - fractionDigits)
        var scale = exponent - fractionDigits;

        var start = 0;
        while (start < digits.Length - 1 && digits[start] == '0')
        {
            start++;
        }

        digits = digits[start..];
        var end = digits.Length;
        while (end > 1 && digits[end - 1] == '0')
        {
            end--;
        }

        scale += digits.Length - end;
        digits = digits[..end];
        return (digits, scale + digits.Length);
    }
}

