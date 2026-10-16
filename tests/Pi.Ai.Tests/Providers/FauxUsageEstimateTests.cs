using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests.Providers;

/// <summary>
/// faux provider 的提示文本 / 用量估算子系统测试。
/// 对应 TS <c>providers/faux.ts</c> 的 <c>estimateTokens</c> / <c>messageToText</c> /
/// <c>joinedLength</c> / <c>commonPromptPrefixLength</c> / <c>withUsageEstimate</c>
/// （上游 <c>fe11328b0</c>：提示缓存改为「逐条消息比较」）。
/// </summary>
/// <remarks>
/// 全部期望值都是按 TS 源码手算出来的，而不是从 C# 实现回抄——这样断言才有判别力。
/// 估算口径：<c>estimateTokens(text) = ceil(len / 4)</c>（**不是** <c>Utils/Estimate</c> 的 3.5）；
/// 提示文本 = 每条消息 <c>"{role}:{messageToText}"</c>，整段按空行拼接，长度 <c>(n-1)*2 + Σlen</c>。
/// </remarks>
public class FauxUsageEstimateTests
{
    private static UserMessage UserText(string text) => new(MessageContent.From(text), 0);

    private static async Task<AssistantMessage> RunAsync(
        Faux.FauxCore core,
        IReadOnlyList<ChatMessage> messages,
        SimpleStreamOptions? options = null,
        ModelSpec? model = null)
        => await core.Stream(model ?? core.CreateModel(), new TranscriptContext(messages), options)
            .WaitForDoneAsync();

    private static async Task<List<AssistantMessageEvent>> CollectAsync(IAssistantMessageEventStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream)
        {
            events.Add(@event);
        }

