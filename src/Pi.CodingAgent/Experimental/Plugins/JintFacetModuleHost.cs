using System.Text.Json;
using Jint;
using Jint.Native;
using Pi.Chord.Facets;
using Pi.Chord.Node;

namespace Pi.CodingAgent.Experimental.Plugins;

/// <summary>Executes bundled CommonJS plugin entries in a constrained Jint engine.</summary>
public sealed class JintFacetModuleHost : IFacetModuleHost
{
    private const string Bootstrap = """
        globalThis.__pi_functions = new Map();
        globalThis.__pi_localServices = new Map();
        globalThis.__pi_stateHandles = new WeakMap();
        globalThis.__pi_nextFunctionId = 0;
        globalThis.__pi_registerFunction = function (callback) {
            if (typeof callback !== "function") throw new TypeError("Expected a function");
            const id = String(++globalThis.__pi_nextFunctionId);
            globalThis.__pi_functions.set(id, callback);
            return id;
        };
        globalThis.__pi_invokeFunction = async function (id, argumentsJson, context, contextIndex) {
            const callback = globalThis.__pi_functions.get(id);
            if (callback === undefined) throw new Error(`Unknown plugin callback ${id}`);
            const args = JSON.parse(argumentsJson);
            let invocationContext;
            if (contextIndex >= 0) {
                invocationContext = context === null || context === undefined
                    ? context
                    : globalThis.__pi_contextProxy(context);
                args.splice(contextIndex, 0, invocationContext);
            }
            try {
                const result = await callback(...args);
                return result === undefined ? null : JSON.stringify(result);
            } finally {
                if (invocationContext !== undefined && invocationContext !== context) {
                    invocationContext.__pi_dispose();
                }
            }
        };
        globalThis.__pi_abortError = function () {
            const error = new Error("The operation was aborted");
            error.name = "AbortError";
            return error;
        };
        globalThis.__pi_contextProxy = function (context) {
            if (context === null || context === undefined || context.__pi_is_context_proxy === true) {
                return context;
            }
            const nativeSignal = context.abortSignal;
            const listeners = new Map();
            let abortSignal;
            if (nativeSignal !== null && nativeSignal !== undefined) {
                let onabort = null;
                let abortReason;
                abortSignal = {
                    get aborted() {
                        return nativeSignal.Aborted;
                    },
                    get reason() {
                        if (!nativeSignal.Aborted) return undefined;
                        return abortReason ??= globalThis.__pi_abortError();
                    },
                    addEventListener(type, listener) {
                        if (type !== "abort" || listener === null || listener === undefined) return;
                        if (typeof listener !== "function"
                            && (typeof listener !== "object" || typeof listener.handleEvent !== "function")) {
                            return;
                        }
                        if (listeners.has(listener)) return;
                        const callbackId = globalThis.__pi_registerFunction(event => {
                            try {
                                const abortEvent = {
                                    type: event.type,
                                    target: abortSignal,
                                    currentTarget: abortSignal,
                                    isTrusted: false
                                };
                                if (typeof listener === "function") listener.call(abortSignal, abortEvent);
                                else listener.handleEvent(abortEvent);
                            } finally {
                                listeners.delete(listener);
                                globalThis.__pi_functions.delete(callbackId);
                            }
                        });
                        const handleId = nativeSignal.AddListener(callbackId);
                        if (typeof handleId !== "string" || handleId.length === 0) {
                            globalThis.__pi_functions.delete(callbackId);
                            return;
                        }
                        listeners.set(listener, { callbackId, handleId });
                    },
                    removeEventListener(type, listener) {
                        if (type !== "abort") return;
                        const registration = listeners.get(listener);
                        if (registration === undefined) return;
                        nativeSignal.RemoveListener(registration.handleId);
                        globalThis.__pi_functions.delete(registration.callbackId);
                        listeners.delete(listener);
                    },
                    throwIfAborted() {
                        if (this.aborted) throw this.reason;
                    },
                    get onabort() {
                        return onabort;
                    },
                    set onabort(listener) {
                        if (onabort !== null) this.removeEventListener("abort", onabort);
                        onabort = typeof listener === "function" ? listener : null;
                        if (onabort !== null) this.addEventListener("abort", onabort);
                    }
                };
            }
            return {
                __pi_is_context_proxy: true,
                __pi_context_id: context.__pi_context_id,
                abortSignal,
                value(key) {
                    return context.value(key);
                },
                toString() {
                    return context.toString();
                },
                __pi_dispose() {
                    if (abortSignal === undefined) return;
                    for (const [listener, registration] of listeners) {
                        nativeSignal.RemoveListener(registration.handleId);
                        globalThis.__pi_functions.delete(registration.callbackId);
                        listeners.delete(listener);
                    }
                }
            };
        };
        globalThis.__pi_extractFacets = function (exports) {
            let value = exports;
            if (value !== null && typeof value === "object"
                && Object.prototype.hasOwnProperty.call(value, "default")) {
                value = value.default;
            }
            if (value === null || value === undefined) return [];
            return Array.isArray(value) ? value : [value];
        };
        globalThis.__pi_arrayLength = value => value.length;
        globalThis.__pi_arrayItem = (value, index) => value[index];
        globalThis.__pi_facetId = facet => facet && typeof facet.id === "string" ? facet.id : "";
        globalThis.__pi_facetHasSetup = facet => facet !== null && typeof facet === "object"
            && typeof facet.setup === "function";
        globalThis.__pi_setupFacet = function (facet, host) {
            const result = facet.setup(globalThis.__pi_createFacetEnvironment(host));
            if (result !== null && result !== undefined && typeof result.then === "function") {
                void Promise.resolve(result).catch(() => {});
                throw new Error(`Facet ${facet.id} setup must be synchronous`);
            }
        };
        globalThis.__pi_pluginApi = Object.freeze({
            AgentController: Object.freeze({ id: "pi.agent-controller", local: false }),
            PresentationUI: Object.freeze({ id: "pi.local.presentation-ui", local: true }),
            SlashCommands: Object.freeze({ id: "pi.local.slash-commands", local: true })
        });
        globalThis.__pi_contextInfo = function (context) {
            if (context === null || context === undefined) return { id: null, description: "" };
            return {
                id: typeof context.__pi_context_id === "string" ? context.__pi_context_id : null,
                description: typeof context.toString === "function" ? String(context) : ""
            };
        };
        globalThis.__pi_makeHandle = function (host, id) {
            let disposed = false;
            return {
                __pi_handle_id: id,
                dispose() {
                    if (disposed) return;
                    disposed = true;
                    host.Dispose(id);
                }
            };
        };
        globalThis.__pi_observedServiceProxy = function (host, serviceId, local, instanceId) {
            return new Proxy({}, {
                get(_target, property) {
                    if (property === "then") return undefined;
                    if (typeof property !== "string") return undefined;
                    const kind = host.ObservedMemberKind(instanceId, property);
                    if (kind === "value") {
                        return JSON.parse(host.ReadObservedMemberValue(instanceId, property));
                    }
                    return globalThis.__pi_memberProxy(host, serviceId, local, property, instanceId);
                }
            });
        };
        globalThis.__pi_invokeObserved = async function (
            host, callbackId, serviceId, local, instanceId, context) {
            const callback = globalThis.__pi_functions.get(callbackId);
            if (callback === undefined) throw new Error(`Unknown plugin callback ${callbackId}`);
            const callbackContext = globalThis.__pi_contextProxy(context);
            try {
                await callback(
                    globalThis.__pi_observedServiceProxy(host, serviceId, local, instanceId),
                    callbackContext);
            } finally {
                callbackContext.__pi_dispose();
            }
        };
        globalThis.__pi_stateProxy = function (host, id) {
            const state = {
                get value() {
                    return JSON.parse(host.ReadState(id));
                },
                change(context, mutate) {
                    if (typeof mutate !== "function") throw new TypeError("State change requires a function");
                    const draft = JSON.parse(host.ReadState(id));
                    mutate(draft);
                    const info = globalThis.__pi_contextInfo(context);
                    host.ChangeState(id, info.id, info.description, JSON.stringify(draft));
                },
                replace(context, value) {
                    const info = globalThis.__pi_contextInfo(context);
                    host.ChangeState(id, info.id, info.description, JSON.stringify(value));
                },
                subscribe(listener) {
                    if (typeof listener !== "function") throw new TypeError("State subscription requires a function");
                    const listenerId = globalThis.__pi_registerFunction(listener);
                    return globalThis.__pi_makeHandle(host, host.SubscribeState(id, listenerId));
                }
            };
            globalThis.__pi_stateHandles.set(state, id);
            return state;
        };
        globalThis.__pi_memberProxy = function (host, serviceId, local, member, instanceId = null) {
            const callable = (...args) => {
                if (instanceId !== null) {
                    const context = args.length > 0
                        && args[args.length - 1] !== null
                        && typeof args[args.length - 1] === "object"
                        && (typeof args[args.length - 1].__pi_context_id === "string"
                            || typeof args[args.length - 1].value === "function")
                        ? args.pop()
                        : undefined;
                    const functionId = host.ObservedFunctionId(instanceId, member);
                    if (functionId !== null) {
                        return globalThis.__pi_invokeFunction(
                            functionId, JSON.stringify(args), context, args.length).then(JSON.parse);
                    }
                    const info = globalThis.__pi_contextInfo(context);
                    return host.InvokeObserved(
                        instanceId, member, JSON.stringify(args), info.id, info.description).then(JSON.parse);
                }
                if (serviceId === "pi.local.presentation-ui" && member === "showStatus") {
                    const context = args[1];
                    const info = globalThis.__pi_contextInfo(context);
                    host.ShowStatus(String(args[0]), info.id, info.description);
                    return undefined;
                }
                if (serviceId === "pi.local.slash-commands") {
                    if (member === "replace" || member === "register") {
                        const command = args[0];
                        if (command === null || typeof command !== "object") {
                            throw new TypeError("Slash command contribution must be an object");
                        }
                        const runId = globalThis.__pi_registerFunction(command.run);
                        const completionId = command.getArgumentCompletions === undefined
                            ? null
                            : globalThis.__pi_registerFunction(command.getArgumentCompletions);
                        const info = {
                            name: command.name,
                            description: command.description ?? null,
                            argumentHint: command.argumentHint ?? null,
                            runId,
                            completionId
                        };
                        return globalThis.__pi_makeHandle(
                            host, host.RegisterSlashCommand(member, JSON.stringify(info)));
                    }
                    if (member === "list") return JSON.parse(host.ListSlashCommands());
                    if (member === "subscribe") {
                        const listenerId = globalThis.__pi_registerFunction(args[0]);
                        return globalThis.__pi_makeHandle(host.SubscribeSlashCommands(listenerId));
                    }
                }
                const context = args.length > 0
                    && args[args.length - 1] !== null
                    && typeof args[args.length - 1] === "object"
                    && (typeof args[args.length - 1].__pi_context_id === "string"
                        || typeof args[args.length - 1].value === "function")
                    ? args.pop()
                    : undefined;
                const info = globalThis.__pi_contextInfo(context);
                const functionId = host.LocalFunctionId(serviceId, local, member);
                if (functionId !== null) {
                    return globalThis.__pi_invokeFunction(
                        functionId, JSON.stringify(args), context, args.length).then(JSON.parse);
                }
                return host.Invoke(serviceId, local, member, JSON.stringify(args), info.id, info.description)
                    .then(JSON.parse);
            };
            return new Proxy(callable, {
                get(target, property, receiver) {
                    if (property === "value") {
                        if (instanceId !== null) {
                            return JSON.parse(host.ReadObservedMemberState(instanceId, member));
                        }
                        return JSON.parse(host.ReadMemberState(serviceId, local, member));
                    }
                    if (property === "subscribe") {
                        return listener => {
                            if (typeof listener !== "function") {
                                throw new TypeError("State subscription requires a function");
                            }
                            const listenerId = globalThis.__pi_registerFunction(listener);
                            if (instanceId !== null) {
                                return globalThis.__pi_makeHandle(
                                    host, host.SubscribeObservedMemberState(instanceId, member, listenerId));
                            }
                            return globalThis.__pi_makeHandle(
                                host, host.SubscribeMemberState(serviceId, local, member, listenerId));
                        };
                    }
                    if (property === "then") return undefined;
                    return Reflect.get(target, property, receiver);
                }
            });
        };
        globalThis.__pi_describeServiceImplementation = function (service, implementation) {
            if (service === null || typeof service.id !== "string") {
                throw new TypeError("Invalid service descriptor");
            }
            if (implementation === null || typeof implementation !== "object"
                || Array.isArray(implementation)) {
                throw new TypeError("Service implementation must be an object");
            }
            const local = service.local === true;
            const members = [];
            for (const name of Object.keys(implementation)) {
                const value = implementation[name];
                const stateId = globalThis.__pi_stateHandles.get(value);
                if (stateId !== undefined) {
                    members.push({ name, kind: "state", stateId });
                } else if (typeof value === "function") {
                    members.push({
                        name,
                        kind: "function",
                        functionId: globalThis.__pi_registerFunction(value.bind(implementation))
                    });
                } else if (local) {
                    const json = JSON.stringify(value);
                    if (json === undefined) throw new TypeError(`Local service member ${name} is not JSON`);
                    members.push({ name, kind: "value", json });
                } else {
                    throw new TypeError(`Remote service member ${service.id}.${name} is not a method or state`);
                }
            }
            return { local, members };
        };
        globalThis.__pi_createFacetEnvironment = function (host) {
            return {
                use(service) {
                    if (service === null || typeof service.id !== "string") {
                        throw new TypeError("Invalid service descriptor");
                    }
                    const local = service.local === true;
                    host.Require(service.id, local);
                    return new Proxy({}, {
                        get(_target, property) {
                            if (property === "then") return undefined;
                            if (typeof property !== "string") return undefined;
                            const implementation = globalThis.__pi_localServices.get(service.id);
                            if (implementation !== undefined) {
                                const value = implementation[property];
                                return typeof value === "function" ? value.bind(implementation) : value;
                            }
                            const kind = host.MemberKind(service.id, local, property);
                            if (kind === "value") {
                                return JSON.parse(host.ReadMemberValue(service.id, local, property));
                            }
                            return globalThis.__pi_memberProxy(host, service.id, local, property);
                        }
                    });
                },
                observe(service, handler) {
                    if (service === null || typeof service.id !== "string" || typeof handler !== "function") {
                        throw new TypeError("Invalid keyed service observation");
                    }
                    host.Observe(service.id, service.local === true, globalThis.__pi_registerFunction(handler));
                },
                provide(service, implementation) {
                    const described = globalThis.__pi_describeServiceImplementation(service, implementation);
                    host.Provide(service.id, described.local, JSON.stringify(described.members));
                    globalThis.__pi_localServices.set(service.id, implementation);
                },
                provideMany(service) {
                    if (service === null || typeof service.id !== "string") {
                        throw new TypeError("Invalid service descriptor");
                    }
                    const local = service.local === true;
                    const spawnerId = host.ProvideMany(service.id, local);
                    return {
                        spawn(key, implementation) {
                            if (typeof key !== "string" || key.length === 0) {
                                throw new TypeError("Facet service instance key must not be empty");
                            }
                            const described =
                                globalThis.__pi_describeServiceImplementation(service, implementation);
                            return globalThis.__pi_makeHandle(
                                host, host.SpawnMany(spawnerId, key, JSON.stringify(described.members)));
                        }
                    };
                },
                replicatedState(initial) {
                    if (initial === null || typeof initial !== "object") {
                        throw new TypeError("Replicated state must start with an object or array");
                    }
                    return globalThis.__pi_stateProxy(host, host.CreateState(JSON.stringify(initial)));
                },
                own(disposal) {
                    if (typeof disposal === "function") {
                        host.OwnFunction(globalThis.__pi_registerFunction(disposal));
                    } else if (disposal !== null && typeof disposal.dispose === "function") {
                        host.OwnFunction(globalThis.__pi_registerFunction(() => disposal.dispose()));
                    } else {
                        throw new TypeError("Facet ownership requires a disposal function or handle");
                    }
                },
                onActivate(callback) {
                    host.OnActivate(globalThis.__pi_registerFunction(callback));
                },
                onDeactivate(callback) {
                    host.OnDeactivate(globalThis.__pi_registerFunction(callback));
                }
            };
        };
        """;

