// ============================================================================
// PackageManagerCollectors — module-level directory collectors of core/package-manager.ts (4e-3c-2)
// ============================================================================
//
// The TS module discovers resources in six different shapes, and the differences matter:
//
//   * `collectFiles`            — recursive, honouring per-directory ignore files, extension-filtered;
//   * `collectSkillEntries`     — a directory holding `SKILL.md` is one skill and is not descended
//                                 into; otherwise root-level `*.md` counts (mode "pi") or nested
//                                 `*.md` counts (mode "agents");
//   * `collectAutoPromptEntries`/`collectAutoThemeEntries` — non-recursive, one extension each;
//   * `resolveExtensionEntries` — a directory is an extension when `package.json` lists extensions
//                                 or it has an `index.ts`/`index.js`;
//   * `collectAutoExtensionEntries` — the above, else scan one level for `.ts`/`.js` files and
//                                 extension directories;
//   * `expandPackageGlob`       — manifest globs, resolved through <see cref="GlobSync"/>.
//
// Symlink handling is folded into a single classification helper. Upstream calls `statSync` (which
// follows links) whenever `Dirent.isSymbolicLink()` is true, and skips the entry when the stat
// throws; `Directory.Exists`/`File.Exists` follow links too and report `false` for a dangling link,
// so the port classifies every entry the same way instead of branching on link-ness.

using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>TS <c>SkillDiscoveryMode</c>: which directory level contributes loose <c>*.md</c> skills.</summary>
internal enum SkillDiscoveryMode
{
    /// <summary>TS <c>"pi"</c>: only <c>*.md</c> directly under the scan root is a skill.</summary>
    Pi,

    /// <summary>TS <c>"agents"</c>: only <c>*.md</c> below the scan root is a skill.</summary>
    Agents,
}

/// <summary>Port of the directory collectors at the top of <c>core/package-manager.ts</c>.</summary>
internal static class PackageManagerCollectors
{
    /// <summary>TS <c>collectFiles</c>: recursive, ignore-file aware, filtered by <paramref name="filePattern"/>.</summary>
    internal static List<string> CollectFiles(
        string dir,
        Regex filePattern,
        bool skipNodeModules = true,
        IgnoreMatcher? ignoreMatcher = null,
        string? rootDir = null)
    {
        var files = new List<string>();
        if (!Directory.Exists(dir))
        {
            return files;
        }

        var root = rootDir ?? dir;
        var ig = ignoreMatcher ?? new IgnoreMatcher();
        Skills.AddIgnoreRules(ig, dir, root);

        foreach (var entry in ReadDir(dir))
        {
            if (entry.Name.StartsWith('.'))
            {
                continue;
            }

            if (skipNodeModules && string.Equals(entry.Name, "node_modules", StringComparison.Ordinal))
            {
                continue;
            }

            var relPath = Skills.ToPosixPath(NodePath.Relative(root, entry.FullPath));
            var ignorePath = entry.IsDirectory ? $"{relPath}/" : relPath;
            if (ig.Ignores(ignorePath))
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                files.AddRange(CollectFiles(entry.FullPath, filePattern, skipNodeModules, ig, root));
            }
            else if (entry.IsFile && filePattern.IsMatch(entry.Name))
            {
                files.Add(entry.FullPath);
            }
        }

