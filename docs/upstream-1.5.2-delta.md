# 上游 v1.5.2 差异台账（v1.5.0 → v1.5.2）

> 生成时间：2026-11-12
> 参照源：`D:\AI\参考项目\pi`，
> `v1.5.0` = `f9a263655`（2026-11-02）、`v1.5.1` = `dd30101cf`（2026-11-09）、
> `v1.5.2` = `b4afdddf4`（2026-11-10，`origin/stable` 亦指向此提交）。
> 当前检出 `main` = `a40abecaa`（v1.5.2 之后的未发布提交，按既有策略**先不跟**）。
> 目标仓库：`D:\AI\参考项目\pi.net`，迁移起点 `v1.5.0`（tag `1ff667a`，HEAD `fb8cf25`）。
> 口径：`v1.5.0..v1.5.2`，共 **395** 个提交（线性，`v1.5.0` 是 `v1.5.2` 的祖先），
> 其中 **233** 个触及 `packages/`；`apps/` 另有 953 文件 / +191,148 行（p2 原型应用），
> 137 个提交是 `docs(p2)` / `docs(p3)` / `feat(p2)` 等实验稿。

## 0. 结论摘要

**1.5.0 之后确有新的 release：`v1.5.1` 与 `v1.5.2`，本轮对齐到 `v1.5.2`。**
`v1.5.1` 是功能版（消息线程、空闲缓存预热、shell 登录/登出、Mermaid 升级、
一批 compat 开关），`v1.5.2` 是小修版（会话目录消失时的 Fork、Anthropic OAuth 版本串、
`/import` 副本清理）。

**规模（只算 `packages/*/src`，不含测试与文档）**

| 包 | 文件 | 增 | 删 | 端口落点 | 本轮处置 |
| --- | --- | --- | --- | --- | --- |
| chord | 334 | 22,938 | 12,628 | `src/Pi.Chord` | ⏸ 整体不跟（v2 换根，见 §2.3） |
| coding-agent | 75 | 2,773 | 1,497 | `src/Pi.CodingAgent` | 🔧 部分（引号解析、glob `./`） |
| mcp | 19 | 1,531 | 72 | `src/Pi.Mcp` | ⏸ 新增 API 不跟 |
| durable | 60 | 1,053 | 500 | `src/Pi.Durable` | ⏸ 新增 API 不跟 |
| ai | 29 | 817 | 102 | `src/Pi.Ai` | 🔧 主战场，11 项已落地 |
| env | 7 | 213 | 32 | `src/Pi.Env` | ⏸ 新增 daemon 操作不跟 |
| schema | 9 | 381 | 94 | `src/Pi.Ai/Utils/Schema` | 🔧 1 项（NaN/Infinity） |
| telemetry | 3 | 69 | 11 | `src/Pi.Telemetry` | 🔧 1 项（控制字符转义） |
| tui | 6 | 64 | 18 | `src/Pi.Tui` | 🔧 2 项（Alacritty、HTML 注释） |
| codemode | 3 | 17 | 3 | `src/Pi.Codemode` | 🔧 1 项（NaN/Infinity 打印） |
| agent | 1 | 2 | 0 | `src/Pi.Agent` | 🔧 见 §2.2（`disableContinuation` 未跟） |
| evals | — | — | — | `src/Pi.Evals` | ➖ 无 src 变更 |

**三条关键判断**

1. **chord 的 22,938 行不是「增量」而是「换实现」**。1.5.1 把实验性的 Chord v2
   （`src/next`）提升为包根，删除 v1（`src/old`），并追加 sync / 插件模型 / app kit /
   web+tui surfaces / 跨主机遥测等 slice 9~13。1.5.0 台账已定「**chord v2 不跟**」（实验、
   未导出）；1.5.1 把它变成 root 属于同一条实验线的继续，端口 `Pi.Chord` 仍是 **v1**
   （`Context` / `Delta` / `Facets` / `Services` / `Node`），没有 v2 的任何对应物。
   整体替换相当于重写整个 `Pi.Chord`，不是补丁版本该做的事，故 ⏸ 不跟并在此备案。
