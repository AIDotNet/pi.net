// ============================================================================
// Experimental server services — port of experimental/services/server.ts (4g-3c)
// ============================================================================
//
// The server side of the experimental session services: a routed service host that, per attached
// presentation, publishes the session directory (replicated state), session management, and the
// presentation plugins. Every mutation runs through a serialised tail so a create/remove/attach never
// interleaves with another, and each successful create/remove refreshes the directory.
//
// Unlike the worker's services (typed classes behind the chord interface), the services here are
// inline object literals in TS, so the C# side provides member dictionaries directly — the shape the
// chord provider classifies.

using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Pi.Server;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>What <see cref="ExperimentalServerProvider.CreateExperimentalServerServicesAsync"/> needs.</summary>
public sealed record ExperimentalServerOptions
{
    public required Func<Context, Task<IReadOnlyList<SessionSummary>>> ListAsync { get; init; }

    public required Func<SessionCreateOptions, Context, Task<SessionSummary>> CreateAsync { get; init; }

    public required Func<string, Context, Task> RemoveAsync { get; init; }

    public required Func<string, IReadOnlyList<string>?, Context, Task<PrepareSessionAnswer>> PrepareSessionPluginsAsync { get; init; }

    public required Func<IReadOnlyList<string>, Context, Task<object?>> ReloadPresentationPluginsAsync { get; init; }
}

/// <summary>The answer of <see cref="ExperimentalServerOptions.PrepareSessionPluginsAsync"/>.</summary>
public sealed record PrepareSessionAnswer(
    IReadOnlyList<string> PackagePaths,
    object? PresentationPlugins);

/// <summary>TS <c>ExperimentalServerServices</c>.</summary>
public sealed class ExperimentalServerServices
{
    public required IRoutedServerServiceHost Host { get; init; }

    public required Func<Context?, Task> Refresh { get; init; }

    public required Func<Task> Dispose { get; init; }

    public Task RefreshAsync(Context? context = null) => Refresh(context);

    public Task DisposeAsync() => Dispose();
}

/// <summary>Port of <c>createExperimentalServerServices</c>.</summary>
public static class ExperimentalServerProvider
{
    /// <summary>
    /// Build the server-side services. Throws when releasing the attachments during disposal fails
    /// (a single error is rethrown as-is, several become an aggregate).
    /// </summary>
    public static async Task<ExperimentalServerServices> CreateExperimentalServerServicesAsync(
        ExperimentalServerOptions options)
    {
        var revision = 1L;
        var directory = new MutableReplicatedState<SessionDirectoryState>(new SessionDirectoryState(
            revision,
            await options.ListAsync(Context.Background).ConfigureAwait(false)));
        var attachments = new HashSet<ServerServiceAttachment>();
        var mutationTail = Task.CompletedTask;

        async Task RefreshNowAsync(Context context)
        {
            var sessions = await options.ListAsync(context).ConfigureAwait(false);
            revision += 1;
            directory.Replace(context, directory.Value with
            {
                Revision = revision,
                Sessions = sessions,
            });
        }

        // TS: mutationTail.catch(() => {}).then(operation) — a failed predecessor never blocks the next
        // operation, and the tail records completion either way so mutations stay serialised.
        Task<T> Serialize<T>(Func<Task<T>> operation)
        {
            var result = mutationTail.ContinueWith(_ => operation()).Unwrap();
            mutationTail = result.ContinueWith(
                _ => { }, System.Threading.Tasks.TaskScheduler.Default);
            return result;
        }

        IRoutedServerServiceAttachment AttachClient(IRoutedServerPresentation presentation, Context context)
        {
            var preparedPluginPackagePaths = (IReadOnlyList<string>?)null;
            var provider = new RemoteServiceProvider(
            [
                new RemoteServiceProvider.ProviderDefinition(SessionDirectory.Service.Id),
                new RemoteServiceProvider.ProviderDefinition(SessionManagement.Service.Id),
                new RemoteServiceProvider.ProviderDefinition(PresentationPlugins.Service.Id),
            ]);

            provider.Provide(SessionDirectory.Service.Id, new Dictionary<string, object>
            {
                ["state"] = MutableReplicatedState<SessionDirectoryState>.GetInternals(directory)
                    ?? throw new InvalidOperationException("Session directory state exposes no internals"),
            });

            provider.Provide(PresentationPlugins.Service.Id, new Dictionary<string, object>
            {
                ["prepareSession"] = (Func<object?[], object?>)(args => Serialize<object?>(async () =>
                {
                    var request = ServiceWireValues.Read<PrepareSessionRequest>(args[0]);
                    var answer = await options.PrepareSessionPluginsAsync(
                        request.SessionId, request.PackagePaths, ServiceWireValues.ContextOf(args)).ConfigureAwait(false);
                    preparedPluginPackagePaths = answer.PackagePaths;
                    return (object?)ServiceWireValues.PrepareSessionAnswer(
                        answer.PackagePaths, answer.PresentationPlugins);
                })),
                ["reload"] = (Func<object?[], object?>)(args => Serialize<object?>(async () =>
                {
                    if (preparedPluginPackagePaths is null)
                    {
                        throw new InvalidOperationException("No Session plugin selection is prepared");
                    }

                    return await options.ReloadPresentationPluginsAsync(
                        preparedPluginPackagePaths, ServiceWireValues.ContextOf(args)).ConfigureAwait(false);
                })),
            });

            provider.Provide(SessionManagement.Service.Id, new Dictionary<string, object>
            {
                ["create"] = (Func<object?[], object?>)(args => Serialize<object?>(async () =>
                {
                    var createOptions = ServiceWireValues.Read<SessionCreateOptions>(args[0]);
                    var created = await options.CreateAsync(createOptions, ServiceWireValues.ContextOf(args)).ConfigureAwait(false);
                    await RefreshNowAsync(ServiceWireValues.ContextOf(args)).ConfigureAwait(false);
                    return (object?)ServiceWireValues.SessionSummary(created);
                })),
                ["remove"] = (Func<object?[], object?>)(args => Serialize<object?>(async () =>
                {
                    var sessionId = ServiceWireValues.Text(args[0]);
                    await presentation.PrepareSessionRemovalAsync(sessionId, ServiceWireValues.ContextOf(args)).ConfigureAwait(false);
                    await options.RemoveAsync(sessionId, ServiceWireValues.ContextOf(args)).ConfigureAwait(false);
                    await RefreshNowAsync(ServiceWireValues.ContextOf(args)).ConfigureAwait(false);
                    return null;
                })),
                ["attach"] = (Func<object?[], object?>)(args => Serialize<object?>(async () =>
                {
                    await presentation.AttachSessionAsync(ServiceWireValues.Text(args[0]), ServiceWireValues.ContextOf(args))
                        .ConfigureAwait(false);
                    return null;
                })),
                ["detach"] = (Func<object?[], object?>)(args => Serialize<object?>(async () =>
                {
                    await presentation.DetachSessionAsync(ServiceWireValues.ContextOf(args)).ConfigureAwait(false);
                    preparedPluginPackagePaths = null;
                    return null;
                })),
            });

            ServerServiceAttachment? attachment = null;
            attachment = new ServerServiceAttachment(provider, () => attachments.Remove(attachment!));
            attachments.Add(attachment);
            return attachment;
        }

        return new ExperimentalServerServices
        {
            Host = new ServerServiceHost(AttachClient),
            Refresh = context => Serialize(
                async () =>
                {
                    await RefreshNowAsync(context ?? Context.Background).ConfigureAwait(false);
                    return (object?)null;
                }),
            Dispose = async () =>
            {
                var releases = await Task.WhenAll(
                        attachments.Select(attachment => attachment.ReleaseAsync(Context.Background)
                            .ContinueWith(task => task.Exception, System.Threading.Tasks.TaskScheduler.Default)))
                    .ConfigureAwait(false);
                attachments.Clear();
                await mutationTail.ConfigureAwait(false);
                var errors = releases.Where(error => error is not null)
                    .SelectMany(error => error!.InnerExceptions).ToList();
                if (errors.Count == 1) throw errors[0];
                if (errors.Count > 1)
                {
                    throw new AggregateException(
                        "Failed to release server service attachments", errors);
                }
            },
        };
    }

