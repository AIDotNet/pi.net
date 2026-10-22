// ============================================================================
// Models service provider — port of experimental/services/models-provider.ts (4g-3b)
// ============================================================================
//
// The facet that serves a conversation's model catalog and selection as the `pi.models` service. The
// conversation's replicated `pi.agent` document is the source of truth for the selection: every change
// — including those made by other clients — is mirrored into the replicated state.
//
// TS builds the facet with `defineFacet({ id, setup })`; C# implements `IFacet` directly (C317).
// The decision leaves TS keeps inside the factory closure are `internal static` here so the suite can
// pin them without a live durable harness (the convention this port follows for providers).

using Pi.Ai.Models;
using Pi.Ai.Types;
// `Pi.CodingAgent.Core.ThinkingLevels` is a thin wrapper of the same two helpers; alias the Ai one
// so the call sites name the class TS imports from `@earendil-works/pi-ai`.
using AiThinkingLevels = Pi.Ai.Models.ThinkingLevels;
using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Pi.CodingAgent.Core;
using Pi.Durable.Harness;
using Pi.Durable.Types;
using HarnessModelRef = Pi.Durable.Harness.ModelRef;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>Options for <see cref="ModelsProvider.CreateModelsServiceFacetAsync"/>.</summary>
public sealed record ModelsServiceFacetOptions
{
    public required IHarness Harness { get; init; }

    public required IConversation Conversation { get; init; }

    /// <summary>Absent when no runtime was configured (the service then reports an empty catalog).</summary>
    public ModelRuntime? ModelRuntime { get; init; }

    public SettingsManager? SettingsManager { get; init; }

    public required Context Context { get; init; }
}

/// <summary>
/// The built service plus its lifecycle hooks. TS <c>ModelsServiceRuntime</c>: the service itself,
/// an activation hook that publishes the first catalog, and a hook that mirrors the agent document's
/// selection into the replicated state.
/// </summary>
public sealed class ModelsServiceRuntime
{
    public required IModels Service { get; init; }

    public required Func<Context, Task> ActivateAsync { get; init; }

    public required Action<Context> SyncConfiguration { get; init; }
}

/// <summary>Port of <c>createModelsService</c> / <c>createModelsServiceFacet</c>.</summary>
public static class ModelsProvider
{
    /// <summary>
    /// Acquire the conversation's agent document, then build the facet that owns it. The facet id is
    /// <c>@pi/models</c>.
    /// </summary>
    public static async Task<IFacet> CreateModelsServiceFacetAsync(ModelsServiceFacetOptions options)
    {
        var watch = await options.Harness
            .WatchDocAsync(AgentDocs.AgentDoc, options.Conversation.Id, options.Context)
            .ConfigureAwait(false);
        if (watch is null)
        {
            throw new InvalidOperationException(
                $"Conversation {options.Conversation.Id.Value} has no agent document");
        }

        // 上游移除了 documentState()；这里用 agent 文档的精确帧 watch 重建只读复制状态。
        return new ModelsFacet(WatchState.Attach<IReadOnlyDictionary<string, object?>?>(watch), options);
    }

    /// <summary>
    /// Build the service over one conversation. <paramref name="createState"/> is the facet
    /// environment's replicated-state factory (TS passes <c>env.replicatedState</c>).
    /// </summary>
    public static ModelsServiceRuntime CreateModelsService(
        IConversation conversation,
        AttachedReplicatedState<IReadOnlyDictionary<string, object?>?> agent,
        ModelRuntime? modelRuntime,
        SettingsManager? settingsManager,
        Func<ModelsState, MutableReplicatedState<ModelsState>> createState)
    {
        var runtime = new ModelsService(conversation, agent, modelRuntime, settingsManager, createState);
        return new ModelsServiceRuntime
        {
            Service = runtime,
            ActivateAsync = runtime.ActivateAsync,
            SyncConfiguration = runtime.SyncConfiguration,
        };
    }

