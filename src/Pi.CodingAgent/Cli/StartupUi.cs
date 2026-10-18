using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;

namespace Pi.CodingAgent.Cli;

// ============================================================================
// 启动期 TUI（选择器 / 首次设置 / 输入框）— TS `cli/startup-ui.ts`（253 行）。
//
// 这些 UI 出现在主会话之前（信任询问、首次设置、会话选择），所以不复用
// InteractiveMode 的渲染器：每个函数各自建一个 `TuiMainScreen`，跑完即停。
//
// 移植要点：
//   1. `createStartupTui` 的顺序与 TS 一致（能力覆盖 → 注册主题 → 标记终端色待定 →
//      initTheme → 全局键位 → 建屏 → clearOnShrink）。
//   2. `loadThemes` 吞掉单个主题的加载异常：启动提示不该因为某个坏主题而失败，
//      诊断留给后续正常资源加载报告。
//   3. `shouldRunFirstTimeSetup` 的四个条件逐条对齐；`process.env[ENV_AGENT_DIR]`
//      的「非空才算覆盖」用 `!string.IsNullOrEmpty`（JS 空串为假）。
//   4. `existsSync` 对目录也为真，故 `File.Exists || Directory.Exists`。
//
// 平台差异：TS 的 `new TuiMainScreen(terminal, cursor, logDirectory)` 第三个参数
// （调试日志目录）在 Pi.Tui 端口已省略（见 TuiMainScreen 的构造签名），此处不传。
// ============================================================================

/// <summary>选择器的一项。对应 TS <c>{ label: string; value: T }</c>。</summary>
public sealed record StartupSelectorOption<T>(string Label, T Value);

/// <summary>对应 TS <c>startup-ui.ts</c> 的模块级函数。</summary>
public static class StartupUi
{
    private const string OfficialPackageName = "@earendil-works/pi-coding-agent";
    private const string OfficialAppName = "pi";
    private const string OfficialConfigDirName = ".pi";

    /// <summary>
    /// 是否为官方发行版（未改名/未换包名）。对应 TS <c>isOfficialDistribution</c>。
    /// </summary>
    /// <remarks>
    /// 参数化而非直接读 <see cref="Config"/>：TS 的 <c>first-time-setup-fork.test.ts</c>
    /// 用 <c>vi.mock</c> 换掉 <c>PACKAGE_NAME</c>，C# 侧没有同等的模块替换缝，
    /// 故把判定抽成纯函数，测试直接喂不同的三元组。
    /// </remarks>
    internal static bool IsOfficialDistribution(string packageName, string appName, string configDirName)
        => packageName == OfficialPackageName
            && appName == OfficialAppName
            && configDirName == OfficialConfigDirName;

    /// <summary>对应 TS <c>loadThemes</c>：逐个加载、跳过禁用项、按名字去重、吞掉异常。</summary>
    private static List<Theme> LoadThemes(IReadOnlyList<ResolvedResource> resources)
    {
        var themes = new List<Theme>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            if (!resource.Enabled)
            {
                continue;
            }

            try
            {
                var loadedTheme = ThemeRuntime.LoadThemeFromPath(resource.Path);
                if (!string.IsNullOrEmpty(loadedTheme.Name))
                {
                    if (!seen.Add(loadedTheme.Name))
                    {
                        continue;
                    }
                }

                themes.Add(loadedTheme);
            }
            catch
            {
                // 启动提示不该因为某个坏主题而失败；诊断由后续正常资源加载报告。
            }
        }

