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

// ============================================================================
// 上游 v1.5.1 #11892：调度 pass 在 await 决策区之后清理共享 `#runnable` /
// `#finalizeChecks` 集合，抹掉了期间 publication 入队的 waiter / holder，导致
// 任务永久卡在 waiting / completing。
//
// 端口 TaskScheduler 是不同架构：
//   - Scheduler.cs:617 Observe() 每次提交更新 `_live` 字典并在末尾 Kick()；
//   - Scheduler.cs:1480 DrainAsync 在 `await ReserveAsync()` **之前**把 `_dirty=false`；
//     期间到达的 Kick() 重新置位，while 循环再跑一遍（1489-1492 finally 亦兜底）；
//   - Scheduler.cs:1679 ReserveAsync 每遍从 `_live.Values.ToList()` 全量重算候选，
//     不存在「共享 pending 集合被 stale 清理」的形态。
//
// 本测试钉住 #11892 的可观察症状：多个在调度进行中并发发布的新候选（父任务在
// phase 内动态创建的一批子任务 + 随后置 waiting 等它们）最终全部被调度、父任务
// 不会永久卡在 waiting。若并发通知被抹掉，父任务会停在 waiting 而超时。
// ============================================================================

public class SchedulerNotificationRaceTests
{
    private static readonly Context Ctx = Context.Background;

