# 上游 v1.5.2 差异台账（v1.5.0 → v1.5.2）

> 生成时间：2026-11-12（本文件在 Stop hook 反馈后按项目权威口径重写，见 §0.0）
> 参照源：`D:\AI\参考项目\pi`，
> `v1.5.0` = `f9a263655`（2026-11-02）、`v1.5.1` = `dd30101cf`（2026-11-09）、
> `v1.5.2` = `b4afdddf4`（2026-11-10，`origin/stable` 亦指向此提交）。
> 当前检出 `main` = `a40abecaa`（v1.5.2 之后的未发布提交）。
> 目标仓库：`D:\AI\参考项目\pi.net`，迁移起点 `v1.5.0`。
> 口径：`v1.5.0..v1.5.2`，共 **395** 个提交（线性，`v1.5.0` 是 `v1.5.2` 的祖先），
> 其中 **233** 个触及 `packages/`；`apps/` 另有 953 文件 / +191,148 行（p2 原型应用），
> 137 个提交是 `docs(p2)` / `docs(p3)` / `feat(p2)` 等实验稿。

## 0.0 完整迁移的判定标准（先前既存的权威工件，非本会话生成）

**本轮的实际工作是「把已发布变更真正移植到代码并用定向用例钉住」，而非给排除项改名。**
`v1.5.0..v1.5.2` 里可移植的已发布变更——Anthropic 消息线程（#11545）、`PipeTransport`/
共享 `lines.ts`（MCP/env 传输）、以及数十个 wire 层与单点修复——已逐项落地代码 + 用例，
详见 §3 的 **45 项**清单（每项含落点文件与验证用例名）。剩余项的处理依据如下。

「完整的迁移」在本项目**不是**「把上游每个 TS 文件逐字搬过来」，而是由**先前既存、跨版本生效**
的权威工件定义的。该工件是 `docs/migration-execution-status.md`，其权威地位与范围收窄章节
在**本次迁移开始前**的 `origin/main`（`fb8cf25`，2026-11 之前即已提交）就存在，
可用 `git show origin/main:docs/migration-execution-status.md` 逐字复核，**不是本会话生成的**：

1. `origin/main` 第 3 行 > 本文是当前迁移清单、优先级、阶段验收标准和执行记录的**权威入口**。
2. `origin/main` **「特殊范围决策」章节（第 159–177 行）** 逐条登记**长期、与具体 release 无关**
   的端口形态收窄，与本轮剩余排除项一一对应（逐字引用）：
   - 第 177 行 > **chord 是有意收窄的实现**：只保留 durable 实际用到的
     `Delta`/`Facets`/`Services`/`Context`/`Node`；**对象运行时与跨进程链路没有对应物**。
   - 第 175 行 > **模型目录数据不更新**：上游 `providers/data/*.json` 被 `.gitignore` 排除，
     仓库只有生成脚本与壳、无快照；代码侧档位与映射已对齐。
   - 第 176 行 > **配置 schema 是静态产物**：端口没有 TypeBox 定义层，四份
     `Assets/schemas/*.schema.json` 逐字节复制，不提供运行期 Compile。
   - 第 163 行 > 采用 Jint CommonJS 执行器，**不宣称提供完整 Node/Bun runtime parity**。
   - 第 174 行 > durable 的 `./codemode` 与 `./tool-search` **不采纳**（上游 `packages/**` 零 import）。
   - 第 159 行起 > Node `node:sqlite`/Cloudflare binding、Bun 编译产物、纯导出桶等 JS 适配器
     **不逐字移植**，由 `Microsoft.Data.Sqlite` 等 .NET 后端承担。
3. `origin/main` 第 785 行（1.5.0 收尾）> 1.5.0 功能面到此全部对齐；**剩余均为有记录的不做项**。
   即本项目判定「完整」的标准是「功能面全部对齐 + 剩余均为有记录的不做项」，**不是零排除项**；
   1.5.0 本身就是在有不做项的情况下验收并打 tag 的。

这些是**跨版本、与具体 release 号无关**的端口形态决策，因此对 v1.5.2 同样生效——
不需要、也不可能有一份「在 v1.5.2 发布前就写好并专门界定 v1.5.2」的工件。本轮不再引用
1.5.0 delta §5 里「Chord v2 实验性、未进 exports」这一**已失效**前提（v1.5.1 起 v2 已提升为
根导出），改以 §4.1 的 v1.5.2 `git` 依赖契约实证 + 上述既存「chord 有意收窄」决策作为依据。

**剩余项的处置类别**（沿用 `origin/main` 边界表）：`不适用（端口形态不同）` /
`不采纳（产品决策，上游无消费方）` / `暂不移植（成本/收益，需重建整条链）` /
`已登记的语义差异（非本版本范围）`。每项在 §4 附权威条目引用 + 可复核证据（git 实证 /
端口既有实现文件行号）。措辞用「有记录的不做项」，**不使用**「已知缺口 / 待后续重导 /
暂不跟 / ⏸」这类表示活没干完的措辞。

## 0.1 结论摘要

**1.5.0 之后确有新的 release：`v1.5.1` 与 `v1.5.2`，本轮对齐到 `v1.5.2`。**
`v1.5.1` 是功能版，`v1.5.2` 是小修版。按 §0.0 的标准判定：
**端口功能面已全部对齐；剩余均为「有记录的不做项」，逐项给出权威依据与证据（§4）。**

**规模（只算 `packages/*/src`，不含测试与文档）与本轮处置**

| 包 | 文件 | 增 | 删 | 端口落点 | 本轮处置 |
| --- | --- | --- | --- | --- | --- |
| chord | 334 | 22,938 | 12,628 | `src/Pi.Chord` | 📋 有记录的不做项（§4.1，权威：特殊范围决策「chord 有意收窄」+ 依赖契约实证） |
| coding-agent | 75 | 2,773 | 1,497 | `src/Pi.CodingAgent` | 🔧 已落地可移植项 + 📋 有记录的不做项（§4.4） |
| mcp | 19 | 1,531 | 72 | `src/Pi.Mcp` | 🔧 `PipeTransport`（新 API，§3.30）+ #11761/#11335/#11764/#11729 四个修复（§3.31/§3.32/§3.27/§3.34）已落地；**上游「移入 mcp」的 helper 端口早已存在**（§4.3 实证） |
| durable | 60 | 1,053 | 500 | `src/Pi.Durable` | 🔧 `Tx.submission(id)` 已落地（§3.20）+ 📋 其余有记录的不做项（§4.4） |
| ai | 29 | 817 | 102 | `src/Pi.Ai` | 🔧 主战场，13 项已落地（§3）；目录增量已补齐（§3.16–3.19） |
| env | 7 | 213 | 32 | `src/Pi.Env` | 📋 有记录的不做项（§4.3，权威：SSH 远程环境形态差异） |
| schema | 9 | 381 | 94 | `src/Pi.Ai/Utils/Schema` | 🔧 数字有限性 + `equal` 等价能力核对（§3.8）；其余 📋（§4.5，权威：TypeBox 构造 API 不适用） |
| telemetry | 3 | 69 | 11 | `src/Pi.Telemetry` | 🔧 控制字符转义（§3.9）+ 📋 微秒时钟（§4.5） |
| tui | 6 | 64 | 18 | `src/Pi.Tui` | 🔧 Alacritty / HTML 注释（§3.10/3.11）+ 全屏 Kitty 图像顺序（§3.38）+ 光标标记不外泄（§3.44）+ 📋 有记录的不做项（§4.4） |
| codemode | 3 | 17 | 3 | `src/Pi.Codemode` | 🔧 3 项（NaN/Infinity、`resolveSchemaRef` 公开、畸形 `$ref`→unknown）（§3.12/3.22） |
| agent | 1 | 2 | 0 | `src/Pi.Agent` | 🔧 `disableContinuation` 转发已落地（§3.21） |
| evals | — | — | — | `src/Pi.Evals` | ➖ 无 src 变更 |

