// ============================================================================
// Reusable single-step and multi-step submenus.
// Port of modes/interactive/components/settings-submenu.ts
// ============================================================================

using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// TS <c>string | ((context: Record&lt;string, string&gt;) =&gt; string)</c>: a submenu step's title or
/// description is either a literal or computed from the selections made so far. Modelled like
/// <c>ThemeJsonColorValue</c> (C194's sibling union) with two implicit conversions, so a call site
/// reads <c>Title = "Choose a model"</c>.
/// </summary>
/// <remarks>
/// The factory branch must go through <see cref="FromFactory"/> (or spell the lambda's parameter type
/// explicitly). C#'s implicit user-defined conversion needs a source type, and a lambda whose parameter
/// type is omitted has no natural type, so <c>Title = ctx =&gt; …</c> does not compile (CS1660).
/// </remarks>
public readonly record struct SubmenuText
{
    private SubmenuText(string? literal, Func<IReadOnlyDictionary<string, string>, string>? factory)
    {
        _literal = literal;
        _factory = factory;
    }

    private readonly string? _literal;
    private readonly Func<IReadOnlyDictionary<string, string>, string>? _factory;

    /// <summary>The literal branch, when the value was a string.</summary>
    public string? Literal => _literal;

    /// <summary>The factory branch, when the value was a function.</summary>
    public Func<IReadOnlyDictionary<string, string>, string>? Factory => _factory;

    /// <summary>Whether the value is a literal rather than a factory.</summary>
    public bool IsLiteral => _literal is not null;

    public static SubmenuText FromText(string text) => new(text, null);

    public static SubmenuText FromFactory(Func<IReadOnlyDictionary<string, string>, string> factory) =>
        new(null, factory);

    public static implicit operator SubmenuText(string text) => FromText(text);

    public static implicit operator SubmenuText(Func<IReadOnlyDictionary<string, string>, string> factory) =>
        FromFactory(factory);

    /// <summary>TS <c>typeof value === "function" ? value(context) : value</c>.</summary>
    public string Resolve(IReadOnlyDictionary<string, string> context) =>
        _literal ?? _factory!(context);
}

/// <summary>TS <c>SelectSubmenuOptions</c>.</summary>
public sealed record SelectSubmenuOptions
{
    /// <summary>Enable type-to-search fuzzy filtering.</summary>
    public bool? Searchable { get; init; }

    /// <summary>Override the select list layout (column widths).</summary>
    public SelectListLayoutOptions? Layout { get; init; }
}

/// <summary>
/// Single-step submenu that shows a titled select list.
/// With <c>Searchable</c>, typing filters the list using fuzzy matching.
/// </summary>
public sealed class SelectSubmenu : Container
{
    /// <summary>TS <c>SUBMENU_SELECT_LIST_LAYOUT</c>.</summary>
    public static readonly SelectListLayoutOptions DefaultListLayout = new()
    {
        MinPrimaryColumnWidth = 12,
        MaxPrimaryColumnWidth = 32,
    };

    private readonly IReadOnlyList<SelectItem> _allOptions;
    private readonly SelectListLayoutOptions _listLayout;
    private readonly Action<string> _onSelect;
    private readonly Action _onCancel;
    private readonly Action<string>? _onSelectionChange;
    private readonly Input? _searchInput;
    private readonly int _listChildIndex;

    private SelectList _selectList;

    public SelectSubmenu(
        string title,
        string description,
        IReadOnlyList<SelectItem> options,
        string currentValue,
        Action<string> onSelect,
        Action onCancel,
        Action<string>? onSelectionChange = null,
        SelectSubmenuOptions? submenuOptions = null)
    {
        var theme = ThemeRuntime.Current;
        _allOptions = options;
        _listLayout = submenuOptions?.Layout ?? DefaultListLayout;
        _onSelect = onSelect;
        _onCancel = onCancel;
        _onSelectionChange = onSelectionChange;

        // Title
        AddChild(new Text(theme.Bold(theme.Fg("accent", title)), 0, 0));

        // Description
        if (description.Length > 0)
        {
            AddChild(new Spacer(1));
            AddChild(new Text(theme.Fg("muted", description), 0, 0));
        }

        // Search input
        if (submenuOptions?.Searchable == true)
        {
            AddChild(new Spacer(1));
            _searchInput = new Input();
            AddChild(_searchInput);
        }

        // Spacer
        AddChild(new Spacer(1));

        // Select list
        _selectList = BuildSelectList(options, currentValue);
        _listChildIndex = GetChildrenSnapshot().Count;
        AddChild(_selectList);

        // TS wires `searchInput.onSubmit` before the list exists; the handler only reads `selectList`
        // when the user presses Enter, so assigning it here keeps both the child order and the
        // nullable-flow analysis happy.
        if (_searchInput is { } searchInput)
        {
            searchInput.OnSubmit = _ => _selectList.HandleInput("\r");
        }

        // Hint
        AddChild(new Spacer(1));
        var hint = submenuOptions?.Searchable == true
            ? "  Type to filter \u00b7 Enter to select \u00b7 Esc to go back"
            : "  Enter to select \u00b7 Esc to go back";
        AddChild(new Text(theme.Fg("dim", hint), 0, 0));
    }

