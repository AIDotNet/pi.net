# 上游 v1.5.0 差异台账（v1.4.0 → v1.5.0）

> 生成时间：2026-11-03
> 参照源：`D:\AI\参考项目\pi`，`v1.5.0` = `f9a263655`（2026-11-02）。当前检出 `9c9ce854`（`v1.5.0+49`，未发布提交，按既有策略**先不跟**）。
> 目标仓库：`D:\AI\参考项目\pi.net`，起点 `1.4.5`（`ac11ead`）。
> 口径：`v1.4.0`（`4a080252f`）`..v1.5.0`，共 **308** 个提交；触及 coding-agent 87、chord 150、durable 43、ai 32、tui 23、env 11、agent 7、mcp 7、schema 6、codemode 5、telemetry 5、evals 5。

## 0. 结论摘要

**规模（只算 `src/`，不含测试与文档）**

| 包 | 文件 | 增 | 删 | 端口落点 |
| --- | --- | --- | --- | --- |
| coding-agent | 84 | 2,185 | 708 | `src/Pi.CodingAgent` |
| ai | 85 | 1,800 | 1,303 | `src/Pi.Ai` |
| tui | 17 | 443 | 141 | `src/Pi.Tui` |
| agent | 2 | 30 | 16 | `src/Pi.Agent` |
| env | 3 | 74 | 30 | `src/Pi.Env` |
| mcp | 3 | 14 | 4 | `src/Pi.Mcp` |
| schema | 43 | 4,565 | 0 | **新包**，见下 |
| durable | 57 | 8,841 | 2,469 | `src/Pi.Durable` |
| chord | 208 | 37,496 | 234 | `src/Pi.Chord`（其中 v2 未导出，不跟） |

**三条关键判断**

1. **`pi-schema` 不需要新工程**。端口的工具参数本来就是明文 JSON Schema（`ToolSchema(IReadOnlyDictionary<string, object?> JsonSchema)`），不依赖 TypeBox 构造器，所以上游 `pi-ai-schemas.ts` / `schema-builders.ts`（构造器）在 C# 侧没有对应物。真正要跟的只有 **校验 + 强制转换 + 错误文案**，落点集中在 `src/Pi.Ai/Utils/Validation.cs`（现 499 行，TypeBox 语义）。
2. **chord v2 不跟**。`packages/chord/src/next/*` 是实验实现，`package.json` 的 `exports` 未导出（v1 仍是 `index/context/delta/json/watch/output/sqlite/...`）。v1 要跟的不是「3 个运行时修复」——真正的大头是 **watch 家族**（`watch.ts` 492 行 + `delta/pending.ts` 173 行，把 durable 的 `CommittedWatch` 收进 chord 并改了调用形状），见 §4.7；`pi-schema` 切换在端口没有对应物（v1 的声明层未移植），SQLite 门面也未移植。
3. **`durable` 是 1.5.0 第二大块**，且是**对外可见的 API 扩展**（turn limit / continue / afterRun / whenIdle / submit(tx) / 只读 Harness / 窗口化会话视图），不是内部重构，必须单独立批。

## 1. 处置标记

| 标记 | 含义 |
| --- | --- |
| ✅ 已覆盖 | 逐行核对过端口源码，语义一致（含本轮新落地） |
| 🔧 待移植 | 上游行为变更，端口缺失或仍是旧语义 |
| ➖ 不适用 | Node/npm/TypeBox/构建链专有，C# 侧没有对应物 |
| ⏸ 暂不跟 | 未发布提交、实验性未导出代码、示例 |

## 2. 逐子系统差异

> **口径**：§2 各表是**初次评估时**的逐提交视图，行的处置列可能落后于后文的批次记录。
> 判断某项是否完成，以 §4.x 的批次小节（含用例与落点）与 [migration-execution-status.md](./migration-execution-status.md)
> 为准：**同一项在 §4.x 里标 ✅ 就以 §4.x 为准**。截至 §4.27，P0/P1/P2/P3/P5 已收口，P4 只做了两项不改渲染
> 字节的（§4.26），P6 余分组提交 / 节流提交延迟落盘 / `appendText` 的工具参数部分（§2.5 末三行）。

### 2.1 pi-schema（**破坏性变更**，P0）

上游：`fab2c5353`（新包）、`8ffea8a1b`（编译与内存优化）、`6536f8012`（**工具参数改用 pi-schema 校验，删除 TypeBox**）、`6291b9bcc`（`Static` 兼容 TypeBox 构造的 schema）。

行为差异（对端口有意义的全部）：

| 变更 | 上游位置 | 端口现状 | 处置 |
| --- | --- | --- | --- |
| 纯 JSON Schema 工具也做强制转换（数字字符串、单值包成数组、null→缺省） | `coerce.ts` | 已按三遍管线逐条对齐（`SchemaCoercer`） | ✅ P0 |
| 校验失败文案改成 `- path: missing` / `- limit: expected number, got string` | `explain.ts` | 已逐条复刻（`SchemaExplainer`） | ✅ P0 |
| 错误路径 JSON Pointer → 点号路径 | `utils/validation.ts:formatValidationPath` | `Validation.FormatPath` 与上游一致 | ✅ |
| 悬空 `$ref` 只在该位置拒绝，不让整个工具调用失败 | `compile(..., {unresolvedRefs:"reject"})` | `SchemaResolver` 固定 reject 模式，文案 `unresolved reference #/…` | ✅ P0 |
| 每个 schema 编译一次并缓存（WeakMap） | `utils/validation.ts:getValidator` | `ConditionalWeakTable<ToolSchema, ToolSchemaValidator>` | ✅ P0（性能） |
| `format` 参与校验（`compile` 传 `formats`） | `formats/typebox/*` | 19 个 format 已移植（见 §4.2 的缺口） | ✅ P0 |

> 注：`packages/schema` 的 `builders.ts` / `annotations.ts` / `typebox-static.ts` 是 TS 侧的 schema 构造 API，端口用不上；`compiler.ts` 的闭包编译策略在 C# 侧的等价物是「预编译的检查器对象 + 引用身份缓存」，已在 P0 落地。

### 2.2 ai（P2）

| 上游提交 | 变更 | 端口现状 | 处置 |
| --- | --- | --- | --- |
| `f715a15c1` | 流式工具调用参数**增量解析**（不再每个 delta 重解析累计串） | **已有**：`JsonParse.ParseStreamingJson` 用在 OpenAI/Anthropic/Bedrock/Mistral/PiMessages 五处流式点 | ✅ |
| `363ad78bf` | GPT-6.1 Terra（OpenAI / Azure Responses / Codex，tool search、会话中改 thinking、`off`） | 默认模型已是 `gpt-6.1-sol`；Terra 的能力位在目录数据里 | 🔧 P2（数据） |
| `2e0f175bd` | provider 定义重构 + 目录升到 models.dev v2 | 目录是 1.4.0 期快照（42 个 JSON，`src/Pi.Ai/ModelData/`）；`tools/` 里没有生成脚本 | 🔧 P2（数据，需先移植 `generate-models.ts`） |
| `7314819f0` + `a7b94a03f` | Gemini 工具 schema 的 `const` 改单值 `enum`，且不改写 defaults/examples/enum 里的字面量 | 本轮落地 `GoogleShared.RewriteConstAsEnum` | ✅ P2 |
| `1de2d7ade` | Kimi K3.1 Pro 按 K3 家族发请求 | 家族判定在 `generate-models.ts`（目录数据） | 🔧 P2（数据） |
| `842c73170` | Bedrock strict 工具超限时重试一次（preferred 放宽、required 保持 strict） | 本轮落地：两个助手抽到 `ConstrainedSampling.PrefersJsonSchemaStrictSampling` / `IsStrictToolLimitErrorMessage`；`ConvertToolConfig` 回报 `HasPreferredStrictTools`；`BedrockConverseStream.Stream` 命中 400 后降级重发一次并记 `bedrock_strict_tools_fallback` 诊断 | ✅ P2 |
| `81d6759fc` | GPT-5.4/5.5 工具声明放顶层（修 `Missing namespace for function_call`） | 能力位来自目录数据（`supportsAdditionalTools`） | 🔧 P2（数据） |
| `177e2ad7e` | OpenCode Zen/Go 用各自协议端点 | 端点来自目录数据（`providers/opencode.ts` 仅 1 行） | 🔧 P2（数据） |
| `44f6851b7` | OpenRouter `~anthropic/claude-*-latest` 走 Anthropic Messages | 同上（`generate-models.ts` + `openrouter-catalog.ts`） | 🔧 P2（数据） |
| `e3bc05e87` | Anthropic 逐消息 effort 时顶层固定发 `low` | **已有**：`AnthropicMessages.cs:331`，注释已写明「组织可设上限」的理由 | ✅ |
| `3bc6eebac` | Anthropic system 更新要越过被丢弃的空/纯推理 assistant 消息 | **已有**：`AnthropicMessages.cs:598-601`（`blocks.Count == 0` 之后再 flush） | ✅ |
| `89c921d8b` | 不可信 URL / 终端文本 / 流式数据的加固 | ai 侧本轮收口：`OAuthHttp.TrustedHttpUrl` 成为唯一实现（Copilot / Kimi / Meta 三处私有副本删除，Kimi 现在按规范化后的形式保存）；`Cli.SaveAuth` 写 `auth.json` 用 0600 并先收紧旧文件；`PiMessages.ValidateContentIndex` 已在。tui / coding-agent / durable 三块分别在 P4 / P3 / P6 | ✅ P2（ai 侧） |
| `7c22a93d0` | `Usage.cost.totalFromProvider` | 已有 | ✅ |
| `cae8d69e5` | OpenRouter 上报成本 | 已有（`2820568`） | ✅ |
| `d3398f826` | LiteLLM `ContextWindowExceededError` 判为溢出 | 本轮已加模式 | ✅ |
| `c719d5b08` | Zhipu `1308` 不重试 | 本轮已加模式 | ✅ |
| `f3fa8bd92` | faux 缓存写入不再重复计入 input | 已有 | ✅ |
| `14c811841` | pi-messages 终端事件带 `responseModel` | 已有 | ✅ |

> **P2 全部落地**。8 行里 6 行是**目录数据**（`generate-models.ts` 的产物），端口这边是
> 42 个 `src/Pi.Ai/ModelData/*.json` 快照——本轮用 `tools/catalog-export.mjs` 直接跑上游
> 生成器重导（见 §4.3），不移植生成器代码。另外两行是代码（`842c73170` Bedrock strict 回退、
> `89c921d8b` 加固），也已落地。

### 2.3 coding-agent（P3）

**新特性**

| 上游提交 | 变更 | 端口现状 | 处置 |
| --- | --- | --- | --- |
| `b4b72a351` | **扩展包更新 changelog**：读 `package.json` 的 `pi.changelogPath` 或根 `CHANGELOG.md`，下次启动在 "Extension Updates" 下显示新于上次所见版本的内容；`collapseChangelog` 折叠成一行 | 无 "Extension Updates" 渲染 | 🔧 P3 |
| `8bc94b19f` | **自定义自更新命令**：`PI_SELF_UPDATE_COMMAND` / `selfUpdateCommand`，`{packageName}`/`{version}`/`{installSpec}` 占位 | 无 | ➖ 端口不执行自更新（§4.4） |
| `7151099d0` | device-code 登录：回车时打开验证页并复制 code；`openBrowserOnConfirm` / `copyCodeOnConfirm` 可关 | 无 | 🔧 P3 |
| `c1810bf70` | `ctx.ui.custom()` 的 `blocked` 选项（overlay 忽略） | 无 | 🔧 P3 |
| `9376c8d17` | 渲染上下文注入 `keyText()` / `keyHint()`（扩展从包里 import 拿不到宿主键位） | `keyHint` 有，但未注入 render context | 🔧 P3 |
| `64627838b` | `MarkdownTransformContext` 增加 `messageId` / `timestamp`（持久化条目回放时才有） | 本轮已落地 | ✅ |
| `606af8662` | `/model`、`/scoped-models` 在名称比 ID 多词时显示名称 | 未核 | 🔧 P3 |
| `705d047cb` | 系统提示词收紧 | 本轮已落地 | ✅ |

**修复（按子系统）**

| 上游提交 | 变更 | 处置 |
| --- | --- | --- |
| `31ade0e0d` | `trust.json` 里保存的「不信任」优先于自动信任 | 🔧 P3 |
| `b9e3eef6c` | `setActiveTools` 同一组工具换顺序不再触发系统提示词更新 | 🔧 P3 |
| `1be312b46` | Windows 路径比较忽略大小写（skills 重复加载 / trust 重问 / `--continue` 丢会话） | 🔧 P3 |
| `88ffcdc73` | 工作目录被删除时给提示，不再 `uv_cwd` 崩栈 | 🔧 P3 |
| `807a5c051` | 会话写盘失败时内存与文件保持一致（不再产生悬空 parent） | 🔧 P3 |
| `269afc66a` | `/tree` 分支摘要传 thinking level | 🔧 P3 |
| `d0fe21308` | 分支摘要输入应用 context edits | 🔧 P3 |
| `7853186d1` | 对话框打开时替换扩展 editor 不再吞掉对话框 | 🔧 P3 |
| `6dfb54678` | 配置的包安装失败不再崩启动（跳过 + 告警 + 下次重试） | 🔧 P3 |
| `f14813e96` | 技能诊断标签 `[Skill conflicts]` → `[Skill warnings]` | 本轮已落地 ✅ |
| `e7a0df27c` | 图片缩放取 PNG/JPEG 较小者 | 已有 ✅ |
| `0d23d5345` | `pi update --extensions` 报「装到的版本与期望不符」 | 🔧 P3 |
| `cd6a8f7f3` / `bec60c54b` | `-e npm:` 无版本、`-e git:...@branch` 每次启动/`/reload` 刷新 | 🔧 P3 |
| `5f960a0e2` | agent 目录是符号链接时 npm 用真实路径（package-lock 嵌套） | ➖ npm 专有 |
| `e3da248ea` | Windows 上 `git clean` 失败时仍继续装依赖并下次重试 | ✅ `2ac34ad`（见 §4.6） |
| `c466a3595` / `36be9c612` / `c60de42e9` | `pi.exec`：256 MiB 上限 + `truncated`；UTF-8 跨块解码；spawn 错误保留 stderr | 🔧 P2（`core/exec.ts` 182 行） |
| `6454d07ec` | WSL 深目录下从 `/` 启动 `powershell.exe`/`explorer.exe`；外部编辑器失败要报状态 | ✅ `2ac34ad`（见 §4.6） |
| `5fb9a0b11` | 安装包时屏蔽 npm audit/fund 输出 | ➖ npm 专有 |
| `fecc19e4e` | `grep` 上下文行号在含孤立 CR 的文件里与 ripgrep 对齐 | 🔧 P2 |
| `984cf6643` | `--model <provider>/<id>` 支持 provider id 含斜杠 | 🔧 P3 |
| `8ddc44ec8` | fork 会话单 fd 写入（打开文件昂贵的文件系统） | 🔧 P2（待核） |
| `8c03fef4d` | `/resume` 搜索：名称/目录模糊，ID 与消息正文只做子串 | ✅ `2ac34ad`（`SessionSelectorSearch` 51 例，见 §4.6） |
| `8c03fef4d` | `/model` 搜索排序：连续匹配优先 | 🔧 P3 |
| `be15fe3c1` | `pi mcp login/logout -l` | ✅ `4afb050`（`local` flag、作用域校验、拼写建议；见 §4.5） |
| `908aa7b74` | MCP OAuth 默认用 Client ID Metadata Document，否则动态注册（`oauth.clientRegistration: auto/dcr`） | ✅ `2ac34ad`（`McpClientRegistration.Auto` + `McpClientRegistrationTests` 10 例；见 §4.6） |
| `1c720d285` / `9359adf0c` | HTML 导出：数千条目不空白；包含 system prompt/tools | ✅ `2ac34ad`（1.5.0 模板，见 §4.6） |
| `27cc6bfc0` | macOS 先停进程组再 kill | ➖（平台语义，C# 侧另行处理） |

### 2.4 tui（P4）

