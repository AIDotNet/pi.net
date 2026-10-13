using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the pattern helpers ported from the top of <c>core/package-manager.ts</c>. The
/// <c>+</c>/<c>-</c>/<c>!</c> ordering and the "several spellings of a path" matching rule are what
/// the whole package filter and override layer rests on, so each is pinned separately.
/// </summary>
public sealed class PackageManagerPatternsTests
{
    private const string Base = "/pkg";

    private static PathMetadata Metadata(string source, string scope, string origin) =>
        new() { Source = source, Scope = scope, Origin = origin };

    [Theory]
    [InlineData("!a.ts", true)]
    [InlineData("+a.ts", true)]
    [InlineData("-a.ts", true)]
    [InlineData("*.ts", true)]
    [InlineData("a?.ts", true)]
    [InlineData("a.ts", false)]
    public void IsPattern_RecognizesOverridesAndGlobs(string value, bool expected)
        => Assert.Equal(expected, PackageManagerPatterns.IsPattern(value));

    [Theory]
    [InlineData("!a.ts", true)]
    [InlineData("+a.ts", true)]
    [InlineData("-a.ts", true)]
    [InlineData("*.ts", false)]
    [InlineData("a.ts", false)]
    public void IsOverridePattern_RecognizesOnlyTheThreePrefixes(string value, bool expected)
        => Assert.Equal(expected, PackageManagerPatterns.IsOverridePattern(value));

    [Theory]
    [InlineData("*.ts", true)]
    [InlineData("a?.ts", true)]
    [InlineData("a.ts", false)]
    [InlineData("!a.ts", false)]
    public void HasGlobPattern_IgnoresOverridePrefixes(string value, bool expected)
        => Assert.Equal(expected, PackageManagerPatterns.HasGlobPattern(value));

    [Theory]
    [InlineData("./a.ts", "a.ts")]
    [InlineData(".\\a.ts", "a.ts")]
    [InlineData("a.ts", "a.ts")]
    [InlineData("skills/review", "skills/review")]
    public void NormalizeExactPattern_StripsADotSlashPrefix(string value, string expected)
        => Assert.Equal(expected, PackageManagerPatterns.NormalizeExactPattern(value));

    [Fact]
    public void SplitPatterns_PartitionsPlainEntriesFromPatterns()
    {
        var (plain, patterns) = PackageManagerPatterns.SplitPatterns(["a.ts", "*.js", "!b.ts", "dir"]);

        Assert.Equal(["a.ts", "dir"], plain);
        Assert.Equal(["*.js", "!b.ts"], patterns);
    }

    [Fact]
    public void MatchesAnyPattern_MatchesRelativePathBasenameAndAbsolutePath()
    {
        var file = "/pkg/skills/review.md";

        Assert.True(PackageManagerPatterns.MatchesAnyPattern(file, ["skills/review.md"], Base));
        Assert.True(PackageManagerPatterns.MatchesAnyPattern(file, ["review.md"], Base));
        Assert.True(PackageManagerPatterns.MatchesAnyPattern(file, ["/pkg/skills/review.md"], Base));
        Assert.True(PackageManagerPatterns.MatchesAnyPattern(file, ["*.md"], Base));
        Assert.False(PackageManagerPatterns.MatchesAnyPattern(file, ["other.md"], Base));
    }

    [Fact]
    public void MatchesAnyPattern_MatchesASkillThroughItsParentDirectory()
    {
        var skill = "/pkg/skills/review/SKILL.md";

        Assert.True(PackageManagerPatterns.MatchesAnyPattern(skill, ["review"], Base));
        Assert.True(PackageManagerPatterns.MatchesAnyPattern(skill, ["skills/review"], Base));
        Assert.True(PackageManagerPatterns.MatchesAnyPattern(skill, ["/pkg/skills/review"], Base));
        Assert.False(PackageManagerPatterns.MatchesAnyPattern(skill, ["other"], Base));
    }

