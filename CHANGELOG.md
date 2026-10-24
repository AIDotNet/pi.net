# Changelog

All notable changes to `pi.net` — the .NET 10 port of [pi](https://github.com/earendil-works/pi) — are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versions track the upstream `packages/*` release line.

## [Unreleased]

### Added

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