| 上游提交 | 变更 | 端口现状 | 处置 |
| --- | --- | --- | --- |
| `fa1685152` | 裸 URL 在 CJK 标点处截断 | 本轮已落地 | ✅ |
| `8a2ebd385` | 嵌套引用不再指数膨胀（`width < 3` 直接渲染子节点） | 已有 | ✅ |
| `3bae85274` | `tui.select.pageUp/pageDown` 作用于所有列表；全屏下翻页键翻列表而不是滚 transcript；`capturesViewportInput` | ✅ 本批（`GetSelectPageIndex` + 两个列表实现分页 + `IComponent.CapturesViewportInput` 与全屏路由；上游三例照抄，见 §4.37） |
| `2ce9d527d` | 换行边界：带样式的词不再多出前导空格；删除线不渗进 padding | ✅ 本批（`IsWhitespaceToken` / 丢弃空白时把码留在本行 / 行尾关删除线 / 长词前先 trim；上游六个定向用例照抄；187 例语料按不变量重算，见 §4.33） |
| `deb12d78e` | Kitty 协议 F1–F12（含修饰键）；F13+ 不插入私用区字符 | ✅ 本批（F1–F12 内部码点 / `CSI P/Q/S` / `:3`、`:2` 事件 / f 键修饰键匹配 / F13+ 私用区拒绝；276 例 `editor-corpus` 场景按「只允许私用区字符消失」守卫重算，见 §4.36） |
| `86dc686cf` | Apple Terminal 的 `ESC`+Return 且本地 Shift 按下时按 Shift+Enter（插换行） | ✅ 本批（`ForwardInputSequence` 在 CR / ESC+CR 上按本机 Shift 升级成 `CSI 13;2u`；Windows 走 `GetAsyncKeyState`、macOS 走 CoreGraphics，失败当没按；见 §4.30） |
| `19ce8059c` | 常规模式不再写 OSC 133 提示符标记 | ✅ 本批（主屏在合成前剥掉行首的 `133;A/B/C`，BEL 与 ST 两种结尾都覆盖；见 §4.28） |
| `0f21a3319` | Markdown 按顶层块缓存渲染结果；代码高亮复用 | ✅ 块级复用已落地（§4.31）；**代码高亮复用与 `Text.setText` 折行缓存未做**（见该节「未移植」） |
| `cd1ea342a` | 显示公式超宽时回退原始 LaTeX | ✅ 本批（2D 排版的任一行宽过内容宽就整段退回源码；上游回归用例照抄；42 例语料按「出现原始标记」守卫重算，见 §4.35） |
| `4464442f9` | 全屏下滚出视口的 Kitty 图片按行裁剪 | ✅ 已由 `1c8cb1781` 的定界搜索覆盖（占位行不再是停止条件，只有图像行才停；见 §4.34） |
| `ff6896471` | 全屏双击选词按 iTerm2 的 word 字符集（`~ . \ +` 不再切词） | ✅ 本批（`TerminalWordSelectionJoiners` 加上 `+ \ ~ _ .`；端到端用例未移植，见 §4.28） |
| `f09d01b85` | Orca 终端识别（truecolor / hyperlink / `ORCA_IMAGE_PROTOCOL=kitty`） | ✅ 本批（`DetectCapabilitiesFromEnvironment` 的 Orca 分支：`TERM_PROGRAM=Orca` 且 `ORCA_IMAGE_PROTOCOL=kitty` 才给 Kitty，否则文本回退；见 §4.26） |
| `89c921d8b` | 终端标题/控制序列注入加固；`stripAnsi` 去掉带 `<=>` 前缀的私有 CSI | ✅ 核对完毕（§4.29）：tui 侧 `Ansi.ReplaceControlCharacters` 已用于两处 `SetTitle`、`ProgramStatus` 的 `msg` 与 `LoginDialog` 的 URL；durable 的 `__proto__` 那条是 JS 原型污染，C# 无对应物 |
| `1c8cb1781` | 图片行搜索按最高注册图片限界 | ✅ 本批（`TerminalImage.MaxKittyImageRows` 给 `Layout.PaintBox` 的倒序搜索定界，并跳过非图像行；见 §4.26） |

### 2.5 durable（P6）

| 上游提交 | 变更 | 处置 |
| --- | --- | --- |
| `f6a9db1d8` | 每会话 turn limit | ✅ 本批（持久化 `runFailure` 记录随 `f3255de76` 落地） |
| `fb524dd2e` | continue 提交 | ✅ 本批 |
| `7e15f9601` | run-end tasks + `afterRun` hook | ✅ `finish`/`drain` + `run.afterRun`（§4.17）；`run.first` 与重启越限收场已补（§4.19） |
| `65fba65b2` | hook/task 的 entry reads | ✅ 本批 |
| `cc9c52310` | `submit(tx, draft)` 在调用方提交内接纳 | ✅ 本批（`Session.OwnsTransaction` 品牌检查、`AdmitInCommitAsync`、两个 `SubmitAsync(ITx, …)`；见 §4.25） |
| `6a2825113` | `whenIdle` 拒绝空闲会话的输入 | ✅ 本批 |
| `1c6928b30` | 未提供的工具调用按 tool task 跑 | ✅ 本批 |
| `87eff524f` | `afterTool` 在每个 tool task 结果上跑 | ✅ 本批 |
| `d280e1ffa` | `beforeProviderRequest` 生成钩子 | ✅ 本批（端口原有 `BeforeRequest` 是消息层，这是请求体层，两者并存） |
| `f3255de76` | 窗口化会话视图 + `ConversationTranscript` + `runFailure`；删除 `watchEvents` | ✅ `runFailure`（§4.11）、窗口化视图与转录（§4.21）、删 `watchEvents`（§4.22） |
| `044148392` | Code Mode 脚本 `read` 返回图片块 | ➖ durable 的 Code Mode 按 1.4.0 的决定不采纳（见 §4.8） |
| `be14d9732` | 只读打开 Harness | ✅ 本批 |
| `da412b039` | `appendText`：partial 提交按追加写文本，不再重赋整段字符串 | 🟡 durable 侧文本/思维增量已按追加写（§4.27）；chord 侧端口本来就有等价物（`Change.Append` 直接记追加 op）；**工具调用参数仍整体赋值**——上游用 `StreamingJsonParser` 的增量改参数，端口没有这个解析器 |
| `6c2fd73b0` | 节流提交延迟落盘（`persistence: {kind:"deferred", withinMs}`） | 🔧 P6 |
| `e5c6182ba` | 分组提交 + 批量 `Storage.commit` | 🔧 P6 |
| `35dfa7573` | 空闲检查与 `wake()` 不再每次提交全扫 | ✅ 脏集合 + 全量回退（§4.15）+ `idleOrder` 索引与睡眠集合（§4.23） |
| `c7372af42` / `79006f061` | 工具搜索排序与 CJK 分词 | ✅ 已在（`ToolSearch.cs` 的覆盖优先 / 名称权重 / 精确名 / 命名空间 0.5x，以及 NFC + CJK bigram 分词；见 §4.24） |
| `d5b3de22d` | assistant 消息记录请求的 thinking level | ✅ 本批 |
| `38ff99177` | 工具交接后继续运行 | ✅ 本批 |
| `b3a29f111` | 重启循环可见地结束；宿主死亡计入一次 generation 尝试 | ✅ 本批（见 §4.19） |

### 2.6 env / mcp / telemetry / evals（P5）

| 上游提交 | 变更 | 处置 |
| --- | --- | --- |
| `102d12871` | 变更列表超过帧上限时报 watch overflow | ➖ 守护进程按原生产物（§4.5 注） |
| `b6d5c2ecd` | daemon 被终止时杀掉它启动的命令 | ➖ 同上（§4.5 注） |
| `92c1da1f6` | 辅助线程起不来时 `exec`/`pty` 返回 `spawn_error` | ➖ 同上（§4.5 注） |
| `479d6e5e2` | 启动错误带 stderr 尾部（20 行 / 2000 字符，去转义） | ✅ `4afb050`（`Pi.Env/Connection.cs` 的 `StartupErrorText`；见 §4.5） |
| `9ffd27cfa` | MCP OAuth 只接受 http/https | 已有 ✅ |
| `6c384aa30` | workspace 测试从源码解析 pi-telemetry | ➖ |
| `936e5f40a` / `9628c8480` | evals 去掉 autoevals 依赖、加提示词模板文档评测 | ➖ |

### 2.7 chord（P7）

| 上游提交 | 变更 | 处置 |
| --- | --- | --- |
| `93029a0f1` 等 ~140 个 | Chord v2（`src/next`，实验、未导出） | ⏸ 不跟 |
| `be039fb31` | Chord v1 与 v2 切到 pi-schema，删 TypeBox | ➖ 端口无 TypeBox 构造面（v1 的 `method()`/`remote()`/`local()` 声明层未移植，见 §4.7） |
| `50d9f823e` | 工具输出里整段 ANSI 序列要删干净 | ✅ `a0da1ea`（落 `Pi.Durable/Harness/ToolOutput.cs`） |
| `c09c98d7d` | relay 消失后仍接纳已路由的 host | ➖ chord v2 的 route/relay 层未移植 |
| `db14038bd` | Node SQLite 驱动预编译语句缓存加上界 | ➖ 端口无 chord SQLite 门面，`Microsoft.Data.Sqlite` 无该缓存 |
| `8025c414d` | `SqliteExecutor.runCounted` 返回影响行数 | ➖ 同上（`ExecuteNonQueryAsync` 本就返回影响行数，端口无消费方） |
| `d0aa218cb` | Delta tracker 容器拷贝降本 | ➖ JS 对象拷贝与 V8 子串驻留专有 |
| `a2fd25f56` / `5e106ab1c` / `42cb822f1` | `Watchable` / `NativeWatchable` / 共享 watch handle | ✅ 本批（`Pi.Chord/Services/Watch.cs`） |
| `8b0caefc2` / `d8d99950a` / `da412b039` | 采纳时发布提交、按地址跟随的 watch、同步 observer、`PendingOps` | 🟡 `PendingOps` 与 watch 调用形状已落地；采纳时发布、按地址跟随与同步 observer 归入 P6 |

## 3. 批次计划

| 批次 | 范围 | 规模 | 验收 |
| --- | --- | --- | --- |
| **P0** | pi-schema 校验/转换/文案对齐（`Validation.cs` 重写 + 编译缓存 + 悬空 `$ref`） | ~1,500 行 | 已过：368 条上游语料差分全绿（见 §4.2） |
| **P1** | 本轮已落地的小修集（见 §4） | ~80 行 | 已过 |
| **P2** | ai wire 层：Gemini const、Bedrock strict 回退、加固、目录数据重导 | ~1,200 行 + 42 份数据 | **已完成**：代码三项 + 数据子批（见 §4.3） |
| **P3** | coding-agent：扩展 changelog、自更新命令、device-code 确认、`blocked`、`keyHint` 注入、trust/路径大小写/写盘一致性/分支摘要/启动容错等 ~18 项 | ~900 行 | `InteractiveModeTests` + 新增定向用例；全量 0 失败 |
| **P4** | tui：翻页键与 `capturesViewportInput`、换行/删除线、Kitty F 键、OSC 133、块级渲染缓存、LaTeX 回退、图片裁剪、双击词字符、Orca | ~700 行 | `Pi.Tui.Tests` 全量 + 上游用例照抄 |
| **P5** | env 4 项 + mcp 2 项 + 工具搜索排序/CJK 分词 | ~500 行 | `Pi.Env.Tests` / `Pi.Mcp.Tests` / 搜索排序向量 |
| **P6** | durable：turn limit、continue、afterRun、entry reads、submit(tx)、whenIdle、tool task、窗口化视图、只读 Harness、分组提交 | ~2,000 行 | `Pi.Durable.Tests` 全量 + 新 API 契约用例 |
| **P7** | chord v1：pi-schema 构造点、ANSI 清理、watch 家族 | ~600 行 | `Pi.Chord.Tests` 全量（首批已过：135 例，见 §4.7） |

依赖：P0 先于 P2（ai 的工具校验在 P0 里换语义）；P3/P4 互不依赖；P6 与 P0 无耦合，可并行。

**语料阻塞已有实证**（§4.28 末）：`deb12d78e`（Kitty F13+ 不再当文本）实现后 `Pi.Tui.Tests` 立刻红 43 例，
失败信息是「期望 `\uE008` 被插入」——那正是 1.4.x 语料里的旧行为。**任何改变 keys/渲染结果的 P4 项都必须先重算语料**。

**P4 的前置条件**：`Pi.Tui.Tests` 的 `markdown-corpus.json` / `alt-screen-corpus.json` 是从
**1.4.x 的 TS 源码**在 Node 里跑出来的。P4 里任何改变输出字节的修复（删除线行尾 `29m`、
换行边界空格、CJK URL 截断的渲染形态）都会让语料对不上。**动 P4 前先恢复语料生成链**
（上游 `npm install` + `tools/gen-tui-*-corpus.mjs` 重跑），否则只能改实现不能改语料。
本轮的 CJK URL 截断只改了分词器、未改渲染字节，所以没触发语料重算。

## 4. 本轮已落地（P1，`677e30c`）

| 变更 | 上游 | 落点 | 测试 |
| --- | --- | --- | --- |
| 系统提示词收紧（前缀/工具段/文档段三条文案） | `705d047cb` | `Core/SystemPrompt.cs` | 新增 `SystemPromptTests` 3 例 |
| 技能诊断标签 `[Skill warnings]` | `f14813e96` | `InteractiveMode.Bind.cs` | 由既有状态用例覆盖 |
| Zhipu `1308` 不重试 | `c719d5b08` | `Pi.Ai/Utils/Retry.cs` | `RetryTests` +1 |
| LiteLLM `ContextWindowExceededError` 判溢出 | `d3398f826` | `Pi.Ai/Utils/Overflow.cs` | `OverflowTests` +1 |
| 裸 URL 在 CJK 标点处截断 | `fa1685152` | `Pi.Tui/Marked/MarkedTokenizer.cs` | `MarkdownCjkTests` +1 |
| `MarkdownTransformContext.messageId/timestamp` 全链路 | `64627838b` | `ExtensionEvents.cs` / `MarkdownTransform.cs` / 两个消息组件 / `InteractiveMode.Events.cs` | `MessageComponentsTests` +1 |

**验证**：`dotnet build Pi.slnx -m:1` 0 警告 0 错误；`Pi.CodingAgent.Tests` 5118 例（排除
`PackageManagerTests`）1 失败（见下）、`Pi.Ai.Tests` 563 例、`Pi.Tui.Tests` 23942 例。

两个**既有**环境/并发问题，与本批改动无关（已在干净工作区复现）：

- `UpstreamTypeScriptExampleTests` 的 6 个示例因 `Cannot find module 'typebox'` 失败。语料里的示例
  `import { Type } from "typebox"`，而解析器按文件位置向上找 `node_modules`；上游 1.5.0 升级后
  `D:\AI\参考项目\pi\node_modules` 已不存在（且 1.5.0 已删掉 TypeBox 依赖），解析自然失败。
  处置：**P3 一并把语料换成 1.5.0 版示例**（示例已改成从 `@earendil-works/pi-ai` 取 `Type`），
  或给 JS 宿主补 `typebox` 兼容模块。
- `Pi.Ai.Tests` 的 `AnthropicFederationWireTests` / `OpenAiCompletionsTests` 与 `Pi.Tui.Tests` 的
  `AltScreenCorpusTests` 在**三个套件并行**时偶发失败（SSE 时序、StringBuilder 容量），单跑全绿。

### 4.2 P0 已落地：pi-schema 校验语义（`367ae68`）

**新增（`src/Pi.Ai/Utils/Schema/`，约 1,500 行）**

| 文件 | 内容 | 上游对应 |
| --- | --- | --- |
| `SchemaJson.cs` | JS 值语义：`typeof`/`Number.isInteger`/`Object.is`、JSON Pointer 转义、`deepEqual`、`String(number)`（ECMAScript `Number::toString`）、`JSON.stringify`（含 `, null, 2` 缩进） | `checks.ts` 的谓词 + JS 运行时 |
| `SchemaResolver.cs` | `$id`/`$anchor` 索引、JSON Pointer 解析、`unresolvedRefs: "reject"` | `resolver.ts` |
| `SchemaChecker.cs` | 布尔检查：type/enum/const/字符串/数值/对象/数组各组关键字、`allOf`/`anyOf`/`oneOf`/`not`/`if`/`$ref`、`isMultiple`（十进制 BigInt 算法） | `compiler.ts` + `checks.ts` |
| `SchemaCoercer.cs` | coerce 三遍管线：可选 null 丢弃 → TypeBox 1.3.27 转换（`tryString/tryNumber/tryBoolean/tryNull`、`convertLiteral/convertEnum/convertUnion/convertMembers`、单值包数组） → 按 `type` 的原语强制；含 `reenter`/`memo` 防递归 | `coerce.ts` |
| `SchemaExplainer.cs` | 错误遍历与文案（`expected X, got Y`、`missing`、`unknown field`、`matches more than one alternative`、枚举截断、tagged union 归因、`$ref` memo） | `explain.ts` |
| `SchemaFormats.cs` + `.Regexes.cs` | 19 个 format（正则由 `tools/schema-oracle/formats.mjs` 从 TypeBox 源码生成，`\d`→`[0-9]`、尾 `$`→`\z`） | `formats/typebox/*` |
| `ToolSchemaValidator.cs` | `Compile` / `Check` / `Coerce` / `Errors` / `Parse`，对应 `compile(schema, {formats, coerce:true, unresolvedRefs:"reject"})` | `validator.ts` |
| `Validation.cs`（重写） | 门面：`ConditionalWeakTable` 按 `ToolSchema` 缓存编译结果；错误拼成 `  - path: message` | `utils/validation.ts` |

**差分验证（语料 + 重跑方式）**

- 语料：`tests/Pi.Ai.Tests/Fixtures/schema-validation-corpus.json`，**368 条**（222 通过 / 146 失败 / 154 条错误）。
- 生成器：`tools/schema-oracle/gen.mjs`（`tools/` 不入库）。它把上游 `packages/schema/src` 拷进沙盒，
  用 Node 22 直接跑 `.ts`，对每个 `(schema, value)` 记录 `parse()` 的 `{ok, value}` 或 `errors`，
  并断言「`parse` 成功 ⇒ `check(coerced)` 为真」。
