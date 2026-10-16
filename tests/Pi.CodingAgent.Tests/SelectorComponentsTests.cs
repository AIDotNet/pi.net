// ============================================================================
// Tests for modes/interactive/components selectors
// (theme-selector / show-images-selector / trust-selector / thinking-selector /
//  user-message-selector)
// Vectors transcribed from TS test/trust-selector.test.ts,
// test/thinking-selector.test.ts, plus the behaviour locked by the component
// sources themselves.
// ============================================================================

using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

// `KeybindingsManager` exists in both Pi.Tui (the TUI table) and Pi.CodingAgent.Core (the app
// table). The app one is the type the TS tests construct, so it gets the bare name via an alias.
using CoreKeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Shared fixture for the selector tests. Mirrors the TS tests' setup pair
/// (<c>initTheme("dark")</c> + <c>setKeybindings(new KeybindingsManager())</c>) and the
/// three-part theme fixture documented in the porting notes: forced chalk, an explicit colour
/// mode, and both theme caches reset.
/// </summary>
public abstract class SelectorTestFixture : IDisposable
{
    private readonly bool? _previousChalk = Chalk.EnabledOverride;
    private readonly Pi.Tui.KeybindingsManager _previousKeybindings = GlobalKeybindings.Get();

    protected SelectorTestFixture()
    {
        ThemeRuntime.ResetCachesForTests();
        Chalk.EnabledOverride = true;
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
        GlobalKeybindings.Set(new CoreKeybindingsManager());
    }

    public void Dispose()
    {
        GlobalKeybindings.Set(_previousKeybindings);
        Chalk.EnabledOverride = _previousChalk;
        ThemeRuntime.ResetCachesForTests();
        GC.SuppressFinalize(this);
    }

    protected static string RenderText(object component, int width) =>
        AnsiText.StripAnsi(string.Join("\n", ((IComponent)component).Render(width)));

    protected static string[] RenderLines(object component, int width) =>
        [.. ((IComponent)component).Render(width).Select(AnsiText.StripAnsi)];

    /// <summary>The one row a selector marks as selected (the row carrying the <c>→</c> prefix).</summary>
    protected static string SelectedRow(object component, int width) =>
        RenderLines(component, width)
            .Select(line => line.TrimEnd())
            .First(line => line.TrimStart().StartsWith('→'));
}

// ---------------------------------------------------------------- theme-selector

public sealed class ThemeSelectorComponentTests : SelectorTestFixture
{
    [Fact]
    public void PreselectsTheCurrentThemeAndMarksItAsCurrent()
    {
        var themes = ThemeRuntime.GetAvailableThemes();
        Assert.True(themes.Count >= 2, "expected at least two available themes");
        var current = themes[1];

        var selector = new ThemeSelectorComponent(current, _ => { }, () => { }, _ => { });

        var selected = selector.GetSelectList().GetSelectedItem();
        Assert.NotNull(selected);
        Assert.Equal(current, selected!.Value);
        Assert.Equal("(current)", selected.Description);
    }

    [Fact]
    public void FallsBackToTheFirstRowWhenTheCurrentThemeIsNotAvailable()
    {
        var selector = new ThemeSelectorComponent("not-a-real-theme", _ => { }, () => { }, _ => { });

        // `themes.indexOf` misses (-1) and the TS guard leaves the selection at index 0.
        Assert.Equal(ThemeRuntime.GetAvailableThemes()[0], selector.GetSelectList().GetSelectedItem()?.Value);
    }

    [Fact]
    public void MovingTheSelectionPreviewsTheHighlightedTheme()
    {
        var themes = ThemeRuntime.GetAvailableThemes();
        var previewed = new List<string>();

        var selector = new ThemeSelectorComponent(themes[0], _ => { }, () => { }, previewed.Add);

        selector.GetSelectList().HandleInput("\x1b[B");

        Assert.Equal(new[] { themes[1] }, previewed);
    }

    [Fact]
    public void ConfirmingSelectsAndCancellingCancels()
    {
        var themes = ThemeRuntime.GetAvailableThemes();
        string? selected = null;
        var cancelled = false;

        var selector = new ThemeSelectorComponent(
            themes[0],
            name => selected = name,
            () => cancelled = true,
            _ => { });

        selector.GetSelectList().HandleInput("\r");
        Assert.Equal(themes[0], selected);
        Assert.False(cancelled);

        selector.GetSelectList().HandleInput("\x1b");
        Assert.True(cancelled);
    }

