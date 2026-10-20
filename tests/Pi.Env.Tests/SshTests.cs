using System.Diagnostics;
using System.Text;
using Xunit;

namespace Pi.Env.Tests;

/// <summary>
/// SSH 层。对应 TS <c>packages/env/test/ssh.test.ts</c>：参数的安全开关、known-hosts 的接受/遗忘规则、
/// 随附二进制命名与部署前置校验。需要真实 <c>sshd</c> 的端到端用例在无 sshd 的平台上跳过（TS 同此策略）。
/// </summary>
public class SshTests
{
    private const string Alias = "pi-env-test";

    private static SshTarget Target(TempDir directory, string? knownHosts = null) => new()
    {
        Host = "gpu-box",
        KnownHostsFile = knownHosts ?? directory.File("known_hosts"),
        HostKeyAlias = Alias,
    };

    [Fact]
    public void Arguments_CarryEverySafetySwitch()
    {
        using var directory = new TempDir();
        var knownHosts = directory.File("known_hosts");

        var arguments = Ssh.SshArguments(new SshTarget
        {
            Host = "gpu-box",
            KnownHostsFile = knownHosts,
            HostKeyAlias = Alias,
        });

        Assert.Equal(
            new[]
            {
                "-T", "-a", "-x",
                "-o", "BatchMode=yes",
                "-o", "ClearAllForwardings=yes",
                "-o", "ForwardAgent=no",
                "-o", "ForwardX11=no",
                "-o", "ControlMaster=no",
                "-o", "ControlPath=none",
                "-o", "RemoteCommand=none",
                "-o", "PermitLocalCommand=no",
                "-o", "SendEnv=-*",
                "-o", "ServerAliveInterval=15",
                "-o", "StrictHostKeyChecking=yes",
                "-o", $"UserKnownHostsFile=\"{knownHosts}\"",
                "-o", "GlobalKnownHostsFile=none",
                "-o", "HashKnownHosts=no",
                "-o", $"HostKeyAlias={Alias}",
                "--", "gpu-box",
            },
            arguments);
    }

    [Fact]
    public void Arguments_PlaceTheOptionalFieldsWhereSshExpectsThem()
    {
        using var directory = new TempDir();
        var knownHosts = directory.File("known_hosts");
        var identity = directory.File("id_ed25519");
        var config = directory.File("ssh_config");

        var arguments = Ssh.SshArguments(
            new SshTarget
            {
                Host = "gpu-box",
                User = "me",
                Port = 2222,
                IdentityFile = identity,
                KnownHostsFile = knownHosts,
                HostKeyAlias = Alias,
                ConfigFile = config,
            },
            strictHostKeys: false);

        Assert.Equal("-F", arguments[0]);
        Assert.Equal(config, arguments[1]);
        Assert.Contains("StrictHostKeyChecking=accept-new", arguments);
        Assert.Equal(
            new[]
            {
                "-l", "me",
                "-p", "2222",
                "-o", $"IdentityFile=\"{identity}\"",
                "-o", "IdentitiesOnly=yes",
                "--", "gpu-box",
            },
            arguments.Skip(arguments.Count - 10));
    }

    [Fact]
    public void Arguments_RejectFieldsSshWouldReadAsOptions()
    {
        using var directory = new TempDir();
        var target = Target(directory);

        Assert.Throws<InvalidOperationException>(() => Ssh.SshArguments(target with { Host = "" }));
        Assert.Throws<InvalidOperationException>(() => Ssh.SshArguments(target with { Host = "-oProxyCommand=evil" }));
        Assert.Throws<InvalidOperationException>(() => Ssh.SshArguments(target with { Host = "gpu box" }));
        Assert.Throws<InvalidOperationException>(() => Ssh.SshArguments(target with { Host = "gpu\tbox" }));
        Assert.Throws<InvalidOperationException>(() => Ssh.SshArguments(target with { HostKeyAlias = "" }));
        Assert.Throws<InvalidOperationException>(() => Ssh.SshArguments(target with { User = "-me" }));
        Assert.Throws<InvalidOperationException>(() => Ssh.SshArguments(target with { Port = 0 }));
        Assert.Throws<InvalidOperationException>(() => Ssh.SshArguments(target with { Port = 70000 }));
    }

