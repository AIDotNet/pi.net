using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>PackageResourceKind</c>（包资源）。Diagnostics 另有同名静态类，故用此名。</summary>
public enum PackageResourceKind
{
    Extensions,
    Skills,
    Prompts,
    Themes,
}

/// <summary>对应 TS <c>PackageOrigin</c>。</summary>
public enum PackageOrigin
{
    Npm,
    Git,
    Local,
}

/// <summary>对应 TS <c>PathMetadata</c>。</summary>
public sealed record PathMetadata
{
    public required string Source { get; init; }

    public required string Scope { get; init; }

    public required string Origin { get; init; }

    public string? BaseDir { get; init; }
}

/// <summary>对应 TS <c>ResolvedResource</c>。</summary>
public sealed record ResolvedResource
{
    public required string Path { get; init; }

    public required PathMetadata Metadata { get; init; }

    public required bool Enabled { get; init; }
}

/// <summary>对应 TS <c>ResolvedResources</c>。</summary>
public sealed record ResolvedResources
{
    public IReadOnlyList<ResolvedResource> Extensions { get; init; } = [];

    public IReadOnlyList<ResolvedResource> Skills { get; init; } = [];

    public IReadOnlyList<ResolvedResource> Prompts { get; init; } = [];

    public IReadOnlyList<ResolvedResource> Themes { get; init; } = [];
}

/// <summary>对应 TS <c>PackageSource</c>。</summary>
public sealed record PackageSource
{
    public required string Source { get; init; }

    public required string Version { get; init; }

    public required string ResolvedHash { get; init; }

    public required string InstallPath { get; init; }

    public required PackageOrigin Origin { get; init; }

    public string? Name { get; init; }
}

/// <summary>对应 TS <c>PackageManager</c>。</summary>
public interface IPackageManager
{
    Task<ResolvedResources> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 包管理器。对应 TS <c>core/package-manager.ts</c> 的 <c>DefaultPackageManager</c>。
/// 4e-3c-1 先落地 <c>resolve()</c>（自动发现、settings 路径、include/exclude/autoload）与 <c>parseSource</c>。
/// npm/git install/update 的网络与进程调用顺延 4e-3c-2。
/// </summary>
public sealed class DefaultPackageManager : IPackageManager
{
    private static readonly (PackageResourceKind Type, string Dir, string[] Extensions)[] ResourceKinds =
    [
        (PackageResourceKind.Extensions, "extensions", [".ts", ".js"]),
        (PackageResourceKind.Skills, "skills", [".md"]),
        (PackageResourceKind.Prompts, "prompts", [".md"]),
        (PackageResourceKind.Themes, "themes", [".json"]),
    ];

    private readonly SettingsManager _settings;
    private readonly string _cwd;
    private readonly string _agentDir;
    private readonly IReadOnlyList<string> _builtinExtensions;

    public DefaultPackageManager(
        SettingsManager settingsManager,
        string cwd,
        string agentDir,
        IReadOnlyList<string>? builtinExtensions = null)
    {
        _settings = settingsManager;
        _cwd = cwd;
        _agentDir = agentDir;
        _builtinExtensions = builtinExtensions ?? [];
    }

    public Task<ResolvedResources> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var acc = new Accumulator();
        AddAutoDiscoveredResources(acc);
        ResolveLocalEntries(acc, "extensions", _settings.GetGlobalExtensionPaths(), _settings.GetProjectExtensionPaths(), PackageResourceKind.Extensions);
        ResolveLocalEntries(acc, "skills", _settings.GetGlobalSkillPaths(), _settings.GetProjectSkillPaths(), PackageResourceKind.Skills);
        ResolveLocalEntries(acc, "prompts", _settings.GetGlobalPromptTemplatePaths(), _settings.GetProjectPromptTemplatePaths(), PackageResourceKind.Prompts);
        ResolveLocalEntries(acc, "themes", _settings.GetGlobalThemePaths(), _settings.GetProjectThemePaths(), PackageResourceKind.Themes);
        AddBuiltinExtensions(acc);
        ApplyAutoloadDisabledPatterns(acc);
        return Task.FromResult(acc.ToResolved());
    }

    /// <summary>对应 TS <c>parseSource</c>。</summary>
    internal static ParsedPackageSource ParseSource(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            throw new ArgumentException("empty package source", nameof(spec));
        }

