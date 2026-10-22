using System.Text.Json.Serialization;
using Pi.Agent.Types;
using Pi.Ai.Types;

namespace Pi.CodingAgent.Core;

// ============================================================================
// AgentSession 事件族 — TS `core/agent-session.ts:191` 的 `AgentSessionEvent` 联合。
// ============================================================================
//
// TS 的形状是「核心 AgentEvent（除 agent_end）加可选 parentToolCallId」∪ 会话自有事件。
// C# 没有结构类型，无法让 Pi.Agent 的事件记录凭空多出一个可选字段，因此这里**重新声明**
// 整个联合——形状逐字段对照 TS；`_handleAgentEvent`（9h-3）负责把 Pi.Agent 事件映射过来。
//
// 差异 C147：TS 用 `WithParentToolCallId<E>` 条件类型给 tool_execution_* 追加可选
// `parentToolCallId`。C# 端口在三个 tool_execution_* 记录上直接加可空
// `ParentToolCallId` 属性；其余核心事件无此字段，与 TS 的分发语义一致。

/// <summary>
/// 会话级事件。对应 TS <c>AgentSessionEvent</c> 联合（核心 AgentEvent 形状 + 会话自有事件）。
/// 判别字符串与 TS 的 <c>type</c> 逐字一致。
/// </summary>
/// <remarks>
/// 多态序列化（4f-5：RPC/print/json 线路需要把事件写成 JSON 行）：
/// 判别串由 <c>[JsonPolymorphic]</c> 元数据承载，与各变体的
/// <see cref="Type"/> 常量一一对应；因此 <see cref="Type"/> 本身必须
/// <c>[JsonIgnore]</c>——STJ 不允许判别式属性名与普通属性重名
/// （见 <c>JsonSerializer</c> 的冲突检查）。
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AgentSessionEvent.AgentStartEvent), "agent_start")]
[JsonDerivedType(typeof(AgentSessionEvent.AgentEndEvent), "agent_end")]
[JsonDerivedType(typeof(AgentSessionEvent.TurnStartEvent), "turn_start")]
[JsonDerivedType(typeof(AgentSessionEvent.TurnEndEvent), "turn_end")]
[JsonDerivedType(typeof(AgentSessionEvent.MessageStartEvent), "message_start")]
[JsonDerivedType(typeof(AgentSessionEvent.MessageUpdateEvent), "message_update")]
[JsonDerivedType(typeof(AgentSessionEvent.MessageEndEvent), "message_end")]
[JsonDerivedType(typeof(AgentSessionEvent.ToolExecutionStartEvent), "tool_execution_start")]
[JsonDerivedType(typeof(AgentSessionEvent.ToolExecutionUpdateEvent), "tool_execution_update")]
[JsonDerivedType(typeof(AgentSessionEvent.ToolExecutionEndEvent), "tool_execution_end")]
[JsonDerivedType(typeof(AgentSessionEvent.AgentSettledEvent), "agent_settled")]
[JsonDerivedType(typeof(AgentSessionEvent.QueueUpdateEvent), "queue_update")]
[JsonDerivedType(typeof(AgentSessionEvent.CompactionStartEvent), "compaction_start")]
[JsonDerivedType(typeof(AgentSessionEvent.EntryAppendedEvent), "entry_appended")]
[JsonDerivedType(typeof(AgentSessionEvent.SessionInfoChangedEvent), "session_info_changed")]
[JsonDerivedType(typeof(AgentSessionEvent.ThinkingLevelChangedEvent), "thinking_level_changed")]
[JsonDerivedType(typeof(AgentSessionEvent.CompactionEndEvent), "compaction_end")]
[JsonDerivedType(typeof(AgentSessionEvent.AutoRetryStartEvent), "auto_retry_start")]
[JsonDerivedType(typeof(AgentSessionEvent.AutoRetryEndEvent), "auto_retry_end")]
[JsonDerivedType(typeof(AgentSessionEvent.SummarizationRetryScheduledEvent), "summarization_retry_scheduled")]
[JsonDerivedType(typeof(AgentSessionEvent.SummarizationRetryAttemptStartEvent), "summarization_retry_attempt_start")]
[JsonDerivedType(typeof(AgentSessionEvent.SummarizationRetryFinishedEvent), "summarization_retry_finished")]
[JsonDerivedType(typeof(AgentSessionEvent.BashExecutionUpdateEvent), "bash_execution_update")]
public abstract record AgentSessionEvent
{
    /// <summary>TS 的判别串（如 <c>"message_start"</c>）。JSON 线路上由多态判别式承载，故忽略本属性。</summary>
    [JsonIgnore]
    public abstract string Type { get; }

