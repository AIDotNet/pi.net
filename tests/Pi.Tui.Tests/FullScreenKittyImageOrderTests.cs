using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// 上游 1.5.1 #11835：把 Kitty 图像存在「所覆盖单元格」的终端不止 WezTerm（还有 Orca 的
/// xterm.js image addon），所以全屏帧里图像 placement 必须在所有 clear / 文本写入之后绘制，
/// 且不再以 WEZTERM_PANE / TERM_PROGRAM 门控。此测试在**未设置 WezTerm 环境变量**时断言
/// transmit 晚于行清除与文本行——旧实现（仅 WezTerm 才 drawLast）会把图像按行交错、提前写出。
/// </summary>
public class FullScreenKittyImageOrderTests
{
    private sealed class FixedLines(params string[] lines) : IComponent
    {
        public string[] Render(int width) => lines;
        public void Invalidate() { }
    }

    [Fact]
    public void DrawsKittyImagesAfterTheRowsTheyCover_OnAnyKittyTerminal()
    {
        // 明确清掉 WezTerm 信号，证明门控已取消（旧实现下此用例会失败）。
        using var env = new EnvScope(("WEZTERM_PANE", null), ("TERM_PROGRAM", null));
        using var scope = new CapabilitiesScope(new TerminalCapabilities(ImageProtocol.Kitty, true, true));

        const long imageId = 11835;
        var imageLine = TerminalImage.EncodeKitty("AAAA", new KittyEncodeOptions
        {
            Columns = 2,
            Rows = 3,
            ImageId = imageId,
            MoveCursor = false,
        });
        TerminalImage.RegisterKittyImageMetadata(
            new KittyImageMetadata(imageId, 2, 3, 100, 100));

        var terminal = new StringTerminal(20, 5);
        var tui = new TuiAltScreen(terminal);
        tui.SetLayoutRoot(new FixedLines("before", imageLine, "", "", "after"));
        tui.Start();
        tui.RenderNow();

        var writes = terminal.Output;
        var transmitIndex = writes.IndexOf("\x1b_Ga=T", System.StringComparison.Ordinal);
        var lastRowClear = writes.LastIndexOf("\x1b[2K", System.StringComparison.Ordinal);
        var afterIndex = writes.IndexOf("after", System.StringComparison.Ordinal);

        Assert.True(transmitIndex >= 0, "expected a Kitty image transmission in the rendered frame");
        Assert.True(afterIndex >= 0, "expected the trailing text row to be written");
        // 图像在所有行清除之后、且在文本行之后才绘制。
        Assert.True(transmitIndex > lastRowClear,
            $"Kitty transmit ({transmitIndex}) must come after row clears ({lastRowClear})");
        Assert.True(transmitIndex > afterIndex,
            $"Kitty transmit ({transmitIndex}) must come after the 'after' text row ({afterIndex})");

        tui.Stop(new TuiStopOptions { PreserveScreen = true });
    }
}
