# coding-agent 剩余迁移：现状分析、迁移清单与分阶段计划

> 生成时间：2026-10-13
> 参照源：`D:\AI\参考项目\pi`（`packages/coding-agent`），基线 commit **`428a12bc7`（= `origin/main`）**
> 目标仓库：`D:\AI\参考项目\pi.net`（`src/Pi.CodingAgent`），起始 commit **`f208567`**
> 本文是**执行台账**：每完成一个阶段在此更新「进度台账」小节。逐文件的移植进度与设计差异
> 仍以 [coding-agent-porting-status.md](coding-agent-porting-status.md) 为准（差异编号 `C##` 已用到 `C160`）。

---

## 一、现状快照（2026-10-13 实测）

### 1.1 规模对照

| 目录 | TS 文件 | TS 行数 | .NET 落点 | 状态 |
| --- | ---: | ---: | --- | --- |
| `core/` | 94 | 35,059 | `Core/*`（93 个 .cs） | 🟡 主体完成，编排本体与 renderers 未完 |
| `modes/` | 67 | 23,705 | — | ⬜ 未开始 |
| `experimental/` | 46 | 9,950 | — | ⬜ 未开始 |
| `extensions/` | 25 | 7,259 | `Extensions/*` | 🟡 codemode / mcp / tool-search / llama 主体完成，llama 流式层未完 |
| `utils/` | 37 | 3,737 | `Utils/*` | ✅ 完成 |
| `cli/` | 17 | 1,949 | — | ⬜ 未开始 |
| `bun/` + `client/` | 6 | 62 | — | ⬜ 未开始（多为打包器垫片，多数无对应物） |
| 根级（`config.ts` / `migrations.ts` / `main.ts` / `index.ts` / `cli.ts` / `rpc-entry.ts` / `package-manager-cli.ts`） | 7 | 3,624 | `Config.cs` / `Migrations.cs` | 🟡 仅 `config.ts` / `migrations.ts` 完成 |
| **合计** | **299** | **85,536** | 62,668 行 C# | **约 73% 行数已落** |

`src/Pi.CodingAgent` 现有 **93 个手写 .cs**（不含 `obj/`），`wc -l` 合计 **62,668 行**。

### 1.2 构建与测试基线（本机实测）

| 项目 | 结果 |
| --- | --- |
| `dotnet build Pi.slnx -m:1` | ✅ 0 警告 0 错误（**修复后**，见 1.3） |
| `Pi.CodingAgent.Tests` | ✅ 1313 项 / 1313 通过 / 0 失败（2m21s） |
| 其余 10 个测试工程 | 见 [porting-status.md](porting-status.md) 的环境限制说明 |

### 1.3 起始状态的一个真实缺陷（本批已修）

`f208567`（上一批「AgentSessionQueue」）**提交了不能编译的代码**：
`src/Pi.CodingAgent/Core/AgentSessionQueue.cs:209` 引用 `Frontmatter.Strip(...)`，但该文件缺少
`using Pi.CodingAgent.Utils;` → `CS0103: 当前上下文中不存在名称"Frontmatter"`。
本批已补上该 `using`，构建恢复 0 警告 0 错误。

> **教训（写入约定）**：任何一批的收口标准「构建 0 警告 0 错误」必须在**提交前**实测，
> 不能沿用上一批的结论。`f208567` 的提交信息未按仓库惯例（`type(scope): 中文标题` + 分节要点），
> 也未更新本文档，属流程偏离。

---

## 二、迁移清单

### 2.1 已完成（不需返工）

