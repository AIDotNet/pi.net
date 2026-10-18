using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;

namespace Pi.CodingAgent.Modes.Rpc;

// ============================================================================
// RPC 客户端 — TS `modes/rpc/rpc-client.ts`（617 行）。
//
// 以子进程方式拉起 RPC 模式的 agent，提供全部 27 个命令的类型化入口；
// 会话事件经 onEvent 订阅。线格式与 rpc-mode 共用 JsonL。
// ============================================================================

/// <summary>RPC 客户端构造选项。对应 TS <c>RpcClientOptions</c>。</summary>
public sealed record RpcClientOptions
{
    /// <summary>The executable that hosts the CLI: the running process, or <c>pi</c> on PATH.</summary>
    private static string DefaultFileName => Environment.ProcessPath ?? Config.AppName;

    /// <summary>
    /// 启动 agent 用的可执行文件。TS 固定 <c>"node"</c>（配合 <see cref="CliPath"/> 指向
    /// <c>dist/cli.js</c>）；端口 4g-2 起默认指向**当前进程的可执行文件**（<c>pi</c>），
    /// 因为 .NET 版是原生入口而非 node 脚本（差异 C306）。显式设置时行为与 TS 一致。
    /// </summary>
    public string FileName { get; init; } = DefaultFileName;

    /// <summary>
    /// CLI 入口路径。TS 默认 <c>dist/cli.js</c>；端口默认 <see langword="null"/>——原生入口
    /// 不需要额外的脚本参数。设为非空时按 TS 的方式作为第一个参数传入（差异 C306）。
    /// </summary>
    public string? CliPath { get; init; }

    /// <summary>agent 的工作目录。</summary>
    public string? Cwd { get; init; }

    /// <summary>附加环境变量（叠加在继承的进程环境之上，对齐 TS 的 <c>{...process.env, ...env}</c>）。</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>使用的 provider。</summary>
    public string? Provider { get; init; }

    /// <summary>使用的模型 id。</summary>
    public string? Model { get; init; }

    /// <summary>附加 CLI 参数。</summary>
    public IReadOnlyList<string>? Args { get; init; }
}

/// <summary><see cref="RpcClient.GetAvailableModels"/> 返回的模型条目。对应 TS <c>ModelInfo</c>。</summary>
public sealed record ModelInfo
{
    public required string Provider { get; init; }

    public required string Id { get; init; }

    public required long ContextWindow { get; init; }

    public required bool Reasoning { get; init; }
}

/// <summary>会话事件监听。对应 TS <c>RpcEventListener</c>。</summary>
/// <remarks>
/// TS 侧是 <c>JsonAgentSessionEvent</c>（判别联合的类型视图）；端口直接把解析出的
/// JSON 节点交给监听方——判别只需 <c>node["type"]</c>，完整联合在这里没有增益。
/// </remarks>
public delegate void RpcEventListener(JsonNode @event);

/// <summary>
/// RPC 客户端。对应 TS <c>RpcClient</c>。
/// </summary>
/// <remarks>
/// 非 sealed：<see cref="SendAsync"/> / <see cref="GetData{T}"/> 声明为 internal virtual 供测试
/// 子类替换（对应 TS 测试对私有 send/getData 的 monkey-patch，见
/// test/rpc-client-clear-queue.test.ts / rpc-client-clone.test.ts）。
/// </remarks>
public class RpcClient
{
    private const int RequestTimeoutMs = 30_000;
    private const int InitDelayMs = 100;
    private const int StopGraceMs = 1_000;

    private static readonly JsonSerializerOptions LineOptions = JsonL.Options;

    private readonly RpcClientOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonNode>> _pendingRequests = [];
    private readonly List<RpcEventListener> _eventListeners = [];
    private readonly StringBuilder _stderr = new();

    private Process? _process;
    private JsonlLineReader? _stdoutReader;
    private int _requestId;
    private Exception? _exitError;

    public RpcClient(RpcClientOptions? options = null)
    {
        _options = options ?? new RpcClientOptions();
    }

    /// <summary>收集到的子进程 stderr（调试用）。对应 TS <c>getStderr</c>。</summary>
    public string GetStderr()
    {
        lock (_gate)
        {
            return _stderr.ToString();
        }
    }

