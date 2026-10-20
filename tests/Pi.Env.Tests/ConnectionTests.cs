using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Pi.Env.Tests;

/// <summary>
/// 协议连接层。对应 TS <c>packages/env/src/connection.ts</c>：帧格式、同步行、请求/结果/错误、
/// 取消、丢失会话与启动重试。协议往返用上游 Rust 守护进程验证，传输边界用假守护进程制造。
/// </summary>
public class ConnectionTests
{
    [Fact]
    public async Task Hello_ReportsTheRemoteMachine()
    {
        using var connection = new Connection(TestPaths.Daemon);
        var info = await connection.InfoAsync();

        Assert.Equal(1L, info.Protocol);
        Assert.False(string.IsNullOrEmpty(info.Version));
        Assert.Equal(ExpectedOs(), info.Os);
        Assert.Contains(info.Arch, new[] { "x86_64", "aarch64" });
        Assert.False(string.IsNullOrEmpty(info.Home));
        Assert.False(string.IsNullOrEmpty(info.TmpDir));
        Assert.Equal(OperatingSystem.IsWindows() ? "\\" : "/", info.Separator);
        Assert.True(info.Pid > 0);
        // 同一连接复用同一个守护进程。
        Assert.Equal(info.Pid, (await connection.InfoAsync()).Pid);
    }

    [Fact]
    public async Task RequestsCarryPayloadBothWays()
    {
        using var directory = new TempDir();
        using var connection = new Connection(TestPaths.Daemon);
        var file = directory.File("payload.txt");
        var bytes = Encoding.UTF8.GetBytes("abcdefgh");

        var written = await connection.RequestAsync(
            "write",
            new JsonObject { ["path"] = file },
            new RequestOptions { Payload = bytes });
        Assert.Empty(written.Json);

        var opened = await connection.RequestAsync("open", new JsonObject { ["path"] = file, ["noFollow"] = false });
        var handle = opened.Json["handle"]!.GetValue<long>();
        Assert.Equal((long)bytes.Length, opened.Json["info"]!["size"]!.GetValue<long>());
        Assert.Equal("file", opened.Json["info"]!["kind"]!.GetValue<string>());

        // 不带 offset 时从当前位置读；新句柄的位置是 0。
        var sequential = await connection.RequestAsync(
            "pread",
            new JsonObject { ["handle"] = handle, ["length"] = 3 });
        Assert.Equal("abc", Encoding.UTF8.GetString(sequential.Payload));

        // 带 offset 的位置读。
        var read = await connection.RequestAsync(
            "pread",
            new JsonObject { ["handle"] = handle, ["offset"] = 2, ["length"] = 4 });
        Assert.Equal("cdef", Encoding.UTF8.GetString(read.Payload));

        await connection.RequestAsync("close", new JsonObject { ["handle"] = handle });
        var closed = await Assert.ThrowsAsync<RemoteError>(() => connection.RequestAsync(
            "fstat",
            new JsonObject { ["handle"] = handle }));
        Assert.Equal("EBADF", closed.Code);
    }

    [Fact]
    public async Task DaemonFailure_BecomesRemoteErrorWithCodeAndPath()
    {
        using var directory = new TempDir();
        using var connection = new Connection(TestPaths.Daemon);
        var missing = directory.File("missing.txt");

        var error = await Assert.ThrowsAsync<RemoteError>(() => connection.RequestAsync(
            "lstat",
            new JsonObject { ["path"] = missing }));

        Assert.Equal("ENOENT", error.Code);
        Assert.Equal(missing, error.Path);
        Assert.False(error.Lost);
        Assert.Equal("Remote operation failed", new RemoteError(new JsonObject()).Message);
    }

    [Fact]
    public async Task RequestsOnAStaleSessionFailInsteadOfReachingTheNewDaemon()
    {
        using var directory = new TempDir();
        using var connection = new Connection(TestPaths.Daemon);
        var info = await connection.InfoAsync();
        var session = await connection.SessionIdAsync();

        TestCommands.Kill(info.Pid);
        await Task.Delay(300);

        var stale = await Assert.ThrowsAsync<RemoteError>(() => connection.RequestAsync(
            "lstat",
            new JsonObject { ["path"] = directory.Path },
            new RequestOptions { Session = session }));
        Assert.True(stale.Lost);
        Assert.Equal("pi-env connection lost", stale.Message);

        // 下一次请求启动新守护进程。
        var restarted = await connection.InfoAsync();
        Assert.NotEqual(info.Pid, restarted.Pid);
    }

    [Fact]
    public async Task AFailedStartIsNotRemembered()
    {
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

        var first = await Assert.ThrowsAsync<RemoteError>(() => connection.InfoAsync());
        Assert.Equal("spawn_error", first.Code);
        Assert.True(first.Lost);
        Assert.Equal("ssh gpu-box failed: Network is unreachable", first.Message);

        var info = await connection.InfoAsync();
        Assert.True(info.Pid > 0);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task CancellingARequestSendsCancel()
    {
        using var directory = new TempDir();
        using var connection = new Connection(TestPaths.Daemon);
        using var cancellation = new CancellationTokenSource();

        var exec = connection.RequestAsync(
            "exec",
            ExecRequest(directory.Path),
            new RequestOptions { Signal = cancellation.Token });
        await Task.Delay(300);
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<RemoteError>(() => exec);
        Assert.Equal("aborted", error.Code);
    }

    [Fact]
    public async Task NoiseBeforeTheSyncLineIsLogged()
    {
        var logs = new List<string>();
        using var connection = new Connection(ConnectionOptions.FromCommand(
            TestPaths.FakeCommand("noise"),
            text =>
            {
                lock (logs) logs.Add(text);
            }));

        var info = await connection.InfoAsync();
        Assert.Equal(4242L, info.Pid);
        Assert.Equal("fake", info.Version);
        lock (logs)
        {
            Assert.Contains(logs, text => text.Contains("banner from the login shell", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ACorruptFrameEndsTheConnection()
    {
        using var connection = new Connection(ConnectionOptions.FromCommand(TestPaths.FakeCommand("corrupt")));

        var error = await Assert.ThrowsAsync<RemoteError>(() => connection.InfoAsync());
        Assert.Equal("unknown", error.Code);
        Assert.Equal("Corrupt frame from pi-env", error.Message);
        Assert.True(error.Lost);
    }

    [Fact]
    public async Task AFrameWithInvalidJsonEndsTheConnection()
    {
        using var connection = new Connection(ConnectionOptions.FromCommand(TestPaths.FakeCommand("garbage-json")));

        var error = await Assert.ThrowsAsync<RemoteError>(() => connection.InfoAsync());
        Assert.Equal("Corrupt frame from pi-env", error.Message);
        Assert.True(error.Lost);
    }

    private static JsonObject ExecRequest(string cwd)
    {
        var argv = new JsonArray();
        foreach (var argument in TestCommands.LongRunning) argv.Add(argument);
        return new JsonObject
        {
            ["argv"] = argv,
            ["cwd"] = cwd,
            ["env"] = new JsonObject(),
            ["inheritEnv"] = true,
        };
    }

    private static string ExpectedOs() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsAndroid() ? "android"
        : "linux";
}
