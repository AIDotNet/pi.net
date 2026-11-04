// ============================================================================
// AI-layer telemetry: the schema, the request observer, and the metrics it reports.
// Port of packages/ai/src/telemetry.ts
// ============================================================================

using System.Diagnostics;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Telemetry;

namespace Pi.Ai.Api;

/// <summary>
/// 一次模型请求的遥测：`pi.ai.request`（或 `pi.ai.poll`）span、首事件/内容块事件、以及时长、token、成本四个指标。
/// 对应 TS <c>AI_TELEMETRY_SCHEMA</c> + <c>observeModelRequest()</c>。
///
/// <para>宿主把 telemetry 手柄放在请求选项的 <c>telemetry</c> 键里（上游是 <c>options.telemetry</c> 这个类型化字段；
/// 端口的选项是字典，故用同名键承载 <see cref="ITelemetryContext"/>）。没有手柄时全部退化为无操作。</para>
/// </summary>
public static class AiTelemetry
{
    private const string RequestSpanName = "pi.ai.request";
    private const string PollSpanName = "pi.ai.poll";

    private static readonly IReadOnlyDictionary<string, TelemetryRequiredAttributeDefinition> StartAttributes =
        new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
        {
            ["provider"] = Required(Scalar(TelemetryAttributeType.String, "Provider identifier.", required: true)),
            ["api"] = Required(Scalar(TelemetryAttributeType.String, "API identifier.", required: true)),
            ["model"] = Required(Scalar(TelemetryAttributeType.String, "Requested model identifier.", required: true)),
            ["reasoning"] = Optional(Scalar(
                TelemetryAttributeType.String, "Scalar requested reasoning level.")),
        };

    private static readonly IReadOnlyList<object?> StopValues =
        ["pending", "stop", "length", "toolUse", "error", "aborted", "deferred"];