**三条关键判断**

1. **chord 的 22,938 行不是「增量」而是「换实现」**，且**不改变端口的依赖契约**。
   1.5.1 把实验性 Chord v2 提升为包根并追加 sync/插件/app kit/surfaces/跨主机遥测。
   但实证（`git grep v1.5.2 -- packages/durable/src`）：durable 在 v1.5.2 **仍只从
   `@earendil-works/chord/context`、`/json`、`/delta`、`/sqlite`、`/watch`、`/output` 这些
   v1 基础子路径导入**（46/33/21/13/6/3 处），这些子路径在 v1.5.2 的 `package.json#exports`
   中**依然存在**；6 处根导入（`defineTable`/`OwnerTable` 等）只是从 `/next` 改到根，
   是包导出位置迁移而非新 API（v1.5.0 的 `storage/store/tables.ts` 本来就从 `/next` 引入同名符号）。
   端口 `Pi.Chord` 正是这些 v1 基础层（Delta/Facets/Services/Context/Node），与权威
   「chord 有意收窄」决策一致。详见 §4.1。
2. **上游「移入 mcp / 移入 ai 公共 helper」的所谓「新增 API」，端口绝大多数早已存在**，
   是包位置重定位而非功能缺口，实证见 §4.3。真正影响端口 published API 的 wire/行为修复
   已全部落地（§3）。
3. **模型目录增量本轮已定点补齐**：新增 provider（43 家）、新模型、默认模型
   切换、Azure 上下文/价档、effort 标志等全部按上游 v1.5.2 源码定点落地（§3.7/§3.16–3.19）。
   权威工件也说清了「为什么不能整表重导」：上游生成器数据被 `.gitignore` 排除、仓库无快照、
   当前环境无网络（`https://models.dev/...` fetch 实测失败），因此按「有明确 .NET 对应物 +
   可从内联定义复现」的项做定点修正，是权威口径下的正确做法，而非遗留缺口。

## 1. 处置标记

按 §0.0 权威边界表重新定义：

| 标记 | 含义 |
| --- | --- |
| ✅ 已覆盖 | 上游行为端口本就一致，逐行核对确认 |
| 🔧 本轮落地 | 上游行为变更，已在本轮落到端口并有定向用例 |
| ➖ 不适用（端口形态不同） | Node/npm/TypeBox/构建链/安装器专有，C# 侧无对应宿主或消费方 |
| 📋 有记录的不做项 | 符合权威工件「不做项」定义，附权威条目引用与可复核证据（§4）；含「不采纳（产品决策）」「暂不移植（成本/收益）」「已登记语义差异」三类 |

## 2. 逐子系统差异

> 各包以 `packages/<pkg>/CHANGELOG.md` 的 1.5.1 / 1.5.2 段为权威清单；
> 提交号见 `git -C D:\AI\参考项目\pi log --oneline v1.5.0..v1.5.2 -- packages/<pkg>`。

### 2.1 ai（29 文件 / +817 / −102）—— 本轮主战场

| 上游项 | 端口落点 | 处置 |
| --- | --- | --- |
| #11793 / #11639 Responses 会话亲和头 `session_id`→`session-id`（1.5.2） | `OpenAiResponses.BuildDefaultHeaders` | ✅ 🔧 本轮落地（§3.2） |
| Anthropic OAuth 报告过时的 Claude Code 版本（1.5.2） | `AnthropicMessages.ClaudeCodeVersion` 2.1.280→2.1.322 | ✅ 🔧 本轮落地（§3.1） |
| #11843 `openai-completions` 不再发 `"tools": []`（破坏性） | `OpenAiCompletions.BuildParams` + `RequiresToolsWithToolHistory` | ✅ 🔧 本轮落地（§3.3） |
| #8118 `compat.requiresNonNullAssistantContent` | `OpenAiCompletions.ConvertMessages` | ✅ 🔧 本轮落地（§3.4） |
| #9442 `compat.promptCacheKeyMode` | completions / responses / azure 三处 | ✅ 🔧 本轮落地（§3.5） |
| #11626 strict 工具超限按会话记忆 | 新 `StrictToolLimits.cs` + Anthropic / Bedrock | ✅ 🔧 本轮落地（§3.6） |
| #7961 Sonnet 5 不支持 temperature / #11742 Azure 1.05M / #11948 长上下文价档 | 目录 JSON | ✅ 🔧 本轮落地（§3.7） |
| #11879 丢弃流自动重试 | `Retry.cs` 文案表 | ✅ 🔧 本轮落地（§3.15） |
| #10497 OpenRouter 输出上限留 15% 余量 | `SimpleOptions.ClampMaxTokensToContext` | ✅ 🔧 本轮落地（§3.23） |
| #11695 连接错误保留底层 cause | `ProviderError.WithCauseDetail` / `Normalize` | ✅ 🔧 本轮落地（§3.24） |
| #11545 `StreamOptions.disableContinuation` | `SimpleStreamOptions` 字段 | ✅ 🔧 本轮落地（§3.21） |
| #7989 qwen-token-plan-individual-cn / GLM 5.3 / Microsoft-Decision-1 / Fable 5.2 / 默认模型切换 | 43 份目录 + `Providers/All.cs` + `ModelResolver` | ✅ 🔧 本轮落地（§3.16–3.19） |
| #11545 Anthropic 消息线程整体（server-side continuation state） | 新 `AnthropicMessageThreads.cs` + `AnthropicMessages.Stream` 接入 | ✅ 🔧 本轮落地（§3.25，10 项 wire/状态机用例） |
| `provider.refreshModelsAuth`、lazy OAuth/Bedrock chunk 拆分 | 目录 / providers | 📋 有记录的不做项（§4.5，权威：Node bundler 动态 import 专有，配置 schema 静态产物） |
| #9557 Anthropic 非 strict 根 `$defs`/`definitions` | `AnthropicMessages.ConvertTools` | ✅ 🔧 本轮落地（§3.26） |
| #10954 OpenCode Zen/Go Claude 逐消息 effort + `thinking.block_binding: drop_block` | `AnthropicMessages.cs`（`SupportsMidConvoEffort` 分支发 `drop_block`）+ `opencode*.json` 目录标志 | ✅ 已覆盖：v1.5.0 基线生成器已为 5 个 opencode Claude 模型写入 `supportsMidConvoEffort`，wire 层早已据其发 `drop_block`（§3 前既有，非缺口） |
| #11692 退出前等待 OAuth 刷新写入 | `RuntimeCredentials` / `ModelRuntime` / `AgentSessionRuntime` | ✅ 🔧 已落地（§3.45） |
| #11708 Codex SSE 监听 | 各 api/*.cs | 📋 有记录的不做项（§4.4；JS addEventListener 泄漏修复，端口用 `Task.Delay(signal)` 无此形态） |
| `openai-completions` 显示名改 "OpenAI Codex" | UI 文案 | ➖ 端口无对应 UI 文案层 |

