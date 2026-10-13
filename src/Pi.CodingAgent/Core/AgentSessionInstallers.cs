using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils.Image;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
// AgentSession 有同名 string 属性 SystemPrompt，类内引用静态类须走别名。
using SystemPromptApi = Pi.CodingAgent.Core.SystemPrompt;

namespace Pi.CodingAgent.Core;

// ============================================================================
// AgentSession 的 agent 接线层 — TS `agent-session.ts` 的 6 个 installer（502-507 / 652-960 /
// 1766-1836）、`_handleAgentEvent`（1102-1187）与其直接依赖的实例成员。
// ============================================================================
//
// 本文件与 `AgentSessionBuild.cs` 的分工：那边是「构造器字段区 + `_buildRuntime` 系」，
// 这里是「装进 Pi.Agent 的回调」与「Pi.Agent 事件 → 会话事件/持久化」的翻译层。
//
// 未落地的叶子一律显式抛 `NotSupportedException`（附批次号），不静默吞掉：
//   · `_runAutoCompaction` → 9h-4（编排本体）
// 对应关系记录在 docs/coding-agent-porting-status.md 的 9h-3 小节。

public partial class AgentSession
{
    /// <summary><c>ConditionalWeakTable</c> 的占位值（TS <c>WeakSet</c> 只关心键）。</summary>
    private static readonly object DispatchedMarker = new();

    /// <summary>空工具入参（TS 里 <c>args as Record&lt;string, unknown&gt;</c> 的结构等价物）。</summary>
    private static readonly IReadOnlyDictionary<string, object?> EmptyToolInput =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// message_end 的替换表（差异 C152）。TS 的 <c>_replaceMessageInPlace</c> 就地改写对象本身，
    /// 于是 agent 状态、后续 turn/agent 事件、监听器与落库都看到新内容；端口的消息记录是**不可变
    /// 快照**（同 <c>StreamAssistantResponse</c> 对流式快照的处理），只能换槽位，故这里记住
    /// target → replacement，供后续事件投影。
    /// </summary>
    private readonly ConditionalWeakTable<object, ChatMessage> _replacedMessages = new();

    // =========================================================================
    // 构造器调用的 6 个 installer（TS 502-507）
    // =========================================================================

    /// <summary>
    /// TS <c>_installAgentToolHooks()</c>（TS 652-655）。回调在执行时读 <c>_extensionRunner</c>，
    /// 故扩展 reload 换 runner 不需要重装钩子。
    /// </summary>
    private void InstallAgentToolHooks()
    {
        _agent.BeforeToolCall = (context, _) => BeforeToolCallAsync(context, null);
        _agent.AfterToolCall = (context, _) => AfterToolCallAsync(context, null);
    }

    /// <summary>
    /// TS <c>_installAgentNextTurnRefresh()</c>（TS 898-940）。<paramref name="parentToolCallId"/>
    /// 非空表示这次调用来自另一个工具的 <c>ctx.executeTool()</c>。
    /// </summary>
    private void InstallAgentNextTurnRefresh()
    {
        // 差异 C155：TS 的 `prepareNextTurnWithContext ?? (prepareNextTurn ? async (_turn, signal) =>
        // await prepareNextTurn(signal) : undefined)` 三层融合在端口的 `Agent.PrepareNextTurn`
        // 已合并为单钩子（Pi.Agent 移植时的既定决定），故这里只剩「串上一个」。
        var previousPrepareNextTurn = _agent.PrepareNextTurn;
        _agent.PrepareNextTurn = async turn =>
        {
            var context = await CompactBeforeNextAssistantResponseAsync(turn.Context).ConfigureAwait(false);
            var previousSnapshot = previousPrepareNextTurn is null
                ? null
                : await previousPrepareNextTurn(turn with { Context = context }).ConfigureAwait(false);
            var nextContext = previousSnapshot?.Context ?? context;

            var runOptions = _runSystemPromptOptions ?? _baseSystemPromptOptions;
            var options = SystemPromptApi.NormalizeBuildSystemPromptOptions(new BuildSystemPromptOptions
            {
                CustomPrompt = runOptions.CustomPrompt,
                ForceSystemPrompt = runOptions.ForceSystemPrompt,
                SelectedTools = GetActiveToolNames(),
                HiddenTools = runOptions.HiddenTools,
                ToolSnippets = MergeSnippets(_baseSystemPromptOptions.ToolSnippets, runOptions.ToolSnippets),
                ToolGuidelines = MergeGuidelines(
                    _baseSystemPromptOptions.ToolGuidelines, runOptions.ToolGuidelines),
                PromptGuidelines = runOptions.PromptGuidelines,
                AppendSystemPrompt = runOptions.AppendSystemPrompt,
                Sections = runOptions.Sections,
                Cwd = runOptions.Cwd,
                ContextFiles = runOptions.ContextFiles,
                Skills = runOptions.Skills,
            });
            var (appliedOptions, updateMessage) = PreparePromptAndToolLoadout(options, nextContext.Messages);
            // 让 session.systemPrompt 与 ctx.getSystemPrompt() 跟上 provider 看到的内容。
            _runSystemPromptOptions = appliedOptions;

            var messages = updateMessage is null
                ? previousSnapshot?.Messages
                : [.. previousSnapshot?.Messages ?? [], updateMessage];

            return new AgentLoopTurnUpdate(
                Context: nextContext with { Tools = [.. _agent.Tools] },
                Messages: messages,
                Model: _agent.Model,
                ThinkingLevel: _agent.ThinkingLevel);
        };
    }

