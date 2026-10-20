using Pi.Chord.Context;
using Pi.Durable.Env;
using Pi.Durable.Storage;
using Pi.Durable.Testing;
using Pi.Durable.Types;
using DurableTaskStatus = Pi.Durable.Types.TaskStatus;
using Xunit;

namespace Pi.Durable.Tests;

public class JsonlStorageConformanceTests
{
    private static IReadOnlyList<StorageConformanceCase> Cases() =>
        StorageConformance.CreateStorageConformance(new StorageConformanceOptions
        {
            Assertions = ConformanceAssertions.Create(),
            WithStorage = async use =>
            {
                var (directory, env) = JsonlStorageFixture.Create();
                try
                {
                    var storage = await JsonlStorage.OpenAsync(directory, env);
                    try
                    {
                        await use(storage);
                    }
                    finally
                    {
                        await storage.CloseAsync();
                    }
                }
                finally
                {
                    JsonlStorageFixture.Remove(directory);
                }
            },
        });

    public static IEnumerable<object[]> CaseNames() =>
        Cases().Select(@case => new object[] { @case.Name });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task PassesStorageConformanceSuite(string caseKey)
    {
        var @case = Cases().Single(candidate => candidate.Name == caseKey);
        await @case.Run();
    }
}

public class JsonlStorageAcrossReopenConformanceTests
{
    private static IReadOnlyList<StorageConformanceCase> Cases() =>
        StorageConformance.CreateStorageConformance(new StorageConformanceOptions
        {
            Assertions = ConformanceAssertions.Create(),
            WithStorage = async use =>
            {
                var (directory, env) = JsonlStorageFixture.Create();
                try
                {
                    var storage = new ReopeningStorage(
                        await JsonlStorage.OpenAsync(directory, env), directory, env);
                    try
                    {
                        await use(storage);
                    }
                    finally
                    {
                        await storage.CloseAsync();
                    }
                }
                finally
                {
                    JsonlStorageFixture.Remove(directory);
                }
            },
        });

    public static IEnumerable<object[]> CaseNames() =>
        Cases().Select(@case => new object[] { @case.Name });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task PassesStorageConformanceSuiteAcrossReopen(string caseKey)
    {
        var @case = Cases().Single(candidate => candidate.Name == caseKey);
        await @case.Run();
    }
}

public class JsonlStorageTests
{
    [Fact]
    public async Task WritesTypeScriptShapedMarkerAndSidecars()
    {
        var (directory, env) = JsonlStorageFixture.Create();
        try
        {
            var storage = await JsonlStorage.OpenAsync(directory, env);
            try
            {
                await storage.CommitAsync(
                    [new StorageWrite.Conversation(new ConversationRecord { Id = DurableIdConstants.RootConversation })]);

                var taskId = await storage.MintIdAsync<TaskId<object?>>();
                await storage.CommitAsync(
                [
                    new StorageWrite.Task(new TaskRecord
                    {
                        Id = taskId,
                        ConversationId = DurableIdConstants.RootConversation,
                        Kind = "test.task",
                        Version = 1,
                        Background = false,
                        AbortRequested = false,
                        State = new TaskState { Status = DurableTaskStatus.Pending },
                    }),
                ]);

                var documentId = await storage.MintIdAsync<DocumentId>();
                await storage.CommitAsync(
                [
                    new StorageWrite.DocumentCreateWrite(
                        new DocumentCreate
                        {
                            Id = documentId,
                            Kind = "state",
                            Scope = new DocumentScope.ConversationScope(DurableIdConstants.RootConversation),
                            History = ConversationHistory.Latest,
                        },
                        new DocumentContent.Base(1, new Dictionary<string, object?> { ["value"] = 1 })),
                ]);

                var main = await File.ReadAllTextAsync(Path.Join(directory, "main.jsonl"));
                Assert.Contains("\"format\":1", main);
                Assert.Contains("\"type\":\"commit\"", main);
                Assert.Contains("\"type\":\"conversation\"", main);
                Assert.Contains("\"type\":\"task.sidecar\"", main);
                Assert.Contains("\"type\":\"document.create\"", main);
                Assert.Contains("\"ordinal\":0", main);

                var taskSidecar = await File.ReadAllTextAsync(Path.Join(directory, $"task-{taskId.Value}.jsonl"));
                Assert.Contains("\"type\":\"record\"", taskSidecar);
                Assert.Contains("\"payload\":{\"type\":\"task\"", taskSidecar);

                var documentSidecar = await File.ReadAllTextAsync(Path.Join(directory, $"doc-{documentId.Value}.jsonl"));
                Assert.Contains("\"payload\":{\"type\":\"document\"", documentSidecar);
                Assert.Contains("\"content\":{\"kind\":\"base\",\"version\":1,\"value\":{\"value\":1}}", documentSidecar);
            }
            finally
            {
                await storage.CloseAsync();
            }
        }
        finally
        {
            JsonlStorageFixture.Remove(directory);
        }
    }

