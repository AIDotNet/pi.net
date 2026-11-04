using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Port of <c>packages/coding-agent/test/model-search.test.ts</c>（上游 <c>8c03fef4d</c>）：
/// 选择器把「查询作为一个连续片段出现」的模型排在「散落在 provider 前缀里」的模型之前。
/// </summary>
public class ModelSearchRankingTests
{
    private static ModelSearchItem Model(string provider, string id, string? name = null) =>
        new() { Provider = provider, Id = id, Name = name };

    private static List<string> Rank(IEnumerable<ModelSearchItem> models, string query) =>
        [.. Fuzzy.Filter(models, query, ModelSearch.GetModelSelectorSearchText).Select(model => model.Id)];

    [Fact]
    public void RanksModelsContainingTheQueryAboveScatteredMatches()
    {
        // #11220
        ModelSearchItem[] models =
        [
            Model("openrouter", "openai/gpt-oss-120b", "OpenAI: gpt-oss-120b"),
            Model("openrouter", "anthropic/claude-sonnet-4.5", "Anthropic: Claude Sonnet 4.5"),
            Model("openrouter", "anthropic/claude-opus-4.1", "Anthropic: Claude Opus 4.1"),
            Model("anthropic", "claude-sonnet-4-5", "Claude Sonnet 4.5"),
            Model("anthropic", "claude-opus-4-1", "Claude Opus 4.1"),
        ];

        var top = Rank(models, "opus").Take(2).OrderBy(id => id, StringComparer.Ordinal);

        Assert.Equal(["anthropic/claude-opus-4.1", "claude-opus-4-1"], top);
    }

    [Fact]
    public void RanksTheTypedModelAboveModelsSharingALongProviderPrefix()
    {
        // #10065
        ModelSearchItem[] models =
        [
            Model("fireworks", "accounts/fireworks/models/deepseek-v4-flash-0731"),
            Model("fireworks", "accounts/fireworks/models/kimi-k3"),
            Model("fireworks", "accounts/fireworks/routers/firerouter", "FireRouter"),
        ];

        Assert.Equal("accounts/fireworks/routers/firerouter", Rank(models, "firerouter")[0]);
    }
}