- 覆盖：coerce 每条规则（含 `"0x10"`、`"1,5"`、`"Infinity"`、`true/false/null` 互换、单值包数组、
  可选 null 丢弃/保留、`$ref` 递归树、悬空 `$ref`）；全部错误关键字（27 类）；19 个 format 各 4~5 例；
  31 组 `String(number)` 量级（`1e21`/`1e-7`/`5e-324`/`2^53` 等）；5 个真实工具 schema（edit/bash/read/grep/union/ref）。
- 重跑：`node --no-warnings tools/schema-oracle/gen.mjs` → 重新生成语料 → `dotnet build tests/Pi.Ai.Tests` →
  `Pi.Ai.Tests.exe --filter-class "*SchemaValidationCorpusTests*"`。
- **负控已做**：手工改坏一条期望文案后该用例立刻失败（1 失败 / 368 通过），确认语料有判别力。

**已知缺口（有意为之，记录在案）**

| 缺口 | 后果 | 理由 |
| --- | --- | --- |
| `unevaluatedProperties` / `unevaluatedItems` | 当作注解（不校验），比上游宽松 | 需要整套 annotation 收集机制；无工具 schema 使用；端口历来如此 |
| `$dynamicRef` | 当作注解 | 同上（上游只在 bookended `$dynamicRef` 时启用动态作用域） |
| `format: hostname` / `idn-hostname` | 当作注解（接受任意值） | TypeBox 走 IDNA 标签表（punycode + bidi 规则，509 行）；无内置工具使用 |
| `format: iri` / `iri-reference` / `url` | 用 `Uri.TryCreate` 近似 `URL.canParse` | .NET 没有 WHATWG URL 解析器；差异集中在畸形输入 |
| `pattern` 的方言 | 用 .NET 正则（`\d`/`\w`/`\s` 是 Unicode 类，`$` 也匹配末尾换行前） | 与 1.4.x 端口一致；`ECMAScript` 选项会拒绝部分合法模式 |
| `type: "bytes"` | 恒假（JSON 里没有 `Uint8Array`） | 上游专有类型 |
| `draft: "7"` | 只实现 2020-12 语义 | 端口只编译工具 schema，不跑 JSON-Schema-Test-Suite |

**验证**：`dotnet build Pi.slnx -m:1` 0 警告 0 错误；`Pi.Ai.Tests` 932 例（2 例既有 flaky 单跑全绿）、
`Pi.CodingAgent.Tests` 5118 例 1 失败（既有 typebox 语料问题，见下）、`Pi.Agent.Tests` 36 例、
`Pi.Durable.Tests` 515 例。

### 4.3 P2 已落地：ai wire 层三项（`e6d4c4d` / `c71cd41` / `1ca2e05`）

| 提交 | 内容 | 上游 | 用例 |
| --- | --- | --- | --- |
| `e6d4c4d` | Gemini 工具声明把字符串/数字 `const` 改写成单值 `enum`；只下钻含子 schema 的关键字，名字映射的键不当关键字，`default`/`examples`/`enum` 里的字面量原样保留 | `7314819f0` + `a7b94a03f` | `GoogleConvertToolsTests` 15 例 |
| `c71cd41` | Bedrock strict 工具超限时降级重发一次：两个助手抽到 `ConstrainedSampling`，`ConvertToolConfig` 回报 `HasPreferredStrictTools`，主流程重试循环 + `bedrock_strict_tools_fallback` 诊断 | `842c73170` | `BedrockStrictToolFallbackTests` 17 例 |
| `1ca2e05` | `OAuthHttp.TrustedHttpUrl` 成为唯一实现（Copilot/Kimi/Meta 三处私有副本删除）；`Cli.SaveAuth` 写 `auth.json` 用 0600 并先收紧旧文件；`contentIndex` 的 JSON `null` 也判非法 | `89c921d8b`（ai 侧） | 4 例 + 3 例 |
| `a8db1eb` | 重导 42 个 `ModelData/*.json` + `.manifest.json`：净 +69 / −67 条，总计 1716 → 1718 | `catalog/` 全量 | `ModelCatalogTests` 8 例（三处计数守卫更新） |

**目录数据是怎么重导的**（不移植生成器）：上游把内部数据写在
`packages/ai/src/providers/data/*.json`，但那是生成物、不入库，所以只能现跑。
`tools/catalog-export.mjs`（gitignored）直接 import 上游的 `catalog/build.ts`、
`catalog/providers/index.ts`、`scripts/model-data.ts`，走**同一套**
`buildCatalog` / `groupProviderModelData` / `createModelDataManifest`，把产物写到端口目录：

```bash
node tools/catalog-export.mjs tools/catalog-out
cp tools/catalog-out/*.json tools/catalog-out/.manifest.json src/Pi.Ai/ModelData/
```

字段与分组结构零转换（端口的 `ModelCatalog.Load` 读的就是这个形状）。
**注意**：`catalog/sources/*` 是线上拉取的（models.dev / OpenRouter / Vercel AI Gateway /
NVIDIA NIM），所以产物反映「跑这一下的时刻」，不是某个 tag 的冻结快照；重跑会得到不同的
`generatedAt` 与可能不同的模型集合。

**顺带修掉的端口缺陷**：`PiMessages.Stream` 在终态事件后只 `Push` 不 `End`。TS 的
`EventStream.push` 收到 `done`/`error` 会自己收尾，端口的 `Push` 不会——成功的
pi-messages 请求会让消费方**永远等下去**。此前没有任何 `PiMessages` 测试，所以一直没暴露；
写 `PiMessagesContentIndexTests` 时正控用例直接挂死才发现。现在终态后显式 `End(message)`。

**`Pi.Ai.Tests` 的间歇性失败**：8 个用 `HttpListener` 起本地端点的用例文件在并行下会随机
1–2 例失败（每次成员不同，单跑全绿）。本轮把新增的 Bedrock 用例改成注入
`HttpMessageHandler`（不占端口），但没有根治既有那 8 个文件。**判为既有问题，与本轮改动无关**：
失败成员分布在 `AnthropicOpenRouterCostTests` / `GoogleGenerativeAiTests` /
`OpenAiCompletionsTests` / `MistralStreamTests` / `AnthropicFederationModelsTests`，
本轮一个都没碰。

### 4.4 P3 已落地：coding-agent 窗口修复与新特性（`ed26895`）

**新特性**

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| 扩展包更新 changelog：读包内 `CHANGELOG.md`（或 `package.json` 的 `pi.changelogPath`），下次启动在 "Extension Updates" 下显示新于上次所见版本的内容；`collapseChangelog` 折叠为一行 | `b4b72a351` | `PiManifest.ChangelogPath` + `PathMetadata.ChangelogPath` + `SourceInfo.ChangelogPath`、`SourceInfos.GetSourcePackageKey`、`SettingsManager.Get/SetExtensionChangelogVersion`、`PackageManager.ResolvePackageChangelogPath`、`InteractiveMode.GetExtensionChangelogUpdates` + `InteractiveMode.Bind` 渲染 | `PathComparisonTests`（key 形状）+ 全量回归 |
| `ctx.ui.custom()` 的 `blocked` 选项（仅内联组件，overlay 忽略） | `c1810bf70` | `ExtensionCustomOptions.Blocked` → `PushExtensionDialog(component, options.Blocked)` | 由既有 dialog blocked 用例覆盖 |
| `keyText()` / `keyHint()` 注入渲染上下文 | `9376c8d17` | `ToolRenderContext` / `MessageRenderOptions` / `EntryRenderOptions` 三个 `KeyText`/`KeyHint` 成员，四个构造点显式传宿主实现 | 默认值 + 显式传参经全量回归 |
| device-code 登录：回车打开验证页并复制 code，`openBrowserOnConfirm` / `copyCodeOnConfirm` 可关 | `7151099d0` | `OAuthDeviceCodeInfo.OpenBrowserOnConfirm/CopyCodeOnConfirm` + `LoginDialog` 提示行/一次性消费/状态行 | `LoginDialog` 既有链路由全量回归覆盖 |
| `/model`、`/scoped-models` 名称比 ID 多词时显示名称 | `606af8662` | `ModelSearch.GetDistinctModelName` + 两个选择器行渲染 | `ModelSearchNameTests`（7 例） |

**修复**

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| `trust.json` 里保存的「不信任」优先于自动信任 | `31ade0e0d` | `ProjectTrust.ResolveProjectTrustedAsync` + `Main` 的 `projectTrusted` 推导 | `SessionWindowFixesTests` 2 例 |
| Windows 路径比较忽略大小写（skills 重复加载 / trust 重问 / `--continue` 丢会话） | `1be312b46` | `Paths.GetPathComparisonKey/PathsEqual/IsPathWithin/ShortenHomePath` + `SourceInfos.GetResourcePathKey`；改动覆盖 `TrustManager`、`Skills`、`ResourceLoader`、`PackageManager`、`SessionManager`/`SessionFiles`、`AgentSessionRuntime`、`ExtensionLoader`、`ConfigSelector`、`SessionSelector`、`TreeSelector`、`InteractiveModeFormatting`、`RenderUtils` | `PathComparisonTests`（17 例）+ `SessionWindowFixesTests` 2 例 |
| 会话写盘失败时内存与文件保持一致（不再产生悬空 parent） | `807a5c051` | `SessionManager.AppendEntry` 回滚 `_fileEntries`/`_byId`/`_byIdOrder`/`_leafId` 后重抛 | `SessionWindowFixesTests` 1 例 |
| `/tree` 分支摘要传 thinking level | `269afc66a` | `GenerateBranchSummaryOptions.ThinkingLevel` + `GenerateBranchSummaryAsync` 按压缩同一规则设 `reasoning` | `BranchSummarizationWindowTests` 3 例 |
| 分支摘要输入应用 context edits | `d0fe21308` | `BranchSummarization.GetMessageFromEntry` 走 `SessionEntries.ProjectContextEntry`（该方法随之由 private 改 internal） | `BranchSummarizationWindowTests` 2 例 |
| 对话框打开时替换扩展 editor 不再吞掉对话框 | `7853186d1` | `InteractiveMode.SetCustomEditorComponent` 的三处 `dialogOpen` 守卫 | 全量回归 |
| 工作目录被删除时给提示，不再崩栈 | `88ffcdc73` | `Main.GetStartupCwd`（ENOENT 语义 + Windows 上 `Directory.Exists` 兜底） | 手动核对；无单测（要改进程 cwd） |
| `pi update --extensions` 报「装到的版本与期望不符」 | `0d23d5345` | `PackageManager.Updates` 的 `NpmUpdateCheck` + `ReportUnchangedNpmUpdates` + `PackageManagerCli` 打印 `progress` 事件 | 全量回归 |
| `-e npm:` 无版本时每次启动检查新版并刷新 | `cd6a8f7f3` | `PackageManager` npm 分支的 temporary 刷新（安装 `name@latest`，失败保留缓存） | 全量回归 |
| `pi.exec`：256 MiB 上限 + `truncated`；UTF-8 跨块解码；spawn 错误保留 stderr | `c466a3595` / `36be9c612` / `c60de42e9` | `ExecOptions.MaxBuffer` + `ExecResult.Truncated`、`SpawnOptions.MaxOutputBytes` + `ChildProcessHandle.OutputLimitExceeded`；跨块解码由 `Stream` 读取天然满足；等待失败时 stderr 回落为错误消息 | `ExecWindowTests` 3 例 |

**核对后确认此前已落地**（本批只回查、不重复改动）：`6dfb54678`（装包失败不拦启动，端口以
`ResourceDiagnostic` 呈现）、`bec60c54b`（`-e git:...@branch` 刷新）、`984cf6643`（provider id 含斜杠）、
`fecc19e4e`（grep 上下文行号与孤立 CR 对齐）、`8ddc44ec8`（fork 会话单 fd 写入）。

**不跟**：`8bc94b19f`（`PI_SELF_UPDATE_COMMAND`）——端口的 `update --self` 按其 4g-2 的既有决定
**不执行自更新**（.NET 构建不是 npm 包），没有可供覆盖的执行路径；`5f960a0e2`/`5fb9a0b11` 是 npm 专有；
`27cc6bfc0` 是 macOS 进程组语义。

**当时仍留在 P3 名下**（后来 `2ac34ad` 全部做完，见 §4.6）：`e3da248ea`（Windows `git clean` 失败后继续装依赖）、
`6454d07ec`（WSL 深目录互操作 + 外部编辑器失败报状态）、`8c03fef4d` 的会话搜索分层
（`/resume` 名称/目录模糊、ID 与正文只做子串）、`1c720d285`/`9359adf0c`（HTML 导出深会话与 system prompt/tools）。

**验证**：`dotnet build Pi.slnx -m:1` 0 警告 0 错误；`Pi.CodingAgent.Tests` **5157** / 1 失败 / 3 跳过
（`--filter-not-class "*PackageManagerTests*"`；唯一失败仍是既有的 `UpstreamTypeScriptExampleTests`
typebox 语料问题），较基线 5118 增 39 例。

## 5. 不跟的项与理由
- **`v1.5.0..HEAD` 的 49 个提交**：按 1.4.0 期定的策略「上游未发布提交先不跟」，等下一个 tag。唯一例外是修真实计费/安全错误的提交，按需单独评估。
- **Chord v2**（`packages/chord/src/next`，~37k 行）：实验性，未进 `exports`。
- **`sandbox/` 原生扩展示例**：上游已删除，端口从未移植。
- **Node/npm 专有**：`undici`、`min-release-age`、`npm audit/fund`、pnpm 归属判定、`package-lock` 嵌套、`uv_cwd`。
- **`packages/schema` 的 builders/annotations/typebox-static**：TS 侧的 schema 构造 API，端口用明文 JSON Schema。

## 6. 复核方法（下一批动工前）

1. `git -C D:\AI\参考项目\pi log -1` 确认检出；若已越过 `f9a263655`，用 `git log --oneline v1.5.0..HEAD` 重新评估。
2. 表驱动常量逐项 diff：`Retry.cs` 模式表、`Overflow.cs` 模式表、`ModelCatalog` 的 provider/model 表、`keys`/`keybindings` 表。**不能只看漂移窗口**。
3. 每批收口：`dotnet build Pi.slnx -m:1` 0 警告 0 错误 → 定向测试 → 全量测试 → 提交 → 更新本台账与 `coding-agent-porting-status.md`。

### 4.5 P5 首批已落地：env 启动错误 + mcp login 作用域（`4afb050`）

| 变更 | 上游 | 落点 | 处置 |
| --- | --- | --- | --- |
| 启动错误带 stderr 尾部（20 行 / 2000 字符，去终端转义与控制字符） | `479d6e5e2` | `Pi.Env/Connection.cs` 的 `StartupErrorText` + `Session.StderrTail/Ready/StderrEnded` | ✅ 本批 |
| `pi mcp login/logout -l`、项目作用域校验、服务器名拼写建议 | `be15fe3c1` | `Extensions/Mcp/Cli.cs`（`local` flag、`EditDistance`、未知选项报原文） | ✅ 本批 |
| watch 变更列表超帧上限报 overflow | `102d12871` | `daemon/src/watch.rs`（Rust） | ➖ 守护进程按原生产物使用 |
| 守护进程被终止时杀掉它启动的命令 | `b6d5c2ecd` | `daemon/src/main.rs` + `sys/*.rs`（Rust） | ➖ 同上 |
| 辅助线程起不来时 `exec`/`pty` 返回 `spawn_error` | `92c1da1f6` | `daemon/src/{exec,pty}.rs`（Rust） | ➖ 同上 |
| MCP OAuth 优先用 Client ID Metadata Document，否则动态注册（`oauth.clientRegistration: auto/dcr`） | `908aa7b74` | `extensions/mcp/oauth.ts` ~59 行 + `core/mcp-servers.ts` | ✅ 下一批（§4.6） |
| 工具搜索：名称权重高于描述、命中词数优先、查询即名称时置顶、CJK 分词；durable 侧同规则 | `cc44fa216` / `e033f255f` / `ae823bc0b` / `79006f061` / `c7372af42` | `extensions/tool-search/tool.ts` + `durable/src/search.ts` | ✅ 已在（§4.24 核对） |

> env 三项的判定依据：`docs/env-package-migration.md` 已明确「守护进程（`daemon/src/*.rs`）不移植，
> 按原生产物处理」。这三条是守护进程内部行为，端口侧没有对应代码路径；升级原生 `pi-env` 即得修复。

### 4.6 P3 收尾 + P5 收尾（`2ac34ad`）

P3 余下的四项全部落地：

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| Windows 上 `git clean` 失败仍继续装依赖，保留 marker 下次重试，并给出 progress 提示 | `e3da248ea` | `PackageManager.Updates.CleanAndInstallGitDependenciesAsync` | 全量回归 |
| 剪贴板命令与浏览器启动器从盘根启动（WSL 深目录超 `MAX_PATH`）；外部编辑器失败带消息并显示 | `6454d07ec` | `Utils/ClipboardCommand`、`Utils/OpenBrowser`、`ExternalEditorResult.Failed(Message)`、`InteractiveMode.HandleOpenExternalEditor`、`ExtensionEditor` 状态行 | 全量回归 |
| `/resume` 搜索分层：名称 / ID·正文·目录 / 模糊三档，消息文本按会话缓存小写副本 | `8c03fef4d` | `SessionSelectorSearch`（`MatchLiteralToken` / `MatchFuzzyToken` / `MatchPhraseToken` / `MatchRegex` + `ConditionalWeakTable` 缓存） | `SessionSelectorSearchTests` 51 例（含新增分层用例） |
| HTML 导出：模板升到 1.5.0（迭代式树排序、系统消息样式），payload 带 `systemPrompt` 与 `tools` | `1c720d285` / `9359adf0c` | `Assets/export-html/{template.js,template.css}`、`ExportHtml.GenerateHtml`、`AgentSessionTree`、`InteractiveMode.Commands` | `ExportHtmlBugReportTests` 新增 2 例 |

