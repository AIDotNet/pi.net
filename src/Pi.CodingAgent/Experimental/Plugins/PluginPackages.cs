// ============================================================================
// Plugin package profiles — port of experimental/plugins/package.ts (4g-3c)
// ============================================================================
//
// The server-owned half of plugin packaging: where a configured plugin package is built, and where an
// explicit plugin selection is persisted (per server generation, and per durable Session). Profiles are
// small JSON documents with an exhaustive validation on read, so a corrupt or stale file fails loudly
// instead of silently changing the selection.
//
// TS builds a package with `bundleFacetPackage` (esbuild under the hood). The C# chord port keeps the
// packaging pipeline but requires the caller to inject the entry bundler (`IFacetEntryBundler`) — .NET
// has no built-in JavaScript bundler (C334).

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Chord;
using Pi.Chord.Node;

namespace Pi.CodingAgent.Experimental.Plugins;

/// <summary>A configured plugin package and its serialized builder. TS <c>ConfiguredServerPluginPackage</c>.</summary>
public sealed record ConfiguredServerPluginPackage(string ManifestPath, Func<Task<IReadOnlyList<FacetBundleArtifact>>> Build);

/// <summary>Port of <c>plugins/package.ts</c>.</summary>
public static class PluginPackages
{
    private const int PluginPackageProfileVersion = 1;

    /// <summary>TS <c>DEFAULT_PLUGIN_FACETS</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultPluginFacets =
        new Dictionary<string, string> { ["session"] = "src/session.ts", ["tui"] = "src/tui.ts" };

