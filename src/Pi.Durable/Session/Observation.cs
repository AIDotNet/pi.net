using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Services;

namespace Pi.Durable.Session;

/// <summary>watch 的一帧（值 + ops + 上下文 + 退休标记）。对应 TS <c>WatchFrame&lt;T&gt;</c>。</summary>
internal sealed record WatchFrame<T>(
    T Value, IReadOnlyList<DeltaOp> Ops, Context Context, bool Retires = false);

/// <summary>
/// 绑定到一个文档化身或对话视图的串行化精确帧 watch。其源显式结束它：退休文档用
/// <see cref="Retire"/>，关闭或失败的 Session 用 <see cref="End"/>。终止会丢弃该 watch 持有的
/// 每个指向其源的引用，因此一个仍被引用的已停止 watch 只保留其最后一个值。
/// 对应 TS <c>CommittedWatch&lt;T&gt;</c>（实现 <c>WatchHandle&lt;T&gt;</c>）。
/// </summary>
public sealed class CommittedWatch<T> : IWatchHandle<T>
{
    /// <summary>一个不可用 watch 监听器之后保留的精确已提交帧上限。</summary>
    internal const int MaxPendingWatchFrames = 100;

    /// <summary>退役文档化身的规范终局更新。对应 TS <c>RETIREMENT_OPERATIONS = [["r", null]]</c>。</summary>
    public static readonly IReadOnlyList<DeltaOp> RetirementOperations = [new DeltaOp.Replace(null)];

    private Action? _detach;
    private Action<Exception>? _report;
    private Func<T>? _replace;
    private readonly TaskCompletionSource<WatchEnd> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<WatchFrame<T>> _pending = [];
    private T _value;
    private Func<T, IReadOnlyList<DeltaOp>, Context, Task>? _listener;
    private bool _started;
    private bool _scheduled;
    private bool _running;
    private bool _retired;
    private WatchEnd? _end;
    private bool _cancellationInstalled;
    private CancellationTokenRegistration _cancellationRegistration;

    /// <summary>
    /// <paramref name="report"/> 接收监听器的抛出（该抛出同时结束 watch）。<paramref name="replace"/>
    /// 给出溢出时投递的值；缺省取最新值。
    /// </summary>
    public CommittedWatch(T value, Action detach, Action<Exception>? report = null, Func<T>? replace = null)
    {
        _value = value;
        _detach = detach;
        _report = report;
        _replace = replace;
    }

    /// <summary>获取时的修订；最新交付的不可变修订。</summary>
    public T Value => _value;

    /// <summary>watch 终止时落定；已运行的回调仍由调用方负责。</summary>
    public Task<WatchEnd> Closed => _closed.Task;

    /// <summary>安装唯一异步监听器；绝不内联调用。</summary>
    public void Start(Func<T, IReadOnlyList<DeltaOp>, Context, Task> listener)
    {
        if (_started) throw new InvalidOperationException("Watch is already started");
        if (_end is not null) throw new InvalidOperationException("Watch is stopped");
        _started = true;
        _listener = listener;
        if (_pending.Count > 0) Schedule();
    }

    /// <summary>幂等地停止未来回调并返回该 watch 的终态结果。</summary>
    public Task<WatchEnd> Stop()
    {
        Terminate(new WatchEnd.Stopped());
        return _closed.Task;
    }

    /// <summary>安装取消信号观察。对应 TS <c>observeCancellation(signal)</c>。</summary>
    public void ObserveCancellation(CancellationToken signal)
    {
        if (_cancellationInstalled)
            throw new InvalidOperationException("Watch cancellation is already installed");
        if (_end is not null) return;
        _cancellationInstalled = true;
        _cancellationRegistration = signal.Register(Cancel);
        if (signal.IsCancellationRequested) Cancel();
    }

    /// <summary>以 cancelled 终态终止。对应 TS <c>cancel()</c>。</summary>
    public void Cancel() => Terminate(new WatchEnd.Cancelled());

    /// <summary>
    /// 源已关闭：以 <see cref="WatchEnd.Ended"/> 结束；Session 失败时以 <see cref="WatchEnd.Failed"/>
    /// 携带该错误结束。对应 TS <c>end(failure?)</c>。
    /// </summary>
    public void End(Exception? failure = null)
        => Terminate(failure is null ? new WatchEnd.Ended() : new WatchEnd.Failed(failure));

    /// <summary>
    /// 文档化身已退休：在末尾排队最后一帧（值为 <c>null</c> 的 <c>[["r", null]]</c>），
    /// 投递之后 watch 以 <see cref="WatchEnd.Retired"/> 结束。溢出中的积压会被丢弃，因为该帧本就替换根。
    /// 对应 TS <c>retire(context)</c>（仅用于 <c>CommittedWatch&lt;ObservedDocumentValue&gt;</c>）。
    /// </summary>
    public void Retire(Context context)
    {
        if (_end is not null || _retired) return;
        _retired = true;
        if (_pending.Count >= MaxPendingWatchFrames) _pending.Clear();
        _pending.Add(new WatchFrame<T>(default!, RetirementOperations, context, Retires: true));
        if (_started) Schedule();
    }

    /// <summary>推进 watch：入队一帧（超限则整批折叠）。对应 TS <c>advance</c>。</summary>
    public void Advance(T value, IReadOnlyList<DeltaOp> ops, Context context)
    {
        if (_end is not null || _retired) return;
        if (_pending.Count >= MaxPendingWatchFrames)
        {
            _pending.Clear();
            var replacement = _replace is not null ? _replace() : value;
            _pending.Add(new WatchFrame<T>(replacement, [new DeltaOp.Replace(replacement)], context));
        }
        else
        {
            _pending.Add(new WatchFrame<T>(value, ops, context));
        }
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
                if (_pending.Count == 0) break;
                var frame = _pending[0];
                _pending.RemoveAt(0);
                var listener = _listener;
                if (listener is null) break;
                _value = frame.Value;
                var deliveryContext = ContextSignals.WithoutAbortSignal(frame.Context);
                try
                {
                    await listener(frame.Value, frame.Ops, deliveryContext).ConfigureAwait(false);
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
                if (frame.Retires)
                {
                    Terminate(new WatchEnd.Retired());
                    break;
                }
            }
        }
        finally
        {
            _running = false;
            if (_end is null && _pending.Count > 0) Schedule();
        }
    }

    /// <summary>
    /// 只结束一次：脱钩、丢弃待发帧与每个指向源的引用，并落定 <see cref="Closed"/>。
    /// 已在运行的回调继续运行。
    /// </summary>
    private void Terminate(WatchEnd end)
    {
        if (_end is not null) return;
        _end = end;
        var detach = _detach;
        _detach = null;
        _report = null;
        _replace = null;
        _listener = null;
        _pending.Clear();
        _cancellationRegistration.Dispose();
        _cancellationRegistration = default;
        detach?.Invoke();
        _closed.TrySetResult(end);
    }
}
