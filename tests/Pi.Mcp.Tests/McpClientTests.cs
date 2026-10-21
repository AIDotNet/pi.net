using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;
using Xunit;

namespace Pi.Mcp.Tests;

/// <summary>
/// MCP 客户端端到端测试：以 InMemoryTransport 配对 + 一个脚本化的"模拟服务器"
/// 验证 initialize 握手、工具列举与调用、通知与 ping 的完整链路。
/// </summary>
public class McpClientTests
{
    /// <summary>极简模拟服务器：对入站请求按方法名应答。</summary>
    private static async Task RunFakeServer(InMemoryTransport serverTransport, CancellationToken ct)
    {
        await serverTransport.StartAsync();
        serverTransport.OnMessage(message =>
        {
            _ = Task.Run(async () =>
            {
                switch (message)
                {
                    case JsonRpcMessage.Request request:
                    {
                        JsonNode? result;
                        switch (request.Method)
                        {
                            case "initialize":
                                result = JsonNode.Parse("""
                                    {
                                        "protocolVersion": "2025-11-25",
                                        "capabilities": {"tools": {}},
                                        "serverInfo": {"name": "fake-server", "version": "1.0.0"}
                                    }
                                    """);
                                break;
                            case "tools/list":
                                result = JsonNode.Parse("""
                                    {"tools":[{"name":"read_file","description":"读文件","inputSchema":{"type":"object"}}]}
                                    """);
                                break;
                            case "tools/call":
                                result = JsonNode.Parse(
                                    """{"content":[{"type":"text","text":"文件内容"}],"isError":false}""");
                                break;
                            case "ping":
                                result = new JsonObject();
                                break;
                            default:
                                await serverTransport.SendAsync(new JsonRpcMessage.ErrorResponse(
                                    request.Id,
                                    new JsonRpcErrorObject(JsonRpcErrorCodes.MethodNotFound, "Method not found")));
                                return;
                        }
                        await serverTransport.SendAsync(new JsonRpcMessage.SuccessResponse(request.Id, result));
                        break;
                    }

                    case JsonRpcMessage.Notification { Method: "notifications/initialized" }:
                        // 模拟服务器收到 initialized 后主动发一条通知。
                        await serverTransport.SendAsync(new JsonRpcMessage.Notification(
                            "notifications/message", JsonNode.Parse("""{"level":"info","data":"ready"}""")));
                        break;
                }
            });
        });

        while (!ct.IsCancellationRequested)
            await Task.Delay(10, ct);
    }

    [Fact]
    public async Task FullClientLifecycleOverInMemoryTransport()
    {
        var (clientTransport, serverTransport) = InMemoryTransportPair.Create();
        using var serverCts = new CancellationTokenSource();
        _ = RunFakeServer(serverTransport, serverCts.Token);

        var client = new McpClient(new McpClientOptions { Name = "test-client", Version = "0.1.0" });
        var notifications = new List<string>();
        client.OnNotification("notifications/message", parameters =>
        {
            notifications.Add(parameters?["data"]?.GetValue<string>() ?? "");
            return Task.CompletedTask;
        });

        var result = await client.ConnectAsync(clientTransport);

        // 握手结果。
        Assert.Equal("2025-11-25", result.ProtocolVersion);
        Assert.Equal("fake-server", result.ServerInfo.Name);
        Assert.Equal(ClientState.Connected, client.State);
        Assert.Equal("fake-server", client.ServerInfo?.Name);

        // ping。
        await client.PingAsync();

        // 工具列举与调用。
        var tools = await client.ListToolsAsync();
        var tool = Assert.Single(tools);
        Assert.Equal("read_file", tool.Name);
        Assert.Equal("读文件", tool.Description);

        var callResult = await client.CallToolAsync("read_file",
            new JsonObject { ["path"] = "a.txt" });
        Assert.False(callResult.IsError);
        Assert.Equal("文件内容", callResult.Content[0].Raw["text"]?.GetValue<string>());

        // 模拟服务器在 initialized 通知后推送的通知被接收。
        await Task.Delay(50);
        Assert.Contains("ready", notifications);

        // 关闭后状态落定。
        await client.CloseAsync();
        Assert.Equal(ClientState.Closed, client.State);
        serverCts.Cancel();
    }

