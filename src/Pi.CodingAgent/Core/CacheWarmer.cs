using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
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

/// <summary>对应 TS <c>CacheWarmingDecisionEvent</c>。</summary>
/// <remarks>每次刷新前发出，携带 pi 自己的决策；扩展可覆盖 <see cref="Action"/>。</remarks>
public sealed record CacheWarmingDecisionEvent
{
    public string Type { get; init; } = "cache_warming_decision";

    /// <summary>本次刷新的价格：提示词的缓存读 + 1 个输出 token。</summary>
    public required double WarmCost { get; init; }

    /// <summary>缓存条目丢失时下一次真实请求的额外开销。</summary>
    public required double MissCost { get; init; }

    /// <summary>条目过期前真实请求到达的估计概率。</summary>
    public required double ContinuationProbability { get; init; }

    /// <summary><see cref="CacheWarmingAction.Warm"/> 或 <see cref="CacheWarmingAction.Stop"/>。</summary>
    public required string Action { get; init; }
}

/// <summary>对应 TS <c>CacheWarmRequest</c>：要保活的、逐字重放的请求。</summary>
/// <remarks>
/// 差异 <b>C162</b>：TS 的 <c>options</c> 就是 <c>buildRequestOptions</c> 产出的请求选项对象，
/// 因此天然带着 <c>transformHeaders</c> / <c>onPayload</c> 这类只有该对象才有的成员。
/// 端口把「结构化选项」（<see cref="SimpleStreamOptions"/>）与「provider 请求字典」分成两半，
/// 故另留 <see cref="RequestOptions"/> 承载原始字典；为空时由 <see cref="Options"/> 反推
/// （此时字典专属键会缺失）。数学判定（TTL / 可重放性）仍走 <see cref="Options"/>。
/// </remarks>
public sealed record CacheWarmRequest
{
    public required ModelSpec Model { get; init; }

    /// <summary>请求的上下文（消息 + 工具）。对应 TS <c>context</c>。</summary>
    public required TranscriptContext Context { get; init; }

    public required SimpleStreamOptions Options { get; init; }

    /// <summary>真实请求实际使用的 provider 选项字典；见差异 C162。</summary>
    public IReadOnlyDictionary<string, object?>? RequestOptions { get; init; }
}

/// <summary>
/// 提示缓存预热。对应 TS <c>core/cache-warmer.ts</c>。
/// 4e-3d-2：delay/ttl/replayable、status 文案、Start/Stop 门闩；真实 streamSimple 顺延 4e-3e。
/// </summary>
public static class CacheWarmerMath
{
    public const double MinimumExpectedSavings = 0.05;
    public const double IdleContinuationProbability = 0.15;

    /// <summary>流式预热绝不超过真实请求之后的这个时长。对应 TS <c>MAX_WARMING_AGE_MS</c>。</summary>
    public const long MaxWarmingAgeMs = 60 * 60_000L;

    /// <summary>空闲预热窗口更短，因为延续概率估计随年龄衰减。对应 TS <c>MAX_IDLE_WARMING_AGE_MS</c>。</summary>
    public const long MaxIdleWarmingAgeMs = 30 * 60_000L;

