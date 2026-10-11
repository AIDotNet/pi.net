using System.Text;

namespace Pi.CodingAgent.Core;

/// <summary>
/// 接管 <see cref="Console.Out"/>，把普通 stdout 写重定向到 stderr，同时保留一条直写原始 stdout 的通道。
/// 对应 TS <c>core/output-guard.ts</c>。
/// </summary>
/// <remarks>
/// Node 侧替换的是 <c>process.stdout.write</c>；端口替换 <see cref="Console.Out"/>。
/// 写入失败且错误码为瞬时背压时重试；其它失败对齐 TS 的 <c>process.exit(1)</c>。
/// </remarks>
public static class OutputGuard
{
    private const int RawStdoutRetryDelayMs = 10;

    private static readonly object Gate = new();
    private static readonly RedirectToErrorWriter RedirectedOut = new();

    private static TextWriter? _rawStdout;
    private static TextWriter? _originalOut;
    private static Task _writeTail = Task.CompletedTask;

    /// <summary>把 <see cref="Console.Out"/> 重定向到 stderr。对应 TS <c>takeOverStdout</c>。</summary>
    public static void TakeOverStdout()
    {
        lock (Gate)
        {
            if (_originalOut is not null)
            {
                return;
            }

            _rawStdout = Console.Out;
            _originalOut = Console.Out;
            Console.SetOut(RedirectedOut);
        }
    }

    /// <summary>恢复接管前的 <see cref="Console.Out"/>。对应 TS <c>restoreStdout</c>。</summary>
    public static void RestoreStdout()
    {
        lock (Gate)
        {
            if (_originalOut is null)
            {
                return;
            }

            Console.SetOut(_originalOut);
            _originalOut = null;
            _rawStdout = null;
        }
    }

    /// <summary>当前是否已接管 stdout。对应 TS <c>isStdoutTakenOver</c>。</summary>
    public static bool IsStdoutTakenOver
    {
        get
        {
            lock (Gate)
            {
                return _originalOut is not null;
            }
        }
    }

    /// <summary>直写原始 stdout（绕过重定向）。对应 TS <c>writeRawStdout</c>。</summary>
    public static void WriteRawStdout(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            _writeTail = _writeTail.ContinueWith(
                static async (_, state) =>
                {
                    try
                    {
                        await WriteRawStdoutChunkAsync((string)state!).ConfigureAwait(false);
                    }
                    catch
                    {
                        Environment.Exit(1);
                    }
                },
                text,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>等到直写队列排空。对应 TS <c>waitForRawStdoutBackpressure</c>。</summary>
    public static async Task WaitForRawStdoutBackpressureAsync()
    {
        while (true)
        {
            Task tail;
            lock (Gate)
            {
                tail = _writeTail;
            }

            await tail.ConfigureAwait(false);

            lock (Gate)
            {
                if (ReferenceEquals(tail, _writeTail))
                {
                    return;
                }
            }
        }
    }

    /// <summary>排空直写队列并 flush。对应 TS <c>flushRawStdout</c>。</summary>
    public static async Task FlushRawStdoutAsync()
    {
        await WaitForRawStdoutBackpressureAsync().ConfigureAwait(false);
        await WriteRawStdoutChunkAsync(string.Empty).ConfigureAwait(false);
    }

    private static TextWriter GetRawStdoutWrite()
    {
        lock (Gate)
        {
            return _rawStdout ?? Console.Out;
        }
    }

    private static async Task WriteRawStdoutChunkAsync(string text)
    {
        while (true)
        {
            try
            {
                var writer = GetRawStdoutWrite();
                if (text.Length > 0)
                {
                    await writer.WriteAsync(text).ConfigureAwait(false);
                }
                else
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }

                return;
            }
            catch (IOException ex) when (IsTransient(ex))
            {
                await Task.Delay(RawStdoutRetryDelayMs).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(IOException ex)
    {
        var code = ex.HResult;
        // POSIX EAGAIN / EWOULDBLOCK / ENOBUFS 的常见 HRESULT 包装；Windows 上按消息兜底。
        return code is unchecked((int)0x8007000B) or unchecked((int)0x80070023)
            || ex.Message.Contains("EAGAIN", StringComparison.Ordinal)
            || ex.Message.Contains("EWOULDBLOCK", StringComparison.Ordinal)
            || ex.Message.Contains("ENOBUFS", StringComparison.Ordinal);
    }

    private sealed class RedirectToErrorWriter : TextWriter
    {
        public override Encoding Encoding => Console.Error.Encoding;

        public override void Write(char value) => Console.Error.Write(value);

        public override void Write(char[] buffer, int index, int count) => Console.Error.Write(buffer, index, count);

        public override void Write(string? value) => Console.Error.Write(value);

        public override void WriteLine(string? value) => Console.Error.WriteLine(value);
    }
}
