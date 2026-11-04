using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>Result of a fuzzy match: whether it matched and the score (lower is better).</summary>
public readonly record struct FuzzyMatch(bool Matches, double Score);

/// <summary>Port of <c>fuzzy.ts</c>: subsequence matching with word-boundary and consecutive bonuses.</summary>
public static partial class Fuzzy
{
    [GeneratedRegex(@"^(?<letters>[a-z]+)(?<digits>[0-9]+)$")]
    private static partial Regex AlphaNumericRegex();

    [GeneratedRegex(@"^(?<digits>[0-9]+)(?<letters>[a-z]+)$")]
    private static partial Regex NumericAlphaRegex();

    [GeneratedRegex(@"[\s\-_./:]")]
    private static partial Regex WordBoundaryRegex();

    /// <summary>
    /// JS <c>/[\s/]+/</c>. The character class is spelled out because .NET's <c>\s</c> is a
    /// different set from JavaScript's (it includes U+0085 and excludes U+FEFF).
    /// </summary>
    [GeneratedRegex("[\\f\\n\\r\\t\\v\\u0020\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff/]+")]
    private static partial Regex TokenSplitRegex();

    /// <summary>
    /// TS <c>scorePositions</c>：按命中位置逐个算分（连续命中加分、跳字罚分、词边界加分、越靠后轻微罚分）。
    /// </summary>
    private static double ScorePositions(string textLower, IReadOnlyList<int> positions)
    {
        double score = 0;
        var lastMatchIndex = -1;
        var consecutiveMatches = 0;

        foreach (var i in positions)
        {
            if (lastMatchIndex == i - 1)
            {
                consecutiveMatches++;
                score -= consecutiveMatches * 5;
            }
            else
            {
                consecutiveMatches = 0;
                if (lastMatchIndex >= 0)
                {
                    score += (i - lastMatchIndex - 1) * 2;
                }
            }

            if (i == 0 || WordBoundaryRegex().IsMatch(textLower[i - 1].ToString()))
            {
                score -= 10;
            }

            score += i * 0.1;

            lastMatchIndex = i;
        }

        return score;
    }

    public static FuzzyMatch Match(string query, string text)
    {
        var queryLower = JsString.ToLowerCase(query);
        var textLower = JsString.ToLowerCase(text);

        FuzzyMatch MatchQuery(string normalizedQuery)
        {
            if (normalizedQuery.Length == 0)
            {
                return new FuzzyMatch(true, 0);
            }
            if (normalizedQuery.Length > textLower.Length)
            {
                return new FuzzyMatch(false, 0);
            }

            // 贪心对齐：每个 query 字符取上一个命中之后的首次出现。
            var positions = new List<int>(normalizedQuery.Length);
            var lastMatchIndex = -1;
            for (var queryIndex = 0; queryIndex < normalizedQuery.Length; queryIndex++)
            {
                var i = textLower.IndexOf(normalizedQuery[queryIndex], lastMatchIndex + 1);
                if (i == -1)
                {
                    return new FuzzyMatch(false, 0);
                }

                positions.Add(i);
                lastMatchIndex = i;
            }

            var score = ScorePositions(textLower, positions);

            // 贪心对齐可能把 query 撒在更早的无关片段上——例如 "opus" 命中
            // "openrouter openrouter/anthropic/claude-opus-4.1" 时，o/p/u 取自 "openrouter"。
            // 因此对每段**连续出现**也计分，取最优（TS <c>8c03fef4d</c>）。
            var contiguous = new int[positions.Count];
            for (var start = textLower.IndexOf(normalizedQuery, StringComparison.Ordinal);
                 start != -1;
                 start = textLower.IndexOf(normalizedQuery, start + 1, StringComparison.Ordinal))
            {
                for (var k = 0; k < contiguous.Length; k++)
                {
                    contiguous[k] = start + k;
                }

                score = Math.Min(score, ScorePositions(textLower, contiguous));
            }

            if (normalizedQuery == textLower)
            {
                score -= 100;
            }

            return new FuzzyMatch(true, score);
        }

        var primary = MatchQuery(queryLower);
        if (primary.Matches)
        {
            return primary;
        }

        var alphaNumeric = AlphaNumericRegex().Match(queryLower);
        var numericAlpha = NumericAlphaRegex().Match(queryLower);
        string swappedQuery;
        if (alphaNumeric.Success)
        {
            swappedQuery = alphaNumeric.Groups["digits"].Value + alphaNumeric.Groups["letters"].Value;
        }
        else if (numericAlpha.Success)
        {
            swappedQuery = numericAlpha.Groups["letters"].Value + numericAlpha.Groups["digits"].Value;
        }
        else
        {
            swappedQuery = "";
        }

        if (swappedQuery.Length == 0)
        {
            return primary;
        }

        var swapped = MatchQuery(swappedQuery);
        if (!swapped.Matches)
        {
            return primary;
        }

        return new FuzzyMatch(true, swapped.Score + 5);
    }

    /// <summary>Filter and sort items by fuzzy match quality (best matches first).</summary>
    public static List<T> Filter<T>(IEnumerable<T> items, string query, Func<T, string> getText)
    {
        var itemList = items.ToList();
        var trimmed = JsString.Trim(query);
        if (trimmed.Length == 0)
        {
            return itemList;
        }

        var tokens = TokenSplitRegex().Split(trimmed).Where(t => t.Length > 0).ToList();
        if (tokens.Count == 0)
        {
            return itemList;
        }

        var results = new List<(T Item, double TotalScore)>();
        foreach (var item in itemList)
        {
            var text = getText(item);
            double totalScore = 0;
            var allMatch = true;

            foreach (var token in tokens)
            {
                var match = Match(token, text);
                if (match.Matches)
                {
                    totalScore += match.Score;
                }
                else
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
            {
                results.Add((item, totalScore));
            }
        }

        return results.OrderBy(r => r.TotalScore).Select(r => r.Item).ToList();
    }
}