    internal static void InitializeEngine(Engine engine) => engine.Execute(Bootstrap);

    public object? ExecuteCommonJsModule(string source, string modulePath,
        IReadOnlyList<string> externalImports, FacetBundleExternalResolver? resolver)
    {
        var imports = new HashSet<string>(externalImports, StringComparer.Ordinal);
        var engine = new Engine(options =>
        {
            options.ExperimentalFeatures = ExperimentalFeature.TaskInterop;
            options.TimeoutInterval(TimeSpan.FromSeconds(15));
            options.MaxStatements(1_000_000);
            options.LimitMemory(128 * 1024 * 1024);
        });
        InitializeEngine(engine);
        engine.SetValue("module", engine.Evaluate("({ exports: {} })"));
        engine.SetValue("exports", engine.GetValue("module").AsObject().Get("exports"));
        engine.SetValue("require", new Func<string, JsValue>(specifier =>
        {
            if (!imports.Contains(specifier) || resolver?.Invoke(specifier) is null)
            {
                throw new InvalidOperationException($"Unsupported JavaScript bundle import {specifier}");
            }
            if (specifier != PluginBundles.PluginApiSpecifier)
            {
                throw new NotSupportedException($"No JavaScript module is registered for {specifier}");
            }
            return engine.GetValue("__pi_pluginApi");
        }));
        engine.Execute(source);

        var exports = engine.GetValue("module").AsObject().Get("exports");
        var candidates = engine.Invoke("__pi_extractFacets", exports);
        var count = (int)engine.Invoke("__pi_arrayLength", candidates).AsNumber();
        var runtime = new JintPluginRuntime(engine);
        var facets = new List<object?>(count);
        for (var index = 0; index < count; index++)
        {
            facets.Add(runtime.CreateFacet(engine.Invoke("__pi_arrayItem", candidates, index)));
        }

        return new Dictionary<string, object?> { ["default"] = facets };
    }
}

