using System.Text.Json.Nodes;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>4f 之前的最小 theme 文档。对应 TS <c>Theme</c> 的 name/path 子集。</summary>
public sealed record LoadedTheme
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public required JsonObject Document { get; init; }

    /// <summary>
    /// 来源。对应 TS <c>Theme.sourceInfo</c>；由 <see cref="DefaultResourceLoader.UpdateThemesFromPaths"/>
    /// 回填。TS 的 <c>sourcePath</c> 在端口里由 <see cref="Path"/> 承担（完整 theme 顺延 4f）。
    /// </summary>
    public SourceInfo? SourceInfo { get; init; }
}

/// <summary>skills 加载结果（含诊断）。对应 TS <c>updateSkillsFromPaths</c> 的 <c>skillsOverride</c> 形参。</summary>
public sealed record SkillLoadOutcome(
    IReadOnlyList<Skill> Skills,
    IReadOnlyList<ResourceDiagnostic> Diagnostics);

/// <summary>prompt 加载结果（含诊断）。对应 TS <c>updatePromptsFromPaths</c> 的 <c>promptsOverride</c> 形参。</summary>
public sealed record PromptLoadOutcome(
    IReadOnlyList<PromptTemplate> Prompts,
    IReadOnlyList<ResourceDiagnostic> Diagnostics);

/// <summary>theme 加载结果（含诊断）。对应 TS <c>updateThemesFromPaths</c> 的 <c>themesOverride</c> 形参。</summary>
public sealed record ThemeLoadOutcome(
    IReadOnlyList<LoadedTheme> Themes,
    IReadOnlyList<ResourceDiagnostic> Diagnostics);

/// <summary>
/// 资源加载。对应 TS <c>core/resource-loader.ts</c>。
/// 4e-3d-2：项目上下文文件、skills/prompts、theme JSON；extensions runner 顺延 4e-3e，完整 theme 顺延 4f。
/// 4e-3e-9h-2b-1：补齐资源溯源（<c>sourceInfo</c>）与 <c>extendResources</c>，并把 skills/prompts/themes
/// 的装载改走 TS 的 <c>update*FromPaths</c> 通路。
/// </summary>
public sealed class DefaultResourceLoader
{
    private static readonly string[] ContextCandidates =
        ["AGENTS.override.md", "AGENTS.md", "AGENTS.MD", "CLAUDE.md", "CLAUDE.MD"];

    private readonly string _cwd;
    private readonly string _agentDir;
    private readonly SettingsManager _settings;
    private readonly DefaultPackageManager _packages;
    private readonly bool _noSkills;
    private readonly bool _noPromptTemplates;
    private readonly bool _noThemes;
    private readonly bool _noContextFiles;
    private readonly IReadOnlyList<string> _additionalSkillPaths;
    private readonly IReadOnlyList<string> _additionalPromptPaths;
    private readonly IReadOnlyList<string> _additionalThemePaths;
    private readonly Func<SkillLoadOutcome, SkillLoadOutcome>? _skillsOverride;
    private readonly Func<PromptLoadOutcome, PromptLoadOutcome>? _promptsOverride;
    private readonly Func<ThemeLoadOutcome, ThemeLoadOutcome>? _themesOverride;

    /// <summary>TS <c>resourceMetadataByPath</c>：路径 → 来源元数据，reload 期间构建。</summary>
    private Dictionary<string, PathMetadata> _resourceMetadataByPath = new(StringComparer.Ordinal);

    private Dictionary<string, SourceInfo> _extensionSkillSourceInfos = new(StringComparer.Ordinal);

    private Dictionary<string, SourceInfo> _extensionPromptSourceInfos = new(StringComparer.Ordinal);

    private Dictionary<string, SourceInfo> _extensionThemeSourceInfos = new(StringComparer.Ordinal);

    private List<string> _lastSkillPaths = [];

    private List<string> _lastPromptPaths = [];

