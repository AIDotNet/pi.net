# Pi → .NET 迁移执行状态

本文是当前迁移清单、优先级、阶段验收标准和执行记录的权威入口。旧的
[migration-plan.md](./migration-plan.md) 与 [porting-status.md](./porting-status.md)
保留历史阶段说明；其汇总快照早于当前代码，不应用来推断当前完成度。

## 基线与范围

- TypeScript 源仓库：`D:\AI\参考项目\pi`，`main`，复核基线 `eb326d265ae0b88489a6d10319307780df827cdf`（与 `origin/main` 一致）。
- .NET 目标仓库：`D:\AI\参考项目\pi.net`，`main`，本轮复核起点 `47cdc4af7be384099b8102a0676e65add23c4a85`（与 `origin/main` 一致）。
- 目标仓库复核前工作区干净。源仓库未跟踪的 `.workbuddy/` 属于既有内容，本轮保持不动。
- 源含 14 个 package，目标映射到 17 个 .NET 项目：12 个常规包各一个项目，`coding-agent` 拆为 `Pi.CodingAgent`、`Pi.Cli`、`Pi.Vacation`、`Pi.DurableHost`，`evals` 对应独立 CLI 项目 `Pi.Evals`。目标另有 13 个测试项目和一个 `Pi.Env.FakeDaemon` 测试支持项目。
- 最近的代码记录已包括远程执行环境 `Pi.Env`、Durable 实验宿主、`createCodingAgentHarness`/session-worker 接线及 codemode 工具渲染器。以下区分已迁移实现、刻意不做逐文件映射的运行时专属文件，以及仍未完成的外部验收；不把旧台账中的早期百分比或启发式文件名未匹配数当作完成度。

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
| `env` | `Pi.Env` | 客户端实现已迁移；Windows conformance 通过 | 协议连接、SSH 引导、远程环境与 watcher 已移植；守护进程按原生产物注入（差异 E-C1）。本轮 `Pi.Env.Tests` 46/46 通过、1 项 sshd 端到端因本机无 sshd 跳过；仍需 POSIX 主机验收。 |
| `durable` | `Pi.Durable` | 核心实现及 harness 行为对齐已完成 | Memory/SQLite storage、Session、context retention/cache、compaction、任务/会话恢复及 harness/runtime 均有实现；本轮 `Pi.Durable.Tests` 398/398 通过。最终公开契约与 POSIX 验收仍在剩余项中。 |
| `tui` | `Pi.Tui` | 功能实现完成 | 44/45 个 TS 源文件已有实现；剩余 `index.ts` 是纯导出桶，不需要单独 C# 文件。本轮新增 fullscreen client TUI/滚动集成测试和容器并发渲染回归测试。 |
| `coding-agent` | `Pi.CodingAgent` + `Pi.Cli` + `Pi.Vacation` + `Pi.DurableHost` | 主体功能与实验插件 JS bundle 默认执行能力已迁移；平台/契约验收未闭环 | 交互/RPC、CLI、MCP/Llama UI、package manager、资源链、Vacation、Durable session-worker 与独立宿主均已实现。C362 已由 `adf2303` 的 harness 复用接线并在 `11e2b58` 关闭；codemode renderer 已由 `c989567` 移植。实验插件 CommonJS bundle 由默认 Jint host 执行，并有 keyed 生命周期/Context 集成测试；Bun/Node 专属运行时垫片、动态加载钩子及被合并/替代的文件不逐字移植，Jint import 支持范围见“特殊范围决策”。 |
| `evals` | `Pi.Evals` | 实现完成，容器级验收未完成 | 已移植 CLI、host/documentation eval、任务发现/计划/报告和 Docker 隔离执行；单元测试已通过。当前环境无 Docker/Podman/nerdctl 或 WSL 发行版，镜像构建与容器 smoke 尚未验证。 |

## 剩余事项与优先级

**P0：本轮没有发现需要从零补建的整包/主模块。** 这不表示所有边界均已完成；例如实验插件 JS bundle 执行仍是待决的兼容性缺口。下列运行时和契约验收未完成前，不能宣称迁移已全部验收。

