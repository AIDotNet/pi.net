// ============================================================================
// Tests for the TUI-helper half of modes/interactive/theme/theme.ts
// ============================================================================
//
// The tokenizer is behind SyntaxHighlight.HighlighterOverride (divergence C23), so these tests drive
// the part that belongs to this repository — scope-to-token mapping, the language table, and the two
// different highlighting fallbacks — with a scripted highlighter, and pin the component themes that
// the rest of the TUI reads.

using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class ThemeHelpersTests : IDisposable
{
    private readonly Func<IHighlighter>? _previousHighlighter = SyntaxHighlight.HighlighterOverride;
    private readonly bool? _previousChalkOverride = Chalk.EnabledOverride;

    public ThemeHelpersTests()
    {
        ThemeRuntime.ResetCachesForTests();
        ThemeHelpers.ResetCachesForTests();
        // theme.bold()/italic()/… delegate to chalk, which disables itself when stdout is redirected.
        Chalk.EnabledOverride = true;
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
    }

    public void Dispose()
    {
        SyntaxHighlight.HighlighterOverride = _previousHighlighter;
        Chalk.EnabledOverride = _previousChalkOverride;
        ThemeHelpers.ResetCachesForTests();
        ThemeRuntime.ResetCachesForTests();
    }

    private static Theme Current => ThemeRuntime.Current;

    /// <summary>A tokenizer that answers with scripted HTML (or throws) so the rewrite can be pinned.</summary>
    private sealed class ScriptedHighlighter(string? html = null, Exception? error = null) : IHighlighter
    {
        public string Highlight(string code, string language, bool ignoreIllegals) =>
            error is not null ? throw error : html!;

        public string HighlightAuto(string code, IReadOnlyList<string>? languageSubset) =>
            error is not null ? throw error : html ?? code;

        public bool SupportsLanguage(string name) => true;

        public Task LoadAllLanguagesAsync() => Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------------------
    // highlightCode
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void HighlightCode_SkipsHighlightingWithoutAValidLanguage()
    {
        // The default highlighter supports nothing, so a language is never valid.
        SyntaxHighlight.HighlighterOverride = null;
        Assert.Equal(
            new[] { Current.Fg(ThemeColors.MdCodeBlock, "const x = 1;"), Current.Fg(ThemeColors.MdCodeBlock, "y") },
            ThemeHelpers.HighlightCode("const x = 1;\ny", "json"));
    }

    [Fact]
    public void HighlightCode_SkipsHighlightingWithoutALanguage() =>
        Assert.Equal(
            new[] { Current.Fg(ThemeColors.MdCodeBlock, "plain") },
            ThemeHelpers.HighlightCode("plain"));

    [Fact]
    public void HighlightCode_MapsScopesToThemeTokens()
    {
        SyntaxHighlight.HighlighterOverride = () => new ScriptedHighlighter(
            "<span class=\"hljs-keyword\">const</span> <span class=\"hljs-string\">&quot;x&quot;</span>");
        Assert.Equal(
            new[]
            {
                Current.Fg(ThemeColors.SyntaxKeyword, "const") + " " + Current.Fg(ThemeColors.SyntaxString, "\"x\""),
            },
            ThemeHelpers.HighlightCode("const \"x\"", "javascript"));
    }

    [Fact]
    public void HighlightCode_MapsEveryScopeTheCliThemeDefines()
    {
        (string Scope, string Token)[] cases =
        [
            ("keyword", ThemeColors.SyntaxKeyword),
            ("built_in", ThemeColors.SyntaxType),
            ("literal", ThemeColors.SyntaxNumber),
            ("number", ThemeColors.SyntaxNumber),
            ("regexp", ThemeColors.SyntaxString),
            ("string", ThemeColors.SyntaxString),
            ("subst", ThemeColors.Text),
            ("comment", ThemeColors.SyntaxComment),
            ("doctag", ThemeColors.SyntaxComment),
            ("meta", ThemeColors.Muted),
            ("function", ThemeColors.SyntaxFunction),
            ("title", ThemeColors.SyntaxFunction),
            ("class", ThemeColors.SyntaxType),
            ("type", ThemeColors.SyntaxType),
            ("tag", ThemeColors.SyntaxPunctuation),
            ("name", ThemeColors.SyntaxKeyword),
            ("attr", ThemeColors.SyntaxVariable),
            ("variable", ThemeColors.SyntaxVariable),
            ("params", ThemeColors.SyntaxVariable),
            ("operator", ThemeColors.SyntaxOperator),
            ("punctuation", ThemeColors.SyntaxPunctuation),
            ("addition", ThemeColors.ToolDiffAdded),
            ("deletion", ThemeColors.ToolDiffRemoved),
        ];

        foreach (var (scope, token) in cases)
        {
            SyntaxHighlight.HighlighterOverride = () =>
                new ScriptedHighlighter($"<span class=\"hljs-{scope}\">x</span>");
            Assert.Equal(
                new[] { Current.Fg(token, "x") },
                ThemeHelpers.HighlightCode("x", "javascript"));
        }
    }

    [Fact]
    public void HighlightCode_MapsEmphasisStrongAndLinkToTextAttributes()
    {
        (string Scope, string Expected)[] cases =
        [
            ("emphasis", Current.Italic("x")),
            ("strong", Current.Bold("x")),
            ("link", Current.Underline("x")),
        ];

        foreach (var (scope, expected) in cases)
        {
            SyntaxHighlight.HighlighterOverride = () =>
                new ScriptedHighlighter($"<span class=\"hljs-{scope}\">x</span>");
            Assert.Equal(new[] { expected }, ThemeHelpers.HighlightCode("x", "javascript"));
        }
    }

    [Fact]
    public void HighlightCode_SplitsIntoLines()
    {
        SyntaxHighlight.HighlighterOverride = () =>
            new ScriptedHighlighter("<span class=\"hljs-keyword\">a</span>\n<span class=\"hljs-keyword\">b</span>");
        Assert.Equal(
            new[] { Current.Fg(ThemeColors.SyntaxKeyword, "a"), Current.Fg(ThemeColors.SyntaxKeyword, "b") },
            ThemeHelpers.HighlightCode("a\nb", "javascript"));
    }

    [Fact]
    public void HighlightCode_ReturnsUncoloredLinesWhenTheTokenizerThrows()
    {
        // Note: TS returns the *uncolored* lines here, unlike MarkdownTheme.highlightCode.
        SyntaxHighlight.HighlighterOverride =
            () => new ScriptedHighlighter(error: new InvalidOperationException("boom"));
        Assert.Equal(new[] { "a", "b" }, ThemeHelpers.HighlightCode("a\nb", "javascript"));
    }

    // ---------------------------------------------------------------------------------------------
    // getLanguageFromPath
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("src/main.ts", "typescript")]
    [InlineData("src/App.tsx", "typescript")]
    [InlineData("a.js", "javascript")]
    [InlineData("a.py", "python")]
    [InlineData("a.cs", "csharp")]
    [InlineData("a.sh", "bash")]
    [InlineData("a.yml", "yaml")]
    [InlineData("a.htm", "html")]
    [InlineData("a.md", "markdown")]
    public void GetLanguageFromPath_MapsKnownExtensions(string path, string expected) =>
        Assert.Equal(expected, ThemeHelpers.GetLanguageFromPath(path));

    [Theory]
    [InlineData("MAIN.TS", "typescript")]
    [InlineData("A.Cs", "csharp")]
    public void GetLanguageFromPath_IsCaseInsensitive(string path, string expected) =>
        Assert.Equal(expected, ThemeHelpers.GetLanguageFromPath(path));

    [Theory]
    [InlineData("Makefile", "makefile")]
    [InlineData("Dockerfile", "dockerfile")]
    public void GetLanguageFromPath_UsesTheWholeNameWhenThereIsNoDot(string path, string expected) =>
        Assert.Equal(expected, ThemeHelpers.GetLanguageFromPath(path));

    [Theory]
    [InlineData("a.unknown")]
    [InlineData("a")]
    [InlineData("file.")]
    [InlineData("")]
    [InlineData("/a/b/c")]
    // The table is keyed by the last dot-separated segment, so a bare name in a directory does not
    // resolve: `path/to/Makefile` yields `path/to/makefile`, not `makefile`. TS behaves the same.
    [InlineData("path/to/Makefile")]
    public void GetLanguageFromPath_ReturnsNullForAnythingElse(string path) =>
        Assert.Null(ThemeHelpers.GetLanguageFromPath(path));

    // ---------------------------------------------------------------------------------------------
    // Component themes
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void GetMarkdownTheme_MapsEveryElementToItsToken()
    {
        var markdown = ThemeHelpers.GetMarkdownTheme();
        Assert.Equal(Current.Fg(ThemeColors.MdHeading, "x"), markdown.Heading("x"));
        Assert.Equal(Current.Fg(ThemeColors.MdLink, "x"), markdown.Link("x"));
        Assert.Equal(Current.Fg(ThemeColors.MdLinkUrl, "x"), markdown.LinkUrl("x"));
        Assert.Equal(Current.Fg(ThemeColors.MdCode, "x"), markdown.Code("x"));
        Assert.Equal(Current.Fg(ThemeColors.MdCodeBlock, "x"), markdown.CodeBlock("x"));
        Assert.Equal(Current.Fg(ThemeColors.MdCodeBlockBorder, "x"), markdown.CodeBlockBorder("x"));
        Assert.Equal(Current.Fg(ThemeColors.MdQuote, "x"), markdown.Quote("x"));
        Assert.Equal(Current.Fg(ThemeColors.MdQuoteBorder, "x"), markdown.QuoteBorder("x"));
        Assert.Equal(Current.Fg(ThemeColors.MdHr, "x"), markdown.Hr("x"));
        Assert.Equal(Current.Fg(ThemeColors.MdListBullet, "x"), markdown.ListBullet("x"));
        Assert.Equal(Current.Bold("x"), markdown.Bold("x"));
        Assert.Equal(Current.Italic("x"), markdown.Italic("x"));
        Assert.Equal(Current.Underline("x"), markdown.Underline("x"));
        Assert.Equal(Current.Strikethrough("x"), markdown.Strikethrough("x"));
    }

    [Fact]
    public void GetMarkdownTheme_KeepsTheCodeBlockColoringWhenTheTokenizerThrows()
    {
        // Unlike highlightCode, this fallback keeps the code-block coloring.
        SyntaxHighlight.HighlighterOverride =
            () => new ScriptedHighlighter(error: new InvalidOperationException("boom"));
        var highlight = ThemeHelpers.GetMarkdownTheme().HighlightCode;
        Assert.NotNull(highlight);
        Assert.Equal(
            new[] { Current.Fg(ThemeColors.MdCodeBlock, "a"), Current.Fg(ThemeColors.MdCodeBlock, "b") },
            highlight!("a\nb", "javascript"));
    }

    [Fact]
    public void GetMarkdownTheme_SkipsHighlightingWithoutAValidLanguage()
    {
        SyntaxHighlight.HighlighterOverride = null;
        var highlight = ThemeHelpers.GetMarkdownTheme().HighlightCode;
        Assert.NotNull(highlight);
        Assert.Equal(new[] { Current.Fg(ThemeColors.MdCodeBlock, "plain") }, highlight!("plain", "json"));
    }

    [Fact]
    public void GetSelectListTheme_MapsEverySlot()
    {
        var select = ThemeHelpers.GetSelectListTheme();
        Assert.Equal(Current.Fg(ThemeColors.Accent, "x"), select.SelectedPrefix("x"));
        Assert.Equal(Current.Fg(ThemeColors.Accent, "x"), select.SelectedText("x"));
        Assert.Equal(Current.Fg(ThemeColors.Muted, "x"), select.Description("x"));
        Assert.Equal(Current.Fg(ThemeColors.Muted, "x"), select.ScrollInfo("x"));
        Assert.Equal(Current.Fg(ThemeColors.Muted, "x"), select.NoMatch("x"));
    }

    [Fact]
    public void GetEditorTheme_UsesTheMutedBorderAndTheSelectListTheme()
    {
        var editor = ThemeHelpers.GetEditorTheme();
        Assert.Equal(Current.Fg(ThemeColors.BorderMuted, "x"), editor.BorderColor("x"));
        Assert.Equal(Current.Fg(ThemeColors.Accent, "x"), editor.SelectList.SelectedText("x"));
    }

    [Fact]
    public void GetSettingsListTheme_StylesSelectedAndUnselectedRowsDifferently()
    {
        var settings = ThemeHelpers.GetSettingsListTheme();
        Assert.Equal(Current.Fg(ThemeColors.Accent, "x"), settings.Label("x", true));
        Assert.Equal("x", settings.Label("x", false));
        Assert.Equal(Current.Fg(ThemeColors.Accent, "x"), settings.Value("x", true));
        Assert.Equal(Current.Fg(ThemeColors.Muted, "x"), settings.Value("x", false));
        Assert.Equal(Current.Fg(ThemeColors.Dim, "x"), settings.Description("x"));
        Assert.Equal(Current.Fg(ThemeColors.Accent, "→ "), settings.Cursor);
        Assert.Equal(Current.Fg(ThemeColors.Dim, "x"), settings.Hint("x"));
    }

    // ---------------------------------------------------------------------------------------------
    // The highlight-theme cache
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void HighlightCode_RebuildsTheCliHighlightThemeWhenTheCurrentThemeChanges()
    {
        SyntaxHighlight.HighlighterOverride = () =>
            new ScriptedHighlighter("<span class=\"hljs-keyword\">x</span>");
        var dark = ThemeHelpers.HighlightCode("x", "javascript");

        ThemeRuntime.InitTheme("light", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
        var light = ThemeHelpers.HighlightCode("x", "javascript");

        Assert.NotEqual(dark, light);
        Assert.Equal(new[] { Current.Fg(ThemeColors.SyntaxKeyword, "x") }, light);
    }
}