    /// <summary>启动 RPC agent 进程。对应 TS <c>start</c>。</summary>
    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_process is not null)
            {
                throw new InvalidOperationException("Client already started");
            }

            _exitError = null;
        }

        var startInfo = CreateStartInfo();
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception error)
        {
            // TS 的 childProcess "error" 事件（如 ENOENT）：记进 exitError 并向上抛。
            lock (_gate)
            {
                _exitError = error;
            }

            throw;
        }

        lock (_gate)
        {
            _process = process;
        }

        // stderr 收集 + 回显（TS: childProcess.stderr?.on("data", ...)）。
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    lock (_gate)
                    {
                        _stderr.AppendLine(line);
                    }

                    Console.Error.WriteLine(line);
                }
            }
            catch (Exception)
            {
                // 进程退出时读 stderr 可能抛 IO 异常：stderr 只是调试信息，忽略。
            }
        });

        process.Exited += OnProcessExited;

        // 严格 JSONL 读取 stdout。
        var reader = new JsonlLineReader(process.StandardOutput.BaseStream, HandleLine);
        lock (_gate)
        {
            _stdoutReader = reader;
        }

        reader.Start();

        // TS: 等一小会儿让进程初始化，再检查是否已经退出。
        await Task.Delay(InitDelayMs).ConfigureAwait(false);

        if (process.HasExited)
        {
            var error = GetExitError(process);
            lock (_gate)
            {
                _exitError = error;
            }

            throw error;
        }
    }

    /// <summary>停止 RPC agent 进程。对应 TS <c>stop</c>。</summary>
    public async Task StopAsync()
    {
        JsonlLineReader? reader;
        Process? process;
        lock (_gate)
        {
            reader = _stdoutReader;
            process = _process;
        }

        if (process is null)
        {
            return;
        }

        reader?.Dispose();
        lock (_gate)
        {
            _stdoutReader = null;
        }

        TryKill(process);
        try
        {
            using var stopTimeout = new CancellationTokenSource(StopGraceMs);
            await process.WaitForExitAsync(stopTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 超时或读输出竞态都不阻塞 stop。
        }

        lock (_gate)
        {
            _process = null;
            _pendingRequests.Clear();
        }
    }

    /// <summary>订阅会话事件；返回退订函数。对应 TS <c>onEvent</c>。</summary>
    public Action OnEvent(RpcEventListener listener)
    {
        lock (_gate)
        {
            _eventListeners.Add(listener);
        }

        return () =>
        {
            lock (_gate)
            {
                _eventListeners.Remove(listener);
            }
        };
    }

    // =========================================================================
    // 命令方法（TS 的同名方法逐个对应）
    // =========================================================================

    /// <summary>发送提示词；处置结果经返回值给出，流式事件走 onEvent。</summary>
    public async Task<string> PromptAsync(
        string message,
        IReadOnlyList<ImageContent>? images = null,
        string? streamingBehavior = null)
    {
        var response = await SendAsync(new RpcPromptCommand { Message = message, Images = images, StreamingBehavior = streamingBehavior })
            .ConfigureAwait(false);
        return GetData<RpcDispositionData>(response).Disposition;
    }

    /// <summary>排队一条 steering 消息（流式中打断）。</summary>
    public async Task<string> SteerAsync(string message, IReadOnlyList<ImageContent>? images = null)
    {
        var response = await SendAsync(new RpcSteerCommand { Message = message, Images = images }).ConfigureAwait(false);
        return GetData<RpcDispositionData>(response).Disposition;
    }

    /// <summary>排队一条 follow-up 消息（agent 结束后处理）。</summary>
    public async Task<string> FollowUpAsync(string message, IReadOnlyList<ImageContent>? images = null)
    {
        var response = await SendAsync(new RpcFollowUpCommand { Message = message, Images = images }).ConfigureAwait(false);
        return GetData<RpcDispositionData>(response).Disposition;
    }

    /// <summary>中止当前操作。</summary>
    public Task AbortAsync() => SendAsync(new RpcAbortCommand());

    /// <summary>清空排队中的 steering / follow-up 消息并返回其文本。</summary>
    public async Task<RpcClearedQueueData> ClearQueueAsync()
    {
        var response = await SendAsync(new RpcClearQueueCommand()).ConfigureAwait(false);
        return GetData<RpcClearedQueueData>(response);
    }

    /// <summary>开新会话（可选父会话谱系）。</summary>
    public async Task<RpcCancelledData> NewSessionAsync(string? parentSession = null)
    {
        var response = await SendAsync(new RpcNewSessionCommand { ParentSession = parentSession }).ConfigureAwait(false);
        return GetData<RpcCancelledData>(response);
    }

    /// <summary>读取当前会话状态。</summary>
    public async Task<RpcSessionState> GetStateAsync()
    {
        var response = await SendAsync(new RpcGetStateCommand()).ConfigureAwait(false);
        return GetData<RpcSessionState>(response);
    }

    /// <summary>按 provider + id 设模型。</summary>
    public async Task<ModelSpec> SetModelAsync(string provider, string modelId)
    {
        var response = await SendAsync(new RpcSetModelCommand { Provider = provider, ModelId = modelId }).ConfigureAwait(false);
        return GetData<ModelSpec>(response);
    }

    /// <summary>切到下一个模型。</summary>
    public async Task<ModelCycleResult?> CycleModelAsync()
    {
        var response = await SendAsync(new RpcCycleModelCommand()).ConfigureAwait(false);
        return GetData<ModelCycleResult?>(response);
    }

    /// <summary>可用模型列表。</summary>
    public async Task<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync()
    {
        var response = await SendAsync(new RpcGetAvailableModelsCommand()).ConfigureAwait(false);
        return GetData<RpcModelsData>(response).Models;
    }

    /// <summary>设置思考档位。</summary>
    public Task SetThinkingLevelAsync(ThinkingLevel level)
        => SendAsync(new RpcSetThinkingLevelCommand { Level = level });

    /// <summary>循环思考档位。</summary>
    public async Task<ThinkingLevel?> CycleThinkingLevelAsync()
    {
        var response = await SendAsync(new RpcCycleThinkingLevelCommand()).ConfigureAwait(false);
        var data = GetData<RpcLevelData?>(response);
        return data?.Level;
    }

    /// <summary>当前模型的可用思考档位。</summary>
    public async Task<IReadOnlyList<ThinkingLevel>> GetAvailableThinkingLevelsAsync()
    {
        var response = await SendAsync(new RpcGetAvailableThinkingLevelsCommand()).ConfigureAwait(false);
        return GetData<RpcLevelsData>(response).Levels;
    }

    /// <summary>设置 steering 排队模式。</summary>
    public Task SetSteeringModeAsync(string mode)
        => SendAsync(new RpcSetSteeringModeCommand { Mode = mode });

    /// <summary>设置 follow-up 排队模式。</summary>
    public Task SetFollowUpModeAsync(string mode)
        => SendAsync(new RpcSetFollowUpModeCommand { Mode = mode });

    /// <summary>压缩会话上下文。</summary>
    public async Task<CompactionResult> CompactAsync(string? customInstructions = null)
    {
        var response = await SendAsync(new RpcCompactCommand { CustomInstructions = customInstructions }).ConfigureAwait(false);
        return GetData<CompactionResult>(response);
    }

    /// <summary>开/关自动压缩。</summary>
    public Task SetAutoCompactionAsync(bool enabled)
        => SendAsync(new RpcSetAutoCompactionCommand { Enabled = enabled });

    /// <summary>开/关自动重试。</summary>
    public Task SetAutoRetryAsync(bool enabled)
        => SendAsync(new RpcSetAutoRetryCommand { Enabled = enabled });

    /// <summary>中止进行中的重试。</summary>
    public Task AbortRetryAsync() => SendAsync(new RpcAbortRetryCommand());

    /// <summary>执行 bash 命令。</summary>
    public async Task<BashResult> BashAsync(string command)
    {
        var response = await SendAsync(new RpcBashCommand { Command = command }).ConfigureAwait(false);
        return GetData<BashResult>(response);
    }

    /// <summary>中止运行中的 bash 命令。</summary>
    public Task AbortBashAsync() => SendAsync(new RpcAbortBashCommand());

    /// <summary>会话统计。</summary>
    public async Task<SessionStats> GetSessionStatsAsync()
    {
        var response = await SendAsync(new RpcGetSessionStatsCommand()).ConfigureAwait(false);
        return GetData<SessionStats>(response);
    }

    /// <summary>导出会话为 HTML。</summary>
    public async Task<RpcPathData> ExportHtmlAsync(string? outputPath = null)
    {
        var response = await SendAsync(new RpcExportHtmlCommand { OutputPath = outputPath }).ConfigureAwait(false);
        return GetData<RpcPathData>(response);
    }

    /// <summary>切换到另一个会话文件。</summary>
    public async Task<RpcCancelledData> SwitchSessionAsync(string sessionPath)
    {
        var response = await SendAsync(new RpcSwitchSessionCommand { SessionPath = sessionPath }).ConfigureAwait(false);
        return GetData<RpcCancelledData>(response);
    }

    /// <summary>从某个条目 fork。</summary>
    public async Task<RpcForkData> ForkAsync(string entryId)
    {
        var response = await SendAsync(new RpcForkCommand { EntryId = entryId }).ConfigureAwait(false);
        return GetData<RpcForkData>(response);
    }

    /// <summary>克隆当前活动分支到新会话。</summary>
    public async Task<RpcCancelledData> CloneAsync()
    {
        var response = await SendAsync(new RpcCloneCommand()).ConfigureAwait(false);
        return GetData<RpcCancelledData>(response);
    }

    /// <summary>可 fork 的消息列表。</summary>
    public async Task<IReadOnlyList<UserMessageForForking>> GetForkMessagesAsync()
    {
        var response = await SendAsync(new RpcGetForkMessagesCommand()).ConfigureAwait(false);
        return GetData<RpcForkMessagesData>(response).Messages;
    }

    /// <summary>按追加顺序取会话条目（可选 <c>since</c> 之后）。</summary>
    public async Task<RpcEntriesData> GetEntriesAsync(string? since = null)
    {
        var response = await SendAsync(new RpcGetEntriesCommand { Since = since }).ConfigureAwait(false);
        return GetData<RpcEntriesData>(response);
    }

    /// <summary>会话条目树。</summary>
    public async Task<RpcTreeData> GetTreeAsync()
    {
        var response = await SendAsync(new RpcGetTreeCommand()).ConfigureAwait(false);
        return GetData<RpcTreeData>(response);
    }

    /// <summary>最后一条 assistant 消息的文本。</summary>
    public async Task<string?> GetLastAssistantTextAsync()
    {
        var response = await SendAsync(new RpcGetLastAssistantTextCommand()).ConfigureAwait(false);
        return GetData<RpcTextData>(response).Text;
    }

    /// <summary>设置会话显示名。</summary>
    public Task SetSessionNameAsync(string name)
        => SendAsync(new RpcSetSessionNameCommand { Name = name });

    /// <summary>会话全部消息。</summary>
    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync()
    {
        var response = await SendAsync(new RpcGetMessagesCommand()).ConfigureAwait(false);
        return GetData<RpcMessagesData>(response).Messages;
    }

    /// <summary>可用命令（扩展命令 / 提示词模板 / 技能）。</summary>
    public async Task<IReadOnlyList<RpcSlashCommand>> GetCommandsAsync()
    {
        var response = await SendAsync(new RpcGetCommandsCommand()).ConfigureAwait(false);
        return GetData<RpcCommandsData>(response).Commands;
    }

    // =========================================================================
    // 帮助方法
    // =========================================================================

    /// <summary>等待 agent 空闲（收到 <c>agent_settled</c>）。</summary>
    public async Task WaitForIdleAsync(int timeoutMs = 60_000)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(timeoutMs);
        using var registration = timeout.Token.Register(() => settled.TrySetException(
            new TimeoutException($"Timeout waiting for agent to become idle. Stderr: {GetStderr()}")));
        var unsubscribe = OnEvent(@event =>
        {
            if (@event["type"]?.GetValue<string>() == "agent_settled")
            {
                settled.TrySetResult();
            }
        });

        try
        {
            await settled.Task.ConfigureAwait(false);
        }
        finally
        {
            unsubscribe();
        }
    }

    /// <summary>收集事件直到 agent 空闲。</summary>
    public async Task<IReadOnlyList<JsonNode>> CollectEventsAsync(int timeoutMs = 60_000)
    {
        var events = new List<JsonNode>();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(timeoutMs);
        using var registration = timeout.Token.Register(() => settled.TrySetException(
            new TimeoutException($"Timeout collecting events. Stderr: {GetStderr()}")));
        var unsubscribe = OnEvent(@event =>
        {
            events.Add(@event);
            if (@event["type"]?.GetValue<string>() == "agent_settled")
            {
                settled.TrySetResult();
            }
        });

        try
        {
            await settled.Task.ConfigureAwait(false);
        }
        finally
        {
            unsubscribe();
        }

        return events;
    }

    /// <summary>发送提示词并等待完成，返回全部事件。</summary>
    public async Task<IReadOnlyList<JsonNode>> PromptAndWaitAsync(
        string message,
        IReadOnlyList<ImageContent>? images = null,
        int timeoutMs = 60_000)
    {
        var events = CollectEventsAsync(timeoutMs);
        await PromptAsync(message, images).ConfigureAwait(false);
        return await events.ConfigureAwait(false);
    }

    // =========================================================================
    // 内部
    // =========================================================================

    private ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo(_options.FileName)
        {
            WorkingDirectory = string.IsNullOrEmpty(_options.Cwd) ? Directory.GetCurrentDirectory() : _options.Cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!string.IsNullOrEmpty(_options.CliPath))
        {
            startInfo.ArgumentList.Add(_options.CliPath!);
        }

        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add("rpc");
        if (!string.IsNullOrEmpty(_options.Provider))
        {
            startInfo.ArgumentList.Add("--provider");
            startInfo.ArgumentList.Add(_options.Provider!);
        }

        if (!string.IsNullOrEmpty(_options.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(_options.Model!);
        }

        if (_options.Args is not null)
        {
            foreach (var arg in _options.Args)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        if (_options.Env is not null)
        {
            foreach (var (key, value) in _options.Env)
            {
                startInfo.Environment[key] = value;
            }
        }

        return startInfo;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process || !ReferenceEquals(process, CurrentProcess()))
        {
            return;
        }

        var error = GetExitError(process);
        lock (_gate)
        {
            _exitError = error;
            RejectPendingRequests(error);
        }
    }

    private Process? CurrentProcess()
    {
        lock (_gate)
        {
            return _process;
        }
    }

    /// <summary>
    /// TS <c>createProcessExitError</c>：退出码 + 信号 + 已收集的 stderr。
    /// </summary>
    /// <remarks>
    /// stderr 取异步泵累积的文本（TS 同样只拿到退出前收到的事件），不再
    /// <c>ReadToEnd()</c>——那会与 stderr 读取泵抢同一个流。
    /// </remarks>
    private Exception GetExitError(Process process)
    {
        int? exitCode = null;
        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }
        }
        catch (Exception)
        {
            // 进程信息可能已不可用；exitCode 留空。
        }

        // TS: `Agent process exited (code=${code} signal=${signal}). Stderr: ...`
        // .NET 不暴露终止信号，signal 恒为 null（对齐 TS 的正常退出文案）。
        var code = exitCode?.ToString() ?? "null";
        var signal = "null";
        return new InvalidOperationException(
            $"Agent process exited (code={code} signal={signal}). Stderr: {GetStderr()}");
    }

    private void HandleLine(string line)
    {
        JsonNode? data;
        try
        {
            data = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            // TS: 忽略非 JSON 行。
            return;
        }

        if (data is not JsonObject)
        {
            return;
        }

        // 待处理请求的响应。
        if (data["type"]?.GetValue<string>() == "response"
            && data["id"]?.GetValue<string>() is { } id)
        {
            TaskCompletionSource<JsonNode>? pending = null;
            lock (_gate)
            {
                if (_pendingRequests.Remove(id, out var removed))
                {
                    pending = removed;
                }
            }

            if (pending is not null)
            {
                pending.TrySetResult(data);
                return;
            }
        }

        // 其余是事件：迭代快照，让派发中退订的监听方不影响后面的监听方。
        RpcEventListener[] listeners;
        lock (_gate)
        {
            listeners = [.. _eventListeners];
        }

        foreach (var listener in listeners)
        {
            listener(data);
        }
    }

    private void RejectPendingRequests(Exception error)
    {
        foreach (var pending in _pendingRequests.Values)
        {
            pending.TrySetException(error);
        }

        _pendingRequests.Clear();
    }

    /// <summary>发出一条命令并等待其响应。对应 TS <c>send</c>。</summary>
    internal virtual async Task<JsonNode> SendAsync(RpcCommand command)
    {
        var process = CurrentProcess();
        if (process is null)
        {
            throw new InvalidOperationException("Client not started");
        }

        lock (_gate)
        {
            if (_exitError is not null)
            {
                throw _exitError;
            }
        }

        if (process.HasExited)
        {
            var error = GetExitError(process);
            lock (_gate)
            {
                _exitError = error;
            }

            throw error;
        }

        if (!IsStdinWritable(process))
        {
            var error = new InvalidOperationException($"Agent process stdin is not writable. Stderr: {GetStderr()}");
            lock (_gate)
            {
                _exitError = error;
            }

            throw error;
        }

        var id = $"req_{Interlocked.Increment(ref _requestId)}";
        var fullCommand = command with { Id = id };
        var pending = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(RequestTimeoutMs);

        lock (_gate)
        {
            _pendingRequests[id] = pending;
        }

        using var registration = timeout.Token.Register(() =>
        {
            bool removed;
            lock (_gate)
            {
                removed = _pendingRequests.Remove(id, out _);
            }

            if (removed)
            {
                pending.TrySetException(new TimeoutException(
                    $"Timeout waiting for response to {GetCommandType(command)}. Stderr: {GetStderr()}"));
            }
        });

        try
        {
            await process.StandardInput.WriteAsync(JsonL.SerializeLine(fullCommand)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // 管道断裂多半是因为进程刚退出：优先用退出错误（TS 的 stdin error 处理器
            // 同样优先 exitError）。
            bool removed;
            Exception failure;
            lock (_gate)
            {
                removed = _pendingRequests.Remove(id, out _);
                failure = _exitError ?? Wrap(error);
            }

            if (removed)
            {
                pending.TrySetException(failure);
            }

            return await pending.Task.ConfigureAwait(false);
        }

        return await pending.Task.ConfigureAwait(false);
    }

    /// <summary>取响应负载；失败响应抛错。对应 TS <c>getData</c>。</summary>
    internal virtual T GetData<T>(JsonNode response)
    {
        if (response["success"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException(response["error"]?.GetValue<string>() ?? "Unknown error");
        }

        var data = response["data"];
        if (data is null)
        {
            // TS: `data as T`——data 缺失时得到 undefined；带负载的命令都有 data。
            return default!;
        }

        return data.Deserialize<T>(LineOptions)!;
    }

    internal static string GetCommandType(RpcCommand command) => command switch    {
        RpcPromptCommand => "prompt",
        RpcSteerCommand => "steer",
        RpcFollowUpCommand => "follow_up",
        RpcAbortCommand => "abort",
        RpcClearQueueCommand => "clear_queue",
        RpcNewSessionCommand => "new_session",
        RpcGetStateCommand => "get_state",
        RpcSetModelCommand => "set_model",
        RpcCycleModelCommand => "cycle_model",
        RpcGetAvailableModelsCommand => "get_available_models",
        RpcSetThinkingLevelCommand => "set_thinking_level",
        RpcCycleThinkingLevelCommand => "cycle_thinking_level",
        RpcGetAvailableThinkingLevelsCommand => "get_available_thinking_levels",
        RpcSetSteeringModeCommand => "set_steering_mode",
        RpcSetFollowUpModeCommand => "set_follow_up_mode",
        RpcCompactCommand => "compact",
        RpcSetAutoCompactionCommand => "set_auto_compaction",
        RpcSetAutoRetryCommand => "set_auto_retry",
        RpcAbortRetryCommand => "abort_retry",
        RpcBashCommand => "bash",
        RpcAbortBashCommand => "abort_bash",
        RpcGetSessionStatsCommand => "get_session_stats",
        RpcExportHtmlCommand => "export_html",
        RpcSwitchSessionCommand => "switch_session",
        RpcForkCommand => "fork",
        RpcCloneCommand => "clone",
        RpcGetForkMessagesCommand => "get_fork_messages",
        RpcGetEntriesCommand => "get_entries",
        RpcGetTreeCommand => "get_tree",
        RpcGetLastAssistantTextCommand => "get_last_assistant_text",
        RpcSetSessionNameCommand => "set_session_name",
        RpcGetMessagesCommand => "get_messages",
        RpcGetCommandsCommand => "get_commands",
        _ => command.GetType().Name,
    };

    private static bool IsStdinWritable(Process process)
    {
        try
        {
            return process.StandardInput.BaseStream.CanWrite;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Exception Wrap(Exception error)
        => error is InvalidOperationException or TimeoutException ? error : new InvalidOperationException(error.Message, error);

    private static void TryKill(Process process)    {
        try
        {
            // TS 先 SIGTERM、1s 后 SIGKILL；.NET 无 SIGTERM API，Kill 即终止
            // （SIGKILL 语义）。整树杀掉以对齐 TS 对 node 子树的处理。
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 进程已退出时 Kill 抛错：忽略。
        }
    }
}

// ============================================================================
// 响应负载形状（TS 的匿名 data 对象的具名化）
// ============================================================================

/// <summary><c>{ disposition }</c>：prompt / steer / follow_up 的处置。</summary>
public sealed record RpcDispositionData
{
    public required string Disposition { get; init; }
}

/// <summary><c>{ steering, followUp }</c>：clear_queue 的清空结果。</summary>
public sealed record RpcClearedQueueData
{
    public IReadOnlyList<string> Steering { get; init; } = [];

    public IReadOnlyList<string> FollowUp { get; init; } = [];
}

/// <summary><c>{ cancelled }</c>：new_session / switch_session / clone 的结果。</summary>
public sealed record RpcCancelledData
{
    public required bool Cancelled { get; init; }
}

/// <summary><c>{ path }</c>：export_html 的结果。</summary>
public sealed record RpcPathData
{
    public required string Path { get; init; }
}

/// <summary><c>{ text, cancelled }</c>：fork 的结果。</summary>
public sealed record RpcForkData
{
    public required string Text { get; init; }

    public required bool Cancelled { get; init; }
}

/// <summary><c>{ messages }</c>：get_fork_messages 的结果。</summary>
public sealed record RpcForkMessagesData
{
    public IReadOnlyList<UserMessageForForking> Messages { get; init; } = [];
}

/// <summary><c>{ entries, leafId }</c>：get_entries 的结果。</summary>
public sealed record RpcEntriesData
{
    public IReadOnlyList<SessionEntry> Entries { get; init; } = [];

    public string? LeafId { get; init; }
}

/// <summary><c>{ tree, leafId }</c>：get_tree 的结果。</summary>
public sealed record RpcTreeData
{
    public IReadOnlyList<SessionTreeNode> Tree { get; init; } = [];

    public string? LeafId { get; init; }
}

/// <summary><c>{ text }</c>：get_last_assistant_text 的结果。</summary>
public sealed record RpcTextData
{
    public string? Text { get; init; }
}

/// <summary><c>{ messages }</c>：get_messages 的结果。</summary>
public sealed record RpcMessagesData
{
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
}

/// <summary><c>{ commands }</c>：get_commands 的结果。</summary>
public sealed record RpcCommandsData
{
    public IReadOnlyList<RpcSlashCommand> Commands { get; init; } = [];
}

/// <summary><c>{ models }</c>：get_available_models 的结果。</summary>
public sealed record RpcModelsData
{
    public IReadOnlyList<ModelInfo> Models { get; init; } = [];
}

/// <summary><c>{ levels }</c>：get_available_thinking_levels 的结果。</summary>
public sealed record RpcLevelsData
{
    public IReadOnlyList<ThinkingLevel> Levels { get; init; } = [];
}

/// <summary><c>{ level }</c>：cycle_thinking_level 的结果（无结果时为 null）。</summary>
public sealed record RpcLevelData
{
    public required ThinkingLevel Level { get; init; }
}
