// ============================================================================
// Extension runner actions — port of core/extensions/types.ts (4e-3e-9g)
// ============================================================================
//
// TS declares three action bags that the host mode supplies and the runner copies into the shared
// runtime / its own fields:
//
//   ExtensionActions               → `pi.*` API methods            (this file)
//   ExtensionContextActions        → `ctx.*` in event handlers     (this file)
//   ExtensionCommandContextActions → `ctx.*` in command handlers   (this file)
//
// plus the inline `providerActions` third parameter of `ExtensionRunner.bindCore` and the
// `ExtensionError` record that `onError` / `emitError` carry. They live in their own file because
// they are the runner's *input* contract: 4e-3e-9h's `AgentSession` wiring implements them and
// 4e-3e-9g's runner consumes them.

using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions.Types;

namespace Pi.CodingAgent.Core.Extensions;

// ---------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------

/// <summary>
/// One error raised by an extension handler. Port of the TS <c>ExtensionError</c>.
/// </summary>
/// <param name="ExtensionPath">Path of the extension that raised the error.</param>
/// <param name="Event">Event or API name being dispatched when the error surfaced.</param>
/// <param name="Error">Error message.</param>
/// <param name="Stack">Stack trace, when the host provided one.</param>
public sealed record ExtensionError(string ExtensionPath, string Event, string Error, string? Stack = null);

/// <summary>Receives every error an extension raised. TS <c>ExtensionErrorListener</c>.</summary>
public delegate void ExtensionErrorListener(ExtensionError error);

// ---------------------------------------------------------------------------
// ExtensionActions — backs pi.* API methods
// ---------------------------------------------------------------------------

/// <summary>
/// Action implementations for <c>pi.*</c> API methods. Port of the TS <c>ExtensionActions</c>.
/// </summary>
/// <remarks>
/// The runner copies every member into the shared runtime, so extension API objects created during
/// loading observe the same actions after <c>bindCore</c> as ones created later.
/// </remarks>
public sealed record ExtensionActions
{
    public required SendMessageHandler SendMessage { get; init; }

    public required SendUserMessageHandler SendUserMessage { get; init; }

    public required AppendEntryHandler AppendEntry { get; init; }

    public required SetSessionNameHandler SetSessionName { get; init; }

    public required GetSessionNameHandler GetSessionName { get; init; }

    public required SetLabelHandler SetLabel { get; init; }

    public required GetActiveToolsHandler GetActiveTools { get; init; }

    public required GetAllToolsHandler GetAllTools { get; init; }

    public required GetSettingsHandler GetSettings { get; init; }

    public required SetActiveToolsHandler SetActiveTools { get; init; }

    public required RefreshToolsHandler RefreshTools { get; init; }

    public required GetCommandsHandler GetCommands { get; init; }

    public required SetModelHandler SetModel { get; init; }

    public required GetThinkingLevelHandler GetThinkingLevel { get; init; }

    public required SetThinkingLevelHandler SetThinkingLevel { get; init; }
}

// ---------------------------------------------------------------------------
// ExtensionContextActions — backs ctx.* in event handlers
// ---------------------------------------------------------------------------

/// <summary>
/// Actions for the <c>ExtensionContext</c> (<c>ctx.*</c> in event handlers). Required by all modes.
/// Port of the TS <c>ExtensionContextActions</c>.
/// </summary>
/// <remarks>
/// Difference C113: TS models the two optional members with optional properties
/// (<c>executeTool?</c> / <c>getCallableTools?</c>) and the runner falls back to a "nested tool
/// calls are not available" stub and an empty list. The port keeps them nullable and applies the
/// same fallbacks inside the runner, so a caller can omit them exactly as in TS.
/// </remarks>
public sealed record ExtensionContextActions
{
    public required Func<ModelSpec?> GetModel { get; init; }

    public required Func<IReadOnlyList<ScopedModel>> GetScopedModels { get; init; }

    public required Func<bool> IsIdle { get; init; }

    public required Func<bool> IsProjectTrusted { get; init; }

    public required Func<CancellationToken?> GetSignal { get; init; }

    public required Action Abort { get; init; }

    public required Func<bool> HasPendingMessages { get; init; }

    public required Action Shutdown { get; init; }

    public required Func<ContextUsage?> GetContextUsage { get; init; }

