namespace Pi.CodingAgent.Utils;

/// <summary>Port of <c>utils/pi-user-agent.ts</c>.</summary>
/// <remarks>
/// Upstream now delegates to pi-ai's <c>formatUserAgent({ userAgent: "detailed", version })</c>, so the
/// value is the same detailed <c>pi/&lt;version&gt; (&lt;platform&gt;; &lt;runtime&gt;; &lt;arch&gt;)</c>
/// form that pi-ai sends to providers. The runtime token is <c>dotnet/&lt;version&gt;</c> here, since
/// neither <c>bun/&lt;version&gt;</c> nor <c>node/&lt;version&gt;</c> exists in this port.
/// </remarks>
public static class PiUserAgent
{
    /// <summary>Build the <c>User-Agent</c> header value pi sends for its own network requests.</summary>
    public static string GetPiUserAgent(string version)
        => Pi.Ai.Utils.Identity.FormatUserAgent(new Pi.Ai.Utils.ClientIdentity
        {
            UserAgent = Pi.Ai.Utils.UserAgentMode.Detailed,
            Version = version,
        });
}