| 子阶段 | 范围 | 收口证据 |
| --- | --- | --- |
| 4a | `src/utils/*`（36 文件）+ 根级 `config.ts` | 36/36 文件 + 409 条差分向量 |
| 4b | 配置 / 信任 / 模型层 16 文件（6,844 行） | 16/16 文件 |
| 4c | 工具系统 8 个工具 + 6 支撑文件 | 8/8 工具；renderers 顺延 4f |
| 4d-1～4d-3 | 扩展契约层 / 事件总线 / 加载器 / ALC 加载器与内置扩展注册 | — |
| 4d-4a/4b | MCP 配置与服务器日志 / MCP 工具适配 | — |
| 4d-5 | codemode 扩展 | — |
| 4d-6 | tool-search 扩展 | — |
| 4d-7a | llama 非 UI HTTP 层 | — |
| 4e-1～4e-3e-8 | core 叶子模块 / Usage / AgentMessage / 消息内容联合 / session-manager / compaction / skills / system-prompt / session-export | — |
| 4e-3e-9a～9e | 会话统计、工具装载、消息溯源、prompt 循环、compaction/retry/bash、扩展绑定、树导航、fork 投影、bug-report 摘要（**纯决策叶子**） | `Pi.CodingAgent.Tests` 逐批 +20～40 |
| 4e-3e-9-0 | 基线对齐（参照检出落后 29 提交 → 按 `origin/main` 回审 allow/deny 与 MCP 注册） | C107/C108 |
| 4e-3e-9f | 会话装配层叶子（services / runtime / sdk） | C109–C112 |
| 4e-3e-9g | `ExtensionRunner`（1,560 行 TS）+ 契约层收口 | C140/C141；61 项新测试 |
| 4e-3e-9h-1 | 实例化地基（`static partial class` → `partial class`） | 行为零变化 |
| 4e-3e-9h-2a/2b-1/2b-2/2c/3 | `ThinkingLevel` 决策 + `IToolContext` 退役 + `wrapper.ts` / 资源溯源 + 扩展加载本体 / 工具目录 + 运行时装配 / 6 个 installer + `_handleAgentEvent` | C142–C157 |
| （未记档） | `AgentSessionQueue`（输入与排队层） | 本批修复其编译错误 |

### 2.2 未完成（本计划的执行对象）

#### 4e-3e-9h-4：会话编排本体 ★最高优先级

| TS 符号 | 行数 | 端口现状 |
| --- | ---: | --- |
| `agent-session.ts` `prompt()` | 1954-2096（143 行） | `PromptAsync` 是缝（`NotSupportedException`） |
| `agent-session.ts` `_runAgentPrompt()` | 1808-1837（30 行） | `RunAgentPromptAsync` 是缝 |
| `agent-session.ts` `_handlePostAgentRun()` | 1839-1877（39 行） | 未落地 |
| `agent-session.ts` `_runBeforeSettleBoundary()` | 1879-1910（32 行） | 未落地 |
| `agent-session.ts` `compact()` | 2750-2895（146 行） | `CompactAsync` 是缝 |
| `agent-session.ts` `_runAutoCompaction()` | 2985-3120（135 行） | `NotSupportedException` 缝（9h-3 记录） |
| `agent-session.ts` `_runDefaultCompaction()` / `_getSummarizationRequestAuth()` | 2720-2750 | 未落地 |
| `agent-session.ts` `_handleAgentEvent` 尾部 / `_flushPendingBashMessages` 等生命周期尾巴 | — | 部分在 9h-3 |
| `sdk.ts` `createAgentSession()` | 462 行（整文件 468） | 仅叶子（9f） |
| `agent-session-services.ts` `createAgentSessionServices` 编排本体 | 233 行 | 仅叶子（9f） |
| `agent-session-runtime.ts` `AgentSessionRuntime` 类本体 | 447 行 | 仅叶子（9f） |
| `sdk.ts` `buildRequestOptions` 余下字段（`transformHeaders` / `onPayload` / `onResponse` / `onProviderStreamEvent`） | — | 顺延 |
| `CreateAgentSessionRuntimeResult` | — | 顺延（9h-1 已解阻塞） |

#### 4d 尾巴：扩展层剩余

| TS 符号 | 行数 | 现状 |
| --- | ---: | --- |
| `extensions/llama/*` 流式层（`provider.ts` 流式分支 / `ui.ts`） | ~900 | 4d-7b 未完（HTTP 层 4d-7a 已完成） |
| `extensions/mcp/*` 余下（`runtime.ts` / `resources.ts` / `oauth.ts` / `ui.ts` / `cli.ts` / `index.ts`） | ~3,300 | 4d-4c/4d-4d 未完 |

#### 独立缺口：package-manager

