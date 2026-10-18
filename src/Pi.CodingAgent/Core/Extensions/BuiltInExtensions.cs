// ============================================================================
// Built-in extension registry — port of extensions/index.ts (4d-3)
// ============================================================================
//
// TS registers the four built-in extensions as InlineExtension descriptions; the CLI prepends
// them to the user's extension factories (main.ts: `[...builtInExtensions, ...options.extensionFactories]`).
// The replaceable ones (codemode / tool-search / mcp) are dropped by the resource loader (4e,
// omitReplacedExtensions) when another extension registers the same tool, command, or flag name;
// llama.cpp is not replaceable.
//
// All four factories are real: llama / codemode / tool-search landed with 4d-5..4d-7 and mcp with
// 4d-4 (McpExtension.CreateMcpExtension). The registry used to carry a placeholder for mcp even
// after 4d-4 landed, which made every startup fail with "lands with batch 4d-4"; 4g-2 replaced it.

namespace Pi.CodingAgent.Core.Extensions;

using Pi.CodingAgent.Extensions.Codemode;
using Pi.CodingAgent.Extensions.Llama;
using Pi.CodingAgent.Extensions.Mcp;
using Pi.CodingAgent.Extensions.ToolSearch;

/// <summary>
/// The built-in extensions, in load order. Port of <c>builtInExtensions</c> from
/// <c>extensions/index.ts</c>.
/// </summary>
public static class BuiltInExtensions
{
    /// <summary>
    /// The four built-in extensions. Order matches the TS array: llama.cpp first (not
    /// replaceable), then the replaceable codemode / tool-search / mcp.
    /// </summary>
    public static IReadOnlyList<InlineExtension> All { get; } = new InlineExtension[]
    {
        new InlineExtension.Described("llama.cpp", LlamaExtension.CreateLlamaExtension(), Builtin: true),
        new InlineExtension.Described(
            "codemode", CodemodeExtension.CreateCodemodeExtension(), Replaceable: true, Builtin: true),
        new InlineExtension.Described(
            "tool-search", ToolSearch.CreateToolSearchExtension(), Replaceable: true, Builtin: true),
        new InlineExtension.Described(
            "mcp", McpExtension.CreateMcpExtension(), Replaceable: true, Builtin: true),
    };
}
