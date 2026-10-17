// ============================================================================
// Tests for the terminal-title, chat-notice and crash-messaging helpers of interactive-mode.ts (4f-4d).
//
// Upstream has no test file for these. The vectors are the exact strings TS builds, including the
// hardcoded "start pi and" branch and the abort/cancel regex.
// ============================================================================

using Pi.Ai.Types;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class InteractiveModeDiagnosticsTests : IDisposable
{
    private readonly bool? _previousChalkOverride = Chalk.EnabledOverride;

    public InteractiveModeDiagnosticsTests()
    {
        // theme.Fg delegates to chalk, which disables itself when stdout is redirected.
        Chalk.EnabledOverride = true;
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
    }

    public void Dispose()
    {
        Chalk.EnabledOverride = _previousChalkOverride;
        ThemeRuntime.ResetCachesForTests();
    }

    // ---------------------------------------------------------------- terminal title

    [Fact]
    public void TerminalTitleWithoutASessionNameIsTheDirectoryBasename()
    {
        Assert.Equal(
            $"{Config.AppTitle} - project",
            InteractiveModeDiagnostics.BuildTerminalTitle("/home/u/project", null));
    }

    [Fact]
    public void TerminalTitlePutsTheSessionNameInTheMiddle()
    {
        Assert.Equal(
            $"{Config.AppTitle} - my session - project",
            InteractiveModeDiagnostics.BuildTerminalTitle("/home/u/project", "my session"));
    }

    [Fact]
    public void TerminalTitleTreatsAnEmptySessionNameAsAbsent()
    {
        // TS `if (sessionName)` is falsy for "".
        Assert.Equal(
            $"{Config.AppTitle} - project",
            InteractiveModeDiagnostics.BuildTerminalTitle("/home/u/project", ""));
    }

    // ---------------------------------------------------------------- chat notices

    [Fact]
    public void ErrorNoticeIsPrefixedAndPaddedWithOutputPad()
    {
        var notice = InteractiveModeDiagnostics.ErrorNotice("boom", outputPad: 3);

        Assert.Equal("Error: boom", notice.Text);
        Assert.Equal("error", notice.ColorToken);
        Assert.Equal(3, notice.Pad);
    }

    [Fact]
    public void WarningAndStatusNoticesHardcodeAPaddingOfOne()
    {
        // TS: showWarning uses `1` and showStatus uses `1`, even though showError uses outputPad.
        var warning = InteractiveModeDiagnostics.WarningNotice("careful");
        Assert.Equal("Warning: careful", warning.Text);
        Assert.Equal("warning", warning.ColorToken);
        Assert.Equal(1, warning.Pad);

        var status = InteractiveModeDiagnostics.StatusNotice("done");
        Assert.Equal("done", status.Text);
        Assert.Equal("dim", status.ColorToken);
        Assert.Equal(1, status.Pad);
    }

    [Theory]
    [InlineData(2, true, true, true)]   // the tracked pair is still the tail: update in place
    [InlineData(3, true, true, true)]   // extra children before the pair are irrelevant
    [InlineData(1, true, true, false)]  // TS needs both `last` and `secondLast` to exist
    [InlineData(0, false, false, false)]
    [InlineData(2, false, true, false)] // something was appended after the status text
    [InlineData(2, true, false, false)] // the spacer is no longer second-to-last
    public void StatusCoalescingNeedsTheTrackedPairAtTheTail(
        int childCount,
        bool lastIsTrackedText,
        bool secondLastIsTrackedSpacer,
        bool expected)
    {
        Assert.Equal(
            expected,
            InteractiveModeDiagnostics.ShouldCoalesceStatus(childCount, lastIsTrackedText, secondLastIsTrackedSpacer));
    }

    // ---------------------------------------------------------------- editor border

    [Fact]
    public void BashModeBorderWinsOverTheThinkingLevel()
    {
        var theme = ThemeRuntime.Current;
        var border = InteractiveModeDiagnostics.ResolveEditorBorderColor(theme, isBashMode: true, ThinkingLevel.Max);

        Assert.Equal(theme.Fg(ThemeColors.BashMode, "x"), border("x"));
    }

    [Theory]
    [InlineData(ThinkingLevel.Off)]
    [InlineData(ThinkingLevel.Minimal)]
    [InlineData(ThinkingLevel.Low)]
    [InlineData(ThinkingLevel.Medium)]
    [InlineData(ThinkingLevel.High)]
    [InlineData(ThinkingLevel.XHigh)]
    [InlineData(ThinkingLevel.Max)]
    public void BorderFollowsTheThinkingLevelOutsideBashMode(ThinkingLevel level)
    {
        var theme = ThemeRuntime.Current;
        var border = InteractiveModeDiagnostics.ResolveEditorBorderColor(theme, isBashMode: false, level);

        Assert.Equal(theme.GetThinkingBorderColor(level)("x"), border("x"));
    }

    // ---------------------------------------------------------------- bug report hint

    [Fact]
    public void BugReportHintPointsAtTheBugCommand()
    {
        var hint = InteractiveModeDiagnostics.BugReportHint();

        Assert.Contains($"If this looks like a {Config.AppName} bug", hint);
        Assert.Contains("/bug sends a report to the developers.", hint);
    }

    // ---------------------------------------------------------------- crash instructions

    [Fact]
    public void CrashInstructionsOfferTheResumeCommandWhenTheSessionIsPersisted()
    {
        var instructions = InteractiveModeDiagnostics.CrashReportInstructions(hasSessionFile: true);

        Assert.Contains($"run `{Config.AppName} -r` to resume the session, then run /bug.", instructions);
        Assert.Contains("The crash details are attached automatically.", instructions);
    }

    [Fact]
    public void CrashInstructionsFallBackToStartingPiWithoutASessionFile()
    {
        var instructions = InteractiveModeDiagnostics.CrashReportInstructions(hasSessionFile: false);

        // The TS string hardcodes "start pi and" rather than using APP_NAME.
        Assert.Contains("To report this crash: start pi and run /bug.", instructions);
        Assert.DoesNotContain("-r", instructions);
    }

    // ---------------------------------------------------------------- abort/cancel detection

    [Theory]
    [InlineData("Request aborted", true)]
    [InlineData("Operation was aborted by the user", true)]
    [InlineData("cancelled", true)]
    [InlineData("canceled", true)]
    [InlineData("cancel", true)]
    [InlineData("Cancelled by user", true)]
    [InlineData("socket hang up", false)]
    [InlineData("abortive", false)] // \b keeps the match on whole words only
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AbortAndCancelMessagesAreRecognised(string? message, bool expected)
    {
        Assert.Equal(expected, InteractiveModeDiagnostics.IsAbortOrCancelMessage(message));
    }

    // ---------------------------------------------------------------- bug report decision

    [Theory]
    [InlineData("error", false, "boom", true)]
    [InlineData("stop", false, "boom", false)]
    [InlineData("error", true, "boom", false)] // retryable errors are handled by the retry path
    [InlineData("error", false, "Request aborted", false)]
    [InlineData("error", false, null, true)]
    public void BugReportIsSuggestedOnlyForNonRetryableNonAbortErrors(
        string? stopReason,
        bool isRetryable,
        string? errorMessage,
        bool expected)
    {
        Assert.Equal(
            expected,
            InteractiveModeDiagnostics.ShouldSuggestBugReport(stopReason, isRetryable, errorMessage));
    }

    // ---------------------------------------------------------------- install change

    [Fact]
    public void InstallChangeWarningNamesTheNewVersionAndTheResumeCommand()
    {
        var warning = InteractiveModeDiagnostics.BuildInstallChangeWarning(
            new InstallChange("updated", "2.0.0"),
            "pi -r");

        Assert.Contains($"{Config.AppName} was updated to 2.0.0", warning);
        Assert.Contains($"while this session was running ({Config.Version})", warning);
        Assert.Contains("Restart with `pi -r` to continue this session.", warning);
    }

    [Fact]
    public void InstallChangeWarningWithoutAResumeCommandSaysRestartPlainly()
    {
        var warning = InteractiveModeDiagnostics.BuildInstallChangeWarning(
            new InstallChange("removed"),
            null);

        Assert.Contains($"The {Config.AppName} installation this session runs from was removed or replaced", warning);
        Assert.Contains($"Restart {Config.AppName}.", warning);
        Assert.DoesNotContain("Restart with", warning);
    }
}
