using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Cli;

// ============================================================================
// CLI 参数解析与帮助文本 — TS `cli/args.ts`（479 行）。
//
// `parseArgs` 是 main.ts（4g-2）的第一道工序：把 argv 解析成 `Args`，
// 带诊断（warning/error）而不抛异常——由调用方决定打印与退出。
//
// 移植要点（与 TS 逐条对齐，均有测试钉住）：
//   1. 取值型 flag（--provider/--model/--tools/…）的守卫是 `i + 1 < len`：
//      **尾缀无值时不下诊断，而是继续沿 else-if 链下落到「未知长 flag 捕获」**，
//      于是 `["--provider"]` 会记 unknownFlags{provider: true}。这是 TS 的真实行为。
//   2. `--print` 只吞「下一个参数既非 @ 开头、也非 - 开头（--- 开头除外）」的参数，
//      所以 YAML frontmatter（`---`）会被当作提示词，而 `--provider` 不会。
//   3. `--mode` / `--tui-mode` / `--use-theme` 的缺值与非法值是两条不同诊断；
//      非法值**消费掉**该参数，缺值不消费。
//   4. `--` 之后一切按位置参数处理：`@` 开头进 fileArgs，其余进 messages（含 - 开头）。
//   5. `--list-models` 的三态（未出现 / 无搜索词 / 带搜索词）在 C# 拆成
//      `ListModels` + `ListModelsSearch` 两个字段，对应 TS `listModels?: string | true`。
// ============================================================================

/// <summary>输出模式。对应 TS <c>Mode</c>（args.ts 导出的 <c>"text" | "json" | "rpc"</c>）。</summary>
public enum CliMode
{
    Text,
    Json,
    Rpc,
}

/// <summary><see cref="CliMode"/> 与 TS 文本字面量的互转。</summary>
public static class CliModeText
{
    public static string ToText(CliMode mode) => mode switch
    {
        CliMode.Text => "text",
        CliMode.Json => "json",
        _ => "rpc",
    };

    /// <summary>字面量 → 枚举；未知文本返回 <c>null</c>（对齐 TS 的联合收窄失败）。</summary>
    public static CliMode? Parse(string? text) => text switch
    {
        "text" => CliMode.Text,
        "json" => CliMode.Json,
        "rpc" => CliMode.Rpc,
        _ => null,
    };
}

/// <summary>
/// 解析结果。对应 TS <c>Args</c> 接口——TS 是可变的普通对象，端口同样用可变属性，
/// <see cref="CliArgs.Parse"/> 按 TS 的分支顺序逐项填充。
/// </summary>
public sealed class Args
{
    public string? Provider { get; set; }

    public string? Model { get; set; }

    public string? ApiKey { get; set; }

    public string? SystemPrompt { get; set; }

    /// <summary>对应 TS <c>appendSystemPrompt</c>（可多次，保持顺序）。</summary>
    public List<string>? AppendSystemPrompt { get; set; }

    public ThinkingLevel? Thinking { get; set; }

    /// <summary>对应 TS <c>continue</c>（C# 保留字，属性名首字母大写后无冲突）。</summary>
    public bool? Continue { get; set; }

    public bool? Resume { get; set; }

    public bool? Help { get; set; }

    public bool? Version { get; set; }

    public CliMode? Mode { get; set; }

    public string? Name { get; set; }

    public bool? NoSession { get; set; }

    public string? Session { get; set; }

    public string? SessionId { get; set; }

    public string? Fork { get; set; }

    public string? SessionDir { get; set; }

    public List<string>? Models { get; set; }

    public List<string>? Tools { get; set; }

    public List<string>? ExcludeTools { get; set; }

    public bool? NoTools { get; set; }

    public bool? NoBuiltinTools { get; set; }

    public List<string>? Extensions { get; set; }

    public bool? NoExtensions { get; set; }

    public bool? NoMcp { get; set; }

    public bool? Print { get; set; }

    public string? Export { get; set; }

    public bool? NoSkills { get; set; }

    public List<string>? Skills { get; set; }

    public List<string>? PromptTemplates { get; set; }

    public bool? NoPromptTemplates { get; set; }

