using System;
using System.Collections.Generic;
using Pi.Ai.Types;
using Pi.Chord;
using Pi.Chord.Context;
using Pi.Durable.Env;
using Pi.Chord.Delta;
using Pi.Telemetry;
using Pi.Durable.Types;

using JsonDict = System.Collections.Generic.IReadOnlyDictionary<string, object?>;
using Path = Pi.Chord.Delta.Path;

namespace Pi.Durable.Harness;

using BclTaskScheduler = System.Threading.Tasks.TaskScheduler;

/// <summary>ToolTask 的 api 适配器（partial 补充）。</summary>
public static partial class ToolTask
{

    /// <summary>
    /// 运行中的工具发起一次嵌套调用：按 <paramref name="key"/> 在调用方任务下定位；已有就复用并校验同工具同参数，
    /// 且当该调用已有结果时判定为「重放」。没有则在同一次提交里建它的工具任务并记下 id。
    /// 对应 TS <c>startNestedCall()</c>。
    /// <para>重放时在 <paramref name="context"/> 携带的 span 上发 <c>pi.durable.replay</c>——这是该事件唯一的发射点。</para>
    /// </summary>
    public static async Task<TaskId<object?>> StartNestedAsync(
        ITaskRuntime runtime,
        string parentCallId,
        string name,
        JsonDict arguments,
        string key,
        bool abandonOnRestart,
        Context context)
    {
        var call = new ToolCallContent($"{parentCallId}/{key}", name, arguments);
        TaskId<object?>? id = null;
        var replayed = false;

        await runtime.CommitAsync(async (tx, _) =>
        {
            var state = await tx.DocAsync(NestedCallDoc, runtime.TaskId, key, null).ConfigureAwait(false);
            var existing = state.Draft.TryGetValue("taskId", out var raw) && raw is not null
                ? TaskId<object?>.From(Convert.ToInt64(raw))
                : (TaskId<object?>?)null;

            if (existing is { } found)
            {
                var input = (await tx.GetTaskAsync(found).ConfigureAwait(false))?.Input as JsonDict;
                var parsed = input is null ? null : ToolTaskInput.FromJson(input);
                var same = parsed is ToolTaskInput.Nested nested
                    && nested.Call.Name == name
                    && DeltaDiff.JsonEquals(nested.Call.Arguments, arguments);
                if (!same)
                {
                    throw new InvalidOperationException(
                        $"Nested call {call.Id} was already made with another tool or other arguments");
                }

                id = found;
                replayed = state.Draft.ContainsKey("result");
                return null;
            }

            var created = await tx.CreateTaskAsync(
                Instance,
                new ToolTaskInput.Nested(runtime.TaskId, parentCallId, key, call).ToJson(),
                new TaskOptions
                {
                    Ownership = new TaskOwnership.TaskOwner(runtime.TaskId),
                    ConversationId = runtime.ConversationId,
                    AbandonOnRestart = abandonOnRestart,
                }).ConfigureAwait(false);
            state.Set(Path.Root.Append(Seg.Key("taskId")), created.Value);
            id = TaskId<object?>.From(created.Value);
            return null;
        }, context).ConfigureAwait(false);

        if (replayed)
        {
            context.Value(DurableTelemetry.SpanKey)?.AddEvent(
                "pi.durable.replay", new SpanAttributes { ["tool.name"] = name });
        }

        return id ?? throw new InvalidOperationException("nested call was not admitted");
    }
    /// <summary>
    /// 为一次调用构建的 <see cref="IToolExecutionApi"/>。对应 TS run() 里的 api 对象：
    /// output / diagnostic / details 在结算后拒绝；details 等待进度提交并随其取消。
    /// </summary>
    private sealed class ToolApi(
        ITaskRuntime runtime,
        ToolCallContent call,
        Reported reported,
        ToolOutput.Progress progress,
        Action assertLive,
        OutputLimits limits) : RuntimeDocumentFaces(runtime, runtime), IToolExecutionApi
    {
        /// <summary>为本次调用构建的环境（执行前注入）；无环境为 null。</summary>
        public IExecutionEnv? Env { get; set; }

        public TaskId<object?> TaskId => runtime.TaskId;

        public ConversationId ConversationId => runtime.ConversationId;

        public string CallId => call.Id;

        public IRegistrySnapshot Registry => runtime.Registry;

        public Task<Agent> AgentAsync(Context context) => runtime.AgentAsync(context);

        public Pi.Ai.Models.ModelSpec? GetModel(string provider, string modelId)
            => runtime.Models.GetModel(provider, modelId);

        public long Now() => runtime.Now();

        /// <summary>无 key 的嵌套调用按调用内递增序号取键（与上游 <c>++sequence</c> 同序）。</summary>
        private int _nestedSequence;

        /// <summary>
        /// 跑一次嵌套 durable 调用：建/复用嵌套任务，等它落定，再从调用方文档读结果。
        /// 任务被 faulted/orphaned 时文档没有结果，按结局造一个错误结果。
        /// 对应 TS <c>executeTool()</c>（tool.ts:520）。
        /// </summary>
        public async Task<ToolExecutionResult> ExecuteToolAsync(
            string name, object? arguments, Context context, ExecuteToolOptions? options = null)
        {
            assertLive();
            var key = options?.Key ?? (++_nestedSequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var json = arguments as JsonDict ?? new Dictionary<string, object?>();

            var id = await StartNestedAsync(
                runtime, call.Id, name, json, key, options?.AbandonOnRestart ?? false, context).ConfigureAwait(false);
            var settled = await runtime.WaitForTaskAsync(id, context).ConfigureAwait(false);

            var document = await runtime.SnapshotAsync(NestedCallDoc, runtime.TaskId, key, context)
                .ConfigureAwait(false);
            if (document is not null
                && document.TryGetValue("result", out var stored)
                && stored is not null)
            {
                // 复制一份：工具可以改它，而文档里的值不可变。
                var message = JsonTrees.Deserialize<ToolResultMessage>(JsonTrees.ToTree(stored));
                return new ToolExecutionResult
                {
                    Content = message.Content,
                    IsError = message.IsError,
                    Details = message.Details,
                };
            }

            // 没有结果 = 嵌套任务被调度器 faulted/orphaned：给调用方一个可读的失败。
            var outcome = settled.State.Outcome;
            return new ToolExecutionResult
            {
                IsError = true,
                Diagnostics =
                [
                    new ToolDiagnostic
                    {
                        Severity = "error",
                        Code = outcome?.Status switch
                        {
                            TaskOutcomeStatus.Faulted => "task_faulted",
                            TaskOutcomeStatus.Orphaned => "task_orphaned",
                            _ => "task_failed",
                        },
                        Message = outcome?.Error?.Message ?? $"Nested call to {name} did not produce a result",
                    },
                ],
            };
        }

        public void Output(string chunk, ShellOutputSkip? skipped = null)
        {
            assertLive();
            if (reported.Output.Push(chunk, skipped)) progress.Mark();
        }

        public void Output(byte[] chunk, ShellOutputSkip? skipped = null)
        {
            assertLive();
            if (reported.Output.Push(chunk, skipped)) progress.Mark();
        }

        public ShellOutputWindow? OutputWindow =>
            limits.Retain == OutputRetain.Tail
                ? new ShellOutputWindow
                {
                    MaxBytes = limits.MaxBytes,
                    MaxLines = limits.MaxLines,
                    MinIntervalMs = runtime.Settings.Progress.OutputIntervalMs,
                    BytesPerSecond = ToolOutput.ProgressBytesPerSecond,
                }
                : null;

        public void Diagnostic(ToolDiagnostic diagnostic)
        {
            assertLive();
            reported.Diagnostics.Add(diagnostic);
            progress.Mark();
        }

        public async Task DetailsAsync(object? value, Context context)
        {
            assertLive();
            if (context.AbortSignal is { IsCancellationRequested: true } signal)
                signal.ThrowIfCancellationRequested();
            reported.Details = Json.CopyJson(value, new CopyJsonOptions { OmitUndefinedProperties = true });
            var committed = progress.MarkAndWait();
            // 取消该等待时更新原地保留；提交自身的结局仍被观察（对齐 TS committed.catch(() => {})）。
            _ = committed.ContinueWith(static task => _ = task.Exception, BclTaskScheduler.Default);
            await ContextSignals.AwaitWithContext(committed, context).ConfigureAwait(false);
        }

        public async Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, Context context)
        {
            T result = default!;
            await runtime.CommitAsync(async (tx, _) =>
            {
                result = await change(tx).ConfigureAwait(false);
                return null;
            }, context).ConfigureAwait(false);
            return result;
        }

        public Task<object?> MemoAsync(string name, Context context) => runtime.MemoAsync(name, context);

        public Task<object> MemoAsync(string name, object candidate, Context context)
            => runtime.MemoAsync(name, candidate, context);

        public Task<TaskId<object?>> CreateTaskAsync(
            AnyDurableTask task, object? input, TaskOptions options, Context context)
            => runtime.CreateTaskAsync(task, input, options, context);

        public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id, Context context)
            => runtime.GetTaskAsync(id, context);

        public Task<SettledTask> WaitForTaskAsync(TaskId<object?> id, Context context)
            => runtime.WaitForTaskAsync(id, context);

        public Task<IConversationHandle?> ConversationAsync(ConversationId id, Context context)
            => runtime.ConversationAsync(id, context);
    }
}
