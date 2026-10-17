// ============================================================================
// Mermaid code-block rendering for assistant markdown.
// Port of modes/interactive/components/mermaid.ts
//
// The transformer skeleton is ported in full: mode gating, thinking-block skip, streaming
// rule, oversize fallback, the warning summary and the per-row inline-code encoding.
//
// The rendering engine itself (grok-mermaid) is NOT ported. It is a third-party package
// (508 KB / 64 files) that is not part of the pi repository, and the reference checkout has
// an empty node_modules, so the upstream test vectors (which assert real box-drawing output)
// cannot be reproduced here. `render()` is therefore an injected seam: with the default
// renderer every block falls back to its raw text, which is the same code path upstream takes
// when `render` returns null (unsupported diagram, syntax error, layout refused).
// ============================================================================

using System.Text;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Core.Extensions;
using Pi.Tui.Marked;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Semantic class of a run of cells. TS <c>Cls</c> — a closed string union in grok-mermaid.
/// </summary>
public enum MermaidSpanClass
{
    /// <summary>Box outlines, subgraph frames, compartment rules.</summary>
    Border,

    /// <summary>Node / participant / compartment labels.</summary>
    Text,

    /// <summary>Connector lines and arrowheads.</summary>
    Edge,

    /// <summary>Text sitting on an edge.</summary>
    EdgeLabel,

    /// <summary>The <c>mermaid: &lt;kind&gt;</c> header of a source box.</summary>
    Title,

    /// <summary>Blank filler.</summary>
    None,
}

/// <summary>A run of adjacent cells sharing one semantic class. TS <c>Span</c>.</summary>
public sealed record MermaidSpan
{
    public required string Text { get; init; }

    public required MermaidSpanClass Cls { get; init; }
}

/// <summary>
/// A rendered diagram. TS <c>MermaidArt</c>.
///
/// <see cref="Plain"/> and <see cref="Styled"/> describe the same rows: <see cref="Plain"/> is
/// right-trimmed for display width, <see cref="Styled"/> keeps the run structure needed to colour
/// it. <see cref="Width"/> is the display columns the widest row needs — the number compared
/// against the space at hand; it cannot be recovered from <see cref="Plain"/>, whose rows are
/// strings of code points, not columns.
/// </summary>
public sealed record MermaidArt
{
    public required IReadOnlyList<string> Plain { get; init; }

    public required IReadOnlyList<IReadOnlyList<MermaidSpan>> Styled { get; init; }

    public required int Width { get; init; }

    /// <summary>
    /// Source the flowchart grammar could not read and dropped. Non-empty means the art is real
    /// but incomplete; the transformer only surfaces these once streaming has finished.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// The <c>render(src)</c> seam. TS imports <c>render</c> from <c>grok-mermaid</c>; this port does
/// not ship that engine, so callers inject one. Returning <c>null</c> means "no art to show" —
/// blank input, syntax error, an unsupported diagram kind, or a layout the renderer refused.
/// </summary>
public interface IMermaidRenderer
{
    MermaidArt? Render(string source);
}

/// <summary>
/// The slice of the theme the transformer needs. TS reads the module-level <c>theme</c> singleton
/// through two structural calls; this seam exists so the tests can replay them with deterministic
/// markers instead of resolving terminal colors.
/// </summary>
internal interface IMermaidTheme
{
    string Fg(string token, string text);

    string Bold(string text);
}

/// <summary>Options for <see cref="Mermaid.CreateMarkdownTransformer"/>. TS <c>MermaidTransformerOptions</c>.</summary>
public sealed record MermaidTransformerOptions
{
    /// <summary>Read lazily so a settings change applies to the next transform.</summary>
    public required Func<string> GetMode { get; init; }

    /// <summary>Null renders diagrams without theme styling (TS <c>options.theme</c> undefined).</summary>
    public Theme? Theme { get; init; }

    /// <summary>
    /// Injected <c>render</c> implementation. Null means no engine is available: every Mermaid
    /// block is left untouched.
    /// </summary>
    public IMermaidRenderer? Renderer { get; init; }
}

/// <summary>Port of <c>mermaid.ts</c>.</summary>
public static partial class Mermaid
{
    /// <summary>marked's whitespace run, used to split a fenced block's info string.</summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>Any run of backticks (TS <c>/`+/g</c>).</summary>
    [GeneratedRegex("`+")]
    private static partial Regex BacktickRunRegex();

    private sealed class RuntimeTheme(Theme theme) : IMermaidTheme
    {
        public string Fg(string token, string text) => theme.Fg(token, text);

        public string Bold(string text) => theme.Bold(text);
    }

    /// <summary>
    /// Create a transformer that replaces top-level Mermaid code blocks with Unicode terminal
    /// diagrams. TS <c>createMermaidMarkdownTransformer(options)</c>.
    /// </summary>
    public static MarkdownTransformer CreateMarkdownTransformer(MermaidTransformerOptions options) =>
        CreateMarkdownTransformer(options, options.Theme is { } theme ? new RuntimeTheme(theme) : null);

