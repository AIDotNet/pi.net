// ============================================================================
// Startup resource display formatting for InteractiveMode.
// Port of the path / scope-group / diagnostic helpers of
// modes/interactive/interactive-mode.ts (L1381-1762).
//
// In TS these are private methods that read `this.settingsManager` / `this.sessionManager`
// and the module-level `theme` singleton. The two instance reads (home directory and cwd)
// and the theme are explicit parameters here so the logic is testable without an
// InteractiveMode; the later 4f-4 batches delegate to these functions.
// ============================================================================

using System.Text.RegularExpressions;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>One resource path plus where it came from. TS <c>{ path, sourceInfo? }</c>.</summary>
public sealed record ResourcePathEntry
{
    public required string Path { get; init; }

    public SourceInfo? SourceInfo { get; init; }
}

/// <summary>One startup listing bucket. TS <c>buildScopeGroups</c>'s per-scope group.</summary>
public sealed class ScopeGroup
{
    /// <summary>"user", "project" or "path".</summary>
    public required string Scope { get; init; }

    /// <summary>Non-package resources, in insertion order.</summary>
    public List<ResourcePathEntry> Paths { get; } = [];

    /// <summary>Package resources keyed by source tag (<c>npm:…</c> / <c>git:…</c>).</summary>
    public Dictionary<string, List<ResourcePathEntry>> Packages { get; } = new(StringComparer.Ordinal);
}

/// <summary>TS <c>formatScopeGroups</c>'s formatter callbacks.</summary>
public sealed record FormatScopeGroupsOptions
{
    public required Func<ResourcePathEntry, string> FormatPath { get; init; }

    public required Func<ResourcePathEntry, string, string> FormatPackagePath { get; init; }
}

/// <summary>
/// The slice of the theme these formatters need. The TS module reads the <c>theme</c> singleton
/// directly; this seam lets the tests replay it with deterministic markers, as with
/// <c>IRenderDiffTheme</c> and <c>IMermaidTheme</c>.
/// </summary>
internal interface IInteractiveModeFormatTheme
{
    string Fg(string token, string text);
}

/// <summary>Port of the display/diagnostic helpers of <c>interactive-mode.ts</c>.</summary>
public static partial class InteractiveModeFormatting
{
    /// <summary>A candidate for <see cref="GetCompactExtensionLabels"/>'s uniqueness pass.</summary>
    internal sealed record CompactExtensionCandidate(
        string Path,
        SourceInfo? SourceInfo,
        List<string> Segments);

    [GeneratedRegex(@"^(?<root>.*/node_modules)/(?<pkg>@?[^/]+(?:/[^/]+)?)$")]
    private static partial Regex NpmRootRegex();

    [GeneratedRegex(@"node_modules/(?<pkg>@?[^/]+(?:/[^/]+)?)/(?<rest>.*)")]
    private static partial Regex NpmPathRegex();

    [GeneratedRegex(@"git/[^/]+/[^/]+/(?<rest>.*)")]
    private static partial Regex GitPathRegex();

    [GeneratedRegex(@"/index\.ts$")]
    private static partial Regex IndexTsRegex();

    [GeneratedRegex(@"/index\.js$")]
    private static partial Regex IndexJsRegex();

    private sealed class RuntimeTheme : IInteractiveModeFormatTheme
    {
        public string Fg(string token, string text) => ThemeRuntime.Current.Fg(token, text);
    }

    private static readonly IInteractiveModeFormatTheme Runtime = new RuntimeTheme();

    // ---------------------------------------------------------------- simple paths

    /// <summary>TS <c>formatDisplayPath(p)</c>: abbreviate the home directory to <c>~</c>.</summary>
    public static string FormatDisplayPath(string p, string home)
    {
        var result = p;
        if (result.StartsWith(home, StringComparison.Ordinal))
        {
            result = $"~{result[home.Length..]}";
        }

        return result;
    }

    /// <summary>TS <c>formatExtensionDisplayPath(path)</c>: also drop a trailing <c>index.ts/js</c>.</summary>
    public static string FormatExtensionDisplayPath(string path, string home)
    {
        var result = FormatDisplayPath(path, home);
        result = IndexTsRegex().Replace(result, "");
        return IndexJsRegex().Replace(result, "");
    }

