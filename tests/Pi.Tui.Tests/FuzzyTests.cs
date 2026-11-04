using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

/// <summary>
/// Port of <c>packages/tui/test/fuzzy.test.ts</c>（8 个 <c>fuzzyMatch</c> + 6 个 <c>fuzzyFilter</c> 用例），
/// 并补上 TS 测试没写死的**具体分值**——分值直接由 TS 实现在 Node 里跑出（非手算回抄），
/// 这样 C# 侧任何一处系数/顺序漂移都会立刻被钉住。
/// </summary>
public class FuzzyTests
{
    // ===== fuzzyMatch =====

    [Fact]
    public void EmptyQueryMatchesEverythingWithScoreZero()
    {
        var result = Fuzzy.Match("", "anything");
        Assert.True(result.Matches);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void QueryLongerThanTextDoesNotMatch()
    {
        var result = Fuzzy.Match("longquery", "short");
        Assert.False(result.Matches);
        // TS 在「长度超限」分支返回 { matches: false, score: 0 }——不是 NaN 也不是 -1。
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void ExactMatchHasNegativeScore()
    {
        var result = Fuzzy.Match("test", "test");
        Assert.True(result.Matches);
        Assert.True(result.Score < 0);
        // 首字符也算「连续命中」（lastMatchIndex == i-1 在 -1/0 时成立），故 4 个字符拿满 5+10+15+20。
        Assert.Equal(-159.4, result.Score, 6);
    }

    [Fact]
    public void CharactersMustAppearInOrder()
    {
        var matchInOrder = Fuzzy.Match("abc", "aXbXc");
        Assert.True(matchInOrder.Matches);
        Assert.Equal(-10.4, matchInOrder.Score, 6);

        var matchOutOfOrder = Fuzzy.Match("abc", "cba");
        Assert.False(matchOutOfOrder.Matches);
    }

    [Fact]
    public void MatchingIsCaseInsensitiveBothWays()
    {
        var result = Fuzzy.Match("ABC", "abc");
        Assert.True(result.Matches);
        Assert.Equal(-139.7, result.Score, 6);

        var result2 = Fuzzy.Match("abc", "ABC");
        Assert.True(result2.Matches);
        Assert.Equal(-139.7, result2.Score, 6);
    }

    [Fact]
    public void ConsecutiveMatchesScoreBetterThanScattered()
    {
        var consecutive = Fuzzy.Match("foo", "foobar");
        var scattered = Fuzzy.Match("foo", "f_o_o_bar");

        Assert.True(consecutive.Matches);
        Assert.True(scattered.Matches);
        Assert.True(consecutive.Score < scattered.Score);
        Assert.Equal(-39.7, consecutive.Score, 6);
        Assert.Equal(-30.4, scattered.Score, 6);
    }

    [Fact]
    public void WordBoundaryMatchesScoreBetter()
    {
        var atBoundary = Fuzzy.Match("fb", "foo-bar");
        var notAtBoundary = Fuzzy.Match("fb", "afbx");

        Assert.True(atBoundary.Matches);
        Assert.True(notAtBoundary.Matches);
        Assert.True(atBoundary.Score < notAtBoundary.Score);
        Assert.Equal(-18.6, atBoundary.Score, 6);
        Assert.Equal(-4.7, notAtBoundary.Score, 6);
    }

    [Fact]
    public void ScoresContiguousOccurrenceInsteadOfEarlierScatteredAlignment()
    {
        // #11220：贪心对齐从 "openrouter" 里取了 o/p/u，真正的 "opus" 反而排到最后。
        var opus = Fuzzy.Match("opus", "openrouter openrouter/anthropic/claude-opus-4.1");
        var sonnet = Fuzzy.Match("opus", "openrouter openrouter/anthropic/claude-sonnet-4.5");
        var gptOss = Fuzzy.Match("opus", "openrouter openrouter/openai/gpt-oss-120b");

        Assert.True(opus.Matches);
        Assert.True(opus.Score < sonnet.Score);
        Assert.True(opus.Score < gptOss.Score);

        // 分值由上游 TS 实跑取得。
        Assert.Equal(-23.8, opus.Score, 6);
        Assert.Equal(41.6, sonnet.Score, 6);
        Assert.Equal(41.1, gptOss.Score, 6);
    }

    [Fact]
    public void UsesBestContiguousOccurrenceNotTheFirst()
    {
        // "xxopus opus" 里第一段连续出现嵌在词中（无词边界加分），要与 "xxxxxx opus" 同分——
        // 说明取的是**最优**连续出现，而不是第一段。
        var embeddedFirst = Fuzzy.Match("opus", "xxopus opus");
        var boundaryOnly = Fuzzy.Match("opus", "xxxxxx opus");

        Assert.Equal(boundaryOnly.Score, embeddedFirst.Score, 6);
        Assert.Equal(-36.6, boundaryOnly.Score, 6);
    }

    [Fact]
    public void MatchesSwappedAlphaNumericTokens()
    {
        var result = Fuzzy.Match("codex52", "gpt-5.2-codex");
        Assert.True(result.Matches);
        // "codex52" 直配失败（'x' 之后已无 '5'），换位成 "52codex" 才命中。
        Assert.Equal(-65, result.Score, 6);
    }

    [Fact]
    public void OrdinarySubsequenceMatches()
    {
        Assert.Equal(-14.4, Fuzzy.Match("hw", "hello world").Score, 6);
        Assert.Equal(-10.4, Fuzzy.Match("hlo", "hello world").Score, 6);
        Assert.Equal(-444.5, Fuzzy.Match("hello world", "hello world").Score, 6);
        Assert.False(Fuzzy.Match("xyz", "hello").Matches);
    }

    [Fact]
    public void SwappedQueryScoreIsPrimaryScorePlusFive()
    {
        // 换位分支的返回是 `{ matches: true, score: swapped.score + 5 }`——+5 是「顺序换位」的罚分。
        // 用同一文本上「换位串直配」的分值做锚，避免把 -65 当成魔法数。
        var swappedDirectly = Fuzzy.Match("52codex", "gpt-5.2-codex");
        Assert.True(swappedDirectly.Matches);
        Assert.Equal(-70, swappedDirectly.Score, 6);
        Assert.Equal(swappedDirectly.Score + 5, Fuzzy.Match("codex52", "gpt-5.2-codex").Score, 6);
    }

    [Fact]
    public void NonSwappableQueryFallsBackToPrimaryResult()
    {
        // "5.2codex" 不满足 /^[a-z]+[0-9]+$/ 也不满足 /^[0-9]+[a-z]+$/，换位串为空 →
        // 直接返回主匹配结果（这里是命中，走的是普通子序列）。
        var result = Fuzzy.Match("5.2codex", "gpt-5.2-codex");
        Assert.True(result.Matches);
        Assert.Equal(-86.5, result.Score, 6);
    }

    // ===== fuzzyFilter =====

    [Fact]
    public void EmptyQueryReturnsAllItemsUnchanged()
    {
        var items = new[] { "apple", "banana", "cherry" };
        var result = Fuzzy.Filter(items, "", x => x);
        Assert.Equal(items, result);
    }

    [Fact]
    public void WhitespaceOnlyQueryReturnsAllItemsUnchanged()
    {
        var items = new[] { "apple", "banana" };
        Assert.Equal(items, Fuzzy.Filter(items, "   ", x => x));
    }

    [Fact]
    public void FiltersOutNonMatchingItems()
    {
        var items = new[] { "apple", "banana", "cherry" };
        var result = Fuzzy.Filter(items, "an", x => x);
        Assert.Equal(new[] { "banana" }, result);
    }

    [Fact]
    public void SortsResultsByMatchQuality()
    {
        var items = new[] { "a_p_p", "app", "application" };
        var result = Fuzzy.Filter(items, "app", x => x);

        // 全等（-139.7）< 前缀连续（-39.7）< 散落但每段落词边界（-30.4）。
        Assert.Equal(new[] { "app", "application", "a_p_p" }, result);
    }

    [Fact]
    public void PrioritizesExactMatchesOverLongerPrefixMatches()
    {
        var items = new[] { "clone", "cl" };
        var result = Fuzzy.Filter(items, "cl", x => x);
        Assert.Equal(new[] { "cl", "clone" }, result);
    }

    [Fact]
    public void WorksWithCustomGetText()
    {
        var items = new[]
        {
            new ModelItem("foo", 1),
            new ModelItem("bar", 2),
            new ModelItem("foobar", 3),
        };
        var result = Fuzzy.Filter(items, "foo", item => item.Name);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Name == "foo");
        Assert.Contains(result, r => r.Name == "foobar");
    }

    [Fact]
    public void MatchesSlashSeparatedProviderModelQueries()
    {
        var item = "gpt-5.5 openai-codex";
        var result = Fuzzy.Filter(new[] { item }, "openai-codex/gpt-5.5", x => x);
        Assert.Equal(new[] { item }, result);
    }

    [Fact]
    public void AllTokensMustMatchAndScoresAccumulate()
    {
        var items = new[] { "hello world", "hello", "world hello" };
        Assert.Equal(new[] { "hello", "hello world", "world hello" }, Fuzzy.Filter(items, "hello", x => x));
        // "hello" 因缺 "world" token 被整体剔除。
        Assert.Equal(new[] { "hello world", "world hello" }, Fuzzy.Filter(items, "hello world", x => x));
    }

    [Fact]
    public void SlashAndWhitespaceAreBothTokenSeparators()
    {
        var items = new[] { "zlib", "alibaba", "a/b", "b a" };
        Assert.Equal(new[] { "a/b", "b a", "alibaba" }, Fuzzy.Filter(items, "a/b", x => x));
    }

    [Fact]
    public void RanksContiguousMatchAboveSharedProviderPrefix()
    {
        // #11220："radius radius/gpt" 里含 a,s,t,r,a 的顺序子序列，于是每个 radius 模型都命中，
        // 真正含 "astra" 的那个反而垫底。
        var items = new[]
        {
            "radius radius/gpt-5.4 radius gpt-5.4 GPT 5.4",
            "radius radius/agentic radius agentic Agentic",
            "radius radius/gpt-6-sol radius gpt-6-sol GPT 6 Sol",
            "radius radius/gpt-6-astra radius gpt-6-astra GPT 6 Astra",
        };
        var result = Fuzzy.Filter(items, "astra", x => x);

        Assert.Equal(new[] { items[3], items[0], items[1], items[2] }, result);
    }

    private sealed record ModelItem(string Name, int Id);
}
