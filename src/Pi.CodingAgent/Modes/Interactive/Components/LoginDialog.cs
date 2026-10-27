// ============================================================================
// Login dialog: replaces the editor during the OAuth login flow.
// Port of modes/interactive/components/login-dialog.ts
// ============================================================================

using Pi.Ai.Auth;
using Pi.Ai.Compat;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;

// `Text` exists in both Pi.Tui.Components (the renderable) and Pi.CodingAgent.Utils (the string
// helpers), and this file needs both namespaces.
using TuiText = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Port of <c>login-dialog.ts</c>: the panel that stands in for the editor while a provider's OAuth
/// flow runs. The host drives it through <see cref="ShowAuth"/> / <see cref="ShowDeviceCode"/> /
/// <see cref="ShowManualInputAsync"/> / <see cref="ShowPromptAsync"/> / <see cref="ShowDetails"/> /
/// <see cref="ShowInfo"/> / <see cref="ShowWaiting"/> / <see cref="ShowProgress"/> and awaits
/// <see cref="Completed"/>-style callbacks passed to the constructor.
/// </summary>
public sealed class LoginDialogComponent : Container, IFocusable
{
    /// <summary>
    /// OSC-8 sequences used by this file. TS writes them inline here with a BEL (<c>\x07</c>)
    /// terminator, while <c>terminal-image.ts</c>'s <c>hyperlink()</c> helper — ported as
    /// <see cref="TerminalImage.Hyperlink"/> — uses ST (<c>\x1b\\</c>). Both are valid; the login dialog
    /// keeps BEL so the rendered bytes match the TS source.
    /// </summary>
    private const string Osc8Open = "\x1b]8;;";

    private const string Osc8Bel = "\x07";

    private const string Osc8Close = "\x1b]8;;\x07";

    private static string BelHyperlink(string text, string url) => $"{Osc8Open}{url}{Osc8Bel}{text}{Osc8Close}";

    /// <summary>
    /// 只认 http/https。共享的打开器同时也能开文件和自定义协议，登录只能打开网页地址。
    /// 顺便把控制字符百分号编码，否则它们会提前结束终端超链接。
    /// </summary>
    private static string? WebUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? url.ToString()
            : null;

    /// <summary>能规范成网页地址就规范化，否则把控制字符替换成空格（不可信字符串不能原样打印）。</summary>
    private static string DisplayUrl(string value)
        => WebUrl(value) ?? Ansi.ReplaceControlCharacters(value);

    /// <summary>网页地址才做成终端超链接，其余只留纯文本。</summary>
    private static string WebLink(string text, string url)
        => WebUrl(url) is { } target ? BelHyperlink(text, target) : text;

    private readonly Container _contentContainer;
    private readonly Input _input;
    private readonly ITui _tui;
    private readonly Action<bool, string?> _onComplete;
    private readonly CancellationTokenSource _abortController = new();

    /// <summary>The shown sign-in URL, which <c>app.message.copy</c> copies. TS <c>authUrl?</c>.</summary>
    private AuthUrlComponent? _authUrl;

    /// <summary>
    /// The pending <c>showManualInput</c> / <c>showPrompt</c> promise. TS keeps a separate
    /// <c>inputResolver</c> and <c>inputRejecter</c>; C# folds both into one completion source.
    /// </summary>
    private TaskCompletionSource<string>? _inputCompletion;

    private bool _focused;

    public LoginDialogComponent(
        ITui tui,
        string providerId,
        Action<bool, string?> onComplete,
        string? providerNameOverride = null,
        string? titleOverride = null)
    {
        _tui = tui;
        _onComplete = onComplete;

        var theme = ThemeRuntime.Current;
        var providerName = string.IsNullOrEmpty(providerNameOverride) ? providerId : providerNameOverride;
        var title = titleOverride ?? $"Login to {providerName}";

        // Top border
        AddChild(new DynamicBorder());

        // Title
        AddChild(new TuiText(theme.Fg("accent", theme.Bold(title)), 1, 0));

        // Dynamic content area
        _contentContainer = new Container();
        AddChild(_contentContainer);

        // Input (always present, used when needed)
        _input = new Input();
        _input.OnSubmit = _ =>
        {
            if (_inputCompletion is not null)
            {
                var value = _input.GetValue();
                ReplaceInputWithSubmittedText(value);
                _inputCompletion.TrySetResult(value);
                _inputCompletion = null;
            }
        };
        _input.OnEscape = Cancel;

        // Bottom border
        AddChild(new DynamicBorder());
    }

