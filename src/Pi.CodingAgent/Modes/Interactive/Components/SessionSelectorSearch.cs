// ============================================================================
// Session selector search: query parsing, matching and sorting.
// Port of modes/interactive/components/session-selector-search.ts
// ============================================================================

using System.Text.RegularExpressions;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>TS <c>SortMode</c>: how the session list is ordered once filtered.</summary>
public enum SortMode
{
    /// <summary>Threaded order (upstream falls through to the relevance branch, see below).</summary>
    Threaded,

    /// <summary>Keep the incoming order; filter only.</summary>
    Recent,

    /// <summary>Order by match score, tie-broken by <c>modified</c> descending.</summary>
    Relevance,
}

/// <summary>TS <c>NameFilter</c>: whether unnamed sessions take part at all.</summary>
public enum NameFilter
{
    All,
    Named,
}

/// <summary>TS <c>ParsedSearchQuery.mode</c>.</summary>
public enum SearchQueryMode
{
    Tokens,
    Regex,
}

/// <summary>TS <c>{ kind: "fuzzy" | "phrase" }</c>.</summary>
public enum SearchTokenKind
{
    Fuzzy,
    Phrase,
}

/// <summary>TS <c>{ kind: "fuzzy" | "phrase"; value: string }</c>.</summary>
public sealed record SearchToken(SearchTokenKind Kind, string Value);

/// <summary>TS <c>ParsedSearchQuery</c>: the result of <see cref="SessionSelectorSearch.ParseSearchQuery"/>.</summary>
public sealed record ParsedSearchQuery
{
    public required SearchQueryMode Mode { get; init; }

    public required IReadOnlyList<SearchToken> Tokens { get; init; }

    /// <summary>
    /// TS <c>regex</c>. Only set in <see cref="SearchQueryMode.Regex"/> mode, and <see langword="null"/>
    /// both when the pattern was empty and when it failed to compile.
    /// </summary>
    public Regex? Regex { get; init; }

    /// <summary>TS <c>error?</c>: when set, the query is treated as matching nothing.</summary>
    public string? Error { get; init; }
}

/// <summary>TS <c>MatchResult</c>: <c>Score</c> is only meaningful when <c>Matches</c> is true.</summary>
public readonly record struct MatchResult(bool Matches, double Score);

/// <summary>
/// Port of <c>session-selector-search.ts</c>: the pure query layer behind the session selector
/// (parse <c>foo "bar baz"</c> / <c>re:pattern</c> queries, score a session against them, then
/// filter and sort the list).
/// </summary>
public static partial class SessionSelectorSearch
{
    /// <summary>JS <c>/\s+/g</c>, in the JS whitespace set.</summary>
    [GeneratedRegex(JsRegex.WhitespaceClass + "+")]
    private static partial Regex WhitespaceRunRegex();

    /// <summary>Port of the private <c>normalizeWhitespaceLower</c>.</summary>
    private static string NormalizeWhitespaceLower(string text) =>
        JsString.Trim(WhitespaceRunRegex().Replace(JsString.ToLowerCase(text), " "));

    // Match tiers, best first. Each token is scored by the best field it matches; tiers dominate
    // fuzzy scores so an exact hit anywhere beats a scattered subsequence in a short field.
    // TS `8c03fef4d`.

    /// <summary>The query text appears in the session name.</summary>
    private const double TierName = 0;

    /// <summary>The query text appears in the session id, messages, or cwd.</summary>
    private const double TierText = 1;

    /// <summary>The query characters appear scattered in the session name or cwd.</summary>
    private const double TierFuzzy = 2;

    private const double TierWeight = 1_000_000;

    /// <summary>Lower-cased message text per session, computed at most once.</summary>
    /// <remarks>
    /// <c>AllMessagesText</c> holds every user/assistant message and can be megabytes, so it is
    /// lowercased once and cached on the session instead of on every keystroke.
    /// </remarks>
    private sealed class LowercaseCache
    {
        public string Source { get; init; } = "";

        public string Lower { get; init; } = "";

        public string? Normalized { get; set; }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SessionInfo, LowercaseCache>
        MessageTextCache = new();

    private static LowercaseCache GetMessageText(SessionInfo session)
    {
        var cache = MessageTextCache.GetOrCreateValue(session);
        if (cache.Source == session.AllMessagesText)
        {
            return cache;
        }

        // `GetOrCreateValue` needs the parameterless constructor; replace the entry when the text moved.
        var replacement = new LowercaseCache
        {
            Source = session.AllMessagesText,
            Lower = JsString.ToLowerCase(session.AllMessagesText),
        };
        MessageTextCache.Remove(session);
        MessageTextCache.Add(session, replacement);
        return replacement;
    }

    private static string GetNormalizedMessageText(SessionInfo session)
    {
        var cached = GetMessageText(session);
        cached.Normalized ??= NormalizeWhitespaceLower(cached.Lower);
        return cached.Normalized;
    }

    private static double TierScore(double tier, double score = 0) => (tier * TierWeight) + score;

