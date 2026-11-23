# 上游 v1.6.0 迁移记录

本文记录 `v1.5.2..v1.6.0`（tag `v1.6.0` = `faac58588`，2026-11-20）之间、触及端口已迁移
package 的提交与发布说明的落地情况。逐提交、逐 CHANGELOG 条目的处置表见
[v1.6.0-disposition.md](./v1.6.0-disposition.md)。

## 1. 范围与口径

- 上游 `v1.5.2`（`b4afdddf4`）..`v1.6.0`（`faac58588`）共 **368 个提交**；其中触及
  `packages/{agent,ai,chord,codemode,coding-agent,durable,env,evals,mcp,schema,telemetry,tui}`
  的提交 **125 个**，其余 241 个在 `apps/p2` / `apps/p3`（端口从未移植这两个原型应用）。
- 端口基线为 `1.5.2.1`。本轮把发布版本提升到 **`1.6.0`**（`Directory.Build.props`、
  `src/Pi.CodingAgent/Assets/package.json` 与 CLI `--version` 三者一致）。
- 上一轮已经落地了 `v1.5.2..edfe6b2ef` 的 116 个提交（见
  [unreleased-disposition.md](./unreleased-disposition.md)）；本轮补齐 `edfe6b2ef..v1.6.0`
  的 19 个提交，并对 62 条 1.6.0 发布说明逐条复核。

## 2. 本轮新增落地

### 2.1 思考档位改走 `configuration_update`（`fcc33744b`，含 `60ac3bcc3` 的共享实现）

上游 `#10930`（v1.3.0）给 `openai-responses` 加了「档位写在会话里」的机制，`#12269`
（v1.6.0）把它扩到 `openai-codex-responses`。端口此前完全没有这套机制：`ConvertResponsesMessages`
没有 `configurationUpdateEffort`，`OpenAiResponses` 也没有 `supportsMidConvoEffort`。
本轮整条链路补齐：

| 落点 | 内容 |
|---|---|
| `src/Pi.Ai/Api/OpenAiResponsesShared.cs` | `ConvertResponsesMessagesOptions.ConfigurationUpdateEffort`；`PushConfigurationUpdate`；在同模型重放回复（带 `providerThinkingLevel`）前插入一项，末尾插入本次档位 |
| `src/Pi.Ai/Api/OpenAiResponses.cs` | `GetConfigurationUpdateEffort`（`supportsMidConvoEffort` + `thinkingLevelMap` 映射；ChatGPT 登录令牌时不发）；`BuildParams` 请求 `reasoning.effort` 固定 `medium`；`Stream` 把档位写进 `AssistantMessage.ProviderThinkingLevel` |
| `src/Pi.Ai/Api/OpenAiCodexResponses.cs` | `GetConfigurationUpdateEffort` / `ResolveReasoningEffort`；`BuildRequestBody` 增加可选 `configurationUpdateEffort`；`StreamSimple` 的 `off` 以 effort `none` 发送（`model.Reasoning` 为假时不发） |

用例：`tests/Pi.Ai.Tests/Api/V160ResponsesTests.cs`
（`ConfigurationUpdatesBracketReplayedRepliesAndEndWithTheRequestedLevel`、
`ConfigurationUpdateBeforeAReplayedReplyIsSkippedForAnotherModel`、
`ConfigurationUpdateEffortRequiresCompatFlagAndMapsThinkingLevel`、
`BuildParamsSendsMediumEffortWithATrailingConfigurationUpdate`、
`CodexConfigurationUpdateEffortHonorsCompatAndOff`、
`CodexBuildRequestBodySendsFixedEffortAndConfigurationUpdates`）。

### 2.2 Codex 转发 `max_output_tokens`（`fcc33744b`）

上游在同一提交修掉「`openai-codex` 忽略模型 `maxTokens`」：`max_output_tokens` 按
`compat.supportsMaxOutputTokens` 转发，并夹到 Responses API 的下限 16（`#6265`）。
端口 `OpenAiCodexResponses.BuildRequestBody` 已补上，用例
`CodexForwardsMaxOutputTokensAboveTheResponsesMinimum`（8 → 16、4096 原样、
`supportsMaxOutputTokens: false` 时不发）。

### 2.3 OpenAI 扣减 reasoning 的通报（`03de604b8`）

GPT-6.2 起 Responses API 会在 `input_transformations` 里报告被扣掉的 reasoning
（`prefix_mismatch`、跨组织/终端用户/模型等）。端口新增：

