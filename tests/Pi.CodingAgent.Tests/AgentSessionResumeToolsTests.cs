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

    /// <summary>
    /// 追加一条记录 loadout 增删的 system 消息，就像一轮 run 会写进去的那样。返回条目 id。
    /// 对应 TS <c>recordLoadoutChange()</c>。
    /// </summary>
    private static string RecordLoadoutChange(
        SessionManager manager, IReadOnlyList<string> added, IReadOnlyList<string>? removed = null)
        => manager.AppendMessage(new SystemMessage(
            MessageContent.From(""),
            ToolsAdded: added.Count > 0 ? [.. added.Select(name => new AiToolDefinition(name, name, EmptySchema()))] : null,
            ToolsRemoved: removed is { Count: > 0 } ? removed : null));

    /// <summary>一条记录着工具集的会话，就像一轮真实的 run 会写进去的那样。</summary>
    private SessionManager RecordedSession(params string[] toolNames)
    {
        var manager = SessionManager.InMemory(_cwd);
        RecordLoadoutChange(manager, toolNames);
        return manager;
    }

    /// <summary>记录过 <c>read</c> / <c>grep</c> / <c>inactive_tool</c>，并关掉了 <c>active_tool</c> 的会话。</summary>
    private SessionManager ChangedSession()
    {
        var manager = RecordedSession("read", "grep", "active_tool");
        RecordLoadoutChange(manager, ["inactive_tool"], ["active_tool"]);
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
        var session = await ResumeAsync(ChangedSession());

        Assert.Equal(["grep", "inactive_tool", "read"], SortedActiveTools(session));
    }

    /// <summary>
    /// 分支从未加过或删过的扩展工具是记录 loadout 之后才注册的，因此按新注册的工具激活；
    /// 内建工具与 <c>defaultActive: false</c> 的工具保持不活跃。上游 #11062。
    /// </summary>
    [Fact]
    public async Task ActivatesExtensionToolsTheTranscriptNeverAddedOrRemoved()
    {
        var session = await ResumeAsync(RecordedSession("read", "grep"));

        Assert.Equal(["active_tool", "grep", "read"], SortedActiveTools(session));
    }

    /// <summary>压缩之前被删掉的工具保持不活跃：搜索的是整条分支，不只是压缩之后的系统消息。</summary>
    [Fact]
    public async Task KeepsToolsRemovedBeforeACompactionInactive()
    {
        var manager = ChangedSession();
        var kept = manager.AppendMessage(new UserMessage("hi", 1));
        manager.AppendCompaction("summary", kept, 1000);

        var session = await ResumeAsync(manager);

        Assert.Equal(["grep", "inactive_tool", "read"], SortedActiveTools(session));
    }

    /// <summary>上游 #11075：切树后按目标分支的 loadout 恢复，而不是原来的叶子。</summary>
    [Fact]
    public async Task RestoresTheTargetBranchesLoadoutOnTreeNavigation()
    {
        // 目标分支从未提及 active_tool，因此在那里它是活跃的；它删掉了 grep。
        var manager = RecordedSession("read", "grep");
        RecordLoadoutChange(manager, [], ["grep"]);
        var target = manager.AppendMessage(new UserMessage("first", 1));
        RecordLoadoutChange(manager, ["grep"], ["active_tool"]);

        var session = await ResumeAsync(manager);
        Assert.Equal(["grep", "read"], SortedActiveTools(session));

        await session.NavigateTreeAsync(target, new AgentSession.NavigateTreeSessionOptions { Summarize = false });

        Assert.Equal(["active_tool", "read"], SortedActiveTools(session));
    }

    /// <summary>不在 tools allowlist 里的扩展工具即使已注册也保持不活跃，切树后同样如此。</summary>
    [Fact]
    public async Task KeepsRegisteredToolsOutsideTheToolsAllowlistInactiveOnTreeNavigation()
    {
        var manager = RecordedSession("read");
        var target = manager.AppendMessage(new UserMessage("first", 1));

        var session = await ResumeAsync(manager, allowedToolNames: ["read"]);
        Assert.Equal(["read"], SortedActiveTools(session));

        await session.NavigateTreeAsync(target, new AgentSession.NavigateTreeSessionOptions { Summarize = false });

        Assert.Equal(["read"], SortedActiveTools(session));
    }

    [Fact]
    public async Task DoesNotRestoreExcludedTools()
    {
        var session = await ResumeAsync(
            ChangedSession(),
            excludedToolNames: ["inactive_tool"]);

        Assert.Equal(["grep", "read"], SortedActiveTools(session));
    }

    [Fact]
    public async Task ExplicitToolOptionsWinOverTheRecordedLoadout()
    {
        // 显式传 tools 的调用方要的就是这份列表：UsesDefaultTools 为 false，整段恢复逻辑不跑。
        // `--tools` 同时会设 allowlist，扩展工具因此被挡在外面（与 SDK 的 ResolveTools 一致）。
        var session = await ResumeAsync(
            ChangedSession(),
            initialActiveToolNames: ["read", "write"],
            usesDefaultTools: false,
            allowedToolNames: ["read", "write"]);

        Assert.Equal(["read", "write"], SortedActiveTools(session));
    }

    [Fact]
    public async Task AppliesToolModifiersToTheRecordedLoadout()
    {
        var session = await ResumeAsync(
            ChangedSession(),
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
