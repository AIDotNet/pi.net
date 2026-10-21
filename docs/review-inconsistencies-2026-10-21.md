# pi → pi.net 移植一致性审查清单（2026-10-21，独立复审）

> 审查者：以当前 TS 检出 `eb326d265` 为唯一参照真值（与台账记录一致，`eb326d265..HEAD` = 0，无漂移）。
> 本轮实测：`dotnet build Pi.slnx -m:1` **0 警告 0 错误**；`Pi.CodingAgent.Tests` **4953/0 失败**（2m42s）。
> 方法：三份登记文档（porting-status / migration-remaining-plan / migration-execution-status / coding-agent-porting-status / tui-porting-status / public-api-audit）交叉核对 + 疑点逐条回查代码与 TS 原件 + 表驱动常量抽查（Overflow 25=25 逐字对齐）。
> 编号 `C##` / `T##` 指向 coding-agent-porting-status.md 与 tui-porting-status.md 的差异登记。

---

## 一、代码层面仍与 TS 不一致（真实差异，非豁免）

| # | 位置 | 差异 | 状态/风险 |
|---|---|---|---|
| 1 | `Pi.Ai/Providers/Radius.cs:13-16` | 动态模型目录刷新**不做事务化持久化**：TS 经 ModelsStore `context.publish` 落盘，C# `RefreshModelsAsync` 只内存更新 | 代码注释自认、porting-status 第 9 条登记；重启动后动态目录丢失，回落基线目录。**当前唯一被代码注释自认的行为级差异** |
| 2 | C152 | `_replaceMessageInPlace` 引用同一性：C# 消息是不可变快照，`AgentLoopRunner` 自己的局部变量**观察不到**就地替换后的新内容（其余观察者经 `ConditionalWeakTable` 覆盖） | 结构性限制，登记为真实缺口；R6 复核项仍挂起 |
| 3 | C154 | 扩展**不能就地改写 `tool_call` 入参**（TS 直接改 `input` 即生效），C# 只能 `Block`/`Terminate` | 真实缺口，R6 复核项仍挂起 |
| 4 | C258 | mermaid 渲染引擎（grok-mermaid）不移植，`IMermaidRenderer` 缺省 null → mermaid 图**回落原文不渲染** | 永久豁免/挂起；功能上仍是缺口 |
| 5 | T22/T30 | CJK 词切分近似：`Intl.Segmenter` 的 ICU 词典分词无 .NET 等价物，**连续汉字串 >2 字时** Ctrl+W / Alt+D / 词级光标一次跨整段（7 条差分用例标 `icuFrom`，`CorpusIsFullyCovered` 钉住不增长） | 有意近似，已显式钉住 |
| 6 | T11 | TUI 文件型调试日志（`PI_TUI_DEBUG` → `/tmp/tui/*.log`、`pi-tui-crash.log`）未移植；超宽行守卫仍抛异常 | 诊断能力缺失，非功能差异 |
| 7 | C263 | 死终端判定比 TS **略宽**（.NET 无 errno，按异常类型映射 IOException/ODSE/断连 SocketException） | 已登记 + 用例钉住；注释留有「引入 errno 兼容层应收窄」备忘 |
| 8 | C276 | RPC 线路上 `TextDelta` 等事件**多出 sequence/index 字段**（TS 客户端忽略未知键，故兼容） | wire 略宽，单向兼容 |
| 9 | C142 | `ThinkingLevel` 枚举比 TS 6 值联合**多 `off` 档** | 消费方出现前不可观测 |
| 10 | C91(9x) | .NET `Trim()` 空白集比 JS 略宽（U+0085 等） | 真实输入域内等价 |
| 11 | **Overflow.cs（本轮新发现）** | 5 处正则保留 `\d`（另有 `[\d,]+` 3 处）而非项目自家约定 `[0-9]`（T5/C285：.NET `\d` 匹配 Unicode 十进制数字，JS 只匹配 0-9） | 语义**略宽**；输入域是 provider ASCII 错误文案，无实害。属「自家约定未贯彻」的一致性瑕疵，建议顺手改齐 |

## 二、平台豁免（登记在案的结构性差异，不算缺口）

