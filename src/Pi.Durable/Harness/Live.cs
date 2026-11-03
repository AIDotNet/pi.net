using Pi.Chord.Delta;
using Pi.Durable.Session;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using Path = Pi.Chord.Delta.Path;
using TxDocChange = Pi.Chord.Delta.Tracker<IReadOnlyDictionary<string, object?>>.Change;

/// <summary>当前一轮一个工具调用的呈现。对应 TS <c>ToolSlot</c>。</summary>
public sealed record ToolSlot
{
    public required string CallId { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// 尚未开始的调用（sequential 轮）与其请求未提供的调用缺省——
    /// 后者以 generation 写入的 entry 进入 <c>done</c>。
    /// </summary>
    public TaskId<object?>? TaskId { get; init; }

    /// <summary>"pending" / "running" / "done"。</summary>
    public required string Status { get; init; }

    /// <summary>保留的运行输出与边界丢弃的部分。</summary>
    public string? Output { get; init; }

    public long? DroppedBytes { get; init; }

    public long? DroppedLines { get; init; }

    /// <summary>最后一次 <c>details()</c> 的值。</summary>
    public object? Details { get; init; }

    /// <summary>经 <c>api.diagnostic()</c> 记录的诊断。</summary>
    public IReadOnlyList<ToolDiagnostic>? Diagnostics { get; init; }

    /// <summary>完成后为结果条目；工具任务 faulted 或 orphaned 时缺省。</summary>
    public EntryId? Entry { get; init; }

    /// <summary>一次对其请求未提供的工具的调用：结算 <c>tool_unavailable</c>，不解析名字。对应 TS <c>unoffered</c>。</summary>
    public bool? Unoffered { get; init; }

    public Dictionary<string, object?> ToJson()
    {
        var json = new Dictionary<string, object?>
        {
            ["callId"] = CallId,
            ["name"] = Name,
            ["status"] = Status,
        };
        if (TaskId is { } taskId) json["taskId"] = taskId.Value;
        if (Output is not null) json["output"] = Output;
        if (DroppedBytes is { } droppedBytes) json["droppedBytes"] = droppedBytes;
        if (DroppedLines is { } droppedLines) json["droppedLines"] = droppedLines;
        if (Details is not null) json["details"] = Details;
        if (Diagnostics is { } diagnostics)
        {
            json["diagnostics"] = diagnostics.Select(diagnostic =>
            {
                var entry = new Dictionary<string, object?> { ["severity"] = diagnostic.Severity, ["message"] = diagnostic.Message };
                if (diagnostic.Code is not null) entry["code"] = diagnostic.Code;
                return (object?)entry;
            }).ToList();
        }

        if (Entry is { } entry) json["entry"] = entry.Value;
        if (Unoffered == true) json["unoffered"] = true;
        return json;
    }

    public static ToolSlot FromJson(IReadOnlyDictionary<string, object?> json)
    {
        IReadOnlyList<ToolDiagnostic>? diagnostics = null;
        if (json.TryGetValue("diagnostics", out var diagnosticsValue)
            && diagnosticsValue is IReadOnlyList<object?> items)
        {
            diagnostics = items.OfType<IReadOnlyDictionary<string, object?>>().Select(item => new ToolDiagnostic
            {
                Severity = (string)item["severity"]!,
                Message = (string)item["message"]!,
                Code = item.TryGetValue("code", out var code) ? code as string : null,
            }).ToList();
        }

        return new ToolSlot
        {
            CallId = (string)json["callId"]!,
            Name = (string)json["name"]!,
            TaskId = json.TryGetValue("taskId", out var taskId) && taskId is not null
                ? TaskId<object?>.From(Convert.ToInt64(taskId))
                : null,
            Status = (string)json["status"]!,
            Output = json.TryGetValue("output", out var output) ? output as string : null,
            DroppedBytes = json.TryGetValue("droppedBytes", out var droppedBytes) && droppedBytes is not null
                ? Convert.ToInt64(droppedBytes)
                : null,
            DroppedLines = json.TryGetValue("droppedLines", out var droppedLines) && droppedLines is not null
                ? Convert.ToInt64(droppedLines)
                : null,
            Details = json.TryGetValue("details", out var details) ? details : null,
            Diagnostics = diagnostics,
            Entry = json.TryGetValue("entry", out var entry) && entry is not null
                ? EntryId.From(Convert.ToInt64(entry))
                : null,
            Unoffered = json.TryGetValue("unoffered", out var unoffered) && unoffered is true,
        };
    }
}

/// <summary>
/// 一次运行没有回答就结束的原因（<c>pi.live.runFailure.reason</c>）。对应 TS <c>RunFailureReason</c>。
/// </summary>
public static class RunFailureReasons
{
    /// <summary>模型的应答失败了（已重试过），或一次请求需要的压缩失败了。</summary>
    public const string ModelError = "model_error";

