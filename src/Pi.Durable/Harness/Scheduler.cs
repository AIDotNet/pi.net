using Pi.Ai.Models;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Env;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using JsonDict = IReadOnlyDictionary<string, object?>;
using Json = Pi.Chord.Json;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using Watch = Pi.Chord.Services.IWatchHandle<IReadOnlyDictionary<string, object?>?>;
using BclTaskScheduler = System.Threading.Tasks.TaskScheduler;

/// <summary>
/// 调度器的依赖与策略（全部以委托承载）。对应 TS <c>TaskSchedulerOptions</c>。
/// </summary>
public sealed record TaskSchedulerOptions
{
    public required DurableSession Session { get; init; }

    public required IStorage Storage { get; init; }

    public required IRegistryReader Registry { get; init; }

    public required Models Models { get; init; }

    /// <summary>对照注册表快照解析一个对话的 agent；运行时每阶段至多调用一次。</summary>
    public required Func<ConversationId, IRegistrySnapshot, Context, Task<Agent>> Agent { get; init; }

    /// <summary>解析设置；每次访问时读取。</summary>
    public required Func<Settings> Settings { get; init; }

    /// <summary>以 <c>HarnessOptions.env</c> 构建一个对话的环境。</summary>
    public required Func<ConversationId, Context, Task<IExecutionEnv?>> Env { get; init; }

    public required Func<long> Now { get; init; }

    /// <summary>
    /// 关闭时把 <c>writes()</c> 直接写入 Storage（Session 的 <c>writeAtClose</c>）：关闭不是崩溃，
    /// 被它止住的任务写回 <c>pending</c>，于是下次 open 不为它计重启。对应 TS <c>TaskSchedulerOptions.writeAtClose</c>。
    /// </summary>
    public required Func<Func<IReadOnlyList<StorageWrite>>, Task> WriteAtClose { get; init; }

    /// <summary><c>HarnessOptions.onWake</c>：替代睡眠任务的定时器。</summary>
    public Action<Wake>? OnWake { get; init; }

    /// <summary>宿主的定时器（<c>HarnessOptions.setTimer</c>）。</summary>
    public required Timers Timers { get; init; }

    /// <summary>上报一次不使调用失败的失败，附带说明其来源的 <see cref="ReportInfo"/>。</summary>
    public required Action<Exception, ReportInfo> Report { get; init; }

    /// <summary>使调度器写入的终局落定的 Harness 清理（与终态同一提交）。</summary>
    public required Func<Transaction, TaskRecord, SchedulerOutcome, Task> SettleOutcome { get; init; }

    /// <summary>撤回一个对话的排队输入；用于对话中止与中止级联。</summary>
    public required Func<Transaction, ConversationId, Task> WithdrawInputs { get; init; }

    /// <summary>既有对话的调用内句柄；供任务运行时与工具使用。</summary>
    public required Func<ConversationId, InvocationBinding, Context, Task<IConversationHandle?>> Conversation { get; init; }

    /// <summary>调度器提交与调用共用的上下文；不携带调用方取消。</summary>
    public required Context Context { get; init; }
}

/// <summary>调度器的时点视图（Harness 检查的调度部分）。对应 TS <c>inspect()</c> 的返回形状。</summary>
public sealed record SchedulerInspection(string Scheduling, IReadOnlyList<TaskInspection> Tasks);

/// <summary>
/// 一个 Harness 的持久任务调度器。
///
/// <c>TaskScheduler._live</c> 镜像每个已提交的非终态任务记录：pending、running、waiting 与 completing。
/// 同步提交监听器在 Session 线上更新它，因此在线上运行的代码读到的恰是已提交状态。
///
/// 任务与对话构成一棵所有权树（spec §5.5）：任务的父是其属主任务或其对话；对话的父是其属主任务（若有）。
/// 沿树向上行走决定级联、空闲范围，以及一个任务的普通自有工作是否存活——存活时其结果保持
/// <c>completing</c> 并延迟其 abort 处理器。
///
/// 不变量：每个任务转移都由一个在 Session 线上串行化的回调决定并写入。覆盖保留、标记、运行时提交、
/// 终结，以及每个阶段之前的同步 step（它应用优先级规则并写入 fault 或交接）。处理器与 join 在线下运行。
/// 调用在决定其结束的 step 内结束，因此它排队的运行时提交要么落在该决定之前，要么被拒绝。
/// 对应 TS <c>harness/scheduler.ts</c> 的 <c>TaskScheduler</c> 全量。
/// </summary>
public sealed class TaskScheduler
{
    private const int ScanPageSize = 256;

    private static readonly TaskStatus[] LiveStatuses =
    [
        TaskStatus.Pending, TaskStatus.Running, TaskStatus.Waiting, TaskStatus.Sleeping, TaskStatus.Completing,
    ];

    private static readonly ReportInfo RegistryError = new ReportInfo.HostError(HostCallbacks.Registry);

    private static readonly ReportInfo SettingsError = new ReportInfo.HostError(HostCallbacks.Settings);

    private static readonly ReportInfo OnWakeError = new ReportInfo.HostError(HostCallbacks.OnWake);

    private static readonly ReportInfo ListenerFailure = new ReportInfo.ListenerError();

    private readonly DurableSession _session;
    private readonly IStorage _storage;
    private readonly IRegistryReader _registry;
    private readonly Models _models;
    private readonly Func<ConversationId, IRegistrySnapshot, Context, Task<Agent>> _agent;
    private readonly Func<Settings> _settings;
    private readonly Func<ConversationId, Context, Task<IExecutionEnv?>> _env;
    private readonly Func<long> _now;
    private readonly Func<Func<IReadOnlyList<StorageWrite>>, Task> _writeAtClose;
    private readonly Action<Wake>? _onWake;
    private readonly Timers _timers;
    private readonly Action<Exception, ReportInfo> _report;
    private readonly Func<Transaction, TaskRecord, SchedulerOutcome, Task> _settleOutcome;
    private readonly Func<Transaction, ConversationId, Task> _withdrawInputs;
    private readonly Func<ConversationId, InvocationBinding, Context, Task<IConversationHandle?>> _conversation;
    private readonly Context _context;

    private readonly Dictionary<TaskId<object?>, TaskRecord> _live = [];
    private readonly Dictionary<TaskId<object?>, Invocation> _invocations = [];
    private sealed record KeptContext(ConversationContextRange Range, long? IdleSince);

    private readonly object _contextsGate = new();
    private readonly Dictionary<ConversationId, KeptContext> _contexts = [];

    /// <summary>最早空闲上下文过期的背景定时器解除函数；宿主拒绝时为 null（见 <see cref="ScheduleContextExpiryLocked"/>）。</summary>
    private Action? _contextExpiryDisarm;

    private double? _contextExpiryAt;

    private readonly Waiters<TaskId<object?>, SettledTask> _taskWaiters = new();

    /// <summary>按对话组织的空闲等待者；<see cref="IdleKey.ConversationId"/> 为 null 时等待整个 Harness。</summary>
    private readonly Waiters<IdleKey, object?> _idleWaiters = new();

    /// <summary>每个任务迁移失败的定义；仅在注册表解析出另一条定义时重试。</summary>
    private readonly Dictionary<TaskId<object?>, (AnyDurableTask Task, Exception Error)> _failedMigrations = [];

    /// <summary>每个已加载对话的属主任务；null 表示无主。</summary>
    private readonly Dictionary<ConversationId, TaskId<object?>?> _edges = [];

    /// <summary>拥有已加载对话的任务；其节点在终态后留在 <see cref="_settled"/>。</summary>
    private readonly HashSet<TaskId<object?>> _conversationOwners = [];

    /// <summary>行走经过的终态任务的所有权字段。</summary>
    private readonly Dictionary<TaskId<object?>, TaskNode> _settled = [];

    /// <summary>
    /// 下一次 reconcile 检查的 <c>failFast</c> 等待者：开启时、开始等待时、以及其中一个任务失败时。
    /// </summary>
    private readonly HashSet<TaskId<object?>> _failFastChecks = [];

    /// <summary>一遍调度作为停放任务略过的任务；每一遍都再检查，因为 <c>settings.restartLimit</c> 可能已上调。</summary>
    private readonly HashSet<TaskId<object?>> _parkedTasks = [];

    /// <summary>阻塞原因可能已在不提交的情况下改变的监听器：注册表变化、迁移失败、取消停放。</summary>
    private readonly List<Action> _blockedListeners = [];

    /// <summary>崩溃后被放弃（<c>TaskOptions.AbandonOnRestart</c>）的任务：首遍调度只打 <c>restart</c> 标记。</summary>
    private readonly HashSet<TaskId<object?>> _abandoned = [];

    /// <summary>注册表快照失败已经上报过：每个任务每次查询都上报会淹没宿主。</summary>
    private bool _snapshotReported;

    /// <summary>最早尚未到点的睡眠时刻，-1 表示没有；在线上随提交重算，故在线外读也安全。</summary>
    private long _earliestSleep = -1;

    /// <summary>最早睡眠的定时器；有 <c>onWake</c> 时不布设。</summary>
    private (long At, Action Disarm)? _sleepTimer;

    /// <summary>
    /// 保护 <see cref="_sleepTimer"/> 的读取与布设。上游是单线程事件循环，<c>#scheduleWake</c> 的
    /// 函数体一次跑完、不会被插入；端口把它丢进 <c>Task.Run</c>（见 <see cref="ScheduleWake"/>），
    /// 于是「读旧定时器 → 拆掉 → 布新的」可能被两个线程交错执行：两边都看到还没有定时器，
    /// 各布一个，先布的那个再没人拆，一次睡眠于是有两个 500ms 定时器同时活着（重复唤醒）。
    /// </summary>
    private readonly object _sleepTimerGate = new();

    /// <summary>上一次交给 <c>onWake</c> 的值，未变化时不重复。</summary>
    private Wake? _lastWake;

    private bool _wakeScheduled;

    private bool _reconcileScheduled;
    private bool _cascadePending;
    private IDisposable _unsubscribeRegistry = new NopDisposable();
    private bool _enabled;
    private bool _closing;
    private bool _dirty;
    private bool _draining;

    /// <summary>封印（close 开始）时取消的调用快照；关闭要把它们跑过的任务写回 <c>pending</c>。</summary>
    private List<Invocation>? _sealed;

    public TaskScheduler(TaskSchedulerOptions options)
    {
        _session = options.Session;
        _storage = options.Storage;
        _registry = options.Registry;
        _models = options.Models;
        _agent = options.Agent;
        _settings = options.Settings;
        _env = options.Env;
        _now = options.Now;
        _writeAtClose = options.WriteAtClose;
        _onWake = options.OnWake;
        _timers = options.Timers;
        _report = options.Report;
        _settleOutcome = options.SettleOutcome;
        _withdrawInputs = options.WithdrawInputs;
        _conversation = options.Conversation;
        _context = options.Context;
    }

    // ─── 生命周期 ───────────────────────────────────────────────────────────

