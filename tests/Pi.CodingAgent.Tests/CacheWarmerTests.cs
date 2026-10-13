using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 对照 TS <c>packages/coding-agent/test/cache-warmer.test.ts</c>。
/// </summary>
/// <remarks>
/// 两处测试手法差异（TS 用 vitest fake timers）：
/// ① 时钟由 <c>CacheWarmer.NowMs</c>（internal）注入，替代 <c>vi.setSystemTime</c>；
/// ② 「推进定时器」改为直接驱动 <c>CacheWarmer.RefreshAsync</c>（internal），
/// 因为真实 <see cref="Timer"/> 无法在单测里快进。定时器本身的排定只断言 <c>Status.NextWarmAt</c>。
/// </remarks>
public sealed class CacheWarmerTests
{
    // 与 TS 测试所用的 claude-opus-4-6 同费率（每 MTok）：input 5 / output 25 / cacheRead 0.5 /
    // cacheWrite 6.25。TS 断言 missCost≈0.575、warmCost≈0.050025（prompt 100k）由此推出。
    private static ModelSpec Spec(
        string api = "anthropic-messages",
        JsonObject? promptCache = null,
        JsonObject? compat = null) =>
        new()
        {
            Id = "m",
            Name = "m",
            Api = api,
            Provider = "p",
            BaseUrl = "https://example.test",
            Cost = new ModelCostRates(5, 25, 0.5, 6.25),
            Compat = compat,
            Extra = promptCache is null ? null : new JsonObject { ["promptCache"] = promptCache },
        };

    // 每次新建：JsonNode 只能有一个父节点，共享实例会在第二次挂载时抛「node already has a parent」。
    private static JsonObject ShortLong() => new() { ["short"] = 300, ["long"] = 3600 };

    private static ModelSpec AdaptiveModel =>
        Spec(promptCache: ShortLong(), compat: new JsonObject { ["forceAdaptiveThinking"] = true });
    private static ModelSpec BudgetModel => Spec(promptCache: ShortLong());
    private static ModelSpec OpenAiModel => Spec("openai-completions", promptCache: ShortLong());
    private static ModelSpec UnknownModel => Spec(promptCache: null);

    private static Usage WarmUsage => new(0, 1, 100, 0)
    {
        TotalTokens = 101,
        Cost = new UsageCost(0, 0, 0.01, 0, 0.01),
    };

    private static AssistantMessage Response(ModelSpec model, StopReason stopReason = StopReason.Length)
        => new(
            Content: [],
            StopReason: stopReason,
            UsageStats: WarmUsage,
            Model: model.Id,
            Api: model.Api,
            Provider: model.Provider);

    private static IReadOnlyList<SessionEntry> BranchWithPrompt(long promptTokens) =>
    [
        new SessionMessageEntry
        {
            Id = "a",
            ParentId = null,
            Timestamp = "1970-01-01T00:00:00.000Z",
            Message = Response(AdaptiveModel) with
            {
                UsageStats = WarmUsage with { Output = 10, CacheRead = promptTokens, TotalTokens = promptTokens + 10 },
            },
        },
    ];

    private sealed class Harness
    {
        public required CacheWarmer Warmer { get; init; }

        public required List<CacheWarmCall> Calls { get; init; }

        public required List<CacheWarmingDecisionEvent> Events { get; init; }

        public required List<UsageEntry> WarmedEntries { get; init; }

        public required SessionManager UsageManager { get; init; }

        public required State State { get; init; }
    }

    private sealed class State
    {
        public string Mode { get; set; } = "idle";

        public IReadOnlyList<SessionEntry> Branch { get; set; } = BranchWithPrompt(100_000);
    }

    private sealed record CacheWarmCall(ModelSpec Model, IReadOnlyDictionary<string, object?> Options);

