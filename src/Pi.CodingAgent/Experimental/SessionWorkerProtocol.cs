// ============================================================================
// Session worker protocol — port of experimental/session-worker.ts, protocol half (4g-3d-3a)
// ============================================================================
//
// The session worker is a replaceable child process that owns one durable session. It talks to the
// server over the coordinator: commands arrive as routed messages, events go back as routed
// messages. This file holds the wire grammar (the typebox schemas, mirrored as records plus
// validators with the same strictness) and WorkerLifecycle, the worker-local reconciliation of
// server-generation demand against Harness activity. The process entry itself (control socket,
// harness wiring) lands with 4g-3d-3b.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Chord.Services;
using Pi.CodingAgent.Experimental.Services;

namespace Pi.CodingAgent.Experimental;

/// <summary>TS <c>SESSION_WORKER_CONTROL_ADDRESS_ENV</c> and friends: the worker's launch contract.</summary>
public static class SessionWorkerEnv
{
    public const string ControlAddress = "PI_SESSION_WORKER_CONTROL_ADDRESS";
    public const string ControlToken = "PI_SESSION_WORKER_CONTROL_TOKEN";
    public const string SessionKey = "PI_SESSION_WORKER_SESSION_KEY_BASE64";
    public const string PeerId = "PI_SESSION_WORKER_PEER_ID";

    /// <summary>TS <c>SESSION_WORKER_INITIAL_DEMAND_GRACE_ENV</c> (test-only override).</summary>
    public const string InitialDemandGraceMs = "__PI_SESSION_WORKER_INITIAL_DEMAND_GRACE_MS";

    /// <summary>TS <c>SESSION_WORKER_ORPHAN_DEMAND_GRACE_ENV</c> (test-only override).</summary>
    public const string OrphanDemandGraceMs = "__PI_SESSION_WORKER_ORPHAN_DEMAND_GRACE_MS";
}

/// <summary>TS <c>SessionWorkerMetadataSchema</c>.</summary>
public sealed record SessionWorkerMetadata(string Id, double CreatedAt, string Cwd, string Path);

/// <summary>TS <c>SessionWorkerOptionsSchema</c>.</summary>
public sealed record SessionWorkerOptions(
    string SessionDir,
    SessionWorkerMetadata Metadata,
    string? Provider,
    string? Model,
    IReadOnlyList<string> PluginManifestPaths);

/// <summary>TS <c>WorkerOperationScopeSchema</c>; an alias of <see cref="WorkerServiceScope"/>.</summary>
public sealed record WorkerOperationScope(string ServerConnectionId, string AttachmentId)
{
    /// <summary>TS <c>sameScope</c>.</summary>
    public bool SameAs(WorkerOperationScope other)
        => ServerConnectionId == other.ServerConnectionId && AttachmentId == other.AttachmentId;
}

/// <summary>TS <c>WorkerOperationRequestSchema</c>.</summary>
public sealed record WorkerOperationRequest(string RequestId, WorkerServiceScope Scope, ServiceCall Call);

/// <summary>TS <c>WorkerOperationResponseSchema</c> (the two union members).</summary>
public abstract record WorkerOperationResponse
{
    public sealed record Result(string RequestId, WorkerServiceScope Scope, JsonNode? Value) : WorkerOperationResponse;

    public sealed record Error(string RequestId, WorkerServiceScope Scope, string? Code, string Message)
        : WorkerOperationResponse;
}

/// <summary>TS <c>SessionWorkerCommandSchema</c>.</summary>
public abstract record SessionWorkerCommand
{
    public sealed record Shutdown : SessionWorkerCommand;

    public sealed record DiscoverWorkers : SessionWorkerCommand;

    public sealed record SessionDemand(string ServerConnectionId, string RequestId, string AttachmentId, bool Attached)
        : SessionWorkerCommand;

    public sealed record Operation(string RequestId, WorkerServiceScope Scope, ServiceCall Call)
        : SessionWorkerCommand;

    public sealed record OperationCancel(string RequestId, WorkerServiceScope Scope) : SessionWorkerCommand;
}

