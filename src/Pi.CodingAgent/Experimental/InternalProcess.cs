// ============================================================================
// Internal process plumbing — port of experimental/process.ts (4g-3a)
// ============================================================================
//
// The experimental server spawns three kinds of Pi-owned background processes: a coordinator (the
// session catalog + worker registry), a server (the client-facing socket), and one session worker per
// hosted Session. TS distinguishes them by *entry script* (`coordinator.ts` / `server.ts` /
// `session-worker.ts`) and tags the child with `__PI_INTERNAL_SPAWN` so descendants can tell where
// they came from.
//
// A .NET CLI is a single executable, so there is no entry script to point at: the role travels as an
// argument instead (C309). Everything else — the env tag, the "consume so descendants do not inherit
// it" rule, the detached/ignore/hide spawn shape, the 128 MiB control-line ceiling — is the same.
//
// Not ported: `isDirectInternalProcessEntry`. It answers "was this module run directly rather than
// imported", which only exists because TS modules are also scripts; the port has no module-loading
// equivalent (C310).

using System.Text;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Experimental;

/// <summary>The roles a Pi-owned internal process can take. Port of the TS <c>InternalProcessRole</c>.</summary>
public enum InternalProcessRole
{
    /// <summary>Owns the session catalog and the worker registry.</summary>
    Coordinator,

    /// <summary>Owns the client-facing socket.</summary>
    Server,

    /// <summary>Owns one hosted Session's storage.</summary>
    SessionWorker,
}

/// <summary>Port of <c>experimental/process.ts</c>.</summary>
public static class InternalProcess
{
    /// <summary>The environment variable that tags a Pi-owned child process.</summary>
    public const string InternalProcessEnv = "__PI_INTERNAL_SPAWN";

    /// <summary>
    /// The argument that carries <see cref="InternalProcessRole"/> to a child process. TS uses a
    /// distinct entry script per role instead (C309).
    /// </summary>
    public const string InternalRoleArg = "--internal-role";

    /// <summary>Largest control line a parent will send a child, in UTF-8 bytes.</summary>
    public const int MaxControlLineBytes = 128 * 1024 * 1024;

    /// <summary>The wire spelling of a role, as it appears in the env var and the role argument.</summary>
    public static string ToWireValue(this InternalProcessRole role) => role switch
    {
        InternalProcessRole.Coordinator => "coordinator",
        InternalProcessRole.Server => "server",
        InternalProcessRole.SessionWorker => "session-worker",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    /// <summary>Parse a wire role, or <see langword="null"/> when it is not one of the three.</summary>
    public static InternalProcessRole? TryParseRole(string? value) => value switch
    {
        "coordinator" => InternalProcessRole.Coordinator,
        "server" => InternalProcessRole.Server,
        "session-worker" => InternalProcessRole.SessionWorker,
        _ => null,
    };

    /// <summary>
    /// Read and validate an internal process role without consuming it. Throws on an unknown value, so
    /// a stale or misspelled tag fails loudly instead of running as a plain CLI.
    /// </summary>
    public static InternalProcessRole? GetInternalProcessRole()
    {
        var role = Environment.GetEnvironmentVariable(InternalProcessEnv);
        if (role is null)
        {
            return null;
        }

        return TryParseRole(role) ?? throw new InvalidOperationException($"Unsupported internal process role: {role}");
    }

    /// <summary>Read, validate, and remove the role so descendants do not inherit it.</summary>
    public static InternalProcessRole? ConsumeInternalProcessRole()
    {
        var role = GetInternalProcessRole();
        Environment.SetEnvironmentVariable(InternalProcessEnv, null);
        return role;
    }

    /// <summary>Options of <see cref="SpawnInternalProcess"/>.</summary>
    public sealed record SpawnInternalProcessOptions
    {
        /// <summary>Environment overlay; a <see langword="null"/> value removes the variable.</summary>
        public IReadOnlyDictionary<string, string?>? Env { get; init; }

        /// <summary>Overrides the executable; defaults to the running one.</summary>
        public string? ExecutablePath { get; init; }
    }

    /// <summary>
    /// Spawn a detached Pi-owned process. The child inherits the environment plus the role tag, runs in
    /// the parent's working directory, and neither writes to nor reads from the parent's terminal.
    /// </summary>
    public static ChildProcessHandle SpawnInternalProcess(
        InternalProcessRole role,
        IReadOnlyList<string> args,
        SpawnInternalProcessOptions? options = null)
    {
        options ??= new SpawnInternalProcessOptions();
        var executable = options.ExecutablePath ?? Environment.ProcessPath ?? Config.AppName;

        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (options.Env is not null)
        {
            foreach (var (key, value) in options.Env)
            {
                env[key] = value;
            }
        }

        env[InternalProcessEnv] = role.ToWireValue();

        // TS passes `[sourceRuntimeArgs, entryScript, ...args]`; the port passes the role instead.
        string[] argv = [InternalRoleArg, role.ToWireValue(), .. args];

        return ChildProcess.SpawnProcess(executable, argv, new SpawnOptions
        {
            Cwd = Environment.CurrentDirectory,
            Detached = true,
            WindowsHide = true,
            Stdio = [StdioMode.Ignore, StdioMode.Ignore, StdioMode.Ignore],
            Env = env,
        });
    }

    /// <summary>
    /// Force a spawned internal process to exit and wait until it can no longer take ownership.
    /// </summary>
    public static async Task TerminateInternalProcessAsync(ChildProcessHandle child)
    {
        if (child.Id is null || child.ExitCode is not null || child.SignalCode is not null)
        {
            return;
        }

        // TS: kill("SIGKILL") then wait for "exit"/"error". KillTree matches the detached process group
        // TS creates with `detached: true`.
        child.KillTree();
        await child.WaitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// One control line: compact JSON plus a newline. Throws when the UTF-8 encoding exceeds
    /// <see cref="MaxControlLineBytes"/> so a runaway message cannot wedge the pipe.
    /// </summary>
    public static string EncodeControlLine(JsonNode? message) => EncodeControlLine(message, MaxControlLineBytes);

    /// <summary>Test seam: the same encoding against an arbitrary ceiling.</summary>
    internal static string EncodeControlLine(JsonNode? message, int maxBytes)
    {
        // TS: `${JSON.stringify(message)}\n`. JSON.stringify does not escape non-ASCII, while STJ's
        // default encoder does, so the relaxed encoder is required to keep the byte count comparable.
        var line = $"{message?.ToJsonString(CompactJson) ?? "null"}\n";
        if (Encoding.UTF8.GetByteCount(line) > maxBytes)
        {
            throw new InvalidOperationException("Internal control message is too large");
        }

        return line;
    }

    /// <summary>Compact output with <c>JSON.stringify</c>'s non-escaping of non-ASCII.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions CompactJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
