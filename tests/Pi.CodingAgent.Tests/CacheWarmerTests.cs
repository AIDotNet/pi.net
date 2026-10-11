using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class CacheWarmerTests
{
    [Fact]
    public void GetCacheWarmingDelayMs_UsesNinetyPercentWithTenSecondBuffer()
    {
        Assert.Null(CacheWarmerMath.GetCacheWarmingDelayMs(10_000));
        Assert.Equal(90_000, CacheWarmerMath.GetCacheWarmingDelayMs(100_000));
        Assert.Equal(3_240_000, CacheWarmerMath.GetCacheWarmingDelayMs(3_600_000));
    }

    [Fact]
    public void GetPromptCacheTtlMs_RequiresRetention()
    {
        var model = Spec("openai-completions", promptCache: new JsonObject { ["short"] = 300, ["long"] = 3600 });
        Assert.Null(CacheWarmerMath.GetPromptCacheTtlMs(model, new SimpleStreamOptions()));
        Assert.Equal(300_000, CacheWarmerMath.GetPromptCacheTtlMs(model, new SimpleStreamOptions { CacheRetention = "short" }));
        Assert.Equal(3_600_000, CacheWarmerMath.GetPromptCacheTtlMs(model, new SimpleStreamOptions { CacheRetention = "long" }));
        Assert.Null(CacheWarmerMath.GetPromptCacheTtlMs(model, new SimpleStreamOptions { CacheRetention = "none" }));
    }

    [Fact]
    public void IsReplayable_BlocksAnthropicReasoningWithoutAdaptiveThinking()
    {
        var anthropic = Spec("anthropic-messages");
        Assert.True(CacheWarmerMath.IsReplayable(anthropic, new SimpleStreamOptions()));
        Assert.False(CacheWarmerMath.IsReplayable(anthropic, new SimpleStreamOptions { Reasoning = "high" }));
        var adaptive = Spec("anthropic-messages", compat: new JsonObject { ["forceAdaptiveThinking"] = true });
        Assert.True(CacheWarmerMath.IsReplayable(adaptive, new SimpleStreamOptions { Reasoning = "high" }));
    }

    [Fact]
    public void FormatCacheWarmingStatus_InactiveWithoutEconomics()
    {
        var text = CacheWarmerMath.FormatCacheWarmingStatus(new CacheWarmingStatus
        {
            State = "inactive",
            Reason = "cache warming disabled",
        });
        Assert.Equal("Inactive (cache warming disabled)", text);
    }

    [Fact]
    public void FormatCacheWarmingUsage_IncludesNoteAndCost()
    {
        var usage = new Usage(1, 0, 0, 0) { Cost = new UsageCost(0, 0, 0, 0, 0.012345) };
        var text = CacheWarmerMath.FormatCacheWarmingUsage(new UsageEntry
        {
            Kind = "cache_warm",
            Timestamp = "1",
            Model = "m",
            Provider = "p",
            Usage = usage,
            Note = "cache warming",
        });
        Assert.Contains("Cache warmed (cache warming)", text);
        Assert.Contains("$0.012345", text);
    }

    [Fact]
    public void Start_StopsWhenModeOff()
    {
        var warmer = new CacheWarmer(() => "off");
        warmer.Start(new CacheWarmRequest { Model = Spec("openai-completions"), Options = new SimpleStreamOptions() }, () => true);
        Assert.Equal("inactive", warmer.Status.State);
        Assert.Equal("cache warming disabled", warmer.Status.Reason);
    }

    [Fact]
    public void Start_StopsWhenNotReplayable()
    {
        var warmer = new CacheWarmer(() => "always");
        warmer.Start(
            new CacheWarmRequest
            {
                Model = Spec("anthropic-messages"),
                Options = new SimpleStreamOptions { Reasoning = "high" },
            },
            () => true);
        Assert.Equal("request cannot be replayed safely", warmer.Status.Reason);
    }

    private static ModelSpec Spec(string api, JsonObject? promptCache = null, JsonObject? compat = null) =>
        new()
        {
            Id = "m",
            Name = "m",
            Api = api,
            Provider = "p",
            BaseUrl = "https://example.test",
            Compat = compat,
            Extra = promptCache is null ? null : new JsonObject { ["promptCache"] = promptCache },
        };
}
