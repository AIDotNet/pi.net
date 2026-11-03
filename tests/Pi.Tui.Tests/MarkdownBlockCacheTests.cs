using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Markdown 的顶层块复用（`0f21a3319`）：流式时只有最后一块在变，前面的块直接取上次样式化并折行后的行。
/// 复用必须是**纯缓存**——同样的输入永远给同样的输出，所以正确性由 `MarkdownCorpusTests` 兜底，
/// 这里验证「真的复用了」、「该失效时确实失效」以及「引用式链接那种跨块解析的文本不复用」。
/// </summary>
public class MarkdownBlockCacheTests
{
    [Fact]
    public void ReusesEarlierBlocksWhenOnlyTheLastBlockChanges()
    {
        var boldCalls = 0;
        var markdown = new Markdown("**one**\n\nplain", 0, 0, Theme(() => boldCalls++));

        var first = markdown.Render(40);
        Assert.Equal(1, boldCalls);
        Assert.Contains("one", string.Concat(first));

        // 只改最后一块：第一块的加粗不再被调用（否则会变成 2）。
        markdown.SetText("**one**\n\nplain, longer");
        var second = markdown.Render(40);
        Assert.Equal(1, boldCalls);
        Assert.Contains("plain, longer", string.Concat(second));
    }

    [Fact]
    public void InvalidateDropsTheBlockCache()
    {
        var boldCalls = 0;
        var markdown = new Markdown("**one**\n\nplain", 0, 0, Theme(() => boldCalls++));
        markdown.Render(40);
        Assert.Equal(1, boldCalls);

        // 主题可能变了：块整体作废，下次渲染重新样式化。
        markdown.Invalidate();
        markdown.Render(40);
        Assert.Equal(2, boldCalls);
    }

    [Fact]
    public void ReferenceLinkDefinitionsDisableBlockReuse()
    {
        // 定义（`[x]: url`）会改变别的块的解析结果，所以这种文本整篇不复用块：
        // 改了定义之后第一块必须重新渲染（否则加粗不会再被调用）。
        var boldCalls = 0;
        var markdown = new Markdown("**a**\n\n[x]: http://one", 0, 0, Theme(() => boldCalls++));
        markdown.Render(40);
        Assert.Equal(1, boldCalls);

        markdown.SetText("**a**\n\n[x]: http://two");
        markdown.Render(40);
        Assert.Equal(2, boldCalls);
    }

    /// <summary>加粗会记一次调用的主题；其余样式原样返回。</summary>
    private static MarkdownTheme Theme(Action? onBold = null) => new()
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
        Bold = text =>
        {
            onBold?.Invoke();
            return $"\x1b[1m{text}\x1b[22m";
        },
        Italic = text => text,
        Strikethrough = text => text,
        Underline = text => text,
    };
}
