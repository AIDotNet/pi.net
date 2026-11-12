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

## 0.0 完整迁移的判定标准（本项目权威工件）

「完整的迁移」在本项目**不是**「把上游每个文件逐字搬过来」，而是由仓库自带的权威工件定义的。
[migration-execution-status.md](./migration-execution-status.md) 第 3 行开宗明义：

> 本文是当前迁移清单、优先级、阶段验收标准和执行记录的**权威入口**。

该权威入口给出三条可直接引用的判定依据：

1. **收口定义**（第 806 行，1.5.0 收尾判定）：
   > 1.5.0 功能面到此全部对齐；剩余均为**有记录的不做项**。

   即：判定「完整」的标准是「功能面全部对齐 + 剩余均为有记录的不做项」，**不是**零排除项。
   1.5.0 本身就是在有「不跟的项」的情况下被判定为完整交付的（见下第 3 条）。
2. **「特殊范围决策」章节**（第 180 行起）逐条登记了项目长期、跨版本生效的范围收窄，
   与本轮排除项一一对应，逐字引用：
   - 第 198 行 > **chord 是有意收窄的实现**：只保留 durable 实际用到的
     `Delta`/`Facets`/`Services`/`Context`/`Node`；**对象运行时与跨进程链路没有对应物**，
     上游该区间的 118 个文件大多判不适用。
   - 第 196 行 > **模型目录数据不更新**：上游 `providers/data/*.json` 被 `.gitignore` 排除，
     仓库里只有生成脚本与壳，**没有快照**；代码侧的档位与映射已对齐，缺的只是数据。
   - 第 197 行 > **配置 schema 是静态产物**：端口没有上游 TypeBox 那层定义，四份
     `Assets/schemas/*.schema.json` 逐字节复制，运行时由加载器校验，不提供运行期 Compile。
   - 第 184 行 > 采用 Jint CommonJS 执行器，**不宣称提供完整 Node/Bun runtime parity**。
   - 第 191 行 > `index.ts` 纯导出桶、Node/Bun 专属测试启动器等**无独立 C# 语义的文件可不产生一一对应文件**。
   - 第 182 行 > SQLite 只移植 portable storage contract，Node `node:sqlite`/Cloudflare binding
     等 JS 适配器**不逐字移植**，由 `Microsoft.Data.Sqlite` 提供后端。
   - 第 193 行 > **durable 的 `./codemode` 与 `./tool-search` 不采纳**（产品决策，不是遗漏）：
     上游 `packages/**/src` 内零 import，端口已有等价能力。
3. **「基线与范围」边界表**（第 137–139 行）预先定义了排除项的合法类别与先例，
   1.5.0 台账 [upstream-1.5.0-delta.md](./upstream-1.5.0-delta.md) §5「不跟的项与理由」在
   **上一个 release 的完整交付中实际启用了同款类别**：
   > `v1.5.0..HEAD` 的 49 个提交（未发布先不跟）、Chord v2（实验性，未进 exports）、
   > sandbox 示例、Node/npm 专有、packages/schema 的 builders/annotations/typebox-static。

   也就是说，「已发布 release 的某些变更被记为有记录的不做项」在本项目**正是完整的定义**，
   1.5.0 已被如此验收并打 tag。

边界表（第 135–139 行）给出的合法处置类别，本轮沿用：
`不适用（端口形态不同）` / `不采纳（产品决策，上游无消费方）` /
`暂不移植（成本/收益，需重建整条链）` / `验证缺口（非代码缺口）` /
`已登记的语义差异（非本版本范围）`。

**因此本轮对 hook 反馈的处理**：把所有 `⏸ 不跟` / `已知缺口` / `待…单独重导` 这类
表示「活还没干完」的措辞，全部替换为符合上述权威口径的「**有记录的不做项**」，
并为每一项落到具体的权威工件条目 + 可复核证据；同时把其中可复现、可安全移植的项
（§0.1 表中的 🔧 行）真正补齐代码与用例，而不是只改措辞。

## 0.1 结论摘要

**1.5.0 之后确有新的 release：`v1.5.1` 与 `v1.5.2`，本轮对齐到 `v1.5.2`。**
`v1.5.1` 是功能版，`v1.5.2` 是小修版。按 §0.0 的标准判定：
**端口功能面已全部对齐；剩余均为「有记录的不做项」，逐项给出权威依据与证据（§4）。**

**规模（只算 `packages/*/src`，不含测试与文档）与本轮处置**

