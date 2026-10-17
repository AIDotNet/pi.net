// ============================================================================
// Tests for the slash-command helpers of interactive-mode.ts (4f-4k).
//
// Upstream has no test file for these. The path-argument vectors mirror the TS bodies, including
// the unterminated-quote and multiple-argument quirks.
// ============================================================================

using Pi.CodingAgent.Modes.Interactive;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class InteractiveModeCommandsTests
{
    // ---------------------------------------------------------------- path argument

    [Fact]
    public void ABareCommandHasNoPath()
    {
        Assert.Null(InteractiveModeCommands.GetPathCommandArgument("/export", "/export"));
        Assert.Null(InteractiveModeCommands.GetPathCommandArgument("/import", "/import"));
    }

    [Fact]
    public void AnUnrelatedCommandHasNoPath()
    {
        Assert.Null(InteractiveModeCommands.GetPathCommandArgument("/model gpt", "/export"));
        // TS checks `text.startsWith(command + " ")`, so a same-prefix command without the space is out.
        Assert.Null(InteractiveModeCommands.GetPathCommandArgument("/exports", "/export"));
    }

    [Fact]
    public void AnUnquotedPathIsTakenUpToTheFirstWhitespace()
    {
        Assert.Equal("/tmp/out.html", InteractiveModeCommands.GetPathCommandArgument("/export /tmp/out.html", "/export"));
        Assert.Equal(
            "/tmp/out.html",
            InteractiveModeCommands.GetPathCommandArgument("/export /tmp/out.html extra words", "/export"));
    }

    [Fact]
    public void LeadingWhitespaceIsSkippedButInteriorWhitespaceEndsThePath()
    {
        Assert.Equal("a.html", InteractiveModeCommands.GetPathCommandArgument("/export    a.html", "/export"));
    }

    [Theory]
    [InlineData("/export \"/tmp/my file.html\"", "/tmp/my file.html")] // double quotes
    [InlineData("/export '/tmp/my file.html'", "/tmp/my file.html")]   // single quotes
    [InlineData("/export ''", "")]                                     // an explicitly empty quoted path
    public void QuotedPathsKeepTheirInnerTextVerbatim(string text, string expected)
    {
        Assert.Equal(expected, InteractiveModeCommands.GetPathCommandArgument(text, "/export"));
    }

    [Fact]
    public void AnUnterminatedQuoteYieldsNoPath()
    {
        Assert.Null(InteractiveModeCommands.GetPathCommandArgument("/export \"/tmp/unterminated", "/export"));
        Assert.Null(InteractiveModeCommands.GetPathCommandArgument("/export '/tmp/unterminated", "/export"));
    }

    [Fact]
    public void AWhitespaceOnlyArgumentYieldsNoPath()
    {
        Assert.Null(InteractiveModeCommands.GetPathCommandArgument("/export    ", "/export"));
    }

    // ---------------------------------------------------------------- status and error strings

    [Fact]
    public void TheReloadStatusMentionsSavedTrustOnlyWhenItWasSaved()
    {
        Assert.Equal(
            "Reloaded keybindings, extensions, skills, prompts, themes, and context files; saved project trust",
            InteractiveModeCommands.ReloadedStatusMessage(true));
        Assert.Equal(
            "Reloaded keybindings, extensions, skills, prompts, themes, and context files",
            InteractiveModeCommands.ReloadedStatusMessage(false));
    }

    [Fact]
    public void TheImportUsageErrorNamesTheExpectedArgument()
    {
        Assert.Equal("Usage: /import <path.jsonl>", InteractiveModeCommands.ImportUsageError);
    }

    [Theory]
    [InlineData("boom", "Bash command failed: boom")]
    [InlineData("", "Bash command failed: Unknown error")]   // TS `err instanceof Error ? err.message : "Unknown error"`
    [InlineData(null, "Bash command failed: Unknown error")]
    public void BashFailuresFallBackToUnknownError(string? message, string expected)
    {
        Assert.Equal(expected, InteractiveModeCommands.BashFailureMessage(message));
    }

    // ---------------------------------------------------------------- hotkeys markdown

    /// <summary>Editor and app keys collapse to the binding id, so the assertions stay readable.</summary>
    private static string Key(string id) => id;

    [Fact]
    public void TheHotkeysBodyHasTheThreeFixedSections()
    {
        var markdown = InteractiveModeCommands.BuildHotkeysMarkdown(Key, Key, isWindows: false, []);

        Assert.Contains("**Navigation**", markdown);
        Assert.Contains("**Editing**", markdown);
        Assert.Contains("**Other**", markdown);
        Assert.StartsWith("**Navigation**", markdown);
        Assert.EndsWith("| `!!` | Run bash command (excluded from context) |", markdown);
    }

    [Fact]
    public void EveryKeyDisplayIsInterpolated()
    {
        var markdown = InteractiveModeCommands.BuildHotkeysMarkdown(Key, Key, isWindows: false, []);

        Assert.Contains("`tui.editor.cursorUp` / `tui.editor.cursorDown`", markdown);
        Assert.Contains("| `tui.input.submit` | Send message |", markdown);
        Assert.Contains("| `app.interrupt` | Cancel autocomplete / abort streaming |", markdown);
        Assert.Contains("| `/` | Slash commands |", markdown);
    }

    [Fact]
    public void TheNewLineHintOnlyAppearsOnWindows()
    {
        Assert.Contains(
            "New line (Ctrl+Enter on Windows Terminal)",
            InteractiveModeCommands.BuildHotkeysMarkdown(Key, Key, isWindows: true, []));
        Assert.DoesNotContain(
            "Ctrl+Enter",
            InteractiveModeCommands.BuildHotkeysMarkdown(Key, Key, isWindows: false, []));
    }

    [Fact]
    public void ExtensionShortcutsGetTheirOwnSection()
    {
        var markdown = InteractiveModeCommands.BuildHotkeysMarkdown(
            Key,
            Key,
            isWindows: false,
            [("ctrl+shift+d", "Deploy the project", "/tmp/deploy.ts")]);

        Assert.Contains("**Extensions**", markdown);
        // The key is capitalised for display, the description wins over the path.
        Assert.Contains("| `Ctrl+Shift+D` | Deploy the project |", markdown);
    }

    [Fact]
    public void AnExtensionShortcutWithoutADescriptionFallsBackToItsPath()
    {
        var markdown = InteractiveModeCommands.BuildHotkeysMarkdown(
            Key,
            Key,
            isWindows: false,
            [("ctrl+shift+d", null, "/tmp/deploy.ts")]);

        Assert.Contains("| `Ctrl+Shift+D` | /tmp/deploy.ts |", markdown);
    }

    [Fact]
    public void NoExtensionSectionWhenNothingRegistered()
    {
        Assert.DoesNotContain(
            "Extensions",
            InteractiveModeCommands.BuildHotkeysMarkdown(Key, Key, isWindows: false, []));
    }
}
