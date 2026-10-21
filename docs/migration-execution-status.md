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
| `server` | `Pi.Server` | 已迁移，Windows/POSIX 验收通过 | RPC 与本地发现已移植；Unix socket 端到端已在 Linux 主机验收（Stage 23，`ServerClientEndToEndTests` 9/9）。 |
| `client` | `Pi.Client` | 已迁移 | 客户端协议与传输已实现。 |
| `ai` | `Pi.Ai` | 已迁移 | Provider 与模型层已广泛移植；公开导出面与 wire 形状已完成审计（[public-api-audit.md](./public-api-audit.md)）。Anthropic OIDC federation 已由 `Auth/AnthropicFederation.cs` 按 SDK 语义实现（Stage 22）；依赖本地浏览器回调的 OAuth 测试不适合无头环境。 |
| `chord` | `Pi.Chord` | 已迁移 | 核心协议/服务/Facet 已实现；`index.ts` 是导出桶，不要求在 C# 复制。 |
| `codemode` | `Pi.Codemode` | 已迁移 | 默认由 Jint 承载 prelude、工具桥与 promise 排空；`ICodemodeJsEngine` 可替换引擎，`UnsupportedCodemodeJsEngine` 可显式禁用。QuickJS WASM 载荷与 Jint 内存计量属于已记录的引擎差异。 |
| `env` | `Pi.Env` | 客户端实现已迁移；Windows/POSIX 验收通过 | 协议连接、SSH 引导、远程环境与 watcher 已移植；守护进程按原生产物注入（差异 E-C1）。Linux 上 `Pi.Env.Tests` 48/48，含真实 `sshd` 扫描/信任/部署/远程执行链（Stage 23）；Windows 44 通过 + 3 平台跳过。 |
| `durable` | `Pi.Durable` | 核心实现及 harness 行为对齐已完成 | Memory/SQLite storage、Session、context retention/cache、compaction、任务/会话恢复及 harness/runtime 均有实现；本轮 `Pi.Durable.Tests` 398/398 通过。最终公开契约与 POSIX 验收仍在剩余项中。 |
| `tui` | `Pi.Tui` | 功能实现完成 | 44/45 个 TS 源文件已有实现；剩余 `index.ts` 是纯导出桶，不需要单独 C# 文件。本轮新增 fullscreen client TUI/滚动集成测试和容器并发渲染回归测试。 |
| `coding-agent` | `Pi.CodingAgent` + `Pi.Cli` + `Pi.Vacation` + `Pi.DurableHost` | 主体功能与实验插件 JS bundle 默认执行能力已迁移；平台/契约验收未闭环 | 交互/RPC、CLI、MCP/Llama UI、package manager、资源链、Vacation、Durable session-worker 与独立宿主均已实现。C362 已由 `adf2303` 的 harness 复用接线并在 `11e2b58` 关闭；codemode renderer 已由 `c989567` 移植。实验插件 CommonJS bundle 由默认 Jint host 执行，并有 keyed 生命周期/Context 集成测试；Bun/Node 专属运行时垫片、动态加载钩子及被合并/替代的文件不逐字移植，Jint import 支持范围见“特殊范围决策”。 |
| `evals` | `Pi.Evals` | 实现完成，容器级验收通过 | 已移植 CLI、host/documentation eval、任务发现/计划/报告和 Docker 隔离执行；容器内 root-only/降权读取探针已移植并有 Linux-only 测试。Stage 23 在 Ubuntu + Docker 29.7.1 上构建双镜像、验证布局与权限探针，并以 10 个任务运行验证发现/选择/报告/失败传播。 |

## 剩余事项与优先级

**P0：2026-10-20 静态复核发现的 G1–G3 已在 2026-10-21 完成实现与运行验证（G1 JSONL、G2 CodeMode 默认 Jint 引擎、G3 Overlay 句柄）；G4 容器验收与 G6 POSIX/SSH 验收已于 2026-10-21 在外部 Ubuntu 主机完成（Stage 23）。当前没有已知的功能缺口或未完成的验收项。** 仍存在的边界是已记录的运行时差异：Node/Bun 内置模块、全局对象和动态加载不提供兼容层，QuickJS WASM 载荷与 Jint 的内存计量不同；这些属于明确范围决策，不是未移植功能。

**2026-10-21 收口结论**：迁移、公开契约审计、G4 容器验收、G6 POSIX/SSH 验收与 G7 Anthropic OIDC federation 均已完成。本机 13 个测试项目共 30,103 项，30,098 通过、0 失败、5 项平台跳过（2 项 Linux-only Evals 权限测试、2 项无 POSIX shell、1 项无 sshd），solution 0 警告构建；外部 Ubuntu 主机上 G4 两镜像与权限探针通过，POSIX `Pi.Server.Tests` 33/33、`Pi.Env.Tests` 48/48。当前没有已知的功能缺口或未完成的外部验收。

