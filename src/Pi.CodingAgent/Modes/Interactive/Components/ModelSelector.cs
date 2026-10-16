// ============================================================================
// Model selector with search for the /model command.
// Port of modes/interactive/components/model-selector.ts
// ============================================================================

using Pi.Ai.Models;
using Pi.Ai.Utils;
using Pi.CodingAgent.Core;
using Pi.Tui;
using Pi.Tui.Components;

// `KeybindingsManager` exists in both namespaces (the TUI's base manager and the app's layered one);
// the app one is what TS's `core/keybindings.ts` exports.
using CoreKeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

// `Text` exists in both Pi.Tui.Components (the component) and Pi.Ai.Utils (the string helpers the
// ModelOperations reference pulls in); the component meaning is what this file uses.
using Text = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Component that renders a model selector with search.
/// TS <c>ModelSelectorComponent extends Container implements Focusable</c>.
/// </summary>
/// <remarks>
/// The file-local TS interfaces (<c>ModelItem</c> / <c>ScopedModelItem</c> / <c>DefaultModelReference</c> /
/// <c>ModelScope</c>) are modelled as public nested types — the namespace already carries an unrelated
/// <c>ModelItem</c> from <see cref="ScopedModelsSelectorComponent"/>.
/// </remarks>
public sealed class ModelSelectorComponent : Container, IFocusable
{
    /// <summary>TS <c>type ModelScope = "all" | "scoped"</c>.</summary>
    public enum ModelScope
    {
        All,
        Scoped,
    }

    /// <summary>TS <c>interface ModelItem</c>: one row of the model list.</summary>
    public sealed record ModelItem
    {
        public required string Provider { get; init; }

        public required string Id { get; init; }

        public required ModelSpec Model { get; init; }
    }

    /// <summary>TS <c>interface ScopedModelItem</c>.</summary>
    public sealed record ScopedModelItem
    {
        public required ModelSpec Model { get; init; }

        public string? ThinkingLevel { get; init; }
    }

    /// <summary>TS <c>interface DefaultModelReference</c>.</summary>
    public sealed record DefaultModelReference
    {
        public required string Provider { get; init; }

        public required string Id { get; init; }
    }

    private readonly Input _searchInput;
    private readonly Container _listContainer;
    private readonly ITui _tui;
    private readonly ModelRuntime _modelRuntime;
    private readonly Action<ModelSpec> _onSelectCallback;
    private readonly Action<ModelSpec>? _onSelectAsDefaultCallback;
    private readonly Action _onCancelCallback;
    private readonly CancellationTokenSource _refreshAbort = new();
    private readonly ModelSpec? _currentModel;
    private readonly DefaultModelReference? _defaultModel;

    private List<ModelItem> _allModels = [];
    private List<ScopedModelItem> _scopedModels;
    private List<ModelItem> _scopedModelItems = [];
    private List<ModelItem> _activeModels = [];
    private List<ModelItem> _filteredModels = [];
    private int _selectedIndex;
    private string? _errorMessage;
    private string _refreshStatusMessage = "Refreshing model catalogs…";
    private bool _refreshStatusSuccess;
    private ModelScope _scope;
    private Text? _scopeText;
    private Text? _scopeHintText;
    private Timer? _refreshTimer;
    private bool _closed;
    private bool _focused;

