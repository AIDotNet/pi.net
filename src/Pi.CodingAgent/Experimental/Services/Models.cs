// ============================================================================
// Models service contract — port of experimental/services/models.ts (4g-3b)
// ============================================================================
//
// The worker exposes the model catalog and the selected model/thinking level as replicated state, plus
// the mutations a presentation may ask for. `refresh` reports a warning variant when a provider catalog
// could not be fetched, carrying one message per failing provider.

using Pi.Ai.Types;
using Pi.Chord;
using Pi.Chord.Facets;
using Pi.Chord.Context;
using Pi.Chord.Services;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>A provider/model pair. TS <c>ModelRef</c>.</summary>
public record ModelRef(string Provider, string ModelId);

/// <summary>A model the worker can select. TS <c>ModelSummary extends ModelRef</c>.</summary>
public sealed record ModelSummary(string Provider, string ModelId, string Name, bool Reasoning)
    : ModelRef(Provider, ModelId);

/// <summary>
/// The catalog refresh status. TS is a union of <c>{ status: "idle" | "refreshing" | "done" }</c> and
/// <c>{ status: "warning"; errors }</c>; the port keeps one record per variant so the payload is
/// type-safe (C315).
/// </summary>
public abstract record ModelsRefreshState
{
    /// <summary>Nothing has been refreshed yet.</summary>
    public sealed record Idle : ModelsRefreshState;

    /// <summary>A refresh is running.</summary>
    public sealed record Refreshing : ModelsRefreshState;

    /// <summary>The last refresh finished cleanly.</summary>
    public sealed record Done : ModelsRefreshState;

    /// <summary>The last refresh finished with per-provider errors (provider id → message).</summary>
    public sealed record Warning(IReadOnlyDictionary<string, string> Errors) : ModelsRefreshState;
}

/// <summary>The catalog half of <see cref="ModelsState"/>.</summary>
public sealed record ModelsCatalogState(long Revision, IReadOnlyList<ModelSummary> AvailableModels);

/// <summary>The selection half of <see cref="ModelsState"/>.</summary>
public sealed record ModelsConfigurationState(ModelRef? Model, ThinkingLevel ThinkingLevel);

/// <summary>The replicated model state. TS <c>ModelsState</c>.</summary>
public sealed record ModelsState(
    ModelsCatalogState Catalog,
    ModelsConfigurationState Configuration,
    ModelsRefreshState Refresh);

/// <summary>Port of the TS <c>Models</c> interface.</summary>
public interface IModels
{
    /// <summary>The replicated catalog + selection state.</summary>
    IReplicatedState<ModelsState> State { get; }

    /// <summary>Advance the thinking level to the next supported one.</summary>
    Task CycleThinkingAsync(Context context);

    /// <summary>The thinking levels the selected model supports.</summary>
    Task<IReadOnlyList<ThinkingLevel>> GetThinkingLevelsAsync(Context context);

    /// <summary>Re-fetch the provider catalogs.</summary>
    Task RefreshAsync(Context context);

    /// <summary>Select a model.</summary>
    Task SelectAsync(ModelRef model, Context context);

    /// <summary>Select a thinking level.</summary>
    Task SelectThinkingAsync(ThinkingLevel level, Context context);
}

/// <summary>TS <c>export const Models = defineService&lt;Models&gt;("pi.models")</c>.</summary>
public static class Models
{
    /// <summary>The chord service handle for <see cref="IModels"/>.</summary>
    public static readonly Service<IModels> Service = Api.DefineService<IModels>("pi.models");
}
