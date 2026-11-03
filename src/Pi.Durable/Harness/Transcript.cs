using Pi.Chord.Context;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

/// <summary>客户端转录的一行。对应 TS <c>TranscriptItem</c>。</summary>
public abstract record TranscriptItem
{
    private TranscriptItem()
    {
    }

    /// <summary>一个条目。</summary>
    public sealed record Entry(EntryRecord Value) : TranscriptItem;

    /// <summary>
    /// <see cref="After"/> 与 <see cref="UpTo"/> 之间的条目缺失：渲染「加载更多」并调用
    /// <see cref="ConversationTranscript.FillGapAsync"/>。
    /// </summary>
    public sealed record Gap(EntryId After, EntryId UpTo) : TranscriptItem;

    /// <summary>
    /// 转录上面还有内容：渲染「加载更早」并调用 <see cref="ConversationTranscript.LoadEarlierAsync"/>。
    /// 永远是第一个条目。
    /// </summary>
    public sealed record Earlier : TranscriptItem;
}

/// <summary>
/// 渲染器看到的对话视图：用 <see cref="Items"/>（客户端的整份转录）代替窗口。对应 TS <c>TranscriptView</c>。
/// </summary>
public sealed record TranscriptView
{
    public required ConversationRecord Conversation { get; init; }

    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Docs { get; init; }

    public required IReadOnlyList<TranscriptItem> Items { get; init; }
}

/// <summary>
/// 按页读一个对话的条目，与不带游标的 <c>Conversation.EntriesAsync</c> 同形。对应 TS <c>EntryLoader</c>。
/// </summary>
public delegate Task<Page<EntryRecord>> EntryLoader(EntryRange query, int limit, Context context);

/// <summary>
/// 一个客户端持有某个对话的内容：它从视图或抓取得到的每个条目，以及它们之间的缺口（spec §9.3）。它是任意
/// <see cref="ConversationView"/> 之上的一层：把视图的每个值交给 <see cref="Merge"/>，它返回以客户端整份转录
/// 作为 <see cref="TranscriptView.Items"/> 的视图。条目一旦提交就不再变，所以转录只增长；窗口丢掉的最旧条目
/// 仍留在这里。
///
/// 一个 <see cref="ConversationView.Before"/> 越过客户端已有的最新条目的窗口（停摆、溢出或重连之后）并不续上它：
/// 中间那些条目成为一个缺口。没有任何地方数条目或从条目 ID 算范围（ID 有洞）；只有排他边界被换成
/// <see cref="EntryRange"/> 的包含边界。被丢弃之后，用 <see cref="Resume"/> 作为窗口的新 watch 接着续转录。
///
/// 一次合并是 O(items)，从不重排或重抓；什么都没添加的合并、<see cref="LoadEarlierAsync"/> 或
/// <see cref="FillGapAsync"/> 返回上一份 items。并发的 <see cref="LoadEarlierAsync"/>、或针对同一缺口并发调用的
/// <see cref="FillGapAsync"/> 共享一次加载，因此一直可见的占位符不会抓两次。对应 TS <c>ConversationTranscript</c>。
/// </summary>
public sealed class ConversationTranscript
{
    /// <summary>ID 的负无穷哨兵：条目 ID 都是正数。</summary>
    private const long NegativeInfinity = long.MinValue;

    private readonly EntryLoader _load;
    private readonly int _pageSize;

    /// <summary>持有的每个条目，按 ID 序。</summary>
    private List<TranscriptItem.Entry> _entries = [];

    private readonly HashSet<long> _ids = [];

    /// <summary>按 ID 序的缺口；每个都是不可变条目，缩小时被替换。</summary>
    private List<TranscriptItem.Gap> _gaps = [];

    /// <summary>是否合并过任何东西。</summary>
    private bool _started;

    /// <summary>已知范围的排他下界：等于或低于它的条目没有加载；开头处为 <see cref="NegativeInfinity"/>。</summary>
    private long _low = NegativeInfinity;

    /// <summary>已知范围的包含上界：最新条目，或更晚一个空窗口的 <c>Before</c>。</summary>
    private long _tail = NegativeInfinity;

    private IReadOnlyList<TranscriptItem> _items = [];
    private ConversationView? _last;
    private TranscriptView? _view;
    private Task<TranscriptView>? _earlier;
    private readonly Dictionary<string, Task<TranscriptView>> _filling = new(StringComparer.Ordinal);