    [Fact]
    public void MatchesAnyPattern_DoesNotTreatAnOrdinaryFileAsASkill()
    {
        var file = "/pkg/skills/review/notes.md";

        Assert.False(PackageManagerPatterns.MatchesAnyPattern(file, ["review"], Base));
    }

    [Fact]
    public void MatchesAnyExactPattern_IsLiteralAndHandlesSkillParents()
    {
        var skill = "/pkg/skills/review/SKILL.md";

        Assert.True(PackageManagerPatterns.MatchesAnyExactPattern(skill, ["skills/review"], Base));
        Assert.True(PackageManagerPatterns.MatchesAnyExactPattern(skill, ["skills/review/SKILL.md"], Base));
        Assert.True(PackageManagerPatterns.MatchesAnyExactPattern(skill, ["/pkg/skills/review"], Base));

        // Unlike `matchesAnyPattern`, the exact matcher has no bare-parent-name spelling.
        Assert.False(PackageManagerPatterns.MatchesAnyExactPattern(skill, ["review"], Base));
        Assert.False(PackageManagerPatterns.MatchesAnyExactPattern(skill, ["skills/*"], Base));
        Assert.False(PackageManagerPatterns.MatchesAnyExactPattern(skill, [], Base));
    }

    [Fact]
    public void GetOverridePatterns_KeepsOnlyPrefixedEntries()
        => Assert.Equal(
            ["!a", "+b", "-c"],
            PackageManagerPatterns.GetOverridePatterns(["a", "!a", "*.ts", "+b", "-c"]));

    [Fact]
    public void IsEnabledByOverrides_AppliesExcludeThenForceIncludeThenForceExclude()
    {
        var file = "/pkg/extensions/a.ts";

        Assert.True(PackageManagerPatterns.IsEnabledByOverrides(file, [], Base));
        Assert.False(PackageManagerPatterns.IsEnabledByOverrides(file, ["!a.ts"], Base));
        Assert.False(PackageManagerPatterns.IsEnabledByOverrides(file, ["!a.ts", "+a.ts"], Base));

        // A force-include is an *exact* pattern, compared against the path relative to the base dir,
        // so it needs the full relative path — a bare basename only works for a top-level file.
        Assert.True(PackageManagerPatterns.IsEnabledByOverrides(file, ["!a.ts", "+extensions/a.ts"], Base));
        Assert.False(
            PackageManagerPatterns.IsEnabledByOverrides(file, ["!a.ts", "+extensions/a.ts", "-extensions/a.ts"], Base));
        Assert.False(PackageManagerPatterns.IsEnabledByOverrides(file, ["+extensions/a.ts", "-extensions/a.ts"], Base));
        Assert.True(PackageManagerPatterns.IsEnabledByOverrides("/pkg/a.ts", ["!a.ts", "+a.ts"], Base));
    }

    [Fact]
    public void ApplyPatterns_IncludesEverythingWhenThereAreNoPlainPatterns()
        => Assert.Equal(
            new[] { "a.ts", "b.ts", "c.ts" },
            PackageManagerPatterns.ApplyPatterns(["a.ts", "b.ts", "c.ts"], [], Base)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray());

    [Fact]
    public void ApplyPatterns_FiltersByPlainPatterns()
        => Assert.Equal(
            new[] { "a.ts", "c.ts" },
            PackageManagerPatterns.ApplyPatterns(["a.ts", "b.ts", "c.ts"], ["a.ts", "c.ts"], Base)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray());

    [Fact]
    public void ApplyPatterns_ExcludesWithoutIncludes()
        => Assert.Equal(
            new[] { "a.ts", "c.ts" },
            PackageManagerPatterns.ApplyPatterns(["a.ts", "b.ts", "c.ts"], ["!b.ts"], Base)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray());