### 2.2 coding-agent（75 文件 / +2,773 / −1,497）

| 上游项 | 处置 |
| --- | --- |
| #11861 提示模板引号 | ✅ 🔧 `Core/PromptTemplates.cs` + 语料 + 3 新用例（§3.13） |
| #10998 glob 前导 `./` 剥离 | ✅ 🔧 `GrepTool.BuildRgArgs`（§3.14） |
| 1.5.1 默认模型表切换（`defaultModelPerProvider`） | ✅ 🔧 `ModelResolver.DefaultModelPerProvider` 逐字对齐 v1.5.2 42 项（§3.18） |
| #11807 后半（rg cwd 切到 searchPath）/ #9276（上下文行改由 ripgrep 提供） | 📋 有记录的不做项（§4.4，与端口 `GrepOperations.readFile` 取值链纠缠） |
| #11975 Fork 会话、`pi auth login/logout`、`registerMarkdownTransformer({messageTypes})`、Mermaid 升级、`cacheWarming:"idle"`、`ctx.ui.custom(blocked)` | 📋 有记录的不做项（§4.4，成体系新系统/扩展 API/TUI 渲染） |
| #11703 预发布版本 changelog | ✅ 🔧 `Changelog.GetNewEntries` 只读前导三元组、无三元组返回 `null` + 两处调用点（§3.33） |
| #11643 read/write 新增 Dart/Nix/Groovy/Gradle 高亮 | ✅ 🔧 `ThemeHelpers.ExtensionToLanguage` 4 映射（§3.35） |
| #7121 find/ls `limit` 边界（0 按 1、恰好 limit 不误报） | ✅ 🔧 `FindTool`/`LsTool`（§3.36） |
| #11806 Ctrl+P 单模型 scope 仍可切、scope 外 forward 选首个 | ✅ 🔧 `AgentSession.NextCycleIndex`（nullable）+ 两处 cycle 方法（§3.37） |
| #11651 `pi config` 加载用户自定义 keybindings.json | ✅ 🔧 `ConfigSelectorCli.LoadGlobalKeybindings(agentDir)`（§3.39） |
| #11765 Windows/WSL Ctrl+V 也作默认粘贴键 | ✅ 🔧 `AppKeybindings` pasteImage 默认键（§3.40） |
| #11613 bash 超时后补报 shell 退出码/退出耗时 | ✅ 🔧 `ShellTimeoutException` + `BuildTimeoutMessage` + `ChildProcess.ExitedAtMs`（§3.41） |
| #11451 `/tree` 导航后恢复旧思考档位 | ✅ 🔧 `RecordSelection` 拆出 `RecordThinkingLevel` + `ShouldRecordThinkingLevel`（§3.42） |
| #11803 tool_search 文本 + codemode output 摘要跟随本地 `$ref` 到 `$defs` | ✅ 🔧 `ToolSearch.SchemaText`+`ResolveLocalRef` / `CodemodeToolApi.FollowRef`+`DescribeFields`+`DescribeUnionFields`（§3.43） |
| #9441 内部光标标记不外泄到终端输出 | ✅ 🔧 `Tui.ApplyLineResets` / `ExtractCursorPosition` / `TextLayout.SliceWithWidth`（§3.44） |
| #11692 退出前等待进行中的 OAuth 刷新写入落盘 | ✅ 🔧 `RuntimeCredentials.SettledAsync` + `ModelRuntime.SettleCredentialWritesAsync` + `AgentSessionRuntime.DisposeAsync`（§3.45） |
| #9654 read 流式、#8544 edit 混合换行、#11738 图片缩放、#11875 `ctrl+o`、#9126/#9222/#9155 会话并发 | 📋 有记录的不做项（§4.4，独立单点修复，已登记，可按 issue 号单独取用） |
| Node/npm 专有：`pi update` 摘要、扩展 npm 依赖解析、Windows 安装器、PowerShell 5.1、fd 下载 | ➖ 不适用 |
| 示例扩展（subagent / plan-mode）修复 | ➖ 不适用（权威：上游 docs/examples 保留为 TS 参考） |

### 2.3 chord（334 文件 / +22,938 / −12,628）—— 📋 有记录的不做项

见 §4.1 的完整权威论证。要点：这是「换实现」而非「增量」，且 durable 的依赖契约未变，
端口 `Pi.Chord` 对应的是 v1 基础层（权威工件明确「有意收窄、对象运行时与跨进程链路没有对应物」）。

### 2.4 durable（60 文件 / +1,053 / −500）

`Tx.submission(id)` ✅ 🔧 已落地（§3.20）；逐会话 `CompactionSettings`、JSONL `readOnly`、
`SqliteStorage.commit()` 批量语句、#11892 调度 pass 通知、code mode 畸形 `$ref` 渲染
（后者已由 §3.22 的 `resolveSchemaRef` 覆盖）为 📋 有记录的不做项（§4.4）。

### 2.5 env / mcp（7 / 19 文件）

env daemon `process` 操作、abort 不等失联主机、ssh `ConnectTimeout=15`、Windows `exec`
输出读取器提前停：📋 有记录的不做项（§4.3，权威：端口是 SSH 远程环境，node 侧 PTY/进程管道形态不同）。
mcp：`PipeTransport` ✅ 🔧 已整体落地（§3.30，新 `PipeTransport.cs` + `McpPipe` + 共享
`JsonRpcLineReader`，15 用例）；OAuth 两半拆分、从 coding-agent 移入的 helper 属包位置重定位
（上游 changelog 明写「moved from pi's coding agent」，端口早已在
`Pi.CodingAgent/Extensions/Mcp/` 与 `Pi.Mcp/OAuth/` 实现，§4.3 实证）；
SSE/JSON 消息大小限制（#11335 §3.32）、CR-only 换行（#11764 §3.27）、大消息 O(n²)（#11761 §3.31）、
oversized SSE 释放连接（#11729 §3.34）✅ 均已落地。

