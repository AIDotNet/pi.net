// ============================================================================
// Skill invocation message (collapsed/expanded).
// Port of modes/interactive/components/skill-invocation-message.ts
// ============================================================================

using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Component that renders a skill invocation message with collapsed/expanded state.
/// Uses same background color as custom messages for visual consistency.
/// Only renders the skill block itself - user message is rendered separately.
/// Port of <c>SkillInvocationMessageComponent</c>.
/// </summary>
public sealed class SkillInvocationMessageComponent : Box
{
    private bool _expanded;
    private readonly Core.ParsedSkillBlock _skillBlock;
    private readonly MarkdownTheme _markdownTheme;

    public SkillInvocationMessageComponent(
        Core.ParsedSkillBlock skillBlock,
        MarkdownTheme? markdownTheme = null,
        int outputPad = 1)
        : base(outputPad, 1, t => ThemeRuntime.Current.Bg("customMessageBg", t))
    {
        _skillBlock = skillBlock;
        _markdownTheme = markdownTheme ?? ThemeHelpers.GetMarkdownTheme();
        UpdateDisplay();
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        UpdateDisplay();
    }

    /// <summary>Update the horizontal padding. TS <c>setOutputPad</c>.</summary>
    public void SetOutputPad(int outputPad) => SetPaddingX(outputPad);

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        Clear();
        var content = new Container();

        if (_expanded)
        {
            // Expanded: label + skill name header + full content.
            var label = ThemeRuntime.Current.Fg("customMessageLabel", "\x1b[1m[skill]\x1b[22m");
            content.AddChild(new Text(label, 0, 0));
            var header = $"**{_skillBlock.Name}**\n\n";
            content.AddChild(new Markdown(
                header + _skillBlock.Content,
                0,
                0,
                _markdownTheme,
                new DefaultTextStyle
                {
                    Color = text => ThemeRuntime.Current.Fg("customMessageText", text),
                }));
        }
        else
        {
            // Collapsed: single line - [skill] name (hint to expand).
            var line =
                ThemeRuntime.Current.Fg("customMessageLabel", "\x1b[1m[skill]\x1b[22m ") +
                ThemeRuntime.Current.Fg("customMessageText", _skillBlock.Name) +
                ThemeRuntime.Current.Fg("dim", $" ({KeybindingHints.KeyText("app.tools.expand")} to expand)");
            content.AddChild(new Text(line, 0, 0));
        }

        AddChild(new MouseRegion(content, @event =>
        {
            if (@event.Type != TuiMouseEventType.Click || @event.Button != TuiMouseButton.Left)
            {
                return null;
            }

            SetExpanded(!_expanded);
            return new TuiMouseEventResult { Handled = true };
        }));
    }
}
