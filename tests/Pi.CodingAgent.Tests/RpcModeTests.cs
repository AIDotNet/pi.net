// ============================================================================
// Tests for RPC mode (TS `modes/rpc/rpc-mode.ts`), landed with 4f-5c.
// Mirrors test/rpc-prompt-response-semantics.test.ts case-for-case: the
// preflight-failure / preflight-success / extension-handled / queued-while-
// streaming / handled-while-queueing / transformed / clear-queue paths.
// Expected dispositions come from the TS bodies; the fixture builds a real
// AgentSessionRuntime over a stub provider (same approach as
// AgentSessionRuntimeTests).
// ============================================================================

using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Rpc;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class RpcModeTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;
    private readonly string _sessionDir;

    public RpcModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-rpc-mode-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_root, "cwd");
        _agentDir = Path.Combine(_cwd, ".pi");
        _sessionDir = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(_agentDir);

        // RPC 的扩展 UI 上下文与 exportToHtml 都读进程级主题单例（TS 的 theme 模块级对象）；
        // 生产里由 CLI 入口 initTheme，测试里自己装一个（同 InteractiveModeStartupTests）。
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
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

    /// <summary>
    /// 对应 TS 测试里的 <c>createAssistantMessageEventStream</c> + 可调 <c>responseDelayMs</c>。
    /// <paramref name="withAuth"/> 为 false 时无凭据，prompt 预检即失败
    /// （TS 的 "No API key found for fake-provider" 路径）。
    /// </summary>
    private sealed class StubProvider(bool withAuth, int responseDelayMs) : IProvider
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

        public ProviderAuth? Auth => withAuth ? new() { ApiKey = new StubApiKeyAuth() } : null;

        public IReadOnlyList<ModelSpec> GetModels() => [Model];

        public IReadOnlyList<ModelSpec> GetAllModels() => [Model];

        public IAssistantMessageEventStream Stream(
            ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
        {
            var stream = new AssistantMessageEventStream();
            var message = new AssistantMessage(
                [new TextContent("done")],
                StopReason.Stop,
                Model: model.Id,
                Api: model.Api,
                Provider: model.Provider);
            _ = Task.Run(async () =>
            {
                await Task.Delay(responseDelayMs);
                stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
                stream.End(message);
            });
            return stream;
        }

        public IAssistantMessageEventStream StreamSimple(
            ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
            => Stream(model, context, options);
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

    private async Task<(AgentSessionRuntime Host, RpcModeHarness Harness)> StartRpcAsync(
        bool withAuth,
        int responseDelayMs,
        Action<IExtensionApi>? configureExtension = null)
    {
        var provider = new StubProvider(withAuth, responseDelayMs);
        var runtime = await CreateRuntimeAsync(provider);
        var extension = configureExtension is null ? null : Ext("rpc-test", configureExtension);

        CreateAgentSessionRuntimeFactory factory = async options =>
        {
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
                        ExtensionFactories = extension is null ? [] : [extension],
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
                SessionManager = SessionManager.Create(_cwd, _sessionDir),
            });

        var harness = new RpcModeHarness(host);
        _ = harness.RunAsync();
        await WaitForAsync(() => harness.Input.Ready);
        return (host, harness);
    }

    /// <summary>RPC 模式的测试驱动器：接管四个 output-guard 接缝 + 输入桩。</summary>
    private sealed class RpcModeHarness(AgentSessionRuntime host)
    {
        private readonly List<JsonObject> _outputs = [];
        private readonly Lock _gate = new();

        public RpcInputStub Input { get; } = new();

        public Task RunAsync() => RpcMode.RunAsync(host, new RpcModeOptions
        {
            // output 既收响应（JsonObject）也收事件投影（message_update 是 JsonObject，
            // 其余是 AgentSessionEvent 记录）——统一序列化成节点再收集。
            Output = value =>
            {
                var node = value as JsonObject
                    ?? JsonNode.Parse(JsonSerializer.Serialize(value, JsonL.Options))!.AsObject();
                lock (_gate)
                {
                    _outputs.Add(node);
                }
            },
            Input = Input,
            TakeOverStdout = false,
            RegisterSignalHandlers = false,
            Exit = _ => { },
            WaitBackpressure = () => Task.CompletedTask,
            FlushOutput = () => Task.CompletedTask,
        });

        public IReadOnlyList<JsonObject> Outputs()
        {
            lock (_gate)
            {
                return [.. _outputs];
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _outputs.Clear();
            }
        }

        public List<JsonObject> Responses(string id)
            => Outputs().Where(o => o["id"]?.GetValue<string>() == id
                && o["type"]?.GetValue<string>() == "response").ToList();

        public List<JsonObject> Responses(string id, string command)
            => Responses(id).Where(o => o["command"]?.GetValue<string>() == command).ToList();

        public void Feed(string json) => Input.Feed(json);
    }

    /// <summary>测试输入桩：抓住 onLine / onEnd 回调，按需喂行。</summary>
    private sealed class RpcInputStub : IRpcInput
    {
        private Action<string>? _onLine;

        public bool Ready => _onLine is not null;

        public void Start(Action<string> onLine, Action? onEnd = null) => _onLine = onLine;

        public void Stop() => _onLine = null;

        public void Feed(string line) => _onLine?.Invoke(line);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("condition not met within timeout");
    }

    private static string Command(object command) => JsonSerializer.Serialize(command);

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Prompt_PreflightFails_EmitsOneErrorResponse()
    {
        var harness = (await StartRpcAsync(withAuth: false, responseDelayMs: 0)).Harness;

        harness.Feed(Command(new { id = "b1", type = "prompt", message = "Hello" }));

        await WaitForAsync(() => harness.Responses("b1", "prompt").Count == 1);
        var response = harness.Responses("b1", "prompt")[0];
        Assert.False(response["success"]!.GetValue<bool>());
        var error = response["error"]!.GetValue<string>();
        Assert.Contains("No API key found for stub", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prompt_PreflightSucceeds_EmitsOneStartedResponse()
    {
        var harness = (await StartRpcAsync(withAuth: true, responseDelayMs: 0)).Harness;

        harness.Feed(Command(new { id = "b2", type = "prompt", message = "Hello" }));

        await WaitForAsync(() => harness.Responses("b2", "prompt").Count == 1);
        var response = harness.Responses("b2", "prompt")[0];
        Assert.True(response["success"]!.GetValue<bool>());
        Assert.Equal("started", response["data"]!["disposition"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExtensionCommandAndInterceptedInput_ReportHandled_NoAgentStart()
    {
        var harness = (await StartRpcAsync(
            withAuth: false,
            responseDelayMs: 0,
            configureExtension: pi =>
            {
                pi.RegisterCommand("handled", new CommandRegistration
                {
                    Handler = (_, _) => Task.CompletedTask,
                });
                pi.On<ExtensionEvent.InputEvent, InputEventResult>(
                    ExtensionEventNames.Input,
                    (@event, _) => Task.FromResult<InputEventResult?>(
                        @event.Text == "handled input" ? new InputEventResult.Handled() : null));
            })).Harness;

        harness.Feed(Command(new { id = "command", type = "prompt", message = "/handled" }));
        await WaitForAsync(() => harness.Responses("command", "prompt").Count == 1);
        harness.Feed(Command(new { id = "input", type = "prompt", message = "handled input" }));
        await WaitForAsync(() => harness.Responses("input", "prompt").Count == 1);

        foreach (var id in new[] { "command", "input" })
        {
            var response = Assert.Single(harness.Responses(id, "prompt"));
            Assert.True(response["success"]!.GetValue<bool>());
            Assert.Equal("handled", response["data"]!["disposition"]!.GetValue<string>());
        }

        Assert.DoesNotContain(
            harness.Outputs(),
            o => o["type"]?.GetValue<string>() == "agent_start");
    }

    [Fact]
    public async Task Prompt_QueuedDuringStreaming_EmitsOneQueuedResponse()
    {
        var harness = (await StartRpcAsync(withAuth: true, responseDelayMs: 300)).Harness;

        harness.Feed(Command(new { id = "b3-start", type = "prompt", message = "Start" }));
        await WaitForAsync(() => harness.Responses("b3-start", "prompt").Count == 1);

        harness.Reset();
        harness.Feed(Command(new
        {
            id = "b3",
            type = "prompt",
            message = "Queue this",
            streamingBehavior = "followUp",
        }));

        await WaitForAsync(() => harness.Responses("b3", "prompt").Count == 1);
        var response = harness.Responses("b3", "prompt")[0];
        Assert.True(response["success"]!.GetValue<bool>());
        Assert.Equal("queued", response["data"]!["disposition"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("steer")]
    [InlineData("follow_up")]
    public async Task QueuedInput_HandledEvenWhenExtensionQueuesAnotherMessage(string type)
    {
        var harness = (await StartRpcAsync(
            withAuth: true,
            responseDelayMs: 600,
            configureExtension: pi =>
            {
                pi.On<ExtensionEvent.InputEvent, InputEventResult>(
                    ExtensionEventNames.Input,
                    (@event, _) =>
                    {
                        if (@event.Text != "A" || @event.Source != InputSource.Rpc)
                        {
                            return Task.FromResult<InputEventResult?>(null);
                        }

                        pi.SendUserMessage("B", new SendUserMessageOptions
                        {
                            DeliverAs = type == "steer" ? DeliverAs.Steer : DeliverAs.FollowUp,
                        });
                        return Task.FromResult<InputEventResult?>(new InputEventResult.Handled());
                    });
            })).Harness;

        harness.Feed(Command(new { id = "start", type = "prompt", message = "Start" }));
        await WaitForAsync(() => harness.Responses("start", "prompt").Count == 1);

        harness.Feed(Command(new { id = "A", type, message = "A" }));
        await WaitForAsync(() =>
            harness.Responses("A", type).Count == 1
            && harness.Outputs().Any(o => o["type"]?.GetValue<string>() == "queue_update"));

        var response = harness.Responses("A", type)[0];
        Assert.Equal("handled", response["data"]!["disposition"]!.GetValue<string>());

        var queueUpdate = harness.Outputs().Last(o => o["type"]?.GetValue<string>() == "queue_update");
        var expectedSteering = type == "steer" ? new[] { "B" } : Array.Empty<string>();
        var expectedFollowUp = type == "follow_up" ? new[] { "B" } : Array.Empty<string>();
        Assert.Equal(expectedSteering, queueUpdate["steering"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(expectedFollowUp, queueUpdate["followUp"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Theory]
    [InlineData("steer")]
    [InlineData("follow_up")]
    public async Task QueuedInput_QueuedAfterInputTransformation(string type)
    {
        var harness = (await StartRpcAsync(
            withAuth: false,
            responseDelayMs: 0,
            configureExtension: pi =>
            {
                pi.On<ExtensionEvent.InputEvent, InputEventResult>(
                    ExtensionEventNames.Input,
                    (@event, _) => Task.FromResult<InputEventResult?>(
                        @event.Text == "A" ? new InputEventResult.Transform("B") : null));
            })).Harness;

        harness.Feed(Command(new { id = "A", type, message = "A" }));

        await WaitForAsync(() =>
            harness.Responses("A", type).Count == 1
            && harness.Outputs().Any(o => o["type"]?.GetValue<string>() == "queue_update"));

        var response = harness.Responses("A", type)[0];
        Assert.Equal("queued", response["data"]!["disposition"]!.GetValue<string>());

        var queueUpdate = harness.Outputs().Last(o => o["type"]?.GetValue<string>() == "queue_update");
        var expectedSteering = type == "steer" ? new[] { "B" } : Array.Empty<string>();
        var expectedFollowUp = type == "follow_up" ? new[] { "B" } : Array.Empty<string>();
        Assert.Equal(expectedSteering, queueUpdate["steering"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(expectedFollowUp, queueUpdate["followUp"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task ClearQueue_ReturnsAndClearsQueuedMessages()
    {
        var harness = (await StartRpcAsync(withAuth: true, responseDelayMs: 600)).Harness;

        harness.Feed(Command(new { id = "clear-start", type = "prompt", message = "Start" }));
        await WaitForAsync(() => harness.Responses("clear-start", "prompt").Count == 1);

        harness.Feed(Command(new
        {
            id = "clear-steering",
            type = "prompt",
            message = "Change direction",
            streamingBehavior = "steer",
        }));
        await WaitForAsync(() => harness.Responses("clear-steering", "prompt").Count == 1);

        harness.Feed(Command(new
        {
            id = "clear-follow-up",
            type = "prompt",
            message = "Summarize when finished",
            streamingBehavior = "followUp",
        }));
        await WaitForAsync(() => harness.Responses("clear-follow-up", "prompt").Count == 1);

        harness.Feed(Command(new { id = "clear", type = "clear_queue" }));
        await WaitForAsync(() => harness.Responses("clear", "clear_queue").Count == 1);

        var response = harness.Responses("clear", "clear_queue")[0];
        Assert.True(response["success"]!.GetValue<bool>());
        Assert.Equal(
            new[] { "Change direction" },
            response["data"]!["steering"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(
            new[] { "Summarize when finished" },
            response["data"]!["followUp"]!.AsArray().Select(n => n!.GetValue<string>()));

        // 等这轮跑完：Start 一个回合，被清空的排队不再触发第二个 agent_start。
        await WaitForAsync(() =>
            harness.Outputs().Count(o => o["type"]?.GetValue<string>() == "agent_start") == 1,
            timeoutMs: 5_000);
    }

    [Fact]
    public async Task UnknownCommand_EmitsTheUnknownCommandError()
    {
        var harness = (await StartRpcAsync(withAuth: true, responseDelayMs: 0)).Harness;

        harness.Feed(Command(new { id = "u1", type = "nope" }));

        await WaitForAsync(() => harness.Responses("u1").Count == 1);
        var response = harness.Responses("u1")[0];
        Assert.False(response["success"]!.GetValue<bool>());
        Assert.Equal("nope", response["command"]!.GetValue<string>());
        Assert.Equal("Unknown command: nope", response["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task MalformedLine_EmitsTheParseError()
    {
        var harness = (await StartRpcAsync(withAuth: true, responseDelayMs: 0)).Harness;

        harness.Feed("this is not json");

        await WaitForAsync(() => harness.Outputs().Any(o => o["command"]?.GetValue<string>() == "parse"));
        var response = harness.Outputs().Last(o => o["command"]?.GetValue<string>() == "parse");
        Assert.False(response["success"]!.GetValue<bool>());
        Assert.StartsWith("Failed to parse command: ", response["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetState_ReturnsTheSessionStateProjection()
    {
        var harness = (await StartRpcAsync(withAuth: true, responseDelayMs: 0)).Harness;

        harness.Feed(Command(new { id = "g1", type = "get_state" }));

        await WaitForAsync(() => harness.Responses("g1", "get_state").Count == 1);
        var data = harness.Responses("g1", "get_state")[0]["data"]!;
        Assert.Equal("stub-chat", data["model"]!["id"]!.GetValue<string>());
        Assert.False(data["isStreaming"]!.GetValue<bool>());
        Assert.False(data["isCompacting"]!.GetValue<bool>());
        Assert.Equal("one-at-a-time", data["steeringMode"]!.GetValue<string>());
        Assert.Equal("one-at-a-time", data["followUpMode"]!.GetValue<string>());
        Assert.Equal(0, data["messageCount"]!.GetValue<int>());
        Assert.Equal(0, data["pendingMessageCount"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(data["sessionId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task GetEntries_UnknownSince_EmitsEntryNotFound()
    {
        var harness = (await StartRpcAsync(withAuth: true, responseDelayMs: 0)).Harness;

        harness.Feed(Command(new { id = "e1", type = "get_entries", since = "nonexistent-id" }));

        await WaitForAsync(() => harness.Responses("e1", "get_entries").Count == 1);
        var response = harness.Responses("e1", "get_entries")[0];
        Assert.False(response["success"]!.GetValue<bool>());
        Assert.Equal("Entry not found: nonexistent-id", response["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task SetSessionName_EmptyName_EmitsTheTsError()
    {
        var harness = (await StartRpcAsync(withAuth: true, responseDelayMs: 0)).Harness;

        harness.Feed(Command(new { id = "n1", type = "set_session_name", name = "   " }));

        await WaitForAsync(() => harness.Responses("n1", "set_session_name").Count == 1);
        var response = harness.Responses("n1", "set_session_name")[0];
        Assert.False(response["success"]!.GetValue<bool>());
        Assert.Equal("Session name cannot be empty", response["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExportHtml_WritesTheFileAndReturnsThePath()
    {
        var harness = (await StartRpcAsync(withAuth: true, responseDelayMs: 0)).Harness;
        var outputPath = Path.Combine(_root, "export.html");

        harness.Feed(Command(new { id = "x1", type = "export_html", outputPath }));

        await WaitForAsync(() => harness.Responses("x1", "export_html").Count == 1);
        var response = harness.Responses("x1", "export_html")[0];
        Assert.True(response["success"]!.GetValue<bool>(), response["error"]?.GetValue<string>());
        Assert.Equal(outputPath, response["data"]!["path"]!.GetValue<string>());
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task GetCommands_ListsExtensionCommands()
    {
        var harness = (await StartRpcAsync(
            withAuth: true,
            responseDelayMs: 0,
            configureExtension: pi =>
            {
                pi.RegisterCommand("handled", new CommandRegistration
                {
                    Description = "A handled command",
                    Handler = (_, _) => Task.CompletedTask,
                });
            })).Harness;

        harness.Feed(Command(new { id = "c1", type = "get_commands" }));

        await WaitForAsync(() => harness.Responses("c1", "get_commands").Count == 1);
        var commands = harness.Responses("c1", "get_commands")[0]["data"]!["commands"]!.AsArray();
        var handled = commands.Single(n => n!["name"]!.GetValue<string>() == "handled")!;
        Assert.Equal("A handled command", handled["description"]!.GetValue<string>());
        Assert.Equal("extension", handled["source"]!.GetValue<string>());
    }
}
