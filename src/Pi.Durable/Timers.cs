namespace Pi.Durable;

/// <summary>一次定时器请求的选项。对应 TS <c>SetTimer</c> 的 <c>{ background }</c>。</summary>
public sealed record TimerOptions
{
    /// <summary>只用于释放内存的定时器；宿主可以拒绝它（此时 Harness 在后续提交时释放那部分内存）。</summary>
    public bool Background { get; init; }
}

/// <summary>
/// 宿主定时器，由 <see cref="Harness.HarnessOptions.SetTimer"/> 提供：在约 <paramref name="delayMs"/> 之后调用一次
/// <paramref name="callback"/>，返回解除函数；<paramref name="options"/> 的 <see cref="TimerOptions.Background"/> 定时器
/// 可以由宿主拒绝（返回 null），例如挂起的定时器会阻止进程休眠的环境；其余定时器为工作调速或唤醒，宿主抛出或拒绝时
/// 以默认定时器代替。定时器不应提前触发；触发时调用方会重读自己的时钟。
/// 对应 TS <c>SetTimer</c>。
/// </summary>
public delegate Action? SetTimer(Action callback, long delayMs, TimerOptions options);

/// <summary>Session 与 Harness 通过宿主 <c>setTimer</c> 布设的定时器。对应 TS <c>Timers</c>。</summary>
public sealed record Timers
{
    /// <summary>为工作调速或恢复工作的定时器；宿主抛出或拒绝时以默认定时器布设。返回解除函数。</summary>
    public required Func<Action, long, Action> Foreground { get; init; }

    /// <summary>只释放内存的定时器；宿主拒绝时为 null。</summary>
    public required Func<Action, long, Action?> Background { get; init; }
}

/// <summary>
/// 默认的 <see cref="SetTimer"/>：<see cref="System.Threading.Timer"/>，超过其上限的延迟分多步等待。
/// 每个定时器回调都跑在线程池线程上，因此挂起的定时器从不保持进程存活——.NET 没有 <c>unref</c>，也没有需要拒绝
/// <see cref="TimerOptions.Background"/> 的环境，所以背景定时器总是布设得出。
/// 对应 TS <c>nativeTimer</c>。
/// </summary>
public static class NativeTimer
{
    /// <summary>单个定时器支持的最长延迟；更长的等待分多步进行。</summary>
    public const long MaxDelay = 2_147_483_647;

    public static Action? Arm(Action callback, long delayMs, TimerOptions options)
        => new NativeTimerHandle(callback, Math.Max(0, delayMs)).Disarm;
}

/// <summary>分步等待的一次默认定时器；解除后回调不再触发。</summary>
internal sealed class NativeTimerHandle
{
    private readonly Action _callback;
    private readonly object _gate = new();
    private Timer? _timer;
    private long _remaining;
    private bool _stopped;

    public NativeTimerHandle(Action callback, long remaining)
    {
        _callback = callback;
        _remaining = remaining;
        ArmLocked();
    }

    /// <summary>解除函数；幂等。</summary>
    public void Disarm()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void ArmLocked()
    {
        var step = Math.Min(_remaining, NativeTimer.MaxDelay);
        _timer = new Timer(static state => ((NativeTimerHandle)state!).Fire(), this, step, Timeout.Infinite);
    }

    private void Fire()
    {
        Action? callback;
        lock (_gate)
        {
            if (_stopped) return;
            if (_remaining > NativeTimer.MaxDelay)
            {
                _remaining -= NativeTimer.MaxDelay;
                _timer?.Dispose();
                ArmLocked();
                return;
            }

            _stopped = true;
            _timer?.Dispose();
            _timer = null;
            callback = _callback;
        }

        callback();
    }
}

/// <summary>
/// 包装宿主 <c>setTimer</c>。前端（foreground）定时器必须触发，否则工作停摆：宿主抛出或拒绝的那个以默认定时器布设，
/// 且只上报第一次失败。背景定时器宿主抛出时报错并拒绝。
/// 对应 TS <c>hostTimers</c>。
/// </summary>
public static class HostTimers
{
    private static readonly TimerOptions ForegroundOptions = new();

    private static readonly TimerOptions BackgroundOptions = new() { Background = true };

    public static Timers Create(SetTimer? setTimer, Action<Exception> report)
    {
        var arm = setTimer ?? NativeTimer.Arm;
        var gate = new object();
        var reported = false;
        void FallBack(Exception error)
        {
            lock (gate)
            {
                if (reported) return;
                reported = true;
            }

            report(error);
        }

        return new Timers
        {
            Foreground = (callback, delayMs) =>
            {
                try
                {
                    if (arm(callback, delayMs, ForegroundOptions) is { } disarm) return disarm;
                    FallBack(new InvalidOperationException(
                        "HarnessOptions.setTimer declined a timer that paces work; using the default timer"));
                }
                catch (Exception error)
                {
                    FallBack(error);
                }

                return NativeTimer.Arm(callback, delayMs, ForegroundOptions)!;
            },
            Background = (callback, delayMs) =>
            {
                try
                {
                    return arm(callback, delayMs, BackgroundOptions);
                }
                catch (Exception error)
                {
                    report(error);
                    return null;
                }
            },
        };
    }
}
