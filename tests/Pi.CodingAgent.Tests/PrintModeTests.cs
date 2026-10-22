// ============================================================================
// Tests for print mode (TS `modes/print-mode.ts`), landed with 4f-5d.
// Mirrors test/print-mode.test.ts case-for-case: text mode, json mode, and the
// assistant-error exit code. The fixture builds a real AgentSessionRuntime over
// a stub provider (same approach as RpcModeTests); session_shutdown is observed
// through an inline extension, like the TS fake's emit spy.
// ============================================================================

using System.Text;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes;
using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class PrintModeTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;
    private readonly string _sessionDir;

    public PrintModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-print-mode-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_root, "cwd");
        _agentDir = Path.Combine(_cwd, ".pi");
        _sessionDir = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(_agentDir);
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

    /// <summary>末条 assistant 消息的停止原因 / 错误文案可调（TS 的 createAssistantMessage 同款）。</summary>
    private sealed class StubProvider(StopReason stopReason, string? errorMessage) : IProvider
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
        {
            var stream = new AssistantMessageEventStream();
            var message = new AssistantMessage(
                [new TextContent("done")],
                stopReason,
                ErrorMessage: errorMessage,
                Model: model.Id,
                Api: model.Api,
                Provider: model.Provider);
            stream.Push(new AssistantMessageEvent.Done(stopReason, message));
            stream.End(message);
            return stream;
        }

        public IAssistantMessageEventStream StreamSimple(
            ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
            => Stream(model, context, options);
    }

    private async Task<(AgentSessionRuntime Host, List<ExtensionEvent.SessionShutdown> Shutdowns)> CreateHostAsync(
        StopReason stopReason = StopReason.Stop,
        string? errorMessage = null,
        bool disableCompaction = false)
    {
        // 关掉自动重试：错误停止原因不该触发重试循环（否则末条消息不稳定）。
        // 截断（length）场景还要关掉压缩：可恢复的长度截断会触发 overflow 自动压缩，
        // 而压缩会把被截断的助手消息从分支里省略，打印模式就看不到 length 了。
        var settings = disableCompaction
            ? """{"retry":{"enabled":false},"compaction":{"enabled":false}}"""
            : """{"retry":{"enabled":false}}""";
        File.WriteAllText(Path.Combine(_agentDir, "settings.json"), settings);

        var provider = new StubProvider(stopReason, errorMessage);
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

        var shutdowns = new List<ExtensionEvent.SessionShutdown>();
        var extension = new InlineExtension.Described("print-test", pi =>
        {
            pi.On<ExtensionEvent.SessionShutdown>(
                ExtensionEventNames.SessionShutdown,
                (@event, _) =>
                {
                    shutdowns.Add(@event);
                    return Task.CompletedTask;
                });
            return Task.CompletedTask;
        });

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
                SessionManager = SessionManager.Create(_cwd, _sessionDir),
            });

        return (host, shutdowns);
    }

    [Fact]
    public async Task TextMode_EmitsSessionShutdownAndReturnsZero()
    {
        var (host, shutdowns) = await CreateHostAsync();
        var images = new List<ImageContent> { new("abc", "image/png") };

        var exitCode = await PrintMode.RunAsync(host, new PrintModeOptions
        {
            Mode = "text",
            InitialMessage = "Say done",
            InitialImages = images,
        });

        Assert.Equal(0, exitCode);
        var shutdown = Assert.Single(shutdowns);
        Assert.Equal(SessionEventReason.Quit, shutdown.Reason);
    }

    [Fact]
    public async Task JsonMode_EmitsSessionShutdownAndReturnsZero()
    {
        var (host, shutdowns) = await CreateHostAsync();

        var exitCode = await PrintMode.RunAsync(host, new PrintModeOptions
        {
            Mode = "json",
            Messages = ["hello"],
        });

        Assert.Equal(0, exitCode);
        var shutdown = Assert.Single(shutdowns);
        Assert.Equal(SessionEventReason.Quit, shutdown.Reason);
    }

    [Fact]
    public async Task AssistantError_EmitsSessionShutdownAndReturnsNonZero()
    {
        var (host, shutdowns) = await CreateHostAsync(StopReason.Error, "provider failure");

        var originalError = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var exitCode = await PrintMode.RunAsync(host, new PrintModeOptions
            {
                Mode = "text",
                InitialMessage = "Say done",
            });

            Assert.Equal(1, exitCode);
            Assert.Contains("provider failure", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }

        var shutdown = Assert.Single(shutdowns);
        Assert.Equal(SessionEventReason.Quit, shutdown.Reason);
    }

    [Fact]
    public async Task AssistantErrorWithoutMessage_FallsBackToRequestReason()
    {
        // TS: `assistantMsg.errorMessage || \`Request ${assistantMsg.stopReason}\``。
        var (host, _) = await CreateHostAsync(StopReason.Aborted, errorMessage: null);

        var originalError = Console.Error;
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var exitCode = await PrintMode.RunAsync(host, new PrintModeOptions
            {
                Mode = "text",
                InitialMessage = "Say done",
            });

            Assert.Equal(1, exitCode);
            Assert.Contains("Request aborted", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task TruncatedResponse_PrintsPartialTextAndReturnsNonZero()
    {
        // 上游 #10799：撞到输出上限时仍打印部分文本，但要告警并以非零码退出。
        var (host, _) = await CreateHostAsync(StopReason.Length, disableCompaction: true);

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exitCode = await PrintMode.RunAsync(host, new PrintModeOptions
            {
                Mode = "text",
                InitialMessage = "Say done",
            });

            Assert.Equal(1, exitCode);
            Assert.Contains("done", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Response was truncated", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task JsonMode_DoesNotTreatTruncationAsFailure()
    {
        // JSON 模式由消费者读事件流，退出码保持 0（与上游一致：只有 text 模式改退出码）。
        var (host, _) = await CreateHostAsync(StopReason.Length, disableCompaction: true);

        var exitCode = await PrintMode.RunAsync(host, new PrintModeOptions
        {
            Mode = "json",
            Messages = ["hello"],
        });

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task JsonMode_WritesTheSessionHeaderAndEventLinesToStdout()
    {
        var (host, _) = await CreateHostAsync();

        var originalOut = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        try
        {
            var exitCode = await PrintMode.RunAsync(host, new PrintModeOptions
            {
                Mode = "json",
                InitialMessage = "hi",
            });

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var lines = stdout.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // 首行是 session header（TS: getHeader() 先落行），其后是事件流。
        Assert.NotEmpty(lines);
        var first = JsonNode.Parse(lines[0])!;
        Assert.Equal("session", first["type"]?.GetValue<string>());
        Assert.Contains(lines, line => line.Contains("\"agent_settled\"", StringComparison.Ordinal));
    }
}
