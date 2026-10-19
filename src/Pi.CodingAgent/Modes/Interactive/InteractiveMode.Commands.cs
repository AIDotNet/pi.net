// ============================================================================
// InteractiveMode — command family, first slice of 4f-4m (batch 4f-4m-5a).
//
// Port of interactive-mode.ts's thinking / model / clone command handlers and
// the trust selector:
//   handleThinkingCommand (L5124-5139) + selectThinkingLevel (L5141-5150) +
//   showThinkingSelector (L5152-5171), handleModelCommand (L5173-5194) +
//   findExactModelMatch (L5196-5228), maybeSaveImplicitProjectTrustAfterReload
//   (L5270-5294), showTrustSelector (L5296-5319), handleCloneCommand (L5517-5536).
//
// The login family (L5767-6397) and the remaining command handlers land with
// batches 4f-4m-5b / 4f-4m-5c; the submit if-chain that dispatches to all of
// them lands with 4f-4m-6.
// ============================================================================

using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Modes.Interactive.Components;
using JsNumber = Pi.CodingAgent.Utils.JsNumber;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive;

public partial class InteractiveMode
{
    /// <summary>TS <c>handleThinkingCommand</c> (L5124-5139).</summary>
    internal void HandleThinkingCommand(string? searchTerm)
    {
        var availableLevels = Session.GetAvailableThinkingLevels();
        if (string.IsNullOrEmpty(searchTerm))
        {
            ShowThinkingSelector();
            return;
        }

        var normalized = searchTerm.Trim().ToLowerInvariant();
        ThinkingLevel? matched = null;
        foreach (var candidate in availableLevels)
        {
            // TS compares `candidate.toLowerCase()`; the wire text is already lowercase.
            if (string.Equals(ThinkingLevelText.ToText(candidate), normalized, StringComparison.Ordinal))
            {
                matched = candidate;
                break;
            }
        }

        if (matched is null)
        {
            var names = string.Join(", ", availableLevels.Select(ThinkingLevelText.ToText));
            ShowError($"Unknown thinking level \"{searchTerm}\". Available levels: {names}.");
            return;
        }

        SelectThinkingLevel(matched.Value, persist: false);
    }

