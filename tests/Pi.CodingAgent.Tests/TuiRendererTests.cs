// ============================================================================
// Tests for modes/interactive/tui-renderer.ts (4f-4b).
//
// Upstream has no test file for this module. The vectors are derived from the source: the two
// factory paths are checked for the renderer they build, and the reference is checked for
// forwarding every call to whatever renderer is current (the point of the TS Proxy).
// ============================================================================

using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class TuiRendererTests : SelectorTestFixture
{
    private static InteractiveTuiOptions Options(TuiMode mode, ITerminal? terminal = null) => new()
    {
        TuiMode = mode,
        ShowHardwareCursor = false,
        LogDirectory = "/tmp/pi-log",
        Terminal = terminal ?? new StringTerminal(80, 24),
    };

    // ---------------------------------------------------------------- factories

    [Fact]
    public void RegularModeBuildsTheMainScreenRenderer()
    {
        var terminal = new StringTerminal(80, 24);

        var renderer = TuiRenderer.CreateInteractiveTui(Options(TuiMode.Regular, terminal));

        var mainScreen = Assert.IsType<TuiMainScreen>(renderer);
        Assert.Equal(TuiMode.Regular, mainScreen.Mode);
        Assert.Same(terminal, mainScreen.Terminal);
    }

    [Fact]
    public void FullscreenModeBuildsTheAltScreenRenderer()
    {
        var terminal = new StringTerminal(80, 24);

        var renderer = TuiRenderer.CreateInteractiveTui(Options(TuiMode.Fullscreen, terminal));

        var altScreen = Assert.IsType<TuiAltScreen>(renderer);
        Assert.Equal(TuiMode.Fullscreen, altScreen.Mode);
        Assert.Same(terminal, altScreen.Terminal);
    }

    // ---------------------------------------------------------------- the reference

    [Fact]
    public void ReferenceForwardsStateToTheCurrentRenderer()
    {
        var fake = new FakeTui();
        var reference = TuiRenderer.CreateInteractiveTuiReference(() => fake);

        Assert.Equal(fake.Mode, reference.Mode);
        Assert.Same(fake.Terminal, reference.Terminal);
        Assert.Same(fake.Children, reference.Children);
        Assert.Equal(fake.FullRedraws, reference.FullRedraws);
        Assert.Equal(fake.GetShowHardwareCursor(), reference.GetShowHardwareCursor());
        Assert.Equal(fake.GetClearOnShrink(), reference.GetClearOnShrink());
        Assert.Equal(fake.HasOverlay(), reference.HasOverlay());
    }

    [Fact]
    public void ReferenceForwardsMutations()
    {
        var fake = new FakeTui();
        var reference = TuiRenderer.CreateInteractiveTuiReference(() => fake);
        var child = new Spacer(1);
        var debug = new Action(() => { });

        reference.AddChild(child);
        reference.RequestRender();
        reference.Invalidate();
        reference.OnDebug = debug;

        Assert.Same(child, Assert.Single(fake.Children));
        Assert.Equal(1, fake.RenderRequests);
        Assert.Equal(1, fake.Invalidations);
        Assert.Same(debug, reference.OnDebug);

        reference.RemoveChild(child);
        Assert.Empty(fake.Children);
    }

    [Fact]
    public void ReferenceFollowsARendererSwap()
    {
        // The whole point of the TS Proxy: a component keeps this reference while InteractiveMode
        // replaces the renderer behind it.
        var first = new FakeTui();
        var second = new FakeTui();
        var current = first;
        var reference = TuiRenderer.CreateInteractiveTuiReference(() => current);

        reference.RequestRender();
        reference.Invalidate();

        current = second;
        reference.RequestRender();

        Assert.Equal(1, first.RenderRequests);
        Assert.Equal(1, first.Invalidations);
        Assert.Equal(1, second.RenderRequests);
        Assert.Equal(0, second.Invalidations);
    }

    [Fact]
    public async Task ReferenceForwardsTheTerminalColorQuery()
    {
        var fake = new FakeTui();
        var reference = TuiRenderer.CreateInteractiveTuiReference(() => fake);

        await reference.QueryTerminalColorsAsync(new TerminalColorQueryOptions { TimeoutMs = 42 });

        Assert.Equal(42, fake.LastColorQuery?.TimeoutMs);
    }

    [Fact]
    public void ReferenceForwardsTheColorSchemeListener()
    {
        var fake = new FakeTui();
        var reference = TuiRenderer.CreateInteractiveTuiReference(() => fake);

        reference.SetTerminalColorSchemeNotifications(true);
        var unsubscribe = reference.OnTerminalColorSchemeChange(_ => { });

        Assert.True(fake.ColorSchemeNotifications);
        Assert.NotNull(fake.ColorSchemeListener);

        unsubscribe();
        Assert.Null(fake.ColorSchemeListener);
    }
}
