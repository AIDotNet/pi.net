using Pi.Durable.Env;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using DurableTaskStatus = Pi.Durable.Types.TaskStatus;
using Xunit;

namespace Pi.Durable.Tests;

// ============================================================================
// v1.5.1 #11939 read-only JSONL storage open（上游 f5f4a71af）。
// 只读打开：不建目录、恢复只在内存跳过撕裂末行与未确认侧车尾、不截断/不回收，
// commit() 抛 JsonlStorageReadOnlyError，持有打开时已提交的状态。
// 对照上游 jsonl-storage.test.ts / harness-read-only.test.ts 的行为。
// ============================================================================

public class JsonlStorageReadOnlyTests
{
    private static async Task<(string Directory, LocalExecutionEnv Env, DocumentId DocumentId)> SeedAsync()
    {
        var (directory, env) = JsonlStorageFixture.Create();
        var storage = await JsonlStorage.OpenAsync(directory, env);
        try
        {
            await storage.CommitAsync(
                [new StorageWrite.Conversation(new ConversationRecord { Id = DurableIdConstants.RootConversation })]);
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
            return (directory, env, documentId);
        }
        finally
        {
            await storage.CloseAsync();
        }
    }

    [Fact]
    public async Task ReadOnlyOpen_ReadsCommittedStateWithoutCreatingDirectory()
    {
        var (directory, env, documentId) = await SeedAsync();
        try
        {
            var before = await File.ReadAllTextAsync(Path.Join(directory, "main.jsonl"));
            var storage = await JsonlStorage.OpenAsync(directory, env, new JsonlStorageOptions { ReadOnly = true });
            try
            {
                // 只读打开持有已提交状态。
                var document = await storage.GetDocumentAsync(documentId, new DocumentPoint.Current());
                Assert.NotNull(document);
                // 目录没被重写（main.jsonl 逐字不变）。
                Assert.Equal(before, await File.ReadAllTextAsync(Path.Join(directory, "main.jsonl")));
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
    public async Task ReadOnlyOpen_RejectsCommitWithReadOnlyError()
    {
        var (directory, env, _) = await SeedAsync();
        try
        {
            var storage = await JsonlStorage.OpenAsync(directory, env, new JsonlStorageOptions { ReadOnly = true });
            try
            {
                await Assert.ThrowsAsync<JsonlStorageReadOnlyError>(() => storage.CommitAsync(
                    [new StorageWrite.Task(new TaskRecord
                    {
                        Id = default,
                        ConversationId = DurableIdConstants.RootConversation,
                        Kind = "test.task",
                        Version = 1,
                        Background = false,
                        AbortRequested = false,
                        State = new TaskState { Status = DurableTaskStatus.Pending },
                    })]));
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
    public async Task ReadOnlyOpen_CreatesNoMissingDirectory()
    {
        // 对照上游 "creates no missing directory"：只读打开不存在的目录不建目录，
        // 恢复时 listDir 失败抛 "directory listing"，且目录依旧不存在。
        var directory = Path.Join(Path.GetTempPath(), "pi-net-jsonl-ro-" + Guid.NewGuid().ToString("N"));
        var env = new LocalExecutionEnv(new LocalExecutionEnv.LocalEnvOptions { Cwd = directory });
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                JsonlStorage.OpenAsync(directory, env, new JsonlStorageOptions { ReadOnly = true }));
            Assert.Contains("directory listing", error.Message);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadOnlyOpen_SkipsTornLineInMemoryWithoutTruncatingFiles()
    {
        var (directory, env, documentId) = await SeedAsync();
        try
        {
            var mainPath = Path.Join(directory, "main.jsonl");
            var sidecarPath = Path.Join(directory, $"doc-{documentId.Value}.jsonl");
            // 追加一个撕裂的末行（未以 \n 结束）与一个未确认的侧车尾记录。
            await File.AppendAllTextAsync(mainPath, "{\"format\":1,");
            await File.AppendAllTextAsync(
                sidecarPath,
                "{\"format\":1,\"type\":\"record\",\"seq\":99,\"ordinal\":0," +
                "\"payload\":{\"type\":\"document\",\"id\":" + documentId.Value +
                ",\"content\":{\"kind\":\"base\",\"version\":1,\"value\":{}}}}\n");
            var mainWithTorn = await File.ReadAllTextAsync(mainPath);
            var sidecarWithTail = await File.ReadAllTextAsync(sidecarPath);

            var storage = await JsonlStorage.OpenAsync(directory, env, new JsonlStorageOptions { ReadOnly = true });
            try
            {
                // 磁盘文件逐字不动（不截断撕裂末行、不截断未确认侧车尾、不回收）。
                Assert.Equal(mainWithTorn, await File.ReadAllTextAsync(mainPath));
                Assert.Equal(sidecarWithTail, await File.ReadAllTextAsync(sidecarPath));
                // 但内存里已提交状态可读（跳过撕裂末行与未确认尾）。
                var document = await storage.GetDocumentAsync(documentId, new DocumentPoint.Current());
                Assert.NotNull(document);
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
    public async Task ReadOnlyOpen_KeepsATornOnlyMainFile()
    {
        // 对照上游 "keeps a torn-only main file"：main.jsonl 只有一条撕裂行时，
        // 只读打开视为空状态且不改动文件（size 保持不变）。
        var directory = Path.Join(Path.GetTempPath(), "pi-net-jsonl-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var mainPath = Path.Join(directory, "main.jsonl");
        var torn = "{\"format\":1,\"type\":\"commit\"";
        await File.WriteAllTextAsync(mainPath, torn);
        var env = new LocalExecutionEnv(new LocalExecutionEnv.LocalEnvOptions { Cwd = directory });
        try
        {
            var storage = await JsonlStorage.OpenAsync(directory, env, new JsonlStorageOptions { ReadOnly = true });
            try
            {
                Assert.Null(await storage.GetConversationAsync(DurableIdConstants.RootConversation));
                Assert.Equal(torn, await File.ReadAllTextAsync(mainPath));
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
    public async Task WritableOpen_StillTruncatesTornLine()
    {
        // 对照：可写打开仍会截断撕裂末行（保证 #11939 没有回归旧行为）。
        var (directory, env, _) = await SeedAsync();
        try
        {
            var mainPath = Path.Join(directory, "main.jsonl");
            var before = await File.ReadAllTextAsync(mainPath);
            await File.AppendAllTextAsync(mainPath, "{\"format\":1,");

            var storage = await JsonlStorage.OpenAsync(directory, env);
            try
            {
                Assert.Equal(before, await File.ReadAllTextAsync(mainPath));
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
}