| 优先级 | 工作项 | 主要源范围 | 依赖 / 理由 |
|---|---|---|---|
| **P1** | Evals 容器级验收 | `packages/evals` 与 `src/Pi.Evals/Docker` | 实现及单元测试已通过；需构建两种镜像并运行隔离 eval smoke。本机没有 Docker、Podman、nerdctl 服务，WSL 未安装发行版。 |
| **P2** | 公开 API 与 JSON/wire 契约最终核验 | 14 个源包的公开导出、`src/**` 中对应 API 与序列化边界 | 现有导出面扫描是启发式：TS 类型别名、工厂函数、命名转换及拆分项目会产生未匹配项；必须逐项分类并用行为/JSON 测试确认，不能把扫描结果直接视作缺陷或通过。 |
| **P2** | POSIX transport 与 SSH 端到端验收 | `packages/client/src/unix.ts`、`packages/env/test/sshd.test.ts`、`Pi.Client` / `Pi.Server` / `Pi.Env` | Windows 定点测试通过，但没有 POSIX .NET runner 或 sshd；需在 POSIX 主机验证 Unix socket、发现/握手，以及 SSH 扫描、信任、部署和远程执行链。 |

文档/示例范围已明确：随包 README 已改为 .NET 10 说明，文档索引和 README 均标明上游 docs/examples 面向 TypeScript/Node/Bun；C# 扩展使用仓库的 .NET 指南。上游示例作为参考保留，不冒充 .NET 示例，也不再作为未决适配项。

## 分阶段计划与验收

每阶段均先检查 TypeScript 对应实现/测试，再做最小范围 C# 实现；完成后跑目标构建和该阶段测试、记录差异与未覆盖边界，再单独提交并推送该阶段。若验证失败，不得把失败测试标成环境限制，除非有可复现证据并将证据写入记录。

