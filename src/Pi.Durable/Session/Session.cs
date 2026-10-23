using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Pi.Durable.Storage;
using Pi.Durable.Types;

namespace Pi.Durable.Session;

/// <summary>在一个存储后端上打开 Session 内核。对应 TS <c>createSession</c>。</summary>
public static class DurableSessions
{
    /// <inheritdoc cref="CreateSession" />
    public static DurableSession Create(IStorage storage) => new(storage);

    /// <summary>在一个存储后端上打开 Session 内核。</summary>
    public static DurableSession CreateSession(IStorage storage) => new(storage);
}

/// <summary>
/// Session 内核：一条变更线、已加载文档跟踪器缓存与已提交发布。
/// 对应 TS <c>session/session.ts</c> 的 <c>SessionImpl</c>。
/// 只有已提交状态可观察；每个提交回调、准备、Storage 结算、采纳与发布入队都在持有变更线时运行；
/// 监听器稍后运行。Harness 派生并覆写 <see cref="OnConversationCreatedAsync"/> /
/// <see cref="OnBeforeCloseAsync"/>（对应 TS 的 protected 钩子）。
/// </summary>
public class DurableSession : ISession
{
    private readonly IStorage _storage;
    private readonly Dictionary<string, LoadedDocument> _documents = [];
    /// <summary>没有观察者的已缓存文档，按最后使用时间从旧到新。对应 TS <c>#idleDocuments</c>。</summary>
    private readonly LinkedList<IdleDocument> _idleDocuments = [];
    private readonly Dictionary<string, LinkedListNode<IdleDocument>> _idleIndex = [];
    /// <summary>每个文档化身（记录 ID）的观察者计数：有观察者的化身永不驱逐。对应 TS <c>#attachedDocuments</c>。</summary>
    private readonly Dictionary<DocumentId, int> _attachedDocuments = [];
    /// <summary>保护上面三个容器的记账；缓存记账不得改变它所跟随的操作的结果。</summary>
    private readonly object _documentCacheGate = new();
    private readonly Func<long> _clock;
    private readonly Func<double> _documentRetentionMs;
    /// <summary>待执行的驱逐：最旧空闲文档的到期时刻（毫秒，可能为 <see cref="double.PositiveInfinity"/>）。</summary>
    private DocumentSweep? _documentSweep;
    private int _sweepJobs;
    private readonly HashSet<Action<CommitPublication, Context>> _commitListeners = [];
    private readonly HashSet<Action> _closeListeners = [];
    private readonly ITransactionHost _host;
    private readonly object _enqueueGate = new();
    private Task _tail = Task.CompletedTask;
    private Task? _closing;
    private Exception? _poison;

    /// <summary>缺省文档保留时长：十分钟（与保留上下文一致）。</summary>
    private static double _defaultDocumentRetention = 600_000;

    /// <summary>
    /// 保留时长：给定值是毫秒数（<c>0</c> 表示用完即弃，<see cref="double.PositiveInfinity"/> 表示永不驱逐）
    /// 时用它，否则用缺省值。对应 TS <c>documentRetention</c>。
    /// </summary>
    public static double DocumentRetention(double? ms) => ms is { } value && value >= 0 ? value : _defaultDocumentRetention;

    /// <summary>测试用：改变「保持缺省」的 Session 所用的保留时长。对应 TS <c>setDefaultDocumentRetention</c>。</summary>
    public static void SetDefaultDocumentRetention(double ms) => _defaultDocumentRetention = ms;

