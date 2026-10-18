// ============================================================================
// Session directory + management services — port of experimental/services/sessions.ts (4g-3b)
// ============================================================================
//
// The directory is replicated state (every presentation sees the same Session list); management is
// request/response (create / remove / attach / detach). `createdAt` stays a double for the same reason
// as in the session catalog: TS only checks `typeof === "number"`.

using Pi.Chord;
using Pi.Chord.Facets;
using Pi.Chord.Context;
using Pi.Chord.Services;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>Where a Session lives. TS <c>SessionAddress</c> (<c>ServerId</c> is a validated string).</summary>
public record SessionAddress(string ServerId, string SessionId);

/// <summary>One row of the Session directory. TS <c>SessionSummary extends SessionAddress</c>.</summary>
public sealed record SessionSummary(string ServerId, string SessionId, double CreatedAt)
    : SessionAddress(ServerId, SessionId);

/// <summary>Options of <see cref="ISessionManagement.CreateAsync"/>. TS <c>SessionCreateOptions</c>.</summary>
public sealed record SessionCreateOptions(string? Id = null);

/// <summary>The replicated directory state. TS <c>SessionDirectoryState</c>.</summary>
public sealed record SessionDirectoryState(long Revision, IReadOnlyList<SessionSummary> Sessions);

/// <summary>Port of the TS <c>SessionDirectory</c> interface.</summary>
public interface ISessionDirectory
{
    /// <summary>The replicated directory state.</summary>
    IReplicatedState<SessionDirectoryState> State { get; }
}

/// <summary>TS <c>export const SessionDirectory = defineService&lt;SessionDirectory&gt;("pi.session-directory")</c>.</summary>
public static class SessionDirectory
{
    /// <summary>The chord service handle for <see cref="ISessionDirectory"/>.</summary>
    public static readonly Service<ISessionDirectory> Service =
        Api.DefineService<ISessionDirectory>("pi.session-directory");
}

/// <summary>Port of the TS <c>SessionManagement</c> interface.</summary>
public interface ISessionManagement
{
    /// <summary>Create a Session and return its summary.</summary>
    Task<SessionSummary> CreateAsync(SessionCreateOptions options, Context context);

    /// <summary>Remove a Session.</summary>
    Task RemoveAsync(string sessionId, Context context);

    /// <summary>Attach this presentation to a Session.</summary>
    Task AttachAsync(string sessionId, Context context);

    /// <summary>Detach from the current Session.</summary>
    Task DetachAsync(Context context);
}

/// <summary>TS <c>export const SessionManagement = defineService&lt;SessionManagement&gt;("pi.session-management")</c>.</summary>
public static class SessionManagement
{
    /// <summary>The chord service handle for <see cref="ISessionManagement"/>.</summary>
    public static readonly Service<ISessionManagement> Service =
        Api.DefineService<ISessionManagement>("pi.session-management");
}
