// ============================================================================
// Tests for modes/interactive/components/scoped-models-selector.ts
//
// All four upstream cases are transcribed; the rest cover the enabled-id algebra and the
// input paths the upstream suite leaves to the interactive-mode integration (4f-4).
// ============================================================================

using Pi.Ai.Models;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class ScopedModelsSelectorTests : SelectorTestFixture
{
    private const string Provider = "acme";

    /// <summary>A model triple the way the upstream harness builds them: id / name / initially enabled.</summary>
    private sealed record ModelState(string Id, string Name, bool Enabled);

    private static ModelSpec Model(ModelState state) => new()
    {
        Id = state.Id,
        Name = state.Name,
        Api = "openai-completions",
        Provider = Provider,
        BaseUrl = "https://example.invalid",
    };

    private static string FullId(ModelState state) => $"{Provider}/{state.Id}";

    /// <summary>A mutable counter, so the callback closures can be observed after <c>Create</c> returns.</summary>
    private sealed class CancelCounter
    {
        public int Count { get; set; }
    }

    private static (
        ScopedModelsSelectorComponent Selector,
        List<IReadOnlyList<string>?> Changes,
        List<IReadOnlyList<string>?> Persists,
        CancelCounter Cancels) Create(ModelState[] models, string? refreshStatus = null)
    {
        var changes = new List<IReadOnlyList<string>?>();
        var persists = new List<IReadOnlyList<string>?>();
        var cancels = new CancelCounter();
        var selector = new ScopedModelsSelectorComponent(
            new ModelsConfig
            {
                AllModels = [.. models.Select(Model)],
                EnabledModelIds = [.. models.Where(m => m.Enabled).Select(FullId)],
                RefreshStatus = refreshStatus,
            },
            new ModelsCallbacks
            {
                OnChange = ids => changes.Add(ids),
                OnPersist = ids => persists.Add(ids),
                OnCancel = () => cancels.Count++,
            });
        return (selector, changes, persists, cancels);
    }

    /// <summary>Upstream's <c>getMarkerStates</c>: the ✓ column of each model's row, in model order.</summary>
    private static bool[] MarkerStates(ScopedModelsSelectorComponent selector, ModelState[] models)
    {
        var lines = RenderLines(selector, 120);
        return [.. models.Select(model =>
        {
            var line = lines.FirstOrDefault(candidate => candidate.Contains($"{model.Id} [", StringComparison.Ordinal));
            Assert.NotNull(line);
            return line![2..].StartsWith("✓ ", StringComparison.Ordinal);
        })];
    }

    /// <summary>
    /// Whether each model is enabled, read back out of the most recent <c>onChange</c> payload
    /// (<c>null</c> = all enabled). The upstream test does the same through its mutable
    /// <c>model.enabled</c> flags.
    /// </summary>
    private static bool[] EnabledStates(ModelState[] models, List<IReadOnlyList<string>?> changes) =>
        [.. models.Select(model => changes.Count == 0
            ? model.Enabled
            : changes[^1] is { } ids ? ids.Contains(FullId(model), StringComparer.Ordinal) : true)];

    /// <summary>The rendered rows (spacer and detail lines excluded), trailing padding trimmed.</summary>
    private static string[] ModelRows(ScopedModelsSelectorComponent selector, int width = 120) =>
        [.. RenderLines(selector, width)
            .Select(line => line.TrimEnd())
            .Where(line => line.Contains(" [", StringComparison.Ordinal))];

    // ------------------------------------------------ upstream: scoped-models-selector.test.ts

    [Fact]
    public void MarksEveryModelAfterEnablingAll()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", false),
            new ModelState("model-c", "Model C", false),
        };
        var (selector, changes, _, _) = Create(models);

        selector.HandleInput("\x01"); // Ctrl+A → app.models.enableAll

        Assert.Equal([true, true, true], EnabledStates(models, changes));
        Assert.Equal([true, true, true], MarkerStates(selector, models));
        Assert.Contains("all enabled", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void DisablesOnlyTheSelectedModelAfterEnablingAll()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", false),
            new ModelState("model-c", "Model C", false),
        };
        var (selector, changes, _, _) = Create(models);

        selector.HandleInput("\x01"); // enable all → null
        selector.HandleInput("\r"); // Enter toggles the selected row (model-a)

        Assert.Equal([false, true, true], EnabledStates(models, changes));
        Assert.Equal([false, true, true], MarkerStates(selector, models));
    }

    [Fact]
    public void EnablesOnlyTheSelectedModelAfterClearingAll()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", true),
            new ModelState("model-c", "Model C", true),
        };
        var (selector, changes, _, _) = Create(models);

        selector.HandleInput("\x18"); // Ctrl+X → app.models.clearAll
        Assert.Equal([false, false, false], EnabledStates(models, changes));
        Assert.Equal([false, false, false], MarkerStates(selector, models));

        selector.HandleInput("\r");
        Assert.Equal([true, false, false], EnabledStates(models, changes));
        Assert.Equal([true, false, false], MarkerStates(selector, models));
    }

    [Fact]
    public void RestoresTheAllEnabledStateAfterReEnablingTheLastDisabledModel()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", false),
            new ModelState("model-c", "Model C", false),
        };
        var (selector, changes, _, _) = Create(models);

        selector.HandleInput("\x01"); // enable all → null
        selector.HandleInput("\r"); // disable model-a; enabled models re-sort first: [b, c, a]
        selector.HandleInput("\x1b[B");
        selector.HandleInput("\x1b[B"); // move selection back to model-a
        selector.HandleInput("\r"); // re-enable model-a

        Assert.Equal([true, true, true], EnabledStates(models, changes));
        Assert.Equal([true, true, true], MarkerStates(selector, models));
        Assert.Contains("all enabled", RenderText(selector, 120), StringComparison.Ordinal);
    }

    // ------------------------------------------------ supplementary: enabled-id algebra

    [Fact]
    public void EnabledIdsKeepTheirStoredOrderAheadOfTheRest()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", true),
            new ModelState("model-c", "Model C", false),
        };
        var (selector, _, _, _) = Create(models);

        selector.HandleInput("\x1b[B"); // select model-b
        selector.HandleInput("\x1bp"); // alt+up reorders model-b above model-a

        // Enabled ids first in their stored order, then the rest in catalog order. The status
        // column is two spaces for a disabled row, so model-c carries four leading spaces.
        Assert.Equal(
            ["→ ✓ model-b [acme]", "  ✓ model-a [acme]", "    model-c [acme]"],
            ModelRows(selector));
    }

    [Fact]
    public void ReorderingPastTheFirstRowIsANoOp()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", true),
        };
        var (selector, changes, _, _) = Create(models);

        // model-a is already first: alt+up cannot move it further, so nothing changes and no
        // change notification fires.
        selector.HandleInput("\x1bp");

        Assert.Empty(changes);
        Assert.DoesNotContain("(unsaved)", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void ReorderingADisabledModelIsIgnored()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", false),
        };
        var (selector, changes, _, _) = Create(models);

        selector.HandleInput("\x1b[B"); // select the disabled model-b
        selector.HandleInput("\x1bn"); // alt+down

        Assert.Empty(changes);
    }

    [Fact]
    public void ReorderingWithoutAnExplicitListIsIgnored()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", true),
        };
        var (selector, changes, _, _) = Create(models);

        selector.HandleInput("\x01"); // enable all → null (no explicit list)
        var changesBeforeReorder = changes.Count;
        selector.HandleInput("\x1bp"); // alt+up

        // The reorder itself is a no-op; the only notification is the enable-all that precedes it.
        Assert.Single(changes);
        Assert.Equal(changesBeforeReorder, changes.Count);
        Assert.Null(changes[0]);
    }

    [Fact]
    public void ReorderingMovesTheSelectionWithTheRow()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", true),
            new ModelState("model-c", "Model C", true),
        };
        var (selector, changes, _, _) = Create(models);

        selector.HandleInput("\x1b[B"); // select model-b
        selector.HandleInput("\x1bn"); // alt+down swaps it with model-c

        Assert.Single(changes);
        Assert.Equal([FullId(models[0]), FullId(models[2]), FullId(models[1])], changes[0]);
        Assert.Contains("(unsaved)", RenderText(selector, 120), StringComparison.Ordinal);

        // The selection followed the row it was on.
        var selected = RenderLines(selector, 120).First(line => line.TrimStart().StartsWith('→'));
        Assert.Contains("model-b", selected, StringComparison.Ordinal);
    }

    [Fact]
    public void TogglingMarksTheSessionDirtyAndSaveClearsIt()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", false),
        };
        var (selector, _, persists, _) = Create(models);

        selector.HandleInput("\r"); // toggle model-a off → [] (nothing enabled)
        Assert.Contains("(unsaved)", RenderText(selector, 120), StringComparison.Ordinal);
        Assert.Contains("0/2 enabled", RenderText(selector, 120), StringComparison.Ordinal);
        Assert.Empty(persists);

        selector.HandleInput("\x13"); // Ctrl+S → app.models.save
        Assert.DoesNotContain("(unsaved)", RenderText(selector, 120), StringComparison.Ordinal);
        Assert.Equal([[]], persists);

        // Saving again with no further edits persists the same list once more.
        selector.HandleInput("\x13");
        Assert.Equal([[], []], persists);
    }

    [Fact]
    public void PersistingAnAllEnabledSelectionReportsNull()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", false),
        };
        var (selector, _, persists, _) = Create(models);

        selector.HandleInput("\x01"); // enable all → null
        selector.HandleInput("\x13"); // save

        Assert.Equal([null], persists);
    }

    [Fact]
    public void ToggleProviderClearsThenRestoresTheWholeProvider()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", false),
            new ModelState("model-c", "Model C", false),
        };
        var (selector, changes, _, _) = Create(models);

        // Only model-a is enabled, so the provider is not fully on: the first press enables the
        // rest of it, which collapses the whole catalog back to null ("all enabled").
        selector.HandleInput("\x10"); // Ctrl+P → app.models.toggleProvider
        Assert.Equal([null], changes);
        Assert.Contains("all enabled", RenderText(selector, 120), StringComparison.Ordinal);

        // The second press sees the provider fully on and clears it.
        selector.HandleInput("\x10");
        Assert.Equal([null, []], changes);
        Assert.Contains("0/3 enabled", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void FooterCountsOnlyModelsStillInTheCatalog()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", false),
        };
        var (selector, _, _, _) = Create(models);

        Assert.Contains("1/2 enabled", RenderText(selector, 120), StringComparison.Ordinal);

        // Drop model-b from the catalog: it stays enabled but is no longer available. The
        // denominator is the catalog size, so it reads 1/1 with one unavailable. Rendered wide
        // enough that the footer hint does not wrap.
        selector.UpdateModels([Model(models[0])], enabledModelIds: [FullId(models[0]), FullId(models[1])]);

        Assert.Contains("1/1 enabled · 1 unavailable", RenderText(selector, 200), StringComparison.Ordinal);
        var lines = RenderLines(selector, 120);
        Assert.Contains(lines, line => line.Contains("model-b", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("[unavailable]", StringComparison.Ordinal));
    }

    // ------------------------------------------------ supplementary: search, navigation, cancel

    [Fact]
    public void SearchFiltersTheListAndReportsNoMatches()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", false),
        };
        var (selector, _, _, _) = Create(models);

        selector.HandleInput("zz");
        Assert.Contains("No matching models", RenderText(selector, 120), StringComparison.Ordinal);

        // Navigation on an empty list is a no-op rather than an out-of-range access.
        selector.HandleInput("\x1b[A");
        selector.HandleInput("\x1b[B");

        selector.HandleInput("\x7f"); // backspace
        selector.HandleInput("\x7f");
        Assert.DoesNotContain("No matching models", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void EnableAllAppliesToTheFilteredSetWhileSearching()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", false),
            new ModelState("model-b", "Model B", false),
            new ModelState("other", "Other", false),
        };
        var (selector, changes, _, _) = Create(models);

        selector.HandleInput("model"); // keeps a and b, drops "other"
        selector.HandleInput("\x01"); // enable all → only the filtered ids

        Assert.Equal([[FullId(models[0]), FullId(models[1])]], changes);
        Assert.Contains("2/3 enabled", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void ClearAllAppliesToTheFilteredSetWhileSearching()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", true),
            new ModelState("other", "Other", true),
        };
        var (selector, changes, _, _) = Create(models);

        selector.HandleInput("model");
        selector.HandleInput("\x18"); // clear all → only the filtered ids are dropped

        Assert.Equal([[FullId(models[2])]], changes);
    }

    [Fact]
    public void CtrlCClearsTheSearchBeforeCancelling()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
        };
        var (selector, _, _, cancels) = Create(models);

        selector.HandleInput("a");
        Assert.Equal("a", selector.GetSearchInput().GetValue());

        selector.HandleInput("\x03"); // Ctrl+C with a non-empty search clears it
        Assert.Equal(0, cancels.Count);
        Assert.Empty(selector.GetSearchInput().GetValue());

        selector.HandleInput("\x03"); // Ctrl+C with an empty search cancels
        Assert.Equal(1, cancels.Count);
    }

    [Fact]
    public void EscapeCancels()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
        };
        var (selector, _, _, cancels) = Create(models);

        selector.HandleInput("\x1b");

        Assert.Equal(1, cancels.Count);
    }

    [Fact]
    public void TypingGoesToTheSearchBoxAndDoesNotToggle()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", true),
        };
        var (selector, changes, _, _) = Create(models);

        selector.HandleInput("b");

        Assert.Empty(changes);
        Assert.Equal("b", selector.GetSearchInput().GetValue());
    }

    [Fact]
    public void UpdateModelsKeepsTheSelectionOnItsModel()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", true),
        };

        // All enabled (null): the list order is the catalog order, so reordering the catalog is
        // what would shift a stale index.
        var selector = new ScopedModelsSelectorComponent(
            new ModelsConfig
            {
                AllModels = [.. models.Select(Model)],
                EnabledModelIds = null,
            },
            new ModelsCallbacks
            {
                OnChange = _ => { },
                OnPersist = _ => { },
                OnCancel = () => { },
            });

        selector.HandleInput("\x1b[B"); // select model-b (index 1)
        selector.UpdateModels([Model(models[1]), Model(models[0])]); // catalog reversed

        // Without the id lookup the selection would stay on index 1 — now model-a.
        var selected = RenderLines(selector, 120).First(line => line.TrimStart().StartsWith('→'));
        Assert.Contains("model-b", selected, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateModelsWithAnExplicitNullResetsToAllEnabled()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", false),
        };
        var (selector, _, _, _) = Create(models);

        selector.HandleInput("\r"); // toggle model-a off → [] (nothing enabled)
        // Rendered wide enough that the footer hint does not wrap.
        Assert.Contains("0/2 enabled", RenderText(selector, 200), StringComparison.Ordinal);

        // The two-argument form with null is "set it to all enabled" — the one-argument form
        // leaves the current selection alone.
        selector.UpdateModels([Model(models[0]), Model(models[1])], enabledModelIds: null);

        Assert.Contains("all enabled", RenderText(selector, 200), StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshStatusIsRenderedWhenProvided()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
        };
        var (selector, _, _, _) = Create(models, refreshStatus: "Refreshed 2m ago");

        Assert.Contains("Refreshed 2m ago", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedModelNameIsShownBelowTheList()
    {
        var models = new[]
        {
            new ModelState("model-a", "Model A", true),
            new ModelState("model-b", "Model B", true),
        };
        var (selector, _, _, _) = Create(models);

        Assert.Contains("Model Name: Model A", RenderText(selector, 120), StringComparison.Ordinal);

        selector.HandleInput("\x1b[B");
        Assert.Contains("Model Name: Model B", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void ScrollInfoAppearsOnlyWhenTheWindowOverflows()
    {
        var models = Enumerable.Range(0, 12)
            .Select(i => new ModelState($"model-{i:D2}", $"Model {i:D2}", true))
            .ToArray();
        var (selector, _, _, _) = Create(models);

        // The first eight rows fill the window and the indicator reports the position.
        Assert.Contains("(1/12)", RenderText(selector, 120), StringComparison.Ordinal);

        // Walk down to row 9: the window slides and the first row scrolls out of view.
        for (var i = 0; i < 8; i++)
        {
            selector.HandleInput("\x1b[B");
        }

        var lines = RenderLines(selector, 120);
        Assert.Contains("(9/12)", string.Join("\n", lines), StringComparison.Ordinal);
        Assert.DoesNotContain(lines, line => line.Contains("model-00", StringComparison.Ordinal));

        // Narrow the selection down to a single row: no overflow, no indicator.
        foreach (var character in "model-05")
        {
            selector.HandleInput(character.ToString());
        }

        Assert.DoesNotContain("(1/1)", RenderText(selector, 120), StringComparison.Ordinal);
    }
}
