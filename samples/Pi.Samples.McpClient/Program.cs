// ---------------------------------------------------------------------------
// 独立使用 MCP 客户端：连接一个 MCP 服务端，列出工具，调用一次。
//
//   dotnet run --project samples/Pi.Samples.McpClient
//   dotnet run --project samples/Pi.Samples.McpClient -- --http http://localhost:3001/mcp
//
// 默认用 stdio 传输拉起 MCP 官方的 everything 参考服务端（需要 Node/npx）：
//   npx -y @modelcontextprotocol/server-everything
//
// 如果你只是想在 pi 里用 MCP 工具，不需要写这段代码——把服务端写进项目的
// `.pi/mcp.json` 或用户级 `~/.pi/mcp.json`，Pi.CodingAgent 会自动连接并把工具
// 暴露给模型（见 docs/usage/04-mcp.md）。
// ---------------------------------------------------------------------------

using System.Text.Json.Nodes;
using Pi.Mcp;
using Pi.Mcp.Transports;

var useHttp = args.Contains("--http");
var httpIndex = Array.IndexOf(args, "--http");
var httpUrl = useHttp && httpIndex + 1 < args.Length ? args[httpIndex + 1] : "http://localhost:3001/mcp";

// 客户端实现名与版本是必填项：服务端会把它记进 initialize 的 clientInfo。
var client = new McpClient(new McpClientOptions
{
    Name = "pi-samples",
    Version = "1.0.0",
    Title = "Pi.NET MCP sample",
    RequestTimeoutMs = 15_000,
});

// 传输层与协议层是解耦的：同一个 McpClient 可以换 stdio / streamable-http / in-memory。
IMcpTransport transport = useHttp
    ? new StreamableHttpTransport(new StreamableHttpTransportOptions { Url = httpUrl })
    : new StdioTransport(new StdioTransportOptions
    {
        Command = OperatingSystem.IsWindows() ? "npx.cmd" : "npx",
        Args = ["-y", "@modelcontextprotocol/server-everything"],
        // 服务端的 stderr 默认被吞掉；要看它的日志就挂这个回调。
        OnStderr = line => Console.Error.WriteLine($"[server] {line}"),
    });

try
{
    var init = await client.ConnectAsync(transport);
    Console.Error.WriteLine($"# server   {init.ServerInfo.Name} {init.ServerInfo.Version}");
    Console.Error.WriteLine($"# protocol {init.ProtocolVersion}");
    Console.Error.WriteLine($"# state    {client.State}");
    if (init.Instructions is { Length: > 0 } instructions)
    {
        Console.Error.WriteLine($"# instructions: {instructions}");
    }

    // ---- 工具 ----
    var tools = await client.ListToolsAsync();
    Console.Error.WriteLine();
    Console.Error.WriteLine($"# {tools.Count} 个工具");
    foreach (var tool in tools)
    {
        Console.Error.WriteLine($"  {tool.Name,-24} {tool.Description}");
    }

    // 调用一个有代表性的工具：everything 服务端提供 add。
    if (tools.Any(tool => tool.Name == "add"))
    {
        var result = await client.CallToolAsync("add", new JsonObject
        {
            ["a"] = 19,
            ["b"] = 23,
        });

        Console.Error.WriteLine();
        Console.Error.WriteLine($"# add(19, 23) isError={result.IsError}");
        foreach (var block in result.Content)
        {
            // 内容块是松散承载（McpContentBlock.Raw），按 type 自行取值。
            var text = block.Raw["text"]?.GetValue<string>();
            Console.WriteLine(text ?? block.Raw.ToJsonString());
        }

        if (result.StructuredContent is { } structured)
        {
            Console.Error.WriteLine($"# structured: {structured.ToJsonString()}");
        }
    }

    // ---- 资源（服务端不一定实现）----
    try
    {
        var resources = await client.ListResourcesAsync();
        Console.Error.WriteLine($"# {resources.Count} 个资源");
        foreach (var resource in resources)
        {
            Console.Error.WriteLine($"  {resource.Uri}");
        }
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        Console.Error.WriteLine($"# 该服务端未提供 resources: {exception.Message}");
    }

    // ---- 直接发原始请求 ----
    // 协议封装没覆盖的方法用 RequestAsync 兜底（method 名与参数完全按 MCP 规范）。
    var ping = await client.RequestAsync("ping");
    Console.Error.WriteLine($"# ping -> {ping?.ToJsonString() ?? "null"}");

    return 0;
}
finally
{
    // CloseAsync 会先做优雅关闭（关 stdin、等进程退出），超时后强杀。
    await client.CloseAsync();
    await transport.CloseAsync();
}
