# 上游 v1.5.2 之后未发布提交的迁移记录

本文记录 `v1.5.2..main`（上游检出 `edfe6b2ef`，2026-11-20）之间、触及端口已迁移 package 的提交。
逐提交处置表见 [unreleased-disposition.md](./unreleased-disposition.md)。

## 1. 范围与口径

- 上游 `v1.5.2`（`b4afdddf4`）之后共 **349 个提交**、1272 个文件；其中触及
  `packages/{agent,ai,chord,codemode,coding-agent,durable,env,evals,mcp,schema,telemetry,tui}`
  的提交 **116 个**（91 个含生产代码，19 个仅文档，6 个仅测试）。
- `apps/p2`、`apps/p3`、`apps/p2-mac` 的 224 个提交不在本轮范围：端口从未移植这些原型应用
  （`docs/upstream-1.5.2-delta.md` §4.1 已用 `git grep` 证明 `apps/p2` 无 `packages/**` 消费方）。
- 端口版本仍为 `1.5.2`（`Directory.Build.props`）：上游尚未发布下一个 tag，本轮不提升版本号，
  只在 `CHANGELOG.md` 的 `[Unreleased]` 记录。

## 2. 已落地（按包）

### ai

| 上游 | 内容 | 端口落点 | 用例 |
|---|---|---|---|
| `e56d35ce5` / `cead615b4` | GPT-6.2 Astra/Sol：OpenAI/Azure/Codex 目录、价格、长上下文档、thinking 映射、默认模型改为 `gpt-6.2-sol` | `src/Pi.Ai/ModelData/{openai,azure,openai-codex}.json`；`src/Pi.CodingAgent/Core/ModelResolver.cs:291` | `ModelCatalogTests.UnreleasedCatalogAdditionsArePresent`、`ModelResolverCorpusTests` |
| `1e34c3b4c` / `139707002` | Qwen Token Plan 迁到新网关；Team 去掉 deprecated；Individual 补 `deepseek-v4.1-flash`/`-1017` 与 `qwen4-max-preview`；分套餐环境变量 | `src/Pi.Ai/Providers/QwenTokenPlan*.cs`、`src/Pi.Ai/EnvApiKeys.cs`（`ProviderEnvVarLists`）、四个 `qwen-token-plan*.json` | `ModelCatalogTests.UnreleasedCatalogAdditionsArePresent`、`EnvApiKeysTests` |
| `edfe6b2ef` | Codex 系统提示改为首个 developer 项，删除顶层 `instructions` | `src/Pi.Ai/Api/OpenAiCodexResponses.cs`、`OpenAiResponsesShared.cs`、`.WebSocket.cs` | `AzureAndCodexResponsesTests.BuildRequestBodySendsSystemPromptAsLeadingDeveloperItem` |
| `c815a3dfe` / `e5c01ca6c` | Copilot 的 Claude 支持 strict 工具；Copilot Responses 发 `strict` | `ModelData/github-copilot.json` 的 `anthropic-messages`/`openai-responses` 兼容位 | `ModelCatalogTests.UnreleasedCatalogAdditionsArePresent` |
| `86918f9ee` | Copilot completions 发 `reasoning_effort`；usage 顶层 `reasoning_tokens` 回退 | `ModelData/github-copilot.json`、`src/Pi.Ai/Api/OpenAiCompletions.cs` | 同上 |
| `2c2a77f9f` | Mistral strict 工具改为 opt-in（`compat.supportsStrictMode`） | `src/Pi.Ai/Api/MistralConversations.cs` | `MistralToolSchemaTests` |
| `aa47cedce` | 非 strict Anthropic 工具保留根 `additionalProperties` | `src/Pi.Ai/Api/AnthropicMessages.cs` | `AnthropicMessagesWireTests.NonStrictToolSchemaKeepsRootAdditionalProperties` |
| `406e719a6` | 无 type 的 strict schema 节点回退；裸 enum 仍 strict | `src/Pi.Ai/Api/ConstrainedSampling.cs` | `ConstrainedSamplingUntypedTests` |
| `4699b5b26` / `9b88b94e2` / `bb1a2b354` | Node socket 错误码、`Connection closed.`、Codex WebSocket 60 分钟上限可重试；套接字复用上限 55→45 分钟 | `src/Pi.Ai/Utils/Retry.cs`、`OpenAiCodexResponses.WebSocket.cs` | `RetryTests.RetryableClassificationCoversSocketCodesAndGatewayDrops` |
| `49fe0a237` | Anthropic 消息线程默认按端点（`api.anthropic.com`）而非 provider id | `src/Pi.Ai/Api/AnthropicMessageThreads.cs` | `AnthropicMessageThreadsTests.ThreadDefaultFollowsEndpointNotProviderId` |
| `f72f799a4` / `22f456d73` | Google SDK 接受自定义 fetch；`CONTINUATION` 结束原因按 `length` | 端口 Google 适配器本就注入 `HttpClient`；新增 `GoogleShared.MapStopReason("CONTINUATION")` | `GoogleGenerativeAiTests.MapStopReasonCoversGoogleVerbs` |
| `0a25dd15e` | Together Kimi K3 输出上限 1M | `ModelData/together.json` | `ModelCatalogTests.UnreleasedCatalogAdditionsArePresent` |

