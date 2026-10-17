using System.Text.Encodings.Web;
using System.Text.Json;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using TuiText = Pi.Tui.Components.Text;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Utils;
using Pi.CodingAgent.Utils.Image;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>Options for <see cref="ToolExecutionComponent"/>. TS <c>ToolExecutionOptions</c>.</summary>
public sealed record ToolExecutionOptions
{
    public bool? ShowImages { get; init; }

    public int? ImageWidthCells { get; init; }

    public int? OutputPad { get; init; }
}

/// <summary>
/// Renders one tool call: the call renderer, the result renderer, fallbacks, and inline images
/// (TS <c>ToolExecutionComponent</c>).
/// </summary>
/// <remarks>
/// TS accepts <c>ToolRenderers | ToolDefinition</c> because the two are structurally compatible;
/// C# has no structural typing, so the constructor takes <see cref="ToolRenderers"/> and
/// <see cref="FromDefinition"/> adapts a full definition (the call sites in interactive-mode pass
/// definitions).
/// </remarks>
public sealed class ToolExecutionComponent : Container
{
    private const int FallbackPreviewLines = 10;

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Box _contentBox;
    private readonly TuiText _contentText;
    private readonly MouseRegion _contentTextRegion;
    private readonly Container _selfRenderContainer;
    private int _selfRenderHeight;
    private IComponent? _callRendererComponent;
    private IComponent? _resultRendererComponent;
    private readonly Dictionary<string, object?> _rendererState = [];
    private List<Image> _imageComponents = [];
    /// <summary>Inputs of <see cref="_imageComponents"/>, so UpdateDisplay can reuse images.</summary>
    private List<(string Data, string MimeType, int WidthCells)> _imageSources = [];
    private List<Spacer> _imageSpacers = [];
    private readonly string _toolName;
    private readonly string _toolCallId;
    private IReadOnlyDictionary<string, object?> _args;
    private bool _expanded;
    private bool _showImages;
    private int _imageWidthCells;
    private int _outputPad;
    private bool _isPartial = true;
    private readonly ToolRenderers? _toolDefinition;
    private readonly ITui _ui;
    private readonly string _cwd;
    private bool _executionStarted;
    private bool _argsComplete;
    private AgentToolResult? _result;
    private long? _resultDurationMs;
    private bool _hideComponent;

    public ToolExecutionComponent(
        string toolName,
        string toolCallId,
        IReadOnlyDictionary<string, object?> args,
        ToolExecutionOptions? options,
        ToolRenderers? toolDefinition,
        ITui ui,
        string cwd)
    {
        _toolName = toolName;
        _toolCallId = toolCallId;
        _args = args;
        _toolDefinition = toolDefinition;
        _showImages = options?.ShowImages ?? true;
        _imageWidthCells = options?.ImageWidthCells ?? 60;
        _outputPad = options?.OutputPad ?? 1;
        _ui = ui;
        _cwd = cwd;

        var theme = ThemeRuntime.Current;
        AddChild(new Spacer(1));

        // Always create all shell variants. contentBox is used for default renderer-based
        // composition, selfRenderContainer when the tool renders its own framing, and contentText
        // for generic fallback rendering when no tool definition exists.
        _contentBox = new Box(1, 1, text => theme.Bg(ThemeBackgrounds.ToolPendingBg, text));
        _contentText = new TuiText("", 1, 1, text => theme.Bg(ThemeBackgrounds.ToolPendingBg, text));
        _contentTextRegion = CreateResultRegion(_contentText);
        _selfRenderContainer = new Container();

        if (HasRendererDefinition())
        {
            AddChild(GetRenderShell() == "self" ? _selfRenderContainer : _contentBox);
        }
        else
        {
            AddChild(_contentTextRegion);
        }

        UpdateDisplay();
    }

    /// <summary>Adapts a full tool definition to its renderer triple (TS's structural union).</summary>
    public static ToolRenderers FromDefinition(ToolDefinition definition) => new()
    {
        RenderShell = definition.RenderShell,
        RenderCall = definition.RenderCall,
        RenderResult = definition.RenderResult,
    };

    private ToolRenderCall? GetCallRenderer() => _toolDefinition?.RenderCall;

    private ToolRenderResult? GetResultRenderer() => _toolDefinition?.RenderResult;

    private bool HasRendererDefinition() => _toolDefinition is not null;

    private string GetRenderShell() => _toolDefinition?.RenderShell ?? "default";

    private ToolRenderContext GetRenderContext(IComponent? lastComponent) => new()
    {
        Args = _args,
        ToolCallId = _toolCallId,
        Invalidate = () =>
        {
            Invalidate();
            _ui.RequestRender();
        },
        LastComponent = lastComponent,
        State = _rendererState,
        Cwd = _cwd,
        ExecutionStarted = _executionStarted,
        ArgsComplete = _argsComplete,
        IsPartial = _isPartial,
        Expanded = _expanded,
        ShowImages = _showImages,
        IsError = _result?.IsError ?? false,
        DurationMs = _isPartial ? null : _resultDurationMs,
        OutputPad = _outputPad,
    };