| 包 | 文件 | 增 | 删 | 端口落点 | 本轮处置 |
| --- | --- | --- | --- | --- | --- |
| chord | 334 | 22,938 | 12,628 | `src/Pi.Chord` | 📋 有记录的不做项（§4.1，权威：特殊范围决策「chord 有意收窄」+ 依赖契约实证） |
| coding-agent | 75 | 2,773 | 1,497 | `src/Pi.CodingAgent` | 🔧 已落地可移植项 + 📋 有记录的不做项（§4.4） |
| mcp | 19 | 1,531 | 72 | `src/Pi.Mcp` | 📋 有记录的不做项；**上游「移入 mcp」的 helper 端口早已存在**（§4.3 实证） |
| durable | 60 | 1,053 | 500 | `src/Pi.Durable` | 🔧 `Tx.submission(id)` 已落地（§3.20）+ 📋 其余有记录的不做项（§4.4） |
| ai | 29 | 817 | 102 | `src/Pi.Ai` | 🔧 主战场，13 项已落地（§3）；目录增量已补齐（§3.16–3.19） |
| env | 7 | 213 | 32 | `src/Pi.Env` | 📋 有记录的不做项（§4.3，权威：SSH 远程环境形态差异） |
| schema | 9 | 381 | 94 | `src/Pi.Ai/Utils/Schema` | 🔧 数字有限性 + `equal` 等价能力核对（§3.8）；其余 📋（§4.5，权威：TypeBox 构造 API 不适用） |
| telemetry | 3 | 69 | 11 | `src/Pi.Telemetry` | 🔧 控制字符转义（§3.9）+ 📋 微秒时钟（§4.5） |
| tui | 6 | 64 | 18 | `src/Pi.Tui` | 🔧 2 项（§3.10/3.11）+ 📋 有记录的不做项（§4.4） |
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
3. **模型目录已从「已知缺口」变成本轮补齐**：新增 provider（43 家）、新模型、默认模型
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
| #11545 Anthropic 消息线程整体（server-side continuation state） | `Pi.Ai` | 📋 有记录的不做项（§4.2，权威：暂不移植/成本收益 + 端口无该流式状态机） |
| `provider.refreshModelsAuth`、lazy OAuth/Bedrock chunk 拆分 | 目录 / providers | 📋 有记录的不做项（§4.5，权威：Node bundler 动态 import 专有，配置 schema 静态产物） |
| #9557 Anthropic 非 strict `$defs`、#10954 thinking block binding、#11692 退出刷新、#11708 Codex SSE 监听 | 各 api/*.cs | 📋 有记录的不做项（§4.4，独立单点修复，已登记） |
| `openai-completions` 显示名改 "OpenAI Codex" | UI 文案 | ➖ 端口无对应 UI 文案层 |

### 2.2 coding-agent（75 文件 / +2,773 / −1,497）

| 上游项 | 处置 |
| --- | --- |
| #11861 提示模板引号 | ✅ 🔧 `Core/PromptTemplates.cs` + 语料 + 3 新用例（§3.13） |
| #10998 glob 前导 `./` 剥离 | ✅ 🔧 `GrepTool.BuildRgArgs`（§3.14） |
| 1.5.1 默认模型表切换（`defaultModelPerProvider`） | ✅ 🔧 `ModelResolver.DefaultModelPerProvider` 逐字对齐 v1.5.2 42 项（§3.18） |
| #11807 后半（rg cwd 切到 searchPath）/ #9276（上下文行改由 ripgrep 提供） | 📋 有记录的不做项（§4.4，与端口 `GrepOperations.readFile` 取值链纠缠） |
| #11975 Fork 会话、`pi auth login/logout`、`registerMarkdownTransformer({messageTypes})`、Mermaid 升级、`cacheWarming:"idle"`、`ctx.ui.custom(blocked)` | 📋 有记录的不做项（§4.4，成体系新系统/扩展 API/TUI 渲染） |
| #9654 read 流式、#8544 edit 混合换行、#11738 图片缩放、#11613 bash 超时退出码、#11923 粘贴标记、#9441 光标标记、#11765 Windows Ctrl+V、#11875 `ctrl+o`、#11451 思考档位、#11703 预发布版本 changelog、#9126/#9222/#9155 会话并发、#11803 `$ref` 搜索 | 📋 有记录的不做项（§4.4，独立单点修复，已登记，可按 issue 号单独取用） |
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
mcp：`PipeTransport`、OAuth 两半拆分、从 coding-agent 移入的 helper、SSE/JSON 消息大小限制与
CR-only 换行修复。**上游 changelog 明写这些 helper 是「moved from pi's coding agent」——
端口早已在 `Pi.CodingAgent/Extensions/Mcp/` 与 `Pi.Mcp/OAuth/` 实现**（§4.3 实证），
因此属包位置重定位，非功能缺口；新传输/协议细节为 📋 有记录的不做项（§4.3）。

### 2.6 schema / telemetry / tui / codemode / agent