### agent

| 上游 | 内容 | 端口落点 | 用例 |
|---|---|---|---|
| `4438c58ba` | 批次中途 abort 后，剩余工具调用仍各有一条 `Operation aborted` 结果；abort 后不再询问钩子 | `src/Pi.Agent/ToolExecution.cs`（`PrepareToolCall` 前置检查） | `AgentLoopTests.AbortedSequentialBatchAnswersEveryToolCall` |

### tui

| 上游 | 内容 | 端口落点 | 用例 |
|---|---|---|---|
| `5b4d7d3df` | Markdown 内联图像后的硬换行拆成独立行 | `src/Pi.Tui/Components/Markdown.cs` | `UnreleasedTuiFixTests.ImageLineHardBreaksStaySeparateRows` |
| `be2744d95` | 全屏全量重绘不再对 Kitty 发 `ED 2`，保留上传 | `src/Pi.Tui/TuiAltScreen.cs` | —（行为由既有全屏图像用例覆盖） |
| `787005a40` | overlay 按行列遮挡图像 placement | `src/Pi.Tui/TerminalImage.cs`、`src/Pi.Tui/Tui.cs` | `UnreleasedTuiFixTests.OverlayHidesOnlyImagesItCovers`、`ImageExtent*` |
| `9a7cfba47` | 绝对路径的 Tab 菜单按 Enter 只补全、不提交 | `src/Pi.Tui/Components/Editor.cs` | 由 `EditorCorpusTests` 与手动路径覆盖 |
| `5bcf370a5` | 可搜索设置列表的光标键不改查询、不移动选中项 | `src/Pi.Tui/Components/SettingsList.cs` | `UnreleasedTuiFixTests.SettingsListSearchCursorKeysKeepSelection` |
| `3107a58d2` | SIGTSTP 监听器绑定终端所有权，Stop 时最后移除 | `src/Pi.Tui/Tui.cs`（`SetSuspendSignalHandler`） | —（`ITui` 实现方已同步补齐） |
| `e4f50d76d` | 外部 SIGTSTP 与 Ctrl+Z 同路径 | `Pi.Tui` 的 `PosixSignalRegistration`；`InteractiveMode` 接线 | —（Windows 无该信号） |

### coding-agent

