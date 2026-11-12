using Pi.CodingAgent.Modes;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 v1.5.1 #11599/#11752：已开始的 print/RPC 关闭必须跑完（共享 dispose），
/// 只有重复信号才强制退出；stdin EOF 在信号之后到来时不该跳过 dispose。
/// 逐字对照 <c>test/print-mode.test.ts</c> 与 <c>test/rpc-prompt-response-semantics.test.ts</c>
/// 的关闭用例；去掉修复（首个信号就 process.exit / 重入直接 exit）时这些用例失败。
/// </summary>
public sealed class ShutdownCoordinatorTests
{
    [Fact]
    public async Task DisposeAsync_IsShared_CalledOnce()
    {
        var disposeCalls = 0;
        var coord = new ShutdownCoordinator(
            () => { disposeCalls++; return Task.CompletedTask; },
            _ => { });

        // finally 与信号同时请求 dispose → 只跑一次。
        var a = coord.DisposeAsync();
        var b = coord.DisposeAsync();
        await Task.WhenAll(a, b);
        Assert.Equal(1, disposeCalls);
    }

    [Fact]
    public void FirstSignal_StartsShutdown_DoesNotForceExit()
    {
        var forced = new List<int>();
        var coord = new ShutdownCoordinator(() => Task.CompletedTask, forced.Add);

        var started = coord.OnSignal(143);
        Assert.True(started); // 首个信号启动正常关闭
        Assert.Equal(1, coord.SignalCount);
        Assert.True(coord.ShuttingDown);
        Assert.Empty(forced); // 不强制退出：等 dispose 完成
    }

    [Fact]
    public async Task RepeatedSignalDuringDispose_ForcesExit()
    {
        // dispose 卡住（模拟 pending MCP token 刷新）时，第二个信号强制退出；第一个信号只等待。
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forced = new List<int>();
        var coord = new ShutdownCoordinator(() => disposeGate.Task, forced.Add);

        Assert.True(coord.OnSignal(143));
        var dispose = coord.DisposeAsync();
        Assert.False(dispose.IsCompleted); // dispose 进行中

        Assert.False(coord.OnSignal(143)); // 重复信号
        Assert.Contains(143, forced);
        Assert.Equal(2, coord.SignalCount);

        disposeGate.SetResult();
        await dispose;
    }

    [Fact]
    public void SignalDuringNormalShutdown_DoesNotForceExitOnFirstSignal()
    {
        // 正常控制流（stdin EOF）先进入关闭，随后到达的首个信号不该强制退出（进程管理器同时关 stdin + 发 SIGTERM 只算一次）。
        var forced = new List<int>();
        var coord = new ShutdownCoordinator(() => Task.CompletedTask, forced.Add);
        coord.BeginShutdown();

        Assert.False(coord.OnSignal(143)); // 关闭已在进行，首个信号只等待
        Assert.Empty(forced);
        Assert.Equal(1, coord.SignalCount);
    }

    [Fact]
    public void NormalShutdownThenRepeatedSignal_ForcesExit()
    {
        var forced = new List<int>();
        var coord = new ShutdownCoordinator(() => Task.CompletedTask, forced.Add);
        coord.BeginShutdown();
        coord.OnSignal(143);  // 关闭中首个信号
        coord.OnSignal(143);  // 第二个 → 强制退出

        Assert.Contains(143, forced);
        Assert.Equal(2, coord.SignalCount);
    }

    [Fact]
    public void Reentry_AfterShutdownStarted_DoesNotRestart()
    {
        var coord = new ShutdownCoordinator(() => Task.CompletedTask, _ => { });
        Assert.True(coord.OnSignal(129));
        Assert.False(coord.OnSignal(129)); // 重入：不重复启动 dispose
        Assert.True(coord.ShuttingDown);
    }
}