    private sealed record Fixture(
        DurableSession Session,
        IRegistry Registry,
        TaskScheduler Scheduler,
        ConversationId Conv,
        List<Exception> Reports) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Session.CloseAsync(Context.Background).Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // 测试收尾不抛。
            }
        }
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var storage = new MemoryStorage();
        var session = new DurableSession(storage);
        var registry = Registry.CreateRegistry();
        var reports = new List<Exception>();
        var conv = await session.CommitAsync(
            async tx => (await tx.CreateConversationAsync(new ConversationOwnership.Ownerless())
                ).Id,
            Ctx);
        var scheduler = new TaskScheduler(new TaskSchedulerOptions
        {
            Session = session,
            Storage = storage,
            Registry = registry,
            Models = null!,
            Agent = (_, snapshot, _) => Task.FromResult(
                AgentDocs.ResolveAgent(null, snapshot, AgentDocs.ResolveSettings(null), (_, _) => { })),
            Settings = () => AgentDocs.ResolveSettings(null),
            Env = (_, _) => Task.FromResult<Pi.Durable.Env.IExecutionEnv?>(null),
            Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            WriteAtClose = writes => storage.CommitAsync(writes(), static _ => { }),
            Timers = HostTimers.Create(null, reports.Add),
            Report = (error, _) => reports.Add(error),
            LimitsRestarts = _ => false,
            SettleOutcome = (_, _, _) => Task.CompletedTask,
            WithdrawInputs = (_, _) => Task.CompletedTask,
            Conversation = (_, _, _) => Task.FromResult<IConversationHandle?>(null),
            Context = Ctx,
        });
        await scheduler.OpenAsync(Ctx);
        return new Fixture(session, registry, scheduler, conv, reports);
    }

    private static Task<TaskId<object?>> CreateTaskAsync(
        Fixture fixture,
        DurableTask<JsonDict, JsonDict, JsonDict> definition,
        TaskOwnership ownership,
        ConversationId? conversationId = null)
        => fixture.Session.CommitAsync(async tx =>
        {
            var id = await tx.CreateTaskAsync(definition, null, new TaskOptions
            {
                Ownership = ownership,
                ConversationId = conversationId,
            });
            return TaskId<object?>.From(id.Value);
        }, Ctx);

    private static async Task<TaskRecord> ReadTaskAsync(Fixture fixture, TaskId<object?> id)
        => (await fixture.Session.CommitAsync(tx => tx.GetTaskAsync(id), Ctx))!;

    private static async Task<TaskRecord> WaitUntilAsync(
        Fixture fixture, TaskId<object?> id, Func<TaskStatus, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var record = await ReadTaskAsync(fixture, id);
            if (predicate(record.State.Status)) return record;
            await Task.Delay(10);
        }

        var current = await ReadTaskAsync(fixture, id);
        throw new TimeoutException(
            $"Task {id.Value} stuck at {current.State.Status}; reports=["
            + string.Join("; ", fixture.Reports.Select(error => error.Message)) + "]");
    }

    private static DurableTask<JsonDict, JsonDict, JsonDict> DefineTask(
        string name, (string Name, TaskPhaseHandler Handler)[] phases)
        => new()
        {
            Definition = new TaskDefinition<JsonDict, JsonDict, JsonDict>
            {
                Name = name,
                Version = 1,
                Initial = _ => new Dictionary<string, object?> { ["phase"] = phases[0].Name },
                Phases = phases.ToDictionary(p => p.Name, p => (object?)p.Handler),
            },
        };

    private sealed class FakeTaskExtension(string name, AnyDurableTask[] tasks) : IExtension
    {
        public string Name => name;
        public IReadOnlyList<IToolRegistration>? Tools => null;
        public IReadOnlyList<IPromptSection>? Sections => null;
        public IReadOnlyList<HookRegistration>? Hooks => null;
        public IReadOnlyList<Wrap>? Wraps => null;
        public IReadOnlyList<AnyDurableTask>? Tasks => tasks;
    }

    private static TaskPhaseHandler CompleteWith(object? result)
        => (_, runtime, context) => runtime.CommitAsync(
            (_, _) => Task.FromResult<TaskState?>(new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = result },
            }),
            context);

    /// <summary>
    /// #11892 可观察行为：父任务在一次 phase 提交里动态发布 N 个子任务并置为 waiting 等它们
    /// （这些子任务与父的等待记录都在调度进行中并发发布），子任务逐个完成，父任务必须最终
    /// 被重新保留并走到 terminal；不会因任何一次 pass 的 stale 清理而永久卡在 waiting。
    /// </summary>
    [Fact]
    public async Task ConcurrentChildPublications_AreNotLost_ParentFinalizes()
    {
        using var fixture = await CreateFixtureAsync();

        // 子任务：短暂延迟后完成，确保它们的终态 publication 落在不同 pass 里并发到达。
        var child = DefineTask("race.child",
        [
            ("c1", (TaskPhaseHandler)(async (_, runtime, context) =>
            {
                await Task.Delay(20);
                await CompleteWith(null)(default!, runtime, context);
            })),
        ]);

        var parent = DefineTask("race.parent",
        [
            ("fanout", (TaskPhaseHandler)(async (task, runtime, context) =>
            {
                // 在同一提交里动态发布多个候选 + 置 waiting，模拟一次 publication 入队多个 waiter/holder。
                await runtime.CommitAsync(async (tx, current) =>
                {
                    var children = new List<TaskId<object?>>();
                    for (var i = 0; i < 4; i++)
                    {
                        var childId = await runtime.CreateTaskAsync(
                            AnyDurableTask.From(child), null,
                            new TaskOptions { Ownership = new TaskOwnership.TaskOwner(task.Id) },
                            context);
                        children.Add(childId);
                    }

                    return new TaskState
                    {
                        Status = TaskStatus.Waiting,
                        On = children,
                        Policy = JoinPolicy.AllSettled,
                        Checkpoint = new Dictionary<string, object?> { ["phase"] = "resume" },
                    };
                }, context);
            })),
            ("resume", CompleteWith("joined")),
        ]);

        fixture.Registry.Install(new FakeTaskExtension("ext",
        [
            AnyDurableTask.From(child),
            AnyDurableTask.From(parent),
        ]));

        var parentId = await CreateTaskAsync(
            fixture, parent, new TaskOwnership.ConversationOwner(), fixture.Conv);
        fixture.Scheduler.Resume();

        // 并发发布的候选不得被抹掉：父任务必须在超时内从 waiting 恢复并终结。
        var record = await WaitUntilAsync(fixture, parentId, status => status == TaskStatus.Terminal);
        Assert.Equal(TaskOutcomeStatus.Completed, record.State.Outcome!.Status);
        Assert.Equal("joined", record.State.Outcome!.Result);
    }
}
