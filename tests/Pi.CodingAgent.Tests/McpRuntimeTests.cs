using System.Text.Json.Nodes;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Extensions.Mcp;
using Pi.CodingAgent.Utils;
using Pi.Mcp;
using Pi.Mcp.OAuth;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the MCP connections (port of <c>extensions/mcp/runtime.ts</c>, batch P4b-4).
/// A scripted <see cref="FakeMcpServer"/> drives <see cref="McpServerConnection"/>: lazy and memoized
/// opening, the capability-gated discovery, the transient-error retry of read-only requests, the
/// session-expired reconnect, needs-auth, the transport drop that the next call repairs, and the
/// notification-driven refreshes. The five pure leaves (<c>isTransientError</c>,
/// <c>signInRequiredMessage</c>, <c>usesOAuth</c>, <c>expandHome</c>, the stderr tail) carry their own
/// boundary vectors.
/// </summary>
public class McpRuntimeTests
{
    private const string StdioConfig = """{"command":"demo-server"}""";
    private const string HttpConfig = """{"url":"https://example.com/mcp"}""";
    private const string ProviderConfig = """{"url":"https://example.com/mcp","auth":{"provider":"anthropic"}}""";

    private static readonly IReadOnlyDictionary<string, object?> NoArgs = new Dictionary<string, object?>();

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// A scripted MCP server: answers requests by method, can fail one request with an injected
    /// exception, can notify, and can drop the connection like a stdio server that exited.
    /// </summary>
    private sealed class FakeMcpServer : TransportEvents
    {
        private readonly Dictionary<string, Func<JsonRpcMessage.Request, JsonNode?>> _handlers =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, Queue<Exception>> _failures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly List<JsonRpcMessage.Request> _requests = [];
        private readonly List<string> _notifications = [];

        /// <summary>How often the transport was started: one per connect attempt.</summary>
        public int ConnectCount { get; private set; }

        public IReadOnlyList<JsonRpcMessage.Request> Requests => _requests;

        public IReadOnlyList<string> Notifications => _notifications;

        public int Count(string method) => _counts.GetValueOrDefault(method);

        /// <summary>The parameters of the <paramref name="occurrence"/>-th request of a method.</summary>
        public JsonObject? ParamsOf(string method, int occurrence = 0) =>
            _requests.Where(request => request.Method == method).ElementAtOrDefault(occurrence)?.Params as JsonObject;

        public void On(string method, Func<JsonRpcMessage.Request, JsonNode?> handler) => _handlers[method] = handler;

        /// <summary>Fails the next request of <paramref name="method"/> with <paramref name="error"/>.</summary>
        public void FailNext(string method, Exception error)
        {
            if (!_failures.TryGetValue(method, out var queue))
            {
                queue = new Queue<Exception>();
                _failures[method] = queue;
            }

            queue.Enqueue(error);
        }

        public void SendNotification(string method, JsonNode? parameters = null) =>
            EmitMessage(new JsonRpcMessage.Notification(method, parameters));

        /// <summary>The transport dropped, as a stdio server that exited does.</summary>
        public void Drop() => EmitClose();

        public override Task StartAsync(CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            return Task.CompletedTask;
        }

        public override Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            switch (message)
            {
                case JsonRpcMessage.Request request:
                    _requests.Add(request);
                    _counts[request.Method] = _counts.GetValueOrDefault(request.Method) + 1;
                    if (_failures.TryGetValue(request.Method, out var queue) && queue.Count > 0)
                    {
                        throw queue.Dequeue();
                    }

                    if (_handlers.TryGetValue(request.Method, out var handler))
                    {
                        EmitMessage(new JsonRpcMessage.SuccessResponse(request.Id, handler(request)));
                        break;
                    }

                    // An unimplemented method, as `resources/templates/list` is on many servers.
                    EmitMessage(new JsonRpcMessage.ErrorResponse(request.Id,
                        new JsonRpcErrorObject(JsonRpcErrorCodes.MethodNotFound, "Method not found")));
                    break;

                case JsonRpcMessage.Notification notification:
                    _notifications.Add(notification.Method);
                    break;
            }

            return Task.CompletedTask;
        }

