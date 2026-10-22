// ============================================================================
// `pi mcp` — port of extensions/mcp/cli.ts (P4b-5)
// ============================================================================
//
// Add, remove, and check MCP servers and sign in to them outside a session. Agents run it through
// bash to configure servers, verify an `mcp.json` they wrote, and start an OAuth sign-in; the user
// only approves access in the browser. Running sessions pick up new credentials on their next turn.
//
// The port keeps the TS text and exit codes exactly (`pi mcp list` exits 1 while any enabled server
// is not connected or any config error was reported, so a script can gate on it). Two seams were
// added for testability, where TS reaches for `createDefaultTransport` and `process.stdin` directly
// (differences C187/C188).

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using JsNumber = Pi.CodingAgent.Utils.JsNumber;

namespace Pi.CodingAgent.Extensions.Mcp;

/// <summary>Options of <see cref="McpCli.RunMcpCommandAsync"/>. Port of the TS <c>McpCommandOptions</c>.</summary>
public sealed record McpCommandOptions
{
    public required string Cwd { get; init; }

    public required string AgentDir { get; init; }

    /// <summary>Defaults to <c>mcp-auth.json</c> in the agent directory.</summary>
    public McpOAuth.McpOAuthCredentialStore? Credentials { get; init; }

    /// <summary>Defaults to the platform browser. TS <c>openUrl</c>.</summary>
    public Action<string>? OpenUrl { get; init; }

    /// <summary>Defaults to console output. TS <c>log</c>.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Defaults to console error. TS <c>error</c>.</summary>
    public Action<string>? Error { get; init; }

    /// <summary>
    /// Whether stdin is a terminal, so a pasted redirect URL can be read. Defaults to the TS rule
    /// (<c>process.stdin.isTTY === true &amp;&amp; openUrl === undefined</c>); set it to exercise the
    /// pasted-URL path with an injected <see cref="OpenUrl"/>.
    /// </summary>
    public bool? Interactive { get; init; }

    /// <summary>Reads one line for the pasted redirect URL. Defaults to the console.</summary>
    public Func<CancellationToken, Task<string?>>? ReadLine { get; init; }

    /// <summary>Builds the transport of one server. Defaults to <c>createDefaultTransport</c>.</summary>
    public McpTransportFactory? CreateTransport { get; init; }
}

/// <summary>One server's row of <c>pi mcp list</c>. TS <c>ServerReport</c>.</summary>
public sealed class McpServerReport
{
    public required string Name { get; init; }

    public required string Scope { get; init; }

    public required string Source { get; init; }

    /// <summary>Project <c>mcp.json</c> that overrides <c>enabled</c>, <c>exposure</c>, or <c>toolExposure</c> of this global server.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Override { get; init; }

    public required bool Enabled { get; init; }

    public required string Exposure { get; init; }

    public required string Transport { get; init; }

    public string State { get; set; } = "disabled";

    public IReadOnlyList<string> Tools { get; set; } = [];

    /// <summary>Tools whose exposure differs from the server's, from <c>toolExposure</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? ToolExposure { get; set; }

    /// <summary>
    /// Whether the server offers resources. They are not listed: listing every one can take long.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Resources { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>Port of <c>extensions/mcp/cli.ts</c>.</summary>
public static class McpCli
{
    private const long DefaultLoginTimeoutSeconds = 300;

    /// <summary>The prompt of the pasted redirect URL. TS reads it through <c>readline</c> on stderr.</summary>
    private const string RedirectPrompt = "If the browser cannot reach this machine, paste the URL it was redirected to: ";