2. **真正影响端口 published API 的是 `ai` 与少量 `tui`/`durable`/`schema` 行为修复**，
   其余大头（消息线程、缓存预热、MCP OAuth 拆分、env daemon `process`、durable 只读 JSONL
   与逐会话压缩设置）都是新增系统，需要成体系移植，按 1.5.0 台账「未发布/实验/示例不跟」
   与「Node/npm 专有不适用」的既定口径逐项备案（§4）。
3. **模型目录是「数据重导」而非「代码移植」**。1.5.0 已按生成器重导 42 份目录且明确
   「不移植生成器」。1.5.1/1.5.2 的目录改动涉及新 provider / 新模型 / 默认模型切换 /
   Azure 上下文与价档，需上游 `catalog/generate.ts` 连网拉 models.dev 才能完整重导；
   连网重导会把发布时点之外的 live 数据一起带进来，风险不可控。本轮按 changelog 中
   **有明确 .NET 对应物**的三项做定点修正（#7961 / #11742 / #11948，见 §3.6），
   其余目录漂移在 §4 备案。

## 1. 处置标记

沿用 1.5.0 台账：

| 标记 | 含义 |
| --- | --- |
| ✅ 已覆盖 | 逐行核对过端口源码，语义一致（含本轮新落地） |
| 🔧 待移植/本轮落地 | 上游行为变更，已在本轮落到端口 |
| ➖ 不适用 | Node/npm/TypeBox/构建链/安装器专有，C# 侧没有对应物 |
| ⏸ 暂不跟 | 实验性换实现、成体系的新系统、未发布提交、示例 |

## 2. 逐子系统差异

> 各包以 `packages/<pkg>/CHANGELOG.md` 的 1.5.1 / 1.5.2 段为权威清单；
> 提交号见 `git -C D:\AI\参考项目\pi log --oneline v1.5.0..v1.5.2 -- packages/<pkg>`。

### 2.1 ai（29 文件 / +817 / −102）—— 本轮主战场

| 上游项 | 端口落点 | 处置 |
| --- | --- | --- |
| #11793 / #11639 Responses 会话亲和头 `session_id`→`session-id`（1.5.2） | `OpenAiResponses.BuildDefaultHeaders` | ✅ 本轮落地 |
| Anthropic OAuth 报告过时的 Claude Code 版本（1.5.2 `a05e3a077`） | `AnthropicMessages.ClaudeCodeVersion` 2.1.280→2.1.322 | ✅ 本轮落地 |
| #11843 `openai-completions` 不再发 `"tools": []`（破坏性） | `OpenAiCompletions.BuildParams` + 新 compat `RequiresToolsWithToolHistory` | ✅ 本轮落地 |
| #8118 `compat.requiresNonNullAssistantContent` | `OpenAiCompletions.ConvertMessages` + compat | ✅ 本轮落地 |
| #9442 `compat.promptCacheKeyMode`（auto/enabled/disabled） | completions / responses / azure 三处 `prompt_cache_key` | ✅ 本轮落地 |
| #11626 按会话记住 strict 工具超限拒绝 | 新 `StrictToolLimits.cs` + Anthropic / Bedrock 两处调用 | ✅ 本轮落地 |
| #7961 Claude Sonnet 5 不支持 temperature | 42 份目录里所有 `sonnet-5*` 模型补 `supportsTemperature:false` | ✅ 本轮落地（目录） |
| #11742 Azure GPT-6 系列上下文 1,050,000 | `azure.json` 五个模型 | ✅ 本轮落地（目录） |
| #11948 Azure 保留长上下文价档 | `azure.json` 13 个模型补 `cost.tiers` | ✅ 本轮落地（目录） |
| #11879 被丢弃的上游流（"cut short after the reply had begun"）应自动重试 | `src/Pi.Ai/Utils/Retry.cs` 可重试文案表 | ✅ 本轮落地 |
| #11545 Anthropic 消息线程 + `StreamOptions.disableContinuation` + 缓存预热公共 helper | `Pi.Ai` / `Pi.Agent` | ⏸ 不跟（成体系，见 §4.1） |
| `provider.refreshModelsAuth`、lazy OAuth/Bedrock、qwen-token-plan-individual-cn、Microsoft-Decision-1 分类器、Claude Fable 5.2 | 目录 / providers | ⏸ 目录漂移，见 §4.2 |
| #10497 OpenRouter 输出上限、#9557 Anthropic 非 strict `$defs`、#10954 thinking block binding、#11692 OAuth 退出刷新、#11708 Codex SSE 监听、#11695 连接错误根因、#11710 见 tui | 各 api/*.cs | ⏸ 不跟（逐项在 §4.3） |
| `openai-completions` 改名为 "OpenAI Codex" 显示名 | UI 文案 | ➖ 端口无对应 UI |

