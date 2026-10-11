using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class PackageManagerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-pkg-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);

    public PackageManagerTests() => Directory.CreateDirectory(_root);

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
    }

    private (string Cwd, string AgentDir, SettingsManager Settings) NewEnv()
    {
        var cwd = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]);
        var agentDir = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agentDir);
        Directory.CreateDirectory(cwd);
        var settings = SettingsManager.Create(cwd, agentDir);
        return (cwd, agentDir, settings);
    }

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
        Assert.Contains(result.Extensions, resource => PathsEqual(resource.Path, extPath) && resource.Enabled);
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
    public async Task Resolve_ExcludesBuiltinExtensionsWhenPatternMatches()
    {
        var (cwd, agentDir, settings) = NewEnv();
        settings.SetExclude("extensions", ["mcp"]);
        var result = await new DefaultPackageManager(settings, cwd, agentDir, ["mcp.ts"]).ResolveAsync();
        var mcp = Assert.Single(result.Extensions);
        Assert.False(mcp.Enabled);
        Assert.StartsWith(SourceInfos.BuiltinPathPrefix, mcp.Metadata.Source);
    }

    [Fact]
    public async Task Resolve_AutoloadFalseDisablesTopLevelResources()
    {
        var (cwd, agentDir, settings) = NewEnv();
        var skillsDir = Path.Combine(agentDir, "skills");
        Directory.CreateDirectory(skillsDir);
        File.WriteAllText(Path.Combine(skillsDir, "summarize.md"), "---\nname: summarize\n---\n");
        settings.SetAutoload("skills", ["false"]);
        var result = await new DefaultPackageManager(settings, cwd, agentDir).ResolveAsync();
        Assert.Contains(result.Skills, resource => !resource.Enabled && resource.Metadata.Origin == "top-level");
    }

    [Fact]
    public void ParseSource_NpmScopedAndGit()
    {
        var npm = DefaultPackageManager.ParseSource("@scope/pkg@1.2.3");
        Assert.Equal("npm", npm.Kind);
        Assert.Equal("@scope/pkg", npm.Locator);
        Assert.Equal("1.2.3", npm.Version);

        var git = DefaultPackageManager.ParseSource("https://github.com/acme/pkg.git#main");
        Assert.Equal("git", git.Kind);
        Assert.Contains("github.com/acme/pkg", git.Locator.Replace('\\', '/'));
    }

    [Fact]
    public void InstallPaths_AreStable()
    {
        Assert.Contains("npm", DefaultPackageManager.GetNpmInstallPath("/pkg", "@scope/pkg", "1.0.0"));
        Assert.Contains("git", DefaultPackageManager.GetGitInstallPath("/pkg", "pkg", "abc123"));
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