    private static readonly string Help = $$"""
        {{Chalk.Bold("Usage:")}}
          {{Config.AppName}} mcp add <server> [options] -- <command> [args...]
          {{Config.AppName}} mcp add <server> [options] --url <url>
          {{Config.AppName}} mcp remove <server> [-l]
          {{Config.AppName}} mcp list [--json]
          {{Config.AppName}} mcp login <server> [--timeout <seconds>]
          {{Config.AppName}} mcp logout <server>

        Configure and check MCP servers and sign in to OAuth servers without starting a session.
        Reads ~/{{Config.ConfigDirName}}/agent/mcp.json and, in trusted projects, {{Config.ConfigDirName}}/mcp.json.

        Commands:
          add <server>            Add or replace a server in mcp.json
          remove <server>         Remove a server from mcp.json
          list                    Show state, tools, and errors (exits 1 on failure)
          login <server>          Sign in through the browser
          logout <server>         Delete the stored OAuth credentials

        Options for add and remove:
          -l, --local             Use {{Config.ConfigDirName}}/mcp.json in the current project instead of the global file

        Options for add:
          --url <url>             Streamable HTTP server URL (instead of a command)
          --env <KEY=VALUE>       Environment variable for a stdio server (repeatable)
          --cwd <dir>             Working directory for a stdio server
          --header <KEY=VALUE>    HTTP header (repeatable)
          --bearer-token-env-var <NAME>
                                  Send "Authorization: Bearer ${NAME}"
          --oauth-client-id <id>  Pre-registered OAuth client id
          --oauth-client-secret <secret>
                                  OAuth client secret (may be ${NAME} or !command)
          --oauth-callback-port <port>
                                  Fixed OAuth callback port
          --oauth-client-name <name>
                                  Client name sent when registering with the OAuth server
          --exposure <mode>       codemode (default), deferred, direct, or hidden
          --description <text>    What the server offers, shown in the system prompt

        Other options:
          --json                  Print the list as JSON
          --timeout <seconds>     How long login waits for the browser (default: 300)
        """;

    private static readonly string HelpHint = Chalk.Dim($"Use \"{Config.AppName} mcp --help\" for usage.");

    /// <summary>Short spellings of options. <c>-l</c>/<c>--local</c> match <c>pi install</c>.</summary>
    private static readonly Dictionary<string, string> OptionAliases = new(StringComparer.Ordinal)
    {
        ["-l"] = "--local",
    };

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        // JSON.stringify always writes "\n"; .NET 9+ defaults to Environment.NewLine.
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Run <c>pi mcp &lt;args&gt;</c> and return the exit code. TS <c>runMcpCommand</c>.</summary>
    public static async Task<int> RunMcpCommandAsync(string[] args, McpCommandOptions options)
    {
        var log = options.Log ?? Console.WriteLine;
        var error = options.Error ?? Console.Error.WriteLine;
        var command = args.Length > 0 ? args[0] : null;
        var rest = args.Length > 1 ? args[1..] : [];
        if (command is null || command == "help" || args.Contains("--help") || args.Contains("-h"))
        {
            log(Help);
            return 0;
        }

        var projectConfig = Path.Combine(options.Cwd, Config.ConfigDirName, "mcp.json");
        if (command is "add" or "remove")
        {
            return command == "add"
                ? Add(rest, projectConfig, options, log, error)
                : Remove(rest, projectConfig, options, log, error);
        }

        var projectTrusted = new ProjectTrustStore(options.AgentDir).Get(options.Cwd) == true;
        var loaded = McpConfig.Load(options.AgentDir, options.Cwd, projectTrusted);
        var untrustedNote = !projectTrusted && File.Exists(projectConfig)
            ? $"{projectConfig} is ignored because the project is not trusted. " +
                $"Start {Config.AppName} in the project to trust it."
            : null;
        var credentials = options.Credentials ?? new McpOAuth.McpOAuthCredentialStore();

        switch (command)
        {
            case "list":
            {
                var parsed = ParseOptions(rest, new Dictionary<string, OptionKind>(StringComparer.Ordinal)
                {
                    ["json"] = OptionKind.Flag,
                }, error);
                if (parsed is null)
                {
                    return 1;
                }

                if (parsed.Positional.Count > 0)
                {
                    error($"Usage: {Config.AppName} mcp list [--json]\n{HelpHint}");
                    return 1;
                }

                return await ListAsync(loaded, parsed.Flags.Contains("json"), untrustedNote, options, credentials, log)
                    .ConfigureAwait(false);
            }

            case "login":
            case "logout":
            {
                var parsed = ParseOptions(
                    rest,
                    command == "login"
                        ? new Dictionary<string, OptionKind>(StringComparer.Ordinal) { ["timeout"] = OptionKind.Value }
                        : new Dictionary<string, OptionKind>(StringComparer.Ordinal),
                    error);
                if (parsed is null)
                {
                    return 1;
                }

                var name = parsed.Positional.Count > 0 ? parsed.Positional[0] : null;
                if (string.IsNullOrEmpty(name) || parsed.Positional.Count > 1)
                {
                    error($"Usage: {Config.AppName} mcp {command} <server>\n{HelpHint}");
                    return 1;
                }

                var entry = loaded.Servers.FirstOrDefault(server => server.Name == name);
                if (entry is null)
                {
                    var configured = loaded.Servers.Count > 0
                        ? string.Join(", ", loaded.Servers.Select(server => server.Name))
                        : "none";
                    error($"No MCP server named \"{name}\"." +
                        $"{(untrustedNote is not null ? $" {untrustedNote}" : "")} Configured: {configured}.");
                    return 1;
                }

                var connection = CreateConnection(entry, options, credentials);
                var url = connection.OAuthUrl;
                if (url is null)
                {
                    error($"MCP server \"{name}\" does not use OAuth. " +
                        "Only HTTP servers without an Authorization header do.");
                    return 1;
                }

                if (command == "logout")
                {
                    var removed = await credentials.RemoveAsync(name, url).ConfigureAwait(false);
                    log(removed
                        ? $"Signed out of MCP server \"{name}\"."
                        : $"No stored credentials for MCP server \"{name}\".");
                    return 0;
                }

                var timeout = JsNumber.Parse(parsed.Value("timeout") ?? DefaultLoginTimeoutSeconds.ToString());
                if (!double.IsFinite(timeout) || timeout <= 0)
                {
                    error("--timeout must be a positive number of seconds.");
                    return 1;
                }

                try
                {
                    return await LoginAsync(
                        entry, connection, url, (long)(timeout * 1000), options, credentials, log, error)
                        .ConfigureAwait(false);
                }
                finally
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }
            }

            default:
                error($"Unknown mcp command \"{command}\".\n{HelpHint}");
                return 1;
        }
    }

