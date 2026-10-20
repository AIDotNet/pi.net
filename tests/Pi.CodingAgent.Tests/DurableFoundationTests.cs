using Pi.CodingAgent.Core;
using Pi.CodingAgent.Experimental;
using Pi.CodingAgent.Experimental.Durable;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// durable 实验模块的基础层：会话目录（与 vacation 共用一份实现，只有目录名与文案不同）、
/// 每目录一个执行环境、编码注册表与 subagent 工具的注册面。
/// </summary>
public sealed class DurableFoundationTests
{
    [Fact]
    public async Task DurableSessionsUseTheirOwnDirectoryNameAndLockFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cwd = Path.Combine(root, "work");
            var agentDirectory = Path.Combine(root, "agent");
            Directory.CreateDirectory(cwd);

            await using var session = await ExperimentalSessions.SelectAsync(
                ExperimentalSessionKind.Durable, cwd, continueSession: false, agentDirectory);

            Assert.True(session.Created);
            Assert.Contains(
                Path.Join("experimental", "durable-sessions"),
                session.Directory,
                StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(session.Directory, ".durable.lock")));
            Assert.Equal(Path.Combine(session.Directory, "session.sqlite"), session.Database);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DurableContinueWithoutSessionNamesDurableInTheMessage()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cwd = Path.Combine(root, "work");
            Directory.CreateDirectory(cwd);

            var missing = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ExperimentalSessions.SelectAsync(
                    ExperimentalSessionKind.Durable, cwd, continueSession: true, Path.Combine(root, "agent")));
            Assert.Contains("No durable session exists", missing.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CodingRegistryInstallsCodingToolsAndThePiPrompt()
    {
        var settings = SettingsManager.Create(Environment.CurrentDirectory);
        var registry = DurableHarnessSetup.CreateCodingRegistry(settings, Environment.CurrentDirectory);
        var snapshot = registry.Snapshot();

        Assert.NotNull(snapshot.Extension("coding-tools"));
        Assert.NotNull(snapshot.Extension("pi-prompt"));
        // pi 的四个编码工具都在；subagent 由运行时另外装上。
        Assert.Equal(
            new[] { "read", "write", "edit", "bash" },
            snapshot.Tools().Select(entry => entry.Tool.Name));
    }

    [Fact]
    public void SubagentToolRegistersOneReplaySafeToolTakingATask()
    {
        var extension = SubagentExtension.Instance;
        Assert.Equal("subagent", extension.Name);
        var tool = Assert.Single(extension.Tools!);
        Assert.Equal("subagent", tool.Name);
        Assert.Equal("safe", tool.Replay);
        Assert.Contains("subagent", tool.Description, StringComparison.OrdinalIgnoreCase);
        // 参数只有一个必填的 task。
        var schema = tool.Parameters.JsonSchema;
        Assert.Equal("object", ((System.Text.Json.Nodes.JsonValue)schema["type"]!).GetValue<string>());
        Assert.True(((System.Text.Json.Nodes.JsonObject)schema["properties"]!).ContainsKey("task"));
        Assert.Equal("task", ((System.Text.Json.Nodes.JsonArray)schema["required"]!)[0]!.GetValue<string>());
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pi-durable-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
