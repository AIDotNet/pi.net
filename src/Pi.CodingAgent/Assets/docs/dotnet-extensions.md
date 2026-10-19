# .NET Extensions

The .NET port loads compiled C# assemblies; it does not compile C# or TypeScript source when an
extension is loaded. The TypeScript extension guide describes the source-runtime API. Use this page
for the assembly entry contract and C# tool implementation.

Extensions execute inside the Pi process with that process's operating-system permissions. They can
read prompts, tool arguments, files, credentials, and session history. Load only trusted assemblies.
An extension is not a security boundary.

## Create an assembly extension

An assembly must contain exactly one public concrete implementation of `IExtensionEntry`. Its
`Factory` is the .NET equivalent of a TypeScript extension's default export:

```csharp
using Pi.CodingAgent.Core.Extensions;

public sealed class HelloExtension : IExtensionEntry
{
    public ExtensionFactory Factory => static _ => Task.CompletedTask;
}
```

Pi discovers direct `*.dll` files in an extensions directory. A subdirectory can provide
`index.dll`; a `package.json` `pi.extensions` manifest can also name assembly entries. A DLL is
loaded as an assembly, not interpreted as a script.

## Register a tool

`IExtensionApi.RegisterTool` accepts a `ToolDefinition`. The parameter schema is JSON Schema, and
the executor returns an `AgentToolResult`:

```csharp
using Pi.Ai.Types;
using Pi.Agent.Types;
using Pi.CodingAgent.Core.Extensions;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

public sealed class HelloExtension : IExtensionEntry
{
    public ExtensionFactory Factory => pi =>
    {
        pi.RegisterTool(new ToolDefinition
        {
            Name = "hello",
            Label = "Say hello",
            Description = "Return a greeting for a name.",
            Parameters = new ToolSchema(new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["name"] = new Dictionary<string, object?> { ["type"] = "string" },
                },
                ["required"] = new[] { "name" },
                ["additionalProperties"] = false,
            }),
            Execute = (callId, arguments, cancellationToken, update, context) =>
            {
                if (!arguments.TryGetValue("name", out var value) || value is not string name)
                {
                    return Task.FromResult(AgentToolResult.FromError("A name is required."));
                }

                return Task.FromResult(
                    new AgentToolResult([new TextContent($"Hello, {name}!")]));
            },
        });
        return Task.CompletedTask;
    };
}
```

Use the extension contracts in `Pi.CodingAgent.Core.Extensions` and the content/result types in
`Pi.Ai.Types` and `Pi.Agent.Types`. Keep host contract assemblies resolved from Pi rather than
shipping private copies beside the extension. If an extension has private dependencies, place those
dependencies with the extension and verify that the loader can resolve them.

The .NET eval harness has a restricted `compile_dotnet_extension` helper for its generated-extension
scenario. It accepts C# files from the isolated agent extension directory and is not a general-purpose
compiler command for interactive Pi sessions.

## Provider and lifecycle APIs

The same `IExtensionEntry.Factory` can register commands, shortcuts, flags, and event handlers
through `IExtensionApi`. Use the public .NET contract types rather than copying TypeScript callback
signatures. The TypeScript [custom-provider guide](custom-provider.md) describes provider protocol
design, but its code samples are not C# and must not be copied verbatim.

For provider configuration, prefer `models.json` when the remote service uses an API Pi already
supports. For an unsupported protocol, implement against the .NET provider registration contracts
available in the referenced Pi assemblies and validate real requests and streaming behavior with a
fixture server.
