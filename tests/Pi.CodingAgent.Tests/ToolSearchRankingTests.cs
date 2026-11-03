using Pi.CodingAgent.Extensions.ToolSearch;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tool search ranking rules introduced by TS <c>cc44fa216</c> / <c>e033f255f</c> / <c>ae823bc0b</c> /
/// <c>79006f061</c>. The score-level parity with the TypeScript implementation lives in
/// <see cref="ToolSearchCorpusTests"/> (regenerated corpus); these pin the rules the corpus only implies,
/// plus the CJK segmentation the port has to approximate.
/// </summary>
public class ToolSearchRankingTests
{
    private static IReadOnlyList<string> Rank(string query, params ToolSearchDocument[] documents) =>
        [.. new ToolSearch.Bm25Ranker().Rank(query, documents, 8).Select(match => match.Name)];

    [Fact]
    public void ANameTermOutranksAnyNumberOfDescriptionMentions()
    {
        // `NAME_WEIGHT` (3) beats the text ceiling (k1 + 1 = 2.2) per term.
        var documents = new[]
        {
            new ToolSearchDocument("grep", "search search search search search file contents"),
            new ToolSearchDocument("search", "query the index"),
        };

        Assert.Equal(["search", "grep"], Rank("search", documents));
    }

    [Fact]
    public void MatchingMoreQueryTermsRanksFirstWhateverTheScores()
    {
        // `window` alone scores higher in a short description, but the tool covering both terms wins.
        var documents = new[]
        {
            new ToolSearchDocument("window", "window"),
            new ToolSearchDocument(
                "screenshot",
                "Capture a screenshot of a window and save it to disk with a long description for weighting"),
        };

        Assert.Equal(["screenshot", "window"], Rank("screenshot window", documents));
    }

    [Fact]
    public void TheWholeLocalNameInTheQueryAddsABonus()
    {
        // Both tools cover `peekaboo` and `app`, but only `mcp__peekaboo__app` has its whole local name
        // (`app`) in the query, so it earns one more match worth of score and wins.
        var documents = new[]
        {
            new ToolSearchDocument("mcp__peekaboo__app_launch", "focus"),
            new ToolSearchDocument("mcp__peekaboo__app", "focus"),
        };

        Assert.Equal(["mcp__peekaboo__app", "mcp__peekaboo__app_launch"], Rank("peekaboo app", documents));
    }

    [Fact]
    public void NamespaceTermsCountLessThanTheToolsOwnFields()
    {
        var withNamespace = new[]
        {
            new ToolSearchDocument("read", "read a file", "mcp__docs__ Server tools documentation"),
            new ToolSearchDocument("docs_search", "search the docs"),
        };
        Assert.Equal(["docs_search", "read"], Rank("docs", withNamespace));

        // A namespace-only hit still matches, just weakly.
        Assert.Equal(["read"], Rank("server", withNamespace));
    }

    [Fact]
    public void OtherScriptsKeepWholeWordsAndAreNotStemmed()
    {
        // TS `[^\p{L}\p{M}\p{N}]` keeps accented letters, and only ASCII words are stemmed.
        Assert.Equal(["café"], ToolSearch.Tokenize("café"));
        Assert.Equal(["cafés"], ToolSearch.Tokenize("cafés"));
    }

    [Fact]
    public void ChineseIsSplitIntoOverlappingBigrams()
    {
        // `Intl.Segmenter` has no .NET equivalent; the port uses bigrams, which match the same queries.
        // Difference C391.
        Assert.Equal(["工具", "搜索"], ToolSearch.Tokenize("工具 搜索"));
        Assert.Equal(["搜索", "索文", "文件"], ToolSearch.Tokenize("搜索文件"));
        Assert.Equal(["搜"], ToolSearch.Tokenize("搜"));
    }

    [Fact]
    public void AChineseQueryFindsAChineseDescription()
    {
        var documents = new[]
        {
            new ToolSearchDocument("read", "Read a file"),
            new ToolSearchDocument("grep", "搜索文件内容"),
        };

        Assert.Equal(["grep"], Rank("搜索", documents));
        Assert.Equal(["grep"], Rank("文件", documents));
    }
}
