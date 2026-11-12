using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Tools;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 1.5.1 #7121：find/ls 的 limit 边界。低于 1 的 limit 一律按 1 处理；
/// 「恰好 limit 条」不再误报达到上限（内部多取一条来区分）；只有真正超过才报，
/// 且只输出前 limit 条。custom glob 也应被要求多返回一条。
/// </summary>
public class FindLsLimitRegressionTests
{
    private static string GetText(AgentToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContent>().Select(block => block.Text));

    private static async Task<AgentToolResult> ExecuteAsync(
        ToolDefinition definition, Dictionary<string, object?> args) =>
        await definition.Execute("test-call", args, CancellationToken.None, null, null);

    private sealed class FakeFindOps(IReadOnlyList<string> files) : IFindToolOperations
    {
        public readonly List<int> RequestedLimits = [];

        public Task<bool> ExistsAsync(string absolutePath) => Task.FromResult(true);

        public Task<IReadOnlyList<string>> GlobAsync(string pattern, string cwd, FindGlobOptions options)
        {
            RequestedLimits.Add(options.Limit);
            return Task.FromResult<IReadOnlyList<string>>(files.Take(options.Limit).ToList());
        }
    }

    private sealed class FakeLsOps(IReadOnlyList<string> entries) : ILsToolOperations
    {
        public Task<bool> ExistsAsync(string absolutePath) => Task.FromResult(true);
        public Task<bool> IsDirectoryAsync(string absolutePath) =>
            Task.FromResult(Path.GetFileName(absolutePath.TrimEnd('/', '\\')) == "dir");
        public Task<IReadOnlyList<string>> ReadDirAsync(string absolutePath) =>
            Task.FromResult(entries);
    }

    [Fact]
    public async Task Find_ExactlyAtLimit_DoesNotReportLimitReached()
    {
        var ops = new FakeFindOps(["a.txt", "b.txt", "c.txt"]);
        var tool = FindTool.CreateFindToolDefinition("/work", new FindToolOptions { Operations = ops });
        var result = await ExecuteAsync(tool, new() { ["pattern"] = "*.txt", ["limit"] = 3 });

        Assert.Equal("a.txt\nb.txt\nc.txt", GetText(result));
        Assert.Null(result.Details);
        // 恰好 3 条时内部只应要 4 条（one past the limit）。
        Assert.Equal([4], ops.RequestedLimits);
    }

    [Fact]
    public async Task Find_OverLimit_ReportsOnceAndShowsOnlyLimitRows()
    {
        var ops = new FakeFindOps(["a.txt", "b.txt", "c.txt"]);
        var tool = FindTool.CreateFindToolDefinition("/work", new FindToolOptions { Operations = ops });
        var result = await ExecuteAsync(tool, new() { ["pattern"] = "*.txt", ["limit"] = 2 });

        Assert.StartsWith("a.txt\nb.txt", GetText(result));
        Assert.Contains("2 results limit reached", GetText(result));
        var details = Assert.IsType<FindToolDetails>(result.Details);
        Assert.Equal(2, details.ResultLimitReached);
        Assert.Equal([3], ops.RequestedLimits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Find_LimitBelowOne_IsTreatedAsOne(int limit)
    {
        var ops = new FakeFindOps(["a.txt", "b.txt", "c.txt"]);
        var tool = FindTool.CreateFindToolDefinition("/work", new FindToolOptions { Operations = ops });
        var result = await ExecuteAsync(tool, new() { ["pattern"] = "*.txt", ["limit"] = limit });

        Assert.StartsWith("a.txt", GetText(result));
        Assert.DoesNotContain("a.txt\nb.txt", GetText(result));
        Assert.Contains("1 results limit reached", GetText(result));
        var details = Assert.IsType<FindToolDetails>(result.Details);
        Assert.Equal(1, details.ResultLimitReached);
        Assert.Equal([2], ops.RequestedLimits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Ls_LimitBelowOne_ListsOneEntry(int limit)
    {
        var ops = new FakeLsOps(["a", "b", "c"]);
        var tool = LsTool.CreateLsToolDefinition("/work", new LsToolOptions { Operations = ops });
        var result = await ExecuteAsync(tool, new() { ["path"] = "dir", ["limit"] = limit });

        Assert.StartsWith("a", GetText(result));
        Assert.DoesNotContain("(empty directory)", GetText(result));
    }
}
