// ============================================================================
// Tests for AuthCredentials.GetAuthCredential — the port of
// cli/auth-command.ts's getAuthCredential (L120-126), needed by the share and
// bug-report flows. Upstream has no test file for it; the vectors are derived
// from the TS body.
// ============================================================================

using Pi.Ai.Auth;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class AuthCredentialsTests
{
    private static AuthResult Auth(string? apiKey = null, params (string Name, string? Value)[] headers)
        => new()
        {
            Auth = new ModelAuth
            {
                ApiKey = apiKey,
                Headers = headers.Length == 0
                    ? null
                    : headers.ToDictionary(header => header.Name, header => header.Value),
            },
        };

    [Fact]
    public void AnApiKeyWinsOverTheAuthorizationHeader()
        => Assert.Equal("sk-live-1", AuthCredentials.GetAuthCredential(Auth("sk-live-1", ("Authorization", "Bearer other"))));

    [Fact]
    public void AnEmptyApiKeyFallsThroughToTheHeader()
        => Assert.Equal("header-token", AuthCredentials.GetAuthCredential(Auth("", ("Authorization", "Bearer header-token"))));

    [Fact]
    public void TheBearerTokenIsExtractedFromTheAuthorizationHeader()
        => Assert.Equal("abc123", AuthCredentials.GetAuthCredential(Auth(null, ("Authorization", "Bearer abc123"))));

    [Fact]
    public void TheBearerPrefixIsCaseInsensitiveAndAllowsExtraWhitespace()
        => Assert.Equal("abc123", AuthCredentials.GetAuthCredential(Auth(null, ("authorization", "bearer   abc123"))));

    [Fact]
    public void ANonBearerAuthorizationHeaderYieldsNothing()
        => Assert.Null(AuthCredentials.GetAuthCredential(Auth(null, ("Authorization", "Basic abc123"))));

    [Fact]
    public void ABearerHeaderWithoutATokenYieldsNothing()
        => Assert.Null(AuthCredentials.GetAuthCredential(Auth(null, ("Authorization", "Bearer"))));

    [Fact]
    public void ABearerHeaderWithoutASpaceYieldsNothing()
        => Assert.Null(AuthCredentials.GetAuthCredential(Auth(null, ("Authorization", "Bearerabc"))));

    [Fact]
    public void MissingAuthYieldsNothing()
    {
        Assert.Null(AuthCredentials.GetAuthCredential(null));
        Assert.Null(AuthCredentials.GetAuthCredential(Auth()));
    }

    [Fact]
    public void OnlyTheAuthorizationHeaderIsConsidered()
        => Assert.Equal(
            "abc123",
            AuthCredentials.GetAuthCredential(Auth(
                null,
                ("X-Api-Key", "Bearer nope"),
                ("Proxy-Authorization", "Bearer nope"),
                ("Authorization", "Bearer abc123"))));
}