    /// <summary>没有配置模型，或配置的模型不可用。</summary>
    public const string NoModel = "no_model";

    /// <summary>运行被中止；<c>detail</c> 说明其 abort 处理器是否被切断 <c>settings.restartLimit</c> 次而没完成。</summary>
    public const string Aborted = "aborted";

    /// <summary>一次排队的 reset 在 postTools 边界起了新上下文，还没回答。</summary>
    public const string Reset = "reset";

    /// <summary>运行的任务在无法恢复处抛出，或存储拒绝了它的提交。</summary>
    public const string Faulted = "faulted";

    /// <summary>运行的任务被中止时，没有注册的定义能接手它。</summary>
    public const string Orphaned = "orphaned";

    /// <summary>宿主在模型回答期间停止，次数超过 <c>settings.retry</c> 的重试。</summary>
    public const string Interrupted = "interrupted";

    /// <summary>运行的生成被连续切断 <c>settings.restartLimit</c> 次；请求或轮询途中的切断先算一次失败尝试，
    /// 因此它结束的是「这次记账本身反复失败」的那种。</summary>
    public const string RestartLimit = "restart_limit";
}

/// <summary>一个活跃压缩任务的呈现（spec §8.7）。对应 TS <c>CompactionStatus</c>。</summary>
public sealed record CompactionStatus
{    public required TaskId<object?> TaskId { get; init; }

    public required CompactionReason Reason { get; init; }

    /// <summary>一次生成是否在等它：generation 自有的压缩。</summary>
    public required bool Blocking { get; init; }

    public required int Attempt { get; init; }

    /// <summary>下一次总结尝试前的持久化退避。</summary>
    public Retry? RetryBackoff { get; init; }

    /// <summary>对应 TS <c>retry: { at, error }</c>。</summary>
    public sealed record Retry(long At, string Error);

    public Dictionary<string, object?> ToJson()
    {
        var json = new Dictionary<string, object?>
        {
            ["taskId"] = TaskId.Value,
            ["reason"] = Reason.ToString().ToLowerInvariant(),
            ["blocking"] = Blocking,
            ["attempt"] = (long)Attempt,
        };
        if (RetryBackoff is { } retry) json["retry"] = new Dictionary<string, object?> { ["at"] = retry.At, ["error"] = retry.Error };
        return json;
    }

    public static CompactionStatus FromJson(IReadOnlyDictionary<string, object?> json)
    {
        Retry? retry = null;
        if (json.TryGetValue("retry", out var retryValue)
            && retryValue is IReadOnlyDictionary<string, object?> retryJson)
        {
            retry = new Retry(
                Convert.ToInt64(retryJson["at"]!),
                (string)retryJson["error"]!);
        }

        return new CompactionStatus
        {
            TaskId = TaskId<object?>.From(Convert.ToInt64(json["taskId"]!)),
            Reason = Enum.TryParse<CompactionReason>((string)json["reason"]!, ignoreCase: true, out var reason)
                ? reason
                : CompactionReason.Manual,
            Blocking = Convert.ToBoolean(json["blocking"]!),
            Attempt = Convert.ToInt32(json["attempt"]!),
            RetryBackoff = retry,
        };
    }
}

/// <summary>
/// 内建活跃对话状态：运行控制与当前生成、工具轮的呈现。对应 TS <c>harness/live.ts</c>
/// （值是 <c>pi.live</c> 文档的严格 JSON 形状；<c>settleSchedulerOutcome</c> 依赖
/// generation 的 <c>convertPartial</c> 与 scheduler 的 <c>SchedulerOutcome</c>，随
/// generation/scheduler 阶段接入）。
/// </summary>
public static class Live
{
    /// <summary>可拥有 <c>pi.live.run</c> 的内建任务种类。对应 TS <c>RUN_TASK_KINDS</c>。</summary>
    public static readonly IReadOnlySet<string> RunTaskKinds = new HashSet<string>(StringComparer.Ordinal) { "pi.generation" };

