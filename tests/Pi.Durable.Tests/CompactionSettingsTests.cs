using Pi.Ai.Models;
using Pi.Chord.Context;
using Pi.Durable;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

// ============================================================================
// v1.5.1 #11771 per-conversation compaction settings（上游 922c3ff2b）。
// 逐字段分层：会话 model override → harness model override → 会话自身字段 →
// harness 全局；会话 enabled:false 也胜过 harness 的 model override。
// 对照上游 harness/types.ts 的 compactionPolicy 与 harness/agent.ts 的
// compactionSettings / compactionFields 校验。
// ============================================================================

public sealed class CompactionSettingsTests
{
    private static Context Ctx => Context.Background;

    // harness 全局 + harness 的 model override。
    private static readonly CompactionPolicy Harness = new()
    {
        Enabled = true,
        ReserveTokens = 16_384,
        KeepRecentTokens = 20_000,
        BackgroundTokens = 32_768,
        ModelOverrides = new Dictionary<string, CompactionPolicyOverride>
        {
            ["prov/model"] = new() { ReserveTokens = 1000, KeepRecentTokens = 2000, Enabled = false },
        },
    };

    private static readonly ModelRef Model = new("prov", "model");

    [Fact]
    public void NoConversation_StillUsesHarnessModelOverrideOnly()
    {
        // 未传会话时保持旧行为（#10942）：model override 覆盖全局。
        var policy = Harness.ForModel(Model);
        Assert.False(policy.Enabled);
        Assert.Equal(1000, policy.ReserveTokens);
        Assert.Equal(2000, policy.KeepRecentTokens);
        Assert.Equal(32_768, policy.BackgroundTokens);
    }

    [Fact]
    public void ConversationGeneralFields_BeatHarnessGlobal_ButNotModelOverrides()
    {
        var conversation = new CompactionSettings
        {
            ReserveTokens = 500,
            BackgroundTokens = 100,
        };
        var policy = Harness.ForModel(Model, conversation);
        // reserveTokens：harness model override(1000) 胜过会话一般字段(500)。
        Assert.Equal(1000, policy.ReserveTokens);
        // keepRecentTokens：harness model override(2000)。
        Assert.Equal(2000, policy.KeepRecentTokens);
        // backgroundTokens：harness model override 没写 → 会话一般字段(100) 胜过全局。
        Assert.Equal(100, policy.BackgroundTokens);
        // enabled：harness model override(false)。
        Assert.False(policy.Enabled);
    }

    [Fact]
    public void ConversationModelOverride_BeatsHarnessModelOverride()
    {
        var conversation = new CompactionSettings
        {
            ModelOverrides = new Dictionary<string, CompactionPolicyOverride>
            {
                ["prov/model"] = new() { ReserveTokens = 7, KeepRecentTokens = 8, BackgroundTokens = 9, Enabled = true },
            },
        };
        var policy = Harness.ForModel(Model, conversation);
        Assert.True(policy.Enabled);
        Assert.Equal(7, policy.ReserveTokens);
        Assert.Equal(8, policy.KeepRecentTokens);
        Assert.Equal(9, policy.BackgroundTokens);
    }

    [Fact]
    public void ConversationEnabledFalse_BeatsHarnessModelOverrides()
    {
        // 特例：会话自身 enabled:false 胜过 harness 的 model override（关闭压缩不依赖模型）。
        var conversation = new CompactionSettings { Enabled = false };
        var overrideTrue = Harness with
        {
            Enabled = true,
            ModelOverrides = new Dictionary<string, CompactionPolicyOverride>
            {
                ["prov/model"] = new() { Enabled = true },
            },
        };
        Assert.False(overrideTrue.ForModel(Model, conversation).Enabled);

        // 但会话 model override 的 enabled:true 仍胜过会话自身的 enabled:false。
        var withConvOverride = conversation with
        {
            ModelOverrides = new Dictionary<string, CompactionPolicyOverride>
            {
                ["prov/model"] = new() { Enabled = true },
            },
        };
        Assert.True(overrideTrue.ForModel(Model, withConvOverride).Enabled);
    }

    [Fact]
    public void ConversationGeneralField_WinsWhenNoModelOverrideApplies()
    {
        var noOverrides = Harness with { ModelOverrides = null };
        var conversation = new CompactionSettings { KeepRecentTokens = 42, Enabled = false };
        var policy = noOverrides.ForModel(Model, conversation);
        Assert.Equal(42, policy.KeepRecentTokens);
        Assert.Equal(16_384, policy.ReserveTokens);
        Assert.False(policy.Enabled);
    }