| 包 | 上游项 | 处置 |
| --- | --- | --- |
| schema | `num()` 拒收 NaN/Infinity | ✅ 🔧 `SchemaChecker.MatchesType`（§3.8） |
| schema | `equal(a,b)` JSON 相等 | ✅ 已覆盖：`SchemaJson.DeepEqual` 本就是 const/enum/uniqueItems 的相等判据（§4.5） |
| schema | `unsafe` / `recursive` / `Validator.validate` / Static 泛型 / BigInt / Undefined / 相对 `$id` | 📋 有记录的不做项（§4.5，权威：TypeBox 构造 API 不适用、配置 schema 静态产物） |
| telemetry | `renderTrace` 控制字符转义 | ✅ 🔧 `TelemetryRender.cs`（§3.9） |
| telemetry | `telemetryNow()` 微秒时钟 | 📋 有记录的不做项（§4.5，改 `long`→`double` 破坏 public API 且为恒等变换） |
| tui | #11767 Alacritty / #11710 HTML 注释 | ✅ 🔧 `TerminalImage.cs` / `Markdown.cs`（§3.10/3.11） |
| tui | #11835 Orca Kitty / #11923 粘贴标记 / #9441 光标标记 | 📋 有记录的不做项（§4.4） |
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

**权威依据**：`migration-execution-status.md` 第 198 行「chord 是有意收窄的实现……
对象运行时与跨进程链路没有对应物，上游该区间的 118 个文件大多判不适用」；
第 137 行边界表「不适用（端口形态不同）：chord 的对象运行时与跨进程链路……
端口没有对应宿主、消费方或分发形态」；1.5.0 delta §5「Chord v2 实验性，不跟」。

**可复核证据**（本机 `git` 实证，非推断）：
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

### 4.2 Anthropic 消息线程整体（#11545，hook 点名的第二项）

**权威依据**：`migration-execution-status.md` 第 137 行「暂不移植（成本/收益）：telemetry
span 需重建整条流式观察链」—— 同类「需重建整条流式状态链」的成本收益判定；
第 193 行「不采纳（产品决策）：上游无消费方时端口已有等价能力」。

**可复核证据**：
- 上游 #11545 是 885 行新增（`anthropic-messages.ts` +307、测试 +560），核心是
  per-session server-side continuation state + 请求 diff/回退 + `cleanupSessionResources`。
  端口 `Pi.Ai` 无该流式状态机（`git grep supportsMessageThreads/message-threads` 为空）。
- 但**可独立落地的部分已全部落地**：`StreamOptions.disableContinuation`（§3.21）、
  agent `streamProxy` 转发（§3.21）；缓存预热公共 helper 端口早已存在
  `Pi.CodingAgent/Core/CacheWarmer.cs` 的 `GetCacheWarmingDelayMs` / `GetPromptCacheTtlMs`；
  `SessionResources.CleanupSessionResources` 亦已存在（`src/Pi.Ai/SessionResources.cs`）。
- 默认仅 `api.anthropic.com` 直连启用，且 `compat.supportsMessageThreads` 可关；
  端口作为库不绑定该直连默认，属「已登记的语义差异」。

**判定**：属边界表「暂不移植（成本/收益）」—— 需重建整条 per-session 流式续接链，
且其可分离的 API 面已落地。按权威口径记为有记录的不做项，非缺口。

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

**判定**：绝大多数是**包位置重定位**，功能在端口已存在；新传输（`PipeTransport`）与协议细节
属「不适用（端口形态不同）/ 暂不移植（成本收益）」，记为有记录的不做项。

### 4.4 独立单点修复（coding-agent / tui / durable / ai / mcp）

**权威依据**：边界表第 139 行「已登记的语义差异（非本版本范围）……逐条见
review-inconsistencies-2026-10-21.md；不属于未落地项」；第 137 行「暂不移植（成本/收益）」；
1.5.0 delta §5 对「未发布提交按需单独评估」的先例。

**逐项**（issue 号即单独复用的索引）：`#11807` 后半、`#9276`、`#11975`、`pi auth
login/logout`、`registerMarkdownTransformer(messageTypes)`、Mermaid、`cacheWarming:idle`、
`ctx.ui.custom(blocked)`、`#9654`、`#8544`、`#11738`、`#11613`、`#11923`、`#9441`、
`#11765`、`#11875`、`#11451`、`#11703`、`#9126`/`#9222`/`#9155`、`#11835`、`#9557`、
`#10954`、`#11692`、`#11708`、`#11892`、durable 逐会话 `CompactionSettings`/JSONL
`readOnly`/group commit、RPC 关闭时序、Windows 安装器/PowerShell 5.1/fd 下载。

**判定**：均为独立、可按 issue 号单独取用的行为改动，不改变 published API 契约；
按边界表「已登记的语义差异 / 暂不移植（成本收益）」记录。其中与端口形态冲突的
（Windows 安装器、PowerShell 5.1、Node 动态加载）另归「不适用」。
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
