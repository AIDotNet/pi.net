using System.Text.Json.Nodes;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using static Pi.CodingAgent.Core.ResourceLoaderExtensions;

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

    /// <summary>TS <c>additionalExtensionPaths</c>, resolved as temporary CLI package sources.</summary>
    private readonly IReadOnlyList<string> _additionalExtensionPaths;

    /// <summary>TS <c>systemPrompt</c>: an explicit system-prompt source, skipping discovery.</summary>
    private readonly string? _systemPromptSource;

    /// <summary>TS <c>appendSystemPrompt</c>: explicit append sources, skipping discovery.</summary>
    private readonly IReadOnlyList<string>? _appendSystemPromptSource;
    private readonly EventBus _eventBus;
    private readonly IReadOnlyList<InlineExtension> _extensionFactories;
    private readonly Dictionary<string, InlineExtension.Described> _builtinExtensions;
    private readonly bool _noExtensions;
    private readonly HashSet<string> _disabledBuiltinExtensions;
    private readonly Func<LoadExtensionsResult, LoadExtensionsResult>? _extensionsOverride;
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
        IReadOnlyList<string>? additionalExtensionPaths = null,
        IReadOnlyList<string>? additionalSkillPaths = null,
        IReadOnlyList<string>? additionalPromptTemplatePaths = null,
        IReadOnlyList<string>? additionalThemePaths = null,
        string? systemPrompt = null,
        IReadOnlyList<string>? appendSystemPrompt = null,
        EventBus? eventBus = null,
        IReadOnlyList<InlineExtension>? extensionFactories = null,
        bool noExtensions = false,
        IReadOnlyList<string>? disabledBuiltinExtensions = null,
        Func<LoadExtensionsResult, LoadExtensionsResult>? extensionsOverride = null,
        Func<SkillLoadOutcome, SkillLoadOutcome>? skillsOverride = null,
        Func<PromptLoadOutcome, PromptLoadOutcome>? promptsOverride = null,
        Func<ThemeLoadOutcome, ThemeLoadOutcome>? themesOverride = null)
    {
        _cwd = cwd;
        _agentDir = agentDir;
        _settings = settingsManager;
        _eventBus = eventBus ?? EventBusController.CreateEventBus();

        // TS splits the inline factories in two: built-in ones become the `builtin:<name>` registry
        // (and are *not* also loaded as inline extensions), the rest are the inline factory list. The
        // built-in names are handed to the package manager so it can emit `builtin:<name>` resources.
        var factories = extensionFactories ?? [];
        _extensionFactories = [.. factories.Where(factory => !IsBuiltinExtension(factory))];
        _builtinExtensions = factories
            .OfType<InlineExtension.Described>()
            .Where(described => described.Builtin)
            .ToDictionary(described => described.Name, StringComparer.Ordinal);

        _packages = new DefaultPackageManager(
            settingsManager, cwd, agentDir, builtinExtensions: [.. _builtinExtensions.Keys]);
        _noSkills = noSkills;
        _noPromptTemplates = noPromptTemplates;
        _noThemes = noThemes;
        _noContextFiles = noContextFiles;
        _additionalExtensionPaths = additionalExtensionPaths ?? [];
        _additionalSkillPaths = additionalSkillPaths ?? [];
        _additionalPromptPaths = additionalPromptTemplatePaths ?? [];
        _additionalThemePaths = additionalThemePaths ?? [];
        _systemPromptSource = systemPrompt;
        _appendSystemPromptSource = appendSystemPrompt;
        _noExtensions = noExtensions;
        _disabledBuiltinExtensions = new HashSet<string>(
            disabledBuiltinExtensions ?? [], StringComparer.Ordinal);
        _extensionsOverride = extensionsOverride;
        _skillsOverride = skillsOverride;
        _promptsOverride = promptsOverride;
        _themesOverride = themesOverride;
    }

    public IReadOnlyList<Skill> Skills { get; private set; } = [];

    /// <summary>TS <c>getSkills().diagnostics</c>。</summary>
    public IReadOnlyList<ResourceDiagnostic> SkillDiagnostics => _skillDiagnostics;

    public IReadOnlyList<PromptTemplate> Prompts { get; private set; } = [];

    /// <summary>TS <c>getPrompts().diagnostics</c>。</summary>
    public IReadOnlyList<ResourceDiagnostic> PromptDiagnostics => _promptDiagnostics;

    public IReadOnlyList<LoadedTheme> Themes { get; private set; } = [];

    /// <summary>TS <c>getThemes().diagnostics</c>。</summary>
    public IReadOnlyList<ResourceDiagnostic> ThemeDiagnostics => _themeDiagnostics;

    public IReadOnlyList<ContextFile> AgentsFiles { get; private set; } = [];

    public IReadOnlyList<ResourceDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>对应 TS <c>getExtensions()</c>。</summary>
    public LoadExtensionsResult GetExtensions() => _extensionsResult;

    /// <summary>TS package update notification query, delegated to the loader's package manager.</summary>
    public Task<IReadOnlyList<PackageUpdate>> CheckForAvailableUpdatesAsync(
        CancellationToken cancellationToken = default)
        => _packages.CheckForAvailableUpdatesAsync(cancellationToken);

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
                && IsShadowedContextFile(current, context.Path, shadowed);
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

    public async Task ReloadAsync(
        ResourceLoaderReloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<ResourceDiagnostic>();

        // The pre-trust bootstrap pass loads the extension set with project trust forced off, so the
        // caller can decide whether to trust the project before project-local extensions are allowed
        // in. It reloads settings itself, which is why TS reloads again below.
        LoadExtensionsResult? preTrustExtensions = null;
        if (options?.ResolveProjectTrust is { } resolveProjectTrust)
        {
            preTrustExtensions = await LoadProjectTrustExtensionsAsync().ConfigureAwait(false);
            var projectTrusted = await resolveProjectTrust(
                new ProjectTrustRequest(preTrustExtensions)).ConfigureAwait(false);
            _settings.SetProjectTrusted(projectTrusted);
        }

        // TS: `await this.settingsManager.reload()`. The trust state is preserved by SettingsManager
        // itself, so the pre-trust bootstrap pass can simply reload again.
        await _settings.ReloadAsync().ConfigureAwait(false);

        var resolved = await _packages.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var cliResolved = await _packages.ResolveExtensionSourcesAsync(
            _additionalExtensionPaths,
            temporary: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

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

        var enabledExtensions = GetEnabledPaths(resolved.Extensions);
        var enabledSkillResources = GetEnabledResources(resolved.Skills);
        var enabledPrompts = GetEnabledPaths(resolved.Prompts);
        var enabledThemes = GetEnabledPaths(resolved.Themes);

        // Auto-discovered skill entries may name a skill *directory*; `MapSkillPath` resolves those to
        // their `SKILL.md` so the loader sees a file, and records the file's metadata on the way.
        var enabledSkills = enabledSkillResources
            .Select(resource => MapSkillPath(resource, metadataByPath))
            .ToArray();

        var cliEnabledExtensions = GetEnabledPaths(cliResolved.Extensions);
        var cliEnabledSkills = GetEnabledPaths(cliResolved.Skills);
        var cliEnabledPrompts = GetEnabledPaths(cliResolved.Prompts);
        var cliEnabledThemes = GetEnabledPaths(cliResolved.Themes);

        // Built-in extensions are filtered here (not in the loader) so `disabledBuiltinExtensions` can
        // veto them by bare name, e.g. `builtin:mcp` disabled as `mcp`.
        var extensionPaths = (_noExtensions
                ? cliEnabledExtensions
                : MergePaths(cliEnabledExtensions, enabledExtensions))
            .Where(path =>
                !path.StartsWith(SourceInfos.BuiltinPathPrefix, StringComparison.Ordinal)
                || !_disabledBuiltinExtensions.Contains(path[SourceInfos.BuiltinPathPrefix.Length..]))
            .ToArray();

        var packageWarnings = CollectExtensionPackageWarnings(extensionPaths, metadataByPath);
        var extensionsResult = await LoadFinalExtensionSetAsync(extensionPaths, preTrustExtensions)
            .ConfigureAwait(false);
        extensionsResult = extensionsResult with
        {
            Warnings = MergeWarnings(extensionsResult.Warnings, packageWarnings),
        };

        // TS L584-591: a local `-e <path>` that does not exist surfaces as a load error rather than
        // being silently dropped.
        var missingExtensionPaths = new List<ExtensionLoadError>();
        foreach (var path in _additionalExtensionPaths)
        {
            if (!Paths.IsLocalPath(path))
            {
                continue;
            }

            var resolvedPath = ResolveResourcePath(path);
            if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
            {
                missingExtensionPaths.Add(new ExtensionLoadError(
                    resolvedPath, $"Extension path does not exist: {resolvedPath}"));
            }
        }

        if (missingExtensionPaths.Count > 0)
        {
            extensionsResult = extensionsResult with
            {
                Errors = [.. extensionsResult.Errors, .. missingExtensionPaths],
            };
        }

        _extensionsResult = _extensionsOverride is null ? extensionsResult : _extensionsOverride(extensionsResult);
        ApplyExtensionSourceInfo(_extensionsResult.Extensions, metadataByPath);

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

        AgentsFiles = _noContextFiles ? [] : LoadProjectContextFiles(_cwd, _agentDir);
        LoadSystemPromptSources();
        Diagnostics = diagnostics;
    }

    // ---------------------------------------------------------------- system prompt sources

    /// <summary>
    /// TS <c>resourceLoader.systemPrompt</c>: the resolved custom system prompt content, or
    /// <c>null</c> when no SYSTEM.md was discovered (the built-in default applies).
    /// </summary>
    public string? SystemPrompt { get; private set; }

    /// <summary>
    /// TS <c>getSystemPromptSource()</c>: the discovered SYSTEM.md path (resolved absolute), or
    /// <c>null</c> when none exists. Used by the loaded-resources listing.
    /// </summary>
    public string? SystemPromptSourcePath { get; private set; }

    /// <summary>TS <c>resourceLoader.appendSystemPrompt</c>: the resolved append fragments.</summary>
    public IReadOnlyList<string> AppendSystemPrompt { get; private set; } = [];

    /// <summary>TS <c>getAppendSystemPromptSources()</c>: the discovered APPEND_SYSTEM.md paths.</summary>
    public IReadOnlyList<string> AppendSystemPromptSourcePaths { get; private set; } = [];

    /// <summary>
    /// TS <c>discoverSystemPromptFile</c> (L1209-1221): project SYSTEM.md only when the project is
    /// trusted, then the global one.
    /// </summary>
    private string? DiscoverSystemPromptFile()
    {
        var projectPath = Path.Combine(_cwd, Config.ConfigDirName, "SYSTEM.md");
        if (_settings.IsProjectTrusted() && File.Exists(projectPath))
        {
            return projectPath;
        }

        var globalPath = Path.Combine(_agentDir, "SYSTEM.md");
        return File.Exists(globalPath) ? globalPath : null;
    }

    /// <summary>TS <c>discoverAppendSystemPromptFile</c> (L1223-1235): same trust gate, APPEND_SYSTEM.md.</summary>
    private string? DiscoverAppendSystemPromptFile()
    {
        var projectPath = Path.Combine(_cwd, Config.ConfigDirName, "APPEND_SYSTEM.md");
        if (_settings.IsProjectTrusted() && File.Exists(projectPath))
        {
            return projectPath;
        }

        var globalPath = Path.Combine(_agentDir, "APPEND_SYSTEM.md");
        return File.Exists(globalPath) ? globalPath : null;
    }

    /// <summary>TS <c>resolvePromptInput</c> (L167-184): read the file (BOM-stripped) or pass the text through.</summary>
    private static string? ResolvePromptInput(string? input, string description)
    {
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        if (File.Exists(input))
        {
            try
            {
                return Text.StripBom(File.ReadAllText(input));
            }
            catch (Exception error)
            {
                Console.WriteLine($"Warning: Could not read {description} file {input}: {error.Message}");
                return input;
            }
        }

        return input;
    }

    /// <summary>
    /// TS loader's system-prompt block (L653-679). An explicit <c>systemPrompt</c> /
    /// <c>appendSystemPrompt</c> source wins over discovery, exactly as in TS
    /// (<c>this.systemPromptSource ?? this.discoverSystemPromptFile()</c>).
    /// </summary>
    private void LoadSystemPromptSources()
    {
        var systemPromptSource = _systemPromptSource ?? DiscoverSystemPromptFile();
        SystemPrompt = ResolvePromptInput(systemPromptSource, "system prompt");
        SystemPromptSourcePath = systemPromptSource is not null && File.Exists(systemPromptSource)
            ? Paths.ResolvePath(systemPromptSource)
            : null;

        var appendSources = _appendSystemPromptSource
            ?? (DiscoverAppendSystemPromptFile() is { } discovered ? [discovered] : []);
        AppendSystemPrompt = appendSources
            .Select(source => ResolvePromptInput(source, "append system prompt"))
            .Where(content => content is not null)
            .Select(content => content!)
            .ToList();
        AppendSystemPromptSourcePaths = appendSources
            .Where(source => File.Exists(source))
            .Select(source => Paths.ResolvePath(source))
            .ToList();
    }

    /// <summary>
    /// TS <c>loadProjectTrustExtensions</c>: force untrusted project settings for the bootstrap pass,
    /// so project-local extensions and packages stay out while user/global and temporary CLI
    /// extensions still load.
    /// </summary>
    public async Task<LoadExtensionsResult> LoadProjectTrustExtensionsAsync()
    {
        _settings.SetProjectTrusted(false);
        await _settings.ReloadAsync().ConfigureAwait(false);
        return await LoadCurrentExtensionSetAsync(includeInlineFactories: true).ConfigureAwait(false);
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

    /// <summary>
    /// 走过的目录里找到的上下文文件是否就是主仓库那份（嵌套工作树的去重）。比较的是「目录的 realpath +
    /// 文件名」，不是文件自己的 realpath：工作树里的 AGENTS.md 若符号链接到主仓库那份，文件 realpath 解析
    /// 出来正是被遮蔽的路径本身，会把工作树那份一起跳过（上游 #10681）。
    /// </summary>
    internal static bool IsShadowedContextFile(string walkedDirectory, string contextPath, string shadowedFile)
        => string.Equals(
            Path.Combine(Paths.CanonicalizePath(walkedDirectory), Path.GetFileName(contextPath)),
            shadowedFile,
            StringComparison.Ordinal);

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

    // ========================================================================
    // 扩展装载（TS loadCurrentExtensionSet / loadFinalExtensionSet / loadExtensionPaths）
    // ========================================================================

    /// <summary>TS <c>resolveExtensionLoadPath</c>: synthetic paths are already "resolved".</summary>
    private string ResolveExtensionLoadPath(string path) =>
        SourceInfos.IsSyntheticPath(path)
            ? path
            : Paths.ResolvePath(path, _cwd, new PathInputOptions(NormalizeUnicodeSpaces: true));

    /// <summary>
    /// TS <c>loadCurrentExtensionSet</c>: the pass used for the pre-trust bootstrap. Built-in
    /// extensions are deliberately excluded — project settings can still disable them, and a loaded
    /// extension cannot be unloaded.
    /// </summary>
    private async Task<LoadExtensionsResult> LoadCurrentExtensionSetAsync(bool includeInlineFactories)
    {
        var resolved = await _packages.ResolveAsync().ConfigureAwait(false);
        var cliResolved = await _packages.ResolveExtensionSourcesAsync(
            _additionalExtensionPaths,
            temporary: true).ConfigureAwait(false);
        var metadataByPath = new Dictionary<string, PathMetadata>(StringComparer.Ordinal);

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

        var enabledExtensions = GetEnabledPaths(resolved.Extensions);
        var cliEnabledExtensions = GetEnabledPaths(cliResolved.Extensions);
        var extensionPaths = (_noExtensions
                ? cliEnabledExtensions
                : MergePaths(cliEnabledExtensions, enabledExtensions))
            .Where(path => !path.StartsWith(SourceInfos.BuiltinPathPrefix, StringComparison.Ordinal))
            .ToArray();

        var extensionsResult = await ExtensionLoader
            .LoadExtensionsCached(extensionPaths, _cwd, _eventBus)
            .ConfigureAwait(false);
        var packageWarnings = CollectExtensionPackageWarnings(extensionPaths, metadataByPath);
        extensionsResult = extensionsResult with
        {
            Warnings = MergeWarnings(extensionsResult.Warnings, packageWarnings),
        };

        if (!includeInlineFactories)
        {
            return extensionsResult;
        }

        var (inlineExtensions, inlineErrors) = await LoadExtensionFactoriesAsync(extensionsResult.Runtime)
            .ConfigureAwait(false);
        var replacementWarnings = new List<ExtensionLoadWarning>();
        return extensionsResult with
        {
            Extensions = OmitReplacedExtensions(
                [.. extensionsResult.Extensions, .. inlineExtensions], replacementWarnings),
            Errors = [.. extensionsResult.Errors, .. inlineErrors],
            Warnings = MergeWarnings(extensionsResult.Warnings, replacementWarnings),
        };
    }

    /// <summary>
    /// TS <c>loadExtensionPaths</c>: disk paths through the loader, <c>builtin:&lt;name&gt;</c> paths
    /// through the built-in registry.
    /// </summary>
    private async Task<LoadExtensionsResult> LoadExtensionPathsAsync(
        IReadOnlyList<string> paths,
        IExtensionRuntime? runtime)
    {
        var diskPaths = paths.Where(path => !path.StartsWith(SourceInfos.BuiltinPathPrefix, StringComparison.Ordinal))
            .ToArray();
        var result = await ExtensionLoader
            .LoadExtensionsCached(diskPaths, _cwd, _eventBus, runtime)
            .ConfigureAwait(false);

        var extensions = new List<Extension>(result.Extensions);
        var errors = new List<ExtensionLoadError>(result.Errors);

        foreach (var path in paths.Where(path => path.StartsWith(SourceInfos.BuiltinPathPrefix, StringComparison.Ordinal)))
        {
            var name = path[SourceInfos.BuiltinPathPrefix.Length..];
            if (!_builtinExtensions.TryGetValue(name, out var described))
            {
                errors.Add(new ExtensionLoadError(path, $"Unknown built-in extension: {path}"));
                continue;
            }

            try
            {
                var extension = await ExtensionLoader
                    .LoadExtensionFromFactory(described.Factory, _cwd, _eventBus, result.Runtime, path)
                    .ConfigureAwait(false);
                extension.Hidden = true;
                extension.Replaceable = described.Replaceable;
                extensions.Add(extension);
            }
            catch (Exception ex)
            {
                errors.Add(new ExtensionLoadError(path, ex.Message));
            }
        }

        return result with { Extensions = extensions, Errors = errors };
    }

    /// <summary>
    /// TS <c>loadFinalExtensionSet</c>: reuse whatever the pre-trust pass already loaded (its runtime
    /// in particular), load only the paths it did not cover, then order everything by
    /// <paramref name="extensionPaths"/>.
    /// </summary>
    private async Task<LoadExtensionsResult> LoadFinalExtensionSetAsync(
        IReadOnlyList<string> extensionPaths,
        LoadExtensionsResult? preTrustExtensions)
    {
        var preloaded = preTrustExtensions?.Extensions ?? [];
        var preloadedByPath = preloaded
            .Where(extension => !extension.Path.StartsWith(InlinePathPrefix, StringComparison.Ordinal))
            .ToDictionary(extension => extension.ResolvedPath, StringComparer.Ordinal);
        var failedPreloadPaths = new HashSet<string>(
            (preTrustExtensions?.Errors ?? []).Select(error => ResolveExtensionLoadPath(error.Path)),
            StringComparer.Ordinal);

        var remainingPaths = extensionPaths
            .Where(path =>
            {
                var resolvedPath = ResolveExtensionLoadPath(path);
                return !preloadedByPath.ContainsKey(resolvedPath) && !failedPreloadPaths.Contains(resolvedPath);
            })
            .ToArray();

        var remainingExtensions = await LoadExtensionPathsAsync(remainingPaths, preTrustExtensions?.Runtime)
            .ConfigureAwait(false);

        var loadedByPath = new Dictionary<string, Extension>(preloadedByPath, StringComparer.Ordinal);
        foreach (var extension in remainingExtensions.Extensions)
        {
            loadedByPath[extension.ResolvedPath] = extension;
        }

        List<Extension> inlineExtensions;
        List<ExtensionLoadError> inlineErrors;
        if (preTrustExtensions is not null)
        {
            // The pre-trust pass already ran the inline factories; reuse its results rather than
            // registering the same tools and commands twice.
            inlineExtensions = [.. preloaded.Where(
                extension => extension.Path.StartsWith(InlinePathPrefix, StringComparison.Ordinal))];
            inlineErrors = [];
        }
        else
        {
            (inlineExtensions, inlineErrors) = await LoadExtensionFactoriesAsync(remainingExtensions.Runtime)
                .ConfigureAwait(false);
        }

        var ordered = new List<Extension>();
        foreach (var path in extensionPaths)
        {
            if (loadedByPath.TryGetValue(ResolveExtensionLoadPath(path), out var extension))
            {
                ordered.Add(extension);
            }
        }

        ordered.AddRange(inlineExtensions);

        var replacementWarnings = new List<ExtensionLoadWarning>();
        var extensionsResult = new LoadExtensionsResult(
            OmitReplacedExtensions(ordered, replacementWarnings),
            [.. preTrustExtensions?.Errors ?? [], .. remainingExtensions.Errors, .. inlineErrors],
            MergeWarnings(
                [.. preTrustExtensions?.Warnings ?? [], .. remainingExtensions.Warnings],
                replacementWarnings),
            remainingExtensions.Runtime);

        return AddExtensionConflictDiagnostics(extensionsResult);
    }

    /// <summary>TS <c>loadExtensionFactories</c>: run the inline factories in registration order.</summary>
    private async Task<(List<Extension> Extensions, List<ExtensionLoadError> Errors)> LoadExtensionFactoriesAsync(
        IExtensionRuntime runtime)
    {
        var extensions = new List<Extension>();
        var errors = new List<ExtensionLoadError>();

        for (var index = 0; index < _extensionFactories.Count; index++)
        {
            var input = _extensionFactories[index];
            var isNamed = input is InlineExtension.Described;
            var factory = input switch
            {
                InlineExtension.Described described => described.Factory,
                InlineExtension.Direct direct => direct.Factory,
                _ => throw new InvalidOperationException($"unknown inline extension form: {input.GetType()}"),
            };
            var extensionPath = isNamed
                ? $"<inline:{((InlineExtension.Described)input).Name}>"
                : $"<inline:{index + 1}>";

            try
            {
                var extension = await ExtensionLoader
                    .LoadExtensionFromFactory(factory, _cwd, _eventBus, runtime, extensionPath)
                    .ConfigureAwait(false);
                extension.Hidden = isNamed && ((InlineExtension.Described)input).Hidden;
                extension.Replaceable = isNamed && ((InlineExtension.Described)input).Replaceable;
                extensions.Add(extension);
            }
            catch (Exception ex)
            {
                errors.Add(new ExtensionLoadError(extensionPath, ex.Message));
            }
        }

        return (extensions, errors);
    }

    /// <summary>
    /// TS <c>addExtensionConflictDiagnostics</c>: keep every extension loaded (load order decides
    /// precedence) and report the clashes as errors. Returns the updated result — TS mutates
    /// <c>extensionsResult.errors</c> in place, but the port's record is immutable.
    /// </summary>
    private static LoadExtensionsResult AddExtensionConflictDiagnostics(LoadExtensionsResult extensionsResult)
    {
        var conflicts = DetectExtensionConflicts(extensionsResult.Extensions);
        if (conflicts.Count == 0)
        {
            return extensionsResult;
        }

        return extensionsResult with
        {
            Errors =
            [
                .. extensionsResult.Errors,
                .. conflicts.Select(conflict => new ExtensionLoadError(conflict.Path, conflict.Message)),
            ],
        };
    }

    /// <summary>TS <c>detectExtensionConflicts</c>: tools and flags registered under the same name.</summary>
    internal static IReadOnlyList<(string Path, string Message)> DetectExtensionConflicts(
        IReadOnlyList<Extension> extensions)
    {
        var conflicts = new List<(string Path, string Message)>();
        var toolOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var flagOwners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var extension in extensions)
        {
            foreach (var toolName in extension.Tools.Keys)
            {
                if (toolOwners.TryGetValue(toolName, out var owner) && owner != extension.Path)
                {
                    conflicts.Add((extension.Path, $"Tool \"{toolName}\" conflicts with {owner}"));
                }
                else
                {
                    toolOwners[toolName] = extension.Path;
                }
            }

            foreach (var flagName in extension.Flags.Keys)
            {
                if (flagOwners.TryGetValue(flagName, out var owner) && owner != extension.Path)
                {
                    conflicts.Add((extension.Path, $"Flag \"--{flagName}\" conflicts with {owner}"));
                }
                else
                {
                    flagOwners[flagName] = extension.Path;
                }
            }
        }

        return conflicts;
    }
}