    /// <summary>TS <c>selectThinkingLevel</c> (L5141-5150).</summary>
    private void SelectThinkingLevel(ThinkingLevel level, bool persist)
    {
        try
        {
            Session.SetThinkingLevel(level, new ModelMutationOptions { Persist = persist });
            _footer.Invalidate();
            UpdateEditorBorderColor();
            ShowStatus(persist ? $"Default thinking level: {ThinkingLevelText.ToText(level)}" : $"Thinking level: {ThinkingLevelText.ToText(level)}");
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    /// <summary>TS <c>showThinkingSelector</c> (L5152-5171).</summary>
    private void ShowThinkingSelector()
    {
        ShowSelector(done =>
        {
            void SelectLevel(ThinkingLevel level, bool persist)
            {
                SelectThinkingLevel(level, persist);
                done();
            }

            // TS reads `session.thinkingLevel ?? DEFAULT_THINKING_LEVEL`; the port's
            // Agent.ThinkingLevel is never null (defaults to Off), so the fallback is inert.
            var selector = new ThinkingSelectorComponent(
                Session.ThinkingLevel,
                Session.GetAvailableThinkingLevels(),
                level => SelectLevel(level, persist: false),
                () =>
                {
                    done();
                    _ui.RequestRender();
                },
                level => SelectLevel(level, persist: true),
                ThinkingLevelText.Parse(SettingsManager.GetDefaultThinkingLevel()) ?? Defaults.DefaultThinkingLevel);
            return (selector, selector, null);
        });
    }

    /// <summary>TS <c>handleModelCommand</c> (L5173-5194).</summary>
    internal async Task HandleModelCommandAsync(string? searchTerm)
    {
        if (string.IsNullOrEmpty(searchTerm))
        {
            ShowModelSelector();
            return;
        }

        var model = await FindExactModelMatchAsync(searchTerm).ConfigureAwait(true);
        if (model is not null)
        {
            try
            {
                await Session.SetModelAsync(model, new ModelMutationOptions { Persist = false }).ConfigureAwait(true);
                _footer.Invalidate();
                UpdateEditorBorderColor();
                ShowStatus($"Model: {model.Id}");
                _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync(model);
            }
            catch (Exception error)
            {
                ShowError(error.Message);
            }

            return;
        }

        ShowModelSelector(searchTerm);
    }

    /// <summary>TS <c>findExactModelMatch</c> (L5196-5228).</summary>
    private async Task<ModelSpec?> FindExactModelMatchAsync(string searchTerm)
    {
        var cachedModels = Session.ScopedModels.Count > 0
            ? Session.ScopedModels.Select(scoped => scoped.Model).ToList()
            : Session.ModelRuntime.GetAvailableSnapshot().ToList();
        var cachedMatch = ModelResolver.FindExactModelReferenceMatch(searchTerm, cachedModels);
        if (cachedMatch is not null || Session.ScopedModels.Count > 0)
        {
            return cachedMatch;
        }

        ShowStatus("Refreshing model catalogs…");
        using var cts = new CancellationTokenSource();
        var timedOut = false;
        // TS aborts the controller from the timeout callback right after setting the flag;
        // the local controller has no other abort source, so `result.Aborted` implies the
        // timeout fired — the flag is kept for a line-by-line reading of the TS source.
        using var timer = new Timer(_ =>
        {
            timedOut = true;
            cts.Cancel();
        }, null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
        try
        {
            var result = await ModelCatalogRefresh.RefreshAsync(Session.ModelRuntime, cts.Token).ConfigureAwait(true);
            if (result.Aborted && timedOut)
            {
                ShowWarning("Model refresh timed out; searching cached models.");
            }
            else if (result.Errors.Count > 0)
            {
                ShowWarning($"Could not refresh {string.Join(", ", result.Errors.Keys)}; searching cached models.");
            }
        }
        catch (Exception error)
        {
            ShowWarning(timedOut
                ? "Model refresh timed out; searching cached models."
                : $"Could not refresh model catalogs: {error.Message}");
        }

        return ModelResolver.FindExactModelReferenceMatch(
            searchTerm, Session.ModelRuntime.GetAvailableSnapshot().ToList());
    }

    /// <summary>TS <c>maybeSaveImplicitProjectTrustAfterReload</c> (L5270-5294).</summary>
    private bool MaybeSaveImplicitProjectTrustAfterReload()
    {
        var cwd = SessionManager.GetCwd();
        if (_autoTrustOnReloadCwd != cwd)
        {
            return false;
        }

        if (!SettingsManager.IsProjectTrusted() || !TrustManager.HasTrustRequiringProjectResources(cwd))
        {
            return false;
        }

        var trustStore = new ProjectTrustStore(_runtimeHost.Services.AgentDir);
        try
        {
            if (trustStore.Get(cwd) is not null)
            {
                _autoTrustOnReloadCwd = null;
                return false;
            }

            trustStore.Set(cwd, true);
            _autoTrustOnReloadCwd = null;
            return true;
        }
        catch (Exception error)
        {
            ShowWarning($"Could not save project trust after reload: {error.Message}");
            return false;
        }
    }

    /// <summary>TS <c>showTrustSelector</c> (L5296-5319).</summary>
    /// <remarks>
    /// TS keeps this member private; widened to internal for the test suite
    /// (InternalsVisibleTo), following the read-only observation seams the earlier
    /// 4f batches added to their components. The submit if-chain (4f-4m-6) reaches
    /// it through the <c>/trust</c> command.
    /// </remarks>
    internal void ShowTrustSelector()
    {
        var cwd = SessionManager.GetCwd();
        var trustStore = new ProjectTrustStore(_runtimeHost.Services.AgentDir);
        var savedDecision = trustStore.GetEntry(cwd);
        ShowSelector(done =>
        {
            var selector = new TrustSelectorComponent(new TrustSelectorOptions
            {
                Cwd = cwd,
                SavedDecision = savedDecision,
                ProjectTrusted = SettingsManager.IsProjectTrusted(),
                OnSelect = selection =>
                {
                    trustStore.SetMany(selection.Updates);
                    done();
                    ShowStatus(
                        $"Saved trust decision: {(selection.Trusted ? "trusted" : "untrusted")}. "
                        + $"Restart {Config.AppName} for this to take effect.");
                },
                OnCancel = () =>
                {
                    done();
                    _ui.RequestRender();
                },
            });
            return (selector, selector, null);
        });
    }

    /// <summary>TS <c>handleCloneCommand</c> (L5517-5536).</summary>
    internal async Task HandleCloneCommandAsync()
    {
        var leafId = SessionManager.GetLeafId();
        if (leafId is null)
        {
            ShowStatus("Nothing to clone yet");
            return;
        }

        try
        {
            var result = await _runtimeHost.ForkAsync(leafId, new ForkOptions { Position = ForkPosition.At })
                .ConfigureAwait(true);
            if (result.Cancelled)
            {
                _ui.RequestRender();
                return;
            }

            _editor.SetText("");
            ShowStatus("Cloned to new session");
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    /// <summary>TS <c>handleReloadCommand</c> (L6403-6491).</summary>
    internal async Task HandleReloadCommandAsync()    {
        if (Session.IsStreaming)
        {
            ShowWarning("Wait for the current response to finish before reloading.");
            return;
        }

        if (Session.IsCompacting)
        {
            ShowWarning("Wait for compaction to finish before reloading.");
            return;
        }

        ResetExtensionUI();

        var reloadBox = new Container();
        var borderColor = (string text) => ThemeRuntime.Current.Fg("border", text);
        reloadBox.AddChild(new DynamicBorder(borderColor));
        reloadBox.AddChild(new Spacer(1));
        reloadBox.AddChild(new ThemedText(
            () => ThemeRuntime.Current.Fg(
                "muted",
                "Reloading keybindings, extensions, skills, prompts, themes, and context files..."),
            1,
            0));
        reloadBox.AddChild(new Spacer(1));
        reloadBox.AddChild(new DynamicBorder(borderColor));

        var previousEditor = _editor;
        _editorContainer.Clear();
        _editorContainer.AddChild(reloadBox);
        _ui.SetFocus(reloadBox);
        _ui.RequestRender(true);
        await Task.Yield();

        void DismissReloadBox(Editor editor)
        {
            _editorContainer.Clear();
            _editorContainer.AddChild(editor);
            _ui.SetFocus(editor);
            _ui.RequestRender();
        }

        var chatRestoredBeforeSessionStart = false;
        var reloadBoxDismissed = false;
        void RestoreChatBeforeSessionStart()
        {
            if (chatRestoredBeforeSessionStart)
            {
                return;
            }

            _hideThinkingBlock = SettingsManager.GetHideThinkingBlock();
            _outputPad = (int)SettingsManager.GetOutputPad();
            RebuildChatFromMessages();
            chatRestoredBeforeSessionStart = true;
        }

        try
        {
            await Session.ReloadAsync(new ReloadOptions
            {
                BeforeSessionStart = () =>
                {
                    RestoreChatBeforeSessionStart();
                    return Task.CompletedTask;
                },
            }).ConfigureAwait(true);
            RestoreChatBeforeSessionStart();
            _keybindings.Reload();
            var activeHeader = _customHeader ?? _builtInHeader;
            if (activeHeader is IExpandable expandableHeader)
            {
                expandableHeader.SetExpanded(_toolOutputExpanded);
            }

            ThemeRuntime.SetRegisteredThemes([.. Session.ResourceLoader.Themes.Select(LoadRegisteredTheme)]);
            ApplyRuntimeSettings();
            _themeController.ApplyFromSettings();
            SetupAutocompleteProvider();
            var runner = Session.ExtensionRunner;
            SetupExtensionShortcuts(runner);
            ShowLoadedResources(new LoadedResourcesOptions { Force = false, ShowDiagnosticsWhenQuiet = true });
            var savedImplicitProjectTrust = MaybeSaveImplicitProjectTrustAfterReload();
            var modelsJsonError = Session.ModelRuntime.GetError();
            if (modelsJsonError is not null)
            {
                ShowError($"models.json error: {modelsJsonError}");
            }

            ShowStatus(savedImplicitProjectTrust
                ? "Reloaded keybindings, extensions, skills, prompts, themes, and context files; saved project trust"
                : "Reloaded keybindings, extensions, skills, prompts, themes, and context files");
            DismissReloadBox(_editor);
            reloadBoxDismissed = true;
        }
        catch (Exception error)
        {
            if (!reloadBoxDismissed)
            {
                DismissReloadBox(previousEditor);
            }

            ShowError($"Reload failed: {error.Message}");
        }
    }

    /// <summary>TS <c>handleExportCommand</c> (L6493-6509).</summary>
    internal async Task HandleExportCommandAsync(string text)
    {
        var outputPath = InteractiveModeCommands.GetPathCommandArgument(text, "/export");

        try
        {
            if (outputPath is not null && outputPath.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                var filePath = SessionExport.ExportSessionToJsonl(SessionManager, outputPath, null);
                ShowStatus($"Session exported to: {filePath}");
            }
            else
            {
                var filePath = ExportHtml.ExportSessionToHtml(SessionManager, new ExportHtmlOptions
                {
                    OutputPath = outputPath,
                    ThemeName = ThemeRuntime.Current.Name,
                });
                ShowStatus($"Session exported to: {filePath}");
            }
        }
        catch (Exception error)
        {
            ShowError($"Failed to export session: {error.Message}");
        }
    }

    /// <summary>TS <c>handleImportCommand</c> (L6540-6582).</summary>
    internal async Task HandleImportCommandAsync(string text)
    {
        var inputPath = InteractiveModeCommands.GetPathCommandArgument(text, "/import");
        if (inputPath is null)
        {
            ShowError("Usage: /import <path.jsonl>");
            return;
        }

        var confirmed = await ShowExtensionConfirmAsync("Import session", $"Replace current session with {inputPath}?")
            .ConfigureAwait(true);
        if (!confirmed)
        {
            ShowStatus("Import cancelled");
            return;
        }

        try
        {
            ClearStatusIndicator();
            var result = await _runtimeHost.ImportFromJsonlAsync(inputPath).ConfigureAwait(true);
            if (result.Cancelled)
            {
                ShowStatus("Import cancelled");
                return;
            }

            ShowStatus($"Session imported from: {inputPath}");
        }
        catch (MissingSessionCwdException error)
        {
            var selectedCwd = await PromptForMissingSessionCwdAsync(error).ConfigureAwait(true);
            if (selectedCwd is null)
            {
                ShowStatus("Import cancelled");
                return;
            }

            var result = await _runtimeHost.ImportFromJsonlAsync(inputPath, selectedCwd).ConfigureAwait(true);
            if (result.Cancelled)
            {
                ShowStatus("Import cancelled");
                return;
            }

            ShowStatus($"Session imported from: {inputPath}");
        }
        catch (SessionImportFileNotFoundError error)
        {
            ShowError($"Failed to import session: {error.Message}");
        }
        catch (Exception error)
        {
            await HandleFatalRuntimeErrorAsync("Failed to import session", error).ConfigureAwait(true);
        }
    }

    /// <summary>TS <c>handleSessionCommand</c> (L6666-6745): the Session Info document.</summary>
    internal void HandleSessionCommand()
    {
        var stats = AgentSession.GetSessionStats(
            SessionManager.GetEntries(),
            SessionManager.GetSessionFile(),
            SessionManager.GetSessionId(),
            Session.GetContextUsage());
        var sessionName = SessionManager.GetSessionName();
        var entries = SessionManager.GetEntries();
        var cacheWaste = CacheStats.ComputeCacheWaste(entries, new ModelRuntimePriceSource(Session.ModelRuntime));
        var usageBreakdown = UsageTotalsApi.GetUsageCostBreakdown(entries);

        // Snapshot the stats; the text is built on demand so it follows theme changes.
        var cacheWarmingStatus = Session.CacheWarmer?.Status;
        var cacheWarmingMode = SettingsManager.GetCacheWarmingMode();
        var model = Session.Model;
        var selectedModelKey = $"{model?.Provider}/{model?.Id}";
        string RenderInfo()
        {
            var theme = ThemeRuntime.Current;
            var info = $"{theme.Bold("Session Info")}\n\n";
            if (!string.IsNullOrEmpty(sessionName))
            {
                info += $"{theme.Fg("dim", "Name:")} {sessionName}\n";
            }

            info += $"{theme.Fg("dim", "File:")} {stats.SessionFile ?? "In-memory"}\n";
            info += $"{theme.Fg("dim", "ID:")} {stats.SessionId}\n\n";
            info += $"{theme.Bold("Messages")}\n";
            info += $"{theme.Fg("dim", "Total:")} {stats.TotalMessages}\n";
            info += $"{theme.Fg("dim", "User:")} {stats.UserMessages}\n";
            info += $"{theme.Fg("dim", "Assistant:")} {stats.AssistantMessages}\n";
            info += $"{theme.Fg("dim", "Tools:")} {stats.ToolCalls} calls, {stats.ToolResults} results\n\n";
            info += $"{theme.Bold("Tokens")}\n";
            var promptTokens = stats.Tokens.Input + stats.Tokens.CacheRead + stats.Tokens.CacheWrite;
            info += $"{theme.Fg("dim", "Input:")} {JsNumber.ToLocaleString(promptTokens)}\n";
            if (promptTokens > 0 && (stats.Tokens.CacheRead > 0 || stats.Tokens.CacheWrite > 0))
            {
                var hitRate = theme.Fg("dim", $"({JsNumber.ToFixed((double)stats.Tokens.CacheRead / promptTokens * 100, 1)}%)");
                info += $"  {theme.Fg("dim", "Cached:")} {JsNumber.ToLocaleString(stats.Tokens.CacheRead)} {hitRate}\n";
                var written = stats.Tokens.CacheWrite > 0
                    ? $" {theme.Fg("dim", $"({JsNumber.ToLocaleString(stats.Tokens.CacheWrite)} written to cache)")}"
                    : "";
                info += $"  {theme.Fg("dim", "Uncached:")} {JsNumber.ToLocaleString(stats.Tokens.Input + stats.Tokens.CacheWrite)}{written}\n";
            }

            info += $"{theme.Fg("dim", "Output:")} {JsNumber.ToLocaleString(stats.Tokens.Output)}\n";
            info += $"{theme.Fg("dim", "Total:")} {JsNumber.ToLocaleString(stats.Tokens.Total)}\n";

            info += $"\n{theme.Bold("Cache Warming")}\n";
            info += $"{theme.Fg("dim", "Mode:")} {cacheWarmingMode}\n";
            info += cacheWarmingStatus is not null
                ? $"{theme.Fg("dim", "Status:")} {CacheWarmerMath.FormatCacheWarmingStatus(cacheWarmingStatus)}\n"
                : $"{theme.Fg("dim", "Status:")} Inactive (cache warming unavailable)\n";
            var decision = cacheWarmingStatus?.Decision;
            if (decision?.EconomicsAvailable == true)
            {
                info += $"{theme.Fg("dim", "Cache miss penalty:")} ${JsNumber.ToFixed(decision.MissCost, 3)}\n";
                info += $"{theme.Fg("dim", "Refresh cost:")} ${JsNumber.ToFixed(decision.WarmCost, 3)}\n";
            }

            if (stats.Cost > 0 || cacheWaste.MissedTokens > 0)
            {
                info += $"\n{theme.Bold("Cost")}\n";
                info += $"{theme.Fg("dim", "Total:")} ${JsNumber.ToFixed(stats.Cost, 3)}";
                // A single entry repeats the total, unless it names a model other than the selected one.
                if (usageBreakdown.Count > 1 || usageBreakdown[0]?.Key != selectedModelKey)
                {
                    foreach (var entry in usageBreakdown)
                    {
                        info += $"\n  {theme.Fg("dim", $"{entry.Key}:")} ${JsNumber.ToFixed(entry.Cost, 3)} {theme.Fg("dim", $"({FooterFormatting.FormatTokens(entry.Tokens)} tokens)")}";
                    }
                }

                if (cacheWaste.MissedTokens > 0)
                {
                    var missLabel = cacheWaste.MissCount == 1 ? "1 miss" : $"{cacheWaste.MissCount} misses";
                    var detail = $"{JsNumber.ToLocaleString(cacheWaste.MissedTokens)} tokens, {missLabel}";
                    info += cacheWaste.MissedCost >= 0.0001
                        ? $"\n{theme.Fg("dim", "Cache Re-billed:")} ${JsNumber.ToFixed(cacheWaste.MissedCost, 3)} {theme.Fg("dim", $"({detail})")}"
                        : $"\n{theme.Fg("dim", "Cache Re-billed:")} {detail}";
                }
            }

            return info;
        }

        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new ThemedText(RenderInfo, 1, 0));
        _ui.RequestRender();
    }

    /// <summary>TS <c>handleChangelogCommand</c> (L6747-6766).</summary>
    internal void HandleChangelogCommand() => HandleChangelogCommand(Config.GetChangelogPath());

    internal void HandleChangelogCommand(string changelogPath)
    {
        var allEntries = Changelog.ParseChangelog(changelogPath);

        // TS reverses the array in place before mapping; the local is not read again afterwards.
        var reversed = allEntries.Reverse().ToList();
        var changelogMarkdown = reversed.Count > 0
            ? string.Join("\n\n", reversed.Select(entry => Changelog.NormalizeChangelogLinks(entry.Content, entry)))
            : "No changelog entries found.";

        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new DynamicBorder());
        _chatContainer.AddChild(new ThemedText(
            () => ThemeRuntime.Current.Bold(ThemeRuntime.Current.Fg("accent", "What's New")), 1, 0));
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new Markdown(changelogMarkdown, 1, 1, GetMarkdownThemeWithSettings()));
        _chatContainer.AddChild(new DynamicBorder());
        _ui.RequestRender();
    }

    /// <summary>TS <c>handleHotkeysCommand</c> (L6782-6897): the hotkeys table document.</summary>
    internal void HandleHotkeysCommand()
    {
        var hotkeys = InteractiveModeCommands.BuildHotkeysMarkdown(
            action => KeybindingHints.KeyDisplayText(action),
            action => KeybindingHints.KeyDisplayText(action),
            OperatingSystem.IsWindows(),
            Session.ExtensionRunner.GetShortcuts(_keybindings.GetEffectiveConfig())
                .Select(pair => (pair.Key, pair.Value.Description, pair.Value.ExtensionPath))
                .ToList());

        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new DynamicBorder());
        _chatContainer.AddChild(new ThemedText(
            () => ThemeRuntime.Current.Bold(ThemeRuntime.Current.Fg("accent", "Keyboard Shortcuts")), 1, 0));
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new Markdown(hotkeys.Trim(), 1, 1, GetMarkdownThemeWithSettings()));
        _chatContainer.AddChild(new DynamicBorder());
        _ui.RequestRender();
    }

