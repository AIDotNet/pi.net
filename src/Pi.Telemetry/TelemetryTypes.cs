using System.Text.Json.Serialization;

namespace Pi.Telemetry;

/// <summary>
/// Span attributes: name → value map.
/// <para>
/// Mirrors the TS <c>SpanAttributes</c> index-signature type. Attribute values
/// (<see cref="AttributeValue"/>) are represented as <see cref="object"/> with a closed
/// runtime set: <see cref="string"/>, <see cref="bool"/>, a .NET numeric type
/// (<see cref="long"/>, <see cref="double"/>, <see cref="int"/>, ...), or an
/// <see cref="IReadOnlyList{T}"/> of those scalars. A <see langword="null"/> value is the
/// equivalent of TS <c>undefined</c> and is skipped when attributes are copied or merged.
/// </para>
/// </summary>
public class SpanAttributes : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly Dictionary<string, object?> _items;

    public SpanAttributes() => _items = new Dictionary<string, object?>();

    public SpanAttributes(IEnumerable<KeyValuePair<string, object?>> items)
    {
        _items = new Dictionary<string, object?>();
        foreach (var (name, value) in items) _items[name] = value;
    }

    /// <summary>Gets or sets an attribute. <see langword="null"/> means "unset" (TS undefined).</summary>
    public object? this[string name]
    {
        get => _items.TryGetValue(name, out var value) ? value : null;
        set => _items[name] = value;
    }

    public int Count => _items.Count;
    public IEnumerable<string> Names => _items.Keys;

    /// <summary>
    /// Returns a defensive copy with <see langword="null"/> values removed.
    /// Enumerates through the public enumerator so derived classes can model unreadable payloads.
    /// </summary>
    public SpanAttributes Copy()
    {
        var copy = new SpanAttributes();
        foreach (var (name, value) in this)
        {
            if (value is null) continue;
            copy._items[name] = CopyValue(value);
        }
        return copy;
    }

    /// <summary>Creates a defensive copy of <paramref name="attributes"/>, dropping null values.</summary>
    public static SpanAttributes From(SpanAttributes? attributes) => attributes?.Copy() ?? new SpanAttributes();

    /// <summary>
    /// Merges <paramref name="attributes"/> over this instance without mutating the source.
    /// Enumerates through the public enumerator so derived classes can model unreadable payloads.
    /// </summary>
    public void Merge(SpanAttributes attributes)
    {
        foreach (var (name, value) in attributes)
        {
            if (value is null) continue;
            _items[name] = CopyValue(value);
        }
    }

    internal static object CopyValue(object value) => value switch
    {
        string s => s,
        bool b => b,
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
        IReadOnlyList<object> list => list.ToArray(),
        IEnumerable<object> enumerable => enumerable.ToArray(),
        _ => throw new InvalidOperationException($"Unsupported attribute value type: {value.GetType().Name}"),
    };

    /// <summary>Validates that a value belongs to the closed attribute-value set.</summary>
    public static bool IsValidValue(object? value) => value is null
        or string
        or bool
        or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
        or IReadOnlyList<object>;

    public virtual IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Options for starting a span. Mirrors TS <c>SpanOptions</c>.</summary>
public sealed record SpanOptions(string Name, SpanAttributes? Attributes = null);

/// <summary>Details attached to an error status. Mirrors TS <c>{ name, message }</c>.</summary>
public sealed record TelemetryErrorDetail(string Name, string Message);

/// <summary>
/// Span status. Mirrors the TS discriminated union
/// <c>{ status: "ok" } | { status: "error"; error?: { name, message } }</c> as a C#
/// record hierarchy: pattern-match with <c>is SpanStatus.Ok</c> / <c>is SpanStatus.ErrorStatus</c>.
/// </summary>
public abstract record SpanStatus
{
    private SpanStatus() { }

    public sealed record Ok : SpanStatus
    {
        public static readonly Ok Instance = new();
    }

    public sealed record ErrorStatus([property: JsonPropertyName("error")] TelemetryErrorDetail? Detail = null) : SpanStatus
    {
        public static readonly ErrorStatus Bare = new((TelemetryErrorDetail?)null);
    }

    public static SpanStatus FromError(Exception exception) => new ErrorStatus(
        new TelemetryErrorDetail(exception.GetType().Name, exception.Message));
}

/// <summary>
/// An event stored on a span, with the offset from the span start. Mirrors the element type of TS
/// <c>StoredSpan.events</c>.
/// </summary>
public sealed record StoredSpanEvent(string Name, long TimeMs, SpanAttributes Attributes);

/// <summary>An event/link style reference to another trace's span. Mirrors a TS <c>StoredSpan.links</c> element.</summary>
public sealed record StoredSpanLink(string TraceId, string SpanId, SpanAttributes? Attributes = null);

/// <summary>
/// A span as stored by an adapter: stable string ids, wall-clock bounds, and its recorded events
/// and links. Mirrors TS <c>StoredSpan</c>. Input to <see cref="TelemetryRender.RenderTrace"/>.
/// </summary>
public sealed record StoredSpan
{
    public required string Id { get; init; }

    public required string TraceId { get; init; }

    /// <summary>Parent span id; absent for roots and for spans whose parent was not stored.</summary>
    public string? ParentId { get; init; }

    public required string Name { get; init; }

    public required long StartedMs { get; init; }

    public required long EndedMs { get; init; }

    public required SpanAttributes Attributes { get; init; }

    public IReadOnlyList<StoredSpanEvent> Events { get; init; } = [];

    public IReadOnlyList<StoredSpanLink> Links { get; init; } = [];

    public required SpanStatus Status { get; init; }
}

/// <summary>One stored trace: the spans sharing a trace id. Mirrors TS <c>StoredTrace</c>.</summary>
public sealed record StoredTrace(string TraceId, IReadOnlyList<StoredSpan> Spans);
