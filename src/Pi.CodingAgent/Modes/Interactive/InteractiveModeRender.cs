// ============================================================================
// Session-rendering helpers for InteractiveMode.
// Port of interactive-mode.ts's module-level isUnknownModel (L327-329), the RenderSessionItem
// type guards (L277-289), renderSessionEntries' item folding (L4072-4085), the tool-call result
// branch of renderSessionItems (L4026-4044) and getUserMessageText (L3774-3781).
//
// The component construction itself stays in the class (4f-4m); these are the decisions the
// folding and the result-injection depend on.
// ============================================================================

using System.Text;
using Pi.Ai.Types;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>TS <c>compaction_cost</c> / <c>branch_summary</c> — the kinds that carry a usage notice.</summary>
public static class CompactionCostKind
{
    public const string Compaction = "compaction";

    public const string BranchSummary = "branch_summary";
}

/// <summary>
/// TS <c>CompactionCostNotice</c>: a synthetic item appended after a compaction or branch-summary
/// turn so the user sees what the summarisation cost.
/// </summary>
public sealed record CompactionCostNotice
{
    /// <summary>One of <see cref="CompactionCostKind"/>.</summary>
    public required string Kind { get; init; }

    public required Usage Usage { get; init; }
}

/// <summary>Port of the session-rendering helpers of <c>interactive-mode.ts</c>.</summary>
public static class InteractiveModeRender
{
    /// <summary>
    /// TS <c>isUnknownModel</c>: the placeholder model every field of which is literally
    /// <c>"unknown"</c>. Used to decide whether a warning about the model makes sense.
    /// </summary>
    public static bool IsUnknownModel(Model? model) =>
        model is not null && model.Provider == "unknown" && model.Id == "unknown" && model.Api == "unknown";

    /// <summary>
    /// TS <c>renderSessionEntries</c>' first fold: entries rendered as themselves instead of through
    /// their context messages — custom entries, and usage entries that warmed the cache.
    /// </summary>
    public static bool IsRenderedDirectly(string entryType, string? usageKind) =>
        entryType == "custom" || (entryType == "usage" && usageKind == "cache_warm");

    /// <summary>
    /// TS <c>renderSessionEntries</c>' second fold: a compaction or branch-summary entry with usage
    /// and at least one message gets a <see cref="CompactionCostNotice"/> appended after the messages.
    /// </summary>
    public static bool ShouldAppendCompactionCost(string entryType, bool hasUsage, int messageCount) =>
        (entryType == CompactionCostKind.Compaction || entryType == CompactionCostKind.BranchSummary)
        && hasUsage
        && messageCount > 0;

    /// <summary>
    /// TS's tool-call branch: aborted and errored assistant messages get their tool components
    /// closed with an error result instead of being registered as pending.
    /// </summary>
    public static bool IsFailedStopReason(string stopReason) =>
        stopReason is "aborted" or "error";

    /// <summary>
    /// TS's error text for a closed tool component: aborts mention the retry count (pluralised),
    /// errors echo the message, and an empty message still reads <c>"Error"</c>.
    /// </summary>
    public static string ResolveToolResultErrorMessage(string stopReason, string? errorMessage, int retryAttempt)
    {
        if (stopReason == "aborted")
        {
            return retryAttempt > 0
                ? $"Aborted after {retryAttempt} retry attempt{(retryAttempt > 1 ? "s" : "")}"
                : "Operation aborted";
        }

        return string.IsNullOrEmpty(errorMessage) ? "Error" : errorMessage;
    }

    /// <summary>
    /// TS <c>getUserMessageText</c>: the concatenated text blocks of a user message, or
    /// <see cref="string.Empty"/> for anything else. The result is also what goes into the editor
    /// history, so an image-only message contributes nothing.
    /// </summary>
    public static string GetUserMessageText(ChatMessage? message)
    {
        if (message is not UserMessage user)
        {
            return "";
        }

        var text = new StringBuilder();
        foreach (var block in user.Content.ToBlocks())
        {
            if (block is TextContent textContent)
            {
                text.Append(textContent.Text);
            }
        }

        return text.ToString();
    }
}