    // ------------------------------------------------------------------ add / remove

    private static int Add(
        string[] args,
        string projectConfig,
        McpCommandOptions options,
        Action<string> log,
        Action<string> error)
    {
        var usage =
            $"Usage: {Config.AppName} mcp add <server> [options] (--url <url> | -- <command> [args...])\n{HelpHint}";
        var parsed = ParseOptions(
            args,
            new Dictionary<string, OptionKind>(StringComparer.Ordinal)
            {
                ["local"] = OptionKind.Flag,
                ["url"] = OptionKind.Value,
                ["env"] = OptionKind.List,
                ["cwd"] = OptionKind.Value,
                ["header"] = OptionKind.List,
                ["bearer-token-env-var"] = OptionKind.Value,
                ["oauth-client-id"] = OptionKind.Value,
                ["oauth-client-secret"] = OptionKind.Value,
                ["oauth-callback-port"] = OptionKind.Value,
                ["oauth-client-name"] = OptionKind.Value,
                ["exposure"] = OptionKind.Value,
                ["description"] = OptionKind.Value,
            },
            error,
            maxPositionals: 2);
        if (parsed is null)
        {
            return 1;
        }

        var name = parsed.Positional.Count > 0 ? parsed.Positional[0] : null;
        var command = parsed.Positional.Skip(1).ToList();
        var url = parsed.Value("url");
        if (string.IsNullOrEmpty(name) || (url is null) == (command.Count == 0))
        {
            error(usage);
            return 1;
        }

        var exposure = parsed.Value("exposure");
        string[] httpOnly =
        [
            "header",
            "bearer-token-env-var",
            "oauth-client-id",
            "oauth-client-secret",
            "oauth-callback-port",
            "oauth-client-name",
        ];
        string[] stdioOnly = ["env", "cwd"];
        var misplaced = (url is null ? httpOnly : stdioOnly).FirstOrDefault(parsed.Has);
        if (misplaced is not null)
        {
            error($"--{misplaced} only applies to {(url is null ? "HTTP servers (--url)" : "stdio servers")}.");
            return 1;
        }

        JsonObject config;
        if (url is not null)
        {
            var headers = ParsePairs("header", parsed.List("header"), error);
            if (headers is null)
            {
                return 1;
            }

            if (parsed.Value("bearer-token-env-var") is { } bearer)
            {
                headers["Authorization"] = $"Bearer ${{{bearer}}}";
            }

            var oauth = new JsonObject();
            if (parsed.Value("oauth-client-id") is { } clientId)
            {
                oauth["clientId"] = clientId;
            }

            if (parsed.Value("oauth-client-secret") is { } clientSecret)
            {
                oauth["clientSecret"] = clientSecret;
            }

            if (parsed.Value("oauth-callback-port") is { } port)
            {
                // TS `Number(port)`: JSON.stringify writes a non-finite number as null, which the
                // config validator then rejects with "oauth.callbackPort must be a port number".
                var number = JsNumber.Parse(port);
                oauth["callbackPort"] = double.IsFinite(number) ? JsonValue.Create(number) : null;
            }

            if (parsed.Value("oauth-client-name") is { } clientName)
            {
                oauth["clientName"] = clientName;
            }

            config = new JsonObject { ["url"] = url };
            if (headers.Count > 0)
            {
                config["headers"] = ToJsonObject(headers);
            }

            if (oauth.Count > 0)
            {
                config["oauth"] = oauth;
            }
        }
        else
        {
            var env = ParsePairs("env", parsed.List("env"), error);
            if (env is null)
            {
                return 1;
            }

            config = new JsonObject { ["command"] = command[0] };
            if (command.Count > 1)
            {
                config["args"] = new JsonArray([.. command.Skip(1).Select(arg => (JsonNode)JsonValue.Create(arg)!)]);
            }

            if (env.Count > 0)
            {
                config["env"] = ToJsonObject(env);
            }

            if (parsed.Value("cwd") is { } cwd)
            {
                config["cwd"] = cwd;
            }
        }

        if (exposure is not null)
        {
            config["exposure"] = exposure;
        }

        if (parsed.Value("description") is { } description)
        {
            config["description"] = description;
        }

        var validation = McpServers.ValidateMcpServerConfig(name, config);
        if (validation.Error is not null)
        {
            error(validation.Error);
            return 1;
        }

        var validated = validation.Config!;
        var project = parsed.Flags.Contains("local");
        var path = project ? projectConfig : Path.Combine(options.AgentDir, "mcp.json");
        var scope = project ? "project" : "global";
        bool replaced;
        try
        {
            replaced = McpConfig.Add(path, name, validated);
        }
        catch (Exception addError)
        {
            error($"Could not update {path}: {addError.Message}");
            return 1;
        }

        log($"{(replaced ? "Replaced" : "Added")} {scope} MCP server \"{name}\" in {path}.");
        if (project && new ProjectTrustStore(options.AgentDir).Get(options.Cwd) != true)
        {
            log($"The project is not trusted, so {path} is ignored until you start " +
                $"{Config.AppName} in the project and trust it.");
        }

        // HTTP servers without an Authorization header may use OAuth.
        var mayNeedSignIn = validated.Url is not null
            && !(validated.Headers?.Keys.Any(header => header.ToLowerInvariant() == "authorization") ?? false);
        log($"Check it with: {Config.AppName} mcp list" +
            $"{(mayNeedSignIn ? $". If it requires sign-in: {Config.AppName} mcp login {name}" : "")}");
        return 0;
    }

