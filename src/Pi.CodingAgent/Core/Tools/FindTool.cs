using System.Text;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>Port of the TS <c>FindToolDetails</c>.</summary>
public sealed record FindToolDetails
{
    public Truncate.TruncationResult? Truncation { get; init; }

    public int? ResultLimitReached { get; init; }
}

/// <summary>
/// Pluggable operations for the find tool. Override these to delegate file search to remote
/// systems (for example SSH). Port of the TS <c>FindOperations</c>.
/// </summary>
public interface IFindToolOperations
{
    /// <summary>Check if path exists.</summary>
    Task<bool> ExistsAsync(string absolutePath);

    /// <summary>Find files matching glob pattern. Returns relative or absolute paths.</summary>
    Task<IReadOnlyList<string>> GlobAsync(string pattern, string cwd, FindGlobOptions options);
}

/// <summary>Port of the TS glob options object.</summary>
public sealed record FindGlobOptions
{
    public required IReadOnlyList<string> Ignore { get; init; }

    public required int Limit { get; init; }
}

/// <summary>Port of the TS <c>FindToolOptions</c>.</summary>
public sealed record FindToolOptions
{
    /// <summary>Custom operations for find. Default: local filesystem plus fd.</summary>
    public IFindToolOperations? Operations { get; init; }

    // 测试接缝（默认 null 走生产逻辑）：让 #11974 的「abort 落在 .git 查找期间」可被精确复现。
    // 放在实例选项里而非 static，避免 xUnit 并行 collection 之间的共享可变状态互相干扰。
    internal Func<CancellationToken, Task<string?>>? EnsureFdForTests { get; init; }
    internal Func<string, Task<bool>>? PathExistsForTests { get; init; }
    internal Action? SpawnHookForTests { get; init; }
}

/// <summary>The find tool. Port of <c>core/tools/find.ts</c>.</summary>
public static class FindTool
{
    /// <summary>The TS <c>findToolSystemPromptContribution</c>.</summary>
    public const string Snippet = "Find files by glob pattern (respects .gitignore)";

    private const int DefaultLimit = 1000;

