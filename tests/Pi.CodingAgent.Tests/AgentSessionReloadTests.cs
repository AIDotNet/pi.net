using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <c>AgentSession.reload(options?)</c>（TS <c>agent-session.ts:3659-3698</c>）。
/// 逐条对照 TS 参考实现 <c>D:\AI\参考项目\pi\packages\coding-agent\src\core\agent-session.ts</c>。
/// </summary>
/// <remarks>
/// 覆盖面：旧 runner 的 <c>flagValues</c> 搬运与 <c>invalidate</c>、刷新前默认工具集快照、
/// <c>addedDefaultTools</c> 的「只加新名」语义、<c>+name</c>/<c>-name</c> 修饰符在 reload 时重放、
/// <c>hasBindings</c> 决定是否重发 <c>session_start</c> 与 <c>beforeSessionStart</c> 的时序。
/// </remarks>
public sealed class AgentSessionReloadTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;

    public AgentSessionReloadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-agent-session-reload-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>注册一个只记录事件、不返回结果的处理器（同 <c>AgentSessionRuntimeTests</c>）。</summary>
    private static void OnSessionEvent<T>(IExtensionApi pi, string name, List<ExtensionEvent> events)
        where T : ExtensionEvent
        => pi.On<T>(name, (@event, _) =>
        {
            events.Add(@event);
            return Task.CompletedTask;
        });

    private static InlineExtension Ext(string name, Action<IExtensionApi> configure)
        => new InlineExtension.Described(name, pi =>
        {
            configure(pi);
            return Task.CompletedTask;
        });

    private void WriteSettings(string json)
        => File.WriteAllText(Path.Combine(_agentDir, "settings.json"), json);

    private async Task<AgentSession> CreateSessionAsync(
        IReadOnlyList<InlineExtension>? extensions = null,
        IReadOnlyList<string>? initialActiveToolNames = null,
        bool usesDefaultTools = false,
        IReadOnlyList<string>? defaultToolModifiers = null)
    {
        var settings = SettingsManager.Create(_cwd, _agentDir);
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
        var agent = new Pi.Agent.Agent(new AgentOptions { StreamFn = ThrowingStreamFn() });
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
            UsesDefaultTools = usesDefaultTools,
            DefaultToolModifiers = defaultToolModifiers,
            ExtensionRunnerRef = runnerRef,
        });
        Assert.NotNull(runnerRef.Current);
        return session;
    }

    private static IReadOnlyList<string> SortedActiveTools(AgentSession session)
        => [.. session.GetActiveToolNames().OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>一个把 <c>session_shutdown</c> / <c>session_start</c> 记进同一个有序列表的扩展。</summary>
    private static InlineExtension Recorder(List<string> order)
        => Ext("recorder", pi =>
        {
            pi.On<ExtensionEvent.SessionShutdown>(ExtensionEventNames.SessionShutdown, (@event, _) =>
            {
                order.Add("shutdown:" + @event.Reason);
                return Task.CompletedTask;
            });
            pi.On<ExtensionEvent.SessionStart>(ExtensionEventNames.SessionStart, (@event, _) =>
            {
                order.Add("start:" + @event.Reason);
                return Task.CompletedTask;
            });
        });

    // ==================================================================
    // 1. 无扩展绑定：仍重建运行时、仍发 session_shutdown，但不发 session_start
    // ==================================================================

    [Fact]
    public async Task Reload_WithoutBindings_EmitsShutdownButNotStart()
    {
        var order = new List<string>();
        var session = await CreateSessionAsync(
            extensions: [Recorder(order)],
            initialActiveToolNames: []);

        var oldRunner = session.ExtensionRunner;
        await session.ReloadAsync();

        // BuildRuntime 每次都 new 一个 runner；旧的被 invalidate 后即弃。
        Assert.NotSame(oldRunner, session.ExtensionRunner);
        // TS：session_shutdown 在 hasBindings 判断之前无条件发出；session_start 才受 hasBindings 门控。
        Assert.Equal(["shutdown:" + SessionEventReason.Reload], order);
    }

    [Fact]
    public async Task Reload_WithoutBindings_DoesNotInvokeBeforeSessionStart()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        var called = false;

        await session.ReloadAsync(new ReloadOptions
        {
            BeforeSessionStart = () =>
            {
                called = true;
                return Task.CompletedTask;
            },
        });

        Assert.False(called);
    }

    // ==================================================================
    // 2. 有扩展绑定：session_shutdown(reload) → beforeSessionStart → session_start(reload)
    // ==================================================================

    [Fact]
    public async Task Reload_WithBindings_EmitsShutdownThenBeforeThenStart()
    {
        var order = new List<string>();
        var session = await CreateSessionAsync(
            extensions: [Recorder(order)],
            initialActiveToolNames: []);
        await session.BindExtensionsAsync(new ExtensionBindings { ShutdownHandler = () => { } });

        // 绑定本身发过 startup；reload 的断言只看之后的事件。
        Assert.Equal(["start:" + SessionEventReason.Startup], order);
        order.Clear();

        await session.ReloadAsync(new ReloadOptions
        {
            BeforeSessionStart = () =>
            {
                order.Add("before");
                return Task.CompletedTask;
            },
        });

        Assert.Equal(
            ["shutdown:" + SessionEventReason.Reload, "before", "start:" + SessionEventReason.Reload],
            order);
    }

    [Fact]
    public async Task Reload_WithBindings_ShutdownComesFromTheOldRunner()
    {
        // 旧 runner 先收 shutdown、随即 invalidate；新 runner 才收 start。
        var order = new List<string>();
        var session = await CreateSessionAsync(
            extensions: [Recorder(order)],
            initialActiveToolNames: []);
        await session.BindExtensionsAsync(new ExtensionBindings { OnError = _ => { } });
        order.Clear();

        var oldRunner = session.ExtensionRunner;
        await session.ReloadAsync();

        Assert.NotSame(oldRunner, session.ExtensionRunner);
        Assert.Equal(
            ["shutdown:" + SessionEventReason.Reload, "start:" + SessionEventReason.Reload],
            order);
    }

    // ==================================================================
    // 3. 旧 runner 失效（stale）
    // ==================================================================

    [Fact]
    public async Task Reload_InvalidatesThePreviousRunner()
    {
        var apis = new List<IExtensionApi>();
        var session = await CreateSessionAsync(
            extensions:
            [
                new InlineExtension.Described("capture", pi =>
                {
                    apis.Add(pi);
                    return Task.CompletedTask;
                }),
            ],
            initialActiveToolNames: []);
        await session.BindExtensionsAsync(new ExtensionBindings { ShutdownHandler = () => { } });

        await session.ReloadAsync();

        // 构造一次 + reload 重新加载一次 → 至少两个 pi 实例。
        Assert.True(apis.Count >= 2);
        var stale = apis[0];
        var ex = Assert.Throws<InvalidOperationException>(() => stale.GetSettings());
        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ==================================================================
    // 4. flagValues 跨 runner 搬运
    // ==================================================================

    [Fact]
    public async Task Reload_CarriesFlagValuesIntoTheNewRunner()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        session.ExtensionRunner.SetFlagValue("verbose", true);
        session.ExtensionRunner.SetFlagValue("mode", "fast");

        await session.ReloadAsync();

        var flags = session.ExtensionRunner.GetFlagValues();
        Assert.Equal(true, flags["verbose"]);
        Assert.Equal("fast", flags["mode"]);
    }

    // ==================================================================
    // 5. addedDefaultTools：只激活本轮新出现的默认工具
    // ==================================================================

    [Fact]
    public async Task Reload_ActivatesOnlyNewlyAddedDefaultToolsAndKeepsTheRestAsTheyWere()
    {
        WriteSettings("""{ "defaultTools": ["read", "bash"] }""");
        var session = await CreateSessionAsync(
            usesDefaultTools: true,
            initialActiveToolNames: ["read"]);

        Assert.Equal(["read"], SortedActiveTools(session));

        // bash 已在刷新前的默认集里 → 不会被激活；edit 是本轮新增 → 被激活。
        WriteSettings("""{ "defaultTools": ["read", "bash", "edit"] }""");
        await session.ReloadAsync();
        Assert.Equal(["edit", "read"], SortedActiveTools(session));

        // edit 被移出默认集 → 保持活跃（移除不回收）。
        WriteSettings("""{ "defaultTools": ["read"] }""");
        await session.ReloadAsync();
        Assert.Equal(["edit", "read"], SortedActiveTools(session));
    }

    [Fact]
    public async Task Reload_WithoutUsesDefaultTools_NeverAddsDefaultTools()
    {
        WriteSettings("""{ "defaultTools": ["read", "bash"] }""");
        var session = await CreateSessionAsync(
            usesDefaultTools: false,
            initialActiveToolNames: []);

        WriteSettings("""{ "defaultTools": ["read", "bash", "edit", "write"] }""");
        await session.ReloadAsync();

        Assert.Empty(session.GetActiveToolNames());
    }

    // ==================================================================
    // 6. reload 时把 +name/-name 修饰符重放到刷新后的 defaultTools 上
    // ==================================================================

    [Fact]
    public async Task Reload_ReappliesDefaultToolModifiersToTheRefreshedDefaultTools()
    {
        WriteSettings("""{ "defaultTools": ["read", "bash", "edit"] }""");
        var session = await CreateSessionAsync(
            usesDefaultTools: true,
            defaultToolModifiers: ["-bash", "+write"],
            initialActiveToolNames: []);

        // 刷新前：{read, edit, write}；刷新后新默认集多了 grep → 只有 grep 是「新增」。
        WriteSettings("""{ "defaultTools": ["read", "bash", "edit", "grep"] }""");
        await session.ReloadAsync();

        Assert.Equal(["grep"], SortedActiveTools(session));
    }

    // ==================================================================
    // 7. 重建后绑定仍生效（BindExtensionsAsync 的四个字段被重新应用）
    // ==================================================================

    [Fact]
    public async Task Reload_KeepsBindingsForTheNextReload()
    {
        var order = new List<string>();
        var session = await CreateSessionAsync(
            extensions: [Recorder(order)],
            initialActiveToolNames: []);
        await session.BindExtensionsAsync(new ExtensionBindings { ShutdownHandler = () => { } });

        order.Clear();
        await session.ReloadAsync();
        Assert.Equal(
            ["shutdown:" + SessionEventReason.Reload, "start:" + SessionEventReason.Reload],
            order);

        // 第二次 reload 依然有绑定 → 依然重发一对事件。
        order.Clear();
        await session.ReloadAsync();
        Assert.Equal(
            ["shutdown:" + SessionEventReason.Reload, "start:" + SessionEventReason.Reload],
            order);
    }
}
