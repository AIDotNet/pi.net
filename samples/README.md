# 示例工程

四个控制台工程，演示怎么从 C# 里消费这些包。它们都在 `Pi.slnx` 里，所以
`dotnet build Pi.slnx`（以及 CI）会一起编译——**示例不会悄悄失效**，公共 API 一改就会报错。

| 工程 | 包 | 密钥 | 耗时 |
|---|---|---|---|
| [Pi.Samples.FauxAgent](Pi.Samples.FauxAgent) | `Pi.Agent` + `Pi.Ai` | 不需要 | 秒级 |
| [Pi.Samples.RealModel](Pi.Samples.RealModel) | `Pi.Ai` | 需要 | 秒级 |
| [Pi.Samples.CodingSession](Pi.Samples.CodingSession) | `Pi.CodingAgent` | 需要 | 十几秒 |
| [Pi.Samples.McpClient](Pi.Samples.McpClient) | `Pi.Mcp` | 不需要（要 Node/npx） | 十几秒 |

## 1. FauxAgent：不需要密钥的完整循环

```bash
dotnet run --project samples/Pi.Samples.FauxAgent
```

它用 `Faux` 把模型回合脚本化（第一轮要求调用 `add`，第二轮给出答案），于是 agent 循环、
工具执行、事件流全都能在没有网络的情况下真跑一遍：

```
[tool] -> add { a = 19, b = 23 }
[tool] <- ok (5 ms)
19 + 23 = 42。
--- transcript: 5 条消息 ---
  SystemMessage
  UserMessage
  AssistantMessage
  ToolResultMessage
  AssistantMessage
```

读这个文件能学到：`StreamFn` 怎么给、`AgentTool` 怎么声明与执行、`Subscribe` 怎么拿事件、
`agent.Messages` 里到底有什么。**写自己的 agent 时先从这里改。**

## 2. RealModel：直连真实模型

```bash
# Anthropic（默认 claude-sonnet-4-5）
ANTHROPIC_API_KEY=... dotnet run --project samples/Pi.Samples.RealModel -- anthropic

# 任何 OpenAI 兼容端点（DeepSeek / Moonshot / 本地 vLLM …）
OPENAI_API_KEY=... OPENAI_BASE_URL=https://api.deepseek.com/v1 \
  dotnet run --project samples/Pi.Samples.RealModel -- openai deepseek-chat

# 目录驱动：从内建 42 个 provider 的目录里取完整 ModelSpec
ANTHROPIC_API_KEY=... dotnet run --project samples/Pi.Samples.RealModel -- catalog claude-haiku-4-5
```

三种入口对应文档里说的三条路：瘦 `Model` + 旧版入口、瘦 `Model` + 自定义 `BaseUrl`、
目录 `ModelSpec` + `Models` 门面。文本走 stdout，`usage` / 错误走 stderr，方便管道化。

## 3. CodingSession：嵌入式 coding agent

```bash
# 先准备凭据：pi login，或设 ANTHROPIC_API_KEY / OPENAI_API_KEY
dotnet run --project samples/Pi.Samples.CodingSession                       # 当前目录
dotnet run --project samples/Pi.Samples.CodingSession -- ../some-repo "看看最近的改动"
```

`Sdk.CreateAgentSessionAsync` 之后，你拿到的是一个**真的**会话：它会读 `~/.pi/agent` 里的凭据与
设置、加载项目的 `.pi/` 配置与 `AGENTS.md`、注册内置工具、把存档写进会话目录。
示例打印了模型、可用工具、流式文本、工具调用、重试/压缩事件，最后给出会话统计。

没配凭据时它不会崩：`session.Model is null`，打印原因后返回 1。

## 4. McpClient：独立 MCP 客户端

```bash
dotnet run --project samples/Pi.Samples.McpClient
dotnet run --project samples/Pi.Samples.McpClient -- --http http://localhost:3001/mcp
```

默认用 `npx -y @modelcontextprotocol/server-everything` 拉起官方参考服务端（需要 Node），
列出工具、调一次 `add`、读资源、再发一个裸 `ping` 请求。

如果你只是想在 `pi` 里用 MCP 工具，**不要写这段代码**——写 `mcp.json` 就行，
见 [docs/usage/04-pi-mcp.md](../docs/usage/04-pi-mcp.md)。

## 自己加示例

新建 `samples/Pi.Samples.<Name>/`，`csproj` 里 `<ProjectReference>` 指向需要的 `src/` 项目，
然后把工程加进 `Pi.slnx` 的 `/samples/` 文件夹。`Directory.Build.targets` 会按
`OutputType=Exe` 自动把它排除出打包，不需要额外处理。
