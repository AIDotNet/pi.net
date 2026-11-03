using Pi.CodingAgent.Extensions.Mcp;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// `pi mcp login/logout` accepted a server name only; TS <c>be15fe3c1</c> added `-l`/`--local` scope
/// selection and a "did you mean" suggestion for a mistyped name.
/// </summary>
public class McpCliSuggestionTests
{
    [Theory]
    [InlineData("github", "github", 0)]
    [InlineData("githbu", "github", 2)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("kitten", "sitting", 3)]
    public void EditDistance_MatchesLevenshtein(string left, string right, int expected)
    {
        Assert.Equal(expected, McpCli.EditDistance(left, right));
    }

    [Fact]
    public void EditDistance_IsSymmetric()
    {
        Assert.Equal(McpCli.EditDistance("sentry", "sentryy"), McpCli.EditDistance("sentryy", "sentry"));
    }
}
