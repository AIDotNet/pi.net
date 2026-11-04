using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// 换行边界上的带样式词（`2ce9d527d`，上游 `wrap-ansi.test.ts` 的六个定向用例）：收尾码要留在被折断的那一行、
/// 它后面的空格要被丢掉、删除线要在行尾关掉并在下一行重开。这些断言全是纯字符串，不依赖主题。
/// </summary>
public class WrapAnsiBoundaryTests
{
    private static string[] Wrap(string text, int width) => [.. TextLayout.WrapTextWithAnsi(text, width)];

    [Fact]
    public void KeepsTheClosingCodeOnTheWrappedLineAndDropsTheFollowingSpace()
    {
        Assert.Equal(
            ["aaaa \x1b[1mbold\x1b[22m", "next word"],
            Wrap("aaaa \x1b[1mbold\x1b[22m next word", 9));
    }

    [Fact]
    public void DoesNotReopenAStyleClosedByTheDroppedSpace()
    {
        Assert.Equal(
            ["aaaa \x1b[31mred\x1b[39m", "next"],
            Wrap("aaaa \x1b[31mred\x1b[39m  next", 8));
    }

    [Fact]
    public void DoesNotExceedWidthForStyledWhitespaceWiderThanTheLine()
    {
        string[] inputs =
        [
            $"\x1b[41m{new string(' ', 20)}\x1b[0m",
            $"ab\x1b[41m{new string(' ', 20)}\x1b[49mcd",
            $"\x1b[33m{new string(' ', 20)}x\x1b[39m",
        ];
        foreach (var input in inputs)
        {
            foreach (var line in Wrap(input, 10))
            {
                Assert.True(UnicodeWidth.VisibleWidth(line) <= 10, $"{line.Replace("\x1b", "<ESC>")}");
            }
        }
    }

    [Fact]
    public void TrimsTrailingSpacesBeforeTheLineEndResetWhenALongWordFollows()
    {
        Assert.Equal(
            ["aaa\x1b[9m\x1b[29m", "\x1b[9mabcdefghi\x1b[29m", "\x1b[9mj\x1b[29m"],
            Wrap("aaa\x1b[9m    abcdefghij\x1b[29m", 9));
    }

    [Fact]
    public void ResetsStrikethroughAtLineEndAndRestoresItOnTheNextLine()
    {
        Assert.Equal(
            ["aaaa \x1b[9mstrike\x1b[29m", "\x1b[9mme\x1b[29m next"],
            Wrap("aaaa \x1b[9mstrike me\x1b[29m next", 12));
    }

    [Fact]
    public void PreservesBackgroundWhenResettingStrikethroughAtLineEnd()
    {
        Assert.Equal(
            ["\x1b[41;9mstrike\x1b[29m", "\x1b[9;41mme\x1b[0m"],
            Wrap("\x1b[41;9mstrike me\x1b[0m", 6));
    }
}
