using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests.Providers;

/// <summary>
/// faux provider 的 S3c 面：模型目录、响应工厂、随机切块、限速、四个块边界事件、
/// 中止中断、延后响应全链、<c>fauxProvider()</c> 真实 provider 句柄。
/// 对应 TS <c>providers/faux.ts</c> 里除用量估算（见 <see cref="FauxUsageEstimateTests"/>）之外的全部行为。
/// </summary>
public class FauxProviderTests
{
    private static UserMessage UserText(string text) => new(MessageContent.From(text), 0);

    private static async Task<List<AssistantMessageEvent>> CollectAsync(IAssistantMessageEventStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream)
        {
            events.Add(@event);
        }

        return events;
    }

    private static ModelSpec Model(Faux.FauxCore core) => core.GetModel()!;

    // ---------- 模型目录（对应 options.models + getModel 重载） ----------

    [Fact]
    public void DefaultCatalogIsASingleFauxModel()
    {
        var core = new Faux.FauxCore();

        var model = Assert.Single(core.Models);
        Assert.Equal("faux-1", model.Id);
        Assert.Equal("Faux Model", model.Name);
        Assert.Equal(core.Api, model.Api);
        Assert.Equal("faux", model.Provider);
        // TS DEFAULT_BASE_URL。
        Assert.Equal("http://localhost:0", model.BaseUrl);
        Assert.False(model.Reasoning);
        Assert.Equal(128000, model.ContextWindow);
        Assert.Equal(16384, model.MaxTokens);
        Assert.Equal(["text", "image"], model.Input);
    }

    [Fact]
    public void CustomModelDefinitionsOverrideTheDefaultsAndKeepTheirOrder()
    {
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions
        {
            Models =
            [
                new Faux.FauxModelDefinition { Id = "m1" },
                new Faux.FauxModelDefinition
                {
                    Id = "m2",
                    Name = "Second",
                    Reasoning = true,
                    Input = ["text"],
                    Cost = new ModelCostRates(1, 2, 3, 4),
                    ContextWindow = 4096,
                    MaxTokens = 512,
                },
            ],
        });

        Assert.Equal(["m1", "m2"], core.Models.Select(model => model.Id));

        // 未给 name 的条目回落到 id（TS `definition.name ?? definition.id`）。
        var first = core.Models[0];
        Assert.Equal("m1", first.Name);
        Assert.False(first.Reasoning);
        Assert.Equal(128000, first.ContextWindow);

        var second = core.Models[1];
        Assert.Equal("Second", second.Name);
        Assert.True(second.Reasoning);
        Assert.Equal(["text"], second.Input);
        Assert.Equal(new ModelCostRates(1, 2, 3, 4), second.Cost);
        Assert.Equal(4096, second.ContextWindow);
        Assert.Equal(512, second.MaxTokens);
    }

    [Fact]
    public void GetModelFallsBackToTheFirstEntryAndReturnsNullForUnknownIds()
    {
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions
        {
            Models = [new Faux.FauxModelDefinition { Id = "m1" }, new Faux.FauxModelDefinition { Id = "m2" }],
        });

        Assert.Equal("m1", core.GetModel()!.Id);
        Assert.Equal("m1", core.GetModel(null)!.Id);
        Assert.Equal("m2", core.GetModel("m2")!.Id);
        Assert.Null(core.GetModel("nope"));
    }

    // ---------- 响应工厂与调用计数 ----------

    [Fact]
    public async Task FactoryStepsReceiveTheContextOptionsStateAndModel()
    {
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions { Provider = "custom" });
        var seen = new List<string>();
        core.SetResponses([
            new Faux.FactoryStep((context, options, state, model) =>
            {
                seen.Add($"{state.CallCount}|{model.Id}|{model.Provider}|{context.Messages.Count}|{options?.SessionId}");
                return Task.FromResult(Faux.ScriptedMessage("from factory"));
            }),
        ]);

        var options = new SimpleStreamOptions(SessionId: "s-1");
        var message = await core.Stream(Model(core), new TranscriptContext([UserText("hi")]), options)
            .WaitForDoneAsync();

        // 工厂在 callCount 自增**之后**被调用（TS 先 `state.callCount++` 再走 microtask）。
        Assert.Equal("1|faux-1|custom|1|s-1", Assert.Single(seen));
        Assert.Equal("from factory", Assert.IsType<TextContent>(message.Content[0]).Text);
        Assert.Equal(1, core.State.CallCount);
    }

    [Fact]
    public async Task CallCountCountsEveryStreamCallIncludingTheExhaustedOne()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("a")]);

        _ = await core.Stream(Model(core), new TranscriptContext([])).WaitForDoneAsync();
        var exhausted = await core.Stream(Model(core), new TranscriptContext([])).WaitForDoneAsync();

        Assert.Equal(2, core.State.CallCount);
        Assert.Equal(StopReason.Error, exhausted.StopReason);
    }

    // ---------- 随机切块（tokenSize + splitStringByTokenSize） ----------

    [Fact]
    public async Task TokenSizeOneProducesFourCharacterChunks()
    {
        // min = max = 1 → charSize = max(1, 1*4) = 4，切块完全确定。
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions { TokenSize = new Faux.FauxTokenSize { Min = 1, Max = 1 } });
        core.SetResponses([Faux.Response.Text("abcdefghij")]);

        var deltas = (await CollectAsync(core.Stream(Model(core), new TranscriptContext([]))))
            .OfType<AssistantMessageEvent.TextDelta>()
            .Select(delta => delta.Delta)
            .ToList();

        Assert.Equal(["abcd", "efgh", "ij"], deltas);
    }

    [Fact]
    public async Task TokenSizeMinIsClampedUpToOneAndToMax()
    {
        // TS：min = max(1, min(min, max))；max = max(min, max)。min=10/max=2 → 两者都是 2 → charSize = 8。
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions { TokenSize = new Faux.FauxTokenSize { Min = 10, Max = 2 } });
        core.SetResponses([Faux.Response.Text(new string('x', 20))]);

        var deltas = (await CollectAsync(core.Stream(Model(core), new TranscriptContext([]))))
            .OfType<AssistantMessageEvent.TextDelta>()
            .ToList();

        Assert.Equal([8, 8, 4], deltas.Select(delta => delta.Delta.Length));
    }

    [Fact]
    public async Task DefaultTokenSizeChunksStayWithinTwelveToTwentyCharacters()
    {
        // 缺省 min=3 / max=5 → tokenSize ∈ [3,5] → charSize ∈ {12,16,20}；末块可短。
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text(new string('y', 40))]);

        var deltas = (await CollectAsync(core.Stream(Model(core), new TranscriptContext([]))))
            .OfType<AssistantMessageEvent.TextDelta>()
            .ToList();

        Assert.True(deltas.Count >= 2);
        foreach (var delta in deltas.Take(deltas.Count - 1))
        {
            Assert.Contains(delta.Delta.Length, new[] { 12, 16, 20 });
        }
    }

    [Fact]
    public async Task EmptyTextStillEmitsOneEmptyChunk()
    {
        // TS：`chunks.length > 0 ? chunks : [""]` —— 空串也要有一个（空）块边界。
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions { TokenSize = new Faux.FauxTokenSize { Min = 1, Max = 1 } });
        core.SetResponses([Faux.Response.Text("")]);

        var deltas = (await CollectAsync(core.Stream(Model(core), new TranscriptContext([]))))
            .OfType<AssistantMessageEvent.TextDelta>()
            .ToList();

        Assert.Equal("", Assert.Single(deltas).Delta);
    }

    // ---------- 限速（tokensPerSecond + scheduleChunk） ----------

    [Fact]
    public async Task TokensPerSecondSchedulesADelayProportionalToTheChunkTokenCount()
    {
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions
        {
            TokensPerSecond = 500,
            TokenSize = new Faux.FauxTokenSize { Min = 1, Max = 1 },
        });
        var delays = new List<double>();
        core.DelayAsyncOverride = delayMs =>
        {
            delays.Add(delayMs);
            return Task.CompletedTask;
        };
        core.SetResponses([Faux.Response.Text("abcdefgh")]);

        _ = await CollectAsync(core.Stream(Model(core), new TranscriptContext([])));

        // 两个 4 字符块 → estimateTokens = 1 → 1 / 500 * 1000 = 2ms。
        Assert.Equal([2d, 2d], delays);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    public async Task WithoutAPositiveTokensPerSecondNoDelayIsScheduled(double? tokensPerSecond)
    {
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions
        {
            TokensPerSecond = tokensPerSecond,
            TokenSize = new Faux.FauxTokenSize { Min = 1, Max = 1 },
        });
        var delays = new List<double>();
        core.DelayAsyncOverride = delayMs =>
        {
            delays.Add(delayMs);
            return Task.CompletedTask;
        };
        core.SetResponses([Faux.Response.Text("abcd")]);

        _ = await CollectAsync(core.Stream(Model(core), new TranscriptContext([])));

        Assert.Empty(delays);
    }

    // ---------- 块边界事件（四个 *_start / *_end） ----------

    [Fact]
    public async Task TextBlocksEmitStartDeltaEndAroundTheChunks()
    {
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions { TokenSize = new Faux.FauxTokenSize { Min = 1, Max = 1 } });
        core.SetResponses([Faux.Response.Text("abcd")]);

        var events = await CollectAsync(core.Stream(Model(core), new TranscriptContext([])));

        Assert.Collection(
            events,
            @event => Assert.IsType<AssistantMessageEvent.Start>(@event),
            @event => Assert.Equal(0, Assert.IsType<AssistantMessageEvent.TextStart>(@event).ContentIndex),
            @event => Assert.Equal("abcd", Assert.IsType<AssistantMessageEvent.TextDelta>(@event).Delta),
            @event =>
            {
                var end = Assert.IsType<AssistantMessageEvent.TextEnd>(@event);
                Assert.Equal(0, end.ContentIndex);
                Assert.Equal("abcd", end.Content);
            },
            @event => Assert.Equal(StopReason.Stop, Assert.IsType<AssistantMessageEvent.Done>(@event).Reason));
    }

    [Fact]
    public async Task ThinkingBlocksEmitStartDeltaEndWithTheFullContentAtTheEnd()
    {
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions { TokenSize = new Faux.FauxTokenSize { Min = 1, Max = 1 } });
        core.SetResponses([new Faux.Response([Faux.Thinking("abcdefgh")])]);

        var events = await CollectAsync(core.Stream(Model(core), new TranscriptContext([])));

        Assert.Collection(
            events,
            @event => Assert.IsType<AssistantMessageEvent.Start>(@event),
            @event => Assert.Equal(0, Assert.IsType<AssistantMessageEvent.ThinkingStart>(@event).ContentIndex),
            @event => Assert.Equal("abcd", Assert.IsType<AssistantMessageEvent.ThinkingDelta>(@event).Delta),
            @event => Assert.Equal("efgh", Assert.IsType<AssistantMessageEvent.ThinkingDelta>(@event).Delta),
            @event =>
            {
                var end = Assert.IsType<AssistantMessageEvent.ThinkingEnd>(@event);
                // TS 的 thinking_end 带的是**整块**原文，不是累计值。
                Assert.Equal("abcdefgh", end.Content);
            },
            @event => Assert.IsType<AssistantMessageEvent.Done>(@event));
    }

    [Fact]
    public async Task ToolCallBlocksStreamTheArgumentsAsJsonAndSettleTheRealObjectAtTheEnd()
    {
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions { TokenSize = new Faux.FauxTokenSize { Min = 1, Max = 1 } });
        var arguments = new Dictionary<string, object?> { ["a"] = 1 };
        core.SetResponses([Faux.Response.ToolUse(Faux.ToolCall("echo", arguments, id: "call-1"))]);

        var events = await CollectAsync(core.Stream(Model(core), new TranscriptContext([])));

        Assert.Collection(
            events,
            @event => Assert.IsType<AssistantMessageEvent.Start>(@event),
            @event => Assert.Equal(0, Assert.IsType<AssistantMessageEvent.ToolCallStart>(@event).ContentIndex),
            // 参数以 JSON 文本增量流出：`{"a":1}` = 7 字符 → 两块 "{\"a\"" + ":1}"。
            @event => Assert.Equal("{\"a\"", Assert.IsType<AssistantMessageEvent.ToolCallDelta>(@event).Delta),
            @event => Assert.Equal(":1}", Assert.IsType<AssistantMessageEvent.ToolCallDelta>(@event).Delta),
            @event =>
            {
                var end = Assert.IsType<AssistantMessageEvent.ToolCallEnd>(@event);
                Assert.Equal("call-1", end.ToolCall.Id);
                Assert.Equal("echo", end.ToolCall.Name);
                Assert.Same(arguments, end.ToolCall.Arguments);
            },
            @event => Assert.Equal(StopReason.ToolUse, Assert.IsType<AssistantMessageEvent.Done>(@event).Reason));
    }

    [Fact]
    public async Task ToolCallDeltasCarryTheContentIndexInBothSlots()
    {
        // 与真实 provider 的约定一致（Google 系传 BlockIndex() 两次），不是工具调用序号。
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions { TokenSize = new Faux.FauxTokenSize { Min = 1, Max = 1 } });
        core.SetResponses([
            new Faux.Response([Faux.Text("x"), Faux.ToolCall("echo", null, id: "call-1")], StopReason.ToolUse),
        ]);

        var deltas = (await CollectAsync(core.Stream(Model(core), new TranscriptContext([]))))
            .OfType<AssistantMessageEvent.ToolCallDelta>()
            .ToList();

        Assert.NotEmpty(deltas);
        Assert.All(deltas, delta =>
        {
            Assert.Equal(1, delta.ContentIndex);
            Assert.Equal(1, delta.Index);
        });
    }

    // ---------- 中止（signal 检查 + createAbortedMessage） ----------

    [Fact]
    public async Task APreAbortedSignalYieldsOnlyAnAbortedTerminalWithoutStart()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("never")]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var events = await CollectAsync(core.Stream(Model(core), new TranscriptContext([]), null, cts.Token));

        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Aborted, error.Reason);
        Assert.Equal("Request was aborted", error.ErrorMessage);
        Assert.DoesNotContain(events, @event => @event is AssistantMessageEvent.Start);
    }

    [Fact]
    public async Task AbortingMidStreamKeepsTheAccumulatedPartialContent()
    {
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions
        {
            // 速率必须为正，延迟缝才会被调用（对应 TS 的 setTimeout 分支）。
            TokensPerSecond = 500,
            TokenSize = new Faux.FauxTokenSize { Min = 1, Max = 1 },
        });
        core.SetResponses([Faux.Response.Text("abcdefghij")]);
        using var cts = new CancellationTokenSource();
        // 第一个 chunk 的延迟里把令牌取消掉 → 延迟返回后立刻中止。
        core.DelayAsyncOverride = _ =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };

        var events = await CollectAsync(core.Stream(Model(core), new TranscriptContext([]), null, cts.Token));

        Assert.Collection(
            events,
            @event => Assert.IsType<AssistantMessageEvent.Start>(@event),
            @event => Assert.IsType<AssistantMessageEvent.TextStart>(@event),
            @event =>
            {
                var error = Assert.IsType<AssistantMessageEvent.Error>(@event);
                Assert.Equal(StopReason.Aborted, error.Reason);
                Assert.Equal("Request was aborted", error.ErrorMessage);
            });
    }

    [Fact]
    public async Task TheSignalOnTheOptionsIsAlsoAnAbortSource()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("never")]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var options = new SimpleStreamOptions { Signal = cts.Token };
        var events = await CollectAsync(core.Stream(Model(core), new TranscriptContext([]), options));

        Assert.Equal(StopReason.Aborted, Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events)).Reason);
    }

    [Fact]
    public async Task APendingStopReasonIsAScriptErrorNotATerminal()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([new Faux.Response([], StopReason.Pending)]);

        var events = await CollectAsync(core.Stream(Model(core), new TranscriptContext([])));

        var error = Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal("Faux response ended without a stop reason", error.ErrorMessage);
        Assert.DoesNotContain(events, @event => @event is AssistantMessageEvent.Done);
    }

    // ---------- 延后响应全链（deferred / fetchDeferred / cancelDeferred） ----------

    [Fact]
    public async Task ADeferredRequestReturnsAHandleCarryingTheRequestIdentityAndPollHint()
    {
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions
        {
            Provider = "custom",
            Deferred = new Faux.FauxDeferredOptions { PendingFetches = 0, PollAfterMs = 250 },
        });
        core.SetResponses([Faux.Response.Text("final")]);

        var options = new SimpleStreamOptions { Deferred = true };
        var message = await core.Stream(Model(core), new TranscriptContext([]), options).WaitForDoneAsync();

        Assert.Equal(StopReason.Deferred, message.StopReason);
        var handle = Assert.IsType<DeferredHandle>(message.Deferred);
        Assert.Equal("custom", handle.Provider);
        Assert.Equal("faux-1", handle.ModelId);
        Assert.Equal(core.Api, handle.Api);
        Assert.Equal(250, handle.PollAfterMs);
        Assert.StartsWith("deferred:", handle.Id);
    }

    [Fact]
    public async Task PendingFetchesReplayTheDeferredPlaceholderBeforeTheFinalAnswer()
    {
        var core = new Faux.FauxCore(new Faux.FauxProviderOptions
        {
            Deferred = new Faux.FauxDeferredOptions { PendingFetches = 1 },
        });
        core.SetResponses([Faux.Response.Text("final")]);

        var submission = new SimpleStreamOptions { Deferred = true };
        var first = await core.Stream(Model(core), new TranscriptContext([]), submission).WaitForDoneAsync();
        var handle = first.Deferred!;

        var stillDeferred = await core.FetchDeferred(Model(core), handle).WaitForDoneAsync();
        Assert.Equal(StopReason.Deferred, stillDeferred.StopReason);

        var final = await core.FetchDeferred(Model(core), handle).WaitForDoneAsync();
        Assert.Equal(StopReason.Stop, final.StopReason);
        Assert.Equal("final", Assert.IsType<TextContent>(final.Content[0]).Text);
        Assert.Equal(2, core.State.DeferredFetchCount);
    }

    [Fact]
    public async Task FetchDeferredRejectsUnknownHandles()
    {
        var core = new Faux.FauxCore();
        var handle = new DeferredHandle { Provider = "faux", ModelId = "faux-1", Api = core.Api, Id = "nope" };

        var events = await CollectAsync(core.FetchDeferred(Model(core), handle));

        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal("Unknown faux deferred response: nope", error.ErrorMessage);
    }

    [Fact]
    public async Task FetchDeferredRejectsHandlesWhoseIdentityDoesNotMatchTheEntry()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("final")]);
        var first = await core.Stream(
            Model(core), new TranscriptContext([]), new SimpleStreamOptions { Deferred = true }).WaitForDoneAsync();
        var handle = first.Deferred!;

        // TS 同时比较 provider / modelId / api 三者，任一不符都算「未知句柄」。
        var tampered = handle with { ModelId = "other" };
        var events = await CollectAsync(core.FetchDeferred(Model(core), tampered));

        Assert.Equal(
            $"Unknown faux deferred response: {handle.Id}",
            Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events)).ErrorMessage);
    }

    [Fact]
    public async Task CancelDeferredMarksTheEntryAndRecordsASnapshotOfTheHandle()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("final")]);
        var first = await core.Stream(
            Model(core), new TranscriptContext([]), new SimpleStreamOptions { Deferred = true }).WaitForDoneAsync();
        var handle = first.Deferred!;

        await core.CancelDeferredAsync(Model(core), handle);

        var recorded = Assert.Single(core.State.CancelledDeferred);
        Assert.Equal(handle.Id, recorded.Id);

        var events = await CollectAsync(core.FetchDeferred(Model(core), handle));
        Assert.Equal(
            $"Faux deferred response was cancelled: {handle.Id}",
            Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events)).ErrorMessage);
    }

    [Fact]
    public async Task TheDeferredSubmissionStripsDeferredSignalAndOnResponseFromTheFinalRequest()
    {
        var core = new Faux.FauxCore();
        var seen = new List<string?>();
        core.SetResponses([
            new Faux.FactoryStep((_, options, _, _) =>
            {
                // 提交时带 deferred；取回最终结果时这三个字段已被剥掉，但 sessionId 仍在。
                seen.Add($"{options?.Deferred}|{options?.OnResponse is null}|{options?.SessionId}");
                return Task.FromResult(Faux.ScriptedMessage("final"));
            }),
        ]);

        var responses = 0;
        var submission = new SimpleStreamOptions(SessionId: "s-1")
        {
            Deferred = true,
            OnResponse = (_, _) =>
            {
                responses++;
                return Task.CompletedTask;
            },
        };
        var first = await core.Stream(Model(core), new TranscriptContext([]), submission).WaitForDoneAsync();

        // 提交那次没有跑工厂（脚本条目被存进延后登记表），所以这里只看到取回时的一次。
        // onResponse 也分两侧：提交走 streamOptions.onResponse，取回走 fetchOptions.onResponse。
        var fetchResponses = 0;
        _ = await core.FetchDeferred(
            Model(core),
            first.Deferred!,
            new SimpleStreamOptions
            {
                OnResponse = (_, _) =>
                {
                    fetchResponses++;
                    return Task.CompletedTask;
                },
            }).WaitForDoneAsync();

        Assert.Equal("False|True|s-1", Assert.Single(seen));
        Assert.Equal(1, responses);
        Assert.Equal(1, fetchResponses);
    }

    // ---------- fauxProvider()（真实 provider 句柄） ----------

    [Fact]
    public void CreateProviderBuildsARealProviderWithAHappyPathApiKeyAuth()
    {
        var handle = Faux.CreateProvider(new Faux.FauxProviderOptions
        {
            Models = [new Faux.FauxModelDefinition { Id = "m1" }, new Faux.FauxModelDefinition { Id = "m2" }],
        });

        Assert.Equal("faux", handle.Provider.Id);
        Assert.Equal("faux", handle.Provider.Name);
        Assert.Equal("Faux", handle.Provider.Auth!.ApiKey!.Name);
        Assert.Equal(["m1", "m2"], handle.Provider.GetModels().Select(model => model.Id));
        Assert.True(handle.Provider.SupportsFetchDeferred);
        Assert.True(handle.Provider.SupportsCancelDeferred);
        Assert.Equal("m1", handle.GetModel(null)!.Id);
        Assert.Equal("m2", handle.GetModel("m2")!.Id);
    }

    [Fact]
    public async Task AProviderHandleWorksEndToEndThroughTheModelsFacade()
    {
        var handle = Faux.CreateProvider(new Faux.FauxProviderOptions { Provider = "faux-e2e" });
        var models = new Pi.Ai.Models.Models();
        models.SetProvider(handle.Provider);
        var model = models.GetModel("faux-e2e", "faux-1")!;

        handle.SetResponses([Faux.Response.Text("hello via provider")]);
        Assert.Equal(1, handle.GetPendingResponseCount());

        var message = await models.CompleteSimpleAsync(model, []);

        Assert.Equal("hello via provider", Assert.IsType<TextContent>(message.Content[0]).Text);
        Assert.Equal(0, handle.GetPendingResponseCount());
        Assert.Equal(1, handle.State.CallCount);
    }

    [Fact]
    public async Task ADeferredRoundTripWorksThroughTheModelsFacade()
    {
        var handle = Faux.CreateProvider(new Faux.FauxProviderOptions { Provider = "faux-deferred" });
        var models = new Pi.Ai.Models.Models();
        models.SetProvider(handle.Provider);
        var model = models.GetModel("faux-deferred", "faux-1")!;

        handle.SetResponses([Faux.Response.Text("deferred answer")]);

        var submission = new Dictionary<string, object?> { ["deferred"] = true };
        var pending = await models.Stream(model, [], submission).WaitForDoneAsync();

        Assert.Equal(StopReason.Deferred, pending.StopReason);
        var final = await models.FetchDeferredAsync(model, pending.Deferred!);
        Assert.Equal("deferred answer", Assert.IsType<TextContent>(final.Content[0]).Text);

        // 取消路径经 provider 分派同样可达。
        handle.SetResponses([Faux.Response.Text("second")]);
        var second = await models.Stream(model, [], submission).WaitForDoneAsync();
        await models.CancelDeferredAsync(model, second.Deferred!);
        Assert.Single(handle.State.CancelledDeferred);
    }

    // ---------- 脚本化消息构造与身份字段 ----------

    [Fact]
    public void ScriptedMessageNormalizesStringsBlocksAndLists()
    {
        var fromString = Faux.ScriptedMessage("hi");
        Assert.Equal("hi", Assert.IsType<TextContent>(Assert.Single(fromString.Content)).Text);
        Assert.Equal("faux", fromString.Api);
        Assert.Equal("faux", fromString.Provider);
        Assert.Equal("faux-1", fromString.Model);
        Assert.Equal(StopReason.Stop, fromString.StopReason);
        Assert.Equal(Faux.DefaultUsage, fromString.UsageStats);

        var fromBlock = Faux.ScriptedMessage(Faux.Thinking("t"));
        Assert.IsType<ThinkingContent>(Assert.Single(fromBlock.Content));

        var fromList = Faux.ScriptedMessage([Faux.Text("a"), Faux.Text("b")]);
        Assert.Equal(2, fromList.Content.Count);

        var withOptions = Faux.ScriptedMessage(
            "boom", StopReason.Error, errorMessage: "e", responseId: "r-1", timestamp: 1234L);
        Assert.Equal(StopReason.Error, withOptions.StopReason);
        Assert.Equal("e", withOptions.ErrorMessage);
        Assert.Equal("r-1", withOptions.ResponseId);
        Assert.Equal(1234, withOptions.Timestamp);
    }

    [Fact]
    public async Task ScriptedIdentityFieldsSurviveTheReplay()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([new Faux.Response([Faux.Text("x")], ResponseId: "resp-9", Timestamp: 4321L)]);

        var message = await core.Stream(Model(core), new TranscriptContext([])).WaitForDoneAsync();

        Assert.Equal("resp-9", message.ResponseId);
        // cloneMessage 的 `timestamp ?? Date.now()` 在脚本已给时间戳时保留原值。
        Assert.Equal(4321, message.Timestamp);
    }

    [Fact]
    public void DefaultUsageIsAllZero()
    {
        Assert.Equal(0, Faux.DefaultUsage.Input);
        Assert.Equal(0, Faux.DefaultUsage.Output);
        Assert.Equal(0, Faux.DefaultUsage.TotalTokens);
        Assert.Equal(UsageCost.Zero, Faux.DefaultUsage.Cost);
    }
}
