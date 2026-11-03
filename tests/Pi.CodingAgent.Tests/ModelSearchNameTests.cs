using Pi.CodingAgent.Modes.Interactive;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Model selector display names (TS <c>606af8662</c>): a name is shown only when it adds words the ID
/// does not already have.
/// </summary>
public class ModelSearchNameTests
{
    [Fact]
    public void ReturnsNameWhenItAddsWords()
    {
        Assert.Equal(
            "MiniMax M3 [rasmus@openrouter, parasail]",
            ModelSearch.GetDistinctModelName("minimax/minimax-m3", "MiniMax M3 [rasmus@openrouter, parasail]"));
    }

    [Fact]
    public void ReturnsNullWhenNameOnlyRestatesTheId()
    {
        // Different separators and casing, same words.
        Assert.Null(ModelSearch.GetDistinctModelName("claude-sonnet-4-5", "Claude Sonnet 4.5"));
        Assert.Null(ModelSearch.GetDistinctModelName("gpt-5", "GPT-5"));
    }

    [Fact]
    public void ReturnsNullWhenNameDropsTheNamespaceAndVersionSuffix()
    {
        Assert.Null(ModelSearch.GetDistinctModelName(
            "eu.anthropic.claude-sonnet-4-5-20250929-v1:0",
            "Claude Sonnet 4.5 (EU)"));
    }

    [Fact]
    public void SingleCharacterWordsMustMatchAWholeIdToken()
    {
        // "4" is not covered by "mistral-small-2603", so the name is distinct.
        Assert.Equal("Mistral 4", ModelSearch.GetDistinctModelName("mistral-small-2603", "Mistral 4"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNullForMissingOrBlankName(string? name)
    {
        Assert.Null(ModelSearch.GetDistinctModelName("gpt-5", name));
    }
}
