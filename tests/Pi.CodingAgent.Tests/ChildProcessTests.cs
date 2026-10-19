using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class ChildProcessTests
{
    [Fact]
    public async Task WaitForStdioIdleAsync_RearmsGraceAfterOutputArrives()
    {
        var nowMs = 0L;
        var lastDataTime = 0L;
        var delayCount = 0;
        var streamsEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        await ChildProcess.WaitForStdioIdleAsync(
            streamsEnded,
            () => lastDataTime,
            () => nowMs,
            milliseconds =>
            {
                delayCount++;
                if (delayCount <= 6)
                {
                    nowMs += 50;
                    lastDataTime = nowMs;
                }
                else
                {
                    nowMs += milliseconds;
                }

                return Task.CompletedTask;
            });

        Assert.Equal(7, delayCount);
    }

    [Fact]
    public async Task WaitForStdioIdleAsync_ReturnsWhenStreamsHaveEnded()
    {
        var delayCount = 0;

        await ChildProcess.WaitForStdioIdleAsync(
            Task.CompletedTask,
            () => 0,
            () => 0,
            _ =>
            {
                delayCount++;
                return Task.CompletedTask;
            });

        Assert.Equal(0, delayCount);
    }

    [Fact]
    public async Task WaitForStdioIdleAsync_ReleasesAnIdleInheritedStreamAfterGrace()
    {
        var nowMs = 0L;
        var delayCount = 0;
        var streamsEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        await ChildProcess.WaitForStdioIdleAsync(
            streamsEnded,
            () => 0,
            () => nowMs,
            milliseconds =>
            {
                delayCount++;
                nowMs += milliseconds;
                return Task.CompletedTask;
            });

        Assert.Equal(1, delayCount);
    }
}
