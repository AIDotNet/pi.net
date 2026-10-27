using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// 深层嵌套引用块的回归：每多一层都会把子行按 <c>width - 2</c> 重新折行，宽度耗尽后
/// 仍按 1 列折，输出随深度指数增长（上游 #11272）。这里钉住「60 层也只出一行」。
/// </summary>
public class MarkdownBlockquoteDepthTests
{
    private static readonly MarkdownTheme PlainTheme = new()
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

    [Fact]
    public void QuotesNestedDeeperThanTheWidthRenderOneLine()
    {
        var markdown = new Markdown(new string('>', 60) + "x", 0, 0, PlainTheme);

        var lines = markdown.Render(80).Select(line => line.TrimEnd()).ToArray();

        Assert.Single(lines);
        Assert.EndsWith("x", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void QuotesThatFitKeepTheirBorder()
    {
        var markdown = new Markdown("> quoted", 0, 0, PlainTheme);

        var lines = markdown.Render(80).Select(line => line.TrimEnd()).ToArray();

        Assert.Single(lines);
        Assert.Contains("quoted", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("│ ", lines[0], StringComparison.Ordinal);
    }
}
