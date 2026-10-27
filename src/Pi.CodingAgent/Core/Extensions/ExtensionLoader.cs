// ============================================================================
// Extension loader — port of core/extensions/loader.ts (4d-2b)
// ============================================================================
//
// The TS loader compiles extension modules with jiti at runtime. Per decision D2 of
// docs/4d-extension-system-plan.md the .NET port loads them as assemblies; until the
// AssemblyLoadContext implementation lands with batch 4d-3 (built-in extension registration),
// module loading sits behind <see cref="IExtensionModuleLoader"/> so every other behaviour —
// runtime stubs, the registration API, commit/discard, the module cache, discovery and error
// aggregation — is already exact. `LoadExtensionFromFactory` needs no module loader at all,
// exactly like the TS regression tests that drive the loader through inline factories.
//
// Not ported from loader.ts (TS-runtime machinery with no .NET counterpart):
//   - getCreateJiti / jiti-loader / jiti-static-loader / virtual-modules (jiti + embedded modules),
//   - getAliases (ESM specifier aliases for the jiti resolver).

using System.Text.Json.Nodes;
using Pi.CodingAgent.Core.Extensions.Js;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core.Extensions;

/// <summary>
/// Loads one extension module and returns its factory, or null when the module exports none.
/// The .NET counterpart of the TS jiti import (decision D2); the AssemblyLoadContext
/// implementation lands with batch 4d-3.
/// </summary>
public interface IExtensionModuleLoader
{
    /// <summary>Load the module at <paramref name="extensionPath"/> and return its default-export factory.</summary>
    ValueTask<ExtensionFactory?> LoadAsync(string extensionPath, CancellationToken cancellationToken = default);
}

/// <summary>Port of <c>core/extensions/loader.ts</c>.</summary>
public static class ExtensionLoader
{
    /// <summary>Default path for inline factories. TS <c>loadExtensionFromFactory</c> default.</summary>
    public const string InlineExtensionPath = "<inline>";

    /// <summary>Extension path used when a registration does not name one. TS default.</summary>
    public const string UnknownExtensionPath = "<unknown>";

    internal const string StaleMessage =
        "This extension ctx is stale after session replacement or reload. Do not use a captured pi or command ctx after ctx.newSession(), ctx.fork(), ctx.switchSession(), or ctx.reload(). For newSession, fork, and switchSession, move post-replacement work into withSession and use the ctx passed to withSession. For reload, do not use the old ctx after await ctx.reload().";

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, ExtensionFactory> ExtensionCache = new(StringComparer.Ordinal);
    private static string? ExtensionCacheCwd;
    private static int ExtensionCacheGeneration;

    // ------------------------------------------------------------------ cache

    /// <summary>Drop every cached module factory and invalidate outstanding cache tokens.</summary>
    public static void ClearExtensionCache()
    {
        lock (CacheGate)
        {
            ExtensionCache.Clear();
            ExtensionCacheCwd = null;
            ExtensionCacheGeneration++;
        }
    }

    private static ExtensionCacheToken UseExtensionCacheCwd(string cwd)
    {
        var resolvedCwd = Paths.ResolvePath(cwd);
        lock (CacheGate)
        {
            if (ExtensionCacheCwd is not null && ExtensionCacheCwd != resolvedCwd)
            {
                ClearExtensionCache();
            }

            ExtensionCacheCwd = resolvedCwd;
            return new ExtensionCacheToken(resolvedCwd, ExtensionCacheGeneration);
        }
    }

    private static bool IsCurrentCacheToken(ExtensionCacheToken? token)
    {
        lock (CacheGate)
        {
            return token is not null
                && ExtensionCacheCwd == token.Cwd
                && ExtensionCacheGeneration == token.Generation;
        }
    }

    internal sealed record ExtensionCacheToken(string Cwd, int Generation);

    // ------------------------------------------------------------------ runtime

    /// <summary>
    /// Create a runtime with throwing stubs for action methods. The runner replaces these with
    /// real implementations when it binds. Port of <c>createExtensionRuntime</c>.
    /// </summary>
    public static IExtensionRuntime CreateExtensionRuntime() => new ExtensionRuntimeImpl();

    // ------------------------------------------------------------------ API

