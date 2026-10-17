// ============================================================================
// Tests for the status-indicator helpers of interactive-mode.ts (4f-4g).
//
// Upstream has no test file for these. The vectors are derived from the TS bodies: the duck-typed
// isWorkingStatusEditor guard, the working-status colour fallback chain, and the idle-placeholder
// rule inside clearStatusIndicator.
// ============================================================================

using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

// The app keybindings manager is what TS's `core/keybindings.ts` exports.
using KeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

namespace Pi.CodingAgent.Tests;

public sealed class InteractiveModeStatusTests : SelectorTestFixture
{
    private static CustomEditor Editor(bool embedWorkingStatus) =>
        new(
            new FakeTui(),
            ThemeHelpers.GetEditorTheme(),
            new KeybindingsManager(),
            new CustomEditorOptions { EmbedWorkingStatus = embedWorkingStatus });

    // ---------------------------------------------------------------- working-status editor

    [Fact]
    public void AnEditorThatEmbedsTheWorkingStatusIsRecognised()
    {
        Assert.True(InteractiveModeStatus.IsWorkingStatusEditor(Editor(embedWorkingStatus: true)));
    }

    [Fact]
    public void AnEditorThatDoesNotEmbedItIsNot()
    {
        Assert.False(InteractiveModeStatus.IsWorkingStatusEditor(Editor(embedWorkingStatus: false)));
    }

    [Fact]
    public void AnythingThatIsNotAnEditorIsNot()
    {
        Assert.False(InteractiveModeStatus.IsWorkingStatusEditor(null));
        Assert.False(InteractiveModeStatus.IsWorkingStatusEditor("editor"));
    }

    // ---------------------------------------------------------------- working colour

    [Fact]
    public void AStandaloneIndicatorHasNoColourFunction()
    {
        // TS leaves colorFn undefined, so WorkingStatusIndicator keeps its accent/muted defaults.
        Assert.Null(InteractiveModeStatus.ResolveWorkingStatusColor(
            Editor(embedWorkingStatus: false),
            ThemeRuntime.Current,
            ThinkingLevel.Off));
        Assert.Null(InteractiveModeStatus.ResolveWorkingStatusColor(null, ThemeRuntime.Current, ThinkingLevel.Off));
    }

    [Fact]
    public void AnEmbeddedIndicatorFollowsTheThinkingLevelWhenTheEditorHasNoBorderColour()
    {
        var theme = ThemeRuntime.Current;
        var editor = Editor(embedWorkingStatus: true);
        // C266: C#'s Editor.BorderColor is a non-nullable property that defaults to the theme's
        // border colour, whereas TS's `borderColor?:` starts out undefined. The `??` fallback is
        // therefore unreachable on the normal path here; null it out to exercise the branch.
        editor.BorderColor = null!;

        var color = InteractiveModeStatus.ResolveWorkingStatusColor(editor, theme, ThinkingLevel.High);

        Assert.NotNull(color);
        Assert.Equal(theme.GetThinkingBorderColor(ThinkingLevel.High)("x"), color!("x"));
    }

    [Fact]
    public void TheEditorBorderColourIsUsedAsIs()
    {
        // The normal C# path: BorderColor is always set, so it wins over the thinking level.
        var theme = ThemeRuntime.Current;
        var editor = Editor(embedWorkingStatus: true);

        var color = InteractiveModeStatus.ResolveWorkingStatusColor(editor, theme, ThinkingLevel.High);

        Assert.NotNull(color);
        Assert.Equal(editor.BorderColor("x"), color!("x"));
    }

    [Fact]
    public void AnExplicitBorderColourWinsOverTheThinkingLevel()
    {
        var editor = Editor(embedWorkingStatus: true);
        editor.BorderColor = text => $"<{text}>";

        var color = InteractiveModeStatus.ResolveWorkingStatusColor(editor, ThemeRuntime.Current, ThinkingLevel.Max);

        Assert.Equal("<x>", color!("x"));
    }

    // ---------------------------------------------------------------- labels and messages

    [Fact]
    public void TheWorkingMessageFallsBackToTheDefault()
    {
        Assert.Equal("Working", InteractiveModeStatus.ResolveWorkingMessage(null, "Working"));
        Assert.Equal("Thinking", InteractiveModeStatus.ResolveWorkingMessage("Thinking", "Working"));
    }

    [Fact]
    public void AnEmptyWorkingMessageIsKeptBecauseOnlyNullFallsBack()
    {
        // TS `??` is null-only, so "" survives.
        Assert.Equal("", InteractiveModeStatus.ResolveWorkingMessage("", "Working"));
    }

    [Fact]
    public void TheHiddenThinkingLabelFallsBackToTheDefault()
    {
        Assert.Equal("Thinking...", InteractiveModeStatus.ResolveHiddenThinkingLabel(null, "Thinking..."));
        Assert.Equal("Pondering", InteractiveModeStatus.ResolveHiddenThinkingLabel("Pondering", "Thinking..."));
    }

    [Fact]
    public void TheResetWorkingMessageAppendsTheInterruptHint()
    {
        Assert.Equal(
            $"Working ({KeybindingHints.KeyText("app.interrupt")} to interrupt)",
            InteractiveModeStatus.DefaultWorkingMessageWithInterrupt("Working"));
    }

    // ---------------------------------------------------------------- idle placeholder

    [Theory]
    [InlineData(true, false, true, true, true)]    // the only combination that restores the placeholder
    [InlineData(false, false, true, true, false)]  // nothing was cleared
    [InlineData(true, true, true, true, false)]    // it was embedded in the editor, so no gap to fill
    [InlineData(true, false, false, true, false)]  // fullscreen keeps its own layout
    [InlineData(true, false, true, false, false)]  // clear-on-shrink off means the row is not blanked
    public void TheIdlePlaceholderIsRestoredOnlyInTheNarrowCase(
        bool hadIndicator,
        bool wasEmbedded,
        bool isRegularTuiMode,
        bool clearOnShrink,
        bool expected)
    {
        Assert.Equal(
            expected,
            InteractiveModeStatus.ShouldRestoreIdleStatus(hadIndicator, wasEmbedded, isRegularTuiMode, clearOnShrink));
    }
}
