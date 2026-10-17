// ============================================================================
// Tests for the lifecycle helpers of interactive-mode.ts (4f-4l).
//
// Upstream has no test file for formatResumeCommand / checkTmuxKeyboardSetup's decision; the
// vectors are derived from the TS bodies. The quoting reuses the 4f-4c QuoteIfNeeded rules.
// ============================================================================

using Pi.CodingAgent.Modes.Interactive;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class InteractiveModeLifecycleTests
{
    private const string App = "pi";

    // ---------------------------------------------------------------- resume command

    [Fact]
    public void APersistedSessionResumesWithItsId()
    {
        Assert.Equal(
            "pi --session s1",
            InteractiveModeLifecycle.FormatResumeCommand(
                isTty: true,
                isPersisted: true,
                sessionFile: "/tmp/s.jsonl",
                sessionFileExists: true,
                usesDefaultSessionDir: true,
                sessionDir: "/default",
                sessionId: "s1",
                appName: App));
    }

    [Theory]
    [InlineData(false, true, "/tmp/s.jsonl", true)]  // piped stdout: no resume command
    [InlineData(true, false, "/tmp/s.jsonl", true)]  // in-memory session
    [InlineData(true, true, null, true)]             // no file recorded
    [InlineData(true, true, "", true)]               // empty file path counts as absent (TS `!sessionFile`)
    [InlineData(true, true, "/tmp/gone.jsonl", false)] // the file was deleted
    public void EveryGateIndependentlySuppressesTheResumeCommand(
        bool isTty,
        bool isPersisted,
        string? sessionFile,
        bool sessionFileExists)
    {
        Assert.Null(InteractiveModeLifecycle.FormatResumeCommand(
            isTty,
            isPersisted,
            sessionFile,
            sessionFileExists,
            usesDefaultSessionDir: true,
            sessionDir: "/default",
            sessionId: "s1",
            appName: App));
    }

    [Fact]
    public void ACustomSessionDirIsPassedWithTheDirQuoted()
    {
        Assert.Equal(
            "pi --session-dir /plain --session s1",
            InteractiveModeLifecycle.FormatResumeCommand(
                isTty: true,
                isPersisted: true,
                sessionFile: "/tmp/s.jsonl",
                sessionFileExists: true,
                usesDefaultSessionDir: false,
                sessionDir: "/plain",
                sessionId: "s1",
                appName: App));
    }

    [Fact]
    public void ASessionDirNeedingQuotesIsQuotedByTheSharedRule()
    {
        // The dir contains a space, so QuoteIfNeeded wraps it in single quotes.
        Assert.Equal(
            "pi --session-dir '/my dir' --session s1",
            InteractiveModeLifecycle.FormatResumeCommand(
                isTty: true,
                isPersisted: true,
                sessionFile: "/tmp/s.jsonl",
                sessionFileExists: true,
                usesDefaultSessionDir: false,
                sessionDir: "/my dir",
                sessionId: "s1",
                appName: App));
    }

    // ---------------------------------------------------------------- tmux keyboard

    [Fact]
    public void AnUnqueryableTmuxStaysSilent()
    {
        // TS: timeout / sandbox failures must not produce a warning.
        Assert.Null(InteractiveModeLifecycle.ResolveTmuxKeyboardWarning(null, null));
        Assert.Null(InteractiveModeLifecycle.ResolveTmuxKeyboardWarning(null, "csi-u"));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("always")]
    public void EnabledExtendedKeysOnlyWarnAboutTheFormat(string extendedKeys)
    {
        Assert.Null(InteractiveModeLifecycle.ResolveTmuxKeyboardWarning(extendedKeys, "csi-u"));
        Assert.Equal(
            "tmux extended-keys-format is xterm. Pi works best with csi-u. Add `set -g extended-keys-format csi-u` to ~/.tmux.conf and restart tmux.",
            InteractiveModeLifecycle.ResolveTmuxKeyboardWarning(extendedKeys, "xterm"));
    }

    [Fact]
    public void DisabledExtendedKeysWarnBeforeTheFormatIsEvenConsidered()
    {
        Assert.Equal(
            "tmux extended-keys is off. Modified Enter keys may not work. Add `set -g extended-keys on` to ~/.tmux.conf and restart tmux.",
            InteractiveModeLifecycle.ResolveTmuxKeyboardWarning("off", "xterm"));
        Assert.Equal(
            "tmux extended-keys is off. Modified Enter keys may not work. Add `set -g extended-keys on` to ~/.tmux.conf and restart tmux.",
            InteractiveModeLifecycle.ResolveTmuxKeyboardWarning("auto", null));
    }

    // ---------------------------------------------------------------- changelog gating

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    public void ResumedSessionsNeverShowTheChangelog(int messageCount, bool expected)
    {
        Assert.Equal(expected, InteractiveModeLifecycle.ShouldSkipChangelog(messageCount));
    }

    [Fact]
    public void AMissingRecordedVersionMeansFreshInstall()
    {
        Assert.True(InteractiveModeLifecycle.IsFreshInstall(null));
        Assert.True(InteractiveModeLifecycle.IsFreshInstall(""));
        Assert.False(InteractiveModeLifecycle.IsFreshInstall("1.2.3"));
    }
}
