using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for the directory collectors ported from the top of <c>core/package-manager.ts</c>. The
/// discovery rules differ per resource type in ways that are easy to get wrong (a directory holding
/// <c>SKILL.md</c> is one skill and is not descended into; prompts and themes are non-recursive;
/// extensions prefer a manifest or an <c>index.ts</c>), so each rule is pinned.
/// </summary>
public sealed class PackageManagerCollectorsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-collect-" + Guid.NewGuid().ToString("N")[..8]);

    public PackageManagerCollectorsTests() => Directory.CreateDirectory(_root);

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
    public void CollectFiles_RecursesAndFiltersByExtension()
    {
        var dir = Dir("pkg");
        Write(dir, "a.ts");
        Write(dir, "b.js");
        Write(dir, "c.md");
        Write(dir, "sub/d.ts");
        Write(dir, "node_modules/e.ts");
        Write(dir, ".hidden/f.ts");

        var files = PackageManagerCollectors.CollectFiles(dir, PackageManagerPatterns.FilePattern("extensions"));

        Assert.Equal(
            new[] { "a.ts", "b.js", "sub/d.ts" },
            files.Select(file => Rel(dir, file)).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void CollectFiles_HonoursPerDirectoryIgnoreFiles()
    {
        var dir = Dir("pkg");
        Write(dir, "kept.ts");
        Write(dir, "ignored.ts");
        Write(dir, ".gitignore", "ignored.ts\n");
        Write(dir, "sub/kept2.ts");
        Write(dir, "sub/ignored2.ts");
        Write(dir, "sub/.ignore", "ignored2.ts\n");

        var files = PackageManagerCollectors.CollectFiles(dir, PackageManagerPatterns.FilePattern("extensions"));

        Assert.Equal(
            new[] { "kept.ts", "sub/kept2.ts" },
            files.Select(file => Rel(dir, file)).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void CollectSkillEntries_ADirectoryWithSkillMdIsOneSkill()
    {
        var dir = Dir("skills");
        Write(dir, "SKILL.md");
        Write(dir, "notes.md");
        Write(dir, "sub/SKILL.md");

        var files = PackageManagerCollectors.CollectSkillEntries(dir, SkillDiscoveryMode.Pi);

        Assert.Equal(new[] { "SKILL.md" }, files.Select(file => Rel(dir, file)).ToArray());
    }

    [Fact]
    public void CollectSkillEntries_PiModeTakesOnlyRootLevelMarkdown()
    {
        var dir = Dir("skills");
        Write(dir, "root.md");
        Write(dir, "sub/nested.md");

        var files = PackageManagerCollectors.CollectSkillEntries(dir, SkillDiscoveryMode.Pi);

        Assert.Equal(new[] { "root.md" }, files.Select(file => Rel(dir, file)).ToArray());
    }

    [Fact]
    public void CollectSkillEntries_AgentsModeTakesOnlyNestedMarkdown()
    {
        var dir = Dir("skills");
        Write(dir, "root.md");
        Write(dir, "sub/nested.md");

        var files = PackageManagerCollectors.CollectSkillEntries(dir, SkillDiscoveryMode.Agents);

        Assert.Equal(new[] { "sub/nested.md" }, files.Select(file => Rel(dir, file)).ToArray());
    }

    [Fact]
    public void CollectSkillEntries_FindsANestedSkillDirectory()
    {
        var dir = Dir("skills");
        Write(dir, "review/SKILL.md");
        Write(dir, "review/extra.md");

        var files = PackageManagerCollectors.CollectSkillEntries(dir, SkillDiscoveryMode.Pi);

        Assert.Equal(new[] { "review/SKILL.md" }, files.Select(file => Rel(dir, file)).ToArray());
    }

    [Fact]
    public void CollectAutoPromptEntries_IsNotRecursive()
    {
        var dir = Dir("prompts");
        Write(dir, "a.md");
        Write(dir, "sub/b.md");
        Write(dir, "c.json");

        var files = PackageManagerCollectors.CollectAutoPromptEntries(dir);

        Assert.Equal(new[] { "a.md" }, files.Select(file => Rel(dir, file)).ToArray());
    }

    [Fact]
    public void CollectAutoThemeEntries_TakesJsonOnly()
    {
        var dir = Dir("themes");
        Write(dir, "dark.json");
        Write(dir, "light.json");
        Write(dir, "notes.md");

        var files = PackageManagerCollectors.CollectAutoThemeEntries(dir);

        Assert.Equal(
            new[] { "dark.json", "light.json" },
            files.Select(file => Rel(dir, file)).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ResolveExtensionEntries_PrefersManifestEntries()
    {
        var dir = Dir("pkg");
        Write(dir, "package.json", """{"pi":{"extensions":["main.ts"]}}""");
        Write(dir, "main.ts");
        Write(dir, "index.ts");

        var entries = PackageManagerCollectors.ResolveExtensionEntries(dir);

        Assert.NotNull(entries);
        Assert.Equal(new[] { "main.ts" }, entries!.Select(file => Rel(dir, file)).ToArray());
    }

    [Fact]
    public void ResolveExtensionEntries_FallsBackToIndexFiles()
    {
        var tsDir = Dir("ts-pkg");
        Write(tsDir, "index.ts");
        Write(tsDir, "index.js");

        var jsDir = Dir("js-pkg");
        Write(jsDir, "index.js");

        var emptyDir = Dir("empty-pkg");

        Assert.Equal(new[] { "index.ts" }, PackageManagerCollectors.ResolveExtensionEntries(tsDir)!.Select(f => Rel(tsDir, f)));
        Assert.Equal(new[] { "index.js" }, PackageManagerCollectors.ResolveExtensionEntries(jsDir)!.Select(f => Rel(jsDir, f)));
        Assert.Null(PackageManagerCollectors.ResolveExtensionEntries(emptyDir));
    }

    [Fact]
    public void CollectAutoExtensionEntries_ReturnsTheRootExtensionWhenDeclared()
    {
        var dir = Dir("extensions");
        Write(dir, "package.json", """{"pi":{"extensions":["main.ts"]}}""");
        Write(dir, "main.ts");
        Write(dir, "other.ts");

        var entries = PackageManagerCollectors.CollectAutoExtensionEntries(dir);

        Assert.Equal(new[] { "main.ts" }, entries.Select(file => Rel(dir, file)).ToArray());
    }

    [Fact]
    public void CollectAutoExtensionEntries_ScansOneLevelOtherwise()
    {
        var dir = Dir("extensions");
        Write(dir, "a.ts");
        Write(dir, "b.js");
        Write(dir, "notes.md");
        Write(dir, "sub/index.ts");

        var entries = PackageManagerCollectors.CollectAutoExtensionEntries(dir);

        Assert.Equal(
            new[] { "a.ts", "b.js", "sub/index.ts" },
            entries.Select(file => Rel(dir, file)).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void CollectResourceFiles_DispatchesPerResourceType()
    {
        var skillsDir = Dir("skills");
        Write(skillsDir, "review/SKILL.md");
        Write(skillsDir, "loose.md");

        var extensionsDir = Dir("extensions");
        Write(extensionsDir, "index.ts");

        var promptsDir = Dir("prompts");
        Write(promptsDir, "a.md");

        var themesDir = Dir("themes");
        Write(themesDir, "a.json");

        // `skills` in "pi" mode takes the root-level `loose.md` *and* the nested `review/SKILL.md`.
        Assert.Equal(
            new[] { "loose.md", "review/SKILL.md" },
            PackageManagerCollectors.CollectResourceFiles(skillsDir, "skills")
                .Select(f => Rel(skillsDir, f)).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "index.ts" },
            PackageManagerCollectors.CollectResourceFiles(extensionsDir, "extensions").Select(f => Rel(extensionsDir, f)).ToArray());
        Assert.Equal(
            new[] { "a.md" },
            PackageManagerCollectors.CollectResourceFiles(promptsDir, "prompts").Select(f => Rel(promptsDir, f)).ToArray());
        Assert.Equal(
            new[] { "a.json" },
            PackageManagerCollectors.CollectResourceFiles(themesDir, "themes").Select(f => Rel(themesDir, f)).ToArray());
    }

    [Fact]
    public void FindGitRepoRoot_WalksUpToTheNearestDotGit()
    {
        var repo = Dir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var nested = Path.Combine(repo, "a", "b");
        Directory.CreateDirectory(nested);

        Assert.Equal(Path.GetFullPath(repo), PackageManagerCollectors.FindGitRepoRoot(nested));
        Assert.Null(PackageManagerCollectors.FindGitRepoRoot(_root));
    }

    [Fact]
    public void CollectAncestorAgentsSkillDirs_StopsAtTheRepoRoot()
    {
        var repo = Dir("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var nested = Path.Combine(repo, "a", "b");
        Directory.CreateDirectory(nested);

        var dirs = PackageManagerCollectors.CollectAncestorAgentsSkillDirs(nested);

        Assert.Equal(3, dirs.Count);
        Assert.EndsWith(Path.Combine("b", ".agents", "skills"), dirs[0]);
        Assert.EndsWith(Path.Combine("a", ".agents", "skills"), dirs[1]);
        Assert.EndsWith(Path.Combine("repo", ".agents", "skills"), dirs[2]);
    }

    [Fact]
    public void ExpandPackageGlob_ResolvesMatchesAndDropsHiddenSegments()
    {
        var dir = Dir("pkg");
        Write(dir, "extensions/a.ts");
        Write(dir, "extensions/b.ts");
        Write(dir, "extensions/.hidden/c.ts");

        var matches = PackageManagerCollectors.ExpandPackageGlob("extensions/**/*.ts", dir);

        Assert.Equal(
            new[] { "extensions/a.ts", "extensions/b.ts" },
            matches.Select(file => Rel(dir, file)).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    private string Dir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string relativePath, string content = "")
    {
        var fullPath = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static string Rel(string dir, string path) => Path.GetRelativePath(dir, path).Replace('\\', '/');
}
