using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;

namespace Pi.CodingAgent.Modes.Rpc;

// ============================================================================
// 会话事件的 JSON 线路投影 — TS `modes/json-event.ts`（61 行）。
//
// `message_update` 之外的事件原样透传；`message_update` 剥掉累积快照 `partial`
// （`message_start` 给初值、`message_end` 给权威终值，中间增量只需增量字段），
// `toolcall_start` 额外从快照里补出 `id` / `toolName`（大小恒定，故保留）。
// ============================================================================

/// <summary>对应 TS <c>toJsonEvent</c> / <c>JsonAgentSessionEvent</c>。</summary>
public static class JsonEvent
{
    /// <summary>
    /// 把会话事件投影成 JSON 线路对象。
    /// </summary>
    /// <remarks>
    /// 非 <c>message_update</c> 事件原样返回（序列化走记录自身形状）；
    /// <c>message_update</c> 返回 <c>{ type, usage, assistantMessageEvent }</c>。
    /// TS 还校验 <c>event.message.role !== "assistant"</c> 抛错——C# 的
    /// <see cref="AgentSessionEvent.MessageUpdateEvent.Message"/> 定型为
    /// <see cref="AssistantMessage"/>，编译期即保证，无需运行期检查。
    /// </remarks>
    public static object ToJsonEvent(AgentSessionEvent @event)
    {
        if (@event is not AgentSessionEvent.MessageUpdateEvent update)
        {
            return @event;
        }

        var projection = new JsonObject
        {
            ["type"] = "message_update",
            ["assistantMessageEvent"] = ToJsonAssistantMessageEvent(update.AssistantMessageEvent),
        };
        if (update.Message.UsageStats is { } usage)
        {
            // TS 的 message.usage 为 undefined 时 JSON.stringify 丢键，故条件插入。
            projection["usage"] = JsonL.SerializeToNode(usage);
        }

        return projection;
    }

    /// <summary>对应 TS <c>toJsonAssistantMessageEvent</c>。</summary>
    private static JsonObject ToJsonAssistantMessageEvent(AssistantMessageEvent assistantMessageEvent)
    {
        var deltaEvent = (JsonObject)JsonL.SerializeToNode(assistantMessageEvent)!;

        // TS: `const { partial: _partial, ...deltaEvent } = event`（无 partial 的事件原样）。
        deltaEvent.Remove("partial");

        if (assistantMessageEvent is AssistantMessageEvent.ToolCallStart toolCallStart)
        {
            var content = toolCallStart.Partial.Content.ElementAtOrDefault(toolCallStart.ContentIndex);
            if (content is not ToolCallContent toolCall)
            {
                throw new InvalidOperationException(
                    $"toolcall_start content at index {toolCallStart.ContentIndex} is not a tool call");
            }

            deltaEvent["id"] = toolCall.Id;
            deltaEvent["toolName"] = toolCall.Name;
        }

        return deltaEvent;
    }
}