| 落点 | 内容 |
|---|---|
| `OpenAiResponsesShared.FinalizeResponse` | `AppendReasoningRemovals`：过滤 `type=reasoning && action=removed && reason!=turn_boundary`，写 `openai_input_transformations` 诊断（OpenAI / Azure / Codex 共用该流处理） |
| `InteractiveModeDiagnostics` | `GetDroppedReasoning` / `ReasoningDropNoticeText`：Anthropic 与 OpenAI 共用一套「Provider + 单复数 + 原因」措辞；`InteractiveMode.MaybeShowThinkingDropNotice` 改为按诊断类型比较上一条回复 |

用例：`V160ResponsesTests.CompletedResponseRecordsReasoningRemovalsButNotTurnBoundary`、
`InteractiveModeDiagnosticsTests.DroppedReasoning*`（3 项）。

### 2.4 `openai-completions` 的 instruction 角色默认值（`b82477b7b`）

`developer` 只对 `provider == "openai"` 或 baseUrl 落在 `api.openai.com` 时默认开启，
其余宿主改用所有 chat 模板都接受的 `system`（`#10741`；Groq 的 Qwen 模板会以
`400 Unexpected message role` 拒绝 `developer`）。

落点：`OpenAiCompletions.DetectCompat`（`isOpenAi`）。目录里 24 个文件、共 ~570 处显式
`supportsDeveloperRole: false` 在新默认下与缺省值等价（无一处 `: true`），因此数据不动；
`LlamaProvider` 里硬编码的那一行按上游删除。

用例：`DeveloperRoleDefaultsToOpenAiOnly`；既有
`OpenAiCompletionsWireTests.AppliesAnthropicCacheMarkersToSystemToolsAndLastMessage`
与 `DetectsCompatDefaultsForKnownProviders` 按新语义更新（OpenRouter → `system`）。

### 2.5 工具预览的语法高亮前景色（`562da75f9`）

无 scope 的普通文本改用 Pi 的 `text` 颜色（`ThemeHelpers.BuildCliHighlightTheme` 的
`default` 项），终端主题与 Pi 主题反差大时工具卡片上的代码不再不可读（`#11092` / `#12347`）。

用例：`ThemeHelpersTests.HighlightCode_AppliesTheThemeForegroundToUnscopedText`；
既有 scope 映射用例按「空格也吃 `text`」更新。

### 2.6 llama.cpp 默认端点（`#12001`）

`LlamaProvider.DefaultServerUrl` 从 `http://127.0.0.1:8080` 改为
`http://127.0.0.1:9931`（llama-server 当前默认端口），随包文档
`Assets/docs/llama-cpp.md` 同步。上一轮台账把 `b3c2e4a6b` 记为「端口无默认端点」，与代码
不符，本轮更正。

### 2.7 全屏滚动的 view-only 帧（`b7bb7ca90`，`#11968`）

上游把全屏滚动从「每帧重渲染整条 transcript」改成「复用上一帧组件输出」。端口此前只有
`RequestRender(bool force)`，`Layout.RenderLayoutFrame` 每帧新建 `LayoutContext` 并重渲染
全部内容（20k 行 2.5–5.5 ms/帧）。本轮按同样语义实现：

| 落点 | 内容 |
|---|---|
| `src/Pi.Tui/Layout.cs` | `LayoutRenderCache`（组件 → 宽度 → 行）跨帧缓存；`RenderLayoutFrame` 接受 `requestViewRender` 与 `cache`；`ScrollView.UpdateLayout` 改用 view 回调 |
| `src/Pi.Tui/Tui.cs` | `RequestRender` 先 `OnRenderInvalidated()`（子类清缓存）；新增 `RequestViewRender()`，只安排渲染、不清缓存 |
| `src/Pi.Tui/TuiAltScreen.cs` | 持有缓存并在尺寸变化时清空；滚动/滚轮/`ScrollToPrompt` 走 view 回调；**渲染期间被失效**（组件在 `Render` 里改状态）时用空缓存重跑第二遍 |

用例：`tests/Pi.Tui.Tests/AltScreenViewFrameTests.cs`（5 项）——滚动帧不重渲染、滚轮帧不重渲染、
内容帧重渲染、尺寸变化清缓存、渲染中失效必须显示新内容（防止复用旧缓存）。

## 3. 已在上一轮落地、本轮复核通过的 1.6.0 内容

