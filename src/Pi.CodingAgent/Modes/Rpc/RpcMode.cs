using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Pi.Tui.Components;
using ExtensionCommandContextActions = Pi.CodingAgent.Core.Extensions.ExtensionCommandContextActions;
using ExtensionNewSessionOptions = Pi.CodingAgent.Core.Extensions.NewSessionOptions;
using KeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

namespace Pi.CodingAgent.Modes.Rpc;

// ============================================================================
// RPC 模式 — TS `modes/rpc/rpc-mode.ts`（819 行）。
//
// stdin 收 JSON 命令、stdout 出 JSON 响应与事件；扩展的交互请求也走 stdout，
// 客户端经 stdin 回 extension_ui_response。协议见 rpc-types.ts 顶部注释。
//
// 与 TS 的对应关系：
// - `output` / `waitForRawStdoutBackpressure` / `flushRawStdout` / `takeOverStdout`
//   全部经 <see cref="RpcModeOptions"/> 注入，默认走 <see cref="OutputGuard"/>
//   （TS 测试整体 mock output-guard，端口用同一组接缝）；
// - `attachJsonlLineReader(process.stdin, ...)` 抽象成 <see cref="IRpcInput"/>，
//   默认读 stdin（TS 测试 mock jsonl 抓 lineHandler，端口用测试替身）；
// - `process.exit` 经 <see cref="RpcModeOptions.Exit"/> 注入；
// - 信号处理（SIGTERM/SIGHUP）在 Windows 上自动跳过 SIGHUP，且可整体关闭。
// ============================================================================

/// <summary>RPC 模式的输入抽象。对应 TS <c>attachJsonlLineReader</c> 的挂载/摘除。</summary>
public interface IRpcInput
{
    /// <summary>开始读取；每来一行调用一次 <paramref name="onLine"/>（同步回调，勿阻塞）。</summary>
    /// <param name="onEnd">输入流结束时调用（对应 TS <c>process.stdin.on("end")</c> → shutdown）。</param>
    void Start(Action<string> onLine, Action? onEnd = null);

    /// <summary>停止读取（对应 TS 的 detach + <c>process.stdin.pause()</c>）。</summary>
    void Stop();
}

/// <summary><see cref="RpcMode.RunAsync"/> 的选项（测试接缝 + 平台差异开关）。</summary>
public sealed record RpcModeOptions
{
    /// <summary>输出一个对象（默认 <see cref="OutputGuard.WriteRawStdout"/> + 序列化）。</summary>
    public Action<object>? Output { get; init; }

    /// <summary>等直写队列排空（默认 <see cref="OutputGuard.WaitForRawStdoutBackpressureAsync"/>）。</summary>
    public Func<Task>? WaitBackpressure { get; init; }

    /// <summary>排空并 flush（默认 <see cref="OutputGuard.FlushRawStdoutAsync"/>）。</summary>
    public Func<Task>? FlushOutput { get; init; }

    /// <summary>是否接管 stdout（默认 true；测试关掉以免污染控制台）。</summary>
    public bool TakeOverStdout { get; init; } = true;

    /// <summary>stdin 行输入（默认读进程 stdin）。</summary>
    public IRpcInput? Input { get; init; }

    /// <summary>是否注册 SIGTERM/SIGHUP 处理器（默认 true）。</summary>
    public bool RegisterSignalHandlers { get; init; } = true;

    /// <summary>进程退出（默认 <see cref="Environment.Exit(int)"/>）。</summary>
    public Action<int>? Exit { get; init; }
}

/// <summary>RPC 模式入口。对应 TS <c>runRpcMode</c>。</summary>
public static class RpcMode
{
    /// <summary>
    /// 以 RPC 模式运行。对应 TS <c>runRpcMode(runtimeHost)</c>——返回的 Task 永不完成
    /// （TS 是 <c>new Promise(() =&gt; {})</c>），进程退出由 shutdown / 信号驱动。
    /// </summary>
    public static Task RunAsync(AgentSessionRuntime runtimeHost, RpcModeOptions? options = null)
    {
        var session = new RpcModeSession(runtimeHost, options ?? new RpcModeOptions());
        return session.RunAsync();
    }
}

/// <summary>
/// RPC 模式的会话状态机。对应 TS <c>runRpcMode</c> 闭包内的全部状态。
/// </summary>
internal sealed class RpcModeSession
{
    private readonly RpcModeOptions _options;
    private readonly AgentSessionRuntime _runtimeHost;
    private readonly Action<object> _output;
    private readonly Func<Task> _waitBackpressure;
    private readonly Func<Task> _flushOutput;
    private readonly IRpcInput _input;
    private readonly Action<int> _exit;
    private readonly List<Action> _signalCleanupHandlers = [];