    private static Harness CreateHarness(
        Func<ModelSpec, Task<AssistantMessage>>? result = null,
        Func<CacheWarmingDecisionEvent, string>? decide = null,
        string mode = "idle",
        IReadOnlyList<SessionEntry>? branch = null)
    {
        var calls = new List<CacheWarmCall>();
        var events = new List<CacheWarmingDecisionEvent>();
        var warmed = new List<UsageEntry>();
        var usageManager = SessionManager.InMemory();
        var state = new State { Mode = mode, Branch = branch ?? BranchWithPrompt(100_000) };

        var warmer = new CacheWarmer(
            (model, _context, options) =>
            {
                calls.Add(new CacheWarmCall(model, options));
                var stream = new AssistantMessageEventStream();
                var message = (result ?? (m => Task.FromResult(Response(m))))(model);
                if (message.IsCompletedSuccessfully)
                {
                    stream.Push(new AssistantMessageEvent.Done(message.Result.StopReason, message.Result));
                    stream.End(message.Result);
                }
                else
                {
                    _ = message.ContinueWith(
                        task =>
                        {
                            stream.Push(new AssistantMessageEvent.Done(task.Result.StopReason, task.Result));
                            stream.End(task.Result);
                        },
                        TaskScheduler.Default);
                }

                return stream;
            },
            usageManager.AppendUsage,
            () => state.Branch,
            () => state.Mode,
            decisionEvent =>
            {
                events.Add(decisionEvent);
                return Task.FromResult(decide?.Invoke(decisionEvent) ?? decisionEvent.Action);
            });

        warmer.OnWarmed = warmed.Add;
        return new Harness
        {
            Warmer = warmer,
            Calls = calls,
            Events = events,
            WarmedEntries = warmed,
            UsageManager = usageManager,
            State = state,
        };
    }

    private static CacheWarmRequest Request(
        ModelSpec? model = null,
        SimpleStreamOptions? options = null,
        IReadOnlyDictionary<string, object?>? requestOptions = null)
        => new()
        {
            Model = model ?? AdaptiveModel,
            Context = new TranscriptContext([]),
            Options = options ?? new SimpleStreamOptions(),
            RequestOptions = requestOptions,
        };

    private static Func<bool> Current => static () => true;

    // =========================================================================
    // 纯函数
    // =========================================================================

    [Fact]
    public void DerivesEligibilityAndTimingFromRetentionAndProviderBehavior()
    {
        // TS：未显式给 retention 时回落 "short"（不是「不可用」）。
        Assert.Equal(300_000, CacheWarmerMath.GetPromptCacheTtlMs(AdaptiveModel, null));
        Assert.Equal(3_600_000, CacheWarmerMath.GetPromptCacheTtlMs(
            AdaptiveModel, new SimpleStreamOptions { CacheRetention = "long" }));
        Assert.Null(CacheWarmerMath.GetPromptCacheTtlMs(
            AdaptiveModel, new SimpleStreamOptions { CacheRetention = "none" }));
        Assert.Equal(3_600_000, CacheWarmerMath.GetPromptCacheTtlMs(
            AdaptiveModel,
            new SimpleStreamOptions { Env = new Dictionary<string, string> { ["PI_CACHE_RETENTION"] = "long" } }));
        Assert.Equal(3_600_000, CacheWarmerMath.GetPromptCacheTtlMs(
            OpenAiModel, new SimpleStreamOptions { CacheRetention = "long" }));
        Assert.Null(CacheWarmerMath.GetPromptCacheTtlMs(UnknownModel, null));

        Assert.Equal(270_000, CacheWarmerMath.GetCacheWarmingDelayMs(300_000));
        Assert.Equal(50_000, CacheWarmerMath.GetCacheWarmingDelayMs(60_000));
        Assert.Null(CacheWarmerMath.GetCacheWarmingDelayMs(10_000));

        Assert.False(CacheWarmerMath.IsReplayable(BudgetModel, new SimpleStreamOptions { Reasoning = "medium" }));
        Assert.True(CacheWarmerMath.IsReplayable(BudgetModel, null));
        Assert.True(CacheWarmerMath.IsReplayable(AdaptiveModel, new SimpleStreamOptions { Reasoning = "medium" }));
        Assert.True(CacheWarmerMath.IsReplayable(OpenAiModel, new SimpleStreamOptions { Reasoning = "medium" }));
    }

