using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Providers;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Agent.Tests;

/// <summary>
/// 思考档位 → 请求 <c>reasoning</c> 的链路，以及 <c>AgentOptions</c> 里转发给流选项的字段。
/// 对应 TS <c>agent.ts</c> 的 <c>createLoopConfig</c>（<c>reasoning</c> / <c>onPayload</c> /
/// <c>onResponse</c> / <c>onProviderStreamEvent</c> / <c>transport</c> / <c>thinkingBudgets</c> /
/// <c>maxRetryDelayMs</c>）与 <c>agent-loop.ts</c> 的 <c>streamAssistantResponse</c>
/// （<c>options.reasoning</c> + <c>result()</c> 的 <c>thinkingLevel</c>）。
/// </summary>
/// <remarks>
/// 这批行为此前在端口里**整体缺失**：循环把 <c>AgentLoopTurnUpdate.ThinkingLevel</c> 丢掉、
/// 且从不在流选项里写 <c>reasoning</c>，于是思考档位永远到不了 provider。本类锁住修复后的语义。
/// </remarks>
public class AgentReasoningTests
{
    private static (StreamFn Fn, List<SimpleStreamOptions?> Captured) Capturing(params Faux.Response[] script)
    {
        var inner = Faux.CreateStreamFn(script);
        var captured = new List<SimpleStreamOptions?>();
        StreamFn fn = (model, context, options, signal) =>
        {
            captured.Add(options);
            return inner(model, context, options, signal);
        };
        return (fn, captured);
    }

    [Theory]
    [InlineData(ThinkingLevel.Off, null)]
    [InlineData(ThinkingLevel.Minimal, "minimal")]
    [InlineData(ThinkingLevel.Low, "low")]
    [InlineData(ThinkingLevel.Medium, "medium")]
    [InlineData(ThinkingLevel.High, "high")]
    [InlineData(ThinkingLevel.XHigh, "xhigh")]
    [InlineData(ThinkingLevel.Max, "max")]
    public async Task ThinkingLevelIsForwardedAsReasoningText(ThinkingLevel level, string? expected)
    {
        var (fn, captured) = Capturing(Faux.Response.Text("ok"));
        var agent = new Agent(new AgentOptions { StreamFn = fn, Model = Faux.DefaultModel, ThinkingLevel = level });

        await agent.Prompt("hi");

        // TS：`reasoning: thinkingLevel === "off" ? undefined : thinkingLevel`。
        Assert.Equal(expected, Assert.Single(captured)!.Reasoning);
    }

    [Fact]
    public async Task TerminalMessageRecordsRequestedThinkingLevel()
    {
        var (fn, _) = Capturing(Faux.Response.Text("ok"));
        var agent = new Agent(new AgentOptions
        {
            StreamFn = fn,
            Model = Faux.DefaultModel,
            ThinkingLevel = ThinkingLevel.Medium,
        });

        await agent.Prompt("hi");

        // TS `result()`：终态消息的 thinkingLevel 记为本次请求档位。
        var assistant = Assert.IsType<AssistantMessage>(agent.Messages[^1]);
        Assert.Equal(ThinkingLevel.Medium, assistant.ThinkingLevel);
    }

    [Fact]
    public async Task OffIsRecordedOnTerminalMessageAsOff()
    {
        var (fn, _) = Capturing(Faux.Response.Text("ok"));
        var agent = new Agent(new AgentOptions { StreamFn = fn, Model = Faux.DefaultModel });

        await agent.Prompt("hi");

        var assistant = Assert.IsType<AssistantMessage>(agent.Messages[^1]);
        Assert.Equal(ThinkingLevel.Off, assistant.ThinkingLevel);
    }

