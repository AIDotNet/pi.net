// ============================================================================
// Branch summary message (collapsed/expanded).
// Port of modes/interactive/components/branch-summary-message.ts
// ============================================================================

using Pi.CodingAgent.Core;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Component that renders a branch summary message with collapsed/expanded state.
/// Uses same background color as custom messages for visual consistency.
/// Port of <c>BranchSummaryMessageComponent</c>.
/// </summary>
public sealed class BranchSummaryMessageComponent : Box
{
    private bool _expanded;
    private readonly BranchSummaryMessage _message;
    private readonly MarkdownTheme _markdownTheme;

    public BranchSummaryMessageComponent(
        BranchSummaryMessage message,
        MarkdownTheme? markdownTheme = null,
        int outputPad = 1)
        : base(outputPad, 1, t => ThemeRuntime.Current.Bg("customMessageBg", t))
    {
        _message = message;
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

        var label = ThemeRuntime.Current.Fg("customMessageLabel", "\x1b[1m[branch]\x1b[22m");
        content.AddChild(new Text(label, 0, 0));
        content.AddChild(new Spacer(1));

        if (_expanded)
        {
            const string header = "**Branch Summary**\n\n";
            content.AddChild(new Markdown(
                header + _message.Summary,
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
            content.AddChild(new Text(
                ThemeRuntime.Current.Fg("customMessageText", "Branch summary (") +
                ThemeRuntime.Current.Fg("dim", KeybindingHints.KeyText("app.tools.expand")) +
                ThemeRuntime.Current.Fg("customMessageText", " to expand)"),
                0,
                0));
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
