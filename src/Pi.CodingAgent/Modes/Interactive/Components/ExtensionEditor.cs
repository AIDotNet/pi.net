// ============================================================================
// Multi-line editor component for extensions (Ctrl+G hands off to an external
// editor). Port of modes/interactive/components/extension-editor.ts
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;

// `KeybindingsManager` exists in both namespaces (the TUI's base manager and the app's layered one);
// the app one is what TS's `core/keybindings.ts` exports.
using CoreKeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

// `Text` exists in both Pi.Tui.Components (the component) and Pi.CodingAgent.Utils (string
// helpers); the component meaning is what this file uses.
using Text = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// TS <c>ExtensionEditorOptions extends EditorOptions</c>. C# has no intersection types, so the
/// extension is modelled as a derived options type (C238 rationale — the base type is unsealed).
/// </summary>
public sealed class ExtensionEditorOptions : EditorOptions
{
    /// <summary>Optional description line rendered between the title and the editor.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// Multi-line editor component for extensions. Supports Ctrl+G for external editor.
/// TS <c>ExtensionEditorComponent extends Container implements Focusable</c>.
/// </summary>
public sealed class ExtensionEditorComponent : Container, IFocusable
{
    private readonly Editor _editor;
    private readonly Action<string> _onSubmitCallback;
    private readonly Action _onCancelCallback;
    private readonly ITui _tui;
    private readonly CoreKeybindingsManager _keybindings;
    private readonly string _externalEditorCommand;

    private bool _focused;

    public ExtensionEditorComponent(
        ITui tui,
        CoreKeybindingsManager keybindings,
        string title,
        string? prefill,
        Action<string> onSubmit,
        Action onCancel,
        ExtensionEditorOptions? options = null,
        string? externalEditorCommand = null)
    {
        var theme = ThemeRuntime.Current;
        _tui = tui;
        _keybindings = keybindings;
        _externalEditorCommand =
            ResolveExternalEditorCommand(externalEditorCommand);
        _onSubmitCallback = onSubmit;
        _onCancelCallback = onCancel;
        var description = options?.Description;

        // Add top border
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));

        // Add title and optional description
        AddChild(new Text(theme.Fg("accent", title), 1, 0));
        if (!string.IsNullOrEmpty(description))
        {
            AddChild(new Spacer(1));
            AddChild(new Text(theme.Fg("text", description), 1, 0));
        }

        AddChild(new Spacer(1));

        // Create editor
        _editor = new Editor(tui, ThemeHelpers.GetEditorTheme(), options);
        if (!string.IsNullOrEmpty(prefill))
        {
            _editor.SetText(prefill);
        }

        // Wire up Enter to submit (Shift+Enter for newlines, like the main editor)
        _editor.OnSubmit = text => _onSubmitCallback(text);
        AddChild(_editor);

        AddChild(new Spacer(1));

        // Add hint
        var hint =
            KeybindingHints.KeyHint(TuiKeybindingIds.SelectConfirm, "submit")
            + "  "
            + KeybindingHints.KeyHint(TuiKeybindingIds.InputNewLine, "newline")
            + "  "
            + KeybindingHints.KeyHint(TuiKeybindingIds.SelectCancel, "cancel")
            + $"  {KeybindingHints.KeyHint(AppKeybindings.AppEditorExternal, "external editor")}";
        AddChild(new Text(hint, 1, 0));

        AddChild(new Spacer(1));

        // Add bottom border
        AddChild(new DynamicBorder());
    }

    /// <summary>Focusable implementation — propagate to the editor for IME cursor positioning.</summary>
    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _editor.Focused = value;
        }
    }

    /// <summary>
    /// TS: <c>externalEditorCommand || process.env.VISUAL || process.env.EDITOR ||
    /// (win32 ? "notepad" : "nano")</c> — the <c>||</c> chain is truthy-based, so an empty explicit
    /// command falls through to the environment, and an empty environment falls through to the
    /// platform default.
    /// </summary>
    private static string ResolveExternalEditorCommand(string? externalEditorCommand) =>
        !string.IsNullOrEmpty(externalEditorCommand)
            ? externalEditorCommand
            : Environment.GetEnvironmentVariable("VISUAL") is { Length: > 0 } visual
                ? visual
                : Environment.GetEnvironmentVariable("EDITOR") is { Length: > 0 } editor
                    ? editor
                    : ProcessInfo.Platform == "win32" ? "notepad" : "nano";

    /// <summary>
    /// Declared <c>override</c> of <see cref="Container"/>'s virtual (C233): the interface map is
    /// built for the class that lists <see cref="IComponent"/>, so a fresh public method here would
    /// never be the implementation the TUI invokes through the focused-component reference.
    /// </summary>
    public override void HandleInput(string data)
    {
        var kb = GlobalKeybindings.Get();
        // Escape or Ctrl+C to cancel
        if (kb.Matches(data, TuiKeybindingIds.SelectCancel))
        {
            _onCancelCallback();
            return;
        }

        // External editor (app keybinding — resolved against the *injected* manager, not the
        // global one: TS uses `this.keybindings.matches(...)` here)
        if (_keybindings.Matches(data, AppKeybindings.AppEditorExternal))
        {
            _ = HandleOpenExternalEditorAsync();
            return;
        }

        // Forward to editor
        _editor.HandleInput(data);
    }

    private async Task HandleOpenExternalEditorAsync()
    {
        var content = _editor.GetText();
        _tui.Stop();
        try
        {
            var result = await ExternalEditor.EditInExternalEditorAsync(
                new ExternalEditorOptions { Command = _externalEditorCommand, Content = content });
            if (result is ExternalEditorResult.Complete complete)
            {
                _editor.SetText(complete.Content);
            }
        }
        finally
        {
            _tui.Start();
            _tui.RequestRender(true);
        }
    }
}
