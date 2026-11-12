namespace Pi.CodingAgent.Cli;

// ============================================================================
// 上游 v1.5.1 #11707 / #11711（package-manager-cli.ts）的 update 结束汇总。
//
// PackageManager.update() 现在为每个实际变化的包发 `result` 事件（消息形如
// "npm:foo 1.2.0 -> 1.3.0"），CLI 收集这些消息、统计 `progress` warning 数，
// 结束时按三态输出：
//   1. offline 模式           -> "Skipped package updates (offline mode)"（黄）
//   2. 无变化但有 warning     -> "No packages were updated, see the warnings above"（黄）
//   3. 无变化且无 warning     -> "Packages are already up to date"（绿）
//   4. 有变化                 -> "Updated N package(s):" + 逐行版本变化（绿）
// 指定单一 --extension <source> 时，2/3 态用该 source 的措辞。
//
// 抽成纯叶子便于逐字回归上游 CLI 文案，与真实 npm/git 进程解耦。
// ============================================================================

/// <summary>`pi update --extensions` 结束汇总的纯决策叶子。</summary>
public static class PackageUpdateReport
{
    /// <summary>汇总输出的所有行（按顺序，不含颜色）。</summary>
    public static IReadOnlyList<string> Build(
        IReadOnlyList<string> resultMessages,
        int warningCount,
        bool offline,
        string? updateSource)
    {
        if (offline)
        {
            return ["Skipped package updates (offline mode)"];
        }

        if (resultMessages.Count == 0)
        {
            if (warningCount > 0)
            {
                return [updateSource is not null
                    ? $"{updateSource} was not updated, see the warnings above"
                    : "No packages were updated, see the warnings above"];
            }

            return [updateSource is not null
                ? $"{updateSource} is already up to date"
                : "Packages are already up to date"];
        }

        var count = resultMessages.Count;
        var lines = new List<string> { $"Updated {count} package{(count == 1 ? "" : "s")}:" };
        lines.AddRange(resultMessages.Select(message => $"  {message}"));
        return lines;
    }
}
