# CLI 参考

`pi` 可执行文件由 `Pi.Cli` 提供，真正的参数解析在 `Pi.CodingAgent/Cli/Args.cs`，
帮助文本与本文同步（源码见 `Args.PrintHelp`）。本文按端口实际实现写，不是上游文档的翻译。

## 用法

```bash
pi [options] [--] [@files...] [messages...]
```

`--` 之后的一切按普通参数处理；`@文件` 会把文件内容附进首条消息。

## 子命令

| 命令 | 作用 |
|---|---|
| `pi install <source> [-l]` | 安装扩展源并写入 settings |
| `pi remove <source> [-l]` / `pi uninstall` | 从 settings 移除扩展源 |
| `pi update [source\|self\|pi]` | 更新 pi、扩展或模型目录 |
| `pi list` | 列出已安装扩展 |
| `pi config [-l]` | TUI 里开关包资源（Tab 切换作用域） |
| `pi auth <command>` | 打印凭据、检查 provider 可用性（`auth print-api-key` / `auth print-bearer-token`） |
| `pi mcp <command>` | 检查 MCP 服务器、OAuth 登录/登出 |
| `pi doctor [--json]` | 离线检查本地配置、Shell、搜索工具和凭据来源 |
| `pi <command> --help` | 查看子命令帮助 |

## 本地诊断

`pi doctor` 在会话与扩展初始化之前运行，适合排查首次启动或配置加载问题：

```powershell
pi doctor
pi doctor --json | ConvertFrom-Json
pi doctor --help
```

检查包括运行时版本/平台、全局配置目录、settings/models/auth/trust 文件、默认工具、
Bash/PowerShell 与 rg/fd 的定位结果，以及保存的凭据类型和已设置的已知认证环境变量名。
项目设置仅在已有保存的信任决定时加载；缺省配置文件属于正常情况。

诊断不联网、不加载用户扩展、不写配置、不自动下载工具，也不执行取密命令或刷新 OAuth。
找到 Shell 路径不代表已执行验证；凭据来源存在不代表认证有效。本地端点、ambient 认证和
扩展可能无需已知 API key，因此未发现凭据来源仅报告警告。MCP 连通性仍使用 `pi mcp check`。

文本和 JSON 使用相同的 `ok` / `warning` / `error` / `skipped` 状态。
JSON 是带 `schemaVersion: 1`、`version` 和 `checks` 数组的单个对象；每项包含 `id`、`status`、
`message`，以及可选的 `path` / `suggestion`，不输出凭据值或原始配置内容。
退出码：`0` 为无错误（允许警告），`1` 为存在诊断错误，`2` 为参数错误。

## 常用选项

会话类：

| 选项 | 作用 |
|---|---|
| `--print, -p` | 非交互：跑完提示词就退出 |
| `--continue, -c` | 继续上一个会话 |
| `--resume, -r` | 选一个会话恢复 |
| `--session <path\|id>` | 指定会话文件或部分 UUID |
| `--session-id <id>` | 用精确会话 ID，没有就建 |
| `--fork <path\|id>` | 把某个会话 fork 成新会话 |
| `--session-dir <dir>` | 会话存储与查找目录 |
| `--no-session` | 不落盘（等价于传入 `SessionManager.InMemory`，见 `Cli/Main.cs`） |
| `--name, -n <name>` | 会话显示名 |

模型类：

| 选项 | 作用 |
|---|---|
| `--model <pattern>` | 模型模式或 ID，支持 `provider/id` 和 `:<thinking>` 后缀 |
| `--provider <name>` | 配合 `--model` 限定搜索的 provider |
| `--api-key <key>` | 显式 API key（缺省走环境变量发现） |
| `--models <patterns>` | Ctrl+P 轮换的模型组，支持 glob 与模糊匹配 |
| `--thinking <level>` | off / minimal / low / medium / high / xhigh / max |
| `--list-models [search]` | 列出可用模型 |

工具与扩展类：

| 选项 | 作用 |
|---|---|
| `--tools, -t <tools>` | 工具 allowlist，支持 `*` 模式；`+name/-name` 在默认集上增删 |
| `--exclude-tools, -xt <tools>` | 工具 denylist，对 MCP 工具也生效 |
| `--no-tools, -nt` | 默认禁用全部工具 |
| `--no-builtin-tools, -nbt` | 只禁内置工具，保留扩展/自定义工具 |
| `--extension, -e <path>` | 加载扩展文件或 `builtin:<name>`（可重复） |
| `--no-extensions, -ne` | 关闭扩展发现（显式 `-e` 仍生效） |
| `--no-mcp` | 不连 MCP 服务器 |
| `--skill <path>` / `--no-skills, -ns` | 加载 / 关闭技能 |
| `--prompt-template <path>` / `--no-prompt-templates, -np` | 加载 / 关闭提示词模板 |
| `--theme <path>` / `--use-theme <name>` / `--no-themes` | 主题 |

其余：

| 选项 | 作用 |
|---|---|
| `--mode <mode>` | 输出模式：text（默认）/ json / rpc |
| `--system-prompt <text>` / `--append-system-prompt <text>` | 覆盖 / 追加系统提示词 |
| `--export <file>` | 把会话文件导出成 HTML 后退出 |
| `--verbose` | 强制详细启动（覆盖 `quietStartup` 设置） |
| `--tui-mode <mode>` | fullscreen（默认）或 regular |
| `--approve, -a` / `--no-approve, -na` | 本次运行信任 / 忽略项目本地文件 |
| `--offline` | 关闭启动期网络操作（同 `PI_OFFLINE=1`） |
| `--help, -h` / `--version, -v` | 帮助 / 版本 |

扩展可以注册自己的 flag（例如 plan-mode 扩展的 `--plan`），出现在帮助末尾的
「Extension CLI Flags」段。

## 环境变量（端口实际读取的）

| 变量 | 作用 |
|---|---|
| `PI_CODING_AGENT_DIR` | 覆盖全局配置目录（缺省 `~/.pi/agent`，见 `Config.GetAgentDir`） |
| `PI_CODING_AGENT_SESSION_DIR` | 覆盖会话目录 |
| `PI_OFFLINE` | 关闭启动期网络操作 |
| `PI_PACKAGE_DIR` | 覆盖扩展包安装目录 |
| `PI_SHARE_VIEWER_URL` | 会话分享页的查看地址 |

provider 凭据走标准变量（`ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `DEEPSEEK_API_KEY` /
`GEMINI_API_KEY` …），映射表在 `Pi.Ai.EnvApiKeys.ProviderEnvMap`。
