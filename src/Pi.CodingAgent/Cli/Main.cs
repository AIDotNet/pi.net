// ============================================================================
// Main — port of main.ts (4g-2)
// ============================================================================
//
// CLI argument parsing plus the translation into createAgentSession options. The SDK does the
// heavy lifting, exactly as in TS.
//
// Port differences (see docs/coding-agent-porting-status.md):
// - `process.exit` is an injected hook; the default throws `MainExitException`, which the entry
//   point turns into the process exit code (C303). `process.exitCode = n` stays a local.
// - `cleanupManagedInstall()` and `cleanupWindowsSelfUpdateQuarantine()` are not ported: both
//   belong to the npm / managed-install self-update path that the .NET build does not have (C301).
// - `configureHttpDispatcher()` is the platform gap already recorded as C32; only
//   `applyHttpProxySettings` remains.
// - `readPipedStdin` uses Console.In (no TTY), `promptConfirm` uses Console.ReadLine.

using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Extensions.Mcp;
using Pi.CodingAgent.Modes;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Rpc;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Cli;

/// <summary>Thrown by the injected exit hook so callers can observe the code. Port seam (C303).</summary>
public sealed class MainExitException(int exitCode) : Exception($"exit({exitCode})")
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>Port of the TS <c>MainOptions</c>.</summary>
public sealed record MainOptions
{
    public IReadOnlyList<InlineExtension>? ExtensionFactories { get; init; }

    /// <summary>Exit hook. Defaults to throwing <see cref="MainExitException"/>.</summary>
    public Action<int>? Exit { get; init; }

    /// <summary>Working directory. Defaults to <see cref="Environment.CurrentDirectory"/>.</summary>
    public string? Cwd { get; init; }

    /// <summary>Agent dir. Defaults to <see cref="Config.GetAgentDir()"/>.</summary>
    public string? AgentDir { get; init; }

    /// <summary>stdout writer. Defaults to <see cref="Console.Out"/>.</summary>
    public TextWriter? Out { get; init; }

    /// <summary>stderr writer. Defaults to <see cref="Console.Error"/>.</summary>
    public TextWriter? Error { get; init; }

    /// <summary>Overrides <c>process.stdin.isTTY</c>. Defaults to <c>!Console.IsInputRedirected</c>.</summary>
    public bool? StdinIsTty { get; init; }

    /// <summary>Overrides <c>process.stdout.isTTY</c>. Defaults to <c>!Console.IsOutputRedirected</c>.</summary>
    public bool? StdoutIsTty { get; init; }

    /// <summary>Piped stdin reader. Defaults to reading <see cref="Console.In"/> to EOF.</summary>
    public Func<Task<string?>>? ReadPipedStdin { get; init; }

    /// <summary>Interactive yes/no prompt. Defaults to <see cref="Console.ReadLine"/>.</summary>
    public Func<string, Task<bool>>? PromptConfirm { get; init; }

    /// <summary>Environment accessor. Defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    public Func<string, string?>? GetEnv { get; init; }

    /// <summary>Environment writer. Defaults to <see cref="Environment.SetEnvironmentVariable(string, string?)"/>.</summary>
    public Action<string, string?>? SetEnv { get; init; }
}

/// <summary>Outcome of <see cref="Main.RunAuthCommandAsync"/>: handled + the exit code to use.</summary>
public sealed record AuthCommandRunResult(bool Handled, int ExitCode)
{
    public static AuthCommandRunResult NotHandled { get; } = new(false, 0);

    public static AuthCommandRunResult Done(int exitCode = 0) => new(true, exitCode);
}

/// <summary>Port of the TS <c>ResolvedSession</c> union.</summary>
public abstract record ResolvedSession
{
    /// <summary>A path-looking argument, resolved as-is.</summary>
    public sealed record PathKind(string Path) : ResolvedSession;

    /// <summary>Found in the current project.</summary>
    public sealed record Local(string Path) : ResolvedSession;

    /// <summary>Found in a different project.</summary>
    public sealed record Global(string Path, string Cwd) : ResolvedSession;

    /// <summary>Not found anywhere.</summary>
    public sealed record NotFound(string Arg) : ResolvedSession;
}

/// <summary>Port of <c>main.ts</c>.</summary>
public static class Main
{
    private const string ExtensionLoadFailureHintTemplate = "Hint: Start without extensions using \"{0} -ne\".";

