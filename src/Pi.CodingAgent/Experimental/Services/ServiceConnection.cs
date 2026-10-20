// ============================================================================
// Remote service sources — port of experimental/services/connection.ts (4g-3c)
// ============================================================================
//
// The presentation side of the experimental session services: two remote service sources over one
// client — the server-scoped one (session directory / management / presentation plugins) and the
// selected-Session one (the worker's services). Both expose replicated connection/attachment state and
// hand out routed bindings that follow the client's connectivity: a binding activates on first use,
// rebinds when the connection or attachment comes and goes, and reports failures through the source's
// error handler.
//
// TS's `RoutedServiceBinding` implements the `RemoteServices` interface and forwards to a
// `RemoteServiceBinding`; C# does the same against `IRemoteServices` (whose surface is narrower — see
// C329).

using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Pi.Client;
using Pi.Protocol;
// `Client` alone resolves to the Pi.Client *namespace* from inside this namespace; alias the type.
using ServiceClient = Pi.Client.Client;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>The server connection as a presentation sees it. TS <c>ServerConnectionState</c>.</summary>
public abstract record ServerConnectionState
{
    public sealed record Connecting(int Attempt) : ServerConnectionState;

    public sealed record Connected(string Since) : ServerConnectionState;

    public sealed record Disconnected(string Since, string Reason, string? RetryAt) : ServerConnectionState;
}

/// <summary>The selected Session as a presentation sees it. TS <c>SessionAttachmentState</c>.</summary>
public abstract record SessionAttachmentState
{
    public sealed record Detached : SessionAttachmentState;

    public sealed record Attaching(string SessionId) : SessionAttachmentState;

    public sealed record Attached(string SessionId) : SessionAttachmentState;

    public sealed record Degraded(string SessionId) : SessionAttachmentState;
}

/// <summary>TS <c>ServiceSourceOptions</c>.</summary>
public sealed record ServiceSourceOptions(Action<Exception>? OnError = null);

/// <summary>The server-scoped remote service source. TS <c>ServerServiceSource</c>.</summary>
public interface IServerServiceSource : IRemoteServiceSource
{
    /// <summary>The replicated connection state.</summary>
    IReplicatedState<ServerConnectionState> Connection { get; }

    Task<IReadOnlyList<ServiceCatalogueEntry>> CatalogueAsync(Context context);

    new IRemoteServices Open(RemoteServicesOptions options);

    Task DisposeAsync(Context context);
}

/// <summary>The selected-Session remote service source. TS <c>SessionServiceSource</c>.</summary>
public interface ISessionServiceSource : IRemoteServiceSource
{
    /// <summary>The replicated attachment state.</summary>
    IReplicatedState<SessionAttachmentState> Attachment { get; }

    Task<IReadOnlyList<ServiceCatalogueEntry>> CatalogueAsync(Context context);

    new IRemoteServices Open(RemoteServicesOptions options);

    Task WhenAttachedAsync(string sessionId, Context context);

    Task WhenDetachedAsync(Context context);

    Task DisposeAsync(Context context);
}

/// <summary>Port of <c>createServerServiceSource</c> / <c>createSessionServiceSource</c>.</summary>
public static class ServiceConnection
{
    /// <summary>Create the server-scoped remote service source for one presentation client.</summary>
    public static IServerServiceSource CreateServerServiceSource(ServiceClient client, ServiceSourceOptions? options = null)
        => new ServerServiceSourceImpl(client, options ?? new ServiceSourceOptions());

    /// <summary>Create the selected-Session remote service source for one presentation client.</summary>
    public static ISessionServiceSource CreateSessionServiceSource(ServiceClient client, ServiceSourceOptions? options = null)
        => new SessionServiceSourceImpl(client, options ?? new ServiceSourceOptions());

    // ---------------------------------------------------------------- module helpers

    /// <summary>TS <c>publishReplacement</c>: replace the whole value.</summary>
    private static void PublishReplacement<T>(MutableReplicatedState<T> state, T value, Context context)
        where T : class
        => state.Replace(context, value);