/// <summary>TS <c>SessionWorkerEventSchema</c>.</summary>
public abstract record SessionWorkerEvent
{
    public sealed record WorkerReady(
        string Token,
        string SessionKey,
        string SessionId,
        long Pid,
        SessionWorkerMetadata Metadata,
        IReadOnlyList<string> PluginManifestPaths) : SessionWorkerEvent;

    public sealed record WorkerFailed(string Token, string SessionKey, string Message) : SessionWorkerEvent;

    public sealed record DemandApplied(
        string Token, string SessionKey, string RequestId, string AttachmentId, bool Attached) : SessionWorkerEvent;

    public sealed record DemandRejected(string Token, string SessionKey, string RequestId, string Message)
        : SessionWorkerEvent;

    public sealed record OperationResponse(string Token, string SessionKey, WorkerOperationResponse Response)
        : SessionWorkerEvent;

    public sealed record ServiceUpdate(
        string Token, string SessionKey, WorkerServiceScope Scope, string SubscriptionId, JsonNode? Update)
        : SessionWorkerEvent;
}

/// <summary>TS <c>CoordinatorInputSchema</c>: what the worker receives over its control socket.</summary>
public abstract record CoordinatorInput
{
    public sealed record PeerRegistered(string PeerId, string? ServerConnectionId) : CoordinatorInput;

    public sealed record ServerConnected(string ServerConnectionId) : CoordinatorInput;

    public sealed record ServerDisconnected(string ServerConnectionId) : CoordinatorInput;

    /// <summary>A routed message from the server; the payload is a <see cref="SessionWorkerCommand"/>.</summary>
    public sealed record Message(JsonNode? Payload) : CoordinatorInput;
}

/// <summary>
/// The session worker wire grammar. TS validates with typebox <c>Check</c>; these parsers mirror
/// each schema's strictness exactly: <c>StrictObject</c> rejects unknown keys, plain
/// <c>Type.Object</c> ignores them, <c>Type.Optional</c> allows absence but not null.
/// </summary>
public static class SessionWorkerProtocol
{
    /// <summary>TS <c>demandKey</c>: the NUL separator keeps ids from colliding.</summary>
    public static string DemandKey(string serverConnectionId, string attachmentId)
        => $"{serverConnectionId}\0{attachmentId}";

    /// <summary>
    /// TS <c>lifecycleDelay</c>: a non-negative safe integer override, else the fallback.
    /// </summary>
    public static int LifecycleDelay(string name, int fallback, Func<string, string?> env)
    {
        var value = env(name);
        if (value is null) return fallback;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0
            || parsed > 9007199254740991L)
        {
            throw new InvalidOperationException($"{name} must be a non-negative safe integer");
        }

