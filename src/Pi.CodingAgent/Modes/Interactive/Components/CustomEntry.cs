using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive.Components;

/// <summary>
/// Component that renders a custom session entry from extensions
/// (TS <c>CustomEntryComponent</c>). The host owns transcript spacing; renderer
/// output should provide only its content.
/// </summary>
public sealed class CustomEntryComponent : Container
{
    private readonly CustomEntry _entry;
    private readonly EntryRenderer _renderer;
    private IComponent? _customComponent;
    private bool _expanded;
    private int _outputPad;

    public CustomEntryComponent(CustomEntry entry, EntryRenderer renderer, int outputPad = 1)
    {
        _entry = entry;
        _renderer = renderer;
        _outputPad = outputPad;
        Rebuild();
    }

    public bool HasContent() => _customComponent is not null;

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
        _outputPad = outputPad;
        Rebuild();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        Rebuild();
    }

    private void Rebuild()
    {
        Clear();
        _customComponent = null;

        IComponent? component;
        try
        {
            component = _renderer(_entry, new EntryRenderOptions { Expanded = _expanded }, ThemeRuntime.Current);
        }
        catch (Exception error)
        {
            var message = error.Message;
            var theme = ThemeRuntime.Current;
            var box = new Box(_outputPad, 1, text => theme.Bg(ThemeBackgrounds.CustomMessageBg, text));
            box.AddChild(new Text(
                theme.Fg(ThemeColors.Error, $"[{_entry.CustomType}] renderer failed: {message}"), 0, 0));
            component = box;
        }

        if (component is null)
        {
            return;
        }

        _customComponent = component;
        AddChild(new Spacer(1));
        AddChild(component);
    }
}
