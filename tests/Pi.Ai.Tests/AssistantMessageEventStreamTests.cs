// ============================================================================
// Tests for utils/event-stream.ts's AssistantMessageEventStream timing
// (upstream 36a686ee8 "record response, tool execution, and durable task times").
// ============================================================================

using Pi.Ai.Stream;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests;

public sealed class AssistantMessageEventStreamTests
{
    private static AssistantMessage Message(long? timestamp, long? durationMs = null)
        => new(
            Content: [],
            StopReason: StopReason.Stop,
            Timestamp: timestamp)
        {
            DurationMs = durationMs,
        };

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void DoneEventTimesTheFinalMessage()
    {
        var stream = new AssistantMessageEventStream();
        var message = Message(NowMs());

        stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));

        Assert.NotNull(message.DurationMs);
        Assert.True(message.DurationMs >= 0, $"durationMs 应非负，实际 {message.DurationMs}");
    }

    [Fact]
    public void EndTimesTheFinalResult()
    {
        var stream = new AssistantMessageEventStream();
        var message = Message(NowMs());

        stream.End(message);

        Assert.NotNull(message.DurationMs);
        Assert.True(message.DurationMs >= 0);
    }

    [Fact]
    public void ErrorEventTimesTheErrorMessage()
    {
        var stream = new AssistantMessageEventStream();
        var message = Message(NowMs());

        stream.Push(new AssistantMessageEvent.Error(StopReason.Error, "boom", message));

        Assert.NotNull(message.DurationMs);
    }

    [Fact]
    public void MessageThatStartedBeforeTheStreamIsLeftUntimed()
    {
        // 转发「别处已经开始」的响应（例如稍后取回的延后结果）不应被计时。
        var stream = new AssistantMessageEventStream();
        var message = Message(NowMs() - 5_000);

        stream.End(message);

        Assert.Null(message.DurationMs);
    }

    [Fact]
    public void MessageThatAlreadyHasADurationIsLeftAlone()
    {
        var stream = new AssistantMessageEventStream();
        var message = Message(NowMs(), durationMs: 42);

        stream.End(message);

        Assert.Equal(42, message.DurationMs);
    }

    [Fact]
    public void MessageWithoutTimestampIsTimed()
    {
        // TS 的 timestamp 恒为数字；端口是 long?。undefined 与任何数比较都是 false，
        // 因此缺 timestamp 时仍然计时。
        var stream = new AssistantMessageEventStream();
        var message = Message(timestamp: null);

        stream.End(message);

        Assert.NotNull(message.DurationMs);
    }
}
