// ============================================================================
// Session worker manager — port of experimental/session-worker-manager.ts (4g-3d-4a)
// ============================================================================
//
// The server half of the session-worker protocol: it owns the worker processes for one
// replaceable server generation — launching them as detached internal processes, adopting
// discovered ones, routing client attachments and service calls over the coordinator, and
// keeping the subscription bookkeeping the worker's service updates fan out to.
//
// TS keeps the coordinator as a Pick<> of six members and the spawner as a module function;
// the port turns the former into an interface and the latter into an injectable delegate, the
// same seam style the radius client used (C354). The child-process model maps onto
// ChildProcessHandle: a start failure surfaces as StartupError (not an async error event), and
// SignalCode is always null (the exit code carries the reason; null means failure).

using System.Buffers.Text;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Chord.Context;
using Pi.Chord.Services;
using Pi.CodingAgent.Experimental.Services;
using Pi.CodingAgent.Utils;
using Pi.Server;

namespace Pi.CodingAgent.Experimental;

/// <summary>TS's worker timeout constants.</summary>
public static class WorkerTimeouts
{
    /// <summary>TS <c>WORKER_STARTUP_TIMEOUT_MS</c>: how long a launched worker has to report ready.</summary>
    public const int StartupMs = 15_000;

    /// <summary>TS <c>WORKER_SHUTDOWN_TIMEOUT_MS</c>: how long a stopping worker has to exit.</summary>
    public const int ShutdownMs = 10_000;

    /// <summary>TS <c>WORKER_DISCOVERY_TIMEOUT_MS</c>: how long discovery waits for workers to answer.</summary>
    public const int DiscoveryMs = 5_000;

    /// <summary>TS <c>WORKER_DEMAND_TIMEOUT_MS</c>: how long a demand update has to be acknowledged.</summary>
    public const int DemandMs = 5_000;
}

