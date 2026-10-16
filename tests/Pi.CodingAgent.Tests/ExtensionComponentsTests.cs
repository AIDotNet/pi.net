// ============================================================================
// Tests for modes/interactive/components extension-facing components
// (extension-selector / extension-input / radius-login-selector / first-time-setup)
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

// `Text` exists in both Pi.Tui.Components (the TUI primitive) and Pi.CodingAgent.Utils (a text
// helper), so the TUI one gets an alias here.
using TuiText = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Minimal <see cref="ITui"/> stub that only records render requests, so the countdown and shimmer
/// timers stay deterministic (a real <c>TuiMainScreen</c> would schedule its own render timer).
/// </summary>
internal sealed class FakeTui : ITui
{
    public int RenderRequests { get; private set; }

    public TuiMode Mode => TuiMode.Regular;

    public List<IComponent> Children { get; } = [];

    public ITerminal Terminal { get; } = new StringTerminal();

    public Action? OnDebug { get; set; }

    public int FullRedraws => 0;

    public void AddChild(IComponent component) => Children.Add(component);

    public void RemoveChild(IComponent component) => Children.Remove(component);

    public void Clear() => Children.Clear();

    public bool GetShowHardwareCursor() => false;

    public void SetShowHardwareCursor(bool enabled)
    {
    }

    public bool GetClearOnShrink() => false;

    public void SetClearOnShrink(bool enabled)
    {
    }

    public void SetFocus(IComponent? component)
    {
    }

    public IOverlayHandle ShowOverlay(IComponent component, OverlayOptions? options = null) => null!;

    public void HideOverlay()
    {
    }

    public bool HasOverlay() => false;

    public void Start()
    {
    }

    public void Stop(TuiStopOptions? options = null)
    {
    }

    public void RenderNow(bool force = false)
    {
    }

    public void RequestRender(bool force = false) => RenderRequests++;

    public Action AddInputListener(Func<string, TuiInputListenerResult?> listener) => () => { };

    public void RemoveInputListener(Func<string, TuiInputListenerResult?> listener)
    {
    }

    public Action OnTerminalColorSchemeChange(Action<TerminalColorScheme> listener) => () => { };

    public void SetTerminalColorSchemeNotifications(bool enabled)
    {
    }

    public Task<TerminalColorsSnapshot> QueryTerminalColorsAsync(TerminalColorQueryOptions options) =>
        Task.FromResult(new TerminalColorsSnapshot());

    public Task<RgbColor?> QueryTerminalBackgroundColorAsync(int timeoutMs) => Task.FromResult<RgbColor?>(null);

    public Task<TerminalColorScheme?> QueryTerminalColorSchemeAsync(int timeoutMs) =>
        Task.FromResult<TerminalColorScheme?>(null);

    public string[] Render(int width) => [];

    public void HandleInput(string data)
    {
    }

    public void Invalidate()
    {
    }
}

// ------------------------------------------------------- extension-selector

public sealed class ExtensionSelectorComponentTests : SelectorTestFixture
{
    private static readonly string[] Options = ["alpha", "beta", "gamma"];

    private static ExtensionSelectorComponent Create(
        string title = "Pick one",
        IReadOnlyList<string>? options = null,
        Action<string>? onSelect = null,
        Action? onCancel = null,
        ExtensionSelectorOptions? opts = null) =>
        new(title, options ?? Options, onSelect ?? (_ => { }), onCancel ?? (() => { }), opts);

    [Fact]
    public void RendersTheTitleOptionsAndHintLine()
    {
        var output = RenderText(Create(), 120);

        Assert.Contains("Pick one", output, StringComparison.Ordinal);
        Assert.Contains("alpha", output, StringComparison.Ordinal);
        Assert.Contains("beta", output, StringComparison.Ordinal);
        Assert.Contains("gamma", output, StringComparison.Ordinal);
        // `keyHint` renders the raw key text (lowercase); `keyDisplayText` is the capitalised one.
        Assert.Contains("↑↓ navigate", output, StringComparison.Ordinal);
        Assert.Contains("enter select", output, StringComparison.Ordinal);
        Assert.Contains("escape/ctrl+c cancel", output, StringComparison.Ordinal);
    }

