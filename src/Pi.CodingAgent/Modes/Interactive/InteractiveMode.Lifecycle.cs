// ============================================================================
// InteractiveMode — lifecycle, slice 3 of 4f-4m.
//
// Port of interactive-mode.ts's init (L936-1119), run (L1138-1253), the startup
// helpers (L1255-1271 checkForPackageUpdates, L1324-1368 getChangelogForDisplay +
// reportInstallTelemetry, L1410-1422 startup gates, L3784-3795
// showManagedToolStatus, L3174-3177 handleStartupSubmit, L4646-4664
// showPackageUpdateNotification, L1255-1318 tmux query), the key-exit path
// (L4248-4261 handleCtrlC/D), shutdown (L4270-4309), the crash-exit paths
// (L4311-4357), checkShutdownRequested (L4362-4365), the signal handlers
// (L4367-4415) and handleCtrlZ (L4417-4452), plus the parts of setupKeyHandlers
// (L3047-3117) and the clipboard paste family (L3118-3172) that do not belong to
// the command slice.
//
// Seams retired here: InitAsync, CheckShutdownRequested, HandleRightClickPaste.
// New seams (retire in the command slice 4f-4m-4 unless noted):
//   SetupEditorSubmitHandler (the L3179-3375 submit if-chain) → 4f-4m-4
//   SetupCommandKeyHandlers (command-family editor actions)   → 4f-4m-4
//   ShowDoubleEscapeSelector (double-escape → tree/user-message selector) → 4f-4m-4
//   MaybeWarnAboutAnthropicSubscriptionAuth                   → 4f-4m-4
//   TakeUnnotifiedCrashForWarning (crash-log take flow)       → 4f-4m-5
//   RecordCrashForUi (crash-log record flow)                  → 4f-4m-5
// ============================================================================

using System.Globalization;
using System.Runtime.InteropServices;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive;

public partial class InteractiveMode
{
    private static bool IsOfflineEnv() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PI_OFFLINE"));

    // ---------------------------------------------------------------- init (L936-1119)

    internal async Task InitAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        RegisterSignalHandlers();

        // Load changelog (only show new entries, skip for resumed sessions)
        _changelogMarkdown = GetChangelogForDisplay();

        if (Session.ScopedModels.Count > 0 && ShouldShowStartupDetails())
        {
            var modelList = string.Join(", ", Session.ScopedModels.Select(sm =>
                $"{sm.Model.Id}{(sm.ThinkingLevel is { } level ? $":{Pi.Ai.Types.ThinkingLevelText.ToText(level)}" : "")}"));
            var cycleKeys = GlobalKeybindings.Get().GetKeys("app.model.cycleForward").ToList();
            var cycleHint = cycleKeys.Count > 0
                ? ThemeRuntime.Current.Fg(
                    "muted",
                    $" ({KeybindingHints.FormatKeyText(string.Join("/", cycleKeys), new KeyTextFormatOptions { Capitalize = true })} to cycle)")
                : "";
            Console.WriteLine(ThemeRuntime.Current.Fg("dim", $"Model scope: {modelList}{cycleHint}"));
        }

        // Keep one component tree and remount it when changing renderers.
        RenderWidgets(); // Initialize with default spacer
        var viewport = ChatViewportFactory.CreateChatViewport(new ChatViewportOptions
        {
            Document = _documentContainer,
            PendingMessages = _pendingMessagesContainer,
            Status = _statusContainer,
            WidgetsAbove = _widgetContainerAbove,
            Editor = _editorContainer,
            WidgetsBelow = _widgetContainerBelow,
            Footer = _footerContainer,
            Scrollbar = SettingsManager.GetFullscreenScrollbar(),
            ScrollbarTrackStyle = text => ThemeRuntime.Current.Fg("scrollbarTrack", text),
            ScrollbarThumbStyle = text => ThemeRuntime.Current.Fg("scrollbarThumb", text),
        });
        _transcriptScrollView = viewport.Transcript;
        _fullscreenLayoutRoot = viewport.Root;
        MountInteractiveTui(_renderer,
        [
            _documentContainer,
            _pendingMessagesContainer,
            _statusContainer,
            _widgetContainerAbove,
            _editorContainer,
            _widgetContainerBelow,
            _footerContainer,
        ]);

