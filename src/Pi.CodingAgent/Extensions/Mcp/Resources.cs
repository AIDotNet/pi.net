// ============================================================================
// MCP resource tools — port of extensions/mcp/resources.ts (P4b-3)
// ============================================================================
//
// MCP resources, through the tools Codex and opencode use: `list_mcp_resources`,
// `list_mcp_resource_templates`, and `read_mcp_resource`. They take a `server` argument and cover
// every connected server with resources, so models trained on those tools use them unchanged.
//
// Listings are JSON, as in Codex: `{ server?, resources: [{ server, ...resource }], nextCursor? }`.
// With a `server`, one page is listed and `cursor` continues it; without, every page of every server.
// MCP App resources (`ui://` URIs and `profile=mcp-app` HTML) are left out, since they are user
// interfaces for hosts that render them, and so are icons. Read resources become text and images for
// the model; binary resources are saved to temp files. Scripts get the JSON payloads.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.Mcp;
using Pi.Mcp.Protocol;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using ToolSchema = Pi.Ai.Types.ToolSchema;

namespace Pi.CodingAgent.Extensions.Mcp;

/// <summary>A connected server that offers resources. TS <c>McpResourceServer</c>.</summary>
/// <remarks>
/// Difference C182: the TS interface is kept by name (the sibling <c>McpOAuthServerStore</c> of
/// P4b-2 does the same), while the members follow the C# conventions — <c>Async</c> suffixes, and the
/// request options carry no <c>signal</c> because the cancellation token is its own parameter, exactly
/// like <see cref="IMcpToolCaller"/> of <c>tools.ts</c>.
/// </remarks>
public interface McpResourceServer
{
    /// <summary>Server name as configured, and as listings report it.</summary>
    string Name { get; }

    /// <summary>Request timeout of the server, in milliseconds.</summary>
    long TimeoutMs { get; }

