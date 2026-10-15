// ============================================================================
// Sign-in URL with click/copy hints.
// Port of modes/interactive/components/auth-url.ts
// ============================================================================

using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// A sign-in URL with a click hint and a copy hint. Hosts call <see cref="CopyAsync"/> when
/// <c>app.message.copy</c> is pressed, since a long URL wraps and often cannot be selected or
/// clicked as a whole (SSH, tmux). Port of <c>AuthUrlComponent</c>.
/// </summary>
public sealed class AuthUrlComponent : Container
{
    private readonly ITui _tui;
    private readonly Text _hint;

    public AuthUrlComponent(ITui tui, string url)
    {
        _tui = tui;
        Url = url;
        AddChild(new Text(
            ThemeRuntime.Current.Fg("accent", TerminalImage.Hyperlink(url, url)),
            1,
            0));
        _hint = new Text("", 1, 0);
        AddChild(_hint);
        SetHint(KeybindingHints.KeyHint("app.message.copy", "to copy"));
    }

    /// <summary>The sign-in URL. TS <c>readonly url</c>.</summary>
    public string Url { get; }

    private void SetHint(string suffix)
    {
        var clickHint = OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open";
        _hint.SetText(
            $"{ThemeRuntime.Current.Fg("dim", TerminalImage.Hyperlink(clickHint, Url))} " +
            $"{ThemeRuntime.Current.Fg("dim", "•")} {suffix}");
        _tui.RequestRender();
    }

    public async Task CopyAsync()
    {
        try
        {
            await Utils.Clipboard.CopyToClipboardAsync(Url);
            SetHint(ThemeRuntime.Current.Fg("success", "Copied URL to clipboard"));
        }
        catch (Exception error)
        {
            SetHint(ThemeRuntime.Current.Fg("error", error.Message));
        }
    }
}
