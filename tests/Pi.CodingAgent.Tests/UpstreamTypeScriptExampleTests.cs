using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Js;
using Pi.CodingAgent.Core.Extensions.Types;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// End-to-end compatibility probe over the whole upstream TypeScript example corpus.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JsExtensionTests"/> and <see cref="JsExtensionRuntimeTests"/> prove the
/// host works; this class proves it works on <em>what upstream actually ships</em>.
/// Every entry point under <c>Assets/examples/extensions-typescript</c> is loaded
/// through the real loader and then driven through the real
/// <see cref="ExtensionRunner"/>: a <c>session_start</c>, two <c>tool_call</c>s and a
/// <c>context</c> pass. Loading is not enough — 18 upstream examples used to load
/// cleanly and then throw the moment an event arrived.
/// </para>
/// <para>
/// Handler failures surface through <see cref="ExtensionRunner.OnError"/>, not as
/// exceptions, so the probe subscribes to them. A run is "clean" only when the
/// extension loaded, registered and handled every dispatched event without a single
/// recorded error.
/// </para>
/// </remarks>
public sealed class UpstreamTypeScriptExampleTests(ITestOutputHelper output)
{
    /// <summary>What a single corpus entry did when loaded and driven.</summary>
    private enum Outcome
    {
        /// <summary>Loaded, registered, handled every dispatched event without error.</summary>
        Clean,

        /// <summary>Loaded, but a handler threw while an event was dispatched.</summary>
        HandlerFailed,

        /// <summary>The module could not be loaded or the factory could not be initialised.</summary>
        LoadFailed,

        /// <summary>No default export: a helper module imported by another entry point.</summary>
        NoEntryPoint,
    }

    [Fact(Timeout = 900_000)]
    public async Task UpstreamExamplesLoadAndHandleEvents()
    {
        var results = new List<(string Name, Outcome Outcome, string? Detail, ProbeResult Probe)>();

        foreach (var path in EnumerateEntryPoints())
        {
            var name = Path.GetRelativePath(ExamplesDirectory, path).Replace('\\', '/');
            var probe = await ProbeAsync(path);
            results.Add((name, probe.Outcome, probe.Detail, probe));
        }

        var clean = results.Count(r => r.Outcome == Outcome.Clean);
        var handlerFailures = results.Where(r => r.Outcome == Outcome.HandlerFailed).ToList();
        var loadFailures = results.Where(r => r.Outcome == Outcome.LoadFailed).ToList();

        foreach (var failure in loadFailures)
        {
            output.WriteLine($"LOAD-FAIL   {failure.Name}: {failure.Detail}");
        }

        foreach (var failure in handlerFailures)
        {
            output.WriteLine($"HANDLER-FAIL {failure.Name}: {failure.Detail}");
        }

        foreach (var noEntry in results.Where(r => r.Outcome == Outcome.NoEntryPoint))
        {
            output.WriteLine($"NO-ENTRY     {noEntry.Name}");
        }

        // "It loaded" is a weak claim; "it registered the things upstream registers and
        // then handled a real event with them" is the one that matters.
        var cleans = results.Where(r => r.Outcome == Outcome.Clean).ToList();
        var withHandlers = cleans.Count(r => r.Probe.Handlers > 0);
        var withTools = cleans.Count(r => r.Probe.Tools > 0);
        var withCommands = cleans.Count(r => r.Probe.Commands > 0);
        var withUi = cleans.Count(r => r.Probe.Flags + r.Probe.Shortcuts + r.Probe.Commands > 0);

        output.WriteLine("---");
        output.WriteLine($"total={results.Count} clean={clean} handler-fail={handlerFailures.Count} load-fail={loadFailures.Count} no-entry={results.Count(r => r.Outcome == Outcome.NoEntryPoint)}");
        output.WriteLine($"of the clean ones: handlers={withHandlers} tools={withTools} commands={withCommands} any-ui-surface={withUi}");
        output.WriteLine($"totals registered: handlers={cleans.Sum(r => r.Probe.Handlers)} tools={cleans.Sum(r => r.Probe.Tools)} commands={cleans.Sum(r => r.Probe.Commands)} flags={cleans.Sum(r => r.Probe.Flags)} shortcuts={cleans.Sum(r => r.Probe.Shortcuts)}");

        // Regression guard: the set of known failures is pinned. A new failure fails the
        // build (something regressed); a known failure going away also fails the build (the
        // table below is stale and should be tightened).
        var actual =
            loadFailures.Select(f => f.Name).Concat(handlerFailures.Select(f => f.Name)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var known = KnownFailures.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var regressed = actual.Except(known, StringComparer.Ordinal).ToArray();
        var newlyPassing = known.Except(actual, StringComparer.Ordinal).ToArray();

        Assert.True(
            regressed.Length == 0,
            "这些上游扩展示例现在失败了（回归）：" + Environment.NewLine + string.Join(Environment.NewLine, regressed));

        Assert.True(
            newlyPassing.Length == 0,
            "这些上游扩展示例已经通过，KnownFailures 表需要收紧：" + Environment.NewLine + string.Join(Environment.NewLine, newlyPassing));

        Assert.True(clean >= CleanBaseline, $"clean={clean} 低于基线 {CleanBaseline}。");
    }

    // ------------------------------------------------------------------ dependency resolution

    /// <summary>
    /// Four corpus entries fail purely because their npm dependency is not installed. This
    /// pins the resolver itself: a hand-written package under node_modules is found, loaded
    /// and used — no Node, no network. If this ever fails, those four are a host bug rather
    /// than a missing `npm install`.
    /// </summary>
    [Fact]
    public async Task NodeModuleResolution_ResolvesADependencyFromNodeModules()
    {
        var directory = NewTempDir();

        var packageDirectory = Path.Combine(directory, "node_modules", "demo-pkg");
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(packageDirectory, "package.json"),
            """{ "name": "demo-pkg", "version": "1.0.0", "main": "index.js" }""");
        await File.WriteAllTextAsync(
            Path.Combine(packageDirectory, "index.js"),
            """module.exports = { greet: (who) => "hi " + who };""");

        var extensionPath = Path.Combine(directory, "consumer.ts");
        await File.WriteAllTextAsync(extensionPath, """
            import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

            const pkg = require("demo-pkg");

            export default function (pi: ExtensionAPI) {
              pi.registerCommand("demo", {
                description: "uses a node_modules dependency",
                handler: async () => {
                  pi.appendEntry("demo-result", { greeting: pkg.greet("world") });
                },
              });
            }
            """);

        var entries = new List<(string Type, object? Data)>();
        var (extension, runner, errors) = await BuildRunnerAsync(
            extensionPath, await LoadFactoryAsync(extensionPath), entries);

        var command = Assert.Single(runner.GetRegisteredCommands(), entry => entry.Name == "demo");
        await command.Handler(string.Empty, runner.CreateCommandContext());

        Assert.Empty(errors);
        var appended = Assert.Single(entries);
        Assert.Equal("demo-result", appended.Type);
        var data = Assert.IsType<Dictionary<string, object?>>(appended.Data);
        Assert.Equal("hi world", data["greeting"]);
        Assert.True(extension.Commands.ContainsKey("demo"));
    }

