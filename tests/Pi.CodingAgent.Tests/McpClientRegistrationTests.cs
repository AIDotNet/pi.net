using Pi.CodingAgent.Core;
using Pi.CodingAgent.Extensions.Mcp;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// MCP OAuth client identification (TS <c>908aa7b74</c>): <c>clientRegistration</c> defaults to
/// <c>auto</c>, which uses pi's Client ID Metadata Document when the authorization server supports it
/// and falls back to dynamic client registration otherwise.
/// </summary>
public class McpClientRegistrationTests
{
    private static McpOAuthSettings Settings(
        McpClientRegistration? registration = null,
        string? clientId = null,
        string? clientName = null,
        string? callbackUrl = null) => new()
    {
        ClientRegistration = registration,
        ClientId = clientId,
        ClientName = clientName,
        CallbackUrl = callbackUrl,
    };

    [Fact]
    public void AutoOffersTheDocumentByDefault()
    {
        Assert.True(McpOAuth.OffersClientMetadataDocument(Settings()));
    }

    [Fact]
    public void AutoRegistersWhenTheUserNamesTheClient()
    {
        // A custom client name must reach the authorization server, which only registration carries.
        Assert.False(McpOAuth.OffersClientMetadataDocument(Settings(clientName: "work laptop")));
        Assert.False(McpOAuth.OffersClientMetadataDocument(Settings(clientId: "abc")));
    }

    [Fact]
    public void AutoRegistersForARedirectUriTheDocumentsDoNotList()
    {
        Assert.False(McpOAuth.OffersClientMetadataDocument(
            Settings(callbackUrl: "http://127.0.0.1:1234/other")));
        Assert.False(McpOAuth.OffersClientMetadataDocument(
            Settings(callbackUrl: "http://[::1]:1234/callback")));

        Assert.True(McpOAuth.OffersClientMetadataDocument(
            Settings(callbackUrl: "http://127.0.0.1:1234/callback")));
        Assert.True(McpOAuth.OffersClientMetadataDocument(
            Settings(callbackUrl: "http://localhost:9999/callback")));
    }

    [Fact]
    public void ExplicitModesIgnoreTheAutoConditions()
    {
        Assert.True(McpOAuth.OffersClientMetadataDocument(
            Settings(McpClientRegistration.Cimd, clientName: "work laptop")));
        Assert.False(McpOAuth.OffersClientMetadataDocument(
            Settings(McpClientRegistration.Dcr)));
    }

    [Fact]
    public void DocumentIsNotClaimedWithoutServerSupport()
    {
        // `cimd` throws at the call site; `auto` simply registers instead.
        Assert.Null(McpOAuth.ClientMetadataDocumentFor("https://mcp.example", "http://127.0.0.1:1/callback", null));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://127.0.0.1:1234/callback", true)]
    [InlineData("http://localhost:1234/callback", true)]
    [InlineData("http://[::1]:1234/callback", false)]
    [InlineData("http://127.0.0.1:1234/other", false)]
    public void CallbackRuleMatchesTheDocuments(string? callbackUrl, bool expected)
    {
        Assert.Equal(expected, McpServers.IsClientMetadataDocumentCallback(callbackUrl));
    }
}
