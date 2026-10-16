// ============================================================================
// Tests for modes/interactive/components/custom-editor.ts
//
// Three upstream cases live in `status-indicator.test.ts` (the editor is the fixture
// there), and one in `custom-editor-history-keybindings.test.ts`. The rest of
// `status-indicator.test.ts`'s cases exercise the indicators themselves; the two that
// need no editor are transcribed here as well, so the whole file is accounted for.
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;
using Xunit;

// The app keybindings manager is the one TS's `core/keybindings.ts` exports; the TUI's same-named
// base class is what `GlobalKeybindings.Get()` hands out by default.
using KeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

namespace Pi.CodingAgent.Tests;

public sealed class CustomEditorTests : SelectorTestFixture
{
    private static CustomEditor Create(
        bool embedWorkingStatus = false,
        KeybindingsManager? keybindings = null) =>
        new(
            new FakeTui(),
            ThemeHelpers.GetEditorTheme(),
            keybindings ?? new KeybindingsManager(),
            new CustomEditorOptions { EmbedWorkingStatus = embedWorkingStatus });

    // ------------------------------------------------ upstream: custom-editor-history-keybindings

    [Fact]
    public void ExplicitHistoryBindingTakesPrecedenceOverModelCycling()
    {
        var keybindings = new KeybindingsManager(new KeybindingsConfig
        {
            [TuiKeybindingIds.EditorHistoryPrevious] = "ctrl+p",
            [TuiKeybindingIds.EditorHistoryNext] = "ctrl+n",
        });
        GlobalKeybindings.Set(keybindings);
        try
        {
            var editor = Create(keybindings: keybindings);
            var modelCycles = 0;
            editor.OnAction(AppKeybindings.AppModelCycleForward, () => modelCycles++);
            editor.AddToHistory("previous prompt");
            editor.SetText("draft");

            editor.HandleInput("\x10"); // Ctrl+P
            Assert.Equal("previous prompt", editor.GetText());
            Assert.Equal(0, modelCycles);

            editor.HandleInput("\x0e"); // Ctrl+N
            Assert.Equal("draft", editor.GetText());
        }
        finally
        {
            GlobalKeybindings.Set(new KeybindingsManager());
        }
    }

    // ------------------------------------------------ upstream: status-indicator.test.ts

    [Fact]
    public void TopBorderStaysPlainUnlessTheEditorOptsIn()
    {
        var tui = new FakeTui();
        var editor = Create(embedWorkingStatus: false);
        Assert.False(editor.EmbedWorkingStatus);

        var indicator = new WorkingStatusIndicator(tui, "Working");
        editor.SetWorkingStatusIndicator(indicator);
        try
        {
            Assert.Equal(new string('─', 20), RenderLines(editor, 20)[0]);
        }
        finally
        {
            indicator.Dispose();
        }
    }

    [Fact]
    public void EmbedsTheWorkingIndicatorWhenTheEditorOptsIn()
    {
        var tui = new FakeTui();
        var editor = Create(embedWorkingStatus: true);
        Assert.True(editor.EmbedWorkingStatus);

        editor.BorderColor = text => ThemeRuntime.Current.Fg(ThemeColors.ThinkingHigh, text);
        var indicator = new WorkingStatusIndicator(tui, "Working", null, text => editor.BorderColor(text));
        editor.SetWorkingStatusIndicator(indicator);
        try
        {
            var topBorder = RenderRaw(editor, 20)[0];
            Assert.Equal("── ⠋ Working ───────", AnsiText.StripAnsi(topBorder));
            Assert.Equal(20, UnicodeWidth.VisibleWidth(topBorder));

            // The border is painted with the thinking-high colour, not the editor theme's own
            // (muted) border colour — the first "── " segment proves it.
            var thinkingBorder = ThemeRuntime.Current.Fg(ThemeColors.ThinkingHigh, "── ");
            Assert.Contains(thinkingBorder, topBorder, StringComparison.Ordinal);
        }
        finally
        {
            indicator.Dispose();
        }
    }

