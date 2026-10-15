// ============================================================================
// Presentation for the shell tools (bash and powershell).
// Port of core/tools/renderers/bash.ts
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
/// The per-row state the shell renderers keep between renders. TS keeps it on the
/// <c>state</c> object <c>tool-execution.ts</c> initialises; the port declares it here so the renderers
/// can be driven on their own.
/// </summary>
public sealed class ShellRenderState
{
    /// <summary>When the execution started, in Unix milliseconds. TS <c>state.startedAt</c>.</summary>
    public long? StartedAt { get; set; }

    /// <summary>When the execution ended, in Unix milliseconds. TS <c>state.endedAt</c>.</summary>
    public long? EndedAt { get; set; }

    /// <summary>
    /// The one-second tick that keeps a running execution's elapsed time current. TS
    /// <c>state.interval</c>, a <c>setInterval</c> handle.
    /// </summary>
    public Timer? Interval { get; set; }
}

/// <summary>
/// Port of <c>core/tools/renderers/bash.ts</c>. The renderers live apart from the implementation so a
/// process that only displays tool output does not load the execution path or its parameter schema;
/// the shell tools spread these into their definitions, so their public shape is unchanged.
/// </summary>
/// <remarks>
/// Unlike the other renderers this one ignores the <c>theme</c> argument and reads the module-level
/// theme, because that is what the TS does — it imports the <c>theme</c> singleton from
/// <c>theme.ts</c> rather than taking one. The port reads <see cref="ThemeRuntime.Current"/>, which is
/// that singleton's counterpart.
/// </remarks>
public static class BashRenderers
{
    /// <summary>Visual lines of output shown while collapsed. TS <c>BASH_PREVIEW_LINES</c>.</summary>
    public const int BashPreviewLines = 5;

    /// <summary>How often the live elapsed-time tick fires, in milliseconds. TS <c>BASH_UPDATE_THROTTLE_MS</c>.</summary>
    public const int BashUpdateThrottleMs = 100;

    /// <summary>
    /// Test seam for the clock. TS calls <c>Date.now()</c> directly; the clock is not injectable in
    /// .NET, so tests and the differential corpus override it to make elapsed-time rendering
    /// deterministic.
    /// </summary>
    internal static Func<long>? NowOverride { get; set; }

    /// <summary>TS <c>Date.now()</c>.</summary>
    internal static long Now() => NowOverride?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Human-readable duration. TS <c>formatDuration</c>.</summary>
    internal static string FormatDuration(double milliseconds)
    {
        var seconds = milliseconds / 1000;
        if (seconds < 60)
        {
            // toFixed rounds half away from zero on the exact value; .NET's "F1" rounds half to even.
            return $"{Js.ToFixed(seconds, 1)}s";
        }

        var totalSeconds = Math.Floor(seconds);
        var minutes = Math.Floor(totalSeconds / 60);
        var remainder = totalSeconds % 60;
        if (minutes < 60)
        {
            return $"{Js.NumberToString(minutes)}m {Js.NumberToString(remainder)}s";
        }

        return $"{Js.NumberToString(Math.Floor(minutes / 60))}h " +
            $"{Js.NumberToString(minutes % 60)}m {Js.NumberToString(remainder)}s";
    }

    /// <summary>The tool call header, shared by bash and powershell. TS <c>formatShellCall</c>.</summary>
    internal static string FormatShellCall(object? args, string prompt)
    {
        var theme = ThemeRuntime.Current;
        var command = RenderUtils.Str(RenderUtils.Arg(args, "command").Value);
        var timeout = RenderUtils.Arg(args, "timeout").Value;
        var timeoutSuffix = Js.IsTruthy(timeout)
            ? theme.Fg(ThemeColors.Muted, $" (timeout {RenderUtils.JsToString(timeout)}s)")
            : "";
        var commandDisplay = command is null
            ? RenderUtils.InvalidArgText(theme)
            : command.Length > 0
                ? command
                : theme.Fg(ThemeColors.ToolOutput, "...");
        return theme.Fg(ThemeColors.ToolTitle, theme.Bold($"{prompt} {commandDisplay}")) + timeoutSuffix;
    }

