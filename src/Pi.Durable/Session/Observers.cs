// ============================================================================
// Synchronous observers: Observer, Observation, and the guard around a host's observer.
// Port of packages/durable/src/session/session.ts (guardObserver / observation / contained).
// ============================================================================

using Pi.Chord.Context;
using Pi.Chord.Services;

namespace Pi.Durable.Session;

/// <summary>
/// 同步观察者：源的每一帧都直达（不折叠、不缓冲），源结束时收尾。对应 TS <c>Observer&lt;T&gt;</c>。
/// </summary>
/// <remarks>
/// 与 watch 的区别只在缓冲：watch 把一次忙碌周期内的帧折叠成一次调用，观察者拿到每一帧。
/// <para>
/// 差异 C231：TS 的 <c>advance</c> 允许返回 promise，其拒绝由 guard 上报；端口的 <c>Advance</c> 返回
/// <c>void</c>，宿主若要异步处理得自己在内部吞错。同步抛出照旧被 guard 兜住。
/// </para>
/// </remarks>
public interface ISessionObserver<T>
{
    /// <summary>推进一帧：值 + 增量，或源可恢复的失败。对应 TS <c>advance(call, context)</c>。</summary>
    void Advance(WatchCall<T> call, Context context);

    /// <summary>源结束（关闭、失败、被摘下或监听器出错）。对应 TS <c>end(end)</c>。</summary>
    void End(WatchEnd end);
}

/// <summary>
/// 已附着的观察者：附着那一刻的值，以及幂等的摘下动作——摘下之后不再有任何推进或结束。
/// 对应 TS <c>Observation&lt;T&gt;</c>。
/// </summary>
public sealed record Observation<T>(T Value, Action Detach);

/// <summary>
/// 把宿主的观察者包进「在提交发布时运行」的规则里：抛出被上报、自己摘下、并以
/// <see cref="WatchEnd.ListenerError"/> 结束，绝不传回提交方；摘下或结束之后，guard 不再持有它，也不再持有它的源。
/// 对应 TS <c>guardObserver</c>。
/// </summary>
public sealed class GuardedObserver<T> : ISessionObserver<T>
{
    private Target? _attached;

    /// <summary><paramref name="detach"/> 是源的摘下动作，<paramref name="report"/> 接收观察者的抛出。</summary>
    public GuardedObserver(ISessionObserver<T> observer, Action detach, Action<Exception> report) =>
        _attached = new Target(observer, detach, report);

    /// <summary>源的摘下动作，只跑一次。</summary>
    public void Detach() => Release();

    public void Advance(WatchCall<T> call, Context context)
    {
        var current = _attached;
        if (current is null) return;
        try
        {
            current.Observer.Advance(call, context);
        }
        catch (Exception error)
        {
            Fault(current, error);
        }
    }

    public void End(WatchEnd end)
    {
        var current = Release();
        if (current is null) return;
        Contain(() => current.Observer.End(end), current.Report);
    }

    /// <summary>上报抛出，然后摘下并以 <see cref="WatchEnd.ListenerError"/> 结束——期间若已摘下则什么都不做。</summary>
    private void Fault(Target current, Exception error)
    {
        current.Report(error);
        if (Release() is null) return;
        Contain(() => current.Observer.End(new WatchEnd.ListenerError(error)), current.Report);
    }

    /// <summary>摘下一次，并交回它附着时用的东西。</summary>
    private Target? Release()
    {
        var released = _attached;
        _attached = null;
        released?.Detach();
        return released;
    }

    /// <summary>宿主回调的抛出不能传给调用方：<paramref name="onError"/> 不等待、也不再抛。</summary>
    internal static void Contain(Action callback, Action<Exception> onError)
    {
        try
        {
            callback();
        }
        catch (Exception error)
        {
            onError(error);
        }
    }

    private sealed record Target(ISessionObserver<T> Observer, Action Detach, Action<Exception> Report);
}
