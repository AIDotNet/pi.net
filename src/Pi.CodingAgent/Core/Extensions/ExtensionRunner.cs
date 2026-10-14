// ============================================================================
// Extension runner — port of core/extensions/runner.ts (4e-3e-9g)
// ============================================================================
//
// The runner is the bridge between loaded extensions and the host: the host mode pushes its action
// implementations in (`BindCore` / `BindCommandContext` / `SetUIContext`), and the session pulls
// dispatched events back out (`EmitAsync` and the dedicated `EmitXxxAsync` methods).
//
// TS closures vs C# delegates: every TS private field that holds a callback (`getModel`,
// `isIdleFn`, …) becomes a `Func`/`Action` field with the same default, so the runner is usable
// before a host binds — exactly like TS, where the fields are initialized to inert defaults.
//
// TS's lazy getters on the context objects (`get ui() { … }`) become properties on the nested
// context classes that call `AssertActive()` on every read, preserving the stale-instance guard.

using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Core.Extensions;

/// <summary>Combined result from all <c>before_agent_start</c> handlers. Port of the TS interface.</summary>
public sealed record BeforeAgentStartCombinedResult(
    IReadOnlyList<CustomMessageDraft> Messages,
    NormalizedBuildSystemPromptOptions SystemPromptOptions);

/// <summary>Result of <see cref="ExtensionRunner.EmitBoundaryAsync"/>. Port of the TS interface.</summary>
public sealed record BoundaryDispatchResult(
    IReadOnlyList<SessionBoundaryDraft> Entries,
    bool Continue,
    BoundaryContextPreview Context,
    bool Valid);

/// <summary>One discovered resource plus the extension that contributed it. TS inline record.</summary>
public sealed record AttributedResourcePath(string Path, string ExtensionPath);

/// <summary>Resources reported by <c>resources_discover</c> handlers. Port of the TS inline return.</summary>
public sealed record DiscoveredResourcePaths(
    IReadOnlyList<AttributedResourcePath> SkillPaths,
    IReadOnlyList<AttributedResourcePath> PromptPaths,
    IReadOnlyList<AttributedResourcePath> ThemePaths);

/// <summary>
/// Extension runner — executes extensions and manages their lifecycle. Port of the TS
/// <c>ExtensionRunner</c>.
/// </summary>
/// <remarks>
/// <para>
/// Difference C119: TS keys dispatch on the literal <c>event.type</c> discriminant and types
/// <c>emit</c> with a conditional return type, so passing an event that has a dedicated
/// <c>emitXxx</c> method is a compile error. C# has no such conditional types: the four
/// <c>session_before_*</c> events get precise overloads, and <see cref="EmitAsync(ExtensionEvent)"/>
/// accepts any event. Passing a <c>tool_call</c>/<c>context</c>/… event there compiles but discards
/// the handler's result; the dedicated methods must be used instead.
/// </para>
/// <para>
/// Difference C120: TS's <c>queueMicrotask(() =&gt; void this.emit(event))</c> for UI prompt events
/// defers dispatch by one microtask. <see cref="ExtensionRunner"/> uses a thread-pool task, which
/// defers at least as much; dispatch therefore never runs before the UI call it brackets.
/// </para>
/// </remarks>
public sealed class ExtensionRunner
{
    // Extension shortcuts compete with canonical keybinding ids from keybindings.json.
    // Only editor-global shortcuts are reserved here. Picker-specific bindings are not.
    private static readonly string[] ReservedKeybindingsForExtensionConflicts =
    [
        "app.interrupt",
        "app.clear",
        "app.exit",
        "app.suspend",
        "app.thinking.cycle",
        "app.model.cycleForward",
        "app.model.cycleBackward",
        "app.model.select",
        "app.tools.expand",
        "app.thinking.toggle",
        "app.editor.external",
        "app.message.copy",
        "app.message.followUp",
        "tui.input.submit",
        "tui.select.confirm",
        "tui.select.cancel",
        "tui.input.copy",
        "tui.editor.deleteToLineEnd",
    ];

    /// <summary>Default stale-ctx message. TS <c>invalidate()</c>'s default parameter.</summary>
    public const string StaleMessage = ExtensionLoader.StaleMessage;

    /// <summary>TS <c>isUserBashEventResult</c>'s rejection text, reused verbatim.</summary>
    private const string InvalidUserBashResultMessage =
        "Invalid user_bash handler result: return undefined for local execution or exactly one valid { operations } or { result } object";

    private readonly IReadOnlyList<Extension> extensions;
    private readonly IExtensionRuntime runtime;
    private readonly string cwd;
    private readonly IReadonlySessionManager sessionManager;
    private readonly ModelRegistry modelRegistry;
    private readonly List<ExtensionErrorListener> errorListeners = [];
    private readonly HashSet<string> reportedMcpServers = new(StringComparer.Ordinal);
    private readonly Func<BuildSystemPromptOptions> defaultSystemPromptOptionsFn;

    private IExtensionUIContext uiContext = NoOpUIContext.Instance;
    private string mode = ExtensionMode.Print;
    private Func<ModelSpec?> getModel = () => null;
    private Func<IReadOnlyList<ScopedModel>> getScopedModels = () => [];
    private Func<bool> isIdleFn = () => true;
    private Func<bool> isProjectTrustedFn = () => true;
    private Func<CancellationToken?> getSignalFn = () => null;
    private Func<Task> waitForIdleFn = () => Task.CompletedTask;
    private Action abortFn = () => { };
    private Func<bool> hasPendingMessagesFn = () => false;
    private Func<ContextUsage?> getContextUsageFn = () => null;
    private Action<CompactOptions?> compactFn = _ => { };
    private Func<string> getSystemPromptFn = () => "";
    private Func<BuildSystemPromptOptions> getSystemPromptOptionsFn;
    private Func<string, string, object?, ExecuteToolOptions, Task<AgentToolCallOutcome>>? executeToolFn;
    private Func<IReadOnlyList<AgentTool>> getCallableToolsFn = () => [];
    private NewSessionHandler newSessionHandler = _ => Task.FromResult(false);
    private ForkHandler forkHandler = (_, _) => Task.FromResult(false);
    private NavigateTreeHandler navigateTreeHandler = (_, _) => Task.FromResult(false);
    private SwitchSessionHandler switchSessionHandler = (_, _) => Task.FromResult(false);
    private ReloadHandler reloadHandler = () => Task.CompletedTask;
    private ShutdownHandler shutdownHandler = () => { };
    private IReadOnlyList<ResourceDiagnostic> shortcutDiagnostics = [];
    private IReadOnlyList<ResourceDiagnostic> commandDiagnostics = [];
    private string? staleMessage;
    private int uiPromptDepth;
    private (string Kind, string? Title)? activeUIPrompt;

    public ExtensionRunner(
        IReadOnlyList<Extension> extensions,
        IExtensionRuntime runtime,
        string cwd,
        IReadonlySessionManager sessionManager,
        ModelRegistry modelRegistry)
    {
        this.extensions = extensions;
        this.runtime = runtime;
        this.cwd = cwd;
        this.sessionManager = sessionManager;
        this.modelRegistry = modelRegistry;
        defaultSystemPromptOptionsFn = () => DefaultSystemPromptOptions(this.cwd);
        getSystemPromptOptionsFn = defaultSystemPromptOptionsFn;
    }

    /// <summary>
    /// TS's default <c>getSystemPromptOptions</c>: <c>normalizeBuildSystemPromptOptions({ cwd })</c>.
    /// The result is widened back to the input shape, so a caller sees the same collection defaults
    /// the TS normalizer fills in rather than nulls.
    /// </summary>
    private static BuildSystemPromptOptions DefaultSystemPromptOptions(string cwd) =>
        SystemPrompt.ToBuildSystemPromptOptions(
            SystemPrompt.NormalizeBuildSystemPromptOptions(new BuildSystemPromptOptions { Cwd = cwd }));

    // ======================================================================
    // Binding
    // ======================================================================

