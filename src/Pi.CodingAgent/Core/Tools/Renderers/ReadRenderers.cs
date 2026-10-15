// ============================================================================
// Presentation for the read tool.
// Port of core/tools/renderers/read.ts
// ============================================================================

using Pi.Agent.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;
// `Pi.CodingAgent.Utils.Text` (the port of utils/text.ts) collides with the TUI's Text component;
// the alias keeps the component the unqualified name, as in the TS renderers.
using Text = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Core.Tools.Renderers;

/// <summary>How a read call is shown in the compact form. TS <c>CompactReadClassification</c>.</summary>
/// <remarks>
/// TS models <c>kind</c> as <c>"docs" | "resource" | "skill"</c>; the port keeps the strings.
/// </remarks>
internal sealed record CompactReadClassification(string Kind, string Label);

/// <summary>
/// Port of <c>core/tools/renderers/read.ts</c>. The renderers live apart from the implementation so a
/// process that only displays tool output does not load the execution path or its parameter schema;
/// <c>ReadTool</c> spreads these into its definition, so the tool's public shape is unchanged.
/// </summary>
public static class ReadRenderers
{
    /// <summary>Result lines shown while collapsed in the expanded view. TS literal <c>10</c>.</summary>
    internal const int CollapsedResultLines = 10;

    /// <summary>The resource files the compact form recognises by name. TS <c>COMPACT_RESOURCE_FILE_NAMES</c>.</summary>
    private static readonly HashSet<string> CompactResourceFileNames = new(StringComparer.Ordinal)
    {
        "AGENTS.override.md",
        "AGENTS.md",
        "AGENTS.MD",
        "CLAUDE.md",
        "CLAUDE.MD",
    };

    /// <summary>The <c>:start-end</c> line range suffix. TS <c>formatReadLineRange</c>.</summary>
    /// <remarks>
    /// Strict tool schemas make models send <c>null</c> for omitted optional fields, so the guard is a
    /// nullish test on the values rather than a presence test on the members. The TS interpolates
    /// <c>endLine</c> directly, so a computed <c>0</c> is falsy and drops the suffix entirely.
    /// </remarks>
    internal static string FormatReadLineRange(object? args, Theme theme)
    {
        var offset = RenderUtils.Arg(args, "offset");
        var limit = RenderUtils.Arg(args, "limit");
        if (offset.IsNullish && limit.IsNullish)
        {
            return "";
        }

        var startLine = offset.IsNullish ? 1d : RenderUtils.AsNumber(offset.Value) ?? 0d;
        double? endLine = limit.IsNullish ? null : startLine + (RenderUtils.AsNumber(limit.Value) ?? 0d) - 1;
        var suffix = endLine is { } value && Js.IsTruthy(value) ? $"-{RenderUtils.JsToString(value)}" : "";
        return theme.Fg(ThemeColors.Warning, $":{RenderUtils.JsToString(startLine)}{suffix}");
    }

    /// <summary>The plain tool call header. TS <c>formatReadCall</c>.</summary>
    internal static string FormatReadCall(object? args, Theme theme, string cwd)
    {
        var pathDisplay = RenderUtils.RenderToolPath(
            RenderUtils.Str(RenderUtils.Coalesce(RenderUtils.Arg(args, "file_path"), RenderUtils.Arg(args, "path")).Value),
            theme,
            cwd);
        return $"{theme.Fg(ThemeColors.ToolTitle, theme.Bold("read"))} {pathDisplay}{FormatReadLineRange(args, theme)}";
    }

    /// <summary>
    /// Drop trailing empty lines, so a file ending in a newline does not render a blank final line.
    /// TS <c>trimTrailingEmptyLines</c>.
    /// </summary>
    internal static IReadOnlyList<string> TrimTrailingEmptyLines(IReadOnlyList<string> lines)
    {
        var end = lines.Count;
        while (end > 0 && lines[end - 1].Length == 0)
        {
            end--;
        }

        return [.. lines.Take(end)];
    }

    /// <summary>The compact form's header, used for docs, resources and skills. TS <c>formatCompactReadCall</c>.</summary>
    internal static string FormatCompactReadCall(CompactReadClassification classification, object? args, Theme theme)
    {
        var expandHint = theme.Fg(
            ThemeColors.Dim,
            $" ({KeybindingHints.KeyText(AppKeybindings.AppToolsExpand)} to expand)");
        if (classification.Kind == "skill")
        {
            return theme.Fg(ThemeColors.CustomMessageLabel, "\x1b[1m[skill]\x1b[22m ") +
                theme.Fg(ThemeColors.CustomMessageText, classification.Label) +
                FormatReadLineRange(args, theme) +
                expandHint;
        }

        return theme.Fg(ThemeColors.ToolTitle, theme.Bold($"read {classification.Kind}")) +
            " " +
            theme.Fg(ThemeColors.Accent, classification.Label) +
            FormatReadLineRange(args, theme) +
            expandHint;
    }