P5 余下的两项：

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| 工具搜索：名称/命名空间/描述三段加权、命中词数优先、全名加分、查询即名称置顶、Unicode 分词与 CJK | `cc44fa216` / `e033f255f` / `ae823bc0b` / `79006f061` / `c7372af42` | `Extensions/ToolSearch/ToolSearch.cs`（`NameWeight`/`NamespaceWeight`/`IndexDocument`/`Rank`、Unicode `Tokenize`、`ToolSearchDocument.NamespaceText`） | `ToolSearchCorpusTests` 73 例（语料重新生成）+ `ToolSearchRankingTests` 7 例 |
| MCP OAuth `clientRegistration` 默认 `auto`：支持 CIMD 就用文档，否则动态注册；显式 `cimd` 才报错 | `908aa7b74` | `McpClientRegistration.Auto`、`McpOAuth.OffersClientMetadataDocument`、`ClientMetadataDocumentFor` 改可空、`McpServers.IsClientMetadataDocumentCallback` | `McpClientRegistrationTests` 10 例 |

**工具搜索语料的重生成**：差分语料来自上游 TS，`tools/gen-coding-agent-tool-search-corpus.mjs`
（该目录按仓库惯例不入库）把 `tool.ts` 的排名段原样取出、在 Node 类型擦除下运行并重算
`stem`/`tokenize`/`documents`/`rank`/`rankCustom` 的期望值。退化参数（`k1=0,b=0`）会让 BM25 算出
`NaN`，`JSON.stringify` 写成 `null`；测试按 `double.IsNaN` 比较，保留上游「不丢弃该文档」的行为。

**durable 侧无独立实现**：端口没有 `durable/src/search.ts` 的对应物——codemode 的 `searchTools()`
直接复用 coding-agent 的文档构造与排名器，因此 `c7372af42`（durable 与 coding-agent 对齐）由共用实现覆盖。

**判定不适用**：`8bc94b19f`（`PI_SELF_UPDATE_COMMAND`，端口无自更新执行路径）与本批三处 Rust 守护进程改动。

### 4.7 P7 首批已落地：watch 家族与工具输出 ANSI 清理（`a0da1ea`）

**watch 家族**（`a2fd25f56` + `5e106ab1c` + `42cb822f1` + `d8d99950a` 的 chord 侧）。上游把
`packages/durable/src/harness/observation.ts` 的 `CommittedWatch` 删掉，改成 chord 里一套共享实现；
端口的对应物是 `src/Pi.Chord/Services/Watch.cs`：

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| 监听器收「一次调用」而不是「值 + 增量」：`WatchCall<T>`（`Value(value, ops, retired)` / `Failure(error)`）、`NativeWatchCall<T>` | `8b0caefc2` | `Watch.cs` 的 `WatchCall<T>` / `NativeWatchCall<T>` | `WatchTests` 16 例 |
| `WatchEnd` 端点收敛：`retired` 取消（改为调用的 `retired` 标记）、`cancelled` 更名 `aborted` | `8b0caefc2` | `Watch.cs` 的 `WatchEnd` | `SessionTests` 退休/取消 3 例改写 |
| `WatchLifecycle<T,TCall>` 共享生命周期：串行投递、`endAfterDelivery()`、终止即脱钩并丢弃指向源的引用 | `42cb822f1` | `Watch.cs` 的 `WatchLifecycle` | `WatchTests.EndAfterDelivery*` / `FramesAfterTerminationAreIgnored` |
| `Watchable<T>` / `NativeWatchable<T>` 公共接口面 | `a2fd25f56` / `5e106ab1c` | `Watch.cs` 的 `IWatchable<T>` / `INativeWatchable<T>` | 编译期契约 |
| `Watch<T>`：待发帧折叠成一次调用，单帧原样透传，失败后的首个值是根替换 | `d8d99950a` | `Watch.cs` 的 `Watch<T>` | `WatchTests.ATightSequenceOfFramesArrivesAsOneCallWithTheFoldedOps` 等 |
| `NativeWatch<T>`：每个值各自一帧，超 100 个待发值折叠成一个（`replace` 缺省取最新值） | `d8d99950a` | `Watch.cs` 的 `NativeWatch<T>` | `WatchTests.NativeWatch*` 3 例 |
| `PendingOps`：慢消费者的待发 op 折叠队列（同路径叶子 op 合并、`r` 丢弃此前、超 10,000 只留替换） | `d8d99950a` | `Pi.Chord/Delta/PendingOps.cs` | `PendingOpsTests` 13 例（含 Tracker 随机游走的不变量校验） |
| 事件批次改走原生 watch（每批各自一次调用，超 100 批折叠成最新快照），状态类 watch 才折叠 ops | `d8d99950a` | `Harness/Events.cs` | `HarnessEventsTests` 全绿 |
| durable 侧 `CommittedWatch<T>` 变成 `Watch<T>` 的薄适配（值 + ops + 上下文的动词、`Retire` 投递退休帧后 `endAfterDelivery`） | `42cb822f1` / `8b0caefc2` | `Pi.Durable/Session/Observation.cs` | `SessionTests` / `HarnessViewTests` / `HarnessTaskGraphTests` |

**两个端口专有的取舍**（都写进了代码注释）：

1. **投递时机**：TS 用 `queueMicrotask`，端口用 `Task.Run`。因此「一个忙碌周期一次调用」在紧邻提交的
   循环里等价于「一次调用覆盖这些提交」，而不是 TS 那样恰好一提交一帧。受影响的三个用例改成逐次冲刷
   （`HarnessViewTests` 的 head 标记用例、`SessionTests` 的非退休帧用例）或改成断言折叠语义
   （`HarnessViewTests.FoldsUndeliveredCommitsIntoOneCallWhoseOpsReachTheNewestView`）。
2. **`Watch<T>` 多一个可选 `replace` 参数**：TS 溢出时用最新待发帧的值做根替换，端口允许源给出「当前值」，
   与 `NativeWatch` 的 `replace()` 对齐；缺省行为与 TS 一致。

**同批落地**：`50d9f823e` 的整段 ANSI 序列清理落在 `Pi.Durable/Harness/ToolOutput.SanitizeOutput`
（上游把该文件从 durable 搬到了 chord，端口按 1.4.0 的决定保持分开放），先删 7 位转义序列
（CSI / OSC·DCS·APC·PM·SOS 到 BEL 或 ST / `ESC x` 短形式）再删单个控制字符。

**判定不适用**：`be039fb31`（端口没有 v1 的 `method()`/`remote()`/`local()` 声明层，也就没有 TypeBox
可删）、`c09c98d7d`（chord v2 的 route/relay 层未移植）、`db14038bd` 与 `8025c414d`（端口无 chord
SQLite 门面：`SqliteCommandExecutor` 每条 SQL 现建命令、不缓存预编译语句，且没有消费影响行数的调用方）、
`d0aa218cb`（JS 的属性逐键拷贝与 V8 子串驻留都是引擎专有；端口的 `DeltaApply` 已按路径拷贝容器）。

**仍归 P6**：`8b0caefc2` 的「采纳时发布提交」（`Storage.commit(writes, adopt, context)`）、「按地址跟随的
watch」（退休后同一地址的新化身接续同一 watch）、以及 `observeDoc`/`observeConversation`/`observeTaskGraph`
三个同步 observer——它们和分组提交、节流落盘是同一处 Session 线路改动，拆开做会两次改动同一批方法。
`da412b039` 的 `appendText` 在端口已是既有能力（`Tracker.Change.Append` 直接记 `a` 而不比对整串）。

### 4.8 P6 首批已落地：思考档位落盘 + `whenIdle`（`ae97b71`）

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| assistant 消息记下**请求**的思考档位（`providerThinkingLevel` 另记实际档位）；轮询检查点也带这一项，旧记录缺字段时视为 null | `d5b3de22d` | `Harness/Generation.cs`（`RequestInfo.ThinkingLevel`、`GenerationCheckpoint.Poll.ThinkingLevel`、`ClassifyAsync` 里的 `message with { ThinkingLevel = … }`） | `HarnessGenerationTests.RecordsTheRequestedThinkingLevelOnTheAnswer` |
| `whenIdle: "reject"`：空闲对话在任何写之前以新的 `ConversationIdle` 拒绝输入，因此它不会启动一次运行 | `6a2825113` | `DurableErrors.ConversationIdle`、`SubmissionDraft.Input.WhenIdle`、`Submissions.AdmitAsync` | `HarnessSubmissionsTests.RejectsIdleInputWithWhenIdleRejectBeforeAnyWrite` |

**判定不适用**：`044148392`（durable 的 Code Mode 脚本通道按 1.4.0 的产品决策不采纳——端口没有
`tools.read()` 的脚本桥，`structuredOutput`/`structuredOutputSchema` 一整套也就没有落点）。

**观察项**：`d5b3de22d` 的第二半（轮询检查点）在端口的端到端断言要等 faux provider 的 deferred 流；
目前只覆盖写盘侧（`ClassifyAsync` 与 `Poll` 检查点都带了字段），恢复侧留待补齐 faux deferred 流后加。

### 4.9 P6 第三批：只读 Harness、`beforeProviderRequest`、条目读取（`d57852a` + `332da47`）

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| 只读打开：`running` 任务保持已提交的样子，带写的提交与所有会启用调度的调用以 `ReadOnlySession` 拒绝，`Wake()` 恒为非活跃 | `be14d9732` | `DurableErrors.ReadOnlySession`、`HarnessOptions.ReadOnly`、`DurableSession._readOnly`（提交守卫）、`TaskScheduler`（open/resume/wake/reconcile 守卫） | `ReadOnlyHarnessTests` 4 例 |
| `beforeProviderRequest`：pi-ai 拼好请求体之后、发送之前跑替换链，返回值替换该次请求的体 | `d280e1ffa` | `IGenerationHooks.BeforeProviderRequest`、`Generation.RequestPhaseAsync` 里的 `onPayload` 接线与 `ProviderPayloadAsync` | `HarnessGenerationTests` 2 例 |
| 钩子与任务读原始条目历史：`entry()` / `entries()`，按条目 ID 界定的区间在压缩前后与重跑之间一致 | `65fba65b2` | `EntryRange`、`HarnessUtil.ConversationEntries`、`IHookApi` / `ITaskRuntime` 的同名成员、`Conversation.EntriesAsync` 复用 helper | `HarnessHookReadsTests` 2 例 |

**两处端口判断**：

1. **`d280e1ffa` 与端口原有 `BeforeRequest` 不是同一个钩子**。原有的 `BeforeRequest` 在**消息层**
   （替换要发给模型的 `IReadOnlyList<ChatMessage>`）；新的是**请求体层**（pi-ai 拼好 provider 报文之后）。
   两者并存，顺序是消息层先、请求体层后，与上游一致。
2. **条目区间用独立的 `EntryRange`**，而不是把 `EntryQuery` 的 `ConversationId` 设成可选。TS 用
   `Omit<EntryQuery, "conversationId">` 表达「对话已由调用方确定」，C# 没有 `Omit`，用一个不含对话的
   record 更直白：调用方不可能选错对话，`HarnessUtil.ConversationEntries` 负责补上任务自己的对话。
   顺带修掉 `Conversation.EntriesAsync` 的一处小缺陷：旧的内联收敛漏了 `Order`，调用方传升序会被丢掉。

### 4.10 P6 第四批：每会话工具轮次上限（`a8b05cb`）

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| `AgentState.maxTurns` 只数工具轮；达到上限的运行像失败一样结束（`afterFailedRun` 决定排队项，continue 在轮结果上续跑），且不再请求模型 | `f6a9db1d8` | `AgentState`/`AgentChange`/`Agent` 三处字段 + `AgentDocs.ApplyChange` 的 ≥1 校验、`Live` 的 `run.turns`（`SetRun`/`RunTurns`/`SetRunTurns`）、`Generation`（工具轮自增、`finishToolRound` 的 `turn_limit` 分支、`AnswerAsync` 的 `TakesContinuation`） | `HarnessTurnLimitTests` 3 例 |

**顺带对齐的一处语义**：`AnswerAsync` 的 `onYield` 续延判定前置到 final 边界放置任何东西之前——
只看队列里有没有会被选中的 steer/follow-up、有没有会先跑的重置（`Inbox.QueuedItems` +
`Inbox.HasQueuedReset`，后者从 `ApplyBoundaryAsync` 的内联判定提取）。旧写法在 `ApplyBoundaryAsync`
之后才判断，判断依据是「放置结果」，两者在重置与用户项同时排队时的先后语义不同。

**依赖缺口**：上游该用例还断言 `live.runFailure` 的持久记录（`reason: "turn_limit"`、`inputs`），
而端口的 `runFailure` 属于 `f3255de76`（窗口化会话视图 + `ConversationTranscript`）那一批，
尚未落地。本批只验证「运行已结束、提交以 `turn_limit` 结算、不再请求模型」，`runFailure` 的断言
随那批补。

### 4.11 P6 第五批：`pi.live.runFailure`（`15d315f`）

`f3255de76` 是三件事，端口先只做**公共前置**那一件：运行结束后的失败记录。窗口化视图与
`ConversationTranscript` 是读取面，不改变写入语义，可以后补。

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| `pi.live.runFailure` = `{ taskId, inputs, reason, detail? }`，写在结束运行的那次提交里；下一次运行开始时清掉，但「结束失败的同一次提交里开始的运行」保留它 | `f3255de76` | `Live.SetRunFailure` / `DeleteRunFailure`（`EndRun` 在未应答结算时写）、`Generation.StartRunAsync(…, keepFailure)` | `HarnessTurnLimitTests` 4 例（含 reason/detail/inputs/taskId 与「下一次运行清掉」） |

**为什么先做这一件**：`f6a9db1d8`（轮次上限）的上游用例断言 `live.runFailure`；`fb524dd2e`（continue）的
`nothing_to_answer` 同样走这条通道；`7e15f9601`（afterRun）也要读它判断运行怎么结束。三处都卡在这一块上。

**仍未做**（同一上游提交的其余部分）：`ConversationView.entries` 的窗口（`before` / `from` / `min` / `max`）、
`ConversationTranscript`（`gaps` / `loadEarlier` / `fillGap` / `resume`）、删除 `watchEvents` 及其事件类型。
端口目前仍保留 `Events.WatchEventsAsync`，删它与 P6 的窗口化视图一起做，免得两次改同一批视图代码。

### 4.12 P6 第六批：continue 提交（`79bfdae`）

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| `{ type: "continue" }`：不追加用户消息、直接在现有上下文上起一次运行；不落条目、从不排队；忙碌时 `ConversationBusy`；队列里留着的项先放置并一起并入 | `fb524dd2e` | `SubmissionDraft.Continue` / `SubmissionCreate.ContinueCreate` / `SubmissionRecord.ContinueRecord`、`Transaction`（IsSettled/StatusOf/WithStatus/Create 四处）、`Admission`（含 continue 专用分支与 `CreateFor`）、`MemoryStorage` / `SqliteStorage` / `SqliteStorageJson` | `HarnessContinueTests` 2 例 |
| 上下文最后一条（忽略系统消息）不是用户消息或工具结果时，不发请求就结束运行，结算 `nothing_to_answer` | `fb524dd2e` | `Generation.FailNothingToAnswerAsync` + `PreparePhaseAsync` 的判定 | 同上第 1 例（断言 provider 只被调用一次、`runFailure.reason = nothing_to_answer`） |

**为什么这批值得单列**：`continue` 是新的**提交记录种类**，它横向穿过类型层、事务结算、受理、两个存储
后端与 JSON 编解码共十处。漏任何一处都会出现「某个后端读不回记录」的半成品，所以这批一次改全，
`Pi.Durable.Tests` 全量（532 例）覆盖了 Memory 的存放与 Sqlite 的 JSON 往返转换器。

**仍未做**（同一上游提交）：`ConversationHandle.submit()` 放开为任意 `SubmissionDraft`（端口目前仍只收
输入草稿——子代理对话用 continue 属可用性改进，不影响内核语义）；`afterFailedRun` 的文档里补
`nothing_to_answer` 这一失败原因（端口文档已按同一语义描述）。

### 4.13 P6 第七批：afterTool 覆盖全部结算路径（提交见 git log）

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| call 阶段的拒绝（tool_unavailable / 参数无效 / beforeTool 拦截）先过 afterTool 再结算；被打断与中止的槽结果同样过钩子并按工具上限有界化 | `87eff524f` | `ToolTask.RejectAsync` / `SettleFromSlotAsync` / `AfterHarnessResultAsync` / `LimitsOf`，`FromSlot` 改为单独返回截断诊断 | `ToolTaskTests` 16 例 |

**端口差异（已补）**：上游在 abort 时按 `restarts < restartLimit - 1` 豁免钩子（防「钩子就是打断元凶」的
死循环）；端口此前钩子总是跑，本轮补上这条豁免。