| 优先级 | 工作项 | 主要源范围 | 依赖 / 理由 |
|---|---|---|---|
| **P1** | JSONL 存储后端（G1） | `packages/durable/src/storage/jsonl/{storage,node}.ts` 与 `test/jsonl-storage.test.ts` | **已完成（2026-10-21）**：`JsonlStorage.cs` / `JsonlStorageJson.cs`、`MemoryStorage.PrepareCommit` 与 3 个测试类已构建并通过；修复了 ID 空间按 JS safe-integer 边界耗尽的语义。提交 `8535249`。 |
| **P1** | CodeMode 默认 JS 引擎（G2） | `packages/coding-agent/src/extensions/codemode/{execute.ts,worker.ts}`、`packages/codemode/src/runtime/worker.ts` | **已完成（2026-10-21）**：默认引擎改为 `JintCodemodeEngine`，`codemode/execute` 无需宿主注入即可执行；新增真实 Jint 集成测试，并修复同步工具调用在 host 绑定 VM 前完成回复的竞态。提交 `af67fa0`。详见 G2。 |
| **P2** | 扩展 Overlay 句柄接线（G3） | `packages/coding-agent/src/core/extensions/types.ts`、`packages/tui/src/tui.ts` | **已完成（2026-10-21）**：`OverlayHandle` 包装真实 `IOverlayHandle`，`InteractiveMode` 转交 `ShowOverlay` 返回值；新增转发与移除后调用测试。实现提交 `8535249`，行为测试提交 `e6ae911`。 |
| **P1** | Evals 容器级验收 | `packages/evals` 与 `src/Pi.Evals/Docker` | **已完成（2026-10-21，Stage 23）**：探针此前已移植；本轮在 Ubuntu 24.04 + Docker 29.7.1 上构建 `without_docs` / `with_docs` 两镜像（ID 不同），两变体布局校验通过，`setpriv --reuid=65532` 读取 `/app/Pi.Evals.dll` 失败而 root 读取成功；host 编排执行 5 用例 × 2 变体共 10 个任务，精确选择、产物落盘与失败传播均验证。验收中修复 3 个实现缺陷，并新增 1 项 root 运行兼容扩展（见 Stage 23 记录）。 |
| **P2** | 公开 API 与 JSON/wire 契约最终核验 | 14 个源包的公开导出、`src/**` 中对应 API 与序列化边界 | **已完成（2026-10-21，公开契约部分）**：1,426 个 barrel 导出逐项分类（442 runtime / 945 type / 39 unknown 均已判型），新增 OpenAI 与 Anthropic wire 套件并修复两个 `JsonNode` 归属缺陷；审计登记的 G7（OIDC federation）功能缺口已由 Stage 22 补齐。结论见 [public-api-audit.md](./public-api-audit.md)。POSIX 传输验收仍由 Stage 17 跟踪。 |
| **P2** | POSIX transport 与 SSH 端到端验收 | `packages/client/src/unix.ts`、`packages/env/test/sshd.test.ts`、`Pi.Client` / `Pi.Server` / `Pi.Env` | **已完成（2026-10-21，Stage 23）**：在 Ubuntu 24.04 上运行交叉发布的 linux-x64 测试二进制；`Pi.Server.Tests` 33/33（Unix socket 端到端 9/9），`Pi.Env.Tests` 48/48（真实 `sshd` 扫描/信任/部署/远程读写执行）。验收中修复 2 个仅 Linux 暴露的测试假设（跨流顺序、指纹整行）。 |

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
| **8. Evals** | 新增 `Pi.Evals` 及测试项目，接线 solution/CLI；移植 host/docs eval、CLI、scenario/task/report、Docker supervisor/worker 与 .NET 扩展文档。 | 评测选择、执行、结果格式、失败/超时语义与 TS 对照；新项目和全 solution 构建通过；测试覆盖源包契约；Docker 镜像构建及隔离 smoke 在可用运行时验证。 | 实现、构建与测试完成（`f4b03aa`）；Docker 镜像/容器验收已于 Stage 23 在外部 Ubuntu 主机完成。 |
| **9. 最终一致性与交付** | 全包差异审计、迁移文档收口、上游 TypeScript 文档/示例的 .NET 适配决策、所有提交和远端同步。 | `dotnet build Pi.slnx -m:1` 成功；目标测试按环境可运行范围全部通过，剩余受限测试有隔离证据；源/目标公开接口与 JSON/wire 形状核对完；文档/示例差异已处理或明确接受；工作区干净且提交已推送。 | 本机构建、测试、CLI smoke、公开契约复核（Stage 18/21）、Docker 容器验收（Stage 23）与 POSIX/SSH 端到端（Stage 23）均完成；文档/示例差异已登记；阶段完成。 |
| **10. Experimental client TUI fullscreen parity** | `ClientTuiTests`、`Pi.Tui.Container` 及 InteractiveMode/selector 的子项访问；接入真实 `TuiAltScreen`/`StringTerminal`，覆盖全屏渲染、滚动与异步结构更新。 | 验证 viewport 高度、长 transcript、PageUp/PageDown follow-end；在并发变更时稳定渲染容器快照；运行完整 `Pi.Tui.Tests` 与 `Pi.CodingAgent.Tests`。 | 本轮已完成本机验收：Pi.Tui 23,875/23,875、Pi.CodingAgent 4,944/4,944；整体阶段 9 仍受上述 P1/P2 项阻塞。 |
| **11. Jint 插件包与 keyed Context 生命周期** | `Pi.Chord/Context`、Facet service slots、keyed instance directory/remote consumer，以及 `Pi.CodingAgent/Experimental/Plugins` Jint CommonJS host 与 context bridge。 | 对照 TS keyed observation 语义；每个 observer-entry 有独立可取消 Context，关闭/退订/reload/dispose 会取消；旧远程实例失效；CommonJS bundle、同引擎回调、Context key identity 和 AbortSignal 事件有真实集成测试；Chord/CodingAgent 定向与完整测试、solution build 通过。 | 已完成并推送（`a3b7b0c`）。 |
| **12. Client TUI `/compact` dispatch parity** | `tests/Pi.CodingAgent.Tests/ClientTuiTests.cs` 与 `ClientTuiFixture`；经现有 slash-command/AgentController wire 调用验证，不改生产逻辑。 | 覆盖有/无自定义指令、请求参数形状、pending 时的 `Compacting…` 状态及成功后清理；Client TUI 类测试通过。 | 已完成并推送代码测试（`f9be16f`）。 |
| **22. Anthropic OIDC federation（G7）** | `src/Pi.Ai/Auth/AnthropicFederation.cs`、`src/Pi.Ai/Api/AnthropicMessages.cs`、`tests/Pi.Ai.Tests/AnthropicFederationTests.cs`。按 Anthropic SDK 0.129 的 exchange/cache 语义以 `HttpClient` 实现，不引入 vendor SDK。 | 1) 无显式认证且 provider 为 `anthropic` 时自动换取令牌并注入 `Authorization: Bearer` 与 `anthropic-beta`；2) exchange 请求体/头形状与 SDK 一致；3) 缓存阈值、顾问式刷新、并发合并、文件轮换与 SDK 一致；4) 非回环明文端点、空/超大身份令牌、畸形响应显式失败且不泄露 assertion；5) 显式 key/token/header 优先；6) `Pi.Ai.Tests` 全绿、solution 0 警告。 | **已完成（2026-10-21）**：`Pi.Ai.Tests` 426/426；实现与台账同批提交推送。 |
| **23. 外部主机验收（G4 + G6）** | 在 Ubuntu 24.04 验收主机上用 Docker 29.7.1 构建两镜像并执行容器探针/编排 smoke；交叉发布 linux-x64 测试二进制，运行 Unix socket 与真实 sshd 端到端。 | 1) 两镜像 ID 不同、布局校验通过；2) sandbox UID 读评估器程序集失败、root 读取成功；3) 10 个任务精确选择并产出报告，失败正确传播；4) POSIX `ServerClientEndToEndTests` 9/9、`Pi.Env.Tests` 48/48；5) 工作区干净、提交已推送。 | **已完成（2026-10-21）**：两镜像构建与探针通过，10/10 任务执行且失败按预期传播；Linux 传输/SSH 套件全绿。详见下方 Stage 23 记录。 |

### 特殊范围决策

