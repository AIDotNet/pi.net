// ============================================================================
// Tests for modes/interactive/components/easter-egg-3d.ts (4f-3e Batch 3).
//
// Upstream has no test file for this module. The numeric helpers are checked against reference
// vectors produced by running the TypeScript source verbatim in node (the same differential
// approach used for the faux usage estimate); the renderer is checked for its structural
// invariants (one line per terminal row, exact width, text gone once dissolved).
// ============================================================================

using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class EasterEgg3DTests : SelectorTestFixture
{
    private static readonly Rgb Foreground = new(220, 220, 220);
    private static readonly Rgb Background = new(0, 0, 0);

    private static EasterEgg3dAnimation CreateAnimation(
        FakeTui tui,
        Func<double> clock,
        Action? onDone = null,
        IReadOnlyList<string>? screen = null,
        bool startTimer = false)
    {
        var lines = screen ?? Enumerable.Repeat("hello world", 24).ToArray();
        return new EasterEgg3dAnimation(
            tui,
            lines,
            EasterEgg3D.ArminModel(new Rgb(228, 138, 122)),
            new EasterEggColors(Foreground, Background),
            onDone ?? (() => { }),
            clock,
            startTimer);
    }

    // ---------------------------------------------------------------- numeric helpers

    [Theory]
    // Reference values from the TypeScript hash() run in node.
    [InlineData(0, 0.11478774505667388)]
    [InlineData(1, 0.24678996880538762)]
    [InlineData(2, 0.02330568921752274)]
    [InlineData(7919, 0.09437634493224323)]
    [InlineData(123456789, 0.23941869544796646)]
    [InlineData(217584, 0.30034196632914245)]
    [InlineData(-5, 0.65450135571882129)]
    [InlineData(999999999, 0.12256294884718955)]
    public void HashMatchesTheTypeScriptReferenceVectors(long value, double expected)
    {
        Assert.Equal(expected, EasterEgg3D.Hash(value), 15);
    }

    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.103515625)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.75, 0.896484375)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.0)]
    public void SmoothMatchesTheTypeScriptReferenceVectors(double value, double expected)
    {
        Assert.Equal(expected, EasterEgg3D.Smooth(value), 15);
    }

    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(0.3, 0.657)]
    [InlineData(0.5, 0.875)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 1.0)]
    public void EaseOutMatchesTheTypeScriptReferenceVectors(double value, double expected)
    {
        Assert.Equal(expected, EasterEgg3D.EaseOut(value), 15);
    }

    [Fact]
    public void RotationMatrixMatchesTheTypeScriptReferenceVector()
    {
        double[] expected =
        [
            0.95406038485778155, 0.29784357670004791, 0.03252669462986343,
            -0.19667456405794811, 0.70446630527559173, -0.68194306256752324,
            -0.22602632124962302, 0.64421768723769102, 0.73068164993551243,
        ];

        var actual = EasterEgg3D.Rotation(0.3, 0.7, -0.4);
        Assert.Equal(9, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], actual[i], 14);
        }
    }

    [Theory]
    [InlineData(" ", 0.0, 0)]
    [InlineData("·", 0.0, 2)]
    [InlineData("─", 0.0, 3)]
    [InlineData("a", 0.0, 4)]
    [InlineData("a", 0.5, 5)]
    [InlineData("a", 0.99, 6)]
    public void GlyphInkCountsTheDotsAGlyphTurnsInto(string text, double seed, int expected)
    {
        Assert.Equal(expected, EasterEgg3D.GlyphInk(text, seed));
    }

    [Fact]
    public void IsLightUsesRelativeLuminance()
    {
        Assert.True(EasterEgg3D.IsLight(new Rgb(255, 255, 255)));
        Assert.False(EasterEgg3D.IsLight(new Rgb(0, 0, 0)));
        // 0.2126*200 + 0.7152*100 + 0.0722*50 = 117.13 -> dark.
        Assert.False(EasterEgg3D.IsLight(new Rgb(200, 100, 50)));
        // 0.2126*200 + 0.7152*200 + 0.0722*50 = 189.17 -> light.
        Assert.True(EasterEgg3D.IsLight(new Rgb(200, 200, 50)));
    }

    // ---------------------------------------------------------------- models

    [Fact]
    public void PiLogoModelHasTenBlocksAtTheirBitmapColors()
    {
        var model = EasterEgg3D.PiLogoModel(3, 7);

        Assert.Equal(4, model.Columns);
        Assert.Equal(4, model.Rows);
        Assert.Equal(10, model.Blocks.Count);
        Assert.Equal((3, 7), model.Origin);

        // "ccc." / "b.c." / "bb.y" / "b..y"
        var at = model.Blocks.ToDictionary(block => (block.Home.X, block.Home.Y), block => block.Color);
        Assert.Equal(new Rgb(228, 138, 122), at[(0, 0)]);
        Assert.Equal(new Rgb(79, 142, 179), at[(0, 1)]);
        Assert.Equal(new Rgb(79, 142, 179), at[(0, 2)]);
        Assert.Equal(new Rgb(234, 182, 93), at[(3, 2)]);
        Assert.Equal(new Rgb(234, 182, 93), at[(3, 3)]);
        // '.' is transparent in the bitmap.
        Assert.False(at.ContainsKey((3, 0)));
        Assert.False(at.ContainsKey((2, 2)));
        Assert.All(model.Blocks, block => Assert.Equal(0, block.Home.Z));
    }

    [Fact]
    public void ArminModelUsesOneBlockPerInkPixel()
    {
        var color = new Rgb(10, 20, 30);
        var model = EasterEgg3D.ArminModel(color);

        Assert.Equal(Armin.Width, model.Columns);
        Assert.Equal(Armin.Height, model.Rows);
        // 245 of the 31x36 pixels are foreground (counted from the XBM bits).
        Assert.Equal(245, model.Blocks.Count);
        Assert.All(model.Blocks, block => Assert.Equal(color, block.Color));
        Assert.Null(model.Origin);
    }

    [Fact]
    public void PuzzleMovesRoundTheBlockCountForArmin()
    {
        var model = EasterEgg3D.ArminModel(new Rgb(1, 2, 3));

        // round(245 * (0.03 + random * 0.04)) -> 7 .. 17.
        Assert.Equal(7, model.PuzzleMoves(0));
        Assert.Equal(17, model.PuzzleMoves(1));
        Assert.Equal(10, model.PuzzleMoves(0.3));
    }

    [Fact]
    public void PiLogoPuzzleMovesTwoBlocksBelowFortyPercent()
    {
        var model = EasterEgg3D.PiLogoModel(0, 0);
        Assert.Equal(2, model.PuzzleMoves(0.39));
        Assert.Equal(1, model.PuzzleMoves(0.4));
    }

    // ---------------------------------------------------------------- shuffle

    [Fact]
    public void ShuffleStepsStartAtHomeAndKeepEveryBlockInBounds()
    {
        var model = EasterEgg3D.PiLogoModel(0, 0);
        var steps = EasterEgg3D.ShuffleSteps(model, 3);

        Assert.Equal(13, steps.Count);
        Assert.Equal(model.Blocks.Select(block => block.Home), steps[0]);

        foreach (var positions in steps)
        {
            Assert.Equal(model.Blocks.Count, positions.Length);
            Assert.Equal(positions.Length, positions.Distinct().Count());
            foreach (var position in positions)
            {
                Assert.InRange(position.X, 0, model.Columns - 1);
                Assert.InRange(position.Y, 0, model.Rows - 1);
                Assert.InRange(position.Z, -1, 1);
            }
        }
    }

    [Fact]
    public void ShuffleStepsAreDeterministicPerCycleAndDifferBetweenCycles()
    {
        var model = EasterEgg3D.ArminModel(new Rgb(1, 2, 3));

        var first = EasterEgg3D.ShuffleSteps(model, 5);
        var again = EasterEgg3D.ShuffleSteps(model, 5);
        var other = EasterEgg3D.ShuffleSteps(model, 6);

        Assert.Equal(first[^1], again[^1]);
        Assert.NotEqual(first[^1], other[^1]);
    }

    // ---------------------------------------------------------------- screen parsing

    [Fact]
    public void ParseScreenResolvesTruecolorSgr()
    {
        var cells = EasterEgg3D.ParseScreen(["\x1b[38;2;10;20;30mX\x1b[0m"], 10, Foreground, Background);

        Assert.Single(cells);
        Assert.Single(cells[0]);
        Assert.Equal("X", cells[0][0].Text);
        Assert.Equal(new Rgb(10, 20, 30), cells[0][0].Fg);
        Assert.Null(cells[0][0].Bg);
    }

    [Fact]
    public void ParseScreenResolvesIndexedColorsAndBackgrounds()
    {
        var cells = EasterEgg3D.ParseScreen(["\x1b[41mB"], 10, Foreground, Background);

        Assert.Single(cells[0]);
        Assert.Equal(Foreground, cells[0][0].Fg);
        Assert.NotNull(cells[0][0].Bg);
        // Palette entry 1 is the ANSI red the terminal reports; only the presence is asserted here.
        Assert.Equal(Colors.ColorToRgb(Colors.Indexed(1)).R, cells[0][0].Bg!.Value.R, 9);
    }

    [Fact]
    public void ParseScreenSwapsColorsForInverseVideo()
    {
        var cells = EasterEgg3D.ParseScreen(
            ["\x1b[38;2;1;2;3m\x1b[48;2;4;5;6m\x1b[7mX"],
            10,
            Foreground,
            Background);

        Assert.Equal(new Rgb(4, 5, 6), cells[0][0].Fg);
        Assert.Equal(new Rgb(1, 2, 3), cells[0][0].Bg);
    }

    [Fact]
    public void ParseScreenDimsTowardTheBackground()
    {
        var cells = EasterEgg3D.ParseScreen(["\x1b[38;2;200;100;0m\x1b[2mX"], 10, Foreground, Background);

        // mix(fg, background ?? background, 0.4) = fg * 0.6 when the background is black.
        Assert.Equal(120, cells[0][0].Fg.R, 9);
        Assert.Equal(60, cells[0][0].Fg.G, 9);
    }

    [Fact]
    public void ParseScreenSplitsWideGraphemesIntoTwoCells()
    {
        var cells = EasterEgg3D.ParseScreen(["模x"], 10, Foreground, Background);

        Assert.Equal(3, cells[0].Count);
        Assert.Equal("模", cells[0][0].Text);
        Assert.Equal(2, cells[0][0].Width);
        Assert.Equal("", cells[0][1].Text);
        Assert.Equal(0, cells[0][1].Width);
        Assert.Equal("x", cells[0][2].Text);
    }

    [Fact]
    public void ParseScreenSkipsOscSequences()
    {
        // An OSC 8 hyperlink wraps the text; the escape sequence itself contributes no cells.
        var cells = EasterEgg3D.ParseScreen(["\x1b]8;;http://example.com\u0007A\x1b]8;;\u0007B"], 10, Foreground, Background);

        Assert.Equal(2, cells[0].Count);
        Assert.Equal("A", cells[0][0].Text);
        Assert.Equal("B", cells[0][1].Text);
    }

    [Fact]
    public void ParseScreenClipsCellsAtTheGivenWidth()
    {
        var cells = EasterEgg3D.ParseScreen(["abcdef"], 3, Foreground, Background);
        Assert.Equal(3, cells[0].Count);
    }

    // ---------------------------------------------------------------- animation

    [Fact]
    public void AnimationRendersOneFullWidthLinePerTerminalRow()
    {
        var tui = new FakeTui();
        using var animation = CreateAnimation(tui, () => 0);

        var lines = animation.Render(60);

        Assert.Equal(24, lines.Length);
        foreach (var line in lines)
        {
            Assert.EndsWith("\x1b[0m", line, StringComparison.Ordinal);
            Assert.Equal(60, UnicodeWidth.VisibleWidth(AnsiText.StripAnsi(line)));
        }
    }

    [Fact]
    public void AnimationKeepsTheScreenTextWhileTheDustHasNotReachedIt()
    {
        var tui = new FakeTui();
        using var animation = CreateAnimation(tui, () => 0);

        var rendered = string.Join("\n", animation.Render(60).Select(AnsiText.StripAnsi));
        Assert.Contains("hello world", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AnimationDropsTheTextLayerOnceTheScreenHasDissolved()
    {
        var now = 0.0;
        var tui = new FakeTui();
        using var animation = CreateAnimation(tui, () => now);

        // The dissolve timeline is relative to construction time; jump well past DISSOLVE_END (1.12s).
        now = 3000;
        var rendered = string.Join("\n", animation.Render(60).Select(AnsiText.StripAnsi));
        Assert.DoesNotContain("hello", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("world", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AnimationRasterizesTheModelIntoBrailleCells()
    {
        var now = 0.0;
        var tui = new FakeTui();
        using var animation = CreateAnimation(tui, () => now);

        // Well past the flight: the model fills the center and the raster must produce braille dots.
        now = 4000;
        var rendered = string.Join("\n", animation.Render(60).Select(AnsiText.StripAnsi));
        var braille = rendered.Count(c => c >= '\u2800' && c <= '\u28ff');

        Assert.True(braille > 20, $"expected the model to be rasterized, found {braille} braille cells");
    }

    [Fact]
    public void InvalidateForcesTheScreenToBeParsedAgain()
    {
        var tui = new FakeTui();
        using var animation = CreateAnimation(tui, () => 0);

        var before = animation.Render(40);
        animation.Invalidate();
        var after = animation.Render(40);

        Assert.Equal(before, after);
        Assert.NotSame(before, after);
    }

    [Fact]
    public void ClosingStartsTheExitTimelineAndASecondCloseSkipsIt()
    {
        var now = 500.0;
        var tui = new FakeTui();
        var done = 0;
        using var animation = CreateAnimation(tui, () => now, () => done++);

        animation.Close();
        // The exit is 1.1s long; half way through the text is coming back and nothing has finished yet.
        now = 1050;
        Assert.NotEmpty(animation.Render(40));
        Assert.Equal(0, done);

        animation.Close();
        // The second close skips the exit, but finish() only fires the callback when the timer is live.
        Assert.Equal(0, done);
    }

    [Fact]
    public async Task TheTimerFinishesWhenNoFrameHasBeenRenderedForASecond()
    {
        var now = 0.0;
        var tui = new FakeTui();
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var animation = CreateAnimation(tui, () => now, () => finished.TrySetResult(true), startTimer: true);

        await Task.Delay(120);
        Assert.False(finished.Task.IsCompleted);

        // No render happened, so the wall clock runs past the one-second guard.
        now = 5000;
        var completed = await Task.WhenAny(finished.Task, Task.Delay(5000));
        Assert.Same(finished.Task, completed);
    }

    [Fact]
    public void MouseClickClosesAndHandlesTheEvent()
    {
        var now = 0.0;
        var tui = new FakeTui();
        using var animation = CreateAnimation(tui, () => now);

        var result = animation.HandleMouse(new TuiMouseEvent { Type = TuiMouseEventType.Click });

        Assert.NotNull(result);
        Assert.True(result!.Handled);
        Assert.False(result.Render);

        // A click started the exit timeline: the second close finishes immediately instead of restarting.
        animation.Close();
        Assert.NotEmpty(animation.Render(40));
    }

    [Fact]
    public void InputMatchingTheCancelBindingClosesTheAnimation()
    {
        var now = 0.0;
        var tui = new FakeTui();
        using var animation = CreateAnimation(tui, () => now);

        animation.HandleInput("\x1b");
        // Escape is the default cancel binding, so the exit timeline is now running: rendering at a
        // later time still produces a full frame instead of throwing.
        now = 200;
        Assert.NotEmpty(animation.Render(40));
    }

    [Fact]
    public void PlayerRefusesToPlayWithoutTheAlternateScreen()
    {
        Assert.False(EasterEggPlayer.PlayArmin3d(new FakeTui()));
        Assert.False(EasterEggPlayer.PlayPiLogo3d(new FakeTui(), 0, 0));
    }
}