        return (int)Math.Min(parsed, int.MaxValue);
    }

    // ---------------------------------------------------------------- metadata / options

    public static SessionWorkerMetadata? TryParseMetadata(JsonNode? node)
    {
        if (node is not JsonObject message) return null;
        if (!IsStrict(message, "id", "createdAt", "cwd", "path")) return null;
        var id = StringField(message, "id", minLength: 1);
        var createdAt = NumberField(message, "createdAt");
        var cwd = StringField(message, "cwd", minLength: 0);
        var path = StringField(message, "path", minLength: 0);
        if (id is null || createdAt is null || cwd is null || path is null) return null;
        return new SessionWorkerMetadata(id, createdAt.Value, cwd, path);
    }

    public static SessionWorkerOptions? TryParseOptions(JsonNode? node)
    {
        if (node is not JsonObject message) return null;
        if (!IsStrict(message, "sessionDir", "metadata", "provider", "model", "pluginManifestPaths")) return null;
        var sessionDir = StringField(message, "sessionDir", minLength: 1);
        var metadata = TryParseMetadata(message["metadata"]);
        var pluginManifestPaths = StringArrayField(message, "pluginManifestPaths", minLength: 1);
        if (sessionDir is null || metadata is null || pluginManifestPaths is null) return null;

        // Type.Optional allows absence, never null: a present key must satisfy the inner schema.
        // JsonNode.Parse maps an explicit JSON null to a C# null, so absence needs ContainsKey.
        string? provider = null;
        if (IsExplicitNull(message, "provider")) return null;
        if (message.ContainsKey("provider"))
        {
            provider = StringField(message, "provider", minLength: 1);
            if (provider is null) return null;
        }

        string? model = null;
        if (IsExplicitNull(message, "model")) return null;
        if (message.ContainsKey("model"))
        {
            model = StringField(message, "model", minLength: 1);
            if (model is null) return null;
        }

        return new SessionWorkerOptions(sessionDir, metadata, provider, model, pluginManifestPaths);
    }

    // ---------------------------------------------------------------- scope / calls

    /// <summary>TS <c>WorkerOperationScopeSchema</c>; an alias of <see cref="WorkerServiceScope"/>.</summary>
    public static WorkerServiceScope? TryParseScope(JsonNode? node)
    {
        if (node is not JsonObject message) return null;
        if (!IsStrict(message, "serverConnectionId", "attachmentId")) return null;
        var serverConnectionId = StringField(message, "serverConnectionId", minLength: 0);
        var attachmentId = StringField(message, "attachmentId", minLength: 0);
        if (serverConnectionId is null || attachmentId is null) return null;
        return new WorkerServiceScope(serverConnectionId, attachmentId);
    }

    public static ServiceCall? TryParseServiceCall(JsonNode? node)
    {
        if (node is not JsonObject message) return null;
        if (!IsStrict(message, "serviceId", "instance", "member", "args")) return null;
        var serviceId = StringField(message, "serviceId", minLength: 1);
        var member = StringField(message, "member", minLength: 1);
        ServiceInstanceAddress? instance = null;
        if (IsExplicitNull(message, "instance")) return null;
        if (message["instance"] is not null)
        {
            if (message["instance"] is not JsonObject instanceObject) return null;
            if (!IsStrict(instanceObject, "key", "generation")) return null;
            var key = StringField(instanceObject, "key", minLength: 1);
            var generation = IntegerField(instanceObject, "generation", minimum: 1);
            if (key is null || generation is null) return null;
            instance = new ServiceInstanceAddress(key, generation.Value);
        }

        // An explicit null is not an absent optional: typebox's Optional rejects it.
        if (IsExplicitNull(message, "instance")) return null;
        if (message["args"] is not JsonArray args) return null;
        if (serviceId is null || member is null) return null;
        // The args cross as the CLR value tree the service layer speaks: the chord endpoint's
        // control-call decoder pattern-matches string arguments, and the wire adapters read
        // dictionaries and lists — a JsonNode argument matches none of them.
        var arguments = new object?[args.Count];
        for (var index = 0; index < args.Count; index++)
        {
            arguments[index] = ClrValue(args[index]);
        }

        return new ServiceCall(serviceId, member, arguments, instance);
    }

    /// <summary>
    /// A JSON-line value as the CLR value tree: objects become dictionaries, arrays lists, and
    /// numbers stay integral where the JSON backs them integrally (the wire parsers and the
    /// service adapters speak those types). Corresponds to the value the JSON line carries in TS,
    /// where every JSON value already is the value tree.
    /// </summary>
    internal static object? ClrValue(JsonNode? node) => node switch
    {
        null => null,
        JsonObject json => json.ToDictionary(
            pair => pair.Key, pair => ClrValue(pair.Value), StringComparer.Ordinal),
        JsonArray array => array.Select(ClrValue).ToList(),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => NumberValue(value),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        },
        _ => null,
    };

    private static object? NumberValue(JsonValue value)
    {
        // In-process JSON (the event encoders' output and the test fakes' literals) backs numbers
        // with CLR longs/ints, and GetValue<double>() rejects an integral backing; read the
        // integral forms first, as ToolTask does (C358).
        if (value.TryGetValue<long>(out var integer)) return integer;
        if (value.TryGetValue<int>(out var small)) return (long)small;
        return value.GetValue<double>();
    }

    // ---------------------------------------------------------------- operation protocol

    public static WorkerOperationRequest? TryParseOperationRequest(JsonNode? node)
    {
        if (node is not JsonObject message) return null;
        if (!IsStrict(message, "type", "requestId", "scope", "call")) return null;
        if (!HasLiteralType(message, "operation")) return null;
        var requestId = StringField(message, "requestId", minLength: 1);
        var scope = TryParseScope(message["scope"]);
        var call = TryParseServiceCall(message["call"]);
        if (requestId is null || scope is null || call is null) return null;
        return new WorkerOperationRequest(requestId, scope, call);
    }

    public static WorkerOperationResponse? TryParseOperationResponse(JsonNode? node)
    {
        if (node is not JsonObject message) return null;
        var type = LiteralType(message);
        if (type == "operation_result")
        {
            if (!IsStrict(message, "type", "requestId", "scope", "result")) return null;
            var requestId = StringField(message, "requestId", minLength: 1);
            var scope = TryParseScope(message["scope"]);
            if (requestId is null || scope is null) return null;
            return new WorkerOperationResponse.Result(requestId, scope, message["result"]);
        }

        if (type == "operation_error")
        {
            if (!IsStrict(message, "type", "requestId", "scope", "code", "message")) return null;
            var requestId = StringField(message, "requestId", minLength: 1);
            var scope = TryParseScope(message["scope"]);
            var text = StringField(message, "message", minLength: 0);
            if (requestId is null || scope is null || text is null) return null;
            string? code = null;
            if (message["code"] is not null)
            {
                code = StringField(message, "code", minLength: 0);
                if (code is null || !RemoteServiceErrorCodes.IsRemoteServiceErrorCode(code)) return null;
            }

            return new WorkerOperationResponse.Error(requestId, scope, code, text);
        }

        return null;
    }

    // ---------------------------------------------------------------- commands

    public static SessionWorkerCommand? TryParseCommand(JsonNode? node)
    {
        if (node is not JsonObject message) return null;
        var type = LiteralType(message);
        switch (type)
        {
            case "shutdown":
            case "discover_workers":
                // TS uses a plain Type.Object here: unknown keys are ignored.
                return type == "shutdown" ? new SessionWorkerCommand.Shutdown() : new SessionWorkerCommand.DiscoverWorkers();
            case "session_demand":
            {
                var serverConnectionId = StringField(message, "serverConnectionId", minLength: 0);
                var requestId = StringField(message, "requestId", minLength: 0);
                var attachmentId = StringField(message, "attachmentId", minLength: 0);
                var attached = BoolField(message, "attached");
                if (serverConnectionId is null || requestId is null || attachmentId is null || attached is null)
                {
                    return null;
                }

                return new SessionWorkerCommand.SessionDemand(serverConnectionId, requestId, attachmentId, attached.Value);
            }
            case "operation":
                var operation = TryParseOperationRequest(message);
                return operation is null
                    ? null
                    : new SessionWorkerCommand.Operation(operation.RequestId, operation.Scope, operation.Call);
            case "operation_cancel":
            {
                if (!IsStrict(message, "type", "requestId", "scope")) return null;
                var requestId = StringField(message, "requestId", minLength: 1);
                var scope = TryParseScope(message["scope"]);
                if (requestId is null || scope is null) return null;
                return new SessionWorkerCommand.OperationCancel(requestId, scope);
            }
            default:
                return null;
        }
    }

    // ---------------------------------------------------------------- events

    public static SessionWorkerEvent? TryParseEvent(JsonNode? node)
    {
        if (node is not JsonObject message) return null;
        var type = LiteralType(message);
        switch (type)
        {
            case "worker_ready":
            {
                // Plain Type.Object members: unknown keys ignored.
                var token = StringField(message, "token", minLength: 0);
                var sessionKey = StringField(message, "sessionKey", minLength: 0);
                var sessionId = StringField(message, "sessionId", minLength: 0);
                var pid = IntegerField(message, "pid", minimum: 1);
                var metadata = TryParseMetadata(message["metadata"]);
                var pluginManifestPaths = StringArrayField(message, "pluginManifestPaths", minLength: 1);
                if (token is null || sessionKey is null || sessionId is null || pid is null || metadata is null
                    || pluginManifestPaths is null)
                {
                    return null;
                }

                return new SessionWorkerEvent.WorkerReady(token, sessionKey, sessionId, pid.Value, metadata, pluginManifestPaths);
            }
            case "worker_failed":
            {
                var (token, sessionKey) = TokenAndKey(message);
                var text = StringField(message, "message", minLength: 0);
                if (token is null || sessionKey is null || text is null) return null;
                return new SessionWorkerEvent.WorkerFailed(token, sessionKey, text);
            }
            case "demand_applied":
            {
                var (token, sessionKey) = TokenAndKey(message);
                var requestId = StringField(message, "requestId", minLength: 0);
                var attachmentId = StringField(message, "attachmentId", minLength: 0);
                var attached = BoolField(message, "attached");
                if (token is null || sessionKey is null || requestId is null || attachmentId is null || attached is null)
                {
                    return null;
                }

                return new SessionWorkerEvent.DemandApplied(token, sessionKey, requestId, attachmentId, attached.Value);
            }
            case "demand_rejected":
            {
                var (token, sessionKey) = TokenAndKey(message);
                var requestId = StringField(message, "requestId", minLength: 0);
                var text = StringField(message, "message", minLength: 0);
                if (token is null || sessionKey is null || requestId is null || text is null) return null;
                return new SessionWorkerEvent.DemandRejected(token, sessionKey, requestId, text);
            }
            case "operation_response":
            {
                var (token, sessionKey) = TokenAndKey(message);
                var response = TryParseOperationResponse(message["response"]);
                if (token is null || sessionKey is null || response is null) return null;
                return new SessionWorkerEvent.OperationResponse(token, sessionKey, response);
            }
            case "service_update":
            {
                var (token, sessionKey) = TokenAndKey(message);
                var scope = TryParseScope(message["scope"]);
                var subscriptionId = StringField(message, "subscriptionId", minLength: 1);
                if (token is null || sessionKey is null || scope is null || subscriptionId is null) return null;
                return new SessionWorkerEvent.ServiceUpdate(token, sessionKey, scope, subscriptionId, message["update"]);
            }
            default:
                return null;
        }
    }

    // ---------------------------------------------------------------- coordinator input

    public static CoordinatorInput? TryParseCoordinatorInput(JsonNode? node)
    {
        if (node is not JsonObject message) return null;
        var type = LiteralType(message);
        switch (type)
        {
            case "peer_registered":
            {
                var peerId = StringField(message, "peerId", minLength: 0);
                if (peerId is null) return null;
                if (!message.ContainsKey("serverConnectionId"))
                {
                    return new CoordinatorInput.PeerRegistered(peerId, null);
                }

                if (IsExplicitNull(message, "serverConnectionId")) return null;
                var serverConnectionId = StringField(message, "serverConnectionId", minLength: 0);
                if (serverConnectionId is null) return null;
                return new CoordinatorInput.PeerRegistered(peerId, serverConnectionId);
            }
            case "server_connected":
            case "server_disconnected":
            {
                var serverConnectionId = StringField(message, "serverConnectionId", minLength: 0);
                if (serverConnectionId is null) return null;
                return type == "server_connected"
                    ? new CoordinatorInput.ServerConnected(serverConnectionId)
                    : new CoordinatorInput.ServerDisconnected(serverConnectionId);
            }
            case "message":
            {
                var from = StringField(message, "from", minLength: 0);
                if (from != "server") return null;
                return new CoordinatorInput.Message(message["payload"]);
            }
            default:
                return null;
        }
    }

    // ---------------------------------------------------------------- field helpers

    /// <summary>
    /// Whether the key is present with an explicit JSON null. <see cref="JsonNode.Parse"/> maps both
    /// an absent key and an explicit null to a C# null, so typebox's Optional (absent is fine, null
    /// is not) needs ContainsKey to tell them apart.
    /// </summary>
    private static bool IsExplicitNull(JsonObject message, string name)
        => message.ContainsKey(name) && message[name] is null;

    private static (string? Token, string? SessionKey) TokenAndKey(JsonObject message)
        => (StringField(message, "token", minLength: 0), StringField(message, "sessionKey", minLength: 0));

    /// <summary>The <c>type</c> literal, or null when absent or not that literal.</summary>
    private static string? LiteralType(JsonObject message)
        => message["type"] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static bool HasLiteralType(JsonObject message, string expected)
        => LiteralType(message) == expected;

    /// <summary>TS <c>StrictObject</c>: no keys beyond the schema's.</summary>
    private static bool IsStrict(JsonObject message, params string[] allowed)
    {
        foreach (var (key, _) in message)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal)) return false;
        }

        return true;
    }

    private static string? StringField(JsonObject message, string name, int minLength)
    {
        if (message[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.String) return null;
        var text = value.GetValue<string>();
        return text.Length >= minLength ? text : null;
    }

    private static bool? BoolField(JsonObject message, string name)
        => message[name] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? value.GetValue<bool>()
            : null;

    private static double? NumberField(JsonObject message, string name)
    {
        if (message[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return null;
        // In-process JSON (the event encoders' output) backs numbers with CLR ints/longs, and
        // GetValue<double>() rejects an integral backing; read that first, as ToolTask does.
        if (value.TryGetValue<long>(out var integer)) return integer;
        if (value.TryGetValue<int>(out var small)) return small;
        return value.GetValue<double>();
    }

    /// <summary>TS <c>Type.Integer</c>: integral and at or above the minimum.</summary>
    private static long? IntegerField(JsonObject message, string name, long minimum)
    {
        var number = NumberField(message, name);
        if (number is null) return null;
        var value = number.Value;
        if (value != Math.Floor(value) || value < minimum) return null;
        return (long)value;
    }

    private static IReadOnlyList<string>? StringArrayField(JsonObject message, string name, int minLength)
    {
        if (message[name] is not JsonArray array) return null;
        var items = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || value.GetValueKind() != JsonValueKind.String) return null;
            var text = value.GetValue<string>();
            if (text.Length < minLength) return null;
            items.Add(text);
        }

        return items;
    }
}