    private SelectList BuildSelectList(IReadOnlyList<SelectItem> options, string preselect)
    {
        var list = new SelectList(
            options, Math.Min(options.Count, 10), ThemeHelpers.GetSelectListTheme(), _listLayout);

        var index = -1;
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].Value == preselect)
            {
                index = i;
                break;
            }
        }

        if (index != -1)
        {
            list.SetSelectedIndex(index);
        }

        list.OnSelect = item => _onSelect(item.Value);
        list.OnCancel = _onCancel;
        if (_onSelectionChange is { } selectionChange)
        {
            list.OnSelectionChange = item => selectionChange(item.Value);
        }

        return list;
    }

    private void ApplyFilter(string query)
    {
        var filtered = query.Length > 0
            ? Fuzzy.Filter(_allOptions, query, item => $"{item.Label} {item.Description ?? ""}")
            : _allOptions;

        var newList = BuildSelectList(filtered, "");
        ReplaceChildAt(_listChildIndex, newList);
        _selectList = newList;
    }

    /// <summary>
    /// Port of <c>SelectSubmenu.handleInput</c>. Declared <c>override</c> of
    /// <see cref="Container"/>'s virtual (C233): the interface map is built on the class that lists
    /// <see cref="IComponent"/>, so a fresh public method here would never become the implementation —
    /// and <see cref="Tui"/> drives the focused component through an <see cref="IComponent"/> reference.
    /// </summary>
    public override void HandleInput(string data)
    {
        if (_searchInput is not null)
        {
            var keybindings = GlobalKeybindings.Get();
            var isNavigation =
                keybindings.Matches(data, TuiKeybindingIds.SelectUp)
                || keybindings.Matches(data, TuiKeybindingIds.SelectDown)
                || keybindings.Matches(data, TuiKeybindingIds.SelectConfirm)
                || keybindings.Matches(data, TuiKeybindingIds.SelectCancel);
            if (isNavigation)
            {
                _selectList.HandleInput(data);
            }
            else
            {
                _searchInput.HandleInput(data);
                ApplyFilter(_searchInput.GetValue());
            }
        }
        else
        {
            _selectList.HandleInput(data);
        }
    }

    /// <summary>The live select list. Not in TS, but the list is swapped on every filter pass and the
    /// tests need to observe which row ended up selected.</summary>
    internal SelectList GetSelectList() => _selectList;

    /// <summary>The search box, when <c>Searchable</c> is set. Not in TS (its <c>private</c> is erased at
    /// runtime); the tests need it to reach the <c>onSubmit</c> wiring, which <see cref="HandleInput"/>
    /// never exercises because <c>tui.select.confirm</c> is routed straight to the list.</summary>
    internal Input? GetSearchInput() => _searchInput;
}

// ============================================================================
// SteppedSubmenu — reusable multi-step selector
// ============================================================================

/// <summary>One step in a <see cref="SteppedSubmenu"/>. TS <c>SteppedSubmenuStep</c>.</summary>
public sealed record SteppedSubmenuStep
{
    /// <summary>Unique key — the selected value is stored in the result context under this key.</summary>
    public required string Key { get; init; }

    /// <summary>Title shown at the top of the step. Receives prior selections.</summary>
    public required SubmenuText Title { get; init; }

    /// <summary>Description shown below the title. Receives prior selections.</summary>
    public required SubmenuText Description { get; init; }

    /// <summary>Build the option list for this step. Called fresh each time the step is shown.</summary>
    public required Func<IReadOnlyDictionary<string, string>, IReadOnlyList<SelectItem>> Options { get; init; }

    /// <summary>Optionally pre-select a value when entering this step.</summary>
    public Func<IReadOnlyDictionary<string, string>, string?>? Preselect { get; init; }

    /// <summary>Enable type-to-search fuzzy filtering for this step.</summary>
    public bool? Searchable { get; init; }

