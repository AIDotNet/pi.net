using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using JsonDict = IReadOnlyDictionary<string, object?>;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using TaskScheduler = Pi.Durable.Harness.TaskScheduler;

/// <summary>
/// 1.4.0 的持久睡眠：<c>runtime.sleepUntil()</c> 与 <c>{ status: "sleeping" }</c> 提交一次不持调用的睡眠、
/// 到点在 checkpoint 处恢复；<c>Harness.wake()</c> / <c>HarnessOptions.onWake</c> / <c>setTimer</c> 由宿主接管
/// 定时器与唤醒。对应 TS <c>test/harness-sleep.test.ts</c>，以真实 <see cref="TaskScheduler"/> 与假时钟驱动。
/// </summary>
public class HarnessSleepTests
{
    private static readonly Context Ctx = Context.Background;

    [Fact]
    public async Task SleepsWithoutAnInvocationAndResumesAtItsCheckpointOnceDue()
    {
        var clock = new Clock(1_000);
        var wakes = new List<Wake>();
        var runs = new Runs();
        var nap = NapTask(1_000, runs);
        using var fixture = await CreateAsync(new MemoryStorage(), () => clock.Now, onWake: wakes.Add, tasks: [AnyDurableTask.From(nap)]);
        var id = await CreateTaskAsync(fixture, nap);
        fixture.Scheduler.Resume();
        var asleep = await WaitUntilAsync(fixture, id, status => status == TaskStatus.Sleeping);
        Assert.Equal(2_000L, asleep.State.Until);
        Assert.Equal("after", Phase(asleep));

        var inspection = await fixture.Scheduler.InspectAsync(fixture.Registry.Snapshot());
        var sleeping = Assert.IsType<TaskInspection.Sleeping>(
            Assert.Single(inspection.Tasks, task => task.Record.Id == id));
        Assert.Equal(2_000L, sleeping.Until);

        await WaitForAsync(() => !fixture.Scheduler.Wake().Active);
        Assert.Equal(new Wake { Active = false, At = 2_000 }, fixture.Scheduler.Wake());
        await WaitForAsync(() => wakes.Count > 0 && wakes[^1].At == 2_000 && !wakes[^1].Active);

        // 提前与重复唤醒都不运行任何东西。
        clock.Set(1_999);
        fixture.Scheduler.Resume();
        fixture.Scheduler.Resume();
        await Task.Delay(50);
        Assert.Equal(0, runs.After);
        Assert.Equal(TaskStatus.Sleeping, (await ReadTaskAsync(fixture, id)).State.Status);

        clock.Set(2_000);
        fixture.Scheduler.Resume();
        var resumed = await WaitUntilAsync(fixture, id, status => status == TaskStatus.Terminal);
        Assert.Equal(TaskOutcomeStatus.Completed, resumed.State.Outcome!.Status);
        Assert.Equal(2_000L, resumed.State.Outcome!.Result);
        Assert.Equal(1, runs.After);
        await WaitForAsync(() => !fixture.Scheduler.Wake().Active);
        Assert.Equal(new Wake { Active = false }, fixture.Scheduler.Wake());
    }

    [Fact]
    public async Task KeepsASleepAcrossReopen()
    {
        var clock = new Clock(1_000);
        var runs = new Runs();
        var nap = NapTask(60_000, runs);
        var storage = new MemoryStorage();
        var first = await CreateAsync(storage, () => clock.Now, tasks: [AnyDurableTask.From(nap)]);
        var id = await CreateTaskAsync(first, nap);
        first.Scheduler.Resume();
        await WaitUntilAsync(first, id, status => status == TaskStatus.Sleeping);
        await first.CloseAsync();

        var reopened = await CreateAsync(storage.Reopen(), () => clock.Now, tasks: [AnyDurableTask.From(nap)]);
        try
        {
            await WaitForAsync(() => !reopened.Scheduler.Wake().Active);
            Assert.Equal(new Wake { Active = false, At = 61_000 }, reopened.Scheduler.Wake());
            reopened.Scheduler.Resume();
            await Task.Delay(50);
            Assert.Equal(TaskStatus.Sleeping, (await ReadTaskAsync(reopened, id)).State.Status);
        }
        finally
        {
            await reopened.CloseAsync();
        }

        clock.Set(61_000);
        var third = await CreateAsync(storage.Reopen(), () => clock.Now, tasks: [AnyDurableTask.From(nap)]);
        try
        {
            Assert.True(third.Scheduler.Wake().Active);
            third.Scheduler.Resume();
            var resumed = await WaitUntilAsync(third, id, status => status == TaskStatus.Terminal);
            Assert.Equal(TaskOutcomeStatus.Completed, resumed.State.Outcome!.Status);
            Assert.Equal(61_000L, resumed.State.Outcome!.Result);
            Assert.Equal(1, runs.After);
        }
        finally
        {
            await third.CloseAsync();
        }
    }