    /// <summary>
    /// Create the <see cref="IExtensionApi"/> for one extension. Registration methods write to the
    /// extension object; action methods delegate to the shared runtime. Port of
    /// <c>createExtensionAPI</c>; <see cref="ExtensionApiHandle.Commit"/> / <see cref="ExtensionApiHandle.Discard"/>
    /// are the TS <c>commit</c> / <c>discard</c> pair.
    /// </summary>
    internal static ExtensionApiHandle CreateExtensionApi(
        Extension extension,
        IExtensionRuntime runtime,
        string cwd,
        EventBus eventBus)
    {
        var api = new ExtensionApiImpl(extension, runtime, cwd, eventBus);
        return new ExtensionApiHandle(api, api.Commit, api.Discard);
    }

    internal sealed record ExtensionApiHandle(IExtensionApi Api, Action Commit, Action Discard);

    // ------------------------------------------------------------------ module loading

    private static async ValueTask<ExtensionFactory?> LoadExtensionModule(
        string resolvedPath,
        ExtensionCacheToken? cacheToken,
        IExtensionModuleLoader moduleLoader,
        CancellationToken cancellationToken)
    {
        if (IsCurrentCacheToken(cacheToken))
        {
            lock (CacheGate)
            {
                if (ExtensionCache.TryGetValue(resolvedPath, out var cached))
                {
                    return cached;
                }
            }
        }

        var factory = await moduleLoader.LoadAsync(resolvedPath, cancellationToken);
        if (factory is null)
        {
            return null;
        }

        if (IsCurrentCacheToken(cacheToken))
        {
            lock (CacheGate)
            {
                ExtensionCache[resolvedPath] = factory;
            }
        }

        return factory;
    }

    // ------------------------------------------------------------------ extension lifecycle

    /// <summary>Create an <see cref="Extension"/> with empty collections. Port of <c>createExtension</c>.</summary>
    internal static Extension CreateExtension(string extensionPath, string resolvedPath)
    {
        var source = SourceInfos.GetSyntheticPathSource(extensionPath) ?? "local";
        var baseDir = SourceInfos.IsSyntheticPath(extensionPath) ? null : NodePath.Dirname(resolvedPath);

        return new Extension
        {
            Path = extensionPath,
            ResolvedPath = resolvedPath,
            SourceInfo = SourceInfos.CreateSynthetic(extensionPath, source, baseDir: baseDir),
        };
    }

    internal static async Task<Extension> InitializeExtension(
        ExtensionFactory factory,
        string extensionPath,
        string resolvedPath,
        string cwd,
        EventBus eventBus,
        IExtensionRuntime runtime)
    {
        var extension = CreateExtension(extensionPath, resolvedPath);
        var load = CreateExtensionApi(extension, runtime, cwd, eventBus);
        try
        {
            await factory(load.Api);
            load.Commit();
        }
        catch
        {
            load.Discard();
            throw;
        }

        Timings.Time($"{extensionPath} factory", Timings.Namespaces.Extensions);
        return extension;
    }

    internal static async Task<(Extension? Extension, string? Error)> LoadExtension(
        string extensionPath,
        string cwd,
        EventBus eventBus,
        IExtensionRuntime runtime,
        ExtensionCacheToken? cacheToken,
        IExtensionModuleLoader moduleLoader,
        CancellationToken cancellationToken)
    {
        var resolvedPath = Paths.ResolvePath(extensionPath, cwd, new PathInputOptions(NormalizeUnicodeSpaces: true));

        try
        {
            var factory = await LoadExtensionModule(resolvedPath, cacheToken, moduleLoader, cancellationToken);
            Timings.Time($"{extensionPath} module import", Timings.Namespaces.Extensions);
            if (factory is null)
            {
                return (null, $"Extension does not export a valid factory function: {extensionPath}");
            }

            var extension = await InitializeExtension(factory, extensionPath, resolvedPath, cwd, eventBus, runtime);
            return (extension, null);
        }
        catch (Exception error)
        {
            return (null, $"Failed to load extension: {error.Message}");
        }
    }

    /// <summary>Create an <see cref="Extension"/> from an inline factory function. Port of <c>loadExtensionFromFactory</c>.</summary>
    public static async Task<Extension> LoadExtensionFromFactory(
        ExtensionFactory factory,
        string cwd,
        EventBus eventBus,
        IExtensionRuntime runtime,
        string extensionPath = InlineExtensionPath)
    {
        var resolvedCwd = Paths.ResolvePath(cwd);
        return await InitializeExtension(factory, extensionPath, extensionPath, resolvedCwd, eventBus, runtime);
    }

    // ------------------------------------------------------------------ loading entry points

