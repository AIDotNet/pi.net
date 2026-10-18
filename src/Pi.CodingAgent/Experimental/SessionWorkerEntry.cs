// ============================================================================
// Session worker control protocol — port of experimental/session-worker.ts, entry half (4g-3d-3b)
// ============================================================================
//
// The worker half of the session-worker wire: the control connection (register_peer over the
// coordinator, then routed commands in and events out), the command dispatch, the shutdown
// resource teardown, the entry argument validation, and the JSON encoders for the events the
// worker sends. The orchestration that wires them together (WorkerLifecycle + services + the
// readiness handshake) lands with 4g-3d-3c.
//
// The events carry ambient token/sessionKey fields that the wire grammar's records do not model —
// the control's SendAsync merges them, exactly as TS's closure does.

using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental.Services;
using Pi.Durable.Harness;
using ServicesWorkerOptions = Pi.CodingAgent.Experimental.Services.SessionWorkerOptions;

namespace Pi.CodingAgent.Experimental;

/// <summary>
/// The worker-owned resources a shutdown releases. TS <c>SessionWorkerRuntime</c>.
/// </summary>
public sealed record SessionWorkerRuntime(
    HarnessImpl Harness,
    IConversation Conversation,
    ModelRuntime? ModelRuntime = null,
    SettingsManager? SettingsManager = null,
    IFacetLoader? FacetLoader = null,
    Func<Context, Task>? Cleanup = null);

/// <summary>TS <c>CreateSessionWorkerHarness</c>: opens the Session's Harness over its database.</summary>
public delegate Task<SessionWorkerRuntime> CreateSessionWorkerHarness(string databasePath, SessionWorkerOptions options);

/// <summary>
/// The worker's end of the control connection. TS <c>WorkerControl</c> — the socket plus the
/// parsed message stream and the routed-send helper.
/// </summary>
public interface IWorkerControl
{
    string? InitialServerConnectionId { get; }

    IAsyncEnumerable<JsonNode?> Messages { get; }

    Task SendAsync(SessionWorkerEvent message);

    /// <summary>TS <c>control.socket.destroy(new Error(...))</c>: kill the connection with a reason.</summary>
    void Fail(string message);

    event Action<Exception?>? Closed;
}

/// <summary>The handlers <see cref="SessionWorkerControl.ReadCommandsAsync"/> dispatches to.</summary>
public sealed record WorkerCommandHandlers
{
    public required Action OnShutdown { get; init; }

    public required Action OnDiscovery { get; init; }

    public required Func<SessionWorkerCommand.SessionDemand, Task> OnDemand { get; init; }

    public required Action<SessionWorkerCommand.Operation> OnOperation { get; init; }

    public required Action<SessionWorkerCommand.OperationCancel> OnOperationCancel { get; init; }

    public required Action<string> OnServerConnected { get; init; }

    public required Action<string> OnServerDisconnected { get; init; }
}

/// <summary>
/// TS's module-level <c>failureControl</c>: the control a failed startup reports through. The
/// worker process runs exactly one worker, so process-wide state matches TS's module scope.
/// </summary>
public static class SessionWorkerFailure
{
    internal static IWorkerControl? Control { get; set; }
}

/// <summary>
/// The control-connection plumbing: JSON encoders for the worker's events, the connect handshake,
/// the command dispatch, the shutdown teardown, and the entry validation.
/// </summary>
public static class SessionWorkerControl
{
    /// <summary>
    /// TS <c>connectControl</c>: read the launch env, connect, register as a peer, and expect the
    /// coordinator's <c>peer_registered</c> reply.
    /// </summary>
    public static async Task<IWorkerControl> ConnectControlAsync(Func<string, string?> env)
    {
        var address = env(SessionWorkerEnv.ControlAddress);
        var token = env(SessionWorkerEnv.ControlToken);
        var encodedSessionKey = env(SessionWorkerEnv.SessionKey);
        if (address is null || token is null || encodedSessionKey is null)
        {
            throw new InvalidOperationException("Session worker requires a control address");
        }

        var peerId = env(SessionWorkerEnv.PeerId);
        if (peerId is null)
        {
            throw new InvalidOperationException("Session worker requires a peer ID");
        }

        var socket = await CoordinatorSocket.ConnectAsync(address).ConfigureAwait(false);
        var messages = Channel.CreateUnbounded<JsonNode?>();
        socket.StartReading(node => messages.Writer.TryWrite(node));
        socket.Closed += error => messages.Writer.TryComplete();

        await socket.WriteLineAsync(new JsonObject
        {
            ["type"] = "register_peer",
            ["protocol"] = Coordinator.ProtocolVersion,
            ["peerId"] = peerId,
        }).ConfigureAwait(false);

        JsonNode? registered;
        try
        {
            registered = await messages.Reader.ReadAsync().ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException("Coordinator rejected the session worker registration");
        }

        var reply = SessionWorkerProtocol.TryParseCoordinatorInput(registered);
        if (reply is not CoordinatorInput.PeerRegistered peer)
        {
            throw new InvalidOperationException("Coordinator rejected the session worker registration");
        }

        return new CoordinatorWorkerControl(
            socket, messages.Reader.ReadAllAsync(), peer.ServerConnectionId, token, DecodeSessionKey(encodedSessionKey));
    }

