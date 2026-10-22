using Pi.Ai.Models;
using Pi.Ai.Types;
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

/// <summary>
/// 1.4.0 的上报分类：<c>HarnessOptions.onReport(error, info)</c> 的 <see cref="ReportInfo"/> 指出失败属于
/// 任务、扩展、宿主回调还是会话本身。对应 TS <c>test/harness-reports.test.ts</c>。
/// </summary>
public class HarnessReportTests
{
    private static readonly Context Ctx = Context.Background;

    // ─── 任务自身的失败 ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReportsNameTheTaskAndItsExtensionForItsOwnFailures()
    {
        var faulty = HarnessTestSupport.DefineTask("test.faulty", 1,
        [
            ("run", (TaskPhaseHandler)((_, runtime, _) =>
            {
                runtime.Report(new InvalidOperationException("noted"));
                throw new InvalidOperationException("broken");
            })),
        ]);
        var opened = await OpenAsync([("test", [AnyDurableTask.From(faulty)])]);
        await using var _ = new Closer(opened.Harness);
        var (id, conversationId) = await CreateAsync(opened.Harness, faulty);
        opened.Harness.Resume();
        var settled = await opened.Harness.WaitForTaskAsync(id, Ctx);
        Assert.Equal(TaskOutcomeStatus.Faulted, settled.State.Outcome!.Status);

        // 任务自己的失败带装其定义的扩展名。
        await HarnessTestSupport.WaitForAsync(() => opened.Reports.Count == 2);
        var about = new ReportTask { Id = id, Kind = "test.faulty", ConversationId = conversationId };
        Assert.Equal(
            new Reported("noted", new ReportInfo.TaskError(about, "test")), opened.Reports[0]);
        Assert.Equal(
            new Reported("broken", new ReportInfo.TaskFaulted(about, ReportReasons.Threw, "test")),
            opened.Reports[1]);
    }

    [Fact]
    public async Task RuntimeReportsNameWhoseCodeFailed()
    {
        var faulty = HarnessTestSupport.DefineTask("test.blaming", 1,
        [
            ("run", (TaskPhaseHandler)((_, runtime, _) =>
            {
                // 任务自己、某个扩展、以及某个宿主回调的失败。
                runtime.Report(new InvalidOperationException("own"));
                runtime.Report(new InvalidOperationException("tool"), new ReportFrom.FromExtension("tools"));
                runtime.Report(new InvalidOperationException("models"), new ReportFrom.FromHost(HostCallbacks.Models));
                return runtime.CommitAsync(
                    (_, _) => Task.FromResult<TaskState?>(new TaskState
                    {
                        Status = TaskStatus.Terminal,
                        Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = null },
                    }),
                    Ctx);
            })),
        ]);
        var opened = await OpenAsync([("test", [AnyDurableTask.From(faulty)])]);
        await using var _ = new Closer(opened.Harness);
        var (id, conversationId) = await CreateAsync(opened.Harness, faulty);
        opened.Harness.Resume();
        await opened.Harness.WaitForTaskAsync(id, Ctx);

