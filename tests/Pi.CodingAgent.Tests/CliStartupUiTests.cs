// ============================================================================
// Tests for the startup-UI gate (TS `cli/startup-ui.ts`), landed with 4g-1.
//
// Mirrors test/first-time-setup.test.ts and test/first-time-setup-fork.test.ts:
// the four shouldRunFirstTimeSetup conditions and the official-distribution
// check. The TS fork test swaps PACKAGE_NAME through `vi.mock`; the C# port has
// no module-replacement seam, so IsOfficialDistribution takes the three names
// as parameters and the test feeds a forked triple directly.
// ============================================================================

using Pi.CodingAgent.Cli;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class CliStartupUiTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;
    private readonly string? _originalExperimental;
    private readonly string? _originalAgentDir;

    public CliStartupUiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pi-first-time-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");

        _originalExperimental = Environment.GetEnvironmentVariable(Experimental.ExperimentalEnvVar);
        _originalAgentDir = Environment.GetEnvironmentVariable(Config.EnvAgentDir);
        Environment.SetEnvironmentVariable(Experimental.ExperimentalEnvVar, "1");
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Experimental.ExperimentalEnvVar, _originalExperimental);
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, _originalAgentDir);
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [Fact]
    public void ReturnsTrueWhenExperimentalDefaultAgentDirAndNoSettingsJson()
    {
        Assert.True(StartupUi.ShouldRunFirstTimeSetup(_settingsPath));
    }

    [Fact]
    public void ReturnsFalseWhenExperimentalFeaturesAreDisabled()
    {
        Environment.SetEnvironmentVariable(Experimental.ExperimentalEnvVar, null);
        Assert.False(StartupUi.ShouldRunFirstTimeSetup(_settingsPath));
    }

    [Fact]
    public void ReturnsFalseWhenACustomAgentDirIsSet()
    {
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, _tempDir);
        Assert.False(StartupUi.ShouldRunFirstTimeSetup(_settingsPath));
    }

    [Fact]
    public void EmptyAgentDirEnvDoesNotCountAsACustomDir()
    {
        // TS reads `process.env[ENV_AGENT_DIR]` for truthiness: an empty string is falsy.
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, "");
        Assert.True(StartupUi.ShouldRunFirstTimeSetup(_settingsPath));
    }

    [Fact]
    public void ReturnsFalseWhenSettingsJsonAlreadyExists()
    {
        File.WriteAllText(_settingsPath, "{}");
        Assert.False(StartupUi.ShouldRunFirstTimeSetup(_settingsPath));
    }

    [Fact]
    public void ReturnsFalseWhenAPathComponentIsADirectory()
    {
        // existsSync() is true for directories too.
        var directory = Path.Combine(_tempDir, "settings-dir");
        Directory.CreateDirectory(directory);
        Assert.False(StartupUi.ShouldRunFirstTimeSetup(directory));
    }

    [Fact]
    public void OfficialDistributionTripleIsRecognised()
    {
        Assert.True(StartupUi.IsOfficialDistribution(
            "@earendil-works/pi-coding-agent", "pi", ".pi"));
    }

    [Theory]
    [InlineData("@example/pi-coding-agent", "pi", ".pi")]
    [InlineData("@earendil-works/pi-coding-agent", "mypi", ".pi")]
    [InlineData("@earendil-works/pi-coding-agent", "pi", ".mypi")]
    public void ForkedDistributionIsRejected(string packageName, string appName, string configDirName)
    {
        // first-time-setup-fork.test.ts: a forked package never runs first-time setup.
        Assert.False(StartupUi.IsOfficialDistribution(packageName, appName, configDirName));
    }
}
