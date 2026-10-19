// ============================================================================
// PackageManagerCli — port of package-manager-cli.ts (4g-2)
// ============================================================================
//
// The `install` / `remove` / `update` / `list` subcommands plus the `config` TUI entry.
//
// Platform differences (see docs/coding-agent-porting-status.md):
// - `update --self` (npm / pnpm / managed-install self-update) is not ported: the .NET build is
//   not an npm package, so there is nothing to reinstall. The branch keeps the target resolution
//   and reports a precise diagnostic instead of silently doing nothing.
// - `process.exit` / `process.cwd` are injectable (convention C290) so the exit branches are
//   testable.

using System.Text;
using Pi.Ai.Models;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Cli;

/// <summary>Port of the TS <c>PackageCommand</c> union.</summary>
public enum PackageCommandKind
{
    Install,
    Remove,
    Update,
    List,
}

/// <summary>Text form of <see cref="PackageCommandKind"/> (the TS union's string values).</summary>
public static class PackageCommandKinds
{
    public static string ToText(this PackageCommandKind kind) => kind switch
    {
        PackageCommandKind.Install => "install",
        PackageCommandKind.Remove => "remove",
        PackageCommandKind.Update => "update",
        _ => "list",
    };
}

/// <summary>Port of the TS <c>UpdateTarget</c> discriminated union.</summary>
public abstract record UpdateTarget
{
    public sealed record All : UpdateTarget;

    public sealed record Self : UpdateTarget;

    public sealed record Extensions(string? Source) : UpdateTarget;

    public sealed record Models : UpdateTarget;

    /// <summary>TS <c>updateTargetIncludesSelf</c>.</summary>
    public bool IncludesSelf => this is All or Self;

    /// <summary>TS <c>updateTargetIncludesExtensions</c>.</summary>
    public bool IncludesExtensions => this is All or Extensions;
}

/// <summary>Port of the TS <c>PackageCommandOptions</c> record.</summary>
public sealed record PackageCommandOptions
{
    public required PackageCommandKind Command { get; init; }

    public string? Source { get; init; }

    public UpdateTarget? UpdateTarget { get; init; }

    public bool ShowExtensionsSkippedNote { get; init; }

    public bool Local { get; init; }

    public bool Force { get; init; }

    public bool? ProjectTrustOverride { get; init; }

    public bool Help { get; init; }

    public string? InvalidOption { get; init; }

    public string? InvalidArgument { get; init; }

    public string? MissingOptionValue { get; init; }

    public string? ConflictingOptions { get; init; }
}

/// <summary>
/// Outcome of a package/config command: whether the args were handled and the code the caller should
/// exit with. TS writes <c>process.exitCode</c> and calls <c>process.exit</c> inline; the port returns
/// the code so repeated in-process runs cannot leak state through <see cref="Environment.ExitCode"/>
/// (difference C307).
/// </summary>
public sealed record PackageCommandResult(bool Handled, int ExitCode)
{
    public static PackageCommandResult NotHandled { get; } = new(false, 0);

    public static PackageCommandResult Done(int exitCode = 0) => new(true, exitCode);
}

/// <summary>Port of the TS <c>PackageCommandRuntimeOptions</c> record.</summary>
public sealed record PackageCommandRuntimeOptions
{
    public IReadOnlyList<InlineExtension>? ExtensionFactories { get; init; }

    /// <summary>Working directory. Defaults to <see cref="Environment.CurrentDirectory"/>.</summary>
    public string? Cwd { get; init; }

    /// <summary>Agent dir. Defaults to <see cref="Config.GetAgentDir()"/>.</summary>
    public string? AgentDir { get; init; }

    /// <summary>stdout writer. Defaults to <see cref="Console.Out"/>.</summary>
    public TextWriter? Out { get; init; }

    /// <summary>stderr writer. Defaults to <see cref="Console.Error"/>.</summary>
    public TextWriter? Error { get; init; }

    /// <summary>Whether stdin/stdout are TTYs. Defaults to the process probe.</summary>
    public bool? Interactive { get; init; }
}

/// <summary>Port of <c>package-manager-cli.ts</c>.</summary>
public static class PackageManagerCli
{
    private const string DefaultInstallerApiBase = "https://pi.dev/api/installer/releases";

