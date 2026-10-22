using System.Globalization;
using System.Text;
using Pi.Durable.Env;
using Pi.Durable.Types;
using TaskStatus = Pi.Durable.Types.TaskStatus;

namespace Pi.Durable.Storage;

/// <summary>JSONL 存储选项。对应 TS <c>JsonlStorageOptions</c>。</summary>
public sealed record JsonlStorageOptions
{
    /// <summary>追加主标记前冲刷每个受影响的侧车。缺省 false。</summary>
    public bool Fsync { get; init; }
}

/// <summary>日志内容损坏（格式、顺序、缺失确认记录）。对应 TS <c>JsonlCorruptionError</c>。</summary>
public sealed class JsonlCorruptionError : Exception
{
    public JsonlCorruptionError(string message, Exception? cause = null)
        : base(message, cause)
    {
    }
}

/// <summary>写入失败后存储只能重开的终态。对应 TS <c>JsonlStoragePoisonedError</c>。</summary>
public sealed class JsonlStoragePoisonedError : Exception
{
    public JsonlStoragePoisonedError(Exception cause)
        : base("JSONL storage is poisoned and must be reopened", cause)
    {
    }
}

/// <summary>
/// 可移植的 JSONL 存储后端。对应 TS <c>storage/jsonl/storage.ts</c>：
/// 主日志（<c>main.jsonl</c>）保存提交标记，任务与文档内容放在 <c>task-&lt;id&gt;.jsonl</c> /
/// <c>doc-&lt;id&gt;.jsonl</c> 侧车；提交先写侧车、再写主标记，最后应用到内存镜像。
/// <para>所有读取委托给内部 <see cref="MemoryStorage"/>，因此可见性、分页与游标语义与内存后端一致。</para>
/// </summary>
public sealed class JsonlStorage : IStorage
{
    private const string MainFile = "main.jsonl";
    private const string ReclaimSuffix = ".reclaim";
    private const string SidecarSuffix = ".jsonl";

    private static readonly Pi.Chord.Context.Context DefaultContext = Pi.Chord.Context.Context.Background;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IFileSystem fs;
    private readonly string directory;
    private readonly string mainPath;
    private readonly bool fsync;
    private readonly MemoryStorage memory = new();
    private readonly HashSet<long> currentOnlyDocuments = [];
    private readonly HashSet<long> liveTaskSidecars = [];
    private readonly SemaphoreSlim commitGate = new(1, 1);
    private bool closed;
    private JsonlStoragePoisonedError? poisonError;

    private JsonlStorage(IFileSystem fs, string directory, string mainPath, JsonlStorageOptions options)
    {
        this.fs = fs;
        this.directory = directory;
        this.mainPath = mainPath;
        fsync = options.Fsync;
    }

    /// <summary>打开或创建 JSONL 存储目录，并重放已有日志。</summary>
    public static async Task<JsonlStorage> OpenAsync(
        string directory,
        IFileSystem fs,
        JsonlStorageOptions? options = null)
    {
        var absolute = await fs.AbsolutePathAsync(directory, DefaultContext).ConfigureAwait(false);
        if (!absolute.IsOk) throw ErrorFromFile("path resolution", absolute.Error);
        var created = await fs.CreateDirAsync(
            absolute.Value, new CreateDirOptions { Recursive = true }, DefaultContext).ConfigureAwait(false);
        if (!created.IsOk) throw ErrorFromFile("directory creation", created.Error);
        var mainPath = await fs.JoinPathAsync([absolute.Value, MainFile], DefaultContext).ConfigureAwait(false);
        if (!mainPath.IsOk) throw ErrorFromFile("path join", mainPath.Error);

        var storage = new JsonlStorage(fs, absolute.Value, mainPath.Value, options ?? new JsonlStorageOptions());
        await storage.RecoverAsync().ConfigureAwait(false);
        return storage;
    }