    public required Action<CompactOptions?> Compact { get; init; }

    public required Func<string> GetSystemPrompt { get; init; }

    /// <summary>Backs <c>ctx.getSystemPromptOptions()</c>; defaults to options derived from the cwd.</summary>
    public Func<BuildSystemPromptOptions>? GetSystemPromptOptions { get; init; }

    /// <summary>Backs <c>ctx.executeTool()</c>. Without it, nested calls fail.</summary>
    public Func<string, string, object?, ExecuteToolOptions, Task<AgentToolCallOutcome>>? ExecuteTool { get; init; }

    /// <summary>Backs <c>ctx.tools</c>.</summary>
    public Func<IReadOnlyList<AgentTool>>? GetCallableTools { get; init; }
}

// ---------------------------------------------------------------------------
// ExtensionCommandContextActions — backs ctx.* in command handlers
// ---------------------------------------------------------------------------

/// <summary>
/// Actions for the <c>ExtensionCommandContext</c> (<c>ctx.*</c> in command handlers). Only needed
/// for interactive mode, where extension commands are invokable. Port of the TS
/// <c>ExtensionCommandContextActions</c>.
/// </summary>
/// <remarks>
/// Difference C114: TS returns <c>{ cancelled: boolean }</c> from the four session-control handlers.
/// The port returns a bare <see cref="bool"/> — a record wrapping a single flag carries no extra
/// information and every call site destructures it immediately.
/// </remarks>
public sealed record ExtensionCommandContextActions
{
    public required Func<Task> WaitForIdle { get; init; }

    public required Func<NewSessionOptions?, Task<bool>> NewSession { get; init; }

    public required Func<string, ForkOptions?, Task<bool>> Fork { get; init; }

    public required Func<string, NavigateTreeOptions?, Task<bool>> NavigateTree { get; init; }

    public required Func<string, SwitchSessionOptions?, Task<bool>> SwitchSession { get; init; }

    public required Func<Task> Reload { get; init; }
}

// ---------------------------------------------------------------------------
// Provider registration actions — bindCore's optional third parameter
// ---------------------------------------------------------------------------

/// <summary>
/// Overrides for provider registration performed by extensions. Port of the inline
/// <c>providerActions</c> parameter of the TS <c>ExtensionRunner.bindCore</c>; every member is
/// optional and falls back to the runner's <see cref="ModelRegistry"/>.
/// </summary>
/// <remarks>
/// Difference C115: TS declares the object inline in the method signature. The port names it so the
/// 4e-3e-9h wiring can build it separately from <see cref="ExtensionActions"/>.
/// </remarks>
public sealed record ProviderRegistrationActions
{
    public Action<string, ProviderConfigInput>? RegisterProvider { get; init; }

    public Action<IProvider>? RegisterNativeProvider { get; init; }

    public Action<string>? UnregisterProvider { get; init; }

    public Action<VirtualModelDefinition>? RegisterVirtualModel { get; init; }

    public Action<string, string>? UnregisterVirtualModel { get; init; }
}

// ---------------------------------------------------------------------------
// Session-control handler delegates
// ---------------------------------------------------------------------------

/// <summary>Start a new session. Port of the TS <c>NewSessionHandler</c>; returns "cancelled".</summary>
public delegate Task<bool> NewSessionHandler(NewSessionOptions? options = null);

/// <summary>Fork from an entry. Port of the TS <c>ForkHandler</c>; returns "cancelled".</summary>
public delegate Task<bool> ForkHandler(string entryId, ForkOptions? options = null);

/// <summary>Navigate the session tree. Port of the TS <c>NavigateTreeHandler</c>; returns "cancelled".</summary>
public delegate Task<bool> NavigateTreeHandler(string targetId, NavigateTreeOptions? options = null);

/// <summary>Switch session files. Port of the TS <c>SwitchSessionHandler</c>; returns "cancelled".</summary>
public delegate Task<bool> SwitchSessionHandler(string sessionPath, SwitchSessionOptions? options = null);

/// <summary>Reload extensions, skills, prompts, themes and context files. TS <c>ReloadHandler</c>.</summary>
public delegate Task ReloadHandler();

/// <summary>Request a graceful shutdown. TS <c>ShutdownHandler</c>.</summary>
public delegate void ShutdownHandler();
