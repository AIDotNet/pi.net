using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;

namespace Pi.CodingAgent.Modes.Rpc;

// ============================================================================
// RPC 协议类型 — TS `modes/rpc/rpc-types.ts`（303 行）
//
// 命令经 stdin 以 JSON 行进入，响应与事件经 stdout 以 JSON 行发出。
// TS 的 RpcCommand / RpcExtensionUIRequest 是判别联合；C# 用记录层级 +
// `[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]` 承载（与
// `SessionEntry` 同一手法，见 SessionEntries.cs）。
//
// 注意：序列化一律走 `JsonL.Options`（camelCase + WhenWritingNull +
// UnsafeRelaxedJsonEscaping）——`WhenWritingNull` 对齐 `JSON.stringify`
// 丢 undefined 键的语义；`UnsafeRelaxedJsonEscaping` 对齐 JSON.stringify
// 不转义非 ASCII 与 U+2028/U+2029 的行为（TS 测试 rpc-jsonl.test.ts 钉住）。
// ============================================================================

/// <summary>
/// RPC 命令判别基类。对应 TS <c>RpcCommand</c> 联合（33 个命令）。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RpcPromptCommand), "prompt")]
[JsonDerivedType(typeof(RpcSteerCommand), "steer")]
[JsonDerivedType(typeof(RpcFollowUpCommand), "follow_up")]
[JsonDerivedType(typeof(RpcAbortCommand), "abort")]
[JsonDerivedType(typeof(RpcClearQueueCommand), "clear_queue")]
[JsonDerivedType(typeof(RpcNewSessionCommand), "new_session")]
[JsonDerivedType(typeof(RpcGetStateCommand), "get_state")]
[JsonDerivedType(typeof(RpcSetModelCommand), "set_model")]
[JsonDerivedType(typeof(RpcCycleModelCommand), "cycle_model")]
[JsonDerivedType(typeof(RpcGetAvailableModelsCommand), "get_available_models")]
[JsonDerivedType(typeof(RpcSetThinkingLevelCommand), "set_thinking_level")]
[JsonDerivedType(typeof(RpcCycleThinkingLevelCommand), "cycle_thinking_level")]
[JsonDerivedType(typeof(RpcGetAvailableThinkingLevelsCommand), "get_available_thinking_levels")]
[JsonDerivedType(typeof(RpcSetSteeringModeCommand), "set_steering_mode")]
[JsonDerivedType(typeof(RpcSetFollowUpModeCommand), "set_follow_up_mode")]
[JsonDerivedType(typeof(RpcCompactCommand), "compact")]
[JsonDerivedType(typeof(RpcSetAutoCompactionCommand), "set_auto_compaction")]
[JsonDerivedType(typeof(RpcSetAutoRetryCommand), "set_auto_retry")]
[JsonDerivedType(typeof(RpcAbortRetryCommand), "abort_retry")]
[JsonDerivedType(typeof(RpcBashCommand), "bash")]
[JsonDerivedType(typeof(RpcAbortBashCommand), "abort_bash")]
[JsonDerivedType(typeof(RpcGetSessionStatsCommand), "get_session_stats")]
[JsonDerivedType(typeof(RpcExportHtmlCommand), "export_html")]
[JsonDerivedType(typeof(RpcSwitchSessionCommand), "switch_session")]
[JsonDerivedType(typeof(RpcForkCommand), "fork")]
[JsonDerivedType(typeof(RpcCloneCommand), "clone")]
[JsonDerivedType(typeof(RpcGetForkMessagesCommand), "get_fork_messages")]
[JsonDerivedType(typeof(RpcGetEntriesCommand), "get_entries")]
[JsonDerivedType(typeof(RpcGetTreeCommand), "get_tree")]
[JsonDerivedType(typeof(RpcGetLastAssistantTextCommand), "get_last_assistant_text")]
[JsonDerivedType(typeof(RpcSetSessionNameCommand), "set_session_name")]
[JsonDerivedType(typeof(RpcGetMessagesCommand), "get_messages")]
[JsonDerivedType(typeof(RpcGetCommandsCommand), "get_commands")]
public abstract record RpcCommand
{
    /// <summary>调用方给的关联 id；缺省时不进响应（TS <c>id?</c>）。</summary>
    public string? Id { get; init; }
}

// ---------------------------------------------------------------------------
// Prompting
// ---------------------------------------------------------------------------

/// <summary>TS <c>{ type: "prompt"; message; images?; streamingBehavior? }</c>。</summary>
public sealed record RpcPromptCommand : RpcCommand
{
    public required string Message { get; init; }

    public IReadOnlyList<ImageContent>? Images { get; init; }

