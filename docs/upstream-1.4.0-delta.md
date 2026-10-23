# 上游版本核查与 1.4.0 变更移植评估

> 核查日期：2026-10-22 · 参照仓库：`D:\AI\参考项目\pi` · 端口基线：上游 `v1.0.4`（`eb326d265`）

## 1. 版本核查结论

上游存在 4 个高于 `1.0.4` 的稳定版本，**最新稳定版为 `v1.4.0`**：

| 标签 | 发布时间（CEST） | 说明 |
| --- | --- | --- |
| `v1.0.4` | 2026-10-05 21:22 | 端口当前基线 |
| `v1.1.0` | 2026-10-07 23:33 | 稳定 |
| `v1.2.0` | 2026-10-12 00:46 | 稳定 |
| `v1.3.0` | 2026-10-15 20:57 | 稳定 |
| `v1.4.0` | 2026-10-22 00:29 | **最新稳定版** |

`main` 在 `v1.4.0` 之后还有 **11 个未发布提交**（HEAD `476a303c1`）：`58c0b02ce` 开 `[Unreleased]` 段、
`7c22a93d0` / `cae8d69e5`（用量成本的 `totalFromProvider`、OpenRouter 上报总价）、
`5884ee681`（`/ir` 从 URL 导入会话）、`2685de7a1` / `db774055e`（依赖安全更新、预览包）、
`3bc6eebac`（Anthropic 系统消息跨被丢弃的助手消息保留）、`92ab53602`（durable `commit` 内采纳回调、
watch 类型入 chord、显式退休）、`b9fb2b854` 文档、`ac80dd88f` 贡献者名单、
`476a303c1`（托管 npm ci 去掉 `min-release-age`）。这些属于下一轮未定稿内容，**本次不作为移植目标**。
其中 `92ab53602` 与 durable 的日常行为强相关，已在第三批 / 第二十批一并落地——端口的这一处
**领先** `v1.4.0`；其余列为观察项，等上游发版后再评估。

区间规模（`v1.0.4..v1.4.0`）：**278 提交 / 76 个 feat / 156 个 fix / 1005 个文件 / +80,853 −44,109 行**。
按包分布：coding-agent 294 文件、durable 171、ai 142、chord 118、tui 35、codemode 32、
server 29、env 24、client 19、telemetry 18、protocol 17、mcp 15、agent 9。

## 2. 重要发现：端口基线已漂移

端口名义基线是 `v1.0.4`，但代码中已存在若干 **1.1.0 / 1.4.0 才引入**的特性，
说明此前的移植参照过晚于 `v1.0.4` 的提交：

