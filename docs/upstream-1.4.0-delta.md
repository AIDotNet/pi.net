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
| 模型目录更新（Claude Haiku 5.5、MiniMax-M3.1-Flash、Grok `xhigh` 等） | 1.1.0–1.4.0 | 纯数据 | 不适用（数据不在上游仓库，见 §6.6） |
| Anthropic `strict:"prefer"` 重试、Mistral 超时范围 | 1.2.0 #10409 / #10609 | provider 兼容细节 | 本次移植（第十三批） |
| OpenAI Responses `additional_tools` 延迟工具输入项 | 1.0.5 `e47b8e37a` #7709 | 缓存命中率与工具延迟加载 | 本次移植（第十六批） |

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
| 恢复会话时恢复活动工具集 | 1.3.0 #10785 | 影响 `--tools` 与 resume 交互 | 本次移植（第八批） |
| `--model` 的 provider 选择策略统一 | 1.3.0 #10160 | 663 行重写，风险较高 | 待办 |
| codemode worker 排队与 `PI_CODEMODE_MAX_WORKERS` | 1.4.0 #11184 | 并发脚本从失败改为等待 | 不适用（见 §6） |
| `reachesTools`、命名空间 `aliases` | 1.2.0 | 工具可达性与 MCP 等待 | 待办 |
| `before_agent_start` 覆盖自定义触发轮次（破坏性） | 1.2.0 #10267 | 事件语义变更 | 待办 |
| `serializeConversation` 省略 thinking | 1.2.0 #15e7bdc14 | 摘要请求体积 | 本次移植（第十二批） |
| 续传/截断工具调用导致的孤儿结果修复 | 1.4.0 #11227 | 会话后续请求被 provider 拒绝 | 本次移植（第十批） |
| `agent_settled` 增加 `aborted` | 1.1.0 #10607 | 事件字段 | 本次移植（第十一批） |
| 程序状态上报（OSC 7501） | 1.1.0 #10607 | 终端集成 | 待办 |
| Windows shell 探测补充（Store 别名、非 `C:\Windows`） | 1.1.0–1.4.0 #9501/#9504 | 端口已有同类逻辑，需逐条比对 | 本次移植（第十四批，Store 别名部分不适用） |
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
- **mcp（1.2.0–1.4.0，5 提交）**：`structuredContent` 可为任意 JSON 值（#10416）与
  「无元数据时拒绝 `iss`」（c5f5b3282）、「取消的请求不再在授权刷新后重发」（da0eadf04）
  已移植（第四批）；**本次移植（第九批）**：连接时不再列资源（#10526）；**本次移植（第十五批）**：
  OAuth 登录的取消与关闭语义（f10993bc7 / #10565）。
- **env / codemode**：`codemode` 的输出分隔、`@options` 首行校验、输入约束声明等属 codemode 子系统。
  其中 worker 排队（#11184）**不适用**：端口没有 QuickJS worker 池，脚本直接在托管的 Jint VM 里跑，
  每个脚本各有一个 VM，没有「两个 worker 的进程级容量」可以排队；`PI_CODEMODE_MAX_WORKERS` 也就
  没有对象可限。随包 `codemode.md` 的 Limits 章节早已不含上游那两条 worker 说明。

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
| `Pi.Ai.Tests` | 484 / 484 通过（第十六批新增 4 例；此前 480） |
| `Pi.CodingAgent.Tests` | 5014 / 5014 通过（第十六批新增 1 例；第十五批为 5013） |
| `Pi.Agent.Tests` | 36 / 36 通过 |
| `Pi.Durable.Tests` | 457 / 457 通过（第十四批同步了 shell 探测，数量不变）。
  并行跑多个测试套件时出现过 1 例偶发（单独复跑连续两次全绿），与改动无交集 |
| `Pi.Mcp.Tests` | 55 / 55 通过（第十五批新增 3 例；此前 52） |
| `Pi.Env.Tests` | 44 / 44 通过（3 跳过） |
| `Pi.Tui.Tests` | 23878 / 23878 通过 |

说明：两例时序敏感的偶发已确认与改动无交集，隔离运行均通过——
`ModelSelectorTests.InitialSearchThatMatchesNothingShowsTheNoResultsPath`（顺序相关）与
`DeviceCodeFlowTests.TimesOutWithPlainMessageWhenNoSlowDown`（6 秒超时，与其他测试套件并行跑时
受负载影响）。

## 6. 未移植项与判断依据

