// ============================================================================
// Tests for modes/interactive/components/armin.ts (4f-3e Batch 3).
//
// Upstream has no test file for this component, so the vectors are derived from the source:
// the bitmap itself is checked against the XBM bits (hand-decoded), and every effect is driven
// frame by frame through the internal tick seam.
// ============================================================================

using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class ArminTests : SelectorTestFixture
{
    private static ArminComponent Create(ArminEffect effect, int seed = 1) =>
        new(new FakeTui(), effect, new Random(seed), startAnimation: false);

    // ---------------------------------------------------------------- bitmap

    [Fact]
    public void BitmapIsThirtyOneByThirtySixPixelsAndEighteenHalfBlockRows()
    {
        Assert.Equal(31, Armin.Width);
        Assert.Equal(36, Armin.Height);
        Assert.Equal(18, Armin.DisplayHeight);
    }

    [Fact]
    public void PixelLookupReadsTheXbmBitsLsbFirstWithZeroAsForeground()
    {
        // Row 0 is entirely blank, row 1 has ink at x = 8..11.
        for (var x = 0; x < Armin.Width; x++)
        {
            Assert.False(Armin.IsArminPixel(x, 0));
        }

        Assert.True(Armin.IsArminPixel(8, 1));
        Assert.True(Armin.IsArminPixel(11, 1));
        Assert.False(Armin.IsArminPixel(7, 1));
        Assert.False(Armin.IsArminPixel(12, 1));

        // Columns 0 and 30 are blank for their whole height.
        for (var y = 0; y < Armin.Height; y++)
        {
            Assert.False(Armin.IsArminPixel(0, y));
            Assert.False(Armin.IsArminPixel(30, y));
        }

        // Out-of-range rows read as background rather than throwing.
        Assert.False(Armin.IsArminPixel(5, Armin.Height));
    }

    [Theory]
    [InlineData(11, 2, '█')] // both vertical pixels are foreground
    [InlineData(9, 1, '▀')] // only the upper pixel
    [InlineData(8, 0, '▄')] // only the lower pixel
    [InlineData(0, 0, ' ')] // neither
    public void HalfBlockCharactersPackTwoVerticalPixels(int x, int row, char expected)
    {
        Assert.Equal(expected, Armin.GetChar(x, row));
    }

    [Fact]
    public void FinalGridIsEighteenRowsOfThirtyOneCells()
    {
        var grid = Armin.BuildFinalGrid();
        Assert.Equal(Armin.DisplayHeight, grid.Length);
        Assert.All(grid, row => Assert.Equal(Armin.Width, row.Length));

        // Row 0 renders the four "▄" cells of pixel row 1 (x = 8..11).
        Assert.Equal("        ▄▄▄▄                   ", new string(grid[0]));
    }

    // ---------------------------------------------------------------- render

    [Fact]
    public void RenderPadsEveryLineToTheGivenWidth()
    {
        var component = Create(ArminEffect.Typewriter); // pos 0 -> empty grid

        var lines = component.Render(40).Select(AnsiText.StripAnsi).ToArray();
        Assert.Equal(Armin.DisplayHeight + 1, lines.Length);
        Assert.All(lines, line => Assert.Equal(40, UnicodeWidth.VisibleWidth(line)));
        Assert.All(lines.Take(Armin.DisplayHeight), line => Assert.StartsWith(" ", line, StringComparison.Ordinal));

        // The artwork is still empty; only the closing message has content.
        Assert.Equal(" ARMIN SAYS HI" + new string(' ', 26), lines[^1]);
    }

    [Fact]
    public void RenderClipsRowsToTheAvailableWidth()
    {
        var component = Create(ArminEffect.Typewriter);

        var lines = component.Render(10).Select(AnsiText.StripAnsi).ToArray();
        Assert.All(lines.Take(Armin.DisplayHeight), line => Assert.Equal(10, UnicodeWidth.VisibleWidth(line)));

        // Faithful quirk: the closing message is never clipped, so it overflows a narrow footer.
        Assert.Equal(" ARMIN SAYS HI", lines[^1]);
    }

    [Fact]
    public void RenderCachesPerWidthAndGridVersion()
    {
        var component = Create(ArminEffect.Scanline);

        var first = component.Render(40);
        Assert.Same(first, component.Render(40));
        var other = component.Render(41);
        Assert.NotSame(first, other);

        // A cached width of 0 after invalidate() forces the next render to rebuild.
        component.Invalidate();
        var rebuilt = component.Render(41);
        Assert.NotSame(other, rebuilt);
        Assert.Equal(other, rebuilt);
    }

    [Fact]
    public void InvalidatingDoesNotAdvanceTheGrid()
    {
        var component = Create(ArminEffect.Scanline);
        var before = component.Render(40).Select(AnsiText.StripAnsi).ToArray();

        component.Invalidate();
        var after = component.Render(40).Select(AnsiText.StripAnsi).ToArray();

        Assert.Equal(before, after);
    }

    [Fact]
    public void ConstructorWithoutTheTimerLeavesTheAnimationStopped()
    {
        using var component = Create(ArminEffect.Typewriter);
        Assert.False(component.IsAnimating);
    }

    // ---------------------------------------------------------------- effects

    [Fact]
    public void TypewriterCompletesAfter187Frames()
    {
        var component = Create(ArminEffect.Typewriter);

        var frames = 0;
        while (!component.TickEffect())
        {
            frames++;
            Assert.True(frames < 500, "typewriter never finished");
        }

        // 558 cells at 3 per frame, then one extra frame to notice row >= DisplayHeight.
        Assert.Equal(186, frames);
        AssertGridsEqual(component.FinalGrid, component.CurrentGrid);
    }

    [Fact]
    public void ScanlineCompletesAfter18Rows()
    {
        var component = Create(ArminEffect.Scanline);

        for (var i = 0; i < Armin.DisplayHeight; i++)
        {
            Assert.False(component.TickEffect());
        }

        Assert.True(component.TickEffect());
        AssertGridsEqual(component.FinalGrid, component.CurrentGrid);
    }

    [Fact]
    public void CrtCompletesOnceTheExpansionPassesTheGridHeight()
    {
        var component = Create(ArminEffect.Crt);

        var frames = 0;
        while (!component.TickEffect())
        {
            frames++;
            Assert.True(frames < 100, "crt never finished");
        }

        Assert.Equal(Armin.DisplayHeight, frames);
        AssertGridsEqual(component.FinalGrid, component.CurrentGrid);
    }

    [Fact]
    public void GlitchShowsTheCleanImageOnTheFinalFrame()
    {
        var component = Create(ArminEffect.Glitch);

        for (var i = 0; i < 8; i++)
        {
            Assert.False(component.TickEffect());
        }

        Assert.True(component.TickEffect());
        AssertGridsEqual(component.FinalGrid, component.CurrentGrid);
    }

    [Fact]
    public void FadeCompletesAfter38Frames()
    {
        var component = Create(ArminEffect.Fade);

        var frames = 0;
        while (!component.TickEffect())
        {
            frames++;
            Assert.True(frames < 200, "fade never finished");
        }

        Assert.Equal(37, frames);
        AssertGridsEqual(component.FinalGrid, component.CurrentGrid);
    }

    [Fact]
    public void DissolveCompletesAfter28Frames()
    {
        var component = Create(ArminEffect.Dissolve);

        var frames = 0;
        while (!component.TickEffect())
        {
            frames++;
            Assert.True(frames < 200, "dissolve never finished");
        }

        Assert.Equal(27, frames);
        AssertGridsEqual(component.FinalGrid, component.CurrentGrid);
    }

    [Fact]
    public void RainNeverReportsCompletionBecauseTheTopRowIsEmpty()
    {
        var component = Create(ArminEffect.Rain);

        // Upstream defect kept as-is: a column is settled only when its lowest ink pixel is on
        // row 0, and no column has ink there, so `allSettled` stays false forever.
        for (var i = 0; i < 300; i++)
        {
            Assert.False(component.TickEffect(), $"rain reported completion on frame {i + 1}");
        }

        // Columns 0 and 30 stay blank because they have no ink at all.
        for (var row = 0; row < Armin.DisplayHeight; row++)
        {
            Assert.Equal(' ', component.CurrentGrid[row][0]);
            Assert.Equal(' ', component.CurrentGrid[row][Armin.Width - 1]);
        }
    }

    [Fact]
    public void RainDrawsSettledPixelsFromTheBottomUp()
    {
        var component = Create(ArminEffect.Rain);

        // Run enough frames for columns to start settling, then check that at least one cell now
        // reproduces the artwork (the settled block) rather than the falling "▓" marker.
        for (var i = 0; i < 60; i++)
        {
            component.TickEffect();
        }

        var matched = 0;
        for (var row = 0; row < Armin.DisplayHeight; row++)
        {
            for (var x = 0; x < Armin.Width; x++)
            {
                if (component.CurrentGrid[row][x] == component.FinalGrid[row][x] &&
                    component.FinalGrid[row][x] != ' ')
                {
                    matched++;
                }
            }
        }

        Assert.True(matched > 0, "no column settled within 60 frames");
    }

    [Fact]
    public void DisposeStopsTheTimer()
    {
        var component = new ArminComponent(new FakeTui(), ArminEffect.Scanline, new Random(1), startAnimation: true);
        Assert.True(component.IsAnimating);

        component.Dispose();
        Assert.False(component.IsAnimating);

        // Disposing twice is a no-op.
        component.Dispose();
    }

    private static void AssertGridsEqual(char[][] expected, char[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var row = 0; row < expected.Length; row++)
        {
            Assert.Equal(new string(expected[row]), new string(actual[row]));
        }
    }
}
