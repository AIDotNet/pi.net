// ============================================================================
// InteractiveMode — class assembly, first slice of 4f-4m.
//
// Port of interactive-mode.ts's InteractiveModeOptions (L425-448), the class field
// block (L451-585, plus the two later members MAX_WIDGET_LINES / isShuttingDown),
// the convenience accessors (L587-599) and the constructor (L601-667), plus the
// instance methods the constructor and its runtime callbacks reach into:
//
//   showError / showWarning (L4602-4612), showStatus (L3803-3823),
//   updateEditorBorderColor (L4495-4504), updateTerminalTitle (L1124-1132),
//   mountInteractiveTui (L866-872) / stopInteractiveTui (L874-881) /
//   switchTuiMode (L883-934), applyRuntimeSettings (L2052-2080) +
//   ensurePngTranscoder (L2045-2050) + applyFullscreenScrollbarSetting (L2040-2042),
//   subscribeToAgent (L3380-3384), updateAvailableProviderCount (L5231-5238),
//   rebindCurrentSession (L2082-2107), resetExtensionUI (L2443-2474) and the
//   extension-UI family it touches: the status-indicator chain (L2291-2384),
//   widgets (L2389-2441, L2482-2510), footer/header swap (L2515-2581), terminal
//   input listeners (L2583-2604), the selector/input/editor dialogs (L2681-2854),
//   setCustomEditorComponent (L2855-2930), showExtensionNotify (L2935-2943),
//   showSelector / disposeActiveSelector (L4852-4886) and the small view-state
//   leaves clearEditor / toggleToolOutputExpansion / setToolsExpanded /
//   updateThinkingBlockVisibility / toggleThinkingBlockVisibility (L4536-4600).
//
// Not yet in this file (registered seams, retired in later 4f-4m slices):
//   createBaseAutocompleteProvider + setupAutocompleteProvider → autocomplete batch,
//   renderCurrentSessionState + handleEvent → event/rendering batch,
//   bindCurrentSessionExtensions → extension-binding batch,
//   handleRightClickPaste / handleClipboardPaste → input batch.
// ============================================================================

using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils.Image;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>
/// Port of the TS <c>InteractiveModeOptions</c> (interactive-mode.ts L425-448).
/// </summary>
public sealed record InteractiveModeOptions
{
    /// <summary>Providers that were migrated to auth.json (shows warning).</summary>
    public IReadOnlyList<string>? MigratedProviders { get; init; }

    /// <summary>Diagnostics collected before the interactive TUI was initialized.</summary>
    public IReadOnlyList<AgentSessionRuntimeDiagnostic>? StartupDiagnostics { get; init; }

    /// <summary>Warning message if session model couldn't be restored.</summary>
    public string? ModelFallbackMessage { get; init; }

    /// <summary>Cwd to trust after reload if it gained a .pi directory during this implicitly trusted session.</summary>
    public string? AutoTrustOnReloadCwd { get; init; }

    /// <summary>Initial message to send on startup (can include @file content).</summary>
    public string? InitialMessage { get; init; }

    /// <summary>Images to attach to the initial message.</summary>
    public IReadOnlyList<ImageContent>? InitialImages { get; init; }

    /// <summary>Additional messages to send after the initial message.</summary>
    public IReadOnlyList<string>? InitialMessages { get; init; }

    /// <summary>Force verbose startup (overrides quietStartup setting).</summary>
    public bool? Verbose { get; init; }

    /// <summary>TUI layout mode.</summary>
    public TuiMode? TuiMode { get; init; }

    /// <summary>Initial interactive theme setting for this invocation.</summary>
    public string? InitialThemeSetting { get; init; }

    /// <summary>Terminal implementation. Defaults to the current process terminal.</summary>
    public ITerminal? Terminal { get; init; }
}

/// <summary>
/// The interactive TUI mode (port of <c>InteractiveMode</c>, interactive-mode.ts L450-7082).
/// Assembled across the 4f-4m slices; each slice adds the members it owns.
/// </summary>
public partial class InteractiveMode
{
    // The TS field block (L451-585) is ported verbatim in one place.
    private readonly AgentSessionRuntime _runtimeHost;
    private TuiBase _renderer;
    private readonly ITui _ui;
    private TuiMainScreenRenderState? _mainScreenRenderState;
    private readonly Container _loadedResourcesContainer;
    private readonly Container _chatContainer;
    private readonly Container _documentContainer;
    private ScrollView? _transcriptScrollView;
    private IComponent? _fullscreenLayoutRoot;
    private readonly Container _pendingMessagesContainer;
    private readonly Container _statusContainer;
    private readonly CustomEditor _defaultEditor;
    private Editor _editor;
    private EditorFactory? _editorComponentFactory;
    private IAutocompleteProvider? _autocompleteProvider;
    private readonly List<AutocompleteProviderFactory> _autocompleteProviderWrappers = [];
    private string? _fdPath;
    private readonly Container _editorContainer;
    private object? _activeSelectorToken;
    private Action? _activeSelectorDispose;
    private readonly FooterComponent _footer;
    private readonly Container _footerContainer;
    private readonly FooterDataProvider _footerDataProvider;

    /// <summary>Reports working, blocked, done, and error states to terminals that support OSC 7501.</summary>
    private readonly ProgramStatusReporter _programStatus;

    // Stored so the same manager can be injected into custom editors, selectors, and extension UI.
    private readonly Core.KeybindingsManager _keybindings;
    private readonly string _version;
    private bool _isInitialized;
    private Action<string>? _onInputCallback;
    private readonly List<string> _pendingUserInputs = [];
    private StatusIndicator? _activeStatusIndicator;
    private bool _activeWorkingIndicatorEmbedded;
    private readonly IdleStatus _idleStatus = new();
    private string? _workingMessage;
    private bool _workingVisible = true;
    private WorkingIndicatorOptions? _workingIndicatorOptions;
    private const string DefaultWorkingMessage = "Working";
    private const string DefaultHiddenThinkingLabel = "Thinking...";
    private string _hiddenThinkingLabel = DefaultHiddenThinkingLabel;

    private double _lastSigintTime;
    private double _lastEscapeTime;
    private string? _changelogMarkdown;
    private bool _startupNoticesShown;
    private bool _anthropicSubscriptionWarningShown;

    // Status line tracking (for mutating immediately-sequential status updates)
    private Spacer? _lastStatusSpacer;
    private ThemedText? _lastStatusText;
    private string _lastStatusMessage = "";
    private bool _managedToolStatusStarted;

    // Streaming message tracking
    private AssistantMessageComponent? _streamingComponent;
    private readonly HashSet<string> _entriesRenderedByBoundaryCompaction = new(StringComparer.Ordinal);
    private AssistantMessage? _streamingMessage;

    // Tool execution tracking: toolCallId -> component
    private readonly Dictionary<string, ToolExecutionComponent> _pendingTools = new(StringComparer.Ordinal);

    // Tool output expansion state
    private bool _toolOutputExpanded;

    // Thinking block visibility state
    private bool _hideThinkingBlock;
    private int _outputPad = 1;