    [Fact]
    public void PreselectsTheFirstOption()
    {
        var selector = Create();

        Assert.Equal(0, selector.SelectedIndex);
        Assert.Equal(" → alpha", SelectedRow(selector, 120));
    }

    [Fact]
    public void DescriptionIsRenderedOnlyWhenNonEmpty()
    {
        Assert.Contains(
            "Extra context",
            RenderText(Create(opts: new ExtensionSelectorOptions { Description = "Extra context" }), 120),
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Extra context",
            RenderText(Create(opts: new ExtensionSelectorOptions { Description = "" }), 120),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationClampsAtBothEnds()
    {
        var selector = Create();

        selector.HandleInput("\x1b[A");
        Assert.Equal(" → alpha", SelectedRow(selector, 120));

        for (var i = 0; i < 5; i++)
        {
            selector.HandleInput("\x1b[B");
        }

        Assert.Equal(" → gamma", SelectedRow(selector, 120));
        Assert.Equal(2, selector.SelectedIndex);
    }

    [Fact]
    public void JAndKNavigateLikeTheArrowKeys()
    {
        var selector = Create();

        selector.HandleInput("j");
        Assert.Equal(1, selector.SelectedIndex);

        selector.HandleInput("k");
        Assert.Equal(0, selector.SelectedIndex);
    }

    [Fact]
    public void ConfirmSelectsTheHighlightedOption()
    {
        var selected = new List<string>();
        var selector = Create(onSelect: selected.Add);

        selector.HandleInput("\x1b[B");
        selector.HandleInput("\n");

        Assert.Equal(new[] { "beta" }, selected);
    }

    [Fact]
    public void EscapeCancels()
    {
        var cancelled = false;
        var selector = Create(onCancel: () => cancelled = true);

        selector.HandleInput("\x1b");

        Assert.True(cancelled);
    }

    [Fact]
    public void TheToolsExpandBindingFiresWithoutMovingTheSelection()
    {
        var toggles = 0;
        var selector = Create(opts: new ExtensionSelectorOptions { OnToggleToolsExpanded = () => toggles++ });

        selector.HandleInput("\x0f"); // ctrl+o = app.tools.expand

        Assert.Equal(1, toggles);
        Assert.Equal(0, selector.SelectedIndex);
    }

    [Fact]
    public void TheToolsExpandBindingIsIgnoredWhenNoCallbackIsInstalled()
    {
        var selector = Create();

        selector.HandleInput("\x0f");

        Assert.Equal(0, selector.SelectedIndex);
    }

    [Fact]
    public void AnEmptyOptionListHasNothingToConfirm()
    {
        var selected = new List<string>();
        var selector = Create(options: [], onSelect: selected.Add);

        selector.HandleInput("\n");
        selector.HandleInput("\x1b[B");

        Assert.Empty(selected);
    }

    [Fact]
    public async Task TheTimeoutTicksTheTitleAndCancelsOnExpiry()
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selector = Create(
            title: "Pick one",
            onCancel: () => cancelled.TrySetResult(true),
            opts: new ExtensionSelectorOptions { Tui = new FakeTui(), Timeout = 1000 });

        // The first tick is synchronous, exactly as in TS.
        Assert.Contains("Pick one (1s)", RenderText(selector, 120), StringComparison.Ordinal);

        var completed = await Task.WhenAny(cancelled.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == cancelled.Task, "the countdown should expire and cancel");
        selector.Dispose();
    }

    [Fact]
    public void DisposeWithoutATimeoutIsHarmless()
    {
        var selector = Create();

        selector.Dispose();
        selector.Dispose();
    }
}

// ---------------------------------------------------------- extension-input

public sealed class ExtensionInputComponentTests : SelectorTestFixture
{
    private static ExtensionInputComponent Create(
        string title = "Your name",
        Action<string>? onSubmit = null,
        Action? onCancel = null,
        ExtensionInputOptions? opts = null) =>
        new(title, "type here", onSubmit ?? (_ => { }), onCancel ?? (() => { }), opts);

