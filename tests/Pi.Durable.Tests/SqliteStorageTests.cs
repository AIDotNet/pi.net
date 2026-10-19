using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pi.Durable.Storage;
using Pi.Durable.Testing;
using Pi.Durable.Types;
using DurableTaskStatus = Pi.Durable.Types.TaskStatus;
using DeltaOp = Pi.Chord.Delta.DeltaOp;
using DeltaPath = Pi.Chord.Delta.Path;
using Seg = Pi.Chord.Delta.Seg;
using Xunit;

namespace Pi.Durable.Tests;

public class SqliteStorageConformanceTests
{
    private static IReadOnlyList<StorageConformanceCase> Cases() =>
        StorageConformance.CreateStorageConformance(new StorageConformanceOptions
        {
            Assertions = ConformanceAssertions.Create(),
            WithStorage = async use =>
            {
                await using var storage = await SqliteStorage.OpenAsync(":memory:");
                await use(storage);
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

public class SqliteStorageTests
{
    [Fact]
    public async Task PersistsRecordsSequencesAndIdsAcrossReopenWithTypeScriptJsonShapes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-durable-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "storage.sqlite");
        SqliteStorage? storage = null;
        SqliteStorage? reopened = null;
        try
        {
            storage = await SqliteStorage.OpenAsync(path);
            Assert.Equal(Seq.From(1), await storage.CommitAsync(
                [new StorageWrite.Conversation(new ConversationRecord { Id = DurableIdConstants.RootConversation })]));
            var entryId = await storage.MintIdAsync<EntryId>();
            var taskId = await storage.MintIdAsync<TaskId<object?>>();
            var submissionId = await storage.MintIdAsync<SubmissionId>();
            var documentId = await storage.MintIdAsync<DocumentId>();
            Assert.Equal(Seq.From(2), await storage.CommitAsync(
                [
                    new StorageWrite.Entry(new EntryRecord
                    {
                        Id = entryId,
                        ConversationId = DurableIdConstants.RootConversation,
                        Kind = "message",
                        Data = new Dictionary<string, object?> { ["text"] = "persisted" },
                    }),
                    new StorageWrite.Task(new TaskRecord
                    {
                        Id = taskId,
                        ConversationId = DurableIdConstants.RootConversation,
                        Kind = "test.task",
                        Version = 1,
                        Input = null,
                        State = new TaskState { Status = DurableTaskStatus.Pending, Checkpoint = new Dictionary<string, object?> { ["phase"] = "ready" } },
                        Background = false,
                        AbortRequested = false,
                    }),
                    new StorageWrite.Submission(new SubmissionRecord.InputRecord
                    {
                        Id = submissionId,
                        ConversationId = DurableIdConstants.RootConversation,
                        Status = SubmissionStatus.Queued,
                        RequestId = "request-1",
                    }),
                    new StorageWrite.DocumentCreateWrite(
                        new DocumentCreate
                        {
                            Id = documentId,
                            Kind = "state",
                            Scope = new DocumentScope.ConversationScope(DurableIdConstants.RootConversation),
                            History = ConversationHistory.Rewindable,
                            Fork = ConversationFork.AsOf,
                        },
                        new DocumentContent.Base(
                            1,
                            new Dictionary<string, object?> { ["count"] = 7L })),
                ]));

            await using (var inspector = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString()))
            {
                await inspector.OpenAsync();
                var taskJson = await ReadJsonAsync(inspector, "SELECT record FROM tasks WHERE id = $id", taskId.Value);
                Assert.Equal(JsonValueKind.Number, taskJson.GetProperty("id").ValueKind);
                Assert.Equal("pending", taskJson.GetProperty("state").GetProperty("status").GetString());
                Assert.Equal("\"test.task\"", await ReadStringAsync(inspector, "SELECT kind FROM tasks WHERE id = $id", taskId.Value));

                var submissionJson = await ReadJsonAsync(inspector, "SELECT record FROM submissions WHERE id = $id", submissionId.Value);
                Assert.Equal("input", submissionJson.GetProperty("type").GetString());
                Assert.Equal("queued", submissionJson.GetProperty("status").GetString());

                var documentJson = await ReadJsonAsync(inspector, "SELECT record FROM documents WHERE id = $id", documentId.Value);
                Assert.Equal(JsonValueKind.Number, documentJson.GetProperty("id").ValueKind);
                Assert.Equal("conversation", documentJson.GetProperty("scope").GetProperty("kind").GetString());
                Assert.Equal((long)DurableIdConstants.RootConversation, documentJson.GetProperty("scope").GetProperty("conversationId").GetInt64());
                Assert.Equal("rewindable", documentJson.GetProperty("history").GetString());
                Assert.Equal("asOf", documentJson.GetProperty("fork").GetString());
            }

            await storage.CloseAsync();
            storage = null;
            reopened = await SqliteStorage.OpenAsync(path);
            Assert.Equal(entryId, (await reopened.GetEntryAsync(entryId))!.Entry.Id);
            Assert.Equal(taskId, (await reopened.GetTaskAsync(taskId))!.Id);
            Assert.Equal(submissionId, (await reopened.GetSubmissionByRequestAsync(
                DurableIdConstants.RootConversation, "request-1"))!.Id);
            var persistedCount = (await reopened.GetDocumentAsync(documentId, new DocumentPoint.Current()))!
                .Value["count"];
            Assert.IsType<long>(persistedCount);
            Assert.Equal(7L, persistedCount);
            Assert.Equal(Seq.From(3), await reopened.CommitAsync([]));
            Assert.Equal(DocumentId.From(documentId.Value + 1), await reopened.MintIdAsync<DocumentId>());
        }
        finally
        {
            if (reopened is not null) await reopened.CloseAsync();
            if (storage is not null) await storage.CloseAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsMissingMetadataOnReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-durable-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "storage.sqlite");
        try
        {
            var storage = await SqliteStorage.OpenAsync(path);
            await storage.CloseAsync();
            await using (var database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString()))
            {
                await database.OpenAsync();
                await using var command = database.CreateCommand();
                command.CommandText = "DELETE FROM durable_metadata";
                await command.ExecuteNonQueryAsync();
            }
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteStorage.OpenAsync(path));
            Assert.Contains("Durable SQLite metadata is missing", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RollsBackInitialMigrationWhenBootstrapFailsAndCanRetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-durable-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "storage.sqlite");
        try
        {
            await using (var conflict = CreateUnpooledConnection(path))
            {
                await conflict.OpenAsync();
                await using var command = conflict.CreateCommand();
                command.CommandText = "CREATE TABLE durable_metadata (sentinel INTEGER)";
                await command.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsAsync<SqliteException>(() => SqliteStorage.OpenAsync(path));

            await using (var database = CreateUnpooledConnection(path))
            {
                await database.OpenAsync();
                Assert.Equal(0L, await ReadInt64Async(
                    database,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'durable_schema'"));
                await using var command = database.CreateCommand();
                command.CommandText = "DROP TABLE durable_metadata";
                await command.ExecuteNonQueryAsync();
            }

            await using var recovered = await SqliteStorage.OpenAsync(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsDatabaseSchemaNewerThanSupportedVersion()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-durable-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "storage.sqlite");
        SqliteStorage? storage = null;
        try
        {
            storage = await SqliteStorage.OpenAsync(path);
            await storage.CloseAsync();
            storage = null;
            await using (var database = CreateUnpooledConnection(path))
            {
                await database.OpenAsync();
                await using var command = database.CreateCommand();
                command.CommandText = "UPDATE durable_schema SET version = 999 WHERE singleton = 1";
                await command.ExecuteNonQueryAsync();
            }

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteStorage.OpenAsync(path));
            Assert.Contains("newer than supported version", error.Message);
        }
        finally
        {
            if (storage is not null) await storage.CloseAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsDocumentWhoseRequiredBaseIsMissing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-durable-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "storage.sqlite");
        SqliteStorage? storage = null;
        try
        {
            storage = await SqliteStorage.OpenAsync(path);
            await storage.CommitAsync(
                [new StorageWrite.Conversation(new ConversationRecord { Id = DurableIdConstants.RootConversation })]);
            var id = await storage.MintIdAsync<DocumentId>();
            await storage.CommitAsync(
            [
                new StorageWrite.DocumentCreateWrite(
                    new DocumentCreate
                    {
                        Id = id,
                        Kind = "corrupt",
                        Scope = new DocumentScope.SessionScope(),
                    },
                    new DocumentContent.Base(1, new Dictionary<string, object?> { ["retained"] = true })),
            ]);

            await using (var database = CreateUnpooledConnection(path))
            {
                await database.OpenAsync();
                await using var command = database.CreateCommand();
                command.CommandText = "DELETE FROM document_revisions WHERE document_id = $id";
                command.Parameters.AddWithValue("$id", id.Value);
                await command.ExecuteNonQueryAsync();
            }

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => storage.GetDocumentAsync(id, new DocumentPoint.Current()));
            Assert.Contains($"Document {id.Value} is missing a required base", error.Message);
        }
        finally
        {
            if (storage is not null) await storage.CloseAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReconstructsRewindablePointsAfterReopen()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-durable-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "storage.sqlite");
        SqliteStorage? storage = null;
        try
        {
            storage = await SqliteStorage.OpenAsync(path);
            await storage.CommitAsync(
                [new StorageWrite.Conversation(new ConversationRecord { Id = DurableIdConstants.RootConversation })]);
            var id = await storage.MintIdAsync<DocumentId>();
            var createAt = await storage.CommitAsync(
            [
                new StorageWrite.DocumentCreateWrite(
                    new DocumentCreate
                    {
                        Id = id,
                        Kind = "history",
                        Scope = new DocumentScope.ConversationScope(DurableIdConstants.RootConversation),
                        History = ConversationHistory.Rewindable,
                        Fork = ConversationFork.AsOf,
                    },
                    new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 0L })),
            ]);

            var ancientAt = createAt;
            var recentAt = createAt;
            for (var count = 1; count <= 40; count++)
            {
                DocumentContent content = count == 20
                    ? new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = (long)count })
                    : new DocumentContent.Delta(
                        1,
                        [new DeltaOp.Set(new DeltaPath([Seg.Key("count")]), (long)count)]);
                recentAt = await storage.CommitAsync([new StorageWrite.DocumentChange(id, content)]);
                if (count == 5) ancientAt = recentAt;
            }

