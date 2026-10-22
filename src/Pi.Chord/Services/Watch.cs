using Pi.Chord.Delta;
using ChordContext = Pi.Chord.Context.Context;

namespace Pi.Chord.Services;

/// <summary>
/// 一次 watch 的终态结果。对应 TS <c>WatchEnd</c>（位于 chord，durable 再导出）。
/// <list type="bullet">
/// <item><c>stopped</c>：调用了 <see cref="IWatchHandle{T}.Stop"/>。</item>
/// <item><c>cancelled</c>：取得 watch 时携带的上下文被取消。</item>
/// <item><c>retired</c>：源已退休；其最后一帧是 <c>[["r", null]]</c>。</item>
/// <item><c>ended</c>：源已关闭（其所有者结束或关闭）。</item>
/// <item><c>listener_error</c>：监听器抛出或拒绝，附带该错误。</item>
/// <item><c>failed</c>：源以错误失败，附带该错误。</item>
/// </list>
/// </summary>
public abstract record WatchEnd
{
    private WatchEnd() { }

    /// <summary>调用了 <c>stop()</c>。对应 TS <c>stopped</c>。</summary>
    public sealed record Stopped : WatchEnd;

    /// <summary>取得 watch 时的上下文被取消。对应 TS <c>cancelled</c>。</summary>
    public sealed record Cancelled : WatchEnd;

    /// <summary>源已退休；其最后一帧是 <c>[["r", null]]</c>。对应 TS <c>retired</c>。</summary>
    public sealed record Retired : WatchEnd;

    /// <summary>源已关闭（其所有者结束或关闭）。对应 TS <c>ended</c>（旧名 <c>session_closed</c>）。</summary>
    public sealed record Ended : WatchEnd;

    /// <summary>监听器抛出或拒绝。对应 TS <c>listener_error</c>。</summary>
    public sealed record ListenerError(Exception Error) : WatchEnd;

    /// <summary>源以错误失败，通常是存储错误。对应 TS <c>failed</c>（旧名 <c>session_failed</c>）。</summary>
    public sealed record Failed(Exception Error) : WatchEnd;
}

/// <summary>不可变值的精确帧观察句柄（有待发上限的序列化观察）。对应 TS <c>WatchHandle&lt;T&gt;</c>。</summary>
public interface IWatchHandle<T>
{
    /// <summary>获取时的修订；最新交付的不可变修订。</summary>
    T Value { get; }

    /// <summary>安装唯一异步监听器；绝不内联调用。</summary>
    void Start(Func<T, IReadOnlyList<DeltaOp>, ChordContext, Task> listener);

    /// <summary>幂等地停止未来回调并返回该 watch 的终态结果。</summary>
    Task<WatchEnd> Stop();

    /// <summary>watch 终止时落定；已运行的回调仍由调用方负责。</summary>
    Task<WatchEnd> Closed { get; }
}
