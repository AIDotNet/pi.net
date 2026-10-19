using Pi.CodingAgent.Cli;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// End-to-end runs of <see cref="Main.MainAsync"/> (4g-2). Everything that touches the filesystem is
/// redirected: the agent dir goes through <see cref="MainOptions.AgentDir"/> and — because
/// <see cref="Migrations.RunMigrations"/> reads the process-wide env var — through
/// <c>PI_CODING_AGENT_DIR</c>, which is restored in <see cref="Dispose"/>.
/// </summary>
public sealed class CliMainEndToEndTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousAgentDir;

    public CliMainEndToEndTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-main-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _previousAgentDir = Environment.GetEnvironmentVariable(Config.EnvAgentDir);
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, Path.Combine(_root, "agent"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, _previousAgentDir);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task VersionPrintsTheConfiguredVersionAndExitsZero()
    {
        var (code, output, _) = await RunAsync(["--version"]);

        Assert.Equal(0, code);
        Assert.Equal(Config.Version + Environment.NewLine, output);
        Assert.NotEqual("0.0.0", Config.Version);
    }

    [Fact]
    public async Task HelpExitsZero()
    {
        var (code, _, _) = await RunAsync(["--help"]);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task ListModelsExitsZero()
    {
        var (code, _, _) = await RunAsync(["--list-models"]);
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task InstallWithoutSourceFails()
    {
        var (code, _, error) = await RunAsync(["install"]);

        Assert.Equal(1, code);
        Assert.Contains("Missing install source.", error, StringComparison.Ordinal);
        Assert.Contains("Usage: pi install <source>", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageHelpExitsZero()
    {
        var (code, output, _) = await RunAsync(["list", "--help"]);

        Assert.Equal(0, code);
        Assert.Contains("List installed packages from user and project settings.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelfUpdateReportsThePortGapAndFails()
    {
        var (code, _, error) = await RunAsync(["update", "--self"]);

        Assert.Equal(1, code);
        Assert.Contains("cannot self-update this installation", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtensionsUpdateWithNoConfiguredPackagesSucceeds()
    {
        var (code, output, error) = await RunAsync(["update", "--extensions"]);

        Assert.Equal(0, code);
        Assert.Contains("Updated packages", output, StringComparison.Ordinal);
        Assert.Empty(error);
    }

    [Fact]
    public async Task ExtensionOptionLoadsTheRequestedBuiltinBeforePrintingHelp()
    {
        var factoryLoaded = false;
        var factory = new InlineExtension.Described(
            "cli-e2e",
            _ =>
            {
                factoryLoaded = true;
                return Task.CompletedTask;
            },
            Builtin: true);

        var (code, _, error) = await RunAsync(
            ["-e", "builtin:cli-e2e", "--help"],
            [factory]);

        Assert.Equal(0, code);
        Assert.True(factoryLoaded);
        Assert.Empty(error);
    }

    [Fact]
    public async Task UpdateWithNoTargetSkipsExtensionsAndStillReportsSelfGap()
    {
        // TS defaults to `--self` and prints the "extensions are skipped" note first.
        var (code, output, error) = await RunAsync(["update"]);

        Assert.Equal(1, code);
        Assert.Contains("Extensions are skipped.", output, StringComparison.Ordinal);
        Assert.Contains("cannot self-update this installation", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForkWithSessionFails()
    {
        var (code, _, error) = await RunAsync(["--fork", "some-target", "--session", "other"]);

        Assert.Equal(1, code);
        Assert.Contains("--fork cannot be combined with --session", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidSessionIdFails()
    {
        var (code, _, error) = await RunAsync(["--session-id", "bad id!"]);

        Assert.Equal(1, code);
        Assert.Contains("Session id must be", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownFlagIsNotAnErrorOnItsOwn()
    {
        // `--nonsense` is a candidate extension flag: the parse leaves it in unknownFlags and only
        // `--help` short-circuits before any session work.
        var (code, _, error) = await RunAsync(["--nonsense", "--help"]);

        Assert.Equal(0, code);
        Assert.DoesNotContain("Unknown option", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UninstallIsAnAliasForRemove()
    {
        var (code, _, error) = await RunAsync(["uninstall"]);

        Assert.Equal(1, code);
        Assert.Contains("Missing remove source.", error, StringComparison.Ordinal);
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(
        string[] args,
        IReadOnlyList<InlineExtension>? extensionFactories = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var options = new MainOptions
        {
            ExtensionFactories = extensionFactories,
            Cwd = _root,
            AgentDir = Path.Combine(_root, "agent"),
            Out = output,
            Error = error,
            StdinIsTty = true,
            StdoutIsTty = true,
            ReadPipedStdin = () => Task.FromResult<string?>(null),
            GetEnv = name => name == Config.EnvSessionDir ? null : Environment.GetEnvironmentVariable(name),
        };

        int code;
        try
        {
            code = await Main.MainAsync(args, options);
        }
        catch (MainExitException ex)
        {
            code = ex.ExitCode;
        }

        return (code, output.ToString(), error.ToString());
    }
}