    public const string ToolTaskKind = "pi.tool";

    public const string CompactionTaskKind = "pi.compaction";

    /// <summary>
    /// 被切断 <c>settings.restartLimit</c> 次后自己收场（<see cref="TaskRecord.Restarts"/>）的内建任务种类；
    /// 其他种类的任务由调度器 faulted（spec §5.4）。对应 TS <c>LIMITS_RESTARTS</c>。
    /// </summary>
    public static readonly IReadOnlySet<string> LimitsRestarts =
        new HashSet<string>(StringComparer.Ordinal) { "pi.generation", ToolTaskKind, CompactionTaskKind };

    /// <summary>任务是否已被连续切断 <c>settings.restartLimit</c> 次，其阶段应当收场而不是再跑。</summary>
    public static bool CutOffTooOften(TaskRecord task, ITaskRuntime runtime)
        // 设置只对至少被切断过一次的任务读取，正常路径不多解析任何东西。
        => task.Restarts is { } restarts && restarts >= runtime.Settings.RestartLimit;

    /// <summary>
    /// 到达上限的任务是否还能做它收场时的清理（例如把 partial 转成 aborted 条目）：只在到达上限时。超过上限后，
    /// 那次清理本身可能就是切断它的原因，于是不做清理直接收场。对应 TS <c>salvageable()</c>。
    /// </summary>
    public static bool Salvageable(TaskRecord task, ITaskRuntime runtime)
        => (task.Restarts ?? 0) <= runtime.Settings.RestartLimit;

    /// <summary>任务被切断了几次，供消息使用：<c>once</c> 或 <c>3 times in a row</c>。</summary>
    public static string CutOffCount(TaskRecord task)
    {
        var restarts = task.Restarts ?? 0;
        return restarts == 1 ? "once" : $"{restarts} times in a row";
    }

    public static readonly DocToken<Dictionary<string, object?>> LiveDoc =
        DurableDocuments.DefineDoc(new DocDefinition<Dictionary<string, object?>>
        {
            Kind = "pi.live",
            Version = 1,
            Initial = () => new Dictionary<string, object?>(),
            // 提醒（spec §8.2）：没有任何东西在运行时存完整 base——无生成且无运行中的工具槽。
            // 这在空闲、把生成移交给工具轮的提交与轮内工具之间成立，使 delta 链至多跨一次
            // 生成或一轮工具的重叠执行。不要加 delta 计数上限；工具输出基准检查此规则。
            CheckpointWhen = (value, _, _) => !value.ContainsKey("generation") && !HasRunningTool(value),
            Semantics = new DocumentSemantics.LatestConversationScope(ConversationFork.Initial),
        });

    private static bool HasRunningTool(IReadOnlyDictionary<string, object?> value)
        => value.TryGetValue("tools", out var tools)
            && tools is IReadOnlyList<object?> list
            && list.Any(slot => slot is IReadOnlyDictionary<string, object?> slotJson
                && slotJson.TryGetValue("status", out var status)
                && status is "running");

    private static readonly Path RunPath = Path.Root.Append(Seg.Key("run"));
    private static readonly Path GenerationPath = Path.Root.Append(Seg.Key("generation"));
    private static readonly Path RunFailurePath = Path.Root.Append(Seg.Key("runFailure"));
    private static readonly Path ToolsPath = Path.Root.Append(Seg.Key("tools"));
    private static readonly Path CompactionsPath = Path.Root.Append(Seg.Key("compactions"));
    private static readonly Path CompactionFailurePath = Path.Root.Append(Seg.Key("compactionFailure"));

    // ─── 运行控制 ───────────────────────────────────────────────────────────