    /// <summary>流式中排队方式：<c>steer</c> 或 <c>followUp</c>（TS 字面量）。</summary>
    public string? StreamingBehavior { get; init; }
}

/// <summary>TS <c>{ type: "steer"; message; images? }</c>。</summary>
public sealed record RpcSteerCommand : RpcCommand
{
    public required string Message { get; init; }

    public IReadOnlyList<ImageContent>? Images { get; init; }
}

/// <summary>TS <c>{ type: "follow_up"; message; images? }</c>。</summary>
public sealed record RpcFollowUpCommand : RpcCommand
{
    public required string Message { get; init; }

    public IReadOnlyList<ImageContent>? Images { get; init; }
}

/// <summary>TS <c>{ type: "abort" }</c>。</summary>
public sealed record RpcAbortCommand : RpcCommand;

/// <summary>TS <c>{ type: "clear_queue" }</c>。</summary>
public sealed record RpcClearQueueCommand : RpcCommand;

/// <summary>TS <c>{ type: "new_session"; parentSession? }</c>。</summary>
public sealed record RpcNewSessionCommand : RpcCommand
{
    public string? ParentSession { get; init; }
}

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

/// <summary>TS <c>{ type: "get_state" }</c>。</summary>
public sealed record RpcGetStateCommand : RpcCommand;

// ---------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------

/// <summary>TS <c>{ type: "set_model"; provider; modelId }</c>。</summary>
public sealed record RpcSetModelCommand : RpcCommand
{
    public required string Provider { get; init; }

    public required string ModelId { get; init; }
}

/// <summary>TS <c>{ type: "cycle_model" }</c>。</summary>
public sealed record RpcCycleModelCommand : RpcCommand;

/// <summary>TS <c>{ type: "get_available_models" }</c>。</summary>
public sealed record RpcGetAvailableModelsCommand : RpcCommand;

// ---------------------------------------------------------------------------
// Thinking
// ---------------------------------------------------------------------------

/// <summary>TS <c>{ type: "set_thinking_level"; level }</c>。</summary>
public sealed record RpcSetThinkingLevelCommand : RpcCommand
{
    public required ThinkingLevel Level { get; init; }
}

/// <summary>TS <c>{ type: "cycle_thinking_level" }</c>。</summary>
public sealed record RpcCycleThinkingLevelCommand : RpcCommand;

/// <summary>TS <c>{ type: "get_available_thinking_levels" }</c>。</summary>
public sealed record RpcGetAvailableThinkingLevelsCommand : RpcCommand;

// ---------------------------------------------------------------------------
// Queue modes
// ---------------------------------------------------------------------------

/// <summary>TS <c>{ type: "set_steering_mode"; mode }</c>。</summary>
public sealed record RpcSetSteeringModeCommand : RpcCommand
{
    public required string Mode { get; init; }
}

/// <summary>TS <c>{ type: "set_follow_up_mode"; mode }</c>。</summary>
public sealed record RpcSetFollowUpModeCommand : RpcCommand
{
    public required string Mode { get; init; }
}

// ---------------------------------------------------------------------------
// Compaction
// ---------------------------------------------------------------------------

/// <summary>TS <c>{ type: "compact"; customInstructions? }</c>。</summary>
public sealed record RpcCompactCommand : RpcCommand
{
    public string? CustomInstructions { get; init; }
}

/// <summary>TS <c>{ type: "set_auto_compaction"; enabled }</c>。</summary>
public sealed record RpcSetAutoCompactionCommand : RpcCommand
{
    public required bool Enabled { get; init; }
}

// ---------------------------------------------------------------------------
// Retry
// ---------------------------------------------------------------------------

/// <summary>TS <c>{ type: "set_auto_retry"; enabled }</c>。</summary>
public sealed record RpcSetAutoRetryCommand : RpcCommand
{
    public required bool Enabled { get; init; }
}

/// <summary>TS <c>{ type: "abort_retry" }</c>。</summary>
public sealed record RpcAbortRetryCommand : RpcCommand;

// ---------------------------------------------------------------------------
// Bash
// ---------------------------------------------------------------------------

/// <summary>TS <c>{ type: "bash"; command; excludeFromContext? }</c>。</summary>
public sealed record RpcBashCommand : RpcCommand
{
    public required string Command { get; init; }

    public bool? ExcludeFromContext { get; init; }
}

/// <summary>TS <c>{ type: "abort_bash" }</c>。</summary>
public sealed record RpcAbortBashCommand : RpcCommand;

// ---------------------------------------------------------------------------
// Session
// ---------------------------------------------------------------------------

/// <summary>TS <c>{ type: "get_session_stats" }</c>。</summary>
public sealed record RpcGetSessionStatsCommand : RpcCommand;

