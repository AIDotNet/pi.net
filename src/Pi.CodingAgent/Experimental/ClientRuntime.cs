// ============================================================================
// Experimental client runtime — port of experimental/client-runtime.ts (4e-3c-1)
// ============================================================================
//
// The presentation half of the experimental session services: `openClientRuntime` picks the routes
// (an explicit `--connect`, discovery, or a cold activation), opens one server-scoped and one
// selected-Session service source per route, and assembles the Radius reconnect driver where the
// route is remote. `activateBuiltinClientServices` then hands out the typed facades the
// non-interactive client drives, wrapping management so a remove or detach also settles the
// session source's replicated attachment state.
//
// Port differences:
// - TS's `use(Service)` returns a Proxy satisfying the service interface; the C# chord port has no
//   Proxy, so the typed views are the explicit client-side adapters (C374).
// - The connect step goes through an injectable factory: TS's test mocks the static `Client.connect`
//   to force a version mismatch, and the port's seam is the same injection point.
// - `Promise.allSettled` becomes the established `AllSettledAsync` helper (one failure rethrows
//   itself, several become an aggregate — the same shape TS's inline reduction produces).

using Pi.Chord.Context;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Pi.Client;
using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Experimental.Services;
using Pi.Protocol;
// `Client` alone resolves to the Pi.Client *namespace* from inside this namespace; alias the type.
using ServiceClient = Pi.Client.Client;

// Pi.Server declares its own ConnectionState; the client one is what the reconnect drives.
using ClientConnectionState = Pi.Client.ConnectionState;

namespace Pi.CodingAgent.Experimental;

/// <summary>
/// One route to a server. TS <c>ClientRuntimeRoute</c>: the unix variant spreads a
/// <see cref="UnixServerRoute"/>, the radius variant carries only the logical server id.
/// </summary>
public abstract record ClientRuntimeRoute
{
    /// <summary>The logical server this route reaches.</summary>
    public abstract string ServerId { get; init; }

    /// <summary>A local Unix socket route.</summary>
    public sealed record Unix(string ServerId, string Path) : ClientRuntimeRoute;

    /// <summary>A remote Radius relay route.</summary>
    public sealed record Radius(string ServerId) : ClientRuntimeRoute;
}

/// <summary>
/// One opened server: its route, its client, and its two service sources. Not sealed so
/// <see cref="ActivatedClientRuntimeServer"/> can extend it, the way <see cref="RunningServer"/> is
/// extended by the backend.
/// </summary>
public record ClientRuntimeServer(
    ClientRuntimeRoute Route,
    ServiceClient Client,
    IServerServiceSource Server,
    ISessionServiceSource Session);

/// <summary>
/// One server plus the typed facades the non-interactive client drives.
/// TS <c>ActivatedClientRuntimeServer</c>.
/// </summary>
public sealed record ActivatedClientRuntimeServer(
    ClientRuntimeRoute Route,
    ServiceClient Client,
    IServerServiceSource Server,
    ISessionServiceSource Session,
    ISessionDirectory Directory,
    ISessionManagement Management,
    IPresentationPlugins Plugins,
    IModels Models,
    IAgentController Agent,
    ITranscript Transcript)
    : ClientRuntimeServer(Route, Client, Server, Session);

/// <summary>The opened runtime: every server, plus the disposal that unwinds them all.</summary>
public sealed record ClientRuntime(
    IReadOnlyList<ClientRuntimeServer> Servers,
    Func<Task> Dispose);

/// <summary>Options of <see cref="ClientRuntimes.OpenClientRuntimeAsync"/>.</summary>
public sealed record OpenClientRuntimeOptions
{
    /// <summary>Directory searched when --connect is omitted. Defaults to PI_SERVER_DIR or ~/.pi/server.</summary>
    public string? Directory { get; init; }

    /// <summary>
    /// Connects one client. Defaults to <see cref="ServiceClient.ConnectAsync"/>; the tests inject a
    /// factory that fails the first attempt, the way TS's suite mocks <c>Client.connect</c>.
    /// </summary>
    public Func<ClientOptions, Task<ServiceClient>>? ClientFactory { get; init; }
}

