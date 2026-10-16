// ============================================================================
// Editor with the coding-agent's app-level keybindings and an optional working
// status indicator embedded in the top border.
// Port of modes/interactive/components/custom-editor.ts
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.Tui;
using Pi.Tui.Components;

// `KeybindingsManager` exists in both namespaces (the TUI's base manager and the app's layered one);
// the app one is what TS's `core/keybindings.ts` exports.
using CoreKeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// TS <c>EditorOptions &amp; { embedWorkingStatus?: boolean }</c>. C# has no intersection types, so the
/// extension is modelled as a derived options type — which is why <see cref="EditorOptions"/> is no
/// longer sealed (C238).
/// </summary>
public sealed class CustomEditorOptions : EditorOptions
{
    /// <summary>Render working, compaction, summarization, and retry status in the editor's top border.</summary>
    public bool? EmbedWorkingStatus { get; set; }
}

/// <summary>
/// Custom editor that handles app-level keybindings for the coding agent.
/// TS <c>CustomEditor extends Editor</c>.
/// </summary>
public class CustomEditor : Editor
{
    private readonly CoreKeybindingsManager _keybindings;
    private StatusIndicator? _workingStatusIndicator;

    public CustomEditor(
        ITui tui,
        EditorTheme theme,
        CoreKeybindingsManager keybindings,
        CustomEditorOptions? options = null)
        : base(tui, theme, options)
    {
        _keybindings = keybindings;
        EmbedWorkingStatus = options?.EmbedWorkingStatus ?? false;
    }

    /// <summary>TS <c>readonly embedWorkingStatus</c>.</summary>
    public bool EmbedWorkingStatus { get; }

    /// <summary>
    /// TS <c>actionHandlers: Map&lt;AppKeybinding, () =&gt; void&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Enumeration order is the TS <c>Map</c> order — insertion order, with <c>ActionHandlers[key] = …</c>
    /// overwriting in place. .NET's <see cref="Dictionary{TKey, TValue}"/> enumerates its entries array in
    /// insertion order as long as nothing is removed (there is no removal here), so the loop in
    /// <see cref="HandleInput"/> sees the same sequence. The order is observable: two app actions bound to
    /// the same key resolve to whichever was registered first.
    /// </remarks>
    public Dictionary<string, Action> ActionHandlers { get; } = new(StringComparer.Ordinal);

    /// <summary>Special handler that can be dynamically replaced.</summary>
    public Action? OnEscape { get; set; }

    /// <summary>Special handler that can be dynamically replaced.</summary>
    public Action? OnCtrlD { get; set; }

    /// <summary>Special handler that can be dynamically replaced.</summary>
    public Action? OnPasteImage { get; set; }

    /// <summary>Handler for extension-registered shortcuts. Returns true if handled.</summary>
    public Func<string, bool>? OnExtensionShortcut { get; set; }

    public void SetWorkingStatusIndicator(StatusIndicator? indicator) => _workingStatusIndicator = indicator;

