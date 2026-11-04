using System.Runtime.CompilerServices;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Services;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using Path = Pi.Chord.Delta.Path;

/// <summary>
/// 一个对话的活动转录与内建文档的结构挂载（spec §9.3）。对应 TS <c>ConversationView</c>。
/// C# 的 <c>entries</c> 是类型化 <see cref="EntryRecord"/>（TS 的发布 ops 仍原样投递给观察者；
/// C# 的 <see cref="DeltaApply"/> 无法对含 C# 记录的数组执行 splice，观察者应以投递值为准，
/// 见差异记录）。
/// </summary>
public sealed record ConversationView
{
    public required ConversationRecord Conversation { get; init; }

    /// <summary>
    /// 转录的一个窗口：该对话的分叉感知历史（<c>Conversation.EntriesAsync</c>），按条目 ID 序。压缩摘要这类 head
    /// 标记落在它被写入的位置，其 <c>Head</c> 指出它摘要的条目结束于何处。
    /// </summary>
    public required IReadOnlyList<EntryRecord> Entries { get; init; }

    /// <summary>窗口之前那个条目的 ID；窗口从转录开头起时缺省。对应 TS <c>ConversationView.before</c>。</summary>
    public EntryId? Before { get; init; }

    /// <summary>按 kind 的内建对话文档；缺失的文档缺失。</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Docs { get; init; }
}

/// <summary>
/// 一个视图的窗口持有哪些条目（spec §9.3）。窗口持有 <see cref="Min"/> 到 <see cref="Max"/> 个条目：从最新的
/// <see cref="Min"/> 个开始，接收追加的条目，一旦超过 <see cref="Max"/> 就一次 splice 丢掉最旧的、降到
/// <see cref="Min"/>。带 <see cref="From"/> 时，若它之后至多 <see cref="Max"/> 个条目，则改从它开始。
/// 对应 TS <c>ConversationWindow</c>。
/// </summary>
public sealed record ConversationWindow
{
    /// <summary>
    /// 第一个值的窗口从它开始（含），例如客户端已有的最新条目之后（<c>ConversationTranscript.Resume</c>）。
    /// 它只作用于第一个值：挂载之后提交的每个条目都会被追加，无论其 ID。越过最新条目的 <c>From</c> 让窗口以空
    /// 开始，并把 <c>Before</c> 设为那个最新条目。
    /// </summary>
    public EntryId? From { get; init; }

    /// <summary>缺省 50；整数，至少 1。</summary>
    public int? Min { get; init; }

    /// <summary>缺省 <c>2 * Min</c>；整数，至少 <see cref="Min"/>。</summary>
    public int? Max { get; init; }
}

/// <summary>窗口及其缺省值；边界不合法时抛 <see cref="ArgumentOutOfRangeException"/>。对应 TS <c>resolveWindow()</c>。</summary>
internal readonly record struct WindowBounds(EntryId? From, int Min, int Max)
{
    internal const int DefaultMin = 50;

    public static WindowBounds Resolve(ConversationWindow? window)
    {
        var min = window?.Min ?? DefaultMin;
        if (min < 1) throw new ArgumentOutOfRangeException(nameof(window), $"Window min must be an integer >= 1, got {min}");
        var max = window?.Max ?? 2 * min;
        if (max < min)
        {
            throw new ArgumentOutOfRangeException(
                nameof(window), $"Window max must be an integer >= min ({min}), got {max}");
        }

        return new WindowBounds(window?.From, min, max);
    }
}

/// <summary>接收挂载的每个下一修订与 Session 的关闭。对应 TS <c>ViewObserver</c>。</summary>
public abstract class ViewObserver
{
    public virtual void Advance(ConversationView value, IReadOnlyList<DeltaOp> ops, Context context)
    {
    }

    /// <summary>Session 已关闭；<paramref name="failure"/> 给出使其失败的错误（若失败）。</summary>
    public abstract void End(Exception? failure);
}

