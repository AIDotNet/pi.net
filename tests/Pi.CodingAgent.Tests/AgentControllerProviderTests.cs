using Pi.Ai.Types;
using Pi.CodingAgent.Experimental.Services;
using Pi.Durable;
using Pi.Durable.Types;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the agent-controller provider's pure leaves (4g-3b). The orchestration itself
/// needs a live durable harness, so only the three module-level helpers TS keeps outside the factory are
/// pinned here: <c>parseSubmissionId</c>, <c>toInput</c> and <c>toAgentError</c>.
/// </summary>
public class AgentControllerProviderTests
{
    // ---------------------------------------------------------------- parseSubmissionId

    [Theory]
    [InlineData("1", 1L)]
    [InlineData("42", 42L)]
    [InlineData("9007199254740991", 9007199254740991L)]
    public void ParseSubmissionId_AcceptsPositiveSafeIntegers(string value, long expected)
    {
        var id = AgentControllerProvider.ParseSubmissionId(value);

        Assert.NotNull(id);
        Assert.Equal(expected, id!.Value.Value);
    }

    [Theory]
    // TS: /^[1-9][0-9]*$/ — no leading zero, no sign, no fraction, no whitespace.
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("+1")]
    [InlineData("1\n")]
    // Number.MAX_SAFE_INTEGER + 1 — Number() would round, so TS rejects it.
    [InlineData("9007199254740992")]
    // Beyond long.MaxValue, so TryParse fails as well.
    [InlineData("99999999999999999999")]
    public void ParseSubmissionId_RejectsEverythingElse(string value) =>
        Assert.Null(AgentControllerProvider.ParseSubmissionId(value));

    // ---------------------------------------------------------------- toInput

    [Fact]
    public void ToInput_WithoutImagesIsASingleTextBlock()
    {
        var blocks = AgentControllerProvider.ToInput(new AgentPromptRequest("hi", null));

        var text = Assert.IsType<TextContent>(Assert.Single(blocks));
        Assert.Equal("hi", text.Text);
    }

    [Fact]
    public void ToInput_WithAnEmptyImageListIsStillASingleTextBlock()
    {
        // TS: `images === null || images.length === 0` — an empty array takes the same branch.
        var blocks = AgentControllerProvider.ToInput(new AgentPromptRequest("hi", []));

        Assert.Single(blocks);
        Assert.IsType<TextContent>(blocks[0]);
    }

    [Fact]
    public void ToInput_PutsTheMessageFirstAndImagesAfter()
    {
        var blocks = AgentControllerProvider.ToInput(new AgentPromptRequest("hi",
        [
            new AgentPromptImage("AAA", "image/png"),
            new AgentPromptImage("BBB", "image/jpeg"),
        ]));

        Assert.Collection(
            blocks,
            block => Assert.Equal("hi", Assert.IsType<TextContent>(block).Text),
            block =>
            {
                var image = Assert.IsType<ImageContent>(block);
                Assert.Equal("AAA", image.Data);
                Assert.Equal("image/png", image.MimeType);
            },
            block =>
            {
                var image = Assert.IsType<ImageContent>(block);
                Assert.Equal("BBB", image.Data);
                Assert.Equal("image/jpeg", image.MimeType);
            });
    }

    // ---------------------------------------------------------------- toAgentError

    [Fact]
    public void ToAgentError_MapsBusyToTheBusyCode()
    {
        var error = AgentControllerProvider.ToAgentError(new ConversationBusy(ConversationId.From(7)));

        Assert.Equal("busy", error.Code);
        Assert.Equal("Conversation 7 is busy", error.Message);
    }

    [Fact]
    public void ToAgentError_MapsEverythingElseToOperationFailed()
    {
        var error = AgentControllerProvider.ToAgentError(new InvalidOperationException("nope"));

        Assert.Equal("operation_failed", error.Code);
        Assert.Equal("nope", error.Message);
    }
}