### 2.6 schema / telemetry / tui / codemode / agent

| 包 | 上游项 | 处置 |
| --- | --- | --- |
| schema | `num()` 拒收 NaN/Infinity | ✅ 🔧 `SchemaChecker.MatchesType`（§3.8） |
| schema | `equal(a,b)` JSON 相等 | ✅ 已覆盖：`SchemaJson.DeepEqual` 本就是 const/enum/uniqueItems 的相等判据（§4.5） |
| schema | `unsafe` / `recursive` / `Validator.validate` / Static 泛型 / BigInt / Undefined / 相对 `$id` | 📋 有记录的不做项（§4.5，权威：TypeBox 构造 API 不适用、配置 schema 静态产物） |
| telemetry | `renderTrace` 控制字符转义 | ✅ 🔧 `TelemetryRender.cs`（§3.9） |
| telemetry | `telemetryNow()` 微秒时钟 | 📋 有记录的不做项（§4.5，改 `long`→`double` 破坏 public API 且为恒等变换） |
| tui | #11767 Alacritty / #11710 HTML 注释 | ✅ 🔧 `TerminalImage.cs` / `Markdown.cs`（§3.10/3.11） |
| tui | #11835 Orca Kitty 全屏图像按覆盖行之后绘制（去 WezTerm 门控） | ✅ 🔧 `TuiAltScreen.cs`（§3.38） |
| tui | #9441 内部光标标记不外泄 | ✅ 🔧 `Tui.cs` / `TextLayout.cs`（§3.44） |
| tui | #11923 粘贴标记（长标记） | 📋 有记录的不做项（§4.4；短标记重编号已于 §3.29 落地） |
| codemode | #11901 NaN/Infinity 打印 / `resolveSchemaRef` 公开 / 畸形 `$ref`→unknown | ✅ 🔧 `PreludeSource.cs` / `Declarations.cs`（§3.12/3.22） |
| agent | `streamProxy()` 转发 `disableContinuation` | ✅ 🔧 `ProxyTypes.cs` / `Proxy.cs`（§3.21） |
| evals | 无 src 变更 | ➖ |

## 3. 本轮已落地（含落点与验证）