    private IComponent CreateCallFallback() =>
        new TuiText(RenderUtils.FormatToolCallWithArgs(_toolName, _args, ThemeRuntime.Current, _expanded), 0, 0);

    private IComponent? CreateResultFallback()
    {
        var output = GetTextOutput();
        if (output.Length == 0)
        {
            return null;
        }

        var theme = ThemeRuntime.Current;
        var lines = output.Split('\n');
        var displayLines = _expanded ? lines : lines[..Math.Min(FallbackPreviewLines, lines.Length)];
        var remaining = lines.Length - displayLines.Length;
        var text = string.Join("\n", displayLines.Select(line => theme.Fg(ThemeColors.ToolOutput, line)));
        if (remaining > 0)
        {
            text +=
                $"{theme.Fg(ThemeColors.Muted, $"\n... ({remaining} more lines,")} {KeybindingHints.KeyHint(AppKeybindings.AppToolsExpand, "to expand")}{theme.Fg(ThemeColors.Muted, ")")}";
        }

        return new TuiText(text, 0, 0);
    }

    private MouseRegion CreateResultRegion(IComponent component) => new(component, @event =>
    {
        if (_result is null || @event.Type != TuiMouseEventType.Click || @event.Button != TuiMouseButton.Left)
        {
            return null;
        }

        SetExpanded(!_expanded);
        return new TuiMouseEventResult { Handled = true };
    });

    public void UpdateArgs(IReadOnlyDictionary<string, object?> args)
    {
        _args = args;
        UpdateDisplay();
    }

    public void MarkExecutionStarted()
    {
        _executionStarted = true;
        UpdateDisplay();
        _ui.RequestRender();
    }

    public void SetArgsComplete()
    {
        _argsComplete = true;
        UpdateDisplay();
        _ui.RequestRender();
    }

    /// <summary>
    /// Replaces the result. TS carries <c>durationMs</c> on the result object itself; the port keeps
    /// it alongside (the renderer receives it through <see cref="ToolRenderContext.DurationMs"/>).
    /// </summary>
    public void UpdateResult(AgentToolResult result, bool isPartial = false, long? durationMs = null)
    {
        _result = result;
        _resultDurationMs = durationMs;
        _isPartial = isPartial;
        UpdateDisplay();
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        UpdateDisplay();
    }

    public void SetOutputPad(int outputPad)
    {
        _outputPad = outputPad;
        UpdateDisplay();
    }

    public void SetShowImages(bool show)
    {
        _showImages = show;
        UpdateDisplay();
    }

