// ============================================================================
// Tests for modes/interactive/components/login-dialog.ts
// Vectors transcribed from TS
// test/suite/regressions/5433-extension-oauth-prompt-input.test.ts, plus the
// panel behaviour the TS suite leaves uncovered (device code, info links,
// waiting/progress lines, cancel semantics, copy interception, focus).
// ============================================================================

using Pi.Ai.Auth;
using Pi.Ai.Compat;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

using CoreKeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Restores the static seams this file touches. <see cref="OpenBrowser.OpenOverride"/> stands in for
/// the TS test's <c>vi.mock("../../../src/utils/open-browser.ts")</c>;
/// <see cref="Clipboard.NativeClipboardOverride"/> keeps <c>app.message.copy</c> from touching the
/// real system clipboard.
/// </summary>
internal sealed class LoginDialogSeams : IDisposable
{
    private readonly Action<string>? _previousOpen = OpenBrowser.OpenOverride;
    private readonly Func<INativeClipboard?>? _previousClipboard = Clipboard.NativeClipboardOverride;

    public List<string> Opened { get; } = [];

    public LoginDialogSeams()
    {
        OpenBrowser.OpenOverride = Opened.Add;
        Clipboard.NativeClipboardOverride = () => new RecordingNativeClipboard();
    }

    public void Dispose()
    {
        OpenBrowser.OpenOverride = _previousOpen;
        Clipboard.NativeClipboardOverride = _previousClipboard;
    }
}

internal sealed class RecordingNativeClipboard : INativeClipboard
{
    public List<string> Written { get; } = [];

    public Task<NativeClipboardText?> GetTextAsync() => Task.FromResult<NativeClipboardText?>(null);

    public Task<byte[]?> GetImageAsync() => Task.FromResult<byte[]?>(null);

    public Task<string[]?> GetFilePathsAsync() => Task.FromResult<string[]?>(null);

    public Task SetTextAsync(string text)
    {
        Written.Add(text);
        return Task.CompletedTask;
    }
}

public sealed class LoginDialogComponentTests : SelectorTestFixture
{
    private static LoginDialogComponent CreateDialog(
        string providerId = "prompt-repro",
        Action<bool, string?>? onComplete = null,
        string? providerNameOverride = null,
        string? titleOverride = null) =>
        new(new FakeTui(), providerId, onComplete ?? ((_, _) => { }), providerNameOverride, titleOverride);

    /// <summary>TS <c>renderDialog</c>: strip ANSI, trim trailing whitespace on every line.</summary>
    private static string[] RenderDialog(LoginDialogComponent dialog) =>
        [.. RenderLines(dialog, 120).Select(line => line.TrimEnd())];

    private static int CountRenderedValue(string[] lines, string value) =>
        lines.Count(line => line.Trim() == $"> {value}");

    // ------------------------------------------------- OAuth prompt regressions

    [Fact]
    public async Task KeepsPreviousPromptInputStableWhenALaterPromptIsActive()
    {
        var dialog = CreateDialog();

        var firstPrompt = dialog.ShowPromptAsync("First prompt:", "first-value");
        dialog.HandleInput("first-value");
        dialog.HandleInput("\n");
        Assert.Equal("first-value", await firstPrompt);

        var secondPrompt = dialog.ShowPromptAsync("Second prompt:");
        dialog.HandleInput("second-secret-demo");

        var lines = RenderDialog(dialog);
        var joined = string.Join("\n", lines);
        Assert.Contains("First prompt:", joined, StringComparison.Ordinal);
        Assert.Contains("Second prompt:", joined, StringComparison.Ordinal);
        Assert.Equal(1, CountRenderedValue(lines, "first-value"));
        Assert.Equal(1, CountRenderedValue(lines, "second-secret-demo"));

        dialog.HandleInput("\n");
        Assert.Equal("second-secret-demo", await secondPrompt);
    }

    [Fact]
    public void PreservesAuthInstructionsWhenShowingAPrompt()
    {
        using var seams = new LoginDialogSeams();
        var dialog = CreateDialog();

        dialog.ShowAuth("https://example.invalid/login", "Authorize the extension");
        _ = dialog.ShowPromptAsync("First prompt:");

        var output = RenderText(dialog, 120);
        Assert.Contains("https://example.invalid/login", output, StringComparison.Ordinal);
        Assert.Contains("Authorize the extension", output, StringComparison.Ordinal);
        Assert.Contains("First prompt:", output, StringComparison.Ordinal);
        Assert.Equal(["https://example.invalid/login"], seams.Opened);
    }

