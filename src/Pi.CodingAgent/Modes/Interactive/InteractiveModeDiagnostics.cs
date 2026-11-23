// ============================================================================
// Terminal title, chat notices and crash-report messaging for InteractiveMode.
// Port of interactive-mode.ts's updateTerminalTitle / showError / showWarning / showStatus /
// crashReportInstructions / suggestBugReport / maybeSuggestBugReport / maybeShowInstallChangeWarning /
// updateEditorBorderColor.
//
// In TS these read `this.session`, `this.ui.terminal`, the module-level `theme` and two one-shot
// flags. The decision logic and the strings are extracted here so they can be tested without an
// InteractiveMode; the class itself (4f-4m) keeps the fields and does the DOM work.
// ============================================================================

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.Ai.Types;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>Port of the terminal-title, chat-notice and crash-messaging helpers of <c>interactive-mode.ts</c>.</summary>
public static partial class InteractiveModeDiagnostics
{
    /// <summary>TS <c>/\b(?:abort(?:ed)?|cancel(?:l?ed)?)\b/i</c>.</summary>
    [GeneratedRegex(@"\b(?:abort(?:ed)?|cancel(?:l?ed)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AbortOrCancelRegex();

    /// <summary>
    /// TS <c>updateTerminalTitle()</c>: <c>"{APP_TITLE} - [{sessionName} - ]{cwd basename}"</c>.
    /// </summary>
    public static string BuildTerminalTitle(string cwd, string? sessionName)
    {
        var cwdBasename = NodePath.Basename(cwd);
        return string.IsNullOrEmpty(sessionName)
            ? $"{Config.AppTitle} - {cwdBasename}"
            : $"{Config.AppTitle} - {sessionName} - {cwdBasename}";
    }

    // ---------------------------------------------------------------- chat notices

    /// <summary>
    /// A chat notice as TS's <c>showError</c>/<c>showWarning</c>/<c>showStatus</c> build it: the text
    /// (prefix included), the theme token it is colored with, and the horizontal padding.
    /// </summary>
    public sealed record ChatNotice(string Text, string ColorToken, int Pad);

    /// <summary>
    /// TS <c>showError()</c>. Note the padding is <c>outputPad</c>, unlike the other two notices which
    /// hardcode <c>1</c>.
    /// </summary>
    public static ChatNotice ErrorNotice(string errorMessage, int outputPad) =>
        new($"Error: {errorMessage}", "error", outputPad);

    /// <summary>TS <c>showWarning()</c>: hardcoded padding of 1, ignoring <c>outputPad</c>.</summary>
    public static ChatNotice WarningNotice(string warningMessage) =>
        new($"Warning: {warningMessage}", "warning", 1);

    /// <summary>TS <c>showStatus()</c>: dim, no prefix, hardcoded padding of 1.</summary>
    public static ChatNotice StatusNotice(string message) => new(message, "dim", 1);

    /// <summary>
    /// TS <c>showStatus()</c>'s coalescing guard: when the last two chat children are still the
    /// tracked status text and spacer, the previous line is updated in place instead of appending.
    /// </summary>
    public static bool ShouldCoalesceStatus(int childCount, bool lastIsTrackedText, bool secondLastIsTrackedSpacer) =>
        childCount > 1 && lastIsTrackedText && secondLastIsTrackedSpacer;

    // ---------------------------------------------------------------- editor border

    /// <summary>
    /// TS <c>updateEditorBorderColor()</c>: bash mode wins over the thinking level; otherwise the
    /// border follows the current thinking level (TS falls back to <c>"off"</c> for a missing level).
    /// </summary>
    public static Func<string, string> ResolveEditorBorderColor(Theme theme, bool isBashMode, ThinkingLevel level) =>
        isBashMode ? theme.GetBashModeBorderColor() : theme.GetThinkingBorderColor(level);

    // ---------------------------------------------------------------- bug report hint

    /// <summary>TS <c>suggestBugReport()</c>'s line.</summary>
    public static string BugReportHint() =>
        $"If this looks like a {Config.AppName} bug, /bug sends a report to the developers.";

    /// <summary>TS <c>crashReportInstructions()</c>.</summary>
    public static string CrashReportInstructions(bool hasSessionFile)
    {
        // The TS string hardcodes "start pi and" for the no-session case, so APP_NAME does not apply.
        var resume = hasSessionFile
            ? $"run `{Config.AppName} -r` to resume the session, then"
            : "start pi and";
        return $"To report this crash: {resume} run /bug. The crash details are attached automatically.";
    }

    /// <summary>
    /// TS <c>maybeSuggestBugReport(message)</c>'s guard: a non-retryable error that is not an
    /// abort/cancel. The caller handles the install-change branch first and the one-shot flag.
    /// </summary>
    public static bool ShouldSuggestBugReport(string? stopReason, bool isRetryable, string? errorMessage) =>
        stopReason == "error" && !isRetryable && !IsAbortOrCancelMessage(errorMessage);

    /// <summary>TS's abort/cancel test on the error message.</summary>
    public static bool IsAbortOrCancelMessage(string? errorMessage) =>
        errorMessage is not null && AbortOrCancelRegex().IsMatch(errorMessage);

    /// <summary>TS <c>maybeShowInstallChangeWarning()</c>'s message.</summary>
    public static string BuildInstallChangeWarning(InstallChange change, string? resumeCommand)
    {
        var cause = change.Kind == "updated"
            ? $"{Config.AppName} was updated to {change.Version} while this session was running ({Config.Version})"
            : $"The {Config.AppName} installation this session runs from was removed or replaced";
        var restart = resumeCommand is not null
            ? $"Restart with `{resumeCommand}` to continue this session."
            : $"Restart {Config.AppName}.";
        return $"{cause}. Features that load code on demand can fail until restart. {restart}";
    }

    // ---------------------------------------------------------------- dropped reasoning

    /// <summary>provider 报告从请求中扣掉的 reasoning 块摘要。对应 TS <c>DroppedReasoning</c>。</summary>
    public sealed record DroppedReasoning(
        string DiagnosticType, string Provider, int Count, string Noun, IReadOnlyList<string> Reasons);

    /// <summary>按助手诊断类型记录的扣减项：provider 名、被扣条目的 type、单复数名词。</summary>
    private sealed record ReasoningDropKind(
        string Provider, string DroppedType, string Singular, string Plural);

    /// <summary>列出 provider 从模型中扣掉的 reasoning 的助手诊断类型。对应 TS <c>REASONING_DROP_DIAGNOSTICS</c>。</summary>
    private static readonly Dictionary<string, ReasoningDropKind> ReasoningDropKinds =
        new(StringComparer.Ordinal)
        {
            ["anthropic_input_transformations"] = new(
                "Anthropic", "thinking_dropped", "thinking block", "thinking blocks"),
            ["openai_input_transformations"] = new(
                "OpenAI", "reasoning", "reasoning item", "reasoning items"),
        };

    /// <summary>
    /// 统计生成本条消息的请求里，provider 报告扣掉的 reasoning 块及其原因。对应 TS
    /// <c>getDroppedReasoning</c>；会话数据里的诊断 details 是无类型 JSON。
    /// </summary>
    public static DroppedReasoning? GetDroppedReasoning(AssistantMessage message)
    {
        foreach (var diagnostic in message.Diagnostics ?? [])
        {
            if (!ReasoningDropKinds.TryGetValue(diagnostic.Type, out var kind))
            {
                continue;
            }
            if (diagnostic.Details?["transformations"] is not JsonArray transformations)
            {
                continue;
            }

            var count = 0;
            var reasons = new List<string>();
            foreach (var transformation in transformations)
            {
                if (transformation is not JsonObject obj
                    || !obj.TryGetPropertyValue("type", out var type)
                    || type?.ToString() != kind.DroppedType)
                {
                    continue;
                }
                count++;
                if (obj.TryGetPropertyValue("reason", out var reason)
                    && reason?.ToString() is { Length: > 0 } text
                    && !reasons.Contains(text))
                {
                    reasons.Add(text);
                }
            }
            if (count > 0)
            {
                var noun = count == 1 ? kind.Singular : kind.Plural;
                return new DroppedReasoning(diagnostic.Type, kind.Provider, count, noun, reasons);
            }
        }

        return null;
    }

    /// <summary>扣减通报文本：<c>{provider} dropped {n} {noun}{: reasons} (details in session)</c>。</summary>
    public static string ReasoningDropNoticeText(DroppedReasoning dropped)
    {
        var reasons = dropped.Reasons.Count > 0 ? $": {string.Join(", ", dropped.Reasons)}" : "";
        return $"{dropped.Provider} dropped {dropped.Count} {dropped.Noun}{reasons} (details in session)";
    }
}
