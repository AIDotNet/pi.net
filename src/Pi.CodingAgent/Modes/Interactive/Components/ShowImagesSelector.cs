// ============================================================================
// Show-images selector component.
// Port of modes/interactive/components/show-images-selector.ts
// ============================================================================

using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>Port of <c>show-images-selector.ts</c>: a bordered yes/no selector for inline images.</summary>
public sealed class ShowImagesSelectorComponent : Container
{
    /// <summary>TS <c>SHOW_IMAGES_SELECT_LIST_LAYOUT</c>.</summary>
    private static readonly SelectListLayoutOptions ListLayout = new()
    {
        MinPrimaryColumnWidth = 12,
        MaxPrimaryColumnWidth = 32,
    };

    private readonly SelectList _selectList;

    public ShowImagesSelectorComponent(bool currentValue, Action<bool> onSelect, Action onCancel)
    {
        var items = new List<SelectItem>
        {
            new() { Value = "yes", Label = "Yes", Description = "Show images inline in terminal" },
            new() { Value = "no", Label = "No", Description = "Show text placeholder instead" },
        };

        // Add top border
        AddChild(new DynamicBorder());

        _selectList = new SelectList(items, 5, ThemeHelpers.GetSelectListTheme(), ListLayout);

        // Preselect current value
        _selectList.SetSelectedIndex(currentValue ? 0 : 1);

        _selectList.OnSelect = item => onSelect(item.Value == "yes");
        _selectList.OnCancel = onCancel;

        AddChild(_selectList);

        // Add bottom border
        AddChild(new DynamicBorder());
    }

    public SelectList GetSelectList() => _selectList;
}
