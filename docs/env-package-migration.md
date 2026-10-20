# `packages/env` 迁移（远程执行环境 over SSH）

本文是 `packages/env` → `Pi.Env` 的阶段计划、验收标准与执行台账。它是
[migration-execution-status.md](./migration-execution-status.md) 中 P2 项的展开，
并修正该文件对 `env` 包的映射记录。

## 一、为什么这是一个真实缺口

`migration-execution-status.md` 的逐包矩阵把 `env` 记为「并入 `Pi.Durable/Env`，已迁移」。
逐文件核对后该记录不成立：

| 源 | 内容 | .NET 现状 |
|---|---|---|
| `packages/durable/src/env/` | 执行环境**抽象**（`FileSystem` / `Shell` / `ExecutionEnv` / `NodeExecutionEnv`） | ✅ `src/Pi.Durable/Env/`（`IExecutionEnv`、`LocalExecutionEnv` 等） |
| `packages/env/src/` | 基于 **SSH + 守护进程**的**远程**执行环境（`connection.ts` / `ssh.ts` / `remote-env.ts` / `watch.ts` / `errors.ts`，2,073 行） | ❌ 全仓无对应实现 |

佐证：全仓检索 `HostKey` / `RemoteExecutionEnv` / `DeployDaemon` / `daemon` 均无命中；
`Pi.Durable/Env` 的公开面是 `IExecutionEnv` / `LocalExecutionEnv` / `FileStat` 等，
与 `packages/env` 的 26 个导出**零交集**。因此 `env` 是 14 个源包中唯一整体缺失的一个。

判定方法可复现：`tools/audit-export-surface.py`（本地诊断脚本，按仓库约定 `tools/` 不入库）逐包解析 TS `src/index.ts` 的导出闭包
（含 `export *` 递归），与对应 C# 项目的公开符号集合做归一化比对。其余 13 包的未匹配项
全部落在「TS 纯类型别名 / 类型守卫 / 工厂函数」上，`env` 则是 26/26 全未匹配。

## 二、范围界定：客户端移植，守护进程按原生产物处理

`packages/env` 由两部分组成：

| 部分 | 语言 | 规模 | 本阶段决策 |
|---|---|---|---|
| 客户端（`src/*.ts`） | TypeScript | 2,073 行 | **完整移植**到 `Pi.Env` |
| 守护进程（`daemon/src/*.rs`） | Rust | 4,027 行 | **不移植**，按原生产物（native artifact）处理 |

**判断依据**：

1. 迁移的对象是 TypeScript 仓库；`daemon/` 是 Rust 子系统，与 TS 代码没有共享类型或逻辑，
   只通过 [docs/protocol.md](../../pi/packages/env/docs/protocol.md) 的线上协议交互。
2. 上游自身也不在安装时构建守护进程：`package.json` 的 `files` 含 `bin/`，发布物是**预编译二进制**
   （`bin/pi-env-<platform>-<arch>/pi-env[.exe]`），`build:daemon` 只在开发/CI 里跑 cargo。
   客户端对它的唯一契约是「一个可执行文件路径」。
3. 与 Stage 6 决策 B 同构：不复制 Bun 运行时/打包器，保留可观察行为与产物定位契约。
   把 4,027 行系统级 Rust（Windows Job Object、`O_NOFOLLOW`、libuv 错误码翻译、
   inotify/FSEvents 语义）逐字重写成 C#，收益低于风险。

**因此 `Pi.Env` 的可观察契约是**：协议帧格式、同步行、请求/取消/事件语义、SSH 参数与
主机密钥策略、部署与校验流程、路径与错误码映射、读写/扫描/watch/exec 的 Node 语义。
守护进程本身以「路径」注入（`SshConnectOptions.Binary`、`ConnectionOptions.Command`）。

**验证手段**：本机可用 Rust 1.92，按上游源码构建**参考守护进程**（`cargo build`），
C# 客户端直接与它对话，跑 Durable 的 env conformance 全套用例 + 定点行为用例。
这样等价性由「同一守护进程 + 移植后的客户端」共同证明，而不是靠人工比对。

## 三、阶段划分

依赖顺序：协议层 → SSH 层 → 环境层。每阶段收口条件一致：
`dotnet build Pi.slnx -m:1` 0 警告 0 错误 + 该阶段测试全绿 + 逐条对齐 TS 原文。

