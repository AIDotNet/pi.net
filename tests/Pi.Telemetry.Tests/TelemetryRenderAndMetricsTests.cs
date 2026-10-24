using Pi.Telemetry;
using Xunit;

namespace Pi.Telemetry.Tests;

/// <summary>render.ts 的 trace 渲染与 metrics.ts 的 schema 桥接。</summary>
public class TelemetryRenderAndMetricsTests
{
    private static SpanAttributes Attributes(params (string Key, object? Value)[] items)
    {
        var attributes = new SpanAttributes();
        foreach (var (key, value) in items) attributes[key] = value;
        return attributes;
    }

    private static StoredSpan Span(
        string id,
        string name,
        long startedMs,
        long endedMs,
        string? parentId = null,
        SpanAttributes? attributes = null,
        SpanStatus? status = null,
        IReadOnlyList<StoredSpanEvent>? events = null,
        IReadOnlyList<StoredSpanLink>? links = null)
        => new()
        {
            Id = id,
            TraceId = "trace-1",
            ParentId = parentId,
            Name = name,
            StartedMs = startedMs,
            EndedMs = endedMs,
            Attributes = attributes ?? new SpanAttributes(),
            Status = status ?? SpanStatus.Ok.Instance,
            Events = events ?? [],
            Links = links ?? [],
        };

    [Fact]
    public void RendersChildrenIndentedUnderTheirParent()
    {
        var spans = new List<StoredSpan>
        {
            Span("2", "llm call", 3, 8, parentId: "1", attributes: Attributes(("model", "gpt-4o"))),
            Span("1", "agent run", 0, 12, attributes: Attributes(("session", "s1"), ("turns", 2L))),
        };

        var text = TelemetryRender.RenderTrace(spans);

        Assert.Equal(
            "agent run session=\"s1\" turns=2 (12ms)\n" +
            "  llm call model=\"gpt-4o\" (5ms)",
            text);
    }

    [Fact]
    public void RendersEventsLinksAndErrorStatus()
    {
        var spans = new List<StoredSpan>
        {
            Span("1", "tool", 10, 25,
                status: new SpanStatus.ErrorStatus(new TelemetryErrorDetail("TimeoutException", "too slow")),
                events: [new StoredSpanEvent("retry", 4, Attributes(("attempt", 1L)))],
                links: [new StoredSpanLink("upstream-trace", "span-9", Attributes(("kind", "parent")))]),
        };

        var text = TelemetryRender.RenderTrace(spans);

        Assert.Equal(
            "tool (15ms) error TimeoutException: too slow\n" +
            "  · retry attempt=1 (+4ms)\n" +
            "  → link trace upstream-trace span span-9 kind=\"parent\"",
            text);
    }

    [Fact]
    public void BareErrorStatusRendersWithoutDetail()
    {
        var text = TelemetryRender.RenderTrace(
            [Span("1", "tool", 0, 1, status: SpanStatus.ErrorStatus.Bare)]);

        Assert.Equal("tool (1ms) error", text);
    }

    [Fact]
    public void SpansWhoseParentIsMissingBecomeRoots()
    {
        var text = TelemetryRender.RenderTrace(
            [Span("2", "orphan", 1, 2, parentId: "missing")]);

        Assert.Equal("orphan (1ms)", text);
    }

    [Fact]
    public void RootsFollowStartOrderAndChildrenKeepTheirOrder()
    {
        var spans = new List<StoredSpan>
        {
            Span("3", "second child", 4, 5, parentId: "1"),
            Span("2", "first child", 2, 3, parentId: "1"),
            Span("4", "other root", 6, 7),
            Span("1", "root", 0, 8),
        };

        var text = TelemetryRender.RenderTrace(spans);

        Assert.Equal(
            "root (8ms)\n" +
            "  first child (1ms)\n" +
            "  second child (1ms)\n" +
            "other root (1ms)",
            text);
    }

    [Fact]
    public void EmptyInputRendersEmptyText() => Assert.Equal(string.Empty, TelemetryRender.RenderTrace([]));

    /// <summary>重复 id 按 Map 语义处理：位置取首次出现，值取最后一次（不会渲染成两行）。</summary>
    [Fact]
    public void RepeatedSpanIdKeepsItsFirstPositionAndLatestValue()
    {
        var text = TelemetryRender.RenderTrace(
        [
            Span("1", "first", 0, 1),
            Span("1", "last", 2, 5),
        ]);

        Assert.Equal("last (3ms)", text);
    }

    [Fact]
    public async Task InMemorySpansRenderAsATrace()
    {
        var context = new InMemoryTelemetryContext();
        await context.StartSpanAsync(
            new SpanOptions("root", Attributes(("session", "s1"))),
            async root =>
            {
                await root.StartSpanAsync(new SpanOptions("child"), _ =>
                {
                    return Task.FromResult(1);
                });
                root.AddEvent("note", Attributes(("phase", "start")));
                return 1;
            });

        var stored = context.GetStoredSpans();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, span => Assert.Equal(stored[0].TraceId, span.TraceId));
        Assert.Equal("1", stored[0].Id);
        Assert.Equal("1", stored[1].ParentId);

