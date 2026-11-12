using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// 上游 1.5.1 tui #11710 / #11755：HTML 注释不能作为可见文本渲染。
/// 用例沿用上游口径——带注释的渲染结果必须等于去掉注释后的渲染结果，避免把空行数量钉死。
/// </summary>
public class MarkdownHtmlCommentTests
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

    private static string[] Render(string text)
        => new Markdown(text, 0, 0, PlainTheme).Render(60).Select(line => line.TrimEnd()).ToArray();

    [Fact]
    public void HidesBlockAndInlineCommentsWithoutExtraSpacing()
    {
        Assert.Equal(
            Render("## [1.0.0]\n\nFixed a bug.\n\nSee the docs."),
            Render("## [1.0.0]\n\n<!-- release-notes:start -->\nFixed a bug.\n\nSee <!-- todo: link --> the docs."));
    }

    [Fact]
    public void DropsOneBlankLineAroundCommentOnlyBlocks()
    {
        Assert.Equal(
            Render("## [1.0.0]\n\n- item\n\n## [0.9.0]"),
            Render("<!-- start -->\n\n## [1.0.0]\n\n<!-- a -->\n\n- item\n\n<!-- b -->\n\n## [0.9.0]\n\n<!-- end -->"));
    }

    [Fact]
    public void HidesMultilineAndUnclosedComments()
    {
        Assert.Equal(Render("text"), Render("<!--\nmulti\nline\n-->\ntext"));
        Assert.Equal(Render("text"), Render("text\n\n<!-- streaming"));
    }

    [Fact]
    public void KeepsOtherHtmlAndTextAfterAComment()
    {
        Assert.Equal(["with <tag> markers"], Render("with <tag> markers"));
        Assert.Equal(["trailing text"], Render("<!-- c --> trailing text"));
    }
}
