using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;

namespace Pi.CodingAgent.Core.Extensions;

/// <summary>
/// How the model reaches a tool. TS models this as the literal union
/// <c>"direct" | "model-only" | "codemode" | "deferred" | "hidden"</c>; the port keeps
/// <see cref="string"/> with these constants (difference C80's neighbour).
/// </summary>
/// <remarks>
/// <c>direct</c> is declared to the model while active and callable while active; <c>model-only</c> is
/// declared but never callable; <c>codemode</c> is callable whenever registered but only declared when
/// explicitly activated; <c>deferred</c> is like <c>codemode</c> but hidden from codemode listings;
/// <c>hidden</c> is registered but unreachable. <c>direct</c> and <c>model-only</c> activate on
/// registration, the others do not.
/// </remarks>
public static class ToolExposure
{
    public const string Direct = "direct";

    public const string ModelOnly = "model-only";

    public const string Codemode = "codemode";

    public const string Deferred = "deferred";

    public const string Hidden = "hidden";

    /// <summary>The TS default: <c>direct</c> when the member is absent.</summary>
    public const string Default = Direct;
}

/// <summary>Which framing the TUI draws around a tool row. TS: <c>renderShell</c>.</summary>
public static class RenderShellMode
{
    /// <summary>The standard colored shell.</summary>
    public const string Default = "default";

    /// <summary>The tool renders its own framing.</summary>
    public const string Self = "self";
}

/// <summary>
/// Hints about what a tool does, with the meaning of MCP tool annotations. Port of the TS
/// <c>ToolAnnotations</c>.
/// </summary>
public sealed record ToolAnnotations
{
    /// <summary>The tool does not modify its environment.</summary>
    public bool? ReadOnlyHint { get; init; }

    /// <summary>Meaningful when not read-only.</summary>
    public bool? DestructiveHint { get; init; }

    /// <summary>Meaningful when not read-only.</summary>
    public bool? IdempotentHint { get; init; }

    /// <summary>The tool reaches an open world of external entities rather than a closed domain.</summary>
    public bool? OpenWorldHint { get; init; }
}

/// <summary>A group of related tools, such as the tools of one MCP server. Port of the TS <c>ToolNamespace</c>.</summary>
public sealed record ToolNamespace
{
    /// <summary>For example <c>mcp__docs</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Short summary shown once with the group in model-facing tool listings.</summary>
    public string? Description { get; init; }

    /// <summary>Longer usage guidance, such as MCP server instructions. Not part of tool listings.</summary>
    public string? Instructions { get; init; }
}

/// <summary>
/// The tools of a session as <see cref="ToolDefinition.PrepareLoadout"/> sees them. Port of the TS
/// <c>ToolLoadout</c>.
/// </summary>
public interface IToolLoadout
{
    /// <summary>Tools declared to the model (the active tools), in order, with their original descriptions.</summary>
    IReadOnlyList<AgentTool> Declared { get; }

    /// <summary>Tools callable through the nested-tool entry point.</summary>
    IReadOnlyList<AgentTool> Callable { get; }

    /// <summary>Every registered tool.</summary>
    IReadOnlyList<AgentTool> Registered { get; }

    string GetExposure(string name);

    ToolNamespace? GetNamespace(string name);

    /// <summary>
    /// A tool's <c>promptGuidelines</c>. Hidden declarations leave them out of the system prompt.
    /// TS <c>getPromptGuidelines(name)</c>; the port returns an empty list for unknown tools,
    /// matching the TS implementations' fallback.
    /// </summary>
    IReadOnlyList<string> GetPromptGuidelines(string name);
}

/// <summary>Changes <see cref="ToolDefinition.PrepareLoadout"/> makes to what the model sees.</summary>
public sealed record ToolLoadoutChanges
{
    /// <summary>Model-facing descriptions of declared tools, by tool name.</summary>
    public IReadOnlyDictionary<string, string>? Descriptions { get; init; }

    /// <summary>
    /// Declared tools whose declarations requests leave out. They stay active and callable, and the
    /// transcript still declares them, so the active set survives <c>/tree</c> and resume.
    /// </summary>
    public IReadOnlyList<string>? HiddenDeclarations { get; init; }
}

/// <summary>Rendering options for tool results. Port of the TS <c>ToolRenderResultOptions</c>.</summary>
public sealed record ToolRenderResultOptions(bool Expanded, bool IsPartial);

