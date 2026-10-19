// ============================================================================
// Internal process entry — the C# answer to TS's per-role entry scripts (4g-3d-4c)
// ============================================================================
//
// TS gives every internal process its own entry script: the bottom blocks of coordinator.ts,
// server.ts and session-worker.ts check `isDirectInternalProcessEntry` and run their role, and the
// spawner points the child at that file. The port tags the child with the role (env var plus the
// `--internal-role` argument, C309) and dispatches here, so any entry that starts the executable
// runs the role first — a spawned coordinator must never fall through to the CLI.
//
// The role tag is consumed (removed from the environment) so descendants do not inherit it, and
// the `--internal-role <role>` pair is stripped from the arguments the role runner receives: TS
// passes `process.argv.slice(2)` of a role-specific script, the port forwards the remainder.
//
// Exit codes: a role that fails prints the error and exits 1, exactly like TS's entry blocks
// (`console.error(error); process.exit(1)`). The session-worker role is not wired yet — it waits
// for 4g-3f's durable harness setup (C362).

namespace Pi.CodingAgent.Experimental;

/// <summary>The process-entry dispatch for Pi-owned internal processes.</summary>
public static class InternalProcessEntry
{
    /// <summary>
    /// Run the internal role this process was spawned for, or return null when it is not an
    /// internal process (no role tag). The role is only peeked here: each runner consumes it itself,
    /// exactly like TS's per-role entry blocks.
    /// </summary>
    public static async Task<int?> TryRunAsync(IReadOnlyList<string> args, TextWriter? error = null)
    {
        var role = InternalProcess.GetInternalProcessRole();
        if (role is null) return null;
        var forwarded = StripRoleArgument(args);
        try
        {
            switch (role.Value)
            {
                case InternalProcessRole.Coordinator:
                    await Coordinator.RunCoordinatorEntryAsync(forwarded).ConfigureAwait(false);
                    return 0;

                case InternalProcessRole.Server:
                    await ExperimentalServer.RunServerEntryAsync(forwarded).ConfigureAwait(false);
                    return 0;

                default:
                    // 4g-3f wires the session worker (C362); consume the tag so it cannot leak.
                    InternalProcess.ConsumeInternalProcessRole();
                    throw new InvalidOperationException(
                        $"Internal process role {role.Value} is not wired into this build yet");
            }
        }
        catch (Exception exception)
        {
            // TS: console.error(error); process.exit(1).
            (error ?? Console.Error).WriteLine(exception);
            return 1;
        }
    }

    /// <summary>
    /// Drop the leading <c>--internal-role &lt;role&gt;</c> pair the spawner prepends (C309); every
    /// other argument is forwarded untouched.
    /// </summary>
    internal static IReadOnlyList<string> StripRoleArgument(IReadOnlyList<string> args)
    {
        if (args.Count >= 2 && args[0] == InternalProcess.InternalRoleArg) return args.Skip(2).ToList();
        return args;
    }
}
