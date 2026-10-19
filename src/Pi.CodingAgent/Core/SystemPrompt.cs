// ============================================================================
// System prompt — port of core/system-prompt.ts (4e-2d)
// ============================================================================
//
// Structured system prompt construction: ordered sections (preamble/tools/rules/docs/addendum/
// project_context/skills/cwd plus extension-provided ones), a forced-prompt opaque mode, and a
// section diff for incremental updates. Section insertion order is significant (it becomes the
// replay order of SystemMessage.sections), so ordered dictionaries are used throughout; callers
// passing custom sections keep their enumeration order too.

using System.Collections;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>A pre-loaded context file. TS <c>Array&lt;{ path, content }&gt;</c> element.</summary>
public sealed record ContextFile(string Path, string Content);

/// <summary>The complete prompt state. TS <c>{ content, sections? }</c>.</summary>
public sealed record SystemPromptState(string Content, IReadOnlyDictionary<string, string>? Sections);

/// <summary>Port of the TS <c>BuildSystemPromptOptions</c>.</summary>
public sealed record BuildSystemPromptOptions
{
    /// <summary>Custom system prompt (replaces the default prefix).</summary>
    public string? CustomPrompt { get; init; }

    /// <summary>Exact full prompt replacement set by a before_agent_start handler.</summary>
    public string? ForceSystemPrompt { get; init; }

    /// <summary>Tools to include in prompt. Default: [read, bash, edit, write].</summary>
    public IReadOnlyList<string>? SelectedTools { get; init; }

    /// <summary>
    /// Selected tools whose declarations requests leave out (prepareLoadout hidden declarations).
    /// They are reachable only through another tool, so the tool list and rules leave them out too.
    /// </summary>
    public IReadOnlyList<string>? HiddenTools { get; init; }

    /// <summary>Optional one-line tool snippets keyed by tool name.</summary>
    public IReadOnlyDictionary<string, string>? ToolSnippets { get; init; }

    /// <summary>Guideline bullets contributed by each tool, keyed by tool name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? ToolGuidelines { get; init; }

    /// <summary>Additional guideline bullets appended to the default system prompt rules.</summary>
    public IReadOnlyList<string>? PromptGuidelines { get; init; }

    /// <summary>Text appended from user configuration before project context, skills, and cwd.</summary>
    public string? AppendSystemPrompt { get; init; }

    /// <summary>Additional XML-wrapped prompt sections keyed by tag name.</summary>
    public IReadOnlyDictionary<string, string>? Sections { get; init; }

    /// <summary>Working directory.</summary>
    public required string Cwd { get; init; }

    /// <summary>Pre-loaded context files.</summary>
    public IReadOnlyList<ContextFile>? ContextFiles { get; init; }

    /// <summary>Pre-loaded skills.</summary>
    public IReadOnlyList<Skill>? Skills { get; init; }
}

/// <summary>
/// Collection-complete shape exposed to extensions. TS <c>NormalizedBuildSystemPromptOptions</c>.
/// </summary>
public sealed record NormalizedBuildSystemPromptOptions
{
    public string? CustomPrompt { get; init; }

    public string? ForceSystemPrompt { get; init; }

    public required IReadOnlyList<string> SelectedTools { get; init; }

    public required IReadOnlyList<string> HiddenTools { get; init; }

    public required IReadOnlyDictionary<string, string> ToolSnippets { get; init; }

    public required IReadOnlyDictionary<string, IReadOnlyList<string>> ToolGuidelines { get; init; }

    public required IReadOnlyList<string> PromptGuidelines { get; init; }

    public required string AppendSystemPrompt { get; init; }

    /// <summary>
    /// Extension sections by tag name. Deliberately a concrete, writable, insertion-ordered map:
    /// TS handlers assign and delete <c>event.systemPromptOptions.sections[name]</c> in place — the
    /// MCP extension's <c>mcp_servers</c> section is written that way — and the caller reads the
    /// same object back from the event result. See difference C189.
    /// </summary>
    public required System.Collections.Generic.OrderedDictionary<string, string> Sections { get; init; }

    public required string Cwd { get; init; }

    public required IReadOnlyList<ContextFile> ContextFiles { get; init; }

    public required IReadOnlyList<Skill> Skills { get; init; }
}

