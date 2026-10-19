// ============================================================================
// Program — port of cli.ts (4g-2)
// ============================================================================
//
// TS:
//   setupCli();
//   main(process.argv.slice(2));
//
// The TS file is a bare entry script: `main()` never returns a code, it calls `process.exit` or
// sets `process.exitCode`. The port keeps that shape — `Main.MainAsync` throws `MainExitException`
// from its default exit hook — and turns it into a process exit code here.

using Pi.CodingAgent.Cli;
using Pi.CodingAgent.Experimental;

namespace Pi.Cli;

/// <summary>Port of <c>cli.ts</c>.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // C309/C337: a spawned internal process (coordinator / server / session-worker) runs its
        // role before any CLI setup — TS gives each role its own entry script, which never calls
        // setupCli, and the role tag is only ever set by a spawn.
        var internalExit = await InternalProcessEntry.TryRunAsync(args).ConfigureAwait(false);
        if (internalExit is not null) return internalExit.Value;

        CliSetup.SetupCli();
        return RpcEntry.IsRpcEntryRequested()
            ? await RpcEntry.RunAsync(args).ConfigureAwait(false)
            : ExperimentalEntry.IsExperimentalEntryRequested()
                ? await ExperimentalEntry.RunAsync(args).ConfigureAwait(false)
                : await RunAsync(args).ConfigureAwait(false);
    }

    /// <summary>Shared by the plain CLI entry and <see cref="RpcEntry"/>.</summary>
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var exitCode = await Pi.CodingAgent.Cli.Main.MainAsync(args).ConfigureAwait(false);
            // `process.exitCode` assignments made along the way (auth / package commands) win when
            // the flow returned normally with 0.
            return exitCode != 0 ? exitCode : Environment.ExitCode;
        }
        catch (MainExitException ex)
        {
            return ex.ExitCode;
        }
    }
}