        return themes;
    }

    /// <summary>对应 TS <c>loadStartupThemes</c>：用全局设置（项目未信任）解析主题。</summary>
    private static async Task<List<Theme>> LoadStartupThemesAsync(SettingsManager settingsManager)
    {
        var globalSettingsManager = SettingsManager.InMemory(
            settingsManager.GetGlobalSettings(),
            new SettingsManagerCreateOptions { ProjectTrusted = false });
        var packageManager = new DefaultPackageManager(
            globalSettingsManager,
            Environment.CurrentDirectory,
            Config.GetAgentDir());
        var resolvedPaths = await packageManager.ResolveAsync(_ => Task.FromResult("skip")).ConfigureAwait(false);
        return LoadThemes(resolvedPaths.Themes);
    }

    /// <summary>对应 TS <c>createStartupTui</c>。</summary>
    public static async Task<ITui> CreateStartupTuiAsync(SettingsManager settingsManager)
    {
        TerminalImage.SetCapabilityOverrides(
            InteractiveMode.ToTuiCapabilityOverrides(settingsManager.GetTerminalCapabilityOverrides()));
        ThemeRuntime.SetRegisteredThemes(await LoadStartupThemesAsync(settingsManager).ConfigureAwait(false));
        // 系统主题先按灰度起，等终端回报颜色后再换。
        ThemeRuntime.MarkTerminalColorsPending();
        ThemeRuntime.InitTheme(
            ThemeRuntime.ResolveThemeSetting(settingsManager.GetThemeSetting(), ThemeRuntime.GetTerminalTheme())
                ?? SystemTheme.SystemThemeName);
        GlobalKeybindings.Set(Core.KeybindingsManager.Create());
        ITui ui = new TuiMainScreen(new ProcessTerminal(), settingsManager.GetShowHardwareCursor());
        ui.SetClearOnShrink(settingsManager.GetClearOnShrink());
        return ui;
    }

    /// <summary>对应 TS <c>startStartupTui</c>：启动后异步等终端颜色，到了再切主题。</summary>
    public static void StartStartupTui(ITui ui, SettingsManager settingsManager)
    {
        ui.Start();
        var themeSetting = settingsManager.GetThemeSetting();
        QueryStartupTerminalColors(ui, () =>
            _ = ThemeRuntime.SetTheme(
                ThemeRuntime.ResolveThemeSetting(themeSetting, ThemeRuntime.GetTerminalTheme())
                    ?? SystemTheme.SystemThemeName));
    }

    /// <summary>
    /// 对应 TS <c>queryStartupTerminalColors</c>：不等颜色回来；到达（或超时）后记录终端色、
    /// 跑 <paramref name="onColors"/> 并重绘。
    /// </summary>
    private static void QueryStartupTerminalColors(ITui ui, Action onColors)
    {
        _ = InteractiveThemeController.RequestTerminalColorsAsync(ui, colors =>
        {
            ThemeRuntime.SetTerminalColors(colors);
            onColors();
            ui.Invalidate();
            ui.RequestRender();
        });
    }

    /// <summary>对应 TS <c>clearStartupTui</c>：清屏 + 重绘 + 25ms 让终端吃掉写入。</summary>
    private static async Task ClearStartupTuiAsync(ITui ui)
    {
        ui.Clear();
        ui.RequestRender();
        await Task.Delay(25).ConfigureAwait(false);
    }

    /// <summary>
    /// 首次设置是否运行。对应 TS <c>shouldRunFirstTimeSetup</c>：官方发行版 +
    /// 实验特性开启 + 未自定义 agent 目录 + settings.json 尚不存在。
    /// </summary>
    public static bool ShouldRunFirstTimeSetup(string? settingsPath = null)
    {
        settingsPath ??= Config.GetSettingsPath();
        if (!IsOfficialDistribution(Config.PackageName, Config.AppName, Config.ConfigDirName))
        {
            return false;
        }

        if (!Experimental.AreExperimentalFeaturesEnabled())
        {
            return false;
        }

        // TS `if (process.env[ENV_AGENT_DIR])`——空串为假，故用 IsNullOrEmpty。
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Config.EnvAgentDir)))
        {
            return false;
        }

        // existsSync 对目录也为真。
        return !File.Exists(settingsPath) && !Directory.Exists(settingsPath);
    }

    /// <summary>对应 TS <c>showStartupSelector</c>：返回选中项的值，取消返回 <c>default</c>。</summary>
    public static async Task<T?> ShowStartupSelectorAsync<T>(
        SettingsManager settingsManager,
        string title,
        IReadOnlyList<StartupSelectorOption<T>> options)
    {
        var ui = await CreateStartupTuiAsync(settingsManager).ConfigureAwait(false);
        var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = false;

        async Task FinishAsync(T? result)
        {
            if (settled)
            {
                return;
            }

            settled = true;
            await ClearStartupTuiAsync(ui).ConfigureAwait(false);
            ui.Stop();
            completion.TrySetResult(result);
        }

        var selector = new ExtensionSelectorComponent(
            title,
            options.Select(option => option.Label).ToList(),
            option =>
            {
                var match = options.FirstOrDefault(entry => entry.Label == option);
                _ = FinishAsync(match is null ? default : match.Value);
            },
            () => _ = FinishAsync(default),
            new ExtensionSelectorOptions { Tui = ui });
        ui.AddChild(selector);
        ui.SetFocus(selector);
        StartStartupTui(ui, settingsManager);
        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>对应 TS <c>showFirstTimeSetup</c>：跑首次设置对话框并持久化结果。</summary>
    public static async Task ShowFirstTimeSetupAsync(SettingsManager settingsManager)
    {
        var ui = await CreateStartupTuiAsync(settingsManager).ConfigureAwait(false);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = false;

        async Task FinishAsync(FirstTimeSetupResult? result)
        {
            if (settled)
            {
                return;
            }

            settled = true;
            if (result is not null)
            {
                settingsManager.SetTheme(result.Theme);
                settingsManager.SetEnableAnalytics(result.ShareAnalytics);
                await settingsManager.FlushAsync().ConfigureAwait(false);
            }

            await ClearStartupTuiAsync(ui).ConfigureAwait(false);
            ui.Stop();
            completion.TrySetResult();
        }

        ui.Start();
        var previewTheme = SystemTheme.SystemThemeName;
        _ = ThemeRuntime.SetTheme(previewTheme);
        var component = new FirstTimeSetupComponent(new FirstTimeSetupOptions
        {
            OnThemePreview = themeName =>
            {
                previewTheme = themeName;
                _ = ThemeRuntime.SetTheme(themeName);
                ui.RequestRender();
            },
            OnSubmit = result => _ = FinishAsync(result),
            OnCancel = () => _ = FinishAsync(null),
        });
        ui.AddChild(component);
        ui.SetFocus(component);
        ui.RequestRender();
        // 终端颜色回来后会重建系统主题；重绘让对话框用上新主题。
        QueryStartupTerminalColors(ui, () => _ = ThemeRuntime.SetTheme(previewTheme));
        await completion.Task.ConfigureAwait(false);
    }

    /// <summary>对应 TS <c>showStartupInput</c>：返回输入值，取消返回 <c>null</c>。</summary>
    public static async Task<string?> ShowStartupInputAsync(
        SettingsManager settingsManager,
        string title,
        string? placeholder = null)
    {
        var ui = await CreateStartupTuiAsync(settingsManager).ConfigureAwait(false);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = false;
        // TS 里 `const input` 在 finish 之后声明、靠闭包捕获；C# 需先声明变量（回调只会在
        // 构造完成后触发，故此处非空）。
        ExtensionInputComponent? input = null;

        async Task FinishAsync(string? result)
        {
            if (settled)
            {
                return;
            }

            settled = true;
            input?.Dispose();
            await ClearStartupTuiAsync(ui).ConfigureAwait(false);
            ui.Stop();
            completion.TrySetResult(result);
        }

        input = new ExtensionInputComponent(
            title,
            placeholder,
            value => _ = FinishAsync(value),
            () => _ = FinishAsync(null),
            new ExtensionInputOptions { Tui = ui });
        ui.AddChild(input);
        ui.SetFocus(input);
        StartStartupTui(ui, settingsManager);
        return await completion.Task.ConfigureAwait(false);
    }
}