    /// <summary>
    /// Persist an explicit plugin package selection, or restore it for a later server generation.
    /// An explicit empty selection removes the profile.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RestoreServerPluginPackageProfileAsync(
        string directory, string serverId, IReadOnlyList<string>? configuredPackagePaths)
    {
        var path = ServerProfilePath(directory, serverId);
        if (configuredPackagePaths is null)
        {
            return await ReadProfileAsync(path, allowEmpty: false).ConfigureAwait(false) ?? [];
        }

        var packagePaths = NormalizePluginPackagePaths(configuredPackagePaths);
        if (packagePaths.Count == 0)
        {
            File.Delete(path);
        }
        else
        {
            await WriteProfileAsync(path, packagePaths, sessionPath: null).ConfigureAwait(false);
        }

        return packagePaths;
    }

    /// <summary>Read the explicit plugin selection stored for one durable Session.</summary>
    public static Task<IReadOnlyList<string>?> ReadSessionPluginPackageProfileAsync(
        string directory, string serverId, string sessionPath)
        => ReadProfileAsync(SessionProfilePath(directory, serverId, sessionPath), allowEmpty: true, sessionPath);

    /// <summary>Remove the plugin selection stored for one deleted Session.</summary>
    public static Task RemoveSessionPluginPackageProfileAsync(string directory, string serverId, string sessionPath)
    {
        File.Delete(SessionProfilePath(directory, serverId, sessionPath));
        return Task.CompletedTask;
    }

    /// <summary>Persist the plugin selection for one durable Session.</summary>
    public static Task WriteSessionPluginPackageProfileAsync(
        string directory, string serverId, string sessionPath, IReadOnlyList<string> packagePaths)
        => WriteProfileAsync(
            SessionProfilePath(directory, serverId, sessionPath),
            NormalizePluginPackagePaths(packagePaths),
            sessionPath);

    /// <summary>Create a serialized server-owned builder for one configured plugin package.</summary>
    public static ConfiguredServerPluginPackage CreateServerPluginPackage(
        string directory,
        string serverId,
        string packagePath,
        IFacetEntryBundler bundler)
    {
        var normalizedPackagePath = ResolvePath(packagePath);
        var outdir = Path.Combine(directory, "plugin-builds", serverId, PluginBuildDirectoryName(normalizedPackagePath));
        var manifestPath = Path.Combine(outdir, FacetBundleFormats.ManifestFile);
        var buildTail = Task.CompletedTask;
        return new ConfiguredServerPluginPackage(
            manifestPath,
            () =>
            {
                var operation = buildTail.ContinueWith(async _ =>
                {
                    var result = await Bundler.BundleFacetPackageAsync(new BundleFacetPackageOptions
                    {
                        PackagePath = normalizedPackagePath,
                        Outdir = outdir,
                        DefaultFacets = DefaultPluginFacets,
                    }, bundler).ConfigureAwait(false);
                    // Only the TUI facet is published to presentations; a package without one builds
                    // nothing rather than failing the whole generation.
                    if (!result.Bundle.Manifest.Entries.ContainsKey("tui"))
                    {
                        return (IReadOnlyList<FacetBundleArtifact>)[];
                    }

                    return [await FacetBundleLoader.ReadFacetBundleArtifactAsync(
                        result.Bundle.ManifestPath, "tui").ConfigureAwait(false)];
                }).Unwrap();
                buildTail = IgnoreAsync(operation);
                return operation;
            });
    }

    /// <summary>TS <c>normalizePluginPackagePaths</c>: absolute, non-empty, unique.</summary>
    public static IReadOnlyList<string> NormalizePluginPackagePaths(IReadOnlyList<string> packagePaths)
    {
        var normalized = new List<string>(packagePaths.Count);
        foreach (var packagePath in packagePaths)
        {
            if (packagePath.Length == 0)
            {
                throw new ArgumentException("Plugin package path must not be empty");
            }

            normalized.Add(ResolvePath(packagePath));
        }

        if (new HashSet<string>(normalized, StringComparer.Ordinal).Count != normalized.Count)
        {
            throw new ArgumentException("Plugin package paths must be unique");
        }

        return normalized;
    }

    /// <summary>TS <c>pluginBuildDirectoryName</c>: a readable, collision-free build directory name.</summary>
    public static string PluginBuildDirectoryName(string packagePath)
    {
        var packageDirectory = Path.GetFileName(packagePath) == "package.json"
            ? Path.GetDirectoryName(packagePath) is { Length: > 0 } parent ? parent : "."
            : packagePath;
        var baseName = Path.GetFileName(packageDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var sanitized = Sanitize(baseName);
        var label = sanitized.EndsWith("-plugin", StringComparison.Ordinal) ? sanitized : $"{sanitized}-plugin";
        return $"{label}-{Hash(packagePath, 12)}";

        static string Sanitize(string value)
        {
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (var character in value)
            {
                var ok = char.IsAsciiLetter(character) || char.IsDigit(character) || character is '.' or '_' or '-';
                builder.Append(ok ? character : '-');
            }

            var sanitized = builder.ToString();
            return sanitized.Length == 0 ? "plugin" : sanitized;
        }
    }

    /// <summary>TS <c>sessionPluginProfilePath</c>: the session path hashed into the file name.</summary>
    public static string SessionProfilePath(string directory, string serverId, string sessionPath)
        => Path.Combine(directory, $"session-plugin-packages-{serverId}-{Hash(sessionPath, 24)}.json");

    private static string ServerProfilePath(string directory, string serverId)
        => Path.Combine(directory, $"plugin-packages-{serverId}.json");

    /// <summary>
    /// TS <c>resolve</c>: absolute, and without a trailing separator (Node strips it, .NET keeps it).
    /// A root itself ("/" or "C:\") is the one path that keeps its separator.
    /// </summary>
    private static string ResolvePath(string value)
    {
        var full = Path.GetFullPath(value);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0 || trimmed.EndsWith(':'))
        {
            return full;
        }

        return trimmed;
    }

    private static string Hash(string value, int hexChars)
        => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..hexChars];

    private static async Task IgnoreAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Swallowed: the build tail only serialises, it never reports.
        }
    }

    // ---------------------------------------------------------------- profile files

    private static async Task<IReadOnlyList<string>?> ReadProfileAsync(
        string path, bool allowEmpty, string? sessionPath = null)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Could not read experimental plugin package profile {path}", error);
        }

        // TS runs one big `some(...) || ...` chain over the parsed value; here the same conditions
        // live in a local function so the single "Invalid …" throw stays at the call site.
        bool Invalid(JsonObject profile)
        {
            if (profile.Any(entry => entry.Key != "version"
                && entry.Key != "packagePaths"
                && !(entry.Key == "sessionPath" && sessionPath is not null)))
            {
                return true;
            }

            if (!profile.TryGetPropertyValue("version", out var versionNode)
                || versionNode is not JsonValue versionValue
                || versionValue.GetValueKind() != JsonValueKind.Number
                || !versionValue.TryGetValue(out double version)
                || version != PluginPackageProfileVersion)
            {
                return true;
            }

            if (sessionPath is null)
            {
                if (profile.ContainsKey("sessionPath"))
                {
                    return true;
                }
            }
            else if (!profile.TryGetPropertyValue("sessionPath", out var sessionNode)
                || sessionNode is not JsonValue sessionValue
                || sessionValue.GetValueKind() != JsonValueKind.String
                || !sessionValue.TryGetValue(out string? sessionText)
                || sessionText != sessionPath)
            {
                return true;
            }

            if (!profile.TryGetPropertyValue("packagePaths", out var pathsNode)
                || pathsNode is not JsonArray paths
                || (!allowEmpty && paths.Count == 0)
                || paths.Any(entry => entry is not JsonValue value
                    || value.GetValueKind() != JsonValueKind.String
                    || !value.TryGetValue(out string? text)
                    || text is null
                    || text.Length == 0))
            {
                return true;
            }

            return false;
        }

        if (parsed is not JsonObject profile || Invalid(profile))
        {
            throw new InvalidOperationException($"Invalid experimental plugin package profile {path}");
        }

        var storedPaths = (JsonArray)profile["packagePaths"]!;
        var packagePaths = new List<string>(storedPaths.Count);
        foreach (var entry in storedPaths)
        {
            packagePaths.Add(entry!.GetValue<string>()!);
        }

        return NormalizePluginPackagePaths(packagePaths);
    }

    private static async Task WriteProfileAsync(string path, IReadOnlyList<string> packagePaths, string? sessionPath)
    {
        var profile = new JsonObject { ["version"] = PluginPackageProfileVersion };
        if (sessionPath is not null)
        {
            profile["sessionPath"] = sessionPath;
        }

        profile["packagePaths"] = new JsonArray([.. packagePaths.Select(item => (JsonNode?)JsonValue.Create(item))]);

        // TS writes JSON.stringify(…, null, 2) + "\n" with mode 0600: two-space indent, LF line
        // endings (JSON.stringify never writes CRLF), and an owner-only file. `writeFile` applies the
        // mode only when it creates the file, so an existing profile keeps its permissions.
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentCharacter = ' ',
            IndentSize = 2,
            NewLine = "\n",
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var json = profile.ToJsonString(options);
        var exists = File.Exists(path);
        await File.WriteAllTextAsync(path, $"{json}\n").ConfigureAwait(false);
        if (!exists && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