/// <summary>
/// Worker-local reconciliation of server-generation demand and Harness activity.
/// Port of TS <c>WorkerLifecycle</c>.
/// </summary>
public sealed class WorkerLifecycle
{
    private readonly int _initialDemandGraceMs;
    private readonly int _orphanDemandGraceMs;
    private readonly Action _onRetire;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DemandEntry> _demands = [];
    private CancellationTokenSource? _initialTimer;
    private bool _harnessActive;
    private string? _currentServerConnectionId;
    private bool _demandInitialized;
    private int _retirementHolds;
    private bool _retiring;

    public WorkerLifecycle(
        string? initialServerConnectionId,
        int initialDemandGraceMs,
        int orphanDemandGraceMs,
        Action onRetire)
    {
        _currentServerConnectionId = initialServerConnectionId;
        _initialDemandGraceMs = initialDemandGraceMs;
        _orphanDemandGraceMs = orphanDemandGraceMs;
        _onRetire = onRetire;
        ArmInitialTimer();
    }

    private sealed class DemandEntry(string serverConnectionId, string attachmentId)
    {
        public string ServerConnectionId { get; } = serverConnectionId;

        public string AttachmentId { get; } = attachmentId;

        public CancellationTokenSource? OrphanTimer { get; set; }
    }

    public void ServerConnected(string serverConnectionId)
    {
        lock (_gate)
        {
            _currentServerConnectionId = serverConnectionId;
            foreach (var demand in _demands.Values)
            {
                if (demand.ServerConnectionId != serverConnectionId || demand.OrphanTimer is null) continue;
                CancelTimer(demand.OrphanTimer);
                demand.OrphanTimer = null;
            }
        }
    }

