// ============================================================================
// Tests for buildInitialMessage (TS `cli/initial-message.ts`), landed with 4g-1.
// Mirrors test/initial-message.test.ts case-for-case.
// ============================================================================

using Pi.CodingAgent.Cli;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class CliInitialMessageTests
{
    private static Args CreateArgs(params string[] messages)
    {
        var args = new Args();
        args.Messages.AddRange(messages);
        return args;
    }

    [Fact]
    public void MergesPipedStdinWithTheFirstCliMessageIntoOnePrompt()
    {
        var parsed = CreateArgs("Summarize the text given");
        var result = InitialMessages.Build(new InitialMessageInput
        {
            Parsed = parsed,
            StdinContent = "README contents\n",
        });

        Assert.Equal("README contents\nSummarize the text given", result.InitialMessage);
        Assert.Empty(parsed.Messages);
    }

    [Fact]
    public void UsesStdinAsTheInitialPromptWhenNoCliMessageIsPresent()
    {
        var parsed = CreateArgs();
        var result = InitialMessages.Build(new InitialMessageInput
        {
            Parsed = parsed,
            StdinContent = "README contents",
        });

        Assert.Equal("README contents", result.InitialMessage);
        Assert.Empty(parsed.Messages);
    }

    [Fact]
    public void CombinesStdinFileTextAndFirstCliMessageInOnePrompt()
    {
        var parsed = CreateArgs("Explain it", "Second message");
        var result = InitialMessages.Build(new InitialMessageInput
        {
            Parsed = parsed,
            StdinContent = "stdin\n",
            FileText = "file\n",
        });

        Assert.Equal("stdin\nfile\nExplain it", result.InitialMessage);
        Assert.Equal(["Second message"], parsed.Messages);
    }

    [Fact]
    public void ReturnsNothingWhenThereIsNoContentAtAll()
    {
        var parsed = CreateArgs();
        var result = InitialMessages.Build(new InitialMessageInput { Parsed = parsed });

        Assert.Null(result.InitialMessage);
        Assert.Null(result.InitialImages);
    }

    [Fact]
    public void SkipsEmptyFileTextButKeepsImages()
    {
        // TS `if (fileText)` — the empty string is falsy, so it contributes no part.
        var parsed = CreateArgs();
        var result = InitialMessages.Build(new InitialMessageInput
        {
            Parsed = parsed,
            FileText = "",
            FileImages = [],
        });

        Assert.Null(result.InitialMessage);
        Assert.Null(result.InitialImages);
    }
}