internal sealed class JintPluginRuntime(Engine engine)
{
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private readonly object _registryGate = new();
    private readonly Dictionary<string, Pi.Chord.Context.Context> _contexts = [];
    private readonly Dictionary<string, JintReplicatedState> _states = [];
    private readonly Dictionary<string, IDisposable> _disposables = [];
    private readonly Dictionary<string, (IServiceSpawner<IReadOnlyDictionary<string, object>> Spawner, bool Local)>
        _spawners = [];
    private readonly Dictionary<string, object> _observedServices = [];
    private readonly Dictionary<Delegate, string> _localFunctionIds = new(ReferenceEqualityComparer.Instance);

    public IFacet CreateFacet(JsValue value)
    {
        var id = engine.Invoke("__pi_facetId", value).AsString();
        if (id.Length == 0 || !engine.Invoke("__pi_facetHasSetup", value).AsBoolean())
        {
            throw new InvalidOperationException("JavaScript facet must have a non-empty ID and setup function");
        }
        return new JintFacet(this, value, id);
    }

    public void Setup(JsValue facet, IFacetEnvironment environment)
    {
        _engineGate.Wait();
        try
        {
            engine.Invoke("__pi_setupFacet", facet, new JintFacetEnvironmentBridge(this, environment));
        }
        finally
        {
            _engineGate.Release();
        }
    }