    [Fact]
    public void WrapsBackToTheTopFromTheFirstRow()
    {
        var themes = ThemeRuntime.GetAvailableThemes();
        var previewed = new List<string>();

        var selector = new ThemeSelectorComponent(themes[0], _ => { }, () => { }, previewed.Add);
        selector.GetSelectList().HandleInput("\x1b[A");

        Assert.Equal(new[] { themes[^1] }, previewed);
    }
}

// ------------------------------------------------------- show-images-selector

public sealed class ShowImagesSelectorComponentTests : SelectorTestFixture
{
    [Fact]
    public void PreselectsYesWhenImagesAreCurrentlyShown()
    {
        var selector = new ShowImagesSelectorComponent(true, _ => { }, () => { });

        Assert.Equal("yes", selector.GetSelectList().GetSelectedItem()?.Value);
        var output = RenderText(selector, 80);
        Assert.Contains("Yes", output, StringComparison.Ordinal);
        Assert.Contains("Show images inline in terminal", output, StringComparison.Ordinal);
        Assert.Contains("No", output, StringComparison.Ordinal);
        Assert.Contains("Show text placeholder instead", output, StringComparison.Ordinal);
    }

    [Fact]
    public void PreselectsNoWhenImagesAreCurrentlyHidden()
    {
        var selector = new ShowImagesSelectorComponent(false, _ => { }, () => { });

        Assert.Equal("no", selector.GetSelectList().GetSelectedItem()?.Value);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ConfirmingReportsTheChosenValueAsABoolean(bool current, bool expected)
    {
        bool? reported = null;
        var selector = new ShowImagesSelectorComponent(current, show => reported = show, () => { });

        selector.GetSelectList().HandleInput("\r");

        Assert.Equal(expected, reported);
    }

    [Fact]
    public void CancellingRunsTheCancelCallback()
    {
        var cancelled = false;
        var selector = new ShowImagesSelectorComponent(true, _ => { }, () => cancelled = true);

        selector.GetSelectList().HandleInput("\x1b");

        Assert.True(cancelled);
    }
}

// ------------------------------------------------------------- trust-selector

public sealed class TrustSelectorComponentTests : SelectorTestFixture
{
    private static readonly string TrustRoot = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "pi-trust-selector-tests"));

    private static string Cwd(params string[] segments) =>
        Path.GetFullPath(Path.Combine([TrustRoot, .. segments]));

    private static TrustSelectorComponent Create(
        string cwd,
        ProjectTrustStoreEntry? savedDecision,
        bool projectTrusted,
        Action<TrustSelection>? onSelect = null,
        Action? onCancel = null) =>
        new(new TrustSelectorOptions
        {
            Cwd = cwd,
            SavedDecision = savedDecision,
            ProjectTrusted = projectTrusted,
            OnSelect = onSelect ?? (_ => { }),
            OnCancel = onCancel ?? (() => { }),
        });

    [Fact]
    public void KeepsTheSavedTrustedDecisionMarkedWhileBrowsing()
    {
        var cwd = Cwd("project");
        var selector = Create(cwd, new ProjectTrustStoreEntry(cwd, true), projectTrusted: true);

        var output = RenderText(selector, 120);
        Assert.Contains($"Saved decision: trusted ({cwd})", output, StringComparison.Ordinal);
        Assert.Contains("Current session: trusted", output, StringComparison.Ordinal);
        Assert.Contains("→ ✓ Trust", output, StringComparison.Ordinal);

        selector.HandleInput("\x1b[B");

        output = RenderText(selector, 120);
        Assert.Contains("✓ Trust", output, StringComparison.Ordinal);
        Assert.Contains($"→   Trust parent folder ({TrustRoot})", output, StringComparison.Ordinal);
        Assert.DoesNotContain("✓ Do not trust", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectsATrustDecision()
    {
        var cwd = Cwd("project");
        TrustSelection? selection = null;
        var selector = Create(
            cwd,
            savedDecision: null,
            projectTrusted: false,
            onSelect: value => selection = value);

        selector.HandleInput("\n");

        Assert.NotNull(selection);
        Assert.True(selection.Trusted);
        var update = Assert.Single(selection.Updates);
        Assert.Equal(cwd, update.Path);
        Assert.True(update.Decision);
    }

    [Fact]
    public void LabelsSavedAncestorDecisionsAsInherited()
    {
        var parent = Cwd("parent");
        var cwd = Cwd("parent", "project", "nested");

        var selector = Create(cwd, new ProjectTrustStoreEntry(parent, true), projectTrusted: true);

        var output = RenderText(selector, 120);
        Assert.Contains($"Saved decision: trusted (inherited from {parent})", output, StringComparison.Ordinal);
        Assert.Contains("Current session: trusted", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AddsATrustParentOption()
    {
        var parent = Cwd("parent");
        var cwd = Cwd("parent", "project");
        TrustSelection? selection = null;

        var selector = Create(
            cwd,
            new ProjectTrustStoreEntry(parent, true),
            projectTrusted: true,
            onSelect: value => selection = value);

        var output = RenderText(selector, 120);
        Assert.Contains($"Saved decision: trusted (inherited from {parent})", output, StringComparison.Ordinal);
        Assert.Contains($"✓ Trust parent folder ({parent})", output, StringComparison.Ordinal);

        // The saved ancestor row is preselected, so confirming picks it.
        selector.HandleInput("\n");

        Assert.NotNull(selection);
        Assert.True(selection.Trusted);
        Assert.Equal(2, selection.Updates.Count);
        Assert.Equal(parent, selection.Updates[0].Path);
        Assert.True(selection.Updates[0].Decision);
        Assert.Equal(cwd, selection.Updates[1].Path);
        Assert.Null(selection.Updates[1].Decision);
    }

    [Fact]
    public void UntrustedSessionIsReportedAsUntrusted()
    {
        var cwd = Cwd("project");
        var selector = Create(cwd, savedDecision: null, projectTrusted: false);

        var output = RenderText(selector, 120);
        Assert.Contains("Saved decision: none", output, StringComparison.Ordinal);
        Assert.Contains("Current session: untrusted", output, StringComparison.Ordinal);
    }

    [Fact]
    public void JAndKNavigateLikeTheArrowKeys()
    {
        var cwd = Cwd("parent", "project");
        TrustSelection? selection = null;
        var selector = Create(
            cwd,
            savedDecision: null,
            projectTrusted: false,
            onSelect: value => selection = value);

        // `j` moves down to the parent-folder row, `k` moves back up to `Trust`.
        selector.HandleInput("j");
        Assert.Equal($" →   Trust parent folder ({Cwd("parent")})", SelectedRow(selector, 120));

        selector.HandleInput("k");
        Assert.Equal(" →   Trust", SelectedRow(selector, 120));

        selector.HandleInput("\n");
        Assert.NotNull(selection);
        Assert.Equal(cwd, Assert.Single(selection.Updates).Path);
    }

    [Fact]
    public void NavigationIsClampedAtBothEnds()
    {
        var cwd = Cwd("project");
        var selector = Create(cwd, savedDecision: null, projectTrusted: false);

        // Up from the first row stays put (no wrap-around here, unlike SelectList).
        selector.HandleInput("\x1b[A");
        Assert.Equal(" →   Trust", SelectedRow(selector, 120));

        // Down past the last row stays on `Do not trust`.
        for (var i = 0; i < 10; i++)
        {
            selector.HandleInput("\x1b[B");
        }

        Assert.Equal(" →   Do not trust", SelectedRow(selector, 120));
    }

    [Fact]
    public void EscapeCancels()
    {
        var cwd = Cwd("project");
        var cancelled = false;
        var selector = Create(cwd, savedDecision: null, projectTrusted: false, onCancel: () => cancelled = true);

        selector.HandleInput("\x1b");

        Assert.True(cancelled);
    }

    [Fact]
    public void RendersTheNavigationHintLine()
    {
        var cwd = Cwd("project");
        var selector = Create(cwd, savedDecision: null, projectTrusted: false);

        var output = RenderText(selector, 120);
        Assert.Contains("Project trust", output, StringComparison.Ordinal);
        Assert.Contains("↑↓", output, StringComparison.Ordinal);
        Assert.Contains("navigate", output, StringComparison.Ordinal);
        Assert.Contains("save", output, StringComparison.Ordinal);
        Assert.Contains("cancel", output, StringComparison.Ordinal);
    }
}

// ---------------------------------------------------------- thinking-selector

public sealed class ThinkingSelectorComponentTests : SelectorTestFixture
{
    private static ThinkingSelectorComponent Create(
        ThinkingLevel current,
        IReadOnlyList<ThinkingLevel> available,
        Action<ThinkingLevel>? onSelect = null,
        Action? onCancel = null,
        Action<ThinkingLevel>? onSelectAsDefault = null,
        ThinkingLevel? defaultLevel = null) =>
        new(current, available, onSelect ?? (_ => { }), onCancel ?? (() => { }), onSelectAsDefault, defaultLevel);

    private static string? LevelRow(ThinkingSelectorComponent selector, string level) =>
        selector.GetSelectList().Render(80)
            .Select(AnsiText.StripAnsi)
            .FirstOrDefault(line => line.Contains(level, StringComparison.Ordinal));

    [Fact]
    public void KeepsTheCurrentThinkingLevelMarkedWhileBrowsing()
    {
        var selector = Create(ThinkingLevel.Medium, [ThinkingLevel.Medium, ThinkingLevel.High]);

        Assert.Equal("✓ medium", selector.GetSelectList().GetSelectedItem()?.Label);
        Assert.StartsWith("→ ✓ medium", LevelRow(selector, "medium"), StringComparison.Ordinal);

        selector.HandleInput("\x1b[B");

        Assert.StartsWith("  ✓ medium", LevelRow(selector, "medium"), StringComparison.Ordinal);
        Assert.StartsWith("→   high", LevelRow(selector, "high"), StringComparison.Ordinal);
    }

    [Fact]
    public void UsesTheConfiguredSaveBinding()
    {
        GlobalKeybindings.Set(new CoreKeybindingsManager(
            new KeybindingsConfig { [AppKeybindings.AppThinkingSave] = "ctrl+r" }));

        var saved = new List<ThinkingLevel>();
        var selector = Create(
            ThinkingLevel.Medium,
            [ThinkingLevel.Medium, ThinkingLevel.High],
            onSelectAsDefault: saved.Add);

        Assert.Contains("Ctrl+R to set as default", RenderText(selector, 80), StringComparison.Ordinal);

        selector.HandleInput("\x13");
        Assert.Empty(saved);

        selector.HandleInput("\x12");
        Assert.Equal(new[] { ThinkingLevel.Medium }, saved);
    }

    [Fact]
    public void TheDefaultSaveBindingIsCtrlS()
    {
        var saved = new List<ThinkingLevel>();
        var selector = Create(
            ThinkingLevel.High,
            [ThinkingLevel.Medium, ThinkingLevel.High],
            onSelectAsDefault: saved.Add);

        Assert.Contains("Ctrl+S to set as default", RenderText(selector, 80), StringComparison.Ordinal);

        selector.HandleInput("\x13");

        Assert.Equal(new[] { ThinkingLevel.High }, saved);
    }

    [Fact]
    public void SaveBindingWithoutACallbackIsIgnored()
    {
        var selected = new List<ThinkingLevel>();
        var selector = Create(ThinkingLevel.Medium, [ThinkingLevel.Medium, ThinkingLevel.High], onSelect: selected.Add);

        selector.HandleInput("\x13");

        Assert.Empty(selected);
        Assert.Equal("✓ medium", selector.GetSelectList().GetSelectedItem()?.Label);
    }

    [Fact]
    public void NavigationIsForwardedToTheSelectList()
    {
        var selected = new List<ThinkingLevel>();
        var selector = Create(ThinkingLevel.Medium, [ThinkingLevel.Medium, ThinkingLevel.High], onSelect: selected.Add);

        selector.HandleInput("\x1b[B");
        selector.HandleInput("\r");

        Assert.Equal(new[] { ThinkingLevel.High }, selected);
    }

    [Fact]
    public void CancelIsForwardedToTheSelectList()
    {
        var cancelled = false;
        var selector = Create(
            ThinkingLevel.Medium,
            [ThinkingLevel.Medium, ThinkingLevel.High],
            onCancel: () => cancelled = true);

        selector.HandleInput("\x1b");

        Assert.True(cancelled);
    }

    [Fact]
    public void TypingFiltersTheListFuzzily()
    {
        var selector = Create(
            ThinkingLevel.Medium,
            [ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High]);

        // "hi" is a subsequence of "high Deep reasoning (~16k tokens)" (the h and i are adjacent)
        // and of "low Light reasoning (~2k tokens)" (the h of "Light", the i of "reasoning").
        // "off No reasoning" and "medium Moderate reasoning" contain no h at all.
        selector.HandleInput("h");
        selector.HandleInput("i");

        Assert.Equal(2, selector.GetSelectList().Render(80).Length);

        // The highlighted level fell out of the filtered set, so the cursor resets to the best
        // match: "high" wins on the word-boundary and consecutive-run bonuses.
        Assert.Equal("high", selector.GetSelectList().GetSelectedItem()?.Value);
    }

    [Fact]
    public void ClearingTheSearchRestoresEveryLevel()
    {
        var selector = Create(
            ThinkingLevel.Medium,
            [ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High]);

        selector.HandleInput("h");
        selector.HandleInput("i");
        Assert.Equal(2, selector.GetSelectList().Render(80).Length);

        selector.HandleInput("\x7f"); // backspace
        selector.HandleInput("\x7f");

        // The filter is empty again: every level is back and the previously highlighted one is
        // reselected. Only the *current* level carries the ✓ marker, not the highlighted one.
        Assert.Equal(4, selector.GetSelectList().Render(80).Length);
        Assert.Equal("high", selector.GetSelectList().GetSelectedItem()?.Value);
        Assert.Equal("  high", selector.GetSelectList().GetSelectedItem()?.Label);
    }

    [Fact]
    public void MarksTheDefaultLevelInItsDescription()
    {
        var selector = Create(
            ThinkingLevel.Medium,
            [ThinkingLevel.Medium, ThinkingLevel.High],
            defaultLevel: ThinkingLevel.High);

        var mediumRow = LevelRow(selector, "medium");
        Assert.NotNull(mediumRow);
        Assert.Contains("Moderate reasoning (~8k tokens)", mediumRow, StringComparison.Ordinal);
        Assert.DoesNotContain("· default", mediumRow, StringComparison.Ordinal);

        var highRow = LevelRow(selector, "high");
        Assert.NotNull(highRow);
        Assert.Contains("Deep reasoning (~16k tokens) · default", highRow, StringComparison.Ordinal);
    }

    [Fact]
    public void RendersTheHeaderAndHintLine()
    {
        var selector = Create(ThinkingLevel.Medium, [ThinkingLevel.Medium, ThinkingLevel.High]);

        var output = RenderText(selector, 80);
        Assert.Contains("Thinking Level", output, StringComparison.Ordinal);
        Assert.Contains("Shift+Tab cycles thinking levels in-session", output, StringComparison.Ordinal);
        Assert.Contains("Enter to select", output, StringComparison.Ordinal);
        Assert.Contains("Escape/Ctrl+C to cancel", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FocusIsForwardedToTheSearchInput()
    {
        var selector = Create(ThinkingLevel.Medium, [ThinkingLevel.Medium, ThinkingLevel.High]);

        Assert.False(selector.Focused);
        selector.Focused = true;
        Assert.True(selector.Focused);
    }

    [Fact]
    public void SubmittingTheSearchInputConfirmsTheHighlightedLevel()
    {
        var selected = new List<ThinkingLevel>();
        var selector = Create(ThinkingLevel.Medium, [ThinkingLevel.Medium, ThinkingLevel.High], onSelect: selected.Add);

        // Enter is routed to the select list by `handleInput`, not to the search input.
        selector.HandleInput("\r");

        Assert.Equal(new[] { ThinkingLevel.Medium }, selected);
    }
}

// ----------------------------------------------------- user-message-selector

public sealed class UserMessageSelectorComponentTests : SelectorTestFixture
{
    private static readonly UserMessageItem[] TwoMessages =
    [
        new("entry-1", "first question"),
        new("entry-2", "second question\nwith a line break"),
    ];

    [Fact]
    public void SelectsTheMostRecentMessageByDefault()
    {
        var selector = new UserMessageSelectorComponent(TwoMessages, _ => { }, () => { });

        Assert.Equal(1, selector.GetMessageList().SelectedIndex);

        var lines = RenderLines(selector, 120);
        Assert.StartsWith("› second question with a line break", lines.First(l => l.StartsWith('›')), StringComparison.Ordinal);
    }

    [Fact]
    public void HonoursAnInitialSelection()
    {
        var selector = new UserMessageSelectorComponent(TwoMessages, _ => { }, () => { }, initialSelectedId: "entry-1");

        Assert.Equal(0, selector.GetMessageList().SelectedIndex);
    }

    [Fact]
    public void AnUnknownInitialSelectionFallsBackToTheMostRecentMessage()
    {
        var selector = new UserMessageSelectorComponent(TwoMessages, _ => { }, () => { }, initialSelectedId: "nope");

        Assert.Equal(1, selector.GetMessageList().SelectedIndex);
    }

    [Fact]
    public void RendersTheHeaderAndMetadata()
    {
        var selector = new UserMessageSelectorComponent(TwoMessages, _ => { }, () => { });

        var output = RenderText(selector, 120);
        Assert.Contains("Fork from Message", output, StringComparison.Ordinal);
        Assert.Contains(
            "Select a user message to copy the active path up to that point into a new session",
            output,
            StringComparison.Ordinal);
        Assert.Contains("Message 1 of 2", output, StringComparison.Ordinal);
        Assert.Contains("Message 2 of 2", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationWrapsInBothDirections()
    {
        var selector = new UserMessageSelectorComponent(TwoMessages, _ => { }, () => { });
        var list = selector.GetMessageList();

        list.HandleInput("\x1b[B"); // down from the last row wraps to the first
        Assert.Equal(0, list.SelectedIndex);

        list.HandleInput("\x1b[A"); // up from the first row wraps to the last
        Assert.Equal(1, list.SelectedIndex);
    }

    [Fact]
    public void ConfirmingReportsTheSelectedEntryId()
    {
        string? selected = null;
        var selector = new UserMessageSelectorComponent(TwoMessages, id => selected = id, () => { });

        selector.GetMessageList().HandleInput("\x1b[A");
        selector.GetMessageList().HandleInput("\r");

        Assert.Equal("entry-1", selected);
    }

    [Fact]
    public void CancellingRunsTheCancelCallback()
    {
        var cancelled = false;
        var selector = new UserMessageSelectorComponent(TwoMessages, _ => { }, () => cancelled = true);

        selector.GetMessageList().HandleInput("\x1b");

        Assert.True(cancelled);
    }

    [Fact]
    public void AnEmptyListRendersAPlaceholder()
    {
        var selector = new UserMessageSelectorComponent([], _ => { }, () => { });

        Assert.Contains("No user messages found", RenderText(selector, 120), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyListAutoCancels()
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = new UserMessageSelectorComponent([], _ => { }, () => cancelled.TrySetResult(true));

        var completed = await Task.WhenAny(cancelled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(completed == cancelled.Task, "onCancel should run shortly after construction");
    }

    [Fact]
    public async Task ANonEmptyListDoesNotAutoCancel()
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = new UserMessageSelectorComponent(TwoMessages, _ => { }, () => cancelled.TrySetResult(true));

        var completed = await Task.WhenAny(cancelled.Task, Task.Delay(TimeSpan.FromMilliseconds(400)));
        Assert.False(completed == cancelled.Task, "onCancel must not run when there is something to pick");
    }

    [Fact]
    public void TruncatesLongMessagesAndShowsTheScrollIndicator()
    {
        var messages = Enumerable.Range(1, 12)
            .Select(i => new UserMessageItem($"entry-{i}", new string('x', 300)))
            .ToArray();

        var selector = new UserMessageSelectorComponent(messages, _ => { }, () => { });

        var lines = RenderLines(selector, 40);
        Assert.All(lines.Where(l => l.StartsWith('›')), line => Assert.True(line.Length <= 40));

        var output = string.Join("\n", lines);
        Assert.Contains("/12)", output, StringComparison.Ordinal);
    }
}
