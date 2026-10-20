using System.Text;
using Pi.Chord.Context;
using Pi.Durable.Env;
using Xunit;
using Context = Pi.Chord.Context.Context;

namespace Pi.Env.Tests;

/// <summary>
/// 远程执行环境的定点行为。对应 TS <c>packages/env/test/remote.test.ts</c>：输出窗口的计量、立即取消、
/// 无输出超时、丢失连接后的句柄、每次启动重新求值命令、跨传输块的大文件，以及 <c>cleanup</c> 的「杀而不中止」。
/// </summary>
public class RemoteEnvTests
{
    private static Context Background => Context.Background;

    private static (RemoteExecutionEnv Env, Connection Connection, TempDir Directory) NewEnvironment()
    {
        var directory = new TempDir();
        var connection = new Connection(TestPaths.Daemon);
        var env = new RemoteExecutionEnv(new RemoteExecutionEnvOptions
        {
            Connection = connection,
            Id = "pi-env:test",
            Cwd = directory.Path,
        });
        return (env, connection, directory);
    }

    [Fact]
    public async Task TransfersOnlyAboutAWindowOfOutputWithExactCounts()
    {
        if (!TestCommands.HasPosixShell)
        {
            Assert.Skip("no POSIX shell on this platform");
            return;
        }

        var (env, connection, directory) = NewEnvironment();
        using (connection)
        using (directory)
        {
            const int total = 5_000_000;
            long delivered = 0;
            long skipped = 0;
            var tail = "";
            var result = await env.ExecAsync(
                ShellCommand.FromArgv([.. TestCommands.PosixShell, $"yes 0123456789abcdef | head -c {total}"]),
                new ShellExecOptions
                {
                    Window = new ShellOutputWindow
                    {
                        MaxBytes = 1000,
                        MaxLines = 20,
                        MinIntervalMs = 20,
                        BytesPerSecond = 1_000_000,
                    },
                    OnOutput = (text, _, info) =>
                    {
                        delivered += Encoding.UTF8.GetByteCount(text);
                        if (info.Skipped is { } skip)
                        {
                            skipped += skip.Bytes;
                            tail = "";
                        }

                        tail += text;
                    },
                },
                Background);

            Assert.True(result.IsOk, result.IsOk ? null : result.Error.Message);
            Assert.Equal(0L, result.Value.ExitCode);
            Assert.Equal(total, delivered + skipped);
            Assert.True(skipped > 0, "expected the environment to omit output");
            Assert.True(delivered < total / 10, $"delivered {delivered} of {total}");
            // 最后一次省略之后是直到结尾的全部输出，超过窗口。
            var pattern = "0123456789abcdef\n";
            var full = string.Concat(Enumerable.Repeat(pattern, (total / pattern.Length) + 1))[..total];
            Assert.EndsWith(tail, full, StringComparison.Ordinal);
            Assert.True(tail.Length > 1000 || tail.Count(c => c == '\n') > 20);
        }
    }

