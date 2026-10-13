using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using Pi.CodingAgent.Utils.Image;
using PromptTemplatesApi = Pi.CodingAgent.Core.PromptTemplates;
using SystemPromptApi = Pi.CodingAgent.Core.SystemPrompt;

namespace Pi.CodingAgent.Core;

// ============================================================================
// AgentSession 的提示词编排 — TS `agent-session.ts` 的 `prompt`（1954-2097）、
// `_runAgentPrompt`（1808-1837）、`_handlePostAgentRun`（1839-1877）、
// `_runBeforeSettleBoundary`（1879-1910）、`_tryExecuteExtensionCommand`（2103-2130）、
// `_emitSessionCompactFailed`（1041-1045）、`_omitRecoveryAttempt`（1224-1241）、
// `_prepareRetry`（3697-3740）、`_finishCancelledRetry`（3743-3753）、`_recordSelection`（612-626）。
// ============================================================================
//
// 9a～9e 已把循环控制流、输入归一化、边界判定与重试/压缩门抽成静态叶子
// （AgentSessionLoop / AgentSessionCompaction）；本文件是**实例接线层**：
// 把叶子按 TS 的分支顺序串起来，读写实例状态（`_agentRunAbortRequested` /
// `_retryAttempt` / `_failedResponse` / `_runSystemPromptOptions` / 四个 abort controller）。

public partial class AgentSession
{
    // =========================================================================
    // prompt（TS 1954-2097）
    // =========================================================================

