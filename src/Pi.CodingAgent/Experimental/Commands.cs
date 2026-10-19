// ============================================================================
// Experimental command dispatch — port of experimental/commands.ts (4g-3d-4c)
// ============================================================================
//
// The development entrypoint's command layer: `runServerCommand` (operator-held server with the
// Radius status line), `runClientCommand` (TUI vs headless decision plus the result printing), and
// `runExperimentalCommand` (the gate, the option parser, and the error/exit-code contract).
//
// The headless client runs through the injected runner, which defaults to the real
// `ExperimentalClient.RunClientAsync` (client.ts, 4e-3c-2); `runClientTui` (client-tui.ts) lands
// with 4e-3c-3, so that seam still reports a clear error until then.
//
// Port differences:
// - chalk's colour is the C# `Chalk.Red` (same FORCE_COLOR detection), which degrades to plain
//   text when colour is off — the tests get a plain "Error: …" line either way.
// - The SIGINT/SIGTERM wait uses Console.CancelKeyPress + PosixSignalRegistration, the same pair
//   CoordinatorProcess already uses (C284).

using System.Runtime.InteropServices;
using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental.Services;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Experimental;

/// <summary>
/// The outcome of one client run. TS <c>ClientResult</c> (client.ts): a discriminated union the
/// client command prints in three different shapes.
/// </summary>
public abstract record ClientResult
{
    /// <summary>Every session of every discovered server, tab-separated per line.</summary>
    public sealed record List(IReadOnlyList<SessionAddress> Sessions) : ClientResult;

    /// <summary>One attached session: <c>serverId\tsessionId\tattached</c>.</summary>
    public sealed record Attached(string ServerId, string SessionId) : ClientResult;

    /// <summary>One prompted session: the answer text only.</summary>
    public sealed record Prompted(string ServerId, string SessionId, string Text) : ClientResult;
}

/// <summary>The seams of <see cref="ExperimentalCommands.RunServerCommandAsync"/>.</summary>
public sealed record RunServerCommandOptions
{
    public TextWriter? Out { get; init; }

    /// <summary>Defaults to <see cref="ExperimentalServer.StartForegroundServerAsync"/>.</summary>
    public Func<StartServerOptions, Task<RunningServer>>? StartServer { get; init; }

    /// <summary>
    /// Waits until the server closes or a termination signal arrives. Defaults to the
    /// SIGINT/SIGTERM registration; tests replace it with the runtime's <see cref="RunningServer.Closed"/>.
    /// </summary>
    public Func<RunningServer, Task>? AwaitShutdown { get; init; }
}

/// <summary>The seams of <see cref="ExperimentalCommands.RunClientCommandAsync"/>.</summary>
public sealed record RunClientCommandOptions
{
    public TextWriter? Out { get; init; }

    public TextWriter? Error { get; init; }

    /// <summary>TS <c>runClient</c> (client.ts, 4e-3c).</summary>
    public Func<ClientCommand, Task<ClientResult>>? RunClient { get; init; }

    /// <summary>TS <c>runClientTui</c> (client-tui.ts, 4e-3c).</summary>
    public Func<ClientCommand, Task>? RunClientTui { get; init; }

    public Func<bool>? StdinIsTty { get; init; }

    public Func<bool>? StdoutIsTty { get; init; }
}

/// <summary>
/// The Radius status line of <c>runServerCommand</c>: statuses that arrive before the server banner
/// are buffered (only the last one survives), <c>connecting</c> is never printed, and consecutive
/// identical descriptions are suppressed.
/// </summary>
public sealed class RelayStatusReporter
{
    private readonly TextWriter _out;
    private string _previous = "";
    private bool _ready;
    private RadiusRelayHostStatus? _pending;

    public RelayStatusReporter(TextWriter @out) => _out = @out;

    /// <summary>TS's inline <c>reportRelayStatus</c> mapping.</summary>
    public static string Describe(RadiusRelayHostStatus status) => status switch
    {
        RadiusRelayHostStatus.Connected => "connected",
        RadiusRelayHostStatus.NotAuthenticated => "not connected; local only",
        RadiusRelayHostStatus.Retrying retrying => $"reconnecting: {retrying.Error}",
        _ => "connecting",
    };

    /// <summary>TS's <c>onRelayStatus</c>: buffer until the banner is out, then report.</summary>
    public void Report(RadiusRelayHostStatus status)
    {
        if (!_ready)
        {
            _pending = status;
            return;
        }

        Write(status);
    }

    /// <summary>The banner is out; flush the buffered status, if any.</summary>
    public void MarkReady()
    {
        _ready = true;
        if (_pending is null) return;
        var status = _pending;
        _pending = null;
        Write(status);
    }

    private void Write(RadiusRelayHostStatus status)
    {
        var description = Describe(status);
        if (description == _previous || status is RadiusRelayHostStatus.Connecting) return;
        _previous = description;
        _out.WriteLine($"Radius: {description}");
    }
}

/// <summary>The seams of <see cref="ExperimentalCommands.RunExperimentalCommandAsync"/>.</summary>
public sealed record RunExperimentalCommandOptions
{
    public TextWriter? Out { get; init; }

    public TextWriter? Error { get; init; }
}

