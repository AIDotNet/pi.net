# 上游版本核查与 1.4.0 变更移植评估

> 核查日期：2026-10-22 · 参照仓库：`D:\AI\参考项目\pi` · 端口基线：上游 `v1.0.4`（`eb326d265`）

## 1. 版本核查结论

上游存在 4 个高于 `1.0.4` 的稳定版本，**最新稳定版为 `v1.4.0`**：

| 标签 | 发布时间（CEST） | 说明 |
| --- | --- | --- |
| `v1.0.4` | 2026-10-05 21:22 | 端口当前基线 |
| `v1.1.0` | 2026-10-07 23:33 | 稳定 |
| `v1.2.0` | 2026-10-12 00:46 | 稳定 |
| `v1.3.0` | 2026-10-15 20:57 | 稳定 |
| `v1.4.0` | 2026-10-22 00:29 | **最新稳定版** |

`main` 在 `v1.4.0` 之后还有 6 个未发布提交（HEAD `476a303c1`，含 `feat(durable)` 破坏性改动、
`feat(coding-agent)` 的 `/ir` 导入等）。这些属于下一轮未定稿内容，**本次不作为移植目标**，
只在待办中列为观察项。

区间规模（`v1.0.4..v1.4.0`）：**278 提交 / 76 个 feat / 156 个 fix / 1005 个文件 / +80,853 −44,109 行**。
按包分布：coding-agent 294 文件、durable 171、ai 142、chord 118、tui 35、codemode 32、
server 29、env 24、client 19、telemetry 18、protocol 17、mcp 15、agent 9。

## 2. 重要发现：端口基线已漂移

端口名义基线是 `v1.0.4`，但代码中已存在若干 **1.1.0 / 1.4.0 才引入**的特性，
说明此前的移植参照过晚于 `v1.0.4` 的提交：