    /// <summary>把 <c>run</c> 设为 <paramref name="taskId"/> 持有 <paramref name="inputs"/>。对应 TS <c>live.run = {…}</c>。</summary>
    public static void SetRun(
        TxDocChange live, TaskId<object?> taskId, IReadOnlyList<SubmissionId> inputs, EntryId? first = null)
    {
        var run = new Dictionary<string, object?>
        {
            ["taskId"] = taskId.Value,
            // 必须显式构造 List<object?>：List<long> 因泛型不变性不被严格 JSON 容器判定接受（P56 修正）。
            ["inputs"] = inputs.Select(input => (object?)input.Value).ToList(),
            // 一次运行已走的工具轮数（AgentState.MaxTurns 按它计）。
            ["turns"] = 0L,
        };
        // 起它的边界放置的第一个条目；没有（例如 continue）就等第一个工具轮答案补上。
        if (first is { } firstEntry) run["first"] = firstEntry.Value;
        live.Set(RunPath, run);
    }

    /// <summary>
    /// 最近一次没有回答就结束的运行及其原因，写进结束它的那个提交。对应 TS <c>live.runFailure</c>。
    /// </summary>
    public static void SetRunFailure(
        TxDocChange live, TaskId<object?> taskId, IReadOnlyList<SubmissionId> inputs, string reason, object? detail)
    {
        var failure = new Dictionary<string, object?>
        {
            ["taskId"] = taskId.Value,
            // 必须显式构造 List<object?>：List<long> 不被严格 JSON 容器判定接受。
            ["inputs"] = inputs.Select(input => (object?)input.Value).ToList(),
            ["reason"] = reason,
        };
        if (detail is not null) failure["detail"] = detail;
        live.Set(RunFailurePath, failure);
    }

    /// <summary>清掉 <c>runFailure</c>：一次新的运行开始（除非它结束的那次失败要留着）。对应 TS <c>delete live.runFailure</c>。</summary>
    public static void DeleteRunFailure(TxDocChange live) => live.Delete(RunFailurePath);

    /// <summary>
    /// 运行已答但还在等 <c>afterRun</c> 钩子要来的任务：在 <c>pi.live.run</c> 上留一个标记（上游是
    /// <c>run.afterRun = {}</c>，创建后补 <c>tasks</c>），使对话保持忙碌。对应 TS <c>live.run.afterRun</c>。
    /// </summary>
    public static void SetRunAfterRun(TxDocChange live, IReadOnlyList<TaskId<object?>>? tasks = null)
    {
        if (!live.Draft.TryGetValue("run", out var value) || value is not IReadOnlyDictionary<string, object?> run)
        {
            return;
        }

        var afterRun = new Dictionary<string, object?>();
        if (tasks is not null)
        {
            afterRun["tasks"] = tasks.Select(task => (object?)task.Value).ToList();
        }

        live.Set(RunPath.Append(Seg.Key("afterRun")), afterRun);
    }

    /// <summary>运行已走的工具轮数；不是当前任务持有的运行时为 0。对应 TS <c>live.run.turns</c>。</summary>
    public static long RunTurns(TxDocChange live, TaskId<object?> taskId)
        => RunTaskId(live) == taskId
            && live.Draft.TryGetValue("run", out var value)
            && value is IReadOnlyDictionary<string, object?> run
            && run.TryGetValue("turns", out var turns)
            && turns is not null
                ? Convert.ToInt64(turns)
                : 0;

    /// <summary>把运行的轮数设为 <paramref name="turns"/>（续延把计数清零）。对应 TS <c>live.run.turns = n</c>。</summary>
    public static void SetRunTurns(TxDocChange live, long turns)
    {
        if (live.Draft.TryGetValue("run", out var value) && value is IReadOnlyDictionary<string, object?> run)
        {
            live.Set(RunPath.Append(Seg.Key("turns")), turns);
        }
    }

    /// <summary>当前持有 <c>run</c> 的任务（若存在）。对应 TS <c>live.run?.taskId</c>。</summary>
    public static TaskId<object?>? RunTaskId(TxDocChange live)
        => live.Draft.TryGetValue("run", out var value)
            && value is IReadOnlyDictionary<string, object?> run
            && run.TryGetValue("taskId", out var taskId)
            ? TaskId<object?>.From(Convert.ToInt64(taskId))
            : null;

    /// <summary>运行控制从 <paramref name="from"/> 移交 <paramref name="to"/>；输入随运行。对应 TS <c>handOver()</c>。</summary>
    public static void HandOver(TxDocChange live, TaskId<object?> from, TaskId<object?> to)
    {
        if (RunTaskId(live) != from) return;
        if (live.Draft.TryGetValue("run", out var value) && value is IReadOnlyDictionary<string, object?> run)
        {
            live.Set(RunPath, new Dictionary<string, object?>(run) { ["taskId"] = to.Value });
        }
    }

