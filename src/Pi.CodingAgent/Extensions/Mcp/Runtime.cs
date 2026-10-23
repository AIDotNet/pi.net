// ============================================================================
// MCP connections — port of extensions/mcp/runtime.ts (P4b-4)
// ============================================================================
//
// The part of the MCP integration that talks to servers: connections, transports, and OAuth
// sign-in. It pulls in the MCP client, so index.ts loads it through runtime.lazy.ts only when a
// server is configured.
//
// One `McpServerConnection` per configured server. Opening is lazy and memoized, so a server that
// is never used is never contacted, and a second caller waits for the connect already in flight.
// A dropped connection (a stdio server that exited) is transparently re-opened by the next call.
// Tool calls are not retried — they may have run — but read-only requests retry once after a
// transient HTTP error, and a request on an expired session retries once on the new one. Servers
// that reject the request after a refresh need the user to sign in again; their state becomes
// `needs-auth` and `close` waits for the refresh that may still be saving its rotated tokens.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Pi.Mcp;
using Pi.Mcp.OAuth;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Challenge = Pi.Mcp.OAuth.OAuthTypes.Challenge;

namespace Pi.CodingAgent.Extensions.Mcp;

/// <summary>
/// `disconnected`: the connection dropped (for example the stdio server exited); the next call
/// reconnects. TS <c>ServerState</c>.
/// </summary>
public enum McpServerState
{
    Connecting,
    Connected,
    Disconnected,
    NeedsAuth,
    Failed,
    Closed,
}

/// <summary>
/// The TS spelling of each <see cref="McpServerState"/>. TS keeps the state as its wire string
/// (<c>"needs-auth"</c> …) and reports it verbatim in status lines and JSON; the port stores an
/// enum, so every place that prints a state goes through here (difference C188's neighbour).
/// </summary>
public static class McpServerStates
{
    public static string Name(McpServerState state) => state switch
    {
        McpServerState.Connecting => "connecting",
        McpServerState.Connected => "connected",
        McpServerState.Disconnected => "disconnected",
        McpServerState.NeedsAuth => "needs-auth",
        McpServerState.Failed => "failed",
        McpServerState.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unknown MCP server state"),
    };
}

/// <summary>Builds the transport of one server. TS <c>McpTransportFactory</c>.</summary>
public delegate IMcpTransport McpTransportFactory(
    McpServerEntry entry,
    string cwd,
    AuthProvider? authProvider);

/// <summary>Options of <see cref="McpServerConnection"/>. Port of the TS constructor options.</summary>
public sealed record McpServerConnectionOptions
{
    public required McpServerEntry Entry { get; init; }

    public required string Cwd { get; init; }

    public required McpTransportFactory CreateTransport { get; init; }

    public required McpOAuth.McpOAuthCredentialStore Credentials { get; init; }

    /// <summary>The current token of a pi provider, for servers with <c>auth.provider</c>.</summary>
    public Func<string, Task<string?>>? ProviderToken { get; init; }

    public required Action<McpServerConnection> OnTools { get; init; }

    /// <summary>Called when <see cref="McpServerConnection.State"/>, <c>Error</c> or <c>Tools</c> change.</summary>
    public Action<McpServerConnection>? OnChange { get; init; }

    /// <summary>Receives the server's log messages (<c>notifications/message</c>).</summary>
    public McpServerLog? Log { get; init; }
}

/// <summary>
/// One configured server. Reconnects lazily when a call finds the connection gone.
/// Port of <c>McpServerConnection</c>.
/// </summary>
public sealed class McpServerConnection : IMcpToolCaller, McpResourceServer
{
    private const long DefaultTimeoutSeconds = 60;
    private const int StderrTailChars = 2_000;

    /// <summary>Delays between attempts to connect to an HTTP server that failed with a transient error.</summary>
    private static readonly long[] ConnectRetryDelaysMs = [250, 1_000];

    private readonly CancellationTokenSource _shutdown = new();
    private readonly McpTransportFactory _createTransport;
    private readonly McpAuthProvider? _authProvider;
    private readonly Action<McpServerConnection> _onTools;
    private readonly Action<McpServerConnection>? _onChange;
    private readonly McpServerLog? _log;
    private readonly string _cwd;
    private McpClient? _client;
    private Task<McpClient>? _opening;
    private string? _stderrTail;

