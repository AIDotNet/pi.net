using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Types;

namespace Pi.CodingAgent.Core;

/// <summary>
/// 工具运行期间发起的嵌套工具调用（例如 codemode 脚本里的 <c>ctx.executeTool()</c>）。
/// 对应 TS <c>core/nested-tool-calls.ts</c>。
/// </summary>
public static class NestedCallLimits
{
    public const int MaxCalls = 256;

    public const int MaxArgumentBytesPerCall = 8 * 1024;

    public const int MaxArgumentBytesTotal = 32 * 1024;

    public const int MaxErrorChars = 500;
}

/// <summary>一个模型发出的工具调用所留下的嵌套记录。对应 TS <c>NestedCallSummary</c>。</summary>
public sealed record NestedCallSummary
{
    /// <summary>成为 <c>nestedCalls</c>。没有任何嵌套调用时为 null。</summary>
    public NestedToolCalls? Calls { get; init; }

    /// <summary>嵌套结果的用量合计，加到消息的 <c>usage</c>。</summary>
    public Usage? Usage { get; init; }
}

/// <summary>
/// 收集一个模型发出的工具调用的全部嵌套调用（含嵌套工具自己再调的）。
/// 快照成为工具结果消息上的 <c>nestedCalls</c>。对应 TS <c>NestedCallRecorder</c>。
/// </summary>
public sealed class NestedCallRecorder
{
    private readonly List<NestedToolCallRecord> _calls = [];
    private readonly Dictionary<NestedToolCallRecord, long> _startedAt = new(ReferenceEqualityComparer.Instance);
    private bool _complete = true;
    private int _argumentBytes;
    private Usage? _usage;

    /// <summary>调用开始时记录。超出条数上限则丢弃并返回 null。</summary>
    public NestedToolCallRecord? Start(ToolCallContent toolCall)
    {
        if (_calls.Count >= NestedCallLimits.MaxCalls)
        {
            _complete = false;
            return null;
        }

        var record = new NestedToolCallRecord
        {
            Id = toolCall.Id,
            Name = toolCall.Name,
            Status = "unfinished",
        };
        var json = EncodeArguments(toolCall.Arguments);
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > NestedCallLimits.MaxArgumentBytesPerCall
            || _argumentBytes + bytes > NestedCallLimits.MaxArgumentBytesTotal)
        {
            record.ArgumentsBytes = bytes;
            _complete = false;
        }
        else
        {
            record.Arguments = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            _argumentBytes += bytes;
        }

        _calls.Add(record);
        _startedAt[record] = Stopwatch.GetTimestamp();
        return record;
    }

    public void Finish(NestedToolCallRecord? record, bool isError, string errorText)
    {
        if (record is null)
        {
            return;
        }

        record.Status = isError ? "error" : "ok";
        var started = _startedAt.TryGetValue(record, out var ts) ? ts : Stopwatch.GetTimestamp();
        record.DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _startedAt.Remove(record);
        if (isError && errorText.Length > 0)
        {
            record.Error = errorText.Length <= NestedCallLimits.MaxErrorChars
                ? errorText
                : errorText[..NestedCallLimits.MaxErrorChars];
        }
    }

    public void AddUsage(Usage usage)
        => _usage = _usage is null ? usage : UsageTotalsApi.CombineUsage(_usage, usage);

    public Usage? TotalUsage => _usage;

    /// <summary>当前记录的副本；没有任何嵌套调用且完整时为 null。</summary>
    public NestedToolCalls? Snapshot()
    {
        if (_calls.Count == 0 && _complete)
        {
            return null;
        }

        var calls = _calls.Select(call => call with { }).ToList();
        return new NestedToolCalls
        {
            Calls = calls,
            Complete = _complete && calls.TrueForAll(call => call.Status != "unfinished"),
        };
    }

    private static string EncodeArguments(object? arguments)
    {
        JsonNode node = arguments switch
        {
            null => new JsonObject(),
            JsonObject obj => obj,
            JsonNode json => json,
            _ => JsonSerializer.SerializeToNode(arguments) ?? new JsonObject(),
        };
        return node.ToJsonString();
    }
}

/// <summary>对应 TS <c>NestedToolCallOptions</c>。</summary>
public sealed record NestedToolCallOptions
{
    /// <summary>默认沿用调用方工具的取消令牌。</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>嵌套工具的部分结果，除 <c>tool_execution_update</c> 事件外再回调一次。</summary>
    public AgentToolUpdateCallback? OnUpdate { get; init; }
}

/// <summary>嵌套调用的 <c>tool_execution_*</c> 事件。对应 TS <c>NestedToolExecutionEvent</c>。</summary>
public abstract record NestedToolExecutionEvent
{
    private NestedToolExecutionEvent() { }

    public sealed record Start(string ToolCallId, string ToolName, object? Args, string ParentToolCallId)
        : NestedToolExecutionEvent;

    public sealed record Update(
        string ToolCallId,
        string ToolName,
        object? Args,
        AgentToolResult PartialResult,
        string ParentToolCallId) : NestedToolExecutionEvent;

    public sealed record End(
        string ToolCallId,
        string ToolName,
        AgentToolResult Result,
        bool IsError,
        string ParentToolCallId,
        long? DurationMs = null) : NestedToolExecutionEvent;
}