    /// <summary>
    /// Port of <c>main</c>. Returns the process exit code; the TS version sets
    /// <c>process.exitCode</c> / calls <c>process.exit</c> instead.
    /// </summary>
    public static async Task<int> MainAsync(string[] args, MainOptions? options = null)
    {
        options ??= new MainOptions();
        var output = options.Out ?? Console.Out;
        var error = options.Error ?? Console.Error;
        var getEnv = options.GetEnv ?? Environment.GetEnvironmentVariable;
        var setEnv = options.SetEnv ?? ((name, value) => Environment.SetEnvironmentVariable(name, value));
        var exitCode = 0;

        if (args.Length > 0 && args[0] == "doctor")
        {
            return await DoctorCommand.RunAsync(args.Skip(1).ToArray(), new DoctorOptions
            {
                Cwd = options.Cwd ?? Environment.CurrentDirectory,
                AgentDir = options.AgentDir ?? Config.GetAgentDir(),
                GetEnv = getEnv,
            }, output, error).ConfigureAwait(false);
        }

        void Exit(int code) => (options.Exit ?? (c => throw new MainExitException(c)))(code);

        Timings.Reset();
        var extensionFactories = new List<InlineExtension>(BuiltInExtensions.All);
        if (options.ExtensionFactories is not null)
        {
            extensionFactories.AddRange(options.ExtensionFactories);
        }

        var offlineMode = args.Contains("--offline") || IsTruthyEnvFlag(getEnv("PI_OFFLINE"));
        if (offlineMode)
        {
            setEnv("PI_OFFLINE", "1");
            setEnv("PI_SKIP_VERSION_CHECK", "1");
        }

        var authResult = await RunAuthCommandAsync(args, output, error, Exit).ConfigureAwait(false);
        if (authResult.Handled)
        {
            return authResult.ExitCode;
        }

        // TS: cleanupWindowsSelfUpdateQuarantine(getPackageDir()) on win32 + cleanupManagedInstall().
        // Both belong to the npm / managed-install self-update path (C301).

        var cwd = options.Cwd ?? Environment.CurrentDirectory;
        var agentDir = options.AgentDir ?? Config.GetAgentDir();
        var bootstrapSettingsManager = SettingsManager.Create(cwd, agentDir, new SettingsManagerCreateOptions
        {
            ProjectTrusted = false,
        });
        HttpDispatcher.ApplyHttpProxySettings(bootstrapSettingsManager.GetGlobalSettings().HttpProxy);
        // TS also calls configureHttpDispatcher() here (platform gap C32).

        var packageRuntimeOptions = new PackageCommandRuntimeOptions
        {
            ExtensionFactories = extensionFactories,
            Cwd = cwd,
            AgentDir = agentDir,
            Out = output,
            Error = error,
            Interactive = options.StdinIsTty ?? !Console.IsInputRedirected,
        };
        var packageResult = await PackageManagerCli.HandlePackageCommandAsync(args, packageRuntimeOptions)
            .ConfigureAwait(false);
        if (packageResult.Handled)
        {
            return packageResult.ExitCode;
        }

        var configResult = await PackageManagerCli.HandleConfigCommandAsync(args, packageRuntimeOptions)
            .ConfigureAwait(false);
        if (configResult.Handled)
        {
            return configResult.ExitCode;
        }

        if (args.Length > 0 && args[0] == "mcp")
        {
            exitCode = await McpCli.RunMcpCommandAsync(
                args.Skip(1).ToArray(),
                new McpCommandOptions { Cwd = cwd, AgentDir = agentDir }).ConfigureAwait(false);
            return exitCode;
        }

        var parsed = CliArgs.Parse(args);
        if (parsed.Diagnostics.Count > 0)
        {
            foreach (var diagnostic in parsed.Diagnostics)
            {
                Func<string, string> color = diagnostic.Type == AgentSessionDiagnosticType.Error
                    ? Chalk.Red
                    : Chalk.Yellow;
                var prefix = diagnostic.Type == AgentSessionDiagnosticType.Error ? "Error" : "Warning";
                error.WriteLine(color($"{prefix}: {diagnostic.Message}"));
            }

            if (parsed.Diagnostics.Any(diagnostic => diagnostic.Type == AgentSessionDiagnosticType.Error))
            {
                Exit(1);
            }
        }

        Timings.Time("parseArgs");

        if (parsed.Version == true)
        {
            output.WriteLine(Config.Version);
            Exit(0);
        }

        if (parsed.Export is not null)
        {
            string result;
            try
            {
                var outputPath = parsed.Messages.Count > 0 ? parsed.Messages[0] : null;
                result = ExportHtml.ExportFromFile(parsed.Export, outputPath is null
                    ? null
                    : new ExportHtmlOptions { OutputPath = outputPath });
            }
            catch (Exception ex)
            {
                error.WriteLine(Chalk.Red($"Error: {ex.Message}"));
                Exit(1);
                return 1;
            }

            output.WriteLine($"Exported to: {result}");
            Exit(0);
        }

        var stdinIsTty = options.StdinIsTty ?? !Console.IsInputRedirected;
        var stdoutIsTty = options.StdoutIsTty ?? !Console.IsOutputRedirected;
        var appMode = ResolveAppMode(parsed, stdinIsTty, stdoutIsTty);
        var shouldTakeOverStdout = appMode != AppModes.Interactive && !IsPlainRuntimeMetadataCommand(parsed);
        if (shouldTakeOverStdout)
        {
            OutputGuard.TakeOverStdout();
        }

        if (parsed.Mode == CliMode.Rpc && parsed.FileArgs.Count > 0)
        {
            error.WriteLine(Chalk.Red("Error: @file arguments are not supported in RPC mode"));
            Exit(1);
        }

        ValidateForkFlags(parsed, error, Exit);
        ValidateSessionIdFlags(parsed, error, Exit);

        var migration = Migrations.RunMigrations(cwd);
        Timings.Time("runMigrations");

        var startupSettingsManager = SettingsManager.Create(cwd, agentDir);
        var startupSettingsDiagnostics = SettingsDiagnostics.CollectSettingsDiagnostics(startupSettingsManager);

        if (appMode == AppModes.Interactive
            && parsed.Help != true
            && parsed.ListModels is null
            && StartupUi.ShouldRunFirstTimeSetup())
        {
            await StartupUi.ShowFirstTimeSetupAsync(startupSettingsManager).ConfigureAwait(false);
            Timings.Time("firstTimeSetup");
        }

        if (appMode == AppModes.Interactive && parsed.UseTheme is not null)
        {
            startupSettingsManager.ApplyOverrides(new Settings { Theme = parsed.UseTheme });
        }

        var envSessionDir = getEnv(Config.EnvSessionDir);
        var sessionDir = parsed.SessionDir is not null
            ? Paths.NormalizePath(parsed.SessionDir)
            : envSessionDir is not null
                ? Config.ExpandTildePath(envSessionDir)
                : startupSettingsManager.GetSessionDir();
        var sessionManager = await CreateSessionManagerAsync(
            parsed,
            cwd,
            sessionDir,
            startupSettingsManager,
            output,
            error,
            Exit,
            options.PromptConfirm).ConfigureAwait(false);
        var missingSessionCwdIssue = SessionCwd.GetMissingSessionCwdIssue(sessionManager, cwd);
        if (missingSessionCwdIssue is not null)
        {
            if (appMode == AppModes.Interactive)
            {
                var selectedCwd = await PromptForMissingSessionCwdAsync(missingSessionCwdIssue, startupSettingsManager)
                    .ConfigureAwait(false);
                if (selectedCwd is null)
                {
                    Exit(0);
                    return 0;
                }

                sessionManager = SessionManager.Open(
                    missingSessionCwdIssue.SessionFile!,
                    sessionDir,
                    selectedCwd);
            }
            else
            {
                error.WriteLine(Chalk.Red(SessionCwd.FormatMissingSessionCwdError(missingSessionCwdIssue)));
                Exit(1);
            }
        }

        if (parsed.Name is not null)
        {
            var name = CliArgs.NormalizeSessionName(parsed.Name);
            if (name is null)
            {
                error.WriteLine(Chalk.Red("Error: --name requires a non-empty value"));
                Exit(1);
            }
            else
            {
                sessionManager.AppendSessionInfo(name);
            }
        }

        Timings.Time("createSessionManager");

        var trustStore = new ProjectTrustStore(agentDir);
        var sessionCwd = sessionManager.GetCwd();
        var autoTrustOnReloadCwd =
            parsed.ProjectTrustOverride is null && !TrustManager.HasTrustRequiringProjectResources(sessionCwd)
                ? sessionCwd
                : null;
        var trustPromptMode = parsed.Help == true || parsed.ListModels is not null ? AppModes.Print : appMode;
        var projectTrustByCwd = new Dictionary<string, bool>(StringComparer.Ordinal);

        var resolvedExtensionPaths = ResolveCliPaths(cwd, parsed.Extensions);
        var resolvedSkillPaths = ResolveCliPaths(cwd, parsed.Skills);
        var resolvedPromptTemplatePaths = ResolveCliPaths(cwd, parsed.PromptTemplates);
        var resolvedThemePaths = ResolveCliPaths(cwd, parsed.Themes);

        CreateAgentSessionRuntimeFactory createRuntime = async runtimeOptions =>
        {
            var isInitialRuntime = runtimeOptions.SessionStartEvent is null;
            var projectTrustDiagnostics = new List<AgentSessionRuntimeDiagnostic>();
            var hasCachedTrust = projectTrustByCwd.TryGetValue(runtimeOptions.Cwd, out var cachedProjectTrust);
            var hasTrustRequiringResources = TrustManager.HasTrustRequiringProjectResources(runtimeOptions.Cwd);
            var shouldResolveProjectTrust =
                parsed.ProjectTrustOverride is null && !hasCachedTrust && hasTrustRequiringResources;
            var projectTrusted = shouldResolveProjectTrust
                ? false
                : hasCachedTrust
                    ? cachedProjectTrust
                    : parsed.ProjectTrustOverride
                      ?? (!hasTrustRequiringResources || trustStore.Get(runtimeOptions.Cwd) == true);
            var runtimeSettingsManager = SettingsManager.Create(
                runtimeOptions.Cwd,
                runtimeOptions.AgentDir,
                new SettingsManagerCreateOptions { ProjectTrusted = projectTrusted });

            using var modelRuntimeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var services = await AgentSessionServicesBuilder.CreateAgentSessionServicesAsync(
                new CreateAgentSessionServicesOptions
                {
                    Cwd = runtimeOptions.Cwd,
                    AgentDir = runtimeOptions.AgentDir,
                    SettingsManager = runtimeSettingsManager,
                    ModelRuntimeSignal = modelRuntimeCts.Token,
                    ExtensionFlagValues = ToExtensionFlagValues(parsed.UnknownFlags),
                    ResourceLoaderReloadOptions = shouldResolveProjectTrust
                        ? new ResourceLoaderReloadOptions
                        {
                            ResolveProjectTrust = async request =>
                            {
                                var trusted = await ProjectTrust.ResolveProjectTrustedAsync(
                                    new ResolveProjectTrustedOptions
                                    {
                                        Cwd = runtimeOptions.Cwd,
                                        TrustStore = trustStore,
                                        TrustOverride = parsed.ProjectTrustOverride,
                                        DefaultProjectTrust = startupSettingsManager.GetDefaultProjectTrust(),
                                        ExtensionsResult = request.ExtensionsResult,
                                        ProjectTrustContext = runtimeOptions.ProjectTrustContext
                                            ?? ProjectTrustUi.CreateProjectTrustContext(new ProjectTrustUiOptions
                                            {
                                                Cwd = runtimeOptions.Cwd,
                                                Mode = isInitialRuntime ? trustPromptMode : appMode,
                                                SettingsManager = startupSettingsManager,
                                                HasUI = isInitialRuntime && trustPromptMode == AppModes.Interactive,
                                            }),
                                        OnExtensionError = message =>
                                            projectTrustDiagnostics.Add(
                                                new AgentSessionRuntimeDiagnostic { Type = AgentSessionDiagnosticType.Warning, Message = message }),
                                    }).ConfigureAwait(false);
                                projectTrustByCwd[runtimeOptions.Cwd] = trusted;
                                return trusted;
                            },
                        }
                        : null,
                    ResourceLoaderOptions = new ResourceLoaderExtraOptions
                    {
                        AdditionalExtensionPaths = resolvedExtensionPaths,
                        AdditionalSkillPaths = resolvedSkillPaths,
                        AdditionalPromptTemplatePaths = resolvedPromptTemplatePaths,
                        AdditionalThemePaths = resolvedThemePaths,
                        NoExtensions = parsed.NoExtensions == true,
                        DisabledBuiltinExtensions = parsed.NoMcp == true ? ["mcp"] : null,
                        NoSkills = parsed.NoSkills == true,
                        NoPromptTemplates = parsed.NoPromptTemplates == true,
                        NoThemes = parsed.NoThemes == true,
                        NoContextFiles = parsed.NoContextFiles == true,
                        SystemPrompt = parsed.SystemPrompt,
                        AppendSystemPrompt = parsed.AppendSystemPrompt,
                        ExtensionFactories = extensionFactories,
                    },
                }).ConfigureAwait(false);

            var runtimeSettings = services.SettingsManager;
            var modelRuntime = services.ModelRuntime;
            var resourceLoader = services.ResourceLoader;
            var diagnostics = new List<AgentSessionRuntimeDiagnostic>();
            diagnostics.AddRange(projectTrustDiagnostics);
            diagnostics.AddRange(services.Diagnostics);
            diagnostics.AddRange(SettingsDiagnostics.CollectSettingsDiagnostics(runtimeSettings));
            var loadedExtensions = resourceLoader.GetExtensions();
            diagnostics.AddRange(loadedExtensions.Errors.Select(loadError =>
                new AgentSessionRuntimeDiagnostic { Type = AgentSessionDiagnosticType.Error, Message = $"Failed to load extension \"{loadError.Path}\": {loadError.Error}" }));
            diagnostics.AddRange((loadedExtensions.Warnings ?? []).Select(warning =>
                new AgentSessionRuntimeDiagnostic { Type = AgentSessionDiagnosticType.Warning, Message = $"Extension package \"{warning.Path}\": {warning.Warning}" }));

            var modelPatterns = parsed.Models ?? runtimeSettings.GetEnabledModels();
            using var scopeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var scopedModels = modelPatterns is { Count: > 0 }
                ? await ModelResolver.ResolveModelScopeAsync(modelPatterns, modelRuntime, scopeCts.Token)
                    .ConfigureAwait(false)
                : [];
            var (sessionOptions, cliThinkingFromModel, sessionOptionDiagnostics) = BuildSessionOptions(
                parsed,
                scopedModels,
                sessionManager.BuildSessionContext().Messages.Count > 0,
                modelRuntime,
                runtimeSettings);
            diagnostics.AddRange(sessionOptionDiagnostics);

            if (parsed.ApiKey is not null)
            {
                if (sessionOptions.Model is null)
                {
                    diagnostics.Add(new AgentSessionRuntimeDiagnostic { Type = AgentSessionDiagnosticType.Error, Message = "--api-key requires a model to be specified via --model, --provider/--model, or --models" });
                }
                else
                {
                    await modelRuntime.SetRuntimeApiKeyAsync(sessionOptions.Model.Provider, parsed.ApiKey)
                        .ConfigureAwait(false);
                }
            }

            var created = await AgentSessionServicesBuilder.CreateAgentSessionFromServicesAsync(
                new CreateAgentSessionFromServicesOptions
                {
                    Services = services,
                    SessionManager = sessionManager,
                    SessionStartEvent = runtimeOptions.SessionStartEvent,
                    Model = sessionOptions.Model,
                    ThinkingLevel = sessionOptions.ThinkingLevel,
                    ScopedModels = sessionOptions.ScopedModels,
                    Tools = sessionOptions.Tools,
                    ExcludeTools = sessionOptions.ExcludeTools,
                    NoTools = sessionOptions.NoTools,
                    CustomTools = sessionOptions.CustomTools,
                }).ConfigureAwait(false);
            var cliThinkingOverride = parsed.Thinking is not null || cliThinkingFromModel;
            if (created.Session.Model is not null && cliThinkingOverride)
            {
                created.Session.SetThinkingLevel(created.Session.ThinkingLevel);
            }

            return new CreateAgentSessionRuntimeResult
            {
                Session = created.Session,
                ExtensionsResult = created.ExtensionsResult,
                ModelFallbackMessage = created.ModelFallbackMessage,
                Services = services,
                Diagnostics = diagnostics,
            };
        };

        Timings.Time("createRuntime");
        var runtime = await AgentSessionRuntime.CreateAgentSessionRuntimeAsync(createRuntime,
            new CreateAgentSessionRuntimeOptions
            {
                Cwd = sessionManager.GetCwd(),
                AgentDir = agentDir,
                SessionManager = sessionManager,
            }).ConfigureAwait(false);
        Timings.Time("createAgentSessionRuntime");
        var services = runtime.Services;
        var session = runtime.Session;
        var modelFallbackMessage = runtime.ModelFallbackMessage;
        var runtimeSettingsManager = services.SettingsManager;
        var runtimeModelRuntime = services.ModelRuntime;
        var runtimeResourceLoader = services.ResourceLoader;
        TerminalImage.SetCapabilityOverrides(
            InteractiveMode.ToTuiCapabilityOverrides(runtimeSettingsManager.GetTerminalCapabilityOverrides()));
        HttpDispatcher.ApplyHttpProxySettings(runtimeSettingsManager.GetGlobalSettings().HttpProxy);
        // TS also calls configureHttpDispatcher(getHttpIdleTimeoutMs()) here (C32 / C267).

        if (parsed.Help == true)
        {
            ReportDiagnostics(startupSettingsDiagnostics, error);
            var extensionFlags = runtimeResourceLoader.GetExtensions().Extensions
                .SelectMany(extension => extension.Flags.Values)
                .ToList();
            CliArgs.PrintHelp(extensionFlags);
            Exit(0);
        }

        if (parsed.ListModels is not null)
        {
            ReportDiagnostics(startupSettingsDiagnostics, error);
            var searchPattern = parsed.ListModelsSearch;
            using var listModelsCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await ListModels.ListModelsAsync(runtimeModelRuntime, searchPattern, listModelsCts.Token)
                .ConfigureAwait(false);
            Exit(0);
        }

        // Read piped stdin content (if any) — skip for RPC mode, which uses stdin for JSON-RPC.
        string? stdinContent = null;
        if (appMode != AppModes.Rpc)
        {
            stdinContent = await (options.ReadPipedStdin ?? ReadPipedStdinAsync)().ConfigureAwait(false);
            if (stdinContent is not null && appMode == AppModes.Interactive)
            {
                appMode = AppModes.Print;
            }
        }

        Timings.Time("readPipedStdin");

        var (initialMessage, initialImages) = await PrepareInitialMessageAsync(parsed, stdinContent)
            .ConfigureAwait(false);
        Timings.Time("prepareInitialMessage");

        // pi reads user-authored themes, so it opts into full validation before any theme loads.
        ThemeRuntime.SetThemeJsonValidator(ThemeJson.ValidateThemeJson);
        ThemeRuntime.InitTheme(runtimeSettingsManager.GetTheme(), appMode == AppModes.Interactive);
        Timings.Time("initTheme");

        if (appMode == AppModes.Interactive && migration.DeprecationWarnings.Count > 0)
        {
            await Migrations.ShowDeprecationWarningsAsync(migration.DeprecationWarnings).ConfigureAwait(false);
        }

        Timings.Time("resolveModelScope");
        var startupDiagnostics = SettingsDiagnostics.DeduplicateDiagnostics(
            [.. startupSettingsDiagnostics, .. runtime.Diagnostics]);
        var hasRuntimeErrors = runtime.Diagnostics.Any(diagnostic => diagnostic.Type == AgentSessionDiagnosticType.Error);
        if (appMode != AppModes.Interactive || hasRuntimeErrors)
        {
            ReportDiagnostics(startupDiagnostics, error);
        }

        if (hasRuntimeErrors)
        {
            if (runtime.Diagnostics.Any(diagnostic => diagnostic.Message.Contains(
                    "Failed to load extension",
                    StringComparison.Ordinal)))
            {
                error.WriteLine(Chalk.Yellow(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    ExtensionLoadFailureHintTemplate,
                    Config.AppName)));
            }

            Exit(1);
        }

