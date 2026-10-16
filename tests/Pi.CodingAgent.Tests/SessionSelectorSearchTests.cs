// ============================================================================
// Tests for modes/interactive/components/session-selector-search.ts
// Vectors transcribed from TS test/session-selector-search.test.ts, plus the
// parse-level behaviour the TS suite leaves uncovered (quote handling, the
// `re:` error paths, and the threaded/relevance branch sharing).
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive.Components;
using Xunit;

namespace Pi.CodingAgent.Tests;

public class SessionSelectorSearchTests
{
    private static SessionInfo MakeSession(
        string id,
        DateTimeOffset modified,
        string allMessagesText,
        string? name = null,
        string cwd = "",
        DateTimeOffset? created = null,
        int messageCount = 1,
        string firstMessage = "(no messages)")
    {
        return new SessionInfo
        {
            Path = $"/tmp/{id}.jsonl",
            Id = id,
            Cwd = cwd,
            Name = name,
            Created = created ?? DateTimeOffset.UnixEpoch,
            Modified = modified,
            MessageCount = messageCount,
            FirstMessage = firstMessage,
            AllMessagesText = allMessagesText,
        };
    }

    private static DateTimeOffset Utc(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- filtering

    [Fact]
    public void FiltersByQuotedPhraseWithWhitespaceNormalization()
    {
        var sessions = new[]
        {
            MakeSession("a", Utc(2026, 1, 1), "node\n\n   cve was discussed"),
            MakeSession("b", Utc(2026, 1, 2), "node something else"),
        };

        var result = SessionSelectorSearch.FilterAndSortSessions(sessions, "\"node cve\"", SortMode.Recent);

        Assert.Equal(new[] { "a" }, result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void FiltersByRegexCaseInsensitively()
    {
        var sessions = new[]
        {
            MakeSession("a", Utc(2026, 1, 2), "Brave is great"),
            MakeSession("b", Utc(2026, 1, 3), "bravery is not the same"),
        };

        var result = SessionSelectorSearch.FilterAndSortSessions(sessions, @"re:\bbrave\b", SortMode.Recent);

        Assert.Equal(new[] { "a" }, result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void RecentSortPreservesInputOrder()
    {
        var sessions = new[]
        {
            MakeSession("newer", Utc(2026, 1, 3), "brave"),
            MakeSession("older", Utc(2026, 1, 1), "brave"),
            MakeSession("nomatch", Utc(2026, 1, 4), "something else"),
        };

        var result = SessionSelectorSearch.FilterAndSortSessions(sessions, "\"brave\"", SortMode.Recent);

        Assert.Equal(new[] { "newer", "older" }, result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void RelevanceSortOrdersByScoreAndTieBreaksByModifiedDesc()
    {
        var sessions = new[]
        {
            MakeSession("late", Utc(2026, 1, 3), "xxxx brave"),
            MakeSession("early", Utc(2026, 1, 1), "brave xxxx"),
        };

        var result1 = SessionSelectorSearch.FilterAndSortSessions(sessions, "\"brave\"", SortMode.Relevance);

        Assert.Equal(new[] { "early", "late" }, result1.Select(s => s.Id).ToArray());

        var tieSessions = new[]
        {
            MakeSession("newer", Utc(2026, 1, 3), "brave"),
            MakeSession("older", Utc(2026, 1, 1), "brave"),
        };

        var result2 = SessionSelectorSearch.FilterAndSortSessions(tieSessions, "\"brave\"", SortMode.Relevance);

        Assert.Equal(new[] { "newer", "older" }, result2.Select(s => s.Id).ToArray());
    }

    /// <summary>
    /// Upstream only special-cases <c>recent</c>, so <c>threaded</c> falls through to the scored
    /// branch and behaves exactly like <c>relevance</c>. Locked here so the port stays faithful.
    /// </summary>
    [Fact]
    public void ThreadedSortSharesTheRelevanceBranch()
    {
        var sessions = new[]
        {
            MakeSession("late", Utc(2026, 1, 3), "xxxx brave"),
            MakeSession("early", Utc(2026, 1, 1), "brave xxxx"),
        };

        var result = SessionSelectorSearch.FilterAndSortSessions(sessions, "\"brave\"", SortMode.Threaded);

        Assert.Equal(new[] { "early", "late" }, result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void ReturnsEmptyListForInvalidRegex()
    {
        var sessions = new[]
        {
            MakeSession("a", Utc(2026, 1, 1), "brave"),
        };

        var result = SessionSelectorSearch.FilterAndSortSessions(sessions, "re:(", SortMode.Recent);

        Assert.Empty(result);
    }

    // -------------------------------------------------------------- name filter

    private static SessionInfo[] NameFilterSessions() =>
    [
        MakeSession("named1", Utc(2026, 1, 3), "blueberry", name: "My Project"),
        MakeSession("named2", Utc(2026, 1, 2), "blueberry", name: "Another Named"),
        MakeSession("other1", Utc(2026, 1, 4), "blueberry"),
        MakeSession("other2", Utc(2026, 1, 1), "blueberry"),
    ];

    [Fact]
    public void ReturnsAllSessionsWhenNameFilterIsAll()
    {
        var result = SessionSelectorSearch.FilterAndSortSessions(
            NameFilterSessions(), "", SortMode.Recent, NameFilter.All);

        Assert.Equal(new[] { "named1", "named2", "other1", "other2" }, result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void ReturnsOnlyNamedSessionsWhenNameFilterIsNamed()
    {
        var result = SessionSelectorSearch.FilterAndSortSessions(
            NameFilterSessions(), "", SortMode.Recent, NameFilter.Named);

        Assert.Equal(new[] { "named1", "named2" }, result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void AppliesNameFilterBeforeSearchQuery()
    {
        var result = SessionSelectorSearch.FilterAndSortSessions(
            NameFilterSessions(), "blueberry", SortMode.Recent, NameFilter.Named);

        Assert.Equal(new[] { "named1", "named2" }, result.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void ExcludesWhitespaceOnlyNamesFromNamedFilter()
    {
        var sessions = new[]
        {
            MakeSession("whitespace", Utc(2026, 1, 1), "test", name: "   "),
            MakeSession("empty", Utc(2026, 1, 2), "test", name: ""),
            MakeSession("named", Utc(2026, 1, 3), "test", name: "Real Name"),
        };

        var result = SessionSelectorSearch.FilterAndSortSessions(sessions, "", SortMode.Recent, NameFilter.Named);

        Assert.Equal(new[] { "named" }, result.Select(s => s.Id).ToArray());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\t\n", false)]
    [InlineData("x", true)]
    [InlineData("  named  ", true)]
    public void HasSessionNameIgnoresWhitespaceOnlyNames(string? name, bool expected)
    {
        var session = MakeSession("id", Utc(2026, 1, 1), "text", name: name);

        Assert.Equal(expected, SessionSelectorSearch.HasSessionName(session));
    }

    /// <summary>
    /// The TS version hands back the caller's own array when nothing filtered; the port always builds
    /// a new list (documented divergence in the porting notes). Locked so the copy is not optimised
    /// away later.
    /// </summary>
    [Fact]
    public void EmptyQueryReturnsADetachedList()
    {
        var input = new List<SessionInfo>(NameFilterSessions());

        var result = SessionSelectorSearch.FilterAndSortSessions(input, "", SortMode.Recent);

        Assert.NotSame(input, result);
        result.Clear();
        Assert.Equal(4, input.Count);
    }

    // ------------------------------------------------------------ parseSearchQuery

    [Fact]
    public void ParseSearchQueryTreatsBlankQueryAsEmptyTokenList()
    {
        foreach (var query in new[] { "", "   ", "\t\n" })
        {
            var parsed = SessionSelectorSearch.ParseSearchQuery(query);

            Assert.Equal(SearchQueryMode.Tokens, parsed.Mode);
            Assert.Empty(parsed.Tokens);
            Assert.Null(parsed.Regex);
            Assert.Null(parsed.Error);
        }
    }

    [Fact]
    public void ParseSearchQuerySplitsWhitespaceSeparatedFuzzyTokens()
    {
        var parsed = SessionSelectorSearch.ParseSearchQuery("  foo   bar  ");

        Assert.Equal(SearchQueryMode.Tokens, parsed.Mode);
        Assert.Equal(
            new[] { new SearchToken(SearchTokenKind.Fuzzy, "foo"), new SearchToken(SearchTokenKind.Fuzzy, "bar") },
            parsed.Tokens.ToArray());
    }

    [Fact]
    public void ParseSearchQueryCollectsQuotedPhraseAsASinglePhraseToken()
    {
        var parsed = SessionSelectorSearch.ParseSearchQuery("foo \"node cve\" bar");

        Assert.Equal(SearchQueryMode.Tokens, parsed.Mode);
        Assert.Equal(
            new[]
            {
                new SearchToken(SearchTokenKind.Fuzzy, "foo"),
                new SearchToken(SearchTokenKind.Phrase, "node cve"),
                new SearchToken(SearchTokenKind.Fuzzy, "bar"),
            },
            parsed.Tokens.ToArray());
    }

    /// <summary>
    /// An unbalanced quote discards the whole parse and falls back to plain whitespace tokenization —
    /// including the stray quote character, which stays part of the first token.
    /// </summary>
    [Fact]
    public void ParseSearchQueryFallsBackToWhitespaceTokensOnUnbalancedQuote()
    {
        var parsed = SessionSelectorSearch.ParseSearchQuery("\"foo bar");

        Assert.Equal(SearchQueryMode.Tokens, parsed.Mode);
        Assert.Equal(
            new[] { new SearchToken(SearchTokenKind.Fuzzy, "\"foo"), new SearchToken(SearchTokenKind.Fuzzy, "bar") },
            parsed.Tokens.ToArray());
    }

    [Fact]
    public void ParseSearchQueryReportsEmptyRegex()
    {
        var parsed = SessionSelectorSearch.ParseSearchQuery("re:");

        Assert.Equal(SearchQueryMode.Regex, parsed.Mode);
        Assert.Empty(parsed.Tokens);
        Assert.Null(parsed.Regex);
        Assert.Equal("Empty regex", parsed.Error);
    }

    [Fact]
    public void ParseSearchQueryReportsInvalidRegex()
    {
        var parsed = SessionSelectorSearch.ParseSearchQuery("re:(");

        Assert.Equal(SearchQueryMode.Regex, parsed.Mode);
        Assert.Empty(parsed.Tokens);
        Assert.Null(parsed.Regex);
        Assert.False(string.IsNullOrEmpty(parsed.Error));
    }

    [Fact]
    public void ParseSearchQueryBuildsCaseInsensitiveRegex()
    {
        var parsed = SessionSelectorSearch.ParseSearchQuery("re:^a");

        Assert.Equal(SearchQueryMode.Regex, parsed.Mode);
        Assert.NotNull(parsed.Regex);
        Assert.Null(parsed.Error);
        Assert.Matches(parsed.Regex, "Abc");
        Assert.DoesNotMatch(parsed.Regex, "bcA");
    }

    // --------------------------------------------------------------- matchSession

    [Fact]
    public void MatchSessionScoresRegexHitsByMatchIndex()
    {
        // The haystack is "<id> <name> <allMessagesText> <cwd>", so "brave" lands at index 9.
        var session = MakeSession("id", Utc(2026, 1, 1), "xxxx brave");
        var parsed = SessionSelectorSearch.ParseSearchQuery("re:brave");

        var result = SessionSelectorSearch.MatchSession(session, parsed);

        Assert.True(result.Matches);
        Assert.Equal(9 * 0.1, result.Score, 6);
    }

    [Fact]
    public void MatchSessionTreatsErrorQueryAsNonMatching()
    {
        var session = MakeSession("id", Utc(2026, 1, 1), "brave");
        var parsed = SessionSelectorSearch.ParseSearchQuery("re:(");

        var result = SessionSelectorSearch.MatchSession(session, parsed);

        Assert.False(result.Matches);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void MatchSessionRequiresEveryTokenToMatch()
    {
        var session = MakeSession("id", Utc(2026, 1, 1), "brave new world");
        var parsed = SessionSelectorSearch.ParseSearchQuery("brave \"new world\"");

        Assert.True(SessionSelectorSearch.MatchSession(session, parsed).Matches);

        var missing = SessionSelectorSearch.ParseSearchQuery("brave \"old world\"");
        Assert.False(SessionSelectorSearch.MatchSession(session, missing).Matches);
    }
}
