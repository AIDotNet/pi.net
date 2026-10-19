using Pi.CodingAgent.Experimental.Vacation;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class VacationExtensionsTests
{
    [Fact]
    public void ExtensionsExposePlannerResearchAndSearchContracts()
    {
        var vacation = VacationExtensions.Vacation;
        var search = VacationExtensions.Search;

        Assert.Equal("vacation", vacation.Name);
        Assert.Equal(new[] { "research" }, vacation.Tools?.Select(tool => tool.Name));
        Assert.Single(vacation.Tasks!);
        Assert.Equal("vacation-search", search.Name);
        Assert.Equal(new[] { "search" }, search.Tools?.Select(tool => tool.Name));
        Assert.Equal("safe", search.Tools?.Single().Replay);
        Assert.NotNull(vacation.Sections);
        Assert.Single(vacation.Sections);
    }
}
