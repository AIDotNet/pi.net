// ============================================================================
// The `/login` menu with the animated "Sign in with Radius" option.
// Port of modes/interactive/components/radius-login-selector.ts
// ============================================================================

using System.Diagnostics;
using System.Text;
using Pi.CodingAgent.Core;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// TS <c>RadiusOption = { label: string; text: string }</c> — an unexported structural type there,
/// which has to become a named public type here.
/// </summary>
public sealed record RadiusOption(string Label, string Text);

/// <summary>
/// Port of the TS <c>RadiusLoginMenuComponent</c>: swaps the line
/// <see cref="ExtensionSelectorComponent"/> draws for the selected Radius option with the animated
/// one. When the selector's row style changes or the label wraps, the line no longer matches and the
/// option renders normally.
/// </summary>
public class RadiusLoginMenuComponent : ExtensionSelectorComponent
{
    /// <summary>TS <c>RADIUS_COLORS</c>: the four colors of the Radius logo, in stream order.</summary>
    private static readonly Color[] RadiusColors =
        new[] { "#4d9abf", "#83ccd2", "#f1be57", "#f09082" }.Select(Colors.ParseColor).ToArray();

    /// <summary>TS <c>CHARS_PER_COLOR</c>.</summary>
    private const int CharsPerColor = 4;

    /// <summary>TS <c>CHARS_PER_SECOND</c>.</summary>
    private const int CharsPerSecond = 10;

    /// <summary>TS <c>ANIMATION_FRAME_MS</c>.</summary>
    private const int AnimationFrameMs = 50;

    private readonly ITui _tui;
    private readonly RadiusOption _radiusOption;
    private readonly long _animationStart = Stopwatch.GetTimestamp();
    private Timer? _animationTimer;
    private volatile bool _animating;

    public RadiusLoginMenuComponent(
        ITui tui,
        string title,
        IReadOnlyList<string> options,
        RadiusOption radiusOption,
        Action<string> onSelect,
        Action onCancel)
        : base(title, options, onSelect, onCancel)
    {
        _tui = tui;
        _radiusOption = radiusOption;

        // TS uses `setInterval(...).unref?.()`; a System.Threading.Timer is a thread-pool timer that
        // never keeps the process alive, so the unref call has no counterpart.
        _animationTimer = new Timer(
            _ =>
            {
                if (_animating) _tui.RequestRender();
            },
            null,
            AnimationFrameMs,
            AnimationFrameMs);
    }

    /// <summary>
    /// Color <paramref name="text"/> with the Radius logo colors flowing left to right;
    /// <paramref name="elapsedMs"/> is the animation time. Port of the module-private
    /// <c>radiusShimmer</c> (exposed <c>internal</c> so tests can pin the exact byte stream).
    /// </summary>
    internal static string RadiusShimmer(string text, double elapsedMs)
    {
        var mode = ThemeRuntime.Current.GetColorMode();
        var cycle = RadiusColors.Length * CharsPerColor;
        var offset = elapsedMs / 1000 * CharsPerSecond;
        var result = new StringBuilder();
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var position = ((index - offset) % cycle + cycle) % cycle;
            var band = (int)Math.Floor(position / CharsPerColor);
            var t = position / CharsPerColor - band;
            // Smoothstep keeps each band recognizable while still blending into the next one.
            var amount = t * t * (3 - 2 * t);
            var from = RadiusColors[band];
            var to = RadiusColors[(band + 1) % RadiusColors.Length];
            result.Append(Colors.ForegroundAnsi(Colors.MixColors(from, to, amount, ColorMixSpace.Srgb), mode));
            result.Append(rune.ToString());
            index++;
        }

        result.Append("\x1b[39m");
        return result.ToString();
    }

    public override string[] Render(int width)
    {
        var lines = base.Render(width);
        var theme = ThemeRuntime.Current;
        var label = _radiusOption.Label;
        var text = _radiusOption.Text;

        var selectedLine = new Text(theme.Fg("accent", "→ ") + theme.Fg("accent", label), 1, 0).Render(width);
        var index = selectedLine.Length == 0 ? -1 : Array.IndexOf(lines, selectedLine[0]);
        _animating = index >= 0;
        if (_animating)
        {
            var shimmer = RadiusShimmer(text, Stopwatch.GetElapsedTime(_animationStart).TotalMilliseconds);
            var animatedLine = theme.Fg("accent", "→ ") + shimmer + label[text.Length..];
            var rendered = new Text(animatedLine, 1, 0).Render(width);
            lines[index] = rendered.Length > 0 ? rendered[0] : "";
        }

        return lines;
    }

    /// <summary>
    /// TS wraps the two callbacks in the derived constructor so it can stop the animation before
    /// handing the event on. C# cannot reference <c>this</c> in a <c>base(...)</c> argument list
    /// (CS0027), so the base's <c>HandleInput</c> is <c>virtual</c> and the derived class stops the
    /// animation on the confirm/cancel keys *before* delegating — same ordering as TS.
    /// </summary>
    public override void HandleInput(string data)
    {
        var keybindings = GlobalKeybindings.Get();
        if (keybindings.Matches(data, TuiKeybindingIds.SelectConfirm)
            || data == "\n"
            || keybindings.Matches(data, TuiKeybindingIds.SelectCancel))
        {
            StopAnimation();
        }

        base.HandleInput(data);
    }

    private void StopAnimation()
    {
        var timer = Interlocked.Exchange(ref _animationTimer, null);
        timer?.Dispose();
        _animating = false;
    }

    public override void Dispose()
    {
        StopAnimation();
        base.Dispose();
    }
}

/// <summary>Port of <c>createLoginMenuSelector</c>.</summary>
public static class RadiusLoginSelector
{
    /// <summary>
    /// Top-level <c>/login</c> selector whose Radius option shimmers in the Radius logo colors while
    /// it is selected.
    /// </summary>
    public static ExtensionSelectorComponent CreateLoginMenuSelector(
        ITui tui,
        string title,
        IReadOnlyList<string> options,
        RadiusOption radiusOption,
        Action<string> onSelect,
        Action onCancel) =>
        new RadiusLoginMenuComponent(tui, title, options, radiusOption, onSelect, onCancel);
}
