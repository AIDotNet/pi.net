// ============================================================================
// Extension runtime types — port of core/extensions/types.ts (4d-2b, loader subset)
// ============================================================================
//
// `types.ts` also declares the runtime state/actions, the loaded-extension record and the load
// result (lines 2121–2260). The 4d-1 contract batch ported the API/context/event surface; these
// loader-facing types land here because `ExtensionLoader` (4d-2b) is their first consumer. The
// runner (4e) completes the runtime by replacing the loader's throwing action stubs.

using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions.Types;

namespace Pi.CodingAgent.Core.Extensions;

/// <summary>
/// One registered event handler. TS <c>HandlerFn = (...args: unknown[]) =&gt; Promise&lt;unknown&gt;</c>:
/// the runner always calls it as <c>handler(event, ctx)</c>, so the port takes a params array.
/// </summary>
public delegate Task<object?> ExtensionEventHandler(params object?[] args);

/// <summary>Shared state created by the loader, used during registration and runtime. TS <c>ExtensionRuntimeState</c>.</summary>
public interface IExtensionRuntime
{
    /// <summary>Flag values: defaults set during registration, CLI values set after.</summary>
    IDictionary<string, object> FlagValues { get; }

    /// <summary>Legacy provider-config registrations queued during extension loading, processed when the runner binds.</summary>
    IList<PendingProviderRegistration> PendingProviderRegistrations { get; }

    /// <summary>Native pi-ai provider registrations queued during extension loading, processed when the runner binds.</summary>
    IList<PendingNativeProviderRegistration> PendingNativeProviderRegistrations { get; }

    /// <summary>Virtual model registrations queued during extension loading, processed when the runner binds.</summary>
    IList<PendingVirtualModelRegistration> PendingVirtualModelRegistrations { get; }

    /// <summary>Servers registered with <c>pi.registerMcpServer()</c>.</summary>
    McpServerRegistry McpServers { get; }

    // ---- ExtensionActions (throwing stubs until the runner binds) ----

    void SendMessage(CustomMessageDraft message, SendMessageOptions? options);

    void SendUserMessage(MessageContent content, SendUserMessageOptions? options);

    void AppendEntry(string customType, object? data);

    void SetSessionName(string name);

    string? GetSessionName();

    void SetLabel(string entryId, string? label);

    IReadOnlyList<string> GetActiveTools();

    IReadOnlyList<ToolInfo> GetAllTools();

    Settings GetSettings();

    void SetActiveTools(IReadOnlyList<string> toolNames);

    /// <summary>No-op stub: refreshing is only needed post-bind (TS <c>refreshTools: () =&gt; {}</c>).</summary>
    void RefreshTools();

    IReadOnlyList<SlashCommandInfo> GetCommands();

    Task<bool> SetModel(Model model);

    ThinkingLevel GetThinkingLevel();

    void SetThinkingLevel(ThinkingLevel level);

    // ---- Binding: the runner copies its implementations into this shared runtime ----

    /// <summary>
    /// Copy the runner's <c>pi.*</c> action implementations into this runtime. Port of the
    /// field-by-field assignment at the top of the TS <c>ExtensionRunner.bindCore</c>: extension API
    /// objects created while loading hold this same runtime instance, so they observe the bound
    /// actions afterwards.
    /// </summary>
    void BindActions(ExtensionActions actions);

    /// <summary>
    /// Rebind the <c>ctx</c> factory used by <c>pi.registerVirtualModel()</c> routing. Port of the
    /// TS <c>runtime.createContext = () =&gt; this.createContext()</c> assignment.
    /// </summary>
    void BindContextFactory(Func<IExtensionContext> factory);

    /// <summary>
    /// Rebind provider registration so it takes effect immediately instead of queueing. Port of the
    /// post-flush reassignments at the end of the TS <c>ExtensionRunner.bindCore</c>. The runner
    /// resolves every optional override against its <see cref="ModelRegistry"/> before binding, so
    /// all five delegates are non-null.
    /// </summary>
    void BindProviderRegistration(
        Action<string, ProviderConfigInput> registerProvider,
        Action<IProvider> registerNativeProvider,
        Action<string> unregisterProvider,
        Action<VirtualModelDefinition> registerVirtualModel,
        Action<string, string> unregisterVirtualModel);

    // ---- ExtensionRuntimeState lifecycle ----

    /// <summary>Create an extension context. Throws before the runner binds.</summary>
    IExtensionContext CreateContext();

    /// <summary>Throws when this extension instance is stale after runtime replacement.</summary>
    void AssertActive();

    /// <summary>Marks this extension instance as stale after runtime replacement or reload.</summary>
    void Invalidate(string? message = null);

    /// <summary>Retain an event-bus subscription until this runtime is invalidated.</summary>
    Action TrackEventBusSubscription(Action unsubscribe);

    // ---- Provider / virtual-model registration (queue before bind, direct after) ----

    void RegisterProvider(string name, ProviderConfigInput config, string? extensionPath = null);