    /// <summary>TS <c>@pi/server-service-host</c>: one attachment per attached presentation.</summary>
    private sealed class ServerServiceHost(
        Func<IRoutedServerPresentation, Context, IRoutedServerServiceAttachment> attachClient)
        : IRoutedServerServiceHost
    {
        public Task<IRoutedServerServiceAttachment> AttachClientAsync(
            IRoutedServerPresentation presentation, Context context)
            => Task.FromResult(attachClient(presentation, context));
    }

    /// <summary>
    /// TS <c>createProviderAttachment</c>: one endpoint over one provider, released at most once.
    /// </summary>
    private sealed class ServerServiceAttachment(
        RemoteServiceProvider provider,
        Action onRelease) : IRoutedServerServiceAttachment
    {
        private readonly RemoteServiceEndpoint _endpoint = RemoteServiceEndpoint.Create(provider);
        private bool _released;

        public Task<object?> InvokeServiceAsync(ServiceCall call, ServiceUpdatePublisher publish, Context context)
        {
            if (_released)
            {
                // TS: Promise.reject(new Error("Server service attachment is released")).
                throw new InvalidOperationException("Server service attachment is released");
            }

            return InvokeAsync(call, publish, context);
        }

        private async Task<object?> InvokeAsync(ServiceCall call, ServiceUpdatePublisher publish, Context context)
        {
            var (isControl, result) = await _endpoint.InvokeAsync(call, publish, context).ConfigureAwait(false);
            if (!isControl) return result;
            // TS's control results are already plain JSON; the C# endpoint answers with decoded
            // records, and the protocol layer the server speaks only carries value trees — so the
            // catalogue and the subscribe snapshot cross as their wire form here.
            return result switch
            {
                ServiceSubscriptionSnapshot snapshot =>
                    ServiceWire.ToValue(new ServiceStateEncoder().EncodeSnapshot(snapshot)),
                IReadOnlyList<ServiceCatalogueEntry> catalogue => ServiceWire.ToValue(catalogue),
                _ => result,
            };
        }

        public Task ReleaseAsync(Context context)
        {
            if (_released) return Task.CompletedTask;
            _released = true;
            _endpoint.Dispose();
            provider.Dispose();
            onRelease();
            return Task.CompletedTask;
        }
    }
}
