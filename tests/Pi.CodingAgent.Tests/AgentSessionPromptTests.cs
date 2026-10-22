using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Providers;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 4e-3e-9h-4：<c>agent-session.ts</c> 的提示词编排本体——
/// <c>prompt</c>（TS 1954-2097）、<c>_runAgentPrompt</c>（TS 1808-1837）与
/// <c>_tryExecuteExtensionCommand</c>（TS 2103-2130）。逐条对照 TS 参考实现
/// （<c>D:\AI\参考项目\pi\packages\coding-agent\src\core\agent-session.ts</c>）。
/// </summary>
/// <remarks>
/// 分工：<see cref="AgentSessionLoopTests"/> / <see cref="AgentSessionCompactionTests"/> 覆盖
/// 9a～9e 的**纯决策叶子**（显式入参）；本文件覆盖 9h-4 的**实例接线**——把叶子按 TS 的
/// 分支顺序串起来、读写实例状态。因此断言偏向「TS 求值顺序」与「分支处置」，
/// 而非重复叶子的边界矩阵。
/// </remarks>
public sealed class AgentSessionPromptTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;

    public AgentSessionPromptTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-agent-session-prompt-" + Guid.NewGuid().ToString("N"));
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

    private static InlineExtension Ext(string name, Action<IExtensionApi> configure)
        => new InlineExtension.Described(name, pi =>
        {
            configure(pi);
            return Task.CompletedTask;
        });

    private async Task<AgentSession> CreateSessionAsync(
        IReadOnlyList<InlineExtension>? extensions = null,
        IReadOnlyList<string>? initialActiveToolNames = null,
        StreamFn? streamFn = null,
        Action<SettingsManager>? configureSettings = null)
    {
        var settings = SettingsManager.Create(_cwd, _agentDir);
        configureSettings?.Invoke(settings);
        var loader = new DefaultResourceLoader(
            _cwd,
            _agentDir,
            settings,
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true,
            extensionFactories: extensions);
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
        var agent = new Pi.Agent.Agent(new AgentOptions { StreamFn = streamFn ?? ThrowingStreamFn() });
        var runnerRef = new ExtensionRunnerRef();
        var session = new AgentSession(new AgentSessionConfig
        {
            Agent = agent,
            SessionManager = SessionManager.InMemory(_cwd),
            SettingsManager = settings,
            Cwd = _cwd,
            ResourceLoader = loader,
            ModelRuntime = modelRuntime,
            InitialActiveToolNames = initialActiveToolNames,
            ExtensionRunnerRef = runnerRef,
        });
        Assert.NotNull(runnerRef.Current);
        return session;
    }

    // ==================================================================
    // prompt：无模型 / 无凭据的 TS 错误路径（TS 2062-2097）
    // ==================================================================

    [Fact]
    public async Task PromptAsync_WithoutModel_ThrowsNoModelSelected()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.PromptAsync("hi"));

        // TS: `throw new Error(noModelSelectedMessage())` —— 文案逐字对照。
        Assert.Equal(AuthGuidance.FormatNoModelSelectedMessage(), error.Message);
    }

    [Fact]
    public async Task CompactAsync_WithoutModel_ThrowsNoModelSelected()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompactAsync(null));

        Assert.Equal(AuthGuidance.FormatNoModelSelectedMessage(), error.Message);
    }

    // ==================================================================
    // prompt：扩展命令直通（TS 2103-2130）——在模型校验之前短路
    // ==================================================================

    [Fact]
    public async Task PromptAsync_ExtensionCommand_InvokesHandlerAndReportsHandled()
    {
        string? receivedArgs = null;
        var session = await CreateSessionAsync(
            extensions: [Ext("cmd-ext", pi => pi.RegisterCommand("hello", new CommandRegistration
            {
                Description = "greet",
                Handler = (args, _) =>
                {
                    receivedArgs = args;
                    return Task.CompletedTask;
                },
            }))],
            initialActiveToolNames: []);

        string? disposition = null;
        await session.PromptAsync("/hello world", new PromptOptions
        {
            PreflightResult = value => disposition = value,
        });

        // 命令在模型校验之前处理，故未选模型也不抛错。
        Assert.Equal("world", receivedArgs);
        Assert.Equal(PromptDisposition.Handled, disposition);
    }

    [Fact]
    public async Task PromptAsync_ExtensionCommand_NoArgs_PassesEmptyString()
    {
        string? receivedArgs = null;
        var session = await CreateSessionAsync(
            extensions: [Ext("cmd-ext", pi => pi.RegisterCommand("ping", new CommandRegistration
            {
                Handler = (args, _) =>
                {
                    receivedArgs = args;
                    return Task.CompletedTask;
                },
            }))],
            initialActiveToolNames: []);

        await session.PromptAsync("/ping");

        Assert.Equal(string.Empty, receivedArgs);
    }

    [Fact]
    public async Task PromptAsync_ExtensionCommand_HandlerThrows_StillReportsHandled()
    {
        var session = await CreateSessionAsync(
            extensions: [Ext("cmd-ext", pi => pi.RegisterCommand("boom", new CommandRegistration
            {
                Handler = (_, _) => throw new InvalidOperationException("kaboom"),
            }))],
            initialActiveToolNames: []);

        string? disposition = null;
        // TS: 处理器抛错时被吞掉并记入扩展错误，命令仍算「已处理」。
        await session.PromptAsync("/boom", new PromptOptions
        {
            PreflightResult = value => disposition = value,
        });

        Assert.Equal(PromptDisposition.Handled, disposition);
    }

    [Fact]
    public async Task PromptAsync_UnknownSlashCommand_FallsThroughToModelCheck()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);

        // 以 "/" 开头但扩展未注册该命令 → 直通失败 → 继续走后续分支 → 撞上无模型校验。
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.PromptAsync("/nope"));

        Assert.Equal(AuthGuidance.FormatNoModelSelectedMessage(), error.Message);
    }

    [Fact]
    public async Task PromptAsync_ExpandPromptTemplatesFalse_SkipsExtensionCommandProbe()
    {
        var invoked = false;
        var session = await CreateSessionAsync(
            extensions: [Ext("cmd-ext", pi => pi.RegisterCommand("hello", new CommandRegistration
            {
                Handler = (_, _) =>
                {
                    invoked = true;
                    return Task.CompletedTask;
                },
            }))],
            initialActiveToolNames: []);

        // expandPromptTemplates=false 时 ShouldTryExecuteExtensionCommand 直接返回 false，
        // 命令不被执行，文本按普通提示词继续 → 撞上无模型校验。
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.PromptAsync("/hello", new PromptOptions { ExpandPromptTemplates = false }));

        Assert.False(invoked);
        Assert.Equal(AuthGuidance.FormatNoModelSelectedMessage(), error.Message);
    }

    // ==================================================================
    // _runAgentPrompt：循环本体 + settled 收尾（TS 1808-1837 / 1912-1920）
    // ==================================================================

    [Fact]
    public async Task RunAgentPromptAsync_SingleTurn_RunsLoopAndEmitsAgentSettled()
    {
        var session = await CreateSessionAsync(
            initialActiveToolNames: [],
            streamFn: Faux.CreateStreamFn([Faux.Response.Text("hello")]));

        var events = new List<AgentSessionEvent>();
        using var _ = session.Subscribe(events.Add);

        await session.RunAgentPromptAsync(new UserMessage("hi", 0));

        // 一轮纯文本回复：post-run 无继续、无排队 → 循环退出，finally 发 settled。
        Assert.Contains(events, e => e is AgentSessionEvent.AgentSettledEvent);
        Assert.False(session.IsStreaming);
        Assert.Contains(session.Agent.Messages, m => m is AssistantMessage);
    }

    [Fact]
    public async Task RunAgentPromptAsync_ClearsRunScopedStateInFinally()
    {
        var session = await CreateSessionAsync(
            initialActiveToolNames: [],
            streamFn: Faux.CreateStreamFn([Faux.Response.Text("hello")]));

        await session.RunAgentPromptAsync(new UserMessage("hi", 0));

        // TS finally 的可见副作用：run 收尾后会话回到空闲态（排队/流式标记均清空）。
        Assert.True(session.IsIdle);
        Assert.False(session.IsStreaming);
    }

    [Fact]
    public async Task RunAgentPromptAsync_AgentRunFailure_StillSettlesWithoutThrowing()
    {
        // 关闭重试以免错误回合触发 2s/4s/8s 退避；Pi.Agent 会把 streamFn 的异常降级成一条
        // error 助手消息（不向外抛），故 RunAgentPromptAsync 正常收尾并照发 settled。
        var session = await CreateSessionAsync(
            initialActiveToolNames: [],
            streamFn: ThrowingStreamFn(),
            configureSettings: settings => settings.SetRetryEnabled(false));

        var events = new List<AgentSessionEvent>();
        using var _ = session.Subscribe(events.Add);

        await session.RunAgentPromptAsync(new UserMessage("hi", 0));

        var settled = Assert.Single(events.OfType<AgentSessionEvent.AgentSettledEvent>());
        Assert.False(settled.Aborted);
        Assert.False(session.IsStreaming);
        Assert.True(session.IsIdle);
    }

    [Fact]
    public async Task AgentSettledReportsAbortedWhenTheRunWasAborted()
    {
        // 上游 503c60552（#10607）：agent_settled 带上 aborted，让界面知道这轮是「被中止」
        // 而不是正常收尾（中止要回到 idle，而不是 done/error）。
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        StreamFn blockingStreamFn = (_, _, _, _) =>
        {
            var stream = new AssistantMessageEventStream();
            return Task.Run(async () =>
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
                var message = new AssistantMessage([], StopReason.Aborted);
                stream.Push(new AssistantMessageEvent.Done(StopReason.Aborted, message));
                stream.End(message);
                return (IAssistantMessageEventStream)stream;
            });
        };

        var session = await CreateSessionAsync(initialActiveToolNames: [], streamFn: blockingStreamFn);
        var events = new List<AgentSessionEvent>();
        using var _ = session.Subscribe(events.Add);

        var promptTask = session.RunAgentPromptAsync(new UserMessage("hi", 0));
        await entered.Task;
        // AbortAsync 先把中止标志同步置位、再去等空闲，因此这里可以「先中止、后放开流」。
        var abortTask = session.AbortAsync();
        release.TrySetResult();
        await abortTask;
        await promptTask;

        var settled = Assert.Single(events.OfType<AgentSessionEvent.AgentSettledEvent>());
        Assert.True(settled.Aborted);
        Assert.True(session.IsIdle);
    }
}