    void RegisterNativeProvider(IProvider provider, string? extensionPath = null);

    void UnregisterProvider(string name, string? extensionPath = null);

    void RegisterVirtualModel(VirtualModelDefinition definition, string? extensionPath = null);

    void UnregisterVirtualModel(string provider, string id);
}

/// <summary>A provider-config registration queued during extension loading. TS inline record.</summary>
public sealed record PendingProviderRegistration(string Name, ProviderConfigInput Config, string ExtensionPath);

/// <summary>A native provider registration queued during extension loading. TS inline record.</summary>
public sealed record PendingNativeProviderRegistration(IProvider Provider, string ExtensionPath);

/// <summary>A virtual model registration queued during extension loading. TS inline record.</summary>
public sealed record PendingVirtualModelRegistration(VirtualModelDefinition Definition, string ExtensionPath);

/// <summary>
/// A loaded extension with everything it registered. Port of the TS <c>Extension</c>.
/// </summary>
/// <remarks>
/// Difference C96: TS leaves <c>toolRenderers</c> / <c>markdownTransformer</c> / <c>entryRenderers</c>
/// absent until first use and creates the maps lazily; the port initializes every collection
/// eagerly (empty instead of null) so consumers never null-check. <c>MessageRenderers</c> and
/// <c>EntryRenderers</c> store the closed generic delegates as <see cref="Delegate"/> — TS erases
/// <c>MessageRenderer&lt;T&gt;</c> to its default instantiation the same way; the runner casts at
/// dispatch time.
/// </remarks>
public sealed class Extension
{
    /// <summary>Path as configured (may be synthetic, e.g. <c>&lt;inline&gt;</c>).</summary>
    public required string Path { get; init; }

    /// <summary>Resolved file path (equal to <see cref="Path"/> for synthetic paths).</summary>
    public required string ResolvedPath { get; init; }

    /// <summary>
    /// Hidden extensions load but are not listed. Settable: TS's <c>loadExtensionPaths</c> and
    /// <c>loadExtensionFactories</c> set it on the freshly loaded extension.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// See <see cref="InlineExtension.Described.Replaceable"/>. Settable for the same reason as
    /// <see cref="Hidden"/>.
    /// </summary>
    public bool Replaceable { get; set; }

    /// <summary>
    /// Where the extension came from. Settable, not <c>init</c>: TS's <c>applyExtensionSourceInfo</c>
    /// overwrites it once the final extension set is known (see <c>DefaultResourceLoader</c>).
    /// </summary>
    public required SourceInfo SourceInfo { get; set; }

    /// <summary>Event handlers by event name, in registration order.</summary>
    public Dictionary<string, List<ExtensionEventHandler>> Handlers { get; } = new(StringComparer.Ordinal);

    /// <summary>Registered tools by name.</summary>
    public Dictionary<string, RegisteredTool> Tools { get; } = new(StringComparer.Ordinal);

    /// <summary>Custom message renderers by custom entry type.</summary>
    public Dictionary<string, Delegate> MessageRenderers { get; } = new(StringComparer.Ordinal);

    /// <summary>Tool-call renderers, in extension load order.</summary>
    public List<ToolRendererResolver> ToolRenderers { get; } = [];

    /// <summary>Markdown transformer, when the extension registered one.</summary>
    public MarkdownTransformer? MarkdownTransformer { get; set; }

    /// <summary>Custom entry renderers by custom entry type.</summary>
    public Dictionary<string, Delegate> EntryRenderers { get; } = new(StringComparer.Ordinal);

    /// <summary>Registered commands by name.</summary>
    public Dictionary<string, RegisteredCommand> Commands { get; } = new(StringComparer.Ordinal);

    /// <summary>Registered flags by name.</summary>
    public Dictionary<string, ExtensionFlag> Flags { get; } = new(StringComparer.Ordinal);

    /// <summary>Registered shortcuts by key id.</summary>
    public Dictionary<string, ExtensionShortcut> Shortcuts { get; } = new(StringComparer.Ordinal);
}

/// <summary>One extension that failed to load. TS inline record.</summary>
public sealed record ExtensionLoadError(string Path, string Error);

/// <summary>One non-fatal load diagnostic. TS inline record.</summary>
public sealed record ExtensionLoadWarning(string Path, string Warning);

/// <summary>
/// Result of loading extensions. Port of the TS <c>LoadExtensionsResult</c>.
/// </summary>
/// <param name="Extensions">Successfully loaded extensions, in load order.</param>
/// <param name="Errors">Failures, one per failed path; loading continues past them.</param>
/// <param name="Warnings">Non-fatal diagnostics (always empty in the loader; the runner adds to it).</param>
/// <param name="Runtime">Shared runtime — actions are throwing stubs until the runner binds.</param>
public sealed record LoadExtensionsResult(
    IReadOnlyList<Extension> Extensions,
    IReadOnlyList<ExtensionLoadError> Errors,
    IReadOnlyList<ExtensionLoadWarning> Warnings,
    IExtensionRuntime Runtime);
