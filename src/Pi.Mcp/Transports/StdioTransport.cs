using System.Diagnostics;
using System.Text;
using Pi.Mcp.Protocol;

namespace Pi.Mcp.Transports;

/// <summary>
/// stdio 传输选项。对应 TS <c>StdioTransportOptions</c>。
/// </summary>
public sealed record StdioTransportOptions
{
    /// <summary>可执行文件（或 PATH 上的命令名）。</summary>
    public required string Command { get; init; }

    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>子进程工作目录（缺省继承）。</summary>
    public string? Cwd { get; init; }

    /// <summary>附加环境变量。</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>是否继承宿主环境（缺省 true；false 时只保留 <see cref="Env"/>）。</summary>
    public bool InheritEnv { get; init; } = true;

    /// <summary><c>pipe</c>（默认，缓冲并回调）或 <c>inherit</c>（直接继承宿主 stderr）。</summary>
    public string Stderr { get; init; } = "pipe";

    /// <summary>stderr 原始块回调（仅 <c>pipe</c> 模式）。对应 TS <c>onStderr</c>。</summary>
    public Action<string>? OnStderr { get; init; }

    /// <summary>单条消息字节上限（缺省 16 MiB）。</summary>
    public int MaxMessageBytes { get; init; } = TransportDefaults.MaxMessageBytes;

    /// <summary>stderr 滚动缓冲字节上限（缺省 64 KiB）。</summary>
    public int MaxStderrBytes { get; init; } = StdioTransport.DefaultMaxStderrBytes;

    /// <summary>关闭时等待服务器自行退出的毫秒数（缺省 2s）。</summary>
    public int CloseTimeoutMs { get; init; } = StdioTransport.DefaultCloseTimeoutMs;
}

/// <summary>
/// stdio 传输：启动子进程，以"每行一条 JSON-RPC 消息"的方式经 stdin/stdout 通信。
/// 对应 TS <c>transports/stdio.ts</c>：逐字节分行、消息上限、stderr 滚动缓冲、
/// 关闭时先关 stdin 再升级为强制终止。
/// </summary>
public sealed class StdioTransport : TransportEvents, IMcpTransport
{
    public const int DefaultMaxStderrBytes = 64 * 1024;
    public const int DefaultCloseTimeoutMs = 2_000;
    private const int StdinCloseGraceMs = 500;

    private readonly StdioTransportOptions _options;
    // 上游 1.5.1 lines.ts：stdio 与 PipeTransport 共用换行分帧 / 大消息 chunk 累积 / 上限与解析。
    private readonly JsonRpcLineReader _lines;
    private Process? _process;
    private CancellationTokenSource? _readCts;
    private string? _protocolVersion;
    private readonly object _stderrGate = new();
    private byte[] _stderr = [];
    private bool _started;
    private bool _closed;

    /// <summary>子进程 stderr 的滚动缓冲（最后 <see cref="StdioTransportOptions.MaxStderrBytes"/> 字节）。</summary>
    public string Stderr
    {
        get
        {
            lock (_stderrGate)
            {
                return Encoding.UTF8.GetString(_stderr);
            }
        }
    }

    /// <summary>子进程 pid（未启动时为 null）。</summary>
    public int? Pid => _process?.Id;

    public StdioTransport(StdioTransportOptions options)
    {
        _options = options;
        _lines = new JsonRpcLineReader(new JsonRpcLineReaderOptions
        {
            MaxMessageBytes = options.MaxMessageBytes,
            OnMessage = EmitMessage,
            OnError = EmitError,
        });
    }