/// <summary>Port of <c>experimental/client-runtime.ts</c>.</summary>
public static class ClientRuntimes
{
    /// <summary>
    /// Open live server/session service namespaces for one experimental presentation. TS
    /// <c>openClientRuntime</c>: validate the command, choose the routes, connect, and assemble the
    /// reconnect drivers — unwinding everything already opened when any step fails.
    /// </summary>
    public static async Task<ClientRuntime> OpenClientRuntimeAsync(
        ClientCommand command, OpenClientRuntimeOptions? options = null)
    {
        if (command.Auth is not null && command.Connect is not TransportAddress.Radius)
        {
            throw new InvalidOperationException(
                "Authentication is only supported for experimental Radius connections");
        }

        if (command.Provider is not null && command.Model is null)
        {
            throw new InvalidOperationException("Server model provider requires a model");
        }

        if (command.Connect is not null && command.Model is not null)
        {
            throw new InvalidOperationException(
                "Model selection is only valid when automatically activating a new server");
        }

        if (command.Connect is TransportAddress.Radius && command.PluginPackages is not null)
        {
            throw new InvalidOperationException("Plugin package paths can only be configured on a local Unix server");
        }

        var directory = ExperimentalServer.ResolveServerDirectory(options?.Directory);
        var connectAsync = options?.ClientFactory
            ?? (clientOptions => ServiceClient.ConnectAsync(clientOptions));
        List<ClientRuntimeRoute> routes;
        ServiceClient? activatedClient = null;
        if (command.Connect is { } connect)
        {
            routes =
            [
                connect switch
                {
                    TransportAddress.Radius radius => new ClientRuntimeRoute.Radius(radius.ServerId),
                    TransportAddress.Unix unix => UnixRouteFromPath(unix.Path),
                    _ => throw new InvalidOperationException("Unsupported transport address"),
                },
            ];
        }
        else
        {
            routes = (await UnixTransport.DiscoverUnixServersAsync(
                    new DiscoverUnixServersOptions { Directory = directory }).ConfigureAwait(false))
                .Select(route => (ClientRuntimeRoute)new ClientRuntimeRoute.Unix(route.ServerId, route.Path))
                .ToList();
            if (routes.Count > 0 && command.Model is not null)
            {
                throw new InvalidOperationException(
                    "Model selection is only valid when automatically activating a new server");
            }

            if (routes.Count == 0)
            {
                var activated = await ExperimentalServer.ActivateServerAsync(new ActivateServerOptions
                {
                    Directory = directory,
                    RequestedServerId = Environment.GetEnvironmentVariable(ExperimentalServer.EnvServerId),
                    SessionDir = ExperimentalServer.ResolveSessionDirectory(),
                    Provider = command.Provider,
                    Model = command.Model,
                }).ConfigureAwait(false);
                routes = [new ClientRuntimeRoute.Unix(activated.Route.ServerId, activated.Route.Path)];
                activatedClient = activated.Client;
            }
        }

        if (command.PluginPackages is not null && routes.Count != 1)
        {
            throw new InvalidOperationException("Plugin selection requires exactly one local server");
        }

        var clients = new List<ServiceClient>();
        var reconnectors = new List<RadiusClientReconnect>();
        var serviceSources = new List<Func<Task>>();
        var servers = new List<ClientRuntimeServer>();
        var disposed = false;

        async Task DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            var errors = new List<Exception>();
            errors.AddRange(await AllSettledAsync(
                reconnectors.Select(reconnector => reconnector.DisposeAsync().AsTask())).ConfigureAwait(false));
            errors.AddRange(await AllSettledAsync(
                serviceSources.Select(disposeSource => disposeSource())).ConfigureAwait(false));
            errors.AddRange(await AllSettledAsync(
                clients.Select(client => client.DisposeAsync())).ConfigureAwait(false));
            if (errors.Count == 1) throw errors[0];
            if (errors.Count > 1)
            {
                throw new AggregateException("Failed to dispose experimental client runtime", errors);
            }
        }