/// <summary>嵌套调用宿主。对应 TS <c>NestedToolCallHost</c>。</summary>
public interface INestedToolCallHost
{
    IReadOnlyList<AgentTool> GetTools();

    /// <summary>是否像 agent 顺序执行工具那样，每个嵌套调用都独占。</summary>
    bool IsSequential();

    Task<AgentToolCallOutcome> RunToolCallAsync(
        ToolCallContent toolCall,
        string parentToolCallId,
        CancellationToken cancellationToken,
        Func<AgentToolResult, Task> onUpdate);

    Task EmitAsync(NestedToolExecutionEvent @event);
}

/// <summary>代表模型发出的一次调用去跑嵌套工具。对应 TS <c>NestedToolCallRunner</c>。</summary>
public sealed class NestedToolCallRunner
{
    /// <summary>同一模型发出的调用共享一份 recorder。对应 TS <c>CallScope</c>。</summary>
    private sealed class CallScope
    {
        public required NestedCallRecorder Recorder { get; init; }

        public int NextId { get; set; }

        /// <summary>持有独占队列的调用，其自身的嵌套调用不再排队等待自己。</summary>
        public bool HoldsQueue { get; init; }
    }

    private readonly INestedToolCallHost _host;
    private readonly object _gate = new();
    private readonly Dictionary<string, CallScope> _scopes = new(StringComparer.Ordinal);
    private Task _queueTail = Task.CompletedTask;

    public NestedToolCallRunner(INestedToolCallHost host) => _host = host;

    /// <summary>
    /// 代表 <paramref name="callerId"/> 去跑 <paramref name="name"/>。嵌套 id 为 <c>callerId/n</c>。
    /// 工具失败不抛：以 <c>IsError: true</c> 返回。对应 TS <c>execute</c>。
    /// </summary>
    public async Task<AgentToolCallOutcome> ExecuteAsync(
        string callerId,
        string name,
        object? args,
        NestedToolCallOptions? options = null)
    {
        options ??= new NestedToolCallOptions();
        CallScope scope;
        ToolCallContent toolCall;
        NestedToolCallRecord? record;
        lock (_gate)
        {
            if (!_scopes.TryGetValue(callerId, out scope!))
            {
                scope = new CallScope { Recorder = new NestedCallRecorder(), NextId = 1, HoldsQueue = false };
                _scopes[callerId] = scope;
            }

            toolCall = new ToolCallContent($"{callerId}/{scope.NextId++}", name, args ?? new JsonObject());
            record = scope.Recorder.Start(toolCall);
        }

        await _host.EmitAsync(new NestedToolExecutionEvent.Start(toolCall.Id, name, toolCall.Arguments, callerId))
            .ConfigureAwait(false);

        var exclusive = false;
        lock (_gate)
        {
            exclusive = !scope.HoldsQueue
                && (_host.IsSequential()
                    || _host.GetTools().FirstOrDefault(tool => tool.Name == name)?.ExecutionMode
                        == ToolExecutionMode.Sequential);
        }

        TaskCompletionSource? release = null;
        if (exclusive)
        {
            Task previous;
            lock (_gate)
            {
                previous = _queueTail;
                release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _queueTail = release.Task;
            }

            await previous.ConfigureAwait(false);
        }

        lock (_gate)
        {
            _scopes[toolCall.Id] = new CallScope
            {
                Recorder = scope.Recorder,
                NextId = 1,
                HoldsQueue = scope.HoldsQueue || exclusive,
            };
        }

        AgentToolCallOutcome outcome;
        try
        {
            outcome = await _host.RunToolCallAsync(
                    toolCall,
                    callerId,
                    options.CancellationToken,
                    async partialResult =>
                    {
                        if (options.OnUpdate is not null)
                        {
                            await options.OnUpdate(partialResult).ConfigureAwait(false);
                        }

                        await _host.EmitAsync(new NestedToolExecutionEvent.Update(
                                toolCall.Id,
                                name,
                                toolCall.Arguments,
                                partialResult,
                                callerId))
                            .ConfigureAwait(false);
                    })
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _scopes.Remove(toolCall.Id);
            }

            release?.TrySetResult();
        }

        scope.Recorder.Finish(record, outcome.IsError, TextOf(outcome.Result));
        if (outcome.Result.UsageStats is not null)
        {
            scope.Recorder.AddUsage(outcome.Result.UsageStats);
        }

        await _host.EmitAsync(new NestedToolExecutionEvent.End(
                toolCall.Id,
                name,
                outcome.Result,
                outcome.IsError,
                callerId,
                outcome.DurationMs))
            .ConfigureAwait(false);
        return outcome;
    }

    /// <summary>取出并移除一次模型发出的调用所留下的嵌套记录。对应 TS <c>takeRecord</c>。</summary>
    public NestedCallSummary? TakeRecord(string toolCallId)
    {
        CallScope? scope;
        lock (_gate)
        {
            _scopes.TryGetValue(toolCallId, out scope);
            _scopes.Remove(toolCallId);
        }

        if (scope is null)
        {
            return null;
        }

        return new NestedCallSummary { Calls = scope.Recorder.Snapshot(), Usage = scope.Recorder.TotalUsage };
    }

    public void Clear()
    {
        lock (_gate)
        {
            _scopes.Clear();
        }
    }

    private static string TextOf(AgentToolResult result)
        => string.Join(
            "\n",
            result.Content.OfType<TextContent>().Select(block => block.Text));
}
