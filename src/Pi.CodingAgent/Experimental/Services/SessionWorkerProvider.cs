// ============================================================================
// Session worker services — port of experimental/services/worker.ts (4g-3b)
// ============================================================================
//
// Assembles the worker side of the experimental session services: two runtime facets (the agent
// controller and the session-plugin reload hook), the models and transcript facets, plus whatever the
// plugin loader contributes. The result is a `SessionWorkerServices` that serves remote calls per
// (server connection, attachment) scope, each scope getting its own endpoint so its subscriptions can
// be dropped independently.
//
// The plugin reload is chained through a tail promise: concurrent reloads run one at a time, and a
// failed reload leaves the previous generation loaded.

using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Pi.CodingAgent.Core;
using Pi.Durable.Harness;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>What <see cref="SessionWorkerProvider.CreateSessionWorkerServicesAsync"/> needs.</summary>
public sealed record SessionWorkerOptions
{
    public required IHarness Harness { get; init; }

    /// <summary>The root conversation the services expose.</summary>
    public required IConversation Conversation { get; init; }

    public ModelRuntime? ModelRuntime { get; init; }

    public SettingsManager? SettingsManager { get; init; }

    /// <summary>Plugin facets; the default is an empty static loader.</summary>
    public IFacetLoader? FacetLoader { get; init; }

    /// <summary>TS <c>publish(scope, subscriptionId, update)</c>: forward a provider update.</summary>
    public required Func<WorkerServiceScope, string, ServiceProviderUpdate, Task> Publish { get; init; }
}

/// <summary>Which consumer a set of subscriptions belongs to. TS <c>WorkerServiceScope</c>.</summary>
public sealed record WorkerServiceScope(string ServerConnectionId, string AttachmentId);

/// <summary>
/// The worker-side service surface. TS <c>SessionWorkerServices</c>: one call entry point, a way to
/// drop a consumer's subscriptions, and disposal.
/// </summary>
public sealed class SessionWorkerServices
{
    public required Func<ServiceCall, WorkerServiceScope, Context, Task<object?>> Invoke { get; init; }

    public required Action<Func<WorkerServiceScope, bool>> RemoveSubscriptions { get; init; }

    public required Func<Task> Dispose { get; init; }

    public Task<object?> InvokeAsync(ServiceCall call, WorkerServiceScope scope, Context context)
        => Invoke(call, scope, context);

    public Task DisposeAsync() => Dispose();
}