    public McpServerConnection(McpServerConnectionOptions options)
    {
        Entry = options.Entry;
        _cwd = options.Cwd;
        _createTransport = options.CreateTransport;
        _onTools = options.OnTools;
        _onChange = options.OnChange;
        _log = options.Log;
        var url = OAuthUrl;
        var provider = Entry.Config.Url is not null ? Entry.Config.AuthProvider : null;
        _authProvider = url is not null
            ? McpOAuth.CreateAuthProvider(new McpAuthProviderOptions
            {
                ServerUrl = url,
                Store = options.Credentials.ForServer(Entry.Name, url),
                Settings = () => OAuthSettings(),
                OnChallenge = challenge => Challenge = challenge,
            })
            : provider is not null
                // Read on every request, so the provider's refreshes apply; MCP stores no copy.
                ? new ProviderTokenAuthProvider(options.ProviderToken, provider)
                : null;
    }

    /// <summary>The configured entry of this server.</summary>
    public McpServerEntry Entry { get; }

    public McpServerState State { get; private set; } = McpServerState.Connecting;

    public string? Error { get; private set; }

    public IReadOnlyList<McpTool> Tools { get; private set; } = [];

    /// <summary>
    /// Whether the server offers resources, which the resource tools list and read on demand. They are
    /// not listed at connect: listing every resource can take long (a large drive, say).
    /// </summary>
    public bool HasResources { get; private set; }

    /// <summary>Server instructions from `initialize`, describing its tools as a group.</summary>
    public string? Instructions { get; private set; }

    /// <summary>Last OAuth challenge from the server; sign-in uses its resource metadata URL and scope.</summary>
    public Challenge? Challenge { get; private set; }

    /// <summary>
    /// Forget the stored challenge, after a sign-in used it. TS assigns
    /// <c>connection.challenge = undefined</c>; the port keeps the field read-only and clears it here.
    /// </summary>
    public void ClearChallenge() => Challenge = null;

    /// <summary>Server name as configured, and as listings report it. TS <c>name</c>.</summary>
    public string Name => Entry.Name;

    /// <summary>Request timeout of the server in milliseconds. TS <c>timeoutMs</c>.</summary>
    public long TimeoutMs => (long)((Entry.Config.Timeout ?? DefaultTimeoutSeconds) * 1000);

    /// <summary>Server URL when the server authenticates with OAuth. TS <c>oauthUrl</c>.</summary>
    public string? OAuthUrl => UsesOAuth(Entry) ? Entry.Config.Url : null;

    private bool Closed => _shutdown.IsCancellationRequested;

    /// <summary>OAuth settings of this server. TS <c>oauthSettings</c>.</summary>
    public McpOAuthSettings OAuthSettings()
    {
        var oauth = Entry.Config.OAuth;
        if (oauth is null) return new McpOAuthSettings();
        var url = (oauth["authServerMetadataUrl"] as JsonValue)?.GetValue<string>();
        return new McpOAuthSettings
        {
            ClientId = (oauth["clientId"] as JsonValue)?.GetValue<string>(),
            ClientSecret = (oauth["clientSecret"] as JsonValue)?.GetValue<string>() is { } secret
                ? ConfigValueResolver.ResolveConfigValueOrThrow(
                    secret, $"MCP server \"{Entry.Name}\" oauth.clientSecret")
                : null,
            CallbackPort = (oauth["callbackPort"] as JsonValue)?.GetValue<int>(),
            CallbackUrl = (oauth["callbackUrl"] as JsonValue)?.GetValue<string>(),
            Scope = (oauth["scope"] as JsonValue)?.GetValue<string>(),
            ClientName = (oauth["clientName"] as JsonValue)?.GetValue<string>(),
            ClientRegistration = (oauth["clientRegistration"] as JsonValue)?.GetValue<string>() switch
            {
                "dcr" => McpClientRegistration.Dcr,
                "cimd" => McpClientRegistration.Cimd,
                _ => null,
            },
            AuthServerMetadataUrl = url is null ? null : new Uri(url),
        };
    }

    // ------------------------------------------------------------------ IMcpToolCaller / McpResourceServer

