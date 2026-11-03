using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 客户端转录（conversation-transcript.test.ts 的核心用例）：窗口化视图之上的一层，把窗口拼成客户端持有的整份
/// 转录，缺口、加载更早、加载缺口与续接都在这里。
/// </summary>
public class ConversationTranscriptTests
{
    private static readonly Context Ctx = Context.Background;

    [Fact]
    public void ShowsTheFirstWindowUnderAnEarlierMarkerOrAloneWhenItStartsTheTranscript()
    {
        var store = new FakeStore(20);
        var transcript = new ConversationTranscript(store.Load);

        // 窗口从转录中间起（before 指出它前面还有条目）：上面还有内容。
        var first = transcript.Merge(View(store, 11, 20, before: 10));
        Assert.Equal(["earlier", .. Kinds(Enumerable.Range(11, 10).Select(id => $"e{id}").ToArray())], KindsOf(first));

        // 从头起：没有更早的标记。
        var fromStart = new ConversationTranscript(store.Load).Merge(View(store, 1, 10));
        Assert.Equal(Enumerable.Range(1, 10).Select(id => $"e{id}"), KindsOf(fromStart));
    }

    [Fact]
    public void AppendsKeepsEntriesTheWindowDroppedAndReturnsTheSameItemsWhenNothingIsAdded()
    {
        var store = new FakeStore(14);
        var transcript = new ConversationTranscript(store.Load);
        var first = transcript.Merge(View(store, 9, 12, before: 8));

        // 窗口追加，并把掉出的最旧条目留在转录里：窗口从 11 起，转录仍从 9 起。
        var dropped = View(store, 11, 14, before: 10);
        var grown = transcript.Merge(dropped);
        Assert.Equal(["earlier", "e9", "e10", "e11", "e12", "e13", "e14"], KindsOf(grown));
        Assert.Equal(["earlier", "e9", "e10", "e11", "e12"], KindsOf(first));
        // 同一个视图值再合并一次：什么都没加，返回上一份 items 与视图。
        Assert.Same(grown, transcript.Merge(dropped));
        Assert.Same(grown.Items, transcript.Merge(dropped).Items);
    }

    [Fact]
    public async Task OpensAGapWhenAWindowDoesNotContinueTheTranscriptAndFillsItPageByPage()
    {
        var store = new FakeStore(30);
        var transcript = new ConversationTranscript(store.Load, pageSize: 3);
        transcript.Merge(View(store, 1, 5));

        // 窗口从 10 起，而 6..9 从没见过：那里是一个缺口。
        var stalled = transcript.Merge(View(store, 10, 15, before: 9));
        Assert.Equal(["e1", "e2", "e3", "e4", "e5", "gap:5-10", "e10", "e11", "e12", "e13", "e14", "e15"], KindsOf(stalled));

        var gap = Assert.IsType<TranscriptItem.Gap>(stalled.Items[5]);
        var filled = await transcript.FillGapAsync(gap, Ctx);
        // 一页 3 条：缺口从它的开头缩小，填进来的条目排在它前面。
        var shrunk = Assert.Single(filled.Items.OfType<TranscriptItem.Gap>());
        Assert.Equal(8L, shrunk.After.Value);
        Assert.Equal(10L, shrunk.UpTo.Value);
        Assert.Equal(["e6", "e7", "e8"], KindsOf(filled).SkipWhile(kind => kind != "e6").Take(3));

        var done = await transcript.FillGapAsync(shrunk, Ctx);
        Assert.Empty(done.Items.OfType<TranscriptItem.Gap>());
        Assert.Equal(
            ["e1", "e2", "e3", "e4", "e5", "e6", "e7", "e8", "e9", "e10", "e11", "e12", "e13", "e14", "e15"],
            KindsOf(done));
    }

    [Fact]
    public async Task LoadsEarlierPagesUntilTheStartThenDropsTheMarker()
    {
        var store = new FakeStore(10);
        var transcript = new ConversationTranscript(store.Load, pageSize: 4);
        var first = transcript.Merge(View(store, 7, 10, before: 6));
        Assert.Equal(["earlier", "e7", "e8", "e9", "e10"], KindsOf(first));

        var step = await transcript.LoadEarlierAsync(Ctx);
        Assert.Equal(["earlier", "e3", "e4", "e5", "e6", "e7"], KindsOf(step)[..6]);

        var done = await transcript.LoadEarlierAsync(Ctx);
        Assert.Equal(["e1", "e2"], KindsOf(done)[..2]);
        Assert.DoesNotContain(done.Items, item => item is TranscriptItem.Earlier);
        // 到头之后不再抓：返回同一个视图。
        Assert.Same(done, await transcript.LoadEarlierAsync(Ctx));
    }

