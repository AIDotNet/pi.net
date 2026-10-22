# 其它包

剩下的包大多是「你要用到它的时候自然会用」的专用设施。这里只给入口和适用场景，
不铺开讲——它们的 API 面积比 `Pi.Ai` / `Pi.CodingAgent` 小得多。

## Pi.Tui — 终端 UI 框架

```bash
dotnet add package Pi.Tui
```

入口是 `ITui`（`Pi.Tui` 命名空间），视图由 `IComponent` 组合。它是完整的 TUI 工具包而不是
简单封装：差量渲染 + 同步输出、字素级宽度测量与换行（CJK / emoji / 组合字符）、ANSI/OSC 解析
与 SGR 跟踪、Kitty 键盘协议与按键解码、Kitty / iTerm2 内联图片、浮层栈与焦点管理、鼠标事件、
OKLCH/OKHSL 配色、按键绑定注册表、单行输入（Emacs 式 kill/yank、undo、bracketed paste）、
选择列表、多行编辑器、Markdown 渲染、LaTeX 数学渲染、全屏备用屏渲染器（搜索 / 选择复制 /
滚动条 / 图片缓存）。

**用它的理由**：你要给 `Pi.CodingAgent` 写一个自定义宿主界面，或者干脆自己做一个终端应用。
`Pi.Cli` 的 TUI 就是这套东西拼出来的。

## Pi.Protocol / Pi.Server / Pi.Client — RPC 通道

```bash
dotnet add package Pi.Protocol
dotnet add package Pi.Server
dotnet add package Pi.Client
```

- `Pi.Protocol`：CBOR 线级协议——分帧、编解码、定长严格子集、JS safe-integer 边界。
  **与 TS 实现线级兼容**，所以 .NET 客户端可以驱动 TS 服务端，反之亦然。
- `Pi.Server`：`Server<TMetadata>`，会话路由、请求分发、取消、推送，传输支持 Unix domain
  socket 与本地服务发现。
- `Pi.Client`：`Client`（`IAsyncDisposable`）+ `Connection`，把远程 `pi` 当本地对象用。

**用它的理由**：你要把 coding agent 放进一个独立的进程 / 容器里，由 IDE 插件或 Web 前端驱动。
`Pi.CodingAgent` 的 RPC 模式（`rpc.md` 里那套）就跑在这条通道上。

## Pi.Durable — 耐久会话与任务图

```bash
dotnet add package Pi.Durable
```

入口 `DurableSessions`，存储后端可选内存 / SQLite / JSONL 文件。事务、分支（`Forks`）、
提交后读（`CommittedStateSource` / `CommittedWatch`）、任务图与崩溃恢复。

**用它的理由**：会话要跨进程重启、要能回放到某个提交点、要并行跑子任务。`Pi.DurableHost`
（`pi-durable`）是它的参考宿主，带 `subagent` 工具和按目录的执行环境。

## Pi.Env — SSH 远程执行

```bash
dotnet add package Pi.Env
```

`Connection`（`IDisposable`）连 `pi-env` 守护进程，走它自己的分帧协议；`RemoteWatcher`
做远端文件变更观察。

**用它的理由**：工具要在一台远程机器上跑（编译、跑测试、看远端日志），而你不希望把整个
agent 搬过去。

## Pi.Codemode — 沙箱 JS

```bash
dotnet add package Pi.Codemode
```

用 `ICodemodeJsEngine` 接引擎（默认 `JintCodemodeEngine`），沙箱里唯一的能力是调用注入的
工具；`CodemodeDeclarations` 把工具声明渲染成 TypeScript 供脚本自动补全/类型检查，
`CodemodeIdentifiers` 处理标识符映射。

**用它的理由**：你希望模型**写一小段脚本**来编排多个工具调用（循环、条件、聚合），而不是
一轮一轮地调工具、每轮都吃一次上下文。MCP 工具的缺省 `exposure` 就是 `codemode`，原因在这里。

## Pi.Chord — 编排运行时

```bash
dotnet add package Pi.Chord
```

`Api` + `Context` / `ContextKey<T>`：服务注册、复制状态、RPC、facets 依赖图与拓扑激活。

**用它的理由**：你在做多进程 / 多宿主的应用编排，需要一个比 DI 容器更强的依赖图与状态复制
模型。`Pi.Durable`、`Pi.Env`、`Pi.Protocol`、`Pi.Server` 都建在它上面，日常用 agent 不必直接碰。

## Pi.Telemetry — 遥测契约

```bash
dotnet add package Pi.Telemetry
```

`ITelemetryContext` / `ITelemetrySpan` + 类型化 schema + `InMemoryTelemetryContext`
（参考适配器，也用于断言）+ 一致性测试套件 + `NoopTelemetryContext`。

**用它的理由**：要把 agent 的 span 接到自己的 OpenTelemetry / 内部监控上。`Pi.Ai` 已经依赖它，
所以接一次就能覆盖模型调用。

## 一句话对照

| 你在做 | 用 |
|---|---|
| 自己画终端界面 | `Pi.Tui` |
| IDE 插件 / Web 前端驱动 agent | `Pi.Server` + `Pi.Client` + `Pi.Protocol` |
| 会话要活过进程重启 | `Pi.Durable` |
| 工具要跑在远程机器 | `Pi.Env` |
| 让模型写脚本编排工具 | `Pi.Codemode` |
| 接自己的监控 | `Pi.Telemetry` |
| 多进程应用编排 | `Pi.Chord` |