    /// <summary>加载存活任务，并把幸存的 <c>running</c> 任务改回 <c>pending</c>。不派发任何工作。</summary>
    public async Task OpenAsync(Context context)
    {
        _session.SubscribeCommits(Observe);
        _session.SubscribeClose(Seal);
        _unsubscribeRegistry = _registry.Subscribe(() =>
        {
            NotifyBlocked();
            Kick();
        });
        var limit = RestartLimit();
        var parked = new List<TaskRecord>();
        await _session.CommitWithAsync<object?>(async tx =>
        {
            // 每张表读都在首次写之前。
            var scans = new List<IReadOnlyList<TaskRecord>>();
            foreach (var status in LiveStatuses)
            {
                scans.Add(await HarnessUtil.ScanAllAsync(cursor => tx.ScanTasksAsync(
                    new TaskQuery { Status = status }, ScanPageSize, cursor)).ConfigureAwait(false));
            }

            foreach (var records in scans)
            {
                foreach (var record in records)
                {
                    _live[record.Id] = record;
                    if (record.AbandonOnRestart) _abandoned.Add(record.Id);
                    if (record.State.Status == TaskStatus.Running)
                    {
                        // 幸存的 running 记录意味着宿主在它运行期间死亡：计一次重启。
                        var restarts = (record.Restarts ?? 0) + 1;
                        tx.SetTask(record with
                        {
                            State = new TaskState { Status = TaskStatus.Pending, Checkpoint = record.State.Checkpoint },
                            Restarts = restarts,
                        });
                        if (restarts >= limit && !record.AbortRequested) parked.Add(record);
                    }

                    if (record.State is { Status: TaskStatus.Waiting, Policy: JoinPolicy.FailFast })
                    {
                        _failFastChecks.Add(record.Id);
                    }
                }
            }

            RefreshEarliestSleep();
            return null;
        }, context).ConfigureAwait(false);

        foreach (var record in parked)
        {
            var restarts = (record.Restarts ?? 0) + 1;
            _report(
                new InvalidOperationException(
                    $"Task {record.Id.Value} ({record.Kind}) was cut off {restarts} times without progress; "
                    + "it is parked until retryTask()"),
                new ReportInfo.TaskParked(ReportTaskOf(record), InstallerOf(record)));
        }

        // 补派崩溃遗留的、低于已取消属主的 abort 标记，并终结保持的结果。
        _cascadePending = true;
        ScheduleReconcile();
        ScheduleWake();
    }

    /// <summary>启用调度，并让到点的睡眠任务可运行：宿主唤醒（如 alarm）调用它。幂等；关闭后 kick 无效。</summary>
    public void Resume()
    {
        _enabled = true;
        WakeDue();
        Kick();
    }

    /// <summary>
    /// Session 下一次需要进程的时刻：有任务现在就能跑或正在跑时 <see cref="Wake.Active"/>，
    /// 另有尚未到点的最早睡眠时给出 <see cref="Wake.At"/>。同步读自内存；链尚未加载的任务按“只有它自己”计。
    /// </summary>
    public Wake Wake()
    {
        try
        {
            return new Wake { Active = ComputeActive(), At = ComputeEarliestSleep() };
        }
        catch (InvalidOperationException)
        {
            // 与提交并发时保守地当作有工作：宿主会再唤醒一次，届时重算。
            return new Wake { Active = true, At = EarliestSleep() };
        }
    }

    /// <summary>尚未到点的最早睡眠；读自存活记录。</summary>
    private long? ComputeEarliestSleep()
    {
        var now = _now();
        long? at = null;
        foreach (var record in _live.Values)
        {
            if (record.State.Status != TaskStatus.Sleeping || record.AbortRequested) continue;
            var until = record.State.Until ?? 0;
            if (until <= now) continue;
            at = at is null ? until : Math.Min(at.Value, until);
        }

        return at;
    }

