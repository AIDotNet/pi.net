# 嵌入 SDK 参考

`Pi.CodingAgent` 对外只有一个工厂：`Sdk.CreateAgentSessionAsync`。本文列它的全部入参、
返回值和会话对象的公开面。示例见 `samples/Pi.Samples.CodingSession`（源码即文档，
装配顺序的注释与实现一一对应）。

## 创建会话

```csharp
var result = await Sdk.CreateAgentSessionAsync(new CreateAgentSessionOptions
{
    Cwd = Directory.GetCurrentDirectory(),
});
var session = result.Session;
```

返回 `CreateAgentSessionResult`：

| 成员 | 含义 |
|---|---|
| `Session` | 会话对象（见下） |
| `ExtensionsResult` | 扩展加载结果（含诊断） |
| `ModelFallbackMessage` | 按存档模型恢复失败时的警告；没有则 `null` |

没有可用模型时 `session.Model` 为 `null`，原因在 `result.ModelFallbackMessage`。

## CreateAgentSessionOptions 全部字段

装配入参（TS `createAgentSession` 的同名形参）：

| 字段 | 缺省行为 |
|---|---|
| `Cwd` | 会话管理器的 cwd，再退到进程 cwd |
| `AgentDir` | `Config.GetAgentDir()`（`~/.pi/agent`） |
| `ModelRuntime` | 按 `AgentDir` 现造一个 |
| `Model` / `ThinkingLevel` | 从设置或会话存档恢复 |
| `ScopedModels` | Ctrl+P 轮换的模型组 |
| `ResourceLoader` | 现造 `DefaultResourceLoader` 并 `ReloadAsync` |
| `SessionManager` | `SessionManager.Create(cwd, 默认会话目录)` |
| `SettingsManager` | `SettingsManager.Create(cwd, agentDir)` |
| `CustomTools` | 无 |
| `SessionStartEvent` | 无（扩展绑定会话时的 session_start 元数据） |

工具入参：

| 字段 | 语义 |
|---|---|
| `Tools` | allowlist，支持 `*` 通配 |
| `ExcludeTools` | denylist，在 `Tools` 之后生效 |
| `NoTools` | 无显式 allowlist 时的默认抑制模式（`NoToolsMode.All` 从零工具开始） |

装配顺序（`Sdk.cs` 内有逐行注释）：cwd / agentDir 解析 → 模型运行时 → 设置与会话管理器 →
资源加载（技能 / 扩展 / AGENTS.md）→ 会话模型恢复 → `findInitialModel` 兜底 →
思考档位四级回退 → 工具解析 → `Agent` 装配 → 补写会话元数据。

## AgentSession 公开面

| 成员 | 说明 |
|---|---|
| `PromptAsync(string text, PromptOptions? options = null)` | 跑一轮提示词 |
| `AbortAsync()` | 中止当前运行 |
| `Subscribe(AgentSessionEventListener)` | 订阅会话事件流 |
| `Messages` | 当前消息列表 |
| `GetCallableToolNames()` | 当前可调用工具名 |
| `GetLastAssistantText()` | 最后一条助手文本 |
| `Model` / `ScopedModels` / `CustomTools` | 运行中的模型与工具配置 |
| `SessionManager` / `SettingsManager` / `ModelRuntime` | 会话所用管理器（可后续切换模型等） |

`PromptOptions` 字段（`AgentSession.cs:26`）：

| 字段 | 默认 | 说明 |
|---|---|---|
| `ExpandPromptTemplates` | `true` | 是否展开提示词模板 |
| `Images` | `null` | 随消息附带的图片 |
| `StreamingBehavior` | `null` | 流式行为提示 |
| `Source` | `"interactive"` | 来源标记，写入存档 |
| `Persist` | `true` | 是否写入会话存档 |

## 不落盘的会话

SDK 里显式传 `SessionManager.InMemory(cwd)` 即可（CLI 的 `--no-session` 走的就是它，
见 `Cli/Main.cs` 的 `CreateSessionManager`）。落盘的会话管理器默认写到项目对应的
sessions 目录，见 [sessions.md](sessions.md)。
