using System.Text;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>BashExecutorOptions</c>。</summary>
public sealed record BashExecutorOptions
{
    /// <summary>流式输出块（已消毒）。</summary>
    public Action<string>? OnChunk { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// <see cref="AgentSession.ExecuteBashAsync"/> 的选项。对应 TS <c>executeBash</c> 的内联 options。
/// </summary>
public sealed record ExecuteBashOptions
{
    /// <summary>true 时该结果不进 LLM 上下文（<c>!!</c> 前缀）。</summary>
    public bool? ExcludeFromContext { get; init; }

    /// <summary>流式更新事件的 id（TS <c>id?</c>）。</summary>
    public string? Id { get; init; }

    /// <summary>自定义执行操作（SSH、容器等）。缺省走本地 shell。</summary>
    public IBashToolOperations? Operations { get; init; }
}

/// <summary>对应 TS <c>BashResult</c>。</summary>
public sealed record BashResult
{
    /// <summary>合并后的 stdout+stderr（已消毒，可能被截断）。</summary>
    public required string Output { get; init; }

    /// <summary>进程退出码；被杀/取消时为 null。</summary>
    public int? ExitCode { get; init; }

    /// <summary>是否因取消令牌中止。</summary>
    public required bool Cancelled { get; init; }

    /// <summary>输出是否被截断。</summary>
    public required bool Truncated { get; init; }

    /// <summary>超出截断阈值时，完整输出所在的临时文件路径。</summary>
    public string? FullOutputPath { get; init; }
}

/// <summary>
/// 带流式输出与取消的 bash 执行，供 AgentSession.executeBash 与需要跑命令的模式共用。
/// 对应 TS <c>core/bash-executor.ts</c>。
/// </summary>
public static class BashExecutor
{
    /// <summary>
    /// 用自定义 <see cref="IBashToolOperations"/> 执行命令（SSH、容器等远程执行）。
    /// 对应 TS <c>executeBashWithOperations</c>。
    /// </summary>
    public static async Task<BashResult> ExecuteBashWithOperationsAsync(
        string command,
        string cwd,
        IBashToolOperations operations,
        BashExecutorOptions? options = null)
    {
        options ??= new BashExecutorOptions();
        var outputChunks = new List<string>();
        var outputBytes = 0;
        var maxOutputBytes = Truncate.DefaultMaxBytes * 2;

        string? tempFilePath = null;
        FileStream? tempFileStream = null;
        StreamWriter? tempFileWriter = null;
        var totalBytes = 0L;
        var decoder = Encoding.UTF8.GetDecoder();

        // Unfinished escape sequence at the end of the previous chunk, completed by the next chunk.
        var pendingAnsi = string.Empty;

        void EnsureTempFile()
        {
            if (tempFilePath is not null)
            {
                return;
            }

            (tempFilePath, tempFileStream) = OutputFiles.CreateOutputFileStream("pi-bash", ".log");
            tempFileWriter = new StreamWriter(tempFileStream, Encoding.UTF8, leaveOpen: false)
            {
                AutoFlush = true,
            };
            foreach (var chunk in outputChunks)
            {
                tempFileWriter.Write(chunk);
            }
        }

        void AppendText(string rawText)
        {
            // Sanitize: strip ANSI, replace binary garbage, normalize newlines.
            var text = Shell.SanitizeBinaryOutput(AnsiText.StripAnsi(rawText)).Replace("\r", "");
            if (text.Length == 0)
            {
                return;
            }

            if (totalBytes > Truncate.DefaultMaxBytes)
            {
                EnsureTempFile();
            }

            tempFileWriter?.Write(text);

            outputChunks.Add(text);
            outputBytes += text.Length;
            while (outputBytes > maxOutputBytes && outputChunks.Count > 1)
            {
                var removed = outputChunks[0];
                outputChunks.RemoveAt(0);
                outputBytes -= removed.Length;
            }

            options!.OnChunk?.Invoke(text);
        }

        void OnData(byte[] data)
        {
            totalBytes += data.Length;
            var chars = new char[decoder.GetCharCount(data, 0, data.Length, flush: false)];
            decoder.GetChars(data, 0, data.Length, chars, 0, flush: false);

            // Escape sequences can straddle chunk boundaries; hold an unfinished suffix back and
            // prepend it to the next chunk instead of letting its tail through as literal text (#10504).
            var (complete, pending) = AnsiText.SplitIncompleteAnsiSuffix(pendingAnsi + new string(chars));
            pendingAnsi = pending;
            AppendText(complete);
        }

        void FlushOutput()
        {
            var flushBuffer = new char[4];
            var flushCount = decoder.GetChars([], 0, 0, flushBuffer, 0, flush: true);
            var rest = pendingAnsi + new string(flushBuffer, 0, flushCount);
            pendingAnsi = string.Empty;
            AppendText(rest);
        }

        int? exitCode = null;
        try
        {
            var result = await operations.ExecAsync(
                    command,
                    cwd,
                    new ShellExecOptions { OnData = OnData },
                    options.CancellationToken)
                .ConfigureAwait(false);
            exitCode = result.ExitCode;
        }
        catch (Exception) when (options.CancellationToken.IsCancellationRequested)
        {
            // An aborted command still returns the output it produced so far.
        }
        catch
        {
            tempFileWriter?.Dispose();
            throw;
        }

        FlushOutput();
        var fullOutput = string.Concat(outputChunks);
        var truncationResult = Truncate.TruncateTail(fullOutput);
        if (truncationResult.Truncated)
        {
            EnsureTempFile();
        }

        tempFileWriter?.Dispose();
        tempFileWriter = null;
        tempFileStream = null;
        var cancelled = options.CancellationToken.IsCancellationRequested;

        return new BashResult
        {
            Output = truncationResult.Truncated ? truncationResult.Content : fullOutput,
            ExitCode = cancelled ? null : exitCode,
            Cancelled = cancelled,
            Truncated = truncationResult.Truncated,
            FullOutputPath = tempFilePath,
        };
    }
}
