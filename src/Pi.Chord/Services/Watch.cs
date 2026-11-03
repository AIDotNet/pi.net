using Pi.Chord.Context;
using Pi.Chord.Delta;
using ChordContext = Pi.Chord.Context.Context;

namespace Pi.Chord.Services;

/// <summary>
/// 一次 watch 的终态结果，按 kind 区分。watch 只会永久结束；源可以恢复的失败是一次
/// <see cref="WatchCall{T}.Failure"/> 调用，watch 仍然开着。对应 TS <c>WatchEnd</c>。
/// <list type="bullet">
/// <item><c>stopped</c>：调用了 <see cref="IWatchHandle{T}.Stop"/>。</item>
/// <item><c>aborted</c>：取得 watch 时携带的上下文被取消。</item>
/// <item><c>ended</c>：源已关闭（其所有者结束或关闭）。</item>
/// <item><c>listener_error</c>：监听器抛出或拒绝，附带该错误。</item>
/// <item><c>failed</c>：源以失败永久结束，附带该错误。</item>
/// </list>
/// </summary>
public abstract record WatchEnd
{
    private WatchEnd() { }

    /// <summary>调用了 <c>stop()</c>。对应 TS <c>stopped</c>。</summary>
    public sealed record Stopped : WatchEnd;

    /// <summary>取得 watch 时的上下文被取消。对应 TS <c>aborted</c>（旧名 <c>cancelled</c>）。</summary>
    public sealed record Aborted : WatchEnd;

    /// <summary>源已关闭。对应 TS <c>ended</c>（旧名 <c>session_closed</c>）。</summary>
    public sealed record Ended : WatchEnd;

    /// <summary>监听器抛出或拒绝。对应 TS <c>listener_error</c>。</summary>
    public sealed record ListenerError(Exception Error) : WatchEnd;

    /// <summary>源以失败永久结束，通常是存储错误。对应 TS <c>failed</c>。</summary>
    public sealed record Failed(Exception Error) : WatchEnd;
}

/// <summary>
/// JSON watch 监听器的一次调用，也是源交给它 watch 的一帧，按 kind 区分。对应 TS <c>WatchCall&lt;T&gt;</c>。
/// <list type="bullet">
/// <item><see cref="Value"/>：最新值、把上一个值变成它的 ops，以及上一帧以来被 watch 跟随的文档是否被退休。
/// 退休不结束 watch：<c>Value</c> 此后是该地址退休后显示的内容（源决定），迟到的新文档以
/// <c>[["r", value]]</c> 到达，可能就在同一次调用里。</item>
/// <item><see cref="Failure"/>：源现在算不出值，watch 保持打开，下一个值以 <c>[["r", value]]</c> 到达；
/// <c>Value</c> 保留最后一个值。</item>
/// </list>
/// </summary>
public abstract record WatchCall<T>
{
    private WatchCall() { }

    /// <summary>最新值 + 增量 + 是否发生过退休。</summary>
    public sealed record Value(T NewValue, IReadOnlyList<DeltaOp> Ops, bool Retired = false) : WatchCall<T>;

    /// <summary>源可恢复的失败。</summary>
    public sealed record Failure(Exception Error) : WatchCall<T>;
}

/// <summary>原生 watch 监听器的一次调用：新值，或源可恢复的失败（同 <see cref="WatchCall{T}"/>）。对应 TS <c>NativeWatchCall&lt;T&gt;</c>。</summary>
public abstract record NativeWatchCall<T>
{
    private NativeWatchCall() { }

    /// <summary>新值。</summary>
    public sealed record Value(T NewValue) : NativeWatchCall<T>;

    /// <summary>源可恢复的失败。</summary>
    public sealed record Failure(Exception Error) : NativeWatchCall<T>;
}

/// <summary>
/// 不可变值的串行化观察：同一时刻只有一次监听器调用，其间发生的变更折叠进下一次调用。
/// 对应 TS <c>WatchHandle&lt;T&gt;</c>。
/// </summary>
public interface IWatchHandle<T>
{
    /// <summary>获取时的修订；启动之后是最近交付的不可变修订（失败调用保留它）。</summary>
    T Value { get; }

