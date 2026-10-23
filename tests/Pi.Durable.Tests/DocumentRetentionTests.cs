using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 文档缓存按使用量回收（`documentRetentionMs`）。对应上游 `f44707e7f`：
/// 没有 watch 或 document state 观察的已缓存文档，超过保留时长没被读写就离开缓存，
/// 下次访问重新从 Storage 载入；被观察的化身永不驱逐。
/// </summary>
public class DocumentRetentionTests
{
    private static Context Ctx => Context.Background;

    private static async Task<ConversationId> CreateConversationAsync(DurableSession session)
        => await session.CommitAsync(
            async tx => (await tx.CreateConversationAsync(new ConversationOwnership.Ownerless())
                .ConfigureAwait(false)).Id,
            Ctx);

    /// <summary>写一次 agent 文档：它因此进入缓存（提交后按「刚用过」记账）。</summary>
    private static Task TouchAgentDocAsync(DurableSession session, ConversationId conv)
        => session.CommitAsync(
            tx => AgentDocs.ConfigureAsync((Transaction)tx, conv, new AgentChange { Instructions = "x" }), Ctx);

    /// <summary>一次空提交：不写任何东西，但会跑提交线上的驱逐。</summary>
    private static Task CommitNothingAsync(DurableSession session)
        => session.CommitAsync(_ => Task.CompletedTask, Ctx);

    [Fact]
    public void DocumentRetentionTakesNonNegativeMillisecondsOrTheDefault()
    {
        Assert.Equal(1_000d, DurableSession.DocumentRetention(1_000));
        Assert.Equal(0d, DurableSession.DocumentRetention(0));
        Assert.Equal(double.PositiveInfinity, DurableSession.DocumentRetention(double.PositiveInfinity));
        // 负数与缺省（「不是毫秒数」）都回落到默认十分钟。
        Assert.Equal(600_000d, DurableSession.DocumentRetention(null));
        Assert.Equal(600_000d, DurableSession.DocumentRetention(-1));
    }

    [Fact]
    public async Task EvictsAnUnobservedDocumentAfterTheRetentionAndReloadsIt()
    {
        var clock = 1_000L;
        var session = new DurableSession(new MemoryStorage(), () => clock, () => 100d);
        var conv = await CreateConversationAsync(session);
        await TouchAgentDocAsync(session, conv);

        // 刚写过：在缓存里，且没有观察者。
        var sizes = session.DocumentCacheSizes;
        Assert.Equal(1, sizes.Cached);
        Assert.Equal(1, sizes.Idle);
        Assert.Equal(0, sizes.Attached);

        // 保留时长内：仍不驱逐。
        clock += 50;
        await CommitNothingAsync(session);
        Assert.Equal(1, session.DocumentCacheSizes.Cached);

        // 超过保留时长：驱逐，但值仍可从 Storage 读到（冷加载回缓存）。
        clock += 60;
        await CommitNothingAsync(session);
        Assert.Equal(0, session.DocumentCacheSizes.Cached);
        Assert.Equal(0, session.DocumentCacheSizes.Idle);
        Assert.NotNull(await session.SnapshotAsync(AgentDocs.AgentDoc, conv, Ctx));
        Assert.Equal(1, session.DocumentCacheSizes.Cached);
    }

    [Fact]
    public async Task KeepsAWatchedDocumentUntilTheWatchStops()
    {
        var clock = 1_000L;
        var session = new DurableSession(new MemoryStorage(), () => clock, () => 100d);
        var conv = await CreateConversationAsync(session);
        await TouchAgentDocAsync(session, conv);

        var watch = await session.WatchDocAsync(AgentDocs.AgentDoc, conv, Ctx);
        Assert.NotNull(watch);
        // 被观察的化身被钉住：离开空闲集合。
        Assert.Equal(1, session.DocumentCacheSizes.Attached);
        Assert.Equal(0, session.DocumentCacheSizes.Idle);

        clock += 10_000;
        await CommitNothingAsync(session);
        Assert.Equal(1, session.DocumentCacheSizes.Cached);

        // 观察者摘下后重新成为空闲，再过保留时长就被驱逐。
        await watch!.Stop();
        Assert.Equal(0, session.DocumentCacheSizes.Attached);
        Assert.Equal(1, session.DocumentCacheSizes.Idle);
        clock += 10_000;
        await CommitNothingAsync(session);
        Assert.Equal(0, session.DocumentCacheSizes.Cached);
    }

    [Fact]
    public async Task InfinityKeepsDocumentsAndZeroDropsThemRightAfterUse()
    {
        var clock = 1_000L;
        var keeping = new DurableSession(new MemoryStorage(), () => clock, () => double.PositiveInfinity);
        var keptConv = await CreateConversationAsync(keeping);
        await TouchAgentDocAsync(keeping, keptConv);
        clock += 10_000_000;
        await CommitNothingAsync(keeping);
        Assert.Equal(1, keeping.DocumentCacheSizes.Cached);

        var dropping = new DurableSession(new MemoryStorage(), () => clock, () => 0d);
        var droppedConv = await CreateConversationAsync(dropping);
        await TouchAgentDocAsync(dropping, droppedConv);
        // 0 表示用完即弃：下一次提交就把没有观察者的文档带走。
        await CommitNothingAsync(dropping);
        Assert.Equal(0, dropping.DocumentCacheSizes.Cached);
        // 冷加载仍然拿得到值。
        Assert.NotNull(await dropping.SnapshotAsync(AgentDocs.AgentDoc, droppedConv, Ctx));
    }
}