### 4.14 P6 第八批：未提供的工具调用按工具任务跑（提交见 git log）

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| 未提供的调用进轮：并行轮直接起任务、顺序轮按序进 pending，槽标 `unoffered`；由工具任务结算 `tool_unavailable`（过 afterTool），abort 时同因 | `1c6928b30` | `ToolTaskInput.Unoffered` / `ToolSlot.Unoffered`（JSON 往返兼容旧形状）、`CreateToolTaskAsync`、`StartToolRoundAsync`、`ToolsPhaseAsync`、`ToolTask.CallPhaseAsync` / `AbortAsync`；`pi.tool` 1→3、`pi.generation` 1→2 + 透传 Migrate | `HarnessUnofferedToolTests` 1 例 |

**顺带修正的一处行为差**：旧端口在轮启动提交里直接给未提供的调用落结果，这既绕过 afterTool，也让 abort
把「无论如何都不可能跑过的调用」说成 aborted。上游特意让它们进任务——两条路径（结果、abort）都说
`tool_unavailable`。

### 4.15 P6 第九批：空闲检查按脏集合走（提交见 git log）

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| 提交只重估「其下有任务变为存活或结束」的对话；链未知与链加载回退全量；过期定时器仍全量 | `35dfa7573` | `Scheduler.Observe` 的 dirty 收集 + `AddScopesAbove`、`SettleContextCache(dirty)`、`LoadChainAsync` 置 `_unknownsChanged`、`Harness.IdleFullChecks` 诊断 | `HarnessIdleScopesTests` 1 例 |

**两处有意保留**（与上游不同，端口热点不在这两处）：上游用 `idleOrder` 有序索引找下一次过期，
端口的 `ScheduleContextExpiryLocked` 仍对保留上下文取 min——只是字典迭代，没有逐对话的子树行走；
上游 `wake()` 从 `_sleeping` 集合取最早睡眠，端口 `ComputeEarliestSleep` 仍扫存活任务，而端口的
`ComputeActive` 已在「有调用在跑」时提前返回，等价于上游「只在无调用时扫描」的约束。

### 4.16 P6 第十批：工具交接后运行继续（提交见 git log）

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| handoff 的重置条目总是先于排队项写入；只有 terminate 才结束运行，否则在 handoff 的新上下文里继续（turn_limit / 排队重置 / 交接给下一个生成的分支保持原序） | `38ff99177` | `Generation.FinishToolRoundAsync`（terminate 与 handoff 解耦） | `HarnessHandoffTests` 1 例 + `HarnessInboxTests` 2 例改写 |

**连锁的语义变化**（上游同批改写测试）：排队的 follow-up 不再随 handoff 落条目——它留在队列里，由
继续运行的那个回答在 final 边界放置并接着跑；`pi.reset` 条目按调用序只写最后一个 handoff。

### 4.17 P6 第十一批：afterRun 与运行结束任务（提交见 git log）

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| 运行回答后由 `afterRun` 钩子要持久任务；生成以 `finish` / `drain` 两阶段创建并等待，期间 `pi.live.run` 仍被持有（对话忙碌、排队输入等待、崩溃恢复后重问钩子） | `7e15f9601` | `IRunEndHooks.AfterRun` + `RunSummary` / `RunEndTaskRequest`、`GenerationCheckpoint.Finish` / `Drain`、`HoldRun` / `PrepareRunEndTasks` / `FinishPhaseAsync` / `DrainPhaseAsync` / `EndAnsweredRunAsync`、`Live.SetRunAfterRun` / `RunInputs` / `ClearRunInputs` | `HarnessAfterRunTests` 2 例 |

**端口的两处取舍**：

1. **`afterRun` 用独立接口 `IRunEndHooks` 表达**。TS 里 `afterRun` 是钩子对象上的可选属性（未声明即不问），
   C# 的接口成员无法「未声明」，若放进 `IGenerationHooks` 则每个生成钩子都会被当成声明了 afterRun——每次
   回答都会多一次 hold + finish 提交。独立接口让「声明」保持可选。
2. **`run.first` 已补**（本轮）：`BoundaryResult.First` 把「边界放置的第一个条目」交给它起的运行，
   没有边界条目可依的运行以第一个工具轮答案补；`RunSummary.First` 因此是真正的第一个条目。随后 §4.19 的
   重启收场已经把 `finish` / `drain` 也纳进上限检查；仍未做的是 `runFailure.first`（失败运行的历史起点，
   供 continue 的钩子用）。

### 4.18 自查记录（全仓验证与遗漏核对）

**验证**（本轮实测）：`dotnet build Pi.slnx -m:1` 0 警告 0 错误；`Pi.Durable.Tests` **538**、`Pi.Chord.Tests`
**135**、`Pi.Codemode.Tests` **57**、`Pi.Agent.Tests` **36** 全绿；`Pi.Env.Tests` 53（3 跳过，Windows 上无意义
的 Unix 权限位）；`Pi.CodingAgent.Tests` **5184 / 1 / 3**——那 1 例仍是既有的 `UpstreamTypeScriptExampleTests`
typebox 语料问题，与 1.5.0 追平无关。

**修掉的一处偏差**：`afterRun` 请求的任务在校验与创建时各跑一次 `initial()`；上游保证只跑一次（把校验出的
checkpoint 固定在定义里）。已改为固定 checkpoint 创建。

**已核对落点**（文档标 ✅ 的项逐符号确认）：`ConversationIdle`、`ReadOnlySession`、`BeforeProviderRequest`、
`EntriesAsync`（钩子/任务）、`MaxTurns`、`SetRunFailure`、`ContinueRecord`、`Unoffered`、`AddScopesAbove`、
`IRunEndHooks`、`FinishPhaseAsync`、`TakesContinuation` 均在预期文件；`src/Pi.Durable` 无 TODO/FIXME 残留。

**已知未做**（截至 §4.27，其余都已落地）：`e5c6182ba`（分组提交 + 批量 `Storage.commit`）、`6c2fd73b0`
（节流提交延迟落盘）、`da412b039` 的工具调用参数部分（增量 JSON 解析器没移植，见 §4.27）；
P4（tui）余 11 项（等语料生成链，已做两项不改字节的见 §4.26）；P2 的六行「目录数据」（需先移植
`generate-models` 脚本）。

**已知测试噪声**：全量并行满载时，watch/reopen 与计时类用例（`SessionTests.Watch*`、`HarnessViewTests`、
`HarnessSleepTests`、`HarnessTaskGraphTests`、`ToolsTests.Bash_*`）每轮随机 1–2 例超时，单跑全绿。
判断回归要看「失败集合是否变化」，不能只看一跑。

### 4.19 P6 第十二批：重启循环可见地结束（`b3a29f111`）

上游这个提交把「停放」整套拿掉，改为「到限就结束，并且结束得看得见」。端口按同样语义重写：

| 变更 | 上游落点 | 端口落点 |
| --- | --- | --- |
| 任务被切断 `restartLimit` 次后不再停放 | `scheduler.ts`：删 `#parked*` / `#unparkRaised` / `retry()`，`BlockedReason` 去掉 `restart_limit` | `Scheduler.cs`：删 `Parked` / `UnparkRaised` / `RetryAsync` / `_parkedTasks`，`BlockedReasonOf` 同步；`TaskSchedulerOptions.LimitsRestarts`（= `Live.LimitsRestarts.Contains`） |
| 不由自己收场的种类到限 faulted | 保留遍里 `cutOff && (mode === "abort" \|\| !limitsRestarts(kind))` → `#terminate(faulted, {reason:"restart_limit"})` | `ReserveAsync` 同结构；`ReportInfo.TaskFaulted(..., ReportReasons.RestartLimit, extension)` |
| 内建任务自己收场并报 `task_interrupted` | 任务提交包装里识别「终态 failed + detail.reason ∈ {restart_limit, interrupted}」→ 提交落地后上报 | `RuntimeImpl.CommitAsync` + `InterruptionOf`；`ReportInfo.TaskInterrupted(task, reason, restarts, extension)` |
| 工具调用以错误结果收场 | `tool.ts endCutOff`（code `restart_limit`，不过 afterTool） | `ToolTask.EndCutOffAsync`；`Ending.Reason` 写进 failed 结局的 detail |
| 压缩以 `compactionFailure` 收场 | `compaction.ts failCutOff` + `failCompactionStatus` | `Compaction.FailCutOffAsync`；本批同时补上端口一直缺的 `Live.compactionFailure`（`FailCompactionStatus` / `DeleteCompactionFailure`，摘要产出时清） |
| 请求/轮询途中被切断 = 一次失败尝试 | `generation.ts interrupted()`：partial 转 aborted、按 `settings.retry` 退避、轮询把 handle 塞进 `retry.poll` 再轮询同一个 | `Generation.InterruptedAsync` + `GenerationCheckpoint.RetryPhase.Polling`（`PollRequest`）；`RetryPhaseAsync` 重建成 poll 或 prepare |
| 请求之外到限才 `restart_limit` | `generation.ts endCutOff` + `salvageable()` | `Generation.EndCutOffAsync`（prepare/request/retry/poll/tools 五个阶段各一处检查） |
| 延迟应答的取消集中 | `deferredOf()` / `cancelDeferred()` | `Generation.DeferredOf` / `CancelDeferredAsync`（abort 与收场共用） |
| 被切断的运行以 `aborted` 收场 | `settleSchedulerOutcome`：cutOff 时不转换 partial、运行结算 `aborted` | `LiveSettle.SettleSchedulerOutcomeAsync` + `CutOffForRestartLimit` |

**端口的取舍**：

1. **`RetryPhase.Polling` 而不是 `Poll`**：`GenerationCheckpoint` 里已有嵌套记录 `Poll`（poll 阶段的检查点），
   位置参数同名会被编译器当成类型引用；JSON 键仍是 `poll`，形状与上游一致。
2. **`Live.LimitsRestarts` 用固定集合**（`pi.generation` / `pi.tool` / `pi.compaction`），由 `Harness` 注入
   调度器；上游同样是内建三类。
3. **`compactionFailure` 的 `trigger`** 用 `CompactionReason` 的小写名（`manual` / `threshold` / `overflow`），
   与端口已有的 `pi.live.compactions[].reason` 写法一致。

**用例**：新增 `HarnessRestartInterruptedTests` 5 例（记一次尝试后下一轮作答；无重试可用时 `interrupted`；
请求之外到限 `restart_limit`；工具错误结果 + 其上报告；压缩 `compactionFailure`）。`HarnessRestartTests`
改为新语义（扩展种类 faulted、上限上调仍在保留前生效、到限 faulted、上限之下仍跑 abort 处理器），
`HarnessReportTests` 的 `task_parked` 用例换成 `task_faulted` + `restart_limit`。

**调试踩到的两个坑**（写进记忆日志）：崩溃模拟必须逐次 `storage.Reopen()`（否则一直克隆最初那份，重启计数
停在 1）；压缩要能出摘要得有两轮且 `keepRecentTokens` 足够小（一轮时切点是 0，等于没得压）。

### 4.20 自查记录（P6 第十二批之后）

**构建与测试**：`dotnet build Pi.slnx` 0 警告 0 错误；`Pi.Durable.Tests` **543** 例，本批相关的 39 例全绿。
全量并行仍有个位数旋转噪声（`HarnessViewTests`、`HarnessTaskGraphTests`、`HarnessEventsTests`、
`HarnessGenerationRecoveryTests`、`HarnessSleepTests` 里的 reopen/计时/批投递类），逐例单跑通过；判断回归
仍然只能看失败集合是否变化。

**逐项核对**（b3a29f111 的落点，防漏）：

| 检查 | 结论 |
| --- | --- |
| 调度器不再停放、`task_parked` 不再上报、`restart_limit` 不再是阻塞原因 | `Scheduler.cs` 无 `Parked` / `RetryAsync` / `_parkedTasks`；`BlockedReasonOf` 与 `TaskGraphNode.Blocked` 的注释同步 |
| 到限由调度器 faulted（扩展种类 / abort 处理器） | `ReserveAsync` 的 `cutOff && (mode == "abort" \|\| !_limitsRestarts(kind))`；`ReportInfo.TaskFaulted(..., ReportReasons.RestartLimit, extension)` |
| 设置只在被切断过的任务上读取 | `record.Restarts is { } && >= RestartLimit()` 短路；`RestartLimit()` 仍只在该条件成立时调用 |
| 内建任务自己收场并上报 | 工具 / 压缩 / 生成三处的收场都写 `detail.reason`，`RuntimeImpl.CommitAsync` 统一报 `task_interrupted`（只在内建任务经运行时提交时） |
| 生成请求/轮询被切断 = 一次失败尝试 | `request` / `poll` 在时间判断之前看 `Restarts`；`retry.poll` 保留 handle 再轮询；无重试可用才 `interrupted` |
| 请求之外的到限 | `prepare` / `retry` / `tools` 各一处 `EndCutOffAsync`；`Salvageable` 决定是否再做转换与取消延迟应答 |
| 运行被切断时以 `aborted` 结算 | `LiveSettle` 的 `CutOffForRestartLimit` 分支：不转换 partial、结算 `aborted` |
| 工具结果不跑 `afterTool` | `ToolTask.EndCutOffAsync` 直接走 `SettleAsync`，与上游 `endCutOff` 一致 |
| `runFailure.first` | `Live.SetRunFailure(..., first: RunFirst(live))`；`RunFailureFirst` 供 continue 读，且在 `StartRunAsync` 清掉前读 |
| `no_model` 的 detail | `FailNoModelAsync` 传 `message`；恢复测试断言「is not available」 |
| 失败原因集合 | `RunFailureReasons` 十条与上游联合类型对齐（含 `nothing_to_answer` / `turn_limit`） |
| 旧引用 | `src` 与两份 readme 无 `retryTask` / `task_parked` 残留；`migration-execution-status.md` 的 durable 行已改 |

**发现并修掉的一处隐患**：`HarnessRestartTests` 里「同一 checkpoint 再提交不算进展」的用例原先断言
`TaskInspection.Blocked("restart_limit")`，改语义后必然失败——已改为断言到限 faulted（这正是上游的意图）。

**当时已知未做**（后续批次已补齐）：`f3255de76` 的窗口化视图 + `ConversationTranscript` + 删 `watchEvents`
（§4.21/§4.22）、`35dfa7573` 的 `idleOrder` 索引与 `wake()` 睡眠集合（§4.23）、`e5c6182ba` / `6c2fd73b0`
分组提交与节流落盘（**仍未做**，工具输出在崩溃前是否落盘取决于它，所以本批工具用例只断言诊断码而不部分输出）。

### 4.21 P6 第十三批：窗口化视图与 `ConversationTranscript`（`f3255de76` 前半）

上游把「一份共享的完整视图」换成「每个观察者各自的窗口」，并在其上提供客户端转录。端口按同一模型重写：

| 变更 | 上游落点 | 端口落点 |
| --- | --- | --- |
| `entries` 是完整历史（分叉感知）上的窗口，带 `before` | `ConversationView.entries/before` | `ConversationView.Before`；`ConversationView.Entries` 改为完整历史（head 标记只是按序追加的条目，不再裁历史） |
| 每个观察者一个窗口 | `Windowed` / `Mount.observers` | `ConversationViews.Windowed`（min / max / 自己的值）；`Mount` 只留对话、文档与化身 |
| `watch(ctx, { from, min, max })` | `watch()` 的第三参 | `IConversation.WatchAsync(context, ConversationWindow?)`；`ConversationViews.WatchAsync/AttachAsync` 同参 |
| 初始窗口用有界扫描 | `loadWindow()` / `scanUpTo()` | `LoadWindowAsync`（`from` 装得下就从它起，否则退回最新 `min` 个）+ `ScanUpToAsync`（`SCAN_PAGE_SIZE = 256`，`+1` 探测在 `int.MaxValue` 处饱和） |
| 追加与裁剪 | `advance()` 里每窗口 append，`> max` 时一次 splice 丢到 `min` 并写 `before` | `Advance()` 同结构：`Splice(entries, count, 0, appended)` + `Splice(entries, 0, drop)` + `Set(before)` |
| 客户端转录 | 新增 `transcript.ts`（缺口、`loadEarlier`、`fillGap`、`resume`） | 新增 `Harness/Transcript.cs`：`TranscriptItem`（`Entry` / `Gap` / `Earlier`）、`TranscriptView`、`EntryLoader`、`ConversationTranscript`（`Merge` / `LoadEarlierAsync` / `FillGapAsync` / `Newest` / `Resume`） |

**端口的取舍**：

1. **保留 `ViewObserver.Publication`**（上游删除）。窗口化之后它给出的 `before`/`after` 是该观察者窗口的两次
   修订，既有消费者（`watchEvents`、一个测试）仍在用；删除随 `watchEvents` 一起做。
2. **`watchEvents` 暂留**，并让它挂 `Min = Max = int.MaxValue` 的窗口：事件快照的契约是「含全部条目」，窗口
   裁剪不该改变它。上游把客户端改到 `ConversationTranscript` 之后才删掉整个事件流。
3. **`ConversationTranscript` 的加载用 `Task` + `Task.Yield()`**：与上游的微任务推迟等价，保证并发的
   `loadEarlier` / 同一缺口 `fillGap` 共享同一个 `Task` 实例（测试用 `Assert.Same` 断言）。