| TS 符号 | 影响 |
| --- | --- |
| `resolvePackageSources`（`package-manager.ts`，2,760 行） | `settings.packages` 完全未处理 → 已安装包的扩展/skills/prompts/themes 永远加载不到 |
| `PathMetadata.packageRoot` | `collectExtensionPackageWarnings` 无法移植 |
| `applyAutoloadDisabledPatterns` / `isEnabledByOverrides` / `getOverridePatterns` / `matchesAnyPattern` | `+`/`-`/`!` 前缀与路径模式语义被简化 |

#### 4f：模式层（23,705 行 / 67 文件）—— 最大单块

`modes/interactive/`（`interactive-mode.ts` 单文件 7,072 行 + components 30 余个 + theme 4 个）
+ `modes/rpc/`（`rpc-mode.ts` 819 / `rpc-client.ts` 617 / `rpc-types.ts` 303 / `jsonl.ts` 58）
+ `modes/print-mode.ts` / `json-output` / `json-event.ts`。
依赖：`Pi.Tui`（**已收口，44/45 文件**）+ `core/tools/renderers/*`（未移植，Theme 依赖）。

#### 4g：入口与实验层（~12,000 行）

`main.ts`(999) / `index.ts`(494) / `package-manager-cli.ts`(1,141) / `cli.ts` / `rpc-entry.ts`(13)
+ `cli/*`(17 文件, 1,949) + `experimental/*`(46 文件, 9,950) + `bun/*` + `client/*`。

#### evals（1,300 行，未建项目）

---

## 三、优先级排序与依据

排序原则：**依赖倒序解开阻塞 → 单位工作量收益最大 → 风险最早暴露**。

| 序 | 阶段 | 为什么排这里 |
| ---: | --- | --- |
| 1 | **修复构建 + 基线**（已完成） | HEAD 不可编译时任何后续验证都无意义 |
| 2 | **4e-3e-9h-4 编排本体** | 它是**唯一**让 `Pi.CodingAgent` 端到端可跑的一环；`prompt`/`compact` 是所有上层（4f 交互模式、4g 入口）的入口。前置（`ExtensionRunner` 9g、installers 9h-3）已全部满足，无阻塞 |
| 3 | **package-manager 缺口** | `resolvePackageSources` 缺失会让 4f/4g 的「装包 → 用包」链路在真机上失效；独立于 4f，可并行推进，且越早暴露越好 |
| 4 | **4d 尾巴（llama 流式 / MCP 余下）** | 自包含、与 4f 无耦合；MCP 是扩展生态的实际入口 |
| 5 | **4f 模式层** | 体量最大（23.7k）；依赖 `Pi.Tui`（已就绪）与 `core/tools/renderers`（需先补）。必须再切子阶段 |
| 6 | **4g 入口与实验层** | 依赖 4f 的交互/RPC 模式；`main.ts` 是最终可执行入口 |
| 7 | **evals** | 独立包，最后阶段 |

---

## 四、分阶段计划

> 收口标准（每阶段统一）：`dotnet build Pi.slnx -m:1` **0 警告 0 错误** + 相关测试全绿 +
> **TS↔C# 逐条一致性核对**（在 [coding-agent-porting-status.md](coding-agent-porting-status.md)
> 追加「已落地」对照表与设计差异 `C##`）+ 一次提交。

### 阶段 P1（= 4e-3e-9h-4a）：`AgentSession` 编排本体

- **目标**：`AgentSession` 具备完整的一次「提交 → 运行 → 结算」能力。
- **文件范围**：
  - `src/Pi.CodingAgent/Core/AgentSessionLoop.cs`（补实例面 `RunAgentPromptAsync` / `HandlePostAgentRunAsync` / `RunBeforeSettleBoundaryAsync`）
  - `src/Pi.CodingAgent/Core/AgentSessionBuild.cs`（`PromptAsync` 实现 + 拆掉两个缝）
  - `src/Pi.CodingAgent/Core/AgentSessionCompaction.cs`（`CompactAsync` / `RunAutoCompactionAsync` / `RunDefaultCompactionAsync` / `GetSummarizationRequestAuthAsync`）
  - `tests/Pi.CodingAgent.Tests/AgentSessionPromptTests.cs`（新建）
