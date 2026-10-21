# 公开 API 与 JSON/wire 契约审计（Phase 3）

- TypeScript 基线：`D:\AI\参考项目\pi`，`eb326d265ae0b88489a6d10319307780df827cdf`。
- .NET 起点：`af67fa0`，审计结论适用于本文件同批提交的 Phase 3 工作区。
- 关联台账：[migration-execution-status.md](./migration-execution-status.md)。

本审计回答两个问题：源包的公开导出面在 .NET 是否有可调用/可承载的落点；跨进程与 provider 边界的 JSON/wire 形状是否一致。它是逐项分类结论，不是“文件名/导出名自动匹配率”。

## 判定方法

1. **入口解析**：按各包 `package.json` 的 exports 与 `src/index.ts` 入口，递归展开 `export *`、`export { … } from`，只统计可从包外导入的符号；`evals` 是 CLI 包，没有 barrel export，故不在导出面统计内。
2. **声明分类**：将每个导出分为 `runtime`（function / class / const / let / var，可在运行时调用或取值）、`type`（interface / type alias / 仅类型位置的声明）和 `unknown`（静态脚本无法判定）。`unknown` 逐个人工读源码判型：36 个是 `async function`，3 个是纯类型（`Static`、`TSchema`、`Type`）。
3. **C# 落点匹配**：先按同名 public 标识符匹配类型与静态类/成员；未同名者按语义核对别名、构造器、静态工厂、属性承载或类型系统映射，不使用“名字不同即缺失”的推断。
4. **类型声明的判定**：TS 类型不需要在 C# 出现同名类型。结构类型、联合类型、模板字面量、条件类型、`MaybePromise` 等由 C# record/interface/enum/泛型/`Task`/`JsonNode` 等承载；只要存在可表达同一数据形状的 C# 类型或序列化路径即视为已映射。纯编译期构造（如 `Static`、`Type`）不产生运行时契约。
5. **runtime 声明的判定**：必须能从 C# 公共入口到达。平台专属适配器（Node/Bun/workerd）按既有范围决策显式排除，并在此登记，不伪装成已实现。
6. **wire 证据**：对 provider、MCP、存储等序列化边界，以纯函数 wire 测试和真实传输测试为准；没有行为测试支撑的“同名”不算通过。

## 导出面总量

| package | 导出总数 | runtime | type | unknown | 同名命中 | 未同名 |
|---|---:|---:|---:|---:|---:|---:|
| `telemetry` | 34 | 4 | 30 | 0 | 14 | 20 |
| `protocol` | 37 | 20 | 17 | 0 | 21 | 16 |
| `agent` | 42 | 5 | 34 | 3 | 27 | 15 |
| `mcp` | 66 | 18 | 48 | 0 | 33 | 33 |
| `server` | 18 | 8 | 10 | 0 | 9 | 9 |
| `client` | 15 | 5 | 10 | 0 | 11 | 4 |
| `ai` | 258 | 66 | 188 | 4 | 167 | 91 |
| `chord` | 72 | 25 | 46 | 1 | 53 | 19 |
| `codemode` | 35 | 19 | 16 | 0 | 31 | 4 |
| `env` | 26 | 11 | 9 | 6 | 18 | 8 |
| `durable` | 192 | 35 | 155 | 2 | 133 | 59 |
| `tui` | 173 | 88 | 82 | 3 | 149 | 24 |
| `coding-agent` | 458 | 138 | 300 | 20 | 348 | 110 |
| **合计** | **1,426** | **442** | **945** | **39** | **1,014** | **412** |

412 个未同名项全部完成归类：类型层映射、别名/静态类/构造器落点、或平台专属排除。逐包未同名清单见 `tools/export-surface-audit.md`（该目录被 `.gitignore` 排除，仅作审计原始输出，不作为交付物）。

## 别名与形态映射（代表性证据）