- SQLite 实现移植的是 portable SQLite storage contract；Node `node:sqlite`、Cloudflare binding 等 JS 适配器不逐字移植，而由 `Microsoft.Data.Sqlite` 提供 .NET 后端。存储可观察语义、schema 演进和失败行为仍必须一致。
- Stage 6 采用决策 B：不复制 Bun 编译产物、Bun sandbox `/proc/self/environ` 恢复垫片或 Bun-specific worker 入口；保留 CLI 的进程环境变量、内部角色分派、外部子进程和发布资源定位。`runtime-setup.ts` 的静态 OAuth/Bedrock 接线在 .NET 由 OAuth/provider 注册表直接表达；QuickJS WASM 路径由宿主注入，执行引擎仍是明确的 `ICodemodeJsEngine` 扩展点。
- Stage 11 为实验插件 bundle 采用 Jint CommonJS 执行器作为默认 .NET host；manifest 声明的 import 必须由 host resolver 支持，目前没有模拟 Node/Bun 的内置模块、全局对象或动态加载钩子，未支持的 import 明确失败。此决策解决 bundle 默认不可执行的问题，不宣称提供完整 Node/Bun runtime parity。
- Stage 19 的容器权限探针以 `Pi.Evals.dll` 为对象，而不是 TS 的 `evals/src` 目录：.NET 镜像只发布编译产物，评估器定义已编入程序集，镜像内也没有 TS 源码。`AssertRootOnly` 校验属主与权限位，`AssertSandboxCannotRead` 在 `setuid` 后以 sandbox 身份读取程序集；两者与 TS `entrypoint.ts` 的检查时机（启动布局校验、进入工具沙箱后）一致。`struct stat` 的 uid 偏移按 x86_64/arm64 分支处理。
- Stage 23 的容器产物属主接受非负 UID/GID：上游 `docker/entrypoint.ts` 的 `parseId` 只接受 ≥1，宿主以 root 运行时 `process.getuid()/getgid()` 会传入 0，上游在该组合下会失败；.NET supervisor 接受 0 以支持 root/CI 运行，sandbox UID/GID 仍要求为正。这是记录在案的兼容扩展，不改变非 root 路径的可观察行为。
- Stage 7 的 TS→C# 测试映射按可观察行为而非测试数量/名称逐项对应：compaction/cache 对照 `HarnessCompactionTests`、`HarnessContextCacheTests`、context selection/serialization 与 docs 状态测试；generation recovery 对照 `HarnessGenerationRecoveryTests`；structured/tasks/ownership/tasks-recovery 对照 `HarnessSchedulerTests`、`HarnessTaskGraphTests`、`HarnessInboxTests`、`HarnessSubmissionsTests`、`HarnessLifecycleTests`、`HarnessInspectTests`；conversations 对照 `HarnessIntegrationTests`、`HarnessSubmissionsTests`、`HarnessInboxTests`；live deltas 对照 `HarnessEventsTests`、`HarnessDocsTests`、`HarnessOutputTests`；tools recovery 对照 `HarnessToolTests` 与 generation recovery 场景。新增恢复断言覆盖已落摘要重开幂等、hook 中断重开、阻塞 compaction 重开、后台 compaction 不阻塞 run/idle wait、overflow 重试/不重复压缩，以及 TS transcript 中工具参数的 `key=JSON` 格式。 |
- Stage 6 将上游 `packages/coding-agent` 的 `package.json`、README、CHANGELOG、docs 与 examples 复制为 `Pi.CodingAgent` 输出资源；逐文件核验 45 个 docs 文件、142 个 examples 文件，README/CHANGELOG/package.json 的 SHA-256 与源相同。为通过 whitespace 检查，`compaction.md` 的空白行去掉尾随空格，`qna.ts` 示例用 `\u0020` 保留模板字符串输出空格。这样兑现 `Config.GetReadmePath/GetDocsPath/GetExamplesPath/GetChangelogPath` 和 system prompt 中的路径契约；Stage 9 将随包 README 改为 .NET 说明，上游 docs/examples 明确保留为 TypeScript/Node 参考。
- `packages/client/src/unix.ts` 在 Node/Bun 的 Windows 平台显式拒绝 Unix transport；`Pi.Client` 目前利用 .NET Unix-domain socket 实现，在支持该能力的 Windows 系统上也开放该 API。这是记录在案的 .NET 扩展行为，不声称与源的 Windows 拒绝完全一致；Windows 上 `ServerClientEndToEndTests` 9/9 通过，Linux 上同套件 9/9 通过（Stage 23）。
- Vacation 会话锁由 .NET 对 `.vacation.lock` 的 `FileShare.None` 独占句柄实现；进程退出时操作系统释放锁，但空锁文件保留。与 TS `proper-lockfile` 的 10 秒 stale-lock 等待/清理策略不同，重开没有 stale 等待。HTTP 全局 dispatcher 仍按既有差异 C32 处理：保留代理环境变量播种及 Harness stream timeout 映射，不伪造 Undici global dispatcher 的等价实现。
- `index.ts` 纯导出桶、Node/Bun 专属测试启动器等无独立 C# 语义的文件可不产生一一对应文件，但要在最终公开 API/平台矩阵中有可复核的逐项说明。
- 2026-10-20 复核确认：C362 的 SQLite-backed harness/session-worker 接线已存在；codemode 扩展 renderer 已实现；4g-3h 中 Bun sandbox/启动垫片、Node 动态加载钩子和 re-export barrel 没有可直接照搬的 .NET 运行时语义。`experimental/durable` 与 `experimental/vacation` 共用实现由 `ExperimentalProfile` 表达，不重复移植两份相同 TS 文件。
- `tools/` 下被 `.gitignore` 排除的覆盖/导出面脚本仅作启发式线索；未匹配文件名或导出 token 不等价于功能缺失，也不代表公开契约已核验完成。
- 2026-10-20 复核确认：C362 的 SQLite-backed harness/session-worker 接线已存在；codemode 扩展 renderer 已实现；4g-3h 中 Bun sandbox/启动垫片、Node 动态加载钩子和 re-export barrel 没有可直接照搬的 .NET 运行时语义。`experimental/durable` 与 `experimental/vacation` 共用实现由 `ExperimentalProfile` 表达，不重复移植两份相同 TS 文件。
- `tools/` 下被 `.gitignore` 排除的覆盖/导出面脚本仅作启发式线索；未匹配文件名或导出 token 不等价于功能缺失，也不代表公开契约已核验完成。

## 验证基线、风险与限制