        // Accept text while startup completes, but only enable interrupt, exit, and submission feedback.
        _defaultEditor.OnAction("app.clear", HandleCtrlC);
        _defaultEditor.OnCtrlD = HandleCtrlD;
        _defaultEditor.OnSubmit = HandleStartupSubmit;
        _ui.SetFocus(_editor);

        // Start the UI before initializing extensions so session_start handlers can use interactive dialogs
        _ui.Start();
        _isInitialized = true;
        EnsurePngTranscoder();

        _themeController.ApplyFromSettings();
        // The header and startup notices bake theme colors into their text, so build them once the terminal
        // reported its colors. This ends at the terminal's DA1 reply, or after 100 ms if it answers nothing.
        await _themeController.WaitForTerminalColorsAsync().ConfigureAwait(true);

        // Add header with keybindings from config (unless silenced)
        if (ShouldShowStartupHeader())
        {
            var showDetails = ShouldShowStartupDetails();
            // Built on demand so the header follows theme changes. The logo's first line carries the version,
            // its second line the first line of key hints. Terminals that cannot render the logo get a
            // "Pi vX" line instead, with the key hints below it.
            var showLogo = PiLogo.SupportsLogo();
            string WithLogo(string hints)
            {
                if (!showLogo)
                {
                    return $"{PiLogo.Wordmark()} {ThemeRuntime.Current.Fg("dim", $"v{Config.Version}")}\n{hints}";
                }

                var (top, bottom) = PiLogo.LogoLines();
                return $"{top} {ThemeRuntime.Current.Fg("dim", $"v{Config.Version}")}\n{bottom} {hints}";
            }

            static string Hint(string keybinding, string description) => KeybindingHints.KeyHint(keybinding, description);
            static string RawHint(string key, string description) => KeybindingHints.RawKeyHint(key, description);

            string ExpandedInstructions() => string.Join("\n",
                Hint("app.interrupt", "to interrupt"),
                Hint("app.clear", "to clear"),
                RawHint($"{KeybindingHints.KeyText("app.clear")} twice", "to exit"),
                Hint("app.exit", "to exit (empty)"),
                Hint("app.suspend", "to suspend"),
                KeybindingHints.KeyHint("tui.editor.deleteToLineEnd", "to delete to end"),
                Hint("app.thinking.cycle", "to cycle thinking level"),
                RawHint(
                    $"{KeybindingHints.KeyText("app.model.cycleForward")}/{KeybindingHints.KeyText("app.model.cycleBackward")}",
                    "to cycle models"),
                Hint("app.model.select", "to select model"),
                Hint("app.tools.expand", "to expand tools"),
                Hint("app.thinking.toggle", "to expand thinking"),
                Hint("app.editor.external", "for external editor"),
                RawHint("/", "for commands"),
                RawHint("!", "to run bash"),
                RawHint("!!", "to run bash (no context)"),
                Hint("app.message.followUp", "to queue follow-up"),
                Hint("app.message.dequeue", "to edit all queued messages"),
                Hint("app.clipboard.pasteImage", "to paste files on macOS, images, or text"),
                RawHint("drop files", "to attach"));

            string CompactInstructions() => string.Join(
                ThemeRuntime.Current.Fg("muted", " · "),
                Hint("app.interrupt", "interrupt"),
                RawHint($"{KeybindingHints.KeyText("app.clear")}/{KeybindingHints.KeyText("app.exit")}", "clear/exit"),
                RawHint("/", "commands"),
                RawHint("!", "bash"),
                Hint("app.tools.expand", "more"));

            string CompactOnboarding() => ThemeRuntime.Current.Fg(
                "dim",
                $"Press {KeybindingHints.KeyText("app.tools.expand")} to show full startup help{(showDetails ? " and loaded resources" : "")}.");

            string Onboarding() =>
                ThemeRuntime.Current.Fg("dim", "Pi can explain its own features and look up its docs. Ask it how to use or extend Pi.");

            var header = new BuiltInHeader(
                () => $"{WithLogo(CompactInstructions())}\n{CompactOnboarding()}\n\n{Onboarding()}",
                () => $"{WithLogo(ExpandedInstructions())}\n\n{Onboarding()}",
                GetStartupExpansionState(),
                1,
                0);
            if (showLogo)
            {
                header.OnLogoClick = (column, row) => EasterEggPlayer.PlayPiLogo3d(_renderer, column, row);
            }

            _builtInHeader = header;

            // Setup UI layout
            _headerContainer.AddChild(new Spacer(1));
            _headerContainer.AddChild(_builtInHeader);
            _headerContainer.AddChild(new Spacer(1));
        }
        else
        {
            // Minimal header when silenced
            _builtInHeader = new Pi.Tui.Components.Text("", 0, 0);
            _headerContainer.AddChild(_builtInHeader);
        }

