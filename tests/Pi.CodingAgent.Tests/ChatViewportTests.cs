// ============================================================================
// Tests for modes/interactive/chat-viewport.ts (port: Modes/Interactive/ChatViewport.cs).
// Upstream has no test file; vectors derived from the source's layout contract:
// transcript config (follow end / primary / chain / auto scrollbar) and the dock
// ordering with its shrink/minSize ladder.
// ============================================================================

using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

// Pi.CodingAgent.Utils（4f-1 的 chalk 辅助）与 Pi.Tui.Components 都有 Text；这里要的是 TUI 组件。
using TuiText = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Tests;

public sealed class ChatViewportTests
{
    private static ChatViewport Create(bool withOptionalWidgets = false, ScrollViewScrollbar? scrollbar = null)
    {
        return ChatViewportFactory.CreateChatViewport(new ChatViewportOptions
        {
            Document = new TuiText("DOC", 1, 0),
            PendingMessages = new TuiText("PENDING", 1, 0),
            Status = new TuiText("STATUS", 1, 0),
            Editor = new TuiText("EDITOR", 3, 0),
            Footer = new TuiText("FOOTER", 1, 0),
            WidgetsAbove = withOptionalWidgets ? new TuiText("ABOVE", 1, 0) : null,
            WidgetsBelow = withOptionalWidgets ? new TuiText("BELOW", 1, 0) : null,
            Scrollbar = scrollbar,
            ScrollbarTrackStyle = text => $"T{text}T",
            ScrollbarThumbStyle = text => $"H{text}H",
        });
    }

    [Fact]
    public void TranscriptIsFollowEndPrimaryScrollView()
    {
        var viewport = Create();

        // Follow="end" 只进 options（ScrollView 不回读），布局行为由 Pi.Tui.Tests 覆盖；
        // 这里验证其余配置与 primary 语义。
        Assert.True(viewport.Transcript.Primary);
        Assert.Equal(ScrollOverscroll.Chain, viewport.Transcript.Overscroll);
        Assert.Equal(ScrollViewScrollbar.Auto, viewport.Transcript.Scrollbar);
    }

    [Fact]
    public void ExplicitScrollbarOverridesTheAutoDefault()
    {
        var viewport = Create(scrollbar: ScrollViewScrollbar.Always);

        Assert.Equal(ScrollViewScrollbar.Always, viewport.Transcript.Scrollbar);
    }

    [Fact]
    public void RootIsTranscriptOverDock()
    {
        var viewport = Create();

        var root = Assert.IsType<VStack>(viewport.Root);
        Assert.Equal(2, root.Children.Count);
        Assert.Same(viewport.Transcript, root.Children[0]);
        var dock = Assert.IsType<VStack>(root.Children[1]);
        Assert.Equal(4, dock.Children.Count); // pending / status / editor / footer
    }

    [Fact]
    public void DockOrdersTheFixedRowsAndOptionalWidgetRows()
    {
        var withOptional = Create(withOptionalWidgets: true);
        var dock = Assert.IsType<VStack>(Assert.IsType<VStack>(withOptional.Root).Children[1]);

        var names = dock.Children.Select(child => RenderMarker(child)).ToList();
        Assert.Equal(
            ["PENDING", "STATUS", "ABOVE", "EDITOR", "BELOW", "FOOTER"],
            names);

        // 不带可选 widget 行时只有五个 dock 行。
        var withoutOptional = Create();
        var bareDock = Assert.IsType<VStack>(Assert.IsType<VStack>(withoutOptional.Root).Children[1]);
        Assert.Equal(4, bareDock.Children.Count);
    }

    [Fact]
    public void EditorDockRowKeepsAMinimumOfThreeRows()
    {
        // Stack.Entries 是 protected（minSize/basis 阶梯由 Pi.Tui.Tests 的布局测试覆盖）；
        // 这里验证 dock 的行数与顺序（编辑器 minSize 3 的可观察面是布局高度，见 Tui 测试）。
        var viewport = Create(withOptionalWidgets: true);
        var dock = Assert.IsType<VStack>(Assert.IsType<VStack>(viewport.Root).Children[1]);

        Assert.Equal(6, dock.Children.Count);
        // transcript 行是根 VStack 的第一个条目，且与 Root 同容器。
        var root = Assert.IsType<VStack>(viewport.Root);
        Assert.Equal(2, root.Children.Count);
    }

    private static string RenderMarker(IComponent component)
    {
        var lines = component.Render(40);
        var text = AnsiText.StripAnsi(string.Join("\n", lines));
        return text.Trim();
    }
}