        public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>In-memory auth file, so tests never touch the user's <c>mcp-auth.json</c>.</summary>
    private sealed class FakeAuthStorageBackend : IAuthStorageBackend
    {
        public T WithLock<T>(Func<string?, LockResult<T>> fn) => fn(null).Result;

        public async Task<T> WithLockAsync<T>(
            Func<string?, Task<LockResult<T>>> fn, CancellationToken cancellationToken = default) =>
            (await fn(null)).Result;
    }

    /// <summary>One connection under test, plus the transports it created and the callbacks it fired.</summary>
    private sealed class Harness : IDisposable
    {
        public List<FakeMcpServer> Servers { get; } = [];

        public List<McpServerState> Changes { get; } = [];

        public List<string> ProviderTokenCalls { get; } = [];

        public int ToolsCallbacks { get; private set; }

        /// <summary>The auth provider handed to the transport factory.</summary>
        public AuthProvider? Auth { get; private set; }

        public McpServerLog? Log { get; set; }

        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "pi-mcp-runtime-" + Guid.NewGuid().ToString("N")[..8]);

        /// <summary>The transport of the most recent connect attempt.</summary>
        public FakeMcpServer Server => Servers[^1];

        public McpServerConnection Connect(string config, Action<FakeMcpServer>? configure = null)
        {
            var entry = new McpServerEntry("demo", new McpServerConfig(JsonNode.Parse(config)!.AsObject()), "test");
            return new McpServerConnection(new McpServerConnectionOptions
            {
                Entry = entry,
                Cwd = Root,
                CreateTransport = (_, _, auth) =>
                {
                    Auth = auth;
                    var server = new FakeMcpServer();
                    ConfigureDefaults(server);
                    configure?.Invoke(server);
                    Servers.Add(server);
                    return server;
                },
                Credentials = new McpOAuth.McpOAuthCredentialStore(new FakeAuthStorageBackend()),
                ProviderToken = provider =>
                {
                    ProviderTokenCalls.Add(provider);
                    return Task.FromResult<string?>("tok-anthropic");
                },
                OnTools = _ => ToolsCallbacks++,
                OnChange = connection => Changes.Add(connection.State),
                Log = Log,
            });
        }