        await HarnessTestSupport.WaitForAsync(() => opened.Reports.Count >= 3);
        var about = new ReportTask { Id = id, Kind = "test.blaming", ConversationId = conversationId };
        Assert.Contains(new Reported("own", new ReportInfo.TaskError(about, "test")), opened.Reports);
        Assert.Contains(new Reported("tool", new ReportInfo.ExtensionError("tools", about)), opened.Reports);
        Assert.Contains(
            new Reported("models", new ReportInfo.HostError(HostCallbacks.Models, about)), opened.Reports);
    }

    [Fact]
    public async Task NamesTheExtensionWhoseDefinitionRanAlsoAfterAnotherReplacedIt()
    {
        var gate = new HarnessTestSupport.Deferred<object?>();
        var started = new HarnessTestSupport.Deferred<object?>();
        var old = ReplacedTask(async () =>
        {
            started.Resolve(null);
            await gate.Task.ConfigureAwait(false);
        });
        var opened = await OpenAsync([]);
        await using var _ = new Closer(opened.Harness);
        var oldExtension = new HarnessTestSupport.TaskExtension("old-extension", [AnyDurableTask.From(old)]);
        opened.Registry.Install(oldExtension);

        var (id, conversationId) = await CreateAsync(opened.Harness, old);
        opened.Harness.Resume();
        await started.Task;

        // 换成另一个扩展里的同名定义：仍在跑的是旧定义的代码。
        opened.Registry.Uninstall(oldExtension);
        opened.Registry.Install(new HarnessTestSupport.TaskExtension(
            "new-extension", [AnyDurableTask.From(ReplacedTask(() => Task.CompletedTask))]));
        gate.Resolve(null);
        await opened.Harness.WaitForTaskAsync(id, Ctx);

        await HarnessTestSupport.WaitForAsync(
            () => opened.Reports.Any(report => report.Info is ReportInfo.TaskFaulted));
        var faulted = opened.Reports.First(report => report.Info is ReportInfo.TaskFaulted);
        Assert.Equal("broken", faulted.Message);
        Assert.Equal(
            new ReportInfo.TaskFaulted(
                new ReportTask { Id = id, Kind = "test.replaced", ConversationId = conversationId },
                ReportReasons.Threw,
                "old-extension"),
            faulted.Info);
    }

    /// <summary>先跑 <paramref name="run"/>，然后抛出的任务；同名的两个扩展各装一份。</summary>
    private static DurableTask<JsonDict, JsonDict, JsonDict> ReplacedTask(Func<Task> run)
        => HarnessTestSupport.DefineTask("test.replaced", 1,
        [
            ("run", (TaskPhaseHandler)(async (_, _, _) =>
            {
                await run().ConfigureAwait(false);
                throw new InvalidOperationException("broken");
            })),
        ]);

    // ─── 停放、迁移失败与过旧定义 ──────────────────────────────────────────

    [Fact]
    public async Task ReportsAParkedTaskWithItsExtension()
    {
        var started = new HarnessTestSupport.Deferred<object?>();
        var definition = HarnessTestSupport.DefineTask("test.parked", 1,
        [
            ("run", (TaskPhaseHandler)(async (_, _, context) =>
            {
                started.Resolve(null);
                await HangAsync(context).ConfigureAwait(false);
            })),
        ]);
        var settings = new HarnessSettings { RestartLimit = 1 };
        var storage = new MemoryStorage();
        var opened = await OpenAsync([("test", [AnyDurableTask.From(definition)])], storage, settings);
        var (id, conversationId) = await CreateAsync(opened.Harness, definition);
        opened.Harness.Resume();
        await started.Task;

        // 不关闭即“崩溃”：记录留在 running，下一次打开按被切断计。
        var second = await OpenAsync([("test", [AnyDurableTask.From(definition)])], storage.Reopen(), settings);
        await using var _ = new Closer(second.Harness);
        await HarnessTestSupport.WaitForAsync(
            () => second.Reports.Any(report => report.Info is ReportInfo.TaskParked));
        var parked = second.Reports.First(report => report.Info is ReportInfo.TaskParked);
        Assert.Contains("is parked until retryTask()", parked.Message);
        Assert.Equal(
            new ReportInfo.TaskParked(
                new ReportTask { Id = id, Kind = "test.parked", ConversationId = conversationId }, "test"),
            parked.Info);
    }

    [Fact]
    public async Task ReportsAMigrationFailureWithItsExtension()
    {
        var v1 = HarnessTestSupport.DefineTask("fake.migrating", 1, [("run", (_, _, _) => Task.CompletedTask)]);
        var v2 = HarnessTestSupport.DefineTask(
            "fake.migrating", 2, [("run", (_, _, _) => Task.CompletedTask)],
            migrate: () => throw new InvalidOperationException("cannot migrate"));
        var storage = new MemoryStorage();
        var first = await OpenAsync([("v1", [AnyDurableTask.From(v1)])], storage);
        var (id, conversationId) = await CreateAsync(first.Harness, v1);
        await first.Harness.CloseAsync(Ctx);

        var second = await OpenAsync([("v2", [AnyDurableTask.From(v2)])], storage.Reopen());
        await using var _ = new Closer(second.Harness);
        second.Harness.Resume();
        await HarnessTestSupport.WaitForAsync(
            () => second.Reports.Any(report => report.Info is ReportInfo.MigrationFailed));
        var failure = second.Reports.First(report => report.Info is ReportInfo.MigrationFailed);
        Assert.Equal("cannot migrate", failure.Message);
        Assert.Equal(
            new ReportInfo.MigrationFailed(
                new ReportTask { Id = id, Kind = "fake.migrating", ConversationId = conversationId }, "v2"),
            failure.Info);
    }

    [Fact]
    public async Task ReportsATaskThatKeepsRunningUnderAnUninstalledDefinition()
    {
        var gate = new HarnessTestSupport.Deferred<object?>();
        var started = new HarnessTestSupport.Deferred<object?>();
        var definition = HarnessTestSupport.DefineTask("test.keeps-running", 1,
        [
            // 有进展：提交到下一阶段，于是下一个 step 会重读注册表。
            ("run", (TaskPhaseHandler)(async (_, runtime, context) =>
            {
                started.Resolve(null);
                await gate.Task.ConfigureAwait(false);
                await runtime.CommitAsync(
                    (_, _) => Task.FromResult<TaskState?>(new TaskState
                    {
                        Status = TaskStatus.Running,
                        Checkpoint = new Dictionary<string, object?> { ["phase"] = "done" },
                    }),
                    context).ConfigureAwait(false);
            })),
            ("done", (TaskPhaseHandler)((_, runtime, context) => runtime.CommitAsync(
                (_, _) => Task.FromResult<TaskState?>(new TaskState
                {
                    Status = TaskStatus.Terminal,
                    Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = null },
                }),
                context))),
        ]);
        var opened = await OpenAsync([]);
        await using var _ = new Closer(opened.Harness);
        var extension = new HarnessTestSupport.TaskExtension("gone", [AnyDurableTask.From(definition)]);
        opened.Registry.Install(extension);
        var (id, conversationId) = await CreateAsync(opened.Harness, definition);
        opened.Harness.Resume();
        await started.Task;

        // 定义被卸载：任务继续按旧定义跑，并上报过旧的原因。
        opened.Registry.Uninstall(extension);
        gate.Resolve(null);
        await HarnessTestSupport.WaitForAsync(
            async () => (await opened.Harness.GetTaskAsync(id, Ctx))?.State.Status == TaskStatus.Terminal, 10_000);
        await HarnessTestSupport.WaitForAsync(
            () => opened.Reports.Any(report => report.Info is ReportInfo.TaskOutdated));
        var outdated = opened.Reports.First(report => report.Info is ReportInfo.TaskOutdated);
        Assert.Contains("keeps running under its old", outdated.Message);
        // 仍在跑的是被卸载扩展 "gone" 的定义：失败代码属于它。
        Assert.Equal(
            new ReportInfo.TaskOutdated(
                new ReportTask { Id = id, Kind = "test.keeps-running", ConversationId = conversationId },
                ReportReasons.MissingTask,
                "gone"),
            outdated.Info);
    }

    // ─── 扩展、监听器、宿主回调与会话 ──────────────────────────────────────

    [Fact]
    public async Task TellsExtensionListenerWakeAndTimerFailuresFromAFailedSession()
    {
        var definition = HarnessTestSupport.DefineTask("test.sleeper", 1,
        [
            ("run", (TaskPhaseHandler)(async (_, runtime, context) =>
            {
                // 宿主的 setTimer 抛出：上报，并以内建定时器布设。
                runtime.SetTimer(() => { }, 10);
                await runtime.SleepUntilAsync(runtime.Now() + 60_000, new Dictionary<string, object?>
                {
                    ["phase"] = "done",
                }, context).ConfigureAwait(false);
            })),
            ("done", (TaskPhaseHandler)((_, runtime, context) => runtime.CommitAsync(
                (_, _) => Task.FromResult<TaskState?>(new TaskState
                {
                    Status = TaskStatus.Terminal,
                    Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = null },
                }),
                context))),
        ]);
        var storage = new ControlledHarnessStorage(new MemoryStorage());
        var opened = await OpenAsync([("tasks", [AnyDurableTask.From(definition)])], storage,
            onWake: _ => throw new InvalidOperationException("alarm"),
            setTimer: (_, _, _) => throw new InvalidOperationException("no timers"));
        await using var _ = new Closer(opened.Harness);
        // 一个改名的包装：解析 agent 时它的目标被丢弃，失败带包装器的扩展名。
        opened.Registry.Install(new ToolExtension("wraps", "echo",
            new Wrap.Tool("echo", _ => new FakeTool("renamed"))));
        opened.Harness.SubscribeCommits((_, _) => throw new InvalidOperationException("listener"));

        var root = await opened.Harness.RootAsync(Ctx);
        await root.AgentAsync(Ctx);
        var (id, _) = await CreateAsync(opened.Harness, definition);
        opened.Harness.Resume();
        await HarnessTestSupport.WaitForAsync(
            () => opened.Reports.Any(report => report.Message == "Wrapper renamed echo to renamed"));
        await HarnessTestSupport.WaitForAsync(
            () => opened.Reports.Any(report => report.Message == "alarm")
                && opened.Reports.Any(report => report.Message == "no timers"));

        var infoOf = (string message) => opened.Reports.First(report => report.Message == message).Info;
        Assert.Equal(new ReportInfo.ExtensionError("wraps", ConversationId: root.Id), infoOf("Wrapper renamed echo to renamed"));
        Assert.Equal(new ReportInfo.HostError(HostCallbacks.OnWake), infoOf("alarm"));
        Assert.Equal(new ReportInfo.HostError(HostCallbacks.SetTimer), infoOf("no timers"));
        // 提交监听器抛出：上报为 listener_error，提交本身不受影响。
        Assert.Equal(new ReportInfo.ListenerError(), infoOf("listener"));

        // Storage 提交失败使 Session 失败：session_failed。
        storage.FailNextCommit(new InvalidOperationException("disk gone"));
        await Assert.ThrowsAnyAsync<Exception>(async () => await root.CommitAsync(
            tx => ((Transaction)tx).CreateConversationAsync(new ConversationOwnership.Ownerless()), Ctx));
        await HarnessTestSupport.WaitForAsync(() => opened.Reports.Any(report => report.Message == "disk gone"));
        Assert.Equal(new ReportInfo.SessionFailed(), infoOf("disk gone"));
    }

    // ─── 脚手架 ────────────────────────────────────────────────────────────

    private sealed record Reported(string Message, ReportInfo Info);

    /// <summary>一次打开：Harness 及其注册表与上报记录。</summary>
    private sealed record Opened(HarnessImpl Harness, IRegistry Registry, List<Reported> Reports);

    private sealed class Closer(HarnessImpl harness) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await harness.CloseAsync(Ctx).ConfigureAwait(false);
    }

    private static Task<Opened> OpenAsync(
        (string Name, AnyDurableTask[] Tasks)[] installs,
        MemoryStorage? storage = null,
        HarnessSettings? settings = null,
        Action<Wake>? onWake = null,
        SetTimer? setTimer = null)
        => OpenAsync(installs, (IStorage?)storage ?? new MemoryStorage(), settings, onWake, setTimer);

    private static async Task<Opened> OpenAsync(
        (string Name, AnyDurableTask[] Tasks)[] installs,
        IStorage storage,
        HarnessSettings? settings = null,
        Action<Wake>? onWake = null,
        SetTimer? setTimer = null)
    {
        var registry = Registry.CreateRegistry();
        foreach (var (name, tasks) in installs)
        {
            registry.Install(new HarnessTestSupport.TaskExtension(name, tasks));
        }

        var reports = new List<Reported>();
        object gate = new();
        var harness = await DurableHarness.OpenAsync(storage, new HarnessOptions
        {
            Models = new Models(),
            Registry = registry,
            SettingsSource = () => settings,
            OnWake = onWake,
            SetTimer = setTimer,
            OnReport = (error, info) =>
            {
                lock (gate) reports.Add(new Reported(error.Message, info));
            },
            Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, Ctx).ConfigureAwait(false);
        return new Opened(harness, registry, reports);
    }

    private static async Task<(TaskId<object?> Id, ConversationId ConversationId)> CreateAsync(
        HarnessImpl harness, DurableTask<JsonDict, JsonDict, JsonDict> task)
    {
        var root = await harness.RootAsync(Ctx).ConfigureAwait(false);
        var id = await root.CommitAsync(
            tx => tx.CreateTaskAsync(task, null, new TaskOptions
            {
                Ownership = new TaskOwnership.ConversationOwner(),
                ConversationId = root.Id,
            }),
            Ctx).ConfigureAwait(false);
        return (TaskId<object?>.From(id.Value), root.Id);
    }

    /// <summary>在阶段里挂住直到调用被中止或放行。</summary>
    private static async Task HangAsync(Context context)
    {
        var signal = context.AbortSignal ?? CancellationToken.None;
        if (signal.IsCancellationRequested) return;
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = signal.Register(() => stop.TrySetResult());
        await stop.Task.ConfigureAwait(false);
    }

    /// <summary>提供一个工具并以 <see cref="Wrap.Tool"/> 包装它的扩展。</summary>
    private sealed class ToolExtension(string name, string toolName, Wrap wrap) : IExtension
    {
        public string Name => name;

        public IReadOnlyList<IToolRegistration>? Tools => [new FakeTool(toolName)];

        public IReadOnlyList<IPromptSection>? Sections => null;

        public IReadOnlyList<HookRegistration>? Hooks => null;

        public IReadOnlyList<Wrap>? Wraps => [wrap];

        public IReadOnlyList<AnyDurableTask>? Tasks => null;
    }

    private sealed class FakeTool(string name) : IToolRegistration
    {
        public string Name { get; } = name;

        public string Description => Name;

        public ToolSchema Parameters => new(new Dictionary<string, object?>());

        public string? Replay => null;

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => args;

        public Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
            => throw new NotSupportedException();
    }
}
