using Pi.CodingAgent.Experimental.Vacation;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class VacationRuntimeTests
{
    [Fact]
    public async Task OpenAndContinueRestoreTheSameRootSessionAndReleaseItsLock()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pi-vacation-runtime-tests-{Guid.NewGuid():N}");
        var cwd = Path.Combine(root, "work");
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(cwd);

        try
        {
            var first = await VacationRuntime.OpenAsync(new OpenVacationOptions
            {
                Cwd = cwd,
                AgentDirectory = agentDirectory,
            });
            string sessionId;
            try
            {
                var view = first.View.Current;
                sessionId = view.Session.Id;
                Assert.Equal(Path.GetFullPath(cwd), view.Session.Cwd);
                Assert.Single(view.Conversations);
                Assert.Equal("main", Assert.Single(view.Conversations).Label);
            }
            finally
            {
                await first.CloseAsync();
            }

            await using var continued = await VacationRuntime.OpenAsync(new OpenVacationOptions
            {
                Cwd = cwd,
                ContinueSession = true,
                AgentDirectory = agentDirectory,
            });

            Assert.Equal(sessionId, continued.View.Current.Session.Id);
            Assert.Equal("main", Assert.Single(continued.View.Current.Conversations).Label);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
