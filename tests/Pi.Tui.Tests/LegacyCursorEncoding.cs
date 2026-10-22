namespace Pi.Tui.Tests;

/// <summary>
/// Deviation T33: the <c>editor-corpus.json</c>, <c>input-corpus.json</c>,
/// <c>extra-components-corpus.json</c> and <c>alt-screen-search-corpus.json</c> vectors were recorded
/// before the fake cursor existed, when <see cref="Components.Editor"/> and
/// <see cref="Components.Input"/> wrote the reverse-video sequence inline. They now emit the
/// zero-width <see cref="Tui.FakeCursorStart"/>/<see cref="Tui.FakeCursorEnd"/> pair and leave the
/// decision to the renderer, so every corpus that embeds a rendered input translates the marker form
/// back into the recorded bytes. That keeps the vectors verifying text, wrapping, scroll offset,
/// padding and cursor placement; the marker form itself and the renderer's resolution are covered by
/// <see cref="FakeCursorTests"/>.
/// </summary>
/// <remarks>
/// <see cref="ResolvedEnd"/> is byte-identical to what the old components wrote, so the translation is
/// lossless for <see cref="Components.Input"/> and everything that embeds it. The editor is the one
/// exception: its cursor cell used to end with <c>ESC[0m</c> (full reset) and now ends with
/// <c>ESC[27m</c> (reverse video off), which is upstream's deliberate change - see
/// <c>docs/upstream-1.4.0-delta.md</c> §6.e.
/// </remarks>
internal static class LegacyCursorEncoding
{
    /// <summary>What <see cref="TuiBase.ResolveFakeCursors"/> emits, and what the old input wrote.</summary>
    internal const string ResolvedEnd = "\x1b[27m";

    /// <summary>The editor's former cursor cell ended with a full reset instead.</summary>
    internal const string EditorLegacyEnd = "\x1b[0m";

    internal static string Resolve(string line, string endSequence = ResolvedEnd) =>
        line.Replace(Tui.FakeCursorStart, "\x1b[7m", StringComparison.Ordinal)
            .Replace(Tui.FakeCursorEnd, endSequence, StringComparison.Ordinal);

    internal static string[] Resolve(string[] lines, string endSequence = ResolvedEnd) =>
        Array.ConvertAll(lines, line => Resolve(line, endSequence));

    internal static IEnumerable<string> Resolve(IEnumerable<string> lines, string endSequence = ResolvedEnd) =>
        lines.Select(line => Resolve(line, endSequence));
}
