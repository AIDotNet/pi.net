using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// 上游 1.5.2 #8635：延迟准备（认证解析 / provider 加载）阶段被取消时，
/// 终态必须是 <c>aborted</c> 而不是 <c>error</c>；<c>options.signal</c> 要一路透传到 lazyStream。
/// </summary>
public class LazyStreamAbortTests
{
    private static ModelSpec Model() => new()
    {
        Id = "test-1",
        Name = "Test",
        Api = "test",
        Provider = "faux",
        BaseUrl = "http://localhost:0",
        ContextWindow = 8_000,
        MaxTokens = 1_000,
    };

    private static Func<Task<IAssistantMessageEventStream>> FailsWith(Exception error)
        => () => Task.FromException<IAssistantMessageEventStream>(error);

    private static async Task<(AssistantMessage Final, List<AssistantMessageEvent> Events)> DrainAsync(
        IAssistantMessageEventStream stream)
    {
        var events = new List<AssistantMessageEvent>();
        await foreach (var @event in stream) events.Add(@event);
        var final = await stream.WaitForDoneAsync();
        return (final, events);
    }

    [Fact]
    public async Task SetupFailureWithAbortedSignalEndsAsAborted()
    {
        // 关键回归：抛出的并非 OperationCanceledException，但信号已取消 → 仍按 aborted 记。
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stream = LazyStream.Run(Model(), FailsWith(new InvalidOperationException("auth failed")),
            signal: cts.Token);

        var (final, events) = await DrainAsync(stream);
        Assert.Equal(StopReason.Aborted, final.StopReason);
        Assert.Equal("auth failed", final.ErrorMessage);
        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Aborted, error.Reason);
    }

    [Fact]
    public async Task SetupFailureWithoutAbortEndsAsError()
    {
        var stream = LazyStream.Run(Model(), FailsWith(new InvalidOperationException("auth failed")),
            signal: CancellationToken.None);

        var (final, events) = await DrainAsync(stream);
        Assert.Equal(StopReason.Error, final.StopReason);
        var error = Assert.IsType<AssistantMessageEvent.Error>(Assert.Single(events));
        Assert.Equal(StopReason.Error, error.Reason);
    }

    [Fact]
    public async Task OperationCanceledExceptionEndsAsAbortedEvenWithoutSignal()
    {
        // .NET 侧取消通常表现为 OperationCanceledException；即便没有单独传 signal 也应按 aborted 记。
        var stream = LazyStream.Run(Model(), FailsWith(new OperationCanceledException("cancelled")));

        var (final, _) = await DrainAsync(stream);
        Assert.Equal(StopReason.Aborted, final.StopReason);
    }

    [Fact]
    public async Task SuccessfulSetupIsUnaffectedByAnUncancelledSignal()
    {
        using var cts = new CancellationTokenSource();
        var stream = LazyStream.Run(Model(), () =>
        {
            var inner = new AssistantMessageEventStream();
            var final = new AssistantMessage([new TextContent("ok")]) { StopReason = StopReason.Stop };
            _ = Task.Run(() => inner.End(final));
            return Task.FromResult<IAssistantMessageEventStream>(inner);
        }, signal: cts.Token);

        var (final, _) = await DrainAsync(stream);
        Assert.Equal(StopReason.Stop, final.StopReason);
    }

    [Fact]
    public void SetupErrorMessageDefaultsToError()
    {
        var message = LazyStream.SetupErrorMessage(Model(), new InvalidOperationException("x"), 123);
        Assert.Equal(StopReason.Error, message.StopReason);
        Assert.Equal(123, message.Timestamp);
    }

    [Fact]
    public void SignalOfExtractsTheCancellationTokenFromOptions()
    {
        using var cts = new CancellationTokenSource();
        var options = new Dictionary<string, object?> { ["signal"] = cts.Token };

        Assert.Equal(cts.Token, LazyStream.SignalOf(options));
        Assert.Equal(CancellationToken.None, LazyStream.SignalOf(new Dictionary<string, object?>()));
        Assert.Equal(CancellationToken.None, LazyStream.SignalOf(null));
        Assert.Equal(CancellationToken.None,
            LazyStream.SignalOf(new Dictionary<string, object?> { ["signal"] = "not-a-token" }));
    }
}
