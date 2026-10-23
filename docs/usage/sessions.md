# 会话文件、恢复与压缩

会话存档由 `SessionManager` 负责（`Core/SessionManager.cs` / `Core/SessionEntries.cs`），
CLI 的 `--continue` / `--resume` / `--fork` / `--session` 都是它的不同入口。

## 文件与目录

- 默认会话目录：`~/.pi/agent/sessions/<项目路径剥掉盘符>/`，
  由 `SessionFiles.GetDefaultSessionDirPath(cwd, agentDir)` 计算；`--session-dir` 或
  `PI_CODING_AGENT_SESSION_DIR` 可整体覆盖。
- 每个会话一个 JSONL 文件：`<文件时间戳>_<会话UUID>.jsonl`（`SessionManager.cs:173`）。
  每行一个 JSON 条目：会话头、消息、工具调用与结果、压缩记录、分支信息。
- fork 会新建一个文件、复制条目并写入 fork 来源（`SessionManager.cs:936`）。

## CLI 入口对照

| 入口 | 行为 |
|---|---|
| `--continue, -c` | 接着最近一个会话跑 |
| `--resume, -r` | 会话选择器里挑一个 |
| `--session <path\|id>` | 指定文件路径或部分 UUID |
| `--session-id <id>` | 精确 ID，不存在则创建 |
| `--fork <path\|id>` | 复制成新会话再跑 |
| `--export <file>` | 把会话导出成 HTML（不进入交互） |
| `--no-session` | `SessionManager.InMemory`，不落盘 |

SDK 里同样的能力就是 `SessionManager` 的公开方法（`Create` / `InMemory` / 恢复与 fork），
`Sdk.CreateAgentSessionAsync` 只是把它接进装配流程。

## 恢复行为

`CreateAgentSessionAsync` 打开会话后先看存档里有没有可恢复的数据（`Sdk.cs:540` 起）：
有则恢复模型与思考档位，恢复失败时 `result.ModelFallbackMessage` 给出原因并走
`findInitialModel` 兜底——不会因为存档里的模型已经下线而开不了会话。

## 压缩（compaction）

上下文接近模型窗口时按 `settings.compaction` 策略压缩：

| 字段 | 含义 |
|---|---|
| `enabled` | 是否启用（默认 `true`） |
| `reserveTokens` | 给输出预留的 token 数 |
| `keepRecentTokens` | 压缩时至少保留的最近消息 token 数 |
| `backgroundTokens` | 触发后台压缩的阈值 |

`settings.branchSummary` 控制分支摘要。压缩在 durable 任务里是持久化的
（`Harness/Compaction.cs`）：模型调用失败会落到任务的失败结局，重启后从 checkpoint 重跑，
不会丢上下文也不会重复计费——这是端口相对「内存里做压缩」的方案的主要差别。
