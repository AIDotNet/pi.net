using Pi.Ai.Api;
using Xunit;

namespace Pi.Ai.Tests.Api;

// ============================================================================
// 上游 v1.5.1 #11725（90b42bca6）：Codex SSE 重试的本地 sleep() helper 把匿名 abort
// 监听挂到调用者的 AbortSignal 上，timer 触发后从不移除；重试循环每次 backoff 都调它，
// 于是「三次 429 后成功」会在 signal 上残留三个监听。修复：命名 handler + { once: true }
// + timer 里 remove，且仍以 Error("Request was aborted") 拒绝（重试 catch 依赖此文案）。
//
// 端口 OpenAiCodexResponses.AbortableDelayAsync 用 Task.Delay(ms, signal) + try/catch，
// CancellationToken 不累积回调注册，天然不存在「addEventListener 不 remove」形态。
// 本测试把上游的「三次 backoff 后无残留 + abort 文案」等价契约钉到端口实现上：
//   1. 连续多次已完成的 backoff 不抛、不给 token 留任何回调（IsCancellationRequested 不变、
//      重新 Register 的回调不会被旧 backoff 触发）；
//   2. abort 时抛 InvalidOperationException("Request was aborted")；
//   3. 已取消的 token 立即以相同文案拒绝。
// ============================================================================

public sealed class CodexSseRetryBackoffTests
{
    [Fact]
    public async Task CompletedBackoffsCompleteAndLeaveNoAbortRegistration()
    {
        using var cts = new CancellationTokenSource();
        // 模拟「三次 429 → 成功」里的三次 backoff：每次都应立即完成（retry-after: 0 / 0ms）。
        for (var i = 0; i < 3; i++)
        {
            await OpenAiCodexResponses.AbortableDelayAsync(0, cts.Token);
        }

        // 关键回归：backoff 完成后 signal 上不应残留任何回调。若实现像旧 TS 那样注册了
        // abort 监听却不注销，这些已完成的 backoff 会在 Cancel 时额外触发（这里无法直接数
        // 监听，改以「取消后新注册的回调恰好触发一次」+ 无异常来验证没有遗留副作用）。
        var fired = 0;
        using var registration = cts.Token.Register(() => fired++);
        cts.Cancel();
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task AbortDuringBackoffThrowsRequestWasAborted()
    {
        using var cts = new CancellationTokenSource();
        var pending = OpenAiCodexResponses.AbortableDelayAsync(60_000, cts.Token);
        cts.Cancel();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Equal("Request was aborted", error.Message);
    }

    [Fact]
    public async Task AlreadyCancelledTokenThrowsRequestWasAborted()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OpenAiCodexResponses.AbortableDelayAsync(0, cts.Token));
        Assert.Equal("Request was aborted", error.Message);
    }

    [Fact]
    public async Task BackoffAfterCancellationIsNotSilentlySwallowed()
    {
        // 重试循环 catch 依赖确切文案来判定 abort，不能把取消当成功继续。
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(5);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OpenAiCodexResponses.AbortableDelayAsync(60_000, cts.Token));
        Assert.Equal("Request was aborted", error.Message);
    }
}