/// <summary>
/// 一个 Harness 的对话视图挂载：每个对话至多一个，由其第一个观察者在 Session 线上构建，最后一个离开时丢弃；
/// 文档共享，条目按观察者各自的窗口推进。对应 TS <c>view.ts</c> 全量。
/// </summary>
public sealed class ConversationViews
{
    /// <summary>初始窗口读取时每页的条目数。对应 TS <c>SCAN_PAGE_SIZE</c>。</summary>
    private const int ScanPageSize = 256;

    private static readonly ConditionalWeakTable<DurableSession, ConversationViews> ViewsTable = new();

    /// <summary>一个 Harness 的视图挂载，供建于其上的适配器使用。对应 TS <c>conversationViews()</c>。</summary>
    public static ConversationViews For(DurableSession harness)
        => ViewsTable.TryGetValue(harness, out var views)
            ? views
            : throw new InvalidOperationException("Not a Harness");

    /// <summary>内建挂载文档（无键控的对话文档令牌）。对应 TS <c>MOUNTED</c>。</summary>
    private static readonly IReadOnlyList<DocToken<Dictionary<string, object?>>> Mounted =
    [
        AgentDocs.AgentDoc,
        Live.LiveDoc,
        Inbox.InboxDoc,
        Provider.ProviderDoc,
        Usage.UsageDoc,
    ];

    private static readonly IReadOnlySet<string> MountedKinds =
        new HashSet<string>(Mounted.Select(token => token.Definition.Kind), StringComparer.Ordinal);

    private static readonly Path EntriesPath = new([Seg.Key("entries")]);
    private static readonly Path BeforePath = new([Seg.Key("before")]);
    private static readonly Path DocsPath = new([Seg.Key("docs")]);

    private readonly DurableSession _session;
    private readonly IStorage _storage;
    private readonly object _gate = new();
    private readonly Dictionary<ConversationId, Mount> _mounts = [];
    private bool _closed;

    /// <summary>
    /// 一个对话的挂载：它展示的文档（所有观察者共享）、文档化身，以及每个观察者各自的窗口。
    /// 对应 TS <c>Mount</c>。
    /// </summary>
    private sealed class Mount
    {
        public ConversationRecord Conversation = null!;

        /// <summary>按 kind 的文档值（累积后的最新值）。</summary>
        public Dictionary<string, IReadOnlyDictionary<string, object?>> Docs = new(StringComparer.Ordinal);

        /// <summary>按 kind 的挂载化身与定义版本；另一个化身或版本整体设置。</summary>
        public readonly Dictionary<string, MountedDoc> Incarnations = new(StringComparer.Ordinal);

        public readonly HashSet<Windowed> Observers = [];
    }

    private sealed record MountedDoc(DocumentId Id, int Version);

    /// <summary>挂载的一个观察者及其窗口：边界与它最后一次拿到的修订。对应 TS <c>Windowed</c>。</summary>
    private sealed class Windowed(int min, int max, object observer)
    {
        public int Min { get; } = min;

        public int Max { get; } = max;

        public object Observer { get; } = observer;

        public ConversationView Value { get; set; } = null!;
    }

    public ConversationViews(DurableSession session, IStorage storage)
    {
        _session = session;
        _storage = storage;
        ViewsTable.Remove(session);
        ViewsTable.Add(session, this);
        _session.SubscribeCommits((publication, context) =>
        {
            lock (_gate)
            {
                foreach (var (id, mount) in _mounts) Advance(id, mount, publication, context);
            }
        });
        _session.SubscribeClose(() =>
        {
            List<Windowed[]> observers;
            lock (_gate)
            {
                _closed = true;
                observers = [.. _mounts.Values.Select(mount => mount.Observers.ToArray())];
                _mounts.Clear();
            }

            foreach (var group in observers)
            {
                foreach (var windowed in group) CloseObserver(windowed.Observer, _session.Failure);
            }
        });
    }

