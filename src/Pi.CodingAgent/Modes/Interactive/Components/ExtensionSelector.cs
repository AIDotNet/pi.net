// ============================================================================
// Generic selector component for extensions.
// Port of modes/interactive/components/extension-selector.ts
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>TS <c>ExtensionSelectorOptions</c>.</summary>
public sealed record ExtensionSelectorOptions
{
    /// <summary>The terminal UI, needed to schedule renders for the countdown.</summary>
    public ITui? Tui { get; init; }

    /// <summary>Auto-cancel after this many milliseconds.</summary>
    public int? Timeout { get; init; }

    /// <summary>Invoked when the tools-expand keybinding fires while this selector is focused.</summary>
    public Action? OnToggleToolsExpanded { get; init; }

    public string? Description { get; init; }
}

/// <summary>
/// Port of <c>extension-selector.ts</c>: a bordered list of string options with keyboard navigation.
/// </summary>
/// <remarks>
/// <see cref="Dispose"/> is <c>virtual</c> (not <c>sealed</c>) because the TS subclass
/// <c>RadiusLoginMenuComponent</c> overrides <c>dispose()</c> to stop its shimmer timer.
/// </remarks>
public class ExtensionSelectorComponent : Container
{
    private readonly IReadOnlyList<string> _options;
    private readonly Container _listContainer = new();
    private readonly Action<string> _onSelect;
    private readonly Action _onCancel;
    private readonly Text _titleText;
    private readonly string _baseTitle;
    private readonly Action? _onToggleToolsExpanded;
    private CountdownTimer? _countdown;
    private int _selectedIndex;

    public ExtensionSelectorComponent(
        string title,
        IReadOnlyList<string> options,
        Action<string> onSelect,
        Action onCancel,
        ExtensionSelectorOptions? opts = null)
    {
        var theme = ThemeRuntime.Current;
        _options = options;
        _onSelect = onSelect;
        _onCancel = onCancel;
        _onToggleToolsExpanded = opts?.OnToggleToolsExpanded;
        _baseTitle = title;

        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));

        _titleText = new Text(theme.Fg("accent", theme.Bold(title)), 1, 0);
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
                seconds => _titleText.SetText(theme.Fg("accent", theme.Bold($"{_baseTitle} ({seconds}s)"))),
                () => _onCancel());
        }

        AddChild(_listContainer);
        AddChild(new Spacer(1));
        AddChild(new Text(
            KeybindingHints.RawKeyHint("↑↓", "navigate")
            + "  "
            + KeybindingHints.KeyHint(TuiKeybindingIds.SelectConfirm, "select")
            + "  "
            + KeybindingHints.KeyHint(TuiKeybindingIds.SelectCancel, "cancel"),
            1,
            0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());

        UpdateList();
    }

    /// <summary>Index of the highlighted row (test seam; TS keeps <c>selectedIndex</c> private).</summary>
    internal int SelectedIndex => _selectedIndex;

    private void UpdateList()
    {
        var theme = ThemeRuntime.Current;
        _listContainer.Clear();
        for (var index = 0; index < _options.Count; index++)
        {
            var isSelected = index == _selectedIndex;
            var text = isSelected
                ? theme.Fg("accent", "→ ") + theme.Fg("accent", _options[index])
                : $"  {theme.Fg("text", _options[index])}";
            _listContainer.AddChild(new Text(text, 1, 0));
        }
    }

    /// <summary>
    /// Port of <c>ExtensionSelectorComponent.handleInput</c>. Declared <c>override</c> of
    /// <see cref="Container"/>'s virtual (C233): the interface map is built on the class that lists
    /// <see cref="IComponent"/>, so a subclass declaring a fresh public method would never become the
    /// implementation. Kept <c>virtual</c> because the Radius login subclass has to intercept the
    /// confirm/cancel keys (see <see cref="RadiusLoginMenuComponent.HandleInput"/>).
    /// </summary>
    public override void HandleInput(string data)
    {
        var keybindings = GlobalKeybindings.Get();
        if (keybindings.Matches(data, AppKeybindings.AppToolsExpand))
        {
            _onToggleToolsExpanded?.Invoke();
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectUp) || data == "k")
        {
            _selectedIndex = Math.Max(0, _selectedIndex - 1);
            UpdateList();
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectDown) || data == "j")
        {
            _selectedIndex = Math.Min(_options.Count - 1, _selectedIndex + 1);
            UpdateList();
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectConfirm) || data == "\n")
        {
            // TS reads `this.options[this.selectedIndex]` and guards with `if (selected)`; an empty
            // list makes that `undefined`. The port keeps the same "nothing to select" outcome.
            var selected = _selectedIndex >= 0 && _selectedIndex < _options.Count ? _options[_selectedIndex] : null;
            if (!string.IsNullOrEmpty(selected)) _onSelect(selected);
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectCancel))
        {
            _onCancel();
        }
    }

    /// <summary>TS <c>dispose()</c>.</summary>
    public virtual void Dispose()
    {
        _countdown?.Dispose();
        _countdown = null;
    }
}
