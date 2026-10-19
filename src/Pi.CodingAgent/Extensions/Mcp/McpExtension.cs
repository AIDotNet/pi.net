// ============================================================================
// Built-in MCP integration — port of extensions/mcp/index.ts (P4b-6)
// ============================================================================
//
// Connects the servers from `mcp.json` and the servers extensions register with
// `pi.registerMcpServer()` when a session starts, and servers registered later right away. A server
// in `mcp.json` takes precedence over a registered server of the same name. Connections run in the
// background: the first prompt waits only for servers with `direct` tools, and codemode scripts,
// `tool_search`, and the resource tools wait for the servers they need when they run. Tools are
// registered as `mcp__<server>__<tool>`. By default (`"exposure": "codemode"`) the tools are only
// callable from codemode scripts, which keeps MCP tools out of the model's tool declarations and
// the codemode description: scripts find the tools with `searchTools()` and the server instructions
// with `describeNamespace()`. The codemode tool is activated for that unless `autoEnableCodemode` is
// false. `"deferred"` declares the tools to the model once the `tool_search` tool loads them, and
// activates `tool_search` instead of codemode. `"exposure": "direct"` declares them to the model
// right away, and `"hidden"` makes them unreachable. `toolExposure` overrides the exposure of single
// tools. Servers with resources are reached through Codex's `list_mcp_resources`,
// `list_mcp_resource_templates`, and `read_mcp_resource` tools (Resources.cs).
//
// Every call runs through pi's tool pipeline, so `tool_call`/`tool_result` hooks and permission
// extensions apply to MCP tools the same way they do to built-in tools.
//
// Problems found at startup (config errors, failed connections, servers that need a sign-in) are
// reported once. `/mcp` opens a manager to sign in, reconnect, enable or disable servers, and change
// their exposure; the last two are saved to the `mcp.json` that defines the server, or apply to the
// current session for registered servers.
//
// Differences from the TS source, all registered in docs/coding-agent-porting-status.md:
//   C189  the port has no lazy module load (`runtime.lazy.ts`), so only the event-loop turn that
//         `setImmediate` bought is kept (`DeferOneTurnAsync`).
//   C190  `McpServerListing.Connection` is flattened to `Instructions` (TS structural `{instructions?}`).
//   C191  `McpExtensionOptions.ShowManager` remains injectable, with `McpManagerView` as its default.
//   C192  the sign-in token snapshot is a `Task<string>` because the port's credential store reads
//         asynchronously (TS `credentials.tokens()` is synchronous).
//   C154's neighbour  the port always emits `ExtensionEvent.CustomToolCall` for `tool_call`, so the
//         handler reads the tool name and input from that variant instead of the TS union's
//         `event.toolName` / `event.input`.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Extensions.Codemode;
using Pi.CodingAgent.Extensions.ToolSearch;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
// The class name `ToolSearch` collides with the sibling namespace `Pi.CodingAgent.Extensions.ToolSearch`,
// which wins over the type in this scope; the alias matches Extensions/Codemode/CodemodeExecute.cs.
using ToolSearchApi = Pi.CodingAgent.Extensions.ToolSearch.ToolSearch;

namespace Pi.CodingAgent.Extensions.Mcp;

/// <summary>
/// Options of <see cref="McpExtension.CreateMcpExtension"/>. Port of the TS
/// <c>McpExtensionOptions</c>.
/// </summary>
public sealed record McpExtensionOptions
{
    /// <summary>Defaults to reading <c>mcp.json</c> from the agent directory and the trusted project.</summary>
    public Func<IExtensionContext, LoadedMcpConfig>? LoadConfig { get; init; }

    /// <summary>Defaults to stdio and streamable HTTP transports built from the server config.</summary>
    public McpTransportFactory? CreateTransport { get; init; }

    /// <summary>Defaults to <c>mcp-auth.json</c> in the agent directory.</summary>
    public McpOAuth.McpOAuthCredentialStore? Credentials { get; init; }

    /// <summary>File server log messages are appended to. Defaults to <c>mcp.log</c> in the agent directory.</summary>
    public string? LogPath { get; init; }

    /// <summary>Opens the OAuth authorization URL. Defaults to the platform browser.</summary>
    public Action<string>? OpenUrl { get; init; }

    /// <summary>
    /// Saves <c>/mcp</c> changes to the server's config file: its project <c>override</c> when set,
    /// else its <c>source</c>. Defaults to editing that <c>mcp.json</c>.
    /// </summary>
    public Action<McpServerEntry, McpServerConfigPatch>? UpdateConfig { get; init; }

    /// <summary>
    /// How long the first prompt waits for servers with <c>direct</c> tools that are still connecting
    /// at startup, in milliseconds. Their tools become available when they connect. Other servers are
    /// waited for when a script or search needs them. Default: 10000.
    /// </summary>
    public long? StartupWaitMs { get; init; }

    /// <summary>
    /// Runs <c>manage</c> in the manager view. TS <c>showMcpManager</c>. Tests and hosts may replace
    /// the built-in TUI view.
    /// </summary>
    public Func<IExtensionCommandContext, Func<IMcpUi, Task>, Task> ShowManager { get; init; }
        = McpManagerView.ShowMcpManagerAsync;
}

/// <summary>
/// A configured server. Disabled servers have no connection. TS <c>McpServer</c>.
/// </summary>
/// <remarks>
/// Difference C193: TS declares the shape inline with optional members. The port names it and
/// exposes it to the test project, because <see cref="McpExtension.FormatStatus"/> and
/// <see cref="McpExtension.DescribeState"/> are the only way to check a connection's reported state
/// without a terminal.
/// </remarks>
internal sealed class McpServer
{
    public required McpServerEntry Entry { get; set; }

    public McpServerConnection? Connection { get; set; }

    /// <summary>For servers extensions registered: the config as registered, to detect re-registrations.</summary>
    public string? RegisteredConfig { get; set; }

    /// <summary>Result of the last <c>/mcp</c> action that failed, shown in the manager.</summary>
    public string? Message { get; set; }

    /// <summary>Settles when the connection started for the server connected or failed.</summary>
    public Task? Ready { get; set; }
}

/// <summary>
/// What the <c>mcp_servers</c> section needs of a server. TS <c>McpServerListing</c>.
/// </summary>
/// <remarks>
/// Difference C190: TS types <c>connection</c> structurally as <c>{ instructions?: string }</c>.
/// C# has no structural types, so the only member that is read is flattened into
/// <see cref="Instructions"/> and callers pass <c>server.Connection?.Instructions</c>.
/// </remarks>
public sealed record McpServerListing(McpServerEntry Entry, string? Instructions = null);

/// <summary>Port of <c>extensions/mcp/index.ts</c>.</summary>
public static partial class McpExtension
{
    /// <summary>Name of the system prompt section that lists the servers whose tools are not declared.</summary>
    public const string McpServersSection = "mcp_servers";

    /// <summary>
    /// Characters of the whole section. Descriptions shrink to fit; when the server lines alone do not
    /// fit, the last servers are left out and counted in a closing line.
    /// </summary>
    public const int MaxServersSectionChars = 4096;

    /// <summary>Characters of a server description in the section, as Codex allows for deferred namespaces.</summary>
    private const int MaxServerDescriptionChars = 250;

    private const long DefaultStartupWaitMs = 10_000;

    private const string McpUsage =
        "Usage: /mcp, /mcp login [server], /mcp logout [server], /mcp reconnect [server]";

    /// <summary>The exposure whose tools codemode reaches, as the section names it.</summary>
    private const string CodemodeReach = "codemode";

    /// <summary>The exposure whose tools <c>tool_search</c> reaches, as the section names it.</summary>
    private const string ToolSearchReach = "tool_search";

    /// <summary>Names of the tools the MCP extension registers for a server's resources.</summary>
    private static readonly HashSet<string> ResourceToolNames = new(StringComparer.Ordinal)
    {
        McpServers.ToolNames.ListMcpResources,
        McpServers.ToolNames.ListMcpResourceTemplates,
        McpServers.ToolNames.ReadMcpResource,
    };

    /// <summary>
    /// How each exposure is described in the manager, in the order the exposure menu lists them.
    /// TS <c>EXPOSURE_DESCRIPTIONS</c> (a record keyed by <c>Exclude&lt;McpExposure, "hidden"&gt;</c>).
    /// </summary>
    private static readonly (string Exposure, string Description)[] ExposureDescriptions =
    [
        (McpServers.Exposures.Codemode, "called from codemode scripts, which find them with searchTools()"),
        (McpServers.Exposures.Deferred,
            "not declared until tool_search loads them, then called directly; no codemode needed"),
        (McpServers.Exposures.Direct, "declared to the model like built-in tools"),
    ];