    /// <summary>Port of <c>CustomEditor.renderTopBorder</c>.</summary>
    protected override string RenderTopBorder(int width, int hiddenLineCount)
    {
        if (!EmbedWorkingStatus || _workingStatusIndicator is null || width <= 0)
        {
            return base.RenderTopBorder(width, hiddenLineCount);
        }

        var indicator = _workingStatusIndicator;
        var status = indicator.RenderInBorder(Math.Max(1, width - 5));
        var statusWidth = UnicodeWidth.VisibleWidth(status);
        if (statusWidth == 0)
        {
            return base.RenderTopBorder(width, hiddenLineCount);
        }

        var overflowLabel = hiddenLineCount > 0 ? $" ↑ {hiddenLineCount} more " : null;
        var overflowLabelWidth = overflowLabel is null ? 0 : UnicodeWidth.VisibleWidth(overflowLabel);
        var overflowStart = (width - overflowLabelWidth) / 2;

        bool CanFitOverflow() =>
            overflowLabel is not null
            && overflowLabelWidth + 2 <= width
            && overflowStart - (3 + statusWidth + 1) >= 1;

        if (overflowLabel is not null && !CanFitOverflow())
        {
            status = indicator.RenderSpinnerInBorder(width);
            statusWidth = UnicodeWidth.VisibleWidth(status);
        }

        if (CanFitOverflow())
        {
            var leftBlockWidth = 3 + statusWidth + 1;
            return BorderColor("── ")
                + status
                + BorderColor(
                    $" {new string('─', overflowStart - leftBlockWidth)}{overflowLabel}"
                    + $"{new string('─', width - overflowStart - overflowLabelWidth)}");
        }

        if (width >= statusWidth + 5)
        {
            return BorderColor("── ")
                + status
                + BorderColor($" {new string('─', width - statusWidth - 4)}");
        }

        status = indicator.RenderSpinnerInBorder(width);
        statusWidth = UnicodeWidth.VisibleWidth(status);
        var prefixWidth = Math.Min(3, Math.Max(0, width - statusWidth));
        return BorderColor(new string('─', prefixWidth))
            + status
            + BorderColor(new string('─', Math.Max(0, width - prefixWidth - statusWidth)));
    }

    /// <summary>Register a handler for an app action.</summary>
    public void OnAction(string action, Action handler) => ActionHandlers[action] = handler;

    /// <summary>
    /// Port of <c>CustomEditor.handleInput</c>. Declared <c>override</c> of <see cref="Editor"/>'s virtual
    /// (C233): <see cref="Editor"/> is the class that lists <see cref="IEditorComponent"/>, so a fresh
    /// public method here would never enter the interface map.
    /// </summary>
    public override void HandleInput(string data)
    {
        // Check extension-registered shortcuts first
        if (OnExtensionShortcut?.Invoke(data) == true)
        {
            return;
        }

        // Check for clipboard paste keybinding
        if (_keybindings.Matches(data, AppKeybindings.AppClipboardPasteImage))
        {
            OnPasteImage?.Invoke();
            return;
        }

        // Check app keybindings first

        // Escape/interrupt - only if autocomplete is NOT active
        if (_keybindings.Matches(data, AppKeybindings.AppInterrupt))
        {
            if (!IsShowingAutocomplete())
            {
                // Use dynamic onEscape if set, otherwise the registered handler
                var handler = OnEscape
                    ?? (ActionHandlers.TryGetValue(AppKeybindings.AppInterrupt, out var registered)
                        ? registered
                        : null);
                if (handler is not null)
                {
                    handler();
                    return;
                }
            }

            // Let the parent handle escape for autocomplete cancellation
            base.HandleInput(data);
            return;
        }

        // Exit (Ctrl+D) - only when the editor is empty
        if (_keybindings.Matches(data, AppKeybindings.AppExit))
        {
            if (GetText().Length == 0)
            {
                var handler = OnCtrlD
                    ?? (ActionHandlers.TryGetValue(AppKeybindings.AppExit, out var registered)
                        ? registered
                        : null);
                handler?.Invoke();
                return;
            }

            // Fall through to editor handling for delete-char-forward when not empty
        }

        // Explicit history bindings take precedence over app actions while the editor is focused.
        // This lets users bind Ctrl+P even though it cycles models by default.
        if (_keybindings.Matches(data, TuiKeybindingIds.EditorHistoryPrevious)
            || _keybindings.Matches(data, TuiKeybindingIds.EditorHistoryNext))
        {
            base.HandleInput(data);
            return;
        }

        // Check all other app actions
        foreach (var (action, handler) in ActionHandlers)
        {
            if (action != AppKeybindings.AppInterrupt
                && action != AppKeybindings.AppExit
                && _keybindings.Matches(data, action))
            {
                handler();
                return;
            }
        }

        // Pass to parent for editor handling
        base.HandleInput(data);
    }
}
