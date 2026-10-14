using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Extensions.Mcp;
using Pi.Mcp.Protocol;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the MCP resource tools (port of <c>extensions/mcp/resources.ts</c>, batch
/// P4b-3). Vectors are transcribed from the TS behaviour: the MCP App filter, the Codex listing shape
/// (<c>{ server?, resources, nextCursor? }</c>), <c>_meta</c> and icon stripping, the per-server error
/// report of the fan-out listing, and the read tool's text / image / temp-file contents.
/// </summary>
public class McpResourceToolsTests
{
    // ------------------------------------------------------------------ fixtures

    private static McpResource Resource(string json) =>
        McpProtocol.ToResource(JsonNode.Parse(json)!.AsObject());

    private static McpResourceTemplate Template(string json) =>
        McpProtocol.ToResourceTemplate(JsonNode.Parse(json)!.AsObject());

    private sealed class FakeResourceServer : McpResourceServer
    {
        public required string Name { get; init; }

        public long TimeoutMs { get; init; } = 4321;

        public IReadOnlyList<McpResource> Resources { get; init; } = [];

        public IReadOnlyList<McpResourceTemplate> Templates { get; init; } = [];

        public string? PageCursor { get; init; }

        /// <summary>Fails the fan-out listing, as a server that is slow or unwell does.</summary>
        public Exception? ListingError { get; init; }

        public Func<string, ReadResourceResult>? Read { get; init; }

        public List<string> Calls { get; } = [];

        public List<long> Timeouts { get; } = [];

        public Task<ListResourcesResult> ResourcesPageAsync(
            string? cursor, McpCallOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add($"resources:{cursor ?? "first"}");
            Timeouts.Add(options.TimeoutMs ?? 0);
            return Task.FromResult(new ListResourcesResult(Resources, PageCursor));
        }

        public Task<ListResourceTemplatesResult> ResourceTemplatesPageAsync(
            string? cursor, McpCallOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add($"templates:{cursor ?? "first"}");
            Timeouts.Add(options.TimeoutMs ?? 0);
            return Task.FromResult(new ListResourceTemplatesResult(Templates, PageCursor));
        }

        public Task<IReadOnlyList<McpResource>> AllResourcesAsync(
            McpCallOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add("all-resources");
            Timeouts.Add(options.TimeoutMs ?? 0);
            return ListingError is null
                ? Task.FromResult(Resources)
                : Task.FromException<IReadOnlyList<McpResource>>(ListingError);
        }

        public Task<IReadOnlyList<McpResourceTemplate>> AllResourceTemplatesAsync(
            McpCallOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add("all-templates");
            return ListingError is null
                ? Task.FromResult(Templates)
                : Task.FromException<IReadOnlyList<McpResourceTemplate>>(ListingError);
        }

        public Task<ReadResourceResult> ReadResourceAsync(
            string uri, McpCallOptions options, CancellationToken cancellationToken = default)
        {
            Calls.Add($"read:{uri}");
            Timeouts.Add(options.TimeoutMs ?? 0);
            return Task.FromResult(Read?.Invoke(uri) ?? new ReadResourceResult([]));
        }
    }

    private static readonly McpResource DocsIntro = Resource(
        """{ "uri": "docs://intro", "name": "intro", "mimeType": "text/markdown", "_meta": { "x": 1 } }""");

    private static readonly McpResource DocsViewer = Resource(
        """{ "uri": "ui://docs/viewer", "name": "viewer", "mimeType": "text/html;profile=mcp-app" }""");

    private static readonly McpResource WikiHome = Resource(
        """{ "uri": "wiki://home", "name": "home", "size": 12, "icons": [{ "src": "data:image/png;base64,AAAA" }] }""");

    private static readonly McpResourceTemplate PageTemplate = Template(
        """{ "uriTemplate": "docs://pages/{slug}", "name": "page", "icons": [{ "src": "data:image/png;base64,AAAA" }] }""");

    private static readonly McpResourceServer Docs = new FakeResourceServer
    {
        Name = "docs",
        Resources = [DocsIntro, DocsViewer],
        Templates = [PageTemplate],
        Read = uri => new ReadResourceResult(
            [new McpResourceContents(uri, "# " + uri) { MimeType = "text/markdown" }]),
    };

    private static readonly McpResourceServer Wiki = new FakeResourceServer
    {
        Name = "wiki",
        Resources = [WikiHome],
    };

    private static McpResourceToolOptions Options(params McpResourceServer[] servers) => new()
    {
        Exposure = McpServers.Exposures.Direct,
        Servers = () => servers,
    };

    private static IReadOnlyList<ToolDefinition> Definitions(params McpResourceServer[] servers) =>
        McpResources.CreateMcpResourceToolDefinitions(Options(servers));

    private static ToolDefinition Tool(McpResourceToolOptions options, string name) =>
        McpResources.CreateMcpResourceToolDefinitions(options)
            .Single(tool => tool.Name == name);

    private static async Task<AgentToolResult> ExecuteAsync(
        ToolDefinition tool, params (string Key, object? Value)[] args)
    {
        var parameters = new Dictionary<string, object?>();
        foreach (var (key, value) in args)
        {
            parameters[key] = value;
        }

        return await tool.Execute("call-1", parameters, CancellationToken.None, null, null);
    }

    private static string TextOf(AgentToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContent>().Select(block => block.Text));

    private static JsonObject Payload(AgentToolResult result) =>
        Assert.IsType<JsonObject>(result.StructuredContent);

    private static JsonObject Entry(JsonNode node, int index) => node!.AsArray()[index]!.AsObject();

    private static JsonArray Items(JsonObject payload, string key) => payload[key]!.AsArray();

    /// <summary>The <c>server</c> tag of every listed entry, in listing order.</summary>
    private static IEnumerable<string> ServerNames(JsonObject payload, string key) =>
        Items(payload, key).Select(item => item!["server"]!.GetValue<string>());

    /// <summary>The member names of a listed entry, in order.</summary>
    private static IEnumerable<string> MemberNames(JsonObject payload, string key, int index) =>
        Entry(payload[key]!, index).AsObject().Select(pair => pair.Key);

    // ------------------------------------------------------------------ MCP App filter

    [Theory]
    [InlineData("ui://docs/viewer", null, null, true)]
    [InlineData("docs://intro", null, null, false)]
    [InlineData(null, "ui://pages/{slug}", null, true)]
    [InlineData(null, "docs://pages/{slug}", null, false)]
    [InlineData(null, null, "text/html;profile=mcp-app", true)]
    [InlineData(null, null, "text/html; profile=\"mcp-app\"", true)]
    [InlineData(null, null, "TEXT/HTML;PROFILE=MCP-APP", true)]
    [InlineData(null, null, "text/markdown", false)]
    [InlineData(null, null, null, false)]
    public void IsMcpAppResource_MatchesUiSchemeAndProfileParameter(string? uri, string? uriTemplate, string? mimeType, bool expected)
    {
        Assert.Equal(expected, McpResources.IsMcpAppResource(uri, uriTemplate, mimeType));
    }

    [Fact]
    public void IsMcpAppResource_ProfileMidMimeType_Matches()
    {
        // TS: /;\s*profile\s*=\s*"?mcp-app"?/i, so the parameter may follow others.
        Assert.True(McpResources.IsMcpAppResource(null, null, "text/html;charset=utf-8;profile=mcp-app"));
        Assert.True(McpResources.IsMcpAppResource(null, null, "text/html; profile = \"mcp-app\""));
    }

    // ------------------------------------------------------------------ definitions

    [Fact]
    public void Definitions_ThreeTools_NamedAndLabeledAfterTheToolNames()
    {
        var definitions = Definitions();

        Assert.Equal(
            [
                McpServers.ToolNames.ListMcpResources,
                McpServers.ToolNames.ListMcpResourceTemplates,
                McpServers.ToolNames.ReadMcpResource,
            ],
            definitions.Select(tool => tool.Name));
        Assert.All(definitions, tool =>
        {
            Assert.Equal(tool.Name, tool.Label);
            Assert.NotNull(tool.Annotations);
            Assert.True(tool.Annotations!.ReadOnlyHint);
            Assert.Equal(McpServers.Exposures.Direct, tool.Exposure);
        });
    }

    [Fact]
    public void Definitions_CodemodeExposure_BecomesDeferred()
    {
        var definitions = McpResources.CreateMcpResourceToolDefinitions(new McpResourceToolOptions
        {
            Exposure = McpServers.Exposures.Codemode,
            Servers = () => [],
        });

        Assert.All(definitions, tool => Assert.Equal(McpServers.Exposures.Deferred, tool.Exposure));
    }

    [Fact]
    public void Definitions_Descriptions_MatchCodexText()
    {
        var definitions = Definitions();
        var descriptions = definitions.ToDictionary(tool => tool.Name, tool => tool.Description);

        Assert.StartsWith("Lists resources provided by MCP servers.", descriptions[McpServers.ToolNames.ListMcpResources], StringComparison.Ordinal);
        Assert.StartsWith("Lists resource templates provided by MCP servers.", descriptions[McpServers.ToolNames.ListMcpResourceTemplates], StringComparison.Ordinal);
        Assert.Equal(
            "Read a specific resource from an MCP server given the server name and resource URI.",
            descriptions[McpServers.ToolNames.ReadMcpResource]);
    }

    [Fact]
    public void Definitions_ListParameters_TakeOptionalServerAndCursor()
    {
        var schema = Tool(Options(), McpServers.ToolNames.ListMcpResources).Parameters;
        var json = SchemaJson(schema);

        Assert.Equal("object", json["type"]!.GetValue<string>());
        Assert.Equal(["server", "cursor"], json["properties"]!.AsObject().Select(pair => pair.Key));
        Assert.False(json.AsObject().ContainsKey("required"));
        Assert.False(json["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void Definitions_ReadParameters_RequireServerAndUri()
    {
        var json = SchemaJson(Tool(Options(), McpServers.ToolNames.ReadMcpResource).Parameters);

        Assert.Equal(["server", "uri"], json["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(["server", "uri"], json["properties"]!.AsObject().Select(pair => pair.Key));
        Assert.False(json["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void Definitions_OutputSchemas_DeclareListingsAndContents()
    {
        var resources = SchemaJson(Tool(Options(), McpServers.ToolNames.ListMcpResources).OutputSchema!);
        Assert.Equal(["resources"], resources["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        var item = resources["properties"]!["resources"]!["items"]!;
        Assert.Equal(
            ["server", "uri", "name", "title", "description", "mimeType", "size"],
            item["properties"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(
            ["server", "uri", "name"],
            item["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        var errorItem = resources["properties"]!["errors"]!["items"]!;
        Assert.Equal(
            ["server", "error"], errorItem["required"]!.AsArray().Select(node => node!.GetValue<string>()));

        var templates = SchemaJson(Tool(Options(), McpServers.ToolNames.ListMcpResourceTemplates).OutputSchema!);
        Assert.Equal(
            ["server", "uriTemplate", "name", "title", "description", "mimeType"],
            templates["properties"]!["resourceTemplates"]!["items"]!["properties"]!.AsObject().Select(pair => pair.Key));

        var read = SchemaJson(Tool(Options(), McpServers.ToolNames.ReadMcpResource).OutputSchema!);
        Assert.Equal(
            ["server", "uri", "contents"],
            read["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        var contentsItem = read["properties"]!["contents"]!["items"]!;
        Assert.Equal(2, contentsItem["anyOf"]!.AsArray().Count);
        Assert.Equal("base64", contentsItem["anyOf"]![1]!["properties"]!["blob"]!["description"]!.GetValue<string>());
    }

    private static JsonObject SchemaJson(ToolSchema schema) =>
        JsonSerializer.SerializeToNode(schema.JsonSchema)!.AsObject();

    // ------------------------------------------------------------------ listing every server

    [Fact]
    public async Task ListResources_WithoutServer_AggregatesEveryServerSortedByName()
    {
        var zeta = new FakeResourceServer
        {
            Name = "zeta",
            Resources = [Resource("""{ "uri": "zeta://a", "name": "a", "icons": [{ "src": "data:,x" }] }""")],
        };
        var alpha = new FakeResourceServer { Name = "alpha", Resources = [DocsIntro, DocsViewer] };
        var tool = Tool(Options(zeta, alpha), McpServers.ToolNames.ListMcpResources);

        var result = await ExecuteAsync(tool);
        var payload = Payload(result);

        Assert.Equal(["alpha", "zeta"], ServerNames(payload, "resources"));
        Assert.Equal(["server", "uri", "name", "mimeType"], MemberNames(payload, "resources", 0));
        Assert.Equal(["server", "uri", "name"], MemberNames(payload, "resources", 1));
        // `_meta` and icons are left out, MCP App resources are skipped, and no page or server key is
        // claimed for an aggregate listing.
        Assert.Equal(2, Items(payload, "resources").Count);
        Assert.False(payload.AsObject().ContainsKey("server"));
        Assert.False(payload.AsObject().ContainsKey("nextCursor"));
        Assert.False(payload.AsObject().ContainsKey("errors"));
        var details = Assert.IsType<McpToolDetails>(result.Details);
        Assert.Equal(string.Empty, details.Server);
    }

    [Fact]
    public async Task ListResources_WithoutServer_FailingServer_ReportsItsError()
    {
        var broken = new FakeResourceServer { Name = "broken", ListingError = new IOException("timed out") };
        var tool = Tool(Options(Docs, broken), McpServers.ToolNames.ListMcpResources);

        var payload = Payload(await ExecuteAsync(tool));

        var error = Assert.IsType<JsonObject>(Assert.Single(payload["errors"]!.AsArray()));
        Assert.Equal("broken", error["server"]!.GetValue<string>());
        Assert.Equal("timed out", error["error"]!.GetValue<string>());
        var resource = Assert.IsType<JsonObject>(Assert.Single(payload["resources"]!.AsArray()));
        Assert.Equal("docs", resource["server"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListResources_BlankServer_ListsEveryServer()
    {
        var tool = Tool(Options(Docs, Wiki), McpServers.ToolNames.ListMcpResources);

        var payload = Payload(await ExecuteAsync(tool, ("server", "   ")));

        Assert.False(payload.AsObject().ContainsKey("server"));
        Assert.Equal(["docs", "wiki"], ServerNames(payload, "resources"));
    }

    [Fact]
    public async Task ListResources_WithoutServer_RequestsEveryPageWithoutACursor()
    {
        var docs = new FakeResourceServer { Name = "docs", Resources = [DocsIntro] };
        var wiki = new FakeResourceServer { Name = "wiki", Resources = [WikiHome] };
        var tool = Tool(Options(docs, wiki), McpServers.ToolNames.ListMcpResources);

        _ = await ExecuteAsync(tool);

        Assert.Equal(["all-resources"], docs.Calls);
        Assert.Equal(["all-resources"], wiki.Calls);
        Assert.All(new[] { docs, wiki }, server =>
            Assert.All(server.Timeouts, timeout => Assert.Equal(4321, timeout)));
    }

    // ------------------------------------------------------------------ listing one server

    [Fact]
    public async Task ListResources_WithServer_ReturnsOnePageAndTheCursor()
    {
        var docs = new FakeResourceServer
        {
            Name = "docs",
            Resources = [DocsIntro, DocsViewer],
            PageCursor = "c1",
        };
        var tool = Tool(Options(docs, Wiki), McpServers.ToolNames.ListMcpResources);

        var result = await ExecuteAsync(tool, ("server", "docs"));
        var payload = Payload(result);

        Assert.Equal("docs", payload["server"]!.GetValue<string>());
        Assert.Equal("c1", payload["nextCursor"]!.GetValue<string>());
        Assert.Equal(["docs://intro"], Items(payload, "resources").Select(item => item!["uri"]!.GetValue<string>()));
        Assert.Equal(["resources:first"], docs.Calls);
        Assert.Equal([4321], docs.Timeouts);
        var details = Assert.IsType<McpToolDetails>(result.Details);
        Assert.Equal("docs", details.Server);
        Assert.Equal(McpServers.ToolNames.ListMcpResources, details.Tool);
        Assert.Null(details.FullOutputPath);
    }

    [Fact]
    public async Task ListResources_WithServer_WithoutCursor_OmitsTheKey()
    {
        var tool = Tool(Options(Docs), McpServers.ToolNames.ListMcpResources);

        var payload = Payload(await ExecuteAsync(tool, ("server", "docs")));

        Assert.False(payload.AsObject().ContainsKey("nextCursor"));
    }

    [Fact]
    public async Task ListResources_CursorWithoutServer_Throws()
    {
        var tool = Tool(Options(Docs), McpServers.ToolNames.ListMcpResources);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => ExecuteAsync(tool, ("cursor", "c2")));

        Assert.Equal("cursor can only be used when a server is specified", error.Message);
    }

    [Fact]
    public async Task ListResources_UnknownServer_NamesTheServersWithResources()
    {
        var tool = Tool(Options(Docs, Wiki), McpServers.ToolNames.ListMcpResources);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => ExecuteAsync(tool, ("server", "nope")));

        Assert.Equal(
            "MCP server \"nope\" has no resources. Servers with resources: docs, wiki",
            error.Message);
    }

    [Fact]
    public async Task ListResources_UnknownServer_WithoutServers_HasNoSuffix()
    {
        var tool = Tool(Options(), McpServers.ToolNames.ListMcpResources);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => ExecuteAsync(tool, ("server", "nope")));

        Assert.Equal("MCP server \"nope\" has no resources", error.Message);
    }

    [Fact]
    public async Task ListResources_ServerIsNotAString_Throws()
    {
        var tool = Tool(Options(Docs), McpServers.ToolNames.ListMcpResources);

        await Assert.ThrowsAsync<ArgumentException>(() => ExecuteAsync(tool, ("server", 42)));
    }

    // ------------------------------------------------------------------ listing templates

    [Fact]
    public async Task ListTemplates_WithoutServer_AggregatesTemplates()
    {
        var extra = new FakeResourceServer
        {
            Name = "extra",
            Templates = [Template("""{ "uriTemplate": "extra://{id}", "name": "id", "_meta": { "a": 1 } }""")],
        };
        var tool = Tool(Options(Docs, extra), McpServers.ToolNames.ListMcpResourceTemplates);

        var payload = Payload(await ExecuteAsync(tool));

        Assert.Equal(["docs", "extra"], ServerNames(payload, "resourceTemplates"));
        Assert.Equal(["server", "uriTemplate", "name"], MemberNames(payload, "resourceTemplates", 0));
        Assert.Equal("docs://pages/{slug}", Items(payload, "resourceTemplates")[0]!["uriTemplate"]!.GetValue<string>());
        Assert.False(payload.AsObject().ContainsKey("server"));
    }

    [Fact]
    public async Task ListTemplates_WithServer_ReturnsOnePageAndTheCursor()
    {
        var docs = new FakeResourceServer { Name = "docs", Templates = [PageTemplate], PageCursor = "t2" };
        var tool = Tool(Options(docs), McpServers.ToolNames.ListMcpResourceTemplates);

        var payload = Payload(await ExecuteAsync(tool, ("server", "docs")));

        Assert.Equal("docs", payload["server"]!.GetValue<string>());
        Assert.Equal("t2", payload["nextCursor"]!.GetValue<string>());
        Assert.Equal(["templates:first"], docs.Calls);
    }

    [Fact]
    public async Task ListTemplates_FailingServer_ReportsItsError()
    {
        var broken = new FakeResourceServer { Name = "broken", ListingError = new TimeoutException("slow") };
        var tool = Tool(Options(broken), McpServers.ToolNames.ListMcpResourceTemplates);

        var payload = Payload(await ExecuteAsync(tool));

        var error = Assert.IsType<JsonObject>(Assert.Single(payload["errors"]!.AsArray()));
        Assert.Equal("slow", error["error"]!.GetValue<string>());
        Assert.Empty(Items(payload, "resourceTemplates"));
    }

    // ------------------------------------------------------------------ reading

    [Fact]
    public async Task ReadResource_TextContent_BecomesTextAndKeepsTheContents()
    {
        var docs = new FakeResourceServer
        {
            Name = "docs",
            Read = uri => new ReadResourceResult(
                [new McpResourceContents(uri, "# " + uri) { MimeType = "text/markdown" }]),
        };
        var tool = Tool(Options(docs), McpServers.ToolNames.ReadMcpResource);

        var result = await ExecuteAsync(tool, ("server", "docs"), ("uri", "docs://pages/setup"));

        Assert.Equal("# docs://pages/setup", TextOf(result));
        Assert.Equal(["read:docs://pages/setup"], docs.Calls);
        Assert.Equal([4321], docs.Timeouts);
        var payload = Payload(result);
        Assert.Equal("docs", payload["server"]!.GetValue<string>());
        Assert.Equal("docs://pages/setup", payload["uri"]!.GetValue<string>());
        var contents = Assert.IsType<JsonObject>(Assert.Single(payload["contents"]!.AsArray()));
        Assert.Equal("text/markdown", contents["mimeType"]!.GetValue<string>());
        Assert.Equal("# docs://pages/setup", contents["text"]!.GetValue<string>());
        var details = Assert.IsType<McpToolDetails>(result.Details);
        Assert.Equal("docs", details.Server);
        Assert.Equal(McpServers.ToolNames.ReadMcpResource, details.Tool);
        Assert.Null(details.FullOutputPath);
    }

    [Fact]
    public async Task ReadResource_SeveralContents_AreLabeledWithTheirUris()
    {
        var docs = new FakeResourceServer
        {
            Name = "docs",
            Read = _ => new ReadResourceResult(
            [
                new McpResourceContents("docs://a", "body a") { MimeType = "text/markdown" },
                new McpResourceContents("docs://b", "body b") { MimeType = "text/markdown" },
            ]),
        };
        var tool = Tool(Options(docs), McpServers.ToolNames.ReadMcpResource);

        var result = await ExecuteAsync(tool, ("server", "docs"), ("uri", "docs://dir"));

        Assert.Equal("docs://a:\nbody a\ndocs://b:\nbody b", TextOf(result));
        var payload = Payload(result);
        Assert.Equal(2, payload["contents"]!.AsArray().Count);
    }

    [Fact]
    public async Task ReadResource_NoContents_SaysTheResourceIsEmpty()
    {
        var docs = new FakeResourceServer { Name = "docs", Read = _ => new ReadResourceResult([]) };
        var tool = Tool(Options(docs), McpServers.ToolNames.ReadMcpResource);

        var result = await ExecuteAsync(tool, ("server", "docs"), ("uri", "docs://empty"));

        Assert.Equal("Resource docs://empty is empty.", TextOf(result));
        var payload = Payload(result);
        Assert.Empty(payload["contents"]!.AsArray());
    }

    [Fact]
    public async Task ReadResource_MissingArguments_Throw()
    {
        var tool = Tool(Options(Docs), McpServers.ToolNames.ReadMcpResource);

        Assert.Equal(
            "server must be provided",
            (await Assert.ThrowsAsync<ArgumentException>(
                () => ExecuteAsync(tool, ("uri", "docs://x")))).Message);
        Assert.Equal(
            "uri must be provided",
            (await Assert.ThrowsAsync<ArgumentException>(
                () => ExecuteAsync(tool, ("server", "docs")))).Message);
    }

    [Fact]
    public async Task ReadResource_UnknownServer_NamesTheServersWithResources()
    {
        var tool = Tool(Options(Docs, Wiki), McpServers.ToolNames.ReadMcpResource);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => ExecuteAsync(tool, ("server", "nope"), ("uri", "docs://x")));

        Assert.Equal(
            "MCP server \"nope\" has no resources. Servers with resources: docs, wiki",
            error.Message);
    }

    [Fact]
    public async Task ReadResource_ImageBlob_BecomesAnImage()
    {
        var docs = new FakeResourceServer
        {
            Name = "docs",
            Read = uri => new ReadResourceResult(
                [new McpResourceContents(uri, Blob: "AAAA") { MimeType = "image/png" }]),
        };
        var tool = Tool(Options(docs), McpServers.ToolNames.ReadMcpResource);

        var result = await ExecuteAsync(tool, ("server", "docs"), ("uri", "docs://logo.png"));

        var image = Assert.IsType<ImageContent>(Assert.Single(result.Content));
        Assert.Equal("AAAA", image.Data);
        Assert.Equal("image/png", image.MimeType);
        var payload = Payload(result);
        var contents = Assert.IsType<JsonObject>(Assert.Single(payload["contents"]!.AsArray()));
        Assert.Equal("AAAA", contents["blob"]!.GetValue<string>());
        Assert.False(contents.AsObject().ContainsKey("text"));
    }

    [Fact]
    public async Task ReadResource_TextBlob_BecomesItsText()
    {
        var blob = Convert.ToBase64String(Encoding.UTF8.GetBytes("# docs://intro"));
        var docs = new FakeResourceServer
        {
            Name = "docs",
            Read = uri => new ReadResourceResult(
                [new McpResourceContents(uri, Blob: blob) { MimeType = "text/markdown" }]),
        };
        var tool = Tool(Options(docs), McpServers.ToolNames.ReadMcpResource);

        var result = await ExecuteAsync(tool, ("server", "docs"), ("uri", "docs://intro"));

        Assert.Equal("# docs://intro", TextOf(result));
    }

    [Fact]
    public async Task ReadResource_BinaryBlob_SavesAFileAndNamesIt()
    {
        var docs = new FakeResourceServer
        {
            Name = "docs",
            Read = uri => new ReadResourceResult(
                [new McpResourceContents(uri, Blob: "AAAA") { MimeType = "application/octet-stream" }]),
        };
        var tool = Tool(Options(docs), McpServers.ToolNames.ReadMcpResource);

        var result = await ExecuteAsync(tool, ("server", "docs"), ("uri", "docs://file.bin"));

        var text = TextOf(result);
        const string prefix = "[Binary resource docs://file.bin (application/octet-stream, 3B) saved to ";
        Assert.StartsWith(prefix, text, StringComparison.Ordinal);
        Assert.EndsWith("]", text, StringComparison.Ordinal);
        // The saved file is the resource's bytes, under the extension of its URI.
        Assert.True(File.Exists(text[prefix.Length..^1]));
    }
}
