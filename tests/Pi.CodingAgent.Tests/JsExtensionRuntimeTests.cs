using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Js;
using Pi.CodingAgent.Core.Extensions.Types;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Runtime-level tests: real upstream extensions are loaded and their handlers are
/// dispatched through <see cref="ExtensionRunner"/>, so event payloads, contexts and
/// results cross the JavaScript bridge exactly as they do in a session.
/// </summary>
/// <remarks>
/// <see cref="JsExtensionTests"/> stops at "the extension registered what it says it
/// registers". These tests start where that leaves off: a guard extension returning
/// <c>{ block: true }</c> from a <c>tool_call</c> handler has to actually block, and a
/// handler that calls <c>pi.*</c> has to reach the bound API after the factory returned.
/// </remarks>
public sealed class JsExtensionRuntimeTests
{
    private static readonly string ExamplesDirectory = FindExamplesDirectory();

    [Fact]
    public async Task ProtectedPaths_BlocksAWriteThroughTheRealRunner()
    {
        // The extension returns undefined unless `event.toolName === "write"`, so a block
        // here proves the event shape and the result mapping both work — not just that
        // the handler ran.
        var harness = await CreateRunnerAsync(Path.Combine(ExamplesDirectory, "protected-paths.ts"));

        var result = await harness.Runner.EmitToolCallAsync(new ExtensionEvent.WriteToolCall(
            "call-1",
            new Dictionary<string, object?> { ["path"] = "/repo/.env" }));

        Assert.NotNull(result);
        Assert.True(result!.Block);
        Assert.Contains(".env", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectedPaths_IgnoresNonMatchingToolAndPath()
    {
        var harness = await CreateRunnerAsync(Path.Combine(ExamplesDirectory, "protected-paths.ts"));

        var allowedPath = await harness.Runner.EmitToolCallAsync(new ExtensionEvent.WriteToolCall(
            "call-1",
            new Dictionary<string, object?> { ["path"] = "/repo/src/app.ts" }));
        Assert.Null(allowedPath);

        // toolName is "bash" here, so the guard bails out even though the input mentions .env.
        var otherTool = await harness.Runner.EmitToolCallAsync(new ExtensionEvent.BashToolCall(
            "call-2",
            new Dictionary<string, object?> { ["command"] = "echo .env" }));
        Assert.Null(otherTool);
    }

    [Fact]
    public async Task PermissionGate_BlocksADangerousCommand()
    {
        var harness = await CreateRunnerAsync(Path.Combine(ExamplesDirectory, "permission-gate.ts"));

        var result = await harness.Runner.EmitToolCallAsync(new ExtensionEvent.BashToolCall(
            "call-1",
            new Dictionary<string, object?> { ["command"] = "sudo rm -rf /" }));

        Assert.NotNull(result);
        Assert.True(result!.Block);
    }

    [Fact]
    public async Task CommandHandler_ReachesTheApiAndTheSessionManager()
    {
        // After the factory returned, the api must still be bound and the context must
        // expose a usable sessionManager. Both used to fail: the api was unbound in a
        // finally block, and sessionManager was a payload object with string markers.
        var directory = NewTempDir();
        var extensionPath = Path.Combine(directory, "probe.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

            export default function (pi: ExtensionAPI) {
              pi.registerFlag("probe-flag", { type: "boolean", default: true });
              pi.registerCommand("probe", {
                description: "exercise the host api",
                handler: async (_args, ctx) => {
                  const tools = pi.getAllTools();
                  if (!Array.isArray(tools)) throw new Error("getAllTools did not return an array");

                  const entries = ctx.sessionManager.getBranch();
                  if (!Array.isArray(entries)) throw new Error("getBranch did not return an array");

                  if (typeof ctx.sessionManager.getSessionId() !== "string") {
                    throw new Error("getSessionId did not return a string");
                  }

                  pi.setSessionName("probe-ok");
                  pi.appendEntry("probe-result", {
                    cwd: ctx.cwd,
                    entries: entries.length,
                    flag: pi.getFlag("probe-flag"),
                  });
                },
              });
            }
            """);

        var harness = await CreateRunnerAsync(extensionPath);
        var command = Assert.Single(harness.Runner.GetRegisteredCommands(), entry => entry.Name == "probe");

        await command.Handler(string.Empty, harness.Runner.CreateCommandContext());

        Assert.Equal("probe-ok", harness.Recorder.SessionName);
        var appended = Assert.Single(harness.Recorder.Entries);
        Assert.Equal("probe-result", appended.Type);
        var data = Assert.IsType<Dictionary<string, object?>>(appended.Data);
        Assert.Equal(directory, data["cwd"]);
        Assert.Equal(0d, data["entries"]);
        Assert.Equal(true, data["flag"]);
    }

    [Fact]
    public async Task Unsubscribe_StopsTheHandler()
    {
        var directory = NewTempDir();
        var extensionPath = Path.Combine(directory, "one-shot.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

            export default function (pi: ExtensionAPI) {
              const off = pi.on("tool_call", async () => ({ block: true, reason: "should not fire" }));
              off();
            }
            """);

        var harness = await CreateRunnerAsync(extensionPath);

        var result = await harness.Runner.EmitToolCallAsync(new ExtensionEvent.WriteToolCall(
            "call-1",
            new Dictionary<string, object?> { ["path"] = "/repo/.env" }));

        Assert.Null(result);
    }

    [Fact]
    public async Task ToolExecute_ReceivesTheContextAndStreamsUpdates()
    {
        var directory = NewTempDir();
        var extensionPath = Path.Combine(directory, "streaming.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

            export default function (pi: ExtensionAPI) {
              pi.registerTool({
                name: "streamer",
                label: "Streamer",
                description: "Emits one partial result, then a final one.",
                parameters: { type: "object", properties: {}, additionalProperties: false },
                async execute(_id, _params, _signal, onUpdate, ctx) {
                  if (typeof onUpdate !== "function") throw new Error("onUpdate missing");
                  if (!ctx || typeof ctx.cwd !== "string") throw new Error("tool ctx missing");
                  await onUpdate({ content: [{ type: "text", text: "partial" }] });
                  return { content: [{ type: "text", text: "final:" + ctx.cwd }] };
                },
              });
            }
            """);

        var harness = await CreateRunnerAsync(extensionPath);
        var tool = harness.Extension.Tools["streamer"].Definition;

        var updates = new List<string>();
        var result = await tool.Execute(
            "call-1",
            new Dictionary<string, object?>(),
            CancellationToken.None,
            partial =>
            {
                updates.Add(Assert.IsType<TextContent>(partial.Content[0]).Text);
                return Task.CompletedTask;
            },
            harness.Runner.CreateToolContext("call-1", CancellationToken.None));

        Assert.Equal(["partial"], updates);
        Assert.Equal("final:" + directory, Assert.IsType<TextContent>(result.Content[0]).Text);
    }

    [Fact]
    public async Task EventsBus_DeliversBetweenExtensions()
    {
        // Each extension owns an engine, so `pi.events` is a host-side bus: a handler in
        // one extension has to be reachable from another one's emit.
        var directory = NewTempDir();
        var subscriber = Path.Combine(directory, "subscriber.ts");
        await File.WriteAllTextAsync(subscriber, """
            import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

            export default function (pi: ExtensionAPI) {
              pi.events.on("demo:ping", (data) => {
                pi.setSessionName("got:" + data.message);
              });
            }
            """);

        var publisher = Path.Combine(directory, "publisher.ts");
        await File.WriteAllTextAsync(publisher, """
            import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

            export default function (pi: ExtensionAPI) {
              pi.on("session_start", async () => {
                pi.events.emit("demo:ping", { message: "hi" });
              });
            }
            """);

        var subscriberHarness = await CreateRunnerAsync(subscriber);
        var publisherHarness = await CreateRunnerAsync(publisher);

        await publisherHarness.Runner.EmitAsync(new ExtensionEvent.SessionStart("startup"));

        Assert.Equal("got:hi", subscriberHarness.Recorder.SessionName);
    }

    // ------------------------------------------------------------------ harness

    /// <summary>What the bound host actions recorded, so a handler's effect is observable.</summary>
    private sealed class Recorder
    {
        public List<(string Type, object? Data)> Entries { get; } = [];

        public string? SessionName { get; set; }
    }

    private sealed record Harness(
        ExtensionRunner Runner,
        IExtensionRuntime Runtime,
        Extension Extension,
        Recorder Recorder);

    private static async Task<Harness> CreateRunnerAsync(string extensionPath)
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var factory = await JsExtensionModuleLoader.Instance.LoadAsync(extensionPath);
        Assert.NotNull(factory);

        var directory = Path.GetDirectoryName(extensionPath)!;
        var extension = await ExtensionLoader.LoadExtensionFromFactory(
            factory!,
            directory,
            EventBusController.CreateEventBus(),
            runtime);

        var modelRuntime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new Pi.Ai.Auth.InMemoryCredentialStore(),
            ModelsPathDisabled = true,
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            RefreshOnCreate = false,
            Env = _ => null,
            AuthContext = new StubAuthContext(_ => null),
        });

        var runner = new ExtensionRunner(
            [extension],
            runtime,
            directory,
            new StubSessionManager(),
            new ModelRegistry(modelRuntime));

        var recorder = new Recorder();
        runner.BindCore(
            new ExtensionActions
            {
                SendMessage = (_, _) => { },
                SendUserMessage = (_, _) => { },
                AppendEntry = (type, data) => recorder.Entries.Add((type, data)),
                SetSessionName = name => recorder.SessionName = name,
                GetSessionName = () => recorder.SessionName,
                SetLabel = (_, _) => { },
                GetActiveTools = () => [],
                GetAllTools = () => [],
                GetSettings = () => new Settings(),
                SetActiveTools = _ => { },
                RefreshTools = () => { },
                GetCommands = () => [],
                SetModel = _ => Task.FromResult(true),
                GetThinkingLevel = () => ThinkingLevel.Off,
                SetThinkingLevel = _ => { },
            },
            new ExtensionContextActions
            {
                GetModel = () => null,
                GetScopedModels = () => [],
                IsIdle = () => true,
                IsProjectTrusted = () => true,
                GetSignal = () => null,
                Abort = () => { },
                HasPendingMessages = () => false,
                Shutdown = () => { },
                GetContextUsage = () => null,
                Compact = _ => { },
                GetSystemPrompt = () => "system prompt",
            });

        return new Harness(runner, runtime, extension, recorder);
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "pi-js-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindExamplesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "Pi.CodingAgent", "Assets", "examples", "extensions-typescript");
            if (File.Exists(Path.Combine(candidate, "protected-paths.ts")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("examples not found");
    }

    private sealed class StubSessionManager : IReadonlySessionManager
    {
        public string SessionId => "session";

        public string? SessionFile => null;

        public IReadOnlyList<SessionEntry> Entries => [];
    }
}