| TypeScript 导出 | C# 落点 | 形态 |
|---|---|---|
| `decodeCbor` / `encodeCbor` | `Pi.Protocol.Cbor.CborCodec.Decode` / `Encode` | 拆到 partial 静态类的同语义方法 |
| `isJsonRpcRequest` / `isJsonRpcNotification` | `Pi.Mcp.Protocol.JsonRpc.IsRequest` / `IsNotification` | 类型守卫 → 静态谓词 |
| `scanHostKey` / `deployDaemon` / `connectSsh` | `Pi.Env.Ssh.ScanHostKeyAsync` / `DeployDaemonAsync` / `ConnectSshAsync` | 驼峰函数 → async 方法 |
| `runPrintMode` / `runRpcMode` | `Pi.CodingAgent.Modes.PrintMode.RunAsync` / `RpcMode.RunAsync` | 模块函数 → 模式类的静态入口 |
| `setDefaultStreamFn` | `Pi.Agent.DefaultStreamFn.Set` | 模块级可变状态 → 静态类 |
| `withFileMutationQueue` | `Pi.CodingAgent.Core.Tools.FileMutationQueue.WithFileMutationQueueAsync` | 高阶函数 → 泛型 async 方法 |
| `fuzzyFilter` | `Pi.Tui.Fuzzy.Filter<T>` | 同名语义、C# 命名风格 |
| `parseFrontmatter` / `stripFrontmatter` | `Pi.CodingAgent.Utils.Frontmatter` | 成组函数 → 静态工具类 |
| `defineTelemetrySchema` / `createTypedSpanStarter` | `Pi.Telemetry.TelemetrySchema` / `TypedSpanStarterFactory` | 工厂 → 静态类/工厂 |
| `registerFauxProvider` 等 faux 系列 | `Pi.Ai.Compat` 与 `Pi.Ai.Faux` | 测试用 provider 契约由 C# 类型直接承载 |
| `JsonObject` / `JsonValue` / `JsonRepresentation` | `System.Text.Json.Nodes.JsonObject` / `JsonNode` | 类型层复用 BCL |
| `TSchema` / `Type` / `Tool` | `Pi.Ai.Types.ToolSchema` / `ToolDefinition` / `Pi.Mcp.Types.Tool` | 类型别名 → record |

类型声明的典型承载还包括：`ContentBlock` → C# `ContentBlock` 继承层次；`MaybePromise<T>` → `Task<T>`；`AnyModel` / `BaseModel` → `Model` / `ModelSpec`；`KnownProvider` / `KnownApi` → provider/API 字符串常量与注册表。它们不要求同名，但要求在 wire/调用边界可往返。

## JSON/wire 对照证据

| 边界 | 覆盖内容 | 测试 |
|---|---|---|
| OpenAI Chat Completions | provider compat 矩阵、`max_tokens`/`max_completion_tokens`、developer role、thinking 格式（openai/deepseek/openrouter/zai/together）、缓存标记与会话亲和、空工具数组、usage 四桶、stop reason、`reasoning_details` 回放、工具结果图片、工具调用 ID 归一化 | `tests/Pi.Ai.Tests/OpenAiCompletionsWireTests.cs`（32 项）、`OpenAiCompletionsTests.cs` |
| Anthropic Messages | compat 覆盖、system/tools/最后一条消息的 `cache_control`、`1h` TTL 与 `none`、legacy budget thinking 与 adaptive effort/output_config、thinking disabled、managed effort 的历史消息插入、原生中段工具增删、OAuth Claude Code 身份与工具名映射、server-side fallback、stop reason 映射 | `tests/Pi.Ai.Tests/AnthropicMessagesWireTests.cs`（10 项）、`AnthropicMessagesTests.cs`（3 项 SSE 集成） |
| MCP JSON-RPC | progress token 与超时续期、调用方中止/超时的 `notifications/cancelled`、服务端取消、未知响应错误、工具 `annotations`/`outputSchema`/`execution`/`_meta` 保留、stdio 字节 framing/超限/截断/关闭 | `tests/Pi.Mcp.Tests/McpClientTests.cs`、`StdioTransportTests.cs`（8 项，真实 Node 子进程） |
| usage 与成本 | `input = max(0, prompt − cached − cacheWrite)`、四桶总量、按总输入量的严格大于分级成本 | `Pi.Ai.Tests` 中 usage/cost 对照用例 |

审计过程中 wire 测试发现并修复了两个真实的 `JsonNode` 归属缺陷：

