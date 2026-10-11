using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;
namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>CacheWarmingAction</c>。</summary>
public static class CacheWarmingAction
{
    public const string Warm = "warm";
    public const string Stop = "stop";
}

/// <summary>对应 TS <c>CacheWarmingDecision</c>。</summary>
public sealed record CacheWarmingDecision
{
    public required string Phase { get; init; }

    public required double WarmCost { get; init; }

    public required double MissCost { get; init; }

    public required double ContinuationProbability { get; init; }

    public required double ExpectedSavings { get; init; }

    public required bool EconomicsAvailable { get; init; }

    public required string Action { get; init; }
}

/// <summary>对应 TS <c>CacheWarmingStatus</c>。</summary>
public sealed record CacheWarmingStatus
{
    public required string State { get; init; }

    public string? Reason { get; init; }

    public long? NextWarmAt { get; init; }

    public CacheWarmingDecision? Decision { get; init; }

    public bool? ExtensionOverride { get; init; }
}

/// <summary>对应 TS <c>CacheWarmRequest</c>。</summary>
public sealed record CacheWarmRequest
{
    public required ModelSpec Model { get; init; }

    public required SimpleStreamOptions Options { get; init; }
}

/// <summary>
/// 提示缓存预热。对应 TS <c>core/cache-warmer.ts</c>。
/// 4e-3d-2：delay/ttl/replayable、status 文案、Start/Stop 门闩；真实 streamSimple 顺延 4e-3e。
/// </summary>
public static class CacheWarmerMath
{
    public const double MinimumExpectedSavings = 0.05;
    public const double IdleContinuationProbability = 0.15;

    /// <summary>对应 TS <c>getCacheWarmingDelayMs</c>。</summary>
    public static int? GetCacheWarmingDelayMs(int ttlMs)
    {
        if (ttlMs <= 10_000)
        {
            return null;
        }

        return Math.Max(1, (int)Math.Floor(Math.Min(ttlMs * 0.9, ttlMs - 10_000d)));
    }

    /// <summary>对应 TS <c>getPromptCacheTtlMs</c>。</summary>
    public static int? GetPromptCacheTtlMs(ModelSpec model, SimpleStreamOptions? options)
    {
        var retention = options?.CacheRetention;
        if (string.IsNullOrEmpty(retention))
        {
            retention = GetEnv(options, "PI_CACHE_RETENTION") == "long" ? "long" : null;
        }

        if (string.IsNullOrEmpty(retention) || retention == "none")
        {
            return null;
        }

        var seconds = ReadPromptCacheSeconds(model, retention);
        return seconds is null ? null : seconds * 1000;
    }

    /// <summary>对应 TS <c>isReplayable</c>。</summary>
    public static bool IsReplayable(ModelSpec model, SimpleStreamOptions? options)
    {
        if (string.IsNullOrEmpty(options?.Reasoning) || model.Api != "anthropic-messages")
        {
            return true;
        }

        return model.Compat?["forceAdaptiveThinking"] is JsonValue value
            && value.TryGetValue<bool>(out var flag)
            && flag;
    }

    /// <summary>对应 TS <c>formatCacheWarmingStatus</c>。</summary>
    public static string FormatCacheWarmingStatus(CacheWarmingStatus status, long? nowMs = null)
    {
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var decision = status.Decision;
        if (decision is null || (status.State == "inactive" && !decision.EconomicsAvailable && status.ExtensionOverride is not true))
        {
            return $"Inactive ({status.Reason ?? "unknown reason"})";
        }

        var details = status.ExtensionOverride is true
            ? $"extension override, {FormatEconomics(decision)}"
            : $"{FormatEconomics(decision)} -> {decision.Action}";
        if (status.State == "inactive")
        {
            return $"Stopped ({details})";
        }

        if (status.State == "refreshing")
        {
            return $"Warming cache ({details})";
        }

        return $"{FormatDecisionTime(status.NextWarmAt, now)} ({details})";
    }

    /// <summary>对应 TS <c>formatCacheWarmingUsage</c>。</summary>
    public static string FormatCacheWarmingUsage(UsageEntry entry)
    {
        var note = string.IsNullOrEmpty(entry.Note) ? string.Empty : $" ({entry.Note})";
        var cost = entry.Usage.Cost.Total.ToString("0.000000").TrimEnd('0');
        if (cost.EndsWith('.'))
        {
            cost += "0";
        }

        return $"Cache warmed{note}: ${cost}";
    }