    /// <summary>
    /// Rebuild a shell result row from scratch: output preview, truncation notices, elapsed time.
    /// TS <c>rebuildBashResultRenderComponent</c>.
    /// </summary>
    internal static void RebuildBashResultRenderComponent(
        Container component,
        AgentToolResult result,
        ToolRenderResultOptions options,
        bool showImages,
        long? startedAt,
        long? endedAt)
    {
        var theme = ThemeRuntime.Current;
        component.Clear();

        var output = Js.Trim(RenderUtils.GetTextOutput(result, showImages));
        var details = result.Details as BashToolDetails;
        var truncation = details?.Truncation;
        var fullOutputPath = details?.FullOutputPath;
        if (!options.IsPartial &&
            truncation is { Truncated: true } &&
            !string.IsNullOrEmpty(fullOutputPath) &&
            output.EndsWith(']'))
        {
            // The truncation footer duplicates the path the warning line below already carries.
            var footerStart = output.LastIndexOf("\n\n[", StringComparison.Ordinal);
            if (footerStart != -1 && output[footerStart..].Contains(fullOutputPath, StringComparison.Ordinal))
            {
                output = Js.TrimEnd(output[..footerStart]);
            }
        }

        if (output.Length > 0)
        {
            var styledOutput = string.Join(
                "\n",
                output.Split('\n').Select(line => theme.Fg(ThemeColors.ToolOutput, line)));

            if (options.Expanded)
            {
                component.AddChild(new Text($"\n{styledOutput}", 0, 0));
            }
            else
            {
                component.AddChild(new Spacer(1));
                component.AddChild(new VisualLinePreview(new VisualLinePreviewOptions
                {
                    Text = styledOutput,
                    MaxVisualLines = BashPreviewLines,
                    Keep = "end",
                    FormatHint = hidden =>
                        theme.Fg(ThemeColors.Muted, $"... ({hidden} earlier lines,") +
                        $" {KeybindingHints.KeyHint(AppKeybindings.AppToolsExpand, "to expand")}" +
                        theme.Fg(ThemeColors.Muted, ")"),
                }));
            }
        }

        if (truncation is { Truncated: true } || !string.IsNullOrEmpty(fullOutputPath))
        {
            var warnings = new List<string>();
            if (!string.IsNullOrEmpty(fullOutputPath))
            {
                warnings.Add($"Full output: {fullOutputPath}");
            }

            if (truncation is { Truncated: true })
            {
                if (truncation.TruncatedBy == TruncatedBy.Lines)
                {
                    warnings.Add(
                        $"Truncated: showing {truncation.OutputLines} of {truncation.TotalLines} lines");
                }
                else
                {
                    warnings.Add(
                        $"Truncated: {truncation.OutputLines} lines shown " +
                        $"({Truncate.FormatSize(truncation.MaxBytes)} limit)");
                }
            }

            component.AddChild(new Text(
                $"\n{theme.Fg(ThemeColors.Warning, $"[{string.Join(". ", warnings)}]")}",
                0,
                0));
        }

        // A final result's recorded duration wins: it is monotonic and survives reloads. The renderer's
        // own clock is the fallback for live progress and for results stored without one.
        if (startedAt is not null)
        {
            var label = options.IsPartial ? "Elapsed" : "Took";
            var endTime = endedAt ?? Now();
            component.AddChild(new Text(
                $"\n{theme.Fg(ThemeColors.Muted, $"{label} {FormatDuration(endTime - startedAt.Value)}")}",
                0,
                0));
        }
    }

    /// <summary>
    /// The renderer triple for one shell tool. Shell renderers are shared by bash and powershell, which
    /// differ only in the prompt they display. TS <c>createShellRenderers</c>.
    /// </summary>
    public static ToolRenderers CreateShellRenderers(string prompt) => new()
    {
        RenderCall = (args, _, context) =>
        {
            var state = StateOf(context);
            if (context.ExecutionStarted && state.StartedAt is null)
            {
                state.StartedAt = Now();
                state.EndedAt = null;
            }

            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(FormatShellCall(args, prompt));
            return text;
        },
        RenderResult = (result, options, _, context) =>
        {
            var state = StateOf(context);
            if (state.StartedAt is not null && options.IsPartial && state.Interval is null)
            {
                state.Interval = new Timer(_ => context.Invalidate(), null, 1000, 1000);
            }

            if (!options.IsPartial || context.IsError)
            {
                state.EndedAt ??= Now();
                if (state.Interval is not null)
                {
                    state.Interval.Dispose();
                    state.Interval = null;
                }
            }

            var component = context.LastComponent as Container ?? new Container();
            RebuildBashResultRenderComponent(
                component,
                result,
                options,
                context.ShowImages,
                state.StartedAt,
                state.EndedAt);
            component.Invalidate();
            return component;
        },
    };

    /// <summary>
    /// The row state, which <c>ToolExecutionComponent</c> supplies. The TS would fail with a property
    /// access on <c>undefined</c> here; the port says so explicitly.
    /// </summary>
    private static ShellRenderState StateOf(ToolRenderContext context) =>
        context.State as ShellRenderState
        ?? throw new InvalidOperationException(
            "The shell renderers require a ShellRenderState in ToolRenderContext.State; " +
            "ToolExecutionComponent supplies it.");
}