    /// <summary>TS <c>readCommands</c>: validate every coordinator input and dispatch the commands.</summary>
    public static async Task ReadCommandsAsync(IWorkerControl control, WorkerCommandHandlers handlers)
    {
        await foreach (var value in control.Messages.ConfigureAwait(false))
        {
            var message = SessionWorkerProtocol.TryParseCoordinatorInput(value);
            if (message is null)
            {
                control.Fail("Coordinator sent an invalid worker message");
                return;
            }

            switch (message)
            {
                case CoordinatorInput.ServerConnected connected:
                    handlers.OnServerConnected(connected.ServerConnectionId);
                    continue;
                case CoordinatorInput.ServerDisconnected disconnected:
                    handlers.OnServerDisconnected(disconnected.ServerConnectionId);
                    continue;
            }

            if (message is not CoordinatorInput.Message routed)
            {
                // A late peer_registered and friends are ignored, as in TS.
                continue;
            }

            var command = SessionWorkerProtocol.TryParseCommand(routed.Payload);
            switch (command)
            {
                case null:
                    continue;
                case SessionWorkerCommand.Shutdown:
                    handlers.OnShutdown();
                    break;
                case SessionWorkerCommand.DiscoverWorkers:
                    handlers.OnDiscovery();
                    break;
                case SessionWorkerCommand.SessionDemand demand:
                    await handlers.OnDemand(demand).ConfigureAwait(false);
                    break;
                case SessionWorkerCommand.OperationCancel cancel:
                    handlers.OnOperationCancel(cancel);
                    break;
                case SessionWorkerCommand.Operation operation:
                    handlers.OnOperation(operation);
                    break;
            }
        }
    }

    /// <summary>
    /// TS <c>closeResources</c>: release everything, aggregating the failures. One error rethrows
    /// itself; several become an AggregateError-shaped exception.
    /// </summary>
    public static async Task CloseResourcesAsync(
        SessionWorkerRuntime? runtime,
        SessionWorkerServices? services,
        Func<Task> releaseOwnership)
    {
        var errors = new List<Exception>();
        await AttemptAsync(() => services is null ? Task.CompletedTask : services.DisposeAsync(), errors).ConfigureAwait(false);
        await AttemptAsync(() => runtime is null ? Task.CompletedTask : runtime.Harness.CloseAsync(Context.Todo), errors).ConfigureAwait(false);
        await AttemptAsync(() => runtime?.Cleanup?.Invoke(Context.Todo) ?? Task.CompletedTask, errors).ConfigureAwait(false);
        await AttemptAsync(releaseOwnership, errors).ConfigureAwait(false);
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1)
        {
            throw new AggregateException("Session worker cleanup failed", errors);
        }
    }

    /// <summary>
    /// TS <c>runSessionWorkerWithHarness</c>: validate the single options argument, run, and report
    /// a startup failure back over the control connection when one exists.
    /// </summary>
    public static async Task RunSessionWorkerWithHarnessAsync(
        IReadOnlyList<string> args,
        CreateSessionWorkerHarness createHarness,
        Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        try
        {
            if (args.Count != 1)
            {
                throw new InvalidOperationException("Session worker requires one options argument");
            }

            SessionWorkerOptions? options;
            try
            {
                options = SessionWorkerProtocol.TryParseOptions(JsonNode.Parse(args[0]));
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("Session worker received invalid options");
            }

            if (options is null
                || !Path.IsPathFullyQualified(options.SessionDir)
                || !Path.IsPathFullyQualified(options.Metadata.Cwd)
                || !Path.IsPathFullyQualified(options.Metadata.Path)
                || (options.Provider is not null && options.Model is null))
            {
                throw new InvalidOperationException("Session worker received invalid options");
            }

            await SessionWorkerEntry.RunAsync(options, createHarness, env).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var token = env(SessionWorkerEnv.ControlToken);
            var encodedSessionKey = env(SessionWorkerEnv.SessionKey);
            var control = SessionWorkerFailure.Control;
            if (token is not null && encodedSessionKey is not null && control is not null)
            {
                try
                {
                    await control.SendAsync(new SessionWorkerEvent.WorkerFailed(
                        token, DecodeSessionKey(encodedSessionKey), error.Message)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // TS: .catch(() => {}) — a dead control line must not mask the original error.
                }
            }

            throw;
        }
    }

    /// <summary>TS <c>Buffer.from(value, "base64url").toString()</c>.</summary>
    public static string DecodeSessionKey(string encoded)
        => Encoding.UTF8.GetString(Base64Url.DecodeFromChars(encoded.AsSpan()));

    private static async Task AttemptAsync(Func<Task> operation, List<Exception> errors)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }
}