    [Fact]
    public void EmbedsCompactionSummaryAndRetryLabelsWithinTheBorderWidth()
    {
        var tui = new FakeTui();
        var editor = Create(embedWorkingStatus: true);
        var indicators = new IDisposable[]
        {
            new CompactionStatusIndicator(tui, CompactionStatusReason.Manual),
            new CompactionStatusIndicator(tui, CompactionStatusReason.Threshold),
            new CompactionStatusIndicator(tui, CompactionStatusReason.Overflow),
            new BranchSummaryStatusIndicator(tui),
            new RetryStatusIndicator(tui, 1, 3, 3000),
        };
        try
        {
            foreach (var disposable in indicators)
            {
                var indicator = (StatusIndicator)disposable;
                editor.SetWorkingStatusIndicator(indicator);
                var label = AnsiText.StripAnsi(RenderLines(indicator, 120)[1]).Trim();
                Assert.Contains($"── {label} ", AnsiText.StripAnsi(RenderRaw(editor, 120)[0]), StringComparison.Ordinal);
                foreach (var width in new[] { 1, 4, 10, 20, 80, 120 })
                {
                    Assert.Equal(width, UnicodeWidth.VisibleWidth(RenderRaw(editor, width)[0]));
                }
            }

            // The countdown re-renders once a second, so the label must follow.
            System.Threading.Thread.Sleep(1100);
            Assert.Contains(
                "Retrying (1/3) in 2s",
                AnsiText.StripAnsi(RenderRaw(editor, 120)[0]),
                StringComparison.Ordinal);

            editor.SetWorkingStatusIndicator(null);
            Assert.Equal(new string('─', 120), RenderLines(editor, 120)[0]);
        }
        finally
        {
            foreach (var disposable in indicators)
            {
                disposable.Dispose();
            }
        }
    }

    [Fact]
    public void IdleStatusKeepsTheSameHeightAsStandaloneIndicators()
    {
        var idle = new IdleStatus();

        var lines = RenderLines(idle, 20);

        Assert.Equal(2, lines.Length);
        Assert.Equal([new string(' ', 20), new string(' ', 20)], lines);
    }

    [Fact]
    public void RetryCountdownStopsAfterDispose()
    {
        var tui = new FakeTui();
        var indicator = new RetryStatusIndicator(tui, 1, 3, 1000);
        var callsBeforeDispose = tui.RenderRequests;

        indicator.Dispose();
        System.Threading.Thread.Sleep(1500);

        Assert.Equal(callsBeforeDispose, tui.RenderRequests);
    }

    // ------------------------------------------------ supplementary: input precedence

    [Fact]
    public void ExtensionShortcutRunsBeforeEverythingElse()
    {
        var editor = Create();
        var handled = new List<string>();
        editor.OnExtensionShortcut = data =>
        {
            handled.Add(data);
            return data == "x";
        };
        var interrupted = false;
        editor.OnAction(AppKeybindings.AppInterrupt, () => interrupted = true);

        editor.HandleInput("x");
        editor.HandleInput("y");

        Assert.Equal(["x", "y"], handled);
        Assert.False(interrupted);
    }