    [Fact]
    public async Task AbortsCommandsCancelledRightAfterTheyAreSent()
    {
        var (env, connection, directory) = NewEnvironment();
        using (connection)
        using (directory)
        {
            var started = Environment.TickCount64;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                using var cancellation = new CancellationTokenSource();
                var context = ContextSignals.WithAbortSignal(cancellation.Token, Background);
                var running = env.ExecAsync(ShellCommand.FromArgv([.. TestCommands.LongRunning]), null, context);
                if (attempt % 2 == 0) cancellation.Cancel();
                else _ = Task.Delay(attempt % 5).ContinueWith(_ => cancellation.Cancel(), TaskScheduler.Default);

                var result = await running;
                Assert.False(result.IsOk, "expected the command to be aborted");
                Assert.Equal(ExecutionErrorCode.Aborted, result.Error.Code);
            }

            // 二十次中止，每次都要终止一次进程树；Windows 上的这部分开销见 KillBudget。
            Assert.True(Environment.TickCount64 - started < 10_000 + TestCommands.KillBudget(20));
        }
    }

    [Fact]
    public async Task TimesOutACommandThatProducesNoOutput()
    {
        var (env, connection, directory) = NewEnvironment();
        using (connection)
        using (directory)
        {
            var started = Environment.TickCount64;
            var result = await env.ExecAsync(
                ShellCommand.FromArgv([.. TestCommands.SilentLongRunning]),
                new ShellExecOptions { Timeout = 0.3 },
                Background);

            Assert.False(result.IsOk);
            Assert.Equal(ExecutionErrorCode.Timeout, result.Error.Code);
            // 截止时间一到就要落定，剩下的只有终止进程树的开销（Windows 上约 1.1–1.8 秒）。
            var elapsed = Environment.TickCount64 - started;
            Assert.True(elapsed < 3_000 + TestCommands.KillBudget(1), $"timeout took {elapsed} ms");
        }
    }

    [Fact]
    public async Task FailsRequestsOnHandlesOfALostConnection()
    {
        var (env, connection, directory) = NewEnvironment();
        using (connection)
        using (directory)
        {
            Assert.True((await env.WriteFileAsync("a.txt", "AAA", Background)).IsOk);
            Assert.True((await env.WriteFileAsync("b.txt", "BBB", Background)).IsOk);
            var a = (await env.OpenBinaryReaderAsync("a.txt", null, Background)).Value;
            var info = await connection.InfoAsync();

            TestCommands.Kill(info.Pid);
            await Task.Delay(300);

            // 新守护进程的句柄号又从 1 开始。
            var b = (await env.OpenBinaryReaderAsync("b.txt", null, Background)).Value;
            Assert.NotEqual(info.Pid, (await connection.InfoAsync()).Pid);

            var stale = await a.ReadAsync(0, 3, Background);
            Assert.False(stale.IsOk);
            Assert.Equal(FileErrorCode.Unknown, stale.Error.Code);
            Assert.Equal("BBB", Encoding.UTF8.GetString((await b.ReadAsync(0, 3, Background)).Value));

            await a.CloseAsync(Background);
            await b.CloseAsync(Background);
        }
    }

    [Fact]
    public async Task ComputesTheCommandAtEachStartAndRetriesAFailedStart()
    {
        using var directory = new TempDir();
        var starts = 0;
        using var connection = new Connection(new ConnectionOptions
        {
            Command = () =>
            {
                if (Interlocked.Increment(ref starts) == 1)
                {
                    throw new InvalidOperationException("ssh gpu-box failed: Network is unreachable");
                }

                return Task.FromResult<IReadOnlyList<string>>([TestPaths.Daemon]);
            },
        });
        var env = new RemoteExecutionEnv(new RemoteExecutionEnvOptions
        {
            Connection = connection,
            Id = "pi-env:test",
            Cwd = directory.Path,
        });

        var offline = await env.ReadTextFileAsync("missing.txt", Background);
        Assert.False(offline.IsOk);
        Assert.Equal(FileErrorCode.Unknown, offline.Error.Code);
        Assert.Equal("ssh gpu-box failed: Network is unreachable", offline.Error.Message);

        var argv = OperatingSystem.IsWindows()
            ? new[] { "cmd", "/c", "exit", "0" }
            : new[] { "sh", "-c", "exit 0" };
        var command = await env.ExecAsync(ShellCommand.FromArgv(argv), null, Background);

        Assert.True(command.IsOk, command.IsOk ? null : command.Error.Message);
        Assert.Equal(0L, command.Value.ExitCode);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task WritesAndReadsFilesLargerThanOneTransferChunk()
    {
        var (env, connection, directory) = NewEnvironment();
        using (connection)
        using (directory)
        {
            var content = new byte[3_500_017];
            for (var index = 0; index < content.Length; index++) content[index] = (byte)((index * 7919) % 251);

            Assert.True((await env.WriteFileAsync("big.bin", content, Background)).IsOk);
            Assert.True((await env.AppendFileAsync("big.bin", content, Background)).IsOk);

            var read = (await env.ReadBinaryFileAsync("big.bin", Background)).Value;
            Assert.Equal(content.Length * 2, read.Length);
            Assert.Equal(content, read[..content.Length]);
            Assert.Equal(content, read[content.Length..]);

            var reader = (await env.OpenBinaryReaderAsync("big.bin", null, Background)).Value;
            var range = (await reader.ReadAsync(1_000_003, 2_000_000, Background)).Value;
            await reader.CloseAsync(Background);
            Assert.Equal(read[1_000_003..3_000_003], range);
        }
    }

    [Fact]
    public async Task CleanupKillsWithoutAborting()
    {
        var (env, connection, directory) = NewEnvironment();
        using (connection)
        using (directory)
        {
            var running = env.ExecAsync(ShellCommand.FromArgv([.. TestCommands.LongRunning]), null, Background);
            await Task.Delay(400);
            await env.CleanupAsync(Background);

            var result = await running;
            Assert.True(result.IsOk, result.IsOk ? null : result.Error.Message);
            // 被信号杀死的进程报 128 + signal（POSIX）；Windows 的 TerminateProcess 报 1。
            Assert.Equal(OperatingSystem.IsWindows() ? 1L : 137L, result.Value.ExitCode);
        }
    }

    [Fact]
    public async Task ExecReportsOutputAndExitCode()
    {
        if (!TestCommands.HasPosixShell)
        {
            Assert.Skip("no POSIX shell on this platform");
            return;
        }

        var (env, connection, directory) = NewEnvironment();
        using (connection)
        using (directory)
        {
            var output = new StringBuilder();
            var result = await env.ExecAsync(
                ShellCommand.FromArgv([.. TestCommands.PosixShell, "printf %s hello; printf %s err >&2; exit 3"]),
                new ShellExecOptions { OnOutput = (text, _, _) => output.Append(text) },
                Background);

            Assert.True(result.IsOk, result.IsOk ? null : result.Error.Message);
            Assert.Equal(3L, result.Value.ExitCode);
            Assert.Equal("helloerr", output.ToString());
        }
    }
}