    // Skill commands: command name -> skill file path
    private readonly Dictionary<string, string> _skillCommands = new(StringComparer.Ordinal);

    // Agent subscription unsubscribe handle
    private IDisposable? _unsubscribe;
    private readonly List<Action> _signalCleanupHandlers = [];

    // Track if editor is in bash mode (text starts with !)
    private bool _isBashMode;

    // Track current bash execution component
    private BashExecutionComponent? _bashComponent;

    // Track pending bash components (shown in pending area, moved to chat on submit)
    private readonly List<BashExecutionComponent> _pendingBashComponents = [];

    // Auto-compaction state
    private Action? _autoCompactionEscapeHandler;

    // Auto-retry state
    private Action? _retryEscapeHandler;

    // Messages queued while compaction is running
    private readonly List<CompactionQueuedMessage> _compactionQueuedMessages = [];

    // Shutdown state
    private bool _shutdownRequested;

    /// <summary>The <c>/bug</c> hint is shown at most once per session so error output stays readable.</summary>
    private bool _bugReportHintShown;
    private bool _installChangeWarningShown;

    // Extension UI state
    private ExtensionSelectorComponent? _extensionSelector;
    private ExtensionInputComponent? _extensionInput;
    private ExtensionEditorComponent? _extensionEditor;
    private readonly HashSet<TerminalInputSubscription> _extensionTerminalInputSubscriptions = [];

    // Extension widgets (components rendered above/below the editor)
    private readonly Dictionary<string, IComponent> _extensionWidgetsAbove = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IComponent> _extensionWidgetsBelow = new(StringComparer.Ordinal);
    private Container _widgetContainerAbove = null!;
    private Container _widgetContainerBelow = null!;

    // Custom footer from extension (null = use built-in footer)
    private IComponent? _customFooter;

    // Header container that holds the built-in or custom header
    private readonly Container _headerContainer;

    // Built-in header (logo + keybinding hints + changelog)
    private IComponent? _builtInHeader;

    // Custom header from extension (null = use built-in header)
    private IComponent? _customHeader;

    private InteractiveModeOptions _options;
    private readonly Action _onRightClickPaste;
    // TS reassigns `autoTrustOnReloadCwd = undefined` once the implicit trust is saved
    // (maybeSaveImplicitProjectTrustAfterReload), so the field is mutable in the port too.
    private string? _autoTrustOnReloadCwd;

    // Constructed at the very end of the constructor (TS order), but the rebind callback registered
    // above captures it; `null!` satisfies the nullable flow analysis for that closure.
    private readonly InteractiveThemeController _themeController = null!;

    // TS L4268: guard so a shutdown racing in-flight work runs its exit path once.
    private bool _isShuttingDown;
#pragma warning restore CS0169, CS0414, CS0649

    /// <summary>A terminal-input listener registration (TS's <c>extensionTerminalInputSubscriptions</c> set entries).</summary>
    private sealed class TerminalInputSubscription(
        TerminalInputHandler handler,
        Func<string, TuiInputListenerResult?> listener,
        Action unsubscribe)
    {
        public TerminalInputHandler Handler { get; } = handler;
        public Func<string, TuiInputListenerResult?> Listener { get; } = listener;
        public Action Unsubscribe { get; set; } = unsubscribe;
    }

    /// <summary>
    /// TS hands the extension handler straight to <c>tui.addInputListener</c> (structural typing);
    /// the C# TUI listener shape differs, so adapt. TS's <c>{ consume?, data? } | undefined</c> maps
    /// onto <see cref="TuiInputListenerResult"/> field by field.
    /// </summary>
    private static Func<string, TuiInputListenerResult?> ToTuiInputListener(TerminalInputHandler handler) =>
        data =>
        {
            var result = handler(data);
            return result is null
                ? null
                : new TuiInputListenerResult { Consume = result.Value.Consume, Data = result.Value.Data };
        };

    // ---------------------------------------------------------------- convenience accessors (L587-599)

    private AgentSession Session => _runtimeHost.Session;
    private Pi.Agent.Agent Agent => Session.Agent;
    private SessionManager SessionManager => Session.SessionManager;
    private SettingsManager SettingsManager => Session.SettingsManager;

    // ---------------------------------------------------------------- test accessors
    // TS keeps these members private; widened to internal for the test suite (InternalsVisibleTo),
    // following the read-only observation seams the earlier 4f batches added to their components.

    internal ITui UiForTests => _ui;
    internal TuiBase RendererForTests => _renderer;
    internal CustomEditor DefaultEditorForTests => _defaultEditor;
    internal Editor EditorForTests => _editor;
    internal InteractiveModeOptions OptionsForTests => _options;
    internal bool ToolOutputExpandedForTests => _toolOutputExpanded;
    internal StatusIndicator? ActiveStatusIndicatorForTests => _activeStatusIndicator;
    internal bool ActiveWorkingIndicatorEmbeddedForTests => _activeWorkingIndicatorEmbedded;
    internal IReadOnlyList<IComponent> ChatChildrenForTests => _chatContainer.GetChildrenSnapshot();
    internal IReadOnlyList<IComponent> PendingMessagesContainerChildrenForTests => _pendingMessagesContainer.GetChildrenSnapshot();
    internal SessionManager SessionManagerForTests => SessionManager;
    internal IReadOnlyList<IComponent> EditorContainerChildrenForTests => _editorContainer.GetChildrenSnapshot();
    internal IReadOnlyList<IComponent> FooterContainerChildrenForTests => _footerContainer.GetChildrenSnapshot();
    internal IReadOnlyList<IComponent> HeaderContainerChildrenForTests => _headerContainer.GetChildrenSnapshot();
    internal IReadOnlyList<IComponent> StatusContainerChildrenForTests => _statusContainer.GetChildrenSnapshot();
    internal IReadOnlyList<IComponent> WidgetContainerAboveChildrenForTests => _widgetContainerAbove.GetChildrenSnapshot();
    internal IReadOnlyList<IComponent> WidgetContainerBelowChildrenForTests => _widgetContainerBelow.GetChildrenSnapshot();
    internal ExtensionSelectorComponent? ExtensionSelectorForTests => _extensionSelector;
    internal FooterDataProvider FooterDataProviderForTests => _footerDataProvider;

    /// <summary>Test seam: the registered autocomplete wrapper factories.</summary>
    internal IReadOnlyList<AutocompleteProviderFactory> AutocompleteProviderWrappersForTests => _autocompleteProviderWrappers;

    /// <summary>Test seam: the loaded-resources container children.</summary>
    internal IReadOnlyList<IComponent> LoadedResourcesContainerChildrenForTests => _loadedResourcesContainer.GetChildrenSnapshot();

    /// <summary>
    /// Test seam: the staged changelog markdown (normally staged by <see cref="InitAsync"/>).
    /// TS keeps this as a plain field the constructor path fills in.
    /// </summary>
    internal string? ChangelogMarkdownForTests
    {
        set => _changelogMarkdown = value;
    }

