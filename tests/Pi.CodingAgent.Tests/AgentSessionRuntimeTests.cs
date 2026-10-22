using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using Xunit;

// 扩展契约里的 NewSessionOptions 与核心的 NewSessionOptions 同名；本文件两种都要用，
// 故给扩展层加别名（与生产代码 AgentSessionRuntime.cs 的处理一致）。
using ExtensionNewSessionOptions = Pi.CodingAgent.Core.Extensions.NewSessionOptions;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 4e-3e-9h-4b-2：<c>AgentSessionRuntime</c> 的编排本体（TS <c>core/agent-session-runtime.ts</c>，447 行）
/// 与 <c>createAgentSessionServices</c> / <c>createAgentSessionFromServices</c>
/// （TS <c>core/agent-session-services.ts</c>，233 行）。
/// </summary>
/// <remarks>
/// <para>
/// 逐条对照 TS 的 <c>test/agent-session-runtime-events.test.ts</c>：会话替换类方法的事件发出顺序、
/// <c>session_before_switch</c> / <c>session_before_fork</c> 的取消短路、
/// <c>beforeSessionInvalidate</c> 相对于 <c>session_shutdown</c> 与 <c>rebindSession</c> 的位置。
/// </para>
/// <para>
/// 与 <see cref="AgentSessionServicesTests"/> 的分工：后者覆盖纯决策叶子（显式入参的静态函数）；
/// 本文件覆盖**编排**——真正装配服务、真正换会话、真正发事件。
/// </para>
/// </remarks>
public sealed class AgentSessionRuntimeTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;
    private readonly string _sessionDir;

    public AgentSessionRuntimeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-runtime-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_root, "cwd");
        _agentDir = Path.Combine(_cwd, ".pi");
        _sessionDir = Path.Combine(_root, "sessions");
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

    // ==================================================================
    // 夹具
    // ==================================================================

    private static InlineExtension Ext(string name, Action<IExtensionApi> configure)
        => new InlineExtension.Described(name, pi =>
        {
            configure(pi);
            return Task.CompletedTask;
        });

    private sealed class StubApiKeyAuth : IApiKeyAuth
    {
        public string Name => "stub-api-key";

        public Task<AuthResult?> ResolveAsync(
            Credential.ApiKey? credential, IAuthContext ctx, CancellationToken cancellationToken = default)
            => Task.FromResult<AuthResult?>(new AuthResult
            {
                Auth = new ModelAuth { ApiKey = credential?.Key ?? "stub-key" },
                Source = "stub-env",
            });
    }

    /// <summary>以一条终态消息立即结束流。与真实 provider 同款收尾：先推终态事件，再 <c>End</c>。</summary>
    private sealed class RecordingProvider : IProvider
    {
        public ModelSpec Model { get; } = new()
        {
            Id = "stub-chat",
            Name = "stub-chat",
            Api = "stub-api",
            Provider = "stub",
            BaseUrl = "https://stub.invalid",
            Type = ModelType.Chat,
            Input = [ModelInput.Text],
            Cost = new ModelCostRates(0, 0, 0, 0),
            ContextWindow = 1_000,
            MaxTokens = 100,
            Reasoning = true,
        };

        public string Id => "stub";

        public string Name => "Stub";

        public string? BaseUrl => null;

        public ProviderAuth? Auth => new() { ApiKey = new StubApiKeyAuth() };

        public IReadOnlyList<ModelSpec> GetModels() => [Model];

        public IReadOnlyList<ModelSpec> GetAllModels() => [Model];

        public IAssistantMessageEventStream Stream(
            ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
            => Respond(model);

        public IAssistantMessageEventStream StreamSimple(
            ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
            => Respond(model);

        private static IAssistantMessageEventStream Respond(ModelSpec model)
        {
            var stream = new AssistantMessageEventStream();
            var message = new AssistantMessage(
                Content: [],
                StopReason: StopReason.Stop,
                Model: model.Id,
                Api: model.Api,
                Provider: model.Provider);
            stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
            stream.End(message);
            return stream;
        }
    }

    private static async Task<ModelRuntime> CreateRuntimeAsync(IProvider provider)
    {
        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new InMemoryCredentialStore(),
            ModelsPathDisabled = true,
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            RefreshOnCreate = false,
            Env = name => name == "PI_OFFLINE" ? "1" : null,
            AuthContext = new StubAuthContext(name => name == "PI_OFFLINE" ? "1" : null),
        });
        runtime.RegisterNativeProvider(provider);
        if (runtime.PendingBackgroundRefresh is { } pending)
        {
            await pending;
        }

        return runtime;
    }

    /// <summary>
    /// 装配一个完整的运行时宿主。工厂内部走 TS 测试的同款路径：
    /// <c>createAgentSessionServices</c> → <c>createAgentSessionFromServices</c> → 组装结果。
    /// </summary>
    private async Task<(AgentSessionRuntime Host, RecordingProvider Provider)> CreateHostAsync(
        Action<IExtensionApi> configureExtension,
        SessionManager? sessionManager = null,
        Action<CreateAgentSessionRuntimeOptions>? onFactory = null)
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);
        var extension = Ext("runtime-events", configureExtension);

        CreateAgentSessionRuntimeFactory factory = async options =>
        {
            onFactory?.Invoke(options);
            var services = await AgentSessionServicesBuilder.CreateAgentSessionServicesAsync(
                new CreateAgentSessionServicesOptions
                {
                    Cwd = options.Cwd,
                    AgentDir = _agentDir,
                    ModelRuntime = runtime,
                    ResourceLoaderOptions = new ResourceLoaderExtraOptions
                    {
                        NoSkills = true,
                        NoPromptTemplates = true,
                        NoThemes = true,
                        NoContextFiles = true,
                        ExtensionFactories = [extension],
                    },
                });
            var created = await AgentSessionServicesBuilder.CreateAgentSessionFromServicesAsync(
                new CreateAgentSessionFromServicesOptions
                {
                    Services = services,
                    SessionManager = options.SessionManager,
                    SessionStartEvent = options.SessionStartEvent,
                    Model = provider.Model,
                },
                options.Cwd);
            return new CreateAgentSessionRuntimeResult
            {
                Session = created.Session,
                ExtensionsResult = created.ExtensionsResult,
                ModelFallbackMessage = created.ModelFallbackMessage,
                Services = services,
                Diagnostics = services.Diagnostics,
            };
        };

        var host = await AgentSessionRuntime.CreateAgentSessionRuntimeAsync(
            factory,
            new CreateAgentSessionRuntimeOptions
            {
                Cwd = _cwd,
                AgentDir = _agentDir,
                SessionManager = sessionManager ?? SessionManager.Create(_cwd, _sessionDir),
            });
        await host.Session.BindExtensionsAsync(new ExtensionBindings());
        return (host, provider);
    }

    /// <summary>注册一个只记录事件、不返回结果的处理器。</summary>
    private static void OnSessionEvent<T>(IExtensionApi pi, string name, List<ExtensionEvent> events)
        where T : ExtensionEvent
        => pi.On<T>(name, (@event, _) =>
        {
            events.Add(@event);
            return Task.CompletedTask;
        });

    private static string[] ThinkingLevelsOn(SessionManager sessionManager)
        => [.. sessionManager.GetBranch().OfType<ThinkingLevelChangeEntry>().Select(entry => entry.ThinkingLevel)];

    private static (string Provider, string ModelId)[] ModelChangesOn(SessionManager sessionManager)
        => [.. sessionManager.GetBranch().OfType<ModelChangeEntry>().Select(entry => (entry.Provider, entry.ModelId))];

    // ==================================================================
    // 事件顺序：new / resume
    // ==================================================================

    [Fact]
    public async Task EmitsSessionBeforeSwitchAndSessionStartForNewAndResumeFlows()
    {
        var events = new List<ExtensionEvent>();
        var (host, _) = await CreateHostAsync(pi =>
        {
            OnSessionEvent<ExtensionEvent.SessionBeforeSwitch>(pi, ExtensionEventNames.SessionBeforeSwitch, events);
            OnSessionEvent<ExtensionEvent.SessionShutdown>(pi, ExtensionEventNames.SessionShutdown, events);
            OnSessionEvent<ExtensionEvent.SessionStart>(pi, ExtensionEventNames.SessionStart, events);
        });

        // 首次绑定扩展发出 startup。
        var startup = Assert.IsType<ExtensionEvent.SessionStart>(Assert.Single(events));
        Assert.Equal(SessionEventReason.Startup, startup.Reason);
        events.Clear();

        await host.Session.PromptAsync("hello");
        var originalSessionFile = host.Session.SessionFile;
        Assert.False(string.IsNullOrEmpty(originalSessionFile));

        var newResult = await host.NewSessionAsync();
        Assert.False(newResult.Cancelled);
        await host.Session.BindExtensionsAsync(new ExtensionBindings());
        var secondSessionFile = host.Session.SessionFile;

        Assert.Collection(
            events,
            e =>
            {
                var before = Assert.IsType<ExtensionEvent.SessionBeforeSwitch>(e);
                Assert.Equal(SessionEventReason.New, before.Reason);
                Assert.Null(before.TargetSessionFile);
            },
            e =>
            {
                var shutdown = Assert.IsType<ExtensionEvent.SessionShutdown>(e);
                Assert.Equal(SessionEventReason.New, shutdown.Reason);
                Assert.Equal(secondSessionFile, shutdown.TargetSessionFile);
            },
            e =>
            {
                var start = Assert.IsType<ExtensionEvent.SessionStart>(e);
                Assert.Equal(SessionEventReason.New, start.Reason);
                Assert.Equal(originalSessionFile, start.PreviousSessionFile);
            });

        events.Clear();
        Assert.False(string.IsNullOrEmpty(secondSessionFile));

        var switchResult = await host.SwitchSessionAsync(originalSessionFile!);
        Assert.False(switchResult.Cancelled);
        await host.Session.BindExtensionsAsync(new ExtensionBindings());

        Assert.Collection(
            events,
            e =>
            {
                var before = Assert.IsType<ExtensionEvent.SessionBeforeSwitch>(e);
                Assert.Equal(SessionEventReason.Resume, before.Reason);
                Assert.Equal(originalSessionFile, before.TargetSessionFile);
            },
            e =>
            {
                var shutdown = Assert.IsType<ExtensionEvent.SessionShutdown>(e);
                Assert.Equal(SessionEventReason.Resume, shutdown.Reason);
                Assert.Equal(originalSessionFile, shutdown.TargetSessionFile);
            },
            e =>
            {
                var start = Assert.IsType<ExtensionEvent.SessionStart>(e);
                Assert.Equal(SessionEventReason.Resume, start.Reason);
                Assert.Equal(secondSessionFile, start.PreviousSessionFile);
            });
    }

    [Fact]
    public async Task HonorsSessionBeforeSwitchCancellation()
    {
        var events = new List<ExtensionEvent>();
        var (host, _) = await CreateHostAsync(pi =>
        {
            pi.On<ExtensionEvent.SessionBeforeSwitch, SessionBeforeSwitchResult>(
                ExtensionEventNames.SessionBeforeSwitch,
                (@event, _) =>
                {
                    events.Add(@event);
                    return Task.FromResult<SessionBeforeSwitchResult?>(new SessionBeforeSwitchResult { Cancel = true });
                });
            OnSessionEvent<ExtensionEvent.SessionStart>(pi, ExtensionEventNames.SessionStart, events);
        });

        events.Clear();
        await host.Session.PromptAsync("hello");
        var originalSessionFile = host.Session.SessionFile;

        var result = await host.NewSessionAsync();

        // TS：取消时直接返回，既不拆当前会话也不创建新会话。
        Assert.True(result.Cancelled);
        Assert.Equal(originalSessionFile, host.Session.SessionFile);
        var before = Assert.IsType<ExtensionEvent.SessionBeforeSwitch>(Assert.Single(events));
        Assert.Equal(SessionEventReason.New, before.Reason);
        Assert.Null(before.TargetSessionFile);
    }

    [Fact]
    public async Task RunsBeforeSessionInvalidateAfterSessionShutdownAndBeforeRebind()
    {
        var phases = new List<string>();
        var (host, _) = await CreateHostAsync(pi => pi.On<ExtensionEvent.SessionShutdown>(
            ExtensionEventNames.SessionShutdown,
            (_, _) =>
            {
                phases.Add("session_shutdown");
                return Task.CompletedTask;
            }));

        var oldSession = host.Session;
        host.SetBeforeSessionInvalidate(() =>
        {
            phases.Add("beforeSessionInvalidate");
            // 回调时旧会话的扩展上下文仍然可用（宿主要在此时拆掉扩展提供的 TUI 组件）。
            Assert.Equal(oldSession.SessionManager.GetCwd(), oldSession.ExtensionRunner.CreateContext().Cwd);
        });
        host.SetRebindSession(_ =>
        {
            phases.Add("rebindSession");
            return Task.CompletedTask;
        });

        await host.NewSessionAsync();

        // TS：session_shutdown 处理器 → beforeSessionInvalidate → rebindSession。
        Assert.Equal(["session_shutdown", "beforeSessionInvalidate", "rebindSession"], phases);

        // 替换之后旧上下文变陈旧（文案与 TS 逐字一致）。
        var ex = Assert.Throws<InvalidOperationException>(() => oldSession.ExtensionRunner.CreateContext().Cwd);
        Assert.Equal(ExtensionRunner.StaleMessage, ex.Message);

        host.SetBeforeSessionInvalidate(null);
        host.SetRebindSession(null);
    }

    [Fact]
    public async Task NewSession_InvalidatesTheOldContextOnlyAfterTheShutdownHandlersFinish()
    {
        var sawLiveContext = false;
        var (host, _) = await CreateHostAsync(_ => { });
        var oldSession = host.Session;

        host.SetBeforeSessionInvalidate(() =>
        {
            // 此时尚未 dispose，旧上下文必须还能读。
            oldSession.ExtensionRunner.CreateContext();
            sawLiveContext = true;
        });

        await host.NewSessionAsync();

        Assert.True(sawLiveContext);
    }

    // ==================================================================
    // 事件顺序：fork
    // ==================================================================

    [Fact]
    public async Task EmitsSessionBeforeForkAndSessionStartAndHonorsCancellation()
    {
        var events = new List<ExtensionEvent>();
        var cancelNextFork = false;
        var (host, _) = await CreateHostAsync(pi =>
        {
            pi.On<ExtensionEvent.SessionBeforeFork, SessionBeforeForkResult>(
                ExtensionEventNames.SessionBeforeFork,
                (@event, _) =>
                {
                    events.Add(@event);
                    if (!cancelNextFork)
                    {
                        return Task.FromResult<SessionBeforeForkResult?>(null);
                    }

                    cancelNextFork = false;
                    return Task.FromResult<SessionBeforeForkResult?>(new SessionBeforeForkResult { Cancel = true });
                });
            OnSessionEvent<ExtensionEvent.SessionShutdown>(pi, ExtensionEventNames.SessionShutdown, events);
            OnSessionEvent<ExtensionEvent.SessionStart>(pi, ExtensionEventNames.SessionStart, events);
        });

        events.Clear();
        await host.Session.PromptAsync("hello");
        var userMessage = AgentSession.ProjectUserMessagesForForking(host.Session.SessionManager.GetEntries())[0];
        var previousSessionFile = host.Session.SessionFile;

        var success = await host.ForkAsync(userMessage.EntryId);
        Assert.False(success.Cancelled);
        Assert.Equal("hello", success.SelectedText);
        await host.Session.BindExtensionsAsync(new ExtensionBindings());

        Assert.Collection(
            events,
            e =>
            {
                var before = Assert.IsType<ExtensionEvent.SessionBeforeFork>(e);
                Assert.Equal(userMessage.EntryId, before.EntryId);
                Assert.Equal(ForkPosition.Before, before.Position);
            },
            e =>
            {
                var shutdown = Assert.IsType<ExtensionEvent.SessionShutdown>(e);
                Assert.Equal(SessionEventReason.Fork, shutdown.Reason);
                Assert.Equal(host.Session.SessionFile, shutdown.TargetSessionFile);
            },
            e =>
            {
                var start = Assert.IsType<ExtensionEvent.SessionStart>(e);
                Assert.Equal(SessionEventReason.Fork, start.Reason);
                Assert.Equal(previousSessionFile, start.PreviousSessionFile);
            });

        events.Clear();
        cancelNextFork = true;
        var cancelled = await host.ForkAsync(userMessage.EntryId);
        Assert.True(cancelled.Cancelled);
        Assert.Null(cancelled.SelectedText);
        Assert.Equal(ForkPosition.Before, Assert.IsType<ExtensionEvent.SessionBeforeFork>(Assert.Single(events)).Position);

        // 取消发生在 entry 解析**之前**，所以缺失的 entry 也不会抛。
        events.Clear();
        cancelNextFork = true;
        var cancelledAt = await host.ForkAsync("missing-entry", new ForkOptions { Position = ForkPosition.At });
        Assert.True(cancelledAt.Cancelled);
        var onlyAt = Assert.IsType<ExtensionEvent.SessionBeforeFork>(Assert.Single(events));
        Assert.Equal("missing-entry", onlyAt.EntryId);
        Assert.Equal(ForkPosition.At, onlyAt.Position);
    }

    [Fact]
    public async Task ForkAtPosition_KeepsTheSelectedEntryAndDoesNotReportSelectedText()
    {
        var (host, _) = await CreateHostAsync(_ => { });
        await host.Session.PromptAsync("hello");
        var userMessage = AgentSession.ProjectUserMessagesForForking(host.Session.SessionManager.GetEntries())[0];

        var result = await host.ForkAsync(userMessage.EntryId, new ForkOptions { Position = ForkPosition.At });

        Assert.False(result.Cancelled);
        // TS：position === "at" 时不回填编辑器文本。
        Assert.Null(result.SelectedText);
    }

    [Fact]
    public async Task ForkBeforePosition_RejectsANonUserMessageEntry()
    {
        var (host, _) = await CreateHostAsync(_ => { });
        await host.Session.PromptAsync("hello");

        // 助手消息不是合法的 "before" fork 目标。
        var assistantEntry = host.Session.SessionManager.GetEntries()
            .OfType<SessionMessageEntry>()
            .First(entry => entry.Message is AssistantMessage);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ForkAsync(assistantEntry.Id!));
        Assert.Equal("Invalid entry ID for forking", ex.Message);
    }

    // ==================================================================
    // newSession 的 persisted / in-memory 分支
    // ==================================================================

    [Fact]
    public async Task NewSession_KeepsAnInMemorySessionInMemory()
    {
        var (host, _) = await CreateHostAsync(_ => { }, SessionManager.InMemory(_cwd));
        Assert.False(host.Session.SessionManager.IsPersisted());
        Assert.Null(host.Session.SessionFile);

        var result = await host.NewSessionAsync();

        Assert.False(result.Cancelled);
        // TS：`isPersisted() ? create(...) : inMemory(...)`。
        Assert.False(host.Session.SessionManager.IsPersisted());
        Assert.Null(host.Session.SessionFile);
    }

    [Fact]
    public async Task NewSession_CarriesTheParentSessionIntoTheFreshHeader()
    {
        var (host, _) = await CreateHostAsync(_ => { });
        await host.Session.PromptAsync("hello");
        var parentSessionFile = host.Session.SessionFile;

        var result = await host.NewSessionAsync(new ExtensionNewSessionOptions { ParentSession = parentSessionFile });

        Assert.False(result.Cancelled);
        var header = host.Session.SessionManager.GetHeader();
        Assert.NotNull(header);
        Assert.Equal(parentSessionFile, header!.ParentSession);
        Assert.NotEqual(parentSessionFile, host.Session.SessionFile);
    }

    [Fact]
    public async Task NewSession_RunsSetupThenWithSessionInOrder()
    {
        var phases = new List<string>();
        var (host, _) = await CreateHostAsync(_ => { });
        host.SetRebindSession(_ =>
        {
            phases.Add("rebind");
            return Task.CompletedTask;
        });

        var result = await host.NewSessionAsync(new ExtensionNewSessionOptions
        {
            Setup = manager =>
            {
                // 新会话已持久化 → setup 拿到的 manager 有 session file。
                phases.Add("setup:" + (manager.SessionFile is not null));
                return Task.CompletedTask;
            },
            WithSession = _ =>
            {
                phases.Add("withSession");
                return Task.CompletedTask;
            },
        });

        Assert.False(result.Cancelled);
        // TS：createRuntime → setup(sessionManager) → refreshContext() → finishSessionReplacement。
        Assert.Equal(["setup:True", "rebind", "withSession"], phases);
    }

    // ==================================================================
    // switchSession 的附加输入
    // ==================================================================

    [Fact]
    public async Task SwitchSession_InvokesTheProjectTrustFactoryWithTheTargetCwd()
    {
        CreateAgentSessionRuntimeOptions? lastOptions = null;
        var (host, _) = await CreateHostAsync(_ => { }, onFactory: options => lastOptions = options);
        await host.Session.PromptAsync("hello");
        var target = host.Session.SessionFile!;

        string? seenCwd = null;
        await Assert.ThrowsAsync<SentinelException>(() => host.SwitchSessionAsync(
            target,
            new SwitchSessionRuntimeOptions
            {
                ProjectTrustContextFactory = cwd =>
                {
                    seenCwd = cwd;
                    throw new SentinelException();
                },
            }));

        // TS：`options?.projectTrustContextFactory?.(sessionManager.getCwd())`——用**目标**会话的 cwd。
        Assert.Equal(Paths.ResolvePath(_cwd), seenCwd);
        Assert.NotNull(lastOptions);
    }

    [Fact]
    public async Task SwitchSession_WithoutTrustFactory_LeavesTheContextUnset()
    {
        CreateAgentSessionRuntimeOptions? lastOptions = null;
        var (host, _) = await CreateHostAsync(_ => { }, onFactory: options => lastOptions = options);
        var target = host.Session.SessionFile!;

        var result = await host.SwitchSessionAsync(target);

        Assert.False(result.Cancelled);
        Assert.NotNull(lastOptions);
        Assert.Null(lastOptions!.ProjectTrustContext);
        Assert.Equal(SessionEventReason.Resume, lastOptions.SessionStartEvent!.Reason);
    }

    // ==================================================================
    // importFromJsonl
    // ==================================================================

    [Fact]
    public async Task ImportFromJsonl_ThrowsForAMissingFile()
    {
        var (host, _) = await CreateHostAsync(_ => { });

        var missing = Path.Combine(_root, "nope", "session.jsonl");
        var ex = await Assert.ThrowsAsync<SessionImportFileNotFoundError>(
            () => host.ImportFromJsonlAsync(missing));

        Assert.Equal(Paths.ResolvePath(missing), ex.FilePath);
        Assert.Contains("File not found:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportFromJsonl_CopiesIntoTheSessionDirAndSwitchesRuntime()
    {
        var events = new List<ExtensionEvent>();
        var (host, _) = await CreateHostAsync(pi =>
        {
            OnSessionEvent<ExtensionEvent.SessionBeforeSwitch>(pi, ExtensionEventNames.SessionBeforeSwitch, events);
            OnSessionEvent<ExtensionEvent.SessionShutdown>(pi, ExtensionEventNames.SessionShutdown, events);
            OnSessionEvent<ExtensionEvent.SessionStart>(pi, ExtensionEventNames.SessionStart, events);
        });

        await host.Session.PromptAsync("hello");
        var previousSessionFile = host.Session.SessionFile;

        // 造一份可导入的会话文件（带 header，cwd 指向另一个存在的目录）。
        var sourceCwd = Path.Combine(_root, "imported-cwd");
        Directory.CreateDirectory(sourceCwd);
        var incomingDir = Path.Combine(_root, "incoming");
        Directory.CreateDirectory(incomingDir);
        var sourceManager = SessionManager.Create(sourceCwd, incomingDir);
        sourceManager.AppendMessage(new UserMessage([new TextContent("imported")], 1));
        var sourceFile = sourceManager.GetSessionFile()!;
        Assert.True(File.Exists(sourceFile));

        events.Clear();
        var result = await host.ImportFromJsonlAsync(sourceFile);
        await host.Session.BindExtensionsAsync(new ExtensionBindings());

        Assert.False(result.Cancelled);
        var destination = Path.Combine(_sessionDir, Path.GetFileName(sourceFile));
        Assert.True(File.Exists(destination));
        Assert.Equal(Paths.ResolvePath(sourceCwd), host.Cwd);

        Assert.Collection(
            events,
            e =>
            {
                var before = Assert.IsType<ExtensionEvent.SessionBeforeSwitch>(e);
                Assert.Equal(SessionEventReason.Resume, before.Reason);
                Assert.Equal(destination, before.TargetSessionFile);
            },
            e =>
            {
                var shutdown = Assert.IsType<ExtensionEvent.SessionShutdown>(e);
                Assert.Equal(SessionEventReason.Resume, shutdown.Reason);
                Assert.Equal(Paths.ResolvePath(destination), shutdown.TargetSessionFile);
            },
            e =>
            {
                var start = Assert.IsType<ExtensionEvent.SessionStart>(e);
                Assert.Equal(SessionEventReason.Resume, start.Reason);
                Assert.Equal(previousSessionFile, start.PreviousSessionFile);
            });
    }

    [Fact]
    public async Task ImportFromJsonl_DoesNotCopyAFileAlreadyInTheSessionDir()
    {
        var (host, _) = await CreateHostAsync(_ => { });

        // 直接放进会话目录：源即目标，不再复制（TS 的 sourceAlreadyStored）。
        Directory.CreateDirectory(_sessionDir);
        var sourceManager = SessionManager.Create(_cwd, _sessionDir);
        sourceManager.AppendMessage(new UserMessage([new TextContent("already")], 1));
        var storedFile = sourceManager.GetSessionFile()!;
        Assert.True(File.Exists(storedFile));
        var countBefore = Directory.GetFiles(_sessionDir, "*.jsonl").Length;

        var result = await host.ImportFromJsonlAsync(storedFile);

        Assert.False(result.Cancelled);
        // 未复制 → 不产生 "…-1.jsonl" 之类的副本。
        Assert.Equal(countBefore, Directory.GetFiles(_sessionDir, "*.jsonl").Length);
        Assert.Equal(storedFile, host.Session.SessionFile);
    }

    // ==================================================================
    // dispose / 工厂
    // ==================================================================

    [Fact]
    public async Task Dispose_EmitsShutdownWithQuitReasonAndInvalidatesTheContext()
    {
        var events = new List<ExtensionEvent>();
        var (host, _) = await CreateHostAsync(pi =>
            OnSessionEvent<ExtensionEvent.SessionShutdown>(pi, ExtensionEventNames.SessionShutdown, events));

        events.Clear();
        var session = host.Session;
        var invalidated = false;
        host.SetBeforeSessionInvalidate(() => invalidated = true);

        await host.DisposeAsync();

        var shutdown = Assert.IsType<ExtensionEvent.SessionShutdown>(Assert.Single(events));
        Assert.Equal(SessionEventReason.Quit, shutdown.Reason);
        Assert.Null(shutdown.TargetSessionFile);
        Assert.True(invalidated);
        // TS：dispose 走 `session.dispose()` → `runner.invalidate()`。
        Assert.Throws<InvalidOperationException>(() => session.ExtensionRunner.CreateContext().Cwd);
    }

    [Fact]
    public async Task CreateAgentSessionRuntime_RejectsAMissingCwd()
    {
        var missingCwd = Path.Combine(_root, "gone");
        var called = false;

        var ex = await Assert.ThrowsAsync<MissingSessionCwdException>(
            () => AgentSessionRuntime.CreateAgentSessionRuntimeAsync(
                _ =>
                {
                    called = true;
                    throw new InvalidOperationException("factory must not run");
                },
                new CreateAgentSessionRuntimeOptions
                {
                    Cwd = missingCwd,
                    AgentDir = _agentDir,
                    SessionManager = SessionManager.Create(missingCwd, _sessionDir),
                }));

        // TS：assertSessionCwdExists 在调用工厂**之前**跑。
        Assert.False(called);
        Assert.Equal(Paths.ResolvePath(missingCwd), ex.Issue.SessionCwd);
    }

    [Fact]
    public async Task CreateAgentSessionRuntime_SeedsDiagnosticsAndServices()
    {
        var (host, _) = await CreateHostAsync(_ => { });

        Assert.Equal(Paths.ResolvePath(_cwd), host.Cwd);
        Assert.Equal(Paths.ResolvePath(_cwd), host.Services.Cwd);
        Assert.Equal(Paths.ResolvePath(_agentDir), host.Services.AgentDir);
        // 工厂把 services.diagnostics 原样交给结果。
        Assert.Same(host.Services.Diagnostics, host.Diagnostics);
        Assert.Null(host.ModelFallbackMessage);
    }

    // ==================================================================
    // createAgentSessionServices / createAgentSessionFromServices
    // ==================================================================

    [Fact]
    public async Task CreateAgentSessionServices_BindsCwdAndAgentDirAndReturnsNoDiagnostics()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);

        var services = await AgentSessionServicesBuilder.CreateAgentSessionServicesAsync(
            new CreateAgentSessionServicesOptions
            {
                Cwd = _cwd,
                AgentDir = _agentDir,
                ModelRuntime = runtime,
                ResourceLoaderOptions = new ResourceLoaderExtraOptions
                {
                    NoSkills = true,
                    NoPromptTemplates = true,
                    NoThemes = true,
                    NoContextFiles = true,
                },
            });

        Assert.Equal(Paths.ResolvePath(_cwd), services.Cwd);
        Assert.Equal(Paths.ResolvePath(_agentDir), services.AgentDir);
        // 传了 modelRuntime 就复用，不再新建（TS 的 `options.modelRuntime ?? await ModelRuntime.create(...)`）。
        Assert.Same(runtime, services.ModelRuntime);
        Assert.NotNull(services.SettingsManager);
        Assert.NotNull(services.ResourceLoader);
        Assert.Empty(services.Diagnostics);
    }

    [Fact]
    public async Task CreateAgentSessionServices_AppliesExtensionFlagsOntoTheRuntime()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);
        var extension = Ext("flags", pi =>
        {
            pi.RegisterFlag("verbose", new FlagRegistration { Type = FlagType.Boolean });
            pi.RegisterFlag("token", new FlagRegistration { Type = FlagType.String });
        });

        var services = await AgentSessionServicesBuilder.CreateAgentSessionServicesAsync(
            new CreateAgentSessionServicesOptions
            {
                Cwd = _cwd,
                AgentDir = _agentDir,
                ModelRuntime = runtime,
                ResourceLoaderOptions = new ResourceLoaderExtraOptions
                {
                    NoSkills = true,
                    NoPromptTemplates = true,
                    NoThemes = true,
                    NoContextFiles = true,
                    ExtensionFactories = [extension],
                },
                ExtensionFlagValues =
                [
                    new("verbose", ExtensionFlagInput.Boolean),
                    new("token", ExtensionFlagInput.Text("abc")),
                    new("ghost", ExtensionFlagInput.Boolean),
                ],
            });

        var flagValues = services.ResourceLoader.GetExtensions().Runtime.FlagValues;
        Assert.True((bool)flagValues["verbose"]!);
        Assert.Equal("abc", flagValues["token"]);
        Assert.False(flagValues.ContainsKey("ghost"));
        var diagnostic = Assert.Single(services.Diagnostics);
        Assert.Equal(AgentSessionDiagnosticType.Error, diagnostic.Type);
        Assert.Equal("Unknown option: --ghost", diagnostic.Message);
    }

    [Fact]
    public async Task CreateAgentSessionFromServices_ResolvesModelAndPersistsSessionMetadata()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);
        var services = await AgentSessionServicesBuilder.CreateAgentSessionServicesAsync(
            new CreateAgentSessionServicesOptions
            {
                Cwd = _cwd,
                AgentDir = _agentDir,
                ModelRuntime = runtime,
                ResourceLoaderOptions = new ResourceLoaderExtraOptions
                {
                    NoSkills = true,
                    NoPromptTemplates = true,
                    NoThemes = true,
                    NoContextFiles = true,
                },
            });
        var sessionManager = SessionManager.Create(_cwd, _sessionDir);

        var result = await AgentSessionServicesBuilder.CreateAgentSessionFromServicesAsync(
            new CreateAgentSessionFromServicesOptions
            {
                Services = services,
                SessionManager = sessionManager,
                SessionStartEvent = new ExtensionEvent.SessionStart(SessionEventReason.Startup),
                Model = provider.Model,
                ThinkingLevel = ThinkingLevel.High,
            },
            _cwd);

        Assert.Equal(provider.Model.Id, result.Session.Model!.Id);
        Assert.Equal(ThinkingLevel.High, result.Session.ThinkingLevel);
        Assert.Null(result.ModelFallbackMessage);
        // TS：`cwd` / `agentDir` 取自 services，而不是调用方另给的参数。
        Assert.Equal(services.Cwd, result.Session.SessionManager.GetCwd());
        Assert.Equal(_sessionDir, result.Session.SessionManager.GetSessionDir());
        Assert.Equal(["high"], ThinkingLevelsOn(sessionManager));
        Assert.Equal([("stub", provider.Model.Id)], ModelChangesOn(sessionManager));
    }

    [Fact]
    public async Task CreateAgentSessionFromServices_ForwardsTheSessionStartEventToBindings()
    {
        var events = new List<ExtensionEvent>();
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);
        var extension = Ext("bindings", pi =>
            OnSessionEvent<ExtensionEvent.SessionStart>(pi, ExtensionEventNames.SessionStart, events));
        var services = await AgentSessionServicesBuilder.CreateAgentSessionServicesAsync(
            new CreateAgentSessionServicesOptions
            {
                Cwd = _cwd,
                AgentDir = _agentDir,
                ModelRuntime = runtime,
                ResourceLoaderOptions = new ResourceLoaderExtraOptions
                {
                    NoSkills = true,
                    NoPromptTemplates = true,
                    NoThemes = true,
                    NoContextFiles = true,
                    ExtensionFactories = [extension],
                },
            });

        var result = await AgentSessionServicesBuilder.CreateAgentSessionFromServicesAsync(
            new CreateAgentSessionFromServicesOptions
            {
                Services = services,
                SessionManager = SessionManager.Create(_cwd, _sessionDir),
                SessionStartEvent = new ExtensionEvent.SessionStart(SessionEventReason.Resume, "/previous.jsonl"),
                Model = provider.Model,
            },
            _cwd);

        Assert.Empty(events);
        await result.Session.BindExtensionsAsync(new ExtensionBindings());

        // 绑定扩展时才发出——且带的是创建会话时传入的那一份。
        var start = Assert.IsType<ExtensionEvent.SessionStart>(Assert.Single(events));
        Assert.Equal(SessionEventReason.Resume, start.Reason);
        Assert.Equal("/previous.jsonl", start.PreviousSessionFile);
    }

    [Fact]
    public async Task BeforeAgentStartFiresForRunsStartedByCustomMessages()
    {
        // 上游 a447ba11f / #10739：triggerTurn 的自定义消息起的这一轮也要走 before_agent_start，
        // 否则首次工具调用之后的 next-turn 刷新会退回基础提示词选项，把处理器加进去的段落刷掉，
        // 运行途中改系统提示词、破掉 provider 的提示词缓存。
        var events = new List<ExtensionEvent.BeforeAgentStart>();
        var (host, _) = await CreateHostAsync(pi =>
            pi.On<ExtensionEvent.BeforeAgentStart>(ExtensionEventNames.BeforeAgentStart, (@event, _) =>
            {
                events.Add(@event);
                return Task.CompletedTask;
            }));

        await host.Session.PromptAsync("hello");
        var user = Assert.IsType<ExtensionEvent.BeforeAgentStart.User>(Assert.Single(events));
        Assert.Equal("user", user.MessageType);
        Assert.Equal("hello", user.Prompt);

        events.Clear();
        await host.Session.SendCustomMessageAsync(
            new CustomMessageDraft
            {
                CustomType = "note",
                Content = MessageContent.From("ping"),
                Display = false,
            },
            new SendMessageOptions { TriggerTurn = true });

        var custom = Assert.IsType<ExtensionEvent.BeforeAgentStart.Custom>(Assert.Single(events));
        Assert.Equal("custom", custom.MessageType);
        Assert.Equal("note", custom.Message.CustomType);
        // 自定义消息本身就是本轮的起始消息，不再额外造一条用户消息。
        Assert.Equal([custom.Message], host.Session.Agent.Messages.OfType<CustomMessage>().Take(1));
    }

    /// <summary>用于证明某个回调被调用（且参数正确）的哨兵异常。</summary>
    private sealed class SentinelException : Exception
    {
    }
}
