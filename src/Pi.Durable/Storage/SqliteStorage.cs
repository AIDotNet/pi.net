using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Types;
using DurableTaskStatus = Pi.Durable.Types.TaskStatus;

namespace Pi.Durable.Storage;

/// <summary>SQLite connection settings for durable storage.</summary>
public sealed record SqliteStorageOptions
{
    /// <summary>WAL auto-checkpoint threshold in pages; zero disables auto-checkpointing.</summary>
    public int WalAutoCheckpointPages { get; init; } = 1_000;

    /// <summary>Milliseconds SQLite waits for a competing file lock.</summary>
    public int BusyTimeoutMs { get; init; } = 5_000;

    internal void Validate()
    {
        if (WalAutoCheckpointPages < 0) throw new ArgumentOutOfRangeException(nameof(WalAutoCheckpointPages));
        if (BusyTimeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(BusyTimeoutMs));
    }
}

/// <summary>SQLite-backed durable storage. Each instance owns one connection and serializes its operations.</summary>
public sealed class SqliteStorage : IStorage, IAsyncDisposable
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;

    private readonly SqliteConnection _connection;
    private readonly object _queueGate = new();
    private readonly object _publicationGate = new();
    private readonly List<Func<CommitPublication, Task>> _publications = [];
    private readonly Dictionary<long, MemoryState> _attachedStates = [];
    private Task _queueTail = Task.CompletedTask;
    private Task? _closeTask;
    private long _nextId;
    private bool _closed;

    private SqliteStorage(SqliteConnection connection, long nextId)
        => (_connection, _nextId) = (connection, nextId);

    /// <summary>Open or create a file-backed database, or use <c>:memory:</c> for an ephemeral database.</summary>
    public static async Task<SqliteStorage> OpenAsync(
        string path,
        SqliteStorageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new SqliteStorageOptions();
        options.Validate();
        if (path != ":memory:")
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = (int)Math.Ceiling(options.BusyTimeoutMs / 1_000d),
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var executor = new SqliteCommandExecutor(connection);
            await executor.ExecuteAsync(
                $"PRAGMA busy_timeout = {options.BusyTimeoutMs.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken).ConfigureAwait(false);
            await executor.ExecuteAsync("PRAGMA journal_mode = WAL", cancellationToken).ConfigureAwait(false);
            await executor.ExecuteAsync("PRAGMA synchronous = NORMAL", cancellationToken).ConfigureAwait(false);
            await executor.ExecuteAsync(
                $"PRAGMA wal_autocheckpoint = {options.WalAutoCheckpointPages.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken).ConfigureAwait(false);

            await SqliteStorageMigrations.ApplyAsync(connection, cancellationToken).ConfigureAwait(false);
            var metadata = await executor.GetAsync(
                "SELECT next_id, next_seq FROM durable_metadata WHERE singleton = 1",
                reader => new MetadataRow(reader.GetString(0), reader.GetInt64(1)),
                cancellationToken).ConfigureAwait(false);
            if (metadata is null) throw new InvalidOperationException("Durable SQLite metadata is missing");
            if (!long.TryParse(metadata.NextId, NumberStyles.None, CultureInfo.InvariantCulture, out var nextId))
                throw new InvalidOperationException("Durable SQLite next_id metadata is invalid");
            return new SqliteStorage(connection, nextId);
        }
        catch (Exception failure)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception closeFailure)
            {
                throw new AggregateException("SQLite storage initialization and connection cleanup failed", failure, closeFailure);
            }
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, CancellationToken signal = default)
        => EnqueueAsync(() => CommitCoreAsync(writes, signal));

    /// <inheritdoc />
    public Task<TId> MintIdAsync<TId>() where TId : struct
        => EnqueueAsync(() =>
        {
            if (_nextId < -MaximumSafeInteger || _nextId > MaximumSafeInteger)
                throw new InvalidOperationException("ID space is exhausted");
            var id = CreateId<TId>(_nextId);
            _nextId++;
            return Task.FromResult(id);
        });

    /// <inheritdoc />
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id)
        => EnqueueAsync(() => ReadConversationAsync(new SqliteCommandExecutor(_connection), id));

    /// <inheritdoc />
    public Task<Page<ConversationRecord>> ScanConversationsAsync(
        ConversationQuery? query,
        int limit,
        IReadOnlyDictionary<string, object?>? cursor = null)
        => EnqueueAsync(async () =>
        {
            var start = StorageScan.Start(query?.Order, cursor, ScanOrder.Ascending);
            var scan = ScanCondition(start);
            var clauses = new List<string> { scan.Clause };
            var parameters = new List<object?> { scan.Parameter };
            if (query?.OwnerConversationId is { } ownerConversation)
            {
                clauses.Add("owner_conversation_id = ?");
                parameters.Add(ownerConversation.Value);
            }
            if (query?.OwnerTaskId is { } ownerTask)
            {
                clauses.Add("owner_task_id = ?");
                parameters.Add(ownerTask.Value);
            }
            parameters.Add(limit + 1);
            var rows = await new SqliteCommandExecutor(_connection).AllAsync(
                $"SELECT record FROM conversations WHERE {string.Join(" AND ", clauses)} ORDER BY id {scan.Direction} LIMIT ?",
                reader => new JsonRow(reader.GetString(0)),
                values: parameters.ToArray()).ConfigureAwait(false);
            var records = rows.Select(row => SqliteStorageJson.Deserialize<ConversationRecord>(row.Record)).ToList();
            return StorageScan.CreatePage(records, limit, start.Order);
        });

    /// <inheritdoc />
    public Task<StoredEntry?> GetEntryAsync(EntryId id)
        => EnqueueAsync(() => ReadEntryAsync(new SqliteCommandExecutor(_connection), id));

    /// <inheritdoc />
    public Task<StoredEntry?> GetEntryAsync(ConversationId conversationId, EntryId id)
        => EnqueueAsync(() => ReadVisibleEntryAsync(new SqliteCommandExecutor(_connection), conversationId, id));

    /// <inheritdoc />
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId)
        => EnqueueAsync(() => ReadLatestHeadMarkerAsync(
            new SqliteCommandExecutor(_connection), conversationId, atOrBeforeEntryId));

    /// <inheritdoc />
    public Task<Page<EntryRecord>> ScanEntriesAsync(
        EntryQuery query,
        int limit,
        IReadOnlyDictionary<string, object?>? cursor = null)
        => EnqueueAsync(() => ScanEntriesCoreAsync(query, limit, cursor));

    /// <inheritdoc />
    public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id)
        => EnqueueAsync(async () =>
        {
            var row = await new SqliteCommandExecutor(_connection).GetAsync(
                "SELECT record FROM tasks WHERE id = ?",
                reader => new JsonRow(reader.GetString(0)),
                values: [id.Value]).ConfigureAwait(false);
            return row is null ? null : SqliteStorageJson.Deserialize<TaskRecord>(row.Record);
        });

    /// <inheritdoc />
    public Task<Page<TaskRecord>> ScanTasksAsync(
        TaskQuery? query,
        int limit,
        IReadOnlyDictionary<string, object?>? cursor = null)
        => EnqueueAsync(async () =>
        {
            var start = StorageScan.Start(query?.Order, cursor, ScanOrder.Ascending);
            var scan = ScanCondition(start);
            var clauses = new List<string> { scan.Clause };
            var parameters = new List<object?> { scan.Parameter };
            if (query?.ConversationId is { } conversation)
            {
                clauses.Add("conversation_id = ?");
                parameters.Add(conversation.Value);
            }
            if (query?.Kind is { } kind)
            {
                clauses.Add("kind = ?");
                parameters.Add(SqliteStorageJson.EncodeIndexedString(kind));
            }
            if (query?.Status is { } status)
            {
                clauses.Add("status = ?");
                parameters.Add(EnumWireName(status));
            }
            if (query?.AbortRequested is { } abortRequested)
            {
                clauses.Add("abort_requested = ?");
                parameters.Add(abortRequested ? 1 : 0);
            }
            if (query?.Background is { } background)
            {
                clauses.Add("background = ?");
                parameters.Add(background ? 1 : 0);
            }
            parameters.Add(limit + 1);
            var rows = await new SqliteCommandExecutor(_connection).AllAsync(
                $"SELECT record FROM tasks WHERE {string.Join(" AND ", clauses)} ORDER BY id {scan.Direction} LIMIT ?",
                reader => new JsonRow(reader.GetString(0)),
                values: parameters.ToArray()).ConfigureAwait(false);
            var records = rows.Select(row => SqliteStorageJson.Deserialize<TaskRecord>(row.Record)).ToList();
            return StorageScan.CreatePage(records, limit, start.Order);
        });

    /// <inheritdoc />
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id)
        => EnqueueAsync(async () =>
        {
            var row = await new SqliteCommandExecutor(_connection).GetAsync(
                "SELECT record FROM submissions WHERE id = ?",
                reader => new JsonRow(reader.GetString(0)),
                values: [id.Value]).ConfigureAwait(false);
            return row is null ? null : SqliteStorageJson.Deserialize<SubmissionRecord>(row.Record);
        });

    /// <inheritdoc />
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(
        SubmissionQuery? query,
        int limit,
        IReadOnlyDictionary<string, object?>? cursor = null)
        => EnqueueAsync(async () =>
        {
            var start = StorageScan.Start(query?.Order, cursor, ScanOrder.Ascending);
            var scan = ScanCondition(start);
            var clauses = new List<string> { scan.Clause };
            var parameters = new List<object?> { scan.Parameter };
            if (query?.ConversationId is { } conversation)
            {
                clauses.Add("conversation_id = ?");
                parameters.Add(conversation.Value);
            }
            if (query?.Status is { } status)
            {
                clauses.Add("status = ?");
                parameters.Add(EnumWireName(status));
            }
            parameters.Add(limit + 1);
            var rows = await new SqliteCommandExecutor(_connection).AllAsync(
                $"SELECT record FROM submissions WHERE {string.Join(" AND ", clauses)} ORDER BY id {scan.Direction} LIMIT ?",
                reader => new JsonRow(reader.GetString(0)),
                values: parameters.ToArray()).ConfigureAwait(false);
            var records = rows.Select(row => SqliteStorageJson.Deserialize<SubmissionRecord>(row.Record)).ToList();
            return StorageScan.CreatePage(records, limit, start.Order);
        });

    /// <inheritdoc />
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId)
        => EnqueueAsync(async () =>
        {
            var row = await new SqliteCommandExecutor(_connection).GetAsync(
                "SELECT record FROM submissions WHERE conversation_id = ? AND request_id = ?",
                reader => new JsonRow(reader.GetString(0)),
                values: [conversationId.Value, SqliteStorageJson.EncodeIndexedString(requestId)])
                .ConfigureAwait(false);
            return row is null ? null : SqliteStorageJson.Deserialize<SubmissionRecord>(row.Record);
        });

    /// <inheritdoc />
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at)
        => EnqueueAsync(() => FindDocumentCoreAsync(new SqliteCommandExecutor(_connection), address, at));

    /// <inheritdoc />
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at)
        => EnqueueAsync(() => InTransactionAsync(
            executor => MaterializeDocumentAsync(executor, id, at),
            CancellationToken.None));

    /// <inheritdoc />
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(
        DocumentQuery query,
        int limit,
        IReadOnlyDictionary<string, object?>? cursor = null)
        => EnqueueAsync(async () =>
        {
            var scope = GetScopeColumns(query.Scope);
            var clauses = new List<string> { "scope_kind = ?", "owner_id = ?", "id > ?" };
            var parameters = new List<object?> { scope.Kind, scope.OwnerId, StorageScan.CursorAfter(cursor) ?? -1 };
            if (query.Kind is { } kind)
            {
                clauses.Add("kind = ?");
                parameters.Add(SqliteStorageJson.EncodeIndexedString(kind));
            }
            if (query.At is DocumentPoint.Current)
            {
                clauses.Add("retired_at IS NULL");
            }
            else if (query.At is DocumentPoint.AtSeq atSeq)
            {
                clauses.Add("created_at <= ?");
                clauses.Add("(retired_at IS NULL OR retired_at > ?)");
                parameters.Add(atSeq.Seq.Value);
                parameters.Add(atSeq.Seq.Value);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(query), "Unknown document point");
            }
            parameters.Add(limit + 1);
            var rows = await new SqliteCommandExecutor(_connection).AllAsync(
                $"SELECT record FROM documents WHERE {string.Join(" AND ", clauses)} ORDER BY id LIMIT ?",
                reader => new JsonRow(reader.GetString(0)),
                values: parameters.ToArray()).ConfigureAwait(false);
            var records = rows.Select(row => SqliteStorageJson.Deserialize<DocumentRecord>(row.Record)).ToList();
            return StorageScan.CreatePage(records, limit);
        });

    /// <inheritdoc />
    public Task SubscribeAsync(Func<CommitPublication, Task> listener, CancellationToken signal = default)
        => EnqueueAsync(() =>
        {
            signal.ThrowIfCancellationRequested();
            lock (_publicationGate) _publications.Add(listener);
            return Task.CompletedTask;
        });

    /// <inheritdoc />
    public Task UnsubscribeAsync(Func<CommitPublication, Task> listener)
    {
        lock (_publicationGate) _publications.Remove(listener);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<MemoryState?> AttachDocumentAsync(DocumentId id, Func<Context, Task>? listener = null)
        => EnqueueAsync(async () =>
        {
            if (_attachedStates.TryGetValue(id.Value, out var attached))
            {
                if (listener is not null) attached.Start(listener);
                return attached;
            }
            var stored = await InTransactionAsync(
                executor => MaterializeDocumentAsync(executor, id, new DocumentPoint.Current()),
                CancellationToken.None).ConfigureAwait(false);
            if (stored is null) return null;
            var state = new MemoryState(CloneJson(stored.Value));
            _attachedStates.Add(id.Value, state);
            if (listener is not null) state.Start(listener);
            return state;
        });

    /// <inheritdoc />
    public Task CloseAsync()
    {
        lock (_queueGate)
        {
            if (_closeTask is not null) return _closeTask;
            _closed = true;
            _closeTask = Schedule(_queueTail, CloseDatabaseAsync);
            _queueTail = Settled(_closeTask);
            return _closeTask;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private async Task<Seq> CommitCoreAsync(IReadOnlyList<StorageWrite> writes, CancellationToken signal)
    {
        signal.ThrowIfCancellationRequested();
        var detachedWrites = CloneWrites(writes);
        var actions = PrepareDocumentActions(detachedWrites);
        var candidateNextId = CandidateNextId(detachedWrites);
        var (seq, publication) = await InTransactionAsync(async executor =>
        {
            var metadata = await executor.GetAsync(
                "SELECT next_id, next_seq FROM durable_metadata WHERE singleton = 1",
                reader => new MetadataRow(reader.GetString(0), reader.GetInt64(1)),
                signal).ConfigureAwait(false);
            if (metadata is null) throw new InvalidOperationException("Durable SQLite metadata is missing");
            if (!long.TryParse(metadata.NextId, NumberStyles.None, CultureInfo.InvariantCulture, out var storedNextId))
                throw new InvalidOperationException("Durable SQLite next_id metadata is invalid");

            var seq = Seq.From(metadata.NextSeq);
            await CheckGlobalIdsAsync(executor, detachedWrites, signal).ConfigureAwait(false);
            await CheckDocumentActionsAsync(executor, actions, signal).ConfigureAwait(false);
            foreach (var write in detachedWrites)
                await ApplyTableWriteAsync(executor, write, seq, signal).ConfigureAwait(false);
            await ApplyDocumentActionsAsync(executor, actions, seq, signal).ConfigureAwait(false);

            var publication = await BuildPublicationAsync(executor, seq, detachedWrites, signal).ConfigureAwait(false);
            await executor.ExecuteAsync(
                "UPDATE durable_metadata SET next_id = ?, next_seq = ? WHERE singleton = 1",
                signal,
                Math.Max(storedNextId, candidateNextId).ToString(CultureInfo.InvariantCulture),
                checked(seq.Value + 1)).ConfigureAwait(false);
            signal.ThrowIfCancellationRequested();
            return (seq, publication);
        }, signal).ConfigureAwait(false);
        _nextId = Math.Max(_nextId, candidateNextId);
        UpdateAttachedStates(actions);
        QueuePublication(publication);
        return seq;
    }

    private async Task<Page<EntryRecord>> ScanEntriesCoreAsync(
        EntryQuery query,
        int limit,
        IReadOnlyDictionary<string, object?>? cursor)
    {
        var start = StorageScan.Start(query.Order, cursor, ScanOrder.Descending);
        if (start.Order == ScanOrder.Ascending)
            return await ScanEntriesAscendingAsync(query, limit, start.After).ConfigureAwait(false);

        var executor = new SqliteCommandExecutor(_connection);
        var conversation = await ReadConversationAsync(executor, query.ConversationId).ConfigureAwait(false);
        if (conversation is null)
            throw new InvalidOperationException($"Unknown conversation: {query.ConversationId.Value}");
        long? upper = query.MaxEntryId?.Value;
        if (start.After is { } after) upper = Math.Min(upper ?? MaximumSafeInteger, after - 1);
        var values = new List<EntryRecord>();
        while (true)
        {
            var clauses = new List<string> { "conversation_id = ?" };
            var parameters = new List<object?> { conversation.Id.Value };
            if (query.MinEntryId is { } minimum)
            {
                clauses.Add("id >= ?");
                parameters.Add(minimum.Value);
            }
            if (upper is { } maximum)
            {
                clauses.Add("id <= ?");
                parameters.Add(maximum);
            }
            parameters.Add(limit + 1 - values.Count);
            var rows = await executor.AllAsync(
                $"SELECT record FROM entries WHERE {string.Join(" AND ", clauses)} ORDER BY id DESC LIMIT ?",
                reader => new JsonRow(reader.GetString(0)),
                values: parameters.ToArray()).ConfigureAwait(false);
            values.AddRange(rows.Select(row => SqliteStorageJson.Deserialize<EntryRecord>(row.Record)));
            if (values.Count > limit || conversation.Parent is null) break;
            upper = upper is null
                ? conversation.Parent.At.Value
                : Math.Min(upper.Value, conversation.Parent.At.Value);
            if (query.MinEntryId is { } minEntryId && upper < minEntryId.Value) break;
            conversation = await ReadRequiredConversationAsync(executor, conversation.Parent.ConversationId)
                .ConfigureAwait(false);
        }
        return StorageScan.CreatePage(values, limit, ScanOrder.Descending);
    }

    private async Task<Page<EntryRecord>> ScanEntriesAscendingAsync(
        EntryQuery query,
        int limit,
        long? after)
    {
        var executor = new SqliteCommandExecutor(_connection);
        var conversation = await ReadConversationAsync(executor, query.ConversationId).ConfigureAwait(false);
        if (conversation is null)
            throw new InvalidOperationException($"Unknown conversation: {query.ConversationId.Value}");
        var segments = new List<EntryScanSegment>();
        long? upper = query.MaxEntryId?.Value;
        while (true)
        {
            segments.Add(new EntryScanSegment(conversation.Id, upper));
            if (conversation.Parent is null) break;
            upper = upper is null
                ? conversation.Parent.At.Value
                : Math.Min(upper.Value, conversation.Parent.At.Value);
            if (query.MinEntryId is { } minimum && upper < minimum.Value) break;
            conversation = await ReadRequiredConversationAsync(executor, conversation.Parent.ConversationId)
                .ConfigureAwait(false);
        }

        long? lower = query.MinEntryId?.Value;
        if (after is { } afterId) lower = Math.Max(lower ?? afterId + 1, afterId + 1);
        var values = new List<EntryRecord>();
        foreach (var segment in segments.AsEnumerable().Reverse())
        {
            var clauses = new List<string> { "conversation_id = ?" };
            var parameters = new List<object?> { segment.ConversationId.Value };
            if (lower is { } minimum)
            {
                clauses.Add("id >= ?");
                parameters.Add(minimum);
            }
            if (segment.Upper is { } maximum)
            {
                clauses.Add("id <= ?");
                parameters.Add(maximum);
            }
            parameters.Add(limit + 1 - values.Count);
            var rows = await executor.AllAsync(
                $"SELECT record FROM entries WHERE {string.Join(" AND ", clauses)} ORDER BY id ASC LIMIT ?",
                reader => new JsonRow(reader.GetString(0)),
                values: parameters.ToArray()).ConfigureAwait(false);
            values.AddRange(rows.Select(row => SqliteStorageJson.Deserialize<EntryRecord>(row.Record)));
            if (values.Count > limit) break;
        }
        return StorageScan.CreatePage(values, limit, ScanOrder.Ascending);
    }

    private async Task<StoredEntry?> ReadEntryAsync(SqliteCommandExecutor executor, EntryId id)
    {
        var row = await executor.GetAsync(
            "SELECT record, commit_seq FROM entries WHERE id = ?",
            reader => new EntryRow(reader.GetString(0), reader.GetInt64(1)),
            values: [id.Value]).ConfigureAwait(false);
        return row is null
            ? null
            : new StoredEntry
            {
                Entry = SqliteStorageJson.Deserialize<EntryRecord>(row.Record),
                CommitSeq = Seq.From(row.CommitSeq),
            };
    }

    private async Task<StoredEntry?> ReadVisibleEntryAsync(
        SqliteCommandExecutor executor,
        ConversationId conversationId,
        EntryId id)
    {
        var conversation = await ReadConversationAsync(executor, conversationId).ConfigureAwait(false);
        if (conversation is null)
            throw new InvalidOperationException($"Unknown conversation: {conversationId.Value}");
        var stored = await ReadEntryAsync(executor, id).ConfigureAwait(false);
        if (stored is null) return null;
        long? upper = null;
        while (conversation.Id != stored.Entry.ConversationId)
        {
            if (conversation.Parent is null) return null;
            upper = upper is null
                ? conversation.Parent.At.Value
                : Math.Min(upper.Value, conversation.Parent.At.Value);
            conversation = await ReadRequiredConversationAsync(executor, conversation.Parent.ConversationId)
                .ConfigureAwait(false);
        }
        return upper is { } maximum && stored.Entry.Id.Value > maximum ? null : stored;
    }

    private async Task<EntryRecord?> ReadLatestHeadMarkerAsync(
        SqliteCommandExecutor executor,
        ConversationId conversationId,
        EntryId? atOrBeforeEntryId)
    {
        var conversation = await ReadConversationAsync(executor, conversationId).ConfigureAwait(false);
        if (conversation is null)
            throw new InvalidOperationException($"Unknown conversation: {conversationId.Value}");
        long? upper = atOrBeforeEntryId?.Value;
        while (true)
        {
            var sql = upper is null
                ? "SELECT record FROM entries WHERE conversation_id = ? AND head IS NOT NULL ORDER BY id DESC LIMIT 1"
                : "SELECT record FROM entries WHERE conversation_id = ? AND head IS NOT NULL AND id <= ? ORDER BY id DESC LIMIT 1";
            object?[] parameters = upper is { } max
                ? new object?[] { conversation.Id.Value, max }
                : [conversation.Id.Value];
            var row = await executor.GetAsync(
                sql,
                reader => new JsonRow(reader.GetString(0)),
                values: parameters).ConfigureAwait(false);
            if (row is not null) return SqliteStorageJson.Deserialize<EntryRecord>(row.Record);
            if (conversation.Parent is null) return null;
            upper = upper is null
                ? conversation.Parent.At.Value
                : Math.Min(upper.Value, conversation.Parent.At.Value);
            conversation = await ReadRequiredConversationAsync(executor, conversation.Parent.ConversationId)
                .ConfigureAwait(false);
        }
    }

    private async Task<ConversationRecord?> ReadConversationAsync(SqliteCommandExecutor executor, ConversationId id)
    {
        var row = await executor.GetAsync(
            "SELECT record FROM conversations WHERE id = ?",
            reader => new JsonRow(reader.GetString(0)),
            values: [id.Value]).ConfigureAwait(false);
        return row is null ? null : SqliteStorageJson.Deserialize<ConversationRecord>(row.Record);
    }

    private async Task<ConversationRecord> ReadRequiredConversationAsync(
        SqliteCommandExecutor executor,
        ConversationId id)
        => await ReadConversationAsync(executor, id).ConfigureAwait(false)
           ?? throw new InvalidOperationException($"Unknown conversation: {id.Value}");

    private async Task<DocumentRecord?> FindDocumentCoreAsync(
        SqliteCommandExecutor executor,
        DocumentAddress address,
        DocumentPoint at)
    {
        var parts = Address(address);
        var sql = at switch
        {
            DocumentPoint.Current =>
                """
                SELECT record FROM documents
                WHERE kind = ? AND scope_kind = ? AND owner_id = ? AND family = ? AND key_value = ?
                    AND retired_at IS NULL
                ORDER BY created_at DESC LIMIT 1
                """,
            DocumentPoint.AtSeq =>
                """
                SELECT record FROM documents
                WHERE kind = ? AND scope_kind = ? AND owner_id = ? AND family = ? AND key_value = ?
                    AND created_at <= ? AND (retired_at IS NULL OR retired_at > ?)
                ORDER BY created_at DESC LIMIT 1
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(at), "Unknown document point"),
        };
        var parameters = new List<object?> { parts.Kind, parts.ScopeKind, parts.OwnerId, parts.Family, parts.KeyValue };
        if (at is DocumentPoint.AtSeq atSeq)
        {
            parameters.Add(atSeq.Seq.Value);
            parameters.Add(atSeq.Seq.Value);
        }
        var row = await executor.GetAsync(
            sql,
            reader => new JsonRow(reader.GetString(0)),
            values: parameters.ToArray()).ConfigureAwait(false);
        return row is null ? null : SqliteStorageJson.Deserialize<DocumentRecord>(row.Record);
    }

    private async Task<StoredDocument?> MaterializeDocumentAsync(
        SqliteCommandExecutor executor,
        DocumentId id,
        DocumentPoint at)
    {
        var row = await executor.GetAsync(
            "SELECT record FROM documents WHERE id = ?",
            reader => new JsonRow(reader.GetString(0)),
            values: [id.Value]).ConfigureAwait(false);
        if (row is null) return null;
        var record = SqliteStorageJson.Deserialize<DocumentRecord>(row.Record);
        var atSequence = at is DocumentPoint.AtSeq atSeq ? atSeq.Seq.Value : (long?)null;
        if (atSequence is not null && IsCurrentOnly(record))
            throw new InvalidOperationException($"Document {id.Value} does not retain historical content");
        if (!IsAliveAt(record, at, atSequence)) return null;
        var upper = atSequence ?? MaximumSafeInteger;
        var baseRow = await executor.GetAsync(
            """
            SELECT seq, kind, version, content FROM document_revisions
            WHERE document_id = ? AND kind = 'base' AND seq <= ?
            ORDER BY seq DESC LIMIT 1
            """,
            reader => new RevisionRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)),
            values: [id.Value, upper]).ConfigureAwait(false);
        if (baseRow is null) throw new InvalidOperationException($"Document {id.Value} is missing a required base");
        var value = SqliteStorageJson.Deserialize<IReadOnlyDictionary<string, object?>>(baseRow.Content);
        var tail = await executor.AllAsync(
            """
            SELECT seq, kind, version, content FROM document_revisions
            WHERE document_id = ? AND seq > ? AND seq <= ?
            ORDER BY seq
            """,
            reader => new RevisionRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)),
            values: [id.Value, baseRow.Seq, upper]).ConfigureAwait(false);
        foreach (var revision in tail)
        {
            if (revision.Kind != "delta" || revision.Version != baseRow.Version)
                throw new InvalidOperationException(
                    $"Document {id.Value} crosses a stored version boundary without a base");
            var ops = SqliteStorageJson.Deserialize<IReadOnlyList<DeltaOp>>(revision.Content);
            value = DeltaApply.Apply(ops, value) as IReadOnlyDictionary<string, object?>
                ?? throw new InvalidOperationException($"Document {id.Value} delta application failed");
        }
        return new StoredDocument
        {
            Record = record,
            Version = baseRow.Version,
            Value = value,
            DeltasSinceBase = tail.Count,
        };
    }

    private async Task<T> InTransactionAsync<T>(
        Func<SqliteCommandExecutor, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var transaction = _connection.BeginTransaction(deferred: false);
        var executor = new SqliteCommandExecutor(_connection, transaction);
        try
        {
            var result = await operation(executor).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (Exception failure)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException("SQLite transaction failed and rollback failed", failure, rollbackFailure);
            }
            throw;
        }
    }

    private async Task CloseDatabaseAsync()
    {
        Exception? checkpointFailure = null;
        try
        {
            await new SqliteCommandExecutor(_connection)
                .ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)").ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            checkpointFailure = failure;
        }

        foreach (var state in _attachedStates.Values) state.Dispose();
        _attachedStates.Clear();
        try
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception closeFailure)
        {
            if (checkpointFailure is not null)
                throw new AggregateException("SQLite checkpoint and close failed", checkpointFailure, closeFailure);
            throw;
        }

        if (checkpointFailure is not null) ExceptionDispatchInfo.Capture(checkpointFailure).Throw();
    }

    private async Task CheckGlobalIdsAsync(
        SqliteCommandExecutor executor,
        IReadOnlyList<StorageWrite> writes,
        CancellationToken cancellationToken)
    {
        var claimed = new Dictionary<long, string>();
        foreach (var write in writes)
        {
            if (write is StorageWrite.DocumentChange or StorageWrite.DocumentRetire) continue;
            var (id, table) = RecordIdentity(write);
            var existing = await executor.GetAsync(
                "SELECT record_type FROM record_ids WHERE id = ?",
                reader => new RecordTypeRow(reader.GetString(0)),
                cancellationToken,
                id).ConfigureAwait(false);
            var earlier = claimed.GetValueOrDefault(id);
            if (table is "conversation" or "entry" or "document")
            {
                if (existing is not null) throw new InvalidOperationException($"ID {id} already belongs to {existing.RecordType}");
                if (earlier is not null) throw new InvalidOperationException($"ID {id} is written more than once");
            }
            else
            {
                if (existing is not null && existing.RecordType != table)
                    throw new InvalidOperationException($"ID {id} already belongs to {existing.RecordType}");
                if (earlier is not null && earlier != table)
                    throw new InvalidOperationException($"ID {id} is written as two record types");
            }
            claimed[id] = table;
        }
    }

    private async Task CheckDocumentActionsAsync(
        SqliteCommandExecutor executor,
        IReadOnlyDictionary<DocumentId, DocumentAction> actions,
        CancellationToken cancellationToken)
    {
        var liveCounts = new Dictionary<AddressParts, int>();
        foreach (var (id, action) in actions)
        {
            if (action.Copy is { } copy && actions.ContainsKey(copy.Id))
                throw new StorageRejected($"Document copy {id.Value} source is changed in the copy batch");
            var row = await executor.GetAsync(
                "SELECT record FROM documents WHERE id = ?",
                reader => new JsonRow(reader.GetString(0)),
                cancellationToken,
                id.Value).ConfigureAwait(false);
            var existing = row is null ? null : SqliteStorageJson.Deserialize<DocumentRecord>(row.Record);
            if (action.Create is null && existing is null)
                throw new InvalidOperationException($"Unknown document: {id.Value}");
            if (action.Create is not null && existing is not null)
                throw new InvalidOperationException($"Document {id.Value} already exists");
            if (existing?.RetiredAt is not null)
                throw new InvalidOperationException($"Document {id.Value} is retired");

            if (action.Content is DocumentContent.Delta delta)
            {
                var previous = await executor.GetAsync(
                    "SELECT version FROM document_revisions WHERE document_id = ? ORDER BY seq DESC LIMIT 1",
                    reader => new VersionRow(reader.GetInt32(0)),
                    cancellationToken,
                    id.Value).ConfigureAwait(false);
                if (previous is null) throw new InvalidOperationException($"Document {id.Value} delta has no base");
                if (previous.Version != delta.Version)
                    throw new InvalidOperationException($"Document {id.Value} version transition requires a base");
            }

            var address = action.Create is { } create ? Address(create) : Address(existing!);
            if (!liveCounts.TryGetValue(address, out var live))
            {
                var current = await CurrentDocumentIdAsync(executor, address, cancellationToken).ConfigureAwait(false);
                live = current is null ? 0 : 1;
            }
            if (action.Retire && existing is not null) live--;
            if (action.Create is not null && !action.Retire) live++;
            liveCounts[address] = live;
        }
        if (liveCounts.Values.Any(live => live > 1))
            throw new InvalidOperationException("Document address already has a current incarnation");
    }

    private async Task<DocumentId?> CurrentDocumentIdAsync(
        SqliteCommandExecutor executor,
        AddressParts address,
        CancellationToken cancellationToken)
    {
        var id = await executor.ScalarInt64Async(
            """
            SELECT id FROM documents
            WHERE kind = ? AND scope_kind = ? AND owner_id = ? AND family = ? AND key_value = ?
                AND retired_at IS NULL LIMIT 1
            """,
            cancellationToken,
            address.Kind,
            address.ScopeKind,
            address.OwnerId,
            address.Family,
            address.KeyValue).ConfigureAwait(false);
        return id is { } value ? DocumentId.From(value) : null;
    }

    private async Task ApplyTableWriteAsync(
        SqliteCommandExecutor executor,
        StorageWrite write,
        Seq seq,
        CancellationToken cancellationToken)
    {
        switch (write)
        {
            case StorageWrite.Conversation conversation:
                await ClaimIdAsync(executor, conversation.Value.Id.Value, "conversation", cancellationToken).ConfigureAwait(false);
                await executor.ExecuteAsync(
                    "INSERT INTO conversations (id, owner_conversation_id, owner_task_id, record) VALUES (?, ?, ?, ?)",
                    cancellationToken,
                    conversation.Value.Id.Value,
                    conversation.Value.Owner?.ConversationId.Value,
                    conversation.Value.Owner?.TaskId.Value,
                    SqliteStorageJson.Serialize(conversation.Value)).ConfigureAwait(false);
                break;
            case StorageWrite.Entry entry:
                await ClaimIdAsync(executor, entry.Value.Id.Value, "entry", cancellationToken).ConfigureAwait(false);
                await executor.ExecuteAsync(
                    "INSERT INTO entries (id, conversation_id, head, commit_seq, record) VALUES (?, ?, ?, ?, ?)",
                    cancellationToken,
                    entry.Value.Id.Value,
                    entry.Value.ConversationId.Value,
                    entry.Value.Head?.Value,
                    seq.Value,
                    SqliteStorageJson.Serialize(entry.Value)).ConfigureAwait(false);
                break;
            case StorageWrite.Task task:
                await ClaimIdAsync(executor, task.Value.Id.Value, "task", cancellationToken).ConfigureAwait(false);
                await executor.ExecuteAsync(
                    """
                    INSERT INTO tasks (id, conversation_id, kind, status, abort_requested, background, record)
                    VALUES (?, ?, ?, ?, ?, ?, ?)
                    ON CONFLICT(id) DO UPDATE SET conversation_id = excluded.conversation_id, kind = excluded.kind,
                        status = excluded.status, abort_requested = excluded.abort_requested,
                        background = excluded.background, record = excluded.record
                    """,
                    cancellationToken,
                    task.Value.Id.Value,
                    task.Value.ConversationId.Value,
                    SqliteStorageJson.EncodeIndexedString(task.Value.Kind),
                    EnumWireName(task.Value.State.Status),
                    task.Value.AbortRequested ? 1 : 0,
                    task.Value.Background ? 1 : 0,
                    SqliteStorageJson.Serialize(task.Value)).ConfigureAwait(false);
                break;
            case StorageWrite.Submission submission:
                await ClaimIdAsync(executor, submission.Value.Id.Value, "submission", cancellationToken).ConfigureAwait(false);
                await executor.ExecuteAsync(
                    """
                    INSERT INTO submissions (id, conversation_id, request_id, status, record) VALUES (?, ?, ?, ?, ?)
                    ON CONFLICT(id) DO UPDATE SET conversation_id = excluded.conversation_id,
                        request_id = excluded.request_id, status = excluded.status, record = excluded.record
                    """,
                    cancellationToken,
                    submission.Value.Id.Value,
                    submission.Value.ConversationId.Value,
                    submission.Value.RequestId is null
                        ? null
                        : SqliteStorageJson.EncodeIndexedString(submission.Value.RequestId),
                    EnumWireName(StatusOf(submission.Value)),
                    SqliteStorageJson.Serialize(submission.Value)).ConfigureAwait(false);
                break;
            case StorageWrite.DocumentCreateWrite:
            case StorageWrite.DocumentCopyWrite:
            case StorageWrite.DocumentChange:
            case StorageWrite.DocumentRetire:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(write), $"Unknown storage write type: {write.GetType().Name}");
        }
    }

    private async Task ClaimIdAsync(
        SqliteCommandExecutor executor,
        long id,
        string table,
        CancellationToken cancellationToken)
        => await executor.ExecuteAsync(
            "INSERT OR IGNORE INTO record_ids (id, record_type) VALUES (?, ?)",
            cancellationToken,
            id,
            table).ConfigureAwait(false);

    private async Task ApplyDocumentActionsAsync(
        SqliteCommandExecutor executor,
        IReadOnlyDictionary<DocumentId, DocumentAction> actions,
        Seq seq,
        CancellationToken cancellationToken)
    {
        foreach (var (id, action) in actions)
        {
            var content = action.Content;
            if (action.Copy is { } source)
            {
                try
                {
                    var stored = await MaterializeDocumentAsync(executor, source.Id, source.At).ConfigureAwait(false);
                    if (stored is null)
                        throw new InvalidOperationException($"Fork source document {source.Id.Value} cannot be read");
                    var create = action.Create!;
                    if (stored.Record.Scope is not DocumentScope.ConversationScope
                        || create.Scope is not DocumentScope.ConversationScope
                        || stored.Record.Kind != create.Kind
                        || stored.Record.Key != create.Key
                        || stored.Record.History != create.History
                        || stored.Record.Fork != create.Fork)
                    {
                        throw new InvalidOperationException(
                            $"Fork source document {source.Id.Value} does not match the copied record");
                    }
                    content = new DocumentContent.Base(stored.Version, stored.Value);
                }
                catch (Exception error) when (error is not StorageRejected)
                {
                    throw new StorageRejected($"Document copy {id.Value} was rejected", error);
                }
            }

            DocumentRecord record;
            if (action.Create is { } createRecord)
            {
                record = new DocumentRecord
                {
                    Id = createRecord.Id,
                    Kind = createRecord.Kind,
                    Key = createRecord.Key,
                    CreatedAt = seq,
                    RetiredAt = action.Retire ? seq : null,
                    Scope = createRecord.Scope,
                    History = createRecord.History,
                    Fork = createRecord.Fork,
                };
                var address = Address(record);
                await ClaimIdAsync(executor, id.Value, "document", cancellationToken).ConfigureAwait(false);
                await executor.ExecuteAsync(
                    """
                    INSERT INTO documents
                        (id, kind, family, key_value, scope_kind, owner_id, created_at, retired_at, record)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    cancellationToken,
                    id.Value,
                    address.Kind,
                    address.Family,
                    address.KeyValue,
                    address.ScopeKind,
                    address.OwnerId,
                    seq.Value,
                    action.Retire ? seq.Value : null,
                    SqliteStorageJson.Serialize(record)).ConfigureAwait(false);
            }
            else
            {
                var row = await executor.GetAsync(
                    "SELECT record FROM documents WHERE id = ?",
                    reader => new JsonRow(reader.GetString(0)),
                    cancellationToken,
                    id.Value).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Unknown document: {id.Value}");
                record = SqliteStorageJson.Deserialize<DocumentRecord>(row.Record);
            }

            if (content is not null)
            {
                if (content is DocumentContent.Base && IsCurrentOnly(record))
                {
                    await executor.ExecuteAsync(
                        "DELETE FROM document_revisions WHERE document_id = ?",
                        cancellationToken,
                        id.Value).ConfigureAwait(false);
                }
                var (kind, version, encodedContent) = content switch
                {
                    DocumentContent.Base baseContent =>
                        ("base", baseContent.Version, SqliteStorageJson.Serialize(baseContent.Value)),
                    DocumentContent.Delta deltaContent =>
                        ("delta", deltaContent.Version, SqliteStorageJson.Serialize(deltaContent.Ops)),
                    _ => throw new ArgumentOutOfRangeException(nameof(content)),
                };
                await executor.ExecuteAsync(
                    """
                    INSERT INTO document_revisions (document_id, seq, kind, version, content)
                    VALUES (?, ?, ?, ?, ?)
                    """,
                    cancellationToken,
                    id.Value,
                    seq.Value,
                    kind,
                    version,
                    encodedContent).ConfigureAwait(false);
            }

            if (!action.Retire) continue;
            if (action.Create is null)
            {
                record = record with { RetiredAt = seq };
                await executor.ExecuteAsync(
                    "UPDATE documents SET retired_at = ?, record = ? WHERE id = ?",
                    cancellationToken,
                    seq.Value,
                    SqliteStorageJson.Serialize(record),
                    id.Value).ConfigureAwait(false);
            }
            if (IsCurrentOnly(record))
            {
                await executor.ExecuteAsync(
                    "DELETE FROM document_revisions WHERE document_id = ?",
                    cancellationToken,
                    id.Value).ConfigureAwait(false);
            }
        }
    }

    private async Task<CommitPublication> BuildPublicationAsync(
        SqliteCommandExecutor executor,
        Seq seq,
        IReadOnlyList<StorageWrite> writes,
        CancellationToken cancellationToken)
    {
        var changes = new List<CommitChange>();
        foreach (var write in writes)
        {
            switch (write)
            {
                case StorageWrite.Conversation conversation:
                    changes.Add(new CommitChange.ConversationTable(conversation.Value));
                    break;
                case StorageWrite.Entry entry:
                    changes.Add(new CommitChange.EntryTable(entry.Value));
                    break;
                case StorageWrite.Task task:
                    changes.Add(new CommitChange.TaskTable(task.Value));
                    break;
                case StorageWrite.Submission submission:
                    changes.Add(new CommitChange.SubmissionTable(submission.Value));
                    break;
                case StorageWrite.DocumentCreateWrite create:
                {
                    var record = await ReadDocumentRecordAsync(executor, create.Record.Id, cancellationToken).ConfigureAwait(false);
                    changes.Add(new CommitChange.DocumentChanged(new DocumentCommitChange.Document(
                        record,
                        await ConversationOfAsync(executor, record, cancellationToken).ConfigureAwait(false),
                        create.Content.Version,
                        create.Content.Value,
                        [])));
                    break;
                }
                case StorageWrite.DocumentCopyWrite copy:
                {
                    var record = await ReadDocumentRecordAsync(executor, copy.Record.Id, cancellationToken).ConfigureAwait(false);
                    changes.Add(new CommitChange.DocumentCopied(new DocumentCommitChange.Copy(
                        record,
                        await ConversationOfAsync(executor, record, cancellationToken).ConfigureAwait(false),
                        copy.Source)));
                    break;
                }
                case StorageWrite.DocumentChange change:
                {
                    var record = await ReadDocumentRecordAsync(executor, change.Id, cancellationToken).ConfigureAwait(false);
                    changes.Add(new CommitChange.DocumentChanged(new DocumentCommitChange.Document(
                        record,
                        await ConversationOfAsync(executor, record, cancellationToken).ConfigureAwait(false),
                        VersionOf(change.Content),
                        null,
                        OpsOf(change.Content))));
                    break;
                }
                case StorageWrite.DocumentRetire:
                    break;
            }
        }
        return new CommitPublication { Seq = seq, Changes = changes };
    }

    private async Task<DocumentRecord> ReadDocumentRecordAsync(
        SqliteCommandExecutor executor,
        DocumentId id,
        CancellationToken cancellationToken)
    {
        var row = await executor.GetAsync(
            "SELECT record FROM documents WHERE id = ?",
            reader => new JsonRow(reader.GetString(0)),
            cancellationToken,
            id.Value).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown document: {id.Value}");
        return SqliteStorageJson.Deserialize<DocumentRecord>(row.Record);
    }

    private async Task<ConversationId> ConversationOfAsync(
        SqliteCommandExecutor executor,
        DocumentRecord record,
        CancellationToken cancellationToken)
    {
        return record.Scope switch
        {
            DocumentScope.ConversationScope conversation => conversation.ConversationId,
            DocumentScope.TaskScope task => await ReadTaskConversationAsync(executor, task.TaskId, cancellationToken)
                .ConfigureAwait(false),
            DocumentScope.SessionScope => DurableIdConstants.RootConversation,
            _ => throw new ArgumentOutOfRangeException(nameof(record)),
        };
    }

    private async Task<ConversationId> ReadTaskConversationAsync(
        SqliteCommandExecutor executor,
        TaskId<object?> id,
        CancellationToken cancellationToken)
    {
        var row = await executor.GetAsync(
            "SELECT record FROM tasks WHERE id = ?",
            reader => new JsonRow(reader.GetString(0)),
            cancellationToken,
            id.Value).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown task: {id.Value}");
        return SqliteStorageJson.Deserialize<TaskRecord>(row.Record).ConversationId;
    }

    private void UpdateAttachedStates(IReadOnlyDictionary<DocumentId, DocumentAction> actions)
    {
        foreach (var (id, action) in actions)
        {
            if (!_attachedStates.TryGetValue(id.Value, out var state)) continue;
            switch (action.Content)
            {
                case DocumentContent.Base baseContent:
                    state.Replace(CloneJson(baseContent.Value));
                    break;
                case DocumentContent.Delta delta:
                    state.ApplyOps(delta.Ops, Context.Background);
                    break;
            }
            if (!action.Retire) continue;
            state.Dispose();
            _attachedStates.Remove(id.Value);
        }
    }

    private void QueuePublication(CommitPublication publication)
    {
        Func<CommitPublication, Task>[] listeners;
        lock (_publicationGate) listeners = [.. _publications];
        foreach (var listener in listeners)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await listener(publication).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    System.Diagnostics.Trace.TraceError("SQLite storage publication listener failed: {0}", error);
                }
            });
        }
    }

    private static (string Clause, long Parameter, string Direction) ScanCondition(ScanStart start)
        => start.Order == ScanOrder.Ascending
            ? ("id > ?", start.After ?? -1, "ASC")
            : ("id < ?", start.After ?? MaximumSafeInteger, "DESC");

    private static ScopeColumns GetScopeColumns(DocumentScope scope) => scope switch
    {
        DocumentScope.SessionScope => new ScopeColumns("session", 0),
        DocumentScope.ConversationScope conversation => new ScopeColumns("conversation", conversation.ConversationId.Value),
        DocumentScope.TaskScope task => new ScopeColumns("task", task.TaskId.Value),
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static AddressParts Address(DocumentAddress address) => Address(address.Kind, address.Key, address.Scope);

    private static AddressParts Address(DocumentCreate address) => Address(address.Kind, address.Key, address.Scope);

    private static AddressParts Address(DocumentRecord address) => Address(address.Kind, address.Key, address.Scope);

    private static AddressParts Address(string kind, string? key, DocumentScope scope)
    {
        var columns = GetScopeColumns(scope);
        return new AddressParts(
            SqliteStorageJson.EncodeIndexedString(kind),
            columns.Kind,
            columns.OwnerId,
            key is null ? 0 : 1,
            SqliteStorageJson.EncodeIndexedString(key ?? ""));
    }

    private static bool IsCurrentOnly(DocumentRecord record)
        => record.Scope is not DocumentScope.ConversationScope
            || record.History == ConversationHistory.Latest;

    private static bool IsAliveAt(DocumentRecord record, DocumentPoint at, long? atSequence) => at switch
    {
        DocumentPoint.Current => record.RetiredAt is null,
        DocumentPoint.AtSeq => record.CreatedAt.Value <= atSequence!.Value
            && (record.RetiredAt is null || atSequence.Value < record.RetiredAt.Value.Value),
        _ => false,
    };

    private static string EnumWireName<TEnum>(TEnum value) where TEnum : struct, Enum
        => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static SubmissionStatus StatusOf(SubmissionRecord record) => record switch
    {
        SubmissionRecord.InputRecord input => input.Status,
        SubmissionRecord.WriteRecord write => write.Status,
        _ => throw new ArgumentOutOfRangeException(nameof(record)),
    };

    private static (long Id, string Table) RecordIdentity(StorageWrite write) => write switch
    {
        StorageWrite.Conversation conversation => (conversation.Value.Id.Value, "conversation"),
        StorageWrite.Entry entry => (entry.Value.Id.Value, "entry"),
        StorageWrite.Task task => (task.Value.Id.Value, "task"),
        StorageWrite.Submission submission => (submission.Value.Id.Value, "submission"),
        StorageWrite.DocumentCreateWrite create => (create.Record.Id.Value, "document"),
        StorageWrite.DocumentCopyWrite copy => (copy.Record.Id.Value, "document"),
        _ => throw new ArgumentOutOfRangeException(nameof(write)),
    };

    private long CandidateNextId(IReadOnlyList<StorageWrite> writes)
    {
        var nextId = _nextId;
        foreach (var write in writes)
        {
            if (write is StorageWrite.DocumentChange or StorageWrite.DocumentRetire) continue;
            var (id, _) = RecordIdentity(write);
            var candidate = id == long.MaxValue ? long.MaxValue : id + 1;
            nextId = Math.Max(nextId, candidate);
        }
        return nextId;
    }

    private static IReadOnlyDictionary<DocumentId, DocumentAction> PrepareDocumentActions(
        IReadOnlyList<StorageWrite> writes)
    {
        var actions = new Dictionary<DocumentId, DocumentAction>();
        foreach (var write in writes)
        {
            var id = write switch
            {
                StorageWrite.DocumentCreateWrite create => create.Record.Id,
                StorageWrite.DocumentCopyWrite copy => copy.Record.Id,
                StorageWrite.DocumentChange change => change.Id,
                StorageWrite.DocumentRetire retire => retire.Id,
                _ => (DocumentId?)null,
            };
            if (id is null) continue;
            if (!actions.TryGetValue(id.Value, out var action))
            {
                action = new DocumentAction();
                actions.Add(id.Value, action);
            }
            switch (write)
            {
                case StorageWrite.DocumentCreateWrite create:
                    if (action.Create is not null || action.Content is not null || action.Copy is not null)
                        throw new InvalidOperationException($"Document {id.Value.Value} has more than one content command");
                    action.Create = create.Record;
                    action.Content = create.Content;
                    break;
                case StorageWrite.DocumentCopyWrite copy:
                    if (action.Create is not null || action.Content is not null || action.Copy is not null)
                        throw new InvalidOperationException($"Document {id.Value.Value} has more than one content command");
                    action.Create = copy.Record;
                    action.Copy = copy.Source;
                    break;
                case StorageWrite.DocumentChange change:
                    if (action.Content is not null || action.Copy is not null)
                        throw new InvalidOperationException($"Document {id.Value.Value} has more than one content command");
                    action.Content = change.Content;
                    break;
                case StorageWrite.DocumentRetire:
                    if (action.Retire)
                        throw new InvalidOperationException($"Document {id.Value.Value} is retired more than once");
                    action.Retire = true;
                    break;
            }
        }
        return actions;
    }

    private static IReadOnlyList<StorageWrite> CloneWrites(IReadOnlyList<StorageWrite> writes)
    {
        var result = new List<StorageWrite>(writes.Count);
        foreach (var write in writes)
        {
            result.Add(write switch
            {
                StorageWrite.Conversation conversation => new StorageWrite.Conversation(CloneJson(conversation.Value)),
                StorageWrite.Entry entry => new StorageWrite.Entry(CloneJson(entry.Value)),
                StorageWrite.Task task => new StorageWrite.Task(CloneJson(task.Value)),
                StorageWrite.Submission submission => new StorageWrite.Submission(CloneJson(submission.Value)),
                StorageWrite.DocumentCreateWrite create => new StorageWrite.DocumentCreateWrite(
                    CloneJson(create.Record),
                    CloneBase(create.Content)),
                StorageWrite.DocumentCopyWrite copy => new StorageWrite.DocumentCopyWrite(
                    CloneJson(copy.Record),
                    new DocumentCopySource { Id = copy.Source.Id, At = copy.Source.At }),
                StorageWrite.DocumentChange change => new StorageWrite.DocumentChange(change.Id, CloneContent(change.Content)),
                StorageWrite.DocumentRetire retire => new StorageWrite.DocumentRetire(retire.Id),
                _ => throw new ArgumentOutOfRangeException(nameof(writes), $"Unknown storage write type: {write.GetType().Name}"),
            });
        }
        return result;
    }

    private static DocumentContent CloneContent(DocumentContent content) => content switch
    {
        DocumentContent.Base baseContent => new DocumentContent.Base(
            baseContent.Version,
            CloneJson(baseContent.Value)),
        DocumentContent.Delta deltaContent => new DocumentContent.Delta(
            deltaContent.Version,
            CloneJson<IReadOnlyList<DeltaOp>>(deltaContent.Ops)),
        _ => throw new ArgumentOutOfRangeException(nameof(content)),
    };

    private static DocumentContent.Base CloneBase(DocumentContent.Base content)
        => new(content.Version, CloneJson(content.Value));

    private static T CloneJson<T>(T value)
        => SqliteStorageJson.Deserialize<T>(SqliteStorageJson.Serialize(value));

    private static int VersionOf(DocumentContent content) => content switch
    {
        DocumentContent.Base baseContent => baseContent.Version,
        DocumentContent.Delta delta => delta.Version,
        _ => throw new ArgumentOutOfRangeException(nameof(content)),
    };

    private static IReadOnlyList<DeltaOp> OpsOf(DocumentContent content)
        => content is DocumentContent.Delta delta ? delta.Ops : [];

    private static TId CreateId<TId>(long value) where TId : struct
    {
        if (typeof(TId) == typeof(ConversationId)) return (TId)(object)ConversationId.From(value);
        if (typeof(TId) == typeof(EntryId)) return (TId)(object)EntryId.From(value);
        if (typeof(TId) == typeof(SubmissionId)) return (TId)(object)SubmissionId.From(value);
        if (typeof(TId) == typeof(DocumentId)) return (TId)(object)DocumentId.From(value);
        if (typeof(TId) == typeof(Seq)) return (TId)(object)Seq.From(value);
        if (typeof(TId).IsGenericType && typeof(TId).GetGenericTypeDefinition() == typeof(TaskId<>))
        {
            var from = typeof(TaskId<>).MakeGenericType(typeof(TId).GetGenericArguments()[0])
                .GetMethod("From", [typeof(long)])!;
            return (TId)from.Invoke(null, [value])!;
        }
        throw new NotSupportedException($"SqliteStorage cannot mint IDs of type {typeof(TId).Name}");
    }

    private Task<T> EnqueueAsync<T>(Func<Task<T>> operation)
    {
        lock (_queueGate)
        {
            if (_closed) return Task.FromException<T>(new InvalidOperationException("SqliteStorage is closed"));
            var result = Schedule(_queueTail, operation);
            _queueTail = Settled(result);
            return result;
        }
    }

    private Task EnqueueAsync(Func<Task> operation)
        => EnqueueAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        });

    private static Task<T> Schedule<T>(Task predecessor, Func<Task<T>> operation)
        => predecessor.ContinueWith(
            _ => RunAsync(operation),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();

    private static Task Schedule(Task predecessor, Func<Task> operation)
        => Schedule(predecessor, async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        });

    private static async Task<T> RunAsync<T>(Func<Task<T>> operation)
        => await operation().ConfigureAwait(false);

    private static Task Settled(Task operation)
        => operation.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private sealed record MetadataRow(string NextId, long NextSeq);

    private sealed record JsonRow(string Record);

    private sealed record EntryRow(string Record, long CommitSeq);

    private sealed record RecordTypeRow(string RecordType);

    private sealed record VersionRow(int Version);

    private sealed record RevisionRow(long Seq, string Kind, int Version, string Content);

    private sealed record ScopeColumns(string Kind, long OwnerId);

    private readonly record struct AddressParts(
        string Kind,
        string ScopeKind,
        long OwnerId,
        int Family,
        string KeyValue);

    private sealed record EntryScanSegment(ConversationId ConversationId, long? Upper);

    private sealed class DocumentAction
    {
        public DocumentCreate? Create { get; set; }

        public DocumentCopySource? Copy { get; set; }

        public DocumentContent? Content { get; set; }

        public bool Retire { get; set; }
    }
}
