# Pi → .NET 迁移执行状态

本文是当前迁移清单、优先级、阶段验收标准和执行记录的权威入口。旧的
[migration-plan.md](./migration-plan.md) 与 [porting-status.md](./porting-status.md)
保留历史阶段说明；其汇总快照早于当前代码，不应用来推断当前完成度。

## 基线与范围

- TypeScript 源仓库：`D:\AI\参考项目\pi`，`main`，基线 `eb326d265ae0b88489a6d10319307780df827cdf`。
- .NET 目标仓库：`D:\AI\参考项目\pi.net`，`main`，基线 `28aed27a515c6f87c3670ffcd10fd867fd7060c6`。
- 目标仓库起始工作区干净并与 `origin/main` 同步。源仓库的未跟踪 `.workbuddy/` 属于原有内容，本轮保持不动。
- 源包含 14 个 package；目标在本轮前有 13 个运行时项目，Stage 5 新增 `Pi.Vacation` 后现有 14 个。`env` 独立为 `Pi.Env`（见 `docs/env-package-migration.md`；此处原记「并入 `Pi.Durable/Env`」有误，`packages/env` 是基于 SSH + 守护进程的**远程**执行环境，与 Durable 里的执行环境抽象不是一回事），`Pi.Cli` 是 coding-agent 的 .NET 进程入口；Stage 8 新增独立评测工具项目 `Pi.Evals`（不计为运行时 package）。
- 逐阶段代码记录比旧的逐包汇总新：coding-agent 已覆盖交互/RPC 模式、CLI、多个实验运行时及独立 Vacation 宿主；最近的实验客户端批次为 4e-3c-3。以下按源/目标代码和剩余事项描述，不把旧台账中的早期百分比当作当前状态。

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
| `env` | `Pi.Env` | 客户端已迁移，ssh 端到端未在本机验证 | 协议连接、SSH 引导、远程环境与 watcher 已移植；守护进程按原生产物注入（差异 E-C1）。`Pi.Env.Tests` 47 项 / 46 通过 / 1 跳过（sshd 端到端需 POSIX 主机）。 |
| `durable` | `Pi.Durable` | 核心实现与 Stage 7 harness 一致性核对已完成 | Memory/SQLite storage、Session、context retention/cache、compaction、任务/会话恢复及 harness/runtime 均有实现和行为级测试；剩余平台验收与最终 wire/API 审计列入 Stage 9。 |
| `tui` | `Pi.Tui` | 基本完成 | 44/45 个源文件已覆盖；剩余 `index.ts` 是 barrel/export 文件，公开面已逐项核对。 |
| `coding-agent` | `Pi.CodingAgent` + `Pi.Cli` + `Pi.Vacation` | 部分迁移，主体已落地 | 交互模式、RPC、CLI、实验客户端/服务端、Durable session-worker 及 Vacation 已有实现。Package manager 更新、CLI `-e` 资源链及扩展依赖告警已在阶段 3 完成；MCP/Llama 管理 UI 已在阶段 4 完成；Vacation 已在阶段 5 完成；Stage 6 平台核对与宿主资源定位已完成；Stage 8 增加 .NET 扩展指南。Stage 9 已将随包 README 改为 .NET 说明，并明确上游 docs/examples 是 TypeScript 参考内容。 |
| `evals` | `Pi.Evals` | 实现完成，容器级验收未完成 | 已移植 CLI、host/documentation eval、任务发现/计划/报告和 Docker 隔离执行；单元测试已通过。当前环境无 Docker/Podman/nerdctl 或 WSL 发行版，镜像构建与容器 smoke 尚未验证。 |

## 剩余事项与优先级

| 优先级 | 工作项 | 主要源范围 | 依赖 / 理由 |
|---|---|---|---|
| **P1** | Evals 容器级验收 | `packages/evals` 与 `Pi.Evals/Docker` | Stage 8 已实现并通过构建/定点测试；需在具备 Docker 的环境构建两种镜像并执行隔离 eval smoke。 |
| **P2** | POSIX 运行时与公开契约最终核验 | `packages/client/src/unix.ts`、CLI/worker 入口及各包公开 API/wire 类型 | Stage 6 已完成 Windows 验收；仍需在 POSIX runner 验证 Unix transport，并完成剩余公开接口/JSON 形状的最终复核。 |

文档/示例范围已明确：随包 README 已改为 .NET 10 说明，文档索引和 README 均标明上游 docs/examples 面向 TypeScript/Node/Bun；C# 扩展使用仓库的 .NET 指南。上游示例作为参考保留，不冒充 .NET 示例，也不再作为未决适配项。