/// <summary>Context passed to tool renderers. Port of the TS <c>ToolRenderContext</c>.</summary>
/// <remarks>
/// The TS is generic over <c>TState</c>/<c>TArgs</c>; the port carries the shared renderer state as
/// <see cref="object"/> and the raw arguments as a JSON-shaped dictionary, since C# has no structural
/// typing to keep a renderer's state type aligned with its tool (the same trade-off as C62).
/// </remarks>
public sealed record ToolRenderContext
{
    /// <summary>Current tool call arguments. Shared across call/result renders for the same call.</summary>
    public IReadOnlyDictionary<string, object?> Args { get; init; } = new Dictionary<string, object?>();

    /// <summary>Unique id for this tool execution. Stable across call/result renders.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>Invalidate just this tool execution component for redraw.</summary>
    public required Action Invalidate { get; init; }

    /// <summary>Previously returned component for this render slot, if any.</summary>
    public IComponent? LastComponent { get; init; }

    /// <summary>Shared renderer state for this tool row.</summary>
    public object? State { get; init; }

    /// <summary>Working directory for this tool execution.</summary>
    public required string Cwd { get; init; }

    public bool ExecutionStarted { get; init; }

    public bool ArgsComplete { get; init; }

    public bool IsPartial { get; init; }

    public bool Expanded { get; init; }

    /// <summary>Whether inline images are currently shown in the TUI.</summary>
    public bool ShowImages { get; init; }

    public bool IsError { get; init; }

    /// <summary>
    /// 工具执行耗时（毫秒），来自最终结果；运行中、未运行或早于时长记录的结果为 <c>null</c>。
    /// TS <c>durationMs</c>。
    /// </summary>
    public long? DurationMs { get; init; }

    /// <summary>
    /// Horizontal padding configured by the outputPad setting. Renderers with
    /// <see cref="RenderShellMode.Self"/> apply it themselves. TS <c>outputPad</c>.
    /// </summary>
    public int OutputPad { get; init; }
}

/// <summary>
/// A tool's call renderer. Port of the TS
/// <c>renderCall?: (args, theme, context) =&gt; Component</c>.
/// </summary>
/// <remarks>
/// The TS is generic over the argument type; the port takes the raw arguments as <see cref="object"/>
/// (the JSON-shaped dictionary the runtime hands tools), for the same reason as
/// <see cref="ToolRenderContext"/>.
/// </remarks>
public delegate IComponent ToolRenderCall(object? args, Theme theme, ToolRenderContext context);

/// <summary>
/// A tool's result renderer. Port of the TS
/// <c>renderResult?: (result, options, theme, context) =&gt; Component</c>.
/// </summary>
/// <remarks>
/// The TS <c>result</c> is <c>AgentToolResult&lt;TDetails&gt;</c>; the port passes the
/// <see cref="AgentToolResult"/> itself, whose <c>Details</c> member is already <see cref="object"/>
/// (see C62).
/// </remarks>
public delegate IComponent ToolRenderResult(
    AgentToolResult result,
    ToolRenderResultOptions options,
    Theme theme,
    ToolRenderContext context);

/// <summary>Helpers shared by tools that read an optional <see cref="IExtensionToolContext"/>.</summary>
/// <remarks>
/// Difference C84 declared a narrow <c>IToolContext</c> seam here (the five members the built-in tools
/// read) so the tool layer could land before the extension system, mirroring <c>IModelResolverRuntime</c>.
/// That seam is retired as of 4e-3e-9h-2: <see cref="IExtensionToolContext"/> now exists, so tools take
/// the real context. The seam was not merely redundant — it was <em>missing</em> <c>tools</c> and
/// <c>executeTool()</c>, which TS's <c>ExtensionToolContext</c> gives every tool, so an extension tool
/// registered through <c>registerTool()</c> could not run a nested tool.
/// </remarks>
public static class ToolContexts
{
    /// <summary>
    /// The TS <c>ctx?.cwd || cwd</c>: an absent context <em>or an empty cwd</em> falls back to the
    /// tool's own working directory, because JS <c>||</c> is a truthiness test.
    /// </summary>
    public static string ResolveCwd(IExtensionToolContext? context, string fallback) =>
        !string.IsNullOrEmpty(context?.Cwd) ? context.Cwd : fallback;

    /// <summary>
    /// The TS <c>sessionManager.getSessionId()</c> read through an optional context, or null when no
    /// context was supplied.
    /// </summary>
    public static string? ResolveSessionId(IExtensionToolContext? context) => context?.SessionManager.SessionId;

    /// <summary>The TS <c>sessionManager.getSessionFile()</c> read through an optional context.</summary>
    public static string? ResolveSessionFile(IExtensionToolContext? context) => context?.SessionManager.SessionFile;
}

