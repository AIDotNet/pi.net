using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using Pi.Tui.Components;

using CoreKeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;
using JsString = Pi.Tui.JsString;
using TuiText = Pi.Tui.Components.Text;

namespace Pi.CodingAgent.Extensions.Mcp;

/// <summary>The TUI implementation of the MCP manager contract from <c>extensions/mcp/ui.ts</c>.</summary>
public sealed class McpManagerView : IComponent, IFocusable, IMcpUi
{
    private const int MaxVisibleItems = 12;

    private readonly ITui _tui;
    private readonly Theme _theme;
    private readonly CoreKeybindingsManager _keybindings;
    private Container _content;
    private Action<string>? _inputHandler;
    private IFocusable? _inputTarget;
    private bool _focused;

    public McpManagerView(ITui tui, Theme theme, CoreKeybindingsManager keybindings)
    {
        _tui = tui;
        _theme = theme;
        _keybindings = keybindings;
        _content = Frame(theme, "MCP servers", [new TuiText(theme.Fg("muted", "Loading…"), 1, 1)]);
    }

    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            if (_inputTarget is not null)
            {
                _inputTarget.Focused = value;
            }
        }
    }

    public Task<string?> MenuAsync(Func<McpMenu> build, Func<Action, Action>? subscribe = null)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? unsubscribe = null;
        string? selected = null;
        var settled = false;

        void Finish(string? value)
        {
            if (settled)
            {
                return;
            }

            settled = true;
            unsubscribe?.Invoke();
            completion.TrySetResult(value);
        }

        void RenderMenu()
        {
            var menu = build();
            var wanted = selected ?? menu.Selected;
            var body = new List<IComponent>();
            if (!string.IsNullOrEmpty(menu.Details))
            {
                body.Add(new TuiText(_theme.Fg("muted", menu.Details), 1, 0));
            }
            if (!string.IsNullOrEmpty(menu.Error))
            {
                body.Add(new TuiText(_theme.Fg("error", menu.Error), 1, 0));
            }
            body.Add(new Spacer(1));

            var footer = $"{KeybindingHints.KeyHint("tui.select.confirm", menu.ConfirmLabel)} • " +
                KeybindingHints.KeyHint("tui.select.cancel", menu.CancelLabel);

            if (menu.Items.Count == 0)
            {
                body.Add(new TuiText(_theme.Fg("muted", menu.Empty ?? "Nothing to show."), 1, 0));
                SetContent(
                    Frame(_theme, menu.Title, body, KeybindingHints.KeyHint("tui.select.cancel", menu.CancelLabel)),
                    data =>
                    {
                        if (_keybindings.Matches(data, "tui.select.cancel"))
                        {
                            Finish(null);
                        }
                    });
                return;
            }

            var list = new SelectList(
                menu.Items,
                Math.Min(menu.Items.Count, MaxVisibleItems),
                ThemeHelpers.GetSelectListTheme());
            var index = menu.Items.ToList().FindIndex(item => item.Value == wanted);
            if (index >= 0)
            {
                list.SetSelectedIndex(index);
            }

            selected = list.GetSelectedItem()?.Value;
            list.OnSelectionChange = item => selected = item.Value;
            list.OnSelect = item => Finish(item.Value);
            list.OnCancel = () => Finish(null);
            body.Add(list);
            SetContent(Frame(_theme, menu.Title, body, footer), data => list.HandleInput(data));
        }

        RenderMenu();
        unsubscribe = subscribe?.Invoke(RenderMenu);
        return completion.Task;
    }

    public void Status(string title, string message, Action? onCancel = null)
    {
        var body = new IComponent[] { new Spacer(1), new Text(_theme.Fg("muted", message), 1, 0) };
        if (onCancel is null)
        {
            SetContent(Frame(_theme, title, body));
            return;
        }

        SetContent(Frame(_theme, title, body, KeybindingHints.KeyHint("tui.select.cancel", "cancel")), data =>
        {
            if (_keybindings.Matches(data, "tui.select.cancel"))
            {
                onCancel();
            }
        });
    }

    public Task<string?> RedirectUrlAsync(string title, string authorizationUrl, CancellationToken signal)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = signal.Register(() => completion.TrySetResult(null));
        if (!completion.Task.IsCompleted)
        {
            var input = new Input();
            var link = new AuthUrlComponent(_tui, authorizationUrl);
            var body = new IComponent[]
            {
                new Spacer(1),
                new Text(_theme.Fg("muted", "Approve access in your browser. If it did not open, visit:"), 1, 0),
                link,
                new Spacer(1),
                new Text(
                    _theme.Fg("muted", "If the browser runs on another machine, paste the URL it was redirected to:"),
                    1,
                    0),
                input,
            };

            SetContent(
                Frame(
                    _theme,
                    title,
                    body,
                    $"{KeybindingHints.KeyHint("tui.select.confirm", "submit")} • " +
                    KeybindingHints.KeyHint("tui.select.cancel", "cancel")),
                data =>
                {
                    if (_keybindings.Matches(data, "tui.select.confirm"))
                    {
                                var value = JsString.Trim(input.GetValue());
                        if (value.Length > 0)
                        {
                            completion.TrySetResult(value);
                        }
                        return;
                    }

                    if (_keybindings.Matches(data, "tui.select.cancel"))
                    {
                        completion.TrySetResult(null);
                        return;
                    }

                    if (_keybindings.Matches(data, "app.message.copy"))
                    {
                        _ = link.CopyAsync();
                        return;
                    }

                    input.HandleInput(data);
                },
                input);
        }

        return AwaitAndDisposeAsync(completion.Task, registration);

        static async Task<string?> AwaitAndDisposeAsync(
            Task<string?> task,
            CancellationTokenRegistration cancellationRegistration)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                cancellationRegistration.Dispose();
            }
        }
    }

    public void HandleInput(string data)
    {
        _inputHandler?.Invoke(data);
        _tui.RequestRender();
    }

    public string[] Render(int width) =>
        _content.Render(width)
            .Select(line => UnicodeWidth.VisibleWidth(line) > width ? TextLayout.TruncateToWidth(line, width, "") : line)
            .ToArray();

    public void Invalidate() => _content.Invalidate();

    private void SetContent(Container content, Action<string>? inputHandler = null, IFocusable? inputTarget = null)
    {
        if (_inputTarget is not null)
        {
            _inputTarget.Focused = false;
        }

        _content = content;
        _inputHandler = inputHandler;
        _inputTarget = inputTarget;
        if (_inputTarget is not null)
        {
            _inputTarget.Focused = _focused;
        }

        _tui.RequestRender();
    }

    private static Container Frame(Theme theme, string title, IEnumerable<IComponent> body, string? footer = null)
    {
        var container = new Container();
        container.AddChild(new DynamicBorder(text => theme.Fg("accent", text)));
        container.AddChild(new TuiText(theme.Fg("accent", theme.Bold(title)), 1, 0));
        foreach (var child in body)
        {
            container.AddChild(child);
        }

        if (!string.IsNullOrEmpty(footer))
        {
            container.AddChild(new Spacer(1));
            container.AddChild(new TuiText(theme.Fg("dim", footer), 1, 0));
        }

        container.AddChild(new DynamicBorder(text => theme.Fg("accent", text)));
        return container;
    }

    /// <summary>Runs the MCP manager inside the extension's focused custom TUI slot.</summary>
    public static async Task ShowMcpManagerAsync(
        IExtensionCommandContext context,
        Func<IMcpUi, Task> manage)
    {
        _ = await context.Ui.Custom<bool>((tui, theme, keybindings, done) =>
        {
            var view = new McpManagerView(tui, theme, keybindings);
            _ = RunAsync();
            return view;

            async Task RunAsync()
            {
                await Task.Yield();
                try
                {
                    await manage(view).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    context.Ui.Notify(error.Message, NotifyType.Error);
                }
                finally
                {
                    done(false);
                }
            }
        }).ConfigureAwait(false);
    }
}
