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
    /// <summary>
    /// 一条会跑几秒的命令，用于取消与超时用例。对齐 TS <c>remote.test.ts</c> 的 <c>["sleep", "5"]</c>：
    /// 单个进程、约五秒。这里同样取「裸进程」而非 <c>cmd /c</c> 包裹——一来形状与 TS 一致，
    /// 二来一旦某次中止没生效，用例仍在五秒内有界，不会拖成三十秒。
    /// </summary>
    public static IReadOnlyList<string> LongRunning =>
        OperatingSystem.IsWindows()
            ? ["ping", "-n", "6", "127.0.0.1"]
            : ["sleep", "5"];

    /// <summary>
    /// 同上，但完全静默——<see cref="LongRunning"/> 在 Windows 上是 <c>ping</c>，会持续打印；
    /// 而「无输出超时」要的正是守护进程停在收包等待、只能被截止时间唤醒的路径。
    /// </summary>
    public static IReadOnlyList<string> SilentLongRunning =>
        OperatingSystem.IsWindows()
            ? ["cmd", "/c", "ping -n 6 127.0.0.1 >nul"]
            : ["sleep", "5"];

    /// <summary>
    /// 终止一次进程树的时间预算（毫秒）。守护进程用 <c>taskkill /F /T</c> 在 Windows 上终止，
    /// 用 <c>kill(-pid)</c> 在 POSIX 上终止：后者是一次系统调用，前者要另起一个进程。
    /// 用裸帧协议脚本对同一个守护进程实测（本机，13 次）：Windows 每次 <b>1.1–1.8 秒</b>，
    /// 且与进程树形状无关（<c>cmd /c ping</c> 与裸 <c>ping</c> 相同）。
    /// 上游测试里「中止/超时应在若干毫秒内落定」的预算按 POSIX 标定，故 Windows 上按实测放宽。
    /// </summary>
    public static long KillBudget(int kills) => OperatingSystem.IsWindows() ? 2_000L * kills : 0L;

    /// <summary>杀掉一个进程；用于模拟连接丢失。</summary>
    public static void Kill(long pid)
    {
        using var process = Process.GetProcessById((int)pid);
        process.Kill();
        process.WaitForExit(5000);
    }

    /// <summary>
    /// 充当 <c>sh</c> 的 Git Bash。上游 conformance 测试直接写死 <c>$ProgramFiles/Git/bin/bash.exe</c>，
    /// 这里额外接受 <c>PI_ENV_BASH</c> 覆盖——Git 装到非标准位置（例如随宿主程序附带的 PortableGit）时，
    /// 不必把测试改成静默跳过。POSIX 上返回 null（用系统 <c>sh</c>）。
    /// </summary>
    public static string? FindBash()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (Environment.GetEnvironmentVariable("PI_ENV_BASH") is { Length: > 0 } configured)
        {
            return File.Exists(configured) ? configured : null;
        }

        foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)" })
        {
            if (Environment.GetEnvironmentVariable(variable) is not { } root) continue;
            var candidate = System.IO.Path.Join(root, "Git", "bin", "bash.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public static bool HasPosixShell => !OperatingSystem.IsWindows() || FindBash() is not null;

    /// <summary>POSIX shell 的「程序 + -c」，与 conformance 套件的 <c>Shell</c> 选项对应。</summary>
    public static IReadOnlyList<string> PosixShell =>
        OperatingSystem.IsWindows() ? [FindBash()!, "-c"] : ["sh", "-c"];
}