    /// <summary>安装唯一异步监听器；绝不内联调用。</summary>
    void Start(Func<WatchCall<T>, ChordContext, Task> listener);

    /// <summary>幂等地停止未来回调并返回该 watch 的终态结果。</summary>
    Task<WatchEnd> Stop();

    /// <summary>watch 终止时落定；已运行的回调仍由调用方负责。</summary>
    Task<WatchEnd> Closed { get; }
}

/// <summary>
/// 进程内值的串行化观察：与 <see cref="IWatchHandle{T}"/> 相同，但每个新值各自一次调用，按序、不带 ops。
/// 对应 TS <c>NativeWatchHandle&lt;T&gt;</c>。
/// </summary>
public interface INativeWatchHandle<T>
{
    /// <summary>获取时的值；启动之后是最近交付的值（失败调用保留它）。</summary>
    T Value { get; }

    /// <summary>安装唯一异步监听器；绝不内联调用。</summary>
    void Start(Func<NativeWatchCall<T>, ChordContext, Task> listener);

    /// <summary>幂等地停止未来回调并返回该 watch 的终态结果。</summary>
    Task<WatchEnd> Stop();

    /// <summary>watch 终止时落定；已运行的回调仍由调用方负责。</summary>
    Task<WatchEnd> Closed { get; }
}

/// <summary>
/// 会变化、可被观察的值：文档、派生值、引用的成员。值按约定不可变（state 是 JSON）；每个 watch 从当前值开始，
/// 之后收到变更，监听器忙时折叠。对应 TS <c>Watchable&lt;T&gt;</c>。
/// </summary>
public interface IWatchable<T>
{
    /// <summary>当前值。</summary>
    T Value { get; }

    /// <summary>持有当前值、随后收到其变更的 watch；取消上下文即结束它。</summary>
    Task<IWatchHandle<T>> WatchAsync(ChordContext context);
}

/// <summary>
/// 会变化、可被观察的进程内值（可以装任何东西，错误与委托也行）：一次变更就是一个新值，按标识交付、不带 ops。
/// 对应 TS <c>NativeWatchable&lt;T&gt;</c>。
/// </summary>
public interface INativeWatchable<T>
{
    /// <summary>当前值。</summary>
    T Value { get; }

    /// <summary>持有当前值、随后每个新值一帧的 watch；取消上下文即结束它。</summary>
    Task<INativeWatchHandle<T>> WatchAsync(ChordContext context);
}

/// <summary>
/// 两种句柄共享的生命周期：串行投递与终止。源喂它并显式结束它；终止会丢掉 watch 持有的一切指向源的引用，
/// 所以一个仍被引用的已停止 watch 只留最后一个值。子类各自保存待发内容，调用与其帧的上下文分开——监听器拿到的
/// 调用只带数据。对应 TS <c>WatchLifecycle&lt;T, C&gt;</c>。
/// </summary>
public abstract class WatchLifecycle<T, TCall> where TCall : class
{
    private static readonly WatchEnd StoppedEnd = new WatchEnd.Stopped();
    private static readonly WatchEnd AbortedEnd = new WatchEnd.Aborted();
    private static readonly WatchEnd EndedEnd = new WatchEnd.Ended();

    private Action? _detach;
    private Action<Exception>? _report;
    private readonly TaskCompletionSource<WatchEnd> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private T _value;
    private Func<TCall, ChordContext, Task>? _listener;
    private bool _started;
    private bool _scheduled;
    private bool _running;
    /// <summary>由 <see cref="EndAfterDelivery"/> 置位：源不再送帧；投递完剩余内容后以 ended 结束。</summary>
    private bool _ending;
    /// <summary>由 <see cref="Terminate"/> 置一次。</summary>
    private WatchEnd? _end;
    private bool _abortInstalled;
    private CancellationTokenRegistration _abortRegistration;

