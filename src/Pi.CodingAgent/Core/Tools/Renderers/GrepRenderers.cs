// ============================================================================
// Presentation for the grep tool.
// Port of core/tools/renderers/grep.ts
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

/// <summary>
/// Port of <c>core/tools/renderers/grep.ts</c>. The renderers live apart from the implementation so a
/// process that only displays tool output does not load the execution path or its parameter schema;
/// <c>GrepTool</c> spreads these into its definition, so the tool's public shape is unchanged.
/// </summary>
public static class GrepRenderers
{
    /// <summary>Result lines shown while collapsed. TS literal <c>15</c>.</summary>
    internal const int CollapsedResultLines = 15;

    /// <summary>The tool call header. TS <c>formatGrepCall</c>.</summary>
    internal static string FormatGrepCall(object? args, Theme theme)
    {
        var pattern = RenderUtils.Str(RenderUtils.Arg(args, "pattern").Value);
        var rawPath = RenderUtils.Str(RenderUtils.Arg(args, "path").Value);
        var path = rawPath is not null
            ? RenderUtils.ShortenPath(rawPath.Length > 0 ? rawPath : ".")
            : null;
        var glob = RenderUtils.Str(RenderUtils.Arg(args, "glob").Value);
        var limit = RenderUtils.Arg(args, "limit");
        var invalidArg = RenderUtils.InvalidArgText(theme);

        var text =
            theme.Fg(ThemeColors.ToolTitle, theme.Bold("grep")) +
            " " +
            (pattern is null ? invalidArg : theme.Fg(ThemeColors.Accent, $"/{pattern}/")) +
            theme.Fg(ThemeColors.ToolOutput, $" in {(path is null ? invalidArg : path)}");
        if (!string.IsNullOrEmpty(glob))
        {
            text += theme.Fg(ThemeColors.ToolOutput, $" ({glob})");
        }

        if (!limit.IsUndefined)
        {
            text += theme.Fg(ThemeColors.ToolOutput, $" limit {RenderUtils.JsToString(limit.Value)}");
        }

        return text;
    }

    /// <summary>The result body: the matches, then the truncation notices. TS <c>formatGrepResult</c>.</summary>
    internal static string FormatGrepResult(
        AgentToolResult result,
        ToolRenderResultOptions options,
        Theme theme,
        bool showImages)
    {
        var output = Js.Trim(RenderUtils.GetTextOutput(result, showImages));
        var text = "";
        if (output.Length > 0)
        {
            var lines = output.Split('\n');
            var maxLines = options.Expanded ? lines.Length : CollapsedResultLines;
            var displayLines = lines.Take(maxLines);
            var remaining = lines.Length - maxLines;
            text += $"\n{string.Join("\n", displayLines.Select(line => theme.Fg(ThemeColors.ToolOutput, line)))}";
            if (remaining > 0)
            {
                text += $"{theme.Fg(ThemeColors.Muted, $"\n... ({remaining} more lines,")} " +
                    $"{KeybindingHints.KeyHint(AppKeybindings.AppToolsExpand, "to expand")}" +
                    $"{theme.Fg(ThemeColors.Muted, ")")}";
            }
        }

        var details = result.Details as GrepToolDetails;
        var matchLimit = details?.MatchLimitReached;
        var truncation = details?.Truncation;
        var linesTruncated = details?.LinesTruncated is true;
        if (Js.IsTruthy(matchLimit) || truncation is { Truncated: true } || linesTruncated)
        {
            var warnings = new List<string>();
            if (Js.IsTruthy(matchLimit))
            {
                warnings.Add($"{matchLimit} matches limit");
            }

            if (truncation is { Truncated: true })
            {
                warnings.Add($"{Truncate.FormatSize(truncation.MaxBytes)} limit");
            }

            if (linesTruncated)
            {
                warnings.Add("some lines truncated");
            }

            text += $"\n{theme.Fg(ThemeColors.Warning, $"[Truncated: {string.Join(", ", warnings)}]")}";
        }

        return text;
    }

    /// <summary>The renderer triple <c>GrepTool</c> spreads into its definition. TS <c>grepRenderers</c>.</summary>
    public static readonly ToolRenderers Value = new()
    {
        RenderCall = (args, theme, context) =>
        {
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(FormatGrepCall(args, theme));
            return text;
        },
        RenderResult = (result, options, theme, context) =>
        {
            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(FormatGrepResult(result, options, theme, context.ShowImages));
            return text;
        },
    };
}
