# Pi.NET

> Pi.NET 是 [Pi](https://github.com/earendil-works/pi)（TypeScript agent harness，约 17 万行）的 .NET 10 移植：
> 17 个源项目、13 个发布到 nuget.org 的库、4 个不打包的可执行宿主、14 个测试项目、约 3 万项测试。
> 上游 TypeScript 仓库是**行为基准**；本仓库用惯用 C# 重写，不做逐行翻译。
>
> 这份文件写给 AI 编程助手：先看命令与硬规则，再按源码地图定位，需要细节时只打开被指向的那一份。
> 全仓库 Markdown 超过 2 MB，不要整目录读。仓库没有 `AGENTS.md`，这份文件承担同样的职责。

## 动手之前先读

- [readme.md](readme.md)：对外介绍、13 个包各做什么、快速上手、嵌入式 SDK 示例。
- [CHANGELOG.md](CHANGELOG.md)：按版本记录每个改动的动机与验证方式。想弄清"这个行为为什么长这样"，这里最快。
- [docs/migration-execution-status.md](docs/migration-execution-status.md)：迁移台账的唯一权威入口——当前口径、逐包矩阵、剩余事项、验证基线。**判断"某功能做了没有"只看这一份**；`migration-plan.md`、`porting-status.md` 是历史快照，不能用来推断现状。

## 工作协议

改任何东西都按这个顺序，别跳步：

1. **定位包** → 读对应的 `docs/usage/0X-*.md`，确认公开面和已知坑。
2. **对上游语义**：在 TS 仓库里找到对应实现（见「可选」一节的路径），确认行为，再写 C#。
3. **构建到 0 警告**：`TreatWarningsAsErrors=true`，警告就是失败。
4. **先跑目标测试项目，再跑全量**（约 3.5 分钟）。
5. **留痕**：行为变了补 CHANGELOG；迁移范围或取舍变了更新台账。

不允许的做法：把失败测试标成"环境限制"而不给可复现证据；为了让构建过而降低分析级别或压制警告；把上游没有的行为"顺手"加进来。

## 命令

| 目的 | 命令 |
|---|---|
| 还原 | `dotnet restore Pi.slnx` |
| 构建（与 CI 一致） | `dotnet build Pi.slnx -c Release --no-restore -p:ContinuousIntegrationBuild=true` |
| 全量测试 | `dotnet test Pi.slnx -c Release`（约 3.5 分钟） |
| 单个测试项目 | `dotnet test tests/Pi.Tui.Tests -c Release` |
| 打包 | `dotnet pack Pi.slnx -c Release -o artifacts` → 13 个 `.nupkg` + 13 个 `.snupkg` |
| 跑 CLI | `dotnet run --project src/Pi.Cli/Pi.Cli.csproj -- --help` |
| 发布 `pi` 到目录 | `dotnet publish src/Pi.Cli -c Release -o ./pi-bin` |
| 重新生成 models.json 字段文档 | `node scripts/gen-models-configuration.mjs`（`--check` 供 CI 判断是否过期） |

需要 .NET 10 SDK。库与可执行程序都是多目标 `net9.0;net10.0`，消费方按自己的运行时选；net8.0 及以下不支持。

多目标是零成本的：net9.0 已经有 net10.0 用到的全部 API（`JsonStringEnumMemberName`、`System.Threading.Lock`、`Convert.ToHexStringLower`、`JsonSerializerOptions.NewLine`），所以代码里没有任何 `#if` 分支。真正的运行时差异只有两处，都已按「两个 TFM 行为一致」修掉：事件记录上的 `Type` 属性必须逐个标 `[JsonIgnore]`（否则一行 JSON 出现两个 `type` 键，net9.0 的 `JsonObject` 会直接抛错），以及 `Pi.Evals.ExtensionCompiler` 生成扩展工程时必须用当前运行的 TFM 而不是硬编码。

`samples/` 下五个工程**在 solution 里**，这是故意的：改公共 API 时它们一起编译失败，逼迫文档里的示例不烂掉。

```bash
dotnet run --project samples/Pi.Samples.FauxAgent     # 无需密钥，先跑这个确认环境
dotnet run --project samples/Pi.Samples.McpClient     # 需要本机 Node/npx
dotnet run --project samples/Pi.Samples.RealModel -- anthropic
dotnet run --project samples/Pi.Samples.CodingSession
```

`Pi.Sample.Extension` 是扩展程序集（不是 Exe）：`dotnet build samples/Pi.Sample.Extension`，把产出的 dll 放进 `.pi/extensions/`。

## 源码地图

`src/Pi.<X>` 对应上游 `packages/<x>`。文件数用来判断规模，不是精确指标；口径是不含 `obj/`、`bin/` 下的生成文件。

| 项目 | .cs | 里面有什么 |
|---|---|---|
| `Pi.Ai` | 159 | `Api/` 10 个原生 API 实现 · `Providers/` 42 家 provider 目录 · `ModelData/` 1713 条模型数据 · `Auth/` OAuth（PKCE / 设备码 / 回环） · `Stream/` · `Types/` · `Compat/` |
| `Pi.Agent` | 9 | 状态机 + 双队列（steering / follow-up）、agent-loop 主循环、串行/并行工具执行 |
| `Pi.CodingAgent` | 366 | `Core/` 会话与内置工具 · `Cli/` 参数解析（`Args.cs`） · `Modes/` 交互 / print / json · `Extensions/` · `Utils/` · `Assets/` 主题、schema、随包文档 |
| `Pi.Tui` | 61 | `Components/` 渲染组件 · `Marked/` markdown；差量渲染、字素级换行、ANSI/OSC、Kitty 键盘、浮层、全屏渲染 |
| `Pi.Durable` | 79 | `Session/` · `Storage/`（内存 / SQLite / JSONL） · `Tools/` · `Harness/` · `Env/`；耐久会话、任务图、恢复 |
| `Pi.Chord` | 34 | `Services/` · `Delta/` · `Facets/`（拓扑激活） · `Node/` · `Context/`；应用编排运行时 |
| `Pi.Mcp` | 16 | `Protocol/` JSON-RPC · `Transports/` stdio / streamable-http / 内存 · `OAuth/` · `Client/` |
| `Pi.Server` / `Pi.Client` | 10 / 7 | RPC 握手、请求分发、取消、推送；`Transports/` 含 Unix domain socket 与本地服务发现 |
| `Pi.Protocol` | 6 | `Cbor/` 分帧与编解码，与 TS 实现线级兼容（含 JS safe-integer 边界） |
| `Pi.Codemode` | 10 | `Runtime/` 沙箱 JS，默认 Jint，引擎经 `ICodemodeJsEngine` 可替换 |
| `Pi.Env` | 6 | SSH 远程环境的 `pi-env` 守护进程客户端与分帧协议；**守护进程本身不是 C#**，产物在 `tools/pi-env-daemon-target` |
| `Pi.Telemetry` | 8 | 遥测契约、参考适配器、`Testing/` 一致性套件 |
| `Pi.Cli` | 3 | `pi` 命令入口，真正的逻辑在 `Pi.CodingAgent/Cli/Args.cs` |
| `Pi.Vacation` / `Pi.DurableHost` / `Pi.Evals` | 1 / 1 / 17 | 三个独立宿主（研究规划 / 耐久会话 / 评测），`IsPackable=false` |

依赖方向：`Telemetry` → `Ai` → `Agent` → `CodingAgent`；`Chord` 被 `Protocol`、`Server`/`Client`、`Durable`、`Env` 复用。越靠上越薄：`Pi.Ai` 只管发消息收事件，`Pi.Agent` 加状态机和工具循环，`Pi.CodingAgent` 把模型目录、凭据、设置、技能、扩展、存档、内置工具全装配好。

## 使用文档索引

按 C# 消费方视角写的，每条都对过源码，不是上游文档的翻译。

- [docs/usage/README.md](docs/usage/README.md)：30 秒选型表、分层图、13 个包的依赖关系、凭据优先级、三个常见坑。**从这里进。**
- [docs/usage/01-pi-ai.md](docs/usage/01-pi-ai.md)：直接调 LLM——流式事件、工具调用、思考预算、OAuth、成本计算。
- [docs/usage/02-pi-agent.md](docs/usage/02-pi-agent.md)：自己写 agent 循环，`StreamFn` 与工具执行边界。
- [docs/usage/03-pi-codingagent.md](docs/usage/03-pi-codingagent.md)：开箱即用的 coding agent，会话/技能/扩展/存档。
- [docs/usage/04-pi-mcp.md](docs/usage/04-pi-mcp.md)：MCP 客户端，列工具、调工具、读资源、OAuth。
- [docs/usage/05-other-packages.md](docs/usage/05-other-packages.md)：TUI、RPC、耐久会话、远程执行、遥测、沙箱 JS。
- [docs/usage/sdk.md](docs/usage/sdk.md)：`Sdk.CreateAgentSessionAsync`、`CreateAgentSessionOptions` 全字段与缺省行为、`AgentSession` 公开面、装配顺序、不落盘会话。
- [docs/usage/cli.md](docs/usage/cli.md)：`pi` 的子命令、全部选项、端口实际读取的环境变量。
- [docs/usage/configuration.md](docs/usage/configuration.md)：`~/.pi/agent/` 与 `<cwd>/.pi/` 每个文件谁在读、信任机制。
- [docs/usage/sessions.md](docs/usage/sessions.md)：会话存档的文件与目录布局、恢复行为、压缩策略。
- [docs/usage/codemode.md](docs/usage/codemode.md)：codemode 开关、脚本可用面、`@options`、失败语义。
- [docs/usage/models-configuration.md](docs/usage/models-configuration.md)：`models.json` 逐字段的类型、默认值与说明。**由脚本生成**，改 schema 后必须重跑生成器。

## 迁移与验收台账

性质不同，别混读：矩阵看结论，delta 看证据。

- [docs/migration-execution-status.md](docs/migration-execution-status.md)：当前口径（上游目标 `v1.4.0`）、逐包矩阵、四类边界（不适用 / 不采纳 / 暂不移植 / 验证缺口）、验证基线。
- [docs/upstream-1.4.0-delta.md](docs/upstream-1.4.0-delta.md)：`v1.0.4..v1.4.0` 的 278 个提交逐批评估（221 KB），含上游哈希与不适用判定依据。**要反驳"这功能没做"先查这里。**
- [docs/public-api-audit.md](docs/public-api-audit.md)：1,426 个 barrel 导出逐项分类（442 runtime / 945 type / 39 unknown）。
- [docs/review-inconsistencies-2026-10-21.md](docs/review-inconsistencies-2026-10-21.md)：已登记的 TS/.NET 语义差异（C152、C154、C258、T11 等）。**看到"和上游不一样"先来这查，可能是有意为之。**
- [docs/coding-agent-porting-status.md](docs/coding-agent-porting-status.md)：coding-agent 逐文件台账（803 KB），含 flake 观测名单。
- [docs/tui-porting-status.md](docs/tui-porting-status.md)：TUI 逐项台账（字素、ANSI、Kitty、备用屏）。
- [docs/publishing.md](docs/publishing.md)：发版流程、包内容约定、CI 与测试口径、回退方式。
- [docs/env-package-migration.md](docs/env-package-migration.md)、[docs/4d-extension-system-plan.md](docs/4d-extension-system-plan.md)：远程环境与扩展系统的设计与差异。
- [docs/migration-plan.md](docs/migration-plan.md)、[docs/porting-status.md](docs/porting-status.md)、[docs/migration-remaining-plan.md](docs/migration-remaining-plan.md)：**历史快照**，只作证据链，不当现状。

## 随包分发的上游文档

`src/Pi.CodingAgent/Assets/docs/` 是上游 TS 用户文档原文，行为与端口一致（`Assets/docs` 与 `Assets/examples` 不随 NuGet 包分发）。

- [index.md](src/Pi.CodingAgent/Assets/docs/index.md)：39 篇的总索引，按主题分流。
- [dotnet-extensions.md](src/Pi.CodingAgent/Assets/docs/dotnet-extensions.md)：**C# 扩展 API 的唯一权威**。上游 `extensions.md` 讲的是 TS 扩展，写 .NET 扩展别照它抄；可编译的示例在 `samples/Pi.Sample.Extension`。
- **TypeScript/JavaScript 扩展可以直接加载**（1.4.5 起）：`Assets/examples/extensions-typescript/` 的上游 `.ts` 示例由进程内 JavaScript 宿主（Jint + Babel）直接跑，不必先编译成程序集；回归口径见
  `tests/Pi.CodingAgent.Tests/UpstreamTypeScriptExampleTests.cs`（79 个真实入口，71 个端到端通过）。
  尚未桥接的部分（`registerProvider` / `registerVirtualModel`、`ctx.ui.custom()` 与自定义渲染器）记在
  `dotnet-extensions.md`。
- [skills.md](src/Pi.CodingAgent/Assets/docs/skills.md)：`skills/<name>/SKILL.md` 的格式与加载规则。
- [providers.md](src/Pi.CodingAgent/Assets/docs/providers.md) / [custom-provider.md](src/Pi.CodingAgent/Assets/docs/custom-provider.md)：内置 provider 清单；怎么加一个自定义 provider。
- [settings.md](src/Pi.CodingAgent/Assets/docs/settings.md) / [configuration.md](src/Pi.CodingAgent/Assets/docs/configuration.md)：settings 字段与配置目录。
- [rpc.md](src/Pi.CodingAgent/Assets/docs/rpc.md) / [rpc-commands.md](src/Pi.CodingAgent/Assets/docs/rpc-commands.md)：RPC 模式与命令表。
- [security.md](src/Pi.CodingAgent/Assets/docs/security.md)：工具权限与审批模型。

## 约定：怎么从 TS 翻过来

- **语义等价，不逐行对应。** TypeScript 的判别联合 → 抽象 record + 模式匹配；JS Proxy → 显式动词 API。看到 TS 的写法不要照搬结构。
- **barrel export 不逐个镜像。** `packages/tui/src/index.ts` 是导出桶，C# 侧没有对应文件。
- **文档双语分工**：`readme.md` 对外是英文（另有 [readme.zh-CN.md](readme.zh-CN.md)），`docs/` 内部台账是中文。
- **测试框架**：xUnit v3 + Microsoft.Testing.Platform。`tests/Directory.Build.props` 里的 `TestingPlatformDotnetTestSupport=true` 不能删——删了 `dotnet test` 会走 VSTest 路径、找不到适配器、**一行不跑却退出 0**。
- **语料可重算**：`tools/gen-coding-agent-minimatch-corpus.mjs`、`tools/gen-coding-agent-model-resolver-corpus.mjs` 生成行为语料。改了 minimatch 或 model-resolver 语义，要重算语料，否则测试在比对过期期望。

## 会咬人的地方

- **版本号有两处**，`Directory.Build.props` 的 `<Version>` 和 `src/Pi.CodingAgent/Assets/package.json` 的 `version`。`Config.Version` 优先读后者（启动横幅、`--version` 都取自它），构建期有守卫直接报错。发版改一处必挂。
- **`StreamFn` 没有默认值**。`AgentOptions.StreamFn` 必填，不想每次传就调 `DefaultStreamFn.Set(fn)`；忘了会抛 `No default stream function configured`。
- **模型有两个类型**：`Pi.Ai.Types.Model` 是瘦记录（`Id`/`Name`/`Api`/`Provider`），出现在 `StreamFn` 边界上；`Pi.Ai.Models.ModelSpec` 是目录条目（含 `BaseUrl`、`ContextWindow`、计价）。跨层时经 `CompatShims.ToModel()` 转换，别混用。
- **`dotnet test` 不要加 `-m:1`**。串行会让测试项目互相干扰（实测 `Pi.Ai.Tests` 多出 2 个失败、`Pi.CodingAgent.Tests` 18 分钟不结束）。`dotnet build -m:1` 没问题。
- **已知 flake**（并行争抢时偶发，单跑必绿，别当回归追）：
  `ModelSelectorTests.InitialSearchThatMatchesNothingShowsTheNoResultsPath`、
  `ThemeRuntimeTests.StartWatcher_IgnoresChangesToOtherFiles`、
  `SessionSelectorTests.PageDownMovesByMaxVisible`（分页边界）、
  `AnthropicFederationModelsTests.ThreadsFederationEnvThroughModels`（共享 handler）。
- **CI 默认不跑测试**。这套用例依赖真实交互终端（TUI 要尺寸与 ANSI）和本地 sshd（`Pi.Env`），GitHub runner 上同一份代码实测 959（ubuntu）/ 369（windows）项失败。要跑得手动触发并勾 `run_tests`。
- **`Pi.Mcp` 需要 ASP.NET Core 框架引用**（`Microsoft.AspNetCore.App`），消费方得用 `Web` SDK 或加 `FrameworkReference`。
- **别动 SourceLink 的版本**。当前锁 `10.0.401`：更早的版本经 `Microsoft.Build.Tasks.Git` 命中 CVE-2026-62900（GHSA-23fw-v26w-5fgq），NU1902 会被 `TreatWarningsAsErrors` 升级成 error，restore 直接失败。
- **模型目录别手写**。1,713 条数据取自 `@earendil-works/pi-ai@1.4.0` 的 `dist/providers/data/`，41 条默认模型必须条条可解析（有回归测试钉住）。编造 id 会被 `EveryDefaultModelResolvesInTheBuiltinCatalog` 抓住。

## 可选

- [上游 Pi（TypeScript）](https://github.com/earendil-works/pi)：行为基准。本机同级检出在 `D:\AI\参考项目\pi`，本轮对齐 `v1.4.0`（`4a080252f`）。
- [nuget.org 上的 Pi.*](https://www.nuget.org/packages/Pi.Ai)：13 个库的发布地址。
- [publishing.md](docs/publishing.md)：发版细节；打 tag 推送即触发 `nuget-publish.yml`。
- [samples/README.md](samples/README.md)：四个示例工程各自演示什么。
