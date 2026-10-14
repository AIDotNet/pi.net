using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Port of <c>test/terminal-colors.test.ts</c> plus coverage for the <c>queryTerminalColors()</c>
/// plumbing that <c>theme-controller.ts</c> consumes (OSC 10/11/4 replies, the DA1 end marker, the
/// timeout/late-reply contract and the cell-size response).
/// </summary>
public class TerminalColorsTests
{
    private static string DescribeTarget(OscColorTarget target) => target switch
    {
        OscColorTarget.Foreground => "foreground",
        OscColorTarget.Background => "background",
        OscColorTarget.Palette palette => $"palette:{palette.Index}",
        _ => "unknown",
    };

    // ------------------------------------------------------------------
    // OSC color reply parsing
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("\x1b]10;#ff0000\x07", "foreground", 255.0, 0.0, 0.0)]
    [InlineData("\x1b]11;#00ff00\x1b\\", "background", 0.0, 255.0, 0.0)]
    [InlineData("\x1b]4;5;#0000ff\x07", "palette:5", 0.0, 0.0, 255.0)]
    [InlineData("\x1b]4;0;#010203\x07", "palette:0", 1.0, 2.0, 3.0)]
    public void ParseOscColorResponse_ReadsTargetAndHexColor(
        string data,
        string target,
        double r,
        double g,
        double b)
    {
        var response = TerminalColors.ParseOscColorResponse(data);

        Assert.NotNull(response);
        var reply = response!.Value;
        Assert.Equal(target, DescribeTarget(reply.Target));
        Assert.Equal(new RgbColor(r, g, b), reply.Rgb);
    }

    [Theory]
    // `rgb:`/`rgba:` prefixed channels, 8-bit each (max = 255).
    [InlineData("\x1b]11;rgb:00/80/ff\x07", 0.0, 128.0, 255.0)]
    // 12-digit hex, 16-bit channels scaled down to 8 bits (0xffff -> 255).
    [InlineData("\x1b]11;#ffffffffffff\x07", 255.0, 255.0, 255.0)]
    [InlineData("\x1b]11;#000000008000\x07", 0.0, 0.0, 128.0)]
    public void ParseOscColorResponse_AcceptsRgbPrefixAndSixteenBitHex(
        string data,
        double r,
        double g,
        double b)
    {
        var response = TerminalColors.ParseOscColorResponse(data);

        Assert.NotNull(response);
        Assert.Equal(new RgbColor(r, g, b), response!.Value.Rgb);
    }

    [Fact]
    public void ParseOscColorResponse_TrimsSurroundingWhitespace()
    {
        var response = TerminalColors.ParseOscColorResponse("\x1b]11;  #112233 \x07");

        Assert.NotNull(response);
        Assert.Equal(new RgbColor(0x11, 0x22, 0x33), response!.Value.Rgb);
    }

    [Theory]
    [InlineData("\x1b]11;\x07")] // empty value
    [InlineData("\x1b]11;#12345\x07")] // five hex digits
    [InlineData("\x1b]11;#12345g\x07")] // non-hex digit
    [InlineData("\x1b]11;rgb:00/80\x07")] // only two channels
    [InlineData("\x1b]11;rgb:zz/00/00\x07")] // non-hex channel
    public void ParseOscColorResponse_ReturnsNullRgbForUnparseableColor(string data)
    {
        var response = TerminalColors.ParseOscColorResponse(data);

        // A reply with a bad color is still a reply; only `rgb` is undefined.
        Assert.NotNull(response);
        Assert.Null(response!.Value.Rgb);
    }

    [Theory]
    [InlineData("a")] // ordinary key
    [InlineData("\x1b]12;#ffffff\x07")] // OSC 12 (cursor) is not a color target
    [InlineData("\x1b]11;#ffffff")] // missing terminator
    [InlineData("\x1b]11;#ffffff\x07\n")] // trailing newline: JS `$` does not match here
    [InlineData("\x1b]11;#ffffff\x07extra")]
    [InlineData("x\x1b]11;#ffffff\x07")]
    [InlineData("\x1b]4;#ffffff\x07")] // palette index missing
    public void ParseOscColorResponse_ReturnsNullForNonReplies(string data)
    {
        Assert.Null(TerminalColors.ParseOscColorResponse(data));
    }

    [Theory]
    [InlineData("\x1b[?997;1n", TerminalColorScheme.Dark)]
    [InlineData("\x1b[?997;2n", TerminalColorScheme.Light)]
    // Repeated reports: the last one wins, as with the TS `+` quantifier.
    [InlineData("\x1b[?997;1n\x1b[?997;2n", TerminalColorScheme.Light)]
    [InlineData("\x1b[?997;2n\x1b[?997;1n", TerminalColorScheme.Dark)]
    public void ParseTerminalColorSchemeReport_ReadsTheLastReport(
        string data,
        TerminalColorScheme expected)
    {
        Assert.Equal(expected, TerminalColors.ParseTerminalColorSchemeReport(data));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("\x1b[?997;3n")] // only 1 and 2 are defined
    [InlineData("\x1b[?997;1n\n")]
    public void ParseTerminalColorSchemeReport_ReturnsNullForNonReports(string data)
    {
        Assert.Null(TerminalColors.ParseTerminalColorSchemeReport(data));
    }

    // ------------------------------------------------------------------
    // queryTerminalColors()
    // ------------------------------------------------------------------

    private static (TuiMainScreen Tui, StringTerminal Terminal) CreateTui()
    {
        var terminal = new StringTerminal(80, 24);
        var tui = new TuiMainScreen(terminal);
        tui.Start();
        return (tui, terminal);
    }

    [Fact]
    public void QueryTerminalColors_WritesTheFullQuery()
    {
        var (tui, terminal) = CreateTui();
        try
        {
            terminal.ClearOutput();
            _ = tui.QueryTerminalColorsAsync(new TerminalColorQueryOptions { TimeoutMs = 10_000 });

            var output = terminal.Output;
            Assert.StartsWith("\x1b]10;?\x07\x1b]11;?\x07", output);
            Assert.Contains("\x1b]4;0;?\x07", output);
            Assert.Contains("\x1b]4;15;?\x07", output);
            Assert.EndsWith("\x1b[c", output);
        }
        finally
        {
            tui.Stop();
        }
    }

    [Fact]
    public async Task QueryTerminalColors_CompletesOnDa1WithTheRepliesSoFar()
    {
        var (tui, terminal) = CreateTui();
        try
        {
            var pending = tui.QueryTerminalColorsAsync(new TerminalColorQueryOptions { TimeoutMs = 10_000 });

            terminal.SendInput("\x1b]10;#112233\x07");
            terminal.SendInput("\x1b]11;#445566\x07");
            Assert.False(pending.IsCompleted);

            // DA1 ends the query even though only two of the eighteen replies arrived.
            terminal.SendInput("\x1b[?1;2c");

            Assert.True(pending.IsCompleted);
            var colors = await pending;
            Assert.Equal(new RgbColor(0x11, 0x22, 0x33), colors.Foreground);
            Assert.Equal(new RgbColor(0x44, 0x55, 0x66), colors.Background);
            Assert.Null(colors.Palette);
        }
        finally
        {
            tui.Stop();
        }
    }

    [Fact]
    public async Task QueryTerminalColors_SetsPaletteOnlyWhenAllSixteenArrived()
    {
        var (tui, terminal) = CreateTui();
        try
        {
            var pending = tui.QueryTerminalColorsAsync(new TerminalColorQueryOptions { TimeoutMs = 10_000 });

            for (var index = 0; index < 16; index++)
            {
                terminal.SendInput($"\x1b]4;{index};#0000{index:x2}\x07");
            }

            // All 16 palette colors plus OSC 10/11 = 18 replies; the query completes without DA1.
            terminal.SendInput("\x1b]10;#ffffff\x07");
            Assert.False(pending.IsCompleted);
            terminal.SendInput("\x1b]11;#000000\x07");

            var colors = await pending;
            Assert.NotNull(colors.Palette);
            Assert.Equal(16, colors.Palette!.Count);
            Assert.Equal(new RgbColor(0, 0, 0x0f), colors.Palette[15]);
        }
        finally
        {
            tui.Stop();
        }
    }

    [Fact]
    public async Task QueryTerminalColors_IgnoresDuplicateAndOutOfRangeReplies()
    {
        var (tui, terminal) = CreateTui();
        try
        {
            var pending = tui.QueryTerminalColorsAsync(new TerminalColorQueryOptions { TimeoutMs = 10_000 });

            terminal.SendInput("\x1b]10;#ffffff\x07");
            terminal.SendInput("\x1b]10;#000000\x07"); // duplicate: ignored, so foreground stays white
            terminal.SendInput("\x1b]4;200;#123456\x07"); // index >= 16: counted, never stored
            terminal.SendInput("\x1b[?1;2c");

            var colors = await pending;
            Assert.Equal(new RgbColor(255, 255, 255), colors.Foreground);
            Assert.Null(colors.Palette);
        }
        finally
        {
            tui.Stop();
        }
    }

    [Fact]
    public async Task QueryTerminalColors_TimeoutResolvesEmptyAndLateRepliesGoToTheCallback()
    {
        var (tui, terminal) = CreateTui();
        try
        {
            var late = new List<TerminalColorsSnapshot>();
            var pending = tui.QueryTerminalColorsAsync(new TerminalColorQueryOptions
            {
                TimeoutMs = 30,
                OnLateReply = late.Add,
            });

            var colors = await pending;
            Assert.Null(colors.Foreground);
            Assert.Null(colors.Background);
            Assert.Null(colors.Palette);
            Assert.Empty(late);

            // A reply after the timeout no longer resolves the promise, but the query stays pending so
            // the replies can still complete it later; `onLateReply` then receives the full result.
            terminal.SendInput("\x1b]11;#abcdef\x07");
            Assert.Empty(late);

            terminal.SendInput("\x1b[?1;2c");
            Assert.Single(late);
            Assert.Equal(new RgbColor(0xab, 0xcd, 0xef), late[0].Background);
        }
        finally
        {
            tui.Stop();
        }
    }

    [Fact]
    public async Task QueryTerminalColors_LateFullPaletteAlsoCompletesTheQuery()
    {
        var (tui, terminal) = CreateTui();
        try
        {
            var late = new List<TerminalColorsSnapshot>();
            await tui.QueryTerminalColorsAsync(new TerminalColorQueryOptions
            {
                TimeoutMs = 30,
                OnLateReply = late.Add,
            });

            for (var index = 0; index < 16; index++)
            {
                terminal.SendInput($"\x1b]4;{index};#{index:x2}0000\x07");
            }
            terminal.SendInput("\x1b]10;#ffffff\x07");
            Assert.Empty(late);
            terminal.SendInput("\x1b]11;#000000\x07");

            Assert.Single(late);
            Assert.Equal(16, late[0].Palette!.Count);
        }
        finally
        {
            tui.Stop();
        }
    }

    [Fact]
    public async Task QueryTerminalColors_NoLateReplyCallbackKeepsLateRepliesHarmless()
    {
        var (tui, terminal) = CreateTui();
        try
        {
            await tui.QueryTerminalColorsAsync(new TerminalColorQueryOptions { TimeoutMs = 30 });

            // No `onLateReply`: the completion is cleared, so late replies are consumed and dropped.
            terminal.SendInput("\x1b]11;#abcdef\x07");
            terminal.SendInput("\x1b[?1;2c");
        }
        finally
        {
            tui.Stop();
        }
    }

    [Fact]
    public async Task QueryTerminalColors_TwoQueriesAreAnsweredInOrder()
    {
        var (tui, terminal) = CreateTui();
        try
        {
            var first = tui.QueryTerminalColorsAsync(new TerminalColorQueryOptions { TimeoutMs = 10_000 });
            var second = tui.QueryTerminalColorsAsync(new TerminalColorQueryOptions { TimeoutMs = 10_000 });

            // Terminals answer in order, so the first DA1 belongs to the oldest query.
            terminal.SendInput("\x1b]11;#111111\x07");
            terminal.SendInput("\x1b[?1;2c");
            terminal.SendInput("\x1b]11;#222222\x07");
            terminal.SendInput("\x1b[?1;2c");

            Assert.Equal(new RgbColor(0x11, 0x11, 0x11), (await first).Background);
            Assert.Equal(new RgbColor(0x22, 0x22, 0x22), (await second).Background);
        }
        finally
        {
            tui.Stop();
        }
    }

    [Fact]
    public async Task QueryTerminalBackgroundColor_ReturnsTheBackground()
    {
        var (tui, terminal) = CreateTui();
        try
        {
            var pending = tui.QueryTerminalBackgroundColorAsync(10_000);
            terminal.SendInput("\x1b]11;#0a0b0c\x07");
            terminal.SendInput("\x1b[?1;2c");

            Assert.Equal(new RgbColor(0x0a, 0x0b, 0x0c), await pending);
        }
        finally
        {
            tui.Stop();
        }
    }

    [Fact]
    public async Task QueryTerminalColorScheme_ResolvesOnTheDsrReport()
    {
        var (tui, terminal) = CreateTui();
        try
        {
            var pending = tui.QueryTerminalColorSchemeAsync(10_000);
            Assert.Contains("\x1b[?996n", terminal.Output);

            terminal.SendInput("\x1b[?997;2n");

            Assert.Equal(TerminalColorScheme.Light, await pending);
        }
        finally
        {
            tui.Stop();
        }
    }

    [Fact]
    public void OnTerminalColorSchemeChange_FiresForDsrReports()
    {
        var (tui, terminal) = CreateTui();
        try
        {
            var seen = new List<TerminalColorScheme>();
            tui.OnTerminalColorSchemeChange(seen.Add);

            terminal.SendInput("\x1b[?997;1n");
            terminal.SendInput("\x1b[?997;2n");

            Assert.Equal(
                new List<TerminalColorScheme> { TerminalColorScheme.Dark, TerminalColorScheme.Light },
                seen);
        }
        finally
        {
            tui.Stop();
        }
    }

    // ------------------------------------------------------------------
    // Cell size response
    // ------------------------------------------------------------------

    [Fact]
    public void CellSizeResponse_UpdatesTheCellDimensions()
    {
        var previous = TerminalImage.GetCellDimensions();
        var (tui, terminal) = CreateTui();
        try
        {
            terminal.SendInput("\x1b[6;20;10t");

            Assert.Equal(new CellDimensions(10, 20), TerminalImage.GetCellDimensions());
        }
        finally
        {
            tui.Stop();
            TerminalImage.SetCellDimensions(previous);
        }
    }

    [Theory]
    [InlineData("\x1b[6;0;10t")] // zero height
    [InlineData("\x1b[6;20;0t")] // zero width
    [InlineData("\x1b[6;20;10")] // no terminator
    public void CellSizeResponse_IgnoresDegenerateOrMalformedReports(string data)
    {
        var previous = TerminalImage.GetCellDimensions();
        var (tui, terminal) = CreateTui();
        try
        {
            terminal.SendInput(data);

            Assert.Equal(previous, TerminalImage.GetCellDimensions());
        }
        finally
        {
            tui.Stop();
            TerminalImage.SetCellDimensions(previous);
        }
    }
}
