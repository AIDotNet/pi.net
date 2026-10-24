using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pi.Ai;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Cli;

internal sealed record DoctorOptions
{
    public required string Cwd { get; init; }
    public required string AgentDir { get; init; }
    public Func<string, string?> GetEnv { get; init; } = Environment.GetEnvironmentVariable;
    public Func<string?, ShellConfig> ResolveBash { get; init; } = Shell.GetShellConfig;
    public Func<ShellConfig> ResolvePowerShell { get; init; } = Shell.GetPowerShellConfig;
    public Func<string, string?>? FindTool { get; init; }
    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();
}

internal sealed record DoctorCheck(string Id, string Status, string Message, string? Path = null, string? Suggestion = null);

internal sealed record DoctorReport(int SchemaVersion, string Version, IReadOnlyList<DoctorCheck> Checks)
{
    [JsonIgnore]
    public int ExitCode => Checks.Any(check => check.Status == "error") ? 1 : 0;
}

/// <summary>Local diagnostics that remain available before session and extension initialization.</summary>
internal static class DoctorCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args, DoctorOptions options, TextWriter output, TextWriter error)
    {
        if (args.Any(arg => arg is "--help" or "-h"))
        {
            PrintHelp(output);
            return 0;
        }

        var unknown = args.FirstOrDefault(arg => arg != "--json");
        if (unknown is not null)
        {
            error.WriteLine($"Unknown doctor argument: {unknown}");
            error.WriteLine($"Use \"{Config.AppName} doctor --help\" for usage.");
            return 2;
        }

        var report = await CollectAsync(options).ConfigureAwait(false);
        if (args.Contains("--json")) output.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        else WriteText(report, output);
        return report.ExitCode;
    }

    private static void PrintHelp(TextWriter output)
    {
        output.WriteLine($"Usage: {Config.AppName} doctor [--json]");
        output.WriteLine();
        output.WriteLine("Check local configuration, shell and search-tool paths, and credential sources.");
        output.WriteLine("Runs offline without loading extensions, executing credential commands, or changing files.");
        output.WriteLine("Shells are located but not executed; credential presence does not prove authentication works.");
        output.WriteLine("Exit codes: 0 = no errors (warnings allowed), 1 = diagnostic errors, 2 = invalid arguments.");
    }

    internal static async Task<DoctorReport> CollectAsync(DoctorOptions options)
    {
        var checks = new List<DoctorCheck>
        {
            new("runtime", "ok", $"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}"),
            new("config.directory", "ok", "Global configuration directory (may be absent on a new installation).", options.AgentDir),
        };

        var globalPath = Path.Combine(options.AgentDir, "settings.json");
        var projectPath = Path.Combine(options.Cwd, Config.ConfigDirName, "settings.json");
        var projectTrusted = false;
        var trustPath = Path.Combine(options.AgentDir, "trust.json");
        try
        {
            var trust = TrustManager.ReadTrustFile(trustPath);
            projectTrusted = TrustManager.FindNearestTrustEntryForTest(trust, options.Cwd)?.Decision == true;
            checks.Add(new("project.trust", projectTrusted ? "ok" : "skipped",
                projectTrusted ? "Project has a saved trust decision." : "Project settings are skipped without a saved trust decision.",
                trustPath));
        }
        catch (Exception error) when (IsFileError(error))
        {
            checks.Add(new("project.trust", "error", "Could not read or parse the project trust store.", trustPath));
        }

        // FileSettingsStorage takes write locks even when reading. A read-only adapter also keeps
        // migrations in memory and reuses the same parsing, defaults, and tool-selection rules.
        var settings = SettingsManager.FromStorage(
            new ReadOnlySettingsStorage(globalPath, projectPath),
            new SettingsManagerCreateOptions { ProjectTrusted = projectTrusted });
        var settingsErrors = settings.DrainErrors();
        AddSettingsCheck(SettingsScope.Global, globalPath, true);
        AddSettingsCheck(SettingsScope.Project, projectPath, projectTrusted);

        var modelsPath = Path.Combine(options.AgentDir, "models.json");
        var modelConfig = await ModelConfig.LoadAsync(modelsPath).ConfigureAwait(false);
        checks.Add(modelConfig.Error is not null
            ? new("config.models", "error", "Could not read or validate models.json. Check its JSON and provider/model definitions.", modelsPath)
            : new("config.models", Exists(modelsPath) ? "ok" : "skipped",
                Exists(modelsPath) ? "Model configuration loaded." : "No custom model configuration; built-in catalogs apply.", modelsPath));

        var tools = settings.GetDefaultTools() ?? SettingsManager.DefaultToolNames;
        checks.Add(new("tools.selection", "ok", $"Default tools: {string.Join(", ", tools)}"));
        AddShellCheck("bash", () => options.ResolveBash(settings.GetShellPath()), tools.Contains("bash"));
        if (options.IsWindows) AddShellCheck("powershell", options.ResolvePowerShell, tools.Contains("powershell"));
        else if (tools.Contains("powershell"))
            checks.Add(new("shell.powershell", "error", "The powershell tool is available only on native Windows.",
                Suggestion: "Remove powershell from defaultTools or run Pi on native Windows."));

        foreach (var tool in new[] { "rg", "fd" })
        {
            try
            {
                var path = options.FindTool is { } find
                    ? find(tool)
                    : ToolsManager.GetToolPath(tool, Path.Combine(options.AgentDir, "bin"));
                checks.Add(path is not null
                    ? new($"tool.{tool}", "ok", "Search tool located (not executed).", path)
                    : new($"tool.{tool}", "warning", "Search tool is not installed locally.",
                        Suggestion: $"Install {tool}, or allow Pi to download it when a search tool needs it."));
            }
            catch (Exception error) when (IsFileError(error))
            {
                checks.Add(new($"tool.{tool}", "warning", "Could not locate the search tool."));
            }
        }

        await AddCredentialChecksAsync().ConfigureAwait(false);
        return new DoctorReport(1, Config.Version, checks);

        void AddSettingsCheck(SettingsScope scope, string path, bool enabled)
        {
            var id = scope == SettingsScope.Global ? "config.global" : "config.project";
            if (!enabled)
            {
                checks.Add(new(id, "skipped", "Project settings were not loaded because the project is not trusted.", path));
                return;
            }
            var error = settingsErrors.FirstOrDefault(item => item.Scope == scope);
            checks.Add(error is not null
                ? new(id, "error", "Could not read or parse settings.json; defaults are used for this layer.", path)
                : new(id, Exists(path) ? "ok" : "skipped",
                    Exists(path) ? "Settings loaded." : "Settings file is absent; defaults apply.", path));
        }

        void AddShellCheck(string name, Func<ShellConfig> resolve, bool required)
        {
            try
            {
                var shell = resolve();
                var wsl = name == "bash" && Shell.IsLegacyWslBashPath(shell.Shell, options.GetEnv("SystemRoot"));
                checks.Add(new($"shell.{name}", wsl ? "warning" : "ok",
                    wsl ? "WSL Bash entry located; the Linux distribution and shell execution have not been checked."
                        : $"Shell located (not executed); {(required ? "selected in defaultTools" : "optional")}.",
                    shell.Shell,
                    wsl ? "Configure WSL, or select read,powershell,edit,write on native Windows." : null));
            }
            catch (Exception error) when (IsFileError(error))
            {
                checks.Add(new($"shell.{name}", required ? "error" : "warning",
                    required ? "The shell selected in defaultTools could not be located." : "Optional shell could not be located.",
                    Suggestion: name == "bash" && options.IsWindows
                        ? $"Install Git Bash or run \"{Config.AppName} --tools read,powershell,edit,write\"."
                        : $"Install {name} or update the shell/tool settings."));
            }
        }

        async Task AddCredentialChecksAsync()
        {
            var sources = new List<string>();
            var authPath = Path.Combine(options.AgentDir, "auth.json");
            try
            {
                var stored = await new ReadOnlyAuthStorage(authPath).ListAsync().ConfigureAwait(false);
                checks.Add(new("config.auth", Exists(authPath) ? "ok" : "skipped",
                    Exists(authPath) ? "Credential metadata loaded; values and validity were not checked." : "No saved credential file.", authPath));
                sources.AddRange(stored.Select(item => $"{item.ProviderId} ({item.Type})"));
            }
            catch (Exception error) when (IsFileError(error))
            {
                checks.Add(new("config.auth", "error", "Could not read or parse auth.json.", authPath));
            }

            var variables = EnvApiKeys.ProviderEnvMap.Values.Concat(
                ["ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_OAUTH_TOKEN", "COPILOT_GITHUB_TOKEN",
                 "AWS_PROFILE", "AWS_ACCESS_KEY_ID", "AWS_BEARER_TOKEN_BEDROCK", "GOOGLE_APPLICATION_CREDENTIALS"])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            sources.AddRange(variables.Where(name => !string.IsNullOrWhiteSpace(options.GetEnv(name)))
                .Select(name => $"environment: {name}"));
            sources.AddRange(modelConfig.GetProviderIds()
                .Where(id => !string.IsNullOrWhiteSpace(modelConfig.GetProvider(id)?.ApiKey))
                .Select(id => $"{id} (models.json credential configured; not evaluated)"));
            checks.Add(sources.Count > 0
                ? new("credentials", "ok", $"Configured sources (validity not checked): {string.Join(", ", sources)}")
                : new("credentials", "warning", "No stored credentials or known credential variables found. Ambient or extension-provided authentication was not checked.",
                    Suggestion: "Use /login for a provider that requires authentication."));
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsFileError(Exception error)
        => error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception;

    private static void WriteText(DoctorReport report, TextWriter output)
    {
        output.WriteLine($"Pi.NET doctor ({report.Version})");
        output.WriteLine();
        foreach (var check in report.Checks)
        {
            output.WriteLine($"{check.Status.ToUpperInvariant(),-8} {check.Id}: {check.Message}");
            if (check.Path is not null) output.WriteLine($"         {check.Path}");
            if (check.Suggestion is not null) output.WriteLine($"         Suggestion: {check.Suggestion}");
        }
        output.WriteLine();
        output.WriteLine($"Errors: {report.Checks.Count(check => check.Status == "error")}; warnings: {report.Checks.Count(check => check.Status == "warning")}");
    }

    private sealed class ReadOnlySettingsStorage(string globalPath, string projectPath) : ISettingsStorage
    {
        public void WithLock(SettingsScope scope, Func<string?, string?> fn)
        {
            var path = scope == SettingsScope.Global ? globalPath : projectPath;
            string? content;
            try { content = File.ReadAllText(path); }
            catch (FileNotFoundException) { content = null; }
            catch (DirectoryNotFoundException) { content = null; }

            if (!string.IsNullOrEmpty(content) && JsonNode.Parse(Text.StripBom(content)) is not JsonObject)
                throw new InvalidOperationException("Settings must be a JSON object.");
            if (fn(content) is not null)
                throw new InvalidOperationException("Doctor cannot write settings.");
        }
    }
}
