using Pi.CodingAgent.Experimental.Vacation;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class VacationCommandLineTests
{
    [Fact]
    public void ParseDefaultsToANewSession()
    {
        var options = VacationCommandLine.Parse([]);

        Assert.False(options.ContinueSession);
    }

    [Theory]
    [InlineData("--continue")]
    [InlineData("-c")]
    [InlineData("--continue", "-c")]
    public void ParseRecognizesContinueSwitch(params string[] arguments)
    {
        var options = VacationCommandLine.Parse(arguments);

        Assert.True(options.ContinueSession);
    }

    [Fact]
    public void ParseRejectsUnsupportedArguments()
    {
        var error = Assert.Throws<ArgumentException>(() => VacationCommandLine.Parse(["--help"]));

        Assert.Contains("Unknown argument: --help", error.Message, StringComparison.Ordinal);
    }
}
