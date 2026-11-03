using System.Text.RegularExpressions;

namespace Pi.Ai.Utils.Schema;

/// <summary>
/// The JSON Schema 2020-12 format checks, as <c>utils/validation.ts</c> compiles with: the set the TS
/// <c>@earendil-works/pi-schema/formats</c> entry point provides (TypeBox 1.3.27, MIT).
/// </summary>
/// <remarks>
/// Two entries are deliberately absent: <c>hostname</c> and <c>idn-hostname</c> (TypeBox validates those through
/// its IDNA label tables, which are not ported). A schema that names them treats <c>format</c> as an annotation,
/// so the value is accepted rather than rejected — the safe direction for tool arguments.
/// <c>iri</c>, <c>iri-reference</c> and <c>url</c> delegate to the platform URL parser (the TS calls
/// <c>URL.canParse</c>); <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/> is the closest equivalent.
/// </remarks>
internal static partial class SchemaFormats
{
    /// <summary>Every format check by name.</summary>
    public static readonly IReadOnlyDictionary<string, Func<string, bool>> All =
        new Dictionary<string, Func<string, bool>>(StringComparer.Ordinal)
        {
            ["date"] = IsDate,
            ["date-time"] = IsDateTime,
            ["duration"] = IsDuration,
            ["email"] = IsEmail,
            ["idn-email"] = IsIdnEmail,
            ["ipv4"] = IsIPv4,
            ["ipv6"] = IsIPv6,
            ["iri"] = IsIri,
            ["iri-reference"] = IsIriReference,
            ["json-pointer"] = IsJsonPointer,
            ["json-pointer-uri-fragment"] = IsJsonPointerUriFragment,
            ["regex"] = IsRegex,
            ["relative-json-pointer"] = IsRelativeJsonPointer,
            ["time"] = IsTime,
            ["uri"] = IsUri,
            ["uri-reference"] = IsUriReference,
            ["uri-template"] = IsUriTemplate,
            ["url"] = IsUrl,
            ["uuid"] = IsUuid,
        };

    private static readonly int[] Days = [0, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    /// <summary>An ISO 8601 date (<c>2020-12-12</c>), including the leap-year day.</summary>
    public static bool IsDate(string value)
    {
        var match = DatePattern.Match(value);
        if (!match.Success) return false;
        var year = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        var limit = month == 2 && IsLeapYear(year) ? 29 : month >= 1 && month <= 12 ? Days[month] : 0;
        return month is >= 1 and <= 12 && day >= 1 && day <= limit;
    }

    private static bool IsLeapYear(int year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    /// <summary>An ISO 8601 date-time: a date and a time joined by <c>T</c>.</summary>
    public static bool IsDateTime(string value)
    {
        var parts = value.Split('T', 't');
        return parts.Length == 2 && IsDate(parts[0]) && IsTime(parts[1]);
    }

    /// <summary>An ISO 8601 time, requiring a time zone offset (or <c>Z</c>) and accepting a leap second.</summary>
    public static bool IsTime(string value)
    {
        var match = TimePattern.Match(value);
        if (!match.Success) return false;
        // A time zone offset or Z/z is required (strictTimeZone defaults to true).
        if (!match.Groups[4].Success && !match.Groups[5].Success) return false;
        var hour = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var second = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (hour > 23 || minute > 59 || second > 60) return false;
        var zoneHour = 0;
        var zoneMinute = 0;
        if (match.Groups[5].Success)
        {
            zoneHour = int.Parse(match.Groups[6].Value, System.Globalization.CultureInfo.InvariantCulture);
            zoneMinute = int.Parse(match.Groups[7].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (zoneHour > 23 || zoneMinute > 59) return false;
        }
        if (second < 60) return true;
        // A leap second is only valid at 23:59:60 UTC.
        var sign = match.Groups[5].Value == "-" ? -1 : 1;
        var utcMinutes = hour * 60 + minute - sign * (zoneHour * 60 + zoneMinute);
        return ((utcMinutes % 1440) + 1440) % 1440 == 1439;
    }

    /// <summary>An ISO 8601 duration (<c>P1Y2M3DT4H5M6S</c>).</summary>
    public static bool IsDuration(string value) => DurationPattern.IsMatch(value);

    /// <summary>An RFC 5322 email address.</summary>
    public static bool IsEmail(string value) => EmailPattern.IsMatch(value);

    /// <summary>An RFC 4952 internationalized email address.</summary>
    public static bool IsIdnEmail(string value) => IdnEmailPattern.IsMatch(value.Normalize());

    public static bool IsIPv4(string value) => Ipv4Pattern.IsMatch(value);

    public static bool IsIPv6(string value) => Ipv6Pattern.IsMatch(value);

    /// <summary>An RFC 3987 IRI: no unencoded whitespace, no malformed percent-encoding, parseable as a URL.</summary>
    public static bool IsIri(string value) =>
        !InvalidIriChars.IsMatch(value) && !InvalidPercentEncoding.IsMatch(value) && IsUrl(value);

    /// <summary>An RFC 3987 IRI reference: also allows a relative reference.</summary>
    public static bool IsIriReference(string value) =>
        !InvalidIriReferenceChars.IsMatch(value)
        && !MalformedScheme.IsMatch(value)
        && Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out _);

    public static bool IsJsonPointer(string value) => JsonPointerPattern.IsMatch(value);

    public static bool IsJsonPointerUriFragment(string value) => JsonPointerUriFragmentPattern.IsMatch(value);

    /// <summary>Whether the value compiles as an ECMAScript regular expression.</summary>
    public static bool IsRegex(string value)
    {
        try
        {
            _ = new Regex(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsRelativeJsonPointer(string value) => RelativeJsonPointerPattern.IsMatch(value);

    public static bool IsUri(string value) => UriPattern.IsMatch(value);

    public static bool IsUriReference(string value) => UriReferencePattern.IsMatch(value);

    public static bool IsUriTemplate(string value) => UriTemplatePattern.IsMatch(value);

    /// <summary>A URL: a scheme and an absolute URI (the TS uses <c>URL.canParse</c>).</summary>
    public static bool IsUrl(string value) =>
        Scheme.IsMatch(value) && Uri.TryCreate(value, UriKind.Absolute, out _);

    public static bool IsUuid(string value) => UuidPattern.IsMatch(value);

    private static readonly Regex InvalidIriChars = new(@"[\x00-\x20<>^`{|}\\]", RegexOptions.CultureInvariant);

    private static readonly Regex InvalidPercentEncoding = new(@"%(?![0-9a-fA-F]{2})", RegexOptions.CultureInvariant);

    private static readonly Regex InvalidIriReferenceChars =
        new(@"[\x00-\x20\x7F\\]|%(?![0-9a-fA-F]{2})", RegexOptions.CultureInvariant);

    private static readonly Regex MalformedScheme = new(@"^[a-zA-Z][a-zA-Z0-9+\-.]*//", RegexOptions.CultureInvariant);

    private static readonly Regex Scheme = new(@"^[a-zA-Z][a-zA-Z0-9+\-.]*:", RegexOptions.CultureInvariant);
}
