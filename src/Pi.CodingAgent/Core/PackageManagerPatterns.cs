// ============================================================================
// PackageManagerPatterns — module-level pattern helpers of core/package-manager.ts (4e-3c-2)
// ============================================================================
//
// The TS module keeps a block of free functions above `DefaultPackageManager` that decide which
// resolved paths are enabled. They are pure (`(paths, patterns, baseDir)` in, a set/map out) and
// carry the whole `+`/`-`/`!` override semantics, so they are ported as a separate static class
// rather than as private methods — the leaf-first split the porting plan asks for.
//
// Pattern vocabulary (TS `applyPatterns` doc comment):
//   * plain pattern — include matching paths;
//   * `!pattern`    — exclude matching paths;
//   * `+path`       — force-include an exact path, overriding exclusions;
//   * `-path`       — force-exclude an exact path, overriding force-includes.
//
// `matchesAnyPattern` compares against four spellings of a path (path relative to the base dir,
// bare basename, absolute posix path, and — for a `SKILL.md` — the same three for its parent
// directory), which is what lets an entry like `review` target `skills/review/SKILL.md`.

using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>Port of the pattern helpers at the top of <c>core/package-manager.ts</c>.</summary>
internal static class PackageManagerPatterns
{
    /// <summary>TS <c>RESOURCE_TYPES</c>; also the order packages are scanned in.</summary>
    internal static readonly string[] ResourceTypes = ["extensions", "skills", "prompts", "themes"];

    private static readonly Regex ExtensionFilePattern = new(@"\.(ts|js)$", RegexOptions.CultureInvariant);

    private static readonly Regex MarkdownFilePattern = new(@"\.md$", RegexOptions.CultureInvariant);

    private static readonly Regex JsonFilePattern = new(@"\.json$", RegexOptions.CultureInvariant);

    /// <summary>TS <c>FILE_PATTERNS[resourceType]</c>.</summary>
    internal static Regex FilePattern(string resourceType) => resourceType switch
    {
        "extensions" => ExtensionFilePattern,
        "skills" or "prompts" => MarkdownFilePattern,
        "themes" => JsonFilePattern,
        _ => throw new ArgumentException($"Unknown resource type: {resourceType}", nameof(resourceType)),
    };

    /// <summary>TS <c>isPattern</c>: an override prefix or a glob metacharacter.</summary>
    internal static bool IsPattern(string s) =>
        s.StartsWith('!') || s.StartsWith('+') || s.StartsWith('-') || s.Contains('*') || s.Contains('?');

    /// <summary>TS <c>isOverridePattern</c>: one of the three override prefixes.</summary>
    internal static bool IsOverridePattern(string s) => s.StartsWith('!') || s.StartsWith('+') || s.StartsWith('-');

    /// <summary>TS <c>hasGlobPattern</c>.</summary>
    internal static bool HasGlobPattern(string s) => s.Contains('*') || s.Contains('?');

    /// <summary>TS <c>normalizeExactPattern</c>: strip a leading <c>./</c> and go posix.</summary>
    internal static string NormalizeExactPattern(string pattern)
    {
        var normalized = pattern.StartsWith("./", StringComparison.Ordinal) || pattern.StartsWith(".\\", StringComparison.Ordinal)
            ? pattern[2..]
            : pattern;
        // 先展开 ~（+~/x、-~/x、!~/skills/pdf 这类条目），否则会按字面量比较而永不匹配（上游 #10765）。
        return Skills.ToPosixPath(Paths.NormalizePath(normalized));
    }

    /// <summary>TS <c>splitPatterns</c>: partition entries into plain paths and patterns.</summary>
    internal static (List<string> Plain, List<string> Patterns) SplitPatterns(IReadOnlyList<string> entries)
    {
        var plain = new List<string>();
        var patterns = new List<string>();
        foreach (var entry in entries)
        {
            if (IsPattern(entry))
            {
                patterns.Add(entry);
            }
            else
            {
                plain.Add(entry);
            }
        }

        return (plain, patterns);
    }

