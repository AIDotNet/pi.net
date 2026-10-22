// ============================================================================
// Tests for the `sessionShareDestination` setting (#10870, upstream cea257439).
//
// The end-to-end side of the setting — what `/share` does with "gist" and "radius" — lives in
// InteractiveModeTests, next to the rest of the share flow.
// ============================================================================

using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class SessionShareDestinationTests
{
    [Fact]
    public void DefaultsToAutoWhenTheSettingIsAbsent()
    {
        Assert.Equal("auto", SettingsManager.InMemory(new Settings()).GetSessionShareDestination());
    }

    [Theory]
    [InlineData("gist")]
    [InlineData("radius")]
    public void KeepsTheExplicitDestinations(string destination)
    {
        var manager = SettingsManager.InMemory(new Settings { SessionShareDestination = destination });

        Assert.Equal(destination, manager.GetSessionShareDestination());
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("GitHub")]
    [InlineData("")]
    public void CoercesAnythingElseToTheDefault(string destination)
    {
        // TS keeps "auto" out of the getter's accepted set: the union's third member is what any
        // unrecognized value coerces to, so a typo or a value from a newer pi falls back to it.
        var manager = SettingsManager.InMemory(new Settings { SessionShareDestination = destination });

        Assert.Equal(SessionShareDestinations.Auto, manager.GetSessionShareDestination());
    }

    [Fact]
    public void TheSetterIsReadBackByTheGetter()
    {
        var manager = SettingsManager.InMemory(new Settings());

        manager.SetSessionShareDestination(SessionShareDestinations.Radius);

        Assert.Equal("radius", manager.GetSessionShareDestination());
    }
}