        _ui.RequestRender();

        // Ensure fd and rg are available after mounting the TUI (downloads if missing, adds to PATH via getBinDir)
        // so slow downloads do not make startup appear frozen.
        // Both are needed: fd for autocomplete, rg for grep tool and bash commands.
        var fdTask = ToolsManager.EnsureToolAsync("fd", ShowManagedToolStatus);
        var rgTask = ToolsManager.EnsureToolAsync("rg", ShowManagedToolStatus);
        await Task.WhenAll(fdTask, rgTask).ConfigureAwait(true);
        _fdPath = fdTask.Result;

        // Enable the remaining input handlers only after managed-tool setup completes.
        SetupKeyHandlers();
        SetupEditorSubmitHandler();
        _ui.RequestRender();

        // Initialize extensions first so resources are shown before messages
        await RebindCurrentSessionAsync().ConfigureAwait(true);

        // Render initial messages AFTER showing loaded resources
        RenderInitialMessages();

        // Set up theme file watcher
        ThemeRuntime.OnThemeChange(() =>
        {
            _ui.Invalidate();
            UpdateEditorBorderColor();
            _ui.RequestRender();
        });

        // Set up git branch watcher (uses provider instead of footer)
        _footerDataProvider.OnBranchChange(() => _ui.RequestRender());

        // Initialize available provider count for footer display
        UpdateAvailableProviderCount();