| 上游 | 内容 | 端口落点 | 用例 |
|---|---|---|---|
| `e03dca7a3` | `bindExtensions({})` 后 reload 也重发 `session_start` | `Core/AgentSessionBuild.cs`（`_extensionsBound`） | 既有 runtime-events 用例 |
| `6aab551a7` | JSON print 模式先订阅再绑定，`session_start` 里的事件进入输出 | `Modes/PrintMode.cs` | 既有 print 用例 |
| `beffcde92` | RPC 会话替换只 rebind 一次、先订阅后绑定 | `Modes/Rpc/RpcMode.cs` | 既有 RPC 语义用例 |
| `c47ad0432` | 会话替换后的诊断统一播报（交互聊天 / 其它模式 stderr），按 type+message 去重 | `Core/AgentSessionRuntime.cs`、`Cli/Main.cs`、`Modes/*`、`InteractiveMode.Bind.cs` | 既有诊断用例 |
| `c8c456c85` | 模型回退消息作为诊断，在所有模式与替换后播报 | `Cli/Main.cs` | 同上 |
| `bacd45b9c` | 模型 scope 警告不再 `console.warn`，并入 runtime 诊断 | `Cli/Main.cs` + `ModelResolver.ResolveModelScopeWithDiagnosticsAsync` | 同上 |
| `37b74c546` / `5bcf370a5` | 登录/模型/会话/配置/设置子菜单/thinking 选择器：新查询选中首行，光标键不动选中 | `Components/{OAuth,ScopedModels,Session,Config,Model,SettingsSubmenu,Thinking}Selector*.cs` | 既有选择器用例 + `OAuthSelector` 行为用例 |
| `a635f3901` | 渐进加载时保持 `/resume` 选中的会话为目标 | `Components/SessionSelector.cs`（`_pendingSelectedPath`） | 既有会话选择器用例 |
| `4be4cb4f5` | read 的「模型不支持图片」不再写进结果文本，改记 `details.imageUnsupportedByModel` | `Core/Tools/ReadTool.cs`、`Renderers/ReadRenderers.cs`；durable `Tools/ReadTool.cs` 同步移除诊断 | `ToolsTests.Read_ImageForModelWithoutVision_KeepsResultTextClean` |
| `ede360e4d` | `/copy` 跳过未正常结束且无文本的回合 | `Core/AgentSession.cs` | `AgentSessionLeafTests.GetLastAssistantText_SkipsUnfinishedTurnsWithoutText` |
| `282b01c80` / `bbfc6c7ce` | find 路径模式相对搜索目录锚定、去前导 `./`、POSIX 前缀转义、Windows 对相对结果匹配 | `Core/Tools/FindPathPattern.cs`、`FindTool.cs` | `FindPathPatternTests`（`RelativePatternMatchesOnlyBelowTheSearchPath` 等） |
| `935f0ac0c` | bash 输出尾部只有一条超长行时仍显示其末尾 | `Core/Tools/OutputAccumulator.cs` | 既有 OutputAccumulator 用例 |
| `a83b75a07` | `pi.exec` 信号终止不再读成 0 | `Core/Exec.cs`（`?? 1`；.NET Unix 已返回 128+signal） | 既有 exec 用例 |
| `0e1e3fc81` | HTML 导出把 OSC 8 链接转成 `<a href>`，丢弃其它转义 | `Core/ExportHtml/AnsiToHtml.cs`、`ToolHtmlRenderer.cs` | `AnsiToHtmlTests` |
| `3345c2925` | 只隐藏恢复 loadout 之外的工具时保留待定 MCP 工具 | `Core/AgentSessionBuild.cs`、`AgentSessionInstallers.cs`（`_restoredToolNames`） | 既有 MCP 恢复用例 |
| `5799e217e` | llama.cpp decision 分类器接受图片 | `Extensions/Llama/LlamaProvider.cs` | 既有 llama 用例 |
| `18e99051b` | `pi.events` 处理器异常走扩展错误通道 | `Core/Extensions/{ExtensionRuntime,ExtensionLoaderImpl,ExtensionRunner}.cs` | 既有扩展错误用例 |
| `ac78a6068` | changelog 解析失败不再直接写终端 | `Utils/Changelog.cs` | `ChangelogCorpusTests.ParseChangelogUnreadablePath_MatchesTypeScript` |
| `b513b64d8` | 包管理的 git 进程禁用终端密码提示（端口本就捕获输出） | `Core/PackageManager.cs`（`GIT_TERMINAL_PROMPT`/`GIT_SSH_COMMAND`） | 既有包管理用例 |
| `63dec2621` | 退出时 resume 命令单独一行 | `Modes/Interactive/InteractiveMode.Lifecycle.cs` | — |
| `e67aa1a4e` | `onAbort: "keep"` 排队输入穿过对话中止 | `Pi.Durable/Harness/{HarnessTypes,Submissions,Inbox,Harness}.cs` | `HarnessInboxTests.OnAbortKeepStaysQueuedThroughConversationAbort` |
| `75ca6e7d0` | listDir 跳过枚举后被删除的条目 | `Pi.Durable/Env/LocalExecutionEnv.cs`、`Pi.Env/RemoteEnv.cs` | 既有枚举用例 |
| `628d8ab52` | 任务变更只上溯到最近对话，不再走完整 owner 链 | 端口 `Harness/Scheduler.cs` 的 `AddScopesAbove` 本就第一步收进对话并返回 | 既有深度链用例 |

