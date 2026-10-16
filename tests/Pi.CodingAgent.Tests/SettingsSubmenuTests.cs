// ============================================================================
// Tests for modes/interactive/components/settings-submenu.ts
// The TS suite has no direct coverage for this module (it is only reached
// through settings-selector), so the vectors here come from the source's own
// behaviour: child order, the two hint strings, the filter round-trip, and the
// stepped wizard's context/back/loop semantics.
// ============================================================================

using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class SelectSubmenuTests : SelectorTestFixture
{
    private static IReadOnlyList<SelectItem> Options() =>
    [
        new SelectItem { Value = "alpha", Label = "Alpha", Description = "first" },
        new SelectItem { Value = "beta", Label = "Beta", Description = "second" },
        new SelectItem { Value = "gamma", Label = "Gamma" },
    ];

    private static SelectSubmenu Create(
        string description = "Pick one",
        IReadOnlyList<SelectItem>? options = null,
        string currentValue = "beta",
        Action<string>? onSelect = null,
        Action? onCancel = null,
        Action<string>? onSelectionChange = null,
        SelectSubmenuOptions? submenuOptions = null) =>
        new(
            "Settings",
            description,
            options ?? Options(),
            currentValue,
            onSelect ?? (_ => { }),
            onCancel ?? (() => { }),
            onSelectionChange,
            submenuOptions);

    [Fact]
    public void RendersTitleDescriptionListAndThePlainHint()
    {
        var submenu = Create();

        var output = RenderText(submenu, 100);
        Assert.Contains("Settings", output, StringComparison.Ordinal);
        Assert.Contains("Pick one", output, StringComparison.Ordinal);
        Assert.Contains("Alpha", output, StringComparison.Ordinal);
        Assert.Contains("Beta", output, StringComparison.Ordinal);
        Assert.Contains("Gamma", output, StringComparison.Ordinal);
        Assert.Contains("  Enter to select \u00b7 Esc to go back", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Type to filter", output, StringComparison.Ordinal);
        Assert.Null(submenu.GetSearchInput());
    }

    [Fact]
    public void SearchableAddsAnInputAndSwitchesTheHint()
    {
        var submenu = Create(submenuOptions: new SelectSubmenuOptions { Searchable = true });

        Assert.NotNull(submenu.GetSearchInput());
        Assert.Contains(
            "  Type to filter \u00b7 Enter to select \u00b7 Esc to go back",
            RenderText(submenu, 100),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyDescriptionOmitsTheDescriptionRow()
    {
        var withDescription = RenderLines(Create(), 100).Length;
        var withoutDescription = RenderLines(Create(description: ""), 100).Length;

        Assert.Equal(withDescription - 2, withoutDescription); // Spacer(1) + the Text row
        Assert.DoesNotContain("Pick one", RenderText(Create(description: ""), 100), StringComparison.Ordinal);
    }

    [Fact]
    public void PreselectsTheRowMatchingCurrentValue()
    {
        Assert.Equal("beta", Create(currentValue: "beta").GetSelectList().GetSelectedItem()?.Value);
    }

    [Fact]
    public void FallsBackToTheFirstRowWhenCurrentValueIsMissing()
    {
        Assert.Equal("alpha", Create(currentValue: "nope").GetSelectList().GetSelectedItem()?.Value);
    }

    [Fact]
    public void ConfirmingReportsTheSelectedValue()
    {
        string? selected = null;
        var submenu = Create(onSelect: value => selected = value);

        submenu.HandleInput("\r");

        Assert.Equal("beta", selected);
    }

    [Fact]
    public void CancellingInvokesTheCancelCallback()
    {
        var cancelled = false;
        var submenu = Create(onCancel: () => cancelled = true);

        submenu.HandleInput("\x1b");

        Assert.True(cancelled);
    }

    [Fact]
    public void SelectionChangeIsOnlyWiredWhenACallbackIsGiven()
    {
        var seen = new List<string>();
        var submenu = Create(onSelectionChange: seen.Add);

        submenu.HandleInput("\x1b[B");
        Assert.Equal(new[] { "gamma" }, seen);

        var silent = Create();
        silent.HandleInput("\x1b[B");
        Assert.Equal("gamma", silent.GetSelectList().GetSelectedItem()?.Value);
    }

    [Fact]
    public void TypingFiltersTheListInPlace()
    {
        var submenu = Create(submenuOptions: new SelectSubmenuOptions { Searchable = true });
        var before = submenu.GetSelectList();
        var childCount = submenu.Children.Count;

        submenu.HandleInput("ga");

        var after = submenu.GetSelectList();
        Assert.NotSame(before, after);
        Assert.Equal("gamma", after.GetSelectedItem()?.Value);
        // `children[listChildIndex] = newList` swaps in place, so the child count is unchanged (the
        // number of *rendered* rows does drop, because the rebuilt list only holds the matches).
        Assert.Equal(childCount, submenu.Children.Count);
    }

    /// <summary>
    /// `fuzzyFilter` is a subsequence match over `label + " " + description`, so "aa" hits Alpha
    /// (`<em>a</em>lph<em>a</em>`) and Gamma (g<em>a</em>mm<em>a</em>) but not Beta (only one "a").
    /// </summary>
    [Fact]
    public void FuzzyFilterMatchesSubsequences()
    {
        var submenu = Create(submenuOptions: new SelectSubmenuOptions { Searchable = true });

        submenu.HandleInput("aa");

        var output = RenderText(submenu, 100);
        Assert.Contains("Alpha", output, StringComparison.Ordinal);
        Assert.Contains("Gamma", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Beta", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearingTheQueryRestoresEveryOption()
    {
        var submenu = Create(submenuOptions: new SelectSubmenuOptions { Searchable = true });

        submenu.HandleInput("ga");
        Assert.DoesNotContain("Beta", RenderText(submenu, 100), StringComparison.Ordinal);

        for (var i = 0; i < 2; i++)
        {
            submenu.HandleInput("\x7f"); // backspace
        }

        Assert.Contains("Beta", RenderText(submenu, 100), StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationKeysGoToTheListRatherThanTheSearchBox()
    {
        var submenu = Create(submenuOptions: new SelectSubmenuOptions { Searchable = true });

        submenu.HandleInput("\x1b[B");

        Assert.Equal("gamma", submenu.GetSelectList().GetSelectedItem()?.Value);
        Assert.Equal("", submenu.GetSearchInput()!.GetValue());
    }

    [Fact]
    public void SearchSubmitIsWiredToTheList()
    {
        string? selected = null;
        var submenu = Create(onSelect: value => selected = value, submenuOptions: new SelectSubmenuOptions { Searchable = true });

        submenu.GetSearchInput()!.OnSubmit!("");

        Assert.Equal("beta", selected);
    }

    [Fact]
    public void LayoutOverrideReplacesTheDefaultColumns()
    {
        Assert.Equal(12, SelectSubmenu.DefaultListLayout.MinPrimaryColumnWidth);
        Assert.Equal(32, SelectSubmenu.DefaultListLayout.MaxPrimaryColumnWidth);

        // A 4-column primary column leaves only `4 - PRIMARY_COLUMN_GAP = 2` visible characters, so the
        // labels are truncated. The description column is *not* clamped, so "second" survives.
        var narrow = Create(submenuOptions: new SelectSubmenuOptions
        {
            Layout = new SelectListLayoutOptions { MinPrimaryColumnWidth = 4, MaxPrimaryColumnWidth = 4 },
        });

        Assert.StartsWith("→ Be ", SelectedRow(narrow, 60), StringComparison.Ordinal);
        Assert.DoesNotContain("Beta", RenderText(narrow, 60), StringComparison.Ordinal);
        Assert.Contains("second", RenderText(narrow, 60), StringComparison.Ordinal);

        // The default layout clamps to 12, which is wider than every label, so nothing is truncated.
        Assert.StartsWith("→ Beta ", SelectedRow(Create(), 100), StringComparison.Ordinal);
        Assert.Contains("Beta", RenderText(Create(), 100), StringComparison.Ordinal);
    }
}

public sealed class SteppedSubmenuTests : SelectorTestFixture
{
    private static IReadOnlyList<SelectItem> Items(params string[] values) =>
        [.. values.Select(value => new SelectItem { Value = value, Label = value })];

    private static SteppedSubmenuStep FirstStep(bool searchable = false) => new()
    {
        Key = "provider",
        Title = "Provider",
        Description = "Pick a provider",
        Options = _ => Items("anthropic", "openai"),
        Preselect = _ => "openai",
        Searchable = searchable,
    };

    private static SteppedSubmenuStep SecondStep() => new()
    {
        Key = "model",
        Title = SubmenuText.FromFactory(context => $"Model for {context["provider"]}"),
        Description = SubmenuText.FromFactory(context => $"Provider is {context["provider"]}"),
        Options = context => context["provider"] == "anthropic" ? Items("claude") : Items("gpt"),
    };

    private static SteppedSubmenu Create(
        IReadOnlyList<SteppedSubmenuStep>? steps = null,
        Action<IReadOnlyDictionary<string, string>>? onComplete = null,
        Action? onCancel = null,
        SteppedSubmenuOptions? opts = null) =>
        new(
            steps ?? [FirstStep(), SecondStep()],
            onComplete ?? (_ => { }),
            onCancel ?? (() => { }),
            opts);

    [Fact]
    public void RendersTheFirstStepWithTheStepLabel()
    {
        var output = RenderText(Create(), 100);
        Assert.Contains("Provider", output, StringComparison.Ordinal);
        Assert.Contains("Step 1/2 \u00b7 Pick a provider", output, StringComparison.Ordinal);
        Assert.Contains("anthropic", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleStepHasNoStepLabel()
    {
        var submenu = Create([FirstStep()]);

        Assert.Contains("Pick a provider", RenderText(submenu, 100), StringComparison.Ordinal);
        Assert.DoesNotContain("Step 1/1", RenderText(submenu, 100), StringComparison.Ordinal);
    }

    [Fact]
    public void PreselectAppliesToTheCurrentStep()
    {
        var submenu = Create();

        Assert.Equal("openai", submenu.ActiveComponent is SelectSubmenu first
            ? first.GetSelectList().GetSelectedItem()?.Value
            : null);
    }

    [Fact]
    public void SelectingOnANonFinalStepAdvancesWithoutCompleting()
    {
        var completions = new List<IReadOnlyDictionary<string, string>>();
        var submenu = Create(onComplete: completions.Add);

        submenu.HandleInput("\r"); // confirm "openai" on step 1

        Assert.Empty(completions);
        var output = RenderText(submenu, 100);
        Assert.Contains("Step 2/2 \u00b7 Provider is openai", output, StringComparison.Ordinal);
        // The second step's title is computed from the context.
        Assert.Contains("Model for openai", output, StringComparison.Ordinal);
        Assert.Contains("gpt", output, StringComparison.Ordinal);
        Assert.DoesNotContain("claude", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFinalStepDeliversASnapshotOfTheContext()
    {
        IReadOnlyDictionary<string, string>? result = null;
        var submenu = Create(onComplete: context => result = context);

        submenu.HandleInput("\r"); // openai
        submenu.HandleInput("\r"); // gpt

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Equal("openai", result["provider"]);
        Assert.Equal("gpt", result["model"]);
    }

    [Fact]
    public void CompletingWithoutLoopCancels()
    {
        var cancelled = false;
        var submenu = Create(onCancel: () => cancelled = true);

        submenu.HandleInput("\r");
        submenu.HandleInput("\r");

        Assert.True(cancelled);
    }

    [Fact]
    public void EscapeOnALaterStepGoesBackAndDropsThatKey()
    {
        var cancelled = false;
        var submenu = Create(onCancel: () => cancelled = true);

        submenu.HandleInput("\r"); // to step 2
        Assert.Equal("openai", submenu.Context["provider"]);

        submenu.HandleInput("\x1b"); // back to step 1

        Assert.False(cancelled);
        Assert.Contains("Step 1/2", RenderText(submenu, 100), StringComparison.Ordinal);
        Assert.False(submenu.Context.ContainsKey("model"));
        Assert.Equal("openai", submenu.Context["provider"]);
    }

    [Fact]
    public void EscapeOnTheFirstStepCancels()
    {
        var cancelled = false;
        var submenu = Create(onCancel: () => cancelled = true);

        submenu.HandleInput("\x1b");

        Assert.True(cancelled);
    }

    [Fact]
    public void LoopCompletesThenRestartsAtStepZeroWithAnEmptyContext()
    {
        var completions = new List<IReadOnlyDictionary<string, string>>();
        var cancelled = false;
        var submenu = Create(
            onComplete: completions.Add,
            onCancel: () => cancelled = true,
            opts: new SteppedSubmenuOptions { Loop = true });

        submenu.HandleInput("\r");
        submenu.HandleInput("\r");

        var completion = Assert.Single(completions);
        Assert.Equal("gpt", completion["model"]);
        Assert.False(cancelled);
        Assert.Empty(submenu.Context);
        Assert.Contains("Step 1/2", RenderText(submenu, 100), StringComparison.Ordinal);
    }

    [Fact]
    public void StartAtStepSkipsEarlierStepsAndUsesTheInitialContext()
    {
        var submenu = Create(opts: new SteppedSubmenuOptions
        {
            StartAtStep = 1,
            InitialContext = new Dictionary<string, string> { ["provider"] = "anthropic" },
        });

        var output = RenderText(submenu, 100);
        Assert.Contains("Step 2/2", output, StringComparison.Ordinal);
        Assert.Contains("Model for anthropic", output, StringComparison.Ordinal);
        Assert.Contains("claude", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderIsExactlyTheActiveStepAndInvalidateIsForwarded()
    {
        var submenu = Create();
        var active = submenu.ActiveComponent;

        Assert.Equal(active.Render(100), submenu.Render(100));

        submenu.Invalidate(); // must not throw and must reach the active step
        Assert.Equal(active.Render(100), submenu.Render(100));
    }

    [Fact]
    public void SearchableStepGetsTheSearchHint()
    {
        var submenu = Create([FirstStep(searchable: true), SecondStep()]);

        Assert.Contains("Type to filter", RenderText(submenu, 100), StringComparison.Ordinal);
    }
}