    private List<string> _lastThemePaths = [];

    /// <summary>
    /// TS <c>extensionsResult</c>。扩展加载本体（<c>loadFinalExtensionSet</c> 等）随 4e-3e-9h-2b-2 落地，
    /// 在此之前 <see cref="GetExtensions"/> 返回构造器里那个空结果，与 TS 构造器的初值一致。
    /// </summary>
    private LoadExtensionsResult _extensionsResult =
        new([], [], [], ExtensionLoader.CreateExtensionRuntime());

    public DefaultResourceLoader(
        string cwd,
        string agentDir,
        SettingsManager settingsManager,
        bool noSkills = false,
        bool noPromptTemplates = false,
        bool noThemes = false,
        bool noContextFiles = false,
        IReadOnlyList<string>? additionalSkillPaths = null,
        IReadOnlyList<string>? additionalPromptTemplatePaths = null,
        IReadOnlyList<string>? additionalThemePaths = null,
        Func<SkillLoadOutcome, SkillLoadOutcome>? skillsOverride = null,
        Func<PromptLoadOutcome, PromptLoadOutcome>? promptsOverride = null,
        Func<ThemeLoadOutcome, ThemeLoadOutcome>? themesOverride = null)
    {
        _cwd = cwd;
        _agentDir = agentDir;
        _settings = settingsManager;
        _packages = new DefaultPackageManager(settingsManager, cwd, agentDir);
        _noSkills = noSkills;
        _noPromptTemplates = noPromptTemplates;
        _noThemes = noThemes;
        _noContextFiles = noContextFiles;
        _additionalSkillPaths = additionalSkillPaths ?? [];
        _additionalPromptPaths = additionalPromptTemplatePaths ?? [];
        _additionalThemePaths = additionalThemePaths ?? [];
        _skillsOverride = skillsOverride;
        _promptsOverride = promptsOverride;
        _themesOverride = themesOverride;
    }

    public IReadOnlyList<Skill> Skills { get; private set; } = [];

    public IReadOnlyList<PromptTemplate> Prompts { get; private set; } = [];

    public IReadOnlyList<LoadedTheme> Themes { get; private set; } = [];

    public IReadOnlyList<ContextFile> AgentsFiles { get; private set; } = [];

    public IReadOnlyList<ResourceDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>对应 TS <c>getExtensions()</c>。</summary>
    public LoadExtensionsResult GetExtensions() => _extensionsResult;