    /// <summary>待扩展 UI 响应的请求（只 resolve；TS 存的 reject 槽全程未被调用）。</summary>
    private readonly Dictionary<string, Action<RpcExtensionUIResponse>> _pendingExtensionRequests = [];

    private AgentSession _session;
    private IDisposable? _unsubscribe;
    private IDisposable? _unsubscribeBackpressure;
    private bool _shutdownRequested;
    private bool _shuttingDown;

    public RpcModeSession(AgentSessionRuntime runtimeHost, RpcModeOptions options)
    {
        _runtimeHost = runtimeHost;
        _options = options;
        _session = runtimeHost.Session;
        _output = options.Output ?? (value => OutputGuard.WriteRawStdout(JsonL.SerializeLine(value)));
        _waitBackpressure = options.WaitBackpressure ?? (() => OutputGuard.WaitForRawStdoutBackpressureAsync());
        _flushOutput = options.FlushOutput ?? (() => OutputGuard.FlushRawStdoutAsync());
        _input = options.Input ?? new StdinRpcInput();
        _exit = options.Exit ?? (code => Environment.Exit(code));
    }

    public async Task RunAsync()
    {
        if (_options.TakeOverStdout)
        {
            OutputGuard.TakeOverStdout();
        }

        _runtimeHost.SetRebindSession(_ => RebindSessionAsync());

        await RebindSessionAsync().ConfigureAwait(false);
        RegisterSignalHandlers();

        _input.Start(
            line =>
            {
                _ = HandleInputLineAsync(line);
            },
            () =>
            {
                // 对应 TS 的 onInputEnd：stdin 关闭即收工。
                _ = ShutdownAsync();
            });

        // 保活：TS 是 `return new Promise(() => {})`。
        await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.ConfigureAwait(false);
    }

    // =========================================================================
    // 扩展绑定 / 事件订阅（TS rebindSession）
    // =========================================================================

    private async Task RebindSessionAsync()
    {
        _session = _runtimeHost.Session;
        await _session.BindExtensionsAsync(new ExtensionBindings
        {
            UiContext = CreateExtensionUIContext(),
            Mode = ExtensionMode.Rpc,
            CommandContextActions = CreateCommandContextActions(),
            ShutdownHandler = () => _shutdownRequested = true,
            OnError = error => _output(new JsonObject
            {
                ["type"] = "extension_error",
                ["extensionPath"] = error.ExtensionPath,
                ["event"] = error.Event,
                ["error"] = error.Error,
            }),
        }).ConfigureAwait(false);

        _unsubscribe?.Dispose();
        _unsubscribeBackpressure?.Dispose();
        _unsubscribe = _session.Subscribe(@event =>
        {
            _output(JsonEvent.ToJsonEvent(@event));
            if (@event is AgentSessionEvent.AgentSettledEvent)
            {
                _ = CheckShutdownRequestedAsync();
            }
        });
        _unsubscribeBackpressure = _session.Agent.Subscribe(async (_, _) =>
        {
            await _waitBackpressure().ConfigureAwait(false);
        });
    }

    private ExtensionCommandContextActions CreateCommandContextActions() => new()
    {
        WaitForIdle = () => _session.WaitForIdleAsync(),
        NewSession = async options => (await _runtimeHost.NewSessionAsync(options).ConfigureAwait(false)).Cancelled,
        Fork = (entryId, forkOptions) => ForkAndReportCancelledAsync(entryId, forkOptions),
        NavigateTree = (targetId, navigateOptions) => NavigateTreeAndReportCancelledAsync(targetId, navigateOptions),
        SwitchSession = (sessionPath, switchOptions) => SwitchSessionAndReportCancelledAsync(sessionPath, switchOptions),
        Reload = async () => await _session.ReloadAsync().ConfigureAwait(false),
    };

    private async Task<bool> ForkAndReportCancelledAsync(string entryId, ForkOptions? forkOptions)
        => (await _runtimeHost.ForkAsync(entryId, forkOptions).ConfigureAwait(false)).Cancelled;

    private async Task<bool> NavigateTreeAndReportCancelledAsync(string targetId, NavigateTreeOptions? navigateOptions)
    {
        var result = await _session.NavigateTreeAsync(targetId, new AgentSession.NavigateTreeSessionOptions
        {
            Summarize = navigateOptions?.Summarize,
            CustomInstructions = navigateOptions?.CustomInstructions,
            ReplaceInstructions = navigateOptions?.ReplaceInstructions,
            Label = navigateOptions?.Label,
        }).ConfigureAwait(false);
        return result.Cancelled;
    }

