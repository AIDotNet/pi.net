# Pi.CodingAgent：嵌入式 coding agent

`Pi.CodingAgent` 是整套东西里最厚的一层，也是唯一一个「装上去就能干活」的包：
它把模型目录、凭据、设置、技能、扩展、会话存档、内置工具（read / bash / edit / write / …）
全部装好，对外只留一个 `AgentSession`。

```bash
dotnet add package Pi.CodingAgent
```

对应示例：[`samples/Pi.Samples.CodingSession`](../../samples/Pi.Samples.CodingSession)。

## 最小会话

```csharp
using Pi.CodingAgent.Core;

var result = await Sdk.CreateAgentSessionAsync(new CreateAgentSessionOptions
{
    Cwd = Directory.GetCurrentDirectory(),
});

var session = result.Session;

if (session.Model is null)
{
    // 没配凭据：result.ModelFallbackMessage 里是原因
    Console.Error.WriteLine(result.ModelFallbackMessage);
    return 1;
}

await session.PromptAsync("列出 src 下的顶层目录，各用一句话说明。");

Console.WriteLine(session.GetLastAssistantText());
```

`CreateAgentSessionAsync` 的装配顺序与上游 TS 版逐行对齐：cwd / agentDir 解析 → 模型运行时 →
设置与会话管理器 → 资源加载（技能 / 扩展 / AGENTS.md）→ 会话模型恢复 →
`findInitialModel` 兜底 → 思考档位四级回退 → 工具解析 → `Agent` 装配 → 补写会话元数据。

`PromptAsync` 在整轮跑完（含所有工具调用）后返回。session 之间互不影响，同一个进程里可以开多个。

## 会话能做什么

| 成员 | 说明 |
|---|---|
| `PromptAsync(text, PromptOptions?)` | 提交提示词；`PromptOptions` 可带图片、关模板展开、标来源 |
| `Subscribe(listener)` | 订阅会话事件，返回 `IDisposable` |
| `Model` | 当前 `ModelSpec`（`null` = 没有可用模型） |
| `Messages` / `GetLastAssistantText()` | transcript 与最后一条助手文本 |
| `GetCallableToolNames()` | 当前模型真正能调用的工具名 |
| `GetSessionStats()` | 消息数、工具调用数、token、成本、上下文占用、存档路径 |
| `RunAgentPromptAsync(params ChatMessage[])` | 直接以消息数组跑一轮（绕过模板展开等预处理） |

`AgentSessionRuntime`（`CreateAgentSessionRuntimeAsync`）在此之上加了
`newSession` / `switchSession` / `fork` / `importFromJsonl`——每个操作会替换活动会话并
按新的工作目录重建服务。切换之后旧会话的订阅需要重新绑定。

## 事件

`AgentSessionEvent` 是 `Pi.Agent` 事件的超集，多了压缩、重试、队列、会话条目等：

| 事件 | 说明 |
|---|---|
| `AgentStartEvent` / `AgentEndEvent` / `AgentSettledEvent` | 运行起止与「彻底空闲」 |
| `TurnStartEvent` / `TurnEndEvent` | 模型回合 |
| `MessageStartEvent` / `MessageUpdateEvent` / `MessageEndEvent` | 消息生命周期（`MessageUpdateEvent` 内嵌 `Pi.Ai` 流事件） |
| `ToolExecutionStartEvent` / `ToolExecutionUpdateEvent` / `ToolExecutionEndEvent` | 工具执行 |
| `CompactionStartEvent` / `CompactionEndEvent` | 上下文压缩 |
| `AutoRetryStartEvent` / `AutoRetryEndEvent` | 自动重试 |
| `QueueUpdateEvent` | steering / follow-up 队列变化 |
| `EntryAppendedEvent` / `ThinkingLevelChangedEvent` / `SessionInfoChangedEvent` | 存档与会话状态 |

```csharp
using var subscription = session.Subscribe(evt =>
{
    switch (evt)
    {
        case AgentSessionEvent.MessageUpdateEvent { AssistantMessageEvent: AssistantMessageEvent.TextDelta delta }:
            Console.Write(delta.Delta);
            break;
        case AgentSessionEvent.ToolExecutionStartEvent start:
            Console.Error.WriteLine($"[tool] {start.ToolName}");
            break;
    }
});
```

监听器是**同步**委托（`delegate void AgentSessionEventListener`），别在里面 `await`。

## 装配选项

`CreateAgentSessionOptions` 只在你想改默认行为时才需要填：

