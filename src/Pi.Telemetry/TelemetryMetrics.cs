namespace Pi.Telemetry;

/// <summary>Options shared by every metric instrument. Mirrors TS <c>InstrumentOptions</c>.</summary>
public record InstrumentOptions
{
    public string? Description { get; init; }

    public string? Unit { get; init; }
}

/// <summary>Options for a histogram. Mirrors TS <c>HistogramOptions</c>.</summary>
public sealed record HistogramOptions : InstrumentOptions
{
    /// <summary>Bucket hints; callers supply finite, strictly increasing values.</summary>
    public IReadOnlyList<double>? Boundaries { get; init; }
}

/// <summary>A monotonic counter. Mirrors TS <c>TelemetryCounter</c>.</summary>
public interface ITelemetryCounter
{
    void Add(double value, SpanAttributes? attributes = null);
}

/// <summary>A counter that may go both ways. Mirrors TS <c>TelemetryUpDownCounter</c>.</summary>
public interface ITelemetryUpDownCounter
{
    void Add(double value, SpanAttributes? attributes = null);
}

/// <summary>A recorded distribution. Mirrors TS <c>TelemetryHistogram</c>.</summary>
public interface ITelemetryHistogram
{
    void Record(double value, SpanAttributes? attributes = null, ITelemetryContext? context = null);
}

/// <summary>
/// Optional adapter capability with synchronous factories and recording methods. Mirrors TS
/// <c>TelemetryMetrics</c>: typed inputs are trusted, callers supply finite measurements and
/// nonnegative counter increments, and malformed input behavior is unspecified.
/// </summary>
public interface ITelemetryMetrics
{
    ITelemetryCounter Counter(string name, InstrumentOptions? options = null);

    ITelemetryUpDownCounter UpDownCounter(string name, InstrumentOptions? options = null);

    ITelemetryHistogram Histogram(string name, HistogramOptions? options = null);
}

/// <summary>
/// Metrics bound to one or more schemas: instrument names, kinds and metadata come from the schema
/// rather than the call site. Mirrors TS <c>TypedTelemetryMetrics</c>.
/// </summary>
/// <remarks>
/// The TS type constrains names and measurement attributes at compile time through mapped types;
/// C# has no equivalent, so names stay plain strings and the schema is looked up at runtime.
/// </remarks>
public sealed class TypedTelemetryMetrics
{
    private readonly ITelemetryMetrics _metrics;
    private readonly IReadOnlyDictionary<string, TelemetryMetricDefinition> _definitions;

    internal TypedTelemetryMetrics(
        ITelemetryMetrics metrics,
        IReadOnlyDictionary<string, TelemetryMetricDefinition> definitions)
    {
        _metrics = metrics;
        _definitions = definitions;
    }

    public ITelemetryCounter Counter(string name) => _metrics.Counter(name, OptionsOf(name));

    public ITelemetryUpDownCounter UpDownCounter(string name) => _metrics.UpDownCounter(name, OptionsOf(name));

    public ITelemetryHistogram Histogram(string name)
        => _metrics.Histogram(name, _definitions.TryGetValue(name, out var definition)
            ? new HistogramOptions
            {
                Description = definition.Description,
                Unit = definition.Unit,
                Boundaries = definition.Boundaries,
            }
            : null);

    private InstrumentOptions? OptionsOf(string name)
        => _definitions.TryGetValue(name, out var definition)
            ? new InstrumentOptions { Description = definition.Description, Unit = definition.Unit }
            : null;
}

/// <summary>Identity helper for serializable telemetry metrics bindings. Mirrors TS <c>createTypedMetrics</c>.</summary>
public static class TelemetryMetrics
{
    /// <summary>
    /// Binds instrument names to the schema definitions of one or more schemas. A later schema wins when
    /// two schemas define the same name (TS builds the same map by overwriting).
    /// </summary>
    public static TypedTelemetryMetrics CreateTypedMetrics(
        ITelemetryMetrics metrics, params TelemetrySchemaDefinition[] schemas)
    {
        var definitions = new Dictionary<string, TelemetryMetricDefinition>(StringComparer.Ordinal);
        foreach (var schema in schemas)
        {
            if (schema.Metrics is not { } metricsOfSchema) continue;
            foreach (var (name, definition) in metricsOfSchema) definitions[name] = definition;
        }
        return new TypedTelemetryMetrics(metrics, definitions);
    }
}
