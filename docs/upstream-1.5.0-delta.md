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
| `8bc94b19f` | **自定义自更新命令**：`PI_SELF_UPDATE_COMMAND` / `selfUpdateCommand`，`{packageName}`/`{version}`/`{installSpec}` 占位 | 无 | 🔧 P3 |
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
| `e3da248ea` | Windows 上 `git clean` 失败时仍继续装依赖并下次重试 | 🔧 P3（待核） |
| `c466a3595` / `36be9c612` / `c60de42e9` | `pi.exec`：256 MiB 上限 + `truncated`；UTF-8 跨块解码；spawn 错误保留 stderr | 🔧 P2（`core/exec.ts` 182 行） |
| `6454d07ec` | WSL 深目录下从 `/` 启动 `powershell.exe`/`explorer.exe`；外部编辑器失败要报状态 | ➖/🔧（Windows 宿主，待核） |
| `5fb9a0b11` | 安装包时屏蔽 npm audit/fund 输出 | ➖ npm 专有 |
| `fecc19e4e` | `grep` 上下文行号在含孤立 CR 的文件里与 ripgrep 对齐 | 🔧 P2 |
| `984cf6643` | `--model <provider>/<id>` 支持 provider id 含斜杠 | 🔧 P3 |
| `8ddc44ec8` | fork 会话单 fd 写入（打开文件昂贵的文件系统） | 🔧 P2（待核） |
| `8c03fef4d` | `/resume` 搜索：名称/目录模糊，ID 与消息正文只做子串 | 🔧 P3 |
| `8c03fef4d` | `/model` 搜索排序：连续匹配优先 | 🔧 P3 |
| `be15fe3c1` | `pi mcp login/logout -l` | 🔧 P5 |
| `908aa7b74` | MCP OAuth 默认用 Client ID Metadata Document，否则动态注册（`oauth.clientRegistration: auto/dcr`） | 🔧 P5 |
| `1c720d285` / `9359adf0c` | HTML 导出：数千条目不空白；包含 system prompt/tools | 🔧 P3（待核） |
| `27cc6bfc0` | macOS 先停进程组再 kill | ➖（平台语义，C# 侧另行处理） |

### 2.4 tui（P4）

| 上游提交 | 变更 | 端口现状 | 处置 |
| --- | --- | --- | --- |
| `fa1685152` | 裸 URL 在 CJK 标点处截断 | 本轮已落地 | ✅ |
| `8a2ebd385` | 嵌套引用不再指数膨胀（`width < 3` 直接渲染子节点） | 已有 | ✅ |
| `3bae85274` | `tui.select.pageUp/pageDown` 作用于所有列表；全屏下翻页键翻列表而不是滚 transcript；`capturesViewportInput` | 无 | 🔧 P4 |
| `2ce9d527d` | 换行边界：带样式的词不再多出前导空格；删除线不渗进 padding | 无 | 🔧 P4 |
| `deb12d78e` | Kitty 协议 F1–F12（含修饰键）；F13+ 不插入私用区字符 | 未核 | 🔧 P4 |
| `86dc686cf` | Apple Terminal 的 `ESC`+Return 且本地 Shift 按下时按 Shift+Enter（插换行） | 未核 | 🔧 P4 |
| `19ce8059c` | 常规模式不再写 OSC 133 提示符标记 | 未核 | 🔧 P4 |
| `0f21a3319` | Markdown 按顶层块缓存渲染结果；代码高亮复用 | 无 | 🔧 P4 |
| `cd1ea342a` | 显示公式超宽时回退原始 LaTeX | 未核 | 🔧 P4 |
| `4464442f9` | 全屏下滚出视口的 Kitty 图片按行裁剪 | 未核 | 🔧 P4 |
| `ff6896471` | 全屏双击选词按 iTerm2 的 word 字符集（`~ . \ +` 不再切词） | 无 | 🔧 P4 |
| `f09d01b85` | Orca 终端识别（truecolor / hyperlink / `ORCA_IMAGE_PROTOCOL=kitty`） | 未核 | 🔧 P4 |
| `89c921d8b` | 终端标题/控制序列注入加固；`stripAnsi` 去掉带 `<=>` 前缀的私有 CSI | 部分 | 🔧 P4 |
| `1c8cb1781` | 图片行搜索按最高注册图片限界 | 未核 | 🔧 P4 |

### 2.5 durable（P6）