    internal async Task<object?> InvokeFunctionAsync(string functionId, IReadOnlyList<object?> arguments,
        Pi.Chord.Context.Context? context, int contextIndex, CancellationToken cancellationToken = default,
        Action<Exception>? reportError = null)
    {
        var argumentsJson = JsonSerializer.Serialize(arguments, JintFacetEnvironmentBridge.JsonOptions);
        await _engineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ContextLease? contextLease = null;
        try
        {
            contextLease = context is null ? null : RegisterContext(context, reportError);
            var result = await engine.InvokeAsync("__pi_invokeFunction", cancellationToken,
                functionId, argumentsJson, contextLease is null ? JsValue.Undefined : contextLease.Bridge,
                contextIndex)
                .ConfigureAwait(false);
            if (result.IsNull() || result.IsUndefined()) return null;
            var json = result.AsString();
            return JintFacetEnvironmentBridge.ParseJson(json);
        }
        finally
        {
            contextLease?.Dispose();
            _engineGate.Release();
        }
    }

    internal async Task InvokeObservedCallbackAsync(JintFacetEnvironmentBridge host, string callbackId,
        string serviceId, bool local, string instanceId, Pi.Chord.Context.Context context)
    {
        await _engineGate.WaitAsync(context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
        ContextLease? contextLease = null;
        try
        {
            contextLease = RegisterContext(context, host.ReportError);
            await engine.InvokeAsync("__pi_invokeObserved", context.AbortSignal ?? CancellationToken.None,
                host, callbackId, serviceId, local, instanceId, contextLease.Bridge).ConfigureAwait(false);
        }
        finally
        {
            contextLease?.Dispose();
            _engineGate.Release();
        }
    }

    internal string CreateState(string initialJson)
    {
        var initial = JintFacetEnvironmentBridge.ParseJson(initialJson);
        JintReplicatedState state = initial switch
        {
            Dictionary<string, object?> map => CreateState(map),
            List<object?> list => CreateState(list),
            _ => throw new ArgumentException("Replicated state must start with an object or array"),
        };
        var id = Guid.NewGuid().ToString("N");
        lock (_registryGate) _states.Add(id, state);
        return id;
    }

    private static JintReplicatedState CreateState(Dictionary<string, object?> initial)
        => new JintReplicatedState<Dictionary<string, object?>>(initial);

    private static JintReplicatedState CreateState(List<object?> initial)
        => new JintReplicatedState<List<object?>>(initial);

    internal JintReplicatedState GetState(string id)
    {
        lock (_registryGate)
        {
            return _states.TryGetValue(id, out var state)
                ? state
                : throw new InvalidOperationException($"JavaScript replicated state {id} is not active");
        }
    }

    internal string AddSpawner(
        IServiceSpawner<IReadOnlyDictionary<string, object>> spawner, bool local)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_registryGate) _spawners.Add(id, (spawner, local));
        return id;
    }

    internal (IServiceSpawner<IReadOnlyDictionary<string, object>> Spawner, bool Local) GetSpawner(string id)
    {
        lock (_registryGate)
        {
            return _spawners.TryGetValue(id, out var spawner)
                ? spawner
                : throw new InvalidOperationException($"JavaScript service spawner {id} is not active");
        }
    }

    internal void RegisterFunctionDelegate(Delegate serviceMember, string functionId)
    {
        lock (_registryGate) _localFunctionIds.Add(serviceMember, functionId);
    }

    internal string? GetLocalFunctionId(object? serviceMember)
    {
        if (serviceMember is not Delegate callback) return null;
        lock (_registryGate)
        {
            return _localFunctionIds.TryGetValue(callback, out var functionId) ? functionId : null;
        }
    }

    internal JsValue ToJsValue(object? value)
        => value switch
        {
            null => JsValue.Null,
            JsValue jsValue => jsValue,
            _ => JsValue.FromObject(engine, value),
        };

    internal string AddDisposable(IDisposable disposable)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_registryGate) _disposables.Add(id, disposable);
        return id;
    }

    internal void DisposeHandle(string id)
    {
        IDisposable? disposable;
        lock (_registryGate)
        {
            if (!_disposables.Remove(id, out disposable)) return;
        }
        disposable.Dispose();
    }

    internal string AddObservedService(object service)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_registryGate) _observedServices.Add(id, service);
        return id;
    }

    internal object GetObservedService(string id)
    {
        lock (_registryGate)
        {
            return _observedServices.TryGetValue(id, out var service)
                ? service
                : throw new InvalidOperationException($"Observed JavaScript service {id} is no longer active");
        }
    }

    internal void RemoveObservedService(string id)
    {
        lock (_registryGate) _observedServices.Remove(id);
    }

    private ContextLease RegisterContext(Pi.Chord.Context.Context context, Action<Exception>? reportError)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_registryGate) _contexts.Add(id, context);
        return new ContextLease(this, id, context, reportError);
    }

    internal Pi.Chord.Context.Context ResolveContext(string? id, string description, bool required)
    {
        if (id is not null)
        {
            lock (_registryGate)
            {
                if (_contexts.TryGetValue(id, out var context)) return context;
            }
            throw new InvalidOperationException("JavaScript context has expired");
        }
        if (description == "[Context BACKGROUND_CONTEXT]") return Pi.Chord.Context.Context.Background;
        if (description == "[Context TODO_CONTEXT]") return Pi.Chord.Context.Context.Todo;
        if (!required) return Pi.Chord.Context.Context.Background;
        throw new InvalidOperationException("The JavaScript context is not backed by a Chord context");
    }

    private void RemoveContext(string id)
    {
        lock (_registryGate) _contexts.Remove(id);
    }

    private sealed class ContextLease(JintPluginRuntime runtime, string id,
        Pi.Chord.Context.Context context, Action<Exception>? reportError) : IDisposable
    {
        public JsContextBridge Bridge { get; } = new(runtime, id, context, reportError);

        public void Dispose()
        {
            Bridge.Dispose();
            runtime.RemoveContext(id);
        }
    }

    private sealed class JintFacet(JintPluginRuntime runtime, JsValue value, string id) : IFacet
    {
        public string Id { get; } = id;

        public void Setup(IFacetEnvironment environment) => runtime.Setup(value, environment);
    }
}