/// <summary>The production control: a coordinator socket carrying routed lines.</summary>
public sealed class CoordinatorWorkerControl : IWorkerControl
{
    private readonly CoordinatorSocket _socket;
    private readonly string _token;
    private readonly string _sessionKey;

    public CoordinatorWorkerControl(
        CoordinatorSocket socket,
        IAsyncEnumerable<JsonNode?> messages,
        string? initialServerConnectionId,
        string token,
        string sessionKey)
    {
        _socket = socket;
        Messages = messages;
        InitialServerConnectionId = initialServerConnectionId;
        _token = token;
        _sessionKey = sessionKey;
    }

    public string? InitialServerConnectionId { get; }

    public IAsyncEnumerable<JsonNode?> Messages { get; }

    public event Action<Exception?>? Closed
    {
        add => _socket.Closed += value;
        remove => _socket.Closed -= value;
    }

    /// <summary>TS <c>send</c>: a routed message to the server carrying the event as its payload.</summary>
    public Task SendAsync(SessionWorkerEvent message)
    {
        var payload = SessionWorkerEventJson.ToJson(message);
        payload["token"] = _token;
        payload["sessionKey"] = _sessionKey;
        return _socket.WriteLineAsync(new JsonObject
        {
            ["type"] = "send",
            ["to"] = "server",
            ["payload"] = payload,
        });
    }

    public void Fail(string message) => _socket.Destroy(new InvalidOperationException(message));
}

/// <summary>
/// The JSON encoders for the worker's events — the inverse of
/// <see cref="SessionWorkerProtocol.TryParseEvent"/>, mirroring TS's literals key for key
/// (a null code or result leaves no key behind, as <c>JSON.stringify</c> drops undefined).
/// </summary>
public static class SessionWorkerEventJson
{
    public static JsonObject ToJson(SessionWorkerEvent message)
    {
        var json = new JsonObject();
        switch (message)
        {
            case SessionWorkerEvent.WorkerReady ready:
                json["type"] = "worker_ready";
                json["sessionId"] = ready.SessionId;
                json["pid"] = ready.Pid;
                json["metadata"] = MetadataJson(ready.Metadata);
                var manifestPaths = new JsonArray();
                foreach (var path in ready.PluginManifestPaths)
                {
                    manifestPaths.Add(JsonValue.Create(path));
                }

                json["pluginManifestPaths"] = manifestPaths;
                break;
            case SessionWorkerEvent.WorkerFailed failed:
                json["type"] = "worker_failed";
                json["message"] = failed.Message;
                break;
            case SessionWorkerEvent.DemandApplied applied:
                json["type"] = "demand_applied";
                json["requestId"] = applied.RequestId;
                json["attachmentId"] = applied.AttachmentId;
                json["attached"] = applied.Attached;
                break;
            case SessionWorkerEvent.DemandRejected rejected:
                json["type"] = "demand_rejected";
                json["requestId"] = rejected.RequestId;
                json["message"] = rejected.Message;
                break;
            case SessionWorkerEvent.OperationResponse response:
                json["type"] = "operation_response";
                json["response"] = ResponseJson(response.Response);
                break;
            case SessionWorkerEvent.ServiceUpdate update:
                json["type"] = "service_update";
                json["scope"] = ScopeJson(update.Scope);
                json["subscriptionId"] = update.SubscriptionId;
                json["update"] = update.Update?.DeepClone();
                break;
            default:
                throw new ArgumentException("unknown session worker event", nameof(message));
        }

        return json;
    }

