// ============================================================================
// Experimental server runtime — port of experimental/server.ts (4g-3d-4b)
// ============================================================================
//
// The replaceable server generation behind the stable coordinator endpoint. Four blocks:
//
// 1. Server directory identity and locking — `resolveServerDirectory`,
//    `ensurePrivateServerDirectory`, `acquireServerProfile` (one logical server id per directory,
//    minted once into `default-server-id` and serialized through a launcher lock) and
//    `acquireServerActivation` (cold-activation serialization).
// 2. `ServerLifetime` — the retirement policy that decides when an automatically activated server
//    exits: a startup grace for the first client, then both client and worker demand at zero.
// 3. `startServerBackend` — the session services behind one per-generation Unix socket.
// 4. The entry points: `startServer` (coordinator + workers + relay + backend), `startForegroundServer`
//    (operator-held, serialized against cold activation), `activateServer` (spawn if unreachable)
//    and `runServerProcess` (the internal process entry).
//
// Differences from TS (see docs/coding-agent-porting-status.md):
//   C334 — plugin package building needs an injected `IFacetEntryBundler`; .NET has no esbuild.
//   C369 — `ensurePrivateServerDirectory` skips the POSIX uid/mode checks on Windows instead of
//          refusing to run, the same choice the Unix listener and transport already made.

using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Node;
using Pi.Chord.Services;
using Pi.Client;
using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental.Plugins;
using Pi.CodingAgent.Experimental.Services;
using Pi.CodingAgent.Utils;
using Pi.Protocol;
using Pi.Server;
using Pi.Server.Transports.Unix;

// `Client` is both the Pi.Client type and a namespace name in scope; alias the type.
using ServerClient = Pi.Client.Client;

namespace Pi.CodingAgent.Experimental;

/// <summary>The server lifetime's timer source. TS's fake timers; production uses real delays.</summary>
public interface IServerLifetimeClock
{
    /// <summary>Schedule <paramref name="fire"/> after <paramref name="milliseconds"/>. TS <c>setTimeout</c>.</summary>
    IServerLifetimeTimer StartTimer(int milliseconds, Action fire);
}

/// <summary>One scheduled callback. TS's <c>clearTimeout</c> handle.</summary>
public interface IServerLifetimeTimer
{
    /// <summary>Cancel a not-yet-fired timer. TS <c>clearTimeout</c>.</summary>
    void Cancel();
}

/// <summary>
/// The server lifetime's grace windows and clock. TS hard-codes both windows and tests drive fake
/// timers; the port injects them so the same cases run deterministically.
/// </summary>
public sealed record ServerLifetimeOptions
{
    /// <summary>How long an automatically activated server waits for its first client. TS 10s.</summary>
    public int StartupGraceMs { get; init; } = ExperimentalServer.AutoServerStartupGraceMs;

    /// <summary>How long demand must stay at zero before retiring. TS 1s.</summary>
    public int IdleGraceMs { get; init; } = ExperimentalServer.AutoServerIdleGraceMs;

    /// <summary>The timer source; defaults to real delays.</summary>
    public IServerLifetimeClock? Clock { get; init; }
}

// ---------------------------------------------------------------------------------------------
// ServerLifetime
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Reconcile operator, startup, client, and worker holds for one server generation. TS
/// <c>ServerLifetime</c>.
/// </summary>
/// <remarks>
/// A keep-alive (operator-held) server never retires. An automatically activated one first holds
/// a startup grace: if no client connects within it, the server retires immediately. After that,
/// retirement requires both the connection and the worker count to stay at zero for the idle
/// grace — and the conditions are re-checked when the idle timer fires, so a demand that
/// reappears inside the grace window cancels it.
/// </remarks>
public sealed class ServerLifetime
{
    private readonly bool _keepAlive;
    private readonly int _startupGraceMs;
    private readonly int _idleGraceMs;
    private readonly IServerLifetimeClock _clock;
    private readonly Lock _gate = new();

    private int _connectionCount;
    private int _workerCount;
    private bool _startupHeld;
    private IServerLifetimeTimer? _startupTimer;
    private IServerLifetimeTimer? _retirementTimer;
    private Action? _retire;
    private bool _stopped;

    public ServerLifetime(bool keepAlive, ServerLifetimeOptions? options = null)
    {
        _keepAlive = keepAlive;
        var defaults = new ServerLifetimeOptions();
        _startupGraceMs = options?.StartupGraceMs ?? defaults.StartupGraceMs;
        _idleGraceMs = options?.IdleGraceMs ?? defaults.IdleGraceMs;
        _clock = options?.Clock ?? SystemServerLifetimeClock.Instance;
        _startupHeld = !keepAlive;
    }

    /// <summary>Arm the lifetime. TS <c>start(retire)</c>.</summary>
    public void Start(Action retire)
    {
        lock (_gate)
        {
            _retire = retire;
            if (_startupHeld)
            {
                _startupTimer = _clock.StartTimer(_startupGraceMs, () =>
                {
                    lock (_gate)
                    {
                        _startupTimer = null;
                        _startupHeld = false;
                        Reconcile();
                    }
                });
            }

            Reconcile();
        }
    }

    /// <summary>Publish the connection count. TS <c>setConnectionCount</c>.</summary>
    public void SetConnectionCount(int count)
    {
        lock (_gate)
        {
            _connectionCount = count;
            if (count > 0 && _startupHeld)
            {
                // 首个客户端提前到达：启动宽限不再需要。
                _startupHeld = false;
                _startupTimer?.Cancel();
                _startupTimer = null;
            }

            Reconcile();
        }
    }

    /// <summary>Publish the worker count. TS <c>setWorkerCount</c>.</summary>
    public void SetWorkerCount(int count)
    {
        lock (_gate)
        {
            _workerCount = count;
            Reconcile();
        }
    }

