// ============================================================================
// Theme tokens — port of the type unions at the top of modes/interactive/theme/theme.ts
// ============================================================================
//
// TS models these as string-literal unions, so the port keeps plain strings and groups them in
// constant classes (the same choice Core/Keybindings.cs makes for keybinding ids). The JSON in a theme
// file is keyed by these exact strings, and the system-theme recipe in SystemTheme.cs is keyed by them
// too, so strings keep both directions direct.

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>
/// The background a theme is designed for. TS <c>TerminalTheme</c> / <c>ThemeAppearance</c>
/// (<c>"dark" | "light"</c>).
/// </summary>
public static class ThemeAppearance
{
    public const string Dark = "dark";
    public const string Light = "light";

    /// <summary>Whether <paramref name="value"/> is a usable appearance.</summary>
    public static bool IsValid(string? value) => value is Dark or Light;
}

/// <summary>
/// Foreground tokens. TS <c>ThemeColor</c>; the 49 values a theme's <c>colors</c> map may use in the
/// foreground slot.
/// </summary>
public static class ThemeColors
{
    // Core UI
    public const string Accent = "accent";
    public const string Border = "border";
    public const string BorderAccent = "borderAccent";
    public const string BorderMuted = "borderMuted";
    public const string Success = "success";
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Muted = "muted";
    public const string Dim = "dim";
    public const string Text = "text";
    public const string ThinkingText = "thinkingText";

    // Scrollbar
    public const string ScrollbarTrack = "scrollbarTrack";
    public const string ScrollbarThumb = "scrollbarThumb";

    // Backgrounds & content text
    public const string SearchMatchText = "searchMatchText";
    public const string UserMessageText = "userMessageText";
    public const string CustomMessageText = "customMessageText";
    public const string CustomMessageLabel = "customMessageLabel";
    public const string ToolTitle = "toolTitle";
    public const string ToolOutput = "toolOutput";

    // Markdown
    public const string MdHeading = "mdHeading";
    public const string MdLink = "mdLink";
    public const string MdLinkUrl = "mdLinkUrl";
    public const string MdCode = "mdCode";
    public const string MdCodeBlock = "mdCodeBlock";
    public const string MdCodeBlockBorder = "mdCodeBlockBorder";
    public const string MdQuote = "mdQuote";
    public const string MdQuoteBorder = "mdQuoteBorder";
    public const string MdHr = "mdHr";
    public const string MdListBullet = "mdListBullet";

    // Tool diffs
    public const string ToolDiffAdded = "toolDiffAdded";
    public const string ToolDiffRemoved = "toolDiffRemoved";
    public const string ToolDiffContext = "toolDiffContext";

    // Syntax highlighting
    public const string SyntaxComment = "syntaxComment";
    public const string SyntaxKeyword = "syntaxKeyword";
    public const string SyntaxFunction = "syntaxFunction";
    public const string SyntaxVariable = "syntaxVariable";
    public const string SyntaxString = "syntaxString";
    public const string SyntaxNumber = "syntaxNumber";
    public const string SyntaxType = "syntaxType";
    public const string SyntaxOperator = "syntaxOperator";
    public const string SyntaxPunctuation = "syntaxPunctuation";

    // Thinking-level borders
    public const string ThinkingOff = "thinkingOff";
    public const string ThinkingMinimal = "thinkingMinimal";
    public const string ThinkingLow = "thinkingLow";
    public const string ThinkingMedium = "thinkingMedium";
    public const string ThinkingHigh = "thinkingHigh";
    public const string ThinkingXhigh = "thinkingXhigh";
    public const string ThinkingMax = "thinkingMax";

    // Bash mode
    public const string BashMode = "bashMode";

