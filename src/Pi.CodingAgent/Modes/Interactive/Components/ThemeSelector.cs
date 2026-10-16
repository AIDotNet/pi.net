// ============================================================================
// Theme selector component.
// Port of modes/interactive/components/theme-selector.ts
// ============================================================================

using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>Port of <c>theme-selector.ts</c>: a bordered list of the available themes with live preview.</summary>
public sealed class ThemeSelectorComponent : Container
{
    /// <summary>TS <c>THEME_SELECT_LIST_LAYOUT</c>.</summary>
    private static readonly SelectListLayoutOptions ListLayout = new()
    {
        MinPrimaryColumnWidth = 12,
        MaxPrimaryColumnWidth = 32,
    };

    private readonly SelectList _selectList;

    public ThemeSelectorComponent(
        string currentTheme,
        Action<string> onSelect,
        Action onCancel,
        Action<string> onPreview)
    {
        var themes = ThemeRuntime.GetAvailableThemes().ToList();
        var items = themes.Select(name => new SelectItem
        {
            Value = name,
            Label = name,
            Description = name == currentTheme ? "(current)" : null,
        }).ToList();

        // Add top border
        AddChild(new DynamicBorder());

        _selectList = new SelectList(items, 10, ThemeHelpers.GetSelectListTheme(), ListLayout);

        // Preselect current theme
        var currentIndex = themes.IndexOf(currentTheme);
        if (currentIndex != -1)
        {
            _selectList.SetSelectedIndex(currentIndex);
        }

        _selectList.OnSelect = item => onSelect(item.Value);
        _selectList.OnCancel = onCancel;
        _selectList.OnSelectionChange = item => onPreview(item.Value);

        AddChild(_selectList);

        // Add bottom border
        AddChild(new DynamicBorder());
    }

    public SelectList GetSelectList() => _selectList;
}