/// <summary>Port of <c>core/system-prompt.ts</c>.</summary>
public static class SystemPrompt
{
    private const string DefaultPreamble =
        "You are an expert coding assistant operating inside pi, a coding agent harness. You help users by reading files, executing commands, editing code, and writing new files.";

    private const string DocsSection = """
        Pi documentation (read only when the user asks about pi itself, its SDK, extensions, themes, skills, or TUI):
        - Main documentation: {0}
        - Additional docs: {1}
        - Examples: {2} (extensions, custom tools, SDK)
        - When reading pi docs or examples, resolve docs/... under Additional docs and examples/... under Examples, not the current working directory
        - When asked about: extensions (docs/extensions.md, docs/dotnet-extensions.md, examples/extensions/), themes (docs/themes.md), skills (docs/skills.md), prompt templates (docs/prompt-templates.md), TUI components (docs/tui.md), keybindings (docs/keybindings.md), SDK integrations (docs/sdk.md), custom providers (docs/custom-provider.md), adding models (docs/models.md), pi packages (docs/packages.md), environment variables (docs/environment-variables.md), MCP servers (docs/mcp.md), codemode scripts and non-LLM models such as classifiers and image models (docs/codemode.md)
        - When working on pi topics, read the docs and examples, and follow .md cross-references before implementing
        - Always read pi .md files completely and follow links to related docs (e.g., tui.md for TUI API details)
        """;