/// <summary>TS <c>{ type: "export_html"; outputPath? }</c>。</summary>
public sealed record RpcExportHtmlCommand : RpcCommand
{
    public string? OutputPath { get; init; }
}

/// <summary>TS <c>{ type: "switch_session"; sessionPath }</c>。</summary>
public sealed record RpcSwitchSessionCommand : RpcCommand
{
    public required string SessionPath { get; init; }
}

/// <summary>TS <c>{ type: "fork"; entryId }</c>。</summary>
public sealed record RpcForkCommand : RpcCommand
{
    public required string EntryId { get; init; }
}

/// <summary>TS <c>{ type: "clone" }</c>。</summary>
public sealed record RpcCloneCommand : RpcCommand;

/// <summary>TS <c>{ type: "get_fork_messages" }</c>。</summary>
public sealed record RpcGetForkMessagesCommand : RpcCommand;

/// <summary>TS <c>{ type: "get_entries"; since? }</c>。</summary>
public sealed record RpcGetEntriesCommand : RpcCommand
{
    public string? Since { get; init; }
}

/// <summary>TS <c>{ type: "get_tree" }</c>。</summary>
public sealed record RpcGetTreeCommand : RpcCommand;

/// <summary>TS <c>{ type: "get_last_assistant_text" }</c>。</summary>
public sealed record RpcGetLastAssistantTextCommand : RpcCommand;

/// <summary>TS <c>{ type: "set_session_name"; name }</c>。</summary>
public sealed record RpcSetSessionNameCommand : RpcCommand
{
    public required string Name { get; init; }
}

// ---------------------------------------------------------------------------
// Messages
// ---------------------------------------------------------------------------

/// <summary>TS <c>{ type: "get_messages" }</c>。</summary>
public sealed record RpcGetMessagesCommand : RpcCommand;

// ---------------------------------------------------------------------------
// Commands (available for invocation via prompt)
// ---------------------------------------------------------------------------

/// <summary>TS <c>{ type: "get_commands" }</c>。</summary>
public sealed record RpcGetCommandsCommand : RpcCommand;

// ============================================================================
// RPC Slash Command (get_commands 响应)
// ============================================================================

/// <summary>可经提示词调用的命令。对应 TS <c>RpcSlashCommand</c>。</summary>
public sealed record RpcSlashCommand
{
    /// <summary>命令名（不含前导斜杠）。</summary>
    public required string Name { get; init; }

    /// <summary>人类可读描述。</summary>
    public string? Description { get; init; }

    /// <summary>命令种类：<c>extension</c> / <c>prompt</c> / <c>skill</c>。</summary>
    public required string Source { get; init; }

    /// <summary>所属资源的来源元数据。</summary>
    public required SourceInfo SourceInfo { get; init; }
}

// ============================================================================
// RPC State (get_state 响应)
// ============================================================================

/// <summary>会话状态投影。对应 TS <c>RpcSessionState</c>。</summary>
public sealed record RpcSessionState
{
    /// <summary>当前模型；未选时为 <c>null</c>（键省略）。</summary>
    public ModelSpec? Model { get; init; }

    /// <summary>思考档位（枚举经 JsonStringEnumConverter 落 TS 字面量）。</summary>
    public required ThinkingLevel ThinkingLevel { get; init; }

    public required bool IsStreaming { get; init; }

    public required bool IsCompacting { get; init; }

    /// <summary><c>all</c> 或 <c>one-at-a-time</c>。</summary>
    public required string SteeringMode { get; init; }

    /// <summary><c>all</c> 或 <c>one-at-a-time</c>。</summary>
    public required string FollowUpMode { get; init; }

    public string? SessionFile { get; init; }

    public required string SessionId { get; init; }

    public string? SessionName { get; init; }

    public required bool AutoCompactionEnabled { get; init; }

    public required int MessageCount { get; init; }

    public required int PendingMessageCount { get; init; }
}

// ============================================================================
// Extension UI Events (stdout)
// ============================================================================

/// <summary>
/// 扩展需要用户输入时发出。对应 TS <c>RpcExtensionUIRequest</c> 联合（9 个 method）。
/// </summary>
/// <remarks>
/// TS 是判别联合，但线形状是扁平对象（<c>type</c> 恒为 <c>extension_ui_request</c>，
/// 按 <c>method</c> 分流）；本记录按「每种 method 只写自己的键」建模，
/// 缺省字段经 <c>WhenWritingNull</c> 省略，与 TS 逐键一致。
/// </remarks>
public sealed record RpcExtensionUIRequest
{
    /// <summary>TS 判别串，恒为 <c>extension_ui_request</c>。</summary>
    public string Type => "extension_ui_request";