        Timings.Time("createAgentSession");

        if (appMode != AppModes.Interactive && session.Model is null)
        {
            error.WriteLine(Chalk.Red(AuthGuidance.FormatNoModelsAvailableMessage()));
            Exit(1);
        }

        var startupBenchmark = IsTruthyEnvFlag(getEnv("PI_STARTUP_BENCHMARK"));
        if (startupBenchmark && appMode != AppModes.Interactive)
        {
            error.WriteLine(Chalk.Red("Error: PI_STARTUP_BENCHMARK only supports interactive mode"));
            Exit(1);
        }

        // RPC refreshes catalogs here in the background; interactive mode starts its refresh after
        // TUI initialization.
        if (!offlineMode && appMode == AppModes.Rpc)
        {
            var controller = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _ = runtimeModelRuntime.RefreshAsync(new ModelsRefreshOptions { Signal = controller.Token })
                .ContinueWith(_ => controller.Dispose(), TaskScheduler.Default);
        }

        if (appMode == AppModes.Rpc)
        {
            Timings.Print();
            await RpcMode.RunAsync(runtime).ConfigureAwait(false);
            return exitCode;
        }

        if (appMode == AppModes.Interactive)
        {
            var interactiveMode = new InteractiveMode(runtime, new InteractiveModeOptions
            {
                MigratedProviders = migration.MigratedAuthProviders,
                StartupDiagnostics = startupDiagnostics,
                ModelFallbackMessage = modelFallbackMessage,
                AutoTrustOnReloadCwd = autoTrustOnReloadCwd,
                InitialMessage = initialMessage,
                InitialImages = initialImages,
                InitialMessages = parsed.Messages,
                Verbose = parsed.Verbose,
                TuiMode = parsed.TuiMode,
                InitialThemeSetting = parsed.UseTheme,
            });

            if (startupBenchmark)
            {
                await interactiveMode.InitAsync().ConfigureAwait(false);
                Timings.Time("interactiveMode.init");
                // Give the TUI's stdin handler a brief chance to consume terminal query replies
                // (Kitty keyboard protocol, device attributes, cell size) before restoring.
                await Task.Delay(150).ConfigureAwait(false);
                interactiveMode.Stop();
                ThemeRuntime.StopThemeWatcher();
                Timings.Print();
                await output.FlushAsync().ConfigureAwait(false);
                await error.FlushAsync().ConfigureAwait(false);
                return exitCode;
            }

            Timings.Print();
            await interactiveMode.RunAsync().ConfigureAwait(false);
            return exitCode;
        }