    /// <summary>
    /// Copy the host's action implementations into the shared runtime and the runner. Port of the TS
    /// <c>bindCore</c>; the provider queue is flushed here so registrations made during loading take
    /// effect before any event fires.
    /// </summary>
    public void BindCore(
        ExtensionActions actions,
        ExtensionContextActions contextActions,
        ProviderRegistrationActions? providerActions = null)
    {
        // Copy actions into the shared runtime (all extension APIs reference this)
        runtime.BindActions(actions);
        runtime.BindContextFactory(CreateContext);

        // Context actions (required)
        getModel = contextActions.GetModel;
        getScopedModels = contextActions.GetScopedModels;
        isIdleFn = contextActions.IsIdle;
        isProjectTrustedFn = contextActions.IsProjectTrusted;
        getSignalFn = contextActions.GetSignal;
        abortFn = contextActions.Abort;
        hasPendingMessagesFn = contextActions.HasPendingMessages;
        shutdownHandler = () => contextActions.Shutdown();
        getContextUsageFn = contextActions.GetContextUsage;
        compactFn = contextActions.Compact;
        getSystemPromptFn = contextActions.GetSystemPrompt;
        getSystemPromptOptionsFn = contextActions.GetSystemPromptOptions ?? defaultSystemPromptOptionsFn;
        executeToolFn = contextActions.ExecuteTool;
        getCallableToolsFn = contextActions.GetCallableTools ?? (() => []);

        // Servers registered from now on reach the extension that connects them right away. Servers
        // registered during loading are read on session_start.
        runtime.McpServers.SetChangeListener(() =>
        {
            FireAndForget(EmitAsync(new ExtensionEvent.McpServersChange(runtime.McpServers.List())));
            ReportUnhandledMcpServers();
        });

        // The three post-bind registrars fall back to the model registry, so they are resolved once
        // and reused for both the flush below and every later registration.
        void RegisterProvider(string name, ProviderConfigInput config)
        {
            if (providerActions?.RegisterProvider is { } overrideRegister)
            {
                overrideRegister(name, config);
                return;
            }

            modelRegistry.RegisterProvider(name, config);
        }

        void RegisterNativeProvider(IProvider provider)
        {
            if (providerActions?.RegisterNativeProvider is { } overrideRegister)
            {
                overrideRegister(provider);
                return;
            }

            modelRegistry.RegisterProvider(provider);
        }

        void UnregisterProvider(string name)
        {
            if (providerActions?.UnregisterProvider is { } overrideUnregister)
            {
                overrideUnregister(name);
                return;
            }

            modelRegistry.UnregisterProvider(name);
        }

        void RegisterVirtualModel(VirtualModelDefinition definition)
        {
            if (providerActions?.RegisterVirtualModel is { } overrideRegister)
            {
                overrideRegister(definition);
                return;
            }

            modelRegistry.RegisterVirtualModel(definition);
        }

        void UnregisterVirtualModel(string provider, string id)
        {
            if (providerActions?.UnregisterVirtualModel is { } overrideUnregister)
            {
                overrideUnregister(provider, id);
                return;
            }

            modelRegistry.UnregisterVirtualModel(provider, id);
        }

        // Flush provider registrations queued during extension loading
        foreach (var registration in runtime.PendingProviderRegistrations.ToList())
        {
            try
            {
                RegisterProvider(registration.Name, registration.Config);
            }
            catch (Exception error)
            {
                EmitError(new ExtensionError(
                    registration.ExtensionPath, "register_provider", error.Message, error.StackTrace));
            }
        }

        runtime.PendingProviderRegistrations.Clear();

        foreach (var registration in runtime.PendingNativeProviderRegistrations.ToList())
        {
            try
            {
                RegisterNativeProvider(registration.Provider);
            }
            catch (Exception error)
            {
                EmitError(new ExtensionError(
                    registration.ExtensionPath, "register_provider", error.Message, error.StackTrace));
            }
        }

        runtime.PendingNativeProviderRegistrations.Clear();

        foreach (var registration in runtime.PendingVirtualModelRegistrations.ToList())
        {
            try
            {
                RegisterVirtualModel(registration.Definition);
            }
            catch (Exception error)
            {
                EmitError(new ExtensionError(
                    registration.ExtensionPath, "register_virtual_model", error.Message, error.StackTrace));
            }
        }

        runtime.PendingVirtualModelRegistrations.Clear();

        // From this point on, provider registration/unregistration takes effect immediately
        // without requiring a /reload.
        runtime.BindProviderRegistration(
            RegisterProvider, RegisterNativeProvider, UnregisterProvider, RegisterVirtualModel, UnregisterVirtualModel);
    }

    /// <summary>
    /// Bind the session-control handlers available to command handlers. Port of the TS
    /// <c>bindCommandContext</c>; passing null restores the inert defaults.
    /// </summary>
    public void BindCommandContext(ExtensionCommandContextActions? actions = null)
    {
        if (actions is not null)
        {
            waitForIdleFn = actions.WaitForIdle;
            newSessionHandler = (options) => actions.NewSession(options);
            forkHandler = (entryId, options) => actions.Fork(entryId, options);
            navigateTreeHandler = (targetId, options) => actions.NavigateTree(targetId, options);
            switchSessionHandler = (sessionPath, options) => actions.SwitchSession(sessionPath, options);
            reloadHandler = () => actions.Reload();
            return;
        }

        waitForIdleFn = () => Task.CompletedTask;
        newSessionHandler = _ => Task.FromResult(false);
        forkHandler = (_, _) => Task.FromResult(false);
        navigateTreeHandler = (_, _) => Task.FromResult(false);
        switchSessionHandler = (_, _) => Task.FromResult(false);
        reloadHandler = () => Task.CompletedTask;
    }

    /// <summary>
    /// Install the mode's UI context. Passing null restores the no-op context, which is also what
    /// <see cref="HasUI"/> reports on.
    /// </summary>
    public void SetUIContext(IExtensionUIContext? uiContext = null, string mode = ExtensionMode.Print)
    {
        this.uiContext = uiContext is null ? NoOpUIContext.Instance : new UIPromptWrappingContext(uiContext, this);
        this.mode = mode;
    }

    /// <summary>Get the UI context currently bound, or the no-op context.</summary>
    public IExtensionUIContext GetUIContext() => uiContext;

    /// <summary>Whether a dialog-capable UI context is bound.</summary>
    public bool HasUI() => !ReferenceEquals(uiContext, NoOpUIContext.Instance);

    /// <summary>Paths of every loaded extension, in load order.</summary>
    public IReadOnlyList<string> GetExtensionPaths() => [.. extensions.Select(extension => extension.Path)];

    // ======================================================================
    // Registration lookups
    // ======================================================================

    /// <summary>Get all registered tools from all extensions (first registration per name wins).</summary>
    public IReadOnlyList<RegisteredTool> GetAllRegisteredTools()
    {
        var toolsByName = new Dictionary<string, RegisteredTool>(StringComparer.Ordinal);
        foreach (var extension in extensions)
        {
            foreach (var tool in extension.Tools.Values)
            {
                if (!toolsByName.ContainsKey(tool.Definition.Name))
                {
                    toolsByName[tool.Definition.Name] = tool;
                }
            }
        }

        return [.. toolsByName.Values];
    }

    /// <summary>Get a tool definition by name. Returns null if not found.</summary>
    public ToolDefinition? GetToolDefinition(string toolName)
    {
        foreach (var extension in extensions)
        {
            if (extension.Tools.TryGetValue(toolName, out var tool))
            {
                return tool.Definition;
            }
        }

        return null;
    }

    /// <summary>Get all registered flags from all extensions (first registration per name wins).</summary>
    public IReadOnlyDictionary<string, ExtensionFlag> GetFlags()
    {
        var allFlags = new Dictionary<string, ExtensionFlag>(StringComparer.Ordinal);
        foreach (var extension in extensions)
        {
            foreach (var (name, flag) in extension.Flags)
            {
                if (!allFlags.ContainsKey(name))
                {
                    allFlags[name] = flag;
                }
            }
        }

        return allFlags;
    }

    /// <summary>Set a flag value after parsing the CLI arguments.</summary>
    public void SetFlagValue(string name, object value) => runtime.FlagValues[name] = value;

    /// <summary>A copy of the current flag values.</summary>
    public IReadOnlyDictionary<string, object> GetFlagValues() =>
        new Dictionary<string, object>(runtime.FlagValues, StringComparer.Ordinal);

