// ============================================================================
// Search haystacks for the model selectors.
// Port of modes/interactive/model-search.ts
// ============================================================================

using System.Text.RegularExpressions;
using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>
/// TS <c>ModelSearchItem</c>: the three fields both haystack builders read.
/// </summary>
public sealed record ModelSearchItem
{
    public required string Id { get; init; }

    public required string Provider { get; init; }

    /// <summary>TS <c>name?: string</c>. Falsy (null or empty) contributes nothing to the haystack.</summary>
    public string? Name { get; init; }
}

/// <summary>Port of <c>getModelSearchText</c> / <c>getModelSelectorSearchText</c>.</summary>
public static partial class ModelSearch
{
    /// <summary>
    /// The scoped-models haystack: the bare model ID leads, so <c>"gpt-5"</c> ranks above
    /// <c>"openai/gpt-5"</c>-style proxy IDs.
    /// </summary>
    public static string GetModelSearchText(ModelSearchItem item) =>
        $"{item.Id} {item.Provider} {item.Provider}/{item.Id} {item.Provider} {item.Id}{NameSuffix(item)}";

    /// <summary>
    /// The <c>/model</c> haystack: the provider leads, so an exact provider-prefixed query
    /// (<c>"openai gpt-5"</c>) ranks before the proxy-provider ID <c>openrouter/openai/gpt-5</c>.
    /// </summary>
    public static string GetModelSelectorSearchText(ModelSearchItem item) =>
        $"{item.Provider} {item.Provider}/{item.Id} {item.Provider} {item.Id}{NameSuffix(item)}";

    /// <summary>
    /// The model name when it has words the ID does not, e.g. a renamed override like
    /// "MiniMax M3 [rasmus@openrouter, parasail]" for "minimax/minimax-m3". Names that only restate the
    /// ID with different casing, separators, or a dropped namespace/version suffix return null.
    /// Port of <c>getDistinctModelName</c> (TS <c>606af8662</c>).
    /// </summary>
    public static string? GetDistinctModelName(string id, string? name)
    {
        var trimmed = name is null ? null : Pi.Tui.JsString.Trim(name);
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var idWords = SplitModelLabel(id);
        var idTokens = new HashSet<string>(idWords, StringComparer.Ordinal);
        var compactId = string.Concat(idWords);
        // Single-character words must match a whole ID token: "4" is not covered by "mistral-small-2603".
        bool IsCovered(string word) =>
            idTokens.Contains(word) || (word.Length > 1 && compactId.Contains(word, StringComparison.Ordinal));
        return SplitModelLabel(trimmed).All(IsCovered) ? null : trimmed;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex SplitSeparatorRegex();

    private static List<string> SplitModelLabel(string value) =>
        [.. SplitSeparatorRegex().Split(Pi.Tui.JsString.ToLowerCase(value))
            .Where(part => part.Length > 0)];

    /// <summary>TS <c>item.name ? ` ${item.name}` : ""</c> — a truthy check, so an empty name adds nothing.</summary>
    private static string NameSuffix(ModelSearchItem item) =>
        string.IsNullOrEmpty(item.Name) ? "" : $" {item.Name}";
}
