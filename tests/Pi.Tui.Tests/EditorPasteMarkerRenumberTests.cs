using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>上游 1.5.1 #11923：退格删除短粘贴标记后，后面的短标记要正确重编号，不能出现 "undefined"。</summary>
public class EditorPasteMarkerRenumberTests
{
    private static Editor Create()
    {
        var tui = new FakeTui();
        var editor = new Editor(tui, new EditorTheme
        {
            BorderColor = static text => text,
            SelectList = SelectListTheme.Plain,
        });
        editor.Focused = true;
        return editor;
    }

    [Fact]
    public void RenumberingShortPasteMarkersDoesNotAppendUndefined()
    {
        var editor = Create();
        string? submitted = null;
        editor.OnSubmit = text => submitted = text;

        // 两段大粘贴 → 注册表 #1 / #2（标记带 size 后缀）。
        var pasteA = string.Concat(Enumerable.Repeat("alpha\n", 12)).TrimEnd('\n');
        var pasteB = string.Concat(Enumerable.Repeat("beta\n", 12)).TrimEnd('\n');
        editor.HandleInput($"\x1b[200~{pasteA}\x1b[201~");
        editor.HandleInput($"\x1b[200~{pasteB}\x1b[201~");

        // Ctrl+U 清空这一行（注册表仍在），再逐字符输入两个不带 size 后缀的短标记。
        editor.HandleInput("\x15");
        foreach (var ch in "[paste #1][paste #2]")
        {
            editor.HandleInput(ch.ToString());
        }
        // 光标左移越过 #2 标记，退格删掉 #1 标记 → 触发 #2 重编号为 #1。
        editor.HandleInput("\x1b[D");
        editor.HandleInput("\x7f");

        var text = editor.GetText();
        Assert.DoesNotContain("undefined", text);
        Assert.Equal("[paste #1]", text);
        editor.HandleInput("\r");
        Assert.Equal(pasteB, submitted);
    }
}
