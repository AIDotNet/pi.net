// ============================================================================
// Auth credential extraction — port of cli/auth-command.ts's getAuthCredential.
//
// The rest of auth-command.ts (the `pi auth …` command parsing) lands with the
// 4g CLI layer; this helper is needed by the interactive share and bug-report
// flows today.
// ============================================================================

using System.Text.RegularExpressions;
using Pi.Ai.Auth;

namespace Pi.CodingAgent.Core;

/// <summary>
/// Pull the bearer token out of a resolved auth result. Port of
/// <c>cli/auth-command.ts</c>'s <c>getAuthCredential</c> (L120-126).
/// </summary>
public static partial class AuthCredentials
{
    [GeneratedRegex(@"^Bearer\s+(.+)\z", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    /// <summary>
    /// The api key when the auth carries one, otherwise the token from the
    /// <c>Authorization: Bearer …</c> header. <see langword="null"/> when neither is present.
    /// </summary>
    /// <remarks>
    /// TS tests <c>auth?.auth.apiKey</c> for truthiness, so an empty key falls through to the
    /// header. The JS <c>$</c> anchor does not match before a trailing newline, hence
    /// <c>\z</c> instead of .NET's <c>$</c>.
    /// </remarks>
    public static string? GetAuthCredential(AuthResult? auth)
    {
        if (!string.IsNullOrEmpty(auth?.Auth.ApiKey))
        {
            return auth!.Auth.ApiKey;
        }

        var headers = auth?.Auth.Headers;
        if (headers is null)
        {
            return null;
        }

        foreach (var (name, value) in headers)
        {
            if (!string.Equals(name.ToLowerInvariant(), "authorization", StringComparison.Ordinal))
            {
                continue;
            }

            if (value is not { } authorization)
            {
                continue;
            }

            var match = BearerRegex().Match(authorization);
            return match.Success ? match.Groups[1].Value : null;
        }

        return null;
    }
}