/// <summary>
/// A conflicting plugin selection for a Session that is already running. TS
/// <c>SessionPluginSelectionConflictError</c>.
/// </summary>
public sealed class SessionPluginSelectionConflictError : Exception
{
    public SessionPluginSelectionConflictError(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The coordinator surface the manager uses. TS
/// <c>Pick&lt;CoordinatorConnection, "controlPath" | "serverConnectionId" | "wasReplaced" | "onEvent" | "send" | "broadcast"&gt;</c>.
/// </summary>
public interface ISessionWorkerCoordinator
{
    string ControlPath { get; }

    string ServerConnectionId { get; }

    bool WasReplaced { get; }

    /// <summary>Subscribe to connection events; the returned action unsubscribes.</summary>
    Action OnEvent(Action<CoordinatorConnectionEvent> listener);

    Task SendAsync(string peerId, JsonNode? payload);

    Task BroadcastAsync(JsonNode? payload);
}

/// <summary>
/// The manager's timeouts. TS hard-codes them and tests drive a fake clock; the port makes them
/// injectable so the same cases run on millisecond-scale real timers.
/// </summary>
public sealed record SessionWorkerManagerOptions
{
    public int StartupMs { get; init; } = WorkerTimeouts.StartupMs;

    public int ShutdownMs { get; init; } = WorkerTimeouts.ShutdownMs;

    public int DiscoveryMs { get; init; } = WorkerTimeouts.DiscoveryMs;

    public int DemandMs { get; init; } = WorkerTimeouts.DemandMs;
}

/// <summary>
/// The worker spawn seam. TS <c>spawnInternalProcess</c>; production wires
/// <see cref="InternalProcess.SpawnInternalProcess"/>, tests wire a fake.
/// </summary>
public delegate ChildProcessHandle SessionWorkerSpawner(
    InternalProcessRole role,
    IReadOnlyList<string> args,
    InternalProcess.SpawnInternalProcessOptions? options);

/// <summary>
/// Kill a worker that overstayed its shutdown. TS <c>process.kill(pid, "SIGKILL")</c>; a test
/// seam observes the call without touching a real process.
/// </summary>
public delegate void SessionWorkerKiller(long pid);

/// <summary>One running worker process. TS <c>WorkerRecord</c>.</summary>
internal sealed class WorkerRecord
{
    public required string PeerId { get; init; }

    public required SessionCatalogMetadata Metadata { get; init; }

    public required long Pid { get; init; }

    public required string Token { get; init; }

    public required IReadOnlyList<string> PluginManifestPaths { get; init; }

    public required TaskCompletionSource<Exception?> Terminated { get; init; }

    /// <summary>
    /// The child handle for a worker this manager launched; null for an adopted one (TS kills
    /// those by pid, which is why the record keeps the pid too).
    /// </summary>
    public ChildProcessHandle? Child { get; init; }

    public HashSet<string> AttachmentIds { get; } = [];

    public bool ExpectedStop { get; set; }

    public Task? StopPromise { get; set; }

    public bool Stopping { get; set; }
}

/// <summary>A demand awaiting its worker acknowledgement. TS <c>PendingDemand</c>.</summary>
internal sealed class PendingDemand
{
    public required string AttachmentId { get; init; }

    public required bool Attached { get; init; }

    public required string RequestId { get; init; }

    public required WorkerTimer Timer { get; init; }

    public required WorkerRecord Worker { get; init; }

    public required Action Resolve { get; init; }

    public required Action<Exception> Reject { get; init; }
}

/// <summary>An operation awaiting its worker response. TS <c>PendingWorkerOperation</c>.</summary>
internal sealed class PendingWorkerOperation
{
    public required WorkerRecord Worker { get; init; }

    public required WorkerServiceScope Scope { get; init; }

    public required Action Cleanup { get; init; }

    public required Action<object?> Resolve { get; init; }

    public required Action<Exception> Reject { get; init; }
}

/// <summary>A service subscription the worker publishes updates for. TS <c>WorkerServiceSubscription</c>.</summary>
internal sealed class WorkerServiceSubscription
{
    public required WorkerRecord Worker { get; init; }

    public required WorkerServiceScope Scope { get; init; }

    public required Func<ServiceProviderUpdate, Context, Task> Listener { get; init; }

    /// <summary>
    /// One decoder per subscription, the mirror of the worker's per-subscription encoder: the
    /// wire ops carry path-residency references that only decode in arrival order.
    /// </summary>
    public required ServiceStateDecoder Decoder { get; init; }

    public Task DeliveryTail { get; set; } = Task.CompletedTask;
}

/// <summary>A launched worker that has not reported ready yet. TS <c>PendingLaunch</c>.</summary>
internal sealed class PendingLaunch
{
    public required string SessionKey { get; init; }

    public required string PeerId { get; init; }

    public required string Token { get; init; }

    public required IReadOnlyList<string> PluginManifestPaths { get; init; }

    public required ChildProcessHandle Child { get; init; }

    public required WorkerTimer Timer { get; init; }

    public required TaskCompletionSource<WorkerRecord> Promise { get; init; }
}

/// <summary>
/// A cancellable timeout. TS's <c>setTimeout</c>/<c>clearTimeout</c>: cancelling prevents a
/// not-yet-fired callback, while one whose continuation was already queued still runs — the Node
/// semantics the lifecycle timers already model (C356).
/// </summary>
internal sealed class WorkerTimer
{
    private readonly Action _fire;
    private readonly CancellationTokenSource _cts = new();

    internal WorkerTimer(int milliseconds, Action fire)
    {
        _fire = fire;
        _ = Task.Delay(milliseconds, _cts.Token).ContinueWith(
            static (task, state) =>
            {
                var timer = (WorkerTimer)state!;
                if (task.IsCanceled) return;
                timer._fire();
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>TS <c>clearTimeout</c>.</summary>
    public void Cancel()
    {
        try
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }
    }
}

/// <summary>
/// Session and process bookkeeping owned by one replaceable server process. TS
/// <c>SessionWorkerManager</c>.
/// </summary>
public sealed class SessionWorkerManager
{
    /// <summary>The pids of the running workers, keyed by session id. TS <c>workerPids</c>.</summary>
    public Dictionary<string, long> WorkerPids { get; } = [];

    private readonly ISessionWorkerCoordinator _coordinator;
    private readonly string _sessionDir;
    private readonly (string? Provider, string Model)? _model;
    private readonly Dictionary<string, WorkerRecord> _workersBySession = [];
    private readonly Dictionary<string, WorkerRecord> _workersByPeer = [];
    private readonly Dictionary<string, PendingLaunch> _pending = [];
    private readonly Dictionary<string, PendingDemand> _pendingDemand = [];
    private readonly Dictionary<string, PendingWorkerOperation> _pendingOperations = [];
    private readonly Dictionary<string, WorkerServiceSubscription> _serviceSubscriptions = [];
    private readonly Action _removeListener;
    private readonly Action<int>? _onWorkerCountChanged;
    private readonly SessionWorkerSpawner _spawn;
    private readonly SessionWorkerKiller _killWorker;
    private readonly SessionWorkerManagerOptions _timeouts;
    private readonly Lock _gate = new();
    private HashSet<string>? _discoveryPeers;
    private Action? _resolveDiscovery;
    private bool _detached;
    private bool _shuttingDown;

    public SessionWorkerManager(
        ISessionWorkerCoordinator coordinator,
        string sessionDir,
        (string? Provider, string Model)? model = null,
        Action<int>? onWorkerCountChanged = null,
        SessionWorkerSpawner? spawn = null,
        SessionWorkerManagerOptions? timeouts = null,
        SessionWorkerKiller? killWorker = null)
    {
        _coordinator = coordinator;
        _sessionDir = sessionDir;
        _model = model;
        _onWorkerCountChanged = onWorkerCountChanged;
        _spawn = spawn ?? InternalProcess.SpawnInternalProcess;
        _timeouts = timeouts ?? new SessionWorkerManagerOptions();
        _killWorker = killWorker ?? (pid => TryKillTree((int)pid));
        _removeListener = coordinator.OnEvent(HandleCoordinatorEvent);
    }

    /// <summary>The metadata of every tracked session. TS <c>trackedSessions</c>.</summary>
    public IReadOnlyList<SessionCatalogMetadata> TrackedSessions
    {
        get
        {
            lock (_gate)
            {
                return _workersBySession.Values.Select(worker => worker.Metadata).ToList();
            }
        }
    }

    /// <summary>
    /// Reject a plugin selection that conflicts with a running or starting worker for the same
    /// Session. TS <c>assertSessionPluginManifestPaths</c>.
    /// </summary>
    public void AssertSessionPluginManifestPaths(
        SessionCatalogMetadata metadata, IReadOnlyList<string> manifestPaths)
    {
        lock (_gate)
        {
            if (_workersBySession.TryGetValue(metadata.Path, out var existing)
                && !SameStrings(existing.PluginManifestPaths, manifestPaths))
            {
                throw new SessionPluginSelectionConflictError(
                    $"Session {metadata.Id} is active with a different plugin selection");
            }

            if (_pending.TryGetValue(metadata.Path, out var pending)
                && !SameStrings(pending.PluginManifestPaths, manifestPaths))
            {
                throw new SessionPluginSelectionConflictError(
                    $"Session {metadata.Id} is starting with a different plugin selection");
            }
        }
    }

    /// <summary>
    /// Ask the workers behind these peers to announce themselves, adopting any that answer. TS
    /// <c>discover</c>.
    /// </summary>
    public async Task DiscoverAsync(IReadOnlySet<string> peerIds)
    {
        HashSet<string> undiscovered;
        lock (_gate)
        {
            if (_detached) return;
            undiscovered = peerIds
                .Where(peerId => !_workersByPeer.ContainsKey(peerId) && PendingPeer(peerId) is null)
                .ToHashSet(StringComparer.Ordinal);
            if (undiscovered.Count == 0) return;
            _discoveryPeers = undiscovered;
        }

        var discovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _resolveDiscovery = () => discovered.TrySetResult();
        }

        await _coordinator.BroadcastAsync(DiscoverWorkersCommand()).ConfigureAwait(false);
        await DelayWinsAsync(discovered.Task, _timeouts.DiscoveryMs).ConfigureAwait(false);

        lock (_gate)
        {
            _discoveryPeers = null;
            _resolveDiscovery = null;
        }
    }

    /// <summary>Open (or adopt) the worker for one session. TS <c>openSession</c>.</summary>
    public async Task<IRoutedSessionHandle> OpenSessionAsync(
        SessionCatalogMetadata metadata, Context context, IReadOnlyList<string> pluginManifestPaths)
    {
        lock (_gate)
        {
            if (_detached || _shuttingDown)
            {
                throw new InvalidOperationException("Experimental server is shutting down");
            }
        }

        AssertSessionPluginManifestPaths(metadata, pluginManifestPaths);
        WorkerRecord? existing;
        PendingLaunch? pending;
        lock (_gate)
        {
            existing = _workersBySession.GetValueOrDefault(metadata.Path);
            pending = _pending.GetValueOrDefault(metadata.Path);
        }

        if (existing is not null) return RoutedHandle(this, existing);
        if (pending is not null)
        {
            return RoutedHandle(this, await pending.Promise.Task.ConfigureAwait(false));
        }

        return RoutedHandle(this, await LaunchAsync(metadata, pluginManifestPaths).ConfigureAwait(false));
    }

    /// <summary>Stop the worker for one session, if any. TS <c>closeSession</c>.</summary>
    public async Task CloseSessionAsync(SessionCatalogMetadata metadata, Context context)
    {
        WorkerRecord? worker;
        PendingLaunch? pending;
        lock (_gate)
        {
            worker = _workersBySession.GetValueOrDefault(metadata.Path);
            pending = _pending.GetValueOrDefault(metadata.Path);
        }

        worker ??= pending is null ? null : await pending.Promise.Task.ConfigureAwait(false);
        if (worker is not null) await StopWorkerAsync(worker).ConfigureAwait(false);
    }

    /// <summary>Stop every worker, then forget all state. TS <c>shutdown</c>.</summary>
    public async Task ShutdownAsync()
    {
        lock (_gate)
        {
            if (_detached || _shuttingDown) return;
            _shuttingDown = true;
        }

        PendingLaunch[] pendingWorkers;
        lock (_gate)
        {
            pendingWorkers = [.. _pending.Values];
        }

        foreach (var pending in pendingWorkers)
        {
            _ = SendAsync(pending.PeerId, ShutdownCommand()).ContinueWith(Observe);
        }

        var pendingFinished = Task.WhenAll(pendingWorkers.Select(
            pending => ChildIsDone(pending.Child) ? Task.CompletedTask : pending.Child.WaitAsync()));
        if (await DelayWinsAsync(pendingFinished, _timeouts.ShutdownMs).ConfigureAwait(false))
        {
            foreach (var pending in pendingWorkers)
            {
                // TS: child.kill("SIGKILL"); the port kills the detached process group.
                pending.Child.KillTree();
            }

            await pendingFinished.ConfigureAwait(false);
        }

        WorkerRecord[] workers;
        lock (_gate)
        {
            workers = [.. _workersBySession.Values];
        }

        await Task.WhenAll(workers.Select(StopWorkerAsync)).ConfigureAwait(false);
        DetachState();
    }

    /// <summary>
    /// Forget workers without stopping them, because this server was replaced. TS <c>detach</c>.
    /// </summary>
    public void Detach()
    {
        lock (_gate)
        {
            if (_detached) return;
            _detached = true;
            foreach (var pending in _pending.Values)
            {
                pending.Timer.Cancel();
                pending.Promise.TrySetException(
                    new InvalidOperationException("Experimental server was replaced"));
            }
        }

        foreach (var requestId in PendingOperationIds())
        {
            RejectOperation(requestId, new InvalidOperationException(
                "Experimental server was replaced during a worker operation"));
        }

        DetachState();
    }

    private static IRoutedSessionHandle RoutedHandle(SessionWorkerManager manager, WorkerRecord worker)
        => new RoutedWorkerHandle(manager, worker);

    private sealed class RoutedWorkerHandle(SessionWorkerManager manager, WorkerRecord worker)
        : IRoutedSessionHandle
    {
        public Task<Exception?>? Terminated => worker.Terminated.Task;

        public Task<IRoutedSessionAttachment> AttachClientAsync(Context context)
            => manager.AttachClientAsync(worker, context);

        public Task CloseAsync(Context context) => manager.StopWorkerAsync(worker);
    }

    private async Task<IRoutedSessionAttachment> AttachClientAsync(WorkerRecord worker, Context context)
    {
        lock (_gate)
        {
            if (_detached || _shuttingDown || worker.Stopping)
            {
                throw new InvalidOperationException("Experimental Session worker is stopping");
            }

            if (!ReferenceEquals(_workersByPeer.GetValueOrDefault(worker.PeerId), worker))
            {
                throw new InvalidOperationException("Experimental Session worker is no longer available");
            }
        }

        var attachmentId = Guid.NewGuid().ToString();
        lock (_gate)
        {
            worker.AttachmentIds.Add(attachmentId);
        }

        try
        {
            await ApplyDemandAsync(worker, attachmentId, attached: true, compensateOnTimeout: true)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            lock (_gate)
            {
                worker.AttachmentIds.Remove(attachmentId);
            }

            throw;
        }

        var scope = OperationScope(worker, attachmentId);
        return new RoutedWorkerAttachment(this, worker, scope, attachmentId);
    }

    private sealed class RoutedWorkerAttachment(
        SessionWorkerManager manager, WorkerRecord worker, WorkerServiceScope scope, string attachmentId)
        : IRoutedSessionAttachment
    {
        private bool _released;

        public Task<object?> InvokeServiceAsync(
            ServiceCall call, ServiceUpdatePublisher publish, Context context)
            => manager.InvokeServiceAsync(worker, scope, call, publish, context);

        public async Task ReleaseAsync(Context context)
        {
            if (_released) return;
            _released = true;
            bool skip;
            lock (manager._gate)
            {
                skip = manager._detached || !worker.AttachmentIds.Contains(attachmentId);
            }

            if (skip) return;
            try
            {
                await manager.ApplyDemandAsync(
                    worker, attachmentId, attached: false, compensateOnTimeout: true).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // TS rethrows unless the server was replaced; a replaced server is gone either way.
                if (!manager._detached && !manager._coordinator.WasReplaced) throw;
            }
            finally
            {
                lock (manager._gate)
                {
                    worker.AttachmentIds.Remove(attachmentId);
                    manager.RemoveServiceSubscriptions(entry =>
                        ReferenceEquals(entry.Worker, worker) && SameScope(entry.Scope, scope));
                }
            }
        }
    }

    private async Task<object?> InvokeServiceAsync(
        WorkerRecord worker,
        WorkerServiceScope scope,
        ServiceCall call,
        ServiceUpdatePublisher publish,
        Context context)
    {
        var control = ServiceWire.DecodeServiceControlCall(call);
        string? addedSubscriptionKey = null;
        if (control is ServiceControlCall.Subscribe subscribe)
        {
            var key = ScopedServiceSubscriptionKey(scope, subscribe.SubscriptionId);
            bool added;
            lock (_gate)
            {
                added = _serviceSubscriptions.TryAdd(key, new WorkerServiceSubscription
                {
                    Worker = worker,
                    Scope = scope,
                    Listener = (update, updateContext) =>
                        publish(subscribe.SubscriptionId, update, updateContext),
                    Decoder = new ServiceStateDecoder(),
                });
            }

            if (!added)
            {
                throw new InvalidOperationException("Service subscription ID is already active");
            }

            addedSubscriptionKey = key;
        }

        object? response;
        try
        {
            response = await InvokeAsync(worker, scope, call, context).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (addedSubscriptionKey is not null && control is ServiceControlCall.Subscribe failed)
            {
                lock (_gate)
                {
                    _serviceSubscriptions.Remove(addedSubscriptionKey);
                }

                _ = InvokeAsync(
                    worker,
                    scope,
                    ServiceWire.CreateServiceUnsubscribeCall(failed.SubscriptionId),
                    Context.Background).ContinueWith(Observe);
            }

            throw;
        }

        if (control is ServiceControlCall.Unsubscribe unsubscribe)
        {
            lock (_gate)
            {
                _serviceSubscriptions.Remove(ScopedServiceSubscriptionKey(scope, unsubscribe.SubscriptionId));
            }
        }

        if (control is ServiceControlCall.Subscribe && addedSubscriptionKey is not null && response is not null)
        {
            // The mirror decoder learns the subscription's members from the snapshot: the wire
            // ops of later updates only decode against a registry the snapshot populated. TS
            // needs no such step — its worker sends already-decoded ops, so its parse is
            // stateless. A null response is the server's "did not return a snapshot" case, left
            // for it to report; a malformed one surfaces here, as the server's parse would.
            WorkerServiceSubscription? subscription;
            lock (_gate)
            {
                _serviceSubscriptions.TryGetValue(addedSubscriptionKey, out subscription);
            }

            subscription?.Decoder.DecodeSnapshot(
                ServiceWire.ParseWireServiceSubscriptionSnapshot(response));
        }

        return response;
    }

    private async Task ApplyDemandAsync(
        WorkerRecord worker, string attachmentId, bool attached, bool compensateOnTimeout)
    {
        bool stopping;
        WorkerRecord? byPeer;
        lock (_gate)
        {
            stopping = worker.Stopping;
            byPeer = _workersByPeer.GetValueOrDefault(worker.PeerId);
        }

        if (stopping || !ReferenceEquals(byPeer, worker))
        {
            throw new InvalidOperationException("Experimental Session worker is stopping");
        }

        var requestId = Guid.NewGuid().ToString();
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new WorkerTimer(_timeouts.DemandMs, () =>
        {
            if (compensateOnTimeout)
            {
                _ = ReconcileDemandTimeoutAsync(requestId).ContinueWith(Observe);
            }
            else
            {
                RejectDemand(requestId, new InvalidOperationException("Session worker demand update timed out"));
            }
        });

        lock (_gate)
        {
            _pendingDemand[requestId] = new PendingDemand
            {
                AttachmentId = attachmentId,
                Attached = attached,
                RequestId = requestId,
                Timer = timer,
                Worker = worker,
                Resolve = () => applied.TrySetResult(),
                Reject = error => applied.TrySetException(error),
            };
        }

        try
        {
            await SendAsync(worker.PeerId, SessionDemandCommand(
                _coordinator.ServerConnectionId, requestId, attachmentId, attached)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            RejectDemand(requestId, error);
        }

        await applied.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Worker operations have no wall-clock timeout: completion, disconnect, replacement, or
    /// shutdown settles them. TS <c>#invoke</c>.
    /// </summary>
    private Task<object?> InvokeAsync(
        WorkerRecord worker, WorkerServiceScope scope, ServiceCall call, Context context)
    {
        try
        {
            OperationScope(worker, scope.AttachmentId);
        }
        catch (Exception error)
        {
            return Task.FromException<object?>(error);
        }

        var signal = context.AbortSignal;
        if (signal?.IsCancellationRequested == true)
        {
            return Task.FromException<object?>(new OperationCanceledException(signal.Value));
        }

        var requestId = Guid.NewGuid().ToString();
        var result = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnAbort()
        {
            bool live;
            lock (_gate)
            {
                live = _pendingOperations.ContainsKey(requestId);
            }

            if (!live) return;
            _ = SendAsync(worker.PeerId, OperationCancelCommand(requestId, scope)).ContinueWith(Observe);
            RejectOperation(requestId, new OperationCanceledException(signal!.Value));
        }

        CancellationTokenRegistration registration = default;
        if (signal is not null)
        {
            registration = signal.Value.Register(OnAbort);
        }

        lock (_gate)
        {
            _pendingOperations[requestId] = new PendingWorkerOperation
            {
                Worker = worker,
                Scope = scope,
                Cleanup = registration.Dispose,
                Resolve = value => result.TrySetResult(value),
                Reject = error => result.TrySetException(error),
            };
        }

        _ = SendAsync(worker.PeerId, OperationCommand(requestId, scope, call)).ContinueWith(Observe);
        return result.Task;
    }

    private WorkerServiceScope OperationScope(WorkerRecord worker, string attachmentId)
    {
        lock (_gate)
        {
            if (_detached || _shuttingDown || worker.Stopping)
            {
                throw new InvalidOperationException("Experimental Session worker is stopping");
            }

            if (!ReferenceEquals(_workersByPeer.GetValueOrDefault(worker.PeerId), worker)
                || !worker.AttachmentIds.Contains(attachmentId))
            {
                throw new InvalidOperationException("Experimental Session worker has no active attachment");
            }

            return new WorkerServiceScope(_coordinator.ServerConnectionId, attachmentId);
        }
    }

    private Task StopWorkerAsync(WorkerRecord worker)
    {
        lock (_gate)
        {
            if (_detached || !ReferenceEquals(_workersByPeer.GetValueOrDefault(worker.PeerId), worker))
            {
                return Task.CompletedTask;
            }

            return worker.StopPromise ??= StopWorkerInternalAsync(worker);
        }
    }

    private Task StopWorkerAsync(WorkerRecord worker, Context context) => StopWorkerAsync(worker);

    private async Task StopWorkerInternalAsync(WorkerRecord worker)
    {
        RejectWorkerOperations(worker, new InvalidOperationException("Session worker is stopping"));
        lock (_gate)
        {
            worker.Stopping = true;
            worker.ExpectedStop = true;
        }

        _ = SendAsync(worker.PeerId, ShutdownCommand()).ContinueWith(Observe);
        if (await DelayWinsAsync(worker.Terminated.Task, _timeouts.ShutdownMs).ConfigureAwait(false))
        {
            KillWorker(worker);
            RemoveWorker(worker, null);
            await worker.Terminated.Task.ConfigureAwait(false);
        }
    }

    private void HandleCoordinatorEvent(CoordinatorConnectionEvent @event)
    {
        lock (_gate)
        {
            if (_detached) return;
        }

        if (@event is PeerDisconnected disconnected)
        {
            MarkDiscovered(disconnected.PeerId);
            WorkerRecord? worker;
            lock (_gate)
            {
                worker = _workersByPeer.GetValueOrDefault(disconnected.PeerId);
            }

            if (worker is not null)
            {
                RemoveWorker(
                    worker,
                    worker.ExpectedStop
                        ? null
                        : new InvalidOperationException(
                            $"Session worker {worker.Metadata.Id} disconnected unexpectedly"));
            }

            var pending = PendingPeer(disconnected.PeerId);
            if (pending is not null)
            {
                FailPending(
                    pending.SessionKey,
                    new InvalidOperationException("Session worker disconnected during startup"));
            }

            return;
        }

        if (@event is not ConnectionMessage message) return;

        var parsed = SessionWorkerProtocol.TryParseEvent(message.Payload);
        if (parsed is null)
        {
            // A payload that claims to be an operation response but fails validation still has to
            // settle the operations waiting on it, as TS does.
            if (message.Payload is JsonObject raw
                && raw["type"] is JsonValue type
                && type.GetValueKind() == JsonValueKind.String
                && type.GetValue<string>() == "operation_response")
            {
                WorkerRecord? worker;
                lock (_gate)
                {
                    worker = _workersByPeer.GetValueOrDefault(message.From);
                }

                if (worker is not null)
                {
                    RejectWorkerOperations(
                        worker,
                        new InvalidOperationException("Session worker returned an invalid operation response"));
                }
            }

            return;
        }

        switch (parsed)
        {
            case SessionWorkerEvent.WorkerFailed failed:
            {
                PendingLaunch? pending;
                lock (_gate)
                {
                    pending = _pending.GetValueOrDefault(failed.SessionKey);
                }

                if (pending is not null && pending.PeerId == message.From && pending.Token == failed.Token)
                {
                    FailPending(failed.SessionKey, new InvalidOperationException(
                        $"Session worker failed: {failed.Message}"));
                }

                return;
            }

            case SessionWorkerEvent.DemandApplied or SessionWorkerEvent.DemandRejected:
            {
                var requestId = parsed is SessionWorkerEvent.DemandApplied applied
                    ? applied.RequestId
                    : ((SessionWorkerEvent.DemandRejected)parsed).RequestId;
                PendingDemand? demand;
                lock (_gate)
                {
                    demand = _pendingDemand.GetValueOrDefault(requestId);
                }

                if (demand is null
                    || demand.Worker.PeerId != message.From
                    || demand.Worker.Token != EventToken(parsed)
                    || demand.Worker.Metadata.Path != EventSessionKey(parsed))
                {
                    return;
                }

                if (parsed is SessionWorkerEvent.DemandApplied ok
                    && (demand.AttachmentId != ok.AttachmentId || demand.Attached != ok.Attached))
                {
                    return;
                }

                lock (_gate)
                {
                    _pendingDemand.Remove(requestId);
                }

                demand.Timer.Cancel();
                if (parsed is SessionWorkerEvent.DemandApplied)
                {
                    demand.Resolve();
                }
                else
                {
                    demand.Reject(new InvalidOperationException(
                        $"Session worker rejected demand: {((SessionWorkerEvent.DemandRejected)parsed).Message}"));
                }

                return;
            }

            case SessionWorkerEvent.OperationResponse operation:
                HandleOperationResponse(
                    message.From, EventToken(parsed), EventSessionKey(parsed), operation.Response);
                return;

            case SessionWorkerEvent.ServiceUpdate update:
                HandleServiceEvent(
                    message.From,
                    EventToken(parsed),
                    EventSessionKey(parsed),
                    update.Scope,
                    update.SubscriptionId,
                    update.Update);
                return;

            case SessionWorkerEvent.WorkerReady ready:
                RecordReadyWorker(message.From, ready);
                return;
        }
    }

    private void HandleOperationResponse(
        string peerId, string token, string sessionKey, WorkerOperationResponse response)
    {
        // Both union members carry the request id and scope, as in TS.
        var requestId = response switch
        {
            WorkerOperationResponse.Result ok => ok.RequestId,
            WorkerOperationResponse.Error failed => failed.RequestId,
            _ => throw new ArgumentException("unknown operation response", nameof(response)),
        };
        var scope = response switch
        {
            WorkerOperationResponse.Result ok => ok.Scope,
            WorkerOperationResponse.Error failed => failed.Scope,
            _ => throw new ArgumentException("unknown operation response", nameof(response)),
        };

        PendingWorkerOperation? pending;
        lock (_gate)
        {
            pending = _pendingOperations.GetValueOrDefault(requestId);
        }

        if (pending is null) return;
        if (pending.Worker.PeerId != peerId
            || pending.Worker.Token != token
            || pending.Worker.Metadata.Path != sessionKey
            || scope.ServerConnectionId != pending.Scope.ServerConnectionId
            || scope.AttachmentId != pending.Scope.AttachmentId)
        {
            RejectOperation(
                requestId,
                new InvalidOperationException("Session worker returned a mismatched operation response"));
            return;
        }

        lock (_gate)
        {
            _pendingOperations.Remove(requestId);
        }

        pending.Cleanup();
        if (response is WorkerOperationResponse.Error error)
        {
            pending.Reject(error.Code is null
                ? new InvalidOperationException($"Session worker operation failed: {error.Message}")
                : new ServerError(error.Code, error.Message));
        }
        else
        {
            pending.Resolve(ToClrValue(((WorkerOperationResponse.Result)response).Value));
        }
    }

    /// <summary>
    /// The worker answers over its JSON-line channel, so a result arrives as a
    /// <see cref="JsonNode"/>; the server-side attachment contract speaks the CLR value tree the
    /// protocol layer parses into — the same conversion the worker applies to the calls it reads.
    /// </summary>
    private static object? ToClrValue(JsonNode? node) => SessionWorkerProtocol.ClrValue(node);

    private void HandleServiceEvent(
        string peerId,
        string token,
        string sessionKey,
        WorkerServiceScope scope,
        string subscriptionId,
        JsonNode? update)
    {
        WorkerServiceSubscription? entry;
        lock (_gate)
        {
            entry = _serviceSubscriptions.GetValueOrDefault(
                ScopedServiceSubscriptionKey(scope, subscriptionId));
        }

        if (entry is null
            || entry.Worker.PeerId != peerId
            || entry.Worker.Token != token
            || entry.Worker.Metadata.Path != sessionKey
            || !SameScope(entry.Scope, scope))
        {
            return;
        }

        ServiceProviderUpdate parsed;
        try
        {
            // The worker publishes wire-encoded ops (its provider emits decoded records, so the
            // worker owns one encoder per subscription); this side owns the mirror decoder, and
            // the server re-encodes per subscription for the client. The update arrives as a JSON
            // line, so it crosses into the value tree the wire parsers speak first, and it parses
            // as the wire form: its ops may carry path ids and omitted references, which only the
            // stateful decoder resolves (TS sends already-decoded ops, so its parse is stateless).
            parsed = entry.Decoder.DecodeUpdate(
                ServiceWire.ParseWireServiceProviderUpdate(ToClrValue(update)));
        }
        catch (Exception)
        {
            return;
        }

        // TS: entry.deliveryTail = entry.deliveryTail.then(() => listener(...)).catch(() => {})
        var tail = entry.DeliveryTail;
        var delivery = DeliverAsync(tail, entry, parsed);
        entry.DeliveryTail = delivery;
    }

    private static async Task DeliverAsync(
        Task tail, WorkerServiceSubscription entry, ServiceProviderUpdate update)
    {
        try
        {
            // A turn of slack keeps a completed tail from running the listener inline, the way a
            // promise .then always defers.
            await Task.Yield();
            await tail.ConfigureAwait(false);
            await entry.Listener(update, Context.Todo).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // TS: .catch(() => {}) — one failed delivery must not break the chain.
        }
    }

    private void RecordReadyWorker(string peerId, SessionWorkerEvent.WorkerReady message)
    {
        if (message.SessionKey != message.Metadata.Path
            || message.SessionId != message.Metadata.Id
            || !NodePath.IsAbsolute(message.Metadata.Cwd)
            || !NodePath.IsAbsolute(message.Metadata.Path))
        {
            return;
        }

        MarkDiscovered(peerId);
        PendingLaunch? pending;
        lock (_gate)
        {
            pending = _pending.GetValueOrDefault(message.SessionKey);
        }

        if (pending is not null
            && pending.PeerId == peerId
            && !SameStrings(message.PluginManifestPaths, pending.PluginManifestPaths))
        {
            FailPending(
                message.SessionKey,
                new InvalidOperationException("Session worker started with stale plugin packages"));
            return;
        }

        WorkerRecord? existing;
        lock (_gate)
        {
            existing = _workersBySession.GetValueOrDefault(message.SessionKey);
        }

        if (existing is not null && existing.PeerId != peerId)
        {
            // Another worker already owns this session: tell the newcomer to stand down.
            _ = SendAsync(peerId, ShutdownCommand()).ContinueWith(Observe);
            return;
        }

        if (pending is not null
            && (pending.PeerId != peerId
                || pending.Token != message.Token
                || pending.Child.Id is not int childPid
                || childPid != message.Pid))
        {
            return;
        }

        if (existing is not null) return;
        var terminated = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new WorkerRecord
        {
            PeerId = peerId,
            Metadata = new SessionCatalogMetadata(
                message.Metadata.Id, message.Metadata.CreatedAt,
                message.Metadata.Cwd, message.Metadata.Path),
            Pid = message.Pid,
            Token = message.Token,
            PluginManifestPaths = message.PluginManifestPaths,
            Terminated = terminated,
            Child = pending?.Child,
        };

        lock (_gate)
        {
            _workersBySession[message.SessionKey] = worker;
            _workersByPeer[peerId] = worker;
            WorkerPids[message.SessionId] = message.Pid;
            if (pending is not null)
            {
                _pending.Remove(message.SessionKey);
                pending.Timer.Cancel();
                pending.Promise.TrySetResult(worker);
            }
        }

        NotifyWorkerCountChanged();
    }

    private void ChildExited(PendingLaunch pending, int? code)
    {
        PendingLaunch? current;
        lock (_gate)
        {
            current = _pending.GetValueOrDefault(pending.SessionKey);
        }

        if (ReferenceEquals(current, pending))
        {
            // SignalCode is always null in the port, so the code carries the reason.
            FailPending(
                pending.SessionKey,
                new InvalidOperationException(
                    $"Session worker exited before readiness ({code?.ToString() ?? "unknown"})"));
            return;
        }

        WorkerRecord? worker;
        lock (_gate)
        {
            worker = _workersByPeer.GetValueOrDefault(pending.PeerId);
        }

        if (worker is not null)
        {
            RemoveWorker(
                worker,
                worker.ExpectedStop
                    ? null
                    : new InvalidOperationException(
                        $"Session worker {worker.Metadata.Id} exited unexpectedly ({code?.ToString() ?? "unknown"})"));
        }
    }

    private async Task<WorkerRecord> LaunchAsync(
        SessionCatalogMetadata metadata, IReadOnlyList<string> pluginManifestPaths)
    {
        var sessionKey = metadata.Path;
        var peerId = $"worker-{Guid.NewGuid()}";
        var token = Guid.NewGuid().ToString();
        ChildProcessHandle child;
        try
        {
            var options = new SessionWorkerOptions(
                _sessionDir,
                new SessionWorkerMetadata(
                    metadata.Id, metadata.CreatedAt, metadata.Cwd, metadata.Path),
                _model?.Provider,
                _model?.Model,
                pluginManifestPaths);
            var env = new Dictionary<string, string?>
            {
                [SessionWorkerEnv.ControlAddress] = _coordinator.ControlPath,
                [SessionWorkerEnv.ControlToken] = token,
                [SessionWorkerEnv.SessionKey] = new(Base64Url.EncodeToChars(Encoding.UTF8.GetBytes(sessionKey))),
                [SessionWorkerEnv.PeerId] = peerId,
            };
            child = _spawn(
                InternalProcessRole.SessionWorker,
                [JsonSerializer.Serialize(options, WorkerJson.Options)],
                new InternalProcess.SpawnInternalProcessOptions { Env = env });
        }
        catch (Exception error)
        {
            return await Task.FromException<WorkerRecord>(error).ConfigureAwait(false);
        }

        var promise = new TaskCompletionSource<WorkerRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new WorkerTimer(_timeouts.StartupMs, () =>
            FailPending(sessionKey, new InvalidOperationException("Session worker startup timed out")));
        var pending = new PendingLaunch
        {
            SessionKey = sessionKey,
            PeerId = peerId,
            Token = token,
            PluginManifestPaths = pluginManifestPaths,
            Child = child,
            Timer = timer,
            Promise = promise,
        };

        lock (_gate)
        {
            _pending[sessionKey] = pending;
        }

        NotifyWorkerCountChanged();

        if (child.StartupError is not null)
        {
            // TS reports a start failure through an asynchronous error event; the port surfaces it
            // as StartupError on the handle, whose exit never settles.
            FailPending(sessionKey, child.StartupError);
            return await promise.Task.ConfigureAwait(false);
        }

        _ = child.WaitAsync().ContinueWith(
            static (task, state) =>
            {
                var (self, launch) = ((SessionWorkerManager, PendingLaunch))state!;
                _ = task.Exception;
                self.ChildExited(launch, task.Result);
            },
            (this, pending),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await promise.Task.ConfigureAwait(false);
    }

    private void FailPending(string sessionKey, Exception error)
    {
        PendingLaunch? pending;
        lock (_gate)
        {
            pending = _pending.GetValueOrDefault(sessionKey);
            if (pending is null) return;
            _pending.Remove(sessionKey);
        }

        pending.Timer.Cancel();
        NotifyWorkerCountChanged();
        if (pending.Child.Id is int pid && !pending.Child.HasExited)
        {
            TryKillTree(pid);
        }

        pending.Promise.TrySetException(error);
    }

    private void RejectDemand(string requestId, Exception error)
    {
        PendingDemand? pending;
        lock (_gate)
        {
            pending = _pendingDemand.GetValueOrDefault(requestId);
            if (pending is null) return;
            _pendingDemand.Remove(requestId);
        }

        pending.Timer.Cancel();
        pending.Reject(error);
    }

    private void RejectOperation(string requestId, Exception error)
    {
        PendingWorkerOperation? pending;
        lock (_gate)
        {
            pending = _pendingOperations.GetValueOrDefault(requestId);
            if (pending is null) return;
            _pendingOperations.Remove(requestId);
        }

        pending.Cleanup();
        pending.Reject(error);
    }

    private void RejectWorkerOperations(WorkerRecord worker, Exception error)
    {
        foreach (var requestId in PendingOperationIds())
        {
            PendingWorkerOperation pending;
            lock (_gate)
            {
                if (!_pendingOperations.TryGetValue(requestId, out pending!)) continue;
            }

            if (ReferenceEquals(pending.Worker, worker)) RejectOperation(requestId, error);
        }
    }

    private async Task ReconcileDemandTimeoutAsync(string requestId)
    {
        PendingDemand? pending;
        lock (_gate)
        {
            pending = _pendingDemand.GetValueOrDefault(requestId);
            if (pending is null) return;
            _pendingDemand.Remove(requestId);
        }

        pending.Timer.Cancel();
        var timeoutError = new InvalidOperationException("Session worker demand update timed out");
        try
        {
            await ApplyDemandAsync(
                pending.Worker, pending.AttachmentId, attached: false, compensateOnTimeout: false)
                .ConfigureAwait(false);
            if (pending.Attached) pending.Reject(timeoutError);
            else pending.Resolve();
        }
        catch (Exception cleanupError)
        {
            try
            {
                await StopWorkerAsync(pending.Worker).ConfigureAwait(false);
            }
            catch (Exception stopError)
            {
                pending.Reject(new AggregateException(
                    "Session worker demand reconciliation and termination failed",
                    timeoutError, cleanupError, stopError));
                return;
            }

            pending.Reject(new AggregateException(
                "Session worker demand reconciliation failed; worker was terminated",
                timeoutError, cleanupError));
        }
    }

    private void RemoveWorker(WorkerRecord worker, Exception? error)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_workersByPeer.GetValueOrDefault(worker.PeerId), worker)) return;
        }

        RejectWorkerOperations(worker, new InvalidOperationException("Session worker disconnected during an operation"));
        RemoveServiceSubscriptions(entry => ReferenceEquals(entry.Worker, worker));
        foreach (var requestId in PendingDemandIds())
        {
            PendingDemand pending;
            lock (_gate)
            {
                if (!_pendingDemand.TryGetValue(requestId, out pending!)) continue;
            }

            if (ReferenceEquals(pending.Worker, worker))
            {
                RejectDemand(requestId, new InvalidOperationException(
                    "Session worker disconnected during demand update"));
            }
        }

        lock (_gate)
        {
            _workersByPeer.Remove(worker.PeerId);
            _workersBySession.Remove(worker.Metadata.Path);
            if (WorkerPids.GetValueOrDefault(worker.Metadata.Id) == worker.Pid)
            {
                WorkerPids.Remove(worker.Metadata.Id);
            }
        }

        worker.Terminated.TrySetResult(error);
        NotifyWorkerCountChanged();
    }

    private void RemoveServiceSubscriptions(Func<WorkerServiceSubscription, bool> matches)
    {
        lock (_gate)
        {
            foreach (var key in _serviceSubscriptions
                .Where(entry => matches(entry.Value))
                .Select(entry => entry.Key)
                .ToList())
            {
                _serviceSubscriptions.Remove(key);
            }
        }
    }

    private void NotifyWorkerCountChanged()
    {
        int count;
        lock (_gate)
        {
            count = _workersBySession.Count + _pending.Count;
        }

        _onWorkerCountChanged?.Invoke(count);
    }

    private PendingLaunch? PendingPeer(string peerId)
    {
        lock (_gate)
        {
            return _pending.Values.FirstOrDefault(pending => pending.PeerId == peerId);
        }
    }

    private void MarkDiscovered(string peerId)
    {
        Action? resolve = null;
        lock (_gate)
        {
            if (_discoveryPeers?.Remove(peerId) != true || _discoveryPeers.Count != 0) return;
            resolve = _resolveDiscovery;
        }

        resolve?.Invoke();
    }

    private List<string> PendingOperationIds()
    {
        lock (_gate)
        {
            return [.. _pendingOperations.Keys];
        }
    }

    private List<string> PendingDemandIds()
    {
        lock (_gate)
        {
            return [.. _pendingDemand.Keys];
        }
    }

    private void DetachState()
    {
        _removeListener();
        foreach (var requestId in PendingOperationIds())
        {
            RejectOperation(requestId, new InvalidOperationException(
                "Experimental server detached during a worker operation"));
        }

        foreach (var requestId in PendingDemandIds())
        {
            PendingDemand pending;
            lock (_gate)
            {
                if (!_pendingDemand.TryGetValue(requestId, out pending!)) continue;
                _pendingDemand.Remove(requestId);
            }

            pending.Timer.Cancel();
            pending.Resolve();
        }

        lock (_gate)
        {
            _pending.Clear();
            _serviceSubscriptions.Clear();
            _workersByPeer.Clear();
            _workersBySession.Clear();
            WorkerPids.Clear();
            _discoveryPeers = null;
            var resolve = _resolveDiscovery;
            _resolveDiscovery = null;
            resolve?.Invoke();
        }
    }

    // ---------------------------------------------------------------- process lifecycle

    /// <summary>
    /// TS's <c>process.kill(worker.pid, "SIGKILL")</c>: kill the worker's process group. A dead or
    /// unkillable process is ignored, exactly as TS ignores ESRCH.
    /// </summary>
    private void KillWorker(WorkerRecord worker) => _killWorker(worker.Pid);

    private static void TryKillTree(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Join(
                        Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows",
                        "System32", "taskkill.exe"),
                    ArgumentList = { "/F", "/T", "/PID", pid.ToString() },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                _ = killer;
            }
            else
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 进程已死或不可杀：忽略（TS 的 ESRCH 分支）。
        }
    }

