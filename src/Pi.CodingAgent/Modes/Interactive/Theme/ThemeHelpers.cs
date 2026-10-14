// ============================================================================
// TUI helpers — port of the helper half of modes/interactive/theme/theme.ts
// ============================================================================

using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>Port of the TUI-helper half of <c>theme.ts</c>: syntax highlighting and component themes.</summary>
public static class ThemeHelpers
{
    private static Theme? _cachedHighlightThemeFor;
    private static IReadOnlyDictionary<string, HighlightFormatter>? _cachedCliHighlightTheme;

    /// <summary>Scope name to formatter, the shape <c>SyntaxHighlight.Highlight</c> consumes.</summary>
    private static IReadOnlyDictionary<string, HighlightFormatter> BuildCliHighlightTheme(Theme theme) =>
        new Dictionary<string, HighlightFormatter>(StringComparer.Ordinal)
        {
            ["keyword"] = text => theme.Fg(ThemeColors.SyntaxKeyword, text),
            ["built_in"] = text => theme.Fg(ThemeColors.SyntaxType, text),
            ["literal"] = text => theme.Fg(ThemeColors.SyntaxNumber, text),
            ["number"] = text => theme.Fg(ThemeColors.SyntaxNumber, text),
            ["regexp"] = text => theme.Fg(ThemeColors.SyntaxString, text),
            ["string"] = text => theme.Fg(ThemeColors.SyntaxString, text),
            ["subst"] = text => theme.Fg(ThemeColors.Text, text),
            ["comment"] = text => theme.Fg(ThemeColors.SyntaxComment, text),
            ["doctag"] = text => theme.Fg(ThemeColors.SyntaxComment, text),
            ["meta"] = text => theme.Fg(ThemeColors.Muted, text),
            ["function"] = text => theme.Fg(ThemeColors.SyntaxFunction, text),
            ["title"] = text => theme.Fg(ThemeColors.SyntaxFunction, text),
            ["class"] = text => theme.Fg(ThemeColors.SyntaxType, text),
            ["type"] = text => theme.Fg(ThemeColors.SyntaxType, text),
            ["tag"] = text => theme.Fg(ThemeColors.SyntaxPunctuation, text),
            ["name"] = text => theme.Fg(ThemeColors.SyntaxKeyword, text),
            ["attr"] = text => theme.Fg(ThemeColors.SyntaxVariable, text),
            ["variable"] = text => theme.Fg(ThemeColors.SyntaxVariable, text),
            ["params"] = text => theme.Fg(ThemeColors.SyntaxVariable, text),
            ["operator"] = text => theme.Fg(ThemeColors.SyntaxOperator, text),
            ["punctuation"] = text => theme.Fg(ThemeColors.SyntaxPunctuation, text),
            ["emphasis"] = theme.Italic,
            ["strong"] = theme.Bold,
            ["link"] = theme.Underline,
            ["addition"] = text => theme.Fg(ThemeColors.ToolDiffAdded, text),
            ["deletion"] = text => theme.Fg(ThemeColors.ToolDiffRemoved, text),
        };

    private static IReadOnlyDictionary<string, HighlightFormatter> GetCliHighlightTheme(Theme theme)
    {
        if (!ReferenceEquals(_cachedHighlightThemeFor, theme) || _cachedCliHighlightTheme is null)
        {
            _cachedHighlightThemeFor = theme;
            _cachedCliHighlightTheme = BuildCliHighlightTheme(theme);
        }
        return _cachedCliHighlightTheme;
    }

    /// <summary>
    /// Highlight code with syntax coloring based on file extension or language. Returns the highlighted
    /// lines.
    /// </summary>
    public static string[] HighlightCode(string code, string? lang = null)
    {
        // Validate language before highlighting to avoid stderr spam from the tokenizer.
        var validLang = !string.IsNullOrEmpty(lang) && SyntaxHighlight.SupportsLanguage(lang) ? lang : null;

        // Skip highlighting when no valid language is specified. The tokenizer's auto-detection is
        // unreliable and can misidentify prose as a programming language, coloring random English words
        // as keywords.
        var theme = ThemeRuntime.Current;
        if (validLang is null)
        {
            return code.Split('\n').Select(line => theme.Fg(ThemeColors.MdCodeBlock, line)).ToArray();
        }

        var options = new HighlightOptions
        {
            Language = validLang,
            IgnoreIllegals = true,
            Theme = GetCliHighlightTheme(theme),
        };
        try
        {
            return SyntaxHighlight.Highlight(code, options).Split('\n');
        }
        catch
        {
            // Note: TS returns the *uncolored* lines here, unlike `MarkdownTheme.highlightCode`.
            return code.Split('\n');
        }
    }