| 阶段 | 目标 | 文件范围 | 验收标准 |
|---|---|---|---|
| **E1. 协议连接层** | 帧编解码、同步行、请求/结果/错误/事件、取消、ping 与静默超时、启动超时、丢失重连语义 | `src/Pi.Env/Connection.cs`、`EnvJson.cs`、`RemoteError.cs` | 与参考守护进程互通：`hello` 字段齐全、`lstat`/`pread` 往返、错误码透传、cancel 立即生效、kill 与 abort 的差异、丢连接后旧会话请求失败、启动失败可重试、坏帧断开、同步行前的噪声被丢弃 |
| **E2. SSH 层** | `sshArguments` 全部安全开关、主机密钥扫描/接受/遗忘、平台探测、按内容寻址部署与校验、连接工厂（惰性/急切） | `src/Pi.Env/Ssh.cs`、`KnownHosts.cs` | 参数逐项等于 TS（顺序、引号、`%%` 转义、非法字段拒绝）；known_hosts 并发写、别名限定、类型冲突拒绝、遗忘；部署的 SHA-256 命名与旧版本清理；无 sshd 时纯逻辑用例仍全绿 |
| **E3. 远程环境层** | `RemoteExecutionEnv`（路径解析、错误映射、读写分块与并发、行读取、目录分页、临时文件、exec 超时/中止/回调错误、cleanup）+ `RemoteWatcher`（重连与 overflow） | `src/Pi.Env/RemoteEnv.cs`、`RemoteWatcher.cs`、`Errors.cs` | Durable `EnvConformance` 全套通过；大文件写入/追加/位置读一致；watch 的 native/polling 与重连 overflow；exec 的 window/skipped 计量；`cleanup` 不产生 aborted |
| **E4. 台账与交付** | 迁移清单、逐文件映射、差异记录、执行记录 | 本文 + `migration-execution-status.md` + `coding-agent-porting-status.md` 的入口 | 文档与代码一致；提交已推送；工作区干净 |

## 四、已知差异（须在实现时逐条落实）

| 编号 | 差异 | 理由 |
|---|---|---|
| E-C1 | 守护进程不重写，按原生产物路径注入 | 见第二节 |
| E-C2 | `packagedDaemon` 的根目录取 `AppContext.BaseDirectory`，而非 npm 包根 | .NET 没有「包根」概念；输出目录即发布根，`bin/` 随发布物布置 |
| E-C3 | `runSsh` 的进程错误以 `Process` 事件而非 libuv 回调表达；退出码语义一致 | 平台 API 差异 |
| E-C4 | 错误消息里的 `JSON.stringify(value)` 改用 STJ 的宽松转义 | 仅影响消息文本；`toThrow()` 级断言不变 |
| E-C5 | 文件模式（0700/0600）只在非 Windows 上设置 | Windows 无 POSIX 模式位 |
| E-C6 | 参考守护进程来自上游 Rust 源码的本地 `cargo build`，不随仓库分发 | 与上游发布模型一致（二进制在发布产物里） |
| E-C7 | 测试里「中止/超时应在若干毫秒内落定」的墙钟预算在 Windows 上按实测放宽 | 守护进程用 `taskkill /F /T` 在 Windows 上终止进程树，用 `kill(-pid)` 在 POSIX 上终止。实测（裸帧协议脚本、零客户端逻辑、13 次采样）：Windows 每次终止 **1.1–1.8 秒**，与进程树形状无关（`cmd /c ping` 与裸 `ping` 相同）。上游阈值按 POSIX 标定 |
| E-C8 | 测试宿主的 `sh` 用 Git Bash，并允许 `PI_ENV_BASH` 指到非标准安装位置 | 上游把 `$ProgramFiles/Git/bin/bash.exe` 写死；本机 Git 装在被宿主程序附带的 PortableGit 里，硬编码路径会退化成静默跳过（见执行记录） |

## 五、执行记录

### E1 协议连接层 — `9013cbb`

- `src/Pi.Env/{EnvJson,Connection}.cs` + `Pi.Env.csproj`，`Pi.slnx` 增加 `src/Pi.Env`、`tests/Pi.Env.Tests`、`tests/Pi.Env.FakeDaemon`。
- 参考守护进程：`cargo build --target-dir tools/pi-env-daemon-target`（本机 Rust 1.92）→ `pi-env 1.0.4`。
- `ConnectionTests` 9/9；全解决方案 `dotnet build Pi.slnx -m:1` 0 警告 0 错误。

### E2 SSH 层 — `d7230b9`

- `src/Pi.Env/Ssh.cs`，`SshTests` 9/9（`Pi.Env.Tests` 合计 18/18）。
- 该轮把依赖 `RemoteExecutionEnv` 的 sshd 端到端用例暂时移出，E3 补回（见下）。