- **Node/Bun 运行时垫片（4g-3h 批）**：`bun/*`、`jiti-loader/jiti-static-loader`、`virtual-modules.ts`、`experimental/source-resolver.ts`、`*.lazy.ts`、`experimental/plugin.ts`——.NET 侧由 AssemblyLoadContext 扩展加载、单进程单 theme、程序集预编译等原生机制替代，逐件核验成立（migration-execution-status「特殊范围决策」已登记）。
- **G5 Cloudflare Workers AI binding**（workerd `env.AI.fetch()` 适配）：显式排除，保留 HTTP 路径。
- **QuickJS WASM → `ICodemodeJsEngine` 注入**：缺省引擎已由 Jint 落地（G2 关闭，`af67fa0`）；但 Jint 与 QuickJS 的**内存计量不同**（已登记的运行时差异）。
- **JS Proxy 无对应物**：TUI reference 显式转发 30 成员（C262）、chord 服务门面显式 API（第 13 条）、六个类型化服务门面（C374）。
- **JS 语义复刻层**：`JSON.stringify` 转义自制编码器（孤立代理 STJ 层写 U+FFFD，编码器管不到）、`Math.round` 舍入、`toPrecision` 有理数实现（C381）、大小写映射 56 码点补丁（T23）——均为近似等价，有向量锁定。
- **`update --self`（npm/pnpm 自更新）不移植**：.NET 产物不是 npm 包，无可重装对象（`PackageManagerCli.cs:8-10` 注明，保留目标解析 + 精确诊断）。
- **无 SIGTERM**：Kill 即终止（SIGKILL 语义），signal 恒 null（C277）；SIGINT/SIGHUP 走 CancelKeyPress/PosixSignalRegistration（C340）。

## 三、有意保留的 TS 缺陷/怪癖（③类，逐条确认登记在案）

C7（parseInt V8 舍入）、C8（chalk close→close+open）、C12（cross-spawn 惰性量词，448 向量）、C65（裸 catch 吞取消）、C91(4d)（BoundaryState 对齐运行时构造）、C131（`"yes"` 算已通报）、C249（rain 特效永不自行结束）、C260（startsWith 前缀怪癖）、C287（帮助文本写死 `pi`，文本一致优先）、C321（Set 边遍历边删）、C349（ContinueWith 不传播异常→try/finally 复刻 JS finally 拒绝）、C356（定时器「已入队仍执行」）。

## 四、文档层面不一致（台账过期/质量问题）——本轮审查发现

| # | 问题 | 证据 |
|---|---|---|
| 1 | `migration-remaining-plan.md` 0.7 节顺延项**三处已过期**：P4b-7 `McpManagerView`/`showMcpManager`（已落地 `Extensions/Mcp/McpManagerView.cs`）、P4c `LlamaView`/`HuggingFaceSearch`/`showLlamaUi`（已落地 `Extensions/Llama/LlamaUi.cs` 等）、4e-3c-3 package-manager 网络检查本体（已落地 `Core/PackageManager.Updates.cs` 1,142 行，npm `npm view` + git `ls-remote` 比对齐全） | 代码 grep 实证；台账 703/704/714/881/883/926 行仍是旧口径 |
| 2 | P7 evals「~1,300 行，未建项目」**过期**：`src/Pi.Evals` 完整工程（20+ 文件），G4 容器验收已于 Stage 23 在外部 Ubuntu 完成 | `migration-execution-status.md` Stage 19/23 |
| 3 | **C86–C112 编号被两套系列重复使用**（4d 批表格 vs 4e-3e-9a~9f 列表，内容完全不同）；C317 两批重复登记。另有缺号：C33-C36（预留）、C145/C146/C149-C151 从未出现、C82-C85/C148 仅被引用无定义行 | 实证：C86 同时指 JsDiff（511 行）与 usage 处理（1702 行）；C92 同指 cache_warming_decision（517）与 message.role（1768） |
| 4 | `porting-status.md` 第 204 行 durable harness 测试「约 8,900 行顺延」**口径未更新**：`tests/Pi.Durable.Tests` 已有 23 个 `Harness*Tests` 类，实际覆盖是否对齐 8,900 行 TS 未对账 | 该文件自标「历史快照」，但此条被下游引用为待办 |
| 5 | `migration-remaining-plan.md:1409`「真正的缺口只有一个 codemode renderer」已被 `c989567` 兑现，同节仍以现在时表述（0.41 节本身有收口记录，属可容忍的自洽） | — |

> 建议按 0.40 节自定的约定（「暂缓/未移植记录必须在兑现提交里同步回写」）做一轮文档清扫；C 编号复用建议冻结旧段、新差异从 C383 顺延。

## 五、环境/验收边界（非移植差异，复审确认在案）

- 本机 5 项平台跳过：2 Linux-only Evals 权限测试、2 无 POSIX shell、1 无 sshd。
- durable 满负荷偶发挂起（约 1/6 运行，早于本批存在，porting-status 第 55 条）。
- 2 个负载敏感 flake 观察名单：`RadiusLoginMenuComponentTests.ConfirmingStopsTheRenderRequests`、`CoreProviderComposerTests.ImagesWithoutAnyImplementationReportTheModelsLayerMessage`（单跑全绿）。

## 结论

- **整文件功能缺口：0**（覆盖审计脚本结论 + 本轮独立抽查支持；基线无漂移）。
- **真实行为差异**：一节 11 条，其中 #1（Radius 持久化）是唯一可能造成可观察数据损失的；#2/#3 是扩展 API 能力收窄；其余均有测试钉住或影响域可忽略。
- **平台豁免与有意保留**均已登记且有据可查，未见「伪装成已实现」的排除项。
- **最大问题在文档卫生**：台账多处指向已兑现的缺口（历史上已两次因此重复排期），C 编号复用埋着误引风险——建议单独排一个清扫批。