    [Fact]
    public void RendersTheTitleInputAndHintLine()
    {
        var output = RenderText(Create(), 120);

        Assert.Contains("Your name", output, StringComparison.Ordinal);
        Assert.Contains("enter submit", output, StringComparison.Ordinal);
        Assert.Contains("escape/ctrl+c cancel", output, StringComparison.Ordinal);
        // TS takes a `placeholder` argument and never uses it.
        Assert.DoesNotContain("type here", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptionIsRenderedOnlyWhenNonEmpty()
    {
        Assert.Contains(
            "Say hello",
            RenderText(Create(opts: new ExtensionInputOptions { Description = "Say hello" }), 120),
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "Say hello",
            RenderText(Create(opts: new ExtensionInputOptions { Description = "" }), 120),
            StringComparison.Ordinal);
    }

    [Fact]
    public void InitialValueIsPreseeded()
    {
        var submitted = new List<string>();
        var selector = Create(
            onSubmit: submitted.Add,
            opts: new ExtensionInputOptions { InitialValue = "preset" });

        selector.HandleInput("\n");

        Assert.Equal(new[] { "preset" }, submitted);
    }

    [Fact]
    public void AnEmptyInitialValueLeavesTheInputBlank()
    {
        var submitted = new List<string>();
        var selector = Create(
            onSubmit: submitted.Add,
            opts: new ExtensionInputOptions { InitialValue = "" });

        selector.HandleInput("\n");

        Assert.Equal(new[] { "" }, submitted);
    }

    [Fact]
    public void ConfirmSubmitsWhatWasTyped()
    {
        var submitted = new List<string>();
        var selector = Create(onSubmit: submitted.Add);

        selector.HandleInput("a");
        selector.HandleInput("b");
        selector.HandleInput("c");
        selector.HandleInput("\n");

        Assert.Equal(new[] { "abc" }, submitted);
    }

    [Fact]
    public void EscapeCancels()
    {
        var cancelled = false;
        var selector = Create(onCancel: () => cancelled = true);

        selector.HandleInput("\x1b");

        Assert.True(cancelled);
    }

    [Fact]
    public void FocusIsForwardedToTheInput()
    {
        var selector = Create();

        Assert.False(selector.Focused);
        selector.Focused = true;
        Assert.True(selector.Focused);
    }

    [Fact]
    public async Task TheTimeoutTicksTheTitleAndCancelsOnExpiry()
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selector = Create(
            title: "Your name",
            onCancel: () => cancelled.TrySetResult(true),
            opts: new ExtensionInputOptions { Tui = new FakeTui(), Timeout = 1000 });

        Assert.Contains("Your name (1s)", RenderText(selector, 120), StringComparison.Ordinal);

        var completed = await Task.WhenAny(cancelled.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == cancelled.Task, "the countdown should expire and cancel");
        selector.Dispose();
    }

    [Fact]
    public void DisposeWithoutATimeoutIsHarmless()
    {
        var selector = Create();

        selector.Dispose();
        selector.Dispose();
    }
}

// ----------------------------------------------------- radius-login-selector

public sealed class RadiusLoginMenuComponentTests : SelectorTestFixture
{
    private static readonly RadiusOption SignIn =
        new("Sign in with Radius (browser)", "Sign in with Radius");

    private static readonly string[] Options =
        ["Sign in with Radius (browser)", "Use an API key", "Cancel"];

    private static RadiusLoginMenuComponent Create(
        Action<string>? onSelect = null,
        Action? onCancel = null,
        FakeTui? tui = null) =>
        new(
            tui ?? new FakeTui(),
            "Sign in",
            Options,
            SignIn,
            onSelect ?? (_ => { }),
            onCancel ?? (() => { }));

    [Fact]
    public void ShimmerColoursEveryRuneAndResetsTheForeground()
    {
        var shimmer = RadiusLoginMenuComponent.RadiusShimmer("Radius", 0);

        Assert.Equal("Radius", AnsiText.StripAnsi(shimmer));
        // One escape per rune plus the trailing SGR-39 reset.
        Assert.Equal(7, shimmer.Split("\x1b[").Length - 1);
        Assert.EndsWith("\x1b[39m", shimmer, StringComparison.Ordinal);
    }

    [Fact]
    public void ShimmerAdvancesWithTime()
    {
        Assert.NotEqual(
            RadiusLoginMenuComponent.RadiusShimmer("Radius", 0),
            RadiusLoginMenuComponent.RadiusShimmer("Radius", 100));
    }