/// <summary>Port of <c>createSessionWorkerServices</c>.</summary>
public static class SessionWorkerProvider
{
    /// <summary>
    /// Build the worker's services over one root conversation. Throws when the built-in facets fail to
    /// start, after attempting to clean up whatever was loaded.
    /// </summary>
    public static async Task<SessionWorkerServices> CreateSessionWorkerServicesAsync(
        SessionWorkerOptions options)
    {
        var agentControllerRuntimeFacet = new AgentControllerRuntimeFacet(options);
        var sessionPlugins = new SessionPluginsState();
        var pluginRuntimeFacet = new SessionPluginsRuntimeFacet(sessionPlugins);

        var builtins = await Api.CreateStaticFacetLoader(
        [
            agentControllerRuntimeFacet,
            pluginRuntimeFacet,
            await ModelsProvider.CreateModelsServiceFacetAsync(new ModelsServiceFacetOptions
            {
                Harness = options.Harness,
                Conversation = options.Conversation,
                ModelRuntime = options.ModelRuntime,
                SettingsManager = options.SettingsManager,
                Context = Context.Background,
            }).ConfigureAwait(false),
            await TranscriptProvider.CreateTranscriptServiceFacetAsync(
                options.Conversation, Context.Background).ConfigureAwait(false),
        ]).LoadAsync().ConfigureAwait(false);

        var pluginLoader = options.FacetLoader ?? Api.CreateStaticFacetLoader([]);
        var loadedPlugins = await pluginLoader.LoadAsync().ConfigureAwait(false);

        FacetHost facetHost;
        try
        {
            facetHost = await Api.CreateFacetHostAsync(new FacetOptions
            {
                Facets = [.. builtins.Facets, .. loadedPlugins.Facets],
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var cleanupErrors = await FacetLoader.DisposeLoadedFacetsAsync(
                [loadedPlugins, builtins]).ConfigureAwait(false);
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException("Session facets failed to start and clean up",
                    [error, .. cleanupErrors]);
            }

            throw;
        }

        // The reload hook the plugin runtime facet serves. Until the first reload it rejects, exactly
        // like the TS default.
        var reloadTail = Task.CompletedTask;
        sessionPlugins.Reload = () =>
        {
            var operation = reloadTail.ContinueWith(async _ =>
            {
                var candidate = await pluginLoader.LoadAsync().ConfigureAwait(false);
                try
                {
                    await facetHost.ReloadAsync(candidate.Facets).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    try
                    {
                        await candidate.Dispose().ConfigureAwait(false);
                    }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException(
                            "Session plugin reload and cleanup failed", error, cleanupError);
                    }

                    throw;
                }

                var retired = loadedPlugins;
                loadedPlugins = candidate;
                await retired.Dispose().ConfigureAwait(false);
            }).Unwrap();
            reloadTail = operation.ContinueWith(
                _ => { }, System.Threading.Tasks.TaskScheduler.Default);
            return operation;
        };

        var provider = facetHost.Services;
        var endpoints = new Dictionary<string, (WorkerServiceScope Scope, RemoteServiceEndpoint Endpoint)>();

        void RemoveSubscriptions(Func<WorkerServiceScope, bool> matches)
        {
            foreach (var (key, entry) in endpoints.ToArray())
            {
                if (!matches(entry.Scope)) continue;
                entry.Endpoint.Dispose();
                endpoints.Remove(key);
            }
        }

        return new SessionWorkerServices
        {
            Invoke = (call, scope, context) =>
            {
                var key = ServiceScopeKey(scope);
                if (!endpoints.TryGetValue(key, out var entry))
                {
                    entry = (scope, RemoteServiceEndpoint.Create(provider));
                    endpoints[key] = entry;
                }

                return InvokeAsync(entry.Endpoint, call, options, scope, context);
            },
            RemoveSubscriptions = RemoveSubscriptions,
            Dispose = async () =>
            {
                RemoveSubscriptions(_ => true);
                await reloadTail.ConfigureAwait(false);
                var errors = new List<Exception>();
                try
                {
                    await facetHost.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }

                errors.AddRange(await FacetLoader.DisposeLoadedFacetsAsync(
                    [loadedPlugins, builtins]).ConfigureAwait(false));
                if (errors.Count == 1) throw errors[0];
                if (errors.Count > 1)
                {
                    throw new AggregateException("Failed to dispose Session facets", errors);
                }
            },
        };

        static async Task<object?> InvokeAsync(
            RemoteServiceEndpoint endpoint,
            ServiceCall call,
            SessionWorkerOptions options,
            WorkerServiceScope scope,
            Context context)
        {
            // TS: (subscriptionId, update) => options.publish(scope, subscriptionId, update) — the
            // delivery context the endpoint passes is dropped.
            var (_, result) = await endpoint.InvokeAsync(
                call,
                (subscriptionId, update, _) => options.Publish(scope, subscriptionId, update),
                context).ConfigureAwait(false);
            return result;
        }
    }

    /// <summary>TS <c>serviceScopeKey</c>: the two ids joined by NUL.</summary>
    internal static string ServiceScopeKey(WorkerServiceScope scope)
        => $"{scope.ServerConnectionId}\0{scope.AttachmentId}";

    /// <summary>TS <c>@pi/agent-controller-runtime</c>: provides the controller.</summary>
    private sealed class AgentControllerRuntimeFacet(SessionWorkerOptions options) : IFacet
    {
        public string Id => "@pi/agent-controller-runtime";

        public void Setup(IFacetEnvironment environment)
            => environment.Provide(AgentController.Service,
                AgentControllerProvider.Create(options.Harness, options.Conversation));
    }

    /// <summary>
    /// TS <c>@pi/session-plugins-runtime</c>: provides <see cref="ISessionPlugins"/>, whose
    /// <c>reload</c> forwards to whatever hook the worker installed (initially a rejecting stub).
    /// </summary>
    private sealed class SessionPluginsRuntimeFacet(SessionPluginsState state) : IFacet
    {
        public string Id => "@pi/session-plugins-runtime";

        public void Setup(IFacetEnvironment environment)
            => environment.Provide(SessionPlugins.Service, new SessionPluginsService(state));
    }

    /// <summary>
    /// The mutable reload hook. TS reassigns the closure variable; C# holds it in this holder, seeded
    /// with the rejecting stub TS starts from.
    /// </summary>
    private sealed class SessionPluginsState
    {
        public Func<Task> Reload { get; set; } =
            () => Task.FromException(new InvalidOperationException("Session plugins are not ready"));
    }

    private sealed class SessionPluginsService(SessionPluginsState state)
        : ServiceWireAdapter, ISessionPlugins
    {
        protected override IEnumerable<KeyValuePair<string, object>> WireMembers()
            => [Method("reload", ReloadWire)];

        private async Task<object?> ReloadWire(object?[] args)
        {
            await ReloadAsync(ContextOf(args)).ConfigureAwait(false);
            return null;
        }

        public Task ReloadAsync(Context context) => state.Reload();
    }
}
