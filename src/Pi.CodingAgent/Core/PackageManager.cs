using System.Text.Json.Nodes;
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

    void SetProgressCallback(Action<PackageProgressEvent>? callback);
}

/// <summary>对应 TS 安装进度事件。</summary>
public sealed record PackageProgressEvent(string Phase, string Message);

/// <summary>npm/git 进程注入点。测试替换为假实现，避免真实网络。</summary>
public interface IPackageCommandRunner
{
    Task<string> RunAsync(string command, IReadOnlyList<string> arguments, string? workingDirectory = null, CancellationToken cancellationToken = default);
}

/// <summary>默认用 <see cref="ChildProcess.SpawnSync"/> 跑 npm/git。</summary>
public sealed class DefaultPackageCommandRunner : IPackageCommandRunner
{
    public Task<string> RunAsync(string command, IReadOnlyList<string> arguments, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = ChildProcess.SpawnSync(command, arguments, new SpawnSyncOptions { Cwd = workingDirectory });
        if (result.Error is not null)
        {
            throw result.Error;
        }

        if (result.Status is not 0)
        {
            throw new InvalidOperationException($"{command} exited {result.Status}: {result.Stderr}");
        }

        return Task.FromResult(result.Stdout);
    }
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
    private readonly IPackageCommandRunner _runner;
    private Action<PackageProgressEvent>? _progress;

    public DefaultPackageManager(
        SettingsManager settingsManager,
        string cwd,
        string agentDir,
        IReadOnlyList<string>? builtinExtensions = null,
        IPackageCommandRunner? commandRunner = null)
    {
        _settings = settingsManager;
        _cwd = cwd;
        _agentDir = agentDir;
        _builtinExtensions = builtinExtensions ?? [];
        _runner = commandRunner ?? new DefaultPackageCommandRunner();
    }

    public void SetProgressCallback(Action<PackageProgressEvent>? callback) => _progress = callback;

    /// <summary>对应 TS <c>install</c>。</summary>
    public async Task<string> InstallAsync(string source, string scope = "user", CancellationToken cancellationToken = default)
    {
        var parsed = ParseSource(source);
        _progress?.Invoke(new PackageProgressEvent("install", source));
        return parsed.Type switch
        {
            "npm" => await InstallNpmAsync(parsed, scope, cancellationToken).ConfigureAwait(false),
            "git" => await InstallGitAsync(parsed, scope, cancellationToken).ConfigureAwait(false),
            _ => InstallLocal(parsed),
        };
    }

    /// <summary>对应 TS <c>installAndPersist</c>。</summary>
    public async Task<string> InstallAndPersistAsync(string source, bool local = false, CancellationToken cancellationToken = default)
    {
        var path = await InstallAsync(source, local ? "project" : "user", cancellationToken).ConfigureAwait(false);
        AddSourceToSettings(source, local);
        return path;
    }