    /// <summary>
    /// TS <c>_installAgentRequestProjection()</c>（TS 787-836）。把请求的上下文换成会话投影，
    /// 并在选中虚拟模型时把这一次请求路由到物理模型。
    /// </summary>
    private void InstallAgentRequestProjection()
    {
        var previousPrepareRequest = _agent.PrepareRequest;
        _agent.PrepareRequest = async (request, model, thinkingLevel, signal) =>
        {
            var failed = _failedResponse;
            _failedResponse = null;

            async Task<(AgentLoopTurnUpdate? Previous, AgentContext Context, SessionProjection Projection)> PrepareAsync()
            {
                var projection = SessionManager.BuildSessionProjection();
                var canonicalContext = new AgentContext
                {
                    Messages = [.. projection.Messages],
                    // 消息声明 provider 可见的装载；context.tools 保留可执行实现。
                    Tools = [.. _agent.Tools],
                };
                // TS 用 `this.agent.state.model/thinkingLevel` 覆盖入参：循环传的是 config 里的值，
                // 而权威状态在 agent 上。
                var previous = previousPrepareRequest is null
                    ? null
                    : await previousPrepareRequest(
                        canonicalContext, _agent.Model, _agent.ThinkingLevel, signal).ConfigureAwait(false);
                return (previous, previous?.Context ?? canonicalContext, projection);
            }

            var (previous, context, projection) = await PrepareAsync().ConfigureAwait(false);
            var effectiveModel = previous?.Model ?? _agent.Model;
            var effectiveThinking = previous?.ThinkingLevel ?? _agent.ThinkingLevel;
            if (!VirtualModels.IsVirtualModel(effectiveModel))
            {
                return WithTurnUpdate(previous, context, effectiveModel, effectiveThinking);
            }

            // 选择留在 agent 状态里，只有这一次请求用路由后的模型。路由失败会抛错，从而以
            // 错误响应结束本轮。只有用户写的消息才开启一个 turn；扩展消息可以跟在它们后面
            // （例如来自 before_agent_start）。
            var userTurn = HasUserTurnAfterLastAssistant(context.Messages);
            var spec = _modelRuntime.GetModel(effectiveModel.Provider, effectiveModel.Id)
                ?? throw new InvalidOperationException(
                    $"Virtual model {effectiveModel.Provider}/{effectiveModel.Id} is not registered.");
            var state = VirtualModels.GetVirtualModelState(SessionManager.GetBranch(), spec.Provider, spec.Id);
            var route = await _modelRuntime.ResolveModelAsync(
                spec,
                AgentMessages.ConvertToLlm(context.Messages),
                new ResolveModelOptions
                {
                    Reason = AgentSession.ResolveRouteReason(failed is not null, userTurn),
                    ThinkingLevel = Core.ThinkingLevels.ToText(effectiveThinking),
                    Signal = signal,
                    Failed = failed,
                    State = state,
                }).ConfigureAwait(false);

            // 差异 C152 的近亲：TS 的 `route.state !== state` 是引用比较，端口用 ReferenceEquals。
            if (route.State is not null && !ReferenceEquals(route.State, state))
            {
                var data = new VirtualModelStateData
                {
                    Provider = spec.Provider,
                    ModelId = spec.Id,
                    State = route.State,
                };
                var entry = SessionManager.GetEntry(
                    SessionManager.AppendCustomEntry(VirtualModels.StateEntry, data));
                if (entry is not null)
                {
                    Emit(new AgentSessionEvent.EntryAppendedEvent(entry));
                }
            }

            // 路由已定：这一次请求由它决定。状态条目不影响投影。
            if (ExceedsCompactionThreshold(route.Model, projection))
            {
                await RunAutoCompactionAsync(CompactionReason.Threshold, false).ConfigureAwait(false);
                (previous, context, _) = await PrepareAsync().ConfigureAwait(false);
            }

            return WithTurnUpdate(
                previous,
                context,
                CompatShims.ToModel(route.Model),
                Core.ThinkingLevels.Parse(route.ThinkingLevel) ?? effectiveThinking);
        };
    }

    /// <summary>TS <c>return { ...previous, context, model, thinkingLevel }</c>。</summary>
    private static AgentLoopTurnUpdate WithTurnUpdate(
        AgentLoopTurnUpdate? previous, AgentContext context, Model model, ThinkingLevel thinkingLevel)
        => previous is null
            ? new AgentLoopTurnUpdate(Context: context, Model: model, ThinkingLevel: thinkingLevel)
            : previous with { Context = context, Model = model, ThinkingLevel = thinkingLevel };

    /// <summary>TS <c>_installAgentBoundaryHooks()</c>（TS 886-896）。</summary>
    private void InstallAgentBoundaryHooks()
    {
        var previousFinishTurn = _agent.FinishTurn;
        _agent.FinishTurn = async (turn, signal) =>
        {
            // TS 的 WeakSet.add；ConditionalWeakTable 的 Add 对重复键抛错，故用幂等写法。
            _ = _boundaryDispatchedMessages.GetValue(turn.Message, _ => DispatchedMarker);
            var extensionContinue = await DispatchTurnEndBoundaryAsync(turn.Message, turn.ToolResults)
                .ConfigureAwait(false);
            var previousDecision = previousFinishTurn is null
                ? null
                : await previousFinishTurn(turn, signal).ConfigureAwait(false);
            return ResolveFinishTurnDecision(
                previousDecision switch
                {
                    AgentTurnDecision.End => FinishTurnAction.End,
                    AgentTurnDecision.Continue => FinishTurnAction.Continue,
                    _ => null,
                },
                extensionContinue) switch
            {
                FinishTurnAction.End => new AgentTurnDecision.End(),
                FinishTurnAction.Continue => new AgentTurnDecision.Continue(),
                _ => null,
            };
        };
    }

    /// <summary>
    /// TS <c>_installHiddenDeclarationsProjection()</c>（TS 1766-1784）。<c>prepareLoadout</c> 钩子
    /// 隐藏的声明从**每一条**请求里剔除；整份 transcript 用当前集合过滤，故投影的声明集在
    /// 装载变化之前保持一致。
    /// </summary>
    private void InstallHiddenDeclarationsProjection()
    {
        var previousTransformContext = _agent.TransformContext;
        _agent.TransformContext = async (messages, signal) =>
        {
            var transformed = previousTransformContext is null
                ? messages
                : await previousTransformContext(messages, signal).ConfigureAwait(false);
            var hidden = _hiddenDeclarations;
            if (hidden.Count == 0)
            {
                return transformed;
            }

            var result = new List<ChatMessage>(transformed.Count);
            foreach (var message in transformed)
            {
                if (message is not SystemMessage system
                    || (system.ToolsAdded is null && system.ToolsRemoved is null))
                {
                    result.Add(message);
                    continue;
                }

                var added = system.ToolsAdded?.Where(tool => !hidden.Contains(tool.Name)).ToList() ?? [];
                var removed = system.ToolsRemoved?.Where(name => !hidden.Contains(name)).ToList() ?? [];
                result.Add(system with
                {
                    ToolsAdded = added.Count > 0 ? added : null,
                    ToolsRemoved = removed.Count > 0 ? removed : null,
                });
            }

            return result;
        };
    }