## 分阶段计划与验收

每阶段均先检查 TypeScript 对应实现/测试，再做最小范围 C# 实现；完成后跑目标构建和该阶段测试、记录差异与未覆盖边界，再单独提交并推送该阶段。若验证失败，不得把失败测试标成环境限制，除非有可复现证据并将证据写入记录。

| 阶段 | 目标与文件范围 | 验收标准 | 状态 |
|---|---|---|---|
| **0. 基线与盘点** | 核对源/目标 commit、项目映射、旧台账与可复现基线；更新本文件及旧文档入口。 | 13 个目标项目/14 个源包的映射清楚；构建基线记录准确；工作区无临时调试改动。 | 已完成并提交、推送（`47dcfe7`）。 |
| **1. SQLite storage** | `Pi.Durable/Storage` 新增 SQLite schema/migration 与实现；`Pi.Durable.csproj` 添加 SQLite provider；新增 `Pi.Durable.Tests` SQLite/conformance 测试。 | schema migration 可重复/原子；事务失败可回滚；ID 全局冲突规则、排序/分页/cursor、fork 可见性、文档历史/当前点、重开恢复和 close 语义与 TS 一致；全部 storage conformance 通过；0 警告构建。 | 已完成并推送（`0546fc3`）；原全量套件挂起在阶段 2 修复并复验通过。 |
| **2. Harness setup 与 worker** | `Pi.CodingAgent/Experimental`、Durable harness 及 Session 入队；SQLite-backed `createCodingAgentHarness`、worker process role/spawn/shutdown/reopen。 | 真实子进程覆盖 ready/demand/operation/shutdown、锁释放和重开持久性；worker 输入/控制错误、操作取消和 manager 超时有定点测试；compaction safe-integer 范围不被收窄；相关测试及 Durable 全量套件通过。 | 已完成并随阶段提交推送；执行细节见下表。 |
| **3. Package manager/资源链** | `Core/PackageManager*`、`ResourceLoader`、CLI/settings/resource 装配及扩展告警。 | 更新检查/执行与 marker/ref 状态、告警排序/内容、额外路径解析和缺失路径错误均有 TS 对照测试；CLI `-e` 端到端通过。 | 已完成并推送（`1d8e077`）。 |
| **4. MCP/Llama UI** | `Extensions/Mcp`、`Extensions/Llama` 与必要的 Interactive 组件/注册点。 | 命令触发、视图生命周期、键盘交互、主题渲染、取消/错误路径覆盖；extension manager 默认行为与 TS 相同。 | 已完成并推送（`35d9723`）。 |
| **5. Vacation** | 新增 `Pi.CodingAgent/Experimental/Vacation`、`Pi.Vacation` 宿主及定点测试；映射 `sessions.ts`、`vacation.ts`、`harness-setup.ts`、`runtime.ts`、`tui.ts`、`main.ts` 与 README 的用户文档。 | 会话按 cwd 隔离、continue 选最新有效目录、独占锁释放、Research/Search task 和 request ID 幂等、控制器及 TUI 交互/状态、压缩反馈、仅支持 `--continue`/`-c` 的宿主入口均有对应实现与测试；全 solution 0 警告构建。 | 已完成并提交（`1b26f0f`，与 Stage 5 台账提交一并推送）。 |
| **6. Bun/platform 判定与适配** | 检查 `bun/*`、`packages/client/*` 和 CLI/环境/进程入口；核对 `Pi.CodingAgent` 包资源，并验证 .NET 宿主与传输。 | **决策 B**：不移植 Bun 专属编译/打包器或运行时启动垫片；.NET 使用原生宿主与发布流程，保留可观察 CLI、子进程角色、环境继承、资源定位和客户端传输行为。按源测试核对 Unix discovery、握手/数据完整性、超时、失败边界和 post-exit stdio idle grace；README、CHANGELOG、package metadata、docs、examples 和交互图片均随宿主输出。Windows UDS 是有记录的 .NET 扩展行为。 | Windows 验收完成；POSIX 运行时验证留待阶段 9。 |
| **7. Durable test parity** | `Pi.Durable/Harness` 的 context retention/cache、compaction、任务/会话和 generation/tool recovery；核对 `harness-compaction`、`structured`、`tasks`、`ownership`、`tasks-recovery`、`conversations`、`live-deltas`、`tools-recovery`、`generation-recovery`。 | 各源测试套件的可观察状态迁移、边界和恢复行为均有 C# 等价断言（允许将重复场景合并到行为级测试）；不可移植项有可复现理由；完整 Durable 套件可稳定完成。 | 已完成并推送（`eb9d195`）。 |
| **8. Evals** | 新增 `Pi.Evals` 及测试项目，接线 solution/CLI；移植 host/docs eval、CLI、scenario/task/report、Docker supervisor/worker 与 .NET 扩展文档。 | 评测选择、执行、结果格式、失败/超时语义与 TS 对照；新项目和全 solution 构建通过；测试覆盖源包契约；Docker 镜像构建及隔离 smoke 在可用运行时验证。 | 实现、构建与测试完成，已提交并推送（`f4b03aa`）；Docker 镜像/容器验收因环境无容器运行时而阻塞，未宣称通过。 |
| **9. 最终一致性与交付** | 全包差异审计、迁移文档收口、上游 TypeScript 文档/示例的 .NET 适配决策、所有提交和远端同步。 | `dotnet build Pi.slnx -m:1` 成功；目标测试按环境可运行范围全部通过，剩余受限测试有隔离证据；源/目标公开接口与 JSON/wire 形状核对完；文档/示例差异已处理或明确接受；工作区干净且提交已推送。 | 本机构建、测试、CLI smoke 和文档适配完成；POSIX、Docker 与剩余公开契约复核仍未完成，阶段保持未完成。 |