    private static readonly IReadOnlyDictionary<string, TelemetryAttributeDefinition> EndAttributes =
        new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal)
        {
            ["stop"] = Scalar(TelemetryAttributeType.String, "Normalized stop reason.", values: StopValues),
            ["usage.input"] = Scalar(TelemetryAttributeType.Number, "Uncached input tokens."),
            ["usage.output"] = Scalar(TelemetryAttributeType.Number, "Output tokens."),
            ["usage.cache_read"] = Scalar(TelemetryAttributeType.Number, "Cache-read tokens."),
            ["usage.cache_write"] = Scalar(TelemetryAttributeType.Number, "Cache-write tokens."),
            ["cost.total"] = Scalar(TelemetryAttributeType.Number, "Total request cost in USD."),
            ["response.id"] = Scalar(
                TelemetryAttributeType.String,
                "Provider response identifier.",
                cardinality: TelemetryCardinality.High),
        };

    private static readonly IReadOnlyDictionary<string, TelemetryEventDefinition> Events =
        new Dictionary<string, TelemetryEventDefinition>(StringComparer.Ordinal)
        {
            ["pi.ai.first_chunk"] = new(
                "First normalized stream event delivered.",
                new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)),
            ["pi.ai.block"] = new(
                "A completed content block; content is never recorded.",
                new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["type"] = Required(Scalar(
                        TelemetryAttributeType.String,
                        "Block kind.",
                        required: true,
                        values: ["text", "thinking", "tool_call"])),
                    ["index"] = Required(Scalar(
                        TelemetryAttributeType.Number, "Content block index.", required: true)),
                    ["chars"] = Required(Scalar(
                        TelemetryAttributeType.Number, "UTF-16 character count.", required: true)),
                    ["tool"] = Optional(Scalar(
                        TelemetryAttributeType.String, "Tool name, for tool-call blocks only.")),
                }),
        };

    private static readonly TelemetryStatusPolicy Status = new(
        "ok",
        "Stop reason is error: provider_error with at most 500 error-message characters. Aborted stays ok.");

    /// <summary>Pi 自有的请求元数据、有界错误状态、事件与低基数指标。对应 TS <c>AI_TELEMETRY_SCHEMA</c>。</summary>
    public static readonly TelemetrySchemaDefinition Schema = TelemetrySchema.Define(new TelemetrySchemaDefinition(
        1,
        new Dictionary<string, TelemetrySpanDefinition>(StringComparer.Ordinal)
        {
            [RequestSpanName] = RequestSpan("One logical model request, including authentication and provider setup."),
            [PollSpanName] = RequestSpan(
                "One deferred-response poll, not the deferred request's lifetime."),
        },
        new Dictionary<string, TelemetryMetricDefinition>(StringComparer.Ordinal)
        {
            ["pi.ai.request.duration"] = new()
            {
                Kind = TelemetryMetricKind.Histogram,
                Description = "Logical request or poll duration.",
                Unit = "ms",
                Attributes = MetricAttributes(("provider", Provider()), ("model", Model()), ("stop", Stop(required: true))),
            },
            ["pi.ai.first_chunk.duration"] = new()
            {
                Kind = TelemetryMetricKind.Histogram,
                Description = "Time to the first normalized stream event.",
                Unit = "ms",
                Attributes = MetricAttributes(("provider", Provider()), ("model", Model())),
            },
            ["pi.ai.tokens"] = new()
            {
                Kind = TelemetryMetricKind.Counter,
                Description = "Reported model token usage; pending and deferred polls are excluded.",
                Unit = "token",
                Attributes = MetricAttributes(
                    ("provider", Provider()),
                    ("model", Model()),
                    ("kind", Required(Scalar(
                        TelemetryAttributeType.String,
                        "Token usage category.",
                        required: true,
                        values: ["input", "output", "cache_read", "cache_write"])))),
            },
            ["pi.ai.cost"] = new()
            {
                Kind = TelemetryMetricKind.Counter,
                Description = "Reported model cost; pending and deferred polls are excluded.",
                Unit = "USD",
                Attributes = MetricAttributes(("provider", Provider()), ("model", Model())),
            },
        }));

    private static TelemetrySpanDefinition RequestSpan(string description) => new(
        description,
        new AnyParent(),
        StartAttributes,
        EndAttributes,
        Events,
        Status);

    private static TelemetryRequiredAttributeDefinition Provider() =>
        Required(Scalar(TelemetryAttributeType.String, "Provider identifier.", required: true));

    private static TelemetryRequiredAttributeDefinition Model() =>
        Required(Scalar(TelemetryAttributeType.String, "Requested model identifier.", required: true));

    private static TelemetryRequiredAttributeDefinition Stop(bool required) =>
        Required(Scalar(TelemetryAttributeType.String, "Normalized stop reason.", required: required, values: StopValues));

    private static IReadOnlyDictionary<string, TelemetryRequiredAttributeDefinition> MetricAttributes(
        params (string Name, TelemetryRequiredAttributeDefinition Definition)[] attributes)
        => attributes.ToDictionary(pair => pair.Name, pair => pair.Definition, StringComparer.Ordinal);

    private static ScalarAttributeDefinition Scalar(
        TelemetryAttributeType type,
        string description,
        bool? required = null,
        TelemetryCardinality? cardinality = null,
        IReadOnlyList<object?>? values = null)
    {
        _ = required;
        return new ScalarAttributeDefinition(type, description, cardinality: cardinality, values: values);
    }

    private static TelemetryRequiredAttributeDefinition Required(TelemetryAttributeDefinition definition) =>
        new(definition, true);

    private static TelemetryRequiredAttributeDefinition Optional(TelemetryAttributeDefinition definition) =>
        new(definition, false);

    /// <summary>
    /// 观察一次模型请求。返回的观察者在每个事件与终态上记录 span 属性、事件与指标；
    /// 没有 telemetry 手柄时返回无操作实现。对应 TS <c>observeModelRequest()</c>。
    /// </summary>
    public static ILazyStreamObserver ObserveRequest(
        ModelSpec model, IReadOnlyDictionary<string, object?>? options, bool poll)
    {
        var context = options is not null
            && options.TryGetValue("telemetry", out var handle)
            && handle is ITelemetryContext telemetry
                ? telemetry
                : null;
        if (context is null) return NoopObserver.Instance;

        return new RequestObserver(model, context, poll);
    }

    private sealed class NoopObserver : ILazyStreamObserver
    {
        public static readonly NoopObserver Instance = new();

        public void OnEvent(AssistantMessageEvent @event) { }

        public void OnResult(AssistantMessage? message) { }
    }

    private sealed class RequestObserver : ILazyStreamObserver
    {
        private readonly ModelSpec _model;
        private readonly ITelemetryMetrics _metrics;
        private readonly bool _poll;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly TaskCompletionSource<bool> _finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<int, int> _toolChars = [];
        private ITelemetrySpan? _span;
        private bool _first = true;

        public RequestObserver(ModelSpec model, ITelemetryContext context, bool poll)
        {
            _model = model;
            _poll = poll;
            _metrics = context.Metrics ?? NoopTelemetryMetrics.Instance;
            var starter = TypedSpanStarterFactory.Create<bool>(context, Schema);
            var attributes = new SpanAttributes { ["provider"] = model.Provider, ["model"] = model.Id, ["api"] = model.Api };
            _ = starter(poll ? PollSpanName : RequestSpanName, attributes, (span, _) =>
            {
                // span 的生命周期跟着请求走：完成信号由 OnResult 触发。
                _span = span;
                return _finished.Task;
            });
        }

        public void OnEvent(AssistantMessageEvent @event)
        {
            var span = _span;
            if (span is null) return;
            if (_first)
            {
                _first = false;
                span.AddEvent("pi.ai.first_chunk");
            }

            switch (@event)
            {
                case AssistantMessageEvent.ToolCallDelta delta:
                    _toolChars[delta.ContentIndex] = _toolChars.GetValueOrDefault(delta.ContentIndex) + delta.Delta.Length;
                    break;
                case AssistantMessageEvent.TextEnd end:
                    span.AddEvent("pi.ai.block", new SpanAttributes
                    {
                        ["type"] = "text",
                        ["index"] = (long)end.ContentIndex,
                        ["chars"] = (long)end.Content.Length,
                    });
                    break;
                case AssistantMessageEvent.ThinkingEnd end:
                    span.AddEvent("pi.ai.block", new SpanAttributes
                    {
                        ["type"] = "thinking",
                        ["index"] = (long)end.ContentIndex,
                        ["chars"] = (long)end.Content.Length,
                    });
                    break;
                case AssistantMessageEvent.ToolCallEnd end:
                {
                    var chars = _toolChars.Remove(end.ContentIndex, out var recorded) ? recorded : 0;
                    span.AddEvent("pi.ai.block", new SpanAttributes
                    {
                        ["type"] = "tool_call",
                        ["index"] = (long)end.ContentIndex,
                        ["chars"] = (long)chars,
                        ["tool"] = end.ToolCall.Name,
                    });
                    break;
                }
            }
        }

        public void OnResult(AssistantMessage? message)
        {
            try
            {
                var span = _span;
                if (span is not null && message is not null)
                {
                    var usage = message.UsageStats ?? new Usage(0, 0);
                    span.SetAttributes(new SpanAttributes
                    {
                        ["stop"] = StopName(message.StopReason),
                        ["usage.input"] = (double)usage.Input,
                        ["usage.output"] = (double)usage.Output,
                        ["usage.cache_read"] = (double)usage.CacheRead,
                        ["usage.cache_write"] = (double)usage.CacheWrite,
                        ["cost.total"] = usage.Cost.Total,
                        ["response.id"] = message.ResponseId,
                    });
                    if (message.StopReason == Types.StopReason.Error)
                    {
                        span.SetStatus(new SpanStatus.ErrorStatus(new TelemetryErrorDetail(
                            "provider_error", Truncate(message.ErrorMessage ?? "", 500))));
                    }

                    var attributes = new SpanAttributes { ["provider"] = _model.Provider, ["model"] = _model.Id };
                    var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
                    var durationAttributes = attributes.Copy();
                    durationAttributes["stop"] = StopName(message.StopReason);
                    _metrics.Histogram("pi.ai.request.duration").Record(elapsed, durationAttributes, span);

                    if (!_poll || (message.StopReason != Types.StopReason.Pending
                        && message.StopReason != Types.StopReason.Deferred))
                    {
                        var tokens = _metrics.Counter("pi.ai.tokens");
                        tokens.Add(usage.Input, With(attributes, "kind", "input"));
                        tokens.Add(usage.Output, With(attributes, "kind", "output"));
                        tokens.Add(usage.CacheRead, With(attributes, "kind", "cache_read"));
                        tokens.Add(usage.CacheWrite, With(attributes, "kind", "cache_write"));
                        _metrics.Counter("pi.ai.cost").Add(usage.Cost.Total, attributes);
                    }
                }
            }
            finally
            {
                _finished.TrySetResult(true);
            }
        }

        private static SpanAttributes With(SpanAttributes attributes, string key, object? value)
        {
            var copy = attributes.Copy();
            copy[key] = value;
            return copy;
        }

        private static string StopName(StopReason reason) => reason switch
        {
            Types.StopReason.Stop => "stop",
            Types.StopReason.Length => "length",
            Types.StopReason.ToolUse => "toolUse",
            Types.StopReason.Error => "error",
            Types.StopReason.Aborted => "aborted",
            Types.StopReason.Pending => "pending",
            Types.StopReason.Deferred => "deferred",
            _ => "stop",
        };

        private static string Truncate(string text, int max) =>
            text.Length <= max ? text : text[..max];
    }
}
