using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Path comparison helpers introduced by TS <c>1be312b46</c>: Windows paths compare case-insensitively,
/// POSIX paths stay case-sensitive, containment matches whole segments, and home shortening is anchored.
/// </summary>
public class PathComparisonTests
{
    // ---- getPathComparisonKey / pathsEqual ------------------------------------------------

    [Fact]
    public void ComparisonKey_FoldsCaseOnWindowsOnly()
    {
        Assert.Equal(
            Paths.GetPathComparisonKey(@"C:\Users\Me\Project", windows: true),
            Paths.GetPathComparisonKey(@"c:\users\me\project", windows: true));

        Assert.NotEqual(
            Paths.GetPathComparisonKey("/home/Me/Project", windows: false),
            Paths.GetPathComparisonKey("/home/me/project", windows: false));
    }

    [Fact]
    public void ComparisonKey_ResolvesRelativeSegments()
    {
        Assert.Equal(
            Paths.GetPathComparisonKey(@"C:\a\b\..\c", windows: true),
            Paths.GetPathComparisonKey(@"C:\a\c", windows: true));
    }

    [Fact]
    public void PathsEqual_MatchesDriveLetterCasing()
    {
        Assert.True(Paths.PathsEqual(@"c:\Users\me\project", @"C:\Users\me\project", windows: true));
        Assert.False(Paths.PathsEqual(@"C:\Users\me\project", @"C:\Users\me\other", windows: true));
    }

    [Fact]
    public void PathsEqual_PosixIsCaseSensitive()
    {
        Assert.True(Paths.PathsEqual("/home/me", "/home/me", windows: false));
        Assert.False(Paths.PathsEqual("/home/me", "/home/Me", windows: false));
    }

    // ---- isPathWithin ---------------------------------------------------------------------

    [Fact]
    public void IsPathWithin_AcceptsThePathItself()
    {
        Assert.True(Paths.IsPathWithin(@"C:\a\b", @"C:\a\b", windows: true));
        Assert.True(Paths.IsPathWithin("/a/b", "/a/b", windows: false));
    }

    [Fact]
    public void IsPathWithin_MatchesWholeSegments()
    {
        // The old startsWith check turned "/home/me2" into a child of "/home/me".
        Assert.False(Paths.IsPathWithin("/home/me", "/home/me2/x", windows: false));
        Assert.True(Paths.IsPathWithin("/home/me", "/home/me/x", windows: false));
    }

    [Fact]
    public void IsPathWithin_HandlesFilesystemRoots()
    {
        Assert.True(Paths.IsPathWithin("/", "/x", windows: false));
        Assert.True(Paths.IsPathWithin(@"C:\", @"C:\x", windows: true));
        Assert.False(Paths.IsPathWithin(@"C:\x", @"C:\y", windows: true));
    }

    [Fact]
    public void IsPathWithin_RejectsParentEscape()
    {
        Assert.False(Paths.IsPathWithin("/a/b/c", "/a/b", windows: false));
        Assert.False(Paths.IsPathWithin("/a/b/c", "/a/x", windows: false));
    }

    [Fact]
    public void IsPathWithin_IgnoresCaseOnWindows()
    {
        Assert.True(Paths.IsPathWithin(@"c:\users\me", @"C:\Users\Me\project", windows: true));
    }

    // ---- shortenHomePath ------------------------------------------------------------------

    [Fact]
    public void ShortenHomePath_ReplacesLeadingHome()
    {
        Assert.Equal("~/project", Paths.ShortenHomePath("/home/me/project", "/home/me", windows: false));
        Assert.Equal("~", Paths.ShortenHomePath("/home/me", "/home/me", windows: false));
    }

    [Fact]
    public void ShortenHomePath_DoesNotMatchSiblingDirectory()
    {
        // The unanchored startsWith produced "~2/x" here.
        Assert.Equal("/home/me2/x", Paths.ShortenHomePath("/home/me2/x", "/home/me", windows: false));
    }

    [Fact]
    public void ShortenHomePath_LeavesRelativePathsAlone()
    {
        Assert.Equal("project/x", Paths.ShortenHomePath("project/x", "/home/me", windows: false));
    }

    [Fact]
    public void ShortenHomePath_IgnoresEmptyHome()
    {
        Assert.Equal("/home/me/x", Paths.ShortenHomePath("/home/me/x", "", windows: false));
    }

    [Fact]
    public void ShortenHomePath_UsesWindowsSeparator()
    {
        Assert.Equal(@"~\project", Paths.ShortenHomePath(@"C:\Users\me\project", @"C:\Users\me", windows: true));
    }

    // ---- getResourcePathKey ---------------------------------------------------------------

    [Fact]
    public void ResourcePathKey_KeepsSyntheticIdentifiersVerbatim()
    {
        Assert.Equal("builtin:read", SourceInfos.GetResourcePathKey("builtin:read"));
        Assert.Equal("<inline:test>", SourceInfos.GetResourcePathKey("<inline:test>"));
    }

    [Fact]
    public void ResourcePathKey_FoldsCaseForFilePathsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "pi-path-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var upper = Path.Combine(directory, "CHANGELOG.md");
            var lower = Path.Combine(directory, "changelog.md");
            File.WriteAllText(upper, "x");
            Assert.Equal(SourceInfos.GetResourcePathKey(upper), SourceInfos.GetResourcePathKey(lower));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- getSourcePackageKey --------------------------------------------------------------

    [Fact]
    public void SourcePackageKey_IsJsonStringifyShaped()
    {
        Assert.Equal(
            "{\"source\":\"npm:foo\",\"scope\":\"user\"}",
            SourceInfos.GetSourcePackageKey(new SourcePackageIdentity("npm:foo", "user")));
    }
}
