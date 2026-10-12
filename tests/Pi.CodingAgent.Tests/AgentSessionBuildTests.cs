using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Tools;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 4e-3e-9h-2c：<c>core/tools/index.ts</c>（ToolIndex）、<c>core/tools/powershell.ts</c>
/// 的收尾，与 <c>AgentSession</c> 运行时装配（构造器字段区 / <c>_buildRuntime</c> /
/// <c>_refreshToolRegistry</c> / <c>_bindExtensionCore</c> / <c>_applyExtensionBindings</c>）。
/// </summary>
public sealed class AgentSessionBuildTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;

    public AgentSessionBuildTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-agent-session-build-" + Guid.NewGuid().ToString("N"));
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

    private static StreamFn ThrowingStreamFn() =>
        (_, _, _, _) => throw new InvalidOperationException("The agent loop must not run in these tests.");

    private async Task<AgentSession> CreateSessionAsync(
        Func<AgentSessionConfig, AgentSessionConfig>? configure = null,
        IReadOnlyList<string>? initialActiveToolNames = null)
    {
        var settings = SettingsManager.Create(_cwd, _agentDir);
        var loader = new DefaultResourceLoader(_cwd, _agentDir, settings);
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
        var runnerRef = new ExtensionRunnerRef();
        var config = new AgentSessionConfig
        {
            Agent = agent,
            SessionManager = SessionManager.InMemory(_cwd),
            SettingsManager = settings,
            Cwd = _cwd,
            ResourceLoader = loader,
            ModelRuntime = modelRuntime,
            InitialActiveToolNames = initialActiveToolNames,
            ExtensionRunnerRef = runnerRef,
        };
        config = configure?.Invoke(config) ?? config;
        var session = new AgentSession(config);
        Assert.NotNull(runnerRef.Current);
        return session;
    }

    // ------------------------------------------------------------------ ToolIndex（core/tools/index.ts）

    [Fact]
    public void ToolIndex_AllToolNames_MatchesTsInsertionOrder()
    {
        Assert.Equal(
            (IReadOnlyList<string>)["read", "bash", "powershell", "edit", "write", "grep", "find", "ls"],
            ToolIndex.AllToolNames);
    }

    [Fact]
    public void ToolIndex_CreateToolDefinition_AllEightBuiltIns()
    {
        foreach (var name in ToolIndex.AllToolNames)
        {
            Assert.Equal(name, ToolIndex.CreateToolDefinition(name, _cwd).Name);
        }
    }

    [Fact]
    public void ToolIndex_CreateTool_AllEightBuiltIns()
    {
        foreach (var name in ToolIndex.AllToolNames)
        {
            Assert.Equal(name, ToolIndex.CreateTool(name, _cwd).Name);
        }
    }

    [Fact]
    public void ToolIndex_UnknownToolName_ThrowsLikeTsDefault()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ToolIndex.CreateToolDefinition("nope", _cwd));
        Assert.Equal("Unknown tool name: nope", ex.Message);
        Assert.Throws<InvalidOperationException>(() => ToolIndex.CreateTool("nope", _cwd));
    }

    [Fact]
    public void ToolIndex_CodingAndReadOnlySets_MatchTsSplits()
    {
        Assert.Equal(["read", "bash", "edit", "write"], ToolIndex.CreateCodingToolDefinitions(_cwd).Select(d => d.Name));
        Assert.Equal(["read", "grep", "find", "ls"], ToolIndex.CreateReadOnlyToolDefinitions(_cwd).Select(d => d.Name));
        Assert.Equal(["read", "bash", "edit", "write"], ToolIndex.CreateCodingTools(_cwd).Select(t => t.Name));
        Assert.Equal(["read", "grep", "find", "ls"], ToolIndex.CreateReadOnlyTools(_cwd).Select(t => t.Name));
    }

    [Fact]
    public void ToolIndex_CreateAllFactories_KeepTsInsertionOrder()
    {
        var definitions = ToolIndex.CreateAllToolDefinitions(_cwd);
        var tools = ToolIndex.CreateAllTools(_cwd);
        Assert.Equal(ToolIndex.AllToolNames, (IReadOnlyList<string>)[.. definitions.Keys]);
        Assert.Equal(ToolIndex.AllToolNames, (IReadOnlyList<string>)[.. tools.Keys]);
    }

    // ------------------------------------------------------------------ PowerShellTool（core/tools/powershell.ts）

    private sealed class CapturingOperations : IBashToolOperations
    {
        public string? Received { get; private set; }

        public Task<ShellExecResult> ExecAsync(
            string command,
            string cwd,
            ShellExecOptions options,
            CancellationToken cancellationToken)
        {
            Received = command;
            return Task.FromResult(new ShellExecResult(0));
        }
    }

    [Fact]
    public void PowerShellTool_Contribution_MatchesTs()
    {
        Assert.Equal("Execute PowerShell commands", PowerShellTool.Snippet);
        Assert.Equal(
            (IReadOnlyList<string>)["You can inspect PI_* environment variables for current model and session details."],
            PowerShellTool.Guidelines);
    }

    [Fact]
    public async Task PowerShellTool_LocalOperations_RunRealPowershellWithUtf8Prefix()
    {
        // 真实 PowerShell 冒烟：UTF-8 前缀必须原样可用（TS UTF8_OUTPUT_PREFIX）。
        var operations = PowerShellTool.CreateLocalPowerShellOperations();
        var result = await operations.ExecAsync(
            "Get-Location",
            _cwd,
            new ShellExecOptions { OnData = _ => { }, Timeout = 30_000 },
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void PowerShellTool_Definition_UsesPowershellNameAndSnippet()
    {
        var definition = PowerShellTool.CreatePowerShellToolDefinition(_cwd);
        Assert.Equal("powershell", definition.Name);
        Assert.Equal("Execute PowerShell commands", definition.PromptSnippet);
        Assert.Equal(PowerShellTool.Guidelines, definition.PromptGuidelines);
    }

    [Fact]
    public void PowerShellTool_CustomOperations_PassthroughWithoutPrefixWrapping()
    {
        // TS: options?.operations ?? createLocalPowerShellOperations()——自定义 operations 原样携带。
        var marker = new CapturingOperations();
        var definition = PowerShellTool.CreatePowerShellToolDefinition(
            _cwd,
            new PowerShellToolOptions { Operations = marker });
        Assert.Equal("powershell", definition.Name);
    }

    // ------------------------------------------------------------------ AgentSession 运行时装配

    [Fact]
    public async Task Constructor_BuildRuntime_RegistersAllEightBuiltIns_ActivatesDefaultFour()
    {
        var session = await CreateSessionAsync();
        var allNames = session.GetAllTools().Select(tool => tool.Name).ToList();
        Assert.Equal(ToolIndex.AllToolNames, allNames);
        Assert.All(session.GetAllTools(), tool => Assert.Equal(ToolExposure.Direct, tool.Exposure));

        // TS 默认活跃四件套（_buildRuntime 的 defaultActiveToolNames）。
        Assert.Equal(["read", "bash", "edit", "write"], session.GetActiveToolNames());
        // Agent 镜像被刷新（差异 C148）。
        Assert.Equal(["read", "bash", "edit", "write"], [.. session.Agent.Tools.Select(tool => tool.Name)]);
        Assert.NotNull(session.GetToolDefinition("bash"));
    }

    [Fact]
    public async Task Constructor_Allowlist_FiltersRegistryAndActivatesNamedOnly()
    {
        var session = await CreateSessionAsync(config => config with { AllowedToolNames = ["read", "grep"] }, initialActiveToolNames: []);
        Assert.Equal(["read", "grep"], session.GetAllTools().Select(tool => tool.Name));
        // 默认四件套被 allow 过滤剩 read；allowlist 分支再把命名的 grep 激活。
        Assert.Equal(["read", "grep"], session.GetActiveToolNames());
    }

    [Fact]
    public async Task Constructor_Denylist_RemovesToolFromRegistry()
    {
        var session = await CreateSessionAsync(config => config with { ExcludedToolNames = ["bash"] });
        Assert.DoesNotContain("bash", session.GetAllTools().Select(tool => tool.Name));
        Assert.Equal(["read", "edit", "write"], session.GetActiveToolNames());
    }

    [Fact]
    public async Task Constructor_BaseToolsOverride_ReplacesBuiltInDefinitions()
    {
        var custom = new AgentTool(
            "custom-read",
            "custom read",
            new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }),
            "custom-read",
            (_, _, _, _) => Task.FromResult(new AgentToolResult([new TextContent("ok")])));
        var session = await CreateSessionAsync(config => config with { BaseToolsOverride = new Dictionary<string, AgentTool> { ["custom-read"] = custom } });
        Assert.Equal(["custom-read"], session.GetAllTools().Select(tool => tool.Name));
        Assert.Equal(["custom-read"], session.GetActiveToolNames());
    }

    [Fact]
    public async Task SetActiveToolsByName_UpdatesAgentMirror_AndSystemPromptSelection()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        session.SetActiveToolsByName(["read", "grep"]);
        Assert.Equal(["read", "grep"], session.GetActiveToolNames());
        Assert.Equal(["read", "grep"], [.. session.Agent.Tools.Select(tool => tool.Name)]);

        // 系统提示词的 selectedTools 反映活跃集（_rebuildSystemPrompt）。
        Assert.Contains("read", session.SystemPrompt);
    }

    [Fact]
    public async Task SetSessionName_EmitsSessionInfoChanged_AndPersistsToSession()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        var events = new List<AgentSessionEvent>();
        using var subscription = session.Subscribe(events.Add);

        session.SetSessionName("my session");
        Assert.Equal("my session", session.SessionName);
        var changed = Assert.Single(events, e => e is AgentSessionEvent.SessionInfoChangedEvent)
            as AgentSessionEvent.SessionInfoChangedEvent;
        Assert.Equal("my session", changed!.Name);
    }

    [Fact]
    public async Task AppendCustomEntry_WritesTreeEntry()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        var entryId = session.SessionManager.AppendCustomEntry("my-type", new { value = 1 });
        var entry = session.SessionManager.GetEntry(entryId);
        Assert.NotNull(entry);
        Assert.Equal("my-type", Assert.IsType<CustomEntry>(entry).CustomType);
    }

    [Fact]
    public async Task SendCustomMessage_NonStreaming_AppendsEntryAndEmitsMessageStartEnd()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        var events = new List<AgentSessionEvent>();
        using var subscription = session.Subscribe(events.Add);

        await session.SendCustomMessageAsync(new CustomMessageDraft
        {
            CustomType = "note",
            Content = MessageContent.From("hello"),
            Display = true,
        });

        var entry = Assert.IsType<CustomMessageEntry>(session.SessionManager.GetEntries().Last());
        Assert.Equal("note", entry.CustomType);
        Assert.Contains(events, e => e is AgentSessionEvent.MessageStartEvent { Message: CustomMessage });
        Assert.Contains(events, e => e is AgentSessionEvent.MessageEndEvent { Message: CustomMessage });
    }

    [Fact]
    public async Task SendCustomMessage_NextTurn_IsQueuedNotAppended()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        var events = new List<AgentSessionEvent>();
        using var subscription = session.Subscribe(events.Add);

        await session.SendCustomMessageAsync(
            new CustomMessageDraft { CustomType = "aside", Content = MessageContent.From("later") },
            new SendMessageOptions { DeliverAs = "nextTurn" });
        Assert.DoesNotContain(events, e => e is AgentSessionEvent.MessageStartEvent);
        Assert.Empty(session.SessionManager.GetEntries().OfType<CustomMessageEntry>());
    }

    [Fact]
    public async Task SetThinkingLevel_PersistsAndEmits_WhenChanged()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        var events = new List<AgentSessionEvent>();
        using var subscription = session.Subscribe(events.Add);

        Assert.Equal(Defaults.ThinkingLevelOptions, session.GetAvailableThinkingLevels());
        Assert.False(session.SupportsThinking()); // 无模型

        session.SetThinkingLevel(ThinkingLevel.Medium);
        Assert.Equal(ThinkingLevel.Medium, session.ThinkingLevel);
        Assert.Equal(ThinkingLevel.Medium, session.Agent.ThinkingLevel);
        Assert.Single(events, e => e is AgentSessionEvent.ThinkingLevelChangedEvent);

        // 同档位重复设置不再发事件（TS isChanging 守卫）。
        session.SetThinkingLevel(ThinkingLevel.Medium);
        Assert.Single(events, e => e is AgentSessionEvent.ThinkingLevelChangedEvent);
    }

    [Fact]
    public async Task SetThinkingLevel_ClampsOffToAvailableOptions()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        // off 不在 THINKING_LEVEL_OPTIONS 里（TS 6 值联合）→ clamp 到 off（无模型分支）。
        session.SetThinkingLevel(ThinkingLevel.Off);
        Assert.Equal(ThinkingLevel.Off, session.ThinkingLevel);
    }

    [Fact]
    public async Task GetContextUsage_WithoutModel_ReturnsNull()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        Assert.Null(session.GetContextUsage());
    }

    [Fact]
    public async Task PromptAndCompact_AreLoudSeamsUntil9h4()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.PromptAsync("hi"));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.CompactAsync(null));
    }

    [Fact]
    public async Task ModelState_WithoutSelection_IsNull()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        Assert.Empty(session.ScopedModels);
        Assert.Null(session.RoutedModel);
        Assert.Null(session.Model);
    }
}
