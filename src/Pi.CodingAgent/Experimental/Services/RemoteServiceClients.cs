// ============================================================================
// Presentation-side typed views over remote chord facades — port of the client
// half of experimental/services/*.ts (4e-3c-1)
// ============================================================================
//
// TS hands `env.provide(Service, plainObject)` a plain object and lets chord classify its properties
// into wire members; a consumer's `use(Service)` returns a Proxy over the remote binding that
// structurally satisfies the interface. The C# chord port has no Proxy: a remote service is a member
// dictionary (`RemoteServiceFacade`), and `UseAs<T>` only works when T is that dictionary view (the
// porting-status note on the chord remote facade). The typed interfaces the experimental layer
// declares (`ISessionManagement`, `IModels`, …) therefore need explicit client-side adapters — the
// mirror image of the server-side `ServiceWireAdapter` (C325): one class per service, forwarding
// methods over `RemoteServiceMember.CallAsync` and projecting state members into the typed
// `IReplicatedState<T>`.
//
// State values cross as chord value trees (C365/C368): dictionaries with camelCase keys and integral
// payloads normalized to longs. The adapters decode those trees into the records the service
// contracts declare; the loopback transport (used by tests) delivers the same tree shape, so one
// decoder serves both.

using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Services;
using Pi.Durable.Harness;
using Pi.Durable.Types;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>Factory for the typed presentation-side views over one service's remote facade.</summary>
public static class RemoteServiceClients
{
    /// <summary>TS <c>use(SessionDirectory)</c> over a remote binding.</summary>
    public static ISessionDirectory SessionDirectory(RemoteServiceFacade facade) => new SessionDirectoryClient(facade);

    /// <summary>TS <c>use(SessionManagement)</c> over a remote binding.</summary>
    public static ISessionManagement SessionManagement(RemoteServiceFacade facade) => new SessionManagementClient(facade);

    /// <summary>TS <c>use(PresentationPlugins)</c> over a remote binding.</summary>
    public static IPresentationPlugins PresentationPlugins(RemoteServiceFacade facade)
        => new PresentationPluginsClient(facade);

    /// <summary>TS <c>use(Models)</c> over a remote binding.</summary>
    public static IModels Models(RemoteServiceFacade facade) => new ModelsClient(facade);

    /// <summary>TS <c>use(AgentController)</c> over a remote binding.</summary>
    public static IAgentController AgentController(RemoteServiceFacade facade) => new AgentControllerClient(facade);

    /// <summary>TS <c>use(Transcript)</c> over a remote binding.</summary>
    public static ITranscript Transcript(RemoteServiceFacade facade) => new TranscriptClient(facade);
}

/// <summary>
/// A typed read-only view over one remote state member. TS's <c>use(Service).state</c> is the
/// replicated state itself; here the member's value tree is projected into the record the contract
/// declares.
/// </summary>
internal sealed class RemoteStateView<T>(RemoteServiceMember member, Func<object?, T?> decode) : IReplicatedState<T>
    where T : class
{
    public T? Value => decode(member.Value);

    public IDisposable Subscribe(StateListener<T> listener)
        => member.Subscribe((value, context, delivery) => listener(decode(value)!, context, delivery));
}

/// <summary>TS <c>SessionDirectory</c>: the replicated directory state.</summary>
internal sealed class SessionDirectoryClient(RemoteServiceFacade facade) : ISessionDirectory
{
    private readonly IReplicatedState<SessionDirectoryState> _state =
        new RemoteStateView<SessionDirectoryState>(facade.Member("state"), RemoteServiceValues.DecodeDirectory);

    public IReplicatedState<SessionDirectoryState> State => _state;
}

/// <summary>
/// TS <c>SessionManagement</c>: create / remove / attach / detach. The wire carries the summary for a
/// create and nothing for the rest.
/// </summary>
internal sealed class SessionManagementClient(RemoteServiceFacade facade) : ISessionManagement
{
    private readonly RemoteServiceFacade _facade = facade;

    public Task<SessionSummary> CreateAsync(SessionCreateOptions options, Context context)
        => CallAsync("create", [ServiceWireValues.SessionCreateOptions(options)], context,
            ServiceWireValues.ReadSummary);

    public Task RemoveAsync(string sessionId, Context context)
        => _facade.Member("remove").CallAsync([sessionId], context);

    public Task AttachAsync(string sessionId, Context context)
        => _facade.Member("attach").CallAsync([sessionId], context);

    public Task DetachAsync(Context context)
        => _facade.Member("detach").CallAsync([], context);

    private async Task<T> CallAsync<T>(
        string member, object?[] args, Context context, Func<object?, T> read)
    {
        var reply = await _facade.Member(member).CallAsync(args, context).ConfigureAwait(false);
        return read(reply);
    }
}

/// <summary>TS <c>PresentationPlugins</c>: both members answer the presentation facet data.</summary>
internal sealed class PresentationPluginsClient(RemoteServiceFacade facade) : IPresentationPlugins
{
    private readonly RemoteServiceFacade _facade = facade;