- Stage 7 复验：`dotnet build .\Pi.slnx --no-restore -m:1` 通过，0 警告、0 错误；`dotnet run --no-restore --project .\tests\Pi.Durable.Tests\Pi.Durable.Tests.csproj` 共 398 项，396 通过、0 失败、2 项跳过；跳过项仅为 `ToolsTests.Bash_NonZeroExit_Throws` 和 `ToolsTests.Bash_StreamsOutputThroughApiAndReturnsNoContent`，当前 Windows 环境的 Git Bash/MSYS2 初始化失败已有逐项说明；`git diff --check` 通过。
- Stage 8：`Pi.Evals.Tests` 52/52 通过；`Pi.Evals --help` 退出码 0；完整 solution 构建 0 警告、0 错误；提交 `f4b03aa` 已推送至 `origin/main`。当日本机无 Docker、Podman、nerdctl 且 WSL 无发行版，镜像构建和容器运行未验证；该验收已于 Stage 23 在外部 Ubuntu 主机完成。
- Stage 9 本机复验：`dotnet build .\Pi.slnx --no-restore -m:1` 通过，0 警告、0 错误；12 个测试项目总计 29,799 项，29,797 通过、0 失败、2 项跳过。跳过项仅为 `ToolsTests.Bash_NonZeroExit_Throws` 与 `ToolsTests.Bash_StreamsOutputThroughApiAndReturnsNoContent`，因 Git Bash/MSYS2 在当前 Windows 环境无法初始化；Pi CLI 与 Evals CLI `--help` 退出码均为 0，包资源测试 1/1 通过，`git diff --check` 通过。全量复验还发现并修正了测试中的已退出子进程、过期 session-worker 断言和打包 changelog 干扰；修正后全量套件无失败。
- Stage 9 当时的本机外部验收未完成：本机无容器运行时/WSL，无法执行 Docker eval；也没有 POSIX runner 可核验 Unix socket 与公开接口/JSON 边界。该限制已于 Stage 23 解除：G4 容器验收与 G6 POSIX/SSH 均已在外部 Ubuntu 主机通过。
- 上述 29,799 项是历史快照，已由本轮复验取代：`dotnet build Pi.slnx --no-restore -m:1 -v:minimal` 通过，0 警告、0 错误；13 个测试项目共 29,942 项，29,941 通过、0 失败、1 项 sshd 测试因本机无 sshd 跳过。`Pi.CodingAgent.Tests` 为 4,943/4,943、`Pi.Durable.Tests` 为 398/398、`Pi.Env.Tests` 为 46/46 加 1 跳过。PortableGit 仅通过测试进程环境配置启用，没有改机器级或仓库配置。
- Stage 21 最终全量复验（取代上述历史计数）：`dotnet build Pi.slnx -m:1 -v:minimal` 通过，0 警告、0 错误；13 个测试项目共 30,077 项，30,072 通过、0 失败、5 跳过。分布：`Pi.Tui` 23,875、`Pi.CodingAgent` 4,953、`Pi.Durable` 452、`Pi.Ai` 402、`Pi.Chord` 104、`Pi.Evals` 52+2 跳过、`Pi.Mcp` 47、`Pi.Env` 44+3 跳过、`Pi.Agent` 33、`Pi.Server` 33、`Pi.Protocol` 14、`Pi.Telemetry` 12、`Pi.Codemode` 51。跳过原因为 2 项 Linux-only Evals 权限测试、2 项无 POSIX shell、1 项无 sshd。复验中修正了 `ModelCatalogRefresh.RefreshOverride` 的并行测试污染（改为 `AsyncLocal`）和 Durable 进度节流断言基准（改为写入开始时刻）；修正后两个套件全绿，并各以类级 5 轮复跑。
- CLI smoke：Pi CLI 与 `Pi.Evals` 的 `--help` 退出码为 0；`Pi.DurableHost` 与 `Pi.Vacation` 对 `--help` 以退出码 1 拒绝未知参数，这与 TS 宿主只支持 `--continue`/`-c` 的参数契约一致。`git diff --check` 通过。
- `dotnet build Pi.slnx -m:1 --no-restore`：此前阶段基线通过，0 警告、0 错误；Stage 7 复验结果见上一条。
- `dotnet test --solution` 在当前 SDK/Microsoft.Testing.Platform 配置下不能作为入口；本机可直接执行 `tests\<项目>\bin\Debug\net10.0\<项目>.exe` 并使用 MTP 的 `--filter-method`、`--filter-class` 等筛选参数。每阶段记录实际命令和结果。
- `Pi.Durable.Tests` 的阶段 1 全量执行曾卡在 inbox 用例；阶段 2 将测试 fixture 的默认扩展选择校正为 TS 的 undefined 语义，并修复了 `Session.Enqueue` 并发调用时 `_tail` 链接非原子的竞态。阶段 2 最终全量复验：370 项通过、2 项因本机缺少可用 Git Bash 而跳过、0 失败；期间两次全量运行中的时序敏感输出测试曾分别偶发失败，单测复跑及之后的全量复验通过，故保留为时序稳定性观察项而非环境豁免。
- 早期 harness inbox 挂起及 coding-agent 4,797 项测试记录均为历史诊断；本轮全量测试项目运行完成且无失败，当前测试数见上述复验结果。
- Stage 11 复验：`dotnet build Pi.slnx --no-restore -m:1` 通过，0 警告、0 错误；`Pi.Chord.Tests` 104/104、`PluginBundlesTests` 12/12、`JintContextBridgeTests` 3/3、完整 `Pi.CodingAgent.Tests` 4,948/4,948 通过，0 失败；`git diff --check` 通过。
- Stage 18（公开契约与 wire 审计）：`dotnet build Pi.slnx --no-restore -m:1 -v:minimal` 通过，0 警告、0 错误；`Pi.Ai.Tests` 402/402（新增 OpenAI wire 32 项、Anthropic wire 10 项）；`Pi.Mcp.Tests` 47/47（新增客户端 4 项、真实 Node stdio 8 项）；`git diff --check` 通过。审计导出面 1,426 项；当时唯一功能缺口为 G7（Stage 22 已补齐），容器/POSIX 验收当时受环境阻塞（Stage 23 已完成）。
- Stage 19（Evals 权限探针）：`dotnet build tests/Pi.Evals.Tests/Pi.Evals.Tests.csproj --no-restore -m:1` 通过，0 警告、0 错误；`Pi.Evals.Tests` 54 项，52 通过、0 失败、2 项 Linux-only 权限测试因 Windows 跳过；镜像构建与容器内实证仍未执行。
- Stage 20（容器/POSIX 验收尝试）：`docker`、`podman`、`nerdctl` 均不存在；`wsl.exe -l -v` 报告未安装发行版；Windows 未安装 sshd 服务，Git Bash 的 `sshd` 按上游 win32 规则不参与测试。`Pi.Env.Tests` 47 项，44 通过、0 失败、3 跳过（2 无 POSIX shell、1 无 sshd）；`ServerClientEndToEndTests` 9/9；`dotnet publish src/Pi.Evals -c Release -r linux-x64 --self-contained false` 成功，产物含 `Pi.Evals.dll` 与 Docker 资源。该次尝试证明本机不可执行；容器镜像构建/运行、POSIX Unix socket 与 SSH 链路随后在 Stage 23 完成。
- Stage 22（G7 Anthropic OIDC federation，取代上述全量计数）：`dotnet build Pi.slnx --no-restore -m:1 -v:minimal` 通过，0 警告、0 错误；13 个测试项目共 30,101 项，30,096 通过、0 失败、5 跳过（2 项 Linux-only Evals 权限测试、2 项无 POSIX shell、1 项无 sshd）。分布：`Pi.Tui` 23,875、`Pi.CodingAgent` 4,953、`Pi.Durable` 452、`Pi.Ai` 426（较 Stage 21 新增 24 项 federation 测试）、`Pi.Chord` 104、`Pi.Evals` 52+2 跳过、`Pi.Mcp` 47、`Pi.Env` 44+3 跳过、`Pi.Agent` 33、`Pi.Server` 33、`Pi.Protocol` 14、`Pi.Telemetry` 12、`Pi.Codemode` 51；`git diff --check` 通过。
- Stage 23（G4 + G6 外部主机验收，最终全量计数）：`dotnet build Pi.slnx --no-restore -m:1 -v:minimal` 通过，0 警告、0 错误；本机 13 个测试项目共 30,103 项，30,098 通过、0 失败、5 跳过（2 项 Linux-only Evals 权限测试、2 项无 POSIX shell、1 项无 sshd）。分布：`Pi.Tui` 23,875、`Pi.CodingAgent` 4,953、`Pi.Durable` 452、`Pi.Ai` 426、`Pi.Chord` 104、`Pi.Evals` 54+2 跳过（新增 2 项选择回归）、`Pi.Mcp` 47、`Pi.Env` 44+3 跳过、`Pi.Agent` 33、`Pi.Server` 33、`Pi.Protocol` 14、`Pi.Telemetry` 12、`Pi.Codemode` 51。外部 Ubuntu 24.04.4（Docker 29.7.1、OpenSSH 9.6p1）：`without_docs`/`with_docs` 镜像 ID 不同，布局与权限探针通过，10 个 docs eval 任务产出逐任务 `vitest.json`、`observations.jsonl` 与报告，假模型失败以退出码 1 传播。Linux 交叉发布测试：`Pi.Server.Tests` 33/33（UDS E2E 9/9）、`Pi.Env.Tests` 48/48（真实 sshd 扫描/信任/部署/远程执行）。`git diff --check` 通过。
- Stage 20 补充探测（2026-10-21）：Git for Windows 自带 `sshd.exe`（`/usr/bin/sshd.exe`）实际可用，已在本机启动并验证公钥登录与 `sh -c` 执行；但 MSYS sshd 的登录 shell 是 bash，超出上游 SSH 引导覆盖的 cmd/powershell 场景——探测按 `echo %OS%` 会把 bash 误判为 powershell，启动命令 `& '...'` 在 bash 下无效，且该会话中 `powershell` 无法解析（`powershell.exe` 可以）。TS 侧是同一逻辑，因此这不是移植差异，也不能作为 POSIX/SSH 验收替代；完整验收仍需要 POSIX 主机，或管理员安装 Windows OpenSSH Server 后以 cmd 为默认 shell 运行。本次探测未写入用户 `~/.pi`，临时 sshd 与密钥已清理。
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
| 8. Evals | 新增 `Pi.Evals` CLI 和 Docker 镜像/worker，移植 host eval、5 个 docs scenario、协议发现与报告；增加 C# extension authoring 文档与上游 docs 导航。 | `dotnet build .\Pi.slnx --no-restore -m:1`：0 警告、0 错误；Evals 测试 52/52；CLI `--help` 通过；项目输出含 README/docs/examples/Dockerfile；`git diff --check` 通过。Docker、Podman、nerdctl 均不存在且 WSL 无发行版，因此镜像 build 和隔离运行未验证，作为显式剩余风险。 | `f4b03aa feat(evals): add native .NET evaluation runner` 已提交并推送；容器级验收当时受环境阻塞，后由 Stage 23 完成。 |
| 9. 最终一致性与交付 | 修正全量复验发现的测试假设；将根 README 与打包说明适配为 .NET，并标注上游 docs/examples 的适用范围；复核源码缺口并重新验证 solution、全部测试和 CLI。 | `dotnet build Pi.slnx --no-restore -m:1 -v:minimal`：0 警告、0 错误；13 个测试项目共 29,942 项，29,941 通过、0 失败、1 项 sshd 跳过；Pi CLI 与 Evals CLI `--help` 退出码 0；Durable/Vacation 宿主对 `--help` 按 TS 契约拒绝未知参数；`git diff --check` 通过。 | 本机可运行验收通过；Docker/POSIX 随后由 Stage 23 完成，公开契约由 Stage 18 完成，阶段完成。原实现记录提交 `309fb30` 已推送；后续台账更新（含本阶段）持续记录并单独推送。 |
| 11. Jint 插件包与 keyed Context 生命周期 | 默认插件 host 改为 Jint CommonJS 执行；修复同 Jint 引擎回调重入；桥接 Context key identity、AbortSignal 事件及监听器释放；将 keyed observation 改为按 observer-entry 创建/取消 Context，并覆盖 remote reload 的旧实例失效。 | `dotnet build Pi.slnx --no-restore -m:1`：0 警告、0 错误；`Pi.Chord.Tests` 104/104、`PluginBundlesTests` 12/12、`JintContextBridgeTests` 3/3、完整 `Pi.CodingAgent.Tests` 4,948/4,948；`git diff --check` 通过。 | `a3b7b0c feat(plugins): align Jint bundles and keyed contexts`，已推送 `origin/main`。 |
| 12. Client TUI `/compact` dispatch parity | 新增经 loopback AgentController 服务调用的 Client TUI 覆盖；分别验证自定义指令和空参数，并将 compact 响应 gate 住以观察 pending 状态及完成清理。 | `dotnet build .\tests\Pi.CodingAgent.Tests\Pi.CodingAgent.Tests.csproj --no-restore -m:1`：0 警告、0 错误；`ClientTuiTests.CompactCommandForwardsOptionalInstructionsAndShowsProgress` 2/2；`ClientTuiTests` 类 10/10。 | `f9be16f test(client): cover compact slash command`，已推送 `origin/main`。 |
| 13. JSONL 存储后端（G1） | 按 `storage/jsonl/storage.ts` 逐段移植：主标记 + `doc-*/task-*` 侧车、严格 UTF-8/撕裂行处理、侧车确认与未确认尾截断、current-only 文档与 terminal 任务的回收、`.reclaim` 原子替换、毒化与重开语义；为 `MemoryStorage` 抽出 `PrepareCommit`（先校验后落盘）。新增 `JsonlStorageTests`、`JsonlStorageConformanceTests`、`JsonlStorageAcrossReopenConformanceTests`。另将 ID 铸造收紧到 JS safe-integer 边界（`2655c56`）。 | `dotnet build Pi.slnx --no-restore -m:1`：0 警告、0 错误；JSONL 定向 46/46（conformance 23 + 跨重开 23，另有定点恢复/回收/损坏用例），完整 `Pi.Durable.Tests` 452/452 连续 3 轮通过；其间修复了 partial 提交竞态导致的偶发失败（`2b53885`）。 | `8535249 feat(durable): 新增 JSONL 存储后端并接通扩展 overlay 句柄`、`2655c56 fix(durable): reject ids beyond the JS safe-integer range`、`2b53885 test(durable): wait for the committed partial before closing`。 |
| 14. CodeMode 默认引擎（G2） | 新增 `JintCodemodeEngine`：每次执行独立 `Engine`，复用共享 prelude，`bridge` 经 `ClrFunction` 接入 `call/global/output/done/crash` 协议；`SandboxOptions.Engine` 与 `CodemodeExecute.EngineDefaults` 默认使用 Jint，`UnsupportedCodemodeJsEngine` 保留为显式禁用。host 增加延迟回复队列，修复同步工具调用早于 VM 绑定完成的竞态。 | `Pi.Codemode.Tests` 51/51（含 10 个真实 Jint 集成测试），Codemode 输出/默认引擎定向 7/7；solution 0 警告、0 错误。覆盖工具调用与输出、globals、脚本/语法错误、永不 settle 的 promise、工具失败、store 写回、超时停止后复用、递归保护与执行隔离。 | `af67fa0 feat(codemode): run scripts with a default Jint engine`。 |
| 15. 扩展 Overlay 句柄（G3） | `OverlayHandle` 由空占位改为包装 `Pi.Tui.IOverlayHandle` 的七成员契约；`InteractiveMode` 把 `ShowOverlay` 的真实句柄交给扩展回调，不再丢弃；并修复 TUI `Stop()` 与进行中渲染回调的竞态（`fc56e1b`）。 | `OverlayHandle_ForwardsControlToTuiHandle`：show/hide/setHidden/focus/unfocus/getBounds 逐项转发，移除后重复调用无异常；完整 `Pi.Tui.Tests` 23,875/23,875；solution 0 警告、0 错误。 | 实现 `8535249`，转发/移除测试 `e6ae911`，渲染竞态修复 `fc56e1b`。 |
| 18. 公开 API 与 wire 审计 | 解析 13 个包的 barrel 导出并逐项分类：1,426 = 442 runtime + 945 type + 39 unknown（36 个 async function、3 个纯类型）；1,014 个同名命中，412 个未同名项归入类型层映射、别名落点或平台排除。新增 OpenAI Chat Completions 32 项、Anthropic Messages 10 项 wire 测试与 MCP 4 项客户端/8 项真实 stdio 测试；修复 OpenAI 工具结果图片与 Anthropic 原生工具变更的 `JsonNode` 重复挂载缺陷。登记 G5 平台排除与 G7 OIDC federation 缺口。 | `dotnet build Pi.slnx --no-restore -m:1 -v:minimal`：0 警告、0 错误；`Pi.Ai.Tests` 402/402；`Pi.Mcp.Tests` 47/47；`git diff --check` 通过。 | 结论见 `docs/public-api-audit.md`，随本阶段提交推送。 |
| 19. Evals 容器权限探针（G4） | 新增 `EvalFileProbe`：`.NET` 镜像不含 TS 源码，评估器定义编译进 `Pi.Evals.dll`，因此对该程序集执行 root 属主 + 无 group/other 位校验；`EnterToolSandbox` 在 `setuid` 后以 sandbox 身份验证程序集不可读。`VerifyImageLayout` 改为调用同一探针。 | `dotnet build tests/Pi.Evals.Tests/Pi.Evals.Tests.csproj --no-restore -m:1`：0 警告、0 错误；`Pi.Evals.Tests` 54 项，52 通过、0 失败、2 项 Linux-only 跳过（Windows 无 Unix 权限）；`git diff --check` 通过。 | 实现与记录随本阶段提交推送；镜像构建/隔离 smoke 仍待容器运行时。 |
| 20. 容器与 POSIX/SSH 验收尝试 | 探测容器运行时、WSL 与 sshd；执行 Windows 可运行的环境/传输测试；交叉发布 linux-x64 版 Evals 以验证发布阶段。 | `docker`/`podman`/`nerdctl` 均 not found；WSL 已安装但无发行版；Windows 未安装 sshd 服务（Git Bash 的 sshd 按 TS win32 规则跳过）。`Pi.Env.Tests` 44/47（3 跳过：2 无 POSIX shell、1 无 sshd）；`ServerClientEndToEndTests` 9/9；`dotnet publish src/Pi.Evals -c Release -r linux-x64 --self-contained false` 成功产出 `Pi.Evals.dll`。以上仅为可执行部分，容器与 POSIX 验收未宣称通过。 | 本记录文档提交；无代码变更。 |
| 21. 最终收口与全量复验 | 修复并行测试类共享 `ModelCatalogRefresh.RefreshOverride` 导致 model selector 无结果路径偶发失败的隔离问题（改为 `AsyncLocal`）；将 Durable 进度节流断言改为以写入开始时刻为基准，匹配 TS 语义。全量构建、13 个测试项目、CLI smoke 与台账收口。 | `dotnet build Pi.slnx -m:1 -v:minimal`：0 警告、0 错误；13 个测试项目共 30,077 项，30,072 通过、0 失败、5 跳过（2 项 Linux-only Evals 权限测试、2 项无 POSIX shell、1 项无 sshd）；Pi CLI 与 Evals CLI `--help` 退出 0，DurableHost/Vacation 按 TS 契约以退出码 1 拒绝未知参数；`git diff --check` 通过。 | 代码修正 `abd3c6a` 与本记录提交推送；G4 镜像级、G6 POSIX、G7 OIDC federation 仍未完成。 |
| 22. Anthropic OIDC federation（G7） | 新增 `AnthropicFederation`：RFC 7523 jwt-bearer exchange、SDK 等价 token 缓存（>120s 直用 / 30–120s 顾问式刷新 / ≤30s 阻塞刷新、并发合并、失败退避）、身份令牌文件每次重读、16 KiB assertion 与 1 MiB 响应上限、非回环 https 强制与 RFC 6749 错误字段脱敏；`AnthropicMessages.Stream`/`StreamSimple` 在 provider 为 `anthropic` 且无显式认证时注入 federation Bearer 与 `oauth-2025-04-20` beta；显式 API key/auth token/header 保持优先。 | `dotnet build Pi.slnx --no-restore -m:1 -v:minimal`：0 警告、0 错误；`Pi.Ai.Tests` 426/426（新增 federation auth 5、wire 18、`Models` 端到端 1）；13 个测试项目全量复验见验证基线；`git diff --check` 通过。 | 随本台账同批提交推送；G4 镜像级与 G6 POSIX 验收当时受环境阻塞，后由 Stage 23 完成。 |
| 23. 外部主机验收（G4 + G6） | 在 Ubuntu 24.04.4（Docker 29.7.1、OpenSSH 9.6p1）上以 `src/Pi.Evals/Docker/Dockerfile` 构建 `without_docs` / `with_docs` 镜像，运行容器布局/权限探针与 host 编排 smoke；交叉发布 linux-x64 测试二进制，运行 Unix socket 与真实 `sshd` 端到端（守护进程由上游 Rust 源码构建）。验收中修复 3 个实现缺陷：布局扫描把 docs/examples 的 `.ts` 当作评估器源码、任务名过滤使用发现态 ` > ` 形式而非 vitest 空格形式、空文件列表被当作零匹配；另有 1 项 root 运行兼容扩展：上游 `parseId` 仅接受正整数，root 调用者传入 0/0 会失败，.NET 接受非负 ID 以支持 root/CI 运行。另修正 2 个仅 Linux 暴露的测试假设（stdout/stderr 跨流顺序、指纹整行）。 | 镜像 `without_docs` `sha256:9f7f1b21ab381401440973ec7ccd9f0168424bba9e21dd312c8a765032f7267b`、`with_docs` `sha256:e3b05b57b98bae9538e2717b8ccb468245b24d395313a86c740c0a642f4b6f06`。两变体布局校验通过；`/app/Pi.Evals.dll` 为 root 属主 0400；`setpriv --reuid=65532` 读取失败、root 读取成功；两变体发现均输出相同 5 个用例。host 编排 5 用例 × 2 变体 = 10 个任务全部执行：`expected-runs.json` 10 项、`observations.jsonl` 10 行、每个 `vitest.json` 精确命中单个用例、10 个任务各写入 `vitest.json`；`report.txt` 列出 5 个 blocked pair，退出码 1（假模型预期）。Linux：`Pi.Server.Tests` 33/33（UDS E2E 9/9）、`Pi.Env.Tests` 48/48（真实 sshd 扫描/信任/部署与远程写读执行）。 | 代码、测试与台账同批提交推送。 |