    public Task<CallToolResult> CallToolAsync(
        string name,
        IReadOnlyDictionary<string, object?> args,
        McpCallOptions options,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(client => client.CallToolAsync(name, ToArguments(args), RequestOptions(options), cancellationToken));

    public Task<ReadResourceResult> ReadResourceAsync(
        string uri,
        McpCallOptions options,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            client => client.ReadResourceAsync(uri, RequestOptions(options), cancellationToken), readOnly: true);

    public Task<ListResourcesResult> ResourcesPageAsync(
        string? cursor,
        McpCallOptions options,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            client => client.ListResourcesPageAsync(cursor, RequestOptions(options), cancellationToken), readOnly: true);

    public Task<ListResourceTemplatesResult> ResourceTemplatesPageAsync(
        string? cursor,
        McpCallOptions options,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            client => WithoutTemplatesAsync(
                client.ListResourceTemplatesPageAsync(cursor, RequestOptions(options), cancellationToken),
                new ListResourceTemplatesResult([], null)),
            readOnly: true);

    public Task<IReadOnlyList<McpResource>> AllResourcesAsync(
        McpCallOptions options,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(client => client.ListResourcesAsync(RequestOptions(options), cancellationToken), readOnly: true);

    public Task<IReadOnlyList<McpResourceTemplate>> AllResourceTemplatesAsync(
        McpCallOptions options,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(
            client => ListTemplatesAsync(client, cancellationToken), readOnly: true);

    // ------------------------------------------------------------------ connecting

    /// <summary>The connected client, opening it when needed. TS <c>getClient</c>.</summary>
    public Task<McpClient> GetClientAsync()
    {
        if (Closed)
        {
            return Task.FromException<McpClient>(
                new InvalidOperationException($"MCP server \"{Entry.Name}\" is shut down"));
        }

        if (_client?.State == ClientState.Connected)
        {
            return Task.FromResult(_client);
        }

        return _opening ?? StartOpen();
    }

    /// <summary>
    /// Starts one connect, memoized in <c>_opening</c> until it settles.
    /// </summary>
    /// <remarks>
    /// TS memoizes with <c>this.opening ??= this.open().finally(() =&gt; { this.opening = undefined; })</c>,
    /// where the clear always happens in a later microtask. A C# async method that never suspends runs
    /// its <c>finally</c> *before* the assignment, so the naive port left <c>_opening</c> pointing at a
    /// settled task and every later call — after a drop, a sign-out, or an expired session — reused the
    /// dead client instead of connecting again. Assigning the memo before registering the continuation
    /// keeps the TS order.
    /// </remarks>
    private Task<McpClient> StartOpen()
    {
        var opening = OpenAsync();
        _opening = opening;
        _ = opening.ContinueWith(
            completed =>
            {
                if (ReferenceEquals(_opening, completed))
                {
                    _opening = null;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return opening;
    }

    /// <summary>
    /// Run a request, reconnecting when needed. <paramref name="readOnly"/> requests are retried once
    /// after a transient HTTP error; tool calls are not, since they may have run.
    /// </summary>
    private async Task<T> WithClientAsync<T>(Func<McpClient, Task<T>> run, bool readOnly = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            var client = await GetClientAsync().ConfigureAwait(false);
            try
            {
                return await run(client).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (readOnly && attempt == 1 && error is McpHttpError http && IsTransientError(http))
                {
                    await Task.Delay((int)ConnectRetryDelaysMs[0]).ConfigureAwait(false);
                    continue;
                }

                if (error is McpSessionExpiredError && attempt == 1)
                {
                    // The server no longer knows the session (restart, deploy), so it did not run the
                    // request. Retry once on a new session. The old client is detached but not closed:
                    // closing would fail its other in-flight calls, which instead get the same 404 and
                    // retry the same way.
                    if (ReferenceEquals(_client, client))
                    {
                        _client = null;
                    }

                    continue;
                }

                if (!NeedsSignIn(error))
                {
                    throw;
                }

                await DropClientAsync(client).ConfigureAwait(false);
                MarkNeedsAuth();
                throw new InvalidOperationException(SignInRequiredMessage(Entry));
            }
        }
    }

    /// <summary>Connect again with fresh credentials, for example after signing in.</summary>
    public async Task ReconnectAsync()
    {
        if (_opening is not null)
        {
            await SwallowAsync(_opening).ConfigureAwait(false);
        }

        if (_client is not null)
        {
            await DropClientAsync(_client).ConfigureAwait(false);
        }

        await GetClientAsync().ConfigureAwait(false);
    }

    /// <summary>Disconnect after the stored credentials were removed.</summary>
    public async Task SignOutAsync()
    {
        if (_opening is not null)
        {
            await SwallowAsync(_opening).ConfigureAwait(false);
        }

        if (_client is not null)
        {
            await DropClientAsync(_client).ConfigureAwait(false);
        }

        if (!Closed)
        {
            MarkNeedsAuth();
        }
    }

    /// <summary>OAuth servers that still reject the request after a refresh need the user to sign in again.</summary>
    private bool NeedsSignIn(Exception error) =>
        error is McpOAuthAuthorizationRequiredError
        || (_authProvider is not null && error is McpAuthRequiredError);

    private void MarkNeedsAuth()
    {
        State = McpServerState.NeedsAuth;
        Error = null;
        Changed();
    }

    private void Changed() => _onChange?.Invoke(this);

    private async Task DropClientAsync(McpClient client)
    {
        if (ReferenceEquals(_client, client))
        {
            _client = null;
        }

        try
        {
            await client.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore: closing is best effort, and a failed close must not mask the original error.
        }
    }

    private async Task<McpClient> OpenAsync()
    {
        State = McpServerState.Connecting;
        Changed();
        var retries = Entry.Config.Url is not null ? ConnectRetryDelaysMs : [];
        for (var attempt = 0; ; attempt++)
        {
            _stderrTail = null;
            try
            {
                return await ConnectOnceAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var delay = attempt < retries.Length ? retries[attempt] : (long?)null;
                if (Closed || delay is null || !IsTransientError(error))
                {
                    throw ConnectFailed(error);
                }

                try
                {
                    await Task.Delay((int)delay.Value, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // `close()` aborted the wait between retries.
                }

                if (Closed)
                {
                    throw ConnectFailed(error);
                }
            }
        }
    }

    private async Task<McpClient> ConnectOnceAsync()
    {
        var fullPath = Path.GetFullPath(_cwd);
        var client = new McpClient(new McpClientOptions
        {
            Name = "pi",
            Version = Config.Version,
            RequestTimeoutMs = TimeoutMs,
            Roots = [new McpRoot(new Uri(fullPath).AbsoluteUri, Path.GetFileName(fullPath))],
        });
        var log = _log;
        if (log is not null)
        {
            client.OnNotification(
                "notifications/message",
                parameters =>
                {
                    log.Write(Entry.Name, parameters);
                    return Task.CompletedTask;
                });
        }

        // Closing the client fails its pending initialize or discovery requests and closes the transport.
        // Memoized: a second close returns before the first finished closing the transport.
        var closing = new Lazy<Task>(
            () => SwallowAsync(client.CloseAsync()), LazyThreadSafetyMode.ExecutionAndPublication);
        var registration = _shutdown.Token.Register(() => _ = closing.Value);
        IMcpTransport? transport = null;
        try
        {
            transport = _createTransport(Entry, _cwd, _authProvider);
            await client.ConnectAsync(transport, _shutdown.Token).ConfigureAwait(false);
            client.OnNotification(
                "notifications/tools/list_changed",
                _notification =>
                {
                    _ = RefreshToolsAsync(client);
                    return Task.CompletedTask;
                });
            var stdio = transport as StdioTransport;
            client.OnClose(() => HandleClientClose(client, stdio));
            // TS reads `serverCapabilities?.tools` / `?.resources`: a capability the server
            // declared — `"tools": {}` — is truthy; `"tools": null` is not.
            var capabilities = client.ServerCapabilities?.Raw;
            var hasTools = HasCapability(capabilities, "tools");
            var tools = hasTools
                ? await client.ListToolsAsync(cancellationToken: _shutdown.Token).ConfigureAwait(false)
                : [];
            if (Closed)
            {
                throw new InvalidOperationException("shut down while connecting");
            }

            if (client.State != ClientState.Connected)
            {
                throw new InvalidOperationException("connection closed during setup");
            }

            _client = client;
            Tools = tools;
            HasResources = HasCapability(capabilities, "resources");
            Instructions = string.IsNullOrWhiteSpace(client.Instructions) ? null : client.Instructions.Trim();
            State = McpServerState.Connected;
            Error = null;
            _onTools(this);
            Changed();
            return client;
        }
        catch (Exception)
        {
            await closing.Value.ConfigureAwait(false);
            if (transport is StdioTransport broken)
            {
                _stderrTail = StderrTail(broken.Stderr);
            }

            throw;
        }
        finally
        {
            registration.Dispose();
        }
    }

    private InvalidOperationException ConnectFailed(Exception error)
    {
        if (NeedsSignIn(error) && !Closed)
        {
            MarkNeedsAuth();
            return new InvalidOperationException(SignInRequiredMessage(Entry));
        }

        State = Closed ? McpServerState.Closed : McpServerState.Failed;
        Error = _stderrTail is null ? ErrorMessage(error) : $"{ErrorMessage(error)}\n{_stderrTail}";
        Changed();
        return new InvalidOperationException($"MCP server \"{Entry.Name}\" failed to connect: {Error}");
    }

    /// <summary>The transport dropped. The next call reconnects; until then the status shows why.</summary>
    private void HandleClientClose(McpClient client, StdioTransport? stdio)
    {
        if (!ReferenceEquals(_client, client) || Closed)
        {
            return;
        }

        _client = null;
        State = McpServerState.Disconnected;
        var stderr = StderrTail(stdio?.Stderr);
        Error = stderr is null ? "Connection closed" : $"Connection closed\n{stderr}";
        Changed();
    }

    private async Task RefreshToolsAsync(McpClient client)
    {
        try
        {
            var tools = await client.ListToolsAsync().ConfigureAwait(false);
            if (!ReferenceEquals(_client, client) || Closed)
            {
                return;
            }

            Tools = tools;
            _onTools(this);
        }
        catch (Exception error)
        {
            Error = $"Failed to refresh tools: {ErrorMessage(error)}";
        }

        Changed();
    }

    /// <summary>Resolves once no transport of this server is open, including one that was still connecting.</summary>
    public async Task CloseAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        State = McpServerState.Closed;
        Changed();
        var client = _client;
        _client = null;
        var closing = client is null ? Task.CompletedTask : SwallowAsync(client.CloseAsync());
        await Task.WhenAll(closing, _opening is null ? Task.CompletedTask : SwallowAsync(_opening)).ConfigureAwait(false);
        // A refresh the server already answered may have rotated the refresh token; exiting before the
        // new tokens are saved would lose the grant.
        if (_authProvider is not null)
        {
            await _authProvider.SettledAsync().ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ statics and helpers

    private static bool HasCapability(JsonObject? capabilities, string name) =>
        capabilities is not null && capabilities[name] is not null;

    /// <summary>Servers that do not implement `resources/templates/list` have no templates.</summary>
    private static async Task<T> WithoutTemplatesAsync<T>(Task<T> list, T empty)
    {
        try
        {
            return await list.ConfigureAwait(false);
        }
        catch (McpError error) when (error.Code == JsonRpcErrorCodes.MethodNotFound)
        {
            return empty;
        }
    }

    private static Task<IReadOnlyList<McpResourceTemplate>> ListTemplatesAsync(
        McpClient client, CancellationToken cancellationToken = default) =>
        WithoutTemplatesAsync(client.ListResourceTemplatesAsync(cancellationToken: cancellationToken), []);

    /// <summary>Network failures and overloaded or restarting servers, which are worth another attempt.</summary>
    internal static bool IsTransientError(Exception error) =>
        error is McpHttpError http
            ? http.Status == 408 || http.Status == 429 || (http.Status >= 500 && http.Status != 501)
            : error is HttpRequestException;

    internal static string SignInRequiredMessage(McpServerEntry entry)
    {
        var provider = entry.Config.Url is not null ? entry.Config.AuthProvider : null;
        return $"MCP server \"{entry.Name}\" requires sign-in. " +
            $"Run {(provider is not null ? $"/login {provider}" : "/mcp")} to sign in.";
    }

    /// <summary>HTTP servers authenticate with OAuth unless the config supplies an `Authorization` header or `auth`.</summary>
    internal static bool UsesOAuth(McpServerEntry entry)
    {
        var config = entry.Config;
        if (config.Url is null || config.Value["auth"] is JsonObject)
        {
            return false;
        }

        var headers = config.Headers;
        return headers is null || !headers.Keys.Any(header => header.ToLowerInvariant() == "authorization");
    }

    /// <summary>`~` and `~/…` (also `~\…` on Windows) name the home directory, like in a shell.</summary>
    internal static string ExpandHome(string value)
    {
        if (value == "~")
        {
            return Paths.GetHomeDirectory();
        }

        if (value.StartsWith("~/", StringComparison.Ordinal)
            || (OperatingSystem.IsWindows() && value.StartsWith("~\\", StringComparison.Ordinal)))
        {
            return Path.Combine(Paths.GetHomeDirectory(), value[2..]);
        }

        return value;
    }

    /// <summary>The transport of one server from its config. TS <c>createDefaultTransport</c>.</summary>
    public static IMcpTransport CreateDefaultTransport(
        McpServerEntry entry, string cwd, AuthProvider? authProvider)
    {
        var config = entry.Config;
        var url = config.Url;
        if (url is not null)
        {
            return new StreamableHttpTransport(new StreamableHttpTransportOptions
            {
                Url = url,
                Headers = ConfigValueResolver.ResolveHeadersOrThrow(
                    config.Headers, $"MCP server \"{entry.Name}\""),
                AuthProvider = authProvider,
            });
        }

        var command = config.Command
            ?? throw new InvalidOperationException($"MCP server \"{entry.Name}\" has no command or url");
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config.Env is not null)
        {
            foreach (var (key, value) in config.Env)
            {
                env[key] = ConfigValueResolver.ResolveConfigValueOrThrow(
                    value, $"MCP server \"{entry.Name}\" env \"{key}\"");
            }
        }

        var serverCwd = Path.GetFullPath(Path.Combine(cwd, ExpandHome(config.Cwd ?? ".")));
        return new StdioTransport(
            ExpandHome(command),
            config.Args?.Select(ExpandHome).ToList(),
            env.Count > 0 ? env : null,
            serverCwd);
    }

    private static McpRequestOptions RequestOptions(McpCallOptions options) => new()
    {
        TimeoutMs = options.TimeoutMs,
        OnProgress = options.OnProgress is null
            ? null
            : progress => options.OnProgress(
                new McpProgress(progress.Progress, progress.Total, progress.Message)),
    };

    private static JsonObject ToArguments(IReadOnlyDictionary<string, object?>? args)
    {
        var json = new JsonObject();
        if (args is null)
        {
            return json;
        }

        foreach (var (key, value) in args)
        {
            // 必须 DeepClone：调用方（Pi.Agent 的 ValidateToolArguments）给出的是已挂在
            // 参数对象下的子节点，直接塞进新的 JsonObject 会抛 "The node already has a parent."。
            json[key] = value switch
            {
                null => null,
                JsonNode node => node.DeepClone(),
                string text => JsonValue.Create(text),
                _ => JsonSerializer.SerializeToNode(value),
            };
        }

        return json;
    }

    private static string ErrorMessage(Exception error) => error.Message;

    /// <summary>The last <see cref="StderrTailChars"/> characters of a server's stderr, or null when it is blank.</summary>
    internal static string? StderrTail(string? stderr)
    {
        if (stderr is null)
        {
            return null;
        }

        var trimmed = stderr.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length <= StderrTailChars ? trimmed : trimmed[^StderrTailChars..];
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Ignore: closing and aborted connects are best effort.
        }
    }

    /// <summary>
    /// Sends a pi provider's token instead of using OAuth. TS <c>{ token, settled }</c>: no refresh of
    /// its own, so <see cref="McpAuthProvider.SettledAsync"/> completes immediately.
    /// </summary>
    private sealed class ProviderTokenAuthProvider(
        Func<string, Task<string?>>? providerToken, string provider) : McpAuthProvider
    {
        public Task<string?> Token(CancellationToken cancellationToken = default) =>
            providerToken?.Invoke(provider) ?? Task.FromResult<string?>(null);

        public Task SettledAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
