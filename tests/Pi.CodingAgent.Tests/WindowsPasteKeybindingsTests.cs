using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 1.5.1 #11765：Windows/WSL 上 Ctrl+V 也要是默认粘贴键（此前只有 alt+v），
/// 否则从剪贴板粘贴图片/文本不生效。断言 app.clipboard.pasteImage 的默认键在 Windows
/// 判定下同时含 alt+v 与 ctrl+v，非 Windows 下仅 ctrl+v。
/// </summary>
public class WindowsPasteKeybindingsTests
{
    [Fact]
    public void PasteImageIncludesCtrlVOnWindows()
    {
        Assert.True(AppKeybindings.UseWindowsKeybindings("win32"));
        Assert.True(AppKeybindings.UseWindowsKeybindings("linux", new Dictionary<string, string?>
        {
            ["WSL_DISTRO_NAME"] = "Ubuntu",
        }));

        // 当前构建表在本机（Windows）平台生成，pasteImage 默认键应同时含 alt+v 与 ctrl+v。
        var defaults = AppKeybindings.Definitions[AppKeybindings.AppClipboardPasteImage].DefaultKeys.Values;
        if (AppKeybindings.UseWindowsKeybindings())
        {
            Assert.Contains("alt+v", defaults);
            Assert.Contains("ctrl+v", defaults);
        }
        else
        {
            Assert.Contains("ctrl+v", defaults);
        }
    }
}