    private bool ComputeActive()
    {
        if (_invocations.Count > 0) return true;
        IRegistrySnapshot? snapshot = null;
        var snapshotFailed = false;
        foreach (var record in _live.Values)
        {
            var state = record.State;
            if (state.Status == TaskStatus.Completing) continue;
            if (state.Status == TaskStatus.Waiting && !record.AbortRequested
                && (state.On ?? []).Any(id => _live.ContainsKey(id)))
            {
                continue;
            }

            // 尚未到点的睡眠：它的定时器会唤醒，本身不算“现在能跑”。
            if (NotDue(record)) continue;
            // 停放的任务不需要进程：它要等 retry() 或上调的上限。
            if (Parked(record)) continue;
            // abort 调用等待其下的存活工作，那份工作会自己计入。
            if (record.AbortRequested && WaitingOn(record, OwnedLive()).Count > 0) continue;
            // 没有定义能接手的任务在等注册表变化，带 abort 标记的除外（它会被 orphaned）。
            if (!record.AbortRequested)
            {
                if (snapshot is null && !snapshotFailed)
                {
                    try
                    {
                        snapshot = _registry.Snapshot();
                    }
                    catch (Exception error)
                    {
                        snapshotFailed = true;
                        _report(error, RegistryError);
                    }
                }

                if (snapshot is not null && FitOf(record, snapshot.Task(record.Kind)) is Fit.No) continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>尚未到点的最早睡眠；没有时为 null。读自线上维护的字段，可在线外调用。</summary>
    private long? EarliestSleep()
    {
        var at = Interlocked.Read(ref _earliestSleep);
        return at < 0 ? null : at;
    }

    /// <summary>在线上重算最早睡眠，供 <see cref="Wake"/> 的降级路径与定时器使用。</summary>
    private void RefreshEarliestSleep()
        => Interlocked.Exchange(ref _earliestSleep, ComputeEarliestSleep() ?? -1);

    /// <summary>
    /// 给每个调用发信号并等待它们，然后把它们跑过的任务写回 <c>pending</c>（<c>writeAtClose</c>）：
    /// 关闭不是崩溃。写回发生在本调用的线上，因此它保留了关闭前已受理的提交（例如 abort 标记）。
    /// </summary>
    public async Task JoinAsync()
    {
        // 封印时已取消的调用快照：Cancel 之后它们的阶段可能立刻结束并从 _invocations 消失，
        // 但关闭仍须把其跑过的任务写回 pending。对应 TS seal 与 join 在同一同步块内完成。
        var stopped = _sealed ?? _invocations.Values.ToList();
        foreach (var invocation in stopped) invocation.Controller.Cancel();
        if (stopped.Count == 0) return;
        try
        {
            await Task.WhenAll(stopped.Select(invocation => invocation.Done)).ConfigureAwait(false);
        }
        catch
        {
            // Promise.allSettled：吞掉所有结局。
        }

        try
        {
            await _writeAtClose(() =>
            {
                var writes = new List<StorageWrite>();
                foreach (var invocation in stopped)
                {
                    var record = _live.GetValueOrDefault(invocation.TaskId);
                    if (record is null || record.State.Status != TaskStatus.Running) continue;
                    writes.Add(new StorageWrite.Task(WithState(record, new TaskState
                    {
                        Status = TaskStatus.Pending,
                        Checkpoint = record.State.Checkpoint,
                    })));
                }

                return writes;
            }).ConfigureAwait(false);
        }
        catch
        {
            // 写回失败时记录留在 running，按被切断计；Storage 守卫会使 Session 失败并上报。
        }
    }

    /// <summary>
    /// 写入 abort 标记；或当没有已注册定义能接手且其拥有的工作无存活时以 <c>orphaned</c> 结算，
    /// 然后加入在线上看到的 run 调用（提交监听器已发信号）。abort 调用在该任务的普通自有工作消失后启动。
    /// <c>completing</c> 任务只被标记。中止请求替换 <c>restart</c> 标记，因此等待定义的、崩溃后放弃的任务
    /// 会被 orphaned；带 <paramref name="keepRestart"/>（如属主自身清理）时它保留标记并继续等待。
    /// 返回 "marked" / "terminal"。
    /// </summary>
    public async Task<string> AbortAsync(TaskId<object?> id, Context context, bool keepRestart = false)
    {
        var (result, run) = await _session.CommitWithAsync(async tx =>
        {
            var current = await tx.GetTaskAsync(id).ConfigureAwait(false);
            if (current is null) throw new InvalidOperationException($"Task {id.Value} does not exist");
            if (current.State.Status == TaskStatus.Terminal) return ("terminal", (Invocation?)null);
            var invocation = _invocations.GetValueOrDefault(id);
            // 带 restart 标记的任务没有 run 调用；它只等自己的 abort。
            if (keepRestart && current.AbortReason == RestartReason) return ("marked", (Invocation?)null);
            if (invocation is null && current.State.Status != TaskStatus.Completing)
            {
                await LoadScopesAsync(false).ConfigureAwait(false);
                if (!OwnedLive().ContainsKey(id))
                {
                    var resolution = Resolve(current, _registry.Snapshot());
                    if (resolution is Resolution.Blocked blocked)
                    {
                        await TerminateAsync(tx, current, SchedulerOutcome.Orphaned(blocked.Reason))
                            .ConfigureAwait(false);
                        return ("marked", (Invocation?)null);
                    }
                }
            }

            if (!current.AbortRequested || current.AbortReason is not null) tx.SetTask(WithAbortMark(current));
            return ("marked", invocation is { Mode: "run" } ? invocation : null);
        }, context).ConfigureAwait(false);

        // 提交监听器已发信号给 run；加入它。
        if (run is { } runInvocation)
        {
            await ContextSignals.AwaitWithContext(runInvocation.Done, context).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// 重跑被重启上限停放的任务：清除其重启计数并启用调度。未停放的任务什么也不做；未知任务拒绝。
    /// </summary>
    public async Task RetryAsync(TaskId<object?> id, Context context)
    {
        await _session.CommitWithAsync<object?>(async tx =>
        {
            var current = await tx.GetTaskAsync(id).ConfigureAwait(false);
            if (current is null) throw new InvalidOperationException($"Task {id.Value} does not exist");
            var status = current.State.Status;
            if (status is TaskStatus.Terminal or TaskStatus.Completing || !Parked(current)) return null;
            tx.SetTask(Progressed(current));
            return null;
        }, context).ConfigureAwait(false);
        Resume();
    }

    /// <summary>以终态回执完成；检查与注册同在线上，使终态发布不落在两者之间。</summary>
    public async Task<SettledTask> WaitForTaskAsync(TaskId<object?> id, Context context)
    {
        var found = await _session.ReadOnLineAsync(async () =>
        {
            if (_closing) throw HarnessUtil.ClosedError();
            if (_live.ContainsKey(id)) return _taskWaiters.Add(id, context);
            var record = await _storage.GetTaskAsync(id).ConfigureAwait(false);
            if (record is null) throw new InvalidOperationException($"Task {id.Value} does not exist");
            return Task.FromResult(new SettledTask(record));
        }).ConfigureAwait(false);
        return await found.ConfigureAwait(false);
    }

    /// <summary>
    /// 当从该对话、或从每个无主对话出发的普通遍历不再触达存活的非后台任务时完成。
    /// </summary>
    public Task WaitForIdleAsync(ConversationId? conversationId, Context context)
    {
        if (_closing) return Task.FromException(HarnessUtil.ClosedError());
        if (Idle(conversationId)) return Task.CompletedTask;
        ScheduleReconcile();
        return _idleWaiters.Add(new IdleKey(conversationId), context);
    }

    /// <summary>
    /// <c>Conversation.abort()</c>：一次提交内撤回排队输入，并标记从该对话出发的普通遍历触达的每个
    /// 存活非后台任务；范围空闲时完成。带 <paramref name="background"/> 时遍历跨过后台边界，
    /// 等待也覆盖它触达的每个任务。
    /// </summary>
    public async Task AbortConversationAsync(ConversationId conversationId, bool background, Context context)
    {
        var reached = await _session.CommitWithAsync(async tx =>
        {
            var queued = await LoadScopesAsync(true).ConfigureAwait(false);
            var scope = new Scope.OfConversation(conversationId);
            var reachedIds = new List<TaskId<object?>>();
            foreach (var record in _live.Values)
            {
                if (record.Background && !background) continue;
                if (InScope(ParentOf(record), scope, background) != true) continue;
                reachedIds.Add(record.Id);
                if (!record.AbortRequested || record.AbortReason is not null) tx.SetTask(WithAbortMark(record));
            }

            foreach (var id in queued)
            {
                if (InScope(new Up.OfConversation(id), scope, background) == true)
                {
                    await _withdrawInputs(tx, id).ConfigureAwait(false);
                }
            }

            return reachedIds;
        }, context).ConfigureAwait(false);

        if (background)
        {
            foreach (var id in reached) await WaitForTaskAsync(id, context).ConfigureAwait(false);
        }

        await WaitForIdleAsync(conversationId, context).ConfigureAwait(false);
    }

    // ─── 调度 ───────────────────────────────────────────────────────────────

    private void Observe(CommitPublication publication, Context context)
    {
        var updated = new List<TaskRecord>();
        var failed = new List<TaskId<object?>>();
        var changed = false;
        foreach (var change in publication.Changes)
        {
            if (change is not CommitChange.TaskTable { Value: { } record }) continue;
            changed = true;
            var previous = _live.GetValueOrDefault(record.Id);
            if (FailedOutcome(record) && (previous is null || !FailedOutcome(previous))) failed.Add(record.Id);
            if (record.State.Status == TaskStatus.Terminal)
            {
                _live.Remove(record.Id);
                _failedMigrations.Remove(record.Id);
                _failFastChecks.Remove(record.Id);
                if (_conversationOwners.Contains(record.Id)) _settled[record.Id] = NodeOf(record);
                _taskWaiters.Resolve(record.Id, new SettledTask(record));
                // 其属主现在可能可以终结。
                ScheduleReconcile();
                continue;
            }

            if (record.AbortRequested && previous?.AbortRequested != true)
            {
                _cascadePending = true;
                // 给新标记任务的 run 调用发信号；其下一个 step 结束它。
                if (_invocations.GetValueOrDefault(record.Id) is { Mode: "run" } invocation)
                {
                    invocation.Controller.Cancel();
                }
            }

            var status = record.State.Status;
            if (status == TaskStatus.Completing && previous?.State.Status != TaskStatus.Completing)
            {
                if (CancellationIntent(record)) _cascadePending = true;
                ScheduleReconcile();
            }

            if (status == TaskStatus.Waiting && record.State.Policy == JoinPolicy.FailFast
                && previous?.State.Status != TaskStatus.Waiting)
            {
                _failFastChecks.Add(record.Id);
                ScheduleReconcile();
            }

            _live[record.Id] = record;
            updated.Add(record);
        }

        foreach (var id in failed)
        {
            foreach (var record in _live.Values)
            {
                if (record.State is { Status: TaskStatus.Waiting, Policy: JoinPolicy.FailFast, On: { } on }
                    && on.Contains(id))
                {
                    _failFastChecks.Add(record.Id);
                    ScheduleReconcile();
                }
            }
        }

        foreach (var change in publication.Changes)
        {
            if (change is CommitChange.ConversationTable { Value: { } conversation }
                && !_edges.ContainsKey(conversation.Id))
            {
                SetEdge(conversation.Id, conversation.Owner?.TaskId);
            }
        }

        foreach (var change in publication.Changes)
        {
            // 低于已取消属主的排队输入被撤回，即使其级联已过。
            if (change is CommitChange.SubmissionTable
                {
                    Value: SubmissionRecord.InputRecord { Status: SubmissionStatus.Queued } input,
                })
            {
                var up = new Up.OfConversation(input.ConversationId);
                if (!ChainKnown(up) || BelowCancelled(up)) _cascadePending = true;
            }
        }

        foreach (var record in updated)
        {
            // 低于已取消属主创建的工作，即使其级联已过，也要中止。
            if (!ChainKnown(ParentOf(record))) ScheduleReconcile();
            else if (!record.Background && !record.AbortRequested && BelowCancelled(ParentOf(record)))
            {
                _cascadePending = true;
            }
        }

        // 级联提交失败时，也随下一次任意提交重试。
        if (_cascadePending) ScheduleReconcile();
        if (!changed) return;
        RefreshEarliestSleep();
        ResolveIdleWaiters();
        SettleContextCache();
        Kick();
    }

    private void ResolveIdleWaiters()
    {
        foreach (var key in _idleWaiters.Keys())
        {
            if (Idle(key.ConversationId)) _idleWaiters.Resolve(key, null);
        }
    }

    private double ContextRetentionMs()
    {
        try
        {
            return _settings().ContextRetentionMs;
        }
        catch (Exception error)
        {
            _report(error, SettingsError);
            return 0;
        }
    }

    private void SettleContextCache()
    {
        lock (_contextsGate)
        {
            if (_closing) return;
            var now = _now();
            var retention = ContextRetentionMs();
            foreach (var (conversationId, kept) in _contexts.ToArray())
            {
                if (kept.IdleSince is { } idleSince
                    && (double)now - idleSince >= retention)
                {
                    _contexts.Remove(conversationId);
                }
                else if (!Idle(conversationId))
                {
                    _contexts[conversationId] = kept with { IdleSince = null };
                }
                else if (retention > 0)
                {
                    _contexts[conversationId] = kept with { IdleSince = kept.IdleSince ?? now };
                }
                else
                {
                    _contexts.Remove(conversationId);
                }
            }

            ScheduleContextExpiryLocked();
        }
    }

    private void ScheduleContextExpiryLocked()
    {
        var retention = ContextRetentionMs();
        double? at = null;
        if (retention > 0)
        {
            foreach (var kept in _contexts.Values)
            {
                if (kept.IdleSince is not { } idleSince) continue;
                var deadline = AddRetention(idleSince, retention);
                if (at is null || deadline < at.Value) at = deadline;
            }
        }

        if (_contextExpiryDisarm is not null && _contextExpiryAt == at) return;
        CancelContextExpiryLocked();
        if (at is null || _closing) return;

        var delay = (long)Math.Max(0, Math.Ceiling(at.Value - _now()));
        var disarm = _timers.Background(() => ExpireContextCache(at.Value), delay);
        // 宿主拒绝背景定时器时不布设：后续提交与任务变化仍会检查过期。
        if (disarm is null) return;
        _contextExpiryAt = at;
        _contextExpiryDisarm = disarm;
    }

    private void CancelContextExpiryLocked()
    {
        var disarm = _contextExpiryDisarm;
        _contextExpiryDisarm = null;
        _contextExpiryAt = null;
        disarm?.Invoke();
    }

    /// <summary>背景定时器触发：在线上结算空闲上下文；提前触发只重新布设。</summary>
    private void ExpireContextCache(double at)
    {
        lock (_contextsGate)
        {
            // 已被取消或重排：本次触发作废。
            if (_closing || _contextExpiryAt != at) return;
            _contextExpiryDisarm = null;
            _contextExpiryAt = null;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _session.ReadOnLineAsync(() =>
                {
                    if (_closing) return Task.FromResult(false);
                    SettleContextCache();
                    return Task.FromResult(true);
                }).ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (_closing)
            {
            }
            catch (Exception error)
            {
                if (!_closing) _report(error, ListenerFailure);
            }
        });
    }

    private static double AddRetention(long idleSince, double retention) => idleSince + retention;

    // ─── 所有权 ─────────────────────────────────────────────────────────────

    private void ScheduleReconcile()
    {
        if (_reconcileScheduled || _closing) return;
        _reconcileScheduled = true;
        _ = Task.Run(ReconcileAsync);
    }

    /// <summary>
    /// 一次提交，应用已提交记录所蕴含的内容：存活属主带取消意图之下的 abort 标记（spec §5.4）、
    /// <c>failFast</c> 标记（spec §5.5）、低于已取消属主的排队输入撤回，以及每个普通自有工作已消失的
    /// <c>completing</c> 任务的最终终态记录。持久记录即意图，因此这也修复崩溃遗留的未应用状态。
    /// 解析已加载边决定的空闲等待者。
    /// </summary>
    private async Task ReconcileAsync()
    {
        _reconcileScheduled = false;
        var cascade = _cascadePending;
        _cascadePending = false;
        var checks = _failFastChecks.ToList();
        _failFastChecks.Clear();
        try
        {
            await _session.CommitWithAsync<object?>(async tx =>
            {
                if (_closing) return null;
                var queued = await LoadScopesAsync(cascade).ConfigureAwait(false);
                var marked = new HashSet<TaskId<object?>>();
                void Mark(TaskRecord record)
                {
                    if (record.AbortRequested || !marked.Add(record.Id)) return;
                    tx.SetTask(WithAbortMark(record));
                }

                // 加载边可能揭示一个已取消的属主，因此每次遍历都重新派生标记。
                foreach (var record in _live.Values)
                {
                    if (!record.Background && BelowCancelled(ParentOf(record))) Mark(record);
                }

                foreach (var id in checks)
                {
                    var waiter = _live.GetValueOrDefault(id);
                    if (waiter is not { State.Status: TaskStatus.Waiting }
                        || !await AnyFailedAsync(waiter.State.On ?? []).ConfigureAwait(false))
                    {
                        continue;
                    }

                    // 其余每个存活任务：失败的那个保留自己的结局。
                    foreach (var member in waiter.State.On ?? [])
                    {
                        var record = _live.GetValueOrDefault(member);
                        if (record is not null && !FailedOutcome(record)) Mark(record);
                    }
                }

                foreach (var id in queued)
                {
                    if (BelowCancelled(new Up.OfConversation(id)))
                    {
                        await _withdrawInputs(tx, id).ConfigureAwait(false);
                    }
                }

                await FinalizeAsync(tx).ConfigureAwait(false);
                return null;
            }, _context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // 任何一遍都可能暂存了标记，因此失败的一遍随下一次提交重试。
            _cascadePending = true;
            foreach (var id in checks) _failFastChecks.Add(id);
            if (!_closing) _report(error, ListenerFailure);
        }

        ResolveIdleWaiters();
    }

    /// <summary><paramref name="ids"/> 中是否有任务持有或以非 <c>completed</c> 的结局结束。</summary>
    private async Task<bool> AnyFailedAsync(IReadOnlyList<TaskId<object?>> ids)
    {
        foreach (var id in ids)
        {
            var record = _live.GetValueOrDefault(id) ?? await _storage.GetTaskAsync(id).ConfigureAwait(false);
            if (record is not null && FailedOutcome(record)) return true;
        }

        return false;
    }

    /// <summary>
    /// 为每个普通自有工作已消失的 <c>completing</c> 任务写入终态。终结一个任务可能释放其属主，
    /// 因此在本提交的候选上重复直至无变化。保持的调度器结局在此获得其 Harness 清理。
    /// </summary>
    private async Task FinalizeAsync(Transaction tx)
    {
        for (;;)
        {
            var overlay = OverlayOf(tx);
            var owned = OwnedLive(overlay);
            var done = LiveRecords(overlay)
                .Where(record => record.State.Status == TaskStatus.Completing && !owned.ContainsKey(record.Id))
                .ToList();
            if (done.Count == 0) return;
            foreach (var record in done)
            {
                var outcome = record.State.Outcome!;
                tx.SetTask(WithState(record, new TaskState { Status = TaskStatus.Terminal, Outcome = outcome }));
                // 只有调度器写入 faulted / orphaned（spec §5.4）；其清理等待本提交。
                if (outcome.Status is TaskOutcomeStatus.Faulted or TaskOutcomeStatus.Orphaned)
                {
                    await _settleOutcome(tx, record, SchedulerOutcome.From(outcome)).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// 在 Session 线上加载每个存活任务、以及（带 <paramref name="queued"/> 时）每个有排队提交的对话的
    /// 属主链；返回后者。直接读取已提交 Storage，因此可在提交回调内运行。
    /// </summary>
    private async Task<List<ConversationId>> LoadScopesAsync(bool queued)
    {
        foreach (var record in _live.Values.ToList())
        {
            if (!ChainKnown(ParentOf(record))) await LoadChainAsync(ParentOf(record)).ConfigureAwait(false);
        }

        if (!queued) return [];
        var submissions = await HarnessUtil.ScanAllAsync(cursor => _storage.ScanSubmissionsAsync(
            new SubmissionQuery { Status = SubmissionStatus.Queued }, ScanPageSize, cursor)).ConfigureAwait(false);
        var conversations = submissions.Select(submission => submission.ConversationId).Distinct().ToList();
        foreach (var id in conversations) await LoadChainAsync(new Up.OfConversation(id)).ConfigureAwait(false);
        return conversations;
    }

    /// <summary>从 <paramref name="start"/> 向上加载属主边与任务节点，直至无主根。</summary>
    private async Task LoadChainAsync(Up start, Overlay? overlay = null)
    {
        Up? at = start;
        while (at is not null)
        {
            if (at is Up.OfTask ofTask)
            {
                var node = Node(ofTask.TaskId, overlay);
                if (node is null)
                {
                    var record = await _storage.GetTaskAsync(ofTask.TaskId).ConfigureAwait(false);
                    if (record is null) return;
                    node = NodeOf(record);
                    if (record.State.Status == TaskStatus.Terminal) _settled[record.Id] = node;
                }

                at = ParentOf(node);
            }
            else
            {
                var conversationId = ((Up.OfConversation)at).ConversationId;
                var (found, owner) = Edge(conversationId, overlay);
                if (!found)
                {
                    var record = await _storage.GetConversationAsync(conversationId).ConfigureAwait(false);
                    owner = record?.Owner?.TaskId;
                    SetEdge(conversationId, owner);
                }

                at = owner is { } ownerTask ? new Up.OfTask(ownerTask) : null;
            }
        }
    }

    private void SetEdge(ConversationId conversationId, TaskId<object?>? owner)
    {
        _edges[conversationId] = owner;
        if (owner is { } ownerTask) _conversationOwners.Add(ownerTask);
    }

    /// <summary>一个对话的属主任务；null 为无主；<paramref name="found"/> 为 false 表示尚未加载。</summary>
    private (bool Found, TaskId<object?>? Owner) Edge(ConversationId id, Overlay? overlay)
    {
        if (overlay?.Edges.TryGetValue(id, out var overlayOwner) == true) return (true, overlayOwner);
        if (_edges.TryGetValue(id, out var owner)) return (true, owner);
        return (false, null);
    }

    private TaskNode? Node(TaskId<object?> id, Overlay? overlay)
    {
        if (overlay?.Tasks.TryGetValue(id, out var candidate) == true && candidate is not null) return NodeOf(candidate);
        if (_live.TryGetValue(id, out var live)) return NodeOf(live);
        return _settled.GetValueOrDefault(id);
    }

    /// <summary>从 <paramref name="start"/> 向上行走：属主任务与对话，止于无主根或尚未加载的边。</summary>
    private IEnumerable<Step> Above(Up start, Overlay? overlay = null)
    {
        Up? at = start;
        while (at is not null)
        {
            if (at is Up.OfTask ofTask)
            {
                var node = Node(ofTask.TaskId, overlay);
                if (node is null)
                {
                    yield return new Step.Unknown();
                    yield break;
                }

                yield return new Step.Owner(ofTask.TaskId, node);
                at = ParentOf(node);
            }
            else
            {
                var conversationId = ((Up.OfConversation)at).ConversationId;
                yield return new Step.OfConversation(conversationId);
                var (found, owner) = Edge(conversationId, overlay);
                if (!found)
                {
                    yield return new Step.Unknown();
                    yield break;
                }

                at = owner is { } ownerTask ? new Up.OfTask(ownerTask) : null;
            }
        }
    }

    /// <summary><paramref name="start"/> 之上的属主是否全部已加载。</summary>
    private bool ChainKnown(Up start, Overlay? overlay = null)
    {
        foreach (var step in Above(start, overlay))
        {
            if (step is Step.Unknown) return false;
        }

        return true;
    }

    /// <summary>存活记录；overlay 的候选替换已提交记录；终态候选消失。</summary>
    private IEnumerable<TaskRecord> LiveRecords(Overlay? overlay = null)
    {
        foreach (var record in _live.Values)
        {
            var candidate = overlay?.Tasks.GetValueOrDefault(record.Id) ?? record;
            if (candidate.State.Status != TaskStatus.Terminal) yield return candidate;
        }

        if (overlay is null) yield break;
        foreach (var record in overlay.Tasks.Values)
        {
            if (!_live.ContainsKey(record.Id) && record.State.Status != TaskStatus.Terminal)
            {
                yield return record;
            }
        }
    }

    /// <summary>
    /// 每个有存活普通自有工作的任务（spec §5.5），映射到该工作：每个存活的非后台任务为其上方、
    /// 直至并含首个后台属主的每个属主任务计数。属主链必须已加载。
    /// </summary>
    private Dictionary<TaskId<object?>, List<TaskId<object?>>> OwnedLive(Overlay? overlay = null)
    {
        var owned = new Dictionary<TaskId<object?>, List<TaskId<object?>>>();
        foreach (var record in LiveRecords(overlay))
        {
            if (record.Background) continue;
            foreach (var step in Above(ParentOf(record), overlay))
            {
                if (step is Step.Unknown) break;
                if (step is not Step.Owner ownerStep) continue;
                if (!owned.TryGetValue(ownerStep.TaskId, out var below))
                {
                    owned[ownerStep.TaskId] = below = [];
                }

                below.Add(record.Id);
                if (ownerStep.Node.Background) break;
            }
        }

        return owned;
    }

    /// <summary>
    /// 从 <paramref name="scope"/> 出发的普通遍历是否触达 <paramref name="start"/>：向上行走抵达范围的
    /// 对话（或 <c>roots</c> 的某个无主对话），且不跨过后台属主——除非 <paramref name="crossBackground"/>。
    /// 有边未加载时为 null。
    /// </summary>
    private bool? InScope(Up start, Scope scope, bool crossBackground = false)
    {
        foreach (var step in Above(start))
        {
            if (step is Step.Unknown) return null;
            if (step is Step.OfConversation conversationStep)
            {
                if (scope is Scope.OfConversation scopeConversation
                    && conversationStep.ConversationId == scopeConversation.ConversationId)
                {
                    return true;
                }
            }
            else if (step is Step.Owner { Node.Background: true } && !crossBackground)
            {
                return false;
            }
        }

        return scope is Scope.Roots;
    }

    /// <summary>
    /// 存活属主的取消意图是否触达 <paramref name="start"/>：向上行走先于无意图的后台属主找到一个带意图
    /// 的属主。终态属主永不级联（spec §5.4）。
    /// </summary>
    private bool BelowCancelled(Up start)
    {
        foreach (var step in Above(start))
        {
            if (step is Step.Unknown) return false;
            if (step is not Step.Owner ownerStep) continue;
            var live = _live.GetValueOrDefault(ownerStep.TaskId);
            if (live is not null && CancellationIntent(live)) return true;
            if (ownerStep.Node.Background) return false;
        }

        return false;
    }

    /// <summary>关闭监听器：受理封印后同步运行一次，先于 <see cref="JoinAsync"/>。</summary>
    private void Seal()
    {
        _closing = true;
        lock (_contextsGate)
        {
            _contexts.Clear();
            CancelContextExpiryLocked();
        }

        lock (_sleepTimerGate)
        {
            _sleepTimer?.Disarm();
            _sleepTimer = null;
        }

        _unsubscribeRegistry.Dispose();
        var error = HarnessUtil.ClosedError();
        _taskWaiters.RejectAll(error);
        _idleWaiters.RejectAll(error);
        _sealed = [.. _invocations.Values];
        foreach (var invocation in _sealed) invocation.Controller.Cancel();
    }

    private void Kick()
    {
        ScheduleWake();
        _dirty = true;
        if (_draining || !_enabled || _closing) return;
        _draining = true;
        // 永不在提交或注册表监听器里同步提交。
        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        try
        {
            while (_dirty && _enabled && !_closing)
            {
                _dirty = false;
                foreach (var reservation in await ReserveAsync().ConfigureAwait(false)) Start(reservation);
            }
        }
        catch (Exception error)
        {
            if (!_closing) _report(error, ListenerFailure);
        }
        finally
        {
            _draining = false;
            // 失败一遍期间到达的唤醒仍需要一遍。
            if (_dirty) Kick();
        }
    }

    /// <summary>在线外重算唤醒：布设内建定时器，或把变化告知 <c>onWake</c>。对应 TS <c>#scheduleWake</c>。</summary>
    private void ScheduleWake()
    {
        if (_wakeScheduled || _closing) return;
        _wakeScheduled = true;
        _ = Task.Run(() =>
        {
            _wakeScheduled = false;
            if (_closing) return;
            Wake wake;
            try
            {
                wake = Wake();
            }
            catch (Exception error)
            {
                if (!_closing) _report(error, OnWakeError);
                // 失败也要保住定时器，否则睡眠任务再没人唤醒。
                ArmSleepTimer(_enabled ? EarliestSleep() : null);
                return;
            }

            if (_onWake is null)
            {
                ArmSleepTimer(_enabled ? EarliestSleep() : null);
                return;
            }

            var last = _lastWake;
            if (last is not null && last.Active == wake.Active && last.At == wake.At) return;
            try
            {
                _onWake(wake);
                // 只有送达的唤醒才记账：抛出之后，下一次变化或 kick 会再给一次。
                _lastWake = wake;
            }
            catch (Exception error)
            {
                _report(error, OnWakeError);
            }
        });
    }

    /// <summary>
    /// 保持最早睡眠的定时器。它不同于上下文过期定时器，会保持进程存活：有睡眠任务的进程还有工作（例如重试退避）。
    /// 触发时重读时钟，所以提前到的唤醒只是再布设一次。
    /// </summary>
    private void ArmSleepTimer(long? at)
    {
        lock (_sleepTimerGate)
        {
            if (_sleepTimer is { } pending && pending.At == at) return;
            _sleepTimer?.Disarm();
            _sleepTimer = null;
            if (at is null || _closing) return;
            var target = at.Value;
            var disarm = _timers.Foreground(() =>
            {
                lock (_sleepTimerGate) _sleepTimer = null;
                WakeDue();
                Kick();
            }, target - _now());
            _sleepTimer = (target, disarm);
        }
    }

    /// <summary>
    /// 让已到点的睡眠任务成为保留候选。端口的保留遍每次都扫描全部存活记录，因此重新发起一遍即可。
    /// </summary>
    private void WakeDue() => Kick();

    /// <summary>连续被切断 <c>restartLimit</c> 次的任务；带 abort 标记的任务仍运行其 abort 处理器。</summary>
    private bool Parked(TaskRecord record)
        => !record.AbortRequested && record.Restarts is { } restarts && restarts >= RestartLimit();

    /// <summary><c>settings.restartLimit</c>；宿主的设置抛出时用缺省值。</summary>
    private int RestartLimit()
    {
        try
        {
            return _settings().RestartLimit;
        }
        catch (Exception error)
        {
            _report(error, SettingsError);
            return AgentDocs.DefaultRestartLimit;
        }
    }

    /// <summary>让上调的 <c>settings.restartLimit</c> 不再停放的任务重新成为候选，并告知任务图。</summary>
    private void UnparkRaised()
    {
        var unparked = false;
        foreach (var id in _parkedTasks.ToArray())
        {
            var record = _live.GetValueOrDefault(id);
            if (record is not null && Parked(record)) continue;
            _parkedTasks.Remove(id);
            if (record is null) continue;
            unparked = true;
        }

        if (unparked) _ = Task.Run(NotifyBlocked);
    }

    /// <summary>告知每个阻塞原因监听器；一个监听器抛出被上报，其余仍运行。</summary>
    private void NotifyBlocked()
    {
        Action[] listeners;
        lock (_blockedListeners) listeners = [.. _blockedListeners];
        foreach (var listener in listeners)
        {
            try
            {
                listener();
            }
            catch (Exception error)
            {
                _report(error, ListenerFailure);
            }
        }
    }

    /// <summary>
    /// 当阻塞原因可能已在不提交的情况下改变时调用 <paramref name="listener"/>：注册表变化、迁移失败、取消停放。
    /// 返回退订。对应 TS <c>subscribeBlocked</c>。
    /// </summary>
    public IDisposable SubscribeBlocked(Action listener)
    {
        lock (_blockedListeners) _blockedListeners.Add(listener);
        return new Unsubscribe(() =>
        {
            lock (_blockedListeners) _blockedListeners.Remove(listener);
        });
    }

    /// <summary>
    /// 存活任务在这里不能运行的原因（若有）：其定义（缺失、过旧、迁移失败）或重启上限。运行中、持有结局、
    /// 以及带 abort 请求标记的任务从不阻塞：中止请求会运行其处理器或将任务 orphaned；带 <c>restart</c> 标记的
    /// 任务等待其定义，因此算阻塞。对应 TS <c>blockedReason()</c>。
    /// </summary>
    public string? BlockedReasonOf(TaskRecord record)
    {
        var status = record.State.Status;
        if (status is TaskStatus.Running or TaskStatus.Completing or TaskStatus.Terminal) return null;
        if (record.AbortRequested && record.AbortReason is null) return null;
        if (Parked(record)) return "restart_limit";
        IRegistrySnapshot snapshot;
        try
        {
            snapshot = _registry.Snapshot();
        }
        catch (Exception error)
        {
            // 读注册表取调度时上报；每次查询都上报会淹没宿主。
            if (!_snapshotReported) _report(error, RegistryError);
            _snapshotReported = true;
            return null;
        }

        var fit = FitOf(record, snapshot.Task(record.Kind));
        if (fit is Fit.No no) return no.Reason;
        var fits = (Fit.Fits)fit;
        return fits.Migrates && fits.Task.Migrate is null ? "migration_failed" : null;
    }

    /// <summary>时钟尚未到达其 <c>until</c> 的睡眠；abort 标记结束睡眠。</summary>
    private bool NotDue(TaskRecord record)
    {
        var state = record.State;
        return state.Status == TaskStatus.Sleeping && !record.AbortRequested && (state.Until ?? 0) > _now();
    }

    /// <summary>
    /// 一次提交保留每个合格任务；没有定义能接手的 abort 标记任务以 orphaned 结算。open 之后的第一遍只为
    /// 崩溃后放弃的任务打 <c>restart</c> 标记，后续几遍才看到这些标记。
    /// </summary>
    private async Task<List<Reservation>> ReserveAsync()
    {
        var reservations = new List<Reservation>();
        var faulted = new List<(TaskRecord Record, string Message)>();
        var abandoned = new List<TaskId<object?>>();
        try
        {
            await _session.CommitWithAsync<object?>(async tx =>
            {
                if (!_enabled || _closing) return null;
                if (_abandoned.Count > 0)
                {
                    foreach (var id in _abandoned)
                    {
                        var record = _live.GetValueOrDefault(id);
                        if (record is not null && !record.AbortRequested) tx.SetTask(WithAbortMark(record, RestartReason));
                    }

                    abandoned.AddRange(_abandoned);
                    return null;
                }

                await LoadScopesAsync(false).ConfigureAwait(false);
                // 任一遍都可能在上调上限后第一次运行停放的任务：每一遍先取消不再成立的停放。
                UnparkRaised();
                var owned = OwnedLive();
                // 每遍取一次，且仅当有任务是候选时。
                IRegistrySnapshot? snapshot = null;
                foreach (var record in _live.Values.ToList())
                {
                    if (_invocations.ContainsKey(record.Id) || WaitingOn(record, owned).Count > 0) continue;
                    if (record.State.Status == TaskStatus.Completing) continue;
                    // 到点才回到候选：由睡眠定时器或 Resume() 的 WakeDue() 重新发起一遍。
                    if (NotDue(record)) continue;
                    // 取消停放（retry()）或 settings.restartLimit 上调后的某一遍才重新成为候选。
                    if (Parked(record))
                    {
                        // 没有提交就新停放（例如上限被下调）：任务图必须听到。
                        if (_parkedTasks.Add(record.Id)) _ = Task.Run(NotifyBlocked);
                        continue;
                    }

                    var mode = record.AbortRequested ? "abort" : "run";
                    // abort 处理器连续被切断 restartLimit 次会永远运行下去；没有东西能再停放它。
                    if (mode == "abort" && (record.Restarts ?? 0) >= RestartLimit())
                    {
                        var message = $"Abort handler of task {record.Id.Value} ({record.Kind}) was cut off "
                            + $"{record.Restarts} times";
                        await TerminateAsync(tx, record, SchedulerOutcome.Faulted(
                            message, new Dictionary<string, object?> { ["reason"] = "restart_limit" }))
                            .ConfigureAwait(false);
                        faulted.Add((record, message));
                        continue;
                    }

                    snapshot ??= _registry.Snapshot();
                    var resolution = Resolve(record, snapshot);
                    if (resolution is Resolution.Blocked blocked)
                    {
                        // 放弃的任务等待其定义，好让 abort 处理器清理。
                        if (mode == "abort" && record.AbortReason is null)
                        {
                            await TerminateAsync(tx, record, SchedulerOutcome.Orphaned(blocked.Reason))
                                .ConfigureAwait(false);
                        }

                        continue;
                    }

                    var ready = (Resolution.Ready)resolution;
                    if (!ReferenceEquals(ready.Record, record) || record.State.Status != TaskStatus.Running)
                    {
                        tx.SetTask(ready.Record with
                        {
                            State = new TaskState
                            {
                                Status = TaskStatus.Running, Checkpoint = ready.Record.State.Checkpoint,
                            },
                        });
                    }

                    // 在线上注册：标记与后续保留都能看到，关闭也会加入它。
                    var invocation = CreateInvocation(record, mode, InstallerOf(snapshot, ready.Task));
                    reservations.Add(new Reservation(invocation, ready.Task, snapshot));
                }

                return null;
            }, _context).ConfigureAwait(false);
        }
        catch
        {
            foreach (var reservation in reservations)
            {
                _invocations.Remove(reservation.Invocation.TaskId);
                reservation.Invocation.Finish();
            }

            throw;
        }

        foreach (var (record, message) in faulted)
        {
            _report(
                new InvalidOperationException(message),
                new ReportInfo.TaskFaulted(ReportTaskOf(record), ReportReasons.RestartLimit, InstallerOf(record)));
        }

        // 已标记、已带标记或已消失：都不再需要，重新保留一次以看到标记。
        foreach (var id in abandoned) _abandoned.Remove(id);
        if (abandoned.Count > 0) _dirty = true;
        return reservations;
    }

    /// <summary>
    /// 下一次调用前任务等待的存活任务：abort 标记时是其存活的普通自有工作（abort 自底向上运行），
    /// 否则是等待 <c>on</c> 的存活部分。
    /// </summary>
    private IReadOnlyList<TaskId<object?>> WaitingOn(
        TaskRecord record, IReadOnlyDictionary<TaskId<object?>, List<TaskId<object?>>> owned)
    {
        if (record.AbortRequested) return owned.TryGetValue(record.Id, out var below) ? below : [];
        if (record.State.Status != TaskStatus.Waiting) return [];
        return (record.State.On ?? []).Where(id => _live.ContainsKey(id)).ToList();
    }

    /// <summary>按种类解析记录的定义，迁移较旧的存储版本。</summary>
    private Resolution Resolve(TaskRecord record, IRegistrySnapshot snapshot)
    {
        var fit = FitOf(record, snapshot.Task(record.Kind));
        if (fit is Fit.No no) return new Resolution.Blocked(no.Reason);
        var fits = (Fit.Fits)fit;
        if (!fits.Migrates) return new Resolution.Ready(fits.Task, record);
        try
        {
            var migrate = (ErasedMigrate?)fits.Task.Migrate ?? throw MissingMigration(record, fits.Task);
            var migrated = migrate(record.Input, record.State.Checkpoint, record.Version);
            var migratedRecord = record with
            {
                Version = fits.Task.Version,
                Input = Json.CopyJson(migrated.Input),
                State = record.State with { Checkpoint = Json.CopyJson(migrated.Checkpoint) },
            };
            return new Resolution.Ready(fits.Task, migratedRecord);
        }
        catch (Exception error)
        {
            _failedMigrations[record.Id] = (fits.Task, error);
            // 不提交就阻塞了：任务图与宿主的唤醒都必须听到。
            _ = Task.Run(NotifyBlocked);
            ScheduleWake();
            _report(error, new ReportInfo.MigrationFailed(ReportTaskOf(record), InstallerOf(snapshot, fits.Task)));
            return new Resolution.Blocked("migration_failed");
        }
    }

    private Fit FitOf(TaskRecord record, AnyDurableTask? task)
    {
        if (task is null) return new Fit.No("missing_task", null);
        var version = task.Version;
        if (version == record.Version) return new Fit.Fits(task, false);
        if (version < record.Version) return new Fit.No("task_too_old", null);
        if (_failedMigrations.TryGetValue(record.Id, out var failed) && ReferenceEquals(failed.Task, task))
        {
            return new Fit.No("migration_failed", failed.Error);
        }

        return new Fit.Fits(task, true);
    }

    /// <summary>
    /// 调度状态与每个存活任务及其派生状态，读于 Session 线。不运行任务代码：pending 的迁移显示为
    /// 带 migrates 的 ready，只有调度器已尝试过、或不可能存在的迁移显示为失败。
    /// </summary>
    public async Task<SchedulerInspection> InspectAsync(IRegistrySnapshot snapshot)
    {
        await LoadScopesAsync(false).ConfigureAwait(false);
        var owned = OwnedLive();
        var tasks = new List<TaskInspection>();
        foreach (var record in _live.Values)
        {
            tasks.Add(InspectTask(record, snapshot, owned));
        }

        var scheduling = _closing ? "closing" : _enabled ? "running" : "paused";
        return new SchedulerInspection(scheduling, tasks);
    }

    private TaskInspection InspectTask(
        TaskRecord record,
        IRegistrySnapshot snapshot,
        IReadOnlyDictionary<TaskId<object?>, List<TaskId<object?>>> owned)
    {
        if (_invocations.ContainsKey(record.Id)) return new TaskInspection.Running { Record = record };
        if (record.State.Status == TaskStatus.Completing)
        {
            return new TaskInspection.Completing { Record = record };
        }

        var on = WaitingOn(record, owned);
        if (on.Count > 0) return new TaskInspection.Waiting(on) { Record = record };
        if (NotDue(record)) return new TaskInspection.Sleeping(record.State.Until ?? 0) { Record = record };
        if (Parked(record)) return new TaskInspection.Blocked("restart_limit") { Record = record };
        var fit = FitOf(record, snapshot.Task(record.Kind));
        if (fit is Fit.No no) return new TaskInspection.Blocked(no.Reason, no.Error) { Record = record };
        var fits = (Fit.Fits)fit;
        if (fits.Migrates && fits.Task.Migrate is null)
        {
            return new TaskInspection.Blocked("migration_failed", MissingMigration(record, fits.Task))
            {
                Record = record,
            };
        }

        return new TaskInspection.Ready(fits.Migrates) { Record = record };
    }

    private Invocation CreateInvocation(TaskRecord record, string mode, string? extension)
    {
        var controller = new CancellationTokenSource();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = new Invocation
        {
            TaskId = record.Id,
            Kind = record.Kind,
            Extension = extension,
            ConversationId = record.ConversationId,
            Mode = mode,
            Controller = controller,
            Context = ContextSignals.WithAbortSignal(controller.Token, _context),
            Watches = [],
            Timers = [],
            Done = completion.Task,
            Finish = () => completion.TrySetResult(null),
        };
        _invocations[record.Id] = invocation;
        return invocation;
    }

    private void Start(Reservation reservation)
    {
        var invocation = reservation.Invocation;
        _ = Task.Run(async () =>
        {
            try
            {
                if (invocation.Mode == "run") await RunAsync(reservation).ConfigureAwait(false);
                else await RunAbortAsync(reservation).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _report(error, new ReportInfo.TaskError(ReportTaskOf(invocation), invocation.Extension));
            }
            finally
            {
                End(invocation);
                invocation.Finish();
                Kick();
            }
        });
    }

    /// <summary>运行阶段处理器，每个之前有一个在线上决定调用是否继续的 step。</summary>
    private async Task RunAsync(Reservation reservation)
    {
        var invocation = reservation.Invocation;
        var state = new RunState { Task = reservation.Task, Snapshot = reservation.Snapshot };
        var phase = new Phase { SnapshotOf = () => state.Snapshot, TaskOf = () => state.Task };
        var runtime = RuntimeOf(invocation, phase);
        PhaseResult? previous = null;
        for (;;)
        {
            var previousNow = previous;
            var current = await StepAsync(
                invocation,
                (tx, stepCurrent) => Decide(tx, stepCurrent, previousNow, state, invocation.Extension))
                .ConfigureAwait(false);

            // 决定与派发之间可能封印关闭。
            if (current is null || _closing) return;
            var checkpoint = current.State.Checkpoint as IReadOnlyDictionary<string, object?>
                ?? throw new InvalidOperationException(
                    $"Task {current.Kind} checkpoint is not an object");
            var phaseName = checkpoint.TryGetValue("phase", out var value) ? value as string : null;
            if (phaseName is null)
            {
                throw new InvalidOperationException($"Task {current.Kind} checkpoint has no phase");
            }

            // 每个阶段处理器在首次使用时重新解析其 agent。
            phase.Agent = null;
            try
            {
                if (!phase.TaskOf().Phases.TryGetValue(phaseName, out var handlerObject)
                    || handlerObject is not TaskPhaseHandler handler)
                {
                    throw new InvalidOperationException(
                        $"Task {current.Kind} has no handler for phase {phaseName}");
                }

                await handler(current, runtime, invocation.Context).ConfigureAwait(false);
                previous = new PhaseResult(checkpoint, null);
            }
            catch (Exception error)
            {
                previous = new PhaseResult(checkpoint, error);
            }
        }
    }

    /// <summary>run 调用的优先级规则，于线上。规则 1（终态 / completing / waiting）与规则 2（关闭）由 step 应用。</summary>
    private Decision Decide(
        Transaction tx, TaskRecord current, PhaseResult? previous, RunState state, string? extension)
    {
        // 3. abort 标记：结束；该任务的普通自有工作消失后新的 abort 调用启动。
        if (current.AbortRequested) return Decision.End;
        if (previous is null) return Decision.Proceed;
        // 4. 未捕获的错误。
        if (previous.Value.Failure is { } failure) return Decision.Faulted(failure);
        // 6. 无持久化进展。
        if (DeltaDiff.JsonEquals(current.State.Checkpoint, previous.Value.Checkpoint))
        {
            var message =
                $"Task {current.Kind} phase {PhaseNameOf(previous.Value.Checkpoint)} returned without durable progress";
            return Decision.Faulted(new InvalidOperationException(message));
        }

        // 5. 有进展：刷新快照；把任务交接给能接手它的替换定义。
        state.Snapshot = _registry.Snapshot();
        var next = state.Snapshot.Task(current.Kind);
        if (!ReferenceEquals(next, state.Task))
        {
            if (next is not null && CanReserve(next, current))
            {
                tx.SetTask(current with
                {
                    State = new TaskState { Status = TaskStatus.Pending, Checkpoint = current.State.Checkpoint },
                });
                return Decision.End;
            }

            if (!state.ReportedSet || !ReferenceEquals(state.Reported, next))
            {
                state.Reported = next;
                state.ReportedSet = true;
                var cause = next is null ? ReportReasons.MissingTask : ReportReasons.IncompatibleTask;
                _report(
                    new InvalidOperationException(
                        $"Task {current.Id.Value} keeps running under its old {current.Kind} definition (cause: {cause})"),
                    new ReportInfo.TaskOutdated(ReportTaskOf(current), cause, extension));
            }
        }

        return Decision.Proceed;
    }

    /// <summary>运行一次 abort 处理器；规则 1、2 与 4 适用；无结局返回即 fault。</summary>
    private async Task RunAbortAsync(Reservation reservation)
    {
        var invocation = reservation.Invocation;
        var current = _live.GetValueOrDefault(invocation.TaskId);
        if (current is null || _closing) return;
        Exception? failure = null;
        try
        {
            var phase = new Phase { SnapshotOf = () => reservation.Snapshot, TaskOf = () => reservation.Task };
            var runtime = RuntimeOf(invocation, phase);
            if (reservation.Task.Abort is not TaskPhaseHandler abort)
            {
                throw new InvalidOperationException(
                    $"Task {reservation.Task.Name} has no abort handler");
            }

            await abort(current, runtime, invocation.Context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }

        var message = $"Abort handler of task {invocation.TaskId.Value} returned without a terminal outcome";
        await StepAsync(
            invocation,
            (_, _) => Decision.Faulted(failure ?? new InvalidOperationException(message))).ConfigureAwait(false);
    }

    /// <summary>
    /// Session 线上的一次同步决定。不再是 running（规则 1：终态 / completing / waiting）、或 Harness 正在
    /// 关闭（规则 2）的任务结束调用且不写；否则 decide 可暂写并返回调用是否继续。结束发生在回调内，
    /// 先于 fault 的 Harness 清理。被拒绝的 step（如关闭后的受理）同样结束调用。
    /// </summary>
    private async Task<TaskRecord?> StepAsync(Invocation invocation, Func<Transaction, TaskRecord, Decision> decide)
    {
        Exception? fault = null;
        TaskRecord? continued;
        try
        {
            continued = await _session.CommitWithAsync(async tx =>
            {
                var found = _live.GetValueOrDefault(invocation.TaskId);
                var current = found?.State.Status == TaskStatus.Running ? found : null;
                var decision = current is not null && !_closing ? decide(tx, current) : Decision.End;
                if (decision.Continue) return current;
                End(invocation);
                if (decision.Fault is { } decided)
                {
                    fault = decided;
                    await TerminateAsync(tx, current!, SchedulerOutcome.Faulted(decided.Message)).ConfigureAwait(false);
                }

                return null;
            }, _context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            End(invocation);
            if (!_closing) _report(error, ListenerFailure);
            return null;
        }

        // 结局落定之后才上报：宿主可以据此计数，例如计入熔断器。
        if (fault is not null)
        {
            _report(
                fault,
                new ReportInfo.TaskFaulted(ReportTaskOf(invocation), ReportReasons.Threw, invocation.Extension));
        }

        return continued;
    }

    /// <summary>
    /// 写入调度器决定的结果。当任务的普通自有工作存活时保持为 <c>completing</c>，其 Harness 清理等待
    /// 最终提交（spec §5.5 规则 4）；否则直接终态并清理。
    /// </summary>
    private async Task TerminateAsync(Transaction tx, TaskRecord record, SchedulerOutcome outcome)
    {
        await LoadScopesAsync(false).ConfigureAwait(false);
        if (OwnedLive(OverlayOf(tx)).ContainsKey(record.Id))
        {
            tx.SetTask(WithState(record, new TaskState
            {
                Status = TaskStatus.Completing, Outcome = outcome.ToOutcome(),
            }));
            return;
        }

        tx.SetTask(WithState(record, new TaskState { Status = TaskStatus.Terminal, Outcome = outcome.ToOutcome() }));
        await _settleOutcome(tx, record, outcome).ConfigureAwait(false);
    }

    /// <summary>
    /// 用任务提交的状态替换其运行中状态。终态在普通自有工作存活时保持为 <c>completing</c>（按提交的
    /// 候选判定，因此同一提交在其下创建的工作计入）。等待先校验。
    /// </summary>
    private async Task CommitStateAsync(
        Transaction tx, Invocation invocation, TaskRecord current, TaskState next)
    {
        if (next.Status == TaskStatus.Waiting)
        {
            await ValidateWaitAsync(tx, invocation, current, next.On ?? [], next.Policy).ConfigureAwait(false);
        }

        if (next.Status == TaskStatus.Sleeping) ValidateSleep(invocation, current, next.Until);

        if (next.Status == TaskStatus.Terminal)
        {
            var overlay = OverlayOf(tx);
            await LoadScopesAsync(false).ConfigureAwait(false);
            foreach (var record in overlay.Tasks.Values)
            {
                await LoadChainAsync(ParentOf(record), overlay).ConfigureAwait(false);
            }

            if (OwnedLive(overlay).ContainsKey(current.Id))
            {
                tx.SetTask(WithState(current, new TaskState
                {
                    Status = TaskStatus.Completing, Outcome = next.Outcome,
                }));
                return;
            }
        }

        // 任务提交的、与其当前状态不同的状态就是进展：其重启计数从零开始。同一 checkpoint 再提交一次
        //（例如作为副作用前的意图）不算。
        var same = SameState(current.State, next);
        tx.SetTask(WithState(same ? current : Progressed(current), next));
    }

    /// <summary>
    /// 等待命名的必须是等待者与其属主之外的既有任务——它们不可能先完成；<c>failFast</c> 只能等待等待者
    /// 拥有的任务。abort 处理器不能等待。
    /// </summary>
    private async Task ValidateWaitAsync(
        Transaction tx,
        Invocation invocation,
        TaskRecord current,
        IReadOnlyList<TaskId<object?>> on,
        JoinPolicy? policy)
    {
        if (invocation.Mode == "abort")
        {
            throw new InvalidOperationException($"Abort handler of task {current.Id.Value} cannot wait");
        }

        var overlay = OverlayOf(tx);
        await LoadChainAsync(ParentOf(current)).ConfigureAwait(false);
        var owners = new HashSet<TaskId<object?>>();
        foreach (var step in Above(ParentOf(current)))
        {
            if (step is Step.Owner ownerStep) owners.Add(ownerStep.TaskId);
        }

        foreach (var id in on)
        {
            if (id == current.Id || owners.Contains(id))
            {
                throw new InvalidOperationException(
                    $"Task {current.Id.Value} cannot wait on itself or its owner {id.Value}");
            }

            var member = overlay.Tasks.GetValueOrDefault(id) ?? _live.GetValueOrDefault(id)
                ?? await _storage.GetTaskAsync(id).ConfigureAwait(false);
            if (member is null) throw new InvalidOperationException($"Task {id.Value} does not exist");
            if (policy == JoinPolicy.FailFast && !Equals(member.Owner, current.Id))
            {
                throw new InvalidOperationException(
                    $"Task {current.Id.Value} can wait failFast only on tasks it owns; {id.Value} is not one");
            }
        }
    }

    /// <summary>
    /// 结束一次调用：其运行时操作自此拒绝、其信号中止、其 watch 停止、其任务被释放。
    /// </summary>
    private void End(Invocation invocation)
    {
        if (invocation.Ended) return;
        invocation.Ended = true;
        if (ReferenceEquals(_invocations.GetValueOrDefault(invocation.TaskId), invocation))
        {
            _invocations.Remove(invocation.TaskId);
        }

        foreach (var watch in invocation.Watches) _ = watch.Stop();
        foreach (var disarm in invocation.Timers.ToArray()) disarm();
        invocation.Timers.Clear();
        // 绑定到该调用的挂起等待（如工具的 waitForTask）随其拒绝。
        invocation.Controller.Cancel();
    }

    /// <summary>范围内没有存活的非后台任务；属主边未加载的任务算在其内。</summary>
    private bool Idle(ConversationId? conversationId)
    {
        var scope = conversationId is { } id
            ? new Scope.OfConversation(id)
            : (Scope)new Scope.Roots();
        foreach (var record in _live.Values)
        {
            if (!record.Background && InScope(ParentOf(record), scope) != false) return false;
        }

        return true;
    }

    // ─── 调用运行时 ─────────────────────────────────────────────────────────

    private ITaskRuntime RuntimeOf(Invocation invocation, Phase phase) => new RuntimeImpl(this, invocation, phase);

    /// <summary>调用已结束后抛出。对应 TS <c>endedError</c>。</summary>
    private static InvalidOperationException EndedError(Invocation invocation)
        => new($"Task {invocation.TaskId.Value} invocation has ended");

    /// <summary>运行一个已提交状态读取，除非调用已结束。</summary>
    private Task<T> ReadAsync<T>(Invocation invocation, Func<Task<T>> read)
    {
        if (invocation.Ended) return Task.FromException<T>(EndedError(invocation));
        return read();
    }

    private async Task<ContextView> ReadRuntimeContextAsync(
        Invocation invocation, ConversationId conversationId, Context context, EntryId? at)
    {
        ConversationContextRange? previous;
        lock (_contextsGate) previous = _contexts.GetValueOrDefault(conversationId)?.Range;

        var result = await ConversationContext.ReadContextFromAsync(
            _session, _storage, conversationId, context, at, previous).ConfigureAwait(false);
        if (result.Range is not { } range || _closing || invocation.Ended) return result.View;

        try
        {
            await _session.ReadOnLineAsync(() =>
            {
                if (_closing || invocation.Ended) return Task.FromResult(false);
                var idle = Idle(conversationId);
                lock (_contextsGate)
                {
                    if (_closing || invocation.Ended) return Task.FromResult(false);
                    var kept = _contexts.GetValueOrDefault(conversationId);
                    if (kept is not null && kept.Range.Bounds.Tail.Value > range.Bounds.Tail.Value)
                        return Task.FromResult(false);

                    if (!idle)
                    {
                        _contexts[conversationId] = new KeptContext(range, null);
                    }
                    else if (ContextRetentionMs() > 0)
                    {
                        _contexts[conversationId] = new KeptContext(range, kept?.IdleSince ?? _now());
                    }
                    else
                    {
                        return Task.FromResult(false);
                    }

                    ScheduleContextExpiryLocked();
                    return Task.FromResult(true);
                }
            }).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (_closing)
        {
        }

        return result.View;
    }

    /// <summary>在线上重读任务并门控调用之后提交。</summary>
    private Task<T> GatedAsync<T>(
        Invocation invocation, Func<Transaction, TaskRecord, Task<T>> change, Context context)
    {
        if (invocation.Ended) return Task.FromException<T>(EndedError(invocation));
        return _session.CommitWithAsync(async tx =>
        {
            if (invocation.Ended) throw EndedError(invocation);
            if (_closing) throw HarnessUtil.ClosedError();
            var found = _live.GetValueOrDefault(invocation.TaskId);
            if (found is null)
                throw new InvalidOperationException($"Task {invocation.TaskId.Value} is terminal");
            if (found.State.Status != TaskStatus.Running)
            {
                throw new InvalidOperationException(
                    $"Task {invocation.TaskId.Value} is {found.State.Status}");
            }

            if (invocation.Mode == "run" && found.AbortRequested)
            {
                throw new InvalidOperationException(
                    $"Task {invocation.TaskId.Value} has a durable abort mark");
            }

            return await change(tx, found).ConfigureAwait(false);
        }, context, new TransactionScope
        {
            ConversationId = invocation.ConversationId, TaskId = invocation.TaskId,
        });
    }

    /// <summary>
    /// 服务一个阶段处理器的运行时：该阶段的快照与任务，及其惰性解析的 agent。
    /// 对应 TS <c>#runtime()</c> 的返回对象。派生 <see cref="RuntimeDocumentFaces"/>（源为 Session），
    /// 读操作以调用结束门控，watch 登记进调用的 watch 集合。
    /// </summary>
    private sealed class RuntimeImpl(TaskScheduler scheduler, Invocation invocation, Phase phase)
        : RuntimeDocumentFaces(
            scheduler._session,
            scheduler._session,
            () =>
            {
                if (invocation.Ended) throw EndedError(invocation);
            }),
            ITaskRuntime
    {
        private IHookRunner? _hooks;

        public TaskId<object?> TaskId => invocation.TaskId;

        public ConversationId ConversationId => invocation.ConversationId;

        public CancellationToken Signal => invocation.Controller.Token;

        public IRegistrySnapshot Registry => phase.SnapshotOf();

        public Settings Settings => scheduler._settings();

        public Models Models => scheduler._models;

        public async Task<Agent> AgentAsync(Context context)
        {
            if (invocation.Ended) throw EndedError(invocation);
            // 每阶段至多解析一次、首次使用时解析；用调用的上下文。
            phase.Agent ??= scheduler._agent(invocation.ConversationId, phase.SnapshotOf(), invocation.Context);
            // 停止等待的调用方不得让共享解析的失败无人观察。
            _ = phase.Agent.ContinueWith(static task => _ = task.Exception, BclTaskScheduler.Default);
            return await ContextSignals.AwaitWithContext(phase.Agent, context).ConfigureAwait(false);
        }

        public IHookRunner Hooks => _hooks ??= new RuntimeHooks(scheduler, invocation, phase);

        public Task<IExecutionEnv?> EnvAsync(Context context)
            => scheduler.ReadAsync(invocation, () => scheduler._env(invocation.ConversationId, context));

        public Task CommitAsync(Func<Transaction, TaskRecord, Task<TaskState?>> change, Context context)
            => scheduler.GatedAsync<object?>(invocation, async (tx, current) =>
            {
                var next = await change(tx, current).ConfigureAwait(false);
                if (next is not null)
                {
                    await scheduler.CommitStateAsync(tx, invocation, current, next).ConfigureAwait(false);
                }

                return null;
            }, context);

        public Task<TaskId<object?>> CreateTaskAsync(
            AnyDurableTask task, object? input, TaskOptions options, Context context)
            => scheduler.GatedAsync(invocation, (tx, _) => tx.CreateTaskErasedAsync(task, input, options), context);

        public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id, Context context)
            => scheduler.ReadAsync(
                invocation, () => scheduler._session.ReadOnLineAsync(() => scheduler._storage.GetTaskAsync(id)));

        public Task<SettledTask> WaitForTaskAsync(TaskId<object?> id, Context context)
            => scheduler.ReadAsync(invocation, () => scheduler.WaitForTaskAsync(
                id, ContextSignals.WithAbortSignal(invocation.Controller.Token, context)));

        public Task<IReadOnlyList<TaskOutcome>> OutcomesAsync(
            IReadOnlyList<TaskId<object?>> ids, Context context)
            => scheduler.ReadAsync(invocation, () => scheduler._session.ReadOnLineAsync(async () =>
            {
                var outcomes = new List<TaskOutcome>();
                foreach (var id in ids)
                {
                    var record = await scheduler._storage.GetTaskAsync(id).ConfigureAwait(false);
                    if (record is not { State.Status: TaskStatus.Terminal })
                    {
                        throw new InvalidOperationException($"Task {id.Value} is not terminal");
                    }

                    outcomes.Add(record.State.Outcome!);
                }

                return (IReadOnlyList<TaskOutcome>)outcomes;
            }));

        public Task<IConversationHandle?> ConversationAsync(ConversationId id, Context context)
            => scheduler.ReadAsync(invocation, () => scheduler._conversation(
                id,
                new InvocationBinding
                {
                    Signal = invocation.Controller.Token,
                    Check = () =>
                    {
                        if (invocation.Ended) throw EndedError(invocation);
                    },
                },
                context));

        public async Task<EntryRecord?> EntryAsync(EntryId id, Context context)
        {
            var stored = await ReadStoredEntryAsync(id, context).ConfigureAwait(false);
            return stored?.Entry;
        }

        public async Task<TypedEntry<TData>?> EntryAsync<TData>(Entry<TData> token, EntryId id, Context context)
        {
            var stored = await ReadStoredEntryAsync(id, context).ConfigureAwait(false);
            var entry = stored?.Entry;
            if (entry is null || entry.Kind != token.Kind) return null;
            return new TypedEntry<TData>
            {
                Id = entry.Id,
                ConversationId = entry.ConversationId,
                Kind = entry.Kind,
                Model = entry.Model,
                Data = (TData?)entry.Data,
                Head = entry.Head,
                Edits = entry.Edits,
                ByTaskId = entry.ByTaskId,
            };
        }

        private Task<StoredEntry?> ReadStoredEntryAsync(EntryId id, Context context)
            => scheduler.ReadAsync(invocation, () => scheduler._session.ReadOnLineAsync(
                () => scheduler._storage.GetEntryAsync(invocation.ConversationId, id)));

        public Task<ContextView> ContextAsync(ConversationId conversationId, Context context, EntryId? at = null)
            => scheduler.ReadAsync(invocation, () => scheduler.ReadRuntimeContextAsync(
                invocation, conversationId, context, at));

        public long Now()
        {
            if (invocation.Ended) throw EndedError(invocation);
            return scheduler._now();
        }

        public void Report(Exception error, ReportFrom? from = null)
        {
            if (invocation.Ended) throw EndedError(invocation);
            // 任务自己的失败带运行它的扩展名；其他来源指出失败的代码属于谁。
            var task = ReportTaskOf(invocation);
            scheduler._report(
                error,
                from switch
                {
                    ReportFrom.FromExtension extension => new ReportInfo.ExtensionError(extension.Extension, task),
                    ReportFrom.FromHost host => new ReportInfo.HostError(host.Callback, task),
                    _ => new ReportInfo.TaskError(task, invocation.Extension),
                });
        }

        public Task SleepUntilAsync(long until, object? checkpoint, Context context)
            => scheduler.GatedAsync<object?>(invocation, async (tx, current) =>
            {
                var next = new TaskState
                {
                    Status = TaskStatus.Sleeping, Checkpoint = checkpoint, Until = until,
                };
                await scheduler.CommitStateAsync(tx, invocation, current, next).ConfigureAwait(false);
                return null;
            }, context);

        public Action SetTimer(Action callback, long delayMs)
        {
            // 已结束的调用，其定时器永不触发。
            if (invocation.Ended) return () => { };
            Action? disarm = null;
            disarm = scheduler._timers.Foreground(() =>
            {
                if (disarm is not null) invocation.Timers.Remove(disarm);
                if (!invocation.Ended) callback();
            }, delayMs);
            invocation.Timers.Add(disarm);
            return () =>
            {
                invocation.Timers.Remove(disarm);
                disarm();
            };
        }

        public Task<object?> MemoAsync(string name, Context context)
            => scheduler.ReadAsync(invocation, async () =>
            {
                var (found, value) = MemoOf(scheduler._live.GetValueOrDefault(invocation.TaskId), name);
                await Task.CompletedTask.ConfigureAwait(false);
                return found ? value : null;
            });

        public Task<object> MemoAsync(string name, object candidate, Context context)
            => scheduler.GatedAsync(invocation, (tx, current) =>
            {
                var (found, winner) = MemoOf(current, name);
                if (found) return Task.FromResult(winner!);
                var memos = current.Memos is { } existing
                    ? new Dictionary<string, object?>(existing)
                    : new Dictionary<string, object?>();
                memos[name] = candidate;
                tx.SetTask(current with { Memos = memos });
                return Task.FromResult(candidate);
            }, context);

        // watch：经 Session 获取后登记进调用；调用已结束时停止并抛出。
        public override async Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, Context context)
            => await TrackWatchAsync(base.WatchDocAsync(token, context)).ConfigureAwait(false);

        public override async Task<IDocumentWatch<T>?> WatchDocAsync<T>(
            DocToken<T> token, ConversationId conversationId, Context context)
            => await TrackWatchAsync(base.WatchDocAsync(token, conversationId, context)).ConfigureAwait(false);

        public override async Task<IDocumentWatch<T>?> WatchDocAsync<T>(
            DocToken<T> token, TaskId<object?> taskId, Context context)
            => await TrackWatchAsync(base.WatchDocAsync(token, taskId, context)).ConfigureAwait(false);

        public override async Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, string key, Context context)
            => await TrackWatchAsync(base.WatchDocAsync(token, key, context)).ConfigureAwait(false);

        public override async Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
            => await TrackWatchAsync(base.WatchDocAsync(token, conversationId, key, context)).ConfigureAwait(false);

        public override async Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
            DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
            => await TrackWatchAsync(base.WatchDocAsync(token, taskId, key, context)).ConfigureAwait(false);

        private async Task<IDocumentWatch<T>?> TrackWatchAsync<T>(Task<IDocumentWatch<T>?> pending)
            where T : class, IReadOnlyDictionary<string, object?>
        {
            if (invocation.Ended) throw EndedError(invocation);
            var watch = await pending.ConfigureAwait(false);
            if (watch is null) return null;
            if (invocation.Ended)
            {
                _ = watch.Stop();
                throw EndedError(invocation);
            }

            invocation.Watches.Add(watch);
            _ = watch.Closed.ContinueWith(
                _ => invocation.Watches.Remove(watch), BclTaskScheduler.Default);
            return watch;
        }

        /// <summary>
        /// 按名称把一个任务的钩子分发给每个匹配的已注册处理器。对应 TS <c>hooks.each</c>：
        /// 处理器错误在调用信号未取消时上报并继续下一个；取消后向上传播。
        /// </summary>
        private sealed class RuntimeHooks(TaskScheduler scheduler, Invocation invocation, Phase phase) : IHookRunner
        {
            public async Task EachAsync(string name, Func<object?, Task> invoke)
            {
                var agent = await AgentAsync().ConfigureAwait(false);
                foreach (var handlers in AgentDocs.AgentHooks(agent, phase.TaskOf().Name))
                {
                    try
                    {
                        await invoke(handlers.Handlers).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        if (invocation.Controller.Token.IsCancellationRequested) throw;
                        scheduler._report(
                            error,
                            new ReportInfo.ExtensionError(
                                handlers.Extension, ReportTaskOf(invocation)));
                    }
                }
            }

            private Task<Agent> AgentAsync()
            {
                if (invocation.Ended) return Task.FromException<Agent>(EndedError(invocation));
                phase.Agent ??= scheduler._agent(
                    invocation.ConversationId, phase.SnapshotOf(), invocation.Context);
                _ = phase.Agent.ContinueWith(static task => _ = task.Exception, BclTaskScheduler.Default);
                return phase.Agent;
            }
        }
    }

    // ─── 辅助类型 ───────────────────────────────────────────────────────────

    /// <summary>一次任务在 run / abort 模式下的内存执行。</summary>
    private sealed class Invocation
    {
        public required TaskId<object?> TaskId { get; init; }

        /// <summary>任务注册名，供上报指明任务。</summary>
        public required string Kind { get; init; }

        /// <summary>安装其定义（即跑其代码）的扩展；内建任务为 null。</summary>
        public required string? Extension { get; init; }

        public required ConversationId ConversationId { get; init; }

        /// <summary>"run" / "abort"。</summary>
        public required string Mode { get; init; }

        public required CancellationTokenSource Controller { get; init; }

        /// <summary>传给处理器的上下文；由 <see cref="Controller"/> 取消。</summary>
        public required Context Context { get; init; }

        /// <summary>经运行时获取的 watch；调用结束时停止。</summary>
        public required HashSet<Watch> Watches { get; init; }

        /// <summary>经运行时布设的定时器；调用结束时解除。</summary>
        public required HashSet<Action> Timers { get; init; }

        public bool Ended { get; set; }

        public required Task<object?> Done { get; init; }

        public required Action Finish { get; init; }
    }

    /// <summary>一个阶段处理器读取的内容：惰性快照与任务，及至多一次的 agent 解析。</summary>
    private sealed class Phase
    {
        public required Func<IRegistrySnapshot> SnapshotOf { get; init; }

        public required Func<AnyDurableTask> TaskOf { get; init; }

        public Task<Agent>? Agent { get; set; }
    }

    private sealed record Reservation(Invocation Invocation, AnyDurableTask Task, IRegistrySnapshot Snapshot);

    /// <summary>刚返回的阶段的结局，由下一个 step 判定。</summary>
    private readonly record struct PhaseResult(JsonDict Checkpoint, Exception? Failure);

    /// <summary>step 决定：以下一阶段继续、结束调用、或以 fault 结束。</summary>
    private readonly record struct Decision(bool Continue, Exception? Fault)
    {
        public static readonly Decision Proceed = new(true, null);

        public static readonly Decision End = new(false, null);

        public static Decision Faulted(Exception error) => new(false, error);
    }

    /// <summary>run 调用的可变状态：当前定义、快照与已上报的替换定义。</summary>
    private sealed class RunState
    {
        public AnyDurableTask Task = null!;

        public IRegistrySnapshot Snapshot = null!;

        public AnyDurableTask? Reported;

        public bool ReportedSet;
    }

    /// <summary>任务的不可变所有权字段。</summary>
    private sealed record TaskNode(ConversationId ConversationId, TaskId<object?>? Owner, bool Background);

    /// <summary>所有权树上向上行走的继续点：属主任务或对话。</summary>
    private abstract record Up
    {
        public sealed record OfTask(TaskId<object?> TaskId) : Up;

        public sealed record OfConversation(ConversationId ConversationId) : Up;
    }

    /// <summary>向上行走的一步：带节点的属主任务、对话、或尚未加载的边。</summary>
    private abstract record Step
    {
        public sealed record Owner(TaskId<object?> TaskId, TaskNode Node) : Step;

        public sealed record OfConversation(ConversationId ConversationId) : Step;

        public sealed record Unknown : Step;
    }

    /// <summary>提交暂存的候选记录；在所有权行走中覆盖已提交记录。</summary>
    private sealed record Overlay(
        IReadOnlyDictionary<TaskId<object?>, TaskRecord> Tasks,
        IReadOnlyDictionary<ConversationId, TaskId<object?>?> Edges);

    /// <summary>普通所有权遍历的起点：一个对话，或每个无主对话。</summary>
    private abstract record Scope
    {
        public sealed record OfConversation(ConversationId ConversationId) : Scope;

        public sealed record Roots : Scope;
    }

    /// <summary>空闲等待键：null 等待整个 Harness。包装 nullable 结构键以通过 notnull 约束。</summary>
    private readonly record struct IdleKey(ConversationId? ConversationId);

    /// <summary>在注册表快照下 pending 任务无法保留的原因；派生值，不持久化。</summary>
    private abstract record Resolution
    {
        public sealed record Ready(AnyDurableTask Task, TaskRecord Record) : Resolution;

        public sealed record Blocked(string Reason) : Resolution;
    }

    /// <summary>能接手记录的定义，或为何不能；判定它不运行任务代码。</summary>
    private abstract record Fit
    {
        public sealed record Fits(AnyDurableTask Task, bool Migrates) : Fit;

        public sealed record No(string Reason, Exception? Error) : Fit;
    }

    private sealed class NopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    // ─── 自由函数 ───────────────────────────────────────────────────────────

    /// <summary>存活属主的持久取消意图：其 abort 标记，或非 <c>completed</c> 的保持结局。</summary>
    private static bool CancellationIntent(TaskRecord record)
        => record.State.Status != TaskStatus.Terminal
            && (record.AbortRequested || FailedOutcome(record));

    /// <summary>记录是否持有或以非 <c>completed</c> 的结局结束。</summary>
    private static bool FailedOutcome(TaskRecord record)
        => record.State is { Status: TaskStatus.Completing or TaskStatus.Terminal, Outcome: { } outcome }
            && outcome.Status != TaskOutcomeStatus.Completed;

    private static Up ParentOf(TaskRecord record)
        => record.Owner is { } owner ? new Up.OfTask(owner) : new Up.OfConversation(record.ConversationId);

    private static Up ParentOf(TaskNode node)
        => node.Owner is { } owner ? new Up.OfTask(owner) : new Up.OfConversation(node.ConversationId);

    private static TaskNode NodeOf(TaskRecord record)
        => new(record.ConversationId, record.Owner, record.Background);

    private static Overlay OverlayOf(Transaction tx)
    {
        var tasks = new Dictionary<TaskId<object?>, TaskRecord>();
        foreach (var record in tx.StagedTasks()) tasks[record.Id] = record;
        var edges = new Dictionary<ConversationId, TaskId<object?>?>();
        foreach (var record in tx.StagedConversations()) edges[record.Id] = record.Owner?.TaskId;
        return new Overlay(tasks, edges);
    }

    /// <summary>只认自有 memo 项；与 TS <c>memoOf</c>（Object.hasOwn）一致，区分缺失与存储的 null。</summary>
    private static (bool Found, object? Value) MemoOf(TaskRecord? record, string name)
        => record?.Memos is { } memos && memos.TryGetValue(name, out var value)
            ? (true, value)
            : (false, null);

    private static string PhaseNameOf(JsonDict checkpoint)
        => checkpoint.TryGetValue("phase", out var value) ? value as string ?? "unknown" : "unknown";

    private static InvalidOperationException MissingMigration(TaskRecord record, AnyDurableTask definition)
        => new($"Task {record.Kind} version {definition.Version} has no migration from {record.Version}");

    /// <summary>一次性退订。</summary>
    private sealed class Unsubscribe(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    /// <summary>替换存活记录的状态；结局一定后 memo 与重启计数消失。</summary>
    private static TaskRecord WithState(TaskRecord record, TaskState state)
        => state.Status is TaskStatus.Terminal or TaskStatus.Completing
            ? record with { State = state, Memos = null, Restarts = null }
            : record with { State = state };

    /// <summary>
    /// 带上 abort 标记的记录：<paramref name="reason"/> 为 <c>restart</c> 表示崩溃后的放弃；不给原因即中止请求，
    /// 它替换 <c>restart</c> 标记。重启计数归零：此后它计的是 abort 处理器被切断的次数。
    /// </summary>
    private static TaskRecord WithAbortMark(TaskRecord record, string? reason = null)
        => record with { AbortRequested = true, AbortReason = reason, Restarts = null };

    /// <summary>任务提交了（不同的）状态之后去掉重启计数的记录。</summary>
    private static TaskRecord Progressed(TaskRecord record)
        => record.Restarts is null ? record : record with { Restarts = null };

    /// <summary>
    /// 两个状态在 JSON 形状上相同：同一 checkpoint 再提交一次（例如副作用前的意图）不是进展。
    /// 无法序列化的状态退回记录相等。
    /// </summary>
    private static bool SameState(TaskState left, TaskState right)
    {
        try
        {
            return System.Text.Json.JsonSerializer.SerializeToElement(left).ToString()
                == System.Text.Json.JsonSerializer.SerializeToElement(right).ToString();
        }
        catch (NotSupportedException)
        {
            return Equals(left, right);
        }
    }

    /// <summary>崩溃后放弃的标记原因（<c>abortReason</c>）。</summary>
    private const string RestartReason = "restart";

    /// <summary>上报所指的任务。对应 TS <c>reportTask(recordTask(record))</c>。</summary>
    private static ReportTask ReportTaskOf(TaskRecord record) => new()
    {
        Id = record.Id,
        Kind = record.Kind,
        ConversationId = record.ConversationId,
    };

    /// <summary>上报所指的任务，按其调用的身份。对应 TS <c>reportTask(invocation)</c>。</summary>
    private static ReportTask ReportTaskOf(Invocation invocation) => new()
    {
        Id = invocation.TaskId,
        Kind = invocation.Kind,
        ConversationId = invocation.ConversationId,
    };

    /// <summary><paramref name="snapshot"/> 中安装 <paramref name="task"/> 的扩展：最后一个列出它的；内建任务没有。</summary>
    private static string? InstallerOf(IRegistrySnapshot snapshot, AnyDurableTask? task)
    {
        if (task is null) return null;
        string? found = null;
        foreach (var installed in snapshot.Installed())
        {
            if (installed.Tasks?.Any(candidate => ReferenceEquals(candidate, task)) == true) found = installed.Name;
        }

        return found;
    }

    /// <summary>当前注册表中安装此任务定义的扩展；读注册表抛出时上报不带扩展名。</summary>
    private string? InstallerOf(TaskRecord record)
    {
        try
        {
            var snapshot = _registry.Snapshot();
            return InstallerOf(snapshot, snapshot.Task(record.Kind));
        }
        catch (Exception)
        {
            // 读注册表取调度时会上报；这里的上报只是不带扩展名地发出。
            return null;
        }
    }

    /// <summary>定义能否在保留时接手任务：同版本，或更新且带迁移。</summary>
    private static bool CanReserve(AnyDurableTask task, TaskRecord record)
        => task.Version == record.Version || (task.Version > record.Version && task.Migrate is not null);

    /// <summary>睡眠的 <c>until</c> 必须是 Harness 时钟上的时刻；abort 处理器不能睡眠。</summary>
    private static void ValidateSleep(Invocation invocation, TaskRecord record, long? until)
    {
        if (invocation.Mode == "abort")
        {
            throw new InvalidOperationException($"Abort handler of task {record.Id.Value} cannot sleep");
        }

        if (until is not { } value)
        {
            throw new InvalidOperationException(
                $"Task {record.Id.Value} cannot sleep without an until time in milliseconds");
        }

        if (value < 0)
        {
            throw new InvalidOperationException(
                $"Task {record.Id.Value} cannot sleep until {value}: not a time in milliseconds");
        }
    }
}
