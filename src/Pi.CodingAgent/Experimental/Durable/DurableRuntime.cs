using Pi.CodingAgent.Core;
using Pi.Durable.Harness;
using Pi.Durable.Tools;
using Pi.Durable.Types;

namespace Pi.CodingAgent.Experimental.Durable;

/// <summary>
/// durable 实验宿主的入口。对应 TS <c>experimental/durable/runtime.ts</c> 的 <c>openDurable</c>：
/// 一个进程拥有模型运行时、durable Harness、它的 SQLite 存储和 TUI。
/// <para>与 vacation 版的差别只有装配：pi 的编码工具与系统提示、<c>subagent</c> 扩展、按目录缓存的执行环境、
/// 以及根对话始终写 <c>cwd</c>。其余（视图、控制器、命令串行队列、关闭链）已收敛到
/// <see cref="ExperimentalRuntime"/>。</para>
/// </summary>
public static class DurableRuntime
{
    internal static readonly ExperimentalProfile Profile = new()
    {
        SessionKind = ExperimentalSessionKind.Durable,
        Label = "durable",
        InstallRegistry = DurableHarnessSetup.InstallCodingRegistry,
        // TS 的 durable 根对话不显式给 extensions：pi 的默认扩展集合就够（CodingTools + pi-prompt）。
        RootExtensions = null,
        RootCwd = location => location.Cwd,
        // 每个目录一个执行环境，同目录的所有对话共用；关闭时统一清理。
        CreateEnvironments = location =>
        {
            var envs = new ExecutionEnvs(location.Cwd);
            return new ExperimentalEnvironments(envs.EnvAsync, envs.CleanupAsync);
        },
    };

    /// <summary>
    /// 对应 TS <c>openDurable</c>。选项类型与 vacation 共用 <see cref="OpenExperimentalOptions"/>：
    /// 上游两处 <c>runtime.ts</c> 各自声明的 <c>OpenDurableOptions</c> 字段完全相同（<c>cwd</c> / <c>continueSession</c>）。
    /// </summary>
    public static Task<OpenExperimentalResult> OpenAsync(OpenExperimentalOptions? options = null)
        => ExperimentalRuntime.OpenAsync(Profile, options);

    /// <summary>视图里对话的 <c>pi.agent</c> 文档；没有时返回 null。</summary>
    public static AgentState? AgentOf(ConversationView view) => ExperimentalRuntime.AgentOf(view);
}
