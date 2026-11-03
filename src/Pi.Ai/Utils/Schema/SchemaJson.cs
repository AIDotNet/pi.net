using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pi.Ai.Utils.Schema;

/// <summary>
/// JSON value semantics shared by the schema validator, coercer and explainer: the JS predicates the TS
/// implementation relies on (<c>typeof</c>, <c>Number.isInteger</c>, <c>Object.is</c>), JSON Pointer
/// escaping, deep equality and the JS <c>String(number)</c> / <c>JSON.stringify</c> renderings.
/// </summary>
internal static partial class SchemaJson
{
    /// <summary>Whether <paramref name="node"/> is a schema object (not <c>true</c>/<c>false</c>, an array or a primitive).</summary>
    public static bool IsSchemaObject(JsonNode? node) => node is JsonObject;

    /// <summary>Whether <paramref name="node"/> is a JSON object (the TS <c>isPlainObject</c>).</summary>
    public static bool IsPlainObject(JsonNode? node) => node is JsonObject;

    public static bool IsArray(JsonNode? node) => node is JsonArray;

    public static bool IsString(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String;

    public static bool IsBoolean(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    public static bool IsNull(JsonNode? node) => node is null || node.GetValueKind() == JsonValueKind.Null;

    /// <summary>Whether <paramref name="node"/> is a number (the TS <c>typeof v === "number"</c>).</summary>
    public static bool IsNumber(JsonNode? node, out double number)
    {
        number = 0;
        return node is JsonValue value && TryGetDouble(value, out number);
    }

    /// <summary>Whether <paramref name="node"/> is an integer (the TS <c>Number.isInteger</c>).</summary>
    public static bool IsInteger(JsonNode? node, out double number) =>
        IsNumber(node, out number) && !double.IsInfinity(number) && Math.Floor(number) == number;

    public static bool TryGetDouble(JsonValue value, out double number)
    {
        number = 0;
        if (value.GetValueKind() != JsonValueKind.Number) return false;
        if (value.TryGetValue<double>(out number)) return true;
        if (value.TryGetValue<long>(out var integer)) { number = integer; return true; }
        if (value.TryGetValue<int>(out var small)) { number = small; return true; }
        if (value.TryGetValue<decimal>(out var exact)) { number = (double)exact; return true; }
        return double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    public static string String(JsonNode? node) => IsString(node) ? node!.GetValue<string>() : node?.ToJsonString() ?? "";

    /// <summary>The TS <c>typeName(v)</c> used in "expected X, got Y" messages.</summary>
    public static string TypeName(JsonNode? node)
    {
        if (IsNull(node)) return "null";
        if (node is JsonArray) return "array";
        if (IsInteger(node, out _)) return "integer";
        return node switch
        {
            JsonObject => "object",
            JsonValue value => value.GetValueKind() switch
            {
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                _ => "null",
            },
            _ => "undefined",
        };
    }

    /// <summary>The TS <c>pointer(path, key)</c>: a JSON Pointer segment with <c>~</c> and <c>/</c> escaped.</summary>
    public static string Pointer(string path, string key) => $"{path}/{key.Replace("~", "~0").Replace("/", "~1")}";

    /// <summary>The TS <c>deepEqual</c>: JSON equality over primitives, arrays (ordered) and objects (same keys).</summary>
    public static bool DeepEqual(JsonNode? left, JsonNode? right)
    {
        if (IsNull(left) || IsNull(right)) return IsNull(left) && IsNull(right);
        if (left is JsonArray leftArray)
        {
            if (right is not JsonArray rightArray || leftArray.Count != rightArray.Count) return false;
            for (var index = 0; index < leftArray.Count; index++)
            {
                if (!DeepEqual(leftArray[index], rightArray[index])) return false;
            }
            return true;
        }
        if (left is JsonObject leftObject)
        {
            if (right is not JsonObject rightObject || leftObject.Count != rightObject.Count) return false;
            foreach (var (key, value) in leftObject)
            {
                if (!rightObject.TryGetPropertyValue(key, out var other) || !DeepEqual(value, other)) return false;
            }
            return true;
        }
        if (right is JsonArray or JsonObject) return false;
        if (IsNumber(left, out var leftNumber))
        {
            return IsNumber(right, out var rightNumber) && leftNumber.Equals(rightNumber);
        }
        if (IsBoolean(left) || IsBoolean(right))
        {
            return IsBoolean(left) && IsBoolean(right)
                && left!.GetValueKind() == right!.GetValueKind();
        }
        return IsString(left) && IsString(right)
            && string.Equals(left!.GetValue<string>(), right!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>The TS <c>Object.is</c>: primitives by value, objects by reference.</summary>
    public static bool SameValue(JsonNode? left, JsonNode? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is JsonArray or JsonObject || right is JsonArray or JsonObject) return false;
        return DeepEqual(left, right);
    }

    /// <summary>The number of Unicode code points of <paramref name="value"/> (JSON Schema counts code points).</summary>
    public static int CodePoints(string value)
    {
        var count = 0;
        for (var index = 0; index < value.Length; index++, count++)
        {
            var character = value[index];
            // A high surrogate starts a pair only when a low surrogate follows; an unpaired one stands alone.
            if (character is >= '\ud800' and <= '\udbff' && index + 1 < value.Length
                && value[index + 1] is >= '\udc00' and <= '\udfff')
            {
                index++;
            }
        }
        return count;
    }

    /// <summary>
    /// Whether a string satisfies the length bounds, counting code points only when the UTF-16 length is
    /// ambiguous (the TS <c>lengthOk</c>).
    /// </summary>
    public static bool LengthOk(string value, double min, double max)
    {
        var units = value.Length;
        if (units < min) return false;
        if (units < 2 * min && CodePoints(value) < min) return false;
        return units <= max || CodePoints(value) <= max;
    }

    // ---------------------------------------------------------------- JS number conversion

    /// <summary>The largest integer JS represents exactly (<c>Number.MAX_SAFE_INTEGER</c>).</summary>
    private static readonly BigInteger MaxSafeInteger = new(9007199254740991L);

    /// <summary>The TS <c>Number(value)</c> for a string. Never throws; unparseable input yields NaN.</summary>
    public static double ParseJsNumber(string value)
    {
        // JS StringToNumber trims StrWhiteSpace: tab, LF, VT, FF, CR, space, NBSP, BOM and the space separators.
        var text = value.Trim(' ', '\t', '\n', '\r', '\v', '\f', '\u00a0', '\ufeff');
        if (text.Length == 0) return 0;

        var signed = text[0] is '+' or '-';
        var sign = signed && text[0] == '-' ? -1d : 1d;
        var body = signed ? text[1..] : text;

        if (body == "Infinity") return sign * double.PositiveInfinity;

        // Non-decimal integer literals: 0x / 0o / 0b. JS accepts them unsigned only, so a sign turns them into NaN.
        if (body.Length > 2 && body[0] == '0')
        {
            var radix = body[1] switch { 'x' or 'X' => 16, 'o' or 'O' => 8, 'b' or 'B' => 2, _ => 0 };
            if (radix != 0) return signed ? double.NaN : ParseRadix(body[2..], radix);
        }

        // Decimal: a leading/trailing dot and an exponent are allowed, no grouping separators and no trailing junk.
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.NaN;
    }

    private static double ParseRadix(string digits, int radix)
    {
        if (digits.Length == 0) return double.NaN;
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
            if (digit < 0 || digit >= radix) return double.NaN;
            result = result * radix + digit;
        }
        return result;
    }

    /// <summary>TypeBox's <c>TryBigInt</c> of a string: <c>12n</c>, <c>12.5</c> (truncated) or <c>12</c>.</summary>
    public static bool TryBigInteger(string value, out BigInteger result)
    {
        result = BigInteger.Zero;
        var body = value;
        if (body.EndsWith('n'))
        {
            body = body[..^1];
            if (!IntegerPattern().IsMatch(body)) return false;
        }
        else if (DecimalPattern().IsMatch(body))
        {
            body = body[..body.IndexOf('.')];
        }
        else if (!IntegerPattern().IsMatch(body))
        {
            return false;
        }
        result = BigInteger.Parse(body, CultureInfo.InvariantCulture);
        return true;
    }

    [GeneratedRegex(@"^-?(?:0|[1-9]\d*)$")]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^-?(?:0|[1-9]\d*)\.\d+$")]
    private static partial Regex DecimalPattern();

    /// <summary>Whether <paramref name="value"/> is within the range JS converts to a number exactly.</summary>
    public static bool IsSafeInteger(BigInteger value) =>
        value <= MaxSafeInteger && value >= -MaxSafeInteger;

    // ---------------------------------------------------------------- JS renderings
    /// <summary>
    /// The TS <c>String(number)</c>: ECMAScript <c>Number::toString(10)</c>. <c>.NET</c> switches to exponent
    /// notation far earlier (1E-05), so the shortest round-trip digits are re-rendered with the ECMAScript rules.
    /// </summary>
    public static string NumberToString(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (value == 0) return "0";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value < 0) return "-" + NumberToString(-value);

        var (digits, n) = ShortestDigits(value);
        // value == 0.digits * 10^n, i.e. digits * 10^(n - k) with k = digits.Length
        var k = digits.Length;
        if (k <= n && n <= 21) return digits + new string('0', n - k);
        if (0 < n && n <= 21) return digits[..n] + "." + digits[n..];
        if (-6 < n && n <= 0) return "0." + new string('0', -n) + digits;
        var head = k == 1 ? digits : digits[..1] + "." + digits[1..];
        var sign = n - 1 < 0 ? "-" : "+";
        return head + "e" + sign + Math.Abs(n - 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The shortest round-trip decimal digits of a positive finite double, and the decimal exponent <c>n</c> such
    /// that <c>value == 0.digits * 10^n</c>.
    /// </summary>
    private static (string Digits, int N) ShortestDigits(double value)
    {
        // "R" is the shortest round-trippable form on .NET Core 3.0+; it prints "1.2345E+21" or "0.00123".
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var exponent = 0;
        var eIndex = text.IndexOfAny(['E', 'e']);
        if (eIndex >= 0)
        {
            exponent = int.Parse(text[(eIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..eIndex];
        }
        var point = text.IndexOf('.');
        var integerDigits = point < 0 ? text.Length : point;
        var all = point < 0 ? text : text.Remove(point, 1);
        var leading = 0;
        while (leading < all.Length - 1 && all[leading] == '0') leading++;
        var digits = all[leading..].TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        return (digits, exponent + integerDigits - leading);
    }

    /// <summary>The TS <c>JSON.stringify(value)</c> without indentation.</summary>
    public static string Stringify(JsonNode? node)
    {
        var builder = new StringBuilder();
        WriteJson(builder, node, null, 0);
        return builder.ToString();
    }

    /// <summary>The TS <c>JSON.stringify(value, null, 2)</c>.</summary>
    public static string StringifyIndented(JsonNode? node)
    {
        var builder = new StringBuilder();
        WriteJson(builder, node, "  ", 0);
        return builder.ToString();
    }

    private static void WriteJson(StringBuilder builder, JsonNode? node, string? indent, int depth)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                return;
            case JsonObject obj:
                if (obj.Count == 0) { builder.Append("{}"); return; }
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in obj)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    NewLine(builder, indent, depth + 1);
                    WriteString(builder, key);
                    builder.Append(':');
                    if (indent is not null) builder.Append(' ');
                    WriteJson(builder, value, indent, depth + 1);
                }
                NewLine(builder, indent, depth);
                builder.Append('}');
                return;
            case JsonArray array:
                if (array.Count == 0) { builder.Append("[]"); return; }
                builder.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    NewLine(builder, indent, depth + 1);
                    WriteJson(builder, array[index], indent, depth + 1);
                }
                NewLine(builder, indent, depth);
                builder.Append(']');
                return;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        WriteString(builder, value.GetValue<string>());
                        return;
                    case JsonValueKind.Number:
                        builder.Append(NumberToString(IsNumber(value, out var number) ? number : 0));
                        return;
                    case JsonValueKind.True:
                        builder.Append("true");
                        return;
                    case JsonValueKind.False:
                        builder.Append("false");
                        return;
                    default:
                        builder.Append("null");
                        return;
                }
        }
    }

    private static void NewLine(StringBuilder builder, string? indent, int depth)
    {
        if (indent is null) return;
        builder.Append('\n');
        for (var index = 0; index < depth; index++) builder.Append(indent);
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        builder.Append('"');
    }
}