## 2026-10-20 独立静态复核

### 复核限制（先读）

本轮 `execute_command` 无法启动 shell：`spawn C:\Users\Xzy\AppData\Local\Microsoft\WindowsApps\pwsh.exe ENOENT`。本机存在 Windows PowerShell 5.1（`C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe`），但工具固定调用 PowerShell 7 的 app execution alias。因此本轮：

- 未运行 `dotnet build`、任何测试项目或 CLI 烟测；
- 未运行任何 `git` 命令，**未提交、未推送**；
- 以下全部结论来自源码逐文件静态对比，是“证据 + 判断”，不是运行结果。台账中历史阶段的构建/测试数字仍以其原提交记录为准。

### 复核方法

以 `packages/*/src`（TS，1,570 个 `.ts`）对 `src/Pi.*`（C#）逐包比对：先按目录统计两侧文件，再对无同名落点的 TS 文件做人工判定（区分桶文件、纯类型、平台专属适配器与真实功能）；同时检索 C# 侧 `NotImplementedException`、`TODO`、`Unsupported*`、空占位类型与从未赋值的注入点。

### 结论

12 个包（telemetry、protocol、agent、mcp、server、client、ai、chord、codemode 的编排层、tui、env、durable 的 memory/sqlite 层）文件级覆盖完整，未发现结构性缺失。发现 **3 处真实功能缺口** 与 **3 项受环境阻塞的验收**（截至 2026-10-21 均已关闭：G1–G3 于 Stage 13–15，G4/G6 于 Stage 23）：