    private static int Remove(
        string[] args,
        string projectConfig,
        McpCommandOptions options,
        Action<string> log,
        Action<string> error)
    {
        var parsed = ParseOptions(
            args,
            new Dictionary<string, OptionKind>(StringComparer.Ordinal) { ["local"] = OptionKind.Flag },
            error);
        if (parsed is null)
        {
            return 1;
        }

        var name = parsed.Positional.Count > 0 ? parsed.Positional[0] : null;
        if (string.IsNullOrEmpty(name) || parsed.Positional.Count > 1)
        {
            error($"Usage: {Config.AppName} mcp remove <server> [-l]\n{HelpHint}");
            return 1;
        }

        var project = parsed.Flags.Contains("local");
        var globalConfig = Path.Combine(options.AgentDir, "mcp.json");
        var path = project ? projectConfig : globalConfig;
        var scope = project ? "project" : "global";
        bool removed;
        try
        {
            removed = McpConfig.Remove(path, name);
        }
        catch (Exception removeError)
        {
            error($"Could not update {path}: {removeError.Message}");
            return 1;
        }

        if (removed)
        {
            log($"Removed {scope} MCP server \"{name}\" from {path}.");
            return 0;
        }

        var other = McpConfig.Load(options.AgentDir, options.Cwd, projectTrusted: true).Servers
            .FirstOrDefault(server => server.Name == name && server.Scope != scope);
        var hint = other is null
            ? ""
            : $" It is defined in {other.Source}{(other.Scope == "project" ? "; use --local" : "; omit --local")}.";
        error($"No {scope} MCP server named \"{name}\" in {path}.{hint}");
        return 1;
    }

    // ------------------------------------------------------------------ list

