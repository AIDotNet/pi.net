using Pi.CodingAgent.Core;
using Pi.Durable.Harness;
using Pi.Durable.Types;

namespace Pi.CodingAgent.Experimental.Vacation;

/// <summary>
/// Vacation 实验宿主的入口。上游 <c>experimental/vacation/runtime.ts</c> 与 durable 版除装配差异外是同一份代码，
/// 已收敛到 <see cref="ExperimentalRuntime"/>；这里只固定 vacation 的装配。
/// </summary>
public static class VacationRuntime
{
    internal static readonly ExperimentalProfile Profile = new()
    {
        SessionKind = ExperimentalSessionKind.Vacation,
        Label = "vacation",
        // 只装 vacation 规划器；研究子代理自己选 search。
        InstallRegistry = (registry, _, _) =>
        {
            registry.Install(VacationExtensions.Vacation);
            registry.Install(VacationExtensions.Search);
        },
        RootExtensions = [VacationExtensions.Vacation],
        // vacation 只在新建会话时写 cwd（沿用既有行为）。
        RootCwd = location => location.Created ? location.Cwd : null,
    };

    public static Task<OpenExperimentalResult> OpenAsync(OpenExperimentalOptions? options = null)
        => ExperimentalRuntime.OpenAsync(Profile, options);

    /// <summary>视图里对话的 <c>pi.agent</c> 文档；没有时返回 null。</summary>
    public static AgentState? AgentOf(ConversationView view) => ExperimentalRuntime.AgentOf(view);
}