    /// <summary>
    /// <paramref name="report"/> 接收监听器的抛出（该抛出同时结束 watch）。对应 TS 构造参数
    /// <c>value, detach, report</c>。
    /// </summary>
    protected WatchLifecycle(T value, Action detach, Action<Exception> report)
    {
        _value = value;
        _detach = detach;
        _report = report;
    }

    /// <summary>获取时的值；启动之后是最近交付的值。</summary>
    public T Value => _value;

    /// <summary>watch 终止时落定；已运行的回调仍由调用方负责。</summary>
    public Task<WatchEnd> Closed => _closed.Task;

    internal void StartListener(Func<TCall, ChordContext, Task> listener)
    {
        if (_started) throw new InvalidOperationException("Watch is already started");
        if (_end is not null) throw new InvalidOperationException("Watch is stopped");
        _started = true;
        _listener = listener;
        Schedule();
    }

    internal Task<WatchEnd> StopWatch()
    {
        Terminate(StoppedEnd);
        return _closed.Task;
    }

    /// <summary>
    /// 取消信号取消时以 aborted 结束；只安装一次。对应 TS <c>observeAbort(signal)</c>
    /// （端口用 <see cref="CancellationToken"/> 承载 AbortSignal）。
    /// </summary>
    public void ObserveAbort(CancellationToken signal)
    {
        if (_abortInstalled) throw new InvalidOperationException("Watch abort is already installed");
        if (_end is not null) return;
        _abortInstalled = true;
        _abortRegistration = signal.Register(Abort);
        if (signal.IsCancellationRequested) Abort();
    }

    /// <summary>以 aborted 结束：取得 watch 时的上下文被取消。对应 TS <c>abort()</c>。</summary>
    public void Abort() => Terminate(AbortedEnd);

    /// <summary>
    /// 立刻以 <paramref name="end"/> 结束并丢掉待发内容：源关闭（ended）或永久失败（failed）。源可能恢复的
    /// 失败是一次 <see cref="Watch{T}.Fail"/> 调用。对应 TS <c>end(end)</c>。
    /// </summary>
    public void End(WatchEnd end) => Terminate(end);

    /// <summary>以 ended 结束。对应 TS <c>end({kind:"ended"})</c>。</summary>
    public void End() => Terminate(EndedEnd);

    /// <summary>以 failed 结束。对应 TS <c>end({kind:"failed"})</c>。</summary>
    public void Fail(Exception error) => Terminate(new WatchEnd.Failed(error));

    /// <summary>
    /// 源在最后几帧之后不再送任何东西（例如一次退休）：不再收帧，把待发的投递完，然后以 ended 结束——若有一次
    /// 调用正在跑，等它返回。在此之前 watch 仍保持附着，所以 <see cref="End(WatchEnd)"/>、<see cref="Stop"/>、
    /// 取消与抛出的监听器仍会先结束它；从未启动的 watch 会把待发内容留到其中之一或 <c>Start</c>。
    /// 对应 TS <c>endAfterDelivery()</c>。
    /// </summary>
    public void EndAfterDelivery()
    {
        if (_end is not null || _ending) return;
        _ending = true;
        if (!_running && PendingContext is null) Terminate(EndedEnd);
    }

    /// <summary>watch 是否还收帧：自身未结束，且源未结束。</summary>
    protected bool Open => _end is null && !_ending;

    /// <summary>下一次调用覆盖的最新一帧的上下文；没有待发内容时为 null。</summary>
    protected abstract ChordContext? PendingContext { get; }

    /// <summary>取下一次调用；只在有待发内容时调用。</summary>
    protected abstract TCall Next();

    /// <summary>丢掉待发内容与所有指向源的引用；终止时调用一次。</summary>
    protected abstract void Release();

    /// <summary>本次调用交付的值；失败调用返回最后交付的值（watch 的 <c>Value</c> 保持不变）。</summary>
    protected abstract T DeliveredValue(TCall call);

    /// <summary>有内容待发了：一旦启动就投递，绝不内联。对应 TS <c>pending()</c>。</summary>
    protected void Pending()
    {
        if (_started) Schedule();
    }

