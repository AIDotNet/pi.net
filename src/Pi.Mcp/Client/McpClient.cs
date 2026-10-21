using System.Text.Json.Nodes;
using Pi.Mcp.Protocol;
using Pi.Mcp.Transports;

namespace Pi.Mcp;

/// <summary>
/// MCP 客户端会话。对应 TS <c>client.ts</c>：initialize 握手（协议版本协商）、
/// 请求/通知收发（超时 + 中止 + 进度）、分页列举、工具调用、服务器请求处理
/// （ping/roots/list）与优雅关闭。
/// </summary>
public sealed class McpClient
{
    private const long DefaultRequestTimeoutMs = 30_000;
    private const int MaxListPages = 1_000;

    private readonly McpClientOptions _options;
    private readonly object _lock = new();
    private readonly Dictionary<JsonRpcId, PendingRequest> _pending = [];
    private readonly Dictionary<JsonRpcId, JsonRpcId> _progressRequests = [];
    private readonly Dictionary<JsonRpcId, CancellationTokenSource> _incoming = [];
    private readonly Dictionary<string, Func<JsonNode?, CancellationToken, Task<JsonNode?>>> _requestHandlers = [];
    private readonly Dictionary<string, List<Func<JsonNode?, Task>>> _notificationListeners = [];
    private readonly List<Action<Exception>> _errorListeners = [];
    private readonly List<Action> _closeListeners = [];
    private readonly List<IDisposable> _disposers = [];

    private ClientState _state = ClientState.Idle;
    private IMcpTransport? _transport;
    private long _nextRequestId = 1;
    private InitializeResult? _initializeResult;

    /// <param name="options">客户端选项（名称/版本必填，对应 TS <c>McpClientOptions</c>）。</param>
    public McpClient(McpClientOptions options)
    {
        _options = options;
        _requestHandlers["ping"] = (_, _) => Task.FromResult<JsonNode?>(new JsonObject());
        if (options.Roots is not null)
        {
            _requestHandlers["roots/list"] = (_, _) =>
                Task.FromResult<JsonNode?>(new JsonObject
                {
                    ["roots"] = new JsonArray(options.Roots.Select(root =>
                        (JsonNode)new JsonObject { ["uri"] = root.Uri, ["name"] = root.Name }).ToArray()),
                });
        }
    }

    // ---------- 连接状态与协商结果 ----------

    /// <summary>连接状态。</summary>
    public ClientState State => _state;

    /// <summary>握手返回的服务器信息。</summary>
    public McpImplementation? ServerInfo => _initializeResult?.ServerInfo;

    /// <summary>握手返回的服务器能力。</summary>
    public ServerCapabilities? ServerCapabilities => _initializeResult?.Capabilities;

    /// <summary>服务器附带的使用说明。</summary>
    public string? Instructions => _initializeResult?.Instructions;

    /// <summary>协商出的协议版本。</summary>
    public string? ProtocolVersion => _initializeResult?.ProtocolVersion;

    // ---------- 握手与关闭 ----------