    public void ServerDisconnected(string serverConnectionId)
    {
        lock (_gate)
        {
            if (_currentServerConnectionId == serverConnectionId)
            {
                _currentServerConnectionId = null;
            }

            foreach (var (key, demand) in _demands)
            {
                if (demand.ServerConnectionId != serverConnectionId || demand.OrphanTimer is not null) continue;
                ArmOrphanTimer(key, demand);
            }
        }
    }

    /// <summary>TS <c>beginRequest</c>: the hold released when the request ends.</summary>
    public Action BeginRequest(string serverConnectionId, string attachmentId)
    {
        lock (_gate)
        {
            if (_retiring)
            {
                throw new InvalidOperationException("Session worker is retiring");
            }

            if (serverConnectionId != _currentServerConnectionId)
            {
                throw new InvalidOperationException("Session worker received a request from a stale server generation");
            }

            var demand = _demands.GetValueOrDefault(DemandKey(serverConnectionId, attachmentId));
            if (demand is null || demand.OrphanTimer is not null)
            {
                throw new InvalidOperationException("Session worker request does not match the active attachment");
            }

            return HoldRetirement();
        }
    }

    /// <summary>TS <c>holdRetirement</c>.</summary>
    public Action HoldRetirement()
    {
        lock (_gate)
        {
            _retirementHolds += 1;
            var released = false;
            return () =>
            {
                lock (_gate)
                {
                    if (released) return;
                    released = true;
                    _retirementHolds -= 1;
                    Reconcile();
                }
            };
        }
    }

