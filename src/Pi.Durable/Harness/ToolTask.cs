using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Env;
using Pi.Durable.Session;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using JsonDict = IReadOnlyDictionary<string, object?>;
using Path = Pi.Chord.Delta.Path;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using TxDocChange = Pi.Chord.Delta.Tracker<IReadOnlyDictionary<string, object?>>.Change;

/// <summary>
/// pi.tool 任务的持久化输入：模型发起的调用（从 assistant 条目读），或运行中的工具经 <c>executeTool()</c>
/// 发起的嵌套调用（调用随输入携带，没有条目持有它）。对应 TS <c>ToolTaskInput</c>。
/// <para>v1/v2 的行没有 <c>kind</c>，按上游 migrate 的口径当作模型发起的调用。</para>
/// </summary>
public abstract record ToolTaskInput
{
    /// <summary>模型发起的调用：<c>{kind:"model", assistant, callId, unoffered?}</c>。</summary>
    public sealed record Model(EntryId Assistant, string CallId, bool? Unoffered = null) : ToolTaskInput;

    /// <summary>
    /// 嵌套调用：<c>{kind:"nested", parent, parentCallId, key, call, progress?}</c>。
    /// <c>progress = false</c> 表示不向槽位提交运行输出/明细/诊断（结果仍带）。
    /// </summary>
    public sealed record Nested(
        TaskId<object?> Parent, string ParentCallId, string Key, ToolCallContent Call, bool? Progress = null)
        : ToolTaskInput;

    public JsonDict ToJson() => this switch
    {
        Model model => BuildModel(model),
        Nested nested => BuildNested(nested),
        _ => throw new InvalidOperationException("Unknown pi.tool input"),
    };

    public static ToolTaskInput FromJson(JsonDict json)
    {
        var kind = json.TryGetValue("kind", out var rawKind) ? rawKind as string : null;
        if (kind == "nested")
        {
            var call = json.TryGetValue("call", out var rawCall) && rawCall is not null
                ? JsonTrees.Deserialize<ToolCallContent>(rawCall)
                : throw new InvalidOperationException("pi.tool nested input has no call");
            return new Nested(
                TaskId<object?>.From(Convert.ToInt64(json["parent"]!)),
                (string)json["parentCallId"]!,
                (string)json["key"]!,
                call,
                json.TryGetValue("progress", out var progress) && progress is false ? false : null);
        }

        return new Model(
            EntryId.From(Convert.ToInt64(json["assistant"]!)),
            (string)json["callId"]!,
            json.TryGetValue("unoffered", out var unoffered) && unoffered is true ? true : null);
    }

    private static JsonDict BuildModel(Model model)
    {
        var json = new Dictionary<string, object?>
        {
            ["kind"] = "model",
            ["assistant"] = model.Assistant.Value,
            ["callId"] = model.CallId,
        };
        if (model.Unoffered == true) json["unoffered"] = true;
        return json;
    }

    private static JsonDict BuildNested(Nested nested)
    {
        var json = new Dictionary<string, object?>
        {
            ["kind"] = "nested",
            ["parent"] = nested.Parent.Value,
            ["parentCallId"] = nested.ParentCallId,
            ["key"] = nested.Key,
            ["call"] = JsonTrees.ToTree(nested.Call),
        };
        if (nested.Progress == false) json["progress"] = false;
        return json;
    }
}

/// <summary>
/// pi.tool 任务的持久化 checkpoint（JSON 字典形状，phase 判别）：
/// <c>{ phase: "call" }</c> 或 <c>{ phase: "execute", arguments, replay: "safe" | "unsafe" }</c>。
/// 对应 TS <c>ToolTaskCheckpoint</c>（execute 携带最终参数与执行前记录的重放策略——持久化意图）。
/// </summary>
public abstract record ToolTaskCheckpoint
{
    public sealed record Call : ToolTaskCheckpoint;

    public sealed record Execute(object? Arguments, string Replay) : ToolTaskCheckpoint;

    public JsonDict ToJson() => this switch
    {
        Call => new Dictionary<string, object?> { ["phase"] = "call" },
        Execute execute => new Dictionary<string, object?>
        {
            ["phase"] = "execute",
            ["arguments"] = execute.Arguments,
            ["replay"] = execute.Replay,
        },
        _ => throw new InvalidOperationException("Unknown pi.tool checkpoint"),
    };

    public static ToolTaskCheckpoint FromJson(JsonDict json)
        => json.TryGetValue("phase", out var phase) && phase as string == "execute"
            ? new Execute(
                json.TryGetValue("arguments", out var arguments) ? arguments : null,
                json.TryGetValue("replay", out var replay) ? replay as string ?? "unsafe" : "unsafe")
            : new Call();
}

