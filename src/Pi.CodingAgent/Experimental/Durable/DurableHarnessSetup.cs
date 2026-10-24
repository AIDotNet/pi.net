using Pi.Chord.Context;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils.Image;
using Pi.Durable.Env;
using Pi.Durable.Harness;
using Pi.Durable.Tools;

namespace Pi.CodingAgent.Experimental.Durable;

/// <summary>
/// durable 实验宿主专属的装配件。对应 TS <c>durable/harness-setup.ts</c> 中不与 vacation 共用的部分：
/// <c>createCodingRegistry</c> 与 <c>ExecutionEnvs</c>。
/// <para>其余部分已有共用实现，不在此重复：<c>configureHarnessHttp</c> 见 <see cref="HttpDispatcher"/>，
/// <c>createHarnessSettings</c> 见 <see cref="SessionWorkerHarnessSetup.CreateHarnessSettings"/>，
/// <c>createPiPrompt</c> 见 <see cref="SessionWorkerHarnessSetup.CreatePiPrompt"/>，
/// <c>findInitialAgentModel</c> 见 <see cref="ModelResolver.FindInitialModelAsync"/>。</para>
/// </summary>
public static class DurableHarnessSetup
{
    /// <summary>
    /// pi 的 HTTP 设置。TS 的 <c>configureHarnessHttp</c> 还装一个 Undici 全局 dispatcher 并替换
    /// <c>globalThis.fetch</c>；.NET 没有对应物，空闲超时由各 client 自行配置（差异 C32 / C267），
    /// 因此这里只播种代理环境变量。
    /// </summary>
    public static void ConfigureHarnessHttp(SettingsManager settingsManager)
        => HttpDispatcher.ApplyHttpProxySettings(settingsManager.GetGlobalSettings().HttpProxy);

    /// <summary>装了 pi 的编码工具与系统提示的注册表。</summary>
    public static IRegistry CreateCodingRegistry(SettingsManager settingsManager, string cwd)
    {
        var registry = Registry.CreateRegistry();
        InstallCodingRegistry(registry, settingsManager, cwd);
        return registry;
    }

    /// <summary>往既有注册表里装 pi 的编码工具与系统提示（运行时用这个，因为注册表已经建好）。</summary>
    public static void InstallCodingRegistry(IRegistry registry, SettingsManager settingsManager, string cwd)
    {
        registry.Install(CodingTools.Create(SkiaImageProcessor.Instance));
        registry.Install(SessionWorkerHarnessSetup.CreatePiPrompt(settingsManager, cwd));
    }
}

/// <summary>
/// 每个目录一个执行环境，同目录的所有对话共用。对应 TS <c>ExecutionEnvs</c>。
/// </summary>
public sealed class ExecutionEnvs(string defaultCwd)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LocalExecutionEnv> _envs = new(StringComparer.Ordinal);

    /// <summary>按对话的 <c>cwd</c> 取环境；缺省用构造时的目录。</summary>
    public Task<IExecutionEnv?> EnvAsync(EnvTarget target, Context context)
    {
        var cwd = target.Cwd ?? defaultCwd;
        lock (_gate)
        {
            if (!_envs.TryGetValue(cwd, out var env))
            {
                env = new LocalExecutionEnv(new LocalExecutionEnv.LocalEnvOptions { Cwd = cwd });
                _envs[cwd] = env;
            }

            return Task.FromResult<IExecutionEnv?>(env);
        }
    }

    public async Task CleanupAsync(Context context)
    {
        LocalExecutionEnv[] envs;
        lock (_gate)
        {
            envs = [.. _envs.Values];
            _envs.Clear();
        }

        foreach (var env in envs) await env.CleanupAsync(context).ConfigureAwait(false);
    }
}