    /// <summary>连接传输并完成 initialize 握手。对应 TS <c>connect()</c>。</summary>
    public async Task<InitializeResult> ConnectAsync(IMcpTransport transport, CancellationToken cancellationToken = default)
    {
        if (_state != ClientState.Idle)
            throw new InvalidOperationException($"Cannot connect MCP client in {_state} state");
        _state = ClientState.Connecting;
        _transport = transport;
        _disposers.Add(transport.OnMessage(HandleMessage));
        // 传输错误仅报告；挂起请求在连接关闭时统一失败。
        _disposers.Add(transport.OnError(error => EmitError(error)));
        _disposers.Add(transport.OnClose(HandleTransportClose));

        try
        {
            await transport.StartAsync(cancellationToken).ConfigureAwait(false);
            var capabilities = _options.Capabilities?.Raw?.DeepClone() as JsonObject ?? new JsonObject();
            if (_options.Roots is not null && !capabilities.ContainsKey("roots"))
                capabilities["roots"] = new JsonObject();

            var initializeParams = new JsonObject
            {
                ["protocolVersion"] = _options.ProtocolVersion ?? McpProtocolVersions.Latest,
                ["capabilities"] = capabilities,
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = _options.Name,
                    ["version"] = _options.Version,
                },
            };
            var raw = await RequestInternalAsync(
                "initialize", initializeParams, McpRequestOptions.None, allowConnecting: true,
                cancellationToken).ConfigureAwait(false);
            var result = McpProtocol.ValidateInitializeResult(
                raw as JsonObject ?? throw new McpError(JsonRpcErrorCodes.InvalidRequest, "Invalid MCP initialize result"));

            if (!McpProtocolVersions.IsSupported(result.ProtocolVersion))
                throw new InvalidOperationException(
                    $"MCP server selected unsupported protocol version {result.ProtocolVersion}");

            _initializeResult = result;
            transport.SetProtocolVersion(result.ProtocolVersion);
            await NotifyInternalAsync("notifications/initialized", null, allowConnecting: true, cancellationToken).ConfigureAwait(false);
            _state = ClientState.Connected;
            return result;
        }
        catch (Exception)
        {
            try { await CloseAsync().ConfigureAwait(false); } catch { /* 尽力而为 */ }
            throw;
        }
    }

    /// <summary>关闭连接：拒绝全部挂起请求并关闭传输。对应 TS <c>close()</c>。</summary>
    public async Task CloseAsync()
    {
        var transport = _transport;
        _transport = null;
        DisposeTransportListeners();
        MarkClosed(new McpConnectionClosedError());
        if (transport is not null) await transport.CloseAsync().ConfigureAwait(false);
    }

    // ---------- 请求 / 通知 ----------

    /// <summary>发起一次 JSON-RPC 请求并等待结果。</summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null,
        McpRequestOptions? options = null, CancellationToken cancellationToken = default)
        => await RequestInternalAsync(method, parameters, options ?? McpRequestOptions.None,
            allowConnecting: false, cancellationToken).ConfigureAwait(false);

    /// <summary>发送一条通知（无 id，无响应）。</summary>
    public Task NotifyAsync(string method, JsonObject? parameters = null, CancellationToken cancellationToken = default)
        => NotifyInternalAsync(method, parameters, allowConnecting: false, cancellationToken);

    /// <summary>ping 服务器。</summary>
    public async Task PingAsync(McpRequestOptions? options = null, CancellationToken cancellationToken = default)
        => await RequestAsync("ping", null, options, cancellationToken).ConfigureAwait(false);

    /// <summary>注册服务器请求处理器（如 ping / roots/list），返回取消注册委托。</summary>
    public IDisposable SetRequestHandler(
        string method, Func<JsonNode?, CancellationToken, Task<JsonNode?>> handler)
    {
        _requestHandlers[method] = handler;
        return new Unsubscriber(() =>
        {
            if (_requestHandlers.TryGetValue(method, out var registered) && registered == handler)
                _requestHandlers.Remove(method);
        });
    }

    /// <summary>订阅服务器通知，返回取消订阅委托。</summary>
    public IDisposable OnNotification(string method, Func<JsonNode?, Task> listener)
    {
        if (!_notificationListeners.TryGetValue(method, out var listeners))
        {
            listeners = [];
            _notificationListeners[method] = listeners;
        }
        listeners.Add(listener);
        return new Unsubscriber(() =>
        {
            listeners.Remove(listener);
            if (listeners.Count == 0) _notificationListeners.Remove(method);
        });
    }

    /// <summary>订阅传输层错误。</summary>
    public IDisposable OnError(Action<Exception> listener)
    {
        _errorListeners.Add(listener);
        return new Unsubscriber(() => _errorListeners.Remove(listener));
    }

    /// <summary>订阅连接关闭（无论传输掉线还是主动 Close）。</summary>
    public IDisposable OnClose(Action listener)
    {
        _closeListeners.Add(listener);
        return new Unsubscriber(() => _closeListeners.Remove(listener));
    }

    // ---------- 高层封装（分页列举与工具调用） ----------

    /// <summary>列举全部工具（跟随 nextCursor 翻页）。</summary>
    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(McpRequestOptions? options = null,
        CancellationToken cancellationToken = default)
        => (await ListAllAsync("tools/list", "tools", McpProtocol.IsTool, options, cancellationToken)
                .ConfigureAwait(false))
            .Select(McpProtocol.ToTool)
            .ToList();

    /// <summary>调用一个工具。对应 TS <c>callTool</c>。</summary>
    public async Task<CallToolResult> CallToolAsync(string name, JsonObject? arguments = null,
        McpRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject { ["name"] = name };
        if (arguments is not null) parameters["arguments"] = arguments.DeepClone();
        var result = await RequestAsync("tools/call", parameters, options, cancellationToken).ConfigureAwait(false);
        return McpProtocol.ValidateCallToolResult(
            result as JsonObject ?? throw new McpError(JsonRpcErrorCodes.InvalidRequest, "Invalid MCP tools/call result"));
    }

    /// <summary>列举全部资源（跟随 nextCursor 翻页）。对应 TS <c>listResources</c>。</summary>
    public async Task<IReadOnlyList<McpResource>> ListResourcesAsync(McpRequestOptions? options = null,
        CancellationToken cancellationToken = default)
        => (await ListAllAsync("resources/list", "resources", McpProtocol.IsResource, options, cancellationToken)
                .ConfigureAwait(false))
            .Select(McpProtocol.ToResource)
            .ToList();

    /// <summary>列举一页资源（从 <paramref name="cursor"/> 起）。对应 TS <c>listResourcesPage</c>。</summary>
    public async Task<ListResourcesResult> ListResourcesPageAsync(string? cursor = null,
        McpRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var page = await ListPageAsync("resources/list", "resources", McpProtocol.IsResource, cursor, options,
            cancellationToken).ConfigureAwait(false);
        return new ListResourcesResult(page.Items.Select(McpProtocol.ToResource).ToList(), page.NextCursor);
    }

    /// <summary>列举全部资源模板（跟随 nextCursor 翻页）。对应 TS <c>listResourceTemplates</c>。</summary>
    public async Task<IReadOnlyList<McpResourceTemplate>> ListResourceTemplatesAsync(McpRequestOptions? options = null,
        CancellationToken cancellationToken = default)
        => (await ListAllAsync("resources/templates/list", "resourceTemplates", McpProtocol.IsResourceTemplate,
                options, cancellationToken).ConfigureAwait(false))
            .Select(McpProtocol.ToResourceTemplate)
            .ToList();

    /// <summary>列举一页资源模板（从 <paramref name="cursor"/> 起）。对应 TS <c>listResourceTemplatesPage</c>。</summary>
    public async Task<ListResourceTemplatesResult> ListResourceTemplatesPageAsync(string? cursor = null,
        McpRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var page = await ListPageAsync("resources/templates/list", "resourceTemplates",
            McpProtocol.IsResourceTemplate, cursor, options, cancellationToken).ConfigureAwait(false);
        return new ListResourceTemplatesResult(
            page.Items.Select(McpProtocol.ToResourceTemplate).ToList(), page.NextCursor);
    }

    /// <summary>读取一个资源。</summary>
    public async Task<ReadResourceResult> ReadResourceAsync(string uri,
        McpRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("resources/read", new JsonObject { ["uri"] = uri }, options,
            cancellationToken).ConfigureAwait(false);
        return McpProtocol.ValidateReadResourceResult(
            result as JsonObject ?? throw new McpError(JsonRpcErrorCodes.InvalidRequest, "Invalid MCP resources/read result"));
    }

    /// <summary>列举一页（不翻页）。对应 TS <c>listPage</c>。</summary>
    private async Task<(IReadOnlyList<JsonObject> Items, string? NextCursor)> ListPageAsync(
        string method, string key, Func<JsonObject, bool> isItem, string? cursor,
        McpRequestOptions? options, CancellationToken cancellationToken)
    {
        var parameters = cursor is null ? null : new JsonObject { ["cursor"] = cursor };
        var value = await RequestAsync(method, parameters, options, cancellationToken).ConfigureAwait(false);
        return McpProtocol.ValidateListPage(method, key,
            value as JsonObject ?? throw new McpError(JsonRpcErrorCodes.InvalidRequest, $"Invalid MCP {method} result"),
            isItem);
    }

    private async Task<List<JsonObject>> ListAllAsync(string method, string key,
        Func<JsonObject, bool> isItem, McpRequestOptions? options, CancellationToken cancellationToken)
    {
        var items = new List<JsonObject>();
        var seenCursors = new HashSet<string>();
        string? cursor = null;
        for (var pageNumber = 0; pageNumber < MaxListPages; pageNumber++)
        {
            var parameters = cursor is null ? null : new JsonObject { ["cursor"] = cursor };
            var value = await RequestAsync(method, parameters, options, cancellationToken).ConfigureAwait(false);
            var (pageItems, nextCursor) = McpProtocol.ValidateListPage(method, key,
                value as JsonObject ?? throw new McpError(JsonRpcErrorCodes.InvalidRequest, $"Invalid MCP {method} result"),
                isItem);
            items.AddRange(pageItems);
            if (nextCursor is null) return items;
            if (!seenCursors.Add(nextCursor))
                throw new InvalidOperationException($"MCP {method} returned duplicate cursor: {nextCursor}");
            cursor = nextCursor;
        }
        throw new InvalidOperationException($"MCP {method} exceeded {MaxListPages} pages");
    }

    // ---------- 请求内部机制 ----------

    private async Task<JsonNode?> RequestInternalAsync(
        string method,
        JsonObject? parameters,
        McpRequestOptions options,
        bool allowConnecting,
        CancellationToken cancellationToken)
    {
        var transport = RequireTransport(allowConnecting);
        if (_state != ClientState.Connected && !allowConnecting)
            throw new InvalidOperationException($"Cannot send MCP request in {_state} state");
        if (cancellationToken.IsCancellationRequested)
            throw new McpAbortError();

        var timeoutMs = options.TimeoutMs ?? _options.RequestTimeoutMs ?? DefaultRequestTimeoutMs;
        var cancellable = method != "initialize";

        JsonRpcId id;
        PendingRequest pending;
        lock (_lock)
        {
            id = JsonRpcId.FromNumber(_nextRequestId++);
            pending = new PendingRequest
            {
                TimeoutMs = timeoutMs,
                OnProgress = options.OnProgress,
                Cancellable = cancellable,
            };
            if (options.OnProgress is not null)
            {
                // TS 以请求 id 同时充当 progressToken。
                pending.ProgressToken = id;
                _progressRequests[id] = id;
            }
            _pending[id] = pending;
        }

        var requestParams = parameters;
        if (pending.ProgressToken is { } token)
        {
            var withMeta = parameters?.DeepClone() as JsonObject ?? new JsonObject();
            var meta = withMeta["_meta"] as JsonObject ?? new JsonObject();
            withMeta["_meta"] = meta;
            meta["progressToken"] = token.ToJson();
            requestParams = withMeta;
        }

        using var timeoutSource = timeoutMs > 0 ? new CancellationTokenSource() : null;
        using var waitSource = timeoutSource is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        if (timeoutSource is not null)
        {
            pending.TimeoutSource = timeoutSource;
            timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        }

        try
        {
            await transport.SendAsync(new JsonRpcMessage.Request(id, method, requestParams), cancellationToken)
                .ConfigureAwait(false);

            var cancelled = Task.Delay(Timeout.Infinite, waitSource.Token);
            var completed = await Task.WhenAny(pending.Completion.Task, cancelled).ConfigureAwait(false);
            if (completed == pending.Completion.Task)
                return await pending.Completion.Task.ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                RemovePending(id, pending);
                NotifyCancelled(id, cancellable, "Aborted");
                throw new McpAbortError();
            }

            RemovePending(id, pending);
            NotifyCancelled(id, cancellable, "Request timed out");
            throw new McpTimeoutError(timeoutMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 传输层在等待阶段之前就观察到了调用方取消。
            RemovePending(id, pending);
            NotifyCancelled(id, cancellable, "Aborted");
            throw new McpAbortError();
        }
        finally
        {
            RemovePending(id, pending);
        }
    }

    /// <summary>发送 <c>notifications/cancelled</c>（仅可取消的方法）；失败只报告。</summary>
    private void NotifyCancelled(JsonRpcId id, bool notifyServer, string? reason)
    {
        if (!notifyServer || _transport is not { } transport) return;
        var parameters = new JsonObject { ["requestId"] = id.ToJson() };
        if (!string.IsNullOrEmpty(reason)) parameters["reason"] = reason;
        _ = SendCancellationAsync(transport, parameters);
    }

    private async Task SendCancellationAsync(IMcpTransport transport, JsonObject parameters)
    {
        try
        {
            await transport.SendAsync(
                new JsonRpcMessage.Notification("notifications/cancelled", parameters),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            EmitError(error);
        }
    }

    private Task NotifyInternalAsync(string method, JsonObject? parameters, bool allowConnecting,
        CancellationToken cancellationToken)
    {
        var transport = RequireTransport(allowConnecting);
        return transport.SendAsync(new JsonRpcMessage.Notification(method, parameters), cancellationToken);
    }

    private IMcpTransport RequireTransport(bool allowConnecting)
    {
        if (_transport is not null && (_state == ClientState.Connected || allowConnecting))
            return _transport;
        throw new McpConnectionClosedError("MCP client is not connected");
    }

    /// <summary>分发入站消息：响应 / 服务器请求 / 通知。对应 TS <c>handleMessage</c>。</summary>
    private void HandleMessage(JsonRpcMessage message)
    {
        switch (message)
        {
            case JsonRpcMessage.SuccessResponse success:
            {
                PendingRequest? pending;
                lock (_lock) _pending.TryGetValue(success.Id, out pending);
                if (pending is null)
                {
                    EmitError(new InvalidOperationException(
                        $"Received response for unknown MCP request {success.Id}"));
                    break;
                }

                RemovePending(success.Id, pending);
                pending.Completion.TrySetResult(success.Result);
                break;
            }

            case JsonRpcMessage.ErrorResponse error:
            {
                PendingRequest? pending;
                lock (_lock) _pending.TryGetValue(error.Id, out pending);
                if (pending is null)
                {
                    EmitError(new InvalidOperationException(
                        $"Received response for unknown MCP request {error.Id}"));
                    break;
                }

                RemovePending(error.Id, pending);
                pending.Completion.TrySetException(
                    new McpError(error.Error.Code, error.Error.Message, error.Error.Data));
                break;
            }

            case JsonRpcMessage.Request serverRequest:
            {
                // 服务器发起的请求：分发到已注册处理器并回发响应；未知方法回 method-not-found。
                var cancellation = new CancellationTokenSource();
                lock (_lock) _incoming[serverRequest.Id] = cancellation;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!_requestHandlers.TryGetValue(serverRequest.Method, out var handler))
                            throw new McpError(JsonRpcErrorCodes.MethodNotFound,
                                $"No handler for {serverRequest.Method}");
                        var result = await handler(serverRequest.Params, cancellation.Token).ConfigureAwait(false);
                        var transport = _transport;
                        if (transport is not null)
                            await transport.SendAsync(new JsonRpcMessage.SuccessResponse(serverRequest.Id, result))
                                .ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        var code = error is McpError mcpError ? mcpError.Code : JsonRpcErrorCodes.InternalError;
                        var data = error is McpError m ? m.ErrorData : null;
                        var transport = _transport;
                        if (transport is not null)
                            await transport.SendAsync(new JsonRpcMessage.ErrorResponse(serverRequest.Id,
                                new JsonRpcErrorObject(code, error.Message, data))).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_lock)
                        {
                            if (_incoming.TryGetValue(serverRequest.Id, out var current)
                                && ReferenceEquals(current, cancellation))
                            {
                                _incoming.Remove(serverRequest.Id);
                            }
                        }

                        cancellation.Dispose();
                    }
                });
                break;
            }

            case JsonRpcMessage.Notification notification:
            {
                if (notification.Method == "notifications/progress") HandleProgress(notification.Params);
                else if (notification.Method == "notifications/cancelled") HandleCancelled(notification.Params);
                if (_notificationListeners.TryGetValue(notification.Method, out var listeners))
                    foreach (var listener in listeners.ToList())
                        _ = Task.Run(() => listener(notification.Params));
                break;
            }
        }
    }

    /// <summary><c>notifications/progress</c>：按 token 找回请求、重置超时并回调。对应 TS <c>handleProgress</c>。</summary>
    private void HandleProgress(JsonNode? parameters)
    {
        if (parameters is not JsonObject payload
            || !payload.TryGetPropertyValue("progressToken", out var tokenNode)
            || !JsonRpcId.TryParse(tokenNode, out var token)
            || payload["progress"] is not JsonValue progressValue
            || !progressValue.TryGetValue<double>(out var progress))
        {
            return;
        }

        PendingRequest? pending;
        lock (_lock)
        {
            if (!_progressRequests.TryGetValue(token, out var requestId)
                || !_pending.TryGetValue(requestId, out pending))
            {
                return;
            }
        }

        pending.RearmTimeout();
        double? total = payload["total"] is JsonValue totalValue
            && totalValue.TryGetValue<double>(out var parsedTotal)
                ? parsedTotal
                : null;
        var message = payload["message"] is JsonValue messageValue
            && messageValue.TryGetValue<string>(out var parsedMessage)
                ? parsedMessage
                : null;
        if (pending.OnProgress is { } onProgress)
        {
            _ = InvokeProgressAsync(
                onProgress,
                new McpProgressNotification(token, progress, total, message));
        }
    }

    private async Task InvokeProgressAsync(
        Func<McpProgressNotification, Task> onProgress,
        McpProgressNotification progress)
    {
        try
        {
            await onProgress(progress).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            EmitError(error);
        }
    }

    /// <summary><c>notifications/cancelled</c>：中止正在处理的服务器请求。对应 TS <c>handleCancelled</c>。</summary>
    private void HandleCancelled(JsonNode? parameters)
    {
        if (parameters is not JsonObject payload
            || !payload.TryGetPropertyValue("requestId", out var idNode)
            || !JsonRpcId.TryParse(idNode, out var id))
        {
            return;
        }

        CancellationTokenSource? incoming;
        lock (_lock) _incoming.TryGetValue(id, out incoming);
        try
        {
            incoming?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 处理器已结束并释放。
        }
    }

    /// <summary>移除挂起请求及其进度 token；重复调用安全。</summary>
    private bool RemovePending(JsonRpcId id, PendingRequest pending)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(id, out var current) || !ReferenceEquals(current, pending))
                return false;
            _pending.Remove(id);
            if (pending.ProgressToken is { } token
                && _progressRequests.TryGetValue(token, out var requestId)
                && requestId == id)
            {
                _progressRequests.Remove(token);
            }
            return true;
        }
    }

    private void HandleTransportClose()
        => MarkClosed(new McpConnectionClosedError());

    /// <summary>关闭落定：拒绝全部挂起请求、中止在处理的服务器请求并触发 close 监听器（仅一次）。</summary>
    private void MarkClosed(McpConnectionClosedError reason)
    {
        List<PendingRequest> toReject;
        List<CancellationTokenSource> incoming;
        lock (_lock)
        {
            if (_state == ClientState.Closed) return;
            toReject = [.. _pending.Values];
            _pending.Clear();
            _progressRequests.Clear();
            incoming = [.. _incoming.Values];
            _incoming.Clear();
            _state = ClientState.Closed;
        }
        foreach (var pending in toReject)
            pending.Completion.TrySetException(reason);
        foreach (var cancellation in incoming)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 处理器已结束并释放。
            }
        }

        foreach (var listener in _closeListeners.ToList()) listener();
    }

    private void EmitError(Exception error)
    {
        foreach (var listener in _errorListeners.ToList()) listener(error);
    }

    private void DisposeTransportListeners()
    {
        foreach (var disposer in _disposers) disposer.Dispose();
        _disposers.Clear();
    }

    /// <summary>挂起请求的登记条目。</summary>
    private sealed class PendingRequest
    {
        public TaskCompletionSource<JsonNode?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long TimeoutMs { get; init; }

        public CancellationTokenSource? TimeoutSource { get; set; }

        public Func<McpProgressNotification, Task>? OnProgress { get; init; }

        public JsonRpcId? ProgressToken { get; set; }

        public bool Cancellable { get; init; }

        /// <summary>收到进度后重置超时；已取消/已释放时静默忽略。</summary>
        public void RearmTimeout()
        {
            var source = TimeoutSource;
            if (source is null || TimeoutMs <= 0) return;
            try
            {
                source.CancelAfter(TimeSpan.FromMilliseconds(TimeoutMs));
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}

/// <summary>客户端连接状态。对应 TS <c>ClientState</c>。</summary>
public enum ClientState
{
    Idle,
    Connecting,
    Connected,
    Closed,
}
