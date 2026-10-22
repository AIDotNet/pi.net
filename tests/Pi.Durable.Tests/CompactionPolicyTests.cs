using Pi.Ai.Models;
using Pi.Durable.Harness;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 上游 #10942：<c>settings.compaction.modelOverrides</c> 按精确的 <c>"provider/modelId"</c> 逐字段覆盖
/// 全局压缩设置，这样同一个 harness 可以同时服务 32k 与 1M 窗口的模型。
/// </summary>
public sealed class CompactionPolicyTests
{
    private static readonly CompactionPolicy Global = AgentDocs.DefaultCompactionPolicy with
    {
        Enabled = false,
        ReserveTokens = 100,
        KeepRecentTokens = 10,
        BackgroundTokens = 5,
        ModelOverrides = new Dictionary<string, CompactionPolicyOverride>
        {
            ["anthropic/large"] = new() { ReserveTokens = 1000 },
            ["openai/small"] = new() { Enabled = true, BackgroundTokens = 0 },
        },
    };

    [Fact]
    public void AppliesOnlyTheFieldsTheOverrideSets()
    {
        var large = Global.ForModel(new ModelRef("anthropic", "large"));
        Assert.False(large.Enabled);
        Assert.Equal(1000, large.ReserveTokens);
        Assert.Equal(10, large.KeepRecentTokens);
        Assert.Equal(5, large.BackgroundTokens);

        var small = Global.ForModel(new ModelRef("openai", "small"));
        Assert.True(small.Enabled);
        Assert.Equal(100, small.ReserveTokens);
        // 0 是显式写下的值，不是「没写」，必须生效。
        Assert.Equal(0, small.BackgroundTokens);
    }

    [Fact]
    public void FallsBackToTheGlobalPolicyForUnknownOrMissingModels()
    {
        Assert.Same(Global, Global.ForModel(new ModelRef("anthropic", "unknown")));
        Assert.Same(Global, Global.ForModel(null));
        Assert.Same(
            AgentDocs.DefaultCompactionPolicy,
            AgentDocs.DefaultCompactionPolicy.ForModel(new ModelRef("any", "model")));
    }

    [Fact]
    public void MatchesTheExactProviderSlashModelIdOnly()
    {
        Assert.Same(Global, Global.ForModel(new ModelRef("anthropic", "large:1")));
        Assert.Same(Global, Global.ForModel(new ModelRef("Anthropic", "large")));
    }
}
