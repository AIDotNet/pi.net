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

    /// <summary>Port of the private <c>getSessionSearchText</c>.</summary>
    private static string GetSessionSearchText(SessionInfo session) =>
        $"{session.Id} {session.Name ?? ""} {session.AllMessagesText} {session.Cwd}";

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
    /// Phrase tokens are matched against the whitespace-normalized, lower-cased haystack (computed at
    /// most once, and only if a phrase token is present); fuzzy tokens run against the raw text. Every
    /// token must match, and their scores add up.
    /// </remarks>
    public static MatchResult MatchSession(SessionInfo session, ParsedSearchQuery parsed)
    {
        var text = GetSessionSearchText(session);

        if (parsed.Mode == SearchQueryMode.Regex)
        {
            if (parsed.Regex is null)
            {
                return new MatchResult(false, 0);
            }

            // JS `text.search(regex)`: index of the first match, or -1.
            var match = parsed.Regex.Match(text);
            return match.Success ? new MatchResult(true, match.Index * 0.1) : new MatchResult(false, 0);
        }

        if (parsed.Tokens.Count == 0)
        {
            return new MatchResult(true, 0);
        }

        var totalScore = 0.0;
        string? normalizedText = null;

        foreach (var token in parsed.Tokens)
        {
            if (token.Kind == SearchTokenKind.Phrase)
            {
                normalizedText ??= NormalizeWhitespaceLower(text);
                var phrase = NormalizeWhitespaceLower(token.Value);
                if (phrase.Length == 0)
                {
                    continue;
                }

                var index = normalizedText.IndexOf(phrase, StringComparison.Ordinal);
                if (index < 0)
                {
                    return new MatchResult(false, 0);
                }

                totalScore += index * 0.1;
                continue;
            }

            var fuzzy = Fuzzy.Match(token.Value, text);
            if (!fuzzy.Matches)
            {
                return new MatchResult(false, 0);
            }

            totalScore += fuzzy.Score;
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