1. **CJK 标点相邻的强调渲染（#10730）**：上游通过第三方包 `marked-cjk-friendly@0.1.2` 替换 marked 的
   `emStrong` 与 `del` 两个分词器。已把该包（204 行的 `dist/index.js`，无构建期依赖）取下来读过，移植要点：
   - 一张 CJK 区间表（含 `\u{20000}-\u{3fffd}` 这类增补平面区间）；CJK 在这里被**当成标点**参与
     flanking，而不是按 CommonMark 的「非标点非空白」。据此生成四张带 6 个捕获组的右定界符正则
     （`emStrongRDelimAstCjk` / `...AstCjkGfm` / `...UndCjk` / `delRDelimCjk`），GFM 变体额外排除 `~`。
   - 判定入口：`!nextChar || !prevChar || punctuation.test(prevChar) || prevIsCjk || cjkTest(nextChar)`
     —— 比 marked 原版多出 `prevIsCjk` 与 `cjkTest(nextChar)` 两项。
   - `isPrevCharCjk` 要处理 marked 只传一个 UTF-16 code unit 的老行为：`prevChar` 可能落在代理对或
     变体选择符（65024–65038）上，需回退到 `maskedSrc` 里取真正的码点。
   - 命中右定界符后还多一层 `isCjkAdjacentToDelimiter`：定界符紧邻 CJK（前或后）时按 "both" 处理，
     以便跨 CJK 标点收尾。
   最后一段仍是障碍：端口有自研 `MarkedTokenizer.EmStrong` 与约 4,990 条 `markdown-corpus.json` 基线，
   而仓库里**没有**这份语料的生成器（`tools/` 下只有 prompt / compaction / codemode / toolrenderers
   等）。要照上游等价移植，需先补一个 `gen-tui-markdown-corpus.mjs`（驱动 `packages/tui` 的 marked
   管线，依赖 `marked` 与 `marked-cjk-friendly`），再改 C# 分词器；否则只能手工推导向量，风险过高。
2. **telemetry span**：`observeModelRequest` 观察器在 ai 层没有对应端口（`LazyStream` 也没有 observer
   参数），要接就得先重建整条流式观察链，收益与改动量不成比例，暂不移植。per-key OpenRouter 目录
   已在第六批完成，客户端身份已在第五批完成。
3. **durable 观察面 / chord `traceIn`**：`adopt(seq)` 已在第三批完成；剩下的观察面重构
   （`watchDoc`/`watch`/`watchTaskGraph` 取代 `documentState`/`viewState`/`taskGraph`、
   WatchEnd 迁 chord 并改 `ended`/`failed`）必须与 Experimental 运行时一起改，`traceIn`
   则等 telemetry 落地后才有对象可改。
4. **模型目录新增条目**：**已判定不适用**。上游的模型数据不在仓库里 ——
   `packages/ai/src/providers/data/*.json` 被 `.gitignore` 排除（`.gitignore` 第 11 行），
   `providers/*.models.ts` 只是 `generate-models.ts` 从这些 JSON 生成的壳；
   `scripts/hydrate-model-catalog.ts` 明确要求传入**本地的** `models.all.json` 快照
   （「without network access」），仓库里没有这份快照，本机也没有。端口只有
   `src/Pi.Ai/ModelData/{anthropic,deepseek,openai}.json` 三个手写文件，忠实同步 42 家目录
   需要有源数据，硬编会变成编造。决定不改目录数据。
   需要澄清的是**代码侧并无缺口**：`xhigh`/`max` 档位、`clampThinkingLevel` 的顺序、
   `supportsNativeXhighEffort`、各 provider 的 effort 映射在端口里都已存在
   （`Pi.Ai/Models/Models.cs`、`Api/BedrockConverseStream.cs`、`Api/SimpleOptions.cs` 等），
   所以缺的只是「哪些模型有哪些档位」这份数据。
5. **`sessionShareDestination` / resume 活动工具 / `--model` 选择策略**：均属 coding-agent 中等规模
   行为变更（14 / 4 / 7 文件），本轮已完成 11 项低风险修复，继续保持小步验证的节奏更稳。

## 7. 建议的后续顺序

1. durable 观察面重构（`watchDoc`/`watch`/`watchTaskGraph` 取代旧读接口）——
   `commit(adopt)` 已在第三批完成，剩余部分须与 `Pi.CodingAgent.Experimental` 一并改。