    /// <summary>
    /// Cancellation for the in-flight login. TS <c>get signal(): AbortSignal</c>; the port follows the
    /// repo-wide <c>AbortSignal</c> → <see cref="CancellationToken"/> mapping (C2).
    /// </summary>
    public CancellationToken Signal => _abortController.Token;

    // Focusable implementation - propagate to input for IME cursor positioning
    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _input.Focused = value;
        }
    }

    private void ReplaceInputWithSubmittedText(string value)
    {
        // TS rebuilds the array with `children.map(child => child === input ? new TuiText(...) : child)`.
        var children = _contentContainer.GetChildrenSnapshot();
        for (var i = 0; i < children.Count; i++)
        {
            if (ReferenceEquals(children[i], _input))
            {
                _contentContainer.ReplaceChildAt(i, new TuiText($"> {value}", 0, 0));
            }
        }
    }

    private void Cancel()
    {
        _abortController.Cancel();
        if (_inputCompletion is not null)
        {
            // TS rejects with `new Error("Login cancelled")`; cancellation is normalised to
            // OperationCanceledException repo-wide (C2 / C44).
            _inputCompletion.TrySetException(new OperationCanceledException("Login cancelled", _abortController.Token));
            _inputCompletion = null;
        }

        _onComplete(false, "Login cancelled");
    }

    /// <summary>Called by the <c>onAuth</c> callback — show URL and optional instructions.</summary>
    public void ShowAuth(string url, string? instructions = null)
    {
        var target = WebUrl(url);
        _contentContainer.Clear();
        _contentContainer.AddChild(new Spacer(1));
        _authUrl = new AuthUrlComponent(_tui, target ?? Ansi.ReplaceControlCharacters(url));
        _contentContainer.AddChild(_authUrl);

        if (!string.IsNullOrEmpty(instructions))
        {
            _contentContainer.AddChild(new Spacer(1));
            _contentContainer.AddChild(new TuiText(ThemeRuntime.Current.Fg("warning", instructions), 1, 0));
        }

        if (target is not null)
        {
            OpenBrowser.Open(target);
        }

        _tui.RequestRender();
    }

    /// <summary>Called by the <c>onDeviceCode</c> callback — show URL and user code.</summary>
    public void ShowDeviceCode(OAuthDeviceCodeInfo info)
    {
        var theme = ThemeRuntime.Current;
        _authUrl = null;
        _contentContainer.Clear();
        _contentContainer.AddChild(new Spacer(1));

        var shownUrl = DisplayUrl(info.VerificationUri);
        var linkedUrl = WebLink(shownUrl, shownUrl);
        _contentContainer.AddChild(new TuiText(theme.Fg("accent", linkedUrl), 1, 0));

        var clickHint = OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open";
        var hyperlink = WebLink(clickHint, info.VerificationUri);
        _contentContainer.AddChild(new TuiText(theme.Fg("dim", hyperlink), 1, 0));
        _contentContainer.AddChild(new Spacer(1));
        _contentContainer.AddChild(new TuiText(theme.Fg("warning", $"Enter code: {info.UserCode}"), 1, 0));

        _tui.RequestRender();
    }

    /// <summary>Show input for manual code/URL entry (for callback server providers).</summary>
    public Task<string> ShowManualInputAsync(string prompt)
    {
        _input.SetValue("");
        _contentContainer.AddChild(new Spacer(1));
        _contentContainer.AddChild(new TuiText(ThemeRuntime.Current.Fg("dim", prompt), 1, 0));
        _contentContainer.AddChild(_input);
        _contentContainer.AddChild(new TuiText(
            $"({KeybindingHints.KeyHint(TuiKeybindingIds.SelectCancel, "to cancel")})", 1, 0));
        _tui.RequestRender();

        return BeginInput();
    }

    /// <summary>
    /// Called by the <c>onPrompt</c> callback — show prompt and wait for input.
    /// Note: does NOT clear content, appends to existing (preserves the URL from <see cref="ShowAuth"/>).
    /// </summary>
    public Task<string> ShowPromptAsync(string message, string? placeholder = null)
    {
        var theme = ThemeRuntime.Current;
        _contentContainer.AddChild(new Spacer(1));
        _contentContainer.AddChild(new TuiText(theme.Fg("text", message), 1, 0));
        if (!string.IsNullOrEmpty(placeholder))
        {
            _contentContainer.AddChild(new TuiText(theme.Fg("dim", $"e.g., {placeholder}"), 1, 0));
        }

        _contentContainer.AddChild(_input);
        _contentContainer.AddChild(new TuiText(
            $"({KeybindingHints.KeyHint(TuiKeybindingIds.SelectCancel, "to cancel,")} "
            + $"{KeybindingHints.KeyHint(TuiKeybindingIds.SelectConfirm, "to submit")})",
            1,
            0));

        _input.SetValue("");
        _tui.RequestRender();

        return BeginInput();
    }

    /// <summary>Show informational text before another login step.</summary>
    public void ShowDetails(IReadOnlyList<string> lines)
    {
        _authUrl = null;
        _contentContainer.Clear();
        _contentContainer.AddChild(new Spacer(1));
        foreach (var line in lines)
        {
            _contentContainer.AddChild(new TuiText(line, 1, 0));
        }

        _tui.RequestRender();
    }

    /// <summary>Show provider-owned information and links without starting an auth callback flow.</summary>
    public void ShowInfo(string message, IReadOnlyList<AuthInfoLink>? links = null, bool showCloseHint = false)
    {
        var theme = ThemeRuntime.Current;
        _contentContainer.AddChild(new Spacer(1));
        _contentContainer.AddChild(new TuiText(theme.Fg("text", message), 1, 0));

        foreach (var link in links ?? [])
        {
            var url = DisplayUrl(link.Url);
            var text = link.Label is { Length: > 0 } label ? $"{label}: {url}" : url;
            _contentContainer.AddChild(new TuiText(theme.Fg("accent", WebLink(text, url)), 1, 0));
        }

        if (showCloseHint)
        {
            _contentContainer.AddChild(new Spacer(1));
            _contentContainer.AddChild(new TuiText(
                $"({KeybindingHints.KeyHint(TuiKeybindingIds.SelectCancel, "to close")})", 1, 0));
        }

        _tui.RequestRender();
    }

    /// <summary>Show a waiting message (for polling flows like GitHub Copilot).</summary>
    public void ShowWaiting(string message)
    {
        _contentContainer.AddChild(new Spacer(1));
        _contentContainer.AddChild(new TuiText(ThemeRuntime.Current.Fg("dim", message), 1, 0));
        _contentContainer.AddChild(new TuiText(
            $"({KeybindingHints.KeyHint(TuiKeybindingIds.SelectCancel, "to cancel")})", 1, 0));
        _tui.RequestRender();
    }

    /// <summary>Called by the <c>onProgress</c> callback.</summary>
    public void ShowProgress(string message)
    {
        _contentContainer.AddChild(new TuiText(ThemeRuntime.Current.Fg("dim", message), 1, 0));
        _tui.RequestRender();
    }

    /// <summary>
    /// Port of <c>LoginDialogComponent.handleInput</c>. Declared <c>override</c> of
    /// <see cref="Container"/>'s virtual (C233): the interface map is built on the class that lists
    /// <see cref="IComponent"/>, so a fresh public method here would never become the implementation.
    /// </summary>
    public override void HandleInput(string data)
    {
        var keybindings = GlobalKeybindings.Get();

        if (keybindings.Matches(data, TuiKeybindingIds.SelectCancel))
        {
            Cancel();
            return;
        }

        if (_authUrl is not null && keybindings.Matches(data, AppKeybindings.AppMessageCopy))
        {
            // TS `void this.authUrl.copy()`: fire and forget, the hint text updates when it settles.
            _ = _authUrl.CopyAsync();
            return;
        }

        // Pass to input
        _input.HandleInput(data);
    }

    private Task<string> BeginInput()
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inputCompletion = completion;
        return completion.Task;
    }
}