    // -------------------------------------------------------------------------
    // 核心 AgentEvent 形状（TS WithParentToolCallId 展开后）
    // -------------------------------------------------------------------------

    /// <summary>TS <c>agent_start</c>。</summary>
    public sealed record AgentStartEvent : AgentSessionEvent
    {
        public override string Type => "agent_start";
    }

    /// <summary>TS <c>agent_end</c> 的会话形态：追加 <c>willRetry</c>。</summary>
    public sealed record AgentEndEvent(IReadOnlyList<ChatMessage> Messages, bool WillRetry) : AgentSessionEvent
    {
        public override string Type => "agent_end";
    }

    /// <summary>TS <c>turn_start</c>。</summary>
    public sealed record TurnStartEvent : AgentSessionEvent
    {
        public override string Type => "turn_start";
    }

    /// <summary>TS <c>turn_end</c>。</summary>
    public sealed record TurnEndEvent(AssistantMessage Message, IReadOnlyList<ToolResultMessage> ToolResults)
        : AgentSessionEvent
    {
        public override string Type => "turn_end";
    }

    /// <summary>TS <c>message_start</c>。</summary>
    public sealed record MessageStartEvent(ChatMessage Message) : AgentSessionEvent
    {
        public override string Type => "message_start";
    }

    /// <summary>TS <c>message_update</c>。</summary>
    public sealed record MessageUpdateEvent(AssistantMessageEvent AssistantMessageEvent, AssistantMessage Message)
        : AgentSessionEvent
    {
        public override string Type => "message_update";
    }

    /// <summary>TS <c>message_end</c>。</summary>
    public sealed record MessageEndEvent(ChatMessage Message) : AgentSessionEvent
    {
        public override string Type => "message_end";
    }

    /// <summary>TS <c>tool_execution_start</c>（+ 可选 parentToolCallId，差异 C147）。</summary>
    public sealed record ToolExecutionStartEvent(
        string ToolCallId,
        string ToolName,
        object? Args,
        string? ParentToolCallId = null) : AgentSessionEvent
    {
        public override string Type => "tool_execution_start";
    }

    /// <summary>TS <c>tool_execution_update</c>（+ 可选 parentToolCallId，差异 C147）。</summary>
    public sealed record ToolExecutionUpdateEvent(
        string ToolCallId,
        string ToolName,
        object? Args,
        object? PartialResult,
        string? ParentToolCallId = null) : AgentSessionEvent
    {
        public override string Type => "tool_execution_update";
    }

    /// <summary>TS <c>tool_execution_end</c>（+ 可选 parentToolCallId，差异 C147）。</summary>
    /// <param name="DurationMs">
    /// <c>execute()</c> 的耗时（毫秒，单调时钟）；工具未运行时为 <c>null</c>。TS <c>durationMs?</c>。
    /// </param>
    public sealed record ToolExecutionEndEvent(
        string ToolCallId,
        string ToolName,
        object? Result,
        bool IsError,
        long? DurationMs = null,
        string? ParentToolCallId = null) : AgentSessionEvent
    {
        public override string Type => "tool_execution_end";
    }

    // -------------------------------------------------------------------------
    // 会话自有事件
    // -------------------------------------------------------------------------