| 特性 | 上游引入 | 端口状态 | 证据 |
| --- | --- | --- | --- |
| `durationMs`（AssistantMessage / ToolResultMessage / `tool_execution_end`） | 1.1.0 (#10549) | 已存在 | `Pi.Ai/Types/Messages.cs`、`Pi.Agent/Types/AgentTypes.cs` |
| `outputPad`（工具渲染上下文 + 设置） | 1.1.0 (#10557) | 已存在 | `ToolDefinition.cs`、`ExtensionEvents.cs`、`Settings.cs` |
| `--tools` 的 `+name`/`-name` 增量语义 | 1.1.0 | 已存在 | `AgentSession.cs`、`AgentSessionBuild.cs` |
| 输出预算按 3.5 字符/token 估算 | 1.1.0 (#10497) | 已存在 | `Pi.Ai/Utils/Estimate.cs`（`CharsPerToken = 3.5`） |
| OSC 52 兜底（SSH/MOSH 探测） | 1.0.4 起有 | 已存在 | `Utils/Clipboard.cs` |

结论：本次**不重复移植**上述项，只补充它们缺少的增量（例如 OSC 52 的 `PI_CLIPBOARD_OSC52` 强制开关）。

## 3. 变更清单与影响评估（按包）

标注：**已移植**＝端口此前已有；**本次移植**＝本轮落地；**待办**＝适用但本轮未做；
**不适用**＝与 C# 端口形态无关（Bun/Node 运行时、发布流水线等）。

### 3.1 `packages/ai`

| 变更 | 版本 | 影响 | 结论 |
| --- | --- | --- | --- |
| 工具结果空白输出按空处理 | 1.4.0 #8720 | 三家 provider 的 400 风险 | 本次移植 |
| 非法 `\u` 转义保留为字面量 | 1.3.0 #10868 | 工具参数丢字段 | 本次移植 |
| 重试判定的状态码需独立出现 | 1.4.0 #11057 | 请求 id/字节数误触发重试 | 本次移植 |
| 新增瞬时故障文案（`unable to forward upstream`、`upstream_stream_read_error`、`stream disconnected before completion` 等） | 1.3.0–1.4.0 | 网关故障不再中断回合 | 本次移植 |
| ChatGPT Business 工作区限额不可重试 | 1.4.0 | 避免无意义重试 | 本次移植 |
| 畸形最终参数上报 `argumentsError`（不再保留解析前缀） | 1.3.0 #10897 | 5 个 API 实现 + 校验层 + agent 预检 | 本次移植（第二批） |
| `personalizeOpenRouterCatalog()`、`RefreshModelsContext.baseUrl`、按 key 的模型列表 | 1.2.0 #10353 | 新增 `openrouter-api.ts`，重写远端目录 provider | 本次移植（第六批） |
| 客户端身份 `identity` / `User-Agent` 格式重构 | 1.4.0 | 破坏性：`telemetryContext`→`telemetry`、`getPiUserAgent` 移除 | 本次移植（第五批） |
| `resolveTranscriptTools` 由布尔改为 `TranscriptToolSupport`（含 #11142 前缀缓存） | 1.4.0 `caa6967b6` | 破坏性 API：`anchorsAdditions` → `foldedSystemMessages` | 本次移植（第十七批） |
| System One 分类器图像输入、`compat`、llama.cpp 决策模型 | 1.1.0–1.4.0 | codemode + 分类器链路 | 本次移植（第二十二批；llama.cpp 决策模型此前已落地） |
| 模型目录更新（Claude Haiku 5.5、MiniMax-M3.1-Flash、Grok `xhigh` 等） | 1.1.0–1.4.0 | 纯数据 | 不适用（数据不在上游仓库，见 §6.6） |
| Anthropic `strict:"prefer"` 重试、Mistral 超时范围 | 1.2.0 #10409 / #10609 | provider 兼容细节 | 本次移植（第十三批） |
| OpenAI Responses `additional_tools` 延迟工具输入项 | 1.0.5 `e47b8e37a` #7709 | 缓存命中率与工具延迟加载 | 本次移植（第十六批） |

### 3.2 `packages/agent`

| 变更 | 版本 | 影响 | 结论 |
| --- | --- | --- | --- |
| `AgentOptions.identity` / `Agent.identity` | 1.4.0 | 需 pi-ai `ClientIdentity` 先落地 | 本次移植（第五批，含 `DEFAULT_AGENT_IDENTITY`） |
| `durationMs` 全链路 | 1.1.0 | — | 已移植 |
| `prepareToolCall` 内接入 `validateToolArguments`（顺带把 `argumentsError` 短路接上） | 1.3.0 #10897 | 工具拿到 coerce 后参数；畸形/不合 schema 的调用以 isError 结果回模型 | 本次移植（第二批） |

### 3.3 `packages/coding-agent`

| 变更 | 版本 | 影响 | 结论 |
| --- | --- | --- | --- |
| grep 工具加 `--no-config` | 1.4.0 #11203 | `RIPGREP_CONFIG_PATH` 静默改变搜索语义 | 本次移植 |
| `mcp.json` 接受 UTF-8 BOM | 1.4.0 #11191 | BOM 导致整份配置被跳过 | 本次移植 |
| 资源设置里的 `~` 展开 | 1.2.0 #10765 | `+~/x`、`!~/skills/pdf` 永不匹配 | 本次移植 |
| 提示模板保留空引号参数 | 1.3.0 #10867 | 位置参数错位、默认值失效 | 本次移植 |
| 打印模式截断时告警并退出 1 | 1.2.0 #10799 | 脚本把截断当成功 | 本次移植 |
| `PI_CLIPBOARD_OSC52=1` 强制 OSC 52 | 1.2.0 #10826 | 多路复用器/远程 IDE 复制失效 | 本次移植 |
| 摘要不再按 `reserveTokens` 折算输出上限 | 1.4.0 #9512 | 思考占满预算时压缩直接失败 | 本次移植 |
| `sessionShareDestination` 设置 | 1.3.0 #10870 | 新增设置项 + `/share` 分支（14 文件） | 本次移植（第七批） |
| 恢复会话时恢复活动工具集 | 1.3.0 #10785 | 影响 `--tools` 与 resume 交互 | 本次移植（第八批） |
| `--model` 的 provider 选择策略统一 | 1.3.0 #10160 | 663 行重写，风险较高；连带 `findInitialModel` 不再解析 CLI（端口差异 C78 退休）、`defaultModelPerProvider` 5 项同步 | 本次移植（第二十一批） |
| codemode worker 排队与 `PI_CODEMODE_MAX_WORKERS` | 1.4.0 #11184 | 并发脚本从失败改为等待 | 不适用（见 §6） |
| `reachesTools`、命名空间 `aliases`（codemode 与 MCP 解耦） | 1.2.0 `24382bedd` | 工具可达性与 MCP 等待，破坏性 API：删除 `isCodemodeTool` / `isToolSearchTool` / `mcpStructuredContentSchema` | 本次移植（第十八批） |
| `before_agent_start` 覆盖自定义触发轮次（破坏性） | 1.2.0 `a447ba11f` / #10739 | 事件语义变更：`BeforeAgentStartEvent` 按 `messageType` 分成 user / custom 两支 | 本次移植（第十九批） |
| `serializeConversation` 省略 thinking | 1.2.0 #15e7bdc14 | 摘要请求体积 | 本次移植（第十二批） |
| 续传/截断工具调用导致的孤儿结果修复 | 1.4.0 #11227 | 会话后续请求被 provider 拒绝 | 本次移植（第十批） |
| `agent_settled` 增加 `aborted` | 1.1.0 #10607 | 事件字段 | 本次移植（第十一批） |
| 程序状态上报（OSC 7501） | 1.1.0 #10607 | 终端集成 | 本次移植（第二十五批，见 §6.f） |
| 嵌套工作树里符号链接的 `AGENTS.md` 一份都不加载（#10681） | 1.1.0 `f1b2e77f5` | 项目上下文丢失 | 本次移植（第三十六批，见 §6.af） |
| Windows shell 探测补充（Store 别名、非 `C:\Windows`） | 1.1.0–1.4.0 #9501/#9504 | 端口已有同类逻辑，需逐条比对 | 本次移植（第十四批，Store 别名部分不适用） |
| 移除 `@earendil-works/chord` 依赖、移除实验性 server/client/plugin 栈 | 1.2.0 | 上游删除，端口保留自有实现 | 不适用 |

### 3.4 `packages/tui`

| 变更 | 版本 | 影响 | 结论 |
| --- | --- | --- | --- |
| 未闭合 `$$` 不再吞掉 Markdown 表格 | 1.4.0 #11159 | 流式输出时表格消失 | 本次移植 |
| CJK 标点相邻的粗体/斜体渲染（`marked-cjk-friendly`） | 1.4.0 #10730 | 中文排版可见缺陷 | 本次移植（第二十三批，见 §6.h） |
| Rex 终端支持（OSC 8 / truecolor / 内联图） | 1.4.0 | 终端能力探测 | 本次移植（第二十四批，见 §6.g） |
| 单段 fd 查询未转义正则元字符（#11212） | 1.4.0 | `@pages/[` 这类补全查不到 | 本次移植（第二十七批，见 §6.d） |
| 进程直接退出时未复原终端模式（#11170） | 1.4.0 | 退出后终端留在鼠标/粘贴模式 | 本次移植（第二十七批，见 §6.d） |
| 聚焦 overlay 时未处理的滚轮应滚动转写（#10769） | 1.4.0 | 滚轮被塞给 overlay 的 `handleInput` | 本次移植（第二十七批，见 §6.d） |
| 延迟到达的终端回复被当成按键（#10256 / #10657） | 1.4.0 | OSC 的 BEL 变成 Ctrl+G | 本次移植（第二十七批，见 §6.d） |
| 发布配置文件 JSON Schema（settings / models / theme / keybindings） | 1.3.0–1.4.0 `7f9e1198f` / `96abe9a5a` | 编辑器校验与补全；内置主题的 `$schema` 指向发布地址 | 本次移植（第二十八批，见 §6.c） |
| Kitty / modifyOtherKeys 的移位符号键位归一化（`+` = Shift+`=`） | 1.4.0 `7f9e1198f` | `ctrl++` 这类绑定匹配不上 | 本次移植（第二十八批，见 §6.c） |
| 并发扩展对话框应堆叠而非互相替换（#11166 / #9909） | 1.4.0 `38847563f` | 第二个对话框顶掉第一个且其 Promise 永不 settle | 本次移植（第二十九批，见 §6.b） |
| `ctx.ui.custom({overlay:true})` 的 `done()` 关掉的是栈顶 overlay（#10648） | 1.3.0 `a4f67eb65` | 下层 overlay 结束时抹掉上层，两边 Promise 都没人收 | 本次移植（第二十九批，见 §6.b） |
| `showHardwareCursor` 语义变化 | 1.2.0 | 光标绘制 | 本次移植（第二十六批，见 §6.e） |

### 3.5 其他包

- **durable（1.3.0–1.4.0，171 文件）**：`Storage.commit(writes, adopt, context)` 的 `adopt(seq)`
  回调、`mintId()` 契约收窄 —— **已移植（第三批）**：存储负责在批次持久后、下一次提交开始前
  同步采纳一次，Session 不再自己 `tx.adopt(seq)`，改为发现「已提交却没采纳」即毒化。
  其余改动的现状见下面 §3.5.1 的分项清单。另外 1.4.0 的 durable 新增了可选的 `./codemode` 与
  `./tool-search` 两个导出入口，**判不采纳**（上游无仓内消费方，端口已有等价能力，见 §6 第 7 条）。
- **chord（1.2.0–1.4.0，118 文件 / 28 个 `src` 提交）**：**范围判定** —— 端口 `Pi.Chord` 是**有意收窄**
  的实现，只保留 durable 实际用到的部分：`Delta/`（wire 编解码、不可变路径拷贝应用、修订跟踪器）、
  `Facets/`（宿主、装载器、打包）、`Services/`（句柄 / 提供者 / 消费端 / loopback / 状态编解码 /
  `Watch`）、`Context/`、`Node/`。上游这一段的 28 个提交几乎全落在端口**没有的两块**上：
  对象运行时（`object.ts` / `cell.ts` / `declare.ts` / `contract.ts` / `registry.ts` / `publish.ts`，
  protocol 3 的 `self.state({init})` 与 typed principal、protocol 4 的引用先水合再使用）与
  跨进程链路（`link.ts` / `remote.ts` / `websocket.ts` / `connection.ts` / `cluster.ts`、
  chord2 宿主运行时、帧压缩与流控、`sqlite/` 复制与 telemetry 存储）。端口也没有可与 Node 实现
  互通的对端，故判**不适用**，逐条依据见 §6 第 6 条。真正落到端口形态的只有本批的数组追加快路径
  （`091bcc099`，见 §6.al）与 durable sleeps 涉及 chord 的部分（第三十一批已完成）。
- **env（1.2.0）**：`listFiles`、伪终端与前台作业、`nodePlatform` —— **不适用**：端口的 `Pi.Env` 是 SSH
  远程环境，实现 `IExecutionEnv`（`IFileSystem` + `IShell`），没有上游的 node 侧守护进程与交互式终端
  会话；树形列举以 `ListDirAsync` 递归表达（见 §6.af 第 2 条）。
- **托管安装（1.0.x–1.4.0）**：`releases/<version>` 目录清理、托管标记的 BOM、独立二进制的 `.env` 自动
  加载、npm 打包产物 —— **不适用**：端口的自更新只走包管理器命令，没有托管安装布局（见 §6.af 第 3 条）。
- **durable 1.4.0 剩余项（§3.5.1）**：按「是否影响日常使用」排序，逐批推进；**已全部落地**（第三十批
  `compaction.modelOverrides` → 第三十一批持久睡眠 → 第三十二批 crash-loop guard → 第三十三批
  `onReport` info → 第三十四批 `settings.afterFailedRun`），表内只剩两项判不适用。

#### 3.5.1 durable 1.4.0 剩余清单

| 项 | 上游 | 影响 | 状态 |
| --- | --- | --- | --- |
| `compaction.modelOverrides`（按模型压缩阈值） | 1.4.0 #10942 | 同一 harness 服务小/大窗口模型 | 本次移植（第三十批，见 §6.a） |
| durable sleeps：`runtime.sleepUntil()`、`{status:"sleeping"}`、`sleeping` 状态 | 1.4.0 | 睡眠期间不占 invocation、跨重启恢复、按时醒来 | 本次移植（第三十一批，见 §6.aa） |
| `HarnessOptions.setTimer` / `now` / `ToolExecutionApi.now()` | 1.4.0 | 宿主/假时钟接管定时器与脚本超时 | 本次移植（第三十一批，见 §6.aa） |
| `Harness.wake()` / `HarnessOptions.onWake` | 1.4.0 | Cloudflare Durable Object 等宿主可休眠 | 本次移植（第三十一批，见 §6.aa） |
| Crash-loop guard：`TaskRecord.restarts`、`settings.restartLimit`、parked、`Harness.retryTask()` | 1.4.0 | 连续崩溃的任务不再无限重跑 | 本次移植（第三十二批，见 §6.ab） |
| `onReport(error, info)` 的 `ReportInfo`（`task_parked` / `task_outdated` / `extension_error` / `host_error` / `listener_error` / `migration_failed`） | 1.4.0 | 上报分类 | 本次移植（第三十三批，见 §6.ac） |
| `settings.afterFailedRun`（`keep` / `continue` / `withdraw`） | 1.4.0 | 失败运行后排队输入的去向 | 本次移植（第三十四批，见 §6.ad） |
| Task graph `blocked` 原因（`missing_task` / `task_too_old` / `migration_failed` / `restart_limit`） | 1.4.0 | `/tree` 里能看出任务为何跑不了 | 本次移植（第三十二批，见 §6.ab） |
| `IndexedDbStorage`（浏览器存储） | 1.4.0 | 浏览器侧持久化 | 不适用（端口没有浏览器宿主） |
| telemetry span / metrics / trace 传播 | 1.4.0 | 可观测性 | 不适用（按此前判定，端口不接 OpenTelemetry） |
| 生成重试、延迟轮询、压缩重试改走 sleep | 1.4.0 | 退避期间可休眠 | 本次移植（第三十一批，见 §6.aa） |
| SQLite 迁移到 schema v2（重建 task 表） | 1.4.0 | 存储格式 | 本次移植（第三十一批：`status` 增加 `sleeping`；`restarts` / `abortReason` / `abandonOnRestart` 是记录内 JSON 字段，不占列，第三十二批无需再迁移） |

- **durable 观察面（同一提交 92ab53602）—— 本次移植（第二十批）**：移除 `Session.documentState()` /
  `Conversation.viewState()` / `Harness.taskGraph()` 与 `DocumentState<T>`，统一走
  `watchDoc()` / `watch()` / `watchTaskGraph()`；`WatchHandle`/`WatchEnd` 迁往 chord，
  结束原因为 `ended`（原 `session_closed`）/ `failed`（原 `session_failed`）；
  退休改成显式源事件 `retire(context)`，`advance(null, …)` 不再自动退休；终止清空指向源的
  全部引用（detach / report / replace / listener / 待发帧）。端口细节见 §6.k。
- **chord（1.3.0）**：`traceIn(options, fn, context)` 参数顺序变更 —— **暂不适用**：端口 chord
  尚无 `telemetry` 成员与 `TelemetrySpan`，`traceIn` 本身不存在；与 telemetry 一起落地。
- **telemetry（1.4.0）**：`telemetryContext`→`telemetry?: TelemetrySpan` —— **暂不适用**：端口 ai 层
  没有 `observeModelRequest` 观察器（`LazyStream` 无 observer 参数），无从改名的选项；identity 已独立落地。
- **mcp（1.2.0–1.4.0，5 提交）**：`structuredContent` 可为任意 JSON 值（#10416）与
  「无元数据时拒绝 `iss`」（c5f5b3282）、「取消的请求不再在授权刷新后重发」（da0eadf04）
  已移植（第四批）；**本次移植（第九批）**：连接时不再列资源（#10526）；**本次移植（第十五批）**：
  OAuth 登录的取消与关闭语义（f10993bc7 / #10565）。
- **env / codemode**：`codemode` 的输出分隔、`@options` 首行校验、输入约束声明等属 codemode 子系统。
  其中 worker 排队（#11184）**不适用**：端口没有 QuickJS worker 池，脚本直接在托管的 Jint VM 里跑，
  每个脚本各有一个 VM，没有「两个 worker 的进程级容量」可以排队；`PI_CODEMODE_MAX_WORKERS` 也就
  没有对象可限。随包 `codemode.md` 的 Limits 章节早已不含上游那两条 worker 说明。

## 4. 本次移植明细

| 上游提交 | 变更点 | 落地位置 | 测试 |
| --- | --- | --- | --- |
| `8c4e87389` | grep `--no-config` | `Core/Tools/GrepTool.cs`（抽出 `BuildRgArgs`） | `GrepArgsTests`（3） |
| `bdd9f0399` | `mcp.json` BOM | `Extensions/Mcp/Config.cs` | `McpConfigTests.Load_AcceptsFileWithUtf8Bom` |
| `6a51a39d4` | 资源模式 `~` 展开 | `Core/PackageManagerPatterns.cs` | `PackageManagerPatternsTests`（3） |
| `9fe3f3716` | 空引号模板参数 | `Core/PromptTemplates.cs` | `ParseCommandArgs_KeepsEmptyQuotedArguments` |
| `441f5767c` | 打印模式截断退出码 | `Modes/PrintMode.cs` | `PrintModeTests`（2） |
| `3e477eab5` | `PI_CLIPBOARD_OSC52` | `Utils/Clipboard.cs` | `ClipboardOsc52Tests`（3） |
| `6fcb48670` | 摘要输出上限 | `Core/Compaction.cs`、`Core/BranchSummarization.cs` | `SummaryOutputLimitTests`（2） |
| `4dd2b149d` | 空白工具输出 | `Pi.Ai/Api/{OpenAiCompletions,OpenAiResponsesShared,GoogleShared}.cs` | `OpenAiResponsesTests`（2） |
| `4b994d5e5` | 非法 `\u` 转义 | `Pi.Ai/Utils/JsonParse.cs` | `JsonParseTests`（2） |
| `6e491cfa9` + 1.3.0/1.4.0 文案 | 重试判定 | `Pi.Ai/Utils/Retry.cs` | `RetryableClassificationMatchesUpstream1_4_0` |
| `2ed3118b0` | 显示公式 vs 表格 | `Pi.Tui/Marked/MarkedExtensions.cs` | `MarkdownLatexTableTests`（3） |
| `#10897`（1.3.0） | 畸形最终参数上报 `argumentsError` | `Pi.Ai/Utils/JsonParse.cs`（`ParseToolCallArguments`）、`Types/Messages.cs`（`ToolCallContent.ArgumentsError`）、5 个 API 实现、`Utils/Validation.cs`、`Pi.Agent/ToolExecution.cs` | `JsonParseTests`（5）、`OpenAiResponsesTests`（2）、`ValidationTests`（2）、`AgentLoopTests`（3） |
| `92ab53602`（1.4.0） | `Storage.commit(writes, adopt, context)`：持久后同步采纳一次 | `Pi.Durable/Storage/{IStorage,MemoryStorage,JsonlStorage,SqliteStorage}.cs`、`Session/Session.cs` | `StorageConformance`「adopts a durable commit once…」（4 后端）、`SessionTests.CommitWithoutAdoptionPoisonsTheSession` |
| `92ab53602`（1.4.0，观察面） | 显式退休、watch 类型入 chord、删 `documentState`/`viewState`/`taskGraph` | `Pi.Chord/Services/Watch.cs`（新）、`Pi.Durable/Session/{Observation,Session}.cs`、`Pi.Durable/Harness/*`、`Pi.CodingAgent/Experimental/WatchState.cs`（新）+ 4 处调用点 | `SessionTests`（新增 2 例：null 不退休、`retire` 投递后结束）、`HarnessViewTests` / `HarnessTaskGraphTests` / `HarnessContextTests` 迁到 watch |
| `67996ab75` | `structuredContent` 接受任意 JSON 值 | `Pi.Mcp/Protocol/{Types,Content}.cs`、`Pi.CodingAgent/Extensions/Mcp/Tools.cs` | `ProtocolContentTests`（4）、`McpToolsTests.CreateMcpResultSchema_WithoutStructuredSchema_DeclaresUnknown` |
| `d7f7e0dfe`（1.3.0，`#10160`） | `--model` / `--models` / 凭据打印共用一套 provider 选择策略；`findInitialModel` 不再解析 CLI | `Pi.CodingAgent/Core/ModelResolver.cs`、`Cli/CredentialPrint.cs`、`Cli/Main.cs` | 语料重算（`model-resolver-corpus.json`，语料生成器重建）+ `ModelResolverTests`（删 1 例、加 1 例） |
| `7f225f25d`（1.4.0） | System One 分类器图像输入（`assets` / `images` / `state-image-urls`），新增 `SystemOneCompat`；`models.json` 的 provider `compat` 作用于分类器 | `Pi.Ai/Types/Classifier.cs`、`Api/{SystemOne,SystemOneTransports,LlamaCppClassify}.cs`、`Pi.CodingAgent/Core/{ProviderComposer,ProviderConfig,ModelConfig}.cs` | `SystemOneImageTests`（14）、`CoreProviderComposerTests.ModelsJsonAppliesProviderCompatToClassifierModels` |
| `c5f5b3282` | 无元数据时拒绝 `iss`（RFC 9207） | `Pi.Mcp/OAuth/{OAuthFlow,OAuthErrors}.cs` | `OAuthFlowTests.IssWithoutDiscoveredMetadataIsRejectedBeforeTheExchange` |
| `f75b3240a`（1.4.0） | durable 持久睡眠：`runtime.sleepUntil()`、`{status:"sleeping"}`、`Harness.wake()` / `onWake` / `setTimer`、`ToolExecutionApi.now()`、SQLite v2 | `Pi.Durable/Timers.cs`（新）、`Harness/{Scheduler,HarnessTypes,Harness,Generation,Compaction,ToolOutput,ToolTask*}.cs`、`Types/{DurableTypes,TaskRuntime}.cs`、`Harness/TaskGraphView.cs`、`Storage/SqliteStorageMigrations.cs` | `HarnessSleepTests`（12）、`StorageConformance`「stores and scans sleeping tasks by status」（4 后端） |
| crash-loop guard（1.4.0，`scheduler.ts` / `task-graph.ts`） | `TaskRecord.restarts` / `abortReason` / `abandonOnRestart`、`settings.restartLimit`、parked + `retryTask()`、任务图 `blocked` 原因、close 写回 `pending` | `Pi.Durable/Types/DurableTypes.cs`、`Session/{Session,Transaction}.cs`、`Harness/{Scheduler,Harness,HarnessTypes,AgentDocs,TaskGraphView}.cs` | `HarnessRestartTests`（10） |
| `da0eadf04` | 已取消的请求不再在授权刷新后重发 | `Pi.Mcp/Transports/StreamableHttpTransport.cs` | —（.NET 侧 SendAsync 已按 token 取消，仅补齐 MCP 中止错误语义） |

**语料同步说明**（上游行为变更导致的期望更新，非放宽断言）：

- `prompt-corpus.json`：2 条向量按 1.3.0 新语义更新（`"a '' b"` → `["a","","b"]`、`'""'` → `[""]`）。
- `prompt-corpus.json` 的 `builtinSlashCommands`：`share` 的描述按上游第 27 行改为
  `Share session via a Radius artifact or a GitHub gist`（第七批）。语料生成器本次无法直跑
  （上游 1.4.0 的 `child-process.ts` 引入 `cross-spawn`，本地 `node_modules` 没有该包），
  故按上游源码逐字更新该字段，其余向量未动。
- `compaction-corpus.json`：5 处 `captured[].options.maxTokens` 移除（摘要不再带输出上限）。
- `model-resolver-corpus.json`：整份语料由重建的 `tools/gen-coding-agent-model-resolver-corpus.mjs`
  对着 1.4.0 的 `model-resolver.ts` 重算（第二十一批）。向量**输入**沿用旧语料（模型集、授权集、
  pattern、CLI 选项组合），只重算期望值，故变化即上游语义变化：`parsePatterns` 50 条、`exactMatches`
  4 条、`scopes` 9 条、`initialModels` 3 条，其余不变；`cliModels` 因新增 `inlineApiKey` 维度从 1440
  条扩到 2880 条（原 1440 条即 `inlineApiKey: false` 分支，期望值未变）。

## 5. 验证结果

| 项 | 结果 |
| --- | --- |
| `dotnet build`（slnx） | 0 警告 / 0 错误 |
| `Pi.Ai.Tests` | 512 / 512 通过（第四十一批新增 3 例：同一 delta 里思维块先于正文、Codex 头可被调用方覆盖、
  OAuth 首选回调端口被占时退到随机端口；第三十九批新增 6 例：跨模型丢弃思维块 / 同模型保留签名块 / 模型交替、
  错误工具结果图片在 Anthropic 与 Bedrock 上移到工具结果之后；第三十五批新增 3 例：413 请求体超限、
  ChatGPT 限额文案、增量工具提示的移除登记；第二十二批新增 14 例分类器图片；此前 486） |
| `Pi.CodingAgent.Tests` | 5073 例（5070 通过 / 3 跳过，第四十一批新增 5 例：`RenderUtils` 状态袋取状态、
  codemode 行耗时、shell 行的时钟仍在组件状态袋、Android 上的 Termux 剪贴板读与提示；
  第四十批新增 3 例：握手未完成时打开 MCP 管理器、
  重连在途时菜单仍可用、禁用再启用后工具定义走到新连接；第三十九批新增 3 例：锁等待窗口、
  PowerShell 安装目录形状、解析结果存在性；第三十八批新增 1 例 HTML 导出 CJK 强调接线；
  第三十七批新增 5 例跨 chunk 拆分的 ANSI 序列；
  第三十六批新增 6 例工作树上下文，其中 2 例需要符号链接权限、无权限时跳过；
  第三十五批新增 8 例：恢复 loadout 的扩展工具激活 / 切树 / 压缩前删除 4 例、拆分回合的前缀摘要 focus 2 例、
  剪贴板进程组 2 例；第二十九批新增 5 例对话框栈 / overlay 归属；第二十八批
  新增 8 例配置 schema 一致性；第二十五批新增 9 例状态上报器；第二十二批新增 1 例「provider 级
  `compat` 落到分类器」；第二十一批删 1 例、加 1 例，数量不变） |
| `Pi.Agent.Tests` | 36 / 36 通过 |
| `Pi.Chord.Tests` | 106 / 106 通过（第四十二批新增 2 例：末尾追加的快路径、中间 splice 的尾部保留与 `remove` 夹取） |
| `Pi.Durable.Tests` | 507 / 507 通过（第四十六批新增 4 例文档缓存回收：解析规则、到期驱逐与冷加载、
  被观察不驱逐直到 watch 停止、无限保留与用完即弃；第四十三批新增 2 例等待环：自等、经自有工作成环；
  第四十二批修掉 `ArmSleepTimer` 的并发布设竞态：全量跑必现「一次睡眠两个 500ms 定时器」，
  单类跑不复现）。
  第三十四批新增 4 例 `afterFailedRun`；第三十三批新增 7 例上报分类；第三十二批新增 10 例 crash-loop guard / 停放 / retryTask / 阻塞原因；第三十一批新增 12 例持久睡眠 + 1 例跨后端 conformance；第三十批新增 5 例按模型压缩策略；第二十批新增 2 例；此前 457 |
| `Pi.Mcp.Tests` | 55 / 55 通过（第十五批新增 3 例；此前 52） |
| `Pi.Codemode.Tests` | 57 / 57 通过（第四十五批新增 5 例声明约束：约束进注释、可选字段读非 null 分支、
  Draft-04 布尔排他界、超长标签略去与行终止符转义、预算回退；第四十一批新增 1 例 `@options` 必须位于首行；
  第十八批替换 2 例） |
| `Pi.Env.Tests` | 44 / 44 通过（3 跳过） |
| `Pi.Tui.Tests` | 23931 / 23931 通过（第二十八批 +2：1 例移位符号键位、1 例畸形键位 id；第二十七批
  +28：23 例 stdin 回复串、3 例退出复原、1 例滚轮路由、1 例 fd 转义；第二十六批 +6 假光标、
  第二十三批 +3 CJK 强调、第二十四批 +1 Rex/Herdr、第二十五批 +13 程序状态协议） |

说明：两例时序敏感的偶发均已处理——
`ModelSelectorTests.InitialSearchThatMatchesNothingShowsTheNoResultsPath`（顺序相关）在第四十九批
修掉了根因：`CreateWaitedSelectorAsync` 只等「Refreshing model catalogs… 消失」，而首帧可能根本没有
这行（列表尚未排布），于是断言打在一个只有边框的空帧上；现在同时要求出现任一「已出结果」的痕迹
（模型行 / `No matching models` / `Could not refresh`）。修后全量连跑两次均 5070 / 5073（3 跳过）。
`DeviceCodeFlowTests.TimesOutWithPlainMessageWhenNoSlowDown`（6 秒超时，与其他套件并行跑时受负载
影响）仍按「隔离运行通过」处理。

## 6. 未移植项与判断依据

1. ~~**CJK 标点相邻的强调渲染（#10730）**~~ —— **第二十三批已移植（见 §6.h）**，含缺失的语料生成器；
   以下保留当时的判断依据作记录：上游通过第三方包 `marked-cjk-friendly@0.1.2` 替换 marked 的
   `emStrong` 与 `del` 两个分词器。已把该包（204 行的 `dist/index.js`，无构建期依赖）取下来读过，移植要点：
   - 一张 CJK 区间表（含 `\u{20000}-\u{3fffd}` 这类增补平面区间）；CJK 在这里被**当成标点**参与
     flanking，而不是按 CommonMark 的「非标点非空白」。据此生成四张带 6 个捕获组的右定界符正则
     （`emStrongRDelimAstCjk` / `...AstCjkGfm` / `...UndCjk` / `delRDelimCjk`），GFM 变体额外排除 `~`。
   - 判定入口：`!nextChar || !prevChar || punctuation.test(prevChar) || prevIsCjk || cjkTest(nextChar)`
     —— 比 marked 原版多出 `prevIsCjk` 与 `cjkTest(nextChar)` 两项。
   - `isPrevCharCjk` 要处理 marked 只传一个 UTF-16 code unit 的老行为：`prevChar` 可能落在代理对或
     变体选择符（65024–65038）上，需回退到 `maskedSrc` 里取真正的码点。
   - 命中右定界符后还多一层 `isCjkAdjacentToDelimiter`：定界符紧邻 CJK（前或后）时按 "both" 处理，
     以便跨 CJK 标点收尾。
   最后一段仍是障碍：端口有自研 `MarkedTokenizer.EmStrong` 与约 4,990 条 `markdown-corpus.json` 基线，
   而仓库里**没有**这份语料的生成器（`tools/` 下只有 prompt / compaction / codemode / toolrenderers
   等）。要照上游等价移植，需先补一个 `gen-tui-markdown-corpus.mjs`（驱动 `packages/tui` 的 marked
   管线，依赖 `marked` 与 `marked-cjk-friendly`），再改 C# 分词器；否则只能手工推导向量，风险过高。
2. **telemetry span**：`observeModelRequest` 观察器在 ai 层没有对应端口（`LazyStream` 也没有 observer
   参数），要接就得先重建整条流式观察链，收益与改动量不成比例，暂不移植。per-key OpenRouter 目录
   已在第六批完成，客户端身份已在第五批完成。
3. **chord `traceIn` / telemetry 成员**：durable 的观察面重构已**全部完成（第二十批）**
   （`watchDoc`/`watch`/`watchTaskGraph` 取代 `documentState`/`viewState`/`taskGraph`、
   `WatchEnd` 迁 chord 并改 `ended`/`failed`、显式 `retire`）。仍缺的只有 chord 的 `telemetry`
   成员与 `traceIn(options, fn, context)`：它要和第 2 条的 span 链路一起落地，端口 chord 目前
   没有 `TelemetrySpan` 可供承载。
4. **模型目录新增条目**：**已判定不适用**。上游的模型数据不在仓库里 ——
   `packages/ai/src/providers/data/*.json` 被 `.gitignore` 排除（`.gitignore` 第 11 行），
   `providers/*.models.ts` 只是 `generate-models.ts` 从这些 JSON 生成的壳；
   `scripts/hydrate-model-catalog.ts` 明确要求传入**本地的** `models.all.json` 快照
   （「without network access」），仓库里没有这份快照，本机也没有。端口只有
   `src/Pi.Ai/ModelData/{anthropic,deepseek,openai}.json` 三个手写文件，忠实同步 42 家目录
   需要有源数据，硬编会变成编造。决定不改目录数据。
   需要澄清的是**代码侧并无缺口**：`xhigh`/`max` 档位、`clampThinkingLevel` 的顺序、
   `supportsNativeXhighEffort`、各 provider 的 effort 映射在端口里都已存在
   （`Pi.Ai/Models/Models.cs`、`Api/BedrockConverseStream.cs`、`Api/SimpleOptions.cs` 等），
   所以缺的只是「哪些模型有哪些档位」这份数据。
5. ~~**`sessionShareDestination` / resume 活动工具 / `--model` 选择策略**~~ —— 均已移植：
   `sessionShareDestination`（第七批）、恢复会话时恢复活动工具集（第八批）、`--model` 的 provider
   选择策略统一（第二十一批，连带 5 项 `defaultModelPerProvider` 同步与差异 C78 退休）。
6. **chord 的对象运行时与跨进程链路** —— **不适用**（端口形态不同，不是未做）。逐条依据：
   - 端口 `Pi.Chord` 只有 `Delta/`、`Facets/`、`Services/`、`Context/`、`Node/` 五块，
     **没有** `object.ts` / `cell.ts` / `declare.ts` / `contract.ts` / `registry.ts` / `publish.ts`
     对应的对象模型（全仓 `grep Principal` 为空，也没有 `self.state` 这类声明入口）。
   - 也**没有**链路层：`src/Pi.Chord` 与 `src/Pi.Durable` 里没有任何 socket / websocket / IPC
     （跨进程只有 coding-agent 自己的 `Experimental/CoordinatorSocket.cs`，与 chord 无关）。
   - 于是上游这批提交的落点都不存在：`0104fef1e`（chord2 宿主运行时替换 facet/services 运行时）、
     `aa9cf15be`（protocol 3：`self.state({init})`、typed principal、contract placement）、
     `b45e0ebcc`（protocol 4：引用先水合再使用，723 行 `connection.ts` + 685 行 `remote.ts`）、
     `db6497c3d` / `018c87ddc`（链路流控、大帧 deflate）、`9a3ca68bd`（远程引用拒绝外来 ctx）、
     `4759f09fb` / `89e309581`（WAL 页复制，加了又删）、`d9025a5f5`（SQLite telemetry 存储）、
     `c268154ca` / `4a5d37291`（把 SQLite 驱动与 `OutputBuffer`/`Progress` 搬进 chord——端口本来
     就分开放）。
   - 剩余能对上端口的只有 `091bcc099` 的数组追加计划：tracker 侧的 `orderedArrayPlan` 无对应物
     （端口 `Tracker` 是显式动词 API，没有 overlay/plan 机制，见 `Delta/Tracker.cs` 的差异说明），
     apply 侧的「追加别拷两遍」已移植（见 §6.al）。
7. **durable 的 code mode 与 tool search —— 判定：不采纳（产品决策，非「没来得及」）**

   先摆事实。第四十三批复核「只按功能名记录」的 103 个提交时发现，`packages/durable/src/codemode/`
   与 `packages/durable/src/tool-search/` 在端口 `Pi.Durable` 里没有对应物（`grep Codemode` /
   `grep ToolSearch` 皆空）。进一步查它的**定位与消费方**后，结论是不予移植：

   - **它不是新能力，是库的一个可选入口。** `packages/durable/package.json` 把它导出为
     `./codemode` 与 `./tool-search`；但全仓 `grep "pi-durable/codemode"`（`packages/**/src`）**零命中**
     —— 没有源码文件 import 它，`packages/durable/src/index.ts` 与 `harness/*` 也不引用它。
   - **上游自己的 coding-agent 用的是另一个包。** `coding-agent` 的依赖列表里是
     `@earendil-works/pi-codemode`，不是 `pi-durable`；也就是说「在 agent 回合里跑脚本」这条真实
     产品路径走的是 `pi-codemode`，而这一层端口已经完整移植（`src/Pi.Codemode` + 2276 行的
     `Extensions/Codemode`，含 `tool_search`、`models`、`grammar`、`output`、`@options`）。
   - **嵌套调用也在正确的位置。** 上游 `coding-agent/src/core/nested-tool-calls.ts`（263 行）对应
     端口 `src/Pi.CodingAgent/Core/NestedToolCalls.cs`，已落地。
   - **它面向的是「绕过 coding-agent、直接嵌 durable」的应用作者。** 端口内目前没有这样的消费方；
     为假设的消费方维护约 1,800 行沙箱集成（`codemode.ts` 1044 / `models.ts` 287 / `output.ts` 206 /
     `describe.ts` 157 / `tool-search` 99）只会变成长期漂移负担。

   **重启条件（满足任一条就该做）：** ① 有应用只引用 `Pi.Durable`、不装 `Pi.CodingAgent`，却需要在
   持久任务里跑脚本；② 端口的某条产品线要把「脚本 + 工具调用」做成可恢复的 durable 任务。
   真要动手时不必重新调研，按下面这张清单照搬即可（依赖在端口里都齐了）：

   | 上游 | 内容 | 端口已有可复用的底座 |
   | --- | --- | --- |
   | `7b597d148` | durable code mode 入口（`createCodemodeTool` / `createCodemodeExtension`） | `Pi.Durable` 的 `defineExtension`/`defineTool`、`Pi.Codemode` 的 `ICodemodeVm` |
   | `8bb180bec` | 脚本内 `tools` 列举、`tool_search`、`output`、`models.*`、`grammar`；工具错误规则 | `Pi.Codemode.Declarations`、`Pi.Durable.Harness` 的模型/工具 API |
   | `127b77121` / `04566631c` | 结果列出脚本发起的工具调用；关闭 sandbox 不决定结局 | `Pi.Codemode.CodemodeProtocol` |
   | `77332f7a3` | 延迟工具与 `tool_search` 扩展 | 同上 |
   | `c661bc574` | 从 code mode 段去掉共享的 MCP 类型 | —（纯删减） |
   | `eba849739` / `4ac0bd8c7` | 嵌套工具调用、`callers`、`TaskRuntime.ownedTasks()`、一次嵌套调用一份 document | `Pi.Durable` 的 document 与子任务机制 |
   | `f44707e7f` | `documentRetentionMs` 按使用量约束 document 缓存 | `Pi.Durable` 的 Session document 缓存 |
   | `42a3497d0` | `read` 返回图片、可选 Photon 图像处理器 | `Pi.Durable/Tools/ImageMime.cs`（Photon 为 JS 依赖，C# 侧需另选解码器） |

   **方法上的教训**（写给下一次复核）：判断「某个提交是否落地」不能只看文档里有没有它的哈希，
   也不能因为「同族功能已有实现」就跳过，更不能因为「某个包里有这个文件」就认定有用——要看
   **谁引用它**。本轮三条都踩过：先漏了 codemode 一族，又差点把它当成必须移植的功能。

8. **1.4.0 的 `docs/models-configuration.md`** —— **已补齐（第五十批）**：上游由
   `scripts/generate-references.ts` 从 TypeBox schema 生成；端口对应
   `scripts/gen-models-configuration.mjs`，从已发布的 `models.schema.json` 生成到
   `docs/usage/models-configuration.md`。逐行对照结果：Providers 10 / Models 26 /
   Model overrides 23 / Input limits 9 行**零差异**（字段、类型、默认值、说明全部一致）；
   Compatibility 42 个字段一致，差异只有两点：上游按 API 分成 6 个小节（端口发布的是合并后的
   一个 schema），以及 9 条说明措辞更概括（端口 schema 自带文本）。`--check` 可在 CI 里校验是否过期。
### 6.as 本地验证（第五十二批）：崩溃恢复 / fan-out / 内存曲线的实跑结论

生产前的三道坎之一（缺 soak）用本地工具补了一轮。工具在 `tools/soak/`（目录被 gitignore，仅本地），
跑法：`dotnet run --project tools/soak`。三个阶段，全部 PASS：

1. **崩溃恢复（JSONL，真杀进程）**：25 个任务（20 个三阶段链 + 5 个睡眠任务），4 轮
   `Environment.Exit(137)`（不 close、不结算，等效断电），80 条副作用写盘。每次重开后
   `OpenAsync` 重放日志、任务从 checkpoint 继续，最终全部 `Completed`；副作用审计确认
   阶段顺序无异常、单阶段重复 ≤ 崩溃轮数 + 1（只有未落提交的阶段会重跑）。
2. **并发 fan-out（JSONL）**：1 父任务等 50 个子任务（各睡 150ms）全部落定后完成，415 ms。
3. **内存曲线（SQLite + retention=100ms）**：1500 个会话各触碰一次 agent 文档，
   缓存在每个检查点都是 0，堆增长 0.1 MB——`documentRetentionMs` 的回收在线上真实工作。

**过程中确认的一个使用边界**：崩溃后**密集地开关存储**会让 JSONL 重放的目录列举与上一实例的
回收删除竞态（列举到的 sidecar 在 lstat 前被删，`OpenAsync` 抛 ENOENT）。上游 TS 的重放对这种
竞态同样抛错——两边语义一致，生产里重开是低频一次性操作，不构成实际问题；演练工具用重试吸收。
这不是「端口与上游的偏差」，记录于此以免误判。

**对生产结论的更新**：durable 的「缺 soak」这道坎，本地压缩时钟下已经过了一遍
（崩溃恢复、并发等待、内存有界均有实跑证据）。剩余的两道坎不变：CI 只有 Windows、
测试未进 CI（需拆非交互子集）。

### 6.at 全量 agent 功能实测（第五十三批）：真实端点端到端 + 修复一个自定义 provider 流式缺陷

用真实 OpenAI 兼容端点（models.json 自定义 provider）对 CLI 做了端到端实测：

- `--version` / `--help` / `--list-models`（自定义 provider 正确并入目录，含 contextWindow/maxTokens）
- `--print` 全链路：模型决策 → `write` 工具真实落盘（内容逐字节正确）→ `read` 回读校验 →
  最终回答 → 会话 JSONL 落盘（目录布局与会话文档一致）。

**实测抓到并修复一个真缺陷**：models.json 自定义 provider（api 指向内建 `openai-completions`）
流式时全部抛 "No API provider registered"。根因：上游在 compat.ts **模块加载时**注册内建 API
（compat.ts:215），端口的对应物是 `CompatApi` 静态构造——但 CLI 流式路径不必然触碰它，
自定义 provider 不经过 `_base`、直接查注册表时它还是空的。修复：
`ProviderComposer.StreamWith` 的注册表回退路径上显式确保注册（幂等，不覆盖已有条目）。
回归测试 `ProviderComposerCustomApiTests`（断言错误落在连接层而非「未注册」）；
CodingAgent 5071/5074、Ai 512/512 无回归。

**第二轮（第五十四批，同一端点）**：

- `--mode json`：print 的事件流逐行 JSON（session v3 头、agent_start、message_*）。✅
- `--continue`：接续上一会话，在既有文件上追加内容成功。✅
- `--fork`：从会话文件分叉出新会话并继承上下文。✅
- `--export`：导出带主题的会话 HTML。✅
- **RPC 模式**：stdio 行式 JSON（`{"type":"prompt","message":...}`），agent_start →
  turn → agent_end 全事件流正常，bash 工具输出经模型回传。✅
- **MCP 实连**：自建最小 MCP stdio 服务器（initialize / tools/list / tools/call），
  `pi mcp list` 显示 connected + 1 tool，真实模型经 `--print` 调用
  `mcp__e2e__echo` 并把 `ECHO:...` 结果带回回答。✅
- 过程记录：MCP 工具默认 `exposure: "codemode"`（只进脚本不声明给模型），
  直连给模型用需要 `"exposure": "direct"`。

**仍未覆盖**：交互式 TUI、OAuth 登录流、compaction 的实触发、api.openai.com 官方密钥路径（网络不可达）。


## 7. 建议的后续顺序

1. ~~durable 观察面重构（`watchDoc`/`watch`/`watchTaskGraph` 取代旧读接口）~~（第二十批已完成；
   `commit(adopt)` 在第三批，`Pi.CodingAgent.Experimental` 用 `WatchState` 适配器一并改完）。
2. ~~ai：identity~~（第五批已完成）→ ~~per-key OpenRouter 目录~~（第六批已完成）。
3. ai：~~`additional_tools` 延迟工具输入项~~（第十六批）→ ~~`TranscriptToolSupport` 与前缀缓存~~（第十七批）。
4. coding-agent：~~`before_agent_start` 覆盖自定义轮次~~（第十九批）→ ~~`reachesTools` / `aliases`~~（第十八批）→ ~~`--model` provider 选择策略统一~~（第二十一批）→ ~~`sessionShareDestination`~~（第七批）→ ~~resume 活动工具~~（第八批）→ ~~codemode worker 排队~~（不适用）→ ~~孤儿工具结果修复~~（第十批）→ ~~`agent_settled.aborted`~~（第十一批）→ ~~`serializeConversation` 省略 thinking~~（第十二批）→ ~~Anthropic strict 工具降级、Mistral 超时范围~~（第十三批）→ ~~Windows shell 探测~~（第十四批）→ ~~MCP OAuth 取消与关闭语义~~（第十五批）。
5. ~~ai：System One 分类器图像输入~~（第二十二批）→ ~~tui：CJK 强调（先补语料生成脚本）~~（第二十三批）
   → ~~tui：Rex / Herdr 终端探测~~（第二十四批）→ ~~tui + coding-agent：OSC 7501 程序状态~~（第二十五批）
   → ~~tui：`showHardwareCursor` 假光标~~（第二十六批）→ ~~tui：fd 转义 / 退出复原 / 滚轮路由 /
   stdin 回复串~~（第二十七批）→ ~~coding-agent：配置文件 JSON Schema 产物、Kitty 移位符号键位~~
   （第二十八批）→ ~~coding-agent：并发扩展对话框堆叠（`38847563f`）、overlay 归属修复（`a4f67eb65`）~~
   （第二十九批）→ ~~durable：按模型压缩阈值~~（第三十批）→ ~~durable：持久睡眠 + 宿主时钟/唤醒~~
   （第三十一批）→ ~~durable：crash-loop guard + task graph `blocked` 原因~~（第三十二批）→ ~~durable：
   `onReport` info（`ReportInfo`）~~（第三十三批）→ ~~durable：`settings.afterFailedRun`~~（第三十四批）→
   ~~收尾审计：五项遗漏修复（`/compact` 前缀摘要、扩展工具 loadout 激活、剪贴板进程组、ChatGPT 限额文案、
   增量工具提示的移除登记、413 请求体超限）~~（第三十五批）→ ~~收尾复核：工作树上下文 + env / 托管安装的
   不适用判定~~（第三十六批）→ ~~跨 chunk 拆分的 ANSI 序列~~（第三十七批）→ ~~HTML 导出的 CJK 强调~~
   （第三十八批）→ ~~剩余提交复核：跨模型思维、错误结果图片、Windows 锁与 shell 探测~~（第三十九批）
   → ~~MCP 管理器响应性（启动连接未完成即可开、动作走后台、重连串行化）~~（第四十批）→
   ~~剩余提交复核：completions 思维顺序、Codex 头覆盖、OAuth 回调兜底端口、codemode 输出/计时/
   `@options`、Termux 剪贴板~~（第四十一批）→ ~~chord 范围收口 + 数组追加快路径、睡眠定时器布设竞态~~
   （第四十二批）。
   **当前口径**：`v1.0.4..v1.4.0` 的 278 个提交里 104 个在本文档留有哈希引用；其余 174 个
   （其中 103 个改了 `packages/*/src`）按功能名/子系统记录。第四十三至四十七批逐轮抽查后：
   **功能面已对齐**（含 `f44707e7f` 的 `documentRetentionMs`，第四十六批）；
   **性能面已对齐**（`cb4375493` + `ea448f454` 的所有权计算，第四十八批；`68ccef176` 早已同构）。
   至此 `v1.0.4..v1.4.0` 的适用项全部落地，不适用/不采纳项逐条记录在 §6（含 §6 第 7 条的
   durable code mode 决策与重启条件）。
6. 模型目录：已判定不适用（数据不在上游仓库，见 §6 第 4 条）。

### 6.x 第五批（identity）落地明细

上游 `adc5fa9f8` 把客户端身份与 provider 归属从 coding-agent 搬进 pi-ai，端口按同一归属实现：

- `Pi.Ai.Utils.Identity`（新，对应 `utils/identity.ts`）：`UserAgentMode`（detailed/short/custom）、
  `ClientAttribution`、`ClientIdentity`（`AttributionSet` 区分「未设置」与「显式 null」）、
  `ClientIdentityTarget`、`MergeClientIdentity` / `FormatUserAgent` / `GetAttributionHeaders` /
  `GetOpenCodeSessionHeaders` / `GetIdentityHeaders` / `ApplyIdentityHeaders` / `ParseClientIdentity`。
  与上游差异：详版 UA 的运行时段是 `dotnet/<version>`（上游为 `node/`|`bun/`），平台与架构按 Node
  命名映射，便于与上游日志对照。
- 8 个 provider 改由 `GetIdentityHeaders` / `ApplyIdentityHeaders` 打底，`SimpleStreamOptions.Identity`
  与各 options 类型的 `Identity` 承载；身份头位于 model / provider / 请求头**之下**，故配置头仍可覆盖。
- `Models`：新增 `Identity`（`CreateModelsOptions.Identity`）与 `WithClientIdentity` 视图（共用同一批
  provider/凭据/刷新控制，增删互相可见），`Stream`/`StreamSimple`/`StreamDeferred` 按
  `merge(视图身份, 请求身份)` 合并，请求身份最优先。
- `Pi.Agent`：`AgentLoop.DefaultAgentIdentity`、`AgentLoopConfig.Identity`、`AgentOptions.Identity`、
  `Agent.Identity`，在 `streamAssistantResponse` 处按上游同一表达式合并。
- `Pi.Durable`：`Harness.DefaultIdentity`、`HarnessOptions.Identity`，构造时把 `Models` 包成身份视图。
- `Pi.CodingAgent`：删除 `Core/ProviderAttribution.cs`（上游已删），新增
  `Core/ClientIdentitySource`（对应 `core/client-identity.ts`；因与 `Pi.Ai.Utils.ClientIdentity`
  同名，C# 侧改名）。SDK 的 `transformHeaders` 只补 OpenCode 会话头，归属头改由 identity 承载；
  `Utils/PiUserAgent` 改为委托 `Identity.FormatUserAgent`。
- 语料：`prompt-corpus.json` 的 `providerAttribution`（22 向量，驱动已删除的 TS 模块）换成
  `clientIdentity`（16 向量，驱动 pi-ai 的 `getAttributionHeaders`/`getOpenCodeSessionHeaders`）。

### 6.y 第六批（per-key OpenRouter 目录）落地明细

上游 `ee409a85f`（#10353）让 OpenRouter 的目录跟着用户的 key 走：`GET /models/user` 列出该 key 可用的
文本模型，并带回真实的限额与价格。

- `Pi.Ai.Providers.OpenRouterCatalog`（新，对应 `providers/openrouter.ts` + `openrouter-api.ts`）：
  - `PersonalizeAsync(catalog, context, httpClient?, ct?)`：凭据取 `Credential.OAuth.Access` 或
    `Credential.ApiKey.Key`，无凭据原样返回；请求跟随 `context.BaseUrl`（缺省
    `https://openrouter.ai/api/v1`），故区域端点 `https://us.openrouter.ai/api/v1` 按本区域过滤。
    15 秒超时与请求信号合并（对应 `AbortSignal.any([...timeout])`）。401/403/404 原样返回目录
    （models.json 里配的代理拒绝该 key 或根本没有该端点时保留完整列表），其余非 2xx 抛
    `OpenRouter model list request failed: <status>`，body 不是 `{data:[…]}` 抛
    `Invalid OpenRouter model list response`。
  - 过滤规则与上游 `flatMap` 一致：图片/分类模型原样保留（`/models/user` 只列文本模型），chat 模型
    不在列表里就丢弃；在列表里则用 `top_provider.context_length || context_length || 原值` 覆盖
    `ContextWindow`、`top_provider.max_completion_tokens || 原值` 覆盖 `MaxTokens`、有 `pricing` 时
    覆盖 `Cost`。`||` 的语义按「缺失/0 都算缺失」实现，与生成器针对 OpenRouter 缺数据的修补一致。
  - `OpenRouterCost`：`$/token` 换成 `$/1M token`（`(v * 1e6).toFixed(6)` 等价于
    `Math.Round(..., 6, AwayFromZero)`）；缺失、非法与负基础价归零（`openrouter/auto` 这类路由模型
    报 -1，价格取决于它最终选了谁）。只有「带 `min_prompt_tokens` 且不含任何 `utc_*`」的 override
    变请求级阶梯，未列费率沿用基础价；按时段的 override 跳过，因为 `ModelCost` 表达不了。
- `RefreshModelsContext.BaseUrl`（对应上游同名新增）：`ModelsAuth.RunProviderRefreshPhaseAsync`
  传 `provider.BaseUrl`，供个性化按区域端点发请求。
- `Pi.CodingAgent.Core.PersonalizedCatalogProvider`（新，对应 `withPersonalizedCatalog`）：与
  `RemoteCatalogProvider` 同款包装（C# 无对象展开，逐成员转发，两个包装刻意互不继承），差别是：
  - 每次联网刷新把「内建模型 + 更新的 pi.dev 目录」过一遍 `personalize` 再保存 —— 存的是个性化
    列表而非 pi.dev 目录，因此**离线也生效**；
  - 跳过刷新间隔与 etag 复验（结果依赖 key，换 key 后必须重新取）；
  - 个性化列表为空（从未保存过，或旧版 pi 在目录缺失/失败后存了空表）时回落到内建模型；
  - 404/501 视为「pi.dev 没有该 provider 的目录」，只用内建模型做个性化。
  `RemoteCatalogProvider` 的 `ParseCatalog`/`ParseHttpDate`/`ReadHeader`/`MergeModels` 相应改为
  `internal static` 以便复用（`MergeModels` 的键仍是 `类型\0id`，chat 与 image 同名模型得以共存）。
- `ModelRuntime.Create` 的 provider 构建改为上游同一分支：`radius` 不包装、`openrouter` 走
  `PersonalizedCatalogProvider`、其余走 `RemoteCatalogProvider`。
- 测试：`OpenRouterCatalogTests` 9 例（覆盖限额优先级、图片/分类不过滤、401/403/404 原样返回、
  base URL 跟随、OAuth 凭据、失败与非 JSON body、阶梯价与负价归零）；其中「原样返回」按上游
  `toBe(catalog)` 断言同一实例。

### 6.z 第七批（`sessionShareDestination`）落地明细

上游 `cea257439`（#10870）让 `/share` 的上传目标可配置：默认仍是「有 Radius 登录就用 Radius，
否则建私有 gist」，但可以强制成 `gist`（想要永久链接的人不希望被 Radius 抢走）或 `radius`
（要求必须有登录，而不是悄悄退回 gist）。

- `Settings.SessionShareDestination`（`string?`），取值拼写集中在 `SessionShareDestinations`
  （`Auto`/`Gist`/`Radius` + `All`），因为端口把 TS 的字符串联合一律当 `string` 承载。
- `SettingsManager.GetSessionShareDestination()`：只认 `gist`/`radius`，其余（包括 `auto`、拼错、
  新版 pi 写入的值）一律归到默认 `auto` —— 与上游 getter 的 `destination === "gist" || "radius"`
  判定一致；`SetSessionShareDestination` 写全局段、`MarkModified` 后保存。
- `InteractiveMode.HandleShareCommandAsync`：`destination != "gist"` 时才探测 Radius；探测失败且
  `destination == "radius"` 时报 `Radius authentication is not configured. Run '/login radius' first.`
  并结束，不再落到 gist；`gist` 分支完全跳过 Radius（连凭据都不读）。
- 设置界面：`SettingsConfig.SessionShareDestination`、`SettingsCallbacks.OnSessionShareDestinationChange`、
  列表项 `share-destination`（紧跟在 `transport` 之后，循环 `auto → gist → radius`），
  `InteractiveMode.ShowSettingsSelector` 接上读取与回写。
- `/share` 的命令描述改为 `Share session via a Radius artifact or a GitHub gist`。
- 随包文档同步上游三处：`Assets/docs/settings.md` 新增 `sessionShareDestination` 行、
  `sessions.md` / `usage.md` 的 `/share` 段落改为「默认行为 + 可强制」。
- 与上游差异：①上游同时新增 `schemas/settings.schema.json` 条目与 TypeBox `SettingsSchema`，
  端口没有 JSON Schema 产物、设置也刻意不在加载时校验（坏值在 getter 处兜底），故只做 getter；
  ②上游 `test/session-share.test.ts` 的「`gist` 不探测 Radius」用例靠 mock `child_process.spawn`，
  端口的子进程层没有测试缝，端到端跑会真的调用 `gh`（在已登录 gh 的机器上会创建真实 gist），
  因此只端到端覆盖「强制 radius 且无登录时报错」这条确定路径；`gist` 分支的入口条件由
  `SessionShareDestinationTests`（5 例）与 `SettingsSelectorTests.CyclesThroughShareDestinations` 覆盖。
- 测试：`SessionShareDestinationTests` 7 例（缺省 → `auto`、`gist`/`radius` 原样、`auto`/拼错/空串
  归到默认、setter 回读）、`SettingsSelectorTests.CyclesThroughShareDestinations`、
  `InteractiveModeTests.ShareWhenForcedToRadiusReportsTheMissingLogin`。
### 6.w 第八批（恢复会话时恢复活动工具集）落地明细

上游 `6f2f164ab`（#10887，fixes #10785）：SDK 总会把初始工具集算好传进 `AgentSessionConfig`，
于是构造器里那句「`initialActiveToolNames === undefined` 才从记录恢复」永远为假 —— 恢复的会话
丢掉 run 期间临时打开的工具。修法是改用 `usesDefaultTools` 作为判据。

- `AgentSessionBuild` 构造器：`if (_usesDefaultTools) RestoreToolsFromTranscript(_defaultToolModifiers);`
  显式传 `tools` 或 `--no-tools` 的调用方仍然是 `UsesDefaultTools = false`，不恢复。
- `RestoreToolsFromTranscript(modifiers)`：记录的 `toolsAdded` 名单先过
  `SettingsManager.ApplyToolModifiers`（`+name` 加、`-name` 删、按条目顺序），再按
  `IsAllowedTool` 收进 `_pendingToolNames`、`SetActiveTools`。
- `navigateTree` 那处调用保持无参（上游同样未传修饰符）：树的每个分支记录自己的工具集。
- 文档：`UsesDefaultTools` / `DefaultToolModifiers` 的说明补上「恢复会话时生效」。
- 与上游差异：无。上游 `test/suite/harness.ts` 只是给测试替身补了
  `usesDefaultTools: options.initialActiveToolNames === undefined`，端口测试直接显式传该字段。
- 测试：`AgentSessionResumeToolsTests` 6 例（恢复记录的工具集、排除名单生效、显式 `tools` 优先、
  修饰符作用到记录的名单、记录里没有工具集时回落默认集、`UsesDefaultTools = false` 时整段跳过）。
  后两例的期望值按端口形态调整：显式 `tools` 同时要传 `AllowedToolNames`（与 SDK 的
  `ResolveTools` 一致），反向断言那例不注册扩展工具，否则「注册即激活」的 `active_tool` 会让
  「空工具集」不可达。
### 6.v 第九批（MCP 连接时不列资源）落地明细

上游 `f4cca2dc2`（#10526）：注册了资源的服务器，要等 `resources/list` 与
`resources/templates/list` 都完成才注册工具；一个大网盘之类的慢列举会把工具挡在第一条提示之外。
那两份列表只喂 `/mcp` 与 `pi mcp list` 的计数，所以改成本地不列举、只标记「该服务器提供资源」。

- `McpServerConnection`：删掉 `Resources` / `ResourceTemplates` 两个缓存属性、连接时的
  `FetchResourcesAsync`、`notifications/resources/list_changed` 处理器与 `RefreshResourcesAsync`
  （以及随之无用的 `FetchedResources` / `SwallowEmptyAsync`）。连接只问 `initialize` 与 `tools/list`，
  `HasResources` 直接取 `resources` 能力位。
- `/mcp` 的 `connected` 状态：`connected · N tools` 后面只在有资源时加 ` · resources`（不再计数）。
- `pi mcp list`：报告字段 `resources` 从 `int` 计数改成 `bool?`（`true` 表示提供资源），
  `resourceTemplates` 字段删除；文本输出改为 `  offers resources`。
- 资源工具（`list_mcp_resources` / `list_mcp_resource_templates` / `read_mcp_resource`）不变，
  仍按需列举与读取，因此 `McpResources.IsMcpAppResource` 保留（`Resources.cs` 里的按需过滤还在用）。
- 与上游差异：上游把 `isMcpAppResource` 降级为模块私有（只剩 `resources.ts` 内部一处用），端口的
  `McpResources.IsMcpAppResource` 仍是 public —— 端口有单测直接钉这个判定的入参组合，且
  `Resources.cs` 的按需列举也在用。
- 测试调整（行为变更导致的期望更新，非放宽断言）：
  `ConnectListsToolsAndResourcesWithoutMcpApps` → `ConnectListsToolsButNotResources`（并断言
  `resources/*` 一次都没问）、`AResourcesListChangedNotificationRefreshesTheResources` →
  `AResourcesListChangedNotificationIsIgnored`、`AFailingResourceListStillConnects` →
  `AResourcesListThatFailsDoesNotAffectTheConnect`（失败落到按需列举上）、两条重试计数各减一、
  `/mcp` 补全文案改成 `connected · 1 tool · resources`。
### 6.u 第十批（恢复性剔除连同工具结果一起）落地明细

上游 `02726086d`（#11227）：可恢复的 length 停止在**提交前**恢复时，只剔除了助手消息、没剔除应答它的
工具结果 —— 因为只有「一轮跑完」那个调用方把结果传了进去。落单的工具结果进了 transcript，之后每个
请求都被 provider 拒绝（`tool_result` 找不到对应的 `tool_call`）。

- `OmitRecoveryAttempt(message)` 不再收 `toolResults` 参数：自己按 `ToolCallContent.Id` 收齐
  `toolCallId`，在 `_agent.Messages` 里定位这条助手消息（`ReferenceEquals`，或按
  `_entryIdsByMessage` 的 entryId 认 —— 上下文编辑会把投影副本换掉），从它的下一条扫到**下一条
  助手消息**为止，收集 `ToolCallId` 命中的 `ToolResultMessage`。调用方是谁都无所谓。
- 删除 `AgentSessionBuild._lastAssistantToolResults`、`turn_end` 里的记录、`HandlePostAgentRunAsync`
  的传递，以及 `CheckCompactionAsync(…, toolResults)` 的第三个参数。
- 与上游差异：无。上游用 `messages.slice(index + 1)` + `for…of` 早退，端口用显式下标循环。
- 测试：`AgentSessionRecoveryOmitsToolResultsTests` 2 例 ——
  - 恢复的会话里塞一条 `length` 停止的助手消息（带 `toolCall`）与它的 `toolResult`，走 `PromptAsync`
    的提交前恢复，断言**请求上下文里既没有 `toolResult`，也没有 `length` 停止的助手消息**；
  - 反向断言：剔除范围正好是「截断的调用 + 应答它的结果」两条 `context_edit`（都是 `replacement` 为
    null 的剔除），避免以后把范围放宽悄悄丢上下文。
  - 这两例在去掉结果查找后会失败（请求里会重新出现 `ToolResultMessage`），已实测确认。
### 6.t 第十一批（`agent_settled.aborted`）落地明细

上游 `503c60552`（#10607）的前半段。`agent_settled` 原先不带任何字段，界面分不清「这轮正常收尾」
和「这轮被 Escape 中止」，中止也就没法回到 idle。

- `AgentSessionEvent.AgentSettledEvent(bool Aborted)` 与扩展侧的 `ExtensionEvent.AgentSettled(bool Aborted)`
  各加一个必填字段（TS 同样是必填，不是可选）。
- `EmitAgentSettledAsync`：在派发前取一次 `_agentRunAbortRequested`，扩展事件与会话事件用**同一个**
  快照值（TS 的 `const aborted = this._agentRunAbortRequested`）。
- 文档：`Assets/docs/json.md` 的示例记录与事件表同步成
  `{"type":"agent_settled","aborted":false}` / `| agent_settled | aborted |`。
- 与上游差异：无。OSC 7501 那半段（`packages/tui/src/program-status.ts`、
  `coding-agent/src/modes/interactive/program-status-reporter.ts` 等 20 余文件）当时未做，
  已在**第二十五批**补齐（见 §6.f）。
- 测试：`AgentSessionPromptTests.AgentSettledReportsAbortedWhenTheRunWasAborted`（新增）与
  `RunAgentPromptAsync_AgentRunFailure_StillSettlesWithoutThrowing`（强化为断言 `Aborted == false`）。
  新用例用一条阻塞式 `StreamFn`（TCS 放行）把 run 卡在流里，再 `AbortAsync()` —— 中止标志是同步
  置位的，所以「先中止、后放行」不会死锁。
### 6.s 第十二批（摘要序列化省略思维链）落地明细

上游 `15e7bdc14`：回放的思维块常常不在模型活上下文里（radius 会丢，Anthropic 会丢掉与其请求不匹配的
块），而 `serializeConversation` 把它们全塞进去 —— 一个 ~996K/1M token 的会话因此产生了 1.38M token 的
摘要请求，每次重试都失败。provider 本来就只返回真实推理的摘要，可见文本、工具调用与工具结果已经把
「发生了什么」记全了。

- `Pi.CodingAgent/Core/CompactionUtils.SerializeConversation`：删掉 `[Assistant thinking]: …` 这一段与
  收集它的 `thinkingParts`（`case ThinkingContent` 分支一并删掉）。助手消息现在只产出
  `[Assistant]: …` 与 `[Assistant tool calls]: …`。
- `Pi.Durable/Harness/Compaction.SerializeConversation`：同一改法（`thinking` 列表与那段 push 删除）。
  其余格式不变 —— 助手文本块仍按 `\n` 连接，工具结果仍按 2000 字符截断。
- 文档：`Assets/docs/compaction.md` 的序列化示例与扩展示例注释各去掉一行 thinking，并补一句
  「Thinking blocks are omitted…」。
- 语料：`tests/Pi.CodingAgent.Tests/compaction-corpus.json` 的 `serializeConversation` 向量去掉
  `[Assistant thinking]: hidden reasoning\n\n`。**这条不是靠手改拍板**：`tools/gen-coding-agent-compaction-corpus.mjs`
  在本机可以跑（`node --experimental-strip-types --import ./tools/register-compaction-shims.mjs …`），
  重新生成后与手改结果逐字段对比，只有 `__tmp` 与几个未 token 化的时间戳不同，
  `/serializeConversation` 整段**完全一致**，因此没有把重新生成的文件整体写回（避免引入无关抖动）。
- 与上游差异：无。
- 测试：`CompactionCorpusTests.SerializeConversation_OmitsThinking`（新增，直接钉住「思维链不出现、
  文本/工具调用/工具结果还在」），`Pi.Durable.Tests` 的
  `SerializeConversationFormatsTranscriptOmitsThinkingAndTruncatesToolResults`（原断言 `Contains`
  改为 `DoesNotContain`，方法名一并改）。
### 6.r 第十三批（Anthropic strict 工具降级 + Mistral 超时范围）落地明细

约束采样本体（`Tool.constrainedSampling`、`resolveJsonSchemaStrictSampling`、grammar 工具、
各 provider 的 strict 分支）端口早已移植，缺的是这两条**兼容性兜底**。

**Anthropic strict 容量拒绝降级重试**（上游 `d4d53612b`，#10409）：Anthropic 会因为 strict 工具集
超出容量回 400 —— 编译出的语法太大、strict 工具太多、union 类型参数太多。这类拒绝只跟 strict 有关。

- `BuildParams` 拆成 `BuildParamsForRequest(...) → AnthropicBuiltParams(Params, HasPreferredStrictTools)`；
  `BuildParams` 保留原签名、委托给它（调用方与既有测试不受影响）。
- `ConvertTools(..., disablePreferredStrictTools)`：该开关为真时，声明为
  `{type:"json_schema", strict:"prefer"}` 的工具按**原始** schema 发送；`strict:"require"` 仍走 strict。
- 判定「有没有可降级的工具」用的是**转换结果**：`definitions[i].strict == true` **且** 该工具的声明是
  `prefer`。这样 `supportsStrictTools` 为假、或 schema 命中 Anthropic 不接受的 keyword 而自行降级的
  工具，都不会误触发重试。
- `Stream`：`retryProviderRequest` 外面套一层循环。捕获 `ProviderHttpException` 且
  `!signal.IsCancellationRequested && !disablePreferredStrictTools && built.HasPreferredStrictTools &&
  IsStrictToolLimitRejection(error)` 时置开关、追加诊断
  `anthropic_strict_tools_fallback`（`details.reason = "strict_tool_limits_exceeded"`）、重发一次。
  **已经开始的流永不重试**（异常只会来自发请求阶段）。
- `IsStrictToolLimitRejection`：`status == 400` + body 里 `error.type == "invalid_request_error"` +
  文案命中 `The compiled grammar is too large` / `Too many strict tools (` /
  `Schemas contains too many parameters with union types (`。非 JSON body 直接判否。

**Mistral 超时范围**（上游 `6b07b4e57`，#10609）：超时只覆盖「等响应头」，长流不能因为固定截止时间
被砍断，中途卡住交给 HTTP 客户端的空闲超时。

- `RequestMistralStreamAsync`：CTS 改名 `headerTimeoutCts`，`SendAsync` 成功后立刻
  `CancelAfter(Timeout.Infinite)`，明确撤掉截止时间；错误文案改成
  `Mistral response headers timed out after {ms}ms`（原 `Request timed out after …`），
  且取消过滤器补上 `!cancellationToken.IsCancellationRequested`，不让调用方取消被误报成超时。
- **顺带修掉一个端口真 bug**：原来 `await using var content = await response.Content.ReadAsStreamAsync(…)`
  在方法返回时就把正文流 Dispose 了 —— 而 `ReadMistralEvents` 是异步迭代器，真正读取发生在之后。
  于是 Mistral 的**流式路径从来没有成功过**（一片 delta 都读不到），因为没有 Mistral 流式测试所以一直
  没暴露。现在把 `HttpContent` 交给迭代器，在**枚举开始时**打开、枚举结束 `await using` 释放。
- 与上游差异：无。上游用 `combineAbortSignals` + 单独的 header 信号；端口用 `CreateLinkedTokenSource`
  + `CancelAfter(Infinite)`，语义一致。
- 测试：`AnthropicStrictToolFallbackTests` 9 例（400 后重发一次且 prefer 降级、require 保持 strict、
  诊断条目、无 prefer 工具时不重试而把 400 原样上抛、6 组 `IsStrictToolLimitRejection` 判定矩阵、
  非 JSON body 判否）；`MistralStreamTests.AStreamThatOutlivesTheTimeoutKeepsGoing`（`TimeoutMs = 100`，
  正文两片间隔 400ms，断言 delta 与 `StopReason.Stop` 都到位 —— 这条同时钉住了上面那个 Dispose bug，
  修之前它一片 delta 都收不到）。

### 6.q 第十四批（Windows shell 探测跟随 `%SystemRoot%`）落地明细

上游 `28bea006b`（#9501/#9504）把 Windows shell 探测改了两处，端口分开处理。

**一、`isLegacyWslBashPath` 跟随系统目录（已移植）**

上游原来写死 `/^[a-z]:\windows\(system32|sysnative)\bash\.exe$/`，Windows 装在别处（`D:\WINNT`、
精简镜像、改过系统盘符）时旧版 WSL 垫片识别不出来，于是拿 `-c` 去调一个只认 stdin 的 bash。

- `Pi.CodingAgent.Utils.Shell.IsLegacyWslBashPath(path, systemRoot = null)`：新增可选 `systemRoot`
  （测试缝，缺省读 `%SystemRoot%`），按它拼 `System32` / `Sysnative` 两个目录名比较；`systemRoot`
  缺失或**不是盘符限定**（`\\server\share`、相对路径）时退回原来的约定形态正则 —— 与上游
  「未知 Windows 目录就按 `C:\Windows` 认」的兜底一致。`DriveQualifiedRegex` 必须带
  `RegexOptions.IgnoreCase`，否则 `C:\Windows` / `D:\WINNT` 这种大写盘符匹配不上（实测踩过）。
- `Pi.Durable.Env.LocalExecutionEnv` 里有一份同名私有方法（Pi.Durable 不引用 Pi.CodingAgent），
  同步成同一语义，`GetBashShellConfig` 的 `-s` / `-c` 分支行为随之对齐。
- 测试：`ShellProbeTests` 19 例 —— 13 例覆盖「约定形态 / 已知 SystemRoot / Windows 装在别处 /
  非盘符 SystemRoot」，6 例钉住只有 `System32` 与 `Sysnative` 两个目录名命中。
  `CoreUtilsCorpusTests` 里那两条向量改为显式传 `systemRoot: ""`，避免本机 `%SystemRoot%`
  让语料结果随机器变化。

**二、Store 别名探测（不适用）**

上游另一半把 `existsSync(path)` 换成 `accessSync(path, F_OK)`，因为 node 的 `existsSync` 在
Windows 上会额外 stat，让可正常执行的 Store 应用别名（`WindowsApps\...`）返回 EACCES 而被判不存在
（nodejs/node#36790）。这条在 .NET 没有对应物：`File.Exists` 走 `GetFileAttributesEx`，不额外 stat，
对符号链接/重解析点只报存在性，不会因权限位把可执行别名判死。端口维持 `File.Exists`，无需改动 ——
硬造一个「访问检查」反而会引入上游要规避的那个误判。

### 6.p 第十五批（MCP OAuth 取消与关闭语义）落地明细

上游 `f10993bc7`（#10565）：`pi-mcp` 的 OAuth 流程给每个请求带上取消信号；被取消的刷新不再退化成
一次新授权；流式 HTTP 传输关闭会话时复用「最近一次请求的令牌」而不是去问认证提供者；coding-agent
的 MCP 扩展按会话持有**一个**取消源，Esc 与会话关闭都会中止登录，关闭时等被中止的登录清理完；
发往授权服务器的请求一律 15s 上限，`pi mcp login --timeout` 限制整次登录。

端口侧把上游的 `AbortSignal` 落成 `CancellationToken`（本来就贯穿这一层），分四处改：

**一、关闭会话不再触发刷新**（`Pi.Mcp/Transports/StreamableHttpTransport.cs`）

- 新增 `_lastToken`，`ApplyAuthHeaderAsync` 每次取令牌时记下；`ApplyHeaders` 多一个 `token` 参数。
- `CloseAsync` 的 DELETE 改用 `_lastToken`，且超时只用 `new CancellationTokenSource(1s)`，
  **不再链接调用方的令牌**。理由与上游一致：`AuthProvider.Token()` 可能先做一次网络刷新，
  关闭流程不能等它。
- `AuthProvider.Token` 的文档同步说明「只在会等应答的请求前调用，关闭时不问」。

**二、取消的刷新不得变成新授权**（`Pi.Mcp/OAuth/OAuthFlow.cs`）

`RunFlowAsync` 的刷新分支在原有 `catch (OAuthInsecureEndpointError)` 之前加
`catch (Exception) when (cancellationToken.IsCancellationRequested) { throw; }`：
请求可能已到服务器并轮转了 refresh token，再走一次授权会把新凭据丢掉。.NET 的取消异常本就不是
`OAuthError`，这层是上游同款防御（自定义 fetch 可能把中止包成别的异常）。

**三、登录整体可取消 + 15s 请求上限**（`Pi.CodingAgent/Extensions/Mcp/OAuth.cs`）

- `RefreshRequestTimeoutMs` → `OAuthRequestTimeoutMs`，语义从「刷新请求」扩到「所有发往授权服务器的请求」。
- 新增 `TimedFetch(McpFetch? = null)`（对应 TS `timedFetch`）：给每次请求叠加 15s 上限，调用方令牌继续生效；
  刷新路径与 `SignInServerAsync` 都用它。
- `SignInServerAsync`：进入即 `if (cancellationToken.IsCancellationRequested) throw McpSignInCancelledError`；
  外层再加 `catch (Exception) when (cancellationToken.IsCancellationRequested) → McpSignInCancelledError`，
  让「底层中止异常」统一报成「登录已取消」（CLI 的错误文案依赖这个类型）。
- `WaitForAuthorizationResponseAsync` 无需改：端口本来就把 `controller` 与调用方令牌链接，取消会中止粘贴提示。

**四、扩展按会话持有一个取消源**（`Pi.CodingAgent/Extensions/Mcp/McpExtension.cs`）

- 删掉 `sessionActive` 与 `generation` 计数器，换成一个初始即取消的 `CancellationTokenSource`：
  「会话是否活跃」变成 `!token.IsCancellationRequested`，迟到的运行时加载/连接结果按它丢弃。
- 新增 `Track(Task)`（对应上游 `track`）登记后台工作：管理动作与登录。`SessionShutdown` 先
  `CancelAsync()`，关连接，再**等**登记的这些工作清理完（回调端口不会在会话消失后才释放）。
- `SignInAsync(server, prompt, cancel)`：会话令牌与 Esc 令牌链接后传给 `SignInServerAsync`；
  `RunActionAsync` / `LoginCommandAsync` 在动作结束后检查会话是否已结束，结束就丢弃结果、不再碰 `ctx`。
- `SignInWithUiAsync`：建一个 Esc 用的 CTS，状态页带取消键（`Status(title, message, onCancel)`），
  「Connecting…」仍可取消 —— 与上游一致。
- `IMcpUi.Status` 增加可选 `onCancel`；`McpManagerView` 有回调时在页脚显示 cancel 提示并响应
  `tui.select.cancel`，无回调时按键不做任何事。

**五、`pi mcp login --timeout` 覆盖整次登录**（`Cli.cs`）

超时从「等粘贴重定向 URL」提到 `SignInServerAsync` 的令牌上：`WaitForRedirectUrlAsync` 去掉自己的
`timeoutMs`，只跟随传入令牌。于是发现、授权、换令牌任何一步卡住都会在时限内报
「was cancelled or not completed within Xs」。

**测试**

- `tests/Pi.Mcp.Tests/TransportCloseTests.cs`（新）：关闭复用最后一个令牌、不再调提供者（计数不变）、
  DELETE 带 `Mcp-Session-Id`。
- `tests/Pi.Mcp.Tests/OAuthFlowCancelTests.cs`（新）：发现阶段取消 → 直接失败且 `RedirectedTo` 为空；
  刷新阶段取消 → 停在 `/token`，不落到新授权。
- `tests/Pi.CodingAgent.Tests/McpOAuthTests.cs`：已取消令牌 → 立即 `McpSignInCancelledError`；
  卡住的发现请求被取消 → 报 `McpSignInCancelledError`（去掉新加的 catch 这例会抛底层中止异常，用例即失败）。
- `tests/Pi.CodingAgent.Tests/ExtensionManagerViewTests.cs`：状态页有取消键时 Esc 触发回调；
  无回调时既不显示 cancel 提示也不响应按键。
- 坑：`HttpListener` 的前缀必须挂在**根**（`http://127.0.0.1:port/`）上，发现请求打在
  `/.well-known/...`，用 `/mcp/` 前缀会让 `GetContextAsync` 抛 404 并结束服务循环（该用例超时 10s）。

### 6.o 第十六批（`additional_tools` 延迟工具输入项）落地明细

上游 `e47b8e37a`（#7709）：延迟加载的工具以前只能靠客户端 tool search（`tool_search_call` +
`tool_search_output` 两个输入项）就地补发；支持 `additional_tools` 的模型改为直接插一个
`{type:"additional_tools", role:"developer", tools:[…]}` 输入项，少两个回合合成项，前缀缓存也更容易命中。

**ai 侧此前已落地，本轮只差两处**

- `OpenAiResponsesShared.ConvertResponsesMessages` 里 `SupportsAdditionalTools` 分支、
  `OpenAiResponses` / `AzureOpenAiResponses` / `OpenAiCodexResponses` 里
  「`additional_tools` 优先、`tool_search` 兜底、都没有则进顶层 `tools`」的三段选择都在位。
- **补上的是 compat 声明**：`Pi.CodingAgent/Core/ModelConfig.cs` 的
  `OpenAiResponsesCompatSchema` 缺 `supportsAdditionalTools` 与 `supportsToolSearch`
  两个字段（上游把它们放在 `packages/ai/src/providers/compat-schema.ts`）。缺了它们并不会报错
  —— 因为 `ProviderCompatSchema` 是三分支 `Union`，而对象校验与 TypeBox 一样**不拒绝未知键**，
  写错的字段会被静默接受（与上游同构，故未改成严格模式）。

**为什么这段值得单独记一笔**

「schema 里没声明」在这里不等于「用户写了会报错」：`ObjectJsonSchema` 只校验声明过的键，
`Union` 只要有一个分支通过就算通过。所以这类字段漏声明是**静默失效**，只能靠测试盯住语义。

**测试**

- `tests/Pi.Ai.Tests/Api/DeferredToolsTests.cs`（新，4 例，对应上游 `deferred-tools.test.ts`）：
  `additional_tools` 分支（`role=developer`、迟到的工具、无 `defer_loading`、无 tool search 项）；
  退回 `tool_search` 分支（`execution=client`、`status=completed`、`defer_loading=true`、无
  `additional_tools`）；两者都不支持时不留任何就地标记；工具用过之后标记仍排在
  `function_call` 之前、且顶层请求工具仍只有基础工具。
- `CoreProviderComposerTests.ModelsJsonAcceptsTheDeferredToolsCompatFlags`：`models.json` 里写
  `compat.supportsAdditionalTools` / `supportsToolSearch` 能带进快照。
- **踩坑**：测试上下文必须给 `SupportsMidConvoSystemMessages = true`。否则 `Transcript.ResolveTranscript`
  会把后续系统消息折叠进首条，「迟到的工具」就成了请求级变更，`ResolveTranscriptTools` 返回
  `anchorsAdditions = false`，两种就地补发都不会发生 —— 一开始四个用例里有三个因此失败。

### 6.n 第十七批（`TranscriptToolSupport` 与响应前缀缓存）落地明细

上游 `caa6967b6`（#11142，fixes #10940）把「工具变化能不能就地表达」从布尔改成三值，并顺手修掉
「每次后续加载都重发顶层工具列表、缓存整段失效」的问题。

**`Transcript.ResolveTranscriptTools` 的契约变了**

- 参数：`bool supportsToolAdditions` → `TranscriptToolSupport`（`None` / `Additions` / `Changes`）。
- 返回：`(RequestTools, bool AnchorsAdditions)` → `(RequestTools, int FoldedSystemMessages)`。
  「前多少条系统消息的工具变化已被顶层列表吸收」比「能不能锚定」更精确：`Additions` 下删除或同名
  重声明会把吸收点前移一次，之后的纯新增仍就地锚定 —— 于是缓存前缀每次删除只失效一次，
  而不是每次后续新增都失效。
- 同名在先即算重声明（即使中间被删过）：Kimi K3 会忽略「被删掉又被后来的工具消息重新声明」的工具。

**`OpenAiResponsesShared`**

- 新增 `GetResponsesToolSupport(options)`：`midConvo && additionalTools` → `Changes`；
  否则 `toolSearch` → `Additions`；否则 `None`。
- `Changes` 下**所有**工具都进 `additional_tools`（含首条系统消息，像 Codex），顶层 `tools` 留空：
  后来的 `additional_tools` 项无法替换顶层工具，混用会让重定义失效。
- 首条之后的每个 `additional_tools` 前加一句「这是增量」；通报过删除后还要重申
  `Still unavailable: …`，否则模型会把最新一项当成完整工具集，小模型会在后续加载后重新调用已删工具。
- 删除改成文本通报 `renderToolRemovalNotice`；重定义（删同名 + 加同名）**不**通报删除。

**调用点**

`OpenAiResponses` / `AzureOpenAiResponses` / `OpenAiCodexResponses` 改成先建
`ConvertResponsesMessagesOptions`，再用 `GetResponsesToolSupport(同一份 options)` 决定顶层列表；
`OpenAiCompletions`（两处 + `ConvertMessages`）用 `Additions`/`None` 二值，并按系统消息序号
`systemIndex++ >= FoldedSystemMessages` 决定是否就地发 Kimi 的 `system{tools}` 消息。

**测试**（`tests/Pi.Ai.Tests/Api/DeferredToolsTests.cs` 重写为 6 例，对应上游
`transcript-tool-changes.test.ts` 的 Responses 部分）

- `Changes`：顶层工具为空；`additional_tools` 依次是 `base_tool`、`late_tool`，且「增量」提示排在第二条之前。
- `Additions`：顶层只有 `base_tool`；`late_tool` 走 `tool_search_call/output`（`defer_loading=true`）。
- `None`：顶层就是 `[base_tool, late_tool]`，没有任何就地标记。
- 删除以文本通报；重定义只发新的 `additional_tools`，不通报删除。
- **前缀稳定（#10940）**：分四步累积同一段对话（初始 → 删除 → 两次加载），断言每次请求的 `input`
  以上一次的 `input` 为前缀、`additional_tools` 序列为
  `[base_tool, drop_tool] / [late_tool] / [later_tool]`、两次增量提示都带 `Still unavailable: drop_tool.`。
- `TranscriptAndUtilsTests` 的旧用例改写为四值断言（`Additions` 吸收点 1、删除后 2，`Changes` 0 且顶层空，
  `None` 2 且顶层为当前工具集）。

### 6.m 第十八批（codemode 与 MCP 解耦：`reachesTools` / `aliases`）落地明细

对应上游 `24382bedd`（1.2.0，20 文件 / +377 −314）。三项特化被一次替换掉：

| 旧机制 | 新机制 |
| --- | --- |
| `isCodemodeTool()` / `isToolSearchTool()` 按参数 schema 的**引用**判定「这是不是我这个工具」 | 工具自报 `reachesTools(input)`；同名但没声明的工具一律够不到 MCP 工具 |
| MCP 扩展解析 codemode 脚本源码（`/\b(searchTools\|describeNamespace\|describeTool\|ALL_TOOLS)\b/` + `includes(mcp__server)`），再等对应服务器 | 直接问 `tool.reachesTools(input)`：`"all"` 等全部，否则按「单词是否包含命名空间」挑服务器 |
| `pi-codemode` 认识 MCP：`MCP_TYPESCRIPT_PREAMBLE` + `mcpStructuredContentSchema()` 把 MCP 输出 schema 渲染成 `CallToolResult<T>`；命名空间靠「最后一个 `__` 之后的片段」匹配 | codemode 不认识 MCP：MCP 结果 schema 就是一个自我描述的普通对象 schema；命名空间带 `aliases`（MCP 服务器名即别名） |

**协议面**

- `ToolDefinition.ReachesTools`：`Func<IReadOnlyDictionary<string, object?>, ToolReach>?`。TS 是
  `(input) => "all" | readonly string[]` 的联合；端口用 `ToolReach`（`IsAll` + `Words`）承载，
  避免用 null 冒充 `"all"`。
- `ToolNamespace.Aliases`：`IReadOnlyList<string>?`。
- `ToolInfo.ReachesTools`：携带**函数本身**（不是结果），调用方自己给 input；`GetAllTools()` 透传。

**codemode 侧**

- 新增 `GetScriptReach(object? code)`：取 input 的 `code`，不是字符串则空；命中四个发现类全局
  （`searchTools` / `describeNamespace` / `describeTool` / `ALL_TOOLS`）则 `All`，否则按 `\w+` 切词。
  定义处 `ReachesTools = input => GetScriptReach(input.GetValueOrDefault("code"))`。
- `IsCodemodeTool` 删除；`CodemodeSchema` 仍作为 `Parameters` 使用。
- 描述不再追加 `Shared MCP Types` 段；`DescribeOutput` 改为 `DescribeFields(schema, depth)`：
  对象 schema 列字段名，对象类型字段往下走 `depth` 层，于是 MCP 工具声明成
  `{ content, structuredContent?: { hits }, isError? }` 而不是 `CallToolResult<{ hits }>`。
- `IsNamespaceName` 从 `(string ns, string query)` 改为 `(ToolNamespace ns, string query)`：
  名字 + 别名逐个比较，自身与 query 都过一遍 `ToCodemodeIdentifier`，删掉 `__` 后缀规则。为便于
  单测改为 `internal`。

**tool_search 侧**

- `IsToolSearchTool` 删除，改为 `ReachesTools = _ => ToolReach.All`（查询可能命中任何尚未声明的工具）。

**MCP 侧**

- `ScriptNeedsServer(code, server)` → `ReachesServer(ToolReach reach, string server)`；新增
  `HasReachingTool(tools, name)` 取代 `tools.Any(IsCodemodeTool)`，自动激活与「是否已经可达」两处判定都用它。
- `ToolNamespace` 多一个 `Aliases = [server]`，`describeNamespace("docs")` 因此能找到 `mcp__docs`。
- `CreateMcpResultSchema` 重写：内容块换成 `CONTENT_BLOCK_SCHEMA`（`type` 枚举 + `text`/`data`/`mimeType`，
  `required: ["type"]`，`additionalProperties: true`）；服务器自己的 `structuredContent` schema 经
  `RelocateSchema` 把本地 `$ref`（`#`、`#/$defs/X`）下沉到 `#/properties/structuredContent` 之下再嵌进去，
  `const`/`enum`/`default`/`examples` 里的 `$ref` 当数据不动、`properties`/`$defs`/`definitions` 等映射类键的
  键名不当关键字；去掉 `_meta`（`convertMcpResult` 本来就丢它）；补 `additionalProperties: true`。
  无 `structuredContent` schema 时仍沿用 #10416 的 `{}`（any）。

**prelude 措辞**

`image()` 的错误文案去掉 MCP 说法：「…or an image block `{ type: "image", data, mimeType }`」、
「image only accepts image blocks」、「image expected image block data」。

**顺带补上的一处遗漏**

`427052247`（1.4.0，「code mode description says schema tools resolve with their value on failure」）
此前没落：`DESCRIPTION_INTRO` 的 `tools.<name>` 那条补上「A tool whose declaration returns an object
resolves on failure too when it returned that object; check its fields.」。重新生成语料时该行暴露出来。

**语料与测试**

- `tools/gen-coding-agent-codemode-corpus.mjs`：`isCodemodeTool` 段换成 `reachesTools` 8 例（含
  `tools.mcp__docs__search`、`tools['mcp__docs__' + name]` 两种写法与四个发现类全局）。
- `tools/gen-coding-agent-tool-search-corpus.mjs`：`identity` 段换成 `reachesTools: "all"`。
- 生成器现在能跑通：补 `@earendil-works/pi-ai/utils/retry` 的 src 映射（`settings-defaults.ts` 新依赖）与
  `quickjs-wasi` 桩（`pi-codemode` 的 index 会拉进 `runtime/vm.ts`，语料只用声明面，不构造沙箱）。
- `CodemodeCorpusTests`：`IsCodemodeTool_UsesSchemaIdentity` → `ReachesTools_MatchesTypeScript`；
  新增 `DirectlyExposedMcpToolsTellScriptsWhatTheyResolveTo`（走 `PrepareLoadout` 的
  `Descriptions`，对应上游 `agent-session-mcp.test.ts` 那条）与
  `NamespaceLookupUsesTheAliasesInsteadOfTheDollarSuffixRule`。
- `ToolSearchCorpusTests`：`IsToolSearchTool_UsesSchemaIdentity` → `ReachesTools_IsAll`。
- `McpExtensionTests`：`ScriptNeedsServer_*` → `ReachesServer_*`，另加 `HasReachingTool_*`。
- `McpToolsTests`：schema 用例拆成三条（内容块/`_meta` 缺席/`additionalProperties`、`$ref` 下沉重定位、
  无 schema 时 any）。
- `Pi.Codemode.Tests`：`DetectsMcpCallToolResultStructuredContent` → `RendersAnMcpResultSchemaAsAPlainObject`，
  `McpPreambleIsExportedVerbatim` → `DeclarationLimitsAreExported`。

### 6.k 第二十批（durable 观察面重构：显式退休与 watch 类型入 chord）落地明细

对应上游 `92ab53602`（22 文件 / +482 −473；本条只做其中的观察面部分——`Storage.commit(writes,
adopt, context)` 的采纳回调已在第三批落地，`chord traceIn` 仍与 telemetry 一并待办）。

**问题**

`Session.documentState()` / `Conversation.viewState()` / `Harness.taskGraph()` 这套「把观察者包成
`ReplicatedStateSource` 再挂成只读复制状态」的 API 与 `watchDoc()` / `watch()` / `watchTaskGraph()`
重叠，而且退休只能从「投递的值是 null」推断：一个合法的 null 值会被误判成退休。同时一个已停止的
watch 仍持有指向 Session 的 detach / report / listener，Session 因此无法被回收。

**chord**（新增 `src/Pi.Chord/Services/Watch.cs`）

- `WatchEnd` 与 `IWatchHandle<T>` 迁到 chord（端口命名空间 `Pi.Chord.Services`），durable 不再定义；
  六个变体：`Stopped` / `Cancelled` / `Retired` / `Ended`（原 `SessionClosed`）/ `ListenerError` /
  `Failed`（原 `SessionFailed`，端口此前缺这个变体，本次补上）。
- `Pi.Chord.Services` 里 `Context` 会先命中命名空间 `Pi.Chord.Context`，文件内用
  `using ChordContext = Pi.Chord.Context.Context;` 消歧。

**Observation.cs**

- 删除 `CommittedStateSource<T>` 与 `SessionSourceAttachment<T>`（两者只为
  `documentState()` / `viewState()` / `taskGraph()` 服务）：`MaxPendingWatchFrames` 与
  `RetirementOperations` 上移到 `CommittedWatch<T>`。
- `CommittedWatch` 构造改为 `(value, detach, report = null, replace = null)`：监听器抛出时先
  `report(error)`（Harness 把观察者错误转给宿主），再以 `ListenerError` 结束。
- 帧带 `Retires` 标记；`Retire(context)` 入队「值 null + `[["r", null]]` + Retires」，投递之后
  才以 `Retired` 结束；`Advance` 不再自己判 null。
- `CloseSession()` → `End(failure?)`：无 failure 走 `Ended`，有 failure 走 `Failed`。
- `Terminate` 现在是唯一的终结点：一次性清空 `_detach` / `_report` / `_replace` / `_listener` /
  待发帧与取消注册，再脱钩并落定 `Closed`（`_resolved` / `FinishIfReady` 随之取消）。

**Session.cs**

- 删除 6 个 `DocumentStateAsync` 重载与 `DocumentStateCoreAsync`；`AttachDocument` 不再收
  `create` 工厂，直接造 `CommittedWatch`，`change.Value is null` 分支改走 `observer.Retire(context)`。
- `ObservedOperations` 去掉 `change.Value is null → RetirementOperations`：退休不再借用变更操作。
- 新增 `virtual void Report(Exception)`（普通 Session 丢弃，`HarnessImpl` 覆盖为 `options.OnReport`）
  与只读 `Failure`（当前 `_poison`），供观察者在关闭时拿到 `Failed` 的负载。

**Harness 层**

`ConversationViews.StateAsync` / `TaskGraphView.StateAsync` / `ConversationImpl.ViewStateAsync` /
`HarnessImpl.TaskGraphAsync` 与 `IDocumentReader` 的 6 个 `DocumentStateAsync` 全部删除；
`ViewObserver.CloseSession()` → `End(Exception? failure)`；观察者与关闭分发里的
`CommittedStateSource` 分支删掉，`CommittedWatch` 的构造分别传入 `_session.Report`。

**Pi.CodingAgent.Experimental（端口自留实现）**

上游在执行这条改动**之前**（`7472cda0d`）就删掉了整套实验性 server/client 栈，所以 `documentState()`
等移除在上游没有对应的改写可以照搬；端口保留该栈，于是在
`src/Pi.CodingAgent/Experimental/WatchState.cs` 用一个 `IReplicatedStateSource<T>` 适配器把
watch 重新挂成 `AttachedReplicatedState<T>`（快照取 watch 当前修订，帧按投递顺序自增游标，处置时
`watch.Stop()`）。`ModelsProvider` / `TranscriptProvider` / `ExperimentalRuntime` /
`SessionWorkerEntry` 与 `ClientTuiTests` 的夹具改用 `WatchDocAsync` / `WatchAsync` /
`WatchTaskGraphAsync` + `WatchState.Attach`。

**测试**

`SessionTests` 新增两例钉住新语义：`WatchEndsRetiredOnlyOnItsSourcesRetirementNeverOnANullValue`
（`advance(null, [["r", null]])` 只是又一个值，仍继续投递、不以 `Retired` 结束）与
`WatchRetireDeliversTheRetirementFrameThenEndsRetired`（`retire` 投递退休帧后才结束、之后不再有帧）。
`HarnessViewTests` / `HarnessTaskGraphTests` / `HarnessContextTests` 按上游同批测试的写法从
`state()` / `taskGraph()` 迁到「启动过的 watch」：`Start` 之后 `Value` 才随提交推进，未启动的
watch 读到的就是取得的那个修订；`Dispose()` 一律换成 `await Stop()`。


### 6.j 第二十一批（`--model` 的 provider 选择策略统一）落地明细

对应上游 `d7f7e0dfe`（1.3.0 / #10160，closes #10913；7 文件 / +663 −781，其中
`model-resolver.ts` 占 735 行）。破坏性：`findInitialModel` 不再解析 CLI 参数。

**问题**

四条入口——`--model`、`--models` / `enabledModels`、交互式 `/model`、`pi auth print`——各有一套
provider 选择逻辑，且都受目录顺序影响：同一个 id 落在多个 provider 时，「谁先被遍历到」决定结果。
`provider/id` 前缀还会在模糊匹配阶段被重新解释，于是 `:<thinking>` 后缀能把解析导向另一个 provider；
精确匹配有歧义时又会退化成模糊匹配，选出一个用户没写过的模型。

**统一策略**（`ResolveModelReference`）

- 先收集全部候选，再决定 provider：
  - 同一 provider 的多条候选 → 直接取第一条（同 provider 不存在「选错 provider」问题）；
  - 命名 provider 可用 → 命名 provider 胜出（`provider/id` 的通常语义）；
  - 只有一个非命名候选可用 → 它胜出（命名 provider 服务不了这个请求，例如网关把 `zai/glm-5` 当裸 id 暴露）；
  - 都不可用但存在命名候选 → 仍取命名候选，让 `--api-key` 能完成首次配置；
  - 否则报歧义（`Ambiguous`，带候选列表与可用计数），绝不按目录顺序挑一个。
- `ResolveText` 分三段：精确/前缀匹配 → 模糊匹配 → thinking 后缀递归。后缀不能改变 provider：
  递归只解析 `<base>`，把 `thinkingLevel` 贴回结果，`warning` 存在时不贴（内层已经降级）。
- 非法后缀在 `strict` 模式（`--model`）下整体失败，`lenient` 模式（`--models`）下「用默认 thinking、
  保留 model」并附警告，文案与上游逐字一致。
- `NotFound` 现在带 `Target`（provider、该 provider 的模型列表且 anchor 在前、按原样键入的
  `modelText`、去掉合法后缀的 `modelId`、`thinkingLevel`），自定义 model id 由它构造，
  不再靠「warning 里有没有 Using custom model id」这种字符串比对。

**连带的破坏性变更：`findInitialModel` 不再看 CLI**

上游把 `--provider` / `--model` 的解析整体上移到 `main.ts`（同批给 `resolveCliModel` 补了
`inlineApiKey: Boolean(parsed.apiKey)`）。端口照此删掉 `InitialModelOptions.CliProvider` /
`CliModel` 与该分支，`FindInitialModelAsync` 现在只有 scoped → 保存默认 → 第一可用 → 无 四步。
生产调用点本来就分两段（`Cli/Main.cs` 先 `ResolveCliModel`、`SessionWorkerHarnessSetup` 同），
所以只有测试需要改。随之退休的还有端口差异 **C78**：不再需要
`ModelResolutionException` 去模拟「打印红字后 `process.exit(1)`」，该类已删除；
`--model` 解析失败由调用方写诊断（`--provider` 缺 `--model` 的检查仍在 `buildSessionOptions` 里）。

**数据同步**

`defaultModelPerProvider` 有 5 项落后于 1.4.0（`github-copilot`、`xiaomi` 与三个
`xiaomi-token-plan-*`、`nvidia`），语料重算时逐条暴露，已按上游表更新。

**语料生成器（重建）**

`tools/gen-coding-agent-model-resolver-corpus.mjs` 早先丢失（`tools/` 被 `.gitignore` 排除，从未提交），
本次重写：把上游 `model-resolver.ts` 的**原文**放进暂存文件，只替换 import 头（`chalk` / `minimatch`
用真实包，`modelsAreEqual` / `isValidThinkingLevel` / `DEFAULT_THINKING_LEVEL` 按上游语义补三个桩），
再用 Node 22 的类型擦除直接 `import` 执行——因此期望值仍是参考实现自己的输出。
向量的**输入**（模型集、授权集、pattern、CLI 选项组合）从既有语料读入，只重算输出，重跑幂等。

**语料差异**（相对上一版，即上游语义变化）

| 段 | 变化 | 代表 |
| --- | --- | --- |
| `parsePatterns` | 50 / 1206 | `""` → 不解析（上游 `if (!text) not-found`）；`"claude-sonnet-4-5 :high"`（空格在冒号前）→ 不解析（新实现不再逐段 trim） |
| `exactMatches` | 4 / 270 | `mixedTypes` 集里同 id 的 chat / image / classifier 三条：旧实现要求「唯一匹配」→ `-1`，新实现同 provider 取第一条 → `0` |
| `scopes` | 9 / 279 | 同 id 多 provider 现在追加一条 `ambiguous` 诊断 |
| `initialModels` | 3 / 24 | CLI 字段不再参与（`anthropic/nope` 不再造出自定义模型、未知 provider 不再报错） |
| `cliModels` | 0 改 / +1440 条 | 新增 `inlineApiKey` 维度：原 1440 条即 `false` 分支，期望值未变 |
| `defaultModelPerProvider` | 5 项 | 见上 |


### 6.i 第二十二批（System One 分类器图像输入）落地明细

对应上游 `7f225f25d`（1.4.0；19 文件 / +687 −36，其中 `packages/ai` 的 `system-one-shared.ts` 占 110 行）。
`ClassifierContext` 自 1.4.0 起可带 `images`，三个 System One 服务各有自己的线上形态，由新增的
`SystemOneCompat` 选择。

**为什么不是「加上 images 就完了」**

三家服务的接收方式互不兼容，写错不会报错、只会静默失效：

| `imageInput` | 形态 | 服务 | 约束 |
| --- | --- | --- | --- |
| `assets` | `assets` 表 + 状态前插资源名（`state: ["<image-1>", …]`） | TypeSafe、Workers AI 上的 Jev | 仅 PNG / JPEG；TypeSafe 还要 `TypeSafe-Danger-Zone-Images: true` |
| `images` | 顶层 `images` 的 data URL 列表 | Workers AI 上的 Clef | PNG / JPEG / WebP，最多四张 |
| `state-image-urls` | 状态前的 `image_url` 片段 | OpenRouter | 任意格式，由 OpenRouter 按模型校验；OpenRouter 对 `assets` 应答 200 但丢弃 |

**ai 层**

- `ClassifierContext.Images`；`SystemOneWireRequest` 从记录变成类（`State` 可能是数组，另有
  `Assets` / `Images`），`ToJsonObject()` 按 wire 顺序投影，两个 transport 的 payload 都改用它，
  避免靠手抄字段而漏掉新键。
- `SystemOneTransport.DetectCompat`：TypeSafe 按 provider id（`openrouter`，能躲过 baseUrl 覆盖）
  或主机名（`openrouter.ai`，覆盖区域端点）判定；Workers AI 按 `@cf/cloudflare/clef` 前缀判定。
- `compat` 逐字段覆盖检测值（`model.compat?.imageInput ?? detected.imageInput`），因此只改头不改形态
  是合法写法。
- 校验先于发送：模型 `input` 不含 `image` 报 `${label} does not support image input for ${id}`；
  格式不支持报 `${label} accepts PNG and JPEG images for ${id}, got ${mimeType}`；Clef 超四张报
  `accepts at most 4 images`。三条文案与上游逐字一致。
- `llama-cpp-classify` 现在显式拒绝图片（此前端口没有 `Images`，等于静默忽略）：
  `${label} classification does not support image input`。

**coding-agent 层**

- `ProviderClassifierModelConfig.Compat` 与 `ModelConfig` 的 `SystemOneCompatSchema`：`models.json`
  的 provider 级 `compat` 现在也落到分类器模型上（图片模型仍然只吃 `baseUrl`——分类器的 compat 词汇
  与它无关）。合并是浅合并（后者覆盖前者），与 TS 的 `{...model.compat, ...config.compat}` 一致。
- 端口此前把 provider 级 `compat` 当纯 chat 概念，`ApplyModelsJson` 只对 chat 合并；本批改成三分支。

**测试**

新增 `tests/Pi.Ai.Tests/Api/SystemOneImageTests.cs`（14 例）覆盖：三家的形态与状态改写、opt-in 头
的有无、格式与张数上限的三条错误文案、provider id 与主机名两条 OpenRouter 检测路径、compat 逐字段
覆盖、compat 指定形态（代理场景）、以及 llama.cpp 的拒绝。`CoreProviderComposerTests` 新增一例
钉住「provider 级 compat 落到分类器、且不影响图片模型」。

### 6.l 第十九批（`before_agent_start` 覆盖自定义触发轮次）落地明细

对应上游 `a447ba11f`（1.2.0 / #10739，closes #10267 / #5581 / #10639 / #10289，partly #10606；
11 文件 / +312 −92）。破坏性：`BeforeAgentStartEvent` 从单一形状变成按 `messageType` 的联合。

**问题**

`sendMessage(..., { triggerTurn: true })` 起的那一轮不走 `before_agent_start`。首次工具调用之后
的 next-turn 刷新因此退回基础提示词选项，把处理器加进去的段落「补丁掉」——运行途中改系统提示词，
provider 的提示词前缀缓存随之失效。

**事件面**（`ExtensionEvents.cs`）

- 新增 `BeforeAgentStartTrigger`（`User(prompt, images)` / `Custom(message)`）：runner 对每个处理器
  重新渲染系统提示词，所以只收「触发消息」，事件在循环里现造。
- `BeforeAgentStart` 改为抽象基类（带 `SystemPrompt` / `SystemPromptOptions` / 抽象 `MessageType`），
  两支：`BeforeAgentStart.User`（`prompt`、`images`，`messageType == "user"`）与
  `BeforeAgentStart.Custom`（`message`，`messageType == "custom"`）。这是文件里既有的
  「一个抽象记录 + 嵌套变体」约定，`switch` 模式匹配替代 TS 的类型收窄。
- `Custom` 的负载用全限定 `Pi.CodingAgent.Core.CustomMessage`：嵌套作用域里
  `ExtensionEvent.CustomMessage`（会话边界草稿）会先命中，属于同名冲突而非笔误。

**runner**（`ExtensionRunner.EmitBeforeAgentStartAsync(trigger, options)`）

按 trigger 造出对应变体再交给处理器，其余（累积消息、折叠 `systemPrompt` 覆盖）不变。

**会话**（`AgentSessionPrompt.cs`）

- `_runAgentPrompt` 拆成三段：
  `StartRunAsync(trigger, onStarted)`（置忙、`_agentRunAbortRequested = false`、准备、冲刷待发消息、
  运行）+ `PrepareRunAsync(trigger)`（压缩检查、`before_agent_start`、装载、起始消息装配）+
  `RunLoopAsync(messages)`（原来的请求/后处理循环）。
- 置忙提到准备的**最前面**：处理器运行期间发来的消息进本轮待发队列而不是另起一轮；期间请求的中止
  在运行真正开始后立刻生效（`HandleAgentEventAsync` 在 `agent_start` 上补 `_agent.Abort()`）。
- 准备抛异常时复位 `_isAgentRunActive`、冲刷待发消息、`ResolveIdleWaitIfIdle()`，再把异常抛出去。
- `Trigger.Custom` 时**不**再造一条用户消息：自定义消息本身就是本轮起始消息。
- `RunAgentPromptAsync` 保留为「消息已备好，直接跑」的入口（`StartRunAsync` 之外的老语义），
  `AgentSessionBuild` 的 `triggerTurn` 分支与 agent-settled 延迟动作都改走
  `StartRunAsync` / `SendCustomMessageAsync(message, options)`——后者让延迟的那次也重新走一遍准备。

**随包资源**

`Assets/docs/extensions.md` 补「启动期间算忙」一句与 `messageType` 说明；
`Assets/examples/extensions/plan-mode/index.ts` 的处理器改成 `if (event.messageType !== "user") return;`
（后台任务通知触发的那一轮不该重复注入计划模式上下文）。

**测试**

`AgentSessionRuntimeTests.BeforeAgentStartFiresForRunsStartedByCustomMessages`：`PromptAsync` 得到
`messageType == "user"` 且 `prompt` 为原文；随后 `SendCustomMessageAsync(..., TriggerTurn = true)`
得到 `messageType == "custom"`、`Message.CustomType` 相符，且该自定义消息就是本轮起始消息。
`ExtensionRunnerTests` / `McpExtensionTests` 的三处调用改成传 trigger。

### 6.h 第二十三批（TUI：CJK 标点相邻的粗体/斜体）落地明细

对应上游 `packages/tui/src/components/markdown.ts` 的两行加上一个新依赖（1.4.0 / #10730，修 #10154）：

```ts
import markedCjkFriendly from "marked-cjk-friendly";           // 0.1.2
markdownParser.use({ tokenizer: { emStrong: markedCjkFriendly().tokenizer?.emStrong } });
```

**问题**

CommonMark 用「前后是不是标点/空白」判定分隔符能否开合。中文的句读几乎全是 Unicode 标点
（`。` `P*`、`：` `P*`、`「」` `Ps/Pe`），而紧跟其后的汉字是 `Lo`（字母类，不是标点也不是空白），
所以 `**这是测试。**后面` 的收尾 run 落在「前是标点、后是字母」——按规范既不是左邻接也不是右邻接，
em/strong 不成立，整段按字面渲染。这是中文界面上最常见的排版形态，属于肉眼可见的破版。

**落地**

- `Pi.Tui.Marked.MarkedCjk`（新）：包的 CJK 区间表（34 BMP + 30 星平面）作为唯一真源，
  `PunctuationCjk`、`EmStrongRDelimAst`、`EmStrongRDelimUnd` 三条正则由它拼出，
  `IsPrevCharCjk` / `IsCjkAdjacentToDelimiter` 两个逐码点判定走 `Rune`。
- `MarkedOptions.CjkFriendlyEmphasis`（默认 `false`）：`MarkedTokenizer.EmStrong` 据此分发到
  `EmStrongCjk`。`Markdown.ParserOptions` 打开它；CodingAgent 的 Mermaid 解析器不打开——上游
  `mermaid.ts` 用的也是裸 `new Marked()`，两边仍是「一个装、一个不装」。
- 只换 `emStrong`。`del` 依旧是 `StrictStrikethroughTokenizer` 的严格正则：`markdown.ts` 的
  `setOptions({ tokenizer })` 在 `use()` 之前就把整个 tokenizer 换掉了，扩展提供的 `del` 从未生效
  （`MarkedTokenizer.Del` 的注释早有这条结论）。

**算法差异**（相对 marked 自带 `emStrong`，四处）

1. 开合判断合并成一条：`nextChar` 为空 / `previous` 为空 / `previous` 是标点 / `previous` 是 CJK /
   `nextChar` 是 CJK，任一成立才进入收尾扫描（原版是「`g1||g3` 且 `previous` 非标点就放弃」）。
2. 收尾扫描换 CJK 规则集：`*` 用 GFM 变体（保留 `(?!~)` 守卫），`_` 用不带守卫的版本。
3. `left-only` 的 run 只有**不贴 CJK** 时才累加进开启总数；贴 CJK 一律按 "both" 处理，于是
   「前是标点、后是汉字」的 run 也能收尾 —— 这是本条修复的核心。
4. "both" 的 run 若 `previous` 与开启符同字符（`**` 前面紧挨 `*`），直接 `break` 结束扫描，
   不再折进开启计数。

**语料**

`tools/gen-tui-cjk-corpus.mjs` 驱动真实的 `marked@18.0.11` + `marked-cjk-friendly@0.1.2`
（临时工作区 `tools/_cjkws`，随 `tools/` 一起 gitignore），对 40 条输入各跑两遍：装覆盖（`cjk`）
与不装（`plain`），比对内联 token 树。`plain` 那一半专门钉「非 CJK 文本仍按 CommonMark 不闭合」
（`**end.**Then next`）。生成命令：

```
node tools/gen-tui-cjk-corpus.mjs > tests/Pi.Tui.Tests/markdown-cjk-corpus.json
```

没有重建整份 `markdown-corpus.json`：那 4,990 条是整条渲染管线（chalk 样式链、换行、内边距）的输出，
生成它的 harness 不在仓库里；本轮改的是 tokenizer 层，拿两个真实库的内联 token 树对照更贴近问题、
也不必复刻渲染栈。渲染这一侧只复刻上游 `markdown.test.ts:1658` 的 6 条断言（主题打印 `<b>`/`<i>`）。

**坑**

- 参考实现必须照抄 `markdown.ts` 的安装顺序：先 `setOptions({ tokenizer: strict })` 再
  `use({ tokenizer: { emStrong } })`。漏了 strict 那步时，`~~删除。~~后面` 的参考值会按 marked 自带
  的 flanking 版 `del` 变成字面量，而端口是严格正则 → 语料对不上。这是本轮唯一一次「参考实现与端口
  不同构」导致的失败，排查要点就是先确认参考实现装了哪些覆盖。
- 星平面码点进不了 .NET 字符类（详见 `docs/tui-porting-status.md` 的 T32）：正则只用 BMP 区间，
  逐码点判定用 `Rune` 全表覆盖。
- 别把语料条数写死：手列输入时数错成 41，实际 40，第一次跑就挂在计数断言上；改成
  `Assert.Equal(cases.GetArrayLength(), compared)`。

**验证**

构建 0 警告 0 错误；`Pi.Tui.Tests` 23881 / 23881（新增 3 例），`Pi.CodingAgent.Tests` 5020 / 5020。
既有的 4,990 条 markdown 差分向量全绿 —— 覆盖里本来就含 CJK/emoji 输入，说明这条覆盖没有把
「非歧义的中文强调」改坏。

### 6.g 第二十四批（终端能力探测补 Rex / Herdr）落地明细

对应上游四个提交：`b2363841a` + `78a3c10ba`（Herdr，1.4.0 / #10774）与 `0ed1b0431` + `396ecd7b8`
（Rex，1.4.0 / #11131），每个都是 `detectCapabilitiesFromEnvironment` 里的一段分支。

**为什么要排在外层终端之前**

Rex 与 Herdr 都是**套在别的终端里跑**的（Rex 是自己的终端应用，Herdr 是复用宿主终端的 pane），
宿主的环境变量——尤其是 `KITTY_WINDOW_ID`——会漏进它们的进程。原顺序里 `KITTY_WINDOW_ID` 分支在
最前面，于是这两个终端会被判成宿主，拿到"不确定"的能力组合。两个上游提交都特意把分支插在
`screen` 之后、`KITTY_WINDOW_ID` 之前。

**取值**

| 终端 | images | trueColor | hyperlinks |
| --- | --- | --- | --- |
| Herdr（`TERM_PROGRAM=herdr`） | `kitty` | `hasTrueColorHint` | `true` |
| Rex（`TERM_PROGRAM=rex`） | `kitty` | `true`（硬开） | `true` |

- Herdr 的 truecolor 仍跟随 `COLORTERM` 提示，与 tmux / screen 的处理一致。
- Rex **硬开** truecolor：它自己就支持 24 位色，不看 `COLORTERM`。图像也开——Rex 只实现了
  Kitty 协议的一部分（没有 Unicode placeholder），对 Pi 够用；`0ed1b0431` 当时先留 `images: null`，
  同一天被 `396ecd7b8` 改成 `kitty`，端口直接落到最终形态。
- `PI_HYPERLINKS` / `PI_IMAGE_PROTOCOL` / `PI_TRUE_COLOR` 的覆盖逻辑在 `DetectCapabilities` 里，
  位于探测之后，所以 Rex 的硬开 truecolor 也能被 `PI_TRUE_COLOR=0` 关掉。

**测试**

`TerminalImageCapabilityTests.DetectCapabilities_RecognisesRexAndHerdrBeforeLeakedKittyVariables`：
Rex 带 `KITTY_WINDOW_ID=1` 仍判 Rex（hyperlinks/kitty/truecolor 全开）、`COLORTERM=256` 不影响
truecolor、`PI_HYPERLINKS=0` 能关掉超链接；Herdr 带 `KITTY_WINDOW_ID=1` 拿到 kitty 图像但
truecolor 为假（无 `COLORTERM`），补上 `COLORTERM=truecolor` 才变真。

### 6.f 第二十五批（OSC 7501 程序状态上报）落地明细

上游 1.1.0 / #10607 的一半：让程序把「空闲 / 忙碌 / 等用户 / 完成 / 失败」告诉终端。
协议本体是 `packages/tui/src/program-status.ts`（53 行），其余是接入：`terminal.ts` 的探测与
重发、`stdin-buffer.ts` 的回复头、`coding-agent` 的 `program-status-reporter.ts` 与交互模式接线。

**协议**（`src/Pi.Tui/ProgramStatus.cs`）

- 线上格式 `ESC ] 7501 ; key=value:key=value ESC \`，键序 `state` → `app` → `kind` → `msg`。
- `kind` 只在 `state=blocked` 时输出；`app` 必须匹配 `[A-Za-z0-9_.+-]{1,32}`，不匹配就整段省略
  （不是转义，是丢弃）。
- `msg` 是 base64 的 UTF-8，解码后最多 2048 字节，按**码点**截断（对应 TS 的 `for…of`）；
  控制字符 `[\u0000-\u001f\u007f-\u009f]+` 每段替换成一个空格再 `trim()`——终端会丢弃带控制字符的
  报告，所以是替换而不是转义。
- `IsReply` 匹配 `ESC ] 7501 ; ?` 开头、BEL 或 ST 收尾的回复；`?` 之后可以再带键值对（后续修订）。

**终端**（`Terminal.cs`）

- `ITerminal.SetProgramStatus` 带默认空实现：测试里的假终端不需要跟着改；`StringTerminal` 覆写成
  「记录 + 写出」，供状态上报器的用例断言。
- `ProcessTerminal.Start()` 发查询并把 `PI_PROGRAM_STATUS` 落到 `1`（声明支持、不查询）/ `0`
  （完全不碰）/ 未设（查询）三态；`SetProgramStatus` 只在确认支持后写，`Stop()` 先发 `clear`
  再复位，`Start()` 重发最后一条。
- **偏差（回复装配）**：上游在 `stdin-buffer` 里把终端回复当整条序列投递，端口这个 reader 是
  `Console.ReadKey` 逐键的，回复会拆成 `ESC` + `]` + 其余。因此 `TryConsumeProgramStatusReply`
  在「查询未落地」期间对 ESC 做一次 30ms/64 字符的装配：命中回复就吞掉并翻支持标志；命中 DA1
  （`ESC [ ?`）就把待定标志收掉；其余情况把装配结果**原样**交给输入处理器，不丢键。窗口只在启动后
  那一次查询期间打开，稳态下不参与输入。
- `stdin-buffer.ts` 的 `TERMINAL_REPLY_HEADER_PATTERN`（把 `7501;?` 加进回复头）没有跟：端口的
  `StdinBuffer` 尚未接进 `ProcessTerminal`，这一项要等那条路径落地时一起改。

**交互模式**（`Modes/Interactive/ProgramStatusReporter.cs`）

- 状态推导与上游同构：`blocked`（最近打开的对话框）> `compacting`（固定文案 `Compacting context`）
  > `working`/`resting`；`working` 与 `done` 带会话名，`error` 带错误首行，`idle` 什么都不带；
  `app` 一律取 `Config.AppName`。
- 事件映射：`agent_start` 清结果、`message_end` 只看助手消息（`stopReason=error` → error，否则 done，
  后一条覆盖前一条所以重试成功会顶掉错误）、`compaction_start/end`（运行中的失败/中止改写本轮结果，
  运行外的 `manual` 压缩落到 done/error）、`agent_settled`（中止回到 idle，否则用本轮结果）、
  `session_info_changed` 只触发一次重报。
- 去重按「线上可见字段」做键（等价于上游的 `JSON.stringify` 比较）；`SetBlocked(source, …)` 后写
  覆盖先写，与上游那个有序 `Map` 一致。
- 接线：启动后 `Report()`、事件分发最前面 `HandleEvent(event)`、`RebindCurrentSessionAsync` 里
  `Reset()`、扩展对话框（选择/确认/输入/编辑器）统一用 `extension-dialog` 这一个 source 上报
  「栈顶」的阻塞态（第二十九批改成对话框栈后与上游一致；此前三个对话框各用一次性 key）、登录流程按
  `{kind:"auth", message:"Log in to <provider>"}` 标阻塞（`LoginProviderAsync` 因此多收一个
  `providerName`）。自定义组件的对话框不上报——上游那一处 `pushExtensionDialog(c)` 也没传 blocked。

**测试**

`ProgramStatusTests`（13 例）：格式（键序、`app` 白名单、`kind` 只在 blocked）、控制字符替换与 trim、
2048 字节按码点截断（4 字节与 3 字节两种边界）、`IsReply` 的 8 条正反例、三个枚举的线上拼写。
`ProgramStatusReporterTests`（9 例）：启动 idle、运行 working + 会话名 → settled done、错误首行与重试
覆盖、中止回 idle、压缩文案与运行内/外分支、阻塞优先级与替换/清理、去重与 reset、无关事件不重报。

### 6.e 第二十六批（`showHardwareCursor` 假光标）落地明细

上游 `1cedd3272`（1.2.0）的动机：把「光标那一格」的绘制权从组件收回到渲染器。此前
`Editor` / `Input` 直接写 `ESC[7m…ESC[0m`，`showHardwareCursor` 打开时只能靠终端把硬件光标叠在
同一格上；而某些终端（边缘单元格颜色会渗进窗口 padding）会让这一格的反显被拉长到行尾。改成
「组件只标记、渲染器决定」之后，硬件光标模式下渲染器直接**丢掉**聚焦那一格的反显。

**端口落点**（`src/Pi.Tui/Tui.cs`）

- `Tui.FakeCursorStart = "\x1b_pi:fc\x07"` / `Tui.FakeCursorEnd = "\x1b_pi:/fc\x07"` /
  `Tui.RenderFakeCursor(text)`，与 `CursorMarker` 并列导出的零宽 APC 标记。
- `TuiBase.ResolveFakeCursors(string[])`：逐行短路（不含起始标记就原样返回）；`_showHardwareCursor`
  为真时先用 `FocusedFakeCursorRegex` 把 `CURSOR_MARKER + START + text + END` 折叠成
  `CURSOR_MARKER + text`（末尾被截断时 `END` 缺失也能匹配），再把 START/END 换成
  `\x1b[7m`/`\x1b[27m`。无 `CURSOR_MARKER` 的（未聚焦）假光标保持可见——与上游一致。
- 三个渲染入口各包一次：`TuiMainScreen` 的 `newLines`、`TuiAltScreen` 的 `DoRender` 屏幕行与
  `ExitAltScreen` 时的 `_lastDocument`、`TuiBase.CompositeOverlays` 里每个 overlay 的渲染结果。
  位置刻意与上游一致：都在 OSC 133 前缀剥离之后、搜索高亮/选择高亮/overlay 合成之前，
  只让后续逻辑看到普通 SGR。

**组件侧**

- `Editor.Render` 两处光标（行内字素 / 行尾补空格）改 `RenderFakeCursor(...)`。
- `Input.Render` 两处（占位符首字符 / 正文字符）改 `RenderFakeCursor(...)`。
- `TextLayout.ExtractSegments` 补上上游 `utils.ts` 的那条例外：在 overlay 右边界处，
  `\x1b_` 开头的零宽 APC 即使 `afterStarted` 还没置位也收进 `after`，这样 overlay 正好结束在
  光标左侧时 `CURSOR_MARKER` 不丢（`KeepsTheCursorWhenAnOverlayEndsDirectlyLeftOfIt` 两种模式都钉住）。

**偏差与代价（T33）**

- 组件输出从「内联 SGR」变成「标记」，因此**所有内嵌 Input/Editor 渲染的语料**都会失配。
  这批语料（`editor` / `input` / `extra-components` / `alt-screen-search`）的生成器不在仓库里
  （配方在 `docs/tui-porting-status.md`，`gen.mjs` 未入库），无法按新源码重录，改由测试侧
  `LegacyCursorEncoding.Resolve` 把标记形式还原成录制时的字节再比对。对 `Input` 及其派生组件
  （设置列表、搜索框）这一步是**无损**的：`ResolveFakeCursors` 产出的正是 `\x1b[7m`/`\x1b[27m`。
- 唯一有损的是 `Editor`：它的光标格以前以 `\x1b[0m`（全重置）收尾，现在是 `\x1b[27m`（只关反显），
  这是上游刻意的改动，`LegacyCursorEncoding` 用 `EditorLegacyEnd` 单独映射掉，并在测试注释里点明。
- 上游 `tui-alt-screen.test.ts` 的「选择高亮跨过假光标」用例没有逐字移植：端口没有 SGR 鼠标注入的
  测试基建。等价保障是 `AltScreenResolvesFakeCursorsBeforeCompositing`（屏幕行里只有 `7m/27m`、
  没有 `\x1b_pi:`）+ `ApplySelectionHighlight` 在每条 SGR 之后重新补 `\x1b[7m` 的既有实现。
- 后续修正 `21d1dcd4d` 一并跟上：`LoginDialogTests.RenderDialog` 先剥 APC 再 `StripAnsi`
  （`stripAnsi` 不管 APC，不剥的话 `> value` 的整行比较会失配）。

**测试**

`FakeCursorTests`（6 例）：聚焦 Input 画反显、硬件光标模式下丢反显且不泄漏标记、未聚焦 Input 在
硬件光标模式下仍画、裁剪切掉 END 时仍然丢、overlay 结束在光标左侧时两种模式都保留（硬件光标列
固定为 4）、alt screen 在合成前解析。语料面 `Pi.Tui.Tests` 23901 / 23901、`Pi.CodingAgent.Tests`
5029 / 5029 全绿。

### 6.aq 第四十八批（调度器所有权索引：不再从每条存活记录上溯）落地明细

上游 `cb4375493` + `ea448f454` 是同一条性能线的两半：**每遍 reconcile 不再遍历全部存活任务**。
端口的问题形状不同但同源 —— `OwnedLive()` 每次都**物化整张所有权表**：从每条存活记录上溯到根，
把「属主 → 其名下的自有工作」全算一遍。一条长度为 n 的持有链因此是 O(n²)（物化出的对数本身就是
n² 级），而 `LoadScopes` 里每条记录再走一遍 `ChainKnown` 上溯，又叠一层 O(n × 链深)。

**先量化再改**（临时基准，测完删除；`InspectAsync` 内部就是 `LoadScopes` + 所有权计算）：

| 链长 n | 改前（3 次 Inspect） | 改后（3 次 Inspect） |
| --- | --- | --- |
| 100 | 7 ms | 0 ms |
| 200 | 28 ms | 0 ms |
| 400 | 122 ms | 0 ms |
| 800 | 296 ms | 2 ms |
| 1600 | 1224 ms | 4 ms |

改前是干净的二次增长（n 翻倍约四倍耗时）；改后线性。

**改法（对应上游 `#below`，但按需构建、不做增量维护）**

1. **向下索引** `BuildChildren(overlay)`：父节点（任务或对话）→ 子节点。节点集合是
   `_settled`（行走经过的终态任务）∪ `_live` ∪ 本次提交的候选；边集合是 `_edges` ∪ overlay 的边。
   每遍 reconcile / 每次 Inspect 建一次，O(节点 + 边)。
2. **单点查询** `OwnedLiveBelow(task)` / `HasOwnedLive(task)`：只走该任务的子树，穿过对话与终态
   属主，后台任务只算它自己、不再往下（其下工作归它，不归更上面的属主）。`HasOwnedLive` 命中即
   返回，所以「有没有自有工作」在典型情况下是 O(1)。
3. **替换全部调用点**：`FinalizeAsync`（逐条 completing 判断）、reconcile 的候选循环（只对已标
   abort 的记录查）、`InspectAsync`、`MarkAbort`、`TerminateAsync`、终态提交、等待环检测。
   `WaitingOn` 的第二个参数从「整张表」改成「该记录名下的工作列表」。
4. **`ChainKnown` 正结果缓存** `_knownChains`：属主边只会被加载、不会被忘记，所以「已知」不会变假；
   任务变成终态离开 `_live` 时把它的节点从缓存里删掉（它不再是已知节点，除非是对话属主留在
   `_settled`），否则会谎报已知、跳过 `LoadChainAsync`。带 overlay 时不缓存（暂存边）。

**没有照搬的部分**：上游把 `#below` 做成增量维护的长期索引，并按存活工作收缩（`ea448f454`）。
端口改成「每遍重建 + 正结果缓存」：每遍 O(n) 的构造成本远低于原来的 O(n²)，且没有索引失效的
一类 bug；代价是每遍多一次线性构造。任务数在上万级时若仍嫌贵，再往增量维护走。

**验证**：`Pi.Durable.Tests` 507 / 507（含 completing 的终结、abort 自下而上、后台任务边界、
等待环等既有用例，全部无回归）。基准数据仅作记录，未留成测试 —— 计时断言在不同机器上不可靠，
真正的保证是「不再有从每条记录上溯的路径」。

### 6.ap 第四十六批（documentRetentionMs：文档缓存按使用量回收）落地明细

上游 `f44707e7f`：Session 把每个加载过的文档留在内存里直到进程结束；触及的对话越多，内存只增不减。
端口 `Session._documents` 正是这个形状（只在关闭与 `UnloadDocumentsAsync` 时清空），本批按上游补齐。

**1. 记账结构**

- `_idleDocuments`：没有观察者的已缓存文档，**按最后使用时间从旧到新**。上游用 `Map` 的插入序，
  C# `Dictionary` 不保序，故用 `LinkedList<IdleDocument>` + `Dictionary<string, 节点>`。
- `_attachedDocuments`：化身（记录 ID）→ 观察者计数。被观察的化身**永不驱逐**。
- `_clock` / `_documentRetentionMs`：都是每次使用时读取的委托；两者抛出都不得改变操作结局，
  因此各自包一层 try/catch 回落到「现在」与缺省值。

**2. 使用与驱逐**

- `TouchDocument`：缓存命中、`Install`、`LoadDocument` 命中与新建后各记一次。热文档有「颗粒」节流
  ——`min(1 秒, 保留时长 / 10)` 之内不重复后移，避免提交密集的负载每次重排（代价是可能提前一个
  颗粒被驱逐，一次重载）。
- `SweepDocuments`：在**提交线上**把 `since <= now - retention` 的空闲文档从旧到新丢掉，然后排下一次。
  调用点是 `RunCommitAsync` 的四个出口：结算失败、结算抛错、无写入、发布之后。
- `ScheduleDocumentSweep`：为最旧空闲文档的到期时刻排一个后台定时器（`System.Threading.Timer`，
  不拖住进程退出，等价上游的 `unref`）；保留时长为 0 时立刻排到线上；为
  `PositiveInfinity` 时不排——没有东西会过期。上游「定时器不可用就只靠提交驱逐」那条退路在 .NET
  不需要，故没有对应分支。
- 观察者：`AttachDocument` 在创建 watch 前钉住化身（失败则解钉），`detach` 与「化身退休」
  （`change.Value is null`）各解钉一次；解钉后若仍是同一个化身，重新成为空闲。
- **缓存访问统一进锁**：`SnapshotCoreAsync` 在变更线**外**读缓存，而驱逐在线上写同一个
  `Dictionary`——不锁的并发读写会让它进入未定义状态（这是原代码就有的缝，本轮加了写方之后更容易踩到）。
  现在所有读写都走 `CachedDocument` / `StoreDocument`（内部 `lock (_documentCacheGate)`），
  或本就在锁内的记账方法；`Clear()` 的两处（关闭、`UnloadDocumentsAsync`）也在同一把锁里。

**3. 设置与接线**

`HarnessSettings.DocumentRetentionMs` → `Settings.DocumentRetentionMs` →
`HarnessImpl` 构造时把 `() => options.Settings?.DocumentRetentionMs` 传给 `DurableSession`。
解析规则 `DurableSession.DocumentRetention(ms)`：非负毫秒（含 `0` 与 `PositiveInfinity`）照用，
其余（含 null）回落十分钟。`SetDefaultDocumentRetention` 与 `DocumentCacheSizes`（cached / idle /
attached / sweeps）供测试使用，对应上游同名入口。

**测试**（`DocumentRetentionTests`，+4）：解析规则；到期驱逐后冷加载拿回同一个值（缓存数 1 → 0 → 1）；
被观察的文档跨 10 秒仍不驱逐、`Stop()` 后重新空闲并被驱逐；`PositiveInfinity` 永不驱逐、
`0` 用完即弃。`Pi.Durable.Tests` 507 / 507（原有 503 例无回归——默认十分钟，测试里的假时钟推进
不会误驱逐）。

### 6.ao 第四十五批（codemode 声明约束；以及「到底还剩什么」的确切清单）落地明细

**① 工具声明补上 schema 约束**（`8a9c051f8` / #10707，本次移植）

端口 `CodemodeDeclarations.SchemaToType` 只把 `description` 渲染成属性注释，`minimum` / `pattern` /
`maxLength` 这类 TypeScript 类型表达不出来的约束全部丢失 —— 脚本作者（和模型）看不到取值限制。
按上游补齐：

- 新增 `@integer` 与 11 个标签关键字（`minimum` / `exclusiveMinimum` / `maximum` /
  `exclusiveMaximum` / `multipleOf` / `minLength` / `maxLength` / `pattern` / `format` /
  `minItems` / `maxItems`）、`@uniqueItems` / `@default` / `@deprecated`。
- 同时成立的下界只保留最大、上界只保留最小；`format` 这类非界关键字按值去重后各自成标签。
- 可选字段（Pydantic 的 `int | None`）把约束放在非 null 分支：属性与分支都读，界取交集。
- Draft-04 / OpenAPI 3.0 的布尔形式：`exclusiveMinimum: true` 表示 `minimum` 本身排他，标签改成
  `@exclusiveMinimum <原 minimum>`。
- 超过 80 字符的标签（大段 `default`、超长 `pattern`）整条略去；注释里不能出现的行终止符
  （`\r` `\n` U+2028 U+2029）转成 `\uXXXX`。
- 渲染预算：先带标签渲染，超长则退回不带标签的版本，仍超长才 `unknown`。

测试（`DeclarationTests`，+5，逐条对照上游 `declarations.test.ts` 的五个用例）：约束进注释、
可选字段读非 null 分支、Draft-04 布尔排他界、超长标签略去与行终止符转义、预算回退。
`Pi.Codemode.Tests` 57 / 57。

**② `sandbox.start`（`0fb1aee74`）：跟随 §6 第 7 条，不采纳**

它的提交信息写得很直白：「For durable code mode」——虚拟时钟、种子随机、按 seq 严格应用结果、
指纹上报，全部为了让 durable 的脚本能在重启后重放出同样的调用。没有 durable code mode 就没有
消费方，故与那一族一起不采纳；`execute()` 路径不受影响，端口现有的 codemode 不需要它。

**③ 复核结论：还剩什么**

这一轮把「只按功能名记录」的条目又过了一遍，除①外只确认了**一处**真缺口：

- ~~`f44707e7f` `documentRetentionMs`~~ —— **第四十六批已移植**：`_idleDocuments`（链表 + 索引，保证
  最旧在前）+ `_attachedDocuments`（化身 → 观察者计数）+ 提交线上的驱逐 + 后台定时器；
  端口 `Session._documents` 此前只增不清的问题到此关闭。见 §6.ap。
- **性能三条**：`68ccef176`（复用扫描区间）**已在端口里**（`ConversationContext.cs` 的
  `previous` 分支与上游同构）；`ea448f454` 与 `cb4375493`（调度器所有权索引）**第四十八批已落地**
  ——不再物化整张所有权表、不再每遍从每条存活记录上溯，见 §6.aq。

**当前口径（写在这里，避免再改口）**：**功能面已对齐**——`v1.0.4..v1.4.0` 里所有影响行为的提交
都已落地或判为不适用/不采纳（§6 第 7 条）。**性能面也已对齐到本条线上游的两个提交**：调度器的
所有权计算从「每遍物化整表、O(n²)」改成「向下查询 + 正结果缓存」，同一基准 1600 个持有任务从
1224 ms 降到 4 ms（§6.aq）。此处唯一保留的差异是索引**按需重建**而非增量维护，属实现选择，
不是缺口。

### 6.an 第四十四批（durable code mode 的取舍：不移植，附重启条件）落地明细

**结论**：不移植 `packages/durable/src/codemode/` 与 `tool-search/`。这不是「先放着」，是明确的
产品决策，理由三条（均可复核）：

1. **仓内零消费方。** `packages/durable/package.json` 只把它导出为 `./codemode` / `./tool-search`；
   `grep -rn "@earendil-works/pi-durable/codemode" packages/*/src` 零命中，`durable/src/index.ts`
   与 `harness/*` 也不引用它。
2. **真实产品路径走的是另一个包。** `coding-agent` 的依赖里是 `@earendil-works/pi-codemode`，
   而这一层端口已完整移植：`src/Pi.Codemode`（`ICodemodeVm` / `CodemodeProtocol` / 声明渲染 /
   `@options` 解析）+ `Extensions/Codemode`（2276 行，含 `tool_search`、`models`、`grammar`、
   `output`）+ `Core/NestedToolCalls.cs`（对应上游 263 行的 `nested-tool-calls.ts`）。
3. **成本与漂移。** 约 1,800 行沙箱集成（`codemode.ts` 1044 / `models.ts` 287 / `output.ts` 206 /
   `describe.ts` 157 / `tool-search` 99），为零消费方维护它是纯负担。

**顺带确认「剩下的 103 个提交里没有别的功能缺口」**：本批抽查了 20 余个行为点，覆盖 ai 的重试
文案表（`Retry.cs` 里 `unable to forward upstream`、`stream disconnected before completion`、
`upstream_stream_read_error`、`The service is temporarily unavailable` 等均在）、provider 端点
清单、llama.cpp 分类器；durable 的模型注入、按条目读上下文、`checkpointWhen`、命名空间存储、
任务计时、保留上下文、扫描顺序等——除第四十三批已修的两条外，未再发现落地缺失。

**没有做的事**（如实记录）：剩余提交里还有一批属于并行/性能（`ea448f454` 约束调度器索引、
`cb4375493` 调度器不再每遍遍历全部任务、`68ccef176` 复用扫描区间）与模型数据、provider 默认值、
测试/文档，本批只按功能名核对，没有逐条做性能对照。它们不影响「功能面对齐」，但也别把这一栏
写成「全部逐条验证」——这正是第四十三批撤回一次结论的原因。

### 6.am 第四十三批（覆盖率复核：Gemini 4 档位、等待环检测，以及审计口径的修正）落地明细

**① 为什么会做这一批**

前两批一直用「文档里是否出现哈希」来算差集。这一批先做了个更硬的统计：`v1.0.4..v1.4.0` 共 278 个
提交，文档里显式出现哈希的只有 **104** 个，其余 174 个是按功能名记录的，其中 **103** 个改动了
`packages/*/src`。抽查这 103 个，立刻命中两条真缺口，随后又发现一个整族的缺失（§6 第 7 条）。
因此 §7 里「全部提交均已逐条判定」这句结论已撤回。

**② Gemini 4 及以后要用离散档位而不是 token budget**（`1692bba63` / #10890）

`src/Pi.Ai/Api/GoogleShared.cs` 的 `UsesGoogleThinkingLevel` 只匹配 `gemini-3(?:\.\d+)?-(?:pro|flash)`，
于是 `gemini-4-argon` 落到 token budget 分支：Vertex 直接拒收 `thinkingBudget`，Gemini API 则忽略
所选档位。补一条 `gemini-(?:[4-9]|[1-9]\d)`，覆盖 4–9 与两位数版本。
测试：`GoogleGenerativeAiTests.ThinkingLevelHelpers` 增加 `gemini-4-argon`、`gemini-10-pro` 为真，
`gemini-2-argon` 为假（防止把 `gemini-2` 误判进来）。

**③ 等待图必须无环，而不是「不许等属主」**（`eb2fce43d` / #10411）

端口 `Scheduler.ValidateWaitAsync` 仍是旧规则：等待不能指向自己或自己的任一属主。上游把它换成
**等待图上的环检测**——边从每个任务指向它等待的任务，以及它存活的普通自有工作（spec §5.5）：

- 校验只把「非终结、且不是等待者自己拥有的任务」放进候选（活着的子任务已经握着等待者，等它
  不新增边；终结的任务没有边）；候选为空直接通过。
- `WaitCycleAsync` 从候选出发做广度遍历，同时沿「等待边」（`state.On`，abort 已标记的任务除外，
  因为 abort 自下而上跑）与「自有工作边」（`OwnedLive`）走，走回等待者即返回该环。
- 报错点名环：`Task B cannot wait on A: B -> A -> B is a wait cycle`。

这条修正的实际意义：旧规则会把「后台任务等待它所在对话的属主」这种合法情况一起拒掉，因为端口
没有区分「后台任务不计入属主的自有工作」。现在只有真成环才拒。
测试（`SchedulerTests`，+2）：`Scheduler_RejectsAWaitOnItselfAsACycle`（自等 → `A -> A`）、
`Scheduler_RejectsAWaitThatClosesACycleThroughOwnedWork`（父等子、子回头等父 → `子 -> 父 -> 子`）。
两条在旧实现下都只会报「cannot wait on itself or its owner」，断言 `is a wait cycle` 即失败。

**测试**：`Pi.Ai.Tests` 512 / 512、`Pi.Durable.Tests` 503 / 503（+2）。

### 6.al 第四十二批（chord 范围收口：数组追加别拷两遍）落地明细

这一批不是新功能，而是把「chord 到底对齐到什么程度」说清并补上唯一能对上的那一处。

**① 范围判定（写进 §3.5 与 §6 第 6 条）**

上游 1.2.0–1.4.0 的 28 个 `packages/chord/src` 提交，绝大多数落在端口**从未拥有**的两块上：
对象运行时（`object.ts`/`cell.ts`/`declare.ts`/`contract.ts`/`registry.ts`/`publish.ts`，
protocol 3 的 `self.state({init})`、typed principal，protocol 4 的「引用先水合再使用」）与
跨进程链路（`link.ts`/`remote.ts`/`websocket.ts`/`connection.ts`/`cluster.ts`、帧压缩与流控、
`sqlite/` 复制）。端口 `Pi.Chord` 只有 delta / facets / services / context / node 五块，且
`src/Pi.Chord` 与 `src/Pi.Durable` 内没有任何 socket / websocket / IPC——这类改动的落点不存在，
判**不适用**而非「待办」。依据逐条列在 §6 第 6 条，便于以后复核。

**② `091bcc099`（append to arrays without walking or copying them twice）**

上游改了 tracker 与 immutable apply 两处。tracker 侧的 `orderedArrayPlan` 依赖 overlay/plan
机制，端口 `Tracker<T>` 是显式动词 API（`Set`/`Delete`/`Append`/`Truncate`/`Splice`/`Move`），
没有对应结构，跳过。apply 侧的「追加被拷两遍」在端口成立：

- `src/Pi.Chord/Delta/DeltaApply.cs` 的 `SpliceList` 原来是
  `new List<object?>(list)` → `RemoveRange` → `InsertRange`。追加（起点在末尾、`remove == 0`）时
  整表被拷一遍，`InsertRange` 越过容量再分配一次，等于拷两遍；中间插入还多一次整体左移。
- 改为按结果长度一次分配，再依次压入「前段 / 新项（深拷贝）/ 尾段」：一趟搬运、无移位、无扩容。
  ops、值、深拷贝语义与不可变语义都不变；`start` 越界仍抛 `p index beyond array length`，
  另补 `p index must be >= 0` / `p remove must be >= 0` 两道防御（`AssertValidOp` 已拦，这里
  只是让直接调 `ApplyOne` 时的失败信息与校验层一致）。

**测试**（`DeltaTests`，+2）：`AppendingOneItemAtTheEndKeepsTheBaseAndTheOrder`（前缀原样、新项
在末尾且是深拷贝、原数组不动、同一数组连续追加顺序不丢）、
`SpliceInTheMiddleKeepsTheTailAndClampsTheRemoval`（中间插入 / 替换 / `remove` 越过末尾时夹取 /
原值不动 / 起点越界仍报错）。

**③ 顺带修掉的既有缺陷：睡眠定时器被布两遍**

跑全量 `Pi.Durable.Tests` 时 `HarnessSleepTests` 稳定失败（4 次全量跑 4 次失败、单类跑 12/12 全绿）：
`Assert.Single(timers.Live, t => t.DelayMs == 500)` 报「有 2 个匹配项」，两个 500ms 定时器都没被拆。

- 根因是**移植带进来的并发**：上游 `#scheduleWake` 用 `queueMicrotask`，函数体在单线程事件循环里
  一次跑完，`#armTimer` 的「读旧定时器 → 拆 → 布新的」中间不会被插入。端口为了不阻塞调用方把它
  丢进 `Task.Run`（`Scheduler.ScheduleWake`），而函数体一进来就 `_wakeScheduled = false`，
  于是第二次 `Kick()` 可以再排一个任务体，两个线程同时走 `ArmSleepTimer`：都看到还没有定时器，
  各布一个并各自赋值 `_sleepTimer`，先布的那个再没人拆 —— 一次睡眠有两个定时器同时活着，
  醒来时会重复唤醒（另一例 `DisarmsTheSleepTimerWhen…` 表现为 10 秒超时）。
- 修法：`_sleepTimer` 的「读 / 拆 / 布」整段进 `lock (_sleepTimerGate)`（`Seal()` 的拆除也在同一把
  锁里），定时器回调里把清空动作单独加锁、`WakeDue()` 与 `Kick()` 放到锁外。`Timers.Foreground`
  只是登记定时器、不同步回调，持锁安全。
- 验证：修前全量 4/4 失败、修后连跑 3 次 501/501 全绿。既有 `HarnessSleepTests` 就是这条竞态的
  回归守卫（无锁必现），故没有另加用例。

**测试**：`Pi.Chord.Tests` 106 / 106（+2）、`Pi.Durable.Tests` 501 / 501。

### 6.ak 第四十一批（剩余提交复核：completions 思维顺序、Codex 头、OAuth 回调端口、codemode、Termux 剪贴板）落地明细

第四十批之后继续对「文档里没有被哈希显式引用」的提交做差集。几项按功能名记录、代码已落地的条目
（`outputPad` 全量转录块 `18336987a`、`Estimate.cs` 的 3.5 字符/token、重试模式表、
`await searchTools` 文案 `269121616`、worker 上限走排队 `3daa20280`）逐条用代码位置确认无需改动；
以下 7 处是真实缺口：

**① 一个 delta 里同时带推理与正文时的顺序**（`63035734f`，#10926）

`src/Pi.Ai/Api/OpenAiCompletions.cs`：原来先处理 `content`、后处理推理字段，于是「思维块在前、
正文在后」这条不变量要靠流式事件的到达顺序碰运气。改为 **推理字段 → `reasoning_details` →
`content` → `tool_calls`**：LiteLLM 这类代理会把推理与答案塞进同一个 delta，先落思维块才能保证
`output.Content` 的顺序。同时把上游关于「三个候选字段取第一个非空」的注释一并补上
（llama.cpp 用 `reasoning_content`，另一些端点用 `reasoning`，chutes.ai 两个字段都给且内容相同）。

**② Codex 请求头可被调用方覆盖**（`0cf65d2bf`，#10429）

`src/Pi.Ai/Api/OpenAiCodexResponses.cs`：`BuildBaseCodexHeaders` 由 `private` 放开为 `internal`
（便于语料/测试直接断言头序），并加 `/// <remarks>Difference C195…` 标注与上游的差异；头顺序固定为
「身份头 → `originator: pi` → `initHeaders` → `additionalHeaders` → `Authorization` →
`chatgpt-account-id`」。原先 `originator` 在最后写，调用方通过 `initHeaders` 传进来的
`originator` 会被顶掉；现在调用方的值覆盖默认值，而 `Authorization` / 账号头仍是最后写入。

**③ OAuth 回调端口被占用时退到随机端口**（`8d8ae2fc2`，#10571）

`src/Pi.Ai/Auth/OAuth/AnthropicOAuth.cs`：抽出局部函数 `StartCallbackServer(int port)`，
`callback = StartCallbackServer(CallbackPort) ?? StartCallbackServer(0)`——首选端口监听失败就换 0
让系统分配。`redirectUri` 随之取 `callback?.RedirectUri ?? RedirectUri`，并且 authorize URL、
手动粘贴提示（`ManualPrompt`）、token 交换三处统一用这个 `redirectUri`。原来端口被占时会直接失败，
且三处各自算 `RedirectUri` 容易写进不一致的值。

**④ codemode：拼接、计时起点与 `@options` 位置**（三条）

- `JoinAdjacentText`（`f11e50f72`，#10678）：改为「parts 列表 + lastPart」的单次拼接。原实现对每个
  part 都做一次 `string.Concat`，N 段相邻文本是 O(N²)；现在是 O(N)，且只对最后一个 part 做原地追加。
- `ExecuteCodemodeAsync`（`014873d7d`）：在 `AddGeneratedImages` 之后补 `await PublishAsync()`，
  让计时器在第一个嵌套工具调用之前就开始走，而不是等到第一段输出才起表。
- `src/Pi.Codemode/Source.cs`（`b6d505cd1`，#10631）：`// @options:` 必须位于首行，否则它的
  `timeout` / `max_output_tokens` 会被无声忽略。原来第二行及以后的 options 行是「当作普通脚本行丢给
  运行时」；现在 for 循环找到第一个错位行（`Array.FindIndex` 的 `Predicate<T>` 没有 index 参数，
  只能写循环）并抛 `The // @options: line must be the first line of the input; found one on line N`。
  既有用例 `OptionsLineMustBeOnTheFirstLine` 随之从「忽略」改为断言抛错。

**⑤ 工具行计时器抽到 `RenderUtils`，codemode 接上**（`f11e50f72` 同批的渲染面）

`src/Pi.CodingAgent/Core/Tools/RenderUtils.cs` 新增 `StateOf<T>`、`NowOverride` / `Now()`、
`FormatToolDuration`、`ToolTimerState`、`StartToolTimer`、`ToolTimerText`；`BashRenderers` 里的同名
成员改为转发（`NowOverride` 的测试缝保持原入口）。`CodemodeRenderers.RenderCall` 开头
`StartToolTimer(context)`，`RenderResult` 开头取 `ToolTimerText`，末尾在有值时补一个空行 + 灰色
耗时行（运行中 `elapsed`，结束后 `took`）。

这里的 `StateOf<T>` 同时修掉一个**被静默兜底的真缺陷**：`ToolExecutionComponent` 传下来的
`context.State` 是 `Dictionary<string, object?>` 状态袋，而 `BashRenderers` / `EditRenderers` 直接
`context.State as ShellRenderState` 强转，恒为 `null`（引用类型强转失败不抛，但后续 `??=`/取值用空
状态），`ToolExecution.run` 的 `catch` 再把它降级成纯文本行。`StateOf<T>` 该从状态袋里取、该用测试
直接传入的键入状态、都没有就 `new()`，两条路径都成立。

**⑥ `mcp --timeout` 的说明文案**（`148622bc6`，#10636）

`src/Pi.CodingAgent/Extensions/Mcp/Cli.cs`：`--timeout <seconds>  Time limit for the whole sign-in
(default: 300)`。原文案写成「等待浏览器多久」，实际这个超时覆盖整个登录流程（含设备码轮询与
浏览器回跳），照原文案调小容易误判。

**⑦ Termux 剪贴板在 Android 上也要走**（`592fb57b7`，#10391）

`src/Pi.CodingAgent/Utils/Clipboard.cs`：`ReadCommands` 把 `TERMUX_VERSION` 判断提到
`platform != "linux"` 之前——Termux 报的平台是 `android` 而不是 `linux`，原来的顺序让它永远走不到
`termux-clipboard-get`。`CopyFailureMessage` 同样把 Termux 分支提到最前（否则 Android 上没有任何
可用命令，却拿到一个 `null` 提示，界面只说「复制失败」不说怎么装 Termux:API）。

**测试**：`Pi.Ai.Tests` 512 / 512（+3：`KeepsAThinkingBlockAheadOfTextWhenOneDeltaCarriesBoth`、
`CodexHeadersLetCallerValuesOverrideTheDefaults`、
`FallsBackToAFreeCallbackPortWhenThePreferredOneIsTaken`）；
`Pi.CodingAgent.Tests` 5073（5070 通过 / 3 跳过，+5：`CodemodeRenderersTimerTests` 的
`StateOf_KeepsOneInstancePerRowAndPassesATypedStateThrough`、
`CodemodeRow_ShowsElapsedWhileRunningAndTookWhenDone`、
`ShellRow_KeepsItsClockInTheComponentStateBag`，以及 `ClipboardCommandTests` 的
`ReadsTheTermuxClipboardOnAndroidToo`、`ReportsTheTermuxCopyHintOnAndroidToo`）；
`Pi.Codemode.Tests` 52 / 52（+1 `RejectsAnOptionsLineThatIsNotTheFirstLine`）；`Pi.Mcp.Tests` 55 / 55。

### 6.aj 第四十批（MCP 管理器响应性）落地明细

上游 `2db5e359b`（#10562）：启动连接没跑完就打开 `/mcp` 会卡住，改配置的动作也把菜单锁着。
端口 `src/Pi.CodingAgent/Extensions/Mcp/McpExtension.cs` 按同一语义重排：

**1. 连接尝试的身份令牌（`attempt` / `closing`）**

- `McpServer` 新增 `Attempt`（TS `attempt?: symbol`；C# 无 symbol，用 `new object()` 作身份令牌，
  比较一律 `ReferenceEquals`）与 `Closing`（脱钩连接的清理任务）。
- `StartConnectionAsync(server, after)` 不再接收调用方给的 `Func<bool> isCurrent`，改为内部自建
  `attempt` 并合成判定：`session` 仍是同一个 CTS、未取消、`server.Attempt == attempt`、
  `IsEnabled(server)`、`servers.Contains(server)`。这样禁用、替换、会话结束都会让旧尝试作废，
  调用点（session_start / McpServersChange / 重新启用）不必各自写一遍条件。
- `CreateConnectionAsync(server, isCurrent)` 返回 `McpServerConnection?`（作废时 null），并把
  `OnTools` / `OnChange` 包一层：脱钩连接的 `onChange` 只把 `tokensAtSignIn` 删掉，不再往会话里灌
  工具和状态。TS 在 `loadMcpRuntime()` 之后再查一次，端口没有运行时加载（差异 C189），
  所以在构造前查一次即可。

**2. 工具定义在执行时解析当前连接**

`RegisterTools` 里 `GetClient` 原来捕获注册那一刻的 `connection`。一次「准备好的调用」可能跨过
禁用/重新启用，于是 `GetClient` 改为在**执行时**按名字重新找 server：禁用 → `is disabled.`、
尚未连上 → `is still starting.`、曝光为 `hidden` 或该 server 已不再提供这个工具 →
`is no longer available.`。`ReadableResources` 同样按名字比较而不是按连接实例。

**3. 等待语义**

- `WaitForServersAsync` 改成循环：等完一批后，如果某个 server 的 `Ready` 已经被换成新的尝试
  （重连、禁用后重新启用），就再等一轮，而不是在旧连接上返回。同时过滤掉已禁用或已移除的 server；
  因此它不再是 `static`（要读闭包里的 `servers`）。
- `ReconnectAsync` 先串到 `server.Ready` 之后，再把本次重连的 `Ready` 装回去，
  于是「关旧传输再开新的」不会和启动连接交错，`tool_call` 也能等到它。
- `SetEnabledAsync(false)` 把 `connection.CloseAsync()` 与 `server.Ready` 合成 `server.Closing` 再
  等它（重连可能已经把旧 client 摘掉、正在等传输关闭，单关连接等不到那一步）；
  `(true)` 则把 `server.Closing` 作为 `after` 传给 `StartConnectionAsync`，替换连接前先等清理完。

**4. 管理器不再被动作阻塞**

新增 `RunInBackground(ctx, server, operation)`：先启动 `operation()`、**再**读 `server.Attempt`
（禁用会同步把 attempt 置空，启用会同步写入新 attempt，顺序反过来就认错尝试），
完成时只有会话、尝试、server 列表都还对得上才写 `server.Message` 并 `EmitChange`。
管理器里的 `reconnect` 与 `enable*/disable*` 改走它，`status("Reconnecting…")` 之类的临时状态行去掉
——连接状态和错误本身已经报出来了。`shutdown` 把 `backgroundActions` 和待关闭的连接一起等。

**5. `/mcp` 与 `tool_call`**

- 命令处理器：无参数时 TUI 直接开管理器（不再 `await pending`），仅 Print 模式的状态行与其余子命令
  才等启动连接跑完。
- `tool_call` 钩子：候选集合从「未连上的 server」放宽为「已启用且有 `Ready` 的 server」
  （重连时显示状态还是 connected，但 `Ready` 已在途），并且**直接 MCP 工具只等它自己的 server**
  （`toolOwners` 里 `server\0tool` 的前半段），codemode 脚本按脚本需要等，`tool_search` 与资源工具等全部。

**测试**（`McpExtensionTests`，+3）：`McpCommand_OpensTheManagerBeforeTheStartupConnectionsFinish`
（握手卡住时管理器仍立刻打开）、`Manage_KeepsTheMenuUsableWhileAReconnectIsStillRunning`
（重连卡住时菜单照常走完四步）、
`McpTool_ResolvesTheCurrentConnectionAfterTheServerIsDisabledAndEnabledAgain`
（禁用再启用后，先前拿到的工具定义走**新**连接）。第三条在旧实现下报
`MCP server "docs" is shut down`，确认用例有效。测试脚手架为此加了 `InitializeGate` / `Handshakes` /
`AnsweredHandshakes` 与按脚本作答的 `QueueUi`。

**测试**：`Pi.CodingAgent.Tests` 5068 / 5068（3 跳过，+3）、`Pi.Mcp.Tests` 55 / 55。

### 6.ai 第三十九批（剩余提交复核：跨模型思维、错误结果图片、Windows 锁与 shell 探测）落地明细

第三十八批把 278 个提交按「文档里是否出现哈希」做了差集，发现 200 个没被显式引用过。逐条复核后，
绝大多数属于此前按**功能名 / PR 号**记录、代码早已落地的条目（用代码位置逐一确认）：
工具参数非法 JSON 报错 `87f9a3641`（`JsonParse.ParseToolCallArguments` + `Validation`）、
`read` 行范围标签 `910149912`（`ReadRenderers.FormatReadLineRange`）、提示模板空引号参数 `9fe3f3716`
（`PromptTemplates.ParseCommandArgs` 的 `hasToken`）、打印模式截断告警 `441f5767c`（`PrintMode` 的
stderr 行）、假光标选择重置 `83c9e2645`（`TuiAltScreen.ResetTextSelection`）、
display math 不吞表格 `2ed3118b0`（`MarkedExtensions.ContainsTableStart`）、
`CompactionStatusIndicator` 的 `colorFn` `dbc7ff87c`、摘要请求不设固定输出上限 `6fcb48670`
（`Compaction` 里 `_ = reserveTokens`）、`\u` 非法转义的修复 `4b994d5e5`、`--tools` 的 `+name/-name`
`ddaa0a034`，以及整组重试文案（`Retry.cs` 里已带 `#10965` / `#11057` 注释）。
下面四条是真缺的。

**1. 跨模型的思维块不再当纯文本发出去（`911309429`，#10995）**

- 现象：切模型后，旧模型助手消息里的思维块被改写成纯文本塞进新模型的上下文，新模型于是把上一轮
  的推理当成「自己回复的一部分」，开始把自己的思考也写进可见答案。
- `TransformMessages` 的 `ThinkingContent` 分支重写：**非同模型直接丢弃**；同模型才保留
  （`Redacted` 或带 `Signature` 的空文本块照样保留，回放需要）。原实现里
  `else transformedContent.Add(new TextContent(thinking.Thinking))` 整段删掉。
- 测试：`CrossModelDropsThinkingAndStripsSignatures`（原 `CrossModelDowngradesThinking…` 按新语义重写）、
  `SameModelKeepsSignedAndEncryptedThinking`、`KeepsThinkingOnlyFromTheTargetModelWhenModelsAlternate`
  （对齐上游新增用例「两个模型交替时只保留目标模型的那几块」）；`GoogleGenerativeAiTests`
  的 `ConvertMessagesDropsThinkingAcrossModels` 同步改成「只剩文本」。

**2. 错误工具结果的图片移到工具结果之后（`5ab7fe74c`，#10794；前置 `34a8bb38b`）**

- 现象：Anthropic 与 Bedrock 上的 Claude 拒收「错误工具结果里带图片」。原先的做法是把图片换成一句
  说明，等于把截图丢了。
- 新增 `TransformMessages.SplitErrorToolResultImages(msg)`：错误结果里的图片被替换成
  `(image attached after the tool results)` 占位（连续图片只折叠一次），返回
  `Content` / `Caption` / `Images`；成功的、没有图片的错误结果返回 null。
- `AnthropicMessages`：连续工具结果循环里用拆出来的 `Content` 转换，`Caption` + 图片收集到
  `errorImages`，循环结束后追加到同一个 user 消息的 `tool_results` 之后；`ConvertToolResult` 增加
  可选的 content 覆写参数，图片构造抽成 `ConvertImageBlock`。
- `BedrockConverseStream`：同样处理，但**只对 `IsAnthropicClaudeModel` 的模型**，其它模型图片留在原位。
- 测试：`AnthropicMessagesTests` 2 例（含「连续两张图片只折叠成一个占位」与「无图片的保持不变」）、
  `BedrockTests` 2 例（Claude 移走 / 非 Claude 原位）。
- **坑**：`BedrockTests.Model(id)` 把 `Name` 固定成 `Claude Sonnet 4`，于是 `IsAnthropicClaudeModel`
  对任何 id 都返回 true；非 Claude 用例必须自己给完整 spec。另外测试模型不写 `Input` 时
  `DowngradeUnsupportedImages` 会先把图片换成占位，两个用例都得声明 `Input = [text, image]`。

**3. Windows 上锁产物的瞬时权限失败单独设上限（`e6fb2581d`，#10801）**

- 现象：刚删掉的锁产物可能处于 delete-pending（另一个进程还握着句柄），创建会短时间失败。
  端口的 `NodeLock.AcquireAsync` 把这种失败和「已被别人持有」一视同仁，于是真正的权限错误要等满
  30 秒窗口才报 `ELOCKED`。
- `TryAcquire` 增加 `out Exception? failure`（目录创建失败也接住），`AcquireAsync` 单独计数
  `permissionRetries`：`UnauthorizedAccessException`（且 `OperatingSystem.IsWindows()`）超过
  `MaxPermissionRetries = 5` 就立刻抛 `LockHeldException`，`ELOCKED` 的窗口行为不变。
- 同步路径 `AcquireSync` 本来就是「把创建失败包成 `LockHeldException` 再重试 10 次」，与上游给
  `EPERM` 扩展可重试集合等价，未改。
- **偏差**：.NET 没有 errno，`EPERM` 只能映射成 `UnauthorizedAccessException`；`ERROR_SHARING_VIOLATION`
  这类 `IOException` 不上限，否则会把「文件已存在」的正常等待也误判成瞬时失败。
- 测试：`NodeLockTests.WaitsTheWholeWindowWhenAnotherHolderOwnsTheLock`（钉住窗口没有被误缩短）。

**4. Windows shell 从安装目录探测（`61060ddf8`）**

- `GetPowerShellConfig` 的查找顺序补齐为：`pwsh.exe`（PATH）→ `%ProgramFiles%\PowerShell\7\pwsh.exe`
  → `powershell.exe`（PATH）→ `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`。
  后两个候选抽成 `Shell.PowerShell7InstallPath` / `Shell.WindowsPowerShellInstallPath` 便于钉住形状。
- `ToolsManager.GetWindowsTarCommand` 去掉 `WINDIR` 回退——只有 `SystemRoot` 下的 `tar.exe` 才是
  Windows 自带那份。
- `IsLegacyWslBashPath` 在第十四批已经改成跟 `SystemRoot` 走，本批只是复核。
- 测试：`ShellProbeTests` +2（安装目录拼装形状、Windows 上解析出的可执行文件确实存在）。

**测试**：`Pi.Ai.Tests` 509 / 509（+6），`Pi.CodingAgent.Tests` 5065 / 5065（3 跳过，+3）。

### 6.ah 第三十八批（HTML 导出的 CJK 强调）落地明细

逐条复核 `v1.0.4..v1.4.0` 剩余提交时，按代码逐项确认了此前按「功能名 / PR 号」记录过的条目
（工具参数非法 JSON 报错、`read` 行范围标签、提示模板空引号参数、打印模式截断告警、假光标选择重置、
`fd` 转义、滚轮路由、重试与溢出文案……）均已落地。真正缺的是这一条：它落在 **HTML 导出的浏览器端
JS**，C# 侧没有对应实现，容易被漏掉。

**1. HTML 导出里 CJK 标点旁的粗体/斜体显示成字面星号（`26293e74f`，#3353 / #10154）**

- 现象：`**这是测试。**后面`、`字**「引用」**字`、`*斜体。*后面` 在导出的 HTML 里原样显示星号。
  CommonMark 的 flanking 规则把 `。`、`」` 归为标点，收尾分隔符因此被判为「不能闭合」。
- 上游做法是引入 `marked-cjk-friendly`（v0.1.2，MIT），把 `emStrong` 分词器替换成「把 CJK 标点当
  文字看」的版本。端口同步三处：
  1. `Assets/export-html/vendor/marked-cjk-friendly.min.js`（新增）与 `marked.min.js`
     18.0.5 → **18.0.11**（同一提交里的版本对齐；`PackAssets` 的 `Assets\export-html\**\*`
     通配会自动打包新文件）。
  2. `template.html` 在 `{{MARKED_JS}}` 之后插入 `<script>{{MARKED_CJK_FRIENDLY_JS}}</script>`。
  3. `template.js` 的 `marked.use({ tokenizer: { … } })` 里追加
     `emStrong: markedCjkFriendly().tokenizer.emStrong`。注意模板用的是**扁平 `tokenizer`**，
     不是 `extensions` 数组——后者要求显式 `level`，写错会抛 `extension level must be 'block' or 'inline'`。
- `ExportHtml.GenerateHtml` 读取并替换新占位符；其余 8 个占位符顺序不变。
- **行为核对**（把两个 vendor 脚本放进 `node:vm` 沙箱，用模板同样的 `marked.use` 形态跑）：
  上游四条 CJK 用例全部产出 `<strong>` / `<em>`（`**这是测试。**后面` /
  `**一句话说清：**一台机器` / `字**「引用」**字` / `*斜体。*后面`），
  而非 CJK 的 `**end.**Then next` **仍然保持字面星号**——CommonMark 语义没有被放宽。
  去掉 `emStrong` 覆盖后同一批输入会回到字面星号，说明覆盖确实生效。
- **测试**：`ExportHtmlBugReportTests.TemplateJs_UsesCjkFriendlyEmphasisTokenizer`（静态解析三处接线），
  `GenerateHtml_ReplacesTemplatePlaceholders` 追加 `{{MARKED_CJK_FRIENDLY_JS}}` 与
  `markedCjkFriendly` 的断言。

**测试**：`Pi.CodingAgent.Tests` 5062 / 5062（3 跳过，+1）。

### 6.ag 第三十七批（跨 chunk 拆分的 ANSI 序列）落地明细

第三十六批的复核里留下一条待确认项：用户 `!bash` 的流式输出按任意边界切块，颜色码一旦跨块，
每块各自 `stripAnsi` 就把尾巴漏成字面量。本条关闭它。

**1. 拆分跨 chunk 的 ANSI 序列（`27c7b6ff4`，#10504）**

- 现象：`echo -e '\033[31mERROR\033[0m'` 若 `\033[0` 与 `m` 分属两块，第一块的
  `stripAnsi` 匹配不到（CSI 缺末字节），`sanitizeBinaryOutput` 再把孤立的 `ESC` 当控制字符删掉，
  于是输出里留下 `[0m` 这样的残渣；`ESC` 后跟 `[32m` 被拆开时同理。
- `AnsiText` 新增 `SplitIncompleteAnsiSuffix(string)`：把 OSC 前缀（`ESC ]`，无 ST 收尾）与
  CSI 前缀（`ESC`/C1 + 中间字符 + 参数，缺末字节）用 `\z` 锚定在末尾，命中就切成
  `Complete` / `Pending` 两段；回看窗口 `MaxPendingAnsiLength = 256`，超长的未终结串视为普通文本
  直接放行（否则一条没有 ST 的 OSC 会把整条流一直扣住）。
  **偏差**：JS 的 `$` 只锚定输入末尾，.NET 的 `$` 还会匹配末尾换行之前，因此改用 `\z`。
- `BashExecutor.OnData` 改为 `pendingAnsi + 解码文本` → 切分 → 存回 `pendingAnsi` →
  `AppendText(complete)`；原文里 `totalBytes` / 临时文件 / 滚动缓冲 / `OnChunk` 的逻辑原样搬进
  `AppendText`，并补上上游新加的空串早返回。
- 新增 `FlushOutput()`：`pendingAnsi` 与 `decoder.GetChars([], 0, 0, buf, 0, flush: true)` 的残余
  （截断的多字节字符 → `U+FFFD`）一起过 `AppendText`。取消与正常结束**共用**一条出口
  ——原来的两个 `catch` 复制了同一段收尾，现在只留 `catch (Exception) when (CancellationToken…)`
  空吞（中止的命令仍返回已产出的输出）与一个 `catch { dispose; throw; }`。
- 顺带修掉旧取消分支没把 `tempFileStream` 置空的泄漏。

**测试**（`SessionRuntimeLeafTests`，5 例，逐条对上上游 `agent-session-bash-persistence.test.ts`
新增的 5 例）：参数中间被拆开的颜色重置（`ESC[0` + `m`）、`ESC` 后紧跟颜色码（`ESC` + `[32m`）、
OSC 在 ST 之前被拆成三段、结尾只剩多字节字符首字节时 flush 成 `U+FFFD`、
300 字符长的未终结 OSC 不被扣住。跳过的那条「`recorded` 与流式一致」属于会话层断言，
端口的 `BashExecution` 组件复用同一份 output，等价保障在既有语料里。

**测试**：`Pi.CodingAgent.Tests` 5061 / 5061（3 跳过，+5），其余套件未受影响。

### 6.af 第三十六批（收尾复核第二批：工作树上下文 + 两组不适用判定）落地明细

第三十五批之后继续按提交逐条复核，补上一项遗漏修复，并把两组此前从未被显式判定的上游改动做成书面判断。

**1. 嵌套工作树里符号链接的 `AGENTS.md` 一份都不加载（`f1b2e77f5`，#10681）**

去重原本比较「上下文文件自己的 realpath」与主仓库那份：工作树里的 `AGENTS.md` 若符号链接到主仓库的同名文件，
realpath 正好等于被遮蔽的路径，于是工作树这份和主仓库那份一起被跳过——结果一份项目上下文都不加载。改为比较
**走过的目录的 realpath + 文件名**（抽成 `DefaultResourceLoader.IsShadowedContextFile`）。

新增 `ResourceLoaderWorktreeTests`（6 例）：自建链接工作树骨架（不需要 git 二进制），覆盖工作树自有上下文、
工作树符号链接到主仓库文件、两边都链接到同一文件、工作树根无上下文时继承主仓库，以及两条直接锁定判定的用例
（`worktree/../..` 造出「文件规范路径落在主仓库」的形状，等价于真实符号链接场景）。

**2. `packages/env` 的 1.2.0 特性：不适用**

`listFiles`（守护进程 op + `ExecutionEnv.listFiles` 可选方法，`aa499c242` / `abfe62928` / `b6fda579f`）、
伪终端里跑程序与前台作业（`367b2bcf5` / `74613de65`）、`nodePlatform` 探测：端口的 `Pi.Env` 是**SSH 远程
环境**（`Ssh` / `Connection` / `RemoteEnv` / `RemoteWatcher`），实现 `Pi.Durable.Env.IExecutionEnv`
（`IFileSystem` + `IShell`），没有上游那个 node 侧守护进程，也没有「交互式终端会话」这一概念（`IShell` 只有
一次性 `ExecAsync` + 中止）。树形列举在端口以 `ListDirAsync` / `OpenDirReaderAsync` 递归表达，且 `Pi.Durable`
内没有需要可选 `listFiles` 的消费者（只有 `JsonlStorage` 用它枚举目录）。相关的守护进程测试/CI 稳定性提交
（`14ff455a0`、`d6370b72d`、`de022efd6`）同属测试层。

**3. 托管安装（managed install）：不适用**

`pi update` 后清理旧 release 目录（`9f013cf59`）、托管安装标记接受 UTF-8 BOM（`ea25697e9`）、独立二进制禁用
`.env` 自动加载（`1ffb6bd62`）、npm 12 pack JSON（`6fb2e7815`）：端口的 `InstallMethod` 只有
`BunBinary` / `Npm` / `Pnpm` / `Yarn` / `Bun` / `Unknown`，没有 `releases/<version>` 那套托管安装目录布局，
自更新只走包管理器命令；`build-binaries.sh`、npm 打包脚本在 .NET 侧无对应物。

**验证**：`dotnet build` 0 警告 0 错误；`Pi.CodingAgent.Tests` 5056 / 5056（4 例新用例通过；3 例跳过——
`ClipboardCommandTests` 的 POSIX 进程组用例，以及两条需要符号链接权限的工作树用例）。

### 6.ae 第三十五批（1.4.0 收尾审计：五项遗漏修复）落地明细

§3.5.1 的表清空后，按 `v1.0.4..v1.4.0` 的 278 个提交逐条复核，发现五项**适用但漏移植**的行为修复（另有一处判定
为纯重构、无行为差异）。本批补齐它们，1.4.0 对齐到此收尾。

**1. `/compact` 的自定义指令漏作用于拆分回合的前缀摘要（`adfc14461`，#11058）**

`Compaction.CompactAsync` 把 `customInstructions` 传给主摘要，却没传给 `GenerateTurnPrefixSummaryAsync`，
于是拆分回合时用户写的关注点只作用于历史部分。端口补上该参数，并在提示末尾追加
`\n\nAdditional focus: {customInstructions}`（仅在给出时）。新增 `CompactionSummaryPromptTests`（2 例）：
拆分回合的唯一请求带上 `Additional focus`，未给指令时不出现该段。

**2. 分支未记录过的扩展工具在 resume / `/tree` 后不激活（`bd3c9b823`，#11062 / #11075）**

`RestoreToolsFromTranscript` 原先只重放「当前系统消息」的 `toolsAdded`，因此恢复后只会有分支当时记下的工具：
记录 loadout 之后才注册的扩展工具永远不活跃。改为：

- 取**整条分支**的系统消息（含压缩之前的——压缩快照由它们派生），`Transcript.GetCurrentTools` 重放出当时的工具集；
- 收集分支曾经 `toolsAdded`/`toolsRemoved` 过的全部名字，从未出现过的注册工具视为「记录之后才注册」，
  按 `IsActivatedOnRegistration`（`defaultActive: false` 除外）激活；内建工具仍跟随 `defaultTools`；
- 记录过但还没注册的工具（如仍在连接的 MCP 服务器）留待注册时激活；被排除的工具不进待定集合。

`AgentSessionResumeToolsTests` 从 6 例扩到 10 例：新增「激活分支从未提过的扩展工具」「压缩前删掉的工具保持
不活跃」「切树按目标分支恢复 loadout」「allowlist 外的注册工具切树后仍不活跃」，并把三处既有用例改用
`ChangedSession()`（记录过增删的会话）。

**3. 剪贴板命令与 pi 共享进程组（`f8ab362cc`，#11179）**

剪贴板写入器常自我守护化，与 pi 同进程组时 `Ctrl+Z`（向整组发 `SIGTSTP`）会把真正的剪贴板属主一起停掉。
上游在非 Windows 上加 `detached: true`；端口对应 `SpawnOptions.Detached`（Unix 经 `setsid`），Windows 上仍为
`false`（与上游一致，Windows 没有进程组语义）。新增 `ClipboardCommandTests`（2 例，进程组那条在 Windows 上
`Assert.Skip`）。

**4. ChatGPT Business 工作区的用量限额没有提示（`cedefc532`）**

Business 计划的 `subscription_sharing_workplace_individual_usage_limit_exceeded` 只有工作区管理员能提高，
重试无意义。端口此前只在 retry 判定里把它标成不可重试，但失败文案没有提示。抽出
`OpenAiResponses.DescribeChatGptError`：共享限额附用量页链接，工作区限额提示去找管理员。

**5. 增量工具提示提前把同条消息要删的工具算作「仍然不可用」（`530015d93`）**

`convertResponsesMessages` 原先在 `additional_tools` 之前就把本条消息的 `toolsRemoved` 记进
`unavailableTools`，于是排在它后面的移除通报被提前复述。改为在渲染完本条移除通报之后再登记，
增量提示因此只复述更早消息宣布过的移除。新增 `DoesNotListAToolAsStillUnavailableBeforeItsRemovalIsAnnounced`。

**6. 任何 HTTP 413 都算请求体超限（`73f8de80b`）**

Copilot 对超过字节上限的请求体回 413 + 通用正文（`failed to parse request`），原有按 token 文案匹配的启发式
一条都命中不了，于是溢出被当成普通 API 错误抛出、不触发压缩重试。`Overflow` 补一条模式：**开头的 413** 或
`<provider> API error (413):` 前缀一律判为请求体超限（正文里提到的 413 不算）。新增一条用例覆盖三种开头写法
与一条反例。

**判为无需移植（1 条）**

`transcript.ts` 的 `hasNonAdditiveToolChanges` / `resolveTranscriptTools` 合并成一次扫描（`530015d93`）：
端口两处逻辑本就等价，只是没有共享循环，属重构而非行为变更，不动。

**验证**：`dotnet build` 0 警告 0 错误；`Pi.Ai.Tests` 503 / 503（+3）、`Pi.CodingAgent.Tests` 5050 / 5050（+8，
`ClipboardCommandTests` 2、`CompactionSummaryPromptTests` 2、`AgentSessionResumeToolsTests` +4）。

### 6.ad 第三十四批（durable：`settings.afterFailedRun`）落地明细

上游给 `HarnessSettings` 加了 `afterFailedRun`（spec §6）：一次失败的运行（`model_error` / `no_model`）对排在它
**后面**的输入做什么——`keep`（缺省）留着等下一个输入、`continue` 像拿到答案一样立刻用它们开下一个运行、
`withdraw` 以 `run_failed` 结算为 `unanswered`。这是 1.4.0 的最后一个 durable 变更项。

**1. 类型（`src/Pi.Durable/Harness/HarnessTypes.cs`）**

- `enum AfterFailedRun { Keep, Continue, Withdraw }`；
  `HarnessSettings.AfterFailedRun`（`AfterFailedRun?`，缺省 `null`）；
  `Settings.AfterFailedRun`（`required`，`ResolveSettings` 落为 `settings?.AfterFailedRun ?? AfterFailedRun.Keep`）。
- 上游的 `keep` 不是「什么都不做」的同义词：它把输入留在收件箱里，之后的输入或排队写入仍会在边界把它们
  与自己的队列一起放置——端口原本就是这个行为，因此 `keep` 无需新代码。

**2. 三种失败路径统一到一处（`src/Pi.Durable/Harness/Generation.cs`）**

- `FailurePolicy(Policy, Boundary?)` + `PrepareFailureAsync(tx, runtime)`：`continue` 时在这里
  `Inbox.PrepareBoundaryAsync`；**表读必须先于提交的首次表写**，所以三个调用点都把它放在提交回调的最前面。
- `EndFailedRunAsync(tx, runtime, live, failure, settlement)`：`Live.EndRun` → `withdraw` 时
  `Inbox.WithdrawQueuedInputsAsync(tx, id, "run_failed")` → `boundary` 存在时
  `ApplyBoundaryAsync(..., BoundaryAt.Final, ...)`，产生了用户项就 `StartRunAsync` 起下一个运行。
- 三处调用点：`FailModelErrorAsync`、`FailNoModelAsync`，以及生成循环里非 retry 的 `model_error` 分支
  （该分支的 `failure` 在 `AppendAssistantAsync` 之前读好，retry 分支则为 `null`，不会用到）。
  原先散落的 `Live.EndRun(...)` 调用被这一处替换，语义收敛成「结束运行 + 处置队列」一个动作。

**3. `Inbox.WithdrawQueuedInputsAsync` 加 `reason` 参数（缺省 `aborted`）**

`Conversation.abort()` / 中止级联用缺省值，失败运行传 `run_failed`。行为不变的部分值得写明：撤回**只撤
用户输入**，排队写入（`mode: "write"`）留在收件箱等后续边界放置——`withdraw` 不丢弃宿主已经受理的被动写入。

**4. 测试（`tests/Pi.Durable.Tests/HarnessFailedRunTests.cs`，4 例）**

`KeepsTheQueuedInputBehindAFailedRunByDefault`（留队且下一个输入把队列跑完）、
`StartsTheNextRunWithTheQueuedInputWhenContinue`（转发作答来自第二次应答）、
`WithdrawsTheQueuedInputBehindAFailedRunWhenWithdraw`（`unanswered` + `run_failed`）、
`PlacesQueuedWritesAndKeepsThemWithWithdraw`（同一用例跑两种策略：`continue` 放置成 `done`、`withdraw` 仍
`queued`）。语料面 `Pi.Durable.Tests` 501 / 501、`Pi.CodingAgent.Tests` 5042 / 5042 全绿。

**与上游差异（2 条）**

1. 端口没有把 `afterFailedRun` 暴露到 coding-agent 的配置文件 / CLI；上游同样没有（`packages/coding-agent`
   中无此键），因此两边都只在 `HarnessSettings` 层可配。
2. `Settings.AfterFailedRun` 在端口是 `required`，`HarnessSettings.AfterFailedRun` 是可空——与端口既有的
   「解析后的设置字段全部 `required`、原始设置字段可缺省」约定一致；上游 `Settings` 用一个 `type` 表达同一件事。

### 6.ac 第三十三批（durable：上报分类 `ReportInfo`）落地明细

上游把 `HarnessOptions.onReport` 从 `(error)` 改成 `(error, info)`：宿主据此区分「会话本身失败（通常是平台
重置）」与「某个任务的失败 / 停放 / 过旧」「某个扩展的代码失败」「某个宿主回调抛出」。端口同批对齐，
`ReportInfo` 成为唯一的上报形状。

**1. 类型（`src/Pi.Durable/Harness/HarnessTypes.cs`）**

- `ReportTask`（任务 ID + 注册名 + 对话）、`HostCallbacks`（`now` / `settings` / `registry` / `env` /
  `models` / `setTimer` / `onWake`）、`ReportReasons`（`threw` / `restart_limit` / `missing_task` /
  `incompatible_task`）。
- `ReportInfo`（抽象记录，9 个派生）：`SessionFailed`、`TaskFaulted(task, reason, extension?)`、
  `TaskParked`、`MigrationFailed`、`TaskOutdated(task, reason, extension?)`、`TaskError`、
  `ExtensionError(extension?, task?, conversationId?)`、`HostError(callback, task?)`、`ListenerError`。
- `ReportFrom`（`runtime.Report()` 的来源）：`FromExtension(name?)` / `FromHost(callback)`。
- `HarnessOptions.OnReport` → `Action<Exception, ReportInfo>?`；
  `DurableSession.Report(Exception, ReportInfo? info = null)`（缺省 `listener_error`）。

**2. 分类落点（`Scheduler.cs`）**

| 事件 | `ReportInfo` |
| --- | --- |
| 阶段抛出 / 无进展 / abort 处理器无结局（`StepAsync` 落定后上报） | `task_faulted(threw)` |
| abort 处理器被切断 `restartLimit` 次 | `task_faulted(restart_limit)` |
| 重启上限停放 | `task_parked` |
| 迁移抛出 | `migration_failed` |
| 运行中的任务其定义被卸载/替换 | `task_outdated(missing_task / incompatible_task)` |
| `runtime.Report()` 缺省 | `task_error` |
| `runtime.Report(err, from)` | `extension_error` / `host_error` |
| `settings`（`restartLimit` / `contextRetentionMs`）抛出 | `host_error(settings)` |
| 注册表快照抛出（调度与 `blockedReason` 各一处，后者只报一次） | `host_error(registry)` |
| `onWake` 抛出 | `host_error(onWake)` |
| 提交/关闭/阻塞/视图监听器、后台过期与 reconcile/drain 失败 | `listener_error` |
| Storage 提交失败使会话失败 | `session_failed`（由 `DurableSession` 发出） |

- `Invocation` 新增 `Kind` / `Extension`：`CreateInvocation(record, mode, InstallerOf(snapshot, task))`。
  `extension` 取**跑这段代码时**安装其定义的扩展，因此定义被替换后仍指向旧扩展（与上游一致）。
- 新增 `ReportTaskOf(record)` / `ReportTaskOf(invocation)`、`InstallerOf(snapshot, task)`（最后一个列出该
  任务的已安装扩展）与 `InstallerOf(record)`（读注册表抛出时上报不带扩展名）。
- `Decide` 多一个 `extension` 参数，供 `task_outdated` 指明旧定义的来源。
- `AgentDocs.AgentHooks` 改为返回 `AgentHook(Extension, Handlers)`；`AgentDocs.ResolveAgent` /
  `ApplyWrap` 的 `report` 改成 `(error, extension)`，包装器失败因此带上其扩展名
  （`HarnessImpl.ResolveAgentAsync` 包成 `extension_error` + `conversationId`）。
- `HarnessImpl`：`_report` 改为 `Action<Exception, ReportInfo>` 并对宿主处理器做**吞异常**包装
  （对应上游 `safeReport` 的 `contained`）；`setTimer` 的失败单独标成 `host_error(setTimer)`；
  `Report` 覆写带 `info`；open 失败时的 close 错误走 `ListenerError`。

**3. 顺带修的两处会真实丢事件的问题**

- `Session.Publish` 与关闭监听器现在逐个 `try/catch`：一个监听器抛出既不失败提交，也不跳过其余监听器，
  而是上报 `listener_error`（上游同语义，端口此前直接向外抛）。
- `Scheduler.Seal` 记录**封印时**的调用快照 `_sealed`，`JoinAsync` 用它而不是重新读 `_invocations`。
  取消之后阶段可能立刻结束并从 `_invocations` 消失，导致关闭时漏写回 `pending`、下次 open 误计一次重启
  （`CountsNoRestartForATaskACloseStopped` 在并发下因此间歇失败）。

**4. 测试（`tests/Pi.Durable.Tests/HarnessReportTests.cs`，7 例）**

任务自身的 `task_error` + `task_faulted(threw)` 带扩展名、`runtime.Report` 的三种来源、定义被另一扩展替换
后的 `extension` 归属、`task_parked`、`migration_failed`、`task_outdated(missing_task)`，以及一条覆盖
`extension_error(conversationId)` / `listener_error` / `host_error(onWake)` / `host_error(setTimer)` /
`session_failed` 的合并用例。同时更新三处既有断言（无进展 fault 现在会多一条上报、open 失败的 close
错误与 `session_failed` 分成两条、`AgentHooks` 返回形状）。

**与上游差异（3 条）**

1. 端口没有 `session.fail()`，因此「内部监听器抛出」在端口被当作 `listener_error` 上报而不是让会话失败；
   `session_failed` 只在 Storage 提交/采纳失败时发出。
2. durable 侧没有 telemetry：上游 `safeReport` 同时记 `pi.durable.reports` 计数器与 span 属性，端口只转发。
3. `env` 回调失败的 `host_error(env)` 在上游来自 coding-agent 的 `generation.ts`（`runtime.report` 的
   `from`），不在 durable 调度器里；端口 `runtime.EnvAsync` 抛出走正常的 `task_faulted(threw)`，
   需要 generation 层对齐时再补。

### 6.ab 第三十二批（durable：crash-loop guard 与阻塞原因）落地明细

上游同批引入（`scheduler.ts` 的 `restarts` / `parked` / `retry` 与 `task-graph.ts` 的 `blocked`）。目标：一个在
运行中被反复切断、却从未改变状态的任务不再无限重跑，而是停放成 `restart_limit` 并可从外部放行。

**1. 记录与设置**

- `TaskRecord` 新增三个字段：`Restarts`（`int?`，连续被切断次数）、`AbortReason`（`string?`，只有 `"restart"`
  一种值）、`AbandonOnRestart`（`bool`，来自 `TaskOptions`，不可变）。
- `TaskOptions.AbandonOnRestart` → `Tx.CreateTaskAsync` 写入记录。**与上游差异**：上游 `tool.ts` 对不可重放的
  工具把子任务设成 `abandonOnRestart`；端口没有嵌套工具调用/子调用任务，因此本批只开放 `TaskOptions` 这个入口，
  由宿主与测试使用。
- `HarnessSettings.RestartLimit`（`int?`）+ `Settings.RestartLimit`（必填）；`AgentDocs.DefaultRestartLimit = 5`
  与 `AgentDocs.ResolveRestartLimit(int?)`（`>= 1` 取原值，否则缺省）。**与上游差异**：TS 用 `Infinity` 表示
  “永不停放”，C# 用 `int.MaxValue`。
- `restarts` / `abortReason` / `abandonOnRestart` 都是记录内 JSON 字段（序列化随 `TaskRecord` 走），
  因此 SQLite 不需要第三段迁移；第三十一批的 v2 表已经容纳 `sleeping`。

**2. 调度器（`src/Pi.Durable/Harness/Scheduler.cs`）**

- `RestartLimit()`（设置抛出时上报并回落缺省）、`Parked(record)`（`!abortRequested && restarts >= limit`）、
  `UnparkRaised()`（每遍保留前把“上限上调后不再停放”的任务移出 `_parkedTasks` 并通知任务图）、
  `NotifyBlocked()` / `SubscribeBlocked(listener)`（阻塞原因可在不提交的情况下改变的通道）。
- `OpenAsync`：幸存的 `running` 记录 `restarts + 1` 并改回 `pending`；达到上限且没有 abort 标记的任务在提交后
  逐个上报 `Task ... was cut off N times without progress; it is parked until retryTask()`。`abandonOnRestart`
  的记录进 `_abandoned`。
- `ReserveAsync`：第一遍只为 `_abandoned` 打 `restart` 标记（并置 `_dirty` 再跑一遍），后续遍才看到标记；
  每遍先 `UnparkRaised()`；停放的任务略过并登记（新停放时通知任务图）；带 abort 标记且 `restarts >= limit`
  的任务直接 fault（`detail.reason = "restart_limit"`），因为 abort 处理器被切断会永远重跑，没有东西能停放它；
  abort 模式下 `abortReason` 为空的放行任务以 orphaned 结算，带 `restart` 标记的等定义。
- `RetryAsync(id, ctx)`：清 `Restarts` 后 `Resume()`；未停放的任务什么也不做，未知任务报错。
  `IHarness.RetryTaskAsync` / `HarnessImpl.RetryTaskAsync` 暴露它。
- `AbortAsync(id, ctx, keepRestart = false)` 与 `AbortConversationAsync` / 级联标记统一走
  `WithAbortMark(record, reason?)`：不给原因为中止请求，替换 `restart` 标记；两者都把 `restarts` 归零，
  此后计数改计 abort 处理器被切断的次数。
- `CommitStateAsync` 用 `SameState(current.State, next)`（JSON 形状比较）判断进展：只有真正不同的状态才
  `Progressed(record)` 清零计数，同一 checkpoint 再提交（副作用前的意图）不算。
- `WithState` 在终态/completing 时同时丢掉 memos 与 `restarts`（回执不带计数）。
- `ComputeActive`/`Wake` 略过停放任务：停放的工作不需要进程，宿主的唤醒因此可以落下（上游同语义）。
- **`writeAtClose`（关闭写回）**：`TaskSchedulerOptions.WriteAtClose` + `Session.WriteAtCloseAsync(writes)`
  （protected，线上、封印后直接落库、不发布）。`JoinAsync` 先取消并加入每个调用，再把仍是 `running` 的任务
  写回 `pending`——关闭不是崩溃，下次 open 不计重启。写回失败保持 `running`，按被切断计。
- `Resolve` 的迁移失败分支补上 `NotifyBlocked()` + `ScheduleWake()`：阻塞原因不提交就变了，任务图与宿主必须听到。

**3. 任务图（`src/Pi.Durable/Harness/TaskGraphView.cs`）**

- `TaskGraphNode.Blocked`（`string?`）+ `TaskGraphReader` 读取；`NodeOf(record, conversations, blocked)`。
- `TaskGraphBlocking`（`Blocked` + `SubscribeBlocked`）由 `HarnessImpl` 接到调度器的 `BlockedReasonOf` /
  `SubscribeBlocked`；`BlockedReasonOf` 实现上游 `blockedReason()`：`running`/`completing`/`terminal` 永不阻塞，
  纯中止请求不阻塞，`Parked` → `restart_limit`，否则按注册表产生 `missing_task` / `task_too_old` /
  `migration_failed`（注册表快照失败只上报一次）。
- `Mount.Records`（key → 记录）+ `RefreshBlocked()`：注册表变化或取消停放时在 Session 线上重新派生每个节点的
  原因并发布差量；`Publish(mount, ops, context)` 从 `Advance` 抽出来复用。

**4. 测试（`tests/Pi.Durable.Tests/HarnessRestartTests.cs`，10 例）**

停放后显示原因并 `RetryTaskAsync` 重跑、close 不计重启、abort 处理器被切断即 fault、上调 `restartLimit` 放行、
`abort_reason` 为 restart 的放弃任务等待定义、abort 处理器照常运行、进展清零、同 checkpoint 再提交不算进展、
无定义任务的 `missing_task` 与注册表变化后的唤醒、迁移失败的 `migration_failed` 与宿主唤醒。
崩溃用「放弃旧 Harness（不关闭）+ `MemoryStorage.Reopen()` 快照里仍是 `running`」模拟，与上游的存储代理挂死等价。
顺带更新三处 fixture：`TaskSchedulerOptions.WriteAtClose`（测试直落 Storage）、`TaskGraphView` 的三参构造、
`Settings.RestartLimit`。

**与上游差异（4 条）**

1. 报告信息：本批上报停放/迁移失败的错误文本；`ReportInfo`（`task_parked` / `migration_failed` 的
   `type`、`task`、`extension`）已在第三十三批补齐。
2. `Infinity` 用 `int.MaxValue`；`restartLimit` 的 `Math.floor` 无对应（C# 已是整数）。
3. `abandonOnRestart` 只经 `TaskOptions` 开放：端口没有嵌套工具调用的子任务。
4. `writeAtClose` 以 `TaskSchedulerOptions` 委托 + `Session.WriteAtCloseAsync` 表达；上游是同一份代码里的
   protected 方法，端口按既有分层（Session ← Harness ← Scheduler）拆开。

### 6.aa 第三十一批（durable：持久睡眠与宿主定时器）落地明细

上游 `f75b3240a`（1.4.0）把「阶段内 sleep」改成**持久的睡眠状态**：任务以
`runtime.sleepUntil(until, checkpoint)` 或 `commit` 返回 `{status:"sleeping", until, checkpoint}` 结束
一个阶段，此后不占调用、跨重启存活，到点在原 checkpoint 处恢复；同时把 Harness 的定时器交给宿主
（`setTimer`）并让宿主自己唤醒进程（`wake()` / `onWake`）。

**定时器（新 `Pi.Durable/Timers.cs`）**

- `SetTimer` 委托：`(callback, delayMs, TimerOptions) => Action?`，返回解除函数、`null` 表示宿主拒绝；
  `TimerOptions.Background` 区分「只释放内存」与「为工作调速/恢复」的定时器。
- `NativeTimer`：默认实现，用 `System.Threading.Timer` 分步等待（单步上限 `2_147_483_647` ms，与上游同一常量）。
  **与上游差异**：.NET 的定时器回调跑在线程池线程上，挂起的定时器从来不保持进程存活，也没有
  `unref()` 可言，因此端口没有「背景定时器不可用」的环境——`NativeTimer` 永远能布设背景定时器，
  上游 `inWorkers` / `unref` 的两条拒绝路径在端口不存在。
- `HostTimers.Create(setTimer, report)`：前端定时器被宿主拒绝或抛出时以 `NativeTimer` 兜底，**只上报第一次**
  （消息为 `HarnessOptions.setTimer declined a timer that paces work; using the default timer`）；
  背景定时器抛出时报错并拒绝。

**睡眠状态**

- `TaskStatus.Sleeping` + `TaskState.Until`；`TaskInspection.Sleeping(Until)`；`TaskGraphState.Sleeping(Phase, Until)`
  （JSON 里多 `until` 字段）；`TaskStatus` 的线上名由 `CamelCaseEnumConverter` 自动给出 `sleeping`。
- SQLite schema **v2**：SQLite 改不了 CHECK 约束，按上游做法重建 `tasks` 表（`tasks_v2` → 拷数据 →
  `DROP` → 重命名 → 重建五个索引）。v2 只加 `sleeping`；crash-loop guard 的 `restarts` 是记录内字段，
  不占列，等那一批再动。
- `OpenAsync` 只把 `running` 核回 `pending`，**`sleeping` 原样保留**——这正是跨重启睡眠的持久性所在。

**调度器**

- 新增选项 `OnWake`（`HarnessOptions.OnWake`）与必需项 `Timers`（`HarnessOptions.SetTimer` 经 `HostTimers` 包装）。
- `Wake()`：`Active` 由存活记录判定（跳过 `completing`、等待存活任务的等待、未到点的睡眠；带 abort 标记的
  任务若其下有存活普通自有工作也不计入；定义缺失/过旧/迁移失败的任务不计入），`At` 是尚未到点的最早睡眠。
- `ScheduleWake()` 在**线外**重算并去重：没有 `onWake` 时布设内建定时器（仅在调度启用时），有 `onWake` 时只在
  值变化时投递、且**只有投递成功才记账**（抛出会被上报，下一次变化或 kick 会再给一次）。
- `ArmSleepTimer(at)` 用前端定时器；触发时清空自身、调用 `WakeDue()` 再 kick，触发得早只会重新布设；
  睡眠被中止或调度器 `Seal()` 时解除。
- 保留遍用 `NotDue(record)` 跳过未到点的睡眠（`until > now` 且无 abort 标记）；`WakeDue()` 在端口里就是
  「重新发起一遍调度」——端口的保留遍本来每次扫描全部存活记录，不需要上游的 `#runnable` 集合。
  `Resume()` 先 `WakeDue()` 再 kick，宿主 alarm 因此可以直接调 `Resume()`。
- `CommitStateAsync` 校验睡眠：abort 处理器不能睡眠；`until` 必须是**非负**时刻。
  **与上游差异**：C# 的 `long` 没有 `NaN` / `Infinity`，所以上游那两条「不是毫秒时刻」的拒绝分支在端口
  退化为「缺 `until`」与「负数」两种。
- **与上游差异（并发模型）**：上游单线程，`wake()` 可以随意读内存；端口的提交在 Session 线上、唤醒在
  `Task.Run` 里。`Wake()` 因此对「与提交并发」做保守降级（捕获 `InvalidOperationException` → `Active = true`），
  并用线上维护的 `_earliestSleep` 作为降级路径的 `At`；定时器的布设不依赖成功的内存扫描，
  失败也照样按 `_earliestSleep` 布设，避免睡眠任务再没人唤醒。

**运行时与内建任务**

- `ITaskRuntime.SleepAsync(until, ctx)`（阶段内干等）**删除**，换成
  `SleepUntilAsync(until, checkpoint, ctx)`（经 `GatedAsync` 提交睡眠后立即返回，阶段随即结束）与
  `SetTimer(callback, delayMs)`（登记进 `Invocation.Timers`，调用结束时解除；已结束的调用永不触发回调）。
- 生成：`retry` / `poll` 阶段先判 `checkpoint.Until > runtime.Now()` 再睡（旧版本的记录、提前唤醒都会再睡一次）；
  分类时把退避与延迟轮询提交成 `status: "sleeping"` 并带 `Until`。
- 压缩：同样的 `retry` 处理与 sleeping 提交。
- 节流：partial 提交（`PartialThrottle`）与工具进度提交改为走 `runtime.SetTimer` + Harness 时钟
  （`ToolOutput.ProgressClock`，缺省仍用进程时钟）；`IToolExecutionApi.Now()` 补上，脚本/工具可直接比较
  重启后的时刻。
- 上下文过期定时器也改走 `Timers.Background`：宿主拒绝时不布设，后续提交仍会检查过期（语义不变，
  只是可以被宿主的 `setTimer` 接管）。**与上游差异**：端口 `DurableSession` 没有文档保留期的清理定时器，
  所以 `setTimer` 只接到调度器与工具运行时，没有上游 `createSession(..., setTimer)` 那条路径。

**测试**

- 新增 `HarnessSleepTests`（12 例）：不持调用地睡住并在到点恢复、检查面 `sleeping`、`Wake` 形状与提前唤醒
  不干活、跨重开仍睡（重开后仍 `sleeping`，到点才完成）、由定时器唤醒、任何一遍调度都能唤醒到点睡眠、
  中止与关闭时解除定时器、调用结束时解除其定时器（迟到触发不做事）、有 `onWake` 时不布设定时器、
  宿主拒绝前端定时器时兜底并只上报一次、睡眠任务不用等到点就能跑 abort 处理器、非法 `until` 与 abort
  处理器睡眠被拒、缺定义的任务不计为 active、`onWake` 抛出被上报且下一次仍会再投递。
- `StorageConformance` 新增「stores and scans sleeping tasks by status」（4 个后端各跑一次）。
- 更新既有用例：`HarnessGenerationRecoveryTests` 的退避/轮询恢复改为断言 `sleeping` 并推进假时钟
  （含一次「提前醒来不重试」）；`HarnessCompactionTests` 的压缩退避恢复同样断言 `sleeping`；
  `SchedulerTests` 里两处「阶段内干等」改用 `Task.Delay`（持久睡眠会让阶段结束，语义不同）。

### 6.a 第三十批（durable：按模型压缩阈值）落地明细

上游 `#10942`（1.4.0）：`settings.compaction.modelOverrides` 以精确的 `"provider/modelId"` 为键，
让同一个 harness 同时服务 32k 与 1M 上下文窗口的模型。三个位置要用「该模型的策略」而不是全局策略：
阈值判定、上下文溢出后的恢复、压缩任务取范围。

- 端口本来把 `CompactionSettings` 与 `CompactionPolicy` 合成一个 `CompactionPolicy`，所以这里只在它上面
  加两样：`ModelOverrides`（`IReadOnlyDictionary<string, CompactionPolicyOverride>`）与
  `ForModel(ModelRef?)`（逐字段覆盖，缺省字段沿用全局；键不匹配或没有覆盖时**返回自身**，
  省掉无意义的分配）。`CompactionPolicyOverride` 的四个字段都可空 —— `0` 与「没写」必须区分得开。
- 三处接入：`Generation.cs` 的 `ThresholdCompaction(...)` 传
  `settings.Compaction.ForModel(modelReference)`（用当前 agent 的模型，而不是 checkpoint 里的，
  这样换模型后立即生效）；溢出恢复分支用 `settings.Compaction.ForModel(request.Model)` 判定
  `Enabled` 与 `KeepRecentTokens`；`Compaction.cs` 的 `SelectPhaseAsync` 用
  `settings.Compaction.ForModel(reference)`（摘要输出上限的 `0.8 * ReserveTokens` 因此也随模型走）。
- 测试（`Pi.Durable.Tests` +5）：`CompactionPolicyTests` 3 例（只覆盖写了的字段、`0` 生效、
  未知模型/`null` 模型回落到全局、大小写与前缀都不算匹配）；`HarnessCompactionTests` 2 例
  —— 全局 `Enabled = false` 而覆盖给当前模型打开时，溢出仍触发压缩并重试成功（5 次调用）；
  覆盖写成别的 `"other/model"` 时行为与「压缩关闭」一致（`unanswered` / `model_error`、无压缩条目）。

**与上游差异**：上游的 `compactionPolicy(settings, model)` 是独立函数，入参是
`CompactionSettings`（含 `modelOverrides`）；端口没有第二层类型，方法挂在 `CompactionPolicy` 上。
语义一致：解析后的对象不再携带 `ModelOverrides`（避免「解析过的策略又被解析一次」时把别的模型
的覆盖带进来）。

### 6.b 第二十九批（扩展对话框栈 + overlay 归属）落地明细

两个都在 `showExtensionSelector` / `showExtensionInput` / `showExtensionEditor` /
`showExtensionCustom` 这一片，合批处理。

**① 并发扩展对话框改为栈**（`38847563f`，#11166 / #9909）

- 原来三个字段 `_extensionSelector` / `_extensionInput` / `_extensionEditor` 各管一种对话框，
  后开的直接顶掉前一个：前一个的 Promise 永远不 settle，而且关闭时无条件把编辑器塞回容器。
- 改为 `_extensionDialogs`（`List<ExtensionDialogEntry>`，条目是
  `record(Component, Blocked)`，`[^1]` 即可见的那个）+ 三个方法：
  `PushExtensionDialog`（先 `DisposeActiveSelector()`，再入栈、刷栈顶）、
  `RemoveExtensionDialog`（按引用找到后移除，只有移除的是栈顶才刷）、
  `ShowTopExtensionDialog`（把栈顶或编辑器装进编辑器容器、交焦点、按**栈顶**上报
  `extension-dialog` 的阻塞态、请求重绘）。四个 show 方法各自收敛成「建组件 → 一次
  `Finish(value)`（释放注册、释组件、出栈、完成 Promise）→ 入栈 → await」。
- `ResetExtensionUI` 改成「逐个释放库存对话框 → 清空 → 刷栈顶」，不再依赖各 Hide 方法。
- C# 侧两处细节：①闭包不能引用**后**声明的局部，所以组件变量先声明为可空、构造后再赋值；
  ②取消注册可能在组件挂载**之前**触发（另一线程 `Cancel()`），挂载后补一个
  `completion.Task.IsCompleted` 分支把刚入栈的对话框摘掉，避免留在屏幕上。
- 「阻塞态」的 source 从「每个对话框一个 `extension-dialog:N`」收敛成单一的 `extension-dialog`，
  只由 `ShowTopExtensionDialog` 写——与上游一致，也顺带修掉了原来 `ResetExtensionUI` 直接藏对话框、
  却不曾清掉那个 key 的泄漏。

**② `ctx.ui.custom({overlay:true})` 只关自己的 overlay**（`a4f67eb65`，#10648 / #10999）

- `Close()` 原来调 `_ui.HideOverlay()`，那是「移除栈顶」：下层 overlay 结束时会把上层一起抹掉，
  上层对话框的 Promise 就此无解。
- 改为 `overlayHandle?.Hide()`：句柄在 `ShowOverlay` 之后立刻保存，同一个包装也交给
  `options.OnHandle`，所以外层拿到的和内部关闭的是同一个对象。

**测试**（`InteractiveModeTests` +5）

`ConcurrentConfirmsStackAndRestoreTheEarlierDialog`（后开的挡在前面、关掉后露出前一个、最后回到编辑器）、
`AbortingAHiddenDialogRemovesItAndLeavesTheVisibleOne`（信号取消的是**隐藏**的那个，栈顶不受影响）、
`ConcurrentInlineCustomComponentsStack`（两个内联自定义组件，按后进先出各自 settle）、
`ConfirmOpenedFromAnInlineCustomComponentRestoresTheComponent`（自定义组件里再开确认框，关掉后回到组件本体）、
`ClosingALowerOverlayHidesItsOwnOverlayNotTheTopmost`（两个 overlay，关下层后上层仍聚焦、
`UiForTests.HasOverlay()` 仍为真）。最后这条做过反向验证：把 `overlayHandle?.Hide()` 换回
`_ui.HideOverlay()` 时它以 `Assert.False()` 变红。

**与上游差异**：上游测试用 Proxy 伪造 `this`（`InteractiveMode.prototype.showExtensionCustom.call(fakeThis, …)`
配 VirtualTerminal 的 viewport 断言）；端口是真实 `InteractiveMode` + `StringTerminal`，所以断言换成
「编辑器容器的栈顶组件渲染文本」+ `OverlayHandle.IsFocused()` / `UiForTests.HasOverlay()`，
覆盖同一批行为。

### 6.c 第二十八批（配置文件 JSON Schema + 移位符号键位归一化）落地明细

上游 `7f9e1198f`（#9880，1.4.0）把四个配置文件的 schema 从 TS 源码生成并发布到 pi.dev，
`96abe9a5a`（#10751）再把地址统一到 `https://pi.dev/schemas/`。端口取其中与「用户可见产物」
有关的两块，运行期那套 TypeBox 重构没有对应物。

**① 四份 schema 产物**

- `Assets/schemas/{settings,models,theme,keybindings}.schema.json` 从 v1.4.0 逐字节复制，
  `$id` 仍是 `https://pi.dev/schemas/<name>.schema.json`、draft 为 2020-12。
  csproj 里按 `Assets/theme` 同样的方式登记：进构建输出、随 NuGet 作为 `contentFiles` 分发，
  用户可以拿本地路径引用，也可以直接指向发布地址（与上游文档一致）。
- 内置主题 `Assets/theme/{dark,light}.json` 的 `$schema` 从 `raw.githubusercontent.com/.../
  theme-schema.json` 改为 `https://pi.dev/schemas/theme.schema.json`（`96abe9a5a` 原文）。
- 示例 `Assets/examples/extensions/dynamic-resources/dynamic.json` 同步同一地址。
- 文档同步四篇：`settings.md` 顶部加 `"$schema"` 一行、`themes.md` 两处链接换成发布地址、
  `keybindings.md` / `models.md` 的示例块补 `$schema` 键。
- `ConfigurationSchemaTests`（8 例）钉住产物一致性：四份文件都在、`$id` 与 draft 正确、内置主题的
  `$schema` 指向发布地址、**theme schema 的 51 个必需色与 56 个已描述色和两套内置主题完全对齐**
  （任何一侧漂移都会红）、settings schema 覆盖 `outputPad` / `showHardwareCursor` / `terminal` 等
  键、keybindings schema 枚举了各批次落地的动作 id 且仍允许 `additionalProperties`。

**② 移位符号键位归一化**（同一提交里的 `fix(tui): normalize shifted symbol keybindings`）

- Kitty 把 `+` 报成「Shift + 物理 `=` 键」（`CSI 61:43;2u`），并把产生的符号放在 shifted-key 位。
  上游新增 `SHIFTED_SYMBOL_KEYS` 与 `getLogicalShiftedSymbolCandidate`：命中时**吃掉隐含的 Shift**、
  保留 Ctrl/Alt/Super，于是 `matchesKey(…, "+")` 与 `matchesKey(…, "ctrl++")` 都成立，而
  `shift+=` 这种物理写法仍走原来的直接匹配。端口照抄进 `Keys.cs`
  （`ShiftedSymbolKeys` / `GetLogicalShiftedSymbolCandidate`），三处接入：
  `MatchesKittySequence`（物理匹配不中时再试逻辑匹配，之后才 `if (actualMod != expectedMod) return false`）、
  `ParseKey` 的两条分支（Kitty 与 modifyOtherKeys）、以及 `MatchesKey` 的可打印兜底
  （未带修饰键时也要试 `MatchesPrintableModifyOtherKeys(data, cp, 0)`）。
- `MatchesPrintableModifyOtherKeys` 去掉了 `expectedModifier == 0` 的早退——否则裸 `+` 永远匹配不上
  modifyOtherKeys 形式；Shift 的消耗改用 `SHIFTED_SYMBOL_KEYS`（不含 `` ` `` `-` `=` `[` `]` 等
  无需 Shift 的符号），避免把 `shift+=` 误判成 `=`。
- `ParseKeyId` 重写：按 `shift → alt → ctrl → super` 的固定顺序**逐段**消费前缀，重复前缀或
  残留 `+`（除裸 `+` 本身）一律判无效。旧实现 `split("+")` 取最后一段，`ctrl++` 因此被读成
  「key 为空」而直接丢弃。测试：
  `MatchesKey_NormalizesShiftedSymbolKeybindings`（Kitty 与 modifyOtherKeys 两条路径）、
  `MatchesKey_RejectsMalformedKeyIds`（重复修饰符、残留 `+`、空 id、裸 `+` 仍可用）。

**与上游的差异**：schemas 在端口里是**静态产物**，没有 `scripts/generate-schemas.ts` 的对应物——
生成器读的是 TS 源码里的 TypeBox 定义，端口没有那层定义，重新生成只能手写。
运行期校验（`settings-schema.ts` / `theme-schema.ts` / `keybindings-schema.ts` 的 `Compile`）
也没有移植：端口继续在各自的加载器里校验，`SettingsManager` 的 getter 兜底坏值——见 §6.z 的
既有说明。schema 与实现的偏差靠 `ConfigurationSchemaTests` 兜，不靠运行期。

### 6.d 第二十七批（TUI 零碎修复：fd 转义 / 退出复原 / 滚轮路由 / 回复串）落地明细

四个互不相干的小修，合批是为了保持「一次验证、一次提交」的节奏。

**① fd 单段查询转义正则元字符**（`612a172e6`，#11212 / #10972）

- `CombinedAutocompleteProvider.BuildFdPathQuery` 的「不含分隔符」分支原来直接返回原串，而这条串是
  交给 `fd` 的正则：`@pages/[` 于是被 `fd` 当成字符类，永远补不出 `pages/[id].tsx`。
- 改法是这一支也走 `EscapeRegex`，并删掉 `segments.Count == 0` 的死分支（`trimmed` 为空时上面已经
  返回过）。多段路径的 `\` / `[` / `(` 一并被转义。
- **语料**：`autocomplete-corpus.json` 的 44 条 `buildFdPathQuery` 向量里有 4 条期望值随之变化
  （`(@REA` / `foo(@REA` / `see (` / `x [y]`）。生成器（`gen.mjs`）不在仓库里，改用
  `tools/regen-autocomplete-fdquery.mjs` 重算：脚本里是 v1.4.0 那三个纯函数的逐字副本，只重算
  `expected`、不动输入与顺序，写回前先确认 `JSON.stringify(JSON.parse(raw)) === raw`（文件是压缩
  单行格式，重序列化逐字节一致）。
- 另加 `EscapesRegexMetacharactersInSingleSegmentFdQueries` 钉住上游 #10972 的三个场景
  （`@pages/[` / `@pages/(` / `@[id`）与多段路径的分隔符类。

**② 进程直接退出时复原终端模式**（`9df819de3`，#11170）

- 上游在 `TuiBase.start()` 注册 `process.on("exit")`，在 `stop()` 注销；端口对应
  `AppDomain.CurrentDomain.ProcessExit`（`-=` 后 `+=`，委托按 target+method 相等，所以能正确去重）。
- `ITerminal.RestoreOnExit(before, after)`（带默认空实现，测试替身不必实现）：
  `ProcessTerminal` 写「进度条清除（仅进度计时器在跑时）+ `ESC[?2004l` 关括号粘贴 + KitKat 键盘
  `ESC[<u`（仅协议启用时）+ `after`」，然后走既有的 `Write`（本来就是同步 `Flush`）。
  `StringTerminal` 记到 `ExitRestores`。
- 前缀/后缀由 `TuiBase.GetExitRestorePrefix/Suffix` 提供：前缀是「关配色通知（若曾开）+ `ESC[?25h`」，
  `TuiAltScreen` 覆写后缀追加 `ESC[?1049l ESC[?25h`——Kitty 键盘标志是按屏的，必须先弹键盘协议再
  退出备用屏。
- **偏差**：端口从未推送 `modifyOtherKeys`（`ESC[>4;1m`），所以没有上游那一句 `ESC[>4;0m`；
  `keyboardProtocolPushed` 在端口不存在，用 `Keys.IsKittyProtocolActive()` 代替。
- 硬杀 / 原生崩溃不跑托管代码，与上游的「JS 也跑不到」是同一类缺口。

**③ 聚焦 overlay 时未处理的滚轮改滚动转写**（`11f991145`，#10769）

- 端口 `HandleViewportInput` 的滚轮分支删掉 `ShouldDeferViewportInputToOverlay()` 早返回。
  语义：滚轮先给 overlay 的 `handleMouse`，命中但没处理（或没命中）就滚转写，而不是把裸序列
  转给焦点组件的 `handleInput`。想用滚轮的 overlay 必须实现 `handleMouse`。
  键盘路径（`\x1b[5~` 这类）仍然 defer，行为不变。
- **语料**：`alt-screen-corpus.json` 的 `overlay-focused-routing` 场景整段受影响——滚轮那一步之后
  `viewportTop` 少 1、屏幕行整体上移一行、`isFollowingOutput` 由 true 变 false，且 overlay 不再收到
  `ESC[<64;10;3M`。生成器同样不在仓库，处理方式是把该场景的 `observations` 与 `overlayInputs`
  按新语义重录（压缩格式下的定点更新），并逐条核对差异只有上述三类。
  `search-navigate-buttons` 在这个修复前偶发失败过一次，隔离与全量复跑均通过——它是
  `GlobalKeybindings` / `TerminalImage.SetCapabilities` 这两个静态缝在并行套件下的老问题，与滚轮无关。

**④ 延迟到达的终端回复不再变成按键**（`edb2eb966`，#10256 / #10657）

- `StdinBuffer` 新增 `TerminalReplyHeaderPattern`（OSC 10/11、OSC 4;n;、OSC 7501;?、XTVersion
  `ESC P>|`、Kitty 图形 `ESC _G…;`）与 `IsControlString`；`ExtractCompleteSequences` 在识别出回复头
  之后，遇 `CAN`/`SUB` 直接丢弃整条串、遇新的 `ESC`（非 `ESC \`）回退到它之前重新解析；
  `ProcessCore` 的兜底超时对「回复头已成立」的缓冲**不再冲刷**（原来会把尾巴当文字，OSC 的 BEL
  还会变成 Ctrl+G，即外编辑器快捷键），而其它输入（含 `Alt+]` 后跟文字）仍按超时恢复，
  冲刷时把 `ESC]` 两个字符单独发出去、余下重新解析成按键。
- 端口 `StdinBuffer` 至今没有接进 `ProcessTerminal`（读取走 `Console.ReadKey`，见 §6.f），所以这条
  修复目前只让这个类与上游 1.4.0 对齐；`docs/upstream-1.4.0-delta.md` §6.f 里「`stdin-buffer.ts` 的
  `TERMINAL_REPLY_HEADER_PATTERN` 改动未跟」这一条到此关闭。
- 测试 23 例：6 条回复串 × 表头之后的每一个切分点都能整条保住；跨多次超时仍缓存；裸 `ESC]` /
  `ESC P` / `ESC _` 仍按 Alt 键超时；11 条歧义输入在超时后恢复成「Alt + 逐键」；表头在歧义窗口内
  拼齐；未终结的回复遇到新转义序列让位；`CAN`/`SUB` 取消不吞后续输入。

**测试**

`Pi.Tui.Tests` 23929 / 23929（+28：23 例 `StdinBufferTests`、3 例 `TerminalRestoreTests`、
1 例 `AltScreenTests.UnhandledWheelInputScrollsTheTranscriptUnderAFocusedOverlay`、
1 例 `AutocompleteTests.EscapesRegexMetacharactersInSingleSegmentFdQueries`），
`Pi.CodingAgent.Tests` 5029 / 5029，其余 11 个测试项目全绿。
