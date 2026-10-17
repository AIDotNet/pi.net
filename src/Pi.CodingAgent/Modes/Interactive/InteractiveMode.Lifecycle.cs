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
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;
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

    /// <summary>
    /// TS <c>setupKeyHandlers</c>' command-family actions (L3082-3101). The selector actions
    /// (model/tree/fork/resume) and the debug TUI handler land with the 4f-4m-4 tail slice — see
    /// <see cref="SetupSelectorKeyHandlers"/> and <see cref="HandleDebugCommandForUi"/>.
    /// </summary>
    private void SetupCommandKeyHandlers()
    {
        _defaultEditor.OnAction("app.thinking.cycle", CycleThinkingLevel);
        _defaultEditor.OnAction("app.model.cycleForward", () => _ = CycleModelAsync("forward"));
        _defaultEditor.OnAction("app.model.cycleBackward", () => _ = CycleModelAsync("backward"));

        _defaultEditor.OnAction("app.tools.expand", ToggleToolOutputExpansion);
        _defaultEditor.OnAction("app.thinking.toggle", ToggleThinkingBlockVisibility);
        _defaultEditor.OnAction("app.message.copy", () => _ = HandleCopyCommandAsync(flashConfirmation: true, preferSelection: true));
        _defaultEditor.OnAction("app.message.followUp", () => _ = HandleFollowUpAsync());
        _defaultEditor.OnAction("app.message.dequeue", HandleDequeue);

        _defaultEditor.OnAction("app.session.new", () => _ = HandleClearCommandAsync());
        _defaultEditor.OnAction("app.model.select", () => ShowModelSelector());
        _ui.OnDebug = HandleDebugCommand;
        SetupTreeForkResumeKeyHandlers();
    }

    /// <summary>
    /// Seam: the tree / fork / resume selector actions (TS L3099-3101) need the session
    /// fork / navigateTree / getUserMessagesForForking / switchSession APIs and their selector
    /// methods (showTreeSelector L5538 / showUserMessageSelector L5479 / showSessionSelector L5687)
    /// — lands with the 4f-4m-4 尾批.
    /// </summary>
    private void SetupTreeForkResumeKeyHandlers() =>
        throw new NotImplementedException("4f-4m-4 tail: tree/fork/resume selectors (TS L3099-3101)");

    // ---------------------------------------------------------------- selectors / clear / debug

    /// <summary>TS <c>showModelSelector</c> (L5321-5355).</summary>
    internal void ShowModelSelector(string? initialSearchInput = null)
    {
        ShowSelector(done =>
        {
            async Task SelectModelAsync(ModelSpec model, bool persist)
            {
                try
                {
                    await Session.SetModelAsync(model, new ModelMutationOptions { Persist = persist }).ConfigureAwait(true);
                    UpdateAvailableProviderCount();
                    _footer.Invalidate();
                    UpdateEditorBorderColor();
                    done();
                    ShowStatus(persist ? $"Default model: {model.Provider}/{model.Id}" : $"Model: {model.Id}");
                    _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync(model);
                }
                catch (Exception error)
                {
                    done();
                    ShowError(error.Message);
                }
            }

            var defaultProvider = SettingsManager.GetDefaultProvider();
            var defaultModel = SettingsManager.GetDefaultModel();
            var selector = new ModelSelectorComponent(
                _ui,
                Session.Model,
                Session.ModelRuntime,
                [.. Session.ScopedModels.Select(sm => new ModelSelectorComponent.ScopedModelItem
                {
                    Model = sm.Model,
                    ThinkingLevel = sm.ThinkingLevel is { } scopedLevel
                        ? Pi.Ai.Types.ThinkingLevelText.ToText(scopedLevel)
                        : null,
                })],
                model => _ = SelectModelAsync(model, persist: false),
                () =>
                {
                    done();
                    _ui.RequestRender();
                },
                initialSearchInput,
                model => _ = SelectModelAsync(model, persist: true),
                defaultProvider is not null && defaultModel is not null
                    ? new ModelSelectorComponent.DefaultModelReference { Provider = defaultProvider, Id = defaultModel }
                    : null);
            return (selector, selector, () => selector.Dispose());
        });
    }

    /// <summary>TS <c>handleClearCommand</c> (L6899-6912).</summary>
    internal async Task HandleClearCommandAsync()
    {
        ClearStatusIndicator();
        try
        {
            var result = await _runtimeHost.NewSessionAsync().ConfigureAwait(true);
            if (result.Cancelled)
            {
                return;
            }

            _chatContainer.AddChild(new Spacer(1));
            _chatContainer.AddChild(new ThemedText(() => ThemeRuntime.Current.Fg("accent", "✓ New session started"), 1, 1));
            _ui.RequestRender();
        }
        catch (Exception error)
        {
            await HandleFatalRuntimeErrorAsync("Failed to create session", error).ConfigureAwait(true);
        }
    }

    /// <summary>TS <c>handleFatalRuntimeError</c> (L2109-2123): report, record, restore terminal, exit.</summary>
    private async Task HandleFatalRuntimeErrorAsync(string prefix, Exception error)
    {
        ShowError($"{prefix}: {error.Message}");
        var extensionHint = GetCrashExtensionHintForUi(error);
        if (extensionHint is not null)
        {
            _chatContainer.AddChild(new ThemedText(
                () => ThemeRuntime.Current.Fg("warning", extensionHint), _outputPad, 0));
        }

        if (RecordCrashForUi(CrashKind.FatalError, error))
        {
            var instructions = InteractiveModeDiagnostics.CrashReportInstructions(SessionManager.GetSessionFile() is not null);
            _chatContainer.AddChild(new ThemedText(
                () => ThemeRuntime.Current.Fg("muted", instructions), _outputPad, 0));
        }

        // TS stopThemeWatcher(): the theme watcher dies with the process here; the port's watcher
        // lifecycle is owned by the ThemeRuntime seams and needs no explicit stop on the fatal path.
        Stop("transcript");
        Environment.Exit(1);
    }

    /// <summary>TS <c>handleDebugCommand</c> (L6914-6945).</summary>
    internal void HandleDebugCommand()
    {
        var width = _ui.Terminal.Columns;
        var height = _ui.Terminal.Rows;
        var allLines = _ui.Render(width);

        var debugLogPath = Config.GetDebugLogPath();
        var debugParts = new List<string>
        {
            $"Debug output at {NowIso()}",
            $"Terminal: {width}x{height}",
            $"Total lines: {allLines.Length}",
            "",
            "=== All rendered lines with visible widths ===",
        };
        debugParts.AddRange(allLines.Select((line, index) =>
            $"[{index}] (w={UnicodeWidth.VisibleWidth(line)}) {JsonSerializer.Serialize(line)}"));
        debugParts.Add("");
        debugParts.Add("=== Agent messages (JSONL) ===");
        debugParts.AddRange(Session.Messages.Select(message => JsonSerializer.Serialize(
            message,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
        debugParts.Add("");
        var debugData = string.Join("\n", debugParts);

        Directory.CreateDirectory(Path.GetDirectoryName(debugLogPath)!);
        File.WriteAllText(debugLogPath, debugData);

        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new ThemedText(
            () => $"{ThemeRuntime.Current.Fg("accent", "✓ Debug log written")}\n{ThemeRuntime.Current.Fg("muted", debugLogPath)}",
            1,
            1));
        _ui.RequestRender();
    }

    // ---------------------------------------------------------------- command methods (L4454-4591, L6610-6639)

    /// <summary>TS <c>handleFollowUp</c> (L4454-4484).</summary>
    internal async Task HandleFollowUpAsync()
    {
        // TS reads `editor.getExpandedText?.() ?? editor.getText()`; the optional expansion API is
        // not part of the editor port yet, so the getText fallback is the only path.
        var text = _editor.GetText().Trim();
        if (text.Length == 0)
        {
            return;
        }

        // Queue input during compaction (extension commands execute immediately)
        if (Session.IsCompacting)
        {
            if (IsExtensionCommand(text))
            {
                _editor.AddToHistory(text);
                _editor.SetText("");
                await Session.PromptAsync(text).ConfigureAwait(true);
            }
            else
            {
                QueueCompactionMessage(text, CompactionQueueModes.FollowUp);
            }

            return;
        }

        // Alt+Enter queues a follow-up message (waits until agent finishes).
        // This handles extension commands (execute immediately), prompt template expansion, and queueing.
        if (Session.IsStreaming)
        {
            _editor.AddToHistory(text);
            _editor.SetText("");
            await Session.PromptAsync(text, new PromptOptions { StreamingBehavior = "followUp" }).ConfigureAwait(true);
            UpdatePendingMessagesDisplay();
            _ui.RequestRender();
        }
        else if (_editor.OnSubmit is { } onSubmit)
        {
            // If not streaming, Alt+Enter acts like regular Enter (trigger onSubmit)
            _editor.SetText("");
            onSubmit(text);
        }
    }

    /// <summary>TS <c>handleDequeue</c> (L4486-4493).</summary>
    internal void HandleDequeue()
    {
        var restored = RestoreQueuedMessagesToEditor();
        if (restored == 0)
        {
            ShowStatus("No queued messages to restore");
        }
        else
        {
            ShowStatus($"Restored {restored} queued message{(restored > 1 ? "s" : "")} to editor");
        }
    }

    /// <summary>TS <c>cycleThinkingLevel</c> (L4506-4515).</summary>
    internal void CycleThinkingLevel()
    {
        var newLevel = Session.CycleThinkingLevel();
        if (newLevel is null)
        {
            ShowStatus("Current model does not support thinking");
        }
        else
        {
            _footer.Invalidate();
            UpdateEditorBorderColor();
            ShowStatus($"Thinking level: {Pi.Ai.Types.ThinkingLevelText.ToText(newLevel.Value)}");
        }
    }

    /// <summary>TS <c>cycleModel</c> (L4517-4534).</summary>
    internal async Task CycleModelAsync(string direction)
    {
        try
        {
            var result = await Session.CycleModelAsync(direction).ConfigureAwait(true);
            if (result is null)
            {
                var msg = Session.ScopedModels.Count > 0 ? "Only one model in scope" : "Only one model available";
                ShowStatus(msg);
            }
            else
            {
                _footer.Invalidate();
                UpdateEditorBorderColor();
                var thinkingStr = result.Model.Reasoning && result.ThinkingLevel != ThinkingLevel.Off
                    ? $" (thinking: {Pi.Ai.Types.ThinkingLevelText.ToText(result.ThinkingLevel)})"
                    : "";
                ShowStatus($"Switched to {result.Model.Name ?? result.Model.Id}{thinkingStr}");
                _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync(result.Model);
            }
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    /// <summary>TS <c>handleOpenExternalEditor</c> (L4575-4591).</summary>
    internal async Task HandleOpenExternalEditorAsync()
    {
        var editorCommand = SettingsManager.GetExternalEditorCommand();
        var content = _editor.GetText();
        _ui.Stop();
        try
        {
            var result = await ExternalEditor.EditInExternalEditorAsync(new ExternalEditorOptions
            {
                Command = editorCommand,
                Content = content,
            }).ConfigureAwait(true);
            if (result is ExternalEditorResult.Complete complete)
            {
                _editor.SetText(complete.Content);
            }
        }
        finally
        {
            _ui.Start();
            _ui.RequestRender(true);
        }
    }

    /// <summary>TS <c>handleCopyCommand</c> (L6610-6639).</summary>
    internal async Task HandleCopyCommandAsync(bool flashConfirmation = false, bool preferSelection = false)
    {
        if (preferSelection
            && _renderer is TuiAltScreen selectionScreen
            && !selectionScreen.GetCopyOnSelect()
            && selectionScreen.HasActiveSelection())
        {
            await selectionScreen.CopyActiveSelectionToClipboardAsync().ConfigureAwait(true);
            return;
        }

        var text = Session.GetLastAssistantText();
        if (string.IsNullOrEmpty(text))
        {
            ShowError("No agent messages to copy yet.");
            return;
        }

        try
        {
            await Clipboard.CopyToClipboardAsync(text).ConfigureAwait(true);
            if (flashConfirmation && _renderer is TuiAltScreen flashScreen)
            {
                flashScreen.Flash("Copied!");
            }
            else
            {
                ShowStatus("Copied last agent message to clipboard");
            }
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
    }

    /// <summary>Seam: double-escape → tree / user-message selector lands with 4f-4m-4.</summary>
    private void ShowDoubleEscapeSelector(string action) =>
        throw new NotImplementedException($"4f-4m-4 command slice: double-escape action '{action}' (TS L3065-3070)");

    /// <summary>Test seam: the default-arg path of the anthropic warning check.</summary>
    internal Task MaybeWarnAboutAnthropicSubscriptionAuthForTestsAsync() =>
        MaybeWarnAboutAnthropicSubscriptionAuthAsync();

    /// <summary>TS <c>maybeWarnAboutAnthropicSubscriptionAuth</c> (L5240-5268).</summary>
    private async Task MaybeWarnAboutAnthropicSubscriptionAuthAsync(Pi.Ai.Models.ModelSpec? model = null)
    {
        var warnings = SettingsManager.GetWarnings();
        if (warnings.TryGetPropertyValue("anthropicExtraUsage", out var flag)
            && flag is JsonValue { } jsonValue
            && jsonValue.TryGetValue<bool>(out var enabled)
            && !enabled)
        {
            return;
        }

        if (_anthropicSubscriptionWarningShown)
        {
            return;
        }

        model ??= Session.Model;
        if (model is null || model.Provider != "anthropic")
        {
            return;
        }

        try
        {
            if ((await Session.ModelRuntime.CheckAuthAsync("anthropic").ConfigureAwait(true))?.Type == Pi.Ai.Auth.CredentialKind.OAuth)
            {
                _anthropicSubscriptionWarningShown = true;
                ShowWarning(InteractiveModeSupport.AnthropicSubscriptionAuthWarning);
                return;
            }

            var apiKey = (await Session.ModelRuntime.GetAuthAsync(model.Provider).ConfigureAwait(true))?.Auth?.ApiKey;
            if (!InteractiveModeSupport.IsAnthropicSubscriptionAuthKey(apiKey))
            {
                return;
            }

            _anthropicSubscriptionWarningShown = true;
            ShowWarning(InteractiveModeSupport.AnthropicSubscriptionAuthWarning);
        }
        catch
        {
            // Ignore auth lookup failures for warning-only checks.
        }
    }

    /// <summary>TS <c>takeUnnotifiedCrash()</c>: the newest recent crash, marked as announced.</summary>
    internal (DateTimeOffset Timestamp, string Message)? TakeUnnotifiedCrashForWarning(string? path = null)
    {
        var crash = CrashLog.TakeUnnotified(path);
        if (crash is null)
        {
            return null;
        }

        var timestamp = DateTimeOffset.TryParse(crash.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
        var message = crash.Members?.TryGetValue("message", out var messageElement) == true
            ? messageElement.GetString() ?? ""
            : "";
        return (timestamp, message);
    }

    /// <summary>TS instance <c>recordCrash(kind, error)</c>: best-effort persistence; returns whether it recorded.</summary>
    internal bool RecordCrashForUi(string kind, Exception error, string? path = null) =>
        CrashLog.Record(new CrashInput
        {
            Kind = kind,
            Error = error,
            SessionFile = SessionManager.GetSessionFile(),
            Cwd = SessionManager.GetCwd(),
        }, path) is not null;

    /// <summary>
    /// TS <c>this.getCrashExtensionHint(error)</c>: the error stack matched against the loaded
    /// extensions (try/catch upstream — any throw collapses rather than breaking the crash report).
    /// </summary>
    internal string? GetCrashExtensionHintForUi(Exception error)
    {
        try
        {
            return InteractiveModeExtensions.GetCrashExtensionHint(
                error.StackTrace,
                Session.ExtensionRunner.GetExtensionStackMetadata());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>TS <c>showNewVersionNotification</c> (L4620-4644).</summary>
    internal void ShowNewVersionNotification(LatestPiRelease release)
    {
        string UpdateInstruction() =>
            ThemeRuntime.Current.Fg("muted", $"New version {release.Version} is available. Run ")
            + ThemeRuntime.Current.Fg("accent", $"{Config.AppName} update");
        const string changelogUrl = "https://pi.dev/changelog";
        string ChangelogLine()
        {
            var changelogLink = TerminalImage.GetCapabilities().Hyperlinks
                ? TerminalImage.Hyperlink(ThemeRuntime.Current.Fg("accent", changelogUrl), changelogUrl)
                : ThemeRuntime.Current.Fg("accent", changelogUrl);
            return ThemeRuntime.Current.Fg("muted", "Changelog: ") + changelogLink;
        }

        var note = release.Note?.Trim();

        _chatContainer.AddChild(new Spacer(1));
        _chatContainer.AddChild(new DynamicBorder(text => ThemeRuntime.Current.Fg("warning", text)));
        _chatContainer.AddChild(new ThemedText(
            () => $"{ThemeRuntime.Current.Bold(ThemeRuntime.Current.Fg("warning", "Update Available"))}\n{UpdateInstruction()}",
            1,
            0));
        if (!string.IsNullOrEmpty(note))
        {
            _chatContainer.AddChild(new Spacer(1));
            _chatContainer.AddChild(new Markdown(
                note,
                1,
                0,
                GetMarkdownThemeWithSettings(),
                new DefaultTextStyle { Color = text => ThemeRuntime.Current.Fg("muted", text) }));
            _chatContainer.AddChild(new Spacer(1));
        }

        _chatContainer.AddChild(new ThemedText(ChangelogLine, 1, 0));
        _chatContainer.AddChild(new DynamicBorder(text => ThemeRuntime.Current.Fg("warning", text)));
        _ui.RequestRender();
    }
}