    [Fact]
    public async Task ConnectRejectsUnsupportedProtocolVersion()
    {
        var (clientTransport, serverTransport) = InMemoryTransportPair.Create();
        using var serverCts = new CancellationTokenSource();
        // 模拟服务器选择一个不支持的版本。
        await serverTransport.StartAsync();
        serverTransport.OnMessage(message =>
        {
            if (message is JsonRpcMessage.Request { Method: "initialize" } request)
            {
                _ = serverTransport.SendAsync(new JsonRpcMessage.SuccessResponse(request.Id,
                    JsonNode.Parse("""
                        {"protocolVersion":"1999-01-01","capabilities":{},"serverInfo":{"name":"old","version":"0"}}
                        """)));
            }
        });

        var client = new McpClient(new McpClientOptions { Name = "c", Version = "1" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(clientTransport));
        // 握手失败后自动关闭。
        Assert.Equal(ClientState.Closed, client.State);
        serverCts.Cancel();
    }

    private static JsonNode InitializeResultNode() => JsonNode.Parse("""
        {"protocolVersion":"2025-11-25","capabilities":{"tools":{}},"serverInfo":{"name":"fake","version":"1"}}
        """)!;

    [Fact]
    public async Task ProgressNotificationsExtendTheTimeoutAndReachTheCallback()
    {
        var (clientTransport, serverTransport) = InMemoryTransportPair.Create();
        await serverTransport.StartAsync();
        JsonRpcId? progressToken = null;
        serverTransport.OnMessage(message =>
        {
            if (message is not JsonRpcMessage.Request request) return;
            if (request.Method == "initialize")
            {
                _ = serverTransport.SendAsync(new JsonRpcMessage.SuccessResponse(request.Id, InitializeResultNode()));
                return;
            }

            if (request.Method != "tools/call") return;
            JsonRpcId.TryParse(request.Params?["_meta"]?["progressToken"], out var token);
            progressToken = token;
            _ = Task.Run(async () =>
            {
                // 首次响应晚于原始超时；进度通知须把计时器重新拉长。
                await Task.Delay(200);
                await serverTransport.SendAsync(new JsonRpcMessage.Notification(
                    "notifications/progress",
                    new JsonObject
                    {
                        ["progressToken"] = token.ToJson(),
                        ["progress"] = 1,
                        ["total"] = 2,
                        ["message"] = "halfway",
                    }));
                await Task.Delay(200);
                await serverTransport.SendAsync(new JsonRpcMessage.SuccessResponse(
                    request.Id, new JsonObject { ["content"] = new JsonArray() }));
            });
        });

        var client = new McpClient(new McpClientOptions { Name = "test", Version = "1" });
        await client.ConnectAsync(clientTransport);
        var progressSeen = new TaskCompletionSource<McpProgressNotification>();
        var result = await client.CallToolAsync("slow", null, new McpRequestOptions
        {
            TimeoutMs = 300,
            OnProgress = update =>
            {
                progressSeen.TrySetResult(update);
                return Task.CompletedTask;
            },
        });

        Assert.NotNull(result);
        Assert.NotNull(progressToken);
        var progress = await progressSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, progress.Progress);
        Assert.Equal(2, progress.Total);
        Assert.Equal("halfway", progress.Message);
        await client.CloseAsync();
    }