    private const string ManagedInstallMarker = "managed-install.json";

    /// <summary>TS <c>CONFIG_COMMAND_USAGE</c>.</summary>
    public static string ConfigCommandUsage => $"{Config.AppName} config [-l] [--approve|--no-approve]";

    /// <summary>Port of <c>getPackageCommandUsage</c>.</summary>
    public static string GetPackageCommandUsage(PackageCommandKind command) => command switch
    {
        PackageCommandKind.Install => $"{Config.AppName} install <source> [-l] [--approve|--no-approve]",
        PackageCommandKind.Remove => $"{Config.AppName} remove <source> [-l] [--approve|--no-approve]",
        PackageCommandKind.Update =>
            $"{Config.AppName} update [source|self|pi] [--self|--extensions|--models|--all] "
            + "[--extension <source>] [--approve|--no-approve] [--force]",
        _ => $"{Config.AppName} list [--approve|--no-approve]",
    };

    // ========================================================================
    // 帮助文本（TS printConfigCommandHelp / printPackageCommandHelp）
    // ========================================================================

    /// <summary>Port of <c>printConfigCommandHelp</c>.</summary>
    public static void PrintConfigCommandHelp(TextWriter? output = null)
    {
        output ??= Console.Out;
        output.WriteLine($"{Chalk.Bold("Usage:")}\n  {ConfigCommandUsage}\n");
        output.WriteLine("Open the resource configuration TUI to enable or disable package resources.");
        output.WriteLine(
            $"Without -l, starts in global settings (~/{Config.ConfigDirName}/agent/settings.json).");
        output.WriteLine("Press Tab in the TUI to switch between global and project-local modes.\n");
        output.WriteLine("Options:");
        output.WriteLine($"  -l, --local       Edit project overrides ({Config.ConfigDirName}/settings.json)");
        output.WriteLine("  -a, --approve     Trust project-local files for this command with -l");
        output.WriteLine("  -na, --no-approve Ignore project-local files for this command with -l");
        output.WriteLine();
    }

    /// <summary>Port of <c>printPackageCommandHelp</c>.</summary>
    public static void PrintPackageCommandHelp(PackageCommandKind command, TextWriter? output = null)
    {
        output ??= Console.Out;
        var app = Config.AppName;
        var configDir = Config.ConfigDirName;

        switch (command)
        {
            case PackageCommandKind.Install:
                output.WriteLine($"{Chalk.Bold("Usage:")}\n  {GetPackageCommandUsage(command)}\n");
                output.WriteLine("Install a package and add it to settings.\n");
                output.WriteLine("Options:");
                output.WriteLine($"  -l, --local       Install project-locally ({configDir}/settings.json)");
                output.WriteLine("  -a, --approve     Trust project-local files for this command");
                output.WriteLine("  -na, --no-approve Ignore project-local files for this command\n");
                output.WriteLine("Examples:");
                output.WriteLine($"  {app} install npm:@foo/bar");
                output.WriteLine($"  {app} install git:github.com/user/repo");
                output.WriteLine($"  {app} install git:git@github.com:user/repo");
                output.WriteLine($"  {app} install https://github.com/user/repo");
                output.WriteLine($"  {app} install ssh://git@github.com/user/repo");
                output.WriteLine($"  {app} install ./local/path");
                output.WriteLine();
                return;

            case PackageCommandKind.Remove:
                output.WriteLine($"{Chalk.Bold("Usage:")}\n  {GetPackageCommandUsage(command)}\n");
                output.WriteLine("Remove a package and its source from settings.");
                output.WriteLine($"Alias: {app} uninstall <source> [-l]\n");
                output.WriteLine("Options:");
                output.WriteLine($"  -l, --local       Remove from project settings ({configDir}/settings.json)");
                output.WriteLine("  -a, --approve     Trust project-local files for this command");
                output.WriteLine("  -na, --no-approve Ignore project-local files for this command\n");
                output.WriteLine("Examples:");
                output.WriteLine($"  {app} remove npm:@foo/bar");
                output.WriteLine($"  {app} uninstall npm:@foo/bar");
                output.WriteLine();
                return;

            case PackageCommandKind.Update:
                output.WriteLine($"{Chalk.Bold("Usage:")}\n  {GetPackageCommandUsage(command)}\n");
                output.WriteLine("Update pi, installed packages, or model catalogs.\n");
                output.WriteLine("Options:");
                output.WriteLine("  --self                  Update pi only (default when no target is given)");
                output.WriteLine("  --extensions            Update installed packages only");
                output.WriteLine("  --models                Refresh model catalogs only");
                output.WriteLine("  --all                   Update pi and installed packages");
                output.WriteLine("  --extension <source>    Update one package only");
                output.WriteLine("  -a, --approve           Trust project-local files for this command");
                output.WriteLine("  -na, --no-approve       Ignore project-local files for this command");
                output.WriteLine("  --force                 Reinstall pi even if the current version is latest\n");
                output.WriteLine("Short forms:");
                output.WriteLine($"  {app} update                Update pi only");
                output.WriteLine($"  {app} update --all          Update pi and all extensions");
                output.WriteLine($"  {app} update --models       Refresh model catalogs only");
                output.WriteLine($"  {app} update <source>       Update one package");
                output.WriteLine($"  {app} update pi             Update pi only (self works as alias to pi)");
                output.WriteLine();
                return;

            default:
                output.WriteLine($"{Chalk.Bold("Usage:")}\n  {GetPackageCommandUsage(command)}\n");
                output.WriteLine("List installed packages from user and project settings.\n");
                output.WriteLine("Options:");
                output.WriteLine("  -a, --approve      Trust project-local files for this command");
                output.WriteLine("  -na, --no-approve  Ignore project-local files for this command");
                output.WriteLine();
                return;
        }
    }

