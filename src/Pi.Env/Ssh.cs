using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pi.Env;

/// <summary>本包为其提供守护进程的远端系统。对应 TS <c>RemotePlatform</c>。</summary>
public sealed record RemotePlatform
{
    /// <summary><c>linux</c> / <c>android</c> / <c>darwin</c> / <c>windows</c>。</summary>
    public required string Platform { get; init; }

    /// <summary><c>x64</c> / <c>arm64</c>。</summary>
    public required string Arch { get; init; }

    /// <summary>远端 home 目录，按远端自己的写法。</summary>
    public required string Home { get; init; }

    /// <summary>Windows：<c>sshd</c> 运行命令所用的 shell（<c>DefaultShell</c>）。</summary>
    public string? Shell { get; init; }

    /// <summary>值得告知使用者的注意事项，例如缺少 <c>termux-exec</c> 的 Termux。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>如何用系统 <c>ssh</c> 到达一台机器。对应 TS <c>SshTarget</c>。</summary>
public record SshTarget
{
    /// <summary>主机名或 <c>~/.ssh/config</c> 里的别名。</summary>
    public required string Host { get; init; }

    public string? User { get; init; }

    public int? Port { get; init; }

    public string? IdentityFile { get; init; }

    /// <summary>本应用信任的主机密钥文件；<see cref="Ssh.AcceptHostKeyAsync"/> 往里添加。</summary>
    public required string KnownHostsFile { get; init; }

    /// <summary>密钥存放所用的名字，与别名、端口、跳板机无关，例如 <c>pi-env-&lt;env name&gt;</c>。</summary>
    public required string HostKeyAlias { get; init; }

    /// <summary><c>ssh</c> 程序；缺省 <c>ssh</c>。</summary>
    public string? Ssh { get; init; }

    /// <summary>替代 <c>~/.ssh/config</c> 的配置文件（<c>-F</c>）。</summary>
    public string? ConfigFile { get; init; }
}

/// <summary>远端主机密钥不在 <see cref="SshTarget.KnownHostsFile"/> 中；<c>ScanHostKey</c> 展示它以便使用者接受。</summary>
public sealed class HostKeyUnknownError : Exception
{
    public HostKeyUnknownError(string message) : base(message)
    {
    }
}

/// <summary>远端主机密钥与已信任的不同；永不自动接受。</summary>
public sealed class HostKeyChangedError : Exception
{
    public HostKeyChangedError(string message) : base(message)
    {
    }
}

/// <summary>因其他原因失败的 <c>ssh</c> 调用，带退出码与诊断输出。对应 TS <c>SshError</c>。</summary>
public sealed class SshError : Exception
{
    public SshError(string message, int? exitCode, string stderr) : base(message)
    {
        ExitCode = exitCode;
        Stderr = stderr;
    }

    public int? ExitCode { get; }

    public string Stderr { get; }
}

/// <summary><see cref="Ssh.ScanHostKeyAsync"/> 的结果：known-hosts 行与指纹。</summary>
public sealed record HostKeyScan(IReadOnlyList<string> Lines, IReadOnlyList<string> Fingerprints);

/// <summary><c>sshConnection</c> 与 <c>connectSsh</c> 的选项：目标，加上要部署的二进制（缺省为本包随附的那份）。</summary>
public sealed record SshConnectOptions : SshTarget
{
    /// <summary>要部署的守护进程可执行文件；缺省取 <see cref="Ssh.PackagedDaemon(RemotePlatform)"/>。</summary>
    public string? Binary { get; init; }

    /// <summary>
    /// POSIX：通过用户的登录 shell（<c>$SHELL -l</c>）启动守护进程，让命令看到 <c>~/.profile</c> 等文件的环境。
    /// 缺省关闭：<c>ssh</c> 运行命令时不带登录 shell。
    /// </summary>
    public bool LoginShell { get; init; }

    /// <summary>接收守护进程与传输层的诊断输出。</summary>
    public Action<string>? OnLog { get; init; }
}

/// <summary>本包为其提供守护进程的远端系统探测、主机密钥与部署。对应 TS <c>ssh.ts</c>。</summary>
public static class Ssh
{
    /// <summary>检测、校验或上传经 <c>ssh</c> 的时限。</summary>
    private const int SshTimeoutMs = 60_000;

