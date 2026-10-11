using Pi.Ai.Types;

namespace Pi.CodingAgent.Core;

/// <summary>按桶累计的用量总数。对应 TS <c>UsageTotals</c>（usage-totals.ts）。</summary>
public sealed record UsageTotals
{
    public long Input { get; set; }

    public long Output { get; set; }

    public long CacheRead { get; set; }

    public long CacheWrite { get; set; }

    public double Cost { get; set; }
}

/// <summary>用量累计与合并。对应 TS <c>core/usage-totals.ts</c>。</summary>
public static class UsageTotalsApi
{
    /// <summary>TS <c>createUsageTotals()</c>。</summary>
    public static UsageTotals CreateUsageTotals() => new();

    /// <summary>TS <c>addUsageToTotals(totals, usage)</c>。</summary>
    public static void AddUsageToTotals(UsageTotals totals, Usage usage)
    {
        totals.Input += usage.Input;
        totals.Output += usage.Output;
        totals.CacheRead += usage.CacheRead;
        totals.CacheWrite += usage.CacheWrite;
        totals.Cost += usage.Cost.Total;
    }

    /// <summary>
    /// Sum of two usages, keeping the optional token splits when either side reports them.
    /// TS <c>combineUsage(first, second)</c>。
    /// </summary>
    public static Usage CombineUsage(Usage first, Usage second) => new(
        first.Input + second.Input,
        first.Output + second.Output,
        first.CacheRead + second.CacheRead,
        first.CacheWrite + second.CacheWrite)
    {
        CacheWrite1h = first.CacheWrite1h is not null || second.CacheWrite1h is not null
            ? (first.CacheWrite1h ?? 0) + (second.CacheWrite1h ?? 0)
            : null,
        Reasoning = first.Reasoning is not null || second.Reasoning is not null
            ? (first.Reasoning ?? 0) + (second.Reasoning ?? 0)
            : null,
        TotalTokens = first.TotalTokens + second.TotalTokens,
        Cost = new UsageCost(
            first.Cost.Input + second.Cost.Input,
            first.Cost.Output + second.Cost.Output,
            first.Cost.CacheRead + second.Cost.CacheRead,
            first.Cost.CacheWrite + second.Cost.CacheWrite,
            first.Cost.Total + second.Cost.Total),
    };
}

/// <summary>按 key 分组的成本行。对应 TS <c>UsageCostBreakdownEntry</c>。</summary>
public sealed record UsageCostBreakdownEntry
{
    public required string Key { get; init; }

    public required double Cost { get; init; }

    public required long Tokens { get; init; }
}

public static partial class UsageTotalsApi
{
    /// <summary>
    /// Group model-attributed usage by model and all other usage into a separate bucket.
    /// TS <c>getUsageCostBreakdown(entries)</c>。
    /// </summary>
    public static IReadOnlyList<UsageCostBreakdownEntry> GetUsageCostBreakdown(IEnumerable<FileEntry> entries)
    {
        var totalsByKey = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            string? key = null;
            Usage? usage = null;
            switch (entry)
            {
                case SessionMessageEntry { Message: AssistantMessage assistant }:
                    key = $"{assistant.Provider}/{assistant.ResponseModel ?? assistant.Model}";
                    usage = assistant.UsageStats;
                    break;
                case UsageEntry usageEntry:
                    key = $"{usageEntry.Provider}/{usageEntry.Model}";
                    usage = usageEntry.Usage;
                    break;
                case SessionMessageEntry
                    {
                        Message: ToolResultMessage { UsageStats: not null } toolResult
                    }:
                    key = "Tools/summaries";
                    usage = toolResult.UsageStats;
                    break;
                case BranchSummaryEntry { UsageStats: not null } branchSummary:
                    key = "Tools/summaries";
                    usage = branchSummary.UsageStats;
                    break;
                case CompactionEntry { UsageStats: not null } compaction:
                    key = "Tools/summaries";
                    usage = compaction.UsageStats;
                    break;
            }

            if (key is null || usage is null)
            {
                continue;
            }

            if (!totalsByKey.TryGetValue(key, out var totals))
            {
                totals = CreateUsageTotals();
                totalsByKey[key] = totals;
            }

            AddUsageToTotals(totals, usage);
        }

        return [.. totalsByKey
            .Select(pair => new UsageCostBreakdownEntry
            {
                Key = pair.Key,
                Cost = pair.Value.Cost,
                Tokens = pair.Value.Input + pair.Value.Output + pair.Value.CacheRead + pair.Value.CacheWrite,
            })
            .Where(row => row.Cost > 0 || row.Tokens > 0)
            .OrderByDescending(row => row.Cost)];
    }
}
