using Pi.CodingAgent.Core.Tools;
using Xunit;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Tests;

// ============================================================================
// 上游 v1.5.1 #11974（a07f25129）：find 在启动 fd 之前会向上遍历每一级父目录找
// `.git`；若 abort 正好落在那次查找期间，调用已被拒绝但那时还没有可停止的子进程，
// 旧代码会继续启动 fd、让它白跑整棵树且无人回收。修复：spawn 之前再确认一次
// signal，已 abort 就不启动子进程。
//
// 端口 FindTool.cs 里 `.git` 循环（Path.Combine(current, ".git")）之后、
// ChildProcess.SpawnProcess 之前，同样只有 signal.Register（spawn 后才注册）
// 而没有 spawn 前的重新检查。本测试精确复现「abort 落在 .git 查找期间」，断言
// Execute 抛 OperationCanceledException 且从未调用 spawn。
// ============================================================================

public class FindAbortBeforeSpawnTests
{
    [Fact]
    public async Task AbortDuringGitLookup_DoesNotStartFd()
    {
        var cwd = Path.GetTempPath();
        using var cts = new CancellationTokenSource();
        var spawnCalled = 0;

        // ensureFd 未取消时「找到」一个假 fd 路径，让流程走到 .git 循环。
        FindTool.EnsureFdForTests = _ => Task.FromResult<string?>("fd-for-test");
        // 第一次 .git 存在性检查时取消 token，模拟 abort 落在父目录遍历期间。
        FindTool.PathExistsForTests = _ =>
        {
            cts.Cancel();
            return Task.FromResult(false);
        };
        FindTool.SpawnHookForTests = () => spawnCalled++;
        try
        {
            var tool = FindTool.CreateFindToolDefinition(cwd);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                tool.Execute("test-call", new Dictionary<string, object?>
                {
                    ["pattern"] = "*.cs",
                    ["path"] = ".",
                }, cts.Token, null, null));
            // 修复生效：spawn 之前的 ThrowIfCancellationRequested 已抛出，fd 从未启动。
            Assert.Equal(0, spawnCalled);
        }
        finally
        {
            FindTool.EnsureFdForTests = null;
            FindTool.PathExistsForTests = null;
            FindTool.SpawnHookForTests = null;
        }
    }

    [Fact]
    public async Task NoAbort_StartsFd()
    {
        // 对照：未取消时流程照常走到 spawn（证明 seam 生效、不是被别的分支提前挡住）。
        var cwd = Path.GetTempPath();
        FindTool.EnsureFdForTests = _ => Task.FromResult<string?>("fd-for-test");
        FindTool.PathExistsForTests = _ => Task.FromResult(true); // 立刻命中 .git，跳出循环
        var spawnCalled = 0;
        FindTool.SpawnHookForTests = () => spawnCalled++;
        try
        {
            var tool = FindTool.CreateFindToolDefinition(cwd);
            // 真实 spawn 假路径会失败，但那已在 spawn 之后；只关心是否走到了 spawn。
            await Assert.ThrowsAnyAsync<Exception>(() =>
                tool.Execute("test-call", new Dictionary<string, object?>
                {
                    ["pattern"] = "*.cs",
                    ["path"] = ".",
                }, CancellationToken.None, null, null));
            Assert.Equal(1, spawnCalled);
        }
        finally
        {
            FindTool.EnsureFdForTests = null;
            FindTool.PathExistsForTests = null;
            FindTool.SpawnHookForTests = null;
        }
    }
}
