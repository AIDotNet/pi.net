# Pi → .NET 迁移执行状态

本文是当前迁移清单、优先级、阶段验收标准和执行记录的权威入口。旧的
[migration-plan.md](./migration-plan.md) 与 [porting-status.md](./porting-status.md)
保留历史阶段说明；其汇总快照早于当前代码，不应用来推断当前完成度。

## 基线与范围

- TypeScript 源仓库：`D:\AI\参考项目\pi`，`main`，基线 `eb326d265ae0b88489a6d10319307780df827cdf`。
- .NET 目标仓库：`D:\AI\参考项目\pi.net`，`main`，基线 `28aed27a515c6f87c3670ffcd10fd867fd7060c6`。
- 目标仓库起始工作区干净并与 `origin/main` 同步。源仓库的未跟踪 `.workbuddy/` 属于原有内容，本轮保持不动。
- 源包含 14 个 package；目标已有 13 个运行时项目。`env` 并入 `Pi.Durable/Env`，`Pi.Cli` 是 coding-agent 的 .NET 进程入口；目前没有 `evals` 目标项目。
- 逐阶段代码记录比旧的逐包汇总新：coding-agent 已覆盖交互/RPC 模式、CLI 和多个实验运行时；最近的实验客户端批次为 4e-3c-3。以下按源/目标代码和剩余事项描述，不把旧台账中的早期百分比当作当前状态。

## 逐包迁移矩阵

| TypeScript package | .NET 目标 | 当前判断 | 未完成项 / 明确取舍 |
|---|---|---|---|
| `telemetry` | `Pi.Telemetry` | 已迁移 | 契约、参考适配器和 conformance 覆盖已在仓库记录。 |
| `protocol` | `Pi.Protocol` | 已迁移 | CBOR/framing/codec 已移植并有线上兼容测试。 |
| `agent` | `Pi.Agent` | 已迁移 | Agent 状态机、循环、工具执行和 proxy 已移植。 |
| `mcp` | `Pi.Mcp` | 已迁移 | JSON-RPC、传输与 OAuth 已实现；部分网络测试受本机 loopback/监听限制。 |
| `server` | `Pi.Server` | 已迁移 | RPC 与本地发现已移植；Unix socket 等平台边界仍需最终跨平台验收。 |
| `client` | `Pi.Client` | 已迁移 | 客户端协议与传输已实现。 |
| `ai` | `Pi.Ai` | 已迁移 | Provider 与模型层已广泛移植；依赖本地浏览器回调的 OAuth 测试不适合无头环境。 |
| `chord` | `Pi.Chord` | 已迁移 | 核心协议/服务/Facet 已实现；`index.ts` 是导出桶，不要求在 C# 复制。 |
| `codemode` | `Pi.Codemode` | 已迁移 | VM 执行由 `ICodemodeJsEngine` 注入；默认实现明确拒绝未配置的执行，不伪装为成功。 |
| `env` | `Pi.Durable/Env` | 已迁移 | 合并在 Durable 项目中。 |
| `durable` | `Pi.Durable` | 核心实现已迁移，差分工作仍在进行 | Memory/SQLite storage、Session 与大部分 harness/runtime 已有实现；`contextRetentionMs`/idle context-cache policy 和部分 harness/recovery 差分测试仍缺。 |
| `tui` | `Pi.Tui` | 基本完成 | 44/45 个源文件已覆盖；剩余 `index.ts` 是 barrel/export 文件，公开面已逐项核对。 |
| `coding-agent` | `Pi.CodingAgent` + `Pi.Cli` | 部分迁移，主体已落地 | 交互模式、RPC、CLI、实验客户端/服务端及 Durable session-worker 接线已有实现。待补 package-manager 更新与告警/路径链路、MCP/Llama 管理 UI、`vacation/*`，并判定 Bun 专属入口如何映射到 .NET。 |
| `evals` | 尚无项目 | 未迁移 | 约 1.4k 行源代码；依赖 coding-agent，安排在其运行时和 harness 前置完成后。 |

## 剩余事项与优先级