    /// <summary>
    /// TS <c>formatContextPath(p)</c>: prefer the path relative to the session cwd, else fall back
    /// to the home-abbreviated absolute path.
    /// </summary>
    public static string FormatContextPath(string p, string cwd, string home)
    {
        var resolvedCwd = NodePath.Resolve(cwd);
        var absolutePath = NodePath.IsAbsolute(p) ? NodePath.Resolve(p) : NodePath.Resolve(resolvedCwd, p);
        var relativePath = Paths.GetCwdRelativePath(absolutePath, resolvedCwd);
        if (relativePath is not null)
        {
            return relativePath;
        }

        return FormatDisplayPath(absolutePath, home);
    }

    // ---------------------------------------------------------------- startup gating

    /// <summary>TS <c>getStartupExpansionState()</c>.</summary>
    public static bool GetStartupExpansionState(bool verbose, bool toolOutputExpanded) =>
        verbose || toolOutputExpanded;

    /// <summary>
    /// TS <c>shouldShowStartupHeader()</c>: the logo/version/key-hint block is hidden only by
    /// <c>quietStartup: true</c>.
    /// </summary>
    public static bool ShouldShowStartupHeader(bool verbose, bool? quietStartup) =>
        verbose || quietStartup != true;

    /// <summary>
    /// TS <c>shouldShowStartupDetails()</c>: model scope and loaded resources are hidden by
    /// <c>quietStartup: true</c> or <c>"header"</c>.
    /// </summary>
    public static bool ShouldShowStartupDetails(bool verbose, bool? quietStartup) =>
        verbose || quietStartup == false;

    // ---------------------------------------------------------------- short paths

    /// <summary>TS <c>getShortPath(fullPath, sourceInfo)</c>: shorten relative to the package root.</summary>
    public static string GetShortPath(string fullPath, SourceInfo? sourceInfo, string home)
    {
        var normalizedFullPath = fullPath.Replace('\\', '/');
        var baseDir = sourceInfo?.BaseDir;
        if (baseDir is not null && IsPackageSource(sourceInfo))
        {
            var normalizedBaseDir = baseDir.Replace('\\', '/');
            var npmRootMatch = NpmRootRegex().Match(normalizedBaseDir);
            // If fullPath is under the same node_modules root as baseDir, preserve that relative
            // topology: the package name is what the user recognises, not the package root.
            if (npmRootMatch.Success && normalizedFullPath.StartsWith($"{npmRootMatch.Groups["root"].Value}/", StringComparison.Ordinal))
            {
                return NodePath.Relative(false, normalizedBaseDir, normalizedFullPath);
            }

            var relativePath = NodePath.Relative(NodePath.Resolve(baseDir), NodePath.Resolve(fullPath));
            if (relativePath.Length > 0 &&
                relativePath != "." &&
                // TS checks both ".." and ".." + sep; the first already covers the second.
                !relativePath.StartsWith("..", StringComparison.Ordinal) &&
                !NodePath.IsAbsolute(relativePath))
            {
                return relativePath.Replace('\\', '/');
            }
        }

        var source = sourceInfo?.Source ?? "";
        var npmMatch = NpmPathRegex().Match(normalizedFullPath);
        if (npmMatch.Success && source.StartsWith("npm:", StringComparison.Ordinal))
        {
            return npmMatch.Groups["rest"].Value;
        }

        var gitMatch = GitPathRegex().Match(normalizedFullPath);
        if (gitMatch.Success && source.StartsWith("git:", StringComparison.Ordinal))
        {
            return gitMatch.Groups["rest"].Value;
        }

        return FormatDisplayPath(fullPath, home);
    }

    /// <summary>TS <c>getCompactPathLabel(resourcePath, sourceInfo)</c>: the last path segment.</summary>
    public static string GetCompactPathLabel(string resourcePath, SourceInfo? sourceInfo, string home)
    {
        var shortPath = GetShortPath(resourcePath, sourceInfo, home);
        var normalizedPath = shortPath.Replace('\\', '/');
        var segments = normalizedPath
            .Split('/')
            .Where(segment => segment.Length > 0 && segment != "~")
            .ToList();
        return segments.Count > 0 ? segments[^1] : shortPath;
    }

    /// <summary>TS <c>getCompactPackageSourceLabel(sourceInfo)</c>: the package identity without its prefix.</summary>
    public static string GetCompactPackageSourceLabel(SourceInfo? sourceInfo)
    {
        var source = sourceInfo?.Source ?? "";
        if (source.StartsWith("npm:", StringComparison.Ordinal))
        {
            // TS `source.slice("npm:".length) || source` — an empty remainder falls back to source.
            var trimmed = source["npm:".Length..];
            return trimmed.Length > 0 ? trimmed : source;
        }

        var gitSource = Git.ParseGitUrl(source);
        if (gitSource is not null)
        {
            return gitSource.Path.Length > 0 ? gitSource.Path : source;
        }

        return source;
    }