    public void SetImageWidthCells(int width)
    {
        _imageWidthCells = Math.Max(1, (int)Math.Floor((double)width));
        UpdateDisplay();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    public override string[] Render(int width)
    {
        if (_hideComponent)
        {
            return [];
        }

        if (HasRendererDefinition() && GetRenderShell() == "self")
        {
            var contentLines = _selfRenderContainer.Render(width);
            _selfRenderHeight = contentLines.Length;
            if (contentLines.Length == 0 && _imageComponents.Count == 0)
            {
                return [];
            }

            var lines = new List<string>();
            if (contentLines.Length > 0)
            {
                lines.Add("");
                lines.AddRange(contentLines);
            }

            for (var i = 0; i < _imageComponents.Count; i++)
            {
                if (i < _imageSpacers.Count && _imageSpacers[i] is { } spacer)
                {
                    lines.AddRange(spacer.Render(width));
                }

                lines.AddRange(_imageComponents[i].Render(width));
            }

            return [.. lines];
        }

        return base.Render(width);
    }

    public override TuiMouseEventResult? HandleMouse(TuiMouseEvent @event)
    {
        if (!HasRendererDefinition() || GetRenderShell() != "self")
        {
            return base.HandleMouse(@event);
        }

        if (@event.Y <= 0 || @event.Y > _selfRenderHeight)
        {
            return null;
        }

        var forwarded = @event.Clone();
        forwarded.Y = @event.Y - 1;
        forwarded.Height = _selfRenderHeight;
        return _selfRenderContainer.HandleMouse(forwarded);
    }

    private void UpdateDisplay()
    {
        var theme = ThemeRuntime.Current;
        Func<string, string> bgFn = _isPartial
            ? text => theme.Bg(ThemeBackgrounds.ToolPendingBg, text)
            : _result?.IsError == true
                ? text => theme.Bg(ThemeBackgrounds.ToolErrorBg, text)
                : text => theme.Bg(ThemeBackgrounds.ToolSuccessBg, text);

        var hasContent = false;
        _hideComponent = false;
        if (HasRendererDefinition())
        {
            // TS: `const renderContainer = shell === "self" ? selfRenderContainer : contentBox`,
            // where Box extends Container so both share clear()/addChild(). C#'s Box is a sibling of
            // Container (not a subclass), so the two are unified through the local helpers below.
            var shellIsSelf = GetRenderShell() == "self";
            var renderBox = shellIsSelf ? null : _contentBox;
            var renderContainer = shellIsSelf ? _selfRenderContainer : null;
            if (renderBox is not null)
            {
                renderBox.SetBgFn(bgFn);
                renderBox.SetPaddingX(_outputPad);
            }

            void ClearRender()
            {
                if (renderBox is not null)
                {
                    renderBox.Clear();
                }
                else
                {
                    renderContainer!.Clear();
                }
            }

            void AddToRender(IComponent child)
            {
                if (renderBox is not null)
                {
                    renderBox.AddChild(child);
                }
                else
                {
                    renderContainer!.AddChild(child);
                }
            }

            ClearRender();

            var callRenderer = GetCallRenderer();
            if (callRenderer is null)
            {
                AddToRender(CreateResultRegion(CreateCallFallback()));
                hasContent = true;
            }
            else
            {
                try
                {
                    var component = callRenderer(_args, theme, GetRenderContext(_callRendererComponent));
                    _callRendererComponent = component;
                    AddToRender(CreateResultRegion(component));
                    hasContent = true;
                }
                catch
                {
                    _callRendererComponent = null;
                    AddToRender(CreateResultRegion(CreateCallFallback()));
                    hasContent = true;
                }
            }

            if (_result is not null)
            {
                var resultRenderer = GetResultRenderer();
                if (resultRenderer is null)
                {
                    var component = CreateResultFallback();
                    if (component is not null)
                    {
                        AddToRender(CreateResultRegion(component));
                        hasContent = true;
                    }
                }
                else
                {
                    try
                    {
                        var component = resultRenderer(
                            _result,
                            new ToolRenderResultOptions(_expanded, _isPartial),
                            theme,
                            GetRenderContext(_resultRendererComponent));
                        _resultRendererComponent = component;
                        AddToRender(CreateResultRegion(component));
                        hasContent = true;
                    }
                    catch
                    {
                        _resultRendererComponent = null;
                        var component = CreateResultFallback();
                        if (component is not null)
                        {
                            AddToRender(CreateResultRegion(component));
                            hasContent = true;
                        }
                    }
                }
            }
        }
        else
        {
            _contentText.SetCustomBgFn(bgFn);
            _contentText.SetPaddingX(_outputPad);
            _contentText.SetText(FormatToolExecution());
            hasContent = true;
        }

        var previousImages = _imageComponents;
        var previousSources = _imageSources;
        foreach (var img in _imageComponents)
        {
            RemoveChild(img);
        }

        _imageComponents = [];
        _imageSources = [];
        foreach (var spacer in _imageSpacers)
        {
            RemoveChild(spacer);
        }

        _imageSpacers = [];

        if (_result is not null)
        {
            var imageBlocks = _result.Content.OfType<ImageContent>().ToList();
            var capabilities = TerminalImage.GetCapabilities();
            foreach (var img in imageBlocks)
            {
                if (capabilities.Images != ImageProtocol.None && _showImages && img.Data.Length > 0 && img.MimeType is not null)
                {
                    var spacer = new Spacer(1);
                    AddChild(spacer);
                    _imageSpacers.Add(spacer);
                    var source = (Data: img.Data, MimeType: img.MimeType, WidthCells: _imageWidthCells);
                    var index = _imageComponents.Count;
                    var previous = index < previousSources.Count ? previousSources[index] : default;
                    var reuse = index < previousSources.Count &&
                        previous.Data == source.Data &&
                        previous.MimeType == source.MimeType &&
                        previous.WidthCells == source.WidthCells;
                    var imageComponent = reuse && index < previousImages.Count
                        ? previousImages[index]
                        : new Image(
                            source.Data,
                            source.MimeType,
                            new ImageTheme { FallbackColor = s => theme.Fg(ThemeColors.ToolOutput, s) },
                            new ImageOptions { MaxWidthCells = source.WidthCells });
                    if (source.MimeType != "image/png")
                    {
                        ImageConvert.EnsurePngTranscoder(() =>
                        {
                            Invalidate();
                            _ui.RequestRender();
                        });
                    }

                    _imageComponents.Add(imageComponent);
                    _imageSources.Add(source);
                    AddChild(imageComponent);
                }
            }
        }

        if (HasRendererDefinition() && !hasContent && _imageComponents.Count == 0)
        {
            _hideComponent = true;
        }
    }

    private string GetTextOutput() => RenderUtils.GetTextOutput(_result, _showImages);

    private string FormatToolExecution()
    {
        var theme = ThemeRuntime.Current;
        var text = theme.Fg(ThemeColors.ToolTitle, theme.Bold(_toolName));
        var content = JsonSerializer.Serialize(_args, IndentedJson);
        if (content.Length > 0)
        {
            text += $"\n\n{content}";
        }

        var output = GetTextOutput();
        if (output.Length > 0)
        {
            text += $"\n{output}";
        }

        return text;
    }
}
