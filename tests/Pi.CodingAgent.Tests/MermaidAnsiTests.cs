using Pi.CodingAgent.Modes.Interactive.Components;
using Xunit;

namespace Pi.CodingAgent.Tests;

// ============================================================================
// 上游 v1.5.1 #8158（aceb1bd12）：Mermaid 终端渲染升级。
//
// 端口 Mermaid.cs 不移植第三方渲染引擎（grok-/lovely-mermaid，render() 是注入 seam），
// 但 #8158 改动里有完全可移植、可测的**着色/超链接语义**：
//   1. themedLines 从 theme.fg(token, text) 包装改为 getFgAnsi(color).slice(2,-1) 派生
//      AnsiTheme 的 SGR 参数串，再 toAnsi 输出 \x1b[<sgr>m...\x1b[39m；
//   2. edgeLabel 语义色由 muted 改为 text；
//   3. title 用 `1;<accent>` 加粗（SGR 1 而非 theme.bold()）；
//   4. span 携带 href/role，displayArt 只给 role=="text" 的标签保留 href，
//      且仅当 getCapabilities().hyperlinks 为真时才输出 OSC 8 链接
//      （"Only osc 8 the text, not the whole box"）。
//
// 本测试逐字对照上游 test/mermaid.test.ts 的 #8158 新向量：
//   maps semantic spans through the Pi theme / links only Mermaid labels and only
//   when the terminal supports hyperlinks。既有 MermaidTests 的 marker 路径不回归。
// ============================================================================

public sealed class MermaidAnsiTests
{
    // 与上游测试同款 getFgAnsi mock：把颜色名映射成 \x1b[38;5;N m。
    private static readonly Dictionary<string, string> Colors = new(StringComparer.Ordinal)
    {
        ["borderMuted"] = "240",
        ["text"] = "45",
        ["accent"] = "75",
        ["muted"] = "245",
        ["warning"] = "180",
    };

    private static string FgAnsi(string color) => $"\x1b[38;5;{Colors[color]}m";

    /// <summary>提供 #8158 需要的能力（GetFgAnsi + Hyperlinks）的主题 seam。</summary>
    private sealed class AnsiTheme(bool hyperlinks) : IMermaidTheme
    {
        public string Fg(string token, string text) => $"<{token}>{text}</{token}>";
        public string Bold(string text) => $"<bold>{text}</bold>";
        public string? GetFgAnsi(string color) => FgAnsi(color);
        public bool Hyperlinks => hyperlinks;
    }

    private static MermaidArt Art(params MermaidSpan[] spans) => new()
    {
        Plain = [string.Concat(spans.Select(span => span.Text))],
        Styled = [spans],
        Width = spans.Sum(span => span.Text.Length),
        Warnings = [],
    };

    [Fact]
    public void AnsiTheme_DerivesSgrParamsAndRepsects8158ColorChanges()
    {
        var theme = MermaidAnsiTheme.From(FgAnsi);
        // border/text/edge 分别来自 borderMuted/text/accent 的 SGR 参数串。
        Assert.Equal("38;5;240", theme.Border);
        Assert.Equal("38;5;45", theme.Text);
        Assert.Equal("38;5;75", theme.Edge);
        // #8158：edgeLabel 从 muted 改为 text。
        Assert.Equal("38;5;45", theme.EdgeLabel);
        Assert.NotEqual("38;5;245", theme.EdgeLabel);
        // #8158：title 前置 1; 加粗（不再用 theme.bold()）。
        Assert.Equal("1;38;5;75", theme.Title);
    }

    [Fact]
    public void ToAnsi_MapsSemanticSpansThroughSgrOpenings()
    {
        var art = Art(
            new MermaidSpan { Text = "B", Cls = MermaidSpanClass.Border },
            new MermaidSpan { Text = "T", Cls = MermaidSpanClass.Text },
            new MermaidSpan { Text = "E", Cls = MermaidSpanClass.Edge },
            new MermaidSpan { Text = "L", Cls = MermaidSpanClass.EdgeLabel },
            new MermaidSpan { Text = "H", Cls = MermaidSpanClass.Title },
            new MermaidSpan { Text = "N", Cls = MermaidSpanClass.None });
        var line = Assert.Single(Mermaid.ToAnsi(art, MermaidAnsiTheme.From(FgAnsi), hyperlinks: false));

        // 上游测试断言输出含 \x1b[38;5;240m 与 \x1b[38;5;45m；这里逐 span 核对全套。
        Assert.Equal(
            "\x1b[38;5;240mB\x1b[39m"
            + "\x1b[38;5;45mT\x1b[39m"
            + "\x1b[38;5;75mE\x1b[39m"
            + "\x1b[38;5;45mL\x1b[39m"        // edgeLabel 用 text 色
            + "\x1b[1;38;5;75mH\x1b[39m"      // title 加粗
            + "N",                             // none 不着色
            line);
    }

    [Fact]
    public void ToAnsi_LinksOnlyTextRoleLabelsWhenHyperlinksSupported()
    {
        var art = Art(
            new MermaidSpan { Text = "┌───┐", Cls = MermaidSpanClass.Border, Href = "https://example.com/docs" },
            new MermaidSpan { Text = "Node", Cls = MermaidSpanClass.Text, Role = "text", Href = "https://example.com/docs" });
        const string linkOpen = "\x1b]8;;https://example.com/docs\x1b\\";

        var linked = Assert.Single(Mermaid.ToAnsi(art, MermaidAnsiTheme.From(FgAnsi), hyperlinks: true));
        // 只有 text 角色的标签被 OSC 8 包裹；边框 span 即便带 href 也不链接（"not the whole box"）。
        Assert.Equal(1, linked.Split(linkOpen).Length - 1);
        Assert.Contains($"{linkOpen}\x1b[38;5;45mNode\x1b[39m\x1b]8;;\x1b\\", linked);
        Assert.StartsWith("\x1b[38;5;240m┌───┐\x1b[39m", linked);

        var unlinked = Assert.Single(Mermaid.ToAnsi(art, MermaidAnsiTheme.From(FgAnsi), hyperlinks: false));
        Assert.DoesNotContain("\x1b]8;;", unlinked);
        Assert.DoesNotContain("https://example.com/docs", unlinked);
    }

    [Fact]
    public void ToAnsi_IgnoresHrefOnNonTextSpansEvenWhenHyperlinksSupported()
    {
        var art = Art(
            new MermaidSpan { Text = "─", Cls = MermaidSpanClass.Edge, Role = "edge", Href = "https://example.com/x" },
            new MermaidSpan { Text = "t", Cls = MermaidSpanClass.Text, Href = "https://example.com/x" });
        var line = Assert.Single(Mermaid.ToAnsi(art, MermaidAnsiTheme.From(FgAnsi), hyperlinks: true));
        Assert.DoesNotContain("\x1b]8;;", line); // 无 role=="text"，整块不链接
    }

    [Fact]
    public void ThemedLines_UsesAnsiPathWhenThemeProvidesFgAnsi()
    {
        var art = Art(new MermaidSpan { Text = "x", Cls = MermaidSpanClass.Border });
        var lines = Mermaid.ThemedLines(art, new AnsiTheme(hyperlinks: false));
        // 走 #8158 ANSI 路径，而非旧 marker 的 <borderMuted>x</borderMuted>。
        Assert.Equal(["\x1b[38;5;240mx\x1b[39m"], lines);
    }
}