    /// <summary>运行第一个条目的记录：由它起的边界放置的第一个条目，或它第一个工具轮答案。对应 TS <c>live.run.first</c>。</summary>
    public static EntryId? RunFirst(TxDocChange live)
        => live.Draft.TryGetValue("run", out var value)
            && value is IReadOnlyDictionary<string, object?> run
            && run.TryGetValue("first", out var first)
            && first is not null
                ? EntryId.From(Convert.ToInt64(first))
                : null;

    /// <summary>尚未记录运行第一个条目时记下它（首个工具轮答案）。对应 TS <c>live.run.first ??= entry.id</c>。</summary>
    public static void SetRunFirstIfUnset(TxDocChange live, EntryId first)
    {
        if (live.Draft.TryGetValue("run", out var value)
            && value is IReadOnlyDictionary<string, object?> run
            && run.ContainsKey("first")
            && run["first"] is not null)
        {
            return;
        }

        if (value is IReadOnlyDictionary<string, object?>) live.Set(RunPath.Append(Seg.Key("first")), first.Value);
    }

    /// <summary>运行当前的输入提交。对应 TS <c>live.run.inputs</c>。</summary>
    public static IReadOnlyList<SubmissionId> RunInputs(TxDocChange live)
        => live.Draft.TryGetValue("run", out var value)
            && value is IReadOnlyDictionary<string, object?> run
            && run.TryGetValue("inputs", out var inputs)
            && inputs is IReadOnlyList<object?> list
                ? [.. list.Where(item => item is not null).Select(item => SubmissionId.From(Convert.ToInt64(item)))]
                : [];

    /// <summary>清空运行的 <c>inputs</c>：已回答的运行把它们结算 done 之后不再持有。对应 TS <c>run.inputs = []</c>。</summary>
    public static void ClearRunInputs(TxDocChange live)
    {
        if (live.Draft.TryGetValue("run", out var value) && value is IReadOnlyDictionary<string, object?>)
        {
            live.Set(RunPath.Append(Seg.Key("inputs")), new List<object?>());
        }
    }

    /// <summary>把输入提交并入运行的 <c>inputs</c>（一次 Splice）。对应 TS <c>live.run.inputs.push(…)</c>。</summary>
    public static void PushRunInputs(TxDocChange live, IEnumerable<SubmissionId> inputs)
    {
        if (live.Draft.TryGetValue("run", out var value) && value is IReadOnlyDictionary<string, object?> run
            && run.TryGetValue("inputs", out var inputsValue) && inputsValue is IReadOnlyList<object?> list)
        {
            live.Splice(
                RunPath.Append(Seg.Key("inputs")), list.Count, 0,
                inputs.Select(input => (object?)input.Value).ToList());
        }
    }

    /// <summary>设置 <c>generation</c>（整体 Set；TS 的字段变异由调用方以整值替换表达）。</summary>
    public static void SetGeneration(TxDocChange live, IReadOnlyDictionary<string, object?> generation)
        => live.Set(GenerationPath, generation);

    public static void DeleteGeneration(TxDocChange live) => live.Delete(GenerationPath);

    public static void DeleteTools(TxDocChange live) => live.Delete(ToolsPath);

    /// <summary>读取 <c>generation</c> 草稿（若有）。</summary>
    public static IReadOnlyDictionary<string, object?>? GenerationOf(TxDocChange live)
        => live.Draft.TryGetValue("generation", out var value)
            && value is IReadOnlyDictionary<string, object?> generation
            ? generation
            : null;

    /// <summary>读取当前工具槽列表（若列出）。对应 TS <c>live.tools</c>。</summary>
    public static IReadOnlyList<ToolSlot> ToolSlotsOf(TxDocChange live)
        => live.Draft.TryGetValue("tools", out var value) && value is IReadOnlyList<object?> list
            ? [.. list.OfType<IReadOnlyDictionary<string, object?>>().Select(ToolSlot.FromJson)]
            : [];