    internal static int? ReadPromptCacheSeconds(ModelSpec model, string retention)
    {
        if (model.Extra?["promptCache"] is not JsonObject cache)
        {
            return null;
        }

        if (cache[retention] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var seconds))
        {
            return seconds;
        }

        return value.TryGetValue<double>(out var numeric) ? (int)numeric : null;
    }

    private static string? GetEnv(SimpleStreamOptions? options, string name)
        => options?.Env is { } env && env.TryGetValue(name, out var value) ? value : Environment.GetEnvironmentVariable(name);

    private static string FormatEconomics(CacheWarmingDecision decision)
    {
        if (!decision.EconomicsAvailable)
        {
            return "cache economics unavailable";
        }

        var probability = (int)Math.Round(decision.ContinuationProbability * 100);
        var probabilityText = decision.Phase == "streaming"
            ? $"{probability}% continuation probability while agent is running"
            : $"{probability}% continuation probability";
        var comparison = decision.Action == CacheWarmingAction.Warm ? ">=" : "<";
        return $"{probabilityText}, expected savings {FormatDollars(decision.ExpectedSavings)} {comparison} ${MinimumExpectedSavings:0.000}";
    }

    private static string FormatDollars(double value)
        => value < 0 ? $"-${Math.Abs(value):0.000}" : $"${value:0.000}";

    private static string FormatDecisionTime(long? nextWarmAt, long now)
    {
        if (nextWarmAt is null || nextWarmAt <= now)
        {
            return "Decision now";
        }

        var remainingSeconds = (int)Math.Ceiling((nextWarmAt.Value - now) / 1000.0);
        var hours = remainingSeconds / 3600;
        remainingSeconds %= 3600;
        var minutes = remainingSeconds / 60;
        var seconds = remainingSeconds % 60;
        var parts = new List<string>();
        if (hours > 0)
        {
            parts.Add($"{hours}h");
        }

        if (minutes > 0)
        {
            parts.Add($"{minutes}m");
        }

        if (seconds > 0 || parts.Count == 0)
        {
            parts.Add($"{seconds}s");
        }

        return $"Decision in {string.Join(" ", parts)}";
    }
}

/// <summary>对应 TS <c>CacheWarmer</c> 的门闩与状态；刷新流顺延 4e-3e。</summary>
public sealed class CacheWarmer
{
    private readonly Func<string> _getMode;
    private CacheWarmingStatus _inactive = new() { State = "inactive", Reason = "waiting for first request" };
    private CacheWarmRequest? _run;
    private Func<bool>? _isCurrent;

    public CacheWarmer(Func<string> getMode) => _getMode = getMode;

    public CacheWarmingStatus Status
    {
        get
        {
            if (_getMode() == "off")
            {
                return new CacheWarmingStatus { State = "inactive", Reason = "cache warming disabled" };
            }

            if (_run is null)
            {
                return _inactive;
            }

            if (_isCurrent is not null && !_isCurrent())
            {
                return new CacheWarmingStatus { State = "inactive", Reason = "conversation context changed" };
            }

            return new CacheWarmingStatus { State = "scheduled", NextWarmAt = _inactive.NextWarmAt };
        }
    }

    public void Start(CacheWarmRequest request, Func<bool> isCurrent)
    {
        _run = null;
        _isCurrent = isCurrent;
        if (_getMode() == "off")
        {
            Stop("cache warming disabled");
            return;
        }

        if (!CacheWarmerMath.IsReplayable(request.Model, request.Options))
        {
            Stop("request cannot be replayed safely");
            return;
        }

        var ttlMs = CacheWarmerMath.GetPromptCacheTtlMs(request.Model, request.Options);
        if (ttlMs is null)
        {
            Stop(request.Options.CacheRetention == "none"
                ? "request disabled prompt caching"
                : "cache lifetime unavailable");
            return;
        }

        if (CacheWarmerMath.GetCacheWarmingDelayMs(ttlMs.Value) is null)
        {
            Stop("cache lifetime unavailable");
            return;
        }

        _run = request;
        _inactive = new CacheWarmingStatus { State = "scheduled" };
    }

    public void OnAgentSettled()
    {
        if (_run is null)
        {
            return;
        }

        if (_getMode() == "streaming")
        {
            Stop("agent run settled");
        }
    }

    public void Cancel() => Stop("inactive");

    private void Stop(string reason)
    {
        _run = null;
        _inactive = new CacheWarmingStatus { State = "inactive", Reason = reason };
    }
}
