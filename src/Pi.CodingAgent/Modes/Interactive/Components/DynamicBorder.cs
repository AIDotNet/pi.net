// ============================================================================
// Dynamic border component that adjusts to the viewport width.
// Port of modes/interactive/components/dynamic-border.ts
// ============================================================================

using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// A single rule line spanning the viewport width, colored with the theme's <c>border</c> token by
/// default. Extensions may pass an explicit color function, because a theme loaded through a separate
/// module cache would otherwise be unreachable (matching the TS note on <c>DynamicBorder</c>).
/// </summary>
public sealed class DynamicBorder : IComponent
{
    private readonly Func<string, string> _color;

    public DynamicBorder(Func<string, string>? color = null) =>
        _color = color ?? (str => ThemeRuntime.Current.Fg("border", str));

    public void Invalidate()
    {
        // No cached state to invalidate currently.
    }

    public string[] Render(int width) => [_color(new string('─', Math.Max(1, width)))];
}