    /// <summary>Whether the child can no longer produce an exit: it exited, or it never started.</summary>
    private static bool ChildIsDone(ChildProcessHandle child)
        => child.HasExited || child.StartupError is not null;

    // ---------------------------------------------------------------- wire commands

    private Task SendAsync(string peerId, JsonNode payload)
        => _coordinator.SendAsync(peerId, payload);

    private static JsonObject DiscoverWorkersCommand() => new() { ["type"] = "discover_workers" };

    private static JsonObject ShutdownCommand() => new() { ["type"] = "shutdown" };

    private static JsonObject SessionDemandCommand(
        string serverConnectionId, string requestId, string attachmentId, bool attached) => new()
        {
            ["type"] = "session_demand",
            ["serverConnectionId"] = serverConnectionId,
            ["requestId"] = requestId,
            ["attachmentId"] = attachmentId,
            ["attached"] = attached,
        };

    private static JsonObject OperationCommand(
        string requestId, WorkerServiceScope scope, ServiceCall call) => new()
        {
            ["type"] = "operation",
            ["requestId"] = requestId,
            ["scope"] = ScopeJson(scope),
            ["call"] = ServiceCallJson.ToJson(call),
        };

    private static JsonObject OperationCancelCommand(string requestId, WorkerServiceScope scope) => new()
    {
        ["type"] = "operation_cancel",
        ["requestId"] = requestId,
        ["scope"] = ScopeJson(scope),
    };

