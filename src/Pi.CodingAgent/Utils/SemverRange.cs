// ============================================================================
// SemverRange — the range half of semver, ported for core/package-manager.ts (4e-3c-2a)
// ============================================================================
//
// `Semver.cs` covers `valid()` / `parse()` / `compare()`. The range half (`validRange`,
// `satisfies`) has a single consumer in the port — `core/package-manager.ts`'s
// `getNpmVersionRange` and `installedNpmMatchesConfiguredVersion` — so it lives here, next to
// `Semver`, rather than being folded into it.
//
// This is a transcription of semver 7.8.5's `classes/range.js` + `classes/comparator.js` plus the
// `functions/replace-*` desugaring helpers, narrowed to the two option values the caller uses:
// `loose: false` and `includePrerelease: false`. Both are hard-coded, so the loose regexes and the
// `includePrerelease` branches are not reproduced; the strict regexes and the plain `-0` sentinels
// are. `maxSatisfying` / `gt` / `rcompare` are not ported — their only callers are the npm update
// paths (`update` / `checkForAvailableUpdates`), which the port has not landed.
//
// Covered range syntax: comparator sets separated by `||`, space-separated comparators within a
// set, the operators `=`, `<`, `>`, `<=`, `>=`, `^`, `~`, `~>`, x-ranges (`1.x`, `1.2.x`, `*`,
// `""`), hyphen ranges (`1.2.3 - 1.2.4`), and semver's prerelease-inclusion rule (a prerelease
// version satisfies a set only when some comparator in it carries a prerelease on the same
// major.minor.patch).

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pi.CodingAgent.Utils;

