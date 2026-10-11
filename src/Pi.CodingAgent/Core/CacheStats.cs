using System.Globalization;
using Pi.Ai.Types;

namespace Pi.CodingAgent.Core;

/// <summary>
/// 会话 prompt-cache 浪费统计。对应 TS <c>core/cache-stats.ts</c>。
/// </summary>
public static class CacheStats
{
    /// <summary>
    /// Prompt-cache TTL：空闲间隔超过该值时值得作为 miss 的可能原因提出。
    /// Anthropic 默认 cache TTL 为 5 分钟。对应 TS <c>CACHE_TTL_MS</c>。
    /// </summary>
    public const long CacheTtlMs = 5 * 60 * 1000;

    /// <summary>单回合 miss 低于该 token 数视为 cache breakpoint 粒度噪声。对应 TS <c>NOISE_FLOOR_TOKENS</c>。</summary>
    public const long NoiseFloorTokens = 1024;

    /// <summary>最小定价查询，由 <c>ModelRuntime</c> 满足。费率单位是 $/百万 token。对应 TS <c>ModelPriceSource</c>。</summary>
    public interface IModelPriceSource
    {
        ModelPriceQuote? GetModel(string provider, string modelId);
    }

    /// <summary>对应 TS <c>{ cost: { cacheRead: number } }</c>。</summary>
    public sealed record ModelPriceQuote
    {
        public required ModelPriceQuoteCost Cost { get; init; }
    }

    /// <summary>对应 TS <c>{ cacheRead: number }</c>。</summary>
    public sealed record ModelPriceQuoteCost
    {
        public required double CacheRead { get; init; }
    }

    /// <summary>单条助手消息上计入的 cache miss。对应 TS <c>CacheMiss</c>。</summary>
    public sealed record CacheMiss
    {
        /// <summary>上一回合 prompt 中有、但本回合未从 cache 读回的 token 数。</summary>
        public required long MissedTokens { get; init; }

        /// <summary>相对全命中多付的美元；定价未知时为 0。</summary>
        public required double MissedCost { get; init; }

        /// <summary>距上一次请求（刷新 cache）的毫秒数。</summary>
        public required long IdleMs { get; init; }

        /// <summary>相对上一请求是否换了模型。</summary>
        public required bool ModelChanged { get; init; }
    }

    /// <summary>会话累计 cache 浪费。对应 TS <c>CacheWasteTotals</c>。</summary>
    public sealed record CacheWasteTotals
    {
        public required long MissedTokens { get; init; }

        public required double MissedCost { get; init; }

        /// <summary>计入的 miss 次数（超过噪声地板的回合）。</summary>
        public required int MissCount { get; init; }
    }

    private sealed record PreviousRequest(long PromptTokens, string ModelKey, long Timestamp, bool ReportedCache);

    /// <summary>
    /// 会话累计 cache 浪费：本应是 cache read（它们在上一回合 prompt 里）却被重新计费的 prompt token。
    /// 对应 TS <c>computeCacheWaste</c>。
    /// </summary>
    public static CacheWasteTotals ComputeCacheWaste(IEnumerable<SessionEntry> entries, IModelPriceSource models)
        => Scan(entries, models).Totals;

    /// <summary>
    /// 会话中所有计入的 cache miss，按产生它们的助手消息**引用**索引。
    /// 对应 TS <c>collectCacheMisses</c>（<c>Map</c> 按对象引用）。
    /// </summary>
    public static Dictionary<AssistantMessage, CacheMiss> CollectCacheMisses(
        IEnumerable<SessionEntry> entries,
        IModelPriceSource models)
        => Scan(entries, models).Misses;

    /// <summary>
    /// 刚完成的助手消息上的 cache miss。<paramref name="entries"/> 尚不包含该消息
    /// （<c>message_end</c> 在持久化之前触发）。对应 TS <c>detectCacheMiss</c>。
    /// </summary>
    public static CacheMiss? DetectCacheMiss(
        IEnumerable<SessionEntry> entries,
        AssistantMessage message,
        IModelPriceSource models)
        => DetectMiss(Scan(entries, models).Prev, message, models);