    [Fact]
    public async Task PrepareRequestSeesCurrentReasoningAndItsUpdateAppliesToTheRequest()
    {
        ThinkingLevel? seen = null;
        var (fn, captured) = Capturing(Faux.Response.Text("ok"));
        var agent = new Agent(new AgentOptions
        {
            StreamFn = fn,
            Model = Faux.DefaultModel,
            ThinkingLevel = ThinkingLevel.Low,
            PrepareRequest = (_, _, thinkingLevel, _) =>
            {
                seen = thinkingLevel;
                return Task.FromResult<AgentLoopTurnUpdate?>(
                    new AgentLoopTurnUpdate(ThinkingLevel: ThinkingLevel.High));
            },
        });

        await agent.Prompt("hi");

        // 入参是 `config.reasoning ?? "off"`，不是写死的 off。
        Assert.Equal(ThinkingLevel.Low, seen);
        Assert.Equal("high", Assert.Single(captured)!.Reasoning);
    }

    [Fact]
    public async Task PrepareRequestCanTurnReasoningOff()
    {
        var (fn, captured) = Capturing(Faux.Response.Text("ok"));
        var agent = new Agent(new AgentOptions
        {
            StreamFn = fn,
            Model = Faux.DefaultModel,
            ThinkingLevel = ThinkingLevel.High,
            PrepareRequest = (_, _, _, _) => Task.FromResult<AgentLoopTurnUpdate?>(
                new AgentLoopTurnUpdate(ThinkingLevel: ThinkingLevel.Off)),
        });

        await agent.Prompt("hi");

        Assert.Null(Assert.Single(captured)!.Reasoning);
    }

    [Fact]
    public async Task PrepareRequestOmittingThinkingLevelKeepsCurrentReasoning()
    {
        var (fn, captured) = Capturing(Faux.Response.Text("ok"));
        var agent = new Agent(new AgentOptions
        {
            StreamFn = fn,
            Model = Faux.DefaultModel,
            ThinkingLevel = ThinkingLevel.High,
            PrepareRequest = (_, _, _, _) => Task.FromResult<AgentLoopTurnUpdate?>(
                new AgentLoopTurnUpdate()),
        });

        await agent.Prompt("hi");

        Assert.Equal("high", Assert.Single(captured)!.Reasoning);
    }

    [Fact]
    public async Task OptionFieldsAreForwardedToStreamOptions()
    {
        var budgets = new ThinkingBudgets(Minimal: 1, Low: 2, Medium: 3, High: 4);
        Func<JsonObject, Pi.Ai.Models.ModelSpec, Task<JsonObject?>> onPayload =
            (_, _) => Task.FromResult<JsonObject?>(null);
        Func<ProviderResponse, Pi.Ai.Models.ModelSpec, Task> onResponse = (_, _) => Task.CompletedTask;
        Func<JsonObject, Pi.Ai.Models.ModelSpec, Task> onStreamEvent = (_, _) => Task.CompletedTask;
        var (fn, captured) = Capturing(Faux.Response.Text("ok"));
        var agent = new Agent(new AgentOptions
        {
            StreamFn = fn,
            Model = Faux.DefaultModel,
            Transport = "websocket",
            ThinkingBudgets = budgets,
            MaxRetryDelayMs = 1234,
            OnPayload = onPayload,
            OnResponse = onResponse,
            OnProviderStreamEvent = onStreamEvent,
        });

        await agent.Prompt("hi");

        var options = Assert.Single(captured)!;
        Assert.Equal("websocket", options.Transport);
        Assert.Same(budgets, options.ThinkingBudgets);
        Assert.Equal(1234, options.MaxRetryDelayMs);
        Assert.Same(onPayload, options.OnPayload);
        Assert.Same(onResponse, options.OnResponse);
        Assert.Same(onStreamEvent, options.OnProviderStreamEvent);
    }

    [Fact]
    public async Task TransportDefaultsToAuto()
    {
        var (fn, captured) = Capturing(Faux.Response.Text("ok"));
        var agent = new Agent(new AgentOptions { StreamFn = fn, Model = Faux.DefaultModel });

        await agent.Prompt("hi");

        Assert.Equal("auto", Assert.Single(captured)!.Transport);
    }
}