/// <summary>Port of semver's <c>validRange()</c> and <c>satisfies()</c> in strict mode.</summary>
public static class SemverRange
{
    /// <summary>
    /// semver's <c>validRange(range)</c>: the normalized range, <c>"*"</c> for an empty range, or
    /// <see langword="null"/> when <paramref name="range"/> is not a valid range.
    /// </summary>
    public static string? ValidRange(string? range)
    {
        // Upstream calls `range.trim()` inside the Range constructor, so a null range throws and is
        // caught here. An empty *string* is a valid range meaning "any".
        if (range is null)
        {
            return null;
        }

        try
        {
            var parsed = new Range(range);
            var formatted = parsed.Formatted;
            return formatted.Length > 0 ? formatted : "*";
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>semver's <c>satisfies(version, range)</c>: false for an unparsable version or range.</summary>
    public static bool Satisfies(string? version, string? range)
    {
        if (range is null)
        {
            return false;
        }

        try
        {
            return new Range(range).Test(version ?? "");
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ---- internal/re.js, strict flavour only ----

    private const string NumericIdentifier = "0|[1-9]\\d*";

    private const string NonNumericIdentifier = "\\d*[a-zA-Z-][a-zA-Z0-9-]*";

    private const string PrereleaseIdentifier = "(?:" + NumericIdentifier + "|" + NonNumericIdentifier + ")";

    private const string Prerelease = "(?:-(" + PrereleaseIdentifier + "(?:\\." + PrereleaseIdentifier + ")*))";

    private const string BuildIdentifier = "[a-zA-Z0-9-]+";

    private const string Build = "(?:\\+(" + BuildIdentifier + "(?:\\." + BuildIdentifier + ")*))";

    private const string MainVersion =
        "(" + NumericIdentifier + ")\\.(" + NumericIdentifier + ")\\.(" + NumericIdentifier + ")";

    private const string FullPlain = "v?" + MainVersion + Prerelease + "?" + Build + "?";

    private const string GtLt = "((?:<|>)?=?)";

    private const string XRangeIdentifier = NumericIdentifier + "|x|X|\\*";

    private const string XRangePlain =
        "[v=\\s]*(" + XRangeIdentifier + ")" +
        "(?:\\.(" + XRangeIdentifier + ")" +
        "(?:\\.(" + XRangeIdentifier + ")" +
        Prerelease + "?" + Build + "?" +
        ")?)?";

    // .NET `$` also matches before a trailing newline, and JS `$` (without `m`) does not, so every
    // anchored pattern ends with `\z` instead. Digits are written `[0-9]` because .NET `\d` accepts
    // any Unicode decimal digit while JS `\d` is ASCII-only.
    private static readonly Regex CaretRegex = new("^\\^(?:" + XRangePlain + ")\\z", RegexOptions.Compiled);

    private static readonly Regex TildeRegex = new("^~>?(?:" + XRangePlain + ")\\z", RegexOptions.Compiled);

    private static readonly Regex XRangeRegex =
        new("^" + GtLt + "\\s*(?:" + XRangePlain + ")\\z", RegexOptions.Compiled);

    private static readonly Regex StarRegex = new("(<|>)?=?\\s*\\*", RegexOptions.Compiled);

    private static readonly Regex Gte0Regex = new("^\\s*>=\\s*0\\.0\\.0\\s*\\z", RegexOptions.Compiled);

    private static readonly Regex ComparatorRegex =
        new("^" + GtLt + "\\s*(" + FullPlain + ")\\z|^\\z", RegexOptions.Compiled);

    // Upstream wraps each XRANGEPLAIN in a *capturing* group here (unlike COMPARATOR and XRANGE,
    // which do not), which is what puts the whole `from`/`to` text at groups 1 and 7.
    private static readonly Regex HyphenRangeRegex =
        new("^\\s*(" + XRangePlain + ")\\s+-\\s+(" + XRangePlain + ")\\s*\\z", RegexOptions.Compiled);

    // Upstream's COMPARATORTRIM is not mode-switched: it always offers the *loose* plain version
    // first. The port offers the strict one instead. The only inputs the two disagree on are loose
    // spellings such as `> 01.2.3`, which the strict <see cref="Comparator"/> would reject anyway.
    private static readonly Regex ComparatorTrimRegex =
        new("(\\s*)" + GtLt + "\\s*(" + FullPlain + "|" + XRangePlain + ")", RegexOptions.Compiled);

    private static readonly Regex TildeTrimRegex = new("(\\s*)(?:~>?)\\s+", RegexOptions.Compiled);

    private static readonly Regex CaretTrimRegex = new("(\\s*)(?:\\^)\\s+", RegexOptions.Compiled);

    private static readonly Regex WhitespaceRunRegex = new("\\s+", RegexOptions.Compiled);

    /// <summary>semver's <c>isX</c>.</summary>
    private static bool IsX(string id) =>
        id.Length == 0 || string.Equals(id, "x", StringComparison.OrdinalIgnoreCase) || string.Equals(id, "*", StringComparison.Ordinal);

    /// <summary>JS <c>+id + 1</c> for an identifier already known to be numeric.</summary>
    private static string Increment(string id)
        => (long.Parse(id, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);

    // ---- functions/replace-*.js ----

    /// <summary>semver's <c>parseComparator</c>: desugar caret, tilde, x-range and star forms.</summary>
    private static string ParseComparator(string comp)
    {
        comp = ReplaceCarets(comp);
        comp = ReplaceTildes(comp);
        comp = ReplaceXRanges(comp);
        comp = ReplaceStars(comp);
        return comp;
    }

    private static string ReplaceCarets(string comp)
        => string.Join(' ', Regex.Split(comp.Trim(), "\\s+").Select(ReplaceCaret));

    private static string ReplaceCaret(string comp) => CaretRegex.Replace(comp, match =>
    {
        var major = match.Groups[1].Value;
        var minor = match.Groups[2].Value;
        var patch = match.Groups[3].Value;
        var prerelease = match.Groups[4].Value;

        if (IsX(major))
        {
            return "";
        }

        if (IsX(minor))
        {
            return $">={major}.0.0 <{Increment(major)}.0.0-0";
        }

        if (IsX(patch))
        {
            return string.Equals(major, "0", StringComparison.Ordinal)
                ? $">={major}.{minor}.0 <{major}.{Increment(minor)}.0-0"
                : $">={major}.{minor}.0 <{Increment(major)}.0.0-0";
        }

        if (prerelease.Length > 0)
        {
            if (string.Equals(major, "0", StringComparison.Ordinal))
            {
                return string.Equals(minor, "0", StringComparison.Ordinal)
                    ? $">={major}.{minor}.{patch}-{prerelease} <{major}.{minor}.{Increment(patch)}-0"
                    : $">={major}.{minor}.{patch}-{prerelease} <{major}.{Increment(minor)}.0-0";
            }

            return $">={major}.{minor}.{patch}-{prerelease} <{Increment(major)}.0.0-0";
        }

        if (string.Equals(major, "0", StringComparison.Ordinal))
        {
            return string.Equals(minor, "0", StringComparison.Ordinal)
                ? $">={major}.{minor}.{patch} <{major}.{minor}.{Increment(patch)}-0"
                : $">={major}.{minor}.{patch} <{major}.{Increment(minor)}.0-0";
        }

        return $">={major}.{minor}.{patch} <{Increment(major)}.0.0-0";
    });

    private static string ReplaceTildes(string comp)
        => string.Join(' ', Regex.Split(comp.Trim(), "\\s+").Select(ReplaceTilde));

    private static string ReplaceTilde(string comp) => TildeRegex.Replace(comp, match =>
    {
        var major = match.Groups[1].Value;
        var minor = match.Groups[2].Value;
        var patch = match.Groups[3].Value;
        var prerelease = match.Groups[4].Value;

        if (IsX(major))
        {
            return "";
        }

        if (IsX(minor))
        {
            return $">={major}.0.0 <{Increment(major)}.0.0-0";
        }

        if (IsX(patch))
        {
            return $">={major}.{minor}.0 <{major}.{Increment(minor)}.0-0";
        }

        return prerelease.Length > 0
            ? $">={major}.{minor}.{patch}-{prerelease} <{major}.{Increment(minor)}.0-0"
            : $">={major}.{minor}.{patch} <{major}.{Increment(minor)}.0-0";
    });

    private static string ReplaceXRanges(string comp)
        => string.Join(' ', Regex.Split(comp, "\\s+").Select(ReplaceXRange));

    private static string ReplaceXRange(string comp) => XRangeRegex.Replace(comp.Trim(), match =>
    {
        var op = match.Groups[1].Value;
        var major = match.Groups[2].Value;
        var minor = match.Groups[3].Value;
        var patch = match.Groups[4].Value;

        var xMajor = IsX(major);
        var xMinor = xMajor || IsX(minor);
        var xPatch = xMinor || IsX(patch);
        var anyX = xPatch;

        if (string.Equals(op, "=", StringComparison.Ordinal) && anyX)
        {
            op = "";
        }

        if (xMajor)
        {
            // Nothing is allowed for `>x`/`<x`; everything is allowed otherwise.
            return op is ">" or "<" ? "<0.0.0-0" : "*";
        }

        if (op.Length > 0 && anyX)
        {
            // The patch is known to be an x, because some x is present.
            if (xMinor)
            {
                minor = "0";
            }

            patch = "0";

            if (string.Equals(op, ">", StringComparison.Ordinal))
            {
                op = ">=";
                if (xMinor)
                {
                    major = Increment(major);
                    minor = "0";
                    patch = "0";
                }
                else
                {
                    minor = Increment(minor);
                    patch = "0";
                }
            }
            else if (string.Equals(op, "<=", StringComparison.Ordinal))
            {
                // `<=0.7.x` is `<0.8.0`, since any 0.7.x should pass; likewise `<=7.x` is `<8.0.0`.
                op = "<";
                if (xMinor)
                {
                    major = Increment(major);
                }
                else
                {
                    minor = Increment(minor);
                }
            }

            var suffix = string.Equals(op, "<", StringComparison.Ordinal) ? "-0" : "";
            return $"{op}{major}.{minor}.{patch}{suffix}";
        }

        if (xMinor)
        {
            return $">={major}.0.0 <{Increment(major)}.0.0-0";
        }

        if (xPatch)
        {
            return $">={major}.{minor}.0 <{major}.{Increment(minor)}.0-0";
        }

        return match.Value;
    });

    private static string ReplaceStars(string comp) => StarRegex.Replace(comp.Trim(), "");

    private static string ReplaceGte0(string comp) => Gte0Regex.Replace(comp.Trim(), "");

    private static string HyphenReplace(Match match)
    {
        var from = match.Groups[1].Value;
        var fromMajor = match.Groups[2].Value;
        var fromMinor = match.Groups[3].Value;
        var fromPatch = match.Groups[4].Value;

        var to = match.Groups[7].Value;
        var toMajor = match.Groups[8].Value;
        var toMinor = match.Groups[9].Value;
        var toPatch = match.Groups[10].Value;
        var toPrerelease = match.Groups[11].Value;

        if (IsX(fromMajor))
        {
            from = "";
        }
        else if (IsX(fromMinor))
        {
            from = $">={fromMajor}.0.0";
        }
        else if (IsX(fromPatch))
        {
            from = $">={fromMajor}.{fromMinor}.0";
        }
        else
        {
            from = $">={from}";
        }

        if (IsX(toMajor))
        {
            to = "";
        }
        else if (IsX(toMinor))
        {
            to = $"<{Increment(toMajor)}.0.0-0";
        }
        else if (IsX(toPatch))
        {
            to = $"<{toMajor}.{Increment(toMinor)}.0-0";
        }
        else if (toPrerelease.Length > 0)
        {
            to = $"<={toMajor}.{toMinor}.{toPatch}-{toPrerelease}";
        }
        else
        {
            to = $"<={to}";
        }

        return $"{from} {to}".Trim();
    }

    // ---- classes/comparator.js ----

    private sealed class Comparator
    {
        public Comparator(string comp)
        {
            var match = ComparatorRegex.Match(comp);
            if (!match.Success)
            {
                throw new ArgumentException($"Invalid comparator: {comp}", nameof(comp));
            }

            var op = match.Groups[1].Success ? match.Groups[1].Value : "";
            Operator = string.Equals(op, "=", StringComparison.Ordinal) ? "" : op;

            var versionText = match.Groups[2].Value;
            if (versionText.Length == 0)
            {
                IsAny = true;
            }
            else
            {
                Version = Semver.Parse(versionText)
                    ?? throw new ArgumentException($"Invalid comparator: {comp}", nameof(comp));
            }

            Value = IsAny ? "" : Operator + Version!.Version;
        }

        public string Operator { get; } = "";

        public Semver.ParsedVersion? Version { get; }

        public bool IsAny { get; }

        public string Value { get; }

        public bool Test(Semver.ParsedVersion version)
        {
            if (IsAny)
            {
                return true;
            }

            return Compare(version, Operator, Version!);
        }
    }

    /// <summary>semver's <c>cmp</c>.</summary>
    private static bool Compare(Semver.ParsedVersion a, string op, Semver.ParsedVersion b) => op switch
    {
        "" or "=" or "==" => Semver.CompareParsed(a, b) == 0,
        "!=" => Semver.CompareParsed(a, b) != 0,
        ">" => Semver.CompareParsed(a, b) > 0,
        ">=" => Semver.CompareParsed(a, b) >= 0,
        "<" => Semver.CompareParsed(a, b) < 0,
        "<=" => Semver.CompareParsed(a, b) <= 0,
        _ => throw new ArgumentException($"Invalid operator: {op}", nameof(op)),
    };

    // ---- classes/range.js ----

    private sealed class Range
    {
        private readonly List<List<Comparator>> _set;

        public Range(string range)
        {
            Raw = WhitespaceRunRegex.Replace(range.Trim(), " ");

            var sets = new List<List<Comparator>>();
            foreach (var part in Raw.Split("||"))
            {
                var parsed = ParseRange(part.Trim());
                if (parsed.Count > 0)
                {
                    sets.Add(parsed);
                }
            }

            if (sets.Count == 0)
            {
                throw new ArgumentException($"Invalid SemVer Range: {Raw}", nameof(range));
            }

            // If any set is not the null set, drop the null sets. If all of them were, keep the first.
            if (sets.Count > 1)
            {
                var first = sets[0];
                sets = sets.Where(set => !IsNullSet(set[0])).ToList();
                if (sets.Count == 0)
                {
                    sets = [first];
                }
                else if (sets.Count > 1)
                {
                    // A `*` set swallows every other set.
                    foreach (var set in sets)
                    {
                        if (set.Count == 1 && IsAny(set[0]))
                        {
                            sets = [set];
                            break;
                        }
                    }
                }
            }

            _set = sets;
        }

        public string Raw { get; }

        /// <summary>semver's <c>Range#range</c> getter: the normalized range string.</summary>
        public string Formatted
        {
            get
            {
                var builder = new StringBuilder();
                for (var i = 0; i < _set.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append("||");
                    }

                    var comparators = _set[i];
                    for (var k = 0; k < comparators.Count; k++)
                    {
                        if (k > 0)
                        {
                            builder.Append(' ');
                        }

                        builder.Append(comparators[k].Value.Trim());
                    }
                }

                return builder.ToString();
            }
        }

        public bool Test(string version)
        {
            if (version.Length == 0)
            {
                return false;
            }

            var parsed = Semver.Parse(version);
            if (parsed is null)
            {
                return false;
            }

            foreach (var set in _set)
            {
                if (TestSet(set, parsed))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TestSet(List<Comparator> set, Semver.ParsedVersion version)
        {
            foreach (var comparator in set)
            {
                if (!comparator.Test(version))
                {
                    return false;
                }
            }

            if (version.Prerelease.Count > 0)
            {
                // A prerelease may only satisfy a set that names a prerelease on the same triple:
                // `^1.2.3-pr.1` desugars to `>=1.2.3-pr.1 <2.0.0` and admits `1.2.3-pr.2`, but
                // `1.2.4-alpha` stays out even though the comparators cover it.
                foreach (var comparator in set)
                {
                    if (comparator.IsAny || comparator.Version!.Prerelease.Count == 0)
                    {
                        continue;
                    }

                    var allowed = comparator.Version;
                    if (allowed.Major == version.Major && allowed.Minor == version.Minor && allowed.Patch == version.Patch)
                    {
                        return true;
                    }
                }

                return false;
            }

            return true;
        }

        private static List<Comparator> ParseRange(string range)
        {
            // `1.2.3 - 1.2.4` => `>=1.2.3 <=1.2.4`
            range = HyphenRangeRegex.Replace(range, HyphenReplace);

            // `> 1.2.3 < 1.2.5` => `>1.2.3 <1.2.5`
            range = ComparatorTrimRegex.Replace(range, "$1$2$3");

            // `~ 1.2.3` => `~1.2.3`, `^ 1.2.3` => `^1.2.3`
            range = TildeTrimRegex.Replace(range, "$1~");
            range = CaretTrimRegex.Replace(range, "$1^");

            // Normalize spaces, then split into comparators.
            range = WhitespaceRunRegex.Replace(range, " ");

            var rangeList = new List<string>();
            foreach (var comp in range.Split(' '))
            {
                foreach (var piece in Regex.Split(ParseComparator(comp), "\\s+"))
                {
                    rangeList.Add(ReplaceGte0(piece));
                }
            }

            var order = new List<string>();
            var map = new Dictionary<string, Comparator>(StringComparer.Ordinal);
            foreach (var comp in rangeList.Select(text => new Comparator(text)))
            {
                if (IsNullSet(comp))
                {
                    return [comp];
                }

                if (!map.ContainsKey(comp.Value))
                {
                    order.Add(comp.Value);
                }

                map[comp.Value] = comp;
            }

            if (map.Count > 1 && map.ContainsKey(""))
            {
                map.Remove("");
                order.Remove("");
            }

            return order.Select(key => map[key]).ToList();
        }

        private static bool IsNullSet(Comparator comparator) => comparator.Value == "<0.0.0-0";

        private static bool IsAny(Comparator comparator) => comparator.Value.Length == 0;
    }
}