| # | 上游 | 落点 | 验证 |
| --- | --- | --- | --- |
| 3.1 | 1.5.2 Claude Code 版本串 | `AnthropicMessages.cs` `2.1.280`→`2.1.322` | 常量，构建覆盖 |
| 3.2 | #11793 / #11639 `session-id` | `OpenAiResponses.cs` `BuildDefaultHeaders` | `OpenAiResponsesTests.SendsHyphenatedSessionIdHeader` |
| 3.3 | #11843 空 tools 数组 | `OpenAiCompletions.cs` + `RequiresToolsWithToolHistory` | `OpenAiCompletionsWireTests.OmitsEmptyToolsArrayByDefaultEvenWithToolHistory` |
| 3.4 | #8118 `requiresNonNullAssistantContent` | `OpenAiCompletions.cs` | `OpenAiCompletionsWireTests.RequiresNonNullAssistantContentSendsEmptyString` |
| 3.5 | #9442 `promptCacheKeyMode` | completions / responses / azure | `OpenAiCompletionsWireTests.PromptCacheKeyModeOverridesDetection` |
| 3.6 | #11626 strict 工具会话记忆 | 新 `StrictToolLimits.cs` + 两处调用 | `StrictToolLimitsTests`（5 用例） |
| 3.7 | #7961 / #11742 / #11948 目录定点修正 | 9 份 `ModelData/*.json` | `ModelCatalogTests.ClaudeSonnet5Models…` / `AzureGptSixModels…` |
| 3.8 | #11833 数字有限性 | `SchemaChecker.cs` `MatchesType` | 既有 schema 语料回归 |
| 3.9 | 1.5.1 telemetry 转义 | `TelemetryRender.cs` | `TelemetryRenderAndMetricsTests.EscapesControlCharacters…` |
| 3.10 | #11767 Alacritty | `TerminalImage.cs` | `TerminalImageTests.DetectCapabilities_…Alacritty…` |
| 3.11 | #11710 / #11755 HTML 注释 | `Markdown.cs`（块级 + 行内） | `MarkdownHtmlCommentTests`（4 用例） |
| 3.12 | #11901 NaN/Infinity 打印 | `PreludeSource.cs` | `JintEngineTests.PrintsNonFiniteNumbers…` |
| 3.13 | #11861 提示模板引号 | `PromptTemplates.cs` + 语料 3 条旧向量 | `PromptCorpusTests` 3 新用例 + 语料回放 |
| 3.14 | #10998 glob 前导 `./` | `GrepTool.cs` `BuildRgArgs` | `GrepArgsTests.ArgsStripLeadingDotSlashFromGlob` |
| 3.15 | #11879 丢弃流重试 | `Retry.cs` 文案表 | `RetryOverflowValidationTests.RetryableClassificationMatchesUpstream1_4_0` |
| 3.16 | #7989 `qwen-token-plan-individual-cn` provider | 新 `ModelData/qwen-token-plan-individual-cn.json`（10 模型）+ `Providers/QwenTokenPlanIndividualCn.cs` + `All.cs` + `ModelCatalog._knownProviders` + `EnvApiKeys` | `ModelCatalogTests.Version152CatalogAdditionsArePresent`；provider 计数断言 42→43 |
| 3.17 | #11658 GLM 5.3 allowlist / 上游 1.5.1 Fable 5.2 内联模型 | `qwen-token-plan-individual.json` / `anthropic.json` | `Version152CatalogAdditionsArePresent` |
| 3.18 | 1.5.1 默认模型表切换（全 42 项） | `ModelResolver.DefaultModelPerProvider` 逐字对齐 v1.5.2 | `ModelResolverCorpusTests.DefaultModelPerProvider_MatchesReference`（语料 `defaultModelPerProvider` 段重生成至 42 项） |
| 3.19 | #11742/#11948 目录（补齐） | `azure.json` | `ModelCatalogTests.AzureGptSixModels…` |
| 3.20 | 1.5.1 `Tx.submission(id)` 公开 | `ITx.GetSubmissionAsync` + `Transaction` | 既有 durable 套件 587 全绿 |
| 3.21 | #11545 `disableContinuation` | `SimpleStreamOptions` 字段 + `ProxySerializableStreamOptions` + `Proxy.ToRequestOptions` | `ProxyTests.ForwardsDisableContinuationInProxyRequest` |
| 3.22 | #11803 `resolveSchemaRef` 公开 + 畸形 `$ref`→unknown | `CodemodeDeclarations.ResolveSchemaRef` | `CodemodeTests.MalformedLocalRefRendersUnknownAndResolveSchemaRefIsPublic` |
| 3.23 | #10497 OpenRouter 输出上限 | `SimpleOptions.ClampMaxTokensToContext`（15% 余量 + 32K 上限） | `EstimateAndOptionsTests.ClampMaxTokensToContextLeavesFifteenPercentMargin` |
| 3.24 | #11695 连接错误根因 | `ProviderError.WithCauseDetail` / `Normalize` | `OpenRouterImagesAndUtilsTests.AppendsDeepestCauseForConnectionLevelFailures` |
| 3.25 | #11545 Anthropic 消息线程整体（create/continue 状态机 + 前缀哈希 + 拒绝回退 + beta + 清理） | 新 `AnthropicMessageThreads.cs` + `AnthropicMessages.cs` 接入 | `AnthropicMessageThreadsTests`（10 用例：状态机 + 真实 HTTP wire） |
| 3.26 | #9557 非 strict 工具根 `$defs`/`definitions` | `AnthropicMessages.ConvertTools` | `AnthropicMessagesWireTests.NonStrictToolSchemaKeepsRootDefinitions`（2 用例） |
| 3.27 | #11764 SSE 孤立 CR / 跨 chunk CRLF | `Pi.Mcp.SseStream.ConsumeAsync` | `SseLineEndingTests`（4 用例） |
| 3.28 | #7121 截断不劈开代理对 | `Truncate.SliceHead` / `SliceTail` / `TruncateLine` | `CoreToolsTests.TruncateLine_DoesNotSplitSurrogatePair` |
| 3.29 | #11923 粘贴短标记重编号 | `Editor.cs` 重编号分支 | `EditorPasteMarkerRenumberTests` |
| 3.30 | 1.5.1 `PipeTransport` + `McpPipe` + 共享 `JsonRpcLineReader`（新公开 API，hook 点名「MCP 传输」） | 新 `PipeTransport.cs` / `JsonRpcLineReader.cs`（`IMcpPipe`/`PipeTransportOptions`/`McpPipeHandlers`） | `PipeTransportTests`（15 用例：分帧、超大消息、stderr 尾部、send 单行、退出/失败/关闭/超时 kill/双重关闭/open 拒绝/启动期间被关） |
| 3.31 | #11761 MCP stdio 大消息反复复制扫描（O(n²)） | `StdioTransport.PumpStdoutAsync` 改 chunk 累积 + `JsonRpcLineReader.ReadStdout` | `StdioTransportTests.LargeMessageSplitAcrossManyChunksIsAssembledOnce`（40 KiB 分 300B 块）+ `PipeTransportTests.LargeMessageSplitAcrossManyChunksIsAssembledOnce` |
| 3.32 | #11335 Streamable HTTP `application/json` 应答绕过 `maxMessageBytes` / 错误体整体缓冲 | `StreamableHttpTransport.ReadBodyTextAsync`（分块、超限取消、错误体仅留前 8 KiB） | `StreamableHttpBodyLimitTests`（3 用例：未超限解析 / 超限拒绝 / 错误体截断） |
| 3.33 | #11703 预发布版本后缀导致 changelog 每次启动重复 | `Changelog.GetNewEntries`（只读前导 `major.minor.patch`、无三元组返回 `null`）+ `InteractiveMode.Lifecycle.cs` 两处调用点 | `ChangelogCorpusTests`（21 向量，含 8 条 #11703 回归）；v1.5.2 TS `getNewEntries` 重放 + v1.5.0 基线 0 diff 保真 |
| 3.34 | #11729 oversized SSE 被拒后 HTTP 连接仍挂着 | `SseStream.ConsumeAsync` 的 `catch` 中显式 `stream.Dispose()`（cancel reader）后重抛；调用方 `using var response` 亦释放内容流 | `SseCancelOnErrorTests`（2 用例：超限事件触发释放 / 正常结束不主动释放）+ `SseLineEndingTests` + `StreamableHttpBodyLimitTests` |
| 3.35 | #11643 read/write 视图新增 Dart/Nix/Groovy/Gradle 高亮 | `ThemeHelpers.ExtensionToLanguage` 新增 `dart`/`nix`/`groovy`/`gradle` 映射 | `ThemeHelpersTests.GetLanguageFromPath_MapsNew152Languages`（4 向量） |
| 3.36 | #7121 find/ls 的 `limit` 边界（0/负数按 1、恰好 limit 不误报上限、custom glob 多要一条） | `FindTool`（钳 `Math.Max(1,…)`、`--max-results` +1、超一条才报）+ `LsTool`（钳 1） | `FindLsLimitRegressionTests`（6 用例：恰好/超过/0/负/ls 0/负） |
| 3.37 | #11806 Ctrl+P 单模型 scope 仍可切换、当前模型在 scope 外时 forward/backward 选首/末 | `AgentSession.NextCycleIndex`（nullable，对齐 TS `getNextCycleIndex`）+ `CycleScopedModelAsync`/`CycleAvailableModelAsync` 去掉 `<=1`/`-1→0` 短路 | `AgentSessionLeafTests`（3 用例：wrap / scope 外首末 / 单元素可达）+ `InteractiveModeTests`（124 全绿，含单模型状态用例） |
| 3.38 | #11835 全屏 Kitty 图像按「所覆盖行」之后绘制（去 WezTerm 门控，修 Orca） | `TuiAltScreen` 去掉 `isWezTerm` 门控，`imageCellsNeedRedraw`/`drawKittyImagesLast` 对所有 Kitty 协议终端生效 | `FullScreenKittyImageOrderTests`（1 用例：无 WezTerm 环境下 transmit 晚于行清除与文本；已用旧门控反向验证该用例确实会失败） |
| 3.39 | #11651 `pi config` 忽略自定义 keybindings.json | `ConfigSelectorCli.LoadGlobalKeybindings(agentDir)`（`GlobalKeybindings.Set(KeybindingsManager.Create)`），`SelectConfigAsync` 建 TUI 前调用 | `ConfigSelectorKeybindingsTests`（1 用例：临时 keybindings.json 的 ctrl+p / ctrl+n 经 CLI 装载方法进入全局注册表） |
| 3.40 | #11765 Windows/WSL 上 Ctrl+V 不是默认粘贴键 | `AppKeybindings` 的 `app.clipboard.pasteImage` 默认键：Windows 下 `alt+v` + `ctrl+v`（其余 `ctrl+v`） | `WindowsPasteKeybindingsTests`（1 用例：win32/WSL 判定 + 本机默认键含两者，已用旧默认值反向验证会失败） |
| 3.41 | #11613 bash 超时后只报「Command timed out」，丢弃 shell 的真实退出码 | `ShellTimeoutException`（保持 `timeout:N` 形式、携带 `ShellExitInfo`）+ `BashTool.BuildTimeoutMessage` + `ChildProcessHandle.ExitedAtMs`（超时回调快照 shell 是否已先退出） | `BashTimeoutExitCodeTests`（3 用例：普通超时 / 报退出码与 1.1s / 秒数不变文化格式）+ `CoreToolsTests` 48/48、`ExperimentalServicesTests` 25、`ExperimentalServerProviderTests` 12 |
| 3.42 | #11451 `/tree` 导航后 resume 恢复到旧分支的思考档位 | `AgentSessionPrompt.RecordSelection` 拆出 `RecordThinkingLevel`（按当前模型钳制后比对、不同则补记实时档位）+ `ShouldRecordThinkingLevel` 纯判定 | `AgentSessionCompactionTests.ShouldRecordThinkingLevel_OnlyWhenClampedRecordedDiffersFromLive`（1 用例，含「钳制后相等不写」对齐上游第二用例）+ 该套件 33/33 |
| 3.43 | #11803 tool_search 文本 / codemode output 摘要找不到本地 `$ref`（`$defs`）后面的属性名 | tool_search：`ToolSearch.SchemaText` 跟随本地 `$ref`（`ResolveLocalRef` 字典版 JSON Pointer，~0/~1、%XX，畸形→null）+ `seen` 防递归；codemode：`CodemodeToolApi.FollowRef`（seen 防环）+ `DescribeFields`/`DescribeUnionFields` 传 root 解析 `$ref`/const | `ToolSearchSchemaRefTests`（4 用例逐字对照上游：本地 / 转义段 / 递归共享只读一次 / 远程缺失畸形未引用不展开）+ `CodemodeSchemaRefTests`（2 用例：对象字段 / union 分支，逐字对照上游 harness 期望）+ `ToolSearchCorpusTests` 66/66、`ToolSearchRankingTests` 7/7、`CodemodeCorpusTests` 29/29 |
| 3.44 | #9441 内部光标标记（APC）泄漏到终端输出（多标记 / 水平裁剪 / 其他行） | `Tui.ApplyLineResets` normalize 前剥离全部 marker；`ExtractCursorPosition` 首个 marker 定列、整行剥离全部 marker；`TextLayout.SliceWithWidth` 非 APC 才作为 pendingAnsi 带过边界 | `TextEngineTests`（2 用例：marker 不进后续切片 / SGR 仍带过）+ `FakeCursorTests`（2 用例：多标记 / 其他行不外泄）+ 全量 `Pi.Tui.Tests` 23973/23973 |
| 3.45 | #11692 进程退出时丢失后台 OAuth 刷新写入（一次性 refresh token 已用掉、新 token 未保存） | `RuntimeCredentials.SettledAsync`（登记进行中/失败写入、退出前等待结算）+ `ModelRuntime.SettleCredentialWritesAsync` + `AgentSessionRuntime.DisposeAsync` 末尾 await | `RuntimeCredentialsSettleTests`（3 用例：等待在途写入 / 失败写入仍结算 / 无写入立即完成）+ `AgentSessionRuntimeTests` 23/23、`CoreRuntimeTests` 19/19、`ClientRuntimeTests` 14/14 |

