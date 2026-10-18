// ============================================================================
// Presentation UI service contract — port of experimental/services/presentation-ui.ts (4g-3b)
// ============================================================================
//
// The narrow, process-local UI surface a presentation facet may use. `local: true` means the service
// is never routed to another process: a select dialog only makes sense in the client that owns the
// terminal.

using Pi.Chord;
using Pi.Chord.Facets;
using Pi.Chord.Context;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>One row of a select dialog. TS <c>PresentationSelectItem</c>.</summary>
public sealed record PresentationSelectItem(string Value, string Label, string? Description = null);

/// <summary>
/// Narrow process-local UI capabilities available to presentation facets.
/// TS <c>PresentationUI</c>.
/// </summary>
public interface IPresentationUI
{
    /// <summary>Ask the user to pick one item; <see langword="null"/> when cancelled.</summary>
    Task<string?> SelectAsync(
        string title,
        IReadOnlyList<PresentationSelectItem> items,
        string? selectedValue,
        Context context);

    /// <summary>Show a transient status line.</summary>
    void ShowStatus(string message, Context context);
}

/// <summary>TS <c>export const PresentationUI = defineService&lt;PresentationUI&gt;(…, { local: true })</c>.</summary>
public static class PresentationUI
{
    /// <summary>The chord service handle for <see cref="IPresentationUI"/>.</summary>
    public static readonly Service<IPresentationUI> Service =
        Api.DefineService<IPresentationUI>("pi.local.presentation-ui", local: true);
}