        var text = TelemetryRender.RenderTrace(stored);
        var lines = text.Split('\n');
        // Root line, then its event, then the child (upstream order: span → events → links → children).
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("root session=\"s1\" (", lines[0]);
        Assert.StartsWith("  · note phase=\"start\" (+", lines[1]);
        Assert.StartsWith("  child (", lines[2]);
        Assert.Contains(context.GetStoredSpans()[0].Events, e => e.Name == "note");
    }

    [Fact]
    public async Task InMemoryKeepsEventOffsetsAndEndBounds()
    {
        var context = new InMemoryTelemetryContext();
        await context.StartSpanAsync(new SpanOptions("one"), span =>
        {
            span.AddEvent("tick");
            return Task.FromResult(0);
        });

        var span = Assert.Single(context.GetStoredSpans());
        Assert.True(span.EndedMs >= span.StartedMs);
        var storedEvent = Assert.Single(span.Events);
        Assert.Equal("tick", storedEvent.Name);
        Assert.True(storedEvent.TimeMs >= 0);
    }

    // ─── metrics ────────────────────────────────────────────────────────────

    private sealed class NoopCounter : ITelemetryCounter
    {
        public void Add(double value, SpanAttributes? attributes = null) { }
    }

    private sealed class NoopUpDownCounter : ITelemetryUpDownCounter
    {
        public void Add(double value, SpanAttributes? attributes = null) { }
    }

    private sealed class NoopHistogram : ITelemetryHistogram
    {
        public void Record(double value, SpanAttributes? attributes = null, ITelemetryContext? context = null) { }
    }

    private sealed class RecordingMetrics : ITelemetryMetrics
    {
        public List<(string Name, InstrumentOptions? Options)> Counters { get; } = [];
        public List<(string Name, InstrumentOptions? Options)> UpDownCounters { get; } = [];
        public List<(string Name, HistogramOptions? Options)> Histograms { get; } = [];

        public ITelemetryCounter Counter(string name, InstrumentOptions? options = null)
        {
            Counters.Add((name, options));
            return new NoopCounter();
        }

        public ITelemetryUpDownCounter UpDownCounter(string name, InstrumentOptions? options = null)
        {
            UpDownCounters.Add((name, options));
            return new NoopUpDownCounter();
        }

        public ITelemetryHistogram Histogram(string name, HistogramOptions? options = null)
        {
            Histograms.Add((name, options));
            return new NoopHistogram();
        }
    }

    private static TelemetrySchemaDefinition Schema()
        => new(1, new Dictionary<string, TelemetrySpanDefinition>(),
            new Dictionary<string, TelemetryMetricDefinition>
            {
                ["requests"] = new()
                {
                    Description = "Requests handled",
                    Unit = "1",
                    Kind = TelemetryMetricKind.Counter,
                    Attributes = new Dictionary<string, TelemetryRequiredAttributeDefinition>(),
                },
                ["latency"] = new()
                {
                    Description = "Request latency",
                    Unit = "ms",
                    Kind = TelemetryMetricKind.Histogram,
                    Boundaries = [10, 100],
                    Attributes = new Dictionary<string, TelemetryRequiredAttributeDefinition>(),
                },
            });

    [Fact]
    public void TypedMetricsTakeMetadataFromTheSchema()
    {
        var metrics = new RecordingMetrics();
        var typed = TelemetryMetrics.CreateTypedMetrics(metrics, Schema());

        typed.Counter("requests");
        typed.Histogram("latency");
        // Unschematized names still reach the adapter, with no options.
        typed.UpDownCounter("queue-depth");

        var counter = Assert.Single(metrics.Counters);
        Assert.Equal("requests", counter.Name);
        Assert.Equal("Requests handled", counter.Options!.Description);
        Assert.Equal("1", counter.Options.Unit);

        var histogram = Assert.Single(metrics.Histograms);
        Assert.Equal("Request latency", histogram.Options!.Description);
        Assert.Equal([10d, 100d], histogram.Options.Boundaries);

        var upDown = Assert.Single(metrics.UpDownCounters);
        Assert.Equal("queue-depth", upDown.Name);
        Assert.Null(upDown.Options);
    }

    [Fact]
    public void LaterSchemaWinsForDuplicateMetricNames()
    {
        var metrics = new RecordingMetrics();
        var later = new TelemetrySchemaDefinition(1, new Dictionary<string, TelemetrySpanDefinition>(),
            new Dictionary<string, TelemetryMetricDefinition>
            {
                ["requests"] = new()
                {
                    Description = "Overridden",
                    Kind = TelemetryMetricKind.Counter,
                    Attributes = new Dictionary<string, TelemetryRequiredAttributeDefinition>(),
                },
            });

        TelemetryMetrics.CreateTypedMetrics(metrics, Schema(), later).Counter("requests");

        Assert.Equal("Overridden", Assert.Single(metrics.Counters).Options!.Description);
    }

    [Fact]
    public void SchemaWithoutMetricsBindsNothing()
    {
        var metrics = new RecordingMetrics();
        var schema = new TelemetrySchemaDefinition(1, new Dictionary<string, TelemetrySpanDefinition>());

        TelemetryMetrics.CreateTypedMetrics(metrics, schema).Counter("anything");

        Assert.Null(Assert.Single(metrics.Counters).Options);
    }
}
