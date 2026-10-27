using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>One configured package plus the scope it came from. TS's <c>{ pkg, scope }</c> tuple.</summary>
internal sealed record PackageEntry(JsonNode? Package, string Scope);

/// <summary>
/// TS <c>PackageFilter</c>: the object form of a <c>packages</c> entry. A <see langword="null"/> list
/// means the key was absent (collect the conventional directory), an empty list means it was present
/// but empty (load nothing of that kind) — TS relies on `undefined` vs `[]` and so does the port.
/// </summary>
internal sealed record PackageFilter
{
    public bool? Autoload { get; init; }

    public IReadOnlyList<string>? Extensions { get; init; }

    public IReadOnlyList<string>? Skills { get; init; }

    public IReadOnlyList<string>? Prompts { get; init; }

    public IReadOnlyList<string>? Themes { get; init; }

    public IReadOnlyList<string>? PatternsFor(string resourceType) => resourceType switch
    {
        "extensions" => Extensions,
        "skills" => Skills,
        "prompts" => Prompts,
        "themes" => Themes,
        _ => null,
    };
}

/// <summary>Which kind of resource a resolved path is. Port-only discriminator for the accumulator.</summary>
public enum PackageResourceKind
{
    Extensions,
    Skills,
    Prompts,
    Themes,
}

/// <summary>Port of TS <c>PathMetadata</c>.</summary>
public sealed record PathMetadata
{
    public required string Source { get; init; }

    public required string Scope { get; init; }

    public required string Origin { get; init; }

    public string? BaseDir { get; init; }

    /// <summary>
    /// The package directory a resource came out of, when it came out of a package. TS added this for
    /// <c>collectExtensionPackageWarnings</c>, which reads the package's own <c>package.json</c>.
    /// </summary>
    public string? PackageRoot { get; init; }
}

/// <summary>Port of TS <c>ResolvedResource</c>.</summary>
public sealed record ResolvedResource
{
    public required string Path { get; init; }

    public required PathMetadata Metadata { get; init; }

    public required bool Enabled { get; init; }
}

/// <summary>Port of TS <c>ResolvedPaths</c>.</summary>
public sealed record ResolvedResources
{
    public IReadOnlyList<ResolvedResource> Extensions { get; init; } = [];

    public IReadOnlyList<ResolvedResource> Skills { get; init; } = [];

    public IReadOnlyList<ResolvedResource> Prompts { get; init; } = [];

    public IReadOnlyList<ResolvedResource> Themes { get; init; } = [];

    /// <summary>配置了但装不上的包（注册表不可达、包不存在、git prepare 失败等）。</summary>
    public IReadOnlyList<PackageInstallFailure> InstallFailures { get; init; } = [];
}

/// <summary>Port of TS <c>PackageInstallFailure</c>.</summary>
public sealed record PackageInstallFailure
{
    public required string Source { get; init; }

    public required string Scope { get; init; }

    public required string Message { get; init; }
}

/// <summary>Port of TS <c>ConfiguredPackage</c>.</summary>
public sealed record ConfiguredPackage
{
    public required string Source { get; init; }

    public required string Scope { get; init; }

    /// <summary>TS <c>filtered</c>: the entry is the object form, so it carries a filter.</summary>
    public required bool Filtered { get; init; }

    public string? InstalledPath { get; init; }
}

/// <summary>One available package update. Port of TS <c>PackageUpdate</c>.</summary>
public sealed record PackageUpdate(string Source, string DisplayName, string Type, string Scope);

/// <summary>What to do about a package source that is not installed. Port of TS <c>MissingSourceAction</c>.</summary>
public static class MissingSourceAction
{
    public const string Install = "install";

    public const string Skip = "skip";

    public const string Error = "error";
}

/// <summary>Port of the TS <c>PackageManager</c> interface, narrowed to what the port implements.</summary>
public interface IPackageManager
{
    Task<ResolvedResources> ResolveAsync(CancellationToken cancellationToken = default);

    Task<ResolvedResources> ResolveAsync(
        Func<string, Task<string>> onMissing,
        CancellationToken cancellationToken = default);

    Task<ResolvedResources> ResolveExtensionSourcesAsync(
        IReadOnlyList<string> sources,
        bool local = false,
        bool temporary = false,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(string? source = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PackageUpdate>> CheckForAvailableUpdatesAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<ConfiguredPackage> ListConfiguredPackages();

    string? GetInstalledPath(string source, string scope);

    bool AddSourceToSettings(string source, bool local = false);

    bool RemoveSourceFromSettings(string source, bool local = false);

    void SetProgressCallback(Action<PackageProgressEvent>? callback);
}

/// <summary>Port of TS <c>ProgressEvent</c>.</summary>
public sealed record PackageProgressEvent(string Type, string Action, string Source, string? Message = null);

/// <summary>Working directory, timeout, and environment overlay for a package process.</summary>
public sealed record PackageProcessOptions
{
    public string? WorkingDirectory { get; init; }

    public int? TimeoutMs { get; init; }

    public IReadOnlyDictionary<string, string?>? Env { get; init; }
}

/// <summary>npm/git 进程注入点。测试替换为假实现，避免真实网络。</summary>
public interface IPackageCommandRunner
{
    Task<string> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        PackageProcessOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>默认在线程池上用 <see cref="ChildProcess.SpawnSync"/> 跑 npm/git。</summary>
public sealed class DefaultPackageCommandRunner : IPackageCommandRunner
{
    public Task<string> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        PackageProcessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PackageProcessOptions();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ChildProcess.SpawnSync(command, arguments, new SpawnSyncOptions
            {
                Cwd = options.WorkingDirectory,
                TimeoutMs = options.TimeoutMs,
                Env = options.Env,
            });
            if (result.Error is not null)
            {
                throw result.Error;
            }

            if (result.Status is not 0)
            {
                throw new InvalidOperationException($"{command} exited {result.Status}: {result.Stderr}");
            }

            return result.Stdout;
        });
    }
}

