using Pi.CodingAgent.Cli;
using Xunit;

namespace Pi.CodingAgent.Tests;

// ============================================================================
// 上游 v1.5.1 #11707 / #11711（package-manager-cli.ts）的 pi update 结束汇总。
// 逐字对照上游 CLI 文案的四种状态 + 单一 --extension 措辞 + 复数/版本变化行：
//   - offline：              "Skipped package updates (offline mode)"
//   - 无变化 + warning：     "No packages were updated, see the warnings above"
//                             （单 source："<source> was not updated, see the warnings above"）
//   - 无变化 + 无 warning：  "Packages are already up to date"
//                             （单 source："<source> is already up to date"）
//   - 有变化：               "Updated N package(s):" + 每行 "  <result 消息>"
// result 消息形如 "npm:foo 1.2.0 -> 1.3.0"（由 PackageManager.update() 上报）。
// ============================================================================

public sealed class PackageUpdateReportTests
{
    [Fact]
    public void OfflineMode_SkipsRegardlessOfResults()
    {
        Assert.Equal(
            ["Skipped package updates (offline mode)"],
            PackageUpdateReport.Build(["npm:foo 1.0.0 -> 2.0.0"], warningCount: 3, offline: true, updateSource: null));
    }

    [Fact]
    public void NoUpdatesWithWarnings_ReportsWarningsNotUpToDate()
    {
        Assert.Equal(
            ["No packages were updated, see the warnings above"],
            PackageUpdateReport.Build([], warningCount: 1, offline: false, updateSource: null));
        Assert.Equal(
            ["npm:widget was not updated, see the warnings above"],
            PackageUpdateReport.Build([], warningCount: 2, offline: false, updateSource: "npm:widget"));
    }

    [Fact]
    public void NoUpdatesNoWarnings_ReportsAlreadyUpToDate()
    {
        Assert.Equal(
            ["Packages are already up to date"],
            PackageUpdateReport.Build([], warningCount: 0, offline: false, updateSource: null));
        Assert.Equal(
            ["npm:widget is already up to date"],
            PackageUpdateReport.Build([], warningCount: 0, offline: false, updateSource: "npm:widget"));
    }

    [Fact]
    public void OneUpdate_ListsSingularAndVersionChange()
    {
        Assert.Equal(
        [
            "Updated 1 package:",
            "  npm:foo 1.2.0 -> 1.3.0",
        ],
            PackageUpdateReport.Build(["npm:foo 1.2.0 -> 1.3.0"], 0, offline: false, updateSource: null));
    }

    [Fact]
    public void SeveralUpdates_ListsPluralAndAllLinesInOrder()
    {
        Assert.Equal(
        [
            "Updated 3 packages:",
            "  npm:foo 1.2.0 -> 1.3.0",
            "  npm:bar (project) (none) -> 2.0.0",
            "  git:tool abc1234 -> def5678",
        ],
            PackageUpdateReport.Build(
            [
                "npm:foo 1.2.0 -> 1.3.0",
                "npm:bar (project) (none) -> 2.0.0",
                "git:tool abc1234 -> def5678",
            ],
                2, // 即便有 warning，有 result 仍按 "Updated N" 输出
                offline: false,
                updateSource: null));
    }
}