    public InteractiveMode(AgentSessionRuntime runtimeHost, InteractiveModeOptions? options = null)
    {
        options ??= new InteractiveModeOptions();
        _runtimeHost = runtimeHost;
        TerminalImage.SetCapabilityOverrides(ToTuiCapabilityOverrides(SettingsManager.GetTerminalCapabilityOverrides()));
        var tuiMode = options.TuiMode ?? SettingsManager.GetTuiMode();
        _options = options with { TuiMode = tuiMode };
        _autoTrustOnReloadCwd = options.AutoTrustOnReloadCwd;
        _runtimeHost.SetBeforeSessionInvalidate(ResetExtensionUI);
        _runtimeHost.SetRebindSession(async _ =>
        {
            await RebindCurrentSessionAsync(renderBeforeBind: true).ConfigureAwait(true);
            _themeController.ApplyFromSettings();
        });
        _version = Config.Version;
        _onRightClickPaste = () => _ = HandleRightClickPasteAsync();
        _renderer = TuiRenderer.CreateInteractiveTui(new InteractiveTuiOptions
        {
            TuiMode = tuiMode,
            ShowHardwareCursor = SettingsManager.GetShowHardwareCursor(),
            LogDirectory = Config.GetAgentDir(),
            Terminal = options.Terminal,
            OnRightClickPaste = _onRightClickPaste,
            FullscreenCopyOnSelect = SettingsManager.GetFullscreenCopyOnSelect(),
            FullscreenWheelScrollLines = SettingsManager.GetFullscreenWheelScrollLines(),
        });
        _ui = TuiRenderer.CreateInteractiveTuiReference(() => _renderer);
        _ui.SetClearOnShrink(SettingsManager.GetClearOnShrink());
        _programStatus = new ProgramStatusReporter(() => _ui.Terminal, () => SessionManager.GetSessionName());
        _headerContainer = new Container();
        _loadedResourcesContainer = new Container();
        _chatContainer = new Container();
        _documentContainer = new Container();
        _documentContainer.AddChild(_headerContainer);
        _documentContainer.AddChild(_loadedResourcesContainer);
        _documentContainer.AddChild(_chatContainer);
        _pendingMessagesContainer = new Container();
        _statusContainer = new Container();
        _widgetContainerAbove = new Container();
        _widgetContainerBelow = new Container();
        _keybindings = Core.KeybindingsManager.Create();
        GlobalKeybindings.Set(_keybindings);
        var editorPaddingX = SettingsManager.GetEditorPaddingX();
        var autocompleteMaxVisible = SettingsManager.GetAutocompleteMaxVisible();
        _defaultEditor = new CustomEditor(_ui, ThemeHelpers.GetEditorTheme(), _keybindings, new CustomEditorOptions
        {
            PaddingX = editorPaddingX,
            AutocompleteMaxVisible = autocompleteMaxVisible,
            EmbedWorkingStatus = true,
        });
        _editor = _defaultEditor;
        _editorContainer = new Container();
        _editorContainer.AddChild(_editor);
        _footerDataProvider = new FooterDataProvider(SessionManager.GetCwd());
        _footer = new FooterComponent(Session, _footerDataProvider);
        _footer.SetAutoCompactEnabled(Session.AutoCompactionEnabled);
        _footerContainer = new Container();
        _footerContainer.AddChild(_footer);

        // Load hide thinking block setting
        _hideThinkingBlock = SettingsManager.GetHideThinkingBlock();
        _outputPad = (int)SettingsManager.GetOutputPad();

        // Register themes from resource loader and initialize. The C# resource loader keeps loaded
        // theme documents as <see cref="LoadedTheme"/> records, so each file is read again here
        // (same content the loader validated); TS hands over the already-built Theme objects.
        ThemeRuntime.SetRegisteredThemes([.. Session.ResourceLoader.Themes.Select(LoadRegisteredTheme)]);
        _themeController = new InteractiveThemeController(_ui, new InteractiveThemeControllerOptions
        {
            GetSettingsManager = () => SettingsManager,
            ShowError = ShowError,
            OnChanged = UpdateEditorBorderColor,
            InitialThemeSetting = options.InitialThemeSetting,
        });
    }

    /// <summary>
    /// Settings 层的 <see cref="TerminalCapabilityOverrides"/> → Pi.Tui 的能力覆盖记录。
    /// 4g-1 起由 <c>Cli.StartupUi</c> 共用（启动期 TUI 也要装同一份覆盖），故从 private 提到 internal。
    /// </summary>
    internal static Pi.Tui.CapabilityOverrides ToTuiCapabilityOverrides(TerminalCapabilityOverrides overrides) =>
        new()
        {
            Images = overrides.ImagesSpecified ? overrides.Images : null,
            TrueColor = overrides.TrueColor,
            Hyperlinks = overrides.Hyperlinks,
        };

    private static Theme LoadRegisteredTheme(LoadedTheme loadedTheme) =>
        ThemeRuntime.LoadThemeFromPath(loadedTheme.Path);

    // ---------------------------------------------------------------- notices (L4602-4612, L3803-3823)

    internal void ShowError(string errorMessage)
    {
        _chatContainer.AddChild(new Spacer(1));
        var notice = InteractiveModeDiagnostics.ErrorNotice(errorMessage, _outputPad);
        _chatContainer.AddChild(new ThemedText(() => ThemeRuntime.Current.Fg(notice.ColorToken, notice.Text), notice.Pad, 0));
        _ui.RequestRender();
    }

    internal void ShowWarning(string warningMessage)
    {
        _chatContainer.AddChild(new Spacer(1));
        var notice = InteractiveModeDiagnostics.WarningNotice(warningMessage);
        _chatContainer.AddChild(new ThemedText(() => ThemeRuntime.Current.Fg(notice.ColorToken, notice.Text), notice.Pad, 0));
        _ui.RequestRender();
    }

    internal void ShowStatus(string message)
    {
        var children = _chatContainer.GetChildrenSnapshot();
        var last = children.Count > 0 ? children[^1] : null;
        var secondLast = children.Count > 1 ? children[^2] : null;

        if (InteractiveModeDiagnostics.ShouldCoalesceStatus(
                children.Count,
                ReferenceEquals(last, _lastStatusText),
                ReferenceEquals(secondLast, _lastStatusSpacer)))
        {
            _lastStatusMessage = message;
            _lastStatusText!.Invalidate();
            _ui.RequestRender();
            return;
        }

        var spacer = new Spacer(1);
        _lastStatusMessage = message;
        var text = new ThemedText(() => ThemeRuntime.Current.Fg("dim", _lastStatusMessage), 1, 0);
        _chatContainer.AddChild(spacer);
        _chatContainer.AddChild(text);
        _lastStatusSpacer = spacer;
        _lastStatusText = text;
        _ui.RequestRender();
    }

    // ---------------------------------------------------------------- editor chrome (L4495-4504, L1124-1132)