    /// <summary>对应 TS <c>removeAndPersist</c> 的 settings 侧；进程卸载经 runner。</summary>
    public async Task<bool> RemoveAndPersistAsync(string source, bool local = false, CancellationToken cancellationToken = default)
    {
        var parsed = ParseSource(source);
        if (parsed.Type == "npm")
        {
            var root = GetNpmInstallRoot(local ? "project" : "user", temporary: false);
            var npm = GetNpmInvocation();
            await _runner.RunAsync(npm.Command, [.. npm.Args, "uninstall", parsed.Name ?? source, "--prefix", root], cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else if (parsed.Type == "git")
        {
            var dest = GetManagedGitInstallPath(parsed, local ? "project" : "user");
            if (Directory.Exists(dest))
            {
                Directory.Delete(dest, recursive: true);
            }
        }

        return RemoveSourceFromSettings(source, local);
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

    /// <summary>对应 TS <c>parseSource</c>：先 <c>npm:</c>，再本地路径，再 git URL，否则视为本地。</summary>
    internal static ParsedPackageSource ParseSource(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            throw new ArgumentException("empty package source", nameof(spec));
        }

        if (spec.StartsWith("npm:", StringComparison.Ordinal))
        {
            var npmSpec = spec[4..].Trim();
            var (npmName, version) = ParseNpmSpec(npmSpec);
            return new ParsedPackageSource
            {
                Type = "npm",
                Spec = npmSpec,
                Locator = npmName,
                Name = npmName,
                Version = version,
                Pinned = IsExactNpmVersion(version),
            };
        }

        if (IsLocalPath(spec))
        {
            return new ParsedPackageSource { Type = "local", Spec = spec, Locator = spec, Path = spec };
        }

        var git = Git.ParseGitUrl(spec);
        if (git is not null)
        {
            return new ParsedPackageSource
            {
                Type = "git",
                Spec = spec,
                Locator = git.Repo,
                Host = git.Host,
                Path = git.Path,
                Ref = git.Ref,
                Pinned = git.Pinned,
                Name = git.Path,
            };
        }

        return new ParsedPackageSource { Type = "local", Spec = spec, Locator = spec, Path = spec };
    }

    /// <summary>对应 TS <c>addSourceToSettings</c>。</summary>
    public bool AddSourceToSettings(string source, bool local = false)
    {
        var scope = local ? "project" : "user";
        var current = GetPackageNodes(local);
        var normalized = NormalizePackageSourceForSettings(source, scope);
        var matchIndex = current.FindIndex(existing => PackageSourcesMatch(existing, source, scope));
        if (matchIndex >= 0)
        {
            if (GetPackageSourceString(current[matchIndex]) == normalized)
            {
                return false;
            }

            current[matchIndex] = ReplacePackageSource(current[matchIndex], normalized);
        }
        else
        {
            current.Add(JsonValue.Create(normalized));
        }

        WritePackageNodes(local, current);
        _progress?.Invoke(new PackageProgressEvent("settings", normalized));
        return true;
    }

    /// <summary>对应 TS <c>removeSourceFromSettings</c>。</summary>
    public bool RemoveSourceFromSettings(string source, bool local = false)
    {
        var scope = local ? "project" : "user";
        var current = GetPackageNodes(local);
        var next = current.Where(existing => !PackageSourcesMatch(existing, source, scope)).ToList();
        if (next.Count == current.Count)
        {
            return false;
        }

        WritePackageNodes(local, next);
        return true;
    }

    internal string GetNpmInstallPath(ParsedPackageSource source, string scope)
    {
        var name = source.Name ?? "package";
        if (scope == "temporary")
        {
            return Path.Combine(_agentDir, "tmp", "extensions", "node_modules", Sanitize(name));
        }

        return GetNpmInstallPath(Path.Combine(_agentDir, "packages"), name, source.Version ?? "latest");
    }

    internal static string GetNpmInstallPath(string packagesRoot, string name, string version)
        => Path.Combine(packagesRoot, "npm", Sanitize(name), Sanitize(version));

    internal static string GetGitInstallPath(string packagesRoot, string name, string resolvedHash)
        => Path.Combine(packagesRoot, "git", Sanitize(name), Sanitize(resolvedHash));

    internal string GetNpmInstallRoot(string scope, bool temporary)
    {
        if (temporary || scope == "temporary")
        {
            return Path.Combine(_agentDir, "tmp", "extensions");
        }

        return scope == "project"
            ? Path.Combine(_cwd, Config.ConfigDirName, "npm")
            : Path.Combine(_agentDir, "npm");
    }

    internal string GetManagedGitInstallPath(ParsedPackageSource source, string scope)
    {
        var host = source.Host ?? "git";
        var path = (source.Path ?? source.Name ?? "repo").Replace('/', Path.DirectorySeparatorChar);
        var root = scope == "project"
            ? Path.Combine(_cwd, Config.ConfigDirName, "git")
            : Path.Combine(_agentDir, "git");
        return Path.Combine(root, host, path);
    }

    private string InstallLocal(ParsedPackageSource source)
    {
        var path = source.Path ?? source.Spec;
        var absolute = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_cwd, path));
        if (!File.Exists(absolute) && !Directory.Exists(absolute))
        {
            throw new DirectoryNotFoundException($"local package not found: {absolute}");
        }

        return absolute;
    }

