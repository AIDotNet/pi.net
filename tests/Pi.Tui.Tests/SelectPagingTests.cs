using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// 选择列表的翻页键（`3bae85274`，上游 `select-list.test.ts` / `settings-list.test.ts` 的两例）：
/// `tui.select.pageUp/pageDown` 按一页（可见行数）移动并夹在列表边界内；列表为空或页面没变时什么也不做。
/// </summary>
public class SelectPagingTests
{
    [Fact]
    public void SelectListMovesByOneVisiblePage()
    {
        var items = Enumerable.Range(0, 12)
            .Select(index => new SelectItem { Value = $"item-{index}", Label = $"Item {index}" })
            .ToList();
        var list = new SelectList(items, 5, SelectListTheme.Plain);
        var selections = new List<string>();
        list.OnSelectionChange = item => selections.Add(item.Value);

        foreach (var key in new[] { "\x1b[6~", "\x1b[6~", "\x1b[6~", "\x1b[6~", "\x1b[5~" }) list.HandleInput(key);

        // 0 → 5 → 10 → 11（夹到末尾）→ 11 再翻不动 → 6
        Assert.Equal(["item-5", "item-10", "item-11", "item-6"], selections);

        // 过滤后没有条目：翻页键不选中任何东西。
        list.SetFilter("no match");
        list.HandleInput("\x1b[6~");
        Assert.Equal(["item-5", "item-10", "item-11", "item-6"], selections);
    }

    [Fact]
    public void SettingsListMovesByOneVisiblePage()
    {
        var items = Enumerable.Range(0, 12)
            .Select(index => new SettingItem
            {
                Id = $"setting-{index}",
                Label = $"Setting {index}",
                CurrentValue = index.ToString(),
            })
            .ToList();
        var list = new SettingsList(items, 5, Theme(), (_, _) => { }, () => { });

        list.HandleInput("\x1b[6~");
        Assert.Contains("> Setting 5", string.Join("\n", list.Render(80)));

        list.HandleInput("\x1b[6~");
        list.HandleInput("\x1b[6~");
        Assert.Contains("> Setting 11", string.Join("\n", list.Render(80)));

        list.HandleInput("\x1b[5~");
        Assert.Contains("> Setting 6", string.Join("\n", list.Render(80)));
    }

    private static SettingsListTheme Theme() => new()
    {
        Label = (text, _) => text,
        Value = (text, _) => text,
        Description = text => text,
        Cursor = "> ",
        Hint = text => text,
    };

    /// <summary>
    /// 全屏下聚焦的列表自己吃视口键（`3bae85274`，上游 `tui-alt-screen.test.ts` 第一例）：PageDown 移动列表选择，
    /// 转录的 viewportTop 不变；Ctrl+Home 这类视口跳转键也交给列表而不是滚到顶。
    /// </summary>
    [Fact]
    public void FullscreenDefersViewportKeysToAFocusedListInsteadOfScrollingTheTranscript()
    {
        var terminal = new StringTerminal(20, 6);
        var tui = new TuiAltScreen(terminal);
        var items = Enumerable.Range(0, 12)
            .Select(index => new SelectItem { Value = $"item-{index}", Label = $"item-{index}" })
            .ToList();
        var list = new SelectList(items, 3, SelectListTheme.Plain);
        tui.AddChild(new Text(string.Join("\n", Enumerable.Range(1, 12).Select(index => $"line {index}")), 0, 0));
        tui.AddChild(list);
        tui.SetFocus(list);
        tui.Start();
        try
        {
            tui.RenderNow();
            var topBefore = tui.ViewportTop;

            terminal.SendInput("\x1b[6~"); // PageDown
            terminal.SendInput("\x1b[1;5H"); // Ctrl+Home
            tui.RenderNow();

            Assert.Equal("item-3", list.GetSelectedItem()?.Value);
            Assert.Equal(topBefore, tui.ViewportTop);
        }
        finally
        {
            tui.Stop();
        }
    }
}
