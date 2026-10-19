// ============================================================================
// Service-only experimental TUI — port of experimental/client-tui.ts (4e-3c-3)
// ============================================================================
//
// The presentation half of the experimental client: it opens one chord facet host over the
// selected server's two service sources (the slash-command runtime, the presentation bridge,
// the built-in commands, the shared facets and the server-selected presentation plugins),
// attaches the chosen Session, and drives the replicated transcript through
// <see cref="ExperimentalChatView"/>.
//
// Port shape notes:
// - TS's `env.use(Service)` returns a Proxy that structurally satisfies the service interface;
//   the C# chord port has no Proxy (C374), so the host is given explicit typed views over the
//   remote facades — <see cref="RemoteServiceClients"/> — through
//   <see cref="FacetOptions.TypedServiceViews"/>. The six services the non-interactive client
//   activates (4e-3c-1) are adapted here, plus the worker-side <c>SessionPlugins</c> view the
//   built-in <c>/reload</c> resolves.
// - The service groups `prepareClientSession` opens are left to their source's lifetime: the
//   C# `IRemoteServices` surface has no dispose (C329), and the source disposes every binding
//   it handed out when it is disposed — the same pattern `ActivateBuiltinClientServicesAsync`
//   follows (TS disposes the groups in a finally block).
// - `process.cwd()` is `Directory.GetCurrentDirectory()`; `localeCompare` is
//   `StringComparer.CurrentCulture` (C79); `theme.fg/bold` is `ThemeRuntime.Current`.

using Pi.Agent.Types;
using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental.Plugins;
using Pi.CodingAgent.Experimental.Services;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Durable.Harness;
using Pi.Durable.Types;
using Pi.Tui;
using Pi.Tui.Components;
using Text = Pi.Tui.Components.Text;
// Pi.Tui declares its own KeybindingsManager; the coding-agent one (the app-level bindings)
// is what this module configures.
using KeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;
// Pi.CodingAgent.Core declares its own SlashCommands (the built-in command catalog); the
// experimental service handle is what the facet graph wires.
using ServicesSlashCommands = Pi.CodingAgent.Experimental.Services.SlashCommands;

namespace Pi.CodingAgent.Experimental;

/// <summary>Options of <see cref="ExperimentalClientTui.RunClientTuiAsync"/>. TS <c>RunClientTuiOptions</c>.</summary>
public sealed record RunClientTuiOptions : OpenClientRuntimeOptions
{
    /// <summary>Extra facets the caller contributes (tests inject one). TS <c>facetLoader</c>.</summary>
    public IFacetLoader? FacetLoader { get; init; }
}

/// <summary>One server as the TUI sees it. TS <c>ClientTuiServer</c>.</summary>
public sealed record ClientTuiServer(
    string ServerId,
    bool Radius,
    IServerServiceSource Server,
    ISessionServiceSource Session);

/// <summary>The attached session's three faces. TS <c>SessionFeature</c>.</summary>
public sealed record SessionFeature(
    string ServerId,
    ISessionServiceSource Session,
    ITranscript Transcript);

/// <summary>A session chosen before the TUI starts. TS <c>PreparedClientSession</c>.</summary>
public sealed record PreparedClientSession(
    ClientTuiServer Server,
    SessionSummary Summary,
    object? PresentationPlugins);

/// <summary>A selector waiting for the user's pick. TS <c>PendingSelection</c>.</summary>
public sealed class PendingSelection
{
    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string Title { get; init; }

    public required IReadOnlyList<SelectItem> Items { get; init; }

    public string? SelectedValue { get; init; }

    public Task<string?> WaitAsync() => _completion.Task;

    public void Resolve(string? value) => _completion.TrySetResult(value);
}

/// <summary>
/// Service-only presentation driven by a replicated main-lane snapshot.
/// Port of TS <c>ExperimentalClientTui</c>.
/// </summary>
public sealed class ExperimentalClientTui : IComponent
{
    private static readonly SelectListTheme SelectTheme = new()
    {
        SelectedPrefix = text => ThemeRuntime.Current.Fg("accent", text),
        SelectedText = text => ThemeRuntime.Current.Fg("accent", text),
        Description = text => ThemeRuntime.Current.Fg("muted", text),
        ScrollInfo = text => ThemeRuntime.Current.Fg("dim", text),
        NoMatch = text => ThemeRuntime.Current.Fg("warning", text),
    };