    /// <summary>
    /// 视图的序列化精确帧 watch；<paramref name="window"/> 决定它持有哪个窗口。<paramref name="context"/> 取消时停止。
    /// 对应 TS <c>watch()</c>。
    /// </summary>
    public async Task<CommittedWatch<ConversationView>> WatchAsync(
        ConversationId id, Context context, ConversationWindow? window = null)
    {
        var (observer, _) = await AttachAsync(
            id,
            (value, release) => new CommittedWatch<ConversationView>(
                value, release, error => _session.Report(error)),
            context,
            window).ConfigureAwait(false);
        var signal = context.AbortSignal;
        if (signal?.IsCancellationRequested == true)
        {
            observer.Abort();
            throw new OperationCanceledException(signal.GetValueOrDefault());
        }

        if (signal is { } watchSignal) observer.ObserveAbort(watchSignal);
        return observer;
    }

    /// <summary>
    /// 在 Session 线上原子地附着视图观察者：它拿到附着之后的每一帧（不折叠），Session 关闭时以「session closed」
    /// 的失败收尾，好跟上下一个 Session。返回附着那一刻的值与幂等的摘下动作。对应 TS <c>views.observe()</c>
    /// （<c>8b0caefc2</c>）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="WatchAsync"/> 的区别只在缓冲：watch 把一次忙碌周期内的帧折叠成一次调用，观察者拿每一帧。
    /// </remarks>
    public async Task<Observation<ConversationView>> ObserveAsync(
        ConversationId id,
        ISessionObserver<ConversationView> observer,
        Context context,
        ConversationWindow? window = null)
    {
        ConversationView? value = null;
        var (guarded, detach) = await AttachAsync(
            id,
            (current, release) =>
            {
                value = current;
                return new GuardedObserver<ConversationView>(observer, release, error => _session.Report(error));
            },
            context,
            window).ConfigureAwait(false);
        try
        {
            context.AbortSignal?.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            guarded.Detach();
            throw;
        }

        return new Observation<ConversationView>(value!, detach);
    }

    /// <summary>
    /// 注册一个观察者，原子地在 Session 线上：它看到之后的每次发布而非更早的。<paramref name="create"/> 可读已提交
    /// Storage（仍在线上）；<c>release</c> 丢弃它——以及挂载与其最后一个观察者。对应 TS <c>attach()</c>。
    /// </summary>
    public Task<(T Observer, Action Detach)> AttachAsync<T>(
        ConversationId id, Func<ConversationView, Action, T> create, Context context,
        ConversationWindow? window = null)
        where T : notnull
        => AttachCoreAsync(id, async (value, release) => create(value, release), context, window);

    /// <summary>异步创建回调的重载（回调仍运行在 Session 线上，可读已提交 Storage）。</summary>
    public Task<(T Observer, Action Detach)> AttachAsync<T>(
        ConversationId id, Func<ConversationView, Action, Task<T>> create, Context context,
        ConversationWindow? window = null)
        where T : notnull
        => AttachCoreAsync(id, create, context, window);

    private Task<(T Observer, Action Detach)> AttachCoreAsync<T>(
        ConversationId id, Func<ConversationView, Action, Task<T>> create, Context context,
        ConversationWindow? window = null)
        where T : notnull
        => _session.ReadOnLineAsync(async () =>
        {
            Mount? mount;
            lock (_gate) _mounts.TryGetValue(id, out mount);
            if (mount is null)
            {
                var built = await BuildAsync(id, context).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!_mounts.TryGetValue(id, out mount)) _mounts[id] = mount = built;
                }
            }

            var (observer, detach, windowed) = await AttachToAsync(mount, create, window, context)
                .ConfigureAwait(false);
            // 挂载水合期间 close 或取消可能已开始；此后不再注册。
            lock (_gate)
            {
                if (_closed) throw HarnessUtil.ClosedError();
            }