    [Fact]
    public async Task ReopenRestoresRecordsDocumentsAndIdNamespace()
    {
        var (directory, env) = JsonlStorageFixture.Create();
        try
        {
            var storage = await JsonlStorage.OpenAsync(directory, env);
            EntryId entryId = default;
            DocumentId documentId = default;
            try
            {
                await storage.CommitAsync(
                    [new StorageWrite.Conversation(new ConversationRecord { Id = DurableIdConstants.RootConversation })]);
                entryId = await storage.MintIdAsync<EntryId>();
                documentId = await storage.MintIdAsync<DocumentId>();
                await storage.CommitAsync(
                [
                    new StorageWrite.Entry(new EntryRecord
                    {
                        Id = entryId,
                        ConversationId = DurableIdConstants.RootConversation,
                        Kind = "message",
                        Data = new Dictionary<string, object?> { ["text"] = "persisted" },
                    }),
                    new StorageWrite.DocumentCreateWrite(
                        new DocumentCreate
                        {
                            Id = documentId,
                            Kind = "state",
                            Scope = new DocumentScope.ConversationScope(DurableIdConstants.RootConversation),
                            History = ConversationHistory.Rewindable,
                        },
                        new DocumentContent.Base(1, new Dictionary<string, object?> { ["value"] = 7 })),
                ]);
            }
            finally
            {
                await storage.CloseAsync();
            }

            var reopened = await JsonlStorage.OpenAsync(directory, env);
            try
            {
                var entry = await reopened.GetEntryAsync(entryId);
                Assert.NotNull(entry);
                Assert.Equal("message", entry!.Entry.Kind);

                var document = await reopened.GetDocumentAsync(documentId, new DocumentPoint.Current());
                Assert.NotNull(document);
                Assert.Equal(7L, Convert.ToInt64(document!.Value["value"]));

                var fresh = await reopened.MintIdAsync<EntryId>();
                Assert.True(fresh.Value > entryId.Value);
            }
            finally
            {
                await reopened.CloseAsync();
            }
        }
        finally
        {
            JsonlStorageFixture.Remove(directory);
        }
    }

    [Fact]
    public async Task ReopenTruncatesTornMainLineAndUnconfirmedSidecarTail()
    {
        var (directory, env) = JsonlStorageFixture.Create();
        try
        {
            DocumentId documentId = default;
            var storage = await JsonlStorage.OpenAsync(directory, env);
            try
            {
                await storage.CommitAsync(
                    [new StorageWrite.Conversation(new ConversationRecord { Id = DurableIdConstants.RootConversation })]);
                documentId = await storage.MintIdAsync<DocumentId>();
                await storage.CommitAsync(
                [
                    new StorageWrite.DocumentCreateWrite(
                        new DocumentCreate
                        {
                            Id = documentId,
                            Kind = "state",
                            Scope = new DocumentScope.ConversationScope(DurableIdConstants.RootConversation),
                            History = ConversationHistory.Latest,
                        },
                        new DocumentContent.Base(1, new Dictionary<string, object?> { ["value"] = 1 })),
                ]);
            }
            finally
            {
                await storage.CloseAsync();
            }

            var mainPath = Path.Join(directory, "main.jsonl");
            var mainBefore = await File.ReadAllTextAsync(mainPath);
            await File.AppendAllTextAsync(mainPath, "{\"format\":1,");

            var sidecarPath = Path.Join(directory, $"doc-{documentId.Value}.jsonl");
            var sidecarBefore = await File.ReadAllTextAsync(sidecarPath);
            await File.AppendAllTextAsync(
                sidecarPath,
                "{\"format\":1,\"type\":\"record\",\"seq\":99,\"ordinal\":0," +
                "\"payload\":{\"type\":\"document\",\"id\":" + documentId.Value +
                ",\"content\":{\"kind\":\"base\",\"version\":1,\"value\":{}}}}\n");

            var reopened = await JsonlStorage.OpenAsync(directory, env);
            try
            {
                Assert.Equal(mainBefore, await File.ReadAllTextAsync(mainPath));
                Assert.Equal(sidecarBefore, await File.ReadAllTextAsync(sidecarPath));
                var document = await reopened.GetDocumentAsync(documentId, new DocumentPoint.Current());
                Assert.NotNull(document);
            }
            finally
            {
                await reopened.CloseAsync();
            }
        }
        finally
        {
            JsonlStorageFixture.Remove(directory);
        }
    }

