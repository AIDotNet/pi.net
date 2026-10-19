// ============================================================================
// ExperimentalEntry — port of experimental/cli.ts (4g-3d-4c)
// ============================================================================
//
// TS:
//   setupCli();
//   const args = process.argv.slice(2);
//   if (await runExperimentalCommand(args)) {
//       if (args[0] === "client") process.exit(process.exitCode ?? 0);
//   } else {
//       await main(args);
//   }
//
// The development entrypoint: the experimental layer gets the first look at the arguments, and the
// stable CLI is the fallback. A `client` invocation exits with the accumulated exit code right
// away (the TUI and its sockets must not keep the process alive); a `server` invocation simply
// returns once its runtime closes, exactly like TS.
//
// TS runs this as a separate file (`src/experimental/cli.ts`, the development bin), while the
// published bundle only ships the stable `cli.ts`. The port has one executable, so — following the
// RpcEntry pattern — the entry is selected with `PI_EXPERIMENTAL_ENTRY=1`.

using Pi.CodingAgent.Cli;
using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Experimental;

namespace Pi.Cli;

/// <summary>Port of <c>experimental/cli.ts</c>.</summary>
public static class ExperimentalEntry
{
    /// <summary>True when the process was started through the experimental entry point.</summary>
    internal static bool IsExperimentalEntryRequested() =>
        Environment.GetEnvironmentVariable("PI_EXPERIMENTAL_ENTRY") is "1" or "true";

    /// <summary>
    /// The experimental entry. Returns the process exit code; a handled client command exits with
    /// the code the experimental layer accumulated (TS's <c>process.exit(process.exitCode ?? 0)</c>).
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var handled = await ExperimentalCommands.RunExperimentalCommandAsync(
            args,
            new ExperimentalCliContext(
                command => ExperimentalCommands.RunServerCommandAsync(command),
                command => ExperimentalCommands.RunClientCommandAsync(command))).ConfigureAwait(false);
        if (!handled) return await RunStableAsync(args).ConfigureAwait(false);
        // TS: process.exit(process.exitCode ?? 0) — but only for the client command.
        return args.Length > 0 && args[0] == "client"
            ? Environment.ExitCode
            : Environment.ExitCode != 0 ? Environment.ExitCode : 0;
    }

    /// <summary>
    /// The stable fallback, with the same exit-code contract as <see cref="Program.RunAsync"/>:
    /// an exit thrown along the way wins, and a <c>process.exitCode</c> assignment wins over a
    /// normal zero return.
    /// </summary>
    private static async Task<int> RunStableAsync(string[] args)
    {
        try
        {
            var exitCode = await Pi.CodingAgent.Cli.Main.MainAsync(args).ConfigureAwait(false);
            return exitCode != 0 ? exitCode : Environment.ExitCode;
        }
        catch (MainExitException ex)
        {
            return ex.ExitCode;
        }
    }
}