        // Flush the completed startup state before loading the remaining syntax grammars.
        _ui.RenderNow();
        _ = SyntaxHighlight.LoadAllLanguagesAsync().ContinueWith(t =>
        {
            if (!_isInitialized)
            {
                return;
            }

            _ui.Invalidate();
            _ui.RequestRender();
        });
    }

    // ---------------------------------------------------------------- startup gates (L1410-1422)

    internal bool GetStartupExpansionState() => _options.Verbose == true || _toolOutputExpanded;

    /// <summary>Startup header (logo, version, key hints). Hidden only by quietStartup: true.</summary>
    internal bool ShouldShowStartupHeader() =>
        _options.Verbose == true || SettingsManager.GetQuietStartup() != "true";

    /// <summary>Startup details (model scope, loaded resources). Hidden by quietStartup: true or "header".</summary>
    internal bool ShouldShowStartupDetails() =>
        _options.Verbose == true || SettingsManager.GetQuietStartup() == "false";

    // ---------------------------------------------------------------- changelog / telemetry (L1324-1368)

    internal string? GetChangelogForDisplay()
    {
        // Skip changelog for resumed/continued sessions (already have messages)
        // TS counts `session.state.messages` (the in-memory transcript); the port derives the same
        // observable from the persisted message entries (C-diff C271, same init-time value).
        if (InteractiveModeLifecycle.ShouldSkipChangelog(
                SessionManager.GetEntries().Count(e => e is SessionMessageEntry)))
        {
            return null;
        }

        var lastVersion = SettingsManager.GetLastChangelogVersion();
        var entries = Changelog.ParseChangelog(Config.GetChangelogPath());

        if (InteractiveModeLifecycle.IsFreshInstall(lastVersion))
        {
            // Fresh install - record the version, send telemetry, don't show changelog
            SettingsManager.SetLastChangelogVersion(Config.Version);
            ReportInstallTelemetry(Config.Version);
            return null;
        }

        var newEntries = Changelog.GetNewEntries(entries, lastVersion!);
        if (newEntries.Count > 0)
        {
            SettingsManager.SetLastChangelogVersion(Config.Version);
            ReportInstallTelemetry(Config.Version);
            return string.Join("\n\n", newEntries.Select(e => Changelog.NormalizeChangelogLinks(e.Content, e)));
        }

        return null;
    }

    private void ReportInstallTelemetry(string version)
    {
        if (IsOfflineEnv())
        {
            return;
        }

        if (!Pi.CodingAgent.Core.Telemetry.IsInstallTelemetryEnabled(SettingsManager))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://pi.dev/api/report-install?version={Uri.EscapeDataString(version)}");
                request.Headers.TryAddWithoutValidation("User-Agent", PiUserAgent.GetPiUserAgent(version));
                await client.SendAsync(request).ConfigureAwait(false);
            }
            catch
            {
                // Telemetry failures are silently ignored (TS `.catch(() => undefined)`).
            }
        });
    }

    // ---------------------------------------------------------------- package / tmux checks (L1255-1318)

    /// <summary>
    /// Seam: TS <c>checkForPackageUpdates()</c> needs the extension package-manager update flow
    /// (<c>update</c> / <c>checkForAvailableUpdates</c>), which the port has not landed (see the
    /// <c>SemverRange</c> note). Lands with the extensions package-manager batch.
    /// </summary>
    private Task<IReadOnlyList<string>> CheckForPackageUpdatesAsync() =>
        throw new NotImplementedException("extensions package-manager batch: checkForAvailableUpdates (TS L1255-1271)");

    private async Task<string?> CheckTmuxKeyboardSetupAsync()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TMUX")))
        {
            return null;
        }

        var (extendedKeys, extendedKeysFormat) = (
            await RunTmuxShowAsync("extended-keys").ConfigureAwait(true),
            await RunTmuxShowAsync("extended-keys-format").ConfigureAwait(true));

        return InteractiveModeLifecycle.ResolveTmuxKeyboardWarning(extendedKeys, extendedKeysFormat);
    }

    /// <summary>TS <c>runTmuxShow</c>: query a tmux option with a 2 s timeout; silence on failure.</summary>
    private static async Task<string?> RunTmuxShowAsync(string option)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("tmux", $"show -gv {option}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
        };
        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var completed = await Task.WhenAny(stdoutTask, Task.Delay(2000)).ConfigureAwait(true);
            if (completed != stdoutTask)
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // 超时/沙箱环境下 kill 失败同样沉默。
                }

                return null;
            }

            var stdout = await stdoutTask.ConfigureAwait(true);
            return process.WaitForExit(2000) && process.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- managed tools / startup submit (L3784-3795, L3174-3177)

    internal void ShowManagedToolStatus(ToolStatus status)
    {
        if (!_managedToolStatusStarted)
        {
            _chatContainer.AddChild(new Spacer(1));
            _managedToolStatusStarted = true;
        }

        var message = status.Type == "warning" ? $"Warning: {status.Message}" : status.Message;
        var color = status.Type == "warning" ? "warning" : "dim";
        _chatContainer.AddChild(new ThemedText(() => ThemeRuntime.Current.Fg(color, message), 1, 0));
        _lastStatusSpacer = null;
        _lastStatusText = null;
        _ui.RequestRender();
    }

    internal void HandleStartupSubmit(string text)
    {
        _editor.SetText(text);
        ShowStatus("Startup is still in progress");
    }

    // ---------------------------------------------------------------- package update notification (L4646-4664)

    private void ShowPackageUpdateNotification(IReadOnlyList<string> packages)
    {
        string UpdateInstruction() =>
            ThemeRuntime.Current.Fg("muted", "Package updates are available. Run ")
            + ThemeRuntime.Current.Fg("accent", $"{Config.AppName} update --extensions");
        var packageLines = string.Join("\n", packages.Select(pkg => $"- {pkg}"));

        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new DynamicBorder(text => ThemeRuntime.Current.Fg("warning", text)));
        _chatContainer.AddChild(new ThemedText(
            () =>
                $"{ThemeRuntime.Current.Bold(ThemeRuntime.Current.Fg("warning", "Package Updates Available"))}\n{UpdateInstruction()}\n{ThemeRuntime.Current.Fg("muted", "Packages:")}\n{packageLines}",
            1,
            0));
        _chatContainer.AddChild(new DynamicBorder(text => ThemeRuntime.Current.Fg("warning", text)));
        _ui.RequestRender();
    }

    // ---------------------------------------------------------------- run (L1138-1253)

    /// <summary>Run the interactive mode. This is the main entry point.</summary>
    public async Task RunAsync()
    {
        await InitAsync().ConfigureAwait(true);

        if (!IsOfflineEnv())
        {
            var timeout = new CancellationTokenSource(15_000);
            _ = ModelCatalogRefresh.RefreshAsync(Session.ModelRuntime, timeout.Token)
                .ContinueWith(async t =>
                {
                    try
                    {
                        if (t.Status == TaskStatus.RanToCompletion)
                        {
                            UpdateAvailableProviderCount();
                        }
                    }
                    finally
                    {
                        timeout.Dispose();
                    }
                });
        }

        // Start version check asynchronously
        _ = VersionCheck.CheckForNewPiVersionAsync(Config.Version).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result is { } newRelease)
            {
                ShowNewVersionNotification(newRelease);
            }
        });

        // Check tmux keyboard setup asynchronously
        _ = CheckTmuxKeyboardSetupAsync().ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result is { } warning)
            {
                ShowWarning(warning);
            }
        });

        // Start package update check asynchronously
        _ = CheckForPackageUpdatesAsync().ContinueWith(t =>
        {
            // The check itself is a seam until the package-manager batch lands; the ContinueWith
            // shape matches TS (updates → banner, finally → Windows title restore).
            if (t.Status == TaskStatus.RanToCompletion && t.Result.Count > 0)
            {
                ShowPackageUpdateNotification(t.Result);
            }

            // On Windows, npm can overwrite the shared console title while checking
            // extension package versions. Restore Pi's title after the startup check.
            if (OperatingSystem.IsWindows() && _isInitialized)
            {
                UpdateTerminalTitle();
            }
        });

        // Show startup warnings
        var options = _options;
        foreach (var diagnostic in options.StartupDiagnostics ?? [])
        {
            switch (diagnostic.Type)
            {
                case AgentSessionDiagnosticType.Error:
                    ShowError(diagnostic.Message);
                    break;
                case AgentSessionDiagnosticType.Warning:
                    ShowWarning(diagnostic.Message);
                    break;
                default:
                    ShowStatus(diagnostic.Message);
                    break;
            }
        }

        if (options.MigratedProviders is { Count: > 0 } migratedProviders)
        {
            ShowWarning($"Migrated credentials to auth.json: {string.Join(", ", migratedProviders)}");
        }

        var modelsJsonError = Session.ModelRuntime.GetError();
        if (modelsJsonError is not null)
        {
            ShowError($"models.json error: {modelsJsonError}");
        }

        if (options.ModelFallbackMessage is { } modelFallbackMessage)
        {
            ShowWarning(modelFallbackMessage);
        }

        var crash = TakeUnnotifiedCrashForWarning();
        if (crash is { } unnotified)
        {
            var when = unnotified.Timestamp.LocalDateTime.ToString("G", CultureInfo.CurrentCulture);
            ShowWarning(
                $"{Config.AppName} crashed on {when} ({unnotified.Message}). Run /bug to report it; the crash details are attached automatically.");
        }

        _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync();

        // Process initial messages
        if (options.InitialMessage is { } initialMessage)
        {
            try
            {
                await Session.PromptAsync(initialMessage, new PromptOptions { Images = options.InitialImages }).ConfigureAwait(true);
            }
            catch (Exception error)
            {
                ShowError(error.Message);
            }
        }

        if (options.InitialMessages is { Count: > 0 } initialMessages)
        {
            foreach (var message in initialMessages)
            {
                try
                {
                    await Session.PromptAsync(message).ConfigureAwait(true);
                }
                catch (Exception error)
                {
                    ShowError(error.Message);
                }
            }
        }

        // Main interactive loop
        while (true)
        {
            var userInput = await GetUserInputAsync().ConfigureAwait(true);
            try
            {
                await Session.PromptAsync(userInput).ConfigureAwait(true);
            }
            catch (Exception error)
            {
                ShowError(error.Message);
            }
        }
    }

    // ---------------------------------------------------------------- key exit path (L4248-4261)

    internal void HandleCtrlC()
    {
        var now = Environment.TickCount64;
        if (now - _lastSigintTime < 500)
        {
            _ = ShutdownAsync();
        }
        else
        {
            ClearEditor();
            _lastSigintTime = now;
        }
    }

    private void HandleCtrlD()
    {
        // Only called when editor is empty (enforced by CustomEditor)
        _ = ShutdownAsync();
    }

    // ---------------------------------------------------------------- shutdown (L4270-4309)

    /// <summary>
    /// Gracefully shutdown the agent. Stops the TUI before emitting shutdown events so extension
    /// UI cleanup cannot repaint the final frame while the process is exiting.
    /// </summary>
    private async Task ShutdownAsync(bool fromSignal = false)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        // Keep signal handlers registered until terminal cleanup has completed.

        if (fromSignal)
        {
            // Signal-triggered shutdown (SIGTERM/SIGHUP). Emit extension cleanup (session_shutdown)
            // BEFORE touching the terminal — see the TS note on #4144.
            await _runtimeHost.DisposeAsync().ConfigureAwait(true);
            _themeController.DisableAutoSync();
            await _ui.Terminal.DrainInputAsync(1000).ConfigureAwait(true);
            Stop();
            Environment.Exit(0);
        }

        // Interactive quit (Ctrl+D, Ctrl+C, /quit, extension shutdown()). Stop the TUI before
        // emitting shutdown events. Drain any in-flight Kitty key release events before stopping
        // (prevents escape sequences leaking to the parent shell over slow SSH).
        _themeController.DisableAutoSync();
        await _ui.Terminal.DrainInputAsync(1000).ConfigureAwait(true);

        Stop();
        await _runtimeHost.DisposeAsync().ConfigureAwait(true);

        var sessionFile = SessionManager.GetSessionFile();
        var resumeCommand = InteractiveModeLifecycle.FormatResumeCommand(
            isTty: !Console.IsOutputRedirected,
            isPersisted: SessionManager.IsPersisted(),
            sessionFile: sessionFile,
            sessionFileExists: sessionFile is not null && File.Exists(sessionFile),
            usesDefaultSessionDir: SessionManager.UsesDefaultSessionDir(),
            sessionDir: SessionManager.GetSessionDir(),
            sessionId: SessionManager.GetSessionId(),
            appName: Config.AppName);
        if (resumeCommand is not null)
        {
            Console.Write($"{Chalk.Dim("To resume this session:")} {resumeCommand}\n");
        }

        Environment.Exit(0);
    }

    // ---------------------------------------------------------------- crash exits (L4311-4357)

    private void EmergencyTerminalExit()
    {
        _isShuttingDown = true;
        UnregisterSignalHandlers();
        Shell.KillTrackedDetachedChildren();
        // The terminal is gone. Do not run normal shutdown because TUI and extension cleanup can
        // write restore sequences and re-trigger EIO.
        Environment.Exit(129);
    }

    /// <summary>
    /// Last-resort handler for uncaught exceptions. Unlike <see cref="EmergencyTerminalExit"/>, the
    /// terminal is still alive here, so <c>ui.stop()</c> restores cooked mode, the cursor, and
    /// disables bracketed paste / Kitty / modifyOtherKeys sequences.
    /// </summary>
    private void UncaughtCrash(Exception error)
    {
        // A dead terminal is not a pi crash. Do not try to restore it or record it.
        if (InteractiveModeSupport.IsDeadTerminalError(error))
        {
            EmergencyTerminalExit();
        }

        if (_isShuttingDown)
        {
            Environment.Exit(1);
        }

        _isShuttingDown = true;
        try
        {
            UnregisterSignalHandlers();
        }
        catch
        {
        }

        try
        {
            Shell.KillTrackedDetachedChildren();
        }
        catch
        {
        }

        try
        {
            _ui.Stop();
        }
        catch
        {
        }

        Console.Error.WriteLine($"{Config.AppName} exiting due to uncaughtException:");
        Console.Error.WriteLine(error);
        var extensionHint = GetCrashExtensionHintForUi(error);
        if (extensionHint is not null)
        {
            Console.Error.WriteLine($"\n{extensionHint}");
        }

        if (RecordCrashForUi(CrashKind.UncaughtException, error))
        {
            Console.Error.WriteLine(
                $"\n{InteractiveModeDiagnostics.CrashReportInstructions(SessionManager.GetSessionFile() is not null)}");
        }

        Environment.Exit(1);
    }

    // ---------------------------------------------------------------- shutdown check (L4362-4365)

    /// <summary>Check if shutdown was requested and perform shutdown if so.</summary>
    internal void CheckShutdownRequested()
    {
        if (!_shutdownRequested)
        {
            return;
        }

        _ = ShutdownAsync();
    }

    // ---------------------------------------------------------------- signal handlers (L4367-4415)

    /// <summary>Test seam: the private registration path, for idempotency checks.</summary>
    internal void RegisterSignalHandlersForTests() => RegisterSignalHandlers();

    private void RegisterSignalHandlers()
    {
        UnregisterSignalHandlers();

        if (!OperatingSystem.IsWindows())
        {
            RegisterPosixSignal(PosixSignal.SIGHUP);
        }

        RegisterPosixSignal(PosixSignal.SIGTERM);

        // TS also wires stdin/stdout/stderr stream error handlers that convert dead-terminal EIO
        // into EmergencyTerminalExit. .NET has no per-stream error events; the dead-terminal
        // conversion happens in UncaughtCrash, which the AppDomain handler below feeds.

        // Restore the terminal before the process dies on any uncaught throw.
        void UncaughtHandler(object? sender, UnhandledExceptionEventArgs args) =>
            UncaughtCrash((Exception)args.ExceptionObject);
        AppDomain.CurrentDomain.UnhandledException += UncaughtHandler;
        _signalCleanupHandlers.Add(() => AppDomain.CurrentDomain.UnhandledException -= UncaughtHandler);
    }

    /// <summary>TS <c>process.prependListener(signal, handler)</c> for graceful signal shutdown.</summary>
    private void RegisterPosixSignal(PosixSignal signal)
    {
        if (!OperatingSystem.IsWindows() || signal != PosixSignal.SIGHUP)
        {
            var registration = PosixSignalRegistration.Create(signal, context =>
            {
                // SIGHUP no longer hard-exits: graceful shutdown emits session_shutdown first, then
                // attempts terminal restore (see #4144, #5080).
                Shell.KillTrackedDetachedChildren();
                _ = ShutdownAsync(fromSignal: true);
            });
            _signalCleanupHandlers.Add(registration.Dispose);
        }
    }

    // ---------------------------------------------------------------- suspend (L4417-4452)

    internal void HandleCtrlZ()
    {
        if (OperatingSystem.IsWindows())
        {
            ShowStatus("Suspend to background is not supported on Windows");
            return;
        }

        // Keep the event loop alive while suspended (TS uses a long setInterval; .NET uses a timer).
        using var suspendKeepAlive = new Timer(_ => { }, null, 1 << 30, Timeout.Infinite);

        // Ignore SIGINT while suspended so Ctrl+C in the terminal does not kill the backgrounded
        // process. The handler is removed on resume.
        using var ignoreSigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => { });

        // Set up handler to restore TUI when resumed
        using var sigcont = PosixSignalRegistration.Create(PosixSignal.SIGCONT, _ =>
        {
            _ui.Start();
            _ui.RequestRender(true);
        });

        try
        {
            // Stop the TUI (restore terminal to normal mode)
            _ui.Stop();

            // Send SIGTSTP to process group (pid=0 means all processes in group)
            KillProcessGroup(20 /* SIGTSTP */);
        }
        catch (Exception)
        {
            throw;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private static void KillProcessGroup(int signal)
    {
        if (kill(0, signal) != 0)
        {
            throw new InvalidOperationException($"kill(0, {signal}) failed with errno {Marshal.GetLastWin32Error()}");
        }
    }

    // ---------------------------------------------------------------- key handlers (L3047-3117, partial)

    /// <summary>
    /// Set up handlers on defaultEditor — they use <c>editor</c> for text access so they work
    /// correctly regardless of which editor is active. The command-family actions land with 4f-4m-4
    /// (see <see cref="SetupCommandKeyHandlers"/>).
    /// </summary>
    private void SetupKeyHandlers()
    {
        _defaultEditor.OnEscape = () =>
        {
            if (Session.IsStreaming)
            {
                RestoreQueuedMessagesToEditor(abort: true);
            }
            else if (Session.IsBashRunning)
            {
                Session.AbortBash();
            }
            else if (_isBashMode)
            {
                _editor.SetText("");
                _isBashMode = false;
                UpdateEditorBorderColor();
            }
            else if (string.IsNullOrWhiteSpace(_editor.GetText()))
            {
                // Double-escape with empty editor triggers /tree, /fork, or nothing based on setting
                var action = SettingsManager.GetDoubleEscapeAction();
                if (action != "none")
                {
                    var now = Environment.TickCount64;
                    if (now - _lastEscapeTime < 500)
                    {
                        ShowDoubleEscapeSelector(action);
                        _lastEscapeTime = 0;
                    }
                    else
                    {
                        _lastEscapeTime = now;
                    }
                }
            }
        };

        // Register app action handlers available this slice
        _defaultEditor.OnAction("app.clear", HandleCtrlC);
        _defaultEditor.OnCtrlD = HandleCtrlD;
        _defaultEditor.OnAction("app.suspend", HandleCtrlZ);

        // Command-family actions (4f-4m-4): thinking/model cycles, selectors, message actions.
        SetupCommandKeyHandlers();

        _defaultEditor.OnChange = text =>
        {
            var wasBashMode = _isBashMode;
            _isBashMode = text.TrimStart().StartsWith('!');
            if (wasBashMode != _isBashMode)
            {
                UpdateEditorBorderColor();
            }
        };

        // Handle clipboard paste (triggered on Ctrl+V). Copied files use their original paths,
        // images are attached via temporary files, and plain text is the final fallback.
        _defaultEditor.OnPasteImage = () => _ = HandleClipboardPasteAsync();
    }

    /// <summary>
    /// Seam: TS <c>handleClipboardPaste</c> (L3132-3172) needs the editor's optional cursor APIs
    /// (<c>getCursor?</c> / <c>insertTextAtCursor?</c>), which the editor port has not exposed yet.
    /// Lands with the 4f-4m-4 input batch.
    /// </summary>
    private Task HandleClipboardPasteAsync() =>
        throw new NotImplementedException("4f-4m-4 input batch: handleClipboardPaste (TS L3132-3172)");

    // ---------------------------------------------------------------- clipboard (L3118-3172)

    private async Task HandleRightClickPasteAsync()
    {
        var target = _renderer.GetFocusedComponent();
        if (target is null)
        {
            return;
        }

        try
        {
            var text = await Clipboard.ReadClipboardTextAsync().ConfigureAwait(true);
            if (string.IsNullOrEmpty(text) || _renderer.GetFocusedComponent() != target)
            {
                return;
            }

            target.HandleInput($"\x1b[200~{text}\x1b[201~");
            _ui.RequestRender();
        }
        catch
        {
            // Silently ignore clipboard errors (may not have permission, etc.)
        }
    }

    // ---------------------------------------------------------------- seams

    /// <summary>Seam: the L3179-3375 submit if-chain lands with the 4f-4m-4 command slice.</summary>
    private void SetupEditorSubmitHandler() =>
        throw new NotImplementedException("4f-4m-4 command slice: setupEditorSubmitHandler (TS L3179-3375)");

    /// <summary>Seam: command-family editor actions land with the 4f-4m-4 command slice.</summary>
    private void SetupCommandKeyHandlers() =>
        throw new NotImplementedException("4f-4m-4 command slice: setupKeyHandlers command actions (TS L3082-3101)");

    /// <summary>Seam: double-escape → tree / user-message selector lands with 4f-4m-4.</summary>
    private void ShowDoubleEscapeSelector(string action) =>
        throw new NotImplementedException($"4f-4m-4 command slice: double-escape action '{action}' (TS L3065-3070)");

    /// <summary>Seam: the anthropic subscription auth warning lands with the 4f-4m-4 login slice.</summary>
    private Task MaybeWarnAboutAnthropicSubscriptionAuthAsync() =>
        throw new NotImplementedException("4f-4m-4 login slice: maybeWarnAboutAnthropicSubscriptionAuth (TS L5240+)");

    /// <summary>
    /// Seam: TS <c>takeUnnotifiedCrash()</c> — read and mark-notified the pending crash record.
    /// The crash-log take flow lands with the 4f-4m-5 closeout slice.
    /// </summary>
    private (DateTimeOffset Timestamp, string Message)? TakeUnnotifiedCrashForWarning() =>
        throw new NotImplementedException("4f-4m-5 closeout: takeUnnotifiedCrash (crash-log module)");

    /// <summary>Seam: TS <c>recordCrash</c> — persist a crash record. Lands with the 4f-4m-5 closeout.</summary>
    private bool RecordCrashForUi(string kind, Exception error) =>
        throw new NotImplementedException("4f-4m-5 closeout: recordCrash (crash-log module)");

    /// <summary>
    /// TS <c>this.getCrashExtensionHint(error)</c>: the error stack matched against the loaded
    /// extensions. The runner-side extension-stack metadata accessor lands with the 4f-4m-5
    /// closeout; the pure matcher is <see cref="InteractiveModeExtensions.GetCrashExtensionHint"/>.
    /// </summary>
    private string? GetCrashExtensionHintForUi(Exception error) =>
        throw new NotImplementedException("4f-4m-5 closeout: getCrashExtensionHint (TS L2125-2137)");

    /// <summary>
    /// Seam: TS <c>showNewVersionNotification</c> (L4620-4644). Lands with the 4f-4m-4 command
    /// slice (it shares the changelog-markdown rendering path).
    /// </summary>
    private void ShowNewVersionNotification(object newRelease) =>
        throw new NotImplementedException("4f-4m-4 command slice: showNewVersionNotification (TS L4620-4644)");
}
