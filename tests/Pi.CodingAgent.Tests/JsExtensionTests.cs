using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Js;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// End-to-end tests for the JavaScript extension host: real TypeScript files from the
/// upstream example corpus are loaded through Jint, transpiled with Babel and registered
/// against the same <see cref="IExtensionApi"/> a compiled extension uses.
/// </summary>
public sealed class JsExtensionTests
{
    private static readonly string ExamplesDirectory = FindExamplesDirectory();

    [Fact]
    public async Task LoadsUpstreamHelloExtension_AndRunsItsTool()
    {
        var factory = await JsExtensionModuleLoader.Instance.LoadAsync(
            Path.Combine(ExamplesDirectory, "hello.ts"));

        Assert.NotNull(factory);

        var extension = await ExtensionLoader.LoadExtensionFromFactory(
            factory!, ExamplesDirectory, EventBusController.CreateEventBus(), ExtensionLoader.CreateExtensionRuntime());

        var registered = Assert.Single(extension.Tools.Values);
        var definition = registered.Definition;
        Assert.Equal("hello", definition.Name);
        Assert.Equal("Hello", definition.Label);
        Assert.Equal("A simple greeting tool", definition.Description);

        // Type.Object({ name: Type.String() }) must arrive as plain JSON Schema, with
        // `name` in `required` because it was not wrapped in Type.Optional.
        var parameters = definition.Parameters.JsonSchema;
        Assert.Equal("object", parameters["type"]);
        var properties = Assert.IsType<Dictionary<string, object?>>(parameters["properties"]);
        Assert.True(properties.ContainsKey("name"));
        var required = Assert.IsAssignableFrom<IEnumerable<object?>>(parameters["required"]);
        Assert.Contains("name", required);

        var result = await definition.Execute(
            "call-1",
            new Dictionary<string, object?> { ["name"] = "Ada" },
            CancellationToken.None,
            null,
            null);

        Assert.False(result.IsError);
        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Equal("Hello, Ada!", text.Text);

        // `details` survives the JSON round trip.
        var details = Assert.IsType<Dictionary<string, object?>>(result.Details);
        Assert.Equal("Ada", details["greeted"]);
    }

    [Fact]
    public async Task LoadsExtensionThatUsesNodeBuiltins()
    {
        var directory = NewTempDir();
        var dataFile = Path.Combine(directory, "data.txt");
        await File.WriteAllTextAsync(dataFile, "from-node-fs");

        var extensionPath = Path.Combine(directory, "reader.ts");
        await File.WriteAllTextAsync(extensionPath, $$"""
            import { readFileSync } from "node:fs";
            import { basename } from "node:path";
            import { defineTool } from "@earendil-works/pi-coding-agent";
            import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

            export default function (pi: ExtensionAPI) {
              pi.registerTool(defineTool({
                name: "read-text",
                label: "Read text",
                description: "Reads the fixture file.",
                parameters: { type: "object", properties: {}, additionalProperties: false },
                async execute() {
                  return {
                    content: [{ type: "text", text: readFileSync({{JsonString(dataFile)}}, "utf8") + "|" + basename({{JsonString(dataFile)}}) }],
                  };
                },
              }));
            }
            """);

        var factory = await JsExtensionModuleLoader.Instance.LoadAsync(extensionPath);
        Assert.NotNull(factory);

        var extension = await ExtensionLoader.LoadExtensionFromFactory(
            factory!, directory, EventBusController.CreateEventBus(), ExtensionLoader.CreateExtensionRuntime());

        var result = await Assert.Single(extension.Tools.Values).Definition.Execute(
            "call-1", new Dictionary<string, object?>(), CancellationToken.None, null, null);

        var text = Assert.IsType<TextContent>(Assert.Single(result.Content));
        Assert.Equal("from-node-fs|data.txt", text.Text);
    }

    [Fact]
    public async Task RegistersCommandsFlagsAndEventHandlers()
    {
        var directory = NewTempDir();
        var extensionPath = Path.Combine(directory, "surface.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

            export default async function (pi: ExtensionAPI) {
              pi.registerCommand("greet", { description: "Say hi", handler: async () => {} });
              pi.registerFlag("loud", { type: "boolean", default: false });
              pi.registerShortcut("ctrl+g", { description: "Greet", handler: async () => {} });
              pi.on("session_start", async () => {});
            }
            """);

        var factory = await JsExtensionModuleLoader.Instance.LoadAsync(extensionPath);
        var extension = await ExtensionLoader.LoadExtensionFromFactory(
            factory!, directory, EventBusController.CreateEventBus(), ExtensionLoader.CreateExtensionRuntime());

        Assert.Equal("Say hi", extension.Commands["greet"].Description);
        Assert.Equal(FlagType.Boolean, extension.Flags["loud"].Type);
        Assert.True(extension.Shortcuts.ContainsKey("ctrl+g"));
        Assert.True(extension.Handlers.ContainsKey("session_start"));
    }

    [Fact]
    public async Task ModuleWithoutDefaultExport_YieldsNoFactory()
    {
        var directory = NewTempDir();
        var extensionPath = Path.Combine(directory, "no-default.ts");
        await File.WriteAllTextAsync(extensionPath, "export const value = 1;");

        var factory = await JsExtensionModuleLoader.Instance.LoadAsync(extensionPath);
        Assert.Null(factory);
    }

    [Fact]
    public async Task TranspilesTypeScriptSyntaxBeyondPlainAnnotations()
    {
        var directory = NewTempDir();
        var extensionPath = Path.Combine(directory, "syntax.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

            enum Mode { Fast = "fast", Slow = "slow" }

            interface Options { readonly mode: Mode; readonly retries?: number }

            const options = { mode: Mode.Fast } satisfies Options;

            export default function (pi: ExtensionAPI) {
              const label = `${options.mode}:${(options as Options).retries ?? 3}`;
              pi.registerCommand(label, { description: "syntax probe", handler: async () => {} });
            }
            """);

        var factory = await JsExtensionModuleLoader.Instance.LoadAsync(extensionPath);
        var extension = await ExtensionLoader.LoadExtensionFromFactory(
            factory!, directory, EventBusController.CreateEventBus(), ExtensionLoader.CreateExtensionRuntime());

        Assert.True(extension.Commands.ContainsKey("fast:3"));
    }

    [Fact]
    public void CompositeLoader_RoutesScriptsToTheJavaScriptHost()
    {
        Assert.True(CompositeExtensionModuleLoader.IsScript("a.ts"));
        Assert.True(CompositeExtensionModuleLoader.IsScript("a.tsx"));
        Assert.True(CompositeExtensionModuleLoader.IsScript("a.mjs"));
        Assert.True(CompositeExtensionModuleLoader.IsScript("a.cjs"));
        Assert.False(CompositeExtensionModuleLoader.IsScript("a.dll"));
    }

    private static string JsonString(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "pi-js-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Walk up from the test binary to the upstream examples in the source tree.</summary>
    private static string FindExamplesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "Pi.CodingAgent", "Assets", "examples", "extensions-typescript");
            if (File.Exists(Path.Combine(candidate, "hello.ts")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/Pi.CodingAgent/Assets/examples/extensions-typescript from " + AppContext.BaseDirectory);
    }
}