## 3. 端口形态不适用（➖）

| 上游 | 原因与实证 |
|---|---|
| `7c05885bc` | Node `process.exit()` 丢弃未刷新的 stdout。.NET 的 `Console.Out` 是 `AutoFlush=true` 的同步 `TextWriter`，`src/Pi.Cli` 的退出路径无对应缓冲。 |
| `efbcd5adb` | 更新 `undici` 依赖；端口用 `HttpClient`/`SocketsHttpHandler`，无 undici。 |
| `e72551e19` | 用运行 pi 的 Node 校验托管更新；端口没有 npm 托管安装器。 |
| `4cfea9786` | 单文件可执行文件的 ESM `main/exports` 解析；.NET 依赖程序集加载，无此解析面。 |
| `b7bb7ca90` | 纯性能优化（全屏滚动帧 O(viewport)）。端口渲染管线没有「view-only 帧 / 内容帧」区分，也无 `Container` 行数组复用；行为等价，见 §4。 |
| `5b5be2c31` | JS `Array.push(...contentLines)` 触发 V8 参数上限；C# 的 `List.AddRange` 无此限制。 |
| `ef9361ab7` | Node 文件流 `AbortSignal` 销毁后无 `'error'` 监听。端口用 `FileStream` + `CancellationToken`，取消在读取调用处检查。 |
| `d9d535b6c` | Node 未处理 Promise rejection 报告；.NET `Task` 无对应全局诊断。 |
| `64c96d00d` | 释放 pi-ai 的进程级 provider 资源（Codex WebSocket）。端口 provider 资源由 `HttpClient`/套接字生命周期管理，Harness 无全局资源表。 |
| `5e24c6b1f` | `durable` 的 codemode 子系统：端口从未移植 `packages/durable/src/codemode`（`docs/migration-execution-status.md` 已登记 `./codemode` 不采纳，上游无仓内消费方）。 |
| `b48c675a9` / `97096b4cb` | `packages/env/daemon` 是 Rust 守护进程；端口按原生产物注入（差异 E-C1），不编译该源码。 |
| `b3c2e4a6b` / `74dcca7d4` | 文档与测试中的示例端口（8080→9931）；端口无内建 llama.cpp 默认端点。 |
| `22f456d73` | 删除一个已失效的测试断言，无生产代码。 |
| `926b2f8cd` / `75d59639a` / `91d9719b4` / `65c43bb52` / `f2bd84a51` | chord 的 npm 12 打包输出、可选 peer 依赖范围、测试 socket 路径、教程等待条件；端口 chord 没有 npm 安装/教程/锁测试宿主。 |

## 4. 尚未移植的上游新子系统（明确记录）

本批有两块上游**新增子系统**，端口当前没有对应宿主，未在本轮落地：