    /// <summary>One page of the server's resources. TS <c>resourcesPage</c>.</summary>
    Task<ListResourcesResult> ResourcesPageAsync(
        string? cursor,
        McpCallOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>One page of the server's resource templates. TS <c>resourceTemplatesPage</c>.</summary>
    Task<ListResourceTemplatesResult> ResourceTemplatesPageAsync(
        string? cursor,
        McpCallOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Every resource of the server, all pages. TS <c>allResources</c>.</summary>
    Task<IReadOnlyList<McpResource>> AllResourcesAsync(
        McpCallOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Every resource template of the server, all pages. TS <c>allResourceTemplates</c>.</summary>
    Task<IReadOnlyList<McpResourceTemplate>> AllResourceTemplatesAsync(
        McpCallOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Read one resource. TS <c>readResource</c>.</summary>
    Task<ReadResourceResult> ReadResourceAsync(
        string uri,
        McpCallOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Options of <see cref="McpResources.CreateMcpResourceToolDefinitions"/>.</summary>
public sealed record McpResourceToolOptions
{
    /// <summary>Exposure of the servers the tools reach. TS <c>McpExposure</c>.</summary>
    public required string Exposure { get; init; }

    /// <summary>The servers whose resources the tools reach, read at call time.</summary>
    public required Func<IReadOnlyList<McpResourceServer>> Servers { get; init; }
}

/// <summary>
/// The three resource tools of <c>resources.ts</c>: <c>list_mcp_resources</c>,
/// <c>list_mcp_resource_templates</c> and <c>read_mcp_resource</c>.
/// </summary>
public static partial class McpResources
{
    // ------------------------------------------------------------------ public surface

    /// <summary>
    /// MCP App user interfaces, which only hosts that render them can use: a <c>ui://</c> URI, or an
    /// HTML MIME type carrying a <c>profile=mcp-app</c> parameter.
    /// </summary>
    public static bool IsMcpAppResource(string? uri, string? uriTemplate, string? mimeType)
    {
        var item = uri ?? uriTemplate ?? "";
        return item.StartsWith("ui://", StringComparison.Ordinal)
            || McpAppProfileRegex().IsMatch(mimeType ?? "");
    }

    /// <summary>
    /// The three resource tools. <paramref name="options"/>.<see cref="McpResourceToolOptions.Servers"/>
    /// returns the servers whose resources they reach, at call time.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> CreateMcpResourceToolDefinitions(McpResourceToolOptions options)
    {
        var readOnly = new ToolAnnotations { ReadOnlyHint = true };

        ToolDefinition ListResources() => new()
        {
            Name = McpServers.ToolNames.ListMcpResources,
            Label = McpServers.ToolNames.ListMcpResources,
            Description =
                "Lists resources provided by MCP servers. Resources allow servers to share data that provides context to language models, such as files, database schemas, or application-specific information. Prefer resources over web search when possible.",
            Parameters = ToSchema(ListParameters()),
            OutputSchema = ToSchema(ListOutputSchema()),
            Exposure = McpTools.ToToolExposure(options.Exposure),
            Annotations = readOnly,
            Execute = async (_, parameters, cancellationToken, _, _) =>
            {
                var payload = await ListAsync(
                    options,
                    parameters,
                    "resources",
                    async (server, cursor, requestOptions, ct) =>
                    {
                        var result = await server.ResourcesPageAsync(cursor, requestOptions, ct);
                        return new Page<McpResource>(result.Resources, result.NextCursor);
                    },
                    (server, requestOptions, ct) => server.AllResourcesAsync(requestOptions, ct),
                    item => item.Raw ?? new JsonObject(),
                    cancellationToken);
                return await JsonResultAsync(
                    McpServers.ToolNames.ListMcpResources, StringArgument(parameters, "server"), payload);
            },
        };

        ToolDefinition ListTemplates() => new()
        {
            Name = McpServers.ToolNames.ListMcpResourceTemplates,
            Label = McpServers.ToolNames.ListMcpResourceTemplates,
            Description =
                "Lists resource templates provided by MCP servers. Parameterized resource templates allow servers to share data that takes parameters and provides context to language models, such as files, database schemas, or application-specific information. Prefer resource templates over web search when possible.",
            Parameters = ToSchema(ListParameters()),
            OutputSchema = ToSchema(ListTemplatesOutputSchema()),
            Exposure = McpTools.ToToolExposure(options.Exposure),
            Annotations = readOnly,
            Execute = async (_, parameters, cancellationToken, _, _) =>
            {
                var payload = await ListAsync(
                    options,
                    parameters,
                    "resourceTemplates",
                    async (server, cursor, requestOptions, ct) =>
                    {
                        var result = await server.ResourceTemplatesPageAsync(cursor, requestOptions, ct);
                        return new Page<McpResourceTemplate>(result.ResourceTemplates, result.NextCursor);
                    },
                    (server, requestOptions, ct) => server.AllResourceTemplatesAsync(requestOptions, ct),
                    item => item.Raw ?? new JsonObject(),
                    cancellationToken);
                return await JsonResultAsync(
                    McpServers.ToolNames.ListMcpResourceTemplates, StringArgument(parameters, "server"), payload);
            },
        };

        ToolDefinition ReadResource() => new()
        {
            Name = McpServers.ToolNames.ReadMcpResource,
            Label = McpServers.ToolNames.ReadMcpResource,
            Description = "Read a specific resource from an MCP server given the server name and resource URI.",
            Parameters = ToSchema(ReadParameters()),
            OutputSchema = ToSchema(ReadOutputSchema()),
            Exposure = McpTools.ToToolExposure(options.Exposure),
            Annotations = readOnly,
            Execute = async (_, parameters, cancellationToken, _, _) =>
            {
                var serverName = StringArgument(parameters, "server");
                var uri = StringArgument(parameters, "uri");
                if (serverName is null) throw new ArgumentException("server must be provided");
                if (uri is null) throw new ArgumentException("uri must be provided");
                var server = FindServer(options, serverName);
                var result = await server.ReadResourceAsync(uri, RequestOptions(server), cancellationToken);
                // Several contents (for example a directory) are labeled with their URIs.
                List<McpContentBlock> blocks = [];
                foreach (var contents in result.Contents)
                {
                    if (result.Contents.Count > 1)
                    {
                        blocks.Add(new McpContentBlock(new JsonObject { ["type"] = "text", ["text"] = $"{contents.Uri}:" }));
                    }

                    blocks.Add(new McpContentBlock(new JsonObject
                    {
                        ["type"] = "resource",
                        ["resource"] = ResourceContentsJson(contents),
                    }));
                }

                var converted = await McpTools.ToModelContentAsync(
                    server.Name, blocks, new ConvertMcpResultOptions(), cancellationToken);
                var (content, fullOutputPath) = await McpTools.LimitMcpContentAsync(
                    converted.Count > 0 ? converted : [new TextContent($"Resource {uri} is empty.")]);
                var contentsArray = new JsonArray();
                foreach (var contents in result.Contents)
                {
                    contentsArray.Add(ResourceContentsJson(contents));
                }

                return new AgentToolResult(
                    content,
                    Details: new McpToolDetails
                    {
                        Server = server.Name,
                        Tool = McpServers.ToolNames.ReadMcpResource,
                        FullOutputPath = fullOutputPath,
                    },
                    StructuredContent: new JsonObject
                    {
                        ["server"] = server.Name,
                        ["uri"] = uri,
                        ["contents"] = contentsArray,
                    });
            },
        };

        return [ListResources(), ListTemplates(), ReadResource()];
    }

    // ------------------------------------------------------------------ listing

    /// <summary>One page of one server, or every page of every server.</summary>
    private static async Task<JsonObject> ListAsync<T>(
        McpResourceToolOptions options,
        IReadOnlyDictionary<string, object?> parameters,
        string key,
        Func<McpResourceServer, string?, McpCallOptions, CancellationToken, Task<Page<T>>> page,
        Func<McpResourceServer, McpCallOptions, CancellationToken, Task<IReadOnlyList<T>>> all,
        Func<T, JsonObject> rawOf,
        CancellationToken cancellationToken)
    {
        var serverName = StringArgument(parameters, "server");
        var cursor = StringArgument(parameters, "cursor");
        if (serverName is not null)
        {
            var server = FindServer(options, serverName);
            var result = await page(server, cursor, RequestOptions(server), cancellationToken);
            var items = new JsonArray();
            AddListedItems(items, server.Name, result.Items, rawOf);
            var payload = new JsonObject
            {
                ["server"] = server.Name,
                [key] = items,
            };
            if (result.NextCursor is not null)
            {
                payload["nextCursor"] = result.NextCursor;
            }

            return payload;
        }

        if (cursor is not null)
        {
            throw new ArgumentException("cursor can only be used when a server is specified");
        }

        var servers = options.Servers().OrderBy(server => server.Name, StringComparer.CurrentCulture).ToArray();
        var outcomes = await Task.WhenAll(
            servers.Select(server => ListServerAsync(server, all, cancellationToken)));
        var listed = new JsonArray();
        var errors = new JsonArray();
        foreach (var (outcome, server) in outcomes.Zip(servers))
        {
            if (outcome.Error is null)
            {
                AddListedItems(listed, server.Name, outcome.Items, rawOf);
                continue;
            }

            errors.Add(new JsonObject { ["server"] = server.Name, ["error"] = outcome.Error.Message });
        }

        var listing = new JsonObject { [key] = listed };
        if (errors.Count > 0)
        {
            listing["errors"] = errors;
        }

        return listing;
    }

    /// <summary>One page of one server's items. TS <c>{ items, nextCursor? }</c>.</summary>
    private sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

    /// <summary>Every item of one server, or the error that stopped the listing.</summary>
    private sealed record ListedItems<T>(IReadOnlyList<T> Items, Exception? Error);

    private static async Task<ListedItems<T>> ListServerAsync<T>(
        McpResourceServer server,
        Func<McpResourceServer, McpCallOptions, CancellationToken, Task<IReadOnlyList<T>>> all,
        CancellationToken cancellationToken)
    {
        try
        {
            return new ListedItems<T>(await all(server, RequestOptions(server), cancellationToken), null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Difference C183: TS `Promise.allSettled` records a rejected page as that server's error,
            // and an aborted call rejects all of them, so a cancelled listing there still answers. The
            // port lets cancellation through instead: a cancelled call is not a listing with errors.
            return new ListedItems<T>([], error);
        }
    }

    /// <summary>The listed items of one server, without MCP App resources, icons and <c>_meta</c>.</summary>
    private static void AddListedItems<T>(JsonArray target, string server, IReadOnlyList<T> items, Func<T, JsonObject> rawOf)
    {
        foreach (var item in items)
        {
            var raw = rawOf(item);
            if (IsMcpAppResource(raw))
            {
                continue;
            }

            target.Add(Listed(server, raw));
        }
    }

    /// <summary>A listed resource or template without <c>_meta</c> and icons, tagged with its server.</summary>
    private static JsonObject Listed(string server, JsonObject raw)
    {
        var entry = new JsonObject { ["server"] = server };
        foreach (var (key, value) in raw)
        {
            if (key is "_meta" or "icons")
            {
                continue;
            }

            entry[key] = value?.DeepClone();
        }

        return entry;
    }

    private static bool IsMcpAppResource(JsonObject raw) =>
        IsMcpAppResource(Text(raw, "uri"), Text(raw, "uriTemplate"), Text(raw, "mimeType"));

    private static McpResourceServer FindServer(McpResourceToolOptions options, string name)
    {
        var servers = options.Servers();
        var server = servers.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (server is not null)
        {
            return server;
        }

        var available = string.Join(", ", servers.Select(candidate => candidate.Name));
        throw new ArgumentException(
            $"MCP server \"{name}\" has no resources{(available.Length > 0 ? $". Servers with resources: {available}" : "")}");
    }

    private static McpCallOptions RequestOptions(McpResourceServer server) => new() { TimeoutMs = server.TimeoutMs };

    // ------------------------------------------------------------------ results

    private static async Task<AgentToolResult> JsonResultAsync(string tool, string? server, JsonObject payload)
    {
        var (content, fullOutputPath) = await McpTools.LimitMcpContentAsync([new TextContent(payload.ToJsonString())]);
        return new AgentToolResult(
            content,
            Details: new McpToolDetails { Server = server ?? "", Tool = tool, FullOutputPath = fullOutputPath },
            StructuredContent: payload);
    }

    /// <summary>One <c>resources/read</c> content as JSON, without <c>_meta</c>.</summary>
    private static JsonObject ResourceContentsJson(McpResourceContents contents)
    {
        var json = new JsonObject { ["uri"] = contents.Uri };
        if (contents.MimeType is not null)
        {
            json["mimeType"] = contents.MimeType;
        }

        if (contents.Text is not null)
        {
            json["text"] = contents.Text;
        }

        if (contents.Blob is not null)
        {
            json["blob"] = contents.Blob;
        }

        return json;
    }

    // ------------------------------------------------------------------ arguments

    private static string? StringArgument(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        object? value = parameters is not null && parameters.TryGetValue(key, out var found) ? found : null;
        if (value is null)
        {
            return null;
        }

        var text = value switch
        {
            string s => s,
            JsonValue json when json.TryGetValue<string>(out var j) => j,
            _ => throw new ArgumentException($"{key} must be a string"),
        };
        return text.Trim() is { Length: > 0 } trimmed ? trimmed : null;
    }

    private static string? Text(JsonObject raw, string key) =>
        raw.TryGetPropertyValue(key, out var node) && node is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : null;

    // ------------------------------------------------------------------ schemas

    private static ToolSchema ToSchema(JsonObject schema)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var (key, value) in schema)
        {
            dictionary[key] = value;
        }

        return new ToolSchema(dictionary);
    }

    private static JsonObject StringProperty(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
    };

    /// <summary>A <c>{ type: "string" }</c> property. TS <c>optionalString</c>.</summary>
    private static JsonObject PlainString() => new() { ["type"] = "string" };

    private static JsonObject ListParameters() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["server"] = StringProperty("MCP server name. Omit to list every server with resources."),
            ["cursor"] = StringProperty("Opaque cursor from a previous call with the same server; omit for the first page."),
        },
        ["additionalProperties"] = false,
    };

    private static JsonObject ReadParameters() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["server"] = StringProperty(
                "MCP server name exactly as configured. Must match the 'server' field returned by list_mcp_resources."),
            ["uri"] = StringProperty("Resource URI to read. Must be one of the URIs returned by list_mcp_resources."),
        },
        ["required"] = new JsonArray("server", "uri"),
        ["additionalProperties"] = false,
    };

    private static JsonObject ListingErrors() => new()
    {
        ["type"] = "array",
        ["description"] = "Servers that could not be listed",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["server"] = PlainString(),
                ["error"] = PlainString(),
            },
            ["required"] = new JsonArray("server", "error"),
        },
    };

    private static JsonObject ResourceItemProperties(string uriKey) => new()
    {
        ["server"] = PlainString(),
        [uriKey] = PlainString(),
        ["name"] = PlainString(),
        ["title"] = PlainString(),
        ["description"] = PlainString(),
        ["mimeType"] = PlainString(),
    };

    /// <summary>The listing properties plus <c>size</c>, which templates do not have.</summary>
    private static JsonObject ResourceItemPropertiesWithSize() => new()
    {
        ["server"] = PlainString(),
        ["uri"] = PlainString(),
        ["name"] = PlainString(),
        ["title"] = PlainString(),
        ["description"] = PlainString(),
        ["mimeType"] = PlainString(),
        ["size"] = new JsonObject { ["type"] = "number" },
    };

    private static JsonObject ListOutputSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["server"] = PlainString(),
            ["resources"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = ResourceItemPropertiesWithSize(),
                    ["required"] = new JsonArray("server", "uri", "name"),
                },
            },
            ["nextCursor"] = PlainString(),
            ["errors"] = ListingErrors(),
        },
        ["required"] = new JsonArray("resources"),
    };

    private static JsonObject ListTemplatesOutputSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["server"] = PlainString(),
            ["resourceTemplates"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = ResourceItemProperties("uriTemplate"),
                    ["required"] = new JsonArray("server", "uriTemplate", "name"),
                },
            },
            ["nextCursor"] = PlainString(),
            ["errors"] = ListingErrors(),
        },
        ["required"] = new JsonArray("resourceTemplates"),
    };

    private static JsonObject ReadOutputSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["server"] = PlainString(),
            ["uri"] = PlainString(),
            ["contents"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["anyOf"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["uri"] = PlainString(),
                                ["mimeType"] = PlainString(),
                                ["text"] = PlainString(),
                            },
                            ["required"] = new JsonArray("uri", "text"),
                        },
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["uri"] = PlainString(),
                                ["mimeType"] = PlainString(),
                                ["blob"] = new JsonObject { ["type"] = "string", ["description"] = "base64" },
                            },
                            ["required"] = new JsonArray("uri", "blob"),
                        },
                    },
                },
            },
        },
        ["required"] = new JsonArray("server", "uri", "contents"),
    };

    [GeneratedRegex(@";\s*profile\s*=\s*""?mcp-app""?", RegexOptions.IgnoreCase)]
    private static partial Regex McpAppProfileRegex();
}
