// ============================================================================
// Coordinator socket channel — the transport shared by both halves of
// experimental/coordinator.ts (4g-3c-3)
// ============================================================================
//
// The coordinator is an intentionally opaque message router over Unix domain sockets: one control
// socket where servers and peers register, one public socket proxied to the current server. Both
// halves speak newline-delimited JSON with a shared ceiling
// (<see cref="InternalProcess.MaxControlLineBytes"/>), and both destroy a connection whose peer
// misbehaves — an invalid line, an oversized buffer, or a handler that throws.
//
// TS models a connection as a Node <c>Socket</c> with 'data' / 'error' / 'close' events. This channel
// keeps the same surface: a receive loop feeds framed lines to a handler, every failure path
// destroys the connection with the error that caused it, and a single <see cref="Closed"/> event
// carries that error (null meaning the peer went away). Node raises 'error' before 'close', so one
// event with the error argument reproduces both listeners' effect.
//
// Framing works on bytes, not decoded text: 0x0A can never appear inside a multi-byte UTF-8
// sequence, so a chunk boundary in the middle of a character cannot produce a phantom newline
// (Node's utf8 stream decoder has the same property).

using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Pi.CodingAgent.Experimental;

/// <summary>
/// One coordinator connection: newline-delimited JSON in both directions over a Unix domain socket.
/// Port of the socket behaviour TS gets from <c>node:net</c>.
/// </summary>
public sealed class CoordinatorSocket
{
    private readonly Socket _socket;
    private readonly List<byte> _pending = [];
    private readonly Lock _writeGate = new();
    private readonly Queue<JsonNode?> _writeQueue = [];
    private readonly CancellationTokenSource _reads = new();
    private Action<JsonNode?>? _onMessage;
    private bool _destroyed;
    private bool _reading;
    private bool _writing;

    private CoordinatorSocket(Socket socket)
    {
        _socket = socket;
    }

    /// <summary>Raised once when the connection ends: the destroy error, or null for a remote close.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>Whether the connection has been destroyed. TS <c>socket.destroyed</c>.</summary>
    public bool IsDestroyed
    {
        get { lock (_writeGate) return _destroyed; }
    }

    /// <summary>TS <c>connectSocket</c>: resolve once connected, reject with the socket error.</summary>
    public static async Task<CoordinatorSocket> ConnectAsync(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path)).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new CoordinatorSocket(socket);
    }

    /// <summary>Wrap an accepted socket. TS passes accepted sockets straight to the readers.</summary>
    public static CoordinatorSocket Accept(Socket socket) => new(socket);

    /// <summary>
    /// Start the receive loop. A framing failure or a throwing handler destroys the connection with
    /// the error that caused it — TS's readers do the same from their catch blocks.
    /// </summary>
    public void StartReading(Action<JsonNode?> onMessage)
    {
        lock (_writeGate)
        {
            if (_destroyed || _reading) return;
            _reading = true;
            _onMessage = onMessage;
        }

        _ = Task.Run(() => ReceiveLoopAsync(), CancellationToken.None);
    }

    /// <summary>
    /// TS <c>writeJsonLine</c>: resolve when the line is written, reject with the socket error.
    /// Callers await this, so concurrent calls are serialised by the caller itself.
    /// </summary>
    public async Task WriteLineAsync(JsonNode? message)
    {
        var line = InternalProcess.EncodeControlLine(message);
        await _socket.SendAsync(Encoding.UTF8.GetBytes(line), SocketFlags.None).ConfigureAwait(false);
    }

    /// <summary>
    /// TS <c>writeRoutedLine</c>: fire and forget, and only while the connection is alive. Writes go
    /// through a per-connection queue drained by one task, so lines written back to back reach the
    /// peer in that order (Node queues them on the socket) — a plain <c>Task.Run</c> per write would
    /// let concurrent pool threads race for the socket.
    /// </summary>
    public void WriteLine(JsonNode? message)
    {
        if (IsDestroyed) return;
        lock (_writeGate)
        {
            _writeQueue.Enqueue(message);
            if (_writing) return;
            _writing = true;
        }

        _ = Task.Run(() => DrainWritesAsync(), CancellationToken.None);
    }

    private async Task DrainWritesAsync()
    {
        while (true)
        {
            JsonNode? message;
            lock (_writeGate)
            {
                if (_writeQueue.Count == 0)
                {
                    _writing = false;
                    return;
                }

                message = _writeQueue.Dequeue();
            }

            try
            {
                await WriteLineAsync(message).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Destroy(error);
                return;
            }
        }
    }

    /// <summary>Raw byte write for the public proxy, which pipes opaque bytes rather than lines.</summary>
    public async Task WriteRawAsync(ReadOnlyMemory<byte> bytes)
        => await _socket.SendAsync(bytes, SocketFlags.None).ConfigureAwait(false);

    /// <summary>Raw byte read for the public proxy; 0 means the peer closed.</summary>
    public async Task<int> ReceiveRawAsync(byte[] buffer, CancellationToken cancellationToken = default)
        => await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);

    /// <summary>TS <c>socket.destroy(error)</c>: idempotent, and reports the error to <see cref="Closed"/>.</summary>
    public void Destroy(Exception? error = null)
    {
        lock (_writeGate)
        {
            if (_destroyed) return;
            _destroyed = true;
        }

        try
        {
            _reads.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已在关闭流程中取消过。
        }

        try
        {
            _socket.Dispose();
        }
        catch (Exception)
        {
            // 释放失败不掩盖原始关闭原因。
        }

        try
        {
            Closed?.Invoke(error);
        }
        catch
        {
            // 关闭观察者不得影响其余连接的拆卸。
        }
    }

    public void Dispose()
    {
        Destroy();
        _reads.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await _socket.ReceiveAsync(buffer, SocketFlags.None, _reads.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    // 对端关闭：TS 的 'close' 不带错误参数。
                    Destroy();
                    return;
                }

                if (!TryFrame(buffer[..read]))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 本地销毁取消了读取。
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            Destroy(error);
        }
    }

    /// <summary>
    /// Buffer a chunk, enforce the ceiling, and dispatch every complete line. Returns false once the
    /// connection has been destroyed by a framing failure.
    /// </summary>
    private bool TryFrame(ReadOnlySpan<byte> chunk)
    {
        List<string> lines;
        lock (_writeGate)
        {
            if (_destroyed) return false;
            _pending.AddRange(chunk.ToArray());
            if (_pending.Count > InternalProcess.MaxControlLineBytes)
            {
                Destroy(new InvalidOperationException("Coordinator message is too large"));
                return false;
            }

            lines = [];
            while (true)
            {
                var newline = CollectionsMarshal.AsSpan(_pending).IndexOf((byte)'\n');
                if (newline < 0) break;
                lines.Add(Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_pending)[..newline]));
                _pending.RemoveRange(0, newline + 1);
            }
        }

        foreach (var line in lines)
        {
            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (Exception error)
            {
                Destroy(new InvalidOperationException("Coordinator sent invalid JSON", error));
                return false;
            }

            try
            {
                _onMessage!(message);
            }
            catch (Exception error)
            {
                Destroy(error);
                return false;
            }
        }

        return true;
    }
}