    [Fact]
    public void ShimmerWrapsAtTheEndOfTheColourCycle()
    {
        // 4 colours × 4 chars = 16 chars; at 10 chars/second that is exactly 1,600 ms.
        Assert.Equal(
            RadiusLoginMenuComponent.RadiusShimmer("Radius", 0),
            RadiusLoginMenuComponent.RadiusShimmer("Radius", 1600));
    }

    [Fact]
    public void ShimmerIsEmptyForEmptyText()
    {
        Assert.Equal("\x1b[39m", RadiusLoginMenuComponent.RadiusShimmer("", 0));
    }

    [Fact]
    public void TheSelectedRadiusRowIsAnimated()
    {
        var theme = ThemeRuntime.Current;
        var selector = Create();

        var lines = selector.Render(120);
        var index = Array.FindIndex(
            lines,
            line => AnsiText.StripAnsi(line).TrimEnd() == $" → {SignIn.Label}");
        Assert.True(index >= 0, "the selected Radius row should be rendered");

        // Same visible text as the selector's own row, but with the shimmer's colour escapes.
        var plain = new TuiText(theme.Fg("accent", "→ ") + theme.Fg("accent", SignIn.Label), 1, 0)
            .Render(120)[0];
        Assert.NotEqual(plain, lines[index]);
    }

    [Fact]
    public void TheRadiusRowIsNotAnimatedWhileAnotherOptionIsSelected()
    {
        var selector = Create();
        selector.HandleInput("\x1b[B"); // move the selection to "Use an API key"

        var expected = new TuiText($"  {ThemeRuntime.Current.Fg("text", SignIn.Label)}", 1, 0).Render(120)[0];

        Assert.Contains(expected, selector.Render(120));
    }

    [Fact]
    public async Task ConfirmingStopsTheRenderRequests()
    {
        var tui = new FakeTui();
        var selected = new List<string>();
        var selector = Create(onSelect: selected.Add, tui: tui);

        selector.Render(120); // sets `animating`
        await Task.Delay(300);
        Assert.True(tui.RenderRequests > 0, "the shimmer should request renders while it is animating");

        selector.HandleInput("\n");
        await Task.Delay(150); // let any in-flight tick drain
        var afterConfirm = tui.RenderRequests;
        await Task.Delay(300);

        Assert.Equal(new[] { "Sign in with Radius (browser)" }, selected);
        Assert.Equal(afterConfirm, tui.RenderRequests);
    }

    [Fact]
    public async Task DisposingStopsTheRenderRequests()
    {
        var tui = new FakeTui();
        var selector = Create(tui: tui);

        selector.Render(120);
        await Task.Delay(300);
        Assert.True(tui.RenderRequests > 0);

        selector.Dispose();
        await Task.Delay(150); // let any in-flight tick drain
        var afterDispose = tui.RenderRequests;
        await Task.Delay(300);

        Assert.Equal(afterDispose, tui.RenderRequests);
    }

    [Fact]
    public void TheFactoryReturnsTheAnimatedSelector()
    {
        var selector = RadiusLoginSelector.CreateLoginMenuSelector(
            new FakeTui(),
            "Sign in",
            Options,
            SignIn,
            _ => { },
            () => { });

        Assert.IsType<RadiusLoginMenuComponent>(selector);
    }

    [Fact]
    public void CancellingReportsCancel()
    {
        var tui = new FakeTui();
        var cancelled = false;
        var selector = Create(onCancel: () => cancelled = true, tui: tui);

        selector.Render(120);
        selector.HandleInput("\x1b");

        Assert.True(cancelled);
        selector.Dispose();
    }
}

// ------------------------------------------------------- first-time-setup

public sealed class FirstTimeSetupComponentTests : SelectorTestFixture
{
    private sealed record Recorder
    {
        public List<string> Previews { get; } = [];

        public List<FirstTimeSetupResult> Submissions { get; } = [];

        public int Cancels { get; private set; }

        public FirstTimeSetupOptions Options => new()
        {
            OnThemePreview = Previews.Add,
            OnSubmit = Submissions.Add,
            OnCancel = () => Cancels++,
        };
    }

