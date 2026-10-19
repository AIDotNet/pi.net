using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

// `Pi.CodingAgent.Core` also declares ToolDefinition-shaped names; inside the test namespace both
// usings are in scope, so alias the extension-contract type explicitly.
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Tests;

public sealed class ResourceLoaderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-res-" + Guid.NewGuid().ToString("N")[..8]);

    public ResourceLoaderTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// The skills the loader discovered under this test's own temp root. The package manager also
    /// auto-discovers skills from the *real* <c>~/.agents/skills</c> (package-manager.ts:2458 and
    /// 2550-2561 — both the unconditional user branch and, because `projectTrusted` defaults to
    /// `true`, the ancestor walk up from `cwd`), exactly like TS. A developer machine that has that
    /// directory populated would otherwise contribute dozens of unrelated skills to every
    /// <c>Skills</c> assertion here, so the assertions below scope themselves to the fixture.
    /// </summary>
    private IEnumerable<Skill> OwnSkills(DefaultResourceLoader loader) =>
        loader.Skills.Where(skill => skill.FilePath.StartsWith(_root, StringComparison.Ordinal));

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
    public async Task ReloadAsync_ResolvesResourcesFromAdditionalExtensionPackage()
    {
        var (cwd, agentDir) = NewProject("cli-package-resources");
        var packageRoot = Path.Combine(_root, "cli-package-resources");
        var skillPath = Path.Combine(packageRoot, "skills", "review", "SKILL.md");
        var promptPath = Path.Combine(packageRoot, "prompts", "research.md");
        var themePath = Path.Combine(packageRoot, "themes", "solar.json");
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(promptPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(themePath)!);
        File.WriteAllText(Path.Combine(packageRoot, "package.json"), """{"name":"cli-resources"}""");
        File.WriteAllText(skillPath, "---\nname: review\ndescription: Review source code\n---\nReview source code.");
        File.WriteAllText(promptPath, "---\ndescription: research\n---\nResearch the topic.");
        File.WriteAllText(themePath, """{"name":"solar"}""");

        var settings = SettingsManager.Create(cwd, agentDir);
        var cliResolved = await new DefaultPackageManager(settings, cwd, agentDir)
            .ResolveExtensionSourcesAsync([packageRoot], temporary: true);
        Assert.Contains(cliResolved.Skills, resource => resource.Path == skillPath);

        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            settings,
            noExtensions: true,
            additionalExtensionPaths: [packageRoot]);

        await loader.ReloadAsync();

        Assert.Contains(OwnSkills(loader), skill => skill.FilePath == skillPath);
        Assert.Contains(loader.Prompts, prompt => prompt.FilePath == promptPath);
        Assert.Contains(loader.Themes, theme => theme.Path == themePath);
    }

    [Fact]
    public async Task ReloadAsync_LoadsCliBuiltinWhenDefaultExtensionsAreDisabled()
    {
        var (cwd, agentDir) = NewProject("cli-builtin");
        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            SettingsManager.Create(cwd, agentDir),
            noExtensions: true,
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true,
            additionalExtensionPaths: ["builtin:demo"],
            extensionFactories: [new InlineExtension.Described("demo", DemoFactory(), Builtin: true)]);

        await loader.ReloadAsync();

        Assert.Equal("builtin:demo", Assert.Single(loader.GetExtensions().Extensions).Path);
    }

    [Fact]
    public async Task ReloadAsync_ReportsMissingAdditionalExtensionPaths()
    {
        var (cwd, agentDir) = NewProject("missing-cli-extension");
        var missingPath = Path.Combine(_root, "missing-extension");
        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            SettingsManager.Create(cwd, agentDir),
            noExtensions: true,
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true,
            additionalExtensionPaths: [missingPath]);

        await loader.ReloadAsync();

        var loadError = Assert.Single(loader.GetExtensions().Errors);
        Assert.Equal(missingPath, loadError.Path);
        Assert.Equal($"Extension path does not exist: {missingPath}", loadError.Error);
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
        Assert.Empty(OwnSkills(loader));

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

        var skill = Assert.Single(OwnSkills(loader));
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
        Assert.Equal("early", Assert.Single(OwnSkills(loader)).Name);

        var lateEntry = new ExtensionResourcePath(
            Path.Combine(addedLater, "SKILL.md"),
            SourceInfos.CreateSynthetic(Path.Combine(addedLater, "SKILL.md"), "extension:test"));

        // Calling extendResources twice with the same path must not duplicate the skill: `mergePaths`
        // canonicalises before merging, and the merged set replaces `lastSkillPaths`.
        loader.ExtendResources(new ResourceExtensionPaths { SkillPaths = [lateEntry] });
        loader.ExtendResources(new ResourceExtensionPaths { SkillPaths = [lateEntry] });

        var own = OwnSkills(loader).ToList();
        Assert.Equal(2, own.Count);
        Assert.Contains(own, skill => skill.Name == "early");
        Assert.Contains(own, skill => skill.Name == "late");
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

        Assert.Equal("default-one", Assert.Single(OwnSkills(loader)).Name);
        Assert.DoesNotContain(loader.Diagnostics, diagnostic => diagnostic.Type == ResourceDiagnosticType.Collision);
    }

    // ------------------------------------------------------------------ extension loading (9h-2b-2)

    private static ExtensionFactory DemoFactory(Action<IExtensionApi>? configure = null) => pi =>
    {
        configure?.Invoke(pi);
        return Task.CompletedTask;
    };

    private (string Cwd, string AgentDir) NewProject(string name)
    {
        var cwd = Path.Combine(_root, "projects", name);
        var agentDir = Path.Combine(cwd, "agent");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(agentDir);
        return (cwd, agentDir);
    }

    [Fact]
    public async Task ReloadAsync_LoadsBuiltinExtensionFromTheRegistry()
    {
        // A built-in factory is registered as `builtin:<name>` by the package manager and loaded by
        // the resource loader; it is hidden and never also loaded as an inline extension.
        var (cwd, agentDir) = NewProject("builtin");
        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            SettingsManager.Create(cwd, agentDir),
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true,
            extensionFactories:
            [
                new InlineExtension.Described("demo", DemoFactory(), Builtin: true),
            ]);

        await loader.ReloadAsync();

        var extension = Assert.Single(loader.GetExtensions().Extensions);
        Assert.Equal("builtin:demo", extension.Path);
        Assert.True(extension.Hidden);
        Assert.False(extension.Replaceable);
        Assert.Equal("builtin", extension.SourceInfo.Source);
    }

    [Fact]
    public async Task ReloadAsync_DisabledBuiltinExtensionsAreNotLoaded()
    {
        var (cwd, agentDir) = NewProject("builtin-disabled");
        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            SettingsManager.Create(cwd, agentDir),
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true,
            disabledBuiltinExtensions: ["demo"],
            extensionFactories:
            [
                new InlineExtension.Described("demo", DemoFactory(), Builtin: true),
            ]);

        await loader.ReloadAsync();

        Assert.Empty(loader.GetExtensions().Extensions);
    }

    [Fact]
    public async Task ReloadAsync_LoadsInlineExtensionFactoryAsAnInlinePath()
    {
        var (cwd, agentDir) = NewProject("inline");
        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            SettingsManager.Create(cwd, agentDir),
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true,
            extensionFactories:
            [
                new InlineExtension.Described("third-party", DemoFactory(), Hidden: true),
            ]);

        await loader.ReloadAsync();

        var extension = Assert.Single(loader.GetExtensions().Extensions);
        Assert.Equal("<inline:third-party>", extension.Path);
        Assert.True(extension.Hidden);
    }

    [Fact]
    public async Task ReloadAsync_FailingExtensionFactoryBecomesALoadError()
    {
        var (cwd, agentDir) = NewProject("inline-fail");
        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            SettingsManager.Create(cwd, agentDir),
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true,
            extensionFactories:
            [
                new InlineExtension.Described(
                    "broken",
                    _ => throw new InvalidOperationException("factory exploded")),
            ]);

        await loader.ReloadAsync();

        var result = loader.GetExtensions();
        Assert.Empty(result.Extensions);
        var error = Assert.Single(result.Errors);
        Assert.Equal("<inline:broken>", error.Path);
        Assert.Equal("factory exploded", error.Error);
    }

    [Fact]
    public async Task ReloadAsync_ExtensionsOverrideCanReplaceTheLoadedSet()
    {
        var (cwd, agentDir) = NewProject("override");
        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            SettingsManager.Create(cwd, agentDir),
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true,
            extensionsOverride: baseResult => baseResult with
            {
                Errors = [.. baseResult.Errors, new ExtensionLoadError("<override>", "added by override")],
            });

        await loader.ReloadAsync();

        Assert.Equal("added by override", Assert.Single(loader.GetExtensions().Errors).Error);
    }

    [Fact]
    public async Task LoadProjectTrustExtensionsAsync_ForcesUntrustedAndLoadsInlineFactories()
    {
        var (cwd, agentDir) = NewProject("trust");
        var settings = SettingsManager.Create(cwd, agentDir);
        settings.SetProjectTrusted(true);
        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            settings,
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true,
            extensionFactories:
            [
                new InlineExtension.Described("inline-one", DemoFactory()),
            ]);

        var result = await loader.LoadProjectTrustExtensionsAsync();

        // The bootstrap pass keeps user/global and temporary (inline) extensions but forces the
        // project untrusted so project-local settings cannot contribute extensions yet.
        Assert.False(settings.IsProjectTrusted());
        Assert.Equal("<inline:inline-one>", Assert.Single(result.Extensions).Path);
    }

    [Fact]
    public async Task LoadProjectTrustExtensionsAsync_CollectsCliPackageDependencyWarnings()
    {
        var (cwd, agentDir) = NewProject("cli-package-trust");
        var packageRoot = Path.Combine(_root, "cli-package-trust");
        var extensionPath = Path.Combine(packageRoot, "extensions", "entry.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(extensionPath)!);
        File.WriteAllText(
            Path.Combine(packageRoot, "package.json"),
            """{"name":"cli-package","dependencies":{"@earendil-works/pi-ai":"*"}}""");
        File.WriteAllText(extensionPath, "export default function(pi) {}");
        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            SettingsManager.Create(cwd, agentDir),
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true,
            additionalExtensionPaths: [packageRoot]);

        var result = await loader.LoadProjectTrustExtensionsAsync();

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(Path.Combine(packageRoot, "package.json"), warning.Path);
        Assert.Contains("@earendil-works/pi-ai", warning.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectExtensionConflicts_ReportsToolAndFlagClashes()
    {
        var first = NewExtensionFor("a.ts", toolNames: ["read"], flagNames: ["verbose"]);
        var second = NewExtensionFor("b.ts", toolNames: ["read"], flagNames: ["verbose"]);
        var third = NewExtensionFor("c.ts", toolNames: ["write"]);

        var conflicts = DefaultResourceLoader.DetectExtensionConflicts([first, second, third]);

        Assert.Equal(2, conflicts.Count);
        Assert.Contains(conflicts, c => c.Message == "Tool \"read\" conflicts with a.ts");
        Assert.Contains(conflicts, c => c.Message == "Flag \"--verbose\" conflicts with a.ts");
    }

    [Fact]
    public void DetectExtensionConflicts_SameExtensionReRegisteringIsNotAConflict()
    {
        var extension = NewExtensionFor("a.ts", toolNames: ["read"], flagNames: ["verbose"]);
        Assert.Empty(DefaultResourceLoader.DetectExtensionConflicts([extension]));
    }

    [Fact]
    public void OmitReplacedExtensions_DropsReplaceableBuiltinAndWarns()
    {
        // A third-party extension registering /mcp replaces the built-in `mcp` extension rather than
        // both connecting the same servers.
        var builtin = NewExtensionFor("builtin:mcp", commandNames: ["mcp"], replaceable: true);
        var thirdParty = NewExtensionFor("/tmp/ext.ts", commandNames: ["mcp"]);
        var unrelated = NewExtensionFor("builtin:tool-search", commandNames: ["search"], replaceable: true);

        var warnings = new List<ExtensionLoadWarning>();
        var kept = ResourceLoaderExtensions.OmitReplacedExtensions([builtin, thirdParty, unrelated], warnings);

        Assert.DoesNotContain(kept, extension => extension.Path == "builtin:mcp");
        Assert.Contains(kept, extension => extension.Path == "/tmp/ext.ts");
        Assert.Contains(kept, extension => extension.Path == "builtin:tool-search");

        var warning = Assert.Single(warnings);
        Assert.Equal("builtin:mcp", warning.Path);
        Assert.Contains("registers command `/mcp`", warning.Warning, StringComparison.Ordinal);
        Assert.Contains("built-in extension `mcp` was not loaded", warning.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void OmitReplacedExtensions_KeepsReplaceableWhenNothingClaimsItsNames()
    {
        var builtin = NewExtensionFor("builtin:mcp", commandNames: ["mcp"], replaceable: true);
        var warnings = new List<ExtensionLoadWarning>();

        var kept = ResourceLoaderExtensions.OmitReplacedExtensions([builtin], warnings);

        Assert.Single(kept);
        Assert.Empty(warnings);
    }

    [Fact]
    public void MergeWarnings_DedupesByPathWithLaterEntryWinning()
    {
        var merged = ResourceLoaderExtensions.MergeWarnings(
            [new ExtensionLoadWarning("a.ts", "first")],
            [new ExtensionLoadWarning("a.ts", "second"), new ExtensionLoadWarning("b.ts", "other")]);

        Assert.Equal(2, merged.Count);
        Assert.Equal("second", merged.Single(w => w.Path == "a.ts").Warning);
    }

    [Fact]
    public void CollectExtensionPackageWarnings_DedupesRootsAndSortsHostProvidedDependencies()
    {
        var packageRoot = Path.Combine(_root, "warning-package");
        Directory.CreateDirectory(packageRoot);
        var packageJsonPath = Path.Combine(packageRoot, "package.json");
        File.WriteAllText(
            packageJsonPath,
            """{"dependencies":{"typebox":"1.0.0","other":"1.0.0","@earendil-works/pi-ai":"*"}}""");
        var firstExtension = Path.Combine(packageRoot, "extensions", "first.ts");
        var secondExtension = Path.Combine(packageRoot, "extensions", "second.ts");
        var metadata = new Dictionary<string, PathMetadata>(StringComparer.Ordinal)
        {
            [firstExtension] = new PathMetadata
            {
                Source = "cli",
                Scope = "temporary",
                Origin = "package",
                PackageRoot = packageRoot,
            },
            [secondExtension] = new PathMetadata
            {
                Source = "cli",
                Scope = "temporary",
                Origin = "package",
                PackageRoot = packageRoot,
            },
        };

        var warning = Assert.Single(ResourceLoaderExtensions.CollectExtensionPackageWarnings(
            [firstExtension, secondExtension],
            metadata));

        Assert.Equal(packageJsonPath, warning.Path);
        Assert.Contains(
            "@earendil-works/pi-ai, typebox",
            warning.Warning,
            StringComparison.Ordinal);
    }

    private static Extension NewExtensionFor(
        string path,
        IReadOnlyList<string>? toolNames = null,
        IReadOnlyList<string>? commandNames = null,
        IReadOnlyList<string>? flagNames = null,
        bool replaceable = false)
    {
        var extension = new Extension
        {
            Path = path,
            ResolvedPath = path,
            SourceInfo = SourceInfos.CreateSynthetic(path, "test"),
            Replaceable = replaceable,
        };

        foreach (var name in toolNames ?? [])
        {
            extension.Tools[name] = new RegisteredTool
            {
                Definition = new ToolDefinition
                {
                    Name = name,
                    Label = name,
                    Description = name,
                    Parameters = new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }),
                    Execute = (_, _, _, _, _) => Task.FromResult(new AgentToolResult([])),
                },
                SourceInfo = extension.SourceInfo,
            };
        }

        foreach (var name in commandNames ?? [])
        {
            extension.Commands[name] = new RegisteredCommand
            {
                Name = name,
                SourceInfo = extension.SourceInfo,
                Handler = (_, _) => Task.CompletedTask,
            };
        }

        foreach (var name in flagNames ?? [])
        {
            extension.Flags[name] = new ExtensionFlag
            {
                Name = name,
                Type = FlagType.Boolean,
                ExtensionPath = path,
            };
        }

        return extension;
    }
}
