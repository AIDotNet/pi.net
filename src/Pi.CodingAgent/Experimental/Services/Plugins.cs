// ============================================================================
// Plugin services — port of experimental/services/plugins.ts (4g-3b)
// ============================================================================
//
// Two sides of plugin hosting:
// - `PresentationPlugins` is server-side: it builds the plugin generation a presentation will load
//   (`prepareSession`) and rebuilds it on `reload`.
// - `SessionPlugins` is the worker-side view: it asks the attached Session's plugin facets to reload.
//
// Both answers are chord JSON values. TS types them `JsonValue`; the port's chord uses a CLR value tree
// (dictionaries/lists/scalars), so they are `object?` (C316).

using Pi.Chord;
using Pi.Chord.Facets;
using Pi.Chord.Context;

namespace Pi.CodingAgent.Experimental.Services;

/// <summary>What to prepare for a presentation. TS the inline request of <c>prepareSession</c>.</summary>
public sealed record PrepareSessionRequest(string SessionId, IReadOnlyList<string>? PackagePaths);

/// <summary>Server-built plugin generations available to presentations. TS <c>PresentationPlugins</c>.</summary>
public interface IPresentationPlugins
{
    /// <summary>Build the plugin generation for one Session.</summary>
    Task<object?> PrepareSessionAsync(PrepareSessionRequest request, Context context);

    /// <summary>Rebuild every prepared generation.</summary>
    Task<object?> ReloadAsync(Context context);
}

/// <summary>TS <c>export const PresentationPlugins = defineService&lt;PresentationPlugins&gt;("pi.presentation-plugins")</c>.</summary>
public static class PresentationPlugins
{
    /// <summary>The chord service handle for <see cref="IPresentationPlugins"/>.</summary>
    public static readonly Service<IPresentationPlugins> Service =
        Api.DefineService<IPresentationPlugins>("pi.presentation-plugins");
}

/// <summary>Plugin facets hosted in the currently attached Session worker. TS <c>SessionPlugins</c>.</summary>
public interface ISessionPlugins
{
    /// <summary>Reload the attached Session's plugin facets.</summary>
    Task ReloadAsync(Context context);
}

/// <summary>TS <c>export const SessionPlugins = defineService&lt;SessionPlugins&gt;("pi.session-plugins")</c>.</summary>
public static class SessionPlugins
{
    /// <summary>The chord service handle for <see cref="ISessionPlugins"/>.</summary>
    public static readonly Service<ISessionPlugins> Service =
        Api.DefineService<ISessionPlugins>("pi.session-plugins");
}