| 优先级 | 工作项 | 主要源范围 | 依赖 / 理由 |
|---|---|---|---|
| **P1** | Package manager 与扩展资源链 | `core/package-manager.ts`、CLI `-e`、resource-loader、扩展告警路径 | 补 `update` / `hasAvailableUpdate`、git ref/marker 更新、`collectExtensionPackageWarnings`，并核清 `additionalExtensionPaths` 从 CLI 到包解析及资源加载的完整传递。已有的路径字段/本地路径处理不代表整条链已完成。 |
| **P1** | MCP / Llama 管理 UI | MCP `ui.ts`；Llama `ui.ts` | 当前 C# 是调用缝/拒绝桩，不是完整 UI；补 `McpManagerView` / `showMcpManager` 与 `LlamaView` / `HuggingFaceSearch` / `showLlamaUi`，依赖已落地的 Interactive TUI 主题和组件。 |
| **P1** | Vacation workflow | `experimental/vacation/*`，6 个文件、约 1.3k 行 | 独立实验功能，需逐行为移植并补测试。 |
| **P1** | Bun / 客户端平台范围判定 | `bun/*`、`client/*` 及入口相关代码 | 不能把 Bun 运行时垫片直接当成 .NET 功能缺失。需先核对可见行为，再记录取舍并验证 Windows/POSIX 对应行为；不允许静默删去用户可见能力。 |
| **P2** | Durable harness/cache/recovery 差分 | `contextRetentionMs`/idle context-cache policy；`harness-compaction`、`structured`、`tasks`、`ownership`、`tasks-recovery`、`conversations`、`live-deltas`、`tools-recovery`、`generation-recovery` | 旧台账估算约 8.9k 行 TS 测试。SQLite 和 worker 接线已完成；仍按测试文件逐批对齐行为和缓存边界，不能用代码行数替代覆盖。 |
| **P3** | Evals package | `packages/evals`（约 1.4k 行） | 依赖 coding-agent 可调用入口及实验 harness；在目标新建项目、CLI/程序集引用和测试。 |

## 分阶段计划与验收

每阶段均先检查 TypeScript 对应实现/测试，再做最小范围 C# 实现；完成后跑目标构建和该阶段测试、记录差异与未覆盖边界，再单独提交并推送该阶段。若验证失败，不得把失败测试标成环境限制，除非有可复现证据并将证据写入记录。

| 阶段 | 目标与文件范围 | 验收标准 | 状态 |
|---|---|---|---|
| **0. 基线与盘点** | 核对源/目标 commit、项目映射、旧台账与可复现基线；更新本文件及旧文档入口。 | 13 个目标项目/14 个源包的映射清楚；构建基线记录准确；工作区无临时调试改动。 | 已完成并提交、推送（`47dcfe7`）。 |
| **1. SQLite storage** | `Pi.Durable/Storage` 新增 SQLite schema/migration 与实现；`Pi.Durable.csproj` 添加 SQLite provider；新增 `Pi.Durable.Tests` SQLite/conformance 测试。 | schema migration 可重复/原子；事务失败可回滚；ID 全局冲突规则、排序/分页/cursor、fork 可见性、文档历史/当前点、重开恢复和 close 语义与 TS 一致；全部 storage conformance 通过；0 警告构建。 | 已完成并推送（`0546fc3`）；原全量套件挂起在阶段 2 修复并复验通过。 |
| **2. Harness setup 与 worker** | `Pi.CodingAgent/Experimental`、Durable harness 及 Session 入队；SQLite-backed `createCodingAgentHarness`、worker process role/spawn/shutdown/reopen。 | 真实子进程覆盖 ready/demand/operation/shutdown、锁释放和重开持久性；worker 输入/控制错误、操作取消和 manager 超时有定点测试；compaction safe-integer 范围不被收窄；相关测试及 Durable 全量套件通过。 | 已完成并随阶段提交推送；执行细节见下表。 |
| **3. Package manager/资源链** | `Core/PackageManager*`、`ResourceLoader`、CLI/settings/resource 装配及扩展告警。 | 更新检查/执行与 marker/ref 状态、告警排序/内容、额外路径解析和缺失路径错误均有 TS 对照测试；CLI `-e` 端到端通过。 |
| **4. MCP/Llama UI** | `Extensions/Mcp`、`Extensions/Llama` 与必要的 Interactive 组件/注册点。 | 命令触发、视图生命周期、键盘交互、主题渲染、取消/错误路径覆盖；extension manager 默认行为与 TS 相同。 |
| **5. Vacation** | 新增对应 `Experimental/Vacation` 模块及测试。 | 六个源文件的公开行为和边界逐一映射；相关测试通过；无占位实现。 |
| **6. Bun/platform 判定与适配** | `bun/*`、`client/*`、CLI/环境/进程入口。 | 在本阶段动工前记录以下决策：A. 为 .NET 重造 Bun 兼容层；B. 不移植运行时专属打包器/启动垫片，但保留并验证其可观察 CLI、进程、文件和平台行为。按当前目标建议 B，因为目标不是 Bun/JavaScript runtime；只有源文件存在 .NET 用户可见语义时才实现对应 .NET 行为。 |
| **7. Durable test parity** | `tests/Pi.Durable.Tests` 中尚缺的 harness/recovery 测试，逐个对应列出的 TS 文件。 | 所有适用 TS 测试均有 C# 等价断言，或有逐项记录的不可移植理由；完整 Durable 套件可稳定完成，不把无限挂起视为通过。 |
| **8. Evals** | 新增 `Pi.Evals`（如源包实际公开面要求）及 `Pi.Evals.Tests`，接线 solution/CLI。 | 评测选择、执行、结果格式、失败/超时语义与 TS 对照；新项目和全 solution 构建通过；测试覆盖源包契约。 |
| **9. 最终一致性与交付** | 全包差异审计、迁移文档收口、所有提交和远端同步。 | `dotnet build Pi.slnx -m:1` 成功；目标测试按环境可运行范围全部通过，剩余受限测试有隔离证据；源/目标公开接口与 JSON/wire 形状核对完；工作区干净且提交已推送。 |

