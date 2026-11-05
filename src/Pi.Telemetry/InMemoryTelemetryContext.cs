namespace Pi.Telemetry;

/// <summary>A recorded telemetry event (detached snapshot).</summary>
public sealed record RecordedTelemetryEvent(string Name, IReadOnlyDictionary<string, object?> Attributes);

/// <summary>A recorded telemetry span (detached snapshot).</summary>
public sealed record RecordedTelemetrySpan(
    int Id,
    int? ParentId,
    string Name,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<RecordedTelemetryEvent> Events,
    SpanStatus Status,
    bool Settled,
    int? EndSequence,
    IReadOnlyList<StoredSpanLink>? Links = null);

/// <summary>One recorded metric measurement (detached snapshot).</summary>
public sealed record RecordedTelemetryMeasurement(
    string Kind,
    string Name,
    double Value,
    IReadOnlyDictionary<string, object?> Attributes);

/// <summary>
/// Backend-neutral reference implementation that records spans in process memory.
/// Mirrors TS <c>InMemoryTelemetryContext</c>, including settlement semantics,
/// automatic error status, end ordering, and passivity toward malformed payloads.
/// </summary>
public sealed class InMemoryTelemetryContext : ITelemetryContext
{
    private readonly object _lock = new();
    private readonly List<MutableSpan> _spans = new();
    private readonly string _traceId = Guid.NewGuid().ToString("N");
    private readonly string _scope = Guid.NewGuid().ToString("N");
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, TelemetryCarrier> _carriers = new();
    private ITelemetryPropagation? _propagation;
    private readonly long _origin = System.Diagnostics.Stopwatch.GetTimestamp();
    private int _nextSpanId = 1;
    private int _nextEndSequence = 1;

    private long NowMs() => (long)System.Diagnostics.Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;

    /// <summary>Recorded event plus its offset from the owning span's start.</summary>
    private sealed record EventSlot(RecordedTelemetryEvent Event, long TimeMs);

    /// <summary>
    /// 父级：本地 span 带 <see cref="Span"/>，跨边界的 carrier 只有身份。对应 TS <c>InMemoryParent</c>。
    /// </summary>
    private sealed record InMemoryParent(TelemetryCarrier Carrier, int? LocalId, MutableSpan? Span);

    private sealed class MutableSpan
    {
        public required int Id { get; init; }
        public required int? ParentId { get; init; }
        public required string Name { get; init; }
        public required SpanAttributes Attributes { get; init; }
        public required long StartedMs { get; init; }
        public required TelemetryCarrier Carrier { get; init; }
        public long? EndedMs { get; set; }
        public List<EventSlot> Events { get; } = new();
        public List<StoredSpanLink> Links { get; } = new();
        public SpanStatus Status { get; set; } = SpanStatus.Ok.Instance;
        public bool ExplicitStatus { get; set; }
        public bool Settled { get; set; }
        public int? EndSequence { get; set; }
    }

    private readonly List<RecordedTelemetryMeasurement> _measurements = [];
    private ITelemetryMetrics? _metrics;

    /// <inheritdoc />
    public ITelemetryMetrics Metrics => _metrics ??= new RecordingMetrics(this);

    /// <summary>Detached metric measurements in recording order.</summary>
    public IReadOnlyList<RecordedTelemetryMeasurement> GetMeasurements()
    {
        lock (_lock) return [.. _measurements];
    }

    private void Record(string kind, string name, double value, SpanAttributes? attributes)
    {
        var copy = new Dictionary<string, object?>();
        try
        {
            if (attributes is not null)
            {
                foreach (var (key, item) in attributes)
                {
                    if (item is null) continue;
                    copy[key] = SpanAttributes.CopyValue(item);
                }
            }
        }
        catch
        {
            // 录制是旁路行为：不可读的属性不外传。
        }

        lock (_lock) _measurements.Add(new RecordedTelemetryMeasurement(kind, name, value, copy));
    }

    /// <summary>Records every measurement in memory; instruments are created per call, as upstream does.</summary>
    private sealed class RecordingMetrics(InMemoryTelemetryContext owner) : ITelemetryMetrics
    {
        public ITelemetryCounter Counter(string name, InstrumentOptions? options = null) => new CounterInstrument(owner, name);

        public ITelemetryUpDownCounter UpDownCounter(string name, InstrumentOptions? options = null)
            => new UpDownCounterInstrument(owner, name);

        public ITelemetryHistogram Histogram(string name, HistogramOptions? options = null)
            => new HistogramInstrument(owner, name);

