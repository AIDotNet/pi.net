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
2. **chord v2 不跟**。`packages/chord/src/next/*` 是实验实现，`package.json` 的 `exports` 未导出（v1 仍是 `index/context/delta/json/watch/output/sqlite/...`）。v1 只需跟 `pi-schema` 切换与 3 个运行时修复。
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
| `f715a15c1` | 流式工具调用参数**增量解析**（不再每个 delta 重解析累计串） | 只有 `JsonParse` 全量修复式解析 | 🔧 P2 |
| `363ad78bf` | GPT-6.1 Terra（OpenAI / Azure Responses / Codex，tool search、会话中改 thinking、`off`） | 默认模型已是 `gpt-6.1-sol`；Terra 的能力位未核 | 🔧 P2（核对） |
| `2e0f175bd` | provider 定义重构 + 目录升到 models.dev v2 | 目录为 1.4.0 期快照（42 家 / 1713 条） | 🔧 P2（数据，需生成脚本） |
| `7314819f0` + `a7b94a03f` | Gemini 工具 schema 的 `const` 改单值 `enum`，且不改写 defaults/examples/enum 里的字面量 | 未核 | 🔧 P2 |
| `1de2d7ade` | Kimi K3.1 Pro 按 K3 家族发请求（reasoning effort、保留 thinking、中途加工具、K3 定价） | 有 `kimi-k3` 条目，家族判定未核 | 🔧 P2 |
| `842c73170` | Bedrock strict 工具超限时重试一次（preferred 放宽、required 保持 strict） | 未核 | 🔧 P2 |
| `81d6759fc` | GPT-5.4/5.5 工具声明放顶层（修 `Missing namespace for function_call`） | 未核 | 🔧 P2 |
| `177e2ad7e` | OpenCode Zen/Go 用各自协议端点（OpenAI/Anthropic/Google/System One） | 未核 | 🔧 P2 |
| `44f6851b7` | OpenRouter `~anthropic/claude-*-latest` 走 Anthropic Messages，保住 thinking | 未核 | 🔧 P2 |
| `e3bc05e87` | Anthropic 逐消息 effort 时顶层固定发 `low` | 未核 | 🔧 P2 |
| `3bc6eebac` | Anthropic system 更新要越过被丢弃的空/纯推理 assistant 消息 | 未核 | 🔧 P2 |
| `89c921d8b` | 不可信 URL / 终端文本 / 流式数据的加固 | 部分（MCP OAuth 已跟） | 🔧 P2 |
| `7c22a93d0` | `Usage.cost.totalFromProvider` | 已有 | ✅ |
| `cae8d69e5` | OpenRouter 上报成本 | 已有（`2820568`） | ✅ |
| `d3398f826` | LiteLLM `ContextWindowExceededError` 判为溢出 | 本轮已加模式 | ✅ |
| `c719d5b08` | Zhipu `1308` 不重试 | 本轮已加模式 | ✅ |
| `f3fa8bd92` | faux 缓存写入不再重复计入 input | 已有 | ✅ |
| `14c811841` | pi-messages 终端事件带 `responseModel` | 已有 | ✅ |

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
| `f6a9db1d8` | 每会话 turn limit | 🔧 P6 |
| `fb524dd2e` | continue 提交 | 🔧 P6 |
| `7e15f9601` | run-end tasks + `afterRun` hook | 🔧 P6 |
| `65fba65b2` | hook/task 的 entry reads | 🔧 P6 |
| `cc9c52310` | `submit(tx, draft)` 在调用方提交内接纳 | 🔧 P6 |
| `6a2825113` | `whenIdle` 拒绝空闲会话的输入 | 🔧 P6 |
| `1c6928b30` | 未提供的工具调用按 tool task 跑 | 🔧 P6 |
| `87eff524f` | `afterTool` 在每个 tool task 结果上跑 | 🔧 P6 |
| `d280e1ffa` | `beforeProviderRequest` 生成钩子 | 🔧 P6（端口已有同名符号，需核对是否同一钩子） |
| `f3255de76` | 窗口化会话视图 + `ConversationTranscript` + `runFailure`；删除 `watchEvents` | 🔧 P6 |
| `044148392` | Code Mode 脚本 `read` 返回图片块 | 🔧 P6 |
| `be14d9732` | 只读打开 Harness | 🔧 P6 |
| `e5c6182ba` / `6c2fd73b0` / `da412b039` | 分组提交、批量 `Storage.commit`、节流提交延迟落盘、`appendText` | 🔧 P6 |
| `35dfa7573` | 空闲检查与 `wake()` 不再每次提交全扫 | 🔧 P6 |
| `c7372af42` / `79006f061` | 工具搜索排序与 CJK 分词 | 🔧 P5 |
| `d5b3de22d` | assistant 消息记录请求的 thinking level | 🔧 P6 |
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
| `be039fb31` | Chord v1 与 v2 切到 pi-schema，删 TypeBox | 🔧 P7（仅 v1 的 schema 构造点） |
| `50d9f823e` | 工具输出里整段 ANSI 序列要删干净 | 🔧 P7 |
| `c09c98d7d` | relay 消失后仍接纳已路由的 host | 🔧 P7 |
| `db14038bd` | Node SQLite 驱动预编译语句缓存加上界 | 🔧 P7 |
| `8025c414d` | `SqliteExecutor.runCounted` 返回影响行数 | 🔧 P7 |
| `d0aa218cb` | Delta tracker 容器拷贝降本 | 🔧 P7 |
| `a2fd25f56` / `5e106ab1c` / `42cb822f1` | `Watchable` / `NativeWatchable` / 共享 watch handle | 🔧 P7 |
| `8b0caefc2` / `d8d99950a` / `da412b039` | 采纳时发布提交、按地址跟随的 watch、同步 observer、`PendingOps` | 🔧 P7 |