    /// <summary>Disarm the lifetime; no retirement can follow. TS <c>stop</c>.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _startupTimer?.Cancel();
            _retirementTimer?.Cancel();
            _startupTimer = null;
            _retirementTimer = null;
        }
    }

    private void Reconcile()
    {
        // 调用方持有 _gate。
        if (_stopped || _keepAlive || _startupHeld || _connectionCount != 0 || _workerCount != 0)
        {
            _retirementTimer?.Cancel();
            _retirementTimer = null;
            return;
        }

        if (_retirementTimer is not null || _retire is null) return;
        _retirementTimer = _clock.StartTimer(_idleGraceMs, () =>
        {
            lock (_gate)
            {
                _retirementTimer = null;
                if (!_stopped && !_startupHeld && _connectionCount == 0 && _workerCount == 0)
                {
                    _retire?.Invoke();
                }
            }
        });
    }
}

/// <summary>
/// Production clock. TS's <c>unref</c>'d timers have no .NET equivalent: a pending delay never
/// keeps a process alive here either.
/// </summary>
internal sealed class SystemServerLifetimeClock : IServerLifetimeClock
{
    public static readonly SystemServerLifetimeClock Instance = new();

    private SystemServerLifetimeClock()
    {
    }

    public IServerLifetimeTimer StartTimer(int milliseconds, Action fire)
        => new SystemTimer(milliseconds, fire);

    private sealed class SystemTimer : IServerLifetimeTimer
    {
        private readonly CancellationTokenSource _cts = new();

        public SystemTimer(int milliseconds, Action fire)
        {
            _ = Task.Delay(milliseconds, _cts.Token).ContinueWith(
                static (task, state) =>
                {
                    var (callback, _) = ((Action, SystemTimer))state!;
                    if (task.IsCanceled) return;
                    callback();
                },
                (fire, this),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }
        }
    }
}

/// <summary>TS <c>RunningServer</c>. Not sealed: the backend extends it with its refresh.</summary>
public class RunningServer
{
    public required string ServerId { get; init; }

    public required string SessionDir { get; init; }

    /// <summary>The stable public socket path the coordinator proxies.</summary>
    public required string SocketPath { get; init; }

    public required Server<SessionCatalogMetadata> Server { get; init; }

    /// <summary>The pids of the running workers, keyed by session id. Live, like TS's map.</summary>
    public required IReadOnlyDictionary<string, long> WorkerPids { get; init; }

    /// <summary>Completes once the server has shut down. TS <c>closed</c>.</summary>
    public required Task Closed { get; init; }

    public required Func<Task> Close { get; init; }
}

/// <summary>TS <c>StartServerOptions</c>.</summary>
public sealed record StartServerOptions
{
    /// <summary>Server profile and socket directory. Defaults to PI_SERVER_DIR or ~/.pi/server.</summary>
    public string? Directory { get; init; }

    /// <summary>Logical service ID. Defaults to PI_SERVER_ID or the directory's default-server-id.</summary>
    public string? ServerId { get; init; }

    /// <summary>Durable session directory. Defaults to the experimental directory under the agent directory.</summary>
    public string? SessionDir { get; init; }

    /// <summary>Optional provider for an explicitly selected Session worker model.</summary>
    public string? Provider { get; init; }

    /// <summary>Optional model override for newly started Session workers.</summary>
    public string? Model { get; init; }

    /// <summary>Hold the server open without client or Session demand. Defaults to true.</summary>
    public bool? KeepAlive { get; init; }

    /// <summary>Optional explicit Radius credential. Stored Radius auth is used when omitted.</summary>
    public AuthInput? RelayAuth { get; init; }

    /// <summary>Explicit plugin packages. Null restores the profile; an empty list clears it.</summary>
    public IReadOnlyList<string>? PluginPackages { get; init; }

    /// <summary>
    /// The entry bundler plugin packages are built with (C334: .NET has no esbuild). Required
    /// only when plugin packages are actually configured.
    /// </summary>
    public IFacetEntryBundler? Bundler { get; init; }

    public Action<RadiusRelayHostStatus>? OnRelayStatus { get; init; }
}

/// <summary>TS <c>ActivatedServer</c>.</summary>
public sealed record ActivatedServer(ServerClient Client, UnixServerRoute Route);

/// <summary>TS <c>ActivateServerOptions</c>.</summary>
public sealed record ActivateServerOptions
{
    public required string Directory { get; init; }

    public string? RequestedServerId { get; init; }

    public required string SessionDir { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }
}

/// <summary>
/// The experimental server runtime. Port of <c>experimental/server.ts</c>: directory identity and
/// locking, the retirement policy, the backend, and the four entry points.
/// </summary>
public static class ExperimentalServer
{
    /// <summary>TS <c>ENV_SERVER_DIR</c>.</summary>
    public const string EnvServerDir = "PI_SERVER_DIR";

    /// <summary>TS <c>ENV_SERVER_ID</c>.</summary>
    public const string EnvServerId = "PI_SERVER_ID";

    private const int LockStaleMs = 30_000;
    private const int LockRetryMs = 25;
    private const int LockWaitMs = 30_000;
    private const string DefaultServerIdFile = "default-server-id";

    private const int ActivationTimeoutMs = 10_000;
    private const int ActivationRetryMs = 10;

    internal const int AutoServerStartupGraceMs = 10_000;
    internal const int AutoServerIdleGraceMs = 1_000;

    /// <summary>TS's socket file mode <c>0o600</c>: owner read/write only.</summary>
    private const int SocketFileMode = 0b110_000_000;

    // ---------------------------------------------------------------------------------------------
    // Directory identity
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The server directory: an explicit one, then <c>PI_SERVER_DIR</c>, then <c>~/.pi/server</c>.
    /// TS <c>resolveServerDirectory</c>.
    /// </summary>
    public static string ResolveServerDirectory(string? directory = null)
    {
        var value = directory ?? Environment.GetEnvironmentVariable(EnvServerDir)
            ?? NodePath.Join(Paths.GetHomeDirectory(), ".pi", "server");
        return Paths.ResolvePath(value);
    }

    /// <summary>
    /// Create the server directory as a private, current-user-owned directory. TS
    /// <c>ensurePrivateServerDirectory</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On POSIX the directory is created 0700 and must be a directory owned by the current user;
    /// the mode is re-applied afterwards so a pre-existing directory is tightened as well.
    /// </para>
    /// <para>
    /// C369: TS throws "Unix socket directory requires a POSIX user ID" on Windows, because Node's
    /// <c>process.getuid</c> does not exist there. The port keeps the server usable on Windows the
    /// way the Unix listener and transport already do — the directory is still created and
    /// validated, only the ownership and mode checks are skipped.
    /// </para>
    /// </remarks>
    public static async Task EnsurePrivateServerDirectoryAsync(string directory)
    {
        ModeFile.CreateDirectory(directory, ModeFile.OwnerAll);
        if (OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(directory))
            {
                throw new InvalidOperationException($"Unix socket directory is not a directory: {directory}");
            }

            await Task.CompletedTask.ConfigureAwait(false);
            return;
        }