    // ========================================================================
    // parsePackageCommand
    // ========================================================================

    /// <summary>
    /// Port of <c>parsePackageCommand</c>. Returns null when the first argument is not a package
    /// subcommand (so the caller falls through to the regular CLI).
    /// </summary>
    public static PackageCommandOptions? ParsePackageCommand(IReadOnlyList<string> args)
    {
        var rawCommand = args.Count > 0 ? args[0] : null;
        PackageCommandKind? command = rawCommand switch
        {
            "uninstall" => PackageCommandKind.Remove,
            "install" => PackageCommandKind.Install,
            "remove" => PackageCommandKind.Remove,
            "update" => PackageCommandKind.Update,
            "list" => PackageCommandKind.List,
            _ => null,
        };
        if (command is null)
        {
            return null;
        }

        var kind = command.Value;
        var local = false;
        var force = false;
        bool? projectTrustOverride = null;
        var help = false;
        string? invalidOption = null;
        string? invalidArgument = null;
        string? missingOptionValue = null;
        string? conflictingOptions = null;
        string? source = null;
        var selfFlag = false;
        var extensionsFlag = false;
        var modelsFlag = false;
        var allFlag = false;
        string? extensionFlagSource = null;

        for (var index = 1; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg is "-h" or "--help")
            {
                help = true;
                continue;
            }

            if (arg is "-l" or "--local")
            {
                if (kind is PackageCommandKind.Install or PackageCommandKind.Remove)
                {
                    local = true;
                }
                else
                {
                    invalidOption ??= arg;
                }

                continue;
            }

            if (arg == "--self")
            {
                if (kind == PackageCommandKind.Update)
                {
                    selfFlag = true;
                }
                else
                {
                    invalidOption ??= arg;
                }

                continue;
            }

            if (arg == "--extensions")
            {
                if (kind == PackageCommandKind.Update)
                {
                    extensionsFlag = true;
                }
                else
                {
                    invalidOption ??= arg;
                }

                continue;
            }

            if (arg == "--models")
            {
                if (kind == PackageCommandKind.Update)
                {
                    modelsFlag = true;
                }
                else
                {
                    invalidOption ??= arg;
                }

                continue;
            }

            if (arg == "--all")
            {
                if (kind == PackageCommandKind.Update)
                {
                    allFlag = true;
                }
                else
                {
                    invalidOption ??= arg;
                }

                continue;
            }

            if (arg is "--approve" or "-a")
            {
                projectTrustOverride = true;
                continue;
            }

            if (arg is "--no-approve" or "-na")
            {
                projectTrustOverride = false;
                continue;
            }

            if (arg == "--force")
            {
                if (kind == PackageCommandKind.Update)
                {
                    force = true;
                }
                else
                {
                    invalidOption ??= arg;
                }

                continue;
            }

            if (arg == "--extension")
            {
                if (kind != PackageCommandKind.Update)
                {
                    invalidOption ??= arg;
                    continue;
                }

                var value = index + 1 < args.Count ? args[index + 1] : null;
                if (string.IsNullOrEmpty(value) || value.StartsWith('-'))
                {
                    missingOptionValue ??= arg;
                }
                else if (extensionFlagSource is not null)
                {
                    conflictingOptions ??= "--extension can only be provided once";
                    index++;
                }
                else
                {
                    extensionFlagSource = value;
                    index++;
                }

                continue;
            }

            if (arg.StartsWith('-'))
            {
                invalidOption ??= arg;
                continue;
            }

            if (source is null)
            {
                source = arg;
            }
            else
            {
                invalidArgument ??= arg;
            }
        }