internal sealed class JsContextBridge(
    JintPluginRuntime runtime,
    string id,
    Pi.Chord.Context.Context context,
    Action<Exception>? reportError) : IDisposable
{
    public string __pi_context_id { get; } = id;

    private readonly JsAbortSignalBridge? _abortSignal = context.AbortSignal is { } token
        ? new JsAbortSignalBridge(runtime, token, reportError)
        : null;

    internal JsAbortSignalBridge? AbortSignalBridge => _abortSignal;

    public JsValue abortSignal
        => _abortSignal is null ? JsValue.Undefined : runtime.ToJsValue(_abortSignal);

    public JsValue value(JsValue key)
    {
        if (key.IsNull() || key.IsUndefined())
        {
            throw new ArgumentNullException(nameof(key), "Context.value requires a context key");
        }
        return key.ToObject() is Pi.Chord.Context.ContextKey contextKey
            && context.TryGetValue(contextKey, out var value)
                ? runtime.ToJsValue(value)
                : JsValue.Undefined;
    }

    public string toString() => context.ToString() ?? "";

    public void Dispose() => _abortSignal?.Dispose();
}

internal sealed class JsAbortSignalBridge : IDisposable
{
    private readonly JintPluginRuntime _runtime;
    private readonly CancellationToken _token;
    private readonly Action<Exception>? _reportError;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _listeners = [];
    private readonly CancellationTokenRegistration _registration;
    private Task? _dispatchTask;
    private bool _disposed;

