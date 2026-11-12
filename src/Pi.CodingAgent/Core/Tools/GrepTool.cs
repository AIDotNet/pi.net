using System.Text;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>Port of the TS <c>GrepToolDetails</c>.</summary>
public sealed record GrepToolDetails
{
    public Truncate.TruncationResult? Truncation { get; init; }

    public int? MatchLimitReached { get; init; }

    public bool? LinesTruncated { get; init; }
}

/// <summary>
/// Pluggable operations for the grep tool. Override these to delegate search to remote systems
/// (for example SSH). Port of the TS <c>GrepOperations</c>.
/// </summary>
public interface IGrepToolOperations
{
    /// <summary>Check if path is a directory. Throws if path does not exist.</summary>
    Task<bool> IsDirectoryAsync(string absolutePath);

    /// <summary>
    /// 上游 1.5.1 #9276 起**不再调用**：上下文行改由 ripgrep 的 <c>--context</c> 事件直接给出。
    /// 保留此成员以兼容既有自定义实现（上游把 readFile 标为 deprecated 且不再读取）。
    /// 要远程搜索请直接覆写工具的 execute。
    /// </summary>
    Task<string> ReadFileAsync(string absolutePath);
}

/// <summary>Port of the TS <c>GrepToolOptions</c>.</summary>
public sealed record GrepToolOptions
{
    /// <summary>Custom operations for grep. Default: local filesystem plus ripgrep.</summary>
    public IGrepToolOperations? Operations { get; init; }
}

/// <summary>The grep tool. Port of <c>core/tools/grep.ts</c>.</summary>
public static class GrepTool
{
    /// <summary>The TS <c>grepToolSystemPromptContribution</c>.</summary>
    public const string Snippet = "Search file contents for patterns (respects .gitignore)";

    private const int DefaultLimit = 100;