    public ModelSelectorComponent(
        ITui tui,
        ModelSpec? currentModel,
        ModelRuntime modelRuntime,
        IReadOnlyList<ScopedModelItem> scopedModels,
        Action<ModelSpec> onSelect,
        Action onCancel,
        string? initialSearchInput = null,
        Action<ModelSpec>? onSelectAsDefault = null,
        DefaultModelReference? defaultModel = null)
    {
        var theme = ThemeRuntime.Current;
        _tui = tui;
        _currentModel = currentModel;
        _modelRuntime = modelRuntime;
        _scopedModels = [.. scopedModels];
        _defaultModel = defaultModel;
        _scope = scopedModels.Count > 0 ? ModelScope.Scoped : ModelScope.All;
        _onSelectCallback = onSelect;
        _onSelectAsDefaultCallback = onSelectAsDefault;
        _onCancelCallback = onCancel;

        // Add top border
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));

        // Add hint about model filtering
        if (scopedModels.Count > 0)
        {
            _scopeText = new Text(GetScopeText(), 0, 0);
            AddChild(_scopeText);
            _scopeHintText = new Text(GetScopeHintText(), 0, 0);
            AddChild(_scopeHintText);
        }
        else
        {
            const string hintText = "Only showing models from configured providers. Use /login to add providers.";
            AddChild(new Text(theme.Fg("warning", hintText), 0, 0));
        }

        AddChild(new Spacer(1));

        // Create search input
        _searchInput = new Input();
        if (!string.IsNullOrEmpty(initialSearchInput))
        {
            _searchInput.SetValue(initialSearchInput);
        }

        // C# `Input.OnSubmit` is `Action<string>` (the submitted value); TS's handler ignores it.
        _searchInput.OnSubmit = _ =>
        {
            // Enter on search input selects the first filtered item
            if (_selectedIndex < _filteredModels.Count)
            {
                HandleSelect(_filteredModels[_selectedIndex].Model);
            }
        };
        AddChild(_searchInput);

        AddChild(new Spacer(1));

        // Create list container
        _listContainer = new Container();
        AddChild(_listContainer);

        AddChild(new Spacer(1));

        // Hint
        if (_onSelectAsDefaultCallback is not null)
        {
            AddChild(new Text(
                theme.Fg(
                    "dim",
                    $"  {KeybindingHints.KeyDisplayText(TuiKeybindingIds.SelectConfirm)} to select"
                    + $" · {KeybindingHints.KeyDisplayText(AppKeybindings.AppModelsSave)} to set as default"
                    + $" · {KeybindingHints.KeyDisplayText(TuiKeybindingIds.SelectCancel)} to cancel"),
                0,
                0));
        }

        // Add bottom border
        AddChild(new DynamicBorder());

        // Render the current snapshot immediately, then refresh in the background.
        LoadModelsFromSnapshot();
        if (!string.IsNullOrEmpty(initialSearchInput))
        {
            FilterModels(initialSearchInput);
        }
        else
        {
            UpdateList();
        }

        _tui.RequestRender();
        _ = RefreshModelsAsync();
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

    /// <summary>TS <c>getSearchInput()</c> — test seam.</summary>
    public Input GetSearchInput() => _searchInput;

    /// <summary>TS <c>dispose()</c>.</summary>
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _refreshAbort.Cancel();
    }

    private void LoadModelsFromSnapshot()
    {
        var models = _modelRuntime.GetAvailableSnapshot()
            .Select(model => new ModelItem { Provider = model.Provider, Id = model.Id, Model = model })
            .ToList();
        _allModels = SortModels(models);
        _scopedModels = _scopedModels
            .Select(scoped =>
            {
                var refreshed = _modelRuntime.GetModel(scoped.Model.Provider, scoped.Model.Id);
                return refreshed is not null ? scoped with { Model = refreshed } : scoped;
            })
            .ToList();
        _scopedModelItems = _scopedModels
            .Select(scoped => new ModelItem
            {
                Provider = scoped.Model.Provider,
                Id = scoped.Model.Id,
                Model = scoped.Model,
            })
            .ToList();
        _activeModels = _scope == ModelScope.Scoped ? _scopedModelItems : _allModels;
        _filteredModels = _activeModels;
        var currentIndex = _filteredModels.FindIndex(item => ModelOperations.ModelsAreEqual(_currentModel, item.Model));
        _selectedIndex = currentIndex >= 0
            ? currentIndex
            : Math.Min(_selectedIndex, Math.Max(0, _filteredModels.Count - 1));
    }

    private async Task RefreshModelsAsync()
    {
        const int timeoutMs = 15_000;
        var timedOut = false;
        _refreshTimer = new Timer(
            _ =>
            {
                timedOut = true;
                _refreshAbort.Cancel();
            },
            null,
            timeoutMs,
            Timeout.Infinite);
        try
        {
            var result = await ModelCatalogRefresh.RefreshAsync(_modelRuntime, _refreshAbort.Token);
            if (_closed)
            {
                return;
            }

            _refreshStatusMessage = "";
            if (result.Aborted && timedOut)
            {
                _errorMessage = "Model refresh timed out; showing cached models.";
            }
            else if (result.Errors.Count == 1)
            {
                // TS `result.errors.keys().next().value` — first key in insertion order. The result
                // dictionary is append-only, so .NET's Dictionary preserves the same order.
                _errorMessage = $"Could not refresh {result.Errors.Keys.First()}; showing cached models.";
            }
            else if (result.Errors.Count > 1)
            {
                _errorMessage =
                    $"Could not refresh {result.Errors.Count} model catalogs ({string.Join(", ", result.Errors.Keys)}); showing cached models.";
            }
            else
            {
                _errorMessage = _modelRuntime.GetError();
                if (_errorMessage is null)
                {
                    _refreshStatusMessage = "Model catalogs refreshed.";
                    _refreshStatusSuccess = true;
                }
            }

            LoadModelsFromSnapshot();
            FilterModels(_searchInput.GetValue());
            _tui.RequestRender();
        }
        catch (Exception error)
        {
            if (_closed)
            {
                return;
            }

            _refreshStatusMessage = "";
            // TS: `error instanceof Error ? error.message : String(error)` — .NET values are always
            // Exceptions here, so the message branch is the only reachable one.
            _errorMessage = timedOut
                ? "Model refresh timed out; showing cached models."
                : $"Could not refresh model catalogs: {error.Message}";
            UpdateList();
            _tui.RequestRender();
        }
        finally
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }
    }

    private List<ModelItem> SortModels(List<ModelItem> models)
    {
        // Sort: current model first, default model second, then by provider. TS `Array.prototype.sort`
        // is stable in V8, so the port uses LINQ's stable OrderBy chain instead of `List.Sort`
        // (introsort, unstable) — the SessionSelectorSearch batch established that convention.
        // `localeCompare` is culture-sensitive; CurrentCulture is the closest BCL analogue.
        return models
            .OrderByDescending(item => ModelOperations.ModelsAreEqual(_currentModel, item.Model))
            .ThenByDescending(item => IsDefaultModel(item.Model))
            .ThenBy(item => item.Provider, StringComparer.CurrentCulture)
            .ToList();
    }

    private string GetScopeText()
    {
        var theme = ThemeRuntime.Current;
        var allText = _scope == ModelScope.All ? theme.Fg("accent", "all") : theme.Fg("muted", "all");
        var scopedText = _scope == ModelScope.Scoped ? theme.Fg("accent", "scoped") : theme.Fg("muted", "scoped");
        return $"{theme.Fg("muted", "Scope: ")}{allText}{theme.Fg("muted", " | ")}{scopedText}";
    }

    private string GetScopeHintText() =>
        KeybindingHints.KeyHint(TuiKeybindingIds.InputTab, "scope") + ThemeRuntime.Current.Fg("muted", " (all/scoped)");

    private bool IsDefaultModel(ModelSpec model) =>
        _defaultModel is { } defaultModel
        && defaultModel.Provider == model.Provider
        && defaultModel.Id == model.Id;

    private static bool IsDefaultSearch(string query)
    {
        var normalized = query.Trim().ToLowerInvariant();
        return normalized.Length > 0 && "default".StartsWith(normalized, StringComparison.Ordinal);
    }

    private void SetScope(ModelScope scope)
    {
        if (_scope == scope)
        {
            return;
        }

        _scope = scope;
        _activeModels = _scope == ModelScope.Scoped ? _scopedModelItems : _allModels;
        var currentIndex = _activeModels.FindIndex(item => ModelOperations.ModelsAreEqual(_currentModel, item.Model));
        _selectedIndex = currentIndex >= 0 ? currentIndex : 0;
        FilterModels(_searchInput.GetValue());
        _scopeText?.SetText(GetScopeText());
    }

    private void FilterModels(string query)
    {
        if (query.Length > 0)
        {
            var filtered = Fuzzy.Filter(_activeModels, query, item =>
            {
                var defaultText = IsDefaultModel(item.Model) ? " default" : "";
                return ModelSearch.GetModelSelectorSearchText(
                    new ModelSearchItem { Id = item.Id, Provider = item.Provider, Name = item.Model.Name })
                    + defaultText;
            });
            if (IsDefaultSearch(query))
            {
                var defaultItems = _activeModels.Where(item => IsDefaultModel(item.Model)).ToList();
                var defaultKeys = new HashSet<string>(
                    defaultItems.Select(item => $"{item.Provider}\0{item.Id}"),
                    StringComparer.Ordinal);
                _filteredModels =
                [
                    .. defaultItems,
                    .. filtered.Where(item => !defaultKeys.Contains($"{item.Provider}\0{item.Id}")),
                ];
            }
            else
            {
                _filteredModels = filtered;
            }
        }
        else
        {
            _filteredModels = _activeModels;
        }

        // When filtering by a query, move the selector to the top row so the best match is
        // highlighted. When the query is cleared, keep the current position clamped to the
        // (restored) list length.
        _selectedIndex = query.Length > 0
            ? 0
            : Math.Min(_selectedIndex, Math.Max(0, _filteredModels.Count - 1));
        UpdateList();
    }

    private void UpdateList()
    {
        var theme = ThemeRuntime.Current;
        _listContainer.Clear();

        const int maxVisible = 10;
        var startIndex = Math.Max(
            0,
            Math.Min(_selectedIndex - (maxVisible / 2), _filteredModels.Count - maxVisible));
        var endIndex = Math.Min(startIndex + maxVisible, _filteredModels.Count);

        // Show visible slice of filtered models
        for (var i = startIndex; i < endIndex; i++)
        {
            var item = _filteredModels[i];
            var isSelected = i == _selectedIndex;
            var isCurrent = ModelOperations.ModelsAreEqual(_currentModel, item.Model);
            var isDefault = IsDefaultModel(item.Model);
            var defaultBadge = isDefault ? theme.Fg("muted", " · default") : "";

            var cursor = isSelected ? theme.Fg("accent", "→ ") : "  ";
            var currentMarker = isCurrent ? theme.Fg("accent", "✓ ") : "  ";
            var modelText = isSelected ? theme.Fg("accent", item.Id) : item.Id;
            var providerBadge = theme.Fg("muted", $"[{item.Provider}]");
            var line = $"{cursor}{currentMarker}{modelText} {providerBadge}{defaultBadge}";

            _listContainer.AddChild(new Text(line, 0, 0));
        }

        // Add scroll indicator if needed
        if (startIndex > 0 || endIndex < _filteredModels.Count)
        {
            _listContainer.AddChild(
                new Text(theme.Fg("muted", $"  ({_selectedIndex + 1}/{_filteredModels.Count})"), 0, 0));
        }

        // Show error message or "no results" if empty
        if (_errorMessage is not null)
        {
            // Show error in red
            foreach (var line in _errorMessage.Split('\n'))
            {
                _listContainer.AddChild(new Text(theme.Fg("error", line), 0, 0));
            }
        }
        else if (_filteredModels.Count == 0)
        {
            _listContainer.AddChild(new Text(theme.Fg("muted", "  No matching models"), 0, 0));
        }
        else
        {
            var selected = _filteredModels[_selectedIndex];
            _listContainer.AddChild(new Spacer(1));
            _listContainer.AddChild(new Text(theme.Fg("muted", $"  Model Name: {selected.Model.Name}"), 0, 0));
        }

        if (_refreshStatusMessage.Length > 0)
        {
            _listContainer.AddChild(new Spacer(1));
            _listContainer.AddChild(new Text(
                theme.Fg(_refreshStatusSuccess ? "success" : "muted", $"  {_refreshStatusMessage}"),
                0,
                0));
        }
    }

    /// <summary>
    /// Declared <c>override</c> of <see cref="Container"/>'s virtual (C233) — see
    /// <see cref="ScopedModelsSelectorComponent.HandleInput"/>.
    /// </summary>
    public override void HandleInput(string data)
    {
        var kb = GlobalKeybindings.Get();
        if (kb.Matches(data, TuiKeybindingIds.InputTab))
        {
            if (_scopedModelItems.Count > 0)
            {
                var nextScope = _scope == ModelScope.All ? ModelScope.Scoped : ModelScope.All;
                SetScope(nextScope);
                _scopeHintText?.SetText(GetScopeHintText());
            }

            return;
        }

        // Up arrow - wrap to bottom when at top
        if (kb.Matches(data, TuiKeybindingIds.SelectUp))
        {
            if (_filteredModels.Count == 0)
            {
                return;
            }

            _selectedIndex = _selectedIndex == 0 ? _filteredModels.Count - 1 : _selectedIndex - 1;
            UpdateList();
        }
        // Down arrow - wrap to top when at bottom
        else if (kb.Matches(data, TuiKeybindingIds.SelectDown))
        {
            if (_filteredModels.Count == 0)
            {
                return;
            }

            _selectedIndex = _selectedIndex == _filteredModels.Count - 1 ? 0 : _selectedIndex + 1;
            UpdateList();
        }
        // Enter
        else if (kb.Matches(data, TuiKeybindingIds.SelectConfirm))
        {
            if (_selectedIndex < _filteredModels.Count)
            {
                HandleSelect(_filteredModels[_selectedIndex].Model);
            }
        }
        // Escape or Ctrl+C
        else if (kb.Matches(data, TuiKeybindingIds.SelectCancel))
        {
            Dispose();
            _onCancelCallback();
        }
        // Select and save as default
        else if (kb.Matches(data, AppKeybindings.AppModelsSave) && _onSelectAsDefaultCallback is not null)
        {
            if (_selectedIndex < _filteredModels.Count)
            {
                Dispose();
                _onSelectAsDefaultCallback(_filteredModels[_selectedIndex].Model);
            }
        }
        // Pass everything else to search input
        else
        {
            _searchInput.HandleInput(data);
            FilterModels(_searchInput.GetValue());
        }
    }

    private void HandleSelect(ModelSpec model)
    {
        Dispose();
        _onSelectCallback(model);
    }
}
