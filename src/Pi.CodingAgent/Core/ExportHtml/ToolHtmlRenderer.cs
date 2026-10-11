using System.Text.RegularExpressions;
using Pi.Tui;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>ToolHtmlRendererDeps</c>。</summary>
public sealed record ToolHtmlRendererDeps
{
    public required Func<string, ToolHtmlRenderers?> GetToolRenderers { get; init; }

    public required object Theme { get; init; }

    public required string Cwd { get; init; }

    public int Width { get; init; } = 100;
}

/// <summary>
/// 4e-3b 用的轻量渲染器入口。4f 接入 <c>ToolDefinition.renderCall/renderResult</c> 后替换为完整签名。
/// </summary>
public sealed record ToolHtmlRenderers
{
    public Func<object?, object?, object?, IComponent>? RenderCall { get; init; }

    public Func<object?, object?, object?, object?, IComponent>? RenderResult { get; init; }
}

/// <summary>对应 TS <c>createToolHtmlRenderer</c>。</summary>
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
        public string? RenderCall(string toolCallId, string toolName, object? args)
        {
            try
            {
                var tool = deps.GetToolRenderers(toolName);
                if (tool?.RenderCall is null)
                {
                    return null;
                }

                var component = tool.RenderCall(args, deps.Theme, null);
                var lines = component.Render(deps.Width);
                return AnsiToHtml.LinesToHtml(lines);
            }
            catch
            {
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

                var collapsedComponent = tool.RenderResult(result, details, deps.Theme, false);
                var expandedComponent = tool.RenderResult(result, details, deps.Theme, true);
                var collapsed = AnsiToHtml.LinesToHtml(TrimRenderedResultLines(collapsedComponent.Render(deps.Width)));
                var expanded = AnsiToHtml.LinesToHtml(TrimRenderedResultLines(expandedComponent.Render(deps.Width)));
                return new RenderedToolHtmlParts
                {
                    Collapsed = collapsed != expanded ? collapsed : null,
                    Expanded = expanded,
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