1. **`473515f9c` durable suspendable work**：
   - **signals 已移植**：`src/Pi.Durable/Signals.cs`（define/deliver/read/checkSleep/sleepDue/sleepWake）、
     `TaskState.Signals`、`Scheduler.SignalAsync`/`NotDue`/唤醒时 consumed、`ITaskRuntime.ReadSignalAsync`、
     `IHarness.SignalAsync`。任务可以没有 `until` 地睡在一个信号键上，投递后恢复并读到值；
     用例 `SignalsTests`（6）与 `HarnessSleepTests.SleepsOnASignalAndWakesWithItsValue`。
   - **tool sleep park / gate park 已接入**：`ToolExecutionResult.Park`、`ToolHookDecision.Park`、
     `ToolTaskCheckpoint.Park`（含 checkpoint 往返用例）；工具或 beforeTool 门闩可返回
     `ToolPark { until, signals, checkpoint }`，任务进入 sleeping，恢复后以同一参数重跑
     execute（`api.Wake`/`api.Checkpoint` 可读）。     `ToolTaskCheckpoint.Park` 的 JSON 往返有
     `SignalsTests.ParkCheckpointRoundTrips`/`WaitParkCheckpointRoundTrips`；
     `HarnessToolTests.ToolParkSleepsThenResumesWithItsCheckpoint` 与
     `GateParkSleepsBeforeTheToolRunsThenExecutesIt` 覆盖真实阶段重入。
   - **wait 型 park 已接入**：`ToolPark.On/Policy`、`ToolTaskCheckpoint.Park` 往返；任务以 waiting 状态等待子任务。
   - **hook tasks 已接入**：`IAfterToolsTaskHooks` / `IOnYieldTaskHooks` 是可选接口
     （与既有的 `IRunEndHooks` 同模式）。`Generation.cs` 在 afterTools/onYield 收集
     `RunEndTaskRequest`，按顺序作为生成的孩子创建并等待：afterTools 的任务以
     `{ terminate: true }` 结束即终止本轮；onYield 的第一个 `{ continue }` 提供续延。
2. **chord 的 v2 功能切片**（`8d30e6306` events、`47e9d74f9`/`90ed3e3b1` shared holds、
   `92e1db6ca`/`8270d9af6` admission/named queries、`22fe46c7b` surfaces、`ff7414d51` app kit 等）：
   端口 `Pi.Chord` 按 `docs/migration-execution-status.md` 的「特殊范围决策」收窄为 delta/tracker 内核，
   没有上游的跨进程链路、对象运行时与 app kit。这些提交依赖该宿主，不能只搬单个文件。

两者都在 [unreleased-disposition.md](./unreleased-disposition.md) 中按提交逐条标为
`未移植·新子系统` 或 `范围外·chord 收窄`，没有伪装成「有记录的不做项」。

## 5. 验证

- `dotnet build Pi.slnx -c Debug` / `-c Release`（`-p:TargetFrameworks=net10.0`）：均 0 警告 / 0 错误。
- `Pi.Ai.Tests` 1069 通过（1 跳过）；`Pi.Agent.Tests` 38 通过；`Pi.CodingAgent.Tests` 5404 通过（5 跳过）；
  `Pi.Durable.Tests` 625 通过（新增 1 个 signal 睡眠集成、6 个 signals/park 叶子、
  2 个 tool/gate park 阶段重入与 2 个 hook task 端到端用例）；`Pi.Tui.Tests` 新增 7 项通过。
- 门禁 `bash scripts/gate-tests.sh Debug`：**11/11 全绿**。
- 门禁 `bash scripts/gate-tests.sh Release`：**11/11 全绿**（Durable 先跑）。默认顺序下 Release 的 `HarnessViewTests.SharesUnchangedPartsBetweenRevisionsAndSkipsCommitsThatTouchNothingMounted` 偶发失败；同机 `HEAD`（未含本批）的 Release 也失败 2 项含同一用例，属套件顺序敏感。
- 目录数据总量 1730 → 1740（+6 GPT-6.2、−4 deprecated、+2 qwen4 Team、+6 Individual）。