- OpenAI 工具结果图片块被重复挂载（`content.Add(image.DeepClone())`）。
- Anthropic 原生中段工具变更把仍挂在源数组上的 `definition` 直接赋给新对象，触发 `The node already has a parent`；修复为 `definition.DeepClone()`（`src/Pi.Ai/Api/AnthropicMessages.cs`）。

这两处都不是命名或接口问题，只有对请求体做结构断言才会暴露，说明 wire 测试应作为公开契约验收的一部分而非可选项。

## 排除项（显式登记，不计为缺口）

- **G5 Cloudflare Workers AI binding**：`packages/ai/src/api/cloudflare-ai-binding.ts` 依赖 workerd 的 `env.AI.fetch()`，属于平台注入适配器；.NET 保留 HTTP 路径 `Pi.Ai.Providers.CloudflareWorkersAi`。按 Node/Bun 适配器同一规则排除。
- **Node/Bun 运行时专属物**：Bun 打包器/启动垫片、`bun-oauth.ts`、Node 动态加载钩子、QuickJS WASM 载荷。分别由 Stage 6 决策 B 与 Stage 14 的 Jint 引擎决策覆盖，保留可观察行为（CLI、子进程、环境继承、资源定位、CodeMode 工具桥）。
- **纯导出桶**：各包 `index.ts`、`chord/src/delta/draft.ts` 等类型别名文件不产生独立 C# 语义。
- **无头环境受限的 OAuth loopback**：浏览器回调测试不作为 CI 通过项，但对应 OAuth 契约有纯函数/流程测试。

## 功能缺口收口（G7）

| 项 | TS 行为 | .NET 现状 | 影响与处置 |
|---|---|---|---|
| Anthropic workload identity federation（OIDC） | `packages/ai/src/api/anthropic-messages.ts` 构造 `authentication: { type: "oidc_federation", federation_rule_id, service_account_id, identity_token: { source: "file", path } }`，由 Anthropic SDK 的 `lib/credentials` 完成 token exchange 与刷新 | **已实现（2026-10-21）**：`Auth/AnthropicFederation.cs` 直接实现 `POST /v1/oauth/token` 的 RFC 7523 jwt-bearer 交换（`oauth-2025-04-20,oidc-federation-2026-04-01` 双 beta 头、16 KiB assertion 上限、https/环回校验、错误体按 RFC 6749 字段脱敏、`Request-Id` 透出），并按 SDK 语义实现令牌缓存：剩余 >120s 直接使用、30–120s 返回旧令牌并后台刷新（失败退避 5s）、≤30s 阻塞刷新、并发刷新合并；每次交换重新读取身份令牌文件以支持轮换。`AnthropicMessages.Stream` 注入 `Authorization: Bearer` 并追加 `oauth-2025-04-20` beta，`StreamSimple` 的认证预检同步放行 federation | 使用 federation 的部署现在无需 API key 即可调用；API key / `ANTHROPIC_AUTH_TOKEN` / 显式认证头仍然优先，且仅 `anthropic` provider 参与交换。新增 24 项测试（5 项 provider 解析、18 项 token/messages wire、1 项 `Models` 端到端），覆盖交换一次复用、请求形状、可选 id、优先级、其他 provider 拒绝、到期重换、令牌文件轮换、并发合并、错误脱敏、明文端点拒绝与文件校验 |

除 G7 外，本审计未发现公开导出面或 wire 形状的功能缺失；容器级（G4）与 POSIX/SSH（G6）属于验收环境阻塞，已分别在 Stage 16、Stage 17 跟踪。

## 复现命令

```bash
dotnet build Pi.slnx --no-restore -m:1 -v:minimal
tests/Pi.Ai.Tests/bin/Debug/net10.0/Pi.Ai.Tests.exe
tests/Pi.Mcp.Tests/bin/Debug/net10.0/Pi.Mcp.Tests.exe
git diff --check
```

本轮实测：solution 构建 0 警告、0 错误；`Pi.Ai.Tests` 426/426；`Pi.Mcp.Tests` 47/47。`dotnet test` 在当前 Microsoft.Testing.Platform 配置下不是有效入口，测试通过各测试项目的可执行文件运行。
