using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Loader wrapped with borders for extension UI (TS <c>BorderedLoader</c>).
/// </summary>
public sealed class BorderedLoader : Container
{
    private readonly IComponent _loader;
    private readonly bool _cancellable;
    private readonly CancellationTokenSource? _signalController;

    public BorderedLoader(ITui tui, Theme theme, string message, bool cancellable = true)
    {
        _cancellable = cancellable;
        var borderColor = (string s) => theme.Fg(ThemeColors.Border, s);
        AddChild(new DynamicBorder(borderColor));
        if (_cancellable)
        {
            _loader = new CancellableLoader(
                tui,
                s => theme.Fg(ThemeColors.Accent, s),
                s => theme.Fg(ThemeColors.Muted, s),
                message);
        }
        else
        {
            _signalController = new CancellationTokenSource();
            _loader = new Loader(
                tui,
                s => theme.Fg(ThemeColors.Accent, s),
                s => theme.Fg(ThemeColors.Muted, s),
                message);
        }
        AddChild(_loader);
        if (_cancellable)
        {
            AddChild(new Spacer(1));
            AddChild(new Text(KeybindingHints.KeyHint(TuiKeybindingIds.SelectCancel, "cancel"), 1, 0));
        }
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder(borderColor));
    }

    /// <summary>Abort signal: from the cancellable loader, or from the internal controller.</summary>
    public CancellationToken Signal =>
        _cancellable
            ? ((CancellableLoader)_loader).Signal
            : _signalController?.Token ?? new CancellationTokenSource().Token;

    public Action? OnAbort
    {
        set
        {
            if (_cancellable)
            {
                ((CancellableLoader)_loader).OnAbort = value;
            }
        }
    }

    /// <summary>C233: <see cref="Container"/> is the class that lists <see cref="IComponent"/>;
    /// a fresh public method would never become the interface implementation.</summary>
    public override void HandleInput(string data)
    {
        if (_cancellable)
        {
            ((CancellableLoader)_loader).HandleInput(data);
        }
    }

    /// <summary>Stops the underlying loader (TS <c>dispose()</c> / <c>stop()</c> probe).</summary>
    public void DisposeLoader()
    {
        if (_loader is IDisposable disposable)
        {
            disposable.Dispose();
        }
        else if (_loader is Loader loader)
        {
            loader.Stop();
        }
    }
}