| 选项 | 缺省 | 用途 |
|---|---|---|
| `Cwd` | 进程 cwd | 项目根：决定资源发现、会话分组、内置工具的路径基准 |
| `AgentDir` | `~/.pi/agent` | 全局配置目录（凭据 / 设置 / 模型 / 技能 / 主题） |
| `Model` | 从会话存档恢复，再退到 `findInitialModel` | 指定 `ModelSpec` |
| `ThinkingLevel` | 显式 → 存档 → 模型级覆盖 → 全局默认 → `off` | 思考档位 |
| `Tools` | 内置默认工具集 | allowlist（支持 `*`）；全为 `+name` / `-name` 时是「在默认集上增删」 |
| `ExcludeTools` | 无 | denylist，在 `Tools` 之后生效 |
| `NoTools` | 无 | `All` = 从零工具开始；`Builtin` = 停内置、留扩展工具 |
| `CustomTools` | 无 | 直接注入 `ToolDefinition`（不用写扩展） |
| `SessionManager` / `SettingsManager` / `ResourceLoader` / `ModelRuntime` | 按 `Cwd` / `AgentDir` 现造 | 替换某一块实现（测试常用内存版） |
| `SessionStartEvent` | 无 | 扩展绑定会话时收到的 `session_start` 元数据 |

`Tools` 的语义有一处容易混：`["read", "bash"]` 是**替换**默认集；`["+bash"]` / `["-write"]`
是在 `defaultTools` 设置之上**增删**。两者不能混写，混写会抛 `Invalid tools option: …`。

## 文件与目录约定

```
~/.pi/agent/                # 全局 agent 目录（Config.GetAgentDir()，可用 PI_CODING_AGENT_DIR 覆盖）
├── auth.json               # 凭据（会话内 /login 写入，pi auth 读取）
├── settings.json           # 全局设置
├── models.json             # 自定义模型 / provider
├── mcp.json                # 全局 MCP 服务端
├── skills/                 # 全局技能
└── sessions/               # 会话存档（也可落到项目目录，见 SessionFiles）

<项目>/
├── .pi/
│   ├── settings.json       # 项目设置（覆盖全局）
│   ├── mcp.json            # 项目 MCP 服务端（仅在可信项目里读取）
│   └── skills/             # 项目技能
└── AGENTS.md               # 项目说明，会进 system prompt（也认 CLAUDE.md / AGENTS.override.md）
```

`~/.pi/agent/mcp.json` 是全局服务端，`<项目>/.pi/mcp.json` 只能**覆盖**已有的 `enabled` /
`exposure` / `toolExposure`，不能凭空新增服务端——这是上游的安全约定，别指望在项目里偷偷挂一个。

## MCP / 技能 / 扩展

三者都不需要改代码：

- **MCP**：写 `mcp.json`（字段见 [04-pi-mcp.md](04-pi-mcp.md)），会话启动时自动连接，工具以
  `mcp__<server>__<tool>` 注册。
- **技能**：`skills/<name>/SKILL.md`（带 frontmatter），用户级与项目级都认；模型通过
  `skill` 工具按需加载。
- **扩展**：继承扩展基类、编译成 .NET 程序集后放进扩展目录；C# 扩展 API 见
  [`src/Pi.CodingAgent/Assets/docs/dotnet-extensions.md`](../../src/Pi.CodingAgent/Assets/docs/dotnet-extensions.md)。

## 和 CLI 的关系

`Pi.Cli`（`pi` 命令）是 `Pi.CodingAgent` 的一个**宿主**，不是另一个包：它把会话接到
TUI（`Pi.Tui`）、RPC 模式、print 模式上。它不进 NuGet（`OutputType=Exe`），要用就
`dotnet run --project src/Pi.Cli -- ...` 或自己 `dotnet publish`。

想给团队发一个「只能干某件事」的定制版，正确姿势不是改 CLI，而是像示例那样写一个
`Sdk.CreateAgentSessionAsync` 的小宿主，把 `Tools` / `Cwd` / 扩展固定死。

## 上游用户文档

随包分发（也在仓库里）的 `src/Pi.CodingAgent/Assets/docs/` 是 CLI 与行为的完整说明书，
TypeScript 版原文，接口与端口一致：`quickstart.md`、`cli.md`、`sdk.md`、`configuration.md`、
`settings.md`、`models.md`、`providers.md`、`mcp.md`、`skills.md`、`extensions.md`、
`sessions.md`、`compaction.md`、`security.md`……
