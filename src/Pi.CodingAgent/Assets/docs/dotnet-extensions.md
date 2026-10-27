# .NET Extensions

The .NET port loads extensions two ways:

- **TypeScript / JavaScript**, exactly as the upstream guide describes. The host compiles the module
  in-process (Babel on Jint) and executes it, so `~/.pi/agent/extensions/hello.ts` works without a build
  step or a Node install. This covers the upstream extension ecosystem.
- **Compiled C# assemblies** (`.dll`), for extensions written natively against the port's contracts.

`CompositeExtensionModuleLoader` picks between them by file extension. Both kinds live in the same
directories, are discovered the same way, and register against the same `IExtensionApi`.

Extensions execute inside the Pi process with that process's operating-system permissions. They can
read prompts, tool arguments, files, credentials, and session history. Load only trusted extensions.
An extension is not a security boundary.

## JavaScript / TypeScript extensions

The upstream [Extensions](extensions.md) page is the authoritative guide for the API: `pi.on()`,
`registerTool`, `registerCommand`, `ctx.ui`, and the rest. Write the extension the same way you would
for upstream pi and drop it in `.pi/extensions/`.

What the host provides:

| Area | Coverage |
|---|---|
| Compilation | TypeScript (enums, generics, `satisfies`, decorators), TSX/JSX via `__pi_jsx`, ESM in `node_modules` |
| Module resolution | relative paths, directory `index.*`, `node_modules` lookup, `package.json` `exports` / `module` / `main`, JSON modules |
| Node built-ins | `fs`, `path`, `os`, `process`, `timers`, `url`, `crypto`, `util`, `events`, `child_process`, `buffer`, `module`, `zlib`, `readline` |
| `@earendil-works/pi-ai` | `Type` (TypeBox-shaped schemas), `StringEnum`, `uuidv7`, `Value` |
| `@earendil-works/pi-coding-agent` | `defineTool`, `getAgentDir`, `withFileMutationQueue`, `keyHint`, `CONFIG_DIR_NAME`, `truncateHead` / `truncateTail` / `formatSize`, `create*Tool(Definition)` |
| `@earendil-works/pi-tui` | `Text`, `Box`, `Container`, `Spacer`, `SettingsList`, `matchesKey`, `Key`, `visibleWidth`, `truncateToWidth` |
| `pi` API | tools, commands, shortcuts, flags, `on()`, `exec`, `sendMessage`, `sendUserMessage`, `appendEntry`, session name/label, active tools, settings, model + thinking level, `pi.events` |
| `ctx` | `cwd`, `mode`, `hasUI`, `model`, `thinkingLevel`, `sessionManager`, `ui.*` (notify / select / confirm / input / editor / status / widget), command-context session actions |

Not bridged yet, and how each behaves:

| API | Behaviour |
|---|---|
| `registerMessageRenderer`, `registerEntryRenderer`, `registerToolRenderer`, `registerMarkdownTransformer` | Degrades to a warning: the extension loads, custom rendering is not applied. |
| `registerProvider`, `unregisterProvider`, `registerVirtualModel`, `registerMcpServer` | Throws. Silently ignoring these would make a user believe a provider is registered. |
| `ctx.ui.custom()` | Throws. The component protocol needs the terminal render loop in the same process. |
| `process.exit()`, `crypto.createHash` | Throws. An extension shares the host process; exiting it would kill the session. |

Notes that matter when porting an extension:

- **Promises are pumped, not awaited by a loop.** The host settles `async` factories and handlers by
  running Jint's task continuations plus a timer queue it owns. A promise waiting on something the host
  does not drive (a socket, a worker) never settles and is reported as an error rather than hanging.
- **Contexts are scoped to the callback that received them.** A `ctx` captured beyond its handler is
  stale, matching upstream's documented reload/session-replacement rule.
- **`create*ToolDefinition()` returns a permissive schema.** The real built-in schema is not marshalled
  to JavaScript yet; execution still runs the host's tool through `ctx.executeTool`.
- **No npm install step.** Packages are resolved from `node_modules` next to the extension. Install them
  the way you would for upstream pi (a `package.json` beside the extension, or `pi install`).

`Assets/examples/extensions-typescript/` holds the upstream examples unchanged and they are loadable as
is — copy one into `.pi/extensions/` to try it.

## Assembly extensions

An assembly contains exactly one public concrete implementation of `IExtensionEntry`; its `Factory` is
the .NET equivalent of a TypeScript extension's default export. `samples/Pi.Sample.Extension` is a
complete, buildable example: one tool, one slash command, a status-line update, and the project file
that keeps Pi's contract assemblies out of the output directory (`ProjectReference Private="false"`).

```bash
dotnet build samples/Pi.Sample.Extension
cp samples/Pi.Sample.Extension/bin/Debug/net10.0/Pi.Sample.Extension.dll .pi/extensions/
```

```csharp
using Pi.CodingAgent.Core.Extensions;

public sealed class HelloExtension : IExtensionEntry
{
    public ExtensionFactory Factory => pi =>
    {
        pi.RegisterCommand("hello", new CommandRegistration
        {
            Description = "Show a greeting",
            Handler = (args, ctx) =>
            {
                ctx.Ui.Notify($"Hello, {(args.Length > 0 ? args : "world")}!", "info");
                return Task.CompletedTask;
            },
        });
        return Task.CompletedTask;
    };
}
```

Assembly extensions resolve their dependencies from their own directory first, then from the host, so
contract assemblies such as `Pi.CodingAgent` stay single-identity. A `package.json` `pi.extensions`
manifest can name assembly entries alongside script entries.

The .NET eval harness has a restricted `compile_dotnet_extension` helper for its generated-extension
scenario. It accepts C# files from the isolated agent extension directory and is not a general-purpose
compiler command for interactive Pi sessions.

## Choosing between them

Use a TypeScript extension when one already exists upstream, or when you want the ecosystem's idioms
(`defineTool`, TUI components, `pi.on`). Use an assembly when the extension needs .NET libraries, must
stay strongly typed against the host contracts, or performs work that should not cross the JSON bridge
per call.