    [Fact]
    public async Task ResumesADueSleepWithItsTimerWithoutOnWake()
    {
        var clock = new Clock(1_000);
        var timers = new FakeTimers();
        var runs = new Runs();
        var nap = NapTask(500, runs);
        using var fixture = await CreateAsync(
            new MemoryStorage(), () => clock.Now, timers: timers, tasks: [AnyDurableTask.From(nap)]);
        var id = await CreateTaskAsync(fixture, nap);
        fixture.Scheduler.Resume();
        await WaitForAsync(() => timers.Live.Any(timer => timer.DelayMs == 500));
        Assert.Equal(TaskStatus.Sleeping, (await ReadTaskAsync(fixture, id)).State.Status);

        clock.Set(1_500);
        Assert.Single(timers.Live, timer => timer.DelayMs == 500).Callback();
        var resumed = await WaitUntilAsync(fixture, id, status => status == TaskStatus.Terminal);
        Assert.Equal(1_500L, resumed.State.Outcome!.Result);
    }

    [Fact]
    public async Task RunsADueSleepAtAnySchedulingPassBeforeItsTimerFires()
    {
        var clock = new Clock(1_000);
        var timers = new FakeTimers();
        var runs = new Runs();
        var nap = NapTask(500, runs);
        using var fixture = await CreateAsync(
            new MemoryStorage(), () => clock.Now, timers: timers, tasks: [AnyDurableTask.From(nap)]);
        var id = await CreateTaskAsync(fixture, nap);
        fixture.Scheduler.Resume();
        await WaitUntilAsync(fixture, id, status => status == TaskStatus.Sleeping);

        // 到点之后，任何一遍调度都会让它运行，定时器还在挂。
        clock.Set(1_500);
        fixture.Registry.Install(new FakeTaskExtension("unrelated", []));
        var resumed = await WaitUntilAsync(fixture, id, status => status == TaskStatus.Terminal);
        Assert.Equal(1_500L, resumed.State.Outcome!.Result);
    }

    [Fact]
    public async Task DisarmsTheSleepTimerWhenTheSleepIsAbortedAndWhenTheSchedulerCloses()
    {
        var clock = new Clock(1_000);
        var timers = new FakeTimers();
        var runs = new Runs();
        var nap = NapTask(500, runs);
        var fixture = await CreateAsync(new MemoryStorage(), () => clock.Now, timers: timers, tasks: [AnyDurableTask.From(nap)]);
        try
        {
            var aborted = await CreateTaskAsync(fixture, nap);
            fixture.Scheduler.Resume();
            await WaitForAsync(() => timers.Live.Any(timer => timer.DelayMs == 500));
            Assert.Equal("marked", await fixture.Scheduler.AbortAsync(aborted, TestContext()));
            var receipt = await fixture.Scheduler.WaitForTaskAsync(aborted, TestContext());
            Assert.Equal(TaskOutcomeStatus.Aborted, receipt.Record.State.Outcome?.Status);
            await WaitForAsync(() => timers.Live.Count == 0);

            var kept = await CreateTaskAsync(fixture, nap);
            await WaitUntilAsync(fixture, kept, status => status == TaskStatus.Sleeping);
            await WaitForAsync(() => timers.Live.Count == 1);
        }
        finally
        {
            await fixture.CloseAsync();
        }

        Assert.Empty(timers.Live);
    }