    private static readonly IReadOnlyDictionary<string, string> ExtensionToLanguage =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ts"] = "typescript",
            ["tsx"] = "typescript",
            ["js"] = "javascript",
            ["jsx"] = "javascript",
            ["mjs"] = "javascript",
            ["cjs"] = "javascript",
            ["py"] = "python",
            ["rb"] = "ruby",
            ["rs"] = "rust",
            ["go"] = "go",
            ["java"] = "java",
            ["kt"] = "kotlin",
            ["swift"] = "swift",
            ["c"] = "c",
            ["h"] = "c",
            ["cpp"] = "cpp",
            ["cc"] = "cpp",
            ["cxx"] = "cpp",
            ["hpp"] = "cpp",
            ["cs"] = "csharp",
            ["php"] = "php",
            ["sh"] = "bash",
            ["bash"] = "bash",
            ["zsh"] = "bash",
            ["fish"] = "fish",
            ["ps1"] = "powershell",
            ["sql"] = "sql",
            ["html"] = "html",
            ["htm"] = "html",
            ["css"] = "css",
            ["scss"] = "scss",
            ["sass"] = "sass",
            ["less"] = "less",
            ["json"] = "json",
            ["yaml"] = "yaml",
            ["yml"] = "yaml",
            ["toml"] = "toml",
            ["xml"] = "xml",
            ["md"] = "markdown",
            ["markdown"] = "markdown",
            ["dockerfile"] = "dockerfile",
            ["makefile"] = "makefile",
            ["cmake"] = "cmake",
            ["lua"] = "lua",
            ["perl"] = "perl",
            ["r"] = "r",
            ["scala"] = "scala",
            ["clj"] = "clojure",
            ["ex"] = "elixir",
            ["exs"] = "elixir",
            ["erl"] = "erlang",
            ["hs"] = "haskell",
            ["ml"] = "ocaml",
            ["vim"] = "vim",
            ["graphql"] = "graphql",
            ["proto"] = "protobuf",
            ["tf"] = "hcl",
            ["hcl"] = "hcl",
        };

    /// <summary>
    /// Get the language identifier from a file path extension. A path without a dot uses its whole name,
    /// which is how <c>Makefile</c> and <c>Dockerfile</c> resolve.
    /// </summary>
    public static string? GetLanguageFromPath(string filePath)
    {
        var ext = filePath.Split('.').LastOrDefault()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
        {
            return null;
        }
        return ExtensionToLanguage.TryGetValue(ext, out var language) ? language : null;
    }

    /// <summary>The markdown theme, bound to the current theme.</summary>
    public static MarkdownTheme GetMarkdownTheme()
    {
        var theme = ThemeRuntime.Current;
        return new MarkdownTheme
        {
            Heading = text => theme.Fg(ThemeColors.MdHeading, text),
            Link = text => theme.Fg(ThemeColors.MdLink, text),
            LinkUrl = text => theme.Fg(ThemeColors.MdLinkUrl, text),
            Code = text => theme.Fg(ThemeColors.MdCode, text),
            CodeBlock = text => theme.Fg(ThemeColors.MdCodeBlock, text),
            CodeBlockBorder = text => theme.Fg(ThemeColors.MdCodeBlockBorder, text),
            Quote = text => theme.Fg(ThemeColors.MdQuote, text),
            QuoteBorder = text => theme.Fg(ThemeColors.MdQuoteBorder, text),
            Hr = text => theme.Fg(ThemeColors.MdHr, text),
            ListBullet = text => theme.Fg(ThemeColors.MdListBullet, text),
            Bold = theme.Bold,
            Italic = theme.Italic,
            Underline = theme.Underline,
            Strikethrough = theme.Strikethrough,
            HighlightCode = (code, lang) =>
            {
                // Validate language before highlighting to avoid stderr spam from the tokenizer.
                var validLang = !string.IsNullOrEmpty(lang) && SyntaxHighlight.SupportsLanguage(lang) ? lang : null;

                // Skip highlighting when no valid language is specified. The tokenizer's auto-detection is
                // unreliable and can misidentify prose as a programming language.
                if (validLang is null)
                {
                    return code.Split('\n').Select(line => theme.Fg(ThemeColors.MdCodeBlock, line)).ToArray();
                }

                var options = new HighlightOptions
                {
                    Language = validLang,
                    IgnoreIllegals = true,
                    Theme = GetCliHighlightTheme(theme),
                };
                try
                {
                    return SyntaxHighlight.Highlight(code, options).Split('\n');
                }
                catch
                {
                    // Unlike `highlightCode`, the fallback keeps the code-block coloring.
                    return code.Split('\n').Select(line => theme.Fg(ThemeColors.MdCodeBlock, line)).ToArray();
                }
            },
        };
    }

    /// <summary>The select-list theme, bound to the current theme.</summary>
    public static SelectListTheme GetSelectListTheme()
    {
        var theme = ThemeRuntime.Current;
        return new SelectListTheme
        {
            SelectedPrefix = text => theme.Fg(ThemeColors.Accent, text),
            SelectedText = text => theme.Fg(ThemeColors.Accent, text),
            Description = text => theme.Fg(ThemeColors.Muted, text),
            ScrollInfo = text => theme.Fg(ThemeColors.Muted, text),
            NoMatch = text => theme.Fg(ThemeColors.Muted, text),
        };
    }

    /// <summary>The editor theme, bound to the current theme.</summary>
    public static EditorTheme GetEditorTheme() => new()
    {
        BorderColor = text => ThemeRuntime.Current.Fg(ThemeColors.BorderMuted, text),
        SelectList = GetSelectListTheme(),
    };

    /// <summary>The settings-list theme, bound to the current theme.</summary>
    public static SettingsListTheme GetSettingsListTheme()
    {
        var theme = ThemeRuntime.Current;
        return new SettingsListTheme
        {
            Label = (text, selected) => selected ? theme.Fg(ThemeColors.Accent, text) : text,
            Value = (text, selected) => selected
                ? theme.Fg(ThemeColors.Accent, text)
                : theme.Fg(ThemeColors.Muted, text),
            Description = text => theme.Fg(ThemeColors.Dim, text),
            Cursor = theme.Fg(ThemeColors.Accent, "→ "),
            Hint = text => theme.Fg(ThemeColors.Dim, text),
        };
    }

    /// <summary>Drop the highlight-theme cache. Not in TS; used by tests that swap the current theme.</summary>
    internal static void ResetCachesForTests()
    {
        _cachedHighlightThemeFor = null;
        _cachedCliHighlightTheme = null;
    }
}