| ID | 缺口 | TS 证据 | .NET 现状 | 影响 | 优先级 |
|---|---|---|---|---|---|
| **G1** | JSONL 纯文件存储后端缺失 | `packages/durable/src/storage/jsonl/storage.ts:1-880`、`node.ts`、`test/jsonl-storage.test.ts`（含跨重开 conformance 与恢复/回收用例） | `src/Pi.Durable/Storage` 仅 `MemoryStorage`、`SqliteStorage`；无 `JsonlStorage` | 无法用“目录 + JSONL 文件”持久化会话；TS 的第三套后端与其恢复语义在 .NET 无对应物 | **P1** |
| **G2** | CodeMode 无默认 JS 引擎 | `packages/coding-agent/src/extensions/codemode/{execute.ts,worker.ts}`、`packages/codemode/src/runtime/worker.ts`（QuickJS WASM，每次执行新建 worker） | `src/Pi.Codemode/Runtime/Engine.cs:53` 只有 `UnsupportedCodemodeJsEngine`；`src/Pi.CodingAgent/Extensions/Codemode/CodemodeExecute.cs:59-63` 的 `EngineDefaults.Override` 在生产代码中无任何赋值点 | 内置 `codemode/execute` 工具开箱即以 `kind: "sandbox"` 失败，仅测试/宿主注入后才可用 | **P1** |
| **G3** | 扩展 Overlay 句柄仍是空占位 | `packages/coding-agent/src/core/extensions/types.ts`（`OverlayHandle` 的 show/hide/setHidden）、`packages/tui/src/tui.ts`（真实 overlay 句柄） | `src/Pi.CodingAgent/Core/Extensions/Types/Placeholders.cs:119` 空类；`src/Pi.CodingAgent/Modes/Interactive/InteractiveMode.cs:1281-1285` 丢弃 `_ui.ShowOverlay(...)` 返回值并回传 `new OverlayHandle()` | 扩展拿到的是无行为对象，无法隐藏/恢复自己弹出的 overlay；TS 的 UI 契约在该路径上未兑现 | **P2** |
| **G4** | Evals 容器内权限探针未移植 | `packages/evals/docker/entrypoint.ts:73-101,127-130`（`assertRootOnly` / `assertSandboxCannotRead`） | **已实现并验收（2026-10-21）**：`EvalFileProbe` 以 libc `stat` 校验 root 属主与无 group/other 位，降权后在进程内验证入口程序集不可读；`VerifyImageLayout` 与 `EnterToolSandbox` 均接入。.NET 镜像把评估器定义编译进 `Pi.Evals.dll`，因此探针对象是程序集而非 TS 源码目录。Stage 23 在真实镜像中验证：sandbox UID 读取失败、root 读取成功。 | 无功能缺口；镜像构建、布局、权限探针与 10 任务编排均已在 Ubuntu + Docker 29.7.1 通过。 | **已完成** |
| **G5** | Cloudflare Workers AI binding 路径 | `packages/ai/src/api/cloudflare-ai-binding.ts`、`api/cloudflare.ts` | 无；只有 `Providers/CloudflareWorkersAi.cs` 的 HTTP 路径 | 该模块是 workerd `env.AI.fetch()` 适配，属平台专属；建议按 Node/Bun 适配器同一规则**明确排除**并在文档登记，而不是补建 | **P3** |
| **G6** | POSIX transport / SSH 端到端 | `packages/client/src/unix.ts`、`packages/env/test/sshd.test.ts` | `Pi.Client`/`Pi.Server`/`Pi.Env` 已实现；Stage 23 在 Ubuntu 24.04 上以 linux-x64 二进制验收：`Pi.Server.Tests` 33/33（含 Unix socket 端到端 9/9）、`Pi.Env.Tests` 48/48（真实 `sshd` 扫描/信任/部署/远程读写执行，Rust 守护进程由上游源码构建）。Windows 侧此前 `ServerClientEndToEndTests` 9/9。 | 无功能缺口；POSIX Unix socket、发现/握手与 SSH 引导链已通过。 | **已完成** |
| **G7** | Anthropic workload identity federation（OIDC） | `packages/ai/src/api/anthropic-messages.ts`（federation 配置与 SDK token exchange/refresh） | **已实现（2026-10-21）**：`Auth/AnthropicFederation.cs` 以 `HttpClient` 实现 SDK 等价的 jwt-bearer exchange 与 token 缓存/刷新，`AnthropicMessages.Stream` 在无显式认证且 provider 为 `anthropic` 时注入 Bearer 令牌与 `oauth-2025-04-20` beta；显式 API key/auth token/header 仍优先 | 无功能缺口；使用 federation 的部署可直接调用 Anthropic。新增 24 项测试覆盖解析、wire 形状、到期/轮换、并发与错误路径 | **已完成** |