    // ─── 提交 ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Seq> CommitAsync(
        IReadOnlyList<StorageWrite> writes, Action<Seq> adopt, CancellationToken signal = default)
    {
        await commitGate.WaitAsync(signal).ConfigureAwait(false);
        try
        {
            AssertUsable();
            var prepared = memory.PrepareCommit(writes);
            var encoded = EncodeCommit(prepared.Seq, prepared.Writes);
            var reclamations = PlanReclamations(prepared.Writes, encoded);

            var sidecars = new List<(string File, string Content, string Path)>(encoded.Sidecars.Count);
            foreach (var (file, content) in encoded.Sidecars)
                sidecars.Add((file, content, await ResolveFileAsync(file).ConfigureAwait(false)));

            foreach (var (file, content, path) in sidecars)
            {
                var appended = await fs.AppendFileAsync(path, content, DefaultContext).ConfigureAwait(false);
                if (!appended.IsOk) throw Poison(ErrorFromFile($"append to {file}", appended.Error));
            }
            if (fsync)
            {
                foreach (var (file, _, path) in sidecars)
                {
                    var flushed = await fs.FlushFileAsync(path, DefaultContext).ConfigureAwait(false);
                    if (!flushed.IsOk) throw Poison(ErrorFromFile($"flush of {file}", flushed.Error));
                }
            }
            var marker = await fs.AppendFileAsync(mainPath, encoded.Marker, DefaultContext).ConfigureAwait(false);
            if (!marker.IsOk) throw Poison(ErrorFromFile($"append to {MainFile}", marker.Error));

            var seq = prepared.Apply();
            AdoptSidecarState(prepared.Writes);
            // 主日志标记已追加：批次已持久。回收 sidecar 放在采纳之后。
            adopt(seq);
            await ReclaimSidecarsAsync(reclamations).ConfigureAwait(false);
            return seq;
        }
        finally
        {
            commitGate.Release();
        }
    }

    /// <summary>没有内存所有者的调用方（基准测试、存储用例）的便捷重载。</summary>
    public Task<Seq> CommitAsync(IReadOnlyList<StorageWrite> writes, CancellationToken signal = default)
        => CommitAsync(writes, static _ => { }, signal);

    /// <inheritdoc />
    public Task<TId> MintIdAsync<TId>() where TId : struct => Store().MintIdAsync<TId>();

    // ─── 读取（委托内存镜像） ────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<ConversationRecord?> GetConversationAsync(ConversationId id) => Store().GetConversationAsync(id);

    /// <inheritdoc />
    public Task<Page<ConversationRecord>> ScanConversationsAsync(
        ConversationQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => Store().ScanConversationsAsync(query, limit, cursor);

    /// <inheritdoc />
    public Task<StoredEntry?> GetEntryAsync(EntryId id) => Store().GetEntryAsync(id);

    /// <inheritdoc />
    public Task<StoredEntry?> GetEntryAsync(ConversationId conversationId, EntryId id)
        => Store().GetEntryAsync(conversationId, id);

    /// <inheritdoc />
    public Task<EntryRecord?> FindLatestHeadMarkerAsync(ConversationId conversationId, EntryId? atOrBeforeEntryId)
        => Store().FindLatestHeadMarkerAsync(conversationId, atOrBeforeEntryId);

    /// <inheritdoc />
    public Task<Page<EntryRecord>> ScanEntriesAsync(
        EntryQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => Store().ScanEntriesAsync(query, limit, cursor);

    /// <inheritdoc />
    public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id) => Store().GetTaskAsync(id);

    /// <inheritdoc />
    public Task<Page<TaskRecord>> ScanTasksAsync(
        TaskQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => Store().ScanTasksAsync(query, limit, cursor);

    /// <inheritdoc />
    public Task<SubmissionRecord?> GetSubmissionAsync(SubmissionId id) => Store().GetSubmissionAsync(id);

    /// <inheritdoc />
    public Task<Page<SubmissionRecord>> ScanSubmissionsAsync(
        SubmissionQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => Store().ScanSubmissionsAsync(query, limit, cursor);

    /// <inheritdoc />
    public Task<SubmissionRecord?> GetSubmissionByRequestAsync(ConversationId conversationId, string requestId)
        => Store().GetSubmissionByRequestAsync(conversationId, requestId);

    /// <inheritdoc />
    public Task<DocumentRecord?> FindDocumentAsync(DocumentAddress address, DocumentPoint at)
        => Store().FindDocumentAsync(address, at);

    /// <inheritdoc />
    public Task<StoredDocument?> GetDocumentAsync(DocumentId id, DocumentPoint at)
        => Store().GetDocumentAsync(id, at);

    /// <inheritdoc />
    public Task<Page<DocumentRecord>> ScanDocumentsAsync(
        DocumentQuery query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => Store().ScanDocumentsAsync(query, limit, cursor);

    /// <inheritdoc />
    public Task SubscribeAsync(Func<CommitPublication, Task> listener, CancellationToken signal = default)
        => Store().SubscribeAsync(listener, signal);

    /// <inheritdoc />
    public Task UnsubscribeAsync(Func<CommitPublication, Task> listener) => Store().UnsubscribeAsync(listener);

    /// <inheritdoc />
    public Task<MemoryState?> AttachDocumentAsync(DocumentId id, Func<Pi.Chord.Context.Context, Task>? listener = null)
        => Store().AttachDocumentAsync(id, listener);

    /// <inheritdoc />
    public Task CloseAsync()
    {
        if (closed) return Task.CompletedTask;
        closed = true;
        return memory.CloseAsync();
    }

    // ─── 编码 ────────────────────────────────────────────────────────────────

    private static EncodedCommit EncodeCommit(Seq seq, IReadOnlyList<StorageWrite> writes)
    {
        var mainWrites = new List<JsonlMainOperation>();
        var sidecarRecords = new List<(string File, JsonlSidecarRecord Record)>();
        var nextOrdinal = 0;

        int AddSidecar(string file, JsonlSidecarPayload payload)
        {
            var ordinal = nextOrdinal++;
            sidecarRecords.Add((file, new JsonlSidecarRecord(seq, ordinal, payload)));
            return ordinal;
        }

        foreach (var write in writes)
        {
            switch (write)
            {
                case StorageWrite.Conversation conversation:
                    mainWrites.Add(new JsonlMainOperation.OperationConversation(conversation.Value));
                    break;
                case StorageWrite.Entry entry:
                    mainWrites.Add(new JsonlMainOperation.OperationEntry(entry.Value));
                    break;
                case StorageWrite.Submission submission:
                    mainWrites.Add(new JsonlMainOperation.OperationSubmission(submission.Value));
                    break;
                case StorageWrite.DocumentRetire retire:
                    mainWrites.Add(new JsonlMainOperation.DocumentRetire(retire.Id));
                    break;
                case StorageWrite.Task task:
                    if (task.Value.State.Status == TaskStatus.Terminal)
                    {
                        mainWrites.Add(new JsonlMainOperation.OperationTask(task.Value));
                    }
                    else
                    {
                        var ordinal = AddSidecar(
                            TaskFile(task.Value.Id.Value),
                            new JsonlSidecarPayload.TaskPayload(task.Value));
                        mainWrites.Add(new JsonlMainOperation.TaskSidecar(task.Value.Id, ordinal));
                    }
                    break;
                case StorageWrite.DocumentCreateWrite create:
                {
                    var ordinal = AddSidecar(
                        DocumentFile(create.Record.Id.Value),
                        new JsonlSidecarPayload.DocumentPayload(create.Record.Id, create.Content));
                    mainWrites.Add(new JsonlMainOperation.DocumentCreateOp(create.Record, ordinal));
                    break;
                }
                case StorageWrite.DocumentChange change:
                {
                    var ordinal = AddSidecar(
                        DocumentFile(change.Id.Value),
                        new JsonlSidecarPayload.DocumentPayload(change.Id, change.Content));
                    mainWrites.Add(new JsonlMainOperation.DocumentChangeOp(change.Id, ordinal));
                    break;
                }
                case StorageWrite.DocumentCopyWrite:
                    // 副本已由 MemoryStorage.prepareCommit 解析为带 base 的创建写入；与 TS 一致不单独编码。
                    break;
            }
        }

        var sidecars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (file, record) in sidecarRecords)
        {
            var line = JsonlStorageJson.SerializeSidecar(record);
            sidecars[file] = sidecars.TryGetValue(file, out var existing) ? existing + line : line;
        }
        return new EncodedCommit(JsonlStorageJson.SerializeCommit(seq, mainWrites), sidecars);
    }

    private Dictionary<string, string> PlanReclamations(
        IReadOnlyList<StorageWrite> writes,
        EncodedCommit encoded)
    {
        var createdCurrentOnly = new HashSet<long>();
        var retiredDocuments = new HashSet<long>();
        var baseDocuments = new HashSet<long>();
        var finalTasks = new Dictionary<long, TaskRecord>();

        foreach (var write in writes)
        {
            switch (write)
            {
                case StorageWrite.DocumentCreateWrite create:
                    if (IsCurrentOnly(create.Record)) createdCurrentOnly.Add(create.Record.Id.Value);
                    break;
                case StorageWrite.DocumentChange change:
                    if (change.Content is DocumentContent.Base) baseDocuments.Add(change.Id.Value);
                    break;
                case StorageWrite.DocumentRetire retire:
                    retiredDocuments.Add(retire.Id.Value);
                    break;
                case StorageWrite.Task task:
                    finalTasks[task.Value.Id.Value] = task.Value;
                    break;
            }
        }

        bool IsCurrentOnlyDocument(long id) => currentOnlyDocuments.Contains(id) || createdCurrentOnly.Contains(id);

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in retiredDocuments)
            if (IsCurrentOnlyDocument(id)) replacements[DocumentFile(id)] = string.Empty;

        foreach (var id in baseDocuments)
        {
            if (!IsCurrentOnlyDocument(id) || retiredDocuments.Contains(id)) continue;
            var file = DocumentFile(id);
            if (encoded.Sidecars.TryGetValue(file, out var content)) replacements[file] = content;
        }

        foreach (var (id, task) in finalTasks)
        {
            if (task.State.Status != TaskStatus.Terminal) continue;
            if (liveTaskSidecars.Contains(id) || encoded.Sidecars.ContainsKey(TaskFile(id)))
                replacements[TaskFile(id)] = string.Empty;
        }

        return replacements;
    }

    private void AdoptSidecarState(IReadOnlyList<StorageWrite> writes)
    {
        foreach (var write in writes)
        {
            switch (write)
            {
                case StorageWrite.DocumentCreateWrite create:
                    if (IsCurrentOnly(create.Record)) currentOnlyDocuments.Add(create.Record.Id.Value);
                    break;
                case StorageWrite.Task task:
                    if (task.Value.State.Status == TaskStatus.Terminal) liveTaskSidecars.Remove(task.Value.Id.Value);
                    else liveTaskSidecars.Add(task.Value.Id.Value);
                    break;
            }
        }
    }

    /// <summary>主标记已经发布该状态，因此回收是尽力而为的可重试维护。对应 TS <c>reclaimSidecars</c>。</summary>
    private async Task ReclaimSidecarsAsync(IReadOnlyDictionary<string, string> replacements)
    {
        if (replacements.Count == 0) return;
        if (fsync)
        {
            var flushed = await fs.FlushFileAsync(mainPath, DefaultContext).ConfigureAwait(false);
            if (!flushed.IsOk) return;
        }
        foreach (var (file, content) in replacements)
            await ReplaceSidecarAsync(file, content).ConfigureAwait(false);
    }

    private async Task ReplaceSidecarAsync(string file, string content)
    {
        var path = await fs.JoinPathAsync([directory, file], DefaultContext).ConfigureAwait(false);
        if (!path.IsOk) return;
        if (content.Length == 0)
        {
            await fs.RemoveAsync(path.Value, new RemoveOptions { Force = true }, DefaultContext).ConfigureAwait(false);
            return;
        }
        var temporaryPath = await fs.JoinPathAsync(
            [directory, file + ReclaimSuffix], DefaultContext).ConfigureAwait(false);
        if (!temporaryPath.IsOk) return;
        var written = await fs.WriteFileAsync(temporaryPath.Value, content, DefaultContext).ConfigureAwait(false);
        if (!written.IsOk) return;
        if (fsync)
        {
            var flushed = await fs.FlushFileAsync(temporaryPath.Value, DefaultContext).ConfigureAwait(false);
            if (!flushed.IsOk) return;
        }
        await fs.RenameFileAsync(temporaryPath.Value, path.Value, DefaultContext).ConfigureAwait(false);
    }

    // ─── 恢复 ────────────────────────────────────────────────────────────────

    private async Task RecoverAsync()
    {
        var main = await ReadLinesAsync(mainPath, MainFile, JsonlStorageJson.ParseCommit).ConfigureAwait(false);

        long previousSeq = 0;
        foreach (var marker in main.Lines)
        {
            if (marker.Value.Seq.Value <= previousSeq)
                throw new JsonlCorruptionError($"Commit sequence does not strictly increase in {MainFile}");
            previousSeq = marker.Value.Seq.Value;
        }

        var listed = await fs.ListDirAsync(directory, DefaultContext).ConfigureAwait(false);
        if (!listed.IsOk) throw ErrorFromFile("directory listing", listed.Error);
        foreach (var info in listed.Value)
        {
            if (info.Kind == FileKind.File && IsReclaimFileName(info.Name))
                await fs.RemoveAsync(info.Path, new RemoveOptions { Force = true }, DefaultContext).ConfigureAwait(false);
        }

        var sidecarFiles = listed.Value
            .Where(info => info.Kind == FileKind.File && IsSidecarFileName(info.Name))
            .Select(info => info.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var parsedFiles = new Dictionary<string, ParsedFile<JsonlSidecarRecord>>(StringComparer.Ordinal);
        var recordByKey = new Dictionary<string, ParsedLine<JsonlSidecarRecord>>(StringComparer.Ordinal);
        foreach (var file in sidecarFiles)
        {
            var path = await fs.JoinPathAsync([directory, file], DefaultContext).ConfigureAwait(false);
            if (!path.IsOk) throw ErrorFromFile("path join", path.Error);
            var parsed = await ReadLinesAsync(
                path.Value, file, (text, line) => JsonlStorageJson.ParseSidecar(text, file, line)).ConfigureAwait(false);
            parsedFiles[file] = parsed;

            JsonlSidecarRecord? previous = null;
            foreach (var line in parsed.Lines)
            {
                if (previous is not null
                    && (line.Value.Seq.Value < previous.Seq.Value
                        || (line.Value.Seq.Value == previous.Seq.Value && line.Value.Ordinal <= previous.Ordinal)))
                    throw new JsonlCorruptionError($"Sidecar records are out of order in {file}");
                previous = line.Value;
                recordByKey[SidecarKey(file, line.Value.Seq, line.Value.Ordinal)] = line;
            }
        }

        var currentOnly = new HashSet<long>();
        var retired = new HashSet<long>();
        var finalTaskIsLive = new Dictionary<long, bool>();
        foreach (var line in main.Lines)
        {
            foreach (var operation in line.Value.Writes)
            {
                switch (operation)
                {
                    case JsonlMainOperation.DocumentCreateOp create:
                        if (IsCurrentOnly(create.Record)) currentOnly.Add(create.Record.Id.Value);
                        break;
                    case JsonlMainOperation.DocumentRetire retire:
                        retired.Add(retire.Id.Value);
                        break;
                    case JsonlMainOperation.OperationTask task:
                        finalTaskIsLive[task.Value.Id.Value] = false;
                        break;
                    case JsonlMainOperation.TaskSidecar sidecar:
                        finalTaskIsLive[sidecar.Id.Value] = true;
                        break;
                }
            }
        }
        var retiredCurrentOnly = new HashSet<long>(retired.Where(currentOnly.Contains));

        var latestBases = new Dictionary<long, JsonlSidecarRecord>();
        foreach (var line in main.Lines)
        {
            foreach (var operation in line.Value.Writes)
            {
                long id;
                int ordinal;
                switch (operation)
                {
                    case JsonlMainOperation.DocumentCreateOp create:
                        id = create.Record.Id.Value;
                        ordinal = create.Ordinal;
                        break;
                    case JsonlMainOperation.DocumentChangeOp change:
                        id = change.Id.Value;
                        ordinal = change.Ordinal;
                        break;
                    default:
                        continue;
                }
                if (!currentOnly.Contains(id)) continue;
                if (!recordByKey.TryGetValue(
                        SidecarKey(DocumentFile(id), line.Value.Seq, ordinal), out var recordLine))
                    continue;
                if (recordLine.Value.Payload is not JsonlSidecarPayload.DocumentPayload payload
                    || payload.Id.Value != id
                    || payload.Content is not DocumentContent.Base)
                    continue;

                var record = recordLine.Value;
                if (!latestBases.TryGetValue(id, out var previous)
                    || record.Seq.Value > previous.Seq.Value
                    || (record.Seq.Value == previous.Seq.Value && record.Ordinal > previous.Ordinal))
                    latestBases[id] = record;
            }
        }

        var terminalTasks = new HashSet<long>(
            finalTaskIsLive.Where(pair => !pair.Value).Select(pair => pair.Key));
        var confirmed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in main.Lines)
        {
            var marker = line.Value;
            var writes = new List<StorageWrite>();
            foreach (var operation in marker.Writes)
            {
                switch (operation)
                {
                    case JsonlMainOperation.OperationConversation conversation:
                        writes.Add(new StorageWrite.Conversation(conversation.Value));
                        break;
                    case JsonlMainOperation.OperationEntry entry:
                        writes.Add(new StorageWrite.Entry(entry.Value));
                        break;
                    case JsonlMainOperation.OperationSubmission submission:
                        writes.Add(new StorageWrite.Submission(submission.Value));
                        break;
                    case JsonlMainOperation.OperationTask task:
                        writes.Add(new StorageWrite.Task(task.Value));
                        break;
                    case JsonlMainOperation.DocumentRetire retire:
                        writes.Add(new StorageWrite.DocumentRetire(retire.Id));
                        break;
                    case JsonlMainOperation.TaskSidecar sidecar:
                    {
                        var optional = terminalTasks.Contains(sidecar.Id.Value);
                        var record = ConfirmRecord(
                            marker.Seq, sidecar.Ordinal, TaskFile(sidecar.Id.Value), recordByKey, confirmed, optional);
                        if (record is not null)
                        {
                            if (record.Payload is not JsonlSidecarPayload.TaskPayload payload
                                || payload.Value.Id.Value != sidecar.Id.Value)
                                throw new JsonlCorruptionError(
                                    $"Confirmed task sidecar data does not match commit {marker.Seq.Value}");
                            if (!optional) writes.Add(new StorageWrite.Task(payload.Value));
                        }
                        break;
                    }
                    case JsonlMainOperation.DocumentCreateOp create:
                    {
                        var id = create.Record.Id.Value;
                        var reclaimed = retiredCurrentOnly.Contains(id)
                                        || IsBeforeLatestBase(id, marker.Seq, create.Ordinal, latestBases);
                        var record = ConfirmRecord(
                            marker.Seq, create.Ordinal, DocumentFile(id), recordByKey, confirmed, reclaimed);
                        var content = ConfirmedDocumentContent(record, marker.Seq, id);
                        if (content is not null && content is not DocumentContent.Base)
                            throw new JsonlCorruptionError(
                                $"Document creation lacks a confirmed base in commit {marker.Seq.Value}");
                        var effective = reclaimed || content is null
                            ? new DocumentContent.Base(1, new Dictionary<string, object?>())
                            : (DocumentContent.Base)content;
                        writes.Add(new StorageWrite.DocumentCreateWrite(create.Record, effective));
                        break;
                    }
                    case JsonlMainOperation.DocumentChangeOp change:
                    {
                        var id = change.Id.Value;
                        var reclaimed = retiredCurrentOnly.Contains(id)
                                        || IsBeforeLatestBase(id, marker.Seq, change.Ordinal, latestBases);
                        var record = ConfirmRecord(
                            marker.Seq, change.Ordinal, DocumentFile(id), recordByKey, confirmed, reclaimed);
                        var content = ConfirmedDocumentContent(record, marker.Seq, id);
                        if (!reclaimed && content is not null)
                            writes.Add(new StorageWrite.DocumentChange(change.Id, content));
                        break;
                    }
                }
            }

            try
            {
                memory.PrepareCommit(writes, marker.Seq).Apply();
            }
            catch (Exception error)
            {
                throw new JsonlCorruptionError(
                    $"Invalid committed state at sequence {marker.Seq.Value}", error);
            }
        }

        var reclamations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (file, parsed) in parsedFiles)
        {
            long? unconfirmedAt = null;
            foreach (var line in parsed.Lines)
            {
                var key = SidecarKey(file, line.Value.Seq, line.Value.Ordinal);
                if (confirmed.Contains(key))
                {
                    if (unconfirmedAt is not null)
                        throw new JsonlCorruptionError($"Confirmed record follows an unconfirmed tail in {file}");
                }
                else if (unconfirmedAt is null)
                {
                    unconfirmedAt = line.Start;
                }
            }
            if (unconfirmedAt is { } size)
            {
                var truncated = await fs.TruncateFileAsync(parsed.Path, size, DefaultContext).ConfigureAwait(false);
                if (!truncated.IsOk) throw ErrorFromFile($"tail truncation of {file}", truncated.Error);
            }

            var numericId = NumericIdOf(file);
            var confirmedLines = parsed.Lines
                .Where(line => confirmed.Contains(SidecarKey(file, line.Value.Seq, line.Value.Ordinal)))
                .ToList();
            List<ParsedLine<JsonlSidecarRecord>>? retained = null;
            if (file.StartsWith("task-", StringComparison.Ordinal) && terminalTasks.Contains(numericId))
            {
                retained = [];
            }
            else if (file.StartsWith("doc-", StringComparison.Ordinal))
            {
                if (retiredCurrentOnly.Contains(numericId))
                    retained = [];
                else if (latestBases.ContainsKey(numericId))
                    retained = confirmedLines
                        .Where(line => !IsBeforeLatestBase(numericId, line.Value.Seq, line.Value.Ordinal, latestBases))
                        .ToList();
            }

            if (retained is not null
                && (retained.Count < confirmedLines.Count || retained.Count == 0))
                reclamations[file] = string.Concat(retained.Select(line => JsonlStorageJson.SerializeSidecar(line.Value)));
        }
        await ReclaimSidecarsAsync(reclamations).ConfigureAwait(false);

        foreach (var id in currentOnly) currentOnlyDocuments.Add(id);
        foreach (var (id, live) in finalTaskIsLive)
            if (live) liveTaskSidecars.Add(id);
    }

    private static DocumentContent? ConfirmedDocumentContent(
        JsonlSidecarRecord? record,
        Seq markerSeq,
        long id)
    {
        if (record is null) return null;
        if (record.Payload is not JsonlSidecarPayload.DocumentPayload payload || payload.Id.Value != id)
            throw new JsonlCorruptionError(
                $"Confirmed document sidecar data does not match commit {markerSeq.Value}");
        return payload.Content;
    }

    private static JsonlSidecarRecord? ConfirmRecord(
        Seq markerSeq,
        int ordinal,
        string file,
        IReadOnlyDictionary<string, ParsedLine<JsonlSidecarRecord>> recordByKey,
        HashSet<string> confirmed,
        bool optional)
    {
        var key = SidecarKey(file, markerSeq, ordinal);
        if (confirmed.Contains(key)) throw new JsonlCorruptionError("Sidecar record is confirmed more than once");
        if (!recordByKey.TryGetValue(key, out var line))
        {
            if (optional) return null;
            throw new JsonlCorruptionError(
                $"Missing confirmed sidecar record {file} at sequence {markerSeq.Value}");
        }
        confirmed.Add(key);
        return line.Value;
    }

    private static bool IsBeforeLatestBase(
        long id,
        Seq seq,
        int ordinal,
        IReadOnlyDictionary<long, JsonlSidecarRecord> latestBases)
        => latestBases.TryGetValue(id, out var @base)
           && (seq.Value < @base.Seq.Value || (seq.Value == @base.Seq.Value && ordinal < @base.Ordinal));

    // ─── 文件读取 ────────────────────────────────────────────────────────────

    private async Task<ParsedFile<T>> ReadLinesAsync<T>(
        string path,
        string name,
        Func<string, int, T> parse)
    {
        var read = await fs.ReadBinaryFileAsync(path, DefaultContext).ConfigureAwait(false);
        if (!read.IsOk)
        {
            if (read.Error.Code == FileErrorCode.NotFound) return new ParsedFile<T>(path, []);
            throw ErrorFromFile($"read of {name}", read.Error);
        }

        var bytes = read.Value;
        var completeSize = bytes.Length;
        if (completeSize > 0 && bytes[completeSize - 1] != 0x0a)
        {
            completeSize = Array.LastIndexOf(bytes, (byte)0x0a, completeSize - 1) + 1;
            var truncated = await fs.TruncateFileAsync(path, completeSize, DefaultContext).ConfigureAwait(false);
            if (!truncated.IsOk) throw ErrorFromFile($"torn-line truncation of {name}", truncated.Error);
        }

        var lines = new List<ParsedLine<T>>();
        var start = 0;
        var lineNumber = 1;
        for (var end = 0; end < completeSize; end++)
        {
            if (bytes[end] != 0x0a) continue;
            string text;
            try
            {
                text = StrictUtf8.GetString(bytes, start, end - start);
            }
            catch (DecoderFallbackException error)
            {
                throw new JsonlCorruptionError($"Invalid UTF-8 in complete {name} line {lineNumber}", error);
            }
            lines.Add(new ParsedLine<T>(parse(text, lineNumber), start));
            start = end + 1;
            lineNumber++;
        }
        return new ParsedFile<T>(path, lines);
    }

    private async Task<string> ResolveFileAsync(string file)
    {
        var path = await fs.JoinPathAsync([directory, file], DefaultContext).ConfigureAwait(false);
        if (!path.IsOk) throw ErrorFromFile("path join", path.Error);
        return path.Value;
    }

    // ─── 文件名与序列 ────────────────────────────────────────────────────────

    private static string DocumentFile(long id) => $"doc-{id.ToString(CultureInfo.InvariantCulture)}{SidecarSuffix}";

    private static string TaskFile(long id) => $"task-{id.ToString(CultureInfo.InvariantCulture)}{SidecarSuffix}";

    private static bool IsSidecarFileName(string name)
    {
        if (!name.EndsWith(SidecarSuffix, StringComparison.Ordinal)) return false;
        var stem = name[..^SidecarSuffix.Length];
        var dash = stem.IndexOf('-');
        if (dash < 0) return false;
        var prefix = stem[..dash];
        if (prefix is not ("doc" or "task")) return false;
        var digits = stem[(dash + 1)..];
        if (digits.Length == 0 || (digits.Length > 1 && digits[0] == '0')) return false;
        foreach (var character in digits)
            if (character is < '0' or > '9') return false;
        return true;
    }

    private static bool IsReclaimFileName(string name)
        => name.EndsWith(ReclaimSuffix, StringComparison.Ordinal)
           && IsSidecarFileName(name[..^ReclaimSuffix.Length]);

    private static long NumericIdOf(string file)
    {
        var stem = file[..^SidecarSuffix.Length];
        var start = stem.IndexOf('-') + 1;
        return long.Parse(stem[start..], CultureInfo.InvariantCulture);
    }

    private static string SidecarKey(string file, Seq seq, int ordinal)
        => $"[\"{file}\",{seq.Value.ToString(CultureInfo.InvariantCulture)},"
           + $"{ordinal.ToString(CultureInfo.InvariantCulture)}]";

    private static bool IsCurrentOnly(DocumentCreate record)
        => record.Scope is not DocumentScope.ConversationScope
           || record.History == ConversationHistory.Latest;

    // ─── 生命周期 ────────────────────────────────────────────────────────────

    private MemoryStorage Store()
    {
        AssertUsable();
        return memory;
    }

    private JsonlStoragePoisonedError Poison(Exception cause)
    {
        var error = poisonError ??= new JsonlStoragePoisonedError(cause);
        return error;
    }

    private void AssertUsable()
    {
        if (closed) throw new InvalidOperationException("JsonlStorage is closed");
        if (poisonError is not null) throw poisonError;
    }

    private static Exception ErrorFromFile(string action, FileError error)
        => new InvalidOperationException($"JSONL {action} failed: {error.Message}", error);

    // ─── 内部类型 ────────────────────────────────────────────────────────────

    private sealed record EncodedCommit(string Marker, IReadOnlyDictionary<string, string> Sidecars);

    private sealed record ParsedLine<T>(T Value, long Start);

    private sealed record ParsedFile<T>(string Path, IReadOnlyList<ParsedLine<T>> Lines);
}