    public ConversationTranscript(EntryLoader load, int pageSize = 50)
    {
        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"Page size must be an integer >= 1, got {pageSize}");
        }

        _load = load;
        _pageSize = pageSize;
    }

    /// <summary>
    /// 转录到达的 ID：它的最新条目，或那个让窗口为空的更晚窗口的 <c>Before</c>。
    /// 第一次合并之前与空转录为 null。
    /// </summary>
    public EntryId? Newest => _tail == NegativeInfinity ? null : EntryId.From(_tail);

    /// <summary>
    /// 被丢弃之后接着续这份转录的窗口：<c>{ From }</c> 紧跟 <see cref="Newest"/>，于是新 watch 只返回错过的东西
    /// （装得进窗口时）或它最新的条目加一个缺口；空转录为 <c>{}</c>；第一次合并前为 null。
    /// <c>conversation.WatchAsync(context, transcript.Resume)</c>。
    /// </summary>
    public ConversationWindow? Resume
    {
        get
        {
            if (!_started) return null;
            var newest = Newest;
            return newest is { } tail ? new ConversationWindow { From = EntryId.From(tail.Value + 1) } : new ConversationWindow();
        }
    }

    /// <summary>取用视图的一个值：它窗口里的条目加入转录。</summary>
    public TranscriptView Merge(ConversationView view)
    {
        _last = view;
        var entries = view.Entries;
        var hi = entries.Count > 0
            ? entries[^1].Id.Value
            : view.Before?.Value ?? NegativeInfinity;
        Cover(view.Before?.Value ?? NegativeInfinity, hi, entries);
        return Result();
    }

    /// <summary>加载持有的最旧条目之前那一页。没有更早的内容时返回当前视图。</summary>
    public Task<TranscriptView> LoadEarlierAsync(Context context)
    {
        if (_last is null)
        {
            return Task.FromException<TranscriptView>(
                new InvalidOperationException("ConversationTranscript: merge a view first"));
        }

        if (_low == NegativeInfinity) return Task.FromResult(Result());
        if (_earlier is { } running) return running;
        var low = _low;
        var loading = LoadAsync();
        _earlier = loading;
        _ = loading.ContinueWith(
            _ =>
            {
                if (ReferenceEquals(_earlier, loading)) _earlier = null;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            System.Threading.Tasks.TaskScheduler.Default);
        return loading;

        async Task<TranscriptView> LoadAsync()
        {
            // 与上游一样让这次加载从不「同步完成」：并发调用于是总能共享它。
            await Task.Yield();
            var query = new EntryRange
            {
                MaxEntryId = EntryId.From(low), Order = ScanOrder.Descending,
            };
            var page = await _load(query, _pageSize, context).ConfigureAwait(false);
            var items = page.Items.Reverse().ToList();
            var oldest = items.Count > 0 ? items[0] : null;
            // 没有下一页，或下一页是空的：转录从这里开始。
            if (page.Next is null || oldest is null) Cover(NegativeInfinity, low, items);
            else Cover(oldest.Id.Value - 1, low, items);
            return Result();
        }
    }

    /// <summary>
    /// 加载 <paramref name="gap"/>（<see cref="TranscriptItem.Items"/> 里的一个缺口项）的下一页，从它的开头缩小它，
    /// 什么都不缺了就移除它。一个已经缩小过的缺口加载它变成的那个缺口；一个不再存在的缺口返回当前视图。
    /// </summary>
    public Task<TranscriptView> FillGapAsync(TranscriptItem.Gap gap, Context context)
    {
        var current = _gaps.FirstOrDefault(each => each.After == gap.After && each.UpTo == gap.UpTo)
            ?? _gaps.FirstOrDefault(each => each.After.Value < gap.UpTo.Value && gap.After.Value < each.UpTo.Value);
        if (current is null)
        {
            return _last is null
                ? Task.FromException<TranscriptView>(
                    new InvalidOperationException("ConversationTranscript: merge a view first"))
                : Task.FromResult(Result());
        }

        // 以调用解析到的缺口为键，使一个过时的描述符与当前那个共享这次加载。
        var key = $"{current.After.Value}:{current.UpTo.Value}";
        if (_filling.TryGetValue(key, out var running)) return running;
        TranscriptItem.Gap captured = current;
        var filling = FillAsync();
        _filling[key] = filling;
        _ = filling.ContinueWith(
            _ =>
            {
                if (_filling.TryGetValue(key, out var tracked) && ReferenceEquals(tracked, filling)) _filling.Remove(key);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            System.Threading.Tasks.TaskScheduler.Default);
        return filling;

        async Task<TranscriptView> FillAsync()
        {
            await Task.Yield();
            var query = new EntryRange
            {
                MinEntryId = EntryId.From(captured.After.Value + 1),
                MaxEntryId = EntryId.From(captured.UpTo.Value - 1),
                Order = ScanOrder.Ascending,
            };
            var page = await _load(query, _pageSize, context).ConfigureAwait(false);
            var newest = page.Items.Count > 0 ? page.Items[^1] : null;
            // 没有下一页，或下一页是空的：没有别的缺失了。
            if (page.Next is null || newest is null) Cover(captured.After.Value, captured.UpTo.Value - 1, page.Items);
            else Cover(captured.After.Value, newest.Id.Value, page.Items);
            return Result();
        }
    }

    /// <summary>
    /// 记下 ID 在 (<paramref name="lo"/>, <paramref name="hi"/>] 里的每个条目都已知，<paramref name="added"/> 是那些
    /// 条目（按 ID 序）：持有新的、缩小它们填上的缺口、扩展已知范围，并在范围接不上时开一个缺口。
    /// </summary>
    private void Cover(long lo, long hi, IReadOnlyList<EntryRecord> added)
    {
        var fresh = added.Where(entry => !_ids.Contains(entry.Id.Value)).ToList();
        foreach (var entry in fresh) _ids.Add(entry.Id.Value);
        var held = _entries;
        // 新条目都排在已持有的之后：items 从末尾增长。
        var appended = fresh.Count == 0 || held.Count == 0 || fresh[0].Id.Value > held[^1].Value.Id.Value;
        var items = fresh.Select(entry => new TranscriptItem.Entry(entry)).ToList();
        if (items.Count > 0) _entries = appended ? [.. held, .. items] : MergeById(held, items);
        var rebuild = false;
        if (!_started || (held.Count == 0 && _gaps.Count == 0 && lo >= _tail))
        {
            // 第一个范围，或空转录之后的一个范围：它就是客户端知道的全部。
            rebuild = !_started || lo != _low;
            _started = true;
            _low = lo;
            _tail = Math.Max(_tail, hi);
        }
        else if (lo > _tail)
        {
            // 越过已知的最新 ID：中间那些是缺失的。
            var upTo = added.Count > 0 ? added[0].Id.Value : lo + 1;
            _gaps = [.. _gaps, GapOf(_tail, upTo)];
            _tail = hi;
            rebuild = true;
        }
        else if (hi != NegativeInfinity && hi < _low)
        {
            // 低于已知的最旧 ID：中间那些是缺失的。
            var upTo = held.FirstOrDefault(item => item.Value.Id.Value > hi)?.Value.Id.Value ?? _low + 1;
            _gaps = [GapOf(hi, upTo), .. _gaps];
            _low = lo;
            rebuild = true;
        }
        else
        {
            var gaps = Subtract(lo, hi);
            if (!ReferenceEquals(gaps, _gaps))
            {
                _gaps = gaps;
                rebuild = true;
            }

            if (lo < _low)
            {
                _low = lo;
                rebuild = true;
            }

            if (hi > _tail) _tail = hi;
        }

        if (rebuild || !appended) _items = Build();
        else if (items.Count > 0) _items = [.. _items, .. items];
    }

    /// <summary>去掉 (<paramref name="lo"/>, <paramref name="hi"/>] 之后的缺口；没有一个变化时返回同一个数组。</summary>
    private List<TranscriptItem.Gap> Subtract(long lo, long hi)
    {
        if (hi <= lo) return _gaps;
        var changed = false;
        var gaps = new List<TranscriptItem.Gap>();
        foreach (var gap in _gaps)
        {
            // 缺失的：ID 在 (after, upTo) 里；已知的：ID 在 (lo, hi] 里。
            if (hi <= gap.After.Value || lo + 1 >= gap.UpTo.Value)
            {
                gaps.Add(gap);
                continue;
            }

            changed = true;
            if (lo > gap.After.Value)
            {
                var firstKnown = _entries.FirstOrDefault(item => item.Value.Id.Value > lo)?.Value.Id.Value ?? lo + 1;
                gaps.Add(GapOf(gap.After.Value, Math.Min(gap.UpTo.Value, firstKnown)));
            }

            if (hi + 1 < gap.UpTo.Value) gaps.Add(GapOf(hi, gap.UpTo.Value));
        }

        return changed ? gaps : _gaps;
    }

    /// <summary>按 ID 序的条目：开头未知时先是 "earlier"，然后每个缺口排在它后面的条目之前。</summary>
    private List<TranscriptItem> Build()
    {
        var items = new List<TranscriptItem>();
        if (_low != NegativeInfinity) items.Add(new TranscriptItem.Earlier());
        var gaps = _gaps;
        var next = 0;
        foreach (var item in _entries)
        {
            while (next < gaps.Count && gaps[next].After.Value < item.Value.Id.Value) items.Add(gaps[next++]);
            items.Add(item);
        }

        while (next < gaps.Count) items.Add(gaps[next++]);
        return items;
    }

    /// <summary>以当前 items 构成的视图；什么都没变时是上一个。</summary>
    private TranscriptView Result()
    {
        var last = _last!;
        var previous = _view;
        if (previous is not null
            && ReferenceEquals(previous.Items, _items)
            && ReferenceEquals(previous.Docs, last.Docs)
            && ReferenceEquals(previous.Conversation, last.Conversation))
        {
            return previous;
        }

        _view = new TranscriptView { Conversation = last.Conversation, Docs = last.Docs, Items = _items };
        return _view;
    }

    private static TranscriptItem.Gap GapOf(long after, long upTo) => new(EntryId.From(after), EntryId.From(upTo));

    /// <summary>两份按 ID 序的条目列表一趟合并。</summary>
    private static List<TranscriptItem.Entry> MergeById(
        IReadOnlyList<TranscriptItem.Entry> left, IReadOnlyList<TranscriptItem.Entry> right)
    {
        var merged = new List<TranscriptItem.Entry>(left.Count + right.Count);
        var i = 0;
        var j = 0;
        while (i < left.Count && j < right.Count)
        {
            merged.Add(left[i].Value.Id.Value < right[j].Value.Id.Value ? left[i++] : right[j++]);
        }

        while (i < left.Count) merged.Add(left[i++]);
        while (j < right.Count) merged.Add(right[j++]);
        return merged;
    }
}