2. ~~ai：identity~~（第五批已完成）→ ~~per-key OpenRouter 目录~~（第六批已完成）。
3. ai：~~`additional_tools` 延迟工具输入项~~（第十六批）。
4. coding-agent：~~`sessionShareDestination`~~（第七批）→ ~~resume 活动工具~~（第八批）→ ~~codemode worker 排队~~（不适用）→ ~~孤儿工具结果修复~~（第十批）→ ~~`agent_settled.aborted`~~（第十一批）→ ~~`serializeConversation` 省略 thinking~~（第十二批）→ ~~Anthropic strict 工具降级、Mistral 超时范围~~（第十三批）→ ~~Windows shell 探测~~（第十四批）→ ~~MCP OAuth 取消与关闭语义~~（第十五批）。
5. tui：先补语料生成脚本，再做 CJK 强调与 Rex 终端。
6. 模型目录：已判定不适用（数据不在上游仓库，见 §6 第 4 条）。

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
### 6.w 第八批（恢复会话时恢复活动工具集）落地明细

上游 `6f2f164ab`（#10887，fixes #10785）：SDK 总会把初始工具集算好传进 `AgentSessionConfig`，
于是构造器里那句「`initialActiveToolNames === undefined` 才从记录恢复」永远为假 —— 恢复的会话
丢掉 run 期间临时打开的工具。修法是改用 `usesDefaultTools` 作为判据。

- `AgentSessionBuild` 构造器：`if (_usesDefaultTools) RestoreToolsFromTranscript(_defaultToolModifiers);`
  显式传 `tools` 或 `--no-tools` 的调用方仍然是 `UsesDefaultTools = false`，不恢复。
- `RestoreToolsFromTranscript(modifiers)`：记录的 `toolsAdded` 名单先过
  `SettingsManager.ApplyToolModifiers`（`+name` 加、`-name` 删、按条目顺序），再按
  `IsAllowedTool` 收进 `_pendingToolNames`、`SetActiveTools`。
- `navigateTree` 那处调用保持无参（上游同样未传修饰符）：树的每个分支记录自己的工具集。
- 文档：`UsesDefaultTools` / `DefaultToolModifiers` 的说明补上「恢复会话时生效」。
- 与上游差异：无。上游 `test/suite/harness.ts` 只是给测试替身补了
  `usesDefaultTools: options.initialActiveToolNames === undefined`，端口测试直接显式传该字段。
- 测试：`AgentSessionResumeToolsTests` 6 例（恢复记录的工具集、排除名单生效、显式 `tools` 优先、
  修饰符作用到记录的名单、记录里没有工具集时回落默认集、`UsesDefaultTools = false` 时整段跳过）。
  后两例的期望值按端口形态调整：显式 `tools` 同时要传 `AllowedToolNames`（与 SDK 的
  `ResolveTools` 一致），反向断言那例不注册扩展工具，否则「注册即激活」的 `active_tool` 会让
  「空工具集」不可达。
### 6.v 第九批（MCP 连接时不列资源）落地明细

上游 `f4cca2dc2`（#10526）：注册了资源的服务器，要等 `resources/list` 与
`resources/templates/list` 都完成才注册工具；一个大网盘之类的慢列举会把工具挡在第一条提示之外。
那两份列表只喂 `/mcp` 与 `pi mcp list` 的计数，所以改成本地不列举、只标记「该服务器提供资源」。

- `McpServerConnection`：删掉 `Resources` / `ResourceTemplates` 两个缓存属性、连接时的
  `FetchResourcesAsync`、`notifications/resources/list_changed` 处理器与 `RefreshResourcesAsync`
  （以及随之无用的 `FetchedResources` / `SwallowEmptyAsync`）。连接只问 `initialize` 与 `tools/list`，
  `HasResources` 直接取 `resources` 能力位。
- `/mcp` 的 `connected` 状态：`connected · N tools` 后面只在有资源时加 ` · resources`（不再计数）。
- `pi mcp list`：报告字段 `resources` 从 `int` 计数改成 `bool?`（`true` 表示提供资源），
  `resourceTemplates` 字段删除；文本输出改为 `  offers resources`。
- 资源工具（`list_mcp_resources` / `list_mcp_resource_templates` / `read_mcp_resource`）不变，
  仍按需列举与读取，因此 `McpResources.IsMcpAppResource` 保留（`Resources.cs` 里的按需过滤还在用）。
