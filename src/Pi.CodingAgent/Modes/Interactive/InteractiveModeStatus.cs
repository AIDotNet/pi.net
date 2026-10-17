// ============================================================================
// Status-indicator helpers for InteractiveMode.
// Port of interactive-mode.ts's isWorkingStatusEditor (L221-228) and the decisions inside
// showWorkingStatusIndicator / setWorkingIndicator / setWorkingVisible / setHiddenThinkingLabel /
// clearStatusIndicator (L2290-2375, L2338-2375, L4495 area).
//
// The container shuffling itself (statusContainer.clear / addChild / editor rebinding) stays in the
// class (4f-4m); everything here is the part worth pinning in isolation.
// ============================================================================

using Pi.Ai.Types;
using Pi.CodingAgent.Modes.Interactive.Components;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>Port of the status-indicator helpers of <c>interactive-mode.ts</c>.</summary>
public static class InteractiveModeStatus
{
    /// <summary>
    /// TS <c>isWorkingStatusEditor(editor)</c>. The TS guard is duck-typed (<c>"embedWorkingStatus"
    /// in editor &amp;&amp; … === true &amp;&amp; typeof setWorkingStatusIndicator === "function"</c>);
    /// in C# only <see cref="CustomEditor"/> carries those members, so the type test is equivalent.
    /// </summary>
    public static bool IsWorkingStatusEditor(object? editor) =>
        editor is CustomEditor { EmbedWorkingStatus: true };

    /// <summary>
    /// TS <c>showWorkingStatusIndicator</c>'s <c>colorFn</c>: <see langword="null"/> (the indicator
    /// then uses the accent/muted defaults) unless the editor embeds the working status, in which
    /// case the spinner follows the editor's border colour, falling back to the thinking-level
    /// border colour.
    /// </summary>
    /// <remarks>
    /// C266: TS's <c>borderColor?: (str: string) =&gt; string</c> starts out <c>undefined</c>, so the
    /// <c>??</c> fallback is the normal path there. C#'s <see cref="CustomEditor.BorderColor"/> is a
    /// non-nullable property that <see cref="Editor"/> initialises to the theme's border colour, so
    /// the fallback is only reachable when a caller explicitly clears it. The branch is kept so the
    /// two implementations still read the same.
    /// </remarks>
    public static Func<string, string>? ResolveWorkingStatusColor(object? editor, Theme theme, ThinkingLevel level)
    {
        if (editor is not CustomEditor { EmbedWorkingStatus: true } customEditor)
        {
            return null;
        }

        return customEditor.BorderColor ?? theme.GetThinkingBorderColor(level);
    }

    /// <summary>TS <c>this.workingMessage ?? this.defaultWorkingMessage</c>.</summary>
    public static string ResolveWorkingMessage(string? workingMessage, string defaultWorkingMessage) =>
        workingMessage ?? defaultWorkingMessage;

    /// <summary>TS <c>label ?? this.defaultHiddenThinkingLabel</c>.</summary>
    public static string ResolveHiddenThinkingLabel(string? label, string defaultLabel) => label ?? defaultLabel;

    /// <summary>
    /// TS <c>clearStatusIndicator</c>'s idle placeholder rule: re-add <c>IdleStatus</c> only when
    /// something was actually cleared, it was not embedded in the editor, the TUI is in regular mode
    /// and clear-on-shrink is on (otherwise the placeholder would leave a permanent gap).
    /// </summary>
    public static bool ShouldRestoreIdleStatus(
        bool hadIndicator,
        bool wasEmbedded,
        bool isRegularTuiMode,
        bool clearOnShrink) =>
        hadIndicator && !wasEmbedded && isRegularTuiMode && clearOnShrink;

    /// <summary>
    /// TS <c>resetExtensionUI</c>'s working-message reset: the default message plus the interrupt
    /// hint, used when the cleared indicator was the working one.
    /// </summary>
    public static string DefaultWorkingMessageWithInterrupt(string defaultWorkingMessage) =>
        $"{defaultWorkingMessage} ({KeybindingHints.KeyText("app.interrupt")} to interrupt)";
}
