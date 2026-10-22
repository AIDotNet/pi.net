// ============================================================================
// Client identity — port of core/client-identity.ts
// ============================================================================
//
// pi's requests identify themselves to providers with a detailed User-Agent carrying pi's version,
// and attribute the traffic to pi as a CLI agent while install telemetry is enabled. The headers
// themselves are produced by pi-ai (Utils.Identity); this module only decides the identity, replacing
// the former core/provider-attribution.ts, which upstream folded into pi-ai.

namespace Pi.CodingAgent.Core;

/// <summary>
/// Port of <c>core/client-identity.ts</c>. Named separately from <see cref="Pi.Ai.Utils.ClientIdentity"/>,
/// the identity value type it produces, so both can be in scope at once.
/// </summary>
public static class ClientIdentitySource
{
    /// <summary>
    /// How pi identifies itself to providers: detailed User-Agent with pi's version, plus attribution
    /// to pi as a CLI agent while install telemetry is enabled (read per request, so a settings change
    /// applies at once).
    /// </summary>
    public static Pi.Ai.Utils.ClientIdentity GetClientIdentity(SettingsManager settingsManager)
        => new()
        {
            UserAgent = Pi.Ai.Utils.UserAgentMode.Detailed,
            Version = Config.Version,
            Attribution = Telemetry.IsInstallTelemetryEnabled(settingsManager)
                ? Pi.Ai.Utils.Identity.PiAttribution
                : null,
        };
}