    [Fact]
    public void Arguments_QuoteAndEscapeThePathsSshExpands()
    {
        using var directory = new TempDir();
        var target = Target(directory);

        // `%` 是 ssh 的记号展开符：写成字面量必须加倍。
        var arguments = Ssh.SshArguments(target with { KnownHostsFile = "/data/100%/known_hosts" });
        Assert.Contains("UserKnownHostsFile=\"/data/100%%/known_hosts\"", arguments);

        Assert.Throws<InvalidOperationException>(
            () => Ssh.SshArguments(target with { KnownHostsFile = "/data/\"quoted\"/known_hosts" }));
        Assert.Throws<InvalidOperationException>(() => Ssh.SshArguments(target with { KnownHostsFile = "" }));
    }

    [Fact]
    public async Task AcceptHostKey_AddsOnlyPlainLinesForTheAlias()
    {
        using var directory = new TempDir();
        var target = Target(directory);
        var first = "pi-env-test ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFirstKey";

        await Assert.ThrowsAsync<InvalidOperationException>(() => Ssh.AcceptHostKeyAsync(target, [$"other-alias {first}"]));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Ssh.AcceptHostKeyAsync(target, [$"@cert-authority {first}"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ssh.AcceptHostKeyAsync(target, ["pi-env-test ssh-ed25519 AAA!!"]));

        await Task.WhenAll(
            Ssh.AcceptHostKeyAsync(target, [first]),
            Ssh.AcceptHostKeyAsync(target, [first]));

        Assert.Equal($"{first}\n", await File.ReadAllTextAsync(target.KnownHostsFile));
    }

    [Fact]
    public async Task AcceptHostKey_NeverReplacesATrustedKeySilently()
    {
        using var directory = new TempDir();
        var target = Target(directory);
        var first = "pi-env-test ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFirstKey";
        var second = "pi-env-test ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAISecondKey";
        // 不同类型的主机密钥可以并存。
        var rsa = "pi-env-test ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQDRsaKey";

        await Ssh.AcceptHostKeyAsync(target, [first, rsa]);
        await Assert.ThrowsAsync<HostKeyChangedError>(() => Ssh.AcceptHostKeyAsync(target, [second]));

        await Ssh.ForgetHostKeyAsync(target);
        Assert.Equal("", await File.ReadAllTextAsync(target.KnownHostsFile));

        await Ssh.AcceptHostKeyAsync(target, [second]);
        Assert.Equal($"{second}\n", await File.ReadAllTextAsync(target.KnownHostsFile));
    }

    [Fact]
    public async Task ForgetHostKey_KeepsOtherAliases()
    {
        using var directory = new TempDir();
        var target = Target(directory);
        var mine = "pi-env-test ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFirstKey";
        var other = "another-host ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOtherKey";
        await File.WriteAllTextAsync(target.KnownHostsFile, $"{mine}\n{other}\n");

        await Ssh.ForgetHostKeyAsync(target);

        Assert.Equal($"{other}\n", await File.ReadAllTextAsync(target.KnownHostsFile));
    }

    [Fact]
    public void PackagedDaemon_NamesTheBinaryByPlatformAndArchitecture()
    {
        var path = Ssh.PackagedDaemon("linux", "arm64");
        Assert.EndsWith(Path.Join("bin", "pi-env-linux-arm64", "pi-env"), path, StringComparison.Ordinal);
        Assert.EndsWith("pi-env.exe", Ssh.PackagedDaemon("windows", "x64"), StringComparison.Ordinal);
        // 路径以发布根为基准（差异 E-C2）。
        Assert.StartsWith(AppContext.BaseDirectory, path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployDaemon_RejectsAMissingBinaryBeforeTouchingSsh()
    {
        using var directory = new TempDir();
        var target = Target(directory) with { Ssh = "/nonexistent/ssh" };
        var remote = new RemotePlatform { Platform = "linux", Arch = "x64", Home = "/home/me" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Ssh.DeployDaemonAsync(target, remote, directory.File("missing-daemon")));
        Assert.Contains("No pi-env daemon for linux-x64", error.Message, StringComparison.Ordinal);
    }
}
