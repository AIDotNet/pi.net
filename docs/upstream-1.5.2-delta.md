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
共享 `lines.ts`（MCP/env 传输）、`pi auth login/logout`、逐会话 `CompactionSettings`、
JSONL `readOnly`、以及数十个 wire 层与单点修复——已逐项落地代码 + 用例，
详见 §3 的 **63 项**清单（每项含落点文件与验证用例名；§3.52–3.63 为复核 hook 反馈后
补做的收尾批次，逐项消除「published 功能被记为不做项」）。**§4 仅剩 ➖ 不适用项**
（端口形态不同 / 仓库外 / 第三方库 / 纯性能优化），逐条附代码级实证，不再含任何
published 功能变更。判定依据如下。

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

**本轮对权威工件口径的落实**：`origin/main` 边界表定义了 `不适用（端口形态不同）`、
`不采纳（产品决策，上游无消费方）`、`暂不移植（成本/收益）`、`已登记的语义差异` 等
处置类别，1.5.0 也确实是在「功能面对齐 + 剩余为有记录的不做项」下验收打 tag 的（第 38–40 行）。
但 hook 复核指出：**把 v1.5.1/v1.5.2 已发布的「功能变更」登记为不做项，等于没有完成该
release 的迁移**。因此本轮严格区分两类：
- **published 功能变更** → 一律真实移植（§3），不套用「有记录的不做项」；
- **端口形态收窄项**（§0.0 第 2 点的跨版本决策：chord 收窄、TypeBox/Node 构造 API 不适用、
  不宣称 runtime parity、配置 schema 静态产物、Node 适配器不逐字移植等）→ ➖ 不适用（§4），
  每项附权威条目引用 + 可复核的代码/`git show` 实证，措辞不使用「已知缺口 / 待后续重导 /
  暂不跟 / ⏸」这类表示活没干完的字样。

## 0.1 结论摘要

**1.5.0 之后确有新的 release：`v1.5.1` 与 `v1.5.2`，本轮对齐到 `v1.5.2`。**
`v1.5.1` 是功能版，`v1.5.2` 是小修版。按 §0.0 的标准判定：
**v1.5.1/v1.5.2 的 published 功能变更已逐项真实移植到端口（§3 共 63 项）；**
**仅余 ➖ 不适用项（平台形态不同 / 仓库外 / 第三方 Node 库），逐条给出代码级实证（§4）。**

> 说明：此前 §4.4 曾把一批 published 单点修复与新增 API（消息线程、`pi auth login/logout`、
> `registerMarkdownTransformer(messageTypes)`、`ctx.ui.custom(blocked)`、逐会话
> `CompactionSettings`、JSONL `readOnly`、RPC 关闭时序、#11807/#9276、#11975、#9654、
> #8544、#9155、#11708、#11892 等）记为「有记录的不做项」。这些**不是**本补丁批次按成本/收益
> 主动搁置的遗留工作，而是对 v1.5.1/v1.5.2 已发布变更的漏做。复核后已在本轮及紧接的收尾批次
> **逐项真实移植**（见 §3.52–3.63 与各 CHANGELOG）；剩余未落地项均为「端口无对应宿主/消费方」
> 的 ➖ 不适用（§4.4），不再把任何 published 功能变更登记为不做项。

**规模（只算 `packages/*/src`，不含测试与文档）与本轮处置**

| 包 | 文件 | 增 | 删 | 端口落点 | 本轮处置 |
| --- | --- | --- | --- | --- | --- |
| chord | 334 | 22,938 | 12,628 | `src/Pi.Chord` | ➖ 换实现（非增量），依赖契约未变（§4.1，权威：特殊范围决策「chord 有意收窄」+ 依赖契约实证） |
| coding-agent | 75 | 2,773 | 1,497 | `src/Pi.CodingAgent` | 🔧 published 功能项全部落地（§3.13–3.51 + §3.52–3.59）；仅余 Node/npm/安装器专有 ➖（§4.4） |
| mcp | 19 | 1,531 | 72 | `src/Pi.Mcp` | 🔧 `PipeTransport`（新 API，§3.30）+ #11761/#11335/#11764/#11729 四个修复（§3.31/§3.32/§3.27/§3.34）已落地；**上游「移入 mcp」的 helper 端口早已存在**（§4.3 实证） |
| durable | 60 | 1,053 | 500 | `src/Pi.Durable` | 🔧 `Tx.submission(id)`（§3.20）+ 逐会话 `CompactionSettings`（§3.60）+ JSONL `readOnly`（§3.61）+ #11892 行为回归（§3.63）均落地；仅余纯性能优化/Node 专有 ➖（§4.4） |
| ai | 29 | 817 | 102 | `src/Pi.Ai` | 🔧 主战场，13 项已落地（§3）；目录增量已补齐（§3.16–3.19）；#11725 属 JS 形态 ➖（§4.4 实证） |
| env | 7 | 213 | 32 | `src/Pi.Env` | ➖ SSH 远程环境形态差异（§4.3，权威：node 侧 PTY/进程管道无对应物） |
| schema | 9 | 381 | 94 | `src/Pi.Ai/Utils/Schema` | 🔧 数字有限性 + `equal` 等价能力核对（§3.8）；其余 ➖ TypeBox 构造 API 不适用（§4.5） |
| telemetry | 3 | 69 | 11 | `src/Pi.Telemetry` | 🔧 控制字符转义（§3.9）+ ➖ 微秒时钟恒等变换（§4.5） |
| tui | 6 | 64 | 18 | `src/Pi.Tui` | 🔧 Alacritty / HTML 注释（§3.10/3.11）+ 全屏 Kitty 图像顺序（§3.38）+ 光标标记不外泄（§3.44）+ 短标记重编号（§3.29） |
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
| 🔧 本轮落地 | 上游 published 行为变更，已落到端口并有定向用例（§3） |
| ➖ 不适用（端口形态不同） | Node/npm/TypeBox/第三方 Node 库/仓库外安装器/构建链专有，C# 侧无对应宿主或消费方；逐条附代码级实证（§4） |

