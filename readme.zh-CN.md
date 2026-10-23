# Pi.NET

[English](https://github.com/AIDotNet/pi.net/blob/main/readme.md) | [简体中文](https://github.com/AIDotNet/pi.net/blob/main/readme.zh-CN.md)

[![NuGet](https://img.shields.io/nuget/v/Pi.Ai?label=nuget)](https://www.nuget.org/packages/Pi.Ai)
[![CI](https://github.com/AIDotNet/pi.net/actions/workflows/ci.yml/badge.svg)](https://github.com/AIDotNet/pi.net/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/github/license/AIDotNet/pi.net)](https://github.com/AIDotNet/pi.net/blob/main/LICENSE)

Pi.NET 是 [Pi](https://github.com/earendil-works/pi) 的 .NET 10 移植版。Pi 是一个极简、可扩展的 Agent harness（TypeScript monorepo，约 17.1 万行），本项目把它的核心库逐一移植到 `net10.0`，让 .NET 生态用上同一套能力。

Pi 的理念是「让 Pi 适配你的工作流，而不是反过来」：库只做最小而完整的事，上层由你自己组合。本项目沿用这一理念——不是把 TS 代码翻成 C# 就完事，而是在语义等价的前提下用 C# 的方式重建（判别联合 → abstract record 模式匹配、JS Proxy → 显式动词 API）。

> **本项目完全由 AI 驱动。** `src/`、`tests/`、`samples/`、`docs/` 下的代码、测试与文档都由 AI 以 TS 实现为行为参考生成并维护。这换来了推进速度和一致性，也意味着必然存在粗糙的地方。**遇到 bug 欢迎直接[提 issue](https://github.com/AIDotNet/pi.net/issues/new)——报告本身就是它被修好的方式。**

<p align="center">
  <a href="https://github.com/AIDotNet/pi.net/blob/main/docs/media/pi-net-promo.mp4">
    <img src="https://raw.githubusercontent.com/AIDotNet/pi.net/main/docs/media/pi-net-promo-poster.png" alt="Pi.NET 产品宣传片" width="820">
  </a>
  <br>
  <sub>39 秒宣传片（带配音） · 点击封面播放（1080p / MP4 / 5.4 MB）</sub>
</p>

## 它能做什么

- **统一的多 Provider LLM API**——10 个内建 API 实现（openai-completions / openai-responses / openai-codex-responses / azure-openai-responses / anthropic-messages / google-generative-ai / google-vertex / mistral-conversations / bedrock-converse-stream / pi-messages）+ **42 家内建 provider 全家桶**；流式事件、工具调用、thinking 预算、延后响应（deferred）、图片生成（images）与文本分类（classify）；9 家 provider 的 OAuth 登录流（PKCE / RFC 8628 设备码 / loopback 回调 / 刷新编排）。
- **Agent 运行时**——Agent 状态机与双队列、agent-loop 主循环、顺序 / 并行工具执行、abort / reset。
- **MCP 客户端**——JSON-RPC 协议层 + in-memory / stdio / streamable-http 三种传输 + 完整 OAuth 层（元数据发现、PKCE 授权码、动态注册、凭据失效重试）。
- **应用编排运行时（Chord）**——服务与复制状态、delta 引擎、facets 依赖图与拓扑激活、外部服务源绑定。
- **TCP RPC 通道**——握手校验、请求分发、取消、推送，为远程控制 coding-agent 准备。
- **供应商中立的遥测**——契约 + 参考适配器 + conformance 测试。

## 包

| 包 | 说明 |
|---|---|
| **Pi.Telemetry** | 供应商中立的遥测契约、参考适配器与类型化 schema，带 conformance 测试套件 |
| **Pi.Protocol** | CBOR 线上协议：framing / codec，definite-length 严格子集 + JS safe-integer 边界，与 TS 实现**线上兼容** |
| **Pi.Ai** | 统一多 Provider LLM API：原生 API 样板 + 兼容商注册表；流式、工具调用、thinking、images / classify、OAuth 登录全流程 |
| **Pi.Agent** | Agent 运行时：状态机 / 双队列、agent-loop 主循环、工具执行 |
| **Pi.Mcp** | MCP 客户端：JSON-RPC + 三种传输 + OAuth 全层 |
| **Pi.Chord** | 独立的应用编排运行时：服务、复制状态、RPC 与插件 |
| **Pi.Server / Pi.Client** | RPC 传输：握手 / 请求 / 取消 / 推送、会话路由、Unix 域套接字与本地服务器发现 |
| **Pi.Codemode** | 沙箱化 JavaScript 执行：唯一能力是调用注入的工具；@options 源码解析、TypeScript 声明渲染与沙箱编排，默认由 Jint 承载 VM，可通过 `ICodemodeJsEngine` 替换 |
| **Pi.Tui** | 终端 UI 框架：差分渲染 + 同步输出、字素级宽度测量与换行、ANSI/OSC 解析与 SGR 跟踪、Kitty 键盘协议与按键解码、overlay 栈、焦点管理、鼠标事件派发、颜色（OKLCH/OKHSL）与键位注册表、stdin 转义序列缓冲、终端内联图像（Kitty / iTerm2 协议）、栈/滚动布局引擎、LaTeX 数学渲染、斜杠命令与文件路径自动补全（含 `fd` 模糊搜索）、单行输入组件（Emacs 风格 kill/yank、撤销、括号粘贴）、选择列表组件（过滤/滚动窗口/鼠标/滚轮）、多行编辑器、markdown 渲染、设置列表、图像组件、备用屏渲染器（全屏模式：搜索 / 选择与复制 / 滚动条 / Kitty 图像缓存 / overlay 路由） |
| **Pi.Durable** | Durable 会话、内存 / SQLite / JSONL 文件存储后端、任务图、恢复机制与 coding-agent harness |
| **Pi.Env** | SSH 远程执行环境：`pi-env` daemon 客户端及它所讲的帧协议（daemon 本身是上游的原生构件） |
| **Pi.CodingAgent / Pi.Cli** | 交互式编码 Agent、工具、已编译 .NET 扩展、CLI、RPC 与实验性服务/worker runtime |
| **Pi.Vacation** | 基于 coding-agent runtime 的独立研究与规划宿主 |
| **Pi.DurableHost** | 基于 coding-agent runtime 的独立 durable 会话宿主（`pi-durable`），带 `subagent` 工具与按目录缓存的执行环境 |
| **Pi.Evals** | 原生 .NET host/documentation 评测工具；配对文档评测使用 Docker |

上表覆盖整个仓库。**其中 Pi.Telemetry、Pi.Protocol、Pi.Ai、Pi.Agent、Pi.Mcp、Pi.Chord、Pi.Server、Pi.Client、Pi.Codemode、Pi.Tui、Pi.Durable、Pi.Env、Pi.CodingAgent 是发布到 nuget.org 的 13 个库**；`Pi.Cli`、`Pi.Vacation`、`Pi.DurableHost`、`Pi.Evals` 是可执行项目，从源码构建，刻意不打成包。

TypeScript monorepo 仍是行为参考；随包提供的上游 docs/examples 保留为 TypeScript 参考资料，C# 扩展 API 请看 [.NET 扩展指南](https://github.com/AIDotNet/pi.net/blob/main/src/Pi.CodingAgent/Assets/docs/dotnet-extensions.md)。权威迁移阶段、验收证据与已记录的 TypeScript/.NET 差异见 [migration-execution-status.md](https://github.com/AIDotNet/pi.net/blob/main/docs/migration-execution-status.md)。

## 快速开始

### 从 NuGet 安装

13 个库已发布到 nuget.org，包名与项目名一致，目标框架 `net10.0`：

```bash
dotnet add package Pi.Ai        # 多供应商 LLM 统一 API
dotnet add package Pi.Agent     # Agent 主循环
dotnet add package Pi.Mcp       # MCP 客户端
dotnet add package Pi.CodingAgent   # 整套依赖，含 CLI 运行时资源
```

### 使用文档

- **[docs/usage](https://github.com/AIDotNet/pi.net/blob/main/docs/usage/README.md)** —— 怎么从 C# 里用这些包：按用途选包、最小可运行代码、凭据配置，以及几个值得提前知道的坑。
- **[docs/usage/models-configuration.md](https://github.com/AIDotNet/pi.net/blob/main/docs/usage/models-configuration.md)** —— `models.json` 的字段级参考：provider / model / modelOverrides / inputLimits / compat 逐字段的类型、默认值与说明，由已发布的 JSON Schema 生成。

包内容、发版流程和回退方式见 [docs/publishing.md](https://github.com/AIDotNet/pi.net/blob/main/docs/publishing.md)。

### 从源码构建

需要 .NET 10 SDK。

```bash
dotnet restore Pi.slnx
dotnet build Pi.slnx --no-restore
dotnet run --project src/Pi.Cli/Pi.Cli.csproj -- --help
```

测试项目使用 xUnit v3 和 Microsoft.Testing.Platform。可直接运行一个测试项目：

```bash
dotnet run --project tests/Pi.Evals.Tests/Pi.Evals.Tests.csproj --no-restore
```

## 示例

`pi` 就是那个 coding agent。发布一次，把 `pi` 放进 `PATH`：

```bash
dotnet publish src/Pi.Cli -c Release -o ./pi-bin
export PATH="$PWD/pi-bin:$PATH"    # PowerShell：$env:PATH = "$PWD\pi-bin;$env:PATH"
pi --version
```

### 在终端里跟它对话

```bash
cd /path/to/your/repo
pi
```

第一次用先在会话里敲 `/login`，选一个 provider（OAuth 或 API key）。之后直接说要做什么：

```text
把这个仓库的结构讲一遍，然后给参数解析补一个测试。
```

读了哪个文件、跑了哪条命令、改了哪几行，它都会打出来，但不会每调一次工具就停下来问你——所以要么把活在版本控制里跑，要么用下面的只读跑法。

### 用 `@` 把文件先塞给它

```bash
pi @src/Pi.Cli/Pi.Cli.csproj @Directory.Build.props "程序集为什么叫 pi？它都引了哪些包？"
```

`@路径` 会展开进首条消息，模型一上来就拿到文件内容，不用先花一轮去读。图片同理。

### 放进脚本里跑

```bash
# -p：干完活，把最终回答打到 stdout，退出
pi -p "列出 src/ 下所有 net10.0 的工程，一行一个。"

# 管道进来的 stdin 会拼到提示词前面，并且自动进入非交互模式
git diff | pi "看这个 diff 有没有正确性问题。别客气。"
```

只要不是交互场景都算：stdin 或 stdout 任一端不是终端就会走 print 模式，只有两端都在终端上时才需要显式写 `-p`。

### 要 JSON，不要散文

```bash
pi --mode json "找出最慢的三个测试工程，说明为什么" \
  | jq -c 'select(.type == "tool_execution_end") | {tool: .toolName, ms: .durationMs}'
```

`--mode json` 一行一条 JSON：先是 session 头，然后是 TUI 里渲染的同一套会话事件——消息增量、工具执行、重试、压缩。诊断信息走 stderr，stdout 保持可解析。

### 限制它能动什么

```bash
# -t 是替换默认工具集，这四个都改不了文件
pi -t read,grep,find,ls -p "审一遍 src/Pi.Agent，按风险排前三。"

# -xt 是在默认工具集上摘掉几个
pi -xt write,edit -p "讲讲 tools/ 下面那些文件是怎么产出的。"
```

### 接着上次、起名、导出

```bash
pi -c                                # 继续这个目录最近一次会话
pi --resume                          # 或者从列表里挑一个
pi --name "重构 auth" "先从会话状态机开始"
```

会话存在 `~/.pi/agent/sessions/` 下，按工作目录一个子目录。会话里 `/session` 看当前文件和 token 用量，`/export` 导出成 HTML 或 JSONL。

### 把你的规矩告诉它

三条路，都不用写代码：

| 想要 | 写在哪 |
|---|---|
| 某个仓库的长期约定 | 根目录 `AGENTS.md`（也认 `CLAUDE.md`、`AGENTS.override.md`） |
| 让别的进程提供工具 | `mcp.json`，或者 `pi mcp add` |
| 可复用的多步流程 | `skills/<name>/SKILL.md` |

```bash
pi mcp add filesystem -- npx -y @modelcontextprotocol/server-filesystem .
pi mcp list
```

```markdown
---
name: release-notes
description: 把 git log 整理成发布说明。要 changelog、release notes，或者问「上个 tag 之后改了什么」时使用。
---
```

文件放好之后，`/skill:release-notes v1.4.2` 可以手动点名；不点名时，任务和 description 对上了它自己会加载。

### 嵌进你自己的 .NET 应用

```csharp
using Pi.Ai.Types;
using Pi.CodingAgent.Core;

var result = await Sdk.CreateAgentSessionAsync(new CreateAgentSessionOptions
{
    Cwd = Directory.GetCurrentDirectory(),
    Tools = ["read", "grep", "find", "ls"],   // 不写就是用完整的默认工具集
});

var session = result.Session;
if (session.Model is null)
{
    Console.Error.WriteLine(result.ModelFallbackMessage);   // 没找到凭据
    return 1;
}

using var subscription = session.Subscribe(evt =>
{
    switch (evt)
    {
        case AgentSessionEvent.MessageUpdateEvent { AssistantMessageEvent: AssistantMessageEvent.TextDelta d }:
            Console.Write(d.Delta);
            break;
        case AgentSessionEvent.ToolExecutionStartEvent t:
            Console.Error.WriteLine($"\n[tool] {t.ToolName}");
            break;
    }
});

await session.PromptAsync("CLI 的入口在哪，它都装配了些什么？");

var stats = session.GetSessionStats();
Console.Error.WriteLine($"\n{stats.TotalMessages} 条消息 · {stats.ToolCalls} 次工具调用 · ${stats.Cost:0.0000}");
```

这不是 mock：会话会去读 `~/.pi/agent` 里的凭据与设置、加载项目的 `.pi/` 与 `AGENTS.md`、注册内置工具、把 transcript 写进会话目录——跟 CLI 跑的是同一套程序集。把 `Tools` 和 `Cwd` 固定死，就是发给团队的受控版。

### 能直接跑的工程

四个控制台工程用的是同一批 API，并且跟着 CI 一起编译，不会悄悄失效：

| 工程 | 演示什么 | 需要密钥 |
|---|---|---|
| [Pi.Samples.FauxAgent](samples/Pi.Samples.FauxAgent) | 完整 agent 循环：脚本化模型 + 真执行工具 + 事件流 | 不需要 |
| [Pi.Samples.RealModel](samples/Pi.Samples.RealModel) | 直连真实模型看流式，以及目录驱动的模型入口 | 需要 |
| [Pi.Samples.CodingSession](samples/Pi.Samples.CodingSession) | 上面那段 SDK，端到端，含事件与会话统计 | 需要 |
| [Pi.Samples.McpClient](samples/Pi.Samples.McpClient) | 独立 MCP 客户端：列工具、调一次、读资源 | 不需要（要 Node/npx） |

```bash
dotnet run --project samples/Pi.Samples.FauxAgent      # 不需要 API key
dotnet run --project samples/Pi.Samples.RealModel -- anthropic
dotnet run --project samples/Pi.Samples.CodingSession
dotnet run --project samples/Pi.Samples.McpClient
```

每个工程的细节见 [samples/README.md](samples/README.md)。全部选项与子命令在 [docs/usage/cli.md](https://github.com/AIDotNet/pi.net/blob/main/docs/usage/cli.md)，嵌入 API 的完整字段在 [docs/usage/sdk.md](https://github.com/AIDotNet/pi.net/blob/main/docs/usage/sdk.md)。

## 迁移状态

实施进度、逐阶段验收证据与已记录的 TypeScript/.NET 差异统一维护在 [docs/migration-execution-status.md](https://github.com/AIDotNet/pi.net/blob/main/docs/migration-execution-status.md)。

## 问题反馈

既然项目由 AI 生成和维护，你能给它最有价值的东西就是一份精确的失败报告。出现崩溃、结果不对、与上游 Pi 行为不一致，或者文档某句话已经和代码对不上时，请[提 issue](https://github.com/AIDotNet/pi.net/issues/new)。

最快被定位的报告通常包含：

- 包名与版本（`dotnet list package`，CLI 用 `pi --version`），以及 `dotnet --info` 的摘要；
- 最小复现——最好基于 `samples/Pi.Samples.FauxAgent` 改几行，它不需要 API key 就能跑完整循环；
- 你期望的结果 vs 实际结果；如果是在和上游对比，附上对应的 TypeScript 行为。

## 许可

MIT，见 [LICENSE](https://github.com/AIDotNet/pi.net/blob/main/LICENSE)。Pi.NET 是 Mario Zechner 的 [Pi](https://github.com/earendil-works/pi) 的移植版，上游版权与本项目的版权一并保留。
