# Pi.NET

[English](readme.md) | [简体中文](readme.zh-CN.md)

[![NuGet](https://img.shields.io/nuget/v/Pi.Ai?label=nuget)](https://www.nuget.org/packages/Pi.Ai)
[![CI](https://github.com/xuzeyu91/pi.net/actions/workflows/ci.yml/badge.svg)](https://github.com/xuzeyu91/pi.net/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/github/license/xuzeyu91/pi.net)](LICENSE)

Pi.NET 是 [Pi](https://github.com/earendil-works/pi) 的 .NET 10 移植版。Pi 是一个极简、可扩展的 Agent harness（TypeScript monorepo，约 17.1 万行），本项目把它的核心库逐一移植到 `net10.0`，让 .NET 生态用上同一套能力。

Pi 的理念是「让 Pi 适配你的工作流，而不是反过来」：库只做最小而完整的事，上层由你自己组合。本项目沿用这一理念——不是把 TS 代码翻成 C# 就完事，而是在语义等价的前提下用 C# 的方式重建（判别联合 → abstract record 模式匹配、JS Proxy → 显式动词 API）。

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
| **Pi.CodingAgent / Pi.Cli** | 交互式编码 Agent、工具、已编译 .NET 扩展、CLI、RPC 与实验性服务/worker runtime |
| **Pi.Vacation** | 基于 coding-agent runtime 的独立研究与规划宿主 |
| **Pi.DurableHost** | 基于 coding-agent runtime 的独立 durable 会话宿主（`pi-durable`），带 `subagent` 工具与按目录缓存的执行环境 |
| **Pi.Evals** | 原生 .NET host/documentation 评测工具；配对文档评测使用 Docker |

TypeScript monorepo 仍是行为参考；随包提供的上游 docs/examples 保留为 TypeScript 参考资料，C# 扩展 API 请看 [.NET 扩展指南](src/Pi.CodingAgent/Assets/docs/dotnet-extensions.md)。权威迁移阶段、验收证据与已记录的 TypeScript/.NET 差异见 [migration-execution-status.md](docs/migration-execution-status.md)。

## 快速开始

### 从 NuGet 安装

13 个库已发布到 nuget.org，包名与项目名一致，目标框架 `net10.0`：

```bash
dotnet add package Pi.Ai        # 多供应商 LLM 统一 API
dotnet add package Pi.Agent     # Agent 主循环
dotnet add package Pi.Mcp       # MCP 客户端
dotnet add package Pi.CodingAgent   # 整套依赖，含 CLI 运行时资源
```

包内容、发版流程和回退方式见 [docs/publishing.md](docs/publishing.md)。

### 从源码构建

需要 .NET 10 SDK。

```bash
dotnet build Pi.slnx --no-restore -m:1
dotnet run --project src/Pi.Cli/Pi.Cli.csproj -- --help
```

测试项目使用 xUnit v3 和 Microsoft.Testing.Platform。可直接运行一个测试项目：

```bash
dotnet run --project tests/Pi.Evals.Tests/Pi.Evals.Tests.csproj --no-restore
```

## 迁移状态

实施进度、逐阶段验收证据与已记录的 TypeScript/.NET 差异统一维护在 [docs/migration-execution-status.md](docs/migration-execution-status.md)。
