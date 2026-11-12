using System.Text.Json.Nodes;
using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// v1.5.1 一批独立单点修复的回归用例：
/// <list type="bullet">
/// <item>cacheWarming 默认档位 streaming → idle（7bcc15c92）。</item>
/// <item>#9126（7a53c310a）：runtime.dispose 在断开持久化前先 abort，让被中止回合的工具/助手消息落库。</item>
/// <item>#9222（c3b938e3e）：核心 reload 在 isStreaming / isCompacting 时拒绝，避免扩展 ctx stale。</item>
/// </list>
/// 三项均为已发布行为改动；去掉修复时对应用例失败。
/// </summary>
public sealed class V152BatchRegressionTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;

    public V152BatchRegressionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-v152-batch-" + Guid.NewGuid().ToString("N"));
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

    // ------------------------------------------------------------------ cacheWarming:idle

    [Fact]
    public void CacheWarmingMode_DefaultsToIdle()
    {
        var settings = SettingsManager.Create(_cwd, _agentDir);
        // 上游 1.5.1 把默认档位改为 idle；旧实现回落 "streaming"。
        Assert.Equal("idle", settings.GetCacheWarmingMode());
    }

    [Fact]
    public void SettingsSchema_CacheWarmingDefaultIsIdle()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "schemas", "settings.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("idle", schema["properties"]!["cacheWarming"]!["default"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ harness

    private sealed class Fixture
    {
        public required AgentSession Session { get; init; }
        public required AgentSessionRuntime Runtime { get; init; }
        public required SessionManager SessionManager { get; init; }
    }

    private async Task<Fixture> CreateAsync(StreamFn streamFn)
    {
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
        var agent = new Pi.Agent.Agent(new AgentOptions { StreamFn = streamFn });
        var runnerRef = new ExtensionRunnerRef();
        var sm = SessionManager.InMemory(_cwd);
        var session = new AgentSession(new AgentSessionConfig
        {
            Agent = agent,
            SessionManager = sm,
            SettingsManager = settings,
            Cwd = _cwd,
            ResourceLoader = loader,
            ModelRuntime = modelRuntime,
            InitialActiveToolNames = [],
            ExtensionRunnerRef = runnerRef,
        });
        Assert.NotNull(runnerRef.Current);
        var services = new AgentSessionServices
        {
            Cwd = _cwd,
            AgentDir = _agentDir,
            ModelRuntime = modelRuntime,
            SettingsManager = settings,
            ResourceLoader = loader,
        };
        CreateAgentSessionRuntimeFactory factory = _ =>
            throw new InvalidOperationException("runtime replacement is not used in these tests");
        var runtime = new AgentSessionRuntime(session, services, factory);
        return new Fixture { Session = session, Runtime = runtime, SessionManager = sm };
    }

    /// <summary>流一直阻塞到其取消令牌被中止，然后以 aborted 助手消息收尾（模拟进行中的回合）。</summary>
    private static StreamFn AbortableBlockingStream(TaskCompletionSource entered) =>
        (_, _, _, ct) =>
        {
            var stream = new AssistantMessageEventStream();
            return Task.Run(async () =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // abort 触发。
                }

                var message = new AssistantMessage([new TextContent("")], StopReason.Aborted);
                stream.Push(new AssistantMessageEvent.Done(StopReason.Aborted, message));
                stream.End(message);
                return (IAssistantMessageEventStream)stream;
            });
        };

    // ------------------------------------------------------------------ #9126

    [Fact]
    public async Task DisposeAsync_SettlesInFlightRunBeforeDisconnecting()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fx = await CreateAsync(AbortableBlockingStream(entered));

        var runTask = fx.Session.RunAgentPromptAsync(new UserMessage("hi", 0));
        await entered.Task;
        // 不 await runTask：dispose 自身必须结算持久化（TS #9124 回归要点）。
        await fx.Runtime.DisposeAsync();

        // 修复后：abort 在断开会话事件之前结算，aborted 助手消息已落库。
        Assert.Contains(
            fx.SessionManager.GetEntries(),
            e => e is SessionMessageEntry { Message: AssistantMessage });
        await runTask; // 不应挂起
    }

    // ------------------------------------------------------------------ #9222

    [Fact]
    public async Task ReloadAsync_WhileStreaming_Throws()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StreamFn blocking = (_, _, _, _) =>
        {
            var stream = new AssistantMessageEventStream();
            return Task.Run(async () =>
            {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
                var message = new AssistantMessage([new TextContent("done")]);
                stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
                stream.End(message);
                return (IAssistantMessageEventStream)stream;
            });
        };
        var fx = await CreateAsync(blocking);

        var runTask = fx.Session.RunAgentPromptAsync(new UserMessage("hi", 0));
        await entered.Task;
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Session.ReloadAsync());
            Assert.Equal("Wait for the current response to finish before reloading.", error.Message);
        }
        finally
        {
            release.TrySetResult();
            await runTask;
        }
    }
}