4. **`Merge` 的成本是 O(items)**，不做增量索引；`Result()` 用引用比较（items / docs / conversation）保持
   「什么都没加就返回上一份」。

**用例**：`ConversationTranscriptTests` 7 例（首个窗口与 `earlier`、追加并保留掉出的条目、缺口逐页填、
逐页 `loadEarlier` 到开头、并发共享一次加载、`resume` 与越过最新条目的缺口、合并前拒绝与消失的缺口）；
`HarnessViewWindowTests` 5 例（最新 min 个 + `before`、追加到 max 后一次丢到 min、`from` 装得下与装不下、
非法窗口拒绝、真实 Harness 上的 merge → resume → loadEarlier 连续性）。

**连带修正的既有用例**（都是旧语义的直接后果）：`HarnessViewTests` 6 例（head 不再裁剪、fork 继承条目、
原始 head 写入、文档快照、每个观察者自己的值、折叠需要够大的窗口）、`HarnessDocsTests` 的视图构建用例。
调试踩到的坑：`{ max = 3 }` 不带 `min` 会被缺省的 `min = 50` 判为非法（上游同样如此）。

### 4.22 删除 `watchEvents`（`f3255de76` 后半）

窗口化之后，「一份共享的完整视图」不存在了，事件流没有可翻译的输入；上游把客户端迁到
`ConversationTranscript` 之后删掉了整个事件流。端口照做：

- 删 `Harness/Events.cs`（`AgentEvent` / `AgentEventStream` / `Events.WatchEventsAsync`，共 1062 行）、
  `ViewObserver.Publication` 回调与它的派发、`HarnessEventsTests`（2 例）。
- `HarnessDocsTests` 的 `RecordingObserver` 去掉发布计数。
- **迁移说明**（与上游 README 同义）：原来用 `watchEvents` 的客户端改为 `conversation.WatchAsync(transcript.Resume)`
  + `ConversationTranscript.Merge`，缺口交给 `FillGapAsync`，更早的历史交给 `LoadEarlierAsync`。

验证：`dotnet build Pi.slnx` 0 警告 0 错误；`Pi.Durable.Tests` **554** 全绿（删掉的 2 例就是事件流用例）。

### 4.23 `wake()` 睡眠集合与空闲过期索引（`35dfa7573` 收尾）

§4.15 已落地「按脏集合重估 + 链加载未知时全量」；这次补齐剩下两处，都是把「每次扫描全部」换成「只看相关的」：

| 变更 | 上游落点 | 端口落点 |
| --- | --- | --- |
| `wake()` 的最早睡眠只走睡眠集合 | `#sleeping`（`advance()` 与 `open()` 维护） | `_sleeping`（`NoteSleeping` 在 `OpenAsync` 载入与 `Observe` 更新时登记，终态移除）；`ComputeEarliestSleep` 只遍历它，读记录时再核对状态与 `AbortRequested` |
| 有调用在跑时不再扫存活任务 | `#runsNow(now)` 只在 `active` 为假时调用 | `ComputeActive` 在 `_invocations.Count > 0 \|\| _reconcileScheduled` 时直接为真 |
| 空闲过期不扫 `#contexts` | `#idleOrder`（按 `idleSince` 有序）+ `#idleNewest` | `_idleOrder`（`SortedSet<(IdleSince, ConversationId)>`）+ `_idleSinceById` 镜像（O(1) 移除）+ `_idleNewest`；`ExpireContextsLocked` 只看队首，`ScheduleContextExpiryLocked` 用队首算下次定时器 |
| 时钟回退仍保持有序 | `#markIdle` 发现比最新更旧就重排 | `MarkIdleLocked` 同结构：按当时的 `_contexts` 重建索引并重算 `_idleNewest` |
| 保留为零丢掉所有空闲范围 | `#settleIdle` 的 `retention <= 0` 分支 | `SettleContextCache` 同分支（`DropContextLocked`） |
| 空闲等待者按作用域结算 | `#resolveIdleWaiters(scopes)` | `ResolveIdleWaiters(HashSet<ConversationId>?)`；`Observe` 传脏集合，`Reconcile` 的收尾传 `null`（那一遍可能终结任务而没走发布路径） |

**所有 `_contexts` 写点都改走** `MarkIdleLocked` / `ForgetIdleLocked` / `DropContextLocked`（读路径记录范围时也
一样），否则索引会与保留集合脱节。**行为不变**：睡眠/空闲/保留上下文的既有用例（`HarnessSleepTests` 的
`Wake().At` 断言、`HarnessContextCacheTests` 的定时过期与保留为零、`HarnessIdleScopesTests` 的脏集合）
全部照旧通过，durable 554 例全绿。

### 4.24 工具搜索：确认已对齐（`c7372af42` / `79006f061`）

这两行在台账里长期标着 🔧 P5，实际核对下来端口早已具备（此前几批移植时一并做了），这次只补核对记录：

| 上游要求 | 端口落点 |
| --- | --- |
| 覆盖优先（命中查询词更多的排在前面，先于分数） | `ToolSearch.cs` 的 `Covered`，排序 `Exact → Covered → Score` |
| 名称是独立字段、每个词 3x IDF，高于描述里任意次出现 | `NameWeight = 3`、`NameTerms` |
| 整个本地名（最后一个 `__` 之后）都在查询里 → 加一份「每个词再命中一次」的奖励 | `fullNameBonus` 等价分支 |
| 查询恰好等于工具名（或 `__` 之后的名字）→ 置顶 | `Exact` |
| 命名空间文本独立字段、0.5x、不做长度归一 | `NamespaceWeight = 0.5`、`NamespaceTerms` |
| Unicode 分词：NFC、非字母/标记/数字切分、CJK 不分词脚本要有词 | `Tokenize`：ASCII 快路径 + 无空格脚本的 **bigram**（差异 C391，`Intl.Segmenter` 无 .NET 等价物） |

回归用例也已在：`ToolSearchRankingTests`（`screenshot window`、`peekaboo app`、精确名等上游 ranker 用例）与
`ToolSearchCorpusTests`，两组合计 **73** 例通过。

### 4.25 在调用方的提交内受理提交（`cc9c52310`，P6 第十五批）

上游把「宿主自己的检查/写入 + 一次用户提交」放进同一次存储提交（#11149）：`Conversation.submit(tx, draft)`
与 `ConversationHandle.submit(tx, draft)` 重载。端口动作：

| 上游要求 | 端口落点 |
| --- | --- |
| 事务必须属于本 Session（别的 Session 的、`Proxy` 包装的、手搓的对象都拒绝） | `DurableSession.OwnsTransaction(tx)` 比对 `Transaction.Host`；失败抛 `InvalidOperationException`「The transaction belongs to another Session」 |
| 受理随调用方的提交存储或丢弃；必须在首次表写前 | `Submissions.AdmitInCommitAsync`：`Transaction.HasTableWrite` 为真即抛 `ReadAfterWrite("submit(tx, draft)")` |
| 受理本身算一次表写（同一次提交的第二次受理也被拒） | 同上（首次受理后 `_hasTableWrite` 已置位），用例 `RefusesASecondAdmissionInTheSameCommit` |
| 拒绝（`ConversationBusy` / `ConversationIdle`）在写之前抛出，回调可捕获 | 保持原受理顺序：去重 → 忙碌/空闲判定 → 写；用例 `RejectsABusyConversationsContinueBeforeAnyWrite…` |
| `ReadAfterWrite` 的消息按操作命名 | `ReadAfterWrite(string operation)`：事务读表传 `Tx.<method>()`，受理传 `submit(tx, draft)` |
| 句柄版先查本次调用是否结束、是否已中止 | `BoundConversations.Handle.SubmitAsync(ITx, …)`：`binding.Check()` + `binding.Signal.ThrowIfCancellationRequested()`（调用方的提交自带上下文，中止只能在这里查） |
| 受理自己索取进度（不 `resume()` 也能起空闲输入的那一轮） | `AdmitInCommitAsync` 内 `_resume()`，与 `submit()` 相同 |

用例：`tests/Pi.Durable.Tests/HarnessSubmitInCommitTests.cs` 共 **11** 例（宿主文档写与受理同一次提交、回调
抛错回滚、存储拒绝提交后重开无残留、请求 ID 去重、第二次受理、失败运行的 `continue` 早晚两次、工具提交内
经句柄受理并核对 placed 输入的 `byTaskId`、调用结束后的句柄拒绝、别的 Session 的事务）。

未移植：上游这些用例里的 telemetry span 断言（`pi.durable.submit` 的 trace carrier / 链接到运行 trace）——
端口 `Submissions` 本就没有 telemetry 面，与 §4.1 的记法一致；批内其余语义已全部对齐。

### 4.26 P4 首批：两处不改渲染字节的 tui 修复（`f09d01b85` / `1c8cb1781`）

P4 的前置条件是先恢复语料生成链（`markdown-corpus.json` / `alt-screen-corpus.json`）。这两个提交不改变
任何渲染字节，因此可以先做，不会让语料对不上：

| 变更 | 上游 | 端口落点 | 用例 |
| --- | --- | --- | --- |
| Orca 只在 `ORCA_IMAGE_PROTOCOL=kitty` 时算 Kitty 图像能力：两个信号都要，早于该声明的版本继续文本回退 | `f09d01b85` | `TerminalImage.DetectCapabilitiesFromEnvironment` 的 Orca 分支（放在 rex 之后、`KITTY_WINDOW_ID` 之前，保持 tmux/screen 的优先级） | `TerminalImageTests.DetectCapabilities_EnablesKittyImagesForOrcaOnlyWhenItAdvertisesThem`（并把 `ORCA_IMAGE_PROTOCOL` 加进测试的环境清理清单） |
| 视口上方的图像行搜索按「已注册图像里最高的行数」定界，而不是遇到第一个非空行就停 | `1c8cb1781` | `TerminalImage._maxKittyImageRows`（只增不减）+ `MaxKittyImageRows`；`Layout.PaintBox` 的倒序循环改成 `minRow = max(0, scrollTop - MaxKittyImageRows)`、`!IsImageLine` 与无元数据都 `continue` | `TerminalImageMetadataTests.MaxKittyImageRowsTracksTheTallestRegisteredImage` |

**为什么只增不减**：这个值只用来缩小搜索范围。图像被淘汰后不回退，搜索范围偏大只是多走几步；
偏小才会漏掉已滚出视口的图像（上游注释同此）。

**未移植的回归用例**：上游 `tui-alt-screen.test.ts` 那个端到端用例（滚到 `viewportTop=3` 后断言写出
`i=11272` 且 `y=66,h=34,r=1`）需要「录制终端 + 全屏渲染」的测试设施，端口 `Pi.Tui.Tests` 没有这一层
（现有全屏用例走语料，图像相关用例只测编码/元数据）。因此这项行为只由代码与 `MaxKittyImageRows` 的
单元用例背书，端到端等语料链恢复后补。

### 4.27 P6 第十六批：partial 提交按追加写文本（`da412b039` 的 durable 侧）

上游 `da412b039`（#9265 的后续）让流式 partial 的提交花「新增文本的长度」，而不是整段消息的长度：

| 上游要求 | 端口落点 |
| --- | --- |
| chord 侧 `appendText(draft, key, text)` 免去 O(长度) 的前缀比较 | 端口本来就有等价物：`Tracker.Change.Append(path, text)` 直接记 `a` op（端口不是 JS Proxy + diff，而是显式动词），所以这部分无需改动 |
| 从响应事件里攒文本/思维增量，块结束（`text_end`/`thinking_end`）时丢弃（provider 可能替换整段） | 新增 `Harness/PartialChanges.cs`：`Observe(evt)` 按内容块序号合并 `text`/`thinking` 增量，`Take()` 取走 |
| 提交按「逐叶赋值 + 追加」写 `generation.message`；没有已提交消息或块数变了才整段写 | `PartialChanges.Commit(live, taken, message, committed)`：块数相同且已提交块有基串时逐叶赋值 + `Append`，否则整段写 `content` |
| 增量与 partial 的排队必须是同一原子步骤 | `PartialThrottle.Observe(evt, partial)` 在同一把锁里 `Observe` + 排队（JS 单线程天然如此，端口要显式加锁，否则一次冲刷可能取走增量而它的 partial 还没排队） |

**踩坑**：草稿里 `generation` 在请求开始时只有 `{attempt, compacted}`，`message`/`content` 还不存在；而 `DeltaApply.SetAt`
对「下标段下降进 null」直接抛 `cannot descend into null`。所以块数为 0（或与已提交块数不等）时必须整段写 `content`，
不能逐块 `Set`。

**未移植**：工具调用参数的增量（上游每个 `toolcall_delta` 喂自己的 `StreamingJsonParser`，把参数改写成按路径的
set/append）。端口没有 pi-ai 的增量解析器，工具调用块仍整体赋值——协议语义一致，只是大参数流式时每次提交仍
O(参数长度)。要做的话顺序是：先移植 `packages/ai/src/utils/streaming-json.ts`（559 行，含宽松修复语义与变更流）。

用例：`HarnessPartialAppendTests.StreamsTextAsAppendsInsteadOfReassigningTheMessage`——每个增量等它落盘再发下一个
（写入形状不随并行负载的合并而变），断言文本路径上全是 `a` op、没有任何一次把整条 `generation.message` 重赋，
且最终助手条目仍是完整答案。

### 4.28 P4 第二批：OSC 133 与双击选词字符集（`19ce8059c` / `ff6896471`）

| 变更 | 上游 | 端口落点 | 用例 |
| --- | --- | --- | --- |
| 常规模式不再把 OSC 133 提示符标记写到终端（终端把 `133;A` 当成新 shell 提示符，会结束 OSC 7501 程序状态，`working` 指示随即消失） | `19ce8059c` | `TuiMainScreen` 在合成前对每行 `Osc133ZonePrefix()` 剥一次（与全屏用的同一个模式：`133;A/B/C`，BEL 或 ST 结尾） | `TuiMainScreenTests.Render_StripsOsc133PromptMarkers`（BEL 与 ST 两种结尾） |
| 全屏双击选词用 iTerm2 的默认 word 字符集，`~/...`、`~/.pi/...`、`foo+bar` 整段选中 | `ff6896471` | `TuiAltScreen.TerminalWordSelectionJoiners` 由 `{"/","-"}` 扩到 `{"/","-","+","\\","~","_","."}` | 未移植（见下） |

**未移植的用例**：双击选词要经鼠标事件驱动（`GetWordSelection` 是私有，公开面只有 `HasActiveSelection` /
`GetActiveSelectionText`），上游那条是 `tui-alt-screen.test.ts` 里的端到端用例；端口补这条得先有输入/鼠标的
录制式测试设施。

验证：`dotnet build Pi.slnx` 0 警告 0 错误；`Pi.Tui.Tests` **23945** 例全绿（新增 1 例）。

**附：`deb12d78e` 的试做与回退（语料阻塞的实证）**。按上游把 F1–F12 的内部码点、`CSI P/Q/S` 解析、
`:3P/:3Q/:3S` 释放事件、`f1`…`f12` 的修饰键匹配、F13+ 私用区拒绝都移植进来后，`dotnet build` 与
`KeysAndUtilTests` 全绿，但 `Pi.Tui.Tests` 全量红 **43** 例，全是 `EditorCorpusTests.ScenariosMatchTheTypeScriptReference`
的 `keys` 场景：

```
[k464] expected {"L":["hello world\uE008"],"C":[0,12],...}
       actual   {"L":["hello world"],"C":[0,11],...}
```

语料是 1.4.x 的 TS 跑出来的，那时 F13+ 确实会被当成文本插进编辑器（正是这次要修的 bug）。所以这项**不是实现问题，
是语料版本问题**：必须先用 1.5.0 的 TS 重算 `editor-corpus.json` 才能落地。已整体回退，代码库保持全绿。

### 4.29 `89c921d8b` 的 tui 侧核对：已落地

| 上游要求 | 端口落点 |
| --- | --- |
| `setTitle()` 先替换控制字符（标题里的 BEL/ESC 会提前结束 OSC 0，剩下的字节被当成转义指令执行） | `Ansi.ReplaceControlCharacters`（`[\u0000-\u001f\u007f-\u009f]+` → 一个空格），`ProcessTerminal.SetTitle` 与 `StringTerminal.SetTitle` 都用它 |
| 导出的 `replaceControlCharacters()` 供状态与对话框复用 | 同一份 `Ansi.ReplaceControlCharacters`：`ProgramStatus.FormatProgramStatus` 的 `msg`、`LoginDialog` 的 URL 展示都用它 |
| durable：partial 提交不再把新的 `__proto__` 成员并进 `Object.prototype` | ➖ C# 没有原型链，`Dictionary<string, object?>` 里的 `__proto__` 只是普通键 |
| ai 侧（可信 URL、contentIndex 校验、auth.json 0600） | ✅ P2 已收口（见 §2.2 的 `89c921d8b` 行） |

台账里这行长期写着「部分」是没核对：tui 三处都在，durable 那条本就不适用。

### 4.30 原生 Shift+Enter（`86dc686cf`，前置 `73dd066ee`）

上游在**序列层**做这件事：`normalizeNativeShiftEnterInput(data, shouldDetect, isShiftPressed)`，`shouldDetect` 表示
「这串是 CR 或 ESC+CR，且平台是 Apple Terminal 或 Windows」；修饰键状态来自 tui 的原生插件（`native-modifiers.ts`
+ win32/macOS 预编译）。