        if (spec.StartsWith("git+", StringComparison.Ordinal)
            || spec.StartsWith("git://", StringComparison.Ordinal)
            || spec.StartsWith("ssh://", StringComparison.Ordinal)
            || spec.Contains(".git", StringComparison.Ordinal)
            || spec.StartsWith("github:", StringComparison.Ordinal)
            || spec.StartsWith("gitlab:", StringComparison.Ordinal)
            || spec.StartsWith("bitbucket:", StringComparison.Ordinal)
            || LooksLikeGithubShorthand(spec))
        {
            var parsed = Git.ParseGitUrl(spec.StartsWith("git+", StringComparison.Ordinal) ? spec[4..] : spec)
                ?? throw new ArgumentException($"invalid git source: {spec}", nameof(spec));
            var repoName = Path.GetFileName(parsed.Repo.TrimEnd('/', '\\'));
            if (repoName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                repoName = repoName[..^4];
            }

            return new ParsedPackageSource("git", spec, parsed.Repo, parsed.Ref, null, repoName);
        }

        if (spec.StartsWith("file:", StringComparison.Ordinal)
            || spec.StartsWith(".", StringComparison.Ordinal)
            || spec.StartsWith("~", StringComparison.Ordinal)
            || Path.IsPathRooted(spec)
            || spec.Contains("/./", StringComparison.Ordinal)
            || spec.Contains("\\", StringComparison.Ordinal))
        {
            return new ParsedPackageSource("local", spec, spec, null, null, null);
        }

