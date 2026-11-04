using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// 显示公式比内容宽度还宽时退回原始 LaTeX（`cd1ea342a`，上游 `markdown.test.ts` 的回归用例）：
/// 2D 排版（分式、求和）不能逐行折行，否则只有溢出的那一行断开、下面的行错位。
/// </summary>
public class MarkdownLatexFallbackTests
{
    private const string Source =
        "\\[\n\\frac{\\partial^2 u}{\\partial x^2} + \\frac{\\partial^2 u}{\\partial y^2} = "
        + "\\sum_{i=1}^{n} \\frac{1}{i^2} + \\int_0^1 f(x)\\,dx\n\\]";

    [Fact]
    public void FallsBackToRawSourceWhenDisplayMathIsWiderThanTheContentWidth()
    {
        var options = new MarkdownOptions { RenderLatex = true };
        var markdown = new Markdown(Source, 0, 0, Theme(), null, options);

        // 宽 40 放得下：完整 2D 排版。
        var wide = Render(markdown, 40);
        Assert.Equal(
            [
                "∂² u   ∂² u   n   1   1",
                "──── + ──── = ∑   ── +∫ f(x) dx",
                "∂ x²   ∂ y²  i=1  i²  0",
            ],
            wide);

        // 宽 30 放不下：整段退回原始源码，折行后不再有分数线，每行不超宽。
        var narrow = Render(markdown, 30);
        Assert.Equal("\\[", narrow[0]);
        Assert.Equal("\\]", narrow[^1]);
        Assert.DoesNotContain(narrow, line => line.Contains('─'));
        Assert.All(narrow, line => Assert.True(
            UnicodeWidth.VisibleWidth(line) <= 30, $"{line} ({UnicodeWidth.VisibleWidth(line)})"));
    }

    private static string[] Render(Markdown markdown, int width)
        => [.. markdown.Render(width).Select(line => JsString.TrimEnd(Ansi.StripTerminalSequences(line)))];

    private static MarkdownTheme Theme() => new()
    {
        Heading = text => text,
        Link = text => text,
        LinkUrl = text => text,
        Code = text => text,
        CodeBlock = text => text,
        CodeBlockBorder = text => text,
        Quote = text => text,
        QuoteBorder = text => text,
        Hr = text => text,
        ListBullet = text => text,
        Bold = text => text,
        Italic = text => text,
        Strikethrough = text => text,
        Underline = text => text,
    };
}