    [Fact]
    public void RendersTheWelcomeAndTheThemeStep()
    {
        var component = new FirstTimeSetupComponent(new Recorder().Options);

        var output = RenderText(component, 120);
        Assert.Contains("██████", output, StringComparison.Ordinal);
        Assert.Contains(
            $"Welcome to {Config.AppName}, the minimal coding agent.",
            output,
            StringComparison.Ordinal);
        Assert.Contains("Pick a theme.", output, StringComparison.Ordinal);
        Assert.Contains("System (matches your terminal colors)", output, StringComparison.Ordinal);
        Assert.Contains("Dark", output, StringComparison.Ordinal);
        Assert.Contains("Light", output, StringComparison.Ordinal);
        Assert.Contains("enter continue", output, StringComparison.Ordinal);
        Assert.Contains("escape/ctrl+c skip setup", output, StringComparison.Ordinal);
        Assert.Equal("theme", component.StepName);
    }

    [Fact]
    public void ConfirmAdvancesToTheAnalyticsStep()
    {
        var recorder = new Recorder();
        var component = new FirstTimeSetupComponent(recorder.Options);

        component.HandleInput("\n");

        Assert.Equal("analytics", component.StepName);
        Assert.Empty(recorder.Submissions);

        var output = RenderText(component, 120);
        Assert.Contains("Opt-in to anonymous usage data sharing?", output, StringComparison.Ordinal);
        Assert.Contains("Share anonymous usage data", output, StringComparison.Ordinal);
        Assert.Contains("Don't share", output, StringComparison.Ordinal);
        Assert.Contains("enter finish", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Pick a theme.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnalyticsStepSubmitsTheChoices()
    {
        var recorder = new Recorder();
        var component = new FirstTimeSetupComponent(recorder.Options);

        component.HandleInput("\n");   // theme -> analytics
        component.HandleInput("\x1b[B"); // opt out
        component.HandleInput("\n");

        var result = Assert.Single(recorder.Submissions);
        Assert.Equal("system", result.Theme);
        Assert.False(result.ShareAnalytics);
        Assert.Equal(1, component.AnalyticsIndex);
    }

    [Fact]
    public void TheThemeStepPreviewsOnEveryChangeOnly()
    {
        var recorder = new Recorder();
        var component = new FirstTimeSetupComponent(recorder.Options);

        component.HandleInput("\x1b[B");
        Assert.Equal(new[] { "dark" }, recorder.Previews);
        Assert.Equal(1, component.ThemeIndex);

        component.HandleInput("\x1b[B");
        Assert.Equal(new[] { "dark", "light" }, recorder.Previews);
        Assert.Equal(2, component.ThemeIndex);

        // Clamped at the end: no further preview.
        component.HandleInput("\x1b[B");
        Assert.Equal(new[] { "dark", "light" }, recorder.Previews);
        Assert.Equal(2, component.ThemeIndex);
    }

    [Fact]
    public void JAndKNavigateLikeTheArrowKeys()
    {
        var recorder = new Recorder();
        var component = new FirstTimeSetupComponent(recorder.Options);

        component.HandleInput("j");
        Assert.Equal(new[] { "dark" }, recorder.Previews);

        component.HandleInput("k");
        Assert.Equal(new[] { "dark", "system" }, recorder.Previews);
        Assert.Equal(0, component.ThemeIndex);
    }

    [Fact]
    public void TheAnalyticsStepDoesNotPreview()
    {
        var recorder = new Recorder();
        var component = new FirstTimeSetupComponent(recorder.Options);

        component.HandleInput("\n"); // theme -> analytics
        component.HandleInput("\x1b[B");
        component.HandleInput("\x1b[B");
        component.HandleInput("\x1b[B");

        Assert.Empty(recorder.Previews);
        Assert.Equal(1, component.AnalyticsIndex);
    }

    [Fact]
    public void CancelReportsCancel()
    {
        var recorder = new Recorder();
        var component = new FirstTimeSetupComponent(recorder.Options);

        component.HandleInput("\x1b");

        Assert.Equal(1, recorder.Cancels);
        Assert.Empty(recorder.Submissions);
    }

    [Fact]
    public void InvalidateRebuildsTheDialog()
    {
        var component = new FirstTimeSetupComponent(new Recorder().Options);
        var before = component.Children.Count;

        component.Invalidate();

        Assert.Equal(before, component.Children.Count);
        Assert.Contains("Pick a theme.", RenderText(component, 120), StringComparison.Ordinal);
    }
}
