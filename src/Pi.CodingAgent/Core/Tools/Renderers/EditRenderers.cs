// ============================================================================
// Presentation for the edit tool.
// Port of core/tools/renderers/edit.ts
// ============================================================================

using System.Text.Json.Nodes;
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
/// The edit call's component: a <see cref="Box"/> carrying the preview state. TS
/// <c>EditCallRenderComponent</c> (<c>Box &amp; {preview, previewArgsKey, previewPending, settledError}</c>).
/// </summary>
public sealed class EditCallRenderComponent : Box
{
    public EditCallRenderComponent()
        : base(1, 1, text => text)
    {
    }

    /// <summary>The preview diff, or the error that stopped it. TS <c>preview</c>.</summary>
    public EditDiffOutcome? Preview { get; set; }

    /// <summary>The argument key the current preview was requested for. TS <c>previewArgsKey</c>.</summary>
    public string? PreviewArgsKey { get; set; }

    /// <summary>Whether a preview computation is in flight. TS <c>previewPending</c>.</summary>
    public bool PreviewPending { get; set; }

    /// <summary>Whether the finished result was an error. TS <c>settledError</c>.</summary>
    public bool SettledError { get; set; }
}

/// <summary>The per-row state the edit renderers keep. TS <c>EditRenderState</c>.</summary>
public sealed class EditRenderState
{
    /// <summary>The call component, remembered so the result render can find it again. TS <c>callComponent</c>.</summary>
    public EditCallRenderComponent? CallComponent { get; set; }
}

/// <summary>
/// The arguments a preview can be computed from. TS <c>getRenderablePreviewInput</c>'s return value.
/// </summary>
/// <remarks>
/// <paramref name="RawEdits"/> is the original <c>edits</c> argument when that is where the edits came
/// from. The TS caches previews under <c>JSON.stringify({path, edits})</c> over the <em>original</em>
/// argument, so any extra fields a model sent survive into the cache key; when the edits were built from
/// the <c>oldText</c>/<c>newText</c> shorthand there is no original node and the key is built from the
/// pair.
/// </remarks>
internal sealed record EditPreviewInput(string Path, IReadOnlyList<Edit> Edits, JsonNode? RawEdits);

/// <summary>
/// Port of <c>core/tools/renderers/edit.ts</c>. The renderers live apart from the implementation so a
/// process that only displays tool output does not load the execution path or its parameter schema;
/// <c>EditTool</c> spreads these into its definition, so the tool's public shape is unchanged.
/// </summary>
public static class EditRenderers
{
    /// <summary>
    /// The preview input a set of arguments describes, or null when the arguments are not yet complete
    /// enough to compute one. TS <c>getRenderablePreviewInput</c>.
    /// </summary>
    internal static EditPreviewInput? GetRenderablePreviewInput(object? args)
    {
        if (args is null)
        {
            return null;
        }

        var path = RenderUtils.AsString(RenderUtils.Arg(args, "path").Value)
            ?? RenderUtils.AsString(RenderUtils.Arg(args, "file_path").Value);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var rawEdits = RenderUtils.Arg(args, "edits").Value;
        if (RenderUtils.AsArray(rawEdits) is { Count: > 0 } edits && edits.All(IsEdit))
        {
            // `edits.All(IsEdit)` above is the TS `every(...)` guard; it proves both fields are strings.
            return new EditPreviewInput(
                path,
                edits.Select(edit => new Edit(OldText(edit)!, NewText(edit)!)).ToList(),
                RenderUtils.ToNode(rawEdits));
        }

        var oldText = RenderUtils.AsString(RenderUtils.Arg(args, "oldText").Value);
        var newText = RenderUtils.AsString(RenderUtils.Arg(args, "newText").Value);
        if (oldText is not null && newText is not null)
        {
            return new EditPreviewInput(path, [new Edit(oldText, newText)], null);
        }

        return null;

        static bool IsEdit(object? edit) => OldText(edit) is not null && NewText(edit) is not null;

        static string? OldText(object? edit) => RenderUtils.AsString(RenderUtils.Arg(edit, "oldText").Value);

        static string? NewText(object? edit) => RenderUtils.AsString(RenderUtils.Arg(edit, "newText").Value);
    }

