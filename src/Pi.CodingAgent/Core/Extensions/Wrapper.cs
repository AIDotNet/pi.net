using Pi.Agent.Types;
using Pi.CodingAgent.Core.Tools;

namespace Pi.CodingAgent.Core.Extensions;

// ============================================================================
// Tool wrappers for extension-registered tools — port of core/extensions/wrapper.ts (4e-3e-9h-2)
// ============================================================================
//
// These wrappers only adapt tool execution so extension tools receive the runner context. Tool call and
// tool result interception is handled by AgentSession via the agent-core hooks, not here.

/// <summary>
/// Port of <c>wrapRegisteredTool</c>: wrap one registered tool into an <see cref="AgentTool"/>, using the
/// runner's <see cref="ExtensionRunner.CreateToolContext"/> so tools and event handlers share one context.
/// </summary>
/// <remarks>
/// <para>
/// TS declares the parameter as <c>RegisteredTool</c> (extensions/types.ts) but <c>agent-session.ts</c>
/// also passes plain <c>{ definition, sourceInfo }</c> literals — for SDK tools and for built-in
/// definitions — and for its own private <c>ToolDefinitionEntry</c>. Structural typing accepts all of
/// them, so one TS function has three callers with three declared types. C# has no structural typing,
/// so the port takes the type the signature actually names, <see cref="RegisteredTool"/>, and the
/// session builds <see cref="RegisteredTool"/> values for the two literal callers.
/// </para>
/// <para>
/// This is deliberately <em>not</em> a retirement of the duplicated <c>ToolDefinitionEntry</c>: TS
/// declares both records with the same shape, because <c>ToolDefinitionEntry</c> is private to
/// <c>agent-session.ts</c> and describes a different thing (the session's definition registry) than
/// <c>RegisteredTool</c> (what an extension registered).
/// </para>
/// </remarks>
public static class Wrapper
{
    public static AgentTool WrapRegisteredTool(RegisteredTool registeredTool, ExtensionRunner runner) =>
        ToolDefinitionWrapper.WrapToolDefinition(
            registeredTool.Definition,
            (toolCallId, signal) => runner.CreateToolContext(toolCallId, signal));

    /// <summary>
    /// Port of <c>wrapRegisteredTools</c>: wrap every registered tool into an <see cref="AgentTool"/>.
    /// </summary>
    public static List<AgentTool> WrapRegisteredTools(
        IReadOnlyList<RegisteredTool> registeredTools,
        ExtensionRunner runner) =>
        registeredTools.Select(tool => WrapRegisteredTool(tool, runner)).ToList();
}
