"""Regenerate the per-package Native AOT JSON source-gen contexts.

For each src project that creates JsonSerializerOptions, emit an
`Aot/AotJson.cs` with:
  * a `JsonSerializerContext` covering the concrete root types serialised in
    that project (fully qualified, from the AOT analysis log), plus
  * an `AotJson.Configure` helper that installs the source-generated resolver
    only when running without runtime code generation (Native AOT).

The helper intentionally keeps `DefaultJsonTypeInfoResolver` as a fallback so
primitive / JsonNode serialisation is unchanged. JIT builds never enter the
branch, so behaviour is byte-for-byte identical to the pre-change build.

The TYPES dict is the single source of truth for the root-type lists; rerun this
script after the set of serialized DTOs changes. Note: wrapping the
`new JsonSerializerOptions` creation sites in `AotJson.Configure(...)` is a
one-time source edit (already applied to all 57 sites); this generator only
rewrites the `Aot/AotJson.cs` files. If you add a new options-creation site,
wrap it manually in the same way.
"""
import os

ROOT = 'src'

# Per-project root types. Keys are project directory names; values are fully
# qualified type expressions usable inside typeof(...).
TYPES = {
    'Pi.CodingAgent': [
        'Pi.CodingAgent.Core.Settings',
        'Pi.CodingAgent.Cli.DoctorReport',
        'Pi.CodingAgent.Cli.DoctorCheck',
        'Pi.CodingAgent.Cli.DoctorOptions',
        'Pi.CodingAgent.Core.CrashRecord',
        'Pi.CodingAgent.Core.ExecResult',
        'Pi.CodingAgent.Core.FileEntry',
        'Pi.CodingAgent.Core.SessionEntry',
        'Pi.CodingAgent.Core.SlashCommandInfo',
        'Pi.CodingAgent.Core.VirtualModelStateData',
        'Pi.CodingAgent.Core.BashExecutionMessage',
        'Pi.CodingAgent.Core.CustomMessage',
        'Pi.CodingAgent.Core.BranchSummaryMessage',
        'Pi.CodingAgent.Core.CompactionSummaryMessage',
        'Pi.CodingAgent.Core.Extensions.ExtensionUIDialogOptions',
        'Pi.CodingAgent.Core.Extensions.CompactOptions',
        'Pi.CodingAgent.Core.Extensions.ForkOptions',
        'Pi.CodingAgent.Core.Extensions.NavigateTreeOptions',
        'Pi.CodingAgent.Core.Extensions.NewSessionOptions',
        'Pi.CodingAgent.Core.Extensions.SwitchSessionOptions',
        'Pi.CodingAgent.Extensions.Mcp.McpServerReport',
        'Pi.CodingAgent.Modes.Rpc.RpcCommand',
        'Pi.CodingAgent.Experimental.Services.SessionWorkerOptions',
        'Pi.Ai.Types.AssistantImages',
        'Pi.Ai.Types.AssistantMessage',
        'Pi.Ai.Types.ChatMessage',
        'Pi.Ai.Types.ContentBlock',
        'Pi.Ai.Types.MessageContent',
        'Pi.Ai.Types.Model',
        'Pi.Ai.Types.ThinkingLevel',
        'Pi.Ai.Types.ToolDefinition',
        'Pi.Ai.Types.TranscriptContext',
        'Pi.Ai.Types.ClassifierQuestion',
        'Pi.Ai.Types.ClassifierResult',
        'Pi.Ai.Models.ModelSpec',
        'Pi.Ai.ModelsStoreEntry',
        'Pi.Ai.Auth.Credential',
        'Pi.Mcp.OAuth.McpOAuthState',
        'Pi.Mcp.OAuth.OAuthTypes.OAuthTokens',
        'Pi.Durable.Types.SubmissionStatus',
        'Pi.Durable.Types.TaskState',
        'Pi.Chord.Node.FacetBundleArtifact',
        'Pi.Chord.Node.FacetBundleManifest',
        'Pi.Tui.AutocompleteItem',
        'System.Collections.Generic.List<Pi.Ai.Types.ChatMessage>',
        'System.Collections.Generic.List<Pi.Ai.Types.ContentBlock>',
        'System.Collections.Generic.List<Pi.Tui.AutocompleteItem>',
        'System.Collections.Generic.List<Pi.CodingAgent.Core.CrashRecord>',
        'System.Collections.Generic.List<Pi.CodingAgent.Core.SlashCommandInfo>',
        'System.Collections.Generic.IReadOnlyList<Pi.CodingAgent.Cli.DoctorCheck>',
        'System.Collections.Generic.IReadOnlyList<Pi.Ai.Types.ChatMessage>',
        'System.Collections.Generic.IReadOnlyList<Pi.Ai.Types.ContentBlock>',
        'System.Collections.Generic.IReadOnlyList<object>',
        'System.Collections.Generic.IReadOnlyList<string>',
        'System.Collections.Generic.Dictionary<string, Pi.Ai.Auth.Credential>',
        'System.Collections.Generic.IReadOnlyDictionary<string, Pi.Ai.Auth.Credential>',
        'System.Collections.Generic.IReadOnlyDictionary<string, object>',
        'System.Collections.Generic.IReadOnlyDictionary<string, string>',
        'System.Text.Json.Nodes.JsonObject',
        'System.Text.Json.Nodes.JsonNode',
        'System.Text.Json.JsonElement',
        'object',
        'string',
        'bool',
    ],
    'Pi.Ai': [
        'Pi.Ai.Types.AssistantImages',
        'Pi.Ai.Types.AssistantMessage',
        'Pi.Ai.Types.ChatMessage',
        'Pi.Ai.Types.ContentBlock',
        'Pi.Ai.Types.MessageContent',
        'Pi.Ai.Types.Model',
        'Pi.Ai.Types.ThinkingLevel',
        'Pi.Ai.Types.ToolDefinition',
        'Pi.Ai.Types.TranscriptContext',
        'Pi.Ai.Types.ClassifierQuestion',
        'Pi.Ai.Types.ClassifierResult',
        'Pi.Ai.Models.ModelSpec',
        'Pi.Ai.ModelsStoreEntry',
        'Pi.Ai.Auth.Credential',
        'System.Collections.Generic.List<Pi.Ai.Types.ContentBlock>',
        'System.Collections.Generic.List<Pi.Ai.Types.ChatMessage>',
        'System.Collections.Generic.Dictionary<string, Pi.Ai.Auth.Credential>',
        'System.Collections.Generic.IReadOnlyDictionary<string, object>',
        'System.Text.Json.Nodes.JsonObject',
        'System.Text.Json.Nodes.JsonNode',
        'System.Text.Json.JsonElement',
        'object',
        'string',
    ],
    'Pi.Durable': [
        'Pi.Durable.Types.SubmissionStatus',
        'Pi.Durable.Types.TaskState',
        'System.Text.Json.JsonElement',
        'System.Collections.Generic.IReadOnlyList<Pi.Ai.Types.ChatMessage>',
        'System.Collections.Generic.IReadOnlyList<Pi.Ai.Types.ContentBlock>',
        'Pi.Ai.Types.AssistantMessage',
        'Pi.Ai.Types.ChatMessage',
        'object',
    ],
    'Pi.Chord': [
        'Pi.Chord.Node.FacetBundleArtifact',
        'Pi.Chord.Node.FacetBundleManifest',
        'System.Text.Json.Nodes.JsonObject',
        'System.Text.Json.Nodes.JsonNode',
        'object',
        'string',
    ],
    'Pi.Mcp': [
        'Pi.Mcp.OAuth.McpOAuthState',
        'Pi.Mcp.OAuth.OAuthTypes.OAuthTokens',
        'System.Text.Json.Nodes.JsonObject',
        'System.Text.Json.Nodes.JsonNode',
        'object',
        'string',
    ],
    'Pi.Tui': [
        'Pi.Tui.AutocompleteItem',
        'System.Text.Json.Nodes.JsonObject',
        'object',
        'string',
    ],
    'Pi.Agent': [
        'System.Text.Json.Nodes.JsonObject',
        'System.Text.Json.Nodes.JsonNode',
        'object',
        'string',
    ],
    'Pi.Codemode': [
        'System.Text.Json.Nodes.JsonObject',
        'System.Text.Json.Nodes.JsonNode',
        'object',
        'string',
    ],
    'Pi.Env': [
        'System.Text.Json.Nodes.JsonObject',
        'System.Text.Json.Nodes.JsonNode',
        'object',
        'string',
    ],
    'Pi.Evals': [
        'System.Text.Json.Nodes.JsonObject',
        'System.Text.Json.Nodes.JsonNode',
        'object',
        'string',
    ],
    'Pi.Telemetry': [
        'System.Text.Json.Nodes.JsonObject',
        'System.Text.Json.Nodes.JsonNode',
        'object',
        'string',
    ],
}