    /// <param name="command">可执行文件（或 PATH 上的命令名）。</param>
    /// <param name="args">命令行参数。</param>
    /// <param name="env">附加环境变量（null 表示继承当前进程环境）。</param>
    /// <param name="workingDirectory">子进程工作目录（缺省继承）。</param>
    public StdioTransport(string command, IReadOnlyList<string>? args = null,
        IReadOnlyDictionary<string, string>? env = null, string? workingDirectory = null)
        : this(new StdioTransportOptions
        {
            Command = command,
            Args = args,
            Env = env,
            Cwd = workingDirectory,
        })
    {
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("MCP stdio transport already started");
        if (_closed) throw new McpConnectionClosedError("MCP stdio transport is closed");
        _started = true;

        var inheritStderr = string.Equals(_options.Stderr, "inherit", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.Command,
            WorkingDirectory = _options.Cwd ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = !inheritStderr,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (!inheritStderr) startInfo.StandardErrorEncoding = Encoding.UTF8;

        foreach (var arg in _options.Args ?? []) startInfo.ArgumentList.Add(arg);
        if (!_options.InheritEnv) startInfo.Environment.Clear();
        if (_options.Env is not null)
        {
            foreach (var (key, value) in _options.Env) startInfo.Environment[key] = value;
        }

        var process = Process.Start(startInfo)
            ?? throw new McpError(JsonRpcErrorCodes.InternalError, $"Failed to spawn MCP server process: {_options.Command}");
        _process = process;
        _readCts = new CancellationTokenSource();

        _ = PumpStdoutAsync(process, _readCts.Token);
        if (!inheritStderr) _ = PumpStderrAsync(process, _readCts.Token);

        process.Exited += (_, _) => EmitClose();
        process.EnableRaisingEvents = true;
        if (process.HasExited) EmitClose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (!_started || _closed) throw new McpConnectionClosedError("MCP stdio transport is closed");
        var process = _process
            ?? throw new McpConnectionClosedError("MCP server is not started");
        if (process.HasExited) throw new McpConnectionClosedError("MCP server process has exited");

        var line = JsonRpc.Serialize(message).ToJsonString() + "\n";
        await process.StandardInput.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) return;
        _closed = true;
        _readCts?.Cancel();
        var process = _process;
        if (process is null)
        {
            EmitClose();
            return;
        }

        var closeTimeout = Math.Max(0, _options.CloseTimeoutMs);
        var grace = Math.Min(StdinCloseGraceMs, closeTimeout);
        try
        {
            if (process.HasExited)
            {
                try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
                return;
            }

            // 规范流程：关 stdin 让服务器自行退出，随后升级为信号/强制终止。
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
            if (!await WaitForExitAsync(process, grace, cancellationToken).ConfigureAwait(false))
            {
                KillTree(process);
            }
            if (!await WaitForExitAsync(process, closeTimeout, cancellationToken).ConfigureAwait(false))
            {
                KillTree(process);
                await WaitForExitAsync(process, 1_000, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
        }
        catch (InvalidOperationException)
        {
            // 进程已退出：忽略。
        }
        finally
        {
            _process = null;
            EmitClose();
        }
    }

    /// <inheritdoc />
    public void SetProtocolVersion(string version) => _protocolVersion = version;

    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs, CancellationToken cancellationToken)
    {
        if (timeoutMs <= 0) return process.HasExited;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return process.HasExited;
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (NotSupportedException) { }
    }

    private async Task PumpStdoutAsync(Process process, CancellationToken cancellationToken)
    {
        // 上游 1.5.1 #11761 / lines.ts：换行分帧、大消息 chunk 累积到换行才合并一次、消息上限、
        // CR 修剪、解析失败仅报告、结尾残留按未完成消息报错——全部由共享 JsonRpcLineReader 负责
        // （与 PipeTransport 共用，避免 stdio 与 pipe 各写一份 O(n²) 逻辑）。
        var chunk = new byte[8192];
        try
        {
            var stream = process.StandardOutput.BaseStream;
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                // ReadStdout 内部会复制未完成分片，可安全传入复用的 chunk 切片。
                _lines.ReadStdout(chunk.AsSpan(0, read).ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            EmitError(error);
        }

        _lines.End();
        EmitClose();
    }

    private async Task PumpStderrAsync(Process process, CancellationToken cancellationToken)
    {
        var chunk = new byte[8192];
        try
        {
            var stream = process.StandardError.BaseStream;
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) return;
                AppendStderr(chunk.AsSpan(0, read));
                _options.OnStderr?.Invoke(Encoding.UTF8.GetString(chunk, 0, read));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            EmitError(error);
        }
    }

    /// <summary>追加 stderr 原始字节，只保留最后 <see cref="StdioTransportOptions.MaxStderrBytes"/> 字节。</summary>
    private void AppendStderr(ReadOnlySpan<byte> chunk)
    {
        lock (_stderrGate)
        {
            var limit = Math.Max(0, _options.MaxStderrBytes);
            var combined = new byte[_stderr.Length + chunk.Length];
            _stderr.CopyTo(combined, 0);
            chunk.CopyTo(combined.AsSpan(_stderr.Length));
            _stderr = combined.Length <= limit ? combined : combined[^limit..];
        }
    }
}