    [Fact]
    public void ApplyPatterns_ForceIncludeAddsBackWhatAnExcludeRemoved()
        => Assert.Equal(
            new[] { "a.ts", "b.ts", "c.ts" },
            PackageManagerPatterns.ApplyPatterns(["a.ts", "b.ts", "c.ts"], ["*.ts", "!b.ts", "+b.ts"], Base)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray());

    [Fact]
    public void ApplyPatterns_ForceExcludeWinsOverEverything()
        => Assert.Equal(
            new[] { "a.ts", "c.ts" },
            PackageManagerPatterns.ApplyPatterns(["a.ts", "b.ts", "c.ts"], ["*.ts", "!b.ts", "+b.ts", "-b.ts"], Base)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray());

    [Fact]
    public void ApplyAutoloadDisabledPatterns_MapsEachPathToWhetherAPatternEnablesIt()
    {
        var disabled = PackageManagerPatterns.ApplyAutoloadDisabledPatterns(["a.ts", "b.ts"], ["-a.ts"], Base);
        Assert.False(disabled["a.ts"]);
        Assert.False(disabled.ContainsKey("b.ts"));

        var enabled = PackageManagerPatterns.ApplyAutoloadDisabledPatterns(["a.ts"], ["+a.ts"], Base);
        Assert.True(enabled["a.ts"]);

        var globbed = PackageManagerPatterns.ApplyAutoloadDisabledPatterns(["a.ts", "b.ts"], ["*.ts"], Base);
        Assert.True(globbed["a.ts"]);
        Assert.True(globbed["b.ts"]);

        var negated = PackageManagerPatterns.ApplyAutoloadDisabledPatterns(["a.ts"], ["!a.ts"], Base);
        Assert.False(negated["a.ts"]);
    }

    [Fact]
    public void ApplyAutoloadDisabledPatterns_LaterPatternsWinForTheSamePath()
    {
        var result = PackageManagerPatterns.ApplyAutoloadDisabledPatterns(["a.ts"], ["-a.ts", "+a.ts"], Base);
        Assert.True(result["a.ts"]);
    }

    [Fact]
    public void ResourcePrecedenceRank_OrdersBuiltinLastAndProjectLocalFirst()
    {
        Assert.Equal(0, PackageManagerPatterns.ResourcePrecedenceRank(Metadata("local", SourceScope.Project, SourceOrigin.TopLevel)));
        Assert.Equal(1, PackageManagerPatterns.ResourcePrecedenceRank(Metadata("auto", SourceScope.Project, SourceOrigin.TopLevel)));
        Assert.Equal(2, PackageManagerPatterns.ResourcePrecedenceRank(Metadata("local", SourceScope.User, SourceOrigin.TopLevel)));
        Assert.Equal(3, PackageManagerPatterns.ResourcePrecedenceRank(Metadata("auto", SourceScope.User, SourceOrigin.TopLevel)));
        Assert.Equal(4, PackageManagerPatterns.ResourcePrecedenceRank(Metadata("npm:foo", SourceScope.User, SourceOrigin.Package)));
        Assert.Equal(5, PackageManagerPatterns.ResourcePrecedenceRank(Metadata("builtin", SourceScope.User, SourceOrigin.TopLevel)));
    }

    [Fact]
    public void FilePattern_SelectsTheDocumentedExtensions()
    {
        Assert.Matches(PackageManagerPatterns.FilePattern("extensions"), "a.ts");
        Assert.Matches(PackageManagerPatterns.FilePattern("extensions"), "a.js");
        Assert.DoesNotMatch(PackageManagerPatterns.FilePattern("extensions"), "a.md");
        Assert.Matches(PackageManagerPatterns.FilePattern("skills"), "a.md");
        Assert.Matches(PackageManagerPatterns.FilePattern("prompts"), "a.md");
        Assert.Matches(PackageManagerPatterns.FilePattern("themes"), "a.json");
        Assert.Throws<ArgumentException>(() => PackageManagerPatterns.FilePattern("nope"));
    }
}
