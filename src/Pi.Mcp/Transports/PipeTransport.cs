using System.Text;
using Pi.Mcp.Protocol;

namespace Pi.Mcp.Transports;

/// <summary>
/// 一个已运行程序的 stdio，由调用方提供（例如 pi-env 的 <c>RemoteProcess</c>）。
/// 对应 TS <c>McpPipe</c>。
/// </summary>
public interface IMcpPipe
{
    /// <summary>向程序 stdin 写入字节。</summary>
    void Write(byte[] data);

    /// <summary>关闭 stdin，让程序自行退出。</summary>
    void CloseStdin();

    /// <summary>终止程序（以及它启动的一切），管道负责清理。</summary>
    void Close();

    /// <summary>程序结束时（无论何种原因）完成；<see cref="Close"/> 之后也必须完成。</summary>
    Task Exited { get; }
}

/// <summary><see cref="PipeTransportOptions.Open"/> 用来连接程序输出的处理器。</summary>
public sealed class McpPipeHandlers
{
    public required Action<byte[]> OnStdout { get; init; }
    public required Action<byte[]> OnStderr { get; init; }
}

/// <summary>MCP <see cref="PipeTransport"/> 选项，对应 TS <c>PipeTransportOptions</c>。</summary>
public sealed class PipeTransportOptions
{
    /// <summary>启动程序，并把它的输出接到给定处理器上。</summary>
    public required Func<McpPipeHandlers, Task<IMcpPipe>> Open { get; init; }

    public int? MaxMessageBytes { get; init; }
    public int? MaxStderrBytes { get; init; }
    public Action<string>? OnStderr { get; init; }

    /// <summary>关 stdin 后等待程序自行退出的毫秒数，超时则 <see cref="IMcpPipe.Close"/>（缺省 2s）。</summary>
    public int CloseTimeoutMs { get; init; } = PipeTransport.DefaultCloseTimeoutMs;
}

/// <summary>
/// 在「由别处启动的程序」之上运行的 MCP stdio 传输（例如经 pi-env 在另一台机器上启动的 MCP server）。
/// 对应 TS <c>transports/pipe.ts</c> 的 <c>PipeTransport</c>（1.5.1 新增）。程序结束即关闭传输；
/// 非 <see cref="CloseAsync"/> 引起的 <c>Exited</c> 拒绝先作为错误上报。
/// </summary>
public sealed class PipeTransport : TransportEvents
{
    public const int DefaultCloseTimeoutMs = 2_000;

    private readonly PipeTransportOptions _options;
    private readonly JsonRpcLineReader _lines;
    private IMcpPipe? _pipe;
    private bool _started;
    private bool _closed;
    private bool _ended;

    public PipeTransport(PipeTransportOptions options)
    {
        _options = options;
        _lines = new JsonRpcLineReader(new JsonRpcLineReaderOptions
        {
            MaxMessageBytes = options.MaxMessageBytes,
            MaxStderrBytes = options.MaxStderrBytes,
            OnStderr = options.OnStderr,
            OnMessage = EmitMessage,
            OnError = EmitError,
        });
    }

    /// <summary>程序 stderr 的尾部（缺省 64 KiB）。</summary>
    public string Stderr => _lines.Stderr;

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("MCP pipe transport already started");
        if (_closed) throw new McpConnectionClosedError();
        _started = true;

        var pipe = await _options.Open(new McpPipeHandlers
        {
            OnStdout = _lines.ReadStdout,
            OnStderr = _lines.ReadStderr,
        }).ConfigureAwait(false);

        if (_closed)
        {
            // close() 在启动期间执行、且当时没事可关。没人再等待 Exited，管道也可能在 kill 后拒绝它。
            _ = pipe.Exited.ContinueWith(_ => { }, TaskScheduler.Default);
            pipe.Close();
            throw new McpConnectionClosedError();
        }

        _pipe = pipe;
        _ = WatchExitAsync(pipe);
    }

    private async Task WatchExitAsync(IMcpPipe pipe)
    {
        try
        {
            await pipe.Exited.ConfigureAwait(false);
            Exit();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    /// <inheritdoc />
    public override Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (_pipe is null || _closed || _ended) throw new McpConnectionClosedError();
        var line = JsonRpc.Serialize(message).ToJsonString() + "\n";
        _pipe.Write(Encoding.UTF8.GetBytes(line));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) return;
        _closed = true;
        var pipe = _pipe;
        if (pipe is null)
        {
            EmitClose();
            return;
        }
        if (_ended) return;

        // 规范流程：关 stdin 让程序自行退出，超时才升级为强制终止。
        pipe.CloseStdin();
        var exitedInTime = await WaitForExitAsync(pipe, Math.Max(0, _options.CloseTimeoutMs)).ConfigureAwait(false);
        if (exitedInTime) return;

        pipe.Close();
        try { await pipe.Exited.ConfigureAwait(false); }
        catch { /* kill 之后 Exited 可能拒绝：忽略，原始结果由 WatchExitAsync 处理 */ }
    }

    /// <summary>程序结束：报告未完成消息，然后关闭。</summary>
    private void Exit()
    {
        _ended = true;
        _lines.End();
        EmitClose();
    }

    /// <summary><c>Exited</c> 拒绝：非 close 引起的作为错误上报，随后等同结束。</summary>
    private void Fail(Exception error)
    {
        if (!_closed) EmitError(error);
        Exit();
    }

    private static async Task<bool> WaitForExitAsync(IMcpPipe pipe, int timeoutMs)
    {
        // WhenAny 不抛：Exited 完成或拒绝都算「程序已结束」，只有超时才算「还没结束」。
        var exited = pipe.Exited;
        var settled = await Task.WhenAny(exited, Task.Delay(timeoutMs)).ConfigureAwait(false);
        return settled == exited;
    }
}