    private async Task<bool> SwitchSessionAndReportCancelledAsync(string sessionPath, SwitchSessionOptions? switchOptions)
    {
        var result = await _runtimeHost.SwitchSessionAsync(sessionPath, new SwitchSessionRuntimeOptions
        {
            WithSession = switchOptions?.WithSession,
        }).ConfigureAwait(false);
        return result.Cancelled;
    }

    // =========================================================================
    // 扩展 UI 上下文（TS createExtensionUIContext）
    // =========================================================================

    private IExtensionUIContext CreateExtensionUIContext() => new RpcExtensionUIContext(this);

    /// <summary>
    /// 带 signal/timeout 的对话框请求。对应 TS <c>createDialogPromise</c>。
    /// </summary>
    private Task<T> CreateDialogPromiseAsync<T>(
        ExtensionUIDialogOptions? opts,
        T defaultValue,
        Func<string, RpcExtensionUIRequest> request,
        Func<RpcExtensionUIResponse, T> parseResponse)
    {
        if (opts?.Signal is { IsCancellationRequested: true })
        {
            return Task.FromResult(defaultValue);
        }

        var id = Guid.NewGuid().ToString();
        var settled = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? timeout = null;
        CancellationTokenRegistration? abortRegistration = null;

        void Cleanup()
        {
            timeout?.Dispose();
            abortRegistration?.Dispose();
            _pendingExtensionRequests.Remove(id);
        }

        if (opts?.TimeoutMs is { } timeoutMs)
        {
            timeout = new CancellationTokenSource(timeoutMs);
            timeout.Token.Register(() =>
            {
                Cleanup();
                settled.TrySetResult(defaultValue);
            });
        }

        if (opts?.Signal is { } signal)
        {
            abortRegistration = signal.Register(() =>
            {
                Cleanup();
                settled.TrySetResult(defaultValue);
            });
        }

        _pendingExtensionRequests[id] = response =>
        {
            Cleanup();
            settled.TrySetResult(parseResponse(response));
        };
        _output(request(id));
        return settled.Task;
    }

    private sealed class RpcExtensionUIContext(RpcModeSession owner) : IExtensionUIContext
    {
        public Task<string?> Select(string title, IReadOnlyList<string> options, ExtensionUIDialogOptions? opts = null)
            => owner.CreateDialogPromiseAsync(
                opts,
                (string?)null,
                id => new RpcExtensionUIRequest
                {
                    Id = id,
                    Method = "select",
                    Title = title,
                    Options = options,
                    Timeout = opts?.TimeoutMs,
                },
                response => response.Cancelled == true ? null : response.Value);

        public Task<bool> Confirm(string title, string message, ExtensionUIDialogOptions? opts = null)
            => owner.CreateDialogPromiseAsync(
                opts,
                false,
                id => new RpcExtensionUIRequest
                {
                    Id = id,
                    Method = "confirm",
                    Title = title,
                    Message = message,
                    Timeout = opts?.TimeoutMs,
                },
                response => response.Cancelled == true ? false : response.Confirmed ?? false);

        public Task<string?> Input(string title, string? placeholder = null, ExtensionUIDialogOptions? opts = null)
            => owner.CreateDialogPromiseAsync(
                opts,
                (string?)null,
                id => new RpcExtensionUIRequest
                {
                    Id = id,
                    Method = "input",
                    Title = title,
                    Placeholder = placeholder,
                    Timeout = opts?.TimeoutMs,
                },
                response => response.Cancelled == true ? null : response.Value);

        public void Notify(string message, string? type = null)
        {
            // Fire and forget — 无需响应。
            owner._output(new RpcExtensionUIRequest
            {
                Id = Guid.NewGuid().ToString(),
                Method = "notify",
                Message = message,
                NotifyType = type,
            });
        }

        public Action OnTerminalInput(TerminalInputHandler handler)
        {
            // RPC 模式没有裸终端输入。
            return () => { };
        }

        public void SetStatus(string key, string? text)
        {
            // Fire and forget — 无需响应。
            owner._output(new RpcExtensionUIRequest
            {
                Id = Guid.NewGuid().ToString(),
                Method = "setStatus",
                StatusKey = key,
                StatusText = text,
            });
        }

        public void SetWorkingMessage(string? message = null)
        {
            // RPC 模式不支持（需要 TUI loader）。
        }

        public void SetWorkingVisible(bool visible)
        {
            // RPC 模式不支持（需要 TUI loader）。
        }

        public void SetWorkingIndicator(WorkingIndicatorOptions? options = null)
        {
            // RPC 模式不支持（需要 TUI loader）。
        }

        public void SetHiddenThinkingLabel(string? label = null)
        {
            // RPC 模式不支持（需要消息渲染）。
        }

