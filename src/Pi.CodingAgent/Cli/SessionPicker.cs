using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using KeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

namespace Pi.CodingAgent.Cli;

// ============================================================================
// `--resume` 的会话选择器 — TS `cli/session-picker.ts`（55 行）。
//
// 纯胶水：建一个启动期 TUI、装一个 `SessionSelectorComponent`、等它给出结果。
// 没有可独立测试的决策逻辑，故不抽纯函数——一致性靠逐行对齐 TS。
//
// 与 TS 的差异：
//   - `process.exit(0)` → `Environment.Exit(0)`（Esc/Ctrl-C 退出路径）。
//   - `setKeybindings(keybindings)` 是 pi-tui 的全局设置，C# 侧对应 `GlobalKeybindings.Set`。
//   - TS 的 Promise 用 `resolved` 布尔量保证只结算一次，且**先 `ui.stop()` 再 `resolve()`**；
//     这里保留同一次序（`settled` 守卫 → Stop → TrySetResult），不用
//     `TrySetResult` 的返回值兼任守卫，否则 Stop 会跑到结算之后。
// ============================================================================

/// <summary>Port of <c>cli/session-picker.ts</c>.</summary>
public static class SessionPicker
{
    /// <summary>对应 TS <c>selectSession</c>：返回选中的会话路径，取消返回 <c>null</c>。</summary>
    public static async Task<string?> SelectSessionAsync(
        SessionsLoader currentSessionsLoader,
        SessionsLoader allSessionsLoader,
        SettingsManager settingsManager)
    {
        var ui = await StartupUi.CreateStartupTuiAsync(settingsManager).ConfigureAwait(false);

        var keybindings = KeybindingsManager.Create();
        GlobalKeybindings.Set(keybindings);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = false;

        void Settle(string? sessionPath)
        {
            if (settled)
            {
                return;
            }

            settled = true;
            ui.Stop();
            completion.TrySetResult(sessionPath);
        }

        var selector = new SessionSelectorComponent(
            currentSessionsLoader,
            allSessionsLoader,
            sessionPath => Settle(sessionPath),
            () => Settle(null),
            () =>
            {
                ui.Stop();
                Environment.Exit(0);
            },
            () => ui.RequestRender(),
            new SessionSelectorComponent.Options { ShowRenameHint = false, Keybindings = keybindings });

        ui.AddChild(selector);
        ui.SetFocus(selector.GetSessionList());
        StartupUi.StartStartupTui(ui, settingsManager);
        return await completion.Task.ConfigureAwait(false);
    }
}
