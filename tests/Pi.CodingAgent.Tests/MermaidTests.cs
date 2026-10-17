// ============================================================================
// Tests for modes/interactive/components/mermaid.ts (4f-3e Batch 4).
//
// The upstream tests assert real box-drawing output from grok-mermaid, which this port does not
// ship (see the header of Mermaid.cs). These vectors therefore pin the transformer skeleton —
// mode gating, thinking/streaming rules, oversize fallback, warning summary, and the inline-code
// encoding — by driving it with a fake renderer and a marker theme.
// ============================================================================

using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui.Marked;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class MermaidTests
{
    // ---------------------------------------------------------------- fixtures

    private sealed class FakeRenderer(Func<string, MermaidArt?> render) : IMermaidRenderer
    {
        public List<string> Sources { get; } = [];

        public MermaidArt? Render(string source)
        {
            Sources.Add(source);
            return render(source);
        }
    }

    /// <summary>Stands in for the TS test's structural <c>{ fg, bold }</c> theme mock.</summary>
    private sealed class MarkerTheme : IMermaidTheme
    {
        public string Fg(string token, string text) => $"<{token}>{text}</{token}>";

        public string Bold(string text) => $"<bold>{text}</bold>";
    }

    private static MermaidArt Art(
        int width,
        IReadOnlyList<string>? plain = null,
        IReadOnlyList<string>? warnings = null)
    {
        var rows = plain ?? ["┌───┐", "│ A │", "└───┘"];
        return new MermaidArt
        {
            Plain = rows,
            Styled =
            [
                .. rows.Select(row => (IReadOnlyList<MermaidSpan>)
                    [new MermaidSpan { Text = row, Cls = MermaidSpanClass.Border }]),
            ],
            Width = width,
            Warnings = warnings ?? [],
        };
    }

    private static string Transform(
        string markdown,
        string mode = "streaming",
        bool isStreaming = false,
        string messageType = "assistant",
        int availableWidth = 100,
        IMermaidRenderer? renderer = null,
        // TS passes options.theme only when the caller supplies one, so the default is plain.
        bool themed = false)
    {
        var transformer = Mermaid.CreateMarkdownTransformer(
            new MermaidTransformerOptions { GetMode = () => mode, Renderer = renderer },
            themed ? new MarkerTheme() : null);
        return transformer(
            markdown,
            new MarkdownTransformContext
            {
                MessageType = messageType,
                IsStreaming = isStreaming,
                AvailableWidth = availableWidth,
            });
    }

    private const string Flowchart = "```mermaid\nflowchart LR\n  A --> B\n```";

    private static FakeRenderer TwoRows() => new(_ => Art(7, ["A", "B"]));

    // ---------------------------------------------------------------- replacing blocks

    [Fact]
    public void ReplacesMermaidBlocksWithOneInlineCodeRowPerLine()
    {
        var rendered = Transform(Flowchart, renderer: TwoRows());

        // Hard breaks between rows, one trailing newline after the block.
        Assert.EndsWith("`A`  \n`B`\n", rendered);
        Assert.DoesNotContain("```mermaid", rendered);
    }

    [Fact]
    public void PassesTheFencedBlockTextToTheRenderer()
    {
        var renderer = TwoRows();
        Transform(Flowchart, renderer: renderer);

        // TS: render(token.text) — the fenced content without the fence lines.
        Assert.Equal(["flowchart LR\n  A --> B"], renderer.Sources);
    }

    [Fact]
    public void KeepsTheMarkdownAroundTheBlock()
    {
        var rendered = Transform($"Before\n\n{Flowchart}\nAfter", renderer: TwoRows());

        Assert.StartsWith("Before\n\n", rendered);
        Assert.EndsWith("`B`\nAfter", rendered);
    }

    [Fact]
    public void LeavesNonMermaidCodeBlocksUntouched()
    {
        const string markdown = "```csharp\nvar x = 1;\n```";
        var renderer = TwoRows();

        Assert.Equal(markdown, Transform(markdown, renderer: renderer));
        Assert.Empty(renderer.Sources);
    }

    [Fact]
    public void MatchesTheInfoStringCaseInsensitivelyAndIgnoresExtraWords()
    {
        var renderer = TwoRows();
        var rendered = Transform("```Mermaid title=Demo\nflowchart LR\n```", renderer: renderer);

        Assert.Single(renderer.Sources);
        Assert.Contains("`A`", rendered);
    }

    [Fact]
    public void IgnoresIndentedCodeBlocks()
    {
        // TS requires token.type === "code" *and* a lang; indented blocks have no lang.
        const string markdown = "    flowchart LR\n    A --> B";
        var renderer = TwoRows();

        Assert.Equal(markdown, Transform(markdown, renderer: renderer));
        Assert.Empty(renderer.Sources);
    }

    // ---------------------------------------------------------------- mode gating

    [Fact]
    public void ReturnsTheMarkdownUnchangedWhenRenderingIsOff()
    {
        var renderer = TwoRows();

        Assert.Equal(Flowchart, Transform(Flowchart, mode: "off", renderer: renderer));
        Assert.Empty(renderer.Sources);
    }

    [Fact]
    public void SkipsThinkingBlocks()
    {
        var renderer = TwoRows();

        Assert.Equal(
            Flowchart,
            Transform(Flowchart, messageType: "assistant-thinking", renderer: renderer));
        Assert.Empty(renderer.Sources);
    }

    [Fact]
    public void SkipsRenderingWhileStreamingUnlessTheModeIsStreaming()
    {
        var renderer = TwoRows();

        Assert.Equal(Flowchart, Transform(Flowchart, mode: "final", isStreaming: true, renderer: renderer));
        Assert.Empty(renderer.Sources);
    }

    [Fact]
    public void RendersOnceSettledInFinalMode()
    {
        var rendered = Transform(Flowchart, mode: "final", renderer: TwoRows());

        Assert.DoesNotContain("```mermaid", rendered);
        Assert.Contains("`A`", rendered);
    }

    [Fact]
    public void RendersIncompleteBlocksWhileStreamingInStreamingMode()
    {
        var renderer = TwoRows();
        var rendered = Transform("```mermaid\nflowchart LR\n  A --> B", isStreaming: true, renderer: renderer);

        Assert.Contains("`A`", rendered);
        Assert.DoesNotContain("```mermaid", rendered);
    }

    [Fact]
    public void ReadsTheModeOnEveryCall()
    {
        // TS takes getMode as a callback so a settings change applies to the next transform.
        var mode = "off";
        var transformer = Mermaid.CreateMarkdownTransformer(
            new MermaidTransformerOptions { GetMode = () => mode, Renderer = TwoRows() },
            null);
        var context = new MarkdownTransformContext
        {
            MessageType = "assistant",
            IsStreaming = false,
            AvailableWidth = 100,
        };

        Assert.Equal(Flowchart, transformer(Flowchart, context));

        mode = "final";
        Assert.Contains("`A`", transformer(Flowchart, context));
    }

    // ---------------------------------------------------------------- fallbacks

    [Fact]
    public void FallsBackToTheRawBlockWhenTheRendererHasNoArt()
    {
        // Null means: no engine, blank input, syntax error, unsupported kind, or refused layout.
        var rendered = Transform(Flowchart, renderer: new FakeRenderer(_ => null));

        Assert.Equal(Flowchart, rendered);
    }

    [Fact]
    public void LeavesEverythingUntouchedWithoutARenderer()
    {
        // The port ships no engine; the default must be a byte-identical passthrough.
        Assert.Equal(Flowchart, Transform(Flowchart));
    }

    [Fact]
    public void FallsBackToTheRawBlockWhenTheArtIsWiderThanTheSpaceAvailable()
    {
        Assert.Equal(Flowchart, Transform(Flowchart, availableWidth: 10, renderer: new FakeRenderer(_ => Art(11))));
    }

    [Fact]
    public void RendersArtThatExactlyFitsTheSpaceAvailable()
    {
        var rendered = Transform(Flowchart, availableWidth: 10, renderer: new FakeRenderer(_ => Art(10)));

        Assert.DoesNotContain("```mermaid", rendered);
    }

    // ---------------------------------------------------------------- warnings

    [Fact]
    public void WarnsInsteadOfShowingPartialArtOnceTheBlockSettles()
    {
        var rendered = Transform(
            Flowchart,
            renderer: new FakeRenderer(_ => Art(7, warnings: ["dropped, expected a link: \"invalid\""])));

        Assert.Contains("```\n`Mermaid diagram not rendered", rendered);
        Assert.Contains("dropped, expected a link: \"invalid\"", rendered);
        Assert.DoesNotContain("more)", rendered);
        Assert.EndsWith("`  \n", rendered);
    }

    [Fact]
    public void SummarizesAdditionalWarningsWithoutListingThem()
    {
        var rendered = Transform(
            Flowchart,
            renderer: new FakeRenderer(_ => Art(7, warnings: ["first problem", "second problem"])));

        Assert.Contains("Mermaid diagram not rendered: first problem (+1 more)", rendered);
        Assert.DoesNotContain("second problem", rendered);
    }

    [Fact]
    public void SuppressesWarningsWhileStreaming()
    {
        // A diagram being typed warns at nearly every intermediate state, so the art is shown
        // and the warnings wait until it settles.
        var rendered = Transform(
            Flowchart,
            isStreaming: true,
            renderer: new FakeRenderer(_ => Art(7, ["A"], ["boom"])));

        Assert.DoesNotContain("Mermaid diagram not rendered", rendered);
        Assert.Contains("`A`", rendered);
    }

    // ---------------------------------------------------------------- theming

    [Fact]
    public void MapsEverySemanticClassThroughTheTheme()
    {
        var art = new MermaidArt
        {
            Plain = ["x"],
            Styled =
            [
                [
                    new MermaidSpan { Text = "b", Cls = MermaidSpanClass.Border },
                    new MermaidSpan { Text = "t", Cls = MermaidSpanClass.Text },
                    new MermaidSpan { Text = "e", Cls = MermaidSpanClass.Edge },
                    new MermaidSpan { Text = "l", Cls = MermaidSpanClass.EdgeLabel },
                    new MermaidSpan { Text = "h", Cls = MermaidSpanClass.Title },
                    new MermaidSpan { Text = "n", Cls = MermaidSpanClass.None },
                ],
            ],
            Width = 6,
        };
        var theme = new MarkerTheme();

        Assert.Equal(
            [
                "<borderMuted>b</borderMuted>"
                + "<text>t</text>"
                + "<accent>e</accent>"
                + "<muted>l</muted>"
                + "<accent><bold>h</bold></accent>"
                + "n",
            ],
            Mermaid.ThemedLines(art, theme));
    }

    [Fact]
    public void StylesRenderedRowsWithTheTheme()
    {
        var rendered = Transform(Flowchart, renderer: TwoRows(), themed: true);

        Assert.Contains("<borderMuted>A</borderMuted>", rendered);
    }

    [Fact]
    public void UsesThePlainRowsWhenNoThemeIsSupplied()
    {
        var rendered = Transform(Flowchart, renderer: TwoRows());

        Assert.EndsWith("`A`  \n`B`\n", rendered);
    }

    // ---------------------------------------------------------------- inline code encoding

    [Fact]
    public void WrapsEachRowInInlineCode()
    {
        Assert.Equal("`plain`", Mermaid.CodeSpan("plain"));
        Assert.Equal("`no ticks`", Mermaid.CodeSpan("no ticks"));
    }

    [Fact]
    public void UsesANonBreakingSpaceForBlankRows()
    {
        // An empty code span has no visible height, so a blank diagram row becomes U+00A0.
        Assert.Equal("`\u00a0`", Mermaid.CodeSpan(""));
    }

    [Fact]
    public void OutgrowsTheLongestBacktickRunInTheRow()
    {
        Assert.Equal("``a`b``", Mermaid.CodeSpan("a`b"));
        Assert.Equal("```a``b```", Mermaid.CodeSpan("a``b"));
    }

    [Fact]
    public void PadsRowsThatStartOrEndWithABacktick()
    {
        // CommonMark strips the padding again, leaving the backtick as content.
        Assert.Equal("`` `edge` ``", Mermaid.CodeSpan("`edge`"));
        Assert.Equal("``` `` ```", Mermaid.CodeSpan("``"));
    }

    // ---------------------------------------------------------------- block detection

    [Fact]
    public void IsMermaidRequiresACodeTokenWithAnInfoString()
    {
        Assert.True(Mermaid.IsMermaid(new MarkedToken { Type = "code", Lang = "mermaid" }));
        Assert.True(Mermaid.IsMermaid(new MarkedToken { Type = "code", Lang = "  MERMAID  " }));
        Assert.True(Mermaid.IsMermaid(new MarkedToken { Type = "code", Lang = "mermaid extra" }));

        Assert.False(Mermaid.IsMermaid(new MarkedToken { Type = "code", Lang = null }));
        Assert.False(Mermaid.IsMermaid(new MarkedToken { Type = "code", Lang = "" }));
        Assert.False(Mermaid.IsMermaid(new MarkedToken { Type = "code", Lang = "mermaidx" }));
        Assert.False(Mermaid.IsMermaid(new MarkedToken { Type = "code", Lang = "not-mermaid" }));
        Assert.False(Mermaid.IsMermaid(new MarkedToken { Type = "paragraph", Lang = "mermaid" }));
    }
}