| 特性 | 上游引入 | 端口状态 | 证据 |
| --- | --- | --- | --- |
| `durationMs`（AssistantMessage / ToolResultMessage / `tool_execution_end`） | 1.1.0 (#10549) | 已存在 | `Pi.Ai/Types/Messages.cs`、`Pi.Agent/Types/AgentTypes.cs` |
| `outputPad`（工具渲染上下文 + 设置） | 1.1.0 (#10557) | 已存在 | `ToolDefinition.cs`、`ExtensionEvents.cs`、`Settings.cs` |
| `--tools` 的 `+name`/`-name` 增量语义 | 1.1.0 | 已存在 | `AgentSession.cs`、`AgentSessionBuild.cs` |
| 输出预算按 3.5 字符/token 估算 | 1.1.0 (#10497) | 已存在 | `Pi.Ai/Utils/Estimate.cs`（`CharsPerToken = 3.5`） |
| OSC 52 兜底（SSH/MOSH 探测） | 1.0.4 起有 | 已存在 | `Utils/Clipboard.cs` |

结论：本次**不重复移植**上述项，只补充它们缺少的增量（例如 OSC 52 的 `PI_CLIPBOARD_OSC52` 强制开关）。

## 3. 变更清单与影响评估（按包）

标注：**已移植**＝端口此前已有；**本次移植**＝本轮落地；**待办**＝适用但本轮未做；
**不适用**＝与 C# 端口形态无关（Bun/Node 运行时、发布流水线等）。

### 3.1 `packages/ai`

| 变更 | 版本 | 影响 | 结论 |
| --- | --- | --- | --- |
| 工具结果空白输出按空处理 | 1.4.0 #8720 | 三家 provider 的 400 风险 | 本次移植 |
| 非法 `\u` 转义保留为字面量 | 1.3.0 #10868 | 工具参数丢字段 | 本次移植 |
| 重试判定的状态码需独立出现 | 1.4.0 #11057 | 请求 id/字节数误触发重试 | 本次移植 |
| 新增瞬时故障文案（`unable to forward upstream`、`upstream_stream_read_error`、`stream disconnected before completion` 等） | 1.3.0–1.4.0 | 网关故障不再中断回合 | 本次移植 |
| ChatGPT Business 工作区限额不可重试 | 1.4.0 | 避免无意义重试 | 本次移植 |
| 畸形最终参数上报 `argumentsError`（不再保留解析前缀） | 1.3.0 #10897 | 5 个 API 实现 + 校验层 + agent 预检 | 本次移植（第二批） |
| `personalizeOpenRouterCatalog()`、`RefreshModelsContext.baseUrl`、按 key 的模型列表 | 1.2.0 #10353 | 新增 `openrouter-api.ts`，重写远端目录 provider | 本次移植（第六批） |
| 客户端身份 `identity` / `User-Agent` 格式重构 | 1.4.0 | 破坏性：`telemetryContext`→`telemetry`、`getPiUserAgent` 移除 | 本次移植（第五批） |
| `resolveTranscriptTools` 由布尔改为 `TranscriptToolSupport` | 1.4.0 | 破坏性 API | 待办 |
| System One 分类器图像输入、`compat`、llama.cpp 决策模型 | 1.1.0–1.4.0 | codemode + 分类器链路 | 待办 |
| 模型目录更新（Claude Haiku 5.5、MiniMax-M3.1-Flash、Grok `xhigh` 等） | 1.1.0–1.4.0 | 纯数据 | 待办（随目录同步脚本） |
| Anthropic `strict:"prefer"` 重试、Mistral 超时范围 | 1.2.0 | provider 兼容细节 | 待办（OpenRouter 负价归零已随第六批完成） |
| OpenAI Responses `additional_tools` 前缀缓存 | 1.4.0 #10940 | 缓存命中率 | 待办 |

### 3.2 `packages/agent`

| 变更 | 版本 | 影响 | 结论 |
| --- | --- | --- | --- |
| `AgentOptions.identity` / `Agent.identity` | 1.4.0 | 需 pi-ai `ClientIdentity` 先落地 | 本次移植（第五批，含 `DEFAULT_AGENT_IDENTITY`） |
| `durationMs` 全链路 | 1.1.0 | — | 已移植 |
| `prepareToolCall` 内接入 `validateToolArguments`（顺带把 `argumentsError` 短路接上） | 1.3.0 #10897 | 工具拿到 coerce 后参数；畸形/不合 schema 的调用以 isError 结果回模型 | 本次移植（第二批） |

### 3.3 `packages/coding-agent`

| 变更 | 版本 | 影响 | 结论 |
| --- | --- | --- | --- |
| grep 工具加 `--no-config` | 1.4.0 #11203 | `RIPGREP_CONFIG_PATH` 静默改变搜索语义 | 本次移植 |
| `mcp.json` 接受 UTF-8 BOM | 1.4.0 #11191 | BOM 导致整份配置被跳过 | 本次移植 |
| 资源设置里的 `~` 展开 | 1.2.0 #10765 | `+~/x`、`!~/skills/pdf` 永不匹配 | 本次移植 |
| 提示模板保留空引号参数 | 1.3.0 #10867 | 位置参数错位、默认值失效 | 本次移植 |
| 打印模式截断时告警并退出 1 | 1.2.0 #10799 | 脚本把截断当成功 | 本次移植 |
| `PI_CLIPBOARD_OSC52=1` 强制 OSC 52 | 1.2.0 #10826 | 多路复用器/远程 IDE 复制失效 | 本次移植 |
| 摘要不再按 `reserveTokens` 折算输出上限 | 1.4.0 #9512 | 思考占满预算时压缩直接失败 | 本次移植 |
| `sessionShareDestination` 设置 | 1.3.0 #10870 | 新增设置项 + `/share` 分支（14 文件） | 本次移植（第七批） |
| 恢复会话时恢复活动工具集 | 1.3.0 #10785 | 影响 `--tools` 与 resume 交互 | 待办 |
| `--model` 的 provider 选择策略统一 | 1.3.0 #10160 | 663 行重写，风险较高 | 待办 |
| codemode worker 排队与 `PI_CODEMODE_MAX_WORKERS` | 1.4.0 #11184 | 并发脚本从失败改为等待 | 待办 |
| `reachesTools`、命名空间 `aliases` | 1.2.0 | 工具可达性与 MCP 等待 | 待办 |
| `before_agent_start` 覆盖自定义触发轮次（破坏性） | 1.2.0 #10267 | 事件语义变更 | 待办 |
| `serializeConversation` 省略 thinking | 1.2.0 | 摘要请求体积 | 待办 |
| 续传/截断工具调用导致的孤儿结果修复 | 1.4.0 #11227 | 会话后续请求被 provider 拒绝 | 待办 |
| `agent_settled` 增加 `aborted` | 1.1.0 #10607 | 事件字段 | 待办 |
| 程序状态上报（OSC 7501） | 1.1.0 #10607 | 终端集成 | 待办 |
| Windows shell 探测补充（Store 别名、非 `C:\Windows`） | 1.1.0–1.4.0 #9501/#9504 | 端口已有同类逻辑，需逐条比对 | 待办 |
| 移除 `@earendil-works/chord` 依赖、移除实验性 server/client/plugin 栈 | 1.2.0 | 上游删除，端口保留自有实现 | 不适用 |

### 3.4 `packages/tui`

| 变更 | 版本 | 影响 | 结论 |
| --- | --- | --- | --- |
| 未闭合 `$$` 不再吞掉 Markdown 表格 | 1.4.0 #11159 | 流式输出时表格消失 | 本次移植 |
| CJK 标点相邻的粗体/斜体渲染（`marked-cjk-friendly`） | 1.4.0 #10730 | 中文排版可见缺陷 | 待办（见 §6） |
| Rex 终端支持（OSC 8 / truecolor / 内联图） | 1.4.0 | 终端能力探测 | 待办 |
| 退出时终端模式未复原、重复对话框、主题 schema 校验等 | 1.2.0–1.4.0 | 多为 TUI 细节修复 | 待办 |
| `showHardwareCursor` 语义变化 | 1.2.0 | 光标绘制 | 待办 |

### 3.5 其他包

- **durable（1.3.0–1.4.0，171 文件）**：`Storage.commit(writes, adopt, context)` 的 `adopt(seq)`
  回调、`mintId()` 契约收窄 —— **已移植（第三批）**：存储负责在批次持久后、下一次提交开始前
  同步采纳一次，Session 不再自己 `tx.adopt(seq)`，改为发现「已提交却没采纳」即毒化。
  其余 169 个文件的改动（sleeping state、crash-loop guard、IndexedDB、search、images 等）仍待办。
- **durable 观察面（同一提交 92ab53602）**：移除 `Session.documentState()` /
  `Conversation.viewState()` / `Harness.taskGraph()`，`WatchHandle`/`WatchEnd` 迁往 chord，
  结束原因为 `ended`（原 `session_closed`）/ `failed`（原 `session_failed`）。
  端口仍保留旧 API 且被 `Pi.CodingAgent.Experimental` 直接使用 —— 需与观察/退休重构整批做，
  单独改名只会得到半套实现。
- **chord（1.3.0）**：`traceIn(options, fn, context)` 参数顺序变更 —— **暂不适用**：端口 chord
  尚无 `telemetry` 成员与 `TelemetrySpan`，`traceIn` 本身不存在；与 telemetry 一起落地。
- **telemetry（1.4.0）**：`telemetryContext`→`telemetry?: TelemetrySpan` —— **暂不适用**：端口 ai 层
  没有 `observeModelRequest` 观察器（`LazyStream` 无 observer 参数），无从改名的选项；identity 已独立落地。
- **mcp（1.2.0–1.4.0，4 提交）**：`structuredContent` 可为任意 JSON 值（#10416）与
  「无元数据时拒绝 `iss`」（c5f5b3282）、「取消的请求不再在授权刷新后重发」（da0eadf04）
  已移植（第四批）；**待办**：连接时不再列资源（#10526，影响 `/mcp` 列表与工具注册时机）、
  MCP OAuth 登录的取消与关闭语义（f10993bc7，需 coding-agent 侧按会话持有 signal）。
- **env / codemode**：`codemode` 的输出分隔、`@options` 首行校验、输入约束声明等属 codemode 子系统，
  与 worker 排队一并做。

## 4. 本次移植明细

| 上游提交 | 变更点 | 落地位置 | 测试 |
| --- | --- | --- | --- |
| `8c4e87389` | grep `--no-config` | `Core/Tools/GrepTool.cs`（抽出 `BuildRgArgs`） | `GrepArgsTests`（3） |
| `bdd9f0399` | `mcp.json` BOM | `Extensions/Mcp/Config.cs` | `McpConfigTests.Load_AcceptsFileWithUtf8Bom` |
| `6a51a39d4` | 资源模式 `~` 展开 | `Core/PackageManagerPatterns.cs` | `PackageManagerPatternsTests`（3） |
| `9fe3f3716` | 空引号模板参数 | `Core/PromptTemplates.cs` | `ParseCommandArgs_KeepsEmptyQuotedArguments` |
| `441f5767c` | 打印模式截断退出码 | `Modes/PrintMode.cs` | `PrintModeTests`（2） |
| `3e477eab5` | `PI_CLIPBOARD_OSC52` | `Utils/Clipboard.cs` | `ClipboardOsc52Tests`（3） |
| `6fcb48670` | 摘要输出上限 | `Core/Compaction.cs`、`Core/BranchSummarization.cs` | `SummaryOutputLimitTests`（2） |
| `4dd2b149d` | 空白工具输出 | `Pi.Ai/Api/{OpenAiCompletions,OpenAiResponsesShared,GoogleShared}.cs` | `OpenAiResponsesTests`（2） |
| `4b994d5e5` | 非法 `\u` 转义 | `Pi.Ai/Utils/JsonParse.cs` | `JsonParseTests`（2） |
| `6e491cfa9` + 1.3.0/1.4.0 文案 | 重试判定 | `Pi.Ai/Utils/Retry.cs` | `RetryableClassificationMatchesUpstream1_4_0` |
| `2ed3118b0` | 显示公式 vs 表格 | `Pi.Tui/Marked/MarkedExtensions.cs` | `MarkdownLatexTableTests`（3） |
| `#10897`（1.3.0） | 畸形最终参数上报 `argumentsError` | `Pi.Ai/Utils/JsonParse.cs`（`ParseToolCallArguments`）、`Types/Messages.cs`（`ToolCallContent.ArgumentsError`）、5 个 API 实现、`Utils/Validation.cs`、`Pi.Agent/ToolExecution.cs` | `JsonParseTests`（5）、`OpenAiResponsesTests`（2）、`ValidationTests`（2）、`AgentLoopTests`（3） |
| `92ab53602`（1.4.0） | `Storage.commit(writes, adopt, context)`：持久后同步采纳一次 | `Pi.Durable/Storage/{IStorage,MemoryStorage,JsonlStorage,SqliteStorage}.cs`、`Session/Session.cs` | `StorageConformance`「adopts a durable commit once…」（4 后端）、`SessionTests.CommitWithoutAdoptionPoisonsTheSession` |
| `67996ab75` | `structuredContent` 接受任意 JSON 值 | `Pi.Mcp/Protocol/{Types,Content}.cs`、`Pi.CodingAgent/Extensions/Mcp/Tools.cs` | `ProtocolContentTests`（4）、`McpToolsTests.CreateMcpResultSchema_WithoutStructuredSchema_DeclaresUnknown` |
| `c5f5b3282` | 无元数据时拒绝 `iss`（RFC 9207） | `Pi.Mcp/OAuth/{OAuthFlow,OAuthErrors}.cs` | `OAuthFlowTests.IssWithoutDiscoveredMetadataIsRejectedBeforeTheExchange` |
| `da0eadf04` | 已取消的请求不再在授权刷新后重发 | `Pi.Mcp/Transports/StreamableHttpTransport.cs` | —（.NET 侧 SendAsync 已按 token 取消，仅补齐 MCP 中止错误语义） |

**语料同步说明**（上游行为变更导致的期望更新，非放宽断言）：

- `prompt-corpus.json`：2 条向量按 1.3.0 新语义更新（`"a '' b"` → `["a","","b"]`、`'""'` → `[""]`）。
- `prompt-corpus.json` 的 `builtinSlashCommands`：`share` 的描述按上游第 27 行改为
  `Share session via a Radius artifact or a GitHub gist`（第七批）。语料生成器本次无法直跑
  （上游 1.4.0 的 `child-process.ts` 引入 `cross-spawn`，本地 `node_modules` 没有该包），
  故按上游源码逐字更新该字段，其余向量未动。
- `compaction-corpus.json`：5 处 `captured[].options.maxTokens` 移除（摘要不再带输出上限）。

## 5. 验证结果

| 项 | 结果 |
| --- | --- |
| `dotnet build`（slnx） | 0 警告 / 0 错误 |
| `Pi.Ai.Tests` | 470 / 470 通过 |
| `Pi.CodingAgent.Tests` | 4980 / 4980 通过 |
| `Pi.Agent.Tests` | 36 / 36 通过 |
| `Pi.Durable.Tests` | 457 / 457 通过 |
| `Pi.Mcp.Tests` | 52 / 52 通过 |
| `Pi.Env.Tests` | 44 / 44 通过（3 跳过） |
| `Pi.Tui.Tests` | 23878 / 23878 通过 |

说明：两例时序敏感的偶发已确认与改动无交集，隔离运行均通过——
`ModelSelectorTests.InitialSearchThatMatchesNothingShowsTheNoResultsPath`（顺序相关）与
`DeviceCodeFlowTests.TimesOutWithPlainMessageWhenNoSlowDown`（6 秒超时，与其他测试套件并行跑时
受负载影响）。

## 6. 未移植项与判断依据

1. **CJK 标点相邻的强调渲染（#10730）**：上游通过第三方包 `marked-cjk-friendly@0.1.2` 替换 marked 的
   `emStrong` 分词器（含 CJK 区间表与 flanking 判定）。端口有自研 `MarkedTokenizer.EmStrong` 与
   4,990 条语料基线；直接改会与旧语料冲突，须先从 TS 侧重新生成 `markdown-corpus.json` 才能保证等价。
   属于"需要配套语料基建"的改动，不宜与本次小修混做。
2. **telemetry span**：`observeModelRequest` 观察器在 ai 层没有对应端口（`LazyStream` 也没有 observer
   参数），要接就得先重建整条流式观察链，收益与改动量不成比例，暂不移植。per-key OpenRouter 目录
   已在第六批完成，客户端身份已在第五批完成。
3. **durable 观察面 / chord `traceIn`**：`adopt(seq)` 已在第三批完成；剩下的观察面重构
   （`watchDoc`/`watch`/`watchTaskGraph` 取代 `documentState`/`viewState`/`taskGraph`、
   WatchEnd 迁 chord 并改 `ended`/`failed`）必须与 Experimental 运行时一起改，`traceIn`
   则等 telemetry 落地后才有对象可改。
4. **模型目录新增条目**：纯数据更新，随目录同步脚本一起做，避免手改 `models.json` 漂移。
5. **`sessionShareDestination` / resume 活动工具 / `--model` 选择策略**：均属 coding-agent 中等规模
   行为变更（14 / 4 / 7 文件），本轮已完成 11 项低风险修复，继续保持小步验证的节奏更稳。

## 7. 建议的后续顺序

1. durable 观察面重构（`watchDoc`/`watch`/`watchTaskGraph` 取代旧读接口）——
   `commit(adopt)` 已在第三批完成，剩余部分须与 `Pi.CodingAgent.Experimental` 一并改。
2. ~~ai：identity~~（第五批已完成）→ ~~per-key OpenRouter 目录~~（第六批已完成）。
3. coding-agent：~~`sessionShareDestination`~~（第七批已完成）→ resume 活动工具 → codemode worker 排队。
4. tui：先补语料生成脚本，再做 CJK 强调与 Rex 终端。
5. 模型目录同步（含 1.1.0–1.4.0 新增模型与默认模型变更）。

### 6.x 第五批（identity）落地明细

上游 `adc5fa9f8` 把客户端身份与 provider 归属从 coding-agent 搬进 pi-ai，端口按同一归属实现：

- `Pi.Ai.Utils.Identity`（新，对应 `utils/identity.ts`）：`UserAgentMode`（detailed/short/custom）、
  `ClientAttribution`、`ClientIdentity`（`AttributionSet` 区分「未设置」与「显式 null」）、
  `ClientIdentityTarget`、`MergeClientIdentity` / `FormatUserAgent` / `GetAttributionHeaders` /
  `GetOpenCodeSessionHeaders` / `GetIdentityHeaders` / `ApplyIdentityHeaders` / `ParseClientIdentity`。
  与上游差异：详版 UA 的运行时段是 `dotnet/<version>`（上游为 `node/`|`bun/`），平台与架构按 Node
  命名映射，便于与上游日志对照。
- 8 个 provider 改由 `GetIdentityHeaders` / `ApplyIdentityHeaders` 打底，`SimpleStreamOptions.Identity`
  与各 options 类型的 `Identity` 承载；身份头位于 model / provider / 请求头**之下**，故配置头仍可覆盖。
- `Models`：新增 `Identity`（`CreateModelsOptions.Identity`）与 `WithClientIdentity` 视图（共用同一批
  provider/凭据/刷新控制，增删互相可见），`Stream`/`StreamSimple`/`StreamDeferred` 按
  `merge(视图身份, 请求身份)` 合并，请求身份最优先。
- `Pi.Agent`：`AgentLoop.DefaultAgentIdentity`、`AgentLoopConfig.Identity`、`AgentOptions.Identity`、
  `Agent.Identity`，在 `streamAssistantResponse` 处按上游同一表达式合并。
- `Pi.Durable`：`Harness.DefaultIdentity`、`HarnessOptions.Identity`，构造时把 `Models` 包成身份视图。
- `Pi.CodingAgent`：删除 `Core/ProviderAttribution.cs`（上游已删），新增
  `Core/ClientIdentitySource`（对应 `core/client-identity.ts`；因与 `Pi.Ai.Utils.ClientIdentity`
  同名，C# 侧改名）。SDK 的 `transformHeaders` 只补 OpenCode 会话头，归属头改由 identity 承载；
  `Utils/PiUserAgent` 改为委托 `Identity.FormatUserAgent`。
- 语料：`prompt-corpus.json` 的 `providerAttribution`（22 向量，驱动已删除的 TS 模块）换成
  `clientIdentity`（16 向量，驱动 pi-ai 的 `getAttributionHeaders`/`getOpenCodeSessionHeaders`）。

### 6.y 第六批（per-key OpenRouter 目录）落地明细

上游 `ee409a85f`（#10353）让 OpenRouter 的目录跟着用户的 key 走：`GET /models/user` 列出该 key 可用的
文本模型，并带回真实的限额与价格。

- `Pi.Ai.Providers.OpenRouterCatalog`（新，对应 `providers/openrouter.ts` + `openrouter-api.ts`）：
  - `PersonalizeAsync(catalog, context, httpClient?, ct?)`：凭据取 `Credential.OAuth.Access` 或
    `Credential.ApiKey.Key`，无凭据原样返回；请求跟随 `context.BaseUrl`（缺省
    `https://openrouter.ai/api/v1`），故区域端点 `https://us.openrouter.ai/api/v1` 按本区域过滤。
    15 秒超时与请求信号合并（对应 `AbortSignal.any([...timeout])`）。401/403/404 原样返回目录
    （models.json 里配的代理拒绝该 key 或根本没有该端点时保留完整列表），其余非 2xx 抛
    `OpenRouter model list request failed: <status>`，body 不是 `{data:[…]}` 抛
    `Invalid OpenRouter model list response`。
  - 过滤规则与上游 `flatMap` 一致：图片/分类模型原样保留（`/models/user` 只列文本模型），chat 模型
    不在列表里就丢弃；在列表里则用 `top_provider.context_length || context_length || 原值` 覆盖
    `ContextWindow`、`top_provider.max_completion_tokens || 原值` 覆盖 `MaxTokens`、有 `pricing` 时
    覆盖 `Cost`。`||` 的语义按「缺失/0 都算缺失」实现，与生成器针对 OpenRouter 缺数据的修补一致。
  - `OpenRouterCost`：`$/token` 换成 `$/1M token`（`(v * 1e6).toFixed(6)` 等价于
    `Math.Round(..., 6, AwayFromZero)`）；缺失、非法与负基础价归零（`openrouter/auto` 这类路由模型
    报 -1，价格取决于它最终选了谁）。只有「带 `min_prompt_tokens` 且不含任何 `utc_*`」的 override
    变请求级阶梯，未列费率沿用基础价；按时段的 override 跳过，因为 `ModelCost` 表达不了。
- `RefreshModelsContext.BaseUrl`（对应上游同名新增）：`ModelsAuth.RunProviderRefreshPhaseAsync`
  传 `provider.BaseUrl`，供个性化按区域端点发请求。
- `Pi.CodingAgent.Core.PersonalizedCatalogProvider`（新，对应 `withPersonalizedCatalog`）：与
  `RemoteCatalogProvider` 同款包装（C# 无对象展开，逐成员转发，两个包装刻意互不继承），差别是：
  - 每次联网刷新把「内建模型 + 更新的 pi.dev 目录」过一遍 `personalize` 再保存 —— 存的是个性化
    列表而非 pi.dev 目录，因此**离线也生效**；
  - 跳过刷新间隔与 etag 复验（结果依赖 key，换 key 后必须重新取）；
  - 个性化列表为空（从未保存过，或旧版 pi 在目录缺失/失败后存了空表）时回落到内建模型；
  - 404/501 视为「pi.dev 没有该 provider 的目录」，只用内建模型做个性化。
  `RemoteCatalogProvider` 的 `ParseCatalog`/`ParseHttpDate`/`ReadHeader`/`MergeModels` 相应改为
  `internal static` 以便复用（`MergeModels` 的键仍是 `类型\0id`，chat 与 image 同名模型得以共存）。
- `ModelRuntime.Create` 的 provider 构建改为上游同一分支：`radius` 不包装、`openrouter` 走
  `PersonalizedCatalogProvider`、其余走 `RemoteCatalogProvider`。
- 测试：`OpenRouterCatalogTests` 9 例（覆盖限额优先级、图片/分类不过滤、401/403/404 原样返回、
  base URL 跟随、OAuth 凭据、失败与非 JSON body、阶梯价与负价归零）；其中「原样返回」按上游
  `toBe(catalog)` 断言同一实例。

### 6.z 第七批（`sessionShareDestination`）落地明细

上游 `cea257439`（#10870）让 `/share` 的上传目标可配置：默认仍是「有 Radius 登录就用 Radius，
否则建私有 gist」，但可以强制成 `gist`（想要永久链接的人不希望被 Radius 抢走）或 `radius`
（要求必须有登录，而不是悄悄退回 gist）。

- `Settings.SessionShareDestination`（`string?`），取值拼写集中在 `SessionShareDestinations`
  （`Auto`/`Gist`/`Radius` + `All`），因为端口把 TS 的字符串联合一律当 `string` 承载。
- `SettingsManager.GetSessionShareDestination()`：只认 `gist`/`radius`，其余（包括 `auto`、拼错、
  新版 pi 写入的值）一律归到默认 `auto` —— 与上游 getter 的 `destination === "gist" || "radius"`
  判定一致；`SetSessionShareDestination` 写全局段、`MarkModified` 后保存。
- `InteractiveMode.HandleShareCommandAsync`：`destination != "gist"` 时才探测 Radius；探测失败且
  `destination == "radius"` 时报 `Radius authentication is not configured. Run '/login radius' first.`
  并结束，不再落到 gist；`gist` 分支完全跳过 Radius（连凭据都不读）。
- 设置界面：`SettingsConfig.SessionShareDestination`、`SettingsCallbacks.OnSessionShareDestinationChange`、
  列表项 `share-destination`（紧跟在 `transport` 之后，循环 `auto → gist → radius`），
  `InteractiveMode.ShowSettingsSelector` 接上读取与回写。
- `/share` 的命令描述改为 `Share session via a Radius artifact or a GitHub gist`。
- 随包文档同步上游三处：`Assets/docs/settings.md` 新增 `sessionShareDestination` 行、
  `sessions.md` / `usage.md` 的 `/share` 段落改为「默认行为 + 可强制」。
- 与上游差异：①上游同时新增 `schemas/settings.schema.json` 条目与 TypeBox `SettingsSchema`，
  端口没有 JSON Schema 产物、设置也刻意不在加载时校验（坏值在 getter 处兜底），故只做 getter；
  ②上游 `test/session-share.test.ts` 的「`gist` 不探测 Radius」用例靠 mock `child_process.spawn`，
  端口的子进程层没有测试缝，端到端跑会真的调用 `gh`（在已登录 gh 的机器上会创建真实 gist），
  因此只端到端覆盖「强制 radius 且无登录时报错」这条确定路径；`gist` 分支的入口条件由
  `SessionShareDestinationTests`（5 例）与 `SettingsSelectorTests.CyclesThroughShareDestinations` 覆盖。
- 测试：`SessionShareDestinationTests` 7 例（缺省 → `auto`、`gist`/`radius` 原样、`auto`/拼错/空串
  归到默认、setter 回读）、`SettingsSelectorTests.CyclesThroughShareDestinations`、
  `InteractiveModeTests.ShareWhenForcedToRadiusReportsTheMissingLogin`。
