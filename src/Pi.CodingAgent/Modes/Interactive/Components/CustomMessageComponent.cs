// ============================================================================
// Custom (extension) message rendering.
// Port of modes/interactive/components/custom-message.ts
// ============================================================================

using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Component that renders a custom message entry from extensions. Uses distinct styling to
/// differentiate from user messages. Port of <c>CustomMessageComponent</c>.
/// </summary>
public sealed class CustomMessageComponent : Container
{
    private readonly CustomMessage _message;
    private readonly MessageRenderer? _customRenderer;
    private readonly Box _box;
    private IComponent? _customComponent;
    private readonly MarkdownTheme _markdownTheme;
    private bool _expanded;
    private int _outputPad;

    public CustomMessageComponent(
        CustomMessage message,
        MessageRenderer? customRenderer = null,
        MarkdownTheme? markdownTheme = null,
        int outputPad = 1)
    {
        _message = message;
        _customRenderer = customRenderer;
        _markdownTheme = markdownTheme ?? ThemeHelpers.GetMarkdownTheme();
        _outputPad = outputPad;

        AddChild(new Spacer(1));

        // Create box with purple background (used for default rendering).
        _box = new Box(1, 1, t => ThemeRuntime.Current.Bg("customMessageBg", t));

        Rebuild();
    }

    public void SetExpanded(bool expanded)
    {
        if (_expanded != expanded)
        {
            _expanded = expanded;
            Rebuild();
        }
    }

    public void SetOutputPad(int outputPad)
    {
        if (_outputPad != outputPad)
        {
            _outputPad = outputPad;
            Rebuild();
        }
    }

    public override void Invalidate()
    {
        base.Invalidate();
        Rebuild();
    }

    private void Rebuild()
    {
        // Remove previous content component.
        if (_customComponent is not null)
        {
            RemoveChild(_customComponent);
            _customComponent = null;
        }

        RemoveChild(_box);

        // Try custom renderer first - it handles its own styling.
        if (_customRenderer is not null)
        {
            try
            {
                var component = _customRenderer(
                    _message,
                    new MessageRenderOptions { Expanded = _expanded, OutputPad = _outputPad },
                    ThemeRuntime.Current);
                if (component is not null)
                {
                    // Custom renderer provides its own styled component.
                    _customComponent = component;
                    AddChild(component);
                    return;
                }
            }
            catch
            {
                // Fall through to default rendering.
            }
        }

        // Default rendering uses our box.
        AddChild(_box);
        _box.Clear();
        _box.SetPaddingX(_outputPad);

        // Default rendering: label + content.
        var label = ThemeRuntime.Current.Fg("customMessageLabel", $"\x1b[1m[{_message.CustomType}]\x1b[22m");
        _box.AddChild(new Text(label, 0, 0));
        _box.AddChild(new Spacer(1));

        // Extract text content.
        var text = _message.Content switch
        {
            MessageContent.Text textContent => textContent.Value,
            MessageContent.Blocks blocks => string.Join("\n", blocks.Value
                .OfType<TextContent>()
                .Select(c => c.Text)),
            _ => "",
        };

        _box.AddChild(new Markdown(
            text,
            0,
            0,
            _markdownTheme,
            new DefaultTextStyle
            {
                Color = t => ThemeRuntime.Current.Fg("customMessageText", t),
            }));
    }
}
