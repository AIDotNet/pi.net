using Pi.Ai.Utils;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <c>core/client-identity.ts</c> 测试：pi 如何向 provider 标识自己，取代已被上游并入 pi-ai 的
/// <c>core/provider-attribution.ts</c>。
/// </summary>
public sealed class ClientIdentityTests : IDisposable
{
    private readonly string? _previousTelemetry = Environment.GetEnvironmentVariable("PI_TELEMETRY");

    public ClientIdentityTests() => Environment.SetEnvironmentVariable("PI_TELEMETRY", null);

    public void Dispose() => Environment.SetEnvironmentVariable("PI_TELEMETRY", _previousTelemetry);

    private static SettingsManager Settings(bool installTelemetry)
        => SettingsManager.InMemory(new Settings { EnableInstallTelemetry = installTelemetry });

    [Fact]
    public void UsesADetailedUserAgentWithTheAgentVersion()
    {
        var identity = ClientIdentitySource.GetClientIdentity(Settings(installTelemetry: false));

        Assert.Same(UserAgentMode.Detailed, identity.UserAgent);
        Assert.Equal(Config.Version, identity.Version);
        Assert.StartsWith($"pi/{Config.Version} (", Identity.FormatUserAgent(identity));
    }

    [Fact]
    public void AttributesToPiOnlyWhileInstallTelemetryIsEnabled()
    {
        var enabled = ClientIdentitySource.GetClientIdentity(Settings(installTelemetry: true));
        Assert.Same(Identity.PiAttribution, enabled.Attribution);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["HTTP-Referer"] = "https://pi.dev",
                ["X-OpenRouter-Title"] = "pi",
                ["X-OpenRouter-Categories"] = "cli-agent",
            },
            Identity.GetAttributionHeaders(new ClientIdentityTarget("openrouter", "https://openrouter.ai/api/v1"), enabled));

        // 关闭时是显式 null（而不是「未设置」），才能压掉被合并进来的归属。
        var disabled = ClientIdentitySource.GetClientIdentity(Settings(installTelemetry: false));
        Assert.True(disabled.AttributionSet);
        Assert.Null(disabled.Attribution);
        Assert.Empty(Identity.GetAttributionHeaders(
            new ClientIdentityTarget("openrouter", "https://openrouter.ai/api/v1"), disabled));
    }

    [Fact]
    public void EnvironmentVariableOverridesTheSetting()
    {
        Environment.SetEnvironmentVariable("PI_TELEMETRY", "1");
        Assert.Same(Identity.PiAttribution, ClientIdentitySource.GetClientIdentity(Settings(false)).Attribution);

        Environment.SetEnvironmentVariable("PI_TELEMETRY", "0");
        Assert.Null(ClientIdentitySource.GetClientIdentity(Settings(true)).Attribution);
    }
}
