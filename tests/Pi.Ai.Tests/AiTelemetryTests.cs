using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Telemetry;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// AI 层遥测（<c>ai/telemetry.ts</c>）：请求 span、首事件/内容块事件、四个指标。
/// 观察者挂在 <see cref="LazyStream"/> 上，因此这里用一段脚本化的内层流端到端验证，
/// 而不是直接调观察者——这样 span 的起止与真实时序一致。
/// </summary>
public class AiTelemetryTests
{
    [Fact]
    public async Task ReportsARequestSpanWithUsageEventsAndMetrics()
    {
        var telemetry = new InMemoryTelemetryContext();
        var model = Model();
        var observer = AiTelemetry.ObserveRequest(model, Options(telemetry), poll: false);
        var stream = LazyStream.Run(model, () => Task.FromResult(Scripted()), observer);

        var final = await stream.WaitForDoneAsync();
        Assert.Equal(StopReason.Stop, final.StopReason);

        // span 在 OnResult 之后由 StartSpanAsync 的续体结算：等它落定再看快照。
        Assert.True(await WaitSettledAsync(telemetry), "span did not settle");
        var span = Assert.Single(telemetry.GetSpans());
        Assert.Equal("pi.ai.request", span.Name);
        Assert.Equal("faux", span.Attributes["provider"]);
        Assert.Equal("test-1", span.Attributes["model"]);
        Assert.Equal("test", span.Attributes["api"]);
        Assert.Equal("stop", span.Attributes["stop"]);
        Assert.Equal(3L, Convert.ToInt64(span.Attributes["usage.input"]));
        Assert.Equal(4L, Convert.ToInt64(span.Attributes["usage.output"]));

        Assert.Equal(["pi.ai.first_chunk", "pi.ai.block"], span.Events.Select(@event => @event.Name));
        var block = span.Events[1];
        Assert.Equal("text", block.Attributes["type"]);
        Assert.Equal(0L, Convert.ToInt64(block.Attributes["index"]));
        Assert.Equal(2L, Convert.ToInt64(block.Attributes["chars"]));

        var measurements = telemetry.GetMeasurements();
        Assert.Contains(measurements, item => item is { Kind: "histogram", Name: "pi.ai.request.duration" });
        Assert.Equal(
            ["input", "output", "cache_read", "cache_write"],
            measurements.Where(item => item.Name == "pi.ai.tokens")
                .Select(item => (string)item.Attributes["kind"]!)
                .ToArray());
        Assert.Contains(measurements, item => item is { Kind: "counter", Name: "pi.ai.cost" });
    }

    [Fact]
    public void DoesNothingWithoutATelemetryHandle()
    {
        var model = Model();
        var observer = AiTelemetry.ObserveRequest(model, null, poll: false);
        // 没有手柄时是无操作实现：不抛、不产生任何 span。
        observer.OnEvent(new AssistantMessageEvent.TextDelta(0, "x", 0, new AssistantMessage([])));
        observer.OnResult(null);
    }

    [Fact]
    public async Task ReportsAPollSpanForDeferredResponses()
    {
        var telemetry = new InMemoryTelemetryContext();
        var model = Model();
        var observer = AiTelemetry.ObserveRequest(model, Options(telemetry), poll: true);
        var stream = LazyStream.Run(model, () => Task.FromResult(Scripted()), observer);
        await stream.WaitForDoneAsync();

        var span = Assert.Single(telemetry.GetSpans());
        Assert.Equal("pi.ai.poll", span.Name);
    }

    private static async Task<bool> WaitSettledAsync(InMemoryTelemetryContext telemetry)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (telemetry.GetSpans() is [{ Settled: true }]) return true;
            await Task.Delay(10);
        }

        return false;
    }

    private static IReadOnlyDictionary<string, object?> Options(ITelemetryContext telemetry) =>
        new Dictionary<string, object?> { ["telemetry"] = telemetry };

    private static ModelSpec Model() => new()
    {
        Id = "test-1",
        Name = "Test Model",
        Api = "test",
        Provider = "faux",
        BaseUrl = "http://localhost:0",
        ContextWindow = 8_000,
        MaxTokens = 1_000,
    };

    private static IAssistantMessageEventStream Scripted()
    {
        var stream = new AssistantMessageEventStream();
        var partial = new AssistantMessage([new TextContent("")]);
        var final = new AssistantMessage([new TextContent("hi")])
        {
            StopReason = StopReason.Stop,
            Provider = "faux",
            Model = "test-1",
            Api = "test",
            UsageStats = new Usage(3, 4),
        };
        _ = Task.Run(() =>
        {
            stream.Push(new AssistantMessageEvent.Start(partial));
            stream.Push(new AssistantMessageEvent.TextDelta(0, "hi", 0, partial));
            stream.Push(new AssistantMessageEvent.TextEnd(0, "hi", final));
            stream.End(final);
        });
        return stream;
    }
}
