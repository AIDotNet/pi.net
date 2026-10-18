// ============================================================================
// Service wire members — the C# side of chord's remote service classification (4g-3b)
// ============================================================================
//
// TS hands `env.provide(Service, plainObject)` and chord classifies the object's own properties into
// wire members automatically (`classifyRemoteServiceImplementation`): functions become method members,
// replicated states become state members, and the property names are the wire member names.
//
// The C# chord port publishes only `IReadOnlyDictionary<string, object>` implementations, so a typed
// service carries its wire members explicitly: the implementation class derives from <see cref="ServiceWireAdapter"/>
// (the member dictionary) and implements its service interface (the typed local view). One object,
// both roles (C325).

using Pi.Ai.Types;
using Pi.Chord.Services;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>
/// Base class for a service implementation that is simultaneously the typed local view (the service
/// interface) and the chord wire member dictionary. Member values must be
/// <see cref="Func{T, TResult}"/> (a method, answering with a value or a task) or
/// <see cref="IReplicatedStateInternals"/> (a state member) — exactly what the provider's classifier
/// accepts.
/// </summary>
internal abstract class ServiceWireAdapter : IReadOnlyDictionary<string, object>
{
    private Dictionary<string, object>? _members;

    /// <summary>
    /// The wire members, named as TS names them (the property names of the TS service object). Built
    /// once, on first access — the facet kernel only classifies implementations after every facet has
    /// been constructed, so an override may reference the instance freely.
    /// </summary>
    protected virtual IEnumerable<KeyValuePair<string, object>> WireMembers() => [];

    private Dictionary<string, object> Members =>
        _members ??= new Dictionary<string, object>(WireMembers(), StringComparer.Ordinal);

    public object this[string key] => Members[key];

    public IEnumerable<string> Keys => Members.Keys;

    public IEnumerable<object> Values => Members.Values;

    public int Count => Members.Count;

    public bool ContainsKey(string key) => Members.ContainsKey(key);

    public bool TryGetValue(string key, out object value)
    {
        var found = Members.TryGetValue(key, out var member);
        value = found ? member! : null!;
        return found;
    }

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => Members.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Members.GetEnumerator();

    // ---------------------------------------------------------------- member helpers

    /// <summary>
    /// A method member. TS invokes the member as <c>method(...args, context)</c>; the provider appends
    /// the delivery context as the last argument.
    /// </summary>
    protected static KeyValuePair<string, object> Method(string name, Func<object?[], object?> invoke)
        => new(name, invoke);

    /// <summary>A state member over a mutable replicated state.</summary>
    protected static KeyValuePair<string, object> State<T>(string name, MutableReplicatedState<T> state)
        where T : class
        => new(name, MutableReplicatedState<T>.GetInternals(state)
            ?? throw new InvalidOperationException($"Replicated state {name} exposes no internals"));

    /// <summary>The delivery context the provider appends to every call.</summary>
    protected static Pi.Chord.Context.Context ContextOf(object?[] args) => ServiceWireValues.ContextOf(args);
}

/// <summary>
/// Field-level readers/writers for the wire shapes of the experimental service DTOs. The key order and
/// the always-present keys mirror TS's <c>JSON.stringify</c> output for the same values.
/// </summary>
internal static class ServiceWireValues
{
    // ---------------------------------------------------------------- readers

    /// <summary>TS <c>ModelRef</c>.</summary>
    public static ModelRef ModelRef(IReadOnlyDictionary<string, object?> fields)
        => new(Field(fields, "provider"), Field(fields, "modelId"));

    /// <summary>TS <c>AgentPromptRequest</c>.</summary>
    public static AgentPromptRequest PromptRequest(IReadOnlyDictionary<string, object?> fields)
    {
        var images = OptionalList(fields, "images");
        return new AgentPromptRequest(
            Field(fields, "message"),
            images is null ? null :
            [
                .. images.Select(image => Image(Fields(image))),
            ]);
    }

    /// <summary>TS <c>AgentPromptImage</c>.</summary>
    public static AgentPromptImage Image(IReadOnlyDictionary<string, object?> fields)
        => new(Field(fields, "data"), Field(fields, "mimeType"));

    /// <summary>TS <c>AgentCompactionRequest</c>.</summary>
    public static AgentCompactionRequest CompactionRequest(IReadOnlyDictionary<string, object?> fields)
        => new(OptionalText(fields, "customInstructions"));

    /// <summary>TS the inline request of <c>prepareSession</c>.</summary>
    public static PrepareSessionRequest PrepareSessionRequest(IReadOnlyDictionary<string, object?> fields)
        => new(Field(fields, "sessionId"), OptionalTextList(fields, "packagePaths"));

    /// <summary>TS <c>SessionCreateOptions</c>: <c>id</c> is optional, so a missing key is null.</summary>
    public static SessionCreateOptions SessionCreateOptions(IReadOnlyDictionary<string, object?> fields)
        => new(OptionalText(fields, "id"));

    // ---------------------------------------------------------------- writers

    /// <summary>TS <c>AgentOperationResponse</c>: both variants carry all three keys.</summary>
    public static Dictionary<string, object?> OperationResponse(
        bool accepted, string? operationId, AgentOperationError? error)
        => new()
        {
            ["accepted"] = accepted,
            ["operationId"] = operationId,
            ["error"] = error is null ? null : Error(error),
        };

