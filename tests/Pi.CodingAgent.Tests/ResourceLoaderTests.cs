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
    public void DedupeThemes_FirstPathWins()
    {
        // TS `dedupeThemes` keeps the *first* theme with a given name and reports the later ones as
        // collisions. The port previously deduped inside `LoadThemes` with last-wins semantics, which
        // inverted precedence (difference C143). `LoadThemes` now only collects; dedupe is separate.
        var first = Path.Combine(_root, "a.json");
        var second = Path.Combine(_root, "b.json");
        File.WriteAllText(first, """{"name":"dark","fg":"1"}""");
        File.WriteAllText(second, """{"name":"dark","fg":"2"}""");

        var loaded = DefaultResourceLoader.LoadThemes([first, second]);
        Assert.Equal(2, loaded.Themes.Count);
        Assert.Empty(loaded.Diagnostics);

        var (themes, diagnostics) = DefaultResourceLoader.DedupeThemes(loaded.Themes);
        var theme = Assert.Single(themes);
        Assert.Equal(first, theme.Path);
        Assert.Equal("1", theme.Document["fg"]?.GetValue<string>());
        var collision = Assert.Single(diagnostics);
        Assert.Equal(ResourceDiagnosticType.Collision, collision.Type);
        Assert.Equal(first, collision.Collision!.WinnerPath);
        Assert.Equal(second, collision.Collision.LoserPath);
    }

    [Fact]
    public void DedupePrompts_FirstPathWins()
    {
        // The prompt name comes from the file stem, so two same-named templates need two directories.
        var dirA = Path.Combine(_root, "pa");
        var dirB = Path.Combine(_root, "pb");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var first = Path.Combine(dirA, "same.md");
        var second = Path.Combine(dirB, "same.md");
        File.WriteAllText(first, "---\ndescription: a\n---\nfirst");
        File.WriteAllText(second, "---\ndescription: b\n---\nsecond");

        var loaded = PromptTemplates.Load(new LoadPromptTemplatesOptions
        {
            Cwd = _root,
            AgentDir = _root,
            PromptPaths = [first, second],
            IncludeDefaults = false,
        });
        Assert.Equal(2, loaded.Templates.Count);

        var (prompts, diagnostics) = DefaultResourceLoader.DedupePrompts(loaded.Templates);
        Assert.Equal(first, Assert.Single(prompts).FilePath);
        var collision = Assert.Single(diagnostics);
        Assert.Equal(ResourceDiagnosticType.Collision, collision.Type);
        Assert.Equal(ResourceType.Prompt, collision.Collision!.ResourceType);
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

    [Fact]
    public async Task GetExtensions_IsEmptyBeforeTheExtensionLoaderLands()
    {
        // Mirrors the TS constructor, which seeds `extensionsResult` with an empty set plus a live
        // runtime. The loader itself is 4e-3e-9h-2b-2.
        var cwd = Path.Combine(_root, "cwd2");
        var agentDir = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agentDir);
        var loader = new DefaultResourceLoader(cwd, agentDir, SettingsManager.Create(cwd, agentDir));
        await loader.ReloadAsync();

        var result = loader.GetExtensions();
        Assert.Empty(result.Extensions);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Runtime);
    }

    [Fact]
    public async Task ExtendResources_AddsSkillWithExtensionProvenance()
    {
        var cwd = Path.Combine(_root, "cwd3");
        var agentDir = Path.Combine(cwd, "agent");
        var skillDir = Path.Combine(_root, "ext-skills", "demo");
        Directory.CreateDirectory(agentDir);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: demo\ndescription: an extension-provided skill\n---\nbody");

        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            SettingsManager.Create(cwd, agentDir),
            noPromptTemplates: true,
            noThemes: true);
        await loader.ReloadAsync();
        Assert.Empty(loader.Skills);

        loader.ExtendResources(new ResourceExtensionPaths
        {
            SkillPaths =
            [
                new ExtensionResourcePath(
                    Path.Combine(skillDir, "SKILL.md"),
                    SourceInfos.CreateSynthetic(
                        Path.Combine(skillDir, "SKILL.md"),
                        "extension:<inline:mcp>",
                        SourceScope.Temporary)),
            ],
        });

        var skill = Assert.Single(loader.Skills);
        Assert.Equal("demo", skill.Name);
        // The extension-declared source info wins over the reload metadata lookup.
        Assert.Equal("extension:<inline:mcp>", skill.SourceInfo.Source);
        Assert.Equal(SourceScope.Temporary, skill.SourceInfo.Scope);
    }

    [Fact]
    public async Task ExtendResources_MergesIntoTheLastLoadedSetInsteadOfReplacingIt()
    {
        var cwd = Path.Combine(_root, "cwd4");
        var agentDir = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(agentDir);
        var atStartup = Path.Combine(_root, "startup-skills", "early");
        var addedLater = Path.Combine(_root, "late-skills", "late");
        Directory.CreateDirectory(atStartup);
        Directory.CreateDirectory(addedLater);
        File.WriteAllText(
            Path.Combine(atStartup, "SKILL.md"),
            "---\nname: early\ndescription: present at startup\n---\nbody");
        File.WriteAllText(
            Path.Combine(addedLater, "SKILL.md"),
            "---\nname: late\ndescription: added by an extension\n---\nbody");

        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            SettingsManager.Create(cwd, agentDir),
            noPromptTemplates: true,
            noThemes: true,
            additionalSkillPaths: [Path.Combine(_root, "startup-skills")]);
        await loader.ReloadAsync();
        Assert.Equal("early", Assert.Single(loader.Skills).Name);

        var lateEntry = new ExtensionResourcePath(
            Path.Combine(addedLater, "SKILL.md"),
            SourceInfos.CreateSynthetic(Path.Combine(addedLater, "SKILL.md"), "extension:test"));

        // Calling extendResources twice with the same path must not duplicate the skill: `mergePaths`
        // canonicalises before merging, and the merged set replaces `lastSkillPaths`.
        loader.ExtendResources(new ResourceExtensionPaths { SkillPaths = [lateEntry] });
        loader.ExtendResources(new ResourceExtensionPaths { SkillPaths = [lateEntry] });

        Assert.Equal(2, loader.Skills.Count);
        Assert.Contains(loader.Skills, skill => skill.Name == "early");
        Assert.Contains(loader.Skills, skill => skill.Name == "late");
    }

    [Fact]
    public async Task ReloadAsync_DoesNotDoubleLoadDefaultSkills()
    {
        // Regression guard for the `includeDefaults` divergence: the path list already carries the
        // auto-discovered default directories (they come from `packageManager.resolve()`), so asking
        // the skill loader for its defaults again would load each default skill twice and emit a
        // spurious collision diagnostic for every one of them.
        var cwd = Path.Combine(_root, "cwd5");
        var agentDir = Path.Combine(cwd, "agent");
        var skillDir = Path.Combine(agentDir, "skills", "default-one");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: default-one\ndescription: an auto-discovered default skill\n---\nbody");

        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            SettingsManager.Create(cwd, agentDir),
            noPromptTemplates: true,
            noThemes: true);
        await loader.ReloadAsync();

        Assert.Equal("default-one", Assert.Single(loader.Skills).Name);
        Assert.DoesNotContain(loader.Diagnostics, diagnostic => diagnostic.Type == ResourceDiagnosticType.Collision);
    }
}
