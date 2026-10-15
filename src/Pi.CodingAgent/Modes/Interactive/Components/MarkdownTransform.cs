// ============================================================================
// Markdown transform chain for message components.
// Port of modes/interactive/components/markdown-transform.ts
// ============================================================================

using Pi.CodingAgent.Core.Extensions;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Builds the <see cref="Pi.Tui.Components.MarkdownOptions.Transform"/> callback for message
/// components: the extension-registered markdown transformer chain, applied in registration order.
/// Port of <c>createMarkdownTransform</c>.
/// </summary>
public static class MarkdownTransform
{
    /// <summary>
    /// TS <c>createMarkdownTransform(messageType, isStreaming, transformers)</c>. Returns the
    /// transform callback passed to <see cref="Pi.Tui.Components.MarkdownOptions.Transform"/>.
    /// </summary>
    public static Func<string, int, string> Create(
        string messageType,
        bool isStreaming,
        IReadOnlyList<MarkdownTransformer> transformers) =>
        (markdown, availableWidth) => Apply(
            markdown,
            new MarkdownTransformContext
            {
                MessageType = messageType,
                IsStreaming = isStreaming,
                AvailableWidth = availableWidth,
            },
            transformers);

    // TS: a transformer returning a non-string leaves the markdown unchanged; a throwing
    // transformer is skipped so the rest of the chain still runs.
    private static string Apply(
        string markdown,
        MarkdownTransformContext context,
        IReadOnlyList<MarkdownTransformer> transformers)
    {
        var transformed = markdown;
        foreach (var transformer in transformers)
        {
            try
            {
                var result = transformer(transformed, context);
                if (result is not null)
                {
                    transformed = result;
                }
            }
            catch
            {
                // Keep the current markdown and continue with the next transformer.
            }
        }

        return transformed;
    }
}