// ============================================================================
// 扩展装载的模块级辅助（TS resource-loader.ts 顶部）
// ============================================================================

/// <summary>Options for <see cref="DefaultResourceLoader.ReloadAsync"/>. Port of <c>ResourceLoaderReloadOptions</c>.</summary>
public sealed record ResourceLoaderReloadOptions
{
    /// <summary>
    /// Decide whether to trust the project, given the pre-trust extension set. Returning true keeps
    /// project-local extensions; false drops them for the rest of the load.
    /// </summary>
    public Func<ProjectTrustRequest, Task<bool>>? ResolveProjectTrust { get; init; }
}

/// <summary>Payload of <see cref="ResourceLoaderReloadOptions.ResolveProjectTrust"/>.</summary>
public sealed record ProjectTrustRequest(LoadExtensionsResult ExtensionsResult);

/// <summary>
/// Module-level helpers of <c>core/resource-loader.ts</c> that the loader class shares. TS keeps them
/// as file-scoped functions; the port puts them on a static class so they stay testable.
/// </summary>
internal static class ResourceLoaderExtensions
{
    private static readonly HashSet<string> HostProvidedExtensionPackages =
    [
        "@earendil-works/pi-agent-core",
        "@earendil-works/pi-ai",
        "@earendil-works/pi-coding-agent",
        "@earendil-works/pi-tui",
        "@mariozechner/pi-agent-core",
        "@mariozechner/pi-ai",
        "@mariozechner/pi-coding-agent",
        "@mariozechner/pi-tui",
        "@sinclair/typebox",
        "typebox",
    ];

