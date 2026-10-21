using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Pi.Durable.Env;
using Xunit;
using Context = Pi.Chord.Context.Context;

namespace Pi.Env.Tests;

/// <summary>
/// 真实 <c>sshd</c> 上的端到端用例，对应 TS <c>packages/env/test/ssh.test.ts</c> 的
/// <c>describe.skipIf(sshd === undefined)</c> 整块：主机密钥未信任时拒绝连接、扫描并接受后部署并运行守护进程，
/// 远端执行环境经 SSH 正常读写。
/// </summary>
/// <remarks>
/// 上游把 <c>sshd</c> 的发现写成 <c>process.platform === "win32" ? undefined : …</c>，因此这一块在 Windows 上从不执行；
/// 这里用同样的条件跳过，不另造一条 Windows 路径。
/// </remarks>
public class SshdTests
{
    private const string Alias = "pi-env-test";

    [Fact]
    public async Task RefusesAnUntrustedHostThenDeploysAndRunsTheDaemonOnceItsKeyIsAccepted()
    {
        if (Sshd.Locate() is not { } sshd)
        {
            Assert.Skip("no sshd on this platform");
            return;
        }

        using var root = new TempDir();
        var home = Path.Join(root.Path, "home");
        Directory.CreateDirectory(home);
        var hostKey = Path.Join(root.Path, "host_key");
        var clientKey = Path.Join(root.Path, "client_key");
        await Sshd.KeygenAsync(hostKey);
        await Sshd.KeygenAsync(clientKey);
        await File.WriteAllBytesAsync(
            Path.Join(root.Path, "authorized_keys"),
            await File.ReadAllBytesAsync(clientKey + ".pub"));

        var port = Sshd.FreePort();
        await File.WriteAllTextAsync(
            Path.Join(root.Path, "sshd_config"),
            string.Join("\n", new[]
            {
                $"Port {port}",
                "ListenAddress 127.0.0.1",
                $"HostKey {hostKey}",
                $"AuthorizedKeysFile {Path.Join(root.Path, "authorized_keys")}",
                $"PidFile {Path.Join(root.Path, "sshd.pid")}",
                "PasswordAuthentication no",
                "KbdInteractiveAuthentication no",
                "StrictModes no",
                $"SetEnv HOME={home}",
                "",
            }));
        await File.WriteAllTextAsync(Path.Join(root.Path, "ssh_config"), "");

        using var server = Process.Start(new ProcessStartInfo(sshd)
        {
            ArgumentList = { "-D", "-e", "-f", Path.Join(root.Path, "sshd_config") },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        try
        {
            await Sshd.WaitForPortAsync(port);
            var target = new SshTarget
            {
                Host = "127.0.0.1",
                Port = port,
                User = Environment.UserName,
                IdentityFile = clientKey,
                KnownHostsFile = Path.Join(root.Path, "known_hosts"),
                HostKeyAlias = Alias,
                ConfigFile = Path.Join(root.Path, "ssh_config"),
            };
            var options = new SshConnectOptions
            {
                Host = target.Host,
                Port = target.Port,
                User = target.User,
                IdentityFile = target.IdentityFile,
                KnownHostsFile = target.KnownHostsFile,
                HostKeyAlias = target.HostKeyAlias,
                ConfigFile = target.ConfigFile,
                Binary = TestPaths.Daemon,
            };

            await Assert.ThrowsAsync<HostKeyUnknownError>(() => Ssh.ConnectSshAsync(options));

            var scanned = await Ssh.ScanHostKeyAsync(target);
            var expected = (await Sshd.RunAsync("ssh-keygen", ["-lf", hostKey + ".pub"])).Split(' ')[1];
            // 与 TS 一致：fingerprints 是 ssh-keygen -lf 的整行，包含 bits、指纹与算法。
            Assert.Contains(expected, string.Join("\n", scanned.Fingerprints));
            await Ssh.AcceptHostKeyAsync(target, scanned.Lines);

            var tools = Path.Join(home, ".pi", "mobile", "tools");
            Directory.CreateDirectory(tools);
            await File.WriteAllTextAsync(Path.Join(tools, "pi-env-0123456789abcdef0123456789abcdef"), "old");

            var (connection, remote) = await Ssh.ConnectSshAsync(options);
            try
            {
                Assert.Equal(home, remote.Home);
                var name = "pi-env-" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(TestPaths.Daemon)))
                    .ToLowerInvariant()[..32];
                var deployed = Path.Join(tools, name);
                Assert.Equal(await File.ReadAllBytesAsync(TestPaths.Daemon), await File.ReadAllBytesAsync(deployed));
                // 内容不同的守护进程被清掉。
                Assert.Equal(
                    new[] { name },
                    Directory.GetFileSystemEntries(tools)
                        .Select(Path.GetFileName)
                        .Where(entry => entry!.StartsWith("pi-env-", StringComparison.Ordinal)));

                var env = new RemoteExecutionEnv(new RemoteExecutionEnvOptions
                {
                    Connection = connection,
                    Id = "pi-env:test",
                    Cwd = home,
                });
                Assert.True((await env.WriteFileAsync("over-ssh.txt", "hello", Context.Background)).IsOk);
                Assert.Equal("hello", (await env.ReadTextFileAsync("over-ssh.txt", Context.Background)).Value);

                var output = new StringBuilder();
                var result = await env.ExecAsync(
                    ShellCommand.FromArgv(["sh", "-c", "printf \"%s\" \"$HOME\""]),
                    new ShellExecOptions { OnOutput = (text, _, _) => output.Append(text) },
                    Context.Background);
                Assert.True(result.IsOk, result.IsOk ? null : result.Error.Message);
                Assert.Equal(0L, result.Value.ExitCode);
                Assert.Equal(home, output.ToString());
            }
            finally
            {
                connection.Close();
            }
        }
        finally
        {
            try
            {
                if (!server.HasExited) server.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    /// <summary>本机上 <c>sshd</c> 的定位与配套工具。</summary>
    private static class Sshd
    {
        /// <summary>与 TS 一致：只认 <c>PATH</c> 里的 <c>sshd</c> 与 <c>/usr/sbin/sshd</c>，Windows 上恒为无。</summary>
        public static string? Locate()
        {
            if (OperatingSystem.IsWindows()) return null;
            try
            {
                var found = RunAsync("sh", ["-c", "command -v sshd"]).GetAwaiter().GetResult().Trim();
                if (found.Length != 0) return found;
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            return File.Exists("/usr/sbin/sshd") ? "/usr/sbin/sshd" : null;
        }

        public static Task KeygenAsync(string path) =>
            RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", path]);

        public static async Task<string> RunAsync(string program, IReadOnlyList<string> arguments)
        {
            var info = new ProcessStartInfo(program)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException($"Cannot start {program}");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{program} failed ({process.ExitCode}): {stderr}");
            }

            return stdout;
        }

        public static int FreePort()
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public static async Task WaitForPortAsync(int port)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
                    return;
                }
                catch (SocketException)
                {
                }

                await Task.Delay(50);
            }

            throw new InvalidOperationException("sshd did not start");
        }
    }
}