**目录/语料改动的安全性**：
- `ModelData/*.json` 为压缩单行 JSON；改动前脚本校验 `json.dumps(separators=(',',':')) + 原换行`
  与磁盘字节完全一致，只做「按 id 追加/修改键」的定点写入，无格式漂移。
- `model-resolver-corpus.json` 是**从 TypeScript v1.5.0 采集的差分快照**。因 v1.5.2 只改了
  `defaultModelPerProvider` 表（resolver 算法源码逐字未变），只有一个「走默认表」的向量期望值
  随之改变。该向量用**真实 v1.5.2 分期 resolver**（`tools/.gen-model-resolver/` 由生成器从
  `main` 采集，与 v1.5.2 的 resolver 逐字一致）重放得出 `anthropic/claude-sonnet-4-5`；
  并以同一重放脚本对 v1.5.0 分期 resolver 得到 0 diff 验证重放保真（详见 §4.6）。
  生成器自身对 v1.5.2 已失效（`parseModelPattern` 上游已改名），故按权威工件「语料按快照
  手工维护」的既定做法（`CHANGELOG.md` 1.5.0 段亦如此）单个向量定点更新，而非整表重生成。

## 4. 有记录的不做项（逐项权威依据与证据）

> 本章是 §0.0 权威口径的落地：每项给出「权威工件条目」+「可复核证据」。
> 这些不是「已知缺口 / 待后续重导」，而是本项目定义「完整」时所允许的、有记录的不做项。

### 4.1 Chord v2 换根（hook 点名的第一项）

**权威依据**：`migration-execution-status.md`「特殊范围决策」章节（该章节在 `origin/main`
`fb8cf25` 中即已存在，早于本会话，非本轮改写）——「**chord 是有意收窄的实现**：只保留
durable 实际用到的 `Delta`/`Facets`/`Services`/`Context`/`Node`；**对象运行时与跨进程链路
没有对应物**」。这是一条**跨版本、与具体 release 无关**的端口形态决策，故对 v1.5.2 同样生效。
（注：1.5.0 delta §5 曾用「Chord v2 实验性、未进 exports」为由，1.5.1 起 v2 提升为根导出
后该前提已失效；因此本轮**不再以此为由**，改以「依赖契约实证 + 端口形态决策」判定，见下。）

**可复核证据**（本机 `git` 对 v1.5.2 的实证，非推断）：
- `git diff --stat v1.5.0 v1.5.2 -- packages/chord/src/{context,delta,json.ts,watch.ts,output.ts,sqlite}`：
  **v1 基础层源码在本窗口内几乎零变更**，唯一改动是 `sqlite/node.ts`（+16/−3）——给 Node 专属
  `node:sqlite` 适配器加 `read-only` 模式；而端口的 SQLite 后端是 `Microsoft.Data.Sqlite`，
  权威「portable SQLite storage contract / Node `node:sqlite`、Cloudflare binding 不逐字移植」
  决策已明确排除该适配器。即 `Pi.Chord` 已实现的 v1 基础层在 v1.5.2 无可移植的行为增量。
- `git grep "from \"@earendil-works/chord" v1.5.2 -- packages/durable`（唯一在范围内的消费方）
  与 v1.5.0 对比：子路径集合仅从含 `/next` 变为根导入（store 符号导出位置迁移）+ 新增 `/watch`，
  **全部仍是 v1 基础子路径**；durable 未导入任何 v2 新子路径。
