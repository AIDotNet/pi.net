using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// The pure logic of <see cref="ExperimentalServer"/>: the serialized model options the spawned
/// server parses, the internal process entry's argument validation, and directory resolution.
/// </summary>
public class ServerRuntimeTests
{
    private const string ServerId = "3f0d1c2a-9b4e-4d1f-8a77-5c6b7d8e9f02";

    // ---------------------------------------------------------------- parseServerModelOptions

    [Fact]
    public void ParseServerModelOptions_AcceptsTheTwoDocumentedShapes()
    {
        Assert.Null(ExperimentalServer.ParseServerModelOptions(null));
        Assert.Equal((null, "gpt-5"), ExperimentalServer.ParseServerModelOptions("""{"model":"gpt-5"}"""));
        Assert.Equal(
            ("openai", "gpt-5"),
            ExperimentalServer.ParseServerModelOptions("""{"provider":"openai","model":"gpt-5"}"""));
    }

    [Fact]
    public void ParseServerModelOptions_RejectsEverythingElse()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ExperimentalServer.ParseServerModelOptions("not json"));
        Assert.Throws<InvalidOperationException>(() =>
            ExperimentalServer.ParseServerModelOptions("null"));
        Assert.Throws<InvalidOperationException>(() =>
            ExperimentalServer.ParseServerModelOptions("42"));
        Assert.Throws<InvalidOperationException>(() =>
            ExperimentalServer.ParseServerModelOptions(""""["model"]""""));
        // Unknown keys, a missing or non-string model, an empty model, and an empty provider.
        Assert.Throws<InvalidOperationException>(() =>
            ExperimentalServer.ParseServerModelOptions("""{"model":"m","extra":1}"""));
        Assert.Throws<InvalidOperationException>(() =>
            ExperimentalServer.ParseServerModelOptions("""{"provider":"p"}"""));
        Assert.Throws<InvalidOperationException>(() =>
            ExperimentalServer.ParseServerModelOptions("""{"model":7}"""));
        Assert.Throws<InvalidOperationException>(() =>
            ExperimentalServer.ParseServerModelOptions("""{"model":""}"""));
        Assert.Throws<InvalidOperationException>(() =>
            ExperimentalServer.ParseServerModelOptions("""{"model":"m","provider":""}"""));
        Assert.Throws<InvalidOperationException>(() =>
            ExperimentalServer.ParseServerModelOptions("""{"model":null}"""));
    }

    // ---------------------------------------------------------------- runServerProcess validation

    [Fact]
    public async Task RunServerProcess_ValidatesItsArgumentsBeforeStarting()
    {
        var directory = Path.GetTempPath();
        var sessionDir = Path.Combine(Path.GetTempPath(), "sessions");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExperimentalServer.RunServerProcessAsync([]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExperimentalServer.RunServerProcessAsync(["relative", ServerId, sessionDir]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExperimentalServer.RunServerProcessAsync([directory, "not-a-server-id", sessionDir]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExperimentalServer.RunServerProcessAsync([directory, ServerId, "relative"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExperimentalServer.RunServerProcessAsync([directory, ServerId, sessionDir, "{}", "extra"]));
    }

    // ---------------------------------------------------------------- directory resolution

    [Fact]
    public void ResolveServerDirectory_PrefersTheExplicitDirectoryThenTheEnvironment()
    {
        var explicitDirectory = Path.Combine(Path.GetTempPath(), "explicit-server-dir");
        Assert.Equal(explicitDirectory, ExperimentalServer.ResolveServerDirectory(explicitDirectory));

        var envDirectory = Path.Combine(Path.GetTempPath(), "env-server-dir");
        var previous = Environment.GetEnvironmentVariable(ExperimentalServer.EnvServerDir);
        Environment.SetEnvironmentVariable(ExperimentalServer.EnvServerDir, envDirectory);
        try
        {
            Assert.Equal(envDirectory, ExperimentalServer.ResolveServerDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalServer.EnvServerDir, previous);
        }
    }

    [Fact]
    public void ResolveServerDirectory_FallsBackToTheHomeServerDirectory()
    {
        var previous = Environment.GetEnvironmentVariable(ExperimentalServer.EnvServerDir);
        Environment.SetEnvironmentVariable(ExperimentalServer.EnvServerDir, null);
        try
        {
            var home = Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(home, ".pi", "server")),
                ExperimentalServer.ResolveServerDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalServer.EnvServerDir, previous);
        }
    }

    [Fact]
    public void ResolveSessionDirectory_PrefersTheExplicitDirectoryThenTheAgentDirectory()
    {
        var explicitDirectory = Path.Combine(Path.GetTempPath(), "explicit-session-dir");
        Assert.Equal(explicitDirectory, ExperimentalServer.ResolveSessionDirectory(explicitDirectory));

        var agentDir = Path.Combine(Path.GetTempPath(), "agent-dir");
        var previous = Environment.GetEnvironmentVariable(Config.EnvAgentDir);
        Environment.SetEnvironmentVariable(Config.EnvAgentDir, agentDir);
        try
        {
            Assert.Equal(
                Path.Combine(agentDir, "experimental", "sessions"),
                ExperimentalServer.ResolveSessionDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Config.EnvAgentDir, previous);
        }
    }

    [Fact]
    public async Task EnsurePrivateServerDirectory_CreatesTheDirectoryIdempotently()
    {
        var directory = Path.Combine(Path.GetTempPath(), "psv-private-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await ExperimentalServer.EnsurePrivateServerDirectoryAsync(directory);
            Assert.True(Directory.Exists(directory));

            await ExperimentalServer.EnsurePrivateServerDirectoryAsync(directory);
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Teardown must not mask the test result.
            }
        }
    }
}
