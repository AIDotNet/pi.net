namespace Pi.Tui.Components;

/// <summary>Text component - displays multi-line text with word wrapping (port of <c>components/text.ts</c>).</summary>
/// <remarks>
/// Not sealed: <c>Loader</c> derives from it and overrides <see cref="Render"/> / <see cref="Invalidate"/>
/// exactly as the TS original does. Note that <see cref="SetText"/> and <see cref="SetCustomBgFn"/> clear the
/// cache directly rather than through the virtual <see cref="Invalidate"/> — mirroring TS, where those methods
/// assign the cache fields themselves. Routing them through the virtual would recurse forever in <c>Loader</c>
/// (its override re-enters <see cref="SetText"/>).
/// </remarks>
public class Text : IComponent
{
    private string _text;
    private int _paddingX;
    private readonly int _paddingY;
    private Func<string, string>? _customBgFn;

    private string? _cachedText;
    private int? _cachedWidth;
    private string[]? _cachedLines;

    public Text(string text = "", int paddingX = 1, int paddingY = 1, Func<string, string>? customBgFn = null)
    {
        _text = text;
        _paddingX = paddingX;
        _paddingY = paddingY;
        _customBgFn = customBgFn;
    }

    public void SetText(string text)
    {
        _text = text;
        ClearCache();
    }

    /// <summary>Current text. TS <c>private text</c>.</summary>
    /// <remarks>
    /// TS's <c>private</c> is erased at runtime, so a renderer (or a test) can read a component's
    /// state directly. C#'s is not, and the tool renderers of 4f-1 build text components whose
    /// content the differential corpus has to observe, so the state is exposed read-only here.
    /// The name cannot be <c>Text</c> (CS0542: a member may not share its enclosing type's name).
    /// </remarks>
    public string CurrentText => _text;

    /// <summary>Current left/right padding. TS <c>private paddingX</c>.</summary>
    public int PaddingX => _paddingX;

    /// <summary>Current top/bottom padding. TS <c>private paddingY</c>.</summary>
    public int PaddingY => _paddingY;

    /// <summary>Whether a custom background function is installed. TS <c>private customBgFn</c>.</summary>
    public bool HasCustomBgFn => _customBgFn is not null;

    /// <summary>
    /// The installed custom background function. TS <c>private customBgFn</c>; exposed so the tool
    /// renderer corpus can probe which token the closure paints with.
    /// </summary>
    public Func<string, string>? CustomBgFn => _customBgFn;

    public void SetCustomBgFn(Func<string, string>? customBgFn)
    {
        _customBgFn = customBgFn;
        ClearCache();
    }

    public void SetPaddingX(int paddingX)
    {
        _paddingX = paddingX;
        Invalidate();
    }

    public virtual void Invalidate() => ClearCache();

    /// <summary>
    /// Optional keyboard handler, declared here so subclasses (<c>Loader</c> → <c>CancellableLoader</c>)
    /// can <c>override</c> it. Without this the interface map would point at
    /// <see cref="IComponent"/>'s empty default and an <see cref="IComponent"/>-typed call would never
    /// reach the subclass (C233).
    /// </summary>
    public virtual void HandleInput(string data)
    {
    }

    private void ClearCache()
    {
        _cachedText = null;
        _cachedWidth = null;
        _cachedLines = null;
    }

    public virtual string[] Render(int width)
    {
        if (_cachedLines is not null && _cachedText == _text && _cachedWidth == width)
        {
            return _cachedLines;
        }

        if (_text.Length == 0 || _text.Trim().Length == 0)
        {
            var empty = Array.Empty<string>();
            _cachedText = _text;
            _cachedWidth = width;
            _cachedLines = empty;
            return empty;
        }

        var normalizedText = _text.Replace("\t", "   ");

        // Reduce margins when necessary so content and padding fit within the available width.
        var paddingX = Math.Min(_paddingX, Math.Max(0, (int)Math.Floor((width - 1) / 2.0)));
        var contentWidth = Math.Max(1, width - paddingX * 2);
        var wrappedLines = TextLayout.WrapTextWithAnsi(normalizedText, contentWidth);

        var leftMargin = new string(' ', paddingX);
        var rightMargin = new string(' ', paddingX);
        var contentLines = new List<string>();

        foreach (var line in wrappedLines)
        {
            var lineWithMargins = leftMargin + line + rightMargin;
            if (_customBgFn is { } bg)
            {
                contentLines.Add(TextLayout.ApplyBackgroundToLine(lineWithMargins, width, bg));
            }
            else
            {
                var visibleLen = UnicodeWidth.VisibleWidth(lineWithMargins);
                var paddingNeeded = Math.Max(0, width - visibleLen);
                contentLines.Add(lineWithMargins + new string(' ', paddingNeeded));
            }
        }

        var emptyLine = new string(' ', width);
        var emptyLines = new List<string>();
        for (var i = 0; i < _paddingY; i++)
        {
            emptyLines.Add(_customBgFn is { } bg ? TextLayout.ApplyBackgroundToLine(emptyLine, width, bg) : emptyLine);
        }

        var result = new List<string>();
        result.AddRange(emptyLines);
        result.AddRange(contentLines);
        result.AddRange(emptyLines);

        _cachedText = _text;
        _cachedWidth = width;
        _cachedLines = result.ToArray();

        return result.Count > 0 ? _cachedLines : new[] { "" };
    }
}
