using System.Text;
using System.Text.Json.Nodes;

namespace Pi.Env;

/// <summary>打开终端时的选项。对应 TS <c>TerminalOptions</c>。</summary>
public sealed class TerminalOptions
{
    /// <summary>程序的工作目录；必须存在。</summary>
    public required string Cwd { get; init; }

    /// <summary>程序与参数。缺省是用户的 shell（<c>env</c> 里的 <c>SHELL</c> 或守护进程环境），以登录 shell 启动。</summary>
    public IReadOnlyList<string>? Argv { get; init; }

    /// <summary>追加到守护进程环境；<see cref="InheritEnv"/> 为 false 时是全部环境。<c>TERM</c> 放这里。</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public bool? InheritEnv { get; init; }

    public required int Cols { get; init; }

    public required int Rows { get; init; }

    /// <summary>终端的原始输出，按顺序。</summary>
    public required Action<byte[]> OnData { get; init; }
}

/// <summary>终端的前台作业：进程组 id，以及系统给出时的组长名。对应 TS <c>TerminalForeground</c>。</summary>
public readonly record struct TerminalForeground(long Pid, string? Name);

/// <summary>
/// pi-env 守护进程伪终端里的一个程序。程序退出、<see cref="Close"/> 杀掉它，或连接丢失（守护进程会杀掉它）时结束。
/// 对应 TS <c>RemoteTerminal</c>。
/// </summary>
public sealed class RemoteTerminal
{
    /// <summary>超过这个长度的输入分几次写入；帧最大 16 MiB。对应 TS <c>WRITE_CHUNK</c>。</summary>
    private const int WriteChunk = 1024 * 1024;

    private readonly Connection _connection;
    private readonly long _id;
    private readonly long _session;
    private readonly CancellationTokenSource _abort;

    internal RemoteTerminal(
        Connection connection, long id, long session, long pid, Task<long> exited, CancellationTokenSource abort)
    {
        _connection = connection;
        _id = id;
        _session = session;
        _abort = abort;
        Pid = pid;
        Exited = exited;
    }

    public long Pid { get; }

    /// <summary>退出码；<see cref="Close"/> 之后以守护进程的错误结束。</summary>
    public Task<long> Exited { get; }

    /// <summary>发送输入；终端已结束后输入被丢弃。</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        for (var offset = 0; offset < data.Length; offset += WriteChunk)
        {
            var chunk = data.Slice(offset, Math.Min(WriteChunk, data.Length - offset)).ToArray();
            Fire("ptyWrite", new JsonObject(), chunk);
        }
    }

    /// <summary>发送输入（字符串按 UTF-8 编码）。</summary>
    public void Write(string data) => Write(Encoding.UTF8.GetBytes(data));

    /// <summary>改变尺寸；程序会收到 <c>SIGWINCH</c>。</summary>
    public void Resize(int cols, int rows)
        => Fire("ptyResize", new JsonObject { ["cols"] = cols, ["rows"] = rows }, null);

    /// <summary>杀掉程序及其会话内的所有进程。</summary>
    public void Close() => _abort.Cancel();

    /// <summary>
    /// 程序不在前台时终端里的前台作业（shell 跑起来的东西，例如编辑器）；程序自己在前台（shell 停在提示符）时为
    /// <see langword="null"/>。终端结束后抛出。对应 TS <c>foreground()</c>。
    /// </summary>
    public async Task<TerminalForeground?> ForegroundAsync()
    {
        var reply = await RequestAsync(
            "ptyForeground", new JsonObject { ["target"] = _id }, null, _abort.Token).ConfigureAwait(false);
        if (reply.Json["foreground"] is not JsonObject foreground) return null;
        var pid = foreground["pid"]?.GetValue<long>() ?? 0;
        var name = foreground["name"]?.GetValue<string>();
        return new TerminalForeground(pid, string.IsNullOrEmpty(name) ? null : name);
    }

    private void Fire(string op, JsonObject json, byte[]? payload)
    {
        _ = RequestAsync(op, json, payload, _abort.Token).ContinueWith(
            static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private Task<Reply> RequestAsync(string op, JsonObject json, byte[]? payload, CancellationToken signal)
    {
        json["target"] = _id;
        return _connection.RequestAsync(op, json, new RequestOptions
        {
            Session = _session,
            Signal = signal,
            Payload = payload,
        });
    }
}

/// <summary>在 <paramref name="connection"/> 背后守护进程的新伪终端里启动一个程序。对应 TS <c>openTerminal()</c>。</summary>
public static class Terminals
{
    public static async Task<RemoteTerminal> OpenAsync(Connection connection, TerminalOptions options)
    {
        var abort = new CancellationTokenSource();
        var started = new TaskCompletionSource<RemoteTerminal>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitCode = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        long id = 0;
        long session = 0;

        var json = new JsonObject
        {
            ["cwd"] = options.Cwd,
            ["cols"] = options.Cols,
            ["rows"] = options.Rows,
        };
        if (options.Argv is { } argv)
        {
            var items = new JsonArray();
            foreach (var argument in argv) items.Add(argument);
            json["argv"] = items;
        }

        if (options.Env is { } environment)
        {
            var members = new JsonObject();
            foreach (var (key, value) in environment) members[key] = value;
            json["env"] = members;
        }

        if (options.InheritEnv is { } inherit) json["inheritEnv"] = inherit;

        var request = connection.RequestAsync("pty", json, new RequestOptions
        {
            Signal = abort.Token,
            OnStart = (requestId, requestSession) =>
            {
                id = requestId;
                session = requestSession;
            },
            OnEvent = (eventJson, payload) =>
            {
                var kind = eventJson["kind"]?.GetValue<string>();
                if (kind == "output")
                {
                    try
                    {
                        options.OnData(payload);
                    }
                    catch
                    {
                        // 调用方的回调异常不外传给传输层。
                    }
                }
                else if (kind == "started")
                {
                    var pid = eventJson["pid"]?.GetValue<long>() ?? 0;
                    started.TrySetResult(new RemoteTerminal(connection, id, session, pid, exitCode.Task, abort));
                }
            },
        });

        _ = SettleAsync(request, exitCode, started);
        return await started.Task.ConfigureAwait(false);
    }

    /// <summary>把请求的结果映射成退出码；启动前失败时把异常转交给 open 的等待者。</summary>
    private static async Task SettleAsync(
        Task<Reply> request, TaskCompletionSource<long> exitCode, TaskCompletionSource<RemoteTerminal> started)
    {
        try
        {
            var reply = await request.ConfigureAwait(false);
            var code = reply.Json["exitCode"]?.GetValue<long>() ?? 0;
            exitCode.TrySetResult(code);
        }
        catch (Exception error)
        {
            started.TrySetException(error);
            exitCode.TrySetException(error);
        }
    }
}
