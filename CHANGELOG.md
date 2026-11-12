# Changelog

All notable changes to `pi.net` — the .NET 10 port of [pi](https://github.com/earendil-works/pi) — are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versions track the upstream `packages/*` release line.

## [Unreleased]

## [1.5.2] - 2026-11-12

对齐上游 `v1.5.0 → v1.5.2`（`v1.5.1` 2026-11-09、`v1.5.2` 2026-11-10，共 395 个提交，
`v1.5.0` 是 `v1.5.2` 的祖先）。逐包差异与逐项落点见 `docs/upstream-1.5.2-delta.md`。
本轮把 changelog 中**可复现、可安全移植**的已发布变更逐项落到端口并配套用例；
范围收窄项（Chord v2 换根等）的判定依据与证据在台账 §4 逐条给出。

### Added

- **strict 工具超限按会话记忆**（#11626）：新增 `Pi.Ai.Api.StrictToolLimits`，Anthropic 与
  Bedrock 共用；会话结束经 `SessionResources.CleanupSessionResources` 释放。
- **compat 开关**：`requiresToolsWithToolHistory`（#11843）、`requiresNonNullAssistantContent`
  （#8118）、`promptCacheKeyMode`（#9442，auto / enabled / disabled，覆盖 completions /
  responses / azure 三处 `prompt_cache_key`）。
- **Anthropic 消息线程**（#11545）：新增 `Pi.Ai.Api.AnthropicMessageThreads`（会话级
  create/continue 状态机 + 前缀哈希 + 拒绝回退 + `SessionResources` 清理），接入
  `AnthropicMessages.Stream`；请求带 `thread` 字段与 `message-threads-2026-08-12` beta，
  支持 `disableContinuation` 抑制。新增 10 项 wire/状态机测试。
- **`qwen-token-plan-individual-cn` provider**（#7989）：`Providers/QwenTokenPlanIndividualCn.cs`
  + 目录 + `All.cs`/`ModelCatalog._knownProviders`/`EnvApiKeys` 注册。
- **`resolveSchemaRef`**（#11803）：`CodemodeDeclarations` 公开本地 `$ref` 解析，畸形百分号
  转义（`#/$defs/%E0`）退化为 `unknown` 而非抛 `URI malformed`。
- **`Tx.submission(id)`**（1.5.1）：`ITx` 公开按 ID 读已提交提交记录。

### Changed

- **模型目录对齐到 v1.5.2**：`sonnet-5*` 补 `supportsTemperature: false`（#7961）；
  Azure GPT-6 五个模型上下文改 1,050,000（#11742）、13 个 Azure 模型补 `cost.tiers`
  （#11948）；新增 provider `qwen-token-plan-individual-cn`（#7989，10 个模型 + 独立
  provider 工厂 + 注册表/环境变量映射）、`qwen-token-plan-individual` 的 GLM 5.3（#11658）、
  anthropic 的 Claude Fable 5.2 内联模型；`defaultModelPerProvider` 全 42 项逐字对齐
  上游 v1.5.2（Opus 5.5 / GPT-6.1 Sol / Gemini 4 Argon / Kimi K2.7 Code / MiniMax-M3 等）。
  目录共 43 家 / 1,730 条。生成器数据被上游 `.gitignore` 排除且本机无网络，故按
  权威工件「语料/数据按快照手工维护」的既定做法从 v1.5.2 源码定点复现（台账 §4.6）。
- `OpenAiResponses` 的会话亲和头由 `session_id` 改为 `session-id`（#11793 / #11639）。
- `openai-completions` 默认不再发 `"tools": []`（vLLM 0.20+ 会以
  `tools must not be an empty array` 拒绝），仅当显式配置 `requiresToolsWithToolHistory` 时才发。
- Anthropic OAuth 报告的 Claude Code 版本升到 `2.1.322`（上游 1.5.2）。
- **提示模板参数解析**（#11861）：引号只在参数开头打开，词内撇号保持字面量；未闭合引号当文本；
  反斜杠只转义当前引号字符。`prompt-corpus.json` 三条旧向量按 1.5.1 语义更新。
- **TUI**：HTML 注释不再作为可见文本渲染（#11710 / #11755）；Alacritty 无 `TERM_PROGRAM`
  时靠 `TERM` / `ALACRITTY_WINDOW_ID` 也能启用 OSC 8 超链接（#11767）。
- **遥测**：`renderTrace` 把 span / 事件名、属性键、错误名 / 消息、link 的 trace / span id 里的
  C0 / C1 / DEL 控制字符转义成 `\uXXXX`。
- **codemode**：`console.*` 与抛出的非 Error 值里的 `NaN` / `Infinity` / `-Infinity` 不再打成
  `null`（#11901 / #11945）。
- **schema**：`number` 类型不再接受 NaN / Infinity（TypeBox 语义）。
- **grep**：glob 去掉前导 `./`（rg 不匹配），保留 `!` 取反前缀（#10998 / #11807）。
- **重试**：上游流被丢弃（"cut short after the reply had begun"）现在触发自动重试（#11879）。
- **流式续接开关**（#11545 可分离部分）：`SimpleStreamOptions.DisableContinuation`、agent
  `streamProxy` 的 `disableContinuation` 转发。缓存预热公共 helper 与
  `SessionResources.CleanupSessionResources` 端口本就存在。
- **上下文窗口收敛**（#10497）：`clampMaxTokensToContext` 对 OpenRouter 留 15% 输入余量，
  大窗临近写满时把输出压到 32K，避免被自带 tokenizer 误判为溢出而提前压缩。
- **连接错误根因**（#11695）：`ProviderError.Normalize` 在无 status/body 时把最深层
  `InnerException` 原因拼进 message，不再只留 "Connection error."。
- **`Tx.submission(id)`**（1.5.1）：`ITx` 公开按 ID 读已提交提交记录。
- **Anthropic 非 strict 工具根 `$defs`/`definitions`**（#9557）：属性里的本地 `$ref` 不再指向空。
- **SSE 孤立 CR 行尾**（#11764）：`Pi.Mcp.SseStream` 支持 CRLF / LF / 孤立 CR，
  并正确处理跨 chunk 拆开的 CRLF，不再误分发空事件。
- **截断不劈开代理对**（#7121）：`Truncate.SliceHead` / `SliceTail` 保持非 BMP 字符完整，
  grep/codemode/压缩摘要/MCP 错误文本不再出现孤立代理。
- **粘贴标记重编号**（#11923）：退格删除短标记后，后续短标记不再变成 `[paste #1undefined]`。

### Fixed

- **不支持架构不再错下 x86-64 的 fd/rg**（补齐上游 `c8ec752cf` / #11209）：`ToolsManager` 现在只映射
  x64/arm64 × darwin/linux/win32 的发布包，`arm` / `ia32` 等架构在**任何网络请求之前**返回
  「用系统包管理器安装」的提示，不再静默回退到 x86-64 二进制。此前这一项在 1.5.0 差异台账里
  标为「待核」，是 1.5.0 对齐里唯一漏掉的用户可见修复。`toolsEnsureTool` 语料中 `aix/x64` 的
  过期期望已同步为上游 1.5.0 行为（生成器不在仓库内，语料按快照手工维护）。
- `Shell_CleanupTerminatesActiveProcesses` 改为等 shell 真正启动（marker 文件）再清理，并给结果加
  5 秒超时，与上游 `env-node.test.ts` 同口径。此前固定 `Task.Delay(300)` 在本机早于子进程 PID 登记
  （实测约 865ms），清理拿到空集合、命令跑满 30 秒后报成功。诊断证明 `taskkill /F /T` 对 Git Bash
  进程树有效（PID 登记后清理 1.1 秒结束、退出码 1），**不是** Git Bash 边界或实现缺陷，也未按
  「环境限制」跳过用例。

### Fixed（第三轮：把台账中剩余可移植的已发布变更真正落到代码）

- **`PipeTransport`**（1.5.1 新增公开 API）：新增 `Pi.Mcp.Transports.PipeTransport` + `IMcpPipe`
  + 共享 `JsonRpcLineReader`，让 MCP stdio 帧可以跑在「由别处启动的程序」（如 SSH 远程进程）之上；
  15 项用例对照上游 `pipe.test.ts` / `lines.test.ts`（分帧、超大消息、stderr 尾部、关闭/超时 kill、退出/失败）。
- **MCP stdio 大消息 O(n²)**（#11761）：`StdioTransport.PumpStdoutAsync` 与
  `JsonRpcLineReader.ReadStdout` 改为按 chunk 累积、到换行才合并一次，分多次到达的大消息不再反复复制/扫描。
- **Streamable HTTP 应答体/错误体上限**（#11335）：新增 `ReadBodyTextAsync` 分块读取，
  `application/json` 应答受 `maxMessageBytes` 约束（超限取消并拒绝），错误体只读前 8 KiB、其余取消，不再整体缓冲。
- **oversized SSE 释放连接**（#11729）：`SseStream.ConsumeAsync` 在超限等错误时显式 `stream.Dispose()`
  （cancel reader）后重抛，调用方 `using var response` 亦释放内容流、关闭连接；新增 2 项定向用例。
- **预发布版本 changelog 重复**（#11703）：`Changelog.GetNewEntries` 只读前导 `major.minor.patch`
  （`0.1.5-20261020.7` 记为 `0.1.5`），保存版本无三元组时返回「不显示 notes」而非整本 changelog；
  两处交互模式调用点同步更新；语料 21 向量（含 8 条 #11703 回归）经 v1.5.2 TS `getNewEntries` 重放，
  v1.5.0 基线重放 0 diff 保真。
- **read/write 新增语言高亮**（#11643）：`ThemeHelpers` 语言表新增 Dart、Nix、Groovy、Gradle 映射
  （4 向量定向用例）。
- **find/ls `limit` 边界**（#7121）：`limit` 低于 1 一律按 1；恰好 `limit` 条不再误报「达到上限」
  （内部多取一条区分）；custom glob 被要求多返回一条；6 项定向用例。
- **Ctrl+P 单模型 scope 可切换**（#11806）：`AgentSession.NextCycleIndex` 改为可空返回并对齐上游
  `getNextCycleIndex`——单元素 scope 仍能选中其唯一模型，当前模型在 scope 外时 forward/backward
  分别选首/末个模型；两处 cycle 方法去掉 `<=1` / `-1→0` 短路；3 项定向用例 + 124 项交互模式用例全绿。
- **全屏 Kitty 图像按覆盖行之后绘制**（#11835）：`TuiAltScreen` 去掉 WezTerm 门控，
  `drawKittyImagesLast` / `imageCellsNeedRedraw` 对所有 Kitty 协议终端生效（修 Orca 只显示首行）；
  1 项定向用例（无 WezTerm 环境下 transmit 晚于行清除与文本，并用旧门控反向验证用例有效）。
- **`pi config` 加载自定义键位**（#11651）：`ConfigSelectorCli` 在建 TUI 前调用
  `GlobalKeybindings.Set(KeybindingsManager.Create(agentDir))`，与 session-picker 一致；
  1 项定向用例（临时 keybindings.json 的 ctrl+p / ctrl+n 经 CLI 装载方法进入全局注册表）。
- **Windows/WSL Ctrl+V 粘贴键**（#11765）：`AppKeybindings` 的 `app.clipboard.pasteImage`
  在 Windows 下默认键改为 `alt+v` + `ctrl+v`（此前仅 `alt+v`）；1 项定向用例。
- **bash 超时补报 shell 退出码**（#11613）：shell 已先于超时退出、后台进程持续写输出直到
  超时时，超时文案追加 shell 的退出码与退出耗时（如「after 1.1 seconds」）；`ShellTimeoutException`
  保持 `timeout:N` 形式不影响自定义 `BashOperations`，`ChildProcessHandle.ExitedAtMs` 在超时
  回调快照退出时刻。3 项定向用例 + `CoreToolsTests` 48/48 回归。
- **`/tree` 导航后思考档位恢复**（#11451）：每次 prompt 前把实时思考档位补记到当前分支
  （按当前模型钳制后比对，与实时档位不同才写），避免 resume 恢复到旧分支的档位；
  `RecordSelection` 拆出 `RecordThinkingLevel` + 纯判定 `ShouldRecordThinkingLevel`。
  1 项定向用例 + `AgentSessionCompactionTests` 33/33。
- **tool_search / codemode 跟随本地 `$ref`**（#11803）：MCP 把模型放在 `$defs` 的 schema，
  tool_search 搜索文本与 codemode output 摘要现在都能解析 `#/$defs/...` 里的属性名与描述
  （JSON Pointer 转义、畸形 percent escape→跳过、`seen` 防递归/共享重复展开）。
  `ToolSearch.SchemaText`+`ResolveLocalRef`、`CodemodeToolApi.FollowRef`+`DescribeFields`+
  `DescribeUnionFields`。6 项定向用例（逐字对照上游）+ `ToolSearchCorpusTests` 66/66、
  `ToolSearchRankingTests` 7/7、`CodemodeCorpusTests` 29/29。
- **内部光标标记不外泄到终端**（#9441）：`Tui.ApplyLineResets` 在 normalize 前剥离全部
  光标标记、`ExtractCursorPosition` 整行剥离、`TextLayout.SliceWithWidth` 仅把非 APC 序列
  作为 pendingAnsi 带过切片边界（多标记 / 水平裁剪 / 其他行残留的 APC 不再被终端渲染出来）。
  4 项定向用例 + 全量 `Pi.Tui.Tests` 23973/23973。
- **退出前等待 OAuth 刷新写入落盘**（#11692）：`RuntimeCredentials.SettledAsync` 登记进行中
  的凭据写入（含失败也要结算），`ModelRuntime.SettleCredentialWritesAsync` 暴露，
  `AgentSessionRuntime.DisposeAsync` 在关闭时 await，避免后台目录刷新用掉一次性 refresh token
  却丢失新 token。3 项定向用例 + `AgentSessionRuntimeTests` 23/23、`CoreRuntimeTests` 19/19、
  `ClientRuntimeTests` 14/14。

### Fixed（第四轮：队列图片 / 空串出队 / pending 区展开 / 缓存默认档位 / 会话结算与 reload 守卫）

- **排队消息图片按当前模型档位缩放**（#11738）：steering/follow-up（含 `prompt()` streaming）
  的图片此前按全尺寸发送与入库；现在在 `message_start` 出队后、扩展事件与落库之前，复用空闲
  提示词的 `NormalizePromptImagesAsync`/`ProcessPromptImageAsync` 原地归一化（`UserMessage.Content`
  由 `init` 放宽为 `set`），缩放提示追加到首段文本。`_queuedMessagesWithImages` 按引用跟踪、
  投递时移除，`ClearQueue` 清理。3 项静态叶子用例 + 3 项 `AgentSessionQueuedImagesTests`
  端到端用例（对照上游 `agent-session-queued-images.test.ts`，SkiaSharp 超高 PNG 触发缩放）。
- **纯图片排队消息不再滞留队列**（#8612）：去掉 `DequeueMatchingUserText` 与
  `HandleAgentEventAsync` 里的非空文本守卫，无条件按 `contentText`（空串）出队，对齐上游
  v1.5.2 的无条件 `indexOf`。1 项定向用例（steering 空串出队 / 回落 followUp 空串）。
- **`ctrl+o` 作用到 agent 工作期间的 `!`/`!!` 输出框**（#11875 / 上游 #11886）：
  `SetToolsExpanded` 把 `_pendingMessagesContainer` 纳入遍历；排队消息重建时先放回 pending
  bash 输出框；`agent_settled` 时冲刷进 chat；切会话清空；新建输出框以当前展开态开始。
  2 项定向用例。
- **`cacheWarming` 默认档位 `streaming` → `idle`**（v1.5.1，7bcc15c92）：符合条件的提示缓存
  在两次 agent run 之间继续保温；`SettingsManager.GetCacheWarmingMode` 回落值与
  `Assets/schemas/settings.schema.json` 的 `default` 同步改为 `idle`。2 项定向用例
  （运行期回落值 + schema 产物默认值）。
- **dispose 前先结算在途回合**（#9126，7a53c310a）：`AgentSessionRuntime.DisposeAsync` 在
  `EmitSessionShutdownEventAsync` 之前 `await Session.AbortAsync()`，避免被中止回合残留
  「有 tool call 无 tool result」的助手消息（与 `TeardownCurrentAsync` 一致）。1 项定向用例。
- **核心 reload 的 streaming/compacting 守卫**（#9222，c3b938e3e）：把原先只在 TUI 侧的
  入口检查下沉到 `AgentSession.ReloadAsync`，让 RPC / SDK 调用方同样在 `IsStreaming` /
  `IsCompacting` 时被拒，避免扩展工具 ctx 变 stale。1 项定向用例。
- 上述六项合计 `V152BatchRegressionTests`（5 用例）+ `AgentSessionQueuedImagesTests`（3 用例）
  + 静态叶子/出队/交互模式用例，全量门禁 11/11 绿。

### Fixed（第五轮：复核反馈后把 §4.4 曾误记为「不做项」的 published 功能变更逐项真实移植）

此前台账 §4.4 把下列 **v1.5.1/v1.5.2 已发布的功能变更**登记为「有记录的不做项」，
属对这些 release 变更的漏做。本轮逐项真实移植 + 定向用例钉住，§4 仅余「➖ 不适用」
（端口形态不同 / 仓库外 / 第三方库 / 纯性能优化）项并附代码级实证：

- **`pi auth login/logout`**（9b9cb30ae）：新增 `Cli/AuthLogin.cs`（parseLoginRequest /
  findMatchingProviders / selectAuthType / webUrl / displayUrl / planRadiusMcpServer 纯叶子 +
  LoginInteraction 的 browser/no-browser 方法选择与事件提示 + runAuthLogin/Logout 编排 +
  catalog refresh / Radius MCP 配置 / 取消与同步失败文案）与 `Cli/AuthTerminal.cs`（进程终端 +
  `ModelRuntime` 包装）；`Main.cs` 早于 parseAuthCommand 分流、`AuthCommand`/`Args` help 对齐
  v1.5.2。`AuthLoginTests` 30 用例逐字对照上游 auth-login.test.ts（反向验证破坏 OAuth 默认后
  6 用例失败）。
- **逐会话 `CompactionSettings`**（#11771，922c3ff2b）：`HarnessTypes` 新增
  `CompactionSettings`，`CompactionPolicy.ForModel(model, conversation)` 逐字段分层解析
  （会话 model override > harness model override > 会话自身 > harness 全局，`enabled:false`
  压过 harness model override）；`AgentState`/`AgentChange`/`Agent` 加字段；`AgentDocs`
  configure 校验 / 序列化往返 / ResolveAgent 透传；`Compaction`/`Generation` 三处读者接线。
  `CompactionSettingsTests` 11 用例。
- **JSONL `readOnly` 只读打开**（#11939，f5f4a71af）：`JsonlStorageOptions.ReadOnly` +
  `JsonlStorageReadOnlyError`；不建目录、恢复失败且 `main.jsonl` 大小变化时重试、
  `CommitAsync` 拒绝、只读不截断撕裂末行 / 不回收 / 不删 `.reclaim`。
  `JsonlStorageReadOnlyTests` 6 用例逐字对照上游。
- **#11807/#9276** rg `--glob` 锚定 path + `--context N` 上下文行事件（`GrepRgEvents.cs`，
  16 用例）、**#9155** prompt 预检 / 树导航（1 用例）、**#8544** edit 保留未改动行行尾
  （12 用例）、**#11756** `registerMarkdownTransformer(messageTypes)` + **#11851**
  `ctx.ui.custom(blocked)`（6 用例）、**#9654** read 流式选行（9 用例）、**#11599/#11752**
  RPC/print 关闭时序（6 用例）、**#11975** 会话 cwd 丢失 continue/fork/cancel（4 用例）。
- **#11974** find abort 落在 `.git` 父目录查找期间不启动 fd（spawn 前 abort 检查，
  `FindAbortBeforeSpawnTests` 2 用例，反向验证有效）；**#11892** 调度 pass 期间并发发布
  waiter/holder 不丢（`SchedulerNotificationRaceTests` 真实 `TaskScheduler` 行为回归 1 用例，
  端口 `_dirty` 前置清零 + 每遍全量重算架构本就免疫）。
- **仅余 ➖ 不适用（代码级实证，非 published 功能漏做）**：Mermaid 升级（第三方 Node 库
  替换，端口 `Mermaid.cs` 的 `render()` 是注入 seam）、`SqliteStorage.commit(batches)` 与
  chord store group commit、#11725 Codex SSE abort 监听——这几项在第六轮已进一步
  真实移植 / 等价回归，见下。仍为 ➖ 不适用且附当前源码级实证的仅：Windows 安装器
  `install.ps1` / PowerShell 5.1（修复在 pi.dev 仓库，`git ls-files` 证明端口无该脚本）、
  `pi update --self` npm 自更新（非 npm 包）、TypeBox 构造 API / 微秒时钟 / Node 动态
  import。逐条证据见台账 §4.4。

### Fixed（第六轮：复核 hook 第四次反馈，把 §4.4 剩余 published 项逐项真实移植/等价回归）

- **#8158 Mermaid 渲染升级**：`Mermaid.cs` 真实移植 published 的可观察着色语义——
  `MermaidSpan` 加 `Href`/`Role`；`MermaidAnsiTheme.From(getFgAnsi)` 派生 SGR 参数串
  （edgeLabel `muted`→`text`、title 前置 `1;` 加粗）；`Mermaid.ToAnsi` 按语义类输出
  ANSI，且仅当 `role=="text"` + `GetCapabilities().Hyperlinks` 时给标签加 OSC 8 链接。
  `RuntimeTheme` 委托 `Theme.GetFgAnsi` + 终端能力。`MermaidAnsiTests` 5 用例逐字对照
  上游 mermaid.test.ts；既有 `MermaidTests` 27/27 不回归。
- **#11725 Codex SSE 重试 abort 监听泄漏**：`OpenAiCodexResponses.AbortableDelayAsync`
  升为 internal，等价契约（多次 backoff 无残留注册、abort 抛 `"Request was aborted"`）
  由 `CodexSseRetryBackoffTests` 4 用例钉住。
- **group commit（fabf916fc / 8889e623d）**：`GroupCommitDifferentialTests` 2 用例把
  上游「differential against MemoryStorage」的目标语义（多批顺序、后批依赖前批、
  seq 严格递增）在端口可观察层面钉住；`git show fabf916fc --stat` 证明其不碰
  `types.ts` 接口（纯 SQL 合并优化），chord store.ts 属端口无对应物的 v2 层
  （`git ls-files src/Pi.Chord` 实证）。
- **验证覆盖补齐**：hook 指出 gate 不含 `Pi.Tui.Tests` / `Pi.Env.Tests`、且最后一次
  代码改动后缺完整 Debug 门禁。本轮实测：Debug/Release 全量 build 0 警告 0 错误；
  `gate-tests.sh Debug` 11/11 全绿（Pi.CodingAgent 5362/5365、Pi.Durable 607/607）、
  `gate-tests.sh Release` 11/11 全绿；门禁外 `Pi.Tui.Tests` 全量 23975/23975
  （首次 1 例 `LoaderTests.Stop_HaltsTheAnimation` 负载 flake，隔离 14/14、全量重跑全绿）、
  `Pi.Env.Tests` 50/54（4 项需 sshd 跳过）。

### Fixed（第七轮：全面审计 1.5.2 迁移遗漏与 bug，逐条代码核对后修复）

以「全面检查迁移是否完成、有无遗漏和 bug、修复到上线标准」为目标，从上游权威
`packages/*/CHANGELOG.md` 1.5.1/1.5.2 段（210 bullets / 11 包）与
`git log v1.5.0..v1.5.2`（143 个触及 `packages/*/src` 的 published commit）逐条核对，
发现并真实移植 3 个端口确实缺失的可移植行为：

- **#11707/#11711 `pi update` 报告**：更新完成后逐包发 `result` 事件（npm 版本变化
  `npm:foo 1.2.0 -> 1.3.0`、git commit from/to、registry 陈旧 warning），新增
  `PackageUpdateReport` 纯叶子四态汇总（offline 跳过 / warned-up-to-date /
  already-up-to-date / `Updated N packages:` + 版本变化行），CLI 收集 result/warning
  输出。`PackageUpdateReportTests` 5 + `PackageManagerUpdateTests` 3 集成用例。
- **#11679 `refreshModelsAuth`**：`IProvider.RefreshModelsAuth`（`"credential"`/
  `"configured"`），`Models.RefreshAsync` configured 模式只判配置、不刷新过期 OAuth、
  `context.credential=null`，pi.dev 目录刷新不再被 stale 登录拖垮；`RemoteCatalogProvider`
  =configured、`PersonalizedCatalogProvider` 转发、`ProviderComposer` 组合判定。
  `RefreshModelsAuthTests` 2 用例钉住两模式差异。
- **#11964/#8842 shell tool native exit code**：`PowerShellTool` 新增退出码后缀，逐字对齐
  上游修正 Windows 5.1 stderr 重定向误判 exit 1；`BuildCommand` 纯叶子仅旧版宿主且命令
  不以反引号结尾时追加。`PowerShellExitCodeTests` 5 用例。
- **数据派生覆盖工件**：`scripts/gen_v152_coverage.py` 从上游 git 机械枚举 143 个
  published src-commit，逐条列出 `hash | subject | 文件数` 并对照端口各项目 src/测试文件数，
  生成 `docs/v1.5.2-commit-coverage.md`（可脚本重跑核对，非手写分类表）。

## [1.5.0] - 2026-11-05

对齐上游 `v1.5.0`（308 个提交）。三个大头：**durable 的窗口化视图与提交语义**、**遥测与传播**
（AI 层与 durable 全词表）、**嵌套 durable 调用**。逐条对照与判定见 `docs/upstream-1.5.0-delta.md`。

### Added

- **遥测**：`pi.ai.request` / `pi.ai.poll` span 与四个指标；durable 全词表——`pi.durable.reports`、
  `pi.durable.phase`（含 `phase.duration`、`tasks.active`）、`pi.durable.submit`（含跨 trace 的 link）、
  `pi.durable.entry` / `submission` / `report` / `checkpoint` 事件；`TelemetryPropagation` 上下文传播；
  `RemoteTerminal` 走 `Connection` 层
- **durable 嵌套工具调用**：`IToolExecutionApi.ExecuteToolAsync`、`ToolTaskInput.Nested` / `ToolTaskResult.Nested`、
  `tool.nested-call` 文档族（结果写在调用方文档里、不进 transcript）；同一个 key 重跑时用已存结果，
  并发 `pi.durable.replay`
- **窗口化视图**：`ConversationView.entries` 的 `before` / `from` / `min` / `max`，`ConversationTranscript`
  的 `gaps` / `loadEarlier` / `fillGap` / `resume`；随之删除整个 `watchEvents` 事件流
- **提交语义**：`{ type: "continue" }` 提交（不落条目、从不排队）；在调用方的提交内受理
  （`ConversationHandle.submit(tx, draft)`，且句柄现在收任意 `SubmissionDraft`）；`pi.live.runFailure`
  （含失败运行的历史起点 `first`）
- **运行控制**：每会话工具轮次上限；`afterTool` 覆盖全部结算路径（含 `tool_unavailable`）；
  `afterRun` 与运行结束任务；工具交接后运行继续；重启循环以 `task_interrupted` 可见结束
- **流式**：`streaming-json` 增量解析器；工具参数按增量写（块数不变时只写 `arguments` 的变化）
- **Harness 出口**：`observeConversation` / `observeTaskGraph`、`Session.observeDoc`
- **TUI**：OSC 133 shell 集成、双击选词字符集、原生 Shift+Enter、Markdown 顶层块复用、Kitty 功能键、
  公式超宽回退原始 LaTeX、换行边界与删除线修复、`Text.setText` 同文本保留折行
- **env / mcp**：启动错误分类；`mcp login` 作用域

### Changed

- 模型目录升到 models.dev v2（42 份目录按 1.5.0 生成器重导，不移植生成器）
- assistant 消息记录**请求**的思考档位；轮询检查点同样携带
- 空闲检查按脏集合走，`wake()` 的睡眠集合与空闲过期索引收窄到相关会话
- 只读 Harness：以 `readOnly` 打开的 Harness 不派发也不写
- `IConversationHandle.SubmitAsync` 的入参由 `SubmissionDraft.Input` 收宽为任意 `SubmissionDraft`（子类型关系保留，
  旧调用不受影响）；新增 `SubmitAsync(ITx, SubmissionDraft)`，在调用方自己的提交内受理

### Removed

**注意：1.4.5 → 1.5.0 含破坏性变更，不是纯增量。** 以下按影响面排列：

- **durable 的事件流整条移除**（窗口化视图的前提，对应上游 `f3255de76`）：`Pi.Durable.Harness.Events`
  （`Events.WatchEventsAsync`）、`AgentEventStream`、`EventsObserver`、`ViewObserver.Publication`，以及被删的
  `Harness/Events.cs`（1052 行）定义的整套类型——`AgentEvent` 与它的变体/快照（`Snapshot`、`SnapshotRun`、
  `SnapshotGeneration`、`RunView`、`Parts`、`RunStart` / `RunEnd` / `TurnStart` / `TurnEnd` /
  `MessageStart` / `MessageUpdate` / `MessageEnd` / `EntryAppended` / `InboxUpdate` / `Submission` /
  `UsageChanged` / `TaskFailed` / `AgentChanged` / `CompactionStart` / `CompactionEnd` / `AutoRetryStart` /
  `AutoRetryEnd` / `DeferredPoll` / `ToolExecutionStart` / `ToolExecutionUpdate` / `ToolExecutionEnd`），
  `MessageChange`（`Start` / `Delta` / `ToolCallDelta` / `Block` / `Whole`）、`QueuedItem`、`RetryState`、
  `DeferredState`、`ToolOutputChange`、`ToolUpdateResult`
  **迁移**：改用 `conversation.WatchAsync(window)` 拿到 `CommittedWatch<ConversationView>`，缺口用
  `ConversationTranscript.FillGapAsync`，更早历史用 `LoadEarlierAsync`，合并用 `Merge`，续订用 `Resume`。
  （`Pi.Agent` 下同名的 `AgentEvent`、`Pi.Durable.Types` 下同名的 `Submission` 属另两套类型，均未受影响。）
- `ToolTaskInput` / `ToolTaskResult` 由普通 record 升为 `public abstract record` 联合体：
  `ToolTaskInput.Model(…)` / `.Nested(…)`、`ToolTaskResult.Model(…)` / `.Nested`。直接 `new ToolTaskInput(…)`
  的调用方改走 `Model(…)`；从旧记录读入不受影响——缺 `kind` 的行仍按模型调用解析。
- `WatchEnd.Retired` 删除（退休改为调用上的标记，不再是独立终态）、`WatchEnd.Cancelled` 更名 `Aborted`
  （对齐上游 `retired` 取消 / `cancelled → aborted`）。
- `SchemaValidationError` 换了命名空间与参数顺序：`Pi.Ai.Utils` → `Pi.Ai.Utils.Schema`，
  `(Keyword, InstancePath, Message)` → `(Path, Keyword, Message)`。
- `ExternalEditorResult.Failed` 带上必填原因：`Failed(string Message)`。
- 位置参数有变化、需重编译才兼容的 record：`ExecResult` 多出 `Truncated`、`ToolSearchDocument` 多出
  `NamespaceText`（按位置构造可过，按元素解构与 `with` 需对齐）。

### Fixed

- `pi-schema` 校验语义、AI wire 层三项（含 Bedrock strict 回退）
- 窗口化视图与 `ConversationTranscript` 的缺口填充、更早历史加载
- 未提供（`unoffered`）的工具调用按工具任务跑并由其结算 `tool_unavailable`
- TUI 若干按字节对齐的渲染修复（定界搜索、连续匹配优先、显示宽度回退）

### 已知不做

- `e5c6182ba` 分组提交 + 批量 `Storage.commit`：可观察行为一致，差吞吐与 `IStorage` 契约形状（见 delta §6.1）
- durable code-mode：1.4.0 起的产品决策不采纳（见 delta §4.8）
- `LiveDoc.nestedTools` 槽位：嵌套调用尚无生产调用方（`ExecuteToolAsync` 目前只有测试在用），不为不可达路径铺 UI
  管道；等出现第一个生产调用方再做（见 delta §7.9）
- 代码高亮复用：没有性能证据，加缓存层要处理失效，属「有诉求再做」的优化（见 delta §7.9）
- `d5b3de22d` 第二半（轮询检查点的恢复侧断言）：只补回归保护、不修缺陷，成本是 faux provider 的 deferred 流
  + 崩溃恢复模拟，属独立一批（见 delta §7.9）

## [1.4.5] - 2026-10-28

两个主题：**扩展系统终于能吃上游的 TypeScript 插件**（方案 A，进程内 JavaScript 宿主），
以及顺带落地的 **.NET 9 兼容**（多目标 `net9.0;net10.0`）。

### Added

- **进程内 JavaScript/TypeScript 扩展宿主（方案 A）**：`.ts` / `.tsx` / `.js` / `.mjs` / `.cjs` / `.jsx`
  扩展现在直接加载，不再要求先编译成程序集。实现要点：
  - **转译**：`@babel/standalone` 跑在 Jint 上（`Assets/extension-runtime/vendor/babel.min.js`），
    `sourceType: unambiguous` 让 TypeScript、node_modules 里的 ESM 和 CommonJS 走同一条管线；
    结果按内容哈希缓存，编译线程用 64 MB 栈（TypeBox 的生成式解析器会撑爆默认栈）。
  - **模块系统**：CommonJS 解析（相对路径、`node_modules` 上溯、`package.json` 的
    `exports`/`module`/`main`、目录 index），每个扩展一个 Jint engine，互不污染。
  - **Node 内置模块**：`fs`、`path`、`os`、`process`、`timers`、`url`、`crypto`、`util`、`events`、
    `child_process`、`buffer`、`module`、`zlib`、`readline` 的 shim，全部经宿主落到真实的文件系统/进程。
  - **`@earendil-works/*` 运行时**：`pi-ai`（TypeBox 风格 `Type`、`StringEnum`、`uuidv7`）、
    `pi-coding-agent`（`defineTool`、`getAgentDir`、`withFileMutationQueue`、截断助手、
    内置工具工厂）、`pi-tui`（`Text`/`Box`/`Container`/`matchesKey`/`visibleWidth` 等）。
  - **宿主 API 桥**：`pi.*` 全量注册接口（工具/命令/快捷键/flag/事件/exec/消息/会话标题/工具开关）、
    `ctx.ui`（notify/select/confirm/input/editor/status/widget）、`ctx.sessionManager`、
    `pi.events` 跨扩展事件总线，全部经 JSON 桥落到既有 `IExtensionApi`。
  - **Promise 泵**：扩展的 `async` 工厂与处理函数由 `Engine.Advanced.ProcessTasks()` 加自建定时器队列
    驱动，宿主不需要事件循环。
- `CompositeExtensionModuleLoader`：按扩展名分派——`.dll` 走 `AssemblyLoadContext`，源码走 Jint。
  `ExtensionLoader` 的默认装载器换成它，`DiscoverAndLoadExtensions` 不再把 `.ts`/`.js` 分流成 warning。
- `docs/dotnet-extensions.md` 重写：两条扩展路径（程序集 / 源码）的入口约定、shim 覆盖面、
  尚未桥接的 API 清单。

### Changed

- **多目标 `net9.0;net10.0`**：`Directory.Build.props` 的 `TargetFramework` 改为 `TargetFrameworks`，
  .NET 9 消费者可以直接引用。36 个工程（含测试与示例）在两个 TFM 上都是 0 错误 0 警告——
  代码零改动，因为 net9 已具备 net10 用到的全部 API。唯一实质改动是
  `AgentSessionEvents.cs` 的 23 个 `override string Type` 要逐个补 `[JsonIgnore]`：基类上的
  `[JsonIgnore]` 不会被 override 属性继承（STJ 只读直接声明的特性），漏标会写出两个 `"type"` 键，
  net10 的 `JsonObject` 默认容忍重复键而 net9 直接抛 `An item with the same key has already been added`。
  `Pi.Evals/ExtensionCompiler.cs` 生成扩展工程时的 TFM 也改成跟随当前运行时。
  （不做 .NET 8：实测要改约 66 处 .NET 9 新 API，改造面与长期维护成本都不划算。）

- **`ResolveExtensionEntries` 恢复上游顺序 `index.ts` → `index.js` → `index.dll`**：源码是事实来源，
  编译产物退为后备（1.4.4 里「优先 index.dll」的做法按上游语义回退）。
- 1.4.4 里「源码扩展只给一条提示」的行为已被上面的宿主取代；`SourceExtensionWarning` 与
  `PartitionSourceExtensions` 一并删除。
- `pi.registerMessageRenderer` / `registerEntryRenderer` / `registerToolRenderer` /
  `registerMarkdownTransformer` 目前**降级为警告**：扩展照常加载，自定义渲染不生效，而不是让整个扩展
  加载失败。`registerProvider` / `registerVirtualModel` 仍然显式抛错——静默忽略会让用户误以为模型
  供应商已注册。

### Fixed（运行期审查发现的缺陷）

结论：`JsExtensionTests` 只证明「能加载并注册」，用 `ExtensionRunner` 真实派发事件后才暴露出下面 6 个问题。
均已在本次修掉，并补了 `JsExtensionRuntimeTests`（7 例，含两个真实上游守卫扩展）钉住：

- **事件处理函数拿到的是原始 JSON，不是 ctx**：`api.on` 直接把宿主传来的 payload 交给 handler，
  于是 `ctx.ui.notify`、`ctx.sessionManager.getBranch`、`ctx.ui.theme` 全部是普通字段，
  上游扩展在 `session_start` 里一调用就崩（语料里 18 例）。命令与工具走的是
  `__pi_make_context`，只有事件漏了。修复后语料运行期通过数 55 → 71。
- **`pi.*` 在工厂返回后失效**：`InvokeFactory` 在 `finally` 里解绑 api，而命令/工具/事件回调都在
  工厂之后才执行，因此 `pi.getActiveTools()` 之类会抛「used its pi object outside a host call」。
  改为绑定到扩展生命周期（reload 时重新绑定）。
- **事件处理函数的结果被丢弃**：调用的是 `On<E>` 重载（返回值 `Task`），结果恒为 null，
  只有 `On<E, R>` 会保留；`tool_call` 的 `{ block: true }` 因此从来没生效过。
- **`ctx.sessionManager` 是个空壳**：payload 里放的是字符串占位 `"__pi_fn__"`，
  `getBranch`/`getEntries` 一调用就 TypeError。改为由宿主按需提供（`ctx.session.entries` 等）。
- **上下文构造时急切读取运行时动作**：`RegisterContext` 直接读 `ThinkingLevel`/`Model`/`IsIdle`，
  在运行时未绑定（加载期、project-trust、测试）时抛「Extension runtime not initialized」，
  让一次事件派发整体失败。改为 JS 侧惰性 getter + 宿主 try/catch 降级。
- **`fs.watch` 报「not a function」**：改为明确的「没有后台事件循环」错误，并补 `fs.watchFile` /
  `fs.promises.watch`；`pi.registerTool` 丢弃 `renderCall`/`renderResult` 时也会告警而不是静默。
- **事件结果只映射了 `{ block }`**：`context` 的 `{ messages }` 与 `message_end` 的 `{ message }`
  被静默丢弃（改写上下文/消息的扩展等于无效）。现在按 `ContextEventResult` / `MessageEndEventResult`
  映射，形状不匹配时降级为「无结果」并记 debug 日志，不会让其他事件误伤。

### Added (verification)

- `JsExtensionTests`（6 例）：上游 `hello.ts` 真实加载并执行工具、`node:fs`/`node:path` 解析、
  命令/flag/快捷键/事件注册、无默认导出、`enum`/`satisfies`/泛型语法。
- `JsExtensionRuntimeTests`（7 例）：经 `ExtensionRunner` 真实派发事件——上游 `protected-paths.ts`
  拦截 `.env` 写入且对非目标工具放行、`permission-gate.ts` 拦截危险命令、命令处理函数在工厂返回后
  仍能调用 `pi.*` 并读 `ctx.sessionManager`、`pi.on` 的退订生效、工具 `execute` 拿到工具上下文并
  透传 `onUpdate` 部分结果、`pi.events` 跨扩展投递。
- `UpstreamTypeScriptExampleTests`：把上面那次一次性语料探针固化成**可复跑的回归测试**。
  口径 = 顶层 `*.ts` + 各目录 `index.ts` 共 **79 个真实入口**，每个都经真实加载器加载、经
  `ExtensionRunner` 派发 `session_start` → `tool_call`(bash) → `tool_call`(write) → `context`，
  并把 handler 异常（`ExtensionRunner.OnError`）也算失败——不是「加载成功了」就算过。
  当前结果：**71 clean / 2 handler-fail / 6 load-fail**；clean 的 71 个里 43 个注册了事件处理器、
  16 个注册工具、33 个注册命令、34 个有 UI 表面（合计 80 handlers / 30 tools / 54 commands /
  3 flags / 2 shortcuts），确认是真在干活而不是空转。
  8 个失败全部归因明确并钉在 `KnownFailures` 表里（表变了测试就红，无论变好还是变坏）：

  | 入口 | 原因 |
  | --- | --- |
  | `custom-provider-anthropic/index.ts` | 缺 `@anthropic-ai/sdk` |
  | `gondolin/index.ts` | 缺 `@earendil-works/gondolin` |
  | `sandbox/index.ts` | 缺 `@anthropic-ai/sandbox-runtime` |
  | `with-deps/index.ts` | 缺 `ms` |
  | `custom-provider-gitlab-duo/index.ts` | `pi.registerProvider`（需 streaming 协议桥） |
  | `jev-router.ts` | `pi.registerVirtualModel`（同上） |
  | `file-trigger.ts` | `fs.watch`（扩展只在回调栈上运行，无法投递给空闲扩展） |
  | `mac-system-theme.ts` | `osascript`（macOS 专用，Linux/Windows 上同样失败） |

  前 4 个是依赖未安装而非宿主缺陷：这些目录都带 `package.json` + `package-lock.json` 且
  `node_modules` 被 `.gitignore`（上游也一样，`npm install` 后即可）。宿主解析 node_modules
  的能力由 `NodeModuleResolution_ResolvesADependencyFromNodeModules` 单独钉住（手写假包，
  不依赖 Node 与网络），它一旦失败说明这 4 个是宿主的锅。

  未覆盖（非缺陷，记录在 `src/Pi.CodingAgent/Assets/docs/dotnet-extensions.md`）：
  `registerProvider` / `registerVirtualModel`（需 streaming 协议）、`ctx.ui.custom()` 与
  三个自定义渲染器（需同进程渲染循环，目前降级为警告而非加载失败）、`create*ToolDefinition`
  的精确 schema。

## [1.4.4] - 2026-10-27

对齐上游 `v1.4.0` 之后 88 个未发布提交里的 P0 部分：13 条用户能撞到的缺陷，外加几条低风险特性。
上游剩余的大头是 chord/durable 的 watch 体系重构，另立阶段处理。

### Added

- `pi doctor [--json]`：在会话初始化前离线诊断本地配置、项目信任、Shell/搜索工具路径和凭据来源。
  支持文本与结构化 JSON 输出；不加载用户扩展、不写文件、不刷新 OAuth 或执行取密命令。
- **可编译的 C# 扩展示例**：`samples/Pi.Sample.Extension`（一个工具 + 一个斜杠命令 + 状态栏更新），
  里面写了 `ProjectReference Private="false"` 的原因（契约程序集必须由宿主解析，扩展目录不能放
  私有副本）。已加入解决方案并接入 `dotnet-extensions.md`。

- **GPT-6.1 Terra**：`openai`、`openai-codex`、`azure` 三个 provider 补上 `gpt-6.1-terra`
  （$1/$5，缓存读 $0.05、写 $1.25，272k 以上走长上下文档位；Codex 侧 500k 窗口）。它接受
  `reasoning.effort: "none"`，而 Sol 不接受，两者都拒绝 `minimal`——thinking 档位表按此填写。
  `openai` 的默认模型同时从 `gpt-5.5` 换成 `gpt-6.1-sol`。
- `pi-messages` 终态事件（`done`/`error`）现在透传 `responseModel`，与其它五家 API 一致。

### Changed

- **源码扩展改为一条可执行的提示，而不是一个坏掉的加载**：发现结果里的 `.ts`/`.js`/`.mjs`
  等在进入加载器前被分流，记一条 warning「TypeScript/JavaScript extensions are not loadable by
  the .NET port. Compile … into an assembly …」，同目录的 `.dll` 仍然正常加载。
  `LoadExtensions` 本身保持与上游一致（它可以被喂任意路径，由 module loader 决定怎么处理）。
- **`ResolveExtensionEntries` 优先 `index.dll`**：同一目录同时存在 TS 源与编译产物时，只有能加载
  的那个算数（上游顺序是 ts → js，因为它的运行时能编译源码）。
- `Assets/examples/extensions` 重命名为 **`extensions-typescript`**，README 顶部标明「protocol
  reference only」，避免用户拷进 `.pi/extensions/` 后误以为能生效。

### Fixed

- **扩展模块的默认装载器还是 4d-2b 的占位实现，真实加载必然失败**：`ExtensionLoader` 在调用方
  不给 `moduleLoader` 时落到 `MissingModuleLoader`，它直接抛「no IExtensionModuleLoader was
  supplied. The AssemblyLoadContext implementation lands with batch 4d-3」。而 4d-3 的
  `AssemblyLoadContextExtensionLoader` 早就写好了，只是默认值没换——`ResourceLoader` 的两处
  `LoadExtensionsCached(...)` 都没传 loader，于是**任何真实扩展都加载不了**（内置扩展不受影响，
  它们走 `BuiltInExtensions`）。现在默认 `AssemblyLoadContextExtensionLoader.Instance`
  （进程内单例，保住按路径复用的 collectible context），占位类删除。
- `--list-models` 的模糊搜索包含模型显示名称，可搜索 `models.json` 中的自定义名称；表格格式与排序保持兼容。
- **MCP 登录只认 http/https**：授权端点会用系统 URL 处理器打开，之前只挡了
  `javascript:`/`data:`/`vbscript:`，`file:` 和 `search-ms:` 这类自定义协议仍会被拉起本地程序。
  元数据解析和 `startAuthorization` 两处都按上游改成只放行 http/https。
- **不可信输入加固**：`pi-messages` 的 `contentIndex` 来自 wire 却直接当数组下标用，`__proto__`
  式的越界值会把 content 撑成稀疏数组或让后续 delta 写错块，现在只接受「下一块或已有块」；
  Radius OAuth 的授权端点与 `verification_uri` 走统一的 http/https 校验；登录对话框只打开
  http/https 链接，非网页 URL 里的控制字符替换成空格；终端标题（会话名来自会话文件）同样
  清洗 C0/C1 控制字符，避免 BEL/ESC 提前结束 OSC 序列。
- **Anthropic 系统更新落到 user 消息前面导致 400**：转换后没有任何块的 assistant 消息会被丢弃，
  而中段系统消息在检查之前就 flush 了；现在压在真正发出去的 assistant 消息之前。
- **Anthropic 逐消息 effort 时顶层固定发 `low`**：组织可以设 effort 上限，之前固定 `high` 会让
  低/中档请求也被整条拒掉；真实档位仍由逐条消息的 `output_config` 携带。
- **faux provider 把缓存写入又算进 input**：带 `sessionId` 时同一个 prompt token 同时进了
  `input` 和 `cacheWrite`，`input + cacheRead + cacheWrite` 超过估算的 prompt。现在缓存模拟期间
  `input` 恒为 0，`cacheWrite` 用 `promptTokens - cacheRead` 计算。
- **OpenCode 走了错的协议端点**：`/inference/v1` 是所有协议共用的旧地址，按上游改成
  `/inference/openai/v1`、`/inference/anthropic`、`/inference/google/v1beta`、
  `/inference/systemone/v1`（Go 侧同理加 `/go` 前缀）。顺带把 `deepseek-v4.1-flash` 的
  `supportsLongCacheRetention` 关掉——网关会拒 `prompt_cache_retention`。
- **`pi.exec` 吞掉 spawn 失败原因**：`Process.Start` 找不到可执行文件时抛 `Win32Exception`，
  之前整个方法直接抛出去；现在按 Node 的 reject 分支返回 `code 1` 并把错误信息放进 `stderr`。
- **一个装不上的扩展包会拦住整个启动**：`settings.json` 里的包因为注册表不可达、包不存在或
  git prepare 失败时，解析阶段抛异常直接崩；现在跳过该包、记一条 warning（带 `pi remove`
  提示），下次启动或 `/reload` 重试。
- **grep 上下文行在孤立 CR 的文件里整体偏移**：取上下文时把 CRLF 和单个 CR 都折成了换行，
  而 ripgrep 只按 LF 计行；现在只归一 CRLF。
- **provider id 含 `/` 时 `provider/id` 解析不到**：`llama-server=http://host:10800` 这类
  URL 派生的 id 会在第一个 `/` 处被切断；改成在已知 provider id 里做最长匹配。
- **深层嵌套引用块渲染爆炸**：宽度被嵌套耗尽后仍按 1 列逐层重排，80 列下 50 层要 30 秒、
  55 层直接栈溢出；现在放不下边框时按原宽直接渲染子节点。语料里修复前的引用块向量改为
  由专门的深度用例覆盖（`MarkdownBlockquoteDepthTests`）。
- **隐藏 thinking 且标签为空时留下一行空白**：空标签的隐藏块现在整段不渲染，也不再占一个
  空行；有标签时保持原来的标签行与间距。
- **工具搜索不优先点名匹配**：查询就是工具名（或 MCP 名的 `__` 末段）时，长度归一化会把描述里
  恰好含这些词的短文档排到前面；现在点名匹配排最前，其余仍按 BM25 分数排。

## [1.4.3] - 2026-10-24

### Added

- **工具结果图片的跨 API 回归**：这条链路（工具返回截图后，模型看到的是"文本占位 + 附着的图片"）
  此前只有 Anthropic 有断言，其余四个 API 的 wire 逻辑没有覆盖。新增 `ToolResultImageTests`（17 例）
  钉住 openai-completions（提成 user 消息、`requiresAssistantAfterToolResult` 插 assistant、
  不支持图片时由共用变换层追加 `(tool image omitted: …)`）、Google（`functionResponse.parts`，仅
  Gemini 3+）、responses（`input_image` 数组 vs 文本占位）、Mistral（六种占位组合）的三态行为。
  另补 `AnthropicOpenRouterCostTests`（起 SSE server 验证成本接入的取数位置）与
  `EveryChatModelKeepsItsCoreFields`（1599 个 chat 模型的 api/name/input/cost/contextWindow 零缺失）。
- **durable 的内置图片处理器**：上一版只补了 `read` 的图片分支与 `IImageProcessor` 契约，宿主不注入
  处理器时 BMP 仍只能报错、超像素的图也报错。现在 `Pi.CodingAgent/Utils/Image/SkiaImageProcessor.cs`
  给出默认实现（解码 → EXIF 摆正 → 缩放到限制 → PNG/JPEG 编码 → 按 3/4 递减尺寸直到字节达标），
  由 `DurableHarnessSetup` 注入 `CodingTools.Create(...)`，对应上游 `createCodingTools({ images })`。
  配套给 `ImageResizeOptions` 加 `ForceReencode`：BMP 这类 provider 不能内联接收的格式必须重编码，
  否则缩放核心的快路径会把它原样透传。
- **OpenRouter 上报成本**：`UsageCost.TotalFromProvider` + `Utils/OpenRouterCost.cs`
  （`IsOpenRouterModel` / `ApplyReportedCost`）。走 OpenRouter 时非 BYOK 直接用上报的 `cost`，
  BYOK 再叠加 `upstream_inference_cost`（缺失时用 prompt + completions 两段之和），
  `is_byok` 缺失或成本非法则保持目录价。接入 `AnthropicMessages` 的 `message_delta` 与
  `OpenAiCompletions.ParseChunkUsage`；`CalculateCost` 会把这个标记重置为 false。
  此前这类请求只按目录价计入，BYOK 场景下真实开销完全不进 `cost.total`。
- **42 家 provider 的完整模型目录（1713 个条目）**：此前 `src/Pi.Ai/ModelData/` 只有三个手写文件、
  8 个条目（`gpt-4o` 那批已被上游淘汰的模型），而 `ModelResolver.DefaultModelPerProvider` 的 41 条
  默认模型是照上游新 id 抄的——**41 条里一条都解析不到**，全新安装遍历完整张表也选不出模型。
  数据取自 `@earendil-works/pi-ai@1.4.0` 的 `dist/providers/data/`（上游 `build:offline` 会把
  gitignore 的 `src/providers/data` 复制进发布包），连同 `.manifest.json`（`generatedAt` 供
  `GetBuiltinModelDataGeneratedAt` 判断目录新旧）。现在 openai 42 chat + 1 classifier、
  openrouter 401 chat + 63 image + 28 classifier、anthropic 17、radius 32 基线，默认表 41/41 全部可解析。
  未识别字段（`enabled` / `lab` / `providers` / `output` / `promptCache` / `free`）进 `Extra` 原样保留
  ——上游的 `model-catalog.ts` / `models.ts` 也不消费它们。新增回归
  `EveryEmbeddedCatalogParses`（42 份都能解析、总数 1713）与
  `EveryDefaultModelResolvesInTheBuiltinCatalog`（默认表逐条可解析）。
- **OpenAI Decisions 分类器（`src/Pi.Ai/Api/OpenAiDecisions.cs`）**：整块补上上游
  `api/openai-decisions.ts`。此前 OpenAI 是唯一**没有**注册 `classifiers` 的 provider——System One
  的另外五个承载（Cloudflare Workers AI、TypeSafe、OpenRouter、OpenCode、Vercel AI Gateway）都在，
  唯独 `POST /v1/decisions` 这条路缺失，`model.Api = "openai-decisions"` 会落到「不支持分类」。
  逐条对齐上游：状态以紧凑 JSON 文本作 `input`；带图时变成一条 user 消息（`input_text` 后跟
  `input_image` data URL，上限 128 张）；`choice`/`score`/`bool` 分别映射到
  `choice`/`score`/`predicate`，predicate 没有 criteria 字段，true/false 的含义并进 `instructions`；
  概率是 `[{value, probability}]` 数组而非对象；refusal 是错误结果而不是答案；usage 在解析答案
  **之前**读（畸形答案的请求同样已计费）。连带补 `CreateProviderOptions.FilterAllModels`
  （上游 `filterAllModels`）：ChatGPT 订阅 token 到不了 Decisions 路由，因此 OAuth 凭据下分类
  模型不可见。
- **`ProviderRetryOptions.NoRetryStatuses`**：上游 `retryProviderRequest` 的 `noRetryStatuses`。
  Decisions 用它把网关 504 挡在重试之外——超大输入（约 60 万 token 以上）会稳定撞上同一条时间
  上限，重试只是再撞一次；504 另给固定的超时文案而不是通用错误串。
- **`Pi.Durable` 的 `read` 支持图片**（`tools/read.ts` 的 `readImage`）：此前遇到图片直接返回
  `unsupported_image`。现在按上游两条路径处理：给了 `IImageProcessor` 就交它解码/摆正/缩放/重编码，
  并把「从 X 转成 Y」「从 AxB 缩到 CxD，坐标乘 N 倍」写成 `info` 诊断；没给处理器时，PNG/JPEG/
  GIF/WebP 在字节与像素限制内原样透传（BMP 等需要转换的格式报错并说明）。限制取自对话模型的
  `inputLimits.images.resize`，缺省 2000×2000 / base64 4.5 MiB；模型不支持图片时补一条占位说明。
  配套补齐 `ImageMime.ImageDimensions`（PNG IHDR / GIF 屏幕 / WebP VP8·VP8L·VP8X / JPEG 首个 SOF）
  与 `ImageEncoding`（`InlineImageTypes` / `ToBase64` / `Base64Length`）。`IToolExecutionApi` 新增
  `GetModel`，对应上游 `api.models.getModel`。附带修正读循环：上游允许「追加写」（`after.size >
  before.size` 时沿用已扫描的文本），端口此前一律重读并在第二次抛错。
- **telemetry 的 metrics 与 trace 渲染**：补 `metrics.ts`（`InstrumentOptions` /
  `HistogramOptions` / 三个 instrument 接口 / `ITelemetryMetrics` / `CreateTypedMetrics`，后者把
  schema 里的 description、unit、boundaries 绑到 instrument 上）与 `render.ts`
  （`TelemetryRender.RenderTrace`：按 `startedMs` 组成森林、父节点缺失视为根、每层两空格缩进，
  行尾带耗时与 error 详情，事件 `·`、链接 `→`）。`TelemetrySchemaDefinition` 新增可选
  `Metrics`；`InMemoryTelemetryContext` 新增 `GetStoredSpans()`（真实起止时间、事件偏移、本
  上下文的 trace id），让渲染有可喂的数据。

### Fixed

- **`azure` provider 的 id 仍是上游已废弃的旧名**：`Providers/AzureOpenAiResponses.cs` 写成
  `azure-openai-responses`，而上游早已把 provider 改名为 `azure`（CHANGELOG 明确要求同步
  `auth.json` / `models.json` / `settings.json` 里的 key），`azure-openai-responses` 现在只是 api 名。
  后果有三：`defaultProvider: "azure"` 找不到 provider、`models.json` 里 azure 的配置失配、
  `azure.json` 目录永远按 `Pi.Ai.ModelData.azure-openai-responses.json` 找不到。现在 id 与
  `ModelCatalog._knownProviders` 都改成 `azure`。
- **库被嵌入时 `Config.Version` 上报的是宿主版本**: 没有可读 `package.json` 时，版本回退取的是
  `Assembly.GetEntryAssembly()`——也就是**宿主程序**的程序集版本。而 `Assets/package.json` 是
  `Pack="false"`、不进 nupkg，所以「把 Pi.CodingAgent 当库装进来」的宿主**只**会走这条回退路径：
  一个 `1.0.0` 的宿主调 `Config.Version` 会拿到 `"1.0.0"`，并顺着 `BugReport` 的 version、
  `PiUserAgent` 的 UA、`ClientIdentity`、`CrashLog`、MCP client info 的 `Version`、启动横幅和
  `lastChangelogVersion` 一路扩散出去。上游 TS 是从模块自身目录读 `package.json`，`VERSION`
  永远是 pi 包的版本、与宿主无关；现在回退固定取 `typeof(Config).Assembly` 的
  `AssemblyInformationalVersion`（剥掉 `+<sha>`）。CLI 行为不变：它的 `package.json` 随包落在
  程序集旁边，本来就优先命中。回归用例
  `FallsBackToTheCodingAgentAssemblyRatherThanTheEntryAssembly` 把测试项目的 `<Version>`
  刻意设成 `0.0.1`——两者版本相同时两种实现结果一致，否则测不出这个区别。

## [1.4.2] - 2026-10-23

发 1.4.1 之后又攒到三处真实缺陷：一处让**所有带参数的 MCP 工具**不可用，一处让**自定义
provider** 的流式请求全部失败，一处会让 `pi` 整个进程崩掉。它们都在 `v1.4.1` 打 tag 之后
才修好，同样只能发新版本。

### Fixed

- **带参数的 MCP 工具全部失效（`The node already has a parent.`）**: 构造 `tools/call` 的
  `arguments` 时，`McpServerConnection` 把调用方给的 `JsonNode` 原样塞进新的 `JsonObject`。
  而 `Pi.Agent` 的 `ValidateToolArguments` 交出来的，正是**已经挂在参数对象下**的子节点——
  给已有父节点的 `JsonNode` 重新挂父会抛 `InvalidOperationException`，于是凡是需要参数的
  MCP 工具（`read_file`、`edit`、任何带 schema 的工具）一律调用失败，无参工具反而正常，
  很容易被误判成 server 端问题。现在遍历时逐个 `DeepClone()`，原参数对象保持不动。
  回归用例 `CallToolAcceptsArgumentValuesThatAlreadyHaveAParent` 断言发出的 `arguments`
  内容正确，且原子节点仍挂在原父节点上。
- **自定义 provider 流式报 `No API provider registered for api: …`**: 内置 API provider 的注册
  挂在 `CompatApi` 的静态构造函数里，只有走基类 composer 才会触发；而 `models.json` 里的自定义
  provider 直接查 API 注册表，从不经过那条路径。于是端到端测试一对真实 OpenAI 兼容端点发请求就
  抛 `No API provider registered for api: openai-completions`。现在流式回落路径会确保注册
  （幂等，不覆盖已有条目）。回归用例改为对一个不可达端点流式请求，断言失败落在连接层而不是
  注册表。
- **`Box` 子组件列表与渲染缓存缺少并发防护**（T35）：渲染定时器线程枚举 `Box.Children`
  （`Render` / `Invalidate` / `HandleMouse`）的同时，agent 事件线程增删工具卡片 / 图片子组件，
  抛 `Collection was modified; enumeration operation may not execute`，未捕获即整个 `pi`
  进程退出。缓存命中路径另有 `_cache` 先判后用被并发置 null 的 `NullReferenceException`
  竞态。`Box` 现在与 `Container` 同策略：锁内快照枚举 + 版本号守卫的命中测试布局 +
  缓存单次读取。新增确定性门控与三线程锤击两条并发回归（`ComponentTests`）。

### Changed

- 仓库迁移到 `AIDotNet/pi.net`：readme / README 徽章、文档互链、`Directory.Build.props` 的
  `PackageProjectUrl` / `RepositoryUrl` / `Description`、CodingAgent 内置 docs 索引与宣传片页脚
  全部改指新地址。旧地址 `xuzeyu91/pi.net` 由 GitHub 301 重定向兜底；本次发版起，随 nupkg
  打包的包内 README（`PackageReadmeFile`）也指向新地址。NuGet 包 ID 未变，
  `nuget.org/packages/Pi.*` 链接与安装命令无需调整。

## [1.4.1] - 2026-10-23

修掉 1.4.0 已经发出去的三处缺陷。它们在 `v1.4.0` 打 tag 之后才修好，而 nuget.org 的
`id + version` 不可覆盖，所以只能发新版本——1.4.0 的那批包仍带这些 bug，建议 unlist。

### Fixed

- **终端输入管线根本没接线（C383）**: `StdinBuffer` 此前只在单测里被实例化，生产路径只有
  一个「状态查询期间才生效」的临时聚合在兜着。于是终端对 OSC 10/11/4 配色查询的应答
  （`rgb:cccc/cccc/cccc` 之类 + 16 色调色板）被 `Console.ReadKey` 逐键送达后逐字符漏进编辑器，
  同时 `QueryTerminalColorsAsync` 超时——Windows 上的主题明暗探测实际失效。现在管线是
  `MapKey` → `StdinBuffer.Process` → `Data`/`Paste`，并新增 `ResolveEscapeTimeoutMs`
  （`PI_TUI_ESC_TIMEOUT`，SSH 下自动加宽到 100 ms）。附带两处：重定向输入分支对单个代理项调
  `char.ConvertFromUtf32` 会抛 `ArgumentOutOfRangeException`，改为按 UTF-16 码元喂缓冲；
  `Stop()` 现在会释放缓冲与其冲刷计时器。
- **备用屏退出复原漏了前导复位（C384）**: 退出复原的前缀只走基类版本，缺了同步输出复位、
  关闭鼠标上报、恢复自动换行三项。跳过 `stop()` 的退出路径（Ctrl+C 打断登录对话框、
  `Environment.Exit`）走 `AppDomain.ProcessExit` 兜底时，`\x1b[?1003h` 一直开着，
  Windows Terminal 持续把 `\x1b[<35;x;yM` 运动事件写进控制台输入，父 shell 当键入回显——
  表现为「Ctrl+C 退出后终端一直刷乱码」。已按上游覆写前缀（仅在备用屏激活时下发）。
- **OpenAI Responses 鉴权缺失（C170）**: `apiKey` 没有被注入请求头，`Authorization: Bearer`
  只在调用方自己传头时才存在，直接用 API key 调用会 401。现在 `ApiKeyHeader` 显式把它并入
  头合并，保留 `"unused"` 哨兵语义避免重复写入。
- **启动横幅与 `--version` 仍报 `v1.0.4`**: `Config.Version` 沿用 TS 语义，优先读随包分发的
  `src/Pi.CodingAgent/Assets/package.json` 的 `version`，而 1.4.0 发版只改了
  `Directory.Build.props` 的 `<Version>`（只影响程序集版本，是回落路径）。该资产已重新从上游
  `v1.4.0` 同步（含依赖版本与 `schemas` 条目），`Assets/CHANGELOG.md` 一并对齐，`/changelog`
  不再停在 1.0.4。新增构建期守卫 `ValidatePackagedVersion`，两处版本不一致直接报错。

## [1.4.0] - 2026-10-23

Version alignment with upstream Pi `1.4.0`: the port now tracks the newer stable
release line, with the user-visible fixes from upstream `1.1.0`–`1.4.0` backported
in this batch. The full inventory and the remaining backlog live in
[`docs/upstream-1.4.0-delta.md`](docs/upstream-1.4.0-delta.md).

### Added

- **Usage docs** (`docs/usage/`): per-package guides for consuming the 13 NuGet packages from C# —
  which package for which job, minimal working code, credential sources, and pitfalls. Covers
  `Pi.Ai`, `Pi.Agent`, `Pi.CodingAgent`, `Pi.Mcp`, plus one-liners for the remaining nine.
- **Runnable samples** (`samples/`): four console projects registered in `Pi.slnx`, so CI compiles
  them and the documented APIs cannot silently drift. `Pi.Samples.FauxAgent` runs the full agent
  loop with a scripted provider (no API key), `Pi.Samples.RealModel` streams from a real provider
  through both the legacy `Model` entry and the catalog `ModelSpec` path,
  `Pi.Samples.CodingSession` embeds a coding agent via `Sdk.CreateAgentSessionAsync`, and
  `Pi.Samples.McpClient` drives an MCP server over stdio or streamable HTTP.

### Fixed

- **Upstream 1.1.0–1.4.0 backport (first batch)**: eleven user-visible fixes from the newer
  stable releases, with the full inventory and the remaining backlog in
  `docs/upstream-1.4.0-delta.md`.
  - `grep` runs ripgrep with `--no-config`, so `RIPGREP_CONFIG_PATH` (e.g. `--fixed-strings`)
    can no longer silently turn regex searches into literal ones (upstream #11203).
  - `mcp.json` files that start with a UTF-8 BOM are parsed instead of skipping every server
    (upstream #11191).
  - `~` is expanded in `extensions` / `skills` / `prompts` / `themes` patterns, so `+~/x`,
    `-~/x` and `!~/skills/pdf` match (upstream #10765).
  - Empty quoted prompt-template arguments (`""`, `''`) are kept, so later positional
    arguments no longer shift and positional defaults apply (upstream #10867).
  - Print mode keeps printing the partial text but warns on stderr and exits `1` when the
    response stopped at the model's output token limit (upstream #10799).
  - `PI_CLIPBOARD_OSC52=1` forces OSC 52 copies for remote clients that leave no SSH markers,
    such as multiplexer attaches or remote IDE daemons (upstream #10826/#10835/#10114).
  - Compaction, turn-prefix and branch summaries no longer derive a fixed output cap from
    `reserveTokens`; the provider sizes them from the request, so long reasoning can no longer
    make summarization fail with "generation hit the token cap" (upstream #9512/#9075/#10908).
  - Whitespace-only tool output (a bare `\r\n` from bash) uses the empty-output placeholder on
    OpenAI Completions, OpenAI Responses and Google instead of being sent verbatim (#8720).
  - Invalid `\u` escapes such as `\usepackage` are kept as literal text, so tool-call arguments
    no longer lose the field and everything after it (upstream #10868).
  - Retry classification matches the newer upstream table: HTTP status codes only count as
    standalone numbers, and gateway/Codex-proxy transient failures (`unable to forward upstream`,
    `upstream_stream_read_error`, `stream disconnected before completion`, Vertex OAuth refresh
    failures) are retried (upstream #10920/#10965/#10996/#11057/#10842).
  - A display-math block can no longer span a GFM table, so an unclosed `$$` stops swallowing
    tables while streaming (upstream #11159).
- `readme.md` / `readme.zh-CN.md` links now use absolute GitHub URLs. Relative links were rewritten
  into self-links by nuget.org, so every doc link on the package page was dead.
- Dropped the `-m:1` recommendation from the build-from-source snippet: serial execution is slower
  and reproduces two `Pi.Ai.Tests` failures plus a multi-minute `Pi.CodingAgent.Tests` hang that a
  parallel build does not.

## [1.0.4] - 2026-10-21

Initial stable release of the .NET port. Full functional parity with the upstream
TypeScript release `1.0.4` (baseline `eb326d265`), audited end to end:

- **13 runtime projects**: `Pi.Ai`, `Pi.Agent`, `Pi.Tui`, `Pi.CodingAgent` (+ `Pi.Cli`,
  `Pi.Vacation`, `Pi.DurableHost`), `Pi.Durable`, `Pi.Chord`, `Pi.Server`, `Pi.Client`,
  `Pi.Mcp`, `Pi.Protocol`, `Pi.Env`, `Pi.Telemetry`, `Pi.Codemode`, `Pi.Evals`.
- **Public API & wire audit**: all 1,426 barrel exports of the 13 source packages mapped
  (`docs/public-api-audit.md`); OpenAI / Anthropic / MCP wire shapes locked by test suites.
- **Tests**: 30,103 items across 13 test projects, 0 failures (5 platform skips on Windows);
  container (Docker 29.7.1) and POSIX/SSH acceptance completed on external Ubuntu 24.04.
- **Radius catalog persistence**: dynamic provider refresh now writes back through the models
  store (`RadiusProvider` implements `IProvider.RefreshModels`), superseding the in-memory-only
  note from the 1.0.4 baseline.
- **Differences from TypeScript** are registered with IDs in
  `docs/coding-agent-porting-status.md` (C-series) and `docs/tui-porting-status.md`
  (T-series), with an independent review in `docs/review-inconsistencies-2026-10-21.md`.
  User-visible ones:
  - Mermaid diagrams render as plain text (no bundled renderer; `IMermaidRenderer` seam).
  - Word-wise cursor movement treats a run of CJK characters as one word (no ICU dictionary
    segmentation in .NET); ASCII behavior matches upstream.
  - Extensions cannot rewrite `tool_call` input in place (block/terminate instead).
  - Bun/Node runtime shims (`bun/*`, jiti loaders, virtual modules, lazy loaders) are
    intentionally not ported; experimental plugin bundles run on a Jint engine.
