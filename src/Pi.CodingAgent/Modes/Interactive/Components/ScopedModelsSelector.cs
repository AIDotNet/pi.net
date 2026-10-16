// ============================================================================
// Model configuration panel: enable/disable models for Ctrl+P cycling.
// Changes are session-only until explicitly persisted.
// Port of modes/interactive/components/scoped-models-selector.ts
// ============================================================================

using Pi.Ai.Models;
using Pi.CodingAgent.Core;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// TS <c>EnabledIds = string[] | null</c>: <c>null</c> means "all enabled, no filter", an explicit
/// list means that ordered list. The two states are observably different (the footer reads
/// "all enabled" only for <c>null</c>), so the port keeps <see cref="IReadOnlyList{T}"/> nullable
/// rather than folding <c>null</c> into an empty list.
/// </summary>
public sealed record ModelsConfig
{
    public required IReadOnlyList<ModelSpec> AllModels { get; init; }

    public required IReadOnlyList<string>? EnabledModelIds { get; init; }

    public string? RefreshStatus { get; init; }
}

/// <summary>TS <c>ModelsCallbacks</c>.</summary>
/// <remarks>
/// TS types <c>onChange</c> / <c>onPersist</c> as <c>() =&gt; void | Promise&lt;void&gt;</c>, but the
/// component never awaits them — it fires and forgets — so the promise is unobservable and the port
/// uses plain <see cref="Action{T}"/>.
/// </remarks>
public sealed record ModelsCallbacks
{
    /// <summary>Called whenever the enabled model set or order changes (session-only, no persist).</summary>
    public required Action<IReadOnlyList<string>?> OnChange { get; init; }

    /// <summary>Called when the user wants to persist the current selection to settings.</summary>
    public required Action<IReadOnlyList<string>?> OnPersist { get; init; }

    public required Action OnCancel { get; init; }
}

/// <summary>
/// TS <c>ModelItem</c>: one rendered row. <see cref="Model"/> is null for an id that is still in the
/// enabled list but no longer in the catalog (an "unavailable" model, rendered struck through).
/// </summary>
public sealed record ModelItem
{
    public required string FullId { get; init; }

    public required ModelSpec? Model { get; init; }

    public required bool Enabled { get; init; }
}

/// <summary>
/// Component for enabling/disabling models for Ctrl+P cycling.
/// TS <c>ScopedModelsSelectorComponent extends Container implements Focusable</c>.
/// </summary>
public sealed class ScopedModelsSelectorComponent : Container, IFocusable
{
    private const int MaxVisible = 8;

    private readonly Dictionary<string, ModelSpec> _modelsById = new(StringComparer.Ordinal);
    private readonly Input _searchInput;
    private readonly Container _listContainer;
    private readonly Text _footerText;
    private readonly ModelsCallbacks _callbacks;
    private readonly Text? _refreshStatusText;

    private List<string> _allIds = [];
    private IReadOnlyList<string>? _enabledIds;
    private List<ModelItem> _filteredItems = [];
    private int _selectedIndex;
    private bool _focused;
    private bool _isDirty;