    /// <summary>Load extensions from paths. Port of <c>loadExtensions</c>.</summary>
    public static Task<LoadExtensionsResult> LoadExtensions(
        IReadOnlyList<string> paths,
        string cwd,
        EventBus? eventBus = null,
        IExtensionRuntime? runtime = null,
        IExtensionModuleLoader? moduleLoader = null,
        CancellationToken cancellationToken = default) =>
        LoadExtensionsInternal(paths, cwd, eventBus, runtime, useCache: false, moduleLoader, cancellationToken);

    /// <summary>
    /// Load extensions from paths, caching module factories per cwd. Port of <c>loadExtensionsCached</c>:
    /// factories are re-run on every load, only the module import is cached.
    /// </summary>
    public static Task<LoadExtensionsResult> LoadExtensionsCached(
        IReadOnlyList<string> paths,
        string cwd,
        EventBus? eventBus = null,
        IExtensionRuntime? runtime = null,
        IExtensionModuleLoader? moduleLoader = null,
        CancellationToken cancellationToken = default) =>
        LoadExtensionsInternal(paths, cwd, eventBus, runtime, useCache: true, moduleLoader, cancellationToken);

    private static async Task<LoadExtensionsResult> LoadExtensionsInternal(
        IReadOnlyList<string> paths,
        string cwd,
        EventBus? eventBus,
        IExtensionRuntime? runtime,
        bool useCache,
        IExtensionModuleLoader? moduleLoader,
        CancellationToken cancellationToken)
    {
        var extensions = new List<Extension>();
        var errors = new List<ExtensionLoadError>();
        var warnings = new List<ExtensionLoadWarning>();
        var cacheToken = useCache ? UseExtensionCacheCwd(cwd) : null;
        var resolvedCwd = cacheToken?.Cwd ?? Paths.ResolvePath(cwd);
        var resolvedEventBus = eventBus ?? EventBusController.CreateEventBus();
        var resolvedRuntime = runtime ?? CreateExtensionRuntime();
        // 默认装载器按扩展名分派：程序集走 AssemblyLoadContext，TypeScript/JavaScript 走 Jint。
        // 上游生态的扩展全是 .ts/.js，两者都必须能从同一个目录里加载。
        var resolvedLoader = moduleLoader ?? CompositeExtensionModuleLoader.Default;

        foreach (var extensionPath in paths)
        {
            var (extension, error) = await LoadExtension(
                extensionPath,
                resolvedCwd,
                resolvedEventBus,
                resolvedRuntime,
                cacheToken,
                resolvedLoader,
                cancellationToken);

            if (error is not null)
            {
                errors.Add(new ExtensionLoadError(extensionPath, error));
                continue;
            }

            if (extension is not null)
            {
                extensions.Add(extension);
            }
        }

        return new LoadExtensionsResult(extensions, errors, warnings, resolvedRuntime);
    }

    // ------------------------------------------------------------------ discovery

    /// <summary>
    /// 发现阶段接受的扩展文件。源码与程序集都保留：加载阶段由
    /// <summary>
    /// 发现阶段接受的扩展文件：源码扩展与已编译程序集都算。
    /// </summary>
    private static bool IsExtensionFile(string name) =>
        name.EndsWith(".ts", StringComparison.Ordinal)
        || name.EndsWith(".tsx", StringComparison.Ordinal)
        || name.EndsWith(".js", StringComparison.Ordinal)
        || name.EndsWith(".mjs", StringComparison.Ordinal)
        || name.EndsWith(".cjs", StringComparison.Ordinal)
        || name.EndsWith(".jsx", StringComparison.Ordinal)
        || name.EndsWith(".dll", StringComparison.Ordinal);

    /// <summary>
    /// Resolve extension entry points from a directory: a package.json <c>pi.extensions</c> manifest
    /// first, then <c>index.ts</c>, <c>index.js</c>, and <c>index.dll</c>. Port of
    /// <c>resolveExtensionEntries</c>, including the native .NET assembly entry.
    /// </summary>
    internal static IReadOnlyList<string>? ResolveExtensionEntries(string dir)
    {
        var packageJsonPath = NodePath.Join(dir, "package.json");
        if (File.Exists(packageJsonPath))
        {
            var manifest = PiManifestReader.ReadPiManifest(packageJsonPath);
            if (manifest?.Extensions is { Count: > 0 })
            {
                var entries = new List<string>();
                foreach (var extensionPath in manifest.Extensions)
                {
                    var resolvedExtensionPath = NodePath.Resolve(dir, extensionPath);
                    if (File.Exists(resolvedExtensionPath))
                    {
                        entries.Add(resolvedExtensionPath);
                    }
                }

                if (entries.Count > 0)
                {
                    return entries;
                }
            }
        }

        // 顺序与上游一致：index.ts → index.js → index.dll。端口能编译 TypeScript 之后，
        // 源码是唯一事实来源，编译产物只是后备（同一目录同时存在两者时按上游语义取源码）。
        var indexTs = NodePath.Join(dir, "index.ts");
        if (File.Exists(indexTs))
        {
            return [indexTs];
        }

        var indexJs = NodePath.Join(dir, "index.js");
        if (File.Exists(indexJs))
        {
            return [indexJs];
        }

        var indexDll = NodePath.Join(dir, "index.dll");
        if (File.Exists(indexDll))
        {
            return [indexDll];
        }

        return null;
    }