    /// <summary>TS <c>toServerConnectionState</c>.</summary>
    internal static ServerConnectionState ToServerConnectionState(
        ConnectionState state, int attempt, Exception? error = null)
    {
        var since = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return state switch
        {
            ConnectionState.Connecting => new ServerConnectionState.Connecting(attempt),
            ConnectionState.Connected => new ServerConnectionState.Connected(since),
            _ => new ServerConnectionState.Disconnected(
                since,
                error?.Message ?? "Client is disconnected",
                null),
        };
    }

    /// <summary>TS <c>sameAttachment</c>: all three routing fields must match.</summary>
    internal static bool SameAttachment(RpcTarget.SessionTarget? left, RpcTarget.SessionTarget? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.ServerId == right.ServerId
            && left.SessionId == right.SessionId
            && left.AttachmentId == right.AttachmentId;
    }

    /// <summary>TS <c>throwFailures</c>: one error is rethrown, several become an aggregate.</summary>
    internal static void ThrowFailures(IReadOnlyList<Exception?> failures, string message)
    {
        var errors = failures.Where(error => error is not null).Select(error => error!).ToList();
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1) throw new AggregateException(message, errors);
    }

    /// <summary>
    /// TS reports the rejection reason; awaiting a task hands over an
    /// <see cref="AggregateException"/> wrapping it, so unwrap to the cause.
    /// </summary>
    private static Exception Unwrap(Exception error)
        => error is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? aggregate.InnerExceptions[0]
            : error;

    /// <summary>TS <c>tail.catch(() =&gt; {})</c>: a settled task that never faults.</summary>
    private static async Task IgnoreAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Swallowed: the tail only serialises, it never reports.
        }
    }

    /// <summary>Run the matching callback once <paramref name="task"/> settles.</summary>
    private static async Task WatchAsync(Task task, Action onCompleted, Action<Exception> onFailed)
    {
        try
        {
            await task.ConfigureAwait(false);
            onCompleted();
        }
        catch (Exception error)
        {
            onFailed(error);
        }
    }

    /// <summary>
    /// TS <c>RoutedServiceBinding</c>: a routed binding that activates on first use, follows the
    /// client's bound state, and forwards to the chord binding underneath.
    /// </summary>
    public sealed class RoutedServiceBinding : IRemoteServices
    {
        private readonly RemoteServiceBinding _services;
        private readonly Func<bool> _getBound;
        private readonly Func<Context, Task> _onActivate;
        private readonly Action _remove;
        private readonly Action<Exception> _onError;
        private bool _activated;
        private bool _activationComplete;

        public RoutedServiceBinding(RoutedServiceBindingOptions options)
        {
            _services = new RemoteServiceBinding(new RemoteServiceBindingOptions
            {
                Services = options.Services,
                Transport = options.Transport,
                Bound = false,
                OnError = options.OnError,
                AssertAccess = options.AssertAccess,
            });
            _getBound = options.GetBound ?? (() => true);
            _onActivate = options.OnActivate ?? (_ => Task.CompletedTask);
            _remove = options.Remove ?? (() => { });
            _onError = options.OnError ?? (error => throw error);
        }

        /// <summary>TS <c>use</c>: the local facade for one service.</summary>
        public RemoteServiceFacade Use<T>(Service<T> service) => _services.Use(service);

        /// <summary>TS <c>observe</c>.</summary>
        public IDisposable Observe<T>(Service<T> service, Func<RemoteServiceFacade, Context, Task?> handler)
            => _services.Observe(service, handler);

        /// <summary>TS <c>ready</c>: activate once, wait for the services, then run the activation hook.</summary>
        public async Task ReadyAsync(Context context)
        {
            if (!_activated)
            {
                _activated = true;
                await _services.RebindAsync(_getBound(), context).ConfigureAwait(false);
            }

            await _services.ReadyAsync().ConfigureAwait(false);
            if (!_activationComplete)
            {
                await _onActivate(context).ConfigureAwait(false);
                _activationComplete = true;
            }
        }

        /// <summary>TS <c>serviceReady</c>.</summary>
        public Task ServiceReadyAsync() => _services.ReadyAsync();

        /// <summary>TS <c>updateBound</c>: a binding that never activated ignores the change.</summary>
        public Task UpdateBoundAsync(bool bound, Context context)
            => _activated ? _services.RebindAsync(bound, context) : Task.CompletedTask;

        /// <summary>TS <c>dispose</c>.</summary>
        public async Task DisposeAsync(Context context)
        {
            _remove();
            await _services.DisposeAsync(context).ConfigureAwait(false);
        }

        Task IRemoteServices.ReadyAsync(CancellationToken cancellationToken)
            => ReadyAsync(ContextSignals.WithAbortSignal(cancellationToken, Context.Background));

        object? IRemoteServices.UseRaw(string serviceId) => _services.Use(new Service<object?>(serviceId));

        IDisposable IRemoteServices.ObserveRaw(string serviceId, Func<object, Context, Task?> handler)
            => _services.ObserveRaw(serviceId, handler);
    }

    /// <summary>Options for <see cref="RoutedServiceBinding"/>.</summary>
    public sealed record RoutedServiceBindingOptions
    {
        public required IReadOnlyList<string> Services { get; init; }

        public required IRemoteServiceTransport Transport { get; init; }

        public required Func<bool> GetBound { get; init; }

        public bool Bound { get; init; }

        public Func<Context, Task>? OnActivate { get; init; }

        public Action? AssertAccess { get; init; }

        public Action<Exception>? OnError { get; init; }

        public Action? Remove { get; init; }
    }

    // ---------------------------------------------------------------- server-scoped source

    private sealed class ServerServiceSourceImpl : IServerServiceSource
    {
        private readonly ServiceClient _client;
        private readonly Action<Exception> _onError;
        private readonly HashSet<RoutedServiceBinding> _bindings = [];
        private readonly IRemoteServiceTransport _transport;
        private readonly MutableReplicatedState<ServerConnectionState> _connectionState;
        private readonly Unsubscribe _removeConnectionListener;
        private Task _transition = Task.CompletedTask;
        private int _connectionAttempt;
        private bool _disposed;

        public ServerServiceSourceImpl(ServiceClient client, ServiceSourceOptions options)
        {
            _client = client;
            _onError = options.OnError ?? (_ => { });
            _transport = ClientServiceTransport.Create(
                client, () => new RpcTarget.ServerTarget(client.ServerId));
            _connectionAttempt = client.ConnectionState == ConnectionState.Connecting ? 1 : 0;
            _connectionState = new MutableReplicatedState<ServerConnectionState>(
                ToServerConnectionState(client.ConnectionState, _connectionAttempt));
            _removeConnectionListener = client.OnConnectionStateChange(change =>
            {
                if (change.State == ConnectionState.Connecting) _connectionAttempt += 1;
                PublishReplacement(_connectionState,
                    ToServerConnectionState(client.ConnectionState, _connectionAttempt, change.Error), Context.Background);
                // TS chains every transition onto the previous one and reports failures; a rejected
                // transition never blocks the next.
                var transition = RebindCoreAsync(change.State == ConnectionState.Connected);
                // TS chains every transition onto the previous one (failures swallowed so the next
                // one still runs) and reports a failure through the source's handler.
                _transition = IgnoreAsync(transition);
                _ = WatchAsync(transition, onCompleted: () => { }, onFailed: _onError);
            });
        }

        public IReplicatedState<ServerConnectionState> Connection => _connectionState;

        public bool AcceptsUnavailableServices => false;

        public Task<IReadOnlyList<ServiceCatalogueEntry>> CatalogueAsync(
            CancellationToken cancellationToken = default)
            => _client.ServiceCatalogueAsync(new RpcTarget.ServerTarget(_client.ServerId), cancellationToken);

        public Task<IReadOnlyList<ServiceCatalogueEntry>> CatalogueAsync(Context context)
            => _client.ServiceCatalogueAsync(
                new RpcTarget.ServerTarget(_client.ServerId), context.AbortSignal ?? default);

        public IRemoteServices Open(RemoteServicesOptions options)
        {
            if (_disposed) throw new InvalidOperationException("Server service source is disposed");
            RoutedServiceBinding? binding = null;
            binding = new RoutedServiceBinding(new RoutedServiceBindingOptions
            {
                Services = options.Services,
                Transport = _transport,
                GetBound = () => _client.Connected,
                AssertAccess = options.AssertAccess,
                OnError = options.OnError,
                Remove = () => _bindings.Remove(binding!),
            });
            _bindings.Add(binding);
            return binding;
        }

        private async Task RebindCoreAsync(bool bound)
        {
            var bindings = _bindings.ToList();
            var failures = await Task.WhenAll(bindings.Select(binding => binding.UpdateBoundAsync(
                bound, Context.Background).ContinueWith(task => task.Exception)))
                .ConfigureAwait(false);
            ThrowFailures(failures, "Failed to rebind server services");
        }

        public async Task DisposeAsync(Context context)
        {
            if (_disposed) return;
            _disposed = true;
            _removeConnectionListener();
            await _transition.ConfigureAwait(false);
            var bindings = _bindings.ToList();
            _bindings.Clear();
            var failures = await Task.WhenAll(bindings.Select(binding => binding.DisposeAsync(context)
                .ContinueWith(task => task.Exception, System.Threading.Tasks.TaskScheduler.Default)))
                .ConfigureAwait(false);
            ThrowFailures(failures, "Failed to dispose server service source");
        }
    }

    // ---------------------------------------------------------------- selected-Session source

    private sealed class SessionServiceSourceImpl : ISessionServiceSource
    {
        private readonly ServiceClient _client;
        private readonly Action<Exception> _onError;
        private readonly HashSet<RoutedServiceBinding> _bindings = [];
        private readonly IRemoteServiceTransport _transport;
        private readonly MutableReplicatedState<SessionAttachmentState> _attachmentState;
        private readonly Unsubscribe _removeAttachmentListener;
        private readonly HashSet<Task> _transitions = [];
        private IReadOnlyList<ServiceCatalogueEntry>? _catalogue;
        private int _attachmentRevision;
        private bool _disposed;

        public SessionServiceSourceImpl(ServiceClient client, ServiceSourceOptions options)
        {
            _client = client;
            _onError = options.OnError ?? (_ => { });
            _transport = ClientServiceTransport.Create(client, () => _client.Attachment);
            _attachmentState = new MutableReplicatedState<SessionAttachmentState>(
                _client.Attachment is null
                    ? new SessionAttachmentState.Detached()
                    : new SessionAttachmentState.Attaching(_client.Attachment.SessionId));
            _removeAttachmentListener = client.OnAttachmentChange(attachment =>
            {
                var revision = ++_attachmentRevision;
                if (attachment is not null)
                {
                    PublishReplacement(_attachmentState,
                        new SessionAttachmentState.Attaching(attachment.SessionId), Context.Background);
                    // TS fetches the catalogue for the new attachment and keeps it only if the
                    // attachment is still current when the answer lands.
                    _ = _client.ServiceCatalogueAsync(attachment).ContinueWith(
                        task =>
                        {
                            if (task.Exception is null
                                && _attachmentRevision == revision
                                && SameAttachment(_client.Attachment, attachment))
                            {
                                _catalogue = task.Result;
                            }
                        },
                        System.Threading.Tasks.TaskScheduler.Default);
                }

                var transition = RebindCoreAsync(attachment is not null);
                _transitions.Add(transition);
                _ = WatchAsync(transition,
                    onCompleted: () =>
                    {
                        _transitions.Remove(transition);
                        if (_attachmentRevision == revision && SameAttachment(_client.Attachment, attachment))
                        {
                            PublishReplacement(_attachmentState,
                                attachment is null
                                    ? new SessionAttachmentState.Detached()
                                    : new SessionAttachmentState.Attached(attachment.SessionId),
                                Context.Background);
                        }
                    },
                    onFailed: error =>
                    {
                        _transitions.Remove(transition);
                        if (_attachmentRevision == revision && SameAttachment(_client.Attachment, attachment))
                        {
                            PublishReplacement(_attachmentState,
                                attachment is null
                                    ? new SessionAttachmentState.Detached()
                                    : new SessionAttachmentState.Degraded(attachment.SessionId),
                                Context.Background);
                            _onError(Unwrap(error));
                        }
                    });
            });
        }

        public IReplicatedState<SessionAttachmentState> Attachment => _attachmentState;

        public bool AcceptsUnavailableServices => _client.Attachment is null && _catalogue is null;

        public async Task<IReadOnlyList<ServiceCatalogueEntry>> CatalogueAsync(
            CancellationToken cancellationToken = default)
        {
            var target = _client.Attachment;
            if (target is null) return _catalogue ?? [];
            var catalogue = await _client.ServiceCatalogueAsync(target, cancellationToken).ConfigureAwait(false);
            _catalogue = catalogue;
            return catalogue;
        }

        public Task<IReadOnlyList<ServiceCatalogueEntry>> CatalogueAsync(Context context)
            => CatalogueAsync(context.AbortSignal ?? default);

        public IRemoteServices Open(RemoteServicesOptions options)
        {
            if (_disposed) throw new InvalidOperationException("Session service source is disposed");
            RoutedServiceBinding? binding = null;
            binding = new RoutedServiceBinding(new RoutedServiceBindingOptions
            {
                Services = options.Services,
                Transport = _transport,
                GetBound = () => _client.Attachment is not null,
                OnActivate = context =>
                {
                    var attachment = _client.Attachment;
                    return attachment is null
                        ? WhenDetachedCoreAsync(_attachmentRevision, context)
                        : WhenAttachedCoreAsync(attachment, _attachmentRevision, context);
                },
                AssertAccess = options.AssertAccess,
                OnError = options.OnError,
                Remove = () => _bindings.Remove(binding!),
            });
            _bindings.Add(binding);
            return binding;
        }

        public async Task WhenAttachedAsync(string sessionId, Context context)
        {
            var attachment = _client.Attachment;
            if (attachment is null || attachment.SessionId != sessionId)
            {
                throw new InvalidOperationException($"Session {sessionId} is not the current attachment");
            }

            await WhenAttachedCoreAsync(attachment, _attachmentRevision, context).ConfigureAwait(false);
        }

        public async Task WhenDetachedAsync(Context context)
        {
            if (_client.Attachment is not null)
            {
                throw new InvalidOperationException("A Session is still attached");
            }

            await WhenDetachedCoreAsync(_attachmentRevision, context).ConfigureAwait(false);
        }

        public async Task DisposeAsync(Context context)
        {
            if (_disposed) return;
            _disposed = true;
            _removeAttachmentListener();
            await Task.WhenAll(_transitions.ToList()).ConfigureAwait(false);
            var bindings = _bindings.ToList();
            _bindings.Clear();
            var failures = await Task.WhenAll(bindings.Select(binding => binding.DisposeAsync(context)
                .ContinueWith(task => task.Exception, System.Threading.Tasks.TaskScheduler.Default)))
                .ConfigureAwait(false);
            ThrowFailures(failures, "Failed to dispose Session service source");
        }

        private async Task RebindCoreAsync(bool bound)
        {
            var bindings = _bindings.ToList();
            var failures = await Task.WhenAll(bindings.Select(binding => binding.UpdateBoundAsync(
                bound, Context.Background).ContinueWith(task => task.Exception)))
                .ConfigureAwait(false);
            ThrowFailures(failures, "Failed to rebind Session services");
        }

        private async Task WhenAttachedCoreAsync(RpcTarget.SessionTarget attachment, int revision, Context context)
        {
            try
            {
                await Task.WhenAll(_bindings.Select(binding => binding.ServiceReadyAsync())).ConfigureAwait(false);
            }
            catch
            {
                if (_attachmentRevision == revision && SameAttachment(_client.Attachment, attachment))
                {
                    PublishReplacement(_attachmentState,
                        new SessionAttachmentState.Degraded(attachment.SessionId), context);
                }

                throw;
            }

            if (_attachmentRevision != revision || !SameAttachment(_client.Attachment, attachment))
            {
                throw new InvalidOperationException(
                    $"Session {attachment.SessionId} was replaced while attaching");
            }

            if (_attachmentState.Value is not SessionAttachmentState.Attached { SessionId: var attached }
                || attached != attachment.SessionId)
            {
                PublishReplacement(_attachmentState,
                    new SessionAttachmentState.Attached(attachment.SessionId), context);
            }
        }

        private async Task WhenDetachedCoreAsync(int revision, Context context)
        {
            await Task.WhenAll(_bindings.Select(binding => binding.ServiceReadyAsync())).ConfigureAwait(false);
            if (_attachmentRevision != revision || _client.Attachment is not null)
            {
                throw new InvalidOperationException("The Session attachment changed while detaching");
            }

            if (_attachmentState.Value is not SessionAttachmentState.Detached)
            {
                PublishReplacement(_attachmentState, new SessionAttachmentState.Detached(), context);
            }
        }
    }
}