        var (name, version) = ParseNpmSpec(spec);
        return new ParsedPackageSource("npm", spec, name, version, null, name);
    }

    internal static string GetNpmInstallPath(string packagesRoot, string name, string version)
        => Path.Combine(packagesRoot, "npm", Sanitize(name), Sanitize(version));

    internal static string GetGitInstallPath(string packagesRoot, string name, string resolvedHash)
        => Path.Combine(packagesRoot, "git", Sanitize(name), Sanitize(resolvedHash));

    internal sealed record ParsedPackageSource(string Kind, string Spec, string Locator, string? Version, string? Hash, string? Name);

    private void AddBuiltinExtensions(Accumulator acc)
    {
        foreach (var path in _builtinExtensions)
        {
            acc.Add(PackageResourceKind.Extensions, new ResolvedResource
            {
                Path = path,
                Metadata = new PathMetadata
                {
                    Source = SourceInfos.BuiltinPathPrefix + Path.GetFileNameWithoutExtension(path),
                    Scope = "global",
                    Origin = "package",
                },
                Enabled = true,
            });
        }
    }

    private void AddAutoDiscoveredResources(Accumulator acc)
    {
        CollectAuto(acc, _agentDir, "global");
        CollectAuto(acc, Path.Combine(_cwd, Config.ConfigDirName), "project");
    }

    private void CollectAuto(Accumulator acc, string baseDir, string scope)
    {
        if (!Directory.Exists(baseDir))
        {
            return;
        }

        foreach (var kind in ResourceKinds)
        {
            var dir = Path.Combine(baseDir, kind.Dir);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in EnumerateResourceFiles(dir, kind.Type, kind.Extensions))
            {
                acc.Add(kind.Type, new ResolvedResource
                {
                    Path = file,
                    Metadata = new PathMetadata
                    {
                        Source = "auto",
                        Scope = scope,
                        Origin = "top-level",
                        BaseDir = dir,
                    },
                    Enabled = true,
                });
            }
        }
    }

    private void ResolveLocalEntries(
        Accumulator acc,
        string relativeRoot,
        IReadOnlyList<string> globalPaths,
        IReadOnlyList<string> projectPaths,
        PackageResourceKind type)
    {
        AddResolvedPaths(acc, type, globalPaths, _agentDir, "global", relativeRoot);
        AddResolvedPaths(acc, type, projectPaths, Path.Combine(_cwd, Config.ConfigDirName), "project", relativeRoot);
    }

    private static void AddResolvedPaths(
        Accumulator acc,
        PackageResourceKind type,
        IReadOnlyList<string> entries,
        string baseDir,
        string scope,
        string relativeRoot)
    {
        foreach (var entry in entries)
        {
            var resolved = ResolveEntry(entry, baseDir);
            if (resolved is null)
            {
                continue;
            }

            acc.Add(type, new ResolvedResource
            {
                Path = resolved,
                Metadata = new PathMetadata
                {
                    Source = entry,
                    Scope = scope,
                    Origin = "settings",
                    BaseDir = Path.Combine(baseDir, relativeRoot),
                },
                Enabled = true,
            });
        }
    }

    private static string? ResolveEntry(string entry, string baseDir)
    {
        var path = Path.IsPathRooted(entry) ? entry : Path.Combine(baseDir, entry);
        path = Path.GetFullPath(path);
        return File.Exists(path) || Directory.Exists(path) ? path : null;
    }

    private static IEnumerable<string> EnumerateResourceFiles(string dir, PackageResourceKind type, string[] extensions)
    {
        if (type == PackageResourceKind.Skills)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
                    || (Path.GetDirectoryName(file) == dir
                        && Path.GetExtension(file).Equals(".md", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("README.md", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return Path.GetFullPath(file);
                }
            }

            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(file);
            }
        }
    }

    private void ApplyAutoloadDisabledPatterns(Accumulator acc)
    {
        foreach (var kind in ResourceKinds)
        {
            var typeName = kind.Dir;
            var include = _settings.GetInclude(typeName);
            var exclude = _settings.GetExclude(typeName);
            var autoload = _settings.GetAutoload(typeName);
            if (include.Count == 0 && exclude.Count == 0 && autoload.Count == 0)
            {
                continue;
            }

            acc.Replace(kind.Type, acc.Get(kind.Type).Select(resource =>
            {
                var enabled = resource.Enabled;
                if (include.Count > 0)
                {
                    enabled = MatchesAny(resource, include);
                }

                if (exclude.Count > 0 && MatchesAny(resource, exclude))
                {
                    enabled = false;
                }

                if (autoload.Count > 0 && autoload.All(pattern => pattern != "true") && MatchesAny(resource, autoload.Where(p => p != "false")))
                {
                    enabled = false;
                }

                if (autoload.Count == 1 && autoload[0] == "false" && resource.Metadata.Origin == "top-level")
                {
                    enabled = false;
                }

                return resource with { Enabled = enabled };
            }).ToList());
        }
    }

    private static IReadOnlyList<string> MatchCandidates(ResolvedResource resource)
    {
        var fileName = Path.GetFileName(resource.Path);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var candidates = new List<string> { fileName, stem, resource.Metadata.Source };
        if (resource.Metadata.BaseDir is { Length: > 0 } baseDir)
        {
            candidates.Insert(0, Path.GetRelativePath(baseDir, resource.Path).Replace('\\', '/'));
        }

        return candidates;
    }

    private static bool MatchesAny(ResolvedResource resource, IEnumerable<string> patterns)
        => MatchCandidates(resource).Any(candidate => patterns.Any(pattern =>
        {
            try
            {
                return string.Equals(candidate, pattern, StringComparison.OrdinalIgnoreCase)
                    || new Minimatch(pattern, new GlobOptions { Dot = true, MatchBase = true }).Match(candidate);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }));

    private static (string Name, string Version) ParseNpmSpec(string spec)
    {
        if (spec.StartsWith("@", StringComparison.Ordinal))
        {
            var slash = spec.IndexOf('/');
            var at = spec.LastIndexOf('@');
            if (slash >= 0 && at > slash)
            {
                return (spec[..at], spec[(at + 1)..]);
            }

            return (spec, "latest");
        }

        var idx = spec.IndexOf('@');
        return idx > 0 ? (spec[..idx], spec[(idx + 1)..]) : (spec, "latest");
    }

    private static bool LooksLikeGithubShorthand(string spec)
    {
        if (spec.StartsWith('@'))
        {
            return false;
        }

        if (spec.StartsWith("git@", StringComparison.Ordinal))
        {
            return true;
        }

        var slash = spec.IndexOf('/');
        return slash > 0 && !spec.Contains("://", StringComparison.Ordinal) && spec.Count(c => c == '/') == 1;
    }

    private static string Sanitize(string value)
        => string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

    private sealed class Accumulator
    {
        private readonly Dictionary<PackageResourceKind, List<ResolvedResource>> _items = new()
        {
            [PackageResourceKind.Extensions] = [],
            [PackageResourceKind.Skills] = [],
            [PackageResourceKind.Prompts] = [],
            [PackageResourceKind.Themes] = [],
        };

        public IReadOnlyList<ResolvedResource> Get(PackageResourceKind type) => _items[type];

        public void Replace(PackageResourceKind type, List<ResolvedResource> items) => _items[type] = items;

        public void Add(PackageResourceKind type, ResolvedResource resource)
        {
            var list = _items[type];
            var existing = list.FindIndex(item => PathsEqual(item.Path, resource.Path));
            if (existing >= 0)
            {
                if (resource.Metadata.Scope == "project" && list[existing].Metadata.Scope != "project")
                {
                    list[existing] = resource;
                }

                return;
            }

            list.Add(resource);
        }

        public ResolvedResources ToResolved()
            => new()
            {
                Extensions = [.. _items[PackageResourceKind.Extensions]],
                Skills = [.. _items[PackageResourceKind.Skills]],
                Prompts = [.. _items[PackageResourceKind.Prompts]],
                Themes = [.. _items[PackageResourceKind.Themes]],
            };

        private static bool PathsEqual(string left, string right)
            => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }
}