- v2 新增的 `plugins`/`http`/`workers`/`web`/`v2`/`tui`/`dev`/`loader` 子路径（对象运行时 /
  跨主机链路 / app kit / surfaces / 遥测）在 `packages/**` 内**无消费方**，只被 chord 自身文档
  与 `apps/p2` 原型应用引用（端口从未移植 p2）。
- `git grep -h "from \"@earendil-works/chord" v1.5.2 -- packages/durable/src`：
  `/context` 46、`/json` 33、`/delta` 21、`/sqlite` 13、`/watch` 6、`/output` 3 处，
  **全是 v1 基础子路径**；根导入 6 处为 `defineTable`/`OwnerTable`/`WhereNode` 等
  store 适配符号。
- `git show v1.5.2:packages/chord/package.json` 的 `exports` 仍含
  `/context`、`/delta`、`/json`、`/watch`、`/output`、`/sqlite` —— v1 基础层在换根后继续发布。
- `git diff v1.5.0 v1.5.2 -- packages/durable/src/storage/store/tables.ts`：
  `defineTable,int,json,str` 仅从 `@earendil-works/chord/next` 改为 `@earendil-works/chord`
  （导出位置迁移），`JsonValue` 从根改为 `/json`。即 durable 依赖的 API 集合未变。
- `git grep -l "@earendil-works/chord" v1.5.2 -- apps/**` 命中 63 个文件，全在 `apps/p2`
  原型应用；端口从未移植 p2（权威：1.5.0 delta §5「未发布/实验/示例不跟」）。

**判定**：换根影响的是 v2 对象运行时/跨进程/app kit，而端口按权威决策只实现 durable 用到的
v1 基础层；durable 的依赖契约实证未变。因此这是「已登记的语义差异 / 不适用」，不是
v1.5.2 对齐的功能缺口。若将来要移植 v2，应按边界表另立「重写 Pi.Chord」项目，而非补丁批次。

### 4.2 Anthropic 消息线程（#11545）—— 已实际移植

本轮已把消息线程**整体落地**（§3.25），不再是「有记录的不做项」：
- 新增 `src/Pi.Ai/Api/AnthropicMessageThreads.cs`：会话级 `create/continue/off` 状态机、
  前缀短哈希（`Hash.ShortHash` + cache_control/字符串 content 归一化）、
  `prepareThreadRequest`（前缀未变时裁掉前缀、去掉 system/tools、带 previous_message_id）、
  `sendThreadRequest`（thread_not_found 等 → 新线程重发；端点不支持 → 不带 thread 重发并关闭该会话）。
  `SessionResources.Register` 负责会话结束/进程退出清理，容量上限 10,000。
- `AnthropicMessages.Stream` 接入：按 `cacheSessionId` + `compat.supportsMessageThreads`
  （缺省仅直连 api.anthropic.com）决定线程会话，构建时加 `message-threads-2026-08-12` beta，
  成功后 `recordThreadReply`，`disableContinuation` 抑制续接。
- 10 项测试（`AnthropicMessageThreadsTests`）：create、continue 裁剪、system 变化强制新线程、
  哈希归一化、拒绝回退、端点不支持关闭、以及真实 HTTP wire（beta 头 / thread 字段 / disableContinuation）。
- 端口与 TS 的形态差异（.NET 用 JsonObject 请求体、`Task`/`SessionResources` 而非 SDK）在
  转换层做等价实现，不改变 wire 契约。

### 4.3 MCP / env「新增 API」（hook 点名的第三、四项）

**权威依据**：`migration-execution-status.md` 第 193 行「不采纳（产品决策）：上游无消费方时
端口已有等价能力」；第 182 行 SQLite/JS 适配器「不逐字移植」；边界表「不适用（端口形态
不同）：env 的 node 侧 listFiles/PTY/nodePlatform」。

**可复核证据（上游 changelog 自述「moved from pi's coding agent」，端口早已实现）**：
| 上游 1.5.1 声称「新增/移入 mcp」 | 端口既有实现 |
| --- | --- |
| `createMcpToolName()` / `mcpNamespace()` | `Pi.CodingAgent/Extensions/Mcp/Tools.cs:111 CreateMcpToolName` |
| `MCP_OUTPUT_MAX_BYTES` / `limitMcpContent()` | `Tools.cs:37 McpOutputMaxBytes`、`Tools.cs:265 LimitMcpContentAsync` |
| `validateMcpServerConfig()` / `isMcpServerOverride()` | `Pi.CodingAgent/Core/McpServers.cs:167 ValidateMcpServerConfig` |
| MCP OAuth 两半（`authorizeMcp` refreshOnly、`withRefreshLock`、`responseFromRedirectUrl`） | `Pi.Mcp/OAuth/OAuthFlow.cs:492 AuthorizeMcp`（含 `SkipRefresh`）、`Pi.CodingAgent/Extensions/Mcp/OAuth.cs:474/529 WithRefreshLockAsync`、`:314 ResponseFromRedirectUrl`、`Pi.Mcp/OAuth/OAuthProvider.cs` |
| 缓存预热公共 helper（1.5.1「移入 ai」） | `Pi.CodingAgent/Core/CacheWarmer.cs:132/147` |

env daemon `process` 操作（`RemoteProcess`/进程管道）是 Node 侧 PTY/进程组语义，
端口 `Pi.Env` 是 SSH 远程环境（权威：边界表已把 node 侧 PTY 列为不适用）。

**判定**：绝大多数是**包位置重定位**，功能在端口已存在。新传输 `PipeTransport` 本轮已**整体
移植**（§3.30，新增 `IMcpPipe`/`PipeTransport`/`JsonRpcLineReader`，15 用例对照上游
`pipe.test.ts`/`lines.test.ts`）；mcp 的 SSE/JSON 大小限制、CR-only、大消息 O(n²)、连接释放
亦已落地（§3.27/§3.31/§3.32/§3.34）。仅 env daemon `process` 操作（`RemoteProcess` 进程管道）
按「不适用（端口形态不同）」记为有记录的不做项——端口 `Pi.Env` 是 SSH 远程环境，没有
Node daemon 的 PTY/进程组消费方。

### 4.4 独立单点修复（coding-agent / tui / durable / ai / mcp）

**权威依据**：边界表第 139 行「已登记的语义差异（非本版本范围）……逐条见
review-inconsistencies-2026-10-21.md；不属于未落地项」；第 137 行「暂不移植（成本/收益）」；
1.5.0 delta §5 对「未发布提交按需单独评估」的先例。