    /// <summary>
    /// Exact (case-insensitive) text match. Messages and ids are only matched exactly: scattered
    /// matches in them are noise.
    /// </summary>
    private static double? MatchLiteralToken(SessionInfo session, string value)
    {
        var name = JsString.ToLowerCase(session.Name ?? "");
        if (name.Contains(value, StringComparison.Ordinal))
        {
            return TierScore(TierName, Fuzzy.Match(value, name).Score);
        }

        if (JsString.ToLowerCase(session.Id).Contains(value, StringComparison.Ordinal))
        {
            return TierScore(TierText);
        }

        if (GetMessageText(session).Lower.Contains(value, StringComparison.Ordinal))
        {
            return TierScore(TierText);
        }

        if (JsString.ToLowerCase(session.Cwd).Contains(value, StringComparison.Ordinal))
        {
            return TierScore(TierText);
        }

        return null;
    }

    private static double? MatchFuzzyToken(SessionInfo session, string value)
    {
        var literal = MatchLiteralToken(session, JsString.ToLowerCase(value));
        if (literal is { } literalScore)
        {
            return literalScore;
        }

        double? best = null;
        foreach (var field in new[] { session.Name ?? "", session.Cwd })
        {
            var match = Fuzzy.Match(value, field);
            if (match.Matches && (best is null || match.Score < best))
            {
                best = match.Score;
            }
        }

        return best is { } bestScore ? TierScore(TierFuzzy, bestScore) : null;
    }

    private static double? MatchPhraseToken(SessionInfo session, string phrase)
    {
        var name = NormalizeWhitespaceLower(session.Name ?? "");
        if (name.Contains(phrase, StringComparison.Ordinal))
        {
            return TierScore(TierName, Fuzzy.Match(phrase, name).Score);
        }

        if (JsString.ToLowerCase(session.Id).Contains(phrase, StringComparison.Ordinal))
        {
            return TierScore(TierText);
        }

        if (GetNormalizedMessageText(session).Contains(phrase, StringComparison.Ordinal))
        {
            return TierScore(TierText);
        }

        if (NormalizeWhitespaceLower(session.Cwd).Contains(phrase, StringComparison.Ordinal))
        {
            return TierScore(TierText);
        }

        return null;
    }

    private static double? MatchRegex(SessionInfo session, Regex regex)
    {
        if (!string.IsNullOrEmpty(session.Name) && regex.IsMatch(session.Name))
        {
            return TierScore(TierName);
        }

        if (regex.IsMatch(session.Id) || regex.IsMatch(session.AllMessagesText) || regex.IsMatch(session.Cwd))
        {
            return TierScore(TierText);
        }

        return null;
    }

    /// <summary>Port of the exported <c>hasSessionName</c>.</summary>
    public static bool HasSessionName(SessionInfo session) => JsString.Trim(session.Name ?? "").Length > 0;

    /// <summary>Port of the private <c>matchesNameFilter</c>.</summary>
    private static bool MatchesNameFilter(SessionInfo session, NameFilter filter) =>
        filter == NameFilter.All || HasSessionName(session);

    /// <summary>
    /// Port of the exported <c>parseSearchQuery</c>.
    /// </summary>
    /// <remarks>
    /// Three shapes come out of here: an empty query (<c>tokens: []</c>), a regex query, and a token
    /// query. Quotes group a phrase; an unbalanced quote throws the whole parse away and falls back to
    /// plain whitespace tokenization, which is what makes <c>"foo bar</c> behave like <c>foo bar</c>.
    /// </remarks>
    public static ParsedSearchQuery ParseSearchQuery(string query)
    {
        var trimmed = JsString.Trim(query);
        if (trimmed.Length == 0)
        {
            return new ParsedSearchQuery { Mode = SearchQueryMode.Tokens, Tokens = [], Regex = null };
        }

        // Regex mode: re:<pattern>
        if (JsString.StartsWith(trimmed, "re:"))
        {
            var pattern = JsString.Trim(JsString.Slice(trimmed, 3));
            if (pattern.Length == 0)
            {
                return new ParsedSearchQuery
                {
                    Mode = SearchQueryMode.Regex,
                    Tokens = [],
                    Regex = null,
                    Error = "Empty regex",
                };
            }

            try
            {
                // JS `new RegExp(pattern, "i")`. The user's pattern goes through untouched; the known
                // JS/.NET regex divergences are recorded as C230 in the porting notes.
                return new ParsedSearchQuery
                {
                    Mode = SearchQueryMode.Regex,
                    Tokens = [],
                    Regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                };
            }
            catch (ArgumentException err)
            {
                // TS surfaces `err.message` from V8; .NET's own wording is used instead (C230).
                return new ParsedSearchQuery
                {
                    Mode = SearchQueryMode.Regex,
                    Tokens = [],
                    Regex = null,
                    Error = err.Message,
                };
            }
        }

        // Token mode with quote support. Example: foo "node cve" bar
        var tokens = new List<SearchToken>();
        var buf = "";
        var inQuote = false;

        void Flush(SearchTokenKind kind)
        {
            var value = JsString.Trim(buf);
            buf = "";
            if (value.Length == 0)
            {
                return;
            }

            tokens.Add(new SearchToken(kind, value));
        }

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch == '"')
            {
                if (inQuote)
                {
                    Flush(SearchTokenKind.Phrase);
                    inQuote = false;
                }
                else
                {
                    Flush(SearchTokenKind.Fuzzy);
                    inQuote = true;
                }

                continue;
            }

