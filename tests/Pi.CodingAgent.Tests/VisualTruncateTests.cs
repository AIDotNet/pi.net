// ============================================================================
// Tests for modes/interactive/components/visual-truncate.ts
// ============================================================================

using Pi.CodingAgent.Modes.Interactive.Components;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class VisualTruncateTests
{
    // ------------------------------------------------------------------ TruncateToVisualLines

    [Fact]
    public void TruncateToVisualLines_EmptyText_ReturnsEmpty()
    {
        var result = VisualTruncate.TruncateToVisualLines("", 3, 20);
        Assert.Empty(result.VisualLines);
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void TruncateToVisualLines_WhitespaceOnlyText_ReturnsEmpty()
    {
        var result = VisualTruncate.TruncateToVisualLines("   ", 3, 20);
        Assert.Empty(result.VisualLines);
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void TruncateToVisualLines_TextThatFits_KeepsEverything()
    {
        var result = VisualTruncate.TruncateToVisualLines("a\nb\nc", 5, 20);
        Assert.Equal(3, result.VisualLines.Count);
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void TruncateToVisualLines_TrimsToTheLastLinesByDefault()
    {
        var result = VisualTruncate.TruncateToVisualLines("1\n2\n3\n4\n5", 2, 20);
        Assert.Equal(2, result.VisualLines.Count);
        Assert.Equal(3, result.SkippedCount);
        Assert.StartsWith("4", result.VisualLines[0]);
        Assert.StartsWith("5", result.VisualLines[1]);
    }

    [Fact]
    public void TruncateToVisualLines_KeepStart_TrimsToTheFirstLines()
    {
        var result = VisualTruncate.TruncateToVisualLines("1\n2\n3\n4\n5", 2, 20, 0, "start");
        Assert.Equal(2, result.VisualLines.Count);
        Assert.Equal(3, result.SkippedCount);
        Assert.StartsWith("1", result.VisualLines[0]);
        Assert.StartsWith("2", result.VisualLines[1]);
    }

    [Fact]
    public void TruncateToVisualLines_CountsWrappedVisualLinesNotLogicalLines()
    {
        // One logical line of 10 chars wraps to three visual lines at width 4.
        var result = VisualTruncate.TruncateToVisualLines("aaaaaaaaaa", 2, 4);
        Assert.Equal(2, result.VisualLines.Count);
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public void TruncateToVisualLines_AppliesHorizontalPadding()
    {
        var result = VisualTruncate.TruncateToVisualLines("hello", 5, 20, 2);
        Assert.Single(result.VisualLines);
        Assert.StartsWith("  hello", result.VisualLines[0]);
    }

    // ------------------------------------------------------------------ VisualLinePreview

    [Fact]
    public void VisualLinePreview_PutsTheHintAfterStartKeptLines()
    {
        var preview = new VisualLinePreview(new VisualLinePreviewOptions
        {
            Text = "1\n2\n3\n4\n5",
            MaxVisualLines = 2,
            Keep = "start",
            FormatHint = hidden => $"... {hidden} hidden",
        });

        var lines = preview.Render(20);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("1", lines[0]);
        Assert.StartsWith("2", lines[1]);
        Assert.StartsWith("... 3 hidden", lines[2]);
    }

    [Fact]
    public void VisualLinePreview_PutsTheHintBeforeEndKeptLines()
    {
        var preview = new VisualLinePreview(new VisualLinePreviewOptions
        {
            Text = "1\n2\n3\n4\n5",
            MaxVisualLines = 2,
            Keep = "end",
            FormatHint = hidden => $"... {hidden} hidden",
        });

        var lines = preview.Render(20);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("... 3 hidden", lines[0]);
        Assert.StartsWith("4", lines[1]);
        Assert.StartsWith("5", lines[2]);
    }

    [Fact]
    public void VisualLinePreview_NoHintWhenNothingIsHidden()
    {
        var preview = new VisualLinePreview(new VisualLinePreviewOptions
        {
            Text = "a\nb",
            MaxVisualLines = 5,
            Keep = "end",
            FormatHint = _ => "unused",
        });

        var lines = preview.Render(20);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void VisualLinePreview_CachesPerWidth()
    {
        var preview = new VisualLinePreview(new VisualLinePreviewOptions
        {
            Text = "1\n2\n3\n4\n5",
            MaxVisualLines = 2,
            Keep = "end",
            FormatHint = hidden => $"... {hidden} hidden",
        });

        var atTwenty = preview.Render(20);
        Assert.Same(atTwenty, preview.Render(20));

        var atThirty = preview.Render(30);
        Assert.NotSame(atTwenty, atThirty);
    }

    [Fact]
    public void VisualLinePreview_InvalidateClearsTheCache()
    {
        var preview = new VisualLinePreview(new VisualLinePreviewOptions
        {
            Text = "1\n2\n3",
            MaxVisualLines = 1,
            Keep = "end",
            FormatHint = hidden => $"... {hidden} hidden",
        });

        var before = preview.Render(20);
        preview.Invalidate();
        Assert.NotSame(before, preview.Render(20));
    }

    [Fact]
    public void VisualLinePreview_TruncatesTheHintToTheWidth()
    {
        var preview = new VisualLinePreview(new VisualLinePreviewOptions
        {
            Text = "1\n2\n3",
            MaxVisualLines = 1,
            Keep = "end",
            FormatHint = _ => new string('x', 50),
        });

        var lines = preview.Render(10);
        Assert.Equal(10, Pi.Tui.UnicodeWidth.VisibleWidth(lines[0]));
        Assert.Contains("...", lines[0]);
    }
}
