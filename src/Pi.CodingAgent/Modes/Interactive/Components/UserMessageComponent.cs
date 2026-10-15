// ============================================================================
// User message rendering.
// Port of modes/interactive/components/user-message.ts
// ============================================================================

using Pi.CodingAgent.Core.Extensions;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Component that renders a user message. Port of <c>UserMessageComponent</c>.
/// </summary>
/// <remarks>
/// The Markdown pads and colors its own background: a Box around it would keep a second
/// full-width copy of every line, with identical output (TS comment preserved). Final-zone
/// semantics (OSC 133) wrap the rendered lines.
/// </remarks>
public sealed class UserMessageComponent : Container
{
    private const string Osc133ZoneStart = "\x1b]133;A\x07";
    private const string Osc133ZoneEnd = "\x1b]133;B\x07";
    private const string Osc133ZoneFinal = "\x1b]133;C\x07";

    private readonly MarkdownTheme _markdownTheme;
    private readonly IReadOnlyList<MarkdownTransformer> _markdownTransformers;
    private string _text;
    private int _outputPad;

    public UserMessageComponent(
        string text,
        MarkdownTheme? markdownTheme = null,
        int outputPad = 1,
        IReadOnlyList<MarkdownTransformer>? markdownTransformers = null)
    {
        _text = text;
        _markdownTheme = markdownTheme ?? ThemeHelpers.GetMarkdownTheme();
        _outputPad = outputPad;
        _markdownTransformers = markdownTransformers ?? [];
        Rebuild();
    }

    public void SetOutputPad(int padding)
    {
        _outputPad = padding;
        Rebuild();
    }

    private void Rebuild()
    {
        Clear();
        AddChild(new Markdown(
            _text,
            _outputPad,
            1,
            _markdownTheme,
            new DefaultTextStyle
            {
                Color = content => ThemeRuntime.Current.Fg("userMessageText", content),
                BgColor = content => ThemeRuntime.Current.Bg("userMessageBg", content),
            },
            new MarkdownOptions
            {
                PreserveOrderedListMarkers = true,
                PreserveBackslashEscapes = true,
                Transform = MarkdownTransform.Create("user", false, _markdownTransformers),
            }));
    }

    public override string[] Render(int width)
    {
        var lines = base.Render(width);
        if (lines.Length == 0)
        {
            return lines;
        }

        lines[0] = Osc133ZoneStart + lines[0];
        lines[^1] = Osc133ZoneEnd + Osc133ZoneFinal + lines[^1];
        return lines;
    }
}
