// ============================================================================
// Bash renderer duration semantics
// (upstream 36a686ee8 "record response, tool execution, and durable task times").
//
// The corpus harness drives `renderers/bash.ts` through its own mock clock, so the
// recorded-duration preference needs a direct test: a final result's recorded
// `durationMs` wins over the renderer's clock, and is ignored while the call is partial.
// ============================================================================

using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Tools.Renderers;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class BashRenderersDurationTests : IDisposable
{
    private readonly bool? _previousChalk = Chalk.EnabledOverride;
    private readonly Pi.Tui.KeybindingsManager _previousGlobalKeybindings = GlobalKeybindings.Get();

    public BashRenderersDurationTests()
    {
        Chalk.EnabledOverride = true;
        ThemeRuntime.ResetCachesForTests();
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
        GlobalKeybindings.Set(new Pi.Tui.KeybindingsManager(AppKeybindings.Definitions));
    }

    public void Dispose()
    {
        BashRenderers.NowOverride = null;
        GlobalKeybindings.Set(_previousGlobalKeybindings);
        Chalk.EnabledOverride = _previousChalk;
        ThemeRuntime.ResetCachesForTests();
    }

    private static string Render(
        bool isPartial,
        long? startedAt,
        long? endedAt,
        long? durationMs)
    {
        var component = new Container();
        BashRenderers.RebuildBashResultRenderComponent(
            component,
            new AgentToolResult([new TextContent("hello")]),
            new ToolRenderResultOptions(Expanded: false, IsPartial: isPartial),
            showImages: false,
            startedAt,
            endedAt,
            durationMs);

        return string.Join("\n", component.Render(80).Select(AnsiText.StripAnsi));
    }

    [Fact]
    public void RecordedDurationWinsOverTheRendererClock()
    {
        // The renderer's own clock would say 10s; the recorded 1.5s must win.
        BashRenderers.NowOverride = () => 10_000;

        var rendered = Render(isPartial: false, startedAt: 0, endedAt: null, durationMs: 1_500);

        Assert.Contains("Took 1.5s", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Took 10.0s", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ClockIsTheFallbackWhenNoDurationWasRecorded()
    {
        BashRenderers.NowOverride = () => 10_000;

        var rendered = Render(isPartial: false, startedAt: 0, endedAt: null, durationMs: null);

        Assert.Contains("Took 10.0s", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialResultsIgnoreTheRecordedDuration()
    {
        // A partial result is still running: the label is `Elapsed` and it comes from the clock.
        BashRenderers.NowOverride = () => 10_000;

        var rendered = Render(isPartial: true, startedAt: 0, endedAt: null, durationMs: 1_500);

        Assert.Contains("Elapsed 10.0s", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Took 1.5s", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDurationLineWithoutAClockOrARecordedDuration()
    {
        var rendered = Render(isPartial: false, startedAt: null, endedAt: null, durationMs: null);

        Assert.DoesNotContain("Took", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Elapsed", rendered, StringComparison.Ordinal);
    }
}