    [Fact]
    public void PreservesNeutralInformationAndLinksWhenShowingAPrompt()
    {
        var dialog = CreateDialog();

        dialog.ShowInfo(
            "Configure credentials outside pi.",
            [new AuthInfoLink("https://example.invalid/docs", "Provider documentation")]);
        _ = dialog.ShowPromptAsync("Press Enter to continue:");

        var output = RenderText(dialog, 120);
        Assert.Contains("Configure credentials outside pi.", output, StringComparison.Ordinal);
        Assert.Contains(
            "Provider documentation: https://example.invalid/docs", output, StringComparison.Ordinal);
        Assert.Contains("Press Enter to continue:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesSetupDetailsWhenShowingAPrompt()
    {
        var dialog = CreateDialog();

        dialog.ShowDetails(["AWS credential setup:", "providers.md"]);
        _ = dialog.ShowPromptAsync("Enter API key:");

        var output = RenderText(dialog, 120);
        Assert.Contains("AWS credential setup:", output, StringComparison.Ordinal);
        Assert.Contains("providers.md", output, StringComparison.Ordinal);
        Assert.Contains("Enter API key:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepsPreviousManualInputStableWhenALaterPromptIsActive()
    {
        var dialog = CreateDialog();

        var manualInput = dialog.ShowManualInputAsync("Paste callback URL:");
        dialog.HandleInput("callback-value");
        dialog.HandleInput("\n");
        Assert.Equal("callback-value", await manualInput);

        var prompt = dialog.ShowPromptAsync("Second prompt:");
        dialog.HandleInput("second-secret-demo");

        var lines = RenderDialog(dialog);
        var joined = string.Join("\n", lines);
        Assert.Contains("Paste callback URL:", joined, StringComparison.Ordinal);
        Assert.Contains("Second prompt:", joined, StringComparison.Ordinal);
        Assert.Equal(1, CountRenderedValue(lines, "callback-value"));
        Assert.Equal(1, CountRenderedValue(lines, "second-secret-demo"));

        dialog.HandleInput("\n");
        Assert.Equal("second-secret-demo", await prompt);
    }

    // ------------------------------------------------------------- device code

    [Fact]
    public void ShowDeviceCodeRendersBelTerminatedHyperlinksAndTheUserCode()
    {
        var dialog = CreateDialog();

        dialog.ShowDeviceCode(new OAuthDeviceCodeInfo("ABCD-1234", "https://example.invalid/device"));

        var clickHint = OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open";
        var output = RenderText(dialog, 120);
        Assert.Contains("https://example.invalid/device", output, StringComparison.Ordinal);
        Assert.Contains(clickHint, output, StringComparison.Ordinal);
        Assert.Contains("Enter code: ABCD-1234", output, StringComparison.Ordinal);

        // The login dialog writes OSC-8 inline with a BEL terminator, unlike TerminalImage.Hyperlink
        // (which uses ST). Locking that keeps the bytes identical to the TS source.
        var raw = string.Join("\n", dialog.Render(120));
        Assert.Contains("\x1b]8;;https://example.invalid/device\x07", raw, StringComparison.Ordinal);
        Assert.Contains("\x1b]8;;\x07", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowDeviceCodeReplacesTheAuthUrl()
    {
        using var seams = new LoginDialogSeams();
        var dialog = CreateDialog();

        dialog.ShowAuth("https://example.invalid/login");
        dialog.ShowDeviceCode(new OAuthDeviceCodeInfo("ABCD-1234", "https://example.invalid/device"));

        var output = RenderText(dialog, 120);
        Assert.DoesNotContain("https://example.invalid/login", output, StringComparison.Ordinal);
        Assert.Contains("https://example.invalid/device", output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ links

    [Fact]
    public void ShowInfoRendersUnlabelledLinksAndOptionalCloseHint()
    {
        var dialog = CreateDialog();

        dialog.ShowInfo("No label here.", [new AuthInfoLink("https://example.invalid/plain")]);
        var withoutHint = RenderText(dialog, 120);
        Assert.Contains("https://example.invalid/plain", withoutHint, StringComparison.Ordinal);
        Assert.DoesNotContain("to close", withoutHint, StringComparison.Ordinal);

        dialog.ShowInfo("Now with a hint.", [], showCloseHint: true);
        var withHint = RenderText(dialog, 120);
        Assert.Contains("to close", withHint, StringComparison.Ordinal);
    }

    // ----------------------------------------------------- waiting / progress

    [Fact]
    public void ShowWaitingAppendsAMessageWithACancelHint()
    {
        var dialog = CreateDialog();

        dialog.ShowWaiting("Waiting for authorization...");

        var output = RenderText(dialog, 120);
        Assert.Contains("Waiting for authorization...", output, StringComparison.Ordinal);
        Assert.Contains("to cancel", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowProgressAppendsEveryLineWithoutClearing()
    {
        var dialog = CreateDialog();

        dialog.ShowProgress("step 1");
        dialog.ShowProgress("step 2");

        var output = RenderText(dialog, 120);
        Assert.Contains("step 1", output, StringComparison.Ordinal);
        Assert.Contains("step 2", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowDetailsReplacesTheContentAndDropsTheCopyTarget()
    {
        using var seams = new LoginDialogSeams();
        var dialog = CreateDialog();

        dialog.ShowAuth("https://example.invalid/login", "Authorize the extension");
        dialog.ShowDetails(["AWS credential setup:"]);

        var output = RenderText(dialog, 120);
        Assert.DoesNotContain("https://example.invalid/login", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorize the extension", output, StringComparison.Ordinal);
        Assert.Contains("AWS credential setup:", output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ title

    [Fact]
    public void TitleFallsBackToTheProviderIdAndHonoursBothOverrides()
    {
        var plain = CreateDialog(providerId: "acme");
        Assert.Contains("Login to acme", RenderText(plain, 120), StringComparison.Ordinal);

        // TS `providerNameOverride || providerId`: an empty override falls back too.
        var emptyOverride = CreateDialog(providerId: "acme", providerNameOverride: "");
        Assert.Contains("Login to acme", RenderText(emptyOverride, 120), StringComparison.Ordinal);

        var named = CreateDialog(providerId: "acme", providerNameOverride: "Acme Corp");
        Assert.Contains("Login to Acme Corp", RenderText(named, 120), StringComparison.Ordinal);

        // TS `titleOverride ?? \`Login to ${providerName}\``: null-only, so an empty title stays empty.
        var titled = CreateDialog(providerId: "acme", titleOverride: "Custom title");
        var titledOutput = RenderText(titled, 120);
        Assert.Contains("Custom title", titledOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Login to", titledOutput, StringComparison.Ordinal);

        var emptyTitle = CreateDialog(providerId: "acme", titleOverride: "");
        Assert.DoesNotContain("Login to", RenderText(emptyTitle, 120), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ focus

    [Fact]
    public void FocusedIsForwardedToTheInput()
    {
        var dialog = CreateDialog();
        _ = dialog.ShowPromptAsync("Prompt:");

        var unfocused = string.Join("\n", dialog.Render(120));
        dialog.Focused = true;
        var focused = string.Join("\n", dialog.Render(120));

        Assert.True(dialog.Focused);
        Assert.NotEqual(unfocused, focused);
    }

    // ----------------------------------------------------------------- cancel

    [Fact]
    public async Task CancelKeybindingFailsThePendingPromptAndReportsFailure()
    {
        var completions = new List<(bool Success, string? Message)>();
        var dialog = CreateDialog(onComplete: (success, message) => completions.Add((success, message)));

        var prompt = dialog.ShowPromptAsync("Prompt:");
        dialog.HandleInput("\x1b");

        await Assert.ThrowsAsync<OperationCanceledException>(() => prompt);
        var completion = Assert.Single(completions);
        Assert.False(completion.Success);
        Assert.Equal("Login cancelled", completion.Message);
        Assert.True(dialog.Signal.IsCancellationRequested);
    }

    [Fact]
    public void CancelWithoutAPendingPromptStillReportsFailure()
    {
        var completions = new List<(bool Success, string? Message)>();
        var dialog = CreateDialog(onComplete: (success, message) => completions.Add((success, message)));

        dialog.HandleInput("\x03"); // ctrl+c, the other default for tui.select.cancel

        var completion = Assert.Single(completions);
        Assert.False(completion.Success);
        Assert.Equal("Login cancelled", completion.Message);
    }

    // ------------------------------------------------------------- copy key

    [Fact]
    public async Task CopyKeybindingCopiesTheShownUrlAndIsConsumed()
    {
        using var seams = new LoginDialogSeams();
        var clipboard = new RecordingNativeClipboard();
        Clipboard.NativeClipboardOverride = () => clipboard;

        // Remap app.message.copy to a printable key so "did it reach the input?" is observable.
        var keybindings = new CoreKeybindingsManager();
        keybindings.SetUserBindings(new KeybindingsConfig { [AppKeybindings.AppMessageCopy] = "z" });
        GlobalKeybindings.Set(keybindings);

        var dialog = CreateDialog();
        dialog.ShowAuth("https://example.invalid/login");
        _ = dialog.ShowPromptAsync("Prompt:");

        dialog.HandleInput("z");

        Assert.Equal(["https://example.invalid/login"], clipboard.Written);
        Assert.DoesNotContain("> z", RenderText(dialog, 120), StringComparison.Ordinal);

        // AuthUrlComponent reports success in its hint once the copy settles.
        await Task.Delay(200);
        Assert.Contains("Copied URL to clipboard", RenderText(dialog, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void CopyKeybindingReachesTheInputWhenNoUrlIsShown()
    {
        using var seams = new LoginDialogSeams();
        var clipboard = new RecordingNativeClipboard();
        Clipboard.NativeClipboardOverride = () => clipboard;

        var keybindings = new CoreKeybindingsManager();
        keybindings.SetUserBindings(new KeybindingsConfig { [AppKeybindings.AppMessageCopy] = "z" });
        GlobalKeybindings.Set(keybindings);

        var dialog = CreateDialog();
        _ = dialog.ShowPromptAsync("Prompt:");

        dialog.HandleInput("z");

        Assert.Empty(clipboard.Written);
        Assert.Contains("> z", RenderText(dialog, 120), StringComparison.Ordinal);
    }
}
