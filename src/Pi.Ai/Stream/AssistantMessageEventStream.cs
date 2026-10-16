using System.Diagnostics;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Stream;

/// <summary>
/// 基于通用 <see cref="EventStream{TEvent,TResult}"/> 的助手事件流实现。
/// 跟踪最近的 Partial 快照并转发终态。
/// <para>
/// 它同时给这次响应计时：终态消息（<c>done</c>/<c>error</c> 事件或 <see cref="End"/> 的入参）在自身没有
/// <c>durationMs</c>、且 <c>timestamp</c> 不早于本流创建时刻时，写入「从本流创建到结束」的单调时钟毫秒数。
/// 因此「转发别处已经开始的响应」（例如稍后取回的延后结果）不会被计时。
/// </para>
/// </summary>
public sealed class AssistantMessageEventStream : Types.IAssistantMessageEventStream
{
    private readonly EventStream<AssistantMessageEvent, AssistantMessage> _inner;
    private readonly long _startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly long _startedAtMonotonic = Stopwatch.GetTimestamp();

    /// <summary>当前部分消息快照（每次事件后更新）。</summary>
    public AssistantMessage? Partial { get; private set; }

    public AssistantMessageEventStream()
        => _inner = new EventStream<AssistantMessageEvent, AssistantMessage>();

    /// <summary>推送事件并更新部分消息快照。</summary>
    public void Push(AssistantMessageEvent @event)
    {
        switch (@event)
        {
            case AssistantMessageEvent.Done done:
                Time(done.Message);
                break;
            case AssistantMessageEvent.Error error:
                Time(error.Message);
                break;
        }

        Partial = @event switch
        {
            AssistantMessageEvent.Start start => start.Partial,
            AssistantMessageEvent.TextDelta { Partial: var p } => p,
            AssistantMessageEvent.TextStart { Partial: var p } => p,
            AssistantMessageEvent.TextEnd { Partial: var p } => p,
            AssistantMessageEvent.ThinkingDelta { Partial: var p } => p,
            AssistantMessageEvent.ThinkingStart { Partial: var p } => p,
            AssistantMessageEvent.ThinkingEnd { Partial: var p } => p,
            AssistantMessageEvent.ToolCallStart { Partial: var p } => p,
            AssistantMessageEvent.ToolCallDelta { Partial: var p } => p,
            AssistantMessageEvent.ToolCallEnd { Partial: var p } => p,
            AssistantMessageEvent.Done done => done.Message,
            AssistantMessageEvent.Error error => error.Message,
            _ => Partial,
        };
        _inner.Push(@event);
    }

    /// <summary>写入终态消息并关闭。</summary>
    public void End(AssistantMessage finalMessage)
    {
        Time(finalMessage);
        _inner.End(finalMessage);
    }

    private void Time(AssistantMessage message)
    {
        if (_inner.IsEnded || message.DurationMs is not null)
        {
            return;
        }

        if (message.Timestamp is long timestamp && timestamp < _startedAt)
        {
            return;
        }

        message.DurationMs = Math.Max(
            0,
            (long)Math.Round(Stopwatch.GetElapsedTime(_startedAtMonotonic).TotalMilliseconds));
    }

    /// <inheritdoc />
    public Task<AssistantMessage> WaitForDoneAsync(CancellationToken cancellationToken = default)
        => _inner.WaitForResultAsync(cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerator<AssistantMessageEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => _inner.GetAsyncEnumerator(cancellationToken);
}
