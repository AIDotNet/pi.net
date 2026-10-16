// ============================================================================
// Thinking-level selector component.
// Port of modes/interactive/components/thinking-selector.ts
// ============================================================================

using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>Port of <c>thinking-selector.ts</c>: a searchable thinking-level picker with borders.</summary>
public sealed class ThinkingSelectorComponent : Container, IFocusable
{
    /// <summary>TS <c>THINKING_SELECT_LIST_LAYOUT</c>.</summary>
    private static readonly SelectListLayoutOptions ListLayout = new()
    {
        MinPrimaryColumnWidth = 12,
        MaxPrimaryColumnWidth = 32,
    };

    /// <summary>TS <c>LEVEL_DESCRIPTIONS</c>.</summary>
    private static readonly IReadOnlyDictionary<ThinkingLevel, string> LevelDescriptions =
        new Dictionary<ThinkingLevel, string>
        {
            [ThinkingLevel.Off] = "No reasoning",
            [ThinkingLevel.Minimal] = "Very brief reasoning (~1k tokens)",
            [ThinkingLevel.Low] = "Light reasoning (~2k tokens)",
            [ThinkingLevel.Medium] = "Moderate reasoning (~8k tokens)",
            [ThinkingLevel.High] = "Deep reasoning (~16k tokens)",
            [ThinkingLevel.XHigh] = "Extra-high reasoning (~32k tokens)",
            [ThinkingLevel.Max] = "Maximum reasoning",
        };

    private readonly Input _searchInput;
    private readonly int _selectListChildIndex;
    private readonly IReadOnlyList<SelectItem> _allItems;
    private readonly Action<ThinkingLevel> _onSelect;
    private readonly Action _onCancel;
    private readonly Action<ThinkingLevel>? _onSelectAsDefault;
    private SelectList _selectList;
    private bool _focused;

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _searchInput.Focused = value;
        }
    }

    public ThinkingSelectorComponent(
        ThinkingLevel currentLevel,
        IReadOnlyList<ThinkingLevel> availableLevels,
        Action<ThinkingLevel> onSelect,
        Action onCancel,
        Action<ThinkingLevel>? onSelectAsDefault = null,
        ThinkingLevel? defaultThinkingLevel = null)
    {
        var theme = ThemeRuntime.Current;
        _onSelect = onSelect;
        _onCancel = onCancel;
        _onSelectAsDefault = onSelectAsDefault;

        _allItems = availableLevels.Select(level => new SelectItem
        {
            Value = ThinkingLevelText.ToText(level),
            Label = $"{(level == currentLevel ? "✓ " : "  ")}{ThinkingLevelText.ToText(level)}",
            Description = level == defaultThinkingLevel
                ? $"{LevelDescriptions[level]} · default"
                : LevelDescriptions[level],
        }).ToList();

        // Add top border
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text("Thinking Level", 0, 0));
        AddChild(new Spacer(1));
        AddChild(new Text(
            $"{KeybindingHints.KeyDisplayText(AppKeybindings.AppThinkingCycle)} cycles thinking levels in-session",
            0,
            0));
        AddChild(new Spacer(1));

        // Create selector. TS assigns `searchInput.onSubmit` before `selectList` exists (the closure
        // reads the field at call time); the port builds the list first so the field is definitely
        // assigned when the lambda is created — the child order is unchanged.
        _selectList = BuildSelectList(_allItems, currentLevel);

        _searchInput = new Input();
        _searchInput.OnSubmit = _ => _selectList.HandleInput("\r");
        AddChild(_searchInput);
        AddChild(new Spacer(1));

        _selectListChildIndex = Children.Count;
        AddChild(_selectList);
        AddChild(new Spacer(1));
        AddChild(new Text(
            theme.Fg(
                "dim",
                $"  {KeybindingHints.KeyDisplayText(TuiKeybindingIds.SelectConfirm)} to select · "
                + $"{KeybindingHints.KeyDisplayText(AppKeybindings.AppThinkingSave)} to set as default · "
                + $"{KeybindingHints.KeyDisplayText(TuiKeybindingIds.SelectCancel)} to cancel"),
            0,
            0));

        // Add bottom border
        AddChild(new DynamicBorder());
    }

    private SelectList BuildSelectList(IReadOnlyList<SelectItem> items, ThinkingLevel? preselect)
    {
        var list = new SelectList(items, Math.Max(1, items.Count), ThemeHelpers.GetSelectListTheme(), ListLayout);
        var wanted = preselect is null ? null : ThinkingLevelText.ToText(preselect.Value);
        var currentIndex = -1;
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].Value == wanted)
            {
                currentIndex = index;
                break;
            }
        }
        if (currentIndex != -1)
        {
            list.SetSelectedIndex(currentIndex);
        }
        list.OnSelect = item => _onSelect(ParseLevel(item.Value));
        list.OnCancel = _onCancel;
        return list;
    }

    private void ApplyFilter(string query)
    {
        var filtered = query.Length > 0
            ? Fuzzy.Filter(_allItems, query, item => $"{item.Value} {item.Description ?? ""}")
            : _allItems;
        var selectedValue = _selectList.GetSelectedItem()?.Value;
        var newList = BuildSelectList(filtered, selectedValue is null ? null : ThinkingLevelText.Parse(selectedValue));
        Children[_selectListChildIndex] = newList;
        _selectList = newList;
    }

    /// <summary>
    /// Port of <c>ThinkingSelectorComponent.handleInput</c>. Declared <c>override</c> of
    /// <see cref="Container"/>'s virtual, for the same reason as <see cref="TrustSelectorComponent"/>
    /// (C233).
    /// </summary>
    public override void HandleInput(string data)
    {
        var keybindings = GlobalKeybindings.Get();
        if (keybindings.Matches(data, AppKeybindings.AppThinkingSave) && _onSelectAsDefault is not null)
        {
            var item = _selectList.GetSelectedItem();
            if (item is not null) _onSelectAsDefault(ParseLevel(item.Value));
            return;
        }

        var isNavigation =
            keybindings.Matches(data, TuiKeybindingIds.SelectUp)
            || keybindings.Matches(data, TuiKeybindingIds.SelectDown)
            || keybindings.Matches(data, TuiKeybindingIds.SelectConfirm)
            || keybindings.Matches(data, TuiKeybindingIds.SelectCancel);
        if (isNavigation)
        {
            _selectList.HandleInput(data);
            return;
        }

        _searchInput.HandleInput(data);
        ApplyFilter(_searchInput.GetValue());
    }

    public SelectList GetSelectList() => _selectList;

    /// <summary>
    /// TS casts <c>item.value as ThinkingLevel</c> (an unchecked cast on a string union). The port's
    /// <see cref="ThinkingLevel"/> is an enum, so the raw string has to be parsed; an unparsable value
    /// can only come from a caller-supplied <c>SelectItem</c> and falls back to <c>off</c>.
    /// </summary>
    private static ThinkingLevel ParseLevel(string value) => ThinkingLevelText.Parse(value) ?? ThinkingLevel.Off;
}