        private sealed class CounterInstrument(InMemoryTelemetryContext owner, string name) : ITelemetryCounter
        {
            public void Add(double value, SpanAttributes? attributes = null)
                => owner.Record("counter", name, value, attributes);
        }

        private sealed class UpDownCounterInstrument(InMemoryTelemetryContext owner, string name) : ITelemetryUpDownCounter
        {
            public void Add(double value, SpanAttributes? attributes = null)
                => owner.Record("upDownCounter", name, value, attributes);
        }

        private sealed class HistogramInstrument(InMemoryTelemetryContext owner, string name) : ITelemetryHistogram
        {
            public void Record(double value, SpanAttributes? attributes = null, ITelemetryContext? context = null)
                => owner.Record("histogram", name, value, attributes);
        }
    }

    /// <inheritdoc />
    public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
        => StartInMemorySpan(options, null, callback);

    /// <inheritdoc />
    public ITelemetryPropagation Propagation => _propagation ??= new PropagationCapability(this);

    private sealed class PropagationCapability(InMemoryTelemetryContext owner) : ITelemetryPropagation
    {
        public TelemetryCarrier? Inject(ITelemetryContext context)
            => owner._carriers.TryGetValue(context, out var carrier) ? carrier.Copy() : null;

        public ITelemetryContext Extract(TelemetryCarrier? carrier)
        {
            if (carrier is null
                || !carrier.TryGetValue("pi-memory-scope", out var scope)
                || !carrier.TryGetValue("pi-memory-span", out var span)
                || !carrier.TryGetValue("pi-memory-trace", out var trace))
            {
                return owner;
            }

            var localId = scope == owner._scope
                && int.TryParse(span, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : (int?)null;
            var extracted = new ExtractedContext(owner, new TelemetryCarrier(carrier), localId);
            owner._carriers.AddOrUpdate(extracted, new TelemetryCarrier(carrier));
            return extracted;
        }
    }

    /// <summary>由 carrier 解出的父上下文：起的子 span 带载体的身份（同 scope 才有本地父 id）。</summary>
    private sealed class ExtractedContext(
        InMemoryTelemetryContext owner, TelemetryCarrier carrier, int? localId) : ITelemetryContext
    {
        public ITelemetryMetrics Metrics => owner.Metrics;

        public ITelemetryPropagation Propagation => owner.Propagation;

        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
            => owner.StartInMemorySpan(options, new InMemoryParent(carrier, localId, null), callback);
    }