    /// <summary>Override the select list layout (column widths) for this step.</summary>
    public SelectListLayoutOptions? Layout { get; init; }
}

/// <summary>
/// TS <c>SteppedSubmenuOptions</c>. Upstream does not export this interface; C# has no way to keep a
/// type out of the public surface while using it in a public signature, so it is public here.
/// </summary>
public sealed record SteppedSubmenuOptions
{
    /// <summary>Start at this step index (0-based), skipping earlier steps. Requires InitialContext for skipped keys.</summary>
    public int? StartAtStep { get; init; }

    /// <summary>Pre-fill selections for skipped steps.</summary>
    public IReadOnlyDictionary<string, string>? InitialContext { get; init; }

    /// <summary>After completing the last step, loop back to step 0 instead of closing.</summary>
    public bool? Loop { get; init; }
}

/// <summary>
/// Generic N-step submenu built on top of <see cref="SelectSubmenu"/>.
/// <para>
/// Each step's options can depend on prior selections via the shared context. Esc goes back one step;
/// Esc at step 0 cancels. With <c>Loop</c>, completing the final step invokes <c>onComplete</c> then
/// returns to step 0.
/// </para>
/// </summary>
public sealed class SteppedSubmenu : Container
{
    private readonly IReadOnlyList<SteppedSubmenuStep> _steps;
    private readonly Action<IReadOnlyDictionary<string, string>> _onComplete;
    private readonly Action _onCancel;
    private readonly SteppedSubmenuOptions _options;

    private IComponent _activeComponent;
    private Dictionary<string, string> _context;

    public SteppedSubmenu(
        IReadOnlyList<SteppedSubmenuStep> steps,
        Action<IReadOnlyDictionary<string, string>> onComplete,
        Action onCancel,
        SteppedSubmenuOptions? opts = null)
    {
        _steps = steps;
        _onComplete = onComplete;
        _onCancel = onCancel;
        _options = opts ?? new SteppedSubmenuOptions();
        _context = _options.InitialContext is { } initial
            ? new Dictionary<string, string>(initial)
            : [];
        _activeComponent = BuildStep(_options.StartAtStep ?? 0);
    }

    /// <summary>The component of the step currently on screen. TS <c>private activeComponent</c>.</summary>
    internal IComponent ActiveComponent => _activeComponent;

    /// <summary>A snapshot of the selections made so far. TS <c>private context</c>.</summary>
    internal IReadOnlyDictionary<string, string> Context => _context;

    private IComponent BuildStep(int stepIndex)
    {
        var step = _steps[stepIndex];
        var total = _steps.Count;
        var stepLabel = total > 1 ? $"Step {stepIndex + 1}/{total} \u00b7 " : "";

        var title = step.Title.Resolve(_context);
        var description = step.Description.Resolve(_context);
        var items = step.Options(_context);
        var preselect = step.Preselect?.Invoke(_context) ?? "";

        return new SelectSubmenu(
            title,
            $"{stepLabel}{description}",
            items,
            preselect,
            value =>
            {
                _context[step.Key] = value;

                if (stepIndex < total - 1)
                {
                    // Advance to next step
                    _activeComponent = BuildStep(stepIndex + 1);
                }
                else
                {
                    // Final step — deliver result
                    _onComplete(new Dictionary<string, string>(_context));

                    if (_options.Loop == true)
                    {
                        _context = [];
                        _activeComponent = BuildStep(0);
                    }
                    else
                    {
                        _onCancel();
                    }
                }
            },
            () =>
            {
                if (stepIndex > 0)
                {
                    _context.Remove(step.Key);
                    _activeComponent = BuildStep(stepIndex - 1);
                }
                else
                {
                    _onCancel();
                }
            },
            null,
            step.Searchable == true || step.Layout is not null
                ? new SelectSubmenuOptions { Searchable = step.Searchable, Layout = step.Layout }
                : null);
    }

    public override string[] Render(int width) => _activeComponent.Render(width);

    /// <summary>
    /// Port of <c>SteppedSubmenu.handleInput</c>: forwards to the active step. Declared <c>override</c>
    /// of <see cref="Container"/>'s virtual (C233). The TS <c>handleInput?.(data)</c> optional call is
    /// unnecessary here because <see cref="IComponent.HandleInput"/> has a default implementation, but
    /// the override itself is mandatory for the interface dispatch to reach it.
    /// </summary>
    public override void HandleInput(string data) => _activeComponent.HandleInput(data);

    public override void Invalidate() => _activeComponent.Invalidate();
}