- 与上游差异：上游把 `isMcpAppResource` 降级为模块私有（只剩 `resources.ts` 内部一处用），端口的
  `McpResources.IsMcpAppResource` 仍是 public —— 端口有单测直接钉这个判定的入参组合，且
  `Resources.cs` 的按需列举也在用。
- 测试调整（行为变更导致的期望更新，非放宽断言）：
  `ConnectListsToolsAndResourcesWithoutMcpApps` → `ConnectListsToolsButNotResources`（并断言
  `resources/*` 一次都没问）、`AResourcesListChangedNotificationRefreshesTheResources` →
  `AResourcesListChangedNotificationIsIgnored`、`AFailingResourceListStillConnects` →
  `AResourcesListThatFailsDoesNotAffectTheConnect`（失败落到按需列举上）、两条重试计数各减一、
  `/mcp` 补全文案改成 `connected · 1 tool · resources`。
### 6.u 第十批（恢复性剔除连同工具结果一起）落地明细

上游 `02726086d`（#11227）：可恢复的 length 停止在**提交前**恢复时，只剔除了助手消息、没剔除应答它的
工具结果 —— 因为只有「一轮跑完」那个调用方把结果传了进去。落单的工具结果进了 transcript，之后每个
请求都被 provider 拒绝（`tool_result` 找不到对应的 `tool_call`）。

- `OmitRecoveryAttempt(message)` 不再收 `toolResults` 参数：自己按 `ToolCallContent.Id` 收齐
  `toolCallId`，在 `_agent.Messages` 里定位这条助手消息（`ReferenceEquals`，或按
  `_entryIdsByMessage` 的 entryId 认 —— 上下文编辑会把投影副本换掉），从它的下一条扫到**下一条
  助手消息**为止，收集 `ToolCallId` 命中的 `ToolResultMessage`。调用方是谁都无所谓。
- 删除 `AgentSessionBuild._lastAssistantToolResults`、`turn_end` 里的记录、`HandlePostAgentRunAsync`
  的传递，以及 `CheckCompactionAsync(…, toolResults)` 的第三个参数。
- 与上游差异：无。上游用 `messages.slice(index + 1)` + `for…of` 早退，端口用显式下标循环。
- 测试：`AgentSessionRecoveryOmitsToolResultsTests` 2 例 ——
  - 恢复的会话里塞一条 `length` 停止的助手消息（带 `toolCall`）与它的 `toolResult`，走 `PromptAsync`
    的提交前恢复，断言**请求上下文里既没有 `toolResult`，也没有 `length` 停止的助手消息**；
  - 反向断言：剔除范围正好是「截断的调用 + 应答它的结果」两条 `context_edit`（都是 `replacement` 为
    null 的剔除），避免以后把范围放宽悄悄丢上下文。
  - 这两例在去掉结果查找后会失败（请求里会重新出现 `ToolResultMessage`），已实测确认。
### 6.t 第十一批（`agent_settled.aborted`）落地明细

上游 `503c60552`（#10607）的前半段。`agent_settled` 原先不带任何字段，界面分不清「这轮正常收尾」
和「这轮被 Escape 中止」，中止也就没法回到 idle。

- `AgentSessionEvent.AgentSettledEvent(bool Aborted)` 与扩展侧的 `ExtensionEvent.AgentSettled(bool Aborted)`
  各加一个必填字段（TS 同样是必填，不是可选）。
- `EmitAgentSettledAsync`：在派发前取一次 `_agentRunAbortRequested`，扩展事件与会话事件用**同一个**
  快照值（TS 的 `const aborted = this._agentRunAbortRequested`）。
- 文档：`Assets/docs/json.md` 的示例记录与事件表同步成
  `{"type":"agent_settled","aborted":false}` / `| agent_settled | aborted |`。
- 与上游差异：无。OSC 7501 那半段（`packages/tui/src/program-status.ts`、
  `coding-agent/src/modes/interactive/program-status-reporter.ts` 等 20 余文件）**未做**，
  仍留在待办里 —— 它要先给端口终端层加能力查询与 `PI_PROGRAM_STATUS` 覆盖，属于 TUI 批次。
- 测试：`AgentSessionPromptTests.AgentSettledReportsAbortedWhenTheRunWasAborted`（新增）与
  `RunAgentPromptAsync_AgentRunFailure_StillSettlesWithoutThrowing`（强化为断言 `Aborted == false`）。
  新用例用一条阻塞式 `StreamFn`（TCS 放行）把 run 卡在流里，再 `AbortAsync()` —— 中止标志是同步
  置位的，所以「先中止、后放行」不会死锁。
