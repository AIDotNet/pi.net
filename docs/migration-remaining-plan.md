# coding-agent 剩余迁移：现状分析、迁移清单与分阶段计划

> 生成时间：2026-10-13（2026-10-16 修订：参照基线切换 + 新增 R2 漂移对账阶段 + S3c 收口；**2026-10-18 修订：剩余工作全量重清点 + S6/S7 阶段计划**）
> 参照源：`D:\AI\参考项目\pi`（`packages/coding-agent`），基线 commit **`eb326d265`（= 当前检出，2026-10-18 复核无漂移）**
> 目标仓库：`D:\AI\参考项目\pi.net`，当前 commit **`70c9e9f`**（S0 `42ed85c` → S1 `4ebf6fa` → S2a `3b1346a` → S2b `e657dac` → S3a `2c74e2c` → S3b `a633cb2` + `97eae94` → S4 `3cd141d` → S3c `413718a` → 4f-3d/3e/4 各批 → 4f-4m-1～4 → /name `7a64200` → 4f-4m-5a/5c `9b9d67e` → 4f-4m-5b `159fda6` → 4f-4m-5c 余量 `4b9180d` → compact 公共入口 `9b7bc1a` → 4f-4m-6 `c0ffee2` → 4f-5 `68812e9` → 4g-1a `3d61bbc` → 4g-1b `190814b` → 4g-1c `364926c` → 4g-2 `5c1d79a` → 4g-3a `1b4be2c` → 4g-3b-1 `cc5bcaa` → **4g-3b-2 `70c9e9f`**）
> 本文是**执行台账**：每完成一个阶段在此更新「进度台账」小节。逐文件的移植进度与设计差异
> 仍以 [coding-agent-porting-status.md](coding-agent-porting-status.md) 为准（差异编号 `C##` 已用到 `C318`）。

---

## 零·补、2026-10-18 修订：剩余工作全量重清点（S6 计划）

### 0.6 动工前核对（约定执行）

参照检出实测 `eb326d265`，与台账记录一致，**无新增漂移**（`eb326d265..HEAD` = 0）。
目标仓库工作区干净，HEAD `59c0850`；`dotnet build Pi.slnx -m:1` 实测 **0 警告 0 错误**。

### 0.7 剩余清单（按当前检出逐文件核对，取代 2.2 节的旧快照）

**4f-4m 收尾**（`interactive-mode.ts` 剩余成员）：✅ **已全部收口**（`c0ffee2`，见 0.12——
命令族 16 个 + 登录族 17 成员 + share/bug 两模块 + 提交 if-chain + 三缝清零 + 字段块 pragma 摘除）。

| 块 | TS 位置 | 内容 | 依赖 |
| --- | --- | --- | --- |
| 命令族前半 | L5124-5536 | `handleThinkingCommand`/`selectThinkingLevel`/`showThinkingSelector`、`handleModelCommand`/`findExactModelMatch`、`handleCloneCommand`、`showTrustSelector`、`maybeSaveImplicitProjectTrustAfterReload` | ✅ `9b9d67e`；ThinkingSelector/TrustSelector 组件已就绪 |
| 登录认证族 | L5767-6397 | `getLoginProviderOptions`/`getLogoutProviderOptions`/`findLoginProviderOptions`/`handleLoginCommand`/`startProviderLogin`/`showLoginAuthTypeSelector`/`showLoginProviderSelector`/`showOAuthSelector`/`completeProviderAuthentication`/`showAmbientAuthDialog`/`showApiKeyLoginDialog`/`showAuthSelect`/`showAuthPrompt`/`notifyAuthDialog`/`loginProvider`/`showLoginDialog`/`offerRadiusMcpServer` | ✅ `159fda6` |
| 命令族后半 | L6403-7061 | `handleReloadCommand`/`handleExportCommand`/`handleImportCommand`/`handleShareCommand`/`handleBugCommand`/`handleSessionCommand`/`handleChangelogCommand`/`handleHotkeysCommand`/`handleArminSaysHi`/`handleDementedDelves`/`handleBashCommand`/`handleCompactCommand` | ✅ `9b9d67e` + `4b9180d` + `9b7bc1a`（含 `AgentSession.compact` 公共入口） |
| 提交 if-chain | L3179-3378 | `setupEditorSubmitHandler`（26 个命令分支 + bash 分支 + 压缩/流式排队） | ✅ `c0ffee2` |
| 剪贴板粘贴 | L3132-3172 | `handleClipboardPaste` | ✅ `9b9d67e` 提前落地 |
| autocomplete 组装 | L718-835 | `createBaseAutocompleteProvider` + `setupAutocompleteProvider` | ✅ `9b9d67e` 提前落地 |

> **台账旧记载修正**：`/help` `/tools` `/extensions` `/terminal-setup` `/models` 五个命令在
> **当前检出中不存在**（全网检索确认），旧台账「命令族缺口」一节列出它们有误——以当前检出为准，
> 实际剩余命令以上表为准。

**4f-5**（~2,043 行 TS）：`modes/rpc/rpc-types.ts`(303) / `rpc/jsonl.ts`(58) /
`rpc/rpc-client.ts`(617) / `rpc/rpc-mode.ts`(819) / `print-mode.ts`(169) /
`json-event.ts`(61) / `modes/index.ts`(16)。✅ **已全部收口**（`68812e9`，见 0.13：
类型层 + JSONL 线路层 + 事件投影 + RPC 客户端 + RPC 模式 33 命令分支 + print 模式；
Core 侧补 `GetSessionStats()` 实例包装 / `SetAutoRetryEnabled` / `ExportToHtmlAsync`；
`AgentSessionEvent` 22 变体多态注册）。

**4g**（~13,600 行 TS）：`4g-1` `cli/*`（12 文件 1,455 行）+ `cli/experimental/*`（504 行）——
**4g-1 ✅ 全部收口**（1,455 + 504 行）：
4g-1a `3d61bbc`（见 0.14：`args.ts` 479 / `initial-message.ts` 43 / `setup.ts` 13 /
`auth-command.ts` 126 = 661 行 + 597 例差分语料）；
4g-1b `190814b`（见 0.15：`startup-ui.ts` 253 / `list-models.ts` 115 / `credential-print.ts` 87 /
`file-processor.ts` 88 / `auth-check.ts` 73 / `project-trust.ts` 62 / `config-selector.ts` 61 /
`session-picker.ts` 55 = 794 行 + 60 例）；
4g-1c `364926c`（见 0.16：`cli/experimental/*` 5 文件 504 行 + 62 例）。
`4g-2` `main.ts`(999) / `index.ts`(494) / `package-manager-cli.ts`(1,141) / `cli.ts`(6) /
`rpc-entry.ts`(13)——**4g-2 ✅ 全部收口**（`5c1d79a`，见 0.17：移植 2,159 行 + 新建
`Pi.Cli` 可执行工程；`index.ts` 判定为纯 re-export barrel 不单独移植）+ 71 例；
`4g-3` `experimental/*`（46 文件 9,950 行，依赖已移植的
Pi.Durable/Pi.Chord/Pi.Server/Pi.Client/Pi.Protocol/Pi.Telemetry）+ `bun/*`(61) + `client/*`(1)——
**按依赖层次切 7 个子批**（`experimental/` 无任何外部引用，是一组独立实验入口）：

| 子批 | 范围 | 行数 | 状态 |
| --- | --- | --- | --- |
| **4g-3a** | `session-catalog.ts`(87) + `process.ts`(106) | 193 | ✅ `1b4be2c`（见 0.18，+58 例） |
| **4g-3b** | `services/*`（14 文件）：契约 7 文件 + provider 7 文件 | ~1,420 | 🟡 契约层 ✅ `cc5bcaa`（0.19）+ provider 前半 ✅ `70c9e9f`（0.20）；余 5 文件 |
| 4g-3c | `coordinator.ts`(612) + `coordinator-entry.ts`(10) + `plugins/*`(226) | ~848 | |
| 4g-3d | `server.ts`(770) + `session-worker*.ts`(1,688) + `radius-relay.ts`(724) + `radius-auth.ts`(67) + `commands.ts`(98) + `cli.ts`(12) | ~3,359 | |
| 4g-3e | `client.ts`(86) + `client-runtime.ts`(230) + `client-tui.ts`(809) + `client-tui-chat.ts`(228) | ~1,353 | |
| 4g-3f | `durable/*`（9 文件） | ~1,440 | |
| 4g-3g | `vacation/*`（6 文件） | ~1,305 | |
| 4g-3h | `bun/*`(61) + `client/*`(1) + `plugin.ts`(16) + `source-resolver.ts`(86) | ~164 | 平台缺口判定批 |

**P7 evals**（~1,300 行，未建项目）。

**顺延项**（不阻塞主链，随收尾阶段处理）：P4b-7 `McpManagerView`/`showMcpManager`、
P4c llama `ui.ts` 的 `LlamaView`/`HuggingFaceSearch`/`showLlamaUi`（依赖均已满足）、
4e-3c-3 package-manager update/网络检查（`CheckForPackageUpdatesAsync` 已按 TS 形状落地——
offline 短路 + 失败空表，不抛错；npm/git 版本比对的网络检查本体仍未移植）。

### 0.8 S6 阶段计划（4f-4m 收尾）

| 阶段 | 范围 | 验收标准 |
| --- | --- | --- |
| **4f-4m-5a** | 命令族前半（thinking/model/clone/trust） | ✅ `9b9d67e`（与 5c 同批提交）；`InteractiveModeTests` 94/94、全量 3204/0 |
| **4f-4m-5b** | 登录认证族（login/logout 全链，约 630 行 TS） | ✅ `159fda6`；`InteractiveModeTests` 105/105、全量 3215/0 |
| **4f-4m-5c** | 命令族后半 + `session-share.ts` + `bug-report.ts` 交互流 + `AgentSession.compact` 公共入口 | ✅ 全部收口（`9b9d67e` + `4b9180d` + `9b7bc1a`）；`AgentSession.compact` 公共入口（编排本体 + public 化 + 5 例）见 0.11 |
| **4f-4m-6** | 提交 if-chain + `handleClipboardPaste`（✅ 已随 `9b9d67e` 提前落地）+ autocomplete 组装（✅ 同批落地）+ **清零剩余缝**（`setupEditorSubmitHandler`、`custom<T>`/`customAsync<T>`、`checkForAvailableUpdates`）+ 摘字段块 pragma | ✅ `c0ffee2`（见 0.12）；`InteractiveMode` 类装配正式收口（字段块 pragma 摘除后 0 警告） |

### 0.9 5a/5c 收口记录（2026-10-18，`9b9d67e`）

- 提前落地两件原属 4f-4m-6 的前置件：autocomplete 组装与 `bindCurrentSessionExtensions`（fork/reload 路径被这两处缝阻断，不先落地则 5a 的 clone 用例无法验证）。
- 顺延项落地：ResourceLoader 的 system-prompt 来源发现（SYSTEM.md / APPEND_SYSTEM.md，项目级受信任门控）接线进 `AgentSessionBuild`。
- 三处用例修正的判定依据（均回查 TS 原文）：`playArmin3d` 在 alt-screen 下返回 true 故 fullscreen 不挂内联组件；Markdown 渲染消耗 `**` 故 hotkeys 断言渲染后纯文本；`collapseChangelog` 默认 false 故 condensed 分支需显式开启设置。
- 环境备注：Windows `cmd /c echo hi` 用例在本机 Bash 工具沙箱下挂起（安全层把派生态解析到 WSL 路径并拦截），PowerShell 工具下实测 1.97s 通过；统一改用 bash 内建 `echo hi`（TS 参照测试同款）。
- 基线核对：参照检出经 `git pull` 前进至 `eb326d265`（0.5 节「退回 `428a12bc7`」为过渡态，reflog 可见多次 ff pull）；`428a12bc7..HEAD` 触及 coding-agent 的 9 个提交与 0.1 表一致，无新增未对账漂移。

### 0.10 5c 余量收口记录（2026-10-18，`4b9180d`）

- 落地 `session-share.ts`(217) 与 `bug-report.ts`(298) 两模块全链 + `getAuthCredential` + core 侧 `collectBugReportMetadata`/`collectBugReportDiagnostics`/`bugReportFiles`/`bugReportArchiveFileName`/`uploadBugReport` 高保真重写 + `summarizeForBugReport` 包装；`/share`·`/bug` 的**提交 if-chain 分派**仍随 4f-4m-6 落地（本批经测试缝直驱）。
- **一致性检查发现并修掉两处既有移植缺陷**（均非漂移）：① `BugReport` 的 settings 序列化缺 camelCase 命名策略——`redactSettings` 按 `trackingId`/`deviceId` 摘键实际**未生效**，两个跟踪 id 会漏进报表（隐私问题），已补策略并经用例钉住；② `BugReportUpload` 多部分字段名误用 `"files"`（TS 为 `file.name`），已改并按序列化后的正文钉住线格式。另修 upload URL（TS 自引入起即 `/v1/bug-reports`）。
- 脱敏语义经 TS 对拍验证（`tools/bug-report-redaction-reference.mjs`，34 组用例）：`<redacted>` 占位、userinfo 剥离、嵌套 scheme、`isSensitiveKey` 理论 14 例（含 `privateKey`→false）。
- **事故记录**：`/bug` 冒烟用例首版把 CrashLog 的 agent-dir 环境变量名写成 `PI_AGENT_DIR`（实际为 `PI_CODING_AGENT_DIR`），导致读写并 `Clear` 了真实的 `~/.pi/agent/crashes.json`（内容为历史测试残留）。已改用 `Config.EnvAgentDir` 常量并加断言钉住隔离路径；`CrashLog.Read`/`Clear` 走全局 env 而非会话 agentDir，凡测试触及默认路径处都需显式改环境变量。
- 测试 +33 例（AuthCredentialsTests 9、BugReportCollectTests 12、脱敏扩展、/bug 全链路冒烟含 zip 内容/崩溃日志清除/会话条目断言）；全量 **3261/0**。

### 0.11 compact 公共入口收口记录（2026-10-18，`9b7bc1a`）

- 编排本体（TS `compact()` L2749-2909）随 5a 的命令族批落地为 `CompactAsync`；本批补两处：
  ① `internal` → `public`（TS 的 `compact()` 是公共 API：/compact、RPC、扩展三路共用，
  4f-5 的 RPC 层要经它进入）；② 补 5 例编排测试（`AgentSessionCompactEntryTests`）。
- 用例踩坑：切分点落在回合中部会走 split-turn 双次摘要（历史 + 回合前缀各一次流调用），
  种子数据改为「长 user + 短 assistant」使预算在最后一个 user 消息处越过，对应 TS
  `startsTurn` 分支；`fromHook` 在 TS 的 entry 里恒存在（false 也写盘），断言不能写 Null。
- 逐条核对确认与 TS 一致的分支：abort→compaction_start→prepareCompaction（两条失败文案）→
  `session_before_compact`（cancel / 自定义结果两分支）→默认摘要器→signal 中止检查→
  appendCompaction→session_compact→`_clearManualCompactionState` 先于 compaction_end→
  catch 的 aborted/errorMessage 合成与 `_emitSessionCompactFailed`→finally 清理。
- 全量 **3266/0**。**4f-4m-5c 阶段至此全部收口**，下一阶段 4f-4m-6（提交 if-chain + 清零剩余缝）。

### 0.12 4f-4m-6 收口记录（2026-10-18，`c0ffee2`）——InteractiveMode 类装配收口

- **提交 if-chain**（TS `setupEditorSubmitHandler` L3179-3375）：闭包本体落在
  `InteractiveModeSubmit.cs`（`HandleSubmitAsync` + `DispatchCommandAsync`），路由判定复用既有
  表驱动件 `InteractiveModeSubmit.Route`。26 个命令分支逐条分派，**清空编辑器的时机按 TS
  原样保留**（多数在调用处理器前；export/import/share/copy/name/session/changelog/hotkeys/
  fork/tree/trust/logout/debug/armin/demented/resume 在后）；bash 分支（busy 警告恢复原文/
  历史/`isBashMode` 复位）、压缩期排队（扩展命令直通）、流式 steer、正常提交四段尾链齐全。
- **两个缺失挂载**（if-chain 的前置）：`ShowSettingsSelector`（TS L4888-5115，37 个回调）与
  `ShowModelsSelector`（TS L5357-5465，15s 刷新竞态 + `selectionChanged` 守卫 + enabledIds
  代数 + persist 全选短路）落 `InteractiveMode.Selectors.cs`。
- **清零三处缝**：`custom<T>`/`customAsync<T>` → `ShowExtensionCustomAsync`（TS L2946-3010，
  草稿保存/overlay 与占位两路/close 幂等 + dispose/工厂失败恢复并 reject）；
  `checkForAvailableUpdates` → TS 形状（offline 短路 + 失败空表），npm/git 网络检查本体仍是
  顺延项 4e-3c-3（原先每次 online 启动都炸一个后台任务，已消除）；
  `setupEditorSubmitHandler` → 上面的闭包。
- **摘字段块 pragma**：`CS0169/CS0414/CS0649` 抑制移除后仍 **0 警告**——TS 字段块全部被
  已落地成员点亮，类装配确实完成。
- 顺手：`IOutputPadSettable` 接口（TS `"setOutputPad" in child` 鸭子探针的编译期版），
  7 个 transcript 组件实现。
- 测试 +11 例（提交链 9 + 扩展自产组件 2）；全量 **3277/0**。
- **4f-4m（interactive-mode.ts 本体）至此全部收口。** 下一阶段：4f-5（RPC/print/json ~2,043 行）。

### 0.13 4f-5 收口记录（2026-10-18，`68812e9`）——headless 三模式

- **四个新文件**（`src/Pi.CodingAgent/Modes/Rpc/` + `Modes/PrintMode.cs`）：
  类型层（33 命令判别联合 + 响应三态）、JSONL 线路层（LF-only + 自制
  `JsonStringifyEncoder` 复刻 `JSON.stringify` 转义）、事件投影
  （`toJsonEvent` 剥 partial）、RPC 客户端（27 命令 + 进程生命周期）、
  RPC 模式（33 命令分支 + 25 成员扩展 UI 协议桩 + shutdown/信号）、
  print 模式（text/json 两路 + 退出码语义）。
- **Core 侧三成员 + 事件多态**：`GetSessionStats()` 实例包装、
  `SetAutoRetryEnabled`/`AutoRetryEnabled`、`ExportToHtmlAsync`；
  `AgentSessionEvent` 22 变体 `[JsonDerivedType]`（`Type` 属性 `[JsonIgnore]`）。