    private async Task<string> InstallNpmAsync(ParsedPackageSource source, string scope, CancellationToken cancellationToken)
    {
        var temporary = scope == "temporary";
        var root = GetNpmInstallRoot(scope, temporary);
        Directory.CreateDirectory(root);
        var packageJson = Path.Combine(root, "package.json");
        if (!File.Exists(packageJson))
        {
            File.WriteAllText(packageJson, """{"private":true}""");
        }

        var spec = source.Version is { Length: > 0 } version ? $"{source.Name}@{version}" : source.Name ?? source.Spec;
        var npm = GetNpmInvocation();
        await _runner.RunAsync(npm.Command, [.. npm.Args, "install", spec, "--prefix", root, "--legacy-peer-deps"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return Path.Combine(root, "node_modules", source.Name ?? spec);
    }

    private async Task<string> InstallGitAsync(ParsedPackageSource source, string scope, CancellationToken cancellationToken)
    {
        var dest = GetManagedGitInstallPath(source, scope);
        if (Directory.Exists(dest))
        {
            return dest;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await _runner.RunAsync("git", ["clone", source.Locator, dest], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(source.Ref) && !source.Pinned)
        {
            await _runner.RunAsync("git", ["checkout", source.Ref], dest, cancellationToken).ConfigureAwait(false);
        }

        return dest;
    }

    private (string Command, string[] Args) GetNpmInvocation()
    {
        var custom = _settings.GetNpmCommand();
        if (custom is { Count: > 0 })
        {
            return (custom[0], custom.Skip(1).ToArray());
        }

        return ("npm", []);
    }

    internal sealed record ParsedPackageSource
    {
        public required string Type { get; init; }

        public string Spec { get; init; } = "";

        public string Locator { get; init; } = "";

        public string? Name { get; init; }

        public string? Version { get; init; }

        public string? Path { get; init; }

        public string? Host { get; init; }

        public string? Ref { get; init; }

        public bool Pinned { get; init; }
    }

    /// <summary>
    /// Register the built-in extensions as <c>builtin:&lt;name&gt;</c> resources. TS's <c>resolve</c>
    /// computes each one's enabled flag from the project/global `extensions` override patterns
    /// (<c>applyAutoloadDisabledPatterns</c> + <c>isEnabledByOverrides</c>); the port applies the
    /// (simplified) <see cref="ApplyAutoloadDisabledPatterns"/> pass afterwards instead, and otherwise
    /// enables them — a built-in extension is on unless something disables it.
    /// </summary>
    private void AddBuiltinExtensions(Accumulator acc)
    {
        foreach (var name in _builtinExtensions)
        {
            acc.Add(PackageResourceKind.Extensions, new ResolvedResource
            {
                Path = SourceInfos.BuiltinPathPrefix + name,
                Metadata = new PathMetadata
                {
                    Source = "builtin",
                    Scope = SourceScope.User,
                    Origin = SourceOrigin.TopLevel,
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

        // TS matches override patterns against resolved *paths* (`matchesAnyPattern` with a baseDir),
        // so `-builtin:mcp` targets a built-in. The port's matcher compares file name, stem and source
        // instead, which cannot see through the `builtin:` prefix — add the bare name so
        // `extensions: ["-mcp"]` still disables the built-in `mcp` extension.
        if (resource.Path.StartsWith(SourceInfos.BuiltinPathPrefix, StringComparison.Ordinal))
        {
            candidates.Insert(0, resource.Path[SourceInfos.BuiltinPathPrefix.Length..]);
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

    private static bool IsLocalPath(string spec)
        => spec.StartsWith("file:", StringComparison.Ordinal)
            || spec.StartsWith(".", StringComparison.Ordinal)
            || spec.StartsWith("~", StringComparison.Ordinal)
            || Path.IsPathRooted(spec);

    private static bool IsExactNpmVersion(string version)
        => version.Length > 0 && char.IsDigit(version[0]) && !version.Contains('*') && !version.Contains('^') && !version.Contains('~');

    private List<JsonNode?> GetPackageNodes(bool local)
        => [.. (local ? _settings.GetProjectSettings().Packages : _settings.GetGlobalSettings().Packages) ?? []];

    private void WritePackageNodes(bool local, IReadOnlyList<JsonNode?> nodes)
    {
        if (local)
        {
            _settings.SetProjectPackages(nodes);
        }
        else
        {
            _settings.SetPackages(nodes);
        }
    }

    private string NormalizePackageSourceForSettings(string source, string scope)
    {
        var parsed = ParseSource(source);
        if (parsed.Type != "local" || string.IsNullOrEmpty(parsed.Path))
        {
            return source;
        }

        var absolute = Path.IsPathRooted(parsed.Path) ? parsed.Path : Path.GetFullPath(Path.Combine(_cwd, parsed.Path));
        var baseDir = scope == "project" ? Path.Combine(_cwd, Config.ConfigDirName) : _agentDir;
        var rel = Path.GetRelativePath(baseDir, absolute);
        return rel.StartsWith('.') ? rel : Path.Combine(".", rel);
    }

    private bool PackageSourcesMatch(JsonNode? existing, string incoming, string scope)
    {
        var existingSource = GetPackageSourceString(existing);
        if (existingSource is null)
        {
            return false;
        }

        var left = ParseSource(existingSource);
        var right = ParseSource(incoming);
        if (left.Type != right.Type)
        {
            return false;
        }

        if (left.Type == "git")
        {
            return string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        }

        if (left.Type == "local")
        {
            return string.Equals(
                NormalizePackageSourceForSettings(existingSource, scope),
                NormalizePackageSourceForSettings(incoming, scope),
                StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left.Name, right.Name, StringComparison.Ordinal);
    }

    private static string? GetPackageSourceString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return node is JsonObject obj ? obj["source"]?.GetValue<string>() : null;
    }

    private static JsonNode? ReplacePackageSource(JsonNode? existing, string source)
    {
        if (existing is JsonObject obj)
        {
            var copy = obj.DeepClone().AsObject();
            copy["source"] = source;
            return copy;
        }

        return JsonValue.Create(source);
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