    /// <summary>TS <c>getCompactExtensionLabel(resourcePath, sourceInfo)</c>.</summary>
    public static string GetCompactExtensionLabel(string resourcePath, SourceInfo? sourceInfo, string home)
    {
        if (!IsPackageSource(sourceInfo))
        {
            return GetCompactPathLabel(resourcePath, sourceInfo, home);
        }

        var sourceLabel = GetCompactPackageSourceLabel(sourceInfo);
        if (sourceLabel.Length == 0)
        {
            return GetCompactPathLabel(resourcePath, sourceInfo, home);
        }

        var shortPath = GetShortPath(resourcePath, sourceInfo, home).Replace('\\', '/');
        var packagePath = shortPath.StartsWith("extensions/", StringComparison.Ordinal)
            ? shortPath["extensions/".Length..]
            : shortPath;
        var dir = PosixDirname(packagePath);
        var name = PosixStem(packagePath);

        if (name == "index")
        {
            return dir.Length == 0 || dir == "." ? sourceLabel : $"{sourceLabel}:{dir}";
        }

        return $"{sourceLabel}:{packagePath}";
    }

    /// <summary>TS <c>getCompactDisplayPathSegments(resourcePath)</c>.</summary>
    public static List<string> GetCompactDisplayPathSegments(string resourcePath, string home) =>
        [.. FormatDisplayPath(resourcePath, home)
            .Replace('\\', '/')
            .Split('/')
            .Where(segment => segment.Length > 0 && segment != "~")];

    /// <summary>
    /// TS <c>getCompactNonPackageExtensionLabel(resourcePath, index, allPaths)</c>: the shortest
    /// trailing segment run that is unique among the non-package extensions.
    /// </summary>
    internal static string GetCompactNonPackageExtensionLabel(
        string resourcePath,
        int index,
        IReadOnlyList<CompactExtensionCandidate> allPaths)
    {
        var segments = index >= 0 && index < allPaths.Count ? allPaths[index].Segments : null;
        if (segments is null || segments.Count == 0)
        {
            return GetCompactPathLabel(resourcePath, null, "");
        }

        for (var segmentCount = 1; segmentCount <= segments.Count; segmentCount += 1)
        {
            var candidate = string.Join("/", segments.Skip(segments.Count - segmentCount));
            var isUnique = true;
            for (var itemIndex = 0; itemIndex < allPaths.Count; itemIndex += 1)
            {
                if (itemIndex == index)
                {
                    continue;
                }

                var itemSegments = allPaths[itemIndex].Segments;
                var itemCandidate = string.Join(
                    "/",
                    itemSegments.Skip(Math.Max(0, itemSegments.Count - segmentCount)));
                if (itemCandidate == candidate)
                {
                    isUnique = false;
                    break;
                }
            }

            if (isUnique)
            {
                return candidate;
            }
        }

        return string.Join("/", segments);
    }

    /// <summary>TS <c>getCompactExtensionLabels(extensions)</c>.</summary>
    public static List<string> GetCompactExtensionLabels(
        IReadOnlyList<ResourcePathEntry> extensions,
        string home)
    {
        var nonPackageExtensions = new List<CompactExtensionCandidate>();
        foreach (var extension in extensions)
        {
            var segments = GetCompactDisplayPathSegments(extension.Path, home);
            var lastSegment = segments.Count > 0 ? segments[^1] : null;
            if (segments.Count > 1 && (lastSegment == "index.ts" || lastSegment == "index.js"))
            {
                segments.RemoveAt(segments.Count - 1);
            }

            if (!IsPackageSource(extension.SourceInfo))
            {
                nonPackageExtensions.Add(new CompactExtensionCandidate(extension.Path, extension.SourceInfo, segments));
            }
        }

        var labels = new List<string>(extensions.Count);
        foreach (var extension in extensions)
        {
            if (IsPackageSource(extension.SourceInfo))
            {
                labels.Add(GetCompactExtensionLabel(extension.Path, extension.SourceInfo, home));
                continue;
            }

            var nonPackageIndex = nonPackageExtensions.FindIndex(item => item.Path == extension.Path);
            if (nonPackageIndex == -1)
            {
                labels.Add(GetCompactPathLabel(extension.Path, extension.SourceInfo, home));
                continue;
            }

            labels.Add(GetCompactNonPackageExtensionLabel(extension.Path, nonPackageIndex, nonPackageExtensions));
        }

        return labels;
    }

    // ---------------------------------------------------------------- source labels