    public static JsonObject MetadataJson(SessionWorkerMetadata metadata) => new()
    {
        ["id"] = metadata.Id,
        ["createdAt"] = metadata.CreatedAt,
        ["cwd"] = metadata.Cwd,
        ["path"] = metadata.Path,
    };

    public static JsonObject ScopeJson(WorkerServiceScope scope) => new()
    {
        ["serverConnectionId"] = scope.ServerConnectionId,
        ["attachmentId"] = scope.AttachmentId,
    };

    private static JsonObject ResponseJson(WorkerOperationResponse response)
    {
        switch (response)
        {
            case WorkerOperationResponse.Result result:
            {
                var json = new JsonObject
                {
                    ["type"] = "operation_result",
                    ["requestId"] = result.RequestId,
                    ["scope"] = ScopeJson(result.Scope),
                };
                if (result.Value is not null)
                {
                    json["result"] = result.Value.DeepClone();
                }

                return json;
            }
            case WorkerOperationResponse.Error error:
            {
                var json = new JsonObject
                {
                    ["type"] = "operation_error",
                    ["requestId"] = error.RequestId,
                    ["scope"] = ScopeJson(error.Scope),
                    ["message"] = error.Message,
                };
                if (error.Code is not null)
                {
                    json["code"] = error.Code;
                }

                return json;
            }
            default:
                throw new ArgumentException("unknown operation response", nameof(response));
        }
    }
}

/// <summary>
/// The encode counterpart of <see cref="ServiceWire"/>'s update parser: a decoded provider update,
/// already compressed by a <see cref="ServiceStateEncoder"/>, as the JSON the worker's
/// <c>service_update</c> events carry. TS gets this for free because its updates are plain objects;
/// the C# provider emits decoded records, so the shape has to be written out.
/// </summary>
public static class ServiceUpdateWireJson
{
    public static JsonObject ToJson(WireServiceProviderUpdate update)
    {
        switch (update)
        {
            case WireServiceProviderUpdate.StateUpdate state:
            {
                var json = new JsonObject
                {
                    ["type"] = "state",
                    ["member"] = state.Member,
                    ["sequence"] = state.Sequence,
                    ["ops"] = OpsJson(state.Ops),
                };
                if (state.Instance is not null)
                {
                    json["instance"] = AddressJson(state.Instance);
                }

                return json;
            }
            case WireServiceProviderUpdate.Reset reset:
                return new JsonObject
                {
                    ["type"] = "reset",
                    ["snapshot"] = SnapshotJson(reset.Snapshot),
                };
            case WireServiceProviderUpdate.Unavailable:
                return new JsonObject { ["type"] = "unavailable" };
            case WireServiceProviderUpdate.Replaced replaced:
                return new JsonObject
                {
                    ["type"] = "replaced",
                    ["snapshot"] = InstanceJson(replaced.Snapshot),
                };
            case WireServiceProviderUpdate.Spawned spawned:
                return new JsonObject
                {
                    ["type"] = "spawned",
                    ["instance"] = InstanceJson(spawned.Instance),
                };
            case WireServiceProviderUpdate.Closed closed:
                return new JsonObject
                {
                    ["type"] = "closed",
                    ["instance"] = AddressJson(closed.Instance),
                };
            default:
                throw new ArgumentException("unknown service provider update", nameof(update));
        }
    }

    private static JsonObject AddressJson(ServiceInstanceAddress address) => new()
    {
        ["key"] = address.Key,
        ["generation"] = address.Generation,
    };

    private static JsonObject InstanceJson(WireServiceInstanceSnapshot instance)
    {
        var json = new JsonObject { ["members"] = MembersJson(instance.Members) };
        if (instance.Instance is not null)
        {
            json["instance"] = AddressJson(instance.Instance);
        }

        return json;
    }

