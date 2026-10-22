using Pi.Ai.Models;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using DurableHarness = Pi.Durable.Harness.Harness;
using JsonDict = IReadOnlyDictionary<string, object?>;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using TaskKind = Pi.Durable.Types.DurableTask<
    System.Collections.Generic.IReadOnlyDictionary<string, object?>,
    System.Collections.Generic.IReadOnlyDictionary<string, object?>,
    System.Collections.Generic.IReadOnlyDictionary<string, object?>>;

/// <summary>
/// 1.4.0 的 crash-loop guard：任务在运行中被崩溃连续切断，达到 <c>settings.restartLimit</c> 后停放为
/// blocked（<c>restart_limit</c>），由 <c>Harness.RetryTaskAsync()</c> 或上调的上限放行；close 不算崩溃。
/// 对应 TS <c>test/harness-blocked.test.ts</c> 的 "tasks that cannot run" 一组。
/// </summary>
public class HarnessRestartTests
{
    private static readonly Context Ctx = Context.Background;

    // ─── 停放、显示原因、retryTask 重跑 ─────────────────────────────────────

    [Fact]
    public async Task ParksATaskCutOffRestartLimitTimesShowsWhyAndRunsItAgainOnRetry()
    {
        var loop = new CrashLoop();
        var settings = new MutableSettings(restartLimit: 3);
        var storage = new MemoryStorage();
        var opened = await OpenAsync(storage, [loop.Definition], settings);
        var id = await CreateAsync(opened.Harness, loop.Definition);

        // 每次打开都跑一遍阶段，每次崩溃都切断它：三次重启。
        for (var run = 1; run <= 3; run++)
        {
            opened.Harness.Resume();
            await HarnessTestSupport.WaitForAsync(() => loop.Started == run);
            storage = storage.Reopen();
            opened = await OpenAsync(storage, [loop.Definition], settings);
        }

        var harness = opened.Harness;
        var parked = (await harness.GetTaskAsync(id, Ctx))!;
        Assert.Equal(3, parked.Restarts);
        Assert.Equal(TaskStatus.Pending, parked.State.Status);
        Assert.Contains(opened.Reports, report => report.Message.Contains("was cut off 3 times"));

        var inspection = await harness.InspectAsync(Ctx);
        var blocked = Assert.IsType<TaskInspection.Blocked>(
            Assert.Single(inspection.Tasks, entry => entry.Record.Id == id));
        Assert.Equal("restart_limit", blocked.Reason);

        // 已启动的 watch 保持其值为最新修订。
        var graph = await harness.WatchTaskGraphAsync(Ctx);
        graph.Start((_, _, _) => Task.CompletedTask);
        Assert.Equal("restart_limit", TaskGraphReader.Node(graph.Value, Key(id))!.Blocked);

        // 停放的工作不需要进程。
        await HarnessTestSupport.WaitForAsync(() => !harness.Wake().Active);
        harness.Resume();
        // 一遍调度在若干轮次内运行；它略过停放的任务。
        for (var turn = 0; turn < 5; turn++) await HarnessTestSupport.FlushAsync();
        Assert.Equal(3, loop.Started);

        loop.Crashing = false;
        await harness.RetryTaskAsync(id, Ctx);
        var settled = await harness.WaitForTaskAsync(id, Ctx);
        Assert.Equal(TaskOutcomeStatus.Completed, settled.State.Outcome!.Status);
        Assert.Equal(4, loop.Started);
        await graph.Stop();
        await harness.CloseAsync(Ctx);
    }

    [Fact]
    public async Task CountsNoRestartForATaskACloseStopped()
    {
        var loop = new CrashLoop();
        var settings = new MutableSettings(restartLimit: 1);
        var storage = new MemoryStorage();
        var first = await OpenAsync(storage, [loop.Definition], settings);
        var id = await CreateAsync(first.Harness, loop.Definition);
        first.Harness.Resume();
        await HarnessTestSupport.WaitForAsync(() => loop.Started == 1);
        // 关闭把它止住的任务写回 pending：下次 open 不计重启。
        await first.Harness.CloseAsync(Ctx);

        var second = await OpenAsync(storage.Reopen(), [loop.Definition], settings);
        try
        {
            var reopened = (await second.Harness.GetTaskAsync(id, Ctx))!;
            Assert.Equal(TaskStatus.Pending, reopened.State.Status);
            Assert.Null(reopened.Restarts);
            loop.Crashing = false;
            var settled = await second.Harness.WaitForTaskAsync(id, Ctx);
            Assert.Equal(TaskOutcomeStatus.Completed, settled.State.Outcome!.Status);
        }
        finally
        {
            await second.Harness.CloseAsync(Ctx);
        }
    }