    /// <summary>
    /// 在一个存储后端上打开 Session 内核。<paramref name="now"/> 是任务时间用的墙钟（缺省
    /// <c>DateTimeOffset.UtcNow</c>）；<paramref name="documentRetentionMs"/> 每次使用时读取，使设置可以随时改。
    /// </summary>
    public DurableSession(
        IStorage storage, Func<long>? now = null, Func<double?>? documentRetentionMs = null)
    {
        _storage = storage;
        // 缓存记账绝不能改变它所跟随的操作的结果：时钟或设置抛出时按「现在」与缺省值处理。
        _clock = () =>
        {
            try
            {
                return now?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (Exception)
            {
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        };
        _documentRetentionMs = () =>
        {
            try
            {
                return DocumentRetention(documentRetentionMs?.Invoke());
            }
            catch (Exception)
            {
                return DocumentRetention(null);
            }
        };
        _host = new SessionHost(this);
    }

    private sealed record IdleDocument(string AddressId, double Since);

    private sealed class DocumentSweep(double at)
    {
        public double At { get; } = at;

        /// <summary>后台定时器：不拖住进程退出（对应上游 <c>unref()</c>）。</summary>
        public System.Threading.Timer? Timer { get; set; }
    }

    /// <summary>测试用：缓存里的文档数、空闲数、被钉住的化身数与迄今为止排到线上的驱逐作业数。</summary>
    public (int Cached, int Idle, int Attached, int Sweeps) DocumentCacheSizes
    {
        get
        {
            lock (_documentCacheGate)
            {
                return (_documents.Count, _idleDocuments.Count, _attachedDocuments.Count, _sweepJobs);
            }
        }
    }

    /// <summary>
    /// 读缓存里的文档。<see cref="SnapshotCoreAsync"/> 在变更线外读，驱逐在线上写，所以两边都走这把锁：
    /// 不锁的并发读写会让 <see cref="Dictionary{TKey,TValue}"/> 处于未定义状态。
    /// </summary>
    private LoadedDocument? CachedDocument(string addressId)
    {
        lock (_documentCacheGate) return _documents.GetValueOrDefault(addressId);
    }

    private void StoreDocument(string addressId, LoadedDocument document)
    {
        lock (_documentCacheGate) _documents[addressId] = document;
    }

    /// <inheritdoc />
    public Task<TResult> CommitAsync<TResult>(Func<ITx, Task<TResult>> change, Context context)
        => CommitWithAsync(tx => change(tx), context, scope: null);

    /// <summary>无返回值回调的重载（对应 TS 回调隐式返回 undefined）。</summary>
    public Task CommitAsync(Func<ITx, Task> change, Context context)
        => CommitWithAsync<object?>(
            async tx =>
            {
                await change(tx).ConfigureAwait(false);
                return null;
            },
            context,
            scope: null);

    /// <summary>
    /// 内部提交：暴露具体事务与其内部操作（保留 ID 根引导与任务替换）。
    /// <paramref name="scope"/> 设置缺省 <c>tx.createTask()</c> 对话与追加条目归属的任务。
    /// 对应 TS <c>commitWith</c>。
    /// </summary>
    public Task<TResult> CommitWithAsync<TResult>(
        Func<Transaction, Task<TResult>> change, Context context, TransactionScope? scope = null)
    {
        try
        {
            AssertUsable();
        }
        catch (Exception error)
        {
            return Task.FromException<TResult>(error);
        }
        return Enqueue(() => RunCommitAsync(change, context, scope));
    }

    /// <summary>
    /// 内部：在变更线上运行一个只读作业，使多重读取派生观察同一个已提交状态。
    /// 对应 TS <c>readOnLine</c>。
    /// </summary>
    public Task<T> ReadOnLineAsync<T>(Func<Task<T>> job)
    {
        try
        {
            AssertUsable();
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
        return Enqueue(async () =>
        {
            AssertHealthy();
            return await job().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// 内部：在变更线上分页扫描任务。供已在变更线上运行的作业使用（见 <see cref="ReadOnLineAsync"/>）。
    /// 对应 TS <c>storage.scanTasks</c>（线上读取）。
    /// </summary>
    public Task<Page<TaskRecord>> ScanTasksOnLineAsync(
        TaskQuery? query, int limit, IReadOnlyDictionary<string, object?>? cursor = null)
        => _storage.ScanTasksAsync(query, limit, cursor);

    /// <summary>
    /// 内部：一个对话文档的当前化身与值，供已在变更线上运行的作业使用（见 <see cref="ReadOnLineAsync"/>）。
    /// 缺失文档为 undefined。对应 TS <c>conversationDocumentOnLine</c>。
    /// </summary>
    public async Task<ConversationDocumentSnapshot?> ConversationDocumentOnLineAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        var definition = DurableDocuments.Erase(token.Definition);
        var resolved = DurableDocuments.ResolveAddress(definition, [conversationId.Value]);
        var loaded = await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context)
            .ConfigureAwait(false);
        if (loaded is null) return null;
        DurableDocuments.CheckRecordScope(definition, loaded.Record);
        DurableDocuments.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        return new ConversationDocumentSnapshot(
            loaded.Record, loaded.ValueVersion, loaded.Tracker.Value);
    }

    /// <summary>一个对话文档在变更线上的当前化身与值。对应 TS 返回形状。</summary>
    public sealed record ConversationDocumentSnapshot(
        DocumentRecord Record, int Version, IReadOnlyDictionary<string, object?>? Value);

    // ─── 快照 ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => (IReadOnlyDictionary<string, object?>?)await SnapshotCoreAsync(
            DurableDocuments.Erase(token.Definition), [], context).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => (IReadOnlyDictionary<string, object?>?)await SnapshotCoreAsync(
            DurableDocuments.Erase(token.Definition), [conversationId.Value], context).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T>(
        DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => (IReadOnlyDictionary<string, object?>?)await SnapshotCoreAsync(
            DurableDocuments.Erase(token.Definition), [taskId.Value], context).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => (IReadOnlyDictionary<string, object?>?)await SnapshotCoreAsync(
            DurableDocuments.Erase(token.Definition), [key], context).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => (IReadOnlyDictionary<string, object?>?)await SnapshotCoreAsync(
            DurableDocuments.Erase(token.Definition), [conversationId.Value, key], context).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => (IReadOnlyDictionary<string, object?>?)await SnapshotCoreAsync(
            DurableDocuments.Erase(token.Definition), [taskId.Value, key], context).ConfigureAwait(false);

    private async Task<object?> SnapshotCoreAsync(
        DurableDocuments.AnyDocDefinition definition, object?[] args, Context context)
    {
        AssertUsable();
        var resolved = DurableDocuments.ResolveAddress(definition, args);
        var cached = CachedDocument(resolved.Id);
        if (cached?.ValueVersion == definition.Version) TouchDocument(resolved.Id);
        var loaded = cached?.ValueVersion == definition.Version
            ? cached
            : await Enqueue(async () =>
            {
                AssertHealthy();
                return await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
        if (loaded is null) return null;
        DurableDocuments.CheckRecordScope(definition, loaded.Record);
        DurableDocuments.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        return loaded.Tracker.Value;
    }

    // ─── Watch ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => WatchDocCoreAsync<T>(DurableDocuments.Erase(token.Definition), [], context);

    /// <inheritdoc />
    public Task<IDocumentWatch<T>?> WatchDocAsync<T>(
        DocToken<T> token, ConversationId conversationId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => WatchDocCoreAsync<T>(DurableDocuments.Erase(token.Definition), [conversationId.Value], context);

    /// <inheritdoc />
    public Task<IDocumentWatch<T>?> WatchDocAsync<T>(DocToken<T> token, TaskId<object?> taskId, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => WatchDocCoreAsync<T>(DurableDocuments.Erase(token.Definition), [taskId.Value], context);

    /// <inheritdoc />
    public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => WatchDocCoreAsync<T>(DurableDocuments.Erase(token.Definition), [key], context);

    /// <inheritdoc />
    public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => WatchDocCoreAsync<T>(DurableDocuments.Erase(token.Definition), [conversationId.Value, key], context);

    /// <inheritdoc />
    public Task<IDocumentWatch<T>?> WatchDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, TaskId<object?> taskId, string key, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => WatchDocCoreAsync<T>(DurableDocuments.Erase(token.Definition), [taskId.Value, key], context);

    private async Task<IDocumentWatch<T>?> WatchDocCoreAsync<T>(
        DurableDocuments.AnyDocDefinition definition, object?[] args, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        AssertUsable();
        var resolved = DurableDocuments.ResolveAddress(definition, args);
        var signal = context.AbortSignal;
        var cancelled = signal?.IsCancellationRequested ?? false;
        using var registration = signal?.Register(() => cancelled = true);
        var watch = await Enqueue(async () =>
        {
            AssertHealthy();
            if (cancelled) throw CancellationError(signal!.Value);
            var loaded = await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context)
                .ConfigureAwait(false);
            if (cancelled) throw CancellationError(signal!.Value);
            if (loaded is null) return null;
            var inner = AttachDocument(definition, loaded);
            return new DocumentWatch<T>(inner);
        }).ConfigureAwait(false);
        if (watch is null) return null;
        if (cancelled)
        {
            watch.Cancel();
            throw CancellationError(signal!.Value);
        }
        if (signal is { } watchSignal) watch.ObserveCancellation(watchSignal);
        return watch;
    }

    // ─── 历史快照 ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T>(
        DocToken<T> token, ConversationId conversationId, EntryId at, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => SnapshotAsOfCoreAsync(DurableDocuments.Erase(token.Definition), [conversationId.Value, at.Value], context);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token, ConversationId conversationId, string key, EntryId at, Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => SnapshotAsOfCoreAsync(
            DurableDocuments.Erase(token.Definition), [conversationId.Value, key, at.Value], context);

    private async Task<IReadOnlyDictionary<string, object?>?> SnapshotAsOfCoreAsync(
        DurableDocuments.AnyDocDefinition definition, object?[] args, Context context)
    {
        AssertUsable();
        var resolved = DurableDocuments.ResolveAddress(definition, args);
        if (resolved.Address.Scope is not DocumentScope.ConversationScope addressScope)
        {
            throw new ArgumentException("Session.snapshotAsOf() requires a conversation document");
        }
        var conversationId = addressScope.ConversationId;
        var atValue = args[resolved.NextArgument];
        if (atValue is not long at || at is < -9007199254740991 or > 9007199254740991)
        {
            throw new ArgumentException("Session.snapshotAsOf() requires an entry ID");
        }
        var entryAt = EntryId.From(at);
        return await Enqueue(async () =>
        {
            AssertHealthy();
            var storedEntry = await _storage.GetEntryAsync(conversationId, entryAt).ConfigureAwait(false);
            if (storedEntry is null)
            {
                throw new InvalidOperationException(
                    $"Entry {entryAt.Value} is not visible from conversation {conversationId.Value}");
            }
            var address = resolved.Address with
            {
                Scope = new DocumentScope.ConversationScope(storedEntry.Entry.ConversationId),
            };
            var record = await _storage.FindDocumentAsync(
                address, new DocumentPoint.AtSeq(storedEntry.CommitSeq)).ConfigureAwait(false);
            if (record is null) return null;
            var stored = await _storage.GetDocumentAsync(record.Id, new DocumentPoint.AtSeq(storedEntry.CommitSeq))
                .ConfigureAwait(false);
            if (stored is null)
            {
                throw new InvalidOperationException(
                    $"Historical document {record.Id.Value} ({record.Kind}) cannot be read");
            }
            return DurableDocuments.MaterializeDocument(definition, stored);
        }).ConfigureAwait(false);
    }

    // ─── 生命周期 ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task CloseAsync(Context context)
    {
        if (_closing is null)
        {
            var cleanup = ContextSignals.WithoutAbortSignal(context);
            // 先封闭受理，再停止观察者；已受理工作在 Storage 关闭之前结算。
            _closing = Task.Run(() => OnBeforeCloseAsync())
                .ContinueWith(_ => Enqueue<object?>(async () =>
                {
                    _commitListeners.Clear();
                    lock (_documentCacheGate)
                    {
                        _documents.Clear();
                        _idleDocuments.Clear();
                        _idleIndex.Clear();
                        _attachedDocuments.Clear();
                        _documentSweep?.Timer?.Dispose();
                        _documentSweep = null;
                    }
                    await _storage.CloseAsync().ConfigureAwait(false);
                    return null;
                }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                .Unwrap();
            var listeners = _closeListeners.ToArray();
            _closeListeners.Clear();
            foreach (var listener in listeners) listener();
        }
        return ContextSignals.AwaitWithContext(_closing, context);
    }

    /// <summary>
    /// 在每个创建或 fork 对话的事务里、对话记录暂存之后运行。普通 Session 不暂存；
    /// Harness 暂存其内置文档。对应 TS protected <c>conversationCreated</c>。
    /// </summary>
    protected virtual Task OnConversationCreatedAsync(Transaction tx, ConversationRecord record)
        => Task.CompletedTask;

    /// <summary>在 close 封闭受理之后、变更线关闭 Storage 之前运行；不得拒绝。对应 TS protected <c>beforeClose</c>。</summary>
    protected virtual Task OnBeforeCloseAsync() => Task.CompletedTask;

    /// <summary>
    /// 供 <see cref="OnBeforeCloseAsync"/> 使用：在线上、在每个在封印之前被受理的提交结算之后，
    /// 把 <paramref name="writes"/> 当时返回的批次直接写入 Storage。不发布任何内容（Session 正在关闭）；
    /// 已失败的 Session 什么都不写。对应 TS protected <c>writeAtClose</c>。
    /// </summary>
    protected Task WriteAtCloseAsync(Func<IReadOnlyList<StorageWrite>> writes)
        => Enqueue<object?>(async () =>
        {
            if (_poison is not null) return null;
            var batch = writes();
            if (batch.Count > 0)
            {
                await _storage.CommitAsync(batch, static _ => { }, CancellationToken.None).ConfigureAwait(false);
            }

            return null;
        });

    /// <summary>
    /// 上报一个宿主回调或观察者的意外失败，附带说明来源的 <see cref="Harness.ReportInfo"/>，不得抛出。
    /// 普通 Session 丢弃它；Harness 覆盖它以转发给宿主。缺省来源为 <see cref="Harness.ReportInfo.ListenerError"/>。
    /// 对应 TS <c>report(error, info)</c>。
    /// </summary>
    public virtual void Report(Exception error, Harness.ReportInfo? info = null)
    {
    }

    /// <summary>使 Session 失败的错误（若尚未失败为 null）。对应 TS <c>failure</c>。</summary>
    public Exception? Failure => _poison;

    /// <summary>
    /// 注册同步的采纳后监听器。不得抛出、阻塞或调用 Session 操作。
    /// 对应 TS <c>subscribeCommits</c>；返回退订 <see cref="IDisposable"/>。
    /// </summary>
    public IDisposable SubscribeCommits(Action<CommitPublication, Context> listener)
    {
        AssertUsable();
        _commitListeners.Add(listener);
        return new Unsubscription(() => _commitListeners.Remove(listener));
    }

    /// <summary>注册在 close 开始时同步调用的监听器。不得抛出、阻塞或调用 Session 操作。</summary>
    public IDisposable SubscribeClose(Action listener)
    {
        AssertUsable();
        _closeListeners.Add(listener);
        return new Unsubscription(() => _closeListeners.Remove(listener));
    }

    /// <summary>在变更线上丢弃每个已加载跟踪器；之后的访问从 Storage 冷加载。</summary>
    public Task UnloadDocumentsAsync()
        => Enqueue<object?>(async () =>
        {
            lock (_documentCacheGate)
            {
                _documents.Clear();
                _idleDocuments.Clear();
                _idleIndex.Clear();
            }
            return null;
        });

    // ─── 文档缓存按使用量回收 ───────────────────────────────────────────────

    /// <summary>记一次对已缓存文档的使用：没有观察者的它成为最新的空闲文档，并安排一次驱逐。</summary>
    private void TouchDocument(string addressId)
    {
        var schedule = false;
        lock (_documentCacheGate)
        {
            var cached = _documents.GetValueOrDefault(addressId);
            if (cached is null || _attachedDocuments.ContainsKey(cached.Record.Id)) return;
            var now = _clock();
            var retention = _documentRetentionMs();
            if (_idleIndex.TryGetValue(addressId, out var existing))
            {
                // 热文档每「颗粒」（保留时长的十分之一，至多 1 秒）才后移一次：它可能因此提前一个颗粒被驱逐，
                // 代价是一次重载；提交密集的负载则省掉了每次重排。
                if (now - existing.Value.Since < Math.Min(1_000, retention / 10)) return;
                _idleDocuments.Remove(existing);
                _idleIndex.Remove(addressId);
            }

            _idleIndex[addressId] = _idleDocuments.AddLast(new IdleDocument(addressId, now));
            // 已有更早的待驱逐任务就不必改；保留时长被调小则要提前。
            schedule = _documentSweep is null || _documentSweep.At > now + retention;
        }

        if (schedule) ScheduleDocumentSweep();
    }

    /// <summary>把文档连同它的空闲记录一起丢掉。</summary>
    private void DropDocument(string addressId)
    {
        lock (_documentCacheGate)
        {
            _documents.Remove(addressId);
            if (_idleIndex.Remove(addressId, out var node)) _idleDocuments.Remove(node);
        }
    }

    /// <summary>
    /// 安排最旧空闲文档的驱逐：保留时长为 0 时立刻排到线上，否则到期用一个后台定时器唤醒。
    /// 定时器不拖住进程退出，因此不需要上游「定时器不可用就只靠提交驱逐」那条退路。
    /// </summary>
    private void ScheduleDocumentSweep()
    {
        if (_closing is not null) return;
        DocumentSweep sweep;
        double delay;
        lock (_documentCacheGate)
        {
            if (_idleDocuments.First is not { } oldest) return;
            var retention = _documentRetentionMs();
            // 无限保留：没有东西会过期，也不必留一个 24 天的定时器。
            if (double.IsPositiveInfinity(retention)) return;
            var at = oldest.Value.Since + retention;
            if (_documentSweep is not null && _documentSweep.At <= at) return;
            _documentSweep?.Timer?.Dispose();
            // 待驱逐任务保持到它的作业跑完，线上的忙碌期间不再排队。
            sweep = new DocumentSweep(at);
            _documentSweep = sweep;
            delay = at - _clock();
        }

        void Run()
        {
            Interlocked.Increment(ref _sweepJobs);
            _ = Enqueue<object?>(() =>
            {
                lock (_documentCacheGate)
                {
                    if (ReferenceEquals(_documentSweep, sweep)) _documentSweep = null;
                }
                if (_closing is null) SweepDocuments();
                return Task.FromResult<object?>(null);
            }).ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        }

        if (delay <= 0)
        {
            Run();
            return;
        }

        sweep.Timer = new System.Threading.Timer(
            _ => Run(), null, (long)Math.Min(delay, int.MaxValue), Timeout.Infinite);
    }

    /// <summary>在线上：把超过保留时长的空闲文档从旧到新驱逐，然后安排下一次。</summary>
    private void SweepDocuments()
    {
        lock (_documentCacheGate)
        {
            var expired = _clock() - _documentRetentionMs();
            while (_idleDocuments.First is { } first && first.Value.Since <= expired)
            {
                _idleDocuments.RemoveFirst();
                _idleIndex.Remove(first.Value.AddressId);
                _documents.Remove(first.Value.AddressId);
            }
        }

        ScheduleDocumentSweep();
    }

    /// <summary>
    /// 为一个观察者钉住某个已缓存化身；返回的函数在最后一个观察者摘下时解钉，使它重新成为空闲。
    /// 对应 TS <c>#pinDocument</c>。
    /// </summary>
    private Action PinDocument(string addressId, DocumentId recordId)
    {
        lock (_documentCacheGate)
        {
            _attachedDocuments[recordId] = _attachedDocuments.GetValueOrDefault(recordId) + 1;
            if (_idleIndex.Remove(addressId, out var node)) _idleDocuments.Remove(node);
        }

        var pinned = true;
        return () =>
        {
            if (!pinned) return;
            pinned = false;
            lock (_documentCacheGate)
            {
                var count = _attachedDocuments.GetValueOrDefault(recordId, 1) - 1;
                if (count > 0)
                {
                    _attachedDocuments[recordId] = count;
                    return;
                }

                _attachedDocuments.Remove(recordId);
            }

            // 它可能已被替换成别的化身：只有还是同一个才重新成为空闲。
            if (CachedDocument(addressId)?.Record.Id == recordId) TouchDocument(addressId);
        };
    }

    // ─── 提交执行 ───────────────────────────────────────────────────────────

    private async Task<TResult> RunCommitAsync<TResult>(
        Func<Transaction, Task<TResult>> change, Context context, TransactionScope? scope)
    {
        AssertHealthy();
        if (context.AbortSignal is { IsCancellationRequested: true } signal)
            throw new OperationCanceledException(signal);
        var tx = new Transaction(_host, context, scope);
        TResult result;
        try
        {
            result = await change(tx).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await tx.SettleFailureAsync().ConfigureAwait(false);
            // 结算失败也要驱逐到期的空闲文档：否则缓存只在成功提交时才收缩。
            SweepDocuments();
            throw;
        }
        IReadOnlyList<StorageWrite> writes;
        try
        {
            writes = await tx.SettleSuccessAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 结算已撤销每个变更。
            SweepDocuments();
            throw;
        }
        if (writes.Count == 0)
        {
            tx.Discard();
            SweepDocuments();
            return result;
        }
        // Storage 在批次持久后同步采纳（早于它的下一次提交）；这里不能让异常穿到 Storage，
        // 所以回调自己留住错误，等提交解析后再决定如何失败。
        List<DocumentCommitChange>? adoptedDocuments = null;
        Exception? adoptionError = null;
        var adopted = false;
        void Adopt(Seq committed)
        {
            try
            {
                adoptedDocuments = tx.Adopt(committed);
                adopted = true;
            }
            catch (Exception error)
            {
                adoptionError = error;
                adopted = true;
            }
        }

        Seq seq;
        try
        {
            // 一旦受理，调用方取消不会中断 Storage 结算。
            seq = await _storage.CommitAsync(
                writes, Adopt, ContextSignals.WithoutAbortSignal(context).AbortSignal ?? default)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // 采纳之前失败：没有已提交批次需要丢弃。采纳之后失败则没有暂存可丢。
            if (!adopted) tx.Discard();
            // 回调错误从不进入此分支；只有 StorageRejected 能保证无批次效果被提交。
            if (error is not StorageRejected)
            {
                _poison = error;
                Report(error, new Harness.ReportInfo.SessionFailed());
            }

            throw;
        }
        if (!adopted)
        {
            // Storage 已报告提交，却没采纳批次：内存与持久状态已经无法对齐。
            var error = new InvalidOperationException("Storage committed without adopting the batch");
            _poison = error;
            Report(error, new Harness.ReportInfo.SessionFailed());
            throw error;
        }
        if (adoptionError is not null)
        {
            // Storage 已提交；采纳失败使内存落后于持久状态。
            _poison = adoptionError;
            Report(adoptionError, new Harness.ReportInfo.SessionFailed());
            throw adoptionError;
        }
        // 刚写过的文档算一次使用：它们不会被紧接着的驱逐带走。
        foreach (var document in adoptedDocuments!)
        {
            switch (document)
            {
                case DocumentCommitChange.Document changed:
                    TouchDocument(DurableDocuments.AddressId(changed.Record));
                    break;
                case DocumentCommitChange.Copy copy:
                    TouchDocument(DurableDocuments.AddressId(copy.Record));
                    break;
            }
        }

        Publish(seq, writes, adoptedDocuments!, context);
        SweepDocuments();
        return result;
    }

    private void Publish(
        Seq seq,
        IReadOnlyList<StorageWrite> writes,
        IReadOnlyList<DocumentCommitChange> documents,
        Context context)
    {
        if (_commitListeners.Count == 0) return;
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
            }
        }
        foreach (var document in documents)
        {
            changes.Add(document switch
            {
                DocumentCommitChange.Document changed => new CommitChange.DocumentChanged(changed),
                DocumentCommitChange.Copy copy => new CommitChange.DocumentCopied(copy),
                _ => throw new ArgumentOutOfRangeException(nameof(document)),
            });
        }
        var publication = new CommitPublication { Seq = seq, Changes = changes };
        // 提交已持久化：一个监听器抛出既不让提交失败，也不跳过其余监听器。对应 TS <c>publish</c>。
        foreach (var listener in _commitListeners.ToArray())
        {
            try
            {
                listener(publication, context);
            }
            catch (Exception error)
            {
                Report(error);
            }
        }
    }

    /// <summary>
    /// 把一个 watch 附着到一个已提交化身：校验定义，然后转发本化身的已提交变更、它的退休与关闭。
    /// watch 的 detach 移除两个订阅。对应 TS <c>#attachDocument</c>。
    /// </summary>
    private CommittedWatch<IReadOnlyDictionary<string, object?>?> AttachDocument(
        DurableDocuments.AnyDocDefinition definition,
        LoadedDocument loaded)
    {
        DurableDocuments.CheckRecordScope(definition, loaded.Record);
        DurableDocuments.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        IDisposable unsubscribeCommit = new Unsubscription(() => { });
        IDisposable unsubscribeClose = new Unsubscription(() => { });
        // 被观察的化身留在缓存里；最后一个观察者摘下或它退休之后，它重新成为空闲。
        var unpin = PinDocument(loaded.AddressId, loaded.Record.Id);
        Action detach = () =>
        {
            unsubscribeCommit.Dispose();
            unsubscribeClose.Dispose();
            unpin();
        };
        CommittedWatch<IReadOnlyDictionary<string, object?>?> observer;
        try
        {
            observer = new CommittedWatch<IReadOnlyDictionary<string, object?>?>(
                loaded.Tracker.Value, detach, error => Report(error));
        }
        catch (Exception)
        {
            unpin();
            throw;
        }
        var observed = new ObservedVersion { Version = loaded.ValueVersion };
        unsubscribeCommit = SubscribeCommits((publication, context) =>
        {
            foreach (var change in publication.Changes)
            {
                if (change is not CommitChange.DocumentChanged document
                    || document.Change.Record.Id != loaded.Record.Id)
                {
                    continue;
                }
                if (document.Change.Value is null)
                {
                    // 已退休的化身不再变化：其 watch 投递退休，但不再持有任何东西。
                    unpin();
                    observer.Retire(context);
                    continue;
                }

                var ops = ObservedOperations(observed, document.Change);
                // 仅迁移的 base 对新版本的观察者没有任何改变。
                if (ops.Count == 0) continue;
                observer.Advance(document.Change.Value, ops, context);
            }
        });
        unsubscribeClose = SubscribeClose(() => observer.End(_poison));
        return observer;
    }

    private sealed class ObservedVersion
    {
        public int Version;
    }

    private async Task<LoadedDocument?> LoadDocumentAsync(
        DurableDocuments.AnyDocDefinition definition, string addressId, DocumentAddress address, Context context)
    {
        var cached = CachedDocument(addressId);
        // 跟踪器只服务于其值被物化时的版本的令牌；其他版本从 Storage 重载。
        if (cached?.ValueVersion == definition.Version)
        {
            TouchDocument(addressId);
            return cached;
        }
        if (cached is not null) DropDocument(addressId);
        var record = await _storage.FindDocumentAsync(address, new DocumentPoint.Current())
            .ConfigureAwait(false);
        if (record is null) return null;
        var stored = await _storage.GetDocumentAsync(record.Id, new DocumentPoint.Current())
            .ConfigureAwait(false);
        if (stored is null)
        {
            throw new InvalidOperationException(
                $"Current document {record.Id.Value} ({record.Kind}) cannot be read");
        }
        var value = DurableDocuments.MaterializeDocument(definition, stored);
        var loaded = new LoadedDocument
        {
            AddressId = addressId,
            Record = stored.Record,
            StoredVersion = stored.Version,
            ValueVersion = definition.Version,
            DeltasSinceBase = stored.DeltasSinceBase,
            Tracker = new Tracker<IReadOnlyDictionary<string, object?>>(value),
        };
        StoreDocument(addressId, loaded);
        TouchDocument(addressId);
        return loaded;
    }

    private Task<T> Enqueue<T>(Func<Task<T>> job)
    {
        lock (_enqueueGate)
        {
            var run = _tail.ContinueWith(
                _ => job(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();
            _tail = run.ContinueWith(
                static _ => { },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return run;
        }
    }

    private void AssertUsable()
    {
        if (_closing is not null) throw new InvalidOperationException("Session is closed");
        AssertHealthy();
    }

    private void AssertHealthy()
    {
        if (_poison is not null)
        {
            throw new InvalidOperationException(
                "Session is poisoned by a failed commit after storage admission; reopen it", _poison);
        }
    }

    /// <summary>
    /// 观察者为一次已提交变更应用的操作。以其他定义版本水化的观察者持有不同形状的值，
    /// 因此它收到新值作为根替换而非该形状的操作。对应 TS <c>observedOperations</c>。
    /// </summary>
    private static IReadOnlyList<DeltaOp> ObservedOperations(
        ObservedVersion observed, DocumentCommitChange.Document change)
    {
        if (change.Version == observed.Version) return change.Ops;
        observed.Version = change.Version!.Value;
        return [new DeltaOp.Replace(change.Value)];
    }

    private static OperationCanceledException CancellationError(CancellationToken signal)
        => new(signal);

    /// <summary>事务主机的默认实现，桥接到 Session 私有状态。</summary>
    private sealed class SessionHost(DurableSession session) : ITransactionHost
    {
        public IStorage Storage => session._storage;

        public LoadedDocument? Cached(string addressId)
        {
            var cached = session.CachedDocument(addressId);
            if (cached is not null) session.TouchDocument(addressId);
            return cached;
        }

        public Task<LoadedDocument?> LoadAsync(
            DurableDocuments.AnyDocDefinition definition,
            string addressId,
            DocumentAddress address,
            Context context)
            => session.LoadDocumentAsync(definition, addressId, address, context);

        public void Install(LoadedDocument document)
        {
            session.StoreDocument(document.AddressId, document);
            session.TouchDocument(document.AddressId);
        }

        public void Evict(string addressId, DocumentId recordId)
        {
            if (session.CachedDocument(addressId)?.Record.Id == recordId)
                session.DropDocument(addressId);
        }

        public Task ConversationCreatedAsync(Transaction tx, ConversationRecord record)
            => session.OnConversationCreatedAsync(tx, record);
    }

    private sealed class Unsubscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