    /// <summary>
    /// 提交一条提示词。TS <c>prompt(text, options?)</c>。
    /// </summary>
    /// <remarks>
    /// 分支顺序即 TS 的求值顺序：settled 延迟 → 扩展命令 → 压缩中拒绝 → input 处理器 →
    /// 模板展开 → 流式排队 → 冲刷待发消息 → 模型/凭据校验 → 提交前压缩检查 →
    /// before_agent_start → 图片归一 → 消息装配 → 运行。
    /// </remarks>
    public async Task PromptAsync(string text, PromptOptions? options = null)
    {
        // agent_settled 事件派发中提交的提示词延后到派发结束，避免重入 run 生命周期。
        if (_isEmittingAgentSettled)
        {
            _deferredSettledActions.Add(() => PromptAsync(text, options));
            return;
        }

        var expandPromptTemplates = options?.ExpandPromptTemplates ?? true;
        var preflightResult = options?.PreflightResult;

        // 扩展命令优先（流式中也可执行）：命令自己经 pi.sendMessage() 管 LLM 交互。
        if (ShouldTryExecuteExtensionCommand(expandPromptTemplates, text))
        {
            if (await TryExecuteExtensionCommandAsync(text).ConfigureAwait(false))
            {
                preflightResult?.Invoke(PromptDisposition.Handled);
                return;
            }
        }

        if (_compactionAbortController is not null)
        {
            throw new InvalidOperationException(
                "Cannot submit a prompt while compaction is in progress. Wait for compaction to finish and retry.");
        }

        // input 事件在 skill/模板展开**之前**派发，供扩展拦截。
        var processedInput = await RunInputHandlersAsync(
            text,
            options?.Images,
            options?.Source ?? InputSource.Interactive,
            IsStreaming ? options?.StreamingBehavior : null).ConfigureAwait(false);
        if (processedInput is null)
        {
            preflightResult?.Invoke(PromptDisposition.Handled);
            return;
        }

        var currentText = processedInput.Text;
        var currentImages = processedInput.Images;

        var expandedText = currentText;
        if (expandPromptTemplates)
        {
            expandedText = ExpandSkillCommandForSession(expandedText);
            expandedText = PromptTemplatesApi.ExpandPromptTemplate(expandedText, [.. PromptTemplates]);
        }

        if (IsStreaming)
        {
            if (options?.StreamingBehavior is null)
            {
                throw new InvalidOperationException(
                    "Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.");
            }

            if (options.StreamingBehavior == "followUp")
            {
                await QueueFollowUpAsync(expandedText, currentImages).ConfigureAwait(false);
            }
            else
            {
                await QueueSteerAsync(expandedText, currentImages).ConfigureAwait(false);
            }

            preflightResult?.Invoke(PromptDisposition.Queued);
            return;
        }

        // 新提示词之前冲刷待发的 bash / 自定义消息。
        FlushPendingBashMessages();
        FlushPendingCustomMessages();

        var model = Model;
        if (model is null)
        {
            throw new InvalidOperationException(AuthGuidance.FormatNoModelSelectedMessage());
        }

        var hasConfiguredAuth = _modelRuntime.HasConfiguredAuth(model.Provider)
            || await _modelRuntime.CheckAuthAsync(model.Provider).ConfigureAwait(false) is not null;
        if (!hasConfiguredAuth)
        {
            if (_modelRuntime.IsUsingOAuth(model.Provider))
            {
                throw new InvalidOperationException(
                    $"Authentication failed for \"{model.Provider}\". "
                    + "Credentials may have expired or network is unavailable. "
                    + $"Run '/login {model.Provider}' to re-authenticate.");
            }

            throw new InvalidOperationException(AuthGuidance.FormatNoApiKeyFoundMessage(model.Provider));
        }

        // 提交前的压缩检查（接住被中止的响应）。用户的新提示词在下面发送，故此处**不**调 agent.continue()。
        var lastAssistant = FindLastAssistantMessage(_agent.Messages);
        if (lastAssistant is not null)
        {
            await CheckCompactionAsync(lastAssistant, skipAbortedCheck: false).ConfigureAwait(false);
        }

        // before_agent_start 在图片归一**之前**派发：扩展驱动的模型选择决定缩放档位。
        var selectedToolsBefore = _baseSystemPromptOptions.SelectedTools;
        var result = await _extensionRunner.EmitBeforeAgentStartAsync(
            expandedText,
            currentImages,
            SystemPromptApi.ToBuildSystemPromptOptions(_baseSystemPromptOptions)).ConfigureAwait(false);

        // 处理器可能改 event.systemPromptOptions.selectedTools，也可能调 setActiveTools()（改的是活的装载）。
        // 显式编辑优先；否则以活的装载为准，故 setActiveTools() 的结果不会被这里覆盖回去。
        var resultOptions = result.SystemPromptOptions;
        var handlerEditedTools = resultOptions.SelectedTools.Count != selectedToolsBefore.Count
            || resultOptions.SelectedTools.Where((name, index) => name != selectedToolsBefore[index]).Any();
        if (!handlerEditedTools)
        {
            resultOptions = resultOptions with { SelectedTools = GetActiveToolNames() };
        }

        var normalized = await NormalizePromptImagesAsync(currentImages, ProcessPromptImageAsync).ConfigureAwait(false);
        var userText = normalized.Hints.Count > 0
            ? $"{expandedText}\n\n{string.Join("\n", normalized.Hints)}"
            : expandedText;

        // 消息在钩子与图片归一**之后**才装配。
        var messages = new List<ChatMessage>();
        var userContent = new List<ContentBlock> { new TextContent(userText) };
        userContent.AddRange(normalized.Images);
        messages.Add(new UserMessage(userContent, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        // 待发的 "nextTurn" 消息随用户消息一起注入为上下文。
        foreach (var nextTurnMessage in _pendingNextTurnMessages)
        {
            messages.Add(nextTurnMessage);
        }

        _pendingNextTurnMessages.Clear();

        foreach (var customMessage in result.Messages)
        {
            messages.Add(new CustomMessage(
                customMessage.CustomType,
                customMessage.Content,
                customMessage.Display,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                Details = customMessage.Details,
            });
        }

        var (appliedOptions, updateMessage) = PreparePromptAndToolLoadout(resultOptions, messages);
        _runSystemPromptOptions = appliedOptions;
        if (updateMessage is not null)
        {
            messages.Insert(0, updateMessage);
        }

        preflightResult?.Invoke(PromptDisposition.Started);
        await RunAgentPromptAsync([.. messages]).ConfigureAwait(false);
    }

    /// <summary>TS <c>_normalizePromptImages</c> 里 <c>processImage(...)</c> 的入参准备（差异 C94）。</summary>
    private Task<ProcessImageResult> ProcessPromptImageAsync(ImageContent image)
    {
        var resizeProfile = LimitsModel()?.InputLimits?.Images?.Resize;
        return ImageProcess.ProcessImageAsync(
            LenientBase64.Decode(image.Data),
            image.MimeType ?? string.Empty,
            new ProcessImageOptions
            {
                AutoResizeImages = SettingsManager.GetImageAutoResize(),
                ResizeOptions = resizeProfile is null ? null : ImageResizeOptions.FromModelProfile(resizeProfile),
            });
    }

    // =========================================================================
    // 运行循环（TS 1808-1910）
    // =========================================================================

    /// <summary>TS <c>_runAgentPrompt(messages)</c>。</summary>
    public async Task RunAgentPromptAsync(params ChatMessage[] messages)
    {
        _agentRunAbortRequested = false;
        // 提交前的压缩可能排定了重试；新提示词取代它。
        _failedResponse = null;
        RecordSelection();
        // 本轮运行会把装载写进 transcript；到此刻仍未注册的恢复工具被丢弃。
        _pendingToolNames.Clear();
        _isAgentRunActive = true;
        try
        {
            await _agent.Prompt(messages).ConfigureAwait(false);
            while (!_agentRunAbortRequested)
            {
                var postRun = await HandlePostAgentRunAsync().ConfigureAwait(false);
                var abortAfterPostRun = _agentRunAbortRequested;

                var beforeSettle = false;
                var abortAfterBeforeSettle = abortAfterPostRun;
                if (!postRun && !abortAfterPostRun)
                {
                    // TS 的 `||` 短路：已请求中止时不调用 _runBeforeSettleBoundary()。
                    beforeSettle = await RunBeforeSettleBoundaryAsync().ConfigureAwait(false);
                    abortAfterBeforeSettle = _agentRunAbortRequested;
                }

                var action = ResolvePromptLoopStep(new PromptLoopIteration
                {
                    AbortRequestedAtStart = false,
                    PostAgentRunContinued = postRun,
                    AbortRequestedAfterPostRun = abortAfterPostRun,
                    BeforeSettleContinued = beforeSettle,
                    AbortRequestedAfterBeforeSettle = abortAfterBeforeSettle,
                });

                if (action == PromptLoopAction.Stop)
                {
                    break;
                }

                await _agent.Continue().ConfigureAwait(false);
            }
        }
        finally
        {
            if (_agentRunAbortRequested)
            {
                FinishCancelledRetry();
            }

            _failedResponse = null;
            _runSystemPromptOptions = null;
            FlushPendingBashMessages();
            FlushPendingCustomMessages();
            await EmitAgentSettledAsync().ConfigureAwait(false);
        }
    }

    /// <summary>TS <c>_handlePostAgentRun()</c>（TS 1839-1877）。</summary>
    private async Task<bool> HandlePostAgentRunAsync()
    {
        var message = _lastAssistantMessage;
        var toolResults = new List<ChatMessage>(_lastAssistantToolResults);
        _lastAssistantMessage = null;
        _lastAssistantToolResults.Clear();

        if (_agentRunAbortRequested)
        {
            FinishCancelledRetry();
            return false;
        }

        if (message is null)
        {
            return _agent.HasQueuedMessages();
        }

        if (IsRetryableError(message, ContextWindowForMessage(message))
            && await PrepareRetryAsync(message).ConfigureAwait(false))
        {
            if (_agentRunAbortRequested)
            {
                FinishCancelledRetry();
            }

            _failedResponse = message;
            return !_agentRunAbortRequested;
        }

        if (_agentRunAbortRequested)
        {
            FinishCancelledRetry();
            return false;
        }

        if (message.StopReason == StopReason.Error && _retryAttempt > 0)
        {
            Emit(new AgentSessionEvent.AutoRetryEndEvent(
                Success: false,
                Attempt: _retryAttempt,
                FinalError: message.ErrorMessage));
            _retryAttempt = 0;
        }

        if (await CheckCompactionAsync(message, skipAbortedCheck: true, toolResults).ConfigureAwait(false))
        {
            return !_agentRunAbortRequested;
        }

        // 低层循环在 agent_end 之前排空两个队列。agent_end 处理器排队的消息需要一次新的 run，
        // 才会让 pre-settlement 处理器触发。
        return !_agentRunAbortRequested && _agent.HasQueuedMessages();
    }

    /// <summary>TS <c>_runBeforeSettleBoundary()</c>（TS 1879-1910）。</summary>
    private async Task<bool> RunBeforeSettleBoundaryAsync()
    {
        if (!_extensionRunner.HasHandlers(ExtensionEventNames.AgentBeforeSettle))
        {
            return _agent.HasQueuedMessages();
        }

        _isBeforeSettle = true;
        _abortDuringBeforeSettle = false;
        try
        {
            var result = await _extensionRunner.EmitBoundaryAsync(
                new ExtensionEvent.AgentBeforeSettle { Outcome = _lastActivityOutcome },
                drafts => Task.FromResult(BuildBoundaryContext(drafts, SessionBoundaryKind.AgentBeforeSettle)))
                .ConfigureAwait(false);

            CommitBoundaryDrafts(result.Entries);
            FlushPendingCustomMessages();
            var finalContext = BuildBoundaryContext([], SessionBoundaryKind.AgentBeforeSettle);
            if (_abortDuringBeforeSettle)
            {
                return false;
            }

            var shouldContinue = result.Continue || _agent.HasQueuedMessages();
            if (shouldContinue && !finalContext.CanContinue)
            {
                if (result.Continue)
                {
                    ReportInvalidBoundaryContinuation(SessionBoundaryKind.AgentBeforeSettle);
                }

                return false;
            }

            return shouldContinue;
        }
        finally
        {
            _isBeforeSettle = false;
        }
    }

    /// <summary>TS <c>_tryExecuteExtensionCommand(text)</c>（TS 2103-2130）：命中并执行返回 <c>true</c>。</summary>
    private async Task<bool> TryExecuteExtensionCommandAsync(string text)
    {
        var spaceIndex = text.IndexOf(' ');
        var commandName = spaceIndex == -1 ? text[1..] : text[1..spaceIndex];
        var args = spaceIndex == -1 ? string.Empty : text[(spaceIndex + 1)..];

        var command = _extensionRunner.GetCommand(commandName);
        if (command is null)
        {
            return false;
        }

        var ctx = _extensionRunner.CreateCommandContext();
        try
        {
            await command.Handler(args, ctx).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            _extensionRunner.EmitError(new ExtensionError(
                $"command:{commandName}",
                "command",
                error.Message,
                error.StackTrace));
            return true;
        }
    }

    // =========================================================================
    // 重试（TS 3697-3753）
    // =========================================================================

    /// <summary>TS <c>_prepareRetry(message)</c>（TS 3697-3740）：排定一次退避重试。</summary>
    private async Task<bool> PrepareRetryAsync(AssistantMessage message)
    {
        var settings = SettingsManager.GetRetrySettings();
        var (shouldRetry, nextAttempt) = AdvanceRetryAttempt(settings.Enabled, _retryAttempt, settings.MaxRetries);
        _retryAttempt = nextAttempt;
        if (!shouldRetry)
        {
            return false;
        }

        var policy = new RetryPolicy
        {
            Enabled = settings.Enabled,
            MaxRetries = settings.MaxRetries,
            BaseDelayMs = settings.BaseDelayMs,
            MaxAgentDelayMs = settings.MaxAgentDelayMs,
        };
        var delayMs = Retry.RetryDelayMs(policy, _retryAttempt);

        Emit(new AgentSessionEvent.AutoRetryStartEvent(
            Attempt: _retryAttempt,
            MaxAttempts: settings.MaxRetries,
            DelayMs: (int)delayMs,
            ErrorMessage: ResolveRetryErrorMessage(message.ErrorMessage)));

        // 失败的一次保留在原始历史里，但以 context_edit 从模型投影里剔除。
        OmitRecoveryAttempt(message);

        _retryAbortController = new CancellationTokenSource();
        try
        {
            await Pi.CodingAgent.Utils.Sleep.SleepAsync((int)delayMs, _retryAbortController.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 退避期间被中止——发出结束事件让 UI 清理。
            FinishCancelledRetry();
            return false;
        }
        finally
        {
            _retryAbortController = null;
        }

        return true;
    }

    /// <summary>TS <c>_finishCancelledRetry()</c>（TS 3743-3753）。</summary>
    private void FinishCancelledRetry()
    {
        if (!ShouldEmitCancelledRetry(_retryAttempt))
        {
            return;
        }

        var attempt = _retryAttempt;
        _retryAttempt = 0;
        Emit(new AgentSessionEvent.AutoRetryEndEvent(
            Success: false,
            Attempt: attempt,
            FinalError: "Retry cancelled"));
    }

    /// <summary>TS <c>_omitRecoveryAttempt(message, toolResults)</c>（TS 1224-1241）。</summary>
    private void OmitRecoveryAttempt(AssistantMessage message, IReadOnlyList<ChatMessage>? toolResults = null)
    {
        var targets = new List<ChatMessage> { message };
        if (toolResults is not null)
        {
            targets.AddRange(toolResults);
        }

        var targetIds = targets.Select(FindPersistedMessageEntryIdFor).ToList();
        var agentMessages = _agent.Messages;
        var unresolvedProjectedTarget = targets
            .Where((target, index) => targetIds[index] is null && agentMessages.Contains(target))
            .Any();
        if (unresolvedProjectedTarget)
        {
            throw new InvalidOperationException(
                "Cannot persist recovery omission because a projected message has no source entry");
        }

        foreach (var targetId in targetIds)
        {
            if (targetId is null)
            {
                continue;
            }

            var editId = SessionManager.AppendContextEdit(targetId, null);
            var entry = SessionManager.GetEntry(editId);
            if (entry is not null)
            {
                Emit(new AgentSessionEvent.EntryAppendedEvent(entry));
            }
        }

        RefreshFinalizedContext();
    }

    // =========================================================================
    // 选择记录与扩展失败事件（TS 612-626 / 1041-1045）
    // =========================================================================

    /// <summary>TS <c>_recordSelection()</c>（TS 612-626）。</summary>
    private void RecordSelection()
    {
        var model = Model;
        if (model is null)
        {
            return;
        }

        var recorded = VirtualModels.GetBranchSelection(
            SessionManager.GetBranch(),
            (provider, modelId) => _modelRuntime.GetPhysicalModel(provider, modelId));

        BranchModelSelection? recordedSelection = recorded is { } selection
            ? new BranchModelSelection(selection.Provider, selection.ModelId)
            : null;
        bool? recordedModelIsVirtual = recorded is { } resolved
            ? _modelRuntime.GetPhysicalModel(resolved.Provider, resolved.ModelId) is { } recordedModel
                ? VirtualModels.IsVirtualModel(recordedModel)
                : null
            : null;

        if (!ShouldRecordSelection(
                recordedSelection,
                model.Provider,
                model.Id,
                VirtualModels.IsVirtualModel(model),
                recordedModelIsVirtual))
        {
            return;
        }

        SessionManager.AppendModelChange(model.Provider, model.Id);
    }

    /// <summary>TS <c>_emitSessionCompactFailed(event)</c>（TS 1041-1045）。</summary>
    private Task EmitSessionCompactFailedAsync(
        string reason, string? errorMessage, bool aborted, bool willRetry, bool fromExtension)
    {
        if (!_extensionRunner.HasHandlers(ExtensionEventNames.SessionCompactFailed))
        {
            return Task.CompletedTask;
        }

        return _extensionRunner.EmitAsync(new ExtensionEvent.SessionCompactFailed(
            reason, errorMessage, aborted, willRetry, fromExtension));
    }
}