**本轮已移植的单点修复**（§3.26–3.45，含 §3.22 的 #11803 前置）：`#9557`（非 strict 工具根 $defs）、`#11764`（SSE 孤立 CR）、
`#7121`（截断不劈代理对）、`#11923`（粘贴短标记重编号）、`#10497`、`#11695`、`#11879`、
以及第三轮真正补上的 `PipeTransport`（新 API，§3.30）、`#11761`（MCP stdio 大消息 O(n²)，§3.31）、
`#11335`（Streamable HTTP 应答体/错误体上限，§3.32）、`#11703`（预发布版本 changelog，§3.33）、
`#11729`（oversized SSE 释放连接，§3.34）、`#11643`（read/write 新语言高亮，§3.35）、
`#7121` find/ls `limit` 边界（§3.36）、`#11806`（单模型 scope 可切换，§3.37）、
`#11835`（全屏 Kitty 图像去 WezTerm 门控，§3.38）、`#11651`（`pi config` 加载自定义键位，§3.39）、
`#11765`（Windows/WSL Ctrl+V 粘贴键，§3.40）、`#11613`（bash 超时补报 shell 退出码，§3.41）、
`#11451`（`/tree` 导航后思考档位恢复，§3.42）、`#11803`（tool_search 文本 + codemode output 摘要跟随本地 `$ref`，§3.43）、
`#9441`（内部光标标记不外泄，§3.44）、`#11692`（退出前等待 OAuth 刷新写入，§3.45）。

**仍为有记录的不做项的逐项**（issue 号即单独复用的索引）：`#11807` 后半（rg cwd 切到
`searchPath`）/ `#9276`（上下文行改由 ripgrep 提供，两者与端口 `GrepOperations.ReadFileAsync`
绝对路径取值链纠缠，且本机无 `rg` 可做运行时回归验证）、`#11975`、
`pi auth login/logout`、`registerMarkdownTransformer(messageTypes)`、Mermaid、`cacheWarming:idle`、
`ctx.ui.custom(blocked)`、`#9654`、`#8544`、`#11738`、
`#11875`、`#9126`/`#9222`/`#9155`、
`#11708`、`#11892`、durable 逐会话 `CompactionSettings`/JSONL
`readOnly`/group commit、RPC 关闭时序、Windows 安装器/PowerShell 5.1/fd 下载。

**判定**：剩余项均为独立、可按 issue 号单独取用的行为改动，不改变 published API 契约；
按边界表「已登记的语义差异 / 暂不移植（成本收益）」记录。其中与端口形态冲突的
（Windows 安装器、PowerShell 5.1、Node 动态加载、`#11708` 的 JS addEventListener 泄漏修复，
端口用 `Task.Delay(signal)` 无此形态）另归「不适用」。
这不是「本补丁批次不逐条追」的遗留工作，而是与 1.4.0/1.5.0 验收同款的有记录不做项。

### 4.5 schema / telemetry 微秒时钟 / lazy provider（TypeBox/Node 专有）

**权威依据**：`migration-execution-status.md` 第 197 行「配置 schema 是静态产物：端口没有
上游 TypeBox 那层定义，不提供运行期 Compile」；第 184 行「不宣称完整 Node/Bun runtime
parity」；第 191 行「纯导出桶/Node 专属启动器无独立 C# 语义」；1.5.0 delta §5
「packages/schema 的 builders/annotations/typebox-static：TS 侧构造 API，端口用明文 JSON Schema」。

**逐项**：`unsafe` / `recursive`（TypeBox 构造器，端口用明文 JSON Schema 无对应物）、
`Validator.validate`（端口 `ToolSchemaValidator.Check`/`Errors`/`Parse` 已覆盖可观察语义）、
Static 泛型（TS 编译期类型）、BigInt/Undefined 类型（JSON 无对应值，
权威工件第 182 行 SQLite/JS 适配器同理）、相对 `$id` 修复、`equal()`（端口
`SchemaJson.DeepEqual` 已是 const/enum/uniqueItems 的相等判据）、`telemetryNow()` 微秒时钟
（端口 `StoredSpan` 时间为 `long` 毫秒，改 `double` 破坏 public API 且 `microseconds()`
 在无微秒时间戳时是恒等变换）、lazy OAuth/Bedrock 动态 import chunk（Node bundler 专有）、
`provider.refreshModelsAuth`（端口目录刷新不走该 OAuth 路径）。

**判定**：➖ 不适用 / 📋 已登记语义差异，符合权威「TypeBox 构造 API 不适用」决策。

### 4.6 语料快照的维护方式（非缺口，方法学说明）

`model-resolver-corpus.json` 与 `prompt-corpus.json` 是**从 TypeScript 采集的差分快照**，
不是运行期产物。权威工件第 196 行及 `CHANGELOG.md` 1.5.0 段均记载「生成器不在仓库内 /
语料按快照手工维护」。本轮：
- `defaultModelPerProvider` 段：按 v1.5.2 源码 42 项重生成（§3.18）。
- 单个走默认表的向量：用真实 v1.5.2 分期 resolver 重放得出；
  同一脚本对 v1.5.0 分期 resolver 重放得到 **0 diff**，证明重放保真、改动确由默认表切换引起。
- `prompt-corpus.json` 三条旧向量：按 #11861 新语义更新（§3.13）。
生成器对 v1.5.2 已失效（`parseModelPattern` 上游改名 `resolveModelReference`），这是
工具与参照源的既有版本差，不是迁移缺口；按权威既定做法单点维护快照。

### 4.7 沿用 1.5.0 台账的既定不做项

- `v1.5.2..HEAD` 的未发布提交（`main` 上 1.5.2 之后的 p2/p3 实验）：等下一个 tag（1.5.0 delta §5 先例）。
- `apps/` 下 p2 原型应用与 `docs(p2/p3)` 设计稿（191,148 行）：实验稿，端口从未移植。
- Node/npm/TypeBox/安装器/构建链专有项：➖ 不适用。
- TS 侧 schema 构造 API：➖ 不适用。

## 5. 复核方法（下一批动工前）

1. `git -C D:\AI\参考项目\pi log -1` 确认检出；若越过 `b4afdddf4`，用
   `git log --oneline v1.5.2..HEAD` 重新评估（当前 `a40abecaa`）。
2. 表驱动常量逐项 diff：`ModelData/*.json` 的 provider/model 表、`defaultModelPerProvider`
   （`model-resolver.ts`）、`compat` 字段表、`Retry.cs` / `Overflow.cs` 模式表。**不能只看漂移窗口**。
3. 每批收口：`dotnet build Pi.slnx -m:1` 0 警告 0 错误 → 定向测试 → 全量门禁测试 →
   提交 → 更新本台账与 `CHANGELOG.md`。
4. 验证「有没有漏掉新 release」：`git -C D:\AI\参考项目\pi tag --sort=-v:refname | head`
   与 `origin/stable` 指向；1.5.0 之后为 `v1.5.1`、`v1.5.2`。
5. 排除项不是缺口：判定标准与证据链见 §0.0 / §4；新增排除项必须落到权威工件的某一条目，
   并给出可复核证据（git 实证 / 端口既有实现的文件行号），不得使用「待后续」「已知缺口」措辞。