/// <summary>
/// Tool definition for <c>registerTool()</c>. Port of the TS <c>ToolDefinition</c>
/// (<c>core/extensions/types.ts</c>).
/// </summary>
/// <remarks>
/// <para>
/// The TS is generic over the TypeBox parameter schema, the details payload and the renderer state;
/// the port collapses the first two (parameters travel as a JSON schema, details as <see cref="object"/>)
/// for the same reason as C62, and carries the renderer state as <see cref="object"/> (see
/// <see cref="ToolRenderContext"/>).
/// </para>
/// <para>
/// Difference C85 (resolved in 4f-1): <c>renderCall</c> / <c>renderResult</c> are declared here, and
/// <c>renderShell</c> is kept. Both take a <see cref="Theme"/>, which is why they could not be
/// declared before the theme half of 4f landed.
/// </para>
/// </remarks>
public sealed record ToolDefinition
{
    /// <summary>Tool name (used in LLM tool calls).</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable label for UI.</summary>
    public required string Label { get; init; }

    /// <summary>Description for LLM.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Optional one-line snippet for the Available tools section in the default system prompt. Custom
    /// tools are omitted from that section when this is not provided.
    /// </summary>
    public string? PromptSnippet { get; init; }

    /// <summary>Guideline bullets appended to the default system prompt while this tool is active.</summary>
    public IReadOnlyList<string>? PromptGuidelines { get; init; }

    /// <summary>Parameter schema.</summary>
    public required ToolSchema Parameters { get; init; }

    /// <summary>
    /// Provider-side constrained sampling request. TS also allows <c>false</c> to explicitly disable it,
    /// which the docs call equivalent to leaving it undefined, so the port collapses both to null.
    /// </summary>
    public JsonObject? ConstrainedSampling { get; init; }

    /// <summary><see cref="RenderShellMode.Default"/> or <see cref="RenderShellMode.Self"/>.</summary>
    public string? RenderShell { get; init; }

    /// <summary>Custom rendering for tool call display.</summary>
    public ToolRenderCall? RenderCall { get; init; }

    /// <summary>Custom rendering for tool result display.</summary>
    public ToolRenderResult? RenderResult { get; init; }

    /// <summary>
    /// Compatibility shim to prepare raw tool call arguments before schema validation. Must return an
    /// object conforming to <see cref="Parameters"/>.
    /// </summary>
    public Func<object?, object?>? PrepareArguments { get; init; }

    /// <summary>
    /// JSON Schema of <c>structuredContent</c> in successful results. Tools that declare it should always
    /// set <c>structuredContent</c>; codemode scripts then receive it instead of the text content.
    /// </summary>
    public ToolSchema? OutputSchema { get; init; }

    /// <summary>How the model reaches the tool. Default: <see cref="ToolExposure.Direct"/>.</summary>
    public string? Exposure { get; init; }

    /// <summary>Group the tool belongs to, for example its MCP server.</summary>
    public ToolNamespace? Namespace { get; init; }

    /// <summary>Hints about what the tool does, for example from an MCP server.</summary>
    public ToolAnnotations? Annotations { get; init; }

    /// <summary>
    /// Whether registering the tool activates it. TS default: true for <c>direct</c> and
    /// <c>model-only</c> tools; other exposures are never activated on registration.
    /// </summary>
    public bool? DefaultActive { get; init; }

    /// <summary>
    /// Adjust how the loadout is presented to the model while this tool is active. Called whenever the
    /// active tools change.
    /// </summary>
    public Func<IToolLoadout, ToolLoadoutChanges?>? PrepareLoadout { get; init; }

    /// <summary>Per-tool execution mode override. Null means the default mode applies.</summary>
    public ToolExecutionMode? ExecutionMode { get; init; }

    /// <summary>Executes the tool.</summary>
    public required Func<
        string,
        IReadOnlyDictionary<string, object?>,
        CancellationToken,
        AgentToolUpdateCallback?,
        IExtensionToolContext?,
        Task<AgentToolResult>> Execute { get; init; }
}

/// <summary>
/// The renderer triple a tool may supply. Port of the TS <c>ToolRenderers</c>
/// (<c>Pick&lt;AnyToolDefinition, "renderShell" | "renderCall" | "renderResult"&gt;</c>).
/// </summary>
public sealed record ToolRenderers
{
    public string? RenderShell { get; init; }

    public ToolRenderCall? RenderCall { get; init; }

    public ToolRenderResult? RenderResult { get; init; }
}

/// <summary>
/// Creates the context for one tool call. Port of the TS <c>ToolContextFactory</c>, which TS declares as
/// <c>(toolCallId, signal) =&gt; ExtensionToolContext</c>.
/// </summary>
public delegate IExtensionToolContext ToolContextFactory(string toolCallId, CancellationToken signal);
