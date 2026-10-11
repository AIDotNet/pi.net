using System.Text.Json.Nodes;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class ResourceLoaderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-res-" + Guid.NewGuid().ToString("N")[..8]);

    public ResourceLoaderTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void LoadProjectContextFiles_PrefersAgentsMdOverClaude()
    {
        var cwd = Path.Combine(_root, "proj");
        var agentDir = Path.Combine(_root, "agent");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(cwd, "AGENTS.md"), "project");
        File.WriteAllText(Path.Combine(cwd, "CLAUDE.md"), "claude");
        File.WriteAllText(Path.Combine(agentDir, "AGENTS.md"), "global");
        var files = DefaultResourceLoader.LoadProjectContextFiles(cwd, agentDir);
        Assert.Equal(2, files.Count);
        Assert.Contains("global", files[0].Content);
        Assert.Contains("project", files[1].Content);
        Assert.DoesNotContain(files, file => file.Content.Contains("claude"));
    }

    [Fact]
    public void LoadThemes_LastPathWinsWithCollision()
    {
        var first = Path.Combine(_root, "a.json");
        var second = Path.Combine(_root, "b.json");
        File.WriteAllText(first, """{"name":"dark","fg":"1"}""");
        File.WriteAllText(second, """{"name":"dark","fg":"2"}""");
        var (themes, diagnostics) = DefaultResourceLoader.LoadThemes([first, second]);
        var theme = Assert.Single(themes);
        Assert.Equal(second, theme.Path);
        Assert.Equal("2", theme.Document["fg"]?.GetValue<string>());
        Assert.Single(diagnostics);
        Assert.Equal(ResourceDiagnosticType.Collision, diagnostics[0].Type);
    }

    [Fact]
    public async Task ReloadAsync_LoadsAdditionalThemeWithoutExtensions()
    {
        var cwd = Path.Combine(_root, "cwd");
        var agentDir = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agentDir);
        Directory.CreateDirectory(cwd);
        var themePath = Path.Combine(_root, "theme.json");
        File.WriteAllText(themePath, """{"name":"solar"}""");
        var settings = SettingsManager.Create(cwd, agentDir);
        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            settings,
            noSkills: true,
            noPromptTemplates: true,
            additionalThemePaths: [themePath]);
        await loader.ReloadAsync();
        var theme = Assert.Single(loader.Themes);
        Assert.Equal("solar", theme.Name);
    }
}