- **验收标准**：
  1. 构建 0 警告 0 错误；`Pi.CodingAgent.Tests` 全绿（≥1313）。→ **达成**：0 警告 0 错误；1323 / 0 失败。
  2. 逐条对照 TS：`prompt` 的 11 个分支（settled 延迟 / 扩展命令 / 压缩中拒绝 / input 处理器 / 模板展开 / 流式排队 / 模型校验 / 压缩检查 / before_agent_start / 图片归一 / 消息装配）全部落地。→ **达成**（对照表见 status 文档「4e-3e-9h-4a」节）。
  3. `_runAgentPrompt` 的 `while` 循环三处 `break` 与 `||` 短路语义与 TS 逐行等价（复用 9b 的 `ResolvePromptLoopStep`）。→ **达成**。
  4. 新增测试覆盖：扩展命令直通、流式排队、模型缺失/无凭据报错、`_handlePostAgentRun` 的 retry/compaction/queued 三路、`_runBeforeSettleBoundary` 的非法继续上报。→ **部分达成**（见下）。
- **P1 交付实况（诚实记录）**：
  - 已覆盖（`AgentSessionPromptTests` 10 项 + `AgentSessionBuildTests` 改造 1 项）：无模型 `prompt`/`compact` 的 TS 文案、
    扩展命令直通五路、`_runAgentPrompt` 单轮循环 + settled + run 级状态清空、agent run 失败仍收尾。
  - **未覆盖**（并入 P2 补齐，原因如下）：
    - *流式排队*：`IsStreaming => _isAgentRunActive`，无公开注入点，需在飞 run 才能构造 → 待 P2 的 SDK 装配提供可驱动入口后补。
    - *无凭据报错*：需注册带 `ApiKey` 的 native provider 且让 `CheckAuthAsync` 返回 `null`；当前夹具无此路径 → P2 一并建夹具。
    - *`_handlePostAgentRun` 三路 / `_runBeforeSettleBoundary` 非法继续*：均为**私有**方法且依赖模型 / 扩展边界处理器；其**纯决策**已由 9a～9e 的叶子测试锁死（`ResolvePromptLoopStep` / `AdvanceRetryAttempt` / `ShouldSkipCompactionCheck` / `ResolveInputHandlerOutcome`），实例接线留待 P2 的端到端用例。
- **前置**：无（9g / 9h-3 已满足）。

### 阶段 P2（= 4e-3e-9h-4b）：SDK 装配本体

- **目标**：`createAgentSession()` 端到端可创建并驱动一个会话。
- **文件范围**：`Core/Sdk.cs`、`Core/AgentSessionServices.cs`、`Core/AgentSessionRuntime.cs`、`Core/AgentSessionBuild.cs`（`buildRequestOptions` 余下字段）、`tests/Pi.CodingAgent.Tests/SdkTests.cs`。
- **验收标准**：`CreateAgentSessionRuntimeResult` 落地；`AgentSessionRuntime` 的 `switchSession` / `newSession` / `fork` / `importFromJsonl` / `dispose` 全部落地；`buildRequestOptions` 的 4 个钩子接 `ExtensionRunner`；构建 + 测试全绿。
- **前置**：P1。

### 阶段 P3：package-manager 包解析

- **目标**：`settings.packages` → 资源路径全链路打通。
- **文件范围**：`Core/PackageManager.cs`（`ResolvePackageSources` / `PackageRoot` / 完整 override 语义）、`Core/ResourceLoader.cs`（消费 `packageRoot`）。
- **验收标准**：`+`/`-`/`!` 前缀与路径模式匹配逐条对照；`collectExtensionPackageWarnings` 落地；回归守卫测试（已装包 → 扩展/skill 可加载）。
- **前置**：无（可与 P1/P2 并行）。

### 阶段 P4：4d 尾巴

- **P4a** llama 流式层（`extensions/llama/provider.ts` 流式分支 + `ui.ts`）。
- **P4b** MCP 余下（`runtime.ts` / `resources.ts` / `oauth.ts` / `ui.ts` / `cli.ts` / `index.ts`）。
- **验收标准**：逐文件对照；MCP 走真实 stdio/HTTP 假服务器回放（沙箱禁端口的部分标环境限制）。
- **前置**：无。