端口此前只在 `MapKey` 里做了 Windows 一半：`ConsoleKey.Enter` + `Shift` → `\x1b[13;2u`。这次补齐序列层：

| 上游要求 | 端口落点 |
| --- | --- |
| `shouldDetectNativeShiftEnter(sequence)`：CR / ESC+CR 且 Apple Terminal 或 Windows | `ProcessTerminal.ShouldDetectNativeShiftEnter` |
| Shift 按下时换成 `CSI 13;2u`，否则原样 | `ProcessTerminal.NormalizeNativeShiftEnterInput`（纯函数，`NativeShiftEnterSequence` 常量） |
| 修饰键查询 | `NativeModifiers.IsShiftPressed`：Windows `GetAsyncKeyState(VK_SHIFT)`、macOS `CGEventSourceFlagsState` 的 `kCGEventFlagMaskShift`；`DllNotFoundException` / `EntryPointNotFoundException` 一律当「没按」 |
| 输入转发的最后一站应用 | `ProcessTerminal.ForwardInputSequence`（在吞掉终端回复之后、交给 `_inputHandler` 之前） |

**为什么用 P/Invoke 而不是移植原生插件**：上游的 `.node` 预编译产物在 .NET 里没有消费者，而这里只需要一个
「Shift 是否按着」的布尔值；猜错最多退回原来的 alt+enter 行为，不会破坏输入。

用例：`ProcessTerminalInputTests.NormalizeNativeShiftEnterInput_UpgradesCarriageReturnOnlyWhenShiftIsPressed`
（CR / ESC+CR / Shift 未按 / 不该判定 / 谓词五个方向）。原生查询本身不做断言——它的结果取决于跑测试时键盘状态。

### 4.31 Markdown 顶层块复用（`0f21a3319` 的 tui 主项）

流式回答的消息组件每帧重渲染整段 Markdown：所有块都要重新样式化、折行，而实际上只有最后一块在变。

| 上游要求 | 端口落点 |
| --- | --- |
| 顶层块按源码复用，键还要带上「宽度」与「下一块类型」（它决定间距） | `Markdown.RenderedBlock { Width, NextTokenType, Lines }` + `_blocks`（`MarkedToken.Raw` → 块） |
| `setText()` 只清行缓存、保留块；`invalidate()` 连块一起丢（主题可能变了） | 两个方法分开：`SetText` 清 `_cachedText/_cachedWidth/_cachedLines`，`Invalidate` 额外 `_blocks = null` |
| 文本里有引用式链接定义（`[a]: url`）时整篇不复用——定义会改变别的块的解析结果 | `MarkedLexer.Links.Count > 0` → `reuseBlocks = false` |
| 块缓存持有弱引用，避免长转录里每个消息各留一棵渲染结果 | **偏差**：端口用普通字典，只保留**本次渲染用到**的块（等于「当前文本的块数」），随组件一起回收。上游的弱引用是为了抗一段连续重渲染的突发，端口这层没有 GC 压力，先不加 |

用例：`MarkdownBlockCacheTests` 三例——只改最后一块时前面块的加粗不再被调用；`Invalidate` 后重新样式化；
引用式链接的文本改定义后第一块会重渲染。

**未移植（同批的上游其余部分）**：① `highlightCode` 的逐块复用（上游在块里存 `Map<lang+source, lines>`，
并让 coding-agent 的 `CodeHighlighter` 自己判断主题/语法是否变了）——端口主题里 `HighlightCode` 只是一个委托，
要做这层得先让 coding-agent 的高亮器带缓存；② `Text.setText()` 保留未变文本的折行结果（12 行的独立优化）。
两项都只影响性能，不影响输出；块级复用已经是流式路径上的主要开销。

### 4.32 语料链现状与结论（为 P4 余项铺路）

§3 末注说「动 P4 前先恢复语料生成链」。这轮把链路探通了，也探到它**不能直接当 oracle** 用：

**跑通的部分**：上游 `packages/tui/src/*.ts` 可以用 `node --experimental-strip-types`（v22.22.2）直接跑，只需三个
运行期依赖，版本按 `package-lock.json` 钉住：

| 包 | 版本 | 为什么需要 |
| --- | --- | --- |
| `marked` | 18.0.11 | `components/markdown.ts` 的词法器 |
| `marked-cjk-friendly` | 0.1.2 | CJK 断行扩展 |
| `get-east-asian-width` | 1.6.0 | `utils.ts` 的宽度计算 |

验证方式：把这三个包（外加 `chalk`）装到上游仓库根的 `node_modules`，脚本放仓库内用相对路径 import，`Markdown`
就能 `render(width)`，输出与 .NET 侧同构。上游仓库本身仍是干净的（`node_modules` 被 gitignore）。

**结论（重要）**：语料里的 SGR 序列**不是真 chalk 产生的**。同一句
`## h2 with **bold** and \`code\`` 宽 1，语料（`markdown-corpus.json` id 594）第 9 行是

```
\x1b[1;36m\x1b[1m\x1b[36m\x1b[39m\x1b[22m...     ← 先重开外层 1m+36m，再按 39m、22m 关
```

而 chalk 6.0.0（1.4.0 与 1.5.0 的 lockfile 都钉这个）与 chalk 5.3.0 都给

```
\x1b[1;36m\x1b[22m\x1b[1m\x1b[39m\x1b[22m...     ← 嵌套同款样式时先关再开
```

也就是说：**语料的主题是「每个样式一对定向开关」的朴素实现**（`bold` = `1m…22m`、`cyan` = `36m…39m`、
`heading` = `1m36m…39m22m`，重开由渲染器按当前活动码重建 `1;36m`），与端口 `Colors` 的 chalk 模拟一致，而与
真 chalk 的嵌套重开规则不同。

**因此**：想要「生成 1.5.0 语料」必须用同一套朴素样式函数（端口 `ChalkStyle` 的 TS 等价物），不能直接用 chalk；
否则每个带样式的用例都会有 SGR 差异，得到的不是 oracle 而是一堆噪声。

**对 P4 余项的落地方式**（5 项）：逐项移植上游改动，并**照抄上游那次提交里的定向用例**（`wrap-ansi.test.ts`
51 行、`markdown.test.ts` 16 行这类用例都用纯文本断言，不依赖主题），最后只把「确实变了」的语料用例按
朴素样式函数重算。比整篇重生成可控得多。

### 4.33 换行边界与删除线（`2ce9d527d`）

上游 #11591：`splitIntoTokensWithAnsi()` 把「关闭样式」的码挂到下一个可见字符上，于是 `**commodo** consequat`
被切成 `\x1b[1mcommodo`、`\x1b[22m `、`consequat`。旧代码用 `token.trim() === ""` 判空白，`"\x1b[22m "` 不算空白，
于是一个**带码的空格**被当成词搬到下一行：被折断的那一行少了收尾码（样式渗进右侧 padding），下一行还多出前导空格。
另外 `getLineEndReset()` 只关下划线，跨行折行的删除线会一路渗到 padding。

| 上游要点 | 端口落点 |
| --- | --- |
| 判空白前先跳过转义序列（遇到第一个可见非空白就返回，不分配去码副本） | `TextLayout.IsWhitespaceToken` + `Ansi.ExtractAnsiCode` |
| 空白在折断处被丢掉时，把它携带的码留在本行并更新 tracker | `WrapSingleLine` 的折断分支：`lineToWrap += ExtractAnsiCodes(token); UpdateTrackerFromText(token, tracker)` |
| 比空行还宽、且带码的空白只保留码 | 新增分支：`isWhitespace && totalNeeded > width && token.Contains('\x1b')` |
| 长词前先 `trimEnd()`，否则行尾 reset（现在含删除线）会挡住最后的 `trimEnd()` 留下尾随空格 | 长词分支加 `currentLine = currentLine.TrimEnd()` |
| 行尾关删除线（`29m`），下一行由 `GetActiveCodes()` 重开 | `AnsiCodeTracker.GetLineEndReset` 增加 `_strikethrough` 分支 |

**用例**：`WrapAnsiBoundaryTests` 六例，逐字照抄上游 `wrap-ansi.test.ts` 的期望字符串（纯文本断言，不依赖主题）——
边界收尾码、被丢掉的空格带颜色 reset、删除线行尾关闭与下一行重开、带背景时只关删除线、超宽带码空白不越界、
长词前 trim。这六例是这次改动的 oracle：实现不对它们就红。

**语料重算**：`markdown-corpus.json` 有 **187** 例受影响（换行位置变了，行数也会变）。用一次临时插桩把这些用例的
实际输出导出，再用一条守恒不变量把关后写回：

> 去掉全部 ANSI（SGR 与 OSC 8）与全部空白后，新旧字符流必须**逐字符相同**。

这次改动只允许移动空白与转义码、不允许增删文字，所以这条不变量能挡住「顺手改坏了内容」的假更新。
187 例全部通过该检查后写回；插桩随即撤掉。`Pi.Tui.Tests` 由 23949 变为 **23955**（+6 例），全绿。

### 4.34 `4464442f9` 核对：已被 `1c8cb1781` 的定界搜索覆盖

`4464442f9` 的改动只有 `paintBox` 里一行停止条件：把 `if (imageLine !== "") break;` 换成
「剥掉 SGR 后还有可见文字才 break」——目的是别让带内边距/底色的**占位行**（只有空格与 SGR）挡住向上找图像行。

端口在 `1c8cb1781` 那批里已经把这整段重写成「按已注册图像的最高行数定界」的倒序搜索：

```csharp
if (!TerminalImage.IsImageLine(imageLine)) continue;          // 占位行、文字行都跳过
if (TerminalImage.GetKittyImageMetadata(imageLine) is not { } meta) continue;
...
break;                                                        // 只有真正命中图像行才停
```

也就是说停止条件只剩「命中图像行」或「走到 `minRow`」，比上游那版更宽松（上游仍会在第一行非空且非图像的
文字处停下）。`4464442f9` 想解决的问题因此已经不存在，无需再改代码。

### 4.35 显示公式超宽回退原始 LaTeX（`cd1ea342a`）

`$$…$$` / `\[…\]` 会排成多行 2D 版式（分子、分数线、分母各一行）。版式比内容宽度还宽时，旧代码对**每一行**各跑一次
`wrapTextWithAnsi`：只有溢出的那行断开，下面的行整体下移，公式就错位了（`dx` 掉到分数线与分母之间）。

| 上游要点 | 端口落点 |
| --- | --- |
| `latexBlock` 先渲染 2D 版式，任一行 `visibleWidth > width` 就退回 `token.raw.trim()`（与解析失败时的回退一致），退回后像普通文本一样折行 | `Markdown.RenderToken` 的 `latexBlock` 分支：`rendered = Latex.Render(...)`，`rendered is null || 任一行 VisibleWidth > width` 时改用 `JsString.Trim(token.Raw)` |

**用例**：`MarkdownLatexFallbackTests` 逐字照抄上游回归用例的方程——宽 40 时必须是三行 2D 版式（含 `────` 分数线），
宽 30 时首行 `\[`、末行 `\]`、没有分数线、每行 ≤ 30。

**语料重算**：42 例受影响。守卫条件与前一批不同（这次**会**改变可见文字，因为输出从 2D 版式换成了源码）：

> 输入含显示公式（`$$` 或 `\[`），且新输出出现原始标记（`$$`/`\[`/`\]`）而旧输出没有——即「确实发生了回退」。

只接受满足该条件的用例：32 例按「旧行宽超过内容宽」判定通过，另 10 例的旧输出是**已折行**的版式（行宽看不出超宽），
改用「原始标记出现」判定后一并通过。42 例全部写回，插桩撤回。

### 4.36 Kitty 功能键（`deb12d78e`）

上一轮试做过、被 43 例语料挡住（§4.28 末），这轮用新方法落地。

| 上游要点 | 端口落点 |
| --- | --- |
| `FUNCTIONAL_CODEPOINTS` 增加 `f1`…`f12`（-21…-32），`~` 编号 11–15、17–21、23、24 映射到它们（16、22 不占位） | `Keys.CpF1`…`CpF12` + `ParseKittySequence` 的 `keyNum` 分支 |
| `CSI P/Q/S` 与 `CSI 1;<mod>[:event] P/Q/S` 解析成 F1/F2/F4（F3 仍走 `CSI 13~`，因为 `CSI 1;<n>R` 与光标位置报告冲突） | 新增 `F1F2F4Regex` 与解析分支 |
| `matchesKey` 的 `f1`…`f12` 允许修饰键：无修饰键先比传统序列，否则走 `matchesKittySequence` | `FunctionKeyCodepoints` 表 + `Keys.MatchesKey` |
| `isKeyRelease` / `isKeyRepeat` 认出 `:3P/:3Q/:3S` 与 `:2P…`（否则 F1/F2/F4 释放会二次触发快捷键） | 两个判定函数各加三个后缀 |
| 键名格式化输出 `f1`…`f12` | `FormatParsedKey` 的 `>= CpF12 and <= CpF1` 分支 |
| `decodeKittyPrintable` 拒绝功能键私用区（57344–63743），否则编辑器插入不可见的 U+E020+ | 同一判定，`KittyFunctionalPuaStart/End` |

**用例**：`KeysAndUtilTests.ParseKey_RecognisesKittyFunctionKeys`——F1/F2/F4 的三种形式、`~` 编号、F3 不被光标报告混淆、
带修饰键的 `ctrl+f1` / `ctrl+f5` / `shift+f2`、释放与重复事件、私用区不当文本。

**语料重算**：`editor-corpus.json` **276** 例受影响（`f13`+ 不再插入私用区字符）。守卫按这次改动的语义定：

> 每一步的行文本（`L`）只允许**少掉 U+E000–U+F8FF 私用区字符**；光标列（`C`）的减少量必须等于该步少掉的字符数；
> `A`/`P`/`M` 不变；`changes` 只能减少，且减少量不超过少掉的字符数。

最后一条放宽过一次：原本要求「每少一个字符就少一次 change」，但有 42 例是「连按两次同一个 F13 键」——旧语料
记 2 次 change、新行为只记 1 次（第二次按键既没文字也没改动），这条等式不成立。放宽后 276 例全部通过并写回。

### 4.37 选择列表翻页键与 `capturesViewportInput`（`3bae85274`）——P4 收口

这是 P4 最后一项，也是台账里描述最失真的一项：一行字，实际 492 行 / 32 个文件。拆成两层做：

| 上游要点 | 端口落点 |
| --- | --- |
| `getSelectPageIndex(kb, data, selectedIndex, itemCount, pageSize)`：`tui.select.pageUp/pageDown` 各移一页并夹到 `0..count-1`，非分页键返回 undefined | `TuiSelectPaging.GetSelectPageIndex`（返回值 `int?`） |
| `Component.capturesViewportInput`：聚焦时是否截获 `tui.altScreen.*` 的滚动与提示符跳转 | `IComponent.CapturesViewportInput => false`（默认接口成员，与 `HandleInput` 同样带默认实现） |
| 全屏路由：`shouldDeferViewportInputToOverlay()` → `shouldDeferViewportInput()`，先看**聚焦组件**是否声明截获 | `TuiAltScreen.ShouldDeferViewportInput()`：`GetFocusedComponent()?.CapturesViewportInput == true \|\| 原来的 overlay 判定` |
| `SelectList` / `SettingsList` 声明 `capturesViewportInput = true` 并实现分页键 | 两个组件各自 `CapturesViewportInput => true` + 分页分支（页大小取 `_maxVisible`） |

**用例**（`SelectPagingTests`，三例，逐条对应上游）：

1. `SelectList`：12 项、可见 5 行，PageDown×4 + PageUp → `item-5 → item-10 → item-11（夹到末尾）→ 6`；过滤成空列表后翻页不再选中。
2. `SettingsList`：同参数按页移动，渲染里出现 `> Setting 5` / `> Setting 11` / `> Setting 6`。
3. **全屏路由**：`Text`（12 行）+ 聚焦的 `SelectList`，PageDown 与 Ctrl+Home 都交给列表——选择到 `item-3`，而
   `ViewportTop` 不变（证明这两个键没有再滚转录）。

第 3 例要 `tui.Start()` 之后才送输入（`StringTerminal` 的输入处理器在 `Start` 里注册），这是端口测试设施的约定。

**上游 editor 那 30 行用例没有单独移植**：它验证的是「编辑器自动补全关闭后视口键回到转录」，走的是编辑器内嵌的
`SelectList`（已声明截获）+ 关闭后 `SetFocus` 回到编辑器两条既有路径，端口第 3 例已经覆盖同一路由分支。

### 4.38 `6c2fd73b0` 延迟落盘：第一阶段的落地与第二阶段的清单

**已完成（`39d733f`，行为不变）**

- 公共类型：`CommitPersistence`（`Immediate` / `Deferred{WithinMs}`）、`CommitOptions`、`ObservedCommit`
  （`Seq` 为空 = 只在内存采纳）。上游是判别联合，端口用一个记录 + `IsMemory` 表达，消费者只需读 `Changes`。