    private static async Task<int> ListAsync(
        LoadedMcpConfig loaded,
        bool json,
        string? untrustedNote,
        McpCommandOptions options,
        McpOAuth.McpOAuthCredentialStore credentials,
        Action<string> log)
    {
        // TS `Promise.all`: the servers are checked at once, in configuration order.
        var reports = await Task.WhenAll(loaded.Servers.Select(
            entry => ReportAsync(entry, options, credentials))).ConfigureAwait(false);
        var failed = loaded.Errors.Count > 0
            || reports.Any(report => report.Enabled && report.State != "connected");

        if (json)
        {
            var payload = new JsonObject
            {
                ["servers"] = JsonSerializer.SerializeToNode(reports, ReportJsonOptions),
                ["errors"] = new JsonArray([.. loaded.Errors.Select(text => (JsonNode)JsonValue.Create(text)!)]),
            };
            if (untrustedNote is not null)
            {
                payload["note"] = untrustedNote;
            }

            log(payload.ToJsonString(ReportJsonOptions));
            return failed ? 1 : 0;
        }

        if (reports.Length == 0 && loaded.Errors.Count == 0)
        {
            log($"No MCP servers configured. Add them to {Path.Combine(options.AgentDir, "mcp.json")} " +
                $"or {Config.ConfigDirName}/mcp.json.");
        }

        foreach (var report in reports)
        {
            var state = report.State == "connected"
                ? $"connected, {report.Tools.Count} tool{(report.Tools.Count == 1 ? "" : "s")}"
                : report.State == "needs-auth"
                    ? "needs sign-in"
                    : report.State;
            log($"{report.Name}: {state} ({report.Exposure}, {report.Scope})");
            log($"  {report.Transport}");
            if (report.Override is not null)
            {
                log($"  project override: {report.Override}");
            }

            if (report.State == "needs-auth")
            {
                log($"  sign in with: {Config.AppName} mcp login {report.Name}");
            }

            if (report.Tools.Count > 0)
            {
                var tools = report.Tools.Select(tool =>
                    report.ToolExposure is not null && report.ToolExposure.TryGetValue(tool, out var exposure)
                        ? $"{tool} [{exposure}]"
                        : tool);
                log($"  tools: {string.Join(", ", tools)}");
            }

            if (report.Resources == true)
            {
                log("  offers resources");
            }

            if (report.Error is not null)
            {
                log($"  {report.Error.Replace("\n", "\n  ")}");
            }
        }

        foreach (var configError in loaded.Errors)
        {
            log($"config error: {configError}");
        }

        if (untrustedNote is not null)
        {
            log(untrustedNote);
        }

        return failed ? 1 : 0;
    }

    private static async Task<McpServerReport> ReportAsync(
        McpServerEntry entry,
        McpCommandOptions options,
        McpOAuth.McpOAuthCredentialStore credentials)
    {
        var report = new McpServerReport
        {
            Name = entry.Name,
            Scope = entry.Scope ?? "global",
            Source = entry.Source,
            Override = entry.Override,
            Enabled = entry.Config.Enabled != false,
            Exposure = entry.Config.Exposure ?? "codemode",
            Transport = DescribeTransport(entry),
        };
        if (!report.Enabled)
        {
            return report;
        }

        var connection = CreateConnection(entry, options, credentials);
        try
        {
            await connection.GetClientAsync().ConfigureAwait(false);
        }
        catch
        {
            // The connection records the state and error.
        }

        report.State = McpServerStates.Name(connection.State);
        report.Tools = [.. connection.Tools.Select(tool => tool.Name)];
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in connection.Tools)
        {
            var exposure = McpServers.GetMcpToolExposure(entry.Config, tool.Name);
            if (exposure != report.Exposure)
            {
                overrides[tool.Name] = exposure;
            }
        }

        if (overrides.Count > 0)
        {
            report.ToolExposure = overrides;
        }

        if (connection.HasResources)
        {
            report.Resources = true;
        }

        if (connection.State != McpServerState.Connected && connection.Error is not null)
        {
            report.Error = connection.Error;
        }