    public Task<object?> PrepareSessionAsync(PrepareSessionRequest request, Context context)
        => _facade.Member("prepareSession").CallAsync(
            [ServiceWireValues.PrepareSessionRequest(request)], context);

    public Task<object?> ReloadAsync(Context context)
        => _facade.Member("reload").CallAsync([], context);
}

/// <summary>
/// TS <c>Models</c>: the replicated catalog + selection state plus the mutations a presentation may
/// ask for. The thinking-level answers come back as their TS literals.
/// </summary>
internal sealed class ModelsClient(RemoteServiceFacade facade) : IModels
{
    private readonly RemoteServiceFacade _facade = facade;
    private readonly IReplicatedState<ModelsState> _state =
        new RemoteStateView<ModelsState>(facade.Member("state"), RemoteServiceValues.DecodeModels);

    public IReplicatedState<ModelsState> State => _state;

    public Task CycleThinkingAsync(Context context) => CallAsync("cycleThinking", [], context);

    public async Task<IReadOnlyList<ThinkingLevel>> GetThinkingLevelsAsync(Context context)
        => ServiceWireValues.ReadThinkingLevels(
            await _facade.Member("getThinkingLevels").CallAsync([], context).ConfigureAwait(false));

    public Task RefreshAsync(Context context) => CallAsync("refresh", [], context);

    public Task SelectAsync(ModelRef model, Context context)
        => CallAsync("select", [ServiceWireValues.ModelRef(model)], context);

    public Task SelectThinkingAsync(ThinkingLevel level, Context context)
        // TS sends the level as its literal.
        => CallAsync("selectThinking", [ThinkingLevelText.ToText(level)], context);

    private Task CallAsync(string member, object?[] args, Context context)
        => _facade.Member(member).CallAsync(args, context);
}

/// <summary>
/// TS <c>AgentController</c>: the presentation-safe command facade. Every reply is a small union the
/// wire vocabulary reads back into its record variants.
/// </summary>
internal sealed class AgentControllerClient(RemoteServiceFacade facade) : IAgentController
{
    private readonly RemoteServiceFacade _facade = facade;

    public Task<AgentOperationResponse> PromptAsync(AgentPromptRequest request, Context context)
        => PromptCallAsync("prompt", request, context, ServiceWireValues.ReadOperationResponse);

    public Task<AgentQueueResponse> SteerAsync(AgentPromptRequest request, Context context)
        => PromptCallAsync("steer", request, context, ServiceWireValues.ReadQueueResponse);

    public Task<AgentQueueResponse> FollowUpAsync(AgentPromptRequest request, Context context)
        => PromptCallAsync("followUp", request, context, ServiceWireValues.ReadQueueResponse);

    public async Task<AgentCancelOutcome> CancelQueuedAsync(string entryId, Context context)
        => ServiceWireValues.ReadCancelOutcome(
            await _facade.Member("cancelQueued").CallAsync([entryId], context).ConfigureAwait(false));

    public Task AbortAsync(Context context) => _facade.Member("abort").CallAsync([], context);

    public Task<AgentOperationResponse> CompactAsync(AgentCompactionRequest request, Context context)
        => CallAsync("compact", ServiceWireValues.CompactionRequest(request), context,
            ServiceWireValues.ReadOperationResponse);

    public async Task<AgentPromptResult> WaitForPromptAsync(string operationId, Context context)
        => ServiceWireValues.ReadPromptResult(
            await _facade.Member("waitForPrompt").CallAsync([operationId], context).ConfigureAwait(false));

    private Task<T> PromptCallAsync<T>(
        string member, AgentPromptRequest request, Context context, Func<object?, T> read)
        => CallAsync(member, ServiceWireValues.PromptRequest(request), context, read);

    private async Task<T> CallAsync<T>(
        string member, object? request, Context context, Func<object?, T> read)
        => read(await _facade.Member(member).CallAsync([request], context).ConfigureAwait(false));
}

/// <summary>
/// TS <c>Transcript</c>: state-only, so the adapter projects the durable view. The view's typed
/// halves (the conversation record and its entries) are decoded through <see cref="JsonTrees"/>; the
/// built-in documents stay value trees, exactly as the worker published them.
/// </summary>
internal sealed class TranscriptClient(RemoteServiceFacade facade) : ITranscript
{
    private readonly IReplicatedState<ConversationView> _state =
        new RemoteStateView<ConversationView>(facade.Member("state"), RemoteServiceValues.DecodeView);

    public IReplicatedState<ConversationView> State => _state;
}

/// <summary>The value-tree decoders the client-side adapters share.</summary>
internal static class RemoteServiceValues
{
    /// <summary>TS <c>SessionDirectoryState</c>.</summary>
    public static SessionDirectoryState? DecodeDirectory(object? value)
    {
        if (value is null) return null;
        var fields = Fields(value);
        return new SessionDirectoryState(
            Long(fields, "revision"),
            [.. ListField(fields, "sessions").Select(item => Summary(Fields(item)))]);
    }