        /// <summary>Connect and wait for the handshake and the discovery that follow it.</summary>
        public async Task<McpServerConnection> ConnectedAsync(
            string config = StdioConfig, Action<FakeMcpServer>? configure = null)
        {
            var connection = Connect(config, configure);
            await connection.GetClientAsync();
            return connection;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    /// <summary>The answers a healthy server gives, before a test overrides one of them.</summary>
    private static void ConfigureDefaults(FakeMcpServer server)
    {
        server.On("initialize", _ => Initialize("""{"tools":{},"resources":{}}"""));
        server.On("tools/list", _ => JsonNode.Parse(
            """{"tools":[{"name":"read_file","description":"read","inputSchema":{"type":"object"}}]}"""));
        server.On("resources/list", _ => JsonNode.Parse(
            """{"resources":[{"uri":"ui://app/home","name":"app"},{"uri":"docs://readme","name":"readme"}]}"""));
        server.On("resources/read", _ => JsonNode.Parse(
            """{"contents":[{"uri":"docs://readme","mimeType":"text/plain","text":"hello"}]}"""));
        server.On("tools/call", _ => JsonNode.Parse(
            """{"content":[{"type":"text","text":"ok"}],"isError":false}"""));
    }

    private static JsonNode Initialize(string capabilities, string? instructions = null)
    {
        var result = new JsonObject
        {
            ["protocolVersion"] = "2025-11-25",
            ["capabilities"] = JsonNode.Parse(capabilities),
            ["serverInfo"] = new JsonObject { ["name"] = "fake-server", ["version"] = "1.0.0" },
        };
        if (instructions is not null)
        {
            result["instructions"] = instructions;
        }

        return result;
    }

    /// <summary>Polls until <paramref name="condition"/> holds, so notification work can land.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"timed out waiting for {what}");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Runs <paramref name="run"/> and returns the expected exception, failing on anything else.</summary>
    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    {
        try
        {
            await run();
        }
        catch (T expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new InvalidOperationException(
                $"expected {typeof(T).Name}, got {other.GetType().Name}: {other.Message}", other);
        }

        throw new InvalidOperationException($"expected {typeof(T).Name}, but nothing was thrown");
    }

    // ------------------------------------------------------------------ pure leaves

    [Fact]
    public void IsTransientErrorCoversNetworkAndRestartingServers()
    {
        // 408, 429, and 5xx except 501: worth another attempt.
        foreach (var status in new long[] { 408, 429, 500, 502, 503, 504 })
        {
            Assert.True(McpServerConnection.IsTransientError(new McpHttpError(status, "busy")), $"status {status}");
        }

        Assert.False(McpServerConnection.IsTransientError(new McpHttpError(501, "not implemented")));
        Assert.False(McpServerConnection.IsTransientError(new McpHttpError(400, "bad request")));
        Assert.False(McpServerConnection.IsTransientError(new McpHttpError(404, "not found")));
        Assert.True(McpServerConnection.IsTransientError(new HttpRequestException("socket")));
        Assert.False(McpServerConnection.IsTransientError(new McpError(JsonRpcErrorCodes.InternalError, "boom")));
        Assert.False(McpServerConnection.IsTransientError(new McpSessionExpiredError()));
        Assert.False(McpServerConnection.IsTransientError(new InvalidOperationException("boom")));
    }

    [Fact]
    public void SignInRequiredMessageNamesTheProviderWhenThereIsOne()
    {
        var stdio = new McpServerEntry("demo", Config("""{"command":"x"}"""), "test");
        Assert.Equal("MCP server \"demo\" requires sign-in. Run /mcp to sign in.",
            McpServerConnection.SignInRequiredMessage(stdio));

        var url = new McpServerEntry("demo", Config(HttpConfig), "test");
        Assert.Equal("MCP server \"demo\" requires sign-in. Run /mcp to sign in.",
            McpServerConnection.SignInRequiredMessage(url));

        var provider = new McpServerEntry("demo", Config(ProviderConfig), "test");
        Assert.Equal("MCP server \"demo\" requires sign-in. Run /login anthropic to sign in.",
            McpServerConnection.SignInRequiredMessage(provider));
    }

    [Fact]
    public void UsesOAuthOnlyForHttpServersWithoutCredentialsInTheConfig()
    {
        Assert.True(McpServerConnection.UsesOAuth(new McpServerEntry("demo", Config(HttpConfig), "test")));
        Assert.True(McpServerConnection.UsesOAuth(new McpServerEntry("demo", Config(
            """{"url":"https://example.com/mcp","headers":{"X-Trace":"1"}}"""), "test")));

        // An Authorization header, however it is spelled, replaces OAuth.
        Assert.False(McpServerConnection.UsesOAuth(new McpServerEntry("demo", Config(
            """{"url":"https://example.com/mcp","headers":{"Authorization":"Bearer t"}}"""), "test")));
        Assert.False(McpServerConnection.UsesOAuth(new McpServerEntry("demo", Config(
            """{"url":"https://example.com/mcp","headers":{"AUTHORIZATION":"Bearer t"}}"""), "test")));

        // So does `auth`, which names a pi provider instead.
        Assert.False(McpServerConnection.UsesOAuth(new McpServerEntry("demo", Config(ProviderConfig), "test")));
        Assert.False(McpServerConnection.UsesOAuth(new McpServerEntry("demo", Config(StdioConfig), "test")));
    }

    [Fact]
    public void ExpandHomeResolvesTildeLikeAShell()
    {
        var home = Paths.GetHomeDirectory();
        Assert.Equal(home, McpServerConnection.ExpandHome("~"));
        Assert.Equal(Path.Combine(home, "bin/server"), McpServerConnection.ExpandHome("~/bin/server"));

        // `~\…` is the Windows spelling of the same thing.
        Assert.Equal(OperatingSystem.IsWindows() ? Path.Combine(home, "bin") : "~\\bin",
            McpServerConnection.ExpandHome("~\\bin"));

        Assert.Equal("/usr/local/bin", McpServerConnection.ExpandHome("/usr/local/bin"));
        Assert.Equal("node", McpServerConnection.ExpandHome("node"));

        // `~user` is not a home reference.
        Assert.Equal("~user/bin", McpServerConnection.ExpandHome("~user/bin"));
    }

    [Fact]
    public void StderrTailKeepsTheLastTwoThousandCharacters()
    {
        Assert.Null(McpServerConnection.StderrTail(null));
        Assert.Null(McpServerConnection.StderrTail(""));
        Assert.Null(McpServerConnection.StderrTail("  \n\t "));
        Assert.Equal("boom", McpServerConnection.StderrTail("boom\n"));
        Assert.Equal("boom", McpServerConnection.StderrTail("  boom  "));

        var long_ = new string('x', 2_500) + "TAIL";
        var tail = McpServerConnection.StderrTail(long_);
        Assert.NotNull(tail);
        Assert.Equal(2_000, tail!.Length);
        Assert.EndsWith("TAIL", tail);
    }

    // ------------------------------------------------------------------ opening

    [Fact]
    public async Task ConnectIsLazyUntilTheFirstCall()
    {
        using var harness = new Harness();
        var connection = harness.Connect(StdioConfig);

        Assert.Equal(McpServerState.Connecting, connection.State);
        Assert.Empty(harness.Servers);

        var resources = await connection.AllResourcesAsync(new McpCallOptions());

        // `allResources` is the raw list; MCP App resources are dropped from the listings instead.
        Assert.Equal(2, resources.Count);
        Assert.Contains(resources, resource => resource.Uri == "docs://readme");
        Assert.Equal(McpServerState.Connected, connection.State);
        Assert.Equal([McpServerState.Connecting, McpServerState.Connected], harness.Changes);
        Assert.Single(harness.Servers);
    }

    [Fact]
    public async Task ConcurrentCallsShareOneConnect()
    {
        using var harness = new Harness();
        var connection = harness.Connect(StdioConfig);

        var first = connection.AllResourcesAsync(new McpCallOptions());
        var second = connection.CallToolAsync("read_file", NoArgs, new McpCallOptions());

        await Task.WhenAll(first, second);

        Assert.Single(harness.Servers);
        var connects = harness.Server.Count("initialize");
        Assert.Equal(1, connects);
    }

    [Fact]
    public async Task ConnectListsToolsButNotResources()
    {
        // #10526：列出全部资源可能很慢（例如一个大型网盘），所以连接时只问 initialize 和 tools/list；
        // 资源由资源工具按需列举与读取。
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig, server =>
        {
            server.On("initialize", _ => Initialize("""{"tools":{},"resources":{}}""", "  Plan first  "));
            server.On("resources/templates/list", _ => JsonNode.Parse(
                """{"resourceTemplates":[{"uriTemplate":"docs://{id}","name":"doc"}]}"""));
        });

        Assert.Equal("read_file", Assert.Single(connection.Tools).Name);
        Assert.True(connection.HasResources);
        Assert.Equal(0, harness.Server.Count("resources/list"));
        Assert.Equal(0, harness.Server.Count("resources/templates/list"));
        Assert.Equal("Plan first", connection.Instructions);
        Assert.Null(connection.Error);
        Assert.Equal("demo", connection.Name);
    }

