// ============================================================================
// InteractiveMode — the /settings and /scoped-models selector mounts.
//
// Port of interactive-mode.ts's showSettingsSelector (L4888-5115) and
// showModelsSelector (L5357-5465). Both are reached from the submit if-chain
// (InteractiveModeSubmit.cs); the components themselves are ported separately
// (SettingsSelector / ScopedModelsSelector).
// ============================================================================

using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive;

public partial class InteractiveMode
{
    /// <summary>TS <c>showSettingsSelector</c> (L4888-5115)。</summary>
    internal void ShowSettingsSelector()
    {
        ShowSelector(done =>
        {
            SettingsSelectorComponent? selector = null;
            var defaultProvider = SettingsManager.GetDefaultProvider();
            var defaultModelId = SettingsManager.GetDefaultModel();
            var defaultModel = defaultProvider is not null && defaultModelId is not null
                ? $"{defaultProvider}/{defaultModelId}"
                : "not set";
            selector = new SettingsSelectorComponent(
                new SettingsConfig
                {
                    AutoCompact = Session.AutoCompactionEnabled,
                    DefaultModel = defaultModel,
                    CurrentModel = Session.Model,
                    AvailableDefaultModels = Session.ModelRuntime.GetAvailableSnapshot(),
                    ShowImages = SettingsManager.GetShowImages(),
                    ImageWidthCells = (int)SettingsManager.GetImageWidthCells(),
                    AutoResizeImages = SettingsManager.GetImageAutoResize(),
                    BlockImages = SettingsManager.GetBlockImages(),
                    EnableSkillCommands = SettingsManager.GetEnableSkillCommands(),
                    SteeringMode = Session.SteeringMode,
                    FollowUpMode = Session.FollowUpMode,
                    Transport = SettingsManager.GetTransport(),
                    HttpIdleTimeoutMs = SettingsManager.GetHttpIdleTimeoutMs(),
                    CacheWarmingMode = SettingsManager.GetCacheWarmingMode(),
                    ThinkingLevel = SettingsManager.GetDefaultThinkingLevel()
                        ?? ThinkingLevelText.ToText(Defaults.DefaultThinkingLevel),
                    AvailableThinkingLevels = [.. Defaults.ThinkingLevelOptions.Select(ThinkingLevelText.ToText)],
                    ModelThinkingLevels = SettingsManager.GetAllModelThinkingLevels(),
                    CurrentTheme = _themeController.GetThemeSelection() ?? SystemTheme.SystemThemeName,
                    TerminalTheme = _themeController.GetTerminalTheme(),
                    AvailableThemes = ThemeRuntime.GetAvailableThemes(),
                    HideThinkingBlock = _hideThinkingBlock,
                    MermaidRenderingMode = SettingsManager.GetMermaidRenderingMode(),
                    ShowCacheMissNotices = SettingsManager.GetShowCacheMissNotices(),
                    CollapseChangelog = SettingsManager.GetCollapseChangelog(),
                    EnableInstallTelemetry = SettingsManager.GetEnableInstallTelemetry(),
                    DoubleEscapeAction = SettingsManager.GetDoubleEscapeAction(),
                    TreeFilterMode = SettingsManager.GetTreeFilterMode(),
                    ShowHardwareCursor = SettingsManager.GetShowHardwareCursor(),
                    DefaultProjectTrust = SettingsManager.GetDefaultProjectTrust(),
                    EditorPaddingX = (int)SettingsManager.GetEditorPaddingX(),
                    OutputPad = _outputPad,
                    AutocompleteMaxVisible = (int)SettingsManager.GetAutocompleteMaxVisible(),
                    QuietStartup = SettingsManager.GetQuietStartup(),
                    ClearOnShrink = SettingsManager.GetClearOnShrink(),
                    ShowTerminalProgress = SettingsManager.GetShowTerminalProgress(),
                    TuiMode = _ui.Mode == TuiMode.Fullscreen ? "fullscreen" : "regular",
                    FullscreenExitOutput = SettingsManager.GetFullscreenExitOutput(),
                    FullscreenScrollbar = SettingsManager.GetFullscreenScrollbar(),
                    FullscreenCopyOnSelect = SettingsManager.GetFullscreenCopyOnSelect(),
                    FullscreenWheelScrollLines = SettingsManager.GetFullscreenWheelScrollLines(),
                    Warnings = SettingsManager.GetWarnings(),
                },
                new SettingsCallbacks
                {
                    OnAutoCompactChange = enabled =>
                    {
                        Session.SetAutoCompactionEnabled(enabled);
                        _footer.SetAutoCompactEnabled(enabled);
                    },
                    OnShowImagesChange = enabled =>
                    {
                        SettingsManager.SetShowImages(enabled);
                        foreach (var child in _chatContainer.GetChildrenSnapshot())
                        {
                            if (child is ToolExecutionComponent tool)
                            {
                                tool.SetShowImages(enabled);
                            }
                        }
                    },
                    OnImageWidthCellsChange = width =>
                    {
                        SettingsManager.SetImageWidthCells(width);
                        foreach (var child in _chatContainer.GetChildrenSnapshot())
                        {
                            if (child is ToolExecutionComponent tool)
                            {
                                tool.SetImageWidthCells(width);
                            }
                        }
                    },
                    OnAutoResizeImagesChange = enabled => SettingsManager.SetImageAutoResize(enabled),
                    OnBlockImagesChange = blocked => SettingsManager.SetBlockImages(blocked),
                    OnEnableSkillCommandsChange = enabled =>
                    {
                        SettingsManager.SetEnableSkillCommands(enabled);
                        SetupAutocompleteProvider();
                    },
                    OnSteeringModeChange = mode => Session.SetSteeringMode(mode),
                    OnFollowUpModeChange = mode => Session.SetFollowUpMode(mode),
                    OnTransportChange = transport =>
                    {
                        SettingsManager.SetTransport(transport);
                        Session.Agent.Transport = transport;
                    },
                    OnHttpIdleTimeoutMsChange = timeoutMs =>
                    {
                        SettingsManager.SetHttpIdleTimeoutMs(timeoutMs);
                        // TS 还调用 configureHttpDispatcher(timeoutMs)（进程级 undici dispatcher）；
                        // 端口无全局 dispatcher——请求超时按请求解析（设计差异 C267）。
                        ShowStatus($"HTTP idle timeout: {HttpDispatcher.FormatHttpIdleTimeoutMs(timeoutMs)}");
                    },
                    OnCacheWarmingModeChange = mode =>
                    {
                        Session.SetCacheWarmingMode(mode);
                        ShowStatus($"Cache warming: {mode}");
                    },
                    OnModelThinkingLevelChange = (provider, modelId, level) =>
                    {
                        if (ThinkingLevelText.Parse(level) is not { } parsed)
                        {
                            return;
                        }

                        SettingsManager.SetModelThinkingLevel(provider, modelId, parsed);
                        // If the override is for the current model, apply it to the session too
                        if (Session.Model is { } current && current.Provider == provider && current.Id == modelId)
                        {
                            Session.SetThinkingLevel(parsed);
                            _footer.Invalidate();
                            UpdateEditorBorderColor();
                        }
                    },
                    OnModelThinkingLevelRemove = (provider, modelId) =>
                    {
                        SettingsManager.RemoveModelThinkingLevel(provider, modelId);
                        // If the override was for the current model, revert to global default
                        if (Session.Model is { } current && current.Provider == provider && current.Id == modelId)
                        {
                            var globalDefault = ThinkingLevelText.Parse(SettingsManager.GetDefaultThinkingLevel())
                                ?? Defaults.DefaultThinkingLevel;
                            Session.SetThinkingLevel(globalDefault);
                            _footer.Invalidate();
                            UpdateEditorBorderColor();
                        }
                    },
                    OnThemeChange = themeSetting =>
                    {
                        SettingsManager.SetTheme(themeSetting);
                        _themeController.SetThemeSetting(themeSetting);
                    },
                    OnThemePreview = themeName => _themeController.Preview(themeName),
                    OnHideThinkingBlockChange = hidden =>
                    {
                        _hideThinkingBlock = hidden;
                        SettingsManager.SetHideThinkingBlock(hidden);
                        UpdateThinkingBlockVisibility();
                    },
                    OnMermaidRenderingModeChange = mode =>
                    {
                        SettingsManager.SetMermaidRenderingMode(mode);
                        _chatContainer.Invalidate();
                        _ui.RequestRender();
                    },
                    OnShowCacheMissNoticesChange = shown =>
                    {
                        SettingsManager.SetShowCacheMissNotices(shown);
                        RebuildChatFromMessages();
                    },
                    OnCollapseChangelogChange = collapsed => SettingsManager.SetCollapseChangelog(collapsed),
                    OnEnableInstallTelemetryChange = enabled => SettingsManager.SetEnableInstallTelemetry(enabled),
                    OnQuietStartupChange = value =>
                    {
                        // TS 的 QuietStartup 联合是 false | true | "header"；端口按字符串三分。
                        if (value == "header")
                        {
                            SettingsManager.SetQuietStartupHeader();
                        }
                        else
                        {
                            SettingsManager.SetQuietStartup(value == "true");
                        }
                    },
                    OnDefaultProjectTrustChange = trust => SettingsManager.SetDefaultProjectTrust(trust),
                    OnDoubleEscapeActionChange = action => SettingsManager.SetDoubleEscapeAction(action),
                    OnTreeFilterModeChange = mode => SettingsManager.SetTreeFilterMode(mode),
                    OnShowHardwareCursorChange = enabled =>
                    {
                        SettingsManager.SetShowHardwareCursor(enabled);
                        _ui.SetShowHardwareCursor(enabled);
                    },
                    OnEditorPaddingXChange = padding =>
                    {
                        SettingsManager.SetEditorPaddingX(padding);
                        _defaultEditor.SetPaddingX(padding);
                        if (!ReferenceEquals(_editor, _defaultEditor))
                        {
                            _editor.SetPaddingX(padding);
                        }
                    },
                    OnOutputPadChange = padding =>
                    {
                        SettingsManager.SetOutputPad(padding);
                        _outputPad = padding;
                        foreach (var container in new[] { _chatContainer, _pendingMessagesContainer })
                        {
                            foreach (var child in container.GetChildrenSnapshot())
                            {
                                if (child is IOutputPadSettable padSettable)
                                {
                                    padSettable.SetOutputPad(padding);
                                }
                            }
                        }

                        _ui.RequestRender();
                    },
                    OnAutocompleteMaxVisibleChange = maxVisible =>
                    {
                        SettingsManager.SetAutocompleteMaxVisible(maxVisible);
                        _defaultEditor.SetAutocompleteMaxVisible(maxVisible);
                        if (!ReferenceEquals(_editor, _defaultEditor))
                        {
                            _editor.SetAutocompleteMaxVisible(maxVisible);
                        }
                    },
                    OnClearOnShrinkChange = enabled =>
                    {
                        SettingsManager.SetClearOnShrink(enabled);
                        _ui.SetClearOnShrink(enabled);
                        if (!enabled && _activeStatusIndicator is null)
                        {
                            _statusContainer.Clear();
                        }
                    },
                    OnShowTerminalProgressChange = enabled => SettingsManager.SetShowTerminalProgress(enabled),
                    OnTuiModeChange = mode =>
                    {
                        if (!SwitchTuiMode(mode == "fullscreen" ? TuiMode.Fullscreen : TuiMode.Regular))
                        {
                            selector?.GetSettingsList().UpdateValue("tui-mode", _ui.Mode == TuiMode.Fullscreen ? "fullscreen" : "regular");
                            ShowStatus("Close active overlays before changing TUI mode");
                            return;
                        }

                        SettingsManager.SetTuiMode(mode == "fullscreen" ? TuiMode.Fullscreen : TuiMode.Regular);
                        if (_activeStatusIndicator is null)
                        {
                            _statusContainer.Clear();
                        }

                        ShowStatus($"TUI mode: {mode}");
                    },
                    OnFullscreenExitOutputChange = output => SettingsManager.SetFullscreenExitOutput(output),
                    OnFullscreenScrollbarChange = mode =>
                    {
                        SettingsManager.SetFullscreenScrollbar(mode);
                        ApplyFullscreenScrollbarSetting();
                    },
                    OnFullscreenCopyOnSelectChange = enabled =>
                    {
                        SettingsManager.SetFullscreenCopyOnSelect(enabled);
                        if (_renderer is TuiAltScreen altScreen)
                        {
                            altScreen.SetCopyOnSelect(enabled);
                        }
                    },
                    OnFullscreenWheelScrollLinesChange = lines =>
                    {
                        SettingsManager.SetFullscreenWheelScrollLines(lines);
                        if (_renderer is TuiAltScreen altScreen)
                        {
                            altScreen.SetWheelScrollLines(lines);
                        }
                    },
                    OnWarningsChange = warnings => SettingsManager.SetWarnings(warnings),
                    OnCancel = () =>
                    {
                        done();
                        _ui.RequestRender();
                    },
                });
            return (selector, selector.GetSettingsList(), null);
        });
    }