    /// <summary>
    /// TS <c>_installAgentForcedPromptProjection()</c>（TS 1786-1806）。把 <c>before_agent_start</c>
    /// 返回的强制提示词投影成请求头部的唯一 system 消息（不落 transcript）。在 <c>context</c>
    /// 扩展处理器之后运行。
    /// </summary>
    private void InstallAgentForcedPromptProjection()
    {
        var previousTransformContext = _agent.TransformContext;
        _agent.TransformContext = async (messages, signal) =>
        {
            var transformed = previousTransformContext is null
                ? messages
                : await previousTransformContext(messages, signal).ConfigureAwait(false);
            var forced = _runSystemPromptOptions?.ForceSystemPrompt;
            if (forced is null)
            {
                return transformed;
            }

            var current = Transcript.GetCurrentSystemMessage(transformed);
            var head = new SystemMessage(
                MessageContent.From(forced),
                Sections: null,
                ToolsAdded: current?.ToolsAdded,
                ToolsRemoved: null,
                Timestamp: current?.Timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var result = new List<ChatMessage> { head };
            foreach (var message in transformed)
            {
                if (message is not SystemMessage)
                {
                    result.Add(message);
                }
            }

            return result;
        };
    }

    // =========================================================================
    // 工具钩子（TS 658-716）
    // =========================================================================

    /// <summary>
    /// TS <c>_beforeToolCall()</c>：<c>tool_call</c> 处理器。<paramref name="parentToolCallId"/> 非空
    /// 表示这次调用来自另一个工具。
    /// </summary>
    /// <remarks>
    /// 差异 C154：TS 的 <c>event.input</c> 就是 <c>args</c> 本身，处理器**就地改写 input 即可改参数**。
    /// 端口的 <see cref="ExtensionEvent.CustomToolCall.Input"/> 是 <c>IReadOnlyDictionary</c>（9g 的
    /// 契约决定），既无写入口、也不是同一个对象，故「改写入参」这条路在端口不可用——扩展只能用
    /// <see cref="ToolCallEventResult"/> 的 Block/Reason/Terminate。已登记为缺口。
    /// </remarks>
    private async Task<BeforeToolCallResult?> BeforeToolCallAsync(
        BeforeToolCallContext context, string? parentToolCallId)
    {
        var runner = _extensionRunner;
        if (!runner.HasHandlers(ExtensionEventNames.ToolCall))
        {
            return null;
        }

        // TS 在这里包了 try/catch，把「非 Error 的抛出」包装成
        // `Extension failed, blocking execution: ${String(err)}`。C# 只能抛出异常对象，
        // `err instanceof Error` 恒真，包装分支不可达，故端口直接透传。
        var result = await runner.EmitToolCallAsync(new ExtensionEvent.CustomToolCall(
            context.ToolCall.Id,
            context.ToolCall.Name,
            ToToolInput(context.Args),
            parentToolCallId)).ConfigureAwait(false);
        return result is null
            ? null
            : new BeforeToolCallResult(result.Block, result.Reason, result.Terminate);
    }

    /// <summary>
    /// TS <c>_afterToolCall()</c>：<c>tool_result</c> 处理器 + 图片归一化。
    /// </summary>
    private async Task<AfterToolCallResult?> AfterToolCallAsync(
        AfterToolCallContext context, string? parentToolCallId)
    {
        var runner = _extensionRunner;
        var hookResult = runner.HasHandlers(ExtensionEventNames.ToolResult)
            ? await runner.EmitToolResultAsync(new ExtensionEvent.CustomToolResult(
                context.ToolCall.Id,
                context.ToolCall.Name,
                ToToolInput(context.Args),
                context.Result.Content,
                context.IsError,
                context.Result.Details,
                ToJsonNode(context.Result.StructuredContent),
                context.Result.UsageStats,
                parentToolCallId)).ConfigureAwait(false)
            : null;

        var content = hookResult?.Content ?? context.Result.Content;
        // 在扩展钩子之后运行，故扩展注入或替换的图片也会被归一化。
        var resizeOptions = LimitsModel()?.InputLimits?.Images?.Resize;
        var normalizedContent = await ToolResultImages.NormalizeToolResultImagesAsync(
            content,
            new NormalizeToolResultImagesOptions
            {
                AutoResizeImages = SettingsManager.GetImageAutoResize(),
                ResizeOptions = resizeOptions,
            }).ConfigureAwait(false);

        if (hookResult is null && ReferenceEquals(normalizedContent, content))
        {
            return null;
        }

        // 钩子结果已经丢弃了「内容被替换后不再匹配」的结构化内容（见 ToolExecution.Finalize）。
        return new AfterToolCallResult(
            Content: normalizedContent,
            Details: hookResult?.Details,
            StructuredContent: hookResult is not null ? hookResult.StructuredContent : context.Result.StructuredContent,
            IsError: hookResult?.IsError ?? context.IsError,
            UsageStats: hookResult?.UsageStats);
    }

    /// <summary>TS <c>_limitsModel()</c>：<c>routedModel?.model ?? model</c>。</summary>
    private ModelSpec? LimitsModel() => RoutedModel?.Model ?? Model;

    /// <summary>TS 的 <c>args as Record&lt;string, unknown&gt;</c>（见差异 C154 的说明）。</summary>
    private static IReadOnlyDictionary<string, object?> ToToolInput(object? args) => args switch
    {
        null => EmptyToolInput,
        IReadOnlyDictionary<string, object?> input => input,
        JsonObject json => json.ToDictionary(
            pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal),
        _ => EmptyToolInput,
    };

    /// <summary>把工具结果里的结构化内容（<c>object?</c>）收成事件侧形状（<c>JsonNode?</c>）。</summary>
    private static JsonNode? ToJsonNode(object? structuredContent) => structuredContent switch
    {
        null => null,
        JsonNode node => node,
        _ => JsonSerializer.SerializeToNode(structuredContent),
    };

    // =========================================================================
    // 嵌套工具调用（TS 718-746）
    // =========================================================================

    /// <summary>TS <c>_executeNestedToolCall()</c>：工具经 <c>ctx.executeTool()</c> 发起的调用。</summary>
    private async Task<AgentToolCallOutcome> ExecuteNestedToolCallAsync(
        string callerId, string name, object? args, ExecuteToolOptions options)
    {
        _nestedToolCalls ??= new NestedToolCallRunner(new SessionNestedToolCallHost(this));
        return await _nestedToolCalls.ExecuteAsync(callerId, name, args, new NestedToolCallOptions
        {
            CancellationToken = options.Signal ?? default,
            OnUpdate = options.OnUpdate,
        }).ConfigureAwait(false);
    }

    /// <summary>TS <c>_executeNestedToolCall</c> 里传给 <c>NestedToolCallRunner</c> 的宿主字面量。</summary>
    private sealed class SessionNestedToolCallHost(AgentSession session) : INestedToolCallHost
    {
        public IReadOnlyList<AgentTool> GetTools() => session.GetCallableTools();

        public bool IsSequential() => session._agent.ToolExecution == ToolExecutionMode.Sequential;

        public Task<AgentToolCallOutcome> RunToolCallAsync(
            ToolCallContent toolCall,
            string parentToolCallId,
            CancellationToken cancellationToken,
            Func<AgentToolResult, Task> onUpdate)
        {
            var assistantMessage = FindLastAssistantMessage(session._agent.Messages);
            if (assistantMessage is null)
            {
                return Task.FromResult(new AgentToolCallOutcome(
                    toolCall,
                    new AgentToolResult(
                        [new TextContent("No assistant message issued this call")],
                        Details: new JsonObject()),
                    IsError: true));
            }

            var tools = session.GetCallableTools();
            // 差异 C153：TS 的 `runToolCall` 把「查找用工具集」（options.tools）与「钩子可见上下文」
            // （context.tools = agent.state.tools）分开；端口的 RunToolCallAsync 从 context.Tools 取
            // 查找集，故这里把可调用集同时作为查找集与上下文工具集传入。
            var context = new AgentContext
            {
                Messages = [.. session._agent.Messages],
                Tools = tools,
            };
            var config = new AgentLoopConfig
            {
                Model = session._agent.Model,
                ConvertToLlm = session._agent.ConvertToLlm,
                BeforeToolCall = (hookContext, _) => session.BeforeToolCallAsync(hookContext, parentToolCallId),
                AfterToolCall = (hookContext, _) => session.AfterToolCallAsync(hookContext, parentToolCallId),
            };
            return ToolExecution.RunToolCallAsync(
                context,
                assistantMessage,
                toolCall,
                config,
                cancellationToken,
                partialResult => onUpdate(partialResult));
        }

        public async Task EmitAsync(NestedToolExecutionEvent @event)
        {
            await session._extensionRunner.EmitAsync(ToExtensionEvent(@event)).ConfigureAwait(false);
            session.Emit(ToSessionEvent(@event));
        }

        private static ExtensionEvent ToExtensionEvent(NestedToolExecutionEvent @event) => @event switch
        {
            NestedToolExecutionEvent.Start start => new ExtensionEvent.ToolExecutionStart(
                start.ToolCallId, start.ToolName, start.Args, start.ParentToolCallId),
            NestedToolExecutionEvent.Update update => new ExtensionEvent.ToolExecutionUpdate(
                update.ToolCallId, update.ToolName, update.Args, update.PartialResult, update.ParentToolCallId),
            NestedToolExecutionEvent.End end => new ExtensionEvent.ToolExecutionEnd(
                end.ToolCallId, end.ToolName, end.Result, end.IsError, end.DurationMs, end.ParentToolCallId),
            _ => throw new InvalidOperationException("Unknown nested tool execution event"),
        };

        private static AgentSessionEvent ToSessionEvent(NestedToolExecutionEvent @event) => @event switch
        {
            NestedToolExecutionEvent.Start start => new AgentSessionEvent.ToolExecutionStartEvent(
                start.ToolCallId, start.ToolName, start.Args, start.ParentToolCallId),
            NestedToolExecutionEvent.Update update => new AgentSessionEvent.ToolExecutionUpdateEvent(
                update.ToolCallId, update.ToolName, update.Args, update.PartialResult, update.ParentToolCallId),
            NestedToolExecutionEvent.End end => new AgentSessionEvent.ToolExecutionEndEvent(
                end.ToolCallId, end.ToolName, end.Result, end.IsError, end.ParentToolCallId),
            _ => throw new InvalidOperationException("Unknown nested tool execution event"),
        };
    }

    // =========================================================================
    // 压缩门与提示词装载（TS 762-786 / 1737-1750）
    // =========================================================================

    /// <summary>TS <c>_exceedsCompactionThreshold(model, projection)</c>。</summary>
    private bool ExceedsCompactionThreshold(ModelSpec model, SessionProjection projection)
        => AgentSession.ExceedsCompactionThreshold(
            model.ContextWindow,
            Compaction.EstimateProjectedContextTokens(projection, SessionManager.GetBranch()).Tokens,
            CompactionSettingsForSessionModel());

    /// <summary>
    /// TS <c>this.settingsManager.getCompactionSettings(this.model)</c>——注意取的是**会话当前模型**
    /// （而非被路由到的模型）的按模型覆盖。
    /// </summary>
    private CompactionSettings CompactionSettingsForSessionModel()
    {
        var model = Model;
        var (enabled, reserveTokens, keepRecentTokens) = SettingsManager.GetCompactionSettings(
            model is null ? null : new CompactionModelRef(model.Provider, model.Id));
        return new CompactionSettings(enabled, reserveTokens, keepRecentTokens);
    }

    /// <summary>TS <c>_compactBeforeNextAssistantResponse(context)</c>（TS 771-779）。</summary>
    private async Task<AgentContext> CompactBeforeNextAssistantResponseAsync(AgentContext context)
    {
        var projection = SessionManager.BuildSessionProjection();
        // 虚拟选择在 prepareRequest 里按「请求被路由到的模型」再查一次。
        var model = Model;
        var shouldCompact = model is not null
            && ShouldCompactBeforeNextAssistantResponse(
                hasModel: true,
                isVirtualModel: VirtualModels.IsVirtualModel(model),
                exceedsThreshold: ExceedsCompactionThreshold(model, projection));
        if (!shouldCompact)
        {
            return context with { Messages = [.. projection.Messages] };
        }

        await RunAutoCompactionAsync(CompactionReason.Threshold, false).ConfigureAwait(false);
        return context with { Messages = [.. SessionManager.BuildSessionProjection().Messages] };
    }

    /// <summary>
    /// TS <c>_preparePromptAndToolLoadout(options, messages)</c>（TS 1737-1750）。
    /// </summary>
    /// <remarks>
    /// TS 就地改写 <paramref name="options"/> 的 <c>selectedTools</c> / <c>hiddenTools</c>，调用方随后
    /// 把它存进 <c>_runSystemPromptOptions</c>；端口的这两个成员是 <c>init</c> 的，故用 <c>with</c>
    /// 产出新值并一并返回（差异 C156：只有写法差别，值相同）。
    /// </remarks>
    private (NormalizedBuildSystemPromptOptions Options, SystemMessage? Update) PreparePromptAndToolLoadout(
        NormalizedBuildSystemPromptOptions options, IReadOnlyList<ChatMessage>? messages = null)
    {
        var target = messages ?? _agent.Messages;
        var applied = options with
        {
            SelectedTools = [.. ApplyToolLoadout(options.SelectedTools).Select(tool => tool.Name)],
            // 工具列表与规则必须与请求携带的声明一致。
            HiddenTools = [.. _hiddenDeclarations],
        };

        var sections = SystemPromptApi.DiffSystemPromptSections(
            Transcript.GetCurrentSystemMessage(target)?.Sections
                ?? new Dictionary<string, string?>(StringComparer.Ordinal),
            SystemPromptApi.BuildSystemPromptSections(SystemPromptApi.ToBuildSystemPromptOptions(applied)));
        var update = sections is null
            ? null
            : new SystemMessage(
                MessageContent.From(""),
                sections,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return (applied, update);
    }

    /// <summary>TS 的对象展开 <c>{...base, ...run}</c>（后写的覆盖，保留首次出现的位置）。</summary>
    private static IReadOnlyDictionary<string, string> MergeSnippets(
        IReadOnlyDictionary<string, string> baseSnippets,
        IReadOnlyDictionary<string, string> runSnippets)
    {
        var merged = new System.Collections.Generic.OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, snippet) in baseSnippets)
        {
            merged[name] = snippet;
        }

        foreach (var (name, snippet) in runSnippets)
        {
            merged[name] = snippet;
        }

        return merged;
    }

