using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the semver range half ported for <c>core/package-manager.ts</c>
/// (<c>validRange</c> / <c>satisfies</c>). Every expectation below is what semver 7.8.5 returns for
/// the same input in strict, non-<c>includePrerelease</c> mode.
/// </summary>
public sealed class SemverRangeTests
{
    [Theory]
    [InlineData("", "*")]
    [InlineData("*", "*")]
    [InlineData(">=0.0.0", "*")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("=1.2.3", "1.2.3")]
    [InlineData(">=1.2.3", ">=1.2.3")]
    [InlineData("^1.2.3", ">=1.2.3 <2.0.0-0")]
    [InlineData("~1.2.3", ">=1.2.3 <1.3.0-0")]
    [InlineData("~>1.2.3", ">=1.2.3 <1.3.0-0")]
    [InlineData("^0.2.3", ">=0.2.3 <0.3.0-0")]
    [InlineData("^0.0.3", ">=0.0.3 <0.0.4-0")]
    [InlineData("^1.2.3-alpha", ">=1.2.3-alpha <2.0.0-0")]
    [InlineData("1.x", ">=1.0.0 <2.0.0-0")]
    [InlineData("1", ">=1.0.0 <2.0.0-0")]
    [InlineData("1.2.x", ">=1.2.0 <1.3.0-0")]
    [InlineData(">1.2", ">=1.3.0")]
    [InlineData("<=1.2.x", "<1.3.0-0")]
    [InlineData("1.2.3 - 1.2.5", ">=1.2.3 <=1.2.5")]
    [InlineData("1.2 - 2.3", ">=1.2.0 <2.4.0-0")]
    [InlineData("1.2.3 || 2.x", "1.2.3||>=2.0.0 <3.0.0-0")]
    [InlineData("> 1.2.3", ">1.2.3")]
    [InlineData("^ 1.2.3", ">=1.2.3 <2.0.0-0")]
    [InlineData("1.2.3 <2.0.0", "1.2.3 <2.0.0")]
    public void ValidRange_NormalizesLikeSemver(string range, string expected)
        => Assert.Equal(expected, SemverRange.ValidRange(range));

    [Theory]
    [InlineData("not a version")]
    [InlineData(">")]
    [InlineData("1.2.3.4")]
    public void ValidRange_ReturnsNullForUnparsableInput(string range)
        => Assert.Null(SemverRange.ValidRange(range));

    [Fact]
    public void ValidRange_ReturnsNullForNull() => Assert.Null(SemverRange.ValidRange(null));

    [Theory]
    [InlineData("1.2.3", "^1.2.3", true)]
    [InlineData("1.9.9", "^1.2.3", true)]
    [InlineData("2.0.0", "^1.2.3", false)]
    [InlineData("1.2.2", "^1.2.3", false)]
    [InlineData("1.2.3", "~1.2.3", true)]
    [InlineData("1.3.0", "~1.2.3", false)]
    [InlineData("1.2.3", "1.x", true)]
    [InlineData("2.0.0", "1.x", false)]
    [InlineData("1.0.0", "", true)]
    [InlineData("1.0.0", "*", true)]
    [InlineData("1.2.3", ">1.2.2", true)]
    [InlineData("1.2.2", ">1.2.2", false)]
    [InlineData("1.2.2", ">=1.2.2", true)]
    [InlineData("1.2.2", "<1.2.3", true)]
    [InlineData("1.2.3", "<=1.2.3", true)]
    [InlineData("1.2.4", "1.2.3 - 1.2.5", true)]
    [InlineData("1.2.6", "1.2.3 - 1.2.5", false)]
    [InlineData("2.5.0", "1.2.3 || 2.x", true)]
    [InlineData("3.0.0", "1.2.3 || 2.x", false)]
    public void Satisfies_MatchesSemver(string version, string range, bool expected)
        => Assert.Equal(expected, SemverRange.Satisfies(version, range));

    [Theory]
    // A prerelease only satisfies a set that names a prerelease on the same major.minor.patch.
    [InlineData("1.2.3-alpha", "^1.2.3", false)]
    [InlineData("1.2.3-alpha.1", ">=1.2.3-alpha.0", true)]
    [InlineData("1.2.4-alpha.1", ">=1.2.3-alpha.0", false)]
    [InlineData("2.0.0-alpha", ">=1.2.3-alpha.0 <2.0.0", false)]
    public void Satisfies_AppliesThePrereleaseRule(string version, string range, bool expected)
        => Assert.Equal(expected, SemverRange.Satisfies(version, range));

    [Fact]
    public void Satisfies_ReturnsFalseForAnEmptyOrUnparsableVersionOrRange()
    {
        Assert.False(SemverRange.Satisfies("", "1.2.3"));
        Assert.False(SemverRange.Satisfies(null, "1.2.3"));
        Assert.False(SemverRange.Satisfies("not-a-version", "1.2.3"));
        Assert.False(SemverRange.Satisfies("1.2.3", "not-a-range"));
        Assert.False(SemverRange.Satisfies("1.2.3", null));
    }
}