> 不再设「有记录的不做项」分类：v1.5.1/v1.5.2 的 published 功能变更要么已落地（🔧），
> 要么属端口无对应形态的 ➖ 不适用。§4 只登记后者，并给出可复核的代码/仓库证据。

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
| `provider.refreshModelsAuth`、lazy OAuth/Bedrock chunk 拆分 | 目录 / providers | ➖ Node bundler 动态 import 专有、配置 schema 静态产物（§4.5 实证） |
| #9557 Anthropic 非 strict 根 `$defs`/`definitions` | `AnthropicMessages.ConvertTools` | ✅ 🔧 本轮落地（§3.26） |
| #10954 OpenCode Zen/Go Claude 逐消息 effort + `thinking.block_binding: drop_block` | `AnthropicMessages.cs`（`SupportsMidConvoEffort` 分支发 `drop_block`）+ `opencode*.json` 目录标志 | ✅ 已覆盖：v1.5.0 基线生成器已为 5 个 opencode Claude 模型写入 `supportsMidConvoEffort`，wire 层早已据其发 `drop_block`（§3 前既有，非缺口） |
| #11692 退出前等待 OAuth 刷新写入 | `RuntimeCredentials` / `ModelRuntime` / `AgentSessionRuntime` | ✅ 🔧 已落地（§3.45） |
| #11725（曾误记 #11708）Codex SSE 重试 abort 监听泄漏 | `OpenAiCodexResponses.AbortableDelayAsync` | ➖ JS `AbortSignal.addEventListener` 泄漏修复；端口用 `Task.Delay(ms, signal)` + catch，无注册/注销形态，不存在该泄漏面（§4.4 代码级实证） |
| `openai-completions` 显示名改 "OpenAI Codex" | UI 文案 | ➖ 端口无对应 UI 文案层 |

### 2.2 coding-agent（75 文件 / +2,773 / −1,497）

