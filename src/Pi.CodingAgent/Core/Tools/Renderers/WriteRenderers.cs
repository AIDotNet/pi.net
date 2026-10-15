// ============================================================================
// Presentation for the write tool.
// Port of core/tools/renderers/write.ts
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
/// The syntax-highlighting cache a write call accumulates while its <c>content</c> argument streams in.
/// TS <c>WriteHighlightCache</c>.
/// </summary>
internal sealed class WriteHighlightCache
{
    /// <summary>The path the cache was built for, or null when the argument was not a string.</summary>
    public required string? RawPath { get; init; }

    /// <summary>The resolved language; the cache only exists when one was found.</summary>
    public required string Lang { get; init; }

    /// <summary>The raw content the cache covers, extended as more arrives.</summary>
    public required string RawContent { get; set; }

    /// <summary>Content with CRs dropped and tabs expanded, one entry per line.</summary>
    public required List<string> NormalizedLines { get; init; }

    /// <summary>Highlighted lines, parallel to <see cref="NormalizedLines"/>.</summary>
    public required List<string> HighlightedLines { get; init; }
}

/// <summary>
/// The write call's component, carrying the highlight cache across renders. TS
/// <c>WriteCallRenderComponent</c> (a <c>Text</c> with a <c>cache</c> field).
/// </summary>
internal sealed class WriteCallRenderComponent : Text
{
    public WriteCallRenderComponent()
        : base("", 0, 0)
    {
    }

    public WriteHighlightCache? Cache { get; set; }
}

/// <summary>
/// Port of <c>core/tools/renderers/write.ts</c>. The renderers live apart from the implementation so a
/// process that only displays tool output does not load the execution path or its parameter schema;
/// <c>WriteTool</c> spreads these into its definition, so the tool's public shape is unchanged.
/// </summary>
public static class WriteRenderers
{
    /// <summary>Lines shown while collapsed. TS literal <c>10</c>.</summary>
    internal const int CollapsedResultLines = 10;

    /// <summary>
    /// How many leading lines get re-highlighted as a block while the content streams in. TS
    /// <c>WRITE_PARTIAL_FULL_HIGHLIGHT_LINES</c>.
    /// </summary>
    internal const int PartialFullHighlightLines = 50;

    /// <summary>Highlight one line on its own. TS <c>highlightSingleLine</c>.</summary>
    private static string HighlightSingleLine(string line, string lang) =>
        ThemeHelpers.HighlightCode(line, lang).FirstOrDefault() ?? "";

    /// <summary>
    /// Re-highlight the leading block, which needs the block's own context to colour correctly.
    /// TS <c>refreshWriteHighlightPrefix</c>.
    /// </summary>
    private static void RefreshWriteHighlightPrefix(WriteHighlightCache cache)
    {
        var prefixCount = Math.Min(PartialFullHighlightLines, cache.NormalizedLines.Count);
        if (prefixCount == 0)
        {
            return;
        }

        var prefixSource = string.Join("\n", cache.NormalizedLines.Take(prefixCount));
        var prefixHighlighted = ThemeHelpers.HighlightCode(prefixSource, cache.Lang);
        for (var i = 0; i < prefixCount; i++)
        {
            cache.HighlightedLines[i] = i < prefixHighlighted.Length
                ? prefixHighlighted[i]
                : HighlightSingleLine(cache.NormalizedLines[i], cache.Lang);
        }
    }

    /// <summary>Highlight the whole content. TS <c>rebuildWriteHighlightCacheFull</c>.</summary>
    private static WriteHighlightCache? RebuildWriteHighlightCacheFull(string? rawPath, string fileContent)
    {
        var lang = !string.IsNullOrEmpty(rawPath) ? ThemeHelpers.GetLanguageFromPath(rawPath) : null;
        if (lang is null)
        {
            return null;
        }

        var normalized = RenderUtils.ReplaceTabs(RenderUtils.NormalizeDisplayText(fileContent));
        return new WriteHighlightCache
        {
            RawPath = rawPath,
            Lang = lang,
            RawContent = fileContent,
            NormalizedLines = [.. normalized.Split('\n')],
            HighlightedLines = [.. ThemeHelpers.HighlightCode(normalized, lang)],
        };
    }

    /// <summary>
    /// Extend the cache with whatever the content argument has grown by, falling back to a full rebuild
    /// whenever the prefix no longer matches. TS <c>updateWriteHighlightCacheIncremental</c>.
    /// </summary>
    private static WriteHighlightCache? UpdateWriteHighlightCacheIncremental(
        WriteHighlightCache? cache,
        string? rawPath,
        string fileContent)
    {
        var lang = !string.IsNullOrEmpty(rawPath) ? ThemeHelpers.GetLanguageFromPath(rawPath) : null;
        if (lang is null)
        {
            return null;
        }

        if (cache is null || cache.Lang != lang || cache.RawPath != rawPath)
        {
            return RebuildWriteHighlightCacheFull(rawPath, fileContent);
        }

        if (!fileContent.StartsWith(cache.RawContent, StringComparison.Ordinal))
        {
            return RebuildWriteHighlightCacheFull(rawPath, fileContent);
        }

        if (fileContent.Length == cache.RawContent.Length)
        {
            return cache;
        }

        var deltaNormalized = RenderUtils.ReplaceTabs(
            RenderUtils.NormalizeDisplayText(fileContent[cache.RawContent.Length..]));
        cache.RawContent = fileContent;
        if (cache.NormalizedLines.Count == 0)
        {
            cache.NormalizedLines.Add("");
            cache.HighlightedLines.Add("");
        }

        var segments = deltaNormalized.Split('\n');
        var lastIndex = cache.NormalizedLines.Count - 1;
        cache.NormalizedLines[lastIndex] += segments[0];
        cache.HighlightedLines[lastIndex] = HighlightSingleLine(cache.NormalizedLines[lastIndex], cache.Lang);
        for (var i = 1; i < segments.Length; i++)
        {
            cache.NormalizedLines.Add(segments[i]);
            cache.HighlightedLines.Add(HighlightSingleLine(segments[i], cache.Lang));
        }

        RefreshWriteHighlightPrefix(cache);
        return cache;
    }