        UpdateTarget? updateTarget = null;
        var showExtensionsSkippedNote = false;
        if (kind == PackageCommandKind.Update)
        {
            if (allFlag && (selfFlag || extensionsFlag || modelsFlag || extensionFlagSource is not null))
            {
                conflictingOptions ??=
                    "--all cannot be combined with --self, --extensions, --models, or --extension";
            }

            if (allFlag && source is not null)
            {
                conflictingOptions ??= "--all cannot be combined with a positional source";
            }

            if (modelsFlag)
            {
                if (selfFlag || extensionsFlag || allFlag || extensionFlagSource is not null)
                {
                    conflictingOptions ??=
                        "--models cannot be combined with --self, --extensions, --all, or --extension";
                }

                if (source is not null)
                {
                    conflictingOptions ??= "--models cannot be combined with a positional source";
                }

                updateTarget = new UpdateTarget.Models();
            }
            else if (extensionFlagSource is not null)
            {
                if (selfFlag || extensionsFlag || allFlag)
                {
                    conflictingOptions ??=
                        "--extension cannot be combined with --self, --extensions, or --all";
                }

                if (source is not null)
                {
                    conflictingOptions ??= "--extension cannot be combined with a positional source";
                }

                updateTarget = new UpdateTarget.Extensions(extensionFlagSource);
            }
            else if (source is not null)
            {
                var sourceIsSelf = source is "self" or "pi";
                if (sourceIsSelf)
                {
                    updateTarget = extensionsFlag ? new UpdateTarget.All() : new UpdateTarget.Self();
                }
                else
                {
                    if (extensionsFlag || selfFlag || allFlag)
                    {
                        conflictingOptions ??=
                            "positional update targets cannot be combined with --self, --extensions, or --all";
                    }

                    updateTarget = new UpdateTarget.Extensions(source);
                }
            }
            else if (allFlag)
            {
                updateTarget = new UpdateTarget.All();
            }
            else if (selfFlag && extensionsFlag)
            {
                updateTarget = new UpdateTarget.All();
            }
            else if (selfFlag)
            {
                updateTarget = new UpdateTarget.Self();
            }
            else if (extensionsFlag)
            {
                updateTarget = new UpdateTarget.Extensions(null);
            }
            else
            {
                updateTarget = new UpdateTarget.Self();
                showExtensionsSkippedNote = true;
            }
        }