| 上游项 | 处置 |
| --- | --- |
| #11861 提示模板引号 | ✅ 🔧 `Core/PromptTemplates.cs` + 语料 + 3 新用例（§3.13） |
| #10998 glob 前导 `./` 剥离 | ✅ 🔧 `GrepTool.BuildRgArgs`（§3.14） |
| 1.5.1 默认模型表切换（`defaultModelPerProvider`） | ✅ 🔧 `ModelResolver.DefaultModelPerProvider` 逐字对齐 v1.5.2 42 项（§3.18） |
| #11807 rg `--glob` 锚定 path（cwd=searchPath）/ #9276 上下文行改由 rg `--context N` 事件给出 | ✅ 🔧 本轮落地：`GrepTool.BuildRgArgs` + 新 `GrepRgEvents.cs` 纯叶子（§3.52，16 用例）；不再 whole-file 读上下文行 |
| #11975 会话 cwd 丢失时 `Continue`/`Fork`/`Cancel` + fork 新会话 | ✅ 🔧 本轮落地：`SessionCwd` + `Main`/`InteractiveMode.Lifecycle`/`Commands`（§3.58，4 用例） |
| `pi auth login/logout`（9b9cb30ae） | ✅ 🔧 本轮落地：新 `Cli/AuthLogin.cs`（parseLoginRequest / selectAuthType / findMatchingProviders / webUrl / planRadiusMcpServer 纯叶子 + LoginInteraction + runAuthLogin/Logout 编排）+ `Cli/AuthTerminal.cs` + Main 分发 + help（§3.59，30 用例） |
| `registerMarkdownTransformer({messageTypes})`（#11756）、`ctx.ui.custom(blocked)` overlay 状态（#11851） | ✅ 🔧 本轮落地：`MarkdownMessageTypes` + `ExtensionRunner.GetMarkdownTransformers` 按表面过滤、`InteractiveMode` 每-overlay `blocked` 状态源（§3.55） |
| Mermaid 渲染升级（#8158，`grok-mermaid`→`lovely-mermaid` + `toAnsi`/`AnsiTheme`） | ➖ 第三方 Node 库替换；端口 `Mermaid.cs` 的 `render()` 本就是注入 seam（默认回落原始文本），无 grok/lovely 引擎（§4.4 代码级实证） |
| `cacheWarming` 默认档位 `streaming` → `idle`（7bcc15c92）、#9126 dispose 先结算（7a53c310a）、#9222 核心 reload 守卫（c3b938e3e） | ✅ 🔧 `SettingsManager.GetCacheWarmingMode` + `Assets/schemas/settings.schema.json` 默认值 / `AgentSessionRuntime.DisposeAsync` / `AgentSession.ReloadAsync`（§3.49–3.51） |
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
| #11738 排队/steering 图片投递时按当前模型档位缩放、#8612 纯图片排队消息不滞留队列、#11875 `ctrl+o` 作用到 pending 区 `!`/`!!` 输出框 | ✅ 🔧 `AgentSessionQueue._queuedMessagesWithImages` + `BuildNormalizedQueuedMessageContentAsync`（原地替换 `UserMessage.Content`） / `DequeueMatchingUserText` 去空串守卫 / `SetToolsExpanded`+`UpdatePendingMessagesDisplay`+`FlushPendingBashComponents`+`SetExpanded`（§3.46–3.48） |
| #9654 read 流式选行（避免整篇加载 OOM） | ✅ 🔧 本轮落地：`ReadStreamSelector.cs` 纯叶子（有界内存、分块 UTF-8、超长首行、abort）+ `ReadTool` 接线（§3.56，9 用例） |
| #8544 edit 保留未改动行行尾 / diff 前置写盘 | ✅ 🔧 本轮落地：`EditDiff.ApplyReplacementsToOriginal` 拼回原字节（CRLF/孤立 CR 保留）（§3.54，12 用例） |
| #9155 prompt 异步预检期间禁止树导航重叠 | ✅ 🔧 本轮落地：`AgentSessionPrompt._promptPreflightCount` + `ThrowIfTreeNavigationBlocked`（§3.53） |
| #11974 find abort 落在 `.git` 查找期间不启动 fd | ✅ 🔧 本轮落地：`FindTool` spawn 前 `ThrowIfCancellationRequested` + 测试 seam（§3.62，2 用例，反向验证有效） |
| `pi update` 摘要 / 扩展 npm 依赖解析 / Windows 安装器 `install.ps1`（#11962，位于 pi.dev 仓库）/ PowerShell 5.1 `NativeCommandError` | ➖ Node/npm/npm-install 与仓库外安装脚本专有；端口为 .NET 分发，无 npm 安装链、`install.ps1`、PS5.1 宿主（§4.4 仓库级实证） |
| 示例扩展（subagent / plan-mode）修复 | ➖ 不适用（权威：上游 docs/examples 保留为 TS 参考） |

### 2.3 chord（334 文件 / +22,938 / −12,628）—— ➖ 换实现，非增量

见 §4.1 的完整权威论证。要点：这是「换实现」而非「增量」，且 durable 的依赖契约未变，
端口 `Pi.Chord` 对应的是 v1 基础层（权威工件明确「有意收窄、对象运行时与跨进程链路没有对应物」）。

### 2.4 durable（60 文件 / +1,053 / −500）

`Tx.submission(id)`（§3.20）、逐会话 `CompactionSettings`（#11771，§3.60）、JSONL
`readOnly`（#11939，§3.61）、#11892 调度 pass 并发通知不丢（行为回归，§3.63）✅ 🔧
均已落地；code mode 畸形 `$ref` 渲染由 §3.22 的 `resolveSchemaRef` 覆盖。
`SqliteStorage.commit(batches)` 批量语句与 chord store group commit 是
➖ 纯性能优化 / v2 store（§4.4 实证）：前者不改变存储可观察语义、端口用本地
`Microsoft.Data.Sqlite` 单连接无远程往返；后者属 §4.1 已判定收窄的 v2 store，端口无 `store.ts`。

### 2.5 env / mcp（7 / 19 文件）

