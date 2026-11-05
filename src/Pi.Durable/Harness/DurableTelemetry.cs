// ============================================================================
// Durable's content-free telemetry vocabulary and the parts the port emits today.
// Port of packages/durable/src/harness/telemetry.ts
// ============================================================================

using Pi.Chord.Context;
using Pi.Telemetry;

namespace Pi.Durable.Harness;

/// <summary>
/// durable 的 span / 事件 / 指标词表：只有 id 与种类，从不含内容。对应 TS <c>DURABLE_TELEMETRY_SCHEMA</c>。
///
/// <para>本批已接的是 <c>pi.durable.reports</c> 计数器；<c>pi.durable.phase</c> span 与
/// <c>pi.durable.phase.duration</c> / <c>pi.durable.tasks.active</c> 需要调用模型先具备 <c>attempt</c> /
/// <c>resumed</c> 两项记账（端口 <c>Invocation</c> 目前没有），<c>pi.durable.submit</c> 需要提交侧携带 carrier，
/// 两者都留待后续批次。</para>
/// </summary>
public static class DurableTelemetry
{
    /// <summary>任务调用上下文里的 span 键：处理器可以从这里起子 span。对应 TS 的 <c>withTelemetry</c>。</summary>
    public static readonly ContextKey<ITelemetrySpan> SpanKey = new("pi.telemetry.span");

    private static readonly IReadOnlyList<object?> ReportKinds =
    [
        "storage_failed", "task_faulted", "task_interrupted", "migration_failed",
        "task_outdated", "task_error", "extension_error", "host_error", "listener_error",
    ];

    private static readonly IReadOnlyList<object?> ReportReasons =
        ["threw", "restart_limit", "interrupted", "missing_task", "task_too_old", "migration_failed"];

    private static readonly IReadOnlyDictionary<string, TelemetryRequiredAttributeDefinition> ReportAttributes =
        new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
        {
            ["type"] = Required(new ScalarAttributeDefinition(
                TelemetryAttributeType.String, "Report kind (`Report.kind`).", values: ReportKinds), true),
            ["reason"] = Required(new ScalarAttributeDefinition(
                TelemetryAttributeType.String, "Fixed report reason, when present.", values: ReportReasons), false),
            ["extension"] = Required(new ScalarAttributeDefinition(
                TelemetryAttributeType.String, "Registered extension name, when present."), false),
            ["task.id"] = Required(new ScalarAttributeDefinition(
                TelemetryAttributeType.Number, "Task ID, when present.",
                cardinality: TelemetryCardinality.High), false),
        };

    private static readonly TelemetryEventDefinition Entry = new(
        "A transcript entry was committed; content remains in the transcript.",
        new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
        {
            ["entry.id"] = Required(new ScalarAttributeDefinition(
                TelemetryAttributeType.Number, "Transcript entry ID.",
                cardinality: TelemetryCardinality.High), true),
            ["entry.kind"] = Required(new ScalarAttributeDefinition(
                TelemetryAttributeType.String, "Entry kind."), true),
        });

    private static readonly TelemetryEventDefinition ReportEvent = new(
        "A report was raised; no error payload is recorded.", ReportAttributes);

    private static readonly TelemetryEventDefinition SubmissionEvent = new(
        "A submission was settled.",
        new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
        {
            ["submission.id"] = Required(new ScalarAttributeDefinition(
                TelemetryAttributeType.Number, "Submission ID.",
                cardinality: TelemetryCardinality.High), true),
            ["status"] = Required(new ScalarAttributeDefinition(
                TelemetryAttributeType.String, "Settlement.", values: ["done", "unanswered"]), true),
            ["answer.entry_id"] = Required(new ScalarAttributeDefinition(
                TelemetryAttributeType.Number, "Answer entry ID.",
                cardinality: TelemetryCardinality.High), false),
        });