        public void SetWidget(string key, IReadOnlyList<string>? content, ExtensionWidgetOptions? options = null)
        {
            // RPC 模式只支持字符串数组；组件工厂被忽略（另一重载）。
            owner._output(new RpcExtensionUIRequest
            {
                Id = Guid.NewGuid().ToString(),
                Method = "setWidget",
                WidgetKey = key,
                WidgetLines = content,
                WidgetPlacement = options?.Placement,
            });
        }

        public void SetWidget(string key, Func<ITui, Theme, IComponent>? factory, ExtensionWidgetOptions? options = null)
        {
            // 组件工厂在 RPC 模式下被忽略。
        }

        public void SetFooter(Func<ITui, Theme, IReadonlyFooterDataProvider, IComponent>? factory)
        {
            // RPC 模式不支持（需要 TUI）。
        }

        public void SetHeader(Func<ITui, Theme, IComponent>? factory)
        {
            // RPC 模式不支持（需要 TUI）。
        }

        public void SetTitle(string title)
        {
            // Fire and forget — 宿主可实现终端标题控制。
            owner._output(new RpcExtensionUIRequest
            {
                Id = Guid.NewGuid().ToString(),
                Method = "setTitle",
                Text = title,
            });
        }

        public Task<T> Custom<T>(
            Func<ITui, Theme, KeybindingsManager, Action<T>, IComponent> factory,
            ExtensionCustomOptions? options = null)
        {
            // 自定义 UI 在 RPC 模式下不支持。
            return Task.FromResult<T>(default(T)!);
        }

        public Task<T> CustomAsync<T>(
            Func<ITui, Theme, KeybindingsManager, Action<T>, Task<IComponent>> factory,
            ExtensionCustomOptions? options = null)
        {
            // 自定义 UI 在 RPC 模式下不支持。
            return Task.FromResult<T>(default(T)!);
        }

        public void PasteToEditor(string text) => SetEditorText(text);

        public void SetEditorText(string text)
        {
            // Fire and forget — 宿主可实现编辑器控制。
            owner._output(new RpcExtensionUIRequest
            {
                Id = Guid.NewGuid().ToString(),
                Method = "set_editor_text",
                Text = text,
            });
        }

        public string GetEditorText()
        {
            // 同步方法等不了 RPC 响应；宿主需要就自己记编辑器状态。
            return string.Empty;
        }

        public async Task<string?> Editor(string title, string? prefill = null)
        {
            var id = Guid.NewGuid().ToString();
            var settled = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner._pendingExtensionRequests[id] = response =>
            {
                if (response.Cancelled == true)
                {
                    settled.TrySetResult(null);
                }
                else
                {
                    settled.TrySetResult(response.Value);
                }
            };
            owner._output(new RpcExtensionUIRequest
            {
                Id = id,
                Method = "editor",
                Title = title,
                Prefill = prefill,
            });
            return await settled.Task.ConfigureAwait(false);
        }

        public void AddAutocompleteProvider(AutocompleteProviderFactory factory)
        {
            // RPC 模式不支持自动补全组合。
        }

        public void SetEditorComponent(EditorFactory? factory)
        {
            // RPC 模式不支持自定义编辑器组件。
        }

        public EditorFactory? GetEditorComponent() => null;

        public Theme Theme => ThemeRuntime.Current;

        public IReadOnlyList<ThemeInfo> GetAllThemes() => [];

        public Theme? GetTheme(string name) => null;

        public SetThemeResult SetTheme(string name)
            => new(false, "Theme switching not supported in RPC mode");

        public SetThemeResult SetTheme(Theme theme)
            => new(false, "Theme switching not supported in RPC mode");

        public bool GetToolsExpanded() => false;

        public void SetToolsExpanded(bool expanded)
        {
            // RPC 模式没有 TUI。
        }
    }

    // =========================================================================
    // 信号处理（TS registerSignalHandlers）
    // =========================================================================

    private void RegisterSignalHandlers()
    {
        if (!OperatingSystem.IsWindows())
        {
            RegisterPosixSignal(PosixSignal.SIGHUP);
        }

        RegisterPosixSignal(PosixSignal.SIGTERM);
    }

    private void RegisterPosixSignal(PosixSignal signal)
    {
        var name = signal == PosixSignal.SIGHUP ? "SIGHUP" : "SIGTERM";
        void HandleSignal(PosixSignalContext context)
        {
            Shell.KillTrackedDetachedChildren();
            _ = ShutdownAsync(name == "SIGHUP" ? 129 : 143, name);
        }

        var registration = PosixSignalRegistration.Create(signal, HandleSignal);
        _signalCleanupHandlers.Add(registration.Dispose);
    }

    // =========================================================================
    // 命令处理（TS handleCommand，33 个分支）
    // =========================================================================