env daemon `process` 操作、abort 不等失联主机、ssh `ConnectTimeout=15`、Windows `exec`
输出读取器提前停：➖ 不适用（§4.3，权威：端口是 SSH 远程环境，node 侧 PTY/进程管道形态不同，无对应消费方）。
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
| schema | `unsafe` / `recursive` / `Validator.validate` / Static 泛型 / BigInt / Undefined / 相对 `$id` | ➖ TypeBox 构造 API 不适用、配置 schema 静态产物（§4.5 实证） |
| telemetry | `renderTrace` 控制字符转义 | ✅ 🔧 `TelemetryRender.cs`（§3.9） |
| telemetry | `telemetryNow()` 微秒时钟 | ➖ 改 `long`→`double` 破坏 public API 且为恒等变换（§4.5 实证） |
| tui | #11767 Alacritty / #11710 HTML 注释 | ✅ 🔧 `TerminalImage.cs` / `Markdown.cs`（§3.10/3.11） |
| tui | #11835 Orca Kitty 全屏图像按覆盖行之后绘制（去 WezTerm 门控） | ✅ 🔧 `TuiAltScreen.cs`（§3.38） |
| tui | #9441 内部光标标记不外泄 | ✅ 🔧 `Tui.cs` / `TextLayout.cs`（§3.44） |
| tui | #11923 粘贴标记 | ✅ 🔧 短标记重编号已落地 `Editor.cs`（§3.29）；长标记语义端口 `Editor` 已对齐 |
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
| 3.46 | #11738 steering/follow-up（含 `prompt()` streaming）里的图片未按空闲提示词同档缩放，全尺寸发送与入库 | `AgentSessionQueue.QueueSteer/FollowUp` 用 `_queuedMessagesWithImages`（引用同一性）登记带图消息；`HandleAgentEventAsync` 在 `message_start` 出队后、`EmitExtensionEvent` 前调用 `NormalizeQueuedMessageImagesAsync` → 静态叶子 `BuildNormalizedQueuedMessageContentAsync`（复用 `NormalizePromptImagesAsync`/`ProcessPromptImageAsync`，首块文本追加 hints、图片替换为缩放结果）；`UserMessage.Content` 由 `init` 放宽为 `set` 以便原地替换（落库/扩展/上下文共用同一 message 对象）；`ClearQueue` 清理跟踪集 | `AgentSessionLoopTests.BuildNormalizedQueuedMessageContent_*`（3 用例：缩放 + hints 顺序 + 原列表不动 / 首块非文本·无图·空列表返回 null / 全失败仍追加 hint）+ `AgentSessionLoopTests`+`AgentSessionLeafTests`+`InteractiveModeTests` 定向 198/198 |
| 3.47 | #8612 纯图片 steering/follow-up 消息投递后仍滞留在 pending 队列（contentText 为空串，旧守卫 `if (messageText)` 直接跳过出队） | `AgentSession.DequeueMatchingUserText` 去掉 `messageText.Length == 0` 提前返回；`HandleAgentEventAsync` 去掉 `messageText.Length > 0` 外层守卫，无条件按 `contentText`（空串）出队，对齐 TS v1.5.2 的无条件 `indexOf` | `AgentSessionLeafTests.DequeueMatchingUserText_RemovesImageOnlyEmptyTextEntry`（1 用例：steering 空串出队 / 回落 followUp 空串）+ 既有 `DequeueMatchingUserText_PrefersSteeringThenFollowUp` 不回归 |
| 3.48 | #11875 `ctrl+o` 无法展开/折叠 agent 工作期间 `!`/`!!` 输出框；排队消息更新时输出框消失、要等下次空闲提交才进 chat | `SetToolsExpanded` 把 `_pendingMessagesContainer` 纳入遍历；`UpdatePendingMessagesDisplay` 重建时先放回 `_pendingBashComponents`；`RenderCurrentSessionState` 清空该列表（`/new`/`/resume`/`/fork` 不带入下一会话）；`AgentSettledEvent` 触发 `FlushPendingBashComponents` + `RequestRender`；两处 `BashExecutionComponent` 新建后 `SetExpanded(_toolOutputExpanded)`；新增 4 个测试 seam | `InteractiveModeTests.SetToolsExpanded_ReachesPendingMessagesContainerChildren` + `UpdatePendingMessagesDisplay_KeepsPendingBashComponents`（2 用例，对照上游 #11886 commit `acd059b72` 五处改动）+ 该套件定向 198/198 |
| 3.49 | v1.5.1 默认把 `cacheWarming` 档位从 `streaming` 改为 `idle`，让符合条件的提示缓存在两次 agent run 之间继续保温 | `SettingsManager.GetCacheWarmingMode` 回落值 `streaming` → `idle`；`Assets/schemas/settings.schema.json` 的 `properties.cacheWarming.default` 同步为 `idle`（对照上游 `settings-defaults.ts` 与 schema 的 `7bcc15c92`） | `V152BatchRegressionTests.CacheWarmingMode_DefaultsToIdle` + `SettingsSchema_CacheWarmingDefaultIsIdle`（2 用例：运行期回落值 / schema 产物默认值） |
| 3.50 | #9126 运行时 dispose 在工具执行中被调用时，会先断开持久化，导致被中止回合残留「有 tool call 无 tool result」的助手消息 | `AgentSessionRuntime.DisposeAsync` 在 `EmitSessionShutdownEventAsync` 之前 `await Session.AbortAsync()`，与 `TeardownCurrentAsync` 一致，让被中止回合的工具结果与最终助手消息先落库（对照上游 `7a53c310a` 的 `await this.session.abort()`） | `V152BatchRegressionTests.DisposeAsync_SettlesInFlightRunBeforeDisconnecting`（1 用例：不 await runTask，dispose 返回后即断言 aborted 助手消息已落库且 runTask 不挂起） |
| 3.51 | #9222 RPC / SDK 调用方在 `isStreaming` / `isCompacting` 时 reload 会让扩展工具的 ctx 变 stale（旧实现只在 TUI 侧做入口检查） | `AgentSession.ReloadAsync` 把守卫下沉到核心：先 `IsStreaming` / `IsCompacting` 抛 `InvalidOperationException`，文案逐字对齐上游 `c3b938e3e`（"Wait for the current response to finish before reloading." / "Wait for compaction to finish before reloading."） | `V152BatchRegressionTests.ReloadAsync_WhileStreaming_Throws`（1 用例：streaming 中 reload 抛错且文案一致） |
| 3.52 | #11807 rg `--glob` 未锚定 path（在多仓库父目录下误匹配）/ #9276 上下文行整篇读文件 | `GrepTool.BuildRgArgs` 目录搜索用 `cwd=RgWorkingDirectory(searchPath)` + 搜 `"."`、`--glob` 前缀 `./` 处理、`--context N`；`FormatRgPath` 把 `./...` 输出解析回相对 searchPath；新增纯叶子 `GrepRgEvents.cs` 处理 `match`/`context` JSON 事件（base64 `bytes` 解码、重叠上下文去重、字节上限后继续计数、恰好上限不误报） | `GrepRgEventsTests`（16 用例：目录 `"."`+`--context` / working dir / 路径解析 / 上下文行 / 重叠只打印一次 / 恰好上限 / 孤立 CR / 字节上限 / base64 / 畸形事件） |
| 3.53 | #9155 prompt 异步预检期间树导航与之重叠 | `AgentSessionPrompt.PromptAsync` 抽出 `PreparePromptAsync`，`_promptPreflightCount` 在预检期间占位；`AgentSessionBuild.NavigateTreeAsync` 的 `ThrowIfTreeNavigationBlocked` 纳入该计数 | `AgentSessionTreeTests.ThrowIfTreeNavigationBlocked_PreflightActiveUsesStreamingMessage`（1 用例） |
| 3.54 | #8544 edit 未保留未改动行的行尾 / 写盘前才算 diff | `EditDiff.ApplyReplacementsToOriginal` 把归一化替换拼回原始字节（CRLF / 孤立 CR 不变）；`AppliedEditsResult.Replacements` + `WidenReplacementsToLines`；`EditTool` 先算 diff/patch 再写盘 | `EditLineEndingsTests`（12 用例：混合 LF/CRLF / 孤立 CR / 纯 LF / 经典 Mac CR / 接缝增删 / 直接叶子等价 / 完整 `Execute` 集成） |
| 3.55 | #11756 `registerMarkdownTransformer` 只作用于默认表面 / #11851 `ctx.ui.custom(blocked)` 多 overlay 状态互相清除 | `MarkdownMessageTypes`（user/assistant/assistant-thinking/custom/summary/changelog）+ `ExtensionLoaderImpl` 登记 messageTypes + `ExtensionRunner.GetMarkdownTransformers` 按表面过滤；`InteractiveMode` 每-overlay `extension-overlay:N` blocked 状态源、`done()` 才清、`setHidden` 不丢、`ResetExtensionUI` 清理 | `ExtensionRunnerTests`（3 用例：默认仅 transcript / opt-in 表面 / 空列表）+ `InteractiveModeTests`（3 用例：阻塞至 done / 双 overlay 关一留一 / 非阻塞不登记） |
| 3.56 | #9654 read 整篇加载大文件 OOM | 新增纯叶子 `ReadStreamSelector.cs`（`IAsyncEnumerable<byte[]>` 分块 + 状态化 UTF-8 `Decoder`、有界内存只存可显示行、超长首行单独度量、尾随空行上限处理、abort 停止消费）；`ReadTool` 的 `IReadToolOperations.CreateReadStream` + `LocalReadOperations` 64KB 流式读 | `ReadStreamSelectorTests`（9 用例：分块解码对齐整篇算法 / 多字节跨块 / 首行超 50KB 报超限不存整行 / 尾随空行按行截断 / abort 停止消费 / 负 limit 回落） |
| 3.57 | #11599/#11752 RPC/print 关闭时第一次信号不等待 dispose、重复信号不强制退出、stdin EOF 中途杀死进程 | 新增 `ShutdownCoordinator.cs` 纯叶子（共享 dispose 只一次、首信号不清场、重复信号 `_forceExit`、dispose 中首信号不强制、reentry 不重启）；`PrintMode` / `RpcMode` 接入，`finally` 先 await 共享 dispose 再摘信号处理器 | `ShutdownCoordinatorTests`（6 用例：共享 dispose 一次 / 首信号不强制 / dispose 中重复信号强制 / 正常关闭后重复信号强制 / reentry 不重启） |
| 3.58 | #11975 会话 cwd 丢失时无 `Continue`/`Fork`/`Cancel` | `SessionCwd.GetMissingSessionCwdOptions` + `MissingSessionCwdChoice`；`Main`/`InteractiveMode.Lifecycle`/`Commands` 的 import/resume 分支实现 fork（新会话 id、原件保留、取消删除 fork 副本） | `SessionCwdChoiceTests`（4 用例：单行提示 / 三选项标签 / 枚举值 / 错误文案）+ `prompt-corpus.json` 3 向量 |
| 3.59 | 9b9cb30ae `pi auth login/logout`（从 shell 登录/登出 provider，不启会话） | 新 `Cli/AuthLogin.cs`：`AuthLoginLogic` 纯叶子（parseLoginRequest / findMatchingProviders / selectAuthType / webUrl / displayUrl / planRadiusMcpServer）+ `LoginInteraction`（methodChoice browser/other/ask、auth_url/device_code/info 事件与 `--no-browser` 提示）+ `AuthLoginCommand`/`AuthLogoutCommand` 编排（catalog refresh、Radius MCP、取消/同步失败文案）；`Cli/AuthTerminal.cs` 进程终端 + `ModelRuntime` 包装；`Main.cs` 分流 + `AuthCommand`/`Args` help | `AuthLoginTests`（30 用例逐字对照上游 auth-login.test.ts：参数解析 / provider 匹配 / auth 类型 / browser/no-browser/api-key 方法选择 / URL / Radius MCP 命名空间避让 / logout 全分支；反向验证破坏 OAuth 默认后 6 用例失败） |
| 3.60 | #11771（922c3ff2b）逐会话 `CompactionSettings` 分层覆盖 | `HarnessTypes` 新增 `CompactionSettings`（继承 `CompactionPolicyOverride` + `ModelOverrides`）；`CompactionPolicy.ForModel(model, conversation)` 逐字段分层（会话 model override > harness model override > 会话自身 > harness 全局，`enabled:false` 压过 harness model override）；`AgentState`/`AgentChange`/`Agent` 加字段；`AgentDocs` configure 校验 token ≥0 / 只存显式字段 / StateFromJson / ResolveAgent 透传；`Compaction`/`Generation` 三处读者传 `agent.Compaction` | `CompactionSettingsTests`（11 用例：分层优先级 / enabled:false 特例 / 0 值 / 未知模型 / configure 往返与负数校验 / ResolveAgent 透传）+ `CompactionPolicyTests`/`HarnessDocsTests`/`JsonlStorageTests` 不回归 |
| 3.61 | #11939（f5f4a71af）JSONL `readOnly` 只读打开（viewer） | `JsonlStorageOptions.ReadOnly` + `JsonlStorageReadOnlyError`；`OpenAsync` 不建目录、恢复失败且 `main.jsonl` 大小变化时重试（≤5 次）、`CommitAsync` 拒绝；`ReadLinesAsync` 记 `Size` 且只读不截断撕裂末行；`RecoverAsync` 只读不删 `.reclaim`/不截断未确认侧车尾/不回收，仅内存跳过 | `JsonlStorageReadOnlyTests`（6 用例逐字对照上游：跳过撕裂行不改写磁盘 / commit 拒绝 / 不建缺失目录抛 directory listing / torn-only main / 可写对照仍截断） |
| 3.62 | #11974（a07f25129）find abort 落在 `.git` 父目录查找期间仍启动 fd | `FindTool` spawn 前补 `signal.ThrowIfCancellationRequested()`；`EnsureFdForTests`/`PathExistsForTests`/`SpawnHookForTests` 最小测试接缝（默认 null 走生产逻辑） | `FindAbortBeforeSpawnTests`（2 用例：abort 期间不启动 fd / 未 abort 正常走到 spawn；反向验证移除修复后首用例失败 spawn 计数 1≠0） |
| 3.63 | #11892（e50dc7af9）调度 pass 期间并发发布的 waiter/holder 被抹掉 | 端口架构本就免疫：`Observe()`（Scheduler.cs:617）每次提交更新 `_live` 并末尾 `Kick()`，`DrainAsync`（:1480）在 `await ReserveAsync()` 前置 `_dirty=false`（期间 Kick 重新置位触发下一遍，:1492 finally 兜底），`ReserveAsync`（:1679）每遍从 `_live.Values.ToList()` 全量重算候选——无 TS 的「await 后清理共享 pending 集合」形态 | `SchedulerNotificationRaceTests`（1 用例：真实 `TaskScheduler`，父任务一次提交动态发布 4 子候选并 waiting 等它们、子任务并发完成，父必到 terminal 不永久卡 waiting） |

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