### 6.s 第十二批（摘要序列化省略思维链）落地明细

上游 `15e7bdc14`：回放的思维块常常不在模型活上下文里（radius 会丢，Anthropic 会丢掉与其请求不匹配的
块），而 `serializeConversation` 把它们全塞进去 —— 一个 ~996K/1M token 的会话因此产生了 1.38M token 的
摘要请求，每次重试都失败。provider 本来就只返回真实推理的摘要，可见文本、工具调用与工具结果已经把
「发生了什么」记全了。

- `Pi.CodingAgent/Core/CompactionUtils.SerializeConversation`：删掉 `[Assistant thinking]: …` 这一段与
  收集它的 `thinkingParts`（`case ThinkingContent` 分支一并删掉）。助手消息现在只产出
  `[Assistant]: …` 与 `[Assistant tool calls]: …`。
- `Pi.Durable/Harness/Compaction.SerializeConversation`：同一改法（`thinking` 列表与那段 push 删除）。
  其余格式不变 —— 助手文本块仍按 `\n` 连接，工具结果仍按 2000 字符截断。
- 文档：`Assets/docs/compaction.md` 的序列化示例与扩展示例注释各去掉一行 thinking，并补一句
  「Thinking blocks are omitted…」。
- 语料：`tests/Pi.CodingAgent.Tests/compaction-corpus.json` 的 `serializeConversation` 向量去掉
  `[Assistant thinking]: hidden reasoning\n\n`。**这条不是靠手改拍板**：`tools/gen-coding-agent-compaction-corpus.mjs`
  在本机可以跑（`node --experimental-strip-types --import ./tools/register-compaction-shims.mjs …`），
  重新生成后与手改结果逐字段对比，只有 `__tmp` 与几个未 token 化的时间戳不同，
  `/serializeConversation` 整段**完全一致**，因此没有把重新生成的文件整体写回（避免引入无关抖动）。
- 与上游差异：无。
- 测试：`CompactionCorpusTests.SerializeConversation_OmitsThinking`（新增，直接钉住「思维链不出现、
  文本/工具调用/工具结果还在」），`Pi.Durable.Tests` 的
  `SerializeConversationFormatsTranscriptOmitsThinkingAndTruncatesToolResults`（原断言 `Contains`
  改为 `DoesNotContain`，方法名一并改）。
### 6.r 第十三批（Anthropic strict 工具降级 + Mistral 超时范围）落地明细

约束采样本体（`Tool.constrainedSampling`、`resolveJsonSchemaStrictSampling`、grammar 工具、
各 provider 的 strict 分支）端口早已移植，缺的是这两条**兼容性兜底**。

**Anthropic strict 容量拒绝降级重试**（上游 `d4d53612b`，#10409）：Anthropic 会因为 strict 工具集
超出容量回 400 —— 编译出的语法太大、strict 工具太多、union 类型参数太多。这类拒绝只跟 strict 有关。

- `BuildParams` 拆成 `BuildParamsForRequest(...) → AnthropicBuiltParams(Params, HasPreferredStrictTools)`；
  `BuildParams` 保留原签名、委托给它（调用方与既有测试不受影响）。
- `ConvertTools(..., disablePreferredStrictTools)`：该开关为真时，声明为
  `{type:"json_schema", strict:"prefer"}` 的工具按**原始** schema 发送；`strict:"require"` 仍走 strict。
- 判定「有没有可降级的工具」用的是**转换结果**：`definitions[i].strict == true` **且** 该工具的声明是
  `prefer`。这样 `supportsStrictTools` 为假、或 schema 命中 Anthropic 不接受的 keyword 而自行降级的
  工具，都不会误触发重试。
- `Stream`：`retryProviderRequest` 外面套一层循环。捕获 `ProviderHttpException` 且
  `!signal.IsCancellationRequested && !disablePreferredStrictTools && built.HasPreferredStrictTools &&
  IsStrictToolLimitRejection(error)` 时置开关、追加诊断
  `anthropic_strict_tools_fallback`（`details.reason = "strict_tool_limits_exceeded"`）、重发一次。
  **已经开始的流永不重试**（异常只会来自发请求阶段）。
- `IsStrictToolLimitRejection`：`status == 400` + body 里 `error.type == "invalid_request_error"` +
  文案命中 `The compiled grammar is too large` / `Too many strict tools (` /
  `Schemas contains too many parameters with union types (`。非 JSON body 直接判否。

