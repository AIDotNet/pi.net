// ============================================================================
// Tests for the module-level half of interactive-mode.ts (4f-4c).
//
// Upstream has no test file for these items. The vectors come from the source: the two small
// components are driven directly, and the pure helpers are checked against the exact strings TS
// builds (crash hint grammar, POSIX quoting, llama.cpp guidance).
// ============================================================================

using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class InteractiveModeModuleTests : SelectorTestFixture
{
    private static string RenderText(Pi.Tui.IComponent component, int width = 80) =>
        string.Join("\n", component.Render(width));

    // ---------------------------------------------------------------- expandable text

    [Fact]
    public void ExpandableTextStartsCollapsedAndSwitchesOnDemand()
    {
        var text = new ExpandableText(() => "collapsed", () => "expanded");

        Assert.Contains("collapsed", RenderText(text));
        Assert.DoesNotContain("expanded", RenderText(text));

        text.SetExpanded(true);

        Assert.Contains("expanded", RenderText(text));
        Assert.DoesNotContain("collapsed", RenderText(text));
    }

    [Fact]
    public void ExpandableTextCanStartExpanded()
    {
        var text = new ExpandableText(() => "collapsed", () => "expanded", expanded: true);

        Assert.Contains("expanded", RenderText(text));
    }

    // ---------------------------------------------------------------- built-in header

    private static BuiltInHeader Header(Action<int, int>? onLogoClick)
    {
        var header = new BuiltInHeader(() => "logo", () => "logo-expanded") { OnLogoClick = onLogoClick };
        return header;
    }

    private static TuiMouseEvent Click(int x, int y, int screenX = 10, int screenY = 5) => new()
    {
        Type = TuiMouseEventType.Click,
        X = x,
        Y = y,
        ScreenX = screenX,
        ScreenY = screenY,
    };

    [Fact]
    public void HeaderReportsTheClickedLogoCellInScreenCoordinates()
    {
        (int Column, int Row)? click = null;
        var header = Header((column, row) => click = (column, row));

        var result = header.HandleMouse(Click(x: 2, y: 1, screenX: 10, screenY: 5));

        Assert.NotNull(result);
        Assert.True(result!.Handled);
        // TS: onLogoClick(screenX - x + 1, screenY - y)
        Assert.Equal((9, 4), click);
    }

    [Theory]
    [InlineData(0, 0)] // left of the logo column range
    [InlineData(5, 0)] // right of it
    [InlineData(1, 2)] // below the two logo lines
    public void HeaderIgnoresClicksOutsideTheLogo(int x, int y)
    {
        var calls = 0;
        var header = Header((_, _) => calls += 1);

        Assert.Null(header.HandleMouse(Click(x, y)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void HeaderIgnoresNonClickEvents()
    {
        var calls = 0;
        var header = Header((_, _) => calls += 1);
        var press = Click(2, 0);
        press.Type = TuiMouseEventType.Press;

        Assert.Null(header.HandleMouse(press));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void HeaderIgnoresLogoClickWithoutACallback()
    {
        var header = Header(null);

        Assert.Null(header.HandleMouse(Click(2, 0)));
    }

    // ---------------------------------------------------------------- quoting

    [Theory]
    [InlineData("plain/path-1.2_a~b:c@d", "plain/path-1.2_a~b:c@d")]
    [InlineData("with space", "'with space'")]
    [InlineData("", "''")]
    [InlineData("a'b", "'a'\\''b'")]
    public void QuoteIfNeededQuotesOnlyWhenNecessary(string value, string expected)
    {
        Assert.Equal(expected, InteractiveModeSupport.QuoteIfNeeded(value));
    }

    // ---------------------------------------------------------------- crash hint

    [Fact]
    public void CrashHintIsNullWithoutExtensionMatches()
    {
        Assert.Null(InteractiveModeSupport.FormatCrashExtensionHint(null));
        Assert.Null(InteractiveModeSupport.FormatCrashExtensionHint([]));
        Assert.Null(InteractiveModeSupport.FormatCrashExtensionHint([null, ""]));
    }

    [Fact]
    public void CrashHintNamesASingleExtension()
    {
        var hint = InteractiveModeSupport.FormatCrashExtensionHint(["one"]);

        Assert.NotNull(hint);
        Assert.Contains("loaded extension `one`", hint);
        Assert.Contains("Try disabling it with", hint);
    }

    [Fact]
    public void CrashHintJoinsTwoExtensionsWithAnd()
    {
        var hint = InteractiveModeSupport.FormatCrashExtensionHint(["one", "two"]);

        Assert.NotNull(hint);
        Assert.Contains("loaded extensions `one` and `two`", hint);
        Assert.Contains("Try disabling them with", hint);
    }

    [Fact]
    public void CrashHintUsesASerialCommaForThreeOrMore()
    {
        var hint = InteractiveModeSupport.FormatCrashExtensionHint(["one", "two", "three"]);

        Assert.NotNull(hint);
        Assert.Contains("`one`, `two`, and `three`", hint);
    }

    [Fact]
    public void CrashHintSkipsEmptyEntries()
    {
        var hint = InteractiveModeSupport.FormatCrashExtensionHint(["", null, "one"]);

        Assert.NotNull(hint);
        Assert.Contains("loaded extension `one`", hint);
    }

    // ---------------------------------------------------------------- small predicates

    [Theory]
    [InlineData("sk-ant-oat01-abc", true)]
    [InlineData("sk-ant-api03-abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AnthropicSubscriptionKeysAreDetectedByPrefix(string? apiKey, bool expected)
    {
        Assert.Equal(expected, InteractiveModeSupport.IsAnthropicSubscriptionAuthKey(apiKey));
    }

    [Fact]
    public void LlamaCppGuidanceDependsOnWhetherAModelIsLoaded()
    {
        var none = InteractiveModeSupport.LlamaCppPostLoginGuidance("Logged in", 0);
        var loaded = InteractiveModeSupport.LlamaCppPostLoginGuidance("Logged in", 2);

        Assert.StartsWith("Logged in. No llama.cpp models are loaded.", none);
        Assert.Contains("Use /llama to load a model, then /model to select it.", none);
        Assert.StartsWith("Logged in. Use /model to select a loaded llama.cpp model", loaded);
    }

    [Fact]
    public void DeadTerminalErrorsAreRecognisedByType()
    {
        // .NET folds EIO/EPIPE/ENOTTY into IOException; see C263 for the mapping.
        Assert.True(InteractiveModeSupport.IsDeadTerminalError(new IOException("write failed")));
        Assert.True(InteractiveModeSupport.IsDeadTerminalError(new ObjectDisposedException("tty")));
        Assert.False(InteractiveModeSupport.IsDeadTerminalError(new InvalidOperationException()));
        Assert.False(InteractiveModeSupport.IsDeadTerminalError(null));
    }
}