    /// <summary>
    /// Discover extensions in a directory: direct <c>*.ts</c>/<c>*.js</c>/<c>*.dll</c> files, and
    /// subdirectories with an index file or a <c>pi</c> manifest. No recursion beyond one level.
    /// Port of <c>discoverExtensionsInDir</c>.
    /// </summary>
    internal static IReadOnlyList<string> DiscoverExtensionsInDir(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var discovered = new List<string>();
        try
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                var entryPath = NodePath.Join(dir, entry.Name);

                // 1. Direct extension files. Directory enumeration surfaces symlinks as the
                // FileInfo/DirectoryInfo of their target, which matches TS's isFile/isSymbolicLink
                // and isDirectory/isSymbolicLink branches.
                if (entry is not DirectoryInfo)
                {
                    if (IsExtensionFile(entry.Name))
                    {
                        discovered.Add(entryPath);
                    }

                    continue;
                }

                // 2 & 3. Subdirectories with an index file or a package.json manifest.
                var entries = ResolveExtensionEntries(entryPath);
                if (entries is not null)
                {
                    discovered.AddRange(entries);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return discovered;
    }

    /// <summary>
    /// Discover and load extensions from the standard locations: the project <c>.pi/extensions</c>
    /// directory, the global agent-dir <c>extensions</c> directory, and the explicitly configured
    /// paths. Port of <c>discoverAndLoadExtensions</c>.
    /// </summary>
    public static async Task<LoadExtensionsResult> DiscoverAndLoadExtensions(
        IReadOnlyList<string> configuredPaths,
        string cwd,
        string? agentDir = null,
        EventBus? eventBus = null,
        IExtensionModuleLoader? moduleLoader = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedCwd = Paths.ResolvePath(cwd);
        var resolvedAgentDir = Paths.ResolvePath(agentDir ?? Config.GetAgentDir());
        var allPaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddPaths(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                var resolved = NodePath.Resolve(path);
                if (seen.Add(resolved))
                {
                    allPaths.Add(path);
                }
            }
        }

        // 1. Project-local extensions: cwd/${CONFIG_DIR_NAME}/extensions/
        AddPaths(DiscoverExtensionsInDir(NodePath.Join(resolvedCwd, Config.ConfigDirName, "extensions")));

        // 2. Global extensions: agentDir/extensions/
        AddPaths(DiscoverExtensionsInDir(NodePath.Join(resolvedAgentDir, "extensions")));

        // 3. Explicitly configured paths.
        foreach (var path in configuredPaths)
        {
            var resolved = Paths.ResolvePath(path, resolvedCwd, new PathInputOptions(NormalizeUnicodeSpaces: true));
            if (Directory.Exists(resolved))
            {
                // Check for package.json with pi manifest or index.ts
                var entries = ResolveExtensionEntries(resolved);
                if (entries is not null)
                {
                    AddPaths(entries);
                    continue;
                }

                // No explicit entries - discover individual files in directory
                AddPaths(DiscoverExtensionsInDir(resolved));
                continue;
            }

            AddPaths([resolved]);
        }

        // 上游扩展是 TypeScript/JavaScript，端口现在用 Jint 宿主直接加载它们；程序集仍走 ALC。
        // 两条路径都经过同一个 IExtensionModuleLoader 接缝，发现阶段不再丢弃源码扩展。
        var loaded = await LoadExtensions(
            allPaths, resolvedCwd, eventBus, moduleLoader: moduleLoader, cancellationToken: cancellationToken);
        return loaded;
    }

    // ------------------------------------------------------------------ default module loader

    /// <summary>
    /// Loader used when no <see cref="IExtensionModuleLoader"/> is supplied. Every load fails with a
    /// clear error (aggregated into <see cref="LoadExtensionsResult.Errors"/>), because the
    /// AssemblyLoadContext route lands with batch 4d-3.
    /// </summary>
}
