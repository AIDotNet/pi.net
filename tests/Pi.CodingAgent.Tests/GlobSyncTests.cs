using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the <c>globSync</c> subset the package manager needs. Expectations mirror node's
/// <c>globSync(pattern, { cwd })</c> with its default options (<c>dot: false</c>, <c>nodir: false</c>).
/// </summary>
public sealed class GlobSyncTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-globsync-" + Guid.NewGuid().ToString("N")[..8]);

    public GlobSyncTests()
    {
        Directory.CreateDirectory(_root);
        Write("extensions/a.ts");
        Write("extensions/b.js");
        Write("extensions/readme.md");
        Write("extensions/.dotfile.ts");
        Write("extensions/nested/c.ts");
        Write("skills/review/SKILL.md");
        Write("skills/notes.md");
        Write(".hidden/secret.ts");
        Write("node_modules/pkg/index.ts");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Sync_MatchesASingleSegmentGlob()
        => Assert.Equal(["extensions/a.ts"], GlobSync.Sync("extensions/*.ts", _root));

    [Fact]
    public void Sync_MatchesEveryEntryOfASegment()
        => Assert.Equal(
            ["extensions/a.ts", "extensions/b.js", "extensions/nested", "extensions/readme.md"],
            GlobSync.Sync("extensions/*", _root));

    [Fact]
    public void Sync_GlobstarSpansDirectories()
        => Assert.Equal(
            ["extensions/a.ts", "extensions/nested/c.ts"],
            GlobSync.Sync("extensions/**/*.ts", _root));

    [Fact]
    public void Sync_GlobstarAtTheStartAlsoMatchesTheRootLevel()
        => Assert.Contains("extensions/a.ts", GlobSync.Sync("**/*.ts", _root));

    [Fact]
    public void Sync_AGlobSegmentNeverMatchesADotName()
        => Assert.DoesNotContain("extensions/.dotfile.ts", GlobSync.Sync("extensions/*", _root));

    [Fact]
    public void Sync_GlobstarNeverDescendsIntoADotDirectory()
    {
        var results = GlobSync.Sync("**/*.ts", _root);
        Assert.DoesNotContain(results, path => path.StartsWith(".hidden", StringComparison.Ordinal));
    }

    [Fact]
    public void Sync_PlainSegmentMatchesLiterally()
        => Assert.Equal(["extensions/a.ts"], GlobSync.Sync("extensions/a.ts", _root));

    [Fact]
    public void Sync_ATrailingSlashRestrictsToDirectories()
    {
        Assert.Equal(["skills/review"], GlobSync.Sync("skills/*/", _root));
        Assert.DoesNotContain("skills/notes.md", GlobSync.Sync("skills/*/", _root));
    }

    [Fact]
    public void Sync_ResultsAreOrdinalSorted()
    {
        var results = GlobSync.Sync("extensions/**", _root);
        var sorted = results.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, results);
    }

    [Fact]
    public void Sync_SupportsBraceExpansionInASegment()
        => Assert.Equal(
            ["extensions/a.ts", "extensions/b.js"],
            GlobSync.Sync("extensions/*.{ts,js}", _root));

    [Fact]
    public void Sync_ReturnsEmptyForAMissingRootOrAnEmptyPattern()
    {
        Assert.Empty(GlobSync.Sync("extensions/*.ts", Path.Combine(_root, "nope")));
        Assert.Empty(GlobSync.Sync("", _root));
    }

    [Fact]
    public void Sync_MatchesNodeModulesUnlessThePatternExcludesIt()
        => Assert.Contains("node_modules/pkg/index.ts", GlobSync.Sync("node_modules/**/*.ts", _root));

    private void Write(string relativePath)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "");
    }
}
