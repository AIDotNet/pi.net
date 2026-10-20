using Pi.CodingAgent.Experimental;
using Pi.CodingAgent.Experimental.Durable;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// durable 实验宿主的运行时：会话定位、根对话、默认打开的任务面板、关闭释放锁与 <c>--continue</c> 复用同一会话。
/// 与 <see cref="VacationRuntimeTests"/> 同构——上游两个 <c>runtime.ts</c> 只差装配。
/// </summary>
public sealed class DurableRuntimeTests
{
    [Fact]
    public async Task OpenStartsOnTheRootConversationWithTheTaskPanelOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pi-durable-runtime-tests-{Guid.NewGuid():N}");
        var cwd = Path.Combine(root, "work");
        Directory.CreateDirectory(cwd);

        try
        {
            await using var runtime = await DurableRuntime.OpenAsync(new OpenExperimentalOptions
            {
                Cwd = cwd,
                AgentDirectory = Path.Combine(root, "agent"),
            });

            var view = runtime.View.Current;
            Assert.Equal(Path.GetFullPath(cwd), view.Session.Cwd);
            Assert.Contains(Path.Join("experimental", "durable-sessions"), view.Session.Directory, StringComparison.Ordinal);
            Assert.Equal("main", Assert.Single(view.Conversations).Label);
            Assert.NotEmpty(view.Models);
            // TS 在 openDurable 末尾 toggleTasks()：面板默认打开，/tasks 收起。
            Assert.NotNull(view.Tasks);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CloseReleasesTheLockAndContinueReopensTheSameSession()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pi-durable-runtime-tests-{Guid.NewGuid():N}");
        var cwd = Path.Combine(root, "work");
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(cwd);

        try
        {
            var first = await DurableRuntime.OpenAsync(new OpenExperimentalOptions
            {
                Cwd = cwd,
                AgentDirectory = agentDirectory,
            });
            string sessionId;
            try
            {
                sessionId = first.View.Current.Session.Id;
            }
            finally
            {
                await first.CloseAsync();
            }

            // 关闭后锁已释放，另一个进程（这里是下一次打开）能拿到同一会话。
            await using var continued = await DurableRuntime.OpenAsync(new OpenExperimentalOptions
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

    [Fact]
    public void CommandLineAcceptsOnlyTheContinueFlags()
    {
        Assert.True(DurableCommandLine.Parse(["--continue"]).ContinueSession);
        Assert.True(DurableCommandLine.Parse(["-c"]).ContinueSession);
        Assert.False(DurableCommandLine.Parse([]).ContinueSession);
        var error = Assert.Throws<ArgumentException>(() => DurableCommandLine.Parse(["--resume"]));
        Assert.Contains("Unknown argument: --resume", error.Message, StringComparison.Ordinal);
    }
}
