using System.Text.RegularExpressions;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>ToolHtmlRendererDeps</c>。</summary>
public sealed record ToolHtmlRendererDeps
{
    /// <summary>Renderers of calls to a tool, as resolved by extensions and the registered tool.</summary>
    public required Func<string, ToolRenderers?> GetToolRenderers { get; init; }

    /// <summary>Theme for styling.</summary>
    public required Theme Theme { get; init; }

    /// <summary>Working directory for render context.</summary>
    public required string Cwd { get; init; }

    /// <summary>Terminal width for rendering (default: 100).</summary>
    public int Width { get; init; } = 100;
}

/// <summary>
/// The custom-tool HTML renderer: it looks up tool definitions and invokes their
/// <c>renderCall</c>/<c>renderResult</c>, converting the resulting TUI component output to HTML.
/// Port of <c>core/export-html/tool-renderer.ts</c>.
/// </summary>
public static partial class ToolHtmlRendererFactory
{
    [GeneratedRegex(@"\x1b\[[\d;]*m")]
    private static partial Regex AnsiEscapeRegex();

    public static IToolHtmlRenderer Create(ToolHtmlRendererDeps deps) => new Renderer(deps);

    internal static bool IsBlankRenderedLine(string line)
        => AnsiEscapeRegex().Replace(line, "").Trim().Length == 0;

    internal static IReadOnlyList<string> TrimRenderedResultLines(IReadOnlyList<string> lines)
    {
        var start = 0;
        var end = lines.Count;
        while (start < end && IsBlankRenderedLine(lines[start]))
        {
            start++;
        }

        while (end > start && IsBlankRenderedLine(lines[end - 1]))
        {
            end--;
        }

        return [.. lines.Skip(start).Take(end - start)];
    }

    private sealed class Renderer(ToolHtmlRendererDeps deps) : IToolHtmlRenderer
    {
        // One component and one state object per tool call, so a renderer that caches across renders
        // (the write tool's highlight cache, the edit tool's preview) behaves in an exported page the
        // same way it does in the transcript.
        private readonly Dictionary<string, IComponent> _renderedCallComponents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IComponent> _renderedResultComponents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _renderedStates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, object?> _renderedArgs = new(StringComparer.Ordinal);

        public string? RenderCall(string toolCallId, string toolName, object? args)
        {
            try
            {
                _renderedArgs[toolCallId] = args;
                var tool = deps.GetToolRenderers(toolName);
                if (tool?.RenderCall is null)
                {
                    return null;
                }

                var component = tool.RenderCall(
                    args,
                    deps.Theme,
                    CreateRenderContext(
                        toolCallId,
                        LastComponent(_renderedCallComponents, toolCallId),
                        expanded: false,
                        isPartial: true,
                        isError: false));
                _renderedCallComponents[toolCallId] = component;
                return AnsiToHtml.LinesToHtml(component.Render(deps.Width));
            }
            catch
            {
                // On error, return null so HTML export can fall back to structured result rendering.
                return null;
            }
        }

        public RenderedToolHtmlParts? RenderResult(
            string toolCallId,
            string toolName,
            object? result,
            object? details,
            bool isError)
        {
            try
            {
                var tool = deps.GetToolRenderers(toolName);
                if (tool?.RenderResult is null)
                {
                    return null;
                }

                var agentToolResult = new AgentToolResult(ToContent(result), details, IsError: isError);

                var collapsedComponent = tool.RenderResult(
                    agentToolResult,
                    new ToolRenderResultOptions(Expanded: false, IsPartial: false),
                    deps.Theme,
                    CreateRenderContext(
                        toolCallId,
                        LastComponent(_renderedResultComponents, toolCallId),
                        expanded: false,
                        isPartial: false,
                        isError));
                _renderedResultComponents[toolCallId] = collapsedComponent;
                var collapsed = AnsiToHtml.LinesToHtml(
                    TrimRenderedResultLines(collapsedComponent.Render(deps.Width)));

                var expandedComponent = tool.RenderResult(
                    agentToolResult,
                    new ToolRenderResultOptions(Expanded: true, IsPartial: false),
                    deps.Theme,
                    CreateRenderContext(
                        toolCallId,
                        LastComponent(_renderedResultComponents, toolCallId),
                        expanded: true,
                        isPartial: false,
                        isError));
                _renderedResultComponents[toolCallId] = expandedComponent;
                var expanded = AnsiToHtml.LinesToHtml(
                    TrimRenderedResultLines(expandedComponent.Render(deps.Width)));

                return new RenderedToolHtmlParts
                {
                    Collapsed = collapsed.Length > 0 && collapsed != expanded ? collapsed : null,
                    Expanded = expanded,
                };
            }
            catch
            {
                // On error, return null so HTML export can fall back to structured result rendering.
                return null;
            }
        }

        private ToolRenderContext CreateRenderContext(
            string toolCallId,
            IComponent? lastComponent,
            bool expanded,
            bool isPartial,
            bool isError) => new()
        {
            Args = ArgsOf(toolCallId),
            ToolCallId = toolCallId,
            Invalidate = () => { },
            LastComponent = lastComponent,
            State = GetState(toolCallId),
            Cwd = deps.Cwd,
            ExecutionStarted = true,
            ArgsComplete = true,
            IsPartial = isPartial,
            Expanded = expanded,
            ShowImages = false,
            IsError = isError,
            DurationMs = null,
            OutputPad = 1,
        };

        private static IComponent? LastComponent(
            IReadOnlyDictionary<string, IComponent> components,
            string toolCallId) =>
            components.TryGetValue(toolCallId, out var component) ? component : null;

        private object GetState(string toolCallId)
        {
            if (!_renderedStates.TryGetValue(toolCallId, out var state))
            {
                state = new Dictionary<string, object?>(StringComparer.Ordinal);
                _renderedStates[toolCallId] = state;
            }

            return state;
        }

        /// <summary>
        /// The renderers read <see cref="ToolRenderContext.Args"/> as a member lookup, so the raw argument
        /// object is normalized to the dictionary shape the runtime hands tools.
        /// </summary>
        private IReadOnlyDictionary<string, object?> ArgsOf(string toolCallId)
        {
            if (!_renderedArgs.TryGetValue(toolCallId, out var args) || args is null)
            {
                return new Dictionary<string, object?>();
            }

            return args as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>();
        }

        /// <summary>
        /// Rebuild the content blocks from the generic objects session storage holds. The TS casts its
        /// array of <c>{type, text?, data?, mimeType?}</c> records to <c>ContentBlock[]</c>; the port has to
        /// reconstruct the discriminated union instead.
        /// </summary>
        private static List<ContentBlock> ToContent(object? result)
        {
            var blocks = new List<ContentBlock>();
            if (result is not System.Collections.IEnumerable enumerable || result is string)
            {
                return blocks;
            }

            foreach (var item in enumerable)
            {
                if (item is ContentBlock block)
                {
                    blocks.Add(block);
                    continue;
                }

                switch (RenderUtils.AsString(RenderUtils.Arg(item, "type").Value))
                {
                    case "text":
                        blocks.Add(new TextContent(RenderUtils.AsString(RenderUtils.Arg(item, "text").Value) ?? ""));
                        break;
                    case "image":
                        blocks.Add(new ImageContent(
                            RenderUtils.AsString(RenderUtils.Arg(item, "data").Value) ?? "",
                            RenderUtils.AsString(RenderUtils.Arg(item, "mimeType").Value)));
                        break;
                }
            }

            return blocks;
        }
    }
}
