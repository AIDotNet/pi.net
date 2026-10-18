// ============================================================================
// RpcEntry — port of rpc-entry.ts (4g-2)
// ============================================================================
//
// TS:
//   process.title = `${APP_NAME}-rpc`;
//   process.env.PI_CODING_AGENT = "true";
//   process.env.AI_AGENT = "pi";
//   process.emitWarning = (() => {}) as typeof process.emitWarning;
//   configureHttpDispatcher();
//   main(["--mode", "rpc", ...process.argv.slice(2)]);
//
// Platform gaps (all already registered):
// - `process.title` has no managed equivalent (C284).
// - `process.emitWarning` cannot be replaced globally (C284).
// - `configureHttpDispatcher()` is the C32 platform gap; the port keeps only the proxy settings
//   that `main` applies itself.
//
// The TS file is a `package.json` "exports" subpath rather than a bin, so there is no second
// executable: the port exposes `RunAsync` and lets the CLI entry opt in with `PI_RPC_ENTRY=1`.

using Pi.CodingAgent.Cli;

namespace Pi.Cli;

/// <summary>Port of <c>rpc-entry.ts</c>.</summary>
public static class RpcEntry
{
    /// <summary>Runs the CLI in RPC mode, prepending <c>--mode rpc</c> to <paramref name="args"/>.</summary>
    public static Task<int> RunAsync(string[] args)
    {
        var forwarded = new string[args.Length + 2];
        forwarded[0] = "--mode";
        forwarded[1] = "rpc";
        Array.Copy(args, 0, forwarded, 2, args.Length);
        return Program.RunAsync(forwarded);
    }

    /// <summary>True when the process was started through the RPC entry point.</summary>
    internal static bool IsRpcEntryRequested() =>
        Environment.GetEnvironmentVariable("PI_RPC_ENTRY") is "1" or "true";
}