        return files;
    }

    /// <summary>TS <c>collectSkillEntries</c>.</summary>
    internal static List<string> CollectSkillEntries(
        string dir,
        SkillDiscoveryMode mode,
        IgnoreMatcher? ignoreMatcher = null,
        string? rootDir = null)
    {
        var entries = new List<string>();
        if (!Directory.Exists(dir))
        {
            return entries;
        }

        var root = rootDir ?? dir;
        var ig = ignoreMatcher ?? new IgnoreMatcher();
        Skills.AddIgnoreRules(ig, dir, root);

        var dirEntries = ReadDir(dir);

        // A directory holding SKILL.md is exactly one skill; stop before the recursive pass.
        foreach (var entry in dirEntries)
        {
            if (!string.Equals(entry.Name, "SKILL.md", StringComparison.Ordinal))
            {
                continue;
            }

            var relPath = Skills.ToPosixPath(NodePath.Relative(root, entry.FullPath));
            if (entry.IsFile && !ig.Ignores(relPath))
            {
                entries.Add(entry.FullPath);
                return entries;
            }
        }

        foreach (var entry in dirEntries)
        {
            if (entry.Name.StartsWith('.'))
            {
                continue;
            }

            if (string.Equals(entry.Name, "node_modules", StringComparison.Ordinal))
            {
                continue;
            }

            var relPath = Skills.ToPosixPath(NodePath.Relative(root, entry.FullPath));
            var isScanRoot = string.Equals(dir, root, StringComparison.Ordinal);
            var shouldIncludeMarkdownFile =
                entry.IsFile
                && entry.Name.EndsWith(".md", StringComparison.Ordinal)
                && !ig.Ignores(relPath)
                && ((mode == SkillDiscoveryMode.Pi && isScanRoot) || (mode == SkillDiscoveryMode.Agents && !isScanRoot));
            if (shouldIncludeMarkdownFile)
            {
                entries.Add(entry.FullPath);
                continue;
            }

            if (!entry.IsDirectory)
            {
                continue;
            }

            if (ig.Ignores($"{relPath}/"))
            {
                continue;
            }

            entries.AddRange(CollectSkillEntries(entry.FullPath, mode, ig, root));
        }

        return entries;
    }

    /// <summary>TS <c>collectAutoSkillEntries</c>.</summary>
    internal static List<string> CollectAutoSkillEntries(string dir, SkillDiscoveryMode mode)
        => CollectSkillEntries(dir, mode);

    /// <summary>TS <c>findGitRepoRoot</c>: nearest ancestor containing a <c>.git</c> entry.</summary>
    internal static string? FindGitRepoRoot(string startDir)
    {
        var dir = NodePath.Resolve(startDir);
        while (true)
        {
            var dotGit = NodePath.Join(dir, ".git");
            if (File.Exists(dotGit) || Directory.Exists(dotGit))
            {
                return dir;
            }

            var parent = NodePath.Dirname(dir);
            if (string.Equals(parent, dir, StringComparison.Ordinal))
            {
                return null;
            }

            dir = parent;
        }
    }

    /// <summary>TS <c>collectAncestorAgentsSkillDirs</c>: every <c>.agents/skills</c> up to the repo root.</summary>
    internal static List<string> CollectAncestorAgentsSkillDirs(string startDir)
    {
        var skillDirs = new List<string>();
        var resolvedStartDir = NodePath.Resolve(startDir);
        var gitRepoRoot = FindGitRepoRoot(resolvedStartDir);

        var dir = resolvedStartDir;
        while (true)
        {
            skillDirs.Add(NodePath.Join(dir, ".agents", "skills"));
            if (gitRepoRoot is not null && string.Equals(dir, gitRepoRoot, StringComparison.Ordinal))
            {
                break;
            }

            var parent = NodePath.Dirname(dir);
            if (string.Equals(parent, dir, StringComparison.Ordinal))
            {
                break;
            }

            dir = parent;
        }

        return skillDirs;
    }

    /// <summary>TS <c>collectAutoPromptEntries</c>: non-recursive <c>*.md</c> files.</summary>
    internal static List<string> CollectAutoPromptEntries(string dir) => CollectFlatByExtension(dir, ".md");

    /// <summary>TS <c>collectAutoThemeEntries</c>: non-recursive <c>*.json</c> files.</summary>
    internal static List<string> CollectAutoThemeEntries(string dir) => CollectFlatByExtension(dir, ".json");

    /// <summary>TS <c>resolveExtensionEntries</c>: manifest entries, else <c>index.ts</c>/<c>index.js</c>.</summary>
    internal static List<string>? ResolveExtensionEntries(string dir)
    {
        var packageJsonPath = NodePath.Join(dir, "package.json");
        if (File.Exists(packageJsonPath))
        {
            var manifest = PiManifestReader.ReadPiManifest(packageJsonPath);
            if (manifest?.Extensions is { Count: > 0 } manifestExtensions)
            {
                var entries = new List<string>();
                foreach (var extPath in manifestExtensions)
                {
                    var resolvedExtPath = NodePath.Resolve(dir, extPath);
                    if (File.Exists(resolvedExtPath) || Directory.Exists(resolvedExtPath))
                    {
                        entries.Add(resolvedExtPath);
                    }
                }

                if (entries.Count > 0)
                {
                    return entries;
                }
            }
        }

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

        return null;
    }

    /// <summary>TS <c>collectAutoExtensionEntries</c>.</summary>
    internal static List<string> CollectAutoExtensionEntries(string dir)
    {
        var entries = new List<string>();
        if (!Directory.Exists(dir))
        {
            return entries;
        }

        // A directory that declares its own extension entries is a single extension, not a container.
        var rootEntries = ResolveExtensionEntries(dir);
        if (rootEntries is not null)
        {
            return rootEntries;
        }

        var ig = new IgnoreMatcher();
        Skills.AddIgnoreRules(ig, dir, dir);

        foreach (var entry in ReadDir(dir))
        {
            if (entry.Name.StartsWith('.'))
            {
                continue;
            }

            if (string.Equals(entry.Name, "node_modules", StringComparison.Ordinal))
            {
                continue;
            }

            var relPath = Skills.ToPosixPath(NodePath.Relative(dir, entry.FullPath));
            var ignorePath = entry.IsDirectory ? $"{relPath}/" : relPath;
            if (ig.Ignores(ignorePath))
            {
                continue;
            }

            if (entry.IsFile
                && (entry.Name.EndsWith(".ts", StringComparison.Ordinal) || entry.Name.EndsWith(".js", StringComparison.Ordinal)))
            {
                entries.Add(entry.FullPath);
            }
            else if (entry.IsDirectory)
            {
                var resolvedEntries = ResolveExtensionEntries(entry.FullPath);
                if (resolvedEntries is not null)
                {
                    entries.AddRange(resolvedEntries);
                }
            }
        }

        return entries;
    }

    /// <summary>TS <c>collectResourceFiles</c>: extensions and skills use smart discovery, the rest recurse.</summary>
    internal static List<string> CollectResourceFiles(string dir, string resourceType)
    {
        if (string.Equals(resourceType, "skills", StringComparison.Ordinal))
        {
            return CollectSkillEntries(dir, SkillDiscoveryMode.Pi);
        }

        if (string.Equals(resourceType, "extensions", StringComparison.Ordinal))
        {
            return CollectAutoExtensionEntries(dir);
        }

        return CollectFiles(dir, PackageManagerPatterns.FilePattern(resourceType));
    }

    /// <summary>
    /// TS <c>expandPackageGlob</c>: expand a manifest glob under <paramref name="root"/>, drop any
    /// match whose relative path has a dot-segment, and sort.
    /// </summary>
    internal static List<string> ExpandPackageGlob(string pattern, string root)
    {
        var matches = new List<string>();
        foreach (var match in GlobSync.Sync(pattern, root))
        {
            var path = NodePath.Resolve(root, match);
            var relative = NodePath.Relative(root, path);
            var hidden = false;
            foreach (var segment in relative.Split(NodePath.Separator))
            {
                if (!string.Equals(segment, "..", StringComparison.Ordinal) && segment.StartsWith('.'))
                {
                    hidden = true;
                    break;
                }
            }

            if (!hidden)
            {
                matches.Add(path);
            }
        }

        matches.Sort(StringComparer.Ordinal);
        return matches;
    }

    private static List<string> CollectFlatByExtension(string dir, string extension)
    {
        var entries = new List<string>();
        if (!Directory.Exists(dir))
        {
            return entries;
        }

        var ig = new IgnoreMatcher();
        Skills.AddIgnoreRules(ig, dir, dir);

        foreach (var entry in ReadDir(dir))
        {
            if (entry.Name.StartsWith('.'))
            {
                continue;
            }

            if (string.Equals(entry.Name, "node_modules", StringComparison.Ordinal))
            {
                continue;
            }

            var relPath = Skills.ToPosixPath(NodePath.Relative(dir, entry.FullPath));
            if (ig.Ignores(relPath))
            {
                continue;
            }

            if (entry.IsFile && entry.Name.EndsWith(extension, StringComparison.Ordinal))
            {
                entries.Add(entry.FullPath);
            }
        }

        return entries;
    }

    private readonly record struct DirEntry(string Name, string FullPath, bool IsDirectory, bool IsFile);

    private static List<DirEntry> ReadDir(string dir)
    {
        var entries = new List<DirEntry>();
        string[] names;
        try
        {
            names = Directory.GetFileSystemEntries(dir);
        }
        catch (IOException)
        {
            return entries;
        }
        catch (UnauthorizedAccessException)
        {
            return entries;
        }

        foreach (var fullPath in names)
        {
            var isDirectory = Directory.Exists(fullPath);
            var isFile = !isDirectory && File.Exists(fullPath);
            if (!isDirectory && !isFile)
            {
                continue;
            }

            entries.Add(new DirEntry(Path.GetFileName(fullPath), fullPath, isDirectory, isFile));
        }

        return entries;
    }
}