        return events;
    }

    // ---------- 用量估算 ----------

    [Fact]
    public async Task EstimatesPromptAndOutputTokensFromCharacterCounts()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("hi")]);

        // 提示 "user:hello" = 10 字符 → ceil(10/4) = 3；输出 "hi" = 2 字符 → ceil(2/4) = 1。
        var message = await RunAsync(core, [UserText("hello")]);

        var usage = Assert.IsType<Usage>(message.UsageStats);
        Assert.Equal(3, usage.Input);
        Assert.Equal(1, usage.Output);
        Assert.Equal(0, usage.CacheRead);
        Assert.Equal(0, usage.CacheWrite);
        Assert.Equal(4, usage.TotalTokens);
        Assert.Equal(UsageCost.Zero, usage.Cost);
    }

    [Fact]
    public async Task EmptyPromptStillYieldsZeroInputTokens()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("")]);

        var message = await RunAsync(core, []);

        var usage = Assert.IsType<Usage>(message.UsageStats);
        Assert.Equal(0, usage.Input);
        Assert.Equal(0, usage.Output);
        Assert.Equal(0, usage.TotalTokens);
    }

    [Fact]
    public async Task ToolCallArgumentsCountAsJsonInOutputTokens()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([
            Faux.Response.ToolUse(Faux.ToolCall("echo", new Dictionary<string, object?> { ["a"] = 1 }, id: "call-1")),
        ]);

        // 提示 "user:go" = 7 → 2；输出 `echo:{"a":1}` = 12 → 3。
        var message = await RunAsync(core, [UserText("go")]);

        var usage = Assert.IsType<Usage>(message.UsageStats);
        Assert.Equal(2, usage.Input);
        Assert.Equal(3, usage.Output);
        Assert.Equal(5, usage.TotalTokens);
        Assert.Equal("echo", Assert.Single(message.ToolCalls).Name);
    }

    [Fact]
    public async Task UserImageBlocksRenderAsLengthPlaceholdersInThePrompt()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("x")]);

        // 块文本 "hi" + "\n" + "[image:image/png:4]" = 2 + 1 + 19 = 22 → "user:" + 22 = 27 → ceil(27/4) = 7。
        var user = new UserMessage(
            MessageContent.BlocksOf([new TextContent("hi"), new ImageContent("abcd", "image/png")]), 0);
        var message = await RunAsync(core, [user]);

        var usage = Assert.IsType<Usage>(message.UsageStats);
        Assert.Equal(7, usage.Input);
        Assert.Equal(1, usage.Output);
    }

    [Fact]
    public async Task SystemMessageTextAppendsToolDeltasAsJson()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("x")]);

        // system 段文本 `tool-:"old"` = 11 → "system:" + 11 = 18；再拼 "user:hi" = 7。
        // 整段 = (2-1)*2 + 18 + 7 = 27 → ceil(27/4) = 7。
        var system = new SystemMessage(Content: "", ToolsRemoved: ["old"]);
        var message = await RunAsync(core, [system, UserText("hi")]);

        Assert.Equal(7, Assert.IsType<Usage>(message.UsageStats).Input);
    }

    // ---------- 提示缓存 ----------

    [Fact]
    public async Task WithoutSessionIdThePromptIsNeverCached()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("hi"), Faux.Response.Text("hi")]);

        var first = await RunAsync(core, [UserText("hello")]);
        var second = await RunAsync(core, [UserText("hello")]);

        foreach (var message in new[] { first, second })
        {
            var usage = Assert.IsType<Usage>(message.UsageStats);
            Assert.Equal(3, usage.Input);
            Assert.Equal(0, usage.CacheRead);
            Assert.Equal(0, usage.CacheWrite);
            Assert.Equal(4, usage.TotalTokens);
        }
    }

    [Fact]
    public async Task FirstCallWithSessionIdWritesTheWholePromptToCache()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("hi")]);

        var message = await RunAsync(core, [UserText("hello")], new SimpleStreamOptions(SessionId: "s1"));

        var usage = Assert.IsType<Usage>(message.UsageStats);
        Assert.Equal(3, usage.Input);
        Assert.Equal(0, usage.CacheRead);
        Assert.Equal(3, usage.CacheWrite);
        Assert.Equal(7, usage.TotalTokens);
    }

    [Fact]
    public async Task SecondIdenticalCallReadsTheWholePromptFromCache()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("hi"), Faux.Response.Text("hi")]);

        _ = await RunAsync(core, [UserText("hello")], new SimpleStreamOptions(SessionId: "s1"));
        var message = await RunAsync(core, [UserText("hello")], new SimpleStreamOptions(SessionId: "s1"));

        // 逐条比较时整条消息相等 → 公共前缀就是整段提示（10 字符 → 3 token），新写入 0。
        var usage = Assert.IsType<Usage>(message.UsageStats);
        Assert.Equal(0, usage.Input);
        Assert.Equal(3, usage.CacheRead);
        Assert.Equal(0, usage.CacheWrite);
        Assert.Equal(4, usage.TotalTokens);
    }

    [Fact]
    public async Task DivergentTailCountsOnlyTheSharedPrefixAsCacheRead()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("z"), Faux.Response.Text("z")]);

        var first = await RunAsync(
            core,
            [UserText("aaa"), new AssistantMessage([new TextContent("bbb")])],
            new SimpleStreamOptions(SessionId: "s1"));

        // 首次：提示 = "user:aaa"(8) + "assistant:bbb"(13)，(2-1)*2+21 = 23 → ceil(23/4) = 6。
        var firstUsage = Assert.IsType<Usage>(first.UsageStats);
        Assert.Equal(6, firstUsage.Input);
        Assert.Equal(6, firstUsage.CacheWrite);
        Assert.Equal(13, firstUsage.TotalTokens);

        var second = await RunAsync(
            core,
            [UserText("aaa"), new AssistantMessage([new TextContent("bbc")])],
            new SimpleStreamOptions(SessionId: "s1"));

        // 逐条比较：第 0 条相等 → index=1；首条长度贡献 joinedLength(prev,1) = 8。
        // 余段 "\n\nassistant:bb" 共 14 字符相同 → cachedChars = 8 + 14 = 22 → cacheRead = ceil(22/4) = 6。
        // 当前提示 23 字符 → cacheWrite = ceil((23-22)/4) = 1；input = max(0, 6-6) = 0。
        var secondUsage = Assert.IsType<Usage>(second.UsageStats);
        Assert.Equal(0, secondUsage.Input);
        Assert.Equal(6, secondUsage.CacheRead);
        Assert.Equal(1, secondUsage.CacheWrite);
        Assert.Equal(8, secondUsage.TotalTokens);
    }

    [Fact]
    public async Task CacheRetentionNoneDisablesBothReadAndWrite()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("hi"), Faux.Response.Text("hi")]);
        var options = new SimpleStreamOptions(SessionId: "s1") { CacheRetention = "none" };

        _ = await RunAsync(core, [UserText("hello")], options);
        var message = await RunAsync(core, [UserText("hello")], options);

        // "none" 时整段缓存逻辑跳过，且**不写入**缓存——所以第二次仍是全额 input。
        var usage = Assert.IsType<Usage>(message.UsageStats);
        Assert.Equal(3, usage.Input);
        Assert.Equal(0, usage.CacheRead);
        Assert.Equal(0, usage.CacheWrite);
        Assert.Equal(4, usage.TotalTokens);
    }

    [Fact]
    public async Task CacheIsKeyedBySessionId()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.Text("hi"), Faux.Response.Text("hi")]);

        _ = await RunAsync(core, [UserText("hello")], new SimpleStreamOptions(SessionId: "s1"));
        var message = await RunAsync(core, [UserText("hello")], new SimpleStreamOptions(SessionId: "s2"));

        // 另一个会话 id 没有历史提示 → 仍走「首次写入」分支。
        var usage = Assert.IsType<Usage>(message.UsageStats);
        Assert.Equal(3, usage.Input);
        Assert.Equal(0, usage.CacheRead);
        Assert.Equal(3, usage.CacheWrite);
    }

    // ---------- 身份字段与终态 ----------

    [Fact]
    public async Task ReplayCarriesTheCoresApiProviderAndTheRequestedModelId()
    {
        var core = new Faux.FauxCore("custom-api", "custom-provider");
        var model = core.CreateModel("custom-model");
        core.SetResponses([Faux.Response.Text("x")]);

        var message = await RunAsync(core, [UserText("hi")], model: model);

        // TS cloneMessage(resolved, api, provider, requestModel.id)：三者都来自请求上下文，不是内建默认值。
        Assert.Equal("custom-api", message.Api);
        Assert.Equal("custom-provider", message.Provider);
        Assert.Equal("custom-model", message.Model);
    }

    [Fact]
    public async Task ErrorStopReasonEmitsErrorWithoutDone()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([new Faux.Response([], StopReason.Error, ErrorMessage: "boom")]);

        var events = await CollectAsync(
            core.Stream(core.CreateModel(), new TranscriptContext([UserText("hi")])));

        Assert.Collection(
            events,
            @event => Assert.IsType<AssistantMessageEvent.Start>(@event),
            @event =>
            {
                var error = Assert.IsType<AssistantMessageEvent.Error>(@event);
                Assert.Equal(StopReason.Error, error.Reason);
                Assert.Equal("boom", error.ErrorMessage);
            });
    }

    [Fact]
    public async Task AbortedStopReasonAlsoEmitsOnlyTheErrorEvent()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([new Faux.Response([], StopReason.Aborted)]);

        var events = await CollectAsync(
            core.Stream(core.CreateModel(), new TranscriptContext([UserText("hi")])));

        var error = Assert.IsType<AssistantMessageEvent.Error>(events[^1]);
        Assert.Equal(StopReason.Aborted, error.Reason);
        Assert.DoesNotContain(events, @event => @event is AssistantMessageEvent.Done);
    }

    [Fact]
    public async Task ExhaustedScriptFailsWithAnErrorTerminalCarryingTheModelIdentity()
    {
        var core = new Faux.FauxCore("custom-api", "custom-provider");
        var model = core.CreateModel("custom-model");

        var events = await CollectAsync(core.Stream(model, new TranscriptContext([UserText("hi")])));

        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal("Faux script exhausted", error.ErrorMessage);
        Assert.Equal("custom-api", error.Message.Api);
        Assert.Equal("custom-provider", error.Message.Provider);
        Assert.Equal("custom-model", error.Message.Model);
    }

    [Fact]
    public async Task ResponseFactoryHelpersProduceTheExpectedContentBlocks()
    {
        var core = new Faux.FauxCore();
        core.SetResponses([Faux.Response.ToolUse(Faux.ToolCall("echo", null, id: "call-1"))]);

        var message = await RunAsync(core, [UserText("go")]);

        var call = Assert.Single(message.ToolCalls);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("echo", call.Name);
        Assert.Null(call.Arguments);
        Assert.Equal(StopReason.ToolUse, message.StopReason);
    }
}
