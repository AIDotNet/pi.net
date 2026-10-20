// ============================================================================
// InteractiveMode — extension binding flow (batch 4f-4m-5a, landed early).
//
// Port of interactive-mode.ts's bindCurrentSessionExtensions (L1963-2038) and the
// members it assembles: showLoadedResources (L1764-1958), showStartupNoticesIfNeeded
// (L836-865), setupExtensionShortcuts (L2232-2283) and showExtensionError (L3028-3045).
//
// This batch was pulled ahead of the remaining 4f-4m slices because
// RebindCurrentSessionAsync (the runtime's rebind hook, which every session
// replacement runs) calls BindCurrentSessionExtensionsAsync: while the seam throws,
// fork / clone / new / resume / import all fail after Apply() but before the caller
// sees a completed result.
// ============================================================================

using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>TS <c>showLoadedResources</c>' inline options type.</summary>
public sealed record LoadedResourcesOptions
{
    /// <summary>Explicit extension list; defaults to the loader's non-hidden extensions.</summary>
    public IReadOnlyList<ResourcePathEntry>? Extensions { get; init; }

    /// <summary>Render the listing even when the startup details are silenced.</summary>
    public bool? Force { get; init; }

    /// <summary>Render the diagnostics even when the listing is silenced.</summary>
    public bool? ShowDiagnosticsWhenQuiet { get; init; }
}

public partial class InteractiveMode
{
    /// <summary>TS <c>bindCurrentSessionExtensions</c> (L1963-2038).</summary>
    private async Task BindCurrentSessionExtensionsAsync()
    {
        var uiContext = CreateExtensionUIContext();
        await Session.BindExtensionsAsync(new ExtensionBindings
        {
            UiContext = uiContext,
            Mode = ExtensionMode.Tui,
            AbortHandler = () => RestoreQueuedMessagesToEditor(abort: true),
            CommandContextActions = new ExtensionCommandContextActions
            {
                WaitForIdle = () => Session.WaitForIdleAsync(),
                NewSession = async options =>
                {
                    ClearStatusIndicator();
                    try
                    {
                        return !(await _runtimeHost.NewSessionAsync(options).ConfigureAwait(true)).Cancelled;
                    }
                    catch (Exception error)
                    {
                        // TS handleFatalRuntimeError returns Promise<never> (it exits the process);
                        // the C# port keeps the same shape, so the unreachable return keeps the
                        // delegate's Task<bool> contract.
                        await HandleFatalRuntimeErrorAsync("Failed to create session", error).ConfigureAwait(true);
                        return false;
                    }
                },
                Fork = async (entryId, options) =>
                {
                    try
                    {
                        var result = await _runtimeHost.ForkAsync(entryId, options).ConfigureAwait(true);
                        if (!result.Cancelled)
                        {
                            _editor.SetText(result.SelectedText ?? "");
                            ShowStatus("Forked to new session");
                        }

                        return !result.Cancelled;
                    }
                    catch (Exception error)
                    {
                        await HandleFatalRuntimeErrorAsync("Failed to fork session", error).ConfigureAwait(true);
                        return false;
                    }
                },
                NavigateTree = async (targetId, options) =>
                {
                    var result = await Session.NavigateTreeAsync(targetId, new AgentSession.NavigateTreeSessionOptions
                    {
                        Summarize = options?.Summarize,
                        CustomInstructions = options?.CustomInstructions,
                        ReplaceInstructions = options?.ReplaceInstructions,
                        Label = options?.Label,
                    }).ConfigureAwait(true);
                    if (result.Cancelled)
                    {
                        return false;
                    }

                    _chatContainer.Clear();
                    RenderInitialMessages();
                    if (result.EditorText is not null && string.IsNullOrWhiteSpace(_editor.GetText()))
                    {
                        _editor.SetText(result.EditorText);
                    }

                    ShowStatus("Navigated to selected point");
                    _ = FlushCompactionQueueAsync(willRetry: false);
                    return true;
                },
                SwitchSession = (sessionPath, options) => HandleResumeSessionAsync(sessionPath, options),
                Reload = () => HandleReloadCommandAsync(),
            },
            ShutdownHandler = () =>
            {
                _shutdownRequested = true;
                if (Session.IsIdle)
                {
                    _ = ShutdownAsync();
                }
            },
            OnError = error => ShowExtensionError(error.ExtensionPath, error.Error, error.Stack),
        }).ConfigureAwait(true);

        ThemeRuntime.SetRegisteredThemes([.. Session.ResourceLoader.Themes.Select(LoadRegisteredTheme)]);
        SetupAutocompleteProvider();

        var extensionRunner = Session.ExtensionRunner;
        SetupExtensionShortcuts(extensionRunner);
        ShowLoadedResources(new LoadedResourcesOptions { Force = false, ShowDiagnosticsWhenQuiet = true });
        ShowStartupNoticesIfNeeded();
    }

