using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Behavioural (non-corpus) tests for <see cref="TuiAltScreen"/>: the pieces added after the corpus was
/// recorded, plus the interactions the corpus cannot express as data.
/// </summary>
public class AltScreenTests
{
    /// <summary>A component that records the raw input it is handed and handles nothing else.</summary>
    private sealed class RecordingOverlay : IComponent, IFocusable
    {
        public List<string> ReceivedInputs { get; } = [];

        public bool Focused { get; set; }

        public void HandleInput(string data) => ReceivedInputs.Add(data);

        public string[] Render(int width) => ["overlay"];
    }

    private static TuiAltScreen NewTranscript(StringTerminal terminal, out List<string> hits)
    {
        var tui = new TuiAltScreen(terminal, null, new TuiAltScreenOptions { Mouse = true });
        for (var i = 0; i < 40; i++)
        {
            tui.AddChild(new Text($"line {i}"));
        }

        hits = [];
        return tui;
    }

    /// <summary>
    /// #10769 / upstream 11f991145: a wheel event no component handles scrolls the transcript instead
    /// of being handed to the focused overlay's <c>handleInput</c> as raw input.
    /// </summary>
    [Fact]
    public void UnhandledWheelInputScrollsTheTranscriptUnderAFocusedOverlay()
    {
        var terminal = new StringTerminal(40, 10);
        var tui = NewTranscript(terminal, out _);
        var overlay = new RecordingOverlay();
        tui.ShowOverlay(overlay, new OverlayOptions { Row = 0, Col = 0, Width = 20, MaxHeight = 3 });
        tui.Start();
        tui.RenderNow();

        var start = tui.ViewportTop;
        Assert.True(overlay.Focused);

        // SGR wheel-up (button 64) inside the overlay's bounds.
        terminal.SendInput("\x1b[<64;3;2M");
        tui.RenderNow();

        Assert.True(tui.ViewportTop < start, $"expected the transcript to scroll up from {start}, got {tui.ViewportTop}");
        Assert.False(tui.IsFollowingOutput);
        Assert.Empty(overlay.ReceivedInputs);
    }
}