### 2.2 coding-agent（75 文件 / +2,773 / −1,497）

| 上游项 | 处置 |
| --- | --- |
| #11861 提示模板参数：撇号留在词内、未闭合引号当文本、`\"` 转义 | ✅ `Core/PromptTemplates.cs` + 语料三条旧向量更新 + 3 个新测试 |
| #11807 / #10998 glob 去掉前导 `./`、glob 相对 `path` | 🔧 前半（`./` 剥离）已落地；后半（rg cwd 切到 searchPath）见 §4.4，⏸ 暂不跟 |
| #11975 会话目录消失时 Continue/Fork/Cancel、清理 `/import` 副本（1.5.2） | ⏸ 会话管理成体系，不跟 |
| `pi auth login/logout <provider>` shell 命令 | ⏸ CLI 新命令，不跟 |
| `pi.registerMarkdownTransformer({ messageTypes })`、Mermaid 饼图/样式/可点标签 | ⏸ 扩展 API + TUI 渲染，不跟 |
| `cacheWarming:"idle"` 默认、连续预热合并一行 | ⏸ 依赖 #11545，不跟 |
| #9276（破坏性）grep 上下文行改由 ripgrep 提供、`GrepOperations.readFile` 不再为上下文调用 | ⏸ 端口仍读文件取上下文，属另一条重构线 |
| #9654 read 流式、#8544 edit 保留混合换行、#11738 图片缩放、#11613 bash 超时退出码、#11923 粘贴标记、#9441 光标标记、#11765 Windows Ctrl+V、#11875 `ctrl+o`、#11451 思考档位、#11703 changelog 预发布版本号、#9126/#9222/#9155 会话并发、#11803 `$ref` 搜索、#11901 见 codemode | 见 codemode / tui / §4.3，⏸ 逐项不跟 |
| Node/npm 专有：`pi update` 摘要、扩展 npm 依赖解析、Windows 安装器、Windows PowerShell 5.1、fd 10.5.1 下载、Alacritty 见 tui | ➖ 不适用 |
| 示例扩展（subagent / plan-mode）修复 | ➖ 示例，不跟 |

### 2.3 chord（334 文件 / +22,938 / −12,628）—— ⏸ 整体不跟

1.5.1 把 v2 提为根、删除 v1，并新增 sync 层、插件模型（slice 12）、app kit（slice 13）、
web/tui surfaces（slice 11）、跨主机遥测（slice 9）、live queries、消息与语言、HTTP
默认拒绝、group commit、调用答案跟随状态等。全部是 **Chord v2 的继续实现**，与端口
`Pi.Chord`（v1）无对应关系。1.5.0 台账已定「chord v2 不跟」，本轮沿用：
- 端口 `Pi.Chord` 保持 v1 语义不变（既不删 v1，也不引入 v2）；
- 这些提交不计入「1.5.2 对齐缺口」，而是独立的、需要单独立项的 v2 移植工程；
- 若将来决定移植 v2，应新建台账并以「重写 `Pi.Chord`」立项，而不是补丁批次。

### 2.4 durable（60 文件 / +1,053 / −500）—— ⏸ 新增 API 不跟

`Tx.submission(id)`、逐会话 `CompactionSettings`、JSONL `readOnly` 打开、
`SqliteStorage.commit()` 批量语句、`tool_search` 的 `$ref`、#11892 调度 pass 通知、
code mode 畸形 `$ref` 渲染为 unknown。均为新增系统或内部重构，端口 `Pi.Durable`
在 1.5.0 刚完成窗口化视图大改，本轮不再叠加。

### 2.5 env / mcp（7 / 19 文件）—— ⏸ 新增 API 不跟