    public List<string>? Themes { get; set; }

    public string? UseTheme { get; set; }

    public bool? NoThemes { get; set; }

    public bool? NoContextFiles { get; set; }

    /// <summary>对应 TS <c>listModels?: string | true</c> 的「是否出现」。</summary>
    public bool? ListModels { get; set; }

    /// <summary>
    /// 对应 TS <c>listModels?: string | true</c> 的 string 分支：非 <c>null</c> 即带了搜索词
    /// （含空串——TS <c>--list-models ""</c> 记的是 <c>""</c>，main.ts 以
    /// <c>typeof === "string"</c> 取搜索词）。未带搜索词时为 <c>null</c>。
    /// </summary>
    public string? ListModelsSearch { get; set; }

    public bool? Offline { get; set; }

    public TuiMode? TuiMode { get; set; }

    public bool? Verbose { get; set; }

    /// <summary>对应 TS <c>projectTrustOverride</c>：<c>--approve</c> 置 true、<c>--no-approve</c> 置 false。</summary>
    public bool? ProjectTrustOverride { get; set; }

    public List<string> Messages { get; } = [];

    public List<string> FileArgs { get; } = [];

    /// <summary>
    /// 未知长 flag（可能是扩展注册的）。对应 TS <c>Map&lt;string, boolean | string&gt;</c>：
    /// 值为装箱的 <see cref="bool"/> <c>true</c> 或 <see cref="string"/>；同名重复出现后者覆盖前者
    /// （TS <c>Map.set</c> 同）。
    /// </summary>
    public Dictionary<string, object> UnknownFlags { get; } = [];

    /// <summary>
    /// 解析期诊断。对应 TS <c>diagnostics</c> 的 <c>{ type: "warning" | "error"; message: string }</c>；
    /// main.ts 把这份列表与运行时诊断合并进同一个 <c>AgentSessionRuntimeDiagnostic[]</c>，
    /// 端口直接复用该记录类型（结构完全一致）。
    /// </summary>
    public List<AgentSessionRuntimeDiagnostic> Diagnostics { get; } = [];
}

/// <summary>args.ts 的模块级函数。</summary>
public static class CliArgs
{
    /// <summary>
    /// 合法思考档位表。对应 TS <c>VALID_THINKING_LEVELS</c>。
    /// 顺序即 <c>--thinking</c> 警告文案里的列举顺序。
    /// </summary>
    /// <remarks>
    /// 表驱动常量：CliArgsTests 有一条对拍断言把它逐项映射回
    /// <see cref="ThinkingLevels.ToText"/> 的枚举全集，防止表与枚举漂移。
    /// </remarks>
    public static readonly string[] ValidThinkingLevels =
        ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>对应 TS <c>isValidThinkingLevel</c>（model-resolver 也用它）。</summary>
    public static bool IsValidThinkingLevel(string level) => ThinkingLevels.Parse(level) is not null;

    /// <summary>对应 TS <c>normalizeSessionName</c>：trim 后为空串返回 <c>null</c>（TS 的 undefined）。</summary>
    public static string? NormalizeSessionName(string value)
    {
        var name = value.Trim();
        return name.Length > 0 ? name : null;
    }

    // ------------------------------------------------------------------ parse