        var stats = PosixStat.Lstat(directory);
        if (stats is not { IsDirectory: true })
        {
            throw new InvalidOperationException($"Unix socket directory is not a directory: {directory}");
        }

        if (stats.Value.Uid != PosixStat.CurrentUid)
        {
            throw new InvalidOperationException(
                $"Unix socket directory is not owned by the current user: {directory}");
        }

        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// The durable Session directory: an explicit one, then the experimental directory under the
    /// configured agent directory. TS <c>resolveSessionDirectory</c>.
    /// </summary>
    public static string ResolveSessionDirectory(string? sessionDir = null)
        => Paths.ResolvePath(sessionDir ?? NodePath.Join(Config.GetAgentDir(), "experimental", "sessions"));

    // ---------------------------------------------------------------------------------------------
    // Server profile and activation locks
    // ---------------------------------------------------------------------------------------------

    /// <summary>TS <c>ServerProfile</c>: one logical server id plus its launcher-lock release.</summary>
    public sealed record ServerProfile(string ServerId, Func<Task> Release);

    /// <summary>
    /// Lock one logical server ID in a shared experimental server directory. TS
    /// <c>acquireServerProfile</c>.
    /// </summary>
    /// <remarks>
    /// An explicit ID is validated and used as-is. Without one, the directory's
    /// <c>default-server-id</c> is read; a missing file is minted create-only (the race loser reads
    /// the winner's value), and a corrupt file fails loudly rather than silently re-minting.
    /// </remarks>
    public static async Task<ServerProfile> AcquireServerProfileAsync(
        string directory, string? requestedServerId = null)
    {
        ModeFile.CreateDirectory(directory, ModeFile.OwnerAll);
        string serverId;
        if (requestedServerId is not null)
        {
            if (!ServerIds.IsServerId(requestedServerId))
            {
                throw new InvalidOperationException($"Invalid experimental server ID: {requestedServerId}");
            }

            serverId = requestedServerId;
        }
        else
        {
            var path = NodePath.Join(directory, DefaultServerIdFile);
            string? stored = null;
            try
            {
                stored = (await File.ReadAllTextAsync(path).ConfigureAwait(false)).Trim();
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // TS: ENOENT → mint a new default identity below. Any other read error propagates.
            }

            if (stored is not null)
            {
                if (!ServerIds.IsServerId(stored))
                {
                    throw new InvalidOperationException($"Invalid default experimental server identity in {path}");
                }

                serverId = stored;
            }
            else
            {
                // TS: randomUUID() — a canonical lowercase UUIDv4, which is what isServerId accepts.
                var candidate = Guid.NewGuid().ToString();
                try
                {
                    // TS writeFile flag "wx": create-only. FileMode.CreateNew is the same race.
                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                    };
                    if (!OperatingSystem.IsWindows())
                    {
                        options.UnixCreateMode = (UnixFileMode)ModeFile.OwnerReadWrite;
                    }

                    await using var stream = new FileStream(path, options);
                    await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(candidate)).ConfigureAwait(false);
                    serverId = candidate;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // TS: EEXIST → another launcher won the mint; adopt its identity.
                    var existing = (await File.ReadAllTextAsync(path).ConfigureAwait(false)).Trim();
                    if (!ServerIds.IsServerId(existing))
                    {
                        throw new InvalidOperationException($"Invalid default experimental server identity in {path}");
                    }

                    serverId = existing;
                }
            }
        }

        var release = await NodeLock.AcquireAsync(
            NodePath.Join(directory, $"launcher-{serverId}"),
            CancellationToken.None,
            staleMs: LockStaleMs,
            maxWaitMs: LockWaitMs,
            fixedRetryMs: LockRetryMs).ConfigureAwait(false);
        return new ServerProfile(serverId, release);
    }

    /// <summary>
    /// Serialize cold activation of one logical server. TS <c>acquireServerActivation</c>: a longer
    /// stale window than the wait, because a crashed activator must not block the next one for long.
    /// </summary>
    public static Task<Func<Task>> AcquireServerActivationAsync(string directory, string serverId)
        => NodeLock.AcquireAsync(
            NodePath.Join(directory, $"activation-{serverId}"),
            CancellationToken.None,
            staleMs: ActivationTimeoutMs * 2,
            maxWaitMs: ActivationTimeoutMs,
            fixedRetryMs: 25);

    // ---------------------------------------------------------------------------------------------
    // Activation
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Ensure the selected logical server is reachable, launching the current Pi installation if
    /// needed. TS <c>activateServer</c>.
    /// </summary>
    /// <remarks>
    /// The activation lock is held across the whole spawn-and-wait, so a racing activator either
    /// finds the server already up (and returns it, refusing a model selection it can no longer
    /// apply) or becomes the one that spawns. A model selection is startup-only: it rides along the
    /// spawned process's arguments.
    /// </remarks>
    public static async Task<ActivatedServer> ActivateServerAsync(ActivateServerOptions options)
    {
        if (options.Provider is not null && options.Model is null)
        {
            throw new InvalidOperationException("Server model provider requires a model");
        }

        await EnsurePrivateServerDirectoryAsync(options.Directory).ConfigureAwait(false);
        var profile = await AcquireServerProfileAsync(options.Directory, options.RequestedServerId).ConfigureAwait(false);
        var serverId = profile.ServerId;
        await profile.Release().ConfigureAwait(false);
        var route = new UnixServerRoute(serverId, UnixSocketAddress.GetUnixSocketPath(serverId, options.Directory));
        var release = await AcquireServerActivationAsync(options.Directory, serverId).ConfigureAwait(false);
        try
        {
            var existing = await ConnectAsync(route).ConfigureAwait(false);
            if (existing is not null)
            {
                // Another activator won the race, so startup-only selections can no longer be applied.
                if (options.Model is not null)
                {
                    await existing.DisposeAsync().ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "Model selection is only valid when automatically activating a new server");
                }

                return new ActivatedServer(existing, route);
            }

            IReadOnlyList<string> modelArgs = options.Model is null
                ? []
                : [SerializeModelOptions(options.Provider, options.Model)];
            var child = InternalProcess.SpawnInternalProcess(
                InternalProcessRole.Server, [options.Directory, serverId, options.SessionDir, .. modelArgs]);
            try
            {
                var deadline = Environment.TickCount64 + ActivationTimeoutMs;
                while (true)
                {
                    var client = await ConnectAsync(route).ConfigureAwait(false);
                    if (client is not null) return new ActivatedServer(client, route);
                    // TS: the spawn's asynchronous "error" event; the port records it on the handle.
                    if (child.StartupError is not null)
                    {
                        throw new InvalidOperationException(
                            "Failed to automatically activate server", child.StartupError);
                    }

                    if (child.ExitCode is not null || child.SignalCode is not null)
                    {
                        throw new InvalidOperationException("Automatically activated server exited during startup");
                    }

                    if (Environment.TickCount64 >= deadline)
                    {
                        throw new InvalidOperationException("Timed out waiting for automatically activated server");
                    }

                    await Task.Delay(ActivationRetryMs).ConfigureAwait(false);
                }
            }
            catch
            {
                await InternalProcess.TerminateInternalProcessAsync(child).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            await release().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The serialized model options the spawned server process parses. TS inlines the same shape:
    /// <c>model</c> always, <c>provider</c> only when one was given.
    /// </summary>
    private static string SerializeModelOptions(string? provider, string model)
    {
        var payload = new JsonObject { ["model"] = model };
        if (provider is not null) payload["provider"] = provider;
        return payload.ToJsonString();
    }

    /// <summary>
    /// Connect to one Unix server route, or null when nothing answers there. TS's private
    /// <c>connect</c>.
    /// </summary>
    /// <remarks>
    /// A missing, refused, or reset socket — and a stale server that rejects the protocol version —
    /// all mean "not (yet) reachable" so activation can spawn or retry. Anything else propagates:
    /// TS walks the error's cause chain for the Node connection codes and throws otherwise.
    /// </remarks>
    private static async Task<ServerClient?> ConnectAsync(UnixServerRoute route)
    {
        var client = new ServerClient(new ClientOptions
        {
            ServerId = route.ServerId,
            TransportFactory = UnixTransport.CreateUnixTransportFactory(new UnixTransportOptions { Path = route.Path }),
        });
        try
        {
            await client.ConnectAsync().ConfigureAwait(false);
            return client;
        }
        catch (Exception error)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            if (error is DisconnectedError || (error is Pi.Client.ServerError serverError && serverError.Code == "version"))
            {
                return null;
            }

            if (HasConnectionFailure(error)) return null;
            throw;
        }
    }

    private static readonly string[] AbsentSocketCodes =
        ["ENOENT", "ECONNREFUSED", "ECONNRESET", "EPIPE", "ETIMEDOUT"];

    private static bool HasConnectionFailure(Exception error)
    {
        var seen = new HashSet<Exception>();
        var current = error;
        while (current is not null && seen.Add(current))
        {
            if (current is NodeIoException io && io.Code is string code && AbsentSocketCodes.Contains(code))
            {
                return true;
            }

            if (current is SocketException) return true;
            current = current.InnerException;
        }

        return false;
    }

    // ---------------------------------------------------------------------------------------------
    // Backend
    // ---------------------------------------------------------------------------------------------

    /// <summary>TS's <c>ResolvedSessionPlugins</c>: one resolved plugin selection.</summary>
    private sealed record ResolvedSessionPlugins(
        IReadOnlyList<string> PackagePaths,
        IReadOnlyList<string> ManifestPaths,
        IReadOnlyList<FacetBundleArtifact> PresentationArtifacts);

    /// <summary>TS's <c>StartServerBackendOptions</c>.</summary>
    private sealed record StartServerBackendOptions
    {
        public required string Path { get; init; }

        public required string ServerId { get; init; }

        public string? SessionDir { get; init; }

        public required Func<SessionCatalogMetadata, IReadOnlyList<string>?, Context, Task<ResolvedSessionPlugins>>
            ResolveSessionPlugins { get; init; }

        public required Func<SessionCatalogMetadata, Task> RemoveSessionPlugins { get; init; }

        public required Func<IReadOnlyList<string>, Task<IReadOnlyList<FacetBundleArtifact>>>
            ReloadPresentationFacetBundles { get; init; }
    }

    /// <summary>TS's <c>RunningServerBackend</c>: a running server plus its session refresh.</summary>
    private sealed class RunningServerBackend : RunningServer
    {
        public required Func<Task> RefreshSessions { get; init; }
    }

    /// <summary>
    /// Host the session services behind one per-generation Unix socket. TS
    /// <c>startServerBackend</c>.
    /// </summary>
    /// <remarks>
    /// The session list merges the durable catalog with the workers' tracked sessions (a tracked
    /// entry wins), and the exposed list sorts by session id then creation time — the merge order
    /// itself is not observable. A startup failure closes the listener and the services, and
    /// reports a single error (or an aggregate of the startup and cleanup failures).
    /// </remarks>
    private static async Task<RunningServerBackend> StartServerBackendAsync(
        StartServerBackendOptions options,
        SessionWorkerManager workers,
        Action<int>? onConnectionCountChanged = null)
    {
        var serverId = options.ServerId;
        var sessionDir = ResolveSessionDirectory(options.SessionDir);

        async Task<IReadOnlyList<SessionCatalogMetadata>> ListSessionsAsync()
        {
            var sessions = new Dictionary<string, SessionCatalogMetadata>(StringComparer.Ordinal);
            foreach (var metadata in await SessionCatalog.ListSessionsAsync(sessionDir).ConfigureAwait(false))
            {
                sessions[metadata.Path] = metadata;
            }

            foreach (var metadata in workers.TrackedSessions)
            {
                sessions[metadata.Path] = metadata;
            }

            return [.. sessions.Values];
        }

        async Task<SessionCatalogMetadata> ResolveSessionAsync(string sessionId, Context context)
        {
            var metadata = workers.TrackedSessions.FirstOrDefault(candidate => candidate.Id == sessionId)
                ?? await SessionCatalog.ReadSessionAsync(sessionDir, sessionId).ConfigureAwait(false);
            if (metadata is null) throw new SessionNotFoundError($"Unknown session: {sessionId}");
            return metadata;
        }

        Task<SessionCatalogMetadata> CreateSessionAsync(SessionCreateOptions createOptions)
            => SessionCatalog.CreateSessionAsync(sessionDir, new SessionCatalogCreateOptions
            {
                Id = createOptions.Id,
                // TS: cwd: process.cwd().
                Cwd = Environment.CurrentDirectory,
            });

        static SessionSummary Summarize(string serverId, SessionCatalogMetadata metadata)
            => new(serverId, metadata.Id, metadata.CreatedAt);

        var serverServices = await ExperimentalServerProvider.CreateExperimentalServerServicesAsync(
            new ExperimentalServerOptions
            {
                ListAsync = async context =>
                {
                    var sessions = await ListSessionsAsync().ConfigureAwait(false);
                    return sessions
                        .Select(metadata => Summarize(serverId, metadata))
                        // TS: sessionId.localeCompare → CurrentCulture (C79); createdAt ascending.
                        .OrderBy(summary => summary.SessionId, StringComparer.CurrentCulture)
                        .ThenBy(summary => summary.CreatedAt)
                        .ToList();
                },
                CreateAsync = async (createOptions, context) =>
                    Summarize(serverId, await CreateSessionAsync(createOptions).ConfigureAwait(false)),
                RemoveAsync = async (sessionId, context) =>
                {
                    var metadata = await ResolveSessionAsync(sessionId, context).ConfigureAwait(false);
                    await workers.CloseSessionAsync(metadata, context).ConfigureAwait(false);
                    await SessionCatalog.DeleteSessionAsync(metadata).ConfigureAwait(false);
                    await options.RemoveSessionPlugins(metadata).ConfigureAwait(false);
                },
                PrepareSessionPluginsAsync = async (sessionId, packagePaths, context) =>
                {
                    var metadata = await ResolveSessionAsync(sessionId, context).ConfigureAwait(false);
                    ResolvedSessionPlugins selected;
                    try
                    {
                        selected = await options.ResolveSessionPlugins(metadata, packagePaths, context)
                            .ConfigureAwait(false);
                        workers.AssertSessionPluginManifestPaths(metadata, selected.ManifestPaths);
                    }
                    catch (SessionPluginSelectionConflictError error)
                    {
                        // TS: RoutedServerError("service_invalid_value", …) — chord's RemoteServiceError.
                        throw new RemoteServiceError("service_invalid_value", error.Message);
                    }

                    return new PrepareSessionAnswer(
                        selected.PackagePaths,
                        PluginBundles.CreatePresentationFacetData(selected.PresentationArtifacts));
                },
                ReloadPresentationPluginsAsync = async (packagePaths, context) =>
                    PluginBundles.CreatePresentationFacetData(
                        await options.ReloadPresentationFacetBundles(packagePaths).ConfigureAwait(false)),
            }).ConfigureAwait(false);

        var host = new SessionServerHost(
            serverServices.Host,
            ResolveSessionAsync,
            async (metadata, context) =>
            {
                var selected = await options.ResolveSessionPlugins(metadata, null, context).ConfigureAwait(false);
                return await workers.OpenSessionAsync(metadata, context, selected.ManifestPaths).ConfigureAwait(false);
            });

        var socketPath = options.Path;
        Task CloseCatalogAsync() => serverServices.DisposeAsync();
        var server = UnixServer.Create(host, new UnixServerOptions
        {
            ServerId = serverId,
            Path = socketPath,
            Mode = SocketFileMode,
            OnConnectionCountChanged = onConnectionCountChanged,
        });
        try
        {
            await server.StartAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var cleanupErrors = await AllSettledAsync(
            [
                CleanupAsync(server.CloseAsync()),
                CleanupAsync(CloseCatalogAsync()),
            ]).ConfigureAwait(false);
            if (cleanupErrors.Count > 0)
            {
                var failures = new List<Exception> { error };
                failures.AddRange(cleanupErrors);
                throw new AggregateException("Experimental server startup and cleanup failed", failures);
            }

            throw;
        }

        Task? closePromise = null;
        var closed = CloseTailAsync();
        async Task CloseTailAsync()
        {
            try
            {
                await server.Closed.ConfigureAwait(false);
            }
            catch (Exception serverError)
            {
                try
                {
                    await CloseCatalogAsync().ConfigureAwait(false);
                }
                catch (Exception catalogError)
                {
                    throw new AggregateException(
                        "Server and repository shutdown failed", serverError, catalogError);
                }

                throw;
            }

            await CloseCatalogAsync().ConfigureAwait(false);
        }

        return new RunningServerBackend
        {
            ServerId = serverId,
            SessionDir = sessionDir,
            SocketPath = socketPath,
            Server = server,
            WorkerPids = workers.WorkerPids,
            Closed = closed,
            Close = CloseAsync,
            RefreshSessions = () => serverServices.RefreshAsync(Context.Background),
        };

        async Task CloseAsync()
        {
            closePromise ??= CloseInnerAsync();
            await closePromise.ConfigureAwait(false);
        }

        async Task CloseInnerAsync()
        {
            try
            {
                // TS: server.close().then(() => closed, () => closed) — the tail is awaited either way.
                await server.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // The startup cleanup above already reports a close failure; the tail re-raises it.
            }

            await closed.ConfigureAwait(false);
        }
    }

    /// <summary>TS's <c>ServerHost</c>: the services plus the two session entry points.</summary>
    private sealed class SessionServerHost(
        IRoutedServerServiceHost serverServices,
        Func<string, Context, Task<SessionCatalogMetadata>> resolveSession,
        Func<SessionCatalogMetadata, Context, Task<IRoutedSessionHandle>> openSession)
        : IServerHost<SessionCatalogMetadata>
    {
        public IRoutedServerServiceHost ServerServices => serverServices;

        public Task<SessionCatalogMetadata> ResolveSessionAsync(string sessionId, Context context)
            => resolveSession(sessionId, context);

        public Task<IRoutedSessionHandle> OpenSessionAsync(SessionCatalogMetadata metadata, Context context)
            => openSession(metadata, context);
    }

    // ---------------------------------------------------------------------------------------------
    // Running server
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Start a replaceable experimental server behind the stable coordinator endpoint. TS
    /// <c>startServer</c>.
    /// </summary>
    /// <remarks>
    /// The profile lock is taken first and released once the server is up (it only serializes
    /// identity selection). The startup lease covers the window before this generation registers
    /// with the coordinator; a replacement tears this generation down through the coordinator's
    /// <c>Replaced</c> signal — detaching workers rather than shutting them down, since the next
    /// generation adopts them. Every failure path stops the lifetime, releases the lease and the
    /// profile lock, and unwinds in the same order TS does.
    /// </remarks>
    public static async Task<RunningServer> StartServerAsync(StartServerOptions options)
    {
        if (options.Provider is not null && options.Model is null)
        {
            throw new InvalidOperationException("Server model provider requires a model");
        }

        (string? Provider, string Model)? workerModel = options.Model is null
            ? null
            : (options.Provider, options.Model);
        var directory = ResolveServerDirectory(options.Directory);
        var profile = await AcquireServerProfileAsync(
            directory, options.ServerId ?? Environment.GetEnvironmentVariable(EnvServerId)).ConfigureAwait(false);
        var lifetime = new ServerLifetime(options.KeepAlive ?? true);
        RunningServerBackend? backend = null;
        CoordinatorConnection? coordinator = null;
        CoordinatorStartupLease? startupLease = null;
        SessionWorkerManager? workers = null;
        RadiusRelayHost? relay = null;
        var released = false;
        try
        {
            await EnsurePrivateServerDirectoryAsync(directory).ConfigureAwait(false);
            var pluginPackagePaths = await PluginPackages.RestoreServerPluginPackageProfileAsync(
                directory, profile.ServerId, options.PluginPackages).ConfigureAwait(false);
            var pluginPackages = new Dictionary<string, ConfiguredServerPluginPackage>(StringComparer.Ordinal);
            ConfiguredServerPluginPackage GetPluginPackage(string packagePath)
            {
                if (!pluginPackages.TryGetValue(packagePath, out var plugin))
                {
                    if (options.Bundler is null)
                    {
                        // C334: TS bundles with esbuild; the port needs the host to inject one.
                        throw new InvalidOperationException(
                            $"Plugin package {packagePath} requires an entry bundler");
                    }

                    plugin = PluginPackages.CreateServerPluginPackage(
                        directory, profile.ServerId, packagePath, options.Bundler);
                    pluginPackages[packagePath] = plugin;
                }

                return plugin;
            }

            async Task<ResolvedSessionPlugins> BuildPluginSelectionAsync(IReadOnlyList<string> packagePaths)
            {
                var normalizedPackagePaths = PluginPackages.NormalizePluginPackagePaths(packagePaths);
                var plugins = normalizedPackagePaths.Select(GetPluginPackage).ToList();
                var built = await Task.WhenAll(plugins.Select(plugin => plugin.Build())).ConfigureAwait(false);
                return new ResolvedSessionPlugins(
                    normalizedPackagePaths,
                    plugins.Select(plugin => plugin.ManifestPath).ToList(),
                    built.SelectMany(artifacts => artifacts).ToList());
            }

            var defaultPluginSelection = await BuildPluginSelectionAsync(pluginPackagePaths).ConfigureAwait(false);
            var sessionPluginSelections = new Dictionary<string, ResolvedSessionPlugins>(StringComparer.Ordinal);
            async Task<ResolvedSessionPlugins> ResolveSessionPluginsAsync(
                SessionCatalogMetadata metadata, IReadOnlyList<string>? requestedPackagePaths, Context context)
            {
                if (requestedPackagePaths is not null)
                {
                    var normalizedPackagePaths = PluginPackages.NormalizePluginPackagePaths(requestedPackagePaths);
                    if (sessionPluginSelections.TryGetValue(metadata.Path, out var current)
                        && SameStrings(current.PackagePaths, normalizedPackagePaths))
                    {
                        return current;
                    }

                    var requestedManifestPaths = normalizedPackagePaths
                        .Select(packagePath => GetPluginPackage(packagePath).ManifestPath).ToList();
                    workers?.AssertSessionPluginManifestPaths(metadata, requestedManifestPaths);
                    var candidate = await BuildPluginSelectionAsync(normalizedPackagePaths).ConfigureAwait(false);
                    workers?.AssertSessionPluginManifestPaths(metadata, candidate.ManifestPaths);
                    await PluginPackages.WriteSessionPluginPackageProfileAsync(
                        directory, profile.ServerId, metadata.Path, candidate.PackagePaths).ConfigureAwait(false);
                    sessionPluginSelections[metadata.Path] = candidate;
                    return candidate;
                }

                if (sessionPluginSelections.TryGetValue(metadata.Path, out var cached)) return cached;
                var storedPackagePaths = await PluginPackages.ReadSessionPluginPackageProfileAsync(
                    directory, profile.ServerId, metadata.Path).ConfigureAwait(false);
                var selected = storedPackagePaths is null
                    ? defaultPluginSelection
                    : await BuildPluginSelectionAsync(storedPackagePaths).ConfigureAwait(false);
                if (storedPackagePaths is null)
                {
                    await PluginPackages.WriteSessionPluginPackageProfileAsync(
                        directory, profile.ServerId, metadata.Path, selected.PackagePaths).ConfigureAwait(false);
                }

                sessionPluginSelections[metadata.Path] = selected;
                return selected;
            }

            async Task RemoveSessionPluginsAsync(SessionCatalogMetadata metadata)
            {
                sessionPluginSelections.Remove(metadata.Path);
                await PluginPackages.RemoveSessionPluginPackageProfileAsync(
                    directory, profile.ServerId, metadata.Path).ConfigureAwait(false);
            }

            async Task<IReadOnlyList<FacetBundleArtifact>> ReloadPresentationFacetBundlesAsync(
                IReadOnlyList<string> packagePaths)
            {
                var reloaded = await BuildPluginSelectionAsync(packagePaths).ConfigureAwait(false);
                if (SameStrings(defaultPluginSelection.PackagePaths, reloaded.PackagePaths))
                {
                    defaultPluginSelection = reloaded;
                }

                // TS mutates the map while iterating it (legal there); the port iterates a snapshot.
                foreach (var (sessionPath, selected) in sessionPluginSelections.ToList())
                {
                    if (SameStrings(selected.PackagePaths, reloaded.PackagePaths))
                    {
                        sessionPluginSelections[sessionPath] = reloaded;
                    }
                }

                return reloaded.PresentationArtifacts;
            }

            var socketPath = UnixSocketAddress.GetUnixSocketPath(profile.ServerId, directory);
            var controlPath = NodePath.Join(directory, $"control-{profile.ServerId}.sock");
            // TS: randomUUID().replaceAll("-", "").slice(0, 12) — a 12-hex-char generation nonce.
            var serverNonce = Guid.NewGuid().ToString("N")[..12];
            var serverPath = NodePath.Join(directory, $"server-{profile.ServerId}-{serverNonce}.sock");
            startupLease = await Coordinator.EnsureCoordinatorAsync(socketPath, controlPath).ConfigureAwait(false);
            coordinator = new CoordinatorConnection(new CoordinatorConnectionOptions(controlPath, serverPath));
            var sessionDir = ResolveSessionDirectory(options.SessionDir);
            workers = new SessionWorkerManager(
                coordinator, sessionDir, workerModel, count => lifetime.SetWorkerCount(count));
            backend = await StartServerBackendAsync(
                new StartServerBackendOptions
                {
                    Path = serverPath,
                    ServerId = profile.ServerId,
                    SessionDir = options.SessionDir,
                    ResolveSessionPlugins = ResolveSessionPluginsAsync,
                    RemoveSessionPlugins = RemoveSessionPluginsAsync,
                    ReloadPresentationFacetBundles = ReloadPresentationFacetBundlesAsync,
                },
                workers,
                count => lifetime.SetConnectionCount(count)).ConfigureAwait(false);
            await coordinator.ConnectAsync().ConfigureAwait(false);
            startupLease.Close();
            startupLease = null;
            await workers.DiscoverAsync(coordinator.PeerIds).ConfigureAwait(false);
            await backend.RefreshSessions().ConfigureAwait(false);
            relay = new RadiusRelayHost(new RadiusRelayHostOptions
            {
                ServerId = profile.ServerId,
                // TS passes the Server itself (Pick<Server, "accept">); the port takes the delegate.
                Accept = connection => backend.Server.Accept(connection),
                Auth = new RadiusRelayAuthResolver(options.RelayAuth),
                OnStatus = options.OnRelayStatus,
            });
            relay.Start();

            var activeBackend = backend;
            var activeCoordinator = coordinator;
            var activeWorkers = workers;
            var activeRelay = relay;
            _ = WatchReplacementAsync();
            async Task WatchReplacementAsync()
            {
                try
                {
                    await activeCoordinator.Replaced.ConfigureAwait(false);
                    lifetime.Stop();
                    // 被替换：worker 由新世代收养，不得在这里关闭。
                    activeWorkers.Detach();
                    await activeRelay.CloseAsync().ConfigureAwait(false);
                    await activeBackend.Close().ConfigureAwait(false);
                }
                catch
                {
                    // TS: .catch(() => {}) — 替换拆卸失败无处上报。
                }
                finally
                {
                    activeCoordinator.Close();
                }
            }

            Task? closePromise = null;
            var runtime = new RunningServer
            {
                ServerId = profile.ServerId,
                SessionDir = activeBackend.SessionDir,
                SocketPath = socketPath,
                Server = activeBackend.Server,
                WorkerPids = activeWorkers.WorkerPids,
                Closed = ClosedTailAsync(),
                Close = CloseRuntimeAsync,
            };

            async Task ClosedTailAsync()
            {
                try
                {
                    await activeBackend.Closed.ConfigureAwait(false);
                }
                finally
                {
                    await activeRelay.CloseAsync().ConfigureAwait(false);
                }
            }

            async Task CloseRuntimeAsync()
            {
                lifetime.Stop();
                closePromise ??= CloseRuntimeInnerAsync();
                await closePromise.ConfigureAwait(false);
            }

            async Task CloseRuntimeInnerAsync()
            {
                try
                {
                    await activeRelay.CloseAsync().ConfigureAwait(false);
                    await activeBackend.Close().ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        if (activeCoordinator.WasReplaced) activeWorkers.Detach();
                        else await activeWorkers.ShutdownAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        activeCoordinator.Close();
                    }
                }
            }

            lifetime.Start(() => FireAndForget(CloseRuntimeAsync()));
            released = true;
            await profile.Release().ConfigureAwait(false);
            return runtime;
        }
        catch (Exception error)
        {
            lifetime.Stop();
            startupLease?.Close();
            if (coordinator?.WasReplaced == true) workers?.Detach();
            var cleanup = await AllSettledAsync(
            [
                relay?.CloseAsync() ?? Task.CompletedTask,
                backend?.Close() ?? Task.CompletedTask,
                coordinator?.WasReplaced == true ? Task.CompletedTask : workers?.ShutdownAsync() ?? Task.CompletedTask,
                coordinator is null ? Task.CompletedTask : Task.Run(() => coordinator.Close()),
                released ? Task.CompletedTask : profile.Release(),
            ]).ConfigureAwait(false);
            if (cleanup.Count > 0)
            {
                var failures = new List<Exception> { error };
                failures.AddRange(cleanup);
                throw new AggregateException("Server runtime startup and cleanup failed", failures);
            }

            throw;
        }
    }

    /// <summary>
    /// Start an operator-held server while serializing against automatic cold activation. TS
    /// <c>startForegroundServer</c>: the activation lock is held for the server's whole lifetime, so
    /// a cold activator waits instead of racing a second generation into the same directory.
    /// </summary>
    public static async Task<RunningServer> StartForegroundServerAsync(StartServerOptions options)
    {
        var directory = ResolveServerDirectory(options.Directory);
        await EnsurePrivateServerDirectoryAsync(directory).ConfigureAwait(false);
        var profile = await AcquireServerProfileAsync(
            directory, options.ServerId ?? Environment.GetEnvironmentVariable(EnvServerId)).ConfigureAwait(false);
        // The profile lock only serializes identity selection; it is released before the activation
        // lock is taken, exactly as TS does (startServer re-acquires it for its own startup).
        await profile.Release().ConfigureAwait(false);
        var release = await AcquireServerActivationAsync(directory, profile.ServerId).ConfigureAwait(false);
        try
        {
            return await StartServerAsync(options with
            {
                Directory = directory,
                ServerId = profile.ServerId,
                KeepAlive = true,
            }).ConfigureAwait(false);
        }
        finally
        {
            await release().ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Internal process entry
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Parse the serialized model options an activated server receives. TS
    /// <c>parseServerModelOptions</c>: exactly <c>provider</c> and <c>model</c>, model a non-empty
    /// string, provider — when present — a non-empty string.
    /// </summary>
    public static (string? Provider, string Model)? ParseServerModelOptions(string? value)
    {
        if (value is null) return null;
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(value);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("Internal server received invalid model options", error);
        }

        if (parsed is not JsonObject payload)
        {
            throw new InvalidOperationException("Internal server received invalid model options");
        }

        var model = payload.TryGetPropertyValue("model", out var modelNode) ? modelNode : null;
        var provider = payload.TryGetPropertyValue("provider", out var providerNode) ? providerNode : null;
        var invalid = payload.Any(entry => entry.Key != "provider" && entry.Key != "model")
            || !IsNonEmptyString(model)
            || (provider is not null && !IsNonEmptyString(provider));
        if (invalid)
        {
            throw new InvalidOperationException("Internal server received invalid model options");
        }

        var modelText = Text(model)!;
        return provider is null ? (null, modelText) : (Text(provider)!, modelText);

        static bool IsNonEmptyString(JsonNode? node)
            => node is JsonValue value && value.GetValueKind() == JsonValueKind.String
                && value.GetValue<string>().Length > 0;

        static string? Text(JsonNode? node)
            => node is JsonValue value && value.GetValueKind() == JsonValueKind.String
                ? value.GetValue<string>()
                : null;
    }

    /// <summary>
    /// The server role's process entry: TS server.ts's bottom block consumes the role, requires it
    /// to be the server one, and runs <see cref="RunServerProcessAsync"/>.
    /// </summary>
    public static async Task RunServerEntryAsync(IReadOnlyList<string> args)
    {
        var role = InternalProcess.ConsumeInternalProcessRole();
        if (role != InternalProcessRole.Server)
        {
            throw new InvalidOperationException("Server entrypoint requires an internal server invocation");
        }

        await RunServerProcessAsync(args).ConfigureAwait(false);
    }

    /// <summary>
    /// Run an automatically activated server until its client and Session demand disappears. TS
    /// <c>runServerProcess</c>.
    /// </summary>
    /// <remarks>
    /// SIGINT/SIGTERM close the runtime instead of killing the process outright, so the shutdown
    /// path (workers, coordinator, relay) runs; the signal handlers are removed once it has. The
    /// exit code is left to the caller (C360).
    /// </remarks>
    public static async Task RunServerProcessAsync(IReadOnlyList<string> args)
    {
        if (args.Count > 4) throw new InvalidOperationException("Internal server received unexpected arguments");
        var directory = args.Count > 0 ? args[0] : null;
        var serverId = args.Count > 1 ? args[1] : null;
        var sessionDir = args.Count > 2 ? args[2] : null;
        var serializedModel = args.Count > 3 ? args[3] : null;
        if (string.IsNullOrEmpty(directory) || !NodePath.IsAbsolute(directory))
        {
            throw new InvalidOperationException("Internal server requires an absolute server directory");
        }

        if (serverId is null || !ServerIds.IsServerId(serverId))
        {
            throw new InvalidOperationException("Internal server requires a canonical server ID");
        }

        if (string.IsNullOrEmpty(sessionDir) || !NodePath.IsAbsolute(sessionDir))
        {
            throw new InvalidOperationException("Internal server requires an absolute Session directory");
        }

        var workerModel = ParseServerModelOptions(serializedModel);
        var runtime = await StartServerAsync(new StartServerOptions
        {
            Directory = directory,
            ServerId = serverId,
            SessionDir = sessionDir,
            KeepAlive = false,
            Provider = workerModel?.Provider,
            Model = workerModel?.Model,
        }).ConfigureAwait(false);

        void Close()
        {
            // TS: void runtime.close().catch(() => {}).
            FireAndForget(runtime.Close());
        }

        Console.CancelKeyPress += OnCancelKeyPress;
        PosixSignalRegistration? sigterm = null;
        try
        {
            sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => Close());
        }
        catch (Exception error) when (error is PlatformNotSupportedException or IOException or ArgumentException)
        {
            // 平台不提供该信号（如 Windows 的 SIGTERM）：仅靠 Ctrl+C 关闭。
            sigterm = null;
        }

        try
        {
            await runtime.Closed.ConfigureAwait(false);
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

            await runtime.Close().ConfigureAwait(false);
        }

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            // TS 的信号处理器接管了默认行为：进程不再被 Ctrl+C 直接终止，而是等
            // runtime.closed 落地后由调用方收尾（C360）。
            e.Cancel = true;
            Close();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------------------------------

    private static bool SameStrings(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Count == right.Count && left.Select((value, index) => value == right[index]).All(same => same);

    /// <summary>TS <c>Promise.allSettled</c> over the non-null tasks: the failures, in order.</summary>
    private static async Task<List<Exception>> AllSettledAsync(IReadOnlyList<Task> tasks)
    {
        var settled = await Task.WhenAll(tasks.Select(task => CleanupAsync(task))).ConfigureAwait(false);
        return settled.Where(error => error is not null).Select(error => error!).ToList();
    }

    /// <summary>One settled step: null for a no-op (TS's <c>undefined</c> entry), else the failure.</summary>
    private static async Task<Exception?> CleanupAsync(Task? task)
    {
        if (task is null) return null;
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    /// <summary>Observe a fire-and-forget task without ever surfacing its failure. TS <c>.catch(() =&gt; {})</c>.</summary>
    private static void FireAndForget(Task task)
    {
        _ = task.ContinueWith(static _ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