模型目录（GPT-6.2 Astra/Sol、`gpt-6.2-sol` 默认、Qwen Token Plan 网关与套餐模型、
Together Kimi K3 1M、Copilot strict/reasoning）、Codex 系统提示改为首个 developer 项、
find 路径语义、Anthropic 消息线程、`/copy`、`pi.exec` 信号退出码、MCP 恢复、会话替换诊断、
RPC/`--mode json` 的 `session_start`、`/resume` 选中、git/npm 输出捕获、TUI 图像与
SIGTSTP 等，均沿用 [unreleased-disposition.md](./unreleased-disposition.md) 的处置与用例；
本轮用 `docs/v1.6.0-disposition.md` 的 62 条发布说明逐条复核，未发现新的功能缺口。

## 4. 排除项（端口无对应宿主/消费方）

| 上游 | 内容 | 依据 |
|---|---|---|
| `434dc5e4d` | `pullSession` 读取后未消费的 remote body 要 `return()` 关闭 | 端口 `git ls-files src/Pi.Chord` 无 `sync/`；唯一消费方是 `apps/p2`（`git grep -ln 'pullSession\|uploadSession\|foldSession' v1.6.0 -- packages apps` 只命中 p2 与 chord 自身） |
| `f472efdc1` | outbox 关闭后重启的备份改为整份 archive 续传 | 同上：端口没有 chord store/outbox，也没有备份守护进程 |
| `4ccf44a0f` | 补写 1.6.0 的 changelog 条目 | 文档；端口 CHANGELOG 已按本表重写 |
| `5976a1586` 等 7 个 `docs(p3)` | p3 指南/决策 | `apps/p3` 未移植 |
| `b9d482031` | p2 的 MCP sheet | `apps/p2` 未移植 |
| `ff8970889` | chord 测试去 flake | 测试 only；端口无该测试 |
| `faac58588` | Release v1.6.0（改各包版本） | 版本元数据；端口按本文件 §1 提升到 `1.6.0` |

发布说明里属于 JS/npm 形态的两条同样记 ➖：`undici` 安全更新（端口用 `HttpClient`）、
standalone binary 的 npm 依赖解析（端口是 .NET 程序集加载）。`render()` 返回
`readonly string[]` 是 TS 组件契约；端口 `IComponent.Render(int)` 返回 `string[]`，
`Layout.RenderCached` 已按「组件 + 宽度」缓存（`src/Pi.Tui/Layout.cs:104`），不存在就地
修改共享数组的契约。

## 5. 验证

- **构建**：`dotnet build Pi.slnx` 在 Debug / Release × net8.0 / net9.0 / net10.0 六种组合下
  **0 警告 0 错误**。
- **门禁**：`bash scripts/gate-tests.sh Debug` 与 `Release`（net10.0）各 **11 个工程全绿**；
  `GATE_FRAMEWORK=net8.0`（net8.0 运行时实跑）与 `GATE_FRAMEWORK=net9.0`（本机无 net9 运行时，
  用 `DOTNET_ROLL_FORWARD=LatestMajor` 在 net10 运行时上跑）同样 11 个工程全绿。
- **门禁脚本修正**：`dotnet test -f <tfm> --no-build` 在本仓库多目标（含 net11）布局下会
  **静默空跑并返回 0**——`GATE_FRAMEWORK=net8.0` 之前看起来全绿其实一条没跑。`gate-tests.sh`
  改为直接运行测试工程产出的 MTP 宿主（`tests/<P>/bin/<cfg>/<tfm>/<P>[.exe]`），保留
  `dotnet test` 作为宿主缺失时的回退。net8 门禁因此暴露出 `RadiusRelayHostTests` 的
  `Collection was modified`（宿主线程写状态列表、测试线程枚举）：测试夹具改为加锁快照。
- **测试套件**：门禁 11 工程之外，`Pi.Tui.Tests` **23,987** 项全绿（含 5 项 view-only 帧用例），
  `Pi.Env.Tests` 50 通过 / 4 跳过。
- **Native AOT**：`bash scripts/publish-aot.sh` 产出 `artifacts/aot/win-x64/pi.exe`
  （68.7 MB），`bash scripts/verify-aot.sh` **52/52 通过**。发布路径新增
  `TargetLatestRuntimePatch=true`：SDK 自带补丁版的 `Microsoft.DotNet.ILCompiler` 触发了
  NuGet 审计告警 GHSA-p3jp-9v83-76w9，不压告警而是改取已修补的 runtime pack / ILCompiler；
  产物仍是自包含原生映像，运行时不依赖该补丁。