    /// <summary>TS <c>ModelsState</c>.</summary>
    public static ModelsState? DecodeModels(object? value)
    {
        if (value is null) return null;
        var fields = Fields(value);
        var catalog = Fields(Field(fields, "catalog"));
        var configuration = Fields(Field(fields, "configuration"));
        return new ModelsState(
            new ModelsCatalogState(
                Long(catalog, "revision"),
                [.. ListField(catalog, "availableModels").Select(item => ModelSummary(Fields(item)))]),
            new ModelsConfigurationState(
                ModelRefOrNull(Opt(configuration, "model")),
                ThinkingLevelText.Parse(Text(Opt(configuration, "thinkingLevel")))
                    ?? throw new InvalidOperationException("Unknown thinking level")),
            DecodeRefresh(Opt(fields, "refresh")));
    }

    /// <summary>TS the <c>pi.transcript</c> state: a durable view whose typed halves cross as trees.</summary>
    public static ConversationView? DecodeView(object? value)
    {
        if (value is null) return null;
        var fields = Fields(value);
        var docs = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        if (Opt(fields, "docs") is IReadOnlyDictionary<string, object?> docFields)
        {
            foreach (var (kind, document) in docFields)
            {
                if (document is IReadOnlyDictionary<string, object?> tree) docs[kind] = tree;
            }
        }

        return new ConversationView
        {
            Conversation = JsonTrees.Deserialize<ConversationRecord>(Field(fields, "conversation")),
            Entries = [.. ListField(fields, "entries").Select(item => JsonTrees.Deserialize<EntryRecord>(item))],
            Docs = docs,
        };
    }

    /// <summary>TS the refresh half of <see cref="ModelsState"/>: a status union.</summary>
    private static ModelsRefreshState DecodeRefresh(object? value)
    {
        var fields = Fields(value ?? throw new InvalidOperationException("Missing refresh state"));
        return Text(Opt(fields, "status")) switch
        {
            "refreshing" => new ModelsRefreshState.Refreshing(),
            "done" => new ModelsRefreshState.Done(),
            "warning" => new ModelsRefreshState.Warning(Errors(fields)),
            _ => new ModelsRefreshState.Idle(),
        };
    }

    private static IReadOnlyDictionary<string, string> Errors(IReadOnlyDictionary<string, object?> fields)
    {
        if (Opt(fields, "errors") is not IReadOnlyDictionary<string, object?> entries)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return entries.ToDictionary(
            pair => pair.Key,
            pair => Text(pair.Value),
            StringComparer.Ordinal);
    }

    private static SessionSummary Summary(IReadOnlyDictionary<string, object?> fields)
        => new(Text(Opt(fields, "serverId")), Text(Opt(fields, "sessionId")), Number(fields, "createdAt"));

    private static ModelSummary ModelSummary(IReadOnlyDictionary<string, object?> fields)
        => new(Text(Opt(fields, "provider")), Text(Opt(fields, "modelId")),
            Text(Opt(fields, "name")), Opt(fields, "reasoning") is true);

    /// <summary>TS the configuration's <c>model: ModelRef | null</c>: the pair alone, no catalog fields.</summary>
    private static ModelRef? ModelRefOrNull(object? value)
    {
        if (value is null) return null;
        var fields = Fields(value);
        return new ModelRef(Text(Opt(fields, "provider")), Text(Opt(fields, "modelId")));
    }

    // ---------------------------------------------------------------- tree plumbing

    internal static IReadOnlyDictionary<string, object?> Fields(object? value)
        => value as IReadOnlyDictionary<string, object?>
            ?? throw new InvalidOperationException($"Expected an object, got {Describe(value)}");

    internal static object? Opt(IReadOnlyDictionary<string, object?> fields, string key)
        => fields.TryGetValue(key, out var value) ? value : null;

    internal static object? Field(IReadOnlyDictionary<string, object?> fields, string key)
        => fields.TryGetValue(key, out var value)
            ? value
            : throw new InvalidOperationException($"Missing field \"{key}\"");

    internal static string Text(object? value)
        => value as string ?? throw new InvalidOperationException($"Expected a string, got {Describe(value)}");

    internal static long Long(IReadOnlyDictionary<string, object?> fields, string key)
        => Opt(fields, key) switch
        {
            long integer => integer,
            int small => small,
            _ => throw new InvalidOperationException($"Expected an integer field \"{key}\""),
        };

    internal static double Number(IReadOnlyDictionary<string, object?> fields, string key)
        => Opt(fields, key) switch
        {
            double number => number,
            long integer => integer,
            int small => small,
            _ => throw new InvalidOperationException($"Expected a number field \"{key}\""),
        };

    internal static IReadOnlyList<object?> ListField(IReadOnlyDictionary<string, object?> fields, string key)
        => Opt(fields, key) as IReadOnlyList<object?>
            ?? throw new InvalidOperationException($"Expected an array field \"{key}\"");

    private static string Describe(object? value) => value?.GetType().Name ?? "null";
}
