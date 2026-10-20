// ============================================================================
// Codemode tool renderer decisions
// (port of extensions/codemode/renderer.ts)
//
// The renderer decides which component to build, what it contains, and how it is framed. These tests
// pin the decisions a reader of the transcript can see: the script preview, the "[invalid arg]" row,
// the dropped "Script completed" header, the collapsed call window with its total, the full-output
// pointer, and the cost/duration formatting. Component framing itself (wrapping, padding) belongs to
// the Tui tests.
//
// Note: unlike the seven built-in tool renderers, this one is not in the differential corpus — the
// generator stages `core/tools/renderers/*` and would need a new import chain for an extension
// renderer. The expectations here were written from the TS source, not from this implementation.
// ============================================================================

using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Extensions.Codemode;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;
using Text = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Tests;

public sealed class CodemodeRenderersTests : IDisposable
{
    private readonly bool? _previousChalk = Chalk.EnabledOverride;
    private readonly Pi.Tui.KeybindingsManager _previousGlobalKeybindings = GlobalKeybindings.Get();

    public CodemodeRenderersTests()
    {
        Chalk.EnabledOverride = true;
        ThemeRuntime.ResetCachesForTests();
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
        GlobalKeybindings.Set(new Pi.Tui.KeybindingsManager(AppKeybindings.Definitions));
    }

    public void Dispose()
    {
        GlobalKeybindings.Set(_previousGlobalKeybindings);
        Chalk.EnabledOverride = _previousChalk;
        ThemeRuntime.ResetCachesForTests();
    }

    private static ToolRenderContext Context(bool expanded = false, bool isError = false, bool isPartial = false)
        => new()
        {
            ToolCallId = "call-1",
            Invalidate = () => { },
            Cwd = "/work",
            Expanded = expanded,
            IsError = isError,
            IsPartial = isPartial,
        };

    private static string Plain(IComponent component) =>
        string.Join("\n", component.Render(200).Select(Ansi.StripTerminalSequences));

    private static AgentToolResult Result(
        IReadOnlyList<ContentBlock>? content = null,
        CodemodeToolApi.CodemodeToolDetails? details = null) =>
        new(content ?? [new TextContent("ok")], Details: details);

    private static CodemodeToolApi.CodemodeNestedCall Call(
        string name, string args = "{}", string status = CodemodeToolApi.NestedCallStatus.Ok,
        double? durationMs = null, string? error = null, double? cost = null) => new()
        {
            Id = $"call-1/{name}",
            Name = name,
            Args = args,
            Status = status,
            DurationMs = durationMs,
            Error = error,
            Cost = cost,
        };

