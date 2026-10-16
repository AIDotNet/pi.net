// ============================================================================
// Simple text input component for extensions.
// Port of modes/interactive/components/extension-input.ts
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>TS <c>ExtensionInputOptions</c>.</summary>
public sealed record ExtensionInputOptions
{
    /// <summary>The terminal UI, needed to schedule renders for the countdown.</summary>
    public ITui? Tui { get; init; }

    /// <summary>Auto-cancel after this many milliseconds.</summary>
    public int? Timeout { get; init; }

    public string? InitialValue { get; init; }

    public string? Description { get; init; }
}

/// <summary>Port of <c>extension-input.ts</c>: a bordered single-line prompt for extensions.</summary>
public sealed class ExtensionInputComponent : Container, IFocusable
{
    private readonly Input _input;
    private readonly Action<string> _onSubmit;
    private readonly Action _onCancel;
    private readonly Text _titleText;
    private readonly string _baseTitle;
    private CountdownTimer? _countdown;
    private bool _focused;

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _input.Focused = value;
        }
    }

    /// <param name="placeholder">
    /// Unused, exactly as in TS (the parameter is named <c>_placeholder</c> there and never read).
    /// Kept in the signature so the call site matches.
    /// </param>
    public ExtensionInputComponent(
        string title,
        string? placeholder,
        Action<string> onSubmit,
        Action onCancel,
        ExtensionInputOptions? opts = null)
    {
        _ = placeholder;
        var theme = ThemeRuntime.Current;
        _onSubmit = onSubmit;
        _onCancel = onCancel;
        _baseTitle = title;

        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));

        _titleText = new Text(theme.Fg("accent", title), 1, 0);
        AddChild(_titleText);
        if (opts?.Description is { Length: > 0 } description)
        {
            AddChild(new Spacer(1));
            AddChild(new Text(theme.Fg("text", description), 1, 0));
        }

        AddChild(new Spacer(1));

        if (opts?.Timeout is > 0 && opts.Tui is not null)
        {
            _countdown = new CountdownTimer(
                opts.Timeout.Value,
                opts.Tui,
                seconds => _titleText.SetText(theme.Fg("accent", $"{_baseTitle} ({seconds}s)")),
                () => _onCancel());
        }

        _input = new Input();
        if (opts?.InitialValue is { Length: > 0 } initialValue) _input.SetValue(initialValue);
        AddChild(_input);
        AddChild(new Spacer(1));
        AddChild(new Text(
            $"{KeybindingHints.KeyHint(TuiKeybindingIds.SelectConfirm, "submit")}  "
            + KeybindingHints.KeyHint(TuiKeybindingIds.SelectCancel, "cancel"),
            1,
            0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
    }

    /// <summary>
    /// Port of <c>ExtensionInputComponent.handleInput</c>. Declared <c>override</c> of
    /// <see cref="Container"/>'s virtual (C233): the interface map is built on the class that lists
    /// <see cref="IComponent"/>, so a fresh public method here would never become the implementation.
    /// </summary>
    public override void HandleInput(string data)
    {
        var keybindings = GlobalKeybindings.Get();
        if (keybindings.Matches(data, TuiKeybindingIds.SelectConfirm) || data == "\n")
        {
            _onSubmit(_input.GetValue());
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectCancel))
        {
            _onCancel();
        }
        else
        {
            _input.HandleInput(data);
        }
    }

    /// <summary>TS <c>dispose()</c>.</summary>
    public void Dispose()
    {
        _countdown?.Dispose();
        _countdown = null;
    }
}
