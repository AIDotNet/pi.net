# coding-agent 剩余迁移：现状分析、迁移清单与分阶段计划

> 生成时间：2026-10-13
> 参照源：`D:\AI\参考项目\pi`（`packages/coding-agent`），基线 commit **`428a12bc7`（= `origin/main`）**
> 目标仓库：`D:\AI\参考项目\pi.net`（`src/Pi.CodingAgent`），起始 commit **`f208567`**
> 本文是**执行台账**：每完成一个阶段在此更新「进度台账」小节。逐文件的移植进度与设计差异
> 仍以 [coding-agent-porting-status.md](coding-agent-porting-status.md) 为准（差异编号 `C##` 已用到 `C188`）。

---

## 一、现状快照（2026-10-13 实测）

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

`src/Pi.CodingAgent` 现有 **196 个 `.cs`**（`find . -name '*.cs'` 排除 `obj/`），`wc -l` 合计 **77,589 行**（2026-10-14 4f-3a 后实测；4f-2a 新增 `Modes/Interactive/Theme/` 6 个文件 2,745 行，4f-3a 新增 `Modes/Interactive/Components/` 4 个文件 268 行）。

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
| 4f-2 | `modes/interactive/theme/*`（2,216 行：`theme.ts` 1,160 / `system-theme.ts` 655 / `theme-controller.ts` 253 / `theme-json.ts` 148） | **4f-2a ✅ 已收口**（`theme.ts` 模块级半边 + `system-theme.ts` + `theme-json.ts` + `Theme` 类 + TUI 助手 + OSC 查询链）；`Theme` 占位已删；`MarkdownTheme` / `SelectListTheme` / `SettingsListTheme` 三个形状已由 `ThemeHelpers` 产出。**4f-2b 余**：`theme-controller.ts`，依赖 4f-4 |
| 4f-3 | `modes/interactive/components/*`（实测 **11,652 行** / 49 文件） | 每个组件一个测试类；选择器交互路径。**4f-3a ✅ 已收口**（原语层：`keybinding-hints` + `visual-truncate`，解 4f-1 阻塞；另附 `dynamic-border` + `themed-text`）。**4f-3b 余**：`diff.ts`（需先补 `JsDiff.DiffWords`，4f-1 的 `edit` renderer 依赖）；**4f-3c 余**：消息类组件；**4f-3d 余**：选择器（含 `config-selector` / `settings-selector` / `session-selector` / `tree-selector` 四个 900+ 行大件）；**4f-3e 余**：`footer` / `tool-execution` / `bash-execution` / `armin` / `easter-egg-3d` / `index.ts` |
| 4f-1 | `core/tools/renderers/*`（1,033 行 / 8 文件）+ `core/tools/render-utils.ts`（112 行） | renderers 逐文件对照；`ToolRenderers` 补 `RenderCall` / `RenderResult`（当前只有 `RenderShell`，差异 C85） |
| 4f-4 | `modes/interactive/interactive-mode.ts`（7,072 行） | 主循环 / 键位 / 视口 / 共享 |
| 4f-5 | `modes/rpc/*` + `print-mode` + `json-*`（~2,300 行） | RPC 端到端（Unix 域套接字回放） |

### 阶段 P6：4g 入口与实验层（拆 3 个子阶段）

`4g-1` `cli/*` → `4g-2` `main.ts` / `index.ts` / `package-manager-cli.ts` / `rpc-entry.ts` → `4g-3` `experimental/*` + `bun/*` + `client/*`。

### 阶段 P7：evals（1,300 行，新建项目）

---

## 五、风险与阻塞

| # | 风险 | 影响 | 处置 |
| --- | --- | --- | --- |
| R1 | **推送通道间歇不可用（本轮再次发生，已有可用绕行）** | 阶段产出无法上远端 | 实测（2026-10-13 上午）：`https` 经本机代理对 `github.com` 返回 **502 CONNECT tunnel failed**；`ssh -p 443 git@ssh.github.com` 可达但 **publickey 未授权**。**2026-10-13 下午代理短暂恢复**：`git push origin main` 成功（`f208567..7b942b3`）。**2026-10-13 晚再次 502（连续 5 次重试均失败，`github.com` CONNECT 502 / `Empty reply`）**，但同代理下 `api.github.com` 正常（200）→ 改用 **REST API 复制提交**绕行（见下），已成功推送 `bb874f6`；**P2b 推送时代理又恢复**，`git push origin main` 成功（`e70f5dd..02707fd`）。**4e-3c-2a / 4e-3c-2b 推送时代理再次 502**（`fatal: unable to access … schannel: server closed abruptly`），改用下述 REST API 绕行**一次推上两个提交**（`82b47aa` + `05e9c58`，远端 head `05e9c58d`）。**P4a 推送时代理/SSH 恢复**：`git push origin main`（走 `git@github.com`）一次成功（`696dab5..bafb72e`）。<br>**绕行做法（可复用）**：`GET /repos/:o/:r/git/ref/heads/main` 确认远端 HEAD == 本地 `HEAD^`；对每个改动文件 `POST /git/blobs`（内容取 `git cat-file blob <sha>:<path>` 的原始字节，base64），校验返回 SHA == 本地 blob SHA；`POST /git/trees`（`base_tree` = 父树）校验树 SHA == 本地树 SHA；`POST /git/commits`（message / author / committer / date 与本地**逐字节一致**）校验提交 SHA == 本地 SHA；最后 `PATCH /git/refs/heads/main`。因 SHA 完全相同，本地与远端天然同步，无需 `fetch`；本地 `refs/remotes/origin/main` 用 `git update-ref` 手工对齐。<br>**其它出路**：把 `~/.ssh/id_rsa.pub` 加到 GitHub 账号后改走 `ssh://git@ssh.github.com:443/xuzeyu91/pi.net.git`。 |
| R2 | 参照检出漂移 | 语义错位（9-0 已发生过一次） | 每阶段动工前 `git -C D:\AI\参考项目\pi log -1` 确认仍在 `428a12bc7` |
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

| 2026-10-14 | 4f-3a | 交互组件原语层：`Modes/Interactive/Components/` 新增 `KeybindingHints.cs`（`formatKeyText` / `keyText` / `keyDisplayText` / `keyHint` / `rawKeyHint`）、`VisualTruncate.cs`（`truncateToVisualLines` + 按宽度缓存的 `VisualLinePreview`）、`DynamicBorder.cs`、`ThemedText.cs`；`keybinding-hints` + `visual-truncate` 解 4f-1（renderers）的直接阻塞；新登记 C197（平台探测缝）/C198（`"start"|"end"` 联合退化）；**顺带修 `McpOAuthTests` 一处 flaky 断言**（P4b-2 遗留：同一条断言连读三次 `NowMs()`，跨毫秒边界 → 1000/999） | 本轮提交 | 四个新测试类 **35 / 0 失败**；`Pi.CodingAgent.Tests` 全量 **1924 / 0 失败**（17m13s，1889 + 35）；构建 0 警告 0 错误；变异套件 `keybinding-hints` / `visual-truncate` / `dynamic-border` / `themed-text` 共 18 处锚点 |

（后续阶段完成后逐行追加。）
