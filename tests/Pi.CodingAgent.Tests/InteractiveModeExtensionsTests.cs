// ============================================================================
// Tests for the extension-binding helpers of interactive-mode.ts (4f-4h).
//
// Upstream has no test file for these. The crash-hint vectors reuse the labels the 4f-4c
// InteractiveModeModuleTests already pin, because FormatCrashExtensionHint is the same function.
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class InteractiveModeExtensionsTests
{
    private static SourceInfo Source() =>
        new()
        {
            Path = "/tmp/my-ext.ts",
            Source = "local",
            Scope = SourceScope.User,
            Origin = SourceOrigin.TopLevel,
        };

    // ---------------------------------------------------------------- widget lines

    [Fact]
    public void AShortWidgetKeepsEveryLine()
    {
        var content = Enumerable.Range(1, InteractiveModeExtensions.MaxWidgetLines).Select(i => $"line {i}").ToList();

        var (lines, truncated) = InteractiveModeExtensions.WidgetContent(content);

        Assert.Equal(content, lines);
        Assert.False(truncated);
    }

    [Fact]
    public void ALongWidgetKeepsOnlyTheFirstMaxLines()
    {
        var content = Enumerable.Range(1, 25).Select(i => $"line {i}").ToList();

        var (lines, truncated) = InteractiveModeExtensions.WidgetContent(content);

        Assert.Equal(InteractiveModeExtensions.MaxWidgetLines, lines.Count);
        Assert.Equal("line 1", lines[0]);
        Assert.Equal($"line {InteractiveModeExtensions.MaxWidgetLines}", lines[^1]);
        Assert.True(truncated);
    }

    [Fact]
    public void TheTruncationNoteIsTheSeparateWidgetLine()
    {
        // TS appends a ThemedText with this text after the sliced lines, never inside them.
        Assert.Equal("... (widget truncated)", InteractiveModeExtensions.WidgetTruncationNote);
    }

    [Fact]
    public void TheLimitIsTenLines()
    {
        Assert.Equal(10, InteractiveModeExtensions.MaxWidgetLines);
    }

    // ---------------------------------------------------------------- confirm dialog

    [Fact]
    public void TheConfirmTitleJoinsThePromptWithANewline()
    {
        Assert.Equal("Delete file\nAre you sure?", InteractiveModeExtensions.ConfirmTitle("Delete file", "Are you sure?"));
    }

    [Fact]
    public void OnlyYesConfirms()
    {
        Assert.True(InteractiveModeExtensions.IsConfirmed("Yes"));
        Assert.False(InteractiveModeExtensions.IsConfirmed("No"));
        Assert.False(InteractiveModeExtensions.IsConfirmed(null));  // the dialog was dismissed
        Assert.False(InteractiveModeExtensions.IsConfirmed("yes")); // TS compares exactly
    }

    [Fact]
    public void TheConfirmDialogOffersExactlyYesAndNo()
    {
        Assert.Equal(["Yes", "No"], InteractiveModeExtensions.ConfirmOptions);
    }

    // ---------------------------------------------------------------- crash hint

    [Fact]
    public void AMatchingStackFrameNamesTheExtension()
    {
        var hint = InteractiveModeExtensions.GetCrashExtensionHint(
            "Error: boom\n    at doWork (/tmp/my-ext.ts:3:9)\n    at main (/tmp/other.js:1:1)",
            [new ExtensionStackMetadata("my-ext", "/tmp/my-ext.ts", Source())]);

        Assert.NotNull(hint);
        Assert.Contains("`my-ext`", hint);
    }

    [Fact]
    public void ANullStackYieldsNoHint()
    {
        // TS guards on `!stack`; the class then gets undefined and adds nothing.
        Assert.Null(InteractiveModeExtensions.GetCrashExtensionHint(
            null,
            [new ExtensionStackMetadata("my-ext", "/tmp/my-ext.ts", Source())]));
    }

    [Fact]
    public void ASUnrelatedStackYieldsNoHint()
    {
        Assert.Null(InteractiveModeExtensions.GetCrashExtensionHint(
            "Error: boom\n    at doWork (/tmp/other.js:3:9)",
            [new ExtensionStackMetadata("my-ext", "/tmp/my-ext.ts", Source())]));
    }

    // ---------------------------------------------------------------- shortcut errors

    [Fact]
    public void ShortcutErrorsArePrefixedSoTheUserKnowsWhereTheyCameFrom()
    {
        Assert.Equal("Shortcut handler error: boom", InteractiveModeExtensions.ShortcutErrorMessage("boom"));
    }

    // ---------------------------------------------------------------- theme persistence

    [Fact]
    public void AnAcceptedNewNameIsPersisted()
    {
        Assert.True(InteractiveModeExtensions.ShouldPersistThemeName(true, "dark", "light"));
    }

    [Fact]
    public void ARejectedNameLeavesTheSettingAlone()
    {
        Assert.False(InteractiveModeExtensions.ShouldPersistThemeName(false, "dark", "light"));
    }

    [Fact]
    public void SettingTheSameNameAgainIsNotAWrite()
    {
        Assert.False(InteractiveModeExtensions.ShouldPersistThemeName(true, "dark", "dark"));
        // An unset setting is not "same", so it is written.
        Assert.True(InteractiveModeExtensions.ShouldPersistThemeName(true, null, "dark"));
    }
}