    private const int UploadTimeoutMs = 300_000;

    /// <summary>POSIX 探测，由远端登录 shell 运行；Windows 主机改由 PowerShell 回答。</summary>
    private const string PosixProbe =
        "sh -c 'echo PI-ENV-PROBE; uname -s; uname -m; uname -o 2>/dev/null || echo -; " +
        "printf \"%s\\n\" \"$HOME\" \"${TMPDIR:--}\" \"${LD_PRELOAD:--}\"'";

    private static readonly string WindowsProbe = PowerShell(
        "'PI-ENV-PROBE'; 'Windows'; [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString(); " +
        "'-'; $HOME; '-'; '-'");

    private const string PosixHash =
        "hash() { if command -v sha256sum >/dev/null 2>&1; then sha256sum \"$1\" | cut -d' ' -f1; " +
        "elif command -v shasum >/dev/null 2>&1; then shasum -a 256 \"$1\" | cut -d' ' -f1; " +
        "elif command -v openssl >/dev/null 2>&1; then openssl dgst -sha256 \"$1\" | sed 's/.*= //'; " +
        "else echo none; fi; }";

    private static readonly Regex HostKeyType = new(
        "^(ssh-[a-z0-9-]+|ecdsa-sha2-[a-z0-9-]+|sk-[a-z0-9@.-]+)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex HostKeyKey = new("^[A-Za-z0-9+/]+={0,2}$", RegexOptions.CultureInvariant);

    /// <summary>正在被修改的 known-hosts 文件，使同一文件的修改逐个进行。</summary>
    private static readonly Dictionary<string, Task> KnownHostsChanges = new(StringComparer.Ordinal);

    private static readonly Lock KnownHostsGate = new();

    /// <summary>
    /// <c>ssh</c> 到主机之前的参数：不提示、不做任何转发、不共享连接、不执行配置里的命令、
    /// 不转发区域设置（远端用自己的），主机密钥严格按本应用自己的文件与固定别名校验。
    /// </summary>
    public static IReadOnlyList<string> SshArguments(
        SshTarget target,
        bool strictHostKeys = true,
        string? knownHostsFile = null)
    {
        CheckField("host", target.Host);
        CheckField("host key alias", target.HostKeyAlias);
        if (target.User is { } user) CheckField("user", user);
        if (target.Port is { } port && (port < 1 || port > 65535))
        {
            throw new InvalidOperationException($"Invalid port: {port}");
        }

        var arguments = new List<string>();
        if (target.ConfigFile is { } configFile)
        {
            arguments.Add("-F");
            arguments.Add(configFile);
        }

        arguments.AddRange(["-T", "-a", "-x"]);
        void Option(string setting)
        {
            arguments.Add("-o");
            arguments.Add(setting);
        }

        Option("BatchMode=yes");
        Option("ClearAllForwardings=yes");
        Option("ForwardAgent=no");
        Option("ForwardX11=no");
        Option("ControlMaster=no");
        Option("ControlPath=none");
        Option("RemoteCommand=none");
        Option("PermitLocalCommand=no");
        Option("SendEnv=-*");
        Option("ServerAliveInterval=15");
        Option($"StrictHostKeyChecking={(strictHostKeys ? "yes" : "accept-new")}");
        Option($"UserKnownHostsFile={ConfigPath("known hosts file", knownHostsFile ?? target.KnownHostsFile)}");
        Option("GlobalKnownHostsFile=none");
        Option("HashKnownHosts=no");
        Option($"HostKeyAlias={target.HostKeyAlias}");
        if (target.User is { } login)
        {
            arguments.Add("-l");
            arguments.Add(login);
        }

        if (target.Port is { } portNumber)
        {
            arguments.Add("-p");
            arguments.Add(portNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (target.IdentityFile is { } identityFile)
        {
            Option($"IdentityFile={ConfigPath("identity file", identityFile)}");
            Option("IdentitiesOnly=yes");
        }

        arguments.Add("--");
        arguments.Add(target.Host);
        return arguments;
    }

    /// <summary>
    /// 以 <c>accept-new</c> 对临时 known-hosts 文件连接一次，捕获主机出示的密钥——走的是与真实连接相同的路径
    /// （<c>~/.ssh/config</c>、跳板机）。返回其 known-hosts 行与指纹。展示指纹不等于认证：接受前须以带外方式比对。
    /// </summary>
    public static async Task<HostKeyScan> ScanHostKeyAsync(SshTarget target)
    {
        var directory = Path.Join(Path.GetTempPath(), "pi-env-hostkey-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
        Directory.CreateDirectory(directory);
        try
        {
            var scanned = Path.Join(directory, "known_hosts");
            // 认证可能因缺少密钥而失败；密钥在认证之前就已被记录。
            try
            {
                await RunSshAsync(target, "exit 0", args: SshArguments(target, strictHostKeys: false, scanned))
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is SshError or HostKeyUnknownError)
            {
            }

            var lines = File.Exists(scanned)
                ? File.ReadAllText(scanned).Split('\n').Where(line => line.Trim().Length != 0).ToArray()
                : [];
            if (lines.Length == 0) throw new InvalidOperationException($"No host key received from {target.Host}");
            var output = await RunProgramAsync("ssh-keygen", ["-lf", scanned]).ConfigureAwait(false);
            var fingerprints = output.Split('\n').Where(line => line.Trim().Length != 0).ToArray();
            return new HostKeyScan(lines, fingerprints);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响结果。
            }
        }
    }

    /// <summary>
    /// 把 <see cref="ScanHostKeyAsync"/> 得到的行加入目标的 known-hosts 文件。只接受针对目标别名的普通行。
    /// 与同类型已信任密钥不同的密钥以 <see cref="HostKeyChangedError"/> 拒绝；须先用 <see cref="ForgetHostKeyAsync"/> 移除旧密钥。
    /// </summary>
    public static async Task AcceptHostKeyAsync(SshTarget target, IReadOnlyList<string> lines)
    {
        var alias = target.HostKeyAlias;
        var accepted = lines
            .Select(line =>
            {
                var (type, key) = ParseHostKeyLine(alias, line);
                return (Line: line.Trim(), Type: type, Key: key);
            })
            .ToArray();
        await ChangeKnownHostsAsync(target.KnownHostsFile, existing =>
        {
            var trusted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in existing)
            {
                var parts = SplitFields(line);
                if (parts.Length >= 3 && parts[0] == alias) trusted[parts[1]] = parts[2];
            }

            var added = new List<string>();
            foreach (var (line, type, key) in accepted)
            {
                if (trusted.TryGetValue(type, out var known))
                {
                    if (known == key) continue;
                    throw new HostKeyChangedError($"The {type} host key of {target.Host} changed; forget the old key first");
                }

                trusted[type] = key;
                added.Add(line);
            }

            return [..existing, ..added];
        }).ConfigureAwait(false);
    }

    /// <summary>停止信任该别名下存的所有密钥，例如使用者确认主机密钥变更之后。</summary>
    public static Task ForgetHostKeyAsync(SshTarget target) =>
        ChangeKnownHostsAsync(
            target.KnownHostsFile,
            existing => existing.Where(line => SplitFields(line).FirstOrDefault() != target.HostKeyAlias).ToList());

    /// <summary>目标运行哪种系统：经登录 shell 的 <c>uname</c>，或 Windows 上的 PowerShell。</summary>
    public static async Task<RemotePlatform> DetectPlatformAsync(SshTarget target)
    {
        Probe probe;
        try
        {
            probe = ParseProbe(target, await RunSshAsync(target, PosixProbe).ConfigureAwait(false));
        }
        catch (Exception error) when (error is not HostKeyUnknownError and not HostKeyChangedError)
        {
            // 远端 shell 是 cmd.exe 或 PowerShell：没有 sh，或 sh 把探测里的引号解错了。
            probe = ParseProbe(target, await RunSshAsync(target, WindowsProbe).ConfigureAwait(false));
        }

        // Git Bash 作为 Windows 的默认 SSH shell：向 PowerShell 询问 Windows 自己的架构与 home 写法。
        if (Regex.IsMatch(probe.System, "^(MINGW|MSYS|CYGWIN)", RegexOptions.CultureInvariant))
        {
            probe = ParseProbe(target, await RunSshAsync(target, WindowsProbe).ConfigureAwait(false));
        }

        var arch = NormalizeArch(probe.Machine);
        var (system, os, home) = (probe.System, probe.Os, probe.Home);
        if (system == "Windows")
        {
            // cmd.exe 会展开 %OS%；PowerShell 原样打印。
            var shell = (await RunSshAsync(target, "echo %OS%").ConfigureAwait(false)).Contains("Windows_NT", StringComparison.Ordinal)
                ? "cmd"
                : "powershell";
            return new RemotePlatform { Platform = "windows", Arch = arch, Home = home, Shell = shell };
        }

        if (system == "Darwin") return new RemotePlatform { Platform = "darwin", Arch = arch, Home = home };
        if (system == "Linux")
        {
            var android = os == "Android";
            return new RemotePlatform
            {
                Platform = android ? "android" : "linux",
                Arch = arch,
                Home = home,
                Warnings = android ? TermuxWarnings(probe) : [],
            };
        }

        throw new InvalidOperationException($"Unsupported remote system: {system}");
    }

    /// <summary>本包为某远端系统随附的守护进程路径。</summary>
    public static string PackagedDaemon(RemotePlatform remote) => PackagedDaemon(remote.Platform, remote.Arch);

    /// <summary>
    /// 随发布物布置的守护进程路径：<c>&lt;app&gt;/bin/pi-env-&lt;platform&gt;-&lt;arch&gt;/pi-env[.exe]</c>。
    /// 上游 npm 包用包根下的 <c>bin/</c>；.NET 没有包根概念，输出目录即发布根（差异 E-C2）。
    /// </summary>
    public static string PackagedDaemon(string platform, string arch) =>
        Path.Join(
            AppContext.BaseDirectory,
            "bin",
            $"pi-env-{platform}-{arch}",
            platform == "windows" ? "pi-env.exe" : "pi-env");

    /// <summary>
    /// 确保守护进程已在该远端机器上，运行前先按其 SHA-256 校验，并清掉其它内容的守护进程。上传先写临时文件，
    /// 只有哈希相符才改名就位。返回二进制在远端的路径。
    /// </summary>
    public static async Task<string> DeployDaemonAsync(SshTarget target, RemotePlatform remote, string? binary = null)
    {
        binary ??= PackagedDaemon(remote);
        if (!File.Exists(binary))
        {
            throw new InvalidOperationException($"No pi-env daemon for {remote.Platform}-{remote.Arch} at {binary}");
        }

        var bytes = await File.ReadAllBytesAsync(binary).ConfigureAwait(false);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var file = DaemonPath(remote, sha256);
        var state = await CheckDaemonAsync(target, remote, file, sha256).ConfigureAwait(false);
        if (state == "present") return file;
        if (state == "nohash")
        {
            throw new InvalidOperationException($"{target.Host} has no sha256sum, shasum or openssl to verify pi-env");
        }

        if (remote.Platform == "windows")
        {
            var upload = PowerShell(string.Join("; ", new[]
            {
                "$ErrorActionPreference = 'Stop'",
                $"$f = {QuotePowerShell(file)}",
                "$d = Split-Path -Parent $f",
                "New-Item -ItemType Directory -Force -Path $d | Out-Null",
                "$t = Join-Path $d ('.pi-env-' + [guid]::NewGuid().ToString() + '.tmp')",
                // PowerShell 自己读取重定向的 stdin，按文本行交给 $input，所以二进制以 base64 行传到一个结束标记：
                // Windows 的 sshd 可能永不传递 stdin 的结束。
                "$text = New-Object System.Text.StringBuilder",
                "foreach ($line in $input) { if ($line -eq 'PI-ENV-END') { break }; [void]$text.Append($line) }",
                "$bytes = [Convert]::FromBase64String($text.ToString())",
                "$out = [IO.File]::Open($t, 'CreateNew', 'Write', 'None'); $out.Write($bytes, 0, $bytes.Length); $out.Close()",
                $"if ((Get-FileHash -Algorithm SHA256 -LiteralPath $t).Hash.ToLower() -ne '{sha256}') {{ Remove-Item -LiteralPath $t; throw 'pi-env upload is corrupt' }}",
                // 运行中的守护进程或病毒扫描器可能短暂占住旧文件。
                "for ($i = 0; ; $i++) { try { Move-Item -Force -LiteralPath $t -Destination $f; break } catch { if ($i -ge 20) { throw }; Start-Sleep -Milliseconds 250 } }",
                // 更旧的守护进程；正在运行的那个会留到退出。
                "Get-ChildItem -LiteralPath $d -Filter 'pi-env-*.exe' | Where-Object { $_.FullName -ne $f } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -ErrorAction SilentlyContinue }",
                "'deployed'",
            }));
            var base64 = Convert.ToBase64String(bytes);
            var lines = string.Join("\n", Chunk(base64, 76)) + "\n";
            await RunSshAsync(
                target,
                upload,
                Encoding.UTF8.GetBytes(lines + "PI-ENV-END\n"),
                timeoutMs: UploadTimeoutMs).ConfigureAwait(false);
            return file;
        }

        var script = string.Join("\n", new[]
        {
            "set -e",
            PosixHash,
            $"f={QuotePosix(file)}",
            "d=$(dirname \"$f\")",
            "mkdir -p \"$d\"",
            "chmod 700 \"$d\"",
            "t=$(mktemp \"$d/.pi-env.XXXXXX\")",
            "cat > \"$t\"",
            $"if [ \"$(hash \"$t\")\" != {sha256} ]; then rm -f \"$t\"; echo \"pi-env upload is corrupt\" >&2; exit 1; fi",
            "chmod 700 \"$t\"",
            "mv -f \"$t\" \"$f\"",
            // 更旧的守护进程；正在运行的那些会一直持有自己的文件直到退出。
            "for old in \"$d\"/pi-env-*; do [ \"$old\" = \"$f\" ] || rm -f \"$old\"; done",
            "echo deployed",
        });
        await RunSshAsync(
            target,
            $"sh -c {QuotePosix(script)}",
            bytes,
            timeoutMs: UploadTimeoutMs).ConfigureAwait(false);
        return file;
    }

    /// <summary>
    /// 一个到目标的 <see cref="Connection"/>，首个请求之前什么都不做。每次启动检测远端系统（首次）、校验守护进程，
    /// 缺失或变化则部署，然后经 <c>ssh</c> 启动。失败（无网络、主机密钥未信任或已变更）让等待该次启动的请求以
    /// <c>spawn_error</c> 失败，消息为 <c>ssh</c> 诊断；下一次请求重试。<c>Remote()</c> 给出已知的远端系统。
    /// </summary>
    public static (Connection Connection, Func<RemotePlatform?> Remote) SshConnection(SshConnectOptions options)
    {
        var state = new SshState();
        return (SshConnectionFor(options, state), () => state.Remote);
    }

    /// <summary>
    /// 现在检测远端系统并部署守护进程，然后返回一个经 <c>ssh</c> 启动它的 <see cref="Connection"/>，与
    /// <see cref="SshConnection"/> 相同。失败在此处拒绝：主机密钥必须已信任（<see cref="ScanHostKeyAsync"/>、
    /// <see cref="AcceptHostKeyAsync"/>），否则以 <see cref="HostKeyUnknownError"/> 拒绝。
    /// </summary>
    public static async Task<(Connection Connection, RemotePlatform Remote)> ConnectSshAsync(SshConnectOptions options)
    {
        var remote = await DetectPlatformAsync(options).ConfigureAwait(false);
        var verified = await DeployDaemonAsync(options, remote, options.Binary).ConfigureAwait(false);
        foreach (var warning in remote.Warnings) options.OnLog?.Invoke(warning + "\n");
        return (SshConnectionFor(options, new SshState { Remote = remote, Verified = verified }), remote);
    }

    private static Connection SshConnectionFor(SshConnectOptions options, SshState state) =>
        new(new ConnectionOptions
        {
            Command = async () =>
            {
                if (state.Remote is null)
                {
                    var remote = await DetectPlatformAsync(options).ConfigureAwait(false);
                    foreach (var warning in remote.Warnings) options.OnLog?.Invoke(warning + "\n");
                    state.Remote = remote;
                }

                // 刚校验过的部署结果可供首次启动使用。
                var file = state.Verified
                           ?? await DeployDaemonAsync(options, state.Remote, options.Binary).ConfigureAwait(false);
                state.Verified = null;
                var command = new List<string> { options.Ssh ?? "ssh" };
                command.AddRange(SshArguments(options));
                command.Add(LaunchCommand(state.Remote, file, options.LoginShell));
                return command;
            },
            OnLog = options.OnLog,
        });

    /// <summary>以 <c>ssh</c> 运行一条远端命令，可选择喂入 stdin；成功返回 stdout，失败抛异常。</summary>
    private static async Task<string> RunSshAsync(
        SshTarget target,
        string command,
        byte[]? stdin = null,
        IReadOnlyList<string>? args = null,
        int? timeoutMs = null)
    {
        var arguments = new List<string>(args ?? SshArguments(target)) { command };
        var (exitCode, output, diagnostics, timedOut) = await RunProcessAsync(
            target.Ssh ?? "ssh",
            arguments,
            stdin,
            timeoutMs ?? SshTimeoutMs).ConfigureAwait(false);

        if (timedOut)
        {
            var limit = timeoutMs ?? SshTimeoutMs;
            throw new SshError(
                $"ssh {target.Host} did not finish within {limit / 1000} s: {diagnostics.Trim()}",
                null,
                diagnostics);
        }

        if (exitCode == 0) return output;
        if (diagnostics.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal))
        {
            throw new HostKeyChangedError($"The host key of {target.Host} changed; remove the old key to continue");
        }

        if (Regex.IsMatch(diagnostics, "Host key verification failed|No .* host key is known"))
        {
            throw new HostKeyUnknownError($"The host key of {target.Host} is not trusted yet");
        }

        throw new SshError($"ssh {target.Host} failed with exit code {exitCode}: {diagnostics.Trim()}", exitCode, diagnostics);
    }

    /// <summary>运行一个本地程序并返回 stdout；只在其无法启动时抛错（与 <c>ssh-keygen -lf</c> 的用法一致）。</summary>
    private static async Task<string> RunProgramAsync(string program, IReadOnlyList<string> arguments)
    {
        var (_, output, _, _) = await RunProcessAsync(program, arguments, null, SshTimeoutMs).ConfigureAwait(false);
        return output;
    }

    private static async Task<(int ExitCode, string Output, string Diagnostics, bool TimedOut)> RunProcessAsync(
        string program,
        IReadOnlyList<string> arguments,
        byte[]? stdin,
        int timeoutMs)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{program} did not start");
        }
        catch (Exception error)
        {
            throw new SshError(error.Message, null, "");
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var diagnostics = process.StandardError.ReadToEndAsync();
            try
            {
                if (stdin is { Length: > 0 })
                {
                    await process.StandardInput.BaseStream.WriteAsync(stdin).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
                }

                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // 命令可能不读 stdin 就退出。
            }

            using var timeout = new CancellationTokenSource(timeoutMs);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // 已退出。
                }

