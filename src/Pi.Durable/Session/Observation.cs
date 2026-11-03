using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Services;

namespace Pi.Durable.Session;

/// <summary>
/// 绑定到一个文档化身或对话视图的串行化精确帧 watch。其源显式结束它：退休文档用
/// <see cref="Retire"/>，关闭或失败的 Session 用 <see cref="EndWith"/>。终止会丢弃该 watch 持有的
/// 每个指向其源的引用，因此一个仍被引用的已停止 watch 只保留其最后一个值。
///
/// <para>对应 TS 1.5.0 起直接使用的 <c>Watch&lt;T&gt;</c>（TS 的 <c>session/observation.ts</c> 已删除）。
/// 端口保留这一层薄适配，只是因为 durable 侧的调用点用「值 + ops + 上下文」这样的动词、而不是构造一次
/// <see cref="WatchCall{T}"/>；语义与 <see cref="Watch{T}"/> 完全一致。</para>
/// </summary>
public sealed class CommittedWatch<T> : Watch<T>
{
    /// <summary>退役文档化身的规范终局更新。对应 TS <c>RETIREMENT_OPERATIONS = [["r", null]]</c>。</summary>
    public static readonly IReadOnlyList<DeltaOp> RetirementOperations = [new DeltaOp.Replace(null)];

    private bool _retired;

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
    /// 文档化身已退休：入队最后一帧（值为 <c>null</c> 的 <c>[["r", null]]</c>，并带 retired 标记），投递完待发
    /// 内容后以 <see cref="WatchEnd.Ended"/> 结束。对应 TS <c>advance({…retired:true})</c> +
    /// <c>endAfterDelivery()</c>。
    /// </summary>
    public void Retire(Context context)
    {
        if (_retired) return;
        _retired = true;
        Advance(new WatchCall<T>.Value(default!, RetirementOperations, Retired: true), context);
        EndAfterDelivery();
    }

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
