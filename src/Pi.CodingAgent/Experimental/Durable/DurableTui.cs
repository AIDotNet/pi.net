using Pi.CodingAgent.Core;

namespace Pi.CodingAgent.Experimental.Durable;

/// <summary>
/// durable 实验宿主的 TUI 入口。上游 <c>experimental/durable/tui.ts</c> 与 vacation 版逐字节相同，
/// 已收敛到 <see cref="ExperimentalTui"/>；这里只保留 durable 的入口名。
/// </summary>
public static class DurableTui
{
    public static Task RunAsync(
        IExperimentalViewSource source,
        IExperimentalController controller,
        SettingsManager settings)
        => ExperimentalTui.RunAsync(source, controller, settings);
}