    public JsAbortSignalBridge(JintPluginRuntime runtime, CancellationToken token, Action<Exception>? reportError)
    {
        _runtime = runtime;
        _token = token;
        _reportError = reportError;
        if (token.CanBeCanceled) _registration = token.Register(OnAborted);
    }

    public bool Aborted => _token.IsCancellationRequested;

    public string AddListener(string callbackId)
    {
        ArgumentException.ThrowIfNullOrEmpty(callbackId);
        lock (_gate)
        {
            if (_disposed || _dispatchTask is not null || _token.IsCancellationRequested) return "";
            var handle = Guid.NewGuid().ToString("N");
            _listeners.Add(handle, callbackId);
            return handle;
        }
    }

    public void RemoveListener(string handle)
    {
        lock (_gate) _listeners.Remove(handle);
    }

    internal Task DispatchAsync()
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            return _dispatchTask ??= Task.Run(DispatchListenersAsync);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _listeners.Clear();
        }
        _registration.Dispose();
    }

    private void OnAborted() => _ = DispatchAsync();

    private async Task DispatchListenersAsync()
    {
        string[] handles;
        lock (_gate) handles = [.. _listeners.Keys];
        foreach (var handle in handles)
        {
            string? callbackId;
            lock (_gate)
            {
                if (!_listeners.Remove(handle, out callbackId)) continue;
            }
            try
            {
                await _runtime.InvokeFunctionAsync(callbackId,
                    [new Dictionary<string, object?> { ["type"] = "abort" }],
                    null, -1, reportError: _reportError).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (_reportError is null)
                {
                    System.Diagnostics.Trace.TraceError(error.ToString());
                    continue;
                }
                try
                {
                    _reportError(error);
                }
                catch (Exception reportError)
                {
                    System.Diagnostics.Trace.TraceError(
                        $"Abort listener failed: {error}{Environment.NewLine}Error reporter failed: {reportError}");
                }
            }
        }
    }
}
