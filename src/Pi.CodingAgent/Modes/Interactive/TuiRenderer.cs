// ============================================================================
// Composition root for the interactive presentations.
// Port of modes/interactive/tui-renderer.ts (81 lines).
// ============================================================================

using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>Options for <see cref="TuiRenderer.CreateInteractiveTui"/>. TS <c>InteractiveTuiOptions</c>.</summary>
public sealed record InteractiveTuiOptions
{
    public required TuiMode TuiMode { get; init; }

    public required bool ShowHardwareCursor { get; init; }

    /// <summary>
    /// Accepted for API compatibility: the port does not write a TUI debug log file, so nothing reads
    /// it (the TS base class logs there).
    /// </summary>
    public required string LogDirectory { get; init; }

    /// <summary>Defaults to a <see cref="ProcessTerminal"/>.</summary>
    public ITerminal? Terminal { get; init; }

    public Action? OnRightClickPaste { get; init; }

    public bool? FullscreenCopyOnSelect { get; init; }

    public WheelScrollLines? FullscreenWheelScrollLines { get; init; }
}

/// <summary>Port of <c>tui-renderer.ts</c>.</summary>
public static class TuiRenderer
{
    /// <summary>
    /// Build the renderer for the requested mode. TS overloads this on the literal
    /// <c>tuiMode</c>; C# returns the shared base, and callers that need the fullscreen-only
    /// surface cast to <see cref="TuiAltScreen"/>.
    /// </summary>
    public static TuiBase CreateInteractiveTui(InteractiveTuiOptions options)
    {
        var terminal = options.Terminal ?? new ProcessTerminal();
        if (options.TuiMode == TuiMode.Fullscreen)
        {
            return new TuiAltScreen(terminal, options.ShowHardwareCursor, new TuiAltScreenOptions
            {
                // The TS closures read the module-level `theme` binding, which is replaced when the
                // theme switches, so each callback resolves the current theme.
                SearchMatchStyle = text => ThemeRuntime.Current.Underline(StyleSearchMatch(text)),
                SearchCurrentMatchStyle = text =>
                    ThemeRuntime.Current.Bold(ThemeRuntime.Current.Inverse(StyleSearchMatch(text))),
                SearchNavigationButtonStyle = (text, hovered) => hovered
                    ? ThemeRuntime.Current.Underline(text)
                    : text,
                ScrollToEndIndicator = () =>
                {
                    var shortcut = KeybindingHints.KeyDisplayText("tui.altScreen.bottom");
                    var label = $" ↓ Jump to latest message{(shortcut.Length > 0 ? $" · {shortcut}" : "")} ";
                    return ThemeRuntime.Current.Bg("selectedBg", ThemeRuntime.Current.Fg("text", label));
                },
                OpenUrl = OpenBrowser.Open,
                OnRightClickPaste = options.OnRightClickPaste,
                CopyOnSelect = options.FullscreenCopyOnSelect,
                WheelScrollLines = options.FullscreenWheelScrollLines ?? WheelScrollLines.Auto,
                CopySelection = async text =>
                {
                    try
                    {
                        await Clipboard.CopyToClipboardAsync(text).ConfigureAwait(false);
                        return ClipboardCopyResult.Ok;
                    }
                    catch (Exception error)
                    {
                        // TS returns `error.message` (or String(error)) so the TUI can flash it.
                        return ClipboardCopyResult.Fail(error.Message);
                    }
                },
            });
        }

        return new TuiMainScreen(terminal, options.ShowHardwareCursor);
    }

    private static string StyleSearchMatch(string text) =>
        ThemeRuntime.Current.Bg("searchMatchBg", ThemeRuntime.Current.Fg("searchMatchText", text));

    /// <summary>
    /// Stable reference for components while <c>InteractiveMode</c> replaces the active renderer.
    /// TS returns a <c>Proxy</c> that resolves every property against the current TUI; C# has no
    /// proxy, so this forwards the <see cref="ITui"/> surface explicitly (C262).
    /// </summary>
    public static ITui CreateInteractiveTuiReference(Func<ITui> getTui) => new TuiReference(getTui);
}

/// <summary>
/// Forwards every <see cref="ITui"/> member to whatever renderer <paramref name="getTui"/> returns
/// right now, so a component holding this reference keeps working across a renderer swap.
/// </summary>
internal sealed class TuiReference(Func<ITui> getTui) : ITui
{
    private ITui Current => getTui();

    public TuiMode Mode => Current.Mode;

    public List<IComponent> Children => Current.Children;

    public ITerminal Terminal => Current.Terminal;

    public Action? OnDebug
    {
        get => Current.OnDebug;
        set => Current.OnDebug = value;
    }

    public int FullRedraws => Current.FullRedraws;

    public string[] Render(int width) => Current.Render(width);

    public void HandleInput(string data) => Current.HandleInput(data);

    public TuiMouseEventResult? HandleMouse(TuiMouseEvent @event) => Current.HandleMouse(@event);

    public bool WantsKeyRelease => Current.WantsKeyRelease;

    public void Invalidate() => Current.Invalidate();

    public void AddChild(IComponent component) => Current.AddChild(component);

    public void RemoveChild(IComponent component) => Current.RemoveChild(component);

    public void Clear() => Current.Clear();

    public bool GetShowHardwareCursor() => Current.GetShowHardwareCursor();

    public void SetShowHardwareCursor(bool enabled) => Current.SetShowHardwareCursor(enabled);

    public bool GetClearOnShrink() => Current.GetClearOnShrink();

    public void SetClearOnShrink(bool enabled) => Current.SetClearOnShrink(enabled);

    public void SetFocus(IComponent? component) => Current.SetFocus(component);

    public IOverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null) =>
        Current.ShowOverlay(component, options);

    public void HideOverlay() => Current.HideOverlay();

    public bool HasOverlay() => Current.HasOverlay();

    public void Start() => Current.Start();

    public void Stop(TuiStopOptions? options = null) => Current.Stop(options);

    public void RenderNow(bool force = false) => Current.RenderNow(force);

    public void RequestRender(bool force = false) => Current.RequestRender(force);

    public Action AddInputListener(Func<string, TuiInputListenerResult?> listener) =>
        Current.AddInputListener(listener);

    public void RemoveInputListener(Func<string, TuiInputListenerResult?> listener) =>
        Current.RemoveInputListener(listener);

    public Action OnTerminalColorSchemeChange(Action<TerminalColorScheme> listener) =>
        Current.OnTerminalColorSchemeChange(listener);

    public void SetTerminalColorSchemeNotifications(bool enabled) =>
        Current.SetTerminalColorSchemeNotifications(enabled);

    public Task<TerminalColorsSnapshot> QueryTerminalColorsAsync(TerminalColorQueryOptions options) =>
        Current.QueryTerminalColorsAsync(options);

    public Task<RgbColor?> QueryTerminalBackgroundColorAsync(int timeoutMs) =>
        Current.QueryTerminalBackgroundColorAsync(timeoutMs);

    public Task<TerminalColorScheme?> QueryTerminalColorSchemeAsync(int timeoutMs) =>
        Current.QueryTerminalColorSchemeAsync(timeoutMs);
}
