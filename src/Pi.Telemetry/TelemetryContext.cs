namespace Pi.Telemetry;

/// <summary>
/// A single span in an active telemetry trace. Mirrors TS <c>TelemetrySpan</c>.
/// A span is itself a <see cref="ITelemetryContext"/>, so child spans are started
/// through <see cref="StartSpanAsync{T}"/>.
/// </summary>
public interface ITelemetrySpan : ITelemetryContext
{
    /// <summary>Records an ordered, timestamped event on this span.</summary>
    void AddEvent(string name, SpanAttributes? attributes = null);

    /// <summary>Links this span to another span without parenting it. Mirrors TS <c>addLink()</c>.</summary>
    void AddLink(StoredSpanLink link);

    /// <summary>Merges attributes onto this span; last write wins per key.</summary>
    void SetAttributes(SpanAttributes attributes);

    /// <summary>Sets the terminal status. Only the first explicit status is kept.</summary>
    void SetStatus(SpanStatus status);
}

/// <summary>
/// Telemetry context. Mirrors TS <c>TelemetryContext</c>.
/// <para>
/// The TS callback may return <c>T | Promise&lt;T&gt;</c>; in C# the single async
/// signature <c>Func&lt;ITelemetrySpan, Task&lt;T&gt;&gt;</c> plays that role, and
/// synchronous callbacks wrap their value with <see cref="Task.FromResult{T}(T)"/>.
/// (A sync-value overload would be ambiguous: a lambda returning <c>Task&lt;T&gt;</c>
/// would bind with <c>T = Task&lt;T&gt;</c> because .NET tasks, unlike JS promises,
/// do not auto-flatten.)
/// </para>
/// The callback owns the span lifecycle: the span is settled (with an automatic error
/// status if none was set) when the returned task completes, successfully or not.
/// </summary>
public interface ITelemetryContext
{
    /// <summary>
    /// Optional metrics adapter of this context. Mirrors TS <c>TelemetryContext.metrics?</c>:
    /// absent for a context that records no metrics.
    /// </summary>
    ITelemetryMetrics? Metrics => null;

    /// <summary>
    /// Optional transport capability. Mirrors TS <c>TelemetryContext.propagation?</c>:
    /// absent for a context that cannot carry its identity across a boundary.
    /// </summary>
    ITelemetryPropagation? Propagation => null;

    /// <summary>Starts a span and runs <paramref name="callback"/> inside its lifetime.</summary>
    Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback);
}

/// <summary>
/// Optional transport capability implemented by the application's telemetry adapter.
/// Mirrors TS <c>TelemetryPropagation</c>.
/// </summary>
public interface ITelemetryPropagation
{
    /// <summary>Returns a detached carrier for <paramref name="context"/>, or null when it is unknown.</summary>
    TelemetryCarrier? Inject(ITelemetryContext context);

    /// <summary>Resolves a carrier back to a context; absent or unknown carriers use the root.</summary>
    ITelemetryContext Extract(TelemetryCarrier? carrier);
}

/// <summary>Shared propagation capability that neither inspects nor retains its inputs.</summary>
public static class NoopTelemetryPropagation
{
    public static readonly ITelemetryPropagation Instance = new Noop();

    private sealed class Noop : ITelemetryPropagation
    {
        public TelemetryCarrier? Inject(ITelemetryContext context) => null;

        public ITelemetryContext Extract(TelemetryCarrier? carrier) => NoopTelemetryContext.Instance;
    }
}

/// <summary>Shared no-op telemetry used when the host application provides none.</summary>
public static class NoopTelemetryContext
{
    /// <summary>Singleton context in which every span is inert and callbacks run unchanged.</summary>
    public static readonly ITelemetryContext Instance = new NoopContext();

    private sealed class NoopSpan : ITelemetrySpan
    {
        public static readonly NoopSpan Instance = new();

        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
        {
            try
            {
                return callback(Instance);
            }
            catch (Exception error)
            {
                return Task.FromException<T>(error);
            }
        }

        public ITelemetryMetrics Metrics => NoopTelemetryMetrics.Instance;

        public ITelemetryPropagation Propagation => NoopTelemetryPropagation.Instance;

        public void AddEvent(string name, SpanAttributes? attributes = null) { }
        public void AddLink(StoredSpanLink link) { }
        public void SetAttributes(SpanAttributes attributes) { }
        public void SetStatus(SpanStatus status) { }
    }

    private sealed class NoopContext : ITelemetryContext
    {
        public ITelemetryMetrics Metrics => NoopTelemetryMetrics.Instance;

        public ITelemetryPropagation Propagation => NoopTelemetryPropagation.Instance;

        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
            => NoopSpan.Instance.StartSpanAsync(options, callback);
    }
}