        await connection.CloseAsync().ConfigureAwait(false);
        return report;
    }

    // ------------------------------------------------------------------ login

    private static async Task<int> LoginAsync(
        McpServerEntry entry,
        McpServerConnection connection,
        string url,
        long timeoutMs,
        McpCommandOptions options,
        McpOAuth.McpOAuthCredentialStore credentials,
        Action<string> log,
        Action<string> error)
    {
        var name = entry.Name;
        // Connecting first answers whether a sign-in is needed and records the server's challenge.
        try
        {
            await connection.GetClientAsync().ConfigureAwait(false);
            log($"Already signed in to MCP server \"{name}\" ({connection.Tools.Count} tools).");
            return 0;
        }
        catch
        {
            if (connection.State != McpServerState.NeedsAuth)
            {
                error($"MCP server \"{name}\" failed to connect: {connection.Error ?? "unknown error"}");
                return 1;
            }
        }

        var openUrl = options.OpenUrl ?? OpenBrowser.Open;
        var interactive = options.Interactive ?? (options.OpenUrl is null && IsInputTerminal());
        try
        {
            await McpOAuth.SignInServerAsync(new McpSignInOptions
            {
                ServerUrl = url,
                Store = credentials.ForServer(name, url),
                Settings = connection.OAuthSettings(),
                Challenge = connection.Challenge,
                Prompt = new CliSignInPrompt(
                    authorizationUrl =>
                    {
                        log($"Sign in to MCP server \"{name}\" in your browser:\n{authorizationUrl.AbsoluteUri}");
                        openUrl(authorizationUrl.AbsoluteUri);
                    },
                    cancellationToken => WaitForRedirectUrlAsync(
                        cancellationToken, timeoutMs, interactive, options.ReadLine)),
            }).ConfigureAwait(false);
        }
        catch (Exception signInError)
        {
            error(signInError is McpSignInCancelledError
                ? $"Sign-in to MCP server \"{name}\" was cancelled or not completed within " +
                    $"{JsRound(timeoutMs / 1000d)} seconds."
                : $"Sign-in to MCP server \"{name}\" failed: {signInError.Message}");
            return 1;
        }

        connection.ClearChallenge();
        try
        {
            await connection.ReconnectAsync().ConfigureAwait(false);
        }
        catch (Exception connectError)
        {
            error($"Signed in, but {connectError.Message}");
            return 1;
        }

        log($"Signed in to MCP server \"{name}\" ({connection.Tools.Count} tools).");
        return 0;
    }

    /// <summary>
    /// The pasted redirect URL in a terminal; otherwise only the browser callback can finish the
    /// sign-in. Resolves to null (cancelling the sign-in) after <paramref name="timeoutMs"/>, or when
    /// the callback arrived.
    /// </summary>
    private static async Task<string?> WaitForRedirectUrlAsync(
        CancellationToken cancellationToken,
        long timeoutMs,
        bool interactive,
        Func<CancellationToken, Task<string?>>? readLine)
    {
        using var timeout = new CancellationTokenSource();
        if (timeoutMs > 0)
        {
            timeout.CancelAfter(timeoutMs > int.MaxValue ? int.MaxValue : (int)timeoutMs);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;
        if (!interactive)
        {
            await WaitForCancellationAsync(token).ConfigureAwait(false);
            return null;
        }

        try
        {
            return await (readLine ?? ReadRedirectLineAsync)(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The callback arrived, the timeout elapsed, or the sign-in was cancelled.
        }
    }

    /// <summary>
    /// Read the redirect URL from the terminal. <c>Console.In</c> ignores a cancellation token while
    /// it blocks, so the read is raced against the token instead of being cancelled in place.
    /// </summary>
    private static async Task<string?> ReadRedirectLineAsync(CancellationToken cancellationToken)
    {
        Console.Error.Write(RedirectPrompt);
        var read = Task.Run(Console.In.ReadLine, CancellationToken.None);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
        if (await Task.WhenAny(read, cancelled.Task).ConfigureAwait(false) != read)
        {
            return null;
        }

        return await read.ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Whether stdin is a terminal. TS <c>process.stdin.isTTY === true</c>.</summary>
    private static bool IsInputTerminal()
    {
        try
        {
            return !Console.IsInputRedirected;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The TS <c>Math.round</c>: halves round up, not to even as <see cref="Math.Round(double)"/> does.</summary>
    private static long JsRound(double value) => (long)Math.Floor(value + 0.5);

    private static string DescribeTransport(McpServerEntry entry)
    {
        var config = entry.Config;
        if (config.Url is not null)
        {
            return config.Url;
        }

        // TS joins `[config.command, ...config.args]`, so a missing command prints as `undefined`.
        return string.Join(" ", new[] { config.Command ?? "undefined" }.Concat(config.Args ?? []));
    }

    private static McpServerConnection CreateConnection(
        McpServerEntry entry,
        McpCommandOptions options,
        McpOAuth.McpOAuthCredentialStore credentials) =>
        new(new McpServerConnectionOptions
        {
            Entry = entry,
            Cwd = options.Cwd,
            CreateTransport = options.CreateTransport ?? McpServerConnection.CreateDefaultTransport,
            Credentials = credentials,
            Log = new McpServerLog(Path.Combine(options.AgentDir, "mcp.log")),
            OnTools = _ => { },
        });

    /// <summary>Parse <c>KEY=VALUE</c> pairs of a repeatable option into a record.</summary>
    private static Dictionary<string, string>? ParsePairs(
        string option, List<string>? pairs, Action<string> error)
    {
        var record = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pairs is null)
        {
            return record;
        }

        foreach (var pair in pairs)
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                error($"--{option} expects KEY=VALUE, got \"{pair}\".");
                return null;
            }

            record[pair[..separator]] = pair[(separator + 1)..];
        }

        return record;
    }

    private static JsonObject ToJsonObject(Dictionary<string, string> record)
    {
        var json = new JsonObject();
        foreach (var (key, value) in record)
        {
            json[key] = value;
        }

        return json;
    }

    /// <summary>How an option is parsed. TS <c>"flag" | "value" | "list"</c>.</summary>
    private enum OptionKind
    {
        Flag,
        Value,
        List,
    }

    /// <summary>The parsed arguments. TS <c>ParsedOptions</c>.</summary>
    private sealed class ParsedOptions
    {
        public List<string> Positional { get; } = [];

        public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        /// <summary>Values of <c>list</c> options, in order.</summary>
        public Dictionary<string, List<string>> Lists { get; } = new(StringComparer.Ordinal);

        public bool Has(string name) =>
            Flags.Contains(name) || Values.ContainsKey(name) || Lists.ContainsKey(name);

        public string? Value(string name) => Values.TryGetValue(name, out var value) ? value : null;

        public List<string>? List(string name) => Lists.TryGetValue(name, out var list) ? list : null;
    }

    /// <summary>
    /// Parse <c>--name value</c> options; returns null and reports unknown ones. <c>--</c> ends the
    /// options, as does reaching <paramref name="maxPositionals"/> positional arguments: the remaining
    /// arguments are positional, so a command's own options (<c>add &lt;server&gt; &lt;command&gt; --flag</c>)
    /// are passed through.
    /// </summary>
    private static ParsedOptions? ParseOptions(
        string[] args,
        IReadOnlyDictionary<string, OptionKind> known,
        Action<string> error,
        int maxPositionals = int.MaxValue)
    {
        var parsed = new ParsedOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = OptionAliases.TryGetValue(args[index], out var alias) ? alias : args[index];
            if (arg == "--" || parsed.Positional.Count >= maxPositionals)
            {
                var start = arg == "--" ? index + 1 : index;
                for (var rest = start; rest < args.Length; rest++)
                {
                    parsed.Positional.Add(args[rest]);
                }

                break;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                parsed.Positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            if (!known.TryGetValue(name, out var kind))
            {
                error($"Unknown option {arg}.\n{HelpHint}");
                return null;
            }

            if (kind == OptionKind.Flag)
            {
                parsed.Flags.Add(name);
                continue;
            }

            if (index + 1 >= args.Length)
            {
                error($"{arg} needs a value.");
                return null;
            }

            var value = args[++index];
            if (kind == OptionKind.List)
            {
                if (!parsed.Lists.TryGetValue(name, out var list))
                {
                    list = [];
                    parsed.Lists[name] = list;
                }

                list.Add(value);
            }
            else
            {
                parsed.Values[name] = value;
            }
        }

        return parsed;
    }

    /// <summary>The sign-in prompt of <c>pi mcp login</c>. TS passes an object literal.</summary>
    private sealed class CliSignInPrompt(
        Action<Uri> showAuthorizationUrl,
        Func<CancellationToken, Task<string?>> promptForRedirectUrl) : IMcpSignInPrompt
    {
        public void ShowAuthorizationUrl(Uri url) => showAuthorizationUrl(url);

        public Task<string?> PromptForRedirectUrlAsync(CancellationToken cancellationToken) =>
            promptForRedirectUrl(cancellationToken);
    }
}