    public void SetDemand(string serverConnectionId, string attachmentId, bool attached)
    {
        lock (_gate)
        {
            if (_retiring)
            {
                throw new InvalidOperationException("Session worker is retiring");
            }

            if (serverConnectionId != _currentServerConnectionId)
            {
                throw new InvalidOperationException("Session worker received demand from a stale server generation");
            }

            _demandInitialized = true;
            CancelTimer(_initialTimer);
            _initialTimer = null;
            var key = DemandKey(serverConnectionId, attachmentId);
            if (_demands.TryGetValue(key, out var previous))
            {
                CancelTimer(previous.OrphanTimer);
                previous.OrphanTimer = null;
            }

            if (attached)
            {
                _demands[key] = new DemandEntry(serverConnectionId, attachmentId);
            }
            else
            {
                _demands.Remove(key);
            }

            Reconcile();
        }
    }

    /// <summary>Live Harness tasks, such as runs and compactions, hold the worker until they end.</summary>
    public void SetHarnessActive(bool active)
    {
        lock (_gate)
        {
            _harnessActive = active;
            if (!active)
            {
                Reconcile();
            }
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            CancelTimer(_initialTimer);
            _initialTimer = null;
            foreach (var demand in _demands.Values)
            {
                CancelTimer(demand.OrphanTimer);
                demand.OrphanTimer = null;
            }

            _demands.Clear();
        }
    }