env：daemon `process` 操作（`openProcess` / `RemoteProcess`，Windows 也可用）、
abort 不再等失联主机、ssh `ConnectTimeout=15`、Windows `exec` 输出读取器提前停。
mcp：`PipeTransport`、OAuth 两半拆分（`startMcpSignIn` / `completeMcpSignIn` /
`createMcpAuthProvider` / `withRefreshLock`）、从 coding-agent 移入的工具命名/内容限制/配置
校验 helper、SSE/JSON 消息大小限制与 CR-only 换行修复。都是新增 API 或传输层细节，
端口 `Pi.Env` / `Pi.Mcp` 没有对应骨架，逐项备案（§4.3）。

### 2.6 schema / telemetry / tui / codemode / agent

| 包 | 上游项 | 处置 |
| --- | --- | --- |
| schema | `num()` 拒收 NaN/Infinity（#11833 配套） | ✅ `SchemaChecker.MatchesType("number")` 加有限性检查 |
| schema | `unsafe` / `recursive` / `Validator.validate` / Static 泛型 / BigInt / Undefined / `equal` / 相对 `$id` 只应用一次 | ⏸ TypeBox 构造 API 与校验内联优化，端口用明文 JSON Schema，无对应物 |
| telemetry | `renderTrace` 把 C0/C1/DEL 转义成 `\uXXXX` | ✅ `TelemetryRender.cs` 全字段转义 |
| telemetry | `telemetryNow()` 微秒时钟、`renderTrace` 微秒时长 | ⏸ 端口 `StoredSpan` 时间是 `long` 毫秒，改 `double` 是破坏性 public API 且微秒渲染在无微秒时间戳时是空操作（§4.5） |
| tui | #11767 Alacritty 无 `TERM_PROGRAM` 时也启用 OSC 8 | ✅ `TerminalImage.cs` |
| tui | #11710 / #11755 HTML 注释不可见（块级 + 行内） | ✅ `Markdown.cs`（`StripHtmlComments` / `RemoveHtmlCommentBlocks` / 行内前导空白） |
| tui | #11835 Orca 全屏 Kitty 图、#11923 粘贴标记退格、#9441 光标标记、#11710 的 inline HTML 见上 | ⏸ 其余三项不跟 |
| codemode | #11901 `console.*` 与抛出的非 Error 值把 NaN/Infinity 打出来 | ✅ `PreludeSource.format` |
| codemode | `resolveSchemaRef` / 畸形 `$ref` 渲染成 unknown | ⏸ 不跟 |
| agent | `streamProxy()` 转发 `disableContinuation` | ⏸ 依赖 #11545，不跟 |
| evals | 无 src 变更 | ➖ |

## 3. 本轮已落地（含落点与验证）