    /// <summary>Prefix of inline extension paths, e.g. <c>&lt;inline:mcp&gt;</c>. TS inline literal.</summary>
    public const string InlinePathPrefix = "<inline:";

    /// <summary>TS <c>collectExtensionPackageWarnings</c>.</summary>
    public static IReadOnlyList<ExtensionLoadWarning> CollectExtensionPackageWarnings(
        IReadOnlyList<string> extensionPaths,
        IReadOnlyDictionary<string, PathMetadata> metadataByPath)
    {
        var warnings = new List<ExtensionLoadWarning>();
        var seenPackageRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extensionPath in extensionPaths)
        {
            var packageRoot = metadataByPath.TryGetValue(extensionPath, out var metadata)
                ? metadata.PackageRoot
                : null;
            if (packageRoot is null || !seenPackageRoots.Add(packageRoot))
            {
                continue;
            }

            var packageJsonPath = NodePath.Join(packageRoot, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                continue;
            }

            var manifest = JsonNode.Parse(Text.StripBom(File.ReadAllText(packageJsonPath)));
            if (manifest is not JsonObject package
                || package["dependencies"] is not JsonObject dependencies)
            {
                continue;
            }

            var hostDependencies = dependencies
                .Select(dependency => dependency.Key)
                .Where(HostProvidedExtensionPackages.Contains)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (hostDependencies.Length == 0)
            {
                continue;
            }

            warnings.Add(new ExtensionLoadWarning(
                packageJsonPath,
                "Host-provided extension packages must be declared in peerDependencies with a \"*\" range, "
                + "not dependencies: "
                + $"{string.Join(", ", hostDependencies)}. Installed copies can bypass the extension loader "
                + "and create duplicate runtime modules."));
        }