    /// <summary>durable 的内容无关词表。对应 TS <c>DURABLE_TELEMETRY_SCHEMA</c>。</summary>
    public static readonly TelemetrySchemaDefinition Schema = TelemetrySchema.Define(new TelemetrySchemaDefinition(
        1,
        new Dictionary<string, TelemetrySpanDefinition>(StringComparer.Ordinal)
        {
            ["pi.durable.submit"] = new(
                "Public submission admission: with its commit for submit(draft, context); the admission only for "
                + "submit(tx, draft). Inputs and continues start root traces linked to their callers; writes are "
                + "children.",
                new AnyParent(),
                new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["submission.type"] = Required(new ScalarAttributeDefinition(
                        TelemetryAttributeType.String, "Submission type.",
                        values: ["input", "write", "continue"]), true),
                    ["conversation.id"] = Required(new ScalarAttributeDefinition(
                        TelemetryAttributeType.Number, "Conversation ID.",
                        cardinality: TelemetryCardinality.High), true),
                },
                new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["submission.id"] = new ScalarAttributeDefinition(
                        TelemetryAttributeType.Number, "Admitted submission ID.",
                        cardinality: TelemetryCardinality.High),
                    ["result"] = new ScalarAttributeDefinition(
                        TelemetryAttributeType.String, "Admission result; stale writes report done.",
                        values: ["queued", "placed", "done"]),
                },
                new Dictionary<string, TelemetryEventDefinition>(StringComparer.Ordinal)
                {
                    ["pi.durable.entry"] = Entry,
                    ["pi.durable.submission"] = SubmissionEvent,
                    ["pi.durable.report"] = ReportEvent,
                },
                new TelemetryStatusPolicy("ok", "Admission failed.")),
            ["pi.durable.phase"] = new(
                "One task phase invocation, including its decision commit, never a wait or restart.",
                new AnyParent(),
                new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["task.id"] = Required(new ScalarAttributeDefinition(
                        TelemetryAttributeType.Number, "Task ID.",
                        cardinality: TelemetryCardinality.High), true),
                    ["task.name"] = Required(new ScalarAttributeDefinition(
                        TelemetryAttributeType.String, "Registered task name."), true),
                    ["phase"] = Required(new ScalarAttributeDefinition(
                        TelemetryAttributeType.String, "Invoked checkpoint phase."), true),
                    ["kind"] = Required(new ScalarAttributeDefinition(
                        TelemetryAttributeType.String, "Invocation kind.", values: ["run", "abort"]), true),
                    ["attempt"] = Required(new ScalarAttributeDefinition(
                        TelemetryAttributeType.Number, "Checkpoint attempt, when present."), false),
                    ["resumed"] = Required(new ScalarAttributeDefinition(
                        TelemetryAttributeType.Boolean,
                        "First invocation of a task loaded in this process."), true),
                },
                new Dictionary<string, TelemetryAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["outcome"] = new ScalarAttributeDefinition(
                        TelemetryAttributeType.String,
                        "next=<phase>, waiting, sleeping, done, faulted, or aborted."),
                    ["tool.name"] = new ScalarAttributeDefinition(
                        TelemetryAttributeType.String, "Called tool name."),
                    ["tool.call_id"] = new ScalarAttributeDefinition(
                        TelemetryAttributeType.String, "Transcript tool call ID.",
                        cardinality: TelemetryCardinality.High),
                },
                new Dictionary<string, TelemetryEventDefinition>(StringComparer.Ordinal)
                {
                    ["pi.durable.entry"] = Entry,
                    ["pi.durable.submission"] = SubmissionEvent,
                    ["pi.durable.report"] = ReportEvent,
                    ["pi.durable.checkpoint"] = new(
                        "The running checkpoint's phase changed while the invocation continued.",
                        new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                        {
                            ["phase"] = Required(new ScalarAttributeDefinition(
                                TelemetryAttributeType.String, "Committed checkpoint phase."), true),
                        }),
                    ["pi.durable.replay"] = new(
                        "A nested tool call was answered from its stored result.",
                        new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                        {
                            ["tool.name"] = Required(new ScalarAttributeDefinition(
                                TelemetryAttributeType.String, "Called tool name."), true),
                        }),
                },
                new TelemetryStatusPolicy(
                    "ok",
                    "Faulted, failed, and orphaned outcomes use task_faulted, task_failed, and task_orphaned with a "
                    + "diagnostic capped at 500 characters. Aborted outcomes keep status ok.")),
        },
        new Dictionary<string, TelemetryMetricDefinition>(StringComparer.Ordinal)
        {
            ["pi.durable.reports"] = new()
            {
                Kind = TelemetryMetricKind.Counter,
                Description = "Reports raised, grouped by their fixed kind.",
                Attributes = new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["type"] = ReportAttributes["type"],
                },
            },
            ["pi.durable.phase.duration"] = new()
            {
                Kind = TelemetryMetricKind.Histogram,
                Unit = "ms",
                Description = "Duration of a phase invocation and its decision commit.",
                Attributes = new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["task"] = Required(new ScalarAttributeDefinition(
                        TelemetryAttributeType.String, "Registered task name."), true),
                    ["phase"] = Required(new ScalarAttributeDefinition(
                        TelemetryAttributeType.String, "Invoked checkpoint phase."), true),
                    ["outcome"] = Required(new ScalarAttributeDefinition(
                        TelemetryAttributeType.String, "Phase outcome."), true),
                },
            },
            ["pi.durable.tasks.active"] = new()
            {
                Kind = TelemetryMetricKind.UpDownCounter,
                Description = "Live tasks held by this Harness; decremented at terminal settlement or close.",
                Attributes = new Dictionary<string, TelemetryRequiredAttributeDefinition>(StringComparer.Ordinal)
                {
                    ["task"] = Required(new ScalarAttributeDefinition(
                        TelemetryAttributeType.String, "Registered task name."), true),
                },
            },
        }));

    /// <summary>上报的固定种类，与 schema 的 <c>type</c> 取值一致。</summary>
    public static string ReportKind(ReportInfo info) => info switch
    {
        ReportInfo.SessionFailed => "storage_failed",
        ReportInfo.TaskFaulted => "task_faulted",
        ReportInfo.TaskInterrupted => "task_interrupted",
        ReportInfo.MigrationFailed => "migration_failed",
        ReportInfo.TaskOutdated => "task_outdated",
        ReportInfo.TaskError => "task_error",
        ReportInfo.ExtensionError => "extension_error",
        ReportInfo.HostError => "host_error",
        ReportInfo.ListenerError => "listener_error",
        _ => "task_error",
    };

    /// <summary>上报携带的任务 id（没有任务时为 <see langword="null"/>）。</summary>
    public static long? TaskIdOf(ReportInfo info) => info switch
    {
        ReportInfo.TaskFaulted faulted => faulted.Task.Id.Value,
        ReportInfo.TaskInterrupted interrupted => interrupted.Task.Id.Value,
        ReportInfo.MigrationFailed migration => migration.Task.Id.Value,
        ReportInfo.TaskOutdated outdated => outdated.Task.Id.Value,
        ReportInfo.TaskError error => error.Task.Id.Value,
        _ => null,
    };

    private static TelemetryRequiredAttributeDefinition Required(
        TelemetryAttributeDefinition definition, bool required) => new(definition, required);
}
