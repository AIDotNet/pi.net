namespace Pi.CodingAgent.Experimental.Vacation;

using Pi.CodingAgent.Core;

/// <summary>
/// Vacation 实验宿主的 TUI 入口。上游 <c>experimental/vacation/tui.ts</c> 与 durable 版逐字节相同，
/// 已收敛到 <see cref="ExperimentalTui"/>；这里只保留 vacation 的入口名。
/// </summary>
public static class VacationTui
{
    public static Task RunAsync(
        IExperimentalViewSource source,
        IExperimentalController controller,
        SettingsManager settings)
        => ExperimentalTui.RunAsync(source, controller, settings);
}
