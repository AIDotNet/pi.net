// ============================================================================
// Search haystacks for the model selectors.
// Port of modes/interactive/model-search.ts
// ============================================================================

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
public static class ModelSearch
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

    /// <summary>TS <c>item.name ? ` ${item.name}` : ""</c> — a truthy check, so an empty name adds nothing.</summary>
    private static string NameSuffix(ModelSearchItem item) =>
        string.IsNullOrEmpty(item.Name) ? "" : $" {item.Name}";
}
