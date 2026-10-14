using System.Text.Json.Nodes;
using Pi.CodingAgent.Extensions.Mcp;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for `pi mcp` (port of <c>extensions/mcp/cli.ts</c>, batch P4b-5). The vectors are
/// transcribed from the TS <c>mcp-command.test.ts</c>: the list text and JSON, the exit codes that gate
/// scripts, the `add`/`remove` argument grammar (including passing a command's own options through),
/// the config each invocation writes, and the login/logout preconditions. The transport is injected
/// (difference C187), so no test spawns a real MCP server.
/// </summary>
public class McpCliTests
{
    private const string Servers = """
        {"mcpServers":{
          "fixture":{"command":"pi-fixture-server"},
          "broken":{"command":"pi-test-missing-mcp-server"},
          "parked":{"command":"pi-fixture-server","enabled":false},
          "bad":{"args":["no command"]}}}
        """;

    // ------------------------------------------------------------------ fixtures

    /// <summary>A server that answers the handshake and lists its tools.</summary>
    private sealed class FakeMcpServer(params string[] tools) : TransportEvents
    {
        public override Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            if (message is not JsonRpcMessage.Request request)
            {
                return Task.CompletedTask;
            }

            JsonNode? result = request.Method switch
            {
                "initialize" => JsonNode.Parse(
                    """{"protocolVersion":"2025-11-25","capabilities":{"tools":{}},"serverInfo":{"name":"fake","version":"1.0.0"}}"""),
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray([.. tools.Select(name => (JsonNode)new JsonObject
                    {
                        ["name"] = name,
                        ["inputSchema"] = new JsonObject { ["type"] = "object" },
                    })]),
                },
                _ => null,
            };