### E3 远程环境层 — `b355710`

- `src/Pi.Env/{RemoteEnv,RemoteWatcher,Errors}.cs`；`NodePath` 从 `Pi.CodingAgent/Utils` 下沉到
  `Pi.Durable/Env`（远程环境要按**远端**规则解析路径，否则 `Pi.Env` 会反向依赖 `Pi.CodingAgent`），
  `Pi.CodingAgent/GlobalUsings.cs` 用 `global using` 别名保住 57 个调用点不动。
- `ExecutionError` 增加可选 `spillPath` 参数（`exec` 失败时要能带上溢出文件路径）。
- 结果：`Pi.Env.Tests` **47 项 / 46 通过 / 1 跳过**（跳过的 sshd 端到端用例在本机不可用，与上游同策略）。
  `Pi.Durable.Tests` 398 项通过。全解决方案 0 警告 0 错误。

一致性检查（逐条对齐 TS 原文）：

| 检查项 | 结果 |
|---|---|
| `packages/env` 导出面 26 项 | 26/26 匹配（`tools/audit-export-surface.py`，本地诊断脚本）。脚本原先把 `env` 映射到 `Pi.Durable`，沿用台账的错误记录，已改为 `Pi.Env`；并补上 `<Pascal>Async` 候选，顺带修正 `durable`/`coding-agent` 各 2 个同类假阴性 |
| `abortResult` 调用点 | TS 17 处 ↔ C# 17 处，全部传 `context.abortSignal` |
| `watch.ts` ↔ `RemoteWatcher.cs` | 退避 1s→30s、`ready` 才完成、`error` 后置 closed、断连重连后上报 `overflow`、回调抛异常不中断，逐条一致 |
| `errors.ts` ↔ `Errors.cs` | 错误码映射表逐项一致（`ENOENT/ENOTDIR/EISDIR/EINVAL/SYMLINK/NOT_REGULAR/EACCES/EPERM`） |
| Durable `EnvConformance` 20 项 | 20/20 通过（此前有 13 项被自造的按名字门控静默跳过，见下） |

本轮修正的测试侧问题（均非实现缺陷）：

1. **静默跳过**：conformance 测试曾按用例名（`exec`/`watch`/`symlink`）门控，缺 POSIX shell 时 `return`，
   13/20 项「通过」其实什么都没跑。上游 `conformance.test.ts` **没有这道门**，且 watch 用例只用
   `WriteFileAsync`/`RenameFileAsync`/`RemoveAsync`，根本不碰 shell。已删除门控，改为真实执行。
2. **本机没有 Git Bash**：`$ProgramFiles/Git/bin/bash.exe` 不存在，宿主程序自带的 PortableGit 在
   `D:\soft\WorkBuddyAI\resources\vendor\PortableGit\usr\bin\bash.exe`。`FindBash` 增加 `PI_ENV_BASH` 覆盖后，
   conformance 20/20 实跑通过。
3. **中止/超时阈值**：见 E-C7。判定依据是裸帧协议脚本（不经过任何 C# 代码）对同一个守护进程的实测，
   同步取消 0 ms、延迟取消 1.0–1.4 s、`timeoutMs=300` 落定 1.4–1.8 s。
4. **用例命令形状**：`LongRunning` 原用 `cmd /c ping -n 30`（30 秒），上游是 `["sleep","5"]`（裸进程、约五秒）。
   已改为裸 `ping -n 6` 对齐形状——一旦某次中止没生效，用例仍在五秒内有界。

未完成 / 未验证（不得记为通过）：

- `SshdTests` 的端到端用例（未信任主机被拒 → 扫描接受 → 部署校验 → 经 SSH 执行）已按 TS 结构移植，
  但**本机为 Windows 且无 sshd，实测跳过**（上游 `describe.skipIf(sshd === undefined)` 同样从不在此平台跑）。
  需在 POSIX 主机上执行一次才能记为已验证。
- `Pi.Durable.Tests` 观察到的既有抖动（与本轮改动无关，两轮全量各挂一个不同用例、单独跑均通过）：
  `ToolsTests.Bash_StreamsOutputThroughApiAndReturnsNoContent`（只收到 stderr 分块）、
  `HarnessGenerationRecoveryTests.ConvertsCommittedPartialToAbortedEntryBeforeResendingAfterReopen`（`zz` vs `zzz`）。
  两者都在未改动的代码与测试里，属负载敏感竞态，另行排查。