    private void ArmInitialTimer()
    {
        var cts = new CancellationTokenSource();
        _initialTimer = cts;
        Arm(cts, _initialDemandGraceMs, self =>
        {
            self._initialTimer = null;
            self._demandInitialized = true;
            self.Reconcile();
        });
    }

    private void ArmOrphanTimer(string key, DemandEntry demand)
    {
        var cts = new CancellationTokenSource();
        demand.OrphanTimer = cts;
        Arm(cts, _orphanDemandGraceMs, self =>
        {
            // TS re-checks identity: a newer demand for the same key must not be dropped.
            if (!self._demands.TryGetValue(key, out var current) || !ReferenceEquals(current, demand)) return;
            self._demands.Remove(key);
            self.Reconcile();
        });
    }

    private void Arm(CancellationTokenSource cts, int milliseconds, Action<WorkerLifecycle> fire)
    {
        _ = Task.Delay(milliseconds, cts.Token).ContinueWith(
            static (task, state) =>
            {
                var (self, action) = ((WorkerLifecycle, Action<WorkerLifecycle>))state!;
                lock (self._gate)
                {
                    // A timer whose callback was already queued still runs, exactly as in Node.
                    if (task.IsCanceled) return;
                    action(self);
                }
            },
            (this, fire),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void CancelTimer(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try
        {
            cts.Cancel();
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }
    }

    private void Reconcile()
    {
        if (_retiring || !_demandInitialized || _retirementHolds != 0 || _harnessActive || _demands.Count != 0)
        {
            return;
        }

        _retiring = true;
        // TS calls onRetire synchronously from reconcile; the gate is reentrant for callbacks that
        // come back into this instance.
        _onRetire();
    }

    private static string DemandKey(string serverConnectionId, string attachmentId)
        => SessionWorkerProtocol.DemandKey(serverConnectionId, attachmentId);
}
