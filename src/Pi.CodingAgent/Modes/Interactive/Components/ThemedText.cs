// ============================================================================
// Text whose content applies theme colors.
// Port of modes/interactive/components/themed-text.ts
// ============================================================================

using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Text whose content applies theme colors. A plain <see cref="Text"/> keeps the colors its string was
/// built with, so a theme change (or the system theme receiving the terminal's colors) would leave it
/// stale. This rebuilds the string from <paramref name="build"/> after every invalidation, which the UI
/// performs on theme changes.
/// </summary>
/// <remarks>
/// <paramref name="build"/> must return the same content each time, apart from colors. Snapshot
/// changing data before creating the component, or call <see cref="Invalidate"/> after changing state
/// that <paramref name="build"/> reads.
/// </remarks>
public class ThemedText : Text
{
    private readonly Func<string> _build;
    private bool _stale = true;

    public ThemedText(Func<string> build, int paddingX = 1, int paddingY = 1)
        : base("", paddingX, paddingY)
    {
        _build = build;
    }

    public override void Invalidate()
    {
        base.Invalidate();
        _stale = true;
    }

    public override string[] Render(int width)
    {
        if (_stale)
        {
            _stale = false;
            SetText(_build());
        }

        return base.Render(width);
    }
}
