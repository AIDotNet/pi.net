// ============================================================================
// Tests for the manual compact entry point (agent-session.ts L2749-2909).
//
// The orchestration body (`CompactAsync`) landed with the /compact command; this
// file pins the entry itself: the abort-then-compact ordering, the preparation
// failures ("Already compacted" / "Nothing to compact (session too small)"), the
// `session_before_compact` hook's cancel and custom-result branches, the
// compaction entry that lands in the session, and the compaction_start /
// compaction_end event pair. Expected values are read off the TS source.
// ============================================================================

using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class AgentSessionCompactEntryTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;

    public AgentSessionCompactEntryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-compact-entry-" + Guid.NewGuid().ToString("N"));
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

    private static ModelSpec Model() => new()
    {
        Id = "stub-chat",
        Name = "stub-chat",
        Api = "stub-api",
        Provider = "stub",
        BaseUrl = "https://stub.invalid",
        ContextWindow = 100_000,
        MaxTokens = 4096,
    };

    /// <summary>一句文本收尾的流：摘要请求的唯一应答。</summary>
    private static StreamFn SummaryStreamFn(string text, Action? onCall = null)
        => (_, _, _, _) =>
        {
            onCall?.Invoke();
            var stream = new AssistantMessageEventStream();
            var message = new AssistantMessage(
                [new TextContent(text)],
                StopReason.Stop,
                Model: "stub-chat",
                Api: "stub-api",
                Provider: "stub");
            stream.Push(new AssistantMessageEvent.Done(StopReason.Stop, message));
            stream.End(message);
            return Task.FromResult<IAssistantMessageEventStream>(stream);
        };

    private static InlineExtension Ext(string name, Action<IExtensionApi> configure)
        => new InlineExtension.Described(name, pi =>
        {
            configure(pi);
            return Task.CompletedTask;
        });

    /// <summary>
    /// 把 keepRecentTokens 压到 50 token：四条各 ~400 字符的问答对足以越过预算，
    /// 切分点因此出现在会话后部，前段进入摘要。
    /// </summary>
    private const string SmallKeepRecentSettings =
        """{"compaction":{"enabled":true,"reserveTokens":1024,"keepRecentTokens":50}}""";

    private async Task<AgentSession> CreateSessionAsync(
        string summaryText = "SUMMARY-TEXT",
        string? settingsJson = SmallKeepRecentSettings,
        IReadOnlyList<InlineExtension>? extensions = null,
        Action? onStream = null)
    {
        if (settingsJson is not null)
        {
            File.WriteAllText(Path.Combine(_agentDir, "settings.json"), settingsJson);
        }

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
        var agent = new Pi.Agent.Agent(new AgentOptions { StreamFn = SummaryStreamFn(summaryText, onStream) });
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
            ExtensionRunnerRef = runnerRef,
        });
        Assert.NotNull(runnerRef.Current);
        return session;
    }

    /// <summary>
    /// 四条「长 user + 短 assistant」的问答对。短 assistant 让预算在最后一个 user 消息处
    /// 才被越过，切分点因此落在回合起点（非 split turn），默认摘要器只流一次——
    /// 与 TS 的 <c>startsTurn</c> 分支对应。
    /// </summary>
    private static void SeedConversation(AgentSession session, int pairs = 4)
    {
        for (var i = 0; i < pairs; i++)
        {
            session.SessionManager.AppendMessage(
                new UserMessage([new TextContent($"question {i} " + new string('x', 400))], 1));
            session.SessionManager.AppendMessage(new AssistantMessage(
                [new TextContent($"answer {i}")],
                StopReason.Stop,
                Model: "stub-chat",
                Api: "stub-api",
                Provider: "stub"));
        }
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task CompactAsync_AppendsTheCompactionEntryAndEmitsTheEventPair()
    {
        var streamCalls = 0;
        var session = await CreateSessionAsync(onStream: () => streamCalls++);
        SeedConversation(session);
        var branchBefore = session.SessionManager.GetBranch().Count;
        var events = new List<AgentSessionEvent>();
        using var subscription = session.Subscribe(events.Add);

        var result = await session.CompactAsync(null);

        // 摘要来自默认摘要器（模型流被调用一次），不再是缝。
        Assert.Equal(1, streamCalls);
        Assert.Equal("SUMMARY-TEXT", result.Summary);
        Assert.True(result.TokensBefore > 0);
        Assert.NotNull(result.EstimatedTokensAfter);

        // 会话树末尾落下压缩条目，字段与结果一致。
        var branch = session.SessionManager.GetBranch();
        Assert.Equal(branchBefore + 1, branch.Count);
        var compaction = Assert.IsType<CompactionEntry>(branch[^1]);
        Assert.Equal("SUMMARY-TEXT", compaction.Summary);
        Assert.Equal(result.FirstKeptEntryId, compaction.FirstKeptEntryId);
        Assert.Equal(result.TokensBefore, compaction.TokensBefore);
        // TS 的 entry 恒带 fromHook 字段：手动压缩写 false（不是省略）。
        Assert.Equal(false, compaction.FromHook);

        // compaction_start → compaction_end(aborted:false) 事件对，结果随行。
        var start = Assert.Single(events, e => e is AgentSessionEvent.CompactionStartEvent)
            as AgentSessionEvent.CompactionStartEvent;
        Assert.Equal(CompactionReason.Manual, start!.Reason);
        var end = Assert.Single(events, e => e is AgentSessionEvent.CompactionEndEvent)
            as AgentSessionEvent.CompactionEndEvent;
        Assert.Equal(CompactionReason.Manual, end!.Reason);
        Assert.False(end.Aborted);
        Assert.False(end.WillRetry);
        Assert.Null(end.ErrorMessage);
        Assert.NotNull(end.Result);
        Assert.Equal("SUMMARY-TEXT", end.Result!.Summary);
    }

    [Fact]
    public async Task CompactAsync_AlreadyCompacted_ThrowsTheTsMessage()
    {
        var session = await CreateSessionAsync();
        SeedConversation(session);
        await session.CompactAsync(null);

        // 末尾已是压缩条目：TS 的 prepareCompaction 返回 undefined，报 "Already compacted"。
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompactAsync(null));

        Assert.Equal("Already compacted", error.Message);
    }

    [Fact]
    public async Task CompactAsync_SessionTooSmall_ThrowsNothingToCompact()
    {
        // 默认 keepRecentTokens（20000）下一条短消息装得下：无可摘要内容。
        var session = await CreateSessionAsync(settingsJson: null);
        session.SessionManager.AppendMessage(new UserMessage([new TextContent("hi")], 1));
        var events = new List<AgentSessionEvent>();
        using var subscription = session.Subscribe(events.Add);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompactAsync(null));

        Assert.Equal("Nothing to compact (session too small)", error.Message);
        var end = Assert.Single(events, e => e is AgentSessionEvent.CompactionEndEvent)
            as AgentSessionEvent.CompactionEndEvent;
        Assert.False(end!.Aborted);
        Assert.Equal("Compaction failed: Nothing to compact (session too small)", end.ErrorMessage);
    }

    [Fact]
    public async Task CompactAsync_ExtensionCancels_ThrowsCancelledAndEmitsAborted()
    {
        var streamCalls = 0;
        var extensionEvents = new List<ExtensionEvent>();
        var session = await CreateSessionAsync(
            onStream: () => streamCalls++,
            extensions:
            [
                Ext("cancel-compact", pi =>
                {
                    pi.On<ExtensionEvent.SessionBeforeCompact, SessionBeforeCompactResult>(
                        ExtensionEventNames.SessionBeforeCompact,
                        (_, _) => Task.FromResult<SessionBeforeCompactResult?>(
                            new SessionBeforeCompactResult { Cancel = true }));
                    pi.On<ExtensionEvent.SessionCompactFailed>(
                        ExtensionEventNames.SessionCompactFailed,
                        (@event, _) =>
                        {
                            extensionEvents.Add(@event);
                            return Task.CompletedTask;
                        });
                }),
            ]);
        SeedConversation(session);
        var events = new List<AgentSessionEvent>();
        using var subscription = session.Subscribe(events.Add);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompactAsync(null));

        // TS: result.cancel → throw new Error("Compaction cancelled")；abort 由取消标志合成。
        Assert.Equal(AgentSession.CompactionCancelledMessage, error.Message);
        Assert.Equal(0, streamCalls);
        Assert.Empty(session.SessionManager.GetBranch().OfType<CompactionEntry>());

        var end = Assert.Single(events, e => e is AgentSessionEvent.CompactionEndEvent)
            as AgentSessionEvent.CompactionEndEvent;
        Assert.True(end!.Aborted);
        // TS：aborted 时 errorMessage 为 undefined（整键不进事件）。
        Assert.Null(end.ErrorMessage);

        var failed = Assert.IsType<ExtensionEvent.SessionCompactFailed>(Assert.Single(extensionEvents));
        Assert.Equal(CompactionReason.Manual, failed.Reason);
        Assert.True(failed.Aborted);
        Assert.Null(failed.ErrorMessage);
    }

    [Fact]
    public async Task CompactAsync_ExtensionProvidesTheCompaction_SkipsTheModel()
    {
        var streamCalls = 0;
        var extensionEvents = new List<ExtensionEvent>();
        var session = await CreateSessionAsync(
            onStream: () => streamCalls++,
            extensions:
            [
                Ext("own-compact", pi =>
                {
                    pi.On<ExtensionEvent.SessionBeforeCompact, SessionBeforeCompactResult>(
                        ExtensionEventNames.SessionBeforeCompact,
                        (_, _) => Task.FromResult<SessionBeforeCompactResult?>(
                            new SessionBeforeCompactResult
                            {
                                Compaction = new CompactionResult(
                                    "EXT-SUMMARY",
                                    FirstKeptEntryId: "kept-entry",
                                    TokensBefore: 123,
                                    EstimatedTokensAfter: null,
                                    Usage: null,
                                    Details: new { marker = 1 }),
                            }));
                    pi.On<ExtensionEvent.SessionCompact>(
                        ExtensionEventNames.SessionCompact,
                        (@event, _) =>
                        {
                            extensionEvents.Add(@event);
                            return Task.CompletedTask;
                        });
                }),
            ]);
        SeedConversation(session);

        var result = await session.CompactAsync(null);

        // 扩展供了结果：默认摘要器整段跳过，模型流一次都没调用。
        Assert.Equal(0, streamCalls);
        Assert.Equal("EXT-SUMMARY", result.Summary);
        Assert.Equal("kept-entry", result.FirstKeptEntryId);
        Assert.Equal(123, result.TokensBefore);

        var compaction = Assert.IsType<CompactionEntry>(session.SessionManager.GetBranch()[^1]);
        Assert.Equal("EXT-SUMMARY", compaction.Summary);
        Assert.Equal(true, compaction.FromHook);

        var compacted = Assert.IsType<ExtensionEvent.SessionCompact>(Assert.Single(extensionEvents));
        Assert.True(compacted.FromExtension);
        Assert.Equal(CompactionReason.Manual, compacted.Reason);
        Assert.False(compacted.WillRetry);
    }
}