    private static readonly ToolSchema FindSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["pattern"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Glob pattern to match files, e.g. '*.ts', '**/*.json', or 'src/**/*.spec.ts'",
            },
            ["path"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Directory to search in (default: current directory)",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["description"] = "Maximum number of results (default: 1000)",
            },
        },
        ["required"] = new[] { "pattern" },
        ["additionalProperties"] = false,
    });

    /// <summary>
    /// Relativize a find result against the search root and normalize it to posix separators.
    /// The TS <c>relativizeFindResultPath</c>.
    /// </summary>
    public static string RelativizeFindResultPath(string resultPath, string searchPath)
    {
        var hadTrailingSeparator =
            resultPath.EndsWith(Path.DirectorySeparatorChar)
            || (Path.DirectorySeparatorChar == '\\' && resultPath.EndsWith('/'));
        var relativePath = Path.IsPathRooted(resultPath) ? Path.GetRelativePath(searchPath, resultPath) : resultPath;
        var posixPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        return hadTrailingSeparator && !posixPath.EndsWith('/') ? $"{posixPath}/" : posixPath;
    }

    private static string BuildResultOutput(
        IReadOnlyList<string> relativized, int effectiveLimit, bool resultLimitReached, out FindToolDetails? details)
    {
        var rawOutput = string.Join("\n", relativized);
        var truncation = Truncate.TruncateHead(rawOutput, new Truncate.TruncationOptions { MaxLines = int.MaxValue });
        var resultOutput = truncation.Content;
        var notices = new List<string>();
        if (resultLimitReached)
        {
            notices.Add($"{effectiveLimit} results limit reached. Use limit={effectiveLimit * 2} for more, or refine pattern");
        }

        if (truncation.Truncated)
        {
            notices.Add($"{Truncate.FormatSize(Truncate.DefaultMaxBytes)} limit reached");
        }

        details = notices.Count > 0
            ? new FindToolDetails
            {
                Truncation = truncation.Truncated ? truncation : null,
                ResultLimitReached = resultLimitReached ? effectiveLimit : null,
            }
            : null;
        if (notices.Count > 0)
        {
            resultOutput += $"\n\n[{string.Join(". ", notices)}]";
        }

        return resultOutput;
    }

    /// <summary>The TS <c>createFindToolDefinition(cwd, options)</c>.</summary>
    public static ToolDefinition CreateFindToolDefinition(string cwd, FindToolOptions? options = null)
    {
        var customOps = options?.Operations;
        var ensureFdForTests = options?.EnsureFdForTests;
        var pathExistsForTests = options?.PathExistsForTests;
        var spawnHookForTests = options?.SpawnHookForTests;
        return new ToolDefinition
        {
            Name = "find",
            Label = "find",
            Description =
                $"Search for files by glob pattern. Returns matching file paths relative to the search directory. Respects .gitignore. Output is truncated to {DefaultLimit} results or {Truncate.DefaultMaxBytes / 1024}KB (whichever is hit first).",
            PromptSnippet = Snippet,
            Parameters = FindSchema,
            Execute = async (toolCallId, args, signal, onUpdate, ctx) =>
            {
                var pattern = ToolArgs.GetString(args, "pattern") ?? "";
                var searchDir = ToolArgs.GetString(args, "path");
                var limit = ToolArgs.GetInt(args, "limit");

                var searchPath = ToolPathUtils.ResolveToCwd(searchDir ?? ".", ToolContexts.ResolveCwd(ctx, cwd));
                // 上游 1.5.1 #7121：fd 把 --max-results 0 当无限，低于 1 的 limit 一律按 1 处理。
                var effectiveLimit = Math.Max(1, limit ?? DefaultLimit);
                signal.ThrowIfCancellationRequested();

                // If custom operations provide glob(), use that instead of fd.
                if (customOps is not null)
                {
                    if (!await customOps.ExistsAsync(searchPath).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException($"Path not found: {searchPath}");
                    }

                    signal.ThrowIfCancellationRequested();
                    // #7121：多要一条结果，区分「恰好 limit 条」（不报上限）与「超过 limit 条」。
                    var results = await customOps.GlobAsync(pattern, searchPath, new FindGlobOptions
                    {
                        Ignore = ["**/node_modules/**", "**/.git/**"],
                        Limit = effectiveLimit + 1,
                    }).ConfigureAwait(false);
                    signal.ThrowIfCancellationRequested();
                    if (results.Count == 0)
                    {
                        return new AgentToolResult([new TextContent("No files found matching pattern")]);
                    }

                    var resultLimitReached = results.Count > effectiveLimit;
                    // Relativize paths against the search root for stable output.
                    var relativized = results.Take(effectiveLimit)
                        .Select(p => RelativizeFindResultPath(p, searchPath)).ToList();
                    return new AgentToolResult(
                        [new TextContent(BuildResultOutput(relativized, effectiveLimit, resultLimitReached, out var customDetails))],
                        Details: customDetails);
                }

                // Default implementation uses fd.
                var fdPath = ensureFdForTests is { } ensureFd
                    ? await ensureFd(signal).ConfigureAwait(false)
                    : await ToolsManager.EnsureToolAsync("fd", cancellationToken: signal).ConfigureAwait(false);
                signal.ThrowIfCancellationRequested();
                if (fdPath is null)
                {
                    throw new InvalidOperationException("fd is not available and could not be downloaded");
                }

                // 上游 1.5.2 #11972/#9262：Windows 路径模式不再交给 fd 的 --full-path
                // （fd 10.5.1 前后对 --full-path glob 的行为相反，pi 会下载最新版），改为让 fd
                // 列出全部条目、由端口用 Minimatch 逐行过滤，行为与 fd 版本无关。
                // 决策与匹配器是纯叶子 FindPathPattern，可跨平台单测。
                var pathDecision = FindPathPattern.For(pattern, NodePath.IsWindows);
                var windowsMatcher = pathDecision.UseWindowsMatcher
                    ? FindPathPattern.CreateWindowsMatcher(pathDecision)
                    : null;

                var fdArgs = new List<string> { "--color=never", "--hidden" };

                // fd normally ignores .gitignore outside git repos, so keep --no-require-git
                // there. Inside repos, use fd's default git-aware behavior so parent
                // .gitignore rules stop at nested repo boundaries.
                var insideGitRepo = false;
                for (var current = searchPath; ;)
                {
                    var gitPath = Path.Combine(current, ".git");
                    var gitExists = pathExistsForTests is { } pathExists
                        ? await pathExists(gitPath).ConfigureAwait(false)
                        : await ToolPathUtils.PathExistsAsync(gitPath).ConfigureAwait(false);
                    if (gitExists)
                    {
                        insideGitRepo = true;
                        break;
                    }

                    var parent = Path.GetDirectoryName(current);
                    if (parent is null || parent == current)
                    {
                        break;
                    }

                    current = parent;
                }

                if (!insideGitRepo)
                {
                    fdArgs.Add("--no-require-git");
                }

                if (windowsMatcher is not null)
                {
                    // 上游 #11972：Windows 路径模式让 fd 列出全部条目（空 pattern），不加 --glob /
                    // --full-path / --max-results；由端口在读取结果时用 Minimatch 过滤，且到 limit
                    // 时早停 fd（因为没有了 --max-results，fd 会一直列下去）。
                    fdArgs.Add("--");
                    fdArgs.Add("");
                    fdArgs.Add(searchPath);
                }
                else
                {
                    fdArgs.Add("--glob");
                    fdArgs.Add("--max-results");
                    // #7121：多要一条以区分「恰好 limit」与「超过 limit」；fd 也把 0 当无限，故前面已钳到 ≥1。
                    fdArgs.Add((effectiveLimit + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

                    // 非 Windows：路径模式（含 /）仍需 --full-path + 前导 **/ 才能匹配绝对路径；
                    // EffectivePattern 已由 FindPathPattern 规范化（Windows \ → /、补 **/）。
                    if (pathDecision.IsPathPattern)
                    {
                        fdArgs.Add("--full-path");
                    }

                    fdArgs.Add("--");
                    fdArgs.Add(pathDecision.EffectivePattern);
                    fdArgs.Add(searchPath);
                }

                // v1.5.1 #11974：abort 可能落在上面的 .git 父目录查找期间（调用已被拒绝，但那时
                // 还没有可停止的子进程）。spawn 之前再确认一次，避免启动一个随后无人回收的 fd。
                signal.ThrowIfCancellationRequested();

                var lines = new List<string>();
                var stderr = new StringBuilder();
                var stdoutPending = new StringBuilder();
                spawnHookForTests?.Invoke();
                var child = ChildProcess.SpawnProcess(fdPath, fdArgs, new SpawnOptions
                {
                    Cwd = cwd,
                    Stdio = [StdioMode.Ignore, StdioMode.Pipe, StdioMode.Pipe],
                    WindowsHide = true,
                });

                child.StdoutData += data =>
                {
                    var text = Encoding.UTF8.GetString(data);
                    if (windowsMatcher is null)
                    {
                        foreach (var line in text.Split('\n'))
                        {
                            lines.Add(line);
                        }
                        return;
                    }

                    // 上游 #11972：Windows 路径模式下 fd 列出全部条目，端口逐行用 Minimatch
                    // 过滤；按 \n 成行（跨 chunk 缓冲），到 limit 时早停 fd（无 --max-results）。
                    stdoutPending.Append(text);
                    var buffered = stdoutPending.ToString();
                    var lastNewline = buffered.LastIndexOf('\n');
                    if (lastNewline < 0)
                    {
                        return;
                    }

                    var complete = buffered[..lastNewline];
                    stdoutPending.Clear();
                    stdoutPending.Append(buffered[(lastNewline + 1)..]);
                    foreach (var raw in complete.Split('\n'))
                    {
                        var line = raw.TrimEnd('\r');
                        if (line.Length == 0 || lines.Count > effectiveLimit)
                        {
                            continue;
                        }

                        if (FindPathPattern.Matches(windowsMatcher, line))
                        {
                            lines.Add(line);
                        }
                    }

                    if (lines.Count > effectiveLimit)
                    {
                        child.Kill();
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

                // flush fd 输出末尾不带换行的一行（Windows 路径模式按 \n 成行时留下的尾巴）。
                if (windowsMatcher is not null)
                {
                    var tail = stdoutPending.ToString().TrimEnd('\r');
                    if (tail.Length > 0 && lines.Count <= effectiveLimit
                        && FindPathPattern.Matches(windowsMatcher, tail))
                    {
                        lines.Add(tail);
                    }
                }

                var rawLines = string.Join("\n", lines);
                if (exitCode != 0)
                {
                    var errorMsg = stderr.ToString().Trim();
                    if (rawLines.Length == 0)
                    {
                        throw new InvalidOperationException(errorMsg.Length > 0 ? errorMsg : $"fd exited with code {exitCode}");
                    }
                }

                if (rawLines.Length == 0)
                {
                    return new AgentToolResult([new TextContent("No files found matching pattern")]);
                }

                var relativizedResults = new List<string>();
                foreach (var rawLine in lines)
                {
                    var line = rawLine.TrimEnd('\r').Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    relativizedResults.Add(RelativizeFindResultPath(line, searchPath));
                }

                // #7121：仅在「超过 limit 条」时报上限，且只输出前 limit 条；恰好 limit 条不报。
                var fdLimitReached = relativizedResults.Count > effectiveLimit;
                var shown = relativizedResults.Take(effectiveLimit).ToList();

                return new AgentToolResult(
                    [new TextContent(BuildResultOutput(shown, effectiveLimit, fdLimitReached, out var details))],
                    Details: details);
            },
        };
    }

    /// <summary>The TS <c>createFindTool(cwd, options)</c>.</summary>
    public static AgentTool CreateFindTool(string cwd, FindToolOptions? options = null) =>
        ToolDefinitionWrapper.WrapToolDefinition(CreateFindToolDefinition(cwd, options));
}