    /// <summary>
    /// The arguments key a preview is cached under, or null when there is no preview input. TS
    /// <c>JSON.stringify({ path, edits })</c>.
    /// </summary>
    internal static string? PreviewArgsKey(EditPreviewInput? input)
    {
        if (input is null)
        {
            return null;
        }

        var edits = input.RawEdits ?? new JsonArray(
            input.Edits
                .Select(edit => (JsonNode?)new JsonObject
                {
                    ["oldText"] = edit.OldText,
                    ["newText"] = edit.NewText,
                })
                .ToArray());
        return RenderUtils.JsonStringify(
            new JsonObject
            {
                ["path"] = input.Path,
                ["edits"] = edits,
            },
            indented: false);
    }

    /// <summary>The tool call header. TS <c>formatEditCall</c>.</summary>
    internal static string FormatEditCall(object? args, Theme theme, string cwd)
    {
        var pathDisplay = RenderUtils.RenderToolPath(
            RenderUtils.Str(RenderUtils.Coalesce(RenderUtils.Arg(args, "file_path"), RenderUtils.Arg(args, "path")).Value),
            theme,
            cwd);
        return $"{theme.Fg(ThemeColors.ToolTitle, theme.Bold("edit"))} {pathDisplay}";
    }

    /// <summary>
    /// The result body: the error text, or the diff the result carries when it differs from the preview
    /// already shown. TS <c>formatEditResult</c>.
    /// </summary>
    internal static string? FormatEditResult(
        object? args,
        EditDiffOutcome? preview,
        AgentToolResult result,
        Theme theme,
        bool isError)
    {
        var rawPath = RenderUtils.Str(
            RenderUtils.Coalesce(RenderUtils.Arg(args, "file_path"), RenderUtils.Arg(args, "path")).Value);
        var previewDiff = preview is { Error: null } ? preview.Diff : null;
        var previewError = preview?.Error;

        if (isError)
        {
            var errorText = string.Join(
                "\n",
                result.Content.OfType<Pi.Ai.Types.TextContent>().Select(block => block.Text ?? ""));
            if (errorText.Length == 0 || errorText == previewError)
            {
                return null;
            }

            return theme.Fg(ThemeColors.Error, errorText);
        }

        var resultDiff = (result.Details as EditToolDetails)?.Diff;
        if (!string.IsNullOrEmpty(resultDiff) && resultDiff != previewDiff)
        {
            return DiffRenderer.RenderDiff(
                resultDiff,
                new RenderDiffOptions { FilePath = rawPath ?? null });
        }

        return null;
    }

    /// <summary>The header background for the current preview state. TS <c>getEditHeaderBg</c>.</summary>
    internal static Func<string, string> GetEditHeaderBg(
        EditDiffOutcome? preview,
        bool settledError,
        Theme theme)
    {
        if (preview is not null)
        {
            return preview.Error is not null
                ? text => theme.Bg(ThemeBackgrounds.ToolErrorBg, text)
                : text => theme.Bg(ThemeBackgrounds.ToolSuccessBg, text);
        }

        if (settledError)
        {
            return text => theme.Bg(ThemeBackgrounds.ToolErrorBg, text);
        }

        return text => theme.Bg(ThemeBackgrounds.ToolPendingBg, text);
    }

    /// <summary>Rebuild the call component from the current preview. TS <c>buildEditCallComponent</c>.</summary>
    internal static EditCallRenderComponent BuildEditCallComponent(
        EditCallRenderComponent component,
        object? args,
        Theme theme,
        string cwd,
        int outputPad)
    {
        component.SetBgFn(GetEditHeaderBg(component.Preview, component.SettledError, theme));
        component.SetPaddingX(outputPad);
        component.Clear();
        component.AddChild(new Text(FormatEditCall(args, theme, cwd), 0, 0));

        if (component.Preview is null)
        {
            return component;
        }

        var body = component.Preview.Error is not null
            ? theme.Fg(ThemeColors.Error, component.Preview.Error)
            : DiffRenderer.RenderDiff(component.Preview.Diff ?? "");
        component.AddChild(new Spacer(1));
        component.AddChild(new Text(body, 0, 0));
        return component;
    }

    /// <summary>
    /// Install a new preview and report whether it differs from the one already shown. TS
    /// <c>setEditPreview</c>.
    /// </summary>
    internal static bool SetEditPreview(
        EditCallRenderComponent component,
        EditDiffOutcome preview,
        string? argsKey)
    {
        var current = component.Preview;
        var currentIsError = current?.Error is not null;
        var previewIsError = preview.Error is not null;
        var changed =
            current is null ||
            (currentIsError && previewIsError
                ? current.Error != preview.Error
                : currentIsError != previewIsError) ||
            (!currentIsError && !previewIsError &&
                (current!.Diff != preview.Diff || current.FirstChangedLine != preview.FirstChangedLine));
        component.Preview = preview;
        component.PreviewArgsKey = argsKey;
        component.PreviewPending = false;
        return changed;
    }

