// ============================================================================
// Tests for modes/interactive/components/keybinding-hints.ts
// ============================================================================

using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class KeybindingHintsTests : IDisposable
{
    private readonly bool? _previousChalk = Chalk.EnabledOverride;
    private readonly bool? _previousMacOs = KeybindingHints.MacOsOverride;

    public KeybindingHintsTests()
    {
        ThemeRuntime.ResetCachesForTests();
        Chalk.EnabledOverride = true;
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
    }

    public void Dispose()
    {
        Chalk.EnabledOverride = _previousChalk;
        KeybindingHints.MacOsOverride = _previousMacOs;
        ThemeRuntime.ResetCachesForTests();
    }

    // ------------------------------------------------------------------ formatKeyText

    [Fact]
    public void FormatKeyText_LeavesASingleKeyUnchanged()
    {
        Assert.Equal("ctrl+b", KeybindingHints.FormatKeyText("ctrl+b"));
    }

    [Fact]
    public void FormatKeyText_KeepsTheSlashSeparatedAlternatives()
    {
        Assert.Equal("ctrl+b/left", KeybindingHints.FormatKeyText("ctrl+b/left"));
    }

    [Fact]
    public void FormatKeyText_CapitalizeUppercasesTheFirstCharacterOfEachPart()
    {
        Assert.Equal(
            "Ctrl+B/Left",
            KeybindingHints.FormatKeyText("ctrl+b/left", new KeyTextFormatOptions { Capitalize = true }));
    }

    [Fact]
    public void FormatKeyText_OnMacOsShowsAltAsOption()
    {
        KeybindingHints.MacOsOverride = true;
        Assert.Equal("option+f", KeybindingHints.FormatKeyText("alt+f"));
        Assert.Equal(
            "Option+F",
            KeybindingHints.FormatKeyText("alt+f", new KeyTextFormatOptions { Capitalize = true }));
    }

    [Fact]
    public void FormatKeyText_OnOtherPlatformsAltStaysAlt()
    {
        KeybindingHints.MacOsOverride = false;
        Assert.Equal("alt+f", KeybindingHints.FormatKeyText("alt+f"));
    }

    [Fact]
    public void FormatKeyText_EmptyInputIsEmpty()
    {
        Assert.Equal("", KeybindingHints.FormatKeyText(""));
    }

    // ------------------------------------------------------------------ keyText / keyDisplayText

    [Fact]
    public void KeyText_ReadsTheGlobalKeybindings()
    {
        Assert.Equal("enter", KeybindingHints.KeyText(TuiKeybindingIds.InputSubmit));
    }

    [Fact]
    public void KeyDisplayText_CapitalizesTheBoundKeys()
    {
        Assert.Equal("Enter", KeybindingHints.KeyDisplayText(TuiKeybindingIds.InputSubmit));
    }

    [Fact]
    public void KeyText_MultipleBoundKeysAreSlashSeparated()
    {
        Assert.Equal("left/ctrl+b", KeybindingHints.KeyText(TuiKeybindingIds.EditorCursorLeft));
    }

    [Fact]
    public void KeyText_UnknownKeybindingIsEmpty()
    {
        Assert.Equal("", KeybindingHints.KeyText("not.a.real.binding"));
    }

    // ------------------------------------------------------------------ keyHint / rawKeyHint

    [Fact]
    public void KeyHint_ColorsTheKeyAndTheDescription()
    {
        var hint = KeybindingHints.KeyHint(TuiKeybindingIds.InputSubmit, "submit");
        Assert.Contains("enter", hint);
        Assert.Contains(" submit", hint);
        Assert.Contains("\u001b[", hint);
    }

    [Fact]
    public void RawKeyHint_FormatsARawKeySpec()
    {
        var hint = KeybindingHints.RawKeyHint("ctrl+x", "cut");
        Assert.Contains("ctrl+x", hint);
        Assert.Contains(" cut", hint);
        Assert.Contains("\u001b[", hint);
    }

    [Fact]
    public void KeyHint_UsesTheDimTokenForTheKey()
    {
        var hint = KeybindingHints.KeyHint(TuiKeybindingIds.InputSubmit, "submit");
        // The key half is the dim-styled key text; the description half follows after a space.
        var dimKey = ThemeRuntime.Current.Fg("dim", "enter");
        Assert.StartsWith(dimKey, hint);
        Assert.Equal(dimKey + ThemeRuntime.Current.Fg("muted", " submit"), hint);
    }
}
