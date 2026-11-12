using Pi.Durable.Storage;
using Pi.Durable.Types;
using DurableTaskStatus = Pi.Durable.Types.TaskStatus;
using Xunit;

namespace Pi.Durable.Tests;

// ============================================================================
// 上游 v1.5.1 group commit：
//   - durable SqliteStorage.commit(batches)（fabf916fc）：把一次调用里的多个 batch
//     合并成几条 SQL 语句以省远程 SQLite 往返，不改变可观察语义；上游自带
//     "Randomized differential test against MemoryStorage" 证明多批依次提交、
//     后批依赖前批状态、前缀停止时与 MemoryStorage 一致。
//   - chord store group commit（8889e623d）：packages/chord/src/store/store.ts 的
//     v2 对象运行时；端口 Pi.Chord 无 store.ts（§4.1 已判定有意收窄的 v1 基础层）。
//
// 证据链（git show，可复核）：
//   - v1.4.0 types.ts: `commit(writes: readonly StorageWrite[], context): Promise<Seq>` 单批；
//   - v1.5.0 types.ts: `commit(batches: readonly (readonly StorageWrite[])[], receive, context)`
//     —— 多批接口在 v1.5.0 迁移窗口就确立，端口 IStorage 对齐的是既有单批形态；
//   - fabf916fc --stat 只改 src/storage/sqlite/{commit,rows,statements,storage}.ts + 测试，
//     不碰 types.ts 接口 → group commit 是 SqliteStorage 内部实现优化，无新 published API；
//   - 端口 SqliteStorage 用本地 Microsoft.Data.Sqlite 单连接（无 Durable Objects 远程往返）。
//
// 本测试把上游 differential 保证在端口可观察层面钉住：一串相互依赖的写入逐批提交，
// SqliteStorage 与 MemoryStorage 的最终状态逐字段一致（任务记录、文档值、序列）。
// 说明「多个 batch 顺序提交、后批依赖前批」的语义在端口与参考后端完全一致，
// group commit 的 SQL 合并只是这一语义下的性能实现细节，无行为缺口。
// ============================================================================

public class GroupCommitDifferentialTests
{
    private static IReadOnlyList<StorageWrite> Batch1() =>
        [new StorageWrite.Conversation(
            new ConversationRecord { Id = DurableIdConstants.RootConversation })];

    private static IReadOnlyList<StorageWrite> Batch2(TaskId<object?> taskId) =>
    [
        new StorageWrite.Task(new TaskRecord
        {
            Id = taskId,
            ConversationId = DurableIdConstants.RootConversation,
            Kind = "test.task",
            Version = 1,
            State = new TaskState
            {
                Status = DurableTaskStatus.Pending,
                Checkpoint = new Dictionary<string, object?> { ["phase"] = "ready" },
            },
            Background = false,
            AbortRequested = false,
        }),
    ];

    private static IReadOnlyList<StorageWrite> Batch3(TaskId<object?> taskId) =>
    [
        // 后批依赖前批：把 Batch2 建的任务推进到 Running（依赖其已存在）。
        new StorageWrite.Task(new TaskRecord
        {
            Id = taskId,
            ConversationId = DurableIdConstants.RootConversation,
            Kind = "test.task",
            Version = 1,
            State = new TaskState
            {
                Status = DurableTaskStatus.Running,
                Checkpoint = new Dictionary<string, object?> { ["phase"] = "working" },
            },
            Background = false,
            AbortRequested = false,
        }),
    ];

    private static IReadOnlyList<StorageWrite> Batch4(DocumentId documentId) =>
    [
        new StorageWrite.DocumentCreateWrite(
            new DocumentCreate
            {
                Id = documentId,
                Kind = "state",
                Scope = new DocumentScope.ConversationScope(DurableIdConstants.RootConversation),
                History = ConversationHistory.Rewindable,
                Fork = ConversationFork.AsOf,
            },
            new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 7L })),
    ];

    private sealed record Snapshot(
        Seq LastSeq,
        string TaskStatus,
        string TaskPhase,
        long DocumentCount,
        bool TaskExists,
        bool DocumentExists);

    private static async Task<Snapshot> DriveAsync(
        IStorage storage, TaskId<object?> taskId, DocumentId documentId)
    {
        // 逐批顺序提交，模拟一次多批调用里「后批依赖前批」的前缀语义。
        var s1 = await storage.CommitAsync(Batch1());
        var s2 = await storage.CommitAsync(Batch2(taskId));
        var s3 = await storage.CommitAsync(Batch3(taskId));
        var s4 = await storage.CommitAsync(Batch4(documentId));

        var task = await storage.GetTaskAsync(taskId);
        var document = await storage.GetDocumentAsync(documentId, new DocumentPoint.Current());
        var checkpoint = task?.State.Checkpoint as IReadOnlyDictionary<string, object?>;
        var phase = checkpoint is not null && checkpoint.TryGetValue("phase", out var value)
            ? value as string ?? "" : "";
        var count = document is { } doc && doc.Value.TryGetValue("count", out var number) && number is long n
            ? n : -1;
        return new Snapshot(s4, task?.State.Status.ToString() ?? "", phase, count,
            task is not null, document is not null);
    }

    [Fact]
    public async Task SequentialBatches_AgreeWithMemoryReferenceStorage()
    {
        var taskId = TaskId<object?>.From(2);
        var documentId = DocumentId.From(3);

        await using var sqlite = await SqliteStorage.OpenAsync(":memory:");
        var memory = new MemoryStorage();

        var sqliteSnapshot = await DriveAsync(sqlite, taskId, documentId);
        var memorySnapshot = await DriveAsync(memory, taskId, documentId);

        // 序列、任务状态、checkpoint、文档值、存在性全部一致。
        Assert.Equal(memorySnapshot, sqliteSnapshot);
        Assert.Equal(DurableTaskStatus.Running.ToString(), sqliteSnapshot.TaskStatus);
        Assert.Equal("working", sqliteSnapshot.TaskPhase);
        Assert.Equal(7L, sqliteSnapshot.DocumentCount);
        Assert.True(sqliteSnapshot.TaskExists);
        Assert.True(sqliteSnapshot.DocumentExists);
    }

    [Fact]
    public async Task SequentialBatches_AreDurableInOrder()
    {
        // 每批拿到严格递增的 seq（对应上游 CommitResult.stored 与 receive(index, seq) 顺序）。
        await using var sqlite = await SqliteStorage.OpenAsync(":memory:");
        var s1 = await sqlite.CommitAsync(Batch1());
        var s2 = await sqlite.CommitAsync(Batch2(TaskId<object?>.From(2)));
        var s3 = await sqlite.CommitAsync(Batch3(TaskId<object?>.From(2)));
        var s4 = await sqlite.CommitAsync(Batch4(DocumentId.From(3)));

        Assert.True(s1.Value < s2.Value && s2.Value < s3.Value && s3.Value < s4.Value,
            $"seq must strictly increase: {s1.Value},{s2.Value},{s3.Value},{s4.Value}");
    }
}