### 阶段 P5：4f 模式层（拆 5 个子阶段）

| 子阶段 | 范围 | 验收标准 |
| --- | --- | --- |
| 4f-1 | `core/tools/renderers/*`（~700 行，Theme 依赖） | renderers 逐文件对照 |
| 4f-2 | `modes/interactive/theme/*`（~2,200 行） | 主题解析 / 系统主题 / 主题控制器 / JSON 主题 |
| 4f-3 | `modes/interactive/components/*`（~9,000 行） | 每个组件一个测试类；选择器交互路径 |
| 4f-4 | `modes/interactive/interactive-mode.ts`（7,072 行） | 主循环 / 键位 / 视口 / 共享 |
| 4f-5 | `modes/rpc/*` + `print-mode` + `json-*`（~2,300 行） | RPC 端到端（Unix 域套接字回放） |

### 阶段 P6：4g 入口与实验层（拆 3 个子阶段）

`4g-1` `cli/*` → `4g-2` `main.ts` / `index.ts` / `package-manager-cli.ts` / `rpc-entry.ts` → `4g-3` `experimental/*` + `bun/*` + `client/*`。

### 阶段 P7：evals（1,300 行，新建项目）

---

## 五、风险与阻塞

| # | 风险 | 影响 | 处置 |
| --- | --- | --- | --- |
| R1 | **推送通道曾不可用（已恢复）** | 阶段产出无法上远端 | 实测（2026-10-13 上午）：`https` 经本机代理（`127.0.0.1:52811`）对 `github.com` 返回 **502 CONNECT tunnel failed**；`ssh -p 443 git@ssh.github.com` 可达但 **publickey 未授权**。**2026-10-13 下午代理恢复**：`git push origin main` 成功（`f208567..7b942b3`）。若再次 502，处置：本地提交保证可追溯，或把 `~/.ssh/id_rsa.pub` 加到 GitHub 账号后改走 `ssh://git@ssh.github.com:443/xuzeyu91/pi.net.git` |
| R2 | 参照检出漂移 | 语义错位（9-0 已发生过一次） | 每阶段动工前 `git -C D:\AI\参考项目\pi log -1` 确认仍在 `428a12bc7` |
| R3 | 沙箱构建/测试限制 | 假阳性失败 | 构建必须 `-m:1`；`dotnet test` stdout 为空，改跑 `.exe` + `iconv GBK→UTF-8`；过滤用 `--filter-class` |
| R4 | `4f` 体量（23.7k 行） | 单会话不可完成 | 已切 5 个子阶段，每子阶段独立提交 |
| R5 | package-manager 缺口 | 真机装包链路失效 | 提到 P3，早于 4f |
| R6 | 已登记但未复现的差异（C152 消息替换的引用同一性、C154 `tool_call` 入参不可改写） | 扩展行为受限 | 已在 9h-3 记录为真实缺口，随 P1/P2 复核是否需要补偿 API |

---

## 六、进度台账

| 日期 | 阶段 | 内容 | 提交 | 测试 |
| --- | --- | --- | --- | --- |
| 2026-10-13 | 基线 | 修复 `AgentSessionQueue.cs` 缺 `using`（CS0103），构建恢复 0 警告 0 错误 | 随 P1 一并提交 | 1313 / 1313 ✅ |
| 2026-10-13 | 计划 | 产出本文档 | 随 P1 一并提交 | — |
| 2026-10-13 | P1 | 4e-3e-9h-4a `AgentSession` 编排本体（`prompt`/`compact`/`_runAgentPrompt`/`_handlePostAgentRun`/`_runBeforeSettleBoundary`/`_runAutoCompaction`/`_checkCompaction`/`_runDefaultCompaction`/认证与重试叶子）；退役 4 处 `NotSupportedException` 缝；补记 C158/C159（上批遗漏）、新登记 C160 | `7b942b3`（已推送） | 1323 / 0 失败 ✅ |
| 2026-10-13 | P2 | 4e-3e-9h-4b SDK 装配本体 | （见下） | 见下 |

（后续阶段完成后逐行追加。）
