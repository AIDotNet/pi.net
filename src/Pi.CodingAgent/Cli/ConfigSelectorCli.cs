using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;

namespace Pi.CodingAgent.Cli;

// ============================================================================
// `pi config` 的配置选择器 — TS `cli/config-selector.ts`（61 行）。
//
// 与 `session-picker.ts` 一样是薄胶水，但有两处顺序必须保留：
//   1. `initTheme(..., true)` 在**建 TUI 之前**跑（带 watcher，配置文件改动会实时换主题）。
//   2. 正常关闭走「stop → stopThemeWatcher → resolve」；异常退出走
//      「stop → stopThemeWatcher → process.exit(0)」，两条路径都要关 watcher，
//      否则 `Environment.Exit` 之外的路径会留下一个还在盯文件的定时器。
//
// 与 TS 的差异：
//   - `ui.terminal.rows` → `ui.Terminal.Rows`；`process.exit(0)` → `Environment.Exit(0)`。
//   - `new TuiMainScreen(terminal, cursor, agentDir)` 的第三个参数（调试日志目录）在
//     Pi.Tui 端口已省略，与 `startup-ui.ts` 的处理一致。
//   - TS 的 `writeScope: "global" | "project"` 联合类型在 C# 侧是 `string`（C242 既有结论）。
// ============================================================================

/// <summary>对应 TS <c>ConfigSelectorOptions</c>。</summary>
public sealed record ConfigSelectorOptions
{
    public required ScopedResolvedResources ResolvedPaths { get; init; }
    public required SettingsManager SettingsManager { get; init; }
    public required string Cwd { get; init; }
    public required string AgentDir { get; init; }
    public string WriteScope { get; init; } = "global";
    public bool ProjectModeAvailable { get; init; } = true;
}

/// <summary>Port of <c>cli/config-selector.ts</c>.</summary>
public static class ConfigSelectorCli
{
    /// <summary>对应 TS <c>selectConfig</c>：显示配置选择器，关闭后返回。</summary>
    public static async Task SelectConfigAsync(ConfigSelectorOptions options)
    {
        // 带 watcher 初始化主题（配置文件改动会重载主题），必须在建 TUI 之前。
        ThemeRuntime.InitTheme(options.SettingsManager.GetTheme(), true);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new TuiMainScreen(
            new ProcessTerminal(),
            options.SettingsManager.GetShowHardwareCursor());
        ui.SetClearOnShrink(options.SettingsManager.GetClearOnShrink());
        var settled = false;

        var selector = new ConfigSelectorComponent(
            options.ResolvedPaths,
            options.SettingsManager,
            options.Cwd,
            options.AgentDir,
            () =>
            {
                if (settled)
                {
                    return;
                }

                settled = true;
                ui.Stop();
                ThemeRuntime.StopThemeWatcher();
                completion.TrySetResult();
            },
            () =>
            {
                ui.Stop();
                ThemeRuntime.StopThemeWatcher();
                Environment.Exit(0);
            },
            () => ui.RequestRender(),
            ui.Terminal.Rows,
            options.WriteScope,
            options.ProjectModeAvailable);

        ui.AddChild(selector);
        ui.SetFocus(selector.GetResourceList());
        ui.Start();
        await completion.Task.ConfigureAwait(false);
    }
}