    /// <summary>TS <c>agent_settled</c>：本轮 prompt 循环完全落定（含排队/重试/压缩后续）。</summary>
    /// <param name="Aborted">本轮是否因中止（例如按 Escape）而结束。TS <c>aborted</c>。</param>
    public sealed record AgentSettledEvent(bool Aborted) : AgentSessionEvent
    {
        public override string Type => "agent_settled";
    }

    /// <summary>TS <c>queue_update</c>：steering / follow-up 队列展示快照变化。</summary>
    public sealed record QueueUpdateEvent(IReadOnlyList<string> Steering, IReadOnlyList<string> FollowUp)
        : AgentSessionEvent
    {
        public override string Type => "queue_update";
    }

    /// <summary>TS <c>compaction_start</c>。<paramref name="Reason"/> ∈ manual | threshold | overflow。</summary>
    public sealed record CompactionStartEvent(string Reason) : AgentSessionEvent
    {
        public override string Type => "compaction_start";
    }

    /// <summary>TS <c>entry_appended</c>：会话树新增条目（自定义条目 / cache warmer 回填等）。</summary>
    public sealed record EntryAppendedEvent(SessionEntry Entry) : AgentSessionEvent
    {
        public override string Type => "entry_appended";
    }

    /// <summary>TS <c>session_info_changed</c>：会话名变化。</summary>
    public sealed record SessionInfoChangedEvent(string? Name) : AgentSessionEvent
    {
        public override string Type => "session_info_changed";
    }

    /// <summary>TS <c>thinking_level_changed</c>。</summary>
    public sealed record ThinkingLevelChangedEvent(ThinkingLevel Level) : AgentSessionEvent
    {
        public override string Type => "thinking_level_changed";
    }

    /// <summary>TS <c>compaction_end</c>。</summary>
    public sealed record CompactionEndEvent(
        string Reason,
        CompactionResult? Result,
        bool Aborted,
        bool WillRetry,
        string? ErrorMessage = null) : AgentSessionEvent
    {
        public override string Type => "compaction_end";
    }

    /// <summary>TS <c>auto_retry_start</c>。</summary>
    public sealed record AutoRetryStartEvent(int Attempt, int MaxAttempts, int DelayMs, string ErrorMessage)
        : AgentSessionEvent
    {
        public override string Type => "auto_retry_start";
    }

    /// <summary>TS <c>auto_retry_end</c>。</summary>
    public sealed record AutoRetryEndEvent(bool Success, int Attempt, string? FinalError = null) : AgentSessionEvent
    {
        public override string Type => "auto_retry_end";
    }

    /// <summary>TS <c>summarization_retry_scheduled</c>。</summary>
    public sealed record SummarizationRetryScheduledEvent(int Attempt, int MaxAttempts, int DelayMs, string ErrorMessage)
        : AgentSessionEvent
    {
        public override string Type => "summarization_retry_scheduled";
    }

    /// <summary>
    /// TS <c>summarization_retry_attempt_start</c>。TS 是同 type 的两个分支：
    /// <c>{ source: "branchSummary" }</c> 与 <c>{ source: "compaction", reason }</c>；
    /// 端口用 <see cref="Source"/> + 可空 <see cref="Reason"/> 表达（branchSummary 时 Reason 为 null）。
    /// </summary>
    public sealed record SummarizationRetryAttemptStartEvent(string Source, string? Reason = null) : AgentSessionEvent
    {
        public override string Type => "summarization_retry_attempt_start";
    }

    /// <summary>TS <c>summarization_retry_finished</c>。</summary>
    public sealed record SummarizationRetryFinishedEvent : AgentSessionEvent
    {
        public override string Type => "summarization_retry_finished";
    }

    /// <summary>TS <c>bash_execution_update</c>：bash 执行的流式增量。</summary>
    public sealed record BashExecutionUpdateEvent(string? Id, string Delta) : AgentSessionEvent
    {
        public override string Type => "bash_execution_update";
    }
}

/// <summary>会话事件监听器。对应 TS <c>AgentSessionEventListener</c>。</summary>
public delegate void AgentSessionEventListener(AgentSessionEvent @event);