    /// <summary>
    /// Resolve extension shortcuts against the user's keybindings, dropping the ones that would
    /// shadow a reserved built-in. Port of the TS <c>getShortcuts</c>; the diagnostics are collected
    /// on <see cref="GetShortcutDiagnostics"/>.
    /// </summary>
    public IReadOnlyDictionary<string, ExtensionShortcut> GetShortcuts(KeybindingsConfig resolvedKeybindings)
    {
        var diagnostics = new List<ResourceDiagnostic>();
        shortcutDiagnostics = diagnostics;
        var builtinKeybindings = BuildBuiltinKeybindings(resolvedKeybindings);
        var extensionShortcuts = new Dictionary<string, ExtensionShortcut>(StringComparer.Ordinal);

        void AddDiagnostic(string message, string extensionPath)
        {
            diagnostics.Add(new ResourceDiagnostic
            {
                Type = ResourceDiagnosticType.Warning,
                Message = message,
                Path = extensionPath,
            });
            if (!HasUI())
            {
                Console.Error.WriteLine(message);
            }
        }

        foreach (var extension in extensions)
        {
            foreach (var (key, shortcut) in extension.Shortcuts)
            {
                var normalizedKey = key.ToLowerInvariant();

                if (builtinKeybindings.TryGetValue(normalizedKey, out var builtInKeybinding))
                {
                    if (builtInKeybinding.RestrictOverride)
                    {
                        AddDiagnostic(
                            $"Extension shortcut '{key}' from {shortcut.ExtensionPath} conflicts with built-in shortcut. Skipping.",
                            shortcut.ExtensionPath);
                        continue;
                    }

                    AddDiagnostic(
                        $"Extension shortcut conflict: '{key}' is built-in shortcut for {builtInKeybinding.Keybinding} and {shortcut.ExtensionPath}. Using {shortcut.ExtensionPath}.",
                        shortcut.ExtensionPath);
                }

                if (extensionShortcuts.TryGetValue(normalizedKey, out var existingExtensionShortcut))
                {
                    AddDiagnostic(
                        $"Extension shortcut conflict: '{key}' registered by both {existingExtensionShortcut.ExtensionPath} and {shortcut.ExtensionPath}. Using {shortcut.ExtensionPath}.",
                        shortcut.ExtensionPath);
                }

                extensionShortcuts[normalizedKey] = shortcut;
            }
        }

        return extensionShortcuts;
    }

    /// <summary>Diagnostics from the last <see cref="GetShortcuts"/> call.</summary>
    public IReadOnlyList<ResourceDiagnostic> GetShortcutDiagnostics() => shortcutDiagnostics;

    /// <summary>
    /// Mark every captured <c>pi</c> / command context as stale. Idempotent: only the first call
    /// records a message, matching TS.
    /// </summary>
    public void Invalidate(string? message = null)
    {
        if (staleMessage is not null)
        {
            return;
        }

        staleMessage = message ?? StaleMessage;
        runtime.Invalidate(message);
    }

    /// <summary>Register an error listener and return its unsubscribe action.</summary>
    public Action OnError(ExtensionErrorListener listener)
    {
        errorListeners.Add(listener);
        return () => errorListeners.Remove(listener);
    }

    /// <summary>Report an extension error to every listener.</summary>
    public void EmitError(ExtensionError error)
    {
        foreach (var listener in errorListeners.ToArray())
        {
            listener(error);
        }
    }

    /// <summary>
    /// Report registered MCP servers when no extension handles <c>mcp_servers_change</c>, which means
    /// nothing connects them (for example when another MCP extension replaced the built-in one).
    /// </summary>
    public void ReportUnhandledMcpServers()
    {
        if (HasHandlers(ExtensionEventNames.McpServersChange))
        {
            return;
        }

        foreach (var server in runtime.McpServers.List())
        {
            if (!reportedMcpServers.Add(server.Name))
            {
                continue;
            }

            EmitError(new ExtensionError(
                server.ExtensionPath,
                "register_mcp_server",
                $"MCP server \"{server.Name}\" is registered, but no loaded extension connects MCP servers; another extension may have replaced the built-in MCP support"));
        }
    }