    /// <summary>TS <c>getDisplaySourceInfo(sourceInfo)</c>.</summary>
    public static (string Label, string? ScopeLabel, string Color) GetDisplaySourceInfo(SourceInfo? sourceInfo)
    {
        var source = sourceInfo?.Source ?? "local";
        var scope = sourceInfo?.Scope ?? "project";
        if (source == "local")
        {
            if (scope == "user")
            {
                return ("user", null, "muted");
            }

            if (scope == "project")
            {
                return ("project", null, "muted");
            }

            if (scope == "temporary")
            {
                return ("path", "temp", "muted");
            }

            return ("path", null, "muted");
        }

        if (source == "cli")
        {
            return ("path", scope == "temporary" ? "temp" : null, "muted");
        }

        var scopeLabel = scope switch
        {
            "user" => "user",
            "project" => "project",
            "temporary" => "temp",
            _ => (string?)null,
        };
        return (source, scopeLabel, "accent");
    }

    /// <summary>TS <c>getScopeGroup(sourceInfo)</c>.</summary>
    public static string GetScopeGroup(SourceInfo? sourceInfo)
    {
        var source = sourceInfo?.Source ?? "local";
        var scope = sourceInfo?.Scope ?? "project";
        if (source == "cli" || scope == "temporary")
        {
            return "path";
        }

        if (scope == "user")
        {
            return "user";
        }

        return scope == "project" ? "project" : "path";
    }