    [Fact]
    public async Task SharesOneLoadBetweenConcurrentCallsAndBetweenAStaleGapAndItsSuccessor()
    {
        var store = new FakeStore(30);
        var transcript = new ConversationTranscript(store.Load, pageSize: 2);
        transcript.Merge(View(store, 1, 3));
        var stalled = transcript.Merge(View(store, 12, 15, before: 11));
        var gap = Assert.Single(stalled.Items.OfType<TranscriptItem.Gap>());

        var callsBefore = store.Calls;
        var first = transcript.FillGapAsync(gap, Ctx);
        var second = transcript.FillGapAsync(gap, Ctx);
        Assert.Same(first, second);
        await first;
        Assert.Equal(callsBefore + 1, store.Calls);

        // 缺口缩小之后，拿旧描述符再调一次加载的是它变成的那个缺口，仍然共享。
        var shrunk = await transcript.FillGapAsync(gap, Ctx);
        var shrunkGap = Assert.Single(shrunk.Items.OfType<TranscriptItem.Gap>());
        Assert.NotEqual(gap.After, shrunkGap.After);
        var again = transcript.FillGapAsync(gap, Ctx);
        var current = transcript.FillGapAsync(shrunkGap, Ctx);
        Assert.Same(again, current);
        await again;
    }

    [Fact]
    public async Task ResumesAfterNewestAndOpensAGapForAWindowPastIt()
    {
        var store = new FakeStore(20);
        var transcript = new ConversationTranscript(store.Load);
        Assert.Null(transcript.Resume);
        Assert.Null(transcript.Newest);

        var window = View(store, 1, 5);
        var first = transcript.Merge(window);
        Assert.Equal(5L, transcript.Newest!.Value);
        Assert.Equal(6L, transcript.Resume!.From!.Value);

        // 紧跟其后的同一个窗口什么也没加（before 落在已知范围内）；再往后一个窗口越过已知的最新条目，于是中间成为缺口。
        Assert.Same(first, transcript.Merge(window));
        var resumed = transcript.Merge(View(store, 8, 9, before: 7));
        Assert.Equal(9L, transcript.Newest!.Value);
        Assert.Equal(["e1", "e2", "e3", "e4", "e5", "gap:5-8", "e8", "e9"], KindsOf(resumed));
    }

    [Fact]
    public async Task RejectsLoadEarlierBeforeTheFirstMergeAndReturnsTheViewForAGapThatIsGone()
    {
        var store = new FakeStore(5);
        var empty = new ConversationTranscript(store.Load);
        await Assert.ThrowsAsync<InvalidOperationException>(() => empty.LoadEarlierAsync(Ctx));

        var transcript = new ConversationTranscript(store.Load);
        transcript.Merge(View(store, 1, 5));
        var stale = new TranscriptItem.Gap(EntryId.From(5), EntryId.From(9));
        Assert.Same(transcript.Merge(View(store, 1, 5)), await transcript.FillGapAsync(stale, Ctx));
    }

    // ─── 脚手架 ────────────────────────────────────────────────────────────

    private static List<string> KindsOf(TranscriptView view)
    {
        var kinds = new List<string>();
        foreach (var item in view.Items)
        {
            kinds.Add(item switch
            {
                TranscriptItem.Entry entry => entry.Value.Kind,
                TranscriptItem.Gap gap => $"gap:{gap.After.Value}-{gap.UpTo.Value}",
                TranscriptItem.Earlier => "earlier",
                _ => "?",
            });
        }

        return kinds;
    }

    private static List<string> Kinds(params string[] kinds) => [.. kinds];

    /// <summary>一个窗口：<c>[from, to]</c> 的条目，<c>before</c> 给它之前那个。</summary>
    private static ConversationView View(FakeStore store, long from, long to, long? before = null)
        => new()
        {
            Conversation = new ConversationRecord { Id = ConversationId.From(1) },
            Entries = [.. store.Entries.Where(entry => entry.Id.Value >= from && entry.Id.Value <= to)],
            Before = before is { } id ? EntryId.From(id) : null,
            Docs = new Dictionary<string, IReadOnlyDictionary<string, object?>>(),
        };

    /// <summary>内存里的条目与带游标的分页读取：只认 ID 范围、顺序与每页上限。</summary>
    private sealed class FakeStore(int count)
    {
        public IReadOnlyList<EntryRecord> Entries { get; } =
            [.. Enumerable.Range(1, count).Select(id => new EntryRecord
            {
                Id = EntryId.From(id),
                ConversationId = ConversationId.From(1),
                Kind = $"e{id}",
            })];

        public int Calls { get; private set; }

        /// <summary>
        /// 一次一页：从范围的边界起取 <paramref name="limit"/> 条，<c>Next</c> 只说「还有更多」
        /// （转录只用它判断是否到了尽头，从不回传游标）。
        /// </summary>
        public Task<Page<EntryRecord>> Load(EntryRange query, int limit, Context context)
        {
            Calls++;
            var matching = Entries.Where(entry =>
                    (query.MinEntryId is not { } min || entry.Id.Value >= min.Value)
                    && (query.MaxEntryId is not { } max || entry.Id.Value <= max.Value))
                .ToList();
            if (query.Order != ScanOrder.Ascending) matching.Reverse();
            var page = matching.Take(limit).ToList();
            var next = matching.Count > page.Count
                ? new Dictionary<string, object?> { ["offset"] = (long)page.Count }
                : null;
            return Task.FromResult(new Page<EntryRecord> { Items = page, Next = next });
        }
    }
}
