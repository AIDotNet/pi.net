// ============================================================================
// Recovery omits tool results together with the truncated tool call.
//
// Upstream 02726086d (#11227): pre-prompt recovery of a recoverable length stop omitted
// the assistant message but not the tool results that answered its calls, because only the
// end-of-run caller passed them in. The orphaned results were persisted, and providers
// rejected every later request. `_omitRecoveryAttempt` now finds the matching results in the
// projected context itself, so every caller omits the pair together.
// ============================================================================

using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class AgentSessionRecoveryOmitsToolResultsTests : IDisposable
{
    /// <summary>只留下一条 user 消息：一旦那对消息被省略，压缩就没有可压的东西。</summary>
    private const string SmallKeepRecentSettings =
        """{"compaction":{"enabled":true,"reserveTokens":1024,"keepRecentTokens":50}}""";

    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;

    public AgentSessionRecoveryOmitsToolResultsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-recovery-omits-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>上游 faux 模型的等价物：maxTokens 1000 让 output=16 落在「可恢复的截断」区间。</summary>
    private static ModelSpec Model() => new()
    {
        Id = "stub-chat",
        Name = "stub-chat",
        Api = "stub-api",
        Provider = "stub",
        BaseUrl = "https://stub.invalid",
        ContextWindow = 100_000,
        MaxTokens = 1_000,
        Input = [ModelInput.Text],
        Cost = new ModelCostRates(0, 0, 0, 0),
    };

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

    /// <summary>只为了把 provider 标成「已配置」——本轮真正的流由 <c>AgentOptions.StreamFn</c> 提供。</summary>
    private sealed class StubProvider : IProvider
    {
        public ModelSpec Model { get; } = Model();

        public string Id => "stub";

        public string Name => "Stub";

        public string? BaseUrl => null;

        public ProviderAuth? Auth => new() { ApiKey = new StubApiKeyAuth() };

        public IReadOnlyList<ModelSpec> GetModels() => [Model];

        public IReadOnlyList<ModelSpec> GetAllModels() => [Model];

        public IAssistantMessageEventStream Stream(
            ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
            => throw new InvalidOperationException("The session drives the agent through AgentOptions.StreamFn.");

        public IAssistantMessageEventStream StreamSimple(
            ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
            => throw new InvalidOperationException("The session drives the agent through AgentOptions.StreamFn.");
    }

    /// <summary>记下**第一份**请求上下文，然后以一句文本收尾。</summary>
    private sealed class RequestRecorder
    {
        public IReadOnlyList<ChatMessage>? First { get; private set; }

        public int Calls { get; private set; }

        public StreamFn Fn => (_, context, _, _) =>
        {
            Calls++;
            First ??= context.Messages;
            var stream = new AssistantMessageEventStream();
            var message = new AssistantMessage(
                [new TextContent("done")],
                StopReason.Stop,
                Model: "stub-chat",
                Api: "stub-api",
                Provider: "stub");
            stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
            stream.End(message);
            return Task.FromResult<IAssistantMessageEventStream>(stream);
        };
    }

    private async Task<(AgentSession Session, Pi.Agent.Agent Agent, RequestRecorder Recorder)> CreateSessionAsync()
    {
        File.WriteAllText(Path.Combine(_agentDir, "settings.json"), SmallKeepRecentSettings);

        var settings = SettingsManager.Create(_cwd, _agentDir);
        var loader = new DefaultResourceLoader(
            _cwd,
            _agentDir,
            settings,
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true);
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
        modelRuntime.RegisterNativeProvider(new StubProvider());
        if (modelRuntime.PendingBackgroundRefresh is { } pending)
        {
            await pending;
        }

        var recorder = new RequestRecorder();
        var agent = new Pi.Agent.Agent(new AgentOptions { StreamFn = recorder.Fn });
        var runnerRef = new ExtensionRunnerRef();
        var session = new AgentSession(new AgentSessionConfig
        {
            Agent = agent,
            SessionManager = SessionManager.InMemory(_cwd),
            SettingsManager = settings,
            Cwd = _cwd,
            ResourceLoader = loader,
            ModelRuntime = modelRuntime,
            Model = Model(),
            InitialActiveToolNames = [],
            ExtensionRunnerRef = runnerRef,
        });
        Assert.NotNull(runnerRef.Current);
        return (session, agent, recorder);
    }

    /// <summary>
    /// 一条被 output token 上限截断的助手消息，跟着一个「没被执行的」工具结果——正是 provider
    /// 会拒绝的落单形态。
    /// </summary>
    private static void SeedTruncatedAttempt(AgentSession session, long now)
    {
        var manager = session.SessionManager;
        manager.AppendMessage(new UserMessage("check status", now - 1000));
        manager.AppendMessage(new AssistantMessage(
            [new ToolCallContent("call_truncated", "bash", new Dictionary<string, object?> { ["command"] = "echo" })],
            StopReason.Length,
            UsageStats: new Usage(100, 16),
            Model: "stub-chat",
            Api: "stub-api",
            Provider: "stub",
            Timestamp: now - 500));
        manager.AppendMessage(new ToolResultMessage(
            "call_truncated",
            "bash",
            [new TextContent("Not executed: output token limit reached.")],
            IsError: true,
            Timestamp: now - 400));
    }

    // ------------------------------------------------------------------ upstream cases

    [Fact]
    public async Task PrePromptRecoveryOfAResumedLengthStopOmitsTheTruncatedCallAndItsResult()
    {
        var (session, agent, recorder) = await CreateSessionAsync();
        SeedTruncatedAttempt(session, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // 恢复的会话：agent 的上下文来自 transcript，而不是运行期积累。
        agent.Messages = session.SessionManager.BuildSessionContext().Messages;

        await session.PromptAsync("continue");

        var request = Assert.IsAssignableFrom<IReadOnlyList<ChatMessage>>(recorder.First);
        Assert.DoesNotContain(request, message => message is ToolResultMessage);
        Assert.DoesNotContain(
            request,
            message => message is AssistantMessage { StopReason: StopReason.Length });
    }

    [Fact]
    public async Task RecoveryOmitsExactlyTheCallAndItsAnswer()
    {
        // 反向断言：剔除范围必须正好是「截断的调用 + 应答它的结果」，多剔一条就会悄悄丢上下文。
        var (session, agent, _) = await CreateSessionAsync();
        SeedTruncatedAttempt(session, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        agent.Messages = session.SessionManager.BuildSessionContext().Messages;

        await session.PromptAsync("continue");

        var branch = session.SessionManager.GetBranch();
        string KindOf(string? targetId) => branch
            .OfType<SessionMessageEntry>()
            .FirstOrDefault(entry => entry.Id == targetId)?.Message switch
        {
            AssistantMessage => "assistant",
            ToolResultMessage => "toolResult",
            _ => "other",
        };

        var edits = branch.OfType<ContextEditEntry>().ToList();
        Assert.Equal(
            ["assistant", "toolResult"],
            edits.Select(edit => KindOf(edit.TargetId)).OrderBy(kind => kind, StringComparer.Ordinal));
        // 两条都是「剔除」（replacement 为 null），不是替换。
        Assert.All(edits, edit => Assert.Null(edit.Replacement));
    }
}
