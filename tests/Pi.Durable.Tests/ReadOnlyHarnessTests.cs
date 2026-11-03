using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using JsonDict = IReadOnlyDictionary<string, object?>;

/// <summary>
/// 只读 Harness（harness-read-only.test.ts）：把 Session 当作另一个进程在驱动的副本来看。打开时不恢复任何东西，
/// 任何带写的提交与所有会启用调度的调用都以 <see cref="ReadOnlySession"/> 拒绝，而读取与观察仍然可用。
/// </summary>
public class ReadOnlyHarnessTests
{
    private static readonly Context Ctx = Context.Background;

    /// <summary>一个生成正跑在别处（仍在等模型）的 Session：驱动端保持打开，任务停在 <c>running</c>。</summary>
    private static async Task<(HarnessImpl Harness, IConversation Root, FakeProvider Provider, MemoryStorage Storage)>
        RunningSessionAsync()
    {
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("slow"), block: true);
        var storage = new MemoryStorage();
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(storage, HarnessTestSupport.NewChatSetup(provider));
        await root.SubmitAsync(new SubmissionDraft.Input { Content = [new TextContent("slow")] }, Ctx);
        await provider.Reached;
        return (harness, root, provider, storage);
    }

    [Fact]
    public async Task OpensASessionWithWorkRunningElsewhereWithoutWriting()
    {
        var (driver, driverRoot, _, storage) = await RunningSessionAsync();
        var wakes = new List<Wake>();
        var viewerSetup = HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("unused"));
        var viewer = await HarnessTestSupport.OpenHarnessAsync(
            storage.Reopen(), viewerSetup, readOnly: true, onWake: wakes.Add);
        try
        {
            var root = (await viewer.ConversationAsync(driverRoot.Id, Ctx))!;
            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            Assert.Equal(["pi.user"], entries.Select(entry => entry.Kind));

            // 打开不做恢复：run 还在，任务仍是 running（没有翻回 pending、没有计重启）。
            var live = await viewer.SnapshotAsync(Live.LiveDoc, root.Id, Ctx);
            Assert.NotNull(live);
            var run = Assert.IsAssignableFrom<JsonDict>(live!["run"]);
            var taskId = TaskId<object?>.From(Convert.ToInt64(run["taskId"]));
            var task = await viewer.GetTaskAsync(taskId, Ctx);
            Assert.NotNull(task);
            Assert.Equal(Pi.Durable.Types.TaskStatus.Running, task!.State.Status);
            Assert.Null(task.Restarts);

            // 调度永远不会自己启动：没有 active 唤醒，onWake 一次都没被叫过。
            Assert.Equal("paused", (await viewer.InspectAsync(Ctx)).Scheduling);
            var wake = viewer.Wake();
            Assert.False(wake.Active);
            Assert.Null(wake.At);
            Assert.Empty(wakes);
            Assert.Empty(viewerSetup.Reports);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(viewer);
            await HarnessTestSupport.CloseQuietlyAsync(driver);
        }
    }

    [Fact]
    public async Task RefusesWritesAndSchedulingAndStaysUsable()
    {
        var provider = new FakeProvider("faux", _ => HarnessTestSupport.AssistantReply("one"));
        var storage = new MemoryStorage();
        var (driver, driverRoot) = await HarnessTestSupport.OpenChatAsync(
            storage, HarnessTestSupport.NewChatSetup(provider));
        await (await driverRoot.SubmitAsync(
            new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx)).WaitAsync(Ctx);
        await HarnessTestSupport.CloseQuietlyAsync(driver);

        var viewerSetup = HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("unused"));
        var viewer = await HarnessTestSupport.OpenHarnessAsync(storage.Reopen(), viewerSetup, readOnly: true);
        try
        {
            var root = (await viewer.ConversationAsync(driverRoot.Id, Ctx))!;
            var before = await HarnessTestSupport.AllEntriesAsync(root);
            Assert.Equal(2, before.Count);

            // 带写的提交在到达 Storage 之前被拒。
            await Assert.ThrowsAsync<ReadOnlySession>(() => root.CommitAsync(
                tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "app.note" }), Ctx));
            await Assert.ThrowsAsync<ReadOnlySession>(() => root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("again")] }, Ctx));
            await Assert.ThrowsAsync<ReadOnlySession>(() => viewer.CreateConversationAsync(
                new ConversationCreateOptions { Ownership = new ConversationOwnership.Ownerless() }, Ctx));

            // 会启用调度的调用同步拒绝（它们先调 Resume，因此抛出发生在返回 Task 之前）。
            Assert.Throws<ReadOnlySession>(() => viewer.Resume());
            Assert.Throws<ReadOnlySession>(() => { _ = root.AbortAsync(Ctx); });
            Assert.Throws<ReadOnlySession>(() => { _ = viewer.WaitForIdleAsync(Ctx); });

            // 仍然健康：读取可用，不写任何东西的提交照常成功。
            Assert.Equal("nothing", await root.CommitAsync(_ => Task.FromResult("nothing"), Ctx));
            Assert.Equal(before.Count, (await HarnessTestSupport.AllEntriesAsync(root)).Count);
            Assert.Equal(["pi.user", "pi.assistant"], before.Select(entry => entry.Kind));
            Assert.Empty(viewerSetup.Reports);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(viewer);
        }
    }

    [Fact]
    public async Task RefusesToCreateTheRootOfAnEmptyStorage()
    {
        var storage = new MemoryStorage();
        var setup = HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("unused"));
        var viewer = await HarnessTestSupport.OpenHarnessAsync(storage, setup, readOnly: true);
        try
        {
            await Assert.ThrowsAsync<ReadOnlySession>(() => viewer.RootAsync(
                Ctx, new AgentChange { Model = new ModelRef(setup.Provider.Id, setup.ModelId) }));
            // 什么都没写：没有任何对话。
            var conversations = await viewer.CommitAsync(
                tx => tx.ScanConversationsAsync(new ConversationQuery(), 10, null), Ctx);
            Assert.Empty(conversations.Items);
            Assert.Empty(setup.Reports);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(viewer);
        }
    }

    [Fact]
    public async Task LeavesRecoveryToTheNextWritableOpen()
    {
        var (driver, driverRoot, _, storage) = await RunningSessionAsync();
        var live = await driver.SnapshotAsync(Live.LiveDoc, driverRoot.Id, Ctx);
        var taskId = TaskId<object?>.From(Convert.ToInt64(
            Assert.IsAssignableFrom<JsonDict>(live!["run"])["taskId"]));

        var viewer = await HarnessTestSupport.OpenHarnessAsync(
            storage.Reopen(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("unused")),
            readOnly: true);
        await HarnessTestSupport.CloseQuietlyAsync(viewer);

        // 可写的重开把任务恢复一次，就像这个 viewer 从没打开过。
        var (writer, _) = await HarnessTestSupport.OpenChatAsync(
            storage.Reopen(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("unused")));
        try
        {
            var record = await writer.GetTaskAsync(taskId, Ctx);
            Assert.NotNull(record);
            Assert.Equal(Pi.Durable.Types.TaskStatus.Pending, record!.State.Status);
            Assert.Equal(1, record.Restarts);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(writer);
            await HarnessTestSupport.CloseQuietlyAsync(driver);
        }
    }
}