    /// <summary>整体写入工具槽列表。对应 TS <c>live.tools = slots</c>。</summary>
    public static void SetTools(TxDocChange live, IReadOnlyList<ToolSlot> slots)
        => live.Set(ToolsPath, slots.Select(slot => (object?)slot.ToJson()).ToList());

    /// <summary>
    /// 结束 <paramref name="taskId"/> 拥有的运行：结算其每个输入并移除 <c>run</code>。
    /// 总是移除 <c>generation</c> 与 <c>tools</c>——它们的呈现属于结束的运行。
    /// 对应 TS <c>endRun()</c>。
    /// </summary>
    public static void EndRun(Transaction tx, TxDocChange live, TaskId<object?> taskId, SubmissionSettlement settlement)
    {
        if (live.Draft.TryGetValue("run", out var runValue)
            && runValue is IReadOnlyDictionary<string, object?> run
            && run.TryGetValue("taskId", out var runTaskId)
            && Convert.ToInt64(runTaskId) == taskId.Value)
        {
            if (run.TryGetValue("inputs", out var inputsValue) && inputsValue is IReadOnlyList<object?> inputs)
            {
                var settledInputs = inputs.Select(input => SubmissionId.From(Convert.ToInt64(input))).ToList();
                foreach (var input in settledInputs) tx.SettleSubmission(input, settlement);
                // 没有回答就结束的运行留下原因，供运行结束后展示；开始新运行时清掉（见 StartRunAsync）。
                if (settlement is SubmissionSettlement.Unanswered unanswered)
                {
                    SetRunFailure(live, taskId, settledInputs, unanswered.Reason, unanswered.Detail);
                }
            }

            live.Delete(RunPath);
        }

        live.Delete(GenerationPath);
        live.Delete(ToolsPath);
    }

    // ─── 压缩状态 ───────────────────────────────────────────────────────────

    /// <summary>加入本提交创建的压缩任务的状态；状态保持任务 ID 序。对应 TS <c>addCompactionStatus()</c>。</summary>
    public static void AddCompactionStatus(TxDocChange live, CompactionStatus status)
    {
        if (live.Draft.TryGetValue("compactions", out var value) && value is IReadOnlyList<object?> list)
        {
            live.Splice(CompactionsPath, list.Count, 0, [status.ToJson()]);
        }
        else
        {
            live.Set(CompactionsPath, new List<object?> { status.ToJson() });
        }
    }

    /// <summary>压缩任务 <paramref name="taskId"/> 的状态（若已列出）。对应 TS <c>compactionStatus()</c>。</summary>
    public static CompactionStatus? FindCompactionStatus(TxDocChange live, TaskId<object?> taskId)
        => Compactions(live).FirstOrDefault(status => status.TaskId == taskId);

    /// <summary>移除压缩任务的状态；列表为空时移除列表本身。对应 TS <c>removeCompactionStatus()</c>。</summary>
    public static void RemoveCompactionStatus(TxDocChange live, TaskId<object?> taskId)
    {
        if (!live.Draft.TryGetValue("compactions", out var value) || value is not IReadOnlyList<object?> list) return;
        var index = list.OfType<IReadOnlyDictionary<string, object?>>()
            .ToList()
            .FindIndex(status => Convert.ToInt64(status["taskId"]!) == taskId.Value);
        if (index < 0) return;
        if (list.Count == 1) live.Delete(CompactionsPath);
        else live.Splice(CompactionsPath, index, 1, []);
    }

    /// <summary>
    /// 移除压缩任务 <paramref name="taskId"/> 的状态，并记下它失败了：最新的压缩失败留在
    /// <c>pi.live.compactionFailure</c>，供客户端在任务结束后显示。对应 TS <c>failCompactionStatus()</c>。
    /// </summary>
    public static void FailCompactionStatus(TxDocChange live, TaskId<object?> taskId, string error)
    {
        var status = FindCompactionStatus(live, taskId);
        if (status is null) return;
        live.Set(CompactionFailurePath, new Dictionary<string, object?>
        {
            ["taskId"] = status.TaskId.Value,
            ["trigger"] = status.Reason.ToString().ToLowerInvariant(),
            ["error"] = error,
        });
        RemoveCompactionStatus(live, taskId);
    }

    /// <summary>清掉 <c>compactionFailure</c>：产出了摘要的压缩清掉它。对应 TS <c>delete live.compactionFailure</c>。</summary>
    public static void DeleteCompactionFailure(TxDocChange live) => live.Delete(CompactionFailurePath);

