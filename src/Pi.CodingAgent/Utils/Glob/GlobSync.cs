// ============================================================================
// GlobSync — minimal port of node:fs `globSync` (package-manager subset, 4e-3c-2)
// ============================================================================
//
// `core/package-manager.ts` calls `globSync(pattern, { cwd: root })` from exactly one place:
// `expandPackageGlob`, which turns a package manifest's glob entries (for example
// `extensions/*.ts`) into concrete paths under the package root. Upstream `globSync` is a full
// directory walker with negation, brace expansion, `dot`/`nodir`/`ignore`/`follow` options and an
// absolute/relative result mode; the port needs only the subset that call site exercises, so this
// walker descends the pattern segment by segment rather than enumerating the whole tree — a package
// root contains `node_modules`, and walking it eagerly would be ruinous.
//
// Covered semantics (upstream defaults unless noted):
//   * `/` is the only separator. Results are posix-relative to `cwd`, ordinal-sorted, and carry no
//     trailing slash (upstream's `mark`/`nodir: false` shape).
//   * A segment without magic matches one path segment via <see cref="Minimatch"/>, which also
//     supplies brace expansion and character classes. A segment with magic never matches a name
//     starting with `.` (`dot: false`), and `**` never descends into a dot-directory.
//   * `**` spans zero or more path segments. A trailing `**` also yields the entries it descends
//     through, matching upstream's "directories are results too".
//   * A trailing `/` on the pattern restricts the match to directories.
//
// Not covered (no caller needs it): negation (`!pattern`), `nodir`/`onlyFiles`, `ignore` lists,
// `follow`, absolute patterns, `platform`-specific escaping and `windowsPathsNoEscape`.

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Port of the subset of node:fs <c>globSync</c> that <c>core/package-manager.ts</c> uses.
/// </summary>
internal static class GlobSync
{
    /// <summary>
    /// Expand <paramref name="pattern"/> against <paramref name="cwd"/>, returning posix-relative
    /// matches in ordinal order. Mirrors <c>globSync(pattern, { cwd })</c>.
    /// </summary>
    public static IReadOnlyList<string> Sync(string pattern, string cwd)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(cwd);

        var results = new List<string>();
        if (!Directory.Exists(cwd))
        {
            return results;
        }

        // Upstream treats a leading `/` as "absolute pattern"; the only caller passes relative
        // entries out of a package manifest, so leading slashes are simply dropped. A trailing `/`
        // is meaningful, though — it restricts the match to directories — so it is captured first.
        var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return results;
        }

        Walk(cwd, segments, 0, "", pattern.EndsWith('/'), results);
        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private static void Walk(
        string dir,
        string[] segments,
        int index,
        string relativePrefix,
        bool directoryOnly,
        List<string> results)
    {
        if (index >= segments.Length)
        {
            return;
        }

        var segment = segments[index];
        var isLast = index == segments.Length - 1;

        if (segment == "**")
        {
            if (isLast)
            {
                // A trailing `**` matches every entry at every depth, files and directories alike.
                foreach (var entry in Entries(dir))
                {
                    if (IsDotName(entry.Name))
                    {
                        continue;
                    }

                    if (!directoryOnly || entry.IsDirectory)
                    {
                        results.Add(relativePrefix + entry.Name);
                    }

                    if (entry.IsDirectory)
                    {
                        Walk(entry.FullPath, segments, index, relativePrefix + entry.Name + "/", directoryOnly, results);
                    }
                }

                return;
            }

            // `**` spans zero segments …
            Walk(dir, segments, index + 1, relativePrefix, directoryOnly, results);

            // … or one or more, in which case it stays active at the same index.
            foreach (var entry in Entries(dir))
            {
                if (!entry.IsDirectory || IsDotName(entry.Name))
                {
                    continue;
                }

                Walk(entry.FullPath, segments, index, relativePrefix + entry.Name + "/", directoryOnly, results);
            }

            return;
        }

        var matcher = HasMagic(segment) ? new Minimatch(segment, new GlobOptions()) : null;
        foreach (var entry in Entries(dir))
        {
            if (IsDotName(entry.Name))
            {
                continue;
            }

            if (!MatchesSegment(segment, matcher, entry.Name))
            {
                continue;
            }

            if (isLast)
            {
                if (directoryOnly && !entry.IsDirectory)
                {
                    continue;
                }

                results.Add(relativePrefix + entry.Name);
                continue;
            }

            if (entry.IsDirectory)
            {
                Walk(entry.FullPath, segments, index + 1, relativePrefix + entry.Name + "/", directoryOnly, results);
            }
        }
    }

    private static bool MatchesSegment(string segment, Minimatch? matcher, string name)
        => matcher is null ? string.Equals(segment, name, StringComparison.Ordinal) : matcher.Match(name);

    private static bool HasMagic(string segment) => Glob.GlobMagic.IsMatch(segment);

    private static bool IsDotName(string name) => name.StartsWith('.');

    private readonly record struct Entry(string Name, string FullPath, bool IsDirectory);

    private static List<Entry> Entries(string dir)
    {
        var entries = new List<Entry>();
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
            // `Directory.Exists` follows symlinks (upstream's `follow: true`); a dangling symlink
            // resolves to neither a file nor a directory and is skipped, which is what upstream's
            // ENOENT stat does.
            var isDirectory = Directory.Exists(fullPath);
            if (!isDirectory && !File.Exists(fullPath))
            {
                continue;
            }

            entries.Add(new Entry(Path.GetFileName(fullPath), fullPath, isDirectory));
        }

        return entries;
    }
}