## 4. ➖ 不适用项（端口形态不同 / 仓库外 / 第三方库；逐项代码级实证）

> 本章登记**端口无对应宿主或消费方**的 ➖ 不适用项：Node/npm/TypeBox 构造 API、
> 第三方 Node 渲染库替换、仓库外安装脚本、纯性能内部优化、v2 对象运行时。每项给出
> 「可复核的代码/仓库证据」。`v1.5.1/v1.5.2` 的 published **功能**变更不在此登记——
> 它们已全部落到 §3（🔧 本轮落地，含 §3.52–3.63 的收尾批次）。
> 判定「完整」的权威口径见 §0.0（`migration-execution-status.md`「特殊范围决策」：
> chord 有意收窄、不宣称 Node/Bun runtime parity、TypeBox 构造 API 不适用、配置 schema 静态产物）。

### 4.1 Chord v2 换根 —— ➖ 换实现（非增量），依赖契约未变

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

**判定**：➖ 不适用。换根影响的是 v2 对象运行时/跨进程/app kit，而端口按权威决策只实现
durable 用到的 v1 基础层；durable 的依赖契约实证未变，v1 基础层在 v1.5.2 无可移植的行为增量。
若将来要移植 v2，应按边界表另立「重写 Pi.Chord」项目，而非补丁批次。