| 阶段 | 目标与文件范围 | 验收标准 | 状态 |
|---|---|---|---|
| **0. 基线与盘点** | 核对源/目标 commit、项目映射、旧台账与可复现基线；更新本文件及旧文档入口。 | 14 个 TS 包映射到 17 个 .NET 项目；基线和工作区状态可复查。 | 已完成并提交、推送（`47dcfe7`）。 |
| **1. SQLite storage** | `Pi.Durable/Storage` 新增 SQLite schema/migration 与实现；`Pi.Durable.csproj` 添加 SQLite provider；新增 `Pi.Durable.Tests` SQLite/conformance 测试。 | schema migration 可重复/原子；事务失败可回滚；ID 全局冲突规则、排序/分页/cursor、fork 可见性、文档历史/当前点、重开恢复和 close 语义与 TS 一致；全部 storage conformance 通过；0 警告构建。 | 已完成并推送（`0546fc3`）；原全量套件挂起在阶段 2 修复并复验通过。 |
| **2. Harness setup 与 worker** | `Pi.CodingAgent/Experimental`、Durable harness 及 Session 入队；SQLite-backed `createCodingAgentHarness`、worker process role/spawn/shutdown/reopen。 | 真实子进程覆盖 ready/demand/operation/shutdown、锁释放和重开持久性；worker 输入/控制错误、操作取消和 manager 超时有定点测试；compaction safe-integer 范围不被收窄；相关测试及 Durable 全量套件通过。 | 已完成并随阶段提交推送；执行细节见下表。 |
| **3. Package manager/资源链** | `Core/PackageManager*`、`ResourceLoader`、CLI/settings/resource 装配及扩展告警。 | 更新检查/执行与 marker/ref 状态、告警排序/内容、额外路径解析和缺失路径错误均有 TS 对照测试；CLI `-e` 端到端通过。 | 已完成并推送（`1d8e077`）。 |
| **4. MCP/Llama UI** | `Extensions/Mcp`、`Extensions/Llama` 与必要的 Interactive 组件/注册点。 | 命令触发、视图生命周期、键盘交互、主题渲染、取消/错误路径覆盖；extension manager 默认行为与 TS 相同。 | 已完成并推送（`35d9723`）。 |
| **5. Vacation** | 新增 `Pi.CodingAgent/Experimental/Vacation`、`Pi.Vacation` 宿主及定点测试；映射 `sessions.ts`、`vacation.ts`、`harness-setup.ts`、`runtime.ts`、`tui.ts`、`main.ts` 与 README 的用户文档。 | 会话按 cwd 隔离、continue 选最新有效目录、独占锁释放、Research/Search task 和 request ID 幂等、控制器及 TUI 交互/状态、压缩反馈、仅支持 `--continue`/`-c` 的宿主入口均有对应实现与测试；全 solution 0 警告构建。 | 已完成并提交（`1b26f0f`，与 Stage 5 台账提交一并推送）。 |
| **6. Bun/platform 判定与适配** | 检查 `bun/*`、`packages/client/*` 和 CLI/环境/进程入口；核对 `Pi.CodingAgent` 包资源，并验证 .NET 宿主与传输。 | **决策 B**：不移植 Bun 专属编译/打包器或运行时启动垫片；.NET 使用原生宿主与发布流程，保留可观察 CLI、子进程角色、环境继承、资源定位和客户端传输行为。按源测试核对 Unix discovery、握手/数据完整性、超时、失败边界和 post-exit stdio idle grace；README、CHANGELOG、package metadata、docs、examples 和交互图片均随宿主输出。Windows UDS 是有记录的 .NET 扩展行为。 | Windows 验收完成；POSIX 运行时验证留待阶段 9。 |
| **7. Durable test parity** | `Pi.Durable/Harness` 的 context retention/cache、compaction、任务/会话和 generation/tool recovery；核对 `harness-compaction`、`structured`、`tasks`、`ownership`、`tasks-recovery`、`conversations`、`live-deltas`、`tools-recovery`、`generation-recovery`。 | 各源测试套件的可观察状态迁移、边界和恢复行为均有 C# 等价断言（允许将重复场景合并到行为级测试）；不可移植项有可复现理由；完整 Durable 套件可稳定完成。 | 已完成并推送（`eb9d195`）。 |
| **8. Evals** | 新增 `Pi.Evals` 及测试项目，接线 solution/CLI；移植 host/docs eval、CLI、scenario/task/report、Docker supervisor/worker 与 .NET 扩展文档。 | 评测选择、执行、结果格式、失败/超时语义与 TS 对照；新项目和全 solution 构建通过；测试覆盖源包契约；Docker 镜像构建及隔离 smoke 在可用运行时验证。 | 实现、构建与测试完成，已提交并推送（`f4b03aa`）；Docker 镜像/容器验收因环境无容器运行时而阻塞，未宣称通过。 |
| **9. 最终一致性与交付** | 全包差异审计、迁移文档收口、上游 TypeScript 文档/示例的 .NET 适配决策、所有提交和远端同步。 | `dotnet build Pi.slnx -m:1` 成功；目标测试按环境可运行范围全部通过，剩余受限测试有隔离证据；源/目标公开接口与 JSON/wire 形状核对完；文档/示例差异已处理或明确接受；工作区干净且提交已推送。 | 本机构建、测试、CLI smoke 和文档适配完成；POSIX、Docker 与剩余公开契约复核仍未完成，阶段保持未完成。 |
| **10. Experimental client TUI fullscreen parity** | `ClientTuiTests`、`Pi.Tui.Container` 及 InteractiveMode/selector 的子项访问；接入真实 `TuiAltScreen`/`StringTerminal`，覆盖全屏渲染、滚动与异步结构更新。 | 验证 viewport 高度、长 transcript、PageUp/PageDown follow-end；在并发变更时稳定渲染容器快照；运行完整 `Pi.Tui.Tests` 与 `Pi.CodingAgent.Tests`。 | 本轮已完成本机验收：Pi.Tui 23,875/23,875、Pi.CodingAgent 4,944/4,944；整体阶段 9 仍受上述 P1/P2 项阻塞。 |
| **11. Jint 插件包与 keyed Context 生命周期** | `Pi.Chord/Context`、Facet service slots、keyed instance directory/remote consumer，以及 `Pi.CodingAgent/Experimental/Plugins` Jint CommonJS host 与 context bridge。 | 对照 TS keyed observation 语义；每个 observer-entry 有独立可取消 Context，关闭/退订/reload/dispose 会取消；旧远程实例失效；CommonJS bundle、同引擎回调、Context key identity 和 AbortSignal 事件有真实集成测试；Chord/CodingAgent 定向与完整测试、solution build 通过。 | 本机验收通过，阶段代码与台账待本轮提交推送；具体结果见执行记录。 |

