using System.Text;
using Pi.Mcp.Protocol;

namespace Pi.Mcp.Transports;

/// <summary>构造 <see cref="JsonRpcLineReader"/> 的回调与上限。</summary>
public sealed class JsonRpcLineReaderOptions
{
    public int? MaxMessageBytes { get; init; }
    public int? MaxStderrBytes { get; init; }
    /// <summary>每个 stderr 块（按 UTF-8 解码）回调。</summary>
    public Action<string>? OnStderr { get; init; }
    public required Action<JsonRpcMessage> OnMessage { get; init; }
    public required Action<Exception> OnError { get; init; }
}

/// <summary>
/// 把程序输出按 MCP stdio 帧读取：stdout 上每行一条 JSON-RPC 消息，stderr 保留尾部诊断。
/// 对应 TS <c>transports/lines.ts</c> 的 <c>JsonRpcLineReader</c>（1.5.1 新增，供
/// <see cref="PipeTransport"/> 与 stdio 共用）。
/// <para>上游 1.5.1 #11761：未完成消息按 chunk 累积，到换行才合并一次，避免大消息分多次到达时
/// 反复复制/扫描。</para>
/// </summary>
public sealed class JsonRpcLineReader
{
    private const int DefaultMaxStderrBytes = 64 * 1024;

    private readonly int _maxMessageBytes;
    private readonly int _maxStderrBytes;
    private readonly Action<string>? _onStderr;
    private readonly Action<JsonRpcMessage> _onMessage;
    private readonly Action<Exception> _onError;

    private readonly List<byte[]> _stdoutChunks = [];
    private int _stdoutPendingBytes;
    private byte[] _stderrBuffer = [];

    public JsonRpcLineReader(JsonRpcLineReaderOptions options)
    {
        _maxMessageBytes = options.MaxMessageBytes ?? TransportDefaults.MaxMessageBytes;
        _maxStderrBytes = options.MaxStderrBytes ?? DefaultMaxStderrBytes;
        _onStderr = options.OnStderr;
        _onMessage = options.OnMessage;
        _onError = options.OnError;
    }

    /// <summary>stderr 的最后 <see cref="JsonRpcLineReaderOptions.MaxStderrBytes"/> 字节。</summary>
    public string Stderr => Encoding.UTF8.GetString(_stderrBuffer);

    /// <summary>喂入一段 stdout 字节，分发其中每条完整消息；未完成部分留到下一次。</summary>
    public void ReadStdout(byte[] data)
    {
        var start = 0;
        while (true)
        {
            var newline = Array.IndexOf(data, (byte)'\n', start);
            if (newline < 0)
            {
                if (start < data.Length)
                {
                    // 复制，调用方可能复用该 chunk 的内存。
                    var tail = new byte[data.Length - start];
                    Array.Copy(data, start, tail, 0, tail.Length);
                    _stdoutChunks.Add(tail);
                    _stdoutPendingBytes += tail.Length;
                }
                if (_stdoutPendingBytes > _maxMessageBytes)
                {
                    _stdoutChunks.Clear();
                    _stdoutPendingBytes = 0;
                    _onError(new InvalidOperationException($"MCP stdio message exceeds {_maxMessageBytes} bytes"));
                }
                return;
            }

            var line = new byte[newline - start];
            Array.Copy(data, start, line, 0, line.Length);
            start = newline + 1;
            if (_stdoutChunks.Count > 0)
            {
                _stdoutChunks.Add(line);
                line = Join(_stdoutChunks, _stdoutPendingBytes + line.Length);
                _stdoutChunks.Clear();
                _stdoutPendingBytes = 0;
            }

            if (line.Length > _maxMessageBytes)
            {
                _onError(new InvalidOperationException($"MCP stdio message exceeds {_maxMessageBytes} bytes"));
                continue;
            }

            var text = Encoding.UTF8.GetString(line);
            if (text.Length > 0 && text[^1] == '\r') text = text[..^1];
            if (string.IsNullOrWhiteSpace(text)) continue;
            try
            {
                _onMessage(JsonRpc.ParseText(text));
            }
            catch (Exception error)
            {
                _onError(error);
            }
        }
    }

    /// <summary>喂入一段 stderr 字节，更新尾部缓冲并回调。</summary>
    public void ReadStderr(byte[] data)
    {
        var combined = new byte[_stderrBuffer.Length + data.Length];
        _stderrBuffer.CopyTo(combined, 0);
        data.CopyTo(combined, _stderrBuffer.Length);
        _stderrBuffer = combined.Length <= _maxStderrBytes ? combined : combined[^_maxStderrBytes..];
        _onStderr?.Invoke(Encoding.UTF8.GetString(data));
    }

    /// <summary>stdout 结束：没有换行结尾的残留消息按错误报告并丢弃。</summary>
    public void End()
    {
        var trailing = Join(_stdoutChunks, _stdoutPendingBytes);
        if (!string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(trailing)))
        {
            _onError(new InvalidOperationException("MCP stdio server closed with an incomplete JSON-RPC message"));
        }
        _stdoutChunks.Clear();
        _stdoutPendingBytes = 0;
    }

    private static byte[] Join(List<byte[]> chunks, int total)
    {
        if (chunks.Count == 0) return [];
        var joined = new byte[total];
        var offset = 0;
        foreach (var part in chunks)
        {
            part.CopyTo(joined, offset);
            offset += part.Length;
        }
        return joined;
    }
}
