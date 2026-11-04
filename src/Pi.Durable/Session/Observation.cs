using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Services;

namespace Pi.Durable.Session;

/// <summary>
/// 文档/视图追随器投递到的目标：一次「值 + 增量 + 退休标记」的推进，以及两种终局——投递完待发内容后结束
/// （owner 任务终态），或按 Session 的关闭/失败结束。对应 TS <c>Observer&lt;T&gt;</c> 在 durable 侧的用法。
/// </summary>
/// <remarks>
/// 与 <see cref="CommittedWatch{T}"/> 的区别只在缓冲：watch 把待发帧折叠成一次调用，观察者拿到源的每一帧。
/// </remarks>
public interface IObservationSink
{
    /// <summary>推进到新值。<paramref name="retired"/> 标记上一次投递以来被跟随的化身退休过。</summary>
    void Advance(
        IReadOnlyDictionary<string, object?>? value,
        IReadOnlyList<DeltaOp> ops,
        bool retired,
        Context context);

    /// <summary>源不再送帧：投递完剩余内容后以「已结束」收尾。</summary>
    void EndAfterDelivery();

    /// <summary>源已结束：<c>null</c> 表示正常关闭，否则携带失败原因。</summary>
    void EndWith(Exception? failure);
}

/// <summary>
/// 文档追随器的 watch 出口：缓冲与折叠由 <see cref="CommittedWatch{T}"/> 负责，
/// <see cref="IObservationSink"/> 的三条动词由基类同名成员直接满足。
/// </summary>
internal sealed class DocumentObservationSink
    : CommittedWatch<IReadOnlyDictionary<string, object?>?>, IObservationSink
{
    public DocumentObservationSink(
        IReadOnlyDictionary<string, object?>? value, Action detach, Action<Exception> report)
        : base(value, detach, report)
    {
    }
}

/// <summary>
/// 绑定到一个文档化身或对话视图的串行化精确帧 watch。其源显式结束它：退休文档用
/// <see cref="Retire"/>，关闭或失败的 Session 用 <see cref="EndWith"/>。终止会丢弃该 watch 持有的
/// 每个指向其源的引用，因此一个仍被引用的已停止 watch 只保留其最后一个值。
///
/// <para>对应 TS 1.5.0 起直接使用的 <c>Watch&lt;T&gt;</c>（TS 的 <c>session/observation.ts</c> 已删除）。
/// 端口保留这一层薄适配，只是因为 durable 侧的调用点用「值 + ops + 上下文」这样的动词、而不是构造一次
/// <see cref="WatchCall{T}"/>；语义与 <see cref="Watch{T}"/> 完全一致。</para>
/// </summary>
public class CommittedWatch<T> : Watch<T>
{
    /// <summary>
    /// <paramref name="report"/> 接收监听器的抛出（该抛出同时结束 watch）。<paramref name="replace"/>
    /// 给出溢出时投递的值；缺省取最新待发帧的值。
    /// </summary>
    public CommittedWatch(T value, Action detach, Action<Exception>? report = null, Func<T>? replace = null)
        : base(value, detach, report ?? (_ => { }), replace)
    {
    }

    /// <summary>入队一帧（值 + 增量 + 该帧的上下文）。对应 TS <c>advance({kind:"value", …}, context)</c>。</summary>
    public void Advance(T value, IReadOnlyList<DeltaOp> ops, Context context) =>
        Advance(new WatchCall<T>.Value(value, ops), context);

    /// <summary>
    /// 以「值 + 增量 + 上下文」装监听器：durable 侧的调用点按值思考，不必构造 <see cref="WatchCall{T}"/>。
    /// 源可以恢复的失败（<see cref="Watch.Fail(Exception, Context)"/>）到这里没有对应形状，遇到即以监听器错误结束。
    /// </summary>
    public void Start(Func<T, IReadOnlyList<DeltaOp>, Context, Task> listener) =>
        base.Start((call, context) => call is WatchCall<T>.Value value
            ? listener(value.NewValue, value.Ops, context)
            : Task.FromException(new InvalidOperationException("值形态的监听器收到了可恢复的失败调用")));

    /// <summary>入队一帧，并标记上一帧以来被跟随的文档被退休过。</summary>
    public void Advance(T value, IReadOnlyList<DeltaOp> ops, bool retired, Context context) =>
        Advance(new WatchCall<T>.Value(value, ops, retired), context);

    /// <summary>
    /// 源已关闭：立即以 <see cref="WatchEnd.Ended"/> 结束，丢弃待发内容；Session 失败时以
    /// <see cref="WatchEnd.Failed"/> 携带该错误结束。对应 TS <c>end()</c>。
    /// </summary>
    public void EndWith(Exception? failure = null)
    {
        if (failure is null) End();
        else Fail(failure);
    }
}