        return new PackageCommandOptions
        {
            Command = kind,
            Source = source,
            UpdateTarget = updateTarget,
            ShowExtensionsSkippedNote = showExtensionsSkippedNote,
            Local = local,
            Force = force,
            ProjectTrustOverride = projectTrustOverride,
            Help = help,
            InvalidOption = invalidOption,
            InvalidArgument = invalidArgument,
            MissingOptionValue = missingOptionValue,
            ConflictingOptions = conflictingOptions,
        };
    }

    // ========================================================================
    // handleConfigCommand
    // ========================================================================

    /// <summary>
    /// Port of <c>handleConfigCommand</c>. Returns true when the args were a `config` command.
    /// </summary>
    public static async Task<PackageCommandResult> HandleConfigCommandAsync(
        IReadOnlyList<string> args,
        PackageCommandRuntimeOptions? runtimeOptions = null)
    {
        runtimeOptions ??= new PackageCommandRuntimeOptions();
        if (args.Count == 0 || args[0] != "config")
        {
            return PackageCommandResult.NotHandled;
        }

        var output = runtimeOptions.Out ?? Console.Out;
        var error = runtimeOptions.Error ?? Console.Error;

        if (args.Skip(1).Any(arg => arg is "-h" or "--help"))
        {
            PrintConfigCommandHelp(output);
            return PackageCommandResult.Done();
        }

        var local = false;
        bool? projectTrustOverride = null;
        foreach (var arg in args.Skip(1))
        {
            if (arg is "-l" or "--local")
            {
                local = true;
            }
            else if (arg is "-a" or "--approve")
            {
                projectTrustOverride = true;
            }
            else if (arg is "-na" or "--no-approve")
            {
                projectTrustOverride = false;
            }
            else if (arg.StartsWith('-'))
            {
                error.WriteLine(Chalk.Red($"Unknown option {arg} for \"config\"."));
                error.WriteLine(Chalk.Dim($"Use \"{Config.AppName} --help\" or \"{ConfigCommandUsage}\"."));
                return PackageCommandResult.Done(1);
            }
            else
            {
                error.WriteLine(Chalk.Red($"Unexpected argument {arg}."));
                error.WriteLine(Chalk.Dim($"Usage: {ConfigCommandUsage}"));
                return PackageCommandResult.Done(1);
            }
        }

        var cwd = runtimeOptions.Cwd ?? Environment.CurrentDirectory;
        var agentDir = runtimeOptions.AgentDir ?? Config.GetAgentDir();
        var (settingsManager, projectTrustWarnings) = await CreateCommandSettingsManagerAsync(
            new CommandSettingsOptions
            {
                Cwd = cwd,
                AgentDir = agentDir,
                ProjectTrustOverride = projectTrustOverride,
                ExtensionFactories = runtimeOptions.ExtensionFactories,
                Interactive = runtimeOptions.Interactive,
            }).ConfigureAwait(false);
        ReportProjectTrustWarnings(projectTrustWarnings, error);
        if (local && !settingsManager.IsProjectTrusted())
        {
            error.WriteLine(Chalk.Red("Project is not trusted. Use --approve to modify local resource config."));
            return PackageCommandResult.Done(1);
        }

        ReportSettingsErrors(settingsManager, "config command", error);

        var builtinExtensions = (runtimeOptions.ExtensionFactories ?? [])
            .Where(ResourceLoaderExtensions.IsBuiltinExtension)
            .Select(input => input switch
            {
                InlineExtension.Described described => described.Name,
                _ => "",
            })
            .ToList();

        var globalSettingsManager = SettingsManager.Create(cwd, agentDir, new SettingsManagerCreateOptions
        {
            ProjectTrusted = false,
        });
        var globalResolvedPaths = await new DefaultPackageManager(
            globalSettingsManager,
            cwd,
            agentDir,
            builtinExtensions).ResolveAsync().ConfigureAwait(false);
        var projectResolvedPaths = settingsManager.IsProjectTrusted()
            ? await new DefaultPackageManager(settingsManager, cwd, agentDir, builtinExtensions)
                .ResolveAsync().ConfigureAwait(false)
            : globalResolvedPaths;

        await ConfigSelectorCli.SelectConfigAsync(new ConfigSelectorOptions
        {
            ResolvedPaths = new ScopedResolvedResources
            {
                Global = globalResolvedPaths,
                Project = projectResolvedPaths,
            },
            SettingsManager = settingsManager,
            Cwd = cwd,
            AgentDir = agentDir,
            WriteScope = local ? "project" : "global",
            ProjectModeAvailable = settingsManager.IsProjectTrusted(),
        }).ConfigureAwait(false);

        return PackageCommandResult.Done(0);
    }

    // ========================================================================
    // handlePackageCommand
    // ========================================================================

    /// <summary>
    /// Port of <c>handlePackageCommand</c>. Returns true when the args were a package command.
    /// </summary>
    public static async Task<PackageCommandResult> HandlePackageCommandAsync(
        IReadOnlyList<string> args,
        PackageCommandRuntimeOptions? runtimeOptions = null)
    {
        runtimeOptions ??= new PackageCommandRuntimeOptions();
        var options = ParsePackageCommand(args);
        if (options is null)
        {
            return PackageCommandResult.NotHandled;
        }

        var output = runtimeOptions.Out ?? Console.Out;
        var error = runtimeOptions.Error ?? Console.Error;
        var usage = GetPackageCommandUsage(options.Command);

        if (options.Help)
        {
            PrintPackageCommandHelp(options.Command, output);
            return PackageCommandResult.Done();
        }

        if (options.InvalidOption is not null)
        {
            error.WriteLine(Chalk.Red($"Unknown option {options.InvalidOption} for \"{options.Command.ToText()}\"."));
            error.WriteLine(Chalk.Dim($"Use \"{Config.AppName} --help\" or \"{usage}\"."));
            return PackageCommandResult.Done(1);
        }

        if (options.MissingOptionValue is not null)
        {
            error.WriteLine(Chalk.Red($"Missing value for {options.MissingOptionValue}."));
            error.WriteLine(Chalk.Dim($"Usage: {usage}"));
            return PackageCommandResult.Done(1);
        }

        if (options.InvalidArgument is not null)
        {
            error.WriteLine(Chalk.Red($"Unexpected argument {options.InvalidArgument}."));
            error.WriteLine(Chalk.Dim($"Usage: {usage}"));
            return PackageCommandResult.Done(1);
        }

        if (options.ConflictingOptions is not null)
        {
            error.WriteLine(Chalk.Red(options.ConflictingOptions));
            error.WriteLine(Chalk.Dim($"Usage: {usage}"));
            return PackageCommandResult.Done(1);
        }

        var source = options.Source;
        if (options.Command is PackageCommandKind.Install or PackageCommandKind.Remove && source is null)
        {
            error.WriteLine(Chalk.Red($"Missing {options.Command.ToText()} source."));
            error.WriteLine(Chalk.Dim($"Usage: {usage}"));
            return PackageCommandResult.Done(1);
        }

        var agentDir = runtimeOptions.AgentDir ?? Config.GetAgentDir();
        if (options.Command == PackageCommandKind.Update && options.UpdateTarget is UpdateTarget.Models)
        {
            try
            {
                await RefreshModelCatalogsAsync(agentDir, output).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error.WriteLine(Chalk.Red($"Error: {ex.Message}"));
                return PackageCommandResult.Done(1);
            }

            return PackageCommandResult.Done();
        }

        var cwd = runtimeOptions.Cwd ?? Environment.CurrentDirectory;
        var writesProjectPackageConfig =
            options.Command is PackageCommandKind.Install or PackageCommandKind.Remove && options.Local;
        var (settingsManager, projectTrustWarnings) = await CreateCommandSettingsManagerAsync(
            new CommandSettingsOptions
            {
                Cwd = cwd,
                AgentDir = agentDir,
                ProjectTrustOverride = options.ProjectTrustOverride,
                UseSavedProjectTrustOnly = options.Command == PackageCommandKind.Update,
                ExtensionFactories = runtimeOptions.ExtensionFactories,
                Interactive = runtimeOptions.Interactive,
            }).ConfigureAwait(false);
        ReportProjectTrustWarnings(projectTrustWarnings, error);
        if (!settingsManager.IsProjectTrusted() && writesProjectPackageConfig)
        {
            error.WriteLine(Chalk.Red("Project is not trusted. Use --approve to modify local package config."));
            return PackageCommandResult.Done(1);
        }

        ReportSettingsErrors(settingsManager, "package command", error);

        var packageManager = new DefaultPackageManager(settingsManager, cwd, agentDir);
        packageManager.SetProgressCallback(evt =>
        {
            if (evt.Type == "start")
            {
                output.Write(Chalk.Dim($"{evt.Message}\n"));
            }
        });

        try
        {
            switch (options.Command)
            {
                case PackageCommandKind.Install:
                    await packageManager.InstallAndPersistAsync(source!, options.Local).ConfigureAwait(false);
                    output.WriteLine(Chalk.Green($"Installed {source}"));
                    return PackageCommandResult.Done();

                case PackageCommandKind.Remove:
                {
                    var removed = await packageManager.RemoveAndPersistAsync(source!, options.Local)
                        .ConfigureAwait(false);
                    if (!removed)
                    {
                        error.WriteLine(Chalk.Red($"No matching package found for {source}"));
                        return PackageCommandResult.Done(1);
                    }

                    output.WriteLine(Chalk.Green($"Removed {source}"));
                    return PackageCommandResult.Done();
                }

                case PackageCommandKind.List:
                {
                    var configuredPackages = packageManager.ListConfiguredPackages();
                    var userPackages = configuredPackages.Where(pkg => pkg.Scope == "user").ToList();
                    var projectPackages = configuredPackages.Where(pkg => pkg.Scope == "project").ToList();

                    if (configuredPackages.Count == 0)
                    {
                        output.WriteLine(Chalk.Dim("No packages installed."));
                        return PackageCommandResult.Done();
                    }

                    void FormatPackage(ConfiguredPackage pkg)
                    {
                        var display = pkg.Filtered ? $"{pkg.Source} (filtered)" : pkg.Source;
                        output.WriteLine($"  {display}");
                        if (pkg.InstalledPath is not null)
                        {
                            output.WriteLine(Chalk.Dim($"    {pkg.InstalledPath}"));
                        }
                    }

                    if (userPackages.Count > 0)
                    {
                        output.WriteLine(Chalk.Bold("User packages:"));
                        foreach (var pkg in userPackages)
                        {
                            FormatPackage(pkg);
                        }
                    }

                    if (projectPackages.Count > 0)
                    {
                        if (userPackages.Count > 0)
                        {
                            output.WriteLine();
                        }

                        output.WriteLine(Chalk.Bold("Project packages:"));
                        foreach (var pkg in projectPackages)
                        {
                            FormatPackage(pkg);
                        }
                    }

                    return PackageCommandResult.Done();
                }

                case PackageCommandKind.Update:
                {
                    var target = options.UpdateTarget ?? new UpdateTarget.Self();
                    if (options.ShowExtensionsSkippedNote)
                    {
                        output.WriteLine(Chalk.Dim(
                            $"Extensions are skipped. Run {Config.AppName} update --extensions to update extensions."));
                    }

                    var failed = false;
                    if (target.IncludesExtensions)
                    {
                        var updateSource = (target as UpdateTarget.Extensions)?.Source;
                        await packageManager.UpdateAsync(updateSource).ConfigureAwait(false);
                        output.WriteLine(Chalk.Green(
                            updateSource is null ? "Updated packages" : $"Updated {updateSource}"));
                    }

                    if (target.IncludesSelf)
                    {
                        // TS resolves a self-update plan and runs npm/pnpm (or the managed installer).
                        // The .NET build is not an npm package, so there is nothing to reinstall (C301).
                        error.WriteLine(Chalk.Red($"error: {Config.AppName} cannot self-update this installation."));
                        error.WriteLine(Chalk.Dim(
                            $"Download the latest {Config.AppName} release and replace the installed files."));
                        failed = true;
                    }

                    return PackageCommandResult.Done(failed ? 1 : 0);
                }

                default:
                    return PackageCommandResult.Done();
            }
        }
        catch (Exception ex)
        {
            error.WriteLine(Chalk.Red($"Error: {ex.Message}"));
            return PackageCommandResult.Done(1);
        }
    }

    // ========================================================================
    // 内部辅助
    // ========================================================================

    /// <summary>Port of <c>refreshModelCatalogs</c>.</summary>
    internal static async Task RefreshModelCatalogsAsync(string agentDir, TextWriter? output = null)
    {
        output ??= Console.Out;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var modelRuntime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            AuthPath = NodePath.Join(agentDir, "auth.json"),
            ModelsPath = NodePath.Join(agentDir, "models.json"),
            AllowModelNetwork = false,
            Signal = cts.Token,
        }).ConfigureAwait(false);
        var result = await modelRuntime.RefreshAsync(new ModelsRefreshOptions
        {
            AllowNetwork = true,
            Force = true,
            Signal = cts.Token,
        }).ConfigureAwait(false);
        if (result.Aborted)
        {
            throw new InvalidOperationException("Model catalog refresh timed out.");
        }

        if (result.Errors.Count > 0)
        {
            var details = string.Join(
                "; ",
                result.Errors.Select(pair => $"{pair.Key}: {pair.Value.Message}"));
            throw new InvalidOperationException($"Could not refresh model catalogs: {details}");
        }

        output.WriteLine(Chalk.Green("Model catalogs refreshed"));
    }

    private sealed record CommandSettingsOptions
    {
        public required string Cwd { get; init; }

        public required string AgentDir { get; init; }

        public bool? ProjectTrustOverride { get; init; }

        public bool UseSavedProjectTrustOnly { get; init; }

        public IReadOnlyList<InlineExtension>? ExtensionFactories { get; init; }

        public bool? Interactive { get; init; }
    }

    /// <summary>Port of <c>createCommandSettingsManager</c>.</summary>
    private static async Task<(SettingsManager SettingsManager, List<string> ProjectTrustWarnings)>
        CreateCommandSettingsManagerAsync(CommandSettingsOptions options)
    {
        var settingsManager = SettingsManager.Create(options.Cwd, options.AgentDir, new SettingsManagerCreateOptions
        {
            ProjectTrusted = false,
        });
        var projectTrustWarnings = new List<string>();
        var trustStore = new ProjectTrustStore(options.AgentDir);
        if (options.UseSavedProjectTrustOnly)
        {
            var savedProjectTrusted = trustStore.Get(options.Cwd) == true;
            settingsManager.SetProjectTrusted(options.ProjectTrustOverride ?? savedProjectTrusted);
            return (settingsManager, projectTrustWarnings);
        }

        var appMode = GetCommandAppMode(options.Interactive);
        LoadExtensionsResult? extensionsResult = null;
        if (options.ProjectTrustOverride is null && TrustManager.HasTrustRequiringProjectResources(options.Cwd))
        {
            var loader = new DefaultResourceLoader(
                options.Cwd,
                options.AgentDir,
                settingsManager,
                extensionFactories: options.ExtensionFactories);
            extensionsResult = await loader.LoadProjectTrustExtensionsAsync().ConfigureAwait(false);
        }

        foreach (var loadError in extensionsResult?.Errors ?? [])
        {
            projectTrustWarnings.Add($"Failed to load extension \"{loadPath(loadError)}\": {loadError.Error}");
        }

        var projectTrusted = await ProjectTrust.ResolveProjectTrustedAsync(new ResolveProjectTrustedOptions
        {
            Cwd = options.Cwd,
            TrustStore = trustStore,
            TrustOverride = options.ProjectTrustOverride,
            DefaultProjectTrust = settingsManager.GetDefaultProjectTrust(),
            ExtensionsResult = extensionsResult,
            ProjectTrustContext = ProjectTrustUi.CreateProjectTrustContext(new ProjectTrustUiOptions
            {
                Cwd = options.Cwd,
                Mode = appMode,
                SettingsManager = settingsManager,
                HasUI = appMode == AppModes.Interactive,
            }),
            OnExtensionError = projectTrustWarnings.Add,
        }).ConfigureAwait(false);
        settingsManager.SetProjectTrusted(projectTrusted);
        return (settingsManager, projectTrustWarnings);

        static string loadPath(ExtensionLoadError error) => error.Path;
    }

    /// <summary>Port of <c>getCommandAppMode</c>.</summary>
    internal static string GetCommandAppMode(bool? interactive = null)
    {
        var isInteractive = interactive ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected);
        return isInteractive ? AppModes.Interactive : AppModes.Print;
    }

    /// <summary>Port of <c>reportProjectTrustWarnings</c>.</summary>
    internal static void ReportProjectTrustWarnings(IReadOnlyList<string> warnings, TextWriter? error = null)
    {
        error ??= Console.Error;
        foreach (var warning in warnings)
        {
            error.WriteLine(Chalk.Yellow($"Warning: {warning}"));
        }
    }

    /// <summary>Port of <c>reportSettingsErrors</c>.</summary>
    internal static void ReportSettingsErrors(SettingsManager settingsManager, string context, TextWriter? error = null)
    {
        error ??= Console.Error;
        foreach (var entry in settingsManager.DrainErrors())
        {
            var scope = entry.Scope == SettingsScope.Global ? "global" : "project";
            error.WriteLine(Chalk.Yellow($"Warning ({context}, {scope} settings): {entry.Error.Message}"));
            if (entry.Error.StackTrace is not null)
            {
                error.WriteLine(Chalk.Dim(entry.Error.StackTrace));
            }
        }
    }

    /// <summary>Unused but kept for symmetry with the TS constants (see difference C301).</summary>
    internal static (string Base, string Marker) InstallerConstants => (DefaultInstallerApiBase, ManagedInstallMarker);
}