    /// <summary>TS <c>handleArminSaysHi</c> (L6947-6952).</summary>
    internal void HandleArminSaysHi()
    {
        if (EasterEggPlayer.PlayArmin3d(_renderer))
        {
            return;
        }

        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new ArminComponent(_ui));
        _ui.RequestRender();
    }

    /// <summary>TS <c>handleDementedDelves</c> (L6954-6958).</summary>
    internal void HandleDementedDelves()
    {
        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new EarendilAnnouncementComponent());
        _ui.RequestRender();
    }

    /// <summary>TS <c>handleBashCommand</c> (L6960-7051).</summary>
    internal async Task HandleBashCommandAsync(string command, bool excludeFromContext = false)
    {
        var extensionRunner = Session.ExtensionRunner;

        // Emit user_bash event to let extensions intercept
        UserBashEventResult? eventResult = null;
        try
        {
            eventResult = await extensionRunner.EmitUserBashAsync(new ExtensionEvent.UserBash(
                command,
                excludeFromContext,
                SessionManager.GetCwd())).ConfigureAwait(true);
        }
        catch
        {
            // The extension runner already reported the error. Do not fall back to local execution.
            return;
        }

        // If extension returned a full result, use it directly
        if (eventResult is UserBashEventResult.WithResult withResult)
        {
            // Create UI component for display
            _bashComponent = new BashExecutionComponent(command, _ui, excludeFromContext, _outputPad);
            if (Session.IsStreaming)
            {
                _pendingMessagesContainer.AddChild(_bashComponent);
                _pendingBashComponents.Add(_bashComponent);
            }
            else
            {
                _chatContainer.AddChild(_bashComponent);
            }

            // Show output and complete
            if (!string.IsNullOrEmpty(withResult.Result.Output))
            {
                _bashComponent.AppendOutput(withResult.Result.Output);
            }

            _bashComponent.SetComplete(
                withResult.Result.ExitCode,
                withResult.Result.Cancelled,
                withResult.Result.Truncated
                    ? BashTruncation(withResult.Result.Output)
                    : null,
                withResult.Result.FullOutputPath);

            // Record the result in session
            Session.RecordBashResult(command, withResult.Result, new ExecuteBashOptions { ExcludeFromContext = excludeFromContext });
            _bashComponent = null;
            _ui.RequestRender();
            return;
        }

        // Normal execution path (possibly with custom operations)
        var isDeferred = Session.IsStreaming;
        _bashComponent = new BashExecutionComponent(command, _ui, excludeFromContext, _outputPad);

        if (isDeferred)
        {
            // Show in pending area when agent is streaming
            _pendingMessagesContainer.AddChild(_bashComponent);
            _pendingBashComponents.Add(_bashComponent);
        }
        else
        {
            // Show in chat immediately when agent is idle
            _chatContainer.AddChild(_bashComponent);
        }

        _ui.RequestRender();

        try
        {
            var result = await Session.ExecuteBashAsync(
                command,
                chunk =>
                {
                    if (_bashComponent is not null)
                    {
                        _bashComponent.AppendOutput(chunk);
                        _ui.RequestRender();
                    }
                },
                new ExecuteBashOptions { ExcludeFromContext = excludeFromContext, Operations = (eventResult as UserBashEventResult.WithOperations)?.Operations })
                .ConfigureAwait(true);

            if (_bashComponent is not null)
            {
                _bashComponent.SetComplete(
                    result.ExitCode,
                    result.Cancelled,
                    result.Truncated ? BashTruncation(result.Output) : null,
                    result.FullOutputPath);
            }
        }
        catch (Exception error)
        {
            if (_bashComponent is not null)
            {
                _bashComponent.SetComplete(null, cancelled: false);
            }

            ShowError($"Bash command failed: {error.Message}");
        }
        finally
        {
            _bashComponent = null;
            _ui.RequestRender();
        }
    }

    /// <summary>
    /// TS casts a two-field literal to <c>TruncationResult</c> (L6997 / L7038:
    /// <c>{ truncated: true, content: result.output } as TruncationResult</c>). Only
    /// <see cref=Truncate.TruncationResult.Truncated/> and <c>Content</c> are observable
    /// downstream (BashExecutionComponent reads Truncated), so the remaining fields carry inert
    /// defaults — faithful to the TS cast, which leaves them undefined.
    /// </summary>
    private static Truncate.TruncationResult BashTruncation(string content) => new()
    {
        Content = content,
        Truncated = true,
        TotalLines = 0,
        TotalBytes = 0,
        OutputLines = 0,
        OutputBytes = 0,
        LastLinePartial = false,
        FirstLineExceedsLimit = false,
        MaxLines = 0,
        MaxBytes = 0,
    };

    /// <summary>TS <c>handleCompactCommand</c> (L7053-7061).</summary>
    internal async Task HandleCompactCommandAsync(string? customInstructions = null)
    {
        ClearStatusIndicator();

        try
        {
            await Session.CompactAsync(new CompactOptions { CustomInstructions = customInstructions })
                .ConfigureAwait(true);
        }
        catch
        {
            // Ignore, will be emitted as an event
        }
    }
}