/// <summary>Port of <c>experimental/commands.ts</c>.</summary>
public static class ExperimentalCommands
{
    /// <summary>
    /// Run the operator-held server command. TS <c>runServerCommand</c>: start the server, print
    /// its identity and socket, then wait for a termination signal or the runtime closing, and
    /// always close it on the way out.
    /// </summary>
    public static async Task RunServerCommandAsync(ServerCommand command, RunServerCommandOptions? options = null)
    {
        var output = options?.Out ?? Console.Out;
        var reporter = new RelayStatusReporter(output);
        var runtime = await (options?.StartServer ?? ExperimentalServer.StartForegroundServerAsync)(
            new StartServerOptions
            {
                // TS: command.pluginPackages ?? [] — no -e flags clears the stored selection.
                ServerId = command.ServerId,
                SessionDir = command.SessionDir,
                Provider = command.Provider,
                Model = command.Model,
                PluginPackages = command.PluginPackages ?? [],
                RelayAuth = command.Auth,
                OnRelayStatus = reporter.Report,
            }).ConfigureAwait(false);
        try
        {
            output.WriteLine($"Server: {runtime.ServerId}");
            output.WriteLine($"Socket: {runtime.SocketPath}");
            reporter.MarkReady();

            var awaitShutdown = options?.AwaitShutdown ?? (server => AwaitShutdownAsync(server));
            await awaitShutdown(runtime).ConfigureAwait(false);
        }
        finally
        {
            await runtime.Close().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Run the client command. TS <c>runClientCommand</c>: an interactive terminal without a prompt
    /// opens the TUI, everything else runs headless and prints the result in the shape its kind
    /// demands.
    /// </summary>
    public static async Task RunClientCommandAsync(ClientCommand command, RunClientCommandOptions? options = null)
    {
        var output = options?.Out ?? Console.Out;
        var stdinIsTty = options?.StdinIsTty ?? (() => !Console.IsInputRedirected);
        var stdoutIsTty = options?.StdoutIsTty ?? (() => !Console.IsOutputRedirected);
        if (command.Prompt is null && stdinIsTty() && stdoutIsTty())
        {
            var tui = options?.RunClientTui
                ?? throw new InvalidOperationException("The experimental client TUI is not wired into this build yet");
            await tui(command).ConfigureAwait(false);
            return;
        }

        var runClient = options?.RunClient ?? (clientCommand => ExperimentalClient.RunClientAsync(clientCommand));
        var result = await runClient(command).ConfigureAwait(false);
        switch (result)
        {
            case ClientResult.Attached attached:
                output.WriteLine($"{attached.ServerId}\t{attached.SessionId}\tattached");
                return;
            case ClientResult.Prompted prompted:
                output.WriteLine(prompted.Text);
                return;
            default:
                var list = (ClientResult.List)result;
                foreach (var session in list.Sessions)
                {
                    output.WriteLine($"{session.ServerId}\t{session.SessionId}");
                }

                return;
        }
    }

    /// <summary>
    /// Dispatch an experimental command. TS <c>runExperimentalCommand</c>: false when experiments
    /// are off or the first argument is neither <c>server</c> nor <c>client</c> (the caller then
    /// runs the stable CLI), true once the experimental layer has taken over — including when it
    /// failed, in which case every error is printed and the exit code is set to 1.
    /// </summary>
    public static async Task<bool> RunExperimentalCommandAsync(
        IReadOnlyList<string> args, IExperimentalCliContext context, RunExperimentalCommandOptions? options = null)
    {
        if (!Pi.CodingAgent.Core.Experimental.AreExperimentalFeaturesEnabled()
            || (args.Count == 0 || (args[0] != "server" && args[0] != "client")))
        {
            return false;
        }

        var error = options?.Error ?? Console.Error;
        try
        {
            var result = await ExperimentalCli.Create().ExecuteAsync(args, context).ConfigureAwait(false);
            if (result is CommandExecutionResult.Failed failed)
            {
                foreach (var diagnostic in failed.Errors)
                {
                    error.WriteLine(Chalk.Red($"Error: {diagnostic}"));
                }

                Environment.ExitCode = 1;
            }
        }
        catch (Exception exception)
        {
            error.WriteLine(Chalk.Red($"Error: {exception.Message}"));
            Environment.ExitCode = 1;
        }

        return true;
    }

    /// <summary>
    /// TS's inline promise: resolve on SIGINT/SIGTERM or when the runtime closes, reject when it
    /// closes with an error. The handlers are removed once either settles.
    /// </summary>
    private static async Task AwaitShutdownAsync(RunningServer server)
    {
        // await unwraps the runtime's failure into its first inner exception, exactly what TS's
        // `runtime.closed.then(finish, fail)` rejects with.
        var closed = server.Closed.ContinueWith(
            static task => task.IsFaulted ? throw task.Exception!.InnerException! : Task.CompletedTask,
            TaskScheduler.Default).Unwrap();
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            // TS's handler takes over the default behaviour; the runtime unwinds instead.
            e.Cancel = true;
            signalled.TrySetResult();
        }

        Console.CancelKeyPress += OnCancelKeyPress;
        PosixSignalRegistration? sigterm = null;
        try
        {
            sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => signalled.TrySetResult());
        }
        catch (Exception error) when (error is PlatformNotSupportedException or IOException or ArgumentException)
        {
            // 平台不提供该信号（如 Windows 的 SIGTERM）：仅靠 Ctrl+C 关闭。
            sigterm = null;
        }

        try
        {
            await Task.WhenAny(closed, signalled.Task).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            try
            {
                sigterm?.Dispose();
            }
            catch (Exception error) when (error is ObjectDisposedException or IOException)
            {
                // 已释放。
            }

            // A failure that lands after the signal won must not go unobserved.
            _ = closed.ContinueWith(static _ => { }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        // TS rejects the wait with the runtime's failure; the caller's catch prints it.
        if (closed.IsFaulted)
        {
            await closed.ConfigureAwait(false);
        }
    }
}