    // ------------------------------------------------------------------ probe

    private sealed record ProbeResult(
        Outcome Outcome,
        string? Detail,
        int Handlers = 0,
        int Tools = 0,
        int Commands = 0,
        int Flags = 0,
        int Shortcuts = 0);

    private sealed record Probe(Extension Extension, ExtensionRunner Runner, List<ExtensionError> Errors);

    /// <summary>
    /// Initialises a loaded factory and binds a runner to throwing-free stubs, so a handler
    /// can be driven exactly as a session drives it.
    /// </summary>
    private static async Task<ExtensionFactory> LoadFactoryAsync(string path)
    {
        var factory = await JsExtensionModuleLoader.Instance.LoadAsync(path);
        Assert.NotNull(factory);
        return factory!;
    }

    private static async Task<Probe> BuildRunnerAsync(
        string path,
        ExtensionFactory factory,
        List<(string Type, object? Data)>? entries = null)
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var directory = Path.GetDirectoryName(path)!;

        var extension = await ExtensionLoader.LoadExtensionFromFactory(
            factory, directory, EventBusController.CreateEventBus(), runtime);

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
            new CorpusSessionManager(),
            new ModelRegistry(modelRuntime));

        var errors = new List<ExtensionError>();
        runner.OnError(error => errors.Add(error));

        runner.BindCore(
            new ExtensionActions
            {
                SendMessage = (_, _) => { },
                SendUserMessage = (_, _) => { },
                AppendEntry = (type, data) => entries?.Add((type, data)),
                SetSessionName = _ => { },
                GetSessionName = () => null,
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

        return new Probe(extension, runner, errors);
    }

    private static async Task<ProbeResult> ProbeAsync(string path)
    {
        ExtensionFactory? factory;
        try
        {
            factory = await JsExtensionModuleLoader.Instance.LoadAsync(path);
        }
        catch (Exception error)
        {
            return new ProbeResult(Outcome.LoadFailed, Summarize(error));
        }

        if (factory is null)
        {
            return new ProbeResult(Outcome.NoEntryPoint, null);
        }

        Probe probe;
        try
        {
            probe = await BuildRunnerAsync(path, factory);
        }
        catch (Exception error)
        {
            return new ProbeResult(Outcome.LoadFailed, Summarize(error));
        }

        var (extension, runner, errors) = probe;

        try
        {
            await runner.EmitAsync(new ExtensionEvent.SessionStart("startup"));

            await runner.EmitToolCallAsync(new ExtensionEvent.BashToolCall(
                "call-1",
                new Dictionary<string, object?> { ["command"] = "echo hello" }));

            await runner.EmitToolCallAsync(new ExtensionEvent.WriteToolCall(
                "call-2",
                new Dictionary<string, object?> { ["path"] = "/repo/src/app.ts", ["content"] = "hi" }));

            await runner.EmitContextAsync([new UserMessage("hello", 0)]);
        }
        catch (Exception error)
        {
            return new ProbeResult(Outcome.HandlerFailed, Summarize(error));
        }

        var registered = new ProbeResult(
            Outcome.Clean,
            null,
            Handlers: extension.Handlers.Sum(pair => pair.Value.Count),
            Tools: extension.Tools.Count,
            Commands: extension.Commands.Count,
            Flags: extension.Flags.Count,
            Shortcuts: extension.Shortcuts.Count);

        return errors.Count == 0
            ? registered
            : registered with
            {
                Outcome = Outcome.HandlerFailed,
                Detail = string.Join(" | ", errors.Select(Summarize)),
            };
    }

    private static string Summarize(ExtensionError error) => $"{error.Event}: {error.Error}";

    private static string Summarize(Exception error)
    {
        var message = error.Message.Replace("\r", " ").Replace("\n", " ");
        return message.Length > 240 ? message[..240] + "…" : message;
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "pi-upstream-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static IEnumerable<string> EnumerateEntryPoints()
    {
        // Top-level `*.ts` files are entry points; directories contribute their
        // `index.ts`. Helper modules (`utils.ts`, `doom-engine.ts`, …) are reached
        // through `require` by their owner, so they are not probed directly.
        foreach (var file in Directory.EnumerateFiles(ExamplesDirectory, "*.ts").OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return file;
        }

        foreach (var directory in Directory.EnumerateDirectories(ExamplesDirectory).OrderBy(p => p, StringComparer.Ordinal))
        {
            var index = Path.Combine(directory, "index.ts");
            if (File.Exists(index))
            {
                yield return index;
            }
        }
    }

    // ------------------------------------------------------------------ pinned state

    /// <summary>
    /// Entry points that cannot run here, by design or by missing dependency. Each one is a
    /// deliberate decision, not an oversight — see <c>docs/dotnet-extensions.md</c>.
    /// </summary>
    private static readonly string[] KnownFailures =
    [
        // --- Missing npm dependencies ------------------------------------------------
        // These four ship a package.json + package-lock.json and no node_modules
        // (.gitignore'd upstream too): `npm install` in the example directory is what
        // makes them run, in upstream as well. The host resolves node_modules fine —
        // `NodeModuleResolution_ResolvesADependencyFromNodeModules` proves that with a
        // hand-written package.
        "custom-provider-anthropic/index.ts", // @anthropic-ai/sdk
        "gondolin/index.ts",                  // @earendil-works/gondolin
        "sandbox/index.ts",                   // @anthropic-ai/sandbox-runtime
        "with-deps/index.ts",                 // ms

        // --- Registration APIs that need the streaming protocol bridge ---------------
        // Registering a provider / virtual model means handing the model stream to JS and
        // back. Silently ignoring it would make the user think the model is wired up, so
        // the host throws instead. Tracked in docs/dotnet-extensions.md.
        "custom-provider-gitlab-duo/index.ts", // pi.registerProvider
        "jev-router.ts",                       // pi.registerVirtualModel

        // --- Architecture ------------------------------------------------------------
        // Extensions only run while a callback is on the stack, so a watcher could never
        // deliver into an idle extension. The error says so and suggests polling.
        "file-trigger.ts",

        // --- Platform ----------------------------------------------------------------
        // Shells out to `osascript`, which only exists on macOS. Upstream on Linux/Windows
        // fails the same way.
        "mac-system-theme.ts",
    ];

    /// <summary>Clean entry points at the time the table was pinned.</summary>
    private const int CleanBaseline = 71;

    private static readonly string ExamplesDirectory = FindExamplesDirectory();

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

    private sealed class CorpusSessionManager : IReadonlySessionManager
    {
        public string SessionId => "corpus-session";

        public string? SessionFile => null;

        public IReadOnlyList<SessionEntry> Entries => [];
    }
}
