// ============================================================================
// Shared utility for truncating text to visual lines (accounting for wrapping).
// Port of modes/interactive/components/visual-truncate.ts
// ============================================================================

using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>Result of <see cref="VisualTruncate.TruncateToVisualLines"/>. TS <c>VisualTruncateResult</c>.</summary>
public sealed record VisualTruncateResult
{
    /// <summary>The visual lines to display.</summary>
    public required IReadOnlyList<string> VisualLines { get; init; }

    /// <summary>Number of visual lines that were skipped (hidden).</summary>
    public required int SkippedCount { get; init; }
}

/// <summary>Port of <c>visual-truncate.ts</c>.</summary>
public static class VisualTruncate
{
    /// <summary>
    /// Truncate text to a maximum number of visual lines, accounting for line wrapping at
    /// <paramref name="width"/>. <paramref name="keep"/> selects which visual lines survive:
    /// the literal <c>"start"</c> keeps the first ones, anything else keeps the last ones (matching the
    /// TS <c>"start" | "end"</c> literal union, whose only special value is <c>"start"</c>).
    /// </summary>
    public static VisualTruncateResult TruncateToVisualLines(
        string text,
        int maxVisualLines,
        int width,
        int paddingX = 0,
        string keep = "end")
    {
        if (string.IsNullOrEmpty(text))
        {
            return new VisualTruncateResult { VisualLines = Array.Empty<string>(), SkippedCount = 0 };
        }

        // A temporary Text component renders the wrapped lines exactly as the transcript will.
        var tempText = new Text(text, paddingX, 0);
        var allVisualLines = tempText.Render(width);

        if (allVisualLines.Length <= maxVisualLines)
        {
            return new VisualTruncateResult { VisualLines = allVisualLines, SkippedCount = 0 };
        }

        var truncatedLines = keep == "start"
            ? allVisualLines.Take(maxVisualLines).ToArray()
            : allVisualLines.Skip(allVisualLines.Length - maxVisualLines).ToArray();
        var skippedCount = allVisualLines.Length - maxVisualLines;

        return new VisualTruncateResult { VisualLines = truncatedLines, SkippedCount = skippedCount };
    }
}

/// <summary>Options for <see cref="VisualLinePreview"/>. TS <c>VisualLinePreviewOptions</c>.</summary>
public sealed record VisualLinePreviewOptions
{
    /// <summary>Styled text; may contain newlines.</summary>
    public required string Text { get; init; }

    public required int MaxVisualLines { get; init; }

    /// <summary>Which visual lines to keep; the hint goes before kept end lines, after kept start lines.</summary>
    public required string Keep { get; init; }

    /// <summary>Styled hint line for the given number of hidden visual lines.</summary>
    public required Func<int, string> FormatHint { get; init; }
}

/// <summary>
/// Collapsed tool output limited to a number of visual lines, like bash output. Limiting logical
/// lines instead lets a single long line (such as minified JSON) wrap across the whole screen.
/// Caches its lines per width, since it renders on every frame for every result in the transcript.
/// Port of <c>VisualLinePreview</c> in <c>visual-truncate.ts</c>.
/// </summary>
public sealed class VisualLinePreview : IComponent
{
    private readonly VisualLinePreviewOptions _options;
    private int? _cachedWidth;
    private string[]? _cachedLines;

    public VisualLinePreview(VisualLinePreviewOptions options) => _options = options;

    /// <summary>The options this preview was built with. TS <c>private options</c> (readable at runtime).</summary>
    /// <remarks>
    /// Exposed read-only so a caller can inspect a built preview — in particular the differential
    /// corpus of 4f-1, which compares the component tree a tool renderer produces.
    /// </remarks>
    public VisualLinePreviewOptions Options => _options;

    public string[] Render(int width)
    {
        if (_cachedLines is null || _cachedWidth != width)
        {
            var preview = VisualTruncate.TruncateToVisualLines(
                _options.Text, _options.MaxVisualLines, width, 0, _options.Keep);
            var lines = preview.VisualLines;
            if (preview.SkippedCount > 0)
            {
                var hint = TextLayout.TruncateToWidth(_options.FormatHint(preview.SkippedCount), width, "...");
                _cachedLines = _options.Keep == "start"
                    ? lines.Append(hint).ToArray()
                    : new[] { hint }.Concat(lines).ToArray();
            }
            else
            {
                _cachedLines = lines.ToArray();
            }

            _cachedWidth = width;
        }

        return _cachedLines;
    }

    public void Invalidate()
    {
        _cachedWidth = null;
        _cachedLines = null;
    }
}