    private readonly ITui _ui;
    private readonly Action _requestRender;
    private readonly Action _finish;
    private readonly Container _documentContainer = new();
    private readonly Text _sessionHeading = new("", 1, 0);
    private readonly Container _pendingMessagesContainer = new();
    private readonly Container _statusContainer = new();
    private readonly Container _editorContainer = new();
    private readonly Text _footerComponent = new("", 1, 0);
    private readonly IComponent _layoutRoot;
    private readonly LoadedFacets _sharedFacets;
    private readonly KeybindingsManager _keybindings = KeybindingsManager.Create();
    private readonly CustomEditor _chatInput;
    private LoadedFacets? _presentationFacets;
    private FacetHost? _facetHost;
    private Task _facetReloadTail = Task.CompletedTask;
    private SessionFeature? _session;
    private ISlashCommands? _slashCommands;
    private IAgentController? _controller;
    private SelectList? _selectList;
    private PendingSelection? _selection;
    private string _screen = "chat";
    private string? _selectedServerId;
    private string? _sessionId;
    private string _status = "Starting Session…";
    private bool _busy;
    private bool _closed;
    private Task? _closePromise;
    private Task _recoveryTransition = Task.CompletedTask;
    private IDisposable? _laneUnsubscribe;
    private ExperimentalChatView? _chatView;

    private ExperimentalClientTui(
        ITui ui, Action requestRender, Action finish, LoadedFacets loadedFacets)
    {
        _ui = ui;
        _requestRender = requestRender;
        _finish = finish;
        _sharedFacets = loadedFacets;
        GlobalKeybindings.Set(_keybindings);
        _chatInput = new CustomEditor(ui, ThemeHelpers.GetEditorTheme(), _keybindings,
            new CustomEditorOptions { PaddingX = 1 });
        _chatInput.OnSubmit = message =>
        {
            // TS: `void this.#runPrompt(message)` — the editor cannot await the turn.
            _ = RunPromptAsync(message);
        };
        _chatInput.OnEscape = Interrupt;
        _chatInput.OnCtrlD = finish;
        _chatInput.OnAction("app.clear", finish);
        _chatInput.OnAction("app.model.select", () =>
        {
            _ = ExecuteSlashCommandAsync("model", "");
        });
        _chatInput.OnAction("app.message.followUp", () =>
        {
            var text = _chatInput.GetText().Trim();
            if (text.Length == 0) return;
            _chatInput.SetText("");
            _ = QueueFollowUpAsync(text);
        });
        _editorContainer.AddChild(_chatInput);
        _layoutRoot = ChatViewportFactory.CreateChatViewport(new ChatViewportOptions
        {
            Document = _documentContainer,
            PendingMessages = _pendingMessagesContainer,
            Status = _statusContainer,
            Editor = _editorContainer,
            Footer = _footerComponent,
            ScrollbarTrackStyle = text => ThemeRuntime.Current.Fg("scrollbarTrack", text),
            ScrollbarThumbStyle = text => ThemeRuntime.Current.Fg("scrollbarThumb", text),
        }).Root;
        Rebuild();
    }