    /// <summary>
    /// 解析 argv。对应 TS <c>parseArgs</c>——分支顺序、守卫条件、诊断文案逐条对齐，
    /// 包括「取值型 flag 尾缀无值时下落到未知 flag 捕获」这一 TS 真实行为。
    /// </summary>
    public static Args Parse(IReadOnlyList<string> args)
    {
        var result = new Args();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (arg == "--")
            {
                for (var j = i + 1; j < args.Count; j++)
                {
                    var positionalArg = args[j];
                    if (positionalArg.StartsWith('@'))
                    {
                        result.FileArgs.Add(positionalArg[1..]);
                    }
                    else
                    {
                        result.Messages.Add(positionalArg);
                    }
                }

                break;
            }
            else if (arg is "--help" or "-h")
            {
                result.Help = true;
            }
            else if (arg is "--version" or "-v")
            {
                result.Version = true;
            }
            else if (arg == "--mode")
            {
                var mode = Next(args, i);
                if (mode is null || mode.StartsWith('-'))
                {
                    result.Diagnostics.Add(Error("--mode requires text, json, or rpc"));
                    continue;
                }

                i++;
                var parsedMode = CliModeText.Parse(mode);
                if (parsedMode is null)
                {
                    result.Diagnostics.Add(Error($"Invalid mode \"{mode}\". Valid values: text, json, rpc"));
                    continue;
                }

                result.Mode = parsedMode;
            }
            else if (arg is "--continue" or "-c")
            {
                result.Continue = true;
            }
            else if (arg is "--resume" or "-r")
            {
                result.Resume = true;
            }
            else if (arg == "--provider" && i + 1 < args.Count)
            {
                result.Provider = args[++i];
            }
            else if (arg == "--model" && i + 1 < args.Count)
            {
                result.Model = args[++i];
            }
            else if (arg == "--api-key" && i + 1 < args.Count)
            {
                result.ApiKey = args[++i];
            }
            else if (arg == "--system-prompt" && i + 1 < args.Count)
            {
                result.SystemPrompt = args[++i];
            }
            else if (arg == "--append-system-prompt" && i + 1 < args.Count)
            {
                result.AppendSystemPrompt ??= [];
                result.AppendSystemPrompt.Add(args[++i]);
            }
            else if (arg is "--name" or "-n")
            {
                if (i + 1 < args.Count)
                {
                    result.Name = args[++i];
                }
                else
                {
                    result.Diagnostics.Add(Error("--name requires a value"));
                }
            }
            else if (arg == "--no-session")
            {
                result.NoSession = true;
            }
            else if (arg == "--session" && i + 1 < args.Count)
            {
                result.Session = args[++i];
            }
            else if (arg == "--session-id" && i + 1 < args.Count)
            {
                result.SessionId = args[++i];
            }
            else if (arg == "--fork" && i + 1 < args.Count)
            {
                result.Fork = args[++i];
            }
            else if (arg == "--session-dir" && i + 1 < args.Count)
            {
                result.SessionDir = args[++i];
            }
            else if (arg == "--models" && i + 1 < args.Count)
            {
                result.Models = SplitList(args[++i]);
            }
            else if (arg is "--no-tools" or "-nt")
            {
                result.NoTools = true;
            }
            else if (arg is "--no-builtin-tools" or "-nbt")
            {
                result.NoBuiltinTools = true;
            }
            else if ((arg is "--tools" or "-t") && i + 1 < args.Count)
            {
                var tools = SplitList(args[++i]);
                var error = SettingsManager.GetToolListError(tools);
                if (error is not null)
                {
                    result.Diagnostics.Add(Error($"{arg}: {error}"));
                }
                else
                {
                    result.Tools = tools;
                }
            }
            else if ((arg is "--exclude-tools" or "-xt") && i + 1 < args.Count)
            {
                result.ExcludeTools = SplitList(args[++i]);
            }
            else if (arg == "--thinking" && i + 1 < args.Count)
            {
                var level = args[++i];
                var parsedLevel = ThinkingLevels.Parse(level);
                if (parsedLevel is not null)
                {
                    result.Thinking = parsedLevel;
                }
                else
                {
                    result.Diagnostics.Add(Warning(
                        $"Invalid thinking level \"{level}\". Valid values: {string.Join(", ", ValidThinkingLevels)}"));
                }
            }
            else if (arg is "--print" or "-p")
            {
                result.Print = true;
                var next = Next(args, i);
                if (next is not null && !next.StartsWith('@') && (!next.StartsWith('-') || next.StartsWith("---")))
                {
                    result.Messages.Add(next);
                    i++;
                }
            }
            else if (arg == "--export" && i + 1 < args.Count)
            {
                result.Export = args[++i];
            }
            else if ((arg is "--extension" or "-e") && i + 1 < args.Count)
            {
                result.Extensions ??= [];
                result.Extensions.Add(args[++i]);
            }
            else if (arg is "--no-extensions" or "-ne")
            {
                result.NoExtensions = true;
            }
            else if (arg == "--no-mcp")
            {
                result.NoMcp = true;
            }
            else if (arg == "--skill" && i + 1 < args.Count)
            {
                result.Skills ??= [];
                result.Skills.Add(args[++i]);
            }
            else if (arg == "--prompt-template" && i + 1 < args.Count)
            {
                result.PromptTemplates ??= [];
                result.PromptTemplates.Add(args[++i]);
            }
            else if (arg == "--theme" && i + 1 < args.Count)
            {
                result.Themes ??= [];
                result.Themes.Add(args[++i]);
            }
            else if (arg == "--use-theme")
            {
                var themeName = Next(args, i);
                if (themeName is null || themeName.StartsWith('-'))
                {
                    result.Diagnostics.Add(Error("--use-theme requires a theme name"));
                }
                else
                {
                    result.UseTheme = themeName;
                    i++;
                }
            }
            else if (arg is "--no-skills" or "-ns")
            {
                result.NoSkills = true;
            }
            else if (arg is "--no-prompt-templates" or "-np")
            {
                result.NoPromptTemplates = true;
            }
            else if (arg == "--no-themes")
            {
                result.NoThemes = true;
            }
            else if (arg is "--no-context-files" or "-nc")
            {
                result.NoContextFiles = true;
            }
            else if (arg == "--list-models")
            {
                result.ListModels = true;
                // Check if next arg is a search pattern (not a flag or file arg)
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('@'))
                {
                    result.ListModelsSearch = args[++i];
                }
            }
            else if (arg == "--tui-mode")
            {
                var mode = Next(args, i);
                if (mode is "regular" or "fullscreen")
                {
                    result.TuiMode = mode == "regular" ? TuiMode.Regular : TuiMode.Fullscreen;
                    i++;
                }
                else if (mode is null || mode.StartsWith('-'))
                {
                    result.Diagnostics.Add(Error("--tui-mode requires regular or fullscreen"));
                }
                else
                {
                    i++;
                    result.Diagnostics.Add(Error($"Invalid TUI mode \"{mode}\". Valid values: regular, fullscreen"));
                }
            }
            else if (arg == "--verbose")
            {
                result.Verbose = true;
            }
            else if (arg is "--approve" or "-a")
            {
                result.ProjectTrustOverride = true;
            }
            else if (arg is "--no-approve" or "-na")
            {
                result.ProjectTrustOverride = false;
            }
            else if (arg == "--offline")
            {
                result.Offline = true;
            }
            else if (arg.StartsWith('@'))
            {
                result.FileArgs.Add(arg[1..]); // Remove @ prefix
            }
            else if (arg.StartsWith("--"))
            {
                var eqIndex = arg.IndexOf('=');
                if (eqIndex != -1)
                {
                    result.UnknownFlags[arg[2..eqIndex]] = arg[(eqIndex + 1)..];
                }
                else
                {
                    var flagName = arg[2..];
                    var next = Next(args, i);
                    if (next is not null && !next.StartsWith('-') && !next.StartsWith('@'))
                    {
                        result.UnknownFlags[flagName] = next;
                        i++;
                    }
                    else
                    {
                        result.UnknownFlags[flagName] = true;
                    }
                }
            }
            else if (arg.StartsWith('-') && !arg.StartsWith("--"))
            {
                result.Diagnostics.Add(Error($"Unknown option: {arg}"));
            }
            else if (!arg.StartsWith("-"))
            {
                result.Messages.Add(arg);
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ help

    /// <summary>对应 TS <c>printHelp</c>：整段帮助文本（含扩展 flag 段）走 stdout。</summary>
    public static void PrintHelp(IReadOnlyList<ExtensionFlag>? extensionFlags = null)
    {
        var extensionFlagsText = extensionFlags is { Count: > 0 } flags
            ? $"\n{Chalk.Bold("Extension CLI Flags:")}\n{string.Join("\n", flags.Select(flag =>
            {
                var value = flag.Type == "string" ? " <value>" : "";
                var description = flag.Description ?? $"Registered by {flag.ExtensionPath}";
                return $"  --{flag.Name}{value}".PadRight(30) + description;
            }))}\n"
            : "";

        Console.WriteLine($$"""
            {{Chalk.Bold(Config.AppName)}} - AI coding assistant with read, bash, edit, write tools

            {{Chalk.Bold("Usage:")}}
              {{Config.AppName}} [options] [--] [@files...] [messages...]

            {{Chalk.Bold("Commands:")}}
              {{Config.AppName}} install <source> [-l]     Install extension source and add to settings
              {{Config.AppName}} remove <source> [-l]      Remove extension source from settings
              {{Config.AppName}} uninstall <source> [-l]   Alias for remove
              {{Config.AppName}} update [source|self|pi]   Update pi, extensions, or model catalogs
              {{Config.AppName}} list                      List installed extensions from settings
              {{Config.AppName}} config [-l]               Open TUI to enable/disable package resources (Tab switches scope)
              {{Config.AppName}} auth <command>            Print credentials or check provider readiness
              {{Config.AppName}} mcp <command>             Check MCP servers, sign in to or out of OAuth servers
              {{Config.AppName}} doctor [--json]           Diagnose local configuration, tools, and credential sources offline
              {{Config.AppName}} <command> --help          Show help for install/remove/uninstall/update/list/config/auth/mcp/doctor

            {{Chalk.Bold("Options:")}}
              --provider <name>              Provider to search for --model (requires --model)
              --model <pattern>              Model pattern or ID (supports "provider/id" and optional ":<thinking>")
              --api-key <key>                API key (defaults to env vars)
              --system-prompt <text>         System prompt (default: coding assistant prompt)
              --append-system-prompt <text>  Append text or file contents to the system prompt (can be used multiple times)
              --mode <mode>                  Output mode: text (default), json, or rpc
              --print, -p                    Non-interactive mode: process prompt and exit
              --continue, -c                 Continue previous session
              --resume, -r                   Select a session to resume
              --session <path|id>            Use specific session file or partial UUID
              --session-id <id>              Use exact project session ID, creating it if missing
              --fork <path|id>               Fork specific session file or partial UUID into a new session
              --session-dir <dir>            Directory for session storage and lookup
              --no-session                   Don't save session (ephemeral)
              --name, -n <name>              Set session display name
              --models <patterns>            Comma-separated model patterns for Ctrl+P cycling
                                                 Supports globs (anthropic/*, *sonnet*) and fuzzy matching
              --no-tools, -nt                Disable all tools by default (built-in and extension)
              --no-builtin-tools, -nbt       Disable built-in tools by default but keep extension/custom tools enabled
              --tools, -t <tools>            Comma-separated allowlist of tool names or patterns (*) to enable
                                                 Keeps MCP tools unless an entry starts with mcp__
                                                 Only +name/-name entries add to or remove from the defaults
              --exclude-tools, -xt <tools>   Comma-separated denylist of tool names or patterns (*) to disable
                                                 Applies to all tools, MCP tools included
              --thinking <level>             Set thinking level: off, minimal, low, medium, high, xhigh, max
              --extension, -e <path>         Load an extension file or builtin:<name> (can be used multiple times)
              --no-extensions, -ne           Disable extension discovery and built-in extensions (explicit -e paths still work)
              --no-mcp                       Disable built-in MCP support: no servers connect and no MCP tools
              --skill <path>                 Load a skill file or directory (can be used multiple times)
              --no-skills, -ns               Disable skills discovery and loading
              --prompt-template <path>       Load a prompt template file or directory (can be used multiple times)
              --no-prompt-templates, -np     Disable prompt template discovery and loading
              --theme <path>                 Load a theme file or directory (can be used multiple times)
              --use-theme <name[/name]>      Set the initial interactive theme for this run
              --no-themes                    Disable theme discovery and loading
              --no-context-files, -nc        Disable AGENTS.md and CLAUDE.md discovery and loading
              --export <file>                Export session file to HTML and exit
              --list-models [search]         List available models (fuzzy search by provider, ID, or name)
              --verbose                      Force verbose startup (overrides quietStartup setting)
              --tui-mode <mode>              TUI mode: fullscreen (default) or regular
              --approve, -a                  Trust project-local files for this run
              --no-approve, -na              Ignore project-local files for this run
              --offline                      Disable startup network operations (same as PI_OFFLINE=1)
              --                             End option parsing; treat remaining arguments as messages/files
              --help, -h                     Show this help
              --version, -v                  Show version number

            Extensions can register additional flags (e.g., --plan from plan-mode extension).{{extensionFlagsText}}

            {{Chalk.Bold("Examples:")}}
              # Print a provider API key for an external client
              {{Config.AppName}} auth print-api-key --provider openai

              # Print an OAuth bearer token for an external client (refreshes if expired)
              {{Config.AppName}} auth print-bearer-token --provider openai-codex

              # Interactive mode
              {{Config.AppName}}

              # Interactive mode with initial prompt
              {{Config.AppName}} "List all .ts files in src/"

              # Include files in initial message
              {{Config.AppName}} @prompt.md @image.png "What color is the sky?"

              # Non-interactive mode (process and exit)
              {{Config.AppName}} -p "List all .ts files in src/"

              # Prompt beginning with a dash
              {{Config.AppName}} -p -- "- Summarize these points"

              # Multiple messages (interactive)
              {{Config.AppName}} "Read package.json" "What dependencies do we have?"

              # Continue previous session
              {{Config.AppName}} --continue "What did we discuss?"

              # Start a named session
              {{Config.AppName}} --name "Refactor auth module"

              # Use different model
              {{Config.AppName}} --provider openai --model gpt-4o-mini "Help me refactor this code"

              # Use model with provider prefix (no --provider needed)
              {{Config.AppName}} --model openai/gpt-4o "Help me refactor this code"

              # Use model with thinking level shorthand
              {{Config.AppName}} --model sonnet:high "Solve this complex problem"

              # Limit model cycling to specific models
              {{Config.AppName}} --models claude-sonnet,claude-haiku,gpt-4o

              # Limit to a specific provider with glob pattern
              {{Config.AppName}} --models "github-copilot/*"

              # Cycle models with fixed thinking levels
              {{Config.AppName}} --models sonnet:high,haiku:low

              # Start with a specific thinking level
              {{Config.AppName}} --thinking high "Solve this complex problem"

              # Read-only mode (no file modifications possible)
              {{Config.AppName}} --tools read,grep,find,ls -p "Review the code in src/"

              # Add codemode to the default tools
              {{Config.AppName}} --tools +codemode

              # Codemode with only the tools of one MCP server
              {{Config.AppName}} --tools read,bash,codemode,'mcp__radius__*'

              # Disable one tool while keeping the rest available
              {{Config.AppName}} --exclude-tools ask_question

              # Export a session file to HTML
              {{Config.AppName}} --export ~/{{Config.ConfigDirName}}/agent/sessions/--path--/session.jsonl
              {{Config.AppName}} --export session.jsonl output.html

            {{Chalk.Bold("Environment Variables:")}}
              ANTHROPIC_AUTH_TOKEN             - Anthropic bearer auth token
              ANTHROPIC_API_KEY                - Anthropic Claude API key
              ANTHROPIC_OAUTH_TOKEN            - Anthropic OAuth token (alternative to API key)
              ANT_LING_API_KEY                 - Ant Ling API key
              OPENAI_API_KEY                   - OpenAI GPT API key
              AZURE_OPENAI_API_KEY             - Azure OpenAI API key
              AZURE_OPENAI_BASE_URL            - Azure OpenAI/Cognitive Services base URL (e.g. https://{resource}.openai.azure.com)
              AZURE_OPENAI_RESOURCE_NAME       - Azure OpenAI resource name (alternative to base URL)
              AZURE_OPENAI_API_VERSION         - Azure OpenAI API version (default: v1)
              AZURE_OPENAI_DEPLOYMENT_NAME_MAP - Azure OpenAI model=deployment map (comma-separated)
              DEEPSEEK_API_KEY                 - DeepSeek API key
              NVIDIA_API_KEY                   - NVIDIA NIM API key
              GEMINI_API_KEY                   - Google Gemini API key
              GROQ_API_KEY                      - Groq API key
              CEREBRAS_API_KEY                 - Cerebras API key
              XAI_API_KEY                      - xAI Grok API key
              FIREWORKS_API_KEY                - Fireworks AI API key
              TOGETHER_API_KEY                 - Together AI API key
              BASETEN_API_KEY                  - Baseten API key
              OPENROUTER_API_KEY               - OpenRouter API key
              AI_GATEWAY_API_KEY               - Vercel AI Gateway API key
              ZAI_API_KEY                      - ZAI Coding Plan API key (Global)
              ZAI_CODING_CN_API_KEY            - ZAI Coding Plan API key (China)
              MISTRAL_API_KEY                  - Mistral API key
              MINIMAX_API_KEY                  - MiniMax API key
              MOONSHOT_API_KEY                 - Moonshot AI API key
              OPENCODE_API_KEY                 - OpenCode Zen/OpenCode Go API key
              KIMI_API_KEY                     - Kimi For Coding API key
              META_API_KEY                     - Meta Model API key
              CLOUDFLARE_API_KEY               - Cloudflare API token (Workers AI and AI Gateway)
              CLOUDFLARE_ACCOUNT_ID            - Cloudflare account id (required for both)
              CLOUDFLARE_GATEWAY_ID            - Cloudflare AI Gateway slug (required for AI Gateway)
              QWEN_TOKEN_PLAN_API_KEY          - Qwen Token Plan API key (international region)
              QWEN_TOKEN_PLAN_CN_API_KEY       - Qwen Token Plan API key (China region)
              XIAOMI_API_KEY                   - Xiaomi MiMo API key (api.xiaomimimo.com billing)
              XIAOMI_TOKEN_PLAN_CN_API_KEY     - Xiaomi MiMo Token Plan API key (China region)
              XIAOMI_TOKEN_PLAN_AMS_API_KEY    - Xiaomi MiMo Token Plan API key (Amsterdam region)
              XIAOMI_TOKEN_PLAN_SGP_API_KEY    - Xiaomi MiMo Token Plan API key (Singapore region)
              AWS_PROFILE                      - AWS profile for Amazon Bedrock
              AWS_ACCESS_KEY_ID                - AWS access key for Amazon Bedrock
              AWS_SECRET_ACCESS_KEY            - AWS secret key for Amazon Bedrock
              AWS_BEARER_TOKEN_BEDROCK         - Bedrock API key (bearer token)
              AWS_REGION                       - AWS region for Amazon Bedrock (e.g., us-east-1)
              {{Config.EnvAgentDir.PadRight(32)}} - Config directory (default: ~/{{Config.ConfigDirName}}/agent)
              {{Config.EnvSessionDir.PadRight(32)}} - Session storage directory (overridden by --session-dir)
              PI_PACKAGE_DIR                   - Override package directory (for Nix/Guix store paths)
              PI_OFFLINE                       - Disable startup network operations when set to 1/true/yes
              PI_TELEMETRY                     - Override install telemetry when set to 1/true/yes or 0/false/no
              PI_SHARE_VIEWER_URL              - Base URL for /share command (default: https://pi.dev/session/)

            {{Chalk.Bold("Built-in Tool Names:")}}
              read       - Read file contents
              bash       - Execute bash commands
              powershell - Execute PowerShell commands on Windows
              edit       - Edit files with find/replace
              write      - Write files (creates/overwrites)
              grep       - Search file contents (read-only, off by default)
              find       - Find files by glob pattern (read-only, off by default)
              ls         - List directory contents (read-only, off by default)
            """);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>TS 的 <c>args[i + 1]</c>（越界为 undefined，此处为 <c>null</c>）。</summary>
    private static string? Next(IReadOnlyList<string> args, int i) => i + 1 < args.Count ? args[i + 1] : null;

    /// <summary>TS 的 <c>value.split(",").map(s =&gt; s.trim()).filter(s =&gt; s.length &gt; 0)</c>。</summary>
    private static List<string> SplitList(string value)
        => value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private static AgentSessionRuntimeDiagnostic Error(string message)
        => new() { Type = AgentSessionDiagnosticType.Error, Message = message };

    private static AgentSessionRuntimeDiagnostic Warning(string message)
        => new() { Type = AgentSessionDiagnosticType.Warning, Message = message };
}