    private static readonly System.Text.RegularExpressions.Regex SectionNamePattern =
        new("^[a-z][a-z0-9_-]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>An insertion-ordered string map: section order is the replay order.</summary>
    private static IReadOnlyDictionary<string, T> Ordered<T>(IEnumerable<KeyValuePair<string, T>> pairs)
    {
        var result = new System.Collections.Generic.OrderedDictionary<string, T>();
        foreach (var pair in pairs)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    /// <summary>Copy an input map preserving the caller's enumeration order.</summary>
    private static IReadOnlyDictionary<string, string> OrderedCopy(IReadOnlyDictionary<string, string>? source)
    {
        if (source is null || source.Count == 0)
        {
            return new System.Collections.Generic.OrderedDictionary<string, string>();
        }

        return Ordered(source);
    }

    /// <summary>
    /// The <c>sections</c> copy, kept writable. TS normalizes with <c>{ ...(input.sections ?? {}) }</c>
    /// and hands the copy to <c>before_agent_start</c> handlers, which mutate it in place; the port
    /// therefore cannot fold this one into the read-only <see cref="OrderedCopy{T}"/> shape
    /// (difference C189).
    /// </summary>
    private static System.Collections.Generic.OrderedDictionary<string, string> OrderedSectionsCopy(
        IReadOnlyDictionary<string, string>? source)
    {
        var result = new System.Collections.Generic.OrderedDictionary<string, string>();
        if (source is null)
        {
            return result;
        }

        foreach (var pair in source)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    /// <summary>TS <c>{ ...(x ?? {}) }</c> shallow copy, order-preserving.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> OrderedCopyGuidelines(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? source)
    {
        if (source is null || source.Count == 0)
        {
            return new System.Collections.Generic.OrderedDictionary<string, IReadOnlyList<string>>();
        }

        return Ordered(source.Select(pair =>
            new KeyValuePair<string, IReadOnlyList<string>>(pair.Key, [.. pair.Value])));
    }

    /// <summary>Normalize prompt input into the mutable, collection-complete shape exposed to extensions.</summary>
    public static NormalizedBuildSystemPromptOptions NormalizeBuildSystemPromptOptions(BuildSystemPromptOptions input) =>
        new()
        {
            CustomPrompt = input.CustomPrompt,
            ForceSystemPrompt = input.ForceSystemPrompt,
            SelectedTools = [.. (input.SelectedTools ?? ["read", "bash", "edit", "write"])],
            HiddenTools = [.. (input.HiddenTools ?? [])],
            ToolSnippets = OrderedCopy(input.ToolSnippets),
            ToolGuidelines = OrderedCopyGuidelines(input.ToolGuidelines),
            PromptGuidelines = [.. (input.PromptGuidelines ?? [])],
            AppendSystemPrompt = input.AppendSystemPrompt ?? "",
            Sections = OrderedSectionsCopy(input.Sections),
            Cwd = input.Cwd,
            ContextFiles = [.. (input.ContextFiles ?? [])],
            Skills = [.. (input.Skills ?? [])],
        };

    /// <summary>
    /// Widen the collection-complete shape back to the input shape. TS gets this for free from
    /// structural typing — <c>NormalizedBuildSystemPromptOptions</c> is an intersection type that
    /// still satisfies <c>BuildSystemPromptOptions</c>, so <c>buildSystemPrompt(normalized)</c>
    /// type-checks. C# records are nominal, so the widening is an explicit call. Only the caller
    /// knows it has normalized the input, hence no implicit conversion.
    /// </summary>
    public static BuildSystemPromptOptions ToBuildSystemPromptOptions(NormalizedBuildSystemPromptOptions input) =>
        new()
        {
            CustomPrompt = input.CustomPrompt,
            ForceSystemPrompt = input.ForceSystemPrompt,
            SelectedTools = input.SelectedTools,
            HiddenTools = input.HiddenTools,
            ToolSnippets = input.ToolSnippets,
            ToolGuidelines = input.ToolGuidelines,
            PromptGuidelines = input.PromptGuidelines,
            AppendSystemPrompt = input.AppendSystemPrompt,
            Sections = input.Sections,
            Cwd = input.Cwd,
            ContextFiles = input.ContextFiles,
            Skills = input.Skills,
        };

    private static string RenderProjectContext(IReadOnlyList<ContextFile> contextFiles) =>
        string.Join("\n\n",
        [
            "Project-specific instructions and guidelines:",
            .. contextFiles.Select(file =>
                $"<project_instructions path=\"{file.Path}\">\n{file.Content}\n</project_instructions>"),
        ]);

    private static string BuildRules(
        IReadOnlyList<string> selectedTools,
        IReadOnlyDictionary<string, IReadOnlyList<string>> toolGuidelines,
        IReadOnlyList<string> promptGuidelines)
    {
        var rules = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddRule(string rule)
        {
            var normalized = rule.Trim();
            if (normalized.Length == 0 || !seen.Add(normalized))
            {
                return;
            }

            rules.Add(normalized);
        }

        var hasBash = selectedTools.Contains("bash");
        var hasPowerShell = selectedTools.Contains("powershell");
        var hasGrep = selectedTools.Contains("grep");
        var hasFind = selectedTools.Contains("find");
        var hasLs = selectedTools.Contains("ls");

        if ((hasBash || hasPowerShell) && !hasGrep && !hasFind && !hasLs)
        {
            if (hasBash && hasPowerShell)
            {
                AddRule("Use bash or PowerShell for file operations like listing, searching, and finding files");
            }
            else if (hasPowerShell)
            {
                AddRule("Use PowerShell for file operations like listing, searching, and finding files");
            }
            else
            {
                AddRule("Use bash for file operations like ls, rg, find");
            }
        }

        foreach (var name in selectedTools)
        {
            if (toolGuidelines.TryGetValue(name, out var guidelines))
            {
                foreach (var rule in guidelines)
                {
                    AddRule(rule);
                }
            }
        }

        foreach (var rule in promptGuidelines)
        {
            AddRule(rule);
        }

        AddRule("Be concise in your responses");
        AddRule("Show file paths clearly when working with files");
        return string.Join("\n", rules.Select(rule => $"- {rule}"));
    }

    /// <summary>Build the ordered, independently replaceable sections of the structured system prompt.</summary>
    public static IReadOnlyDictionary<string, string> BuildSystemPromptSections(BuildSystemPromptOptions input)
    {
        var options = NormalizeBuildSystemPromptOptions(input);

        foreach (var name in options.Sections.Keys)
        {
            if (!SectionNamePattern.IsMatch(name) || name == "preamble")
            {
                throw new InvalidOperationException($"Invalid system prompt section name: {name}");
            }
        }

        var declaredTools = options.SelectedTools.Where(name => !options.HiddenTools.Contains(name)).ToList();
        var promptSections = new System.Collections.Generic.OrderedDictionary<string, string>();
        if (!string.IsNullOrEmpty(options.CustomPrompt))
        {
            promptSections["preamble"] = options.CustomPrompt;
        }
        else
        {
            promptSections["preamble"] = DefaultPreamble;
            var visibleTools = declaredTools.Where(name => options.ToolSnippets.ContainsKey(name)).ToList();
            var tools = visibleTools.Count > 0
                ? string.Join("\n", visibleTools.Select(name => $"- {name}: {options.ToolSnippets[name]}"))
                : "(none)";
            promptSections["tools"] =
                $"{tools}\n\nIn addition to the tools above, you may have access to other custom tools depending on the project.";
            promptSections["rules"] = BuildRules(declaredTools, options.ToolGuidelines, options.PromptGuidelines);
            promptSections["docs"] = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                DocsSection,
                Config.GetReadmePath(),
                Config.GetDocsPath(),
                Config.GetExamplesPath());
        }

        if (options.AppendSystemPrompt.Length > 0)
        {
            promptSections["addendum"] = options.AppendSystemPrompt;
        }

        if (options.ContextFiles.Count > 0)
        {
            promptSections["project_context"] = RenderProjectContext(options.ContextFiles);
        }

        // A hidden reader is still reachable through another tool, so skills stay but the hint names no tool.
        string? skillFileReadTool;
        if (declaredTools.Contains("read"))
        {
            skillFileReadTool = "read";
        }
        else if (declaredTools.Contains("bash"))
        {
            skillFileReadTool = "bash";
        }
        else if (options.SelectedTools.Contains("read") || options.SelectedTools.Contains("bash"))
        {
            skillFileReadTool = "indirect";
        }
        else
        {
            skillFileReadTool = null;
        }

        if (skillFileReadTool is not null && options.Skills.Count > 0)
        {
            var tool = skillFileReadTool switch
            {
                "read" => SkillFileReadTool.Read,
                "bash" => SkillFileReadTool.Bash,
                _ => SkillFileReadTool.Indirect,
            };
            var skillsPrompt = Skills.FormatSkillsForPrompt(options.Skills, tool).Trim();
            if (skillsPrompt.Length > 0)
            {
                promptSections["skills"] = skillsPrompt;
            }
        }

        promptSections["cwd"] = options.Cwd.Replace("\\", "/");
        foreach (var pair in options.Sections)
        {
            if (pair.Value.Length > 0)
            {
                promptSections[pair.Key] = pair.Value;
            }
        }

        var sections = new System.Collections.Generic.OrderedDictionary<string, string>
        {
            ["preamble"] = promptSections["preamble"],
        };
        foreach (var pair in promptSections)
        {
            if (pair.Key != "preamble")
            {
                sections[pair.Key] = $"<{pair.Key}>\n{pair.Value}\n</{pair.Key}>";
            }
        }

        return sections;
    }

    /// <summary>
    /// The complete prompt state for <paramref name="input"/>. A forced prompt is opaque and lives in
    /// content with no sections; otherwise content is empty and the structured sections carry the prompt.
    /// </summary>
    public static SystemPromptState BuildSystemPromptState(BuildSystemPromptOptions input)
    {
        if (input.ForceSystemPrompt is not null)
        {
            return new SystemPromptState(input.ForceSystemPrompt, null);
        }

        return new SystemPromptState("", BuildSystemPromptSections(input));
    }

    /// <summary>Build the system prompt text, rendered exactly as the transcript's system message replays it.</summary>
    public static string BuildSystemPrompt(BuildSystemPromptOptions input)
    {
        var state = BuildSystemPromptState(input);
        return Text.GetSystemMessageText(new SystemMessage
        {
            Content = MessageContent.From(state.Content),
            Sections = state.Sections is null
                ? null
                : Ordered(state.Sections.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))),
            Timestamp = 0,
        });
    }

    /// <summary>
    /// Diff the sections the model currently has (replayed from the transcript, so never null)
    /// against the desired ones. Returns a SystemMessage.sections patch, or null when nothing changed.
    /// </summary>
    public static IReadOnlyDictionary<string, string?>? DiffSystemPromptSections(
        IReadOnlyDictionary<string, string?> previous,
        IReadOnlyDictionary<string, string> current)
    {
        var patch = new System.Collections.Generic.OrderedDictionary<string, string?>();
        foreach (var pair in current)
        {
            if (!previous.TryGetValue(pair.Key, out var text) || text != pair.Value)
            {
                patch[pair.Key] = pair.Value;
            }
        }

        foreach (var name in previous.Keys)
        {
            if (!current.ContainsKey(name))
            {
                patch[name] = null;
            }
        }

        return patch.Count > 0 ? patch : null;
    }
}