### 特殊范围决策

- SQLite 实现移植的是 portable SQLite storage contract；Node `node:sqlite`、Cloudflare binding 等 JS 适配器不逐字移植，而由 `Microsoft.Data.Sqlite` 提供 .NET 后端。存储可观察语义、schema 演进和失败行为仍必须一致。
- Stage 6 采用决策 B：不复制 Bun 编译产物、Bun sandbox `/proc/self/environ` 恢复垫片或 Bun-specific worker 入口；保留 CLI 的进程环境变量、内部角色分派、外部子进程和发布资源定位。`runtime-setup.ts` 的静态 OAuth/Bedrock 接线在 .NET 由 OAuth/provider 注册表直接表达；QuickJS WASM 路径由宿主注入，执行引擎仍是明确的 `ICodemodeJsEngine` 扩展点。
- Stage 11 为实验插件 bundle 采用 Jint CommonJS 执行器作为默认 .NET host；manifest 声明的 import 必须由 host resolver 支持，目前没有模拟 Node/Bun 的内置模块、全局对象或动态加载钩子，未支持的 import 明确失败。此决策解决 bundle 默认不可执行的问题，不宣称提供完整 Node/Bun runtime parity。
- Stage 7 的 TS→C# 测试映射按可观察行为而非测试数量/名称逐项对应：compaction/cache 对照 `HarnessCompactionTests`、`HarnessContextCacheTests`、context selection/serialization 与 docs 状态测试；generation recovery 对照 `HarnessGenerationRecoveryTests`；structured/tasks/ownership/tasks-recovery 对照 `HarnessSchedulerTests`、`HarnessTaskGraphTests`、`HarnessInboxTests`、`HarnessSubmissionsTests`、`HarnessLifecycleTests`、`HarnessInspectTests`；conversations 对照 `HarnessIntegrationTests`、`HarnessSubmissionsTests`、`HarnessInboxTests`；live deltas 对照 `HarnessEventsTests`、`HarnessDocsTests`、`HarnessOutputTests`；tools recovery 对照 `HarnessToolTests` 与 generation recovery 场景。新增恢复断言覆盖已落摘要重开幂等、hook 中断重开、阻塞 compaction 重开、后台 compaction 不阻塞 run/idle wait、overflow 重试/不重复压缩，以及 TS transcript 中工具参数的 `key=JSON` 格式。 |
- Stage 6 将上游 `packages/coding-agent` 的 `package.json`、README、CHANGELOG、docs 与 examples 复制为 `Pi.CodingAgent` 输出资源；逐文件核验 45 个 docs 文件、142 个 examples 文件，README/CHANGELOG/package.json 的 SHA-256 与源相同。为通过 whitespace 检查，`compaction.md` 的空白行去掉尾随空格，`qna.ts` 示例用 `\u0020` 保留模板字符串输出空格。这样兑现 `Config.GetReadmePath/GetDocsPath/GetExamplesPath/GetChangelogPath` 和 system prompt 中的路径契约；Stage 9 将随包 README 改为 .NET 说明，上游 docs/examples 明确保留为 TypeScript/Node 参考。
- `packages/client/src/unix.ts` 在 Node/Bun 的 Windows 平台显式拒绝 Unix transport；`Pi.Client` 目前利用 .NET Unix-domain socket 实现，在支持该能力的 Windows 系统上也开放该 API。这是记录在案的 .NET 扩展行为，不声称与源的 Windows 拒绝完全一致；Windows 上 `ServerClientEndToEndTests` 9/9 通过。当前环境未运行 POSIX .NET，阶段 9 需在 POSIX runner 验证对应传输。
- Vacation 会话锁由 .NET 对 `.vacation.lock` 的 `FileShare.None` 独占句柄实现；进程退出时操作系统释放锁，但空锁文件保留。与 TS `proper-lockfile` 的 10 秒 stale-lock 等待/清理策略不同，重开没有 stale 等待。HTTP 全局 dispatcher 仍按既有差异 C32 处理：保留代理环境变量播种及 Harness stream timeout 映射，不伪造 Undici global dispatcher 的等价实现。
- `index.ts` 纯导出桶、Node/Bun 专属测试启动器等无独立 C# 语义的文件可不产生一一对应文件，但要在最终公开 API/平台矩阵中有可复核的逐项说明。
- 2026-10-20 复核确认：C362 的 SQLite-backed harness/session-worker 接线已存在；codemode 扩展 renderer 已实现；4g-3h 中 Bun sandbox/启动垫片、Node 动态加载钩子和 re-export barrel 没有可直接照搬的 .NET 运行时语义。`experimental/durable` 与 `experimental/vacation` 共用实现由 `ExperimentalProfile` 表达，不重复移植两份相同 TS 文件。
- `tools/` 下被 `.gitignore` 排除的覆盖/导出面脚本仅作启发式线索；未匹配文件名或导出 token 不等价于功能缺失，也不代表公开契约已核验完成。
- 2026-10-20 复核确认：C362 的 SQLite-backed harness/session-worker 接线已存在；codemode 扩展 renderer 已实现；4g-3h 中 Bun sandbox/启动垫片、Node 动态加载钩子和 re-export barrel 没有可直接照搬的 .NET 运行时语义。`experimental/durable` 与 `experimental/vacation` 共用实现由 `ExperimentalProfile` 表达，不重复移植两份相同 TS 文件。
- `tools/` 下被 `.gitignore` 排除的覆盖/导出面脚本仅作启发式线索；未匹配文件名或导出 token 不等价于功能缺失，也不代表公开契约已核验完成。