补充说明：
- `chord/src/delta/draft.ts` 只是 10 行类型别名，`index.ts` 类桶文件不产生 C# 语义，二者不计为缺口。
- `packages/durable/src/storage/jsonl/node.ts` 是 `NodeExecutionEnv` 的薄封装；C# 已有 `IFileSystem` 抽象，G1 只需移植 `storage.ts` 的逻辑即可保持可移植性。
- `packages/ai` 的 `models.generated.ts`、`*.lazy.ts`、`bun-oauth.ts` 已在既有“特殊范围决策”中归并/排除，本轮复核未发现新的运行时缺口。
- `src/Pi.Durable`、`src/Pi.Codemode`、`src/Pi.Tui` 中未发现 `NotImplementedException` 或 TODO 桩；`NotSupportedException` 命中均为类型守卫或平台分支。

### 追加阶段计划（Stage 13–17）

依赖顺序按“可离线验证 → 需要新运行时 → 需要容器 → 需要 POSIX”排列；每阶段仍沿用本文件既有约定：先读 TS 实现与测试，再做最小范围 C# 实现，阶段结束跑定点测试 + solution 构建，记录差异后单独提交推送。

| 阶段 | 目标与文件范围 | 验收标准 | 依赖 | 状态 |
|---|---|---|---|---|
| **13. JSONL 存储后端（G1）** | 新增 `src/Pi.Durable/Storage/JsonlStorage.cs`（必要时拆 `JsonlStorageJson.cs`），复用 `IFileSystem`；新增 `tests/Pi.Durable.Tests/JsonlStorageTests.cs` 并注册 storage conformance。 | 1) storage conformance 23 项在 JSONL 后端全通过，含跨重开变体；2) 主日志/侧车文件的格式、`format`/`type` 校验与损坏拒绝文案与 TS 一致（`Malformed complete main.jsonl`、`Confirmed record follows an unconfirmed tail`、`Commit sequence does not strictly increase`、`Missing confirmed sidecar record`、`Invalid document content`）；3) current-only 文档与 terminal 任务的侧车回收、`.reclaim` 临时文件与崩溃恢复语义一致；4) `fsync` 选项、close 后拒绝、毒化后拒绝；5) 0 警告构建。 | 无（纯离线） | **已完成（2026-10-21）**：JSONL 定向 46/46、完整 Durable 452/452；提交 `8535249`。 |
| **14. CodeMode 默认引擎（G2）** | 在 `Pi.Codemode` 提供默认可用的 `ICodemodeJsEngine` 实现。**决策 A**：Jint 承载 prelude + 工具桥，默认接入 `CodemodeSandbox` 与 `codemode/execute`；QuickJS 专属 WASM 载荷不消费，`UnsupportedCodemodeJsEngine` 保留为显式禁用。 | 1) `codemode/execute` 在默认 CLI 下可执行 `tools.*` 调用并回传文本/图片/console 输出；2) 中止、超时、失控脚本停止、沙箱错误路径有测试；3) prelude 的 `settle/run/stalled` 消息序与 TS worker 一致；4) 同步工具调用在 host 绑定 VM 前完成的回复不丢失。 | 无 | **已完成（2026-10-21）**：`Pi.Codemode.Tests` 51/51（含 10 个真实 Jint 集成测试）、Codemode 输出/默认引擎定向 7/7；提交 `af67fa0`。 |
| **15. 扩展 Overlay 句柄（G3）** | `Placeholders.cs` 的 `OverlayHandle` 换成真实契约（show/hide/setHidden 或等价的 `IOverlayHandle`）；`InteractiveMode` 转交 `ShowOverlay` 返回的句柄并处理关闭后失效。 | 1) 扩展可隐藏/恢复自身 overlay；2) overlay 关闭后调用不抛异常且无副作用；3) 非 overlay 路径不受影响；4) `ExtensionManagerViewTests`/交互测试全绿。 | 无 | **已完成（2026-10-21）**：代码提交 `8535249`，转发/隐藏/聚焦/边界与移除后重复调用测试 `e6ae911` 通过；TUI `Stop()` 渲染竞态修复 `fc56e1b`。 |
| **16. Evals 容器验收（G4 + P1）** | 补齐 `assertRootOnly`/`assertSandboxCannotRead` 等价探针；在可用容器运行时构建两镜像并跑隔离 smoke。 | 1) 镜像内以 sandbox UID 读取评估器源码失败、root 读取成功；2) 两镜像 id 不同；3) 隔离 eval smoke 产出报告且失败正确传播。 | Docker/Podman/nerdctl 或 WSL 发行版 | **已完成（2026-10-21，Stage 23）**：外部 Ubuntu 主机 Docker 29.7.1 上构建双镜像并验证权限探针与 10 个任务编排。 |
| **17. 公开契约与 POSIX 收口（G6 + P2）** | 逐包导出面与 JSON/wire 形状核验；POSIX runner 上跑 Unix socket/发现/握手与 sshd 端到端。 | 1) 公开 API 差异逐项分类（补建或登记排除）；2) POSIX 传输与 SSH 链路通过；3) `git status` 干净、全部提交已推送。 | POSIX 主机 + sshd | **已完成（2026-10-21）**：公开契约部分见 Stage 18；POSIX 部分见 Stage 23（`Pi.Server.Tests` 33/33、`Pi.Env.Tests` 48/48）。 |
| **18. 公开 API 与 wire 审计（P2 公开契约部分）** | 解析 13 个包的 barrel 导出；逐项分类 runtime/type/unknown 并核对 C# 落点；为 OpenAI/Anthropic/MCP 序列化边界补 wire 测试；登记排除项与功能缺口。 | 1) 1,426 个导出全部归类，未同名项有类型层/别名/排除理由；2) OpenAI 与 Anthropic 请求体形状有结构断言；3) MCP progress/cancel/工具字段有真实传输测试；4) `Pi.Ai.Tests` 与 `Pi.Mcp.Tests` 全绿、solution 0 警告构建。 | 无（离线） | **已完成（2026-10-21）**：`Pi.Ai.Tests` 402/402、`Pi.Mcp.Tests` 47/47；结论与证据见 [public-api-audit.md](./public-api-audit.md)。 |
| **19. Evals 容器权限探针（G4）** | `EvalDockerRunner` 新增 `EvalFileProbe`：root 属主/权限位校验与降权读取探针；`VerifyImageLayout`、`EnterToolSandbox` 接入；新增 Linux-only 单元测试。 | 1) 组/其他用户可读的文件被拒绝；2) 降权后可读的文件触发失败；3) 非 Linux 显式拒绝执行而非静默通过；4) `Pi.Evals.Tests` 全绿（Linux-only 项在 Windows 跳过）。 | 无（探针逻辑）；镜像级验收需容器 | **已完成探针实现（2026-10-21）**：`Pi.Evals.Tests` 54 项，52 通过、2 项 Linux-only 跳过；镜像构建与隔离 smoke 随后在 Stage 23 完成。 |
| **20. 容器与 POSIX/SSH 验收尝试（G4 + G6）** | 探测本机容器运行时、WSL 发行版与 sshd；运行可执行的 Windows 传输/环境测试；以 linux-x64 交叉发布验证 Docker 发布阶段产物。 | 1) 有证据说明哪些验收不可执行；2) 可执行部分给出实测结果；3) 不以“不可用”替代通过结论。 | 容器运行时与 POSIX 主机 | **受阻并已取证（2026-10-21）**：docker/podman/nerdctl 均不存在，WSL 无发行版，Windows 未安装 sshd；`Pi.Env.Tests` 44/47（3 项跳过：2 无 POSIX shell、1 无 sshd），`ServerClientEndToEndTests` 9/9；`Pi.Evals` linux-x64 交叉发布成功但未运行。本地阻塞随后由 Stage 23 在外部 Ubuntu 主机解除。 |
| **21. 最终收口与全量复验** | 全 solution 构建、13 个测试项目全量执行、CLI smoke、并行测试桩隔离与 Durable 进度时序断言修正、台账收口。 | 1) solution 0 警告、0 错误；2) 全部套件 0 失败，跳过项有平台原因；3) CLI smoke 符合参数契约；4) 工作区干净、提交已推送。 | 无 | **已完成本机验收（2026-10-21）**：30,077 项，30,072 通过、0 失败、5 跳过；G4 镜像、G6 POSIX、G7 OIDC federation 随后由 Stage 22/23 完成。 |
| **22. Anthropic OIDC federation（G7）** | 新增 `src/Pi.Ai/Auth/AnthropicFederation.cs`；`Api/AnthropicMessages.cs` 接入 federation 认证路径；新增 `tests/Pi.Ai.Tests/AnthropicFederationTests.cs`。按 SDK 语义实现 exchange 与缓存，不引入 vendor SDK。 | 1) exchange 请求体与 beta 头有结构断言；2) 缓存阈值/顾问式刷新/并发合并/令牌轮换有定向测试；3) 错误体不泄露 assertion，明文端点与超大/空令牌被拒绝；4) `Models` 认证链端到端产生 Bearer；5) `Pi.Ai.Tests` 与 solution 构建全绿。 | 无（离线） | **已完成（2026-10-21）**：24 项新测试，`Pi.Ai.Tests` 426/426；实现与台账同批提交。 |

