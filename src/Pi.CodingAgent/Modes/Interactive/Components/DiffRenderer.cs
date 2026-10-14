// ============================================================================
// Diff rendering with intra-line word-level highlighting.
// Port of modes/interactive/components/diff.ts
// ============================================================================

using System.Text;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Modes.Interactive;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>Options for <see cref="DiffRenderer.RenderDiff"/>. TS <c>RenderDiffOptions</c>.</summary>
public sealed record RenderDiffOptions
{
    /// <summary>File path (unused, kept for API compatibility).</summary>
    public string? FilePath { get; init; }
}

/// <summary>
/// The slice of the theme the renderer needs. The TS module reads the module-level <c>theme</c>
/// singleton directly; this seam exists so the differential corpus (which stubs the same two calls with
/// deterministic markers) can be replayed without dragging terminal color resolution into the test.
/// </summary>
internal interface IRenderDiffTheme
{
    string Fg(string token, string text);

    string Inverse(string text);
}

/// <summary>Port of <c>diff.ts</c>.</summary>
public static partial class DiffRenderer
{
    private sealed class RuntimeTheme : IRenderDiffTheme
    {
        public string Fg(string token, string text) => ThemeRuntime.Current.Fg(token, text);

        public string Inverse(string text) => ThemeRuntime.Current.Inverse(text);
    }

    private static readonly IRenderDiffTheme Runtime = new RuntimeTheme();

    /// <summary>
    /// Render a diff string with colored lines and intra-line change highlighting.
    /// <list type="bullet">
    /// <item>Context lines: dim/gray</item>
    /// <item>Removed lines: red, with inverse on changed tokens</item>
    /// <item>Added lines: green, with inverse on changed tokens</item>
    /// </list>
    /// </summary>
    public static string RenderDiff(string diffText, RenderDiffOptions? options = null) =>
        RenderDiff(diffText, options, Runtime);