        return warnings;
    }

    /// <summary>
    /// TS <c>isBuiltinExtension</c>: a built-in extension is a described (non-function) inline entry
    /// with <c>builtin === true</c>.
    /// </summary>
    public static bool IsBuiltinExtension(InlineExtension input) =>
        input is InlineExtension.Described { Builtin: true };

    /// <summary>
    /// TS <c>mergeExtensionWarnings</c>: dedupe by path, later entries winning. Returns the merged
    /// list — TS mutates <c>result.warnings</c>, but the port's record is immutable.
    /// </summary>
    public static IReadOnlyList<ExtensionLoadWarning> MergeWarnings(
        IReadOnlyList<ExtensionLoadWarning> baseWarnings,
        IReadOnlyList<ExtensionLoadWarning> extra)
    {
        var merged = new Dictionary<string, ExtensionLoadWarning>(StringComparer.Ordinal);
        foreach (var warning in baseWarnings.Concat(extra))
        {
            merged[warning.Path] = warning;
        }

        return [.. merged.Values];
    }

    /// <summary>
    /// TS <c>omitReplacedExtensions</c>: drop a replaceable extension when another extension already
    /// claims one of its tool, command or flag names — so a third-party MCP extension registering
    /// <c>/mcp</c> replaces the built-in one instead of both connecting the same servers.
    /// </summary>
    public static IReadOnlyList<Extension> OmitReplacedExtensions(
        IReadOnlyList<Extension> extensions,
        List<ExtensionLoadWarning>? warnings = null)
    {
        static IEnumerable<string> Names(Extension extension) =>
            extension.Tools.Keys.Select(name => $"tool:{name}")
                .Concat(extension.Commands.Keys.Select(name => $"command:{name}"))
                .Concat(extension.Flags.Keys.Select(name => $"flag:{name}"));

        var taken = new Dictionary<string, Extension>(StringComparer.Ordinal);
        foreach (var extension in extensions.Where(extension => !extension.Replaceable))
        {
            foreach (var name in Names(extension))
            {
                taken.TryAdd(name, extension);
            }
        }

        var kept = new List<Extension>();
        foreach (var extension in extensions)
        {
            if (!extension.Replaceable)
            {
                kept.Add(extension);
                continue;
            }

            var replacement = Names(extension)
                .Select(name => (Name: name, Extension: taken.TryGetValue(name, out var owner) ? owner : null))
                .FirstOrDefault(candidate => candidate.Extension is not null);
            if (replacement.Extension is null)
            {
                kept.Add(extension);
                continue;
            }

            if (extension.Path.StartsWith(SourceInfos.BuiltinPathPrefix, StringComparison.Ordinal))
            {
                var builtinName = extension.Path[SourceInfos.BuiltinPathPrefix.Length..];
                var separator = replacement.Name.IndexOf(':');
                var kind = replacement.Name[..separator];
                var rawName = replacement.Name[(separator + 1)..];
                var registeredName = kind switch
                {
                    "command" => $"/{rawName}",
                    "flag" => $"--{rawName}",
                    _ => rawName,
                };
                warnings?.Add(new ExtensionLoadWarning(
                    extension.Path,
                    $"Extension {replacement.Extension.Path} registers {kind} `{registeredName}`, so " +
                    $"built-in extension `{builtinName}` was not loaded. To use `{builtinName}`, run " +
                    "`pi config` and make sure it is enabled under Built-in extensions, then disable or " +
                    "remove the existing extension. We recommend only having one or the other loaded at " +
                    "a time."));
            }
        }

        return kept;
    }
}