    [Fact]
    public void Start_ArmsTimerAtTheRetentionDerivedDelay()
    {
        var harness = CreateHarness();
        harness.Warmer.NowMs = static () => 1_000;
        harness.Warmer.Start(Request(), Current);

        Assert.Equal("scheduled", harness.Warmer.Status.State);
        Assert.Equal(1_000 + 270_000, harness.Warmer.Status.NextWarmAt);
        harness.Warmer.Cancel();
    }

    // =========================================================================
    // 刷新
    // =========================================================================

    [Fact]
    public async Task ReplaysProfitableRequestsAndPreservesOptionsAcrossRepeatedRefreshes()
    {
        var harness = CreateHarness();
        var model = AdaptiveModel;
        var originalSignal = new CancellationTokenSource();
        var transformHeaders = new Func<IReadOnlyDictionary<string, string?>, Task<IReadOnlyDictionary<string, string?>>>(
            _ => Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>()));
        var requestOptions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reasoning"] = "high",
            ["sessionId"] = "s",
            ["transformHeaders"] = transformHeaders,
            ["signal"] = originalSignal.Token,
        };

        harness.Warmer.Start(
            Request(
                model: model,
                options: new SimpleStreamOptions { Reasoning = "high", SessionId = "s", Signal = originalSignal.Token },
                requestOptions: requestOptions),
            Current);

        await harness.Warmer.RefreshAsync(harness.Warmer.Run!);

        var options = harness.Calls[0].Options;
        Assert.Same(model, harness.Calls[0].Model);
        Assert.Equal("high", options["reasoning"]);
        Assert.Equal("s", options["sessionId"]);
        Assert.Same(transformHeaders, options["transformHeaders"]);
        Assert.Equal(1, options["maxTokens"]);
        Assert.Equal(0, options["maxRetries"]);
        // TS：预热请求带自己的 controller.signal，不沿用原请求的 signal。
        Assert.NotEqual(originalSignal.Token, (CancellationToken)options["signal"]!);
        Assert.False(((CancellationToken)options["signal"]!).IsCancellationRequested);

        Assert.Equal("cache_warming_decision", harness.Events[0].Type);
        Assert.Equal(1, harness.Events[0].ContinuationProbability);
        Assert.Equal(CacheWarmingAction.Warm, harness.Events[0].Action);
        Assert.Equal(0.575, harness.Events[0].MissCost, 9);
        Assert.Equal(0.050025, harness.Events[0].WarmCost, 9);

        var appended = Assert.Single(harness.UsageManager.GetBranch().OfType<UsageEntry>());
        Assert.Equal("cache_warm", appended.Kind);
        Assert.Equal(AdaptiveModel.Provider, appended.Provider);
        Assert.Equal(AdaptiveModel.Id, appended.Model);
        Assert.Equal(WarmUsage, appended.Usage);
        Assert.Null(appended.Note);
        Assert.Same(appended, Assert.Single(harness.WarmedEntries));

        // 第二次刷新（每次成功后重新排定）。
        await harness.Warmer.RefreshAsync(harness.Warmer.Run!);
        Assert.Equal(2, harness.Calls.Count);
        harness.Warmer.Cancel();
    }

    [Fact]
    public async Task DoesNotIssueRefreshesAfterTheirSafeDeadline()
    {
        var harness = CreateHarness();
        harness.Warmer.NowMs = static () => 0;
        harness.Warmer.Start(Request(), Current);

        // 五分钟的缓存排定在 4m30s，保留 30s 过期余量中的 15s。模拟被休眠拖迟的定时器。
        harness.Warmer.NowMs = static () => 285_001;
        await harness.Warmer.RefreshAsync(harness.Warmer.Run!);

        Assert.Empty(harness.Calls);
        Assert.Equal("inactive", harness.Warmer.Status.State);
        Assert.Equal("cache refresh deadline missed", harness.Warmer.Status.Reason);
    }

    [Fact]
    public async Task RechecksTheDeadlineAfterAnExtensionDecision()
    {
        Harness? harness = null;
        harness = CreateHarness(decide: _ =>
        {
            // 扩展决策期间时钟越过安全期限。
            harness!.Warmer.NowMs = static () => 285_001;
            return CacheWarmingAction.Warm;
        });
        harness.Warmer.NowMs = static () => 0;
        harness.Warmer.Start(Request(), Current);

        await harness.Warmer.RefreshAsync(harness.Warmer.Run!);

        Assert.Empty(harness.Calls);
        Assert.Equal("cache refresh deadline missed", harness.Warmer.Status.Reason);
    }

    [Fact]
    public async Task AppliesEconomicDecisionsAndExtensionOverrides()
    {
        // 不划算：提示词只有 5k token，预期节省低于 $0.05。
        var unprofitable = CreateHarness(branch: BranchWithPrompt(5_000));
        unprofitable.Warmer.Start(Request(), Current);
        await unprofitable.Warmer.RefreshAsync(unprofitable.Warmer.Run!);
        Assert.Empty(unprofitable.Calls);
        Assert.Equal("inactive", unprofitable.Warmer.Status.State);
        Assert.Equal("stop", unprofitable.Warmer.Status.Decision?.Action);
        Assert.True(unprofitable.Warmer.Status.Decision?.EconomicsAvailable);
        Assert.False(unprofitable.Warmer.Status.ExtensionOverride);

        // 扩展强制发起。
        var forced = CreateHarness(branch: BranchWithPrompt(5_000), decide: _ => CacheWarmingAction.Warm);
        forced.Warmer.Start(Request(), Current);
        await forced.Warmer.RefreshAsync(forced.Warmer.Run!);
        Assert.Single(forced.Calls);
        Assert.Equal("extension override", Assert.Single(forced.WarmedEntries).Note);
        forced.Warmer.Cancel();

        // 扩展否决。
        var vetoed = CreateHarness(decide: _ => CacheWarmingAction.Stop);
        vetoed.Warmer.Start(Request(), Current);
        await vetoed.Warmer.RefreshAsync(vetoed.Warmer.Run!);
        Assert.Empty(vetoed.Calls);
        Assert.Equal("inactive", vetoed.Warmer.Status.State);
        Assert.True(vetoed.Warmer.Status.ExtensionOverride);

        // 经济学不可用：分支上没有助手用量。
        var unavailable = CreateHarness(branch: BranchWithPrompt(0));
        unavailable.Warmer.Start(Request(), Current);
        Assert.Equal("inactive", unavailable.Warmer.Status.State);
        Assert.Equal("cache economics unavailable", unavailable.Warmer.Status.Reason);
        await unavailable.Warmer.RefreshAsync(unavailable.Warmer.Run!);
        Assert.Empty(unavailable.Calls);
    }

    [Fact]
    public async Task StopsForUnsupportedRequestsContextChangesAndModeChanges()
    {
        var unsupported = CreateHarness();
        unsupported.State.Mode = "off";
        unsupported.Warmer.Start(Request(), Current);
        Assert.Equal("cache warming disabled", unsupported.Warmer.Status.Reason);

        unsupported.State.Mode = "idle";
        unsupported.Warmer.Start(Request(model: UnknownModel), Current);
        Assert.Equal("cache lifetime unavailable", unsupported.Warmer.Status.Reason);

        unsupported.Warmer.Start(
            Request(model: BudgetModel, options: new SimpleStreamOptions { Reasoning = "high" }), Current);
        Assert.Equal("request cannot be replayed safely", unsupported.Warmer.Status.Reason);

        var stillCurrent = true;
        unsupported.Warmer.Start(Request(), () => stillCurrent);
        stillCurrent = false;
        Assert.Equal("conversation context changed", unsupported.Warmer.Status.Reason);

        unsupported.Warmer.Start(Request(), Current);
        unsupported.State.Mode = "off";
        await unsupported.Warmer.RefreshAsync(unsupported.Warmer.Run!);
        Assert.Empty(unsupported.Calls);

        var streaming = CreateHarness(mode: "streaming", branch: BranchWithPrompt(400_000));
        streaming.Warmer.Start(Request(), Current);
        streaming.Warmer.OnAgentSettled();
        Assert.Equal("agent run settled", streaming.Warmer.Status.Reason);
    }

    [Fact]
    public async Task AbortsReplacedRequestsAndDoesNotRecordFailedRefreshes()
    {
        var pending = new TaskCompletionSource<AssistantMessage>();
        var harness = CreateHarness(result: _ => pending.Task);
        harness.Warmer.Start(Request(), Current);

        var refresh = harness.Warmer.RefreshAsync(harness.Warmer.Run!);
        // 替换 run：旧 run 的 controller 应被取消。
        harness.Warmer.Start(Request(), Current);
        Assert.True(((CancellationToken)harness.Calls[0].Options["signal"]!).IsCancellationRequested);

        pending.SetResult(Response(AdaptiveModel));
        await refresh;
        harness.Warmer.Cancel();
        await Task.Delay(30);
        Assert.Single(harness.Calls);
        Assert.Empty(harness.UsageManager.GetBranch().OfType<UsageEntry>());

        var failed = CreateHarness(result: m => Task.FromResult(Response(m, StopReason.Error)));
        failed.Warmer.Start(Request(), Current);
        await failed.Warmer.RefreshAsync(failed.Warmer.Run!);
        Assert.Empty(failed.UsageManager.GetBranch().OfType<UsageEntry>());
        failed.Warmer.Cancel();
    }

    // =========================================================================
    // 文案
    // =========================================================================

    [Fact]
    public void FormatsStatusAndUsageEntries()
    {
        var decision = new CacheWarmingDecision
        {
            Phase = "idle",
            WarmCost = 0.013,
            MissCost = 0.621,
            ContinuationProbability = 0.6,
            ExpectedSavings = 0.36,
            EconomicsAvailable = true,
            Action = CacheWarmingAction.Warm,
        };
        Assert.Equal(
            "Decision in 3m 42s (60% continuation probability, expected savings $0.360 >= $0.050 -> warm)",
            CacheWarmerMath.FormatCacheWarmingStatus(
                new CacheWarmingStatus { State = "scheduled", NextWarmAt = 222_000, Decision = decision },
                0));

        var manager = SessionManager.InMemory();
        var entry = manager.AppendUsage(
            "cache_warm",
            AdaptiveModel.Provider,
            AdaptiveModel.Id,
            WarmUsage with
            {
                Cost = new UsageCost(0.00004, 0.00005, 0.02940725, 0, 0.02949725),
            },
            "extension override");
        Assert.Equal("Cache warmed (extension override): $0.029497", CacheWarmerMath.FormatCacheWarmingUsage(entry));
    }

    [Fact]
    public void FormatCacheWarmingUsage_KeepsAtLeastThreeDecimals()
    {
        var manager = SessionManager.InMemory();
        var entry = manager.AppendUsage(
            "cache_warm", "p", "m", WarmUsage with { Cost = new UsageCost(0, 0, 0, 0, 0.01) }, null);
        // TS：`toFixed(6)` 后去掉尾随零，但至少保留 3 位小数。
        Assert.Equal("Cache warmed: $0.010", CacheWarmerMath.FormatCacheWarmingUsage(entry));
    }

    [Fact]
    public void FormatCacheWarmingStatus_InactiveWithoutEconomics()
    {
        var text = CacheWarmerMath.FormatCacheWarmingStatus(new CacheWarmingStatus
        {
            State = "inactive",
            Reason = "cache warming disabled",
        });
        Assert.Equal("Inactive (cache warming disabled)", text);
    }
}
