using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Cli;

// ============================================================================
// 启动期项目信任上下文 — TS `cli/project-trust.ts`（62 行）。
//
// 给扩展的 `project_trust` 处理器一个「能问用户」的最小 UI：
//   - `select` / `input`：非交互模式或没有 UI 时返回 undefined；
//   - `confirm`：同样两处闸门，但返回 false（布尔语义，不能返回 undefined）；
//   - `notify`：**只有非交互模式才打印**（交互模式下通知由 TUI 自己呈现，这里不重复）。
//     颜色按 type 取 error=red / warning=yellow / 其余 cyan。
//
// 与 TS 的差异：
//   - TS 用 `Pick<ExtensionUIContext, "select" | "confirm" | "input" | "notify">` 收窄类型；
//     端口新增了同形的 <see cref="IProjectTrustUiContext"/>（`IExtensionUIContext` 继承它），
//     于是这里只需实现四个成员，不必为其余 21 个写空实现。
//   - `mode: "interactive" ? "tui" : mode`：`AppMode` 常量在 C# 侧是 string（C242）。
//   - `console.error` → <see cref="Console.Error"/>。
// ============================================================================

/// <summary>对应 TS <c>createProjectTrustContext</c> 的入参。</summary>
public sealed record ProjectTrustUiOptions
{
    public required string Cwd { get; init; }

    /// <summary>启动模式，取 <see cref="AppModes"/> 之一。</summary>
    public required string Mode { get; init; }

    public required SettingsManager SettingsManager { get; init; }

    public bool HasUI { get; init; }
}

/// <summary>Port of <c>cli/project-trust.ts</c>.</summary>
public static class ProjectTrustUi
{
    /// <summary>对应 TS <c>createProjectTrustContext</c>。</summary>
    public static ProjectTrustContext CreateProjectTrustContext(ProjectTrustUiOptions options) => new()
    {
        Cwd = options.Cwd,
        // TS：交互模式对外报 "tui"，其余原样透传。
        Mode = options.Mode == AppModes.Interactive ? "tui" : options.Mode,
        HasUI = options.HasUI,
        Ui = new StartupTrustUiContext(options),
    };

    /// <summary>只有 <see cref="IProjectTrustUiContext"/> 四个成员的最小实现。</summary>
    private sealed class StartupTrustUiContext(ProjectTrustUiOptions options) : IProjectTrustUiContext
    {
        private bool CanPrompt => options.HasUI && options.Mode == AppModes.Interactive;

        public Task<string?> Select(string title, IReadOnlyList<string> choices, ExtensionUIDialogOptions? opts = null)
        {
            if (!CanPrompt)
            {
                return Task.FromResult<string?>(null);
            }

            return StartupUi.ShowStartupSelectorAsync(
                options.SettingsManager,
                title,
                choices.Select(choice => new StartupSelectorOption<string>(choice, choice)).ToList());
        }

        public Task<bool> Confirm(string title, string message, ExtensionUIDialogOptions? opts = null)
        {
            if (!CanPrompt)
            {
                return Task.FromResult(false);
            }

            return ConfirmCoreAsync(title, message);
        }

        private async Task<bool> ConfirmCoreAsync(string title, string message) =>
            // TS `(await showStartupSelector(...)) ?? false`：取消（undefined）按「否」处理。
            // C# 侧 `ShowStartupSelectorAsync<bool>` 的 `T?` 在无约束泛型里就是 `bool`（不是 `bool?`），
            // 取消时返回 `default(bool)` 即 false——恰好与 TS 的 `?? false` 同值，故不需要再兜。
            await StartupUi.ShowStartupSelectorAsync(
                options.SettingsManager,
                $"{title}\n{message}",
                [
                    new StartupSelectorOption<bool>("Yes", true),
                    new StartupSelectorOption<bool>("No", false),
                ]).ConfigureAwait(false);

        public Task<string?> Input(string title, string? placeholder = null, ExtensionUIDialogOptions? opts = null)
        {
            if (!CanPrompt)
            {
                return Task.FromResult<string?>(null);
            }

            return StartupUi.ShowStartupInputAsync(options.SettingsManager, title, placeholder);
        }

        public void Notify(string message, string? type = null)
        {
            // 交互模式下 TUI 自己会呈现通知，这里不重复打印。
            if (options.Mode == AppModes.Interactive)
            {
                return;
            }

            var color = type switch
            {
                "error" => (Func<string, string>)Chalk.Red,
                "warning" => Chalk.Yellow,
                _ => Chalk.Cyan,
            };
            Console.Error.WriteLine(color(message));
        }
    }
}
