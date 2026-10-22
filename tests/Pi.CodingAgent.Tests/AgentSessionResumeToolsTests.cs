// ============================================================================
// Resuming a session restores the tool loadout its transcript records.
//
// Upstream 6f2f164ab (#10887, fixes #10785): the SDK always passes initial tools, so the
// constructor never restored the recorded loadout and a resumed session silently dropped tools
// that had been activated during the run. The default-tools path now restores it, with the
// `--tools` `+name`/`-name` modifiers applied on top.
// ============================================================================

using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

// 扩展契约在 Pi.CodingAgent.Core.Extensions，与 Pi.Ai.Types 有同名类型，故显式别名。
using AiToolDefinition = Pi.Ai.Types.ToolDefinition;

namespace Pi.CodingAgent.Tests;

public sealed class AgentSessionResumeToolsTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;

    public AgentSessionResumeToolsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-resume-tools-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_root, "cwd");
        _agentDir = Path.Combine(_cwd, ".pi");
        Directory.CreateDirectory(_agentDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    // ------------------------------------------------------------------ helpers

    private static ToolSchema EmptySchema() => new(new Dictionary<string, object?>());

    private static StreamFn ThrowingStreamFn() =>
        (_, _, _, _) => throw new InvalidOperationException("The agent loop must not run in these tests.");

    private static Pi.CodingAgent.Core.Extensions.ToolDefinition Tool(string name, bool defaultActive = true) => new()
    {
        Name = name,
        Label = name,
        Description = $"tool {name}",
        Parameters = EmptySchema(),
        DefaultActive = defaultActive,
        Execute = static (_, _, _, _, _) => Task.FromResult(new AgentToolResult([])),
    };

    /// <summary>两个扩展工具：一个注册即激活，一个注册但不激活（对应上游的 active/inactive_tool）。</summary>
    private static InlineExtension ExtensionTools() => new InlineExtension.Described("resume-tools", pi =>
    {
        pi.RegisterTool(Tool("inactive_tool", defaultActive: false));
        pi.RegisterTool(Tool("active_tool"));
        return Task.CompletedTask;
    });

    /// <summary>一条记录着工具集的会话，就像一轮真实的 run 会写进去的那样。</summary>
    private SessionManager RecordedSession(params string[] toolNames)
    {
        var manager = SessionManager.InMemory(_cwd);
        manager.AppendMessage(new SystemMessage(
            MessageContent.From(""),
            ToolsAdded: [.. toolNames.Select(name => new AiToolDefinition(name, name, EmptySchema()))]));
        return manager;
    }

    private async Task<AgentSession> ResumeAsync(
        SessionManager sessionManager,
        IReadOnlyList<string>? initialActiveToolNames = null,
        bool usesDefaultTools = true,
        IReadOnlyList<string>? defaultToolModifiers = null,
        IReadOnlyList<string>? allowedToolNames = null,
        IReadOnlyList<string>? excludedToolNames = null,
        bool withExtensionTools = true)
    {
        var settings = SettingsManager.Create(_cwd, _agentDir);
        var loader = new DefaultResourceLoader(
            _cwd,
            _agentDir,
            settings,
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true,
            extensionFactories: withExtensionTools ? [ExtensionTools()] : null);
        await loader.ReloadAsync();
        var modelRuntime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new InMemoryCredentialStore(),
            ModelsPathDisabled = true,
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            RefreshOnCreate = false,
            Env = name => name == "PI_OFFLINE" ? "1" : null,
            AuthContext = new StubAuthContext(name => name == "PI_OFFLINE" ? "1" : null),
        });
        var agent = new Pi.Agent.Agent(new AgentOptions { StreamFn = ThrowingStreamFn() });
        return new AgentSession(new AgentSessionConfig
        {
            Agent = agent,
            SessionManager = sessionManager,
            SettingsManager = settings,
            Cwd = _cwd,
            ResourceLoader = loader,
            ModelRuntime = modelRuntime,
            InitialActiveToolNames = initialActiveToolNames,
            UsesDefaultTools = usesDefaultTools,
            DefaultToolModifiers = defaultToolModifiers,
            AllowedToolNames = allowedToolNames,
            ExcludedToolNames = excludedToolNames,
            ExtensionRunnerRef = new ExtensionRunnerRef(),
        });
    }

    private static IReadOnlyList<string> SortedActiveTools(AgentSession session)
        => [.. session.GetActiveToolNames().OrderBy(name => name, StringComparer.Ordinal)];

    // ------------------------------------------------------------------ upstream cases

    [Fact]
    public async Task RestoresTheLoadoutTheTranscriptRecords()
    {
        // 会话期间 active_tool 被关掉、inactive_tool 被打开：恢复时应回到记录的这份。
        var session = await ResumeAsync(RecordedSession("read", "grep", "inactive_tool"));

        Assert.Equal(["grep", "inactive_tool", "read"], SortedActiveTools(session));
    }

    [Fact]
    public async Task DoesNotRestoreExcludedTools()
    {
        var session = await ResumeAsync(
            RecordedSession("read", "grep", "inactive_tool"),
            excludedToolNames: ["inactive_tool"]);

        Assert.Equal(["grep", "read"], SortedActiveTools(session));
    }

    [Fact]
    public async Task ExplicitToolOptionsWinOverTheRecordedLoadout()
    {
        // 显式传 tools 的调用方要的就是这份列表：UsesDefaultTools 为 false，整段恢复逻辑不跑。
        // `--tools` 同时会设 allowlist，扩展工具因此被挡在外面（与 SDK 的 ResolveTools 一致）。
        var session = await ResumeAsync(
            RecordedSession("read", "grep", "inactive_tool"),
            initialActiveToolNames: ["read", "write"],
            usesDefaultTools: false,
            allowedToolNames: ["read", "write"]);

        Assert.Equal(["read", "write"], SortedActiveTools(session));
    }

    [Fact]
    public async Task AppliesToolModifiersToTheRecordedLoadout()
    {
        var session = await ResumeAsync(
            RecordedSession("read", "grep", "inactive_tool"),
            defaultToolModifiers: ["-grep", "+write"]);

        Assert.Equal(["inactive_tool", "read", "write"], SortedActiveTools(session));
    }

    [Fact]
    public async Task FallsBackToTheDefaultToolsWhenTheTranscriptRecordsNoLoadout()
    {
        var manager = SessionManager.InMemory(_cwd);
        manager.AppendMessage(new UserMessage("hi", 1));

        var session = await ResumeAsync(manager);

        Assert.Equal(["active_tool", "bash", "edit", "read", "write"], SortedActiveTools(session));
    }

    [Fact]
    public async Task WithoutDefaultToolsTheRecordedLoadoutIsIgnored()
    {
        // 反向断言：这条用例锁住「恢复只在 defaultTools 路径发生」，避免以后把条件放宽后
        // 悄悄改掉「显式 tools 优先」的语义。
        var session = await ResumeAsync(
            RecordedSession("read", "grep", "inactive_tool"),
            initialActiveToolNames: [],
            usesDefaultTools: false,
            withExtensionTools: false);

        Assert.Empty(session.GetActiveToolNames());
    }
}