    /// <summary>Whether any loaded extension registered a handler for <paramref name="eventType"/>.</summary>
    public bool HasHandlers(string eventType)
    {
        foreach (var extension in extensions)
        {
            if (extension.Handlers.TryGetValue(eventType, out var handlers) && handlers.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Message renderer for a custom entry type (first extension that registered one).</summary>
    public MessageRenderer? GetMessageRenderer(string customType)
    {
        foreach (var extension in extensions)
        {
            if (extension.MessageRenderers.TryGetValue(customType, out var renderer))
            {
                return (MessageRenderer?)renderer;
            }
        }

        return null;
    }

    /// <summary>Markdown transformers of every extension, in load order.</summary>
    public IReadOnlyList<MarkdownTransformer> GetMarkdownTransformers() =>
        [.. extensions.Where(extension => extension.MarkdownTransformer is not null)
            .Select(extension => extension.MarkdownTransformer!)];

    /// <summary>Renderers of calls to <paramref name="toolName"/>: extension resolvers in load order, then <paramref name="base"/>.</summary>
    public ToolRenderers? ResolveToolRenderers(string toolName, Func<ToolRenderers?> @base)
    {
        var resolvers = extensions.SelectMany(extension => extension.ToolRenderers).ToList();

        ToolRenderers? Resolve(int index) =>
            index < resolvers.Count ? resolvers[index](toolName, () => Resolve(index + 1)) : @base();

        return Resolve(0);
    }

    /// <summary>Entry renderer for a custom entry type (first extension that registered one).</summary>
    public EntryRenderer? GetEntryRenderer(string customType)
    {
        foreach (var extension in extensions)
        {
            if (extension.EntryRenderers.TryGetValue(customType, out var renderer))
            {
                return (EntryRenderer?)renderer;
            }
        }

        return null;
    }

    /// <summary>The model registry extensions can read.</summary>
    public ModelRegistry GetModelRegistry() => modelRegistry;

    /// <summary>Every registered command, with its collision-free invocation name.</summary>
    public IReadOnlyList<ResolvedCommand> GetRegisteredCommands()
    {
        commandDiagnostics = [];
        return ResolveRegisteredCommands();
    }

    /// <summary>Diagnostics from the last command resolution. TS keeps this list empty too.</summary>
    public IReadOnlyList<ResourceDiagnostic> GetCommandDiagnostics() => commandDiagnostics;

    /// <summary>Find a registered command by its invocation name.</summary>
    public ResolvedCommand? GetCommand(string name) =>
        ResolveRegisteredCommands().FirstOrDefault(command => command.InvocationName == name);

    /// <summary>
    /// Request a graceful shutdown. Called by extension tools and event handlers; the behavior comes
    /// from the mode via <see cref="BindCore"/>.
    /// </summary>
    public void Shutdown() => shutdownHandler();

    /// <summary>Active tool names, guarded by the stale-ctx check.</summary>
    public IReadOnlyList<string> GetActiveTools()
    {
        AssertActive();
        return runtime.GetActiveTools();
    }

    // ======================================================================
    // Contexts
    // ======================================================================

    /// <summary>
    /// Create an <see cref="IExtensionContext"/> for event handlers and tool execution. Context
    /// values resolve at call time, so changes through <see cref="BindCore"/> /
    /// <see cref="SetUIContext"/> are reflected.
    /// </summary>
    public IExtensionContext CreateContext() => new RunnerContext(this);

    /// <summary>
    /// Create the context for executing the tool call <paramref name="toolCallId"/>: the extension
    /// context plus <see cref="IExtensionToolContext.Tools"/> and
    /// <see cref="IExtensionToolContext.ExecuteTool"/>. <paramref name="signal"/> is the default
    /// signal of nested calls.
    /// </summary>
    public IExtensionToolContext CreateToolContext(string toolCallId, CancellationToken? signal) =>
        new RunnerToolContext(this, toolCallId, signal);

    /// <summary>
    /// Create the context for a command handler: the extension context plus the session-control
    /// methods that are only safe in user-initiated commands.
    /// </summary>
    public IExtensionCommandContext CreateCommandContext() => new RunnerCommandContext(this);

    // ======================================================================
    // Dispatch
    // ======================================================================

    /// <summary>
    /// Run the <c>turn_end</c> / <c>agent_before_settle</c> boundary handlers. Port of the TS
    /// <c>emitBoundary</c>; <paramref name="buildContext"/> is re-run after each handler so the next
    /// one sees the projected result of the drafts so far.
    /// </summary>
    public async Task<BoundaryDispatchResult> EmitBoundaryAsync(
        BoundaryState baseEvent,
        Func<IReadOnlyList<SessionBoundaryDraft>, Task<BoundaryContextPreview>> buildContext)
    {
        var ctx = CreateContext();
        IReadOnlyList<SessionBoundaryDraft> entries = [];
        var shouldContinue = false;
        var context = await buildContext(entries);
        var valid = true;
        var eventName = ExtensionEventNames.For(baseEvent);

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensions, eventName))
        {
            foreach (var handler in handlers)
            {
                var @event = baseEvent switch
                {
                    ExtensionEvent.TurnEnd turnEnd => turnEnd with
                    {
                        Entries = entries,
                        Continue = shouldContinue,
                        Context = context,
                    },
                    ExtensionEvent.AgentBeforeSettle settle => settle with
                    {
                        Entries = entries,
                        Continue = shouldContinue,
                        Context = context,
                    },
                    _ => baseEvent,
                };

                try
                {
                    if (await handler(@event, ctx) is BoundaryResult handlerResult)
                    {
                        if (handlerResult.Entries is not null)
                        {
                            entries = handlerResult.Entries;
                        }

                        if (handlerResult.Continue is not null)
                        {
                            shouldContinue = handlerResult.Continue.Value;
                        }
                    }
                }
                catch (Exception error)
                {
                    EmitError(new ExtensionError(extension.Path, eventName, error.Message, error.StackTrace));
                }

                try
                {
                    context = await buildContext(entries);
                    valid = true;
                }
                catch (Exception error)
                {
                    valid = false;
                    EmitError(new ExtensionError(
                        extension.Path,
                        eventName,
                        $"Invalid boundary entries: {error.Message}",
                        error.StackTrace));
                }
            }
        }

        return valid
            ? new BoundaryDispatchResult(entries, shouldContinue, context, true)
            : new BoundaryDispatchResult([], false, context, false);
    }

    /// <summary>
    /// Dispatch an event with no dedicated method. Port of the generic TS <c>emit</c>; handler errors
    /// are reported through <see cref="EmitError"/> and never propagate.
    /// </summary>
    public Task EmitAsync(ExtensionEvent @event) => EmitCoreAsync(@event, sessionBefore: false);

    /// <summary>Dispatch <c>session_before_switch</c>; the first handler that cancels short-circuits.</summary>
    public async Task<SessionBeforeSwitchResult?> EmitAsync(ExtensionEvent.SessionBeforeSwitch @event) =>
        await EmitCoreAsync(@event, sessionBefore: true) as SessionBeforeSwitchResult;

    /// <summary>Dispatch <c>session_before_fork</c>; the first handler that cancels short-circuits.</summary>
    public async Task<SessionBeforeForkResult?> EmitAsync(ExtensionEvent.SessionBeforeFork @event) =>
        await EmitCoreAsync(@event, sessionBefore: true) as SessionBeforeForkResult;

    /// <summary>Dispatch <c>session_before_compact</c>; the first handler that cancels short-circuits.</summary>
    public async Task<SessionBeforeCompactResult?> EmitAsync(ExtensionEvent.SessionBeforeCompact @event) =>
        await EmitCoreAsync(@event, sessionBefore: true) as SessionBeforeCompactResult;

    /// <summary>Dispatch <c>session_before_tree</c>; the first handler that cancels short-circuits.</summary>
    public async Task<SessionBeforeTreeResult?> EmitAsync(ExtensionEvent.SessionBeforeTree @event) =>
        await EmitCoreAsync(@event, sessionBefore: true) as SessionBeforeTreeResult;

    /// <summary>Returns the event's own action unless a handler overrides it; the last override wins.</summary>
    public async Task<string> EmitCacheWarmingDecisionAsync(ExtensionEvent.CacheWarmingDecision @event)
    {
        var ctx = CreateContext();
        var action = @event.Action;
        var eventName = ExtensionEventNames.CacheWarmingDecision;

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensions, eventName))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    if (await handler(@event, ctx) is CacheWarmingDecisionEventResult { Action: not null } result)
                    {
                        action = result.Action;
                    }
                }
                catch (Exception error)
                {
                    EmitError(new ExtensionError(extension.Path, eventName, error.Message, error.StackTrace));
                }
            }
        }

        return action;
    }

    /// <summary>
    /// Run the <c>message_end</c> handlers. Returns the replacement message, or null when no handler
    /// changed it. Port of the TS <c>emitMessageEnd</c>.
    /// </summary>
    public async Task<ChatMessage?> EmitMessageEndAsync(ExtensionEvent.MessageEnd @event)
    {
        var ctx = CreateContext();
        var currentMessage = @event.Message;
        var modified = false;
        const string eventName = ExtensionEventNames.MessageEnd;

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensions, eventName))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    var currentEvent = @event with { Message = currentMessage };
                    if (await handler(currentEvent, ctx) is not MessageEndEventResult { Message: not null } handlerResult)
                    {
                        continue;
                    }

                    if (!SameRole(handlerResult.Message, currentMessage))
                    {
                        EmitError(new ExtensionError(
                            extension.Path,
                            eventName,
                            "message_end handlers must return a message with the same role"));
                        continue;
                    }

                    currentMessage = handlerResult.Message;
                    modified = true;
                }
                catch (Exception error)
                {
                    EmitError(new ExtensionError(extension.Path, eventName, error.Message, error.StackTrace));
                }
            }
        }

        return modified ? currentMessage : null;
    }

    /// <summary>
    /// Run the <c>tool_result</c> handlers. Each handler sees the event with every earlier handler's
    /// patch applied, exactly as TS does with its mutable shallow copy. Returns null when nothing
    /// changed.
    /// </summary>
    public async Task<ToolResultEventResult?> EmitToolResultAsync(IToolResultEvent @event)
    {
        var ctx = CreateContext();
        var content = @event.Content;
        var details = @event.Details;
        var structuredContent = @event.StructuredContent;
        var isError = @event.IsError;
        var usage = @event.UsageStats;
        var modified = false;
        var currentEvent = @event;
        const string eventName = ExtensionEventNames.ToolResult;

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensions, eventName))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    if (await handler(currentEvent, ctx) is not ToolResultEventResult handlerResult)
                    {
                        continue;
                    }

                    if (handlerResult.Content is not null)
                    {
                        content = handlerResult.Content;
                        // Structured content that is not replaced along with the content may no
                        // longer match it.
                        if (handlerResult.StructuredContent is null)
                        {
                            structuredContent = null;
                        }

                        modified = true;
                    }

                    if (handlerResult.Details is not null)
                    {
                        details = handlerResult.Details;
                        modified = true;
                    }

                    if (handlerResult.StructuredContent is not null)
                    {
                        structuredContent = handlerResult.StructuredContent;
                        modified = true;
                    }

                    if (handlerResult.IsError is not null)
                    {
                        isError = handlerResult.IsError.Value;
                        modified = true;
                    }

                    if (handlerResult.UsageStats is not null)
                    {
                        usage = handlerResult.UsageStats;
                        modified = true;
                    }

                    currentEvent = currentEvent.ApplyResultPatch(handlerResult);
                }
                catch (Exception error)
                {
                    EmitError(new ExtensionError(extension.Path, eventName, error.Message, error.StackTrace));
                }
            }
        }

        if (!modified)
        {
            return null;
        }

        return new ToolResultEventResult
        {
            Content = content,
            Details = details,
            StructuredContent = structuredContent,
            IsError = isError,
            UsageStats = usage,
        };
    }

    /// <summary>
    /// Run the <c>tool_call</c> handlers. Unlike the other dispatchers, handler errors propagate:
    /// TS does not catch them here. Port of the TS <c>emitToolCall</c>.
    /// </summary>
    public async Task<ToolCallEventResult?> EmitToolCallAsync(ExtensionEvent @event)
    {
        var ctx = CreateContext();
        ToolCallEventResult? result = null;

        foreach (var (_, handlers) in SnapshotEventHandlers(extensions, ExtensionEventNames.ToolCall))
        {
            foreach (var handler in handlers)
            {
                if (await handler(@event, ctx) is not ToolCallEventResult handlerResult)
                {
                    continue;
                }

                result = handlerResult;
                if (handlerResult.Block == true)
                {
                    return result;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Run the <c>user_bash</c> handlers. The first handler that returns a result wins; an invalid
    /// return value is reported and rethrown. Port of the TS <c>emitUserBash</c>.
    /// </summary>
    public async Task<UserBashEventResult?> EmitUserBashAsync(ExtensionEvent.UserBash @event)
    {
        var ctx = CreateContext();
        const string eventName = ExtensionEventNames.UserBash;

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensions, eventName))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    var handlerResult = await handler(@event, ctx);
                    if (handlerResult is null)
                    {
                        continue;
                    }

                    if (!IsUserBashEventResult(handlerResult))
                    {
                        throw new InvalidOperationException(InvalidUserBashResultMessage);
                    }

                    return (UserBashEventResult)handlerResult;
                }
                catch (Exception error)
                {
                    EmitError(new ExtensionError(extension.Path, eventName, error.Message, error.StackTrace));
                    throw;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Run the request-time transforms in two phases. <c>context</c> handlers see the conversation
    /// only and Pi restores the prompt and tool state after each; <c>context_with_system</c> handlers
    /// then see the full transcript and their output is used as returned. Port of the TS
    /// <c>emitContext</c>.
    /// </summary>
    /// <remarks>
    /// Difference C121: TS deep-clones the message array with <c>structuredClone</c> so a handler can
    /// splice it in place. The port's <see cref="ChatMessage"/> records are immutable, so the copy is
    /// shallow and a handler signals a change by returning a new list.
    /// </remarks>
    public async Task<IReadOnlyList<ChatMessage>> EmitContextAsync(IReadOnlyList<ChatMessage> messages)
    {
        var ctx = CreateContext();
        var currentMessages = messages.ToList();

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensions, ExtensionEventNames.Context))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    var visibleMessages = currentMessages.Where(message => message is not SystemMessage).ToList();
                    var visibleSnapshot = visibleMessages.ToList();
                    var @event = new ExtensionEvent.ContextEvent(visibleMessages);
                    var handlerResult = await handler(@event, ctx) as ContextEventResult;

                    // Handlers may return a new list or edit event.messages in place.
                    var returned = handlerResult?.Messages
                        ?? (SameMessages(visibleMessages, visibleSnapshot) ? null : visibleMessages);
                    if (returned is null)
                    {
                        continue;
                    }

                    currentMessages = RestoreSystemMessages(currentMessages, visibleSnapshot, returned);
                }
                catch (Exception error)
                {
                    EmitError(new ExtensionError(
                        extension.Path, ExtensionEventNames.Context, error.Message, error.StackTrace));
                }
            }
        }

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensions, ExtensionEventNames.ContextWithSystem))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    var hadLeadingSystemMessage = currentMessages.Count > 0 && currentMessages[0] is SystemMessage;
                    var @event = new ExtensionEvent.ContextWithSystemEvent(currentMessages);
                    var handlerResult = await handler(@event, ctx) as ContextEventResult;
                    if (handlerResult?.Messages is { } replacement)
                    {
                        currentMessages = replacement.ToList();
                    }

                    // Providers read the prompt and initial tools from the leading system message.
                    // Losing it is never intended; report it but honor the handler's output.
                    if (hadLeadingSystemMessage && (currentMessages.Count == 0 || currentMessages[0] is not SystemMessage))
                    {
                        EmitError(new ExtensionError(
                            extension.Path,
                            ExtensionEventNames.ContextWithSystem,
                            "Handler removed the leading system message; the request has no prompt or initial tool declarations. Keep it at index 0 or replace a dropped prefix with getCurrentSystemMessage()."));
                    }
                }
                catch (Exception error)
                {
                    EmitError(new ExtensionError(
                        extension.Path, ExtensionEventNames.ContextWithSystem, error.Message, error.StackTrace));
                }
            }
        }

        return currentMessages;
    }

    /// <summary>Run the <c>before_provider_request</c> handlers; each may replace the payload.</summary>
    public async Task<object?> EmitBeforeProviderRequestAsync(object? payload)
    {
        var ctx = CreateContext();
        var currentPayload = payload;
        const string eventName = ExtensionEventNames.BeforeProviderRequest;

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensions, eventName))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    var @event = new ExtensionEvent.BeforeProviderRequest(currentPayload);
                    var handlerResult = await handler(@event, ctx);
                    if (handlerResult is not null)
                    {
                        currentPayload = handlerResult;
                    }
                }
                catch (Exception error)
                {
                    EmitError(new ExtensionError(extension.Path, eventName, error.Message, error.StackTrace));
                }
            }
        }

        return currentPayload;
    }

    /// <summary>
    /// Run the <c>before_provider_headers</c> handlers. Handlers mutate the headers in place; the
    /// return value is ignored. Port of the TS <c>emitBeforeProviderHeaders</c>.
    /// </summary>
    public async Task<ProviderHeaders> EmitBeforeProviderHeadersAsync(ProviderHeaders headers)
    {
        var ctx = CreateContext();
        const string eventName = ExtensionEventNames.BeforeProviderHeaders;

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensions, eventName))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    await handler(new ExtensionEvent.BeforeProviderHeaders(headers), ctx);
                }
                catch (Exception error)
                {
                    EmitError(new ExtensionError(extension.Path, eventName, error.Message, error.StackTrace));
                }
            }
        }

        return headers;
    }

    /// <summary>
    /// Run the <c>before_agent_start</c> handlers, accumulating their messages and folding their
    /// system-prompt overrides into the options passed to the next one. Port of the TS
    /// <c>emitBeforeAgentStart</c>.
    /// </summary>
    public async Task<BeforeAgentStartCombinedResult> EmitBeforeAgentStartAsync(
        string prompt,
        IReadOnlyList<ImageContent>? images,
        BuildSystemPromptOptions systemPromptOptions)
    {
        var currentOptions = SystemPrompt.NormalizeBuildSystemPromptOptions(systemPromptOptions);
        var messages = new List<CustomMessageDraft>();
        var ctx = new RunnerContext(this, RenderCurrentSystemPrompt);
        const string eventName = ExtensionEventNames.BeforeAgentStart;

        string RenderCurrentSystemPrompt() =>
            SystemPrompt.BuildSystemPrompt(SystemPrompt.ToBuildSystemPromptOptions(currentOptions));

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensions, eventName))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    var @event = new ExtensionEvent.BeforeAgentStart(
                        prompt,
                        images,
                        RenderCurrentSystemPrompt(),
                        currentOptions);
                    if (await handler(@event, ctx) is not BeforeAgentStartEventResult handlerResult)
                    {
                        continue;
                    }

                    if (handlerResult.Message is not null)
                    {
                        messages.Add(handlerResult.Message);
                    }

                    if (handlerResult.SystemPrompt is not null)
                    {
                        currentOptions = currentOptions with { ForceSystemPrompt = handlerResult.SystemPrompt };
                    }
                }
                catch (Exception error)
                {
                    EmitError(new ExtensionError(extension.Path, eventName, error.Message, error.StackTrace));
                }
            }
        }

        return new BeforeAgentStartCombinedResult(messages, currentOptions);
    }

    /// <summary>
    /// Run the <c>resources_discover</c> handlers, attributing every returned path to the extension
    /// that contributed it. Port of the TS <c>emitResourcesDiscover</c>.
    /// </summary>
    public async Task<DiscoveredResourcePaths> EmitResourcesDiscoverAsync(string cwd, string reason)
    {
        var ctx = CreateContext();
        var skillPaths = new List<AttributedResourcePath>();
        var promptPaths = new List<AttributedResourcePath>();
        var themePaths = new List<AttributedResourcePath>();
        const string eventName = ExtensionEventNames.ResourcesDiscover;

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensions, eventName))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    var @event = new ExtensionEvent.ResourcesDiscover(cwd, reason);
                    var result = await handler(@event, ctx) as ResourcesDiscoverResult;
                    if (result is null)
                    {
                        continue;
                    }

                    foreach (var path in result.SkillPaths ?? [])
                    {
                        skillPaths.Add(new AttributedResourcePath(path, extension.Path));
                    }

                    foreach (var path in result.PromptPaths ?? [])
                    {
                        promptPaths.Add(new AttributedResourcePath(path, extension.Path));
                    }

                    foreach (var path in result.ThemePaths ?? [])
                    {
                        themePaths.Add(new AttributedResourcePath(path, extension.Path));
                    }
                }
                catch (Exception error)
                {
                    EmitError(new ExtensionError(extension.Path, eventName, error.Message, error.StackTrace));
                }
            }
        }

        return new DiscoveredResourcePaths(skillPaths, promptPaths, themePaths);
    }

    /// <summary>
    /// Run the <c>input</c> handlers. Transforms chain, "handled" short-circuits. Port of the TS
    /// <c>emitInput</c>.
    /// </summary>
    public async Task<InputEventResult> EmitInputAsync(
        string text,
        IReadOnlyList<ImageContent>? images,
        string source,
        string? streamingBehavior = null)
    {
        var ctx = CreateContext();
        var currentText = text;
        var currentImages = images;
        const string eventName = ExtensionEventNames.Input;

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensions, eventName))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    var @event = new ExtensionEvent.InputEvent(currentText, currentImages, source, streamingBehavior);
                    switch (await handler(@event, ctx))
                    {
                        case InputEventResult.Handled handled:
                            return handled;
                        case InputEventResult.Transform transform:
                            currentText = transform.Text;
                            currentImages = transform.Images ?? currentImages;
                            break;
                    }
                }
                catch (Exception error)
                {
                    EmitError(new ExtensionError(extension.Path, eventName, error.Message, error.StackTrace));
                }
            }
        }

        return currentText != text || !ReferenceEquals(currentImages, images)
            ? new InputEventResult.Transform(currentText, currentImages)
            : new InputEventResult.Continue();
    }

    // ======================================================================
    // Module-level helpers (TS exports of runner.ts)
    // ======================================================================

    /// <summary>
    /// Emit <c>session_shutdown</c> to extensions. Returns true when it was emitted, false when there
    /// were no handlers. Port of the TS <c>emitSessionShutdownEvent</c>; it lives here rather than in
    /// a separate static class because it is part of the runner's contract.
    /// </summary>
    public static async Task<bool> EmitSessionShutdownEventAsync(
        ExtensionRunner extensionRunner,
        ExtensionEvent.SessionShutdown @event)
    {
        if (!extensionRunner.HasHandlers(ExtensionEventNames.SessionShutdown))
        {
            return false;
        }

        await extensionRunner.EmitAsync(@event);
        return true;
    }

    /// <summary>
    /// Ask extensions whether the project is trusted. The first handler that answers yes or no wins;
    /// undecided falls through. Port of the TS <c>emitProjectTrustEvent</c>.
    /// </summary>
    public static async Task<(ProjectTrustEventResult? Result, IReadOnlyList<ExtensionError> Errors)> EmitProjectTrustEventAsync(
        LoadExtensionsResult extensionsResult,
        ExtensionEvent.ProjectTrust @event,
        ProjectTrustContext ctx)
    {
        var errors = new List<ExtensionError>();
        var eventName = ExtensionEventNames.ProjectTrust;

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensionsResult.Extensions, eventName))
        {
            // A single extension may register multiple handlers for the same event.
            // The first project_trust handler that returns yes/no wins; undecided falls through.
            foreach (var handler in handlers)
            {
                try
                {
                    if (await handler(@event, ctx) is not ProjectTrustEventResult handlerResult)
                    {
                        continue;
                    }

                    if (handlerResult.Trusted == ProjectTrustDecision.Undecided)
                    {
                        continue;
                    }

                    return (handlerResult, errors);
                }
                catch (Exception error)
                {
                    errors.Add(new ExtensionError(extension.Path, eventName, error.Message, error.StackTrace));
                }
            }
        }

        return (null, errors);
    }

    // ======================================================================
    // Internals
    // ======================================================================

    /// <summary>
    /// Snapshot each extension's handlers for one event. TS <c>snapshotEventHandlers</c>: the copy
    /// matters because a handler may register or remove handlers while the event is dispatching.
    /// </summary>
    private static IEnumerable<(Extension Extension, IReadOnlyList<ExtensionEventHandler> Handlers)>
        SnapshotEventHandlers(IReadOnlyList<Extension> extensions, string eventName)
    {
        foreach (var extension in extensions)
        {
            var handlers = extension.Handlers.TryGetValue(eventName, out var registered)
                ? registered.ToArray()
                : [];
            yield return (extension, handlers);
        }
    }

    /// <summary>
    /// TS compares messages with <c>===</c>, i.e. reference identity. C# records default to value
    /// equality, so the comparison has to be spelled out.
    /// </summary>
    private static bool SameMessages(IReadOnlyList<ChatMessage> left, IReadOnlyList<ChatMessage> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!ReferenceEquals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// TS compares the <c>role</c> discriminant. The port's message hierarchy maps one type per role,
    /// so a type comparison is the same predicate.
    /// </summary>
    private static bool SameRole(ChatMessage left, ChatMessage right) => left.GetType() == right.GetType();

    /// <summary>
    /// Re-attach the prompt and tool state after a <c>context</c> handler. Handlers only see the
    /// conversation; the system messages belong to Pi. An unchanged conversation keeps every system
    /// message in place, so models with mid-conversation support keep their cached prefix. A changed
    /// one gets the replayed prompt sections and tool declarations as one leading system message, so
    /// pruning, windowing, or slicing from a compaction summary cannot drop them.
    /// </summary>
    private static List<ChatMessage> RestoreSystemMessages(
        IReadOnlyList<ChatMessage> current,
        IReadOnlyList<ChatMessage> visible,
        IReadOnlyList<ChatMessage> returned)
    {
        if (SameMessages(returned, visible))
        {
            return current.ToList();
        }

        var head = Transcript.GetCurrentSystemMessage(current);
        var restored = new List<ChatMessage>(returned.Count + 1);
        if (head is not null)
        {
            restored.Add(head);
        }

        restored.AddRange(returned);
        return restored;
    }

    /// <summary>
    /// TS validates the duck-typed <c>user_bash</c> return (<c>{ operations }</c> XOR
    /// <c>{ result }</c>) because extension code is untyped JavaScript.
    /// </summary>
    /// <remarks>
    /// Difference C122: the port's <see cref="UserBashEventResult"/> is a closed union, so the type
    /// check carries the whole guarantee; only the payload's non-null invariant is still asserted.
    /// </remarks>
    private static bool IsUserBashEventResult(object? value) => value switch
    {
        UserBashEventResult.WithOperations { Operations: not null } => true,
        UserBashEventResult.WithResult { Result: not null } => true,
        _ => false,
    };

    /// <summary>
    /// Resolve the invocation name of every registered command. Names are taken in load order; when
    /// two extensions register the same name both get a <c>name:occurrence</c> form, and any residual
    /// clash is resolved by bumping the suffix until it is free. Port of the TS
    /// <c>resolveRegisteredCommands</c>.
    /// </summary>
    private IReadOnlyList<ResolvedCommand> ResolveRegisteredCommands()
    {
        var commands = new List<RegisteredCommand>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var extension in extensions)
        {
            foreach (var command in extension.Commands.Values)
            {
                commands.Add(command);
                counts[command.Name] = counts.GetValueOrDefault(command.Name) + 1;
            }
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var takenInvocationNames = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<ResolvedCommand>(commands.Count);

        foreach (var command in commands)
        {
            var occurrence = seen.GetValueOrDefault(command.Name) + 1;
            seen[command.Name] = occurrence;

            var invocationName = counts[command.Name] > 1
                ? $"{command.Name}:{occurrence}"
                : command.Name;

            if (takenInvocationNames.Contains(invocationName))
            {
                var suffix = occurrence;
                do
                {
                    suffix++;
                    invocationName = $"{command.Name}:{suffix}";
                }
                while (takenInvocationNames.Contains(invocationName));
            }

            takenInvocationNames.Add(invocationName);
            resolved.Add(new ResolvedCommand
            {
                Name = command.Name,
                SourceInfo = command.SourceInfo,
                Description = command.Description,
                GetArgumentCompletions = command.GetArgumentCompletions,
                Handler = command.Handler,
                InvocationName = invocationName,
            });
        }

        return resolved;
    }

    /// <summary>
    /// Map every key id in the user's keybindings to the action that claims it. When several actions
    /// bind the same key the reserved one wins, so extensions stay blocked regardless of iteration
    /// order. Port of the TS <c>buildBuiltinKeybindings</c>.
    /// </summary>
    private static Dictionary<string, BuiltinKeybinding> BuildBuiltinKeybindings(
        KeybindingsConfig resolvedKeybindings)
    {
        var builtinKeybindings = new Dictionary<string, BuiltinKeybinding>(StringComparer.Ordinal);

        foreach (var (keybinding, keys) in resolvedKeybindings)
        {
            var restrictOverride = ReservedKeybindingsForExtensionConflicts.Contains(keybinding, StringComparer.Ordinal);

            foreach (var key in keys.Values)
            {
                var normalizedKey = key.ToLowerInvariant();

                // If multiple actions bind the same key, the reserved action wins so extensions
                // remain blocked by reserved shortcuts regardless of iteration order.
                if (builtinKeybindings.TryGetValue(normalizedKey, out var existing) &&
                    existing.RestrictOverride &&
                    !restrictOverride)
                {
                    continue;
                }

                builtinKeybindings[normalizedKey] = new BuiltinKeybinding(keybinding, restrictOverride);
            }
        }

        return builtinKeybindings;
    }

    /// <summary>One keybinding id claimed by a key. TS inline <c>{ keybinding, restrictOverride }</c>.</summary>
    private readonly record struct BuiltinKeybinding(string Keybinding, bool RestrictOverride);

    /// <summary>Core of the generic <c>emit</c>. Only <c>session_before_*</c> events produce a result.</summary>
    private async Task<object?> EmitCoreAsync(ExtensionEvent @event, bool sessionBefore)
    {
        var ctx = CreateContext();
        object? result = null;
        var eventName = ExtensionEventNames.For(@event);

        foreach (var (extension, handlers) in SnapshotEventHandlers(extensions, eventName))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    var handlerResult = await handler(@event, ctx);
                    if (!sessionBefore || handlerResult is null)
                    {
                        continue;
                    }

                    result = handlerResult;
                    if (handlerResult is ISessionBeforeResult { Cancel: true })
                    {
                        return result;
                    }
                }
                catch (Exception error)
                {
                    EmitError(new ExtensionError(extension.Path, eventName, error.Message, error.StackTrace));
                }
            }
        }

        return result;
    }

    /// <summary>TS <c>void this.emit(...)</c>: start the dispatch and observe its completion.</summary>
    private static void FireAndForget(Task task) => _ = ObserveAsync(task);

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Handler failures are already reported through EmitError; a listener that throws is the
            // only way to get here, and TS would surface it as an unhandled rejection.
        }
    }

    /// <summary>Throw when a captured context outlived a session replacement or reload.</summary>
    private void AssertActive()
    {
        if (staleMessage is not null)
        {
            throw new InvalidOperationException(staleMessage);
        }
    }

    /// <summary>Bracket one UI prompt with <c>ui_prompt_start</c> / <c>ui_prompt_end</c>. TS <c>withUIPrompt</c>.</summary>
    private async Task<T> WithUIPromptAsync<T>(string kind, string? title, Func<Task<T>> run)
    {
        var outerPrompt = uiPromptDepth++ == 0;
        if (outerPrompt)
        {
            activeUIPrompt = (kind, title);
            EmitUIPromptEvent(new ExtensionEvent.UiPromptStart(kind, title));
        }

        try
        {
            return await run();
        }
        finally
        {
            if (--uiPromptDepth <= 0)
            {
                uiPromptDepth = 0;

                var prompt = activeUIPrompt ?? (kind, title);
                activeUIPrompt = null;
                EmitUIPromptEvent(new ExtensionEvent.UiPromptEnd(prompt.Item1, prompt.Item2));
            }
        }
    }

    /// <summary>Dispatch a UI prompt event without blocking the prompt that is starting or finishing.</summary>
    private void EmitUIPromptEvent(ExtensionEvent @event) =>
        _ = Task.Run(() => FireAndForget(EmitAsync(@event)));

    // ======================================================================
    // Context implementations
    // ======================================================================

    /// <summary>
    /// Port of the object literal <c>createContext()</c> returns. Every member re-reads the runner's
    /// current field through <see cref="AssertActive"/>, so a context captured before a reload
    /// throws instead of silently using stale state.
    /// </summary>
    private class RunnerContext(ExtensionRunner runner, Func<string>? renderSystemPrompt = null) : IExtensionContext
    {
        /// <summary>
        /// The runner this context belongs to. Exposed to the derived contexts so they reuse the
        /// instance the base already stored instead of capturing the constructor argument twice.
        /// </summary>
        protected ExtensionRunner Runner => runner;

        public IExtensionUIContext Ui
        {
            get
            {
                runner.AssertActive();
                return runner.uiContext;
            }
        }

        public string Mode
        {
            get
            {
                runner.AssertActive();
                return runner.mode;
            }
        }

        public bool HasUI
        {
            get
            {
                runner.AssertActive();
                return runner.HasUI();
            }
        }

        public string Cwd
        {
            get
            {
                runner.AssertActive();
                return runner.cwd;
            }
        }

        public IReadonlySessionManager SessionManager
        {
            get
            {
                runner.AssertActive();
                return runner.sessionManager;
            }
        }

        public ModelRegistry ModelRegistry
        {
            get
            {
                runner.AssertActive();
                return runner.modelRegistry;
            }
        }

        public ModelSpec? Model
        {
            get
            {
                runner.AssertActive();
                return runner.getModel();
            }
        }

        public IReadOnlyList<ScopedModel> ScopedModels
        {
            get
            {
                runner.AssertActive();
                return runner.getScopedModels();
            }
        }

        public ThinkingLevel? ThinkingLevel
        {
            get
            {
                runner.AssertActive();
                return runner.runtime.GetThinkingLevel();
            }
        }

        public bool IsIdle()
        {
            runner.AssertActive();
            return runner.isIdleFn();
        }

        public bool IsProjectTrusted()
        {
            runner.AssertActive();
            return runner.isProjectTrustedFn();
        }

        public CancellationToken? Signal
        {
            get
            {
                runner.AssertActive();
                return runner.getSignalFn();
            }
        }

        public void Abort()
        {
            runner.AssertActive();
            runner.abortFn();
        }

        public bool HasPendingMessages()
        {
            runner.AssertActive();
            return runner.hasPendingMessagesFn();
        }

        public void Shutdown()
        {
            runner.AssertActive();
            runner.shutdownHandler();
        }

        public ContextUsage? GetContextUsage()
        {
            runner.AssertActive();
            return runner.getContextUsageFn();
        }

        public void Compact(CompactOptions? options = null)
        {
            runner.AssertActive();
            runner.compactFn(options);
        }

        public string GetSystemPrompt()
        {
            runner.AssertActive();
            return renderSystemPrompt is null ? runner.getSystemPromptFn() : renderSystemPrompt();
        }
    }

    /// <summary>
    /// Port of <c>createToolContext()</c>: the extension context plus <c>tools</c> and
    /// <c>executeTool()</c>. TS extends a fresh context object with property descriptors; the port
    /// derives from <see cref="RunnerContext"/>, which keeps the guarded getters lazy.
    /// </summary>
    private sealed class RunnerToolContext(ExtensionRunner runner, string toolCallId, CancellationToken? signal)
        : RunnerContext(runner), IExtensionToolContext
    {
        public IReadOnlyList<AgentTool> Tools
        {
            get
            {
                Runner.AssertActive();
                return Runner.getCallableToolsFn();
            }
        }

        public async Task<AgentToolCallOutcome> ExecuteTool(
            string name,
            object? args,
            ExecuteToolOptions? options = null)
        {
            Runner.AssertActive();

            if (Runner.executeToolFn is null)
            {
                return new AgentToolCallOutcome(
                    new ToolCallContent($"{toolCallId}/0", name, new Dictionary<string, object?>()),
                    new AgentToolResult(
                        [new TextContent("Nested tool calls are not available in this context")],
                        Details: new Dictionary<string, object?>()),
                    IsError: true);
            }

            var effectiveOptions = options is null
                ? new ExecuteToolOptions { Signal = signal }
                : options with { Signal = options.Signal ?? signal };

            return await Runner.executeToolFn(toolCallId, name, args, effectiveOptions);
        }
    }

    /// <summary>
    /// Port of <c>createCommandContext()</c>: the extension context plus the session-control methods.
    /// </summary>
    private sealed class RunnerCommandContext(ExtensionRunner runner) : RunnerContext(runner), IExtensionCommandContext
    {
        public BuildSystemPromptOptions GetSystemPromptOptions()
        {
            Runner.AssertActive();
            return Runner.getSystemPromptOptionsFn();
        }

        public Task WaitForIdle()
        {
            Runner.AssertActive();
            return Runner.waitForIdleFn();
        }

        public Task<bool> NewSession(NewSessionOptions? options = null)
        {
            Runner.AssertActive();
            return Runner.newSessionHandler(options);
        }

        public Task<bool> Fork(string entryId, ForkOptions? options = null)
        {
            Runner.AssertActive();
            return Runner.forkHandler(entryId, options);
        }

        public Task<bool> NavigateTree(string targetId, NavigateTreeOptions? options = null)
        {
            Runner.AssertActive();
            return Runner.navigateTreeHandler(targetId, options);
        }

        public Task<bool> SwitchSession(string sessionPath, SwitchSessionOptions? options = null)
        {
            Runner.AssertActive();
            return Runner.switchSessionHandler(sessionPath, options);
        }

        public Task Reload()
        {
            Runner.AssertActive();
            return Runner.reloadHandler();
        }
    }

    /// <summary>
    /// Port of the object literal <c>wrapUIPromptContext</c> builds: every member forwards to the
    /// mode's context, and the five dialog methods additionally bracket the call with the UI prompt
    /// events.
    /// </summary>
    private sealed class UIPromptWrappingContext(IExtensionUIContext inner, ExtensionRunner runner) : IExtensionUIContext
    {
        public Task<string?> Select(string title, IReadOnlyList<string> options, ExtensionUIDialogOptions? opts = null) =>
            runner.WithUIPromptAsync(UiPromptKind.Select, title, () => inner.Select(title, options, opts));

        public Task<bool> Confirm(string title, string message, ExtensionUIDialogOptions? opts = null) =>
            runner.WithUIPromptAsync(UiPromptKind.Confirm, title, () => inner.Confirm(title, message, opts));

        public Task<string?> Input(string title, string? placeholder = null, ExtensionUIDialogOptions? opts = null) =>
            runner.WithUIPromptAsync(UiPromptKind.Input, title, () => inner.Input(title, placeholder, opts));

        public void Notify(string message, string? type = null) => inner.Notify(message, type);

        public Action OnTerminalInput(TerminalInputHandler handler) => inner.OnTerminalInput(handler);

        public void SetStatus(string key, string? text) => inner.SetStatus(key, text);

        public void SetWorkingMessage(string? message = null) => inner.SetWorkingMessage(message);

        public void SetWorkingVisible(bool visible) => inner.SetWorkingVisible(visible);

        public void SetWorkingIndicator(WorkingIndicatorOptions? options = null) => inner.SetWorkingIndicator(options);

        public void SetHiddenThinkingLabel(string? label = null) => inner.SetHiddenThinkingLabel(label);

        public void SetWidget(string key, IReadOnlyList<string>? content, ExtensionWidgetOptions? options = null) =>
            inner.SetWidget(key, content, options);

        public void SetWidget(string key, Func<TuiBase, Theme, IComponent>? factory, ExtensionWidgetOptions? options = null) =>
            inner.SetWidget(key, factory, options);

        public void SetFooter(Func<TuiBase, Theme, IReadonlyFooterDataProvider, IComponent>? factory) =>
            inner.SetFooter(factory);

        public void SetHeader(Func<TuiBase, Theme, IComponent>? factory) => inner.SetHeader(factory);

        public void SetTitle(string title) => inner.SetTitle(title);

        public Task<T> Custom<T>(
            Func<TuiBase, Theme, KeybindingsManager, Action<T>, IComponent> factory,
            ExtensionCustomOptions? options = null) =>
            runner.WithUIPromptAsync(UiPromptKind.Custom, null, () => inner.Custom(factory, options));

        public Task<T> CustomAsync<T>(
            Func<TuiBase, Theme, KeybindingsManager, Action<T>, Task<IComponent>> factory,
            ExtensionCustomOptions? options = null) =>
            runner.WithUIPromptAsync(UiPromptKind.Custom, null, () => inner.CustomAsync(factory, options));

        public void PasteToEditor(string text) => inner.PasteToEditor(text);

        public void SetEditorText(string text) => inner.SetEditorText(text);

        public string GetEditorText() => inner.GetEditorText();

        public Task<string?> Editor(string title, string? prefill = null) =>
            runner.WithUIPromptAsync(UiPromptKind.Editor, title, () => inner.Editor(title, prefill));

        public void AddAutocompleteProvider(AutocompleteProviderFactory factory) => inner.AddAutocompleteProvider(factory);

        public void SetEditorComponent(EditorFactory? factory) => inner.SetEditorComponent(factory);

        public EditorFactory? GetEditorComponent() => inner.GetEditorComponent();

        public Theme Theme => inner.Theme;

        public IReadOnlyList<ThemeInfo> GetAllThemes() => inner.GetAllThemes();

        public Theme? GetTheme(string name) => inner.GetTheme(name);

        public SetThemeResult SetTheme(string name) => inner.SetTheme(name);

        public SetThemeResult SetTheme(Theme theme) => inner.SetTheme(theme);

        public bool GetToolsExpanded() => inner.GetToolsExpanded();

        public void SetToolsExpanded(bool expanded) => inner.SetToolsExpanded(expanded);
    }

    /// <summary>
    /// Port of the module-level <c>noOpUIContext</c>: what <c>ctx.ui</c> is before a mode binds a UI.
    /// </summary>
    /// <remarks>
    /// C123 is resolved as of 4f-2: TS returns the process-global <c>theme</c>, and so does the port now
    /// that the theme registry exists (<see cref="ThemeRuntime.Current"/>). Reading it before
    /// <c>initTheme</c> throws, exactly as TS's <c>Proxy</c> does.
    /// </remarks>
    private sealed class NoOpUIContext : IExtensionUIContext
    {
        public static readonly NoOpUIContext Instance = new();

        private NoOpUIContext()
        {
        }

        public Task<string?> Select(string title, IReadOnlyList<string> options, ExtensionUIDialogOptions? opts = null) =>
            Task.FromResult<string?>(null);

        public Task<bool> Confirm(string title, string message, ExtensionUIDialogOptions? opts = null) =>
            Task.FromResult(false);

        public Task<string?> Input(string title, string? placeholder = null, ExtensionUIDialogOptions? opts = null) =>
            Task.FromResult<string?>(null);

        public void Notify(string message, string? type = null)
        {
        }

        public Action OnTerminalInput(TerminalInputHandler handler) => () => { };

        public void SetStatus(string key, string? text)
        {
        }

        public void SetWorkingMessage(string? message = null)
        {
        }

        public void SetWorkingVisible(bool visible)
        {
        }

        public void SetWorkingIndicator(WorkingIndicatorOptions? options = null)
        {
        }

        public void SetHiddenThinkingLabel(string? label = null)
        {
        }

        public void SetWidget(string key, IReadOnlyList<string>? content, ExtensionWidgetOptions? options = null)
        {
        }

        public void SetWidget(string key, Func<TuiBase, Theme, IComponent>? factory, ExtensionWidgetOptions? options = null)
        {
        }

        public void SetFooter(Func<TuiBase, Theme, IReadonlyFooterDataProvider, IComponent>? factory)
        {
        }

        public void SetHeader(Func<TuiBase, Theme, IComponent>? factory)
        {
        }

        public void SetTitle(string title)
        {
        }

        /// <summary>TS resolves this with <c>undefined as never</c>; the port returns the default value.</summary>
        public Task<T> Custom<T>(
            Func<TuiBase, Theme, KeybindingsManager, Action<T>, IComponent> factory,
            ExtensionCustomOptions? options = null) =>
            Task.FromResult(default(T)!);

        /// <inheritdoc cref="Custom{T}"/>
        public Task<T> CustomAsync<T>(
            Func<TuiBase, Theme, KeybindingsManager, Action<T>, Task<IComponent>> factory,
            ExtensionCustomOptions? options = null) =>
            Task.FromResult(default(T)!);

        public void PasteToEditor(string text)
        {
        }

        public void SetEditorText(string text)
        {
        }

        public string GetEditorText() => "";

        public Task<string?> Editor(string title, string? prefill = null) => Task.FromResult<string?>(null);

        public void AddAutocompleteProvider(AutocompleteProviderFactory factory)
        {
        }

        public void SetEditorComponent(EditorFactory? factory)
        {
        }

        public EditorFactory? GetEditorComponent() => null;

        public Theme Theme => ThemeRuntime.Current;

        public IReadOnlyList<ThemeInfo> GetAllThemes() => [];

        public Theme? GetTheme(string name) => null;

        public SetThemeResult SetTheme(string name) => new(false, "UI not available");

        public SetThemeResult SetTheme(Theme theme) => new(false, "UI not available");

        public bool GetToolsExpanded() => false;

        public void SetToolsExpanded(bool expanded)
        {
        }
    }
}
