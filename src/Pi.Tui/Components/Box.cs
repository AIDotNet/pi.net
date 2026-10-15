namespace Pi.Tui.Components;

/// <summary>Box component - a container that applies padding and background to all children.</summary>
/// <remarks>
/// Not sealed: the edit tool's call component is a <c>Box</c> with extra renderer fields, exactly as the
/// TS models it (<c>type EditCallRenderComponent = Box &amp; {...}</c>). <see cref="Pi.Tui.Components.Text"/>
/// is unsealed for the same reason — <c>Loader</c> derives from it.
/// </remarks>
public class Box : IComponent
{
    private sealed record RenderCache(string[] ChildLines, int Width, string? BgSample, string[] Lines);

    public List<IComponent> Children { get; } = new();

    private int _paddingX;
    private readonly int _paddingY;
    private Func<string, string>? _bgFn;
    private RenderCache? _cache;

    /// <summary>
    /// Child heights observed during the last render, reused for mouse hit-testing when the content
    /// width is unchanged (port of the TS <c>Box.mouseLayout</c>).
    /// </summary>
    private (int Width, List<(IComponent Component, int Height)> Children)? _mouseLayout;

    public Box(int paddingX = 1, int paddingY = 1, Func<string, string>? bgFn = null)
    {
        _paddingX = paddingX;
        _paddingY = paddingY;
        _bgFn = bgFn;
    }

    /// <summary>Current left/right padding. TS <c>private paddingX</c>.</summary>
    /// <remarks>
    /// TS's <c>private</c> is erased at runtime, so a renderer (or a test) can read a component's
    /// state directly. C#'s is not, and the tool renderers of 4f-1 build boxes whose shape the
    /// differential corpus has to observe, so the state is exposed read-only here.
    /// </remarks>
    public int PaddingX => _paddingX;

    /// <summary>Current top/bottom padding. TS <c>private paddingY</c>.</summary>
    public int PaddingY => _paddingY;

    /// <summary>The background function, if any. TS <c>private bgFn</c> (readable at runtime).</summary>
    public Func<string, string>? BgFn => _bgFn;

    public void AddChild(IComponent component)
    {
        Children.Add(component);
        InvalidateCache();
    }

    public void RemoveChild(IComponent component)
    {
        if (Children.Remove(component))
        {
            InvalidateCache();
        }
    }

    public void Clear()
    {
        Children.Clear();
        InvalidateCache();
    }

    public void SetBgFn(Func<string, string>? bgFn) => _bgFn = bgFn;

    /// <summary>Set the left/right padding and drop the render cache. TS <c>setPaddingX</c>.</summary>
    public void SetPaddingX(int paddingX)
    {
        _paddingX = paddingX;
        InvalidateCache();
    }

    private void InvalidateCache() => _cache = null;

    private bool MatchCache(int width, string[] childLines, string? bgSample)
    {
        var cache = _cache;
        return cache is not null
            && cache.Width == width
            && cache.BgSample == bgSample
            && cache.ChildLines.Length == childLines.Length
            && cache.ChildLines.SequenceEqual(childLines);
    }

    /// <summary>
    /// Drops the render cache and invalidates children. Virtual so subclasses can rebuild their
    /// content on invalidation, exactly as the TS <c>Box</c> subclasses override
    /// <c>Container.invalidate()</c> (TS <c>Box extends Container</c>).
    /// </summary>
    public virtual void Invalidate()
    {
        InvalidateCache();
        foreach (var child in Children)
        {
            child.Invalidate();
        }
    }

    /// <summary>
    /// Mouse hit-testing across children with padding-aware coordinate mapping (port of the TS
    /// <c>Box.handleMouse</c>). The event coordinates are local to the padded box: content space
    /// strips <c>paddingX</c>/<c>paddingY</c>, and the hit child receives coordinates local to it.
    /// </summary>
    public TuiMouseEventResult? HandleMouse(TuiMouseEvent @event)
    {
        var contentWidth = Math.Max(1, @event.Width - _paddingX * 2);
        var contentY = @event.Y - _paddingY;
        var contentX = @event.X - _paddingX;
        if (contentY < 0 || contentX < 0 || contentX >= contentWidth)
        {
            return null;
        }

        var mouseChildren = _mouseLayout is { } cached && cached.Width == contentWidth
            ? cached.Children
            : Children.Select(child => (Component: child, Height: child.Render(contentWidth).Length)).ToList();

        var childY = 0;
        foreach (var (child, childHeight) in mouseChildren)
        {
            if (contentY >= childY && contentY < childY + childHeight)
            {
                var childEvent = @event.Clone();
                childEvent.X = contentX;
                childEvent.Y = contentY - childY;
                childEvent.Width = contentWidth;
                childEvent.Height = childHeight;
                return MouseDispatch.Dispatch(child, childEvent);
            }

            childY += childHeight;
        }

        return null;
    }

    public string[] Render(int width)
    {
        if (Children.Count == 0)
        {
            return Array.Empty<string>();
        }

        var contentWidth = Math.Max(1, width - _paddingX * 2);
        var leftPad = new string(' ', _paddingX);

        var childLines = new List<string>();
        var mouseChildren = new List<(IComponent Component, int Height)>();
        foreach (var child in Children)
        {
            var lines = child.Render(contentWidth);
            mouseChildren.Add((child, lines.Length));
            foreach (var line in lines)
            {
                childLines.Add(leftPad + line);
            }
        }

        _mouseLayout = (contentWidth, mouseChildren);

        if (childLines.Count == 0)
        {
            return Array.Empty<string>();
        }

        var bgSample = _bgFn?.Invoke("test");

        if (MatchCache(width, childLines.ToArray(), bgSample))
        {
            return _cache!.Lines;
        }

        var result = new List<string>();
        for (var i = 0; i < _paddingY; i++)
        {
            result.Add(ApplyBg("", width));
        }
        foreach (var line in childLines)
        {
            result.Add(ApplyBg(line, width));
        }
        for (var i = 0; i < _paddingY; i++)
        {
            result.Add(ApplyBg("", width));
        }

        _cache = new RenderCache(childLines.ToArray(), width, bgSample, result.ToArray());
        return _cache.Lines;
    }

    private string ApplyBg(string line, int width)
    {
        var visLen = UnicodeWidth.VisibleWidth(line);
        var padNeeded = Math.Max(0, width - visLen);
        var padded = line + new string(' ', padNeeded);
        return _bgFn is { } bg ? TextLayout.ApplyBackgroundToLine(padded, width, bg) : padded;
    }
}
