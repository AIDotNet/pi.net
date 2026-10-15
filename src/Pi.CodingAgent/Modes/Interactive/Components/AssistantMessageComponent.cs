// ============================================================================
// Assistant message rendering (text + thinking + stop-reason notices).
// Port of modes/interactive/components/assistant-message.ts
// ============================================================================

using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Component that renders a complete assistant message. Port of
/// <c>AssistantMessageComponent</c>.
/// </summary>
/// <remarks>
/// Final-zone semantics (OSC 133) are preserved: when the message has no tool calls, the first
/// line is prefixed with the zone start and the last line with zone end + final. The
/// <see cref="Theme"/> singleton maps to <see cref="ThemeRuntime.Current"/> (TS
/// <c>theme</c> from <c>theme.ts</c>).
/// </remarks>
public sealed class AssistantMessageComponent : Container
{
    private const string Osc133ZoneStart = "\x1b]133;A\x07";
    private const string Osc133ZoneEnd = "\x1b]133;B\x07";
    private const string Osc133ZoneFinal = "\x1b]133;C\x07";

    private readonly Container _contentContainer;
    private readonly MarkdownTheme _markdownTheme;
    private readonly IReadOnlyList<MarkdownTransformer> _markdownTransformers;
    private bool _hideThinkingBlock;
    private string _hiddenThinkingLabel;
    private int _outputPad;
    private AssistantMessage? _lastMessage;
    private bool _hasToolCalls;
    private bool _isStreaming;

    /// <summary>Per-thinking-run visibility overrides, keyed by run index (0-based).</summary>
    private readonly Dictionary<int, bool> _thinkingVisibilityOverrides = new();

    public AssistantMessageComponent(
        AssistantMessage? message = null,
        bool hideThinkingBlock = false,
        MarkdownTheme? markdownTheme = null,
        string hiddenThinkingLabel = "Thinking...",
        int outputPad = 1,
        IReadOnlyList<MarkdownTransformer>? markdownTransformers = null)
    {
        _hideThinkingBlock = hideThinkingBlock;
        _markdownTheme = markdownTheme ?? ThemeHelpers.GetMarkdownTheme();
        _hiddenThinkingLabel = hiddenThinkingLabel;
        _outputPad = outputPad;
        _markdownTransformers = markdownTransformers ?? [];

        _contentContainer = new Container();
        AddChild(_contentContainer);

        if (message is not null)
        {
            UpdateContent(message);
        }
    }

    public override void Invalidate()
    {
        base.Invalidate();
        if (_lastMessage is not null)
        {
            UpdateContent(_lastMessage);
        }
    }

    public void SetHideThinkingBlock(bool hide)
    {
        _hideThinkingBlock = hide;
        _thinkingVisibilityOverrides.Clear();
        if (_lastMessage is not null)
        {
            UpdateContent(_lastMessage);
        }
    }

    public void SetHiddenThinkingLabel(string label)
    {
        _hiddenThinkingLabel = label;
        if (_lastMessage is not null)
        {
            UpdateContent(_lastMessage);
        }
    }

    public void SetOutputPad(int padding)
    {
        _outputPad = padding;
        if (_lastMessage is not null)
        {
            UpdateContent(_lastMessage);
        }
    }

    public override string[] Render(int width)
    {
        var lines = base.Render(width);
        if (_hasToolCalls || lines.Length == 0)
        {
            return lines;
        }

        lines[0] = Osc133ZoneStart + lines[0];
        lines[^1] = Osc133ZoneEnd + Osc133ZoneFinal + lines[^1];
        return lines;
    }

    public void UpdateContent(AssistantMessage message, bool isStreaming)
    {
        _lastMessage = message;
        _isStreaming = isStreaming;
        UpdateContentCore(message, isStreaming);
    }

    /// <summary>TS <c>updateContent(message, isStreaming = this.isStreaming)</c>.</summary>
    public void UpdateContent(AssistantMessage message) => UpdateContent(message, _isStreaming);