    private static JsonObject ScopeJson(WorkerServiceScope scope) => new()
    {
        ["serverConnectionId"] = scope.ServerConnectionId,
        ["attachmentId"] = scope.AttachmentId,
    };

    // ---------------------------------------------------------------- helpers

    private static string EventToken(SessionWorkerEvent message) => message switch
    {
        SessionWorkerEvent.WorkerReady ready => ready.Token,
        SessionWorkerEvent.WorkerFailed failed => failed.Token,
        SessionWorkerEvent.DemandApplied applied => applied.Token,
        SessionWorkerEvent.DemandRejected rejected => rejected.Token,
        SessionWorkerEvent.OperationResponse response => response.Token,
        SessionWorkerEvent.ServiceUpdate update => update.Token,
        _ => throw new ArgumentException("unknown session worker event", nameof(message)),
    };

    private static string EventSessionKey(SessionWorkerEvent message) => message switch
    {
        SessionWorkerEvent.WorkerReady ready => ready.SessionKey,
        SessionWorkerEvent.WorkerFailed failed => failed.SessionKey,
        SessionWorkerEvent.DemandApplied applied => applied.SessionKey,
        SessionWorkerEvent.DemandRejected rejected => rejected.SessionKey,
        SessionWorkerEvent.OperationResponse response => response.SessionKey,
        SessionWorkerEvent.ServiceUpdate update => update.SessionKey,
        _ => throw new ArgumentException("unknown session worker event", nameof(message)),
    };