    // ---------------------------------------------------------------------------------------
    // Leaf helpers (pure, so they can be checked without a session)
    // ---------------------------------------------------------------------------------------

    /// <summary>TS <c>firstLine</c>: everything before the first newline.</summary>
    internal static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        return index < 0 ? text : text[..index];
    }

    /// <summary>TS <c>isEnabled</c>: only an explicit <c>false</c> disables a server.</summary>
    internal static bool IsEnabled(McpServer server) => server.Entry.Config.Enabled != false;

    /// <summary>TS <c>isEnabled</c> over the section's listing shape.</summary>
    internal static bool IsEnabled(McpServerListing server) => server.Entry.Config.Enabled != false;

    /// <summary>TS <c>exposureOf</c>: a server's exposure, defaulting to <c>codemode</c>.</summary>
    internal static string ExposureOf(McpServerEntry entry) =>
        entry.Config.Exposure ?? McpServers.Exposures.Codemode;

    /// <summary>Exposures the server's tools can have, known from its config before it connects.</summary>
    internal static HashSet<string> ConfiguredExposures(McpServerEntry entry)
    {
        var exposures = new HashSet<string>(StringComparer.Ordinal) { ExposureOf(entry) };
        foreach (var (_, value) in entry.Config.ToolExposure ?? new JsonObject())
        {
            if (value is JsonValue candidate && candidate.TryGetValue<string>(out var exposure))
            {
                exposures.Add(exposure);
            }
        }

        return exposures;
    }

    /// <summary>Whether some of the server's tools are declared to the model, so the first prompt waits for them.</summary>
    internal static bool HasDirectTools(McpServerEntry entry) =>
        ConfiguredExposures(entry).Contains(McpServers.Exposures.Direct);

    /// <summary>Whether some of the server's tools are reached through codemode or tool_search.</summary>
    internal static bool HasIndirectTools(McpServerEntry entry)
    {
        var exposures = ConfiguredExposures(entry);
        return exposures.Contains(McpServers.Exposures.Codemode)
            || exposures.Contains(McpServers.Exposures.Deferred);
    }

    /// <summary>The section's first line. It explains only the ways of reaching tools that the listed servers use.</summary>
    internal static string ServersSectionIntro(IReadOnlySet<string> reaches)
    {
        var intro = "MCP servers whose tools are not declared to you.";
        if (reaches.Contains(CodemodeReach))
        {
            intro += " Call the tools of `codemode` servers from codemode scripts.";
        }

        if (reaches.Contains(ToolSearchReach))
        {
            intro += " Load the tools of `tool_search` servers with `tool_search`.";
        }

        return intro;
    }

    /// <summary>TS <c>truncate</c>: the text itself, or its head plus an ellipsis.</summary>
    internal static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        return max <= 1 ? "" : $"{text[..(max - 1)].TrimEnd()}…";
    }

    /// <summary>First line of the configured description, or of the server instructions once connected.</summary>
    internal static string ServerSummary(McpServerListing server)
    {
        var configured = server.Entry.Config.Description?.Trim();
        var text = string.IsNullOrEmpty(configured) ? server.Instructions ?? "" : configured;
        return FirstLine(text).Trim();
    }

    /// <summary>
    /// The <c>mcp_servers</c> section: every enabled server with codemode or deferred tools, with how
    /// its tools are reached and a one-line summary. The model learns of the servers from it, since
    /// neither codemode nor tool_search lists them. Null when there are no such servers.
    /// </summary>
    public static string? RenderServersSection(IReadOnlyList<McpServerListing> servers)
    {
        var listed = servers
            .Where(server => IsEnabled(server) && HasIndirectTools(server.Entry))
            .OrderBy(server => server.Entry.Name, StringComparer.CurrentCulture)
            .ToList();
        if (listed.Count == 0)
        {
            return null;
        }

        var reaches = listed
            .Select(server => ConfiguredExposures(server.Entry).Contains(McpServers.Exposures.Codemode)
                ? CodemodeReach
                : ToolSearchReach)
            .ToList();
        var intro = ServersSectionIntro(new HashSet<string>(reaches, StringComparer.Ordinal));
        var heads = listed
            .Select((server, index) => $"- {McpServers.Namespace(server.Entry.Name)} ({reaches[index]})")
            .ToList();

        List<string> Omitted(int count) => count > 0
            ? [$"- … {count} more server{(count == 1 ? "" : "s")}; find their tools with searchTools()"]
            : [];

        // Characters of the intro, the first `kept` server lines without descriptions, and the omission line.
        int Size(int kept) => string.Join("\n", [intro, .. heads.Take(kept), .. Omitted(listed.Count - kept)]).Length;
        var kept = listed.Count;
        while (kept > 0 && Size(kept) > MaxServersSectionChars)
        {
            kept--;
        }

        // Each description also takes a ": " separator.
        var perServer = kept == 0
            ? 0
            : Math.Min(
                MaxServerDescriptionChars,
                (int)Math.Floor((MaxServersSectionChars - Size(kept)) / (double)kept) - 2);
        var lines = new List<string>();
        for (var index = 0; index < kept; index++)
        {
            var summary = perServer > 0 ? Truncate(ServerSummary(listed[index]), perServer) : "";
            lines.Add(summary.Length > 0 ? $"{heads[index]}: {summary}" : heads[index]);
        }

        return string.Join("\n", [intro, .. lines, .. Omitted(listed.Count - kept)]);
    }

    /// <summary>
    /// Whether a codemode script needs the server: it names the server's namespace, or searches,
    /// enumerates, or describes tools or namespaces, which may name the server in other forms.
    /// </summary>
    internal static bool ScriptNeedsServer(string code, string server)
    {
        if (CodemodeCallRegex().IsMatch(code))
        {
            return true;
        }

        return code.Contains(McpServers.Namespace(server), StringComparison.Ordinal);
    }

    /// <summary>Short state for lists and the startup report. <paramref name="withError"/> appends the first line of a failure.</summary>
    internal static string DescribeState(McpServer server, bool withError = true)
    {
        if (!IsEnabled(server))
        {
            return "disabled";
        }

        var connection = server.Connection;
        if (connection is null)
        {
            return "starting";
        }

        return connection.State switch
        {
            McpServerState.NeedsAuth => "needs sign-in",
            McpServerState.Failed => withError ? $"failed: {FirstLine(connection.Error ?? "unknown error")}" : "failed",
            McpServerState.Connected => ConnectedState(connection),
            McpServerState.Connecting => "connecting…",
            _ => McpServerStates.Name(connection.State),
        };
    }

    private static string ConnectedState(McpServerConnection connection)
    {
        var resources = connection.Resources.Count;
        var resourceCount = resources > 0 ? $" · {resources} resource{(resources == 1 ? "" : "s")}" : "";
        var tools = connection.Tools.Count;
        return $"connected · {tools} tool{(tools == 1 ? "" : "s")}{resourceCount}";
    }

    /// <summary>Servers that need the user first.</summary>
    internal static int AttentionRank(McpServer server)
    {
        if (!IsEnabled(server))
        {
            return 5;
        }

        return server.Connection?.State switch
        {
            McpServerState.NeedsAuth => 0,
            McpServerState.Failed => 1,
            McpServerState.Disconnected => 2,
            McpServerState.Connected => 4,
            _ => 3,
        };
    }

    /// <summary>The command line or URL a server is reached at.</summary>
    internal static string DescribeTransport(McpServerEntry entry)
    {
        var config = entry.Config;
        if (config.Url is not null)
        {
            return config.Url;
        }

        return string.Join(" ", new[] { config.Command ?? "" }.Concat(config.Args ?? []));
    }

    /// <summary>The manager's description of an exposure, or the exposure itself when it has none.</summary>
    internal static string ExposureDescription(string exposure) =>
        ExposureDescriptions.FirstOrDefault(entry => entry.Exposure == exposure).Description ?? exposure;

    /// <summary>
    /// The <c>/mcp</c> status lines. TS reads the closure's <c>servers</c> / <c>configErrors</c> /
    /// <c>overridden</c>; the port passes them explicitly so the rendering can be checked on its own
    /// (the leaf-first device used throughout this port).
    /// </summary>
    internal static string FormatStatus(
        IReadOnlyList<McpServer> servers,
        IReadOnlyList<string> configErrors,
        IReadOnlyList<string> overridden)
    {
        if (servers.Count == 0 && configErrors.Count == 0 && overridden.Count == 0)
        {
            return $"No MCP servers configured. Add them to {ResolveMcpJson()} or .pi/mcp.json.";
        }

        var lines = new List<string>();
        foreach (var server in servers)
        {
            var name = server.Entry.Name;
            var exposure = ExposureOf(server.Entry);
            var connection = server.Connection;
            if (connection?.State == McpServerState.NeedsAuth)
            {
                lines.Add($"{name}: needs sign-in, run /mcp login {name} ({exposure})");
                continue;
            }

            var tools = connection?.State == McpServerState.Connected ? $", {connection.Tools.Count} tools" : "";
            var state = !IsEnabled(server)
                ? "disabled"
                : connection?.State == McpServerState.Disconnected
                    ? "disconnected, reconnects on next call"
                    : connection is null ? "starting" : McpServerStates.Name(connection.State);
            var error = connection?.Error is not null && connection.State != McpServerState.Connected
                ? $"\n    {connection.Error.Replace("\n", "\n    ", StringComparison.Ordinal)}"
                : "";
            lines.Add($"{name}: {state}{tools} ({exposure}){error}");
        }

        lines.AddRange(configErrors.Select(error => $"config error: {error}"));
        lines.AddRange(overridden.Select(line => $"overridden: {line}"));
        return string.Join("\n", lines);
    }

    /// <summary>The global <c>mcp.json</c>, as the empty-state messages spell it out.</summary>
    private static string ResolveMcpJson() => NodePath.Resolve(Config.GetAgentDir(), "mcp.json");

    /// <summary>TS <c>/\b(searchTools|describeNamespace|describeTool|ALL_TOOLS)\b/</c>.</summary>
    [GeneratedRegex(@"\b(searchTools|describeNamespace|describeTool|ALL_TOOLS)\b")]
    private static partial Regex CodemodeCallRegex();

    /// <summary>TS <c>/^mcp__(.+?)__(.+)$/</c>, used to render calls to a tool of a server that never connected.</summary>
    [GeneratedRegex(@"^mcp__(.+?)__(.+)$")]
    private static partial Regex McpToolNameRegex();

    // ---------------------------------------------------------------------------------------
    // The extension
    // ---------------------------------------------------------------------------------------

    /// <summary>The built-in MCP extension factory. TS <c>createMcpExtension</c> / <c>default</c>.</summary>
    public static ExtensionFactory CreateMcpExtension(McpExtensionOptions? options = null)
    {
        options ??= new McpExtensionOptions();
        return pi =>
        {
            var servers = new List<McpServer>();
            var configuredEntries = new List<McpServerEntry>();
            var configErrors = new List<string>();

            /** The trusted project's `mcp.json`, where `/mcp` saves project overrides of global servers. */
            string? projectConfig = null;

            /** Registered servers that `mcp.json` overrides, shown in `/mcp`. */
            var overridden = new List<string>();

            /** Between session_start and session_shutdown. Registrations before that are read on session_start. */
            var sessionActive = false;

            var autoEnableCodemode = true;

            /** Whether the "codemode tools unreachable" warning was shown since the session started. */
            var warnedUnreachable = false;

            Task? pending = null;

            /** Whether a prompt already waited for the startup connections since the session started. */
            var waitedForStartup = false;

            var startupWaitMs = options.StartupWaitMs ?? DefaultStartupWaitMs;

            /** Bumped on every session start and shutdown so a runtime load that resolves late is dropped. */
            var generation = 0;

            /** Working directory of the session, for stdio servers. */
            var sessionCwd = Directory.GetCurrentDirectory();

            var credentials = options.Credentials;

            /** The session's model registry, which resolves `auth.provider` tokens. */
            ModelRegistry? modelRegistry = null;

            McpServerLog? serverLog = null;
            var openUrl = options.OpenUrl ?? (url => OpenBrowser.Open(url));
            Action<McpServerEntry, McpServerConfigPatch> updateConfig = options.UpdateConfig
                ?? ((entry, patch) => McpConfig.Update(
                    entry.Override ?? entry.Source, entry.Name, patch, @override: entry.Override is not null));

            var listeners = new HashSet<Action>();
            void EmitChange()
            {
                foreach (var listener in listeners.ToList())
                {
                    listener();
                }
            }

            Func<Action, Action> subscribe = listener =>
            {
                listeners.Add(listener);
                return () => listeners.Remove(listener);
            };

            List<McpServerConnection> Connections() =>
                [.. servers.Where(server => server.Connection is not null).Select(server => server.Connection!)];

            McpServer? FindServer(string name) =>
                servers.FirstOrDefault(server => string.Equals(server.Entry.Name, name, StringComparison.Ordinal));

            /** Servers extensions registered, except names `mcp.json` defines, which take precedence. */
            (List<McpServer> Servers, List<string> Overridden) RegisteredServers()
            {
                var registered = new List<McpServer>();
                var overriddenNames = new List<string>();
                foreach (var (name, config, extensionPath) in pi.GetMcpServers())
                {
                    var configured = configuredEntries.FirstOrDefault(entry =>
                        string.Equals(McpServers.Namespace(entry.Name), McpServers.Namespace(name), StringComparison.Ordinal));
                    if (configured is not null)
                    {
                        overriddenNames.Add(
                            $"\"{name}\" registered by {extensionPath} is overridden by \"{configured.Name}\" in {configured.Source}");
                        continue;
                    }

                    registered.Add(new McpServer
                    {
                        Entry = new McpServerEntry(name, config, extensionPath) { Scope = "extension" },
                        RegisteredConfig = config.Value.ToJsonString(),
                    });
                }

                return (registered, overriddenNames);
            }

            McpOAuth.McpOAuthCredentialStore GetCredentials() => credentials ??= new McpOAuth.McpOAuthCredentialStore();

            McpServerLog GetServerLog() => serverLog ??=
                new McpServerLog(options.LogPath ?? NodePath.Join(Config.GetAgentDir(), "mcp.log"));

            /** pi tool name to the `<server>\0<tool>` it was assigned to, so names stay unique and stable. */
            var toolOwners = new Dictionary<string, string>(StringComparer.Ordinal);

            /** Tool names currently offered by each server. */
            var serverTools = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            /** Last definition registered under each tool name, to re-register withdrawn tools as hidden. */
            var definitions = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);

            // A resumed session renders calls to MCP tools before their server connected, if it ever does.
            pi.RegisterToolRenderer((toolName, next) =>
            {
                var match = McpToolNameRegex().Match(toolName);
                return next() ?? (match.Success
                    ? McpTools.CreateMcpToolRenderers($"{match.Groups[1].Value}/{match.Groups[2].Value}")
                    : null);
            });

            void RegisterTools(McpServerConnection connection)
            {
                var server = connection.Entry.Name;
                var entry = FindServer(server)?.Entry ?? connection.Entry;
                var description = entry.Config.Description?.Trim();
                var ns = new ToolNamespace
                {
                    Name = McpServers.Namespace(server),
                    Description = string.IsNullOrEmpty(description) ? null : description,
                    Instructions = connection.Instructions,
                };
                var previous = serverTools.GetValueOrDefault(server) ?? new HashSet<string>(StringComparer.Ordinal);
                var current = new HashSet<string>(StringComparer.Ordinal);

                // Like Codex, all tools whose names sanitize to the same name get the hash suffix, so which
                // one would keep the plain name does not depend on the order of the list.
                var plain = connection.Tools
                    .Select(tool => tool.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Select(tool => McpTools.CreateMcpToolName(server, tool))
                    .ToList();

                string AssignName(string tool, string owner)
                {
                    var name = McpTools.CreateMcpToolName(server, tool, candidate =>
                    {
                        var existing = toolOwners.GetValueOrDefault(candidate);
                        return (existing is not null && existing != owner)
                            || current.Contains(candidate)
                            || plain.IndexOf(candidate) != plain.LastIndexOf(candidate);
                    });
                    toolOwners[name] = owner;
                    current.Add(name);
                    return name;
                }

                foreach (var tool in connection.Tools)
                {
                    var definition = McpTools.CreateMcpToolDefinition(new McpTools.CreateMcpToolOptions
                    {
                        Server = server,
                        Tool = tool,
                        Name = AssignName(tool.Name, $"{server}\0{tool.Name}"),
                        Exposure = McpServers.GetMcpToolExposure(entry.Config, tool.Name),
                        Namespace = ns,
                        TimeoutMs = connection.TimeoutMs,
                        GetClient = () => Task.FromResult<IMcpToolCaller>(connection),
                        ReadableResources = () => ResourceServers().Contains(connection),
                    });
                    definitions[definition.Name] = definition;
                    pi.RegisterTool(definition);
                }

                serverTools[server] = current;

                // Tools cannot be unregistered, so tools the server dropped are re-registered as hidden. When
                // the server offers them again they are registered with their configured exposure above.
                foreach (var name in previous)
                {
                    if (!current.Contains(name) && definitions.TryGetValue(name, out var definition))
                    {
                        pi.RegisterTool(definition with { Exposure = ToolExposure.Hidden });
                    }
                }

                SyncResourceTools();
            }

            /** Make a disabled server's tools unreachable. */
            void HideTools(string server)
            {
                foreach (var name in serverTools.GetValueOrDefault(server) ?? [])
                {
                    if (definitions.TryGetValue(name, out var definition))
                    {
                        pi.RegisterTool(definition with { Exposure = ToolExposure.Hidden });
                    }
                }

                serverTools[server] = new HashSet<string>(StringComparer.Ordinal);
                SyncResourceTools();
            }

            /** Enabled servers with resources whose exposure is not `hidden`, which the resource tools reach. */
            List<McpServer> ServersWithResources() =>
            [
                .. servers.Where(server => server.Connection?.HasResources == true
                    && IsEnabled(server)
                    && ExposureOf(server.Entry) != McpServers.Exposures.Hidden),
            ];

            List<McpServerConnection> ResourceServers() =>
                [.. ServersWithResources().Select(server => server.Connection!)];

            /** Exposure the resource tools were last registered with; null until a server has resources. */
            string? resourceToolsExposure = null;

            /**
             * Register the resource tools with the widest exposure of the servers they reach: `direct`
             * when one of them is direct, and so on. They are hidden when no server has resources.
             */
            void SyncResourceTools()
            {
                var exposures = new HashSet<string>(
                    ServersWithResources().Select(server => ExposureOf(server.Entry)), StringComparer.Ordinal);
                var exposure = new[] { McpServers.Exposures.Direct, McpServers.Exposures.Codemode, McpServers.Exposures.Deferred }
                    .FirstOrDefault(exposures.Contains);
                var next = exposure ?? McpServers.Exposures.Hidden;
                if (next == resourceToolsExposure
                    || (resourceToolsExposure is null && next == McpServers.Exposures.Hidden))
                {
                    return;
                }

                var wasDirect = resourceToolsExposure == McpServers.Exposures.Direct;
                resourceToolsExposure = next;
                var resourceDefinitions = McpResources.CreateMcpResourceToolDefinitions(
                    new McpResourceToolOptions { Exposure = next, Servers = ResourceServers });
                foreach (var definition in resourceDefinitions)
                {
                    pi.RegisterTool(definition);
                }

                if (wasDirect)
                {
                    var names = new HashSet<string>(
                        resourceDefinitions.Select(definition => definition.Name), StringComparer.Ordinal);
                    pi.SetActiveTools([.. pi.GetActiveTools().Where(name => !names.Contains(name))]);
                }
            }

            /**
             * Tools that are not declared to the model are reached through the codemode tool (scripts call
             * them) or the tool_search tool (it declares them). Either reaches every such tool. Activate the
             * one the tools' exposure asks for: codemode for `codemode` unless `autoEnableCodemode` is false,
             * tool_search for `deferred`.
             */
            void EnsureDiscoveryActive(IExtensionContext ctx)
            {
                // From the config, so the tool is active before the servers connect. Resource tools share
                // their server's exposure.
                var exposures = new HashSet<string>(StringComparer.Ordinal);
                foreach (var server in servers)
                {
                    if (!IsEnabled(server))
                    {
                        continue;
                    }

                    foreach (var exposure in ConfiguredExposures(server.Entry))
                    {
                        exposures.Add(exposure);
                    }
                }

                var needsCodemode = exposures.Contains(McpServers.Exposures.Codemode);
                var needsToolSearch = exposures.Contains(McpServers.Exposures.Deferred);
                if (!needsCodemode && !needsToolSearch)
                {
                    return;
                }

                // Other extensions' tools of the same names cannot reach MCP tools, so never activate them.
                var tools = pi.GetAllTools();
                var hasCodemode = tools.Any(CodemodeToolApi.IsCodemodeTool);
                var hasToolSearch = tools.Any(ToolSearchApi.IsToolSearchTool);
                var active = pi.GetActiveTools();
                var activate = new List<string>();
                if (needsCodemode && hasCodemode && autoEnableCodemode && !active.Contains(CodemodeToolApi.CodemodeToolName))
                {
                    activate.Add(CodemodeToolApi.CodemodeToolName);
                }

                if (needsToolSearch && hasToolSearch && !active.Contains(ToolSearchApi.ToolSearchToolName))
                {
                    activate.Add(ToolSearchApi.ToolSearchToolName);
                }

                if (activate.Count > 0)
                {
                    pi.SetActiveTools([.. active, .. activate]);
                }

                var reachable = active.Concat(activate).ToList();
                if (hasCodemode && reachable.Contains(CodemodeToolApi.CodemodeToolName))
                {
                    return;
                }

                if (hasToolSearch && reachable.Contains(ToolSearchApi.ToolSearchToolName))
                {
                    return;
                }

                if (warnedUnreachable)
                {
                    return;
                }

                warnedUnreachable = true;
                var reason = needsCodemode && hasCodemode && !autoEnableCodemode ? " (autoEnableCodemode is false)" : "";
                ctx.Ui.Notify(
                    "MCP tools are only reachable from the codemode or tool_search tool, but neither is active"
                    + $"{reason}; they cannot be called.",
                    NotifyType.Warning);
            }

            /**
             * Stored tokens of servers waiting for a sign-in, as they were when the sign-in was needed.
             * `pi mcp login` in another process (for example run by the agent) changes them.
             *
             * Difference C192: TS snapshots with the synchronous `credentials.tokens()`, so the map holds
             * a string. The port's credential store reads asynchronously, so the map holds the pending
             * read instead; the read still starts at the moment the state changed, and every consumer
             * awaits it, so the value is the same.
             */
            var tokensAtSignIn = new Dictionary<McpServerConnection, Task<string>>();

            async Task<string> StoredTokensAsync(McpServerConnection connection)
            {
                var url = connection.OAuthUrl;
                if (url is null || credentials is null)
                {
                    return "null";
                }

                var tokens = await credentials.TokensAsync(connection.Name, url).ConfigureAwait(false);
                return JsonSerializer.Serialize(tokens);
            }

            void OnConnectionChange(McpServerConnection connection)
            {
                if (connection.State != McpServerState.NeedsAuth)
                {
                    tokensAtSignIn.Remove(connection);
                }
                else if (!tokensAtSignIn.ContainsKey(connection))
                {
                    tokensAtSignIn[connection] = StoredTokensAsync(connection);
                }

                EmitChange();
            }

            /** Reconnect servers that need a sign-in when their credentials were stored since. */
            async Task ReconnectSignedInAsync(IExtensionContext ctx)
            {
                var signedIn = new List<McpServerConnection>();
                foreach (var (connection, tokens) in tokensAtSignIn.ToList())
                {
                    var before = await tokens.ConfigureAwait(false);
                    if (await StoredTokensAsync(connection).ConfigureAwait(false) != before)
                    {
                        signedIn.Add(connection);
                    }
                }

                if (signedIn.Count == 0)
                {
                    return;
                }

                foreach (var connection in signedIn)
                {
                    tokensAtSignIn.Remove(connection);
                }

                await Task.WhenAll(signedIn.Select(connection => SwallowAsync(connection.ReconnectAsync())))
                    .ConfigureAwait(false);
                EnsureDiscoveryActive(ctx);
            }

            /** Create the server's connection. */
            async Task<McpServerConnection> CreateConnectionAsync(McpServer server)
            {
                var connection = new McpServerConnection(new McpServerConnectionOptions
                {
                    Entry = server.Entry,
                    Cwd = sessionCwd,
                    CreateTransport = options.CreateTransport ?? McpServerConnection.CreateDefaultTransport,
                    Credentials = GetCredentials(),
                    ProviderToken = provider => modelRegistry is null
                        ? Task.FromResult<string?>(null)
                        : modelRegistry.GetApiKeyForProviderAsync(provider),
                    Log = GetServerLog(),
                    OnTools = RegisterTools,
                    OnChange = OnConnectionChange,
                });
                server.Connection = connection;
                EmitChange();
                return connection;
            }

            /**
             * Connect the server in the background. `server.ready` settles when it connected or failed;
             * failures show in its state. It starts after `after`; `isCurrent` stops it when the session
             * ended meanwhile. The returned task rejects when the connection cannot be made.
             */
            Task StartConnectionAsync(McpServer server, Func<bool> isCurrent, Task? after = null)
            {
                var ready = RunAsync();
                server.Ready = SwallowAsync(ready);
                return ready;

                async Task RunAsync()
                {
                    if (after is not null)
                    {
                        await after.ConfigureAwait(false);
                    }

                    if (!isCurrent())
                    {
                        return;
                    }

                    var connection = await CreateConnectionAsync(server).ConfigureAwait(false);
                    if (!isCurrent())
                    {
                        return;
                    }

                    await SwallowAsync(connection.GetClientAsync()).ConfigureAwait(false);
                }
            }

            /** Wait for servers still connecting, until they settle or <paramref name="signal"/> aborts. */
            static async Task WaitForServersAsync(IReadOnlyList<McpServer> waiting, CancellationToken? signal)
            {
                var ready = waiting
                    .Where(server => server.Ready is not null)
                    .Select(server => server.Ready!)
                    .ToList();
                if (ready.Count == 0 || signal?.IsCancellationRequested == true)
                {
                    return;
                }

                var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = signal?.Register(() => aborted.TrySetResult());
                await Task.WhenAny(Task.WhenAll(ready), aborted.Task).ConfigureAwait(false);
            }

            /**
             * One message for everything that needs the user after startup, or only for `only`, servers
             * that connected later.
             */
            void ReportProblems(IExtensionContext ctx, IReadOnlyList<McpServer>? only = null)
            {
                var lines = only is null ? configErrors.Select(error => $"config: {error}").ToList() : [];
                foreach (var server in only ?? servers)
                {
                    var state = server.Connection?.State;
                    if (state is McpServerState.NeedsAuth or McpServerState.Failed)
                    {
                        lines.Add($"{server.Entry.Name}: {DescribeState(server)}");
                    }
                }

                if (lines.Count == 0)
                {
                    return;
                }

                ctx.Ui.Notify(
                    $"MCP servers need attention:\n{string.Join("\n", lines.Select(line => $"  {line}"))}\nRun /mcp to fix.",
                    NotifyType.Warning);
            }

            /**
             * Save a config change; returns an error message when the file could not be updated. Changes to
             * registered servers only apply to the current session. `inProject` adds a project override.
             */
            string? SaveConfig(McpServer server, McpServerConfigPatch patch, bool inProject = false)
            {
                var @override = inProject ? projectConfig : server.Entry.Override;
                var entry = @override is not null ? server.Entry with { Override = @override } : server.Entry;
                if (entry.Scope != "extension")
                {
                    try
                    {
                        updateConfig(entry, patch);
                    }
                    catch (Exception error)
                    {
                        return $"Could not update {entry.Override ?? entry.Source}: {error.Message}";
                    }
                }

                // TS spreads `{ ...entry.config, ...patch }`; only the keys the patch carries change.
                var value = (JsonObject)entry.Config.Value.DeepClone();
                if (patch.Enabled is bool enabled)
                {
                    value["enabled"] = enabled;
                }

                if (patch.Exposure is string exposure)
                {
                    value["exposure"] = exposure;
                }

                server.Entry = entry with { Config = new McpServerConfig(value) };
                return null;
            }

            async Task<string?> SignInAsync(McpServer server, IMcpSignInPrompt prompt)
            {
                var connection = server.Connection;
                var url = connection?.OAuthUrl;
                if (connection is null || url is null)
                {
                    return $"MCP server \"{server.Entry.Name}\" does not use OAuth.";
                }

                try
                {
                    await McpOAuth.SignInServerAsync(new McpSignInOptions
                    {
                        ServerUrl = url,
                        Store = GetCredentials().ForServer(server.Entry.Name, url),
                        Settings = connection.OAuthSettings(),
                        Challenge = connection.Challenge,
                        Prompt = prompt,
                    }).ConfigureAwait(false);
                }
                catch (McpSignInCancelledError)
                {
                    return "Sign-in cancelled.";
                }
                catch (Exception error)
                {
                    return $"Sign-in failed: {error.Message}";
                }

                // The challenge that asked for this sign-in (for example for more scope) is answered.
                connection.ClearChallenge();
                try
                {
                    await connection.ReconnectAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    return $"Signed in, but {error.Message}";
                }

                return null;
            }

            async Task<bool> SignOutAsync(McpServer server)
            {
                var connection = server.Connection;
                var url = connection?.OAuthUrl;
                if (connection is null || url is null)
                {
                    return false;
                }

                var removed = await GetCredentials().RemoveAsync(server.Entry.Name, url).ConfigureAwait(false);
                await connection.SignOutAsync().ConfigureAwait(false);
                return removed;
            }

            async Task<string?> ReconnectAsync(McpServer server)
            {
                var connection = server.Connection;
                if (connection is null)
                {
                    return $"MCP server \"{server.Entry.Name}\" is disabled.";
                }

                try
                {
                    await connection.ReconnectAsync().ConfigureAwait(false);
                    return null;
                }
                catch (Exception error)
                {
                    return error.Message;
                }
            }

            /** Returns an error message when the config could not be saved; connection errors show in the state. */
            async Task<string?> SetEnabledAsync(McpServer server, bool enabled, bool inProject = false)
            {
                var failed = SaveConfig(server, new McpServerConfigPatch { Enabled = enabled }, inProject);
                if (failed is not null)
                {
                    return failed;
                }

                if (!enabled)
                {
                    var connection = server.Connection;
                    server.Connection = null;
                    HideTools(server.Entry.Name);
                    EmitChange();
                    if (connection is not null)
                    {
                        await connection.CloseAsync().ConfigureAwait(false);
                    }

                    return null;
                }

                await StartConnectionAsync(server, () => true).ConfigureAwait(false);
                return null;
            }

            string? SetExposure(McpServer server, string exposure)
            {
                var failed = SaveConfig(server, new McpServerConfigPatch { Exposure = exposure });
                if (failed is not null)
                {
                    return failed;
                }

                var connection = server.Connection;
                if (connection?.State == McpServerState.Connected)
                {
                    RegisterTools(connection);
                }

                SyncResourceTools();

                // Tools no longer exposed directly leave the declared set; direct tools are activated on registration.
                var indirect = new HashSet<string>(
                    pi.GetAllTools().Where(tool => tool.Exposure != ToolExposure.Direct).Select(tool => tool.Name),
                    StringComparer.Ordinal);
                var tools = serverTools.GetValueOrDefault(server.Entry.Name) ?? new HashSet<string>(StringComparer.Ordinal);
                pi.SetActiveTools([.. pi.GetActiveTools().Where(name => !tools.Contains(name) || !indirect.Contains(name))]);
                EmitChange();
                return null;
            }

            // -------------------------------------------------------------------------------
            // Manager (`/mcp` in the TUI)
            // -------------------------------------------------------------------------------

            List<string> Notices() =>
            [
                .. configErrors.Select(error => $"config: {error}"),
                .. overridden.Select(line => $"overridden: {line}"),
            ];

            McpMenu ServersMenu()
            {
                var notices = string.Join("\n", Notices());
                return new McpMenu
                {
                    Title = "MCP servers",
                    Error = notices.Length > 0 ? notices : null,
                    Items =
                    [
                        .. servers
                            .OrderBy(AttentionRank)
                            .ThenBy(server => server.Entry.Name, StringComparer.CurrentCulture)
                            .Select(server => new SelectItem
                            {
                                Value = server.Entry.Name,
                                Label = server.Entry.Name,
                                Description = $"{DescribeState(server)} · {ExposureOf(server.Entry)} · "
                                    + (server.Entry.Override is not null
                                        ? "global, project override"
                                        : server.Entry.Scope ?? server.Entry.Source),
                            }),
                    ],
                    Empty = $"No MCP servers configured. Add them to {ResolveMcpJson()} or .pi/mcp.json.",
                    ConfirmLabel = "manage",
                    CancelLabel = "close",
                };
            }

            McpMenu ServerMenu(string name)
            {
                var server = FindServer(name);
                if (server is null)
                {
                    return new McpMenu
                    {
                        Title = name,
                        Items = [],
                        Empty = "This server is no longer configured.",
                        ConfirmLabel = "",
                        CancelLabel = "back",
                    };
                }

                var entry = server.Entry;
                var connection = server.Connection;
                var saved = entry.Scope == "extension"
                    ? "for this session"
                    : entry.Override is not null
                        ? "saved to the project mcp.json"
                        : entry.Scope is not null
                            ? $"saved to the {entry.Scope} mcp.json"
                            : "saved to mcp.json";

                // Global servers without an override can be turned on or off for the trusted project alone.
                var inProject = entry.Scope == "global" && entry.Override is null && projectConfig is not null;
                const string inProjectSaved = "saved to the project mcp.json";
                var items = new List<SelectItem>();
                if (!IsEnabled(server))
                {
                    items.Add(new SelectItem { Value = "enable", Label = "Enable", Description = saved });
                    if (inProject)
                    {
                        items.Add(new SelectItem
                        {
                            Value = "enable-project", Label = "Enable in this project", Description = inProjectSaved,
                        });
                    }
                }
                else
                {
                    var state = connection?.State;
                    if (state == McpServerState.NeedsAuth)
                    {
                        items.Add(new SelectItem { Value = "signin", Label = "Sign in", Description = "opens the browser" });
                    }

                    if (state == McpServerState.Connected && connection is not null)
                    {
                        items.Add(new SelectItem
                        {
                            Value = "tools", Label = "Tools", Description = $"{connection.Tools.Count} offered",
                        });
                    }

                    if (state is McpServerState.Failed or McpServerState.Disconnected
                        or McpServerState.Connected or McpServerState.NeedsAuth)
                    {
                        items.Add(new SelectItem { Value = "reconnect", Label = "Reconnect" });
                    }

                    if (state == McpServerState.Connected && connection?.OAuthUrl is not null)
                    {
                        items.Add(new SelectItem
                        {
                            Value = "signout", Label = "Sign out", Description = "deletes the stored credentials",
                        });
                    }

                    items.Add(new SelectItem
                    {
                        Value = "exposure", Label = "Exposure", Description = ExposureOf(entry),
                    });
                    items.Add(new SelectItem { Value = "disable", Label = "Disable", Description = saved });
                    if (inProject)
                    {
                        items.Add(new SelectItem
                        {
                            Value = "disable-project", Label = "Disable in this project", Description = inProjectSaved,
                        });
                    }
                }

                var details = new List<string>
                {
                    DescribeTransport(entry),
                    $"{entry.Scope ?? "config"}: {entry.Source}",
                };
                if (entry.Override is not null)
                {
                    details.Add($"project override: {entry.Override}");
                }

                details.Add($"State: {DescribeState(server, withError: false)}");
                var errorLines = new List<string>();
                if (server.Message is not null)
                {
                    errorLines.Add(server.Message);
                }

                if (connection?.State != McpServerState.Connected && connection?.Error is not null)
                {
                    errorLines.Add(connection.Error);
                }

                var error = string.Join("\n", errorLines);
                return new McpMenu
                {
                    Title = $"MCP server {name}",
                    Details = string.Join("\n", details),
                    Error = error.Length > 0 ? error : null,
                    Items = items,
                    Selected = items.Count > 0 ? items[0].Value : null,
                    ConfirmLabel = "select",
                    CancelLabel = "back",
                };
            }

            async Task ShowToolsAsync(IMcpUi ui, McpServer server)
            {
                var exposure = ExposureOf(server.Entry);
                var overrides = server.Entry.Config.ToolExposure?.Count > 0;
                await ui.MenuAsync(() => new McpMenu
                {
                    Title = $"Tools of {server.Entry.Name}",
                    Details = $"Exposure {exposure}: "
                        + (exposure == McpServers.Exposures.Hidden ? "unreachable" : ExposureDescription(exposure))
                        + (overrides ? "\nSome tools override it with toolExposure." : ""),
                    Items =
                    [
                        .. (server.Connection?.Tools ?? []).Select(tool =>
                        {
                            var toolExposure = McpServers.GetMcpToolExposure(server.Entry.Config, tool.Name);
                            var description = FirstLine(tool.Description ?? "");
                            return new SelectItem
                            {
                                Value = tool.Name,
                                Label = tool.Name,
                                Description = toolExposure == exposure ? description : $"[{toolExposure}] {description}",
                            };
                        }),
                    ],
                    Empty = "The server offers no tools.",
                    ConfirmLabel = "back",
                    CancelLabel = "back",
                }).ConfigureAwait(false);
            }

            async Task<string?> ChooseExposureAsync(IMcpUi ui, McpServer server)
            {
                var current = ExposureOf(server.Entry);
                var choice = await ui.MenuAsync(() => new McpMenu
                {
                    Title = $"Exposure of {server.Entry.Name}",
                    Details = server.Entry.Scope == "extension"
                        ? $"Applies to this session; the server is registered by {server.Entry.Source}."
                        : $"Saved to {server.Entry.Override ?? server.Entry.Source}.",
                    Items =
                    [
                        .. ExposureDescriptions.Select(exposure => new SelectItem
                        {
                            Value = exposure.Exposure,
                            Label = $"{(exposure.Exposure == current ? "✓ " : "  ")}{exposure.Exposure}",
                            Description = exposure.Description,
                        }),
                    ],
                    Selected = current,
                    ConfirmLabel = "save",
                    CancelLabel = "back",
                }).ConfigureAwait(false);
                if (choice is null || choice == current)
                {
                    return null;
                }

                return SetExposure(server, choice);
            }

            /** Sign in with the manager view's sign-in screen, which shows the URL with a copy key. */
            Task<string?> SignInWithUiAsync(IMcpUi ui, McpServer server)
            {
                var title = $"Sign in to {server.Entry.Name}";
                ui.Status(title, "Contacting the authorization server…");
                return SignInAsync(server, new UiSignInPrompt(ui, title, openUrl));
            }

            async Task RunActionAsync(IMcpUi ui, IExtensionContext ctx, McpServer server, string action)
            {
                var name = server.Entry.Name;
                string? message = null;
                switch (action)
                {
                    case "signin":
                        message = await SignInWithUiAsync(ui, server).ConfigureAwait(false);
                        break;
                    case "reconnect":
                        // A failure shows as the connection's state and error.
                        ui.Status($"MCP server {name}", "Reconnecting…");
                        await ReconnectAsync(server).ConfigureAwait(false);
                        break;
                    case "signout":
                        await SignOutAsync(server).ConfigureAwait(false);
                        break;
                    case "tools":
                        await ShowToolsAsync(ui, server).ConfigureAwait(false);
                        break;
                    case "exposure":
                        message = await ChooseExposureAsync(ui, server).ConfigureAwait(false);
                        break;
                    case "enable":
                    case "disable":
                    case "enable-project":
                    case "disable-project":
                    {
                        var enable = action.StartsWith("enable", StringComparison.Ordinal);
                        ui.Status($"MCP server {name}", enable ? "Connecting…" : "Disconnecting…");
                        message = await SetEnabledAsync(server, enable, action.EndsWith("-project", StringComparison.Ordinal))
                            .ConfigureAwait(false);
                        break;
                    }
                }

                server.Message = message;
                EnsureDiscoveryActive(ctx);
                EmitChange();
            }

            async Task ManageAsync(IMcpUi ui, IExtensionContext ctx)
            {
                while (true)
                {
                    var name = await ui.MenuAsync(ServersMenu, subscribe).ConfigureAwait(false);
                    if (name is null)
                    {
                        return;
                    }

                    while (true)
                    {
                        var action = await ui.MenuAsync(() => ServerMenu(name), subscribe).ConfigureAwait(false);
                        var server = FindServer(name);
                        if (action is null || server is null)
                        {
                            break;
                        }

                        await RunActionAsync(ui, ctx, server, action).ConfigureAwait(false);
                    }
                }
            }

            // -------------------------------------------------------------------------------
            // Subcommands and plain status (no TUI)
            // -------------------------------------------------------------------------------

            /** Resolve the server for a subcommand, asking when the name is omitted and ambiguous. */
            async Task<McpServer?> PickServerAsync(
                string? name,
                IExtensionCommandContext ctx,
                Func<McpServer, bool> eligible,
                Func<McpServer, bool> preferred,
                string none)
            {
                if (name is not null)
                {
                    var server = FindServer(name);
                    if (server is null)
                    {
                        ctx.Ui.Notify($"No MCP server named \"{name}\".", NotifyType.Error);
                    }
                    else if (!eligible(server))
                    {
                        ctx.Ui.Notify(none, NotifyType.Error);
                    }

                    return server is not null && eligible(server) ? server : null;
                }

                var candidates = servers.Where(eligible).ToList();
                if (candidates.Count == 0)
                {
                    ctx.Ui.Notify(none, NotifyType.Info);
                    return null;
                }

                var preferredCandidates = candidates.Where(preferred).ToList();
                if (candidates.Count == 1)
                {
                    return candidates[0];
                }

                if (preferredCandidates.Count == 1)
                {
                    return preferredCandidates[0];
                }

                var choice = await ctx.Ui
                    .Select("MCP server", [.. candidates.Select(server => server.Entry.Name)])
                    .ConfigureAwait(false);
                return candidates.FirstOrDefault(server => server.Entry.Name == choice);
            }

            bool UsesOAuth(McpServer server) => server.Connection?.OAuthUrl is not null;
            bool PreferNeedsAuth(McpServer server) => server.Connection?.State == McpServerState.NeedsAuth;
            const string oauthNone =
                "No enabled MCP server uses OAuth. Only HTTP servers without an Authorization header do.";

            async Task LoginCommandAsync(McpServer server, IExtensionCommandContext ctx)
            {
                var name = server.Entry.Name;
                if (!ctx.HasUI)
                {
                    ctx.Ui.Notify($"Signing in to MCP server \"{name}\" requires interactive mode.", NotifyType.Error);
                    return;
                }

                string? failure;
                if (ctx.Mode == ExtensionMode.Tui)
                {
                    failure = null;
                    await options.ShowManager(ctx, async ui =>
                    {
                        failure = await SignInWithUiAsync(ui, server).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                else
                {
                    failure = await SignInAsync(server, new NonTuiSignInPrompt(name, ctx, openUrl)).ConfigureAwait(false);
                }

                if (failure is not null)
                {
                    ctx.Ui.Notify(failure, failure == "Sign-in cancelled." ? NotifyType.Info : NotifyType.Error);
                    return;
                }

                EnsureDiscoveryActive(ctx);
                ctx.Ui.Notify(
                    $"Signed in to MCP server \"{name}\" ({server.Connection?.Tools.Count ?? 0} tools).",
                    NotifyType.Info);
            }

            // -------------------------------------------------------------------------------
            // Events
            // -------------------------------------------------------------------------------

            pi.On<ExtensionEvent.SessionStart>(ExtensionEventNames.SessionStart, async (_, ctx) =>
            {
                var loaded = (options.LoadConfig ?? DefaultLoadConfig)(ctx);
                configErrors = [.. loaded.Errors];
                projectConfig = loaded.ProjectConfig;
                autoEnableCodemode = loaded.AutoEnableCodemode ?? true;
                warnedUnreachable = false;
                waitedForStartup = false;
                sessionCwd = ctx.Cwd;
                modelRegistry = ctx.ModelRegistry;
                var current = ++generation;
                sessionActive = true;
                configuredEntries = [.. loaded.Servers];
                var registered = RegisteredServers();
                overridden = registered.Overridden;
                servers = [.. loaded.Servers.Select(entry => new McpServer { Entry = entry }), .. registered.Servers];
                EmitChange();

                // Codemode or tool_search is activated from the config: the first prompt does not wait for
                // servers whose tools are not declared to the model, and scripts or searches wait for them.
                EnsureDiscoveryActive(ctx);
                var enabled = servers.Where(IsEnabled).ToList();
                if (enabled.Count == 0)
                {
                    ReportProblems(ctx);
                    return;
                }

                // The MCP client is already loaded in the port (difference C189). Waiting one event loop
                // turn still lets the first render happen before connecting.
                var runtimeReady = DeferOneTurnAsync();
                bool IsCurrent() => current == generation;
                pending = RunStartupAsync();

                async Task RunStartupAsync()
                {
                    try
                    {
                        await Task
                            .WhenAll(enabled.Select(server => StartConnectionAsync(server, IsCurrent, runtimeReady)))
                            .ConfigureAwait(false);
                        if (IsCurrent())
                        {
                            ReportProblems(ctx);
                        }
                    }
                    catch (Exception error)
                    {
                        // The session may have been disposed meanwhile, which makes ctx stale.
                        try
                        {
                            ctx.Ui.Notify($"MCP failed to load: {error.Message}", NotifyType.Error);
                        }
                        catch
                        {
                            // The context is stale; nothing is left to report to.
                        }
                    }
                }
            });

            // The first prompt waits for servers whose tools are declared to the model, so they are declared
            // in its first request, but not indefinitely: a slow or hanging server must not hold up the
            // prompt. Other servers are waited for when a script or search needs them (below).
            async Task WaitForDirectServersAsync(IExtensionContext ctx)
            {
                if (waitedForStartup)
                {
                    return;
                }

                waitedForStartup = true;
                var ready = servers
                    .Where(server => IsEnabled(server) && HasDirectTools(server.Entry) && server.Ready is not null)
                    .Select(server => server.Ready!)
                    .ToList();
                if (ready.Count == 0)
                {
                    return;
                }

                using var timeout = new CancellationTokenSource();
                var all = Task.WhenAll(ready);
                var finished = await Task
                    .WhenAny(all, Task.Delay(TimeSpan.FromMilliseconds(startupWaitMs), timeout.Token))
                    .ConfigureAwait(false) == all;
                timeout.Cancel();
                if (!finished)
                {
                    ctx.Ui.Notify(
                        "MCP servers are still connecting; their tools become available once connected.",
                        NotifyType.Info);
                }
            }

            // Every prompt lists the servers in the `mcp_servers` section as they are when it starts. Pi
            // appends the section to the conversation when it changed, for example after a server connected.
            pi.On<ExtensionEvent.BeforeAgentStart>(ExtensionEventNames.BeforeAgentStart, async (@event, ctx) =>
            {
                await WaitForDirectServersAsync(ctx).ConfigureAwait(false);
                var sections = @event.SystemPromptOptions.Sections;
                var section = RenderServersSection(
                    [.. servers.Select(server => new McpServerListing(server.Entry, server.Connection?.Instructions))]);
                if (section is not null)
                {
                    sections[McpServersSection] = section;
                }
                else
                {
                    sections.Remove(McpServersSection);
                }
            });

            // A codemode script waits for the servers it names, or for every server when it searches or
            // enumerates tools, so their tools are registered before the script runs. tool_search and the
            // resource tools reach every server, so they wait for all of them.
            pi.On<ExtensionEvent>(ExtensionEventNames.ToolCall, async (@event, ctx) =>
            {
                // Difference C154's neighbour: the port's tool pipeline only emits CustomToolCall, so the
                // tool name and the input come from that variant rather than the TS union's `event.toolName`.
                if (@event is not ExtensionEvent.CustomToolCall call)
                {
                    return;
                }

                var tool = pi.GetAllTools().FirstOrDefault(candidate => candidate.Name == call.ToolName);
                if (tool is null)
                {
                    return;
                }

                var pendingServers = servers
                    .Where(server => IsEnabled(server)
                        && server.Connection?.State != McpServerState.Connected
                        && server.Ready is not null)
                    .ToList();
                if (pendingServers.Count == 0)
                {
                    return;
                }

                List<McpServer> waiting;
                if (CodemodeToolApi.IsCodemodeTool(tool))
                {
                    var source = call.Input.GetValueOrDefault("code") as string ?? "";
                    waiting = [.. pendingServers.Where(server => ScriptNeedsServer(source, server.Entry.Name))];
                }
                else if (ToolSearchApi.IsToolSearchTool(tool) || ResourceToolNames.Contains(tool.Name))
                {
                    waiting = pendingServers;
                }
                else
                {
                    waiting = [];
                }

                await WaitForServersAsync(waiting, ctx.Signal).ConfigureAwait(false);
            });

            // Pick up sign-ins done outside the session, such as `pi mcp login` run by the agent.
            pi.On<ExtensionEvent.TurnStart>(ExtensionEventNames.TurnStart, async (_, ctx) =>
            {
                if (tokensAtSignIn.Count > 0)
                {
                    await ReconnectSignedInAsync(ctx).ConfigureAwait(false);
                }
            });

            // Servers registered or unregistered during the session connect or disconnect right away.
            pi.On<ExtensionEvent.McpServersChange>(ExtensionEventNames.McpServersChange, async (_, ctx) =>
            {
                if (!sessionActive)
                {
                    return;
                }

                var current = generation;
                var registered = RegisteredServers();
                overridden = registered.Overridden;
                var next = registered.Servers.ToDictionary(
                    server => server.Entry.Name, server => server, StringComparer.Ordinal);

                // Unregistered servers and re-registered ones with a new config are dropped; the latter come back below.
                var removed = servers
                    .Where(server => server.Entry.Scope == "extension"
                        && next.GetValueOrDefault(server.Entry.Name)?.RegisteredConfig != server.RegisteredConfig)
                    .ToList();
                servers = [.. servers.Where(server => !removed.Contains(server))];
                foreach (var server in removed)
                {
                    HideTools(server.Entry.Name);
                }

                var added = registered.Servers.Where(server => FindServer(server.Entry.Name) is null).ToList();
                servers.AddRange(added);
                EmitChange();
                EnsureDiscoveryActive(ctx);
                await Task
                    .WhenAll(removed.Select(server => server.Connection?.CloseAsync() ?? Task.CompletedTask))
                    .ConfigureAwait(false);
                var connecting = added.Where(IsEnabled).ToList();
                if (current != generation || connecting.Count == 0)
                {
                    return;
                }

                try
                {
                    await Task
                        .WhenAll(connecting.Select(server => StartConnectionAsync(server, () => current == generation)))
                        .ConfigureAwait(false);
                    if (current != generation)
                    {
                        await Task
                            .WhenAll(connecting.Select(server => server.Connection?.CloseAsync() ?? Task.CompletedTask))
                            .ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception error)
                {
                    ctx.Ui.Notify($"MCP failed to load: {error.Message}", NotifyType.Error);
                    return;
                }

                if (current != generation)
                {
                    return;
                }

                ReportProblems(ctx, connecting);
            });

            pi.On<ExtensionEvent.SessionShutdown>(ExtensionEventNames.SessionShutdown, async (_, _) =>
            {
                sessionActive = false;
                generation++;
                var closing = Connections();
                servers = [];
                EmitChange();
                await Task.WhenAll(closing.Select(connection => connection.CloseAsync())).ConfigureAwait(false);
            });

            // -------------------------------------------------------------------------------
            // `/mcp`
            // -------------------------------------------------------------------------------

            pi.RegisterCommand("mcp", new CommandRegistration
            {
                Description = "Manage MCP servers: sign in, reconnect, enable or disable, and change exposure",
                GetArgumentCompletions = prefix => Task.FromResult(ArgumentCompletions(prefix)),
                Handler = async (args, ctx) =>
                {
                    if (pending is not null)
                    {
                        await pending.ConfigureAwait(false);
                    }

                    var parts = WhitespaceRegex().Split(args.Trim()).Where(part => part.Length > 0).ToList();
                    var action = parts.Count > 0 ? parts[0] : null;
                    var name = parts.Count > 1 ? parts[1] : null;
                    var extra = parts.Skip(2).ToList();
                    if (action is null)
                    {
                        if (ctx.Mode == ExtensionMode.Tui)
                        {
                            await options.ShowManager(ctx, ui => ManageAsync(ui, ctx)).ConfigureAwait(false);
                        }
                        else
                        {
                            ctx.Ui.Notify(FormatStatus(servers, configErrors, overridden), NotifyType.Info);
                        }

                        return;
                    }

                    if (extra.Count > 0)
                    {
                        ctx.Ui.Notify(McpUsage, NotifyType.Warning);
                        return;
                    }

                    switch (action)
                    {
                        case "login":
                        {
                            var server = await PickServerAsync(name, ctx, UsesOAuth, PreferNeedsAuth, oauthNone)
                                .ConfigureAwait(false);
                            if (server is not null)
                            {
                                await LoginCommandAsync(server, ctx).ConfigureAwait(false);
                            }

                            return;
                        }

                        case "logout":
                        {
                            var server = await PickServerAsync(name, ctx, UsesOAuth, PreferNeedsAuth, oauthNone)
                                .ConfigureAwait(false);
                            if (server is null)
                            {
                                return;
                            }

                            var removed = await SignOutAsync(server).ConfigureAwait(false);
                            ctx.Ui.Notify(
                                removed
                                    ? $"Signed out of MCP server \"{server.Entry.Name}\"."
                                    : $"No stored credentials for MCP server \"{server.Entry.Name}\".",
                                NotifyType.Info);
                            return;
                        }

                        case "reconnect":
                        {
                            var server = await PickServerAsync(
                                name,
                                ctx,
                                candidate => candidate.Connection is not null,
                                candidate => candidate.Connection?.State
                                    is McpServerState.Failed or McpServerState.Disconnected,
                                "No enabled MCP server to reconnect.").ConfigureAwait(false);
                            if (server is null)
                            {
                                return;
                            }

                            var failure = await ReconnectAsync(server).ConfigureAwait(false);
                            if (failure is not null)
                            {
                                ctx.Ui.Notify(failure, NotifyType.Error);
                            }
                            else
                            {
                                EnsureDiscoveryActive(ctx);
                                ctx.Ui.Notify(
                                    $"Reconnected to MCP server \"{server.Entry.Name}\" ({DescribeState(server)}).",
                                    NotifyType.Info);
                            }

                            return;
                        }

                        default:
                            ctx.Ui.Notify(McpUsage, NotifyType.Warning);
                            return;
                    }
                },
            });

            IReadOnlyList<AutocompleteItem>? ArgumentCompletions(string prefix)
            {
                var parts = WhitespaceRegex().Split(prefix.TrimStart());
                var action = parts.Length > 0 ? parts[0] : "";
                var server = parts.Length > 1 ? parts[1] : null;
                var rest = parts.Skip(2).ToList();
                if (rest.Count > 0)
                {
                    return null;
                }

                if (server is null)
                {
                    return
                    [
                        .. new[] { "login", "logout", "reconnect" }
                            .Where(item => item.StartsWith(action, StringComparison.Ordinal))
                            .Select(item => new AutocompleteItem { Value = $"{item} ", Label = item }),
                    ];
                }

                if (action is not ("login" or "logout" or "reconnect"))
                {
                    return null;
                }

                var items = servers
                    .Where(candidate => action == "reconnect"
                        ? candidate.Connection is not null
                        : UsesOAuth(candidate))
                    .Where(candidate => candidate.Entry.Name.StartsWith(server, StringComparison.Ordinal))
                    .Select(candidate => new AutocompleteItem
                    {
                        Value = $"{action} {candidate.Entry.Name}",
                        Label = candidate.Entry.Name,
                        Description = DescribeState(candidate),
                    })
                    .ToList();
                return items.Count > 0 ? items : null;
            }

            return Task.CompletedTask;
        };
    }

    /// <summary>Load the default configuration: the agent directory and the trusted project. TS <c>defaultLoadConfig</c>.</summary>
    private static LoadedMcpConfig DefaultLoadConfig(IExtensionContext ctx) =>
        McpConfig.Load(Config.GetAgentDir(), ctx.Cwd, ctx.IsProjectTrusted());

    /// <summary>TS <c>prefix.trimStart().split(/\s+/)</c> and <c>args.trim().split(/\s+/)</c>.</summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>The sign-in prompt of the manager view, which shows the URL with a copy key.</summary>
    private sealed class UiSignInPrompt(IMcpUi ui, string title, Action<string> openUrl) : IMcpSignInPrompt
    {
        private string authorizationUrl = "";

        public void ShowAuthorizationUrl(Uri url)
        {
            authorizationUrl = url.AbsoluteUri;
            openUrl(url.AbsoluteUri);
        }

        public async Task<string?> PromptForRedirectUrlAsync(CancellationToken cancellationToken)
        {
            var value = await ui.RedirectUrlAsync(title, authorizationUrl, cancellationToken).ConfigureAwait(false);
            ui.Status(title, "Connecting…");
            return value;
        }
    }

    /// <summary>The sign-in prompt of <c>/mcp login</c> outside the TUI: notify with the URL, then read a pasted one.</summary>
    private sealed class NonTuiSignInPrompt(
        string name,
        IExtensionCommandContext ctx,
        Action<string> openUrl) : IMcpSignInPrompt
    {
        public void ShowAuthorizationUrl(Uri url)
        {
            ctx.Ui.Notify($"Sign in to MCP server \"{name}\" in your browser:\n{url.AbsoluteUri}", NotifyType.Info);
            openUrl(url.AbsoluteUri);
        }

        public Task<string?> PromptForRedirectUrlAsync(CancellationToken cancellationToken) =>
            ctx.Ui.Input(
                $"Waiting for sign-in to \"{name}\". If the browser cannot reach this machine, paste the URL it was redirected to.",
                "http://127.0.0.1:.../callback?code=...",
                new ExtensionUIDialogOptions { Signal = cancellationToken });
    }


    /// <summary>TS `promise.catch(() => undefined)`: settle without a result and without an error.</summary>
    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The TS source swallows the failure here too; callers read the connection's state instead.
        }
    }

    /// <summary>
    /// TS defers the first connection with <c>setImmediate</c> and only then dynamically imports
    /// <c>runtime.ts</c> ("Waiting one event loop turn lets the first render happen before loading and
    /// connecting"). The port is a single assembly, so there is nothing to load; only the event-loop
    /// turn remains (difference C189).
    /// </summary>
    private static async Task DeferOneTurnAsync() => await Task.Yield();
}
