using Xunit;

namespace Pi.Env.Tests;

/// <summary>
/// Startup error text (TS <c>479d6e5e2</c>): the daemon's stderr tail is appended to the failure, with
/// terminal escapes and control characters removed and at most 20 lines / 2000 characters kept.
/// </summary>
public class StartupErrorTextTests
{
    [Fact]
    public void AppendsTheStderrTail()
    {
        var message = StartupErrorText.WithStderr("pi-env exited with code 1 before it was ready", "boom\nsecond\n");

        Assert.Equal("pi-env exited with code 1 before it was ready\nremote stderr:\nboom\nsecond", message);
    }

    [Fact]
    public void StripsTerminalEscapes()
    {
        var message = StartupErrorText.WithStderr(
            "failed",
            "\u001b[31merror\u001b[0m: config missing\n\u001b]8;;https://example.invalid\u0007link\u001b]8;;\u0007\n");

        Assert.Equal("failed\nremote stderr:\nerror: config missing\nlink", message);
    }

    [Fact]
    public void DropsControlCharactersAndBlankLines()
    {
        // `\u0000`/`\u0007`, not `\x00b`/`\x07c`: a C# `\x` escape takes up to four hex
        // digits, so `"\x00b"` is U+000B and `"\x07c"` is U+007C.
        var message = StartupErrorText.WithStderr("failed", "a\u0000b\u0007c\n\n   \nlast");

        Assert.Equal("failed\nremote stderr:\nabc\nlast", message);
    }

    [Fact]
    public void KeepsOnlyTheLastTwentyLines()
    {
        var stderr = string.Join("\n", Enumerable.Range(1, 25).Select(index => $"line{index}"));

        var message = StartupErrorText.WithStderr("failed", stderr);

        var lines = message.Split('\n');
        Assert.Equal("failed", lines[0]);
        Assert.Equal("remote stderr:", lines[1]);
        Assert.Equal(20, lines.Length - 2);
        Assert.Equal("line6", lines[2]);
        Assert.Equal("line25", lines[^1]);
    }

    [Fact]
    public void ReturnsTheMessageWhenThereIsNoStderr()
    {
        Assert.Equal("failed", StartupErrorText.WithStderr("failed", ""));
        Assert.Equal("failed", StartupErrorText.WithStderr("failed", "  \n\r\n\t\n"));
    }

    [Fact]
    public void NormalizesCarriageReturns()
    {
        var message = StartupErrorText.WithStderr("failed", "one\r\ntwo\rthree\n");

        Assert.Equal("failed\nremote stderr:\none\ntwo\nthree", message);
    }
}