### 环境恢复后必须执行的最小验证

```powershell
dotnet build .\Pi.slnx --no-restore -m:1 -v:minimal
dotnet run --no-restore --project .\tests\Pi.Durable.Tests\Pi.Durable.Tests.csproj
dotnet run --no-restore --project .\tests\Pi.CodingAgent.Tests\Pi.CodingAgent.Tests.csproj
dotnet run --project .\src\Pi.Cli\Pi.Cli.csproj -- --help
git status --short
git diff --check
```

当前 shell 修复方式（任一）：安装 PowerShell 7 并启用 `pwsh` app execution alias；或把 IDE 的默认终端指向 `C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe` 后重试。

### Stage 13–15 变更清单与提交（2026-10-21）

| 文件 | 变更 | 提交 |
|---|---|---|
| `src/Pi.Durable/Storage/JsonlStorage.cs` | 新增 JSONL 存储后端（提交/恢复/回收/毒化/关闭） | `8535249` |
| `src/Pi.Durable/Storage/JsonlStorageJson.cs` | 新增主标记与侧车记录的编码/严格校验 | `8535249` |
| `src/Pi.Durable/Storage/MemoryStorage.cs` | 抽出 `PrepareCommit`（校验 → 落盘 → 应用）；ID 空间按 JS safe-integer 拒绝 | `8535249`、`2655c56` |
| `src/Pi.Durable/Storage/SqliteStorageJson.cs` | 暴露共用序列化选项 | `8535249` |
| `tests/Pi.Durable.Tests/JsonlStorageTests.cs` | 新增 conformance（含跨重开）与定点恢复/回收/损坏测试 | `8535249` |
| `tests/Pi.Durable.Tests/StorageConformance.cs` | 耗尽用例改用 `9_007_199_254_740_991` | `2655c56` |
| `tests/Pi.Durable.Tests/HarnessGenerationRecoveryTests.cs` | 等待 partial 实际提交后再关闭，消除竞态 | `2b53885` |
| `src/Pi.Codemode/Runtime/JintCodemodeEngine.cs` | 新增默认 Jint 引擎（prelude、bridge、settle、停止、栈/内存限制） | `af67fa0` |
| `src/Pi.Codemode/Runtime/Host.cs` | 默认引擎接入；VM 绑定前的延迟回复队列 | `af67fa0` |
| `src/Pi.Codemode/Runtime/Engine.cs`、`src/Pi.Codemode/Types.cs`、`src/Pi.CodingAgent/Extensions/Codemode/CodemodeExecute.cs` | 默认引擎切换与文档更新 | `af67fa0` |
| `src/Pi.Codemode/Pi.Codemode.csproj` | 引入 Jint 4.17.0 | `af67fa0` |
| `tests/Pi.Codemode.Tests/JintEngineTests.cs` | 10 个真实 Jint 集成测试 | `af67fa0` |
| `tests/Pi.Codemode.Tests/SandboxTests.cs` | 显式禁用引擎与同步引擎延迟回复回归 | `af67fa0` |
| `tests/Pi.CodingAgent.Tests/CodemodeOutputLayoutTests.cs` | 默认 Jint 引擎端到端与输出布局 | `af67fa0` |
| `src/Pi.CodingAgent/Core/Extensions/Types/Placeholders.cs` | `OverlayHandle` 改为真实句柄包装 | `8535249` |
| `src/Pi.CodingAgent/Modes/Interactive/InteractiveMode.cs` | 转交 `ShowOverlay` 句柄 | `8535249` |
| `tests/Pi.CodingAgent.Tests/ExtensionContractTests.cs` | overlay 转发/隐藏/边界/移除后调用测试 | `e6ae911` |
| `src/Pi.Tui/Tui.cs` | `Stop()` 等待进行中渲染（排除调用线程），消除主题重置竞态 | `fc56e1b` |
| `readme.md` / `readme.zh-CN.md` | 存储后端与 CodeMode 默认引擎说明 | 随本台账 |
| `docs/migration-execution-status.md` | 本台账 | 随本台账 |

Stage 13 定向验证命令（shell 恢复后执行，先于提交）：

```powershell
dotnet build .\src\Pi.Durable\Pi.Durable.csproj --no-restore -m:1
dotnet build .\tests\Pi.Durable.Tests\Pi.Durable.Tests.csproj --no-restore -m:1
dotnet run --no-restore --project .\tests\Pi.Durable.Tests\Pi.Durable.Tests.csproj -- --filter-class Pi.Durable.Tests.JsonlStorageConformanceTests
dotnet run --no-restore --project .\tests\Pi.Durable.Tests\Pi.Durable.Tests.csproj -- --filter-class Pi.Durable.Tests.JsonlStorageAcrossReopenConformanceTests
dotnet run --no-restore --project .\tests\Pi.Durable.Tests\Pi.Durable.Tests.csproj -- --filter-class Pi.Durable.Tests.JsonlStorageTests
```

Stage 15 定向验证：`dotnet build .\tests\Pi.CodingAgent.Tests\Pi.CodingAgent.Tests.csproj --no-restore -m:1`，并补一条“overlay 关闭后调用句柄不抛异常”的交互测试。

### G2 决策与实现（2026-10-21）

采用方案 **A：Jint 承载 prelude + 工具桥**，未引入 Node/QuickJS 外部进程：

- `JintCodemodeEngine` 每次执行新建独立 `Engine`，求值同一份 prelude，并以 `ClrFunction` 提供 `bridge`；消息经两个 Channel 与 host 的 `call/global/output/done/crash` 协议互通。
- 脚本在后台任务中运行，`Run` 立即返回 VM 句柄，避免同步工具调用在 host 绑定 VM 前完成时丢失回复；host 侧同时保留延迟回复队列作为兜底。
- 每次 `settle` 后调用 `Advanced.ProcessTasks()` 再调用 prelude 的 `stalled()`，对应 TS worker 的 `executePendingJobs()` + `stalled()`。
- `StopAsync` 取消 Jint 的 `CancellationToken`；`Options.Constraints.StackOverflowGuard` 把深递归变成可捕获的 `RangeError`（对应 QuickJS 的 `maxStackSize`）；`LimitMemory` 映射内存上限。
- 已记录差异：QuickJS WASM 载荷（`CodemodeWorkerData.Wasm`）对 Jint 无意义；Jint 的内存计量与 QuickJS 不同；QuickJS 的 worker 线程终止由取消令牌/进程内取消替代。
- 方案 B（移除默认工具）和 C（子进程引擎）未采用：A 已满足默认可用且不增加外部运行时依赖。

定向验证：`JintEngineTests` 10/10（工具调用/输出、globals、脚本与语法错误、永不 settle 的 promise、工具失败、store 写回、超时停止后可复用、递归保护、执行隔离），`SandboxTests` 中同步引擎延迟回复回归通过；`Pi.Codemode.Tests` 51/51。
