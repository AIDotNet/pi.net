// ============================================================================
// Project-trust selector component.
// Port of modes/interactive/components/trust-selector.ts
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>TS <c>TrustSelection = Pick&lt;ProjectTrustOption, "trusted" | "updates"&gt;</c>.</summary>
public sealed record TrustSelection(bool Trusted, IReadOnlyList<ProjectTrustUpdate> Updates);

/// <summary>TS <c>TrustSelectorOptions</c>.</summary>
public sealed record TrustSelectorOptions
{
    public required string Cwd { get; init; }

    public required ProjectTrustStoreEntry? SavedDecision { get; init; }

    public required bool ProjectTrusted { get; init; }

    public required Action<TrustSelection> OnSelect { get; init; }

    public required Action OnCancel { get; init; }
}

/// <summary>Port of <c>trust-selector.ts</c>: the project-trust prompt.</summary>
public sealed class TrustSelectorComponent : Container
{
    private readonly Container _listContainer = new();
    private readonly IReadOnlyList<ProjectTrustOption> _trustOptions;
    private readonly ProjectTrustStoreEntry? _savedDecision;
    private readonly Action<TrustSelection> _onSelect;
    private readonly Action _onCancel;
    private int _selectedIndex;

    public TrustSelectorComponent(TrustSelectorOptions options)
    {
        var theme = ThemeRuntime.Current;
        _savedDecision = options.SavedDecision;
        _trustOptions = TrustManager.GetProjectTrustOptions(options.Cwd);
        _selectedIndex = Math.Max(0, FindSavedOptionIndex());
        _onSelect = options.OnSelect;
        _onCancel = options.OnCancel;

        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("accent", theme.Bold("Project trust")), 1, 0));
        AddChild(new Text(theme.Fg("muted", options.Cwd), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new Text(
            theme.Fg("muted", $"Saved decision: {FormatDecision(_trustOptions[0].SavedPath, options.SavedDecision)}"),
            1,
            0));
        AddChild(new Text(
            theme.Fg("muted", $"Current session: {(options.ProjectTrusted ? "trusted" : "untrusted")}"),
            1,
            0));
        AddChild(new Spacer(1));

        AddChild(_listContainer);
        AddChild(new Spacer(1));
        AddChild(new Text(
            KeybindingHints.RawKeyHint("↑↓", "navigate")
            + "  "
            + KeybindingHints.KeyHint(TuiKeybindingIds.SelectConfirm, "save")
            + "  "
            + KeybindingHints.KeyHint(TuiKeybindingIds.SelectCancel, "cancel"),
            1,
            0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());

        UpdateList();
    }

    /// <summary>TS <c>formatDecision(trustPath, decision)</c>.</summary>
    private static string FormatDecision(string? trustPath, ProjectTrustStoreEntry? decision)
    {
        if (decision is null) return "none";
        var label = decision.Decision ? "trusted" : "untrusted";
        if (trustPath is not null && decision.Path != trustPath)
        {
            return $"{label} (inherited from {decision.Path})";
        }
        return $"{label} ({decision.Path})";
    }

    private bool IsSavedOption(ProjectTrustOption option)
        => option.SavedPath is not null
            && _savedDecision?.Decision == option.Trusted
            && _savedDecision.Path == option.SavedPath;

    private int FindSavedOptionIndex()
    {
        for (var index = 0; index < _trustOptions.Count; index++)
        {
            if (IsSavedOption(_trustOptions[index])) return index;
        }
        return -1;
    }

    private void UpdateList()
    {
        var theme = ThemeRuntime.Current;
        _listContainer.Clear();
        for (var index = 0; index < _trustOptions.Count; index++)
        {
            var option = _trustOptions[index];
            var isSelected = index == _selectedIndex;
            var isCurrent = IsSavedOption(option);
            var currentMarker = isCurrent ? theme.Fg("accent", "✓ ") : "  ";
            var prefix = isSelected ? theme.Fg("accent", "→ ") : "  ";
            var label = isSelected ? theme.Fg("accent", option.Label) : theme.Fg("text", option.Label);
            _listContainer.AddChild(new Text($"{prefix}{currentMarker}{label}", 1, 0));
        }
    }

    /// <summary>
    /// Port of <c>TrustSelectorComponent.handleInput</c>. Declared <c>override</c> of
    /// <see cref="Container"/>'s virtual (C233). It must be an <c>override</c>, not a fresh public
    /// method: the interface map is built on the class that lists <see cref="IComponent"/>
    /// (<see cref="Container"/>), so a subclass method of the same name would never be reached through
    /// an <see cref="IComponent"/> reference — which is exactly how <see cref="Tui"/> drives the
    /// focused component.
    /// </summary>
    public override void HandleInput(string data)
    {
        var keybindings = GlobalKeybindings.Get();
        if (keybindings.Matches(data, TuiKeybindingIds.SelectUp) || data == "k")
        {
            _selectedIndex = Math.Max(0, _selectedIndex - 1);
            UpdateList();
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectDown) || data == "j")
        {
            _selectedIndex = Math.Min(_trustOptions.Count - 1, _selectedIndex + 1);
            UpdateList();
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectConfirm) || data == "\n")
        {
            var selected = _trustOptions[_selectedIndex];
            _onSelect(new TrustSelection(selected.Trusted, selected.Updates));
        }
        else if (keybindings.Matches(data, TuiKeybindingIds.SelectCancel))
        {
            _onCancel();
        }
    }
}