    /// <summary>
    /// The call component for this render, reusing the last one when it is already ours. TS
    /// <c>getEditCallRenderComponent</c>.
    /// </summary>
    /// <remarks>
    /// The TS test is <c>lastComponent instanceof Box</c>; the port tests for the concrete
    /// <see cref="EditCallRenderComponent"/> because a plain <see cref="Box"/> cannot carry the preview
    /// fields in C#, and only this renderer ever produces one.
    /// </remarks>
    internal static EditCallRenderComponent GetEditCallRenderComponent(EditRenderState state, IComponent? lastComponent)
    {
        if (lastComponent is EditCallRenderComponent component)
        {
            state.CallComponent = component;
            return component;
        }

        if (state.CallComponent is not null)
        {
            return state.CallComponent;
        }

        var created = new EditCallRenderComponent();
        state.CallComponent = created;
        return created;
    }

    /// <summary>
    /// Compute the preview in the background and install it if the arguments have not changed since.
    /// TS <c>void computeEditsDiff(...).then(...)</c>.
    /// </summary>
    private static async Task ComputePreviewAsync(
        EditCallRenderComponent component,
        EditPreviewInput input,
        ToolRenderContext context,
        string? requestKey)
    {
        var preview = await EditDiff.ComputeEditsDiffAsync(input.Path, input.Edits, context.Cwd);
        if (component.PreviewArgsKey == requestKey)
        {
            SetEditPreview(component, preview, requestKey);
            context.Invalidate();
        }
    }

    /// <summary>The renderer triple <c>EditTool</c> spreads into its definition. TS <c>editRenderers</c>.</summary>
    public static readonly ToolRenderers Value = new()
    {
        RenderCall = (args, theme, context) =>
        {
            var state = StateOf(context);
            var component = GetEditCallRenderComponent(state, context.LastComponent);
            var previewInput = GetRenderablePreviewInput(args);
            var argsKey = PreviewArgsKey(previewInput);

            if (component.PreviewArgsKey != argsKey)
            {
                component.Preview = null;
                component.PreviewArgsKey = argsKey;
                component.PreviewPending = false;
                component.SettledError = false;
            }

            if (context.ArgsComplete && previewInput is { } input && component.Preview is null && !component.PreviewPending)
            {
                component.PreviewPending = true;
                _ = ComputePreviewAsync(component, input, context, argsKey);
            }

            return BuildEditCallComponent(component, args, theme, context.Cwd, context.OutputPad);
        },
        RenderResult = (result, _, theme, context) =>
        {
            var callComponent = StateOf(context).CallComponent;
            var previewInput = GetRenderablePreviewInput(context.Args);
            var argsKey = PreviewArgsKey(previewInput);
            var resultDiff = !context.IsError ? (result.Details as EditToolDetails)?.Diff : null;
            var changed = false;
            if (callComponent is not null)
            {
                if (resultDiff is not null)
                {
                    changed = SetEditPreview(
                        callComponent,
                        EditDiffOutcome.FromResult(new EditDiffResult
                        {
                            Diff = resultDiff,
                            FirstChangedLine = (result.Details as EditToolDetails)?.FirstChangedLine,
                        }),
                        argsKey) || changed;
                }

                if (callComponent.SettledError != context.IsError)
                {
                    callComponent.SettledError = context.IsError;
                    changed = true;
                }

                if (changed)
                {
                    BuildEditCallComponent(callComponent, context.Args, theme, context.Cwd, context.OutputPad);
                }
            }

            var output = FormatEditResult(context.Args, callComponent?.Preview, result, theme, context.IsError);
            var component = context.LastComponent as Container ?? new Container();
            component.Clear();
            if (output is null)
            {
                return component;
            }

            component.AddChild(new Spacer(1));
            component.AddChild(new Text(output, context.OutputPad, 0));
            return component;
        },
    };

    /// <summary>
    /// The row state, which <c>ToolExecutionComponent</c> supplies. The TS would fail with a property
    /// access on <c>undefined</c> here; the port says so explicitly.
    /// </summary>
    private static EditRenderState StateOf(ToolRenderContext context) =>
        context.State as EditRenderState
        ?? throw new InvalidOperationException(
            "The edit renderers require an EditRenderState in ToolRenderContext.State; " +
            "ToolExecutionComponent supplies it.");
}
