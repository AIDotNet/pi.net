using System.Text.Json;
using Pi.CodingAgent.Cli;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class CliDoctorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-doctor-" + Guid.NewGuid().ToString("N"));
    private string AgentDir => Path.Combine(_root, "agent");

    public CliDoctorTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private DoctorOptions Options() => new()
    {
        Cwd = _root,
        AgentDir = AgentDir,
        GetEnv = _ => null,
        ResolveBash = _ => new ShellConfig("test-bash", ["-c"]),
        ResolvePowerShell = () => new ShellConfig("test-powershell", []),
        FindTool = _ => null,
        IsWindows = true,
    };

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static DoctorCheck Check(DoctorReport report, string id)
        => Assert.Single(report.Checks, item => item.Id == id);

    [Fact]
    public async Task EmptyInstallationCompletesWithoutCreatingFiles()
    {
        var report = await DoctorCommand.CollectAsync(Options());

        Assert.Equal(0, report.ExitCode);
        Assert.Equal("skipped", Check(report, "config.global").Status);
        Assert.Equal("warning", Check(report, "credentials").Status);
        Assert.Equal("warning", Check(report, "tool.fd").Status);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Theory]
    [InlineData("settings.json", "config.global")]
    [InlineData("models.json", "config.models")]
    [InlineData("auth.json", "config.auth")]
    [InlineData("trust.json", "project.trust")]
    public async Task BrokenConfigurationDoesNotStopOtherChecks(string file, string id)
    {
        Write(Path.Combine("agent", file), "{ broken-json");

        var report = await DoctorCommand.CollectAsync(Options());

        Assert.Equal(1, report.ExitCode);
        Assert.Equal("error", Check(report, id).Status);
        Assert.Equal("ok", Check(report, "shell.bash").Status);
        Assert.NotNull(Check(report, "credentials"));
        Assert.Equal("{ broken-json", File.ReadAllText(Path.Combine(AgentDir, file)));
    }

    [Fact]
    public async Task ManagedToolsAreLookedUpInTheSelectedAgentDirectoryWithoutExecution()
    {
        var relative = Path.Combine("agent", "bin", OperatingSystem.IsWindows() ? "fd.exe" : "fd");
        Write(relative, "not an executable");

        var report = await DoctorCommand.CollectAsync(Options() with { FindTool = null });

        Assert.Equal("ok", Check(report, "tool.fd").Status);
        Assert.Equal(Path.Combine(_root, relative), Check(report, "tool.fd").Path);
        Assert.Equal("not an executable", File.ReadAllText(Path.Combine(_root, relative)));
    }

    [Fact]
    public async Task NonObjectSettingsAreReportedAsInvalid()
    {
        Write("agent/settings.json", "[]");

        var report = await DoctorCommand.CollectAsync(Options());

        Assert.Equal(1, report.ExitCode);
        Assert.Equal("error", Check(report, "config.global").Status);
        Assert.Equal("[]", File.ReadAllText(Path.Combine(AgentDir, "settings.json")));
    }

    [Fact]
    public async Task UntrustedProjectSettingsAreSkipped()
    {
        Write(".pi/settings.json", "{ broken-json");

        var report = await DoctorCommand.CollectAsync(Options());

        Assert.Equal(0, report.ExitCode);
        Assert.Equal("skipped", Check(report, "config.project").Status);
        Assert.Equal("{ broken-json", File.ReadAllText(Path.Combine(_root, ".pi", "settings.json")));
    }

    [Fact]
    public async Task SavedParentTrustAppliesProjectToolsWithoutWritingLocksOrMigratingFiles()
    {
        Write("agent/trust.json", JsonSerializer.Serialize(new Dictionary<string, bool>
        {
            [TrustManager.NormalizeCwd(Path.GetDirectoryName(_root)!)] = true,
        }));
        Write(".pi/settings.json", """{"defaultTools":["read","powershell"],"queueMode":"one-at-a-time"}""");
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray();
        var beforeBytes = before.Select(File.ReadAllBytes).ToArray();
        var options = Options() with { ResolveBash = _ => throw new InvalidOperationException("not installed") };

        var report = await DoctorCommand.CollectAsync(options);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal("ok", Check(report, "config.project").Status);
        Assert.Equal("warning", Check(report, "shell.bash").Status);
        Assert.Equal("Default tools: read, powershell", Check(report, "tools.selection").Message);
        var after = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().ToArray();
        Assert.Equal(before, after);
        for (var index = 0; index < before.Length; index++) Assert.Equal(beforeBytes[index], File.ReadAllBytes(after[index]));
        Assert.Empty(Directory.GetDirectories(AgentDir));
    }

    [Fact]
    public async Task MissingRequiredShellIsAnErrorAndOptionalShellIsAWarning()
    {
        var report = await DoctorCommand.CollectAsync(Options() with
        {
            ResolveBash = _ => throw new InvalidOperationException("missing"),
            ResolvePowerShell = () => throw new InvalidOperationException("missing"),
        });

        Assert.Equal(1, report.ExitCode);
        Assert.Equal("error", Check(report, "shell.bash").Status);
        Assert.Equal("warning", Check(report, "shell.powershell").Status);
    }

    [Fact]
    public async Task PowershellSelectionIsAnErrorOnUnix()
    {
        Write("agent/settings.json", """{"defaultTools":["read","powershell"]}""");

        var report = await DoctorCommand.CollectAsync(Options() with { IsWindows = false });

        Assert.Equal(1, report.ExitCode);
        Assert.Equal("error", Check(report, "shell.powershell").Status);
    }

    [Fact]
    public async Task LegacyWslEntryIsNotReportedAsAnExecutedShell()
    {
        var report = await DoctorCommand.CollectAsync(Options() with
        {
            ResolveBash = _ => new ShellConfig(@"C:\Windows\System32\bash.exe", ["-s"], "stdin"),
        });

        Assert.Equal(0, report.ExitCode);
        Assert.Equal("warning", Check(report, "shell.bash").Status);
        Assert.Contains("not been checked", Check(report, "shell.bash").Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportsCredentialMetadataWithoutRevealingValuesOrExecutingCommands(bool json)
    {
        const string secret = "doctor-test-secret-123";
        var marker = Path.Combine(_root, "credential-command-ran");
        var command = "!echo " + secret + " > \"" + marker + "\"";
        Write("agent/auth.json", JsonSerializer.Serialize(new
        {
            demo = new { type = "api_key", key = secret },
            dynamic = new { type = "api_key", key = command },
            oauth = new { type = "oauth", access = secret, refresh = secret, expires = 0 },
        }));
        Write("agent/models.json", JsonSerializer.Serialize(new
        {
            providers = new Dictionary<string, object> { ["custom"] = new { apiKey = command } },
        }));
        var options = Options() with { GetEnv = name => name == "OPENAI_API_KEY" ? secret : null };
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = await DoctorCommand.RunAsync(json ? ["--json"] : [], options, output, error);

        Assert.Equal(0, code);
        Assert.Empty(error.ToString());
        Assert.Contains("OPENAI_API_KEY", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("custom", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("demo", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("credential-command-ran", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task JsonAndTextReturnTheSameDiagnosticExitCode()
    {
        Write("agent/models.json", "{broken");
        using var json = new StringWriter();
        using var text = new StringWriter();
        using var error = new StringWriter();

        var jsonCode = await DoctorCommand.RunAsync(["--json"], Options(), json, error);
        var textCode = await DoctorCommand.RunAsync([], Options(), text, error);

        Assert.Equal(1, jsonCode);
        Assert.Equal(jsonCode, textCode);
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("error", document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "config.models").GetProperty("status").GetString());
        Assert.DoesNotContain("\u001b", json.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task HelpAndInvalidArgumentsDoNotReadConfiguration()
    {
        var options = Options() with { GetEnv = _ => throw new InvalidOperationException("must not inspect") };
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, await DoctorCommand.RunAsync(["--help"], options, output, error));
        Assert.Contains("Usage:", output.ToString(), StringComparison.Ordinal);
        output.GetStringBuilder().Clear();
        Assert.Equal(2, await DoctorCommand.RunAsync(["--unknown"], options, output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("Unknown doctor argument", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task MainDispatchesDoctorBeforeLoadingExtensionsOrStartingASession()
    {
        Write("agent/settings.json", """{"extensions":["missing.dll"],"packages":["npm:does-not-exist"]}""");
        var invoked = false;
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = await Pi.CodingAgent.Cli.Main.MainAsync(["doctor", "--json"], new MainOptions
        {
            Cwd = _root,
            AgentDir = AgentDir,
            Out = output,
            Error = error,
            GetEnv = _ => null,
            ReadPipedStdin = () => throw new InvalidOperationException("must not read stdin"),
            ExtensionFactories = [new InlineExtension.Described("failing", _ =>
            {
                invoked = true;
                throw new InvalidOperationException("must not load extensions");
            }, Builtin: true)],
        });

        Assert.False(invoked);
        Assert.Contains(code, new[] { 0, 1 }); // The real machine may not have a required shell.
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(Config.Version, document.RootElement.GetProperty("version").GetString());
        Assert.Empty(error.ToString());
        Assert.Equal(["settings.json"], Directory.GetFileSystemEntries(AgentDir).Select(Path.GetFileName));
    }
}