            if (!inQuote && JsString.IsWhitespace(ch))
            {
                Flush(SearchTokenKind.Fuzzy);
                continue;
            }

            buf += ch;
        }

        // If quotes were unbalanced, fall back to plain whitespace tokenization.
        if (inQuote)
        {
            var fallback = WhitespaceRunRegex()
                .Split(trimmed)
                .Select(JsString.Trim)
                .Where(part => part.Length > 0)
                .Select(part => new SearchToken(SearchTokenKind.Fuzzy, part))
                .ToList();

            return new ParsedSearchQuery { Mode = SearchQueryMode.Tokens, Tokens = fallback, Regex = null };
        }

        // TS writes `flush(inQuote ? "phrase" : "fuzzy")`; an unbalanced quote would already have taken
        // the fallback branch above, so `inQuote` is necessarily false here.
        Flush(SearchTokenKind.Fuzzy);

        return new ParsedSearchQuery { Mode = SearchQueryMode.Tokens, Tokens = tokens, Regex = null };
    }

    /// <summary>
    /// Port of the exported <c>matchSession</c>: score one session against a parsed query.
    /// </summary>
    /// <remarks>
    /// Each field is matched separately, so a token never matches across field boundaries; different
    /// tokens may match different fields. Tiers dominate fuzzy scores, so an exact hit anywhere beats a
    /// scattered subsequence in a short field. Every token must match, and their scores add up.
    /// </remarks>
    public static MatchResult MatchSession(SessionInfo session, ParsedSearchQuery parsed)
    {
        if (parsed.Mode == SearchQueryMode.Regex)
        {
            if (parsed.Regex is null)
            {
                return new MatchResult(false, 0);
            }

            var score = MatchRegex(session, parsed.Regex);
            return score is { } regexScore ? new MatchResult(true, regexScore) : new MatchResult(false, 0);
        }

        var totalScore = 0.0;
        foreach (var token in parsed.Tokens)
        {
            double? score;
            if (token.Kind == SearchTokenKind.Phrase)
            {
                var phrase = NormalizeWhitespaceLower(token.Value);
                if (phrase.Length == 0)
                {
                    continue;
                }

                score = MatchPhraseToken(session, phrase);
            }
            else
            {
                score = MatchFuzzyToken(session, token.Value);
            }

            if (score is not { } tokenScore)
            {
                return new MatchResult(false, 0);
            }

            totalScore += tokenScore;
        }

        return new MatchResult(true, totalScore);
    }

    /// <summary>
    /// Port of the exported <c>filterAndSortSessions</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The name filter runs first, then an empty query short-circuits (returning the name-filtered
    /// list), then a query that failed to parse yields an empty list.
    /// </para>
    /// <para>
    /// <see cref="SortMode.Recent"/> keeps the incoming order. <see cref="SortMode.Threaded"/> and
    /// <see cref="SortMode.Relevance"/> share the scored branch — upstream only special-cases
    /// <c>recent</c> — and the sort is stable so that entries equal on both score and
    /// <c>modified</c> keep their relative order, matching V8's stable <c>Array.prototype.sort</c>.
    /// </para>
    /// <para>
    /// A new list is always returned; unlike the TS version (which hands back the caller's own array
    /// when no filtering happened) the result never aliases <paramref name="sessions"/>.
    /// </para>
    /// </remarks>
    public static List<SessionInfo> FilterAndSortSessions(
        IReadOnlyList<SessionInfo> sessions,
        string query,
        SortMode sortMode,
        NameFilter nameFilter = NameFilter.All)
    {
        IReadOnlyList<SessionInfo> nameFiltered = nameFilter == NameFilter.All
            ? sessions
            : sessions.Where(session => MatchesNameFilter(session, nameFilter)).ToList();

        if (JsString.Trim(query).Length == 0)
        {
            return [.. nameFiltered];
        }

        var parsed = ParseSearchQuery(query);
        if (parsed.Error is not null)
        {
            return [];
        }

        if (sortMode == SortMode.Recent)
        {
            var filtered = new List<SessionInfo>();
            foreach (var session in nameFiltered)
            {
                if (MatchSession(session, parsed).Matches)
                {
                    filtered.Add(session);
                }
            }

            return filtered;
        }

        var scored = new List<(SessionInfo Session, double Score)>();
        foreach (var session in nameFiltered)
        {
            var result = MatchSession(session, parsed);
            if (!result.Matches)
            {
                continue;
            }

            scored.Add((session, result.Score));
        }

        // OrderBy/ThenBy is a stable sort in LINQ-to-Objects, which is what reproduces the TS
        // tie-break-by-modified-descending behaviour without losing the input order on a full tie.
        return scored
            .OrderBy(entry => entry.Score)
            .ThenByDescending(entry => entry.Session.Modified)
            .Select(entry => entry.Session)
            .ToList();
    }
}
