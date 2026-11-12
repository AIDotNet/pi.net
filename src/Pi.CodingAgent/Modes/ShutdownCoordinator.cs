namespace Pi.CodingAgent.Modes;

/// <summary>
/// 上游 v1.5.1 #11599 / #11752：让已开始的 print/RPC 关闭流程跑完，只有「重复信号」才强制退出。
/// <list type="bullet">
/// <item>dispose 只跑一次，信号处理器与正常 finally 共享同一个 Task（进程管理器常同时关闭 stdin + 发 SIGTERM）。</item>
/// <item>信号计数：第一个信号触发正常关闭并等待 dispose；关闭期间到达的第二个信号才强制 exit
///       （否则一次 SIGTERM 会在 dispose 中途——例如 pending MCP token 刷新——把进程杀掉）。</item>
/// <item>信号处理器保持注册直到 dispose 完成，这样 dispose 期间的 SIGTERM 不会被「无处理器」的默认行为直接终止进程。</item>
/// </list>
/// 纯叶子，可在无 OS 信号 / 无 Environment.Exit 的情况下用 fake 验证。
/// </summary>
public sealed class ShutdownCoordinator
{
    private readonly object _gate = new();
    private readonly Func<Task> _dispose;
    private readonly Action<int> _forceExit;
    private Task? _disposing;
    private int _signalCount;
    private bool _shuttingDown;

    public ShutdownCoordinator(Func<Task> dispose, Action<int> forceExit)
    {
        _dispose = dispose;
        _forceExit = forceExit;
    }

    public int SignalCount { get { lock (_gate) { return _signalCount; } } }
    public bool ShuttingDown { get { lock (_gate) { return _shuttingDown; } } }

    /// <summary>返回共享的 dispose Task；多次调用（信号 + finally）只触发一次 dispose。</summary>
    public Task DisposeAsync()
    {
        lock (_gate)
        {
            return _disposing ??= _dispose();
        }
    }

    /// <summary>
    /// 收到一个信号。返回 true 表示这是「正常关闭尚未开始」的第一个信号，调用方应启动
    /// <see cref="DisposeAsync"/>；返回 false 表示关闭已在进行的重复信号——此时已强制 exit。
    /// </summary>
    public bool OnSignal(int exitCode)
    {
        lock (_gate)
        {
            _signalCount++;
            if (_signalCount > 1 && _shuttingDown)
            {
                // 关闭期间的重复信号：强制退出（进程管理器同时关闭 stdin + 发 SIGTERM 时只算一次请求）。
                _forceExit(exitCode);
                return false;
            }

            if (_shuttingDown)
            {
                return false;
            }

            _shuttingDown = true;
            return true; // 启动正常关闭
        }
    }

    /// <summary>正常控制流（stdin EOF / 命令主动退出）进入关闭：标记 shuttingDown 但不强制 exit。</summary>
    public void BeginShutdown()
    {
        lock (_gate)
        {
            _shuttingDown = true;
        }
    }
}