## 验证基线、风险与限制

- Stage 7 复验：`dotnet build .\Pi.slnx --no-restore -m:1` 通过，0 警告、0 错误；`dotnet run --no-restore --project .\tests\Pi.Durable.Tests\Pi.Durable.Tests.csproj` 共 398 项，396 通过、0 失败、2 项跳过；跳过项仅为 `ToolsTests.Bash_NonZeroExit_Throws` 和 `ToolsTests.Bash_StreamsOutputThroughApiAndReturnsNoContent`，当前 Windows 环境的 Git Bash/MSYS2 初始化失败已有逐项说明；`git diff --check` 通过。
- Stage 8：`Pi.Evals.Tests` 52/52 通过；`Pi.Evals --help` 退出码 0；完整 solution 构建 0 警告、0 错误；提交 `f4b03aa` 已推送至 `origin/main`。本机无 Docker、Podman、nerdctl，且 WSL 无发行版，故未验证镜像构建和容器运行。
- Stage 9 本机复验：`dotnet build .\Pi.slnx --no-restore -m:1` 通过，0 警告、0 错误；12 个测试项目总计 29,799 项，29,797 通过、0 失败、2 项跳过。跳过项仅为 `ToolsTests.Bash_NonZeroExit_Throws` 与 `ToolsTests.Bash_StreamsOutputThroughApiAndReturnsNoContent`，因 Git Bash/MSYS2 在当前 Windows 环境无法初始化；Pi CLI 与 Evals CLI `--help` 退出码均为 0，包资源测试 1/1 通过，`git diff --check` 通过。全量复验还发现并修正了测试中的已退出子进程、过期 session-worker 断言和打包 changelog 干扰；修正后全量套件无失败。
- Stage 9 外部验收仍未完成：本机无容器运行时/WSL，无法执行 Docker eval；也没有 POSIX runner 可核验 Unix socket 与公开接口/JSON 边界。上述事项仍列为 P1/P2，不作为通过项。
- 上述 29,799 项是历史快照，已由本轮复验取代：`dotnet build Pi.slnx --no-restore -m:1 -v:minimal` 通过，0 警告、0 错误；13 个测试项目共 29,942 项，29,941 通过、0 失败、1 项 sshd 测试因本机无 sshd 跳过。`Pi.CodingAgent.Tests` 为 4,943/4,943、`Pi.Durable.Tests` 为 398/398、`Pi.Env.Tests` 为 46/46 加 1 跳过。PortableGit 仅通过测试进程环境配置启用，没有改机器级或仓库配置。
- CLI smoke：Pi CLI 与 `Pi.Evals` 的 `--help` 退出码为 0；`Pi.DurableHost` 与 `Pi.Vacation` 对 `--help` 以退出码 1 拒绝未知参数，这与 TS 宿主只支持 `--continue`/`-c` 的参数契约一致。`git diff --check` 通过。
- `dotnet build Pi.slnx -m:1 --no-restore`：此前阶段基线通过，0 警告、0 错误；Stage 7 复验结果见上一条。
- `dotnet test --solution` 在当前 SDK/Microsoft.Testing.Platform 配置下不能作为入口；本机可直接执行 `tests\<项目>\bin\Debug\net10.0\<项目>.exe` 并使用 MTP 的 `--filter-method`、`--filter-class` 等筛选参数。每阶段记录实际命令和结果。
- `Pi.Durable.Tests` 的阶段 1 全量执行曾卡在 inbox 用例；阶段 2 将测试 fixture 的默认扩展选择校正为 TS 的 undefined 语义，并修复了 `Session.Enqueue` 并发调用时 `_tail` 链接非原子的竞态。阶段 2 最终全量复验：370 项通过、2 项因本机缺少可用 Git Bash 而跳过、0 失败；期间两次全量运行中的时序敏感输出测试曾分别偶发失败，单测复跑及之后的全量复验通过，故保留为时序稳定性观察项而非环境豁免。
- 早期 harness inbox 挂起及 coding-agent 4,797 项测试记录均为历史诊断；本轮全量测试项目运行完成且无失败，当前测试数见上述复验结果。
- Stage 11 复验：`dotnet build Pi.slnx --no-restore -m:1` 通过，0 警告、0 错误；`Pi.Chord.Tests` 104/104、`PluginBundlesTests` 12/12、`JintContextBridgeTests` 3/3、完整 `Pi.CodingAgent.Tests` 4,948/4,948 通过，0 失败；`git diff --check` 通过。
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
| 9. 最终一致性与交付 | 修正全量复验发现的测试假设；将根 README 与打包说明适配为 .NET，并标注上游 docs/examples 的适用范围；复核源码缺口并重新验证 solution、全部测试和 CLI。 | `dotnet build Pi.slnx --no-restore -m:1 -v:minimal`：0 警告、0 错误；13 个测试项目共 29,942 项，29,941 通过、0 失败、1 项 sshd 跳过；Pi CLI 与 Evals CLI `--help` 退出码 0；Durable/Vacation 宿主对 `--help` 按 TS 契约拒绝未知参数；`git diff --check` 通过。 | 本机可运行验收通过；Docker、POSIX 和剩余公开契约复核仍阻塞，阶段未完成。原实现记录提交 `309fb30` 已推送；本轮台账修订待提交推送。 |
| 11. Jint 插件包与 keyed Context 生命周期 | 默认插件 host 改为 Jint CommonJS 执行；修复同 Jint 引擎回调重入；桥接 Context key identity、AbortSignal 事件及监听器释放；将 keyed observation 改为按 observer-entry 创建/取消 Context，并覆盖 remote reload 的旧实例失效。 | `dotnet build Pi.slnx --no-restore -m:1`：0 警告、0 错误；`Pi.Chord.Tests` 104/104、`PluginBundlesTests` 12/12、`JintContextBridgeTests` 3/3、完整 `Pi.CodingAgent.Tests` 4,948/4,948；`git diff --check` 通过。 | 本机验收通过，阶段提交与推送待完成。 |