    /// <summary>The body, with the theme injected (test seam for the differential corpus).</summary>
    internal static string RenderDiff(string diffText, RenderDiffOptions? options, IRenderDiffTheme theme)
    {
        _ = options; // TS keeps `_options` for API compatibility and never reads it.
        var lines = diffText.Split('\n');
        var result = new List<string>();

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var parsed = ParseDiffLine(line);

            if (parsed is null)
            {
                result.Add(theme.Fg("toolDiffContext", line));
                i++;
                continue;
            }

            if (parsed.Prefix == "-")
            {
                // Collect consecutive removed lines
                var removedLines = new List<DiffLineParts>();
                while (i < lines.Length)
                {
                    var p = ParseDiffLine(lines[i]);
                    if (p is null || p.Prefix != "-") break;
                    removedLines.Add(p);
                    i++;
                }

                // Collect consecutive added lines
                var addedLines = new List<DiffLineParts>();
                while (i < lines.Length)
                {
                    var p = ParseDiffLine(lines[i]);
                    if (p is null || p.Prefix != "+") break;
                    addedLines.Add(p);
                    i++;
                }

                // Only do intra-line diffing when there's exactly one removed and one added line
                // (indicating a single line modification). Otherwise, show lines as-is.
                if (removedLines.Count == 1 && addedLines.Count == 1)
                {
                    var removed = removedLines[0];
                    var added = addedLines[0];

                    var (removedLine, addedLine) = RenderIntraLineDiff(
                        ReplaceTabs(removed.Content),
                        ReplaceTabs(added.Content),
                        theme);

                    result.Add(theme.Fg("toolDiffRemoved", $"-{removed.LineNum} {removedLine}"));
                    result.Add(theme.Fg("toolDiffAdded", $"+{added.LineNum} {addedLine}"));
                }
                else
                {
                    // Show all removed lines first, then all added lines
                    foreach (var removed in removedLines)
                    {
                        result.Add(theme.Fg("toolDiffRemoved", $"-{removed.LineNum} {ReplaceTabs(removed.Content)}"));
                    }

                    foreach (var added in addedLines)
                    {
                        result.Add(theme.Fg("toolDiffAdded", $"+{added.LineNum} {ReplaceTabs(added.Content)}"));
                    }
                }
            }
            else if (parsed.Prefix == "+")
            {
                // Standalone added line
                result.Add(theme.Fg("toolDiffAdded", $"+{parsed.LineNum} {ReplaceTabs(parsed.Content)}"));
                i++;
            }
            else
            {
                // Context line
                result.Add(theme.Fg("toolDiffContext", $" {parsed.LineNum} {ReplaceTabs(parsed.Content)}"));
                i++;
            }
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Compute word-level diff and render with inverse on changed parts. Uses
    /// <see cref="JsDiff.DiffWords"/> which groups whitespace with adjacent words for cleaner
    /// highlighting. Strips leading whitespace from the first inverse span to avoid highlighting
    /// indentation.
    /// </summary>
    private static (string RemovedLine, string AddedLine) RenderIntraLineDiff(
        string oldContent, string newContent, IRenderDiffTheme theme)
    {
        var wordDiff = JsDiff.DiffWords(oldContent, newContent);

        var removedLine = new StringBuilder();
        var addedLine = new StringBuilder();
        var isFirstRemoved = true;
        var isFirstAdded = true;

        foreach (var part in wordDiff)
        {
            if (part.Removed)
            {
                var value = part.Value;
                // Strip leading whitespace from the first removed part
                if (isFirstRemoved)
                {
                    var leadingWs = LeadingJsWhitespace(value);
                    value = value[leadingWs.Length..];
                    removedLine.Append(leadingWs);
                    isFirstRemoved = false;
                }

                if (value.Length > 0)
                {
                    removedLine.Append(theme.Inverse(value));
                }
            }
            else if (part.Added)
            {
                var value = part.Value;
                // Strip leading whitespace from the first added part
                if (isFirstAdded)
                {
                    var leadingWs = LeadingJsWhitespace(value);
                    value = value[leadingWs.Length..];
                    addedLine.Append(leadingWs);
                    isFirstAdded = false;
                }

                if (value.Length > 0)
                {
                    addedLine.Append(theme.Inverse(value));
                }
            }
            else
            {
                removedLine.Append(part.Value);
                addedLine.Append(part.Value);
            }
        }

        return (removedLine.ToString(), addedLine.ToString());
    }

    /// <summary>Parse diff line to extract prefix, line number, and content.</summary>
    internal static DiffLineParts? ParseDiffLine(string line)
    {
        var match = DiffLinePattern().Match(line);
        if (!match.Success) return null;
        return new DiffLineParts(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
    }

    /// <summary>Replace tabs with spaces for consistent rendering. TS <c>/\t/g → "   "</c>.</summary>
    internal static string ReplaceTabs(string text) => text.Replace("\t", "   ");

    /// <summary>
    /// The JS <c>/^([+-\s])(\s*\d*)\s(.*)$/</c> as a .NET pattern. Three JS/.NET differences had to be
    /// spelled out rather than relying on the shared syntax:
    /// <list type="bullet">
    /// <item><c>^</c>/<c>$</c> in JS are string anchors (no <c>m</c> flag) and <c>$</c> does not also
    /// match before a trailing newline the way .NET's does — so <c>\A</c>/<c>\z</c> are used.</item>
    /// <item>JS <c>.</c> refuses the four line terminators (<c>\n</c>, <c>\r</c>, <c>\u2028</c>,
    /// <c>\u2029</c>); .NET's <c>.</c> refuses only <c>\n</c>. A line that arrived with a stray
    /// <c>\r</c> therefore stops matching here, exactly as upstream, instead of silently parsing.</item>
    /// <item>JS <c>\s</c> is the ECMAScript WhiteSpace+LineTerminator set and <c>\d</c> is ASCII-only;
    /// .NET's <c>\s</c> swaps <c>\u0085</c> in for <c>\uFEFF</c>, and its <c>\d</c> accepts every
    /// Unicode decimal digit. Both are written out explicitly.</item>
    /// </list>
    /// </summary>
    [GeneratedRegex(
        "\\A([+" + JsWhitespaceClassBody + "\\-])(" +
        "[" + JsWhitespaceClassBody + "]*[0-9]*)" +
        "[" + JsWhitespaceClassBody + "]" +
        "([^\\n\\r\\u2028\\u2029]*)\\z")]
    private static partial Regex DiffLinePattern();

    /// <summary>The body of a JS <c>\s</c> character class, so it can be dropped into a .NET pattern.</summary>
    private const string JsWhitespaceClassBody =
        "\\u0009\\u000A\\u000B\\u000C\\u000D\\u0020\\u00A0\\u1680\\u2000-\\u200A" +
        "\\u2028\\u2029\\u202F\\u205F\\u3000\\uFEFF";

    /// <summary>The JS <c>/^(\s*)/</c> leading run, using the JS <c>\s</c> set.</summary>
    private static string LeadingJsWhitespace(string value)
    {
        var end = 0;
        while (end < value.Length && IsJsWhitespace(value[end])) end++;
        return value[..end];
    }

    private static bool IsJsWhitespace(char ch) => ch switch
    {
        '\u0009' or '\u000A' or '\u000B' or '\u000C' or '\u000D' or '\u0020' => true,
        '\u00A0' or '\u1680' => true,
        >= '\u2000' and <= '\u200A' => true,
        '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' => true,
        '\uFEFF' => true,
        _ => false,
    };
}

/// <summary>
/// A parsed diff line. TS <c>{ prefix, lineNum, content }</c>; <c>prefix</c> is kept as a string because
/// it may be any JS whitespace character, not just a space.
/// </summary>
internal sealed record DiffLineParts(string Prefix, string LineNum, string Content);