    /// <summary>
    /// ripgrep 参数。首项 <c>--no-config</c>：工具契约（literal/glob/limit…）不能受
    /// <c>RIPGREP_CONFIG_PATH</c> 等外部配置影响，否则用户配置里的 <c>--fixed-strings</c>
    /// 会静默把正则搜索变成字面量搜索，模型拿到不完整结果且没有报错（上游 #11203）。
    /// </summary>
    /// <param name="contextValue">&gt;0 时向 rg 传 <c>--context N</c> 由它给出上下文行（#9276）。</param>
    /// <param name="isDirectory">目录搜索时搜索 <c>.</c> 并把 cwd 设为 searchPath，令 glob 锚定在 path（#11807）。</param>
    internal static List<string> BuildRgArgs(
        bool ignoreCase,
        bool literal,
        string? glob,
        string pattern,
        string searchPath,
        int contextValue = 0,
        bool isDirectory = false)
    {
        var rgArgs = new List<string> { "--no-config", "--json", "--line-number", "--color=never", "--hidden" };
        if (ignoreCase)
        {
            rgArgs.Add("--ignore-case");
        }

        if (literal)
        {
            rgArgs.Add("--fixed-strings");
        }

        if (glob is not null)
        {
            rgArgs.Add("--glob");
            // 上游 1.5.1（#10998 / #11807）：rg 永远不匹配前导 "./"（"./src/*.ts" 会零结果），
            // 去掉它但保留 "!" 取反前缀；也允许多次 "./"。
            rgArgs.Add(System.Text.RegularExpressions.Regex.Replace(glob, @"^(!?)(?:\./)+", "$1"));
        }

        // 上游 1.5.1 #9276：上下文行改由 ripgrep 以 context 事件直接给出，不再整文件读回内存
        // （大文件 OOM / UTF-16 / 孤立 CR 一并解决）。
        if (contextValue > 0)
        {
            rgArgs.Add("--context");
            rgArgs.Add(contextValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        rgArgs.Add("--");
        rgArgs.Add(pattern);
        // 上游 1.5.1 #11807：rg 把 --glob 锚在自己的 cwd；目录搜索改在 searchPath 里搜 "."，
        // 输出既已相对，formatPath 再把它解析回 searchPath 求相对路径（同时规避符号链接的实路径问题）。
        rgArgs.Add(isDirectory ? "." : searchPath);
        return rgArgs;
    }

    /// <summary>
    /// 上游 1.5.1 #11807：rg 的 <c>--glob</c> 锚在自己的 cwd，目录搜索必须以 searchPath 为 cwd，
    /// 才能让 <c>path:"repo", glob:"migrations/main/*.js"</c> 命中；单文件搜索仍沿用进程 cwd。
    /// </summary>
    internal static string RgWorkingDirectory(string cwd, string searchPath, bool isDirectory) =>
        isDirectory ? searchPath : cwd;

    /// <summary>
    /// 上游 1.5.1 #11807：目录搜索时 rg 输出的是 <c>./a.js</c> 之类的相对路径，先按 searchPath
    /// 解析成绝对路径再求相对（<c>path.relative(searchPath, path.resolve(searchPath, filePath))</c>）；
    /// 不在 searchPath 下时回落到文件名。
    /// </summary>
    internal static string FormatRgPath(string searchPath, string filePath, bool isDirectory)
    {
        if (isDirectory)
        {
            // 先把 base 变成完全限定路径，Path.GetFullPath(path, basePath) 在 Windows 上否则会抛。
            var basePath = Path.GetFullPath(searchPath);
            var resolved = Path.GetFullPath(filePath, basePath);
            var relative = Path.GetRelativePath(basePath, resolved);
            if (relative.Length > 0 && !relative.StartsWith("..", StringComparison.Ordinal))
            {
                return relative.Replace('\\', '/');
            }
        }

        return Path.GetFileName(filePath);
    }

    private static readonly ToolSchema GrepSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["pattern"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Search pattern (regex or literal string)",
            },
            ["path"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Directory or file to search (default: current directory)",
            },
            ["glob"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Filter files by glob pattern, relative to path, e.g. '*.ts', 'src/**/*.ts' or '**/*.spec.ts'. A pattern without '/' matches file names at any depth",
            },
            ["ignoreCase"] = new Dictionary<string, object?>
            {
                ["type"] = "boolean",
                ["description"] = "Case-insensitive search (default: false)",
            },
            ["literal"] = new Dictionary<string, object?>
            {
                ["type"] = "boolean",
                ["description"] = "Treat pattern as literal string instead of regex (default: false)",
            },
            ["context"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["description"] = "Number of lines to show before and after each match (default: 0)",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["description"] = "Maximum number of matches to return (default: 100)",
            },
        },
        ["required"] = new[] { "pattern" },
        ["additionalProperties"] = false,
    });

    private sealed class LocalGrepOperations : IGrepToolOperations
    {
        public Task<bool> IsDirectoryAsync(string absolutePath) =>
            Task.FromResult(Directory.Exists(absolutePath));

        public Task<string> ReadFileAsync(string absolutePath) =>
            File.ReadAllTextAsync(absolutePath, Encoding.UTF8);
    }

    /// <summary>The TS <c>createGrepToolDefinition(cwd, options)</c>.</summary>
    public static ToolDefinition CreateGrepToolDefinition(string cwd, GrepToolOptions? options = null)
    {
        var customOps = options?.Operations;
        return new ToolDefinition
        {
            Name = "grep",
            Label = "grep",
            Description =
                $"Search file contents for a pattern. Returns matching lines with file paths and line numbers. Respects .gitignore. Output is truncated to {DefaultLimit} matches or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first). Long lines are truncated to {Truncate.GrepMaxLineLength} chars.",
            PromptSnippet = Snippet,
            Parameters = GrepSchema,
            Execute = async (toolCallId, args, signal, onUpdate, ctx) =>
            {
                var pattern = ToolArgs.GetString(args, "pattern") ?? "";
                var searchDir = ToolArgs.GetString(args, "path");
                var glob = ToolArgs.GetString(args, "glob");
                var ignoreCase = ToolArgs.GetBool(args, "ignoreCase") ?? false;
                var literal = ToolArgs.GetBool(args, "literal") ?? false;
                var context = ToolArgs.GetInt(args, "context");
                var limit = ToolArgs.GetInt(args, "limit");

                var rgPath = await ToolsManager.EnsureToolAsync("rg", cancellationToken: signal).ConfigureAwait(false);
                if (rgPath is null)
                {
                    throw new InvalidOperationException("ripgrep (rg) is not available and could not be downloaded");
                }

                var searchPath = ToolPathUtils.ResolveToCwd(searchDir ?? ".", ToolContexts.ResolveCwd(ctx, cwd));
                var ops = customOps ?? new LocalGrepOperations();
                bool isDirectory;
                try
                {
                    isDirectory = await ops.IsDirectoryAsync(searchPath).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException($"Path not found: {searchPath}");
                }

                // 上游 #9276：钳制避免 String() 出现科学计数法（rg 会拒绝）；int 已天然有界。
                var contextValue = context is > 0 ? context.Value : 0;
                var effectiveLimit = Math.Max(1, limit ?? DefaultLimit);

                // 上游 1.5.1 #9276：上下文行由 rg 的 context 事件直接给出，此纯叶子完成
                // pushLine / limit / 字节截断，端口不再整文件读回内存（也不再调用 ReadFileAsync）。
                var processor = new GrepRgEventProcessor(
                    filePath => FormatRgPath(searchPath, filePath, isDirectory),
                    contextValue,
                    effectiveLimit,
                    Truncate.DefaultMaxBytes);

                var rgArgs = BuildRgArgs(ignoreCase, literal, glob, pattern, searchPath, contextValue, isDirectory);
                var killedDueToLimit = false;
                var stderr = new StringBuilder();

                var child = ChildProcess.SpawnProcess(rgPath, rgArgs, new SpawnOptions
                {
                    // 上游 1.5.1 #11807：让 rg 的 --glob 锚定在 path（目录搜索）。
                    Cwd = RgWorkingDirectory(cwd, searchPath, isDirectory),
                    Stdio = [StdioMode.Ignore, StdioMode.Pipe, StdioMode.Pipe],
                    WindowsHide = true,
                });

                // ripgrep 每行一个 JSON 事件；按字节缓冲并按 '\n' 切分（对齐 Node readline），
                // 这样跨 chunk 的长行（含 base64 bytes）也不会被截断。
                var pending = new List<byte>();

                void ProcessLine(string line)
                {
                    if (processor.Done)
                    {
                        return;
                    }

                    if (!GrepRgEventProcessor.TryParseRgEvent(line, out var ev))
                    {
                        return;
                    }

                    // processor.Process 在「limit 已被证明」时返回 true → finish()：停掉 rg 子进程。
                    if (processor.Process(ev) && child.Id is int pid)
                    {
                        Shell.KillProcessTree(pid);
                        killedDueToLimit = true;
                    }
                }

                child.StdoutData += data =>
                {
                    pending.AddRange(data);
                    while (true)
                    {
                        var nl = pending.IndexOf((byte)'\n');
                        if (nl < 0)
                        {
                            break;
                        }

                        var arr = pending.GetRange(0, nl).ToArray();
                        pending.RemoveRange(0, nl + 1);
                        var length = arr.Length;
                        if (length > 0 && arr[length - 1] == (byte)'\r')
                        {
                            length--; // CRLF
                        }

                        ProcessLine(Encoding.UTF8.GetString(arr, 0, length));
                    }
                };

                child.StderrData += data => stderr.Append(Encoding.UTF8.GetString(data));

                using var registration = signal.Register(() =>
                {
                    if (child.Id is int pid)
                    {
                        Shell.KillProcessTree(pid);
                    }
                });

                var exitCode = await child.WaitAsync().ConfigureAwait(false);
                signal.ThrowIfCancellationRequested();

                // readline 在流关闭时会把最后一行（无换行结尾）也发出去。
                if (pending.Count > 0)
                {
                    var arr = pending.ToArray();
                    var length = arr.Length;
                    if (length > 0 && arr[length - 1] == (byte)'\r')
                    {
                        length--;
                    }

                    ProcessLine(Encoding.UTF8.GetString(arr, 0, length));
                }

                // ripgrep exits 0 on matches, 1 on no matches; anything else is a real failure.
                if (!killedDueToLimit && exitCode is not 0 and not 1)
                {
                    var errorMsg = stderr.ToString().Trim();
                    throw new InvalidOperationException(
                        errorMsg.Length > 0 ? errorMsg : $"ripgrep exited with code {exitCode}");
                }

                var (output, details) = processor.Finalize();
                return new AgentToolResult([new TextContent(output)], Details: details);
            },
        };
    }

    /// <summary>The TS <c>createGrepTool(cwd, options)</c>.</summary>
    public static AgentTool CreateGrepTool(string cwd, GrepToolOptions? options = null) =>
        ToolDefinitionWrapper.WrapToolDefinition(CreateGrepToolDefinition(cwd, options));
}