- 事务侧：`HeldDocument`、`AdoptedDocuments`、`DocumentPlan.Held`；`Adopt` 同时给出 `Observed` 与 `Stored`；
  `Holdable()`（前置条件：全部写入都是 `document.change`，每个计划都改一个**已存在**的 `latest`/`initial` 对话文档，
  且 `StoredVersion == Version`——版本落后说明是迁移，要立刻写 base）、`AdoptHeld()`（只按指针交换 tracker，
  不动 `StoredVersion` 与 `DeltasSinceBase`）、`TakeHeld()`（把持有排在本次自有写入之前，**插在同化身的退役写入前面**，
  并解析只读计划的属主）、`StoreHeld()`（`PendingOps` 折叠持有 + 本次 ops；溢出成 `Replace` 或持有里选过 base 时写 base）。

**第二阶段（Session 侧，尚未做）**：

1. 字段：`Dictionary<DocumentId, Hold> _holds`（`Hold` 包 `HeldDocument` + `Deadline` + `Unpin`）、`HoldTimer? _holdTimer`、
   `HashSet<Action<ObservedCommit, Context>> _observedListeners`、`Func<double> _persistWithinMs`；构造参数 `persistWithinMs`。
2. 提交入口：`CommitAsync`/`CommitWithAsync` 增加 `CommitOptions?`；`Deferral()` 把 `Deferred.WithinMs` 校验成
   `>= 0`（无效抛 `ArgumentException`），`null` 时用 `PersistWithin`（缺省 1000ms，`Infinity` 表示只等读/写或关闭）。
3. `RunCommitAsync(..., int? withinMs)`：只读检查之后，`withinMs` 非空且 `tx.Holdable()` 非 null → `Hold()`（`tx.AdoptHeld()`
   的发布只发给 `_observedListeners`）+ `SweepDocuments()` + 返回；否则 `tx.TakeHeld(id => _holds.GetValueOrDefault(id)?.Held)`
   在**Storage 受理前的最后一步**取走，采纳成功后 `Release(taken)`。
4. `Hold()`：合并进已有 `Hold`（`Pending.Push(ops)`、`Base |= base`、`Deadline = min`），首次持有要 `PinDocument`。
5. `StoreHoldsAsync(holds, context)`：为每个持有构造 `document.change`（`StoreHeld(hold.Held, [], value, ValueVersion)`），
   一次 `_storage.CommitAsync`，采纳回调里按 base/delta 调整 `DeltasSinceBase`，然后 `Release`，只发给 `_commitListeners`
   （宿主监听器只该看到真正落盘的东西）。
6. `ArmHoldTimer()`：取最早 `Deadline`，`_closing` 与 `_fired` 时不再重排（照 `ScheduleDocumentSweep` 的写法：
   `System.Threading.Timer` + `Enqueue`）。
7. `Publish` 拆分：`_observedListeners` 拿 `ObservedCommit`（含 `Seq`），`_commitListeners` 拿 `CommitPublication`；
   这次同时落了持有变更时，宿主的文档变更要带上 `Held.Ops`（持有在前），而内存观察者只看本次自己的。
8. `ObserveCommits(...)` 内部订阅，`ConversationView` / `Scheduler` / `Submissions` / `TaskGraphView` 从
   `SubscribeCommits` 切过去（它们跟随内存，不能等落盘）；宿主 `SubscribeCommits` 语义不变。
9. 关闭 / `UnloadDocuments` / `LoadDocument` 重载前都要先 `StoreHoldsAsync`（否则内容随缓存消失）。
10. 调用方：`ProgressPolicy` 增加 `PersistWithinMs`（缺省 1000），`TaskRuntime.CommitAsync` 增加 `CommitOptions?`，
    生成 partial 的 `PartialThrottle` 与工具输出节流改用 `Deferred`。
11. 用例：延迟提交后 `subscribeCommits` 一次都没收到、watch 立刻看到内容、下一次读/写该文档时落盘一次（带 seq）、
    `close()` 落盘、`Infinity` 只在读/写或关闭时落盘；以及「非 latest/迁移/含表写入」都会立刻落盘。

**第二阶段的一次尝试（已回退，`7b579b9` 之后）**

按上面 11 条实现过一版（Session 字段/构造/`CommitOptions` 贯穿 `ISession` → `ITaskRuntime` → `Scheduler.GatedAsync`、
持有与落盘、定时器、`Publish` 拆分、`ObserveCommits` 与四个消费者、关闭/卸载/重载前落盘、`ProgressPolicy.PersistWithinMs`、
partial 用 `Live.ProgressCommit`），构建绿。临时诊断确认**选项确实传到了 Session**：

```
[diag] commit options=Deferred within=1000
```

但 `Transaction.Holdable()` 对 `pi.live` 的 partial 提交返回了 null，于是没有任何提交被持有，
`ObserveCommits` 收不到 `IsMemory` 的提交（用 `PersistWithinMs = int.MaxValue` 的用例直接暴露）。因为已到本轮预算边界，
整体回退到 `7b579b9`（工作区干净、`Pi.Durable.Tests` 566 全绿），没有留下半成品。

下次接着做时，**先给 `Holdable()` 的四个 `return null` 分别加断言/日志**（`_writes` 含非 `document.change`、
`plan.Content`/`plan.Change.Loaded` 为 null、`History`/`Fork` 不符、`StoredVersion != Version`），
确定 `pi.live` 卡在哪一条再动手——选项管道与 Session 侧机制这次已经验证可用。

**第二阶段的第二次尝试（也已回退）：机制通了，卡在视图投递计数**

这次把 11 条全部实现了一遍，并用临时探针验证到「持有真的发生了」：

```
[commit] within=600000 writes=[DocumentChange] plans=[... loaded=pi.live stored=1 version=1 content=DocumentChange history=Latest fork=Initial]
[hold] docs=1 listeners=5
```

沿途确认并修掉两个真问题（下次直接带上）：

1. **`HarnessImpl` 必须读活的设置来源**：原来写 `options.Settings?.Progress?.PersistWithinMs`，而测试夹具用的是
   `SettingsSource = () => setup.Settings`（`Settings` 为 null），于是会话永远拿缺省的 1000ms。
   正确写法：`(options.SettingsSource?.Invoke() ?? options.Settings)?.Progress?.PersistWithinMs`。
2. **内部监听器抛出不该毒化会话**：视图自己会把监听器错误折成 `WatchEnd.ListenerError`，`PublishObserved` 里若
   `_poison = error` 就会让后续提交全部失败（`HarnessViewTests` 立刻红）。

剩下两条 `HarnessViewTests` 失败是**同一批上游改动里的测试期望**，不是新 bug：

- `SharesUnchangedParts…SkipsCommitsThatTouchNothingMounted`：`frames.Count` 期望 2、实际 1；
- `SharesOneMountBetweenConcurrentObservers…`：同样期望 2、实际 1。

原因方向：视图现在会收到 `Seq` 为 null 的内存提交（`ToPublication()` 把它们折成 `Seq = default`），
它的投递记账（已投递 seq / 合并窗口）需要按上游那样区分「内存提交」与「落盘提交」——这正是 `6c2fd73b0`
在 durable 测试里同步改的部分。两次尝试都在这里耗尽预算，已回退到 `aa31071`（工作区干净、`Pi.Durable.Tests` 566 全绿）。

**下次的顺序建议**：先照上面两条把机制接上 → 再看那两个视图用例，按「内存提交不参与 seq 记账、只按 ops 合并」改
视图的投递逻辑 → 最后补 `DeferredPersistenceTests`（延迟窗口用 600000，断言「有 `IsMemory` 提交」「关闭后宿主看见落盘」；
`PersistWithinMs = int.MaxValue` 会走 `(int)` 转换，别用它）。

**第三阶段尝试：落地成功（两阶段都完成）**

前两次卡在「持有没发生」与「视图投递计数」，这次两条都解了：

| 问题 | 结论 |
| --- | --- |
| 持有没发生 | 不是机制问题：`HarnessImpl` 读的是 `options.Settings`，而夹具走 `SettingsSource`（活的来源）。改成 `(options.SettingsSource?.Invoke() ?? options.Settings)?.Progress?.PersistWithinMs` 后，探针立刻看到 `[commit] within=600000 …` 与 `[hold] docs=1 listeners=5` |
| 视图计数少 1 | **视图不该跟随内存**：`ConversationViews` / `TaskGraphViews` 是「把内容交给 UI」，原来的 `SubscribeCommits`（落盘后的那份，已带上持有 ops）就是对的。只有 `Scheduler` 与 `Submissions` 必须跟随内存，切到 `ObserveCommits` 即可 |
| 内部监听器抛出 | 只 `Report`，不 `_poison`：视图/调度器各自把错误折进自己的状态（`WatchEnd.ListenerError`），毒化会话会让后续提交全挂 |

落地清单（`ConfigureAwait`/命名与清单一致）：`CommitPersistence`/`CommitOptions`/`ObservedCommit`、`Holdable`/`AdoptHeld`/`TakeHeld`/`StoreHeld`
（§4.38 上半）、Session 的 `_holds`/`Hold`/`HoldTimer`/`HoldCommit`/`StoreHoldsAsync`/`Release(Held)`/`ArmHoldTimer`、
`Publish` 的 observed/stored 拆分、关闭/卸载/重载前落盘、`ObserveCommits`（调度器与提交受理）、
`ProgressPolicy.PersistWithinMs`、`Live.ProgressCommit` 让生成 partial 走 `Deferred`。

**用例**：`DeferredPersistenceTests.PartialCommitsAreDeferredAndEventuallyStored`——逐增量的假流（每个增量等它出现在
live 文档里再发下一个，否则整段流在第一次冲刷前就结束）、窗口 10 分钟，断言「有 `IsMemory` 提交」且「关闭后宿主
监听器看见 pi.live」。

验证：`dotnet build Pi.slnx` 0 警告 0 错误；`Pi.Durable.Tests` **567** 全绿；`Pi.CodingAgent.Tests` 与基线一致
（5208：1 既有失败 / 3 跳过）。

## 5. 1.5.0 全量核对（`v1.4.0..v1.5.0`，308 个提交）

用户口径：「只同步到 1.5.0 正式版」。据此把 `v1.4.0..v1.5.0` 全量重扫了一遍——**原来的台账是按精选清单写的，
漏了 44 个 feat/fix 提交**。下面按范围分好类，端口相关的那 17 条给了判定。

方法（可复现）：从 `docs/upstream-1.5.0-delta.md` 抽出所有 9 位短哈希，与
`git log --name-only v1.4.0..v1.5.0` 里触及 `packages/{coding-agent,durable,tui,ai,env,mcp}` 的提交比对，差集即漏项。

**端口相关（待评估）**（17）

- `a6bde34bb` feat(schema): benchmark against ata-validator, Ajv and Zod
- `c2760d32c` fix(coding-agent): name the package manager in pi update only when it owns the install (#11628)
- `6fed3cfb9` fix(coding-agent): let the SDK stream resolve auth for summaries (#11556)
- `b5fc6dc84` fix(coding-agent): strip private-parameter CSI sequences in stripAnsi
- `018fdcf43` fix(durable): pi.live version 3 gives a stored run without turns a count of 0
- `f6ee9ce9c` fix(coding-agent): summarize union and multi-line codemode output types in tool descriptions (#11513)
- `7391dbb0f` fix(coding-agent): show model scope in the fullscreen header instead of the shell (#11512)
- `bb65e3e49` fix(durable): import toCodemodeIdentifier from pi-codemode/declarations
- `531340f10` fix(ai): remove unused root bedrock-provider shims
- `a6a7bac86` fix(coding-agent): hidden thinking with an empty label renders no rows (#11470)
- `f049787d9` fix(coding-agent): default openai provider to gpt-6.1-sol (#11467)
- `c17be1e98` fix: skip custom-message runs in prompt-url-widget
- `1e42501bb` fix(durable): keep pi.live cached in the partial-commit cost test
- `3953e9ef4` fix(examples): todo renderResult returns undefined on validation errors
- `c8ec752cf` fix(coding-agent): skip fd/rg downloads on unsupported architectures
- `476a303c1` fix(coding-agent): drop min-release-age override from managed npm ci (#11270)
- `5884ee681` feat(coding-agent): let /ir import a session file from a .jsonl URL

**chord v2（端口只跟 chord v1，超范围）**（16）

- `513f6f1ce` fix: keep Chord example dependencies out of releases
- `20362c155` fix(chord): widen the origin-record heap bound for CI noise
- `cc08278a6` fix(chord): slice 10 http builds without DOM types
- `8959ad6e6` feat(chord): Chord v2 slice 10, HTTP and workers
- `a14cdf90f` feat(chord): v2 slice 7c, plugins component and per-plugin load and reload
- `4b535d57e` feat(chord): slice 7b, reload in the kernel
- `339847eb1` feat(chord): Chord v2 slice 7a, the dependency model
- `5ec241882` feat(chord): hostAddress instead of placement, required on remote types; only hosts with an address serve remote types
- `6eb99ad65` feat(chord): Chord v2 slice 8, registries and host.root
- `391e83fb1` fix(chord): Chord v2 follows its design where the audit of slices 1-6a found it did not
- `14b4c56e5` feat(chord,durable): Chord v2 slice 6b, Durable and jobs on the store
- `a92283056` fix(chord): Delta's tracker holds its one open change without a WeakRef
- `5f312dac4` feat(chord,durable): failures are watch calls; one vocabulary for watches, observers and reports
- `973b74c55` fix(chord): keep the workerd runCounted probe row outside the bulk range
- `bdb151774` fix(chord): give the PendingOps fuzz tests room on loaded CI runners
- `92ab53602` feat: Durable storage adopt callback inside the commit, watch types in Chord, explicit watch retirement

**构建/依赖/CI（N/A）**（9）

- `7ef8680ab` fix: sync esbuild platform package dev flags in lockfile
- `b4c2e92ac` fix: remove vulnerable dependency chains
- `66c2fa3d7` fix(coding-agent): update undici to 8.12.3 for security advisories
- `0eea54f11` fix: normalize path separators in check-entry-graphs on Windows (#11399)
- `911fbab32` fix: unset all supported provider env vars in pi-test.sh --no-env (#11395)
- `a1836b06c` fix: handle npm 12 pack --json output in preview-packages
- `906e1302f` fix(coding-agent): build standalone binaries with Bun 1.4.4 (#11319)
- `74f468640` fix: trust robopipi[bot] in PR gate
- `2685de7a1` fix: update deps for security advisories

**只动测试（N/A）**（2）

- `068e972c7` fix(ai): remove adaptive thinking metadata test that duplicated generator logic
- `ed76cca69` fix(coding-agent): test forkFrom copied content instead of fs calls
**17 条「端口相关」的判定**

| 提交 | 判定 |
| --- | --- |
| `b5fc6dc84` stripAnsi 私有参数 CSI | ✅ 已移植（本节批次）：`AnsiText.CsiStart` 只在真 CSI 引导符后接受 `<`/`=`/`>`，`ESC=`/`ESC>` 后面的数字仍是正文；用例 `AnsiTextPrivateCsiTests` 照抄上游 8 条断言 |
| `018fdcf43` `pi.live` v3：存储里没 `turns` 的运行记 0 | ➖ 判定不适用：端口的 `pi.live` 是 v1，写 `run` 时一直带 `turns`，没有「v2 写了却没 turns」的历史数据可迁 |
| `f049787d9` 默认 openai 模型改 `gpt-6.1-sol` | ⏳ 归到 P2「目录数据」一起做（同一份 models 目录，单改一处会和目录不一致） |
| `a6a7bac86` 隐藏思考块空标签不渲染行 | ✅ 端口早已实现（懒加 Spacer + 隐藏且空标签直接 `continue`）；本轮补上上游两例 `AssistantMessage_RendersNothingForHiddenThinkingWithAnEmptyLabel` / `…KeepsLabelRowAndSpacingForHiddenThinkingWithALabel` |
| `7391dbb0f` 全屏标题栏显示模型 scope | ⏳ 待做（已核：端口仍只在 shell 打 `Model scope:`，全屏路径没有；86 行，动 `InteractiveMode` 生命周期） |
| `f6ee9ce9c` 工具描述里 codemode 输出的 union/多行摘要 | ⏳ 待做（已核：端口没有；74 行，随 `da412b039` 的类型推导一起做更省） |
| `6fed3cfb9` SDK 流在摘要时解析 auth | ⏳ 待做（已核：端口没有；142 行） |
| `c8ec752cf` 不支持的架构上跳过 fd/rg 下载 | ⏳ 待核（端口是否还有下载路径） |
| `c2760d32c` `pi update` 只在自管安装时点名包管理器 | ⏳ 待核 |
| `5884ee681` `/ir` 从 `.jsonl` URL 导入会话 | ⏳ 待做（已核：端口没有；新功能） |
| `a6bde34bb` schema 基准（Ajv/Zod 对比） | ➖ 开发工具，端口不需要 |
| `bb65e3e49` 从 `pi-codemode/declarations` 导入 | ➖ 纯导入整理，端口无对应模块边界 |
| `531340f10` 删 bedrock shim | ➖ 端口没有这些 shim |
| `1e42501bb` partial 成本测试里保住 pi.live 缓存 | ➖ 只动测试 |
| `c17be1e98` prompt-url-widget 跳过自定义消息 | ➖ examples/开发脚本 |
| `3953e9ef4` examples 的 todo renderResult | ➖ examples |
| `476a303c1` 去掉 managed npm ci 的 min-release-age | ➖ 发布/CI |