            EmitMessage(result is null
                ? new JsonRpcMessage.ErrorResponse(request.Id,
                    new JsonRpcErrorObject(JsonRpcErrorCodes.MethodNotFound, "Method not found"))
                : new JsonRpcMessage.SuccessResponse(request.Id, result));
            return Task.CompletedTask;
        }

        public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>One temporary agent directory, the captured output, and the injected transports.</summary>
    private sealed class Harness : IDisposable
    {
        public string AgentDir { get; } =
            Path.Combine(Path.GetTempPath(), "pi-mcp-cli-" + Guid.NewGuid().ToString("N")[..8]);

        public List<string> Output { get; } = [];

        public Harness() => Directory.CreateDirectory(AgentDir);

        /// <summary>The lines the command printed, joined as the TS test joins them.</summary>
        public string Text => string.Join("\n", Output);

        public string ConfigPath => Path.Combine(AgentDir, "mcp.json");

        public string ProjectConfigPath => Path.Combine(AgentDir, ".pi", "mcp.json");

        public Task<int> RunAsync(params string[] args) => RunAsync(args, null);

        public async Task<int> RunAsync(string[] args, string? servers)
        {
            if (servers is not null)
            {
                File.WriteAllText(ConfigPath, servers);
            }

            return await McpCli.RunMcpCommandAsync(args, new McpCommandOptions
            {
                Cwd = AgentDir,
                AgentDir = AgentDir,
                Log = Output.Add,
                Error = Output.Add,
                // Never open a browser, and never read from stdin.
                OpenUrl = _ => { },
                CreateTransport = (entry, _, _) => entry.Name switch
                {
                    "fixture" or "docs" => new FakeMcpServer("echo"),
                    "broken" or "remote" => throw new InvalidOperationException(
                        "spawn pi-test-missing-mcp-server ENOENT"),
                    _ => new FakeMcpServer(),
                },
            });
        }

        public void AssertConfig(string path, string expectedJson)
        {
            Assert.True(File.Exists(path), $"expected {path} to exist");
            var actual = JsonNode.Parse(File.ReadAllText(path));
            Assert.True(JsonNode.DeepEquals(actual, JsonNode.Parse(expectedJson)),
                $"actual config: {actual?.ToJsonString()}");
        }

        public void Dispose()
        {
            if (Directory.Exists(AgentDir))
            {
                Directory.Delete(AgentDir, recursive: true);
            }
        }
    }

    // ------------------------------------------------------------------ list

    [Fact]
    public async Task ListShowsStateToolsAndErrorsAndFailsWhileAnythingIsWrong()
    {
        using var harness = new Harness();

        var exitCode = await harness.RunAsync(["list"], Servers);

        Assert.Equal(1, exitCode);
        Assert.Contains("fixture: connected, 1 tool (codemode, global)\n", harness.Text);
        Assert.Contains("  tools: echo", harness.Text);
        Assert.Contains("parked: disabled (codemode, global)", harness.Text);
        Assert.Contains(
            "broken: failed (codemode, global)\n  pi-test-missing-mcp-server\n  spawn pi-test-missing-mcp-server ENOENT",
            harness.Text);
        Assert.Contains("config error: ", harness.Text);
        Assert.Contains("server \"bad\" needs either \"command\"", harness.Text);

        using var healthy = new Harness();
        Assert.Equal(0, await healthy.RunAsync(["list"], """{"mcpServers":{"fixture":{"command":"pi-fixture-server"}}}"""));
    }

    [Fact]
    public async Task ListPrintsJsonForScripts()
    {
        using var harness = new Harness();

        var exitCode = await harness.RunAsync(
            ["list", "--json"],
            """{"mcpServers":{"fixture":{"command":"pi-fixture-server"},"parked":{"command":"pi-fixture-server","enabled":false}}}""");

        Assert.Equal(0, exitCode);
        var payload = JsonNode.Parse(harness.Text)!.AsObject();
        var rows = payload["servers"]!.AsArray()
            .Select(row => (
                Name: row!["name"]!.GetValue<string>(),
                State: row["state"]!.GetValue<string>(),
                Tools: row["tools"]!.AsArray().Select(tool => tool!.GetValue<string>()).ToList()))
            .ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("fixture", rows[0].Name);
        Assert.Equal("connected", rows[0].State);
        Assert.Equal(["echo"], rows[0].Tools);
        Assert.Equal("parked", rows[1].Name);
        Assert.Equal("disabled", rows[1].State);
        Assert.Empty(rows[1].Tools);
        Assert.Empty(payload["errors"]!.AsArray());
        Assert.Equal("global", payload["servers"]!.AsArray()[0]!["scope"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListReportsAnEmptyConfigurationAndConfigErrors()
    {
        using var harness = new Harness();

        var exitCode = await harness.RunAsync(["list"]);

        Assert.Equal(0, exitCode);
        Assert.Contains($"No MCP servers configured. Add them to {harness.ConfigPath} or .pi/mcp.json.", harness.Text);

        using var broken = new Harness();
        Assert.Equal(1, await broken.RunAsync(["list"], """{"mcpServers":{"bad":{"args":["no command"]}}}"""));
        Assert.Contains("config error: ", broken.Text);
    }

    [Fact]
    public async Task ListNotesAnUntrustedProjectConfig()
    {
        using var harness = new Harness();
        Directory.CreateDirectory(Path.GetDirectoryName(harness.ProjectConfigPath)!);
        File.WriteAllText(harness.ProjectConfigPath, """{"mcpServers":{"local":{"command":"node"}}}""");

        var exitCode = await harness.RunAsync(["list"]);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            $"{harness.ProjectConfigPath} is ignored because the project is not trusted.", harness.Text);
    }

    [Fact]
    public async Task HelpIsPrintedWithoutACommand()
    {
        using var harness = new Harness();

        Assert.Equal(0, await harness.RunAsync([]));
        Assert.Contains("Usage:", harness.Text);
        Assert.Contains("pi mcp add <server> [options] -- <command> [args...]", harness.Text);

        using var help = new Harness();
        Assert.Equal(0, await help.RunAsync(["help"]));
        Assert.Contains("Usage:", help.Text);

        using var flag = new Harness();
        Assert.Equal(0, await flag.RunAsync(["list", "--help"]));
        Assert.Contains("Usage:", flag.Text);

        using var unknown = new Harness();
        Assert.Equal(1, await unknown.RunAsync(["frobnicate"]));
        Assert.Contains("Unknown mcp command \"frobnicate\".", unknown.Text);
    }

    // ------------------------------------------------------------------ login / logout

    [Fact]
    public async Task LoginAndLogoutRejectUnknownServersAndServersWithoutOAuth()
    {
        using var harness = new Harness();

        Assert.Equal(1, await harness.RunAsync(["login", "nope"], Servers));
        Assert.Contains("No MCP server named \"nope\". Configured: fixture, broken, parked.", harness.Text);

        using var stdio = new Harness();
        Assert.Equal(1, await stdio.RunAsync(["logout", "fixture"], Servers));
        Assert.Contains(
            "MCP server \"fixture\" does not use OAuth. Only HTTP servers without an Authorization header do.",
            stdio.Text);

        using var missing = new Harness();
        Assert.Equal(1, await missing.RunAsync(["logout", "nope"], Servers));
        Assert.Contains("No MCP server named \"nope\". Configured: fixture, broken, parked.", missing.Text);
    }

    [Fact]
    public async Task LoginSucceedsWhenTheServerIsAlreadyReachable()
    {
        using var harness = new Harness();

        var exitCode = await harness.RunAsync(
            ["login", "docs"], """{"mcpServers":{"docs":{"url":"https://example.com/mcp"}}}""");

        Assert.Equal(0, exitCode);
        Assert.Contains("Already signed in to MCP server \"docs\" (1 tools).", harness.Text);
    }

    [Fact]
    public async Task LoginReportsAConnectFailureAndARejectsABadTimeout()
    {
        using var harness = new Harness();
        const string config = """{"mcpServers":{"remote":{"url":"https://example.com/mcp"}}}""";

        Assert.Equal(1, await harness.RunAsync(["login", "remote"], config));
        Assert.Contains(
            "MCP server \"remote\" failed to connect: spawn pi-test-missing-mcp-server ENOENT", harness.Text);

        using var timeout = new Harness();
        Assert.Equal(1, await timeout.RunAsync(["login", "remote", "--timeout", "abc"], config));
        Assert.Contains("--timeout must be a positive number of seconds.", timeout.Text);
    }

    [Fact]
    public async Task LoginAndLogoutNeedExactlyOneServerName()
    {
        using var harness = new Harness();

        Assert.Equal(1, await harness.RunAsync(["login"], Servers));
        Assert.Contains("Usage: pi mcp login <server>", harness.Text);

        using var extra = new Harness();
        Assert.Equal(1, await extra.RunAsync(["login", "fixture", "other"], Servers));
        Assert.Contains("Usage: pi mcp login <server>", extra.Text);

        using var unknown = new Harness();
        Assert.Equal(1, await unknown.RunAsync(["logout", "--nope"], Servers));
        Assert.Contains("Unknown option --nope.", unknown.Text);
    }

    // ------------------------------------------------------------------ add / remove

    [Fact]
    public async Task AddWritesStdioServersAndPassesOptionsAfterTheCommandThrough()
    {
        using var harness = new Harness();

        var exitCode = await harness.RunAsync(
            ["add", "--env", "A=1", "--env", "B=x=y", "files", "--", "npx", "-y", "server", "--root", "."]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Added global MCP server \"files\"", harness.Text);
        harness.AssertConfig(
            harness.ConfigPath,
            """{"mcpServers":{"files":{"command":"npx","args":["-y","server","--root","."],"env":{"A":"1","B":"x=y"}}}}""");

        // Without `--`, options after the command belong to the command too.
        var replaced = await harness.RunAsync(["add", "files", "node", "server.js", "--port", "1"]);

        Assert.Equal(0, replaced);
        Assert.Contains("Replaced global MCP server \"files\"", harness.Text);
        harness.AssertConfig(
            harness.ConfigPath,
            """{"mcpServers":{"files":{"command":"node","args":["server.js","--port","1"]}}}""");
    }

    [Fact]
    public async Task AddWritesHttpServersAndKeepsTheOtherContentOfTheFile()
    {
        using var harness = new Harness();
        const string fixture = """{"mcpServers":{"fixture":{"command":"pi-fixture-server"}}}""";

        var exitCode = await harness.RunAsync(
            [
                "add", "docs", "--url", "https://example.com/mcp",
                "--bearer-token-env-var", "DOCS_TOKEN",
                "--header", "X-Team=core",
                "--exposure", "direct",
                "--description", "Product docs",
            ],
            fixture);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("mcp login", harness.Text);
        harness.AssertConfig(
            harness.ConfigPath,
            """
            {"mcpServers":{
              "fixture":{"command":"pi-fixture-server"},
              "docs":{
                "url":"https://example.com/mcp",
                "headers":{"X-Team":"core","Authorization":"Bearer ${DOCS_TOKEN}"},
                "exposure":"direct",
                "description":"Product docs"}}}
            """);

        var oauth = await harness.RunAsync(
            [
                "add", "sentry", "--url", "https://mcp.sentry.dev/mcp",
                "--oauth-client-id", "pi", "--oauth-client-name", "Claude Code",
            ]);

        Assert.Equal(0, oauth);
        Assert.Contains("If it requires sign-in: pi mcp login sentry", harness.Text);
        var sentry = JsonNode.Parse(File.ReadAllText(harness.ConfigPath))!["mcpServers"]!["sentry"]!;
        Assert.Equal("https://mcp.sentry.dev/mcp", sentry["url"]!.GetValue<string>());
        Assert.Equal("pi", sentry["oauth"]!["clientId"]!.GetValue<string>());
        Assert.Equal("Claude Code", sentry["oauth"]!["clientName"]!.GetValue<string>());
    }

    [Fact]
    public async Task AddRejectsInvalidInvocationsWithoutWriting()
    {
        string[][] cases =
        [
            ["add", "x"],
            ["add", "x", "--url", "https://example.com", "--", "cmd"],
            ["add", "bad name", "--", "cmd"],
            ["add", "x", "--url", "ftp://example.com"],
            ["add", "x", "--env", "A=1", "--url", "https://example.com"],
            ["add", "x", "--header", "A=1", "--", "cmd"],
            ["add", "x", "--env", "NOVALUE", "--", "cmd"],
            ["add", "x", "--exposure", "loud", "--", "cmd"],
            ["add", "x", "--nope", "--", "cmd"],
            ["add", "x", "--url", "https://example.com", "--oauth-callback-port", "abc"],
        ];

        foreach (var args in cases)
        {
            using var harness = new Harness();
            var exitCode = await harness.RunAsync(args);
            Assert.True(exitCode == 1, $"{string.Join(" ", args)} → exit {exitCode}: {harness.Text}");
            Assert.False(File.Exists(harness.ConfigPath), $"{string.Join(" ", args)} wrote mcp.json");
            Assert.True(harness.Output.Count > 0, $"{string.Join(" ", args)} printed nothing");
        }
    }

    [Fact]
    public async Task AddAndRemoveProjectServers()
    {
        using var harness = new Harness();

        Assert.Equal(0, await harness.RunAsync(["add", "-l", "local", "--", "node", "server.js"]));
        Assert.Contains("The project is not trusted", harness.Text);
        harness.AssertConfig(
            harness.ProjectConfigPath, """{"mcpServers":{"local":{"command":"node","args":["server.js"]}}}""");

        // The project file is ignored while the project is untrusted, so the removal names the scope.
        var wrongScope = await harness.RunAsync(["remove", "local"], Servers);

        Assert.Equal(1, wrongScope);
        Assert.Contains(
            $"No global MCP server named \"local\" in {harness.ConfigPath}." +
            $" It is defined in {harness.ProjectConfigPath}; use --local.",
            harness.Text);

        Assert.Equal(0, await harness.RunAsync(["remove", "local", "--local"]));
        Assert.Contains($"Removed project MCP server \"local\" from {harness.ProjectConfigPath}.", harness.Text);
        harness.AssertConfig(harness.ProjectConfigPath, """{"mcpServers":{}}""");
    }

    [Fact]
    public async Task RemoveDeletesGlobalServers()
    {
        using var harness = new Harness();

        Assert.Equal(0, await harness.RunAsync(["remove", "broken"], Servers));
        Assert.Contains($"Removed global MCP server \"broken\" from {harness.ConfigPath}.", harness.Text);
        var names = JsonNode.Parse(File.ReadAllText(harness.ConfigPath))!["mcpServers"]!.AsObject().Select(entry => entry.Key);
        Assert.Equal(["fixture", "parked", "bad"], names);

        Assert.Equal(1, await harness.RunAsync(["remove", "broken"]));
        Assert.Contains("No global MCP server named \"broken\"", harness.Text);
    }

    [Fact]
    public async Task RemoveNeedsExactlyOneServerName()
    {
        using var harness = new Harness();

        Assert.Equal(1, await harness.RunAsync(["remove"], Servers));
        Assert.Contains("Usage: pi mcp remove <server> [-l]", harness.Text);

        using var extra = new Harness();
        Assert.Equal(1, await extra.RunAsync(["remove", "fixture", "other"], Servers));
        Assert.Contains("Usage: pi mcp remove <server> [-l]", extra.Text);
    }
}