    /// <summary>对应 TS <c>loadProjectContextFiles</c>。</summary>
    public static IReadOnlyList<ContextFile> LoadProjectContextFiles(string cwd, string agentDir)
    {
        var resolvedCwd = Paths.ResolvePath(cwd);
        var resolvedAgentDir = Paths.ResolvePath(agentDir);
        var files = new List<ContextFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var global = LoadContextFileFromDir(resolvedAgentDir);
        if (global is not null)
        {
            files.Add(global);
            seen.Add(global.Path);
        }

        var shadowed = FindShadowedContextFile(resolvedCwd);
        var ancestors = new List<ContextFile>();
        var current = resolvedCwd;
        while (true)
        {
            var context = LoadContextFileFromDir(current);
            var isShadowed = shadowed is not null
                && context is not null
                && string.Equals(Paths.CanonicalizePath(context.Path), shadowed, StringComparison.Ordinal);
            if (context is not null && !isShadowed && seen.Add(context.Path))
            {
                ancestors.Insert(0, context);
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                break;
            }

            current = parent;
        }

        files.AddRange(ancestors);
        return files;
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<ResourceDiagnostic>();

        // TS: `await this.settingsManager.reload()`. The trust state is preserved by SettingsManager
        // itself, so the pre-trust bootstrap pass (extensions, 9h-2b-2) can simply reload again.
        await _settings.ReloadAsync().ConfigureAwait(false);

        var resolved = await _packages.ResolveAsync(cancellationToken).ConfigureAwait(false);

        // TS rebuilds the provenance tables on every reload; post-reload passes (`extendResources`)
        // read them back, so they must stay on the instance.
        _resourceMetadataByPath = new Dictionary<string, PathMetadata>(StringComparer.Ordinal);
        var metadataByPath = _resourceMetadataByPath;
        _extensionSkillSourceInfos = new Dictionary<string, SourceInfo>(StringComparer.Ordinal);
        _extensionPromptSourceInfos = new Dictionary<string, SourceInfo>(StringComparer.Ordinal);
        _extensionThemeSourceInfos = new Dictionary<string, SourceInfo>(StringComparer.Ordinal);

        IReadOnlyList<ResolvedResource> GetEnabledResources(IReadOnlyList<ResolvedResource> resources)
        {
            foreach (var resource in resources)
            {
                metadataByPath.TryAdd(resource.Path, resource.Metadata);
            }

            return resources.Where(resource => resource.Enabled).ToArray();
        }

        IReadOnlyList<string> GetEnabledPaths(IReadOnlyList<ResolvedResource> resources) =>
            GetEnabledResources(resources).Select(resource => resource.Path).ToArray();

        var enabledSkillResources = GetEnabledResources(resolved.Skills);
        var enabledPrompts = GetEnabledPaths(resolved.Prompts);
        var enabledThemes = GetEnabledPaths(resolved.Themes);

        // Auto-discovered skill entries may name a skill *directory*; `MapSkillPath` resolves those to
        // their `SKILL.md` so the loader sees a file, and records the file's metadata on the way.
        var enabledSkills = enabledSkillResources
            .Select(resource => MapSkillPath(resource, metadataByPath))
            .ToArray();

        // CLI extension sources (`packageManager.resolveExtensionSources(additionalExtensionPaths, …)`)
        // arrive with 4e-3e-9h-2b-2, which is also when `additionalExtensionPaths` enters the ctor.
        string[] cliEnabledSkills = [];
        string[] cliEnabledPrompts = [];
        string[] cliEnabledThemes = [];

        var skillPaths = _noSkills
            ? MergePaths(cliEnabledSkills, _additionalSkillPaths)
            : MergePaths([.. cliEnabledSkills, .. enabledSkills], _additionalSkillPaths);

        _lastSkillPaths = [.. skillPaths];
        UpdateSkillsFromPaths(skillPaths, metadataByPath);
        diagnostics.AddRange(_skillDiagnostics);

        var promptPaths = _noPromptTemplates
            ? MergePaths(cliEnabledPrompts, _additionalPromptPaths)
            : MergePaths([.. cliEnabledPrompts, .. enabledPrompts], _additionalPromptPaths);

        _lastPromptPaths = [.. promptPaths];
        UpdatePromptsFromPaths(promptPaths, metadataByPath);
        diagnostics.AddRange(_promptDiagnostics);

        var themePaths = _noThemes
            ? MergePaths(cliEnabledThemes, _additionalThemePaths)
            : MergePaths([.. cliEnabledThemes, .. enabledThemes], _additionalThemePaths);

        _lastThemePaths = [.. themePaths];
        UpdateThemesFromPaths(themePaths, metadataByPath);
        diagnostics.AddRange(_themeDiagnostics);

        // TS applies extension provenance right after the extension set is finalised. The set is empty
        // until 9h-2b-2 lands the loader, so this is a no-op that keeps the ordering visible.
        ApplyExtensionSourceInfo(_extensionsResult.Extensions, metadataByPath);

        AgentsFiles = _noContextFiles ? [] : LoadProjectContextFiles(_cwd, _agentDir);
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Register extra resource paths discovered by extensions. Port of TS <c>extendResources</c>; the
    /// paths are merged into the last loaded set rather than replacing it, so an extension can add to
    /// the startup set without the loader having to re-resolve packages.
    /// </summary>
    public void ExtendResources(ResourceExtensionPaths paths)
    {
        var skillPaths = NormalizeExtensionPaths(paths.SkillPaths);
        var promptPaths = NormalizeExtensionPaths(paths.PromptPaths);
        var themePaths = NormalizeExtensionPaths(paths.ThemePaths);

        foreach (var entry in skillPaths)
        {
            _extensionSkillSourceInfos[entry.Path] = entry.Metadata;
        }

        foreach (var entry in promptPaths)
        {
            _extensionPromptSourceInfos[entry.Path] = entry.Metadata;
        }

        foreach (var entry in themePaths)
        {
            _extensionThemeSourceInfos[entry.Path] = entry.Metadata;
        }

        if (skillPaths.Count > 0)
        {
            _lastSkillPaths = [.. MergePaths(_lastSkillPaths, [.. skillPaths.Select(entry => entry.Path)])];
            UpdateSkillsFromPaths(_lastSkillPaths, _resourceMetadataByPath);
        }

        if (promptPaths.Count > 0)
        {
            _lastPromptPaths = [.. MergePaths(_lastPromptPaths, [.. promptPaths.Select(entry => entry.Path)])];
            UpdatePromptsFromPaths(_lastPromptPaths, _resourceMetadataByPath);
        }

        if (themePaths.Count > 0)
        {
            _lastThemePaths = [.. MergePaths(_lastThemePaths, [.. themePaths.Select(entry => entry.Path)])];
            UpdateThemesFromPaths(_lastThemePaths, _resourceMetadataByPath);
        }
    }

    internal static (IReadOnlyList<LoadedTheme> Themes, IReadOnlyList<ResourceDiagnostic> Diagnostics) LoadThemes(
        IReadOnlyList<string> paths)
    {
        // Port of TS `loadThemes` for the file case. TS also walks directories and warns about missing
        // paths; the port keeps its 4e-3d-2 stand-in (JSON files only, silently skipped when absent)
        // until the full theme port lands with 4f. Deduplication is *not* done here — TS keeps it in
        // `dedupeThemes`, which `UpdateThemesFromPaths` calls.
        var themes = new List<LoadedTheme>();
        var diagnostics = new List<ResourceDiagnostic>();
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = JsonNode.Parse(Text.StripBom(File.ReadAllText(path))) as JsonObject;
                if (json is null)
                {
                    diagnostics.Add(ResourceDiagnostic.Error($"theme is not a JSON object: {path}", path));
                    continue;
                }

                var name = json["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(path);
                themes.Add(new LoadedTheme { Name = name, Path = path, Document = json });
            }
            catch (Exception ex)
            {
                diagnostics.Add(ResourceDiagnostic.Error($"failed to load theme {path}: {ex.Message}", path));
            }
        }

        return (themes, diagnostics);
    }