    internal void UpdateEditorBorderColor()
    {
        var border = InteractiveModeDiagnostics.ResolveEditorBorderColor(
            ThemeRuntime.Current, _isBashMode, Session.ThinkingLevel);
        _editor.BorderColor = border;
        _activeStatusIndicator?.Invalidate();
        _ui.RequestRender();
    }

    internal void UpdateTerminalTitle()
    {
        var title = InteractiveModeDiagnostics.BuildTerminalTitle(SessionManager.GetCwd(), SessionManager.GetSessionName());
        _ui.Terminal.SetTitle(title);
    }

    // ---------------------------------------------------------------- TUI mount / stop / switch (L866-934)

    private void MountInteractiveTui(TuiBase tui, IReadOnlyList<IComponent> components)
    {
        foreach (var component in components)
        {
            tui.AddChild(component);
        }

        if (tui is IViewportTui viewport)
        {
            if (_fullscreenLayoutRoot is null)
            {
                throw new InvalidOperationException("Fullscreen layout is not initialized");
            }

            viewport.SetLayoutRoot(_fullscreenLayoutRoot);
        }
    }

    private void StopInteractiveTui(string fullscreenExitOutput)
    {
        if (_renderer.Mode == TuiMode.Fullscreen && fullscreenExitOutput == "transcript")
        {
            while (_renderer.HasOverlay())
            {
                _renderer.HideOverlay();
            }

            SwitchTuiMode(TuiMode.Regular, restoreProgress: false, startRenderer: false);
            _renderer.RenderNow();
        }

        _ui.Stop(new TuiStopOptions { PreserveScreen = _renderer.Mode == TuiMode.Fullscreen });
    }

    internal bool SwitchTuiMode(TuiMode mode, bool restoreProgress = true, bool startRenderer = true)
    {
        var previousUi = _renderer;
        if (mode == previousUi.Mode)
        {
            return true;
        }

        if (previousUi.HasOverlay())
        {
            return false;
        }

        var components = new List<IComponent>(previousUi.GetChildrenSnapshot());
        var focus = previousUi.GetFocusedComponent();
        var terminal = previousUi.Terminal;
        var showHardwareCursor = previousUi.GetShowHardwareCursor();
        var clearOnShrink = previousUi.GetClearOnShrink();
        var onDebug = previousUi.OnDebug;
        if (previousUi is TuiMainScreen mainScreen)
        {
            _mainScreenRenderState = mainScreen.CaptureRenderState();
        }

        previousUi.Stop(new TuiStopOptions { PreserveScreen = true });
        previousUi.SetFocus(null);
        previousUi.Clear();
        if (previousUi is IViewportTui previousViewport)
        {
            previousViewport.SetLayoutRoot(null);
        }

        var nextUi = TuiRenderer.CreateInteractiveTui(new InteractiveTuiOptions
        {
            TuiMode = mode,
            ShowHardwareCursor = showHardwareCursor,
            LogDirectory = Config.GetAgentDir(),
            Terminal = terminal,
            OnRightClickPaste = _onRightClickPaste,
            FullscreenCopyOnSelect = SettingsManager.GetFullscreenCopyOnSelect(),
            FullscreenWheelScrollLines = SettingsManager.GetFullscreenWheelScrollLines(),
        });
        nextUi.SetClearOnShrink(clearOnShrink);
        nextUi.OnDebug = onDebug;
        if (nextUi is TuiMainScreen nextMainScreen && _mainScreenRenderState is not null)
        {
            nextMainScreen.RestoreRenderState(_mainScreenRenderState);
        }

        _renderer = nextUi;
        _options = _options with { TuiMode = mode };
        MountInteractiveTui(nextUi, components);
        nextUi.Invalidate();
        nextUi.SetFocus(focus);
        if (!startRenderer)
        {
            return true;
        }

        nextUi.Start();
        _themeController.RebindTui();
        RebindExtensionTerminalInputListeners();
        if (restoreProgress && SettingsManager.GetShowTerminalProgress() && (Session.IsStreaming || Session.IsCompacting))
        {
            terminal.SetProgress(true);
        }

        return true;
    }

    // ---------------------------------------------------------------- runtime settings (L2040-2080)

    private void ApplyFullscreenScrollbarSetting()
    {
        _transcriptScrollView?.SetScrollbar(SettingsManager.GetFullscreenScrollbar());
    }

    /// <summary>Lets extension images use the PNG transcoder; tool results register it themselves.</summary>
    private void EnsurePngTranscoder()
    {
        ImageConvert.EnsurePngTranscoder(() =>
        {
            _ui.Invalidate();
            _ui.RequestRender();
        });
    }

    internal void ApplyRuntimeSettings()
    {
        TerminalImage.SetCapabilityOverrides(ToTuiCapabilityOverrides(SettingsManager.GetTerminalCapabilityOverrides()));
        EnsurePngTranscoder();
        // TS calls configureHttpDispatcher(getHttpIdleTimeoutMs()) here, which reconfigures the
        // process-wide undici dispatcher. The .NET port has no global dispatcher: request timeouts
        // are resolved per request from the same setting (Sdk.ResolveRequestTimeouts), so nothing
        // to apply (design difference C267).
        ApplyFullscreenScrollbarSetting();
        if (_renderer is TuiAltScreen altScreen)
        {
            altScreen.SetCopyOnSelect(SettingsManager.GetFullscreenCopyOnSelect());
            altScreen.SetWheelScrollLines(SettingsManager.GetFullscreenWheelScrollLines());
        }

        _footer.SetSession(Session);
        _footer.SetAutoCompactEnabled(Session.AutoCompactionEnabled);
        _footerDataProvider.SetCwd(SessionManager.GetCwd());
        _hideThinkingBlock = SettingsManager.GetHideThinkingBlock();
        _outputPad = (int)SettingsManager.GetOutputPad();
        _ui.SetShowHardwareCursor(SettingsManager.GetShowHardwareCursor());
        var clearOnShrink = SettingsManager.GetClearOnShrink();
        _ui.SetClearOnShrink(clearOnShrink);
        if (!clearOnShrink && _activeStatusIndicator is null)
        {
            _statusContainer.Clear();
        }

        var editorPaddingX = SettingsManager.GetEditorPaddingX();
        var autocompleteMaxVisible = SettingsManager.GetAutocompleteMaxVisible();
        _defaultEditor.SetPaddingX(editorPaddingX);
        _defaultEditor.SetAutocompleteMaxVisible(autocompleteMaxVisible);
        if (!ReferenceEquals(_editor, _defaultEditor))
        {
            // TS uses optional calls (setPaddingX?.) for the duck-typed editor; the C# Editor base
            // always carries both, so they run unconditionally.
            _editor.SetPaddingX(editorPaddingX);
            _editor.SetAutocompleteMaxVisible(autocompleteMaxVisible);
        }
    }

    // ---------------------------------------------------------------- event subscription (L3380-3384)

    private void SubscribeToAgent()
    {
        _unsubscribe?.Dispose();
        _unsubscribe = Session.Subscribe(@event => _ = HandleEventAsync(@event));
    }

    // ---------------------------------------------------------------- provider count (L5231-5238)