    /// <summary>TS <c>AgentQueueResponse</c>: both variants carry all three keys.</summary>
    public static Dictionary<string, object?> QueueResponse(
        bool accepted, string? entryId, AgentOperationError? error)
        => new()
        {
            ["accepted"] = accepted,
            ["entryId"] = entryId,
            ["error"] = error is null ? null : Error(error),
        };

    /// <summary>TS <c>AgentOperationError</c>.</summary>
    public static Dictionary<string, object?> Error(AgentOperationError error) => new()
    {
        ["code"] = error.Code,
        ["message"] = error.Message,
    };

    /// <summary>TS <c>AgentPromptResult</c>: both variants carry all three keys.</summary>
    public static Dictionary<string, object?> PromptResult(string status, string? text, string? reason)
        => new()
        {
            ["status"] = status,
            ["text"] = text,
            ["reason"] = reason,
        };

    /// <summary>The thinking levels as their TS literals.</summary>
    public static List<object?> ThinkingLevels(IReadOnlyList<ThinkingLevel> levels)
        => [.. levels.Select(level => (object?)ThinkingLevelText.ToText(level))];

    /// <summary>TS <c>AgentCancelOutcome</c>.</summary>
    public static string CancelOutcome(AgentCancelOutcome outcome) => outcome switch
    {
        AgentCancelOutcome.Cancelled => "cancelled",
        AgentCancelOutcome.AlreadyConsumed => "already_consumed",
        _ => "not_found",
    };

    /// <summary>
    /// TS <c>SessionSummary</c>: the address fields first, then <c>createdAt</c> (the order the
    /// interface declares them in).
    /// </summary>
    public static Dictionary<string, object?> SessionSummary(SessionSummary summary) => new()
    {
        ["serverId"] = summary.ServerId,
        ["sessionId"] = summary.SessionId,
        ["createdAt"] = summary.CreatedAt,
    };

    /// <summary>TS the inline answer of <c>prepareSession</c>.</summary>
    public static Dictionary<string, object?> PrepareSessionAnswer(
        IReadOnlyList<string> packagePaths, object? presentationPlugins) => new()
    {
        ["packagePaths"] = packagePaths.ToList<object?>(),
        ["presentationPlugins"] = presentationPlugins,
    };

    // ---------------------------------------------------------------- shared readers

    /// <summary>A required wire string.</summary>
    public static string Text(object? value) => value as string
        ?? throw new ArgumentException($"Expected a string argument, got {Describe(value)}");

    /// <summary>An optional wire string (TS <c>string | null</c>).</summary>
    public static string? OptionalText(object? value) => value switch
    {
        null => null,
        string text => text,
        _ => throw new ArgumentException($"Expected a string or null argument, got {Describe(value)}"),
    };

    /// <summary>A wire object read as <typeparamref name="T"/> (TS destructures the argument).</summary>
    public static T Read<T>(object? value) where T : class
    {
        if (value is not IReadOnlyDictionary<string, object?> fields)
        {
            throw new ArgumentException($"Expected an object argument, got {Describe(value)}");
        }

        return FromFields<T>(fields);
    }

    /// <summary>The delivery context the provider appends to every call.</summary>
    public static Pi.Chord.Context.Context ContextOf(object?[] args) => (Pi.Chord.Context.Context)args[^1]!;

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => $"the string \"{text}\"",
        bool flag => $"the boolean {flag}",
        _ => value.GetType().Name,
    };

    // ---------------------------------------------------------------- plumbing

    /// <summary>Dispatch a wire object to the typed reader for <typeparamref name="T"/>.</summary>
    /// <remarks>
    /// The service contracts are small and closed, so the mapping is an explicit switch rather than
    /// reflection: a new request shape must be added here deliberately.
    /// </remarks>
    public static T FromFields<T>(IReadOnlyDictionary<string, object?> fields) where T : class
    {
        object? typed = typeof(T).Name switch
        {
            nameof(ModelRef) => ModelRef(fields),
            nameof(AgentPromptRequest) => PromptRequest(fields),
            nameof(AgentCompactionRequest) => CompactionRequest(fields),
            nameof(PrepareSessionRequest) => PrepareSessionRequest(fields),
            nameof(SessionCreateOptions) => SessionCreateOptions(fields),
            _ => throw new ArgumentException($"No wire reader for {typeof(T).Name}"),
        };
        return (T)typed!;
    }

    private static string Field(IReadOnlyDictionary<string, object?> fields, string key)
        => fields.TryGetValue(key, out var value) && value is string text
            ? text
            : throw new ArgumentException($"Missing string field \"{key}\"");

    private static string? OptionalText(IReadOnlyDictionary<string, object?> fields, string key)
        => fields.TryGetValue(key, out var value) && value is string text ? text : null;

    private static IReadOnlyList<object?>? OptionalList(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value) || value is null) return null;
        return value as IReadOnlyList<object?>
            ?? throw new ArgumentException($"Field \"{key}\" must be an array or null");
    }

    private static IReadOnlyList<string>? OptionalTextList(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value) || value is null) return null;
        if (value is not IReadOnlyList<object?> list)
        {
            throw new ArgumentException($"Field \"{key}\" must be an array or null");
        }

        return list.Select(item => item as string
            ?? throw new ArgumentException($"Field \"{key}\" must contain only strings")).ToList();
    }

    private static IReadOnlyDictionary<string, object?> Fields(object? value)
        => value as IReadOnlyDictionary<string, object?>
            ?? throw new ArgumentException($"Expected an object, got {value?.GetType().Name ?? "null"}");
}