    public ScopedModelsSelectorComponent(ModelsConfig config, ModelsCallbacks callbacks)
    {
        var theme = ThemeRuntime.Current;
        _callbacks = callbacks;

        foreach (var model in config.AllModels)
        {
            var fullId = $"{model.Provider}/{model.Id}";
            _modelsById[fullId] = model;
            _allIds.Add(fullId);
        }

        _enabledIds = config.EnabledModelIds is null ? null : [.. config.EnabledModelIds];
        _filteredItems = BuildItems();

        // Header
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("accent", theme.Bold("Model Configuration")), 0, 0));
        AddChild(new Text(
            theme.Fg("muted", $"Session-only. {KeybindingHints.KeyText("app.models.save")} to save to settings."),
            0,
            0));
        AddChild(new Spacer(1));

        // Search input
        _searchInput = new Input();
        AddChild(_searchInput);
        AddChild(new Spacer(1));

        // List container
        _listContainer = new Container();
        AddChild(_listContainer);

        // Footer hint
        AddChild(new Spacer(1));
        if (!string.IsNullOrEmpty(config.RefreshStatus))
        {
            _refreshStatusText = new Text(theme.Fg("muted", $"  {config.RefreshStatus}"), 0, 0);
            AddChild(_refreshStatusText);
        }

        _footerText = new Text(GetFooterText(), 0, 0);
        AddChild(_footerText);

        AddChild(new DynamicBorder());
        UpdateList();
    }

    /// <summary>Focusable implementation — propagate to the search input for IME cursor positioning.</summary>
    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            _searchInput.Focused = value;
        }
    }

    /// <summary>TS <c>updateModels</c>: swap in a fresh catalog, keeping the selection on its model.</summary>
    /// <remarks>
    /// TS's <c>enabledModelIds?: string[] | null</c> is a tri-state — not passed, explicit
    /// <c>null</c> (= all enabled), or a list — and C# cannot tell "argument omitted" from
    /// "argument passed as null". Two overloads carry the distinction: the one-argument form is
    /// "not passed" (leave the current selection alone), the two-argument form is "set it",
    /// where <c>null</c> resets to all-enabled.
    /// </remarks>
    public void UpdateModels(IReadOnlyList<ModelSpec> models) => UpdateModels(models, null, setEnabledIds: false);

    /// <inheritdoc cref="UpdateModels(IReadOnlyList{ModelSpec})"/>
    /// <param name="enabledModelIds">
    /// The new selection; <c>null</c> means "all enabled" (TS's <c>EnabledIds = null</c>).
    /// </param>
    public void UpdateModels(IReadOnlyList<ModelSpec> models, IReadOnlyList<string>? enabledModelIds) =>
        UpdateModels(models, enabledModelIds, setEnabledIds: true);

    private void UpdateModels(IReadOnlyList<ModelSpec> models, IReadOnlyList<string>? enabledModelIds, bool setEnabledIds)
    {
        var selectedId = _filteredItems.Count > _selectedIndex ? _filteredItems[_selectedIndex].FullId : null;
        if (setEnabledIds)
        {
            _enabledIds = enabledModelIds is null ? null : [.. enabledModelIds];
        }

        _modelsById.Clear();
        _allIds = [];
        foreach (var model in models)
        {
            var fullId = $"{model.Provider}/{model.Id}";
            _modelsById[fullId] = model;
            _allIds.Add(fullId);
        }

        Refresh();
        var refreshedIndex = selectedId is null
            ? -1
            : _filteredItems.FindIndex(item => item.FullId == selectedId);
        if (refreshedIndex >= 0)
        {
            _selectedIndex = refreshedIndex;
            UpdateList();
        }
    }

    /// <summary>TS <c>setRefreshStatus</c>.</summary>
    public void SetRefreshStatus(string message, StatusKind kind) =>
        _refreshStatusText?.SetText(ThemeRuntime.Current.Fg(TokenName(kind), $"  {message}"));

    /// <summary>TS <c>"muted" | "success" | "warning"</c> — the three tokens <c>setRefreshStatus</c> accepts.</summary>
    public enum StatusKind
    {
        Muted,
        Success,
        Warning,
    }

    private static string TokenName(StatusKind kind) => kind switch
    {
        StatusKind.Muted => "muted",
        StatusKind.Success => "success",
        StatusKind.Warning => "warning",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>TS <c>getSearchInput</c>: read-only test seam (TS's <c>private</c> is readable at runtime; C#'s is not).</summary>
    public Input GetSearchInput() => _searchInput;

    // ---------------------------------------------------------------------------------------------
    // Enabled-id algebra (TS module-level functions)
    // ---------------------------------------------------------------------------------------------

    private static bool IsEnabled(IReadOnlyList<string>? enabledIds, string id) =>
        enabledIds is null || enabledIds.Contains(id, StringComparer.Ordinal);

    /// <summary>
    /// Collapse an explicit list back to <c>null</c> (= all enabled) when it covers every available
    /// model. TS: <c>result.length === allIds.length &amp;&amp; result.every((id) =&gt; allIds.includes(id))</c>.
    /// </summary>
    private static IReadOnlyList<string>? NormalizeEnabled(IReadOnlyList<string> result, IReadOnlyList<string> allIds) =>
        result.Count == allIds.Count && result.All(id => allIds.Contains(id, StringComparer.Ordinal))
            ? null
            : result;

    private static IReadOnlyList<string>? Toggle(IReadOnlyList<string>? enabledIds, IReadOnlyList<string> allIds, string id)
    {
        if (enabledIds is null)
        {
            return allIds.Where(modelId => modelId != id).ToList();
        }

        var index = enabledIds.ToList().IndexOf(id);
        if (index >= 0)
        {
            var list = enabledIds.ToList();
            list.RemoveAt(index);
            return list;
        }

        return NormalizeEnabled([.. enabledIds, id], allIds);
    }

    private static IReadOnlyList<string>? EnableAll(
        IReadOnlyList<string>? enabledIds,
        IReadOnlyList<string> allIds,
        IReadOnlyList<string>? targetIds = null)
    {
        if (enabledIds is null)
        {
            return null; // Already all enabled
        }

        var targets = targetIds ?? allIds;
        var result = enabledIds.ToList();
        foreach (var id in targets)
        {
            if (!result.Contains(id, StringComparer.Ordinal))
            {
                result.Add(id);
            }
        }

        return NormalizeEnabled(result, allIds);
    }

    private static IReadOnlyList<string>? ClearAll(
        IReadOnlyList<string>? enabledIds,
        IReadOnlyList<string> allIds,
        IReadOnlyList<string>? targetIds = null)
    {
        if (enabledIds is null)
        {
            return targetIds is not null
                ? allIds.Where(id => !targetIds.Contains(id, StringComparer.Ordinal)).ToList()
                : [];
        }

        var targets = new HashSet<string>(targetIds ?? (IEnumerable<string>)enabledIds, StringComparer.Ordinal);
        return enabledIds.Where(id => !targets.Contains(id)).ToList();
    }

    private static IReadOnlyList<string>? Move(IReadOnlyList<string>? enabledIds, string id, int delta)
    {
        if (enabledIds is null)
        {
            return null;
        }

        var list = enabledIds.ToList();
        var index = list.IndexOf(id);
        if (index < 0)
        {
            return list;
        }

        var newIndex = index + delta;
        if (newIndex < 0 || newIndex >= list.Count)
        {
            return list;
        }

        (list[index], list[newIndex]) = (list[newIndex], list[index]);
        return list;
    }

    /// <summary>
    /// Enabled ids first (in their stored order), then the rest in catalog order.
    /// TS: <c>[...enabledIds, ...allIds.filter((id) =&gt; !enabledSet.has(id))]</c>.
    /// </summary>
    private List<string> GetSortedIds(IReadOnlyList<string>? enabledIds, IReadOnlyList<string> allIds)
    {
        if (enabledIds is null)
        {
            return [.. allIds];
        }

        var enabledSet = new HashSet<string>(enabledIds, StringComparer.Ordinal);
        return [.. enabledIds, .. allIds.Where(id => !enabledSet.Contains(id))];
    }

    // ---------------------------------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------------------------------

    private List<ModelItem> BuildItems() =>
        [.. GetSortedIds(_enabledIds, _allIds).Select(id => new ModelItem
        {
            FullId = id,
            Model = _modelsById.TryGetValue(id, out var model) ? model : null,
            Enabled = IsEnabled(_enabledIds, id),
        })];

    private string GetFooterText()
    {
        var theme = ThemeRuntime.Current;
        var enabledCount = _enabledIds?.Count(id => _modelsById.ContainsKey(id)) ?? _allIds.Count;
        var unavailableCount = _enabledIds?.Count(id => !_modelsById.ContainsKey(id)) ?? 0;
        var allEnabled = _enabledIds is null;
        var countText = allEnabled
            ? "all enabled"
            : $"{enabledCount}/{_allIds.Count} enabled"
                + (unavailableCount > 0 ? $" · {unavailableCount} unavailable" : "");
        var parts = new[]
        {
            $"{KeybindingHints.KeyText("tui.select.confirm")} toggle",
            $"{KeybindingHints.KeyText("app.models.enableAll")} all",
            $"{KeybindingHints.KeyText("app.models.clearAll")} clear",
            $"{KeybindingHints.KeyText("app.models.toggleProvider")} provider",
            $"{KeybindingHints.KeyText("app.models.reorderUp")}/{KeybindingHints.KeyText("app.models.reorderDown")} reorder",
            $"{KeybindingHints.KeyText("app.models.save")} save",
            countText,
        };
        return _isDirty
            ? theme.Fg("dim", $"  {string.Join(" · ", parts)} ") + theme.Fg("warning", "(unsaved)")
            : theme.Fg("dim", $"  {string.Join(" · ", parts)}");
    }

    private void Refresh()
    {
        var query = _searchInput.GetValue();
        var items = BuildItems();
        _filteredItems = query.Length > 0
            ? Fuzzy.Filter(
                items,
                query,
                item => item.Model is { } model
                    ? ModelSearch.GetModelSearchText(new ModelSearchItem
                    {
                        Id = model.Id,
                        Provider = model.Provider,
                        Name = model.Name,
                    })
                    : item.FullId)
            : items;
        _selectedIndex = Math.Min(_selectedIndex, Math.Max(0, _filteredItems.Count - 1));
        UpdateList();
        _footerText.SetText(GetFooterText());
    }

    private void NotifyChange() =>
        _callbacks.OnChange(_enabledIds is null ? null : [.. _enabledIds]);

    private void UpdateList()
    {
        var theme = ThemeRuntime.Current;
        _listContainer.Clear();

        if (_filteredItems.Count == 0)
        {
            _listContainer.AddChild(new Text(theme.Fg("muted", "  No matching models"), 0, 0));
            return;
        }

        var startIndex = Math.Max(
            0,
            Math.Min(_selectedIndex - (MaxVisible / 2), _filteredItems.Count - MaxVisible));
        var endIndex = Math.Min(startIndex + MaxVisible, _filteredItems.Count);
        for (var i = startIndex; i < endIndex; i++)
        {
            var item = _filteredItems[i];
            var isSelected = i == _selectedIndex;
            var prefix = isSelected ? theme.Fg("accent", "→ ") : "  ";
            var id = item.Model?.Id ?? item.FullId;
            var styledId = item.Model is not null ? id : theme.Strikethrough(id);
            var modelText = isSelected ? theme.Fg("accent", styledId) : styledId;
            var providerBadge = theme.Fg("muted", item.Model is not null ? $" [{item.Model.Provider}]" : " [unavailable]");
            var status = item.Model is not null && item.Enabled ? theme.Fg("accent", "✓ ") : "  ";
            _listContainer.AddChild(new Text($"{prefix}{status}{modelText}{providerBadge}", 0, 0));
        }

        // Add scroll indicator if needed
        if (startIndex > 0 || endIndex < _filteredItems.Count)
        {
            _listContainer.AddChild(
                new Text(theme.Fg("muted", $"  ({_selectedIndex + 1}/{_filteredItems.Count})"), 0, 0));
        }

        if (_filteredItems.Count > 0)
        {
            var selected = _filteredItems[_selectedIndex];
            _listContainer.AddChild(new Spacer(1));
            _listContainer.AddChild(new Text(
                theme.Fg("muted", selected.Model is not null
                    ? $"  Model Name: {selected.Model.Name}"
                    : "  Model unavailable"),
                0,
                0));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Input
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Port of <c>ScopedModelsSelectorComponent.handleInput</c>. Declared <c>override</c> of
    /// <see cref="Container"/>'s virtual (C233): the interface map is built for the class that lists
    /// <see cref="IComponent"/>, so a fresh public method here would never be the implementation that
    /// <see cref="Tui"/> invokes through the focused-component reference.
    /// </summary>
    public override void HandleInput(string data)
    {
        var kb = GlobalKeybindings.Get();

        // Navigation
        if (kb.Matches(data, TuiKeybindingIds.SelectUp))
        {
            if (_filteredItems.Count == 0)
            {
                return;
            }

            _selectedIndex = _selectedIndex == 0 ? _filteredItems.Count - 1 : _selectedIndex - 1;
            UpdateList();
            return;
        }

        if (kb.Matches(data, TuiKeybindingIds.SelectDown))
        {
            if (_filteredItems.Count == 0)
            {
                return;
            }

            _selectedIndex = _selectedIndex == _filteredItems.Count - 1 ? 0 : _selectedIndex + 1;
            UpdateList();
            return;
        }

        // Reorder enabled models
        var reorderUp = kb.Matches(data, AppKeybindings.AppModelsReorderUp);
        var reorderDown = kb.Matches(data, AppKeybindings.AppModelsReorderDown);
        if (reorderUp || reorderDown)
        {
            if (_enabledIds is null)
            {
                return;
            }

            var item = _selectedIndex < _filteredItems.Count ? _filteredItems[_selectedIndex] : null;
            if (item is not null && IsEnabled(_enabledIds, item.FullId))
            {
                var delta = reorderUp ? -1 : 1;
                var currentIndex = _enabledIds.ToList().IndexOf(item.FullId);
                var newIndex = currentIndex + delta;

                // Only move if within bounds
                if (newIndex >= 0 && newIndex < _enabledIds.Count)
                {
                    _enabledIds = Move(_enabledIds, item.FullId, delta);
                    _isDirty = true;
                    _selectedIndex += delta;
                    Refresh();
                    NotifyChange();
                }
            }

            return;
        }

        // Toggle on Enter
        if (kb.Matches(data, TuiKeybindingIds.SelectConfirm))
        {
            var item = _selectedIndex < _filteredItems.Count ? _filteredItems[_selectedIndex] : null;
            if (item is not null)
            {
                _enabledIds = Toggle(_enabledIds, _allIds, item.FullId);
                _isDirty = true;
                Refresh();
                NotifyChange();
            }

            return;
        }

        // Enable all (filtered if search active, otherwise all)
        if (kb.Matches(data, AppKeybindings.AppModelsEnableAll))
        {
            var targetIds = _searchInput.GetValue().Length > 0
                ? _filteredItems.Select(item => item.FullId).ToList()
                : null;
            _enabledIds = EnableAll(_enabledIds, _allIds, targetIds);
            _isDirty = true;
            Refresh();
            NotifyChange();
            return;
        }

        // Clear all (filtered if search active, otherwise all)
        if (kb.Matches(data, AppKeybindings.AppModelsClearAll))
        {
            var targetIds = _searchInput.GetValue().Length > 0
                ? _filteredItems.Select(item => item.FullId).ToList()
                : null;
            _enabledIds = ClearAll(_enabledIds, _allIds, targetIds);
            _isDirty = true;
            Refresh();
            NotifyChange();
            return;
        }

        // Toggle provider of current item
        if (kb.Matches(data, AppKeybindings.AppModelsToggleProvider))
        {
            var item = _selectedIndex < _filteredItems.Count ? _filteredItems[_selectedIndex] : null;
            if (item?.Model is not null)
            {
                var provider = item.Model.Provider;
                var providerIds = _allIds
                    .Where(id => _modelsById.TryGetValue(id, out var model) && model.Provider == provider)
                    .ToList();
                var allEnabled = providerIds.All(id => IsEnabled(_enabledIds, id));
                _enabledIds = allEnabled
                    ? ClearAll(_enabledIds, _allIds, providerIds)
                    : EnableAll(_enabledIds, _allIds, providerIds);
                _isDirty = true;
                Refresh();
                NotifyChange();
            }

            return;
        }

        // Save/persist to settings
        if (kb.Matches(data, AppKeybindings.AppModelsSave))
        {
            _callbacks.OnPersist(_enabledIds is null ? null : [.. _enabledIds]);
            _isDirty = false;
            _footerText.SetText(GetFooterText());
            return;
        }

        // Ctrl+C - clear search or cancel if empty
        if (Keys.MatchesKey(data, "ctrl+c"))
        {
            if (_searchInput.GetValue().Length > 0)
            {
                _searchInput.SetValue("");
                Refresh();
            }
            else
            {
                _callbacks.OnCancel();
            }

            return;
        }

        // Escape - cancel
        if (Keys.MatchesKey(data, "escape"))
        {
            _callbacks.OnCancel();
            return;
        }

        // Pass everything else to search input
        _searchInput.HandleInput(data);
        Refresh();
    }
}