    private void UpdateAvailableProviderCount()
    {
        var models = Session.ScopedModels.Count > 0
            ? Session.ScopedModels.Select(scoped => scoped.Model).ToList()
            : Session.ModelRuntime.GetAvailableSnapshot().ToList();
        var uniqueProviders = new HashSet<string>(models.Select(model => model.Provider));
        _footerDataProvider.SetAvailableProviderCount(uniqueProviders.Count);
    }

    // ---------------------------------------------------------------- rebind (L2082-2107)

    private async Task RebindCurrentSessionAsync(bool renderBeforeBind = false)
    {
        var session = Session;

        _unsubscribe?.Dispose();
        _unsubscribe = null;
        _programStatus.Reset();
        ApplyRuntimeSettings();

        if (renderBeforeBind)
        {
            RenderCurrentSessionState();
            SubscribeToAgent();
        }

        await BindCurrentSessionExtensionsAsync().ConfigureAwait(true);

        if (!ReferenceEquals(Session, session))
        {
            return;
        }

        if (!renderBeforeBind)
        {
            SubscribeToAgent();
        }

        UpdateAvailableProviderCount();
        UpdateEditorBorderColor();
        UpdateTerminalTitle();
    }

    // ---------------------------------------------------------------- status indicators (L2291-2384)

    private void SetExtensionStatus(string key, string? text)
    {
        _footerDataProvider.SetExtensionStatus(key, text);
        _ui.RequestRender();
    }

    private bool SetEditorWorkingStatusIndicator(StatusIndicator? indicator)
    {
        _defaultEditor.SetWorkingStatusIndicator(null);
        if (!InteractiveModeStatus.IsWorkingStatusEditor(_editor))
        {
            return false;
        }

        ((CustomEditor)_editor).SetWorkingStatusIndicator(indicator);
        return true;
    }

    internal void ShowStatusIndicator(StatusIndicator indicator)
    {
        _activeStatusIndicator?.Dispose();
        _activeStatusIndicator = indicator;
        _activeWorkingIndicatorEmbedded = false;
        _statusContainer.Clear();
        SetEditorWorkingStatusIndicator(null);
        if (SetEditorWorkingStatusIndicator(indicator))
        {
            _activeWorkingIndicatorEmbedded = true;
            return;
        }

        _statusContainer.AddChild(indicator);
    }

    internal void ClearStatusIndicator(StatusIndicatorKind? kind = null)
    {
        if (kind is { } kindValue && (_activeStatusIndicator is null || _activeStatusIndicator.Kind != kindValue))
        {
            return;
        }

        var clearedIndicator = _activeStatusIndicator;
        var clearedIndicatorWasEmbedded = _activeWorkingIndicatorEmbedded;
        clearedIndicator?.Dispose();
        _activeStatusIndicator = null;
        _activeWorkingIndicatorEmbedded = false;
        _statusContainer.Clear();
        SetEditorWorkingStatusIndicator(null);
        if (InteractiveModeStatus.ShouldRestoreIdleStatus(
                clearedIndicator is not null,
                clearedIndicatorWasEmbedded,
                _options.TuiMode == TuiMode.Regular,
                _ui.GetClearOnShrink()))
        {
            _statusContainer.AddChild(_idleStatus);
        }
    }

    private void ShowWorkingStatusIndicator()
    {
        var colorFn = InteractiveModeStatus.ResolveWorkingStatusColor(_editor, ThemeRuntime.Current, Session.ThinkingLevel);
        ShowStatusIndicator(new WorkingStatusIndicator(
            _ui,
            InteractiveModeStatus.ResolveWorkingMessage(_workingMessage, DefaultWorkingMessage),
            _workingIndicatorOptions,
            colorFn));
    }

    internal void SetWorkingVisible(bool visible)
    {
        _workingVisible = visible;
        if (!visible)
        {
            ClearStatusIndicator(StatusIndicatorKind.Working);
            _ui.RequestRender();
            return;
        }

        if (Session.IsStreaming && _activeStatusIndicator?.Kind != StatusIndicatorKind.Working)
        {
            ShowWorkingStatusIndicator();
        }

        _ui.RequestRender();
    }

    internal void SetWorkingIndicator(WorkingIndicatorOptions? options = null)
    {
        _workingIndicatorOptions = options;
        if (_activeStatusIndicator is { Kind: StatusIndicatorKind.Working })
        {
            _activeStatusIndicator.SetIndicator(StatusIndicator.ToLoaderIndicator(options));
        }

        _ui.RequestRender();
    }

    internal void SetHiddenThinkingLabel(string? label = null)
    {
        _hiddenThinkingLabel = InteractiveModeStatus.ResolveHiddenThinkingLabel(label, DefaultHiddenThinkingLabel);
        foreach (var child in _chatContainer.GetChildrenSnapshot())
        {
            if (child is AssistantMessageComponent messageComponent)
            {
                messageComponent.SetHiddenThinkingLabel(_hiddenThinkingLabel);
            }
        }

        _streamingComponent?.SetHiddenThinkingLabel(_hiddenThinkingLabel);
        _ui.RequestRender();
    }

    // ---------------------------------------------------------------- extension widgets (L2389-2441, L2482-2510)

    internal void SetExtensionWidget(string key, IReadOnlyList<string>? content, ExtensionWidgetOptions? options = null) =>
        SetExtensionWidgetCore(key, content, null, options);

    internal void SetExtensionWidget(string key, Func<ITui, Theme, IComponent>? factory, ExtensionWidgetOptions? options = null) =>
        SetExtensionWidgetCore(key, null, factory, options);

    /// <summary>
    /// TS <c>setExtensionWidget(key, content, options)</c> with the content union split across the
    /// two overloads above (string array vs component factory).
    /// </summary>
    private void SetExtensionWidgetCore(
        string key,
        IReadOnlyList<string>? lines,
        Func<ITui, Theme, IComponent>? factory,
        ExtensionWidgetOptions? options)
    {
        var placement = options?.Placement ?? WidgetPlacement.Default;
        RemoveExistingWidget(_extensionWidgetsAbove, key);
        RemoveExistingWidget(_extensionWidgetsBelow, key);

        if (lines is null && factory is null)
        {
            RenderWidgets();
            return;
        }

        IComponent component;
        if (lines is not null)
        {
            // Wrap string array in a Container with Text components
            var container = new Container();
            var (widgetLines, truncated) = InteractiveModeExtensions.WidgetContent(lines);
            foreach (var line in widgetLines)
            {
                container.AddChild(new Text(line, 1, 0));
            }

            if (truncated)
            {
                container.AddChild(new ThemedText(
                    () => ThemeRuntime.Current.Fg("muted", InteractiveModeExtensions.WidgetTruncationNote), 1, 0));
            }

            component = container;
        }
        else
        {
            // Factory function - create component
            component = factory!(_ui, ThemeRuntime.Current);
        }

        var targetMap = placement == WidgetPlacement.BelowEditor ? _extensionWidgetsBelow : _extensionWidgetsAbove;
        targetMap[key] = component;
        RenderWidgets();
    }

