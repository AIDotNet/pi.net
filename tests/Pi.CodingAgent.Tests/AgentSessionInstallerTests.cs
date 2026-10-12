using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Providers;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

// The extension contract lives in Pi.CodingAgent.Core.Extensions and binds several names that also exist
// in Pi.Ai.Types / Pi.CodingAgent.Core. These aliases reproduce the binding the production code gets.
using AiToolDefinition = Pi.Ai.Types.ToolDefinition;
using ExtensionToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 4e-3e-9h-3：<c>agent-session.ts</c> 的 6 个 installer（TS 502-507 / 652-960 / 1766-1836）、
/// <c>_handleAgentEvent</c>（TS 1102-1187）与生命周期叶子（<c>dispose</c> / <c>refreshContext</c> /
/// <c>abortBash</c>）。逐条对照 TS 参考实现
/// （<c>D:\AI\参考项目\pi\packages\coding-agent\src\core\agent-session.ts</c>）。
/// </summary>
/// <remarks>
/// 与 <see cref="AgentSessionBuildTests"/> 的分工：那边验「构造器字段区 + <c>_buildRuntime</c> 系」，
/// 这边验「装进 Pi.Agent 的回调」与「Pi.Agent 事件 → 会话事件/持久化」的翻译层。
/// </remarks>
public sealed class AgentSessionInstallerTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;

    public AgentSessionInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-agent-session-installers-" + Guid.NewGuid().ToString("N"));
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

    private static ExtensionToolDefinition CustomTool(
        string name,
        Func<IToolLoadout, ToolLoadoutChanges?>? prepareLoadout = null,
        Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, AgentToolUpdateCallback?,
            IExtensionToolContext?, Task<AgentToolResult>>? execute = null)
        => new()
        {
            Name = name,
            Label = name,
            Description = $"desc:{name}",
            Parameters = new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }),
            PrepareLoadout = prepareLoadout,
            Execute = execute
                ?? ((_, _, _, _, _) => Task.FromResult(new AgentToolResult([new TextContent("ok")]))),
        };

    private async Task<AgentSession> CreateSessionAsync(
        IReadOnlyList<InlineExtension>? extensions = null,
        IReadOnlyList<ExtensionToolDefinition>? customTools = null,
        IReadOnlyList<string>? initialActiveToolNames = null,
        StreamFn? streamFn = null,
        Func<AgentTurnContext, CancellationToken, Task<AgentTurnDecision?>>? finishTurn = null,
        Func<AgentTurnContext, Task<AgentLoopTurnUpdate?>>? prepareNextTurn = null)
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
        var agent = new Pi.Agent.Agent(new AgentOptions
        {
            StreamFn = streamFn ?? ThrowingStreamFn(),
            FinishTurn = finishTurn,
            PrepareNextTurn = prepareNextTurn,
        });
        var runnerRef = new ExtensionRunnerRef();
        var session = new AgentSession(new AgentSessionConfig
        {
            Agent = agent,
            SessionManager = SessionManager.InMemory(_cwd),
            SettingsManager = settings,
            Cwd = _cwd,
            ResourceLoader = loader,
            ModelRuntime = modelRuntime,
            CustomTools = customTools,
            InitialActiveToolNames = initialActiveToolNames,
            ExtensionRunnerRef = runnerRef,
        });
        Assert.NotNull(runnerRef.Current);
        return session;
    }

    private static ToolSchema EmptySchema() => new(new Dictionary<string, object?>());

    private static ToolCallContent BashCall(string id = "call-1")
        => Faux.ToolCall("bash", new Dictionary<string, object?> { ["command"] = "ls" }, id);

    private static BeforeToolCallContext BeforeContext(string id = "call-1")
        => new(
            new AssistantMessage([new TextContent("go")]),
            BashCall(id),
            new Dictionary<string, object?> { ["command"] = "ls" },
            new AgentContext { Messages = [], Tools = [] });

    private static AfterToolCallContext AfterContext(
        IReadOnlyList<ContentBlock>? content = null,
        object? structuredContent = null,
        bool isError = false,
        string id = "call-1")
        => new(
            new AssistantMessage([new TextContent("go")]),
            BashCall(id),
            new Dictionary<string, object?> { ["command"] = "ls" },
            new AgentToolResult(
                content ?? [new TextContent("out")],
                StructuredContent: structuredContent,
                IsError: isError),
            isError,
            new AgentContext { Messages = [], Tools = [] });

    private static AgentTurnContext TurnContext(
        AssistantMessage? message = null,
        IReadOnlyList<ToolResultMessage>? toolResults = null,
        IReadOnlyList<ChatMessage>? messages = null)
        => new(
            message ?? new AssistantMessage([new TextContent("done")]),
            toolResults ?? [],
            new AgentContext { Messages = [.. messages ?? []], Tools = [] },
            messages ?? []);

    // ==================================================================
    // 1. _installAgentToolHooks（TS 652-716）
    // ==================================================================

    [Fact]
    public async Task ToolHooks_NoHandlers_ReturnNull()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);

        Assert.Null(await session.Agent.BeforeToolCall!(BeforeContext(), CancellationToken.None));
        Assert.Null(await session.Agent.AfterToolCall!(AfterContext(), CancellationToken.None));
    }

    [Fact]
    public async Task BeforeToolCall_ToolCallHandler_BlocksWithReason()
    {
        var seenInput = (IReadOnlyDictionary<string, object?>?)null;
        var seenId = (string?)null;
        var session = await CreateSessionAsync(
            extensions:
            [
                Ext("demo", pi => pi.On<ExtensionEvent.CustomToolCall, ToolCallEventResult>(
                    ExtensionEventNames.ToolCall,
                    (@event, _) =>
                    {
                        seenId = @event.ToolCallId;
                        seenInput = @event.Input;
                        return Task.FromResult<ToolCallEventResult?>(
                            new ToolCallEventResult { Block = true, Reason = "no" });
                    })),
            ],
            initialActiveToolNames: []);

        var result = await session.Agent.BeforeToolCall!(BeforeContext("call-9"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Block);
        Assert.Equal("no", result.Reason);
        // 工具调用事件携带 id 与入参（差异 C154：入参是只读字典，扩展无法就地改写）。
        Assert.Equal("call-9", seenId);
        Assert.Equal("ls", seenInput!["command"]);
    }

    [Fact]
    public async Task BeforeToolCall_NestedCallCarriesParentToolCallId()
    {
        var seenParent = (string?)null;
        var session = await CreateSessionAsync(
            extensions:
            [
                Ext("demo", pi => pi.On<ExtensionEvent.CustomToolCall, ToolCallEventResult>(
                    ExtensionEventNames.ToolCall,
                    (@event, _) =>
                    {
                        seenParent = @event.ParentToolCallId;
                        return Task.FromResult<ToolCallEventResult?>(new ToolCallEventResult());
                    })),
            ],
            initialActiveToolNames: []);

        // 顶层调用（钩子以 parentToolCallId = null 装配）不带 parent。
        await session.Agent.BeforeToolCall!(BeforeContext(), CancellationToken.None);
        Assert.Null(seenParent);
    }

    [Fact]
    public async Task AfterToolCall_ToolResultHandler_ReplacesContentAndDropsStaleStructured()
    {
        var session = await CreateSessionAsync(
            extensions:
            [
                Ext("demo", pi => pi.On<ExtensionEvent.CustomToolResult, ToolResultEventResult>(
                    ExtensionEventNames.ToolResult,
                    (_, _) => Task.FromResult<ToolResultEventResult?>(
                        new ToolResultEventResult { Content = [new TextContent("replaced")] }))),
            ],
            initialActiveToolNames: []);

        var result = await session.Agent.AfterToolCall!(
            AfterContext(structuredContent: new { stale = true }), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("replaced", ((TextContent)result!.Content![0]).Text);
        // TS：内容被替换后不再匹配的结构化内容被丢弃。
        Assert.Null(result.StructuredContent);
    }

    [Fact]
    public async Task AfterToolCall_HandlerWithNoPatch_ReturnsNull()
    {
        // 处理器返回空 patch → EmitToolResultAsync 返回 null；文本结果无需归一化 → 无覆盖。
        var session = await CreateSessionAsync(
            extensions:
            [
                Ext("demo", pi => pi.On<ExtensionEvent.CustomToolResult, ToolResultEventResult>(
                    ExtensionEventNames.ToolResult,
                    (_, _) => Task.FromResult<ToolResultEventResult?>(new ToolResultEventResult()))),
            ],
            initialActiveToolNames: []);

        Assert.Null(await session.Agent.AfterToolCall!(AfterContext(), CancellationToken.None));
    }

    [Fact]
    public async Task AfterToolCall_NoHandlers_TextOnlyResultReturnsNull()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        Assert.Null(await session.Agent.AfterToolCall!(AfterContext(), CancellationToken.None));
    }

    // ==================================================================
    // 2. _installAgentNextTurnRefresh（TS 898-940）
    // ==================================================================

    [Fact]
    public async Task PrepareNextTurn_MirrorsAgentStateIntoTheTurnUpdate()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        session.Agent.ThinkingLevel = ThinkingLevel.Medium;

        var update = await session.Agent.PrepareNextTurn!(TurnContext());

        Assert.NotNull(update);
        // TS：context.tools 换成 agent.state.tools（差异 C148 的镜像在下一回合的兑现点）。
        Assert.Equal(
            [.. session.Agent.Tools.Select(tool => tool.Name)],
            [.. update!.Context!.Tools!.Select(tool => tool.Name)]);
        Assert.Same(session.Agent.Model, update.Model);
        Assert.Equal(ThinkingLevel.Medium, update.ThinkingLevel);
    }

    [Fact]
    public async Task PrepareNextTurn_ChainsThePreviousHook()
    {
        var reached = 0;
        var session = await CreateSessionAsync(
            initialActiveToolNames: [],
            // 安装前的上一个 prepareNextTurn：installer 必须把它包在里面（TS 的 previousSnapshot）。
            prepareNextTurn: turn =>
            {
                reached++;
                return Task.FromResult<AgentLoopTurnUpdate?>(
                    new AgentLoopTurnUpdate(Context: turn.Context with { Tools = [] }));
            });

        var update = await session.Agent.PrepareNextTurn!(TurnContext());

        Assert.Equal(1, reached);
        Assert.NotNull(update);
        // 上一个钩子返回的 context 是 installer 的输入（nextContext = previousSnapshot?.Context ?? context），
        // 但 installer 最终把 tools 换回 agent 的镜像。
        Assert.Equal(
            [.. session.Agent.Tools.Select(tool => tool.Name)],
            [.. update!.Context!.Tools!.Select(tool => tool.Name)]);
    }

    // ==================================================================
    // 3. _installAgentRequestProjection（TS 787-836）
    // ==================================================================

    [Fact]
    public async Task PrepareRequest_NonVirtualModel_UsesTheSessionProjection()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        session.SessionManager.AppendMessage(new UserMessage("hi", 1));

        var projection = session.SessionManager.BuildSessionProjection();
        var update = await session.Agent.PrepareRequest!(
            new AgentContext { Messages = [], Tools = [] },
            session.Agent.Model,
            ThinkingLevel.Off,
            CancellationToken.None);

        Assert.NotNull(update);
        // 请求上下文换成会话投影（而不是循环传进来的 messages）。
        Assert.Equal(
            [.. projection.Messages.Select(AgentSession.GetMessageRole)],
            [.. update!.Context!.Messages.Select(AgentSession.GetMessageRole)]);
        Assert.Equal(projection.Messages.Count, update.Context!.Messages.Count);
    }

    [Fact]
    public async Task PrepareRequest_NonVirtualModel_KeepsExecutableTools()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        var update = await session.Agent.PrepareRequest!(
            new AgentContext { Messages = [], Tools = [] },
            session.Agent.Model,
            ThinkingLevel.Off,
            CancellationToken.None);

        // 消息声明 provider 可见的装载；context.tools 保留可执行实现。
        Assert.Equal(
            [.. session.Agent.Tools.Select(tool => tool.Name)],
            [.. update!.Context!.Tools!.Select(tool => tool.Name)]);
    }

    // ==================================================================
    // 4. _installAgentBoundaryHooks（TS 886-896）
    // ==================================================================

    [Fact]
    public async Task FinishTurn_NoTurnEndHandlers_ReturnsNullDecision()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        var decision = await session.Agent.FinishTurn!(TurnContext(), CancellationToken.None);
        Assert.Null(decision);
    }

    [Fact]
    public async Task FinishTurn_TurnEndContinue_WithoutRunnableContext_IsReportedInvalid()
    {
        var session = await CreateSessionAsync(
            extensions:
            [
                Ext("demo", pi => pi.On<ExtensionEvent.TurnEnd, BoundaryResult>(
                    ExtensionEventNames.TurnEnd,
                    (@event, _) => Task.FromResult<BoundaryResult?>(
                        new BoundaryResult { Continue = true }))),
            ],
            initialActiveToolNames: []);

        // 让助手消息可被溯源（TS 的 _findPersistedMessageEntryId 走 branch 的引用同一性）。
        var assistant = new AssistantMessage([new TextContent("done")]);
        session.SessionManager.AppendMessage(assistant);

        // 投影尾条是 assistant 且无排队消息 → 上下文不可继续 → 上报并放弃继续（决策为 null）。
        var decision = await session.Agent.FinishTurn!(
            TurnContext(assistant, messages: [assistant]), CancellationToken.None);

        Assert.Null(decision);
    }

    [Fact]
    public async Task FinishTurn_TurnEndContinue_WithQueuedMessages_DecisionIsContinue()
    {
        var session = await CreateSessionAsync(
            extensions:
            [
                Ext("demo", pi => pi.On<ExtensionEvent.TurnEnd, BoundaryResult>(
                    ExtensionEventNames.TurnEnd,
                    (@event, _) => Task.FromResult<BoundaryResult?>(
                        new BoundaryResult { Continue = true }))),
            ],
            initialActiveToolNames: []);

        var assistant = new AssistantMessage([new TextContent("done")]);
        session.SessionManager.AppendMessage(assistant);
        // 队列消息本身就构成新上下文（ComputeBoundaryCanContinue 的 turn_end 分支）。
        session.Agent.Steer(new UserMessage("more", 2));

        var decision = await session.Agent.FinishTurn!(
            TurnContext(assistant, messages: [assistant]), CancellationToken.None);

        Assert.IsType<AgentTurnDecision.Continue>(decision);
    }

    [Fact]
    public async Task FinishTurn_PreviousEndWinsOverExtensionContinue()
    {
        var session = await CreateSessionAsync(
            extensions:
            [
                Ext("demo", pi => pi.On<ExtensionEvent.TurnEnd, BoundaryResult>(
                    ExtensionEventNames.TurnEnd,
                    (_, _) => Task.FromResult<BoundaryResult?>(new BoundaryResult { Continue = true }))),
            ],
            initialActiveToolNames: [],
            // 安装前的上一个 finishTurn 明确 End：扩展的 continue 不能翻盘。
            finishTurn: (_, _) => Task.FromResult<AgentTurnDecision?>(new AgentTurnDecision.End()));

        var assistant = new AssistantMessage([new TextContent("done")]);
        session.SessionManager.AppendMessage(assistant);

        var decision = await session.Agent.FinishTurn!(
            TurnContext(assistant, messages: [assistant]), CancellationToken.None);

        Assert.IsType<AgentTurnDecision.End>(decision);
    }

    // ==================================================================
    // 5. _installHiddenDeclarationsProjection（TS 1766-1784）
    // ==================================================================

    [Fact]
    public async Task HiddenDeclarationsProjection_StripsHiddenToolsFromDeclarations()
    {
        var session = await CreateSessionAsync(
            customTools:
            [
                CustomTool("hider", prepareLoadout: _ => new ToolLoadoutChanges
                {
                    HiddenDeclarations = ["bash"],
                }),
            ],
            initialActiveToolNames: []);

        var messages = new List<ChatMessage>
        {
            new SystemMessage(
                MessageContent.From("prompt"),
                ToolsAdded:
                [
                    new AiToolDefinition("bash", "run bash", EmptySchema()),
                    new AiToolDefinition("read", "read a file", EmptySchema()),
                ],
                ToolsRemoved: ["grep", "bash"]),
        };

        var transformed = await session.Agent.TransformContext!(messages, CancellationToken.None);

        var system = Assert.IsType<SystemMessage>(Assert.Single(transformed));
        Assert.Equal(["read"], system.ToolsAdded!.Select(tool => tool.Name));
        Assert.Equal(["grep"], system.ToolsRemoved!);
    }

    [Fact]
    public async Task HiddenDeclarationsProjection_NoHiddenDeclarations_IsPassthrough()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        var messages = new List<ChatMessage>
        {
            new SystemMessage(MessageContent.From("prompt"),
                ToolsAdded: [new AiToolDefinition("bash", "run bash", EmptySchema())]),
        };

        var transformed = await session.Agent.TransformContext!(messages, CancellationToken.None);
        Assert.Same(messages, transformed);
    }

    // ==================================================================
    // 6. _installAgentForcedPromptProjection（TS 1786-1806）
    // ==================================================================

    [Fact]
    public async Task ForcedPromptProjection_WithoutRunOptions_IsPassthrough()
    {
        // _runSystemPromptOptions 只在 prompt 编排（9h-4）里被 before_agent_start 的结果写入，
        // 故 9h-3 期间强制提示词分支不可达——这里锁住「无强制提示词即透传」这一半。
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        var messages = new List<ChatMessage>
        {
            new SystemMessage(MessageContent.From("prompt")),
            new UserMessage("hi", 1),
        };

        var transformed = await session.Agent.TransformContext!(messages, CancellationToken.None);
        Assert.Same(messages, transformed);
    }

    // ==================================================================
    // 7. _executeNestedToolCall（TS 718-746）
    // ==================================================================

    [Fact]
    public async Task NestedToolCall_ExecutesInnerToolThroughTheSessionHost()
    {
        var innerRan = false;
        var session = await CreateSessionAsync(
            customTools:
            [
                CustomTool("outer", execute: async (_, _, _, _, ctx) =>
                {
                    Assert.NotNull(ctx);
                    var outcome = await ctx!.ExecuteTool(
                        "inner", new Dictionary<string, object?> { ["x"] = 1 });
                    return new AgentToolResult(outcome.Result.Content);
                }),
                CustomTool("inner", execute: (_, _, _, _, _) =>
                {
                    innerRan = true;
                    return Task.FromResult(new AgentToolResult([new TextContent("inner-result")]));
                }),
            ],
            initialActiveToolNames: []);

        // 嵌套调用要能挂到「发出这次调用的助手消息」上。
        session.Agent.Messages = [new AssistantMessage([new TextContent("calling outer")])];

        var sessionEvents = new List<AgentSessionEvent>();
        using var subscription = session.Subscribe(sessionEvents.Add);

        // 走 agent 的工具镜像，拿到的是经 Wrapper 包装、带 runner 工具上下文的那一个。
        var outer = session.Agent.Tools.Single(tool => tool.Name == "outer");
        var result = await outer.Execute("call-1", new Dictionary<string, object?>(), CancellationToken.None, null);

        Assert.True(innerRan);
        Assert.Equal("inner-result", ((TextContent)result.Content[0]).Text);

        // 嵌套执行事件同时发往扩展与会话监听器（NestedToolExecutionEvent → 两侧映射）。
        var start = Assert.Single(sessionEvents.OfType<AgentSessionEvent.ToolExecutionStartEvent>());
        Assert.Equal("inner", start.ToolName);
        Assert.Equal("call-1", start.ParentToolCallId);
        Assert.Contains(sessionEvents, e => e is AgentSessionEvent.ToolExecutionEndEvent { ToolName: "inner" });
    }

    // ==================================================================
    // 8. _handleAgentEvent（TS 1102-1187）
    // ==================================================================

    [Fact]
    public async Task HandleAgentEvent_SimpleTextTurn_MapsEventsAndPersistsMessages()
    {
        var session = await CreateSessionAsync(
            streamFn: Faux.CreateStreamFn([Faux.Response.Text("hello")]),
            initialActiveToolNames: []);

        var events = new List<AgentSessionEvent>();
        using var subscription = session.Subscribe(events.Add);

        await session.Agent.Prompt(new UserMessage("hi", 1));

        // 事件族：agent_start / turn_start / message_start+end(user) /
        // message_start+end(assistant) / turn_end / agent_end。
        Assert.IsType<AgentSessionEvent.AgentStartEvent>(events[0]);
        Assert.IsType<AgentSessionEvent.TurnStartEvent>(events[1]);
        Assert.Contains(events, e => e is AgentSessionEvent.MessageStartEvent { Message: UserMessage });
        Assert.Contains(events, e => e is AgentSessionEvent.MessageStartEvent { Message: AssistantMessage });
        Assert.Contains(events, e => e is AgentSessionEvent.MessageEndEvent { Message: UserMessage });
        Assert.Contains(events, e => e is AgentSessionEvent.MessageEndEvent { Message: AssistantMessage });
        Assert.Contains(events, e => e is AgentSessionEvent.TurnEndEvent);
        Assert.IsType<AgentSessionEvent.AgentEndEvent>(events[^1]);

        // 持久化：user 与 assistant 都落成 SessionMessageEntry。
        var persisted = session.SessionManager.GetBranch().OfType<SessionMessageEntry>().ToList();
        Assert.Contains(persisted, entry => entry.Message is UserMessage);
        var assistantEntry = Assert.Single(persisted, entry => entry.Message is AssistantMessage);
        Assert.Equal("hello", ((TextContent)((AssistantMessage)assistantEntry.Message).Content[0]).Text);
    }

    [Fact]
    public async Task HandleAgentEvent_MessageEndReplacement_IsProjectedAndPersisted()
    {
        // 扩展在 message_end 上替换助手消息（TS _replaceMessageInPlace 的就地改写）。
        var replacement = new AssistantMessage([new TextContent("rewritten")]);
        var session = await CreateSessionAsync(
            extensions:
            [
                Ext("demo", pi => pi.On<ExtensionEvent.MessageEnd, MessageEndEventResult>(
                    ExtensionEventNames.MessageEnd,
                    (@event, _) => Task.FromResult<MessageEndEventResult?>(
                        @event.Message is AssistantMessage
                            ? new MessageEndEventResult { Message = replacement }
                            : null))),
            ],
            streamFn: Faux.CreateStreamFn([Faux.Response.Text("original")]),
            initialActiveToolNames: []);

        var events = new List<AgentSessionEvent>();
        using var subscription = session.Subscribe(events.Add);

        await session.Agent.Prompt(new UserMessage("hi", 1));

        // 公开监听器看到替换后的内容。
        var assistantEnd = Assert.Single(
            events.OfType<AgentSessionEvent.MessageEndEvent>(),
            e => e.Message is AssistantMessage);
        Assert.Equal("rewritten", ((TextContent)((AssistantMessage)assistantEnd.Message).Content[0]).Text);

        // 落库的也是替换后的内容（差异 C152：端口用 replacement 落库 + 投影后续事件）。
        var assistantEntry = Assert.Single(
            session.SessionManager.GetBranch().OfType<SessionMessageEntry>(),
            entry => entry.Message is AssistantMessage);
        Assert.Equal("rewritten", ((TextContent)((AssistantMessage)assistantEntry.Message).Content[0]).Text);
    }

    [Fact]
    public async Task HandleAgentEvent_ToolCallTurn_PersistsToolResult()
    {
        var session = await CreateSessionAsync(
            customTools:
            [
                CustomTool("echo", execute: (_, _, _, _, _) =>
                    Task.FromResult(new AgentToolResult([new TextContent("echoed")]))),
            ],
            streamFn: Faux.CreateStreamFn(
            [
                Faux.Response.ToolUse(Faux.ToolCall("echo", new Dictionary<string, object?>(), id: "call-1")),
                Faux.Response.Text("done"),
            ]),
            initialActiveToolNames: []);

        var events = new List<AgentSessionEvent>();
        using var subscription = session.Subscribe(events.Add);

        await session.Agent.Prompt(new UserMessage("go", 1));

        var toolResult = Assert.Single(
            session.SessionManager.GetBranch().OfType<SessionMessageEntry>(),
            entry => entry.Message is ToolResultMessage);
        Assert.Equal("call-1", ((ToolResultMessage)toolResult.Message).ToolCallId);
        Assert.False(((ToolResultMessage)toolResult.Message).IsError);
        Assert.Contains(events, e => e is AgentSessionEvent.ToolExecutionStartEvent { ToolName: "echo" });
    }

    [Fact]
    public async Task HandleAgentEvent_UserMessageStart_DequeuesMatchingSteeringMessage()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        session.Agent.Steer(new UserMessage("steer me", 1));
        Assert.True(session.Agent.HasQueuedMessages());

        var events = new List<AgentSessionEvent>();
        using var subscription = session.Subscribe(events.Add);

        // 直接触发一次 message_start：队列里的同名文本必须在这一刻出队并刷新队列事件。
        await session.Agent.Prompt(new UserMessage("steer me", 1));

        Assert.False(session.Agent.HasQueuedMessages());
    }

    // ==================================================================
    // 9. 生命周期（TS 1381-1422）
    // ==================================================================

    [Fact]
    public async Task Dispose_UnsubscribesAndClearsListeners_Idempotent()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        var events = new List<AgentSessionEvent>();
        session.Subscribe(events.Add);

        session.Dispose();
        session.Dispose();

        // 解绑后 agent 事件不再到达会话监听器。
        await session.Agent.Prompt(new UserMessage("hi", 1));
        Assert.Empty(events);
    }

    [Fact]
    public async Task Dispose_InvalidatesExtensionRunner()
    {
        var captured = (IExtensionContext?)null;
        var session = await CreateSessionAsync(
            extensions: [Ext("demo", pi => pi.On<ExtensionEvent.CustomToolCall, ToolCallEventResult>(
                ExtensionEventNames.ToolCall,
                (@event, ctx) =>
                {
                    captured = ctx;
                    return Task.FromResult<ToolCallEventResult?>(null);
                }))],
            initialActiveToolNames: []);

        await session.Agent.BeforeToolCall!(BeforeContext(), CancellationToken.None);
        Assert.NotNull(captured);

        session.Dispose();

        // TS：dispose 后捕获的 ctx 变为陈旧，任何使用都抛错。
        var stale = captured!;
        var ex = Assert.Throws<InvalidOperationException>(() => stale.Model);
        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshContext_IsCallable()
    {
        var session = await CreateSessionAsync();
        session.RefreshContext();
        // TS refreshContext() → _refreshFinalizedContext()：只重建投影，不改活跃集。
        Assert.Equal(["read", "bash", "edit", "write"], session.GetActiveToolNames());
    }

    [Fact]
    public async Task AbortBash_IsSafeWithoutRunningCommands()
    {
        var session = await CreateSessionAsync(initialActiveToolNames: []);
        session.AbortBash();
    }
}