    /// <summary>
    /// 分支上最近一次真实请求的提示词规模（provider 上报值）。对应 TS <c>lastPromptTokens</c>。
    /// </summary>
    /// <remarks>从末尾向前找第一条助手消息，取 <c>input + cacheRead + cacheWrite</c>；没有则 0。</remarks>
    internal static long LastPromptTokens(IReadOnlyList<SessionEntry> entries)
    {
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index] is SessionMessageEntry { Message: AssistantMessage assistant })
            {
                var usage = assistant.UsageStats;
                return usage is null ? 0 : usage.Input + usage.CacheRead + usage.CacheWrite;
            }
        }

        return 0;
    }

    /// <summary>按目录费率折算某组 token 的价格。对应 TS <c>price</c>。</summary>
    internal static double Price(
        ModelSpec model,
        long input = 0,
        long output = 0,
        long cacheRead = 0,
        long cacheWrite = 0)
        => ModelOperations.CalculateCost(
            model,
            new Usage(input, output, cacheRead, cacheWrite) { TotalTokens = input + output + cacheRead + cacheWrite })
            .Cost.Total;

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
    /// <remarks>
    /// TS：<c>options?.cacheRetention ?? (PI_CACHE_RETENTION === "long" ? "long" : "short")</c>。
    /// 未显式指定时**回落到 <c>"short"</c>**（不是「不可用」）——否则默认配置下缓存预热永不激活。
    /// </remarks>
    public static int? GetPromptCacheTtlMs(ModelSpec model, SimpleStreamOptions? options)
    {
        var retention = options?.CacheRetention;
        if (string.IsNullOrEmpty(retention))
        {
            retention = GetEnv(options, "PI_CACHE_RETENTION") == "long" ? "long" : "short";
        }

        if (retention == "none")
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
    /// <remarks>
    /// TS：<c>cost.toFixed(6).replace(/(\.\d{3}\d*?)0+$/, "$1")</c>——去掉尾随零，但**至少保留 3 位小数**。
    /// </remarks>
    public static string FormatCacheWarmingUsage(UsageEntry entry)
    {
        var note = string.IsNullOrEmpty(entry.Note) ? string.Empty : $" ({entry.Note})";
        var raw = entry.Usage.Cost.Total.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
        var dot = raw.IndexOf('.');
        var keep = raw.Length;
        while (keep > dot + 4 && raw[keep - 1] == '0')
        {
            keep--;
        }

        return $"Cache warmed{note}: ${raw[..keep]}";
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

/// <summary>对应 TS <c>Pick&lt;CacheWarmingStatus, "decision" | "extensionOverride"&gt;</c>。</summary>
public sealed record CacheWarmingStopDetail(CacheWarmingDecision? Decision, bool? ExtensionOverride);

/// <summary>预热用的流式调用。对应 TS 的 <c>Pick&lt;ModelRuntime, "streamSimple"&gt;</c>。</summary>
public delegate IAssistantMessageEventStream CacheWarmStreamSimple(
    ModelSpec model,
    IReadOnlyList<ChatMessage> context,
    IReadOnlyDictionary<string, object?> options);

/// <summary>持久化预热用量。对应 TS 的 <c>Pick&lt;SessionManager, "appendUsage"&gt;</c>。</summary>
public delegate UsageEntry CacheWarmAppendUsage(
    string kind,
    string provider,
    string model,
    Usage usage,
    string? note = null);

/// <summary>读取当前分支。对应 TS 的 <c>Pick&lt;SessionManager, "getBranch"&gt;</c>。</summary>
public delegate IReadOnlyList<SessionEntry> CacheWarmGetBranch();

/// <summary>
/// 保活一条提示词缓存条目：在条目过期前，以 1 token 的输出上限重发它的请求。
/// 对应 TS <c>core/cache-warmer.ts</c> 的 <c>CacheWarmer</c>。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Start"/> 会替换上一次的 run；预热请求绝不延长固定的安全窗口
/// （<see cref="CacheWarmerMath.MaxWarmingAgeMs"/> / <see cref="CacheWarmerMath.MaxIdleWarmingAgeMs"/>）。
/// </para>
/// <para>
/// 差异 <b>C163</b>：TS 的构造器收 <c>Pick&lt;ModelRuntime, "streamSimple"&gt;</c> 与
/// <c>Pick&lt;SessionManager, "appendUsage" | "getBranch"&gt;</c>（结构化类型）；C# 无结构化类型，
/// 故把三个成员摊平成三个委托参数。TS 的 <c>sessionManager</c> 拆成
/// <paramref name="appendUsage"/> + <paramref name="getBranch"/> 两个入参。
/// </para>
/// </remarks>
public sealed class CacheWarmer
{
    private readonly CacheWarmStreamSimple _streamSimple;
    private readonly CacheWarmAppendUsage _appendUsage;
    private readonly CacheWarmGetBranch _getBranch;
    private readonly Func<string> _getMode;
    private readonly Func<CacheWarmingDecisionEvent, Task<string>> _decide;

    private ActiveRun? _run;
    private CacheWarmingStatus _inactive;

    public CacheWarmer(
        CacheWarmStreamSimple streamSimple,
        CacheWarmAppendUsage appendUsage,
        CacheWarmGetBranch getBranch,
        Func<string> getMode,
        Func<CacheWarmingDecisionEvent, Task<string>>? decide = null)
    {
        _streamSimple = streamSimple;
        _appendUsage = appendUsage;
        _getBranch = getBranch;
        _getMode = getMode;
        _decide = decide ?? (evt => Task.FromResult(evt.Action));
        _inactive = new CacheWarmingStatus { State = "inactive", Reason = "waiting for first request" };
    }

    /// <summary>每次成功刷新后收到持久化的用量条目。对应 TS <c>onWarmed</c>。</summary>
    public Action<UsageEntry>? OnWarmed { get; set; }

    /// <summary>时钟。对应 TS 的 <c>Date.now()</c>；测试可替换（TS 侧用 fake timers）。</summary>
    internal Func<long> NowMs { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>当前活动 run（测试驱动用；对应 TS 测试里对 <c>warmer.run</c> 的私有访问）。</summary>
    internal ActiveRun? Run => _run;

    public CacheWarmingStatus Status
    {
        get
        {
            if (_getMode() == "off")
            {
                return new CacheWarmingStatus { State = "inactive", Reason = "cache warming disabled" };
            }

            var run = _run;
            if (run is null)
            {
                return _inactive;
            }

            if (!run.IsCurrent())
            {
                return new CacheWarmingStatus { State = "inactive", Reason = "conversation context changed" };
            }

            var decision = Evaluate(run);
            var refreshing = run.Timer is null;
            if (!decision.EconomicsAvailable && !refreshing)
            {
                return new CacheWarmingStatus { State = "inactive", Reason = "cache economics unavailable" };
            }

            return new CacheWarmingStatus
            {
                State = refreshing ? "refreshing" : "scheduled",
                NextWarmAt = run.NextWarmAt,
                Decision = decision,
                ExtensionOverride = run.ExtensionOverride,
            };
        }
    }

    /// <summary>在 <paramref name="isCurrent"/> 成立期间，保活 <paramref name="request"/> 写下的缓存条目。</summary>
    public void Start(CacheWarmRequest request, Func<bool> isCurrent)
    {
        ClearRun();
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

        var delayMs = CacheWarmerMath.GetCacheWarmingDelayMs(ttlMs.Value);
        if (delayMs is null)
        {
            Stop("cache lifetime unavailable");
            return;
        }

        var run = new ActiveRun(request, isCurrent, ttlMs.Value, delayMs.Value) { StartedAt = NowMs() };
        _run = run;
        Schedule(run);
    }

    /// <summary>agent run 结束。对应 TS <c>onAgentSettled</c>。</summary>
    public void OnAgentSettled()
    {
        var run = _run;
        if (run is null)
        {
            return;
        }

        if (_getMode() == "streaming")
        {
            Stop("agent run settled");
            return;
        }

        run.Phase = "idle";
        var deadline = run.StartedAt + CacheWarmerMath.MaxIdleWarmingAgeMs;
        if (run.NextWarmAt > deadline || NowMs() >= deadline)
        {
            Stop("30-minute idle safety limit reached");
        }
    }

    /// <summary>持久化的预热模式变化后重新对账活动 run。对应 TS <c>onModeChanged</c>。</summary>
    public void OnModeChanged()
    {
        var run = _run;
        if (run is null)
        {
            return;
        }

        if (GetModeStopReason(run) is { } reason)
        {
            Stop(reason);
        }
    }

    public void Cancel() => Stop("inactive");

    private void ClearRun()
    {
        var run = _run;
        if (run is null)
        {
            return;
        }

        _run = null;
        run.Timer?.Dispose();
        run.Timer = null;
        run.Controller.Cancel();
    }

    private void Stop(string reason, CacheWarmingStopDetail? stopped = null)
    {
        ClearRun();
        _inactive = new CacheWarmingStatus
        {
            State = "inactive",
            Reason = reason,
            Decision = stopped?.Decision,
            ExtensionOverride = stopped?.ExtensionOverride,
        };
    }

    private void Schedule(ActiveRun run)
    {
        run.ExtensionOverride = false;
        run.NextWarmAt = NowMs() + run.DelayMs;
        // 定时器可能因休眠 / 事件循环阻塞而迟到。为这段延迟与请求派发留出原定过期余量的一半；
        // 迟到的刷新很可能是一次全价缓存写，而不是缓存命中。
        run.RefreshDeadlineAt = run.NextWarmAt + ((run.TtlMs - run.DelayMs) / 2);
        var deadline = run.StartedAt
            + (run.Phase == "idle" ? CacheWarmerMath.MaxIdleWarmingAgeMs : CacheWarmerMath.MaxWarmingAgeMs);
        if (run.NextWarmAt > deadline || NowMs() >= deadline)
        {
            Stop(run.Phase == "idle" ? "30-minute idle safety limit reached" : "one-hour safety limit reached");
            return;
        }

        var delay = Math.Max(0, run.NextWarmAt - NowMs());
        run.Timer = new Timer(_ => _ = RefreshAsync(run), null, delay, Timeout.Infinite);
    }

    /// <summary>执行一次刷新。对应 TS 私有 <c>refresh(run)</c>（测试直接驱动）。</summary>
    internal async Task RefreshAsync(ActiveRun run)
    {
        run.Timer?.Dispose();
        run.Timer = null;
        if (!ValidateRun(run))
        {
            return;
        }

        if (RefreshDeadlineMissed(run))
        {
            return;
        }

        var decision = Evaluate(run);
        var action = decision.Action;
        try
        {
            action = await _decide(new CacheWarmingDecisionEvent
            {
                WarmCost = decision.WarmCost,
                MissCost = decision.MissCost,
                ContinuationProbability = decision.ContinuationProbability,
                Action = action,
            }).ConfigureAwait(false);
        }
        catch
        {
            // 扩展失败时回落到 pi 自己的决策。
        }

        if (!ValidateRun(run) || RefreshDeadlineMissed(run))
        {
            return;
        }

        var extensionOverride = action != decision.Action;
        if (action == CacheWarmingAction.Stop)
        {
            var reason = extensionOverride
                ? "stopped by extension"
                : decision.EconomicsAvailable
                    ? "expected savings below threshold"
                    : "cache economics unavailable";
            Stop(reason, new CacheWarmingStopDetail(decision, extensionOverride));
            return;
        }

        run.ExtensionOverride = extensionOverride;
        try
        {
            var options = new Dictionary<string, object?>(
                run.Request.RequestOptions ?? CompatShims.ToDictionary(run.Request.Options),
                StringComparer.Ordinal)
            {
                ["maxTokens"] = 1,
                ["maxRetries"] = 0,
                ["signal"] = run.Controller.Token,
            };
            var message = await _streamSimple(run.Request.Model, run.Request.Context.Messages, options)
                .WaitForDoneAsync()
                .ConfigureAwait(false);
            if (!ValidateRun(run))
            {
                return;
            }

            if (message.StopReason != StopReason.Error && message.StopReason != StopReason.Aborted)
            {
                var usage = message.UsageStats;
                if (usage is not null)
                {
                    var entry = _appendUsage(
                        "cache_warm",
                        message.Provider ?? run.Request.Model.Provider,
                        message.ResponseModel ?? message.Model ?? run.Request.Model.Id,
                        usage,
                        extensionOverride ? "extension override" : null);
                    OnWarmed?.Invoke(entry);
                }
            }
        }
        catch
        {
            // 缓存预热是尽力而为，绝不能影响正在进行的 agent run。
        }

        if (ReferenceEquals(_run, run))
        {
            Schedule(run);
        }
    }

    private bool RefreshDeadlineMissed(ActiveRun run)
    {
        if (NowMs() <= run.RefreshDeadlineAt)
        {
            return false;
        }

        Stop("cache refresh deadline missed");
        return true;
    }

    private bool ValidateRun(ActiveRun run)
    {
        if (!ReferenceEquals(_run, run))
        {
            return false;
        }

        var reason = GetModeStopReason(run)
            ?? (!run.IsCurrent() ? "conversation context changed" : null);
        if (reason is null)
        {
            return true;
        }

        Stop(reason);
        return false;
    }

    private string? GetModeStopReason(ActiveRun run)
    {
        var mode = _getMode();
        if (mode == "off")
        {
            return "cache warming disabled";
        }

        return mode == "streaming" && run.Phase == "idle" ? "agent run settled" : null;
    }

    private CacheWarmingDecision Evaluate(ActiveRun run)
    {
        var model = run.Request.Model;
        var promptTokens = CacheWarmerMath.LastPromptTokens(_getBranch());
        var cacheHitCost = CacheWarmerMath.Price(model, cacheRead: promptTokens);
        var cacheMissCost = CacheWarmerMath.Price(
            model,
            input: model.Cost?.CacheWrite is > 0 ? 0 : promptTokens,
            cacheWrite: model.Cost?.CacheWrite is > 0 ? promptTokens : 0);
        var warmCost = CacheWarmerMath.Price(model, output: 1, cacheRead: promptTokens);
        var missCost = Math.Max(0, cacheMissCost - cacheHitCost);
        var continuationProbability = run.Phase == "idle" ? CacheWarmerMath.IdleContinuationProbability : 1;
        var economicsAvailable = promptTokens > 0 && (cacheHitCost > 0 || cacheMissCost > 0);
        var expectedSavings = continuationProbability * missCost - warmCost;
        return new CacheWarmingDecision
        {
            Phase = run.Phase,
            WarmCost = warmCost,
            MissCost = missCost,
            ContinuationProbability = continuationProbability,
            ExpectedSavings = expectedSavings,
            EconomicsAvailable = economicsAvailable,
            Action = expectedSavings >= CacheWarmerMath.MinimumExpectedSavings
                ? CacheWarmingAction.Warm
                : CacheWarmingAction.Stop,
        };
    }

    /// <summary>对应 TS <c>ActiveRun</c>：一次进行中的保活。</summary>
    internal sealed class ActiveRun(CacheWarmRequest request, Func<bool> isCurrent, int ttlMs, int delayMs)
    {
        public CacheWarmRequest Request { get; } = request;

        /// <summary>会话的模型或消息不再匹配该请求时为 false。对应 TS <c>isCurrent</c>。</summary>
        public Func<bool> IsCurrent { get; } = isCurrent;

        public int TtlMs { get; } = ttlMs;

        public int DelayMs { get; } = delayMs;

        /// <summary>发送本次刷新的最晚安全时刻（留一半原定过期余量）。</summary>
        public long RefreshDeadlineAt { get; set; }

        public long StartedAt { get; init; }

        public CancellationTokenSource Controller { get; } = new();

        /// <summary><c>"streaming"</c> 或 <c>"idle"</c>。</summary>
        public string Phase { get; set; } = "streaming";

        public long NextWarmAt { get; set; }

        /// <summary>扩展强制发起的一次刷新正在进行时为 true。</summary>
        public bool ExtensionOverride { get; set; }

        public Timer? Timer { get; set; }
    }
}