    // ─── abort 处理器被切断 ────────────────────────────────────────────────

    [Fact]
    public async Task FaultsATaskWhoseAbortHandlerIsCutOffRestartLimitTimes()
    {
        var aborts = 0;
        var stuck = new TaskKind
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = "fake.stuck-abort",
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "run" },
                Phases = new Dictionary<string, object?>
                {
                    ["run"] = (TaskPhaseHandler)((_, _, context) => HangAsync(context)),
                },
                Abort = (TaskPhaseHandler)((_, _, context) =>
                {
                    aborts++;
                    return HangAsync(context);
                }),
            },
        };
        var settings = new MutableSettings(restartLimit: 1);
        var storage = new MemoryStorage();
        var opened = await OpenAsync(storage, [stuck], settings);
        var id = await CreateAsync(opened.Harness, stuck);
        opened.Harness.Resume();
        await HarnessTestSupport.WaitForAsync(() => opened.Harness.Wake().Active);
        _ = opened.Harness.AbortTaskAsync(id, Ctx);
        await HarnessTestSupport.WaitForAsync(() => aborts == 1);
        // abort 处理器里的一次崩溃。
        opened = await OpenAsync(storage.Reopen(), [stuck], settings);
        var harness = opened.Harness;

        var settled = await harness.WaitForTaskAsync(id, Ctx);
        Assert.Equal(TaskOutcomeStatus.Faulted, settled.State.Outcome!.Status);
        var detail = Assert.IsAssignableFrom<JsonDict>(settled.State.Outcome!.Error!.Detail);
        Assert.Equal("restart_limit", detail["reason"]);
        // 回执不带重启计数。
        Assert.Null(settled.Record.Restarts);
        Assert.Equal(1, aborts);
        await harness.CloseAsync(Ctx);
    }

    // ─── 上限上调与进展清零 ────────────────────────────────────────────────

    [Fact]
    public async Task RunsAParkedTaskOnceARaisedRestartLimitNoLongerParksIt()
    {
        var loop = new CrashLoop();
        var settings = new MutableSettings(restartLimit: 1);
        var storage = new MemoryStorage();
        var opened = await OpenAsync(storage, [loop.Definition], settings);
        var id = await CreateAsync(opened.Harness, loop.Definition);
        opened.Harness.Resume();
        await HarnessTestSupport.WaitForAsync(() => loop.Started == 1);
        opened = await OpenAsync(storage.Reopen(), [loop.Definition], settings);
        var harness = opened.Harness;

        var graph = await harness.WatchTaskGraphAsync(Ctx);
        graph.Start((_, _, _) => Task.CompletedTask);
        harness.Resume();
        await HarnessTestSupport.WaitForAsync(
            () => TaskGraphReader.Node(graph.Value, Key(id))?.Blocked == "restart_limit");
        // 让一遍调度略过停放的任务。
        for (var turn = 0; turn < 5; turn++) await HarnessTestSupport.FlushAsync();
        loop.Crashing = false;
        // 宿主的活设置变了；下一遍就运行它。
        settings.RestartLimit = int.MaxValue;
        harness.Resume();
        var settled = await harness.WaitForTaskAsync(id, Ctx);
        Assert.Equal(TaskOutcomeStatus.Completed, settled.State.Outcome!.Status);
        Assert.Equal(2, loop.Started);
        await graph.Stop();
        await harness.CloseAsync(Ctx);
    }

    [Fact]
    public async Task StartsTheCountOverWhenTheTaskMakesProgress()
    {
        var started = 0;
        var crashing = true;
        var steps = new TaskKind
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = "fake.steps",
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "one" },
                Phases = new Dictionary<string, object?>
                {
                    ["one"] = (TaskPhaseHandler)(async (_, runtime, context) =>
                    {
                        started++;
                        if (crashing) await HangAsync(context).ConfigureAwait(false);
                        else
                        {
                            await runtime.CommitAsync(
                                (_, _) => Task.FromResult<TaskState?>(new TaskState
                                {
                                    Status = TaskStatus.Running,
                                    Checkpoint = new Dictionary<string, object?> { ["phase"] = "two" },
                                }),
                                context).ConfigureAwait(false);
                        }
                    }),
                    ["two"] = (TaskPhaseHandler)(async (_, _, context) =>
                    {
                        started++;
                        await HangAsync(context).ConfigureAwait(false);
                    }),
                },
                Abort = (TaskPhaseHandler)((_, _, _) => Task.CompletedTask),
            },
        };
        var settings = new MutableSettings(restartLimit: 2);
        var storage = new MemoryStorage();
        var opened = await OpenAsync(storage, [steps], settings);
        var id = await CreateAsync(opened.Harness, steps);
        opened.Harness.Resume();
        await HarnessTestSupport.WaitForAsync(() => started == 1);
        opened = await OpenAsync(storage.Reopen(), [steps], settings);
        var harness = opened.Harness;
        Assert.Equal(1, (await harness.GetTaskAsync(id, Ctx))!.Restarts);

        // 进展：阶段一提交阶段二，计数清零。
        crashing = false;
        harness.Resume();
        await HarnessTestSupport.WaitForAsync(() => started == 3);
        Assert.Null((await harness.GetTaskAsync(id, Ctx))!.Restarts);
        await harness.CloseAsync(Ctx);
    }

    [Fact]
    public async Task DoesNotCountTheSameCheckpointCommittedAgainAsProgress()
    {
        var started = 0;
        var intent = new TaskKind
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = "fake.intent",
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "run" },
                Phases = new Dictionary<string, object?>
                {
                    ["run"] = (TaskPhaseHandler)(async (_, runtime, context) =>
                    {
                        started++;
                        // 副作用前的意图，然后副作用永不返回：checkpoint 没变。
                        await runtime.CommitAsync(
                            (_, current) => Task.FromResult<TaskState?>(new TaskState
                            {
                                Status = TaskStatus.Running, Checkpoint = current.State.Checkpoint,
                            }),
                            context).ConfigureAwait(false);
                        await HangAsync(context).ConfigureAwait(false);
                    }),
                },
                Abort = (TaskPhaseHandler)((_, _, _) => Task.CompletedTask),
            },
        };
        var settings = new MutableSettings(restartLimit: 2);
        var storage = new MemoryStorage();
        var opened = await OpenAsync(storage, [intent], settings);
        var id = await CreateAsync(opened.Harness, intent);
        var harness = opened.Harness;
        for (var run = 1; run <= 2; run++)
        {
            harness.Resume();
            await HarnessTestSupport.WaitForAsync(() => started == run);
            await HarnessTestSupport.WaitForAsync(async () =>
                (await harness.GetTaskAsync(id, Ctx).ConfigureAwait(false))?.State.Status == TaskStatus.Running);
            storage = storage.Reopen();
            opened = await OpenAsync(storage, [intent], settings);
            harness = opened.Harness;
        }

        Assert.Equal(2, (await harness.GetTaskAsync(id, Ctx))!.Restarts);
        var inspection = await harness.InspectAsync(Ctx);
        var blocked = Assert.IsType<TaskInspection.Blocked>(
            Assert.Single(inspection.Tasks, entry => entry.Record.Id == id));
        Assert.Equal("restart_limit", blocked.Reason);
        await harness.CloseAsync(Ctx);
    }

    // ─── 阻塞原因与注册表 ──────────────────────────────────────────────────

    [Fact]
    public async Task StillRunsTheAbortHandlerOfAParkedTask()
    {
        var loop = new CrashLoop();
        var aborted = new List<TaskId<object?>>();
        var withAbort = new TaskKind
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = "fake.crash-loop",
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "run" },
                Phases = loop.Definition.Definition.Phases,
                Abort = (TaskPhaseHandler)((task, runtime, context) =>
                {
                    aborted.Add(task.Id);
                    return runtime.CommitAsync(
                        (_, _) => Task.FromResult<TaskState?>(new TaskState
                        {
                            Status = TaskStatus.Terminal,
                            Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Aborted },
                        }),
                        context);
                }),
            },
        };
        var settings = new MutableSettings(restartLimit: 1);
        var storage = new MemoryStorage();
        var opened = await OpenAsync(storage, [withAbort], settings);
        var id = await CreateAsync(opened.Harness, withAbort);
        opened.Harness.Resume();
        await HarnessTestSupport.WaitForAsync(() => loop.Started == 1);
        opened = await OpenAsync(storage.Reopen(), [withAbort], settings);
        var harness = opened.Harness;

        await harness.AbortTaskAsync(id, Ctx);
        var settled = await harness.WaitForTaskAsync(id, Ctx);
        Assert.Equal(TaskOutcomeStatus.Aborted, settled.State.Outcome!.Status);
        Assert.Equal(new List<TaskId<object?>> { id }, aborted);
        Assert.Equal(1, loop.Started);
        await harness.CloseAsync(Ctx);
    }

    [Fact]
    public async Task ShowsAnAbandonedTaskWaitingForItsDefinitionAsBlocked()
    {
        var abandoned = new CrashLoop("fake.abandoned");
        var storage = new MemoryStorage();
        var first = await OpenAsync(storage, [abandoned.Definition]);
        var id = await CreateAsync(first.Harness, abandoned.Definition, abandonOnRestart: true);
        // 从未运行：调度保持关闭，关闭不写回任何内容。
        await first.Harness.CloseAsync(Ctx);

        // 下一次 open 为它打 restart 标记；没有定义时它等待一个能清理它的定义。
        var second = await OpenAsync(storage.Reopen(), []);
        try
        {
            var graph = await second.Harness.WatchTaskGraphAsync(Ctx);
            graph.Start((_, _, _) => Task.CompletedTask);
            second.Harness.Resume();
            await HarnessTestSupport.WaitForAsync(async () =>
                (await second.Harness.GetTaskAsync(id, Ctx).ConfigureAwait(false))?.AbortReason == "restart");
            await HarnessTestSupport.WaitForAsync(
                () => TaskGraphReader.Node(graph.Value, Key(id))?.Blocked == "missing_task");
            await graph.Stop();
        }
        finally
        {
            await second.Harness.CloseAsync(Ctx);
        }
    }

    [Fact]
    public async Task ShowsATaskWithoutADefinitionAsBlockedAndWakesTheHostOnceARegistryChangeUnblocksIt()
    {
        var missing = new CrashLoop("fake.missing") { Crashing = false };
        var opened = await OpenAsync(new MemoryStorage(), []);
        var harness = opened.Harness;
        try
        {
            var graph = await harness.WatchTaskGraphAsync(Ctx);
            graph.Start((_, _, _) => Task.CompletedTask);
            var id = await CreateAsync(harness, missing.Definition);
            harness.Resume();
            await HarnessTestSupport.WaitForAsync(
                () => TaskGraphReader.Node(graph.Value, Key(id))?.Blocked == "missing_task");
            await HarnessTestSupport.WaitForAsync(() => !harness.Wake().Active);

            opened.Registry.Install(new HarnessTestSupport.TaskExtension(
                "late", [AnyDurableTask.From(missing.Definition)]));
            // 图随注册表变化；宿主听到现在有可运行的工作。
            await HarnessTestSupport.WaitForAsync(
                () => TaskGraphReader.Node(graph.Value, Key(id))?.Blocked is null);
            await HarnessTestSupport.WaitForAsync(() => opened.Wakes.Any(wake => wake.Active));
            var settled = await harness.WaitForTaskAsync(id, Ctx);
            Assert.Equal(TaskOutcomeStatus.Completed, settled.State.Outcome!.Status);
            await graph.Stop();
        }
        finally
        {
            await harness.CloseAsync(Ctx);
        }
    }

    [Fact]
    public async Task TellsTheGraphAndTheHostWhenAMigrationFails()
    {
        var v1 = HarnessTestSupport.DefineTask("fake.migrating", 1, [("run", (_, _, _) => Task.CompletedTask)]);
        var v2 = HarnessTestSupport.DefineTask(
            "fake.migrating", 2, [("run", (_, _, _) => Task.CompletedTask)],
            migrate: () => throw new InvalidOperationException("cannot migrate"));
        var firstRegistry = Registry.CreateRegistry();
        firstRegistry.Install(new HarnessTestSupport.TaskExtension("v1", [AnyDurableTask.From(v1)]));
        var first = await OpenAsync(new MemoryStorage(), [], registry: firstRegistry);
        var storage = first.Storage;
        var id = await CreateAsync(first.Harness, v1);
        await first.Harness.CloseAsync(Ctx);

        var secondRegistry = Registry.CreateRegistry();
        secondRegistry.Install(new HarnessTestSupport.TaskExtension("v2", [AnyDurableTask.From(v2)]));
        var second = await OpenAsync(storage.Reopen(), [], registry: secondRegistry);
        try
        {
            var graph = await second.Harness.WatchTaskGraphAsync(Ctx);
            graph.Start((_, _, _) => Task.CompletedTask);
            second.Harness.Resume();
            await HarnessTestSupport.WaitForAsync(
                () => TaskGraphReader.Node(graph.Value, Key(id))?.Blocked == "migration_failed");
            await HarnessTestSupport.WaitForAsync(() => !second.Harness.Wake().Active);
            await HarnessTestSupport.WaitForAsync(() => second.Wakes.Count > 0 && !second.Wakes[^1].Active);
            await graph.Stop();
        }
        finally
        {
            await second.Harness.CloseAsync(Ctx);
        }
    }

    // ─── 脚手架 ────────────────────────────────────────────────────────────

    private static string Key(TaskId<object?> id)
        => id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>一次打开：Harness 及其注册表、上报与唤醒记录。</summary>
    private sealed record Opened(
        HarnessImpl Harness,
        IRegistry Registry,
        MemoryStorage Storage,
        List<Exception> Reports,
        List<Wake> Wakes);

    /// <summary>可修改的活设置（<c>HarnessOptions.settingsSource</c>）。</summary>
    private sealed class MutableSettings(int? restartLimit)
    {
        public int? RestartLimit { get; set; } = restartLimit;

        public HarnessSettings Current => new() { RestartLimit = RestartLimit };
    }

    /// <summary>
    /// 在阶段里挂住直到调用被中止：崩溃（不关闭）时永不返回，关闭时正常返回且不提交。
    /// 对应 TS <c>runtime.signal.addEventListener("abort", …)</c>。
    /// </summary>
    private static async Task HangAsync(Context context)
    {
        var signal = context.AbortSignal ?? CancellationToken.None;
        if (signal.IsCancellationRequested) return;
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = signal.Register(() => stop.TrySetResult());
        await stop.Task.ConfigureAwait(false);
    }

    /// <summary><c>run</c> 阶段挂住（或被放行后完成）的任务，模拟每次打开都被切断的崩溃循环。</summary>
    private sealed class CrashLoop(string name = "fake.crash-loop")
    {
        public bool Crashing { get; set; } = true;

        public int Started { get; set; }

        public TaskKind Definition => new()
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = name,
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "run" },
                Phases = new Dictionary<string, object?>
                {
                    ["run"] = (TaskPhaseHandler)(async (_, runtime, context) =>
                    {
                        Started++;
                        if (Crashing) await HangAsync(context).ConfigureAwait(false);
                        else
                        {
                            await runtime.CommitAsync(
                                (_, _) => Task.FromResult<TaskState?>(new TaskState
                                {
                                    Status = TaskStatus.Terminal,
                                    Outcome = new TaskOutcome
                                    {
                                        Status = TaskOutcomeStatus.Completed, Result = null,
                                    },
                                }),
                                context).ConfigureAwait(false);
                        }
                    }),
                },
                Abort = (TaskPhaseHandler)((_, _, _) => Task.CompletedTask),
            },
        };
    }

    /// <summary>打开装有所述任务的 Harness；<paramref name="registry"/> 给定时不再安装任务。</summary>
    private static async Task<Opened> OpenAsync(
        MemoryStorage storage,
        IReadOnlyList<TaskKind> tasks,
        MutableSettings? settings = null,
        IRegistry? registry = null)
    {
        var target = registry ?? Registry.CreateRegistry();
        if (registry is null && tasks.Count > 0)
        {
            target.Install(new HarnessTestSupport.TaskExtension(
                "tasks", [.. tasks.Select(task => AnyDurableTask.From(task))]));
        }

        var reports = new List<Exception>();
        var wakes = new List<Wake>();
        var live = settings ?? new MutableSettings(null);
        var harness = await DurableHarness.OpenAsync(storage, new HarnessOptions
        {
            Models = new Models(),
            Registry = target,
            SettingsSource = () => live.Current,
            OnWake = wake =>
            {
                lock (wakes) wakes.Add(wake);
            },
            OnReport = report => reports.Add(report as Exception ?? new InvalidOperationException(report?.ToString())),
            Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, Ctx).ConfigureAwait(false);
        return new Opened(harness, target, storage, reports, wakes);
    }

    private static async Task<TaskId<object?>> CreateAsync(
        HarnessImpl harness, TaskKind task, bool abandonOnRestart = false)
    {
        var root = await harness.RootAsync(Ctx).ConfigureAwait(false);
        var id = await root.CommitAsync(
            tx => tx.CreateTaskAsync(task, null, new TaskOptions
            {
                Ownership = new TaskOwnership.ConversationOwner(),
                ConversationId = root.Id,
                AbandonOnRestart = abandonOnRestart,
            }),
            Ctx).ConfigureAwait(false);
        return TaskId<object?>.From(id.Value);
    }
}
