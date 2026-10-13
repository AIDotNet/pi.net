// ============================================================================
// Skills — port of core/skills.ts (4e-2d)
// ============================================================================
//
// Discovery of SKILL.md files and root-level markdown skills, per-directory
// .gitignore/.ignore/.fdignore filtering, Agent-Skills-spec validation and
// prompt formatting. The npm `ignore` package is reimplemented here as
// <see cref="IgnoreMatcher"/> with the subset of gitignore semantics the
// original relies on (`*` never crosses `/`, `**` spans levels, a mid-path
// `/` anchors the pattern, a trailing `/` restricts to directories, `!`
// negates and the last matching rule wins). Directory exclusion propagates to
// contents the way git prunes, which is also what `ignore` implements.

using System.Text;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>Port of the TS <c>Skill</c> interface.</summary>
public sealed record Skill
{
    /// <summary>Frontmatter name, or the parent directory name as fallback.</summary>
    public required string Name { get; init; }

    /// <summary>Frontmatter description (required for the skill to load).</summary>
    public required string Description { get; init; }

    /// <summary>Path of the markdown file the skill was loaded from.</summary>
    public required string FilePath { get; init; }

    /// <summary>The skill directory (parent of SKILL.md).</summary>
    public required string BaseDir { get; init; }

    /// <summary>Synthetic source info carrying the load origin.</summary>
    public required SourceInfo SourceInfo { get; init; }

    /// <summary>TS <c>disable-model-invocation === true</c>: prompt-invisible, /skill:name only.</summary>
    public required bool DisableModelInvocation { get; init; }
}

/// <summary>Port of the TS <c>LoadSkillsResult</c>.</summary>
public sealed record LoadSkillsResult
{
    public required IReadOnlyList<Skill> Skills { get; init; }

    public required IReadOnlyList<ResourceDiagnostic> Diagnostics { get; init; }
}

/// <summary>Port of the TS <c>LoadSkillsFromDirOptions</c>.</summary>
public sealed record LoadSkillsFromDirOptions
{
    /// <summary>Directory to scan for skills.</summary>
    public required string Dir { get; init; }

    /// <summary>Source identifier for these skills.</summary>
    public required string Source { get; init; }
}

/// <summary>Port of the TS <c>LoadSkillsOptions</c>.</summary>
public sealed record LoadSkillsOptions
{
    /// <summary>Working directory for project-local skills.</summary>
    public required string Cwd { get; init; }

    /// <summary>Agent config directory for global skills; defaults to <see cref="Config.GetAgentDir"/>.</summary>
    public string? AgentDir { get; init; }

    /// <summary>Explicit skill paths (files or directories).</summary>
    public IReadOnlyList<string> SkillPaths { get; init; } = [];

    /// <summary>Include default skills directories.</summary>
    public bool IncludeDefaults { get; init; }
}

/// <summary>Which tool loads skill files for the prompt. TS <c>"read" | "bash" | "indirect"</c>.</summary>
public enum SkillFileReadTool
{
    /// <summary>Name the read tool.</summary>
    Read,

    /// <summary>Name bash.</summary>
    Bash,

    /// <summary>Name none, for a reader reachable only through another tool.</summary>
    Indirect,
}

/// <summary>
/// Reimplementation of the npm <c>ignore</c> package, narrowed to what skills.ts exercises: rule
/// accumulation and <c>ignores(path)</c> over paths relative to the skills root (optionally with a
/// trailing <c>/</c> marking a directory). See the file header for the covered semantics.
/// </summary>
internal sealed class IgnoreMatcher
{
    private sealed record Rule(string[] Segments, bool Negated, bool DirOnly, bool Anchored, Regex[] SegmentRegexes);

    private readonly List<Rule> _rules = [];