| # | 上游 | 落点 | 验证 |
| --- | --- | --- | --- |
| 3.1 | 1.5.2 Claude Code 版本串 | `src/Pi.Ai/Api/AnthropicMessages.cs:66` `2.1.280`→`2.1.322` | 常量，构建覆盖 |
| 3.2 | #11793 / #11639 `session-id` | `src/Pi.Ai/Api/OpenAiResponses.cs` `BuildDefaultHeaders` | `OpenAiResponsesTests.SendsHyphenatedSessionIdHeader` |
| 3.3 | #11843 空 tools 数组 | `OpenAiCompletions.cs` `BuildParams` + `RequiresToolsWithToolHistory`（默认 false） | `OpenAiCompletionsWireTests.OmitsEmptyToolsArrayByDefaultEvenWithToolHistory` |
| 3.4 | #8118 `requiresNonNullAssistantContent` | `OpenAiCompletions.cs` compat + `ConvertMessages` | `OpenAiCompletionsWireTests.RequiresNonNullAssistantContentSendsEmptyString` |
| 3.5 | #9442 `promptCacheKeyMode` | completions / responses / azure 三处 | `OpenAiCompletionsWireTests.PromptCacheKeyModeOverridesDetection` |
| 3.6 | #11626 strict 工具会话记忆 | 新 `src/Pi.Ai/Api/StrictToolLimits.cs`，`AnthropicMessages` / `BedrockConverseStream` 调用，`ConstrainedSampling` 转委托 | `StrictToolLimitsTests`（5 用例） |
| 3.7 | #7961 / #11742 / #11948 目录 | 9 份 `src/Pi.Ai/ModelData/*.json`：sonnet-5 温度标志、Azure 5 个 1.05M 上下文、13 个 `cost.tiers` | `ModelCatalogTests.ClaudeSonnet5ModelsDoNotSupportTemperature` / `AzureGptSixModelsUseFoundryWindowAndLongContextTier` |
| 3.8 | #11833 数字有限性 | `src/Pi.Ai/Utils/Schema/SchemaChecker.cs` `MatchesType` | 既有 schema 语料回归 |
| 3.9 | 1.5.1 telemetry 转义 | `src/Pi.Telemetry/TelemetryRender.cs` | `TelemetryRenderAndMetricsTests.EscapesControlCharactersInNamesIdsKeysAndErrors` |
| 3.10 | #11767 Alacritty | `src/Pi.Tui/TerminalImage.cs` | `TerminalImageTests.DetectCapabilities_EnablesHyperlinksForAlacrittyWithoutTermProgram` |
| 3.11 | #11710 / #11755 HTML 注释 | `src/Pi.Tui/Components/Markdown.cs` | `MarkdownHtmlCommentTests`（4 用例，对齐上游对比口径） |
| 3.12 | #11901 NaN/Infinity 打印 | `src/Pi.Codemode/Runtime/PreludeSource.cs` | `JintEngineTests.PrintsNonFiniteNumbersAsNumbersInConsoleOutput` |
| 3.13 | #11861 提示模板引号 | `src/Pi.CodingAgent/Core/PromptTemplates.cs`；`prompt-corpus.json` 三条旧向量按 1.5.1 语义更新 | `PromptCorpusTests` 3 个新用例 + 语料回放 |
| 3.14 | #10998 glob 前导 `./` | `src/Pi.CodingAgent/Core/Tools/GrepTool.cs` `BuildRgArgs` | `GrepArgsTests.ArgsStripLeadingDotSlashFromGlob` |
| 3.15 | #11879 丢弃流的自动重试 | `src/Pi.Ai/Utils/Retry.cs` 文案表 | `RetryOverflowValidationTests.RetryableClassificationMatchesUpstream1_4_0` |

**目录改动的安全性**：`ModelData/*.json` 是压缩单行 JSON；改动前用脚本校验 42 份文件
`json.dumps(separators=(',',':'), ensure_ascii=False) + 原换行` 与磁盘字节完全一致，
只做「按 id 追加/修改键」的定点写入，未引入格式漂移。`git diff --stat` 中每份目录
都是 2 行（1 增 1 删），即单行内改动。

## 4. 不跟的项与理由

### 4.1 成体系的新系统（需要单独立项，不是补丁）
- **Anthropic 消息线程 / 空闲缓存预热 / `StreamOptions.disableContinuation`**（#11545）：
  需要 per-session 线程状态、请求 diff/回退、`cleanupSessionResources` 钩子、agent 层
  `streamProxy` 转发、缓存预热调度器。端口 `Pi.Ai` 的 stream 选项与 `SessionResources`
  骨架都在，但整套行为是新增系统。
- **MCP OAuth 两半 / `PipeTransport` / 工具命名 helper**：`Pi.Mcp` 现有 OAuth 实现是
  单块，拆分会动 public API。
- **env daemon `process` 操作**：新的进程管道协议与 `RemoteProcess` 抽象。
- **durable 逐会话压缩设置 / 只读 JSONL / `Tx.submission` / group commit**。

### 4.2 模型目录数据
上游客目录由 `catalog/generate.ts` 连网拉 models.dev 生成；端口不移植生成器（1.5.0
既定）。1.5.1/1.5.2 新增 `qwen-token-plan-individual-cn` provider、Claude Fable 5.2、
GLM 5.3、Microsoft-Decision-1 分类器、各 provider 默认模型切换（Opus 5.5 / GPT-6.1 Sol /
Gemini 4 Argon 等）。完整重导会连带发布时点之后的 live 数据，属不可控漂移；本轮只落
changelog 中有明确 .NET 对应物的三项（§3.7）。**新增 provider / 新模型 / 默认模型切换
记为已知缺口**，待有离线快照或生成器移植后单独重导。