    [Fact]
    public async Task DisarmsAnInvocationsTimersWhenItEndsAndALateFireRunsNothing()
    {
        var fired = new List<string>();
        var timed = new DurableTask<JsonDict, JsonDict, JsonDict>
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = "fake.timed",
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "run" },
                Phases = new Dictionary<string, object?>
                {
                    ["run"] = (TaskPhaseHandler)((task, runtime, context) =>
                    {
                        runtime.SetTimer(() => fired.Add("late"), 60_000);
                        return runtime.CommitAsync(
                            (tx, current) => Task.FromResult<TaskState?>(new TaskState
                            {
                                Status = TaskStatus.Terminal,
                                Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed },
                            }),
                            context);
                    }),
                },
                Abort = (TaskPhaseHandler)((task, runtime, context) => Task.CompletedTask),
            },
        };
        var timers = new FakeTimers();
        using var fixture = await CreateAsync(new MemoryStorage(), () => 1_000L, timers: timers, tasks: [AnyDurableTask.From(timed)]);
        var id = await CreateTaskAsync(fixture, timed);
        fixture.Scheduler.Resume();
        await fixture.Scheduler.WaitForTaskAsync(id, TestContext());

        var timer = Assert.Single(timers.Armed, armed => armed.DelayMs == 60_000);
        await WaitForAsync(() => timer.Disarmed);
        // 宿主仍然触发：已结束的调用什么也不跑。
        timer.Callback();
        Assert.Empty(fired);
    }

    [Fact]
    public async Task ArmsNoTimerForSleepsWithOnWake()
    {
        var clock = new Clock(1_000);
        var timers = new FakeTimers();
        var runs = new Runs();
        var nap = NapTask(500, runs);
        using var fixture = await CreateAsync(
            new MemoryStorage(), () => clock.Now, timers: timers, onWake: _ => { }, tasks: [AnyDurableTask.From(nap)]);
        var id = await CreateTaskAsync(fixture, nap);
        fixture.Scheduler.Resume();
        await WaitUntilAsync(fixture, id, status => status == TaskStatus.Sleeping);
        await Task.Delay(50);
        Assert.Empty(timers.Armed);

        clock.Set(1_500);
        Assert.Equal(new Wake { Active = true }, fixture.Scheduler.Wake());
        fixture.Scheduler.Resume();
        var resumed = await WaitUntilAsync(fixture, id, status => status == TaskStatus.Terminal);
        Assert.Equal(TaskOutcomeStatus.Completed, resumed.State.Outcome!.Status);
    }

    [Fact]
    public async Task ArmsAForegroundTimerTheHostDeclinesWithTheDefaultTimerReportingItOnce()
    {
        var runs = new Runs();
        var nap = NapTask(10, runs);
        using var fixture = await CreateAsync(
            new MemoryStorage(),
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            timers: new FakeTimers { DeclineForeground = true },
            tasks: [AnyDurableTask.From(nap)]);
        var first = await CreateTaskAsync(fixture, nap);
        var second = await CreateTaskAsync(fixture, nap);
        fixture.Scheduler.Resume();
        Assert.Equal(
            TaskOutcomeStatus.Completed,
            (await fixture.Scheduler.WaitForTaskAsync(first, TestContext())).Record.State.Outcome?.Status);
        Assert.Equal(
            TaskOutcomeStatus.Completed,
            (await fixture.Scheduler.WaitForTaskAsync(second, TestContext())).Record.State.Outcome?.Status);
        Assert.Equal(
            ["HarnessOptions.setTimer declined a timer that paces work; using the default timer"],
            fixture.Reports.Select(error => error.Message));
    }

    [Fact]
    public async Task RunsTheAbortHandlerOfASleepingTaskWithoutWaitingForItsTime()
    {
        var clock = new Clock(1_000);
        var runs = new Runs();
        var nap = NapTask(60_000, runs);
        using var fixture = await CreateAsync(new MemoryStorage(), () => clock.Now, onWake: _ => { }, tasks: [AnyDurableTask.From(nap)]);
        var id = await CreateTaskAsync(fixture, nap);
        fixture.Scheduler.Resume();
        await WaitUntilAsync(fixture, id, status => status == TaskStatus.Sleeping);

        Assert.Equal("marked", await fixture.Scheduler.AbortAsync(id, TestContext()));
        var receipt = await fixture.Scheduler.WaitForTaskAsync(id, TestContext());
        Assert.Equal(TaskOutcomeStatus.Aborted, receipt.Record.State.Outcome?.Status);
        Assert.Equal(0, runs.After);
    }

    [Fact]
    public async Task RejectsASleepUntilANegativeTimeAndASleepInAnAbortHandler()
    {
        var errors = new List<string>();
        var invalid = new DurableTask<JsonDict, JsonDict, JsonDict>
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = "fake.invalid-sleep",
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "run" },
                Phases = new Dictionary<string, object?>
                {
                    ["run"] = (TaskPhaseHandler)(async (task, runtime, context) =>
                    {
                        try
                        {
                            await runtime.SleepUntilAsync(-1, new Dictionary<string, object?>
                            {
                                ["phase"] = "next",
                            }, context).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException error)
                        {
                            errors.Add(error.Message);
                        }

                        await runtime.CommitAsync(
                            (tx, current) => Task.FromResult<TaskState?>(new TaskState
                            {
                                Status = TaskStatus.Terminal,
                                Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed },
                            }),
                            context).ConfigureAwait(false);
                    }),
                },
                Abort = (TaskPhaseHandler)((task, runtime, context) => Task.CompletedTask),
            },
        };
        var aborting = new DurableTask<JsonDict, JsonDict, JsonDict>
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = "fake.sleep-in-abort",
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = "run" },
                Phases = new Dictionary<string, object?>
                {
                    // 任务先睡住，好让 abort 处理器在睡眠中执行。
                    ["run"] = (TaskPhaseHandler)((task, runtime, context) => runtime.SleepUntilAsync(
                        runtime.Now() + 60_000,
                        new Dictionary<string, object?> { ["phase"] = "run" },
                        context)),
                },
                Abort = (TaskPhaseHandler)(async (task, runtime, context) =>
                {
                    try
                    {
                        await runtime.SleepUntilAsync(
                            runtime.Now() + 10,
                            new Dictionary<string, object?> { ["phase"] = "run" }, context).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException error)
                    {
                        errors.Add(error.Message);
                    }

                    await runtime.CommitAsync(
                        (tx, current) => Task.FromResult<TaskState?>(new TaskState
                        {
                            Status = TaskStatus.Terminal,
                            Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Aborted },
                        }),
                        context).ConfigureAwait(false);
                }),
            },
        };
        using var fixture = await CreateAsync(
            new MemoryStorage(), () => 1_000L, tasks: [AnyDurableTask.From(invalid), AnyDurableTask.From(aborting)]);
        var id = await CreateTaskAsync(fixture, invalid);
        var aborted = await CreateTaskAsync(fixture, aborting);
        fixture.Scheduler.Resume();
        await fixture.Scheduler.WaitForTaskAsync(id, TestContext());
        await fixture.Scheduler.AbortAsync(aborted, TestContext());
        await fixture.Scheduler.WaitForTaskAsync(aborted, TestContext());

        Assert.Equal(
            [
                $"Task {id.Value} cannot sleep until -1: not a time in milliseconds",
                $"Abort handler of task {aborted.Value} cannot sleep",
            ],
            errors);
    }

    [Fact]
    public async Task DoesNotCountATaskWithoutADefinitionAsActive()
    {
        var runs = new Runs();
        var nap = NapTask(10, runs);
        using var fixture = await CreateAsync(new MemoryStorage(), () => 1_000L, tasks: []);
        var id = await CreateTaskAsync(fixture, nap);
        await WaitForAsync(() => !fixture.Scheduler.Wake().Active);
        Assert.Equal(new Wake { Active = false }, fixture.Scheduler.Wake());
        fixture.Scheduler.Resume();
        await Task.Delay(50);
        await WaitForAsync(() => !fixture.Scheduler.Wake().Active);
        Assert.Equal(new Wake { Active = false }, fixture.Scheduler.Wake());
        Assert.Equal(TaskStatus.Pending, (await ReadTaskAsync(fixture, id)).State.Status);
    }

    [Fact]
    public async Task ReportsAThrowingOnWakeAndOffersTheSameWakeAgainAtTheNextPass()
    {
        var clock = new Clock(1_000);
        var wakes = new List<Wake>();
        var failure = new InvalidOperationException("alarm failed");
        var failing = true;
        var runs = new Runs();
        var nap = NapTask(1_000, runs);
        using var fixture = await CreateAsync(
            new MemoryStorage(),
            () => clock.Now,
            onWake: wake =>
            {
                wakes.Add(wake);
                if (failing && wake.At is not null) throw failure;
            },
            tasks: [AnyDurableTask.From(nap)]);
        var id = await CreateTaskAsync(fixture, nap);
        fixture.Scheduler.Resume();
        await WaitForAsync(() => wakes.Any(wake => wake.At == 2_000 && !wake.Active));
        Assert.Contains(failure, fixture.Reports);

        // 宿主恢复之后，一次无关的提交把同一个未送达的唤醒再给一次。
        failing = false;
        var offered = wakes.Count;
        fixture.Registry.Install(new FakeTaskExtension("unrelated", []));
        await WaitForAsync(() => wakes.Count > offered);
        Assert.Equal(new Wake { Active = false, At = 2_000 }, wakes[^1]);

        clock.Set(2_000);
        fixture.Scheduler.Resume();
        await WaitUntilAsync(fixture, id, status => status == TaskStatus.Terminal);
    }

    // ─── 测试支撑 ────────────────────────────────────────────────────────────

    /// <summary>阶段推进后可变的运行时刻（假 Harness 时钟）。</summary>
    private sealed class Clock(long value)
    {
        private long _value = value;

        public long Now => Interlocked.Read(ref _value);

        public void Set(long next) => Interlocked.Exchange(ref _value, next);
    }

    /// <summary>睡过的次数与被唤醒时的时钟。</summary>
    private sealed class Runs
    {
        public int After;
    }

    private sealed class FakeTimer
    {
        public required long DelayMs { get; init; }

        public required Action Callback { get; init; }

        public bool Disarmed { get; set; }
    }

    /// <summary>记录为工作调速的定时器，并（缺省）拒绝背景定时器。</summary>
    private sealed class FakeTimers
    {
        private readonly List<FakeTimer> _armed = [];

        private readonly object _gate = new();

        /// <summary>宿主拒绝前端定时器：Harness 以默认定时器代替并上报。</summary>
        public bool DeclineForeground { get; init; }

        public SetTimer SetTimer => (callback, delayMs, options) =>
        {
            if (options.Background) return null;
            if (DeclineForeground) return null;
            var timer = new FakeTimer { DelayMs = delayMs, Callback = callback };
            lock (_gate) _armed.Add(timer);
            return () => timer.Disarmed = true;
        };

        public IReadOnlyList<FakeTimer> Armed
        {
            get
            {
                lock (_gate) return [.. _armed];
            }
        }

        public IReadOnlyList<FakeTimer> Live => [.. Armed.Where(timer => !timer.Disarmed)];
    }

    /// <summary>睡 <paramref name="delay"/> 毫秒，之后以醒来时的时钟完成。对应 TS <c>napTask</c>。</summary>
    private static DurableTask<JsonDict, JsonDict, JsonDict> NapTask(long delay, Runs runs) => new()
    {
        Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
        {
            Name = "fake.nap",
            Version = 1,
            Initial = _ => new Dictionary<string, object?> { ["phase"] = "start" },
            Phases = new Dictionary<string, object?>
            {
                ["start"] = (TaskPhaseHandler)((task, runtime, context) => runtime.SleepUntilAsync(
                    runtime.Now() + delay,
                    new Dictionary<string, object?> { ["phase"] = "after", ["slept"] = delay },
                    context)),
                ["after"] = (TaskPhaseHandler)((task, runtime, context) =>
                {
                    runs.After++;
                    var resumed = runtime.Now();
                    return runtime.CommitAsync(
                        (tx, current) => Task.FromResult<TaskState?>(new TaskState
                        {
                            Status = TaskStatus.Terminal,
                            Outcome = new TaskOutcome
                            {
                                Status = TaskOutcomeStatus.Completed, Result = resumed,
                            },
                        }),
                        context);
                }),
            },
            Abort = (TaskPhaseHandler)((task, runtime, context) => runtime.CommitAsync(
                (tx, current) => Task.FromResult<TaskState?>(new TaskState
                {
                    Status = TaskStatus.Terminal,
                    Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Aborted },
                }),
                context)),
        },
    };

    private sealed class Fixture(
        DurableSession session,
        IRegistry registry,
        TaskScheduler scheduler,
        ConversationId conversationId,
        List<Exception> reports) : IDisposable
    {
        public DurableSession Session => session;

        public IRegistry Registry => registry;

        public TaskScheduler Scheduler => scheduler;

        public ConversationId ConversationId => conversationId;

        public List<Exception> Reports => reports;

        public async Task CloseAsync()
        {
            await session.CloseAsync(Context.Background).ConfigureAwait(false);
        }

        public void Dispose()
        {
            try
            {
                session.CloseAsync(Context.Background).Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // 测试收尾不抛。
            }
        }
    }

    private static async Task<Fixture> CreateAsync(
        IStorage storage,
        Func<long> now,
        FakeTimers? timers = null,
        Action<Wake>? onWake = null,
        AnyDurableTask[]? tasks = null)
    {
        var session = new DurableSession(storage);
        var registry = Registry.CreateRegistry();
        var reports = new List<Exception>();
        if (tasks is { Length: > 0 }) registry.Install(new FakeTaskExtension("ext", tasks));
        var conversationId = await session.CommitAsync(
            async tx => (await tx.CreateConversationAsync(new ConversationOwnership.Ownerless())
                .ConfigureAwait(false)).Id,
            Context.Background).ConfigureAwait(false);
        var scheduler = new TaskScheduler(new TaskSchedulerOptions
        {
            Session = session,
            Storage = storage,
            Registry = registry,
            // nap 任务不触达模型目录。
            Models = null!,
            Agent = (id, snapshot, context) => Task.FromResult(
                AgentDocs.ResolveAgent(null, snapshot, AgentDocs.ResolveSettings(null), _ => { })),
            Settings = () => AgentDocs.ResolveSettings(null),
            Env = (_, _) => Task.FromResult<Pi.Durable.Env.IExecutionEnv?>(null),
            Now = now,
            OnWake = onWake,
            Timers = HostTimers.Create(timers?.SetTimer, reports.Add),
            Report = reports.Add,
            SettleOutcome = (_, _, _) => Task.CompletedTask,
            WithdrawInputs = (_, _) => Task.CompletedTask,
            Conversation = (_, _, _) => Task.FromResult<IConversationHandle?>(null),
            Context = Context.Background,
        });
        await scheduler.OpenAsync(Context.Background).ConfigureAwait(false);
        return new Fixture(session, registry, scheduler, conversationId, reports);
    }

    private static Task<TaskId<object?>> CreateTaskAsync(
        Fixture fixture, DurableTask<JsonDict, JsonDict, JsonDict> definition)
        => fixture.Session.CommitAsync(async tx =>
        {
            var id = await tx.CreateTaskAsync(definition, null, new TaskOptions
            {
                Ownership = new TaskOwnership.ConversationOwner(),
                ConversationId = fixture.ConversationId,
            }).ConfigureAwait(false);
            return TaskId<object?>.From(id.Value);
        }, Context.Background);

    private static Context TestContext()
        => ContextSignals.WithAbortSignal(
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token, Context.Background);

    private static async Task<TaskRecord> ReadTaskAsync(Fixture fixture, TaskId<object?> id)
        => (await fixture.Session.CommitAsync(tx => tx.GetTaskAsync(id), Context.Background)
            .ConfigureAwait(false))!;

    private static async Task<TaskRecord> WaitUntilAsync(
        Fixture fixture, TaskId<object?> id, Func<TaskStatus, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var record = await ReadTaskAsync(fixture, id).ConfigureAwait(false);
            if (predicate(record.State.Status)) return record;
            await Task.Delay(10).ConfigureAwait(false);
        }

        var current = await ReadTaskAsync(fixture, id).ConfigureAwait(false);
        throw new TimeoutException(
            $"Task {id.Value} did not reach the expected status; state={current.State.Status}"
            + $" outcome={current.State.Outcome?.Status}"
            + $" reports=[{string.Join("; ", fixture.Reports.Select(error => error.Message))}]");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new TimeoutException("Condition was not met in time");
    }

    private static string? Phase(TaskRecord record)
        => record.State.Checkpoint is IReadOnlyDictionary<string, object?> checkpoint
            && checkpoint.TryGetValue("phase", out var phase)
                ? phase as string
                : null;

    private sealed class FakeTaskExtension(string name, AnyDurableTask[] tasks) : IExtension
    {
        public string Name => name;

        public IReadOnlyList<IToolRegistration>? Tools => null;

        public IReadOnlyList<IPromptSection>? Sections => null;

        public IReadOnlyList<HookRegistration>? Hooks => null;

        public IReadOnlyList<Wrap>? Wraps => null;

        public IReadOnlyList<AnyDurableTask>? Tasks => tasks;
    }
}
