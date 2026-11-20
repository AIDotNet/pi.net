# 上游未发布提交处置表（v1.5.2..edfe6b2ef）

范围：`v1.5.2`（`b4afdddf4`）之后触及端口已迁移 package 的 **116 个提交**（上游共 349 个提交，
其余为 `apps/p2`/`apps/p3` 原型）。逐项证据与落地细节见
[upstream-unreleased-delta.md](./upstream-unreleased-delta.md)。

| # | 提交 | 日期 | 主题 | 处置 | 证据 / 落点 |
|---|---|---|---|---|---|
| 1 | `edfe6b2ef` | 2026-11-20 | fix(ai): send Codex system prompt as leading developer item | 已移植 | src/Pi.Ai/Api/OpenAiCodexResponses.cs; AzureAndCodexResponsesTests |
| 2 | `3107a58d2` | 2026-11-19 | fix(tui): keep the SIGTSTP listener tied to terminal ownership (#12339) | 已移植 | src/Pi.Tui/Tui.cs SetSuspendSignalHandler |
| 3 | `e4f50d76d` | 2026-11-20 | fix(coding-agent): handle external SIGTSTP like Ctrl+Z (#12283) | 已移植 | src/Pi.Tui/Tui.cs + Pi.CodingAgent/Modes/Interactive |
| 4 | `7c05885bc` | 2026-11-19 | fix(coding-agent): flush stdout before exiting after stopping a TUI (#12336) | ➖ 不适用 | .NET Console.Out AutoFlush=true |
| 5 | `5799e217e` | 2026-11-19 | fix(coding-agent): send images to llama.cpp decision classifiers (#12335) | 已移植 | src/Pi.CodingAgent/Extensions/Llama/LlamaProvider.cs |
| 6 | `5bcf370a5` | 2026-11-19 | fix(coding-agent,tui): cursor keys in selector search keep the selection (#12… | 已移植 | Pi.Tui SettingsList + CodingAgent selectors |
| 7 | `37b74c546` | 2026-11-20 | fix(coding-agent): /login and /logout search selects the best match (#12246) | 已移植 | Components/OAuthSelector.cs FilterProviders |
| 8 | `c815a3dfe` | 2026-11-19 | feat(ai): send strict tools to Claude on GitHub Copilot (#12332) | 已移植 | ModelData/github-copilot.json anthropic-messages |
| 9 | `9a7cfba47` | 2026-11-20 | fix(tui): Enter on a Tab menu for an absolute path no longer submits (#12312) | 已移植 | Pi.Tui/Components/Editor.cs IsInSlashCommandNameContext |
| 10 | `e5c01ca6c` | 2026-11-19 | fix(ai): send strict on GitHub Copilot Responses tools (#12329) | 已移植 | ModelData/github-copilot.json openai-responses |
| 11 | `1e34c3b4c` | 2026-11-19 | fix(ai): add qwen4-max-preview to qwen-token-plan-individual models (#12313) | 已移植 | ModelData/qwen-token-plan*.json + ModelCatalogTests |
| 12 | `63dec2625` | 2026-11-18 | feat(coding-agent): print resume command on its own line on exit | 已移植 | InteractiveMode.Lifecycle.cs resume 命令行 |
| 13 | `cead615b4` | 2026-11-17 | fix(ai): use Codex's context windows for GPT-6.2 (500K Sol, 272K Astra) | 已移植 | ModelData/openai-codex.json 500K/272K |
| 14 | `e56d35ce5` | 2026-11-17 | feat(ai,coding-agent): add GPT-6.2 Astra and GPT-6.2 Sol | 已移植 | ModelData/openai|azure|openai-codex.json + ModelResolver |
| 15 | `5f89cc0a7` | 2026-11-17 | feat(p3): slice 1 redo and slice 26, the release build and the p3 command | 范围外·p3 | apps/p3 原型，端口未移植 |
| 16 | `b513b64d8` | 2026-11-16 | fix(coding-agent): capture git and npm output while the interactive TUI runs … | 已移植 | Core/PackageManager.cs GIT_TERMINAL_PROMPT=0 |
| 17 | `b7bb7ca90` | 2026-11-16 | fix(tui,coding-agent): make fullscreen scroll frames O(viewport) (#11968) | ➖ 纯性能 | 端口无 view-only 帧/Container 行复用 |
| 18 | `18e99051b` | 2026-11-16 | fix(coding-agent): report pi.events handler errors like other extension error… | 已移植 | ExtensionLoaderImpl EventsBus + ReportError |
| 19 | `c8c456c85` | 2026-11-16 | fix(coding-agent): report model fallback after /resume and in every mode (#12… | 已移植 | Cli/Main.cs 模型回退诊断 |
| 20 | `bb1a2b354` | 2026-11-16 | fix(ai): recover from the Codex websocket 60-minute connection limit (#12229) | 已移植 | Pi.Ai/Utils/Retry.cs + WebSocket 45min |
| 21 | `e03dca7a3` | 2026-11-16 | fix(coding-agent): emit session_start on reload after bindExtensions({}) (#12… | 已移植 | AgentSessionBuild.cs _extensionsBound |
| 22 | `6aab551a7` | 2026-11-16 | fix(coding-agent): stream session_start events in JSON print mode (#12232) | 已移植 | Modes/PrintMode.cs 先订阅后绑定 |
| 23 | `beffcde92` | 2026-11-16 | fix(coding-agent): emit session_start once on RPC session replacement (#12230) | 已移植 | Modes/Rpc/RpcMode.cs 去重复 rebind |
| 24 | `c47ad0432` | 2026-11-16 | fix(coding-agent): report diagnostics that appear on session replacement (#12… | 已移植 | AgentSessionRuntime.TakeUnreportedDiagnostics |
| 25 | `ac78a6068` | 2026-11-16 | fix(coding-agent): remove two remaining direct terminal writes (#12117) | 已移植 | Utils/Changelog.cs 不写终端 |
| 26 | `efbcd5adb` | 2026-11-16 | fix(coding-agent): update undici to 8.12.4 for security advisories | ➖ 不适用 | undici 依赖；端口用 HttpClient |
| 27 | `2145e3f60` | 2026-11-16 | docs(coding-agent): regenerate models configuration reference | 文档 | 模型配置参考再生成 |
| 28 | `2c2a77f9f` | 2026-11-16 | fix(ai): send Mistral tools non-strict unless compat.supportsStrictMode is set | 已移植 | MistralConversations.cs strict opt-in |
| 29 | `f6600b58f` | 2026-11-15 | fix(chord): a watched derive changed twice in one commit publishes one frame | 未移植·chord 收窄 | 端口 Pi.Chord 无对象运行时/watch 帧 |
| 30 | `36c709e72` | 2026-11-15 | fix(chord): a framed source set several times in one commit publishes one fra… | 未移植·chord 收窄 | 同上 |
| 31 | `f57cd9484` | 2026-11-15 | fix(chord,p2): a worker's exit is published only after its link closed and it… | 未移植·chord 收窄 | p2 原型 + chord worker 链路 |
| 32 | `e67aa1a4e` | 2026-11-15 | feat(durable): onAbort "keep" keeps a queued input through conversation aborts | 已移植 | Pi.Durable Inbox/HarnessTypes OnAbort |
| 33 | `684fddee0` | 2026-11-15 | Revert "fix(p2): Stop withdraws the queue in the abort's commit; conversation… | 范围外·p2 | p2 回滚对，净变化为零 |
| 34 | `f8a292842` | 2026-11-15 | fix(p2): Stop withdraws the queue in the abort's commit; conversations stop k… | 范围外·p2 | p2 Stop/队列，端口未移植 p2 |
| 35 | `8270d9af6` | 2026-11-15 | feat(chord): admission with a typed value, route admission, host-data delete … | 未移植·chord 收窄 | admission/host-data 依赖 p2/chord 宿主 |
| 36 | `7f021f0ee` | 2026-11-15 | fix(chord): consumer event tests wait for the async inflate's delivery, not f… | 测试 only | chord 测试等待语义 |
| 37 | `0a25dd15e` | 2026-11-15 | fix(ai): Together Kimi K3 test expects the upstream 1M output limit | 已移植 | ModelData/together.json Kimi K3 1M |
| 38 | `0db9861ec` | 2026-11-15 | feat(p3): slice 1, base and sessions; chord app kit exports its start-data fa… | 范围外·p3 | apps/p3 |
| 39 | `bb396449c` | 2026-11-15 | Merge branch 'main' of https://github.com/earendil-works/pi | 合并提交 | 无独立变更 |
| 40 | `92e1db6ca` | 2026-11-15 | feat(chord): named live queries, column sets, exists and joins, lists (slice … | 未移植·chord 收窄 | named live queries/column sets |
| 41 | `34fb59f67` | 2026-11-14 | feat(chord): part limits for client and page links | 未移植·chord 收窄 | client/page link part limits |
| 42 | `f6df849c4` | 2026-11-14 | Merge branch 'main' of https://github.com/earendil-works/pi | 合并提交 | 无独立变更 |
| 43 | `12d7d5ca9` | 2026-11-14 | docs(p3): persons, guests and links; admission hands the factory its data; sl… | 文档·p3 | p3 设计稿 |
| 44 | `76c79d8bb` | 2026-11-14 | fix(p2): fit images to the model's limits before they enter the transcript | 范围外·p2 | p2 图片缩放 |
| 45 | `356798725` | 2026-11-14 | feat(p2): MCP servers per conversation location, with pi's server summaries | 范围外·p2 | p2 MCP 位置 |
| 46 | `a309480a7` | 2026-11-14 | docs(chord): admission before the factory (design, proposed) | 文档·chord | admission 设计稿 |
| 47 | `827383a15` | 2026-11-13 | docs(chord): design.md current with the connection loop, sign-in lifetimes, t… | 文档·chord | design.md 更新 |
| 48 | `86918f9ee` | 2026-11-13 | fix(ai): send reasoning_effort and read reasoning tokens for GitHub Copilot (… | 已移植 | github-copilot.json + OpenAiCompletions usage |
| 49 | `21e4cb80d` | 2026-11-13 | fix(chord,p2): reconnect at once after a wake; show Offline only after 2 s | 范围外·p2/chord | 重连时机依赖 p2 UI |
| 50 | `4438c58ba` | 2026-11-13 | fix(agent): answer every tool call when a batch is aborted | 已移植 | Pi.Agent/ToolExecution.cs + AgentLoopTests |
| 51 | `ff7414d51` | 2026-11-13 | feat(chord): the app kit's sign-ins (was Sessions) | 未移植·chord 收窄 | app kit sign-ins |
| 52 | `5b5be2c31` | 2026-11-13 | fix(coding-agent): don't crash when rendering very large self-rendered tool r… | ➖ 不适用 | JS push 展开上限；C# AddRange |
| 53 | `4be4cb4f5` | 2026-11-13 | fix(coding-agent,durable): keep read's non-vision image warning out of what t… | 已移植 | ReadTool + ReadRenderers + durable ReadTool |
| 54 | `4699b5b26` | 2026-11-13 | fix(ai): retry provider errors that report only a Node socket error code (#12… | 已移植 | Retry.cs socket codes |
| 55 | `ede360e4d` | 2026-11-13 | fix(coding-agent): /copy skips turns that ended without an answer (#12121) | 已移植 | AgentSession.GetLastAssistantText |
| 56 | `90ed3e3b1` | 2026-11-13 | fix(chord): shared holds review fixes | 未移植·chord 收窄 | shared holds review 修复 |
| 57 | `47e9d74f9` | 2026-11-13 | feat(chord): remote references of one host share a hold | 未移植·chord 收窄 | shared holds |
| 58 | `282b01c80` | 2026-11-13 | fix(coding-agent): anchor find path patterns at the search path (#12116) | 已移植 | FindPathPattern.cs + FindTool.cs |
| 59 | `bacd45b9c` | 2026-11-13 | fix(coding-agent): report model scope warnings as startup diagnostics (#11973) | 已移植 | Cli/Main.cs scope 诊断 |
| 60 | `139707002` | 2026-11-12 | fix(ai): move Qwen Token Plan to current gateways and model lists (#12112) | 已移植 | QwenTokenPlan*.cs + ModelData |
| 61 | `e675a0d7f` | 2026-11-12 | docs(ai,coding-agent): explain that released clients receive new model catalo… | 文档 | 模型目录发布说明 |
| 62 | `f7734109c` | 2026-11-12 | docs(chord): shared holds, sixth draft: review fixes (relay abort order, susp… | 文档·chord | shared holds 设计稿 |
| 63 | `a635f3901` | 2026-11-12 | fix(coding-agent): keep /resume selection while the session list reloads prog… | 已移植 | SessionSelector._pendingSelectedPath |
| 64 | `0b48a1d18` | 2026-11-12 | docs(chord): shared holds, fifth draft: host-side sharing, no round trip (own… | 文档·chord | shared holds 设计稿 |
| 65 | `787005a40` | 2026-11-12 | fix(tui): hide terminal images covered by overlays (#12109) | 已移植 | TerminalImage/Tui HideImagesUnderOverlay |
| 66 | `ec1ffa15c` | 2026-11-12 | docs(chord): shared streams: joined is a control message, joins never wait fo… | 文档·chord | shared streams 设计稿 |
| 67 | `1b3223b96` | 2026-11-12 | docs(chord): shared holds, fourth draft: one request per reference, shared st… | 文档·chord | shared holds 设计稿 |
| 68 | `7a1c1f61e` | 2026-11-12 | docs(chord): shared holds, third draft: review fixes, no surface change (even… | 文档·chord | shared holds 设计稿 |
| 69 | `22f456d73` | 2026-11-12 | fix(ai): Google custom fetch follow-ups (#12108) | 测试 only | 删除失效 fetch 断言；CONTINUATION 已并入 f72f |
| 70 | `f72f799a4` | 2026-11-12 | fix(ai): pass custom fetch to the Google GenAI SDK adapters (#12103) | 已移植 | Google HttpClient 注入 + CONTINUATION 映射 |
| 71 | `20a6e09dc` | 2026-11-12 | docs(chord): shared holds key includes the computed address (Sol) | 文档·chord | shared holds key 设计 |
| 72 | `502f79e3e` | 2026-11-12 | docs(chord): references sharing one wire hold, design for review | 文档·chord | shared holds 设计稿 |
| 73 | `7ae9ff8c7` | 2026-11-12 | feat(chord): telemetry-ingest subpath export, and admit(principal) on the ing… | 未移植·chord 收窄 | telemetry-ingest 子路径 + ingest principal |
| 74 | `53af02378` | 2026-11-12 | feat(chord): base bundles resolve through a given tsconfig's paths, as a serv… | 未移植·chord 收窄 | base bundle tsconfig paths |
| 75 | `aa47cedce` | 2026-11-12 | fix(ai): keep root additionalProperties in non-strict Anthropic tool schemas … | 已移植 | AnthropicMessages.cs additionalProperties |
| 76 | `3c771bd18` | 2026-11-12 | fix(chord): a client starts without its server and applies the set shown when… | 未移植·chord 收窄 | client/follow 集合 |
| 77 | `da44c4df3` | 2026-11-12 | fix(chord): base bundles collect the fonts and images their stylesheets name | 未移植·chord 收窄 | base bundle 资源收集 |
| 78 | `22fe46c7b` | 2026-11-12 | feat(chord): surfaces for an app with its own HTTP stack and styling, and sta… | 未移植·chord 收窄 | surfaces/static pages |
| 79 | `a83b75a07` | 2026-11-12 | fix(coding-agent): report signal-terminated pi.exec commands as failed (#1208… | 已移植 | Core/Exec.cs ?? 1 |
| 80 | `9b88b94e2` | 2026-11-12 | fix(ai): retry provider errors reading "Connection closed." (#12093) | 已移植 | Retry.cs connection.?closed |
| 81 | `935f0ac0c` | 2026-11-12 | fix(coding-agent): keep bash output when the tail is one over-long line (#120… | 已移植 | OutputAccumulator Snapshot 回退 tail |
| 82 | `406e719a6` | 2026-11-12 | fix(ai): fall back from strict tool schemas with untyped nodes | 已移植 | ConstrainedSampling.cs untyped 回退 |
| 83 | `5e24c6b1f` | 2026-11-12 | feat(durable)!: code-mode namespaces declared by extensions | ➖ 不适用 | 端口无 durable/src/codemode |
| 84 | `822b37a27` | 2026-11-12 | fix(p2,chord): reload_plugins reports deleted plugins as removed; the global … | 范围外·p2/chord | reload_plugins/AGENTS.md 的 p2 侧 |
| 85 | `8d30e6306` | 2026-11-11 | feat(chord): events (fire-and-forget members) and call windows by trust | 未移植·chord 收窄 | events/call windows |
| 86 | `6268cc5cd` | 2026-11-11 | feat(p2): backup coordinator (#12042) | 范围外·p2 | backup coordinator |
| 87 | `628d8ab52` | 2026-11-11 | fix(durable): find the conversations above a changed task without walking its… | 已移植 | Scheduler.AddScopesAbove 已按对话停止 |
| 88 | `b48c675a9` | 2026-11-11 | fix(env): keep reading output after exit while the link holds the reader up | ➖ 不适用 | Rust daemon 源码，端口用原生产物 |
| 89 | `7a4fb4cd4` | 2026-11-11 | docs(chord): the kit serves the reload control (AppChanges) | 文档·chord | kit reload 控制 |
| 90 | `2936f3c8e` | 2026-11-11 | docs(coding-agent,durable): move changelog entries that landed in released se… | 文档 | changelog 归位 |
| 91 | `75ca6e7d0` | 2026-11-11 | fix(durable,env): skip entries removed during listDir enumeration (#12055) | 已移植 | LocalExecutionEnv + RemoteEnv listDir |
| 92 | `3345c2925` | 2026-11-11 | fix(coding-agent): keep restored MCP tools when an extension hides only its o… | 已移植 | AgentSessionBuild _restoredToolNames |
| 93 | `926b2f8cd` | 2026-11-11 | fix(chord): use ranges for optional peer dependencies (#12053) | ➖ 不适用 | chord package.json peer 范围；端口无 npm 包元数据 |
| 94 | `0e1e3fc81` | 2026-11-11 | fix(coding-agent): convert OSC 8 hyperlinks in HTML export tool output (#1205… | 已移植 | AnsiToHtml OSC8 + ToolHtmlRenderer |
| 95 | `e72551e19` | 2026-11-11 | fix(coding-agent): verify managed updates with the Node.js running pi (#12051) | ➖ 不适用 | 端口无 npm 托管更新 |
| 96 | `be2744d95` | 2026-11-11 | fix(tui): keep Kitty images across fullscreen full redraws (#12047) | 已移植 | TuiAltScreen 全屏重绘跳过 ED2 |
| 97 | `b033ade00` | 2026-11-11 | docs(chord): events spec, third draft after delta reviews | 文档·chord | events 设计稿 |
| 98 | `df9114563` | 2026-11-11 | docs(chord): events spec, second draft after two reviews | 文档·chord | events 设计稿 |
| 99 | `edbdf8d1c` | 2026-11-11 | docs(chord): events spec draft (fire-and-forget members, call windows by trus… | 文档·chord | events 设计稿 |
| 100 | `4cfea9786` | 2026-11-11 | Fix standalone binary not resolving extension dependencies via package.json m… | ➖ 不适用 | 单文件 ESM 解析；.NET 程序集加载 |
| 101 | `5b4d7d3df` | 2026-11-11 | fix(tui): keep hard breaks after inline Markdown images as separate rows (#12… | 已移植 | Components/Markdown.cs 图像行拆分 |
| 102 | `bb3e7a05e` | 2026-11-11 | docs(chord): call windows only for untrusted links, default 10,000 | 文档·chord | call windows 设计 |
| 103 | `bbfc6c7ce` | 2026-11-11 | fix(coding-agent): ignore a leading ./ in find patterns (#12044) | 已移植 | FindPathPattern 去前导 ./ |
| 104 | `d9d535b6c` | 2026-11-11 | fix(durable): mark wait promises handled before they reach the caller (#12041) | ➖ 不适用 | Node Promise rejection；.NET Task 无对应 |
| 105 | `74dcca7d4` | 2026-11-11 | use llama serve in the NVIDIA RTX guide (#12004) | 文档 | NVIDIA 指南 |
| 106 | `b3c2e4a6b` | 2026-11-11 | fix llama.cpp default port (#12001) | ➖ 不适用 | llama 示例端口；端口无默认端点 |
| 107 | `49fe0a237` | 2026-11-11 | fix(ai): default Anthropic message threads by endpoint, not provider id | 已移植 | AnthropicMessageThreads.IsEnabledByDefault |
| 108 | `473515f9c` | 2026-11-11 | feat(durable): suspendable work: signals, tools that park, gates, hook tasks | 已移植 | `Signals.cs`、`TaskState.Signals`、`Scheduler.SignalAsync`、`ITaskRuntime.ReadSignalAsync`；`ToolExecutionResult.Park`/`ToolHookDecision.Park`/`ToolTaskCheckpoint.Park`（sleep/wait/gate）；`IAfterToolsTaskHooks`/`IOnYieldTaskHooks` + `Generation.cs` 的 hook task 子任务 |
| 109 | `ef9361ab7` | 2026-11-11 | fix(coding-agent): do not crash when an aborted signal destroys an unwatched … | ➖ 不适用 | Node 文件流 abort；端口用 FileStream+CancellationToken |
| 110 | `91d9719b4` | 2026-11-10 | fix(chord): skip the git-dependency install tests on npm 12 | 测试 only | chord npm12 测试跳过 |
| 111 | `f2bd84a51` | 2026-11-10 | fix(chord): tutorial waits for the base code to be watched before adding a pl… | 文档/测试·chord | tutorial 等待条件 |
| 112 | `75d59639a` | 2026-11-10 | fix(chord): plugin installs read npm 12's pack output | ➖ 不适用 | npm 12 pack 输出；端口无 npm 安装器 |
| 113 | `97096b4cb` | 2026-11-10 | fix(env): tests build the daemon they run | ➖ 不适用 | Rust daemon 测试构建 |
| 114 | `65c43bb52` | 2026-11-10 | fix(chord): lock tests make socket files under /tmp | 测试 only | chord 锁测试 socket 路径 |
| 115 | `64c96d00d` | 2026-11-10 | fix(durable): clean up provider resources when a harness closes (#11961) | ➖ 不适用 | Node provider WebSocket 资源；端口无全局资源表 |
| 116 | `c8390defa` | 2026-11-10 | Add [Unreleased] section for next cycle | 文档 | 新增 Unreleased 段 |

## Chord v2 提交的代码级证据

端口 Pi.Chord 收窄为 delta/tracker 内核（54 个 .cs），没有上游的对象运行时、跨进程链路与 app kit。
以下逐个复核每个 chord v2 提交依赖的符号在端口不存在；命令：
grep -rIl <符号> src/Pi.Chord --include=*.cs | wc -l。

| 提交 | 上游依赖的模块/符号 | 端口复核 |
|---|---|---|
| `f6600b58f` | state/derive.ts 的 Derive、#framed | Derive=0 个文件；Framed=0 |
| `36c709e72` | framed Source 的批内合并 | Framed=0；端口无 derive/source 图 |
| `f57cd9484` | worker link 的 exit 发布 | WorkerLink/Worker=0 |
| `8270d9af6` | admission / host-data / 插件路由 | Admission=0；HostData=0 |
| `92e1db6ca` | named live queries / column sets / exists / joins / lists | LiveQuery=0；ColumnSet=0 |
| `34fb59f67` | client/page link part limits | PartLimit=0；端口无 link 类型 |
| `ff7414d51` | app kit 的 sign-ins/sessions | AppKit=0 |
| `90ed3e3b1` | shared holds 审查修复 | SharedHold=0 |
| `47e9d74f9` | remote references 共享 hold | SharedHold=0 |
| `7ae9ff8c7` | telemetry-ingest 子路径 + ingest facet | TelemetryIngest=0 |
| `53af02378` | base bundle 按 tsconfig paths 解析 | Surface=0；端口 Bundler 只有 facet 打包 |
| `3c771bd18` | client 无 server 启动 / follow 集合 | FollowSet=0；端口无 chord client |
| `da44c4df3` | base bundle 收集字体/图片 | Font=0；Surface=0 |
| `22fe46c7b` | surfaces（自有 HTTP 栈/样式、静态页） | Surface=0；StaticPage=0 |
| `8d30e6306` | events（fire-and-forget members）/ call windows | CallWindow=0；无 events 成员类型 |
| `5e24c6b1f` | durable src/codemode 命名空间 | 端口无 Pi.Durable/Codemode，docs 已登记不采纳 |

## 统计

- ✅ 已移植：**50**
- ➖ 不适用（端口形态/宿主不存在）：**15**
- 未移植·chord 收窄：**15**（依赖端口未移植的对象运行时/跨进程链路，见 delta §4.2）

- 范围外·p2/p3 原型：**9**
- 文档：**21**；测试 only：**4**；合并提交：**2**

## 证据覆盖自检

- 表内 116 行覆盖 `git log v1.5.2..HEAD -- packages/{agent,ai,chord,codemode,coding-agent,durable,env,evals,mcp,schema,telemetry,tui}` 的全部提交；每条可用 `git show <hash>` 复核。
- 49 个「已移植」行都给出端口文件或测试名；对应文件均存在于当前工作树。
- 「不适用」「chord 收窄」「新子系统」行均给出端口侧缺失面的具体实证（文件/子系统名），不是按关键词猜测。