    /// <summary><see cref="MergeSnippets"/> 的准则版本。</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> MergeGuidelines(
        IReadOnlyDictionary<string, IReadOnlyList<string>> baseGuidelines,
        IReadOnlyDictionary<string, IReadOnlyList<string>> runGuidelines)
    {
        var merged = new System.Collections.Generic.OrderedDictionary<string, IReadOnlyList<string>>(
            StringComparer.Ordinal);
        foreach (var (name, guidelines) in baseGuidelines)
        {
            merged[name] = guidelines;
        }

        foreach (var (name, guidelines) in runGuidelines)
        {
            merged[name] = guidelines;
        }

        return merged;
    }

    /// <summary>TS <c>_restoreToolsFromTranscript()</c>（TS 1808-1818）。</summary>
    private void RestoreToolsFromTranscript()
    {
        _pendingToolNames.Clear();
        var current = Transcript.GetCurrentSystemMessage(SessionManager.BuildSessionContext().Messages);
        if (current is null)
        {
            return;
        }

        var names = (current.ToolsAdded ?? []).Select(tool => tool.Name).ToList();
        foreach (var name in names.Where(IsAllowedTool))
        {
            _pendingToolNames.Add(name);
        }

        SetActiveTools(names);
    }

    // =========================================================================
    // turn_end / agent_before_settle 边界（TS 838-1004）
    // =========================================================================

    /// <summary>TS <c>_dispatchTurnEndBoundary(message, toolResults)</c>（TS 838-874）。</summary>
    private async Task<bool> DispatchTurnEndBoundaryAsync(
        AssistantMessage message, IReadOnlyList<ToolResultMessage> toolResults)
    {
        _lastActivityOutcome = OutcomeFromStopReason(message.StopReason);
        var messageEntryId = FindPersistedMessageEntryIdFor(message);
        if (!_extensionRunner.HasHandlers(ExtensionEventNames.TurnEnd))
        {
            return false;
        }

        if (messageEntryId is null)
        {
            _extensionRunner.EmitError(new ExtensionError(
                "<boundary>",
                ExtensionEventNames.TurnEnd,
                "turn_end could not resolve the persisted assistant entry ID"));
            return false;
        }

        var toolResultEntryIds = new List<string>(toolResults.Count);
        foreach (var result in toolResults)
        {
            if (FindPersistedMessageEntryIdFor(result) is { } entryId)
            {
                toolResultEntryIds.Add(entryId);
            }
        }

        var boundary = await _extensionRunner.EmitBoundaryAsync(
            new ExtensionEvent.TurnEnd
            {
                TurnIndex = _turnIndex,
                Message = message,
                ToolResults = toolResults,
                MessageEntryId = messageEntryId,
                ToolResultEntryIds = toolResultEntryIds,
                Outcome = _lastActivityOutcome,
            },
            drafts => Task.FromResult(BuildBoundaryContext(drafts, SessionBoundaryKind.TurnEnd)))
            .ConfigureAwait(false);

        CommitBoundaryDrafts(boundary.Entries);
        var boundaryResult = ResolveTurnEndBoundaryResult(
            boundary.Continue,
            BuildBoundaryContext([], SessionBoundaryKind.TurnEnd).CanContinue);
        if (boundaryResult.ReportInvalid)
        {
            ReportInvalidBoundaryContinuation(SessionBoundaryKind.TurnEnd);
            return false;
        }

        return boundaryResult.Continue;
    }

    /// <summary>TS <c>_applyBoundaryDrafts(manager, drafts)</c>（TS 944-995）。</summary>
    private List<SessionEntry> ApplyBoundaryDrafts(
        SessionManager manager, IReadOnlyList<SessionBoundaryDraft> drafts)
    {
        var appended = new List<SessionEntry>(drafts.Count);
        foreach (var draft in drafts)
        {
            var entryId = draft switch
            {
                SessionBoundaryDraft.Custom custom => manager.AppendCustomEntry(
                    custom.Draft.CustomType, custom.Draft.Data),
                SessionBoundaryDraft.CustomMessage customMessage => manager.AppendCustomMessageEntry(
                    customMessage.Draft.CustomType,
                    customMessage.Draft.Content,
                    customMessage.Draft.Display,
                    customMessage.Draft.Details),
                SessionBoundaryDraft.ContextEdit edit => manager.AppendContextEdit(
                    edit.Draft.TargetId, ToContextEditReplacement(edit.Draft.Replacement)),
                SessionBoundaryDraft.Compaction compaction => manager.AppendCompaction(
                    compaction.Draft.Summary,
                    compaction.Draft.FirstKeptEntryId,
                    Compaction.EstimateProjectedContextTokens(
                        manager.BuildSessionProjection(), manager.GetBranch()).Tokens,
                    compaction.Draft.Details,
                    true,
                    compaction.Draft.UsageStats),
                _ => throw new InvalidOperationException("Unknown session boundary draft"),
            };
            var entry = manager.GetEntry(entryId);
            if (entry is not null)
            {
                appended.Add(entry);
            }
        }

        return appended;
    }

    /// <summary>
    /// 扩展给出的 <c>context_edit</c> 载荷（TS 是 <c>unknown</c>，端口以 <c>object?</c> 承载）
    /// → <see cref="ContextEditReplacement"/>。TS 的 <c>appendContextEdit</c> 在运行时校验形状；
    /// 端口在这里收口，形状不对时抛出同一句话。
    /// </summary>
    private static ContextEditReplacement? ToContextEditReplacement(object? replacement) => replacement switch
    {
        null => null,
        ContextEditReplacement typed => typed,
        JsonObject json => new ContextEditReplacement { Content = json["content"] },
        _ => throw new InvalidOperationException(
            "Context edit replacement must be null or contain string/array content"),
    };

    /// <summary>TS <c>_createBoundaryPreviewManager(drafts)</c>（TS 997-1004）。</summary>
    private SessionManager CreateBoundaryPreviewManager(IReadOnlyList<SessionBoundaryDraft> drafts)
    {
        var header = SessionManager.GetHeader()
            ?? throw new InvalidOperationException("Session header is missing");
        var manager = SessionManager.InMemory(_cwd, null, [header, .. SessionManager.GetBranch()]);
        ApplyBoundaryDrafts(manager, drafts);
        return manager;
    }

    /// <summary>TS <c>_getPendingBoundaryMessages()</c>（TS 1006-1008）。</summary>
    private IReadOnlyList<ChatMessage> GetPendingBoundaryMessages() =>
        GetPendingBoundaryMessages(_agent.PeekQueuedMessages(), _pendingCustomMessages);

    /// <summary>TS <c>_buildBoundaryContext(drafts, boundary)</c>（TS 1010-1031）。</summary>
    private BoundaryContextPreview BuildBoundaryContext(
        IReadOnlyList<SessionBoundaryDraft> drafts, string boundary)
    {
        var projection = CreateBoundaryPreviewManager(drafts).BuildSessionProjection();
        var llmMessages = AgentMessages.ConvertToLlm(projection.Messages);
        return new BoundaryContextPreview
        {
            ContextEntries = projection.Entries,
            ContextMessages = projection.Messages,
            LlmMessages = llmMessages,
            PendingMessages = GetPendingBoundaryMessages(),
            CanContinue = ComputeBoundaryCanContinue(
                llmMessages,
                hasPendingCustomMessages: _pendingCustomMessages.Count > 0,
                hasQueuedMessages: _agent.HasQueuedMessages(),
                boundary),
        };
    }

    /// <summary>TS <c>_commitBoundaryDrafts(drafts)</c>（TS 1033-1038）。</summary>
    private void CommitBoundaryDrafts(IReadOnlyList<SessionBoundaryDraft> drafts)
    {
        var appended = ApplyBoundaryDrafts(SessionManager, drafts);
        RefreshFinalizedContext();
        foreach (var entry in appended)
        {
            Emit(new AgentSessionEvent.EntryAppendedEvent(entry));
        }
    }

    /// <summary>TS <c>_reportInvalidBoundaryContinuation(event)</c>（TS 1040-1046）。</summary>
    private void ReportInvalidBoundaryContinuation(string boundary)
        => _extensionRunner.EmitError(new ExtensionError(
            "<boundary>",
            boundary,
            $"{boundary} requested continuation without runnable model context"));

    /// <summary>TS <c>_emitAgentSettled()</c>（TS 1085-1100）。</summary>
    private async Task EmitAgentSettledAsync()
    {
        _cacheWarmer?.OnAgentSettled();
        _isAgentRunActive = false;
        _isEmittingAgentSettled = true;
        try
        {
            await _extensionRunner.EmitAsync(new ExtensionEvent.AgentSettled()).ConfigureAwait(false);
            Emit(new AgentSessionEvent.AgentSettledEvent());
        }
        finally
        {
            _isEmittingAgentSettled = false;
        }

        var deferred = _deferredSettledActions.ToList();
        _deferredSettledActions.Clear();
        if (deferred.Count > 0)
        {
            try
            {
                foreach (var action in deferred)
                {
                    await action().ConfigureAwait(false);
                }
            }
            finally
            {
                ResolveIdleWaitIfIdle();
            }

            return;
        }

        ResolveIdleWaitIfIdle();
    }

    // =========================================================================
    // Agent 事件 → 会话事件 / 持久化（TS 1102-1187）
    // =========================================================================

    /// <summary>
    /// TS <c>_handleAgentEvent</c>（TS 1102-1187）。由构造器的 <c>agent.subscribe</c> 与
    /// <c>_reconnectToAgent</c> 共用；端口的 <see cref="Pi.Agent.Agent.Subscribe"/> 额外给出本次运行的
    /// 取消令牌，本处理器不需要它（TS 也不读）。
    /// </summary>
    private async Task HandleAgentEventAsync(AgentEvent @event, CancellationToken signal)
    {
        _ = signal;

        // 记录工具经 ctx.executeTool() 发起的调用及其用量，挂到它的结果消息上。
        if (_nestedToolCalls is not null)
        {
            if (@event is AgentEvent.MessageStart { Message: ToolResultMessage toolResult })
            {
                var summary = _nestedToolCalls.TakeRecord(toolResult.ToolCallId);
                if (summary?.Calls is not null)
                {
                    toolResult.NestedCalls = summary.Calls;
                }

                if (summary?.Usage is not null)
                {
                    toolResult.UsageStats = toolResult.UsageStats is null
                        ? summary.Usage
                        : UsageTotalsApi.CombineUsage(toolResult.UsageStats, summary.Usage);
                }
            }
            else if (@event is AgentEvent.AgentEnd)
            {
                _nestedToolCalls.Clear();
            }
        }

        // 用户消息开始时，先看它是否来自某个队列并在**发事件之前**出队，好让 UI 看到更新后的队列。
        if (@event is AgentEvent.MessageStart { Message: UserMessage userMessage })
        {
            _overflowRecoveryAttempted = false;
            var messageText = Pi.Ai.Utils.Text.ContentText(userMessage.Content, "");
            if (messageText.Length > 0)
            {
                var steeringBefore = _steeringMessages.Count;
                var followUpBefore = _followUpMessages.Count;
                var (steering, followUp) = AgentSession.DequeueMatchingUserText(
                    _steeringMessages, _followUpMessages, messageText);
                if (steering.Count != steeringBefore || followUp.Count != followUpBefore)
                {
                    _steeringMessages.Clear();
                    _steeringMessages.AddRange(steering);
                    _followUpMessages.Clear();
                    _followUpMessages.AddRange(followUp);
                    EmitQueueUpdate();
                }
            }
        }

        // 先发给扩展，再通知公开监听器。
        var replacement = await EmitExtensionEventAsync(@event).ConfigureAwait(false);
        if (@event is AgentEvent.AgentEnd agentEnd)
        {
            Emit(new AgentSessionEvent.AgentEndEvent(agentEnd.Messages, WillRetryAfterAgentEnd(agentEnd)));
        }
        else
        {
            Emit(MapToSessionEvent(replacement is null ? @event : WithMessage(@event, replacement)));
        }

        // 会话持久化。
        if (@event is AgentEvent.MessageEnd messageEnd)
        {
            var message = replacement ?? messageEnd.Message;
            if (replacement is not null)
            {
                ReplaceMessageInPlace(messageEnd.Message, replacement);
            }

            string? entryId = null;
            // 来自扩展的自定义消息。
            if (message is CustomMessage custom)
            {
                entryId = SessionManager.AppendCustomMessageEntry(
                    custom.CustomType, custom.Content, custom.Display, custom.Details);
            }
            else if (message is SystemMessage or UserMessage or AssistantMessage or ToolResultMessage)
            {
                // 常规 LLM 消息 —— 落为 SessionMessageEntry。
                entryId = SessionManager.AppendMessage(message);
            }

            if (entryId is not null)
            {
                _entryIdsByMessage.GetOrCreateValue(message).Id = entryId;
                // 差异 C152：TS 里 target 与 replacement 是同一个对象，端口的后续事件仍持有 target，
                // 故两个键都记，`turn_end` 的溯源才不会落空。
                _entryIdsByMessage.GetOrCreateValue(messageEnd.Message).Id = entryId;
            }

            // 其它 role（bashExecution / compactionSummary / branchSummary）在别处落库。
            if (message is AssistantMessage assistant)
            {
                _lastAssistantMessage = assistant;
                if (assistant.StopReason is not (StopReason.Error or StopReason.Length))
                {
                    _overflowRecoveryAttempted = false;
                }

                // 助手响应成功时立刻复位重试计数，避免一个 turn 内多次 LLM 调用累加。
                if (assistant.StopReason is not (StopReason.Error or StopReason.Length) && _retryAttempt > 0)
                {
                    Emit(new AgentSessionEvent.AutoRetryEndEvent(true, _retryAttempt));
                    _retryAttempt = 0;
                }
            }
        }

        // turn 在助手消息与它的全部工具结果都落定后才结束，因此这是「只作上下文的」自定义消息
        // 唯一能插入而不落在 tool_call 与 tool_result 之间的时刻。在扩展与监听器分发之后再冲刷，
        // 也就能捡到 turn_end 处理器排进来的消息。
        if (@event is AgentEvent.TurnEnd turnEnd)
        {
            _lastAssistantToolResults.Clear();
            _lastAssistantToolResults.AddRange(turnEnd.ToolResults);
            FlushPendingCustomMessages();
        }
    }

    /// <summary>TS <c>_willRetryAfterAgentEnd(event)</c>（TS 1197-1213）。</summary>
    private bool WillRetryAfterAgentEnd(AgentEvent.AgentEnd @event)
    {
        if (_agentRunAbortRequested)
        {
            return false;
        }

        var settings = SettingsManager.GetRetrySettings();
        if (!settings.Enabled || _retryAttempt >= settings.MaxRetries)
        {
            return false;
        }

        for (var i = @event.Messages.Count - 1; i >= 0; i--)
        {
            if (@event.Messages[i] is AssistantMessage assistant)
            {
                return AgentSession.IsRetryableError(assistant, ContextWindowForMessage(assistant));
            }
        }

        return false;
    }

    /// <summary>
    /// TS <c>_modelForMessage(message)</c>（TS 615-619）：<c>(_modelForMessage(message) ?? this.model)?.contextWindow ?? 0</c>
    /// 里的前半段。虚拟选择下响应记的是物理模型，故按 provider/id 回查目录。
    /// </summary>
    private ModelSpec? ModelForMessage(AssistantMessage message)
    {
        var model = Model;
        if (model is not null && VirtualModels.IsVirtualModel(model))
        {
            return _modelRuntime.GetPhysicalModel(message.Provider ?? string.Empty, message.Model ?? string.Empty);
        }

        return IsModelForMessage(
            model?.Provider,
            model?.Id,
            message.Provider ?? string.Empty,
            message.Model)
            ? model
            : null;
    }

    private long ContextWindowForMessage(AssistantMessage message)
        => (ModelForMessage(message) ?? Model)?.ContextWindow ?? 0;

    /// <summary>TS <c>_findPersistedMessageEntryId(message)</c> 的实例面。</summary>
    private string? FindPersistedMessageEntryIdFor(ChatMessage message)
        => AgentSession.FindPersistedMessageEntryId(
            message,
            _entryIdsByMessage.TryGetValue(message, out var box) ? box.Id : null,
            SessionManager.GetBranch(),
            _agent.Messages,
            SessionManager.BuildSessionProjection());

    /// <summary>
    /// TS <c>_replaceMessageInPlace(target, replacement)</c>（TS 1266-1281）。
    /// </summary>
    /// <remarks>
    /// 差异 C152：TS 删除 target 的全部自有属性再 <c>Object.assign</c> 覆盖，于是 agent 状态、后续事件、
    /// 监听器与落库共用同一对象、一起看到新内容。端口的消息记录不可变，无法改写 Content 等位置属性，
    /// 故改为「把 agent 状态里指向 target 的槽位换成 replacement」，并由
    /// <see cref="HandleAgentEventAsync"/> 用 replacement 落库、由 <see cref="WithMessage"/> 投影后续事件。
    /// 唯一无法覆盖的观察者是 <c>AgentLoopRunner</c> 自己的局部变量（TS 靠同一性看到新内容，端口看不到），
    /// 已登记为缺口。
    /// </remarks>
    private void ReplaceMessageInPlace(ChatMessage target, ChatMessage replacement)
    {
        if (ReferenceEquals(target, replacement))
        {
            return;
        }

        _replacedMessages.Remove(target);
        _replacedMessages.Add(target, replacement);

        var messages = _agent.Messages;
        for (var i = 0; i < messages.Count; i++)
        {
            if (!ReferenceEquals(messages[i], target))
            {
                continue;
            }

            var updated = messages.ToList();
            updated[i] = replacement;
            _agent.Messages = updated;
            return;
        }
    }

    /// <summary>差异 C152 的投影面：事件里指向被替换对象的槽位换成 replacement。</summary>
    private AgentEvent WithMessage(AgentEvent @event, ChatMessage replacement) => @event switch
    {
        AgentEvent.MessageStart => new AgentEvent.MessageStart(replacement),
        AgentEvent.MessageEnd => new AgentEvent.MessageEnd(replacement),
        AgentEvent.TurnEnd turnEnd when replacement is AssistantMessage assistant =>
            new AgentEvent.TurnEnd(assistant, turnEnd.ToolResults),
        _ => @event,
    };

    /// <summary>TS <c>_emitExtensionEvent(event)</c>（TS 1283-1345）。</summary>
    /// <returns>扩展在 <c>message_end</c> 上替换出的消息（未替换时为 <c>null</c>，见差异 C152）。</returns>
    private async Task<ChatMessage?> EmitExtensionEventAsync(AgentEvent @event)
    {
        switch (@event)
        {
            case AgentEvent.AgentStart:
                _turnIndex = 0;
                await _extensionRunner.EmitAsync(new ExtensionEvent.AgentStart()).ConfigureAwait(false);
                break;

            case AgentEvent.AgentEnd agentEnd:
                await _extensionRunner.EmitAsync(new ExtensionEvent.AgentEnd(agentEnd.Messages))
                    .ConfigureAwait(false);
                break;

            case AgentEvent.TurnStart:
                await _extensionRunner.EmitAsync(new ExtensionEvent.TurnStart(
                    _turnIndex, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ConfigureAwait(false);
                break;

            case AgentEvent.TurnEnd turnEnd:
                if (turnEnd.Message is AssistantMessage assistant
                    && !_boundaryDispatchedMessages.Remove(assistant))
                {
                    await DispatchTurnEndBoundaryAsync(assistant, turnEnd.ToolResults).ConfigureAwait(false);
                }

                _turnIndex++;
                break;

            case AgentEvent.MessageStart messageStart:
                await _extensionRunner.EmitAsync(new ExtensionEvent.MessageStart(messageStart.Message))
                    .ConfigureAwait(false);
                break;

            case AgentEvent.MessageUpdate messageUpdate:
                await _extensionRunner.EmitAsync(new ExtensionEvent.MessageUpdate(
                    messageUpdate.Message, messageUpdate.AssistantMessageEvent)).ConfigureAwait(false);
                break;

            case AgentEvent.MessageEnd messageEnd:
            {
                var replacement = await _extensionRunner.EmitMessageEndAsync(
                    new ExtensionEvent.MessageEnd(messageEnd.Message)).ConfigureAwait(false);
                if (replacement is null)
                {
                    break;
                }

                // TS 在这里把「role ∈ {user, assistant, toolResult, custom} 且 content == null」的
                // 返回值归一化成 `content: []`。端口这四个 role 的 Content 都是非空类型，
                // 构造不出 content 缺失的消息，故该归一化分支不可达（差异 C157）。
                ReplaceMessageInPlace(messageEnd.Message, replacement);
                return replacement;
            }

            case AgentEvent.ToolExecutionStart toolStart:
                await _extensionRunner.EmitAsync(new ExtensionEvent.ToolExecutionStart(
                    toolStart.ToolCallId, toolStart.ToolName, toolStart.Args)).ConfigureAwait(false);
                break;

            case AgentEvent.ToolExecutionUpdate toolUpdate:
                await _extensionRunner.EmitAsync(new ExtensionEvent.ToolExecutionUpdate(
                    toolUpdate.ToolCallId,
                    toolUpdate.ToolName,
                    toolUpdate.Args,
                    toolUpdate.PartialResult)).ConfigureAwait(false);
                break;

            case AgentEvent.ToolExecutionEnd toolEnd:
                await _extensionRunner.EmitAsync(new ExtensionEvent.ToolExecutionEnd(
                    toolEnd.ToolCallId, toolEnd.ToolName, toolEnd.Result, toolEnd.IsError))
                    .ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Unknown agent event {@event.GetType().Name}");
        }

        return null;
    }

    /// <summary>
    /// TS 的 <c>_emit(event)</c> 在会话侧是**同一个** <c>AgentEvent</c> 对象（核心形状 + 可选
    /// <c>parentToolCallId</c>）。端口重新声明了整个联合（差异 C147），故这里做一次显式映射。
    /// </summary>
    private static AgentSessionEvent MapToSessionEvent(AgentEvent @event) => @event switch
    {
        AgentEvent.AgentStart => new AgentSessionEvent.AgentStartEvent(),
        AgentEvent.TurnStart => new AgentSessionEvent.TurnStartEvent(),
        AgentEvent.TurnEnd turnEnd => new AgentSessionEvent.TurnEndEvent(turnEnd.Message, turnEnd.ToolResults),
        AgentEvent.MessageStart start => new AgentSessionEvent.MessageStartEvent(start.Message),
        AgentEvent.MessageUpdate update => new AgentSessionEvent.MessageUpdateEvent(
            update.AssistantMessageEvent, update.Message),
        AgentEvent.MessageEnd end => new AgentSessionEvent.MessageEndEvent(end.Message),
        AgentEvent.ToolExecutionStart start => new AgentSessionEvent.ToolExecutionStartEvent(
            start.ToolCallId, start.ToolName, start.Args),
        AgentEvent.ToolExecutionUpdate update => new AgentSessionEvent.ToolExecutionUpdateEvent(
            update.ToolCallId, update.ToolName, update.Args, update.PartialResult),
        AgentEvent.ToolExecutionEnd end => new AgentSessionEvent.ToolExecutionEndEvent(
            end.ToolCallId, end.ToolName, end.Result, end.IsError),
        _ => throw new InvalidOperationException($"Unknown agent event {@event.GetType().Name}"),
    };

    // =========================================================================
    // 生命周期（TS 1381-1416）
    // =========================================================================

    /// <summary>TS <c>_disconnectFromAgent()</c>（TS 1381-1386）。</summary>
    private void DisconnectFromAgent()
    {
        _unsubscribeAgent?.Dispose();
        _unsubscribeAgent = null;
    }

    /// <summary>TS <c>refreshContext()</c>（TS 1420-1422）。</summary>
    public void RefreshContext() => RefreshFinalizedContext();

    /// <summary>TS <c>abortBash()</c>：中止全部进行中的 <c>!</c> 命令。</summary>
    public void AbortBash()
    {
        foreach (var controller in _bashAbortControllers)
        {
            controller.Cancel();
        }

        _bashAbortControllers.Clear();
    }

    /// <summary>TS <c>_flushPendingBashMessages()</c>（TS 3924-3932）。</summary>
    private void FlushPendingBashMessages()
    {
        var pending = AgentSession.TakePendingBashMessages(_pendingBashMessages);
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var bashMessage in pending)
        {
            SessionManager.AppendMessage(bashMessage);
        }

        RefreshFinalizedContext();
    }

    /// <summary>TS <c>dispose()</c>（TS 1393-1416）。</summary>
    public void Dispose()
    {
        try
        {
            AbortRetry();
            AbortCompaction();
            AbortBranchSummary();
            AbortBash();
            _agent.Abort();
        }
        catch
        {
            // 即使某个中止钩子抛错，dispose 也必须成功。
        }

        _extensionRunner.Invalidate(StaleExtensionContextMessage);
        DisconnectFromAgent();
        _eventListeners.Clear();
        if (_cacheWarmer is not null)
        {
            // TS: this._cacheWarmer.onWarmed = undefined; —— 端口 CacheWarmer 尚无 onWarmed
            // 字段（随刷新流批次落地，见 9h-1 勘察记录），故只做 cancel。
            _cacheWarmer.Cancel();
        }

        Pi.Ai.SessionResources.CleanupSessionResources(SessionId);
    }

    /// <summary>TS <c>dispose()</c> 里传给 <c>invalidate</c> 的文案，逐字照抄。</summary>
    private const string StaleExtensionContextMessage =
        "This extension ctx is stale after session replacement or reload. Do not use a captured pi or "
        + "command ctx after ctx.newSession(), ctx.fork(), ctx.switchSession(), or ctx.reload(). For "
        + "newSession, fork, and switchSession, move post-replacement work into withSession and use the ctx "
        + "passed to withSession. For reload, do not use the old ctx after await ctx.reload().";

    // 9h-4 落地后本文件不再有缝：`_runAutoCompaction` 的实例本体在
    // `AgentSessionCompaction.cs`（与 `compact` / `_checkCompaction` 同处）。
}
