using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Port of the upstream <c>TUI fake cursor</c> suite (tui-render.test.ts) plus the alt-screen
/// selection case. A component marks the text it draws as the cursor with the zero-width
/// <see cref="Tui.FakeCursorStart"/>/<see cref="Tui.FakeCursorEnd"/> pair and leaves it unresolved;
/// <see cref="TuiBase"/> decides at render time whether to draw it in reverse video or to drop it
/// because the terminal cursor is already there.
/// </summary>
public class FakeCursorTests
{
    private sealed class RawComponent : IComponent
    {
        public RawComponent(params string[] lines) => Lines = lines;

        public string[] Lines { get; set; }

        public string[] Render(int width) => Lines;
    }

    /// <summary>Column of the last horizontal absolute (<c>ESC[nG</c>) the renderer emitted.</summary>
    private static int LastCursorColumn(string output)
    {
        var column = -1;
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(output, "\x1b\\[([0-9]+)G"))
        {
            column = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        return column;
    }

    [Fact]
    public void DrawsTheFocusedFakeCursorInReverseVideoAndOmitsItWhenTheHardwareCursorIsShown()
    {
        var terminal = new StringTerminal(20, 5);
        var tui = new TuiMainScreen(terminal);
        var input = new Input();
        input.SetValue("abc");
        tui.AddChild(input);
        tui.SetFocus(input);
        tui.RenderNow();

        Assert.Contains("> \x1b[7ma\x1b[27mbc", terminal.Output);

        terminal.ClearOutput();
        tui.SetShowHardwareCursor(true);
        tui.RenderNow(force: true);

        Assert.Contains("> abc", terminal.Output);
        Assert.DoesNotContain("\x1b[7m", terminal.Output);
        Assert.DoesNotContain("\x1b_pi:", terminal.Output);
    }

    [Fact]
    public void KeepsTheFakeCursorOfAnUnfocusedInputWhenTheHardwareCursorIsShown()
    {
        var terminal = new StringTerminal(20, 5);
        var tui = new TuiMainScreen(terminal, showHardwareCursor: true);
        var input = new Input();
        input.SetValue("abc");
        tui.AddChild(input);
        tui.RenderNow();

        Assert.Contains("> \x1b[7ma\x1b[27mbc", terminal.Output);
    }

    // 上游 1.5.1 #9441：内部光标标记是渲染元数据，多个标记 / 其他行上的标记都不得进入终端输出，
    // 但首个标记仍决定硬件光标列。对齐上游 tui-render.test.ts 的两条回归用例。
    [Fact]
    public void DoesNotWriteDuplicateCursorMarkersToTheTerminal()
    {
        var terminal = new StringTerminal(40, 5);
        var tui = new TuiMainScreen(terminal, showHardwareCursor: true);
        tui.AddChild(new RawComponent($"\x1b[31m界🙂\x1b[0m{Tui.CursorMarker} world{Tui.CursorMarker}"));
        tui.RenderNow();

        Assert.DoesNotContain(Tui.CursorMarker, terminal.Output);
        Assert.Contains("\x1b[31m界🙂\x1b[0m world", terminal.Output);
    }

    [Fact]
    public void DoesNotWriteCursorMarkersFromOtherLines()
    {
        var terminal = new StringTerminal(40, 5);
        var tui = new TuiMainScreen(terminal, showHardwareCursor: true);
        tui.AddChild(new RawComponent(
            $"offscreen{Tui.CursorMarker}",
            $"a{Tui.CursorMarker}",
            $"visible-b{Tui.CursorMarker}"));
        tui.RenderNow();

        Assert.DoesNotContain(Tui.CursorMarker, terminal.Output);
        Assert.Contains("offscreen", terminal.Output);
        Assert.Contains("visible-b", terminal.Output);
    }

    [Fact]
    public void OmitsTheFocusedFakeCursorWhenTruncationDroppedItsEndMarker()
    {
        var terminal = new StringTerminal(20, 5);
        var tui = new TuiMainScreen(terminal, showHardwareCursor: true);
        var baseLine = TextLayout.SliceByColumn($"ab{Tui.CursorMarker}{Tui.RenderFakeCursor("c")}def", 0, 3, true);
        tui.AddChild(new RawComponent(baseLine));
        tui.RenderNow();

        Assert.DoesNotContain("\x1b[7m", terminal.Output);
        Assert.Equal(3, LastCursorColumn(terminal.Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsTheCursorWhenAnOverlayEndsDirectlyLeftOfIt(bool showHardwareCursor)
    {
        var terminal = new StringTerminal(20, 5);
        var tui = new TuiMainScreen(terminal, showHardwareCursor);
        tui.AddChild(new RawComponent($"abc{Tui.CursorMarker}{Tui.RenderFakeCursor("d")}ef"));
        tui.ShowOverlay(new RawComponent("XXX"), new OverlayOptions { Row = 0, Col = 0, Width = 3 });
        tui.RenderNow();

        Assert.Contains("XXX", terminal.Output);
        Assert.Equal(!showHardwareCursor, terminal.Output.Contains("\x1b[7md", StringComparison.Ordinal));
        // The hardware cursor is positioned at column 4 in both modes
        Assert.Equal(4, LastCursorColumn(terminal.Output));
    }

    /// <summary>
    /// The alt screen resolves fake cursors before highlighting and compositing, so the marker never
    /// reaches the screen lines but the reverse-video cell does.
    /// </summary>
    [Fact]
    public void AltScreenResolvesFakeCursorsBeforeCompositing()
    {
        var terminal = new StringTerminal(20, 5);
        var tui = new TuiAltScreen(terminal);
        tui.AddChild(new Text($"> {Tui.RenderFakeCursor("a")}bcd"));
        tui.Start();
        tui.RenderNow();

        var screen = tui.GetScreenLines();
        Assert.Contains(screen, line => line.Contains("\x1b[7ma\x1b[27mbcd", StringComparison.Ordinal));
        Assert.DoesNotContain(screen, line => line.Contains("\x1b_pi:", StringComparison.Ordinal));
    }
}
