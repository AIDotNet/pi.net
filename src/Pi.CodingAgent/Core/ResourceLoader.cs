using System.Text.Json.Nodes;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>4f 之前的最小 theme 文档。对应 TS <c>Theme</c> 的 name/path 子集。</summary>
public sealed record LoadedTheme
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public required JsonObject Document { get; init; }
}

/// <summary>
/// 资源加载。对应 TS <c>core/resource-loader.ts</c>。
/// 4e-3d-2：项目上下文文件、skills/prompts、theme JSON；extensions runner 顺延 4e-3e，完整 theme 顺延 4f。
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
        IReadOnlyList<string>? additionalThemePaths = null)
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
    }

    public IReadOnlyList<Skill> Skills { get; private set; } = [];

    public IReadOnlyList<PromptTemplate> Prompts { get; private set; } = [];

    public IReadOnlyList<LoadedTheme> Themes { get; private set; } = [];

    public IReadOnlyList<ContextFile> AgentsFiles { get; private set; } = [];

    public IReadOnlyList<ResourceDiagnostic> Diagnostics { get; private set; } = [];

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
        var resolved = await _packages.ResolveAsync(cancellationToken).ConfigureAwait(false);

        if (_noSkills)
        {
            Skills = [];
        }
        else
        {
            var skillPaths = resolved.Skills.Where(item => item.Enabled).Select(item => item.Path)
                .Concat(_additionalSkillPaths)
                .ToArray();
            var loaded = Core.Skills.LoadSkills(new LoadSkillsOptions
            {
                Cwd = _cwd,
                AgentDir = _agentDir,
                SkillPaths = skillPaths,
                IncludeDefaults = true,
            });
            Skills = loaded.Skills;
            diagnostics.AddRange(loaded.Diagnostics);
        }

        if (_noPromptTemplates)
        {
            Prompts = [];
        }
        else
        {
            var promptPaths = resolved.Prompts.Where(item => item.Enabled).Select(item => item.Path)
                .Concat(_additionalPromptPaths)
                .ToArray();
            var loaded = PromptTemplates.Load(new LoadPromptTemplatesOptions
            {
                Cwd = _cwd,
                AgentDir = _agentDir,
                PromptPaths = promptPaths,
                IncludeDefaults = true,
            });
            Prompts = loaded.Templates;
            diagnostics.AddRange(loaded.Diagnostics);
        }

        if (_noThemes)
        {
            Themes = [];
        }
        else
        {
            var themePaths = resolved.Themes.Where(item => item.Enabled).Select(item => item.Path)
                .Concat(_additionalThemePaths)
                .ToArray();
            var loaded = LoadThemes(themePaths);
            Themes = loaded.Themes;
            diagnostics.AddRange(loaded.Diagnostics);
        }

        AgentsFiles = _noContextFiles ? [] : LoadProjectContextFiles(_cwd, _agentDir);
        Diagnostics = diagnostics;
    }

    internal static (IReadOnlyList<LoadedTheme> Themes, IReadOnlyList<ResourceDiagnostic> Diagnostics) LoadThemes(
        IReadOnlyList<string> paths)
    {
        var themes = new Dictionary<string, LoadedTheme>(StringComparer.OrdinalIgnoreCase);
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
                if (themes.TryGetValue(name, out var winner))
                {
                    diagnostics.Add(ResourceDiagnostic.CollisionOf(
                        $"theme collision: {name}",
                        path,
                        new ResourceCollision
                        {
                            ResourceType = ResourceType.Theme,
                            Name = name,
                            WinnerPath = path,
                            LoserPath = winner.Path,
                        }));
                }

                themes[name] = new LoadedTheme { Name = name, Path = path, Document = json };
            }
            catch (Exception ex)
            {
                diagnostics.Add(ResourceDiagnostic.Error($"failed to load theme {path}: {ex.Message}", path));
            }
        }

        return (themes.Values.ToArray(), diagnostics);
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

        if (!string.Equals(Paths.CanonicalizePath(Path.Combine(mainRepoRoot, ".git")), commonGitDir, StringComparison.Ordinal))
        {
            return null;
        }

        var worktreeContext = LoadContextFileFromDir(worktreeRoot);
        return worktreeContext is null ? null : Path.Combine(mainRepoRoot, Path.GetFileName(worktreeContext.Path));
    }
}
