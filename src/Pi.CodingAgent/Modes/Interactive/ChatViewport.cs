// ============================================================================
// Port of modes/interactive/chat-viewport.ts — the shared fullscreen transcript
// and fixed input-dock layout. Prerequisite for InteractiveMode.init (4f-4m-3):
// the client TUI (experimental/client-tui.ts) builds the same layout.
//
// TS values map onto the Pi.Tui stack primitives:
//   { shrink: 1, minSize: 0 }  → Shrink = 1, MinSize = 0
//   { shrink: 1, minSize: 3 }  → Shrink = 1, MinSize = 3   (editor dock row)
//   { basis: 0, grow: 1, shrink: 1, minSize: 1 } → transcript row
//   { basis: "auto", grow: 0, shrink: 1, minSize: 1 } → dock row (Basis = null)
// ============================================================================

using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>TS <c>ChatViewportOptions</c>.</summary>
public sealed class ChatViewportOptions
{
    public required IComponent Document { get; init; }

    public required IComponent PendingMessages { get; init; }

    public required IComponent Status { get; init; }

    public required IComponent Editor { get; init; }

    public required IComponent Footer { get; init; }

    /// <summary>TS <c>widgetsAbove?</c>.</summary>
    public IComponent? WidgetsAbove { get; init; }

    /// <summary>TS <c>widgetsBelow?</c>.</summary>
    public IComponent? WidgetsBelow { get; init; }

    /// <summary>TS <c>scrollbar?</c>; default <c>"auto"</c>.</summary>
    public ScrollViewScrollbar? Scrollbar { get; init; }

    /// <summary>TS <c>scrollbarTrackStyle?</c>.</summary>
    public Func<string, string>? ScrollbarTrackStyle { get; init; }

    /// <summary>TS <c>scrollbarThumbStyle?</c>.</summary>
    public Func<string, string>? ScrollbarThumbStyle { get; init; }
}

/// <summary>TS <c>ChatViewport</c>: the fullscreen layout root plus its transcript scroll view.</summary>
public sealed record ChatViewport(IComponent Root, ScrollView Transcript);

/// <summary>Shared fullscreen transcript and fixed input-dock layout.</summary>
public static class ChatViewportFactory
{
    public static ChatViewport CreateChatViewport(ChatViewportOptions options)
    {
        var transcript = new ScrollView(options.Document, new ScrollViewOptions
        {
            Follow = "end",
            Primary = true,
            Overscroll = ScrollOverscroll.Chain,
            Scrollbar = options.Scrollbar ?? ScrollViewScrollbar.Auto,
            ScrollbarTrackStyle = options.ScrollbarTrackStyle,
            ScrollbarThumbStyle = options.ScrollbarThumbStyle,
        });

        var dockChildren = new List<StackChild>
        {
            StackChild.Of(options.PendingMessages, new StackEntryOptions { Shrink = 1, MinSize = 0 }),
            StackChild.Of(options.Status, new StackEntryOptions { Shrink = 1, MinSize = 0 }),
        };
        if (options.WidgetsAbove is { } widgetsAbove)
        {
            dockChildren.Add(StackChild.Of(widgetsAbove, new StackEntryOptions { Shrink = 1, MinSize = 0 }));
        }

        dockChildren.Add(StackChild.Of(options.Editor, new StackEntryOptions { Shrink = 1, MinSize = 3 }));
        if (options.WidgetsBelow is { } widgetsBelow)
        {
            dockChildren.Add(StackChild.Of(widgetsBelow, new StackEntryOptions { Shrink = 1, MinSize = 0 }));
        }

        dockChildren.Add(StackChild.Of(options.Footer, new StackEntryOptions { Shrink = 1, MinSize = 0 }));
        var dock = new VStack(dockChildren);

        var root = new VStack(
        [
            StackChild.Of(transcript, new StackEntryOptions { Basis = 0, Grow = 1, Shrink = 1, MinSize = 1 }),
            StackChild.Of(dock, new StackEntryOptions { Grow = 0, Shrink = 1, MinSize = 1 }),
        ]);

        return new ChatViewport(root, transcript);
    }
}