    [Fact]
    public void ExtensionShortcutThatReturnsFalseDoesNotSwallowTheKey()
    {
        var editor = Create();
        editor.OnExtensionShortcut = _ => false;

        editor.HandleInput("z");

        // The editor itself draws no prompt (a wrapper component does in production); what matters is
        // that the character reached the text buffer instead of being swallowed.
        Assert.Equal("z", editor.GetText());
        Assert.Contains("z", RenderText(editor, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void PasteImageKeybindingIsHandledByTheEditor()
    {
        var keybindings = new KeybindingsManager(new KeybindingsConfig
        {
            [AppKeybindings.AppClipboardPasteImage] = "v",
        });
        GlobalKeybindings.Set(keybindings);
        try
        {
            var editor = Create(keybindings: keybindings);
            var pasted = 0;
            editor.OnPasteImage = () => pasted++;

            editor.HandleInput("v");

            Assert.Equal(1, pasted);
        }
        finally
        {
            GlobalKeybindings.Set(new KeybindingsManager());
        }
    }

    [Fact]
    public void InterruptRunsTheDynamicEscapeHandlerWhenAutocompleteIsInactive()
    {
        var editor = Create();
        var dynamicEscapes = 0;
        var registeredEscapes = 0;
        editor.OnEscape = () => dynamicEscapes++;
        editor.OnAction(AppKeybindings.AppInterrupt, () => registeredEscapes++);

        editor.HandleInput("\x1b");

        Assert.Equal(1, dynamicEscapes);
        Assert.Equal(0, registeredEscapes);
    }

    [Fact]
    public void InterruptFallsBackToTheRegisteredHandlerWhenNoDynamicEscapeHandler()
    {
        var editor = Create();
        var registeredEscapes = 0;
        editor.OnAction(AppKeybindings.AppInterrupt, () => registeredEscapes++);

        editor.HandleInput("\x1b");

        Assert.Equal(1, registeredEscapes);
    }

    [Fact]
    public void InterruptWithNoHandlerFallsThroughToTheBaseEditor()
    {
        var editor = Create();

        // The base editor treats a lone escape as "nothing to cancel" and leaves the text alone,
        // but the key must not be swallowed: typing it again is still observable.
        editor.SetText("draft");
        editor.HandleInput("\x1b");

        Assert.Equal("draft", editor.GetText());
    }

    [Fact]
    public async Task InterruptWithAutocompleteActiveFallsThroughToTheBaseEditor()
    {
        var editor = Create();
        var dynamicEscapes = 0;
        editor.OnEscape = () => dynamicEscapes++;

        // A "/" at the start of the message is the one character that auto-triggers the menu, and the
        // provider always offers a suggestion. The menu opens one microtask later (deviation T25),
        // so the state has to be awaited rather than asserted inline.
        editor.SetAutocompleteProvider(new AlwaysSuggestProvider());
        editor.HandleInput("/");

        await WaitUntil(() => editor.IsShowingAutocomplete());
        Assert.True(editor.IsShowingAutocomplete());

        editor.HandleInput("\x1b");

        Assert.Equal(0, dynamicEscapes);
        Assert.False(editor.IsShowingAutocomplete());
    }

    [Fact]
    public void ExitOnlyFiresWhenTheEditorIsEmpty()
    {
        var editor = Create();
        var exits = 0;
        editor.OnCtrlD = () => exits++;
        editor.OnAction(AppKeybindings.AppExit, () => exits += 10);

        // Ctrl+D on a non-empty editor is delete-char-forward, not an exit. The cursor sits at the
        // end after SetText, so move it to the line start first for the deletion to be observable.
        editor.SetText("draft");
        editor.HandleInput("\x01"); // Ctrl+A → line start
        editor.HandleInput("\x04"); // delete-char-forward removes "d"
        Assert.Equal(0, exits);
        Assert.Equal("raft", editor.GetText());

        editor.SetText("x");
        editor.HandleInput("\x04"); // still non-empty → no exit
        Assert.Equal(0, exits);
        Assert.Equal("x", editor.GetText());

        editor.HandleInput("\x7f"); // backspace, back to empty
        editor.HandleInput("\x04");
        Assert.Equal(1, exits); // `onCtrlD ?? actionHandlers.get(...)` — one handler, never both

        // Dropping the dynamic handler falls back to the registered one.
        editor.OnCtrlD = null;
        editor.HandleInput("\x04");
        Assert.Equal(11, exits);
    }

    [Fact]
    public void ExitFallsBackToTheRegisteredHandlerWhenNoDynamicCtrlDHandler()
    {
        var editor = Create();
        var exits = 0;
        editor.OnAction(AppKeybindings.AppExit, () => exits++);

        editor.HandleInput("\x04");

        Assert.Equal(1, exits);
    }

    [Fact]
    public void RegisteredAppActionsAreDispatched()
    {
        var editor = Create();
        var toggled = 0;
        editor.OnAction(AppKeybindings.AppToolsExpand, () => toggled++);

        editor.HandleInput("\x0f"); // Ctrl+O

        Assert.Equal(1, toggled);
        Assert.DoesNotContain("> ", RenderText(editor, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void UnhandledKeysFallThroughToTheEditor()
    {
        var editor = Create();
        var toggled = 0;
        editor.OnAction(AppKeybindings.AppToolsExpand, () => toggled++);

        editor.HandleInput("a");
        editor.HandleInput("b");

        Assert.Equal(0, toggled);
        Assert.Equal("ab", editor.GetText());
    }

    // ------------------------------------------------ supplementary: border layout

    [Fact]
    public void BorderHidesTheStatusAndShowsTheOverflowLabelWhenLinesAreHidden()
    {
        var tui = new FakeTui();
        var editor = Create(embedWorkingStatus: true);
        var indicator = new WorkingStatusIndicator(tui, "Compacting the conversation");
        editor.SetWorkingStatusIndicator(indicator);
        try
        {
            // Wrap the editor's own text so the first line scrolls out of view.
            editor.SetText(string.Join("\n", Enumerable.Repeat(new string('x', 40), 6)));

            // The label is too wide to sit next to the overflow marker, so the border drops it and
            // keeps only the spinner (`renderSpinnerInBorder`), then re-checks the fit with the
            // narrower status — which now succeeds.
            var topBorder = RenderRaw(editor, 40)[0];
            Assert.Contains("↑ ", AnsiText.StripAnsi(topBorder), StringComparison.Ordinal);
            Assert.DoesNotContain("Compacting", AnsiText.StripAnsi(topBorder), StringComparison.Ordinal);
            Assert.Equal(40, UnicodeWidth.VisibleWidth(topBorder));
        }
        finally
        {
            indicator.Dispose();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void NarrowBordersStayWithinTheRequestedWidth(int width)
    {
        var tui = new FakeTui();
        var editor = Create(embedWorkingStatus: true);
        var indicator = new WorkingStatusIndicator(tui, "Working");
        editor.SetWorkingStatusIndicator(indicator);
        try
        {
            var topBorder = RenderRaw(editor, width)[0];

            Assert.Equal(width, UnicodeWidth.VisibleWidth(topBorder));
        }
        finally
        {
            indicator.Dispose();
        }
    }

    [Fact]
    public void ZeroWidthStatusRendersThePlainBorder()
    {
        var tui = new FakeTui();
        var editor = Create(embedWorkingStatus: true);

        // A single empty frame renders verbatim, so the status collapses to zero visible width and
        // the border falls back to the plain one (the `statusWidth === 0` early return).
        var indicator = new WorkingStatusIndicator(tui, "", new WorkingIndicatorOptions { Frames = [""] });
        editor.SetWorkingStatusIndicator(indicator);

        Assert.Equal(new string('─', 20), RenderLines(editor, 20)[0]);
    }

    // ------------------------------------------------ helpers

    private static string[] RenderRaw(IComponent component, int width) => component.Render(width);

    /// <summary>An autocomplete provider that always suggests, so <c>isShowingAutocomplete()</c> is true.</summary>
    private sealed class AlwaysSuggestProvider : IAutocompleteProvider
    {
        public IReadOnlyList<string>? TriggerCharacters => null;

        public Task<AutocompleteSuggestions?> GetSuggestionsAsync(
            string[] lines, int cursorLine, int cursorCol, AutocompleteRequest options) =>
            Task.FromResult<AutocompleteSuggestions?>(new AutocompleteSuggestions
            {
                Prefix = "/",
                Items = [new AutocompleteItem { Value = "help", Label = "help" }],
            });

        public CompletionApplication ApplyCompletion(
            string[] lines, int cursorLine, int cursorCol, AutocompleteItem item, string prefix) =>
            new CompletionApplication(lines, cursorLine, cursorCol);

        public bool ShouldTriggerFileCompletion(string[] lines, int cursorLine, int cursorCol) => false;
    }

    /// <summary>Spins until <paramref name="condition"/> holds, giving microtask hops time to land.</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }
}
