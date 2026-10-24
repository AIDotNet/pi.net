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
    int? EndSequence);

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
    private readonly long _origin = System.Diagnostics.Stopwatch.GetTimestamp();
    private int _nextSpanId = 1;
    private int _nextEndSequence = 1;

    private long NowMs() => (long)System.Diagnostics.Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;

    /// <summary>Recorded event plus its offset from the owning span's start.</summary>
    private sealed record EventSlot(RecordedTelemetryEvent Event, long TimeMs);

    private sealed class MutableSpan
    {
        public required int Id { get; init; }
        public required int? ParentId { get; init; }
        public required string Name { get; init; }
        public required SpanAttributes Attributes { get; init; }
        public required long StartedMs { get; init; }
        public long? EndedMs { get; set; }
        public List<EventSlot> Events { get; } = new();
        public SpanStatus Status { get; set; } = SpanStatus.Ok.Instance;
        public bool ExplicitStatus { get; set; }
        public bool Settled { get; set; }
        public int? EndSequence { get; set; }
    }

    /// <inheritdoc />
    public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
        => StartInMemorySpan(options, null, callback);

    private async Task<T> StartInMemorySpan<T>(
        SpanOptions options,
        MutableSpan? parent,
        Func<ITelemetrySpan, Task<T>> callback)
    {
        // A settled parent no longer admits recorded children; fall through to the no-op context.
        if (parent is { Settled: true }) return await NoopTelemetryContext.Instance.StartSpanAsync(options, callback);

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
                recordedSpan = new MutableSpan
                {
                    Id = _nextSpanId++,
                    ParentId = parent?.Id,
                    Name = options.Name,
                    Attributes = attributes,
                    StartedMs = NowMs(),
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
                span.EndSequence)).ToList();
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
                Status = span.Status,
            }).ToList();
        }
    }

    private sealed class InMemorySpan(InMemoryTelemetryContext owner, MutableSpan recorded) : ITelemetrySpan
    {
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback)
            => owner.StartInMemorySpan(options, recorded, callback);

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