        try
        {
            foreach (var route in routes)
            {
                var client = activatedClient;
                if (client is null)
                {
                    try
                    {
                        client = await connectAsync(new ClientOptions
                        {
                            ServerId = route.ServerId,
                            TransportFactory = route switch
                            {
                                ClientRuntimeRoute.Unix unix => UnixTransport.CreateUnixTransportFactory(
                                    new UnixTransportOptions { Path = unix.Path }),
                                _ => RadiusRelayClients.CreateTransportFactory(new RadiusClientTransportOptions
                                {
                                    ServerId = route.ServerId,
                                    Auth = new RadiusRelayAuthResolver(command.Auth),
                                }),
                            },
                        }).ConfigureAwait(false);
                    }
                    catch (Exception error) when (IsStaleServer(error, command.Connect, route))
                    {
                        // A discovered socket whose server rejects the protocol version is a stale
                        // generation: activate a fresh one and use its client.
                        var activated = await ExperimentalServer.ActivateServerAsync(new ActivateServerOptions
                        {
                            Directory = directory,
                            RequestedServerId = route.ServerId,
                            SessionDir = ExperimentalServer.ResolveSessionDirectory(),
                        }).ConfigureAwait(false);
                        client = activated.Client;
                    }
                }

                activatedClient = null;
                clients.Add(client);
                var server = ServiceConnection.CreateServerServiceSource(client);
                serviceSources.Add(() => server.DisposeAsync(Context.Background));
                if (route is ClientRuntimeRoute.Radius)
                {
                    var reconnectServices = server.Open(new RemoteServicesOptions
                    {
                        Services = [SessionManagement.Service.Id],
                        AssertAccess = () => { },
                        OnError = _ => { },
                    });
                    var reconnectManagement = RemoteServiceClients.SessionManagement(
                        Facade(reconnectServices, SessionManagement.Service.Id));
                    reconnectors.Add(new RadiusClientReconnect(new ClientReconnect(client), async sessionId =>
                    {
                        await reconnectServices.ReadyAsync().ConfigureAwait(false);
                        await reconnectManagement.AttachAsync(sessionId, Context.Background).ConfigureAwait(false);
                    }));
                }

                var session = ServiceConnection.CreateSessionServiceSource(client);
                serviceSources.Add(() => session.DisposeAsync(Context.Background));
                servers.Add(new ClientRuntimeServer(route, client, server, session));
            }

            return new ClientRuntime(servers, DisposeAsync);
        }
        catch (Exception error)
        {
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                var failures = new List<Exception> { error, cleanupError };
                throw new AggregateException("Experimental client startup and cleanup failed", failures);
            }