- **一致性检查发现的两处平台事实**（均已用测试钉住）：① STJ 的判别式必须是
  JSON 对象的**第一个键**，协议不保证客户端键序，故 `TryDeserializeCommand`
  把 `type` 重排到最前；② 内置三种 `JavaScriptEncoder` 都与 `JSON.stringify`
  不等价（`UnsafeRelaxedJsonEscaping` 仍转义 U+2028/U+2029，正是 TS
  `rpc-jsonl.test.ts` 钉住「行内出现原始分隔符」的那条），自制编码器只转义
  `"` `\` 控制字符与孤立代理；孤立代理本身是平台差异（STJ 写 UTF-8 前替换为
  U+FFFD，编码器层管不到）。
- **TS 测试对拍**：`rpc-jsonl` / `rpc-client-clear-queue` / `rpc-client-clone` /
  `rpc-client-process-exit` / `rpc-prompt-response-semantics` / `print-mode`
  六个文件逐条移植为 52 例；monkey-patch 类测试改为 `internal virtual` 子类
  替换（`RpcClient` 为此去掉 sealed），`vi.mock` 类测试改为
  `RpcModeOptions` 注入（output/输入/退出/flush 四个接缝）。
- 测试 **+52 例**；全量 **3329/0**。
- **4f-5 至此全部收口。** 下一阶段：**4g**（cli/* + main.ts/index.ts 等 ~13,600 行），
  届时 4f-5 的 `RpcClientOptions.FileName`（现默认 `"node"`）切到 .NET CLI 入口。

### 0.14 4g-1a 收口记录（2026-10-18，`3d61bbc`）——CLI 辅助层第一批

- **新目录 `src/Pi.CodingAgent/Cli/`**：`Args.cs`（`args.ts` 479 行）、
  `InitialMessage.cs`（43）、`CliSetup.cs`（13）、`AuthCommand.cs`（126）。
- **新增差分对拍手法**：`args.ts` 是分支密集的纯决策层，除逐条移植 `args.test.ts`
  （129 例）外，把 TS 的 `parseArgs` + `getToolListError`/`isToolModifier`
  **逐行抄进独立 Node 脚本**（`tools/gen-args-corpus.mjs`，只去类型标注与
  chalk/config 导入），生成 597 例语料（`args.test.ts` 全量 argv + 该套件未覆盖
  的边界 + 400 例种子化 fuzz），`CliArgsCorpusTests` 逐例回放比对 → **597/597 全中**。
  生成脚本按约定留在 gitignore 的 `tools/`，语料 JSON 入库。
- **一致性检查抓到的 6 处 TS 真实行为**（全部用测试钉住，不做顺手修正）：
  ① 取值型 flag 尾缀无值时**下落到未知 flag 捕获**（`["--provider"]` → `unknownFlags{provider:true}`，
  且 `-t` 与 `--tools` 因此行为不同）；② `--print` 只吞「非 `@` 且（非 `-` 或 `---` 开头）」的下一参数；
  ③ `--mode`/`--tui-mode`/`--use-theme` 缺值**不消费**、非法值**消费**；
  ④ `--` 之后一切按位置参数（含 `-p`）；⑤ `--min-expiry` 正则带 `i` 但单位比较**大小写敏感**
  （`30M` 按小时算）；⑥ `printAuthCommandHelp` 写死 `pi` 而上表用 `APP_NAME`。
- **测试**：`CliArgsTests` 129 + `CliArgsCorpusTests` 597 + `CliInitialMessageTests` 5 +
  `CliAuthCommandTests` 43 = **+774 例**；全量 **4103/0**（基线 3329）。
- 差异编号推进到 **C287**（C280–C287：unknownFlags 装箱字典、诊断复用运行时记录、
  `listModels` 拆双字段、`Mode`→`CliMode`、`setupCli` 两处平台缺口、
  `--min-expiry` 的 `[0-9]`+`\z`、`getAuthCredential` 转发、帮助文本写死 `pi`）。
- **4g-1a 收口。** 4g-1 剩余：`startup-ui.ts`(253) / `list-models.ts`(115) /
  `credential-print.ts`(87) / `file-processor.ts`(88) / `auth-check.ts`(73) /
  `project-trust.ts`(62) / `config-selector.ts`(61) / `session-picker.ts`(55)
  + `cli/experimental/*`(504)。

### 0.15 4g-1b 收口记录（2026-10-18，`190814b`）——CLI 辅助层第二批

- **8 个源文件共 794 行**：`Cli/StartupUi.cs`（`startup-ui.ts` 253）、`Cli/ListModels.cs`（115）、
  `Cli/CredentialPrint.cs`（87）、`Cli/FileProcessor.cs`（88）、`Cli/AuthCheck.cs`（73）、
  `Cli/ProjectTrustUi.cs`（62）、`Cli/ConfigSelectorCli.cs`（61）、`Cli/SessionPicker.cs`（55）。
- **`Fuzzy.Filter` 早已存在**（P3 TUI 期），但只有 3 条松散断言；本批补
  `Pi.Tui.Tests/FuzzyTests.cs`（20 例，对齐 `packages/tui/test/fuzzy.test.ts` 全部 14 例），
  并把 TS 实现在 Node 里跑出的**具体分值**钉死（`("test","test")` = −159.4、
  `("codex52","gpt-5.2-codex")` = −65、换位分支 = 直配 + 5）。
- **一致性检查抓到的 6 处 TS 真实行为**（全部用测试钉住）：① `<file>` 模板在 content 自带
  尾换行时多一个空行；② `--list-models` 末列也 `padEnd`（行尾带空格）；
  ③ `formatTokenCount(999_999)` = `"1000.0K"`（不升位）；④ `auth check` 的未知 flag 文案用
  `getAuthCommandName`；⑤ `credential-print` 的「0 个可用」错误只看 `providers[0]` 的类型；
  ⑥ `project-trust` 的 `confirm` 取消按「否」。
- **接口收窄（C288）**：新增 `IProjectTrustUiContext`（四成员）并让
  `IExtensionUIContext : IProjectTrustUiContext`，对齐 TS 的 `Pick<…>`；契约测试
  `ExtensionContexts_SurfaceMatchesTsMethodList` 改为对该接口展开基接口成员（其余接口不展开——
  `IExtensionToolContext` 虽继承 `IExtensionContext`，TS 里却是两个独立接口）。
- **测试 +60 例**（`FuzzyTests` 20 / `CliListModelsTests` 25 / `CliFileProcessorTests` 9 /
  `CliAuthCheckTests` 7 / `CliProjectTrustUiTests` 9 中的新增部分）；全量 **4163/0**（1m31s），
  `Pi.Tui.Tests` **23874/0**（14s）。
- 差异编号推进到 **C293**。

### 0.16 4g-1c 收口记录（2026-10-18，`364926c`）——实验性子命令框架

- **`src/Pi.CodingAgent/Cli/Experimental/` 5 文件共 504 行**：`Command.cs`（`command.ts` 225）、
  `ExperimentalCommandOptions.cs`（106）、`ClientCommand.cs`（91）、`ServerCommand.cs`（66）、
  `ExperimentalCli.cs`（16）。
- **WHATWG URL 复刻**：`parseTransportAddress` 的两条判定（`url.href === value`、
  `value === "radius://"+hostname+pathname`）依赖 WHATWG 规范化，`System.Uri` 不等价。
  端口自建 scheme/authority/path 拆分 + `EncodePathLikeWhatwg` + `TryDecodeUriComponent`，
  **28 条地址语料与 TS 逐条一致**。
- **一致性检查抓到的 4 处 TS 真实行为**（全部用测试钉住）：① 未注册选项/`--` 都是
  「从当前位置起的全部参数」落进 `remainingArgs` 并停止解析（`--` 自身在内）；
  ② 开关型带 `=value` 报错但**不**消费下一参数，取值型取到空串报错但**已**消费；
  ③ `errors` 顺序 = auth → provider/model → session 互斥 → unsupportedOptions；
  ④ client 的 prompt 只认「恰好一个 token」，首项是 `--` 时允许 `-` 前缀。
- **测试 +62 例**（`CliExperimentalCommandTests`，对齐 `experimental-cli-command.test.ts`
  全部用例 + 28 条地址语料 + 4 条框架行为）；全量 **4225/0**（2m03s）。
- 差异编号推进到 **C300**（C294–C300：结果联合收敛、`Command` 三参数收成两参数、
  `.command()`→`RegisterCommand`、交叉类型→接口继承、`object?` 类型擦除、
  新增 `Pi.Protocol` 引用、`Cli.Experimental` 遮蔽 `Core.Experimental`）。
- **4g-1 至此全部收口。** 下一阶段 **4g-2**：`main.ts`(999) / `index.ts`(494) /
  `package-manager-cli.ts`(1,141) / `cli.ts`(6) / `rpc-entry.ts`(13)，届时
  `RpcClientOptions.FileName`（现默认 `"node"`）切到 .NET CLI 入口。

### 0.17 4g-2 收口记录（2026-10-18，`5c1d79a`）——CLI 入口层

- **移植范围 2,159 行 TS**：`Cli/Main.cs`（`main.ts` 999）、`Cli/PackageManagerCli.cs`
  （`package-manager-cli.ts` 1,141）、`Pi.Cli/Program.cs`（`cli.ts` 6）、
  `Pi.Cli/RpcEntry.cs`（`rpc-entry.ts` 13）。`index.ts`(494) 复核为**纯 re-export barrel**
  （全文只有 `export`/`export type`），延续 4f-3 既定结论：由命名空间 + public 类型承担，不单独移植。
- **新建 `src/Pi.Cli` 可执行工程**（`OutputType=Exe`、`AssemblyName=pi`、`RootNamespace=Pi.Cli`、
  `<Version>1.0.4</Version>` 对齐 TS `package.json`），已加入 `Pi.slnx`。`Program.Main` 走
  `CliSetup.SetupCli()` → `PI_RPC_ENTRY` 分流 → `Main.MainAsync`，`MainExitException` 在入口
  转成进程退出码。`RpcClientOptions.FileName` 默认由 `"node"` 改为 `Environment.ProcessPath`，
  `CliPath` 置 null（原 `dist/cli.js`），端到端实测 `pi.exe --mode rpc` 可直接跑通。
- **一致性检查抓到 6 处 TS 真实行为**（全部用测试钉住）：① `update` 无目标默认 `--self`，
  且**先**打印 "Extensions are skipped."；② `--extension` 取值以 `-` 开头算缺值（不消费）；
  ③ `uninstall` 是 `remove` 的别名，错误文案仍是 "Missing remove source."；
  ④ `update --models` 走单独分支、不建 settingsManager；⑤ `--all` 与
  `--self`/`--extensions`/`--models`/`--extension`/位置参数的 `conflictingOptions` 优先级链；
  ⑥ `auth check --json` 的 `reason`/`authType` 是**可省略键**（不是恒写 null）。
- **顺带修掉 5 处既有缺陷**（一致性检查发现，均非漂移）：① `BuiltInExtensions` 的 `mcp`
  仍是占位工厂（4d-4 早已落地），导致**每次启动都报 "lands with batch 4d-4"、RPC 模式直接起不来**
  ——旧用例 `PlaceholderFactory_FailsNamingItsBatch` 反而把这个 bug 固化成断言，改为
  `McpFactory_IsTheRealFactory`（加载工厂断言注册了 `/mcp`）；② `ModelsStore.Serialize` 直接挂载
  已有 parent 的 `JsonObject`（TS 的 `JSON.stringify` 无此限制）→ `update --models` 抛
  "The node already has a parent"，补 `DeepClone()`；③ `Config.ReadAssemblyVersion` 在无
  `package.json` 时返回 `0.0.0` → `--version` 打 `0.0.0`，改用
  `AssemblyInformationalVersionAttribute` 兜底（剥 `+<sha>`）；④ `ResourceLoader` 三处 4e 遗留缺口
  （`additionalExtensionPaths` / `systemPrompt` / `appendSystemPrompt`，含 `-e <path>` 不存在时的
  错误收集）；⑤ `Environment.ExitCode` 是进程级静态，in-process 复用会跨用例污染 → 改为
  `PackageCommandResult` / `AuthCommandRunResult` 显式返回码，删除 `DeferExit` 一类钩子。
- **端到端实测 8 项全通过**：`--version`→`1.0.4`；`--help`/`auth --help`/`list --help`→0；
  `install`→1 且文案正确；`update --self`/`--extensions`→1 报端口缺口；`update --models`→0
  "Model catalogs refreshed"；`--mode rpc` 的 `get_state` 返回完整 JSON；`--fork x --session y`→1；
  `--session-id "bad id!"`→1。
- **平台缺口（已在 C301–C307 登记）**：`process.title` / `process.emitWarning` 全局替换（C284）、
  `configureHttpDispatcher()`（C32）、`cleanupManagedInstall` / `cleanupWindowsSelfUpdateQuarantine`
  未移植；`update --self` 与 `update --extensions` 的 npm/git 版本比对仍归 4e-3c-3。
- **测试 +71 例**（`CliMainTests` 59 纯决策 + `CliMainEndToEndTests` 12 端到端，后者隔离
  `PI_CODING_AGENT_DIR` 与 `Cwd`/`AgentDir`）；全量 **4296/0**（1m52s）；构建 0 警告 0 错误。
- 差异编号推进到 **C307**。**4g-2 至此全部收口。** 下一阶段 **4g-3**：`experimental/*`
  （46 文件 9,950 行）+ `bun/*`(61) + `client/*`(1)。

### 0.18 4g-3a 收口记录（2026-10-18，`1b4be2c`）——实验层地基

- **4g-3 勘察结论（动工前）**：`experimental/` **没有任何外部引用**（`grep "experimental/"` 在
  `src/` 下除自身外零命中）——它是一组独立的实验入口，由 `experimental/cli.ts` 与各自的
  `main.ts` 驱动，不参与 `main.ts` 的主链。`package.json` 的 `exports` 只暴露
  `./experimental/plugin`（16 行，纯 re-export）。故 4g-3 按依赖层次切 7 个子批（见 4g 概览表）。
- **前置件核对**：`@earendil-works/{chord,client,protocol,server,durable,ai,tui}` 均已有对应
  C# 项目；`proper-lockfile` → `Core/NodeLock.cs`；`typebox` 的 `Value.Check` →
  `Core/JsonSchema.Check`。无新增外部依赖。
- **4g-3a 落地 193 行**：`Experimental/SessionCatalog.cs`（`session-catalog.ts` 87）与
  `Experimental/InternalProcess.cs`（`process.ts` 106）。`meta.json` 的 tab 缩进、键序
  （createdAt 在前）与尾换行按 `JSON.stringify(…, null, "\t")` 逐字节复刻。
- **一致性检查抓到的最大问题：14 处 CRLF 缺陷**。.NET 9+ 的 `JsonSerializerOptions` 新增
  `NewLine`，**默认值是 `Environment.NewLine`**；TS 的 `JSON.stringify` 恒写 `"\n"`。既有移植
  里凡 `WriteIndented = true` 的写盘在 Windows 上全部落成 CRLF：settings / auth / crashes /
  models / trust / mcp.json / mcp-auth / migrations / 工具调用 JSON 渲染 / 校验错误消息 /
  chord bundle 清单 / MCP structuredContent 文本。全部补 `NewLine = "\n"`（C311）。
  这是**平台默认值差异**，不是笔误——凡新增缩进序列化都必须显式设置。
- **`Pi.CodingAgent.Experimental` 命名空间遮蔽 `Core.Experimental` 类型（C300 复发）**：
  新增目录后 `Footer.cs` 的 `Experimental.AreExperimentalFeaturesEnabled()` 解析到命名空间而报
  CS0234。修法是全限定。**`using Experimental = Pi.CodingAgent.Core.Experimental;` 这样的同名别名
  **无效**——简单名称解析时外层命名空间成员优先于 using 别名（C312）；测试文件改用
  `CoreExperimental` 别名才生效。
- **测试 +58 例**（`ExperimentalFoundationTests`）：16 条会话 id 语料（含 128/129 边界、
  `"a\n"` 因 JS 的 `$` 不匹配尾换行而拒绝）、meta.json 精确文本、EEXIST/非法 id 文案、
  8 条 readSession 拒绝形状（含 `[]` —— JS 里 `typeof [] === "object"` 但两字段都是 undefined）、
  小数 createdAt 被接受、list 的三类跳过、storage 路径不创建文件、delete 幂等、
  角色读写/消费/未知值抛错、控制行 UTF-8 字节计数（`"中"` = 6 字节）。
- 全量 **4354/0**（1m53s）；构建 0 警告 0 错误。差异编号推进到 **C312**。
- **4g-3a 收口。** 下一批 **4g-3b**：`services/*`（14 文件 ~1,150 行，chord 服务契约与 provider）。

### 0.19 4g-3b-1 收口记录（2026-10-18，`cc5bcaa`）——七个 chord 服务契约

- **范围**：`experimental/services/*` 的**契约半边**（7 文件 205 行）——
  `agent-controller.ts`(57) / `presentation-ui.ts`(20) / `slash-commands.ts`(30) /
  `models.ts`(35) / `plugins.ts`(19) / `sessions.ts`(35) / `transcript.ts`(9)。
  provider 半边（`connection.ts` 399 / `server.ts` 151 / `worker.ts` 157 /
  `agent-controller-provider.ts` 95 / `models-provider.ts` 173 /
  `slash-commands-provider.ts` 225 / `transcript-provider.ts` 15 = 1,215 行）留下一批。
- **依赖核对**：`Pi.Chord` 的 `Api.DefineService<T>(id, local)` / `Service<T>`（`Pi.Chord.Facets`）/
  `IReplicatedState<T>` 与 TS 的 `defineService` / `ReplicatedState<T>` 一一对应；
  `ConversationView` 取 `Pi.Durable.Harness`，`ThinkingLevel` 取 `Pi.Ai.Types`（TS `ModelThinkingLevel`），
  `ServerId` 在 C# 侧是校验过的 `string`。`Pi.CodingAgent` 已引用 Chord/Durable/Protocol/Ai，无需新增。
- **四条 C# 表达差异**（都是类型系统差异，不是行为差异）：① TS 的
  `SlashCommandRunResult = AgentOperationResponse | AgentQueueResponse | undefined` 是两个联合的并集，
  C# 加共享抽象基类 `AgentSubmissionResponse`（带 `IsAccepted`/`Error`）+ `null` 表达（C313）；
  ② 接口的可选方法 `getArgumentCompletions?` → 可空委托属性（C314）；③ `refresh` 的匿名联合
  `{status:"idle"|"refreshing"|"done"} | {status:"warning"; errors}` → 四个 record 变体（C315）；
  ④ chord 的 `JsonValue` 在 C# 侧是 CLR 值树（`Dictionary`/`List`/标量），故 `object?`（C316）。
- **命名冲突**：`AgentOperationResponse.Accepted` 这个嵌套 record 与基类属性 `Accepted` 同名会
  CS0102，故基类属性改叫 `IsAccepted`（语义不变，调用方读 `IsAccepted`）。
- **测试 +25 例**（`ExperimentalServicesTests`）：九个服务 id 与 `local` 标志（`pi.local.*` 两个
  是 local）、id 唯一性、两个回复联合的 `IsAccepted`/`Error` 语义、共享基类可承载两者、
  `AgentPromptResult` 两变体、`AgentPromptImage.Type` 恒 `"image"`、
  `ModelSummary` 继承 `ModelRef` 但 record 相等性要求同类型（与 TS 结构比较一致，因为字段不同）、
  四态 `refresh`、`ModelsState` 允许未选模型、`SessionSummary` 继承地址、
  `SessionCreateOptions.Id` 缺省为 null、两个可选 `Description`、`PrepareSessionRequest.PackagePaths` 可为 null。
- 全量 **4379/0**（1m54s）；构建 0 警告 0 错误。差异编号推进到 **C316**。
- **4g-3b-1 收口。** 下一批 **4g-3b-2**：`services/*` 的 provider 半边（7 文件 1,215 行）。

### 0.20 4g-3b-2 收口记录（2026-10-18，`70c9e9f`）——provider 前半

- **范围**：provider 半边的前两个（110 行）——`transcript-provider.ts`(15) →
  `Services/TranscriptProvider.cs`，`agent-controller-provider.ts`(95) →
  `Services/AgentControllerProvider.cs`。余 5 个文件（`connection.ts` 399 / `server.ts` 151 /
  `worker.ts` 157 / `models-provider.ts` 173 / `slash-commands-provider.ts` 225 = 1,105 行）留下一批。
- **durable API 映射核对（agent-controller 的全部依赖）**：`conversation.submit` →
  `IConversation.SubmitAsync(SubmissionDraft.Input, Context)`；`conversation.compact` →
  `IConversation.CompactAsync(string? instructions, Context)`（返回 `TaskId<CompactionResult>`）；
  `conversation.abort` → `AbortAsync(Context, ConversationAbortOptions?)`；
  `harness.abortSubmission(id, context, conversationId)` → `IHarness.AbortSubmissionAsync`；
  `harness.submission(id, context)` → `IHarness.SubmissionAsync`；
  `submission.wait(context)` → `ISubmission.WaitAsync` → `SettledSubmissionRecord.Record`
  （终态在 `SubmissionRecord.InputRecord.Status`）；`harness.commit(tx => tx.entry(answer))` →
  `CommitAsync(tx => tx.GetEntryAsync(id))`。
- **waitForPrompt 的三分支**：`status === "unanswered"` → `Unanswered(reason)`；
  `type === "input" && answer` 存在 → commit 读回该条目，取 `Model[0]`，**只拼 assistant 消息的 text 块**；
  answer 缺省 → `Done("")`。三条都用测试钉住（上一批的 record 断言覆盖枚举映射）。
- **三处 C# 表达差异**：① TS 的 `defineFacet({id, setup})` 工厂 → C# 实现 `IFacet`
  （`Id` + `Setup`）（C317）；② TS 的 `submit` 接受 `string | Content[]`，C# 草稿恒为块表，
  纯消息规整成单个 `TextContent`（C318）；③ `parseSubmissionId` 除正则外再加
  `MAX_SAFE_INTEGER` 上界（TS 的 `Number.isSafeInteger` 会拒绝 2^53+1）。
- **TranscriptProvider 未单测的判定依据**：15 行纯装配，逻辑全在 chord（`IFacetEnvironment`）与
  durable（`ViewStateAsync`）侧且均已测试；要单测它需要一个 70+ 成员的 `IConversation` 假实现，
  收益不足。后续 client/server 集成测试会覆盖该 facet 的接线。
- 全量 **4400/0**（1m59s）；构建 0 警告 0 错误。差异编号推进到 **C319**。
- **4g-3b-2 收口。** 下一批 **4g-3b-3**：`connection.ts`(399) / `server.ts`(151) /
  `worker.ts`(157) / `models-provider.ts`(173) / `slash-commands-provider.ts`(225)。

### 0.21 4g-3b-3 勘察记录（2026-10-18，未动工）——provider 后半的依赖映射

- **范围**：5 个文件 1,105 行。逐文件核对 TS 依赖与 C# 对应件，**全部就位，无新增外部依赖**：

| TS 文件 | 行 | 依赖的 C# 对应件 | 状态 |
| --- | --- | --- | --- |
| `services/connection.ts` | 399 | `Api.CreateRemoteServiceBinding` + `RemoteServiceBinding`（`Use`/`Observe`/`ReadyAsync`/`RebindAsync`）、`Api.ReplicatedState<T>` → `MutableReplicatedState<T>`、`Context.Background`（`BACKGROUND_CONTEXT`）、`Pi.Client.ClientServiceTransport.Create(client, Func<RpcTarget?>)` | 就位 |
| `services/server.ts` | 151 | `RemoteServiceProvider` + `RemoteServiceEndpoint.Create`、`Pi.Server.IRoutedServerServiceAttachment` / `IRoutedServerServiceHost` | 就位 |
| `services/worker.ts` | 157 | 同批已落地的 `AgentControllerProvider` + 本批的 `models-provider` / `plugins` / `transcript-provider` | 就位 |
| `services/models-provider.ts` | 173 | `IModels` 契约 + `ModelRuntime` / `SettingsManager`（Pi.CodingAgent 内） | 就位 |
| `services/slash-commands-provider.ts` | 225 | `ISlashCommands` / `IAgentController` / `IModels` / `IPresentationPlugins` / `ISessionPlugins` 契约 | 就位 |

- **动工前必须解决的一处模型差异（已定位）**：C# 的 `IRemoteServices` 接口只有
  `ReadyAsync(CancellationToken)` + `UseRaw(string)`，**没有** TS `RemoteServices` 的
  `use<T>` / `observe<T>` / `serviceReady` / `updateBound` / `dispose`——这些成员在 C# 里存在于
  `RemoteServiceBinding` 具体类上，而非接口上。因此 TS 的 `RoutedServiceBinding`
  （「实现 `RemoteServices` 并转发」）在 C# 侧要改写成：**实现 `IRemoteServices`（`ReadyAsync` /
  `UseRaw` 转发），同时把 `Use<T>` / `Observe<T>` / `RebindAsync` / `DisposeAsync` 作为自己的
  公共成员暴露**，供 presentation 侧直接持有（`FacetKernel` 只走 `UseRaw`，不受影响）。
- **另两处已知差异**：① TS 的 `ready(context)` / `serviceReady(context)` / `updateBound(bound, context)`
  在 C# 侧是 `ReadyAsync(CancellationToken)` / `RebindAsync(bool, Context)`——上下文传递方式不同，
  调用方需把 `Context` 的取消令牌取出；② TS 的 `Promise.allSettled` + 单错原样抛/多错 `AggregateError`
  在 C# 侧用 `Task.WhenAll` + 手工收集异常（`AggregateException`）。
- **4g-3b-3 开工顺序建议**：`models-provider`（依赖最少，可先跑通 `IModels` 的复制态）→
  `slash-commands-provider` → `worker`（把三个 provider 组装成 facet）→ `server`（host 侧）→
  `connection`（presentation 侧，体量最大且依赖 client 传输）。

---

## 零、2026-10-16 修订：参照基线漂移（R2 已发生）

### 0.1 事实

动工前按约定执行 `git -C D:\AI\参考项目\pi log -1`，发现参照检出**已不在** `428a12bc7`：

| 项 | 值 |
| --- | --- |
| 台账记录的基线 | `428a12bc7`（2026-10-06 08:18 UTC） |
| 实际检出 HEAD | `eb326d26`（2026-10-06 21:51 UTC） |
| 漂移提交数 | **25** |
| 其中触及 `packages/coding-agent` | 8 |
| 其中触及 `modes/interactive` | 3 |

漂移不是排版/文档改动，而是一组**真实特性**：

| 提交 | 内容 |
| --- | --- |
| `18336987a` | `outputPad` 应用到**所有** transcript 块（此前只覆盖消息类组件） |
| `36a686ee8` | 记录响应 / 工具执行 / durable 任务耗时（`durationMs` 贯穿 ai → agent → coding-agent） |
| `f284a2460` | 响应计时用私有字段 + 真实流（`AssistantMessageEventStream` 自己计时） |
| `ddaa0a034` | `--tools` 接受 `+name` / `-name` 修饰符（并新增 `getToolListError` 校验） |
| `eb326d265` | codemode 输出条目分离（**横跨 `packages/codemode` + `packages/coding-agent` 两个包**——只在目标包 grep 会漏掉对侧，S4 的教训） |
| `83c9e2645` | transcript 重建时清空全屏选区（`TuiAltScreen.resetTextSelection`） |
| 其余 5 项 | `bedrock` gpt-oss/gpt 思考档位映射、`radius` 网关目录替换内置基线、`faux` 逐消息 prompt cache、`retry` 增加 `server_busy`、`ai` 事件流可赋值性 |

### 0.2 关键发现：端口本就**部分领先于**台账基线

逐文件核对后发现，端口**并非**统一停在 `428a12bc7`：若干模块早已包含 post-baseline 特性
（`AssistantMessage.DurationMs`、`ToolResultMessage.DurationMs`、`ExtensionEvent.ToolExecutionEnd.DurationMs`、
`NestedToolExecutionEvent.End.DurationMs`、`NestedToolCalls` 计时、`Estimate.CharsPerToken = 3.5`）。

反过来，也有若干处是**按旧基线主动删掉、而上游后来又加回来**的：

| C# 站点 | 旧处置 | 依据 | 现处置 |
| --- | --- | --- | --- |
| `ToolRenderContext.DurationMs` | 4f-1 判为「臆造」删除 | 旧基线 `types.ts` 无此字段 | **加回**（上游 `36a686ee8` 真加了） |
| `BashRenderers` 的 `durationMs` 优先分支 | 同上删除 | 同上 | **加回** |
| `SettingsManager.GetToolListError` | 4f-3 前判为「TS 没有」删除 | 旧基线无校验 | **加回**（上游 `ddaa0a034` 真加了） |

> **结论（约定修订）**：以**当前检出**为唯一参照真值。旧基线下「TS 没有 → 删除」的判定不再自动成立，
> 每批动工前必须重新核对当前检出。这条取代原来的「确认仍在 `428a12bc7`」。

> **补充（S3 收口时发现）**：除上述「删了又加回来」的三处外，还有一类是**首移植时就漏抄**的——
> `Pi.Ai/Utils/Retry.cs` 的 `RETRYABLE_PROVIDER_ERROR_PATTERN` 比 TS 少一条
> `"pending stream has been canceled"`（上游 `5b6c792b4`，用 `git merge-base --is-ancestor`
> 确认**早于**漂移基线）。核对手段是**逐条比对整张模式表**（TS 49 / C# 48）。
> 结论：对账不能只盯 `git log` 的窗口，对「表驱动」的常量（正则表、档位映射表、目录表）
> 必须逐项 diff 一遍。

### 0.5 R2 追记（2026-10-16 晚，Batch F-1 动工前核对）

参照检出**已退回** `428a12bc7`（`git reflog` 显示 2026-10-12 起 HEAD 即在该提交，此后无移动；
`eb326d26` 及漂移窗口的 6 组提交在本地**全部不可解析**，也无其他 worktree/克隆承载该状态）。
逐项实测两基线的特征差异：

| 特征 | 当前检出（428a12bc7） | 结论 |
| --- | --- | --- |
| `getToolListError`（settings-manager.ts） | 无 | S2a 领先参照 |
| `agent-loop.ts` `durationMs` 计时 | 无 | S1 领先参照 |
| `branch-summary-message.ts` `outputPad` | 无 | S0 领先参照 |
| codemode prelude `output("console", …)` | 无 | S4 领先参照 |
| `retry.ts` `server_busy` | 无（`pending stream has been canceled` 在） | S3a 领先参照 |
| `faux.ts` `tokensPerSecond`（13 处，全文 710 行） | **有** | S3c 与当前检出一致 |

**处置**：S0–S4 的移植成果保留（真实上游演化 + 测试齐全，回退即功能回归）；后续批次以**当前检出
`428a12bc7` 为参照真值**。已登记的对账风险：待 4f-4 移植 `interactive-mode.ts` 时，当前检出的
该文件**不含** outputPad 全量接线 / durationMs / `ctx.reload()` 编排，需对照 S0/S1/S2b 已落地的
C# 组件语义手工补齐，不得直接照抄旧版 TS。

### 0.3 R2 对账结果（逐包）

| 包 | 漂移面 | C# 现状 | 处置 |
| --- | --- | --- | --- |
| `packages/tui` | `box.ts` / `text.ts` 加 `setPaddingX`；`tui-alt-screen.ts` 加 `resetTextSelection` | **已全部具备**（`Box.SetPaddingX` / `Text.SetPaddingX` / `TuiAltScreen.ResetTextSelection`） | 无需改动 ✅ |
| `packages/agent` | `agent-loop.ts` 在 `execute()` 周围计时；`ToolExecutionEnd` 事件带 `durationMs` | `AgentToolCallOutcome.DurationMs` 已有；**事件与计时缺失** | S1 补齐 |
| `packages/ai` | 事件流自计时、`lazy` 用流创建时刻、`event-stream` 起始时刻改 `WeakMap`、`bedrock` gpt-oss/gpt 映射、`radius` 目录替换、`faux` 逐消息 cache、`retry` `server_busy` | `Estimate` 已对齐；`WeakMap` 那条因端口用**组合**而非继承而**不适用**（见 C209） | S1（事件流/`lazy`）+ S3（provider 层） |
| `packages/coding-agent` | `outputPad` 全量 / `durationMs` 贯穿 / `--tools` 修饰符 / codemode 条目分离 / 选区重置 | 见下表 | S0 / S1 / S2 / S4 |

### 0.4 修订后的阶段计划

| 阶段 | 范围 | 状态 |
| --- | --- | --- |
| **S0** | `outputPad` 回填到 4f-3c 已收口的 3 个折叠消息组件 + `CustomMessageComponent` 默认渲染 | ✅ `42ed85c` |
| **S1** | `durationMs` 贯穿：`agent-loop` 计时 → `ToolExecutionEnd` 事件 → `ToolResultMessage`；`AssistantMessageEventStream` 自计时；`lazy` 用流创建时刻；coding-agent 侧 `ToolRenderContext.DurationMs` / `ToolExecutionEndEvent.DurationMs` / `BashRenderers` 优先分支 / `ToolHtmlRenderer` | ✅ `4ebf6fa` |
| **S2** | `--tools` 修饰符：`GetToolListError` 回填 + `Sdk` tools 语义 + `AgentSessionConfig.DefaultToolModifiers` + 决策叶子修饰符 + `AgentSession.ReloadAsync` 编排本体 | ✅ `3b1346a`（S2a）；✅ `e657dac`（S2b） |
| **S3** | ai provider 层：`retry` `server_busy`（`8b5708dbb`）、`radius` 网关目录替换（`43d376399`）、`bedrock` OpenAI reasoning（`2989eb581`）、`faux` 逐消息 prompt cache + 用量估算子系统（`fe11328b0`） | ✅ `2c74e2c`（S3a）；✅ `a633cb2` + `97eae94`（S3b） |
| **S3c** | faux 补全（**新增**）：`tokensPerSecond` 限速 / `tokenSize` 随机切块 / `deferred` 全链 / `FauxResponseFactory` / `fauxProvider()` / `aborted` 中断 / 四个 `*_start`·`*_end` 事件 / 自定义模型目录 | ✅ 本批（见 [coding-agent-porting-status.md](coding-agent-porting-status.md) 的「S3c」节） |
| **S4** | codemode 输出条目分离（`eb326d265`）：`==> text N/M <==` 编号 + `<console_output>` 分组 + console 标记 | ✅ `3cd141d` |
| **S5** | 回到原计划：4f-3d 选择器 → 4f-3e → 4f-4 → 4f-5 → 4g → evals | 🟡 **进行中**：4f-3d Batch C ✅（5 个：`theme`/`show-images`/`trust`/`thinking`/`user-message`）+ Batch D ✅（4 个：`extension-selector`/`extension-input`/`radius-login-selector`/`first-time-setup`）+ Batch E ✅（6 个：`session-selector-search`/`login-dialog`/`settings-submenu`/`oauth-selector`/`custom-editor`/`scoped-models-selector`）+ Batch F-1 ✅（`external-editor`/`extension-editor`/`model-selector`）+ Batch F-2 ✅（`settings-selector`）+ Batch F-3 ✅（`config-selector`）+ Batch F-4 ✅（`session-selector`）+ **Batch F-5 ✅**（`tree-selector`，选择器组 **21/21 全部收口**）。**另收口 C233**：`Container`/`Text`/`Box` 三个基类补 `virtual HandleInput`、9 个派生组件改 `override`（此前经 `IComponent` 引用调用会落到空默认实现，影响 Batch C/D 全部组件）；**4f-3e Batch 1 ✅**（`bordered-loader`/`custom-entry`/`earendil-announcement`；`mermaid` 因 grok-mermaid/marked 依赖不可得而延后）+ **Batch 2 ✅**（`footer`/`bash-execution`/`tool-execution`）+ **Batch 3 ✅**（`armin`/`easter-egg-3d`）+ **Batch 4 ✅**（`mermaid` transformer 骨架 + `IMermaidRenderer` 注入缝）；**4f-3e 组件组 11/11 全部收口**。**4f-4 🟡 进行中**（7,082 行 / 311 成员，切 13 个子批次）：**4f-4a ✅**（展示层纯函数 → `InteractiveModeFormatting.cs` + 59 例）；**4f-4c ✅**（模块级层 → `InteractiveModeModule.cs` + 23 例）；**4f-4b ✅**（`tui-renderer.ts` → `TuiRenderer.cs` + 7 例）；**4f-2b ✅**（`theme-controller.ts` → `ThemeController.cs` + 27 例，4f-2 全部收口）；**4f-4d ✅**（诊断与文案叶子层 → `InteractiveModeDiagnostics.cs` + 39 例，C265；类骨架改 13 子批、顺延 4f-4m）；**4f-4e ✅**（自动补全辅助层 → `InteractiveModeAutocomplete.cs` + 24 例）；**4f-4f ✅**（启动横幅与资源展示叶子层 → `InteractiveModeStartup.cs` + 20 例）；**4f-4g ✅**（状态指示器决策层 → `InteractiveModeStatus.cs` + 16 例，C266）；**4f-4h ✅**（扩展绑定辅助层 → `InteractiveModeExtensions.cs` + 14 例）；**4f-4i ✅**（编辑器提交路由层 → `InteractiveModeSubmit.cs` + 55 例）；**4f-4j ✅**（消息与渲染决策层 → `InteractiveModeRender.cs` + 31 例）；**4f-4k ✅**（斜杠命令辅助层 → `InteractiveModeCommands.cs` + 20 例）；**4f-4l ✅**（生命周期叶子层 → `InteractiveModeLifecycle.cs` + 16 例）；**4f-4m-1 ✅**（类装配首批：骨架与扩展 UI 面 → `InteractiveMode.cs` + 32 例）；**4f-4m-2 ✅**（事件分发与会话渲染 → `InteractiveMode.Events.cs` + 7 例）；**4f-4m-3 ✅**（生命周期批 → `InteractiveMode.Lifecycle.cs` + 7 例；退役 InitAsync / CheckShutdownRequested / HandleRightClickPaste 三缝；新登记 8 个缝/缺口 → 4f-4m-4/5） |

---

## 一、现状快照（2026-10-16 复测）

### 1.1 规模对照

| 目录 | TS 文件 | TS 行数 | .NET 落点 | 状态 |
| --- | ---: | ---: | --- | --- |
| `core/` | 94 | 35,059 | `Core/*`（93 个 .cs） | 🟡 主体完成，编排本体与 renderers 未完 |
| `modes/` | 67 | 23,705 | — | ⬜ 未开始 |
| `experimental/` | 46 | 9,950 | — | ⬜ 未开始 |
| `extensions/` | 25 | 7,259 | `Extensions/*` | 🟡 codemode / mcp / tool-search / llama 主体完成，llama 流式层未完 |
| `utils/` | 37 | 3,737 | `Utils/*` | ✅ 完成 |
| `cli/` | 17 | 1,949 | — | ⬜ 未开始 |
| `bun/` + `client/` | 6 | 62 | — | ⬜ 未开始（多为打包器垫片，多数无对应物） |
| 根级（`config.ts` / `migrations.ts` / `main.ts` / `index.ts` / `cli.ts` / `rpc-entry.ts` / `package-manager-cli.ts`） | 7 | 3,624 | `Config.cs` / `Migrations.cs` | 🟡 仅 `config.ts` / `migrations.ts` 完成 |
| **合计** | **299** | **85,536** | 77,321 行 C# | **约 90% 行数已落** |

`src/Pi.CodingAgent` 现有 **197 个 `.cs`**（`find . -name '*.cs'` 排除 `obj/`），`wc -l` 合计 **78,296 行**（2026-10-14 4f-3b 后实测；4f-2a 新增 `Modes/Interactive/Theme/` 6 个文件 2,745 行，4f-3a 新增 `Modes/Interactive/Components/` 4 个文件 268 行，4f-3b 新增 `Components/DiffRenderer.cs` 265 行并把 `Core/Tools/JsDiff.cs` 从 653 行扩到 1,095 行）。

### 1.2 构建与测试基线（本机实测）

| 项目 | 结果 |
| --- | --- |
| `dotnet build Pi.slnx -m:1` | ✅ 0 警告 0 错误（**修复后**，见 1.3） |
| `Pi.CodingAgent.Tests` | ✅ 1313 项 / 1313 通过 / 0 失败（2m21s，**起始基线**） |
| `Pi.CodingAgent.Tests`（P2a 后） | ✅ **1338 项 / 0 失败**（2m54s；1313 + P1 的 10 + P2a 的 15） |
| `Pi.CodingAgent.Tests`（P2b 后） | ✅ **1364 项 / 0 失败**（2m21s；1338 + CacheWarmerTests 重写净增 4 + `AgentSessionRuntimeTests` 22） |
| `Pi.CodingAgent.Tests`（4e-3c-2a 后） | 🟡 **1364 + 114 = 1478 项**（**推算**：该批只跑了新增 4 个单类，未跑全量；`SemverRangeTests` 51 / `PackageManagerPatternsTests` 35 / `GlobSyncTests` 12 / `PackageManagerCollectorsTests` 16 全绿，`PackageManagerTests` 14/14、`ResourceLoaderTests` 19/19 无破） |
| `Pi.CodingAgent.Tests`（4e-3c-2b 后） | ✅ **1488 项 / 0 失败**（4m14s；1478 + `PackageManagerTests` 由 14 扩到 24，净增 10） |
| `Pi.Agent.Tests`（P2b 后） | ✅ **32 项 / 0 失败**（284ms） |
| `Pi.Ai.Tests`（P2b 后） | ✅ **301 项 / 0 失败**（8s） |
| `Pi.CodingAgent.Tests`（P4b-3 后） | ✅ **1607 项 / 0 失败**（2m39s；1570 + `McpResourceToolsTests` 37） |
| `Pi.CodingAgent.Tests`（P4b-4 后） | ✅ **1647 项 / 0 失败**（3m08s；1607 + `McpRuntimeTests` 40） |
| `Pi.CodingAgent.Tests`（P4b-5 后） | ✅ **1662 项 / 0 失败**（3m27s；1647 + `McpCliTests` 15） |
| `Pi.CodingAgent.Tests`（P4b-6 后） | ✅ **1708 项 / 0 失败**（3m01s；1662 + `McpExtensionTests` 46） |
| `Pi.CodingAgent.Tests`（4f-2a 后） | ✅ **1889 项 / 0 失败**（3m50s；1708 + 181） |
| `Pi.Tui.Tests`（4f-2a 后） | ✅ **23854 项 / 0 失败**（9s；23812 + `TerminalColorsTests` 42；另修掉 `LoaderTests` 的一处时序竞态） |
| `Pi.CodingAgent.Tests`（S3c 后） | ✅ **2285 项 / 0 失败**（S0–S4 期间由 2256 逐步到 2285） |
| `Pi.CodingAgent.Tests`（4f-3d Batch C 后） | ✅ **2327 项 / 0 失败**（38s；2285 + `SelectorComponentsTests` 42） |
| `Pi.CodingAgent.Tests`（4f-3d Batch D 后） | ✅ **2366 项 / 0 失败**（38s；2327 + `ExtensionComponentsTests` 39） |
| `Pi.CodingAgent.Tests`（4f-3d Batch E-1 后） | ✅ **2393 项 / 0 失败**（37s；2366 + `SessionSelectorSearchTests` 27） |
| `Pi.CodingAgent.Tests`（4f-3d Batch E-2 后） | ✅ **2410 项 / 0 失败**（44s；2393 + `LoginDialogTests` 17） |
| `Pi.CodingAgent.Tests`（4f-3d Batch E-3 后） | ✅ **2436 项 / 0 失败**（42s；2410 + `SettingsSubmenuTests` 26） |
| `Pi.CodingAgent.Tests`（4f-3d Batch E-4 后） | ✅ **2480 项 / 0 失败**（42s；2436 + `OAuthSelectorTests` 44） |
| `Pi.CodingAgent.Tests`（4f-3d Batch E-5 后） | ✅ **2502 项 / 0 失败**（40s；2480 + `CustomEditorTests` 22；同批动共享基类 `Editor`，`Pi.Tui.Tests` 连跑两轮 **23854 / 0**） |
| `Pi.CodingAgent.Tests`（4f-3d Batch E-6 后，= 当前 HEAD） | ✅ **2526 项 / 0 失败**（42s；2502 + `ScopedModelsSelectorTests` 24） |
| `Pi.Tui.Tests`（4f-3d Batch E-3 后） | ✅ **23854 项 / 0 失败**（9s；连跑三轮全绿；C233 动了 `Container`/`Text`/`Box` 三个共享基类，另修 `UnicodeWidth` 缓存的跨线程竞态，见 C235） |
| `Pi.Ai.Tests`（S3c 后） | ✅ **359 项 / 0 失败**（S3a 后 310 → S3b 326 → S3c 359） |
| `Pi.Agent.Tests`（S3c 后） | ✅ **33 项 / 0 失败**（S1 后 33） |
| 其余 9 个测试工程 | 见 [porting-status.md](porting-status.md) 的环境限制说明 |

### 1.3 起始状态的一个真实缺陷（本批已修）

`f208567`（上一批「AgentSessionQueue」）**提交了不能编译的代码**：
`src/Pi.CodingAgent/Core/AgentSessionQueue.cs:209` 引用 `Frontmatter.Strip(...)`，但该文件缺少
`using Pi.CodingAgent.Utils;` → `CS0103: 当前上下文中不存在名称"Frontmatter"`。
本批已补上该 `using`，构建恢复 0 警告 0 错误。

> **教训（写入约定）**：任何一批的收口标准「构建 0 警告 0 错误」必须在**提交前**实测，
> 不能沿用上一批的结论。`f208567` 的提交信息未按仓库惯例（`type(scope): 中文标题` + 分节要点），
> 也未更新本文档，属流程偏离。

---

## 二、迁移清单

### 2.1 已完成（不需返工）

| 子阶段 | 范围 | 收口证据 |
| --- | --- | --- |
| 4a | `src/utils/*`（36 文件）+ 根级 `config.ts` | 36/36 文件 + 409 条差分向量 |
| 4b | 配置 / 信任 / 模型层 16 文件（6,844 行） | 16/16 文件 |
| 4c | 工具系统 8 个工具 + 6 支撑文件 | 8/8 工具；renderers 顺延 4f |
| 4d-1～4d-3 | 扩展契约层 / 事件总线 / 加载器 / ALC 加载器与内置扩展注册 | — |
| 4d-4a/4b | MCP 配置与服务器日志 / MCP 工具适配 | — |
| 4d-5 | codemode 扩展 | — |
| 4d-6 | tool-search 扩展 | — |
| 4d-7a | llama 非 UI HTTP 层 | — |
| 4e-1～4e-3e-8 | core 叶子模块 / Usage / AgentMessage / 消息内容联合 / session-manager / compaction / skills / system-prompt / session-export | — |
| 4e-3e-9a～9e | 会话统计、工具装载、消息溯源、prompt 循环、compaction/retry/bash、扩展绑定、树导航、fork 投影、bug-report 摘要（**纯决策叶子**） | `Pi.CodingAgent.Tests` 逐批 +20～40 |
| 4e-3e-9-0 | 基线对齐（参照检出落后 29 提交 → 按 `origin/main` 回审 allow/deny 与 MCP 注册） | C107/C108 |
| 4e-3e-9f | 会话装配层叶子（services / runtime / sdk） | C109–C112 |
| 4e-3e-9g | `ExtensionRunner`（1,560 行 TS）+ 契约层收口 | C140/C141；61 项新测试 |
| 4e-3e-9h-1 | 实例化地基（`static partial class` → `partial class`） | 行为零变化 |
| 4e-3e-9h-2a/2b-1/2b-2/2c/3 | `ThinkingLevel` 决策 + `IToolContext` 退役 + `wrapper.ts` / 资源溯源 + 扩展加载本体 / 工具目录 + 运行时装配 / 6 个 installer + `_handleAgentEvent` | C142–C157 |
| 4e-3e-9h-4a | `AgentSession` 编排本体（`prompt` / `compact` / `_runAgentPrompt` / `_handlePostAgentRun` / `_runBeforeSettleBoundary` / `_runAutoCompaction` / `_checkCompaction` / `_runDefaultCompaction` / 认证与重试叶子）；退役 4 处 `NotSupportedException` 缝 | C158–C160；`7b942b3` |
| 4e-3e-9h-4b（第一半） | `createAgentSession` 装配本体（`Sdk.CreateAgentSessionAsync`，TS `sdk.ts:181-468`）；顺带修正三处 reasoning 传递链缺口 | C161；`SdkCreateAgentSessionTests` 15 项 |
| 4e-3e-9h-4b-2 | `CacheWarmer` 完整化（定时重放 / 经济学判定 / `decide` + `extensionOverride` / 三个回调）+ `AgentSessionRuntime` 编排本体（`switchSession` / `newSession` / `fork` / `importFromJsonl` / `dispose` / `createAgentSessionRuntime`）+ `createAgentSessionServices` / `createAgentSessionFromServices`；顺带修正 `GetPromptCacheTtlMs` 的 `"short"` 回落缺陷与 `FormatCacheWarmingUsage` 的尾随零语义 | C162/C163；`CacheWarmerTests` 11 项 + `AgentSessionRuntimeTests` 22 项 |
| 4e-3c-2a | package-manager 叶子层：`PackageManagerPatterns`（patterns 纯函数）/ `PackageManagerCollectors`（目录采集器）/ `GlobSync`（node:fs `globSync` 子集）/ `SemverRange`（`validRange` / `satisfies`）；`Skills` 侧 ignore 助手（`IgnoreFileNames` / `ToPosixPath` / `PrefixIgnorePattern` / `AddIgnoreRules`）提为 `internal` 共享 | C164–C167；`82b47aa`；114 项新测试 |
| 4e-3c-2b | `DefaultPackageManager` 全链路：`ResolveAsync` / `ResolvePackageSourcesAsync` / `DedupePackages` / `FindAutoloadDeltaBase` / `PackageRoot` / 完整 `+`/`-`/`!` override 语义 / `ResourceMap`+`Accumulator`+`ToResolvedPaths`（稳定排序）/ `ListConfiguredPackages` / `GetInstalledPath` / `ResolveExtensionSourcesAsync`；**删除 SettingsManager 里臆造的 `include`/`exclude`/`autoload` 三个 settings 键通路（8 个成员）** | C168；`PackageManagerTests` 24 项（含 `ResourceLoaderTests` 夹具修正） |
| 4f-2a | 主题层本体：`modes/interactive/theme/theme-json.ts`（148）→ `ThemeJson.cs`（332，无 typebox 的手写校验）＋ `system-theme.ts`（655）→ `SystemTheme.cs`（803，14 家族 / 15 档对比曲线 / 三档降级）＋ `theme.ts`（1,160）→ `Theme.cs`（269）+ `ThemeRuntime.cs`（855）+ `ThemeTokens.cs`（221）+ `ThemeHelpers.cs`（265）；另补 `Pi.Tui` 的 `TerminalColors.cs` 与 OSC 10/11/4 + DA1 查询链（此前 OSC 11 通道是死的），`Assets/theme/*` 三个 JSON 落盘，删掉 `Placeholders.cs` 的 `Theme` 空占位类与 `ExtensionContexts` 里重复的 `ThemeInfo` / `SetThemeResult` | 181 项新测试；C194–C196；见 2026-10-14 台账 |
| P4b-6（4d-13） | MCP 扩展本体：`extensions/mcp/index.ts`（1,225 行）→ `Extensions/Mcp/McpExtension.cs`（1,960 行）+ `Extensions/Mcp/Ui.cs`（71 行）：扩展闭包（`session_start` 加载配置并后台连接 / 注册服务器与 `mcp.json` 的优先级 / `mcp__<server>__<tool>` 工具注册与 `plain` 去重 hash 后缀 / `codemode`·`deferred`·`direct`·`hidden` 四种 exposure 与 `toolExposure` 覆盖 / 资源工具同步 / 启动问题一次性汇报）+ `mcp_servers` 段渲染（4096 字符预算：先裁描述、再截尾计数）+ `/mcp` 命令（状态文本 / `login`·`logout`·`reconnect` 子命令 / 参数补全）+ 6 个事件（`session_start`、`before_agent_start`、`tool_call`、`turn_start`、`mcp_servers_change`、`session_shutdown`）；`SystemPrompt` 的 `Sections` 改为可写 `OrderedDictionary` | `McpExtensionTests` 46 项；C189–C193；见 2026-10-14 台账 |
| P4b-5（4d-12） | `pi mcp` 命令：`extensions/mcp/cli.ts`（614 行）→ `Extensions/Mcp/Cli.cs`（1,014 行）：`RunMcpCommandAsync` 的 help / add / remove / list / login / logout 七路（退出码逐条对齐 TS，`list` 在任一启用服务器未连接或存在配置错误时 exit 1）+ 参数解析（`-l`→`--local`、`--` 与 `maxPositionals` 都结束选项解析、命令自身选项透传）+ HTTP/stdio 两路 config 构造（`--bearer-token-env-var` → `Authorization: Bearer ${NAME}`、`oauth` 子对象）+ 文本/JSON 两种列表输出 + 登录（先连接判定 → challenge → `SignInServerAsync` → 清 challenge → 重连）+ 粘贴 redirect URL 的终端读取；新增 `McpServerConnection.ClearChallenge()` | `McpCliTests` 15 项；C187/C188；见 2026-10-14 台账 |
| P4b-4（4d-11） | MCP 连接层：`extensions/mcp/runtime.ts`（491 行）→ `Extensions/Mcp/Runtime.cs`（815 行）：`McpServerConnection`（懒连接 + 记忆化打开 / 六态状态机 / 能力门控的 tools+resources 发现 / readOnly 瞬时错误 250ms 同客户端重试 / 会话过期新会话重试一次 / needs-auth 与登录文案 / stderr 尾拼进连接失败与掉线消息 / `close` 等 `authProvider.settled()` 落盘）+ `CreateDefaultTransport` + `FetchResourcesAsync` + `WithoutTemplatesAsync`；前置补 `Pi.Mcp` 的 `StdioTransport.Stderr`（64KB 滚动缓冲，此前子进程报错全丢）；**自纠一处移植陷阱**：`_opening ??= OpenOnceAsync()` 在同步完成的打开上残留已完成任务，导致掉线/登出/会话过期后的重连都拿到死客户端 | `McpRuntimeTests` 40 项；C184–C186；见 2026-10-14 台账 |
| P4b-3（4d-10） | MCP 资源工具：`extensions/mcp/resources.ts`（339 行）→ `Extensions/Mcp/Resources.cs`（600 行）：`list_mcp_resources` / `list_mcp_resource_templates` / `read_mcp_resource` 三个工具 + `isMcpAppResource` + 单服务器分页与全服务器聚合（排序 / `_meta` 与 icons 剥离 / 逐服务器 errors 条目）+ 多内容 `uri:` 前缀；顺带补 `Pi.Mcp` 的 `McpResourceContents.MimeType`（此前 image blob 无法还原成图片块） | `McpResourceToolsTests` 37 项；C182/C183；见 2026-10-14 台账 |
| P4b-2（4d-9） | MCP OAuth 凭据层：`extensions/mcp/oauth.ts`（532 行）→ `Extensions/Mcp/OAuth.cs`（816 行）：`McpOAuthCredentialStore`（legacy 接管 / refresh 锁）/ `createMcpAuthProvider`（30s 提前刷新、401 刷新、insufficient_scope、进程内共享刷新、跨进程锁、令牌被替换短路）/ `signInMcpServer`（PKCE + CIMD + 粘贴 redirect 竞速）；顺带修正 `Pi.Mcp` 两处真实缺陷（`clientMetadataDocument` 固定值 → 元数据钩子；`OAuthTypes` JSON 命名缺 snake_case） | `McpOAuthTests` 37 项；C179–C181；见 2026-10-14 台账 |
| 4d-7c | llama `provider.ts`（299 行）+ `index.ts`（230 行）+ `ui.ts` 的非 UI 半部（`LlamaUi` / `runWithProgress` / 三个纯函数）；`BuiltInExtensions` 的 `llama.cpp` 占位工厂 → 真实工厂；新增 `Utils/JsNumber.cs`（JS `Number()` / `isSafeInteger` / `toFixed`） | C169–C173；`LlamaExtensionTests` 44 项；变异套件 `llama-extension` 24 处全捕获 |
| （未记档） | `AgentSessionQueue`（输入与排队层） | 本批修复其编译错误 |

### 2.2 未完成（本计划的执行对象）

> **2026-10-13 更新**：`4e-3e-9h-4` 已全部收口（见下），**package-manager 包解析（4e-3c-2）已收口**
> （见 2.1），余下为 4d 尾巴 / 4f / 4g / evals。

#### 4e-3e-9h-4：会话编排本体 ✅ 已完成

> **进度**：`4a`（`AgentSession` 编排本体）✅ `7b942b3`；`4b` 第一半（`createAgentSession` 装配本体）
> ✅ `bb874f6`；`4b-2`（`CacheWarmer` 完整化 + `AgentSessionRuntime` + 服务装配）✅ 本批。
> **本小节全部收口。**

| TS 符号 | 行数 | 端口现状 |
| --- | ---: | --- |
| `agent-session.ts` `prompt()` | 1954-2096（143 行） | ✅ `PromptAsync`（9h-4a） |
| `agent-session.ts` `_runAgentPrompt()` | 1808-1837（30 行） | ✅ `RunAgentPromptAsync`（9h-4a） |
| `agent-session.ts` `_handlePostAgentRun()` | 1839-1877（39 行） | ✅（9h-4a） |
| `agent-session.ts` `_runBeforeSettleBoundary()` | 1879-1910（32 行） | ✅（9h-4a） |
| `agent-session.ts` `compact()` | 2750-2895（146 行） | ✅ `CompactAsync`（9h-4a） |
| `agent-session.ts` `_runAutoCompaction()` | 2985-3120（135 行） | ✅（9h-4a） |
| `agent-session.ts` `_runDefaultCompaction()` / `_getSummarizationRequestAuth()` | 2720-2750 | ✅（9h-4a） |
| `agent-session.ts` `_handleAgentEvent` 尾部 / `_flushPendingBashMessages` 等生命周期尾巴 | — | ✅（9h-3） |
| `sdk.ts` `createAgentSession()` | 462 行（整文件 468） | ✅（`Sdk.CreateAgentSessionAsync`） |
| `sdk.ts` `buildRequestOptions` 余下字段（`transformHeaders` / `onPayload` / `onResponse` / `onProviderStreamEvent`） | — | ✅ |
| `agent-session-services.ts` `createAgentSessionServices` 编排本体 | 233 行 | ✅ **本批**（`AgentSessionServicesBuilder.CreateAgentSessionServicesAsync`） |
| `agent-session-runtime.ts` `AgentSessionRuntime` 类本体 | 447 行 | ✅ **本批**（`SwitchSessionAsync` / `NewSessionAsync` / `ForkAsync` / `ImportFromJsonlAsync` / `DisposeAsync` / `CreateAgentSessionRuntimeAsync`） |
| `CacheWarmer` 完整化（`decide` / 定时重放 / 经济学判定 / 回调） | 454 行 | ✅ **本批** |
| `CreateAgentSessionRuntimeResult` | — | ✅ 类型 + 消费方均就位 |

#### 4d 尾巴：扩展层剩余（P4b-6 已收口）

| TS 符号 | 行数 | 现状 |
| --- | ---: | --- |
| `extensions/llama/*` | 1,577 | ✅ provider / client / huggingface / index 全部完成；`ui.ts` 的 `LlamaView` / `HuggingFaceSearch` / `showLlamaUi`（依赖 4f-2 / 4f-3 的 Theme 与组件）顺延 4f |
| `extensions/mcp/*` 余下（`ui.ts` 252） | ~252 | `oauth.ts`（P4b-2）、`resources.ts`（P4b-3）、`runtime.ts`（P4b-4）、`cli.ts`（P4b-5）、`index.ts` + `ui.ts` 非 TUI 半部（P4b-6）已收口；余 P4b-7（`ui.ts` 的 `McpManagerView` / `showMcpManager`，顺延 4f） |

#### package-manager 包解析 ✅ 已完成（4e-3c-2a `82b47aa` + 4e-3c-2b 本批）

| 原缺口 | 现状 |
| --- | --- |
| `resolvePackageSources`（`package-manager.ts`，2,760 行） | ✅ `DefaultPackageManager.ResolvePackageSourcesAsync`；`settings.packages` 的字符串形 / 对象形全链路打通 |
| `PathMetadata.packageRoot` | ✅ 字段已落地并有生产者；其唯一消费者 `collectExtensionPackageWarnings` 顺延 `4e-3c-3` |
| `applyAutoloadDisabledPatterns` / `isEnabledByOverrides` / `getOverridePatterns` / `matchesAnyPattern` | ✅ `PackageManagerPatterns` 逐条对齐（含 `+`/`-`/`!` 与四种路径拼法、`SKILL.md` 父目录三拼法） |

**余下（4e-3c-3，不阻塞 4f/4g）**：npm/git `update` / `checkForAvailableUpdates` 网络检查、
`ensureGitRef` / update marker / `refreshTemporaryGitSource`、npm legacy 全局根回退（C168）、
`collectExtensionPackageWarnings`、`ResourceLoader` 的 `additionalExtensionPaths`（CLI `-e`）接线。

#### 4f：模式层（23,705 行 / 67 文件）—— 最大单块

`modes/interactive/`（`interactive-mode.ts` 单文件 7,072 行 + components 30 余个 + theme 4 个）
+ `modes/rpc/`（`rpc-mode.ts` 819 / `rpc-client.ts` 617 / `rpc-types.ts` 303 / `jsonl.ts` 58）
+ `modes/print-mode.ts` / `json-output` / `json-event.ts`。
依赖：`Pi.Tui`（**已收口，44/45 文件**）+ `core/tools/renderers/*`（未移植，Theme 依赖）。

> **2026-10-14 进度**：**4f-2a 已收口**（`theme-json.ts` + `system-theme.ts` + `theme.ts` 模块级半边
> + `Pi.Tui` 的 OSC 终端配色查询链，2,745 行新 C# / 181 项新测试）。余 **4f-2b**
> （`theme-controller.ts`）顺延到 4f-4 之后，因为它要装 `InteractiveThemeController`。
> **4f-3a / 4f-3b 已收口**：4f-3a 是原语层（`keybinding-hints` / `visual-truncate` / `dynamic-border` /
> `themed-text`），4f-3b 是 `diff.ts` 渲染器 + 它依赖的 `JsDiff.DiffWords`（jsdiff 8.0.4 词级差分本体）。
> 两者合起来把 **4f-1（`core/tools/renderers/*`）的阻塞全部解开** —— 八个 renderer 里六个要 `keyHint`，
> `bash.ts` 要 `VisualLinePreview`，`edit.ts` 要 `renderDiff`。4f-1 现在可以整段开工。

#### 4g：入口与实验层（~12,000 行）

`main.ts`(999) / `index.ts`(494) / `package-manager-cli.ts`(1,141) / `cli.ts` / `rpc-entry.ts`(13)
+ `cli/*`(17 文件, 1,949) + `experimental/*`(46 文件, 9,950) + `bun/*` + `client/*`。

#### evals（1,300 行，未建项目）

---

## 三、优先级排序与依据

排序原则：**依赖倒序解开阻塞 → 单位工作量收益最大 → 风险最早暴露**。

| 序 | 阶段 | 为什么排这里 |
| ---: | --- | --- |
| 1 | **修复构建 + 基线**（已完成） | HEAD 不可编译时任何后续验证都无意义 |
| 2 | **4e-3e-9h-4 编排本体** | 它是**唯一**让 `Pi.CodingAgent` 端到端可跑的一环；`prompt`/`compact` 是所有上层（4f 交互模式、4g 入口）的入口。前置（`ExtensionRunner` 9g、installers 9h-3）已全部满足，无阻塞 |
| 3 | ~~**package-manager 缺口**~~ ✅ 已完成（4e-3c-2） | `resolvePackageSources` 缺失会让 4f/4g 的「装包 → 用包」链路在真机上失效；独立于 4f，可并行推进，且越早暴露越好。**已于 2026-10-13 收口** |
| 4 | **4d 尾巴（llama 流式 / MCP 余下）** | 自包含、与 4f 无耦合；MCP 是扩展生态的实际入口 |
| 5 | **4f 模式层** | 体量最大（23.7k）；依赖 `Pi.Tui`（已就绪）与 `core/tools/renderers`（需先补）。必须再切子阶段 |
| 6 | **4g 入口与实验层** | 依赖 4f 的交互/RPC 模式；`main.ts` 是最终可执行入口 |
| 7 | **evals** | 独立包，最后阶段 |

---

## 四、分阶段计划

> 收口标准（每阶段统一）：`dotnet build Pi.slnx -m:1` **0 警告 0 错误** + 相关测试全绿 +
> **TS↔C# 逐条一致性核对**（在 [coding-agent-porting-status.md](coding-agent-porting-status.md)
> 追加「已落地」对照表与设计差异 `C##`）+ 一次提交。

### 阶段 P1（= 4e-3e-9h-4a）：`AgentSession` 编排本体

- **目标**：`AgentSession` 具备完整的一次「提交 → 运行 → 结算」能力。
- **文件范围**：
  - `src/Pi.CodingAgent/Core/AgentSessionLoop.cs`（补实例面 `RunAgentPromptAsync` / `HandlePostAgentRunAsync` / `RunBeforeSettleBoundaryAsync`）
  - `src/Pi.CodingAgent/Core/AgentSessionBuild.cs`（`PromptAsync` 实现 + 拆掉两个缝）
  - `src/Pi.CodingAgent/Core/AgentSessionCompaction.cs`（`CompactAsync` / `RunAutoCompactionAsync` / `RunDefaultCompactionAsync` / `GetSummarizationRequestAuthAsync`）
  - `tests/Pi.CodingAgent.Tests/AgentSessionPromptTests.cs`（新建）
- **验收标准**：
  1. 构建 0 警告 0 错误；`Pi.CodingAgent.Tests` 全绿（≥1313）。→ **达成**：0 警告 0 错误；1323 / 0 失败。
  2. 逐条对照 TS：`prompt` 的 11 个分支（settled 延迟 / 扩展命令 / 压缩中拒绝 / input 处理器 / 模板展开 / 流式排队 / 模型校验 / 压缩检查 / before_agent_start / 图片归一 / 消息装配）全部落地。→ **达成**（对照表见 status 文档「4e-3e-9h-4a」节）。
  3. `_runAgentPrompt` 的 `while` 循环三处 `break` 与 `||` 短路语义与 TS 逐行等价（复用 9b 的 `ResolvePromptLoopStep`）。→ **达成**。
  4. 新增测试覆盖：扩展命令直通、流式排队、模型缺失/无凭据报错、`_handlePostAgentRun` 的 retry/compaction/queued 三路、`_runBeforeSettleBoundary` 的非法继续上报。→ **部分达成**（见下）。
- **P1 交付实况（诚实记录）**：
  - 已覆盖（`AgentSessionPromptTests` 10 项 + `AgentSessionBuildTests` 改造 1 项）：无模型 `prompt`/`compact` 的 TS 文案、
    扩展命令直通五路、`_runAgentPrompt` 单轮循环 + settled + run 级状态清空、agent run 失败仍收尾。
  - **未覆盖**（并入 P2 补齐，原因如下）：
    - *流式排队*：`IsStreaming => _isAgentRunActive`，无公开注入点，需在飞 run 才能构造 → 待 P2 的 SDK 装配提供可驱动入口后补。
    - *无凭据报错*：需注册带 `ApiKey` 的 native provider 且让 `CheckAuthAsync` 返回 `null`；当前夹具无此路径 → P2 一并建夹具。
    - *`_handlePostAgentRun` 三路 / `_runBeforeSettleBoundary` 非法继续*：均为**私有**方法且依赖模型 / 扩展边界处理器；其**纯决策**已由 9a～9e 的叶子测试锁死（`ResolvePromptLoopStep` / `AdvanceRetryAttempt` / `ShouldSkipCompactionCheck` / `ResolveInputHandlerOutcome`），实例接线留待 P2 的端到端用例。
- **前置**：无（9g / 9h-3 已满足）。

### 阶段 P2（= 4e-3e-9h-4b）：SDK 装配本体

拆两批，各自独立提交与推送（`createAgentSession` 与 `AgentSessionRuntime` 的装配本体体量互不相容，
强行合批会让一致性核对失去粒度）。

#### P2a（已完成）：`createAgentSession` 装配本体

- **目标**：`Sdk.CreateAgentSessionAsync` 端到端可创建并驱动一个会话。
- **文件范围**：`Core/Sdk.cs`（`CreateAgentSessionOptions` 扩到 TS 全量 + `CreateAgentSessionResult`
  + `CreateAgentSessionAsync`）、`Core/AgentSession.cs` / `Core/AgentSessionBuild.cs`（差异 C161）、
  `Core/CompatShims.cs` / `Core/Compaction.cs` / `src/Pi.Ai/Types/AssistantMessageEvent.cs` /
  `src/Pi.Ai/Api/LazyApis.cs`（reasoning 传递链修正）、
  `src/Pi.Agent/{Agent,AgentLoopRunner,AgentOptions,Types/AgentTypes}.cs` +
  `src/Pi.Ai/Types/ThinkingLevelText.cs`（reasoning 链修复）、
  `tests/Pi.Agent.Tests/AgentReasoningTests.cs`、`tests/Pi.CodingAgent.Tests/SdkCreateAgentSessionTests.cs`。
- **验收标准**：构建 0 警告 0 错误；`Pi.Agent.Tests` 32/32；`SdkCreateAgentSessionTests` 15/15；
  `Pi.CodingAgent.Tests` 全量无回归。→ **达成**（见台账）。
- **前置**：P1。

#### P2b（已完成）：`CacheWarmer` 完整化 + `AgentSessionRuntime` 编排

- **目标**：会话可切换 / 新建 / fork / 导入，且缓存预热真正按 TS 语义重放请求。
- **文件范围（实际）**：
  - `Core/CacheWarmer.cs`（重写：`CacheWarmingDecisionEvent` / `CacheWarmingStopDetail` / `CacheWarmRequest`
    扩形 / 三个委托 / `ActiveRun` / `Start` / `OnAgentSettled` / `OnModeChanged` / `Cancel` / `Schedule` /
    `RefreshAsync` / `Evaluate` / `ValidateRun` / `GetModeStopReason` / `FormatCacheWarmingUsage` 修正）
  - `Core/AgentSessionRuntime.cs`（`static class` → 实例类；编排本体 + `CreateAgentSessionRuntimeAsync`）
  - `Core/AgentSessionServices.cs`（`AgentSessionServicesBuilder` 从空壳改为装配器：
    `CreateAgentSessionServicesAsync` / `CreateAgentSessionFromServicesAsync` + 3 个选项记录）
  - `Core/AgentSessionInstallers.cs`（`CreateReplacedSessionContext` / `HasExtensionHandlers` /
    `ExtensionRunner` + `ReplacedSessionContext` 包装类；`Dispose` 清 `OnWarmed`）
  - `Core/Sdk.cs`（`cacheWarmer` 四入参 + `decide` 接扩展 runner；`CacheWarmRequest` 补 `Context` / `RequestOptions`）
  - `Core/SessionManager.cs`（实现扩展层 `ISessionManager` + `ISessionCwdSource`）
  - `tests/Pi.CodingAgent.Tests/CacheWarmerTests.cs`（重写）、`AgentSessionRuntimeTests.cs`（新建）
- **验收标准**：逐条对照 TS `agent-session-runtime.ts` 447 行 + `agent-session-services.ts` 233 行 +
  `cache-warmer.ts` 454 行；构建 0 警告 0 错误；测试全绿。→ **达成**：
  `CacheWarmerTests` 11/11、`AgentSessionRuntimeTests` 22/22、`Pi.CodingAgent.Tests` **1364 / 0 失败**、
  `Pi.Agent.Tests` 32/32、`Pi.Ai.Tests` 301/301。
- **已核实的真实缺陷（本批已修）**：
  1. `CacheWarmerMath.GetPromptCacheTtlMs` 在「未显式给 `cacheRetention` 且 `PI_CACHE_RETENTION != "long"`」时
     返回 `null`；TS（`cache-warmer.ts:39-46`）回落 `"short"`。后果：**默认配置下缓存预热永远不激活**
     （`Start` 直接 `stop("cache lifetime unavailable")`）。原测试 `CacheWarmerTests.GetPromptCacheTtlMs_RequiresRetention`
     把这个错误行为写死，已随源码一并改正。
  2. `FormatCacheWarmingUsage` 的尾随零语义偏离 TS（旧实现会把 `$0.010000` 输出成 `$0.01`）。已改为
     「`toFixed(6)` 后去尾随零、至少保留 3 位小数」。
- **新登记差异**：**C162**（`CacheWarmRequest` 另带原始请求字典 `RequestOptions`）、**C163**（三个委托
  替代 TS 的结构化类型 `Pick<…>`）。
- **前置**：P2a。

### 阶段 P3：package-manager 包解析 ✅ 已完成

- **目标**：`settings.packages` → 资源路径全链路打通。
- **文件范围（实际）**：
  - `Core/PackageManager.cs`（全量重写，约 1,340 行：`PackageEntry` / `PackageFilter` / `ConfiguredPackage` /
    `MissingSourceAction` / `ResourceMap` / `Accumulator` / `DedupePackages` / `FindAutoloadDeltaBase` /
    `ResolvePackageSourcesAsync` / `CollectPackageResources` 全族 / `ToResolvedPaths` / `ListConfiguredPackages` /
    `GetInstalledPath` / `ResolveExtensionSourcesAsync`）
  - `Core/PackageManagerPatterns.cs`（新增）、`Core/PackageManagerCollectors.cs`（新增）
  - `Utils/Glob/GlobSync.cs`（新增）、`Utils/SemverRange.cs`（新增）
  - `Core/Skills.cs`（4 个 ignore 助手提为 `internal`）
  - `Core/SettingsManager.cs`（**删除** 8 个臆造成员）
  - `tests/Pi.CodingAgent.Tests/{SemverRangeTests,GlobSyncTests,PackageManagerPatternsTests,PackageManagerCollectorsTests}.cs`（新增）
    + `PackageManagerTests.cs`（重写为 24 项）+ `ResourceLoaderTests.cs`（夹具修正）
- **验收标准与实际达成**：
  1. `+`/`-`/`!` 前缀与路径模式匹配逐条对照 → **达成**（`PackageManagerPatternsTests` 35 项）。
  2. 回归守卫测试（已装包 → 扩展/skill 可加载）→ **达成**（`PackageManagerTests` 24 项含本地包 / manifest glob / project 胜 user / `autoload:false` delta / `onMissing` 三路）。
  3. `collectExtensionPackageWarnings` 落地 → **未达成**，顺延 `4e-3c-3`（其唯一输入 `PackageRoot` 本批已就位，但该函数还依赖扩展告警聚合链路）。
  4. 构建 0 警告 0 错误 + 测试全绿 → **达成**（`Pi.CodingAgent.Tests` 1488 / 0 失败）。
- **前置**：无（与 P1/P2 并行推进）。
- **本批核实并修掉的真实缺陷**：
  1. `SettingsManager` 里 `include` / `exclude` / `autoload` 三个 settings 键是端口**臆造**的——TS `settings-manager.ts` 的 `Settings` 根本没有它们（真实 schema 是 `settings.packages`，override 前缀写在各资源数组内部）。8 个成员双侧删除后 `settings.packages` 才真正可走。
  2. `IsExactNpmVersion` 漏 loose 写法（`v1.2.3` 被误判为非精确版本）→ 改为 `Semver.Valid(version) is not null`。
  3. `ToResolvedPaths` 未做稳定排序（同 rank 的来源依赖字典迭代序）→ 改为按插入序号稳定排序。
  4. `ResourceLoaderTests` 夹具假设「home 下没有 `.agents/skills`」，在本机真实存在 45 个技能时失败（产品实现正确，TS 语义即如此）→ 夹具断言收敛到测试临时根。
- **新登记差异**：**C164–C168**（见 status 文档「4e-3c-2」节）。

### 阶段 P4：4d 尾巴

- **P4a（已完成，`bafb72e` 已推送）** llama `provider.ts` + `index.ts` + `ui.ts` 的非 UI 半部；
  `BuiltInExtensions` 的 `llama.cpp` 占位工厂替换为真实工厂；新增 `Utils/JsNumber.cs`。
  **验收标准与实际达成**：逐条对照 `provider.ts`（认证 `login`/`resolve`、`refreshModels` 五步、
  六个 shaping 函数）与 `index.ts`（`/llama` 命令的目录同步 / 加载 / 卸载 / 下载编排）；
  构建 0 警告 0 错误；`LlamaExtensionTests` 44/44；变异套件 `llama-extension` 24 处全捕获
  （首轮 23/24，`ui-context-label-rounding` 盲点补语料后捕获）。新登记 C169–C173。
- **P4b** MCP 余下。勘察后确认比预估大：`Pi.Mcp` 库本身还缺两块（**认证接线**与**资源列举**），
  必须先补齐才能接 `oauth.ts` / `runtime.ts` / `resources.ts`。拆成 7 个子阶段：

  | 子阶段 | 范围 | 依赖 |
  |---|---|---|
  | **P4b-1** | `Pi.Mcp` 补全：`McpFetch` / `UnauthorizedContext` / `McpAuthProvider`（`Token`+`OnUnauthorized`）；`StreamableHttpTransport` 接 `AuthProvider`（`Bearer` 头、401/403-`insufficient_scope` 重试一次）；`McpClient` 补 `ListResources` / `ListResourcesPage` / `ListResourceTemplates` / `ListResourceTemplatesPage` 与 `ListResourcesResult` / `ListResourceTemplatesResult` | — |
  | **P4b-2** | `oauth.ts`(532) → `Extensions/Mcp/OAuth.cs`：`McpOAuthCredentialStore`（`mcp-auth.json`、legacy 键接管、`withRefreshLock`）、`createMcpAuthProvider`（刷新去重 + 30s skew + 锁）、`signInMcpServer`（PKCE 回调、CIMD、粘贴 redirect URL）、`callbackSettings` / `mergeScopes` / `callbackId` / `clientMetadataDocument` | P4b-1 |
  | **P4b-3** | `resources.ts`(339) → `Extensions/Mcp/Resources.cs`：`list_mcp_resources` / `list_mcp_resource_templates` / `read_mcp_resource` 三个工具 + `isMcpAppResource` + 分页/聚合语义 | `Tools.cs`（已就位） |
  | **P4b-4** | ✅ **已收口**（4d-11）：`runtime.ts`(491) → `Extensions/Mcp/Runtime.cs`：`McpServerConnection`（懒连接、状态机、瞬时错误重试、会话过期重连、needs-auth、stderr 尾、`close` 等刷新落盘）、`createDefaultTransport`、`fetchResources`、`withoutTemplates` | P4b-1/2/3 |
  | **P4b-5** | ✅ **已收口**（4d-12）：`cli.ts`(614) → `Extensions/Mcp/Cli.cs`：`pi mcp list/login/logout/...` 子命令 | P4b-2/4 |
  | **P4b-6** | ✅ **已收口**（4d-13）：`index.ts`(1225) → `Extensions/Mcp/McpExtension.cs`：`createMcpExtension`（后台连接、exposure 语义、`mcp_servers` section、`/mcp` 命令、`toolExposure` 覆盖、启动问题汇报）+ `ui.ts` 的非 TUI 半部（`McpMenu` / `IMcpUi` 缝） | P4b-2/3/4/5 |
  | **P4b-7（顺延 4f）** | `ui.ts` 的 `McpManagerView` / `showMcpManager`（依赖 `Theme`、`SelectList`、`DynamicBorder`、`AuthUrlComponent`、`keyHint`） | 4f-2/4f-3 |

- **P4c（顺延 4f）** llama `ui.ts` 的 `LlamaView` / `HuggingFaceSearch` / `showLlamaUi`：渲染在
  4f-2 的主题与 4f-3 的组件之上，随 4f-3 落地后接上 `LlamaExtensionOptions.ShowUi`。
- **验收标准**：逐文件对照；MCP 走真实 stdio/HTTP 假服务器回放（沙箱禁端口的部分标环境限制）。
  P4b-1…P4b-6 已全部收口；`ui.ts` 的 TUI 组件（P4b-7）随 4f 落地。`BuiltInExtensions` 里 `mcp` 的
  `PlaceholderFactory("mcp","4d-4")` 仍在，随 P6（4g 入口接线）替换为
  `McpExtension.CreateMcpExtension()`。
- **前置**：无。

### 阶段 P5：4f 模式层（拆 5 个子阶段）

**2026-10-14 勘察修正**（P4b-6 收口后做的依赖核实，原顺序有误）：renderers **不能先做**。
`core/tools/renderers/*` 的每个文件都 import `modes/interactive/theme/theme.ts` 的 `theme` 单例
（`theme.fg` / `theme.bold`）以及 `modes/interactive/components/{keybinding-hints,visual-truncate}.ts`
（`keyHint` / `VisualLinePreview`），而这两者正是 4f-2 / 4f-3 的产物。另外
`Theme` 目前只是 `Core/Extensions/Types/Placeholders.cs` 里的空占位类（`public sealed class Theme { }`），
`EditorTheme` 已在 `Pi.Tui/Components/Editor.cs`，但 `MarkdownTheme` / `SelectListTheme` /
`SettingsListTheme` 尚未落地（`theme.ts` 的 getter 返回它们）。故实际顺序为
**4f-2 → 4f-3（先 `keybinding-hints` / `visual-truncate`）→ 4f-1 → 4f-4 → 4f-5**。

| 子阶段 | 范围 | 验收标准 |
| --- | --- | --- |
| 4f-2 | `modes/interactive/theme/*`（2,216 行：`theme.ts` 1,160 / `system-theme.ts` 655 / `theme-controller.ts` 253 / `theme-json.ts` 148） | **4f-2a ✅ 已收口**（`theme.ts` 模块级半边 + `system-theme.ts` + `theme-json.ts` + `Theme` 类 + TUI 助手 + OSC 查询链）；`Theme` 占位已删；`MarkdownTheme` / `SelectListTheme` / `SettingsListTheme` 三个形状已由 `ThemeHelpers` 产出。**4f-2b ✅ 已收口**（`theme-controller.ts` 253 行 → `Theme/ThemeController.cs` + 27 例，`IThemeControllerRuntime` 注入缝，见 C261） |
| 4f-3 | `modes/interactive/components/*`（实测 **11,652 行** / 49 文件） | 每个组件一个测试类；选择器交互路径。**4f-3a ✅ 已收口**（原语层：`keybinding-hints` + `visual-truncate`，解 4f-1 阻塞；另附 `dynamic-border` + `themed-text`）。**4f-3b ✅ 已收口**（`diff.ts` 渲染器 + 前置的 `JsDiff.DiffWords` 词级差分；解 4f-1 的 `edit` renderer 阻塞）。**4f-3c ✅ 已收口**（消息类组件 11 个：assistant / user / custom / branch-summary / compaction-summary / skill-invocation / status-indicator / countdown-timer / auth-url / pi-logo / markdown-transform；另修 4f-1 语料套件的 27 处遗留失败）。**4f-3d 🟡 进行中**：选择器组共 **21 个**，按体量切三批。**Batch C ✅ 已收口**（`theme-selector` / `show-images-selector` / `trust-selector` / `thinking-selector` / `user-message-selector` 五个无外部依赖的，合计 560 行 TS → 625 行 C#，附 42 项测试）。**Batch D ✅ 已收口**（`extension-selector` / `extension-input` / `radius-login-selector` / `first-time-setup` 四个扩展面向的对话框，473 行 TS → 653 行 C#，附 39 项测试；只依赖 4f-3a/4f-3c 已收口的原语）。**Batch E-1 ✅ 已收口**（`session-selector-search`，194 行 TS → 355 行 C#，附 27 项测试；纯查询层，是 `session-selector` / `tree-selector` / `config-selector` / `settings-selector` 四个大件的共同底座）。**Batch E-2 ✅ 已收口**（`login-dialog`，238 行 TS → 310 行 C#，附 17 项测试；OAuth 登录面板，含 `showAuth`/`showDeviceCode`/`showManualInput`/`showPrompt`/`showDetails`/`showInfo`/`showWaiting`/`showProgress` 八步与取消/复制键三路 `handleInput`；另给 `OpenBrowser` 加 `OpenOverride` 测试缝）。**Batch E-3 ✅ 已收口**（`settings-submenu`，258 行 TS → 387 行 C#，附 26 项测试；`SelectSubmenu` + `SteppedSubmenu` + `SubmenuText` 联合类型，是 `settings-selector` 与 `config-selector` 的直接底座）。**同批收口 C233**：`Container` / `Text` / `Box` 三个可继承基类补 `public virtual void HandleInput`、9 个派生组件改 `override`（接口映射建在「列出 `IComponent` 的类」上，此前经接口调用会静默落到空默认实现，**影响 Batch C/D 全部组件**，只是那些测试都直接持有具体类型才没暴露）；配套修正 `TuiComponents.HasHandleInput` 的 no-op 排除表。**Batch E-4 ✅ 已收口**（`oauth-selector`，224 行 TS → 360 行 C#，附 44 项测试；登录/登出时选认证 provider 的面板 + `formatAuthSelectorProviderType` / `formatAuthSelectorProviderStatus` 两个纯函数；`authType` 复用 `CredentialKind`、`method` 用具名联合 `AuthSelectorMethod`、`mode` 枚举化）。**Batch E-5 ✅ 已收口**（`custom-editor`，148 行 TS → 220 行 C#，附 22 项测试；`CustomEditor : Editor` + `CustomEditorOptions : EditorOptions`，`renderTopBorder` 三段回退、`handleInput` 七层按键优先级；同批把 `EditorOptions` 去掉 `sealed`、`Editor.HandleInput` 改 `virtual`，登记 **C238**）。**Batch E-6 ✅ 已收口**（`scoped-models-selector`，401 行 TS → 520 行 C#，附 24 项测试；`ModelsConfig`/`ModelsCallbacks` 两个记录 + `EnabledIds`（`null` = 全开）代数七个纯函数 + `ScopedModelsSelectorComponent`；另收口 `model-search.ts` → `ModelSearch.cs` 两个 haystack 构造函数）。**余 6 个 / 4,984 行**：`extension-editor`（141，依赖未移植的 `external-editor.ts`）/ `model-selector`（421）/ `config-selector`（951）/ `settings-selector`（993）/ `session-selector`（1045）/ `tree-selector`（1435）；**4f-3e ✅ 全部收口**（`mermaid` 随 Batch 4，按「transformer 骨架 + `IMermaidRenderer` 注入缝」处理，引擎本体不移植；`footer` / `tool-execution` / `bash-execution` 随 Batch 2，`earendil-announcement` / `bordered-loader` / `custom-entry` 随 Batch 1，`armin` / `easter-egg-3d` 随 Batch 3）；`index.ts` 为纯 re-export barrel 不移植 |
| 4f-1 | `core/tools/renderers/*`（1,033 行 / 8 文件）+ `core/tools/render-utils.ts`（112 行） | ✅ **已收口**（`a0caf71`，2026-10-15 补记）：8 renderer + `RenderUtils` 全量 + 104 case 组件树语料；`ToolRenderers` 已补 `RenderCall` / `RenderResult`（C85 解除） |
| 4f-4 | `modes/interactive/interactive-mode.ts`（7,072 行） | 主循环 / 键位 / 视口 / 共享。**🟡 进行中**，311 个成员，按依赖顺序切 **13 个子批次**：**4f-4a ✅ 已收口**（展示层纯函数 L1381-1762 → `InteractiveModeFormatting.cs` + 59 例：路径缩短 / 来源标签 / 启动门控 / scope 分组 / 诊断渲染）。余 10 批：**4f-4b ✅ 已收口**（`tui-renderer.ts` 81 行 → `TuiRenderer.cs` + 7 例：`CreateInteractiveTui` 双模式组合根 + `CreateInteractiveTuiReference` 的 `ITui` 逐成员转发，C262）→ **4f-4c ✅ 已收口**（模块级层 → `InteractiveModeModule.cs` + 23 例：`IExpandable`/`CompactionQueuedMessage` 类型、`ExpandableText`/`BuiltInHeader` 组件、`QuoteIfNeeded`/`FormatCrashExtensionHint`/`LlamaCppPostLoginGuidance`/`IsAnthropicSubscriptionAuthKey`/`IsDeadTerminalError` 纯函数；C263 + C264。**顺延到后续批**：`formatResumeCommand`（需会话文件系统检查，随 4f-4l）、`createFuzzyAutocompleteItems` + login provider 三函数（随 4f-4e 自动补全）、`RenderSessionItem` 三个判定 + `CompactionCostNotice` + `IsUnknownModel`/`HasDefaultModelProvider`（随 4f-4j 渲染））→ **4f-4d ✅ 已收口**（诊断与文案叶子层 → `InteractiveModeDiagnostics.cs` + 39 例：`BuildTerminalTitle` / `ChatNotice` 三构造 + `ShouldCoalesceStatus` / `ResolveEditorBorderColor` / `BugReportHint` / `CrashReportInstructions` / `ShouldSuggestBugReport` + `IsAbortOrCancelMessage` / `BuildInstallChangeWarning`；C265）→ **4f-4e ✅ 已收口**（自动补全辅助层 → `InteractiveModeAutocomplete.cs` + 24 例：`CreateFuzzyItems` 的 null 语义 / 登录 provider 合并与两种文案 / 来源标签与描述前缀 / 内置命令冲突诊断；`OAuthSelectorComponent.AuthTypeText` 提升为 public 复用线上拼写。`createBaseAutocompleteProvider` 的命令列表组装读 `this.session`，随 4f-4m）→ **4f-4f ✅ 已收口**（启动横幅与资源展示叶子层 → `InteractiveModeStartup.cs` + 20 例：`FormatSectionHeader` / `FormatCompactList`（trim + 丢空 + localeCompare 排序 + 两空格 dim 缩进）/ `ExtractLatestChangelogVersion` + `BuildCondensedChangelogNotice` / `CollectExtensionDiagnostics`。`showLoadedResources` 的容器组装（ExpandableText 段落 + contextFiles + 四类诊断块）随 4f-4m）→ **4f-4g ✅ 已收口**（状态指示器决策层 → `InteractiveModeStatus.cs` + 16 例：`IsWorkingStatusEditor` 类型守卫 / `ResolveWorkingStatusColor` 边框色回落链 / `ResolveWorkingMessage` 与 `ResolveHiddenThinkingLabel` 的「仅 null 回落」/ `ShouldRestoreIdleStatus` 四条件 / `DefaultWorkingMessageWithInterrupt`；C266。容器搬运（statusContainer / 编辑器重绑）随 4f-4m） → **4f-4h ✅ 已收口**（扩展绑定辅助层 → `InteractiveModeExtensions.cs` + 14 例：`MaxWidgetLines`/`WidgetContent` 截断 / `ConfirmTitle`+`IsConfirmed`+`ConfirmOptions` / `GetCrashExtensionHint` 的 try-catch 塌陷 / `ShortcutErrorMessage` / `ShouldPersistThemeName` 写盘门控。`bindCurrentSessionExtensions` 的 commandContextActions 随 4f-4m） → **4f-4i ✅ 已收口**（编辑器提交路由层 → `InteractiveModeSubmit.cs` + 55 例：`SubmitCommand` 27 值枚举 + `Route` 把 200 行 if-chain 变成表（五个带参命令的切片长度 /bug=4、/model=7、/thinking=10、/login=7、/compact=9 是可观测契约；19 个精确命令全等匹配；/export /import /name 收整行）+ `ParseBashCommand`（`!`/`!!`、裸 `!` 不是 bash 行）+ `GetSlashCommandName` + `BashBusyWarning`。onSubmit 闭包随 4f-4m）→ **4f-4j ✅ 已收口**（消息与渲染决策层 → `InteractiveModeRender.cs` + 31 例：`CompactionCostNotice` + `CompactionCostKind`（4f-4c 顺延类型落地）/ `IsRenderedDirectly` / `ShouldAppendCompactionCost` / `IsFailedStopReason` / `ResolveToolResultErrorMessage` 的重试复数化 / `GetUserMessageText` / `IsUnknownModel`。`handleEvent` 分发与组件构造随 4f-4m）→ **4f-4k ✅ 已收口**（斜杠命令辅助层 → `InteractiveModeCommands.cs` + 20 例：`GetPathCommandArgument` 的引号/空白解析 / `ReloadedStatusMessage` / `ImportUsageError` / `BashFailureMessage` 的 Unknown error 回退 / `BuildHotkeysMarkdown` 三段表 + Windows 提示 + 扩展快捷键段。`handleSessionCommand` 的 Session Info 文档是纯渲染器且读 `this.session`，随 4f-4m）→ **4f-4l ✅ 已收口**（生命周期叶子层 → `InteractiveModeLifecycle.cs` + 16 例：`FormatResumeCommand`（isTty / isPersisted / 文件存在三道独立门 + 自定义目录经 QuoteIfNeeded）/ `ResolveTmuxKeyboardWarning`（查询失败刻意沉默，off 优先于 xterm 格式）/ `ShouldSkipChangelog` / `IsFreshInstall`。三个 selector 对话框与 `init`/`run`/`shutdown` 直接操纵容器与 runtimeHost，随 4f-4m）→ **4f-4m** 类装配（`InteractiveMode` 字段块 + `InteractiveModeOptions` + 便利访问器 + 构造器 + 接线）。**批次调整（4f-4d 时定案）**：原计划把「类骨架 + 构造器」放 4f-4d，实测构造器**传递依赖整个类**——`resetExtensionUI` 要 10 个扩展 UI 方法、`rebindCurrentSession` 要 7 个会话生命周期方法，均属 4f-4h/4f-4j 范围，故 4f-4d 改为诊断与文案叶子层，类骨架顺延为收尾批 4f-4m，子批数 12 → 13。**待办接线**：`mermaid` transformer 注册（L516）、`ctx.reload()` 宿主侧（C211）、`getLoginProviderOptions`（C236 上游测试第 1 例） |
| 4f-5 | `modes/rpc/*` + `print-mode` + `json-*`（~2,300 行） | RPC 端到端（Unix 域套接字回放） |

### 阶段 P6：4g 入口与实验层（拆 3 个子阶段）

`4g-1` `cli/*` ✅ → `4g-2` `main.ts` / `index.ts` / `package-manager-cli.ts` / `rpc-entry.ts` ✅（`5c1d79a`）→ `4g-3` `experimental/*` + `bun/*` + `client/*`（**下一步**）。

### 阶段 P7：evals（1,300 行，新建项目）

---

## 五、风险与阻塞

| # | 风险 | 影响 | 处置 |
| --- | --- | --- | --- |
| R1 | **推送通道间歇不可用（本轮再次发生，已有可用绕行）** | 阶段产出无法上远端 | 实测（2026-10-13 上午）：`https` 经本机代理对 `github.com` 返回 **502 CONNECT tunnel failed**；`ssh -p 443 git@ssh.github.com` 可达但 **publickey 未授权**。**2026-10-13 下午代理短暂恢复**：`git push origin main` 成功（`f208567..7b942b3`）。**2026-10-13 晚再次 502（连续 5 次重试均失败，`github.com` CONNECT 502 / `Empty reply`）**，但同代理下 `api.github.com` 正常（200）→ 改用 **REST API 复制提交**绕行（见下），已成功推送 `bb874f6`；**P2b 推送时代理又恢复**，`git push origin main` 成功（`e70f5dd..02707fd`）。**4e-3c-2a / 4e-3c-2b 推送时代理再次 502**（`fatal: unable to access … schannel: server closed abruptly`），改用下述 REST API 绕行**一次推上两个提交**（`82b47aa` + `05e9c58`，远端 head `05e9c58d`）。**P4a 推送时代理/SSH 恢复**：`git push origin main`（走 `git@github.com`）一次成功（`696dab5..bafb72e`）。<br>**绕行做法（可复用）**：`GET /repos/:o/:r/git/ref/heads/main` 确认远端 HEAD == 本地 `HEAD^`；对每个改动文件 `POST /git/blobs`（内容取 `git cat-file blob <sha>:<path>` 的原始字节，base64），校验返回 SHA == 本地 blob SHA；`POST /git/trees`（`base_tree` = 父树）校验树 SHA == 本地树 SHA；`POST /git/commits`（message / author / committer / date 与本地**逐字节一致**）校验提交 SHA == 本地 SHA；最后 `PATCH /git/refs/heads/main`。因 SHA 完全相同，本地与远端天然同步，无需 `fetch`；本地 `refs/remotes/origin/main` 用 `git update-ref` 手工对齐。<br>**其它出路**：把 `~/.ssh/id_rsa.pub` 加到 GitHub 账号后改走 `ssh://git@ssh.github.com:443/xuzeyu91/pi.net.git`。 |
| R2 | 参照检出漂移 | 语义错位（9-0 已发生过一次） | **2026-10-16 再次发生并已处置**：检出从 `428a12bc7` 漂到 `eb326d26`（25 提交，6 组真实特性）。**约定修订**：以当前检出为唯一参照真值；旧基线下「TS 没有 → 删除」的判定不再自动成立（`ToolRenderContext.DurationMs` / `GetToolListError` / `BashRenderers` 优先分支三处即因此被误删）。详见第零节 |
| R3 | 沙箱构建/测试限制 | 假阳性失败 | 构建必须 `-m:1`；`dotnet test` stdout 为空，改跑 `.exe` + `iconv GBK→UTF-8`；过滤用 `--filter-class` |
| R4 | `4f` 体量（23.7k 行） | 单会话不可完成 | 已切 5 个子阶段，每子阶段独立提交 |
| R5 | ~~package-manager 缺口~~ ✅ 已关闭 | 真机装包链路失效 | 已于 2026-10-13 收口（4e-3c-2a `82b47aa` + 4e-3c-2b）；余 `4e-3c-3` 的 update/网络检查不阻塞 4f/4g |
| R6 | 已登记但未复现的差异（C152 消息替换的引用同一性、C154 `tool_call` 入参不可改写） | 扩展行为受限 | 已在 9h-3 记录为真实缺口，随 P1/P2 复核是否需要补偿 API |

---

## 六、进度台账

| 日期 | 阶段 | 内容 | 提交 | 测试 |
| --- | --- | --- | --- | --- |
| 2026-10-13 | 基线 | 修复 `AgentSessionQueue.cs` 缺 `using`（CS0103），构建恢复 0 警告 0 错误 | 随 P1 一并提交 | 1313 / 1313 ✅ |
| 2026-10-13 | 计划 | 产出本文档 | 随 P1 一并提交 | — |
| 2026-10-13 | P1 | 4e-3e-9h-4a `AgentSession` 编排本体（`prompt`/`compact`/`_runAgentPrompt`/`_handlePostAgentRun`/`_runBeforeSettleBoundary`/`_runAutoCompaction`/`_checkCompaction`/`_runDefaultCompaction`/认证与重试叶子）；退役 4 处 `NotSupportedException` 缝；补记 C158/C159（上批遗漏）、新登记 C160 | `7b942b3`（已推送） | 1323 / 0 失败 ✅ |
| 2026-10-13 | P2a | 4e-3e-9h-4b（第一半）`createAgentSession` 装配本体（`Sdk.CreateAgentSessionAsync`，TS `sdk.ts:181-468`；`CreateAgentSessionOptions` 扩到 TS 全量 + `CreateAgentSessionResult`）；顺带修正三处 reasoning 传递链缺口（`Pi.Agent` 忽略 `ThinkingLevel`、`AgentOptions` 缺 6 字段、`ChainOfThought` 臆造字段）；新登记 C161 | `bb874f6`（已推送） | `Pi.CodingAgent.Tests` **1338 / 0 失败** ✅；`Pi.Agent.Tests` 32/32；`Pi.Ai.Tests` 301/301；构建 0 警告 0 错误 |
| 2026-10-13 | P2b | 4e-3e-9h-4b-2 `CacheWarmer` 完整化（含已核实的 `GetPromptCacheTtlMs` `"short"` 回落缺陷与 `FormatCacheWarmingUsage` 尾随零语义）+ `AgentSessionRuntime` 编排本体（`switchSession` / `newSession` / `fork` / `importFromJsonl` / `dispose` / `createAgentSessionRuntime`）+ `AgentSessionServicesBuilder` 装配器（`createAgentSessionServices` / `createAgentSessionFromServices`）；新登记 C162/C163 | `02707fd` | `Pi.CodingAgent.Tests` **1364 / 0 失败** ✅；`Pi.Agent.Tests` 32/32；`Pi.Ai.Tests` 301/301；构建 0 警告 0 错误 |
| 2026-10-13 | P3（4e-3c-2a） | package-manager 叶子层：`PackageManagerPatterns`（patterns 纯函数）/ `PackageManagerCollectors`（目录采集器）/ `GlobSync`（`globSync` 子集）/ `SemverRange`（`validRange`/`satisfies`）+ `Skills` 侧 4 个 ignore 助手提为 `internal` 共享；新登记 C164–C167 | `82b47aa`（已推送） | 114 项新测试全绿（`SemverRangeTests` 51 / `PackageManagerPatternsTests` 35 / `GlobSyncTests` 12 / `PackageManagerCollectorsTests` 16）；`PackageManagerTests` 14/14、`ResourceLoaderTests` 19/19 无破；构建 0 警告 0 错误 |
| 2026-10-13 | P3（4e-3c-2b） | `DefaultPackageManager` 全链路重写（`ResolveAsync` / `ResolvePackageSourcesAsync` / `DedupePackages` / `FindAutoloadDeltaBase` / `PackageRoot` / 完整 `+`/`-`/`!` override 语义 / `ResourceMap`+`Accumulator`+`ToResolvedPaths` 稳定排序 / `ListConfiguredPackages` / `GetInstalledPath` / `ResolveExtensionSourcesAsync`）；**删除 SettingsManager 里臆造的 `include`/`exclude`/`autoload` 三个 settings 键通路（8 个成员）**；顺带修 `IsExactNpmVersion` 漏 loose、`ToResolvedPaths` 不稳定排序、`ResourceLoaderTests` 夹具缺陷；新登记 C168 | `05e9c58`（已推送） | `Pi.CodingAgent.Tests` **1488 / 0 失败** ✅；`PackageManagerTests` 24/24；`ResourceLoaderTests` 19/19；构建 0 警告 0 错误 |
| 2026-10-13 | P4a（4d-7c） | llama `provider.ts` + `index.ts` + `ui.ts` 非 UI 半部；`BuiltInExtensions` 的 `llama.cpp` 占位工厂 → 真实工厂；新增 `Utils/JsNumber.cs`；`LlamaModelInfo` 补 5 个只读访问器；新登记 C169–C173 | `bafb72e`（已推送） | `LlamaExtensionTests` 44/44 ✅；`BuiltInExtensionsTests` 更新；构建 0 警告 0 错误；变异套件 `llama-extension` 24 处全捕获（首轮 23/24，`ui-context-label-rounding` 盲点补语料后捕获） |
| 2026-10-13 | P4b-1（4d-8） | `Pi.Mcp` 补全：新增 `AuthProvider.cs`（`McpFetch` / `UnauthorizedContext` / `AuthProvider`）；`StreamableHttpTransport` 接认证（`SendAuthorizedAsync`：Bearer 头、401/403-`insufficient_scope` 重试一次、`FetchAsync` 暴露给提供者）；`OAuthFlow.AdaptOAuthProvider`；`McpClient` 补 4 个资源列举方法 + `ListPageAsync`；`McpResource`/`McpResourceTemplate` 加 `Raw`；删去无引用的 `OAuthFlow.AuthProvider` 与嵌套 `OAuthDiscovery.McpFetch`；新登记 C174–C178 | 见本批提交 | `Pi.Mcp.Tests` **35 / 0 失败** ✅（新增 16）；构建 0 警告 0 错误 |

| 2026-10-14 | P4b-5（4d-12） | `pi mcp` 命令：`Extensions/Mcp/Cli.cs`（1,014 行）——`RunMcpCommandAsync` 七路（help / add / remove / list / login / logout / unknown，退出码逐条对齐）；参数解析保留 TS 的两处细节（`-l` 别名在判断 `--` 前替换、`--` 与 `maxPositionals` 都结束选项解析使命令自身选项透传）；`add` 的 HTTP/stdio 两路 config（`--bearer-token-env-var` → `Authorization: Bearer ${NAME}`、`oauth` 子对象、`Number()` 语义的 callbackPort、非法调用一律不落盘）；`list` 并发体检 + 文本/JSON 两种输出（可空字段按 `JSON.stringify` 丢 `undefined` 语义省略）；`login` 先连接判定 → challenge → `SignInServerAsync` → 清 challenge → 重连，`logout` 走 `RemoveAsync`；粘贴 redirect URL 的终端读取把「读一行」与取消令牌赛跑；新增 `McpServerConnection.ClearChallenge()`；新登记 C187/C188 | 本轮提交 | `McpCliTests` 15/15（向量转录自 TS `mcp-command.test.ts`）；`Pi.CodingAgent.Tests` 全类 **1662 / 0 失败**（3m27s）；构建 0 警告 0 错误 |
| 2026-10-14 | P4b-4（4d-11） | MCP 连接层：`Extensions/Mcp/Runtime.cs`（815 行）——`McpServerConnection`（懒连接与记忆化打开；六态状态机；能力门控的 tools/resources 发现并剔除 MCP App；`readOnly` 请求遇瞬时 HTTP 错误 250ms 后同客户端重试一次而工具调用不重试；会话过期在新会话重试一次；needs-auth 标记 + `/login <provider>` 或 `/mcp` 文案；stderr 尾拼进「连接失败」与「连接关闭」消息；`close` 中止在途连接并等 `authProvider.settled()`）+ `CreateDefaultTransport`（HTTP/stdio 两路、env 与 args 解析展开）+ `FetchResourcesAsync`（并行、失败降级为空）+ `WithoutTemplatesAsync`（`method-not-found` → 空模板）；前置补 `Pi.Mcp` 的 `StdioTransport.Stderr`（`MaxStderrBytes` 64KB 滚动缓冲 + `Stderr` 属性，此前子进程报错全丢，用户只看到 `spawn failed`）；**自纠一处移植陷阱**：`_opening ??= OpenOnceAsync()` 在同步完成的打开上会残留已完成任务（C# async 无挂起时 `finally` 同步执行，JS 的 `.finally` 总在微任务），导致掉线/登出/会话过期后的重连拿到死客户端，改为「先赋值 memo 再注册清空续体」；新登记 C184–C186 | 本轮提交 | `McpRuntimeTests` 40/40；`Pi.Mcp.Tests` 35/35；`Pi.CodingAgent.Tests` 全类 **1647 / 0 失败**（3m08s）；构建 0 警告 0 错误 |
| 2026-10-14 | P4b-3（4d-10） | MCP 资源工具：`Extensions/Mcp/Resources.cs`——`list_mcp_resources` / `list_mcp_resource_templates` / `read_mcp_resource`（`isMcpAppResource` 过滤 `ui://` 与 `profile=mcp-app`；`listed` 剥离 `_meta`/`icons` 并前置 `server`；带 `server` 走一页 + `nextCursor`，不带则每服务器全量、按名字排序聚合、逐服务器记 `{server,error}`；多条 contents 前置 `uri:` 文本块；text/blob/image 三路还原）；顺带补 `Pi.Mcp` 的 `McpResourceContents.MimeType`（此前 image blob 无法还原成图片块，只能退化成 binary omitted 占位）；新登记 C182/C183 | 本轮提交 | `McpResourceToolsTests` 37/37；`Pi.Mcp.Tests` 35/35；`Pi.CodingAgent.Tests` 全类 **1607 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-14 | P4b-2（4d-9） | MCP OAuth 凭据层：`Extensions/Mcp/OAuth.cs`（816 行）——`McpOAuthCredentialStore`（`mcp-auth.json`、legacy 键接管、`withRefreshLock`）、`CreateAuthProvider`（30s 提前刷新 / 401 刷新 / `insufficient_scope` 重新登录 / 进程内共享一次刷新 / 跨进程刷新锁 / 令牌被替换即用不刷新 / `settled`）、`SignInServerAsync`（PKCE、CIMD、粘贴 redirect URL 竞速、端口回退）；顺带修正 `Pi.Mcp` 两处真实缺陷（`McpOAuthProvider.clientMetadataDocument` 固定值 → 元数据相关钩子；`OAuthTypes` 六记录补 `[JsonPropertyName]` snake_case，修 .NET/TS `mcp-auth.json` 互不识别）；新登记 C179–C181 | 本轮提交 | `McpOAuthTests` 37/37；`Pi.Mcp.Tests` 35/35；`Pi.CodingAgent.Tests` 全类 **1570 / 0 失败**；构建 0 警告 0 错误 |

| 2026-10-14 | 4f-2a | 主题层本体：`ThemeTokens`（56 令牌）/ `ThemeJson`（手写 schema 校验，替代 typebox）/ `SystemTheme`（ families + 对比曲线 + 规则拓扑序 + 三档降级）/ `Theme`（预计算 ANSI + `Colors` 按快照引用同一性缓存 + dim 抽屉）/ `ThemeRuntime`（全局注册表、自定义主题目录与文件监视、`COLORFGBG`/OSC 11/2031 终端外观探测、HTML 导出色）/ `ThemeHelpers`（26 scope + 65 扩展名 + 四个组件主题）；**先补 `Pi.Tui` 的 OSC 终端配色查询链**（OSC 10/11/4 + DA1 收尾，此前 `QueryTerminalBackgroundColorAsync` 从未收到过回复，是阻塞 `theme-controller` 的真实缺陷）；删掉 `Placeholders.cs` 的 `Theme` 占位类与 `ExtensionContexts` 里重复的 `ThemeInfo` / `SetThemeResult`；另修 `Pi.Tui.Tests.LoaderTests` 的一处 5 ms 定时器时序竞态（全量跑约三次挂一次） | 本轮提交 | `Pi.CodingAgent.Tests` **1889 / 0 失败**（3m50s，1708 + 181）；`Pi.Tui.Tests` 23854 / 0；构建 0 警告 0 错误 |

| 2026-10-14 | 4f-3a | 交互组件原语层：`Modes/Interactive/Components/` 新增 `KeybindingHints.cs`（`formatKeyText` / `keyText` / `keyDisplayText` / `keyHint` / `rawKeyHint`）、`VisualTruncate.cs`（`truncateToVisualLines` + 按宽度缓存的 `VisualLinePreview`）、`DynamicBorder.cs`、`ThemedText.cs`；`keybinding-hints` + `visual-truncate` 解 4f-1（renderers）的直接阻塞；新登记 C197（平台探测缝）/C198（`"start"|"end"` 联合退化）；**顺带修 `McpOAuthTests` 一处 flaky 断言**（P4b-2 遗留：同一条断言连读三次 `NowMs()`，跨毫秒边界 → 1000/999） | `07f698f`（已推送） | 四个新测试类 **35 / 0 失败**；`Pi.CodingAgent.Tests` 全量 **1924 / 0 失败**（17m13s，1889 + 35）；构建 0 警告 0 错误；变异套件 `keybinding-hints` / `visual-truncate` / `dynamic-border` / `themed-text` **18 处全捕获** |
| 2026-10-14 | 4f-3b | 词级差分与 diff 渲染器：`Core/Tools/JsDiff.cs`（653 → 1,095 行）补 jsdiff 8.0.4 `diffWords` 本体（`WordDiff` 的 tokenize 空白拼接遍 / 按 JS `\s` 裁边的 `equals` / 只留首 token 前导空白的 `join` / `dedupeWhitespaceInChangeObjects` 五分支的 `postProcess`），并给 `DiffBase` 补 `PostProcess` 钩子；`Modes/Interactive/Components/DiffRenderer.cs`（265 行）移植 `diff.ts` 的 `renderDiff` / `parseDiffLine` / `replaceTabs` / `renderIntraLineDiff`，正则的三处 JS/.NET 语义差异显式化；新登记 C199/C200/C201；**这解掉了 4f-1 的最后一块阻塞**（`renderers/edit.ts` 要 `renderDiff`） | `6821cda`（已推送） | 两条新差分语料（`diffwords-corpus.json` 86 例驱动上游 jsdiff UMD 包本体；`renderdiff-corpus.json` 83 例**直接执行上游 `diff.ts` 文件本体**、只改写两行 import）；三个新测试类 **202 / 0 失败**；另用随机化差分搜索确认纯净实现在 **15,000 组随机输入上与上游逐条一致**，并据此补进 18 条判别输入、判定 3 个锚点为等价变异；`Pi.CodingAgent.Tests` 全量 **2126 / 0 失败**（3m38s，1924 + 202）；`dotnet build Pi.slnx -m:1` 0 警告 0 错误；变异套件 `diffwords`（13 锚点）+ `diff-renderer`（16 锚点）**29 处全捕获** |
| 2026-10-15 | 4f-1（补记） | **八个工具 renderer 全套**（TS `core/tools/renderers/*` 9 文件 1,145 行 → C# 10 文件 2,242 行 + `Utils/Js.cs` 319 行）：`BashRenderers` / `EditRenderers`（含后台 preview）/ `FindRenderers` / `GrepRenderers` / `LsRenderers` / `ReadRenderers`（compact 分类与 pi-docs 识别）/ `WriteRenderers` / `BuiltInToolRenderers`（`index.ts` 注册表）+ `RenderUtils`（`render-utils.ts` 112 行全量）。语料 `tool-renderers-corpus.json`（11,242 行 / 104 case / 110 断言）由生成脚本驱动 **TS renderer 本体**产出（脚本按惯例未入库已丢失）；`ToolRenderersCorpusTests.cs` 798 行重放组件树。`Pi.Tui` 侧 `Box` 去 sealed + 状态只读访问器（语料观察用）、`Text`/`Spacer` 同理。**流程偏离**：提交 `a0caf71` 的信息是通用的 "Refactor code structure…"，未按仓库惯例写明内容、未随提交更新本文档——发现于 4f-3c 收口时，此处补记 | `a0caf71`（已推送） | 语料 110/0；全量见 4f-3c 行（本批修复前该套件 27 处失败，见下） |
| 2026-10-15 | 4f-3c | **消息类组件 11 个**（TS 855 行 → C# 1,150 行 + 测试 515 行）：`AssistantMessageComponent`（OSC 133 zone / thinking 合并与点击折叠 / transformer 链 / 流式语义）/ `UserMessageComponent`（transform 复用 + zone）/ `CustomMessageComponent`（自定义 renderer 优先 + 回退）/ `BranchSummary` / `CompactionSummary` / `SkillInvocation`（三个折叠消息 + MouseRegion）/ `StatusIndicator`（4 子类 + `IdleStatus` + 边框内渲染）/ `CountdownTimer`（`setInterval`→`Timer`）/ `AuthUrlComponent`（hyperlink + 复制提示）/ `PiLogo`（半块像素画 + Apple Terminal 回退）/ `MarkdownTransform`（扩展 transformer 链）。**前置补齐 `Pi.Tui` 三个真实缺口**：`Box` 补 `HandleMouse` + `mouseLayout` 命中缓存（TS `Box` 本体就有，此前遗漏）、`Box.Invalidate` 改 virtual（子类重建内容）、新增 `TerminalSession.IsAppleTerminalSession`；`JsNumber` 补 `ToLocaleString`。**本批修复上批（4f-1）遗留的 27 处语料失败**，根因四类：① 夹具未装载 `AppKeybindings` 全表进全局注册表（`keyText` 渲染空串）；② C# `BashRenderers` 臆造 `durationMs` 直算分支（TS 无此语义，`ToolRenderContext.DurationMs` 系臆造成员）——已删，夹具改为按语料反推出的生成器时钟规则 mock（call 时刻 + durationMs；partial 用挂钟）；③ 夹具对输入路径做包根重写但期望树未同步——补 `RewriteExpectedRoots`；④ TS 生成器把语料 `"none"` 字符串原样塞进 `capabilities.images`（out-of-type、truthy），C# 枚举映射为 falsy `None` 导致图片占位多渲染——夹具改用 truthy 代表值。另修 EditPreview 后台任务被并行测试的主题重置打断（fire-and-forget 死亡 → pending 卡死）：整个套件移入 `DisableParallelization` collection（vitest 文件隔离的 xUnit 等价物）。新登记 C202–C205 | 本轮提交 | 语料 110/0；`MessageComponentsTests` 20/20；`Pi.CodingAgent.Tests` 全量 **2256 / 0 失败**（1m15s，连续两轮）；`Pi.Tui.Tests` 23854 / 0；构建 0 警告 0 错误 |

| 2026-10-16 | **S0（R2-1）** | `outputPad` 回填到已收口组件：`BranchSummaryMessageComponent` / `CompactionSummaryMessageComponent` / `SkillInvocationMessageComponent` 构造函数新增 `outputPad`（默认 1）并传给 `Box` 的 `paddingX`，新增 `SetOutputPad` → `SetPaddingX`；`CustomMessageComponent` 默认渲染路径补 `_box.SetPaddingX(_outputPad)`。**动因**：参照检出已从 `428a12bc7` 漂移到 `eb326d26`（25 提交），漂移含 6 组真实特性（见第零节）。`Pi.Tui` 侧 `Box.SetPaddingX` / `Text.SetPaddingX` / `TuiAltScreen.ResetTextSelection` 经核实已具备，无需改动 | `42ed85c`（已推送） | `MessageComponentsTests` **22 / 0**（新增 2）；`Pi.CodingAgent.Tests` 全量 **2258 / 0 失败**（5m12s）；构建 0 警告 0 错误 |
| 2026-10-16 | **S1（R2-2）** | `durationMs` 全链路贯通（上游 `36a686ee8` + `f284a2460`）：**`Pi.Agent`** 在 `execute()` 周围用单调时钟计时（`ExecutePrepared` / `ExecutePreparedAsync` 返回 `ExecutedToolResult`），`AgentEvent.ToolExecutionEnd` 增加 `DurationMs`，`ToolResultMessage.DurationMs` 随之落地（被阻断的调用不执行 → 不记录耗时）；**`Pi.Ai`** `AssistantMessageEventStream` 自计时（`done`/`error`/`end` 三路，`timestamp` 早于流创建时刻或已有 `DurationMs` 则跳过），`EventStream` 暴露 `IsEnded`，`LazyStream.SetupErrorMessage` 改用**流创建时刻**而非失败时刻；**`Pi.CodingAgent`** 加回 `ToolRenderContext.DurationMs`（4f-1 曾按旧基线判为「臆造」删除，上游已真加）、`AgentSessionEvent.ToolExecutionEndEvent.DurationMs` 并接线两处构造点、`BashRenderers` 恢复「记录时长优先于渲染器时钟」分支、`ToolHtmlRenderer` 补 `DurationMs = null`；**语料夹具修正**：`ToolRenderersCorpusTests` 把 `step.DurationMs` 真正传进 `ToolRenderContext`（此前只用于推进 mock 时钟，导致 9 条语料从未覆盖该分支） | 本批提交 | `Pi.Agent.Tests` **33 / 0**；`Pi.Ai.Tests` **307 / 0**（新增 6）；`BashRenderersDurationTests` 新增 4；`ToolRenderersCorpusTests` 110/0（夹具修正后仍全绿，验证「时钟推演值 == 记录值」）；`Pi.CodingAgent.Tests` 全量 **2262 / 0 失败**（4m54s）；构建 0 警告 0 错误。新登记 **C207–C209** |

| 2026-10-16 | **S2a（R2-3）** | `--tools` 修饰符（上游 `ddaa0a034`）：**`SettingsManager.GetToolListError` 加回**（4f-3 前曾以「TS 没有这个校验」删除，上游真加了）——校验「纯名称/模式 allowlist」与「只含 `+name`/`-name` 精确名」不可混用、修饰符不接受 `*` 模式；`ApplyToolModifiers` 文档改为对齐 TS 同名导出；**`Sdk.ResolveSessionTools` 恢复上游语义**：`tools` 全为修饰符时不再是 allowlist，而是叠加在 `defaultTools` 之上的选择变更（`selectedToolNames`），混用则抛 `Invalid argument: Invalid tools option: …`，`ResolvedSessionTools` 新增 `DefaultToolModifiers`；`AgentSessionConfig.DefaultToolModifiers` + `AgentSessionBuild._defaultToolModifiers` 落地；`AgentSession.ResolveCurrentDefaultTools` 抽出（对应 TS reload 的 `getDefaultTools()` 闭包），`ResolvePreviousDefaultTools` / `ResolveAddedDefaultTools` 增可选修饰符形参；`SdkTests` 的旧断言「tools 是纯 allowlist」随漂移作废并改写为 6 条上游语义用例 | 本批提交 | `SdkTests` + `AgentSessionExtensionTests` + `SdkCreateAgentSessionTests` **76 / 0**（首轮）；改写后 `SdkTests` + `AgentSessionExtensionTests` **64 / 0**；`Pi.CodingAgent.Tests` 全量 **2270 / 0 失败**（4m00s）；构建 0 警告 0 错误。新登记 **C210** |
| 2026-10-16 | **S2b（R2-3 收口）** | `AgentSession.ReloadAsync` 编排本体（TS `agent-session.ts:3659-3698`，约 40 行）——**既有缺口（非 R2 引入）**，本批落地。逐条对齐 TS：① 先抄旧 runner 的 `flagValues`，再对它发 `session_shutdown(reason="reload")`（**不受 `hasBindings` 门控**）并 `invalidate()` 使其立刻失效；② `previousDefaultTools` 在 `SettingsManager.ReloadAsync()` **之前**取快照，刷新后算出 `addedDefaultTools`（只加新名，被移除的保持活跃，会话中手动关掉的也不会被重新激活）；③ `SyncQueueModesFromSettings()` + `ApiRegistry.ResetApiProviders()` + `ResourceLoader.ReloadAsync()`；④ 重建前把当前活跃工具全部塞进 `_pendingToolNames`（新扩展稍后注册的 MCP 工具在注册时再激活）；⑤ `BuildRuntime` 用 `[..getActiveToolNames(), ..addedDefaultTools]` + 旧 `flagValues` + `includeAllExtensionTools: true`；⑥ 仅当四个扩展绑定任一存在时才跑 `beforeSessionStart` → `session_start(reason="reload")` → `ReportUnhandledMcpServers()` → `ExtendResourcesFromExtensions("reload")`。新增 `ReloadOptions` 记录（`BeforeSessionStart` 回调）；复用既有决策叶子 `ResolveCurrentDefaultTools` / `ResolvePreviousDefaultTools` / `ResolveAddedDefaultTools` / `HasExtensionBindings`。**宿主侧 `ctx.reload()` 的接线延后至 4f-4**（`interactive-mode.ts` 未移植） | `e657dac`（已推送） | 新增 `AgentSessionReloadTests` **10 / 0**（含：无绑定仍发 shutdown 但不发 start、`shutdown → before → start` 时序、旧 runner 失效、`flagValues` 搬运、只激活新增默认工具、`usesDefaultTools=false` 不加工具、修饰符重放、二次 reload 绑定仍在）；`Pi.CodingAgent.Tests` 全量 **2280 / 0 失败**（17m51s）；构建 0 警告 0 错误。新登记 **C211–C213** |

| 2026-10-16 | **S3a（R2-4）** | `packages/ai` provider 层漂移三条：**`retry`**（`8b5708dbb`）增加 `overloaded` / `server_busy` / `servers are currently busy` / `currently experiencing high demand`；**`radius`**（`43d376399`）网关目录由「叠加」改「**替换**」随包基线目录（`_dynamic` 由 `[]` 改可空，新增纯决策叶子 `ResolveModels(baseline, dynamic) => dynamic ?? baseline`，凭据恢复路径保留 `Count > 0` 守卫、网络路径无条件覆盖含空目录）；**`bedrock`**（`2989eb581`，#10142）给 OpenAI 系模型发 reasoning effort（`gpt-oss` → 扁平 `reasoning_effort` 且 xhigh/max 夹到 high；`gpt-` → 嵌套 `reasoning.effort` 且 `thinkingLevelMap` 优先；**gpt-oss 必须先判**因为同时命中 `gpt-`），Claude 分支抽成 `BuildClaudeAdditionalModelRequestFields`。**顺带修掉的既有缺陷（非漂移）**：`BuildCommandInput` 原先把未设字段写成 `"k":null`，而 TS 的 `JSON.stringify` 会丢 `undefined` 键（用最小 .NET 程序实测确认），四路改为「非 null 才写键」 | `2c74e2c`（已推送） | `Pi.Ai.Tests` **310 / 0**（新增 3：`BuildAdditionalModelRequestFieldsOpenAiReasoning` / `BuildCommandInputOmitsUnsetFieldsInsteadOfEmittingNull` / `RadiusResolveModelsReplacesInsteadOfOverlaying`；并改写 `BedrockTests` 里「非 Claude → null」的样本模型，因为 `openai.gpt-5.6` 现在**有** OpenAI 分支了）；`Pi.CodingAgent.Tests` 全量 **2280 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-16 | **S3b（R2-5）** | `faux` 逐消息 prompt cache（`fe11328b0`）。核对时发现 C# 的 `Faux` 只移植了半个 provider——**整个用量估算子系统缺失**，本批补齐：新增 `EstimateTokens`（固定 `/4`，与 `Utils/Estimate` 的 3.5 **无关**）、`ContentToText` / `BlockToText` / `AssistantContentToText` / `ToolResultToText` / `MessageToText` / `SystemMessageToText` / `RoleOf` / `JsonStringify`、`JoinedLength` / `CommonPrefixLength` / `CommonPromptPrefixLength`、`WithUsageEstimate`（按 `sessionId` 键控缓存：首次全额写、命中时 `cacheRead=ceil(cached/4)` / `cacheWrite=ceil((len-cached)/4)` / `input=max(0,promptTokens-cacheRead)`，`cacheRetention:"none"` 读写双禁且不落缓存）；`Faux.Response` 去掉 `Usage` 形参（C214）；`FauxCore` 自持 `_promptCache` 并新增 `Provider`（C215）；`Replay` 形参补 `modelId`/`api`/`provider`。**顺带修掉两处既有缺陷（非漂移）**：① `Replay` 身份字段原先写死 `"faux-1"/"faux"/"faux"`，而 TS 走 `cloneMessage(resolved, api, provider, requestModel.id)` 取自请求上下文（`FauxCore.Api` 是随机 id、`CreateModel` 允许自定义 id，写死会让消息的 api/model 与请求模型对不上）；② `error`/`aborted` 终态原先多发一个 `done`，TS 只发 `error` 并 return。**另**：逐条比对 retry 模式表发现漏抄 `"pending stream has been canceled"`（`5b6c792b4`，**早于漂移基线**，属首移植漏抄），已补齐 | `a633cb2` + `97eae94`（已推送） | 新增 `FauxUsageEstimateTests` **16 / 0**（期望值全部按 TS 源码手算；另把 TS 纯函数逐行抄进独立 Node 脚本跑 11 组输入做**差分验证**，输出与手算值逐条一致）；`Pi.Ai.Tests` 全量 **326 / 0**（310 + 16）；`Pi.Agent.Tests` **33 / 0**；`Pi.CodingAgent.Tests` 全量 **2280 / 0 失败**（16m41s）；构建 0 警告 0 错误。新登记 **C214–C215**，并新开 **S3c** 阶段记录 faux 未移植面 |

| 2026-10-16 | **S4（R2-6 收口）** | codemode 输出条目分离（`eb326d265`）。**关键发现：该提交横跨 `packages/codemode` 与 `packages/coding-agent` 两个包，端口此前只移植了 coding-agent 半边**——4d-5 已带 `execute.ts` 的 `formatOutput` / `joinAdjacentText`、`types.ts` 的 `console?: true` 与 `tool.ts` 的 describeGlobals 文案，但 `prelude-source.ts` 的 `console.*` 仍以 `output("text", …)` 上报。而 kind→条目的映射是**注入引擎的职责**（对应 TS `worker.ts`，端口不内置 JS 引擎），引擎按 `"text"` 构造出的条目 `Console` 恒为 `null`，于是 `FormatOutput` 的 `<console_output>` 分组**永远不会触发**、console 行混进正文。本批：`PreludeSource` 的 `output("text", …)` → `output("console", …)` + 类文档与 bridge kind 列表同步；`Engine.cs` 的「实现方职责」补 kind→条目映射规则；`Host`/`Protocol` 无需改（`Output` 直接承载结构化条目）。**另补一大块缺失覆盖**：`executeCodemode` 的编排在 C# 侧此前**完全没有测试**（`CodemodeCorpusTests` 注释所称的「脚本化引擎集成测试」并不存在），新增 `CodemodeOutputLayoutTests` 5 例锁死版式；`SandboxTests` 加正/反向断言（prelude 里必须是 `output("console", …)`，旧的 `output("text", …)` 必须不存在） | `3cd141d`（已推送） | `CodemodeOutputLayoutTests` **5 / 0**（新建）；`Pi.Codemode.Tests` **40 / 0**（39 + 1）；`Pi.CodingAgent.Tests` 全量 **2285 / 0 失败**（12m49s）；构建 0 警告 0 错误。无新增差异编号 |

| 2026-10-16 | **S3c（faux 补全）** | `packages/ai/src/providers/faux.ts`（710 行）剩余面**逐行覆盖**：`FauxProviderOptions`（消解 C215：`Api`/`Provider`/`Models`/`Deferred`/`TokensPerSecond`/`TokenSize`，位置参数构造保留为端口兼容重载）；`FauxResponseFactory` + `FactoryStep` + `FauxProviderState.CallCount`；`tokensPerSecond`/`scheduleChunk`（`ceil(len/4)/tps*1000`）；`tokenSize`/`splitStringByTokenSize`（含 `min` 夹到 1 与 max、空串仍产出一块）；`deferred` 全链（句柄身份三元组校验、`pendingFetches` 递减、取消登记句柄快照、提交时剥离 `deferred`/`signal`/`onResponse`）；`Faux.CreateProvider()`（= `fauxProvider()`，经 `ProviderFactory` + 恒成功的 `apiKey.resolve`）；`aborted` 中断（预中止无 `start`、流中中止保留已累计内容）；`thinking_start`/`thinking_end`/`toolcall_start`/`toolcall_end` 四个事件（`*_end` 带整块原文、`toolcall` 参数以 JSON 增量流出后落定真实对象）；自定义模型目录 + `getModel(id)` 重载；`ScriptedMessage`/`NormalizeFauxAssistantContent`/`DefaultUsage`。**顺带修掉 5 处既有缺陷**：脚本耗尽文案（自造 → TS 原文案）、取消令牌被交给 `Task.Run` 导致流永不收尾、`ToolCallDelta.Index` 填工具调用序号（应填内容块下标）、工具调用默认 id 格式、默认 `BaseUrl`。新登记 **C216–C220** | `413718a` | 新增 `FauxProviderTests` **33 / 0**；`Pi.Ai.Tests` **359 / 0**（326 + 33）；`Pi.Agent.Tests` **33 / 0**；`Pi.CodingAgent.Tests` 全量 **2285 / 0 失败**；构建 0 警告 0 错误 |

| 2026-10-16 | **4f-3d Batch C** | 选择器组第一子批 5 个（TS 560 行 → C# 625 行 + 测试 709 行）：`ThemeSelectorComponent`（10 行可视列表 + `(current)` 描述 + 实时预览）、`ShowImagesSelectorComponent`（yes/no 按当前值预选）、`TrustSelectorComponent`（`formatDecision` 的 `inherited from` 分支 + `✓`/`→` 双标记 + `j`/`k` 等价方向键 + **两端钳制不环绕**）、`ThinkingSelectorComponent`（七档描述 + `Input` 搜索框 + `Fuzzy.Filter` 重建列表 + `app.thinking.save` 优先于导航键、无回调时穿透）、`UserMessageSelectorComponent`（10 行可视列表 + 滚动指示 + 上下环绕 + `initialSelectedId` 未命中回落最新 + 空列表 100 ms 自动取消）。**逐行核对** TS 源；`trust-selector.test.ts` 4 例与 `thinking-selector.test.ts` 2 例全部转写（路径类断言改为按临时目录推导，因 Windows 下 `normalizeCwd` 会把 `/project` 解析成当前盘符下的路径）。新登记 **C221–C224**（默认接口成员 `HandleInput` 的隐式实现、`as ThinkingLevel` → `ThinkingLevelText.Parse`、`setTimeout` → `Task.Delay`、`SelectedIndex` 测试缝） | `1cb2dae` | 新建 `SelectorComponentsTests` **42 / 0**（5 个类：5 + 5 + 9 + 12 + 11）；`Pi.CodingAgent.Tests` 全量 **2327 / 0 失败**（38s）；构建 0 警告 0 错误 |

| 2026-10-16 | **4f-3d Batch D** | 选择器组第二子批 4 个（TS 473 行 → C# 653 行 + 测试 700 行），全部是扩展面向的对话框、只依赖 4f-3a/4f-3c 已收口的原语：`ExtensionSelectorComponent`（标题 + 选项列表 + 提示行；`app.tools.expand` 优先于导航键；`timeout` 挂 `CountdownTimer` 每秒改写标题、归零取消）、`ExtensionInputComponent`（单行输入 + `initialValue` 预填 + `Focusable` 转发；TS 的 `_placeholder` 形参从不读取，端口保留并显式丢弃）、`RadiusLoginMenuComponent`（选中行换成 `radiusShimmer`：四色 16 字符循环、10 字符/秒、smoothstep + `srgb` 混色、50 ms 定时 `requestRender`；`createLoginMenuSelector` 工厂）、`FirstTimeSetupComponent`（两步向导 theme → analytics；`Invalidate` 整树重建；主题步每次移动都预览、分析步不预览）。**逐行核对** TS 源。新登记 **C225–C229**（`HandleInput` 改 `virtual` 以替代 TS 的构造函数回调包装、`RadiusShimmer` 测试缝、`setInterval`→`Timer` + `unref` 无对应物 + `_animating` 用 `volatile`、未导出结构类型具名化、四个只读测试缝） | `09076b4` | 新建 `ExtensionComponentsTests` **39 / 0**（4 个类：12 + 9 + 10 + 8，含 `FakeTui` 桩）；时序类用例连跑三轮全绿；`Pi.CodingAgent.Tests` 全量 **2366 / 0 失败**（38s）；构建 0 警告 0 错误 |

| 2026-10-16 | **4f-3d Batch E-1** | 选择器组第三子批首件（纯查询层，无组件、无渲染）：`session-selector-search.ts`（194 行 → `Components/SessionSelectorSearch.cs` 355 行 + 测试 356 行）。四个类型（`SortMode` / `NameFilter` / `SearchQueryMode` / `SearchTokenKind` 枚举化 + `ParsedSearchQuery` / `MatchResult`）+ 七个函数（`hasSessionName` / `parseSearchQuery` / `matchSession` / `filterAndSortSessions` 公开，`normalizeWhitespaceLower` / `getSessionSearchText` / `matchesNameFilter` 私有）。钉住的语义：引号短语两侧都过 `normalizeWhitespaceLower` 再 `indexOf`（`"node cve"` 命中 `node\n\n   cve`）；引号不闭合 → 整体退回空白分词且**多余引号留在首个 token 里**；`re:` 空模式与编译失败都走 `error`（`matchSession` 视为不匹配、`filterAndSortSessions` 返回空）；评分 = 正则/短语按命中位置 `×0.1` + fuzzy 用 `Fuzzy.Match`，**所有 token 必须命中**；`recent` 只过滤保持入参序，`threaded` 与 `relevance` **共用**评分分支（上游只特判 `recent`），平局按 `modified` 降序；名字过滤**先于**查询短路。新登记 **C230**（`re:` 用户正则直接交给 .NET `Regex`（`IgnoreCase \| CultureInvariant`），不模拟 JS 正则方言；`error` 文案取 .NET 的）。另记不编号的形状差异：`FilterAndSortSessions` **总是返回新列表**（TS 在空查询时交回入参数组本身）、排序改用 LINQ `OrderBy/ThenByDescending`（C# `List.Sort` 是 introsort **不稳定**，会漂平局序） | `e4cd50b` | 新建 `SessionSelectorSearchTests` **27 / 0**（TS 9 例全部转写 + 18 项补解析层）；单跑 400ms；`Pi.CodingAgent.Tests` 全量 **2393 / 0 失败**（37s）；构建 0 警告 0 错误 |

| 2026-10-16 | **4f-3d Batch E-2** | 选择器组第三子批第二件：`login-dialog.ts`（238 行 → `Components/LoginDialog.cs` 310 行 + 测试 391 行）。OAuth 登录期间顶替编辑器的面板：上/下 `DynamicBorder` + 标题 + 动态内容区 + 常驻 `Input`；八个步骤方法 `ShowAuth` / `ShowDeviceCode` / `ShowManualInputAsync` / `ShowPromptAsync` / `ShowDetails` / `ShowInfo` / `ShowWaiting` / `ShowProgress`；`HandleInput` 三路优先级（`tui.select.cancel` → 取消、`app.message.copy` 仅在有 URL 时 → 复制、其余转给 `Input`）。钉住的语义：`ShowPrompt` **不清空**内容（上游 5433 回归的修复点），`ShowAuth`/`ShowDeviceCode`/`ShowDetails` 会清且后两者丢掉复制目标；提交后把 `Input` 子节点换成 `> {value}` 的 `Text`（故同一 `Input` 复用不会串值）；取消 = abort + reject 挂起 prompt + `onComplete(false, "Login cancelled")`。新登记 **C231**（`AbortController`→`CancellationTokenSource`、两个 resolver/rejecter 字段折成一个 `TaskCompletionSource<string>`、`Error("Login cancelled")` → `OperationCanceledException`）与 **C232**（`Utils/OpenBrowser.cs` 新增 `OpenOverride` 测试缝，对应 TS 的 `vi.mock`）。另记不编号：TS 里 OSC-8 有 ST/BEL 两种终止符写法，本文件三处内联用 BEL，端口抽成 `BelHyperlink` 并用原始渲染逐字节钉住 | `9939241` | 新建 `LoginDialogTests` **17 / 0**（TS 的 5 例回归全部转写 + 12 项补）；连跑三轮全绿；`Pi.CodingAgent.Tests` 全量 **2410 / 0 失败**（44s）；构建 0 警告 0 错误 |

| 2026-10-16 | **4f-3d Batch E-3** | 选择器组第三子批第三件：`settings-submenu.ts`（258 行 → `Components/SettingsSubmenu.cs` 387 行 + 测试 412 行）。两个类：`SelectSubmenu`（标题 + 说明 + 可选 `Input` 搜索框 + `SelectList` + 提示行；`ApplyFilter` 用 `Fuzzy.Filter` 重建列表并**原地替换子节点** `Children[listChildIndex] = newList`）与 `SteppedSubmenu`（N 步向导：`context` 累积、Enter 前进、Esc 非首步 `Remove(step.Key)` 后退、首步取消、末步 `onComplete({...context})` 后按 `loop` 回到第 0 步或 `onCancel()`）。钉住的语义：过滤后 `preselect` 传 `""` 故选择回到第 0 行；`stepLabel` 只在 `total > 1` 时出现且带尾随 `· `；`step.searchable \|\| step.layout` 两者皆假时传 `null`（走默认 12/32 列宽）。**同批修掉 C233**：`Container` / `Text` / `Box` 三个基类各补 `public virtual void HandleInput`，9 个派生组件（`SelectSubmenu` / `SteppedSubmenu` / `ExtensionInputComponent` / `FirstTimeSetupComponent` / `LoginDialogComponent` / `ThinkingSelectorComponent` / `TrustSelectorComponent` / `ExtensionSelectorComponent` / `CancellableLoader`）改 `override`；并给 `TuiComponents.HasHandleInput` 加 no-op 排除表。新登记 **C234**（`string \| ((ctx) => string)` 联合 → `readonly record struct SubmenuText`，lambda 须写 `FromFactory`）与 **C235**（`UnicodeWidth` 进程级缓存的跨线程淘汰竞态 → 加 `lock` + 空队列守卫） | `f2e1fca` | 新建 `SettingsSubmenuTests` **26 / 0**（`SelectSubmenuTests` 14 + `SteppedSubmenuTests` 12；**上游无该模块测试**，全部照源码行为自定观测点）；`Pi.CodingAgent.Tests` 全量 **2436 / 0 失败**（42s）；`Pi.Tui.Tests` 全量 **23854 / 0 失败** 连跑三轮；构建 0 警告 0 错误 |
| 2026-10-16 | **4f-3d Batch E-4** | 选择器组第三子批第四件：`oauth-selector.ts`（224 行 → `Components/OAuthSelector.cs` 360 行 + 测试 639 行）。`OAuthSelectorComponent : Container, IFocusable`（上下 `DynamicBorder` + 标题 + `Input` 搜索框 + 8 行窗口的 provider 列表 + `(sel/total)` 滚动信息）；两个纯函数 `FormatAuthSelectorProviderType` / `FormatAuthSelectorProviderStatus`。钉住的语义：状态后缀三分支（缺 status → ` • not configured`；`status.type != authType` → warning 色 `{对方类型} configured`；一致且 source 是 `OAuth`/`stored credential`/空 → ` ✓ configured`）；env-var 名单（`/^[A-Z][A-Z0-9_]*(?:, …)*$/`）才加 `env: ` 前缀；`authType` 标签只在 provider 集合混类型时出现；`subscription === false` 才是 `account`；模糊 haystack 是 `name id authType method?.name` 且 `authType` 用线上拼写 `oauth`/`api_key`；窗口 8 行 + 溢出才显滚动信息；过滤后 `selectedIndex` 钳回；三种空列表文案；上下键在空列表上直接 return。新登记 **C236**（`authType` 复用 `CredentialKind`；`method?: ApiKeyAuth \| OAuthAuth` → 具名联合 `AuthSelectorMethod`，**无隐式转换** —— CS0552 禁止以接口为用户定义转换的源）与 **C237**（`mode: "login" \| "logout"` → `enum AuthSelectorMode`）。另记不编号：`${provider.authType}` 的线上拼写进了搜索 haystack；正则用 `\A…\z` 而非 `$`（.NET 的 `$` 会匹配结尾换行前）；TS 的 `if (!provider) continue;` 在 C# 不可能为空故省略；三个只读测试缝。**上游 6 例中 5 例转写，第 1 例（`InteractiveMode.getLoginProviderOptions`）留给 4f-4** | `07e9e78` | 新建 `OAuthSelectorTests` **44 / 0**（30 个 `[Fact]` + 14 个 `[Theory]` 用例）；单跑 546ms；`Pi.CodingAgent.Tests` 全量 **2480 / 0 失败**（42s）；构建 0 警告 0 错误
| 2026-10-16 | **4f-3d Batch E-5** | 选择器组第三子批第五件（非选择器，是所有面板/输入流共用的宿主组件）：`custom-editor.ts`（148 行 → `Components/CustomEditor.cs` 220 行 + 测试 428 行）。`CustomEditor : Editor` + `CustomEditorOptions : EditorOptions`（TS 的 `EditorOptions & { embedWorkingStatus? }` 交叉类型）；`renderTopBorder` 三段回退（零宽状态 → 溢出标签放不下时塌成纯转轮**再重算一次** `canFitOverflow`（闭包读被重新赋值的 `statusWidth`）→ 前缀截断）；`handleInput` 七层优先级（扩展快捷键 truthy 判定 → pasteImage → interrupt **仅当自动完成未激活** → exit **仅当文本为空**且 `onCtrlD ?? actionHandlers.get(...)` 只跑一个 → history 交还基类 → 其余应用动作按 `Map` 插入序 → 兜底基类）。上游 5 例中 4 例转写。新登记 **C238**（`EditorOptions` 去掉 `sealed`：C# 无交叉类型，且与 C233 同链——接口映射建在「列出 `IComponent` 的类」上，子类必须 `override` 才进映射，`Tui` 正是经接口引用驱动焦点组件）。另记不编号：`actionHandlers` 用 `Dictionary(Ordinal)` 复现 `Map` 插入序（**仅无删除时**）。踩坑 7 条（5 条是测试自身写错）：`SetText` 把光标放行尾使 delete-char-forward 成 no-op；空格不触发自动完成而 T25 又晚一个微任务；`Editor` 自己不画 `> ` 提示符；上游 `render` 无 `width <= 0` 守卫故宽度 0 渲染三条线是忠于 TS；溢出回退宽度要手算；带 timer 的指示器必须 `try/finally` dispose 否则夹具拆主题后抛未处理异常 | `9665bc6` | 新建 `CustomEditorTests` **22 / 0**（上游 5 例转写 + 输入优先级 9 项 + 边框布局 8 项）；单跑 3s；`Pi.CodingAgent.Tests` 全量 **2502 / 0 失败**（40s）；`Pi.Tui.Tests` 全量 **23854 / 0 失败** 连跑两轮（动了共享基类 `Editor`）；构建 0 警告 0 错误
| 2026-10-16 | **4f-3d Batch E-6** | 选择器组第三子批第六件：`scoped-models-selector.ts`（401 行 → `Components/ScopedModelsSelector.cs` 520 行 + `Interactive/ModelSearch.cs` 42 行 + 测试 469 行）。模型配置面板（Ctrl+P 循环用的启用/禁用，会话内有效、Ctrl+S 才落盘）：`ModelsConfig`/`ModelsCallbacks` 两个记录 + `EnabledIds`（`null` = 全开，显式列表 = 有序名单，两者在页脚可观测地区分）代数七个纯函数（`isEnabled`/`normalizeEnabled`/`toggle`/`enableAll`/`clearAll`/`move`/`getSortedIds`）+ `ScopedModelsSelectorComponent`。钉住的语义：启用 id 排在其余之前（含已不在目录里的）；`normalizeEnabled` 在显式列表覆盖全目录时塌回 `null`；页脚分母是**目录规模**且 `· N unavailable` 只在 N > 0 时出现；搜索时 `enableAll`/`clearAll` 只作用于过滤集；`toggleProvider` 按 provider 全开则清、否则补；reorder 只在显式列表内、越界与未启用都是 no-op 且不通知；Ctrl+C 先清搜索再取消；`updateModels` 按 fullId 找回选中行。上游 4 例全部转写。新登记不编号的三处：`void | Promise<void>` 回调 → `Action`（组件从不 await）；`enabledModelIds?: string[] | null` 三态用**两个重载**承载（C# 分不出「省略实参」与「显式传 null」）；`setRefreshStatus` 的 `"muted" | "success" | "warning"` → `enum StatusKind` + 显式映射 | `3870e58` | 新建 `ScopedModelsSelectorTests` **24 / 0**（上游 4 例全部转写 + 20 项补）；单跑 461ms；`Pi.CodingAgent.Tests` 全量 **2526 / 0 失败**（42s）；构建 0 警告 0 错误

| 2026-10-16 | **4f-3d Batch F-1** | 第四子批首件，三文件打包（TS 608 行 → C# 1,041 行 + 测试 837 行）：`external-editor.ts`（46）→ `Modes/Interactive/ExternalEditor.cs`（私有临时目录 prompt.md 往返、win32 shell 包装、异步子进程、StripBom + 单尾随换行裁剪、`SpawnOverride` 缝）、`model-catalog-refresh.ts`（55）→ `ModelCatalogRefresh.cs`（ConditionalWeakTable 承载 WeakMap、waiters 计数、最后一人中止共享刷新、`RefreshOverride` 缝承载 vi.spyOn）、`extension-editor.ts`（141）→ `Components/ExtensionEditor.cs`（注入 app 键位管理器解析 app.editor.external、VISUAL/EDITOR 三级回退）、`model-selector.ts`（421）→ `Components/ModelSelector.cs`（快照即渲染 + 后台刷新三路文案、current/default 优先的稳定排序、all/scoped Tab 切换、fuzzy + "default" 置顶去重、10 行可视窗口）。**动工前基线核对发现参照检出已退回 `428a12bc7`、`eb326d26` 不可达**（见第零节 0.5 追记）；本批三文件未被漂移触及，按当前检出移植，S0–S4 成果保留。新登记 **C239–C241**。**flake 记录**：首轮全量 2 用例超时（等待条件写死成功文案），改等「刷新中」占位消失后连跑两轮无复发 | `3de7e0a` | 新增 18 项（ExternalEditorTests 7 / ExtensionEditorTests 6 / ModelSelectorTests 5）；`Pi.CodingAgent.Tests` 全量 **2544 / 0 失败**（两轮）；构建 0 警告 0 错误 |
| 2026-10-16 | **4f-3d Batch F-2** | `settings-selector.ts`（993 行）→ `Components/SettingsSelector.cs`（约 1,180 行 + 测试 16 例）：`SettingsConfig`（30 字段）/ `SettingsCallbacks`（32 回调）具名化；主列表 23 基础项 + 11 插入项（supportsImages 三段 splice + 9 项 findIndex 链逐条复刻）；`onChange` 26 路 switch 逐条对齐；`WarningSettingsSubmenu`（JsonObject 承载 WarningSettings、缺键回退 true）与 `ThemeSubmenu`（single/automatic 双模式状态机、cancel 恢复原值预览）；`model-thinking` 两步 `SteppedSubmenu`（可搜索模型选择器 current→default→provider 排序、档位选择 + clear override 尾项、`Loop=true`、change/remove 双路完成回调）。**动工前漂移核对（重新执行，见第零节约定）**：参照检出实测又回到 `eb326d26`，`git diff 428a12bc7..eb326d26` 逐文件核对——4 个剩余选择器中仅 `settings-selector.ts` 有 1 行描述文案改动（按当前检出移植），`config-selector`/`session-selector`/`tree-selector` 未被触及。新登记 **C242–C244** | `259b541`（已推送） | 新增 `SettingsSelectorTests` **16 / 0**（上游 4 例全部转写 + 12 项补）；`Pi.CodingAgent.Tests` 全量 **2560 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-16 | **4f-3d Batch F-3** | `config-selector.ts`（951 行）→ `Components/ConfigSelector.cs`（约 1,190 行 + 测试 14 例）：`ScopedResolvedResources`（**C245**）；`buildGroups` 全量（groupKey/subgroup 聚合、displayName 四规则、三段稳定排序）；`ConfigSelectorHeader`（双态标题 + 键位提示两行渲染）；`ResourceList`（三态扁平列表 + 继承启用快照 + 级联搜索 + global 取反与 project inherit→unload→load 三态状态机全链：top-level ±pattern 重写、包对象/字符串互转、空过滤对象清理、`autoload:false` 删包、继承文件绝对路径命名、builtin 不加裸路径）；`ConfigSelectorComponent` 装配与 Tab 切换。**自纠一处 TS↔C# 边界差异**：空列表 space/confirm 时 TS 靠 `undefined` + 可选链 no-op，C# 直接越界抛异常 → 补索引守卫。测试认知修正：`resolvedPaths.project` 由 `resolvePackageSources` 生成，**含继承的全局资源（scope=user）** | `2e61425`（已推送） | 新增 `ConfigSelectorTests` **14 / 0**（上游无该组件测试，照源码行为自定观测点）；`Pi.CodingAgent.Tests` 全量 **2574 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-3d Batch F-4** | `session-selector.ts`（1045 行）→ `Components/SessionSelector.cs`（约 1,290 行 + 测试 22 例）：`formatSessionDate` 八档分桶（Math.floor 全保留）；`buildSessionTree`/`flattenSessionTree`（父在场聚合、latestActivity 稳定降序、ancestorContinues 与 `├─/└─/│` 前缀复刻）；`SessionSelectorHeader` 全状态机（loading 进度、状态消息自动隐藏、删除确认 hint）；`SessionList`（树形/平铺双过滤、选中恢复、四色行渲染、删除确认全拦截键盘、deleteNoninvasive 查询非空转发搜索）；`deleteSessionFile`（trash CLI → unlink 回退 + 错误提示拼接）；`SessionSelectorComponent`（双 loader 异步加载 isActive 门控、重命名模式、作用域切换惰性加载）。测试认知修正：ShortenPath 保留原始分隔符、渲染行以搜索框+空行开头（会话行 index 2 起）、显示 name ?? firstMessage 而非 basename | `e894a0b`（已推送） | 新增 `SessionSelectorTests` **22 / 0**（上游无该组件测试，照源码行为自定观测点）；`Pi.CodingAgent.Tests` 全量 **2596 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-3d Batch F-5** | `tree-selector.ts`（1435 行）→ `Components/TreeSelector.cs`（约 1,790 行 + 测试 18 例）：`RenderHorizontalViewport`（树槽恒可见、按选中行 anchor 水平裁剪正文、四常量两档 `floor(viewport/3\|4)`）；`TreeList`（`flattenTree` 迭代后序活跃分支优先 + toolCallMap 提取、`applyFilter` 六档过滤 + 逐 token 搜索 + 无文本 assistant 折叠、`recalculateVisualStructure` 可见父子映射 + 折叠传播、键盘十九路：fold/unfold「可折叠则折叠否则段跳」双语义）；`SearchLine`/`TreeHelp`（chunk-aware 换行）+ `FormatHelpKeys`/`CompactRawKeys`；`LabelInput`（空值=清除）；`TreeSelectorComponent`（空树 100ms 自动 cancel、标签面板与树列表互斥切换）。**提交前自查修掉**：`Types.StopReason` 五处、空 id 可空性、**`FormatHelpKeys` 词边界缺失**（裸 `Replace` 会把 `pgup` 误伤成 `pg↑`，TS `\b` 不会——改 `Regex.Replace` + `\b`）、`SessionSelector` 局部类改名 `SessionInfoTreeNode` 消解与 Core 层 `SessionTreeNode` 的同名冲突、`UpdateNodeLabel` 用 `with` + 可变 setter 等价 TS 原地改。上游 `test/tree-selector.test.ts` **14 例全部转写**（选择器组唯一有上游测试的组件） | `9da5415`（已推送） | 新增 `TreeSelectorTests` **18 / 0**；`SessionSelectorTests` 22 / 0（改名后回归）；`Pi.CodingAgent.Tests` 全量 **2614 / 0 失败**（新增 18）；构建 0 警告 0 错误 |

| 2026-10-17 | **4f-3e Batch 1** | 三个自包含小组件（TS 190 行 → C# 3 个文件 + 测试 10 例）：`BorderedLoader`（cancellable → `CancellableLoader` Signal/OnAbort 转发 + C233 override；非 cancellable 自持 CTS；`DisposeLoader` 复刻 dispose/stop 探测链）、`CustomEntryComponent`（renderer 异常 → `[customType] renderer failed: …` 错误 Box；SetExpanded/SetOutputPad/Invalidate 三触发点重建；输出恒前置 Spacer）、`EarendilAnnouncementComponent`（标题 + 博客链接 + 可选内联图，资产缺失静默跳过）。`components/index.ts`（37 行）是纯 re-export barrel，C# 无对应物，不移植。**mermaid 阻塞**：`grok-mermaid@0.2.3` + `marked@18` 不在参照检出（node_modules 空），上游测试向量无法差分验证 → 延后，待依赖可得后按注入缝方案移植 | 本批提交 | 新增 `SmallComponentTests` **10 / 0**；`Pi.CodingAgent.Tests` 全量 **2624 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-3e Batch 2** | 三个与工具执行/状态栏直接相关的组件（TS 933 行 → C# 3 个文件 + 测试 30 例）：`footer.ts`（302）→ `Components/Footer.cs`（`FooterFormatting` 三个纯函数 + `FooterComponent`；会话统计按 `(session, sessionId, leafId, entryCount, limitsModel)` 五元组缓存，stats 行右对齐模型名、`• thinking`、`→ routed` 与 `(provider)` 前缀放不下时回落，扩展状态行按 key `CurrentCulture` 排序）；`bash-execution.ts`（223）→ `Components/BashExecution.cs`（`AppendOutput` 续行 + stripAnsi + CRLF/CR 归一、`TruncateTail` 上下文截断后再取末 20 逻辑行预览、`CachedVisualLines` 宽度感知缓存）；`tool-execution.ts`（408）→ `Components/ToolExecution.cs`（call/result 双槽 + 共享 `rendererState`、self shell 自渲染 vs default shell 的 Box 包装、图片三元组复用、渲染器异常回退 fallback、`FromDefinition` 承载 TS 的 `ToolRenderers \| ToolDefinition` 结构联合）。**同批把占位接口 `IReadonlyFooterDataProvider` 补成真实契约**（四成员，对齐 TS `ReadonlyFooterDataProvider` 的 `Pick`），`FooterDataProvider` 实现之。新登记 **C246**（`Box` 在端口里是 `IComponent` 的**兄弟**而非 `Container` 的子类，TS `Box extends Container` 故 `renderContainer.clear()/addChild()` 两容器同源；C# 用局部 `ClearRender`/`AddToRender` 辅助函数统一，**不能强转**——9 个用例曾全部 `InvalidCastException`）；**C247**（`ToolExecutionOptions` 用可空字段承载 TS 的 `?? 默认值`；`UpdateResult` 把 TS 挂在 result 上的 `durationMs` 拆成独立参数，经 `ToolRenderContext.DurationMs` 下发）；**C248**（测试缝 `IFooterSessionView` + `AgentSessionFooterView`：TS 测试用对象字面量冒充 `AgentSession`，C# 对具体类做不到，沿用 E-2 `OpenOverride` 的注入缝做法）。**三处测试期望按 TS 原文改正（实现无误）**：`AppendOutput("…\r\n")` 的尾随换行会留一个空尾行（TS `split("\n")` 同样保留该项）；25 个 `line-NN\n` chunk 产生 26 条逻辑行故隐藏数为 **6** 不是 5；截断警告需 `truncationResult.truncated` 为真（只有路径不算）；另 `formatTokens` 的 <10000 档是 `toFixed(1)` 不是四舍五入——9999 → `"10.0k"` 而 9950 → `"9.9k"`（JS 浮点半格以下），已用 `JsNumber.ToFixed` 钉住 | `439ea60`（已推送） | 新增 `FooterAndToolComponentsTests` **30 / 0**（FooterTests 13 / BashExecutionTests 6 / ToolExecutionComponentTests 11；上游 `test/footer-width.test.ts` 11 例与 `test/bash-execution-width.test.ts` 2 例全部转写）；`Pi.CodingAgent.Tests` 全量 **2654 / 0 失败**（53s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-3e Batch 3** | 两个彩蛋组件（TS 1,703 行 → C# 2 个文件 + 测试 73 例）：`armin.ts`（384）→ `Components/Armin.cs`（`Armin` 静态层：31x36 XBM 位图 144 字节 LSB first、`IsArminPixel`/`GetChar`/`BuildFinalGrid` 半块成 31x18；`ArminComponent`：七种揭示特效 + 30fps/glitch 60fps 定时器 + 按宽度缓存的渲染，行先裁再上色、末尾恒 `ARMIN SAYS HI`）；`easter-egg-3d.ts`（1292）+ `easter-egg-3d.lazy.ts`（27）→ `Components/EasterEgg3D.cs`（`Hash`/`Smooth`/`EaseOut`/`Mix`/`IsLight`/`GlyphInk`/`Rotation`/`SpinPhase`/`SpinAngle` 纯函数层 + `ParseScreen` SGR 解析 + `ShuffleSteps` 拼图代数 + `BlockRaster` 逐 braille 点射线求交与深度缓冲 + halo 分离式 tent 滤波 + `EasterEgg3dAnimation` 溶解/旋转/拼图/星空/提示行与反向退出时间线 + `EasterEggPlayer` 三道守卫）。新登记 **C249**（rain 特效永不结束——上游缺陷原样保留：列只有最低墨点落在第 0 行才 `settle`，而该位图第 0 行全空、首尾列全空）、**C250**（`EasterEgg3d` 联合 → `EasterEggKind` + `PiLogoEgg`/`ArminEgg`，因模块级辅助类已占用 `EasterEgg3D`）、**C251**（TS `interface Box` → `BlockBox`，与 `Pi.Tui.Components.Box` 撞名）、**C252**（动画的时钟与定时器改为注入缝：公开构造器用 `Stopwatch` + 启动定时器，内部构造器可固定时钟且不起定时器）、**C253**（`Intl.Segmenter` → `UnicodeWidth.GraphemesWithIndex`）、**C254**（`Float32Array`/`Uint8Array`/`Uint16Array` → `float[]`/`byte[]`/`ushort[]`，保持同样的舍入）、**C255**（`Math.imul`/`>>>` 的 int32 语义用 `unchecked` 复刻，入参先截低 32 位）、**C256**（`performance.now()` → 注入的 `Func<double>` 毫秒时钟；`timer.unref()` 无 C# 对应物，改由 `Dispose`/`Finish` 释放）、**C257**（lazy 模块的动态 `import()` 在 C# 无对应物，`EasterEggPlayer` 直接调用；三道守卫保留）。**同批修正一处 JS 语义**：全部 `Math.round` 改走 `JsRound`（C# 默认银行家舍入会在 0.5 处翻转星空/颜色的量化档位）。**测试手法**：数值函数用「把 TS 纯函数原样跑在 node 里取参考向量」的差分比对（hash 8 组、smooth 7 组、easeOut 6 组、旋转矩阵 9 元素，精度 1e-14～1e-15） | `125ecf7`（已推送） | 新增 `ArminTests` **21 / 0** + `EasterEgg3DTests` **52 / 0**；`Pi.CodingAgent.Tests` 全量 **2727 / 0 失败**（重建后重跑，53s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-3e Batch 4** | `mermaid.ts`（89 行）→ `Components/Mermaid.cs`（约 250 行 + 测试 27 例）：transformer **骨架逐行移植**——`CreateMarkdownTransformer` 三路短路（`mode === "off"` / `messageType === "assistant-thinking"` / `isStreaming && mode !== "streaming"`）、顶层 token 逐个替换（`MarkedLexer.Lex`，非 mermaid 原样 `token.raw`）、`render` 返回 null 或 `art.width > context.availableWidth` 回落原文、非流式才把 `warnings` 折成 `Mermaid diagram not rendered: <first>[ (+N more)]` 的 code span（`warnings[0]` 之外的条目只计数不列出）；`CodeSpan`（围栏取行内最长反引号串 **+1**、首尾为反引号时补空格 padding、空行用 **U+00A0**）、`StyleSpan`/`ThemedLines`（六类 → `borderMuted`/`text`/`accent`/`muted`，`title` 叠 `bold`，`none` 原样）。**渲染引擎本体不移植**（登记 **C258**）：`grok-mermaid@0.2.3` 是 pi 仓库之外的第三方包（508 KB / 64 文件 / 零依赖），参照检出 `node_modules` 为空，上游 6 例测试断言的 box-drawing 输出**无法差分复现**；`render()` 改为 `IMermaidRenderer` 注入缝，默认 `null` → 全部回落原文，与上游「`render` 返回 null」走同一条代码路径（`MermaidArt`/`MermaidSpan`/`MermaidSpanClass` 按 `grok-mermaid` 的 `types.d.ts` 逐字段重建）。另登记 **C259**（主题测试缝 `IMermaidTheme`：TS 测试用 `{ fg, bold }` 对象字面量冒充 `Theme`，C# 对 sealed 具体类做不到，沿用 `DiffRenderer.IRenderDiffTheme` 的内部接口 + 内部重载做法）。`GetMode` 保持回调语义（每次调用现读，设置改动即刻生效），已用「同一次构造、前后改 mode」的用例钉住。**注册点留待 4f-4**：上游在 `interactive-mode.ts:516` 构造并注册，该文件尚未移植 | `67b8da2`（已推送） | 新增 `MermaidTests` **27 / 0**（骨架语义 17 + 行内编码 4 + 配色 3 + 块识别 1 + 主题 2）；`Pi.CodingAgent.Tests` 全量 **2754 / 0 失败**（1m15s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4a** | 4f-4（`interactive-mode.ts`，7,082 行 / 311 成员）的第一个切片：展示层纯函数（L1381-1762，约 380 行 TS → `Modes/Interactive/InteractiveModeFormatting.cs` + 59 例）。**路径**：`FormatDisplayPath`（home 前缀缩成 `~`，纯字符串前缀比较——`/home/u` 会把 `/home/user2` 缩成 `~ser2`，上游原样）/ `FormatExtensionDisplayPath`（再去 `/index.ts`、`/index.js`）/ `FormatContextPath`（cwd 相对优先，否则回落 home 缩写；分隔符沿用平台 `sep`，与 TS 一致）/ `GetShortPath`（① 同 `node_modules` 根 → posix 相对；② 同包根 → 相对且不含 `..`；③ `npm:` 前缀取 `node_modules/<pkg>/` 之后；④ `git:` 前缀取 `git/<host>/<owner>/` 之后；⑤ 回落 home 缩写）/ `GetCompactPathLabel` / `GetCompactPackageSourceLabel` / `GetCompactExtensionLabel`（`index` 特例三路）/ `GetCompactDisplayPathSegments` / `GetCompactNonPackageExtensionLabel`（尾段唯一性加宽）/ `GetCompactExtensionLabels`。**来源标签**：`GetDisplaySourceInfo`（8 组 source×scope 映射）/ `GetScopeGroup` / `IsPackageSource`。**启动门控**：`GetStartupExpansionState` / `ShouldShowStartupHeader`（仅被 `quietStartup: true` 隐藏）/ `ShouldShowStartupDetails`（仅 `quietStartup: false` 显示）。**分组与诊断**：`BuildScopeGroups`（project→user→path，空组丢弃）/ `FormatScopeGroups`（accent 组名、dim 缩进路径、mdLink 包名，均按 `CurrentCulture` 排序）/ `FindSourceInfoForPath`（就近祖先）/ `FormatPathWithSource` / `FormatDiagnostics`（collision 按资源名分组、保持首次出现顺序、✓/✗ 双色）。**登记 C260**：TS 的 `this.sessionManager` / `os.homedir()` / 模块级 `theme` 三处隐式依赖改为显式参数（`home` / `cwd`）与内部主题接口（`IInteractiveModeFormatTheme` + 内部重载，公开入口用 `ThemeRuntime.Current`），沿用 `IRenderDiffTheme` / `IMermaidTheme` 的既有做法。**三处测试期望按 node 对拍修正（实现无误）**：`~ser2/.pi`；`  "name" collision:` 的两空格缩进在 warning token **内**；`node_modules/(@?[^/]+(?:/[^/]+)?)/(.*)` 的可选子目录贪婪，未加 scope 的包会吞掉第一层目录（`a.js`），scoped 包则保留（`dist/a.js`）——上游怪癖，原样保留并钉住 | `acc414c`（已推送） | 新增 `InteractiveModeFormattingTests` **59 / 0**（22 个 `[Fact]` + 37 个 `[Theory]` 用例）；`Pi.CodingAgent.Tests` 全量 **2813 / 0 失败**（51s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-2b** | `theme-controller.ts`（253 行）→ `Theme/ThemeController.cs`（约 420 行）+ 27 例。`RequestTerminalColorsAsync`：查询终端颜色，完成**或超时**后 apply，`onLateReply` 接迟到回复，查询抛错/被拒时 apply 空快照（逐字对齐 TS 的 `.then(apply, () => apply({}))`），超时 **100ms**。`InteractiveThemeController`：构造即 `markTerminalColorsPending()` + `initTheme(解析名, 开 watcher)` + 绑颜色方案监听；`applyFromSettings` 用「`parseAutoThemeSetting` 非空 **或** 名 == `system`」决定 auto-sync；`setThemeName` 失败回落 `system` 主题并把 `Failed to load theme "X": err\nFell back to the system theme.` 交给 `showError`（仅 `showError: true`）；`setThemeInstance` 标记 `"<in-memory>"` 并关 auto-sync；`preview` 只 `setTheme` + 重渲染、不记设置；`applyTerminalColors` 做 `reported ?? previous` 的**字段级合并**（超时保留旧值），内容相同则**不重渲染**（重渲染会重建所有组件）；`applyTerminalColorSchemeChange` 仅在 auto-sync 下处理，重查颜色且外观真翻转时重新应用；`rebindTui` 换 TUI 后重绑监听并回灌通知开关。**登记 C261**：TS 直接调 `theme.ts` 模块函数，端口抽 `IThemeControllerRuntime`（默认 `ThemeControllerRuntime` 转调 `ThemeRuntime`），理由同既有 `ThemeSeams`——测试不碰真实主题文件、不启文件监听。**同批增补测试替身**：`FakeTui` 加 `Invalidations` / `ColorSchemeListener` / `QueryTerminalColorsHandler` / `LastColorQuery` / `ClearColorSchemeListener`（纯增量，既有 60+ 用例不受影响）。**两处测试期望按 TS 语义修正（实现无误）**：`preview` 解析不到名称时回落 `_activeThemeName`（构造时已解析成 `dark`，不是 `system`）；`getThemeSelection` 优先级是 `显式设置 ?? settings.theme ?? active`，故设置里有 theme 时 `"<in-memory>"` 不会浮出 | `c1e3d78`（已推送） | 新增 `ThemeControllerTests` **27 / 0**（上游无测试文件，向量由源码推导）；`Pi.CodingAgent.Tests` 全量 **2840 / 0 失败**（2m15s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4b** | `tui-renderer.ts`（81 行）→ `Modes/Interactive/TuiRenderer.cs`（约 230 行）+ 7 例。`InteractiveTuiOptions`（7 字段）+ `CreateInteractiveTui`：`regular` → `TuiMainScreen`、`fullscreen` → `TuiAltScreen`，fullscreen 六个回调逐条对齐——`SearchMatchStyle` = `underline(bg("searchMatchBg", fg("searchMatchText", t)))`、`SearchCurrentMatchStyle` = `bold(inverse(同上))`、`SearchNavigationButtonStyle` hover 才 underline、`ScrollToEndIndicator` 读 `tui.altScreen.bottom` 的键位显示文本（无快捷键则省 ` · …`）、`OpenUrl` → `OpenBrowser.Open`、`CopySelection` → `Clipboard.CopyToClipboardAsync` 成功 `ClipboardCopyResult.Ok` / 失败 `Fail(e.Message)`（对齐 TS 的 `true \| error.message`）。**TS 闭包读模块级 `theme` 绑定**（切主题时被替换），故 C# 在回调内取 `ThemeRuntime.Current`。`CreateInteractiveTuiReference`：TS 用 `Proxy` 转发任意属性，C# 无 Proxy → 显式实现 `ITui` 的 30 个成员逐个转发到「当前」renderer，每次调用重新 `getTui()`（等价 TS Proxy 在实例变化后重解析方法）。**登记 C262**（Proxy → 显式转发；TS 的 `has`/`getPrototypeOf` 陷阱无对应物）。**两处记录差异**：`logDirectory` 在端口是死参数（TS 基类写 TUI 调试日志文件，端口 `TuiBase` 不写，4f-2a 已记）；TS 按字面量 `tuiMode` 重载返回具体类型，C# 统一返回 `TuiBase`。**批次调整**：原计划的「类骨架 + 构造器」顺延为 4f-4d（构造器调用的四个方法尚不存在），4f-4 由 10 子批改为 12 子批 | `d8f14b9`（已推送） | 新增 `TuiRendererTests` **7 / 0**（上游无测试文件，向量由源码推导；含「renderer 换掉后参考仍跟随」这条 Proxy 核心语义）；`Pi.CodingAgent.Tests` 全量 **2847 / 0 失败**（2m50s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4c** | interactive-mode 的模块级层（类之上约 200 行 TS）→ `InteractiveModeModule.cs` + 23 例。**类型**：`IExpandable`；`CompactionQueuedMessage`（`Mode` + `Text`）+ `CompactionQueueModes`（`steer`/`followUp`）。**组件**：`ExpandableText : ThemedText`（折叠/展开两个取值函数 + padding，`SetExpanded` 后 `Invalidate`）；`BuiltInHeader : ExpandableText`（logo 占 1-4 列 × 0-1 行，命中回调 `(screenX - x + 1, screenY - y)`，越界/非 `Click`/无回调一律 `null`）。**纯函数**：`QuoteIfNeeded`（`[^a-zA-Z0-9_\-./~:@]` 之外才加单引号，内部 `'` → `'\''`）、`FormatCrashExtensionHint`（1 个 → `extension \`a\`` + `it`；2 个 → `\`a\` and \`b\`` + `them`；3+ → 牛津逗号；先过滤空串/null）、`IsAnthropicSubscriptionAuthKey`（`sk-ant-oat` 前缀）、`LlamaCppPostLoginGuidance`、`IsDeadTerminalError`。**常量**：`AnthropicSubscriptionAuthWarning`、`RadiusLoginIntro`。**登记 C263**（死终端判定：TS 查 Node errno `EIO`/`EPIPE`/`ENOTCONN`/`ENOTTY`，.NET 把 tty 失败折叠成 `IOException` 且不暴露 errno → 按异常类型映射，`IOException` / `ObjectDisposedException` / 断连 `SocketException`，**比 TS 略宽**，注释与台账均写明）与 **C264**（`ThemedText` 是 sealed 且 `Text` 没有 `virtual HandleMouse`：照 C233 给 `Text` 补 `public virtual TuiMouseEventResult? HandleMouse(...) => null`——否则经 `IComponent` 引用调用会落到接口默认实现、子类 override 永不生效——并把 `ThemedText` 去掉 sealed）。**实现细节**：TS 用对象字面量装箱保存 `expanded`，C# 的构造器初始化器不能读 `this`，故用私有重载把状态盒子穿进去。**顺延**：`formatResumeCommand` → 4f-4l、`createFuzzyAutocompleteItems` + login provider 三函数 → 4f-4e、`RenderSessionItem` 三判定 + `CompactionCostNotice` + `IsUnknownModel`/`HasDefaultModelProvider` → 4f-4j | `0740b7e`（已推送） | 新增 `InteractiveModeModuleTests` **23 / 0**（上游无测试文件）；`Pi.CodingAgent.Tests` 全量 **2870 / 0 失败**（2m38s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4d** | interactive-mode 的**诊断与文案叶子层**（终端标题 / 聊天提示 / 崩溃报告 / 安装变更告警 / 编辑器边框色，TS L1124-1134、L2154-2200、L3795-3830、L4495-4505、L4602-4615 五段）→ `Modes/Interactive/InteractiveModeDiagnostics.cs` + 39 例。**批次调整**：原计划本批 = 类骨架 + 构造器，实测构造器**传递依赖整个类**（`resetExtensionUI` 要 10 个扩展 UI 方法、`rebindCurrentSession` 要 7 个会话生命周期方法，均属 4f-4h/4f-4j），无法在不留桩的前提下落地，故本批改为叶子层、类骨架顺延为收尾批 **4f-4m**（子批数 12 → 13）。**终端标题**：`BuildTerminalTitle`（`{APP_TITLE} - [{sessionName} - ]{basename(cwd)}`；TS `if (sessionName)` 是真值判定，空串走无会话名分支）。**聊天提示**：`ChatNotice(Text, ColorToken, Pad)` 三构造 —— `ErrorNotice`（前缀 `Error: `、token `error`、**padding = outputPad**）与 `WarningNotice`/`StatusNotice`（`Warning: ` 前缀 / 无前缀、token `warning`/`dim`、**padding 硬编码 1**）的**不对称原样保留**；`ShouldCoalesceStatus`（`childCount > 1 && 末项是受跟踪 text && 次末项是受跟踪 spacer` 才原地更新）。**编辑器边框**：`ResolveEditorBorderColor`（bash 模式优先于思考等级，否则按等级取边框色；TS 的 `thinkingLevel \|\| "off"` 在 C# 非空枚举下无兜底需求，`Off` 即默认值）。**崩溃与告警**：`BugReportHint`、`CrashReportInstructions`（无会话文件时**硬编码 `start pi and`**，不用 APP_NAME——上游原样）、`ShouldSuggestBugReport`（`stopReason == "error" && !retryable && !abort`）、`IsAbortOrCancelMessage`（`\b(?:abort(?:ed)?\|cancel(?:l?ed)?)\b` 忽略大小写；`null` 视作空串 → false）、`BuildInstallChangeWarning`（`updated` 分支带新旧版本、否则「removed or replaced」；有 resumeCommand 走 `Restart with \`…\``，否则 `Restart {APP_NAME}.`）。**登记 C265**：TS 的 `showError`/`showWarning`/`showStatus` 直接构造 `Spacer`/`ThemedText` 并读模块级 `theme`，C# 抽成纯值 `ChatNotice`，主题应用与组件构造留给 4f-4m 类本体——无行为差异，只为可测。**顺延**：`formatResumeCommand` 仍在 4f-4l（本批 `BuildInstallChangeWarning` 只接收结果参数） | `dd4ad80`（已推送） | 新增 `InteractiveModeDiagnosticsTests` **39 / 0**（上游无测试文件，向量由源码推导；含 padding 不对称、coalescing 六种边界、七档思考等级逐一比对）；`Pi.CodingAgent.Tests` 全量 **2909 / 0 失败**（1m05s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4e** | `createBaseAutocompleteProvider` 中不依赖类自身状态的部分（TS L374-423 的四个模块级辅助 + L669-715 的来源标签与冲突诊断）→ `Modes/Interactive/InteractiveModeAutocomplete.cs` + 24 例。**模糊项**：`CreateFuzzyItems`（`Fuzzy.Filter` 之上薄封装，**无匹配返回 `null` 而非空列表**——编辑器据此判定「无建议」）。**登录 provider**：`LoginProviderCompletionOption`（`Id`/`Name`/`AuthTypes`/`Subscription`）；`BuildLoginProviderCompletionOptions` 按 id 合并多行、`authTypes` 去重后按 `AUTH_TYPE_ORDER`（oauth=0 / api_key=1）排序、最终按 name 用 `CurrentCulture` 排序（用 `OrderBy` 保持 TS `Array.sort` 的**稳定性**，`List.Sort` 不稳定）；`GetLoginProviderSearchText` 是 `{id} {name}` 后跟每个 authType 的**线上拼写 + 标签**两串（搜 `api_key` 必须命中 API key 类 provider）；`FormatLoginProviderCompletionDescription` 用 `/` 连接标签，`name != id` 时前缀 `{name} · `。**来源标签**：`GetSourceTag`（`builtin` 与缺省不加标签；`u`/`p`/`t` 按 scope，`temporary` 落 `t`；`auto`/`local`/`cli` 只留字母；`npm:` 原样拼在冒号后；其余交给 `Git.ParseGitUrl`，命中则重写为 `{prefix}:git:{host}/{path}[@ref]`；先 `Trim` 再判定）；`PrefixDescription`（无标签原样返回，空描述退化为 `[tag]`）。**内置冲突**：`GetBuiltInCommandConflictDiagnostics`（与内置交互命令同名的扩展命令各发一条 `warning`，`invocationName == name` 提示「Skipping in autocomplete」，否则提示「Available as '/{invocationName}'」，`path` 取 `SourceInfo.Path`）。**配套改动**：`OAuthSelectorComponent.AuthTypeText` 由 `private` 提升为 `public`——它是 `oauth`/`api_key` 线上拼写的唯一来源，登录补全的 haystack 复用同一处而非复制。**顺延**：`createBaseAutocompleteProvider` 的命令列表组装（内置命令 / 模型 / 思考等级 / 登录 / 模板 / 扩展 / 技能七路合流 + `CombinedAutocompleteProvider` 构造）与 `setupAutocompleteProvider` 都读 `this.session` / 类字段，随 4f-4m | `acb9769`（已推送） | 新增 `InteractiveModeAutocompleteTests` **24 / 0**（上游无测试文件，向量由源码推导；含 git 源 `u:git:github.com/owner/repo` 与 `@ref` 两种形态）；`Pi.CodingAgent.Tests` 全量 **2933 / 0 失败**（1m16s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4f** | `showStartupNoticesIfNeeded`（L836-865）与 `showLoadedResources`（L1764-1962，199 行）中不碰容器、不读 `this.session` 的部分 → `Modes/Interactive/InteractiveModeStartup.cs` + 20 例。**段落头**：`FormatSectionHeader`（`[name]` 走 `mdHeading`，可显式覆盖）。**紧凑列表**：`FormatCompactList`（逐项 `Trim` → 丢空串 → 可选按 `localeCompare` 排序（C# 用 `CurrentCulture`，同 4f-4a）→ `, ` 连接 → 整体前缀两空格并套 `dim`；**空列表仍保留两空格缩进**，因为 TS 对空数组 `join` 得 `""` 而缩进照留）。**变更日志**：`ExtractLatestChangelogVersion`（`/##\s+\[?(\d+\.\d+\.\d+)\]?/` **无 flags**，取首个三段版本号；`## v1.2.3` 因 `\[?` 吞不掉 `v` 而不匹配、`## 1.2` 缺一段也不匹配，均回落 `null`）；`BuildCondensedChangelogNotice`（`Updated to v{ver}. Use /changelog to view full changelog.`，版本缺失回落当前版本，`/changelog` 加粗）。**诊断折叠**：`CollectExtensionDiagnostics`（加载 `error` → `error` 诊断、`warning` → `warning` 诊断，顺序为**先全部 error 再全部 warning**，因为 TS 是两个独立循环；`warnings` 为 null 视作空）。**顺延**：`showLoadedResources` 的容器组装（`ExpandableText` 四段 + `contextFiles` + 四类诊断块）与 `showStartupNoticesIfNeeded` 的 `DynamicBorder`/`Markdown` 插入都写 `loadedResourcesContainer`/`chatContainer` 并读 `this.session.resourceLoader`，随 4f-4m | `5e66592`（已推送） | 新增 `InteractiveModeStartupTests` **20 / 0**（上游无测试文件，向量由源码推导；含变更日志正则 10 组用例）；`Pi.CodingAgent.Tests` 全量 **2953 / 0 失败**（56s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4g** | `showWorkingStatusIndicator` / `setWorkingIndicator` / `setWorkingVisible` / `setHiddenThinkingLabel` / `clearStatusIndicator`（TS L221-228、L2290-2375）中可独立判定的部分 → `Modes/Interactive/InteractiveModeStatus.cs` + 16 例。**类型守卫**：`IsWorkingStatusEditor` —— TS 是鸭子类型（`"embedWorkingStatus" in editor && === true && typeof setWorkingStatusIndicator === "function"`），C# 里只有 `CustomEditor` 带这两个成员，故类型判定等价。**工作色**：`ResolveWorkingStatusColor` 非工作状态编辑器返回 `null`（指示器沿用 accent/muted 默认），否则跟随编辑器边框色、无边框色回落思考等级边框色。**文案**：`ResolveWorkingMessage` / `ResolveHiddenThinkingLabel` 都是 `??`，**仅 null 回落**（空串保留）。**Idle 占位**：`ShouldRestoreIdleStatus` 四条件（真的清掉了 / 未嵌在编辑器里 / `regular` 模式 / `clearOnShrink` 开），缺一不补——否则全屏或未清屏时留一条永久空行。**重置文案**：`DefaultWorkingMessageWithInterrupt` = `{msg} ({keyText("app.interrupt")} to interrupt)`（`resetExtensionUI` 用）。**登记 C266**：TS 的 `borderColor?:` 初始 `undefined`，`??` 回落是**常态路径**；C# 的 `Editor.BorderColor` 是**非空属性**（默认 `theme.BorderColor`），回落分支只有调用方显式置 `null` 才可达。分支保留以对齐读法，注释与测试（显式置 `null!`）均写明。**顺延**：`statusContainer` 的 clear/addChild、编辑器 `SetWorkingStatusIndicator` 重绑、`chatContainer` 遍历改 hidden-thinking label 都写容器，随 4f-4m | `76f4b9b`（已推送） | 新增 `InteractiveModeStatusTests` **16 / 0**（上游无测试文件，向量由源码推导；含 C266 回落分支与「空串不回落」两组边界）；`Pi.CodingAgent.Tests` 全量 **2969 / 0 失败**（55s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4h** | `bindCurrentSessionExtensions`（L1963-2052）/ `setExtensionWidget`（L2398-2427）/ `showExtensionConfirm`（L2620-2626）/ `getCrashExtensionHint`（L2125-2137）/ 扩展快捷 handler（L2277-2281）/ UI context 的 `setTheme`（L2660-2671）中可独立判定的部分 → `Modes/Interactive/InteractiveModeExtensions.cs` + 14 例。**widget 截断**：`MaxWidgetLines = 10` + `WidgetContent(content)`（只取前 10 行，另给「是否截断」标志，由调用方补 `"... (widget truncated)"` 行——TS 的截断提示是**独立一条 ThemedText**，不混进内容行）。**确认框**：`ConfirmTitle`（`{title}\n{message}`）+ `ConfirmOptions`（`["Yes","No"]`）+ `IsConfirmed`（严格 `=== "Yes"`，大小写敏感；关闭对话框返回 null 即否定）。**崩溃提示**：`GetCrashExtensionHint` 把堆栈帧匹配结果过 `FormatCrashExtensionHint`，**整体 try/catch**，任何异常塌成 null——不能让「崩溃提示」本身再崩。**快捷 handler 错误**：`ShortcutErrorMessage` = `Shortcut handler error: {msg}`。**主题写盘门控**：`ShouldPersistThemeName` —— setTheme 仅在**成功**且与已记录设置**不同**时才写；拒绝不动设置，同名不重复写（`recordedSetting == null` 视为「不同」，照写）。**顺延**：`bindCurrentSessionExtensions` 的 `commandContextActions`（newSession / fork / navigateTree / switchSession / reload / shutdownHandler / onError）与 `createExtensionUIContext` 的 25 个回调都直接绑 `session` / `editor` / `ui`，随 4f-4m | `dfed9b4`（已推送） | 新增 `InteractiveModeExtensionsTests` **14 / 0**（上游无测试文件，向量由源码推导；含 10/25 行截断边界、`yes` 小写不确认、三条主题写盘门控）；`Pi.CodingAgent.Tests` 全量 **2983 / 0 失败**（1m14s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4i** | `setupEditorSubmitHandler`（L3179-3375，201 行 if-chain）的路由决策 → `Modes/Interactive/InteractiveModeSubmit.cs` + 55 例。**命令表**：`SubmitCommand` 枚举（27 个内置命令）+ `SubmitRoute(Command, Argument, FullText)` + `Route(text)`，把 TS 的 26 个 `if` 分支变成一张表。**三类匹配**：① 五个带参命令（`/bug` `/model` `/thinking` `/login` `/compact`）——`/bug` 或 `/bug …`，切片长度是**可观测契约**（`/bug` 切 4 即命令本身，`/model`/`/login` 切 7、`/thinking` 切 10、`/compact` 切 9 各含尾空格），空白切片按 TS 的 `\|\| undefined` 塌成 **null**；② 19 个精确命令**只做全等匹配**（`/settings x` 不是命令）；③ `/export` `/import` `/name` 匹配 `/name` 或 `/name …`，handler 收**整行**。**近失（near-miss）原样钉住**：`/models` `/modelfoo` `/buggy` `/named` `/thinkingx` 均非命令。**bash 分支**：`ParseBashCommand` —— `!` 正常、`!!` 排除上下文且**恰好剥两个字符**（`!!!foo` → `!foo`）；裸 `!` / `!!`（命令为空）**不是** bash 行，落到普通提交——与 handler 里其它所有前缀分支不同。**名称探针**：`GetSlashCommandName`（首个空格前、去 `/`）+ `BashBusyWarning` 常量。**顺延**：onSubmit 闭包本体（各分支调 `handleXxx`、`editor.setText("")` 的先后、压缩/流式期的排队与 `flushPendingBashComponents`）随 4f-4m | `ca2a263`（已推送） | 新增 `InteractiveModeSubmitTests` **55 / 0**（上游无测试文件，向量由 if-chain 逐分支推导）；`Pi.CodingAgent.Tests` 全量 **3038 / 0 失败**（1m12s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4j** | `renderSessionEntries` / `renderSessionItems` / `addMessageToChat` / `getUserMessageText` / `isUnknownModel`（TS L277-289、L327-329、L3774-3781、L3847-3968、L3970-4088）中可独立判定的部分 → `Modes/Interactive/InteractiveModeRender.cs` + 31 例。**类型落地**：`CompactionCostNotice`（`Kind` + `Usage`）与 `CompactionCostKind` 常量 —— 4f-4c 顺延的类型在本批补上。**条目折叠**：`IsRenderedDirectly`（`custom` 与 `usage(kind=cache_warm)` 条目**原样渲染**，不走上下文消息折叠，其余 usage kind 不算）；`ShouldAppendCompactionCost`（`compaction` / `branch_summary` 且有 usage 且折叠后**至少一条消息**才追加成本提示——全被裁掉的摘要不留空提示）。**工具结果**：`IsFailedStopReason`（`aborted` / `error` 的助手消息把工具组件**收尾为错误结果**而非登记 pending）；`ResolveToolResultErrorMessage`（aborted 报重试次数，`>1` 复数化 `attempts`、`0` 则 `Operation aborted`；error 回显 `errorMessage`，空则 `Error`）。**用户文本**：`GetUserMessageText`（字符串内容原样；块内容过滤 `text` 后**无分隔符连接**；结果同时是编辑器历史的入史文本；非 user 角色一律空串）。**占位模型**：`IsUnknownModel`（provider/id/api 三字段全为字面 `"unknown"`；`null` 不算）。**顺延**：`handleEvent` 的 388 行事件分发、`addMessageToChat` 的八类组件构造、`addCacheWarmingUsage` / `addCacheMissNotice` 与 `collectCacheMisses` 的接线（`CollectCacheMisses` 已在 `Core/CacheStats.cs`）随 4f-4m | `c1f81ce`（已推送） | 新增 `InteractiveModeRenderTests` **31 / 0**（上游无测试文件，向量由源码推导）；`Pi.CodingAgent.Tests` 全量 **3069 / 0 失败**（2m44s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4k** | `getPathCommandArgument`（L6511-6538）/ reload 完成态文案（L6478-6481）/ import 用法错误（L6543）/ bash 失败文案（L7043-7045）/ `handleHotkeysCommand` 的 markdown 正文（L6782-6876）→ `Modes/Interactive/InteractiveModeCommands.cs` + 20 例。**路径参数**：`GetPathCommandArgument(text, command)` —— 精确命令无参；非 `{cmd} ` 前缀无参；`TrimStart` 后取值（**只跳过前导**空白，内部空白照旧截断）；引号参数（单双皆可）取引号内文本**原样**（不做转义、空串也照返），未闭合引号返回 `null`；无引号在首个 `\s` 截断。**文案**：`ReloadedStatusMessage`（saved-trust 变体是**同一句话加子句**而非另一条字符串）；`ImportUsageError`；`BashFailureMessage`（非 `Error` 的拒绝显示 `Unknown error` 而非空串）。**热键表**：`BuildHotkeysMarkdown` —— Navigation / Editing / Other 三段固定表，键位显示经回调插值；`tui.input.newLine` 的 `(Ctrl+Enter on Windows Terminal)` 提示**仅 Windows**；扩展快捷键段（有 shortcut 才有，`description ?? extensionPath`，键显示走 `FormatKeyText` 的 `Capitalize`）；返回前 `Trim()`，与 TS 喂给 `Markdown` 的 `hotkeys.trim()` 对齐。**顺延**：`handleReloadCommand` 的 reloadBox + beforeSessionStart 恢复序列、`handleBashCommand` 的 `emitUserBash` 拦截与 pending/chat 两路挂载、`handleImportCommand` 的确认与 MissingSessionCwd 分支、`handleSessionCommand` 的 Session Info 文档（**纯渲染器**，读 `this.session` 统计与 cache-warming 状态）随 4f-4m | `f09dba9`（已推送） | 新增 `InteractiveModeCommandsTests` **20 / 0**（上游无测试文件，向量由源码推导；含未闭合引号、多参数截断、Windows 提示有无、扩展段回落四组边界）；`Pi.CodingAgent.Tests` 全量 **3089 / 0 失败**（2m46s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4l** | `formatResumeCommand`（L341-358）/ `checkTmuxKeyboardSetup` 的判定（L1287-1305）/ `getChangelogForDisplay` 的门控（L1324-1350）→ `Modes/Interactive/InteractiveModeLifecycle.cs` + 16 例。**恢复命令**：`FormatResumeCommand` —— isTty / isPersisted / 会话文件存在**三道独立门**，任一不满足即 `null`（空串文件路径也算缺失，TS `!sessionFile`）；自定义会话目录追加 `--session-dir` 且目录经 `InteractiveModeSupport.QuoteIfNeeded`（含空格 → 单引号）；默认目录不加该参数；崩溃报告与安装变更告警共用。**tmux 键盘**：`ResolveTmuxKeyboardWarning(extendedKeys, extendedKeysFormat)` —— 查询失败（`null`）**刻意沉默**（超时/沙箱不该告警）；`extended-keys` 非 `on`/`always` 先报 off；仅 `xterm` 格式才报 csi-u 建议；两条告警互斥、前者优先。**变更日志门控**：`ShouldSkipChangelog(messageCount)`（transcript 已有消息即续会话，一律不显示）；`IsFreshInstall(lastVersion)`（无已记录版本 = 全新安装——记录版本 + 发遥测，但**不显示**内容，哪怕文件里有条目）。**顺延**：`showSettingsSelector` / `showTreeSelector` / `showModelsSelector` 三个对话框（选组件 + `activeSelectorToken` 单飞令牌）与 `init` / `run` / `shutdown` 直接操纵容器与 runtimeHost，随 4f-4m | `818c171`（已推送） | 新增 `InteractiveModeLifecycleTests` **16 / 0**（上游无测试文件，向量由源码推导；含三道门逐项抑制、引号目录、tmux 六组、changelog 门控四组）；`Pi.CodingAgent.Tests` 全量 **3105 / 0 失败**（3m03s）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4m-1** | InteractiveMode 类装配首批（骨架与扩展 UI 面）：`InteractiveModeOptions` + 字段块全量照抄（`CS0169/0414/0649` pragma 包住，收口批摘除）+ 便利访问器 + 构造器（TS 顺序原样，`_themeController = null!` 化解回调闭包可空流告警）+ 构造器回调链可达方法：通知四方法（ChatNotice 接线 + showStatus coalescing）/ 编辑器 chrome / mount-stop-switch TUI / applyRuntimeSettings 族（**C267**：configureHttpDispatcher 在 .NET 无全局 dispatcher——按请求现读，分支删除并注释）/ subscribeToAgent / updateAvailableProviderCount / rebindCurrentSession / 状态指示器链 8 方法（4f-4g 决策接线 + 容器 DOM 半部）/ 扩展 widget-footer-header-终端监听-对话框五件套 / setCustomEditorComponent（文本与 handler 搬运）/ showSelector + disposeActiveSelector / resetExtensionUI 全家 / 视图叶子五方法。**C268** dispose→IDisposable 判定；**C269** IExtensionUIContext 工厂形参 TuiBase→ITui（转发引用必须可达，同步 ExtensionRunner 两实现与两测试桩）；**C270** 主题经 LoadThemeFromPath 重读。**修掉 Pi.Tui Loader 真实并发缺陷**（tick 回调 UpdateDisplay 在锁外，与 Stop 竞争可在主题拆卸后调配色函数——InteractiveModeTests 稳定复现）。**登记 5 个缝**（SetupAutocompleteProvider / RenderCurrentSessionState / HandleEventAsync / BindCurrentSessionExtensionsAsync / HandleRightClickPaste，按子批退役、收口批清零）。4f-4m 余 4 子批 | 本批提交 | 新增 `InteractiveModeTests` **32 / 0**（runtime 夹具 + StringTerminal）；`Pi.CodingAgent.Tests` 全量 **3137 / 0 失败**；`Pi.Tui.Tests` 全量 **23854 / 0 失败**（Loader 回归）；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4m-2** | InteractiveMode 事件分发与会话渲染（handleEvent 16 类事件全量 + addMessageToChat 八 role + renderSessionEntries/Items 折叠与配对 + cache-warm/cost/thinking-drop/cache-miss 通知族 + queued/pending 族 + stop）：退役 HandleEventAsync / RenderCurrentSessionState 两缝；新登记 InitAsync / CheckShutdownRequested 两缝（生命周期批退役）；message_end 的 errorMessage 就地改写改 `with`（行为等价）；`IModelPriceSource` 以 ModelRuntimePriceSource 适配器桥接（TS 结构化类型直传）；mermaid transformer 延迟构造（ThemeRuntime.Current 顺序）；`GetMarkdownThemeWithSettings` 不可变类重建；4f-4m 余 3 子批、余 3 缝 | 本批提交 | `InteractiveModeTests` 全类 **39 / 0**（+7）；`Pi.CodingAgent.Tests` 全量 **3144 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4m-3-前置** | `chat-viewport.ts`（47 行）→ `Modes/Interactive/ChatViewport.cs` + 5 例：init 的前置依赖（createChatViewport 工厂此前遗漏），ScrollView（follow=end/primary/chain/scrollbar 默认 auto）+ 输入坞 VStack（编辑器行 minSize=3，可选 widget 行插在 status 后与编辑器后）+ 根 VStack（transcript basis=0 grow=1）；TS `{ basis: "auto" }` → `Basis = null`。**4f-4m-3 本体**（init/run/shutdown/信号/handleCtrlC·D·Z）已读完 TS 本体并核对全部 C# 对应 API（CrashLog 缺 take-unnotified 流程 → 登记缝；runtimeHost.dispose → DisposeAsync；SIGTSTP/SIGCONT → PosixSignalRegistration + libc kill P/Invoke），init 需新登记 SetupEditorSubmitHandler 与命令族键位两个缝（命令批退役） | 本批提交 | 新增 `ChatViewportTests` **5 / 0**；`Pi.CodingAgent.Tests` 全量 **3149 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4m-3** | InteractiveMode 生命周期批：init 全量（viewport 装配 / BuiltInHeader / fd+rg 就绪 / 键位与提交 handler / rebind / 初始渲染 / 主题与语法高亮收尾）+ run（目录刷新 15s / 版本检查 / tmux 告警 / 启动诊断族 / 初始消息 / 主循环）+ shutdown（信号路径与交互路径分离，resume 命令行）+ 崩溃退出（emergency 129 / uncaughtCrash 恢复终端）+ 信号注册（PosixSignalRegistration，SIGHUP 软关停；stdin/out/err 流错误监听无 .NET 对应物，注释记录）+ CtrlC/D/Z（libc kill P/Invoke）+ setupKeyHandlers 本批半部 + 右键粘贴 + 启动辅助（changelog 本体 / telemetry / managed tools / 包更新通知 / tmux 接线）。退役 InitAsync / CheckShutdownRequested / HandleRightClickPaste 三缝；新登记：SetupEditorSubmitHandler·SetupCommandKeyHandlers·ShowDoubleEscapeSelector·MaybeWarnAboutAnthropicSubscriptionAuth·ShowNewVersionNotification·HandleClipboardPasteAsync（→ 4f-4m-4）、TakeUnnotifiedCrashForWarning·RecordCrashForUi·GetCrashExtensionHintForUi（→ 4f-4m-5）；CheckForPackageUpdatesAsync 为已知缺口（包管理器 update 流程未移植，主入口路径按 offline 分支返回空表不抛错）。C271：changelog 消息计数口径（持久化 message 条目）；`Session.IsBashRunning` 只读访问器（Core 增补）。4f-4m 余 2 子批，缝余 11 | 本批提交 | `InteractiveModeTests` 全类 **46 / 0**（+7）；`Pi.CodingAgent.Tests` 全量 **3156 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4m-4-前段** | crash-log 三缝 + 登录告警 + 新版本通知（独立可收口单元提前落地）：TakeUnnotifiedCrashForWarning（CrashLog.TakeUnnotified 接实例路径 + ISO 解析 + path 注入参）/ RecordCrashForUi（CrashLog.Record + 会话文件/cwd）/ GetCrashExtensionHintForUi（**Core 增补 ExtensionRunner.GetExtensionStackMetadata()**——TS runner 同名方法，此前仅私有列表，接 InteractiveModeExtensions 栈匹配）/ maybeWarnAboutAnthropicSubscriptionAuth（L5240-5268：warnings.anthropicExtraUsage 严格 false 门控 + 一次性旗标 + CheckAuthAsync oauth + sk-ant-oat 前缀）/ showNewVersionNotification（L4616-4644：横幅 + note Markdown（TS options {color} → C# DefaultTextStyle.Color）+ OSC8 changelog 行按能力降级）。退役 5 缝 | 本批提交 | `InteractiveModeTests` 全类 **51 / 0**（+5）；`Pi.CodingAgent.Tests` 全量 **3161 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4m-4 主体勘察（未动工）** | 命令键位批（退役 SetupCommandKeyHandlers / ShowDoubleEscapeSelector）的**前置依赖已查清**：`session.cycleThinkingLevel()` / `session.cycleModel(direction)` 是 **agent-session.ts L2518-2690 的实例方法**（cycleModel → _cycleScopedModel / _cycleAvailableModel + setThinkingLevel + getAvailableThinkingLevels + supportsThinking + _getThinkingLevelForModelSwitch，另有 _emitModelSelect 事件发射与 _addPersistedDefaultToNonEmptyScope、modelsAreEqual、getSupportedThinkingLevels、THINKING_LEVEL_OPTIONS 依赖），C# AgentSession 目前只有静态叶子（GetLastAssistantText / NextCycleIndex / NextThinkingLevel），**缺实例包装层**——该会话 API 批是命令键位批的前置。已核对的 C# 对应件：TuiAltScreen.GetCopyOnSelect / HasActiveSelection / CopyActiveSelectionToClipboard / Flash ✓、Clipboard.CopyToClipboardAsync ✓、ExternalEditor.EditInExternalEditorAsync ✓、SettingsManager.GetExternalEditorCommand ✓。**TS 方法位置清单**（下轮直接按行号开工）：handleFollowUp L4454 / handleDequeue L4486 / cycleThinkingLevel L4506 / cycleModel L4517 / handleOpenExternalEditor L4575 / handleCopyCommand L6610 / handleClearCommand L6899（需 runtimeHost.newSession + handleFatalRuntimeError）/ handleDebugCommand L6914（需 ui.render + getDebugLogPath）/ showModelSelector L5321 / showUserMessageSelector L5479 / showTreeSelector L5538 / showSessionSelector L5687 / 提交 if-chain L3179-3375 | 勘察记录（无代码改动） | 工作区干净；无测试运行 |
| 2026-10-17 | **4f-4m-4-中段** | 会话 cycle API + 命令键位：**Core 增补 AgentSession.CycleThinkingLevel / CycleModelAsync（scoped 优先 + 可用快照过滤 + ModelCycleResult）+ GetLastAssistantText() 实例包装**（TS agent-session.ts L2518-2690，上层 SetThinkingLevel/EmitModelSelectAsync 等全部复用，4e-3e-3 静态叶子之外补齐实例层）；命令键位六方法（handleFollowUp / handleDequeue / cycleThinkingLevel / cycleModel / handleOpenExternalEditor / handleCopyCommand）+ SetupCommandKeyHandlers 真实落地（thinking.cycle / model.cycle×2 / tools.expand / thinking.toggle / message.copy / followUp / dequeue + ui.OnDebug）。退役 1 缝；新拆 2 缝（SetupSelectorKeyHandlers / HandleDebugCommandForUi → 4f-4m-4 尾批）。C272：editor.getExpandedText 可选 API 未移植，handleFollowUp 回退 getText。4f-4m 余 1.5 子批，缝余 6 | 本批提交 | `InteractiveModeTests` 全类 **57 / 0**（+6）；`Pi.CodingAgent.Tests` 全量 **3167 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-17 | **4f-4m-4-尾批-前半** | showModelSelector（ModelSelectorComponent 装配 + SetModelAsync 双回调 + 默认模型引用）/ handleClearCommand（NewSessionAsync + cancelled 短路）/ **handleFatalRuntimeError 真实落地**（ShowError + 扩展栈提示 + recordCrash("fatal_error") + Stop("transcript") + Exit(1)；退役原计划缝）/ handleDebugCommand（ui.Render 全行清单 + JSONL 会话消息 → GetDebugLogPath 落盘）。退役 HandleDebugCommandForUi；SetupSelectorKeyHandlers 收缩改名 SetupTreeForkResumeKeyHandlers（tree/fork/resume 依赖会话 fork·navigateTree·getUserMessagesForForking·switchSession 家族，未移植）。接线 app.session.new / app.model.select。handleClearCommand 本体不测（致命路径 Exit(1)，与 shutdown 同类）。4f-4m 尾批余：tree/fork/resume 选择器三方法 + 提交 if-chain + handleClipboardPaste | 本批提交 | `InteractiveModeTests` 全类 **60 / 0**（+3）；`Pi.CodingAgent.Tests` 全量 **3170 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-18 | **4f-4m-4-尾批-中段** | fork / resume 选择器 + 扩展 UI 上下文：showUserMessageSelector（ForkAsync + SelectedText 回填）/ showSessionSelector（ListAsync·ListAllAsync + 重命名 via SessionManager.Open）/ handleResumeSession（SwitchSessionAsync + MissingSessionCwdException → confirm → CwdOverride 重试）/ promptForMissingSessionCwd / createProjectTrustContext / **createExtensionUIContext（IExtensionUIContext 全量 21 成员实现——四对话框·终端监听·状态·工作消息·widget·footer·header·标题·编辑器简写·SetTheme 持久化；缝承载：Custom×2 / PasteToEditor / AddAutocompleteProvider / GetAllThemes·GetTheme / GetEditorComponent）**。**Core 增补**：AgentSession.GetUserMessagesForForking() 实例包装。退役 1 缝：SetupTreeForkResumeKeyHandlers → 收缩为 SetupTreeKeyHandlers（仅 app.session.tree，依赖 NavigateTree 富结果形状）。接线 app.session.fork / app.session.resume。缝余 5。4f-4m 余：showTreeSelector（需 NavigateTree 富结果）+ 提交 if-chain + handleClipboardPaste + autocomplete | 本批提交 | `InteractiveModeTests` 全类 **62 / 0**（+2）；`Pi.CodingAgent.Tests` 全量 **3172 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-18 | **4f-4m-4-尾批-后段** | **会话 navigateTree 编排落地**（agent-session.ts L3963-4155，4e-3e-9h 顺延的最后一块会话 API）：AgentSession.NavigateTreeAsync + NavigateTreeResult + NavigateTreeSessionOptions——守卫 / 同叶 no-op / CollectEntriesForBranchSummary / TreePreparation + session_before_tree（取消·供摘要·覆盖经既有叶件合并）/ 默认摘要器（GenerateBranchSummaryAsync + 重试策略四元组转 RetryPolicy + 三重试事件）/ **叶子与回填走 4e-3e-9h 既有 ResolveTreeNavigationLeaf 叶件** / 摘要三分支（BranchWithSummary·ResetLeaf·Branch）/ RefreshFinalizedContext·RestoreToolsFromTranscript / session_tree 事件 / finally 清理。**零新增判定**——编排只是串接既有叶件。showTreeSelector 的依赖障碍已全部清除，随下一批接线 | 本批提交 | `InteractiveModeTests` 全类 **64 / 0**（+2：同叶 no-op / 用户消息导航回填）；`Pi.CodingAgent.Tests` 全量 **3174 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-18 | **4f-4m-4-尾批-收尾** | showTreeSelector（GetTree 空清单状态——新会话 GetTree 恒含根节点，空状态文案移植语义下不可达，注释记录；TreeSelectorComponent 装配 + 标签编辑 + OnCopy；onSelect 回调：同叶 no-op → 「Summarize branch?」三选（SkipPrompt 门控 + Esc 重开 + 自定义提示词回环）→ 流式中断 → 压缩拦截 → 摘要期 escape 换防 + BranchSummaryStatusIndicator → NavigateTreeAsync → aborted 重开 / cancelled 状态 / 成功清空重渲 + 编辑器回填 + FlushCompactionQueue）+ 双击 escape 真实现（"tree" → 树选择器 / "fork" → fork 选择器）。**退役 2 缝：SetupTreeKeyHandlers / ShowDoubleEscapeSelector——setupKeyHandlers 全部动作真实接线完毕**。4f-4m 余：提交 if-chain + handleClipboardPaste + autocomplete | 本批提交 | `InteractiveModeTests` 全类 **65 / 0**（+1）；`Pi.CodingAgent.Tests` 全量 **3175 / 0 失败**（复跑；首跑 ModelSelectorTests 单次时序 Flake，单独 5/5 与复跑均绿，非本批引入）；构建 0 警告 0 错误 |
| 2026-10-18 | **命令族增量：/name** | handleNameCommand（L6641-6664）：空参显示当前会话名（无名时 Usage 警告）→ `SetSessionName` + 归一化警告（JSON.stringify 双引号原样）→「Session name set」。`Session.SetSessionName` 已有（4e 批）。命令族剩余：handleCompactCommand（**缺口确认 + 勘察完成**：TS `session.compact()` 在 agent-session.ts **L2763-2890**——abort → compaction_start → GetCompactionSettings(model ref) → prepareCompaction → session_before_compact（取消/供摘要）→ `_runDefaultCompaction`（已有 C# 私有件）→ appendCompaction → session_compact → compaction_end；C# 侧全部子件已在（GetCompactionSettings/RunDefaultCompaction/AppendCompaction/BuildSessionProjection/EstimateMessagesTokens/ClearManualCompactionState），只缺公共编排本体；该命令随会话 compact 入口批落地）/ handleBugCommand（缺口：reportBug 模块）/ handleExportCommand·handleImportCommand·handleHelpCommand·handleToolsCommand·handleExtensionsCommand·handleTerminalSetupCommand·handleTrustCommand·handleLogoutCommand·handleLoginCommand·handleReloadCommand·handleSessionCommand·handleNewCommand·handleModelsCommand | 本批提交 | `InteractiveModeTests` 全类 **67 / 0**（+2）；`Pi.CodingAgent.Tests` 全量 **3177 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-18 | **4f-4m-5a/5c** | 命令族前半 5 个处理器（thinking/model/clone/trust/reload）+ 后半 9 个（export/import/session/changelog/hotkeys/armin/demented/bash/compact）+ **提前落地两件 4f-4m-6 前置件**（autocomplete 组装：createBaseAutocompleteProvider/setupAutocompleteProvider/TriggerCharacterOverrideProvider/getLoginProviderOptions；bindCurrentSessionExtensions 全量 6 成员 + showLoadedResources 四节四类诊断 + showStartupNoticesIfNeeded + setupExtensionShortcuts/ShortcutExtensionContext 17 成员）+ handleClipboardPasteAsync 全量实现（退役 3 缝）。**Core 侧**：ResourceLoader system-prompt 来源发现（SYSTEM.md/APPEND_SYSTEM.md + 项目受信任门控，接线进 AgentSessionBuild——顺延项落地）+ AgentSession.ExecuteBashAsync/RecordBashResult（abort 注册/shell prefix/bash_execution_update/流式延迟入队）+ BashExecutor.ExecuteBashOptions。测试 +26 例（94/94），修正 3 处用例（armin fullscreen 语义/hotkeys 渲染后文本/collapseChangelog 门控），Windows cmd 变体因沙箱挂起改 bash 内建 echo。命令族余：login/logout 族（5b）+ share/bug 两模块 + compact 公共入口 | `9b9d67e`（已推送） | `InteractiveModeTests` 全类 **94 / 0**（+27）；`Pi.CodingAgent.Tests` 全量 **3204 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-18 | **4f-4m-5b** | 登录认证族 17 个成员全量落地（TS L5767-6397 + /login·/logout 分派）：getLogoutProviderOptions（15s 超时）/ findLoginProviderOptions / handleLoginCommand 三分支 / startProviderLogin 三路 / showLoginAuthTypeSelector（订阅·API key·Radius 直入 + 单项短路）/ showLoginProviderSelector / showOAuthSelector（logout 凭据清单 + CredentialSynchronizationError 文案）/ completeProviderAuthentication（默认模型选型 + persist、llama 指引、三路错误文案、deferSelection + 15s catalog 刷新竞态）/ ambient 与 api-key 对话框（bedrock 文档提示）/ showAuthSelect·showAuthPrompt（四型提示 + 提示级取消竞速）/ notifyAuthDialog / loginProvider（DialogAuthInteraction + device id）/ showLoginDialog / offerRadiusMcpServer（mcp.json auth.provider 接线 + oauth 删除 + reload）。offerRadiusMcpServer 的 agent dir 改取 Services.AgentDir（与 5a 信任存储同源；TS 读全局 getAgentDir()，生产同值）；IsUnknownModel 形参改 ModelSpec?（TS 入参是会话 Model<any>，该叶子此前无调用方）。测试 +11 例（105/105）+ 3 个测试缝；夹具支持追加 provider 与注入凭据存储 | `159fda6`（已推送） | `InteractiveModeTests` 全类 **105 / 0**（+11）；`Pi.CodingAgent.Tests` 全量 **3215 / 0 失败**；构建 0 警告 0 错误 |
| 2026-10-18 | **compact 公共入口** | `AgentSession.compact()`（TS L2749-2905）公共入口收口：编排本体随 5a 命令族批落地，本批把 `CompactAsync` 由 internal 转 **public**（TS 是公共 API——/compact、RPC、扩展三路共用，4f-5 RPC 层要经它进入）+ 新增 `AgentSessionCompactEntryTests` 5 例（快路径条目落盘与 compaction_start/end 事件对；"Already compacted"；"Nothing to compact (session too small)" + Compaction failed 文案；扩展取消 → Compaction cancelled + aborted + session_compact_failed + 模型流零调用；扩展供结果 → 摘要器跳过 + fromHook=true + session_compact 的 fromExtension=true）。踩坑：切分点落回合中部走 split-turn 双次摘要，种子改「长 user+短 assistant」对齐 TS startsTurn；fromHook 在 TS entry 恒存在（false 也写盘）。**4f-4m-5c 全部收口** | `9b7bc1a`（已推送） | `AgentSessionCompactEntryTests` **5 / 0**（新增）；`Pi.CodingAgent.Tests` 全量 **3266 / 0 失败**；构建 0 警告 0 错误 |

| 2026-10-18 | **4f-4m-6** | 提交 if-chain + 清零剩余缝——**InteractiveMode 类装配收口**：① 提交 if-chain 闭包本体（TS `setupEditorSubmitHandler` L3179-3375）落 `InteractiveModeSubmit.cs`：`HandleSubmitAsync`（trim/空提交短路）+ `DispatchCommandAsync`（26 个命令分支逐条分派，**清空编辑器时机按 TS 原样**——多数在调用前，export/import/share/copy/name/session/changelog/hotkeys/fork/tree/trust/logout/debug/armin/demented/resume 在后）+ bash 分支（busy 警告恢复原文/历史/`isBashMode` 复位）+ 压缩期排队（扩展命令直通）+ 流式 steer + 正常提交；路由复用既有表驱动 `Route`。② 两个缺失挂载落 `InteractiveMode.Selectors.cs`：`ShowSettingsSelector`（TS L4888-5115，37 回调；http-idle-timeout 按 C267 只落设置+状态文案）与 `ShowModelsSelector`（TS L5357-5465，15s 刷新竞态 + selectionChanged 守卫 + enabledIds 代数 + persist 全选短路）。③ 清零三缝：`custom<T>`/`customAsync<T>` → `ShowExtensionCustomAsync`（TS L2946-3010：草稿保存/overlay 与占位两路/close 幂等 + dispose/工厂失败恢复并 reject）；`checkForAvailableUpdates` → TS 形状（offline 短路 + 失败空表，网络检查本体仍顺延 4e-3c-3，但消除每次 online 启动炸后台任务）；`setupEditorSubmitHandler` → 闭包。④ **摘字段块 pragma**：CS0169/CS0414/CS0649 抑制移除后仍 0 警告。顺手：`IOutputPadSettable` 接口（TS 鸭子探针的编译期版）+ 7 个 transcript 组件实现。测试 +11 例（提交链 9 + 扩展自产组件 2）。**4f-4m 全部收口** | `c0ffee2`（已推送） | `InteractiveModeTests` 全类 **117 / 0**（+11）；`Pi.CodingAgent.Tests` 全量 **3277 / 0 失败**；构建 0 警告 0 错误 |

> **R2 漂移对账（S0–S4）与 S3c 已全部完成。** `packages/ai` 已无已知缺口。
> **4f-3d 选择器组 21/21 全部收口**（Batch C + D + E + F-1～F-5，含 external-editor）。
> 另已修掉 C233（`IComponent.HandleInput` 接口映射）这一影响全部已收口组件的系统性缺陷。
> **4f-3e 组件组 11/11 全部收口**（Batch 1～4）：`mermaid` 以「transformer 骨架 + 引擎注入缝」
> 收口（C258），`grok-mermaid` 引擎本体不移植——它是 pi 仓库外的第三方包，无参照向量可差分。
> 其后仍是原计划 **S5**（4f-4 → 4f-5 → 4g → evals）。
> **4f-4 已拆 13 个子批次**，`4f-4a`（展示层）、`4f-4b`（`tui-renderer.ts`）、
> `4f-4c`（模块级层）、`4f-4d`（诊断与文案叶子层）、`4f-4e`（自动补全辅助层）、
> `4f-4f`（启动横幅与资源展示叶子层）、`4f-4g`（状态指示器决策层）、`4f-4h`（扩展绑定辅助层）、
> `4f-4i`（编辑器提交路由层）、`4f-4j`（消息与渲染决策层）、`4f-4k`（斜杠命令辅助层）、
> `4f-4l`（生命周期叶子层）收口。
> **批次调整**：原定 4f-4d = 类骨架 + 构造器，实测构造器传递依赖整个类
> （`resetExtensionUI` 需 10 个扩展 UI 方法、`rebindCurrentSession` 需 7 个会话
> 生命周期方法，均在 4f-4h/4f-4j），故类骨架顺延为收尾批 **4f-4m**。
> **2026-10-17 清点（4f-4m 动工前）**：对 `interactive-mode.ts` 全类逐成员清点——204 个方法/访问器中
> 59 个已被 4f-4a～4f-4l 以静态叶子覆盖，**剩余 145 个成员 ≈ 4,755 行 TS**（字段块另计），单批不可
> 收口，拆 5 个子批：**-1 骨架与扩展 UI 面（✅ `4f-4m-1`）→ -2 事件与渲染（handleEvent 388 行 +
> renderSessionState）→ -3 生命周期与命令族（init/run/shutdown/stop/信号 + handle*Command）→
> -4 登录认证族 → -5 autocomplete 组装与收口（清零全部缝、摘字段块 pragma）**。
> 4f-4m-1 登记 5 个缝（SetupAutocompleteProvider / RenderCurrentSessionState / HandleEventAsync /
> BindCurrentSessionExtensionsAsync / HandleRightClickPaste），按子批逐个退役。

（后续阶段完成后逐行追加。）