    private Task<JsonObject?> HandleCommandAsync(RpcCommand command)
    {
        var id = command.Id;
        var session = _session;
        switch (command)
        {
            // ---------------------------------------------------------------
            // Prompting
            // ---------------------------------------------------------------

            case RpcPromptCommand prompt:
                return PromptAsync(id, prompt);

            case RpcSteerCommand steer:
                return SteerAsync(id, steer);

            case RpcFollowUpCommand followUp:
                return FollowUpAsync(id, followUp);

            case RpcAbortCommand:
                return AbortAsync(id);

            case RpcClearQueueCommand:
                return ClearQueueAsync(id);

            case RpcNewSessionCommand newSession:
                return NewSessionAsync(id, newSession);

            // ---------------------------------------------------------------
            // State
            // ---------------------------------------------------------------

            case RpcGetStateCommand:
                return Task.FromResult<JsonObject?>(GetState(id));

            // ---------------------------------------------------------------
            // Model
            // ---------------------------------------------------------------

            case RpcSetModelCommand setModel:
                return SetModelAsync(id, setModel);

            case RpcCycleModelCommand:
                return CycleModelAsync(id);

            case RpcGetAvailableModelsCommand:
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "get_available_models", new
                {
                    models = session.ModelRuntime.GetAvailableSnapshot(),
                }));

            // ---------------------------------------------------------------
            // Thinking
            // ---------------------------------------------------------------