NS = {
    'Pi.CodingAgent': 'Pi.CodingAgent',
    'Pi.Ai': 'Pi.Ai',
    'Pi.Durable': 'Pi.Durable',
    'Pi.Chord': 'Pi.Chord',
    'Pi.Mcp': 'Pi.Mcp',
    'Pi.Tui': 'Pi.Tui',
    'Pi.Agent': 'Pi.Agent',
    'Pi.Codemode': 'Pi.Codemode',
    'Pi.Env': 'Pi.Env',
    'Pi.Evals': 'Pi.Evals',
    'Pi.Telemetry': 'Pi.Telemetry',
}

HEADER = '''// ============================================================================
// Native AOT JSON support (generated by scripts/gen-aot-contexts.py).
// ============================================================================
//
// Reflection-based JsonSerializer overloads cannot create their generic type
// info at runtime under Native AOT. This context provides source-generated
// metadata for every concrete root type that reaches a JsonSerializer call in
// this assembly. AotJson.Configure installs it only when
// RuntimeFeature.IsDynamicCodeSupported is false; JIT / ReadyToRun builds keep
// the original reflection resolver, so serialisation output is unchanged.
// DefaultJsonTypeInfoResolver remains in the chain as a fallback for primitive
// and JsonNode values. See docs/native-aot.md.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace {ns};

'''