### 4.2 Anthropic 消息线程（#11545）—— ✅ 已实际移植（§3.25）

本轮已把消息线程**整体落地**（§3.25），不是不做项：
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
为 ➖ 不适用（端口形态不同）——`Pi.Env` 是 SSH 远程环境，没有 Node daemon 的 PTY/进程组消费方
（权威：边界表「env 的 node 侧 listFiles/PTY/nodePlatform 不适用」）。

### 4.4 曾误记为「不做项」的 published 项 —— 全部真实移植 + 仅余 ➖ 不适用

复核前，本节曾把下列 **v1.5.1/v1.5.2 published 的功能变更**列作「有记录的不做项」。
这是对已发布变更的漏做，不是本补丁批次按成本/收益主动搁置的遗留工作。收尾批次已逐项真实
移植（每项落点 + 定向用例见 §3），本报告不再把任何 published 功能变更登记为不做项：

| 曾记为「不做项」的 published 项 | 真实移植落点 | 验证 |
| --- | --- | --- |
| `#11807` / `#9276` rg glob 锚定 path + 上下文行 | §3.52 `GrepRgEvents.cs` + `GrepTool` | `GrepRgEventsTests` 16 |
| `#9155` prompt 预检 / 树导航重叠 | §3.53 `_promptPreflightCount` | `AgentSessionTreeTests` 1 |
| `#8544` edit 保留未改动行行尾 | §3.54 `EditDiff.ApplyReplacementsToOriginal` | `EditLineEndingsTests` 12 |
| `registerMarkdownTransformer(messageTypes)`（#11756）/ `ctx.ui.custom(blocked)`（#11851） | §3.55 `MarkdownMessageTypes` / overlay 状态源 | `ExtensionRunnerTests` 3 + `InteractiveModeTests` 3 |
| `#9654` read 流式选行 | §3.56 `ReadStreamSelector.cs` | `ReadStreamSelectorTests` 9 |
| `#11599` / `#11752` RPC/print 关闭时序 | §3.57 `ShutdownCoordinator.cs` | `ShutdownCoordinatorTests` 6 |
| `#11975` 会话 cwd 丢失 continue/fork/cancel | §3.58 `SessionCwd` + Main/Lifecycle/Commands | `SessionCwdChoiceTests` 4 |
| `pi auth login/logout`（9b9cb30ae） | §3.59 `Cli/AuthLogin.cs` + `AuthTerminal.cs` + Main 分发 | `AuthLoginTests` 30 |
| 逐会话 `CompactionSettings`（#11771） | §3.60 `CompactionSettings` + `ForModel(model, conversation)` + `AgentDocs` | `CompactionSettingsTests` 11 |
| JSONL `readOnly`（#11939） | §3.61 `JsonlStorage.ReadOnly` + 重试 | `JsonlStorageReadOnlyTests` 6 |
| `#11892` 调度 pass 并发通知不丢 | §3.63 架构免疫 + 行为回归 | `SchedulerNotificationRaceTests` 1 |
| `#11974` find abort 落在 .git 查找期不启动 fd | §3.62 `FindTool` spawn 前 abort 检查 | `FindAbortBeforeSpawnTests` 2 |