            throw;
        }
    }

    /// <summary>
    /// Acquire and connect the built-in service facades used by the non-interactive client. TS
    /// <c>activateBuiltinClientServices</c>: management is wrapped so a remove or detach also settles
    /// the session source's replicated attachment state.
    /// </summary>
    public static async Task<ActivatedClientRuntimeServer> ActivateBuiltinClientServicesAsync(
        ClientRuntimeServer server)
    {
        var serverServices = server.Server.Open(new RemoteServicesOptions
        {
            Services =
            [
                SessionDirectory.Service.Id,
                SessionManagement.Service.Id,
                PresentationPlugins.Service.Id,
            ],
            AssertAccess = () => { },
            OnError = _ => { },
        });
        var sessionServices = server.Session.Open(new RemoteServicesOptions
        {
            Services = [Models.Service.Id, AgentController.Service.Id, Transcript.Service.Id],
            AssertAccess = () => { },
            OnError = _ => { },
        });
        var directory = RemoteServiceClients.SessionDirectory(
            Facade(serverServices, SessionDirectory.Service.Id));
        var remoteManagement = RemoteServiceClients.SessionManagement(
            Facade(serverServices, SessionManagement.Service.Id));
        var management = new AttachedSessionManagement(remoteManagement, server);
        var plugins = RemoteServiceClients.PresentationPlugins(
            Facade(serverServices, PresentationPlugins.Service.Id));
        var models = RemoteServiceClients.Models(Facade(sessionServices, Models.Service.Id));
        var agent = RemoteServiceClients.AgentController(Facade(sessionServices, AgentController.Service.Id));
        var transcript = RemoteServiceClients.Transcript(Facade(sessionServices, Transcript.Service.Id));
        await Task.WhenAll(serverServices.ReadyAsync(), sessionServices.ReadyAsync()).ConfigureAwait(false);
        return new ActivatedClientRuntimeServer(
            server.Route, server.Client, server.Server, server.Session,
            directory, management, plugins, models, agent, transcript);
    }

    /// <summary>
    /// TS's <c>routeFromExplicitPath</c>: an explicit <c>--connect</c> path names its server by the
    /// socket file's stem, which must be a server id.
    /// </summary>
    private static ClientRuntimeRoute UnixRouteFromPath(string path)
    {
        var name = Path.GetFileName(path);
        var serverId = name.EndsWith(".sock", StringComparison.Ordinal) ? name[..^".sock".Length] : "";
        if (!ServerIds.IsServerId(serverId))
        {
            throw new InvalidOperationException("--connect path must end with <uuidv4-server-id>.sock");
        }

        return new ClientRuntimeRoute.Unix(serverId, path);
    }

    /// <summary>
    /// TS's recheck guard: only a *discovered* unix route whose connect failed with a protocol
    /// version error re-activates; an explicit <c>--connect</c> (or any other failure) propagates.
    /// </summary>
    private static bool IsStaleServer(Exception error, TransportAddress? connect, ClientRuntimeRoute route)
        => connect is null
            && route is ClientRuntimeRoute.Unix
            && error is ServerError { Code: "version" };

    private static RemoteServiceFacade Facade(IRemoteServices services, string serviceId)
        => (RemoteServiceFacade)services.UseRaw(serviceId)!;

    /// <summary>TS <c>Promise.allSettled</c> over the tasks: the failures, in order.</summary>
    private static async Task<List<Exception>> AllSettledAsync(IEnumerable<Task> tasks)
    {
        var settled = await Task.WhenAll(tasks.Select(SettleAsync)).ConfigureAwait(false);
        return settled.Where(error => error is not null).Select(error => error!).ToList();
    }

    private static async Task<Exception?> SettleAsync(Task task)
    {
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

    /// <summary>
    /// TS's inline management wrapper: the remote calls plus the local attachment bookkeeping a
    /// presentation needs (a remove of the attached session waits for the detach, an attach waits for
    /// the session source to follow).
    /// </summary>
    private sealed class AttachedSessionManagement(ISessionManagement remote, ClientRuntimeServer server)
        : ISessionManagement    {
        public Task<SessionSummary> CreateAsync(SessionCreateOptions options, Context context)
            => remote.CreateAsync(options, context);

        public async Task RemoveAsync(string sessionId, Context context)
        {
            var removesCurrentAttachment = server.Client.Attachment?.SessionId == sessionId;
            await remote.RemoveAsync(sessionId, context).ConfigureAwait(false);
            if (removesCurrentAttachment)
            {
                await server.Session.WhenDetachedAsync(context).ConfigureAwait(false);
            }
        }

        public async Task AttachAsync(string sessionId, Context context)
        {
            await remote.AttachAsync(sessionId, context).ConfigureAwait(false);
            await server.Session.WhenAttachedAsync(sessionId, context).ConfigureAwait(false);
        }

        public async Task DetachAsync(Context context)
        {
            await remote.DetachAsync(context).ConfigureAwait(false);
            await server.Session.WhenDetachedAsync(context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <see cref="ServiceClient"/> as <see cref="IRadiusReconnectClient"/>. TS's
    /// <c>Pick&lt;Client, …&gt;</c> is structural, so the client passes itself directly; C# is
    /// nominally typed, so the slice is spelled out once here (the client does not declare the
    /// interface itself).
    /// </summary>
    private sealed class ClientReconnect(ServiceClient client) : IRadiusReconnectClient
    {
        public RpcTarget.SessionTarget? Attachment => client.Attachment;

        public bool Connected => client.Connected;

        public ClientConnectionState ConnectionState => client.ConnectionState;

        public void Disconnect(string reason) => client.Disconnect(reason);

        public Unsubscribe OnConnectionStateChange(Action<ConnectionStateChange> listener)
            => client.OnConnectionStateChange(listener);

        public Unsubscribe OnAttachmentChange(AttachmentChangeListener listener)
            => client.OnAttachmentChange(listener);

        public Task<ServerMessage.Hello> ReconnectAsync() => client.ReconnectAsync();
    }
}
