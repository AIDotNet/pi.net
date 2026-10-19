using System.Text.Json.Nodes;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Tests for <see cref="DefaultPackageManager"/>. The <c>settings.packages</c> half (4e-3c-2b) is the
/// one the port was missing entirely: string and object entries, <c>autoload: false</c> deltas,
/// manifest globs and project-over-user dedupe.
/// </summary>
public sealed class PackageManagerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-pkg-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<SettingsManager> _managers = [];

    public PackageManagerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // SettingsManager persists through an async write queue (the port of TS's enqueueWrite/flush).
        // Drain it before deleting the temp tree: otherwise a queued write can still be creating
        // settings.json.lock while Directory.Delete walks the directory, which on Windows fails with
        // UnauthorizedAccessException on the momentarily open lock file.
        foreach (var manager in _managers)
        {
            try
            {
                manager.FlushAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // A failed write is surfaced through the manager's own error reporting.
            }
        }

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

    private (string Cwd, string AgentDir, SettingsManager Settings) NewEnv()
    {
        var cwd = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]);
        var agentDir = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agentDir);
        Directory.CreateDirectory(cwd);
        var settings = SettingsManager.Create(cwd, agentDir);
        _managers.Add(settings);
        return (cwd, agentDir, settings);
    }

    // ========================================================================
    // resolve：settings 路径 / 自动发现 / 内置扩展
    // ========================================================================

    [Fact]
    public async Task Resolve_ReturnsNoPackageSourcedPathsWhenEmpty()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var result = await new DefaultPackageManager(settings, cwd, agentDir).ResolveAsync();
        Assert.Empty(result.Extensions);
        Assert.True(result.Skills.All(resource => resource.Metadata.Source == "auto" && resource.Metadata.Origin == "top-level"));
    }

    [Fact]
    public async Task Resolve_IncludesLocalExtensionFromSettings()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var extDir = Path.Combine(agentDir, "extensions");
        Directory.CreateDirectory(extDir);
        var extPath = Path.Combine(extDir, "my-extension.ts");
        File.WriteAllText(extPath, "export default {}");
        settings.SetExtensionPaths(["extensions/my-extension.ts"]);
        var result = await new DefaultPackageManager(settings, cwd, agentDir).ResolveAsync();
        var extension = Assert.Single(result.Extensions);
        Assert.True(PathsEqual(extension.Path, extPath));
        Assert.True(extension.Enabled);

        // TS's `resolveLocalEntries` passes a metadata object with only source/scope/origin — no
        // baseDir and no packageRoot (the pattern match is done against an explicit baseDir instead).
        Assert.Null(extension.Metadata.BaseDir);
        Assert.Null(extension.Metadata.PackageRoot);
        Assert.Equal("local", extension.Metadata.Source);
        Assert.Equal("top-level", extension.Metadata.Origin);
    }

    [Fact]
    public async Task Resolve_AutoDiscoversRootMarkdownSkills()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var skillsDir = Path.Combine(agentDir, "skills");
        Directory.CreateDirectory(skillsDir);
        var skillPath = Path.Combine(skillsDir, "summarize.md");
        File.WriteAllText(skillPath, "---\nname: summarize\n---\n");
        var result = await new DefaultPackageManager(settings, cwd, agentDir).ResolveAsync();
        Assert.Contains(result.Skills, resource => PathsEqual(resource.Path, skillPath) && resource.Enabled);
    }

    [Fact]
    public async Task Resolve_AutoDiscoversSkillMdFiles()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var nested = Path.Combine(agentDir, "skills", "review");
        Directory.CreateDirectory(nested);
        var skillPath = Path.Combine(nested, "SKILL.md");
        File.WriteAllText(skillPath, "---\nname: review\n---\n");
        var result = await new DefaultPackageManager(settings, cwd, agentDir).ResolveAsync();
        Assert.Contains(result.Skills, resource => PathsEqual(resource.Path, skillPath) && resource.Enabled);
    }

    [Fact]
    public async Task Resolve_ExcludesBuiltinExtensionsWhenAnOverridePatternMatches()
    {
        var (cwd, agentDir, settings) = NewEnv();

        // TS spells this `-builtin:mcp`: the override patterns are matched against the *resolved*
        // path, and a built-in's resolved path is `builtin:<name>`.
        settings.SetExtensionPaths(["-builtin:mcp"]);
        var result = await new DefaultPackageManager(settings, cwd, agentDir, ["mcp"]).ResolveAsync();

        var mcp = Assert.Single(result.Extensions);
        Assert.False(mcp.Enabled);
        Assert.Equal("builtin:mcp", mcp.Path);
        Assert.Equal("builtin", mcp.Metadata.Source);
        Assert.Equal("user", mcp.Metadata.Scope);
        Assert.Equal("top-level", mcp.Metadata.Origin);
    }

    [Fact]
    public async Task Resolve_ForceExcludesAnAutoDiscoveredSkillByItsRelativePath()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var skillsDir = Path.Combine(agentDir, "skills");
        Directory.CreateDirectory(skillsDir);
        var skillPath = Path.Combine(skillsDir, "summarize.md");
        File.WriteAllText(skillPath, "---\nname: summarize\n---\n");

        // A `-` entry is an *exact* pattern compared against the path relative to the scope base dir,
        // so the bare file name would not match; `skills/summarize.md` does.
        settings.SetSkillPaths(["-skills/summarize.md"]);
        var result = await new DefaultPackageManager(settings, cwd, agentDir).ResolveAsync();

        var skill = Assert.Single(result.Skills, resource => PathsEqual(resource.Path, skillPath));
        Assert.False(skill.Enabled);
        Assert.Equal("auto", skill.Metadata.Source);
        Assert.Equal("top-level", skill.Metadata.Origin);
    }

    // ========================================================================
    // resolve：settings.packages（4e-3c-2b 新落地）
    // ========================================================================

    [Fact]
    public async Task Resolve_CollectsResourcesFromAConfiguredLocalPackage()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var pkg = NewPackage("pkg-local");
        WriteFile(pkg, "extensions/hello.ts");
        WriteFile(pkg, "skills/review/SKILL.md", "---\nname: review\n---\n");
        WriteFile(pkg, "themes/dark.json", "{}");

        settings.SetPackages([JsonValue.Create(pkg)]);
        var result = await new DefaultPackageManager(settings, cwd, agentDir).ResolveAsync();

        var extension = Assert.Single(result.Extensions);
        Assert.True(PathsEqual(extension.Path, Path.Combine(pkg, "extensions", "hello.ts")));
        Assert.True(extension.Enabled);
        Assert.Equal("package", extension.Metadata.Origin);
        Assert.Equal("user", extension.Metadata.Scope);
        Assert.True(PathsEqual(pkg, extension.Metadata.PackageRoot!));
        Assert.True(PathsEqual(pkg, extension.Metadata.BaseDir!));

        Assert.Single(result.Skills, resource => PathsEqual(resource.Path, Path.Combine(pkg, "skills", "review", "SKILL.md")));
        Assert.Single(result.Themes, resource => PathsEqual(resource.Path, Path.Combine(pkg, "themes", "dark.json")));
    }

    [Fact]
    public async Task Resolve_AppliesAnObjectFilterToAPackage()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var pkg = NewPackage("pkg-filtered");
        WriteFile(pkg, "extensions/hello.ts");
        WriteFile(pkg, "skills/review/SKILL.md", "---\nname: review\n---\n");
        WriteFile(pkg, "skills/other/SKILL.md", "---\nname: other\n---\n");

        settings.SetPackages(
        [
            new JsonObject
            {
                ["source"] = pkg,
                ["skills"] = new JsonArray("review"),
            },
        ]);
        var result = await new DefaultPackageManager(settings, cwd, agentDir).ResolveAsync();

        // Unmentioned kinds fall back to the conventional directory.
        Assert.Single(result.Extensions, resource => resource.Enabled);

        // The named skill is enabled; the other is still *reported* but disabled, because
        // `applyPackageFilter` adds every candidate file with its enabled flag.
        Assert.Single(result.Skills, resource => PathsEqual(resource.Path, Path.Combine(pkg, "skills", "review", "SKILL.md")) && resource.Enabled);
        Assert.Single(result.Skills, resource => PathsEqual(resource.Path, Path.Combine(pkg, "skills", "other", "SKILL.md")) && !resource.Enabled);
    }

    [Fact]
    public async Task Resolve_AnEmptyFilterArrayDisablesThatKind()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var pkg = NewPackage("pkg-empty-filter");
        WriteFile(pkg, "extensions/hello.ts");
        WriteFile(pkg, "skills/review/SKILL.md", "---\nname: review\n---\n");

        settings.SetPackages(
        [
            new JsonObject
            {
                ["source"] = pkg,
                ["skills"] = new JsonArray(),
            },
        ]);
        var result = await new DefaultPackageManager(settings, cwd, agentDir).ResolveAsync();

        var skill = Assert.Single(result.Skills, resource => resource.Metadata.Origin == "package");
        Assert.False(skill.Enabled);
    }

    [Fact]
    public async Task Resolve_ReadsManifestEntriesIncludingGlobs()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var pkg = NewPackage("pkg-manifest");
        WriteFile(pkg, "package.json", """{"pi":{"extensions":["ext/*.ts"]}}""");
        WriteFile(pkg, "ext/a.ts");
        WriteFile(pkg, "ext/b.ts");
        WriteFile(pkg, "ext/c.js");

        settings.SetPackages([JsonValue.Create(pkg)]);
        var result = await new DefaultPackageManager(settings, cwd, agentDir).ResolveAsync();

        Assert.Equal(
            new[] { "a.ts", "b.ts" },
            result.Extensions.Select(resource => Path.GetFileName(resource.Path))
                .OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.All(result.Extensions, resource => Assert.True(resource.Enabled));
    }

    [Fact]
    public async Task Resolve_ProjectEntryWinsOverTheUserEntryWithTheSameIdentity()
    {
        var (cwd, agentDir, settings) = NewEnv();
        settings.SetProjectTrusted(true);
        var pkg = NewPackage("pkg-both-scopes");
        WriteFile(pkg, "extensions/hello.ts");

        // An absolute local path has the same identity in both scopes, so dedupe keeps the project one.
        settings.SetPackages([JsonValue.Create(pkg)]);
        settings.SetProjectPackages([JsonValue.Create(pkg)]);
        var result = await new DefaultPackageManager(settings, cwd, agentDir).ResolveAsync();

        var extension = Assert.Single(result.Extensions, resource => resource.Metadata.Origin == "package");
        Assert.Equal("project", extension.Metadata.Scope);
    }

    [Fact]
    public async Task Resolve_AnAutoloadFalseProjectEntryIsADeltaOverTheUserEntry()
    {
        var (cwd, agentDir, settings) = NewEnv();
        settings.SetProjectTrusted(true);
        var pkg = NewPackage("pkg-delta");
        WriteFile(pkg, "extensions/hello.ts");
        WriteFile(pkg, "skills/review/SKILL.md", "---\nname: review\n---\n");

        settings.SetPackages(
        [
            new JsonObject
            {
                ["source"] = pkg,
                ["skills"] = new JsonArray("review"),
            },
        ]);
        settings.SetProjectPackages(
        [
            new JsonObject
            {
                ["source"] = pkg,
                ["autoload"] = false,
                ["skills"] = new JsonArray("-skills/review/SKILL.md"),
            },
        ]);

        var result = await new DefaultPackageManager(settings, cwd, agentDir).ResolveAsync();

        // The delta runs first and "first wins", so its explicit disable sticks …
        var skill = Assert.Single(result.Skills, resource => resource.Metadata.Origin == "package");
        Assert.False(skill.Enabled);
        Assert.Equal("project", skill.Metadata.Scope);

        // … while the kinds the delta is silent about still come from the user entry.
        Assert.Single(result.Extensions, resource => resource.Enabled && resource.Metadata.Origin == "package");
    }

    [Fact]
    public async Task Resolve_AsksOnMissingAndSkipsWhenToldTo()
    {
        var (cwd, agentDir, settings) = NewEnv();
        settings.SetPackages([JsonValue.Create("npm:left-pad@1.3.0")]);

        var asked = new List<string>();
        var result = await new DefaultPackageManager(settings, cwd, agentDir)
            .ResolveAsync(source =>
            {
                asked.Add(source);
                return Task.FromResult(MissingSourceAction.Skip);
            });

        Assert.Equal(["npm:left-pad@1.3.0"], asked);
        Assert.Empty(result.Extensions);
    }

    [Fact]
    public async Task Resolve_ThrowsWhenOnMissingSaysError()
    {
        var (cwd, agentDir, settings) = NewEnv();
        settings.SetPackages([JsonValue.Create("npm:left-pad@1.3.0")]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DefaultPackageManager(settings, cwd, agentDir)
                .ResolveAsync(_ => Task.FromResult(MissingSourceAction.Error)));
        Assert.Contains("left-pad", error.Message, StringComparison.Ordinal);
    }

    // ========================================================================
    // listConfiguredPackages / getInstalledPath
    // ========================================================================

    [Fact]
    public void ListConfiguredPackages_ReportsBothScopesAndFilteredEntries()
    {
        var (cwd, agentDir, settings) = NewEnv();
        settings.SetProjectTrusted(true);
        var pkgDir = NewPackage("listed-pkg");
        settings.SetPackages([JsonValue.Create(pkgDir)]);
        settings.SetProjectPackages([new JsonObject { ["source"] = pkgDir }]);

        var configured = new DefaultPackageManager(settings, cwd, agentDir).ListConfiguredPackages();

        Assert.Equal(2, configured.Count);
        Assert.Equal("user", configured[0].Scope);
        Assert.False(configured[0].Filtered);
        Assert.True(PathsEqual(pkgDir, configured[0].InstalledPath!));
        Assert.Equal("project", configured[1].Scope);
        Assert.True(configured[1].Filtered);
        Assert.True(PathsEqual(pkgDir, configured[1].InstalledPath!));
    }

    // ========================================================================
    // parseSource / settings 读写
    // ========================================================================

    [Fact]
    public void ParseSource_NpmPrefixAndHttpsGit()
    {
        var npm = DefaultPackageManager.ParseSource("npm:@scope/pkg@1.2.3");
        Assert.Equal("npm", npm.Type);
        Assert.Equal("@scope/pkg", npm.Locator);
        Assert.Equal("1.2.3", npm.Version);
        Assert.True(npm.Pinned);
        // `getNpmVersionRange` is `validRange(version)`, and an exact version is a valid range of one.
        Assert.Equal("1.2.3", npm.Range);

        var unpinned = DefaultPackageManager.ParseSource("npm:left-pad");
        Assert.False(unpinned.Pinned);
        Assert.Null(unpinned.Range);

        var range = DefaultPackageManager.ParseSource("npm:left-pad@^1.2.0");
        Assert.Equal("^1.2.0", range.Version);
        Assert.False(range.Pinned);
        Assert.Equal(">=1.2.0 <2.0.0-0", range.Range);

        var git = DefaultPackageManager.ParseSource("https://github.com/mariozechner/pi-skills.git");
        Assert.Equal("git", git.Type);
        Assert.Equal("github.com", git.Host);
        Assert.Equal("mariozechner/pi-skills", git.Path);
        Assert.False(git.Pinned);

        var shorthand = DefaultPackageManager.ParseSource("github.com/user/repo");
        Assert.Equal("local", shorthand.Type);
    }

    [Fact]
    public void AddSourceToSettings_NormalizesAndUpdatesSameGitIdentity()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var manager = new DefaultPackageManager(settings, cwd, agentDir);
        var pkgDir = NewPackage("pkg");

        Assert.True(manager.AddSourceToSettings(pkgDir));
        var stored = Assert.Single(settings.GetGlobalSettings().Packages!);
        Assert.Equal(Path.GetRelativePath(agentDir, pkgDir), stored!.GetValue<string>());

        Assert.True(manager.AddSourceToSettings("https://github.com/user/repo.git"));
        Assert.False(manager.AddSourceToSettings("https://github.com/user/repo.git"));
        Assert.True(manager.AddSourceToSettings("https://github.com/user/repo.git#v2"));
        var packages = settings.GetGlobalSettings().Packages!;
        Assert.Equal(2, packages.Count);
        Assert.Equal("https://github.com/user/repo.git#v2", packages[1]!.GetValue<string>());
    }

    [Fact]
    public void AddSourceToSettings_StoresARelativePathWithoutADotSlashPrefix()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var manager = new DefaultPackageManager(settings, cwd, agentDir);
        var pkgDir = Path.Combine(agentDir, "vendor", "pkg");
        Directory.CreateDirectory(pkgDir);

        Assert.True(manager.AddSourceToSettings(pkgDir));
        var stored = Assert.Single(settings.GetGlobalSettings().Packages!);

        // TS returns `relative(baseDir, resolved) || "."`, i.e. a plain relative path — never "./x".
        Assert.Equal(Path.Combine("vendor", "pkg"), stored!.GetValue<string>());
    }

    [Fact]
    public void AddSourceToSettings_PreservesFiltersWhenUpdatingObjectEntry()
    {
        var (cwd, agentDir, settings) = NewEnv();
        settings.SetPackages(
        [
            new JsonObject
            {
                ["source"] = "https://github.com/user/repo.git",
                ["skills"] = new JsonArray("review"),
            },
        ]);
        var manager = new DefaultPackageManager(settings, cwd, agentDir);
        Assert.True(manager.AddSourceToSettings("https://github.com/user/repo.git#v2"));
        var obj = Assert.IsType<JsonObject>(settings.GetGlobalSettings().Packages![0]);
        Assert.Equal("https://github.com/user/repo.git#v2", obj["source"]!.GetValue<string>());
        Assert.Equal("review", obj["skills"]!.AsArray()[0]!.GetValue<string>());
    }

    // ========================================================================
    // 安装路径
    // ========================================================================

    [Fact]
    public void InstallPaths_MatchTheTsLayout()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var manager = new DefaultPackageManager(settings, cwd, agentDir);

        var npm = DefaultPackageManager.ParseSource("npm:@scope/pkg@1.0.0");
        Assert.True(PathsEqual(Path.Combine(agentDir, "npm", "node_modules", "@scope/pkg"), manager.GetNpmInstallPath(npm, "user")));
        Assert.True(PathsEqual(Path.Combine(agentDir, "npm"), manager.GetNpmInstallRoot("user", temporary: false)));

        var git = DefaultPackageManager.ParseSource("https://github.com/acme/skills.git");
        Assert.True(PathsEqual(
            Path.Combine(agentDir, "git", "github.com", "acme", "skills"),
            manager.GetGitInstallPath(git, "user")));
    }

    [Fact]
    public void GetNpmInstallPath_TemporaryUsesTheManagedTempRoot()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var manager = new DefaultPackageManager(settings, cwd, agentDir);
        var parsed = DefaultPackageManager.ParseSource("npm:bar@1.0.0");

        // TS: resolveManagedPath(getExtensionTempFolder(agentDir), "npm") + an 8-hex sha256 slice.
        var path = manager.GetNpmInstallPath(parsed, "temporary");
        var tempRoot = Path.Combine(agentDir, "tmp", "extensions", "npm");
        Assert.StartsWith(tempRoot, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("node_modules", "bar"), path, StringComparison.OrdinalIgnoreCase);

        var segments = Path.GetRelativePath(tempRoot, path).Split(Path.DirectorySeparatorChar);
        Assert.Equal(3, segments.Length);
        Assert.Equal(8, segments[0].Length);
    }

    [Fact]
    public async Task InstallAndPersist_LocalAddsRelativeSource()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var pkgDir = NewPackage("local-pkg");
        var manager = new DefaultPackageManager(settings, cwd, agentDir, commandRunner: new RecordingRunner());
        var installed = await manager.InstallAndPersistAsync(pkgDir);
        Assert.True(Directory.Exists(installed));
        Assert.Single(settings.GetGlobalSettings().Packages!);
    }

    [Fact]
    public async Task InstallAsync_NpmUsesInjectedRunner()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var runner = new RecordingRunner();
        var manager = new DefaultPackageManager(settings, cwd, agentDir, commandRunner: runner);
        var path = await manager.InstallAsync("npm:left-pad@1.3.0");
        Assert.Contains(Path.Combine("npm", "node_modules"), path);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("npm", call.Command);
        Assert.Contains("install", call.Arguments);
        Assert.Contains("left-pad@1.3.0", call.Arguments);
        Assert.Contains("--legacy-peer-deps", call.Arguments);
    }

    [Fact]
    public async Task InstallAsync_GitClonesViaRunner()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var runner = new RecordingRunner();
        var manager = new DefaultPackageManager(settings, cwd, agentDir, commandRunner: runner);
        var path = await manager.InstallAsync("https://github.com/acme/skills.git");
        Assert.Contains(Path.Combine("git", "github.com", "acme", "skills"), path);
        Assert.Equal("git", runner.Calls[0].Command);
        Assert.Equal("clone", runner.Calls[0].Arguments[0]);
    }

    // ========================================================================
    // 夹具
    // ========================================================================

    private string NewPackage(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string root, string relativePath, string content = "")
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed class RecordingRunner : IPackageCommandRunner
    {
        public List<(string Command, IReadOnlyList<string> Arguments, string? Cwd)> Calls { get; } = [];

        public Task<string> RunAsync(
            string command,
            IReadOnlyList<string> arguments,
            PackageProcessOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((command, [.. arguments], options?.WorkingDirectory));
            return Task.FromResult(string.Empty);
        }
    }
}
