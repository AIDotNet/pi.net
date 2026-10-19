// ============================================================================
// Experimental client — port of experimental/client.ts (4e-3c-2)
// ============================================================================
//
// The non-interactive half of the experimental presentation: discover the servers (or connect to the
// one `--connect` names), then either list every Session of every server, attach to a selected one,
// or create one for a prompt and wait for its answer. Every failure path throws before anything is
// attached; the runtime is always disposed on the way out.
//
// Port differences:
// - The attached and prompted paths need a live session worker (the session-scoped services
//   hydrate through it), so they are covered end to end once 4g-3f wires the worker process; the
//   reachable paths — the list and every selection error — are covered against a real server.
// - TS's `Promise.all` over the activations becomes `Task.WhenAll`; with a single server (the only
//   shape the reachable paths produce) the first rejection wins, as in TS.

using Pi.Chord.Context;
using Pi.CodingAgent.Cli.Experimental;
using Pi.CodingAgent.Experimental.Services;

namespace Pi.CodingAgent.Experimental;

/// <summary>Options of <see cref="ExperimentalClient.RunClientAsync"/>.</summary>
public sealed record RunClientOptions
{
    /// <summary>Directory searched when --connect is omitted. Defaults to PI_SERVER_DIR or ~/.pi/server.</summary>
    public string? Directory { get; init; }
}

/// <summary>Port of <c>experimental/client.ts</c>.</summary>
public static class ExperimentalClient
{
    /// <summary>
    /// Discover servers, then list Sessions, attach to one, or create one for a prompt. TS
    /// <c>runClient</c>.
    /// </summary>
    public static async Task<ClientResult> RunClientAsync(ClientCommand command, RunClientOptions? options = null)
    {
        var runtime = await ClientRuntimes.OpenClientRuntimeAsync(
            command, new OpenClientRuntimeOptions { Directory = options?.Directory }).ConfigureAwait(false);
        try
        {
            var discovered = await Task.WhenAll(runtime.Servers.Select(
                server => ClientRuntimes.ActivateBuiltinClientServicesAsync(server))).ConfigureAwait(false);
            var sessionId = command.SessionId;
            if (sessionId is null && command.Prompt is null)
            {
                return new ClientResult.List(ListSessions(discovered));
            }

            ActivatedClientRuntimeServer match;
            if (sessionId is null)
            {
                if (discovered.Length != 1)
                {
                    throw new InvalidOperationException(
                        "Client prompt requires exactly one discovered server to create a Session");
                }

                match = discovered[0];
                sessionId = (await match.Management.CreateAsync(
                    new SessionCreateOptions(), Context.Background).ConfigureAwait(false)).SessionId;
            }
            else
            {
                var selectedSessionId = sessionId;
                var matches = discovered.Where(candidate => SessionsOf(candidate)
                    .Any(summary => summary.SessionId == selectedSessionId)).ToList();
                if (matches.Count > 1)
                {
                    throw new InvalidOperationException(
                        $"Session {selectedSessionId} is available from more than one server");
                }

                var existing = matches.FirstOrDefault();
                if (existing is not null)
                {
                    match = existing;
                }
                else
                {
                    if (command.Connect is TransportAddress.Radius
                        || command.Prompt is null
                        || discovered.Length != 1)
                    {
                        throw new InvalidOperationException(
                            $"No discovered server contains session {selectedSessionId}");
                    }

                    match = discovered[0];
                    await match.Management.CreateAsync(
                        new SessionCreateOptions { Id = selectedSessionId }, Context.Background).ConfigureAwait(false);
                }
            }

            // TS: packagePaths?.map((packagePath) => resolve(packagePath)) ?? null.
            var packagePaths = command.PluginPackages?.Select(Path.GetFullPath).ToList();
            await match.Plugins.PrepareSessionAsync(
                new PrepareSessionRequest(sessionId, packagePaths), Context.Background).ConfigureAwait(false);
            await match.Management.AttachAsync(sessionId, Context.Background).ConfigureAwait(false);
            if (command.Prompt is null)
            {
                return new ClientResult.Attached(match.Route.ServerId, sessionId);
            }

            var response = await match.Agent.PromptAsync(
                new AgentPromptRequest(command.Prompt, null), Context.Background).ConfigureAwait(false);
            if (response is not AgentOperationResponse.Accepted accepted)
            {
                var rejected = (AgentOperationResponse.Rejected)response;
                throw new InvalidOperationException(rejected.Reason.Message);
            }

            var result = await match.Agent.WaitForPromptAsync(
                accepted.OperationId, Context.Background).ConfigureAwait(false);
            if (result is AgentPromptResult.Unanswered unanswered)
            {
                throw new InvalidOperationException($"Prompt was not answered: {unanswered.Reason}");
            }

            return new ClientResult.Prompted(
                match.Route.ServerId, sessionId, ((AgentPromptResult.Done)result).Text);
        }
        finally
        {
            await runtime.Dispose().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// TS's inline flatMap + sort: every session of every discovered server, ordered by server then
    /// session (localeCompare → CurrentCulture, C79).
    /// </summary>
    private static IReadOnlyList<SessionAddress> ListSessions(
        IReadOnlyList<ActivatedClientRuntimeServer> discovered)
    {
        var sessions = new List<SessionAddress>();
        foreach (var server in discovered)
        {
            foreach (var summary in SessionsOf(server))
            {
                sessions.Add(new SessionAddress(server.Route.ServerId, summary.SessionId));
            }
        }

        return sessions
            .OrderBy(session => session.ServerId, StringComparer.CurrentCulture)
            .ThenBy(session => session.SessionId, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// TS's <c>directory.state.value!</c>: the ready gate has hydrated the directory, so a missing
    /// value is a broken invariant rather than an empty list.
    /// </summary>
    private static IReadOnlyList<SessionSummary> SessionsOf(ActivatedClientRuntimeServer server)
        => server.Directory.State.Value?.Sessions
            ?? throw new InvalidOperationException("Session directory state is not hydrated");
}