    /// <summary>TS <c>showModelsSelector</c> (L5357-5465)。</summary>
    /// <remarks>
    /// TS 的该函数返回 void：目录刷新在后台跑，选择器先挂载。端口保持同构（方法签名为 void），
    /// 提交 if-chain 因此不需要 await 它。
    /// </remarks>
    internal void ShowModelsSelector()
    {
        var availableModels = Session.ModelRuntime.GetAvailableSnapshot().ToList();
        var availableModelIds = availableModels.Select(model => $"{model.Provider}/{model.Id}").ToHashSet(StringComparer.Ordinal);
        var configuredPatterns = SettingsManager.GetEnabledModels();
        var sessionScopedModels = Session.ScopedModels;

        IReadOnlyList<string>? ConfiguredEnabledIds(IReadOnlyList<ModelSpec> models)
        {
            if (configuredPatterns is not { Count: > 0 })
            {
                return null;
            }

            var resolved = ModelResolver.ResolveModelScopeFromModels(configuredPatterns, models);
            var ids = resolved.ScopedModels
                .Select(scoped => $"{scoped.Model.Provider}/{scoped.Model.Id}")
                .ToList();
            foreach (var diagnostic in resolved.Diagnostics)
            {
                if (diagnostic.Code == ModelScopeDiagnostic.Codes.NoMatch && !ids.Contains(diagnostic.Pattern))
                {
                    ids.Add(diagnostic.Pattern);
                }
            }

            return ids;
        }

        var currentEnabledIds = sessionScopedModels.Count > 0
            ? sessionScopedModels.Select(scoped => $"{scoped.Model.Provider}/{scoped.Model.Id}").ToList()
            : ConfiguredEnabledIds(availableModels);
        var selectionChanged = false;

        void UpdateSessionModels(IReadOnlyList<string>? enabledIds)
        {
            currentEnabledIds = enabledIds is null ? null : [.. enabledIds];
            var hasEnabledAvailableModel = enabledIds?.Any(id => availableModelIds.Contains(id)) ?? false;
            var allAvailableModelsEnabled =
                enabledIds is not null && availableModelIds.All(id => enabledIds.Contains(id));
            if (enabledIds is not null && hasEnabledAvailableModel && !allAvailableModelsEnabled)
            {
                var newScopedModels = ModelResolver
                    .ResolveModelScopeFromModels(enabledIds, availableModels)
                    .ScopedModels;
                Session.SetScopedModels(newScopedModels);
            }
            else
            {
                Session.SetScopedModels([]);
            }

            UpdateAvailableProviderCount();
            _ui.RequestRender();
        }

        ShowSelector(done =>
        {
            var disposed = false;
            var timedOut = false;
            var controller = new CancellationTokenSource(15_000);
            var selector = new ScopedModelsSelectorComponent(
                new ModelsConfig
                {
                    AllModels = availableModels,
                    EnabledModelIds = currentEnabledIds,
                    RefreshStatus = "Refreshing model catalogs…",
                },
                new ModelsCallbacks
                {
                    OnChange = enabledIds =>
                    {
                        selectionChanged = true;
                        UpdateSessionModels(enabledIds);
                    },
                    OnPersist = enabledIds =>
                    {
                        var allEnabled =
                            enabledIds is not null
                            && enabledIds.Count == availableModels.Count
                            && enabledIds.All(id => availableModelIds.Contains(id));
                        var newPatterns = enabledIds is null || allEnabled ? null : enabledIds;
                        SettingsManager.SetEnabledModels(newPatterns is null ? null : [.. newPatterns]);
                        ShowStatus("Model selection saved to settings");
                    },
                    OnCancel = () =>
                    {
                        done();
                        _ui.RequestRender();
                    },
                });

            _ = ModelCatalogRefresh.RefreshAsync(Session.ModelRuntime, controller.Token)
                .ContinueWith(
                    task =>
                    {
                        // 与 TS 的 .then/.catch/.finally 链等价：三态各自收尾，finally 统一清计时器。
                        try
                        {
                            if (disposed)
                            {
                                return;
                            }

                            if (task.IsFaulted)
                            {
                                var error = task.Exception?.InnerException;
                                selector.SetRefreshStatus(
                                    timedOut
                                        ? "Model refresh timed out; showing cached models."
                                        : $"Could not refresh model catalogs: {error?.Message}",
                                    ScopedModelsSelectorComponent.StatusKind.Warning);
                                _ui.RequestRender();
                                return;
                            }

                            var result = task.Result;
                            availableModels = [.. Session.ModelRuntime.GetAvailableSnapshot()];
                            availableModelIds = availableModels
                                .Select(model => $"{model.Provider}/{model.Id}")
                                .ToHashSet(StringComparer.Ordinal);
                            if (!selectionChanged && sessionScopedModels.Count == 0)
                            {
                                currentEnabledIds = ConfiguredEnabledIds(availableModels);
                                selector.UpdateModels(availableModels, currentEnabledIds);
                            }
                            else
                            {
                                selector.UpdateModels(availableModels);
                            }

                            if (currentEnabledIds is not null)
                            {
                                UpdateSessionModels(currentEnabledIds);
                            }

                            if (result.Aborted && timedOut)
                            {
                                selector.SetRefreshStatus(
                                    "Model refresh timed out; showing cached models.",
                                    ScopedModelsSelectorComponent.StatusKind.Warning);
                            }
                            else if (result.Errors.Count > 0)
                            {
                                selector.SetRefreshStatus(
                                    $"Could not refresh {string.Join(", ", result.Errors.Keys)}; showing cached models.",
                                    ScopedModelsSelectorComponent.StatusKind.Warning);
                            }
                            else
                            {
                                selector.SetRefreshStatus(
                                    "Model catalogs refreshed.",
                                    ScopedModelsSelectorComponent.StatusKind.Success);
                            }

                            _ui.RequestRender();
                        }
                        finally
                        {
                            controller.Dispose();
                        }
                    },
                    TaskScheduler.Default);

            return (selector, selector, () =>
            {
                disposed = true;
                controller.Cancel();
                controller.Dispose();
            });
        });
    }
}
