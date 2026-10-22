# Pi.Mcp：MCP 客户端

`Pi.Mcp` 是 MCP（Model Context Protocol）的客户端实现：JSON-RPC 协议层 + 三种传输 + 完整
OAuth 层。它不依赖 `Pi.Agent` 或 `Pi.Ai`，可以单独用来连服务端。

```bash
dotnet add package Pi.Mcp
```

对应示例：[`samples/Pi.Samples.McpClient`](../../samples/Pi.Samples.McpClient)。

## 独立使用

```csharp
using System.Text.Json.Nodes;
using Pi.Mcp;
using Pi.Mcp.Transports;

var client = new McpClient(new McpClientOptions
{
    Name = "my-app",              // 必填：服务端会记进 clientInfo
    Version = "1.0.0",           // 必填
    RequestTimeoutMs = 15_000,
});

var transport = new StdioTransport(new StdioTransportOptions
{
    Command = "npx",
    Args = ["-y", "@modelcontextprotocol/server-everything"],
    OnStderr = line => Console.Error.WriteLine($"[server] {line}"),
});

try
{
    var init = await client.ConnectAsync(transport);   // initialize + notifications/initialized
    Console.Error.WriteLine($"{init.ServerInfo.Name} {init.ServerInfo.Version} ({init.ProtocolVersion})");

    foreach (var tool in await client.ListToolsAsync())
    {
        Console.Error.WriteLine($"{tool.Name,-24} {tool.Description}");
    }

    var result = await client.CallToolAsync("add", new JsonObject { ["a"] = 19, ["b"] = 23 });
    foreach (var block in result.Content)
    {
        Console.WriteLine(block.Raw["text"]?.GetValue<string>() ?? block.Raw.ToJsonString());
    }
}
finally
{
    await client.CloseAsync();
    await transport.CloseAsync();
}
```

### 常用成员

| 成员 | 说明 |
|---|---|
| `ConnectAsync(transport)` | 握手，返回 `InitializeResult`（`ServerInfo` / `Capabilities` / `ProtocolVersion` / `Instructions`） |
| `ListToolsAsync()` / `CallToolAsync(name, args)` | 工具 |
| `ListResourcesAsync()` / `ReadResourceAsync(uri)` | 资源 |
| `ListResourceTemplatesAsync()` | 资源模板 |
| `PingAsync()` | 心跳 |
| `RequestAsync(method, parameters)` | **兜底**：协议封装没覆盖的方法直接发（返回原始 `JsonNode`） |
| `NotifyAsync(method, parameters)` | 发通知 |
| `SetRequestHandler(method, handler)` | 应答**服务端**发起的请求（sampling / roots / elicitation） |
| `OnNotification(method, listener)` | 订阅服务端通知 |
| `OnError` / `OnClose` | 错误与断开 |
| `State` / `ServerInfo` / `ServerCapabilities` / `Instructions` | 连接后的状态 |

`CallToolAsync` 返回 `CallToolResult`：`Content`（`McpContentBlock`，松散承载
`JsonObject`，按 `type` 自行取值）、`StructuredContent`、`IsError`。

### 传输

```csharp
// 子进程 stdio
new StdioTransport(new StdioTransportOptions
{
    Command = "node", Args = ["server.js"], Cwd = "/opt/mcp",
    Env = new Dictionary<string, string> { ["TOKEN"] = token },
    InheritEnv = true,                    // 默认 true
    Stderr = "pipe",                      // 或 "inherit"
    OnStderr = line => log(line),
});

// 流式 HTTP（可带自定义头做鉴权）
new StreamableHttpTransport(new StreamableHttpTransportOptions
{
    Url = "https://example.com/mcp",
    Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
    OpenGetStream = true,                 // 握手后打开服务端→客户端的 GET 流
});

// 进程内成对传输，测试用
var (clientSide, serverSide) = InMemoryTransportPair.Create();
```

`StdioTransport` 有 `MaxMessageBytes`（默认见 `TransportDefaults.MaxMessageBytes`）与
`MaxStderrBytes` 上限，服务端疯狂输出 stderr 不会吃满内存。

### OAuth

需要授权的 HTTP 服务端（`401` + `WWW-Authenticate`）走 `Pi.Mcp.OAuth` 与
`AuthProvider`：元数据发现（RFC 8414 / OIDC discovery）、PKCE 授权码、动态客户端注册
（RFC 7591）、凭据失效后重试。凭据存在 agent 目录的 `mcp-auth.json`。

## 在 Pi.CodingAgent 里用（推荐）

大多数场景不用写上面的代码：把服务端写进 `mcp.json`，会话启动时自动连接。

```jsonc
// ~/.pi/agent/mcp.json（全局）或 <项目>/.pi/mcp.json（项目，仅覆盖已有服务端）
{
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "/data"],
      "exposure": "direct"
    },
    "internal-api": {
      "url": "https://mcp.internal.example.com/mcp",
      "headers": { "Authorization": "Bearer …" },
      "enabled": true
    }
  }
}
```

字段：

| 字段 | 说明 |
|---|---|
| `command` | stdio 传输的可执行文件（与 `args` 搭配） |
| `url` | streamable-http 传输的地址（与 `headers` 搭配） |
| `args` | 数组 |
| `env` | 子进程环境变量 |
| `headers` | HTTP 头（放鉴权 token） |
| `enabled` | `false` 可以留着配置但不连接 |
| `exposure` | 见下表，缺省 `codemode` |
| `toolExposure` | 按单个工具覆盖 `exposure` |

工具注册名固定为 `mcp__<server>__<tool>`。

### exposure 四档

| 值 | 行为 |
|---|---|
| `codemode`（缺省） | 工具**不进**模型的工具声明，只能在 codemode 脚本里用 `searchTools()` 找到并调用。省上下文，也避免几十个 MCP 工具把工具表撑爆 |
| `deferred` | 模型先用 `tool_search` 把它们「搜」出来，之后才声明 |
| `direct` | 立刻声明给模型，像内置工具一样可直接调用 |
| `hidden` | 注册但不可达 |

缺省是 `codemode` 这一点最容易让人以为「连上了但模型不会用」——如果模型拿不到 MCP 工具，
先检查 `exposure`。资源类的服务端通过 `list_mcp_resources` / `list_mcp_resource_templates` /
`read_mcp_resource` 三个工具暴露，不受 `exposure` 影响。

### CLI 侧

```bash
pi mcp list            # 列出服务端与连接状态（有服务端未连接或配置错误时退出码 1，可用于 CI 门禁）
pi mcp add …           # 写进 mcp.json
pi mcp remove <name>
pi mcp login <name>    # 起 OAuth 授权流程（浏览器里点同意）
```

会话运行中在 TUI 里输入 `/mcp` 可以打开管理面板：登录、重连、启用/停用、改 `exposure`；
后两项会写回定义该服务端的那份 `mcp.json`。

## 注意

- **连接是后台进行的。** 会话第一条提示词只等 `direct` 工具的服务端；`codemode` / `deferred`
  的服务端在脚本或 `tool_search` 真正需要时才等。所以「刚启动时工具列表不全」是设计而不是 bug。
- **项目级 `mcp.json` 不能新增服务端。** 它只能覆盖全局条目的 `enabled` / `exposure` /
  `toolExposure`——防止一个恶意仓库在你的机器上拉起任意进程。
- **MCP 工具走的是 pi 的工具管道。** `tool_call` / `tool_result` 钩子、权限扩展对 MCP 工具
  与内置工具一视同仁。