    [Fact]
    public void ZeroIsAnExplicitValue_NotAnAbsentField()
    {
        var conversation = new CompactionSettings { BackgroundTokens = 0 };
        var noOverrides = Harness with { ModelOverrides = null };
        Assert.Equal(0, noOverrides.ForModel(Model, conversation).BackgroundTokens);
    }

    [Fact]
    public void UnknownModel_ConversationOverridesDoNotApply()
    {
        var conversation = new CompactionSettings
        {
            ReserveTokens = 5,
            ModelOverrides = new Dictionary<string, CompactionPolicyOverride>
            {
                ["prov/model"] = new() { ReserveTokens = 7 },
            },
        };
        // 会话一般字段仍生效，model override 只对精确键生效。
        var policy = Harness.ForModel(new ModelRef("prov", "other"), conversation);
        Assert.Equal(5, policy.ReserveTokens);
        Assert.Equal(20_000, policy.KeepRecentTokens);
    }

    // ─── configure：校验 + 序列化往返 ─────────────────────────────────────────

    private static async Task<ConversationId> CreateConversationAsync(DurableSession session)
        => await session.CommitAsync(
            async tx => (await tx.CreateConversationAsync(
                new ConversationOwnership.Ownerless())).Id,
            Ctx);

    [Fact]
    public async Task Configure_StoresAndRestoresCompactionSettings()
    {
        var session = new DurableSession(new MemoryStorage());
        var conversation = await CreateConversationAsync(session);
        var settings = new CompactionSettings
        {
            Enabled = false,
            ReserveTokens = 10,
            KeepRecentTokens = 20,
            BackgroundTokens = 0,
            ModelOverrides = new Dictionary<string, CompactionPolicyOverride>
            {
                ["prov/model"] = new() { ReserveTokens = 99, Enabled = true },
            },
        };

        await session.CommitAsync(tx => AgentDocs.ConfigureAsync((Transaction)tx, conversation,
            new AgentChange { Compaction = settings }), Ctx);

        var snapshot = await session.SnapshotAsync(AgentDocs.AgentDoc, conversation, Ctx);
        var state = AgentDocs.StateFromJson(snapshot);
        Assert.NotNull(state);
        var restored = state.Compaction;
        Assert.NotNull(restored);
        Assert.False(restored.Enabled);
        Assert.Equal(10, restored.ReserveTokens);
        Assert.Equal(20, restored.KeepRecentTokens);
        Assert.Equal(0, restored.BackgroundTokens);
        Assert.NotNull(restored.ModelOverrides);
        Assert.Equal(99, restored.ModelOverrides["prov/model"].ReserveTokens);
        Assert.True(restored.ModelOverrides["prov/model"].Enabled);
    }

    [Fact]
    public async Task Configure_ClearCompactionRemovesIt()
    {
        var session = new DurableSession(new MemoryStorage());
        var conversation = await CreateConversationAsync(session);
        await session.CommitAsync(tx => AgentDocs.ConfigureAsync((Transaction)tx, conversation,
            new AgentChange { Compaction = new CompactionSettings { KeepRecentTokens = 5 } }), Ctx);

        await session.CommitAsync(tx => AgentDocs.ConfigureAsync((Transaction)tx, conversation,
            new AgentChange { ClearCompaction = true }), Ctx);

        var snapshot = await session.SnapshotAsync(AgentDocs.AgentDoc, conversation, Ctx);
        Assert.Null(AgentDocs.StateFromJson(snapshot)!.Compaction);
    }

    [Fact]
    public async Task Configure_RejectsNegativeTokenCounts()
    {
        var session = new DurableSession(new MemoryStorage());
        var conversation = await CreateConversationAsync(session);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.CommitAsync(
            tx => AgentDocs.ConfigureAsync((Transaction)tx, conversation,
                new AgentChange { Compaction = new CompactionSettings { ReserveTokens = -1 } }),
            Ctx));
    }

    [Fact]
    public void ResolveAgent_CarriesConversationCompaction()
    {
        var state = new AgentState { Compaction = new CompactionSettings { KeepRecentTokens = 3 } };
        var snapshot = Registry.CreateRegistry().Snapshot();
        var settings = AgentDocs.ResolveSettings(null);
        var agent = AgentDocs.ResolveAgent(state, snapshot, settings, (_, _) => { });
        Assert.NotNull(agent.Compaction);
        Assert.Equal(3, agent.Compaction!.KeepRecentTokens);
        // 分层解析确实经过会话：该会话 keepRecentTokens 覆盖全局。
        Assert.Equal(3, settings.Compaction.ForModel(Model, agent.Compaction).KeepRecentTokens);
    }
}