### 特殊范围决策

- SQLite 实现移植的是 portable SQLite storage contract；Node `node:sqlite`、Cloudflare binding 等 JS 适配器不逐字移植，而由 `Microsoft.Data.Sqlite` 提供 .NET 后端。存储可观察语义、schema 演进和失败行为仍必须一致。
- Stage 6 采用决策 B：不复制 Bun 编译产物、Bun sandbox `/proc/self/environ` 恢复垫片或 Bun-specific worker 入口；保留 CLI 的进程环境变量、内部角色分派、外部子进程和发布资源定位。`runtime-setup.ts` 的静态 OAuth/Bedrock 接线在 .NET 由 OAuth/provider 注册表直接表达；QuickJS WASM 路径由宿主注入，执行引擎仍是明确的 `ICodemodeJsEngine` 扩展点。
- Stage 7 的 TS→C# 测试映射按可观察行为而非测试数量/名称逐项对应：compaction/cache 对照 `HarnessCompactionTests`、`HarnessContextCacheTests`、context selection/serialization 与 docs 状态测试；generation recovery 对照 `HarnessGenerationRecoveryTests`；structured/tasks/ownership/tasks-recovery 对照 `HarnessSchedulerTests`、`HarnessTaskGraphTests`、`HarnessInboxTests`、`HarnessSubmissionsTests`、`HarnessLifecycleTests`、`HarnessInspectTests`；conversations 对照 `HarnessIntegrationTests`、`HarnessSubmissionsTests`、`HarnessInboxTests`；live deltas 对照 `HarnessEventsTests`、`HarnessDocsTests`、`HarnessOutputTests`；tools recovery 对照 `HarnessToolTests` 与 generation recovery 场景。新增恢复断言覆盖已落摘要重开幂等、hook 中断重开、阻塞 compaction 重开、后台 compaction 不阻塞 run/idle wait、overflow 重试/不重复压缩，以及 TS transcript 中工具参数的 `key=JSON` 格式。 |
- Stage 6 将上游 `packages/coding-agent` 的 `package.json`、README、CHANGELOG、docs 与 examples 复制为 `Pi.CodingAgent` 输出资源；逐文件核验 45 个 docs 文件、142 个 examples 文件，README/CHANGELOG/package.json 的 SHA-256 与源相同。为通过 whitespace 检查，`compaction.md` 的空白行去掉尾随空格，`qna.ts` 示例用 `\u0020` 保留模板字符串输出空格。这样兑现 `Config.GetReadmePath/GetDocsPath/GetExamplesPath/GetChangelogPath` 和 system prompt 中的路径契约；Stage 9 将随包 README 改为 .NET 说明，上游 docs/examples 明确保留为 TypeScript/Node 参考。
- `packages/client/src/unix.ts` 在 Node/Bun 的 Windows 平台显式拒绝 Unix transport；`Pi.Client` 目前利用 .NET Unix-domain socket 实现，在支持该能力的 Windows 系统上也开放该 API。这是记录在案的 .NET 扩展行为，不声称与源的 Windows 拒绝完全一致；Windows 上 `ServerClientEndToEndTests` 9/9 通过。当前环境未运行 POSIX .NET，阶段 9 需在 POSIX runner 验证对应传输。
- Vacation 会话锁由 .NET 对 `.vacation.lock` 的 `FileShare.None` 独占句柄实现；进程退出时操作系统释放锁，但空锁文件保留。与 TS `proper-lockfile` 的 10 秒 stale-lock 等待/清理策略不同，重开没有 stale 等待。HTTP 全局 dispatcher 仍按既有差异 C32 处理：保留代理环境变量播种及 Harness stream timeout 映射，不伪造 Undici global dispatcher 的等价实现。
- `index.ts` 纯导出桶、Node/Bun 专属测试启动器等无独立 C# 语义的文件可不产生一一对应文件，但要在最终公开 API/平台矩阵中有可复核的逐项说明。

