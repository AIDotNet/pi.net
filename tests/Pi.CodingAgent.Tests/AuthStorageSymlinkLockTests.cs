using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

// ============================================================================
// 上游 v1.5.1 #11787 / #11655（core/auth-storage.ts）：
// Node proper-lockfile 的 `realpath: false` 让 auth.json 的每个符号链接别名各有一把锁，
// 两个 pi 进程可并发进入凭据修改区、同时刷新同一 OAuth token。修复：去掉 realpath:false，
// 锁解析到真实文件，所有别名共享一把锁。
//
// 端口 FileAuthStorageBackend 用自研 NodeLock（path + ".lock"）。此前锁键是未解析符号链接的
// authPath 字符串，别名与真实文件落在不同的 .lock 文件 → 同一竞态。本轮把锁键改为
// Paths.CanonicalizePath(authPath)（逐段解析符号链接的 realpath），读写仍走原 authPath。
//
// 本测试用真实 symlink 钉住「别名与真实路径共享同一把锁」的等价行为；无 symlink 权限的平台跳过。
// ============================================================================

public class AuthStorageSymlinkLockTests
{
    private static void RequireSymlink(string linkPath, string target)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symlinks are unavailable here: {error.Message}");
        }
    }

    [Fact]
    public void LockKeyFor_ResolvesSymlinkAliasToRealFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pi-authlock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var real = Path.Combine(dir, "auth.json");
            File.WriteAllText(real, "{}");
            var alias = Path.Combine(dir, "alias.json");
            RequireSymlink(alias, real);

            var realKey = FileAuthStorageBackend.LockKeyFor(real);
            var aliasKey = FileAuthStorageBackend.LockKeyFor(alias);
            // 别名与真实文件的锁键必须一致（共享一把锁）。
            Assert.Equal(realKey, aliasKey);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void LockKeyFor_NormalizesPathAliasesWithoutNeedingSymlinkPrivileges()
    {
        // 不依赖 OS symlink 权限：同一真实文件的两种路径写法必须得到同一规范化锁键，
        // 这是「别名共享一把锁」的核心不变量（#11787 的移植点）。
        var dir = Path.Combine(Path.GetTempPath(), "pi-authlock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var real = Path.Combine(dir, "auth.json");
            File.WriteAllText(real, "{}");
            var dotted = Path.Combine(dir, "nested", "..", "auth.json");
            Directory.CreateDirectory(Path.Combine(dir, "nested"));

            Assert.Equal(
                FileAuthStorageBackend.LockKeyFor(real),
                FileAuthStorageBackend.LockKeyFor(dotted));
            // 锁键是绝对路径（realpath 规范化结果）。
            Assert.True(Path.IsPathFullyQualified(FileAuthStorageBackend.LockKeyFor(real)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void WithLock_SharesLockAcrossSymlinkAlias()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pi-authlock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var real = Path.Combine(dir, "auth.json");
            File.WriteAllText(real, "{}");
            var alias = Path.Combine(dir, "alias.json");
            RequireSymlink(alias, real);

            var realBackend = new FileAuthStorageBackend(real);
            var aliasBackend = new FileAuthStorageBackend(alias);

            // 真实路径持锁期间，别名后端的 WithLock 必须被挡住（共享同一把锁）。
            var entered = false;
            realBackend.WithLock<string?>(_ =>
            {
                var blocked = false;
                try
                {
                    aliasBackend.WithLock<string?>(_ =>
                    {
                        entered = true;
                        return new LockResult<string?>(null);
                    });
                }
                catch (LockHeldException)
                {
                    blocked = true;
                }

                Assert.True(blocked, "symlink 别名应共享真实文件的锁，不能并发进入临界区");
                return new LockResult<string?>(null);
            });

            Assert.False(entered);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }
}