    /// <summary>The tool call header plus the content preview. TS <c>formatWriteCall</c>.</summary>
    internal static string FormatWriteCall(
        object? args,
        ToolRenderResultOptions options,
        Theme theme,
        WriteHighlightCache? cache,
        string cwd)
    {
        var rawPath = RenderUtils.Str(
            RenderUtils.Coalesce(RenderUtils.Arg(args, "file_path"), RenderUtils.Arg(args, "path")).Value);
        var fileContent = RenderUtils.Str(RenderUtils.Arg(args, "content").Value);
        var pathDisplay = RenderUtils.RenderToolPath(rawPath, theme, cwd);
        var text = $"{theme.Fg(ThemeColors.ToolTitle, theme.Bold("write"))} {pathDisplay}";

        if (fileContent is null)
        {
            text += $"\n\n{theme.Fg(ThemeColors.Error, "[invalid content arg - expected string]")}";
        }
        else if (fileContent.Length > 0)
        {
            var lang = !string.IsNullOrEmpty(rawPath) ? ThemeHelpers.GetLanguageFromPath(rawPath) : null;
            List<string> renderedLines;
            if (lang is not null)
            {
                renderedLines = cache?.HighlightedLines
                    ?? [.. ThemeHelpers.HighlightCode(
                        RenderUtils.ReplaceTabs(RenderUtils.NormalizeDisplayText(fileContent)), lang)];
            }
            else
            {
                renderedLines = [.. RenderUtils.NormalizeDisplayText(fileContent).Split('\n')];
            }

            var lines = ReadRenderers.TrimTrailingEmptyLines(renderedLines);
            var totalLines = lines.Count;
            var maxLines = options.Expanded ? lines.Count : CollapsedResultLines;
            var displayLines = lines.Take(maxLines);
            var remaining = lines.Count - maxLines;
            text += $"\n\n{string.Join(
                "\n",
                displayLines.Select(line => lang is not null
                    ? line
                    : theme.Fg(ThemeColors.ToolOutput, RenderUtils.ReplaceTabs(line))))}";
            if (remaining > 0)
            {
                text += $"{theme.Fg(
                    ThemeColors.Muted,
                    $"\n... ({remaining} more lines, {totalLines} total,")} " +
                    $"{KeybindingHints.KeyHint(AppKeybindings.AppToolsExpand, "to expand")}" +
                    $"{theme.Fg(ThemeColors.Muted, ")")}";
            }
        }

        return text;
    }

    /// <summary>
    /// The error text a failed write shows, or null when there is nothing to show. TS
    /// <c>formatWriteResult</c>.
    /// </summary>
    /// <remarks>
    /// Unlike the other renderers this reads the raw text blocks rather than
    /// <see cref="RenderUtils.GetTextOutput"/>: error text is shown verbatim, without ANSI stripping or
    /// carriage-return removal.
    /// </remarks>
    internal static string? FormatWriteResult(AgentToolResult result, bool isError, Theme theme)
    {
        if (!isError)
        {
            return null;
        }

        var output = string.Join(
            "\n",
            result.Content.OfType<Pi.Ai.Types.TextContent>().Select(block => block.Text ?? ""));
        return output.Length == 0 ? null : $"\n{theme.Fg(ThemeColors.Error, output)}";
    }

    /// <summary>The renderer triple <c>WriteTool</c> spreads into its definition. TS <c>writeRenderers</c>.</summary>
    public static readonly ToolRenderers Value = new()
    {
        RenderCall = (args, theme, context) =>
        {
            var rawPath = RenderUtils.Str(
                RenderUtils.Coalesce(RenderUtils.Arg(args, "file_path"), RenderUtils.Arg(args, "path")).Value);
            var fileContent = RenderUtils.Str(RenderUtils.Arg(args, "content").Value);
            var component = context.LastComponent as WriteCallRenderComponent ?? new WriteCallRenderComponent();
            component.Cache = fileContent is not null
                ? context.ArgsComplete
                    ? RebuildWriteHighlightCacheFull(rawPath, fileContent)
                    : UpdateWriteHighlightCacheIncremental(component.Cache, rawPath, fileContent)
                : null;
            component.SetText(FormatWriteCall(
                args,
                new ToolRenderResultOptions(context.Expanded, context.IsPartial),
                theme,
                component.Cache,
                context.Cwd));
            return component;
        },
        RenderResult = (result, _, theme, context) =>
        {
            var output = FormatWriteResult(result, context.IsError, theme);
            if (output is null)
            {
                var empty = context.LastComponent as Container ?? new Container();
                empty.Clear();
                return empty;
            }

            var text = context.LastComponent as Text ?? new Text("", 0, 0);
            text.SetText(output);
            return text;
        },
    };
}
