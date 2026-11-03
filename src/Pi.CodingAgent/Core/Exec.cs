// ============================================================================
// Exec — port of core/exec.ts (4d-2b)
// ============================================================================
//
// Shared command execution for extensions and custom tools (`pi.exec`). The TS version spawns
// with `shell: false`, accumulates stdout/stderr, and kills on timeout or abort (SIGTERM, then
// SIGKILL after 5s). The port maps that onto `ChildProcess` (utils/child-process.ts, already
// ported in 4a): the same spawn flags, the same kill ladder, and `WaitForChildProcessAsync`
// for the "do not hang on inherited stdio handles" wait.

using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>Options for <see cref="Exec.ExecCommand"/>. Port of the TS <c>ExecOptions</c>.</summary>
public sealed record ExecOptions
{
    /// <summary>Cancels the command; an already-cancelled token kills it immediately.</summary>
    public CancellationToken Signal { get; init; }

    /// <summary>Timeout in milliseconds. Values &lt;= 0 mean no timeout.</summary>
    public int? Timeout { get; init; }

    /// <summary>Working directory; defaults to the current directory.</summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// Combined stdout/stderr byte limit; exceeding it terminates the command with
    /// <see cref="ExecResult.Truncated"/> set. Default: 256 MiB.
    /// </summary>
    public long? MaxBuffer { get; init; }
}

/// <summary>Result of <see cref="Exec.ExecCommand"/>. Port of the TS <c>ExecResult</c>.</summary>
/// <remarks>
/// <c>Truncated</c> means output passed <see cref="ExecOptions.MaxBuffer"/>: stdout and stderr are
/// incomplete and <c>Code</c> is nonzero (TS <c>c466a3595</c>).
/// </remarks>
public sealed record ExecResult(string Stdout, string Stderr, int Code, bool Killed, bool Truncated = false);

/// <summary>Port of <c>core/exec.ts</c>.</summary>
public static class Exec
{
    /// <summary>Grace period after SIGTERM before SIGKILL. TS: the 5s force-kill timer.</summary>
    private const int KillGraceMs = 5000;

    /// <summary>
    /// Default combined output cap. Node also clamps to its string limit
    /// (<c>buffer.constants.MAX_STRING_LENGTH</c>); .NET strings are bounded by memory only, so the
    /// fixed 256 MiB applies.
    /// </summary>
    private const long DefaultMaxBuffer = 256L * 1024 * 1024;

    /// <summary>Execute a command and return its captured output, exit code and killed flag.</summary>
    public static async Task<ExecResult> ExecCommand(
        string command,
        IReadOnlyList<string> args,
        string cwd,
        ExecOptions? options = null)
    {
        ChildProcessHandle process;
        try
        {
            process = ChildProcess.SpawnProcess(
                command,
                args,
                new SpawnOptions
                {
                    Cwd = cwd,
                    Stdio = [StdioMode.Ignore, StdioMode.Pipe, StdioMode.Pipe],
                    MaxOutputBytes = options?.MaxBuffer ?? DefaultMaxBuffer,
                });
        }
        catch (Exception error)
        {
            // Node 的 spawn 失败走 reject 分支：返回 code 1，并把错误信息塞进 stderr。
            // C# 这边 Process.Start 直接抛（找不到可执行文件是 Win32Exception），
            // 不接住的话 "command not found" 只剩一个异常，调用方看不到 ENOENT 之类的原因。
            return new ExecResult("", error.Message, 1, false);
        }

        var killed = false;
        void KillProcess()
        {
            if (killed) return;
            killed = true;
            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // The process may have exited between the check and the kill; TS ignores ESRCH too.
            }

            // Force kill after the grace period if SIGTERM did not work.
            _ = Task.Delay(KillGraceMs).ContinueWith(
                _ =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch (Exception)
                    {
                        // Best effort, like the TS timer.
                    }
                },
                TaskScheduler.Default);
        }

        using var timeoutCts = options?.Timeout is > 0
            ? new CancellationTokenSource(options.Timeout.Value)
            : null;
        using var linkedCts = timeoutCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(options!.Signal, timeoutCts.Token);
        var cancellationToken = linkedCts?.Token ?? options?.Signal ?? CancellationToken.None;

        await using var registration = cancellationToken.Register(KillProcess);

        var stdoutTask = process.ReadStdoutAsync();
        var stderrTask = process.ReadStderrAsync();

        int code;
        try
        {
            code = await ChildProcess.WaitForChildProcessAsync(process) ?? 0;
        }
        catch (Exception error)
        {
            // TS resolves with code 1 when the wait fails (e.g. the spawn itself failed) and keeps the
            // spawn error visible through stderr (TS `c60de42e9`).
            var partialStderr = await stderrTask;
            return new ExecResult(
                await stdoutTask,
                partialStderr.Length > 0 ? partialStderr : error.Message,
                1,
                killed);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // The pump kills the child when the output cap is passed; report it the way TS does.
        var truncated = process.OutputLimitExceeded;
        return new ExecResult(stdout, stderr, code, killed || truncated, truncated);
    }
}
