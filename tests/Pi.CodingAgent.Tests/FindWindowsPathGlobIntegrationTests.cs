using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Utils;
using Xunit;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 1.5.2 #11972 / #9262 端到端：用真实 fd 验证 Windows 路径模式。
/// fd 10.5.0（pi 下载的旧版）在 Windows 上对 `--full-path` + `[/\\]` 的 `src/**/*.ts`
/// 返回空结果——这正是端口修复前的 bug。修复后改「列全部 + Minimatch 过滤」，
/// 与 fd 版本无关。测试缝注入已下载的 fd.exe，未安装 fd 时跳过（CI 稳健）。
/// </summary>
public class FindWindowsPathGlobIntegrationTests
{
    private static string Text(AgentToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContent>().Select(block => block.Text));

    private static ToolDefinition Tool(string cwd, string fdPath) => FindTool.CreateFindToolDefinition(
        cwd,
        new FindToolOptions
        {
            EnsureFdForTests = _ => Task.FromResult<string?>(fdPath),
            // 让 .git 遍历立刻结束，避免读到临时目录外的真实仓库。
            PathExistsForTests = _ => Task.FromResult(true),
        });

    private static async Task<AgentToolResult> ExecuteAsync(ToolDefinition tool, string pattern, string path) =>
        await tool.Execute("test-call", new Dictionary<string, object?>
        {
            ["pattern"] = pattern,
            ["path"] = path,
        }, CancellationToken.None, null, null);

    [Fact]
    public async Task WindowsPathPatternFindsDirectAndNestedFiles()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "#11972 是 Windows 路径模式修复");
        var fdPath = ToolsManager.GetToolPath("fd")
                     ?? Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         ".pi", "agent", "bin", "fd.exe");
        Assert.SkipUnless(File.Exists(fdPath), $"fd not installed at {fdPath}");

        var root = Path.Combine(Path.GetTempPath(), "pi-find-11972-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "foo", "bar"));
        try
        {
            File.WriteAllText(Path.Combine(root, "src", "direct.spec.ts"), "");
            File.WriteAllText(Path.Combine(root, "src", "foo", "bar", "example.spec.ts"), "");

            var result = Text(await ExecuteAsync(Tool(root, fdPath), "src/**/*.ts", root));

            // 修复前（旧 --full-path + [/\\]）此断言在 fd 10.5.x 上会得到 "No files found matching pattern"。
            Assert.Contains("src/direct.spec.ts", result);
            Assert.Contains("src/foo/bar/example.spec.ts", result);
            Assert.DoesNotContain("No files found", result);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task WindowsSingleStarDoesNotMatchIntoSubdirectories()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "#11972 是 Windows 路径模式修复");
        var fdPath = ToolsManager.GetToolPath("fd")
                     ?? Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         ".pi", "agent", "bin", "fd.exe");
        Assert.SkipUnless(File.Exists(fdPath), $"fd not installed at {fdPath}");

        var root = Path.Combine(Path.GetTempPath(), "pi-find-11972-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "foo", "bar"));
        try
        {
            File.WriteAllText(Path.Combine(root, "src", "direct.spec.ts"), "");
            File.WriteAllText(Path.Combine(root, "src", "foo", "bar", "example.spec.ts"), "");

            var result = Text(await ExecuteAsync(Tool(root, fdPath), "src/*.ts", root));

            Assert.Contains("src/direct.spec.ts", result);
            Assert.DoesNotContain("example.spec.ts", result);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }
}