### 4.3 逐项判定为「暂不跟」的修复
`#10497`（OpenRouter 输出上限）、`#9557`（Anthropic `$defs`）、`#10954`（thinking
block binding）、`#11692`（退出时等 token 刷新）、`#11708`（Codex SSE 监听累积）、
`#11695`（连接错误根因）、`#9654`（read 流式）、`#8544`（edit 混合换行）、`#11738`
（图片缩放）、`#11613`（bash 超时退出码）、`#11923`（粘贴标记退格）、`#9441`（光标
标记）、`#11875`（`ctrl+o`）、`#11451`（思考档位）、`#11703`（预发布版本号 changelog）、
`#9126`/`#9222`/`#9155`（会话并发）、`#11803`（`$ref` 搜索/渲染）、`#11892`（调度 pass）、
`#11710` 之外的 tui 三项（#11835 / #11923 / #9441 重复列出以对齐 changelog）、
`#11975`（Fork 会话）、`pi auth login/logout`、`registerMarkdownTransformer(messageTypes)`、
Mermaid 升级、`ctx.ui.custom(blocked)`、RPC 关闭时序、Windows 安装器/PowerShell 5.1/fd
下载。这些逐项都是独立行为改动，端口相应模块存在但**本补丁批次不逐条追**；理由统一为：
「单点修复，风险与收益不成比例，且不阻塞 1.5.2 对齐的 API 契约」。后续可按 issue 号
单独取用。

### 4.4 grep glob 相对 `path` 的后半
上游把 rg 的 `cwd` 切到 `searchPath`、搜索 `.`，使 `glob: "migrations/main/*.js"` 相对
`path` 生效；同时改了 `formatPath` 用 `path.resolve(searchPath, filePath)`。端口
`GrepTool` 的 `FormatPath` / `GetFileLines` 仍以进程 cwd 解析相对路径，且上下文行取自
`ops.ReadFileAsync`（上游 #9276 已删除这条路径）。两者纠缠，单独改 cwd 会让输出路径
与文件读取同时错位，故本轮只落互不依赖的 `./` 剥离（§3.14），cwd 部分暂不跟。

### 4.5 telemetry 微秒时钟
上游 `StoredSpan.startedMs/endedMs` 与 `event.timeMs` 是 `number`（可含小数），
`telemetryNow()` 用 `performance.now()` 锚到墙上时钟给微秒分辨率。端口这三个字段是
`long` 毫秒；改成 `double` 会破坏 `StoredSpan` / `StoredSpanEvent` 的 public API，
且在无微秒时间戳时 `renderTrace` 的 `microseconds()` 只是恒等变换。**控制字符转义
（安全相关）已落地，微秒时钟记为不跟**。

### 4.6 沿用 1.5.0 台账的既定不跟项
- `v1.5.2..HEAD` 的未发布提交（`main` 上 1.5.2 之后的 p2/p3 实验）：先不跟，等下一个 tag。
- Chord v2（见 §2.3）。
- `apps/` 下的 p2 原型应用与 `docs(p2/p3)` 设计稿（191,148 行）。
- Node/npm/TypeBox/安装器/构建链专有项。
- TS 侧 schema 构造 API（builders / annotations / typebox-static）：端口用明文 JSON Schema。

## 5. 复核方法（下一批动工前）

1. `git -C D:\AI\参考项目\pi log -1` 确认检出；若越过 `b4afdddf4`，用
   `git log --oneline v1.5.2..HEAD` 重新评估（当前 `a40abecaa`）。
2. 表驱动常量逐项 diff：`ModelData/*.json` 的 provider/model 表、`compat` 字段表、
   `Retry.cs` / `Overflow.cs` 模式表。不能只看漂移窗口。
3. 本轮收口：`dotnet build Pi.slnx -m:1` 0 警告 0 错误 → 定向测试 → 全量门禁测试 →
   提交 → 更新本台账与 `CHANGELOG.md`。
4. 验证「有没有漏掉新 release」：`git -C D:\AI\参考项目\pi tag --sort=-v:refname | head`
   与 `origin/stable` 指向；1.5.0 之后为 `v1.5.1`、`v1.5.2`。