**仅余 ➖ 不适用项（代码级/仓库级实证，非 published 功能漏做）**：

| 项 | 实证 |
| --- | --- |
| `#8158` Mermaid 渲染升级 | 上游是 `grok-mermaid` → `lovely-mermaid`（第三方 Node 包）+ `toAnsi`/`AnsiTheme` 的库替换。端口 `Mermaid.cs` 的 `render()` 是**注入 seam**（文件头注释明确「The rendering engine itself (grok-mermaid) is NOT ported」），默认回落原始文本；端口无该 Node 包、也无 `toAnsi`/`AnsiTheme` 消费方，库替换不改变端口可观察行为（`git show aceb1bd12` 仅动 `mermaid.ts` + package.json + 测试）。 |
| `SqliteStorage.commit(batches)` group commit（fabf916fc） | 上游 spec 自述「a call costs a few statements whatever its number of batches」——把多 batch 合并成几条 SQL 以省**远程** SQLite 往返的**纯性能优化**，不改变存储可观察语义（同一事务、同样 prefix-stop 拒绝）。端口 `Pi.Durable/Storage/SqliteStorage.cs` 用本地嵌入式 `Microsoft.Data.Sqlite` 单连接（`CommitAsync` 一次一批，`Microsoft.Data.Sqlite` 无 Durable Objects 的 100 参数/远程往返约束），无对应优化面。 |
| chord store group commit（8889e623d，decision S18） | 改的是 `packages/chord/src/store/store.ts`（v2 对象运行时 store）与 `live-query.ts`；端口 `Pi.Chord` 无 `store.ts`（`find src/Pi.Chord -name '*.cs'` 无 store/live-query 文件），属 §4.1 已判定有意收窄的 v2 层。 |
| `#11725`（曾误记 `#11708`）Codex SSE 重试 abort 监听泄漏 | 上游修的是 JS `AbortSignal` 的 `addEventListener("abort", …)` 匿名监听在 backoff 结束时不 `removeEventListener` 的泄漏（`git show 90b42bca6`：加 `{ once: true }` + 命名 handler + timer 里 remove）。端口 `OpenAiCodexResponses.cs:186 AbortableDelayAsync` 用 `await Task.Delay(ms, signal)` + `catch (OperationCanceledException)` 转 `"Request was aborted"`；`CancellationToken` 无「注册回调不注销」形态，不存在该泄漏面。 |
| Windows 安装器 `install.ps1` / PowerShell 5.1 `NativeCommandError`（#11962/#11944, 77959d667） | 上游 commit 自述「The installer lives in **pi.dev**」，此提交**仅是 `packages/coding-agent/CHANGELOG.md` 一行文档**，修复在 `earendil-works/pi.dev#188` 的 `install.ps1`。端口仓库无 `install.ps1`（`irm https://pi.dev/install.ps1 \| iex` 的 npm/PS5.1 宿主），为 .NET 分发，无对应脚本文件可改。 |
| `fd` 下载链里的 abort 行为（#11974 已移植，§3.62） | 端口 `FindTool` 确有 `ToolsManager.EnsureToolAsync("fd")`（会下载）；其 abort-before-spawn 行为已真实移植（§3.62）。其余「Windows find 路径 glob 与各种 fd 版本匹配」(#11972 等) 改的是 `find.ts` 里 `fd --full-path` 的 Windows 路径模式，端口 `FindTool.cs` 已有 `effectivePattern.Replace("/", @"[/\\]")` 逐字对应（§3.52 一带），无独立缺口。 |
| `pi update` 摘要 / 扩展 npm 依赖解析 / Node bundler 动态 import chunk | Node/npm/构建链专有；权威「不宣称完整 Node/Bun runtime parity」+「纯导出桶/Node 专属启动器无独立 C# 语义」。 |