**Mistral 超时范围**（上游 `6b07b4e57`，#10609）：超时只覆盖「等响应头」，长流不能因为固定截止时间
被砍断，中途卡住交给 HTTP 客户端的空闲超时。

- `RequestMistralStreamAsync`：CTS 改名 `headerTimeoutCts`，`SendAsync` 成功后立刻
  `CancelAfter(Timeout.Infinite)`，明确撤掉截止时间；错误文案改成
  `Mistral response headers timed out after {ms}ms`（原 `Request timed out after …`），
  且取消过滤器补上 `!cancellationToken.IsCancellationRequested`，不让调用方取消被误报成超时。
- **顺带修掉一个端口真 bug**：原来 `await using var content = await response.Content.ReadAsStreamAsync(…)`
  在方法返回时就把正文流 Dispose 了 —— 而 `ReadMistralEvents` 是异步迭代器，真正读取发生在之后。
  于是 Mistral 的**流式路径从来没有成功过**（一片 delta 都读不到），因为没有 Mistral 流式测试所以一直
  没暴露。现在把 `HttpContent` 交给迭代器，在**枚举开始时**打开、枚举结束 `await using` 释放。
- 与上游差异：无。上游用 `combineAbortSignals` + 单独的 header 信号；端口用 `CreateLinkedTokenSource`
  + `CancelAfter(Infinite)`，语义一致。
- 测试：`AnthropicStrictToolFallbackTests` 9 例（400 后重发一次且 prefer 降级、require 保持 strict、
  诊断条目、无 prefer 工具时不重试而把 400 原样上抛、6 组 `IsStrictToolLimitRejection` 判定矩阵、
  非 JSON body 判否）；`MistralStreamTests.AStreamThatOutlivesTheTimeoutKeepsGoing`（`TimeoutMs = 100`，
  正文两片间隔 400ms，断言 delta 与 `StopReason.Stop` 都到位 —— 这条同时钉住了上面那个 Dispose bug，
  修之前它一片 delta 都收不到）。

### 6.q 第十四批（Windows shell 探测跟随 `%SystemRoot%`）落地明细

上游 `28bea006b`（#9501/#9504）把 Windows shell 探测改了两处，端口分开处理。

**一、`isLegacyWslBashPath` 跟随系统目录（已移植）**

上游原来写死 `/^[a-z]:\windows\(system32|sysnative)\bash\.exe$/`，Windows 装在别处（`D:\WINNT`、
精简镜像、改过系统盘符）时旧版 WSL 垫片识别不出来，于是拿 `-c` 去调一个只认 stdin 的 bash。

- `Pi.CodingAgent.Utils.Shell.IsLegacyWslBashPath(path, systemRoot = null)`：新增可选 `systemRoot`
  （测试缝，缺省读 `%SystemRoot%`），按它拼 `System32` / `Sysnative` 两个目录名比较；`systemRoot`
  缺失或**不是盘符限定**（`\\server\share`、相对路径）时退回原来的约定形态正则 —— 与上游
  「未知 Windows 目录就按 `C:\Windows` 认」的兜底一致。`DriveQualifiedRegex` 必须带
  `RegexOptions.IgnoreCase`，否则 `C:\Windows` / `D:\WINNT` 这种大写盘符匹配不上（实测踩过）。
- `Pi.Durable.Env.LocalExecutionEnv` 里有一份同名私有方法（Pi.Durable 不引用 Pi.CodingAgent），
  同步成同一语义，`GetBashShellConfig` 的 `-s` / `-c` 分支行为随之对齐。
- 测试：`ShellProbeTests` 19 例 —— 13 例覆盖「约定形态 / 已知 SystemRoot / Windows 装在别处 /
  非盘符 SystemRoot」，6 例钉住只有 `System32` 与 `Sysnative` 两个目录名命中。
  `CoreUtilsCorpusTests` 里那两条向量改为显式传 `systemRoot: ""`，避免本机 `%SystemRoot%`
  让语料结果随机器变化。

**二、Store 别名探测（不适用）**

上游另一半把 `existsSync(path)` 换成 `accessSync(path, F_OK)`，因为 node 的 `existsSync` 在
Windows 上会额外 stat，让可正常执行的 Store 应用别名（`WindowsApps\...`）返回 EACCES 而被判不存在
（nodejs/node#36790）。这条在 .NET 没有对应物：`File.Exists` 走 `GetFileAttributesEx`，不额外 stat，
对符号链接/重解析点只报存在性，不会因权限位把可执行别名判死。端口维持 `File.Exists`，无需改动 ——
硬造一个「访问检查」反而会引入上游要规避的那个误判。