    private static bool SameStrings(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Count == right.Count && left.SequenceEqual(right, StringComparer.Ordinal);

    private static bool SameScope(WorkerServiceScope left, WorkerServiceScope right)
        => left.ServerConnectionId == right.ServerConnectionId && left.AttachmentId == right.AttachmentId;

    private static string ScopedServiceSubscriptionKey(WorkerServiceScope scope, string subscriptionId)
        => $"{scope.ServerConnectionId}\0{scope.AttachmentId}\0{subscriptionId}";

    /// <summary>
    /// TS's <c>Promise.race</c> against an unref'd setTimeout: completes true when the delay wins.
    /// The signal is observed either way, as the race's handlers are.
    /// </summary>
    private static async Task<bool> DelayWinsAsync(Task signal, int milliseconds)
    {
        _ = signal.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        using var cts = new CancellationTokenSource();
        var delay = Task.Delay(milliseconds, cts.Token);
        var winner = await Task.WhenAny(signal, delay).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        return ReferenceEquals(winner, delay);
    }

    /// <summary>Observe a fire-and-forget task so its exception is never left unobserved.</summary>
    private static void Observe(Task task) => _ = task.Exception;
}

/// <summary>
/// The wire form of a service call: the inverse of
/// <see cref="SessionWorkerProtocol.TryParseServiceCall"/>.
/// </summary>
internal static class ServiceCallJson
{
    public static JsonObject ToJson(ServiceCall call)
    {
        var args = new JsonArray();
        foreach (var arg in call.Args)
        {
            args.Add(JsonSerializer.SerializeToNode(arg, WorkerJson.Values));
        }

        var json = new JsonObject
        {
            ["serviceId"] = call.ServiceId,
            ["member"] = call.Member,
            ["args"] = args,
        };
        if (call.Instance is not null)
        {
            json["instance"] = new JsonObject
            {
                ["key"] = call.Instance.Key,
                ["generation"] = call.Instance.Generation,
            };
        }

        return json;
    }
}

/// <summary>The JSON shapes the manager serializes worker options with.</summary>
internal static class WorkerJson
{
    /// <summary>
    /// The options travel as one JSON argument, so their keys must match the TS wire shape
    /// exactly: camelCase, with provider/model omitted when unset (JSON.stringify drops undefined).
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Service call arguments are already wire-encoded values.</summary>
    public static readonly JsonSerializerOptions Values = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
