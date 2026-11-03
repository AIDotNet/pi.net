using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using Path = Pi.Chord.Delta.Path;
using Seg = Pi.Chord.Delta.Seg;

/// <summary>
/// 窗口化视图（f3255de76）：每个观察者持有自己的窗口，起点可以是“最新的 min 个”或 <c>from</c>，超过 max 时一次
/// splice 丢掉最旧的、降到 min，<c>Before</c> 指出窗口之前那个条目。
/// </summary>
public class HarnessViewWindowTests
{
    private static readonly Context Ctx = Context.Background;

    [Fact]
    public async Task StartsAtTheNewestMinEntriesAndReportsTheEntryBeforeThem()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        var ids = await NotesAsync(root, 5);
        try
        {
            var watch = await root.WatchAsync(Ctx, new ConversationWindow { Min = 2, Max = 4 });
            try
            {
                Assert.Equal([ids[3], ids[4]], watch.Value.Entries.Select(entry => entry.Id));
                Assert.Equal(ids[2], watch.Value.Before);
            }
            finally
            {
                await watch.Stop();
            }
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task AppendsUpToMaxThenDropsTheOldestDownToMinInOneSplice()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        var ids = await NotesAsync(root, 2);
        try
        {
            var watch = await root.WatchAsync(Ctx, new ConversationWindow { Min = 2, Max = 4 });
            var frames = new List<(ConversationView Value, IReadOnlyList<DeltaOp> Ops)>();
            watch.Start((value, ops, _) =>
            {
                lock (frames) frames.Add((value, ops));
                return Task.CompletedTask;
            });
            // 再加 3 条：2→3→4（到上限）→ 一次丢两条降到 2。
            ids.AddRange(await NotesAsync(root, 3));
            await HarnessTestSupport.WaitForAsync(() => watch.Value.Entries.Count == 2
                && watch.Value.Entries[^1].Id == ids[^1]);
            await HarnessTestSupport.FlushAsync();

            var last = watch.Value;
            Assert.Equal([ids[^2], ids[^1]], last.Entries.Select(entry => entry.Id));
            Assert.Equal(ids[^3], last.Before);
            // 丢掉最旧的是一次 splice（不是逐条移除），并把 before 一起写上。
            var drop = frames.SelectMany(frame => frame.Ops)
                .OfType<DeltaOp.Splice>()
                .Single(splice => splice.Path.Segments.SequenceEqual([Seg.Key("entries")]) && splice.Remove == 3);
            Assert.Equal(0, drop.Index);
            Assert.Empty(drop.Items);
            Assert.Contains(
                frames.SelectMany(frame => frame.Ops),
                op => op is DeltaOp.Set set && set.Path.Segments.SequenceEqual([Seg.Key("before")]));
            await watch.Stop();
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task StartsAtFromWhenItFitsAndIsEmptyWithBeforeTheNewestWhenItDoesNot()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        var ids = await NotesAsync(root, 5);
        try
        {
            // 从第 4 个起，2 条装得进 max=3：窗口就是它们，before 是第 3 个。
            var fitting = await root.WatchAsync(
                Ctx, new ConversationWindow { From = ids[3], Min = 1, Max = 3 });
            try
            {
                Assert.Equal([ids[3], ids[4]], fitting.Value.Entries.Select(entry => entry.Id));
                Assert.Equal(ids[2], fitting.Value.Before);
            }
            finally
            {
                await fitting.Stop();
            }

            // 从第 2 个起会超过 max=3：装不下就退回「最新的 min 个」，before 是它们之前那个。
            var overflowing = await root.WatchAsync(
                Ctx, new ConversationWindow { From = ids[1], Min = 1, Max = 3 });
            try
            {
                Assert.Equal([ids[4]], overflowing.Value.Entries.Select(entry => entry.Id));
                Assert.Equal(ids[3], overflowing.Value.Before);
            }
            finally
            {
                await overflowing.Stop();
            }

            // 越过最新条目：同样从空窗口开始。
            var past = await root.WatchAsync(
                Ctx, new ConversationWindow { From = EntryId.From(ids[4].Value + 10) });
            try
            {
                Assert.Empty(past.Value.Entries);
                Assert.Equal(ids[4], past.Value.Before);
            }
            finally
            {
                await past.Stop();
            }
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task RejectsAWindowWhoseMaxIsBelowMinOrWhoseMinIsNotPositive()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => root.WatchAsync(Ctx, new ConversationWindow { Min = 5, Max = 4 }));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => root.WatchAsync(Ctx, new ConversationWindow { Min = 0 }));
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task MergesAWindowIntoATranscriptAndResumesFromItAfterAStall()
    {
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(
            new MemoryStorage(), HarnessTestSupport.NewChatSetup(HarnessTestSupport.AssistantReply("ok")));
        var transcript = new ConversationTranscript(
            (query, limit, context) => root.EntriesAsync(
                new EntryQuery
                {
                    ConversationId = root.Id,
                    Order = query.Order,
                    MinEntryId = query.MinEntryId,
                    MaxEntryId = query.MaxEntryId,
                }, limit, null, context),
            pageSize: 2);
        try
        {
            await NotesAsync(root, 5);
            var first = await root.WatchAsync(Ctx, new ConversationWindow { Min = 2, Max = 3 });
            var merged = transcript.Merge(first.Value);
            // 窗口只给了最新的两个，before 让它知道上面还有内容。
            Assert.Contains(merged.Items, item => item is TranscriptItem.Earlier);
            Assert.Equal(2, merged.Items.OfType<TranscriptItem.Entry>().Count());
            await first.Stop();

            // 停摆之后接着续：resume 从最新条目之后起，错过的条目正好装进它的窗口，于是没有缺口。
            await NotesAsync(root, 2);
            var resumed = await root.WatchAsync(Ctx, transcript.Resume);
            var afterResume = transcript.Merge(resumed.Value);
            Assert.Empty(afterResume.Items.OfType<TranscriptItem.Gap>());
            // 更早的部分（第一个窗口之前）要自己加载：一直加载到没有 "earlier" 标记为止。
            var complete = afterResume;
            while (complete.Items.Any(item => item is TranscriptItem.Earlier))
            {
                complete = await transcript.LoadEarlierAsync(Ctx);
            }

            Assert.Equal(
                (await HarnessTestSupport.AllEntriesAsync(root)).Select(entry => entry.Id),
                complete.Items.OfType<TranscriptItem.Entry>().Select(item => item.Value.Id));
            await resumed.Stop();
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    private static async Task<List<EntryId>> NotesAsync(IConversation conversation, int count)
    {
        var ids = new List<EntryId>();
        for (var index = 0; index < count; index++)
        {
            EntryId id = default;
            await conversation.CommitAsync(async tx =>
            {
                id = (await tx.AppendEntryAsync(conversation.Id, new EntryDraft { Kind = "note" })).Id;
                return true;
            }, Ctx);
            ids.Add(id);
        }

        return ids;
    }
}