    [Fact]
    public void RenderCallShowsTheScriptAsAHighlightedPreview()
    {
        var component = CodemodeRenderers.RenderCall(
            new Dictionary<string, object?> { ["code"] = "return await tools.read({ path: 'a' });" },
            ThemeRuntime.Current,
            Context());

        var container = Assert.IsType<Container>(component);
        Assert.Equal(
            "codemode",
            Ansi.StripTerminalSequences(Assert.IsType<Text>(container.Children[0]).CurrentText));
        var preview = Assert.IsType<VisualLinePreview>(container.Children[1]);
        Assert.Equal(10, preview.Options.MaxVisualLines);
        Assert.Equal("start", preview.Options.Keep);
        Assert.Contains("tools.read", preview.Options.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCallShowsTheWholeScriptWhenExpanded()
    {
        var component = CodemodeRenderers.RenderCall(
            new Dictionary<string, object?> { ["code"] = "return 1;" },
            ThemeRuntime.Current,
            Context(expanded: true));

        var container = Assert.IsType<Container>(component);
        Assert.IsType<Text>(container.Children[1]);
        Assert.Contains("return 1;", Plain(component), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCallFlagsAWronglyTypedScript()
    {
        var component = CodemodeRenderers.RenderCall(
            new Dictionary<string, object?> { ["code"] = 42 },
            ThemeRuntime.Current,
            Context());

        var container = Assert.IsType<Container>(component);
        var text = Assert.IsType<Text>(Assert.Single(container.Children));
        Assert.Contains("[invalid arg]", text.CurrentText, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCallOmitsAnEmptyScript()
    {
        var component = CodemodeRenderers.RenderCall(
            new Dictionary<string, object?>(),
            ThemeRuntime.Current,
            Context());

        var container = Assert.IsType<Container>(component);
        Assert.Single(container.Children);
    }

    [Fact]
    public void RenderResultDropsTheScriptHeaderAndKeepsTheOutput()
    {
        var component = CodemodeRenderers.RenderResult(
            Result([
                new TextContent("Script completed\nWall time 0.4 seconds\nOutput:\n"),
                new TextContent("hello"),
            ]),
            new ToolRenderResultOptions(Expanded: false, IsPartial: false),
            ThemeRuntime.Current,
            Context());

        var text = Plain(component);
        Assert.Contains("hello", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Script completed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Wall time", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderResultKeepsTextThatOnlyLooksLikeAHeader()
    {
        var component = CodemodeRenderers.RenderResult(
            Result([new TextContent("Script rejected: invalid options")]),
            new ToolRenderResultOptions(Expanded: false, IsPartial: false),
            ThemeRuntime.Current,
            Context());

        Assert.Contains("Script rejected: invalid options", Plain(component), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderResultKeepsAHeaderFollowedByAnExtraNewline()
    {
        // The JS `$` anchors at the end of the input, so a trailing blank line means "not a header";
        // .NET's `$` would also match before a final newline, which is why the port uses `\z`.
        var component = CodemodeRenderers.RenderResult(
            Result([new TextContent("Script completed\nWall time 0.4 seconds\nOutput:\n\n")]),
            new ToolRenderResultOptions(Expanded: false, IsPartial: false),
            ThemeRuntime.Current,
            Context());

        Assert.Contains("Script completed", Plain(component), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderResultCollapsesToTheLastEightCallsAndNamesTheHiddenOnes()
    {
        var calls = Enumerable.Range(0, 10).Select(i => Call($"tool{i}", $"{{ n: {i} }}")).ToList();
        var component = CodemodeRenderers.RenderResult(
            Result(details: new CodemodeToolApi.CodemodeToolDetails { Calls = calls }),
            new ToolRenderResultOptions(Expanded: false, IsPartial: false),
            ThemeRuntime.Current,
            Context());

        var text = Plain(component);
        Assert.Contains("2 earlier calls", text, StringComparison.Ordinal);
        Assert.Contains("tool2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tool1 ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderResultShowsEveryCallWhenExpanded()
    {
        var calls = Enumerable.Range(0, 10).Select(i => Call($"tool{i}")).ToList();
        var component = CodemodeRenderers.RenderResult(
            Result(details: new CodemodeToolApi.CodemodeToolDetails { Calls = calls }),
            new ToolRenderResultOptions(Expanded: true, IsPartial: false),
            ThemeRuntime.Current,
            Context(expanded: true));

        var text = Plain(component);
        Assert.DoesNotContain("earlier calls", text, StringComparison.Ordinal);
        Assert.Contains("tool0", text, StringComparison.Ordinal);
        Assert.Contains("tool9", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderResultTotalsTheModelCallCostWhenMoreThanOneReportedOne()
    {
        var calls = new List<CodemodeToolApi.CodemodeNestedCall>
        {
            Call("models.classify", cost: 0.00003),
            Call("models.classify", cost: 0.00006),
        };
        var component = CodemodeRenderers.RenderResult(
            Result(details: new CodemodeToolApi.CodemodeToolDetails { Calls = calls }),
            new ToolRenderResultOptions(Expanded: false, IsPartial: false),
            ThemeRuntime.Current,
            Context());

        var text = Plain(component);
        // Per-call costs keep two significant digits below a cent; the total is cents.
        Assert.Contains("$0.000030", text, StringComparison.Ordinal);
        Assert.Contains("Model calls: $0.00", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderResultFormatsDurationsBelowAndAboveASecond()
    {
        var calls = new List<CodemodeToolApi.CodemodeNestedCall>
        {
            Call("fast", durationMs: 42),
            Call("slow", durationMs: 1250),
        };
        var component = CodemodeRenderers.RenderResult(
            Result(details: new CodemodeToolApi.CodemodeToolDetails { Calls = calls }),
            new ToolRenderResultOptions(Expanded: false, IsPartial: false),
            ThemeRuntime.Current,
            Context());

        var text = Plain(component);
        Assert.Contains("42ms", text, StringComparison.Ordinal);
        Assert.Contains("1.3s", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderResultShowsTheErrorOfAFailedCallOnlyWhenExpanded()
    {
        var calls = new List<CodemodeToolApi.CodemodeNestedCall>
        {
            Call("explode", status: CodemodeToolApi.NestedCallStatus.Error, error: "boom\nsecond line"),
        };
        var collapsed = Plain(CodemodeRenderers.RenderResult(
            Result(details: new CodemodeToolApi.CodemodeToolDetails { Calls = calls }),
            new ToolRenderResultOptions(Expanded: false, IsPartial: false),
            ThemeRuntime.Current,
            Context()));
        var expanded = Plain(CodemodeRenderers.RenderResult(
            Result(details: new CodemodeToolApi.CodemodeToolDetails { Calls = calls }),
            new ToolRenderResultOptions(Expanded: true, IsPartial: false),
            ThemeRuntime.Current,
            Context(expanded: true)));

        Assert.DoesNotContain("boom", collapsed, StringComparison.Ordinal);
        Assert.Contains("boom", expanded, StringComparison.Ordinal);
        // The error's continuation lines are indented under the call row.
        Assert.Contains("\n    second line", expanded, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderResultNamesTheFullOutputFileWhenCollapsed()
    {
        var details = new CodemodeToolApi.CodemodeToolDetails
        {
            Calls = [Call("tool")],
            FullOutputPath = "/tmp/codemode-1.txt",
        };
        var component = CodemodeRenderers.RenderResult(
            Result([new TextContent("Script completed\nWall time 1.0 seconds\nOutput:\n"), new TextContent("line")], details),
            new ToolRenderResultOptions(Expanded: false, IsPartial: false),
            ThemeRuntime.Current,
            Context());

        Assert.Contains("Full output: /tmp/codemode-1.txt", Plain(component), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderResultRendersNoOutputWhileTheCallIsPartial()
    {
        var component = CodemodeRenderers.RenderResult(
            Result([new TextContent("partial text")]),
            new ToolRenderResultOptions(Expanded: false, IsPartial: true),
            ThemeRuntime.Current,
            Context(isPartial: true));

        Assert.DoesNotContain("partial text", Plain(component), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderResultReusesThePreviousContainer()
    {
        var existing = new Container();
        existing.AddChild(new Text("stale", 0, 0));
        var component = CodemodeRenderers.RenderResult(
            Result([new TextContent("fresh")]),
            new ToolRenderResultOptions(Expanded: false, IsPartial: false),
            ThemeRuntime.Current,
            Context() with { LastComponent = existing });

        Assert.Same(existing, component);
        Assert.DoesNotContain("stale", Plain(component), StringComparison.Ordinal);
        Assert.Contains("fresh", Plain(component), StringComparison.Ordinal);
    }
}