## 验证基线、风险与限制

- Stage 7 复验：`dotnet build .\Pi.slnx --no-restore -m:1` 通过，0 警告、0 错误；`dotnet run --no-restore --project .\tests\Pi.Durable.Tests\Pi.Durable.Tests.csproj` 共 398 项，396 通过、0 失败、2 项跳过；跳过项仅为 `ToolsTests.Bash_NonZeroExit_Throws` 和 `ToolsTests.Bash_StreamsOutputThroughApiAndReturnsNoContent`，当前 Windows 环境的 Git Bash/MSYS2 初始化失败已有逐项说明；`git diff --check` 通过。
- Stage 8：`Pi.Evals.Tests` 52/52 通过；`Pi.Evals --help` 退出码 0；完整 solution 构建 0 警告、0 错误；提交 `f4b03aa` 已推送至 `origin/main`。本机无 Docker、Podman、nerdctl，且 WSL 无发行版，故未验证镜像构建和容器运行。
- Stage 9 本机复验：`dotnet build .\Pi.slnx --no-restore -m:1` 通过，0 警告、0 错误；12 个测试项目总计 29,799 项，29,797 通过、0 失败、2 项跳过。跳过项仅为 `ToolsTests.Bash_NonZeroExit_Throws` 与 `ToolsTests.Bash_StreamsOutputThroughApiAndReturnsNoContent`，因 Git Bash/MSYS2 在当前 Windows 环境无法初始化；Pi CLI 与 Evals CLI `--help` 退出码均为 0，包资源测试 1/1 通过，`git diff --check` 通过。全量复验还发现并修正了测试中的已退出子进程、过期 session-worker 断言和打包 changelog 干扰；修正后全量套件无失败。
- Stage 9 外部验收仍未完成：本机无容器运行时/WSL，无法执行 Docker eval；也没有 POSIX runner 可核验 Unix socket 与公开接口/JSON 边界。上述事项仍列为 P1/P2，不作为通过项。
- `dotnet build Pi.slnx -m:1 --no-restore`：此前阶段基线通过，0 警告、0 错误；Stage 7 复验结果见上一条。
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
| 3. Package manager/资源链 | 完成 package-manager 更新检查/执行、npm semver 与并发限制、Git ref/reset/incomplete-marker 恢复和临时 Git checkout 刷新；接通 legacy npm/pnpm 全局路径回退、CLI `-e` 资源、host-provided package 告警及交互启动更新通知。 | `dotnet build Pi.slnx --no-restore -m:1`：0 警告、0 错误；`PackageManagerTests` 24/24、`PackageManagerUpdateTests` 12/12、`ResourceLoaderTests` 24/24、`CliMainEndToEndTests` 13/13、`InteractiveModeStartupTests` 20/20（共 93 项通过，0 失败）。测试通过 MTP runner 直接执行目标程序集。 | `1d8e077 feat(coding-agent): complete package updates and resource wiring`，已推送 `origin/main`。 |
| 4. MCP/Llama UI | 补齐 MCP 菜单/状态/OAuth 重定向 TUI 与 Llama 模型菜单、Hugging Face 搜索/缓存/取消、进度视图；将两个扩展的默认 UI hook 接到真实视图。 | `dotnet build Pi.slnx --no-restore -m:1`：0 警告、0 错误；`ExtensionManagerViewTests` 13/13、`McpExtensionTests` 46/46、`LlamaExtensionTests` 44/44、`InteractiveModeStartupTests` 20/20（共 123 项通过，0 失败）；`git diff --check` 通过。 | `35d9723 feat(coding-agent): implement MCP and llama manager UIs`，已推送 `origin/main`。 |
| 5. Vacation | 新增隔离会话与锁、planner/search 扩展和后台研究任务、Durable 运行时/控制器、独立 TUI 与 `pi-vacation` 宿主；压缩结果反馈和摘要展开也与源 TUI 对齐。已知差异为 C32 的 Undici 全局 dispatcher 与 OS 原生文件锁 stale 语义，详见“特殊范围决策”。 | `dotnet build Pi.slnx --no-restore -m:1`：0 警告、0 错误；`VacationSessionsTests` 3/3、`VacationCommandLineTests` 5/5、`VacationExtensionsTests` 1/1、`VacationRuntimeTests` 1/1、聊天视图摘要展开测试 1/1；`HarnessDocsTests` 17/17。宿主 `--help` 参数烟测按源契约以退出码 1 拒绝未知参数；未在本次无交互终端中执行真实模型对话。 | `1b26f0f feat(coding-agent): add vacation planner workflow`，与本台账提交一并推送 `origin/main`。 |
| 6. Bun/platform 判定与适配 | 修复 UDS 短写与发现错误过滤；补齐 coding-agent 包元数据、README、CHANGELOG、docs、examples 和交互图片的宿主输出；以可控时钟测试覆盖 post-exit stdio grace 的输出重置与 idle 释放。按决策 B 使用 .NET 原生进程与发布流程，不复制 Bun 运行时。 | `dotnet build Pi.slnx --no-restore -m:1`：0 警告、0 错误；Windows `ServerClientEndToEndTests` 9/9；`ChildProcessTests` 3/3；`PackageAssetTests` 1/1；`CoreUtilsCorpusTests` 18/18、`PromptCorpusTests` 16/16、`CliMainEndToEndTests` 13/13、`SessionWorkerProcessE2ETests` 1/1、`EarendilAnnouncementComponentTests` 2/2；稳定 CLI、`PI_RPC_ENTRY=1`、`PI_EXPERIMENTAL_ENTRY=1` 的 `--help` smoke 均退出 0；资源副本逐文件校验一致（两处尾随空白已规范化）。`git diff --check` 通过。POSIX 运行时测试留待阶段 9。 | `c828754 fix(coding-agent): align transport and packaged assets` 已推送；此记录随本台账提交。 |
| 7. Durable test parity | 完成 context retention/cache 生命周期与 leading-system 顺序核对；补齐 generation recovery 和 compaction 恢复/overflow/后台路径断言；修正 compaction transcript 工具参数序列化，使其符合 TS 的 `key=JSON.stringify(value)` 形状。其它 harness 测试按上方测试映射做行为级一致性核验。 | `dotnet build .\Pi.slnx --no-restore -m:1`：0 警告、0 错误；完整 Durable 测试 398 项，396 通过、2 项因当前 Git Bash/MSYS2 环境不可用而跳过、0 失败；`git diff --check` 通过。 | `eb9d195 fix(durable): align harness recovery behavior`，已推送 `origin/main`。 |
| 8. Evals | 新增 `Pi.Evals` CLI 和 Docker 镜像/worker，移植 host eval、5 个 docs scenario、协议发现与报告；增加 C# extension authoring 文档与上游 docs 导航。 | `dotnet build .\Pi.slnx --no-restore -m:1`：0 警告、0 错误；Evals 测试 52/52；CLI `--help` 通过；项目输出含 README/docs/examples/Dockerfile；`git diff --check` 通过。Docker、Podman、nerdctl 均不存在且 WSL 无发行版，因此镜像 build 和隔离运行未验证，作为显式剩余风险。 | `f4b03aa feat(evals): add native .NET evaluation runner` 已提交并推送；容器级验收仍受环境阻塞。 |
| 9. 最终一致性与交付 | 修正全量复验发现的测试假设；将根 README 与打包说明适配为 .NET，并标注上游 docs/examples 的适用范围；复验 solution、全部测试和 CLI。 | `dotnet build .\Pi.slnx --no-restore -m:1`：0 警告、0 错误；12 个测试项目共 29,799 项，29,797 通过、0 失败、2 项因 Git Bash/MSYS2 初始化失败而跳过；Pi CLI、Evals CLI `--help` 及包资源测试通过；`git diff --check` 通过。 | 本机可运行验收记录已提交并推送（`309fb30`）；Docker、POSIX 和剩余公开契约复核仍阻塞，未宣称迁移全部完成。 |
