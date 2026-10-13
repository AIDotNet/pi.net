using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 4e-3e-9h-4b：<c>createAgentSession</c> 的装配本体（TS <c>core/sdk.ts:181-468</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="SdkTests"/> 的分工：后者覆盖工具名匹配与装配**叶子**（显式入参的纯函数）；
/// 本文件覆盖**装配本体**——cwd/agentDir 解析、模型恢复、思考档位回退、资源加载器 reload、
/// <c>Agent</c> 装配（含 <c>streamFn</c> / 缓存温热 / 三个 provider 钩子）、会话元数据补写。
/// </para>
/// <para>
/// 断言偏向「TS 的求值顺序与产物」：分支结果（模型 / 档位 / 警告文案）、落到会话分支上的条目、
/// 以及真正抵达 provider 的请求选项。逐行对照 TS 参考实现
/// （<c>D:\AI\参考项目\pi\packages\coding-agent\src\core\sdk.ts</c>）。
/// </para>
/// </remarks>
public sealed class SdkCreateAgentSessionTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;

    public SdkCreateAgentSessionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-sdk-create-session-" + Guid.NewGuid().ToString("N"));
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

    // ==================================================================
    // 夹具
    // ==================================================================

    private static InlineExtension Ext(string name, Action<IExtensionApi> configure)
        => new InlineExtension.Described(name, pi =>
        {
            configure(pi);
            return Task.CompletedTask;
        });

    private sealed class StubApiKeyAuth(IReadOnlyDictionary<string, string?>? headers = null) : IApiKeyAuth
    {
        public string Name => "stub-api-key";

        public Task<AuthResult?> ResolveAsync(
            Credential.ApiKey? credential, IAuthContext ctx, CancellationToken cancellationToken = default)
            => Task.FromResult<AuthResult?>(new AuthResult
            {
                Auth = new ModelAuth { ApiKey = credential?.Key ?? "stub-key", Headers = headers },
                Source = "stub-env",
            });
    }

    /// <summary>记录抵达 provider 的请求选项，并以一条终态消息立即结束流。</summary>
    private sealed class RecordingProvider(string modelId = "stub-chat") : IProvider
    {
        public ModelSpec Model { get; } = new()
        {
            Id = modelId,
            Name = modelId,
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

        /// <summary>认证层解析出的请求头（用于验证 <c>transformHeaders</c> 的输入）。</summary>
        public IReadOnlyDictionary<string, string?>? AuthHeaders { get; init; }

        public IReadOnlyDictionary<string, object?>? LastOptions { get; private set; }

        public string Id => "stub";

        public string Name => "Stub";

        public string? BaseUrl => null;

        public ProviderAuth? Auth => new() { ApiKey = new StubApiKeyAuth(AuthHeaders) };

        public IReadOnlyList<ModelSpec> GetModels() => [Model];

        public IReadOnlyList<ModelSpec> GetAllModels() => [Model];

        public IAssistantMessageEventStream Stream(
            ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
            => Record(model, options);

        public IAssistantMessageEventStream StreamSimple(
            ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
            => Record(model, options);

        private IAssistantMessageEventStream Record(
            ModelSpec model, IReadOnlyDictionary<string, object?>? options)
        {
            LastOptions = options;
            var stream = new AssistantMessageEventStream();
            var message = new AssistantMessage(
                Content: [],
                StopReason: StopReason.Stop,
                Model: model.Id,
                Api: model.Api,
                Provider: model.Provider);
            // 与真实 provider 同款收尾：先推终态事件，再 End（只 End 不推事件的话，
            // agent 循环会把它当作「流意外结束」而合成 error 终态）。
            stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
            stream.End(message);
            return stream;
        }
    }

    private static async Task<ModelRuntime> CreateRuntimeAsync(IProvider? provider = null)
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
        if (provider is not null)
        {
            runtime.RegisterNativeProvider(provider);
            if (runtime.PendingBackgroundRefresh is { } pending)
            {
                await pending;
            }
        }

        return runtime;
    }

    private async Task<DefaultResourceLoader> CreateLoaderAsync(
        SettingsManager settings, IReadOnlyList<InlineExtension>? extensions = null)
    {
        var loader = new DefaultResourceLoader(
            _cwd,
            _agentDir,
            settings,
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true,
            noContextFiles: true,
            extensionFactories: extensions);
        await loader.ReloadAsync();
        return loader;
    }

    private async Task<CreateAgentSessionResult> CreateAsync(
        ModelRuntime runtime,
        Func<CreateAgentSessionOptions, CreateAgentSessionOptions>? configure = null,
        SessionManager? sessionManager = null,
        SettingsManager? settings = null,
        IReadOnlyList<InlineExtension>? extensions = null)
    {
        var settingsManager = settings ?? SettingsManager.Create(_cwd, _agentDir);
        var loader = await CreateLoaderAsync(settingsManager, extensions);
        var options = new CreateAgentSessionOptions
        {
            Cwd = _cwd,
            AgentDir = _agentDir,
            ModelRuntime = runtime,
            SettingsManager = settingsManager,
            SessionManager = sessionManager ?? SessionManager.InMemory(_cwd),
            ResourceLoader = loader,
        };
        if (configure is not null)
        {
            options = configure(options);
        }

        return await Sdk.CreateAgentSessionAsync(options, _cwd);
    }

    private static string[] ThinkingLevelsOn(SessionManager sessionManager)
        => [.. sessionManager.GetBranch().OfType<ThinkingLevelChangeEntry>().Select(entry => entry.ThinkingLevel)];

    private static (string Provider, string ModelId)[] ModelChangesOn(SessionManager sessionManager)
        => [.. sessionManager.GetBranch().OfType<ModelChangeEntry>().Select(entry => (entry.Provider, entry.ModelId))];

    // ==================================================================
    // 模型 / 思考档位解析（TS 200-268）
    // ==================================================================

    [Fact]
    public async Task CreateAgentSession_WithoutAnyModel_ReportsNoModelsAvailable()
    {
        var runtime = await CreateRuntimeAsync();

        var result = await CreateAsync(runtime);

        // TS: `modelFallbackMessage = formatNoModelsAvailableMessage()`，model 保持 undefined。
        Assert.Null(result.Session.Model);
        Assert.Equal(AuthGuidance.FormatNoModelsAvailableMessage(), result.ModelFallbackMessage);
        // 无模型时档位强制 "off"（TS 264-268）。
        Assert.Equal(ThinkingLevel.Off, result.Session.ThinkingLevel);
        // 新会话仍会补写 thinking_level_change（TS 435-441）。
        Assert.Equal(["off"], ThinkingLevelsOn(result.Session.SessionManager));
        Assert.Empty(ModelChangesOn(result.Session.SessionManager));
    }

    [Fact]
    public async Task CreateAgentSession_ExplicitModel_PersistsModelAndThinkingMetadata()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);

        var result = await CreateAsync(runtime, options => options with
        {
            Model = provider.Model,
            ThinkingLevel = ThinkingLevel.High,
        });

        Assert.Equal(provider.Model.Id, result.Session.Model!.Id);
        Assert.Equal(ThinkingLevel.High, result.Session.ThinkingLevel);
        Assert.Null(result.ModelFallbackMessage);
        // 新会话记录初始模型与档位，便于下次恢复（TS 435-441）。
        Assert.Equal([("stub", provider.Model.Id)], ModelChangesOn(result.Session.SessionManager));
        Assert.Equal(["high"], ThinkingLevelsOn(result.Session.SessionManager));
    }

    [Fact]
    public async Task CreateAgentSession_ExistingSession_RestoresModelFromBranch()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);
        var sessionManager = SessionManager.InMemory(_cwd);
        sessionManager.AppendModelChange("stub", provider.Model.Id);
        sessionManager.AppendMessage(new UserMessage([new TextContent("hi")], 1));

        var result = await CreateAsync(runtime, sessionManager: sessionManager);

        Assert.Equal(provider.Model.Id, result.Session.Model!.Id);
        Assert.Null(result.ModelFallbackMessage);
        // 存档没有 thinking 条目 → 用全局默认（fresh 设置 → DEFAULT_THINKING_LEVEL = medium）补写
        // （TS 246-250 + 431-434）。
        Assert.Equal(["medium"], ThinkingLevelsOn(sessionManager));
    }

    [Fact]
    public async Task CreateAgentSession_UnrestorableSessionModel_AppendsFallbackModel()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);
        var sessionManager = SessionManager.InMemory(_cwd);
        sessionManager.AppendModelChange("ghost", "nope");
        sessionManager.AppendMessage(new UserMessage([new TextContent("hi")], 1));

        var result = await CreateAsync(runtime, sessionManager: sessionManager);

        // TS: `Could not restore model ghost/nope` 之后经 findInitialModel 兜底，再追加
        // `. Using stub/<id>`（TS 219-240）。
        Assert.Equal(provider.Model.Id, result.Session.Model!.Id);
        Assert.Equal(
            $"Could not restore model ghost/nope. Using stub/{provider.Model.Id}",
            result.ModelFallbackMessage);
    }

    [Fact]
    public async Task CreateAgentSession_ExistingMessages_AreHandedToTheAgent()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);
        var sessionManager = SessionManager.InMemory(_cwd);
        sessionManager.AppendMessage(new UserMessage([new TextContent("previous")], 1));

        var result = await CreateAsync(runtime, sessionManager: sessionManager);

        // TS: `initialState.messages = existingSession.messages`；空 prompt + 空工具不插入前导 system 消息。
        var first = Assert.IsType<UserMessage>(result.Session.Agent.Messages[0]);
        var blocks = Assert.IsType<MessageContent.Blocks>(first.Content);
        Assert.Equal("previous", Assert.IsType<TextContent>(blocks.Value[0]).Text);
    }

    // ==================================================================
    // 工具解析 / 资源加载（TS 270-276 / 461）
    // ==================================================================

    [Fact]
    public async Task CreateAgentSession_AppliesToolAllowlistAndDenylist()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);

        var result = await CreateAsync(runtime, options => options with
        {
            Model = provider.Model,
            Tools = ["read", "bash"],
            ExcludeTools = ["bash"],
        });

        // TS: `initialActiveToolNames` = allowlist 过滤掉 denylist。
        Assert.Equal(["read"], result.Session.GetActiveToolNames());
    }

    [Fact]
    public async Task CreateAgentSession_NoToolsAll_DisablesEveryTool()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);

        var result = await CreateAsync(runtime, options => options with
        {
            Model = provider.Model,
            NoTools = NoToolsMode.All,
        });

        Assert.Empty(result.Session.GetActiveToolNames());
    }

    [Fact]
    public async Task CreateAgentSession_ReturnsExtensionsFromTheResourceLoader()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);

        var result = await CreateAsync(
            runtime,
            options => options with { Model = provider.Model },
            extensions: [Ext("sdk-test", _ => { })]);

        Assert.Single(result.ExtensionsResult.Extensions);
        Assert.Same(result.Session.ResourceLoader.GetExtensions(), result.ExtensionsResult);
    }

    // ==================================================================
    // streamFn / buildRequestOptions（TS 322-413）
    // ==================================================================

    [Fact]
    public async Task StreamFn_ResolvesSettingsTimeoutsAndSessionIdForTheProvider()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);
        var settings = SettingsManager.Create(_cwd, _agentDir);
        settings.SetHttpIdleTimeoutMs(1234);

        var result = await CreateAsync(
            runtime, options => options with { Model = provider.Model }, settings: settings);
        await result.Session.PromptAsync("hi");

        var optionsSent = Assert.IsType<Dictionary<string, object?>>(provider.LastOptions);
        // TS: `options.timeoutMs ?? providerRetrySettings.timeoutMs ?? effectiveTimeoutMs`。
        Assert.Equal(1234L, optionsSent["timeoutMs"]);
        // maxRetryDelayMs 恒有值（设置缺省 60000）。
        Assert.Equal(60_000L, optionsSent["maxRetryDelayMs"]);
        // websocketConnectTimeoutMs / maxRetries 未配置 → 与 TS 一样不出现在请求里。
        Assert.False(optionsSent.ContainsKey("websocketConnectTimeoutMs"));
        Assert.False(optionsSent.ContainsKey("maxRetries"));
        // transformHeaders 是端口侧的桥接键，必须在交给 provider 前摘掉。
        Assert.False(optionsSent.ContainsKey("transformHeaders"));
        // TS: 会话请求带 sessionId；agent 的 signal 由 StreamFn 第 4 参回填。
        Assert.Equal(result.Session.SessionId, optionsSent["sessionId"]);
        Assert.IsType<CancellationToken>(optionsSent["signal"]);
    }

    [Fact]
    public async Task StreamFn_ForwardsTheSessionThinkingLevelAsReasoning()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);

        var result = await CreateAsync(runtime, options => options with
        {
            Model = provider.Model,
            ThinkingLevel = ThinkingLevel.High,
        });
        await result.Session.PromptAsync("hi");

        // TS: agent loop 把 `config.reasoning` 摊进流选项，再原样转给 provider。
        Assert.Equal("high", (provider.LastOptions!)["reasoning"]);
    }

    [Fact]
    public async Task StreamFn_AppliesBeforeProviderHeadersHookAfterAttributionMerge()
    {
        var provider = new RecordingProvider
        {
            AuthHeaders = new Dictionary<string, string?> { ["x-auth"] = "1" },
        };
        var runtime = await CreateRuntimeAsync(provider);

        var result = await CreateAsync(
            runtime,
            options => options with { Model = provider.Model },
            extensions:
            [
                Ext("headers", pi => pi.On<ExtensionEvent.BeforeProviderHeaders>(
                    ExtensionEventNames.BeforeProviderHeaders,
                    (ev, _) =>
                    {
                        ev.Headers["x-ext"] = "yes";
                        return Task.CompletedTask;
                    })),
            ]);
        await result.Session.PromptAsync("hi");

        var headers = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
            (provider.LastOptions!)["headers"]);
        // 认证头先合并，扩展钩子最后覆盖（TS 336-346）。
        Assert.Equal("1", headers["x-auth"]);
        Assert.Equal("yes", headers["x-ext"]);
    }

    [Fact]
    public async Task ProviderHooks_AreWiredToTheExtensionRunner()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);
        var seen = new List<ExtensionEvent>();

        var result = await CreateAsync(
            runtime,
            options => options with { Model = provider.Model },
            extensions:
            [
                Ext("hooks", pi =>
                {
                    pi.On<ExtensionEvent.AfterProviderResponse>(
                        ExtensionEventNames.AfterProviderResponse,
                        (ev, _) =>
                        {
                            seen.Add(ev);
                            return Task.CompletedTask;
                        });
                    pi.On<ExtensionEvent.ProviderStream>(
                        ExtensionEventNames.ProviderStreamEvent,
                        (ev, _) =>
                        {
                            seen.Add(ev);
                            return Task.CompletedTask;
                        });
                    pi.On<ExtensionEvent.BeforeProviderRequest, object?>(
                        ExtensionEventNames.BeforeProviderRequest,
                        (_, _) => Task.FromResult<object?>(new JsonObject { ["patched"] = true }));
                }),
            ]);

        var agent = result.Session.Agent;

        await (agent.OnResponse!)(
            new ProviderResponse
            {
                Status = 200,
                Headers = new Dictionary<string, string> { ["content-type"] = "application/json" },
            },
            provider.Model);
        await (agent.OnProviderStreamEvent!)(new JsonObject { ["delta"] = "x" }, provider.Model);
        var payload = await (agent.OnPayload!)(new JsonObject { ["original"] = true }, provider.Model);

        var response = Assert.Single(seen.OfType<ExtensionEvent.AfterProviderResponse>());
        Assert.Equal(200, response.Status);
        var streamEvent = Assert.Single(seen.OfType<ExtensionEvent.ProviderStream>());
        Assert.Equal(
            ("stub", "stub-api", provider.Model.Id),
            (streamEvent.Provider, streamEvent.Api, streamEvent.Model));
        Assert.True((payload!)["patched"]!.GetValue<bool>());
    }

    [Fact]
    public async Task OnPayload_WithoutHandlers_ReturnsThePayloadUnchanged()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);

        var result = await CreateAsync(runtime, options => options with { Model = provider.Model });
        var original = new JsonObject { ["original"] = true };

        var payload = await (result.Session.Agent.OnPayload!)(original, provider.Model);

        Assert.Same(original, payload);
    }

    // ==================================================================
    // 装配细节
    // ==================================================================

    [Fact]
    public async Task CreateAgentSession_ForwardsSettingsDrivenAgentOptions()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);
        var settings = SettingsManager.Create(_cwd, _agentDir);
        settings.SetSteeringMode("all");
        settings.SetFollowUpMode("all");
        settings.SetTransport("websocket");

        var result = await CreateAsync(
            runtime, options => options with { Model = provider.Model }, settings: settings);

        var agent = result.Session.Agent;
        Assert.Equal(QueueMode.All, agent.SteeringMode);
        Assert.Equal(QueueMode.All, agent.FollowUpMode);
        Assert.Equal("websocket", agent.Transport);
        Assert.Equal(60_000, agent.MaxRetryDelayMs);
        // sessionId 走会话管理器（TS `sessionId: sessionManager.getSessionId()`）。
        Assert.Equal(result.Session.SessionManager.GetSessionId(), agent.SessionId);
        Assert.NotNull(result.Session.CacheWarmer);
    }

    [Fact]
    public async Task CreateAgentSession_WithoutSessionManager_UsesTheDefaultSessionDir()
    {
        var provider = new RecordingProvider();
        var runtime = await CreateRuntimeAsync(provider);
        var settings = SettingsManager.Create(_cwd, _agentDir);
        var loader = await CreateLoaderAsync(settings);

        var result = await Sdk.CreateAgentSessionAsync(new CreateAgentSessionOptions
        {
            Cwd = _cwd,
            AgentDir = _agentDir,
            ModelRuntime = runtime,
            SettingsManager = settings,
            ResourceLoader = loader,
            Model = provider.Model,
        }, _cwd);

        // TS: `SessionManager.create(cwd, getDefaultSessionDir(cwd, agentDir))`。
        Assert.Equal(_cwd, result.Session.SessionManager.GetCwd());
        Assert.StartsWith(_agentDir, result.Session.SessionManager.GetSessionDir());
    }
}