| 上游提交 | 变更 | 处置 |
| --- | --- | --- |
| `f6a9db1d8` | 每会话 turn limit | ✅ 本批（持久化 `runFailure` 记录随 `f3255de76` 落地） |
| `fb524dd2e` | continue 提交 | ✅ 本批 |
| `7e15f9601` | run-end tasks + `afterRun` hook | 🔧 P6 |
| `65fba65b2` | hook/task 的 entry reads | ✅ 本批 |
| `cc9c52310` | `submit(tx, draft)` 在调用方提交内接纳 | 🔧 P6 |
| `6a2825113` | `whenIdle` 拒绝空闲会话的输入 | ✅ 本批 |
| `1c6928b30` | 未提供的工具调用按 tool task 跑 | ✅ 本批 |
| `87eff524f` | `afterTool` 在每个 tool task 结果上跑 | ✅ 本批 |
| `d280e1ffa` | `beforeProviderRequest` 生成钩子 | ✅ 本批（端口原有 `BeforeRequest` 是消息层，这是请求体层，两者并存） |
| `f3255de76` | 窗口化会话视图 + `ConversationTranscript` + `runFailure`；删除 `watchEvents` | 🟡 `runFailure` 已落地（`15d315f`，见 §4.11）；窗口化视图、`ConversationTranscript`、删 `watchEvents` 未做 |
| `044148392` | Code Mode 脚本 `read` 返回图片块 | ➖ durable 的 Code Mode 按 1.4.0 的决定不采纳（见 §4.8） |
| `be14d9732` | 只读打开 Harness | ✅ 本批 |
| `e5c6182ba` / `6c2fd73b0` / `da412b039` | 分组提交、批量 `Storage.commit`、节流提交延迟落盘、`appendText` | 🔧 P6 |
| `35dfa7573` | 空闲检查与 `wake()` 不再每次提交全扫 | 🔧 P6 |
| `c7372af42` / `79006f061` | 工具搜索排序与 CJK 分词 | 🔧 P5 |
| `d5b3de22d` | assistant 消息记录请求的 thinking level | ✅ 本批 |
| `38ff99177` | 工具交接后继续运行 | 🔧 P6 |
| `b3a29f111` | 重启循环可见地结束；宿主死亡计入一次 generation 尝试 | 🔧 P6 |

### 2.6 env / mcp / telemetry / evals（P5）

| 上游提交 | 变更 | 处置 |
| --- | --- | --- |
| `102d12871` | 变更列表超过帧上限时报 watch overflow | 🔧 P5 |
| `b6d5c2ecd` | daemon 被终止时杀掉它启动的命令 | 🔧 P5 |
| `92c1da1f6` | 辅助线程起不来时 `exec`/`pty` 返回 `spawn_error` | 🔧 P5 |
| `479d6e5e2` | 启动错误带 stderr 尾部（20 行 / 2000 字符，去转义） | 🔧 P5 |
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

**仍留在 P3 名下**（未做，见 §3 验收口径）：`e3da248ea`（Windows `git clean` 失败后继续装依赖）、
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
| MCP OAuth 优先用 Client ID Metadata Document，否则动态注册（`oauth.clientRegistration: auto/dcr`） | `908aa7b74` | `extensions/mcp/oauth.ts` ~59 行 + `core/mcp-servers.ts` | ⏳ 未做 |
| 工具搜索：名称权重高于描述、命中词数优先、查询即名称时置顶、CJK 分词；durable 侧同规则 | `cc44fa216` / `e033f255f` / `ae823bc0b` / `79006f061` / `c7372af42` | `extensions/tool-search/tool.ts` + `durable/src/search.ts` | ⏳ 未做（`Tokenize` 目前只切 ASCII，中日韩文本会被整段丢弃） |

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

**端口差异**：上游在 abort 时按 `restarts < restartLimit - 1` 豁免钩子（防「钩子就是打断元凶」的
死循环）；端口的工具重启计数（`b3a29f111`）尚未移植，钩子总是跑——移植重启计数时必须补上这条豁免，
否则一个改写结果的钩子可能让工具永远跑不完。

### 4.14 P6 第八批：未提供的工具调用按工具任务跑（提交见 git log）

| 变更 | 上游 | 落点 | 用例 |
| --- | --- | --- | --- |
| 未提供的调用进轮：并行轮直接起任务、顺序轮按序进 pending，槽标 `unoffered`；由工具任务结算 `tool_unavailable`（过 afterTool），abort 时同因 | `1c6928b30` | `ToolTaskInput.Unoffered` / `ToolSlot.Unoffered`（JSON 往返兼容旧形状）、`CreateToolTaskAsync`、`StartToolRoundAsync`、`ToolsPhaseAsync`、`ToolTask.CallPhaseAsync` / `AbortAsync`；`pi.tool` 1→3、`pi.generation` 1→2 + 透传 Migrate | `HarnessUnofferedToolTests` 1 例 |

**顺带修正的一处行为差**：旧端口在轮启动提交里直接给未提供的调用落结果，这既绕过 afterTool，也让 abort
把「无论如何都不可能跑过的调用」说成 aborted。上游特意让它们进任务——两条路径（结果、abort）都说
`tool_unavailable`。