## 3. 批次计划

| 批次 | 范围 | 规模 | 验收 |
| --- | --- | --- | --- |
| **P0** | pi-schema 校验/转换/文案对齐（`Validation.cs` 重写 + 编译缓存 + 悬空 `$ref`） | ~1,500 行 | 已过：368 条上游语料差分全绿（见 §4.2） |
| **P1** | 本轮已落地的小修集（见 §4） | ~80 行 | 已过 |
| **P2** | ai wire 层：增量解析、Gemini const、Kimi K3、Bedrock strict、GPT-5.x 顶层工具、OpenCode 端点、Anthropic 两条、exec 三条、grep CR、模型目录数据 | ~1,200 行 | 每项一个 SSE/纯函数回归；目录生成脚本 diff 为空 |
| **P3** | coding-agent：扩展 changelog、自更新命令、device-code 确认、`blocked`、`keyHint` 注入、trust/路径大小写/写盘一致性/分支摘要/启动容错等 ~18 项 | ~900 行 | `InteractiveModeTests` + 新增定向用例；全量 0 失败 |
| **P4** | tui：翻页键与 `capturesViewportInput`、换行/删除线、Kitty F 键、OSC 133、块级渲染缓存、LaTeX 回退、图片裁剪、双击词字符、Orca | ~700 行 | `Pi.Tui.Tests` 全量 + 上游用例照抄 |
| **P5** | env 4 项 + mcp 2 项 + 工具搜索排序/CJK 分词 | ~500 行 | `Pi.Env.Tests` / `Pi.Mcp.Tests` / 搜索排序向量 |
| **P6** | durable：turn limit、continue、afterRun、entry reads、submit(tx)、whenIdle、tool task、窗口化视图、只读 Harness、分组提交 | ~2,000 行 | `Pi.Durable.Tests` 全量 + 新 API 契约用例 |
| **P7** | chord v1：pi-schema 构造点、ANSI 清理、watch 家族 | ~600 行 | `Pi.Chord.Tests` 全量 |

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
