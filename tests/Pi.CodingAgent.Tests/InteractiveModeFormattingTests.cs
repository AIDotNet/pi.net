// ============================================================================
// Tests for the display/diagnostic helpers of interactive-mode.ts (4f-4a).
//
// These are the pure functions extracted from InteractiveMode (L1381-1762): path shortening,
// scope grouping, source labels and diagnostic rendering. The theme is injected with markers,
// so the assertions pin the exact token each fragment is painted with.
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Xunit;
using IOPath = System.IO.Path;
using Pi.Durable.Env;

namespace Pi.CodingAgent.Tests;

public sealed class InteractiveModeFormattingTests
{
    // ---------------------------------------------------------------- fixtures

    private sealed class MarkerTheme : IInteractiveModeFormatTheme
    {
        public string Fg(string token, string text) => $"<{token}>{text}</{token}>";
    }

    private static readonly MarkerTheme Theme = new();

    private static SourceInfo Src(string source, string scope = "project", string? baseDir = null) => new()
    {
        Path = "p",
        Source = source,
        Scope = scope,
        Origin = "local",
        BaseDir = baseDir,
    };

    private static ResourcePathEntry Entry(string path, SourceInfo? sourceInfo = null) =>
        new() { Path = path, SourceInfo = sourceInfo };

    private static readonly IReadOnlyDictionary<string, SourceInfo> NoSources =
        new Dictionary<string, SourceInfo>(StringComparer.Ordinal);

    // ---------------------------------------------------------------- display paths

    [Fact]
    public void AbbreviatesTheHomeDirectoryToTilde()
    {
        Assert.Equal("~/.pi/config.json", InteractiveModeFormatting.FormatDisplayPath("/home/u/.pi/config.json", "/home/u"));
    }

    [Fact]
    public void LeavesPathsOutsideHomeAlone()
    {
        Assert.Equal("/etc/pi", InteractiveModeFormatting.FormatDisplayPath("/etc/pi", "/home/u"));
    }

    [Fact]
    public void AbbreviatesOnAPlainStringPrefix()
    {
        // TS uses startsWith, not a path-component check, so a sibling sharing the prefix is
        // abbreviated too. Pinned because it looks like a bug but is the upstream behaviour
        // (verified against node: "~" + "/home/user2/.pi".slice("/home/u".length)).
        Assert.Equal("~ser2/.pi", InteractiveModeFormatting.FormatDisplayPath("/home/user2/.pi", "/home/u"));
    }

    [Fact]
    public void StripsATrailingIndexFileFromExtensionPaths()
    {
        const string home = "/home/u";
        Assert.Equal(
            "~/.pi/extensions/foo",
            InteractiveModeFormatting.FormatExtensionDisplayPath("/home/u/.pi/extensions/foo/index.ts", home));
        Assert.Equal(
            "~/.pi/extensions/foo",
            InteractiveModeFormatting.FormatExtensionDisplayPath("/home/u/.pi/extensions/foo/index.js", home));
        Assert.Equal(
            "~/.pi/extensions/foo.ts",
            InteractiveModeFormatting.FormatExtensionDisplayPath("/home/u/.pi/extensions/foo.ts", home));
    }

    [Fact]
    public void FormatsContextPathsRelativeToTheSessionCwd()
    {
        var cwd = IOPath.Combine(IOPath.GetTempPath(), "pi-imode-ctx");

        var relative = InteractiveModeFormatting.FormatContextPath("sub/file.ts", cwd, "/home/u");
        var absolute = InteractiveModeFormatting.FormatContextPath(
            IOPath.Combine(cwd, "sub", "file.ts"),
            cwd,
            "/home/u");

        Assert.Equal("sub/file.ts", relative.Replace('\\', '/'));
        Assert.Equal("sub/file.ts", absolute.Replace('\\', '/'));
    }