    [Fact]
    public async Task AbortingARequestSendsCancelledNotification()
    {
        var (clientTransport, serverTransport) = InMemoryTransportPair.Create();
        await serverTransport.StartAsync();
        JsonRpcId? requestedId = null;
        var cancelledSeen = new TaskCompletionSource<JsonRpcMessage.Notification>();
        serverTransport.OnMessage(message =>
        {
            switch (message)
            {
                case JsonRpcMessage.Request { Method: "initialize" } initialize:
                    _ = serverTransport.SendAsync(
                        new JsonRpcMessage.SuccessResponse(initialize.Id, InitializeResultNode()));
                    break;
                case JsonRpcMessage.Request { Method: "tools/call" } call:
                    requestedId = call.Id;
                    break;
                case JsonRpcMessage.Notification { Method: "notifications/cancelled" } notification:
                    cancelledSeen.TrySetResult(notification);
                    break;
            }
        });

        var client = new McpClient(new McpClientOptions { Name = "test", Version = "1" });
        await client.ConnectAsync(clientTransport);
        using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<McpAbortError>(
            () => client.CallToolAsync("slow", null, null, abort.Token));

        var notification = await cancelledSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("notifications/cancelled", notification.Method);
        Assert.True(JsonRpcId.TryParse(notification.Params?["requestId"], out var cancelledId));
        Assert.Equal(requestedId, cancelledId);
        await client.CloseAsync();
    }

    [Fact]
    public async Task ServerCancellationAbortsAnIncomingRequestHandler()
    {
        var (clientTransport, serverTransport) = InMemoryTransportPair.Create();
        await serverTransport.StartAsync();
        var handlerStarted = new TaskCompletionSource();
        var handlerAborted = new TaskCompletionSource();
        serverTransport.OnMessage(message =>
        {
            switch (message)
            {
                case JsonRpcMessage.Request { Method: "initialize" } initialize:
                    _ = serverTransport.SendAsync(
                        new JsonRpcMessage.SuccessResponse(initialize.Id, InitializeResultNode()));
                    break;
                case JsonRpcMessage.Notification { Method: "notifications/initialized" }:
                    _ = serverTransport.SendAsync(new JsonRpcMessage.Request(
                        JsonRpcId.FromNumber(99), "slow", null));
                    break;
            }
        });

        var client = new McpClient(new McpClientOptions { Name = "test", Version = "1" });
        client.SetRequestHandler("slow", async (_, cancellationToken) =>
        {
            handlerStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                handlerAborted.TrySetResult();
                throw;
            }
            return new JsonObject();
        });
        await client.ConnectAsync(clientTransport);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await serverTransport.SendAsync(new JsonRpcMessage.Notification(
            "notifications/cancelled",
            new JsonObject { ["requestId"] = 99, ["reason"] = "no longer needed" }));

        await handlerAborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.CloseAsync();
    }

    [Fact]
    public async Task ListToolsPreservesAnnotationsOutputSchemaExecutionAndMeta()
    {
        var (clientTransport, serverTransport) = InMemoryTransportPair.Create();
        await serverTransport.StartAsync();
        serverTransport.OnMessage(message =>
        {
            if (message is not JsonRpcMessage.Request request) return;
            var result = request.Method switch
            {
                "initialize" => InitializeResultNode(),
                "tools/list" => JsonNode.Parse("""
                    {"tools":[{
                      "name":"read_file",
                      "inputSchema":{"type":"object"},
                      "outputSchema":{"type":"object"},
                      "annotations":{"readOnlyHint":true},
                      "execution":{"taskSupport":"optional"},
                      "_meta":{"server":"fake"}
                    }]}
                    """),
                _ => null,
            };
            if (result is not null)
                _ = serverTransport.SendAsync(new JsonRpcMessage.SuccessResponse(request.Id, result));
        });

        var client = new McpClient(new McpClientOptions { Name = "test", Version = "1" });
        await client.ConnectAsync(clientTransport);
        var tool = Assert.Single(await client.ListToolsAsync());

        Assert.Equal(true, tool.Annotations?["readOnlyHint"]?.GetValue<bool>());
        Assert.Equal("object", tool.OutputSchema?["type"]?.GetValue<string>());
        Assert.Equal("optional", tool.Execution?["taskSupport"]?.GetValue<string>());
        Assert.Equal("fake", tool.Meta?["server"]?.GetValue<string>());
        await client.CloseAsync();
    }
}
