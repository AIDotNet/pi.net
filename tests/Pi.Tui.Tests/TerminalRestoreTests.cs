using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// #11170 / upstream 9df819de3: a process that exits without <c>stop()</c> must still give the terminal
/// its modes back. The TS version registers <c>process.on("exit")</c>; the port registers
/// <see cref="AppDomain.ProcessExit"/> and writes the same sequence, so the handler can be invoked
/// directly instead of exiting the test host.
/// </summary>
public class TerminalRestoreTests
{
    [Fact]
    public void MainScreenExitRestoreShowsTheCursorAndLeavesBracketedPaste()
    {
        var terminal = new StringTerminal(20, 5);
        var tui = new TuiMainScreen(terminal);
        tui.Start();

        tui.HandleProcessExit();

        Assert.Equal(["\x1b[?25h\x1b[?2004l"], terminal.ExitRestores);
        Assert.Contains("\x1b[?25h\x1b[?2004l", terminal.Output);

        // After stop() the exit fallback must stay silent: stop() already restored everything.
        tui.Stop();
        terminal.ExitRestores.Clear();
        tui.HandleProcessExit();
        Assert.Empty(terminal.ExitRestores);
    }

    [Fact]
    public void AltScreenExitRestoreAlsoLeavesTheAlternateScreen()
    {
        var terminal = new StringTerminal(20, 5);
        var tui = new TuiAltScreen(terminal);
        tui.Start();

        tui.HandleProcessExit();

        // The alternate screen owns mouse reporting, autowrap and the synchronized-output wrapper, so
        // the prefix releases them; kitty keyboard flags are per screen, so the alternate screen is
        // left after them. Without the mouse reset a Ctrl+C exit leaves the terminal reporting motion
        // events and the parent shell echoes them as garbage.
        Assert.Equal(
            [
                "\x1b[?2026l\x1b[?1006l\x1b[?1004l\x1b[?1003l\x1b[?1002l\x1b[?1000l\x1b[?7h\x1b[?25h"
                    + "\x1b[?2004l\x1b[?1049l\x1b[?25h",
            ],
            terminal.ExitRestores);
        tui.Stop();
    }

    [Fact]
    public void AltScreenExitRestoreLeavesMouseAloneWhenItWasNeverEnabled()
    {
        var terminal = new StringTerminal(20, 5);
        var tui = new TuiAltScreen(terminal, options: new TuiAltScreenOptions { Mouse = false });
        tui.Start();

        tui.HandleProcessExit();

        Assert.Equal(["\x1b[?2026l\x1b[?7h\x1b[?25h\x1b[?2004l\x1b[?1049l\x1b[?25h"], terminal.ExitRestores);
        tui.Stop();
    }

    [Fact]
    public void ExitRestoreClearsProgressAndPopsTheKeyboardProtocol()
    {
        var original = Console.Out;
        var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);
            var terminal = new ProcessTerminal();
            terminal.RestoreOnExit("\x1b[?25h", "");
        }
        finally
        {
            Console.SetOut(original);
        }

        var written = captured.ToString();

        // No progress indicator is active and the Kitty protocol was never pushed, so only the
        // bracketed-paste reset and the prefix are written.
        Assert.Equal("\x1b[?25h\x1b[?2004l", written);
    }
}
