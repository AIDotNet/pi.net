using System.Diagnostics;

namespace Pi.Env.Tests;

/// <summary>测试用外部程序的定位：参考守护进程（上游 Rust 源码构建）与假守护进程。</summary>
internal static class TestPaths
{
    private static readonly Lazy<string> Root = new(FindRoot);

    public static string RepositoryRoot => Root.Value;

    /// <summary>
    /// 参考守护进程：<c>PI_ENV_DAEMON</c> 优先（对应 TS 测试的同名约定），否则取本仓
    /// <c>tools/pi-env-daemon-target/debug/pi-env</c>（由 <c>cargo build</c> 产出）。
    /// </summary>
    public static string Daemon =>
        Environment.GetEnvironmentVariable("PI_ENV_DAEMON")
        ?? Path.Join(RepositoryRoot, "tools", "pi-env-daemon-target", "debug", Exe("pi-env"));

    public static string FakeDaemon =>
        Path.Join(RepositoryRoot, "tests", "Pi.Env.FakeDaemon", "bin", "Debug", "net10.0", Exe("Pi.Env.FakeDaemon"));

    public static bool DaemonAvailable => File.Exists(Daemon);

    public static bool FakeDaemonAvailable => File.Exists(FakeDaemon);

    /// <summary>假守护进程的启动命令；<c>serve --token</c> 由 <see cref="Connection"/> 追加。</summary>
    public static IReadOnlyList<string> FakeCommand(string script) => [FakeDaemon, "--script", script];

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "Pi.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Pi.slnx not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>可清理的临时目录（Windows 上被占用的句柄需要重试）。</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "pi-env-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Join(Path, name);

    public void Dispose()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

internal static class TestCommands
{
    /// <summary>一条会跑很久的命令，用于取消与超时用例。</summary>
    public static IReadOnlyList<string> LongRunning =>
        OperatingSystem.IsWindows()
            ? ["cmd", "/c", "ping", "-n", "30", "127.0.0.1"]
            : ["sleep", "30"];

    /// <summary>杀掉一个进程；用于模拟连接丢失。</summary>
    public static void Kill(long pid)
    {
        using var process = Process.GetProcessById((int)pid);
        process.Kill();
        process.WaitForExit(5000);
    }
}