    // ---------------------------------------------------------------- decision leaves

    /// <summary>
    /// TS <c>configurationOf</c>: the selection half of the state. An unset model stays
    /// <see langword="null"/> (TS <c>undefined</c>) and an unset thinking level reads as
    /// <see cref="ThinkingLevel.Off"/> (TS <c>"off"</c>).
    /// </summary>
    internal static ModelsConfigurationState ConfigurationOf(AgentState? value) => new(
        value?.Model is { } model ? new ModelRef(model.Provider, model.ModelId) : null,
        value?.ThinkingLevel ?? ThinkingLevel.Off);

    /// <summary>
    /// TS <c>levels[(levels.indexOf(current) + 1) % levels.length] ?? "off"</c>: an empty list makes
    /// the modulo NaN, which indexes to undefined and falls back to <c>"off"</c>; a level the model
    /// does not support indexes to -1 and therefore wraps to the first entry.
    /// </summary>
    internal static string NextThinkingLevel(IReadOnlyList<string> levels, string current)
        => levels.Count == 0 ? "off" : levels[(IndexOf(levels, current) + 1) % levels.Count];

    /// <summary>TS <c>includesModel</c>: provider id and model id must both match.</summary>
    internal static bool IncludesModel(IReadOnlyList<ModelSpec> models, ModelSpec selected)
        => models.Any(model => model.Provider == selected.Provider && model.Id == selected.Id);

    /// <summary>
    /// TS <c>readCatalog</c>: the selected model is appended when the snapshot does not carry it, and
    /// every read bumps the revision (TS increments the closure counter before building the object).
    /// </summary>
    internal static ModelsCatalogState BuildCatalog(
        long revision, IReadOnlyList<ModelSpec> available, ModelSpec? selected)
    {
        var catalog = selected is null || IncludesModel(available, selected)
            ? available
            : [.. available, selected];
        var models = new List<ModelSummary>(catalog.Count);
        foreach (var model in catalog)
        {
            models.Add(new ModelSummary(model.Provider, model.Id, model.Name, model.Reasoning));
        }

        return new ModelsCatalogState(revision + 1, models);
    }

    /// <summary>
    /// TS <c>Object.fromEntries([...refresh.errors].map(([id, error]) => [id, error.message]))</c>:
    /// one message per failing provider. The entry order follows the refresh result, matching the
    /// <c>Record</c> insertion order TS observes.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> RefreshErrors(ModelsRefreshResult result)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (providerId, error) in result.Errors)
        {
            errors[providerId] = error.Message;
        }