    /// <summary>TS <c>isPackageSource(sourceInfo)</c>.</summary>
    public static bool IsPackageSource(SourceInfo? sourceInfo)
    {
        var source = sourceInfo?.Source ?? "";
        return source.StartsWith("npm:", StringComparison.Ordinal) ||
            source.StartsWith("git:", StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- scope groups

    /// <summary>TS <c>buildScopeGroups(items)</c>: project, then user, then path.</summary>
    public static List<ScopeGroup> BuildScopeGroups(IReadOnlyList<ResourcePathEntry> items)
    {
        var groups = new Dictionary<string, ScopeGroup>(StringComparer.Ordinal)
        {
            ["user"] = new ScopeGroup { Scope = "user" },
            ["project"] = new ScopeGroup { Scope = "project" },
            ["path"] = new ScopeGroup { Scope = "path" },
        };

        foreach (var item in items)
        {
            var group = groups[GetScopeGroup(item.SourceInfo)];
            var source = item.SourceInfo?.Source ?? "local";

            if (IsPackageSource(item.SourceInfo))
            {
                if (!group.Packages.TryGetValue(source, out var list))
                {
                    list = [];
                    group.Packages[source] = list;
                }

                list.Add(item);
            }
            else
            {
                group.Paths.Add(item);
            }
        }

        return [.. new[] { groups["project"], groups["user"], groups["path"] }
            .Where(group => group.Paths.Count > 0 || group.Packages.Count > 0)];
    }

    /// <summary>TS <c>formatScopeGroups(groups, options)</c>.</summary>
    public static string FormatScopeGroups(IReadOnlyList<ScopeGroup> groups, FormatScopeGroupsOptions options) =>
        FormatScopeGroups(groups, options, Runtime);

    /// <summary>The body, with the theme injected (test seam).</summary>
    internal static string FormatScopeGroups(
        IReadOnlyList<ScopeGroup> groups,
        FormatScopeGroupsOptions options,
        IInteractiveModeFormatTheme theme)
    {
        var lines = new List<string>();

        foreach (var group in groups)
        {
            lines.Add($"  {theme.Fg("accent", group.Scope)}");

            foreach (var item in group.Paths.OrderBy(item => item.Path, StringComparer.CurrentCulture))
            {
                lines.Add(theme.Fg("dim", $"    {options.FormatPath(item)}"));
            }

            var sortedPackages = group.Packages
                .OrderBy(entry => entry.Key, StringComparer.CurrentCulture)
                .ToList();
            foreach (var (source, items) in sortedPackages)
            {
                lines.Add($"    {theme.Fg("mdLink", source)}");
                foreach (var item in items.OrderBy(item => item.Path, StringComparer.CurrentCulture))
                {
                    lines.Add(theme.Fg("dim", $"      {options.FormatPackagePath(item, source)}"));
                }
            }
        }

        return string.Join("\n", lines);
    }

    // ---------------------------------------------------------------- diagnostics

    /// <summary>TS <c>findSourceInfoForPath(p, sourceInfos)</c>: exact hit, else the nearest ancestor.</summary>
    public static SourceInfo? FindSourceInfoForPath(string p, IReadOnlyDictionary<string, SourceInfo> sourceInfos)
    {
        if (sourceInfos.TryGetValue(p, out var exact))
        {
            return exact;
        }

        var current = p;
        while (current.Contains('/'))
        {
            current = current[..current.LastIndexOf('/')];
            if (sourceInfos.TryGetValue(current, out var parent))
            {
                return parent;
            }
        }

        return null;
    }

    /// <summary>TS <c>formatPathWithSource(p, sourceInfo)</c>.</summary>
    public static string FormatPathWithSource(string p, SourceInfo? sourceInfo, string home)
    {
        if (sourceInfo is not null)
        {
            var shortPath = GetShortPath(p, sourceInfo, home);
            var (label, scopeLabel, _) = GetDisplaySourceInfo(sourceInfo);
            var labelText = scopeLabel is not null ? $"{label} ({scopeLabel})" : label;
            return $"{labelText} {shortPath}";
        }

        return FormatDisplayPath(p, home);
    }

    /// <summary>TS <c>formatDiagnostics(diagnostics, sourceInfos)</c>.</summary>
    public static string FormatDiagnostics(
        IReadOnlyList<ResourceDiagnostic> diagnostics,
        IReadOnlyDictionary<string, SourceInfo> sourceInfos,
        string home) =>
        FormatDiagnostics(diagnostics, sourceInfos, home, Runtime);

    /// <summary>The body, with the theme injected (test seam).</summary>
    internal static string FormatDiagnostics(
        IReadOnlyList<ResourceDiagnostic> diagnostics,
        IReadOnlyDictionary<string, SourceInfo> sourceInfos,
        string home,
        IInteractiveModeFormatTheme theme)
    {
        var lines = new List<string>();

        // Collision diagnostics are grouped by resource name; a JS Map keeps insertion order, so
        // the names appear in first-seen order rather than sorted.
        var collisionOrder = new List<string>();
        var collisions = new Dictionary<string, List<ResourceDiagnostic>>(StringComparer.Ordinal);
        var otherDiagnostics = new List<ResourceDiagnostic>();

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Type == ResourceDiagnosticType.Collision && diagnostic.Collision is { } collision)
            {
                if (!collisions.TryGetValue(collision.Name, out var list))
                {
                    list = [];
                    collisions[collision.Name] = list;
                    collisionOrder.Add(collision.Name);
                }

                list.Add(diagnostic);
            }
            else
            {
                otherDiagnostics.Add(diagnostic);
            }
        }

        foreach (var name in collisionOrder)
        {
            var collisionList = collisions[name];
            var first = collisionList[0].Collision;
            if (first is null)
            {
                continue;
            }

            lines.Add(theme.Fg("warning", $"  \"{name}\" collision:"));
            lines.Add(
                theme.Fg(
                    "dim",
                    $"    {theme.Fg("success", "✓")} {FormatPathWithSource(first.WinnerPath, FindSourceInfoForPath(first.WinnerPath, sourceInfos), home)}"));
            foreach (var diagnostic in collisionList)
            {
                if (diagnostic.Collision is { } collision)
                {
                    lines.Add(
                        theme.Fg(
                            "dim",
                            $"    {theme.Fg("warning", "✗")} {FormatPathWithSource(collision.LoserPath, FindSourceInfoForPath(collision.LoserPath, sourceInfos), home)} (skipped)"));
                }
            }
        }

        foreach (var diagnostic in otherDiagnostics)
        {
            var token = diagnostic.Type == ResourceDiagnosticType.Error ? "error" : "warning";
            if (diagnostic.Path is not null)
            {
                var formattedPath = FormatPathWithSource(
                    diagnostic.Path,
                    FindSourceInfoForPath(diagnostic.Path, sourceInfos),
                    home);
                lines.Add(theme.Fg(token, $"  {formattedPath}"));
                lines.Add(theme.Fg(token, $"    {diagnostic.Message}"));
            }
            else
            {
                lines.Add(theme.Fg(token, $"  {diagnostic.Message}"));
            }
        }

        return string.Join("\n", lines);
    }

    // ---------------------------------------------------------------- posix helpers

    /// <summary>
    /// <c>path.posix.parse(p).dir</c> — an empty string when the path has no directory part,
    /// which is what TS's <c>!parsedPath.dir</c> test looks for.
    /// </summary>
    private static string PosixDirname(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "" : path[..index];
    }

    /// <summary><c>path.posix.parse(p).name</c> — the file name without its extension.</summary>
    private static string PosixStem(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = slash < 0 ? path : path[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