    /// <summary>TS <c>matchesAnyPattern</c>: glob match against the path's several spellings.</summary>
    internal static bool MatchesAnyPattern(string filePath, IReadOnlyList<string> patterns, string baseDir)
    {
        var rel = Skills.ToPosixPath(NodePath.Relative(baseDir, filePath));
        var name = NodePath.Basename(filePath);
        var filePathPosix = Skills.ToPosixPath(filePath);
        var isSkillFile = string.Equals(name, "SKILL.md", StringComparison.Ordinal);
        var parentDir = isSkillFile ? NodePath.Dirname(filePath) : null;
        var parentRel = isSkillFile ? Skills.ToPosixPath(NodePath.Relative(baseDir, parentDir!)) : null;
        var parentName = isSkillFile ? NodePath.Basename(parentDir!) : null;
        var parentDirPosix = isSkillFile ? Skills.ToPosixPath(parentDir!) : null;

        foreach (var pattern in patterns)
        {
            // 与上游一致：glob 模式同样先做 ~ 展开，再做 posix 归一（上游 #10765）。
            var normalizedPattern = Skills.ToPosixPath(Paths.NormalizePath(pattern));
            if (Matches(rel, normalizedPattern)
                || Matches(name, normalizedPattern)
                || Matches(filePathPosix, normalizedPattern))
            {
                return true;
            }

            if (!isSkillFile)
            {
                continue;
            }

            if (Matches(parentRel!, normalizedPattern)
                || Matches(parentName!, normalizedPattern)
                || Matches(parentDirPosix!, normalizedPattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>TS <c>matchesAnyExactPattern</c>: literal (non-glob) comparison, same spellings.</summary>
    internal static bool MatchesAnyExactPattern(string filePath, IReadOnlyList<string> patterns, string baseDir)
    {
        if (patterns.Count == 0)
        {
            return false;
        }

        var rel = Skills.ToPosixPath(NodePath.Relative(baseDir, filePath));
        var name = NodePath.Basename(filePath);
        var filePathPosix = Skills.ToPosixPath(filePath);
        var isSkillFile = string.Equals(name, "SKILL.md", StringComparison.Ordinal);
        var parentDir = isSkillFile ? NodePath.Dirname(filePath) : null;
        var parentRel = isSkillFile ? Skills.ToPosixPath(NodePath.Relative(baseDir, parentDir!)) : null;
        var parentDirPosix = isSkillFile ? Skills.ToPosixPath(parentDir!) : null;

        foreach (var pattern in patterns)
        {
            var normalized = NormalizeExactPattern(pattern);
            if (string.Equals(normalized, rel, StringComparison.Ordinal)
                || string.Equals(normalized, filePathPosix, StringComparison.Ordinal))
            {
                return true;
            }

            if (!isSkillFile)
            {
                continue;
            }

            if (string.Equals(normalized, parentRel, StringComparison.Ordinal)
                || string.Equals(normalized, parentDirPosix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>TS <c>getOverridePatterns</c>.</summary>
    internal static List<string> GetOverridePatterns(IReadOnlyList<string> entries)
    {
        var result = new List<string>();
        foreach (var pattern in entries)
        {
            if (IsOverridePattern(pattern))
            {
                result.Add(pattern);
            }
        }

        return result;
    }

    /// <summary>TS <c>isEnabledByOverrides</c>: `!` excludes, then `+` forces in, then `-` forces out.</summary>
    internal static bool IsEnabledByOverrides(string filePath, IReadOnlyList<string> patterns, string baseDir)
    {
        var overrides = GetOverridePatterns(patterns);
        var excludes = Slice(overrides, '!');
        var forceIncludes = Slice(overrides, '+');
        var forceExcludes = Slice(overrides, '-');

        var enabled = true;
        if (excludes.Count > 0 && MatchesAnyPattern(filePath, excludes, baseDir))
        {
            enabled = false;
        }

        if (forceIncludes.Count > 0 && MatchesAnyExactPattern(filePath, forceIncludes, baseDir))
        {
            enabled = true;
        }

        if (forceExcludes.Count > 0 && MatchesAnyExactPattern(filePath, forceExcludes, baseDir))
        {
            enabled = false;
        }

        return enabled;
    }

    /// <summary>
    /// TS <c>applyPatterns</c>: includes (or everything when there are none), then excludes, then
    /// force-includes, then force-excludes. Returns the enabled paths as a set.
    /// </summary>
    internal static HashSet<string> ApplyPatterns(IReadOnlyList<string> allPaths, IReadOnlyList<string> patterns, string baseDir)
    {
        var includes = new List<string>();
        var excludes = new List<string>();
        var forceIncludes = new List<string>();
        var forceExcludes = new List<string>();

        foreach (var p in patterns)
        {
            if (p.StartsWith('+'))
            {
                forceIncludes.Add(p[1..]);
            }
            else if (p.StartsWith('-'))
            {
                forceExcludes.Add(p[1..]);
            }
            else if (p.StartsWith('!'))
            {
                excludes.Add(p[1..]);
            }
            else
            {
                includes.Add(p);
            }
        }

        // Step 1: apply includes (or all when there are no includes).
        var result = includes.Count == 0
            ? [.. allPaths]
            : allPaths.Where(filePath => MatchesAnyPattern(filePath, includes, baseDir)).ToList();

        // Step 2: apply excludes.
        if (excludes.Count > 0)
        {
            result = result.Where(filePath => !MatchesAnyPattern(filePath, excludes, baseDir)).ToList();
        }

        // Step 3: force-include, adding back from allPaths and overriding exclusions.
        if (forceIncludes.Count > 0)
        {
            foreach (var filePath in allPaths)
            {
                if (!result.Contains(filePath) && MatchesAnyExactPattern(filePath, forceIncludes, baseDir))
                {
                    result.Add(filePath);
                }
            }
        }

        // Step 4: force-exclude, removing even what step 3 put back.
        if (forceExcludes.Count > 0)
        {
            result = result.Where(filePath => !MatchesAnyExactPattern(filePath, forceExcludes, baseDir)).ToList();
        }

        return new HashSet<string>(result, StringComparer.Ordinal);
    }

    /// <summary>
    /// TS <c>applyAutoloadDisabledPatterns</c>: per pattern, map every matching path to whether that
    /// pattern enables it. Later patterns win for the same path.
    /// </summary>
    internal static Dictionary<string, bool> ApplyAutoloadDisabledPatterns(
        IReadOnlyList<string> allPaths,
        IReadOnlyList<string> patterns,
        string baseDir)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var pattern in patterns)
        {
            var isOverride = pattern.StartsWith('+') || pattern.StartsWith('-') || pattern.StartsWith('!');
            var target = isOverride ? pattern[1..] : pattern;
            var enabled = !pattern.StartsWith('-') && !pattern.StartsWith('!');
            var exact = pattern.StartsWith('+') || pattern.StartsWith('-');
            string[] single = [target];
            foreach (var filePath in allPaths)
            {
                var matched = exact
                    ? MatchesAnyExactPattern(filePath, single, baseDir)
                    : MatchesAnyPattern(filePath, single, baseDir);
                if (matched)
                {
                    result[filePath] = enabled;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// TS <c>resourcePrecedenceRank</c>: lower wins. Built-ins load last (5), package resources
    /// next (4), then project/user × settings/auto-discovered (0…3).
    /// </summary>
    internal static int ResourcePrecedenceRank(PathMetadata m)
    {
        if (string.Equals(m.Source, "builtin", StringComparison.Ordinal))
        {
            return 5;
        }

        if (string.Equals(m.Origin, SourceOrigin.Package, StringComparison.Ordinal))
        {
            return 4;
        }

        var scopeBase = string.Equals(m.Scope, SourceScope.Project, StringComparison.Ordinal) ? 0 : 2;
        return scopeBase + (string.Equals(m.Source, "local", StringComparison.Ordinal) ? 0 : 1);
    }

    private static bool Matches(string value, string pattern) =>
        new Minimatch(pattern, new GlobOptions()).Match(value);

    private static List<string> Slice(List<string> overrides, char prefix)
    {
        var result = new List<string>();
        foreach (var pattern in overrides)
        {
            if (pattern.StartsWith(prefix))
            {
                result.Add(pattern[1..]);
            }
        }

        return result;
    }
}
