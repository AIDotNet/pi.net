using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>未发布批的 TUI 修复：图像行硬换行、overlay 遮图、选择器光标键、绝对路径 Tab+Enter。</summary>
public class UnreleasedTuiFixTests
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

    // ------------------------------------------------------------------
    // #12017: hard breaks after inline images
    // ------------------------------------------------------------------

    [Fact]
    public void ImageLineHardBreaksStaySeparateRows()
    {
        using var capabilities = new CapabilitiesScope(new TerminalCapabilities(ImageProtocol.Kitty, true, true));
        var image = "\x1b_Ga=T,f=100,q=2,c=20,r=3,C=1;AAAA\x1b\\";
        var markdown = new Markdown($"`{image}`  \n` `  \n` `\n\n- one", 0, 0, PlainTheme);

        var lines = markdown.Render(80);

        Assert.DoesNotContain(lines, line => line.Contains('\n'));
        Assert.Contains(image, lines[0]);
        Assert.Equal(new[] { "", "", "", "- one" },
            lines.Skip(1).Select(line => Ansi.StripTerminalSequences(line).Trim()).ToArray());
    }

    // ------------------------------------------------------------------
    // #12109: overlay hides the image beneath it
    // ------------------------------------------------------------------

    [Fact]
    public void ImageExtentCoversKittyColumnsAndRows()
    {
        var line = "\x1b_Ga=T,f=100,q=2,c=20,r=3,i=42;AAAA\x1b\\";
        var extent = TerminalImage.GetImageLineExtent(line);

        Assert.NotNull(extent);
        Assert.Equal(0, extent.Value.FirstRow);
        Assert.Equal(2, extent.Value.LastRow);
        Assert.Equal(0, extent.Value.StartCol);
        Assert.Equal(20, extent.Value.EndCol);
    }

    [Fact]
    public void ImageExtentUsesFullWidthWhenUnknown()
    {
        var extent = TerminalImage.GetImageLineExtent("\x1b_Ga=T,f=100,q=2;AAAA\x1b\\");
        Assert.NotNull(extent);
        Assert.Equal(int.MaxValue, extent.Value.EndCol);
        Assert.Equal(1, extent.Value.LastRow - extent.Value.FirstRow + 1);
    }

    [Fact]
    public void ImageExtentHandlesIterm2CursorUp()
    {
        var line = "abc\x1b[3A\x1b]1337;File=inline=1;width=10;height=5:AAAA\x07";
        var extent = TerminalImage.GetImageLineExtent(line);

        Assert.NotNull(extent);
        Assert.Equal(-3, extent.Value.FirstRow);
        Assert.Equal(0, extent.Value.LastRow);
        Assert.Equal(3, extent.Value.StartCol);
        Assert.Equal(13, extent.Value.EndCol);
    }

    [Fact]
    public void OverlayHidesOnlyImagesItCovers()
    {
        var image = "\x1b_Ga=T,f=100,q=2,c=20,r=3,i=42;AAAA\x1b\\";
        var covered = new List<string> { "before", image, "", "", "after" };
        Tui.HideImagesUnderOverlay(covered, new OverlayBounds
        {
            Row = 1, Col = 0, Width = 10, Height = 2,
        }, 0);
        Assert.DoesNotContain("\x1b_G", covered[1]);

        var beside = new List<string> { "before", image, "", "", "after" };
        Tui.HideImagesUnderOverlay(beside, new OverlayBounds
        {
            Row = 1, Col = 30, Width = 10, Height = 2,
        }, 0);
        Assert.Contains("\x1b_G", beside[1]);
    }

    [Fact]
    public void StripImageSequencesKeepsText()
    {
        Assert.Equal("abc", TerminalImage.StripImageSequences(
            "abc\x1b_Ga=T,f=100,q=2;AAAA\x1b\\"));
        Assert.Equal("abc", TerminalImage.StripImageSequences(
            "abc\x1b[3A\x1b]1337;File=inline=1:AAAA\x07"));
    }

    // ------------------------------------------------------------------
    // #12245: cursor keys in searchable settings keep the selection
    // ------------------------------------------------------------------

    [Fact]
    public void SettingsListSearchCursorKeysKeepSelection()
    {
        var items = new[]
        {
            new SettingItem { Id = "a", Label = "alpha", CurrentValue = "1" },
            new SettingItem { Id = "b", Label = "beta", CurrentValue = "2" },
            new SettingItem { Id = "c", Label = "gamma", CurrentValue = "3" },
            new SettingItem { Id = "d", Label = "delta", CurrentValue = "4" },
        };
        var list = new SettingsList(
            items, 10,
            new SettingsListTheme
            {
                Label = static (text, _) => text,
                Value = static (text, _) => text,
                Description = static text => text,
                Cursor = "> ",
                Hint = static text => text,
            },
            static (_, _) => { }, static () => { },
            new SettingsListOptions { EnableSearch = true });

        list.HandleInput("a");
        list.HandleInput("\x1b[B");
        list.HandleInput("\x1b[B");
        Assert.Equal(2, list.SelectedIndexForTests);

        // 左键/Home 只移动搜索框光标，不改变查询，因此选中项保持不动。
        list.HandleInput("\x1b[D");
        Assert.Equal(2, list.SelectedIndexForTests);
        list.HandleInput("\x1b[H");
        Assert.Equal(2, list.SelectedIndexForTests);

        // 真正改变查询时才重新过滤并回到第一项。
        list.HandleInput("m");
        Assert.Equal(0, list.SelectedIndexForTests);
    }
}
