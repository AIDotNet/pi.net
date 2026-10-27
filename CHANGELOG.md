# Changelog

All notable changes to `pi.net` — the .NET 10 port of [pi](https://github.com/earendil-works/pi) — are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versions track the upstream `packages/*` release line.

## [Unreleased]

### Added

- **进程内 JavaScript/TypeScript 扩展宿主（方案 A）**：`.ts` / `.tsx` / `.js` / `.mjs` / `.cjs` / `.jsx`
  扩展现在直接加载，不再要求先编译成程序集。实现要点：
  - **转译**：`@babel/standalone` 跑在 Jint 上（`Assets/extension-runtime/vendor/babel.min.js`），
    `sourceType: unambiguous` 让 TypeScript、node_modules 里的 ESM 和 CommonJS 走同一条管线；
    结果按内容哈希缓存，编译线程用 64 MB 栈（TypeBox 的生成式解析器会撑爆默认栈）。
  - **模块系统**：CommonJS 解析（相对路径、`node_modules` 上溯、`package.json` 的
    `exports`/`module`/`main`、目录 index），每个扩展一个 Jint engine，互不污染。
  - **Node 内置模块**：`fs`、`path`、`os`、`process`、`timers`、`url`、`crypto`、`util`、`events`、
    `child_process`、`buffer`、`module`、`zlib`、`readline` 的 shim，全部经宿主落到真实的文件系统/进程。
  - **`@earendil-works/*` 运行时**：`pi-ai`（TypeBox 风格 `Type`、`StringEnum`、`uuidv7`）、
    `pi-coding-agent`（`defineTool`、`getAgentDir`、`withFileMutationQueue`、截断助手、
    内置工具工厂）、`pi-tui`（`Text`/`Box`/`Container`/`matchesKey`/`visibleWidth` 等）。
  - **宿主 API 桥**：`pi.*` 全量注册接口（工具/命令/快捷键/flag/事件/exec/消息/会话标题/工具开关）、
    `ctx.ui`（notify/select/confirm/input/editor/status/widget）、`ctx.sessionManager`、
    `pi.events` 跨扩展事件总线，全部经 JSON 桥落到既有 `IExtensionApi`。
  - **Promise 泵**：扩展的 `async` 工厂与处理函数由 `Engine.Advanced.ProcessTasks()` 加自建定时器队列
    驱动，宿主不需要事件循环。
- `CompositeExtensionModuleLoader`：按扩展名分派——`.dll` 走 `AssemblyLoadContext`，源码走 Jint。
  `ExtensionLoader` 的默认装载器换成它，`DiscoverAndLoadExtensions` 不再把 `.ts`/`.js` 分流成 warning。
- `docs/dotnet-extensions.md` 重写：两条扩展路径（程序集 / 源码）的入口约定、shim 覆盖面、
  尚未桥接的 API 清单。

### Changed

- **`ResolveExtensionEntries` 恢复上游顺序 `index.ts` → `index.js` → `index.dll`**：源码是事实来源，
  编译产物退为后备（1.4.4 里「优先 index.dll」的做法按上游语义回退）。
- 1.4.4 里「源码扩展只给一条提示」的行为已被上面的宿主取代；`SourceExtensionWarning` 与
  `PartitionSourceExtensions` 一并删除。
- `pi.registerMessageRenderer` / `registerEntryRenderer` / `registerToolRenderer` /
  `registerMarkdownTransformer` 目前**降级为警告**：扩展照常加载，自定义渲染不生效，而不是让整个扩展
  加载失败。`registerProvider` / `registerVirtualModel` 仍然显式抛错——静默忽略会让用户误以为模型
  供应商已注册。

### Fixed（运行期审查发现的缺陷）

结论：`JsExtensionTests` 只证明「能加载并注册」，用 `ExtensionRunner` 真实派发事件后才暴露出下面 6 个问题。
均已在本次修掉，并补了 `JsExtensionRuntimeTests`（7 例，含两个真实上游守卫扩展）钉住：

- **事件处理函数拿到的是原始 JSON，不是 ctx**：`api.on` 直接把宿主传来的 payload 交给 handler，
  于是 `ctx.ui.notify`、`ctx.sessionManager.getBranch`、`ctx.ui.theme` 全部是普通字段，
  上游扩展在 `session_start` 里一调用就崩（语料里 18 例）。命令与工具走的是
  `__pi_make_context`，只有事件漏了。修复后语料运行期通过数 55 → 71。
- **`pi.*` 在工厂返回后失效**：`InvokeFactory` 在 `finally` 里解绑 api，而命令/工具/事件回调都在
  工厂之后才执行，因此 `pi.getActiveTools()` 之类会抛「used its pi object outside a host call」。
  改为绑定到扩展生命周期（reload 时重新绑定）。
- **事件处理函数的结果被丢弃**：调用的是 `On<E>` 重载（返回值 `Task`），结果恒为 null，
  只有 `On<E, R>` 会保留；`tool_call` 的 `{ block: true }` 因此从来没生效过。
- **`ctx.sessionManager` 是个空壳**：payload 里放的是字符串占位 `"__pi_fn__"`，
  `getBranch`/`getEntries` 一调用就 TypeError。改为由宿主按需提供（`ctx.session.entries` 等）。
- **上下文构造时急切读取运行时动作**：`RegisterContext` 直接读 `ThinkingLevel`/`Model`/`IsIdle`，
  在运行时未绑定（加载期、project-trust、测试）时抛「Extension runtime not initialized」，
  让一次事件派发整体失败。改为 JS 侧惰性 getter + 宿主 try/catch 降级。
- **`fs.watch` 报「not a function」**：改为明确的「没有后台事件循环」错误，并补 `fs.watchFile` /
  `fs.promises.watch`；`pi.registerTool` 丢弃 `renderCall`/`renderResult` 时也会告警而不是静默。
- **事件结果只映射了 `{ block }`**：`context` 的 `{ messages }` 与 `message_end` 的 `{ message }`
  被静默丢弃（改写上下文/消息的扩展等于无效）。现在按 `ContextEventResult` / `MessageEndEventResult`
  映射，形状不匹配时降级为「无结果」并记 debug 日志，不会让其他事件误伤。

### Added (verification)

- `JsExtensionTests`（6 例）：上游 `hello.ts` 真实加载并执行工具、`node:fs`/`node:path` 解析、
  命令/flag/快捷键/事件注册、无默认导出、`enum`/`satisfies`/泛型语法。
- `JsExtensionRuntimeTests`（7 例）：经 `ExtensionRunner` 真实派发事件——上游 `protected-paths.ts`
  拦截 `.env` 写入且对非目标工具放行、`permission-gate.ts` 拦截危险命令、命令处理函数在工厂返回后
  仍能调用 `pi.*` 并读 `ctx.sessionManager`、`pi.on` 的退订生效、工具 `execute` 拿到工具上下文并
  透传 `onUpdate` 部分结果、`pi.events` 跨扩展投递。
- 对上游 86 个 TypeScript 示例做**运行期**语料探针（加载 + 注册 + 派发 `session_start` 与两次
  `tool_call`）：71 个完全通过；6 个是没有默认导出的辅助模块（`doom-engine.ts` 等，本就不是入口）；
  9 个未通过 = 4 个缺第三方 npm 包（`@anthropic-ai/sdk`、`@earendil-works/gondolin`、
  `@anthropic-ai/sandbox-runtime`、`ms`，装上即可）+ 2 个 provider/virtual-model 注册 +
  1 个读取 `~/.pi/agent/extensions/auth.json` 的测试脚本 + 1 个 `fs.watch`（无后台循环）+
  1 个 `osascript`（macOS 专用，Windows 上失败属预期）。

## [1.4.4] - 2026-10-27

对齐上游 `v1.4.0` 之后 88 个未发布提交里的 P0 部分：13 条用户能撞到的缺陷，外加几条低风险特性。
上游剩余的大头是 chord/durable 的 watch 体系重构，另立阶段处理。

### Added

- `pi doctor [--json]`：在会话初始化前离线诊断本地配置、项目信任、Shell/搜索工具路径和凭据来源。
  支持文本与结构化 JSON 输出；不加载用户扩展、不写文件、不刷新 OAuth 或执行取密命令。
- **可编译的 C# 扩展示例**：`samples/Pi.Sample.Extension`（一个工具 + 一个斜杠命令 + 状态栏更新），
  里面写了 `ProjectReference Private="false"` 的原因（契约程序集必须由宿主解析，扩展目录不能放
  私有副本）。已加入解决方案并接入 `dotnet-extensions.md`。

- **GPT-6.1 Terra**：`openai`、`openai-codex`、`azure` 三个 provider 补上 `gpt-6.1-terra`
  （$1/$5，缓存读 $0.05、写 $1.25，272k 以上走长上下文档位；Codex 侧 500k 窗口）。它接受
  `reasoning.effort: "none"`，而 Sol 不接受，两者都拒绝 `minimal`——thinking 档位表按此填写。
  `openai` 的默认模型同时从 `gpt-5.5` 换成 `gpt-6.1-sol`。
- `pi-messages` 终态事件（`done`/`error`）现在透传 `responseModel`，与其它五家 API 一致。

### Changed

- **源码扩展改为一条可执行的提示，而不是一个坏掉的加载**：发现结果里的 `.ts`/`.js`/`.mjs`
  等在进入加载器前被分流，记一条 warning「TypeScript/JavaScript extensions are not loadable by
  the .NET port. Compile … into an assembly …」，同目录的 `.dll` 仍然正常加载。
  `LoadExtensions` 本身保持与上游一致（它可以被喂任意路径，由 module loader 决定怎么处理）。
- **`ResolveExtensionEntries` 优先 `index.dll`**：同一目录同时存在 TS 源与编译产物时，只有能加载
  的那个算数（上游顺序是 ts → js，因为它的运行时能编译源码）。
- `Assets/examples/extensions` 重命名为 **`extensions-typescript`**，README 顶部标明「protocol
  reference only」，避免用户拷进 `.pi/extensions/` 后误以为能生效。

### Fixed

- **扩展模块的默认装载器还是 4d-2b 的占位实现，真实加载必然失败**：`ExtensionLoader` 在调用方
  不给 `moduleLoader` 时落到 `MissingModuleLoader`，它直接抛「no IExtensionModuleLoader was
  supplied. The AssemblyLoadContext implementation lands with batch 4d-3」。而 4d-3 的
  `AssemblyLoadContextExtensionLoader` 早就写好了，只是默认值没换——`ResourceLoader` 的两处
  `LoadExtensionsCached(...)` 都没传 loader，于是**任何真实扩展都加载不了**（内置扩展不受影响，
  它们走 `BuiltInExtensions`）。现在默认 `AssemblyLoadContextExtensionLoader.Instance`
  （进程内单例，保住按路径复用的 collectible context），占位类删除。
- `--list-models` 的模糊搜索包含模型显示名称，可搜索 `models.json` 中的自定义名称；表格格式与排序保持兼容。
- **MCP 登录只认 http/https**：授权端点会用系统 URL 处理器打开，之前只挡了
  `javascript:`/`data:`/`vbscript:`，`file:` 和 `search-ms:` 这类自定义协议仍会被拉起本地程序。
  元数据解析和 `startAuthorization` 两处都按上游改成只放行 http/https。
- **不可信输入加固**：`pi-messages` 的 `contentIndex` 来自 wire 却直接当数组下标用，`__proto__`
  式的越界值会把 content 撑成稀疏数组或让后续 delta 写错块，现在只接受「下一块或已有块」；
  Radius OAuth 的授权端点与 `verification_uri` 走统一的 http/https 校验；登录对话框只打开
  http/https 链接，非网页 URL 里的控制字符替换成空格；终端标题（会话名来自会话文件）同样
  清洗 C0/C1 控制字符，避免 BEL/ESC 提前结束 OSC 序列。
- **Anthropic 系统更新落到 user 消息前面导致 400**：转换后没有任何块的 assistant 消息会被丢弃，
  而中段系统消息在检查之前就 flush 了；现在压在真正发出去的 assistant 消息之前。
- **Anthropic 逐消息 effort 时顶层固定发 `low`**：组织可以设 effort 上限，之前固定 `high` 会让
  低/中档请求也被整条拒掉；真实档位仍由逐条消息的 `output_config` 携带。
- **faux provider 把缓存写入又算进 input**：带 `sessionId` 时同一个 prompt token 同时进了
  `input` 和 `cacheWrite`，`input + cacheRead + cacheWrite` 超过估算的 prompt。现在缓存模拟期间
  `input` 恒为 0，`cacheWrite` 用 `promptTokens - cacheRead` 计算。
- **OpenCode 走了错的协议端点**：`/inference/v1` 是所有协议共用的旧地址，按上游改成
  `/inference/openai/v1`、`/inference/anthropic`、`/inference/google/v1beta`、
  `/inference/systemone/v1`（Go 侧同理加 `/go` 前缀）。顺带把 `deepseek-v4.1-flash` 的
  `supportsLongCacheRetention` 关掉——网关会拒 `prompt_cache_retention`。
- **`pi.exec` 吞掉 spawn 失败原因**：`Process.Start` 找不到可执行文件时抛 `Win32Exception`，
  之前整个方法直接抛出去；现在按 Node 的 reject 分支返回 `code 1` 并把错误信息放进 `stderr`。
- **一个装不上的扩展包会拦住整个启动**：`settings.json` 里的包因为注册表不可达、包不存在或
  git prepare 失败时，解析阶段抛异常直接崩；现在跳过该包、记一条 warning（带 `pi remove`
  提示），下次启动或 `/reload` 重试。
- **grep 上下文行在孤立 CR 的文件里整体偏移**：取上下文时把 CRLF 和单个 CR 都折成了换行，
  而 ripgrep 只按 LF 计行；现在只归一 CRLF。
- **provider id 含 `/` 时 `provider/id` 解析不到**：`llama-server=http://host:10800` 这类
  URL 派生的 id 会在第一个 `/` 处被切断；改成在已知 provider id 里做最长匹配。
- **深层嵌套引用块渲染爆炸**：宽度被嵌套耗尽后仍按 1 列逐层重排，80 列下 50 层要 30 秒、
  55 层直接栈溢出；现在放不下边框时按原宽直接渲染子节点。语料里修复前的引用块向量改为
  由专门的深度用例覆盖（`MarkdownBlockquoteDepthTests`）。
- **隐藏 thinking 且标签为空时留下一行空白**：空标签的隐藏块现在整段不渲染，也不再占一个
  空行；有标签时保持原来的标签行与间距。
- **工具搜索不优先点名匹配**：查询就是工具名（或 MCP 名的 `__` 末段）时，长度归一化会把描述里
  恰好含这些词的短文档排到前面；现在点名匹配排最前，其余仍按 BM25 分数排。

## [1.4.3] - 2026-10-24

### Added

- **工具结果图片的跨 API 回归**：这条链路（工具返回截图后，模型看到的是"文本占位 + 附着的图片"）
  此前只有 Anthropic 有断言，其余四个 API 的 wire 逻辑没有覆盖。新增 `ToolResultImageTests`（17 例）
  钉住 openai-completions（提成 user 消息、`requiresAssistantAfterToolResult` 插 assistant、
  不支持图片时由共用变换层追加 `(tool image omitted: …)`）、Google（`functionResponse.parts`，仅
  Gemini 3+）、responses（`input_image` 数组 vs 文本占位）、Mistral（六种占位组合）的三态行为。
  另补 `AnthropicOpenRouterCostTests`（起 SSE server 验证成本接入的取数位置）与
  `EveryChatModelKeepsItsCoreFields`（1599 个 chat 模型的 api/name/input/cost/contextWindow 零缺失）。
- **durable 的内置图片处理器**：上一版只补了 `read` 的图片分支与 `IImageProcessor` 契约，宿主不注入
  处理器时 BMP 仍只能报错、超像素的图也报错。现在 `Pi.CodingAgent/Utils/Image/SkiaImageProcessor.cs`
  给出默认实现（解码 → EXIF 摆正 → 缩放到限制 → PNG/JPEG 编码 → 按 3/4 递减尺寸直到字节达标），
  由 `DurableHarnessSetup` 注入 `CodingTools.Create(...)`，对应上游 `createCodingTools({ images })`。
  配套给 `ImageResizeOptions` 加 `ForceReencode`：BMP 这类 provider 不能内联接收的格式必须重编码，
  否则缩放核心的快路径会把它原样透传。
- **OpenRouter 上报成本**：`UsageCost.TotalFromProvider` + `Utils/OpenRouterCost.cs`
  （`IsOpenRouterModel` / `ApplyReportedCost`）。走 OpenRouter 时非 BYOK 直接用上报的 `cost`，
  BYOK 再叠加 `upstream_inference_cost`（缺失时用 prompt + completions 两段之和），
  `is_byok` 缺失或成本非法则保持目录价。接入 `AnthropicMessages` 的 `message_delta` 与
  `OpenAiCompletions.ParseChunkUsage`；`CalculateCost` 会把这个标记重置为 false。
  此前这类请求只按目录价计入，BYOK 场景下真实开销完全不进 `cost.total`。
- **42 家 provider 的完整模型目录（1713 个条目）**：此前 `src/Pi.Ai/ModelData/` 只有三个手写文件、
  8 个条目（`gpt-4o` 那批已被上游淘汰的模型），而 `ModelResolver.DefaultModelPerProvider` 的 41 条
  默认模型是照上游新 id 抄的——**41 条里一条都解析不到**，全新安装遍历完整张表也选不出模型。
  数据取自 `@earendil-works/pi-ai@1.4.0` 的 `dist/providers/data/`（上游 `build:offline` 会把
  gitignore 的 `src/providers/data` 复制进发布包），连同 `.manifest.json`（`generatedAt` 供
  `GetBuiltinModelDataGeneratedAt` 判断目录新旧）。现在 openai 42 chat + 1 classifier、
  openrouter 401 chat + 63 image + 28 classifier、anthropic 17、radius 32 基线，默认表 41/41 全部可解析。
  未识别字段（`enabled` / `lab` / `providers` / `output` / `promptCache` / `free`）进 `Extra` 原样保留
  ——上游的 `model-catalog.ts` / `models.ts` 也不消费它们。新增回归
  `EveryEmbeddedCatalogParses`（42 份都能解析、总数 1713）与
  `EveryDefaultModelResolvesInTheBuiltinCatalog`（默认表逐条可解析）。
- **OpenAI Decisions 分类器（`src/Pi.Ai/Api/OpenAiDecisions.cs`）**：整块补上上游
  `api/openai-decisions.ts`。此前 OpenAI 是唯一**没有**注册 `classifiers` 的 provider——System One
  的另外五个承载（Cloudflare Workers AI、TypeSafe、OpenRouter、OpenCode、Vercel AI Gateway）都在，
  唯独 `POST /v1/decisions` 这条路缺失，`model.Api = "openai-decisions"` 会落到「不支持分类」。
  逐条对齐上游：状态以紧凑 JSON 文本作 `input`；带图时变成一条 user 消息（`input_text` 后跟
  `input_image` data URL，上限 128 张）；`choice`/`score`/`bool` 分别映射到
  `choice`/`score`/`predicate`，predicate 没有 criteria 字段，true/false 的含义并进 `instructions`；
  概率是 `[{value, probability}]` 数组而非对象；refusal 是错误结果而不是答案；usage 在解析答案
  **之前**读（畸形答案的请求同样已计费）。连带补 `CreateProviderOptions.FilterAllModels`
  （上游 `filterAllModels`）：ChatGPT 订阅 token 到不了 Decisions 路由，因此 OAuth 凭据下分类
  模型不可见。
- **`ProviderRetryOptions.NoRetryStatuses`**：上游 `retryProviderRequest` 的 `noRetryStatuses`。
  Decisions 用它把网关 504 挡在重试之外——超大输入（约 60 万 token 以上）会稳定撞上同一条时间
  上限，重试只是再撞一次；504 另给固定的超时文案而不是通用错误串。
- **`Pi.Durable` 的 `read` 支持图片**（`tools/read.ts` 的 `readImage`）：此前遇到图片直接返回
  `unsupported_image`。现在按上游两条路径处理：给了 `IImageProcessor` 就交它解码/摆正/缩放/重编码，
  并把「从 X 转成 Y」「从 AxB 缩到 CxD，坐标乘 N 倍」写成 `info` 诊断；没给处理器时，PNG/JPEG/
  GIF/WebP 在字节与像素限制内原样透传（BMP 等需要转换的格式报错并说明）。限制取自对话模型的
  `inputLimits.images.resize`，缺省 2000×2000 / base64 4.5 MiB；模型不支持图片时补一条占位说明。
  配套补齐 `ImageMime.ImageDimensions`（PNG IHDR / GIF 屏幕 / WebP VP8·VP8L·VP8X / JPEG 首个 SOF）
  与 `ImageEncoding`（`InlineImageTypes` / `ToBase64` / `Base64Length`）。`IToolExecutionApi` 新增
  `GetModel`，对应上游 `api.models.getModel`。附带修正读循环：上游允许「追加写」（`after.size >
  before.size` 时沿用已扫描的文本），端口此前一律重读并在第二次抛错。
- **telemetry 的 metrics 与 trace 渲染**：补 `metrics.ts`（`InstrumentOptions` /
  `HistogramOptions` / 三个 instrument 接口 / `ITelemetryMetrics` / `CreateTypedMetrics`，后者把
  schema 里的 description、unit、boundaries 绑到 instrument 上）与 `render.ts`
  （`TelemetryRender.RenderTrace`：按 `startedMs` 组成森林、父节点缺失视为根、每层两空格缩进，
  行尾带耗时与 error 详情，事件 `·`、链接 `→`）。`TelemetrySchemaDefinition` 新增可选
  `Metrics`；`InMemoryTelemetryContext` 新增 `GetStoredSpans()`（真实起止时间、事件偏移、本
  上下文的 trace id），让渲染有可喂的数据。

### Fixed

- **`azure` provider 的 id 仍是上游已废弃的旧名**：`Providers/AzureOpenAiResponses.cs` 写成
  `azure-openai-responses`，而上游早已把 provider 改名为 `azure`（CHANGELOG 明确要求同步
  `auth.json` / `models.json` / `settings.json` 里的 key），`azure-openai-responses` 现在只是 api 名。
  后果有三：`defaultProvider: "azure"` 找不到 provider、`models.json` 里 azure 的配置失配、
  `azure.json` 目录永远按 `Pi.Ai.ModelData.azure-openai-responses.json` 找不到。现在 id 与
  `ModelCatalog._knownProviders` 都改成 `azure`。
- **库被嵌入时 `Config.Version` 上报的是宿主版本**: 没有可读 `package.json` 时，版本回退取的是
  `Assembly.GetEntryAssembly()`——也就是**宿主程序**的程序集版本。而 `Assets/package.json` 是
  `Pack="false"`、不进 nupkg，所以「把 Pi.CodingAgent 当库装进来」的宿主**只**会走这条回退路径：
  一个 `1.0.0` 的宿主调 `Config.Version` 会拿到 `"1.0.0"`，并顺着 `BugReport` 的 version、
  `PiUserAgent` 的 UA、`ClientIdentity`、`CrashLog`、MCP client info 的 `Version`、启动横幅和
  `lastChangelogVersion` 一路扩散出去。上游 TS 是从模块自身目录读 `package.json`，`VERSION`
  永远是 pi 包的版本、与宿主无关；现在回退固定取 `typeof(Config).Assembly` 的
  `AssemblyInformationalVersion`（剥掉 `+<sha>`）。CLI 行为不变：它的 `package.json` 随包落在
  程序集旁边，本来就优先命中。回归用例
  `FallsBackToTheCodingAgentAssemblyRatherThanTheEntryAssembly` 把测试项目的 `<Version>`
  刻意设成 `0.0.1`——两者版本相同时两种实现结果一致，否则测不出这个区别。

## [1.4.2] - 2026-10-23

发 1.4.1 之后又攒到三处真实缺陷：一处让**所有带参数的 MCP 工具**不可用，一处让**自定义
provider** 的流式请求全部失败，一处会让 `pi` 整个进程崩掉。它们都在 `v1.4.1` 打 tag 之后
才修好，同样只能发新版本。

### Fixed

- **带参数的 MCP 工具全部失效（`The node already has a parent.`）**: 构造 `tools/call` 的
  `arguments` 时，`McpServerConnection` 把调用方给的 `JsonNode` 原样塞进新的 `JsonObject`。
  而 `Pi.Agent` 的 `ValidateToolArguments` 交出来的，正是**已经挂在参数对象下**的子节点——
  给已有父节点的 `JsonNode` 重新挂父会抛 `InvalidOperationException`，于是凡是需要参数的
  MCP 工具（`read_file`、`edit`、任何带 schema 的工具）一律调用失败，无参工具反而正常，
  很容易被误判成 server 端问题。现在遍历时逐个 `DeepClone()`，原参数对象保持不动。
  回归用例 `CallToolAcceptsArgumentValuesThatAlreadyHaveAParent` 断言发出的 `arguments`
  内容正确，且原子节点仍挂在原父节点上。
- **自定义 provider 流式报 `No API provider registered for api: …`**: 内置 API provider 的注册
  挂在 `CompatApi` 的静态构造函数里，只有走基类 composer 才会触发；而 `models.json` 里的自定义
  provider 直接查 API 注册表，从不经过那条路径。于是端到端测试一对真实 OpenAI 兼容端点发请求就
  抛 `No API provider registered for api: openai-completions`。现在流式回落路径会确保注册
  （幂等，不覆盖已有条目）。回归用例改为对一个不可达端点流式请求，断言失败落在连接层而不是
  注册表。
- **`Box` 子组件列表与渲染缓存缺少并发防护**（T35）：渲染定时器线程枚举 `Box.Children`
  （`Render` / `Invalidate` / `HandleMouse`）的同时，agent 事件线程增删工具卡片 / 图片子组件，
  抛 `Collection was modified; enumeration operation may not execute`，未捕获即整个 `pi`
  进程退出。缓存命中路径另有 `_cache` 先判后用被并发置 null 的 `NullReferenceException`
  竞态。`Box` 现在与 `Container` 同策略：锁内快照枚举 + 版本号守卫的命中测试布局 +
  缓存单次读取。新增确定性门控与三线程锤击两条并发回归（`ComponentTests`）。

### Changed

- 仓库迁移到 `AIDotNet/pi.net`：readme / README 徽章、文档互链、`Directory.Build.props` 的
  `PackageProjectUrl` / `RepositoryUrl` / `Description`、CodingAgent 内置 docs 索引与宣传片页脚
  全部改指新地址。旧地址 `xuzeyu91/pi.net` 由 GitHub 301 重定向兜底；本次发版起，随 nupkg
  打包的包内 README（`PackageReadmeFile`）也指向新地址。NuGet 包 ID 未变，
  `nuget.org/packages/Pi.*` 链接与安装命令无需调整。

## [1.4.1] - 2026-10-23

修掉 1.4.0 已经发出去的三处缺陷。它们在 `v1.4.0` 打 tag 之后才修好，而 nuget.org 的
`id + version` 不可覆盖，所以只能发新版本——1.4.0 的那批包仍带这些 bug，建议 unlist。

### Fixed

- **终端输入管线根本没接线（C383）**: `StdinBuffer` 此前只在单测里被实例化，生产路径只有
  一个「状态查询期间才生效」的临时聚合在兜着。于是终端对 OSC 10/11/4 配色查询的应答
  （`rgb:cccc/cccc/cccc` 之类 + 16 色调色板）被 `Console.ReadKey` 逐键送达后逐字符漏进编辑器，
  同时 `QueryTerminalColorsAsync` 超时——Windows 上的主题明暗探测实际失效。现在管线是
  `MapKey` → `StdinBuffer.Process` → `Data`/`Paste`，并新增 `ResolveEscapeTimeoutMs`
  （`PI_TUI_ESC_TIMEOUT`，SSH 下自动加宽到 100 ms）。附带两处：重定向输入分支对单个代理项调
  `char.ConvertFromUtf32` 会抛 `ArgumentOutOfRangeException`，改为按 UTF-16 码元喂缓冲；
  `Stop()` 现在会释放缓冲与其冲刷计时器。
- **备用屏退出复原漏了前导复位（C384）**: 退出复原的前缀只走基类版本，缺了同步输出复位、
  关闭鼠标上报、恢复自动换行三项。跳过 `stop()` 的退出路径（Ctrl+C 打断登录对话框、
  `Environment.Exit`）走 `AppDomain.ProcessExit` 兜底时，`\x1b[?1003h` 一直开着，
  Windows Terminal 持续把 `\x1b[<35;x;yM` 运动事件写进控制台输入，父 shell 当键入回显——
  表现为「Ctrl+C 退出后终端一直刷乱码」。已按上游覆写前缀（仅在备用屏激活时下发）。
- **OpenAI Responses 鉴权缺失（C170）**: `apiKey` 没有被注入请求头，`Authorization: Bearer`
  只在调用方自己传头时才存在，直接用 API key 调用会 401。现在 `ApiKeyHeader` 显式把它并入
  头合并，保留 `"unused"` 哨兵语义避免重复写入。
- **启动横幅与 `--version` 仍报 `v1.0.4`**: `Config.Version` 沿用 TS 语义，优先读随包分发的
  `src/Pi.CodingAgent/Assets/package.json` 的 `version`，而 1.4.0 发版只改了
  `Directory.Build.props` 的 `<Version>`（只影响程序集版本，是回落路径）。该资产已重新从上游
  `v1.4.0` 同步（含依赖版本与 `schemas` 条目），`Assets/CHANGELOG.md` 一并对齐，`/changelog`
  不再停在 1.0.4。新增构建期守卫 `ValidatePackagedVersion`，两处版本不一致直接报错。

## [1.4.0] - 2026-10-23

Version alignment with upstream Pi `1.4.0`: the port now tracks the newer stable
release line, with the user-visible fixes from upstream `1.1.0`–`1.4.0` backported
in this batch. The full inventory and the remaining backlog live in
[`docs/upstream-1.4.0-delta.md`](docs/upstream-1.4.0-delta.md).

### Added

- **Usage docs** (`docs/usage/`): per-package guides for consuming the 13 NuGet packages from C# —
  which package for which job, minimal working code, credential sources, and pitfalls. Covers
  `Pi.Ai`, `Pi.Agent`, `Pi.CodingAgent`, `Pi.Mcp`, plus one-liners for the remaining nine.
- **Runnable samples** (`samples/`): four console projects registered in `Pi.slnx`, so CI compiles
  them and the documented APIs cannot silently drift. `Pi.Samples.FauxAgent` runs the full agent
  loop with a scripted provider (no API key), `Pi.Samples.RealModel` streams from a real provider
  through both the legacy `Model` entry and the catalog `ModelSpec` path,
  `Pi.Samples.CodingSession` embeds a coding agent via `Sdk.CreateAgentSessionAsync`, and
  `Pi.Samples.McpClient` drives an MCP server over stdio or streamable HTTP.

### Fixed

- **Upstream 1.1.0–1.4.0 backport (first batch)**: eleven user-visible fixes from the newer
  stable releases, with the full inventory and the remaining backlog in
  `docs/upstream-1.4.0-delta.md`.
  - `grep` runs ripgrep with `--no-config`, so `RIPGREP_CONFIG_PATH` (e.g. `--fixed-strings`)
    can no longer silently turn regex searches into literal ones (upstream #11203).
  - `mcp.json` files that start with a UTF-8 BOM are parsed instead of skipping every server
    (upstream #11191).
  - `~` is expanded in `extensions` / `skills` / `prompts` / `themes` patterns, so `+~/x`,
    `-~/x` and `!~/skills/pdf` match (upstream #10765).
  - Empty quoted prompt-template arguments (`""`, `''`) are kept, so later positional
    arguments no longer shift and positional defaults apply (upstream #10867).
  - Print mode keeps printing the partial text but warns on stderr and exits `1` when the
    response stopped at the model's output token limit (upstream #10799).
  - `PI_CLIPBOARD_OSC52=1` forces OSC 52 copies for remote clients that leave no SSH markers,
    such as multiplexer attaches or remote IDE daemons (upstream #10826/#10835/#10114).
  - Compaction, turn-prefix and branch summaries no longer derive a fixed output cap from
    `reserveTokens`; the provider sizes them from the request, so long reasoning can no longer
    make summarization fail with "generation hit the token cap" (upstream #9512/#9075/#10908).
  - Whitespace-only tool output (a bare `\r\n` from bash) uses the empty-output placeholder on
    OpenAI Completions, OpenAI Responses and Google instead of being sent verbatim (#8720).
  - Invalid `\u` escapes such as `\usepackage` are kept as literal text, so tool-call arguments
    no longer lose the field and everything after it (upstream #10868).
  - Retry classification matches the newer upstream table: HTTP status codes only count as
    standalone numbers, and gateway/Codex-proxy transient failures (`unable to forward upstream`,
    `upstream_stream_read_error`, `stream disconnected before completion`, Vertex OAuth refresh
    failures) are retried (upstream #10920/#10965/#10996/#11057/#10842).
  - A display-math block can no longer span a GFM table, so an unclosed `$$` stops swallowing
    tables while streaming (upstream #11159).
- `readme.md` / `readme.zh-CN.md` links now use absolute GitHub URLs. Relative links were rewritten
  into self-links by nuget.org, so every doc link on the package page was dead.
- Dropped the `-m:1` recommendation from the build-from-source snippet: serial execution is slower
  and reproduces two `Pi.Ai.Tests` failures plus a multi-minute `Pi.CodingAgent.Tests` hang that a
  parallel build does not.

## [1.0.4] - 2026-10-21

Initial stable release of the .NET port. Full functional parity with the upstream
TypeScript release `1.0.4` (baseline `eb326d265`), audited end to end:

- **13 runtime projects**: `Pi.Ai`, `Pi.Agent`, `Pi.Tui`, `Pi.CodingAgent` (+ `Pi.Cli`,
  `Pi.Vacation`, `Pi.DurableHost`), `Pi.Durable`, `Pi.Chord`, `Pi.Server`, `Pi.Client`,
  `Pi.Mcp`, `Pi.Protocol`, `Pi.Env`, `Pi.Telemetry`, `Pi.Codemode`, `Pi.Evals`.
- **Public API & wire audit**: all 1,426 barrel exports of the 13 source packages mapped
  (`docs/public-api-audit.md`); OpenAI / Anthropic / MCP wire shapes locked by test suites.
- **Tests**: 30,103 items across 13 test projects, 0 failures (5 platform skips on Windows);
  container (Docker 29.7.1) and POSIX/SSH acceptance completed on external Ubuntu 24.04.
- **Radius catalog persistence**: dynamic provider refresh now writes back through the models
  store (`RadiusProvider` implements `IProvider.RefreshModels`), superseding the in-memory-only
  note from the 1.0.4 baseline.
- **Differences from TypeScript** are registered with IDs in
  `docs/coding-agent-porting-status.md` (C-series) and `docs/tui-porting-status.md`
  (T-series), with an independent review in `docs/review-inconsistencies-2026-10-21.md`.
  User-visible ones:
  - Mermaid diagrams render as plain text (no bundled renderer; `IMermaidRenderer` seam).
  - Word-wise cursor movement treats a run of CJK characters as one word (no ICU dictionary
    segmentation in .NET); ASCII behavior matches upstream.
  - Extensions cannot rewrite `tool_call` input in place (block/terminate instead).
  - Bun/Node runtime shims (`bun/*`, jiti loaders, virtual modules, lazy loaders) are
    intentionally not ported; experimental plugin bundles run on a Jint engine.
