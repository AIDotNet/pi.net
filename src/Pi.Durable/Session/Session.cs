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
    /// <summary>Session 自己的观察者（调度器、提交受理）：跟随内存，先于宿主监听器运行。对应 TS <c>#internalListeners</c>。</summary>
    private readonly HashSet<Action<ObservedCommit, Context>> _observedListeners = [];
    /// <summary>持有还没落盘的变更，按化身；每个化身在缓存里被钉住直到落盘。对应 TS <c>#holds</c>。</summary>
    private readonly Dictionary<DocumentId, Hold> _holds = [];
    private HoldTimer? _holdTimer;
    /// <summary>延迟提交的缺省窗口（毫秒）。对应 TS <c>#persistWithinMs</c>。</summary>
    private readonly Func<double> _persistWithinMs;
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
        IStorage storage, Func<long>? now = null, Func<double?>? documentRetentionMs = null,
        bool readOnly = false, Func<double?>? persistWithinMs = null)
    {
        _storage = storage;
        _readOnly = readOnly;
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
        _persistWithinMs = () =>
        {
            try
            {
                return PersistWithin(persistWithinMs?.Invoke());
            }
            catch (Exception)
            {
                return PersistWithin(null);
            }
        };
        _host = new SessionHost(this);
    }

    /// <summary>拒绝每个带写的提交（<see cref="Harness.HarnessOptions.ReadOnly"/>）。</summary>
    private readonly bool _readOnly;

    private sealed record IdleDocument(string AddressId, double Since);

    private sealed class DocumentSweep(double at)
    {
        public double At { get; } = at;

        /// <summary>后台定时器：不拖住进程退出（对应上游 <c>unref()</c>）。</summary>
        public System.Threading.Timer? Timer { get; set; }
    }

    /// <summary>一份持有的文档：持有内容、最迟落盘时刻（Session 时钟）与解除钉住。对应 TS <c>Hold</c>。</summary>
    private sealed class Hold(HeldDocument held, double deadline, Action unpin)
    {
        public HeldDocument Held { get; } = held;

        public double Deadline { get; set; } = deadline;

        public Action Unpin { get; } = unpin;
    }

    /// <summary>已武装的持有落盘：最早截止时刻的后台定时器，<see cref="Fired"/> 表示作业已排队。</summary>
    private sealed class HoldTimer(double at)
    {
        public double At { get; } = at;

        public System.Threading.Timer? Timer { get; set; }

        public bool Fired { get; set; }
    }

    /// <summary>延迟提交的缺省窗口：<c>settings.progress.persistWithinMs</c>。</summary>
    private const double DefaultPersistWithinMs = 1_000;

    /// <summary>延迟提交的缺省窗口；0 或更大即用它（<see cref="double.PositiveInfinity"/> 表示只等读/写或关闭）。</summary>
    public static double PersistWithin(double? ms) => ms is { } value && value >= 0 ? value : DefaultPersistWithinMs;

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
    public Task<TResult> CommitAsync<TResult>(
        Func<ITx, Task<TResult>> change, Context context, CommitOptions? options = null)
        => CommitWithAsync(tx => change(tx), context, scope: null, options);

    /// <summary>无返回值回调的重载（对应 TS 回调隐式返回 undefined）。</summary>
    public Task CommitAsync(Func<ITx, Task> change, Context context, CommitOptions? options = null)
        => CommitWithAsync<object?>(
            async tx =>
            {
                await change(tx).ConfigureAwait(false);
                return null;
            },
            context,
            scope: null,
            options);

    /// <summary>
    /// 内部提交：暴露具体事务与其内部操作（保留 ID 根引导与任务替换）。
    /// <paramref name="scope"/> 设置缺省 <c>tx.createTask()</c> 对话与追加条目归属的任务。
    /// 对应 TS <c>commitWith</c>。
    /// </summary>
    public Task<TResult> CommitWithAsync<TResult>(
        Func<Transaction, Task<TResult>> change, Context context, TransactionScope? scope = null,
        CommitOptions? options = null)
    {
        int? withinMs;
        try
        {
            AssertUsable();
            withinMs = Deferral(options?.Persistence);
        }
        catch (Exception error)
        {
            return Task.FromException<TResult>(error);
        }
        return Enqueue(() => RunCommitAsync(change, context, scope, withinMs));
    }

    /// <summary>这次提交允许在内存里待多久再落盘；null 表示立即。对应 TS <c>#deferral()</c>。</summary>
    private int? Deferral(CommitPersistence? persistence)
    {
        if (persistence is not CommitPersistence.Deferred deferred) return null;
        if (deferred.WithinMs is not { } ms) return (int)_persistWithinMs();
        if (ms < 0) throw new ArgumentException("CommitPersistence.WithinMs 必须是 0 或更大的毫秒数", nameof(persistence));
        return ms;
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
            var inner = ObserveDocument(
                definition,
                loaded,
                (value, detach, report) => new DocumentObservationSink(value, detach, report));
            return new DocumentWatch<T>(inner);
        }).ConfigureAwait(false);
        if (watch is null) return null;
        if (cancelled)
        {
            watch.Abort();
            throw CancellationError(signal!.Value);
        }
        if (signal is { } watchSignal) watch.ObserveAbort(watchSignal);
        return watch;
    }

    // ─── Observe ────────────────────────────────────────────────────────────

    /// <summary>
    /// 在 Session 线上原子地附着 <paramref name="observer"/>：它拿到附着之后的每一帧（不折叠），退休投
    /// 「最后的值 + 空 ops + retired」，同一地址上的新化身投根替换。文档不存在时返回 <see langword="null"/>。
    /// 对应 TS <c>Session.observeDoc()</c>（<c>8b0caefc2</c>）。
    /// </summary>
    /// <remarks>
    /// 差异 C232：上游按令牌的类型给出 <c>Observer&lt;Readonly&lt;T&gt;&gt;</c>；端口的文档 watch 本来就是
    /// 无类型字典（<see cref="IDocumentWatch{T}"/>），这里沿用同一形状，免得为观察再物化一次强类型值。
    /// </remarks>
    public Task<Observation<IReadOnlyDictionary<string, object?>?>?> ObserveDocAsync<T>(
        DocToken<T> token,
        ISessionObserver<IReadOnlyDictionary<string, object?>?> observer,
        Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => ObserveDocCoreAsync(DurableDocuments.Erase(token.Definition), [], observer, context);

    /// <inheritdoc cref="ObserveDocAsync{T}(DocToken{T}, IObserver{IReadOnlyDictionary{string, object}}, Context)"/>
    public Task<Observation<IReadOnlyDictionary<string, object?>?>?> ObserveDocAsync<T>(
        DocToken<T> token,
        ConversationId conversationId,
        ISessionObserver<IReadOnlyDictionary<string, object?>?> observer,
        Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => ObserveDocCoreAsync(DurableDocuments.Erase(token.Definition), [conversationId.Value], observer, context);

    /// <inheritdoc cref="ObserveDocAsync{T}(DocToken{T}, IObserver{IReadOnlyDictionary{string, object}}, Context)"/>
    public Task<Observation<IReadOnlyDictionary<string, object?>?>?> ObserveDocAsync<T>(
        DocToken<T> token,
        TaskId<object?> taskId,
        ISessionObserver<IReadOnlyDictionary<string, object?>?> observer,
        Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => ObserveDocCoreAsync(DurableDocuments.Erase(token.Definition), [taskId.Value], observer, context);

    /// <inheritdoc cref="ObserveDocAsync{T}(DocToken{T}, IObserver{IReadOnlyDictionary{string, object}}, Context)"/>
    public Task<Observation<IReadOnlyDictionary<string, object?>?>?> ObserveDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token,
        string key,
        ISessionObserver<IReadOnlyDictionary<string, object?>?> observer,
        Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => ObserveDocCoreAsync(DurableDocuments.Erase(token.Definition), [key], observer, context);

    /// <inheritdoc cref="ObserveDocAsync{T}(DocToken{T}, IObserver{IReadOnlyDictionary{string, object}}, Context)"/>
    public Task<Observation<IReadOnlyDictionary<string, object?>?>?> ObserveDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token,
        ConversationId conversationId,
        string key,
        ISessionObserver<IReadOnlyDictionary<string, object?>?> observer,
        Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => ObserveDocCoreAsync(
            DurableDocuments.Erase(token.Definition), [conversationId.Value, key], observer, context);

    /// <inheritdoc cref="ObserveDocAsync{T}(DocToken{T}, IObserver{IReadOnlyDictionary{string, object}}, Context)"/>
    public Task<Observation<IReadOnlyDictionary<string, object?>?>?> ObserveDocAsync<T, TSeed>(
        DocFamilyToken<T, TSeed> token,
        TaskId<object?> taskId,
        string key,
        ISessionObserver<IReadOnlyDictionary<string, object?>?> observer,
        Context context)
        where T : class, IReadOnlyDictionary<string, object?>
        => ObserveDocCoreAsync(DurableDocuments.Erase(token.Definition), [taskId.Value, key], observer, context);

    private async Task<Observation<IReadOnlyDictionary<string, object?>?>?> ObserveDocCoreAsync(
        DurableDocuments.AnyDocDefinition definition,
        object?[] args,
        ISessionObserver<IReadOnlyDictionary<string, object?>?> observer,
        Context context)
    {
        AssertUsable();
        var resolved = DurableDocuments.ResolveAddress(definition, args);
        var signal = context.AbortSignal;
        var cancelled = signal?.IsCancellationRequested ?? false;
        using var registration = signal?.Register(() => cancelled = true);
        var attached = await Enqueue(async () =>
        {
            AssertHealthy();
            if (cancelled) throw CancellationError(signal!.Value);
            var loaded = await LoadDocumentAsync(definition, resolved.Id, resolved.Address, context)
                .ConfigureAwait(false);
            if (cancelled) throw CancellationError(signal!.Value);
            if (loaded is null) return null;
            IReadOnlyDictionary<string, object?>? attachedValue = null;
            var sink = ObserveDocument(definition, loaded, (value, detach, report) =>
            {
                attachedValue = value;
                return new GuardedDocumentObserver(observer, detach, report);
            });
            return new Observation<IReadOnlyDictionary<string, object?>?>(attachedValue, sink.Detach);
        }).ConfigureAwait(false);
        if (attached is null) return null;
        if (cancelled)
        {
            attached.Detach();
            throw CancellationError(signal!.Value);
        }

        return attached;
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
                    // 最后写：关闭时的写入之后、tracker 还在时。失败的 Session 什么都不写。
                    Exception? unstored = null;
                    if (_poison is null && _holds.Count > 0)
                    {
                        try
                        {
                            await StoreHoldsAsync([.. _holds.Values], Context.Background).ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            unstored = error;
                        }
                    }

                    _holdTimer?.Timer?.Dispose();
                    _holdTimer = null;
                    _commitListeners.Clear();
                    _observedListeners.Clear();
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
                    if (unstored is not null) throw unstored;
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

    /// <summary>
    /// 内部：观察**每一次**提交（含只在内存采纳的延迟提交）。Session 自己的组件用它，因为它们必须跟随内存；
    /// 抛出只上报、不毒化会话（各组件自己把错误折进状态）。对应 TS <c>observeCommits</c>。
    /// </summary>
    public IDisposable ObserveCommits(Action<ObservedCommit, Context> listener)
    {
        AssertUsable();
        _observedListeners.Add(listener);
        return new Unsubscription(() => _observedListeners.Remove(listener));
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
            // 持有变更要随缓存离开之前落盘，否则内容就没了。
            if (_poison is null && _holds.Count > 0)
            {
                await StoreHoldsAsync([.. _holds.Values], Context.Background).ConfigureAwait(false);
            }

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
        Func<Transaction, Task<TResult>> change, Context context, TransactionScope? scope, int? withinMs)
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
        if (_readOnly)
        {
            // 在到达 Storage 之前拒绝：Session 保持健康，调用方看到原因。
            tx.Discard();
            SweepDocuments();
            throw new ReadOnlySession("a commit with writes");
        }
        if (withinMs is { } within && tx.Holdable() is { } held)
        {
            // 只在内存采纳：watch 立刻看到，落盘等到下一次读/写该文档、离开内存、关闭或最迟 withinMs。
            HoldCommit(tx, held, within, context);
            SweepDocuments();
            return result;
        }

        // 落盘前的最后一步：持有变更离开队列之后，就只有 Storage 能失败了。
        var taken = _holds.Count == 0
            ? []
            : tx.TakeHeld(id => _holds.TryGetValue(id, out var pending) ? pending.Held : null);

        // Storage 在批次持久后同步采纳（早于它的下一次提交）；这里不能让异常穿到 Storage，
        // 所以回调自己留住错误，等提交解析后再决定如何失败。
        AdoptedDocuments? adoptedDocuments = null;
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
        if (taken.Count > 0) ReleaseHeld(taken);
        // 刚写过的文档算一次使用：它们不会被紧接着的驱逐带走。
        foreach (var document in adoptedDocuments!.Observed)
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

    /// <summary>提交记录的状态与应答条目（三种具体记录才有；抽象基类不带）。</summary>
    private static (SubmissionStatus Status, EntryId? Answer)? SubmissionState(SubmissionRecord record) => record switch
    {
        SubmissionRecord.InputRecord input => (input.Status, input.Answer),
        SubmissionRecord.WriteRecord write => (write.Status, (EntryId?)null),
        SubmissionRecord.ContinueRecord continued => (continued.Status, (EntryId?)null),
        _ => null,
    };

    private void Publish(
        Seq seq,
        IReadOnlyList<StorageWrite> writes,
        AdoptedDocuments documents,
        Context context)
    {
        // 提交里的条目与落定的提交各发一个事件，挂到上下文携带的 span 上（相同时刻没有 span 就不发）。
        var span = context.Value(Pi.Durable.Harness.DurableTelemetry.SpanKey);
        var changes = new List<CommitChange>();
        foreach (var write in writes)
        {
            switch (write)
            {
                case StorageWrite.Conversation conversation:
                    changes.Add(new CommitChange.ConversationTable(conversation.Value));
                    break;
                case StorageWrite.Entry entry:
                    span?.AddEvent("pi.durable.entry", new Pi.Telemetry.SpanAttributes
                    {
                        ["entry.id"] = (double)entry.Value.Id.Value,
                        ["entry.kind"] = entry.Value.Kind,
                    });
                    changes.Add(new CommitChange.EntryTable(entry.Value));
                    break;
                case StorageWrite.Task task:
                    changes.Add(new CommitChange.TaskTable(task.Value));
                    break;
                case StorageWrite.Submission submission:
                    if (SubmissionState(submission.Value) is { } state
                        && state.Status is SubmissionStatus.Done or SubmissionStatus.Unanswered)
                    {
                        var attributes = new Pi.Telemetry.SpanAttributes
                        {
                            ["submission.id"] = (double)submission.Value.Id.Value,
                            ["status"] = state.Status == SubmissionStatus.Done ? "done" : "unanswered",
                        };
                        if (state.Answer is { } answer) attributes["answer.entry_id"] = (double)answer.Value;
                        span?.AddEvent("pi.durable.submission", attributes);
                    }

                    changes.Add(new CommitChange.SubmissionTable(submission.Value));
                    break;
            }
        }

        var tables = changes.Count;
        changes.AddRange(ToCommitChanges(documents.Observed));
        // Session 自己的组件跟随内存：先看到这次提交，宿主监听器只看落盘的那份。
        PublishObserved(new ObservedCommit { Seq = seq, Changes = changes }, context);
        if (_commitListeners.Count == 0) return;
        // 这次同时落了持有变更时，宿主的文档变更要带上持有操作（持有在前）。
        var storedChanges = ReferenceEquals(documents.Stored, documents.Observed)
            ? changes
            : [.. changes.Take(tables), .. ToCommitChanges(documents.Stored)];
        var publication = new CommitPublication { Seq = seq, Changes = storedChanges };
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

    /// <summary>把这次提交展示给 Session 自己的观察者（内存或已落盘）。</summary>
    private void PublishObserved(ObservedCommit observed, Context context)
    {
        foreach (var listener in _observedListeners.ToArray())
        {
            try
            {
                listener(observed, context);
            }
            catch (Exception error)
            {
                // 视图/调度器各自把错误折进自己的状态，这里只上报不毒化会话。
                Report(error);
            }
        }
    }

    /// <summary>把文档变更包成宿主发布的形状。</summary>
    private static IEnumerable<CommitChange> ToCommitChanges(IEnumerable<DocumentCommitChange> documents)
        => documents.Select(document => document switch
        {
            DocumentCommitChange.Document changed => (CommitChange)new CommitChange.DocumentChanged(changed),
            DocumentCommitChange.Copy copy => new CommitChange.DocumentCopied(copy),
            _ => throw new ArgumentOutOfRangeException(nameof(documents)),
        });

    /// <summary>内部：内存采纳一次被持有的提交；按最早截止时刻钉住并排队。</summary>
    private void HoldCommit(
        Transaction tx, List<(LoadedDocument Loaded, IReadOnlyList<DeltaOp> Ops, bool Base)> held,
        int withinMs, Context context)
    {
        var documents = tx.AdoptHeld();
        var deadline = _clock() + withinMs;
        foreach (var (loaded, ops, isBase) in held)
        {
            var id = loaded.Record.Id;
            if (!_holds.TryGetValue(id, out var hold))
            {
                var unpin = PinDocument(loaded.AddressId, id);
                hold = new Hold(new HeldDocument { Loaded = loaded, Pending = new PendingOps() }, deadline, unpin);
                _holds[id] = hold;
            }

            hold.Held.Pending.Push(ops);
            if (isBase) hold.Held.Base = true;
            if (deadline < hold.Deadline) hold.Deadline = deadline;
        }

        ArmHoldTimer();
        PublishObserved(new ObservedCommit { Changes = ToCommitChanges(documents).ToList() }, context);
    }

    /// <summary>内部：把持有变更在一次 Storage 事务里落盘（线上），只发给宿主监听器。</summary>
    private async Task StoreHoldsAsync(IReadOnlyList<Hold> holds, Context context)
    {
        if (holds.Count == 0) return;
        var writes = new List<StorageWrite>(holds.Count);
        var changes = new List<DocumentCommitChange>(holds.Count);
        foreach (var hold in holds)
        {
            var loaded = hold.Held.Loaded;
            var value = loaded.Tracker.Value;
            var stored = Transaction.StoreHeld(hold.Held, [], value, loaded.ValueVersion);
            writes.Add(new StorageWrite.DocumentChange(loaded.Record.Id, stored.Content));
            var conversationId = loaded.Record.Scope is DocumentScope.ConversationScope scope
                ? scope.ConversationId
                : (ConversationId?)null;
            changes.Add(new DocumentCommitChange.Document(
                loaded.Record, conversationId, loaded.ValueVersion, value, stored.Ops));
        }

        var adopted = false;
        void Adopt(Seq _)
        {
            adopted = true;
            for (var index = 0; index < holds.Count; index++)
            {
                if (((StorageWrite.DocumentChange)writes[index]).Content is DocumentContent.Base)
                    holds[index].Held.Loaded.DeltasSinceBase = 0;
                else
                    holds[index].Held.Loaded.DeltasSinceBase++;
            }
        }

        var seq = await _storage.CommitAsync(
            writes, Adopt, ContextSignals.WithoutAbortSignal(context).AbortSignal ?? default).ConfigureAwait(false);
        if (!adopted)
        {
            var error = new InvalidOperationException("Storage committed without adopting the batch");
            _poison = error;
            Report(error, new Harness.ReportInfo.SessionFailed());
            throw error;
        }

        Release(holds);
        var publication = new CommitPublication { Seq = seq, Changes = ToCommitChanges(changes).ToList() };
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

    /// <summary>落盘之后：忘掉这些持有、解除钉住，并把定时器移到下一个截止时刻。</summary>
    private void Release(IReadOnlyList<Hold> holds)
    {
        foreach (var hold in holds)
        {
            _holds.Remove(hold.Held.Loaded.Record.Id);
            hold.Unpin();
        }

        ArmHoldTimer();
    }

    /// <summary>落盘之后（按 <see cref="HeldDocument"/> 标识）：忘掉持有、解除钉住，并重排定时器。</summary>
    private void ReleaseHeld(IReadOnlyList<HeldDocument> taken)
    {
        foreach (var held in taken)
        {
            if (_holds.Remove(held.Loaded.Record.Id, out var hold)) hold.Unpin();
        }

        ArmHoldTimer();
    }

    /// <summary>最早的截止时刻武装一个后台定时器；到点把持有的文档一次全部落盘。</summary>
    private void ArmHoldTimer()
    {
        if (_closing is not null) return;
        var at = double.PositiveInfinity;
        foreach (var hold in _holds.Values)
        {
            if (hold.Deadline < at) at = hold.Deadline;
        }

        var armed = _holdTimer;
        if (armed is not null && (armed.Fired || armed.At == at)) return;
        armed?.Timer?.Dispose();
        _holdTimer = null;
        if (double.IsPositiveInfinity(at)) return;
        var timer = new HoldTimer(at);
        _holdTimer = timer;
        void Run()
        {
            timer.Fired = true;
            _ = Enqueue<object?>(async () =>
            {
                if (ReferenceEquals(_holdTimer, timer)) _holdTimer = null;
                if (_closing is not null || _poison is not null) return null;
                await StoreHoldsAsync([.. _holds.Values], Context.Background).ConfigureAwait(false);
                return null;
            }).ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        }

        var delay = at - _clock();
        if (delay <= 0)
        {
            Run();
            return;
        }

        timer.Timer = new System.Threading.Timer(
            _ => Run(), null, TimeSpan.FromMilliseconds(delay), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 把一个 watch 附着到一个已提交化身：校验定义，然后转发本化身的已提交变更、它的退休与关闭。
    /// watch 的 detach 移除两个订阅。对应 TS <c>#attachDocument</c>。
    /// </summary>
    /// <summary>
    /// 跟随化身 <paramref name="loaded"/> 的地址：<paramref name="create"/> 从它的值建出观察者，之后每次提交变更都
    /// 投给它——退休作为带 <c>retired</c> 标记、ops 为空、携带最后值的变更（退休那次提交里的变更不发布），同一地址
    /// 上的新化身作为根替换。当提交把任务文档的 owner 任务做成终态时，此后没有提交能再改该地址：投完这一批变更后
    /// 退订并解除 pin，观察者以「已结束」收尾。对应 TS <c>#observeDocument</c>（<c>8b0caefc2</c>）。
    /// </summary>
    /// <remarks>
    /// 闭包只保留地址与当前化身 ID，不持有 <see cref="LoadedDocument"/> 的 tracker：退休之后那个化身可以被释放。
    /// </remarks>
    private TObserver ObserveDocument<TObserver>(
        DurableDocuments.AnyDocDefinition definition,
        LoadedDocument loaded,
        Func<IReadOnlyDictionary<string, object?>?, Action, Action<Exception>, TObserver> create)
        where TObserver : class, IObservationSink
    {
        DurableDocuments.CheckRecordScope(definition, loaded.Record);
        DurableDocuments.CheckRecordVersion(definition, loaded.Record, loaded.StoredVersion);
        var addressId = loaded.AddressId;
        var address = loaded.Record;
        DocumentId? incarnation = loaded.Record.Id;
        var value = loaded.Tracker.Value;
        var version = loaded.ValueVersion;
        IDisposable unsubscribeCommit = new Unsubscription(() => { });
        IDisposable unsubscribeClose = new Unsubscription(() => { });
        // 被跟随的化身留在缓存里；它退休或观察者摘下之后，重新成为空闲。
        var unpin = PinDocument(addressId, loaded.Record.Id);
        var attached = true;
        Action detach = () =>
        {
            if (!attached) return;
            attached = false;
            unsubscribeCommit.Dispose();
            unsubscribeClose.Dispose();
            unpin();
        };
        TObserver observer;
        try
        {
            observer = create(value, detach, error => Report(error));
        }
        catch (Exception)
        {
            unpin();
            throw;
        }

        unsubscribeCommit = SubscribeCommits((publication, context) =>
        {
            // 监听器从集合的副本上跑：另一个观察者的调用可能已经把它摘下。
            if (!attached) return;
            var ownerEnds = false;
            foreach (var change in publication.Changes)
            {
                if (change is CommitChange.TaskTable task)
                {
                    if (address.Scope is DocumentScope.TaskScope scope
                        && task.Value.Id == scope.TaskId
                        && task.Value.State.Status == Pi.Durable.Types.TaskStatus.Terminal)
                    {
                        ownerEnds = true;
                    }

                    continue;
                }

                if (change is not CommitChange.DocumentChanged document) continue;
                var record = document.Change.Record;
                // 被跟随的化身按定义就在该地址上；只有另一个记录才需要比较地址。
                if (record.Id != incarnation && !AtAddress(record, address)) continue;
                if (document.Change.Value is null)
                {
                    // 已退休的化身不再变化：投递一次带 retired 标记的最后值，watch 不因此结束。
                    if (record.Id != incarnation) continue;
                    var last = value!;
                    incarnation = null;
                    value = null;
                    unpin();
                    unpin = static () => { };
                    observer.Advance(last, [], retired: true, context);
                }
                else if (record.Id != incarnation || document.Change.Version != version)
                {
                    // 该地址上的新化身，或另一定义版本的值（形状不同）：整体展示。
                    if (record.Id != incarnation)
                    {
                        incarnation = record.Id;
                        unpin();
                        unpin = PinDocument(addressId, record.Id);
                    }

                    version = document.Change.Version!.Value;
                    value = document.Change.Value;
                    observer.Advance(value, [new DeltaOp.Replace(value)], retired: false, context);
                }
                else
                {
                    // 仅迁移的 base 对新版本的观察者没有任何改变。
                    if (document.Change.Ops.Count == 0) continue;
                    value = document.Change.Value;
                    observer.Advance(value, document.Change.Ops, retired: false, context);
                }

                // 观察者可能在这次调用里摘下自己，也可能抛错而被摘下：这次提交余下的内容不属于它。
                if (!attached) return;
            }

            if (!ownerEnds) return;
            unsubscribeCommit.Dispose();
            unpin();
            observer.EndAfterDelivery();
        });
        unsubscribeClose = SubscribeClose(() => observer.EndWith(_poison));
        return observer;
    }

    /// <summary>记录是否是该地址上的一个化身。对应 TS <c>atAddress</c>。</summary>
    private static bool AtAddress(DocumentRecord record, DocumentRecord address) =>
        record.Kind == address.Kind
        && record.Key == address.Key
        && record.Scope switch
        {
            DocumentScope.SessionScope => address.Scope is DocumentScope.SessionScope,
            DocumentScope.ConversationScope scope =>
                address.Scope is DocumentScope.ConversationScope other && other.ConversationId == scope.ConversationId,
            DocumentScope.TaskScope scope =>
                address.Scope is DocumentScope.TaskScope other && other.TaskId == scope.TaskId,
            _ => false,
        };


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
        if (cached is not null)
        {
            // 重载必须读到内存里的内容：持有变更在 tracker 走之前落盘。
            if (_holds.TryGetValue(cached.Record.Id, out var hold))
            {
                await StoreHoldsAsync([hold], context).ConfigureAwait(false);
            }

            DropDocument(addressId);
        }

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

    /// <summary>
    /// <paramref name="tx"/> 是否是这个 Session 的某次提交的事务。品牌检查：被包装过的、或别的 Session 的并不是。
    /// 对应 TS <c>ownsTransaction()</c>。
    /// </summary>
    internal bool OwnsTransaction(Transaction tx) => ReferenceEquals(tx.Host, _host);

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