            await storage.CloseAsync();
            storage = await SqliteStorage.OpenAsync(path);
            var ancient = await storage.GetDocumentAsync(id, new DocumentPoint.AtSeq(ancientAt));
            var recent = await storage.GetDocumentAsync(id, new DocumentPoint.AtSeq(recentAt));
            Assert.Equal(5L, ancient!.Value["count"]);
            Assert.Equal(40L, recent!.Value["count"]);
        }
        finally
        {
            if (storage is not null) await storage.CloseAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReclaimsCurrentOnlyRevisionsAfterBasesAndRetirement()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-durable-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "storage.sqlite");
        SqliteStorage? storage = null;
        try
        {
            storage = await SqliteStorage.OpenAsync(path);
            await storage.CommitAsync(
                [new StorageWrite.Conversation(new ConversationRecord { Id = DurableIdConstants.RootConversation })]);
            var id = await storage.MintIdAsync<DocumentId>();
            await storage.CommitAsync(
            [
                new StorageWrite.DocumentCreateWrite(
                    new DocumentCreate
                    {
                        Id = id,
                        Kind = "latest",
                        Scope = new DocumentScope.SessionScope(),
                    },
                    new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 0L })),
            ]);

            await using var database = CreateUnpooledConnection(path);
            await database.OpenAsync();
            for (var count = 1; count <= 10; count++)
            {
                await storage.CommitAsync(
                [
                    new StorageWrite.DocumentChange(
                        id,
                        new DocumentContent.Delta(
                            1,
                            [new DeltaOp.Set(new DeltaPath([Seg.Key("count")]), (long)count)])),
                ]);
            }
            Assert.Equal(11L, await ReadInt64Async(
                database,
                "SELECT COUNT(*) FROM document_revisions WHERE document_id = $id",
                id.Value));

            await storage.CommitAsync(
            [
                new StorageWrite.DocumentChange(
                    id,
                    new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 11L })),
            ]);
            Assert.Equal(1L, await ReadInt64Async(
                database,
                "SELECT COUNT(*) FROM document_revisions WHERE document_id = $id",
                id.Value));

            await storage.CommitAsync(
            [
                new StorageWrite.DocumentChange(
                    id,
                    new DocumentContent.Delta(
                        1,
                        [new DeltaOp.Replace(new Dictionary<string, object?> { ["count"] = 12L })])),
            ]);
            Assert.Equal(2L, await ReadInt64Async(
                database,
                "SELECT COUNT(*) FROM document_revisions WHERE document_id = $id",
                id.Value));
            await storage.CommitAsync([new StorageWrite.DocumentRetire(id)]);
            Assert.Equal(0L, await ReadInt64Async(
                database,
                "SELECT COUNT(*) FROM document_revisions WHERE document_id = $id",
                id.Value));
        }
        finally
        {
            if (storage is not null) await storage.CloseAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AutoCheckpointsWalFramesAndTruncatesTheWalOnClose()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pi-durable-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "storage.sqlite");
        var walPath = $"{path}-wal";
        SqliteStorage? storage = null;
        try
        {
            storage = await SqliteStorage.OpenAsync(path, new SqliteStorageOptions { WalAutoCheckpointPages = 1 });
            await storage.CommitAsync(
                [new StorageWrite.Conversation(new ConversationRecord { Id = DurableIdConstants.RootConversation })]);
            for (var index = 0; index < 20; index++)
            {
                var id = await storage.MintIdAsync<EntryId>();
                await storage.CommitAsync(
                [
                    new StorageWrite.Entry(new EntryRecord
                    {
                        Id = id,
                        ConversationId = DurableIdConstants.RootConversation,
                        Kind = "message",
                        Data = new Dictionary<string, object?>
                        {
                            ["text"] = new string('x', 32 * 1024),
                            ["index"] = index,
                        },
                    }),
                ]);
            }

            Assert.True(File.Exists(walPath));
            Assert.True(new FileInfo(walPath).Length < 512 * 1024);
            await using var observer = CreateUnpooledConnection(path);
            await observer.OpenAsync();
            Assert.Equal(20L, await ReadInt64Async(observer, "SELECT COUNT(*) FROM entries"));
            await storage.CloseAsync();
            storage = null;
            Assert.Equal(0L, File.Exists(walPath) ? new FileInfo(walPath).Length : 0L);
        }
        finally
        {
            if (storage is not null) await storage.CloseAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SqliteConnection CreateUnpooledConnection(string path)
        => new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());

    private static async Task<long> ReadInt64Async(SqliteConnection connection, string sql, long? id = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is { } idValue) command.Parameters.AddWithValue("$id", idValue);
        var result = await command.ExecuteScalarAsync();
        return result switch
        {
            long longValue => longValue,
            int intValue => intValue,
            _ => throw new InvalidOperationException(
                $"Expected an integer SQLite result, received {result?.GetType().Name ?? "null"}"),
        };
    }

    private static async Task<JsonElement> ReadJsonAsync(SqliteConnection connection, string sql, long id)
    {
        var text = await ReadStringAsync(connection, sql, id);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<string> ReadStringAsync(SqliteConnection connection, string sql, long id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