    [Fact]
    public async Task ReopenRejectsMalformedMainLog()
    {
        var (directory, env) = JsonlStorageFixture.Create();
        try
        {
            var storage = await JsonlStorage.OpenAsync(directory, env);
            await storage.CommitAsync(
                [new StorageWrite.Conversation(new ConversationRecord { Id = DurableIdConstants.RootConversation })]);
            await storage.CloseAsync();

            await File.AppendAllTextAsync(Path.Join(directory, "main.jsonl"), "{bad}\n");
            var error = await Assert.ThrowsAsync<JsonlCorruptionError>(
                () => JsonlStorage.OpenAsync(directory, env));
            Assert.Contains("Malformed complete main.jsonl", error.Message);
        }
        finally
        {
            JsonlStorageFixture.Remove(directory);
        }
    }

    [Fact]
    public async Task ReopenRejectsNonIncreasingCommitSequence()
    {
        var (directory, env) = JsonlStorageFixture.Create();
        try
        {
            const string marker = "{\"format\":1,\"type\":\"commit\",\"seq\":1,\"writes\":[]}\n";
            await File.WriteAllTextAsync(Path.Join(directory, "main.jsonl"), marker + marker);

            var error = await Assert.ThrowsAsync<JsonlCorruptionError>(
                () => JsonlStorage.OpenAsync(directory, env));
            Assert.Contains("Commit sequence does not strictly increase", error.Message);
        }
        finally
        {
            JsonlStorageFixture.Remove(directory);
        }
    }

    [Fact]
    public async Task ReclaimsRetiredCurrentOnlyDocumentSidecar()
    {
        var (directory, env) = JsonlStorageFixture.Create();
        try
        {
            var storage = await JsonlStorage.OpenAsync(directory, env);
            DocumentId documentId = default;
            try
            {
                await storage.CommitAsync(
                    [new StorageWrite.Conversation(new ConversationRecord { Id = DurableIdConstants.RootConversation })]);
                documentId = await storage.MintIdAsync<DocumentId>();
                await storage.CommitAsync(
                [
                    new StorageWrite.DocumentCreateWrite(
                        new DocumentCreate
                        {
                            Id = documentId,
                            Kind = "state",
                            Scope = new DocumentScope.ConversationScope(DurableIdConstants.RootConversation),
                            History = ConversationHistory.Latest,
                        },
                        new DocumentContent.Base(1, new Dictionary<string, object?> { ["value"] = 1 })),
                ]);
                var sidecar = Path.Join(directory, $"doc-{documentId.Value}.jsonl");
                Assert.True(File.Exists(sidecar));

                await storage.CommitAsync([new StorageWrite.DocumentRetire(documentId)]);
                Assert.False(File.Exists(sidecar));
            }
            finally
            {
                await storage.CloseAsync();
            }

            var reopened = await JsonlStorage.OpenAsync(directory, env);
            try
            {
                Assert.Null(await reopened.GetDocumentAsync(documentId, new DocumentPoint.Current()));
            }
            finally
            {
                await reopened.CloseAsync();
            }
        }
        finally
        {
            JsonlStorageFixture.Remove(directory);
        }
    }

    [Fact]
    public async Task ReclaimsTerminalTaskSidecar()
    {
        var (directory, env) = JsonlStorageFixture.Create();
        try
        {
            var storage = await JsonlStorage.OpenAsync(directory, env);
            try
            {
                await storage.CommitAsync(
                    [new StorageWrite.Conversation(new ConversationRecord { Id = DurableIdConstants.RootConversation })]);
                var taskId = await storage.MintIdAsync<TaskId<object?>>();
                await storage.CommitAsync(
                [
                    new StorageWrite.Task(new TaskRecord
                    {
                        Id = taskId,
                        ConversationId = DurableIdConstants.RootConversation,
                        Kind = "test.task",
                        Version = 1,
                        Background = false,
                        AbortRequested = false,
                        State = new TaskState { Status = DurableTaskStatus.Running },
                    }),
                ]);
                var sidecar = Path.Join(directory, $"task-{taskId.Value}.jsonl");
                Assert.True(File.Exists(sidecar));

                await storage.CommitAsync(
                [
                    new StorageWrite.Task(new TaskRecord
                    {
                        Id = taskId,
                        ConversationId = DurableIdConstants.RootConversation,
                        Kind = "test.task",
                        Version = 1,
                        Background = false,
                        AbortRequested = false,
                        State = new TaskState { Status = DurableTaskStatus.Terminal },
                    }),
                ]);
                Assert.False(File.Exists(sidecar));

                var record = await storage.GetTaskAsync(taskId);
                Assert.NotNull(record);
                Assert.Equal(DurableTaskStatus.Terminal, record!.State.Status);
            }
            finally
            {
                await storage.CloseAsync();
            }
        }
        finally
        {
            JsonlStorageFixture.Remove(directory);
        }
    }