    [Fact]
    public void FallsBackToTheAbsolutePathWhenThePathEscapesTheCwd()
    {
        var cwd = IOPath.Combine(IOPath.GetTempPath(), "pi-imode-a");
        var outside = IOPath.Combine(IOPath.GetTempPath(), "pi-imode-b", "file.ts");

        var result = InteractiveModeFormatting.FormatContextPath(outside, cwd, "/home/u");

        Assert.Equal(
            InteractiveModeFormatting.FormatDisplayPath(NodePath.Resolve(outside), "/home/u"),
            result);
    }

    // ---------------------------------------------------------------- startup gating

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void StartupExpansionFollowsVerboseOrToolOutput(bool verbose, bool expanded, bool expected)
    {
        Assert.Equal(expected, InteractiveModeFormatting.GetStartupExpansionState(verbose, expanded));
    }

    [Theory]
    [InlineData(false, null, true)] // quietStartup unset -> header shown
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)] // verbose overrides quietStartup
    public void StartupHeaderIsHiddenOnlyByQuietStartupTrue(bool verbose, bool? quietStartup, bool expected)
    {
        Assert.Equal(expected, InteractiveModeFormatting.ShouldShowStartupHeader(verbose, quietStartup));
    }

    [Theory]
    [InlineData(false, null, false)] // unset -> details hidden
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void StartupDetailsAreShownOnlyWhenQuietStartupIsFalse(bool verbose, bool? quietStartup, bool expected)
    {
        Assert.Equal(expected, InteractiveModeFormatting.ShouldShowStartupDetails(verbose, quietStartup));
    }

    // ---------------------------------------------------------------- short paths

    [Fact]
    public void ShortensAnNpmPackagePathAgainstItsOwnNodeModulesRoot()
    {
        var sourceInfo = Src("npm:pkg", baseDir: "/p/node_modules/pkg");

        Assert.Equal(
            "dist/a.js",
            InteractiveModeFormatting.GetShortPath("/p/node_modules/pkg/dist/a.js", sourceInfo, "/home/u"));
    }

    [Fact]
    public void FallsBackToTheNodeModulesSuffixForAPackagePathFromAnotherRoot()
    {
        var sourceInfo = Src("npm:pkg", baseDir: "/p/node_modules/pkg");

        // The optional sub-directory in `node_modules/(@?[^/]+(?:/[^/]+)?)/(.*)` is greedy, so an
        // unscoped package swallows its first directory: node reports "a.js" for this input.
        // Upstream behaviour, kept as-is.
        Assert.Equal(
            "a.js",
            InteractiveModeFormatting.GetShortPath("/q/node_modules/pkg/dist/a.js", sourceInfo, "/home/u"));

        // A scoped package name already consumes both segments, so the directory survives.
        Assert.Equal(
            "dist/a.js",
            InteractiveModeFormatting.GetShortPath(
                "/q/node_modules/@s/pkg/dist/a.js",
                Src("npm:@s/pkg", baseDir: "/p/node_modules/@s/pkg"),
                "/home/u"));
    }

    [Fact]
    public void ShortensAGitPathAfterHostAndOwner()
    {
        var sourceInfo = Src("git:github.com/a/b");

        Assert.Equal(
            "b/sub/x.ts",
            InteractiveModeFormatting.GetShortPath("/cache/git/github.com/a/b/sub/x.ts", sourceInfo, "/home/u"));
    }

    [Fact]
    public void FallsBackToTheDisplayPathForNonPackageSources()
    {
        Assert.Equal(
            "~/.pi/extensions/foo.ts",
            InteractiveModeFormatting.GetShortPath("/home/u/.pi/extensions/foo.ts", Src("local"), "/home/u"));
    }

    [Fact]
    public void NormalizesBackslashesBeforeMatching()
    {
        // TS normalises to "/" before every regex, then formats the original path.
        Assert.Equal(
            "~\\a.ts",
            InteractiveModeFormatting.GetShortPath("C:\\work\\a.ts", Src("local"), "C:\\work"));
    }

    // ---------------------------------------------------------------- compact labels

    [Fact]
    public void CompactPathLabelKeepsTheLastSegment()
    {
        Assert.Equal(
            "foo.ts",
            InteractiveModeFormatting.GetCompactPathLabel("/home/u/.pi/extensions/foo.ts", null, "/home/u"));
    }

    [Fact]
    public void CompactPathLabelDropsTheTildeSegment()
    {
        Assert.Equal(
            "foo.ts",
            InteractiveModeFormatting.GetCompactPathLabel("/home/u/foo.ts", null, "/home/u"));
    }

    [Fact]
    public void CompactPackageSourceLabelStripsTheNpmPrefix()
    {
        Assert.Equal("pkg", InteractiveModeFormatting.GetCompactPackageSourceLabel(Src("npm:pkg")));
        // TS `slice(...) || source` keeps the whole tag when nothing follows the prefix.
        Assert.Equal("npm:", InteractiveModeFormatting.GetCompactPackageSourceLabel(Src("npm:")));
        Assert.Equal("local", InteractiveModeFormatting.GetCompactPackageSourceLabel(Src("local")));
        Assert.Equal("", InteractiveModeFormatting.GetCompactPackageSourceLabel(null));
    }

    [Fact]
    public void CompactExtensionLabelUsesThePackageNameForARootIndex()
    {
        var sourceInfo = Src("npm:pkg", baseDir: "/p/node_modules/pkg");

        Assert.Equal(
            "pkg",
            InteractiveModeFormatting.GetCompactExtensionLabel(
                "/p/node_modules/pkg/extensions/index.ts",
                sourceInfo,
                "/home/u"));
    }

    [Fact]
    public void CompactExtensionLabelQualifiesASubdirectoryIndex()
    {
        var sourceInfo = Src("npm:pkg", baseDir: "/p/node_modules/pkg");

        Assert.Equal(
            "pkg:foo",
            InteractiveModeFormatting.GetCompactExtensionLabel(
                "/p/node_modules/pkg/extensions/foo/index.ts",
                sourceInfo,
                "/home/u"));
    }

    [Fact]
    public void CompactExtensionLabelKeepsThePathForANonIndexFile()
    {
        var sourceInfo = Src("npm:pkg", baseDir: "/p/node_modules/pkg");

        Assert.Equal(
            "pkg:foo/bar.ts",
            InteractiveModeFormatting.GetCompactExtensionLabel(
                "/p/node_modules/pkg/extensions/foo/bar.ts",
                sourceInfo,
                "/home/u"));
    }

    [Fact]
    public void CompactExtensionLabelFallsBackToThePathForLocalExtensions()
    {
        Assert.Equal(
            "foo.ts",
            InteractiveModeFormatting.GetCompactExtensionLabel(
                "/home/u/.pi/extensions/foo.ts",
                Src("local", "user"),
                "/home/u"));
    }

    [Fact]
    public void CompactDisplayPathSegmentsSplitOnForwardSlashes()
    {
        Assert.Equal(
            [".pi", "extensions", "foo.ts"],
            InteractiveModeFormatting.GetCompactDisplayPathSegments("/home/u/.pi/extensions/foo.ts", "/home/u"));
    }

    [Fact]
    public void NonPackageLabelWidensUntilTheTailIsUnique()
    {
        var all = new List<InteractiveModeFormatting.CompactExtensionCandidate>
        {
            new("/a/x/dup.ts", null, ["a", "x", "dup.ts"]),
            new("/b/x/dup.ts", null, ["b", "x", "dup.ts"]),
        };

        Assert.Equal("a/x/dup.ts", InteractiveModeFormatting.GetCompactNonPackageExtensionLabel("/a/x/dup.ts", 0, all));
        Assert.Equal("b/x/dup.ts", InteractiveModeFormatting.GetCompactNonPackageExtensionLabel("/b/x/dup.ts", 1, all));
    }

    [Fact]
    public void NonPackageLabelStaysShortWhenTheTailIsAlreadyUnique()
    {
        var all = new List<InteractiveModeFormatting.CompactExtensionCandidate>
        {
            new("/a/one.ts", null, ["a", "one.ts"]),
            new("/b/two.ts", null, ["b", "two.ts"]),
        };

        Assert.Equal("one.ts", InteractiveModeFormatting.GetCompactNonPackageExtensionLabel("/a/one.ts", 0, all));
    }

    [Fact]
    public void CompactExtensionLabelsStripIndexFilesAndDisambiguateDuplicates()
    {
        var labels = InteractiveModeFormatting.GetCompactExtensionLabels(
            [
                Entry("/home/u/.pi/extensions/a/index.ts", Src("local", "user")),
                Entry("/home/u/.pi/extensions/b/index.ts", Src("local", "user")),
                Entry("/p/node_modules/pkg/extensions/index.ts", Src("npm:pkg", baseDir: "/p/node_modules/pkg")),
            ],
            "/home/u");

        Assert.Equal(["a", "b", "pkg"], labels);
    }

    // ---------------------------------------------------------------- source labels

    [Theory]
    [InlineData(null, "project", "project", null, "muted")]
    [InlineData("local", "user", "user", null, "muted")]
    [InlineData("local", "temporary", "path", "temp", "muted")]
    [InlineData("local", "other", "path", null, "muted")]
    [InlineData("cli", "temporary", "path", "temp", "muted")]
    [InlineData("cli", "user", "path", null, "muted")]
    [InlineData("npm:pkg", "user", "npm:pkg", "user", "accent")]
    [InlineData("npm:pkg", "temporary", "npm:pkg", "temp", "accent")]
    public void DisplaySourceInfoMapsSourceAndScope(
        string? source,
        string scope,
        string label,
        string? scopeLabel,
        string color)
    {
        var sourceInfo = source is null ? null : Src(source, scope);

        Assert.Equal(
            (label, scopeLabel, color),
            InteractiveModeFormatting.GetDisplaySourceInfo(sourceInfo));
    }

    [Theory]
    [InlineData("cli", "user", "path")]
    [InlineData("local", "temporary", "path")]
    [InlineData("local", "user", "user")]
    [InlineData("local", "project", "project")]
    [InlineData("npm:pkg", "project", "project")]
    [InlineData("npm:pkg", "other", "path")]
    public void ScopeGroupFollowsSourceAndScope(string source, string scope, string expected)
    {
        Assert.Equal(expected, InteractiveModeFormatting.GetScopeGroup(Src(source, scope)));
    }

    [Theory]
    [InlineData("npm:pkg", true)]
    [InlineData("git:github.com/a/b", true)]
    [InlineData("local", false)]
    [InlineData("builtin", false)]
    [InlineData(null, false)]
    public void IsPackageSourceMatchesTheTagPrefix(string? source, bool expected)
    {
        Assert.Equal(expected, InteractiveModeFormatting.IsPackageSource(source is null ? null : Src(source)));
    }

    // ---------------------------------------------------------------- scope groups

    [Fact]
    public void ScopeGroupsAreOrderedProjectUserPathAndEmptyOnesDropped()
    {
        var groups = InteractiveModeFormatting.BuildScopeGroups(
            [
                Entry("/tmp/x.ts", Src("local", "temporary")),
                Entry("/proj/x.ts", Src("local", "project")),
                Entry("/home/u/x.ts", Src("local", "user")),
            ]);

        Assert.Equal(["project", "user", "path"], groups.Select(group => group.Scope));
        Assert.Equal(["/proj/x.ts"], groups[0].Paths.Select(item => item.Path));
    }

    [Fact]
    public void ScopeGroupsBucketPackagesBySourceTag()
    {
        var groups = InteractiveModeFormatting.BuildScopeGroups(
            [
                Entry("/p/node_modules/a/x.ts", Src("npm:a", "project")),
                Entry("/p/node_modules/a/y.ts", Src("npm:a", "project")),
                Entry("/p/node_modules/b/z.ts", Src("npm:b", "project")),
            ]);

        var project = Assert.Single(groups);
        Assert.Equal(["npm:a", "npm:b"], project.Packages.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(2, project.Packages["npm:a"].Count);
        Assert.Empty(project.Paths);
    }

    [Fact]
    public void FormatScopeGroupsPaintsScopePackageAndPathLines()
    {
        var groups = InteractiveModeFormatting.BuildScopeGroups(
            [
                Entry("z.ts"),
                Entry("a.ts"),
                Entry("/x/y.ts", Src("npm:pkg", "project")),
            ]);

        var formatted = InteractiveModeFormatting.FormatScopeGroups(
            groups,
            new FormatScopeGroupsOptions
            {
                FormatPath = item => item.Path,
                FormatPackagePath = (item, _) => item.Path,
            },
            Theme);

        Assert.Equal(
            string.Join(
                "\n",
                "  <accent>project</accent>",
                "<dim>    a.ts</dim>",
                "<dim>    z.ts</dim>",
                "    <mdLink>npm:pkg</mdLink>",
                "<dim>      /x/y.ts</dim>"),
            formatted);
    }

    // ---------------------------------------------------------------- diagnostics

    [Fact]
    public void FindsSourceInfoForAPathOrItsNearestAncestor()
    {
        var sources = new Dictionary<string, SourceInfo>(StringComparer.Ordinal)
        {
            ["/p/node_modules/pkg"] = Src("npm:pkg"),
        };

        Assert.NotNull(InteractiveModeFormatting.FindSourceInfoForPath("/p/node_modules/pkg", sources));
        Assert.NotNull(InteractiveModeFormatting.FindSourceInfoForPath("/p/node_modules/pkg/ext/x.ts", sources));
        Assert.Null(InteractiveModeFormatting.FindSourceInfoForPath("/other/x.ts", sources));
    }

    [Fact]
    public void FormatPathWithSourcePrefersTheScopeQualifiedLabel()
    {
        var sourceInfo = Src("npm:pkg", "user", baseDir: "/p/node_modules/pkg");

        Assert.Equal(
            "npm:pkg (user) dist/a.js",
            InteractiveModeFormatting.FormatPathWithSource("/p/node_modules/pkg/dist/a.js", sourceInfo, "/home/u"));
        Assert.Equal(
            "~/.pi/x.ts",
            InteractiveModeFormatting.FormatPathWithSource("/home/u/.pi/x.ts", null, "/home/u"));
    }

    [Fact]
    public void DiagnosticsGroupCollisionsByResourceName()
    {
        var diagnostics = new List<ResourceDiagnostic>
        {
            ResourceDiagnostic.CollisionOf(
                "dup",
                null,
                new ResourceCollision
                {
                    ResourceType = "skill",
                    Name = "dup",
                    WinnerPath = "/w/a.ts",
                    LoserPath = "/l/a.ts",
                }),
            ResourceDiagnostic.Warning("warn msg", "/w/b.ts"),
            ResourceDiagnostic.Error("err msg"),
        };

        var formatted = InteractiveModeFormatting.FormatDiagnostics(
            diagnostics,
            NoSources,
            "/nope",
            Theme);

        Assert.Equal(
            string.Join(
                "\n",
                // TS paints the two-space indent as part of the warning token.
                "<warning>  \"dup\" collision:</warning>",
                "<dim>    <success>✓</success> /w/a.ts</dim>",
                "<dim>    <warning>✗</warning> /l/a.ts (skipped)</dim>",
                "<warning>  /w/b.ts</warning>",
                "<warning>    warn msg</warning>",
                "<error>  err msg</error>"),
            formatted);
    }

    [Fact]
    public void DiagnosticsKeepCollisionGroupsInFirstSeenOrder()
    {
        static ResourceDiagnostic Collision(string name) => ResourceDiagnostic.CollisionOf(
            name,
            null,
            new ResourceCollision
            {
                ResourceType = "skill",
                Name = name,
                WinnerPath = $"/w/{name}",
                LoserPath = $"/l/{name}",
            });

        var formatted = InteractiveModeFormatting.FormatDiagnostics(
            [Collision("zebra"), Collision("alpha")],
            NoSources,
            "/nope",
            Theme);

        Assert.True(
            formatted.IndexOf("zebra", StringComparison.Ordinal) < formatted.IndexOf("alpha", StringComparison.Ordinal),
            "collision groups must follow insertion order, not alphabetical order");
    }
}