    internal void ClearExtensionWidgets()
    {
        foreach (var widget in _extensionWidgetsAbove.Values)
        {
            DisposeWidget(widget);
        }

        foreach (var widget in _extensionWidgetsBelow.Values)
        {
            DisposeWidget(widget);
        }

        _extensionWidgetsAbove.Clear();
        _extensionWidgetsBelow.Clear();
        RenderWidgets();
    }

    /// <summary>TS <c>widget.dispose?.()</c>: dispose is optional on TS components, IDisposable in C#.</summary>
    private static void DisposeWidget(IComponent component)
    {
        if (component is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static void RemoveExistingWidget(Dictionary<string, IComponent> map, string key)
    {
        if (map.Remove(key, out var existing))
        {
            DisposeWidget(existing);
        }
    }

    /// <summary>Render all extension widgets to the widget container.</summary>
    private void RenderWidgets()
    {
        if (_widgetContainerAbove is null || _widgetContainerBelow is null)
        {
            return;
        }

        RenderWidgetContainer(_widgetContainerAbove, _extensionWidgetsAbove, spacerWhenEmpty: true, leadingSpacer: true);
        RenderWidgetContainer(_widgetContainerBelow, _extensionWidgetsBelow, spacerWhenEmpty: false, leadingSpacer: false);
        _ui.RequestRender();
    }

    private void RenderWidgetContainer(
        Container container,
        Dictionary<string, IComponent> widgets,
        bool spacerWhenEmpty,
        bool leadingSpacer)
    {
        container.Clear();

        if (widgets.Count == 0)
        {
            if (spacerWhenEmpty)
            {
                container.AddChild(new Spacer(1));
            }

            return;
        }

        if (leadingSpacer)
        {
            container.AddChild(new Spacer(1));
        }

        foreach (var component in widgets.Values)
        {
            container.AddChild(component);
        }
    }

    // ---------------------------------------------------------------- footer / header swap (L2515-2581)

    internal void SetExtensionFooter(Func<ITui, Theme, IReadonlyFooterDataProvider, IComponent>? factory)
    {
        // Dispose existing custom footer
        if (_customFooter is not null)
        {
            DisposeWidget(_customFooter);
        }

        _footerContainer.Clear();
        if (factory is not null)
        {
            // Create and add custom footer, passing the data provider
            _customFooter = factory(_ui, ThemeRuntime.Current, _footerDataProvider);
            _footerContainer.AddChild(_customFooter);
        }
        else
        {
            // Restore built-in footer
            _customFooter = null;
            _footerContainer.AddChild(_footer);
        }

        _ui.RequestRender();
    }

    /// <summary>Set a custom header component, or restore the built-in header.</summary>
    internal void SetExtensionHeader(Func<ITui, Theme, IComponent>? factory)
    {
        // Header may not be initialized yet if called during early initialization
        if (_builtInHeader is null)
        {
            return;
        }

        // Dispose existing custom header
        if (_customHeader is not null)
        {
            DisposeWidget(_customHeader);
        }

        // Find the index of the current header in the header container
        var currentHeader = _customHeader ?? _builtInHeader;
        if (factory is not null)
        {
            // Create and add custom header
            _customHeader = factory(_ui, ThemeRuntime.Current);
            if (_customHeader is IExpandable expandableCustom)
            {
                expandableCustom.SetExpanded(_toolOutputExpanded);
            }

            if (!_headerContainer.ReplaceChild(currentHeader, _customHeader))
            {
                _headerContainer.InsertChild(0, _customHeader);
            }
        }
        else
        {
            // Restore built-in header
            _customHeader = null;
            if (_builtInHeader is IExpandable expandableBuiltIn)
            {
                expandableBuiltIn.SetExpanded(_toolOutputExpanded);
            }

            _headerContainer.ReplaceChild(currentHeader, _builtInHeader);
        }

        _ui.RequestRender();
    }

    // ---------------------------------------------------------------- terminal input listeners (L2583-2604)

    private Action AddExtensionTerminalInputListener(TerminalInputHandler handler)
    {
        var listener = ToTuiInputListener(handler);
        var subscription = new TerminalInputSubscription(handler, listener, _ui.AddInputListener(listener));
        _extensionTerminalInputSubscriptions.Add(subscription);
        return () =>
        {
            subscription.Unsubscribe();
            _extensionTerminalInputSubscriptions.Remove(subscription);
        };
    }

    private void RebindExtensionTerminalInputListeners()
    {
        foreach (var subscription in _extensionTerminalInputSubscriptions)
        {
            subscription.Unsubscribe();
            subscription.Unsubscribe = _ui.AddInputListener(subscription.Listener);
        }
    }

    private void ClearExtensionTerminalInputListeners()
    {
        foreach (var subscription in _extensionTerminalInputSubscriptions)
        {
            subscription.Unsubscribe();
        }

        _extensionTerminalInputSubscriptions.Clear();
    }

    // ---------------------------------------------------------------- extension dialogs (L2681-2854)

    internal async Task<string?> ShowExtensionSelectorAsync(string title, IReadOnlyList<string> options, ExtensionUIDialogOptions? opts = null)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (opts?.Signal is { IsCancellationRequested: true })
        {
            completion.TrySetResult(null);
            return await completion.Task.ConfigureAwait(true);
        }

        CancellationTokenRegistration abortRegistration = default;
        if (opts?.Signal is { } signal)
        {
            abortRegistration = signal.Register(() =>
            {
                HideExtensionSelector();
                completion.TrySetResult(null);
            });
        }

        _extensionSelector = new ExtensionSelectorComponent(
            title,
            options,
            option =>
            {
                abortRegistration.Dispose();
                HideExtensionSelector();
                completion.TrySetResult(option);
            },
            () =>
            {
                abortRegistration.Dispose();
                HideExtensionSelector();
                completion.TrySetResult(null);
            },
            new ExtensionSelectorOptions
            {
                Tui = _ui,
                Timeout = opts?.TimeoutMs,
                OnToggleToolsExpanded = ToggleToolOutputExpansion,
            });

        DisposeActiveSelector();
        _editorContainer.Clear();
        _editorContainer.AddChild(_extensionSelector);
        _ui.SetFocus(_extensionSelector);
        _ui.RequestRender();

        // The visible dialog blocks the program: terminals that support OSC 7501 show that state.
        var blockedKey = NextExtensionDialogKey();
        _programStatus.SetBlocked(blockedKey, new BlockedStatus(ProgramStatusKind.Question, title));
        try
        {
            return await completion.Task.ConfigureAwait(true);
        }
        finally
        {
            _programStatus.SetBlocked(blockedKey, null);
        }
    }

    private int _extensionDialogSequence;

    private string NextExtensionDialogKey() =>
        $"extension-dialog:{Interlocked.Increment(ref _extensionDialogSequence)}";

    /// <summary>Hide the extension selector.</summary>
    internal void HideExtensionSelector()
    {
        DisposeWidget(_extensionSelector!);
        _editorContainer.Clear();
        _editorContainer.AddChild(_editor);
        _extensionSelector = null;
        _ui.SetFocus(_editor);
        _ui.RequestRender();
    }

    /// <summary>Show a confirmation dialog for extensions.</summary>
    internal async Task<bool> ShowExtensionConfirmAsync(string title, string message, ExtensionUIDialogOptions? opts = null)
    {
        var result = await ShowExtensionSelectorAsync(
            InteractiveModeExtensions.ConfirmTitle(title, message),
            InteractiveModeExtensions.ConfirmOptions,
            opts).ConfigureAwait(true);
        return InteractiveModeExtensions.IsConfirmed(result);
    }

    internal async Task<string?> ShowExtensionInputAsync(string title, string? placeholder = null, ExtensionUIDialogOptions? opts = null)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (opts?.Signal is { IsCancellationRequested: true })
        {
            completion.TrySetResult(null);
            return await completion.Task.ConfigureAwait(true);
        }

        CancellationTokenRegistration abortRegistration = default;
        if (opts?.Signal is { } signal)
        {
            abortRegistration = signal.Register(() =>
            {
                HideExtensionInput();
                completion.TrySetResult(null);
            });
        }

        _extensionInput = new ExtensionInputComponent(
            title,
            placeholder,
            value =>
            {
                abortRegistration.Dispose();
                HideExtensionInput();
                completion.TrySetResult(value);
            },
            () =>
            {
                abortRegistration.Dispose();
                HideExtensionInput();
                completion.TrySetResult(null);
            },
            new ExtensionInputOptions
            {
                Tui = _ui,
                Timeout = opts?.TimeoutMs,
            });

        DisposeActiveSelector();
        _editorContainer.Clear();
        _editorContainer.AddChild(_extensionInput);
        _ui.SetFocus(_extensionInput);
        _ui.RequestRender();

        var blockedKey = NextExtensionDialogKey();
        _programStatus.SetBlocked(blockedKey, new BlockedStatus(ProgramStatusKind.Question, title));
        try
        {
            return await completion.Task.ConfigureAwait(true);
        }
        finally
        {
            _programStatus.SetBlocked(blockedKey, null);
        }
    }