    /// <summary>TS <c>ExperimentalClientTui.create</c>: prepare the session, then start the host.</summary>
    public static async Task<ExperimentalClientTui> CreateAsync(CreateExperimentalClientTuiOptions options)
    {
        var prepared = await ClientTuiSession.PrepareClientSessionAsync(options.Command, options.Servers)
            .ConfigureAwait(false);
        var loadedFacets = await Api.CombineFacetLoaders(
            options.FacetLoader is null ? [] : [options.FacetLoader]).LoadAsync().ConfigureAwait(false);
        var component = new ExperimentalClientTui(options.Ui, options.RequestRender, options.Finish, loadedFacets);
        try
        {
            await component.StartAsync(prepared).ConfigureAwait(false);
            await component.OpenPreparedSessionAsync(prepared).ConfigureAwait(false);
            return component;
        }
        catch (Exception error)
        {
            try
            {
                await component.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Experimental TUI startup and cleanup failed",
                    new List<Exception> { error, cleanupError });
            }

            throw;
        }
    }

    /// <summary>Options of <see cref="CreateAsync"/>. TS the inline parameter of <c>create</c>.</summary>
    public sealed record CreateExperimentalClientTuiOptions
    {
        public required ClientCommand Command { get; init; }

        public required ITui Ui { get; init; }

        public required IReadOnlyList<ClientTuiServer> Servers { get; init; }

        public IFacetLoader? FacetLoader { get; init; }

        public required Action RequestRender { get; init; }

        public required Action Finish { get; init; }
    }

    /// <summary>TS <c>layoutRoot</c>: the viewport root the renderer mounts.</summary>
    public IComponent LayoutRoot => _layoutRoot;

    public string[] Render(int width) =>
    [
        .. _documentContainer.Render(width),
        .. _pendingMessagesContainer.Render(width),
        .. _statusContainer.Render(width),
        .. _editorContainer.Render(width),
        .. _footerComponent.Render(width),
    ];

    public void HandleInput(string data)
    {
        if (_busy)
        {
            if (_keybindings.Matches(data, "app.clear")
                || (_chatInput.GetText().Length == 0 && _keybindings.Matches(data, "app.exit")))
            {
                _finish();
            }

            return;
        }

        if (_screen == "chat")
        {
            _chatInput.HandleInput(data);
            _requestRender();
            return;
        }

        _selectList?.HandleInput(data);
    }

    public void Invalidate() => _layoutRoot.Invalidate();

    public void Dispose()
    {
        // TS: `void this.close().catch(() => {})` — fire and forget.
        _ = CloseAsync().ContinueWith(_ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, System.Threading.Tasks.TaskScheduler.Default);
    }

    /// <summary>TS <c>refreshTheme</c>: rebuild the chat view, then the layout.</summary>
    public void RefreshTheme()
    {
        var view = ConversationView();
        if (view is not null) _chatView?.RefreshTheme(view);
        Rebuild();
    }

    /// <summary>TS <c>showError</c>: the theme controller's report channel.</summary>
    public void ShowError(string error)
    {
        _status = $"Error: {error}";
        Rebuild();
    }

    /// <summary>TS the inline <c>showStatus</c> of the presentation bridge.</summary>
    private void ShowStatus(string status)
    {
        _status = status;
        Rebuild();
    }

    /// <summary>TS <c>close()</c>: idempotent; the first call's task is shared.</summary>
    public Task CloseAsync()
    {
        _closePromise ??= CloseCoreAsync();
        return _closePromise;
    }

    // ---------------------------------------------------------------- startup

    private async Task StartAsync(PreparedClientSession prepared)
    {
        var server = prepared.Server;
        var presentationFacets = await Api.CombineFacetLoaders(
            PluginBundles.CreatePresentationFacetLoaders(prepared.PresentationPlugins)).LoadAsync()
            .ConfigureAwait(false);
        _presentationFacets = presentationFacets;
        FacetHost? facetHost = null;
        Task ReloadPresentationPluginsAsync(object? data)
        {
            var operation = ReloadTailAsync();
            _facetReloadTail = IgnoreAsync(operation);
            return operation;

            async Task ReloadTailAsync()
            {
                // TS chains onto the previous reload: a failure skips this one and surfaces here.
                await _facetReloadTail.ConfigureAwait(false);
                var candidate = await Api.CombineFacetLoaders(
                    PluginBundles.CreatePresentationFacetLoaders(data)).LoadAsync().ConfigureAwait(false);
                try
                {
                    await facetHost!.ReloadAsync(candidate.Facets).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    try
                    {
                        await candidate.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException("TUI plugin reload and cleanup failed",
                            new List<Exception> { error, cleanupError });
                    }

                    throw;
                }

                var retired = presentationFacets;
                presentationFacets = candidate;
                _presentationFacets = candidate;
                await retired.DisposeAsync().ConfigureAwait(false);
            }
        }

        var bridge = new PresentationBridgeFacet(this, server);
        facetHost = await Api.CreateFacetHostAsync(new FacetOptions
        {
            Facets =
            [
                SlashCommandsProvider.CreateSlashCommandsRuntimeFacet(),
                bridge,
                SlashCommandsProvider.CreateBuiltInSlashCommandsFacet(ReloadPresentationPluginsAsync),
                .. _sharedFacets.Facets,
                .. presentationFacets.Facets,
            ],
            ServiceSources = [server.Server, server.Session],
            // C374: the typed facades the experimental layer declares, over the raw member
            // dictionaries the sources hand out. The built-in facet and the presentation bridge
            // both resolve remote services through these.
            TypedServiceViews = TypedServiceViews(),
        }).ConfigureAwait(false);
        _facetHost = facetHost;
    }

    /// <summary>The seven typed client views, keyed by service id (C374).</summary>
    private static Dictionary<string, Func<RemoteServiceFacade, object>> TypedServiceViews()
        => new(StringComparer.Ordinal)
        {
            [SessionDirectory.Service.Id] = facade => RemoteServiceClients.SessionDirectory(facade),
            [SessionManagement.Service.Id] = facade => RemoteServiceClients.SessionManagement(facade),
            [PresentationPlugins.Service.Id] = facade => RemoteServiceClients.PresentationPlugins(facade),
            [Models.Service.Id] = facade => RemoteServiceClients.Models(facade),
            [AgentController.Service.Id] = facade => RemoteServiceClients.AgentController(facade),
            [Transcript.Service.Id] = facade => RemoteServiceClients.Transcript(facade),
            [SessionPlugins.Service.Id] = facade => new SessionPluginsClient(facade),
        };

    private async Task OpenPreparedSessionAsync(PreparedClientSession prepared)
    {
        var feature = _session ?? throw new InvalidOperationException(
            $"No Session service is available for {prepared.Server.ServerId}");
        await feature.Session.WhenAttachedAsync(prepared.Summary.SessionId, Context.Background)
            .ConfigureAwait(false);
        _selectedServerId = feature.ServerId;
        _sessionId = prepared.Summary.SessionId;
        UpdateAutocomplete();
        await OpenLaneAsync(feature).ConfigureAwait(false);
        _screen = "chat";
        _status = "";
        Rebuild();
    }

    private async Task CloseCoreAsync()
    {
        _closed = true;
        CompleteSelection(null);
        var errors = new List<Exception>();
        try
        {
            await _recoveryTransition.ConfigureAwait(false);
            await CloseLaneAsync().ConfigureAwait(false);
            await _facetReloadTail.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        if (_facetHost is not null)
        {
            try
            {
                await _facetHost.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            _facetHost = null;
        }

        var generations = new List<LoadedFacets>();
        if (_presentationFacets is not null) generations.Add(_presentationFacets);
        generations.Add(_sharedFacets);
        _presentationFacets = null;
        foreach (var generation in generations)
        {
            try
            {
                await generation.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1)
        {
            throw new AggregateException("Failed to dispose experimental TUI facets", errors);
        }
    }

    // ---------------------------------------------------------------- layout

    private void Rebuild()
    {
        _sessionHeading.SetText(
            _sessionId is null || _selectedServerId is null
                ? ""
                : ThemeRuntime.Current.Fg("dim", $"Server: {_selectedServerId}\nSession: {_sessionId}"));
        _statusContainer.Clear();
        if (_status.Length > 0)
        {
            _statusContainer.AddChild(new Text(ThemeRuntime.Current.Fg("dim", _status), 1, 0));
        }

        if (_chatView is not null) _statusContainer.AddChild(_chatView.Status);
        _footerComponent.SetText(ThemeRuntime.Current.Fg("dim", Footer()));
        _editorContainer.Clear();
        if (_screen == "select" && _selection is not null)
        {
            _chatInput.Focused = false;
            var selector = new Container();
            selector.AddChild(new Text(ThemeRuntime.Current.Bold(_selection.Title), 1, 1));
            var items = _selection.Items.ToList();
            _selectList = new SelectList(items, Math.Min(Math.Max(items.Count, 1), 12), SelectTheme);
            var selectedIndex = items.FindIndex(item => item.Value == _selection.SelectedValue);
            if (selectedIndex >= 0) _selectList.SetSelectedIndex(selectedIndex);
            _selectList.OnSelect = item => CompleteSelection(item.Value);
            _selectList.OnCancel = () => CompleteSelection(null);
            selector.AddChild(_selectList);
            _editorContainer.AddChild(selector);
        }
        else
        {
            _selectList = null;
            _chatInput.Focused = !_busy;
            _editorContainer.AddChild(_chatInput);
        }

        _layoutRoot.Invalidate();
        _requestRender();
    }

    private Task<string?> SelectAsync(
        string title, IReadOnlyList<PresentationSelectItem> items, string? selectedValue)
    {
        if (_selection is not null)
        {
            throw new InvalidOperationException("A slash command selector is already active");
        }

        var selection = new PendingSelection
        {
            Title = title,
            Items = [.. items.Select(item => new SelectItem
            {
                Value = item.Value,
                Label = item.Label,
                Description = item.Description,
            })],
            SelectedValue = selectedValue,
        };
        _selection = selection;
        _screen = "select";
        Rebuild();
        return selection.WaitAsync();
    }

    private void CompleteSelection(string? value)
    {
        var selection = _selection;
        if (selection is null) return;
        _selection = null;
        _screen = "chat";
        selection.Resolve(value);
        if (!_closed) Rebuild();
    }

    private void UpdateAutocomplete()
    {
        var commands = _slashCommands?.List() ?? [];
        var entries = new List<AutocompleteCommand>();
        foreach (var command in commands)
        {
            var completions = command.GetArgumentCompletions;
            entries.Add(new SlashCommand
            {
                Name = command.Name,
                Description = command.Description,
                ArgumentHint = command.ArgumentHint,
                // TS copies the items so the provider owns its array; a null answer stays null.
                GetArgumentCompletions = completions is null
                    ? null
                    : async prefix =>
                    {
                        var items = await completions(prefix).ConfigureAwait(false);
                        return items is null
                            ? null
                            : [.. items.Select(item => new AutocompleteItem
                            {
                                Value = item.Value,
                                Label = item.Label,
                                Description = item.Description,
                            })];
                    },
            });
        }

        _chatInput.SetAutocompleteProvider(
            new CombinedAutocompleteProvider(Directory.GetCurrentDirectory(), entries));
        _requestRender();
    }

    // ---------------------------------------------------------------- reconnection

    private void HandleConnectionState(string serverId, ServerConnectionState state)
    {
        if (_closed || _selectedServerId != serverId) return;
        if (state is ServerConnectionState.Connected)
        {
            if (_laneUnsubscribe is null)
            {
                _busy = true;
                _status = "Reattaching Session…";
                Rebuild();
            }

            return;
        }

        _busy = true;
        _status = state is ServerConnectionState.Connecting
            ? "Reconnecting to Radius…"
            : "Radius disconnected; retrying…";
        QueueRecovery(CloseLaneAsync);
        Rebuild();
    }

    private void HandleAttachmentState(SessionFeature feature, SessionAttachmentState state)
    {
        if (_closed || _selectedServerId != feature.ServerId || _sessionId is null) return;
        if (state is SessionAttachmentState.Attached { SessionId: var attached } && attached == _sessionId)
        {
            QueueRecovery(async () =>
            {
                if (_laneUnsubscribe is null) await OpenLaneAsync(feature).ConfigureAwait(false);
                _busy = false;
                _status = "";
                Rebuild();
            });
            return;
        }

        if (state is SessionAttachmentState.Attaching { SessionId: var attaching } && attaching == _sessionId)
        {
            _busy = true;
            _status = "Reattaching Session…";
            Rebuild();
        }
    }

    private void QueueRecovery(Func<Task> operation)
    {
        _recoveryTransition = RecoverAsync(_recoveryTransition, operation);
    }

    private async Task RecoverAsync(Task previous, Func<Task> operation)
    {
        try
        {
            await previous.ConfigureAwait(false);
            if (!_closed) await operation().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (_closed) return;
            _busy = true;
            _status = $"Reconnect error: {Message(error)}";
            Rebuild();
        }
    }

    private async Task OpenLaneAsync(SessionFeature feature)
    {
        await CloseLaneAsync().ConfigureAwait(false);
        var view = new ExperimentalChatView(_ui, Directory.GetCurrentDirectory());
        _chatView = view;
        _documentContainer.AddChild(_sessionHeading);
        _documentContainer.AddChild(view.Transcript);
        _pendingMessagesContainer.AddChild(view.PendingMessages);
        _laneUnsubscribe = feature.Transcript.State.Subscribe((value, _, _) =>
        {
            view.Apply(value);
            Rebuild();
            return (Task?)null;
        });
        if (feature.Transcript.State.Value is null)
        {
            await CloseLaneAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Transcript has no initialized view");
        }
    }

    private async Task CloseLaneAsync()
    {
        _laneUnsubscribe?.Dispose();
        _laneUnsubscribe = null;
        _chatView?.Dispose();
        _chatView = null;
        _documentContainer.Clear();
        _pendingMessagesContainer.Clear();
        _statusContainer.Clear();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- prompts

    private async Task RunPromptAsync(string messageText)
    {
        var prompt = messageText.Trim();
        if (prompt.Length == 0) return;
        if (prompt.StartsWith('/'))
        {
            var separator = prompt.IndexOf(' ');
            var name = prompt[1..(separator == -1 ? prompt.Length : separator)];
            var args = separator == -1 ? "" : prompt[(separator + 1)..].Trim();
            await ExecuteSlashCommandAsync(name, args).ConfigureAwait(false);
            return;
        }

        _chatInput.SetText("");
        try
        {
            await SubmitPromptAsync(prompt).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _status = $"Error: {Message(error)}";
            Rebuild();
        }
    }

    private async Task ExecuteSlashCommandAsync(string name, string args)
    {
        var command = _slashCommands?.List().FirstOrDefault(candidate => candidate.Name == name);
        _chatInput.SetText("");
        if (command is null)
        {
            _status = $"Unknown slash command: /{name}";
            Rebuild();
            return;
        }

        try
        {
            var result = await command.RunAsync(args, Context.Background).ConfigureAwait(false);
            if (result is not null)
            {
                // TS tests `"entryId" in result`; the C# record hierarchy answers it by type.
                if (result is AgentQueueResponse queue) ReportQueue(queue);
                else ReportOperation((AgentOperationResponse)result);
            }
        }
        catch (Exception error)
        {
            _status = $"Error: {Message(error)}";
            Rebuild();
        }
    }

    private async Task SubmitPromptAsync(string prompt)
    {
        var controller = _controller ?? throw new InvalidOperationException(
            "No Session AgentController service is available");
        var view = ConversationView();
        var running = view is not null && ClientTuiChat.LiveOf(view).ContainsKey("run");
        _status = running ? "Queueing steering message…" : "Running turn…";
        Rebuild();
        if (running)
        {
            ReportQueue(await controller.SteerAsync(
                new AgentPromptRequest(prompt, null), Context.Background).ConfigureAwait(false));
        }
        else
        {
            ReportOperation(await controller.PromptAsync(
                new AgentPromptRequest(prompt, null), Context.Background).ConfigureAwait(false));
        }
    }

    private async Task QueueFollowUpAsync(string text)
    {
        var controller = _controller;
        if (controller is null) return;
        try
        {
            _status = "Queueing follow-up…";
            Rebuild();
            ReportQueue(await controller.FollowUpAsync(
                new AgentPromptRequest(text, null), Context.Background).ConfigureAwait(false));
        }
        catch (Exception error)
        {
            _status = $"Error: {Message(error)}";
            Rebuild();
        }
    }

    private void ReportOperation(AgentOperationResponse response)
    {
        _status = response.IsAccepted ? "" : $"Operation rejected: {response.Error!.Message}";
        Rebuild();
    }

    private void ReportQueue(AgentQueueResponse response)
    {
        _status = response.IsAccepted
            ? $"Queued {((AgentQueueResponse.Accepted)response).EntryId}."
            : $"Message rejected: {response.Error!.Message}";
        Rebuild();
    }

    private void Interrupt()
    {
        var view = ConversationView();
        var controller = _controller;
        if (view is null || !ClientTuiChat.LiveOf(view).ContainsKey("run") || controller is null) return;
        _status = "Aborting…";
        Rebuild();
        _ = AbortAsync(controller);

        async Task AbortAsync(IAgentController target)
        {
            try
            {
                await target.AbortAsync(Context.Background).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _status = $"Error: {Message(error)}";
                Rebuild();
                return;
            }

            if (_status != "Aborting…") return;
            _status = "";
            Rebuild();
        }
    }

    // ---------------------------------------------------------------- reads

    private ConversationView? ConversationView() => _session?.Transcript.State.Value;

    private string Footer()
    {
        var view = ConversationView();
        if (view is null) return "/model · /thinking · /compact · /reload";
        var agent = view.Docs.TryGetValue("pi.agent", out var doc) ? doc : EmptyAgent;
        var model = agent.TryGetValue("model", out var selected) && selected is IReadOnlyDictionary<string, object?> pair
            ? $"{Text(Opt(pair, "provider"))}/{Text(Opt(pair, "modelId"))}"
            : "no model";
        var thinking = agent.TryGetValue("thinkingLevel", out var level) && level is string text ? text : "off";
        return $"{model} · thinking:{thinking} · {view.Entries.Count} entries · /model · /thinking · /compact · /reload";
    }

    private static IReadOnlyDictionary<string, object?> EmptyAgent { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private static object? Opt(IReadOnlyDictionary<string, object?> fields, string key)
        => fields.TryGetValue(key, out var value) ? value : null;

    private static string Text(object? value)
        => value as string ?? throw new InvalidOperationException(
            $"Expected a string, got {value?.GetType().Name ?? "null"}");

    private static string Message(Exception error) => error.Message;

    /// <summary>TS's inline <c>operation.catch(() => {})</c> tail: a settled task that never faults.</summary>
    private static async Task IgnoreAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Swallowed: the tail only serialises reloads, it never reports.
        }
    }

    /// <summary>
    /// TS <c>@pi/presentation-bridge</c>: provides the process-local UI surface and resolves the
    /// session-scoped services once the host is assembled.
    /// </summary>
    private sealed class PresentationBridgeFacet(ExperimentalClientTui owner, ClientTuiServer server) : IFacet
    {
        public string Id => "@pi/presentation-bridge";

        public void Setup(IFacetEnvironment environment)
        {
            environment.Provide(PresentationUI.Service, new PresentationUIBridge(owner));
            environment.Require(ServicesSlashCommands.Service);
            environment.Require(AgentController.Service);
            environment.Require(Transcript.Service);
            environment.OnActivate(() =>
            {
                if (owner._session is not null || owner._slashCommands is not null || owner._controller is not null)
                {
                    throw new InvalidOperationException("Presentation services are already active");
                }

                var commands = environment.Use(ServicesSlashCommands.Service);
                var controller = environment.Use(AgentController.Service);
                var transcript = environment.Use(Transcript.Service);
                var sessionFeature = new SessionFeature(server.ServerId, server.Session, transcript);
                owner._session = sessionFeature;
                owner._slashCommands = commands;
                owner._controller = controller;
                environment.Own(() =>
                {
                    if (ReferenceEquals(owner._session, sessionFeature)) owner._session = null;
                    if (ReferenceEquals(owner._slashCommands, commands)) owner._slashCommands = null;
                    if (ReferenceEquals(owner._controller, controller)) owner._controller = null;
                    return Task.CompletedTask;
                });
                environment.Own(Own(commands.Subscribe(_ => owner.UpdateAutocomplete())));
                if (server.Radius)
                {
                    environment.Own(Own(server.Server.Connection.Subscribe(
                        (state, _, _) =>
                        {
                            owner.HandleConnectionState(server.ServerId, state);
                            return (Task?)null;
                        })));
                    environment.Own(Own(server.Session.Attachment.Subscribe(
                        (state, _, _) =>
                        {
                            owner.HandleAttachmentState(sessionFeature, state);
                            return (Task?)null;
                        })));
                }

                return Task.CompletedTask;
            });
        }

        /// <summary>TS <c>env.own(handle)</c> for an unsubscribe handle (TS's <c>own</c> takes either).</summary>
        private static Func<Task> Own(IDisposable handle) => () =>
        {
            handle.Dispose();
            return Task.CompletedTask;
        };
    }

    /// <summary>The process-local UI surface the built-in commands drive. TS the inline object.</summary>
    private sealed class PresentationUIBridge(ExperimentalClientTui owner) : IPresentationUI
    {
        public Task<string?> SelectAsync(
            string title, IReadOnlyList<PresentationSelectItem> items, string? selectedValue, Context context)
            => owner.SelectAsync(title, items, selectedValue);

        public void ShowStatus(string message, Context context) => owner.ShowStatus(message);
    }

    /// <summary>
    /// The worker-side plugin view. The built-in <c>/reload</c> command resolves it from the
    /// session source; only the reload member is used by a presentation.
    /// </summary>
    private sealed class SessionPluginsClient(RemoteServiceFacade facade) : ISessionPlugins
    {
        public Task ReloadAsync(Context context)
            => facade.Member("reload").CallAsync([], context);
    }
}

/// <summary>Port of the module-level <c>prepareClientSession</c>.</summary>
public static class ClientTuiSession
{
    /// <summary>
    /// Choose (or create) the session the command names, prepare its presentation plugins, and
    /// attach it — the half of the startup that runs before any facet exists.
    /// </summary>
    public static async Task<PreparedClientSession> PrepareClientSessionAsync(
        ClientCommand command, IReadOnlyList<ClientTuiServer> servers)
    {
        var opened = servers.Select(server => (Server: server, Services: server.Server.Open(
            new RemoteServicesOptions
            {
                Services =
                [
                    SessionDirectory.Service.Id,
                    SessionManagement.Service.Id,
                    PresentationPlugins.Service.Id,
                ],
                AssertAccess = () => { },
                OnError = _ => { },
            }))).ToList();
        var features = opened.Select(entry => (
            entry.Server,
            Directory: RemoteServiceClients.SessionDirectory(Facade(entry.Services, SessionDirectory.Service.Id)),
            Management: RemoteServiceClients.SessionManagement(Facade(entry.Services, SessionManagement.Service.Id)),
            Plugins: RemoteServiceClients.PresentationPlugins(Facade(entry.Services, PresentationPlugins.Service.Id))))
            .ToList();
        await Task.WhenAll(opened.Select(entry => entry.Services.ReadyAsync())).ConfigureAwait(false);

        (ClientTuiServer Server, ISessionManagement Management, IPresentationPlugins Plugins, SessionSummary Summary)? selected = null;
        if (command.SessionId is not null)
        {
            var matches = features.SelectMany(feature =>
                    (feature.Directory.State.Value?.Sessions ?? [])
                        .Where(session => session.SessionId == command.SessionId)
                        .Select(summary => (feature.Server, feature.Management, feature.Plugins, Summary: summary)))
                .ToList();
            if (matches.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Session {command.SessionId} is available from more than one server");
            }

            selected = matches.Count > 0 ? matches[0] : null;
            if (selected is null)
            {
                if (command.Connect is TransportAddress.Radius)
                {
                    throw new InvalidOperationException(
                        $"Remote server does not contain Session {command.SessionId}");
                }

                var feature = RequireSingleServer(features);
                selected = (feature.Server, feature.Management, feature.Plugins,
                    await feature.Management.CreateAsync(
                        new SessionCreateOptions(command.SessionId), Context.Background).ConfigureAwait(false));
            }
        }
        else if (command.Continue == true || command.Resume == true)
        {
            selected = features.SelectMany(feature =>
                    (feature.Directory.State.Value?.Sessions ?? []).Select(summary =>
                        (feature.Server, feature.Management, feature.Plugins, Summary: summary)))
                .OrderByDescending(entry => entry.Summary.CreatedAt)
                // TS sorts by the summary's own serverId (localeCompare), not the server's id.
                .ThenBy(entry => entry.Summary.ServerId, StringComparer.CurrentCulture)
                .ThenBy(entry => entry.Summary.SessionId, StringComparer.CurrentCulture)
                .FirstOrDefault();
        }

        if (selected is null)
        {
            var feature = RequireSingleServer(features);
            selected = (feature.Server, feature.Management, feature.Plugins,
                await feature.Management.CreateAsync(new SessionCreateOptions(), Context.Background)
                    .ConfigureAwait(false));
        }

        var presentationPlugins = await selected.Value.Plugins.PrepareSessionAsync(
            new PrepareSessionRequest(
                selected.Value.Summary.SessionId,
                command.PluginPackages?.Select(packagePath => Path.GetFullPath(packagePath)).ToList()),
            Context.Background).ConfigureAwait(false);
        await selected.Value.Management.AttachAsync(selected.Value.Summary.SessionId, Context.Background)
            .ConfigureAwait(false);
        await selected.Value.Server.Session.WhenAttachedAsync(
            selected.Value.Summary.SessionId, Context.Background).ConfigureAwait(false);
        return new PreparedClientSession(
            selected.Value.Server, selected.Value.Summary, presentationPlugins);
    }

    private static T RequireSingleServer<T>(IReadOnlyList<T> features)
    {
        if (features.Count != 1)
        {
            throw new InvalidOperationException("Starting a Session requires exactly one server");
        }

        return features[0];
    }

    private static RemoteServiceFacade Facade(IRemoteServices services, string serviceId)
        => (RemoteServiceFacade)services.UseRaw(serviceId)!;
}

/// <summary>Port of <c>runClientTui</c>: the fullscreen terminal driver.</summary>
public static class ClientTuiRunner
{
    public static async Task RunClientTuiAsync(ClientCommand command, RunClientTuiOptions? options = null)
    {
        options ??= new RunClientTuiOptions();
        var cwd = Directory.GetCurrentDirectory();
        var agentDir = Config.GetAgentDir();
        var settingsManager = SettingsManager.Create(cwd, agentDir);
        var resourceLoader = new DefaultResourceLoader(
            cwd,
            agentDir,
            settingsManager,
            noExtensions: true,
            noSkills: true,
            noPromptTemplates: true,
            noContextFiles: true);
        await resourceLoader.ReloadAsync().ConfigureAwait(false);
        ThemeRuntime.SetRegisteredThemes(
            [.. resourceLoader.Themes.Select(loaded => ThemeRuntime.LoadThemeFromPath(loaded.Path))]);
        var runtime = await ClientRuntimes.OpenClientRuntimeAsync(command, options).ConfigureAwait(false);
        var tui = TuiRenderer.CreateInteractiveTui(new InteractiveTuiOptions
        {
            TuiMode = TuiMode.Fullscreen,
            ShowHardwareCursor = settingsManager.GetShowHardwareCursor(),
            LogDirectory = agentDir,
            FullscreenWheelScrollLines = settingsManager.GetFullscreenWheelScrollLines(),
        });
        tui.SetClearOnShrink(settingsManager.GetClearOnShrink());
        ExperimentalClientTui? component = null;
        var tuiStarted = false;
        var themeController = new InteractiveThemeController(tui, new InteractiveThemeControllerOptions
        {
            GetSettingsManager = () => settingsManager,
            ShowError = error => component?.ShowError(error),
            OnChanged = () => component?.RefreshTheme(),
        });
        try
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Finish()
            {
                themeController.DisableAutoSync();
                if (tuiStarted)
                {
                    tui.Stop();
                    tuiStarted = false;
                }

                finished.TrySetResult();
            }

            component = await ExperimentalClientTui.CreateAsync(new ExperimentalClientTui.CreateExperimentalClientTuiOptions
            {
                Command = command,
                Ui = tui,
                Servers = [.. runtime.Servers.Select(server => new ClientTuiServer(
                    server.Route.ServerId,
                    server.Route is ClientRuntimeRoute.Radius,
                    server.Server,
                    server.Session))],
                FacetLoader = options.FacetLoader,
                RequestRender = () => tui.RequestRender(),
                Finish = Finish,
            }).ConfigureAwait(false);
            tui.AddChild(component);
            if (tui is IViewportTui viewport) viewport.SetLayoutRoot(component.LayoutRoot);
            tui.SetFocus(component);
            tuiStarted = true;
            tui.Start();
            themeController.ApplyFromSettings();
            await finished.Task.ConfigureAwait(false);
        }
        finally
        {
            themeController.Dispose();
            ThemeRuntime.StopThemeWatcher();
            if (tuiStarted) tui.Stop();
            if (component is not null) await component.CloseAsync().ConfigureAwait(false);
            await runtime.Dispose().ConfigureAwait(false);
        }
    }
}