    private void UpdateContentCore(AssistantMessage message, bool isStreaming)
    {
        _contentContainer.Clear();

        var hasVisibleContent = message.Content.Any(IsVisibleContent);
        if (hasVisibleContent)
        {
            _contentContainer.AddChild(new Spacer(1));
        }

        // Render content in order.
        var thinkingRunIndex = 0;
        for (var i = 0; i < message.Content.Count; i++)
        {
            var content = message.Content[i];
            if (content is TextContent { Text: var text } && text.Trim().Length > 0)
            {
                // Assistant text messages with no background - trim the text.
                // Set paddingY=0 to avoid extra spacing before tool executions.
                _contentContainer.AddChild(new Markdown(
                    text.Trim(),
                    _outputPad,
                    0,
                    _markdownTheme,
                    defaultTextStyle: null,
                    new MarkdownOptions
                    {
                        Transform = MarkdownTransform.Create("assistant", isStreaming, _markdownTransformers),
                    }));
            }
            else if (content is ThinkingContent)
            {
                var thinkingBlocks = new List<string>();
                for (; i < message.Content.Count; i++)
                {
                    if (message.Content[i] is not ThinkingContent thinkingContent)
                    {
                        break;
                    }

                    var thinking = thinkingContent.Thinking.Trim();
                    if (thinking.Length > 0)
                    {
                        thinkingBlocks.Add(thinking);
                    }
                }

                i--;

                if (thinkingBlocks.Count == 0)
                {
                    continue;
                }

                // Add spacing only when another visible assistant content block follows.
                // This avoids a superfluous blank line before separately-rendered tool execution blocks.
                var hasVisibleContentAfter = message.Content
                    .Skip(i + 1)
                    .Any(IsVisibleContent);

                var runIndex = thinkingRunIndex++;
                var hidden = _thinkingVisibilityOverrides.TryGetValue(runIndex, out var overrideHidden)
                    ? overrideHidden
                    : _hideThinkingBlock;
                IComponent thinkingComponent = hidden
                    ? new Text(
                        ThemeRuntime.Current.Italic(ThemeRuntime.Current.Fg("thinkingText", _hiddenThinkingLabel)),
                        _outputPad,
                        0)
                    : new Markdown(
                        string.Join("\n\n", thinkingBlocks),
                        _outputPad,
                        0,
                        _markdownTheme,
                        new DefaultTextStyle
                        {
                            Color = t => ThemeRuntime.Current.Fg("thinkingText", t),
                            Italic = true,
                        },
                        new MarkdownOptions
                        {
                            Transform = MarkdownTransform.Create("assistant-thinking", isStreaming, _markdownTransformers),
                        });
                _contentContainer.AddChild(new MouseRegion(thinkingComponent, @event =>
                {
                    if (@event.Type != TuiMouseEventType.Click || @event.Button != TuiMouseButton.Left)
                    {
                        return null;
                    }

                    _thinkingVisibilityOverrides[runIndex] = !hidden;
                    if (_lastMessage is not null)
                    {
                        UpdateContent(_lastMessage);
                    }

                    return new TuiMouseEventResult { Handled = true };
                }));
                if (hasVisibleContentAfter)
                {
                    _contentContainer.AddChild(new Spacer(1));
                }
            }
        }

        // Check if incomplete/failed - show after partial content.
        // For aborted/error tool calls, tool execution components show the error.
        // Length stops can happen before a tool call is complete, so surface them here too.
        var hasToolCalls = message.Content.Any(c => c is ToolCallContent);
        _hasToolCalls = hasToolCalls;
        if (message.StopReason == StopReason.Length)
        {
            _contentContainer.AddChild(new Spacer(1));
            _contentContainer.AddChild(new Text(
                ThemeRuntime.Current.Fg("error", "Response was truncated before completion."),
                _outputPad,
                0));
        }
        else if (!hasToolCalls)
        {
            if (message.StopReason == StopReason.Aborted)
            {
                var abortMessage = message.ErrorMessage is { } error && error != "Request was aborted"
                    ? error
                    : "Operation aborted";
                _contentContainer.AddChild(new Spacer(1));
                _contentContainer.AddChild(new Text(
                    ThemeRuntime.Current.Fg("error", abortMessage),
                    _outputPad,
                    0));
            }
            else if (message.StopReason == StopReason.Error)
            {
                var errorMsg = message.ErrorMessage ?? "Unknown error";
                _contentContainer.AddChild(new Spacer(1));
                _contentContainer.AddChild(new Text(
                    ThemeRuntime.Current.Fg("error", $"Error: {errorMsg}"),
                    _outputPad,
                    0));
            }
        }
    }

    private static bool IsVisibleContent(ContentBlock block) => block switch
    {
        TextContent text => text.Text.Trim().Length > 0,
        ThinkingContent thinking => thinking.Thinking.Trim().Length > 0,
        _ => false,
    };
}