### 6.p 第十五批（MCP OAuth 取消与关闭语义）落地明细

上游 `f10993bc7`（#10565）：`pi-mcp` 的 OAuth 流程给每个请求带上取消信号；被取消的刷新不再退化成
一次新授权；流式 HTTP 传输关闭会话时复用「最近一次请求的令牌」而不是去问认证提供者；coding-agent
的 MCP 扩展按会话持有**一个**取消源，Esc 与会话关闭都会中止登录，关闭时等被中止的登录清理完；
发往授权服务器的请求一律 15s 上限，`pi mcp login --timeout` 限制整次登录。

端口侧把上游的 `AbortSignal` 落成 `CancellationToken`（本来就贯穿这一层），分四处改：

**一、关闭会话不再触发刷新**（`Pi.Mcp/Transports/StreamableHttpTransport.cs`）

- 新增 `_lastToken`，`ApplyAuthHeaderAsync` 每次取令牌时记下；`ApplyHeaders` 多一个 `token` 参数。
- `CloseAsync` 的 DELETE 改用 `_lastToken`，且超时只用 `new CancellationTokenSource(1s)`，
  **不再链接调用方的令牌**。理由与上游一致：`AuthProvider.Token()` 可能先做一次网络刷新，
  关闭流程不能等它。
- `AuthProvider.Token` 的文档同步说明「只在会等应答的请求前调用，关闭时不问」。

**二、取消的刷新不得变成新授权**（`Pi.Mcp/OAuth/OAuthFlow.cs`）

`RunFlowAsync` 的刷新分支在原有 `catch (OAuthInsecureEndpointError)` 之前加
`catch (Exception) when (cancellationToken.IsCancellationRequested) { throw; }`：
请求可能已到服务器并轮转了 refresh token，再走一次授权会把新凭据丢掉。.NET 的取消异常本就不是
`OAuthError`，这层是上游同款防御（自定义 fetch 可能把中止包成别的异常）。

**三、登录整体可取消 + 15s 请求上限**（`Pi.CodingAgent/Extensions/Mcp/OAuth.cs`）

- `RefreshRequestTimeoutMs` → `OAuthRequestTimeoutMs`，语义从「刷新请求」扩到「所有发往授权服务器的请求」。
- 新增 `TimedFetch(McpFetch? = null)`（对应 TS `timedFetch`）：给每次请求叠加 15s 上限，调用方令牌继续生效；
  刷新路径与 `SignInServerAsync` 都用它。
- `SignInServerAsync`：进入即 `if (cancellationToken.IsCancellationRequested) throw McpSignInCancelledError`；
  外层再加 `catch (Exception) when (cancellationToken.IsCancellationRequested) → McpSignInCancelledError`，
  让「底层中止异常」统一报成「登录已取消」（CLI 的错误文案依赖这个类型）。
- `WaitForAuthorizationResponseAsync` 无需改：端口本来就把 `controller` 与调用方令牌链接，取消会中止粘贴提示。

**四、扩展按会话持有一个取消源**（`Pi.CodingAgent/Extensions/Mcp/McpExtension.cs`）

- 删掉 `sessionActive` 与 `generation` 计数器，换成一个初始即取消的 `CancellationTokenSource`：
  「会话是否活跃」变成 `!token.IsCancellationRequested`，迟到的运行时加载/连接结果按它丢弃。
- 新增 `Track(Task)`（对应上游 `track`）登记后台工作：管理动作与登录。`SessionShutdown` 先
  `CancelAsync()`，关连接，再**等**登记的这些工作清理完（回调端口不会在会话消失后才释放）。
- `SignInAsync(server, prompt, cancel)`：会话令牌与 Esc 令牌链接后传给 `SignInServerAsync`；
  `RunActionAsync` / `LoginCommandAsync` 在动作结束后检查会话是否已结束，结束就丢弃结果、不再碰 `ctx`。
- `SignInWithUiAsync`：建一个 Esc 用的 CTS，状态页带取消键（`Status(title, message, onCancel)`），
  「Connecting…」仍可取消 —— 与上游一致。
- `IMcpUi.Status` 增加可选 `onCancel`；`McpManagerView` 有回调时在页脚显示 cancel 提示并响应
  `tui.select.cancel`，无回调时按键不做任何事。