    private static JsonArray MembersJson(IReadOnlyList<WireServiceMemberSnapshot> members)
    {
        var json = new JsonArray();
        foreach (var member in members)
        {
            json.Add(member switch
            {
                WireServiceMemberSnapshot.Method method => new JsonObject
                {
                    ["kind"] = "method",
                    ["name"] = method.Name,
                },
                WireServiceMemberSnapshot.State state => new JsonObject
                {
                    ["kind"] = "state",
                    ["name"] = state.Name,
                    ["sequence"] = state.Sequence,
                    ["ops"] = OpsJson(state.Ops),
                },
                _ => throw new ArgumentException("unknown service member", nameof(members)),
            });
        }

        return json;
    }

    private static JsonArray InstancesJson(IReadOnlyList<WireServiceInstanceSnapshot> instances)
    {
        var json = new JsonArray();
        foreach (var instance in instances)
        {
            json.Add(InstanceJson(instance));
        }

        return json;
    }

    private static JsonObject SnapshotJson(WireServiceSubscriptionSnapshot snapshot) => new()
    {
        ["serviceId"] = snapshot.ServiceId,
        ["mode"] = snapshot.Mode == ServiceMode.Singleton ? "singleton" : "keyed",
        ["instances"] = InstancesJson(snapshot.Instances),
    };

    /// <summary>The ops are already wire-encoded values: plain JSON primitives, arrays and objects.</summary>
    private static JsonArray OpsJson(IReadOnlyList<List<object?>> ops)
    {
        var json = new JsonArray();
        foreach (var op in ops)
        {
            json.Add(JsonSerializer.SerializeToNode(op));
        }

        return json;
    }
}