            case RpcSetThinkingLevelCommand setThinking:
                session.SetThinkingLevel(setThinking.Level);
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "set_thinking_level"));

            case RpcCycleThinkingLevelCommand:
            {
                var level = session.CycleThinkingLevel();
                return Task.FromResult<JsonObject?>(level is null
                    ? RpcResponses.SuccessNull(id, "cycle_thinking_level")
                    : RpcResponses.Success(id, "cycle_thinking_level", new { level }));
            }

            case RpcGetAvailableThinkingLevelsCommand:
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "get_available_thinking_levels", new
                {
                    levels = session.GetAvailableThinkingLevels(),
                }));

            // ---------------------------------------------------------------
            // Queue modes
            // ---------------------------------------------------------------

            case RpcSetSteeringModeCommand setSteering:
                session.SetSteeringMode(setSteering.Mode);
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "set_steering_mode"));

            case RpcSetFollowUpModeCommand setFollowUp:
                session.SetFollowUpMode(setFollowUp.Mode);
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "set_follow_up_mode"));

            // ---------------------------------------------------------------
            // Compaction
            // ---------------------------------------------------------------

            case RpcCompactCommand compact:
                return CompactAsync(id, compact);

            case RpcSetAutoCompactionCommand setAutoCompaction:
                session.SetAutoCompactionEnabled(setAutoCompaction.Enabled);
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "set_auto_compaction"));

            // ---------------------------------------------------------------
            // Retry
            // ---------------------------------------------------------------

            case RpcSetAutoRetryCommand setAutoRetry:
                session.SetAutoRetryEnabled(setAutoRetry.Enabled);
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "set_auto_retry"));

            case RpcAbortRetryCommand:
                session.AbortRetry();
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "abort_retry"));

            // ---------------------------------------------------------------
            // Bash
            // ---------------------------------------------------------------

            case RpcBashCommand bash:
                return BashAsync(id, bash);

            case RpcAbortBashCommand:
                session.AbortBash();
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "abort_bash"));

            // ---------------------------------------------------------------
            // Session
            // ---------------------------------------------------------------

            case RpcGetSessionStatsCommand:
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "get_session_stats", session.GetSessionStats()));

            case RpcExportHtmlCommand exportHtml:
                return ExportHtmlAsync(id, exportHtml);

            case RpcSwitchSessionCommand switchSession:
                return SwitchSessionAsync(id, switchSession);

            case RpcForkCommand fork:
                return ForkAsync(id, fork);

            case RpcCloneCommand:
                return CloneAsync(id);

            case RpcGetForkMessagesCommand:
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "get_fork_messages", new
                {
                    messages = session.GetUserMessagesForForking(),
                }));

            case RpcGetEntriesCommand getEntries:
                return Task.FromResult<JsonObject?>(GetEntries(id, getEntries));

            case RpcGetTreeCommand:
            {
                var sessionManager = session.SessionManager;
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "get_tree", new
                {
                    tree = sessionManager.GetTree(),
                    leafId = sessionManager.GetLeafId(),
                }));
            }

            case RpcGetLastAssistantTextCommand:
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "get_last_assistant_text", new
                {
                    text = session.GetLastAssistantText(),
                }));

            case RpcSetSessionNameCommand setName:
                return Task.FromResult<JsonObject?>(SetSessionName(id, setName));

            // ---------------------------------------------------------------
            // Messages
            // ---------------------------------------------------------------

            case RpcGetMessagesCommand:
                return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "get_messages", new
                {
                    messages = session.Messages,
                }));

            // ---------------------------------------------------------------
            // Commands
            // ---------------------------------------------------------------

            case RpcGetCommandsCommand:
                return Task.FromResult<JsonObject?>(GetCommands(id));

            default:
                throw new InvalidOperationException($"Unknown command: {RpcClient.GetCommandType(command)}");
        }
    }

    // -------------------------------------------------------------------------
    // 各分支的本体（按 TS 顺序）
    // -------------------------------------------------------------------------

    private Task<JsonObject?> PromptAsync(string? id, RpcPromptCommand command)
    {
        // 立即开始处理，但权威响应等 preflight 成功后才发；排队 / 被消费的提示词也算成功。
        // TS 的 `void session.prompt(...)` 是即发即忘：本分支不等提示词跑完就返回，
        // 好让 handleInputLine 继续走 checkShutdownRequested。
        var preflightSucceeded = false;
        var prompt = _session.PromptAsync(command.Message, new PromptOptions
        {
            Images = command.Images,
            StreamingBehavior = command.StreamingBehavior,
            Source = InputSource.Rpc,
            PreflightResult = disposition =>
            {
                preflightSucceeded = true;
                _output(RpcResponses.Success(id, "prompt", new RpcDispositionData { Disposition = disposition }));
            },
        });

        _ = ObservePromptAsync(prompt, id, () => preflightSucceeded);
        return Task.FromResult<JsonObject?>(null);

        async Task ObservePromptAsync(Task task, string? id, Func<bool> preflight)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // 对应 TS 的 `.catch`：preflight 未发声才补一条错误响应。
                if (!preflight())
                {
                    _output(RpcResponses.Error(id, "prompt", error.Message));
                }
            }
        }
    }

    private async Task<JsonObject?> SteerAsync(string? id, RpcSteerCommand command)
    {
        var disposition = await _session.SteerAsync(
            command.Message,
            command.Images,
            new QueueInputOptions { Source = InputSource.Rpc }).ConfigureAwait(false);
        return RpcResponses.Success(id, "steer", new RpcDispositionData { Disposition = disposition });
    }

    private async Task<JsonObject?> FollowUpAsync(string? id, RpcFollowUpCommand command)
    {
        var disposition = await _session.FollowUpAsync(
            command.Message,
            command.Images,
            new QueueInputOptions { Source = InputSource.Rpc }).ConfigureAwait(false);
        return RpcResponses.Success(id, "follow_up", new RpcDispositionData { Disposition = disposition });
    }

    private async Task<JsonObject?> AbortAsync(string? id)
    {
        await _session.AbortAsync().ConfigureAwait(false);
        return RpcResponses.Success(id, "abort");
    }

    private Task<JsonObject?> ClearQueueAsync(string? id)
    {
        var (steering, followUp) = _session.ClearQueue();
        return Task.FromResult<JsonObject?>(RpcResponses.Success(id, "clear_queue", new RpcClearedQueueData
        {
            Steering = steering,
            FollowUp = followUp,
        }));
    }

    private async Task<JsonObject?> NewSessionAsync(string? id, RpcNewSessionCommand command)
    {
        var options = command.ParentSession is { } parentSession
            ? new ExtensionNewSessionOptions { ParentSession = parentSession }
            : null;
        var result = await _runtimeHost.NewSessionAsync(options).ConfigureAwait(false);
        if (!result.Cancelled)
        {
            await RebindSessionAsync().ConfigureAwait(false);
        }

        return RpcResponses.Success(id, "new_session", result);
    }

    private JsonObject GetState(string? id)
    {
        var session = _session;
        return RpcResponses.Success(id, "get_state", new RpcSessionState
        {
            Model = session.Model,
            ThinkingLevel = session.ThinkingLevel,
            IsStreaming = session.IsStreaming,
            IsCompacting = session.IsCompacting,
            SteeringMode = session.SteeringMode,
            FollowUpMode = session.FollowUpMode,
            SessionFile = session.SessionFile,
            SessionId = session.SessionId,
            SessionName = session.SessionName,
            AutoCompactionEnabled = session.AutoCompactionEnabled,
            MessageCount = session.Messages.Count,
            PendingMessageCount = session.PendingMessageCount,
        });
    }

    private async Task<JsonObject?> SetModelAsync(string? id, RpcSetModelCommand command)
    {
        var session = _session;
        var model = session.ModelRuntime.GetAvailableSnapshot()
            .FirstOrDefault(m => m.Provider == command.Provider && m.Id == command.ModelId);
        if (model is null)
        {
            return RpcResponses.Error(id, "set_model", $"Model not found: {command.Provider}/{command.ModelId}");
        }

        await session.SetModelAsync(model).ConfigureAwait(false);
        return RpcResponses.Success(id, "set_model", model);
    }

    private async Task<JsonObject?> CycleModelAsync(string? id)
    {
        var result = await _session.CycleModelAsync().ConfigureAwait(false);
        return result is null
            ? RpcResponses.SuccessNull(id, "cycle_model")
            : RpcResponses.Success(id, "cycle_model", result);
    }

    private async Task<JsonObject?> CompactAsync(string? id, RpcCompactCommand command)
    {
        var result = await _session.CompactAsync(new CompactOptions
        {
            CustomInstructions = command.CustomInstructions,
        }).ConfigureAwait(false);
        return RpcResponses.Success(id, "compact", result);
    }

    private async Task<JsonObject?> BashAsync(string? id, RpcBashCommand command)
    {
        var session = _session;
        var eventResult = await session.ExtensionRunner.EmitUserBashAsync(new ExtensionEvent.UserBash(
            command.Command,
            command.ExcludeFromContext ?? false,
            session.SessionManager.GetCwd())).ConfigureAwait(false);

        if (eventResult is UserBashEventResult.WithResult withResult)
        {
            session.RecordBashResult(command.Command, withResult.Result, new ExecuteBashOptions
            {
                ExcludeFromContext = command.ExcludeFromContext,
            });
            return RpcResponses.Success(id, "bash", withResult.Result);
        }

        var result = await session.ExecuteBashAsync(command.Command, null, new ExecuteBashOptions
        {
            ExcludeFromContext = command.ExcludeFromContext,
            Id = id,
            Operations = eventResult is UserBashEventResult.WithOperations withOperations
                ? withOperations.Operations
                : null,
        }).ConfigureAwait(false);
        return RpcResponses.Success(id, "bash", result);
    }

    private async Task<JsonObject?> ExportHtmlAsync(string? id, RpcExportHtmlCommand command)
    {
        var path = await _session.ExportToHtmlAsync(command.OutputPath).ConfigureAwait(false);
        return RpcResponses.Success(id, "export_html", new RpcPathData { Path = path });
    }

    private async Task<JsonObject?> SwitchSessionAsync(string? id, RpcSwitchSessionCommand command)
    {
        var result = await _runtimeHost.SwitchSessionAsync(command.SessionPath).ConfigureAwait(false);
        if (!result.Cancelled)
        {
            await RebindSessionAsync().ConfigureAwait(false);
        }

        return RpcResponses.Success(id, "switch_session", result);
    }

    private async Task<JsonObject?> ForkAsync(string? id, RpcForkCommand command)
    {
        var result = await _runtimeHost.ForkAsync(command.EntryId).ConfigureAwait(false);
        if (!result.Cancelled)
        {
            await RebindSessionAsync().ConfigureAwait(false);
        }

        return RpcResponses.Success(id, "fork", new RpcForkData
        {
            Text = result.SelectedText ?? string.Empty,
            Cancelled = result.Cancelled,
        });
    }

    private async Task<JsonObject?> CloneAsync(string? id)
    {
        var leafId = _session.SessionManager.GetLeafId();
        if (leafId is null)
        {
            return RpcResponses.Error(id, "clone", "Cannot clone session: no current entry selected");
        }

        var result = await _runtimeHost.ForkAsync(leafId, new ForkOptions { Position = ForkPosition.At })
            .ConfigureAwait(false);
        if (!result.Cancelled)
        {
            await RebindSessionAsync().ConfigureAwait(false);
        }

        return RpcResponses.Success(id, "clone", new RpcCancelledData { Cancelled = result.Cancelled });
    }

    private JsonObject GetEntries(string? id, RpcGetEntriesCommand command)
    {
        var sessionManager = _session.SessionManager;
        var entries = sessionManager.GetEntries();
        if (command.Since is { } since)
        {
            // TS: entries.findIndex(e => e.id === since)，-1 即 "Entry not found"。
            var sinceIndex = -1;
            for (var i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].Id, since, StringComparison.Ordinal))
                {
                    sinceIndex = i;
                    break;
                }
            }

            if (sinceIndex < 0)
            {
                return RpcResponses.Error(id, "get_entries", $"Entry not found: {since}");
            }

            entries = entries.Skip(sinceIndex + 1).ToList();
        }

        return RpcResponses.Success(id, "get_entries", new RpcEntriesData
        {
            Entries = entries,
            LeafId = sessionManager.GetLeafId(),
        });
    }

    private JsonObject SetSessionName(string? id, RpcSetSessionNameCommand command)
    {
        var name = command.Name.Trim();
        if (name.Length == 0)
        {
            return RpcResponses.Error(id, "set_session_name", "Session name cannot be empty");
        }

        _session.SetSessionName(name);
        return RpcResponses.Success(id, "set_session_name");
    }

    private JsonObject GetCommands(string? id)
    {
        var session = _session;
        var commands = new List<RpcSlashCommand>();

        foreach (var command in session.ExtensionRunner.GetRegisteredCommands())
        {
            commands.Add(new RpcSlashCommand
            {
                Name = command.InvocationName,
                Description = command.Description,
                Source = "extension",
                SourceInfo = command.SourceInfo,
            });
        }

        foreach (var template in session.PromptTemplates)
        {
            commands.Add(new RpcSlashCommand
            {
                Name = template.Name,
                Description = template.Description,
                Source = "prompt",
                SourceInfo = template.SourceInfo,
            });
        }

        foreach (var skill in session.ResourceLoader.Skills)
        {
            commands.Add(new RpcSlashCommand
            {
                Name = $"skill:{skill.Name}",
                Description = skill.Description,
                Source = "skill",
                SourceInfo = skill.SourceInfo,
            });
        }

        return RpcResponses.Success(id, "get_commands", new RpcCommandsData { Commands = commands });
    }

    // =========================================================================
    // 输入行处理（TS handleInputLine）
    // =========================================================================

    private async Task HandleInputLineAsync(string line)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line);
        }
        catch (Exception parseError)
        {
            _output(RpcResponses.Error(null, "parse", $"Failed to parse command: {parseError.Message}"));
            await _waitBackpressure().ConfigureAwait(false);
            return;
        }

        var rawId = (parsed as JsonObject)?["id"]?.GetValue<string>();
        var rawType = (parsed as JsonObject)?["type"]?.GetValue<string>();

        // 扩展 UI 响应路由（不走 handleCommand）。
        if (rawType == "extension_ui_response")
        {
            if (parsed is JsonObject response && response["id"]?.GetValue<string>() is { } responseId)
            {
                if (_pendingExtensionRequests.Remove(responseId, out var pending))
                {
                    pending(new RpcExtensionUIResponse
                    {
                        Id = responseId,
                        Value = response["value"]?.GetValue<string>(),
                        Confirmed = response["confirmed"] as JsonValue is { } confirmedValue
                            ? confirmedValue.GetValue<bool>()
                            : null,
                        Cancelled = response["cancelled"] as JsonValue is { } cancelledValue
                            ? cancelledValue.GetValue<bool>()
                            : null,
                    });
                }
            }

            return;
        }

        RpcCommand? command = null;
        try
        {
            command = JsonL.TryDeserializeCommand(parsed);
            if (command is null)
            {
                // TS 的 default 分支：`Unknown command: ${command.type}`。
                var unknownType = rawType ?? "undefined";
                _output(RpcResponses.Error(rawId, unknownType, $"Unknown command: {unknownType}"));
                await _waitBackpressure().ConfigureAwait(false);
                return;
            }

            var response = await HandleCommandAsync(command).ConfigureAwait(false);
            if (response is not null)
            {
                _output(response);
                await _waitBackpressure().ConfigureAwait(false);
            }

            await CheckShutdownRequestedAsync().ConfigureAwait(false);
        }
        catch (Exception commandError)
        {
            _output(RpcResponses.Error(
                command?.Id ?? rawId,
                command is null ? rawType ?? "undefined" : RpcClient.GetCommandType(command),
                commandError.Message));
            await _waitBackpressure().ConfigureAwait(false);
        }
    }

    // =========================================================================
    // 关闭（TS shutdown / checkShutdownRequested / onInputEnd）
    // =========================================================================

    private async Task ShutdownAsync(int exitCode = 0, string? signal = null)
    {
        if (_shuttingDown)
        {
            // TS: 重入时直接 process.exit(exitCode)（不再走清理）。
            _exit(exitCode);
            return;
        }

        _shuttingDown = true;
        foreach (var cleanup in _signalCleanupHandlers)
        {
            cleanup();
        }

        _unsubscribe?.Dispose();
        _unsubscribeBackpressure?.Dispose();
        await _runtimeHost.DisposeAsync().ConfigureAwait(false);
        _input.Stop();
        if (signal != "SIGTERM")
        {
            await _flushOutput().ConfigureAwait(false);
        }

        _exit(exitCode);
    }

    private async Task CheckShutdownRequestedAsync()
    {
        if (!_shutdownRequested)
        {
            return;
        }

        await ShutdownAsync().ConfigureAwait(false);
    }

    /// <summary>默认 stdin 输入。</summary>
    private sealed class StdinRpcInput : IRpcInput
    {
        private JsonlLineReader? _reader;

        public void Start(Action<string> onLine, Action? onEnd = null)
        {
            _reader = new JsonlLineReader(Console.OpenStandardInput(), onLine, onEnd);
            _reader.Start();
        }

        public void Stop() => _reader?.Dispose();
    }
}