    public void Add(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            AddOne(raw);
        }
    }

    private void AddOne(string raw)
    {
        // Trailing spaces are stripped unless escaped with a backslash (gitignore spec 2.22.1).
        var pattern = raw;
        while (pattern.Length > 0 && pattern.EndsWith(' ') && !pattern.EndsWith("\\ ", StringComparison.Ordinal))
        {
            pattern = pattern[..^1];
        }

        if (pattern.Length == 0)
        {
            return;
        }

        var negated = false;
        if (pattern.StartsWith('!'))
        {
            negated = true;
            pattern = pattern[1..];
        }
        else if (pattern.StartsWith("\\!", StringComparison.Ordinal))
        {
            pattern = pattern[1..];
        }

        var dirOnly = false;
        if (pattern.EndsWith('/'))
        {
            dirOnly = true;
            pattern = pattern.TrimEnd('/');
        }

        if (pattern.Length == 0)
        {
            return;
        }

        // A `/` anywhere (after trailing-slash removal) anchors the pattern to the ignore file's
        // directory; a bare name matches at every level. The leading `/` is redundant.
        var anchored = pattern.Contains('/');
        if (pattern.StartsWith('/'))
        {
            pattern = pattern[1..];
            anchored = true;
        }

        if (pattern.Length == 0)
        {
            return;
        }

        var segments = pattern.Split('/').Where(s => s.Length > 0).ToArray();
        if (segments.Length == 0)
        {
            return;
        }

        _rules.Add(new Rule(segments, negated, dirOnly, anchored, [.. segments.Select(SegmentToRegex)]));
    }

    /// <summary>The npm package's <c>ignores(path)</c>. A trailing <c>/</c> marks the path a directory.</summary>
    public bool Ignores(string path)
    {
        var isDir = path.EndsWith('/');
        var testPath = path.TrimEnd('/');
        if (testPath.Length == 0)
        {
            return false;
        }

        var segments = testPath.Split('/');
        Rule? lastMatch = null;
        foreach (var rule in _rules)
        {
            if (MatchesRule(rule, segments, isDir))
            {
                lastMatch = rule;
            }
        }

        return lastMatch is { Negated: false };
    }

    /// <summary>
    /// A rule matches the path itself or — the git exclusion pruning behaviour, which `ignore`
    /// reproduces via its `(/|$)` directory regexes — any ancestor directory of it.
    /// </summary>
    private static bool MatchesRule(Rule rule, string[] segments, bool isDir)
    {
        if (MatchesSegments(rule, segments, 0, segments.Length) && !(rule.DirOnly && !isDir))
        {
            return true;
        }

        for (var end = 1; end < segments.Length; end++)
        {
            if (MatchesSegments(rule, segments, 0, end))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Unanchored rules match at any starting level; anchored ones only from the root.</summary>
    private static bool MatchesSegments(Rule rule, string[] segments, int start, int end)
    {
        if (rule.Anchored)
        {
            return MatchFrom(rule.Segments, rule.SegmentRegexes, 0, segments, start, end);
        }

        for (var s = start; s <= end; s++)
        {
            if (MatchFrom(rule.Segments, rule.SegmentRegexes, 0, segments, s, end))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Segment-wise glob match with backtracking <c>**</c>.</summary>
    private static bool MatchFrom(string[] pattern, Regex[] regexes, int pi, string[] segments, int si, int end)
    {
        while (pi < pattern.Length)
        {
            if (pattern[pi] == "**")
            {
                // A trailing `**` matches everything inside, not the parent itself (gitignore spec).
                if (pi == pattern.Length - 1)
                {
                    return si < end;
                }

                for (var k = si; k <= end; k++)
                {
                    if (MatchFrom(pattern, regexes, pi + 1, segments, k, end))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (si >= end || !regexes[pi].IsMatch(segments[si]))
            {
                return false;
            }

            pi++;
            si++;
        }

        return si == end;
    }

    /// <summary>Compile one path segment: <c>*</c>→<c>[^/]*</c>, <c>?</c>→<c>[^/]</c>, <c>[…]</c> classes, rest literal.</summary>
    private static Regex SegmentToRegex(string segment)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            switch (c)
            {
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[':
                {
                    var close = segment.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        sb.Append("\\[");
                        break;
                    }

                    var inner = segment[(i + 1)..close];
                    if (inner.StartsWith('!'))
                    {
                        inner = "^" + inner[1..];
                    }

                    sb.Append('[').Append(inner.Replace("\\", "\\\\").Replace("]", "\\]")).Append(']');
                    i = close;
                    break;
                }
                case '\\':
                    if (i + 1 < segment.Length)
                    {
                        sb.Append(Regex.Escape(segment[i + 1].ToString()));
                        i++;
                    }
                    else
                    {
                        sb.Append("\\\\");
                    }

                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
    }
}

/// <summary>Port of <c>core/skills.ts</c>.</summary>
public static class Skills
{
    /// <summary>Max name length per spec.</summary>
    private const int MaxNameLength = 64;

    /// <summary>Max description length per spec.</summary>
    private const int MaxDescriptionLength = 1024;

    /// <summary>
    /// The ignore files consulted per directory. TS declares this list separately in both
    /// <c>skills.ts</c> and <c>package-manager.ts</c>; the port keeps one copy here and shares it
    /// (see difference C164).
    /// </summary>
    internal static readonly string[] IgnoreFileNames = [".gitignore", ".ignore", ".fdignore"];

    private static readonly Regex NamePattern = new("^[a-z0-9-]+$", RegexOptions.Compiled);

    /// <summary>The TS <c>toPosixPath</c> helper, shared with the package manager (difference C164).</summary>
    internal static string ToPosixPath(string p) => p.Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>The TS <c>prefixIgnorePattern</c>: comment filtering, <c>!</c> extraction and directory prefixing.</summary>
    internal static string? PrefixIgnorePattern(string line, string prefix)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.StartsWith('#') && !trimmed.StartsWith("\\#", StringComparison.Ordinal))
        {
            return null;
        }

        var pattern = line;
        var negated = false;

        if (pattern.StartsWith('!'))
        {
            negated = true;
            pattern = pattern[1..];
        }
        else if (pattern.StartsWith("\\!", StringComparison.Ordinal))
        {
            pattern = pattern[1..];
        }

        if (pattern.StartsWith('/'))
        {
            pattern = pattern[1..];
        }

        var prefixed = prefix.Length > 0 ? $"{prefix}{pattern}" : pattern;
        return negated ? $"!{prefixed}" : prefixed;
    }

    /// <summary>Load the <c>.gitignore</c>/<c>.ignore</c>/<c>.fdignore</c> rules of <paramref name="dir"/>, prefixed for <paramref name="rootDir"/>.</summary>
    internal static void AddIgnoreRules(IgnoreMatcher ig, string dir, string rootDir)
    {
        // Node's path.relative yields "" for the root itself; .NET yields ".". A "." prefix would
        // anchor every rule to a literal "." segment and silently disable the whole ignore file.
        var relativeDir = Path.GetRelativePath(rootDir, dir);
        if (relativeDir == ".")
        {
            relativeDir = "";
        }

        var prefix = relativeDir.Length > 0 ? $"{ToPosixPath(relativeDir)}/" : "";

        foreach (var filename in IgnoreFileNames)
        {
            var ignorePath = NodePath.Join(dir, filename);
            if (!File.Exists(ignorePath))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(ignorePath);
                var patterns = content.Split(["\r\n", "\n"], StringSplitOptions.None)
                    .Select(line => PrefixIgnorePattern(line, prefix))
                    .Where(line => line is not null)
                    .Cast<string>()
                    .ToList();
                if (patterns.Count > 0)
                {
                    ig.Add(patterns);
                }
            }
            catch
            {
                // Unreadable ignore files are silently skipped, as in the original.
            }
        }
    }

    /// <summary>
    /// Validate skill name per Agent Skills spec. Returns validation error messages (empty if valid).
    /// </summary>
    private static List<string> ValidateName(string name)
    {
        var errors = new List<string>();

        if (name.Length > MaxNameLength)
        {
            errors.Add($"name exceeds {MaxNameLength} characters ({name.Length})");
        }

        if (!NamePattern.IsMatch(name))
        {
            errors.Add("name contains invalid characters (must be lowercase a-z, 0-9, hyphens only)");
        }

        if (name.StartsWith('-') || name.EndsWith('-'))
        {
            errors.Add("name must not start or end with a hyphen");
        }

        if (name.Contains("--", StringComparison.Ordinal))
        {
            errors.Add("name must not contain consecutive hyphens");
        }

        return errors;
    }

    /// <summary>Validate description per Agent Skills spec.</summary>
    private static List<string> ValidateDescription(object? description)
    {
        var errors = new List<string>();

        if (description is not string text || text.Trim().Length == 0)
        {
            errors.Add("description is required");
        }
        else if (text.Length > MaxDescriptionLength)
        {
            errors.Add($"description exceeds {MaxDescriptionLength} characters ({text.Length})");
        }

        return errors;
    }

    private static SourceInfo CreateSkillSourceInfo(string filePath, string baseDir, string source) =>
        source switch
        {
            "user" => SourceInfos.CreateSynthetic(filePath, "local", scope: "user", baseDir: baseDir),
            "project" => SourceInfos.CreateSynthetic(filePath, "local", scope: "project", baseDir: baseDir),
            "path" => SourceInfos.CreateSynthetic(filePath, "local", baseDir: baseDir),
            _ => SourceInfos.CreateSynthetic(filePath, source, baseDir: baseDir),
        };

    /// <summary>
    /// Load skills from a directory.
    /// <para>
    /// Discovery rules: a directory containing SKILL.md is a skill root and is not recursed further;
    /// otherwise direct <c>.md</c> children of the root load; subdirectories are searched for SKILL.md.
    /// </para>
    /// </summary>
    public static LoadSkillsResult LoadSkillsFromDir(LoadSkillsFromDirOptions options) =>
        LoadSkillsFromDirInternal(options.Dir, options.Source, includeRootFiles: true);

    private static LoadSkillsResult LoadSkillsFromDirInternal(
        string dir,
        string source,
        bool includeRootFiles,
        IgnoreMatcher? ignoreMatcher = null,
        string? rootDir = null)
    {
        var skills = new List<Skill>();
        var diagnostics = new List<ResourceDiagnostic>();

        if (!Directory.Exists(dir) && !File.Exists(dir))
        {
            return new LoadSkillsResult { Skills = skills, Diagnostics = diagnostics };
        }

        var root = rootDir ?? dir;
        var ig = ignoreMatcher ?? new IgnoreMatcher();
        AddIgnoreRules(ig, dir, root);

        try
        {
            // readdirSync yields directory-bucketed (roughly alphabetical) order on NTFS; sort for
            // determinism since EnumerateFileSystemInfos does not guarantee order.
            var entries = new DirectoryInfo(dir)
                .EnumerateFileSystemInfos()
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var entry in entries)
            {
                if (entry.Name != "SKILL.md")
                {
                    continue;
                }

                var fullPath = entry.FullName;

                var attrs = entry.Attributes;
                var isDirectoryLstat = (attrs & FileAttributes.Directory) != 0;
                var isReparse = (attrs & FileAttributes.ReparsePoint) != 0;
                var isFile = !isDirectoryLstat && !isReparse; // TS entry.isFile() (lstat semantics)
                if (isReparse)
                {
                    try
                    {
                        isFile = (File.GetAttributes(fullPath) & FileAttributes.Directory) == 0; // statSync
                    }
                    catch
                    {
                        continue;
                    }
                }

                var relPath = ToPosixPath(Path.GetRelativePath(root, fullPath));
                if (!isFile || ig.Ignores(relPath))
                {
                    continue;
                }

                var result = LoadSkillFromFile(fullPath, source);
                if (result.Skill is not null)
                {
                    skills.Add(result.Skill);
                }

                diagnostics.AddRange(result.Diagnostics);
                return new LoadSkillsResult { Skills = skills, Diagnostics = diagnostics };
            }

            foreach (var entry in entries)
            {
                if (entry.Name.StartsWith('.'))
                {
                    continue;
                }

                // Skip node_modules to avoid scanning dependencies.
                if (entry.Name == "node_modules")
                {
                    continue;
                }

                var fullPath = entry.FullName;

                var attrs = entry.Attributes;
                var isDirectory = (attrs & FileAttributes.Directory) != 0;
                var isReparse = (attrs & FileAttributes.ReparsePoint) != 0;
                var isFile = !isDirectory && !isReparse;
                if (isReparse)
                {
                    try
                    {
                        var targetAttrs = File.GetAttributes(fullPath); // statSync follows the link
                        isDirectory = (targetAttrs & FileAttributes.Directory) != 0;
                        isFile = (targetAttrs & FileAttributes.Directory) == 0;
                    }
                    catch
                    {
                        // Broken symlink, skip it.
                        continue;
                    }
                }

                var relPath = ToPosixPath(Path.GetRelativePath(root, fullPath));
                var ignorePath = isDirectory ? $"{relPath}/" : relPath;
                if (ig.Ignores(ignorePath))
                {
                    continue;
                }

                if (isDirectory)
                {
                    var subResult = LoadSkillsFromDirInternal(fullPath, source, false, ig, root);
                    skills.AddRange(subResult.Skills);
                    diagnostics.AddRange(subResult.Diagnostics);
                    continue;
                }

                if (!isFile || !includeRootFiles || !entry.Name.EndsWith(".md", StringComparison.Ordinal))
                {
                    continue;
                }

                var result = LoadSkillFromFile(fullPath, source);
                if (result.Skill is not null)
                {
                    skills.Add(result.Skill);
                }

                diagnostics.AddRange(result.Diagnostics);
            }
        }
        catch
        {
            // Unreadable directories contribute nothing, as in the original's empty catch.
        }

        return new LoadSkillsResult { Skills = skills, Diagnostics = diagnostics };
    }

    private static (Skill? Skill, List<ResourceDiagnostic> Diagnostics) LoadSkillFromFile(
        string filePath,
        string source)
    {
        var diagnostics = new List<ResourceDiagnostic>();
        var isDeclaredSkill = Path.GetFileName(filePath) == "SKILL.md";

        string rawContent;
        try
        {
            rawContent = File.ReadAllText(filePath);
        }
        catch (Exception error)
        {
            // Known divergence: .NET exception text is not byte-identical to Node's.
            diagnostics.Add(ResourceDiagnostic.Warning(error.Message, filePath));
            return (null, diagnostics);
        }

        ParsedFrontmatter parsed;
        try
        {
            parsed = Frontmatter.Parse(rawContent);
        }
        catch (Exception error)
        {
            if (isDeclaredSkill)
            {
                diagnostics.Add(ResourceDiagnostic.Warning(error.Message, filePath));
            }

            return (null, diagnostics);
        }

        var descriptionValue = parsed.Frontmatter.TryGetValue("description", out var dv) ? dv : null;
        var hasDescription = descriptionValue is string d && d.Trim().Length > 0;
        if (!isDeclaredSkill && !hasDescription)
        {
            return (null, diagnostics);
        }

        var skillDir = NodePath.Dirname(filePath);
        var parentDirName = NodePath.Basename(skillDir);

        // Validate description.
        var descErrors = ValidateDescription(descriptionValue);
        foreach (var error in descErrors)
        {
            diagnostics.Add(ResourceDiagnostic.Warning(error, filePath));
        }

        // Use name from frontmatter, or fall back to parent directory name (`||` treats "" as unset).
        var frontmatterName = Frontmatter.GetString(parsed.Frontmatter, "name");
        var name = !string.IsNullOrEmpty(frontmatterName) ? frontmatterName : parentDirName;

        // Validate name.
        var nameErrors = ValidateName(name);
        foreach (var error in nameErrors)
        {
            diagnostics.Add(ResourceDiagnostic.Warning(error, filePath));
        }

        // Still load the skill even with warnings, unless description is missing or empty.
        if (!hasDescription)
        {
            return (null, diagnostics);
        }

        return
        (
            new Skill
            {
                Name = name,
                Description = (string)descriptionValue!,
                FilePath = filePath,
                BaseDir = skillDir,
                SourceInfo = CreateSkillSourceInfo(filePath, skillDir, source),
                DisableModelInvocation = Frontmatter.GetBool(parsed.Frontmatter, "disable-model-invocation") == true,
            },
            diagnostics
        );
    }

    /// <summary>
    /// Format skills for inclusion in a system prompt. Uses XML format per the Agent Skills standard
    /// (https://agentskills.io/integrate-skills). Skills with <c>disableModelInvocation</c> are
    /// excluded from the prompt (they can only be invoked explicitly via /skill:name commands).
    /// </summary>
    public static string FormatSkillsForPrompt(
        IReadOnlyList<Skill> skills,
        SkillFileReadTool fileReadTool = SkillFileReadTool.Read)
    {
        var visibleSkills = skills.Where(s => !s.DisableModelInvocation).ToList();

        if (visibleSkills.Count == 0)
        {
            return "";
        }

        var lines = new List<string>
        {
            "\n\nThe following skills provide specialized instructions for specific tasks.",
            fileReadTool switch
            {
                SkillFileReadTool.Read =>
                    "Use the read tool to load a skill's file when the task matches its description.",
                SkillFileReadTool.Bash =>
                    "Use bash to load a skill's file when the task matches its description.",
                _ => "Load a skill's file when the task matches its description.",
            },
            "When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.",
            "",
            "<available_skills>",
        };

        foreach (var skill in visibleSkills)
        {
            lines.Add("  <skill>");
            lines.Add($"    <name>{EscapeXml(skill.Name)}</name>");
            lines.Add($"    <description>{EscapeXml(skill.Description)}</description>");
            lines.Add($"    <location>{EscapeXml(skill.FilePath)}</location>");
            lines.Add("  </skill>");
        }

        lines.Add("</available_skills>");

        return string.Join("\n", lines);
    }

    private static string EscapeXml(string str) =>
        str.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);

    /// <summary>
    /// Load skills from all configured locations. Returns skills and any validation diagnostics.
    /// </summary>
    public static LoadSkillsResult LoadSkills(LoadSkillsOptions options)
    {
        // Resolve agentDir - if not provided, use default from config.
        var resolvedCwd = Paths.ResolvePath(options.Cwd);
        var resolvedAgentDir = Paths.ResolvePath(options.AgentDir ?? Config.GetAgentDir());

        var skillMap = new Dictionary<string, Skill>(StringComparer.Ordinal);
        var realPathSet = new HashSet<string>(StringComparer.Ordinal);
        var allDiagnostics = new List<ResourceDiagnostic>();
        var collisionDiagnostics = new List<ResourceDiagnostic>();

        void AddSkills(LoadSkillsResult result)
        {
            allDiagnostics.AddRange(result.Diagnostics);
            foreach (var skill in result.Skills)
            {
                // Resolve symlinks to detect duplicate files.
                var realPath = Paths.CanonicalizePath(skill.FilePath);

                // Skip silently if we've already loaded this exact file (via symlink).
                if (realPathSet.Contains(realPath))
                {
                    continue;
                }

                if (skillMap.TryGetValue(skill.Name, out var existing))
                {
                    collisionDiagnostics.Add(ResourceDiagnostic.CollisionOf(
                        $"name \"{skill.Name}\" collision",
                        skill.FilePath,
                        new ResourceCollision
                        {
                            ResourceType = ResourceType.Skill,
                            Name = skill.Name,
                            WinnerPath = existing.FilePath,
                            LoserPath = skill.FilePath,
                        }));
                }
                else
                {
                    skillMap.Add(skill.Name, skill);
                    realPathSet.Add(realPath);
                }
            }
        }

        if (options.IncludeDefaults)
        {
            AddSkills(LoadSkillsFromDirInternal(NodePath.Join(resolvedAgentDir, "skills"), "user", true));
            AddSkills(LoadSkillsFromDirInternal(
                NodePath.Join(resolvedCwd, Config.ConfigDirName, "skills"), "project", true));
        }

        var userSkillsDir = NodePath.Join(resolvedAgentDir, "skills");
        var projectSkillsDir = NodePath.Join(resolvedCwd, Config.ConfigDirName, "skills");

        static bool IsUnderPath(string target, string root)
        {
            var normalizedRoot = Path.GetFullPath(root);
            if (string.Equals(target, normalizedRoot, StringComparison.Ordinal))
            {
                return true;
            }

            var separator = Path.DirectorySeparatorChar;
            var prefix = normalizedRoot.EndsWith(separator) ? normalizedRoot : $"{normalizedRoot}{separator}";
            return target.StartsWith(prefix, StringComparison.Ordinal);
        }

        string GetSource(string resolvedPath)
        {
            if (!options.IncludeDefaults)
            {
                if (IsUnderPath(resolvedPath, userSkillsDir))
                {
                    return "user";
                }

                if (IsUnderPath(resolvedPath, projectSkillsDir))
                {
                    return "project";
                }
            }

            return "path";
        }

        foreach (var rawPath in options.SkillPaths)
        {
            var resolvedPath = Paths.ResolvePath(rawPath, resolvedCwd, new PathInputOptions(Trim: true));
            if (!Directory.Exists(resolvedPath) && !File.Exists(resolvedPath))
            {
                allDiagnostics.Add(ResourceDiagnostic.Warning("skill path does not exist", resolvedPath));
                continue;
            }

            try
            {
                var attrs = File.GetAttributes(resolvedPath);
                var source = GetSource(resolvedPath);
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    AddSkills(LoadSkillsFromDirInternal(resolvedPath, source, true));
                }
                else if (resolvedPath.EndsWith(".md", StringComparison.Ordinal))
                {
                    var (skill, skillDiagnostics) = LoadSkillFromFile(resolvedPath, source);
                    if (skill is not null)
                    {
                        AddSkills(new LoadSkillsResult
                        {
                            Skills = [skill],
                            Diagnostics = skillDiagnostics,
                        });
                    }
                    else
                    {
                        allDiagnostics.AddRange(skillDiagnostics);
                    }
                }
                else
                {
                    allDiagnostics.Add(ResourceDiagnostic.Warning(
                        "skill path is not a markdown file", resolvedPath));
                }
            }
            catch (Exception error)
            {
                allDiagnostics.Add(ResourceDiagnostic.Warning(error.Message, resolvedPath));
            }
        }

        return new LoadSkillsResult
        {
            Skills = skillMap.Values.ToList(),
            Diagnostics = [.. allDiagnostics, .. collisionDiagnostics],
        };
    }
}
