// ============================================================================
// Tests for modes/interactive/components/bordered-loader.ts,
// custom-entry.ts and earendil-announcement.ts (4f-3e Batch 1).
//
// No upstream test files exist for these components (mermaid.test.ts exists
// but requires the grok-mermaid package, unavailable in the reference
// checkout); observations are derived from the component sources (same
// approach as Batches E-3/F-3/F-4).
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using Pi.Tui.Components;
using Pi.CodingAgent.Utils;
using TuiText = Pi.Tui.Components.Text;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class BorderedLoaderTests : SelectorTestFixture
{
    private static Theme Theme => ThemeRuntime.Current;

    [Fact]
    public void CancellableLoader_RendersBordersHintAndLoader()
    {
        var loader = new BorderedLoader(new FakeTui(), Theme, "working", cancellable: true);

        var lines = loader.Render(60).Select(AnsiText.StripAnsi).ToArray();
        var joined = string.Join("\n", lines);

        // Border rows top and bottom, the message in the middle, and the cancel hint row.
        // The loader itself renders 2 lines, so cancellable = border + 2 + spacer + hint + spacer + border.
        Assert.Equal(7, lines.Length);
        Assert.Contains("working", joined);
        Assert.Contains("cancel", joined);
    }

    [Fact]
    public void CancellableLoader_SignalAndOnAbort_WireThroughToTheInnerLoader()
    {
        var loader = new BorderedLoader(new FakeTui(), Theme, "working", cancellable: true);
        var aborted = false;
        loader.OnAbort = () => aborted = true;

        Assert.False(loader.Signal.IsCancellationRequested);
        loader.HandleInput("\x1b"); // esc -> tui.select.cancel

        Assert.True(aborted);
        Assert.True(loader.Signal.IsCancellationRequested);
    }

    [Fact]
    public void NonCancellableLoader_HasNoHintRow_AndCreatesItsOwnSignal()
    {
        var loader = new BorderedLoader(new FakeTui(), Theme, "thinking", cancellable: false);

        var lines = loader.Render(60).Select(AnsiText.StripAnsi).ToArray();
        Assert.Equal(5, lines.Length); // border / loader(2) / spacer / border — no cancel hint
        Assert.Contains("thinking", string.Join("\n", lines));

        // Signal comes from the internal controller, never cancelled by input.
        Assert.False(loader.Signal.IsCancellationRequested);
        loader.HandleInput("\x1b");
        Assert.False(loader.Signal.IsCancellationRequested);
    }

    [Fact]
    public void DisposeLoader_StopsTheUnderlyingLoader()
    {
        var loader = new BorderedLoader(new FakeTui(), Theme, "working", cancellable: true);
        loader.DisposeLoader(); // must not throw (CancellableLoader.Dispose => Stop)

        var plain = new BorderedLoader(new FakeTui(), Theme, "working", cancellable: false);
        plain.DisposeLoader(); // Loader has Stop(), not IDisposable
    }
}

public sealed class CustomEntryComponentTests : SelectorTestFixture
{
    private static CustomEntry Entry(string customType = "widget") =>
        new() { Id = "e1", ParentId = null, CustomType = customType, Data = null };

    [Fact]
    public void RendererOutput_IsWrappedInSpacer_AndReportedByHasContent()
    {
        var component = new CustomEntryComponent(Entry(), (_, _, _) => new TuiText("hello", 0, 0));

        Assert.True(component.HasContent());
        Assert.Equal(2, component.Children.Count); // Spacer(1) + the renderer component
        Assert.IsType<Spacer>(component.Children[0]);
        Assert.Equal("hello", string.Join("\n", component.Render(40).Select(AnsiText.StripAnsi)).Trim());
    }

    [Fact]
    public void RendererReturningUndefined_LeavesTheComponentEmpty()
    {
        var component = new CustomEntryComponent(Entry(), (_, _, _) => null);

        Assert.False(component.HasContent());
        Assert.Empty(component.Render(40));
    }

    [Fact]
    public void ThrowingRenderer_ProducesTheErrorBox()
    {
        var component = new CustomEntryComponent(Entry("gauge"), (_, _, _) => throw new InvalidOperationException("boom"));

        Assert.True(component.HasContent());
        var rendered = string.Join("\n", component.Render(80).Select(AnsiText.StripAnsi));
        Assert.Contains("[gauge] renderer failed: boom", rendered);
    }

    [Fact]
    public void SetExpanded_RebuildsWithTheNewFlag()
    {
        var seen = new List<bool>();
        var component = new CustomEntryComponent(Entry(), (_, options, _) =>
        {
            seen.Add(options.Expanded);
            return new TuiText($"expanded={options.Expanded}", 0, 0);
        });

        component.SetExpanded(true);
        component.SetExpanded(true); // no change -> no rebuild
        component.SetExpanded(false);

        Assert.Equal([false, true, false], seen);
    }

    [Fact]
    public void SetOutputPad_AndInvalidate_TriggerRebuild()
    {
        var builds = 0;
        var component = new CustomEntryComponent(Entry(), (_, _, _) =>
        {
            builds++;
            return new TuiText("x", 0, 0);
        });

        Assert.Equal(1, builds); // constructor
        component.SetOutputPad(2);
        Assert.Equal(2, builds);
        component.Invalidate();
        Assert.Equal(3, builds);
    }
}

public sealed class EarendilAnnouncementComponentTests : SelectorTestFixture
{
    [Fact]
    public void RendersTitleAndBlogLink_InsideAccentBorders()
    {
        var component = new EarendilAnnouncementComponent();

        var lines = component.Render(120).Select(AnsiText.StripAnsi).ToArray();
        var joined = string.Join("\n", lines);

        Assert.Contains("pi has joined Earendil", joined);
        Assert.Contains("https://mariozechner.at/posts/2026-04-08-ive-sold-out/", joined);
        Assert.Contains("Read the blog post:", joined);
        // Border rows bracket the content (the boxed layout of the TS original).
        Assert.Equal("─", new string(lines[0].Distinct().Where(c => c != ' ').ToArray()));
        Assert.Equal("─", new string(lines[^1].Distinct().Where(c => c != ' ').ToArray()));
    }
}