for proj, types in TYPES.items():
    d = os.path.join(ROOT, proj, 'Aot')
    os.makedirs(d, exist_ok=True)
    lines = [HEADER.format(ns=NS[proj])]
    for t in types:
        lines.append(f'[JsonSerializable(typeof({t}))]\n')
    ctx = proj.replace('.', '')
    lines.append(f'internal partial class {ctx}JsonContext : JsonSerializerContext\n{{\n}}\n\n')
    lines.append('''internal static class AotJson
{
    internal static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            // Keep any resolver the caller already installed (some wire options
            // attach DefaultJsonTypeInfoResolver.Modifiers that add extra
            // ChatMessage roles). Prepend the source-generated context so POCOs
            // get static metadata, and re-apply those modifiers to its result.
            var existing = options.TypeInfoResolver;
            IJsonTypeInfoResolver chain = JsonTypeInfoResolver.Combine(
                %sJsonContext.Default,
                existing ?? new DefaultJsonTypeInfoResolver());
            if (existing is DefaultJsonTypeInfoResolver { Modifiers.Count: > 0 } withModifiers)
            {
                chain = new ModifierTypeInfoResolver(chain, withModifiers.Modifiers.ToArray());
            }

            options.TypeInfoResolver = chain;
        }

        return options;
    }
}

/// <summary>Applies DefaultJsonTypeInfoResolver.Modifiers to source-generated metadata too.</summary>
internal sealed class ModifierTypeInfoResolver : IJsonTypeInfoResolver
{
    private readonly IJsonTypeInfoResolver _inner;
    private readonly Action<JsonTypeInfo>[] _modifiers;

    internal ModifierTypeInfoResolver(IJsonTypeInfoResolver inner, Action<JsonTypeInfo>[] modifiers)
    {
        _inner = inner;
        _modifiers = modifiers;
    }

    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = _inner.GetTypeInfo(type, options);
        if (typeInfo is not null)
        {
            foreach (var modifier in _modifiers)
            {
                modifier(typeInfo);
            }
        }

        return typeInfo;
    }
}
''' % ctx)
    path = os.path.join(d, 'AotJson.cs')
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(''.join(lines))
    print('wrote', path, len(types), 'types')