    internal static ContextFile? LoadContextFileFromDir(string dir)
    {
        foreach (var name in ContextCandidates)
        {
            var filePath = Path.Combine(dir, name);
            if (!File.Exists(filePath))
            {
                continue;
            }

            try
            {
                return new ContextFile(filePath, Text.StripBom(File.ReadAllText(filePath)));
            }
            catch (IOException)
            {
            }
        }

        return null;
    }

    private static string? FindShadowedContextFile(string cwd)
    {
        var gitPaths = FooterDataProvider.FindGitPaths(cwd);
        if (gitPaths is null)
        {
            return null;
        }

        var commonGitDir = Paths.CanonicalizePath(gitPaths.CommonGitDir);
        var worktreeRoot = Paths.CanonicalizePath(gitPaths.RepoDir);
        var mainRepoRoot = Path.GetDirectoryName(commonGitDir);
        if (string.IsNullOrEmpty(mainRepoRoot))
        {
            return null;
        }

        var prefix = mainRepoRoot + Path.DirectorySeparatorChar;
        if (!worktreeRoot.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var worktreeContext = LoadContextFileFromDir(worktreeRoot);
        return worktreeContext is null ? null : Path.Combine(mainRepoRoot, Path.GetFileName(worktreeContext.Path));
    }

    // ========================================================================
    // 资源通路（TS updateSkillsFromPaths / updatePromptsFromPaths / updateThemesFromPaths）
    // ========================================================================

    private IReadOnlyList<ResourceDiagnostic> _skillDiagnostics = [];
    private IReadOnlyList<ResourceDiagnostic> _promptDiagnostics = [];
    private IReadOnlyList<ResourceDiagnostic> _themeDiagnostics = [];

    /// <summary>
    /// TS <c>updateSkillsFromPaths</c>. Note <c>includeDefaults: false</c>: by this point the path list
    /// already carries the auto-discovered default directories (they come out of
    /// <c>packageManager.resolve()</c>), so asking the skill loader for its defaults again would load
    /// every default skill twice and report a spurious collision for each.
    /// </summary>
    private void UpdateSkillsFromPaths(IReadOnlyList<string> skillPaths, Dictionary<string, PathMetadata>? metadataByPath)
    {
        SkillLoadOutcome outcome;
        if (_noSkills && skillPaths.Count == 0)
        {
            outcome = new SkillLoadOutcome([], []);
        }
        else
        {
            var loaded = Core.Skills.LoadSkills(new LoadSkillsOptions
            {
                Cwd = _cwd,
                AgentDir = _agentDir,
                SkillPaths = skillPaths,
                IncludeDefaults = false,
            });
            outcome = new SkillLoadOutcome(loaded.Skills, loaded.Diagnostics);
        }

        outcome = _skillsOverride is null ? outcome : _skillsOverride(outcome);
        Skills = outcome.Skills
            .Select(skill => skill with
            {
                SourceInfo = FindSourceInfoForPath(skill.FilePath, _extensionSkillSourceInfos, metadataByPath)
                    ?? skill.SourceInfo
                    ?? GetDefaultSourceInfoForPath(skill.FilePath),
            })
            .ToArray();
        _skillDiagnostics = outcome.Diagnostics;
    }

    /// <summary>TS <c>updatePromptsFromPaths</c>.</summary>
    private void UpdatePromptsFromPaths(IReadOnlyList<string> promptPaths, Dictionary<string, PathMetadata>? metadataByPath)
    {
        PromptLoadOutcome outcome;
        if (_noPromptTemplates && promptPaths.Count == 0)
        {
            outcome = new PromptLoadOutcome([], []);
        }
        else
        {
            var loaded = PromptTemplates.Load(new LoadPromptTemplatesOptions
            {
                Cwd = _cwd,
                AgentDir = _agentDir,
                PromptPaths = promptPaths,
                IncludeDefaults = false,
            });
            var deduped = DedupePrompts(loaded.Templates);
            outcome = new PromptLoadOutcome(deduped.Prompts, [.. loaded.Diagnostics, .. deduped.Diagnostics]);
        }

        outcome = _promptsOverride is null ? outcome : _promptsOverride(outcome);
        Prompts = outcome.Prompts
            .Select(prompt => prompt with
            {
                SourceInfo = FindSourceInfoForPath(prompt.FilePath, _extensionPromptSourceInfos, metadataByPath)
                    ?? prompt.SourceInfo
                    ?? GetDefaultSourceInfoForPath(prompt.FilePath),
            })
            .ToArray();
        _promptDiagnostics = outcome.Diagnostics;
    }

    /// <summary>TS <c>updateThemesFromPaths</c>.</summary>
    private void UpdateThemesFromPaths(IReadOnlyList<string> themePaths, Dictionary<string, PathMetadata>? metadataByPath)
    {
        ThemeLoadOutcome outcome;
        if (_noThemes && themePaths.Count == 0)
        {
            outcome = new ThemeLoadOutcome([], []);
        }
        else
        {
            var loaded = LoadThemes(themePaths);
            var deduped = DedupeThemes(loaded.Themes);
            outcome = new ThemeLoadOutcome(deduped.Themes, [.. loaded.Diagnostics, .. deduped.Diagnostics]);
        }

        outcome = _themesOverride is null ? outcome : _themesOverride(outcome);
        Themes = outcome.Themes
            .Select(theme => theme with
            {
                SourceInfo = FindSourceInfoForPath(theme.Path, _extensionThemeSourceInfos, metadataByPath)
                    ?? theme.SourceInfo
                    ?? GetDefaultSourceInfoForPath(theme.Path),
            })
            .ToArray();
        _themeDiagnostics = outcome.Diagnostics;
    }

    /// <summary>
    /// TS <c>dedupePrompts</c>: the <em>first</em> template with a given name wins; later ones are
    /// reported as collisions.
    /// </summary>
    internal static (IReadOnlyList<PromptTemplate> Prompts, IReadOnlyList<ResourceDiagnostic> Diagnostics)
        DedupePrompts(IReadOnlyList<PromptTemplate> prompts)
    {
        var seen = new Dictionary<string, PromptTemplate>(StringComparer.Ordinal);
        var diagnostics = new List<ResourceDiagnostic>();
        var kept = new List<PromptTemplate>();

        foreach (var prompt in prompts)
        {
            if (seen.TryGetValue(prompt.Name, out var existing))
            {
                diagnostics.Add(ResourceDiagnostic.CollisionOf(
                    $"name \"/{prompt.Name}\" collision",
                    prompt.FilePath,
                    new ResourceCollision
                    {
                        ResourceType = ResourceType.Prompt,
                        Name = prompt.Name,
                        WinnerPath = existing.FilePath,
                        LoserPath = prompt.FilePath,
                    }));
                continue;
            }

            seen[prompt.Name] = prompt;
            kept.Add(prompt);
        }

        return (kept, diagnostics);
    }

    /// <summary>
    /// TS <c>dedupeThemes</c>: like <see cref="DedupePrompts"/>, the <em>first</em> theme with a given
    /// name wins. Difference C143 — the port previously kept the last one, inverting precedence.
    /// </summary>
    internal static (IReadOnlyList<LoadedTheme> Themes, IReadOnlyList<ResourceDiagnostic> Diagnostics)
        DedupeThemes(IReadOnlyList<LoadedTheme> themes)
    {
        var seen = new Dictionary<string, LoadedTheme>(StringComparer.Ordinal);
        var diagnostics = new List<ResourceDiagnostic>();
        var kept = new List<LoadedTheme>();

        foreach (var theme in themes)
        {
            var name = theme.Name;
            if (seen.TryGetValue(name, out var existing))
            {
                diagnostics.Add(ResourceDiagnostic.CollisionOf(
                    $"name \"{name}\" collision",
                    theme.Path,
                    new ResourceCollision
                    {
                        ResourceType = ResourceType.Theme,
                        Name = name,
                        WinnerPath = existing.Path,
                        LoserPath = theme.Path,
                    }));
                continue;
            }

            seen[name] = theme;
            kept.Add(theme);
        }

        return (kept, diagnostics);
    }

    // ========================================================================
    // 溯源（TS findSourceInfoForPath / getDefaultSourceInfoForPath / applyExtensionSourceInfo）
    // ========================================================================

    /// <summary>TS <c>applyExtensionSourceInfo</c>: stamp every extension, its commands and its tools.</summary>
    private void ApplyExtensionSourceInfo(IReadOnlyList<Extension> extensions, Dictionary<string, PathMetadata> metadataByPath)
    {
        foreach (var extension in extensions)
        {
            extension.SourceInfo = FindSourceInfoForPath(extension.Path, null, metadataByPath)
                ?? GetDefaultSourceInfoForPath(extension.Path);

            foreach (var command in extension.Commands.Values)
            {
                command.SourceInfo = extension.SourceInfo;
            }

            foreach (var tool in extension.Tools.Values)
            {
                tool.SourceInfo = extension.SourceInfo;
            }
        }
    }

    /// <summary>
    /// TS <c>findSourceInfoForPath</c>. Lookup order matters: the extension-declared source infos win
    /// over the reload metadata, because an extension that contributed a path knows better where it
    /// came from than the package manifest that happens to contain it.
    /// </summary>
    private SourceInfo? FindSourceInfoForPath(
        string resourcePath,
        Dictionary<string, SourceInfo>? extraSourceInfos,
        Dictionary<string, PathMetadata>? metadataByPath)
    {
        if (string.IsNullOrEmpty(resourcePath))
        {
            return null;
        }

        if (SourceInfos.IsSyntheticPath(resourcePath))
        {
            return GetDefaultSourceInfoForPath(resourcePath);
        }

        var normalizedResourcePath = Path.GetFullPath(resourcePath);
        if (extraSourceInfos is not null)
        {
            foreach (var (sourcePath, sourceInfo) in extraSourceInfos)
            {
                if (IsUnderResolvedPath(normalizedResourcePath, sourcePath))
                {
                    return sourceInfo with { Path = resourcePath };
                }
            }
        }

        if (metadataByPath is not null)
        {
            if (metadataByPath.TryGetValue(normalizedResourcePath, out var exact)
                || metadataByPath.TryGetValue(resourcePath, out exact))
            {
                return SourceInfos.CreateSourceInfo(resourcePath, exact);
            }

            foreach (var (sourcePath, metadata) in metadataByPath)
            {
                if (IsUnderResolvedPath(normalizedResourcePath, sourcePath))
                {
                    return SourceInfos.CreateSourceInfo(resourcePath, metadata);
                }
            }
        }

        return null;
    }

    /// <summary>TS <c>normalizedResourcePath === resolve(sourcePath) || startsWith(resolve(sourcePath) + sep)</c>.</summary>
    private static bool IsUnderResolvedPath(string normalizedResourcePath, string sourcePath)
    {
        var normalizedSourcePath = Path.GetFullPath(sourcePath);
        if (string.Equals(normalizedResourcePath, normalizedSourcePath, StringComparison.Ordinal))
        {
            return true;
        }

        var separator = Path.DirectorySeparatorChar;
        var prefix = normalizedSourcePath.EndsWith(separator)
            ? normalizedSourcePath
            : $"{normalizedSourcePath}{separator}";
        return normalizedResourcePath.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// TS <c>getDefaultSourceInfoForPath</c>. Difference C144 — the final fallback calls
    /// <c>statSync</c> in TS, which throws for a path that does not exist; the port uses
    /// <see cref="Directory.Exists"/> and falls back to the parent directory instead. Unreachable in
    /// practice (every caller passes a path the loader just read), so this is a robustness choice
    /// rather than a behavioural one.
    /// </summary>
    private SourceInfo GetDefaultSourceInfoForPath(string filePath)
    {
        var syntheticSource = SourceInfos.GetSyntheticPathSource(filePath);
        if (syntheticSource is not null)
        {
            return SourceInfos.CreateSynthetic(filePath, syntheticSource);
        }

        var normalizedPath = Path.GetFullPath(filePath);
        string[] agentRoots =
        [
            Path.Combine(_agentDir, "skills"),
            Path.Combine(_agentDir, "prompts"),
            Path.Combine(_agentDir, "themes"),
            Path.Combine(_agentDir, "extensions"),
        ];
        string[] projectRoots =
        [
            Path.Combine(_cwd, Config.ConfigDirName, "skills"),
            Path.Combine(_cwd, Config.ConfigDirName, "prompts"),
            Path.Combine(_cwd, Config.ConfigDirName, "themes"),
            Path.Combine(_cwd, Config.ConfigDirName, "extensions"),
        ];

        foreach (var root in agentRoots)
        {
            if (IsUnderPath(normalizedPath, root))
            {
                return SourceInfos.CreateSynthetic(
                    filePath, "local", SourceScope.User, SourceOrigin.TopLevel, root);
            }
        }

        foreach (var root in projectRoots)
        {
            if (IsUnderPath(normalizedPath, root))
            {
                return SourceInfos.CreateSynthetic(
                    filePath, "local", SourceScope.Project, SourceOrigin.TopLevel, root);
            }
        }

        var baseDir = Directory.Exists(normalizedPath)
            ? normalizedPath
            : Path.GetDirectoryName(normalizedPath);
        return SourceInfos.CreateSynthetic(
            filePath, "local", SourceScope.Temporary, SourceOrigin.TopLevel, baseDir);
    }

    /// <summary>TS <c>isUnderPath</c>.</summary>
    private static bool IsUnderPath(string target, string root)
    {
        var normalizedRoot = Path.GetFullPath(root);
        if (string.Equals(target, normalizedRoot, StringComparison.Ordinal))
        {
            return true;
        }

        var separator = Path.DirectorySeparatorChar;
        var prefix = normalizedRoot.EndsWith(separator) ? normalizedRoot : $"{normalizedRoot}{separator}";
        return target.StartsWith(prefix, StringComparison.Ordinal);
    }

    // ========================================================================
    // 路径工具（TS mapSkillPath / normalizeExtensionPaths / mergePaths / resolveResourcePath）
    // ========================================================================

    /// <summary>
    /// TS <c>mapSkillPath</c>: an auto-discovered or package-contributed skill entry may point at the
    /// skill <em>directory</em>; resolve it to its <c>SKILL.md</c> and record that file's metadata so
    /// the skill's own <c>sourceInfo</c> lookup finds it.
    /// </summary>
    private static string MapSkillPath(ResolvedResource resource, Dictionary<string, PathMetadata> metadataByPath)
    {
        if (resource.Metadata.Source != "auto" && resource.Metadata.Origin != SourceOrigin.Package)
        {
            return resource.Path;
        }

        if (!Directory.Exists(resource.Path))
        {
            return resource.Path;
        }

        var skillFile = Path.Combine(resource.Path, "SKILL.md");
        if (File.Exists(skillFile))
        {
            metadataByPath.TryAdd(skillFile, resource.Metadata);
            return skillFile;
        }

        return resource.Path;
    }

    /// <summary>TS <c>normalizeExtensionPaths</c>: resolve each path (and its <c>baseDir</c>) against cwd.</summary>
    private IReadOnlyList<ExtensionResourcePath> NormalizeExtensionPaths(IReadOnlyList<ExtensionResourcePath> entries) =>
        entries
            .Select(entry => entry with
            {
                Path = ResolveResourcePath(entry.Path),
                Metadata = entry.Metadata.BaseDir is { } baseDir
                    ? entry.Metadata with { BaseDir = ResolveResourcePath(baseDir) }
                    : entry.Metadata,
            })
            .ToArray();

    /// <summary>TS <c>mergePaths</c>: concatenate, resolving each path and dropping canonical duplicates.</summary>
    private IReadOnlyList<string> MergePaths(IReadOnlyList<string> primary, IReadOnlyList<string> additional)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in primary.Concat(additional))
        {
            var resolved = ResolveResourcePath(p);
            if (seen.Add(Paths.CanonicalizePath(resolved)))
            {
                merged.Add(resolved);
            }
        }

        return merged;
    }

    /// <summary>TS <c>resolveResourcePath</c>: synthetic paths are left alone, everything else resolves against cwd.</summary>
    private string ResolveResourcePath(string p) =>
        SourceInfos.IsSyntheticPath(p) ? p : Paths.ResolvePath(p, _cwd, new PathInputOptions(Trim: true));
}