**五、`pi mcp login --timeout` 覆盖整次登录**（`Cli.cs`）

超时从「等粘贴重定向 URL」提到 `SignInServerAsync` 的令牌上：`WaitForRedirectUrlAsync` 去掉自己的
`timeoutMs`，只跟随传入令牌。于是发现、授权、换令牌任何一步卡住都会在时限内报
「was cancelled or not completed within Xs」。

**测试**

- `tests/Pi.Mcp.Tests/TransportCloseTests.cs`（新）：关闭复用最后一个令牌、不再调提供者（计数不变）、
  DELETE 带 `Mcp-Session-Id`。
- `tests/Pi.Mcp.Tests/OAuthFlowCancelTests.cs`（新）：发现阶段取消 → 直接失败且 `RedirectedTo` 为空；
  刷新阶段取消 → 停在 `/token`，不落到新授权。
- `tests/Pi.CodingAgent.Tests/McpOAuthTests.cs`：已取消令牌 → 立即 `McpSignInCancelledError`；
  卡住的发现请求被取消 → 报 `McpSignInCancelledError`（去掉新加的 catch 这例会抛底层中止异常，用例即失败）。
- `tests/Pi.CodingAgent.Tests/ExtensionManagerViewTests.cs`：状态页有取消键时 Esc 触发回调；
  无回调时既不显示 cancel 提示也不响应按键。
- 坑：`HttpListener` 的前缀必须挂在**根**（`http://127.0.0.1:port/`）上，发现请求打在
  `/.well-known/...`，用 `/mcp/` 前缀会让 `GetContextAsync` 抛 404 并结束服务循环（该用例超时 10s）。

### 6.o 第十六批（`additional_tools` 延迟工具输入项）落地明细

上游 `e47b8e37a`（#7709）：延迟加载的工具以前只能靠客户端 tool search（`tool_search_call` +
`tool_search_output` 两个输入项）就地补发；支持 `additional_tools` 的模型改为直接插一个
`{type:"additional_tools", role:"developer", tools:[…]}` 输入项，少两个回合合成项，前缀缓存也更容易命中。

**ai 侧此前已落地，本轮只差两处**

- `OpenAiResponsesShared.ConvertResponsesMessages` 里 `SupportsAdditionalTools` 分支、
  `OpenAiResponses` / `AzureOpenAiResponses` / `OpenAiCodexResponses` 里
  「`additional_tools` 优先、`tool_search` 兜底、都没有则进顶层 `tools`」的三段选择都在位。
- **补上的是 compat 声明**：`Pi.CodingAgent/Core/ModelConfig.cs` 的
  `OpenAiResponsesCompatSchema` 缺 `supportsAdditionalTools` 与 `supportsToolSearch`
  两个字段（上游把它们放在 `packages/ai/src/providers/compat-schema.ts`）。缺了它们并不会报错
  —— 因为 `ProviderCompatSchema` 是三分支 `Union`，而对象校验与 TypeBox 一样**不拒绝未知键**，
  写错的字段会被静默接受（与上游同构，故未改成严格模式）。

**为什么这段值得单独记一笔**

「schema 里没声明」在这里不等于「用户写了会报错」：`ObjectJsonSchema` 只校验声明过的键，
`Union` 只要有一个分支通过就算通过。所以这类字段漏声明是**静默失效**，只能靠测试盯住语义。

**测试**

- `tests/Pi.Ai.Tests/Api/DeferredToolsTests.cs`（新，4 例，对应上游 `deferred-tools.test.ts`）：
  `additional_tools` 分支（`role=developer`、迟到的工具、无 `defer_loading`、无 tool search 项）；
  退回 `tool_search` 分支（`execution=client`、`status=completed`、`defer_loading=true`、无
  `additional_tools`）；两者都不支持时不留任何就地标记；工具用过之后标记仍排在
  `function_call` 之前、且顶层请求工具仍只有基础工具。
- `CoreProviderComposerTests.ModelsJsonAcceptsTheDeferredToolsCompatFlags`：`models.json` 里写
  `compat.supportsAdditionalTools` / `supportsToolSearch` 能带进快照。
- **踩坑**：测试上下文必须给 `SupportsMidConvoSystemMessages = true`。否则 `Transcript.ResolveTranscript`
  会把后续系统消息折叠进首条，「迟到的工具」就成了请求级变更，`ResolveTranscriptTools` 返回
  `anchorsAdditions = false`，两种就地补发都不会发生 —— 一开始四个用例里有三个因此失败。