/// <summary>pi.tool 任务的结果：<c>{ entryId: EntryId, control?: ToolControl }</c>。对应 TS <c>ToolTaskResult</c>。</summary>
public sealed record ToolTaskResult(EntryId EntryId, ToolControl? Control)
{
    public static ToolTaskResult FromJson(JsonDict json)
    {
        ToolControl? control = null;
        if (json.TryGetValue("control", out var controlValue) && controlValue is JsonDict controlJson)
        {
            control = new ToolControl
            {
                AddTools = controlJson.TryGetValue("addTools", out var addTools) && addTools is IReadOnlyList<object?> add
                    ? add.OfType<string>().ToList()
                    : null,
                Terminate = controlJson.TryGetValue("terminate", out var terminate) && terminate is bool flag && flag,
                Handoff = controlJson.TryGetValue("handoff", out var handoff) ? handoff as string : null,
            };
        }

        return new ToolTaskResult(EntryId.From(Convert.ToInt64(json["entryId"]!)), control);
    }
}

/// <summary>
/// 内建工具任务：在其阶段 agent 的工具中解析被调用的工具，校验，运行 beforeTool，记录意图，执行，
/// 运行 afterTool，追加结果——全部在一个 call 处理器里，解析与结算之间不夹任何东西。
/// execute 只被恢复到达，应用重放规则。对应 TS <c>harness/tool.ts</c> 的 <c>ToolTask</c>。
/// <para>
/// C# 形状决策（见差异记录）：输入 / checkpoint / 结果都是严格 JSON 字典（MemoryStorage 深克隆要求）；
/// 文档草稿的槽字段变异以显式 Set / Splice / 逐叶 assign ops 表达。
/// </para>
/// </summary>
public static partial class ToolTask
{
    /// <summary>
    /// 运行中的工具发起的嵌套调用：按调用方任务的 key 定位，存它的任务与（终态后）结果。
    /// 一个调用一份文档，所以任何提交都不重写别人；随调用方任务退休。
    /// 对应 TS <c>NestedCallDoc</c>（task 作用域、族、<c>checkpointWhen: () =&gt; false</c>）。
    /// </summary>
    public static readonly DocFamilyToken<Dictionary<string, object?>, object?> NestedCallDoc =
        DurableDocuments.DefineDocFamily<Dictionary<string, object?>, object?>(new DocFamilyDefinition<
            Dictionary<string, object?>, object?>
        {
            Kind = "pi.tool.nested-call",
            Version = 1,
            Initial = _ => new Dictionary<string, object?>(),
            // 文档随它的任务退休：从不按 checkpoint 条件存基线。
            CheckpointWhen = (_, _, _) => false,
            Semantics = new DocumentSemantics.TaskScope(),
        });

    public const string TaskName = "pi.tool";

    /// <summary>擦除形状的内建任务定义（输入 / checkpoint / 结果均为 JSON 字典）。</summary>
    public static readonly DurableTask<JsonDict, JsonDict, JsonDict> Instance = DurableTasks.DefineTask(
        new TaskDefinition<JsonDict, JsonDict, JsonDict>
        {
            Name = TaskName,
            // Version 3 给「请求未提供的调用」的输入带 unoffered；旧版本输入没有该字段，原样迁移。
            Version = 3,
            Migrate = new ErasedMigrate((input, checkpoint, _) => (input, checkpoint)),
            Initial = _ => new ToolTaskCheckpoint.Call().ToJson(),
            Phases = new Dictionary<string, object?>
            {
                ["call"] = (TaskPhaseHandler)CallPhaseAsync,
                ["execute"] = (TaskPhaseHandler)ExecutePhaseAsync,
            },
            Abort = (TaskPhaseHandler)AbortAsync,
        });

    // ─── 阶段 ───────────────────────────────────────────────────────────────

    private static async Task CallPhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var call = await ReadCallAsync(runtime, task, context).ConfigureAwait(false);
        if (Live.CutOffTooOften(task, runtime))
        {
            await EndCutOffAsync(runtime, task, call, context).ConfigureAwait(false);
            return;
        }

        var input = ToolTaskInput.FromJson(
            task.Input as JsonDict ?? throw new InvalidOperationException("pi.tool input must be a JSON object"));
        var agent = await runtime.AgentAsync(context).ConfigureAwait(false);
        // 未提供的调用不解析名字：它请求里就没有，结算 tool_unavailable。
        var tool = input is ToolTaskInput.Model { Unoffered: true } ? null : agent.Tools.FirstOrDefault(each => each.Name == call.Name);
        if (tool is null)
        {
            var error = HarnessError("tool_unavailable", $"Tool {call.Name} is not available");
            await RejectAsync(runtime, call, null, error, context).ConfigureAwait(false);
            return;
        }

        var prepared = Prepare(tool, call.Arguments);
        var check = prepared.Error is null ? Validate(tool, call, prepared.Args) : prepared;
        if (check.Error is not null)
        {
            await RejectAsync(runtime, call, tool, Invalid(check.Error), context).ConfigureAwait(false);
            return;
        }

        var args = check.Args;
        string? block = null;
        await runtime.Hooks.EachAsync("beforeTool", async handler =>
        {
            if (block is not null || handler is not IToolHooks hooks) return;
            try
            {
                var decision = await hooks.BeforeTool(call with { Arguments = args }, runtime, context)
                    .ConfigureAwait(false);
                if (decision?.Block is not null) block = decision.Block;
                else if (decision?.Arguments is not null) args = decision.Arguments;
            }
            catch (Exception error)
            {
                if (runtime.Signal.IsCancellationRequested) throw;
                block = ErrorText(error);
            }
        }).ConfigureAwait(false);
        if (block is not null)
        {
            var blocked = HarnessError("blocked", $"Tool call blocked: {block}");
            await RejectAsync(runtime, call, tool, blocked, context).ConfigureAwait(false);
            return;
        }

