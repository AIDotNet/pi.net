using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 会话装配层的纯逻辑：<c>core/agent-session-services.ts</c> 的 flag 应用与路径解析，
/// 以及 <c>core/agent-session-runtime.ts</c> 本批新增的守卫 / 事件构造（4e-3e-9f）。
/// </summary>
public sealed class AgentSessionServicesTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-asvc-" + Guid.NewGuid().ToString("N")[..8]);

    public AgentSessionServicesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------------
    // 路径解析
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveServicePaths_ResolvesExplicitAgentDir()
    {
        var cwd = Path.Combine(_root, "proj");
        var agentDir = Path.Combine(_root, "agent");

        var paths = AgentSessionServicesBuilder.ResolveServicePaths(cwd, agentDir, "/default-agent");

        Assert.Equal(Paths.ResolvePath(cwd), paths.Cwd);
        Assert.Equal(Paths.ResolvePath(agentDir), paths.AgentDir);
    }

    [Fact]
    public void ResolveServicePaths_FallsBackToDefaultAgentDirUnresolved()
    {
        var cwd = Path.Combine(_root, "proj");

        var paths = AgentSessionServicesBuilder.ResolveServicePaths(cwd, null, "/default-agent");

        // TS：`options.agentDir ? resolvePath(...) : getAgentDir()`——缺省分支不 resolve。
        Assert.Equal("/default-agent", paths.AgentDir);
    }

    [Fact]
    public void ResolveModelRuntimeFilePaths_OnlyWhenAgentDirProvided()
    {
        var agentDir = Path.Combine(_root, "agent");

        var provided = AgentSessionServicesBuilder.ResolveModelRuntimeFilePaths(true, agentDir);
        Assert.Equal(Path.Combine(agentDir, "auth.json"), provided.AuthPath);
        Assert.Equal(Path.Combine(agentDir, "models.json"), provided.ModelsPath);

        // createAgentSession 与 createAgentSessionServices 不同：缺省时交给 ModelRuntime 自己的默认值。
        var omitted = AgentSessionServicesBuilder.ResolveModelRuntimeFilePaths(false, agentDir);
        Assert.Null(omitted.AuthPath);
        Assert.Null(omitted.ModelsPath);
    }

    // ---------------------------------------------------------------------
    // 扩展 flag
    // ---------------------------------------------------------------------

    [Fact]
    public void BuildRegisteredFlagTypes_LaterExtensionsOverrideEarlier()
    {
        var extensions = new[]
        {
            ExtensionWithFlags("a.ts", ("verbose", "boolean"), ("token", "string")),
            ExtensionWithFlags("b.ts", ("token", "boolean")),
        };

        var registered = AgentSessionServicesBuilder.BuildRegisteredFlagTypes(extensions);

        Assert.Equal(2, registered.Count);
        Assert.Equal("boolean", registered["verbose"]);
        // b.ts 覆盖 a.ts。
        Assert.Equal("boolean", registered["token"]);
    }

    [Fact]
    public void ApplyExtensionFlagValues_BooleanFlagIsForcedTrue()
    {
        var resolution = AgentSessionServicesBuilder.ApplyExtensionFlagValues(
            new Dictionary<string, string> { ["verbose"] = "boolean" },
            [new("verbose", ExtensionFlagInput.Boolean)]);

        Assert.Empty(resolution.Diagnostics);
        Assert.True((bool)resolution.FlagValues["verbose"]);
    }

    [Fact]
    public void ApplyExtensionFlagValues_StringFlagKeepsValue()
    {
        var resolution = AgentSessionServicesBuilder.ApplyExtensionFlagValues(
            new Dictionary<string, string> { ["token"] = "string" },
            [new("token", ExtensionFlagInput.Text("abc"))]);

        Assert.Empty(resolution.Diagnostics);
        Assert.Equal("abc", resolution.FlagValues["token"]);
    }

    [Fact]
    public void ApplyExtensionFlagValues_StringFlagWithoutValueReportsError()
    {
        // CLI 只写了 `--token`（布尔分支），但注册类型是 string → TS 报「需要取值」。
        var resolution = AgentSessionServicesBuilder.ApplyExtensionFlagValues(
            new Dictionary<string, string> { ["token"] = "string" },
            [new("token", ExtensionFlagInput.Boolean)]);

        var diagnostic = Assert.Single(resolution.Diagnostics);
        Assert.Equal(AgentSessionDiagnosticType.Error, diagnostic.Type);
        Assert.Equal("Extension flag \"--token\" requires a value", diagnostic.Message);
        Assert.Empty(resolution.FlagValues);
    }

    [Fact]
    public void ApplyExtensionFlagValues_UnknownFlagsReportedLast()
    {
        var resolution = AgentSessionServicesBuilder.ApplyExtensionFlagValues(
            new Dictionary<string, string> { ["token"] = "string" },
            [
                new("nope", ExtensionFlagInput.Boolean),
                new("token", ExtensionFlagInput.Boolean),
                new("alsoNope", ExtensionFlagInput.Text("x")),
            ]);

        Assert.Equal(2, resolution.Diagnostics.Count);
        // 顺序：先按输入顺序报「缺值」，最后统一报「未知选项」。
        Assert.Equal("Extension flag \"--token\" requires a value", resolution.Diagnostics[0].Message);
        Assert.Equal("Unknown options: --nope, --alsoNope", resolution.Diagnostics[1].Message);
    }

    [Fact]
    public void ApplyExtensionFlagValues_SingleUnknownUsesSingularWording()
    {
        var resolution = AgentSessionServicesBuilder.ApplyExtensionFlagValues(
            new Dictionary<string, string>(),
            [new("nope", ExtensionFlagInput.Boolean)]);

        var diagnostic = Assert.Single(resolution.Diagnostics);
        Assert.Equal("Unknown option: --nope", diagnostic.Message);
    }

    [Fact]
    public void ApplyExtensionFlagValues_EmptyInputIsNoOp()
    {
        var resolution = AgentSessionServicesBuilder.ApplyExtensionFlagValues(
            new Dictionary<string, string> { ["verbose"] = "boolean" },
            []);

        Assert.Empty(resolution.Diagnostics);
        Assert.Empty(resolution.FlagValues);
    }

    [Fact]
    public void RegistrationFailureDiagnostic_QuotesExtensionPath()
    {
        var diagnostic = AgentSessionServicesBuilder.RegistrationFailureDiagnostic("/x/ext.ts", "boom");

        Assert.Equal(AgentSessionDiagnosticType.Error, diagnostic.Type);
        Assert.Equal("Extension \"/x/ext.ts\" error: boom", diagnostic.Message);
    }

    // ---------------------------------------------------------------------
    // 运行时守卫与事件构造
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveImportDestination_DeduplicatesAndReportsStoredSource()
    {
        var sessionDir = Path.Combine(_root, "sessions");
        var sourceDir = Path.Combine(_root, "incoming");
        Directory.CreateDirectory(sourceDir);
        var source = Path.Combine(sourceDir, "session.jsonl");
        File.WriteAllText(source, "{}\n");

        var first = AgentSessionRuntime.ResolveImportDestination(sessionDir, source);
        Assert.Equal(Path.Combine(sessionDir, "session.jsonl"), first.DestinationPath);
        Assert.False(first.SourceAlreadyStored);
        // 目录被创建（TS 的 mkdirSync recursive）。
        Assert.True(Directory.Exists(sessionDir));

        File.WriteAllText(first.DestinationPath, "occupied");
        var second = AgentSessionRuntime.ResolveImportDestination(sessionDir, source);
        Assert.Equal(Path.Combine(sessionDir, "session-1.jsonl"), second.DestinationPath);
        Assert.False(second.SourceAlreadyStored);

        // 源已在会话目录里 → 目标就是源本身，不复制。
        var third = AgentSessionRuntime.ResolveImportDestination(sessionDir, first.DestinationPath);
        Assert.Equal(first.DestinationPath, third.DestinationPath);
        Assert.True(third.SourceAlreadyStored);
    }

    [Fact]
    public void ResolveBeforeHookCancellation_OnlyCancelsWithHandlerAndExplicitTrue()
    {
        Assert.False(AgentSessionRuntime.ResolveBeforeHookCancellation(hasHandlers: false, cancel: true));
        Assert.False(AgentSessionRuntime.ResolveBeforeHookCancellation(hasHandlers: true, cancel: null));
        Assert.False(AgentSessionRuntime.ResolveBeforeHookCancellation(hasHandlers: true, cancel: false));
        Assert.True(AgentSessionRuntime.ResolveBeforeHookCancellation(hasHandlers: true, cancel: true));
    }

    [Fact]
    public void ResolveForkPosition_DefaultsToBefore()
    {
        Assert.Equal("before", AgentSessionRuntime.ResolveForkPosition(null));
        Assert.Equal("before", AgentSessionRuntime.ResolveForkPosition("before"));
        Assert.Equal("at", AgentSessionRuntime.ResolveForkPosition("at"));
    }

    [Fact]
    public void RequireForkedSessionPath_ThrowsWhenMissing()
    {
        Assert.Equal("/sessions/fork.jsonl", AgentSessionRuntime.RequireForkedSessionPath("/sessions/fork.jsonl"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentSessionRuntime.RequireForkedSessionPath(null));
        Assert.Equal("Failed to create forked session", ex.Message);
    }

    [Fact]
    public void BuildSessionEvents_CarryReasonAndFile()
    {
        var start = AgentSessionRuntime.BuildSessionStartEvent(SessionEventReason.Resume, "/old.jsonl");
        Assert.Equal("resume", start.Reason);
        Assert.Equal("/old.jsonl", start.PreviousSessionFile);

        var startWithoutPrevious = AgentSessionRuntime.BuildSessionStartEvent(SessionEventReason.Startup);
        Assert.Null(startWithoutPrevious.PreviousSessionFile);

        var shutdown = AgentSessionRuntime.BuildSessionShutdownEvent(SessionEventReason.Quit);
        Assert.Equal("quit", shutdown.Reason);
        Assert.Null(shutdown.TargetSessionFile);
    }

    private static Extension ExtensionWithFlags(string path, params (string Name, string Type)[] flags)
    {
        var extension = new Extension
        {
            Path = path,
            ResolvedPath = path,
            SourceInfo = new SourceInfo
            {
                Path = path,
                Source = "local",
                Scope = "project",
                Origin = "top-level",
            },
        };

        foreach (var (name, type) in flags)
        {
            extension.Flags[name] = new ExtensionFlag
            {
                Name = name,
                Type = type,
                ExtensionPath = path,
            };
        }

        return extension;
    }
}
