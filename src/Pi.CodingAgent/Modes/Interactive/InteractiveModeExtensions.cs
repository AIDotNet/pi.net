// ============================================================================
// Extension-binding helpers for InteractiveMode.
// Port of interactive-mode.ts's setExtensionWidget (L2398-2427), getCrashExtensionHint (L2125-2137),
// showExtensionConfirm (L2620-2626), the extension shortcut handler's error formatting (L2277-2281),
// the UI context's setTheme persistence (L2660-2671) and pasteToEditor / getEditorText shorthands
// (L2655-2657).
//
// Everything here is a decision or a small transform; the bindings themselves stay in the class
// (4f-4m) because they wire callbacks into `session`, `editor` and `ui`.
// ============================================================================

using Pi.CodingAgent.Core;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>Port of the extension-binding helpers of <c>interactive-mode.ts</c>.</summary>
public static class InteractiveModeExtensions
{
    /// <summary>
    /// TS <c>InteractiveMode.MAX_WIDGET_LINES</c>: how many lines of a string-array widget survive
    /// before the rest are dropped in favour of a truncation note.
    /// </summary>
    public const int MaxWidgetLines = 10;

    /// <summary>TS <c>"... (widget truncated)"</c>.</summary>
    public const string WidgetTruncationNote = "... (widget truncated)";

    /// <summary>TS <c>"Yes"</c> / <c>"No"</c> — the options <c>showExtensionConfirm</c> offers.</summary>
    public static IReadOnlyList<string> ConfirmOptions { get; } = ["Yes", "No"];

    /// <summary>
    /// TS <c>showExtensionWidget</c>'s string-array branch: keep the first
    /// <see cref="MaxWidgetLines"/> lines and say whether the remainder was dropped.
    /// </summary>
    public static (IReadOnlyList<string> Lines, bool Truncated) WidgetContent(IReadOnlyList<string> content)
    {
        var truncated = content.Count > MaxWidgetLines;
        return (content.Take(MaxWidgetLines).ToList(), truncated);
    }

    /// <summary>
    /// TS <c>showExtensionConfirm</c>: the selector shows <c>{title}\n{message}</c>. The returned
    /// string is <c>Yes</c> or <c>No</c>, and <see cref="IsConfirmed"/> maps it back to a bool.
    /// </summary>
    public static string ConfirmTitle(string title, string message) => $"{title}\n{message}";

    /// <summary>TS <c>showExtensionConfirm</c>'s <c>result === "Yes"</c>.</summary>
    public static bool IsConfirmed(string? result) => result is "Yes";

    /// <summary>
    /// TS <c>getCrashExtensionHint</c>: the stack frames matched against the loaded extensions, run
    /// through <see cref="CrashLog.FormatCrashExtensionHint"/>. The whole thing is wrapped in a
    /// try/catch upstream, so any throw collapses to <c>undefined</c> rather than breaking the crash
    /// report the user is about to see.
    /// </summary>
    public static string? GetCrashExtensionHint(string? stack, IReadOnlyList<ExtensionStackMetadata> extensions)
    {
        try
        {
            return InteractiveModeSupport.FormatCrashExtensionHint(CrashLog.FindExtensionStackMatches(stack, extensions));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// TS's extension shortcut handler's catch: <c>Shortcut handler error: {message}</c>. The
    /// handler must not block input, so errors surface as chat notices instead.
    /// </summary>
    public static string ShortcutErrorMessage(string message) => $"Shortcut handler error: {message}";

    /// <summary>
    /// TS's UI-context <c>setTheme(name)</c>: persist the new name only when the controller accepted
    /// it **and** it differs from the recorded setting. Rejections leave the setting alone, and
    /// setting the same name is a no-op rather than a write.
    /// </summary>
    public static bool ShouldPersistThemeName(bool succeeded, string? recordedSetting, string requestedName) =>
        succeeded && recordedSetting != requestedName;
}