    private void Schedule()
    {
        if (_scheduled || _running || _end is not null) return;
        _scheduled = true;
        // 设计差异：TS 用 queueMicrotask 保证「当前同步栈结束之后」投递；C# 用线程池任务近似
        // （投递仍严格晚于当前同步段、帧序不变），时序粒度不同但观察语义一致。
        _ = Task.Run(async () =>
        {
            _scheduled = false;
            await DrainAsync().ConfigureAwait(false);
        });
    }

    private async Task DrainAsync()
    {
        if (_running || _end is not null || !_started) return;
        _running = true;
        try
        {
            while (_end is null)
            {
                var listener = _listener;
                if (listener is null) break;
                var frameContext = PendingContext;
                if (frameContext is null)
                {
                    if (_ending) Terminate(EndedEnd);
                    break;
                }
                var call = Next();
                _value = DeliveredValue(call);
                try
                {
                    await listener(call, ContextSignals.WithoutAbortSignal(frameContext)).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    if (_end is null)
                    {
                        _report?.Invoke(error);
                        Terminate(new WatchEnd.ListenerError(error));
                    }
                    break;
                }
            }
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// 只结束一次：脱钩、丢掉待发内容与每个指向源的引用、落定 <see cref="Closed"/>。已在运行的回调继续运行。
    /// 对应 TS <c>#terminate</c>。
    /// </summary>
    private void Terminate(WatchEnd end)
    {
        if (_end is not null) return;
        _end = end;
        var detach = _detach;
        _detach = null;
        _report = null;
        _listener = null;
        Release();
        _abortRegistration.Dispose();
        _abortRegistration = default;
        detach?.Invoke();
        _closed.TrySetResult(end);
    }
}

/// <summary>
/// JSON 源的 watch 句柄。在 <c>Start</c> 之前或监听器运行期间到达的帧排在一个 <see cref="PendingOps"/> 队列里：
/// 下一次调用拿到最新值、上一调用以来所有帧折叠后的 ops（队列溢出后是 <c>[["r", newest]]</c>）、任一帧是退休时的
/// <c>Retired</c>，以及最新一帧的上下文。单独投递的一帧按源给的形状原样传下去。<c>Fail</c>（源可恢复的失败）丢掉
/// 待发帧并作为一次失败调用等着；其后的值替换它，失败后的第一次值调用是 <c>[["r", value]]</c>。
/// 对应 TS <c>Watch&lt;T&gt;</c>。
/// </summary>
public class Watch<T> : WatchLifecycle<T, WatchCall<T>>, IWatchHandle<T>
{
    private readonly PendingOps _ops = new();
    private readonly Func<T>? _replace;
    /// <summary>源的那次调用，当它是唯一待发帧时：调用原样传下去，不再分配。第二帧到达即丢弃它。</summary>
    private WatchCall<T>.Value? _lone;
    /// <summary>待发内容存在时最新一帧的值与上下文；上下文同时充当「有待发内容」的标记。</summary>
    private T? _newestValue;
    private ChordContext? _newestContext;
    /// <summary>出现过一个退休帧。</summary>
    private bool _retired;
    /// <summary>最新一帧是失败：下一次调用就是它。</summary>
    private WatchCall<T>.Failure? _failure;
    /// <summary>上次值调用之后来过失败：下一次值调用替换根，且不保留 ops。</summary>
    private bool _replaceRoot;

    /// <summary>
    /// <paramref name="replace"/> 是端口补充项：队列溢出时用它给出「当前值」，缺省用最新待发帧的值
    /// （对齐 TS 的 <c>[["r", newest]]</c>）。
    /// </summary>
    public Watch(T value, Action detach, Action<Exception> report, Func<T>? replace = null)
        : base(value, detach, report)
    {
        _replace = replace;
    }

    /// <summary>安装唯一异步监听器；绝不内联调用。</summary>
    public void Start(Func<WatchCall<T>, ChordContext, Task> listener) => StartListener(listener);

    /// <summary>幂等地停止未来回调并返回该 watch 的终态结果。</summary>
    public Task<WatchEnd> Stop() => StopWatch();

    /// <summary>
    /// 入队一帧。值的 ops 把上一个值变成它，<paramref name="retired"/> 表示被跟随的文档被退休过——不论这次调用
    /// 折了几帧都上报一次。失败帧请用 <see cref="Fail"/>。对应 TS <c>advance(call, context)</c>。
    /// </summary>
    public void Advance(WatchCall<T> call, ChordContext context)
    {
        if (call is WatchCall<T>.Failure failure)
        {
            FailInternal(failure, context);
            return;
        }
        var value = (WatchCall<T>.Value)call;
        if (!Open) return;
        if (_replaceRoot)
        {
            _failure = null;
        }
        else
        {
            _lone = _newestContext is null ? value : null;
            _ops.Push(value.Ops);
        }
        _newestValue = value.NewValue;
        _newestContext = context;
        if (value.Retired) _retired = true;
        Pending();
    }

    /// <summary>
    /// 入队一个源可恢复的失败：待发帧被丢掉（<see cref="WatchLifecycle{T,TCall}.Value"/> 保留最后交付的值），
    /// 监听器收到 <see cref="WatchCall{T}.Failure"/>，其后的值以 <c>[["r", value]]</c> 到达。
    /// 对应 TS <c>fail(error, context)</c>。
    /// </summary>
    public void Fail(Exception error, ChordContext context) =>
        FailInternal(new WatchCall<T>.Failure(error), context);

    private void FailInternal(WatchCall<T>.Failure failure, ChordContext context)
    {
        if (!Open) return;
        _ops.Take();
        _lone = null;
        _newestValue = default;
        _failure = failure;
        _newestContext = context;
        _replaceRoot = true;
        Pending();
    }

    /// <inheritdoc/>
    protected override ChordContext? PendingContext => _newestContext;

    /// <inheritdoc/>
    protected override WatchCall<T> Next()
    {
        // 失败之后这次调用就是失败，或失败之后的值；否则一次字段读就能决定。
        if (_replaceRoot) return NextAfterFailure();
        var lone = _lone;
        var value = _newestValue!;
        var taken = _ops.Take();
        var retired = _retired;
        _lone = null;
        _newestValue = default;
        _newestContext = null;
        _retired = false;
        // 单独一帧从不折叠也不溢出：它的调用就是监听器拿到的那个。
        if (lone is not null) return lone;
        var ops = taken is PendingTake.Ops take ? take.Operations : OverflowOps(value);
        return new WatchCall<T>.Value(value, ops, retired);
    }

    private WatchCall<T> NextAfterFailure()
    {
        var failure = _failure;
        _newestContext = null;
        if (failure is not null)
        {
            _failure = null;
            return failure;
        }
        var value = _newestValue!;
        var retired = _retired;
        _newestValue = default;
        _retired = false;
        _replaceRoot = false;
        return new WatchCall<T>.Value(value, [new DeltaOp.Replace(value)], retired);
    }

    /// <summary>溢出后的根替换：端口可用 <c>replace</c> 给出源的当前值，缺省用最新待发帧的值。</summary>
    private IReadOnlyList<DeltaOp> OverflowOps(T value) =>
        [new DeltaOp.Replace(_replace is null ? value : _replace())];

    /// <inheritdoc/>
    protected override T DeliveredValue(WatchCall<T> call) =>
        call is WatchCall<T>.Value delivered ? delivered.NewValue : Value;

    /// <inheritdoc/>
    protected override void Release()
    {
        _ops.Take();
        _lone = null;
        _newestValue = default;
        _newestContext = null;
        _retired = false;
        _failure = null;
        _replaceRoot = false;
    }
}

/// <summary>一个不可用原生 watch 监听器背后保留的最大值数。对应 TS <c>MAX_PENDING_NATIVE_VALUES</c>。</summary>
public static class NativeWatchLimits
{
    /// <summary>待发值上限；超过即折叠成一个（<c>replace</c>，缺省取最新值）。</summary>
    public const int MaxPendingNativeValues = 100;
}

/// <summary>
/// 原生源的 watch 句柄：每个值按序一次调用；超过 100 个待发值时折叠成一个（<c>replace</c>，缺省取最新值）。
/// <c>Fail</c> 丢掉待发值并作为一次失败调用等着，其后的值替换它。对应 TS <c>NativeWatch&lt;T&gt;</c>。
/// </summary>
public sealed class NativeWatch<T> : WatchLifecycle<T, NativeWatchCall<T>>, INativeWatchHandle<T>
{
    /// <summary>待发帧（值 + 该帧的上下文）。值原样保留：监听器拿到的调用在取出时才构造。</summary>
    private readonly List<Frame> _pending = [];
    private int _head;
    private Func<T>? _replace;
    /// <summary>待发的失败与其帧上下文；此时没有别的内容待发。</summary>
    private NativeWatchCall<T>.Failure? _failure;
    private ChordContext? _failureContext;

    /// <summary>构造；<paramref name="replace"/> 给出折叠时用的值，缺省取最新值。</summary>
    public NativeWatch(T value, Action detach, Action<Exception> report, Func<T>? replace = null)
        : base(value, detach, report)
    {
        _replace = replace;
    }

    /// <summary>安装唯一异步监听器；绝不内联调用。</summary>
    public void Start(Func<NativeWatchCall<T>, ChordContext, Task> listener) => StartListener(listener);

    /// <summary>幂等地停止未来回调并返回该 watch 的终态结果。</summary>
    public Task<WatchEnd> Stop() => StopWatch();

    /// <summary>入队源的新值；它会替换一个待发的失败。对应 TS <c>advance(value, context)</c>。</summary>
    public void Advance(T value, ChordContext context)
    {
        if (!Open) return;
        _failure = null;
        _failureContext = null;
        if (_pending.Count - _head < NativeWatchLimits.MaxPendingNativeValues)
        {
            _pending.Add(new Frame(value, context));
        }
        else
        {
            _pending.Clear();
            _head = 0;
            _pending.Add(new Frame(_replace is null ? value : _replace(), context));
        }
        Pending();
    }

    /// <summary>
    /// 入队一个源可恢复的失败：待发值被丢掉（<see cref="WatchLifecycle{T,TCall}.Value"/> 保留最后交付的值），
    /// 监听器收到 <see cref="NativeWatchCall{T}.Failure"/>。对应 TS <c>fail(error, context)</c>。
    /// </summary>
    public void Fail(Exception error, ChordContext context)
    {
        if (!Open) return;
        _pending.Clear();
        _head = 0;
        _failure = new NativeWatchCall<T>.Failure(error);
        _failureContext = context;
        Pending();
    }

    /// <inheritdoc/>
    protected override ChordContext? PendingContext =>
        _failureContext ?? (_head < _pending.Count ? _pending[_head].Context : null);

    /// <inheritdoc/>
    protected override NativeWatchCall<T> Next()
    {
        var failure = _failure;
        if (failure is not null)
        {
            _failure = null;
            _failureContext = null;
            return failure;
        }
        var frame = _pending[_head];
        // 取走的槽立刻清掉，全部取完就清空列表；取走的前缀每 100 个值丢一次，让列表保持有界。
        _pending[_head] = default;
        var head = _head + 1;
        if (head == _pending.Count)
        {
            _pending.Clear();
            _head = 0;
        }
        else if (head >= NativeWatchLimits.MaxPendingNativeValues)
        {
            _pending.RemoveRange(0, head);
            _head = 0;
        }
        else
        {
            _head = head;
        }
        return new NativeWatchCall<T>.Value(frame.Value!);
    }

    /// <inheritdoc/>
    protected override T DeliveredValue(NativeWatchCall<T> call) =>
        call is NativeWatchCall<T>.Value delivered ? delivered.NewValue : Value;

    /// <inheritdoc/>
    protected override void Release()
    {
        _pending.Clear();
        _head = 0;
        _replace = null;
        _failure = null;
        _failureContext = null;
    }

    private readonly record struct Frame(T Value, ChordContext Context);
}
