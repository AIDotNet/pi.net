using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// resources/list 与 resources/templates/list 的客户端列举：分页游标、聚合翻页、
/// name 缺省回退、原始字段保留。对应 TS client.ts 的 listResources / listResourceTemplates
/// 与其 Page 变体。
/// </summary>
public class McpResourceListTests
{
    /// <summary>分页的模拟服务器：resources 两页、templates 两页，并记录收到的 cursor。</summary>
    private static async Task RunFakeServer(InMemoryTransport serverTransport, CancellationToken ct)
    {
        await serverTransport.StartAsync();
        serverTransport.OnMessage(message =>
        {
            _ = Task.Run(async () =>
            {
                if (message is not JsonRpcMessage.Request request) return;
                var cursor = (request.Params?["cursor"] as JsonValue)?.GetValue<string>();
                JsonNode? result;
                switch (request.Method)
                {
                    case "initialize":
                        result = JsonNode.Parse("""
                            {
                                "protocolVersion": "2025-11-25",
                                "capabilities": {"resources": {}},
                                "serverInfo": {"name": "fake-server", "version": "1.0.0"}
                            }
                            """);
                        break;
                    case "resources/list" when cursor is null:
                        result = JsonNode.Parse("""
                            {"resources":[
                                {"uri":"file:///a.txt","name":"a","title":"A","description":"desc","mimeType":"text/plain","size":12},
                                {"uri":"file:///b.txt"}
                            ],"nextCursor":"page2"}
                            """);
                        break;
                    case "resources/list":
                        result = JsonNode.Parse("""{"resources":[{"uri":"file:///c.txt","name":"c"}]}""");
                        break;
                    case "resources/templates/list" when cursor is null:
                        result = JsonNode.Parse("""
                            {"resourceTemplates":[{"uriTemplate":"file:///{path}","name":"t1","mimeType":"text/plain"}],
                             "nextCursor":"page2"}
                            """);
                        break;
                    case "resources/templates/list":
                        result = JsonNode.Parse("""{"resourceTemplates":[{"uriTemplate":"db://{id}"}]}""");
                        break;
                    default:
                        await serverTransport.SendAsync(new JsonRpcMessage.ErrorResponse(
                            request.Id,
                            new JsonRpcErrorObject(JsonRpcErrorCodes.MethodNotFound, "Method not found")));
                        return;
                }
                await serverTransport.SendAsync(new JsonRpcMessage.SuccessResponse(request.Id, result));
            });
        });
        while (!ct.IsCancellationRequested) await Task.Delay(10, ct);
    }

    private static async Task<(McpClient Client, CancellationTokenSource Cts)> StartAsync()
    {
        var (clientTransport, serverTransport) = InMemoryTransportPair.Create();
        var cts = new CancellationTokenSource();
        _ = RunFakeServer(serverTransport, cts.Token);
        var client = new McpClient(new McpClientOptions { Name = "test-client", Version = "0.1.0" });
        await client.ConnectAsync(clientTransport);
        return (client, cts);
    }

    [Fact]
    public async Task ListResources_FollowsEveryPage()
    {
        var (client, cts) = await StartAsync();
        using var _ = cts;
        var resources = await client.ListResourcesAsync();
        Assert.Equal(["file:///a.txt", "file:///b.txt", "file:///c.txt"], resources.Select(r => r.Uri));
    }

    [Fact]
    public async Task ListResourcesPage_ReturnsOnePageAndCursor()
    {
        var (client, cts) = await StartAsync();
        using var _ = cts;
        var page = await client.ListResourcesPageAsync();
        Assert.Equal(["file:///a.txt", "file:///b.txt"], page.Resources.Select(r => r.Uri));
        Assert.Equal("page2", page.NextCursor);
        var second = await client.ListResourcesPageAsync("page2");
        Assert.Equal(["file:///c.txt"], second.Resources.Select(r => r.Uri));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task ListResourceTemplates_FollowsEveryPage()
    {
        var (client, cts) = await StartAsync();
        using var _ = cts;
        var templates = await client.ListResourceTemplatesAsync();
        Assert.Equal(["file:///{path}", "db://{id}"], templates.Select(t => t.UriTemplate));
    }

    [Fact]
    public async Task ListResourceTemplatesPage_ReturnsOnePageAndCursor()
    {
        var (client, cts) = await StartAsync();
        using var _ = cts;
        var page = await client.ListResourceTemplatesPageAsync();
        Assert.Equal(["file:///{path}"], page.ResourceTemplates.Select(t => t.UriTemplate));
        Assert.Equal("page2", page.NextCursor);
    }

    [Fact]
    public async Task Resource_KeepsOriginalFieldsAndDefaultsNameToUri()
    {
        var (client, cts) = await StartAsync();
        using var _ = cts;
        var resources = await client.ListResourcesAsync();
        var a = resources[0];
        Assert.Equal("a", a.Name);
        Assert.Equal("A", a.Title);
        Assert.Equal("desc", a.Description);
        Assert.Equal("text/plain", a.MimeType);
        // Raw 保留完整字段（含 size），供扩展层序列化给模型。
        Assert.Equal(12, a.Raw!["size"]!.GetValue<int>());
        // 省略 name 的条目回退到 uri，且 Raw 里也被补上（TS toResource 语义）。
        var b = resources[1];
        Assert.Equal("file:///b.txt", b.Name);
        Assert.Equal("file:///b.txt", b.Raw!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Template_DefaultsNameToUriTemplate()
    {
        var (client, cts) = await StartAsync();
        using var _ = cts;
        var templates = await client.ListResourceTemplatesAsync();
        Assert.Equal("db://{id}", templates[1].Name);
        Assert.Equal("db://{id}", templates[1].Raw!["name"]!.GetValue<string>());
    }
}