/// <summary>
/// 包管理器。对应 TS <c>core/package-manager.ts</c> 的 <c>DefaultPackageManager</c>。
/// </summary>
/// <remarks>
/// <para>
/// 4e-3c-1 落地了 <c>parseSource</c> 与一个简化版 <c>resolve()</c>（只有自动发现、settings 路径与内置扩展，
/// 且用一套端口臆造的 <c>include</c>/<c>exclude</c>/<c>autoload</c> settings 键做过滤）。
/// 4e-3c-2a 补齐了模块级叶子（patterns / collectors / globSync / semver range）。
/// 本批 4e-3c-2b 把 <c>resolve()</c> 重写为 TS 的真实形状：<c>resolvePackageSources</c>、
/// <c>dedupePackages</c>、<c>collectPackageResources</c> 与完整的 <c>+</c>/<c>-</c>/<c>!</c> override 语义，
/// 并给 <see cref="PathMetadata"/> 补上 <c>PackageRoot</c>。臆造的 settings 键同时删除。
/// </para>
/// <para>
/// npm/git 更新和可用更新检查由 <see cref="DefaultPackageManager.Updates"/> 实现；
/// 该分部还保留 legacy 全局 npm/pnpm 安装路径回退。
/// </para>
/// </remarks>
public sealed partial class DefaultPackageManager : IPackageManager
{
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
        _cwd = ResolvePath(cwd);
        _agentDir = ResolvePath(agentDir);
        _builtinExtensions = builtinExtensions ?? [];
        _runner = commandRunner ?? new DefaultPackageCommandRunner();
    }

    public void SetProgressCallback(Action<PackageProgressEvent>? callback) => _progress = callback;

    // ========================================================================
    // resolve（TS resolve / resolvePackageSources / dedupePackages）
    // ========================================================================

    public Task<ResolvedResources> ResolveAsync(CancellationToken cancellationToken = default)
        => ResolveAsync(null, cancellationToken);

    public async Task<ResolvedResources> ResolveAsync(
        Func<string, Task<string>>? onMissing,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accumulator = new Accumulator();
        var installFailures = new List<PackageInstallFailure>();

        // Collect all packages with scope (project first so cwd resources win collisions).
        var allPackages = new List<PackageEntry>();
        foreach (var pkg in ProjectPackageNodes())
        {
            allPackages.Add(new PackageEntry(pkg, SourceScope.Project));
        }

        foreach (var pkg in GlobalPackageNodes())
        {
            allPackages.Add(new PackageEntry(pkg, SourceScope.User));
        }

        // Dedupe: project scope wins over global for the same package identity.
        var packageSources = DedupePackages(allPackages);
        await ResolvePackageSourcesAsync(packageSources, accumulator, onMissing, installFailures, cancellationToken)
            .ConfigureAwait(false);

        var globalBaseDir = _agentDir;
        var projectBaseDir = NodePath.Join(_cwd, Config.ConfigDirName);

        foreach (var resourceType in PackageManagerPatterns.ResourceTypes)
        {
            var target = GetTargetMap(accumulator, resourceType);
            ResolveLocalEntries(
                ProjectEntries(resourceType),
                resourceType,
                target,
                new PathMetadata { Source = "local", Scope = SourceScope.Project, Origin = SourceOrigin.TopLevel },
                projectBaseDir);
            ResolveLocalEntries(
                GlobalEntries(resourceType),
                resourceType,
                target,
                new PathMetadata { Source = "local", Scope = SourceScope.User, Origin = SourceOrigin.TopLevel },
                globalBaseDir);
        }

        AddAutoDiscoveredResources(accumulator, globalBaseDir, projectBaseDir);

        // Built-in extensions are enabled unless the user `extensions` setting excludes them, for
        // example with `-builtin:mcp`. A matching `+`, `-`, or `!` entry in the project setting
        // overrides that.
        foreach (var name in _builtinExtensions)
        {
            var path = SourceInfos.BuiltinPathPrefix + name;
            var projectEnabled = PackageManagerPatterns.ApplyAutoloadDisabledPatterns(
                [path],
                PackageManagerPatterns.GetOverridePatterns(ProjectEntries("extensions")),
                projectBaseDir).TryGetValue(path, out var value)
                ? value
                : (bool?)null;
            AddResource(
                accumulator,
                "extensions",
                path,
                new PathMetadata
                {
                    Source = "builtin",
                    Scope = projectEnabled is null ? SourceScope.User : SourceScope.Project,
                    Origin = SourceOrigin.TopLevel,
                },
                projectEnabled
                    ?? PackageManagerPatterns.IsEnabledByOverrides(path, GlobalEntries("extensions"), globalBaseDir));
        }

        return ToResolvedPaths(accumulator, installFailures);
    }

    /// <summary>TS <c>resolveExtensionSources</c>.</summary>
    public async Task<ResolvedResources> ResolveExtensionSourcesAsync(
        IReadOnlyList<string> sources,
        bool local = false,
        bool temporary = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accumulator = new Accumulator();
        var installFailures = new List<PackageInstallFailure>();
        var scope = temporary ? SourceScope.Temporary : local ? SourceScope.Project : SourceScope.User;

        // `-e builtin:<name>` loads a built-in extension; the resource loader reports unknown names.
        foreach (var source in sources)
        {
            if (source.StartsWith(SourceInfos.BuiltinPathPrefix, StringComparison.Ordinal))
            {
                AddResource(
                    accumulator,
                    "extensions",
                    source,
                    new PathMetadata { Source = "builtin", Scope = scope, Origin = SourceOrigin.TopLevel },
                    true);
            }
        }

        var packageSources = new List<PackageEntry>();
        foreach (var source in sources)
        {
            if (!source.StartsWith(SourceInfos.BuiltinPathPrefix, StringComparison.Ordinal))
            {
                packageSources.Add(new PackageEntry(JsonValue.Create(source), scope));
            }
        }

        await ResolvePackageSourcesAsync(packageSources, accumulator, null, installFailures, cancellationToken)
            .ConfigureAwait(false);
        return ToResolvedPaths(accumulator, installFailures);
    }

    /// <summary>TS <c>listConfiguredPackages</c>.</summary>
    public IReadOnlyList<ConfiguredPackage> ListConfiguredPackages()
    {
        var configured = new List<ConfiguredPackage>();
        foreach (var pkg in GlobalPackageNodes())
        {
            var source = GetPackageSourceString(pkg) ?? "";
            configured.Add(new ConfiguredPackage
            {
                Source = source,
                Scope = SourceScope.User,
                Filtered = pkg is JsonObject,
                InstalledPath = GetInstalledPath(source, SourceScope.User),
            });
        }

        foreach (var pkg in ProjectPackageNodes())
        {
            var source = GetPackageSourceString(pkg) ?? "";
            configured.Add(new ConfiguredPackage
            {
                Source = source,
                Scope = SourceScope.Project,
                Filtered = pkg is JsonObject,
                InstalledPath = GetInstalledPath(source, SourceScope.Project),
            });
        }

        return configured;
    }

    private async Task ResolvePackageSourcesAsync(
        IReadOnlyList<PackageEntry> sources,
        Accumulator accumulator,
        Func<string, Task<string>>? onMissing,
        List<PackageInstallFailure> installFailures,
        CancellationToken cancellationToken)
    {
        foreach (var entry in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pkg = entry.Package;
            var scope = entry.Scope;
            var sourceStr = GetPackageSourceString(pkg) ?? "";
            var filter = PackageFilterFrom(pkg);
            var deltaBase = FindAutoloadDeltaBase(pkg, scope, sources);
            var resolvedSource = deltaBase?.Source ?? sourceStr;
            var resolvedScope = deltaBase?.Scope ?? scope;
            var parsed = ParseSource(resolvedSource);
            var metadata = new PathMetadata { Source = sourceStr, Scope = scope, Origin = SourceOrigin.Package };
            // onMissing 明确要求报错时不能当成「装不上」吞掉，见下方 TryInstallAsync 的过滤条件。
            var missingSourceRequested = false;

            if (parsed.Type == "local")
            {
                var baseDir = GetBaseDirForScope(resolvedScope);
                ResolveLocalExtensionSource(parsed, accumulator, filter, metadata, baseDir);
                continue;
            }

            async Task<bool> InstallMissingAsync()
            {
                if (IsOfflineModeEnabled())
                {
                    return false;
                }

                if (onMissing is null)
                {
                    await InstallParsedSourceAsync(parsed, resolvedScope, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                var action = await onMissing(resolvedSource).ConfigureAwait(false);
                if (action == MissingSourceAction.Skip)
                {
                    return false;
                }

                if (action == MissingSourceAction.Error)
                {
                    missingSourceRequested = true;
                    throw new InvalidOperationException($"Missing source: {resolvedSource}");
                }

                await InstallParsedSourceAsync(parsed, resolvedScope, cancellationToken).ConfigureAwait(false);
                return true;
            }

            // 装不上一个配错的包不该让整个 pi 起不来：记下失败、跳过这个包，
            // 下次启动或 /reload 会重试（对应上游 #11304）。
            async Task<bool> TryInstallAsync()
            {
                try
                {
                    return await InstallMissingAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException && !missingSourceRequested)
                {
                    installFailures.Add(new PackageInstallFailure
                    {
                        Source = resolvedSource,
                        Scope = resolvedScope,
                        Message = error.Message,
                    });
                    return false;
                }
            }

            if (parsed.Type == "npm")
            {
                var installedPath = GetNpmInstallPath(parsed, resolvedScope);
                var needsInstall = !Directory.Exists(installedPath)
                    || !InstalledNpmMatchesConfiguredVersion(parsed, installedPath);
                if (needsInstall)
                {
                    var installed = await TryInstallAsync().ConfigureAwait(false);
                    if (!installed)
                    {
                        continue;
                    }

                    installedPath = GetNpmInstallPath(parsed, resolvedScope);
                }

                metadata = metadata with { BaseDir = installedPath, PackageRoot = installedPath };
                CollectPackageResources(installedPath, accumulator, filter, metadata);
                continue;
            }

            if (parsed.Type == "git")
            {
                var installedPath = GetGitInstallPath(parsed, resolvedScope);
                if (!Directory.Exists(installedPath))
                {
                    var installed = await TryInstallAsync().ConfigureAwait(false);
                    if (!installed)
                    {
                        continue;
                    }
                }

                else if (resolvedScope == SourceScope.Temporary && !parsed.Pinned && !IsOfflineModeEnabled())
                {
                    await RefreshTemporaryGitSourceAsync(parsed, resolvedSource, cancellationToken)
                        .ConfigureAwait(false);
                }

                metadata = metadata with { BaseDir = installedPath, PackageRoot = installedPath };
                CollectPackageResources(installedPath, accumulator, filter, metadata);
            }
        }
    }

    /// <summary>
    /// TS <c>findAutoloadDeltaBase</c>: a project entry with <c>autoload: false</c> is a *delta* over
    /// the user entry with the same identity, so its resources are collected from the user install.
    /// </summary>
    private (string Source, string Scope)? FindAutoloadDeltaBase(
        JsonNode? pkg,
        string scope,
        IReadOnlyList<PackageEntry> sources)
    {
        if (scope != SourceScope.Project || !PackageAutoloadIsFalse(pkg))
        {
            return null;
        }

        var identity = GetPackageIdentity(GetPackageSourceString(pkg) ?? "", scope);
        foreach (var entry in sources)
        {
            if (entry.Scope != SourceScope.User)
            {
                continue;
            }

            var candidate = GetPackageSourceString(entry.Package) ?? "";
            if (GetPackageIdentity(candidate, SourceScope.User) == identity)
            {
                return (candidate, SourceScope.User);
            }
        }

        return null;
    }

    /// <summary>
    /// TS <c>getPackageIdentity</c>: npm by name, git by host/path (so SSH and HTTPS agree), local by
    /// absolute path.
    /// </summary>
    private string GetPackageIdentity(string source, string? scope)
    {
        var parsed = ParseSource(source);
        if (parsed.Type == "npm")
        {
            return $"npm:{parsed.Name}";
        }

        if (parsed.Type == "git")
        {
            return $"git:{parsed.Host}/{parsed.Path}";
        }

        var path = parsed.Path ?? "";
        return scope is null
            ? $"local:{ResolvePath(path)}"
            : $"local:{ResolvePathFromBase(path, GetBaseDirForScope(scope))}";
    }

    /// <summary>
    /// TS <c>dedupePackages</c>: keep only the project entry for a repeated identity, except that a
    /// project entry with <c>autoload: false</c> is a delta and both are kept (delta first).
    /// </summary>
    private List<PackageEntry> DedupePackages(IReadOnlyList<PackageEntry> packages)
    {
        var result = new List<PackageEntry>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in packages)
        {
            var identity = GetPackageIdentity(GetPackageSourceString(entry.Package) ?? "", entry.Scope);
            if (!seen.TryGetValue(identity, out var index))
            {
                seen[identity] = result.Count;
                result.Add(entry);
                continue;
            }

            var existing = result[index];
            if (existing.Scope == SourceScope.Project && entry.Scope == SourceScope.User)
            {
                if (PackageAutoloadIsFalse(existing.Package))
                {
                    result.Add(entry);
                }
            }
            else if (entry.Scope == SourceScope.Project)
            {
                result[index] = entry;
            }
        }

        return result;
    }

    private void ResolveLocalExtensionSource(
        ParsedPackageSource source,
        Accumulator accumulator,
        PackageFilter? filter,
        PathMetadata metadata,
        string baseDir)
    {
        var resolved = ResolvePathFromBase(source.Path ?? "", baseDir);
        if (File.Exists(resolved))
        {
            AddResource(
                accumulator,
                "extensions",
                resolved,
                metadata with { BaseDir = NodePath.Dirname(resolved) },
                true);
            return;
        }

        if (!Directory.Exists(resolved))
        {
            return;
        }

        var directoryMetadata = metadata with { BaseDir = resolved, PackageRoot = resolved };
        var resources = CollectPackageResources(resolved, accumulator, filter, directoryMetadata);
        if (!resources)
        {
            AddResource(accumulator, "extensions", resolved, directoryMetadata, true);
        }
    }

    // ========================================================================
    // 包内资源采集（TS collectPackageResources 及其下游）
    // ========================================================================

    /// <summary>
    /// TS <c>collectPackageResources</c>. Returns whether anything was contributed, so the caller can
    /// fall back to treating the directory itself as an extension.
    /// </summary>
    private bool CollectPackageResources(
        string packageRoot,
        Accumulator accumulator,
        PackageFilter? filter,
        PathMetadata metadata)
    {
        if (filter is not null)
        {
            foreach (var resourceType in PackageManagerPatterns.ResourceTypes)
            {
                var patterns = filter.PatternsFor(resourceType);
                var target = GetTargetMap(accumulator, resourceType);
                if (filter.Autoload is false)
                {
                    ApplyPackageDeltaFilter(packageRoot, patterns ?? [], resourceType, target, metadata);
                }
                else if (patterns is not null)
                {
                    ApplyPackageFilter(packageRoot, patterns, resourceType, target, metadata);
                }
                else
                {
                    CollectDefaultResources(packageRoot, resourceType, target, metadata);
                }
            }

            return true;
        }

        var manifest = PiManifestReader.ReadPiManifest(NodePath.Join(packageRoot, "package.json"));
        if (manifest is not null)
        {
            foreach (var resourceType in PackageManagerPatterns.ResourceTypes)
            {
                AddManifestEntries(
                    ManifestEntries(manifest, resourceType),
                    packageRoot,
                    resourceType,
                    GetTargetMap(accumulator, resourceType),
                    metadata);
            }

            return true;
        }

        var hasAnyDir = false;
        foreach (var resourceType in PackageManagerPatterns.ResourceTypes)
        {
            var dir = NodePath.Join(packageRoot, resourceType);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            // Collect all files from the directory (all enabled by default).
            foreach (var file in PackageManagerCollectors.CollectResourceFiles(dir, resourceType))
            {
                AddResource(accumulator, resourceType, file, metadata, true);
            }

            hasAnyDir = true;
        }

        return hasAnyDir;
    }

    private void CollectDefaultResources(
        string packageRoot,
        string resourceType,
        ResourceMap target,
        PathMetadata metadata)
    {
        var manifest = PiManifestReader.ReadPiManifest(NodePath.Join(packageRoot, "package.json"));
        var entries = ManifestEntries(manifest, resourceType);
        if (entries is not null)
        {
            AddManifestEntries(entries, packageRoot, resourceType, target, metadata);
            return;
        }

        var dir = NodePath.Join(packageRoot, resourceType);
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in PackageManagerCollectors.CollectResourceFiles(dir, resourceType))
        {
            AddResource(target, file, metadata, true);
        }
    }

    private void ApplyPackageFilter(
        string packageRoot,
        IReadOnlyList<string> userPatterns,
        string resourceType,
        ResourceMap target,
        PathMetadata metadata)
    {
        var (allFiles, _) = CollectManifestFiles(packageRoot, resourceType);

        if (userPatterns.Count == 0)
        {
            // Empty array explicitly disables all resources of this type.
            foreach (var file in allFiles)
            {
                AddResource(target, file, metadata, false);
            }

            return;
        }

        var enabledByUser = PackageManagerPatterns.ApplyPatterns(allFiles, userPatterns, packageRoot);
        foreach (var file in allFiles)
        {
            AddResource(target, file, metadata, enabledByUser.Contains(file));
        }
    }

    private void ApplyPackageDeltaFilter(
        string packageRoot,
        IReadOnlyList<string> userPatterns,
        string resourceType,
        ResourceMap target,
        PathMetadata metadata)
    {
        if (userPatterns.Count == 0)
        {
            return;
        }

        var (allFiles, _) = CollectManifestFiles(packageRoot, resourceType);
        var enabledByUser = PackageManagerPatterns.ApplyAutoloadDisabledPatterns(allFiles, userPatterns, packageRoot);
        foreach (var (filePath, enabled) in enabledByUser)
        {
            AddResource(target, filePath, metadata, enabled);
        }
    }

    /// <summary>
    /// TS <c>collectManifestFiles</c>. Note the returned "all files" list is the manifest-enabled set,
    /// not every file on disk — that is what upstream returns.
    /// </summary>
    private (List<string> AllFiles, HashSet<string> EnabledByManifest) CollectManifestFiles(
        string packageRoot,
        string resourceType)
    {
        var manifest = PiManifestReader.ReadPiManifest(NodePath.Join(packageRoot, "package.json"));
        var entries = ManifestEntries(manifest, resourceType);
        if (entries is { Count: > 0 })
        {
            var allFiles = CollectFilesFromManifestEntries(entries, packageRoot, resourceType);
            var manifestPatterns = PackageManagerPatterns.GetOverridePatterns(entries);
            var enabledByManifest = manifestPatterns.Count > 0
                ? PackageManagerPatterns.ApplyPatterns(allFiles, manifestPatterns, packageRoot)
                : new HashSet<string>(allFiles, StringComparer.Ordinal);
            return ([.. enabledByManifest], enabledByManifest);
        }

        var conventionDir = NodePath.Join(packageRoot, resourceType);
        if (!Directory.Exists(conventionDir))
        {
            return ([], new HashSet<string>(StringComparer.Ordinal));
        }

        var collected = PackageManagerCollectors.CollectResourceFiles(conventionDir, resourceType);
        return (collected, new HashSet<string>(collected, StringComparer.Ordinal));
    }

    private void AddManifestEntries(
        IReadOnlyList<string>? entries,
        string root,
        string resourceType,
        ResourceMap target,
        PathMetadata metadata)
    {
        if (entries is null)
        {
            return;
        }

        var allFiles = CollectFilesFromManifestEntries(entries, root, resourceType);
        var patterns = PackageManagerPatterns.GetOverridePatterns(entries);
        var enabledPaths = PackageManagerPatterns.ApplyPatterns(allFiles, patterns, root);

        foreach (var file in allFiles)
        {
            if (enabledPaths.Contains(file))
            {
                AddResource(target, file, metadata, true);
            }
        }
    }

    private List<string> CollectFilesFromManifestEntries(
        IReadOnlyList<string> entries,
        string root,
        string resourceType)
    {
        var resolved = new List<string>();
        foreach (var entry in entries)
        {
            if (PackageManagerPatterns.IsOverridePattern(entry))
            {
                continue;
            }

            if (!PackageManagerPatterns.HasGlobPattern(entry))
            {
                resolved.Add(NodePath.Resolve(root, entry));
                continue;
            }

            resolved.AddRange(PackageManagerCollectors.ExpandPackageGlob(entry, root));
        }

        return CollectFilesFromPaths(resolved, resourceType);
    }

    private void ResolveLocalEntries(
        IReadOnlyList<string> entries,
        string resourceType,
        ResourceMap target,
        PathMetadata metadata,
        string baseDir)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var (plain, patterns) = PackageManagerPatterns.SplitPatterns(entries);
        var resolvedPlain = new List<string>(plain.Count);
        foreach (var entry in plain)
        {
            resolvedPlain.Add(ResolvePathFromBase(entry, baseDir));
        }

        var allFiles = CollectFilesFromPaths(resolvedPlain, resourceType);
        var enabledPaths = PackageManagerPatterns.ApplyPatterns(allFiles, patterns, baseDir);
        foreach (var file in allFiles)
        {
            AddResource(target, file, metadata, enabledPaths.Contains(file));
        }
    }

    private void AddAutoDiscoveredResources(Accumulator accumulator, string globalBaseDir, string projectBaseDir)
    {
        var userMetadata = new PathMetadata
        {
            Source = "auto",
            Scope = SourceScope.User,
            Origin = SourceOrigin.TopLevel,
            BaseDir = globalBaseDir,
        };
        var projectMetadata = new PathMetadata
        {
            Source = "auto",
            Scope = SourceScope.Project,
            Origin = SourceOrigin.TopLevel,
            BaseDir = projectBaseDir,
        };

        var userOverrides = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["extensions"] = GlobalEntries("extensions"),
            ["skills"] = GlobalEntries("skills"),
            ["prompts"] = GlobalEntries("prompts"),
            ["themes"] = GlobalEntries("themes"),
        };
        var projectOverrides = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["extensions"] = ProjectEntries("extensions"),
            ["skills"] = ProjectEntries("skills"),
            ["prompts"] = ProjectEntries("prompts"),
            ["themes"] = ProjectEntries("themes"),
        };

        var userAgentsSkillsDir = NodePath.Join(Paths.GetHomeDirectory(), ".agents", "skills");
        var projectTrusted = _settings.IsProjectTrusted();
        var projectAgentsSkillDirs = new List<string>();
        if (projectTrusted)
        {
            foreach (var dir in PackageManagerCollectors.CollectAncestorAgentsSkillDirs(_cwd))
            {
                if (NodePath.Resolve(dir) != NodePath.Resolve(userAgentsSkillsDir))
                {
                    projectAgentsSkillDirs.Add(dir);
                }
            }
        }

        void AddResources(
            string resourceType,
            IReadOnlyList<string> paths,
            PathMetadata metadata,
            IReadOnlyList<string> overrides,
            string baseDir)
        {
            var target = GetTargetMap(accumulator, resourceType);
            foreach (var path in paths)
            {
                AddResource(
                    target,
                    path,
                    metadata,
                    PackageManagerPatterns.IsEnabledByOverrides(path, overrides, baseDir));
            }
        }

        if (projectTrusted)
        {
            // Project extensions and skills from .pi/
            AddResources(
                "extensions",
                PackageManagerCollectors.CollectAutoExtensionEntries(NodePath.Join(projectBaseDir, "extensions")),
                projectMetadata,
                projectOverrides["extensions"],
                projectBaseDir);
            AddResources(
                "skills",
                PackageManagerCollectors.CollectAutoSkillEntries(
                    NodePath.Join(projectBaseDir, "skills"),
                    SkillDiscoveryMode.Pi),
                projectMetadata,
                projectOverrides["skills"],
                projectBaseDir);
        }

        // Project skills from .agents/ (each with its own baseDir)
        foreach (var agentsSkillsDir in projectAgentsSkillDirs)
        {
            var agentsBaseDir = NodePath.Dirname(agentsSkillsDir);
            AddResources(
                "skills",
                PackageManagerCollectors.CollectAutoSkillEntries(agentsSkillsDir, SkillDiscoveryMode.Agents),
                projectMetadata with { BaseDir = agentsBaseDir },
                projectOverrides["skills"],
                agentsBaseDir);
        }

        if (projectTrusted)
        {
            AddResources(
                "prompts",
                PackageManagerCollectors.CollectAutoPromptEntries(NodePath.Join(projectBaseDir, "prompts")),
                projectMetadata,
                projectOverrides["prompts"],
                projectBaseDir);
            AddResources(
                "themes",
                PackageManagerCollectors.CollectAutoThemeEntries(NodePath.Join(projectBaseDir, "themes")),
                projectMetadata,
                projectOverrides["themes"],
                projectBaseDir);
        }

        // User resources from ~/.pi/agent/
        AddResources(
            "extensions",
            PackageManagerCollectors.CollectAutoExtensionEntries(NodePath.Join(globalBaseDir, "extensions")),
            userMetadata,
            userOverrides["extensions"],
            globalBaseDir);
        AddResources(
            "skills",
            PackageManagerCollectors.CollectAutoSkillEntries(
                NodePath.Join(globalBaseDir, "skills"),
                SkillDiscoveryMode.Pi),
            userMetadata,
            userOverrides["skills"],
            globalBaseDir);

        // User skills from ~/.agents/ (with its own baseDir)
        var userAgentsBaseDir = NodePath.Dirname(userAgentsSkillsDir);
        AddResources(
            "skills",
            PackageManagerCollectors.CollectAutoSkillEntries(userAgentsSkillsDir, SkillDiscoveryMode.Agents),
            userMetadata with { BaseDir = userAgentsBaseDir },
            userOverrides["skills"],
            userAgentsBaseDir);

        AddResources(
            "prompts",
            PackageManagerCollectors.CollectAutoPromptEntries(NodePath.Join(globalBaseDir, "prompts")),
            userMetadata,
            userOverrides["prompts"],
            globalBaseDir);
        AddResources(
            "themes",
            PackageManagerCollectors.CollectAutoThemeEntries(NodePath.Join(globalBaseDir, "themes")),
            userMetadata,
            userOverrides["themes"],
            globalBaseDir);
    }

    private static List<string> CollectFilesFromPaths(IReadOnlyList<string> paths, string resourceType)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                files.Add(path);
            }
            else if (Directory.Exists(path))
            {
                files.AddRange(PackageManagerCollectors.CollectResourceFiles(path, resourceType));
            }
        }

        return files;
    }

    // ========================================================================
    // 累加器（TS createAccumulator / getTargetMap / addResource / toResolvedPaths）
    // ========================================================================

    /// <summary>
    /// TS's per-type <c>Map&lt;string, {metadata, enabled}&gt;</c>: insertion-ordered, and an add for a
    /// path that is already present is a no-op ("first wins").
    /// </summary>
    private sealed class ResourceMap
    {
        private readonly Dictionary<string, (PathMetadata Metadata, bool Enabled)> _entries =
            new(StringComparer.Ordinal);

        private readonly List<string> _order = [];

        public IReadOnlyList<string> Paths => _order;

        public (PathMetadata Metadata, bool Enabled) this[string path] => _entries[path];

        public bool Contains(string path) => _entries.ContainsKey(path);

        public void Set(string path, PathMetadata metadata, bool enabled)
        {
            _entries[path] = (metadata, enabled);
            _order.Add(path);
        }
    }

    private sealed class Accumulator
    {
        public ResourceMap Extensions { get; } = new();

        public ResourceMap Skills { get; } = new();

        public ResourceMap Prompts { get; } = new();

        public ResourceMap Themes { get; } = new();

        public ResourceMap For(string resourceType) => resourceType switch
        {
            "extensions" => Extensions,
            "skills" => Skills,
            "prompts" => Prompts,
            "themes" => Themes,
            _ => throw new ArgumentException($"Unknown resource type: {resourceType}", nameof(resourceType)),
        };
    }

    private static ResourceMap GetTargetMap(Accumulator accumulator, string resourceType)
        => accumulator.For(resourceType);

    private static void AddResource(
        Accumulator accumulator,
        string resourceType,
        string path,
        PathMetadata metadata,
        bool enabled)
        => AddResource(GetTargetMap(accumulator, resourceType), path, metadata, enabled);

    private static void AddResource(ResourceMap map, string path, PathMetadata metadata, bool enabled)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (!map.Contains(path))
        {
            map.Set(path, metadata, enabled);
        }
    }

    /// <summary>
    /// TS <c>toResolvedPaths</c>: sort by precedence (stable, so ties keep insertion order), then drop
    /// canonical-path duplicates keeping the first.
    /// </summary>
    private static ResolvedResources ToResolvedPaths(
        Accumulator accumulator, IReadOnlyList<PackageInstallFailure>? installFailures = null)
    {
        static List<ResolvedResource> MapToResolved(ResourceMap map)
        {
            var indexed = new List<(string Path, int Index)>(map.Paths.Count);
            for (var i = 0; i < map.Paths.Count; i++)
            {
                indexed.Add((map.Paths[i], i));
            }

            indexed.Sort((left, right) =>
            {
                var rank = PackageManagerPatterns.ResourcePrecedenceRank(map[left.Path].Metadata)
                    - PackageManagerPatterns.ResourcePrecedenceRank(map[right.Path].Metadata);
                return rank != 0 ? rank : left.Index - right.Index;
            });

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var resolved = new List<ResolvedResource>(indexed.Count);
            foreach (var (path, _) in indexed)
            {
                var canonical = Paths.CanonicalizePath(path);
                if (!seen.Add(canonical))
                {
                    continue;
                }

                var entry = map[path];
                resolved.Add(new ResolvedResource { Path = path, Enabled = entry.Enabled, Metadata = entry.Metadata });
            }

            return resolved;
        }

        return new ResolvedResources
        {
            Extensions = MapToResolved(accumulator.Extensions),
            Skills = MapToResolved(accumulator.Skills),
            Prompts = MapToResolved(accumulator.Prompts),
            Themes = MapToResolved(accumulator.Themes),
            InstallFailures = installFailures ?? [],
        };
    }

    // ========================================================================
    // parseSource 与 settings 读写（TS parseSource / addSourceToSettings / …）
    // ========================================================================

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
                Range = version is { Length: > 0 } ? SemverRange.ValidRange(version) : null,
                Pinned = IsExactNpmVersion(version),
            };
        }

        if (Paths.IsLocalPath(spec))
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
        var scope = local ? SourceScope.Project : SourceScope.User;
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
        return true;
    }

    /// <summary>对应 TS <c>removeSourceFromSettings</c>。</summary>
    public bool RemoveSourceFromSettings(string source, bool local = false)
    {
        var scope = local ? SourceScope.Project : SourceScope.User;
        var current = GetPackageNodes(local);
        var next = current.Where(existing => !PackageSourcesMatch(existing, source, scope)).ToList();
        if (next.Count == current.Count)
        {
            return false;
        }

        WritePackageNodes(local, next);
        return true;
    }

    /// <summary>TS <c>getInstalledPath</c>.</summary>
    public string? GetInstalledPath(string source, string scope)
    {
        var parsed = ParseSource(source);
        if (parsed.Type == "npm")
        {
            var path = GetNpmInstallPath(parsed, scope);
            return Directory.Exists(path) ? path : null;
        }

        if (parsed.Type == "git")
        {
            var path = GetGitInstallPath(parsed, scope);
            return Directory.Exists(path) ? path : null;
        }

        if (parsed.Type == "local")
        {
            var path = ResolvePathFromBase(parsed.Path ?? "", GetBaseDirForScope(scope));
            return File.Exists(path) || Directory.Exists(path) ? path : null;
        }

        return null;
    }

    // ========================================================================
    // 安装 / 卸载
    // ========================================================================

    /// <summary>对应 TS <c>install</c>。</summary>
    public async Task<string> InstallAsync(string source, string scope = "user", CancellationToken cancellationToken = default)
    {
        var parsed = ParseSource(source);
        AssertProjectTrustedForScope(scope);
        return await WithProgressAsync("install", source, $"Installing {source}...", async () =>
        {
            if (parsed.Type == "npm")
            {
                return await InstallNpmAsync(parsed, scope, cancellationToken).ConfigureAwait(false);
            }

            if (parsed.Type == "git")
            {
                return await InstallGitAsync(parsed, scope, cancellationToken).ConfigureAwait(false);
            }

            return InstallLocal(parsed);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>对应 TS <c>installAndPersist</c>。</summary>
    public async Task<string> InstallAndPersistAsync(string source, bool local = false, CancellationToken cancellationToken = default)
    {
        var path = await InstallAsync(source, local ? SourceScope.Project : SourceScope.User, cancellationToken)
            .ConfigureAwait(false);
        AddSourceToSettings(source, local);
        return path;
    }

    /// <summary>对应 TS <c>removeAndPersist</c> 的 settings 侧；进程卸载经 runner。</summary>
    public async Task<bool> RemoveAndPersistAsync(string source, bool local = false, CancellationToken cancellationToken = default)
    {
        var scope = local ? SourceScope.Project : SourceScope.User;
        var parsed = ParseSource(source);
        AssertProjectTrustedForScope(scope);
        return await WithProgressAsync("remove", source, $"Removing {source}...", async () =>
        {
            if (parsed.Type == "npm")
            {
                var root = GetNpmInstallRoot(scope, temporary: false);
                if (Directory.Exists(root))
                {
                    await RunNpmCommandAsync(
                        GetNpmUninstallArgs(parsed.Name ?? source, root),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            else if (parsed.Type == "git")
            {
                await RemoveGitAsync(parsed, scope).ConfigureAwait(false);
            }

            return RemoveSourceFromSettings(source, local);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> InstallNpmAsync(ParsedPackageSource source, string scope, CancellationToken cancellationToken)
    {
        var installRoot = GetNpmInstallRoot(scope, temporary: scope == SourceScope.Temporary);
        EnsureNpmProject(installRoot);
        var spec = source.Spec.Length > 0 ? source.Spec : source.Name ?? "";
        await RunNpmCommandAsync(
            GetNpmInstallArgs([spec], installRoot),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return NodePath.Join(installRoot, "node_modules", source.Name ?? spec);
    }

    private async Task<string> InstallGitAsync(ParsedPackageSource source, string scope, CancellationToken cancellationToken)
    {
        var dest = GetGitInstallPath(source, scope);
        if (Directory.Exists(dest))
        {
            if (!string.IsNullOrEmpty(source.Ref))
            {
                await EnsureGitRefAsync(dest, ["fetch", "origin", source.Ref], "FETCH_HEAD", cancellationToken)
                    .ConfigureAwait(false);
                return dest;
            }

            var target = await GetLocalGitUpdateTargetAsync(dest, cancellationToken).ConfigureAwait(false);
            await EnsureGitRefAsync(dest, target.FetchArgs, target.Ref, cancellationToken).ConfigureAwait(false);
            return dest;
        }

        var gitRoot = GetGitInstallRoot(scope);
        if (gitRoot is not null)
        {
            EnsureGitIgnore(gitRoot);
        }

        Directory.CreateDirectory(NodePath.Dirname(dest));
        File.Delete(GetGitUpdateMarkerPath(dest));
        try
        {
            await RunCommandAsync("git", ["clone", source.Locator, dest], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(source.Ref))
            {
                await RunCommandAsync("git", ["checkout", source.Ref], dest, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            if (File.Exists(NodePath.Join(dest, "package.json")))
            {
                await RunNpmCommandAsync(
                    GetGitDependencyInstallArgs(),
                    dest,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (Directory.Exists(dest))
            {
                Directory.Delete(dest, recursive: true);
            }

            PruneEmptyGitParents(dest, gitRoot);
            throw;
        }

        return dest;
    }

    private string InstallLocal(ParsedPackageSource source)
    {
        var resolved = ResolvePath(source.Path ?? source.Spec);
        if (!File.Exists(resolved) && !Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"Path does not exist: {resolved}");
        }

        return resolved;
    }

    private async Task InstallParsedSourceAsync(ParsedPackageSource parsed, string scope, CancellationToken cancellationToken)
    {
        if (parsed.Type == "npm")
        {
            await InstallNpmAsync(parsed, scope, cancellationToken).ConfigureAwait(false);
        }
        else if (parsed.Type == "git")
        {
            await InstallGitAsync(parsed, scope, cancellationToken).ConfigureAwait(false);
        }
    }

    // ========================================================================
    // 安装路径（TS getNpmInstallRoot / getManagedNpmInstallPath / getGitInstallPath / …）
    // ========================================================================

    /// <summary>TS <c>getExtensionTempFolder</c>.</summary>
    internal static string GetExtensionTempFolder(string agentDir)
    {
        var tempFolder = NodePath.Join(agentDir, "tmp", "extensions");
        Directory.CreateDirectory(tempFolder);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                tempFolder,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return tempFolder;
    }

    internal string GetNpmInstallRoot(string scope, bool temporary)
    {
        if (temporary)
        {
            return GetTemporaryDir("npm");
        }

        if (scope == SourceScope.Project)
        {
            AssertProjectTrustedForScope(scope);
            return NodePath.Join(_cwd, Config.ConfigDirName, "npm");
        }

        return NodePath.Join(_agentDir, "npm");
    }

    internal string GetManagedNpmInstallPath(ParsedPackageSource source, string scope)
    {
        var name = source.Name ?? "package";
        if (scope == SourceScope.Temporary)
        {
            return NodePath.Join(GetTemporaryDir("npm"), "node_modules", name);
        }

        if (scope == SourceScope.Project)
        {
            AssertProjectTrustedForScope(scope);
            return NodePath.Join(_cwd, Config.ConfigDirName, "npm", "node_modules", name);
        }

        return NodePath.Join(_agentDir, "npm", "node_modules", name);
    }

    /// <summary>
    /// TS <c>getNpmInstallPath</c>: user-scope packages fall back to the legacy global npm root
    /// (or pnpm's global package path) when the managed path is absent.
    /// </summary>
    internal string GetNpmInstallPath(ParsedPackageSource source, string scope)
    {
        var managedPath = GetManagedNpmInstallPath(source, scope);
        if (scope != SourceScope.User || Directory.Exists(managedPath))
        {
            return managedPath;
        }

        var legacyPath = GetLegacyGlobalNpmInstallPath(source);
        return legacyPath is not null && Directory.Exists(legacyPath) ? legacyPath : managedPath;
    }

    /// <summary>TS <c>getGitInstallPath</c>.</summary>
    internal string GetGitInstallPath(ParsedPackageSource source, string scope)
    {
        var host = source.Host ?? "git";
        var path = source.Path ?? "repo";
        if (scope == SourceScope.Temporary)
        {
            // Include the ref in the hash so each pinned ref gets its own checkout.
            return GetTemporaryDir($"git-{host}", path, source.Ref);
        }

        var installRoot = GetGitInstallRoot(scope) ?? throw new InvalidOperationException("Missing git install root");
        return ResolveManagedPath(installRoot, host, path);
    }

    private string? GetGitInstallRoot(string scope)
    {
        if (scope == SourceScope.Temporary)
        {
            return null;
        }

        if (scope == SourceScope.Project)
        {
            AssertProjectTrustedForScope(scope);
            return NodePath.Join(_cwd, Config.ConfigDirName, "git");
        }

        return NodePath.Join(_agentDir, "git");
    }

    /// <summary>TS <c>getTemporaryDir</c>: a managed root, plus an 8-hex-character sha256 of the inputs.</summary>
    internal string GetTemporaryDir(string prefix, string? suffix = null, string? reference = null)
    {
        var root = ResolveManagedPath(GetExtensionTempFolder(_agentDir), prefix);
        var input = $"{prefix}-{suffix ?? ""}{(reference is not null ? $"@{reference}" : "")}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..8];
        return ResolveManagedPath(root, hash, suffix ?? "");
    }

    /// <summary>TS <c>resolveManagedPath</c>: refuse anything that escapes the install root.</summary>
    private static string ResolveManagedPath(string root, params string[] parts)
    {
        var resolvedRoot = NodePath.Resolve(root);
        var resolvedPath = NodePath.Resolve([resolvedRoot, .. parts]);
        if (!string.Equals(resolvedPath, resolvedRoot, StringComparison.Ordinal)
            && !resolvedPath.StartsWith(resolvedRoot + NodePath.Separator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to use path outside package install root: {resolvedPath}");
        }

        return resolvedPath;
    }

    private void EnsureNpmProject(string installRoot)
    {
        if (!Directory.Exists(installRoot))
        {
            Directory.CreateDirectory(installRoot);
        }

        Paths.MarkPathIgnoredByCloudSync(installRoot);
        EnsureGitIgnore(installRoot);
        var packageJsonPath = NodePath.Join(installRoot, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            File.WriteAllText(packageJsonPath, "{\n  \"name\": \"pi-extensions\",\n  \"private\": true\n}");
        }
    }

    private static void EnsureGitIgnore(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var ignorePath = NodePath.Join(dir, ".gitignore");
        if (!File.Exists(ignorePath))
        {
            File.WriteAllText(ignorePath, "*\n!.gitignore\n");
        }
    }

    // ========================================================================
    // 小工具
    // ========================================================================

    /// <summary>TS <c>isOfflineModeEnabled</c>.</summary>
    private static bool IsOfflineModeEnabled()
    {
        var value = Environment.GetEnvironmentVariable("PI_OFFLINE");
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value == "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>TS <c>installedNpmMatchesConfiguredVersion</c>.</summary>
    private static bool InstalledNpmMatchesConfiguredVersion(ParsedPackageSource source, string installedPath)
    {
        var installedVersion = GetInstalledNpmVersion(installedPath);
        if (installedVersion is null)
        {
            return false;
        }

        return source.Range is null || SemverRange.Satisfies(installedVersion, source.Range);
    }

    /// <summary>TS <c>getInstalledNpmVersion</c>: the <c>version</c> of the installed package.json.</summary>
    private static string? GetInstalledNpmVersion(string installedPath)
    {
        var packageJsonPath = NodePath.Join(installedPath, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return null;
        }

        try
        {
            var parsed = JsonNode.Parse(Text.StripBom(File.ReadAllText(packageJsonPath)));
            return parsed is JsonObject obj && obj["version"] is JsonValue value && value.TryGetValue(out string? version)
                ? version
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void AssertProjectTrustedForScope(string scope)
    {
        if (scope == SourceScope.Project && !_settings.IsProjectTrusted())
        {
            throw new InvalidOperationException("Project is not trusted; refusing to access project package storage");
        }
    }

    private string GetBaseDirForScope(string scope)
    {
        if (scope == SourceScope.Project)
        {
            AssertProjectTrustedForScope(scope);
            return NodePath.Join(_cwd, Config.ConfigDirName);
        }

        return scope == SourceScope.User ? _agentDir : _cwd;
    }

    private string ResolvePath(string input)
        => Paths.ResolvePath(input, _cwd, new PathInputOptions(Trim: true, HomeDir: Paths.GetHomeDirectory()));

    private string ResolvePathFromBase(string input, string baseDir)
        => Paths.ResolvePath(input, baseDir, new PathInputOptions(Trim: true, HomeDir: Paths.GetHomeDirectory()));

    private (string Command, string[] Args) GetNpmInvocation()
    {
        var custom = _settings.GetNpmCommand();
        if (custom is { Count: > 0 })
        {
            var command = custom[0];
            if (string.IsNullOrEmpty(command))
            {
                throw new InvalidOperationException("Invalid npmCommand: first array entry must be a non-empty command");
            }

            return (command, custom.Skip(1).ToArray());
        }

        return ("npm", []);
    }

    private IReadOnlyList<JsonNode?> GlobalPackageNodes() => _settings.GetGlobalSettings().Packages ?? [];

    private IReadOnlyList<JsonNode?> ProjectPackageNodes() => _settings.GetProjectSettings().Packages ?? [];

    private IReadOnlyList<string> GlobalEntries(string resourceType) => resourceType switch
    {
        "extensions" => _settings.GetGlobalExtensionPaths(),
        "skills" => _settings.GetGlobalSkillPaths(),
        "prompts" => _settings.GetGlobalPromptTemplatePaths(),
        "themes" => _settings.GetGlobalThemePaths(),
        _ => throw new ArgumentException($"Unknown resource type: {resourceType}", nameof(resourceType)),
    };

    private IReadOnlyList<string> ProjectEntries(string resourceType) => resourceType switch
    {
        "extensions" => _settings.GetProjectExtensionPaths(),
        "skills" => _settings.GetProjectSkillPaths(),
        "prompts" => _settings.GetProjectPromptTemplatePaths(),
        "themes" => _settings.GetProjectThemePaths(),
        _ => throw new ArgumentException($"Unknown resource type: {resourceType}", nameof(resourceType)),
    };

    private List<JsonNode?> GetPackageNodes(bool local)
        => [.. local ? ProjectPackageNodes() : GlobalPackageNodes()];

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

    /// <summary>TS <c>normalizePackageSourceForSettings</c>.</summary>
    private string NormalizePackageSourceForSettings(string source, string scope)
    {
        var parsed = ParseSource(source);
        if (parsed.Type != "local")
        {
            return source;
        }

        var baseDir = GetBaseDirForScope(scope);
        var resolved = ResolvePath(parsed.Path ?? "");
        var rel = NodePath.Relative(baseDir, resolved);
        return rel.Length > 0 ? rel : ".";
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

        return node is JsonObject obj && obj["source"] is JsonValue sourceValue
            && sourceValue.TryGetValue<string>(out var source)
                ? source
                : null;
    }

    /// <summary>TS <c>pkg.autoload !== false</c> is the early-out; this is the "is exactly false" test.</summary>
    private static bool PackageAutoloadIsFalse(JsonNode? pkg)
        => pkg is JsonObject obj
            && obj["autoload"] is JsonValue value
            && value.TryGetValue(out bool autoload)
            && !autoload;

    private static PackageFilter? PackageFilterFrom(JsonNode? pkg)
    {
        if (pkg is not JsonObject obj)
        {
            return null;
        }

        return new PackageFilter
        {
            Autoload = obj["autoload"] is JsonValue autoload && autoload.TryGetValue(out bool flag) ? flag : null,
            Extensions = StringArrayOrNull(obj["extensions"]),
            Skills = StringArrayOrNull(obj["skills"]),
            Prompts = StringArrayOrNull(obj["prompts"]),
            Themes = StringArrayOrNull(obj["themes"]),
        };
    }

    private static IReadOnlyList<string>? StringArrayOrNull(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out string? text) && text is not null)
            {
                values.Add(text);
            }
        }

        return values;
    }

    private static IReadOnlyList<string>? ManifestEntries(PiManifest? manifest, string resourceType)
        => manifest is null
            ? null
            : resourceType switch
            {
                "extensions" => manifest.Extensions,
                "skills" => manifest.Skills,
                "prompts" => manifest.Prompts,
                "themes" => manifest.Themes,
                _ => null,
            };

    private static (string Name, string? Version) ParseNpmSpec(string spec)
    {
        if (spec.StartsWith('@'))
        {
            var slash = spec.IndexOf('/');
            var at = spec.LastIndexOf('@');
            if (slash >= 0 && at > slash)
            {
                return (spec[..at], spec[(at + 1)..]);
            }

            return (spec, null);
        }

        var idx = spec.IndexOf('@');
        return idx > 0 ? (spec[..idx], spec[(idx + 1)..]) : (spec, null);
    }

    /// <summary>TS <c>isExactNpmVersion</c>: <c>valid(version) !== null</c>.</summary>
    private static bool IsExactNpmVersion(string? version) => Semver.Valid(version) is not null;

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

    internal sealed record ParsedPackageSource
    {
        public required string Type { get; init; }

        public string Spec { get; init; } = "";

        public string Locator { get; init; } = "";

        public string? Name { get; init; }

        public string? Version { get; init; }

        /// <summary>TS <c>range</c>: <c>validRange(version)</c>, or null for an exact version or none.</summary>
        public string? Range { get; init; }

        public string? Path { get; init; }

        public string? Host { get; init; }

        public string? Ref { get; init; }

        public bool Pinned { get; init; }
    }
}
