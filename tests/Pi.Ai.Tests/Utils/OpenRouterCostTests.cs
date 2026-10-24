using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Utils;

/// <summary>
/// OpenRouter 上报成本。对应上游 <c>test/openrouter-cost.test.ts</c>：
/// 非 BYOK 直接用上报值，BYOK 再加下游推理成本，账目不全时保持目录价。
/// </summary>
public class OpenRouterCostTests
{
    private static Usage Sample(double total = 1)
        => new(1, 1, 0, 0) { TotalTokens = 2, Cost = new UsageCost(0.25, 0.75, 0, 0, total) };

    private static ModelSpec Model(string provider = "openrouter", string baseUrl = "https://openrouter.ai/api/v1")
        => new()
        {
            Id = "m", Name = "M", Api = "openai-completions", Provider = provider, BaseUrl = baseUrl,
            Cost = new ModelCostRates(1000, 2000),
        };

    [Fact]
    public void RecognizesBuiltInAndCustomOpenRouterEndpoints()
    {
        Assert.True(OpenRouterCost.IsOpenRouterModel(Model("openrouter", "https://example.com")));
        Assert.True(OpenRouterCost.IsOpenRouterModel(Model("openrouter-work", "https://openrouter.ai/api/v1")));
        Assert.False(OpenRouterCost.IsOpenRouterModel(Model("custom", "https://example.com/v1")));
    }

    [Fact]
    public void NonByokUsesTheReportedCost()
    {
        var usage = Sample();

        var result = OpenRouterCost.ApplyReportedCost(usage, new JsonObject
        {
            ["cost"] = 0,
            ["is_byok"] = false,
            ["cost_details"] = new JsonObject
            {
                ["upstream_inference_cost"] = 0,
                ["upstream_inference_prompt_cost"] = 0,
                ["upstream_inference_completions_cost"] = 0,
            },
        });

        Assert.Equal(0, result.Cost.Total);
        Assert.True(result.Cost.TotalFromProvider);
    }

    [Fact]
    public void ByokAddsTheUpstreamInferenceCost()
    {
        var result = OpenRouterCost.ApplyReportedCost(Sample(), new JsonObject
        {
            ["cost"] = 0.1,
            ["is_byok"] = true,
            ["cost_details"] = new JsonObject
            {
                ["upstream_inference_cost"] = 0.9,
                ["upstream_inference_prompt_cost"] = 0.6,
                ["upstream_inference_completions_cost"] = 0.3,
            },
        });

        Assert.Equal(1, result.Cost.Total, 12);
        Assert.True(result.Cost.TotalFromProvider);
    }

    [Fact]
    public void ByokFallsBackToTheUpstreamComponents()
    {
        var result = OpenRouterCost.ApplyReportedCost(Sample(), new JsonObject
        {
            ["cost"] = 0.1,
            ["is_byok"] = true,
            ["cost_details"] = new JsonObject
            {
                ["upstream_inference_cost"] = null,
                ["upstream_inference_prompt_cost"] = 0.6,
                ["upstream_inference_completions_cost"] = 0.3,
            },
        });

        Assert.Equal(1, result.Cost.Total, 12);
        Assert.True(result.Cost.TotalFromProvider);
    }

    [Fact]
    public void IncompleteAccountingKeepsTheCatalogTotal()
    {
        var usage = Sample();
        Assert.False(usage.Cost.TotalFromProvider ?? false);

        Assert.Equal(1, OpenRouterCost.ApplyReportedCost(usage, new JsonObject
        {
            ["cost"] = null,
            ["is_byok"] = false,
            ["cost_details"] = null,
        }).Cost.Total);

        var afterMissingDetails = OpenRouterCost.ApplyReportedCost(usage, new JsonObject
        {
            ["cost"] = 0.1,
            ["is_byok"] = true,
            ["cost_details"] = null,
        });
        Assert.Equal(1, afterMissingDetails.Cost.Total);
        Assert.False(afterMissingDetails.Cost.TotalFromProvider ?? false);
    }

    [Fact]
    public void MissingByokFlagKeepsTheCatalogTotal()
    {
        var result = OpenRouterCost.ApplyReportedCost(Sample(), new JsonObject { ["cost"] = 0.1 });

        Assert.Equal(1, result.Cost.Total);
        Assert.False(result.Cost.TotalFromProvider ?? false);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidCostKeepsTheCatalogTotal(double reported)
    {
        var result = OpenRouterCost.ApplyReportedCost(Sample(), new JsonObject
        {
            ["cost"] = reported,
            ["is_byok"] = false,
        });

        Assert.Equal(1, result.Cost.Total);
        Assert.False(result.Cost.TotalFromProvider ?? false);
    }

    /// <summary>目录价重算会把「来自 provider」的标记重置为 false。</summary>
    [Fact]
    public void CatalogRepricingClearsTheProviderFlag()
    {
        var reported = Sample() with
        {
            Cost = new UsageCost(0, 0, 0, 0, 9) { TotalFromProvider = true },
        };

        var costed = ModelOperations.CalculateCost(Model(), reported);

        Assert.False(costed.Cost.TotalFromProvider);
        // 1 token 输入 × 1000/1M + 1 token 输出 × 2000/1M。
        Assert.Equal(0.003, costed.Cost.Total, 12);
    }

    /// <summary>接入点：openai-completions 的 usage 解析对 OpenRouter 模型应用上报成本。</summary>
    [Fact]
    public void ChunkUsageAppliesReportedCostForOpenRouter()
    {
        var usage = OpenAiCompletions.ParseChunkUsage(new JsonObject
        {
            ["prompt_tokens"] = 10,
            ["completion_tokens"] = 5,
            ["cost"] = 0.0042,
            ["is_byok"] = false,
        }, Model());

        Assert.Equal(0.0042, usage.Cost.Total, 12);
        Assert.True(usage.Cost.TotalFromProvider);
    }

    /// <summary>非 OpenRouter 模型即使带同名成本字段也不改总价。</summary>
    [Fact]
    public void ChunkUsageIgnoresReportedCostForOtherProviders()
    {
        var usage = OpenAiCompletions.ParseChunkUsage(new JsonObject
        {
            ["prompt_tokens"] = 10,
            ["completion_tokens"] = 5,
            ["cost"] = 0.0042,
            ["is_byok"] = false,
        }, Model("deepseek", "https://api.deepseek.com"));

        Assert.NotEqual(0.0042, usage.Cost.Total);
        Assert.False(usage.Cost.TotalFromProvider);
    }
}