    private async Task<T> StartInMemorySpan<T>(
        SpanOptions options,
        InMemoryParent? parent,
        Func<ITelemetrySpan, Task<T>> callback)
    {
        // A settled parent no longer admits recorded children; fall through to the no-op context.
        if (parent?.Span is { Settled: true })
        {
            return await NoopTelemetryContext.Instance.StartSpanAsync(options, callback);
        }

        MutableSpan recordedSpan;
        var attributes = new SpanAttributes();
        try
        {
            if (options.Attributes is not null)
            {
                foreach (var (name, value) in options.Attributes)
                {
                    if (value is null) continue;
                    attributes[name] = SpanAttributes.CopyValue(value);
                }
            }

            lock (_lock)
            {
                var id = _nextSpanId++;
                var trace = parent is not null && parent.Carrier.TryGetValue("pi-memory-trace", out var inherited)
                    ? inherited
                    : $"{_scope}:{id}";
                recordedSpan = new MutableSpan
                {
                    Id = id,
                    ParentId = parent?.LocalId,
                    Name = options.Name,
                    Attributes = attributes,
                    StartedMs = NowMs(),
                    Carrier = new TelemetryCarrier
                    {
                        ["pi-memory-scope"] = _scope,
                        ["pi-memory-span"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["pi-memory-trace"] = trace,
                    },
                };
                _spans.Add(recordedSpan);
            }
        }
        catch
        {
            // Creating the recording slot is passive; unreadable options fall through to no-op.
            return await NoopTelemetryContext.Instance.StartSpanAsync(options, callback);
        }

        var span = new InMemorySpan(this, recordedSpan);
        _carriers.AddOrUpdate(span, recordedSpan.Carrier);

        Task<T> task;
        try
        {
            task = callback(span);
        }
        catch (Exception error)
        {
            Settle(recordedSpan, failed: true, error);
            throw;
        }

        return await SettleAfterCallback(task, recordedSpan);

        async Task<T> SettleAfterCallback(Task<T> pending, MutableSpan span)
        {
            try
            {
                var result = await pending.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                Settle(span, failed: false, null);
                return result;
            }
            catch (Exception error)
            {
                Settle(span, failed: true, error);
                throw;
            }
        }
    }

    private void Settle(MutableSpan span, bool failed, Exception? error)
    {
        lock (_lock)
        {
            if (span.Settled) return;
            if (failed && !span.ExplicitStatus)
                span.Status = error is not null ? SpanStatus.FromError(error) : SpanStatus.ErrorStatus.Bare;
            span.Settled = true;
            span.EndedMs = NowMs();
            span.EndSequence = _nextEndSequence++;
        }
    }

    /// <summary>Returns detached snapshots in span-start order.</summary>
    public IReadOnlyList<RecordedTelemetrySpan> GetSpans()
    {
        lock (_lock)
        {
            return _spans.Select(span => new RecordedTelemetrySpan(
                span.Id,
                span.ParentId,
                span.Name,
                span.Attributes.Copy().ToDictionary(kv => kv.Key, kv => kv.Value),
                span.Events.Select(slot => slot.Event with { }).ToList(),
                span.Status,
                span.Settled,
                span.EndSequence,
                span.Links.ToList())).ToList();
        }
    }

    /// <summary>
    /// Snapshots in the adapter-neutral <see cref="StoredSpan"/> shape, ready for
    /// <see cref="TelemetryRender.RenderTrace"/>. Ids are the recorded span numbers as strings; all
    /// spans share this context's trace id; links are always empty (this context has no link API).
    /// </summary>
    public IReadOnlyList<StoredSpan> GetStoredSpans()
    {
        lock (_lock)
        {
            return _spans.Select(span => new StoredSpan
            {
                Id = span.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TraceId = _traceId,
                ParentId = span.ParentId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name = span.Name,
                StartedMs = span.StartedMs,
                // An unsettled span has no end yet; report it as instantaneous rather than guessing.
                EndedMs = span.EndedMs ?? span.StartedMs,
                Attributes = span.Attributes.Copy(),
                Events = span.Events
                    .Select(slot => new StoredSpanEvent(
                        slot.Event.Name, slot.TimeMs, new SpanAttributes(slot.Event.Attributes)))
                    .ToList(),
                Links = span.Links.ToList(),
                Status = span.Status,
            }).ToList();
        }
    }

    private sealed class InMemorySpan(InMemoryTelemetryContext owner, MutableSpan recorded) : ITelemetrySpan
    {
        public ITelemetryMetrics Metrics => owner.Metrics;

        public ITelemetryPropagation Propagation => owner.Propagation;

        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
            => owner.StartInMemorySpan(
                options, new InMemoryParent(recorded.Carrier, recorded.Id, recorded), callback);

        public void AddLink(StoredSpanLink link)
        {
            if (recorded.Settled) return;
            try
            {
                recorded.Links.Add(link);
            }
            catch
            {
                // 记录 link 是旁路行为：不可读的载荷不外传。
            }
        }

        public void AddEvent(string name, SpanAttributes? attributes = null)
        {
            if (recorded.Settled) return;
            try
            {
                var copy = new Dictionary<string, object?>();
                if (attributes is not null)
                {
                    foreach (var (key, value) in attributes)
                    {
                        if (value is null) continue;
                        copy[key] = SpanAttributes.CopyValue(value);
                    }
                }
                lock (owner._lock) recorded.Events.Add(new EventSlot(
                    new RecordedTelemetryEvent(name, copy), owner.NowMs() - recorded.StartedMs));
            }
            catch
            {
                // Recording is passive. Ignore malformed or unreadable telemetry payloads.
            }
        }

        public void SetAttributes(SpanAttributes attributes)
        {
            if (recorded.Settled) return;
            try
            {
                // Copy first, then swap: a failure anywhere in the payload must leave
                // existing attributes untouched (atomicity, mirrors TS copy-then-assign).
                var copy = attributes.Copy();
                lock (owner._lock) recorded.Attributes.Merge(copy);
            }
            catch
            {
                // Recording is passive. Ignore malformed or unreadable telemetry payloads.
            }
        }

        public void SetStatus(SpanStatus status)
        {
            if (recorded.Settled) return;
            try
            {
                lock (owner._lock)
                {
                    recorded.Status = status switch
                    {
                        SpanStatus.Ok => SpanStatus.Ok.Instance,
                        SpanStatus.ErrorStatus error => new SpanStatus.ErrorStatus(error.Detail is null
                            ? null
                            : new TelemetryErrorDetail(error.Detail.Name, error.Detail.Message)),
                        _ => SpanStatus.ErrorStatus.Bare,
                    };
                    recorded.ExplicitStatus = true;
                }
            }
            catch
            {
                // Recording is passive. Ignore malformed or unreadable telemetry payloads.
            }
        }
    }
}
