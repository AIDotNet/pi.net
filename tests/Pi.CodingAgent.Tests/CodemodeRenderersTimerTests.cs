// ============================================================================
// Codemode row timing and the shared renderer state
// (upstream 014873d7d "show elapsed and total time on codemode rows", #10708).
//
// The timer logic moved from the bash renderer to render-utils, and both rows now keep their clock in
// the row's `state`. The port's ToolExecutionComponent supplies a state *bag* (a dictionary) rather
// than the typed object the renderers cast to, which made every rich row throw and silently fall back
// to plain text; these tests pin the bag path as well as the codemode timing line.
// ============================================================================

using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Core.Tools.Renderers;
using Pi.CodingAgent.Extensions.Codemode;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class CodemodeRenderersTimerTests : IDisposable
{
    private readonly bool? _previousChalk = Chalk.EnabledOverride;
    private readonly Pi.Tui.KeybindingsManager _previousGlobalKeybindings = GlobalKeybindings.Get();

    public CodemodeRenderersTimerTests()
    {
        Chalk.EnabledOverride = true;
        ThemeRuntime.ResetCachesForTests();
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
        GlobalKeybindings.Set(new Pi.Tui.KeybindingsManager(AppKeybindings.Definitions));
    }

    public void Dispose()
    {
        RenderUtils.NowOverride = null;
        GlobalKeybindings.Set(_previousGlobalKeybindings);
        Chalk.EnabledOverride = _previousChalk;
        ThemeRuntime.ResetCachesForTests();
    }

    /// <summary>The state bag <c>ToolExecutionComponent</c> passes as the row's renderer state.</summary>
    private static Dictionary<string, object?> Bag() => [];

    private static ToolRenderContext Context(
        object? state,
        bool executionStarted = true,
        bool isError = false,
        long? durationMs = null) => new()
    {
        ToolCallId = "call-1",
        Invalidate = () => { },
        State = state,
        Cwd = "/tmp",
        ExecutionStarted = executionStarted,
        ArgsComplete = true,
        IsPartial = true,
        Expanded = false,
        ShowImages = false,
        IsError = isError,
        DurationMs = durationMs,
    };

    private static string Render(IComponent component) =>
        string.Join("\n", component.Render(80).Select(AnsiText.StripAnsi));

    [Fact]
    public void StateOf_KeepsOneInstancePerRowAndPassesATypedStateThrough()
    {
        var bag = Bag();
        var context = Context(bag);

        var first = RenderUtils.StateOf<RenderUtils.ToolTimerState>(context);
        var second = RenderUtils.StateOf<RenderUtils.ToolTimerState>(context);
        Assert.Same(first, second);

        // A typed state passed as is (the renderer corpus does that) is used, not replaced.
        var typed = new ShellRenderState();
        Assert.Same(typed, RenderUtils.StateOf<ShellRenderState>(Context(typed)));
    }

    [Fact]
    public void CodemodeRow_ShowsElapsedWhileRunningAndTookWhenDone()
    {
        RenderUtils.NowOverride = () => 1_000_000;
        var bag = Bag();
        var context = Context(bag);

        // renderCall starts the row's clock once execution starts.
        _ = CodemodeRenderers.RenderCall(
            new Dictionary<string, object?> { ["code"] = "return 1" }, ThemeRuntime.Current, context);
        RenderUtils.NowOverride = () => 1_004_200;

        var running = Render(
            CodemodeRenderers.RenderResult(
                new AgentToolResult([new TextContent("hello")]),
                new ToolRenderResultOptions(Expanded: false, IsPartial: true),
                ThemeRuntime.Current,
                context));
        Assert.Contains("Elapsed 4.2s", running, StringComparison.Ordinal);

        // A final result's recorded duration wins over the renderer's clock.
        var done = Render(
            CodemodeRenderers.RenderResult(
                new AgentToolResult([new TextContent("hello")]),
                new ToolRenderResultOptions(Expanded: false, IsPartial: false),
                ThemeRuntime.Current,
                context with { IsPartial = false, DurationMs = 9_500 }));
        Assert.Contains("Took 9.5s", done, StringComparison.Ordinal);
        Assert.DoesNotContain("Elapsed", done, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellRow_KeepsItsClockInTheComponentStateBag()
    {
        // Before the shared StateOf, the shell renderers cast the bag to ShellRenderState, threw, and
        // the component silently rendered the plain fallback instead.
        RenderUtils.NowOverride = () => 1_000_000;
        var renderers = BashRenderers.CreateShellRenderers("$");
        var context = Context(Bag());

        _ = renderers.RenderCall!(new Dictionary<string, object?> { ["command"] = "echo hi" },
            ThemeRuntime.Current, context);
        RenderUtils.NowOverride = () => 1_012_000;

        var rendered = Render(renderers.RenderResult!(
            new AgentToolResult([new TextContent("hi")]),
            new ToolRenderResultOptions(Expanded: false, IsPartial: true),
            ThemeRuntime.Current,
            context));

        Assert.Contains("Elapsed 12.0s", rendered, StringComparison.Ordinal);
    }
}