        return errors;
    }

    /// <summary>
    /// The thinking-level helpers only ever return the seven TS literals, so the parse cannot fail;
    /// the guard keeps the null-forgiving operator honest if that ever changes.
    /// </summary>
    internal static ThinkingLevel ParseLevel(string text) => ThinkingLevelText.Parse(text)
        ?? throw new InvalidOperationException($"Unknown thinking level: {text}");

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var index = 0; index < list.Count; index++)
        {
            if (list[index] == value) return index;
        }

        return -1;
    }

    /// <summary>The facet itself: id <c>@pi/models</c>, providing <see cref="Models.Service"/>.</summary>
    private sealed class ModelsFacet(
        AttachedReplicatedState<IReadOnlyDictionary<string, object?>?> agent,
        ModelsServiceFacetOptions options) : IFacet
    {
        public string Id => "@pi/models";

        public void Setup(IFacetEnvironment environment)
        {
            // TS: env.own(() => agent.dispose()).
            environment.Own(() =>
            {
                agent.Dispose();
                return Task.CompletedTask;
            });

            var runtime = CreateModelsService(
                options.Conversation,
                agent,
                options.ModelRuntime,
                options.SettingsManager,
                initial => environment.ReplicatedState(initial));
            environment.Provide(Models.Service, runtime.Service);

            environment.OnActivate(async () =>
            {
                await runtime.ActivateAsync(Context.Background).ConfigureAwait(false);
                var subscription = agent.Subscribe((_, context, _) =>
                {
                    runtime.SyncConfiguration(context);
                    return null;
                });
                // TS: env.own(agent.subscribe(…)).
                environment.Own(() =>
                {
                    subscription.Dispose();
                    return Task.CompletedTask;
                });
            });
        }
    }

    /// <summary>
    /// The <see cref="IModels"/> implementation over one conversation's agent document. Like every
    /// service in this layer it is both the typed local view and the wire member dictionary (C325);
    /// the member names are the TS property names.
    /// </summary>
    private sealed class ModelsService(
        IConversation conversation,
        AttachedReplicatedState<IReadOnlyDictionary<string, object?>?> agent,
        ModelRuntime? modelRuntime,
        SettingsManager? settingsManager,
        Func<ModelsState, MutableReplicatedState<ModelsState>> createState)
        : ServiceWireAdapter, IModels
    {
        protected override IEnumerable<KeyValuePair<string, object>> WireMembers() =>
        [
            State("state", _state),
            Method("cycleThinking", CycleThinkingWire),
            Method("getThinkingLevels", GetThinkingLevelsWire),
            Method("refresh", RefreshWire),
            Method("select", SelectWire),
            Method("selectThinking", SelectThinkingWire),
        ];

        private Task<object?> CycleThinkingWire(object?[] args)
            => VoidWire(CycleThinkingAsync(ContextOf(args)));

        private async Task<object?> GetThinkingLevelsWire(object?[] args)
        {
            var levels = await GetThinkingLevelsAsync(ContextOf(args)).ConfigureAwait(false);
            return ServiceWireValues.ThinkingLevels(levels);
        }

        private Task<object?> RefreshWire(object?[] args) => VoidWire(RefreshAsync(ContextOf(args)));

        private Task<object?> SelectWire(object?[] args)
            => VoidWire(SelectAsync(ServiceWireValues.Read<ModelRef>(args[0]), ContextOf(args)));

        private async Task<object?> SelectThinkingWire(object?[] args)
        {
            // TS sends the level as its literal.
            var level = ThinkingLevelText.Parse(ServiceWireValues.Text(args[0]))
                ?? throw new ArgumentException($"Unknown thinking level: {ServiceWireValues.Text(args[0])}");
            await SelectThinkingAsync(level, ContextOf(args)).ConfigureAwait(false);
            return null;
        }

        private static async Task<object?> VoidWire(Task call)
        {
            await call.ConfigureAwait(false);
            return null;
        }

        private readonly MutableReplicatedState<ModelsState> _state = createState(new ModelsState(
            new ModelsCatalogState(0, []),
            ConfigurationOf(null),
            new ModelsRefreshState.Idle()));
        private long _catalogRevision;

        public IReplicatedState<ModelsState> State => _state;

        public Task CycleThinkingAsync(Context context)
        {
            var levels = ReadThinkingLevels();
            var current = ThinkingLevelText.ToText(AgentValue?.ThinkingLevel ?? ThinkingLevel.Off);
            var next = NextThinkingLevel(levels, current);
            return conversation.ConfigureAsync(
                new AgentChange { ThinkingLevel = ParseLevel(next) }, context);
        }

        public Task<IReadOnlyList<ThinkingLevel>> GetThinkingLevelsAsync(Context context)
        {
            var levels = ReadThinkingLevels();
            var parsed = new List<ThinkingLevel>(levels.Count);
            foreach (var level in levels)
            {
                if (ThinkingLevelText.Parse(level) is { } typed) parsed.Add(typed);
            }

            return Task.FromResult<IReadOnlyList<ThinkingLevel>>(parsed);
        }

        public async Task RefreshAsync(Context context)
        {
            _state.Replace(context, _state.Value with { Refresh = new ModelsRefreshState.Refreshing() });

            // TS: modelRuntime?.refresh({ signal }) — an absent runtime means "nothing to refresh"
            // and therefore no errors.
            IReadOnlyDictionary<string, string> errors;
            if (modelRuntime is null)
            {
                errors = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            else
            {
                var result = await modelRuntime.RefreshAsync(new ModelsRefreshOptions
                {
                    Signal = context.AbortSignal ?? default,
                }).ConfigureAwait(false);
                errors = RefreshErrors(result);
            }

            var catalog = ReadCatalog();
            _state.Replace(context, _state.Value with
            {
                Catalog = catalog,
                Refresh = errors.Count == 0
                    ? new ModelsRefreshState.Done()
                    : new ModelsRefreshState.Warning(errors),
            });
        }

        public async Task SelectAsync(ModelRef model, Context context)
        {
            var selected = modelRuntime?.GetModel(model.Provider, model.ModelId);
            if (selected is null)
            {
                throw new InvalidOperationException($"Unknown model: {model.Provider}/{model.ModelId}");
            }

            var level = AiThinkingLevels.Clamp(
                selected, ThinkingLevelText.ToText(AgentValue?.ThinkingLevel ?? ThinkingLevel.Off));
            await conversation.ConfigureAsync(new AgentChange
            {
                Model = new HarnessModelRef(selected.Provider, selected.Id),
                ThinkingLevel = ParseLevel(level),
            }, context).ConfigureAwait(false);

            settingsManager?.SetDefaultModelAndProvider(selected.Provider, selected.Id);
            if (settingsManager is not null)
            {
                await settingsManager.FlushAsync().ConfigureAwait(false);
            }
        }

        public Task SelectThinkingAsync(ThinkingLevel level, Context context)
        {
            var levels = ReadThinkingLevels();
            var text = ThinkingLevelText.ToText(level);
            if (!levels.Contains(text, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Thinking level {text} is unavailable; choose one of: {string.Join(", ", levels)}");
            }

            return conversation.ConfigureAsync(new AgentChange { ThinkingLevel = level }, context);
        }

        /// <summary>TS <c>activate(context)</c>: publish the first catalog and reset the refresh status.</summary>
        internal Task ActivateAsync(Context context)
        {
            var catalog = ReadCatalog();
            _state.Replace(context, _state.Value with
            {
                Catalog = catalog,
                Refresh = new ModelsRefreshState.Idle(),
            });
            return Task.CompletedTask;
        }

        /// <summary>
        /// TS <c>syncConfiguration(context)</c>: mirror the agent document's selection, skipping the
        /// publish when nothing changed (the check TS performs before touching the draft).
        /// </summary>
        internal void SyncConfiguration(Context context)
        {
            var next = ConfigurationOf(AgentValue);
            var current = _state.Value.Configuration;
            if (current.Model?.Provider == next.Model?.Provider
                && current.Model?.ModelId == next.Model?.ModelId
                && current.ThinkingLevel == next.ThinkingLevel)
            {
                return;
            }

            _state.Replace(context, _state.Value with { Configuration = next });
        }

        /// <summary>The agent document's typed value (TS <c>agent.value</c>, an <c>AgentState | null</c>).</summary>
        private AgentState? AgentValue => AgentDocs.StateFromJson(agent.Value);

        private ModelSpec? SelectedModel()
        {
            var reference = AgentValue?.Model;
            return reference is null ? null : modelRuntime?.GetModel(reference.Provider, reference.ModelId);
        }

        private IReadOnlyList<string> ReadThinkingLevels()
        {
            var selected = SelectedModel();
            return selected is null ? ["off"] : AiThinkingLevels.GetSupported(selected);
        }

        private ModelsCatalogState ReadCatalog()
        {
            var selected = SelectedModel();
            var available = modelRuntime?.GetAvailableSnapshot() ?? [];
            var catalog = BuildCatalog(_catalogRevision, available, selected);
            _catalogRevision = catalog.Revision;
            return catalog;
        }
    }
}
