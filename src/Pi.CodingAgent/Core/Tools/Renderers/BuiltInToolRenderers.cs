// ============================================================================
// Built-in tool renderers, without the tools themselves.
// Port of core/tools/renderers/index.ts
// ============================================================================

using Pi.CodingAgent.Core.Extensions;

namespace Pi.CodingAgent.Core.Tools.Renderers;

/// <summary>
/// Port of <c>core/tools/renderers/index.ts</c>. A presentation displays tool calls and results; it does
/// not execute them and does not need their parameter schemas, so importing this instead of the tool
/// implementations keeps the execution path out of a process that only renders.
/// </summary>
public static class BuiltInToolRenderers
{
    /// <summary>
    /// Renderers for every built-in tool, keyed by tool name. TS <c>createAllToolRenderers</c>.
    /// </summary>
    /// <remarks>
    /// A fresh dictionary each call, matching the TS, which rebuilds the record (and therefore the two
    /// shell renderer sets) on every lookup.
    /// </remarks>
    public static IReadOnlyDictionary<string, ToolRenderers> CreateAllToolRenderers() =>
        new Dictionary<string, ToolRenderers>(StringComparer.Ordinal)
        {
            ["read"] = ReadRenderers.Value,
            ["bash"] = BashRenderers.CreateShellRenderers("$"),
            ["powershell"] = BashRenderers.CreateShellRenderers("PS>"),
            ["edit"] = EditRenderers.Value,
            ["write"] = WriteRenderers.Value,
            ["grep"] = GrepRenderers.Value,
            ["find"] = FindRenderers.Value,
            ["ls"] = LsRenderers.Value,
        };

    /// <summary>
    /// Merge built-in renderers into a tool definition that does not supply its own. TS
    /// <c>withBuiltInRenderers</c>.
    /// </summary>
    /// <remarks>
    /// <c>ToolExecutionComponent</c> used to do this lookup itself, which forced every presentation to
    /// import the tool implementations. Callers do it now, so a process that renders can import renderers
    /// alone. An unknown tool name yields the definition unchanged, or null when there is neither.
    /// </remarks>
    public static ToolRenderers? WithBuiltInRenderers(string toolName, ToolRenderers? definition)
    {
        var builtIn = CreateAllToolRenderers().GetValueOrDefault(toolName);
        if (definition is null)
        {
            return builtIn;
        }

        if (builtIn is null)
        {
            return definition;
        }

        return definition with
        {
            RenderCall = definition.RenderCall ?? builtIn.RenderCall,
            RenderResult = definition.RenderResult ?? builtIn.RenderResult,
        };
    }
}