        var validated = Validate(tool, call, args);
        if (validated.Error is not null)
        {
            await RejectAsync(runtime, call, tool, Invalid(validated.Error), context).ConfigureAwait(false);
            return;
        }

        var final = validated.Args;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var change = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            var slot = Live.ToolSlotOf(change, runtime.TaskId);
            if (slot is not null) Live.ReplaceSlot(change, slot with { Status = "running" });
            var intent = new ToolTaskCheckpoint.Execute(final, tool.Replay ?? "unsafe");
            return new TaskState { Status = TaskStatus.Running, Checkpoint = intent.ToJson() };
        }, context).ConfigureAwait(false);
        await RunAsync(runtime, call, tool, final, context).ConfigureAwait(false);
    }

    /// <summary>恢复：只有存储与当前策略都说 safe 才重跑。</summary>
    private static async Task ExecutePhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = task.State.Checkpoint as JsonDict is { } checkpointJson
            ? ToolTaskCheckpoint.FromJson(checkpointJson)
            : throw new InvalidOperationException("pi.tool execute phase requires a checkpoint");
        if (checkpoint is not ToolTaskCheckpoint.Execute intent)
            throw new InvalidOperationException("pi.tool execute phase requires an execute checkpoint");
        var call = await ReadCallAsync(runtime, task, context).ConfigureAwait(false);
        var agent = await runtime.AgentAsync(context).ConfigureAwait(false);
        var tool = agent.Tools.FirstOrDefault(each => each.Name == call.Name);
        if (intent.Replay == "safe" && tool?.Replay == "safe")
        {
            if (Live.CutOffTooOften(task, runtime))
            {
                await EndCutOffAsync(runtime, task, call, context).ConfigureAwait(false);
                return;
            }

            // 重跑从头上报；清除被打断的尝试已发布的内容。
            await runtime.CommitAsync(async (tx, _) =>
            {
                var change = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
                var slot = Live.ToolSlotOf(change, runtime.TaskId);
                if (slot is not null) Live.ReplaceSlot(change, Live.ClearProgress(slot));
                return null;
            }, context).ConfigureAwait(false);
            await RunAsync(runtime, call, tool, intent.Arguments, context).ConfigureAwait(false);
            return;
        }

        var message = $"Tool {call.Name} was interrupted and may have partially run";
        // failed 记录取消意图，因此该调用拥有的对话在无人监督时被中止。
        var ending = new Ending(TaskOutcomeStatus.Failed, message);
        await SettleFromSlotAsync(
            runtime, call, agent.Tools.FirstOrDefault(each => each.Name == call.Name),
            ending, "interrupted", message, context).ConfigureAwait(false);
    }

    private static async Task AbortAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var call = await ReadCallAsync(runtime, task, context).ConfigureAwait(false);
        var input = ToolTaskInput.FromJson(
            task.Input as JsonDict ?? throw new InvalidOperationException("pi.tool input must be a JSON object"));
        var agent = await runtime.AgentAsync(context).ConfigureAwait(false);
        // 未提供的调用无论如何都不可能跑过，其结果说明这一点。
        var (code, message) = input is ToolTaskInput.Model { Unoffered: true }
            ? ("tool_unavailable", $"Tool {call.Name} is not available")
            : ("aborted", $"Tool {call.Name} was aborted");
        // 上游只在剩余重启额度还够时跑钩子：连续打断到这个任务的最后一次尝试，可能正是钩子本身在把它打断，
        // 这一次跳过钩子（否则一个改写结果的钩子能让工具永远跑不完）。
        IToolRegistration? forHooks = null;
        if (input is not ToolTaskInput.Model { Unoffered: true } && (task.Restarts ?? 0) < runtime.Settings.RestartLimit - 1)
        {
            forHooks = agent.Tools.FirstOrDefault(each => each.Name == call.Name);
        }

        await SettleFromSlotAsync(
            runtime, call, forHooks,
            new Ending(TaskOutcomeStatus.Aborted), code, message, context).ConfigureAwait(false);
    }

    // ─── 调用与参数 ─────────────────────────────────────────────────────────

    /// <summary>assistant 条目中本次调用的 toolCall。</summary>
    private static async Task<ToolCallContent> ReadCallAsync(ITaskRuntime runtime, TaskRecord task, Context context)
    {
        var input = task.Input as JsonDict
            ?? throw new InvalidOperationException("pi.tool input must be a JSON object");
        var parsed = ToolTaskInput.FromJson(input);
        if (parsed is ToolTaskInput.Nested nested) return nested.Call;
        var model = (ToolTaskInput.Model)parsed;
        var entry = await runtime.EntryAsync(DurableEntries.AssistantEntry, model.Assistant, context)
            .ConfigureAwait(false);
        var message = entry?.Model is { Count: > 0 } content ? content[0] : null;
        var call = message is AssistantMessage assistant
            ? assistant.ToolCalls.FirstOrDefault(content => content.Id == model.CallId)
            : null;
        if (call is null) throw new InvalidOperationException($"Entry {model.Assistant.Value} has no tool call {model.CallId}");
        return call;
    }

    /// <summary>参数，或其非法原因。</summary>
    private readonly record struct Checked(object? Args, string? Error);

    /// <summary>工具修复后的调用参数；抛出的修复使参数非法。</summary>
    private static Checked Prepare(IToolRegistration tool, object? args)
    {
        try
        {
            return new Checked(tool.PrepareArguments(args ?? new Dictionary<string, object?>()), null);
        }
        catch (Exception error)
        {
            return new Checked(null, ErrorText(error));
        }
    }

    /// <summary>按实现的 schema 校验并强制转换的参数。</summary>
    private static Checked Validate(IToolRegistration tool, ToolCallContent call, object? args)
    {
        try
        {
            var definition = new ToolDefinition(tool.Name, tool.Description, tool.Parameters);
            var node = Pi.Ai.Utils.Validation.ValidateToolArguments(definition, call with { Arguments = args });
            return new Checked(FromNode(node), null);
        }
        catch (Exception error)
        {
            return new Checked(null, ErrorText(error));
        }
    }

    private static ToolExecutionResult Invalid(string message) => HarnessError("invalid_arguments", message);

    // ─── 执行 ───────────────────────────────────────────────────────────────

    /// <summary>一个运行中工具经其 api 上报的内容：输出、最后的 details 与诊断。</summary>
    private sealed class Reported
    {
        public required ToolOutput.Buffer Output { get; init; }

        public required OutputLimits Limits { get; init; }

        public List<ToolDiagnostic> Diagnostics { get; } = [];

        public object? Details { get; set; }
    }

    /// <summary>以解析到的实现执行，然后结算其结果。</summary>
    private static async Task RunAsync(
        ITaskRuntime runtime, ToolCallContent call, IToolRegistration tool, object? args, Context context)
    {
        var limits = new OutputLimits
        {
            MaxBytes = tool.OutputLimits?.MaxBytes ?? Truncate.DefaultMaxBytes,
            MaxLines = tool.OutputLimits?.MaxLines ?? Truncate.DefaultMaxLines,
            Retain = tool.OutputLimits?.Retain == "tail" ? OutputRetain.Tail : OutputRetain.Head,
        };
        var reported = new Reported { Output = new ToolOutput.Buffer(limits), Limits = limits };
        var progress = PublishProgress(runtime, reported, context);
        var ended = false;
        void AssertLive()
        {
            if (ended) throw new InvalidOperationException($"Tool call {call.Id} has settled");
        }

        var api = new ToolApi(runtime, call, reported, progress, AssertLive, limits);
        ToolExecutionResult result;
        var ending = Completed;
        try
        {
            // 为本次调用构建，恢复后的重跑由此得到调用当时对话的环境。
            var env = await runtime.EnvAsync(context).ConfigureAwait(false);
            api.Env = env;
            result = await tool.ExecuteAsync(args ?? new Dictionary<string, object?>(), api, context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (runtime.Signal.IsCancellationRequested)
            {
                ended = true;
                foreach (var waiter in await progress.Stop().ConfigureAwait(false)) waiter.TrySetException(error);
                throw;
            }

            result = new ToolExecutionResult
            {
                IsError = true,
                Diagnostics = [ErrorDiagnostic("tool_error", ErrorText(error))],
            };
            // execute() 或环境构建抛出让任务以 failed 结束（取消该调用拥有的东西）；它不再监督。
            // 错误文本已在结果条目里。
            ending = new Ending(TaskOutcomeStatus.Failed, $"Tool {call.Name} threw");
        }

        ended = true;
        reported.Output.End();
        // 仍在等进度提交的 details 以终态提交结算，作为最后一次冲刷。
        var pending = await progress.Stop().ConfigureAwait(false);
        try
        {
            var settled = await FinalResultAsync(runtime, call, result, reported, context).ConfigureAwait(false);
            await SettleAsync(runtime, call, ending, _ => settled, context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            foreach (var waiter in pending) waiter.TrySetException(error);
            throw;
        }

        foreach (var waiter in pending) waiter.TrySetResult(null);
    }

    /// <summary>
    /// 工具上报内容进其 pi.live.tools 槽的节流提交，每次只写自上次以来变化的部分。
    /// 对应 TS <c>publishProgress</c>。
    /// </summary>
    private static ToolOutput.Progress PublishProgress(ITaskRuntime runtime, Reported reported, Context context)
    {
        var written = (Text: string.Empty, Details: (object?)null, Diagnostics: 0);
        return new ToolOutput.Progress(
            async () =>
            {
                // 同步捕获全部内容：提交在途时工具仍在上报。
                var snapshot = reported.Output.Snapshot();
                var currentText = snapshot.Text;
                var currentDetails = reported.Details;
                var currentCount = reported.Diagnostics.Count;
                var added = reported.Diagnostics.Skip(written.Diagnostics).ToList();
                var detailsChanged = !ReferenceEquals(currentDetails, written.Details);
                // 提交要写的内容，由 Chord 对字符串做差：追加、裁剪加追加共享段之后的部分，
                // 或其有界重叠搜索一无所获时的整个窗口。
                long bytes = 0;
                if (currentText != written.Text)
                {
                    var shared = currentText.StartsWith(written.Text, StringComparison.Ordinal)
                        ? written.Text.Length
                        : DeltaText.Overlap(written.Text, currentText, 65_536);
                    bytes += Truncate.Utf8ByteLength(currentText[shared..]);
                }

                if (detailsChanged) bytes += Truncate.Utf8ByteLength(JsonSerializer.Serialize(currentDetails));
                if (added.Count > 0)
                {
                    bytes += Truncate.Utf8ByteLength(
                        JsonSerializer.Serialize(added.Select(DiagnosticJson).ToList()));
                }

                await runtime.CommitAsync(async (tx, _) =>
                {
                    var change = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
                    var slot = Live.ToolSlotOf(change, runtime.TaskId);
                    if (slot is null || !Live.TrySlotIndex(change, slot, out var index)) return null;
                    // 提醒：output 作为一个字符串字段赋值。Chord 随后把它差分成追加，
                    // 或滑动尾部的「裁剪加追加」；替换整个槽对象会让每次提交记录整个窗口。
                    var slotPath = Live.SlotPath(index);
                    if ((slot.Output ?? "") != currentText)
                        change.Set(slotPath.Append(Seg.Key("output")), currentText);
                    if (snapshot.DroppedBytes > 0)
                        change.Set(slotPath.Append(Seg.Key("droppedBytes")), snapshot.DroppedBytes);
                    if (snapshot.DroppedLines > 0)
                        change.Set(slotPath.Append(Seg.Key("droppedLines")), snapshot.DroppedLines);
                    // details 逐叶差分、新诊断追加，使每次提交只写变化的部分。
                    if (detailsChanged && currentDetails is not null)
                        AssignJsonOps(change, slotPath.Append(Seg.Key("details")), slot.Details, currentDetails);
                    if (added.Count > 0)
                    {
                        var diagnosticsPath = slotPath.Append(Seg.Key("diagnostics"));
                        var addedJson = added.Select(DiagnosticJson).ToList();
                        if (slot.Diagnostics is { } existing)
                            change.Splice(diagnosticsPath, existing.Count, 0, addedJson);
                        else
                            change.Set(diagnosticsPath, addedJson);
                    }

                    return null;
                }, context).ConfigureAwait(false);
                written = (currentText, currentDetails, currentCount);
                return bytes;
            },
            error =>
            {
                // 中止标记或关闭之后的拒绝是预期内的；已提交状态保持一致。
                if (!runtime.Signal.IsCancellationRequested) runtime.Report(error);
            },
            runtime.Settings.Progress.OutputIntervalMs,
            new ToolOutput.ProgressClock
            {
                // 调用结束之后不再提交；时钟只需仍能作答。
                Now = () =>
                {
                    try
                    {
                        return runtime.Now();
                    }
                    catch (InvalidOperationException)
                    {
                        return Environment.TickCount64;
                    }
                },
                SetTimer = runtime.SetTimer,
            });
    }

    /// <summary>
    /// 把 value 逐叶差分进 live 文档的 target 路径（对应 TS <c>assignJson</c>；C# 的 Tracker 无
    /// 深度草稿，以显式动词表达同款最小差分：容器逐叶、字符串叶追加、其余整体 set）。
    /// </summary>
    private static void AssignJsonOps(TxDocChange live, Path path, object? current, object? value)
    {
        switch (current, value)
        {
            case (JsonDict currentMap, JsonDict valueMap):
            {
                foreach (var key in currentMap.Keys)
                {
                    if (!valueMap.ContainsKey(key)) live.Delete(path.Append(Seg.Key(key)));
                }

                foreach (var (key, child) in valueMap)
                {
                    AssignJsonOps(live, path.Append(Seg.Key(key)),
                        currentMap.TryGetValue(key, out var found) ? found : null, child);
                }

                return;
            }
            case (IReadOnlyList<object?> currentList, IReadOnlyList<object?> valueList)
                when currentList.Count <= valueList.Count:
            {
                for (var index = 0; index < valueList.Count; index++)
                {
                    AssignJsonOps(live, path.Append(Seg.Index(index)),
                        index < currentList.Count ? currentList[index] : null, valueList[index]);
                }

                return;
            }
        }

        if (!Equals(current, value)) live.Set(path, value);
    }

    // ─── 结果 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 结算后的结果：工具结果以保留输出与最后的 details 兜底、其诊断排在 api 上报的之后、应用 afterTool、
    /// 显式文本有界化、并把 Harness 的截断诊断放在最后。对应 TS <c>finalResult</c>。
    /// </summary>
    private static async Task<ToolExecutionResult> FinalResultAsync(
        ITaskRuntime runtime, ToolCallContent call, ToolExecutionResult result, Reported reported, Context context)
    {
        var harnessDiagnostics = new List<ToolDiagnostic>();
        var retained = result.Content is null ? reported.Output.Snapshot() : null;
        IReadOnlyList<ContentBlock> content = retained is not null
            ? retained.Text == "" ? [] : (IReadOnlyList<ContentBlock>)[new TextContent(retained.Text)]
            : result.Content!;
        var final = result with
        {
            Content = content,
            Details = result.Details ?? reported.Details,
            Diagnostics = [.. reported.Diagnostics, .. result.Diagnostics ?? []],
        };
        await runtime.Hooks.EachAsync("afterTool", async handler =>
        {
            if (handler is not IToolHooks hooks) return;
            var replacement = await hooks.AfterTool(call, final, runtime, context)
                .ConfigureAwait(false);
            if (replacement is not null) final = replacement;
        }).ConfigureAwait(false);
        // 保留输出的截断诊断只在 afterTool 保留该内容时适用。
        if (retained is { DroppedBytes: > 0 } && ReferenceEquals(final.Content, content))
        {
            harnessDiagnostics.Add(TruncatedDiagnostic(retained.DroppedLines, retained.DroppedBytes));
        }

        var bounded = BoundContent(final.Content ?? [], reported.Limits);
        if (bounded.DroppedBytes > 0)
        {
            harnessDiagnostics.Add(TruncatedDiagnostic(bounded.DroppedLines, bounded.DroppedBytes, reported.Limits.Retain));
        }

        return final with { Content = bounded.Content, Diagnostics = [.. final.Diagnostics ?? [], .. harnessDiagnostics] };
    }

    /// <summary>
    /// 结算一个 Harness 在 call 阶段就决定了、没有执行工具的结果：工具没解析到、参数无效、或 <c>beforeTool</c>
    /// 拦截。<c>afterTool</c> 与执行过的调用一视同仁。对应 TS <c>reject()</c>。
    /// </summary>
    private static async Task RejectAsync(
        ITaskRuntime runtime, ToolCallContent call, IToolRegistration? tool,
        ToolExecutionResult result, Context context)
    {
        var settled = await AfterHarnessResultAsync(runtime, call, tool, result, null, context)
            .ConfigureAwait(false);
        await SettleAsync(runtime, call, Completed, _ => settled, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 结束一个被连续切断 <c>settings.restartLimit</c> 次的调用（spec §5.4）：像被打断的调用那样由槽拼出错误结果，
    /// 但不再跑它。与被打断一样是 <c>failed</c>，因此它拥有的工作会被中止。结果不过 <c>afterTool</c>：工具没有
    /// 运行，跑钩子可能正是切断它的原因。对应 TS <c>endCutOff()</c>。
    /// </summary>
    private static async Task EndCutOffAsync(
        ITaskRuntime runtime, TaskRecord task, ToolCallContent call, Context context)
    {
        var message = $"Tool {call.Name} was interrupted {Live.CutOffCount(task)}; it is not run again";
        var ending = new Ending(TaskOutcomeStatus.Failed, message, ReportReasons.RestartLimit);
        await SettleAsync(
            runtime, call, ending,
            slot =>
            {
                var (built, _) = FromSlot(slot, ReportReasons.RestartLimit, message);
                return built;
            },
            context).ConfigureAwait(false);
    }

    /// <summary>
    /// 结算一个由槽拼出来的错误结果（被打断、中止）。工具没在运行，所以提交之前读到的槽在提交时仍然是最新的。
    /// 结果过一遍 <c>afterTool</c> 并按其输出上限有界化。对应 TS <c>settleFromSlot()</c>。
    /// </summary>
    private static async Task SettleFromSlotAsync(
        ITaskRuntime runtime, ToolCallContent call, IToolRegistration? tool,
        Ending ending, string code, string message, Context context)
    {
        var live = await runtime.SnapshotAsync(Live.LiveDoc, runtime.ConversationId, context).ConfigureAwait(false);
        var slot = live is not null
            && live.TryGetValue("tools", out var toolsValue)
            && toolsValue is IReadOnlyList<object?> slots
            ? slots.OfType<JsonDict>().Select(ToolSlot.FromJson)
                .FirstOrDefault(each => each.TaskId == runtime.TaskId)
            : null;
        var (built, truncation) = FromSlot(slot, code, message);
        var settled = await AfterHarnessResultAsync(runtime, call, tool, built, truncation, context)
            .ConfigureAwait(false);
        await SettleAsync(runtime, call, ending, _ => settled, context).ConfigureAwait(false);
    }

    /// <summary>工具的输出上限（缺省值补齐）；工具没解析到时全取缺省。对应 TS <c>outputLimits()</c>。</summary>
    private static OutputLimits LimitsOf(IToolRegistration? tool) => new()
    {
        MaxBytes = tool?.OutputLimits?.MaxBytes ?? Truncate.DefaultMaxBytes,
        MaxLines = tool?.OutputLimits?.MaxLines ?? Truncate.DefaultMaxLines,
        Retain = tool?.OutputLimits?.Retain == "tail" ? OutputRetain.Tail : OutputRetain.Head,
    };

    /// <summary>
    /// Harness 自己写的、没有执行工具的结果，跑完 <c>afterTool</c> 链之后按执行过的调用那样归一化：输出按工具的
    /// 上限（未解析到则全缺省）有界化，有截断就加截断诊断，而钩子换掉了内容时丢弃原有截断诊断——那描述的是
    /// 钩子丢掉的内容。对应 TS <c>afterHarnessResult()</c>（端口的 ToolExecutionResult 没有 structuredOutput，
    /// 该字段按缺省不存在）。
    /// </summary>
    private static async Task<ToolExecutionResult> AfterHarnessResultAsync(
        ITaskRuntime runtime, ToolCallContent call, IToolRegistration? tool,
        ToolExecutionResult result, ToolDiagnostic? truncation, Context context)
    {
        var final = result;
        await runtime.Hooks.EachAsync("afterTool", async handler =>
        {
            if (handler is not IToolHooks hooks) return;
            var replacement = await hooks.AfterTool(call, final, runtime, context).ConfigureAwait(false);
            if (replacement is not null) final = replacement;
        }).ConfigureAwait(false);
        var harnessDiagnostics = new List<ToolDiagnostic>();
        // 原有截断诊断只在钩子保留该内容时适用。
        if (truncation is not null && ReferenceEquals(final.Content, result.Content))
        {
            harnessDiagnostics.Add(truncation);
        }

        var limits = LimitsOf(tool);
        var bounded = BoundContent(final.Content ?? [], limits);
        if (bounded.DroppedBytes > 0)
        {
            harnessDiagnostics.Add(TruncatedDiagnostic(bounded.DroppedLines, bounded.DroppedBytes, limits.Retain));
        }

        return final with
        {
            Content = bounded.Content,
            Diagnostics = [.. final.Diagnostics ?? [], .. harnessDiagnostics],
        };
    }

    /// <summary>
    /// 工具的终态提交：追加结果条目、标记槽完成，并以条目 ID 完成或以 aborted 结束。
    /// build 收到槽，使中断与中止能上报持久的部分输出。对应 TS <c>settle</c>。
    /// </summary>
    private static async Task SettleAsync(
        ITaskRuntime runtime,
        ToolCallContent call,
        Ending ending,
        Func<ToolSlot?, ToolExecutionResult> build,
        Context context)
    {
        await runtime.CommitAsync(async (tx, _) =>
        {
            var change = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            var slot = Live.ToolSlotOf(change, runtime.TaskId);
            var result = build(slot);
            var entry = await AppendToolResultAsync(tx, runtime.ConversationId, call, result, runtime.Now())
                .ConfigureAwait(false);
            if (slot is not null) Live.FinishSlot(change, slot, entry.Id);
            var resultJson = new Dictionary<string, object?> { ["entryId"] = entry.Id.Value };
            if (ending.Status == TaskOutcomeStatus.Failed)
            {
                return new TaskState
                {
                    Status = TaskStatus.Terminal,
                    Outcome = new TaskOutcome
                    {
                        Status = TaskOutcomeStatus.Failed,
                        Error = new TaskOutcomeError
                        {
                            Message = ending.Message!,
                            // 收场原因（被切断到上限）给宿主看的细节；普通失败不带。
                            Detail = ending.Reason is null
                                ? null
                                : new Dictionary<string, object?> { ["reason"] = ending.Reason },
                        },
                        Result = resultJson,
                    },
                };
            }

            if (ending.Status == TaskOutcomeStatus.Aborted)
            {
                return new TaskState
                {
                    Status = TaskStatus.Terminal,
                    Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Aborted, Result = resultJson },
                };
            }

            // 工具自由构建 control 对象；丢弃设为 undefined 的键使任务结果是严格 JSON。
            var control = ControlJson(result.Control);
            if (control is not null) resultJson["control"] = control;
            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = resultJson },
            };
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 工具任务如何结束；两种情况都会追加结果条目。failed（执行抛出或被打断）为该调用拥有的对话记录
    /// 取消意图；带 isError 的结果仍然完成。对应 TS <c>Ending</c>。
    /// </summary>
    private sealed record Ending(TaskOutcomeStatus Status, string? Message = null, string? Reason = null);

    private static readonly Ending Completed = new(TaskOutcomeStatus.Completed);

    /// <summary>来自槽的持久部分输出、details 与诊断的错误结果。对应 TS <c>fromSlot</c>。</summary>
    private static (ToolExecutionResult Result, ToolDiagnostic? Truncation) FromSlot(
        ToolSlot? slot, string code, string message)
    {
        var diagnostics = new List<ToolDiagnostic>(slot?.Diagnostics ?? []);
        ToolDiagnostic? truncation = null;
        var droppedBytes = slot?.DroppedBytes ?? 0;
        if (droppedBytes > 0)
        {
            truncation = TruncatedDiagnostic(slot?.DroppedLines ?? 0, droppedBytes);
            diagnostics.Add(truncation);
        }

        diagnostics.Add(ErrorDiagnostic(code, message));
        return (new ToolExecutionResult
        {
            Content = slot?.Output is not { Length: > 0 } ? [] : (IReadOnlyList<ContentBlock>)[new TextContent(slot.Output)],
            IsError = true,
            Details = slot?.Details,
            Diagnostics = diagnostics,
        }, truncation);
    }

    /// <summary>Harness 自己写的错误结果：无内容和一个带 code 的 error 诊断。对应 TS <c>harnessError</c>。</summary>
    public static ToolExecutionResult HarnessError(string code, string message) => new()
    {
        Content = [],
        IsError = true,
        Diagnostics = [ErrorDiagnostic(code, message)],
    };

    private static ToolDiagnostic ErrorDiagnostic(string code, string message) => new()
    {
        Severity = "error",
        Message = message,
        Code = code,
    };

    /// <summary>Harness 的截断诊断；从槽重建后 retain 未知。对应 TS <c>truncated</c>。</summary>
    private static ToolDiagnostic TruncatedDiagnostic(long droppedLines, long droppedBytes, OutputRetain? retain = null)
    {
        var kept = retain is null ? "" : $" to its {(retain == OutputRetain.Head ? "beginning" : "end")}";
        return new ToolDiagnostic
        {
            Severity = "warn",
            Code = "truncated",
            Message = $"Output truncated{kept}: {droppedLines} lines, {droppedBytes} bytes dropped",
        };
    }

    // ─── 结果条目 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 追加一条 pi.tool-result 条目。内容以渲染后的诊断结尾，存储的消息就是模型看到的；
    /// data 持结构化列表。结果的使用量在同一提交里计入 pi.usage。对应 TS <c>appendToolResult</c>。
    /// </summary>
    public static async Task<TypedEntry<IReadOnlyDictionary<string, object?>>> AppendToolResultAsync(
        ITx tx, ConversationId conversationId, ToolCallContent call, ToolExecutionResult result, long timestamp)
    {
        var diagnostics = new List<ToolDiagnostic>(result.Diagnostics ?? []);
        var content = new List<ContentBlock>(result.Content ?? []);
        if (diagnostics.Count > 0) content.Add(new TextContent(RenderDiagnostics(diagnostics)));
        var message = new ToolResultMessage(
            call.Id, call.Name, content, result.IsError, result.Details, timestamp);
        if (result.Usage is { } usage)
        {
            await Usage.RecordUsageAsync(tx, conversationId, Usage.ToolsBucket, call.Name, usage)
                .ConfigureAwait(false);
        }

        return await tx.AppendEntryAsync(DurableEntries.ToolResultEntry, conversationId,
            new TypedEntryDraft<IReadOnlyDictionary<string, object?>>
            {
                Model = [message],
                Data = new Dictionary<string, object?>
                {
                    ["diagnostics"] = diagnostics.Select(DiagnosticJson).ToList(),
                },
            }).ConfigureAwait(false);
    }

    private static string RenderDiagnostics(IReadOnlyList<ToolDiagnostic> diagnostics)
        => $"<harness>\n{string.Join("\n", diagnostics.Select(diagnostic => $"[{diagnostic.Severity}] {diagnostic.Message}"))}\n</harness>";

    // ─── 内容有界化 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 有界化结果内容的文本。拼接文本超限时，文本项被首个（head）或最后一个（tail）文本项位置上的一个
    /// 有界项替换；其余内容保留。对应 TS <c>boundContent</c>。
    /// </summary>
    private static (IReadOnlyList<ContentBlock> Content, long DroppedBytes, long DroppedLines) BoundContent(
        IReadOnlyList<ContentBlock> content, OutputLimits limits)
    {
        var texts = content.OfType<TextContent>().ToList();
        var bounded = ToolOutput.BoundOutput(string.Concat(texts.Select(item => item.Text)), limits);
        if (bounded.DroppedBytes == 0) return (content, 0, 0);
        var keep = limits.Retain == OutputRetain.Head ? texts[0] : texts[^1];
        var result = new List<ContentBlock>();
        foreach (var item in content)
        {
            if (item is not TextContent text) result.Add(item);
            else if (ReferenceEquals(text, keep)) result.Add(text with { Text = bounded.Text });
        }

        return (result, bounded.DroppedBytes, bounded.DroppedLines);
    }

    // ─── 杂项 ───────────────────────────────────────────────────────────────

    private static Dictionary<string, object?>? ControlJson(ToolControl? control)
    {
        if (control is null) return null;
        var json = new Dictionary<string, object?>(StringComparer.Ordinal);
        // TS 的 terminate 类型是 `true`（字面量），false 不可表示——只在 true 时写入与 TS 完全一致。
        if (control.AddTools is { Count: > 0 } addTools) json["addTools"] = addTools.ToList();
        if (control.Terminate) json["terminate"] = true;
        if (control.Handoff is { } handoff) json["handoff"] = handoff;
        return json.Count > 0 ? json : null;
    }

    internal static object? DiagnosticJson(ToolDiagnostic diagnostic)
    {
        var json = new Dictionary<string, object?>
        {
            ["severity"] = diagnostic.Severity,
            ["message"] = diagnostic.Message,
        };
        if (diagnostic.Code is { } code) json["code"] = code;
        return json;
    }

    /// <summary>JsonNode → 严格 JSON 树（校验器以 JsonNode 承载，文档与任务状态以字典承载）。</summary>
    private static object? FromNode(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True or JsonValueKind.False => value.GetValue<bool>(),
            JsonValueKind.Number => value.TryGetValue<long>(out var integer) ? integer : value.GetValue<double>(),
            _ => null,
        },
        JsonObject @object => (object)@object.ToDictionary(
            entry => entry.Key,
            entry => FromNode(entry.Value)),
        JsonArray array => (object)array.Select(FromNode).ToList(),
        _ => null,
    };

    private static string ErrorText(Exception error) => error.Message;
}
