using Pi.Chord.Context;
using Pi.Durable.Env;

namespace Pi.Env;

/// <summary>守护进程如何观察，与 <c>NodeExecutionEnv</c> 的 <c>NodeWatchOptions</c> 同构。</summary>
public sealed record RemoteWatchOptions
{
    /// <summary>强制模式；缺省时 Windows 与不报告远端变化的文件系统轮询。</summary>
    public string? Mode { get; init; }

    /// <summary><c>polling</c> 模式下两次快照的间隔；缺省 2000 ms。</summary>
    public int? PollIntervalMs { get; init; }

    /// <summary>单个观察者覆盖的目录上限；缺省 10,000。</summary>
    public int? MaxDirectories { get; init; }
}

/// <summary>
/// 跑在守护进程里的观察者（Durable 的 <c>NodeFileWatcher</c>，紧挨着文件）。连接丢失后，等连接可以再次启动时重新打开，
/// 并上报 <c>overflow</c>：这期间的变化没有被看到。对应 TS <c>RemoteWatcher</c>。
/// </summary>
public sealed class RemoteWatcher : IFileWatcher
{
    private const int ReconnectFirstMs = 1000;
    private const int ReconnectMaxMs = 30_000;

    private readonly Connection _connection;
    private readonly IReadOnlyList<WatchTarget> _targets;
    private readonly Action<WatchChange> _onChange;
    private readonly RemoteWatchOptions _options;
    private FileWatchMode _mode = FileWatchMode.Native;
    private bool _closed;
    private bool _ready;
    private CancellationTokenSource? _controller;
    private Task? _request;
    private Action? _wake;

    private RemoteWatcher(
        Connection connection,
        IReadOnlyList<WatchTarget> targets,
        Action<WatchChange> onChange,
        RemoteWatchOptions options)
    {
        _connection = connection;
        _targets = targets;
        _onChange = onChange;
        _options = options;
    }

    /// <summary>观察已解析的 <paramref name="targets"/>；覆盖无法建立时与 <c>NodeFileWatcher.Open</c> 一样失败。</summary>
    public static async Task<RemoteWatcher> OpenAsync(
        Connection connection,
        IReadOnlyList<WatchTarget> targets,
        Action<WatchChange> onChange,
        RemoteWatchOptions? options = null)
    {
        var watcher = new RemoteWatcher(connection, targets, onChange, options ?? new RemoteWatchOptions());
        await watcher.StartAsync().ConfigureAwait(false);
        return watcher;
    }

    public FileWatchMode Mode => _mode;

    public async Task CloseAsync(Context context)
    {
        if (_closed) return;
        _closed = true;
        _wake?.Invoke();
        _controller?.Cancel();
        if (_request is { } request)
        {
            try
            {
                await request.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 关闭不等待请求的结论。
            }
        }
    }

    private void Deliver(WatchChange change)
    {
        if (_closed) return;
        try
        {
            _onChange(change);
        }
        catch (Exception)
        {
            // 抛异常的回调不得中断观察。
        }
    }

    /// <summary>打开守护进程的观察者；覆盖建立后完成。</summary>
    private Task StartAsync()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new CancellationTokenSource();
        _controller = controller;
        _ready = false;
        var request = _connection.RequestAsync(
            "watch",
            WatchJson(),
            new RequestOptions
            {
                Signal = controller.Token,
                OnEvent = (json, _) =>
                {
                    var kind = EnvJson.Str(json, "kind");
                    switch (kind)
                    {
                        case "ready":
                            _ready = true;
                            _mode = EnvJson.Str(json, "mode") == "polling" ? FileWatchMode.Polling : FileWatchMode.Native;
                            ready.TrySetResult();
                            break;
                        case "change":
                            if (EnvJson.Str(json, "mode") == "polling") _mode = FileWatchMode.Polling;
                            if (EnvJson.Flag(json, "overflow"))
                            {
                                Deliver(WatchChange.Overflow());
                            }
                            else
                            {
                                var paths = new List<string>();
                                if (EnvJson.Arr(json, "paths") is { } array)
                                {
                                    foreach (var item in array)
                                    {
                                        if (item is System.Text.Json.Nodes.JsonValue value
                                            && value.TryGetValue<string>(out var path))
                                        {
                                            paths.Add(path);
                                        }
                                    }
                                }

                                Deliver(WatchChange.PathsChanged(paths));
                            }

                            break;
                        case "error":
                            var code = EnvJson.Str(json, "code") == "permission_denied"
                                ? FileErrorCode.PermissionDenied
                                : FileErrorCode.Invalid;
                            Deliver(WatchChange.Errored(new FileError(code, EnvJson.Str(json, "message") ?? "")));
                            _closed = true;
                            break;
                    }
                },
            });

        _request = ObserveAsync(request, ready);
        return ready.Task;
    }

    private async Task ObserveAsync(Task<Reply> request, TaskCompletionSource ready)
    {
        try
        {
            await request.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (!_ready)
            {
                ready.TrySetException(error);
                return;
            }

            if (_closed) return;
            if (error is RemoteError { Lost: true })
            {
                _ = ReconnectAsync();
                return;
            }

            Deliver(WatchChange.Errored(RemoteErrors.ToFileError(error)));
            _closed = true;
        }
    }

    private async Task ReconnectAsync()
    {
        for (var delay = ReconnectFirstMs; !_closed; delay = Math.Min(delay * 2, ReconnectMaxMs))
        {
            await WaitAsync(delay).ConfigureAwait(false);
            _wake = null;
            if (_closed) return;
            try
            {
                await StartAsync().ConfigureAwait(false);
                // 断连期间的变化没有被看到。
                Deliver(WatchChange.Overflow());
                return;
            }
            catch (Exception error)
            {
                if (error is RemoteError { Lost: true }) continue;
                Deliver(WatchChange.Errored(RemoteErrors.ToFileError(error)));
                _closed = true;
                return;
            }
        }
    }

    private Task WaitAsync(int delay)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new Timer(_ => completion.TrySetResult(), null, delay, Timeout.Infinite);
        _wake = () =>
        {
            timer.Dispose();
            completion.TrySetResult();
        };
        return AwaitAsync();

        async Task AwaitAsync()
        {
            try
            {
                await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                timer.Dispose();
            }
        }
    }

    private System.Text.Json.Nodes.JsonObject WatchJson()
    {
        var targets = new System.Text.Json.Nodes.JsonArray();
        foreach (var target in _targets)
        {
            var names = new System.Text.Json.Nodes.JsonArray();
            foreach (var name in target.Exclude?.Names ?? []) names.Add(name);
            targets.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = target.Path,
                ["recursive"] = target.Recursive,
                ["exclude"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["hidden"] = target.Exclude?.Hidden == true,
                    ["names"] = names,
                },
            });
        }

        var json = new System.Text.Json.Nodes.JsonObject { ["targets"] = targets };
        if (_options.Mode is { } mode) json["mode"] = mode;
        if (_options.PollIntervalMs is { } interval) json["pollIntervalMs"] = interval;
        if (_options.MaxDirectories is { } max) json["maxDirectories"] = max;
        return json;
    }
}