    /// <summary>TS <c>showLoadedResources</c> (L1764-1958).</summary>
    internal void ShowLoadedResources(LoadedResourcesOptions? options = null)
    {
        // Resource rendering is idempotent; chat clears no longer clear this separate container.
        _loadedResourcesContainer.Clear();

        var showListing = options?.Force == true || ShouldShowStartupDetails();
        var showDiagnostics = showListing || options?.ShowDiagnosticsWhenQuiet == true;
        if (!showListing && !showDiagnostics)
        {
            return;
        }

        // Bodies are built on demand so the listing follows theme changes.
        void AddLoadedSection(string name, Func<string> collapsedBody, Func<string>? expandedBody = null, string color = ThemeColors.MdHeading)
        {
            var section = new ExpandableText(
                () => $"{InteractiveModeStartup.FormatSectionHeader(name, ThemeRuntime.Current, color)}\n{collapsedBody()}",
                () => $"{InteractiveModeStartup.FormatSectionHeader(name, ThemeRuntime.Current, color)}\n{(expandedBody ?? collapsedBody)()}",
                GetStartupExpansionState(),
                0,
                0);
            _loadedResourcesContainer.AddChild(section);
            _loadedResourcesContainer.AddChild(new Spacer(1));
        }

        var skillsResult = Session.ResourceLoader.Skills;
        var prompts = Session.ResourceLoader.Prompts;
        var themesResult = Session.ResourceLoader.Themes;
        var extensions = options?.Extensions
            ?? Session.ResourceLoader.GetExtensions().Extensions
                .Where(extension => !extension.Hidden)
                .Select(extension => new ResourcePathEntry { Path = extension.Path, SourceInfo = extension.SourceInfo })
                .ToList();
        var sourceInfos = new Dictionary<string, SourceInfo>(StringComparer.Ordinal);
        foreach (var extension in extensions)
        {
            if (extension.SourceInfo is not null)
            {
                sourceInfos[extension.Path] = extension.SourceInfo;
            }
        }

        foreach (var skill in skillsResult)
        {
            sourceInfos[skill.FilePath] = skill.SourceInfo;
        }

        foreach (var prompt in prompts)
        {
            sourceInfos[prompt.FilePath] = prompt.SourceInfo;
        }

        foreach (var loadedTheme in themesResult)
        {
            // TS checks `loadedTheme.sourcePath && loadedTheme.sourceInfo`; the port's LoadedTheme.Path
            // carries the source path (required, possibly empty for synthetic themes).
            if (!string.IsNullOrEmpty(loadedTheme.Path) && loadedTheme.SourceInfo is not null)
            {
                sourceInfos[loadedTheme.Path] = loadedTheme.SourceInfo;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cwd = SessionManager.GetCwd();

        if (showListing)
        {
            var contextFiles = new List<string>();
            if (Session.ResourceLoader.SystemPromptSourcePath is { } systemPromptSource)
            {
                contextFiles.Add(systemPromptSource);
            }

            contextFiles.AddRange(Session.ResourceLoader.AppendSystemPromptSourcePaths);
            contextFiles.AddRange(Session.ResourceLoader.AgentsFiles.Select(file => file.Path));
            if (contextFiles.Count > 0)
            {
                _loadedResourcesContainer.AddChild(new Spacer(1));
                string ContextList() => string.Join("\n", contextFiles.Select(path =>
                    ThemeRuntime.Current.Fg("dim", $"  {InteractiveModeFormatting.FormatDisplayPath(path, home)}")));
                string ContextCompactList() => InteractiveModeStartup.FormatCompactList(
                    contextFiles.Select(path => InteractiveModeFormatting.FormatContextPath(path, cwd, home)).ToList(),
                    ThemeRuntime.Current,
                    sort: false);
                AddLoadedSection("Context", ContextCompactList, ContextList);
            }

            if (skillsResult.Count > 0)
            {
                var groups = InteractiveModeFormatting.BuildScopeGroups(
                    skillsResult.Select(skill => new ResourcePathEntry { Path = skill.FilePath, SourceInfo = skill.SourceInfo }).ToList());
                string SkillList() => InteractiveModeFormatting.FormatScopeGroups(groups, new FormatScopeGroupsOptions
                {
                    FormatPath = item => InteractiveModeFormatting.FormatDisplayPath(item.Path, home),
                    FormatPackagePath = (item, source) => InteractiveModeFormatting.GetShortPath(item.Path, item.SourceInfo, home),
                });
                string SkillCompactList() => InteractiveModeStartup.FormatCompactList(
                    skillsResult.Select(skill => skill.Name).ToList(), ThemeRuntime.Current);
                AddLoadedSection("Skills", SkillCompactList, SkillList);
            }

            if (prompts.Count > 0)
            {
                var groups = InteractiveModeFormatting.BuildScopeGroups(
                    prompts.Select(template => new ResourcePathEntry { Path = template.FilePath, SourceInfo = template.SourceInfo }).ToList());
                var templateByPath = prompts.ToDictionary(template => template.FilePath, StringComparer.Ordinal);
                string TemplateList() => InteractiveModeFormatting.FormatScopeGroups(groups, new FormatScopeGroupsOptions
                {
                    FormatPath = item => templateByPath.TryGetValue(item.Path, out var template)
                        ? $"/{template.Name}"
                        : InteractiveModeFormatting.FormatDisplayPath(item.Path, home),
                    FormatPackagePath = (item, source) => templateByPath.TryGetValue(item.Path, out var template)
                        ? $"/{template.Name}"
                        : InteractiveModeFormatting.FormatDisplayPath(item.Path, home),
                });
                string PromptCompactList() => InteractiveModeStartup.FormatCompactList(
                    prompts.Select(template => $"/{template.Name}").ToList(), ThemeRuntime.Current);
                AddLoadedSection("Prompts", PromptCompactList, TemplateList);
            }

            if (extensions.Count > 0)
            {
                var groups = InteractiveModeFormatting.BuildScopeGroups(extensions);
                string ExtList() => InteractiveModeFormatting.FormatScopeGroups(groups, new FormatScopeGroupsOptions
                {
                    FormatPath = item => InteractiveModeFormatting.FormatExtensionDisplayPath(item.Path, home),
                    FormatPackagePath = (item, source) => InteractiveModeFormatting.FormatExtensionDisplayPath(
                        InteractiveModeFormatting.GetShortPath(item.Path, item.SourceInfo, home), home),
                });
                var extensionLabels = InteractiveModeFormatting.GetCompactExtensionLabels(extensions, home);
                string ExtensionCompactList() => InteractiveModeStartup.FormatCompactList(extensionLabels, ThemeRuntime.Current);
                AddLoadedSection("Extensions", ExtensionCompactList, ExtList);
            }
        }

        if (showDiagnostics)
        {
            var skillDiagnostics = Session.ResourceLoader.SkillDiagnostics;
            if (skillDiagnostics.Count > 0)
            {
                _loadedResourcesContainer.AddChild(new ThemedText(
                    () => $"{ThemeRuntime.Current.Fg("warning", "[Skill conflicts]")}\n{InteractiveModeFormatting.FormatDiagnostics(skillDiagnostics, sourceInfos, home)}",
                    0,
                    0));
                _loadedResourcesContainer.AddChild(new Spacer(1));
            }

            var promptDiagnostics = Session.ResourceLoader.PromptDiagnostics;
            if (promptDiagnostics.Count > 0)
            {
                _loadedResourcesContainer.AddChild(new ThemedText(
                    () => $"{ThemeRuntime.Current.Fg("warning", "[Prompt conflicts]")}\n{InteractiveModeFormatting.FormatDiagnostics(promptDiagnostics, sourceInfos, home)}",
                    0,
                    0));
                _loadedResourcesContainer.AddChild(new Spacer(1));
            }

            var extensionsResult = Session.ResourceLoader.GetExtensions();
            var extensionDiagnostics = InteractiveModeStartup.CollectExtensionDiagnostics(
                extensionsResult.Errors, extensionsResult.Warnings);
            extensionDiagnostics = [.. extensionDiagnostics, .. Session.ExtensionRunner.GetCommandDiagnostics()];
            var registeredCommands = Session.ExtensionRunner.GetRegisteredCommands();
            var builtinNames = new HashSet<string>(
                SlashCommands.BuiltinSlashCommands.Select(command => command.Name), StringComparer.Ordinal);
            extensionDiagnostics =
                [.. extensionDiagnostics, .. InteractiveModeAutocomplete.GetBuiltInCommandConflictDiagnostics(registeredCommands, builtinNames)];
            extensionDiagnostics = [.. extensionDiagnostics, .. Session.ExtensionRunner.GetShortcutDiagnostics()];

            if (extensionDiagnostics.Count > 0)
            {
                _loadedResourcesContainer.AddChild(new ThemedText(
                    () => $"{ThemeRuntime.Current.Fg("warning", "[Extension issues]")}\n{InteractiveModeFormatting.FormatDiagnostics(extensionDiagnostics, sourceInfos, home)}",
                    0,
                    0));
                _loadedResourcesContainer.AddChild(new Spacer(1));
            }

            var themeDiagnostics = Session.ResourceLoader.ThemeDiagnostics;
            if (themeDiagnostics.Count > 0)
            {
                _loadedResourcesContainer.AddChild(new ThemedText(
                    () => $"{ThemeRuntime.Current.Fg("warning", "[Theme conflicts]")}\n{InteractiveModeFormatting.FormatDiagnostics(themeDiagnostics, sourceInfos, home)}",
                    0,
                    0));
                _loadedResourcesContainer.AddChild(new Spacer(1));
            }
        }
    }

    /// <summary>TS <c>showStartupNoticesIfNeeded</c> (L836-865).</summary>
    /// <remarks>
    /// TS keeps this member private; widened to internal for the test suite
    /// (InternalsVisibleTo), following the read-only observation seams the earlier
    /// 4f batches added to their components.
    /// </remarks>
    internal void ShowStartupNoticesIfNeeded()
    {
        if (_startupNoticesShown)
        {
            return;
        }

        _startupNoticesShown = true;

        if (string.IsNullOrEmpty(_changelogMarkdown))
        {
            return;
        }

        if (_chatContainer.GetChildrenSnapshot().Count > 0)
        {
            _chatContainer.AddChild(new Spacer(1));
        }

        _chatContainer.AddChild(new DynamicBorder());
        if (SettingsManager.GetCollapseChangelog())
        {
            _chatContainer.AddChild(new Text(
                InteractiveModeStartup.BuildCondensedChangelogNotice(_changelogMarkdown, _version, ThemeRuntime.Current),
                1,
                0));
        }
        else
        {
            _chatContainer.AddChild(new ThemedText(
                () => ThemeRuntime.Current.Bold(ThemeRuntime.Current.Fg("accent", "What's New")), 1, 0));
            _chatContainer.AddChild(new Spacer(1));
            _chatContainer.AddChild(new Markdown(_changelogMarkdown.Trim(), 1, 0, GetMarkdownThemeWithSettings()));
            _chatContainer.AddChild(new Spacer(1));
        }

        _chatContainer.AddChild(new DynamicBorder());
    }

    /// <summary>TS <c>setupExtensionShortcuts</c> (L2232-2283).</summary>
    private void SetupExtensionShortcuts(ExtensionRunner extensionRunner)
    {
        var shortcuts = extensionRunner.GetShortcuts(_keybindings.GetEffectiveConfig());
        if (shortcuts.Count == 0)
        {
            return;
        }

        // The context is rebuilt per invocation so every handler sees the live session state.
        IExtensionContext CreateContext() => new ShortcutExtensionContext(this, extensionRunner);

        _defaultEditor.OnExtensionShortcut = data =>
        {
            foreach (var (shortcutStr, shortcut) in shortcuts)
            {
                if (Keys.MatchesKey(data, shortcutStr))
                {
                    // Run handler async, don't block input.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await shortcut.Handler(CreateContext()).ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            ShowError(InteractiveModeExtensions.ShortcutErrorMessage(error.Message));
                        }
                    });
                    return true;
                }
            }

            return false;
        };
    }

    /// <summary>TS <c>showExtensionError</c> (L3028-3045).</summary>
    private void ShowExtensionError(string extensionPath, string error, string? stack = null)
    {
        var errorMessage = $"Extension \"{extensionPath}\" error: {error}";
        _chatContainer.AddChild(new ThemedText(() => ThemeRuntime.Current.Fg("error", errorMessage), 1, 0));
        if (!string.IsNullOrEmpty(stack))
        {
            // Skip the first line (it duplicates the error message).
            var stackLines = stack.Split('\n').Skip(1).ToArray();
            if (stackLines.Length > 0)
            {
                _chatContainer.AddChild(new ThemedText(
                    () => string.Join("\n", stackLines.Select(line => ThemeRuntime.Current.Fg("dim", $"  {line.Trim()}"))),
                    1,
                    0));
            }
        }

        _ui.RequestRender();
    }

    /// <summary>
    /// The extension context handed to shortcut handlers (TS <c>setupExtensionShortcuts</c>'s
    /// <c>createContext</c>). Built from the interactive mode's own state, not the runner's
    /// context: the UI surface is the TUI one and the session accessors read this mode's session.
    /// </summary>
    private sealed class ShortcutExtensionContext(InteractiveMode mode, ExtensionRunner runner) : IExtensionContext
    {
        public IExtensionUIContext Ui => mode.CreateExtensionUIContext();

        public string Mode => ExtensionMode.Tui;

        public bool HasUI => true;

        public string Cwd => mode.SessionManager.GetCwd();

        public IReadonlySessionManager SessionManager => mode.SessionManager;

        public ModelRegistry ModelRegistry => runner.GetModelRegistry();

        public ModelSpec? Model => mode.Session.Model;

        public IReadOnlyList<ScopedModel> ScopedModels => mode.Session.ScopedModels;

        public ThinkingLevel? ThinkingLevel => mode.Session.ThinkingLevel;

        public bool IsIdle() => mode.Session.IsIdle;

        public bool IsProjectTrusted() => mode.SettingsManager.IsProjectTrusted();

        public CancellationToken? Signal => mode.Agent.Signal;

        public void Abort() => mode.RestoreQueuedMessagesToEditor(abort: true);

        public bool HasPendingMessages() => mode.Session.PendingMessageCount > 0;

        public void Shutdown() => mode._shutdownRequested = true;

        public ContextUsage? GetContextUsage() => mode.Session.GetContextUsage();

        public void Compact(CompactOptions? options = null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await mode.Session.CompactAsync(new CompactOptions
                    {
                        CustomInstructions = options?.CustomInstructions,
                    }).ConfigureAwait(false);
                    options?.OnComplete?.Invoke(result);
                }
                catch (Exception error)
                {
                    options?.OnError?.Invoke(error);
                }
            });
        }

        public string GetSystemPrompt() => mode.Session.SystemPrompt;
    }
}