    /// <summary>以整体 Set 写回压缩状态（TS 的字段原地变异在 C# 用显式写回表达）。</summary>
    public static void ReplaceCompactionStatus(TxDocChange live, CompactionStatus status)
    {
        if (!live.Draft.TryGetValue("compactions", out var value) || value is not IReadOnlyList<object?> list) return;
        var index = list.OfType<IReadOnlyDictionary<string, object?>>()
            .ToList()
            .FindIndex(entry => Convert.ToInt64(entry["taskId"]!) == status.TaskId.Value);
        if (index >= 0) live.Set(CompactionsPath.Append(Seg.Index(index)), status.ToJson());
    }

    // ─── 工具槽 ─────────────────────────────────────────────────────────────

    /// <summary>当前轮中工具任务 <paramref name="taskId"/> 的槽（若本轮仍列出它）。对应 TS <c>toolSlot()</c>。</summary>
    public static ToolSlot? ToolSlotOf(TxDocChange live, TaskId<object?> taskId)
        => ToolSlots(live).FirstOrDefault(slot => slot.TaskId == taskId);

    /// <summary>槽 <paramref name="slot"/> 在 <c>pi.live.tools</c> 列表中的下标（按 CallId + TaskId 匹配）。</summary>
    public static bool TrySlotIndex(TxDocChange live, ToolSlot slot, out int index)
        => TryFindSlotIndex(live, slot, out index);

    /// <summary><c>pi.live.tools</c> 列表中下标 <paramref name="index"/> 的槽路径（供逐叶 Set 使用）。</summary>
    public static Path SlotPath(int index) => ToolsPath.Append(Seg.Index(index));

    /// <summary>以整体 Set 写回槽（TS 的槽字段原地变异在 C# 用显式写回表达）。</summary>
    public static void ReplaceSlot(TxDocChange live, ToolSlot slot)
    {
        if (!TryFindSlotIndex(live, slot, out var index)) return;
        live.Set(ToolsPath.Append(Seg.Index(index)), slot.ToJson());
    }

    /// <summary>
    /// 标记槽完成：结果条目（若有）此刻携带其运行输出、details 与诊断。
    /// 对应 TS <c>finishSlot()</c>（C# record 不可变：以整体 Set 写回槽）。
    /// </summary>
    public static void FinishSlot(TxDocChange live, ToolSlot slot, EntryId? entry)
    {
        if (!TryFindSlotIndex(live, slot, out var index)) return;
        var finished = slot with { Status = "done", Entry = entry ?? slot.Entry };
        finished = ClearProgress(finished);
        live.Set(ToolsPath.Append(Seg.Index(index)), finished.ToJson());
    }

    /// <summary>移除工具运行期间发布的内容；其结果条目或重跑会替换它。对应 TS <c>clearProgress()</c>。</summary>
    public static ToolSlot ClearProgress(ToolSlot slot)
        => slot with { Output = null, DroppedBytes = null, DroppedLines = null, Details = null, Diagnostics = null };

    private static bool TryFindSlotIndex(TxDocChange live, ToolSlot slot, out int index)
    {
        index = -1;
        if (!live.Draft.TryGetValue("tools", out var value) || value is not IReadOnlyList<object?> list) return false;
        var jsons = list.OfType<IReadOnlyDictionary<string, object?>>().ToList();
        for (var candidate = 0; candidate < jsons.Count; candidate++)
        {
            var current = ToolSlot.FromJson(jsons[candidate]);
            if (current.CallId == slot.CallId && Equals(current.TaskId, slot.TaskId))
            {
                index = candidate;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ToolSlot> ToolSlots(TxDocChange live)
        => live.Draft.TryGetValue("tools", out var value) && value is IReadOnlyList<object?> list
            ? [.. list.OfType<IReadOnlyDictionary<string, object?>>().Select(ToolSlot.FromJson)]
            : [];

    private static IReadOnlyList<CompactionStatus> Compactions(TxDocChange live)
        => live.Draft.TryGetValue("compactions", out var value) && value is IReadOnlyList<object?> list
            ? [.. list.OfType<IReadOnlyDictionary<string, object?>>().Select(CompactionStatus.FromJson)]
            : [];
}