    /// <summary>The result body: the file with syntax highlighting, then the truncation notice.</summary>
    /// <remarks>
    /// The TS <c>_cwd</c> parameter is unused, so the port drops it. TS <c>formatReadResult</c>.
    /// </remarks>
    internal static string FormatReadResult(
        object? args,
        AgentToolResult result,
        ToolRenderResultOptions options,
        Theme theme,
        bool showImages,
        bool isError)
    {
        if (!options.Expanded && !isError)
        {
            return "";
        }

        var rawPath = RenderUtils.Str(
            RenderUtils.Coalesce(RenderUtils.Arg(args, "file_path"), RenderUtils.Arg(args, "path")).Value);
        var output = RenderUtils.GetTextOutput(result, showImages);
        var lang = !isError && !string.IsNullOrEmpty(rawPath) ? ThemeHelpers.GetLanguageFromPath(rawPath) : null;
        var renderedLines = lang is not null
            ? ThemeHelpers.HighlightCode(RenderUtils.ReplaceTabs(output), lang)
            : output.Split('\n');
        var lines = TrimTrailingEmptyLines(renderedLines);
        var maxLines = options.Expanded ? lines.Count : CollapsedResultLines;
        var displayLines = lines.Take(maxLines);
        var remaining = lines.Count - maxLines;
        var text = $"\n{string.Join(
            "\n",
            displayLines.Select(line => lang is not null
                ? RenderUtils.ReplaceTabs(line)
                : theme.Fg(ThemeColors.ToolOutput, RenderUtils.ReplaceTabs(line))))}";
        if (remaining > 0)
        {
            text += $"{theme.Fg(ThemeColors.Muted, $"\n... ({remaining} more lines,")} " +
                $"{KeybindingHints.KeyHint(AppKeybindings.AppToolsExpand, "to expand")}" +
                $"{theme.Fg(ThemeColors.Muted, ")")}";
        }

        var truncation = (result.Details as ReadToolDetails)?.Truncation;
        if (truncation is { Truncated: true })
        {
            if (truncation.FirstLineExceedsLimit)
            {
                text += $"\n{theme.Fg(
                    ThemeColors.Warning,
                    $"[First line exceeds {Truncate.FormatSize(truncation.MaxBytes)} limit]")}";
            }
            else if (truncation.TruncatedBy == TruncatedBy.Lines)
            {
                text += $"\n{theme.Fg(
                    ThemeColors.Warning,
                    $"[Truncated: showing {truncation.OutputLines} of {truncation.TotalLines} lines " +
                    $"({truncation.MaxLines} line limit)]")}";
            }
            else
            {
                text += $"\n{theme.Fg(
                    ThemeColors.Warning,
                    $"[Truncated: {truncation.OutputLines} lines shown " +
                    $"({Truncate.FormatSize(truncation.MaxBytes)} limit)]")}";
            }
        }

        return text;
    }

    /// <summary>
    /// Classify a read call for the compact form: a skill file, a file inside the package's docs, or a
    /// recognised resource file. TS <c>getCompactReadClassification</c>.
    /// </summary>
    internal static CompactReadClassification? GetCompactReadClassification(object? args, string cwd)
    {
        var rawPath = RenderUtils.Str(
            RenderUtils.Coalesce(RenderUtils.Arg(args, "file_path"), RenderUtils.Arg(args, "path")).Value);
        if (string.IsNullOrEmpty(rawPath))
        {
            return null;
        }

        var absolutePath = ToolPathUtils.ResolveToCwd(rawPath, cwd);
        var fileName = NodePath.Basename(absolutePath);
        if (fileName == "SKILL.md")
        {
            var directory = NodePath.Basename(NodePath.Dirname(absolutePath));
            return new CompactReadClassification("skill", directory.Length > 0 ? directory : fileName);
        }

        var docs = GetPiDocsClassification(absolutePath);
        if (docs is not null)
        {
            return docs;
        }

        if (CompactResourceFileNames.Contains(fileName))
        {
            return new CompactReadClassification(
                "resource",
                Paths.FormatPathRelativeToCwdOrAbsolute(absolutePath, cwd));
        }

        return null;
    }

    /// <summary>
    /// Classify a path that lies inside the package directory as documentation. TS
    /// <c>getPiDocsClassification</c>.
    /// </summary>
    internal static CompactReadClassification? GetPiDocsClassification(string absolutePath)
    {
        var packageRoot = NodePath.Dirname(Config.GetReadmePath());
        var relativePath = NodePath.Relative(NodePath.Resolve(packageRoot), NodePath.Resolve(absolutePath));
        if (relativePath.Length == 0 ||
            relativePath == ".." ||
            relativePath.StartsWith($"..{NodePath.Separator}", StringComparison.Ordinal) ||
            NodePath.IsAbsolute(relativePath))
        {
            return null;
        }

        var label = ToPosixPath(relativePath);
        if (label == "README.md" ||
            label.StartsWith("docs/", StringComparison.Ordinal) ||
            label.StartsWith("examples/", StringComparison.Ordinal))
        {
            return new CompactReadClassification("docs", label);
        }

        return null;
    }

    /// <summary>TS <c>toPosixPath</c>.</summary>
    internal static string ToPosixPath(string filePath) => filePath.Replace(NodePath.Separator, '/');

    /// <summary>The renderer triple <c>ReadTool</c> spreads into its definition. TS <c>readRenderers</c>.</summary>
    public static readonly ToolRenderers Value = new()
    {
        RenderCall = (args, theme, context) =>
        {
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            var classification = context.Expanded ? null : GetCompactReadClassification(args, context.Cwd);
            text.SetText(classification is not null
                ? FormatCompactReadCall(classification, args, theme)
                : FormatReadCall(args, theme, context.Cwd));
            return text;
        },
        RenderResult = (result, options, theme, context) =>
        {
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(FormatReadResult(
                context.Args,
                result,
                options,
                theme,
                context.ShowImages,
                context.IsError));
            return text;
        },
    };
}
