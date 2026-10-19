using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental;
using Xunit;

namespace Pi.CodingAgent.Tests;

public class SessionWorkerHarnessSettingsTests
{
    [Fact]
    public void CreateHarnessSettings_PreservesSafeIntegerCompactionBudgetsBeyondInt32()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pi-worker-settings-{Guid.NewGuid():N}");
        var cwd = Path.Combine(root, "project");
        var agentDir = Path.Combine(root, "agent");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(
            Path.Combine(agentDir, "settings.json"),
            """
            {
              "compaction": {
                "reserveTokens": 9007199254740991,
                "keepRecentTokens": 2147483648
              }
            }
            """);

        try
        {
            var manager = SettingsManager.Create(cwd, agentDir);
            var settings = SessionWorkerHarnessSetup.CreateHarnessSettings(manager);

            Assert.NotNull(settings.Compaction);
            Assert.Equal(9007199254740991L, settings.Compaction.ReserveTokens);
            Assert.Equal(2147483648L, settings.Compaction.KeepRecentTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