    [Fact]
    public async Task ConnectDropsBlankInstructions()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig, server =>
            server.On("initialize", _ => Initialize("""{"tools":{}}""", "   ")));

        Assert.Null(connection.Instructions);
    }

    [Fact]
    public async Task ServersWithoutTheToolsCapabilityAreNotAskedForTools()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig, server =>
            server.On("initialize", _ => Initialize("""{"resources":{}}""")));

        Assert.Empty(connection.Tools);
        Assert.True(connection.HasResources);
        var toolsLists = harness.Server.Count("tools/list");
        Assert.Equal(0, toolsLists);
    }

    [Fact]
    public async Task ANullCapabilityIsNotACapability()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig, server =>
            server.On("initialize", _ => Initialize("""{"tools":null,"resources":{}}""")));

        Assert.Empty(connection.Tools);
        var toolsLists = harness.Server.Count("tools/list");
        Assert.Equal(0, toolsLists);
    }

    [Fact]
    public async Task ServersWithoutTheResourcesCapabilityAreNotAskedForResources()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig, server =>
            server.On("initialize", _ => Initialize("""{"tools":{}}""")));

        Assert.False(connection.HasResources);
        var resourceLists = harness.Server.Count("resources/list");
        Assert.Equal(0, resourceLists);
    }

    [Fact]
    public async Task AResourcesListThatFailsDoesNotAffectTheConnect()
    {
        // 连接只问 initialize 与 tools/list，所以一个会失败的 resources/list 连调用都不会发生；
        // 等到资源工具按需列举时才真的失败。
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig, server =>
            server.FailNext("resources/list", new McpError(JsonRpcErrorCodes.InternalError, "listing broke")));

        Assert.Equal(McpServerState.Connected, connection.State);
        Assert.True(connection.HasResources);

        var error = await ThrowsAsync<McpError>(
            async () => await connection.AllResourcesAsync(new McpCallOptions()));
        Assert.Equal("listing broke", error.Message);
    }

    [Fact]
    public async Task ServersWithoutTemplateListingHaveNoTemplates()
    {
        using var harness = new Harness();
        // The fake answers method-not-found for `resources/templates/list` unless a test scripts it.
        var connection = await harness.ConnectedAsync(StdioConfig);

        Assert.Equal(McpServerState.Connected, connection.State);

        var page = await connection.ResourceTemplatesPageAsync(null, new McpCallOptions());
        Assert.Empty(page.ResourceTemplates);
        Assert.Null(page.NextCursor);

        var all = await connection.AllResourceTemplatesAsync(new McpCallOptions());
        Assert.Empty(all);
    }

    [Fact]
    public async Task AFailingToolsListFailsTheConnect()
    {
        using var harness = new Harness();
        var connection = harness.Connect(StdioConfig, server =>
            server.FailNext("tools/list", new McpError(JsonRpcErrorCodes.InternalError, "no tools for you")));

        var error = await ThrowsAsync<InvalidOperationException>(async () => await connection.GetClientAsync());

        Assert.Equal(McpServerState.Failed, connection.State);
        Assert.Equal("no tools for you", connection.Error);
        Assert.Equal("MCP server \"demo\" failed to connect: no tools for you", error.Message);
        Assert.Equal([McpServerState.Connecting, McpServerState.Failed], harness.Changes);
    }

    [Fact]
    public async Task AFailedConnectIsReportedWithTheServerName()
    {
        using var harness = new Harness();
        var connection = harness.Connect(StdioConfig, server =>
            server.FailNext("initialize", new InvalidOperationException("spawn failed")));

        var error = await ThrowsAsync<InvalidOperationException>(async () => await connection.GetClientAsync());

        Assert.Equal(McpServerState.Failed, connection.State);
        Assert.Equal("spawn failed", connection.Error);
        Assert.Equal("MCP server \"demo\" failed to connect: spawn failed", error.Message);
        Assert.Single(harness.Servers);
    }

    // ------------------------------------------------------------------ retries and sign-in

    [Fact]
    public async Task AReadOnlyRequestRetriesOnceAfterATransientHttpError()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(HttpConfig);
        harness.Server.FailNext("resources/list", new McpHttpError(503, "starting up"));

        var resources = await connection.AllResourcesAsync(new McpCallOptions());

        Assert.Equal(2, resources.Count);
        // 连接不再列资源，所以这里是「失败的调用 + 重试」两次。
        var attempts = harness.Server.Count("resources/list");
        Assert.Equal(2, attempts);
        // The retry reuses the client: a transient 503 did not lose the session.
        Assert.Single(harness.Servers);
    }

    [Fact]
    public async Task AToolCallIsNotRetriedAfterATransientHttpError()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(HttpConfig);
        harness.Server.FailNext("tools/call", new McpHttpError(503, "starting up"));

        var error = await ThrowsAsync<McpHttpError>(
            async () => await connection.CallToolAsync("read_file", NoArgs, new McpCallOptions()));

        Assert.Equal(503, error.Status);
        var attempts = harness.Server.Count("tools/call");
        Assert.Equal(1, attempts);
        Assert.Single(harness.Servers);
    }

    [Fact]
    public async Task ANonTransientHttpErrorOnAReadOnlyRequestIsNotRetried()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(HttpConfig);
        harness.Server.FailNext("resources/list", new McpHttpError(501, "not implemented"));

        var error = await ThrowsAsync<McpHttpError>(
            async () => await connection.AllResourcesAsync(new McpCallOptions()));

        Assert.Equal(501, error.Status);
        var attempts = harness.Server.Count("resources/list");
        Assert.Equal(1, attempts); // 连接不列资源，只剩这次失败的调用，没有重试
        Assert.Single(harness.Servers);
    }

    [Fact]
    public async Task AnExpiredSessionIsRetriedOnceOnANewSession()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig);
        harness.Server.FailNext("tools/call", new McpSessionExpiredError("no such session"));

        var result = await connection.CallToolAsync("read_file", NoArgs, new McpCallOptions());

        Assert.Equal("ok", Assert.Single(result.Content).Raw["text"]?.GetValue<string>());
        Assert.Equal(2, harness.Servers.Count);
        Assert.Equal(McpServerState.Connected, connection.State);
    }

    [Fact]
    public async Task AnExpiredSessionIsNotRetriedTwice()
    {
        using var harness = new Harness();
        // Every transport this connection creates fails its first tool call the same way.
        var connection = await harness.ConnectedAsync(StdioConfig, server =>
            server.FailNext("tools/call", new McpSessionExpiredError()));

        await ThrowsAsync<McpSessionExpiredError>(
            async () => await connection.CallToolAsync("read_file", NoArgs, new McpCallOptions()));

        Assert.Equal(2, harness.Servers.Count);
    }

    [Fact]
    public async Task AnAuthRequiredErrorOnARequestMarksTheServerAsNeedingSignIn()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(HttpConfig);
        harness.Server.FailNext("resources/list", new McpAuthRequiredError("Bearer realm=\"mcp\""));

        var error = await ThrowsAsync<InvalidOperationException>(
            async () => await connection.AllResourcesAsync(new McpCallOptions()));

        Assert.Equal(McpServerState.NeedsAuth, connection.State);
        Assert.Null(connection.Error);
        Assert.Equal("MCP server \"demo\" requires sign-in. Run /mcp to sign in.", error.Message);
    }

    [Fact]
    public async Task SignInMessageNamesThePiProviderOfAnAuthConfig()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(ProviderConfig);
        harness.Server.FailNext("tools/call", new McpAuthRequiredError());

        var error = await ThrowsAsync<InvalidOperationException>(
            async () => await connection.CallToolAsync("read_file", NoArgs, new McpCallOptions()));

        Assert.Equal(McpServerState.NeedsAuth, connection.State);
        Assert.Equal("MCP server \"demo\" requires sign-in. Run /login anthropic to sign in.", error.Message);
    }

    [Fact]
    public async Task AnAuthRequiredErrorWithoutAnAuthProviderIsNotASignIn()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig);
        harness.Server.FailNext("tools/call", new McpAuthRequiredError());

        var error = await ThrowsAsync<McpAuthRequiredError>(
            async () => await connection.CallToolAsync("read_file", NoArgs, new McpCallOptions()));

        // Without an auth provider there is nothing to refresh, so the error reaches the caller as is.
        Assert.NotNull(error);
        Assert.Equal(McpServerState.Connected, connection.State);
    }

    [Fact]
    public async Task TheProviderTokenIsReadOnEveryRequest()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(ProviderConfig);

        Assert.NotNull(harness.Auth);
        Assert.Equal("tok-anthropic", await harness.Auth!.Token());
        Assert.Equal(["anthropic"], harness.ProviderTokenCalls);
    }

    [Fact]
    public async Task AnHttpServerRetriesATransientConnectError()
    {
        using var harness = new Harness();
        var armed = false;
        var connection = harness.Connect(HttpConfig, server =>
        {
            // Only the first transport fails: the retry connects to a healthy server.
            if (armed) return;
            armed = true;
            server.FailNext("initialize", new McpHttpError(503, "starting up"));
        });

        await connection.GetClientAsync();

        Assert.Equal(McpServerState.Connected, connection.State);
        Assert.Equal(2, harness.Servers.Count);
    }

    [Fact]
    public async Task AStdioServerDoesNotRetryATransientConnectError()
    {
        using var harness = new Harness();
        var connection = harness.Connect(StdioConfig, server =>
            server.FailNext("initialize", new McpHttpError(503, "starting up")));

        await ThrowsAsync<InvalidOperationException>(async () => await connection.GetClientAsync());

        Assert.Equal(McpServerState.Failed, connection.State);
        Assert.Single(harness.Servers);
    }

    [Fact]
    public async Task AConnectRejectedForAuthorizationNeedsSignIn()
    {
        using var harness = new Harness();
        var connection = harness.Connect(HttpConfig, server =>
            server.FailNext("initialize", new McpAuthRequiredError()));

        var error = await ThrowsAsync<InvalidOperationException>(async () => await connection.GetClientAsync());

        Assert.Equal(McpServerState.NeedsAuth, connection.State);
        Assert.Null(connection.Error);
        Assert.Equal("MCP server \"demo\" requires sign-in. Run /mcp to sign in.", error.Message);
    }

    [Fact]
    public async Task AOAuthAuthorizationRequiredErrorNeedsSignInEvenOnAStdioServer()
    {
        using var harness = new Harness();
        var connection = harness.Connect(StdioConfig, server =>
            server.FailNext("initialize", new McpOAuthAuthorizationRequiredError()));

        var error = await ThrowsAsync<InvalidOperationException>(async () => await connection.GetClientAsync());

        Assert.Equal(McpServerState.NeedsAuth, connection.State);
        Assert.Contains("requires sign-in", error.Message);
    }

    // ------------------------------------------------------------------ dropped and closed connections

    [Fact]
    public async Task ADroppedTransportIsReconnectedByTheNextCall()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig);

        harness.Server.Drop();
        await WaitUntilAsync(() => connection.State == McpServerState.Disconnected, "the drop to land");

        Assert.Equal("Connection closed", connection.Error);

        var resources = await connection.AllResourcesAsync(new McpCallOptions());

        Assert.Equal(2, resources.Count);
        Assert.Contains(resources, resource => resource.Uri == "docs://readme");
        Assert.Equal(McpServerState.Connected, connection.State);
        Assert.Null(connection.Error);
        Assert.Equal(2, harness.Servers.Count);
    }

    [Fact]
    public async Task ADroppedTransportAfterCloseIsIgnored()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig);
        var server = harness.Server;

        await connection.CloseAsync();
        server.Drop();

        Assert.Equal(McpServerState.Closed, connection.State);
        Assert.Equal([McpServerState.Connecting, McpServerState.Connected, McpServerState.Closed], harness.Changes);
    }

    [Fact]
    public async Task CloseShutsDownFurtherCalls()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig);

        await connection.CloseAsync();

        Assert.Equal(McpServerState.Closed, connection.State);
        var error = await ThrowsAsync<InvalidOperationException>(async () => await connection.GetClientAsync());
        Assert.Equal("MCP server \"demo\" is shut down", error.Message);

        var callError = await ThrowsAsync<InvalidOperationException>(
            async () => await connection.CallToolAsync("read_file", NoArgs, new McpCallOptions()));
        Assert.Equal("MCP server \"demo\" is shut down", callError.Message);
    }

    [Fact]
    public async Task ReconnectDropsTheClientAndConnectsAgain()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig);

        await connection.ReconnectAsync();

        Assert.Equal(2, harness.Servers.Count);
        Assert.Equal(McpServerState.Connected, connection.State);
    }

    [Fact]
    public async Task SignOutDisconnectsAndNeedsSignInAgain()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig);

        await connection.SignOutAsync();

        Assert.Equal(McpServerState.NeedsAuth, connection.State);
        Assert.Null(connection.Error);
        // Signing out drops the client without connecting again; the next call reconnects.
        Assert.Single(harness.Servers);

        await connection.AllResourcesAsync(new McpCallOptions());

        Assert.Equal(2, harness.Servers.Count);
        Assert.Equal(McpServerState.Connected, connection.State);
    }

    // ------------------------------------------------------------------ notifications

    [Fact]
    public async Task ServerLogMessagesAreWrittenToTheLog()
    {
        using var harness = new Harness();
        var logPath = Path.Combine(harness.Root, "mcp.log");
        harness.Log = new McpServerLog(logPath);

        var connection = await harness.ConnectedAsync(StdioConfig);
        harness.Server.SendNotification("notifications/message",
            JsonNode.Parse("""{"level":"warning","logger":"boot","data":"disk almost full"}"""));

        await WaitUntilAsync(
            () => File.Exists(logPath) && File.ReadAllText(logPath).Contains("disk almost full"),
            "the log line");

        Assert.Contains("[demo] warning boot: disk almost full", File.ReadAllText(logPath));
        Assert.Contains("notifications/initialized", harness.Server.Notifications);
        Assert.Equal(McpServerState.Connected, connection.State);
    }

    [Fact]
    public async Task AToolsListChangedNotificationRefreshesTheTools()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig);
        var callbacksBefore = harness.ToolsCallbacks;

        harness.Server.On("tools/list", _ => JsonNode.Parse(
            """{"tools":[{"name":"read_file","inputSchema":{"type":"object"}},{"name":"write_file","inputSchema":{"type":"object"}}]}"""));
        harness.Server.SendNotification("notifications/tools/list_changed");

        await WaitUntilAsync(() => connection.Tools.Count == 2, "the refreshed tools");

        Assert.Equal("write_file", connection.Tools[1].Name);
        Assert.Equal(callbacksBefore + 1, harness.ToolsCallbacks);
        Assert.Null(connection.Error);
    }

    [Fact]
    public async Task AResourcesListChangedNotificationIsIgnored()
    {
        // #10526：连接不再缓存资源，`notifications/resources/list_changed` 也就没有可刷新的东西；
        // 资源由资源工具按需列举。
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig);
        var listsBefore = harness.Server.Count("resources/list");

        harness.Server.On("resources/list", _ => JsonNode.Parse(
            """{"resources":[{"uri":"ui://app/home"},{"uri":"docs://readme"},{"uri":"docs://other"}]}"""));
        harness.Server.SendNotification("notifications/resources/list_changed");
        await Task.Delay(100);

        Assert.Equal(listsBefore, harness.Server.Count("resources/list"));
        Assert.Equal(McpServerState.Connected, connection.State);
        Assert.True(connection.HasResources);
    }

    [Fact]
    public async Task AFailedToolRefreshIsReportedOnTheConnection()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig);

        harness.Server.FailNext("tools/list", new McpError(JsonRpcErrorCodes.InternalError, "refresh broke"));
        harness.Server.SendNotification("notifications/tools/list_changed");

        await WaitUntilAsync(() => connection.Error is not null, "the refresh failure");

        Assert.Equal("Failed to refresh tools: refresh broke", connection.Error);
        Assert.Equal("read_file", Assert.Single(connection.Tools).Name);
    }

    // ------------------------------------------------------------------ request shapes

    [Fact]
    public async Task CallsCarryTheToolArgumentsAndTheResourceUri()
    {
        using var harness = new Harness();
        var connection = await harness.ConnectedAsync(StdioConfig);

        var arguments = new Dictionary<string, object?> { ["who"] = "pi", ["times"] = 2 };
        await connection.CallToolAsync("greet", arguments, new McpCallOptions());

        var call = harness.Server.ParamsOf("tools/call");
        Assert.NotNull(call);
        Assert.Equal("greet", call!["name"]?.GetValue<string>());
        Assert.Equal("pi", call["arguments"]?["who"]?.GetValue<string>());
        Assert.Equal(2, call["arguments"]?["times"]?.GetValue<int>());

        var read = await connection.ReadResourceAsync("docs://readme", new McpCallOptions());
        Assert.Equal("docs://readme", harness.Server.ParamsOf("resources/read")?["uri"]?.GetValue<string>());
        Assert.Equal("hello", Assert.Single(read.Contents).Text);
    }

    [Fact]
    public void TheTimeoutComesFromTheConfigInSeconds()
    {
        using var harness = new Harness();

        Assert.Equal(60_000, harness.Connect(StdioConfig).TimeoutMs);
        Assert.Equal(5_000, harness.Connect("""{"command":"demo-server","timeout":5}""").TimeoutMs);
    }

    private static McpServerConfig Config(string json) => new(JsonNode.Parse(json)!.AsObject());
}