/// <summary>
/// The worker entry's orchestration and argument validation. Port of TS <c>run</c> /
/// <c>runSessionWorkerWithHarness</c>; the harness factory stays injectable, as in TS.
/// </summary>
public static class SessionWorkerEntry
{
    /// <summary>
    /// TS <c>run</c>: own the session, open its services, and serve commands until shutdown.
    /// </summary>
    public static async Task RunAsync(
        SessionWorkerOptions options,
        CreateSessionWorkerHarness createHarness,
        Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var control = await SessionWorkerControl.ConnectControlAsync(env).ConfigureAwait(false);
        SessionWorkerFailure.Control = control;
        var token = env(SessionWorkerEnv.ControlToken)!;
        var sessionKey = SessionWorkerControl.DecodeSessionKey(env(SessionWorkerEnv.SessionKey)!);
        var pluginManifestPaths = options.PluginManifestPaths;

        var releaseOwnership = await NodeLock.AcquireAsync(
            options.Metadata.Path, CancellationToken.None, null, staleMs: WorkerLockStaleMs).ConfigureAwait(false);

        SessionWorkerRuntime? runtime = null;
        SessionWorkerServices? services = null;
        AttachedReplicatedState<IReadOnlyDictionary<string, object?>>? activity = null;
        try
        {
            runtime = await createHarness(SessionCatalog.SessionStoragePath(new SessionCatalogMetadata(
                options.Metadata.Id, options.Metadata.CreatedAt, options.Metadata.Cwd, options.Metadata.Path)),
            options).ConfigureAwait(false);
            activity = await runtime.Harness.TaskGraphAsync(Context.Todo).ConfigureAwait(false);
            var encoders = new Dictionary<(WorkerServiceScope Scope, string SubscriptionId), ServiceStateEncoder>();
            var built = await SessionWorkerProvider.CreateSessionWorkerServicesAsync(new ServicesWorkerOptions
            {
                Harness = runtime.Harness,
                Conversation = runtime.Conversation,
                ModelRuntime = runtime.ModelRuntime,
                SettingsManager = runtime.SettingsManager,
                FacetLoader = runtime.FacetLoader,
                Publish = (scope, subscriptionId, update) =>
                {
                    // TS receives wire updates and re-parses them; the C# provider emits decoded
                    // updates, so the worker owns one encoder per subscription.
                    if (!encoders.TryGetValue((scope, subscriptionId), out var encoder))
                    {
                        encoder = new ServiceStateEncoder();
                        encoders[(scope, subscriptionId)] = encoder;
                    }

                    return control.SendAsync(new SessionWorkerEvent.ServiceUpdate(
                        token, sessionKey, scope, subscriptionId,
                        ServiceUpdateWireJson.ToJson(encoder.EncodeUpdate(update))));
                },
            }).ConfigureAwait(false);

            // Drop the encoders of whatever subscriptions the worker removes.
            services = new SessionWorkerServices
            {
                Invoke = built.Invoke,
                RemoveSubscriptions = matches =>
                {
                    built.RemoveSubscriptions(matches);
                    foreach (var key in encoders.Keys.Where(key => matches(key.Scope)).ToList())
                    {
                        encoders.Remove(key);
                    }
                },
                Dispose = built.Dispose,
            };
        }
        catch (Exception error)
        {
            activity?.Dispose();
            try
            {
                await SessionWorkerControl.CloseResourcesAsync(runtime, services, releaseOwnership).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Session worker startup and cleanup failed", error, cleanupError);
            }

            throw;
        }

        var activeRequests = new Dictionary<string, (WorkerServiceScope Scope, Action Cancel)>();
        var harness = runtime.Harness;
        WorkerLifecycle? lifecycle = null;
        var stopActivity = (IDisposable?)null;
        Task? closing = null;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task CloseAsync()
        {
            if (closing is not null) return closing;
            lifecycle?.Close();
            if (stopActivity is not null)
            {
                stopActivity.Dispose();
                stopActivity = null;
            }

            activity.Dispose();
            services!.RemoveSubscriptions(_ => true);
            foreach (var request in activeRequests.Values)
            {
                request.Cancel();
            }

            activeRequests.Clear();
            closing = SessionWorkerControl.CloseResourcesAsync(runtime, services, releaseOwnership);
            return closing;
        }

        // TS exits the process from here; the port completes the run instead and lets the process
        // entry perform the exit, which keeps the orchestration testable.
        void CloseAndExit()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await CloseAsync().ConfigureAwait(false);
                    closed.TrySetResult();
                }
                catch (Exception error)
                {
                    closed.TrySetException(error);
                }
            });
        }

        lifecycle = new WorkerLifecycle(
            control.InitialServerConnectionId,
            SessionWorkerProtocol.LifecycleDelay(SessionWorkerEnv.InitialDemandGraceMs, DefaultInitialDemandGraceMs, env),
            SessionWorkerProtocol.LifecycleDelay(SessionWorkerEnv.OrphanDemandGraceMs, DefaultOrphanDemandGraceMs, env),
            CloseAndExit);

        // Every live task, background work included, keeps the worker and its Session open.
        var activitySubscription = activity.Subscribe((graph, _, _) =>
        {
            lifecycle!.SetHarnessActive(graph.Count > 0);
            return (Task?)null;
        });
        activity.Activate();
        stopActivity = activitySubscription;

        // Recovered work from an interrupted turn continues now.
        harness.Resume();

        async Task HandleOperationAsync(SessionWorkerCommand.Operation request)
        {
            Action releaseRequest = () => { };
            var cancellable = ContextSignals.WithCancel(Context.Background);
            try
            {
                releaseRequest = lifecycle!.BeginRequest(request.Scope.ServerConnectionId, request.Scope.AttachmentId);
                activeRequests[request.RequestId] = (request.Scope, cancellable.Cancel);
                var result = await services!.InvokeAsync(request.Call, request.Scope, cancellable.Context).ConfigureAwait(false);
                if (result is not null && !Json.IsJsonValue(result))
                {
                    throw new InvalidOperationException("Service produced a non-JSON result");
                }

                await control.SendAsync(new SessionWorkerEvent.OperationResponse(
                    token,
                    sessionKey,
                    new WorkerOperationResponse.Result(
                        request.RequestId,
                        request.Scope,
                        result is null ? null : JsonSerializer.SerializeToNode(result)))).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                string? code = null;
                if (error is RemoteServiceError remote)
                {
                    code = remote.Code;
                }
                else if (error is Exception withCode && withCode.Data.Contains("code"))
                {
                    var candidate = withCode.Data["code"];
                    if (candidate is string text && RemoteServiceErrorCodes.IsRemoteServiceErrorCode(text))
                    {
                        code = text;
                    }
                }

                await control.SendAsync(new SessionWorkerEvent.OperationResponse(
                    token,
                    sessionKey,
                    new WorkerOperationResponse.Error(
                        request.RequestId,
                        request.Scope,
                        code,
                        error.Message))).ConfigureAwait(false);
            }
            finally
            {
                if (activeRequests.TryGetValue(request.RequestId, out var active)
                    && active.Cancel == cancellable.Cancel)
                {
                    activeRequests.Remove(request.RequestId);
                }

                releaseRequest();
            }
        }

        var ready = false;
        void Announce()
        {
            if (!ready) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await control.SendAsync(new SessionWorkerEvent.WorkerReady(
                        token,
                        sessionKey,
                        options.Metadata.Id,
                        Environment.ProcessId,
                        options.Metadata,
                        pluginManifestPaths)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    CloseAndExit();
                }
            });
        }

        var commands = SessionWorkerControl.ReadCommandsAsync(control, new WorkerCommandHandlers
        {
            OnShutdown = CloseAndExit,
            OnDiscovery = Announce,
            OnDemand = async command =>
            {
                var releaseRetirement = lifecycle!.HoldRetirement();
                try
                {
                    try
                    {
                        if (!command.Attached)
                        {
                            services!.RemoveSubscriptions(scope =>
                                scope.ServerConnectionId == command.ServerConnectionId
                                && scope.AttachmentId == command.AttachmentId);
                        }

                        lifecycle.SetDemand(command.ServerConnectionId, command.AttachmentId, command.Attached);
                    }
                    catch (Exception error)
                    {
                        await control.SendAsync(new SessionWorkerEvent.DemandRejected(
                            token, sessionKey, command.RequestId, error.Message)).ConfigureAwait(false);
                        return;
                    }

                    await control.SendAsync(new SessionWorkerEvent.DemandApplied(
                        token, sessionKey, command.RequestId, command.AttachmentId, command.Attached)).ConfigureAwait(false);
                }
                finally
                {
                    releaseRetirement();
                }
            },
            OnOperation = request =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleOperationAsync(request).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        CloseAndExit();
                    }
                });
            },
            OnOperationCancel = command =>
            {
                if (activeRequests.TryGetValue(command.RequestId, out var active)
                    && active.Scope == command.Scope)
                {
                    active.Cancel();
                }
            },
            OnServerConnected = serverConnectionId => lifecycle!.ServerConnected(serverConnectionId),
            OnServerDisconnected = serverConnectionId =>
            {
                services!.RemoveSubscriptions(scope => scope.ServerConnectionId == serverConnectionId);
                foreach (var request in activeRequests.Values.ToArray())
                {
                    if (request.Scope.ServerConnectionId == serverConnectionId)
                    {
                        request.Cancel();
                    }
                }

                lifecycle!.ServerDisconnected(serverConnectionId);
            },
        });

        _ = commands.ContinueWith(
            task =>
            {
                // Touch Exception so a faulted dispatch is observed, not left to the finalizer.
                _ = task.Exception;
                if (task.IsFaulted) CloseAndExit();
            },
            System.Threading.Tasks.TaskScheduler.Default);

        control.Closed += _ => CloseAndExit();

        try
        {
            ready = true;
            await control.SendAsync(new SessionWorkerEvent.WorkerReady(
                token,
                sessionKey,
                options.Metadata.Id,
                Environment.ProcessId,
                options.Metadata,
                pluginManifestPaths)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            try
            {
                await CloseAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Session worker readiness and cleanup failed", error, cleanupError);
            }

            throw;
        }

        // TS never resolves run() because its close paths exit the process; here the run ends
        // when the worker has released everything, and the entry point exits.
        await closed.Task.ConfigureAwait(false);
    }

    /// <summary>TS <c>DEFAULT_INITIAL_DEMAND_GRACE_MS</c>.</summary>
    public const int DefaultInitialDemandGraceMs = 10_000;

    /// <summary>TS <c>DEFAULT_ORPHAN_DEMAND_GRACE_MS</c>.</summary>
    public const int DefaultOrphanDemandGraceMs = 30_000;

    /// <summary>
    /// The session lock's stale/retry window. TS retries for 8s and breaks locks older than 2s;
    /// the C# lock couples the two, so the retry window wins (a live holder refreshes its mtime
    /// well inside it).
    /// </summary>
    public const int WorkerLockStaleMs = 8_000;
}
