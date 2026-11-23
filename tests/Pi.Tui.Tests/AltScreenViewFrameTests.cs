using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// 上游 #11968 的 view-only frame：滚动只改视口位置时复用上一帧的组件输出；内容（RequestRender）
/// 或尺寸变化时必须重新渲染，渲染期间被失效的帧要用空缓存重跑。
/// </summary>
public class AltScreenViewFrameTests
{
    private sealed class CountingText(string text) : IComponent
    {
        public int RenderCount { get; private set; }

        public string[] Render(int width)
        {
            RenderCount++;
            return [text];
        }
    }

    /// <summary>首次渲染时把宿主标记为失效，并返回旧文本；之后的渲染返回新文本。</summary>
    private sealed class SelfInvalidatingOnFirstRender(Action invalidate) : IComponent
    {
        public int RenderCount { get; private set; }

        public string[] Render(int width)
        {
            RenderCount++;
            if (RenderCount == 1)
            {
                invalidate();
                return ["stale-body"];
            }
            return ["fresh-body"];
        }
    }

    private static (TuiAltScreen Tui, CountingText Head) NewTranscript(StringTerminal terminal)
    {
        var tui = new TuiAltScreen(terminal);
        var head = new CountingText("head line");
        tui.AddChild(head);
        for (var i = 1; i < 40; i++) tui.AddChild(new Text($"line {i}"));
        tui.Start();
        tui.RenderNow();
        return (tui, head);
    }

    [Fact]
    public void ScrollFramesReuseThePreviousComponentOutput()
    {
        var terminal = new StringTerminal(40, 10);
        var (tui, head) = NewTranscript(terminal);
        var afterFirstFrame = head.RenderCount;
        Assert.True(afterFirstFrame > 0);

        var start = tui.ViewportTop;
        tui.ScrollBy(-1); // 向上滚：会话跟随末尾时向下已经到底。
        tui.RenderNow();

        Assert.True(tui.ViewportTop < start, $"expected a scroll from {start}, got {tui.ViewportTop}");
        Assert.Equal(afterFirstFrame, head.RenderCount);
    }

    [Fact]
    public void WheelScrollFramesReuseThePreviousComponentOutput()
    {
        var terminal = new StringTerminal(40, 10);
        var (tui, head) = NewTranscript(terminal);
        var afterFirstFrame = head.RenderCount;

        // SGR wheel-up（button 64）落在视口里，滚的是主 transcript。
        var start = tui.ViewportTop;
        terminal.SendInput("\x1b[<64;5;5M");
        tui.RenderNow();

        Assert.True(tui.ViewportTop < start, $"expected a scroll from {start}, got {tui.ViewportTop}");
        Assert.Equal(afterFirstFrame, head.RenderCount);
    }

    [Fact]
    public void ContentFramesRenderComponentsAgain()
    {
        var terminal = new StringTerminal(40, 10);
        var (tui, head) = NewTranscript(terminal);
        var afterFirstFrame = head.RenderCount;

        tui.RequestRender();
        tui.RenderNow();

        Assert.True(head.RenderCount > afterFirstFrame);
    }

    [Fact]
    public void ResizingDropsTheCache()
    {
        var terminal = new StringTerminal(40, 10);
        var (tui, head) = NewTranscript(terminal);
        var afterFirstFrame = head.RenderCount;

        terminal.Resize(30, 8);
        tui.RenderNow();

        Assert.True(head.RenderCount > afterFirstFrame);
    }

    [Fact]
    public void AFrameInvalidatedWhileRenderingIsRedrawnFromAFreshCache()
    {
        // 视口要装得下全部内容，否则组件会被滚出屏幕、断言看不到文本。
        var terminal = new StringTerminal(40, 30);
        var tui = new TuiAltScreen(terminal);
        var component = new SelfInvalidatingOnFirstRender(() => tui.RequestRender());
        tui.AddChild(component);
        for (var i = 0; i < 5; i++) tui.AddChild(new Text($"line {i}"));
        tui.Start();

        tui.RenderNow();

        Assert.True(component.RenderCount >= 2);
        Assert.Contains("fresh-body", terminal.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("stale-body", terminal.Output, StringComparison.Ordinal);
    }
}