                var stalledDiagnostics = await SafeResult(diagnostics).ConfigureAwait(false);
                return (0, "", stalledDiagnostics, true);
            }

            return (process.ExitCode, await SafeResult(output).ConfigureAwait(false),
                await SafeResult(diagnostics).ConfigureAwait(false), false);
        }
    }

    private static async Task<string> SafeResult(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>一个 known-hosts 行，格式为 <c>alias type key [comment]</c>，不含标记、模式或哈希名。</summary>
    private static (string Type, string Key) ParseHostKeyLine(string alias, string line)
    {
        var fields = SplitFields(line);
        if (fields.Length < 3
            || fields[0] != alias
            || !HostKeyType.IsMatch(fields[1])
            || !HostKeyKey.IsMatch(fields[2]))
        {
            throw new InvalidOperationException($"Not a host key line for {alias}: {Quote(line)}");
        }

        return (fields[1], fields[2]);
    }

    private static string[] SplitFields(string line) =>
        line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>一次改一个改动，原子地重写 known-hosts 文件。</summary>
    private static async Task ChangeKnownHostsAsync(string file, Func<List<string>, List<string>> change)
    {
        Task next;
        lock (KnownHostsGate)
        {
            var previous = KnownHostsChanges.TryGetValue(file, out var running) ? running : Task.CompletedTask;
            next = Apply(previous);
            KnownHostsChanges[file] = next;
        }

        try
        {
            await next.ConfigureAwait(false);
        }
        finally
        {
            lock (KnownHostsGate)
            {
                if (KnownHostsChanges.TryGetValue(file, out var current) && ReferenceEquals(current, next))
                {
                    KnownHostsChanges.Remove(file);
                }
            }
        }

        async Task Apply(Task previous)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 前一个改动失败不应阻塞后一个。
            }

            var directory = Path.GetDirectoryName(file);
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var existing = File.Exists(file)
                ? File.ReadAllText(file).Split('\n').Where(line => line.Length != 0).ToList()
                : [];
            var lines = change(existing);
            var temporary = $"{file}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.tmp";
            await File.WriteAllTextAsync(temporary, lines.Count == 0 ? "" : string.Join("\n", lines) + "\n", new UTF8Encoding(false))
                .ConfigureAwait(false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try
            {
                File.Move(temporary, file, overwrite: true);
            }
            catch (Exception)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }

                throw;
            }
        }
    }

    private static string PowerShell(string script) =>
        "powershell -NoProfile -NonInteractive -EncodedCommand " +
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private static string NormalizeArch(string machine)
    {
        var lower = machine.ToLowerInvariant();
        return lower switch
        {
            "x86_64" or "amd64" or "x64" => "x64",
            "aarch64" or "arm64" => "arm64",
            _ => throw new InvalidOperationException($"Unsupported remote architecture: {machine}"),
        };
    }

    private sealed record Probe(string System, string Machine, string Os, string Home, string TmpDir, string Preload);

    private static Probe ParseProbe(SshTarget target, string output)
    {
        // 登录 shell 可能先打印横幅。
        var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var start = Array.IndexOf(lines, "PI-ENV-PROBE");
        if (start == -1) throw new InvalidOperationException($"Unexpected answer from {target.Host}: {output.Trim()}");
        string At(int offset) => start + offset < lines.Length ? lines[start + offset] : "";
        return new Probe(At(1), At(2), At(3), At(4), At(5) is { Length: > 0 } tmp ? tmp : "-", At(6) is { Length: > 0 } preload ? preload : "-");
    }

    /// <summary>Termux 只在自己的 <c>TMPDIR</c> 与 <c>termux-exec</c> 下工作，后者让 <c>#!/usr/bin/env</c> shebang 生效。</summary>
    private static List<string> TermuxWarnings(Probe probe)
    {
        var warnings = new List<string>();
        if (probe.TmpDir == "-") warnings.Add("TMPDIR is not set; Termux's sshd normally sets it to $PREFIX/tmp.");
        if (!probe.Preload.Contains("termux-exec", StringComparison.Ordinal))
        {
            warnings.Add("termux-exec is not loaded (LD_PRELOAD); scripts with #!/usr/bin/env shebangs will fail.");
        }

        warnings.Add("Android may suspend Termux; run termux-wake-lock on the device to keep the connection alive.");
        return warnings;
    }

    /// <summary>某内容的守护进程在远端的位置：以 SHA-256 命名，版本之间永不冲突。</summary>
    private static string DaemonPath(RemotePlatform remote, string sha256)
    {
        var name = $"pi-env-{sha256[..32]}";
        return remote.Platform == "windows"
            ? $"{remote.Home}\\.pi\\mobile\\tools\\{name}.exe"
            : $"{remote.Home}/.pi/mobile/tools/{name}";
    }

    /// <summary>远端文件是否已是该内容：<c>present</c>、<c>missing</c>，或（POSIX 无哈希工具时）<c>nohash</c>。</summary>
    private static async Task<string> CheckDaemonAsync(SshTarget target, RemotePlatform remote, string file, string sha256)
    {
        if (remote.Platform == "windows")
        {
            var check = PowerShell(
                $"$f = {QuotePowerShell(file)}; if ((Test-Path -LiteralPath $f) -and " +
                $"((Get-FileHash -Algorithm SHA256 -LiteralPath $f).Hash.ToLower() -eq '{sha256}')) {{ 'present' }} else {{ 'missing' }}");
            var answer = (await RunSshAsync(target, check).ConfigureAwait(false)).Trim();
            return answer.EndsWith("present", StringComparison.Ordinal) ? "present" : "missing";
        }

        var script =
            $"{PosixHash}; f={QuotePosix(file)}; if [ -f \"$f\" ] && [ \"$(hash \"$f\")\" = {sha256} ]; then echo present; " +
            "elif [ \"$(hash /dev/null)\" = none ]; then echo nohash; else echo missing; fi";
        var lines = (await RunSshAsync(target, $"sh -c {QuotePosix(script)}").ConfigureAwait(false))
            .Trim()
            .Split('\n');
        return lines.Length == 0 ? "missing" : lines[^1];
    }

    /// <summary>启动 <paramref name="file"/> 处守护进程的远端命令，位于 <c>serve</c> 参数之前。</summary>
    private static string LaunchCommand(RemotePlatform remote, string file, bool loginShell)
    {
        if (remote.Platform == "windows")
        {
            // PowerShell 只有用调用运算符才能运行带引号的路径；cmd.exe 对程序保留一对引号。
            if (remote.Shell == "powershell") return $"& {QuotePowerShell(file)}";
            return file.Contains(' ', StringComparison.Ordinal) ? $"\"{file}\"" : file;
        }

        // `$0` 是守护进程，`"$@"` 是它的参数；`$SHELL` 是登录 shell 对自己的称呼。
        return loginShell
            ? $"exec \"$SHELL\" -lc 'exec \"$0\" \"$@\"' {QuotePosix(file)}"
            : QuotePosix(file);
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var index = 0; index < text.Length; index += size)
        {
            yield return text.Substring(index, Math.Min(size, text.Length - index));
        }
    }

    private static string QuotePosix(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string QuotePowerShell(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>没有前导 <c>-</c>（会被当作 <c>ssh</c> 选项），也没有空白或控制字符。</summary>
    private static void CheckField(string name, string value)
    {
        if (value.Length == 0
            || value.StartsWith('-')
            || value.Any(c => char.IsWhiteSpace(c) || c < 0x20 || c == 0x7f))
        {
            throw new InvalidOperationException($"Invalid {name}: {Quote(value)}");
        }
    }

    /// <summary>给 <c>ssh</c> 选项用的路径，会展开 <c>%</c> 记号：加引号，<c>%</c> 字面化。</summary>
    private static string ConfigPath(string name, string path)
    {
        if (path.Length == 0 || path.Any(c => c == '"' || c < 0x20 || c == 0x7f))
        {
            throw new InvalidOperationException($"Invalid {name}: {Quote(path)}");
        }

        return "\"" + path.Replace("%", "%%", StringComparison.Ordinal) + "\"";
    }

    /// <summary>错误消息里的字符串字面量；与 <c>JSON.stringify</c> 的可见形态一致（差异 E-C4）。</summary>
    private static string Quote(string value) => JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>SSH 连接学到的远端系统，以及它上次校验过的守护进程。</summary>
    private sealed class SshState
    {
        public RemotePlatform? Remote { get; set; }

        public string? Verified { get; set; }
    }
}