            context.AbortSignal?.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _mounts[id] = mount;
                mount.Observers.Add(windowed);
            }

            return (observer, detach);
        });

    private async Task<(T Observer, Action Detach, Windowed Windowed)> AttachToAsync<T>(
        Mount mount, Func<ConversationView, Action, Task<T>> create, ConversationWindow? window, Context context)
        where T : notnull
    {
        var bounds = WindowBounds.Resolve(window);
        var (entries, before) = await LoadWindowAsync(mount.Conversation.Id, bounds, context).ConfigureAwait(false);
        var value = new ConversationView
        {
            Conversation = mount.Conversation,
            Entries = entries,
            Before = before,
            Docs = SnapshotDocs(mount.Docs),
        };
        Windowed? windowed = null;
        T? observer = default;
        Action detach = () =>
        {
            lock (mount.Observers)
            {
                if (windowed is not null) mount.Observers.Remove(windowed);
                if (mount.Observers.Count == 0) RemoveIfCurrent(mount);
            }
        };
        observer = await create(value, detach).ConfigureAwait(false);
        windowed = new Windowed(bounds.Min, bounds.Max, observer!) { Value = value };
        return (observer, detach, windowed);
    }

    private void RemoveIfCurrent(Mount mount)
    {
        lock (_gate)
        {
            foreach (var (id, current) in _mounts)
            {
                if (!ReferenceEquals(current, mount)) continue;
                _mounts.Remove(id);
                return;
            }
        }
    }

    /// <summary>读对话及其内建文档；条目由每个观察者自己的初始窗口读。对应 TS <c>build()</c>。</summary>
    private async Task<Mount> BuildAsync(ConversationId id, Context context)
    {
        var conversation = await _storage.GetConversationAsync(id).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Conversation {id.Value} does not exist");
        var mount = new Mount { Conversation = conversation };
        foreach (var token in Mounted)
        {
            var loaded = await _session.ConversationDocumentOnLineAsync(token, id, context).ConfigureAwait(false);
            if (loaded?.Value is not { } value) continue;
            var kind = token.Definition.Kind;
            mount.Docs[kind] = new Dictionary<string, object?>(value);
            mount.Incarnations[kind] = new MountedDoc(loaded.Record.Id, loaded.Version);
        }

        return mount;
    }

    /// <summary>
    /// 一个新观察者的窗口，在 Session 线上用有界扫描读出：至多 <c>max</c> 个条目在 <c>from</c> 之后时取它们，
    /// 否则取最新的 <c>min</c> 个；并给出窗口之前那个条目。对应 TS <c>loadWindow()</c>。
    /// </summary>
    private async Task<(IReadOnlyList<EntryRecord> Entries, EntryId? Before)> LoadWindowAsync(
        ConversationId id, WindowBounds bounds, Context context)
    {
        if (bounds.From is { } from)
        {
            var since = await ScanUpToAsync(
                new EntryQuery
                {
                    ConversationId = id, MinEntryId = from, Order = ScanOrder.Ascending,
                },
                SaturatingAdd(bounds.Max, 1)).ConfigureAwait(false);
            if (since.Count <= bounds.Max)
            {
                var previous = (await _storage.ScanEntriesAsync(
                    new EntryQuery { ConversationId = id, MaxEntryId = EntryId.From(from.Value - 1) }, 1)
                    .ConfigureAwait(false)).Items.FirstOrDefault();
                return (since, previous?.Id);
            }
        }

        var newest = await ScanUpToAsync(
            new EntryQuery { ConversationId = id }, SaturatingAdd(bounds.Min, 1)).ConfigureAwait(false);
        EntryId? before = null;
        if (newest.Count > bounds.Min)
        {
            before = newest[^1].Id;
            newest.RemoveAt(newest.Count - 1);
        }

        newest.Reverse();
        return (newest, before);
    }

    /// <summary>窗口的 <c>+1</c> 探测在 <see cref="int.MaxValue"/>（不受裁剪的窗口）处饱和，不溢出成负数。</summary>
    private static int SaturatingAdd(int value, int addend)
        => value > int.MaxValue - addend ? int.MaxValue : value + addend;

    /// <summary>按 <paramref name="query"/> 取至多 <paramref name="limit"/> 个条目，跟随短页的游标。</summary>
    private async Task<List<EntryRecord>> ScanUpToAsync(EntryQuery query, int limit)
    {
        var entries = new List<EntryRecord>();
        IReadOnlyDictionary<string, object?>? cursor = null;
        do
        {
            var page = await _storage.ScanEntriesAsync(
                query, Math.Min(ScanPageSize, limit - entries.Count), cursor).ConfigureAwait(false);
            entries.AddRange(page.Items);
            cursor = page.Next;
        }
        while (cursor is not null && entries.Count < limit);

        return entries;
    }

    /// <summary>
    /// 从一次发布派生文档操作与追加的条目，把每个观察者的窗口推进一版。文档只应用一次、共享；每个窗口接收追加的
    /// 条目，超过其 <c>max</c> 就一次丢掉最旧的、降到 <c>min</c>。对应 TS <c>advance()</c>。
    /// </summary>
    private static void Advance(ConversationId id, Mount mount, CommitPublication publication, Context context)
    {
        var docOps = new List<DeltaOp>();   // 观察者视角的操作（以 docs 为根）
        var relOps = new List<DeltaOp>();   // 以 docs 值为根的应用操作
        var appended = new List<EntryRecord>();
        var docsChanged = false;

        // 条目写入按 ID 序发布；head 标记也只是一个按序追加的条目（视图看的是完整历史）。
        foreach (var change in publication.Changes)
        {
            if (change is CommitChange.EntryTable entryChange && entryChange.Value.ConversationId == id)
            {
                appended.Add(entryChange.Value);
                continue;
            }

            if (change is not CommitChange.DocumentChanged documentChange) continue;
            if (documentChange.Change is not DocumentCommitChange.Document document) continue;
            var kind = document.Record.Kind;
            if (!MountedKinds.Contains(kind) || document.Record.Key is not null) continue;
            if (document.ConversationId is not { } conversationId || conversationId != id) continue;

            var prefix = DocsPath.Append(Seg.Key(kind));
            var relativePrefix = new Path([Seg.Key(kind)]);
            var mounted = mount.Incarnations.TryGetValue(kind, out var found) ? found : null;
            if (document.Value is null)
            {
                if (mounted is null || mounted.Id != document.Record.Id) continue;
                mount.Incarnations.Remove(kind);
                mount.Docs.Remove(kind);
                docsChanged = true;
                docOps.Add(new DeltaOp.Delete(prefix));
                relOps.Add(new DeltaOp.Delete(relativePrefix));
                continue;
            }

            if (mounted is not null && mounted.Id == document.Record.Id && mounted.Version == document.Version)
            {
                docsChanged = true;
                foreach (var op in document.Ops)
                {
                    docOps.Add(Prefixed(op, prefix));
                    relOps.Add(Prefixed(op, relativePrefix));
                }
            }
            else
            {
                mount.Incarnations[kind] = new MountedDoc(document.Record.Id, document.Version ?? 0);
                mount.Docs[kind] = (IReadOnlyDictionary<string, object?>)document.Value!;
                docsChanged = true;
                docOps.Add(new DeltaOp.Set(prefix, document.Value));
                relOps.Add(new DeltaOp.Set(relativePrefix, document.Value));
            }
        }

        if (docOps.Count == 0 && appended.Count == 0) return;
        if (docsChanged)
        {
            // 应用到视图自己的累积文档值上（而非化身记录的旧快照），否则同一文档更早提交写入的键会丢。
            var docsRoot = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, docValue) in mount.Docs) docsRoot[name] = docValue;
            var applied = (Dictionary<string, object?>)DeltaApply.Apply(relOps, docsRoot)!;
            var docs = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
            foreach (var (name, docValue) in applied) docs[name] = (IReadOnlyDictionary<string, object?>)docValue!;
            mount.Docs = docs;
        }

        var frameContext = ContextSignals.WithoutAbortSignal(context);
        Windowed[] observers;
        lock (mount.Observers) observers = [.. mount.Observers];
        foreach (var windowed in observers)
        {
            var previous = windowed.Value;
            var entries = previous.Entries;
            var before = previous.Before;
            var ops = docOps;
            if (appended.Count > 0)
            {
                ops = [.. docOps, new DeltaOp.Splice(EntriesPath, entries.Count, 0, appended)];
                var grown = new List<EntryRecord>(entries);
                grown.AddRange(appended);
                if (grown.Count > windowed.Max)
                {
                    var drop = grown.Count - windowed.Min;
                    before = grown[drop - 1].Id;
                    grown.RemoveRange(0, drop);
                    ops.Add(new DeltaOp.Splice(EntriesPath, 0, drop, []));
                    ops.Add(new DeltaOp.Set(BeforePath, before.Value));
                }

                entries = grown;
            }

            windowed.Value = new ConversationView
            {
                Conversation = previous.Conversation,
                Entries = entries,
                Before = before,
                Docs = SnapshotDocs(mount.Docs),
            };
            DispatchAdvance(windowed.Observer, windowed.Value, ops, frameContext);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> SnapshotDocs(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> docs)
        => new Dictionary<string, IReadOnlyDictionary<string, object?>>(docs, StringComparer.Ordinal);

    /// <summary><c>op</c> 移到 <c>prefix</c> 之下；根替换变成对前缀的设置。对应 TS <c>prefixed()</c>。</summary>
    private static DeltaOp Prefixed(DeltaOp op, Path prefix)
    {
        Path At(Path path) => new([.. prefix.Segments, .. path.Segments]);
        return op switch
        {
            DeltaOp.Replace replace => new DeltaOp.Set(prefix, replace.Value),
            DeltaOp.Splice splice => new DeltaOp.Splice(At(splice.Path), splice.Index, splice.Remove, splice.Items),
            DeltaOp.Move move => new DeltaOp.Move(At(move.Path), move.Permutation),
            DeltaOp.Set set => new DeltaOp.Set(At(set.Path), set.Value),
            DeltaOp.Delete delete => new DeltaOp.Delete(At(delete.Path)),
            DeltaOp.Append append => new DeltaOp.Append(At(append.Path), append.Text),
            DeltaOp.Truncate truncate => new DeltaOp.Truncate(At(truncate.Path), truncate.Length),
            _ => throw new InvalidOperationException("unknown op"),
        };
    }

    private static void DispatchAdvance(
        object observer, ConversationView value, IReadOnlyList<DeltaOp> ops, Context context)
    {
        switch (observer)
        {
            case CommittedWatch<ConversationView> watch:
                watch.Advance(value, ops, context);
                break;
            case ViewObserver viewObserver:
                viewObserver.Advance(value, ops, context);
                break;
            case ISessionObserver<ConversationView> sessionObserver:
                sessionObserver.Advance(new WatchCall<ConversationView>.Value(value, ops), context);
                break;
        }
    }

    private static void CloseObserver(object observer, Exception? failure)
    {
        switch (observer)
        {
            case CommittedWatch<ConversationView> watch:
                watch.EndWith(failure);
                break;
            case ViewObserver viewObserver:
                viewObserver.End(failure);
                break;
            case ISessionObserver<ConversationView> sessionObserver:
                // 同步观察者关闭时收到 failed，好让它跟上下一个 Session（TS closeEnds）。
                sessionObserver.End(new WatchEnd.Failed(
                    failure ?? new InvalidOperationException("session closed")));
                break;
        }
    }
}