    /// <summary>Every foreground token, in the order the TS union lists them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Accent,
        Border,
        BorderAccent,
        BorderMuted,
        Success,
        Error,
        Warning,
        Muted,
        Dim,
        Text,
        ThinkingText,
        ScrollbarTrack,
        ScrollbarThumb,
        SearchMatchText,
        UserMessageText,
        CustomMessageText,
        CustomMessageLabel,
        ToolTitle,
        ToolOutput,
        MdHeading,
        MdLink,
        MdLinkUrl,
        MdCode,
        MdCodeBlock,
        MdCodeBlockBorder,
        MdQuote,
        MdQuoteBorder,
        MdHr,
        MdListBullet,
        ToolDiffAdded,
        ToolDiffRemoved,
        ToolDiffContext,
        SyntaxComment,
        SyntaxKeyword,
        SyntaxFunction,
        SyntaxVariable,
        SyntaxString,
        SyntaxNumber,
        SyntaxType,
        SyntaxOperator,
        SyntaxPunctuation,
        ThinkingOff,
        ThinkingMinimal,
        ThinkingLow,
        ThinkingMedium,
        ThinkingHigh,
        ThinkingXhigh,
        ThinkingMax,
        BashMode,
    ];

    /// <summary>Foreground tokens a theme may omit; the loader fills them from another token.</summary>
    public static readonly IReadOnlySet<string> Optional = new HashSet<string>(StringComparer.Ordinal)
    {
        ScrollbarTrack,
        ScrollbarThumb,
        ThinkingMax,
        SearchMatchText,
    };
}

/// <summary>
/// Background tokens. TS <c>ThemeBg</c>; the 7 values a theme's <c>colors</c> map may use in the
/// background slot.
/// </summary>
public static class ThemeBackgrounds
{
    public const string SelectedBg = "selectedBg";
    public const string SearchMatchBg = "searchMatchBg";
    public const string UserMessageBg = "userMessageBg";
    public const string CustomMessageBg = "customMessageBg";
    public const string ToolPendingBg = "toolPendingBg";
    public const string ToolSuccessBg = "toolSuccessBg";
    public const string ToolErrorBg = "toolErrorBg";

    /// <summary>Every background token. TS <c>BACKGROUND_TOKENS</c>.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        SelectedBg,
        SearchMatchBg,
        UserMessageBg,
        CustomMessageBg,
        ToolPendingBg,
        ToolSuccessBg,
        ToolErrorBg,
    ];

    /// <summary>Background tokens a theme may omit; the loader fills it from another token.</summary>
    public static readonly IReadOnlySet<string> Optional = new HashSet<string>(StringComparer.Ordinal)
    {
        SearchMatchBg,
    };
}

/// <summary>
/// Every theme token. TS <c>ThemeToken</c> (<c>ThemeColor | ThemeBg</c>).
/// </summary>
/// <remarks>
/// Tokens are only accepted in their own slot, because <c>""</c> (terminal default) means the default
/// foreground or background depending on the slot. Use <c>theme.colors[token]</c> to use a token's color
/// in the other slot.
/// </remarks>
public static class ThemeTokens
{
    /// <summary>Every token, foregrounds first. TS <c>Object.keys(TOKEN_FAMILIES)</c> order differs; this list is the union.</summary>
    public static readonly IReadOnlyList<string> All = [.. ThemeColors.All, .. ThemeBackgrounds.All];

    /// <summary>Every background token, as a set.</summary>
    public static readonly IReadOnlySet<string> Backgrounds =
        new HashSet<string>(ThemeBackgrounds.All, StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ColorSet =
        new HashSet<string>(ThemeColors.All, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="token"/> is a foreground token.</summary>
    public static bool IsColor(string token) => ColorSet.Contains(token);

    /// <summary>Whether <paramref name="token"/> is a background token.</summary>
    public static bool IsBackground(string token) => Backgrounds.Contains(token);

    /// <summary>Whether <paramref name="token"/> is any theme token.</summary>
    public static bool IsToken(string token) => ColorSet.Contains(token) || Backgrounds.Contains(token);
}
