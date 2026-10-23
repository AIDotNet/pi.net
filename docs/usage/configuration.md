# 配置：目录、settings、models、mcp、信任

端口有两层配置：**全局**在 `~/.pi/agent/`（`PI_CODING_AGENT_DIR` 可覆盖），
**项目本地**在 `<cwd>/.pi/`（`Config.ConfigDirName`）。本文列每个文件的用途与读取方；
`models.json` 的字段级参考见 [models-configuration.md](models-configuration.md)。

## 目录一览

| 路径 | 用途 | 读取方 |
|---|---|---|
| `~/.pi/agent/settings.json` | 全局设置 | `SettingsManager` |
| `<cwd>/.pi/settings.json` | 项目设置，覆盖全局同名字段 | 同上（`SettingsManager.cs:47`） |
| `~/.pi/agent/models.json` | 自定义 provider / model / 覆盖 | `ModelConfig` |
| `~/.pi/agent/auth.json` | `pi login` 写入的凭据 | 模型运行时 |
| `~/.pi/agent/mcp.json` | 全局 MCP 服务器 | MCP 扩展 |
| `<cwd>/.pi/mcp.json` | 项目 MCP 服务器，同名条目替换全局 | 同上 |
| `~/.pi/agent/sessions/` | 会话存档（按项目分子目录） | `SessionManager` |
| `~/.pi/agent/themes/`、`prompts/`、`skills/`、`tools/`、`bin/` | 主题 / 提示词模板 / 技能 / 工具脚本 / 包管理器 | 资源加载与包管理 |
| `<cwd>/.pi/skills/`、`<cwd>/AGENTS.md`、`CLAUDE.md` | 项目本地资源与上下文文件 | `ResourceLoader.cs:909` |

项目本地的 `settings.json`、`mcp.json`、skills 与上下文文件属于**需要信任**的文件：
交互模式首次会询问，`--approve, -a` 本次直接信任，`--no-approve, -na` 本次忽略
（判定逻辑在 `Core/TrustManager.cs`）。SDK 场景里信任由你自己决定——这些文件只在
`DefaultResourceLoader` 被允许读取时才生效。

## settings.json

完整字段清单以 `src/Pi.CodingAgent/Assets/schemas/settings.schema.json` 为准（字段约 60 个）。
常用的几组：

| 字段 | 用途 |
|---|---|
| `defaultProvider` / `defaultModel` / `defaultThinkingLevel` / `modelThinkingLevels` | 缺省模型与档位 |
| `compaction` / `branchSummary` | 压缩与分支摘要策略（见 [sessions.md](sessions.md)） |
| `retry` | provider 失败重试策略 |
| `codemode` | codemode 呈现方式，见 [codemode.md](codemode.md) |
| `defaultTools` / `enabledModels` | 默认工具集与可用模型 |
| `sessionDir` / `sessionShareDestination` | 会话目录与分享去向 |
| `theme` / `terminal` / `tuiMode` / `images` | 外观与终端行为 |
| `steeringMode` / `followUpMode` / `queueMode` | 消息排队行为 |
| `packages` / `extensions` / `skills` / `prompts` / `themes` | 已安装资源清单 |
| `quietStartup` / `collapseChangelog` / `warnings` | 启动输出 |

编辑校验：在 settings.json 里加
`"$schema": "https://pi.dev/schemas/settings.schema.json"`（资产随包发布在
`src/Pi.CodingAgent/Assets/schemas/`）。

## models.json 与 auth.json

- `auth.json` 只存凭据（`pi login` / `pi auth` 维护），不要手编。
- `models.json` 存自定义端点与模型；给编辑器加 `"$schema"` 引用可获得补全。
  字段级参考见 [models-configuration.md](models-configuration.md)。

## mcp.json

全局 `~/.pi/agent/mcp.json` 与项目 `<cwd>/.pi/mcp.json` 的形状都是 TS 的
`mcpServers` 条目；项目同名条目替换全局条目（`Extensions/Mcp/Config.cs:5`）。
子命令 `pi mcp check` / `pi mcp <server>` 可以验证配置与发起 OAuth 登录，
不必进交互模式。
