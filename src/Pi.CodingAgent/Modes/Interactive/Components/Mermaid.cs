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
using Pi.Tui;
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

/// <summary>
/// A run of adjacent cells sharing one semantic class. TS <c>Span</c> (lovely-mermaid, v1.5.1
/// #8158 起 span 可携带 <c>href</c> / <c>role</c>，用于给 Mermaid 标签加 OSC 8 超链接)。
/// </summary>
public sealed record MermaidSpan
{
    public required string Text { get; init; }

    public required MermaidSpanClass Cls { get; init; }

    /// <summary>该 span 的超链接目标（lovely-mermaid <c>Span.href</c>）。</summary>
    public string? Href { get; init; }

    /// <summary>该 span 的语义角色（lovely-mermaid <c>Span.role</c>，如 <c>"text"</c>）。</summary>
    public string? Role { get; init; }
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
/// <para>v1.5.1 #8158: <c>themedLines</c> now derives an <see cref="MermaidAnsiTheme"/> from
/// <c>theme.getFgAnsi(color)</c> (剥掉 <c>\x1b[</c>/<c>m</c> 的 SGR 参数串) 再交给 <c>toAnsi</c>。
/// <see cref="GetFgAnsi"/> 返回 null 时回落到旧的 <c>fg/bold</c> 包装（测试 marker 与无 ANSI 能力场景）。</para>
/// </summary>
internal interface IMermaidTheme
{
    string Fg(string token, string text);

    string Bold(string text);

    /// <summary>前景色打开序列（如 <c>\x1b[38;5;240m</c>）；null 表示主题只提供 marker 包装。</summary>
    string? GetFgAnsi(string color) => null;

    /// <summary>终端是否支持 OSC 8 超链接（TS <c>getCapabilities().hyperlinks</c>）。</summary>
    bool Hyperlinks => false;
}

/// <summary>
/// v1.5.1 #8158 <c>AnsiTheme</c>：五类语义 span 的 SGR 参数串（不含 <c>\x1b[</c> 与 <c>m</c>）。
/// 从 <c>getFgAnsi(color).slice(2,-1)</c> 派生；<c>edgeLabel</c> 用 <c>text</c> 色（v1.5.0 用
/// <c>muted</c>），<c>title</c> 前置 <c>1;</c> 加粗。
/// </summary>
public sealed record MermaidAnsiTheme(
    string Border,
    string Text,
    string Edge,
    string EdgeLabel,
    string Title)
{
    /// <summary>对应 TS <c>themedLines</c> 里 ansiTheme 的构造。</summary>
    public static MermaidAnsiTheme From(Func<string, string> getFgAnsi)
    {
        static string Params(string opening)
        {
            // getFgAnsi 形如 "\x1b[38;5;240m"；slice(2,-1) 去掉 "\x1b[" 与结尾 "m"。
            if (opening.Length >= 3 && opening[0] == '\x1b' && opening[1] == '['
                && opening[^1] == 'm')
            {
                return opening[2..^1];
            }

            return opening;
        }

        return new MermaidAnsiTheme(
            Params(getFgAnsi("borderMuted")),
            Params(getFgAnsi("text")),
            Params(getFgAnsi("accent")),
            Params(getFgAnsi("text")), // #8158：edgeLabel 从 muted 改为 text
            "1;" + Params(getFgAnsi("accent")));
    }
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

        // v1.5.1 #8158：themedLines 用 getFgAnsi 的 SGR 参数串而非 fg() 包装。
        public string GetFgAnsi(string color) => theme.GetFgAnsi(color);

        public bool Hyperlinks => TerminalImage.GetCapabilities().Hyperlinks;
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

    /// <summary>
    /// TS v1.5.0 <c>styleSpan(span, theme)</c>: map a semantic class onto the Pi theme via
    /// <c>fg()</c> 包装。v1.5.1 #8158 起生产路径改走 <see cref="ToAnsi"/>；这里保留作
    /// 「主题不提供 ANSI 能力」时的回落与既有 marker 用例。
    /// </summary>
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

    /// <summary>
    /// TS v1.5.1 #8158 <c>themedLines(art, theme) → toAnsi(displayArt, ansiTheme)</c>。
    /// 主题提供 <c>GetFgAnsi</c> 时用 SGR 参数串着色并处理 OSC 8 超链接；否则回落
    /// <see cref="StyleSpan"/> 的 marker 包装（测试 seam / 无 ANSI 能力）。
    /// </summary>
    internal static List<string> ThemedLines(MermaidArt art, IMermaidTheme theme) =>
        theme.GetFgAnsi("text") is { }
            ? ToAnsi(art, MermaidAnsiTheme.From(color => theme.GetFgAnsi(color)!), theme.Hyperlinks)
            : [.. art.Styled.Select(row => string.Concat(row.Select(span => StyleSpan(span, theme))))];

    /// <summary>
    /// v1.5.1 #8158 <c>toAnsi</c> 等价：每个 span 按语义类输出 <c>\x1b[&lt;sgr&gt;m...\x1b[39m</c>；
    /// 当 span 带 <c>href</c> 且终端支持 OSC 8 超链接时，只给 <c>role == "text"</c> 的标签加链接
    /// （其余边框/连线 span 去掉 href，避免「整个盒子都可点」）。对应 <c>displayArt</c> 的过滤。
    /// </summary>
    internal static List<string> ToAnsi(MermaidArt art, MermaidAnsiTheme theme, bool hyperlinks)
    {
        string Sgr(MermaidSpanClass cls) => cls switch
        {
            MermaidSpanClass.Border => theme.Border,
            MermaidSpanClass.Text => theme.Text,
            MermaidSpanClass.Edge => theme.Edge,
            MermaidSpanClass.EdgeLabel => theme.EdgeLabel,
            MermaidSpanClass.Title => theme.Title,
            _ => "",
        };

        return [.. art.Styled.Select(row => string.Concat(row.Select(span =>
        {
            var sgr = Sgr(span.Cls);
            var body = sgr.Length == 0 ? span.Text : $"\x1b[{sgr}m{span.Text}\x1b[39m";
            // 只对 text 角色的标签、且终端支持超链接时保留 href；否则整块不链接。
            if (span.Href is { } href && hyperlinks && span.Role == "text")
            {
                return $"\x1b]8;;{href}\x1b\\{body}\x1b]8;;\x1b\\";
            }

            return body;
        })))];
    }
}
