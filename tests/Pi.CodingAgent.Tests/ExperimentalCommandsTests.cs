using System.Runtime.InteropServices;
using Pi.Cli;
using Pi.CodingAgent.Cli;
using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Experimental;
using Pi.CodingAgent.Experimental.Services;
using Xunit;
using ExperimentalFeatures = Pi.CodingAgent.Core.Experimental;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <c>experimental/commands.ts</c> + <c>experimental/cli.ts</c> (4g-3d-4c): the dispatcher, the
/// server and client command layers, the internal-process entry, and the stable/experimental entry
/// boundary (TS <c>experimental-cli-entry.test.ts</c>).
/// </summary>
public sealed class ExperimentalCommandsTests : IDisposable
{
    private const string ServerId = "3f0d1c2a-9b4e-4d1f-8a77-5c6b7d8e9f02";
    private const string ValidServerId = "00000000-0000-4000-8000-000000000001";

    private readonly string? _previousExperimental;
    private readonly int _previousExitCode;
    private readonly string? _previousRole;
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public ExperimentalCommandsTests()
    {
        _previousExperimental = Environment.GetEnvironmentVariable(ExperimentalFeatures.ExperimentalEnvVar);
        _previousExitCode = Environment.ExitCode;
        _previousRole = Environment.GetEnvironmentVariable(InternalProcess.InternalProcessEnv);
        Environment.SetEnvironmentVariable(ExperimentalFeatures.ExperimentalEnvVar, null);
        Environment.SetEnvironmentVariable(InternalProcess.InternalProcessEnv, null);
        Environment.ExitCode = 0;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ExperimentalFeatures.ExperimentalEnvVar, _previousExperimental);
        Environment.SetEnvironmentVariable(InternalProcess.InternalProcessEnv, _previousRole);
        Environment.ExitCode = _previousExitCode;
    }

    private void EnableExperiments() =>
        Environment.SetEnvironmentVariable(ExperimentalFeatures.ExperimentalEnvVar, "1");

    // ---------------------------------------------------------------- runExperimentalCommand

    [Fact]
    public async Task RunExperimentalCommand_IgnoresEverythingTheExperimentalLayerDoesNotOwn()
    {
        var context = new RecordingContext();

        Assert.False(await ExperimentalCommands.RunExperimentalCommandAsync([], context));
        Assert.False(await ExperimentalCommands.RunExperimentalCommandAsync(["auth", "login"], context));
        // The gate is the first condition: even a server command is ignored while it is off.
        Assert.False(await ExperimentalCommands.RunExperimentalCommandAsync(["server"], context));
        Assert.Empty(context.ServerCommands);
        Assert.Empty(context.ClientCommands);

        EnableExperiments();
        Assert.False(await ExperimentalCommands.RunExperimentalCommandAsync(["install", "x"], context));
        Assert.Empty(context.ServerCommands);
    }

    [Fact]
    public async Task RunExperimentalCommand_DispatchesTheServerCommand()
    {
        EnableExperiments();
        var context = new RecordingContext();

        Assert.True(await ExperimentalCommands.RunExperimentalCommandAsync(
            ["server", "--server-id", ValidServerId], context));

        var command = Assert.Single(context.ServerCommands);
        Assert.Equal(ValidServerId, command.ServerId);
        Assert.Equal(0, Environment.ExitCode);
    }

    [Fact]
    public async Task RunExperimentalCommand_ReportsParseErrorsAndSetsTheExitCode()
    {
        EnableExperiments();

        Assert.True(await ExperimentalCommands.RunExperimentalCommandAsync(
            ["server", "--server-id", "invalid"], new RecordingContext(),
            new RunExperimentalCommandOptions { Error = _error }));

        Assert.Equal(1, Environment.ExitCode);
        Assert.Contains("Invalid --server-id", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunExperimentalCommand_ReportsRunnerFailuresAndSetsTheExitCode()
    {
        EnableExperiments();
        var context = new RecordingContext { ServerFailure = new InvalidOperationException("boom") };

        Assert.True(await ExperimentalCommands.RunExperimentalCommandAsync(
            ["server"], context, new RunExperimentalCommandOptions { Error = _error }));

        Assert.Equal(1, Environment.ExitCode);
        Assert.Contains("Error: boom", _error.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- runServerCommand

    [Fact]
    public async Task RunServerCommand_PrintsTheIdentityThenFlushesTheBufferedRelayStatus()
    {
        var closeCalls = 0;
        var runtime = FakeRuntime(Task.CompletedTask, () => closeCalls += 1);
        var seenOptions = (StartServerOptions?)null;

        await ExperimentalCommands.RunServerCommandAsync(
            new ServerCommand { ServerId = ServerId },
            new RunServerCommandOptions
            {
                Out = _output,
                StartServer = options =>
                {
                    seenOptions = options;
                    // Statuses that arrive during startup are buffered; "connecting" never prints.
                    options.OnRelayStatus!(new RadiusRelayHostStatus.Connecting());
                    options.OnRelayStatus!(new RadiusRelayHostStatus.NotAuthenticated());
                    return Task.FromResult(runtime);
                },
                AwaitShutdown = server =>
                {
                    // After the banner: a fresh status prints, a repeat is suppressed.
                    seenOptions!.OnRelayStatus!(new RadiusRelayHostStatus.Retrying("boom"));
                    seenOptions.OnRelayStatus!(new RadiusRelayHostStatus.Retrying("boom"));
                    return Task.CompletedTask;
                },
            });

        Assert.Equal(
            [
                $"Server: {ServerId}",
                $"Socket: {runtime.SocketPath}",
                "Radius: not connected; local only",
                "Radius: reconnecting: boom",
            ],
            Lines(_output));
        Assert.NotNull(seenOptions);
        Assert.Equal(1, closeCalls);
    }

    [Fact]
    public async Task RunServerCommand_ClosesTheRuntimeWhenTheWaitFails()
    {
        var closeCalls = 0;
        var runtime = FakeRuntime(
            Task.FromException(new InvalidOperationException("server died")), () => closeCalls += 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExperimentalCommands.RunServerCommandAsync(
                new ServerCommand(),
                new RunServerCommandOptions
                {
                    Out = _output,
                    StartServer = _ => Task.FromResult(runtime),
                    AwaitShutdown = server => server.Closed,
                }));

        Assert.Equal("server died", error.Message);
        Assert.Equal(1, closeCalls);
    }

    [Fact]
    public async Task RunServerCommand_ForwardsTheCommandSelection()
    {
        var runtime = FakeRuntime(Task.CompletedTask);
        var seenOptions = (StartServerOptions?)null;
        var auth = new AuthInput.Token("token");
        await ExperimentalCommands.RunServerCommandAsync(
            new ServerCommand
            {
                ServerId = ServerId,
                SessionDir = "/sessions",
                Provider = "openai",
                Model = "gpt-5",
                Auth = auth,
            },
            new RunServerCommandOptions
            {
                Out = _output,
                StartServer = options =>
                {
                    seenOptions = options;
                    return Task.FromResult(runtime);
                },
                AwaitShutdown = _ => Task.CompletedTask,
            });

        // No -e flags clears the stored plugin selection (TS: command.pluginPackages ?? []).
        Assert.Equal(ServerId, seenOptions!.ServerId);
        Assert.Equal("/sessions", seenOptions.SessionDir);
        Assert.Equal("openai", seenOptions.Provider);
        Assert.Equal("gpt-5", seenOptions.Model);
        Assert.Same(auth, seenOptions.RelayAuth);
        Assert.NotNull(seenOptions.PluginPackages);
        Assert.Empty(seenOptions.PluginPackages!);
    }

    // ---------------------------------------------------------------- runClientCommand

    [Fact]
    public async Task RunClientCommand_OpensTheTuiWithoutAPromptOnATerminal()
    {
        var tuiCalls = 0;
        await ExperimentalCommands.RunClientCommandAsync(
            new ClientCommand(),
            new RunClientCommandOptions
            {
                Out = _output,
                RunClientTui = _ =>
                {
                    tuiCalls += 1;
                    return Task.CompletedTask;
                },
                StdinIsTty = () => true,
                StdoutIsTty = () => true,
            });

        Assert.Equal(1, tuiCalls);
        Assert.Empty(_output.ToString());
    }

    [Fact]
    public async Task RunClientCommand_RunsHeadlessWithAPromptEvenOnATerminal()
    {
        await ExperimentalCommands.RunClientCommandAsync(
            new ClientCommand { Prompt = "hi" },
            new RunClientCommandOptions
            {
                Out = _output,
                RunClientTui = _ => throw new InvalidOperationException("the TUI must not open"),
                RunClient = _ => Task.FromResult<ClientResult>(
                    new ClientResult.Prompted(ServerId, "s1", "answer")),
                StdinIsTty = () => true,
                StdoutIsTty = () => true,
            });

        Assert.Equal("answer" + Environment.NewLine, _output.ToString());
    }

    [Fact]
    public async Task RunClientCommand_PrintsTheAttachedAndListShapes()
    {
        await ExperimentalCommands.RunClientCommandAsync(
            new ClientCommand(),
            new RunClientCommandOptions
            {
                Out = _output,
                RunClientTui = _ => throw new InvalidOperationException("not a terminal"),
                RunClient = _ => Task.FromResult<ClientResult>(new ClientResult.Attached(ServerId, "s1")),
                StdinIsTty = () => false,
                StdoutIsTty = () => true,
            });
        Assert.Equal($"{ServerId}\ts1\tattached" + Environment.NewLine, _output.ToString());

        _output.GetStringBuilder().Clear();
        await ExperimentalCommands.RunClientCommandAsync(
            new ClientCommand(),
            new RunClientCommandOptions
            {
                Out = _output,
                RunClientTui = _ => throw new InvalidOperationException("not a terminal"),
                RunClient = _ => Task.FromResult<ClientResult>(new ClientResult.List(
                [
                    new SessionAddress(ServerId, "s2"),
                    new SessionAddress(ServerId, "s1"),
                ])),
                StdinIsTty = () => true,
                StdoutIsTty = () => false,
            });
        Assert.Equal($"{ServerId}\ts2{Environment.NewLine}{ServerId}\ts1{Environment.NewLine}", _output.ToString());
    }

    // ---------------------------------------------------------------- internal process entry

    [Fact]
    public async Task InternalProcessEntry_IsNotAnInternalProcessWithoutTheRoleTag()
    {
        Assert.Null(await InternalProcessEntry.TryRunAsync(["--internal-role", "server", "x"]));
    }

    [Fact]
    public async Task InternalProcessEntry_RejectsSessionWorkerInvocationWithoutOptions()
    {
        Environment.SetEnvironmentVariable(InternalProcess.InternalProcessEnv, "session-worker");

        Assert.Equal(1, await InternalProcessEntry.TryRunAsync(
            [InternalProcess.InternalRoleArg, "session-worker"], _error));

        Assert.Contains("Session worker requires one options argument", _error.ToString(), StringComparison.Ordinal);
        Assert.Null(Environment.GetEnvironmentVariable(InternalProcess.InternalProcessEnv));
    }

    [Fact]
    public async Task InternalProcessEntry_RunsTheServerRoleAndStripsTheRoleArgument()
    {
        Environment.SetEnvironmentVariable(InternalProcess.InternalProcessEnv, "server");

        // The server role parses its arguments before starting anything, so a relative directory
        // fails fast — which also proves the forwarded arguments are the ones after the role pair.
        Assert.Equal(1, await InternalProcessEntry.TryRunAsync(
            [InternalProcess.InternalRoleArg, "server", "relative", ValidServerId, "/sessions"], _error));

        Assert.Contains("absolute server directory", _error.ToString(), StringComparison.Ordinal);
        Assert.Null(Environment.GetEnvironmentVariable(InternalProcess.InternalProcessEnv));
    }

    [Fact]
    public async Task InternalProcessEntry_RunsTheCoordinatorRole()
    {
        Environment.SetEnvironmentVariable(InternalProcess.InternalProcessEnv, "coordinator");

        // Missing socket paths fail fast; the happy path binds real sockets and runs until empty.
        Assert.Equal(1, await InternalProcessEntry.TryRunAsync(
            [InternalProcess.InternalRoleArg, "coordinator"], _error));

        Assert.Contains("public and control socket paths", _error.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- entry boundary

    [Fact]
    public async Task ExperimentalEntry_FallsBackToTheStableCliWhenExperimentsAreOff()
    {
        var (code, output, _) = await RunExperimentalEntryAsync(
            ["server", "--server-id", "invalid", "--version"]);

        Assert.Equal(0, code);
        Assert.Equal(Config.Version + Environment.NewLine, output);
    }

    [Fact]
    public async Task ExperimentalEntry_DispatchesWhenExperimentsAreOn()
    {
        EnableExperiments();
        var (code, output, error) = await RunExperimentalEntryAsync(
            ["server", "--server-id", "invalid"]);

        Assert.Equal(1, code);
        Assert.Contains("Invalid --server-id", error, StringComparison.Ordinal);
        Assert.DoesNotContain(Config.Version, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableEntry_NeverDispatchesExperimentalCommands()
    {
        // The published entrypoint (Pi.Cli.Program) has no experimental dispatch: the same
        // arguments print the version even with experiments on (TS #9132).
        EnableExperiments();
        var output = new StringWriter();
        var error = new StringWriter();
        int code;
        try
        {
            code = await Main.MainAsync(
                ["server", "--server-id", "invalid", "--version"],
                new MainOptions { Out = output, Error = error });
        }
        catch (MainExitException ex)
        {
            code = ex.ExitCode;
        }

        Assert.Equal(0, code);
        Assert.Equal(Config.Version + Environment.NewLine, output.ToString());
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<(int Code, string Output, string Error)> RunExperimentalEntryAsync(string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var code = await ExperimentalEntry.RunAsync(args);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    private static RunningServer FakeRuntime(Task closed, Action? onClose = null)
        => new()
        {
            ServerId = ServerId,
            SessionDir = "/sessions",
            SocketPath = $"/tmp/{ServerId}.sock",
            // The command layer only reads the identity, the socket path, and the close handles.
            Server = null!,
            WorkerPids = new Dictionary<string, long>(),
            Closed = closed,
            Close = () =>
            {
                onClose?.Invoke();
                return Task.CompletedTask;
            },
        };

    private static List<string> Lines(StringWriter writer) =>
        writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')).ToList();

    private sealed class RecordingContext : IExperimentalCliContext
    {
        public List<ServerCommand> ServerCommands { get; } = [];

        public List<ClientCommand> ClientCommands { get; } = [];

        public Exception? ServerFailure { get; init; }

        public Task RunServerAsync(ServerCommand command)
        {
            ServerCommands.Add(command);
            return ServerFailure is null ? Task.CompletedTask : Task.FromException(ServerFailure);
        }

        public Task RunClientAsync(ClientCommand command)
        {
            ClientCommands.Add(command);
            return Task.CompletedTask;
        }
    }
}