    private static (PreviousRequest? Prev, CacheWasteTotals Totals, Dictionary<AssistantMessage, CacheMiss> Misses) Scan(
        IEnumerable<SessionEntry> entries,
        IModelPriceSource models)
    {
        PreviousRequest? prev = null;
        var totals = new CacheWasteTotals { MissedTokens = 0, MissedCost = 0, MissCount = 0 };
        var misses = new Dictionary<AssistantMessage, CacheMiss>(ReferenceEqualityComparer.Instance);

        foreach (var entry in entries)
        {
            if (entry is CompactionEntry or BranchSummaryEntry)
            {
                // 上下文合法变化：下一回合的 prompt 是新内容，不是重新计费的旧内容。
                // 换模型不豁免：会整段重计费，必须计入。
                prev = null;
                continue;
            }

            if (entry is UsageEntry { Kind: "cache_warm" } usageEntry)
            {
                var promptTokens = usageEntry.Usage.Input + usageEntry.Usage.CacheRead + usageEntry.Usage.CacheWrite;
                if (promptTokens > 0)
                {
                    prev = new PreviousRequest(
                        promptTokens,
                        $"{usageEntry.Provider}/{usageEntry.Model}",
                        ParseTimestampMs(usageEntry.Timestamp),
                        ReportedCache: true);
                }
            }
            else if (entry is SessionMessageEntry { Message: AssistantMessage assistant })
            {
                var miss = DetectMiss(prev, assistant, models);
                if (miss is not null)
                {
                    totals = totals with
                    {
                        MissedTokens = totals.MissedTokens + miss.MissedTokens,
                        MissedCost = totals.MissedCost + miss.MissedCost,
                        MissCount = totals.MissCount + 1,
                    };
                    misses[assistant] = miss;
                }

                prev = AsPreviousRequest(assistant, prev?.ReportedCache ?? false) ?? prev;
            }
        }

        return (prev, totals, misses);
    }

    private static CacheMiss? DetectMiss(PreviousRequest? prev, AssistantMessage message, IModelPriceSource models)
    {
        var usage = message.UsageStats;
        if (usage is null)
        {
            return null;
        }

        var promptTokens = usage.Input + usage.CacheRead + usage.CacheWrite;
        // 零 cache 回合只在此前报告过 cache 活动时才计入：对只报 cache-read 的 provider 是全 miss，
        // 对从不报 cache 的 provider 则什么都不表示。
        if (prev is null || promptTokens <= 0 || (usage.CacheRead + usage.CacheWrite == 0 && !prev.ReportedCache))
        {
            return null;
        }

        var missedTokens = Math.Min(prev.PromptTokens, promptTokens) - usage.CacheRead;
        if (missedTokens <= NoiseFloorTokens)
        {
            return null;
        }

        var paidTokens = usage.Input + usage.CacheWrite;
        var paidPerToken = paidTokens > 0 ? (usage.Cost.Input + usage.Cost.CacheWrite) / paidTokens : 0;
        var readPerToken = usage.CacheRead > 0
            ? usage.Cost.CacheRead / usage.CacheRead
            : (models.GetModel(message.Provider ?? "", message.Model ?? "")?.Cost.CacheRead ?? 0) / 1_000_000;

        return new CacheMiss
        {
            MissedTokens = missedTokens,
            MissedCost = missedTokens * Math.Max(0, paidPerToken - readPerToken),
            IdleMs = Math.Max(0, (message.Timestamp ?? 0) - prev.Timestamp),
            ModelChanged = $"{message.Provider}/{message.Model}" != prev.ModelKey,
        };
    }

    private static PreviousRequest? AsPreviousRequest(AssistantMessage message, bool reportedCache)
    {
        var usage = message.UsageStats;
        if (usage is null)
        {
            return null;
        }

        var promptTokens = usage.Input + usage.CacheRead + usage.CacheWrite;
        if (promptTokens <= 0)
        {
            return null;
        }

        return new PreviousRequest(
            promptTokens,
            $"{message.Provider}/{message.Model}",
            message.Timestamp ?? 0,
            reportedCache || usage.CacheRead + usage.CacheWrite > 0);
    }

    private static long ParseTimestampMs(string timestamp)
    {
        if (DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            return dto.ToUnixTimeMilliseconds();
        }

        return 0;
    }
}
