using System.Diagnostics;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <see cref="NodeLock"/> 的等待窗口契约：上游 #10801 给「瞬时权限失败」加了单独的 5 次上限，
/// 这条上限不能顺带缩短「锁已被别的持有者占着」的正常等待。
/// </summary>
public class NodeLockTests
{
    [Fact]
    public async Task WaitsTheWholeWindowWhenAnotherHolderOwnsTheLock()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nodelock-{Guid.NewGuid():N}"[..20]);
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "auth.json");
            await File.WriteAllTextAsync(Path.Combine(directory, "auth.json.lock"), "held");

            var stopwatch = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<LockHeldException>(
                async () => await NodeLock.AcquireAsync(
                    path, CancellationToken.None, maxWaitMs: 150, fixedRetryMs: 10));
            stopwatch.Stop();

            Assert.Equal("ELOCKED", error.Code);
            Assert.True(
                stopwatch.ElapsedMilliseconds >= 100,
                $"gave up after {stopwatch.ElapsedMilliseconds}ms; the wait window was cut short");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A stubborn artifact must not mask the test result.
            }
        }
    }
}
