using Pi.Ai.Api;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>上游 1.5.1 #11626：strict 工具超限的拒绝要按会话记住，会话结束才释放。</summary>
public class StrictToolLimitsTests
{
    [Fact]
    public void RemembersRejectionPerSessionUntilCleanup()
    {
        var session = "strict-limits-" + Guid.NewGuid().ToString("N");
        Assert.False(StrictToolLimits.HasExceeded(session));
        Assert.False(StrictToolLimits.HasExceeded(null));

        StrictToolLimits.MarkExceeded(session);
        Assert.True(StrictToolLimits.HasExceeded(session));
        // 重复登记是幂等的。
        StrictToolLimits.MarkExceeded(session);
        Assert.True(StrictToolLimits.HasExceeded(session));

        SessionResources.CleanupSessionResources(session);
        Assert.False(StrictToolLimits.HasExceeded(session));
    }

    [Fact]
    public void MarkWithoutSessionIsANoOp()
    {
        StrictToolLimits.MarkExceeded(null);
        Assert.False(StrictToolLimits.HasExceeded(null));
    }

    [Theory]
    [InlineData("The compiled grammar is too large")]
    [InlineData("Too many strict tools (21)")]
    [InlineData("Schemas contains too many parameters with union types (17)")]
    [InlineData("prefix The compiled grammar is too large suffix")]
    public void RecognisesStrictToolLimitMessages(string message)
        => Assert.True(StrictToolLimits.IsStrictToolLimitErrorMessage(message));

    [Fact]
    public void RejectsUnrelatedMessages()
        => Assert.False(StrictToolLimits.IsStrictToolLimitErrorMessage("overloaded_error"));

    [Fact]
    public void ConstrainedSamplingStillDelegatesTheCheck()
        => Assert.True(ConstrainedSampling.IsStrictToolLimitErrorMessage("Too many strict tools (21)"));
}