### 特殊范围决策

- SQLite 实现移植的是 portable SQLite storage contract；Node `node:sqlite`、Cloudflare binding 等 JS 适配器不逐字移植，而由 `Microsoft.Data.Sqlite` 提供 .NET 后端。存储可观察语义、schema 演进和失败行为仍必须一致。
- Bun 入口处理按阶段 6 的显式决策执行；“目标不是 Bun”本身不能作为删除 CLI 或平台行为的理由。
- `index.ts` 纯导出桶、Node/Bun 专属测试启动器等无独立 C# 语义的文件可不产生一一对应文件，但要在最终公开 API/平台矩阵中有可复核的逐项说明。

## 验证基线、风险与限制

- `dotnet build Pi.slnx -m:1 --no-restore`：本轮基线通过，0 警告、0 错误。
- `dotnet test --solution` 在当前 SDK/Microsoft.Testing.Platform 配置下不能作为入口；本机可直接执行 `tests\<项目>\bin\Debug\net10.0\<项目>.exe` 并使用 MTP 的 `--filter-method`、`--filter-class` 等筛选参数。每阶段记录实际命令和结果。
- `Pi.Durable.Tests` 的阶段 1 全量执行曾卡在 inbox 用例；阶段 2 将测试 fixture 的默认扩展选择校正为 TS 的 undefined 语义，并修复了 `Session.Enqueue` 并发调用时 `_tail` 链接非原子的竞态。阶段 2 最终全量复验：370 项通过、2 项因本机缺少可用 Git Bash 而跳过、0 失败；期间两次全量运行中的时序敏感输出测试曾分别偶发失败，单测复跑及之后的全量复验通过，故保留为时序稳定性观察项而非环境豁免。
- `HarnessInspectTests.Inspect_DerivesEveryLiveTaskStateWithoutRunningTaskCode` 先前在完整执行并移除临时诊断后单测通过（1/1）；harness inbox 挂起应在 worker/harness 阶段继续定位。
- 既有逐阶段记录报告 coding-agent 全量 4,797 项通过；本轮串行复测曾有 1 项失败，但 `UnknownFlagIsNotAnErrorOnItsOwn` 单测通过，失败详情未保留，需在最终审计时复跑并捕获完整报告。
- AI Radius OAuth 的浏览器 loopback 用例可能在无头环境持续等待；本机 `HttpListener`/Unix socket 测试还受权限和路径长度影响。只有逐项复现并记录后，才能把它们列为环境限制。
- 旧文档的 package 状态表和总测试数存在过期快照；本文件优先记录当前基线和逐阶段提交，历史记录不覆盖当前实测。

## 执行记录

| 阶段 | 结果 | 构建/测试 | 提交与推送 |
|---|---|---|---|
| 0. 基线与盘点 | 完成源/目标仓库与项目映射盘点，建立剩余迁移优先级、阶段范围、验收标准和风险入口。 | solution build 基线通过；Durable inspect 定点测试通过。 | `47dcfe7 docs(plan): refresh migration status and remaining phases`，已推送 `origin/main`。 |
| 1. SQLite storage | 补齐 SQLite schema/migration、事务执行器、持久化存储、扫描顺序/cursor 语义及 TypeScript JSON wire 兼容；增加重开、损坏数据、迁移失败回滚、历史点、current-only revision 回收与 WAL close 测试。 | `dotnet build Pi.slnx --no-restore -m:1`：0 警告、0 错误；`SqliteStorageTests`：8/8；`SqliteStorageConformanceTests.PassesStorageConformanceSuite`：23/23；`MemoryStorageConformanceTests.PassesStorageConformanceSuite`：23/23。全量 `Pi.Durable.Tests.exe` 达到 323 通过、2 跳过后在上述 harness inbox 用例挂起超过 15 分钟，已手动停止；阶段定点验收通过，但 Durable 全量套件仍待修复/复验。 | `0546fc3 feat(durable): add SQLite storage`，已推送 `origin/main`。 |
| 2. Harness setup 与 worker | 生产 Worker setup 接入 SQLite、SettingsSource、prompt、模型与插件 facet；CLI 内部 role 负责真实子进程启动/退出。另修复 Session 队列挂接竞态，并将 Harness compaction budgets 扩至与 TS 安全整数一致的 `long`。 | `dotnet build Pi.slnx --no-restore -m:1`：0 警告、0 错误；Worker/entry/manager/protocol/provider 定点测试 74/74；真实 CLI/SQLite restart E2E、Worker setup/settings 与 prompt 测试 4/4；完整 `Pi.Durable.Tests.exe` 最终 370 通过、2 Bash 环境跳过、0 失败。时序敏感测试曾在中间运行偶发失败，单测重跑与最终完整套件通过。 | 本阶段实现与验收记录同一提交，已推送 `origin/main`。 |
| 3–9 | 尚未开始。 | — | — |