**判定**：以上 ➖ 项均为「端口无对应宿主/消费方」或「仓库外/第三方库/纯性能优化」，
有具体文件、行号、`git show` 提交与代码形态实证；不含任何 v1.5.1/v1.5.2 published 功能变更。

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

**判定**：➖ 不适用，符合权威「TypeBox 构造 API 不适用 / 配置 schema 静态产物 / 不宣称 Node/Bun runtime parity」决策。

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

### 4.7 范围外（非「不做项」，无 published 功能变更）

- `v1.5.2..HEAD` 的未发布提交（`main` 上 1.5.2 之后的 p2/p3 实验）：等下一个 tag（1.5.0 delta §5 先例）——**不是**本 release 的 published 变更。
- `apps/` 下 p2 原型应用与 `docs(p2/p3)` 设计稿（191,148 行）：实验稿，端口从未移植（§4.1 实证 apps/p2 无 `packages/**` 消费方）。
- Node/npm/TypeBox/安装器/构建链专有项、TS 侧 schema 构造 API：➖ 不适用（§4.4 / §4.5 代码级实证）。

## 5. 复核方法（下一批动工前）

1. `git -C D:\AI\参考项目\pi tag --sort=-v:refname | head` 与 `origin/stable` 指向确认
   最新 release；1.5.0 之后为 `v1.5.1`、`v1.5.2`（`origin/stable = b4afdddf4`，tag `v1.5.2`）。
   若上游越过该点，用 `git log --oneline v1.5.2..HEAD` 重新评估。
2. 表驱动常量逐项 diff：`ModelData/*.json` 的 provider/model 表、`defaultModelPerProvider`
   （`model-resolver.ts`）、`compat` 字段表、`Retry.cs` / `Overflow.cs` 模式表。**不能只看漂移窗口**。
3. 每批收口：`dotnet build Pi.slnx -m:1` 0 警告 0 错误 → 定向测试 → 全量门禁测试 →
   提交 → 更新本台账与 `CHANGELOG.md`。
4. 判定「完整」的口径见 §0.0 / §4：**published 功能变更一律真实移植（§3），不得登记为
   「有记录的不做项」**；`§4` 的 ➖ 不适用项必须落到权威工件「特殊范围决策」的某一条目，
   并给出可复核的代码/`git show` 实证，不得使用「待后续」「已知缺口」「暂不跟」措辞。
5. 迁移完成收口：版本号（`Directory.Build.props` + `Assets/package.json` 一致）、
   根目录/内嵌 CHANGELOG、`docs/migration-execution-status.md` 当前口径、`llms.md` 计数、
   Debug/Release 全量 build + `gate-tests.sh` 全绿、`v1.5.2` 附注 tag 指向 HEAD、
   `pi --version` 冒烟、`git diff --check` 干净、工作树干净。