    /// <summary>Hide the extension input.</summary>
    private void HideExtensionInput()
    {
        DisposeWidget(_extensionInput!);
        _editorContainer.Clear();
        _editorContainer.AddChild(_editor);
        _extensionInput = null;
        _ui.SetFocus(_editor);
        _ui.RequestRender();
    }

    /// <summary>Show a multi-line editor for extensions (with Ctrl+G support).</summary>
    internal async Task<string?> ShowExtensionEditorAsync(string title, string? prefill = null)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _extensionEditor = new ExtensionEditorComponent(
            _ui,
            _keybindings,
            title,
            prefill,
            value =>
            {
                HideExtensionEditor();
                completion.TrySetResult(value);
            },
            () =>
            {
                HideExtensionEditor();
                completion.TrySetResult(null);
            },
            null,
            SettingsManager.GetExternalEditorCommand());

        DisposeActiveSelector();
        _editorContainer.Clear();
        _editorContainer.AddChild(_extensionEditor);
        _ui.SetFocus(_extensionEditor);
        _ui.RequestRender();

        var blockedKey = NextExtensionDialogKey();
        _programStatus.SetBlocked(blockedKey, new BlockedStatus(ProgramStatusKind.Question, title));
        try
        {
            return await completion.Task.ConfigureAwait(true);
        }
        finally
        {
            _programStatus.SetBlocked(blockedKey, null);
        }
    }

    /// <summary>Hide the extension editor.</summary>
    private void HideExtensionEditor()
    {
        _editorContainer.Clear();
        _editorContainer.AddChild(_editor);
        _extensionEditor = null;
        _ui.SetFocus(_editor);
        _ui.RequestRender();
    }

    /// <summary>Show a notification for extensions.</summary>
    internal void ShowExtensionNotify(string message, string? type = null)
    {
        if (type == "error")
        {
            ShowError(message);
        }
        else if (type == "warning")
        {
            ShowWarning(message);
        }
        else
        {
            ShowStatus(message);
        }
    }

    /// <summary>
    /// TS <c>showExtensionCustom</c> (L2946-3010)：挂载扩展自产组件（同步或异步工厂），
    /// 完成后归还编辑器。overlay 分支走 TUI 的覆盖层，否则占位编辑器容器。
    /// </summary>
    /// <remarks>
    /// TS 的工厂返回 <c>Component | Promise&lt;Component&gt;</c> 联合，端口按工厂形态拆成
    /// <see cref="ShowExtensionCustomAsync{T}(Func{ITui, Theme, Core.KeybindingsManager, Action{T}, Task{IComponent}}, ExtensionCustomOptions?)"/>
    /// 与同步半支（<c>IExtensionUIContext.Custom</c> 经 Task.FromResult 复用同一实现）。
    /// </remarks>
    internal async Task<T> ShowExtensionCustomAsync<T>(
        Func<ITui, Theme, Core.KeybindingsManager, Action<T>, Task<IComponent>> factory,
        ExtensionCustomOptions? options = null)
    {
        var savedText = _editor.GetText();
        var isOverlay = options?.Overlay ?? false;

        void RestoreEditor()
        {
            _editorContainer.Clear();
            _editorContainer.AddChild(_editor);
            _editor.SetText(savedText);
            _ui.SetFocus(_editor);
            _ui.RequestRender();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = false;
        IComponent? component = null;

        void Close(T result)
        {
            if (closed)
            {
                return;
            }

            closed = true;
            if (isOverlay)
            {
                _ui.HideOverlay();
            }
            else
            {
                RestoreEditor();
            }

            // Note: both branches above already call requestRender
            completion.TrySetResult(result);
            try
            {
                switch (component)
                {
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch
            {
                // ignore dispose errors
            }
        }

        try
        {
            component = await factory(_ui, ThemeRuntime.Current, _keybindings, Close).ConfigureAwait(true);
            if (closed)
            {
                return await completion.Task.ConfigureAwait(true);
            }

            if (isOverlay)
            {
                // Resolve overlay options - TS allows a static value or a factory for dynamic updates.
                var overlayOptions = options?.OverlayOptions;
                var handle = _ui.ShowOverlay(component, overlayOptions);
                // 把 TUI 的真实句柄交给扩展；overlay 关闭后该句柄的后续调用为空操作。
                options?.OnHandle?.Invoke(new OverlayHandle(handle));
            }
            else
            {
                DisposeActiveSelector();
                _editorContainer.Clear();
                _editorContainer.AddChild(component);
                _ui.SetFocus(component);
                _ui.RequestRender();
            }
        }
        catch (Exception)
        {
            if (closed)
            {
                return await completion.Task.ConfigureAwait(true);
            }

            if (!isOverlay)
            {
                RestoreEditor();
            }

            throw;
        }

        return await completion.Task.ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- custom editor (L2855-2930)

    /// <summary>
    /// Set a custom editor component from an extension. Pass <see langword="null"/> to restore the
    /// default editor.
    /// </summary>
    internal void SetCustomEditorComponent(EditorFactory? factory)
    {
        _editorComponentFactory = factory;

        // Save text from current editor before switching
        var currentText = _editor.GetText();

        DisposeActiveSelector();
        _editorContainer.Clear();

        if (factory is not null)
        {
            // Create the custom editor with tui, theme, and keybindings
            var newEditor = (Editor)factory(_ui, ThemeHelpers.GetEditorTheme(), _keybindings);

            // Wire up callbacks from the default editor
            newEditor.OnSubmit = _defaultEditor.OnSubmit;
            newEditor.OnChange = _defaultEditor.OnChange;

            // Copy text from previous editor
            newEditor.SetText(currentText);

            // Copy appearance settings if supported. TS guards each call with an undefined check;
            // the C# Editor API is non-optional, so the copies run unconditionally.
            newEditor.BorderColor = _defaultEditor.BorderColor;
            newEditor.SetPaddingX(_defaultEditor.GetPaddingX());
            newEditor.SetAutocompleteMaxVisible(_defaultEditor.GetAutocompleteMaxVisible());

            // Set autocomplete if supported
            if (_autocompleteProvider is not null)
            {
                newEditor.SetAutocompleteProvider(_autocompleteProvider);
            }

            // If extending CustomEditor, copy app-level handlers. TS duck-types this via the
            // `actionHandlers` member; in C# only CustomEditor carries those members.
            if (newEditor is CustomEditor customEditor)
            {
                customEditor.OnEscape ??= _defaultEditor.OnEscape;
                customEditor.OnCtrlD ??= _defaultEditor.OnCtrlD;
                customEditor.OnPasteImage ??= _defaultEditor.OnPasteImage;
                customEditor.OnExtensionShortcut ??= data => _defaultEditor.OnExtensionShortcut?.Invoke(data) ?? false;
                // Copy action handlers (clear, suspend, model switching, etc.)
                foreach (var (action, handler) in _defaultEditor.ActionHandlers)
                {
                    customEditor.ActionHandlers[action] = handler;
                }
            }

            _editor = newEditor;
        }
        else
        {
            // Restore default editor with text from custom editor
            _defaultEditor.SetText(currentText);
            _editor = _defaultEditor;
        }

        _editorContainer.AddChild(_editor);
        if (_activeStatusIndicator is not null)
        {
            _statusContainer.Clear();
            _activeWorkingIndicatorEmbedded = SetEditorWorkingStatusIndicator(_activeStatusIndicator);
            if (!_activeWorkingIndicatorEmbedded)
            {
                _statusContainer.AddChild(_activeStatusIndicator);
            }
        }

        _ui.SetFocus(_editor);
        _ui.RequestRender();
    }

    // ---------------------------------------------------------------- selector plumbing (L4852-4886)

    internal void DisposeActiveSelector()
    {
        var dispose = _activeSelectorDispose;
        _activeSelectorToken = null;
        _activeSelectorDispose = null;
        dispose?.Invoke();
    }

    /// <summary>
    /// Shows a selector component in place of the editor. <paramref name="create"/> receives a
    /// <c>done</c> callback and returns the component, the focus target and an optional dispose hook.
    /// </summary>
    private void ShowSelector(Func<Action, (IComponent Component, IComponent Focus, Action? Dispose)> create)
    {
        var token = new object();
        Action? dispose = null;
        void Done()
        {
            dispose?.Invoke();
            if (!ReferenceEquals(_activeSelectorToken, token))
            {
                return;
            }

            _activeSelectorToken = null;
            _activeSelectorDispose = null;
            _editorContainer.Clear();
            _editorContainer.AddChild(_editor);
            _ui.SetFocus(_editor);
        }

        var created = create(Done);
        dispose = created.Dispose;
        DisposeActiveSelector();
        _activeSelectorToken = token;
        _activeSelectorDispose = dispose;
        _editorContainer.Clear();
        _editorContainer.AddChild(created.Component);
        _ui.SetFocus(created.Focus);
        _ui.RequestRender();
    }

    // ---------------------------------------------------------------- reset (L2443-2474)

    private void ResetExtensionUI()
    {
        if (_extensionSelector is not null)
        {
            HideExtensionSelector();
        }

        if (_extensionInput is not null)
        {
            HideExtensionInput();
        }

        if (_extensionEditor is not null)
        {
            HideExtensionEditor();
        }

        _ui.HideOverlay();
        ClearExtensionTerminalInputListeners();
        SetExtensionFooter(null);
        SetExtensionHeader(null);
        ClearExtensionWidgets();
        _footerDataProvider.ClearExtensionStatuses();
        _footer.Invalidate();
        _autocompleteProviderWrappers.Clear();
        SetCustomEditorComponent(null);
        SetupAutocompleteProvider();
        _defaultEditor.OnExtensionShortcut = null;
        UpdateTerminalTitle();
        _workingMessage = null;
        _workingVisible = true;
        SetWorkingIndicator();
        if (_activeStatusIndicator is { Kind: StatusIndicatorKind.Working })
        {
            _activeStatusIndicator.SetMessage(InteractiveModeStatus.DefaultWorkingMessageWithInterrupt(DefaultWorkingMessage));
        }

        SetHiddenThinkingLabel();
    }

    // ---------------------------------------------------------------- view state leaves (L4536-4600)

    internal void ToggleToolOutputExpansion()
    {
        SetToolsExpanded(!_toolOutputExpanded);
    }

    internal void SetToolsExpanded(bool expanded)
    {
        if (expanded == _toolOutputExpanded)
        {
            return;
        }

        _toolOutputExpanded = expanded;
        var activeHeader = _customHeader ?? _builtInHeader;
        if (activeHeader is IExpandable expandableHeader)
        {
            expandableHeader.SetExpanded(expanded);
        }

        foreach (var container in new[] { _loadedResourcesContainer, _chatContainer })
        {
            foreach (var child in container.GetChildrenSnapshot())
            {
                if (child is IExpandable expandable)
                {
                    expandable.SetExpanded(expanded);
                }
            }
        }

        ShowStatus($"Tool output: {(expanded ? "expanded" : "collapsed")}");
    }

    /// <summary>Update rendered assistant messages without rebuilding live tool components.</summary>
    private void UpdateThinkingBlockVisibility()
    {
        foreach (var child in _chatContainer.GetChildrenSnapshot())
        {
            if (child is AssistantMessageComponent messageComponent)
            {
                messageComponent.SetHideThinkingBlock(_hideThinkingBlock);
            }
        }

        _ui.RequestRender();
    }

    private void ToggleThinkingBlockVisibility()
    {
        _hideThinkingBlock = !_hideThinkingBlock;
        SettingsManager.SetHideThinkingBlock(_hideThinkingBlock);
        UpdateThinkingBlockVisibility();
        ShowStatus($"Thinking blocks: {(_hideThinkingBlock ? "hidden" : "visible")}");
    }

    public void ClearEditor()
    {
        _editor.SetText("");
        _ui.RequestRender();
    }
}