    [Fact]
    public async Task ClosedStorageRejectsReads()
    {
        var (directory, env) = JsonlStorageFixture.Create();
        try
        {
            var storage = await JsonlStorage.OpenAsync(directory, env);
            await storage.CloseAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => storage.GetConversationAsync(DurableIdConstants.RootConversation));
        }
        finally
        {
            JsonlStorageFixture.Remove(directory);
        }
    }
}

internal static class JsonlStorageFixture
{
    public static (string Directory, LocalExecutionEnv Env) Create()
    {
        var directory = Path.Join(Path.GetTempPath(), "pi-net-jsonl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return (directory, new LocalExecutionEnv(new LocalExecutionEnv.LocalEnvOptions { Cwd = directory }));
    }

    public static void Remove(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // 保留临时目录比掩盖测试失败更安全。
        }
    }
}

/// <summary>
/// 每次提交后关闭并重开存储的包装器。对应 TS <c>jsonl-storage.test.ts</c> 的 <c>ReopeningStorage</c>。
/// </summary>
internal sealed class ReopeningStorage : IStorage
{
    private JsonlStorage current;
    private readonly string directory;
    private readonly IFileSystem fs;
    private bool closed;

    public ReopeningStorage(JsonlStorage current, string directory, IFileSystem fs)
    {
        this.current = current;
        this.directory = directory;
        this.fs = fs;
    }

    public async Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, CancellationToken signal = default)
    {
        if (closed) return await current.CommitAsync(writes, signal);
        try
        {
            return await current.CommitAsync(writes, signal);
        }
        finally
        {
            await current.CloseAsync();
            current = await JsonlStorage.OpenAsync(directory, fs);
        }
    }

    public Task<TId> MintIdAsync<TId>() where TId : struct => current.MintIdAsync<TId>();

    public Task<ConversationRecord?> GetConversationAsync(ConversationId id) => current.GetConversationAsync(id);

    public Task<Page<ConversationRecord>> ScanConversationsAsync(
        ConversationQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => current.ScanConversationsAsync(query, limit, cursor);

    public Task<StoredEntry?> GetEntryAsync(EntryId id) => current.GetEntryAsync(id);

    public Task<StoredEntry?> GetEntryAsync(ConversationId conversationId, EntryId id)
        => current.GetEntryAsync(conversationId, id);

    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId)
        => current.FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId);

    public Task<Page<EntryRecord>> ScanEntriesAsync(
        EntryQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => current.ScanEntriesAsync(query, limit, cursor);

    public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id) => current.GetTaskAsync(id);

    public Task<Page<TaskRecord>> ScanTasksAsync(
        TaskQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => current.ScanTasksAsync(query, limit, cursor);

    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id) => current.GetSubmissionAsync(id);

    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(
        SubmissionQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => current.ScanSubmissionsAsync(query, limit, cursor);

    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId)
        => current.GetSubmissionByRequestAsync(conversationId, requestId);

    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at)
        => current.FindDocumentAsync(address, at);

    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at)
        => current.GetDocumentAsync(id, at);

    public Task<Page<DocumentRecord>> ScanDocumentsAsync(
        DocumentQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => current.ScanDocumentsAsync(query, limit, cursor);

    public Task SubscribeAsync(Func<CommitPublication, Task> listener, CancellationToken signal = default)
        => current.SubscribeAsync(listener, signal);

    public Task UnsubscribeAsync(Func<CommitPublication, Task> listener) => current.UnsubscribeAsync(listener);

    public Task<MemoryState?> AttachDocumentAsync(DocumentId id, Func<Context, Task>? listener = null)
        => current.AttachDocumentAsync(id, listener);

    public async Task CloseAsync()
    {
        if (closed) return;
        closed = true;
        await current.CloseAsync();
    }
}