    /// <summary>The body, with the theme injected (test seam).</summary>
    internal static MarkdownTransformer CreateMarkdownTransformer(
        MermaidTransformerOptions options,
        IMermaidTheme? theme) =>
        (markdown, context) =>
        {
            var mode = options.GetMode();
            if (mode == "off" ||
                context.MessageType == "assistant-thinking" ||
                (context.IsStreaming && mode != "streaming"))
            {
                return markdown;
            }

            var builder = new StringBuilder();
            foreach (var token in MarkedLexer.Lex(markdown))
            {
                if (!IsMermaid(token))
                {
                    builder.Append(token.Raw);
                    continue;
                }

                var art = options.Renderer?.Render(token.Text ?? "");
                if (art is null || art.Width > context.AvailableWidth)
                {
                    builder.Append(token.Raw);
                    continue;
                }

                // Warnings are advisory: they only replace the art once the block has settled.
                // A diagram being typed warns at nearly every intermediate state, and streaming
                // output is not the place to say so.
                if (!context.IsStreaming && art.Warnings.Count > 0)
                {
                    var suffix = art.Warnings.Count > 1 ? $" (+{art.Warnings.Count - 1} more)" : "";
                    var warning = $"Mermaid diagram not rendered: {art.Warnings[0]}{suffix}";
                    var styledWarning = theme is not null ? theme.Fg("warning", warning) : warning;
                    builder.Append(token.Raw).Append('\n').Append(CodeSpan(styledWarning)).Append("  \n");
                    continue;
                }

                var lines = theme is not null ? ThemedLines(art, theme) : art.Plain;
                // Markdown hard breaks keep every diagram row on its own line.
                builder.Append(string.Join("  \n", lines.Select(CodeSpan))).Append('\n');
            }

            return builder.ToString();
        };

    /// <summary>
    /// TS <c>isMermaid(token)</c>: a fenced code block whose info string starts with "mermaid"
    /// (case-insensitive, extra words allowed).
    /// </summary>
    internal static bool IsMermaid(MarkedToken token)
    {
        if (token.Type != "code" || token.Lang is null)
        {
            return false;
        }

        // TS: token.lang.trim().split(/\s+/, 1)[0].toLowerCase() === "mermaid".
        // A limit-1 JS split still yields one element, so an empty info string lands on "".
        var first = WhitespaceRegex().Split(token.Lang.Trim(), 2)[0];
        return string.Equals(first.ToLowerInvariant(), "mermaid", StringComparison.Ordinal);
    }

    /// <summary>
    /// TS <c>codeSpan(line)</c>: encode one diagram row as inline code so Markdown preserves its
    /// spacing and box-drawing characters.
    /// </summary>
    internal static string CodeSpan(string line)
    {
        // A blank row needs a non-breaking space: an empty code span has no visible height.
        var content = line.Length == 0 ? "\u00a0" : line;

        // CommonMark code spans use matching backtick delimiters, so pick one longer than any
        // backtick run in the content (``hel`lo`` -> <code>hel`lo</code>). When the content starts
        // or ends with a backtick, a space separates it from the delimiter and CommonMark strips
        // the padding again when rendering (`` `edge` `` -> <code>`edge`</code>).
        var longestBacktickRun = 0;
        foreach (Match match in BacktickRunRegex().Matches(content))
        {
            if (match.Length > longestBacktickRun)
            {
                longestBacktickRun = match.Length;
            }
        }

        var fence = new string('`', longestBacktickRun + 1);
        var padding = content.StartsWith('`') || content.EndsWith('`') ? " " : "";
        return $"{fence}{padding}{content}{padding}{fence}";
    }

    /// <summary>TS <c>styleSpan(span, theme)</c>: map a semantic class onto the Pi theme.</summary>
    internal static string StyleSpan(MermaidSpan span, IMermaidTheme theme) => span.Cls switch
    {
        MermaidSpanClass.Border => theme.Fg("borderMuted", span.Text),
        MermaidSpanClass.Text => theme.Fg("text", span.Text),
        MermaidSpanClass.Edge => theme.Fg("accent", span.Text),
        MermaidSpanClass.EdgeLabel => theme.Fg("muted", span.Text),
        MermaidSpanClass.Title => theme.Fg("accent", theme.Bold(span.Text)),
        // TS's switch is exhaustive over the closed union; the fallback is unreachable.
        _ => span.Text,
    };

    /// <summary>TS <c>themedLines(art, theme)</c>.</summary>
    internal static List<string> ThemedLines(MermaidArt art, IMermaidTheme theme) =>
        [.. art.Styled.Select(row => string.Concat(row.Select(span => StyleSpan(span, theme))))];
}