    /// <summary>请求 id，客户端以此关联响应。</summary>
    public required string Id { get; init; }

    /// <summary>
    /// 方法：<c>select</c> / <c>confirm</c> / <c>input</c> / <c>editor</c> / <c>notify</c> /
    /// <c>setStatus</c> / <c>setWidget</c> / <c>setTitle</c> / <c>set_editor_text</c>。
    /// </summary>
    public required string Method { get; init; }

    // select / confirm / input / editor 共用
    public string? Title { get; init; }

    // select
    public IReadOnlyList<string>? Options { get; init; }

    // select / confirm / input
    public int? Timeout { get; init; }

    // confirm
    public string? Message { get; init; }

    // input
    public string? Placeholder { get; init; }

    // editor
    public string? Prefill { get; init; }

    // notify
    public string? NotifyType { get; init; }

    // setStatus
    public string? StatusKey { get; init; }

    /// <summary>TS <c>statusText: string | undefined</c>——显式清空时键省略。</summary>
    public string? StatusText { get; init; }

    // setWidget
    public string? WidgetKey { get; init; }

    /// <summary>TS <c>widgetLines: string[] | undefined</c>——组件工厂在 RPC 模式下被忽略。</summary>
    public IReadOnlyList<string>? WidgetLines { get; init; }

    public string? WidgetPlacement { get; init; }

    // setTitle / set_editor_text
    public string? Text { get; init; }
}

// ============================================================================
// Extension UI Commands (stdin)
// ============================================================================

/// <summary>
/// 扩展 UI 请求的响应。对应 TS <c>RpcExtensionUIResponse</c> 联合。
/// </summary>
/// <remarks>
/// TS 按<em>键存在性</em>判别（<c>"cancelled" in r</c> / <c>"confirmed" in r</c> /
/// <c>"value" in r</c>）；本记录三个字段可空，消费方按「cancelled 为真 → 取消，
/// 否则取 value / confirmed」解读，与 TS 三种形状的结果逐一对应。
/// </remarks>
public sealed record RpcExtensionUIResponse
{
    public required string Id { get; init; }

    /// <summary>select / input / editor 的文本结果。</summary>
    public string? Value { get; init; }

    /// <summary>confirm 的布尔结果。</summary>
    public bool? Confirmed { get; init; }

    /// <summary>取消标记（TS <c>{ cancelled: true }</c>）。</summary>
    public bool? Cancelled { get; init; }
}

// ============================================================================
// 响应（stdout）
// ============================================================================

/// <summary>
/// 命令响应的线路构造。对应 TS <c>rpc-mode.ts</c> 里的 <c>output(success(...) / error(...))</c>。
/// </summary>
/// <remarks>
/// <para>
/// TS 的 <c>success(id, cmd)</c> 省略 <c>data</c>，而 <c>success(id, cmd, null)</c> 显式写
/// <c>data: null</c>（cycle_model / cycle_thinking_level 的空结果分支）——
/// <see cref="SuccessNull"/> 承载这一区别。
/// </para>
/// <para>
/// 键序对齐 TS 字面量对象：<c>id</c> → <c>type</c> → <c>command</c> → <c>success</c> → <c>data</c> → <c>error</c>。
/// </para>
/// </remarks>
public static class RpcResponses
{
    /// <summary>成功响应；<paramref name="data"/> 为 null 时省略 <c>data</c> 键（TS <c>data === undefined</c> 分支）。</summary>
    public static JsonObject Success(string? id, string command, object? data = null)
    {
        var response = new JsonObject();
        if (id is not null)
        {
            response["id"] = id;
        }

        response["type"] = "response";
        response["command"] = command;
        response["success"] = true;
        if (data is not null)
        {
            response["data"] = JsonL.SerializeToNode(data);
        }

        return response;
    }

    /// <summary>
    /// 成功响应且负载显式为 null（TS <c>success(id, cmd, null)</c>：写 <c>data: null</c>）。
    /// 仅 cycle_model / cycle_thinking_level 的空结果走此路。
    /// </summary>
    public static JsonObject SuccessNull(string? id, string command)
    {
        var response = new JsonObject();
        if (id is not null)
        {
            response["id"] = id;
        }

        response["type"] = "response";
        response["command"] = command;
        response["success"] = true;
        response["data"] = null;
        return response;
    }

    /// <summary>失败响应。错误响应对任意命令通用，故 <c>command</c> 是自由字符串。</summary>
    public static JsonObject Error(string? id, string command, string message)
    {
        var response = new JsonObject();
        if (id is not null)
        {
            response["id"] = id;
        }

        response["type"] = "response";
        response["command"] = command;
        response["success"] = false;
        response["error"] = message;
        return response;
    }
}
