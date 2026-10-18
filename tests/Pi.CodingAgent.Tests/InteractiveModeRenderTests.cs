// ============================================================================
// Tests for the session-rendering helpers of interactive-mode.ts (4f-4j).
//
// Upstream has no test file for these; the vectors are derived from the TS bodies.
// ============================================================================

using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Modes.Interactive;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class InteractiveModeRenderTests
{
    // The leaf takes the port's session model type (ModelSpec); TS passes Model<any>.
    private static ModelSpec Model(string provider, string id, string api) =>
        new()
        {
            Id = id,
            Name = "Some Model",
            Api = api,
            Provider = provider,
            BaseUrl = "https://example.invalid",
        };

    // ---------------------------------------------------------------- unknown model

    [Fact]
    public void OnlyTheAllUnknownPlaceholderCountsAsUnknown()
    {
        Assert.True(InteractiveModeRender.IsUnknownModel(Model("unknown", "unknown", "unknown")));
    }

    [Theory]
    [InlineData("unknown", "unknown", "openai")]
    [InlineData("unknown", "gpt-5", "unknown")]
    [InlineData("anthropic", "unknown", "unknown")]
    [InlineData("anthropic", "claude-x", "anthropic")]
    public void AnyRealFieldMeansTheModelIsKnown(string provider, string id, string api)
    {
        Assert.False(InteractiveModeRender.IsUnknownModel(Model(provider, id, api)));
    }

    [Fact]
    public void AMissingModelIsNotUnknown()
    {
        // TS `!!model` short-circuits: undefined is not "unknown".
        Assert.False(InteractiveModeRender.IsUnknownModel(null));
    }

    // ---------------------------------------------------------------- entry folding

    [Theory]
    [InlineData("custom", null, true)]
    [InlineData("usage", "cache_warm", true)]
    [InlineData("usage", "api", false)]   // only cache-warming usage renders directly
    [InlineData("message", null, false)]
    [InlineData("compaction", null, false)]
    public void OnlyCustomAndCacheWarmEntriesRenderDirectly(string entryType, string? usageKind, bool expected)
    {
        Assert.Equal(expected, InteractiveModeRender.IsRenderedDirectly(entryType, usageKind));
    }

    [Theory]
    [InlineData(CompactionCostKind.Compaction, true, 1, true)]
    [InlineData(CompactionCostKind.BranchSummary, true, 3, true)]
    [InlineData(CompactionCostKind.Compaction, false, 1, false)] // no usage recorded
    [InlineData(CompactionCostKind.Compaction, true, 0, false)]  // nothing survived the cut
    [InlineData("message", true, 1, false)]                      // regular messages never do
    public void OnlyUsageBearingSummariesGetACostNotice(
        string entryType,
        bool hasUsage,
        int messageCount,
        bool expected)
    {
        Assert.Equal(expected, InteractiveModeRender.ShouldAppendCompactionCost(entryType, hasUsage, messageCount));
    }

    [Fact]
    public void TheCostNoticeCarriesItsKindAndUsage()
    {
        var usage = new Usage(10, 20, 30, 40);
        var notice = new CompactionCostNotice { Kind = CompactionCostKind.BranchSummary, Usage = usage };

        Assert.Equal(CompactionCostKind.BranchSummary, notice.Kind);
        Assert.Equal(usage, notice.Usage);
    }

    // ---------------------------------------------------------------- tool result errors

    [Theory]
    [InlineData("aborted", 0, "Operation aborted")]
    [InlineData("aborted", 1, "Aborted after 1 retry attempt")]
    [InlineData("aborted", 3, "Aborted after 3 retry attempts")] // pluralised, like TS
    public void AbortsMentionTheRetryCount(string stopReason, int retryAttempt, string expected)
    {
        Assert.Equal(
            expected,
            InteractiveModeRender.ResolveToolResultErrorMessage(stopReason, "should be ignored", retryAttempt));
    }

    [Theory]
    [InlineData("error", "boom", "boom")]
    [InlineData("error", "", "Error")]     // TS `message.errorMessage || "Error"`
    [InlineData("error", null, "Error")]
    public void ErrorsEchoTheirMessageOrFallBackToError(string stopReason, string? errorMessage, string expected)
    {
        Assert.Equal(
            expected,
            InteractiveModeRender.ResolveToolResultErrorMessage(stopReason, errorMessage, retryAttempt: 0));
    }

    [Theory]
    [InlineData("aborted", true)]
    [InlineData("error", true)]
    [InlineData("stop", false)]
    [InlineData("toolUse", false)]
    public void OnlyAbortedAndErroredMessagesCloseTheirToolComponents(string stopReason, bool expected)
    {
        Assert.Equal(expected, InteractiveModeRender.IsFailedStopReason(stopReason));
    }

    // ---------------------------------------------------------------- user message text

    [Fact]
    public void AStringContentUserMessageIsReturnedVerbatim()
    {
        var message = new UserMessage(MessageContent.From("hello"), 1);

        Assert.Equal("hello", InteractiveModeRender.GetUserMessageText(message));
    }

    [Fact]
    public void TextBlocksAreConcatenatedWithoutASeparator()
    {
        var message = new UserMessage(MessageContent.BlocksOf(
        [
            new TextContent("hello "),
            new TextContent("world"),
        ]), 1);

        Assert.Equal("hello world", InteractiveModeRender.GetUserMessageText(message));
    }

    [Fact]
    public void NonTextBlocksContributeNothing()
    {
        var message = new UserMessage(MessageContent.BlocksOf(
        [
            new ImageContent("image/png", "aGVsbG8="),
            new TextContent("caption"),
        ]), 1);

        Assert.Equal("caption", InteractiveModeRender.GetUserMessageText(message));
    }

    [Fact]
    public void OtherRolesHaveNoUserText()
    {
        // TS guards on `message.role !== "user"` first.
        Assert.Equal("", InteractiveModeRender.GetUserMessageText(new SystemMessage("")));
        Assert.Equal("", InteractiveModeRender.GetUserMessageText(null));
    }
}
