// ============================================================================
// Presentation for the codemode tool.
// Port of extensions/codemode/renderer.ts
// ============================================================================
//
// The call shows the script; the result lists the nested tool calls with their status as they run
// and the cost of its model calls, followed by the script output without the "Script completed"
// header. Nested calls are not separate tool rows because they never reach the model as tool calls.

using System.Globalization;
using System.Text.RegularExpressions;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;
// `Pi.CodingAgent.Utils.Text` (the port of utils/text.ts) collides with the TUI's Text component;
// the alias keeps the component the unqualified name, as in the TS renderers.
using Text = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Extensions.Codemode;

/// <summary>
/// The codemode tool's call and result renderers. TS <c>codemodeRenderers</c>.
/// </summary>
public static partial class CodemodeRenderers
{
    private const int CodePreviewLines = 10;

    private const int CallPreviewCount = 8;

    private const int OutputPreviewLines = 5;

    private const int CollapsedArgsChars = 80;

    /// <summary>
    /// The script's own header, which the result renderer drops.
    /// </summary>
    /// <remarks>
    /// <c>\A</c>/<c>\z</c> and <c>[0-9]</c> rather than <c>^</c>/<c>$</c>/<c>\d</c>: in .NET <c>$</c>
    /// also matches before a trailing newline and <c>\d</c> matches any Unicode digit, while the JS
    /// pattern matches only the exact end of the string and only ASCII digits.
    /// </remarks>
    [GeneratedRegex(@"\AScript (completed|failed)\nWall time [0-9.]+ seconds\nOutput:\n\z", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptHeader();

    /// <summary>TS <c>codemodeRenderers.renderCall</c>.</summary>
    public static IComponent RenderCall(object? args, Theme theme, ToolRenderContext context)
    {
        RenderUtils.StartToolTimer(context);
        // The code includes the `// @options:` line, so options show as part of the script.
        var code = RenderUtils.Str(RenderUtils.Arg(args, "code").Value);
        var title = theme.Fg(ThemeColors.ToolTitle, theme.Bold("codemode"));
        var component = context.LastComponent as Container ?? new Container();
        component.Clear();
        if (code is null)
        {
            component.AddChild(new Text($"{title} {theme.Fg(ThemeColors.Error, "[invalid arg]")}", 0, 0));
            return component;
        }

        component.AddChild(new Text(title, 0, 0));
        if (code.Length > 0)
        {
            var highlighted = string.Join(
                "\n",
                ThemeHelpers.HighlightCode(Js.TrimEnd(RenderUtils.NormalizeDisplayText(code)), "javascript"));
            component.AddChild(context.Expanded
                ? new Text(highlighted, 0, 0)
                : new VisualLinePreview(new VisualLinePreviewOptions
                {
                    Text = highlighted,
                    MaxVisualLines = CodePreviewLines,
                    Keep = "start",
                    FormatHint = hidden => ExpandHint(theme, hidden, "lines"),
                }));
        }

        return component;
    }

    /// <summary>TS <c>codemodeRenderers.renderResult</c>.</summary>
    public static IComponent RenderResult(
        AgentToolResult result, ToolRenderResultOptions options, Theme theme, ToolRenderContext context)
    {
        var timerText = RenderUtils.ToolTimerText(context, options.IsPartial);
        var component = context.LastComponent as Container ?? new Container();
        component.Clear();
        var details = result.Details as CodemodeToolApi.CodemodeToolDetails;
        var calls = details?.Calls ?? [];
        if (calls.Count > 0)
        {
            var shown = options.Expanded
                ? calls
                : calls.Skip(Math.Max(0, calls.Count - CallPreviewCount)).ToList();
            var lines = shown.Select(call => FormatCall(call, theme, options.Expanded)).ToList();
            if (shown.Count < calls.Count)
            {
                lines.Insert(
                    0,
                    $"{theme.Fg(ThemeColors.Muted, $"... ({calls.Count - shown.Count} earlier calls,")} "
                    + $"{KeybindingHints.KeyHint(AppKeybindings.AppToolsExpand, "to expand")}"
                    + theme.Fg(ThemeColors.Muted, ")"));
            }

            // Collapsed rows hide earlier calls, so the total covers every call.
            var priced = calls.Where(call => call.Cost is not null).ToList();
            if (priced.Count > 1)
            {
                var total = priced.Sum(call => call.Cost ?? 0);
                lines.Add(theme.Fg(ThemeColors.Muted, $"Model calls: {FormatCost(total)}"));
            }

            component.AddChild(new Spacer(1));
            component.AddChild(new Text(string.Join("\n", lines), 0, 0));
        }

        // Drop the "Script completed\nWall time ...\nOutput:\n" header. Rejected input (invalid options)
        // has no header.
        var hasHeader = result.Content.FirstOrDefault() is TextContent first && ScriptHeader().IsMatch(first.Text);
        var output = options.IsPartial
            ? ""
            : RenderUtils.GetTextOutput(
                hasHeader ? result with { Content = result.Content.Skip(1).ToList() } : result,
                context.ShowImages).Trim();
        if (output.Length > 0)
        {
            var color = context.IsError ? ThemeColors.Error : ThemeColors.ToolOutput;
            var styled = string.Join(
                "\n",
                RenderUtils.ReplaceTabs(output)
                    .Split('\n')
                    .Select(line => theme.Fg(color, line)));
            component.AddChild(new Spacer(1));
            if (options.Expanded)
            {
                component.AddChild(new Text(styled, 0, 0));
            }
            else
            {
                // Limit wrapped lines, not logical ones: script output is often one long JSON line.
                component.AddChild(new VisualLinePreview(new VisualLinePreviewOptions
                {
                    Text = styled,
                    MaxVisualLines = OutputPreviewLines,
                    Keep = "start",
                    FormatHint = hidden => ExpandHint(theme, hidden, "lines"),
                }));

                // The collapsed preview hides the truncation notice at the end, so name the file here.
                if (details?.FullOutputPath is { Length: > 0 } fullOutputPath)
                {
                    component.AddChild(new Text(theme.Fg(ThemeColors.Muted, $"Full output: {fullOutputPath}"), 0, 0));
                }
            }
        }

        if (timerText is not null)
        {
            component.AddChild(new Spacer(1));
            component.AddChild(new Text(theme.Fg(ThemeColors.Muted, timerText), 0, 0));
        }

        return component;
    }

    /// <summary>TS <c>expandHint</c>.</summary>
    private static string ExpandHint(Theme theme, int hidden, string noun) =>
        $"{theme.Fg(ThemeColors.Muted, $"... ({hidden} more {noun},")} "
        + $"{KeybindingHints.KeyHint(AppKeybindings.AppToolsExpand, "to expand")}"
        + theme.Fg(ThemeColors.Muted, ")");

    /// <summary>TS <c>formatDuration</c>.</summary>
    private static string FormatDuration(double? milliseconds)
    {
        if (milliseconds is not { } ms)
        {
            return "";
        }

        // JS Math.round rounds halves toward +infinity; .NET rounds them to even.
        return ms < 1000
            ? $"{Math.Floor(ms + 0.5).ToString("F0", CultureInfo.InvariantCulture)}ms"
            : $"{Js.ToFixed(ms / 1000, 1)}s";
    }

    /// <summary>
    /// Cents for larger amounts, two significant digits for the fractions of a cent classifier calls
    /// cost. TS <c>formatCost</c>.
    /// </summary>
    private static string FormatCost(double cost) =>
        $"${(cost >= 0.01 ? Js.ToFixed(cost, 2) : Js.ToPrecision(cost, 2))}";

    /// <summary>TS <c>statusIcon</c>.</summary>
    private static string StatusIcon(CodemodeToolApi.CodemodeNestedCall call, Theme theme) =>
        call.Status switch
        {
            CodemodeToolApi.NestedCallStatus.Running => theme.Fg(ThemeColors.Warning, "…"),
            CodemodeToolApi.NestedCallStatus.Ok => theme.Fg(ThemeColors.Success, "✓"),
            CodemodeToolApi.NestedCallStatus.Error => theme.Fg(ThemeColors.Error, "✗"),
            CodemodeToolApi.NestedCallStatus.Cancelled => theme.Fg(ThemeColors.Muted, "⊘"),
            _ => "",
        };

    /// <summary>TS <c>formatCall</c>.</summary>
    private static string FormatCall(CodemodeToolApi.CodemodeNestedCall call, Theme theme, bool expanded)
    {
        var args = !expanded && call.Args.Length > CollapsedArgsChars
            ? $"{call.Args[..(CollapsedArgsChars - 3)]}..."
            : call.Args;
        var duration = FormatDuration(call.DurationMs);
        var line = $"{StatusIcon(call, theme)} {theme.Fg(ThemeColors.ToolTitle, call.Name)}";
        if (args.Length > 0)
        {
            line += $" {theme.Fg(ThemeColors.Muted, args)}";
        }

        if (duration.Length > 0)
        {
            line += $" {theme.Fg(ThemeColors.Dim, duration)}";
        }

        if (call.Cost is { } cost && cost != 0)
        {
            line += $" {theme.Fg(ThemeColors.Dim, FormatCost(cost))}";
        }

        if (expanded && !string.IsNullOrEmpty(call.Error))
        {
            line += $"\n    {theme.Fg(ThemeColors.Error, call.Error.Replace("\n", "\n    ", StringComparison.Ordinal))}";
        }

        return line;
    }
}