        Timings.Print();
        var printExitCode = await PrintMode.RunAsync(runtime, new PrintModeOptions
        {
            Mode = ToPrintOutputMode(appMode),
            Messages = parsed.Messages,
            InitialMessage = initialMessage,
            InitialImages = initialImages,
        }).ConfigureAwait(false);
        ThemeRuntime.StopThemeWatcher();
        OutputGuard.RestoreStdout();
        return printExitCode != 0 ? printExitCode : exitCode;
    }

    // ========================================================================
    // 辅助（TS 同文件模块级函数）
    // ========================================================================

    /// <summary>Port of <c>readPipedStdin</c>: undefined when stdin is a TTY.</summary>
    private static async Task<string?> ReadPipedStdinAsync()
    {
        if (!Console.IsInputRedirected)
        {
            return null;
        }

        var data = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        var trimmed = data.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Port of <c>reportDiagnostics</c>.</summary>
    internal static void ReportDiagnostics(
        IReadOnlyList<AgentSessionRuntimeDiagnostic> diagnostics,
        TextWriter? error = null)
    {
        error ??= Console.Error;
        foreach (var diagnostic in diagnostics)
        {
            Func<string, string> color = diagnostic.Type == AgentSessionDiagnosticType.Error
                ? Chalk.Red
                : diagnostic.Type == AgentSessionDiagnosticType.Warning
                    ? Chalk.Yellow
                    : Chalk.Dim;
            var prefix = diagnostic.Type == AgentSessionDiagnosticType.Error ? "Error: " : diagnostic.Type == AgentSessionDiagnosticType.Warning ? "Warning: " : "";
            error.WriteLine(color($"{prefix}{diagnostic.Message}"));
        }
    }

    /// <summary>Port of <c>isTruthyEnvFlag</c>.</summary>
    internal static bool IsTruthyEnvFlag(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value == "1"
               || value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Port of <c>resolveAppMode</c>.</summary>
    internal static string ResolveAppMode(Args parsed, bool stdinIsTty, bool stdoutIsTty)
    {
        if (parsed.Mode == CliMode.Rpc)
        {
            return AppModes.Rpc;
        }

        if (parsed.Mode == CliMode.Json)
        {
            return AppModes.Json;
        }

        if (parsed.Print == true || !stdinIsTty || !stdoutIsTty)
        {
            return AppModes.Print;
        }

        return AppModes.Interactive;
    }

    /// <summary>Port of <c>toPrintOutputMode</c>.</summary>
    internal static string ToPrintOutputMode(string appMode) => appMode == AppModes.Json ? "json" : "text";

    /// <summary>Port of <c>isPlainRuntimeMetadataCommand</c>.</summary>
    internal static bool IsPlainRuntimeMetadataCommand(Args parsed) =>
        parsed.Print != true && parsed.Mode is null && (parsed.Help == true || parsed.ListModels is not null);

    /// <summary>
    /// Port of <c>runAuthCommand</c>. TS returns a bare boolean and writes
    /// <c>process.exitCode</c>; the port returns the code alongside so repeated in-process runs do
    /// not leak state (difference C307).
    /// </summary>
    internal static async Task<AuthCommandRunResult> RunAuthCommandAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        Action<int> exit)
    {
        if (AuthCommands.IsAuthCommandHelp(args))
        {
            AuthCommands.PrintAuthCommandHelp();
            return AuthCommandRunResult.Done();
        }

        AuthCommand command;
        try
        {
            var parsedCommand = AuthCommands.ParseAuthCommand(args);
            if (parsedCommand is null)
            {
                return AuthCommandRunResult.NotHandled;
            }

            command = parsedCommand;
        }
        catch (Exception ex)
        {
            var message = ex is AuthCommandError ? ex.Message : "Failed to parse auth command";
            error.WriteLine(Chalk.Red($"Error: {message}"));
            return AuthCommandRunResult.Done(1);
        }

        var authExitCode = 0;
        var parsed = CliArgs.Parse(command.Args);
        if (parsed.UnknownFlags.Count > 0)
        {
            var option = parsed.UnknownFlags.Keys.First();
            error.WriteLine(Chalk.Red(
                $"Unknown option --{option} for \"{AuthCommands.GetAuthCommandName(command.Kind)}\"."));
            error.WriteLine(Chalk.Dim(
                $"Use \"{Config.AppName} --help\" or \"{AuthCommands.GetAuthCommandUsage(command.Kind)}\"."));
            return AuthCommandRunResult.Done(1);
        }

        try
        {
            if (parsed.Diagnostics.Count > 0)
            {
                throw new AuthCommandError(string.Join("\n", parsed.Diagnostics.Select(d => d.Message)));
            }

            if (command.Kind != AuthCommandKind.Check)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var modelRuntime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
                {
                    AllowModelNetwork = false,
                    Signal = cts.Token,
                }).ConfigureAwait(false);
                var credential = await CredentialPrint.ResolveCredentialForPrintAsync(
                    parsed,
                    modelRuntime,
                    command.Kind,
                    command.MinExpiryMs,
                    cts.Token).ConfigureAwait(false);
                output.Write($"{credential}\n");
                return AuthCommandRunResult.Done();
            }

            var requestedAuth = AuthCommands.ValidateAuthCommandArgs(parsed, command.Kind);
            AuthCheckResult result;
            string? credentialValue = null;
            try
            {
                ICredentialStore credentials = command.NoRefresh
                    ? new ReadOnlyAuthStorage()
                    : AuthStorage.Create();
                var modelRuntime = await ProviderAuthCheck.CreateAuthCheckModelRuntimeAsync(credentials)
                    .ConfigureAwait(false);
                result = await ProviderAuthCheck.CheckProviderAuthAsync(
                    parsed,
                    modelRuntime,
                    new AuthCheckOptions { Refresh = !command.NoRefresh }).ConfigureAwait(false);
                if (command.Credentials && result.Status == AuthCheckStatus.Ready)
                {
                    credentialValue = await ProviderAuthCheck.GetProviderCredentialAsync(
                        result.Provider,
                        modelRuntime,
                        credentials,
                        new AuthCheckOptions { Refresh = !command.NoRefresh }).ConfigureAwait(false);
                    if (credentialValue is null)
                    {
                        result = result with
                        {
                            Status = AuthCheckStatus.NotReady,
                            Reason = AuthCheckReason.CredentialNotAvailable,
                        };
                    }
                }
            }
            catch
            {
                result = new AuthCheckResult
                {
                    Status = AuthCheckStatus.Invalid,
                    Provider = requestedAuth.Provider ?? requestedAuth.Model!,
                    Reason = AuthCheckReason.InvalidState,
                };
            }

            string serialized;
            if (command.Json)
            {
                // TS spreads `{ ...result }`, so the key order is status → provider → reason →
                // authType, with `credentials` appended last.
                var node = new JsonObject
                {
                    ["status"] = AuthCheckStatusText(result.Status),
                    ["provider"] = result.Provider,
                };
                if (result.Reason is { } reason)
                {
                    node["reason"] = reason switch
                    {
                        AuthCheckReason.ProviderNotFound => "provider_not_found",
                        AuthCheckReason.CredentialsNotConfigured => "credentials_not_configured",
                        AuthCheckReason.CredentialNotAvailable => "credential_not_available",
                        _ => "invalid_state",
                    };
                }

                if (result.AuthType is { } authType)
                {
                    node["authType"] = authType == CredentialKind.OAuth ? "oauth" : "api_key";
                }

                if (credentialValue is not null)
                {
                    node["credentials"] = credentialValue;
                }

                serialized = node.ToJsonString(new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            }
            else
            {
                serialized = credentialValue ?? AuthCheckStatusText(result.Status);
            }

            output.Write($"{serialized}\n");
            authExitCode = result.Status switch
            {
                AuthCheckStatus.Ready => 0,
                AuthCheckStatus.NotReady => 1,
                _ => 2,
            };
        }
        catch (Exception ex)
        {
            var message = ex is AuthCommandError ? ex.Message : "Failed to resolve credential";
            error.WriteLine(Chalk.Red($"Error: {message}"));
            return AuthCommandRunResult.Done(command.Kind == AuthCommandKind.Check ? 2 : 1);
        }

        return AuthCommandRunResult.Done(authExitCode);
    }

    /// <summary>Port of <c>prepareInitialMessage</c>.</summary>
    internal static async Task<(string? InitialMessage, IReadOnlyList<ImageContent>? InitialImages)>
        PrepareInitialMessageAsync(Args parsed, string? stdinContent = null)
    {
        if (parsed.FileArgs.Count == 0)
        {
            var built = InitialMessages.Build(new InitialMessageInput { Parsed = parsed, StdinContent = stdinContent });
            return (built.InitialMessage, built.InitialImages);
        }

        // AgentSession resizes these after extension hooks select the request model.
        var processed = await FileProcessor.ProcessFileArgumentsAsync(
            parsed.FileArgs,
            new ProcessFileOptions { AutoResizeImages = false }).ConfigureAwait(false);
        var result = InitialMessages.Build(new InitialMessageInput
        {
            Parsed = parsed,
            FileText = processed.Text,
            FileImages = processed.Images,
            StdinContent = stdinContent,
        });
        return (result.InitialMessage, result.InitialImages);
    }

    /// <summary>Port of <c>findLocalSessionByExactId</c>.</summary>
    internal static ResolvedSession.Local? FindLocalSessionByExactId(
        string sessionId,
        string cwd,
        string? sessionDir = null)
    {
        var path = SessionManager.FindById(cwd, sessionId, sessionDir);
        return path is null ? null : new ResolvedSession.Local(path);
    }

    /// <summary>Port of <c>resolveSessionPath</c>.</summary>
    internal static async Task<ResolvedSession> ResolveSessionPathAsync(
        string sessionArg,
        string cwd,
        string? sessionDir = null,
        CancellationToken cancellationToken = default)
    {
        // If it looks like a file path, resolve it before handing it to the session manager.
        if (sessionArg.Contains('/') || sessionArg.Contains('\\') || sessionArg.EndsWith(".jsonl", StringComparison.Ordinal))
        {
            return new ResolvedSession.PathKind(Paths.ResolvePath(sessionArg, cwd));
        }

        // Exact IDs only require reading session headers. Fall back to the full metadata listing for
        // prefix matches.
        var exactLocalMatch = FindLocalSessionByExactId(sessionArg, cwd, sessionDir);
        if (exactLocalMatch is not null)
        {
            return exactLocalMatch;
        }

        var localSessions = await SessionManager.ListAsync(cwd, sessionDir, null, cancellationToken)
            .ConfigureAwait(false);
        var localMatch = localSessions.FirstOrDefault(candidate => candidate.Id.StartsWith(sessionArg, StringComparison.Ordinal));
        if (localMatch is not null)
        {
            return new ResolvedSession.Local(localMatch.Path);
        }

        // Try global search across all projects.
        var allSessions = await SessionManager.ListAllAsync(sessionDir, null, cancellationToken)
            .ConfigureAwait(false);
        var globalMatch =
            allSessions.FirstOrDefault(candidate => candidate.Id == sessionArg)
            ?? allSessions.FirstOrDefault(candidate => candidate.Id.StartsWith(sessionArg, StringComparison.Ordinal));
        if (globalMatch is not null)
        {
            return new ResolvedSession.Global(globalMatch.Path, globalMatch.Cwd);
        }

        return new ResolvedSession.NotFound(sessionArg);
    }

    /// <summary>Port of <c>promptConfirm</c>.</summary>
    internal static async Task<bool> PromptConfirmAsync(string message)
    {
        Console.Out.Write($"{message} [y/N] ");
        var answer = await Task.Run(Console.ReadLine).ConfigureAwait(false);
        var lowered = answer?.ToLowerInvariant();
        return lowered == "y" || lowered == "yes";
    }

    /// <summary>Port of <c>validateForkFlags</c>.</summary>
    internal static void ValidateForkFlags(Args parsed, TextWriter error, Action<int> exit)
    {
        if (parsed.Fork is null)
        {
            return;
        }

        var conflictingFlags = new List<string>();
        if (parsed.Session is not null) conflictingFlags.Add("--session");
        if (parsed.Continue == true) conflictingFlags.Add("--continue");
        if (parsed.Resume == true) conflictingFlags.Add("--resume");
        if (parsed.NoSession == true) conflictingFlags.Add("--no-session");

        if (conflictingFlags.Count > 0)
        {
            error.WriteLine(Chalk.Red($"Error: --fork cannot be combined with {string.Join(", ", conflictingFlags)}"));
            exit(1);
        }
    }

    /// <summary>Port of <c>validateSessionIdFlags</c>.</summary>
    internal static void ValidateSessionIdFlags(Args parsed, TextWriter error, Action<int> exit)
    {
        if (parsed.SessionId is null)
        {
            return;
        }

        var conflictingFlags = new List<string>();
        if (parsed.Session is not null) conflictingFlags.Add("--session");
        if (parsed.Continue == true) conflictingFlags.Add("--continue");
        if (parsed.Resume == true) conflictingFlags.Add("--resume");

        if (conflictingFlags.Count > 0)
        {
            error.WriteLine(Chalk.Red(
                $"Error: --session-id cannot be combined with {string.Join(", ", conflictingFlags)}"));
            exit(1);
        }

        try
        {
            SessionEntries.AssertValidSessionId(parsed.SessionId);
        }
        catch (Exception ex)
        {
            error.WriteLine(Chalk.Red($"Error: {ex.Message}"));
            exit(1);
        }
    }

    /// <summary>Port of <c>openSessionOrExit</c>.</summary>
    internal static SessionManager OpenSessionOrExit(string path, TextWriter error, Action<int> exit, string? sessionDir = null)
    {
        try
        {
            return SessionManager.Open(path, sessionDir);
        }
        catch (Exception ex)
        {
            error.WriteLine(Chalk.Red($"Error: {ex.Message}"));
            exit(1);
            throw;
        }
    }

    /// <summary>Port of <c>forkSessionOrExit</c>.</summary>
    internal static SessionManager ForkSessionOrExit(
        string sourcePath,
        string cwd,
        TextWriter error,
        Action<int> exit,
        string? sessionDir = null,
        string? sessionId = null)
    {
        try
        {
            return SessionManager.ForkFrom(
                sourcePath,
                cwd,
                sessionDir,
                sessionId is null ? null : new Core.NewSessionOptions { Id = sessionId });
        }
        catch (Exception ex)
        {
            error.WriteLine(Chalk.Red($"Error: {ex.Message}"));
            exit(1);
            throw;
        }
    }

    /// <summary>Port of <c>createSessionManager</c>.</summary>
    internal static async Task<SessionManager> CreateSessionManagerAsync(
        Args parsed,
        string cwd,
        string? sessionDir,
        SettingsManager settingsManager,
        TextWriter output,
        TextWriter error,
        Action<int> exit,
        Func<string, Task<bool>>? promptConfirm = null,
        CancellationToken cancellationToken = default)
    {
        if (parsed.NoSession == true || parsed.Help == true || parsed.ListModels is not null)
        {
            return SessionManager.InMemory(
                cwd,
                parsed.SessionId is null ? null : new Core.NewSessionOptions { Id = parsed.SessionId });
        }

        if (parsed.Fork is not null)
        {
            if (parsed.SessionId is not null)
            {
                var existingTarget = FindLocalSessionByExactId(parsed.SessionId, cwd, sessionDir);
                if (existingTarget is not null)
                {
                    error.WriteLine(Chalk.Red($"Session already exists with id '{parsed.SessionId}'"));
                    exit(1);
                    throw new MainExitException(1);
                }
            }

            var resolved = await ResolveSessionPathAsync(parsed.Fork, cwd, sessionDir, cancellationToken)
                .ConfigureAwait(false);
            switch (resolved)
            {
                case ResolvedSession.PathKind path:
                    return ForkSessionOrExit(path.Path, cwd, error, exit, sessionDir, parsed.SessionId);
                case ResolvedSession.Local local:
                    return ForkSessionOrExit(local.Path, cwd, error, exit, sessionDir, parsed.SessionId);
                case ResolvedSession.Global global:
                    return ForkSessionOrExit(global.Path, cwd, error, exit, sessionDir, parsed.SessionId);
                default:
                    error.WriteLine(Chalk.Red($"No session found matching '{(resolved as ResolvedSession.NotFound)!.Arg}'"));
                    exit(1);
                    throw new MainExitException(1);
            }
        }

        if (parsed.Session is not null)
        {
            var resolved = await ResolveSessionPathAsync(parsed.Session, cwd, sessionDir, cancellationToken)
                .ConfigureAwait(false);
            switch (resolved)
            {
                case ResolvedSession.PathKind path:
                    return OpenSessionOrExit(path.Path, error, exit, sessionDir);
                case ResolvedSession.Local local:
                    return OpenSessionOrExit(local.Path, error, exit, sessionDir);
                case ResolvedSession.Global global:
                {
                    output.WriteLine(Chalk.Yellow($"Session found in different project: {global.Cwd}"));
                    var shouldFork = await (promptConfirm ?? PromptConfirmAsync)(
                        "Fork this session into current directory?").ConfigureAwait(false);
                    if (!shouldFork)
                    {
                        output.WriteLine(Chalk.Dim("Aborted."));
                        exit(0);
                        throw new MainExitException(0);
                    }

                    return ForkSessionOrExit(global.Path, cwd, error, exit, sessionDir);
                }

                default:
                    error.WriteLine(Chalk.Red($"No session found matching '{(resolved as ResolvedSession.NotFound)!.Arg}'"));
                    exit(1);
                    throw new MainExitException(1);
            }
        }

        if (parsed.Resume == true)
        {
            try
            {
                var selectedPath = await SessionPicker.SelectSessionAsync(
                    (onProgress, signal) => SessionManager.ListAsync(cwd, sessionDir, onProgress, signal),
                    (onProgress, signal) => SessionManager.ListAllAsync(sessionDir, onProgress, signal),
                    settingsManager).ConfigureAwait(false);
                if (selectedPath is null)
                {
                    output.WriteLine(Chalk.Dim("No session selected"));
                    exit(0);
                    throw new MainExitException(0);
                }

                return SessionManager.Open(selectedPath, sessionDir);
            }
            finally
            {
                ThemeRuntime.StopThemeWatcher();
            }
        }

        if (parsed.Continue == true)
        {
            return SessionManager.ContinueRecent(cwd, sessionDir);
        }

        if (parsed.SessionId is not null)
        {
            var existingSession = FindLocalSessionByExactId(parsed.SessionId, cwd, sessionDir);
            if (existingSession is not null)
            {
                return SessionManager.Open(existingSession.Path, sessionDir);
            }

            output.WriteLine(Chalk.Yellow(
                $"Warning: No project session found with id '{parsed.SessionId}'; creating a new session with that id."));
        }

        return SessionManager.Create(cwd, sessionDir, new Core.NewSessionOptions { Id = parsed.SessionId });
    }

    /// <summary>Port of <c>buildSessionOptions</c>.</summary>
    internal static (SessionOptionsBuildResult Result, bool CliThinkingFromModel,
        List<AgentSessionRuntimeDiagnostic> Diagnostics) BuildSessionOptions(
        Args parsed,
        IReadOnlyList<ScopedModel> scopedModels,
        bool hasExistingSession,
        ModelRuntime modelRuntime,
        SettingsManager settingsManager)
    {
        var options = new SessionOptionsBuildResult();
        var diagnostics = new List<AgentSessionRuntimeDiagnostic>();
        var cliThinkingFromModel = false;

        // Model from CLI — supports --provider <name> --model <pattern> and --model <provider>/<pattern>.
        if (parsed.Provider is not null && parsed.Model is null)
        {
            diagnostics.Add(new AgentSessionRuntimeDiagnostic
            {
                Type = AgentSessionDiagnosticType.Error,
                Message = $"--provider requires --model (for example: --provider {parsed.Provider} --model <pattern>)",
            });
        }

        if (parsed.Model is not null)
        {
            var resolved = ModelResolver.ResolveCliModel(new ResolveCliModelOptions
            {
                CliProvider = parsed.Provider,
                CliModel = parsed.Model,
                CliThinking = parsed.Thinking,
                // `--api-key` goes to whichever provider ends up selected, so stored credentials must not
                // decide it (upstream 1.4.0 #10160).
                InlineApiKey = parsed.ApiKey is not null,
                Runtime = modelRuntime,
            });
            if (resolved.Warning is not null)
            {
                diagnostics.Add(new AgentSessionRuntimeDiagnostic { Type = AgentSessionDiagnosticType.Warning, Message = resolved.Warning });
            }

            if (resolved.Error is not null)
            {
                diagnostics.Add(new AgentSessionRuntimeDiagnostic { Type = AgentSessionDiagnosticType.Error, Message = resolved.Error });
            }

            if (resolved.Model is not null)
            {
                options = options with { Model = resolved.Model };
                // Allow "--model <pattern>:<thinking>" as a shorthand. Explicit --thinking still takes
                // precedence (applied later).
                if (parsed.Thinking is null && resolved.ThinkingLevel is not null)
                {
                    options = options with { ThinkingLevel = resolved.ThinkingLevel };
                    cliThinkingFromModel = true;
                }
            }
        }

        if (options.Model is null && scopedModels.Count > 0 && !hasExistingSession)
        {
            // Check if the saved default is in scoped models — use it if so, otherwise the first.
            var savedProvider = settingsManager.GetDefaultProvider();
            var savedModelId = settingsManager.GetDefaultModel();
            var savedModel = savedProvider is not null && savedModelId is not null
                ? modelRuntime.GetModel(savedProvider, savedModelId)
                : null;
            var savedInScope = savedModel is not null
                ? scopedModels.FirstOrDefault(scoped => ModelsAreEqual(scoped.Model, savedModel))
                : null;

            if (savedInScope is not null)
            {
                options = options with { Model = savedInScope.Model };
                if (parsed.Thinking is null && savedInScope.ThinkingLevel is not null)
                {
                    options = options with { ThinkingLevel = savedInScope.ThinkingLevel };
                }
            }
            else
            {
                options = options with { Model = scopedModels[0].Model };
                if (parsed.Thinking is null && scopedModels[0].ThinkingLevel is not null)
                {
                    options = options with { ThinkingLevel = scopedModels[0].ThinkingLevel };
                }
            }
        }

        // Thinking level from CLI (takes precedence over scoped-model thinking levels set above).
        if (parsed.Thinking is not null)
        {
            options = options with { ThinkingLevel = parsed.Thinking };
        }

        // Scoped models for Ctrl+P cycling. Keep the thinking level null when not explicitly set in
        // the model pattern — null means "inherit the current session thinking level" while cycling.
        if (scopedModels.Count > 0)
        {
            options = options with
            {
                ScopedModels = scopedModels
                    .Select(scoped => new ScopedModel { Model = scoped.Model, ThinkingLevel = scoped.ThinkingLevel })
                    .ToList(),
            };
        }

        // Tools.
        if (parsed.NoTools == true)
        {
            options = options with { NoTools = NoToolsMode.All };
        }
        else if (parsed.NoBuiltinTools == true)
        {
            options = options with { NoTools = NoToolsMode.Builtin };
        }

        if (parsed.Tools is not null)
        {
            options = options with { Tools = [.. parsed.Tools] };
        }

        if (parsed.ExcludeTools is not null)
        {
            options = options with { ExcludeTools = [.. parsed.ExcludeTools] };
        }

        return (options, cliThinkingFromModel, diagnostics);
    }

    /// <summary>Port of <c>resolveCliPaths</c>.</summary>
    internal static IReadOnlyList<string>? ResolveCliPaths(string cwd, IReadOnlyList<string>? paths) =>
        paths?.Select(value => Paths.IsLocalPath(value) ? Paths.ResolvePath(value, cwd) : value).ToList();

    /// <summary>Port of <c>promptForMissingSessionCwd</c>.</summary>
    internal static Task<string?> PromptForMissingSessionCwdAsync(
        SessionCwdIssue issue,
        SettingsManager settingsManager) =>
        StartupUi.ShowStartupSelectorAsync(
            settingsManager,
            SessionCwd.FormatMissingSessionCwdPrompt(issue),
            [
                new StartupSelectorOption<string?>("Continue", issue.FallbackCwd),
                new StartupSelectorOption<string?>("Cancel", null),
            ]);

    private static bool ModelsAreEqual(ModelSpec left, ModelSpec right) =>
        left.Provider == right.Provider && left.Id == right.Id;

    private static IReadOnlyList<KeyValuePair<string, ExtensionFlagInput>> ToExtensionFlagValues(
        IReadOnlyDictionary<string, object> unknownFlags)
    {
        var values = new List<KeyValuePair<string, ExtensionFlagInput>>(unknownFlags.Count);
        foreach (var (name, value) in unknownFlags)
        {
            values.Add(new KeyValuePair<string, ExtensionFlagInput>(
                name,
                value is string text ? ExtensionFlagInput.Text(text) : ExtensionFlagInput.Boolean));
        }

        return values;
    }

    /// <summary>Text form of <see cref="AuthCheckStatus"/> for the non-JSON output path.</summary>
    internal static string AuthCheckStatusText(AuthCheckStatus status) => status switch
    {
        AuthCheckStatus.Ready => "ready",
        AuthCheckStatus.NotReady => "not_ready",
        _ => "invalid",
    };
}

/// <summary>
/// Mutable-by-<c>with</c> mirror of the TS <c>CreateAgentSessionOptions</c> subset <c>main.ts</c> fills.
/// </summary>
public sealed record SessionOptionsBuildResult
{
    public ModelSpec? Model { get; init; }

    public ThinkingLevel? ThinkingLevel { get; init; }

    public IReadOnlyList<ScopedModel>? ScopedModels { get; init; }

    public IReadOnlyList<string>? Tools { get; init; }

    public IReadOnlyList<string>? ExcludeTools { get; init; }

    public NoToolsMode? NoTools { get; init; }

    public IReadOnlyList<Core.Extensions.ToolDefinition>? CustomTools { get; init; }
}

/// <summary>Port of the TS <c>{ model, thinkingLevel }</c> scoped-model entries.</summary>
public sealed record ScopedModelConfig(ModelSpec Model, ThinkingLevel? ThinkingLevel);
