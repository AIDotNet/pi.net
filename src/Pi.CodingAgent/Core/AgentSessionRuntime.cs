using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

// `AgentSessionServices` / `AgentSessionRuntimeDiagnostic` / `CreateAgentSessionRuntimeResult`
// 已迁到 AgentSessionServices.cs（对应 TS 的 agent-session-services.ts）。

/// <summary>对应 TS <c>SessionImportFileNotFoundError</c>。</summary>
public sealed class SessionImportFileNotFoundError : FileNotFoundException
{
    public SessionImportFileNotFoundError(string filePath)
        : base($"File not found: {filePath}", filePath)
        => FilePath = filePath;

    public string FilePath { get; }
}

/// <summary>对应 TS <c>fork</c> 解析出的目标叶子与选中文本。</summary>
public sealed record ForkTarget(string? TargetLeafId, string? SelectedText);

/// <summary>对应 TS switch/new/fork/import 的 <c>{ cancelled }</c> 结果。</summary>
public sealed record SessionReplacementResult(bool Cancelled, string? SelectedText = null)
{
    public static SessionReplacementResult CancelledResult { get; } = new(true);

    public static SessionReplacementResult Completed { get; } = new(false);
}

/// <summary>对应 TS <c>newSession</c> 的 persisted / inMemory 分支。</summary>
public enum NewSessionKind
{
    InMemory,
    Persisted,
}

/// <summary>
/// 会话运行时。对应 TS <c>core/agent-session-runtime.ts</c>。
/// 4e-3e-1：导入错误、用户消息文本抽取、JSONL 目标路径去重。
/// 4e-3e-4：fork 目标解析、持久化会话文件前置检查。
/// 4e-3e-5：import JSONL 复制、newSession 模式。
/// 4e-3e-9f：导入目标解析、before-hook 取消判定、fork 位置 / 分支路径守卫、会话事件构造。
/// </summary>
/// <remarks>
/// 类本体（<c>switchSession</c> / <c>newSession</c> / <c>fork</c> / <c>importFromJsonl</c> / <c>dispose</c>
/// 的编排循环）依赖 <c>session.extensionRunner</c> 与 <c>emitSessionShutdownEvent</c>，
/// 二者随扩展运行时（4e-3e-9g）落地，故整段接线顺延 4e-3e-9h。
/// </remarks>
public static class AgentSessionRuntime
{
    /// <summary>对应 TS <c>extractUserMessageText</c>。</summary>
    public static string ExtractUserMessageText(string content) => content;

    /// <summary>对应 TS <c>extractUserMessageText</c> 的块数组分支。</summary>
    public static string ExtractUserMessageText(IEnumerable<ContentBlock> blocks)
        => string.Concat(blocks.OfType<TextContent>().Select(block => block.Text));

    /// <summary>JSON 节点形态，便于对照 TS <c>string | Array</c>。</summary>
    public static string ExtractUserMessageText(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (content is not JsonArray array)
        {
            return string.Empty;
        }

        return string.Concat(
            array.OfType<JsonObject>()
                .Where(part => part["type"]?.GetValue<string>() == "text" && part["text"] is JsonValue)
                .Select(part => part["text"]!.GetValue<string>()));
    }

    /// <summary>对应 TS <c>extractUserMessageText</c> 的 <c>UserMessage.content</c> 分支。</summary>
    public static string ExtractUserMessageText(MessageContent content)
        => content switch
        {
            MessageContent.Text text => text.Value,
            MessageContent.Blocks blocks => ExtractUserMessageText(blocks.Value),
            _ => string.Empty,
        };

    /// <summary>对应 TS <c>importFromJsonl</c> 的目标文件去重。</summary>
    public static string UniqueJsonlDestinationPath(string sessionDir, string sourcePath)
    {
        var destination = Path.Combine(sessionDir, Path.GetFileName(sourcePath));
        if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            return destination;
        }

        if (!File.Exists(destination))
        {
            return destination;
        }

        var name = Path.GetFileNameWithoutExtension(destination);
        var ext = Path.GetExtension(destination);
        var suffix = 1;
        while (File.Exists(destination))
        {
            destination = Path.Combine(sessionDir, $"{name}-{suffix++}{ext}");
        }

        return destination;
    }

    /// <summary>对应 TS <c>importFromJsonl</c> 的存在性检查。</summary>
    public static string RequireImportSource(string inputPath)
    {
        var resolved = Paths.ResolvePath(inputPath);
        if (!File.Exists(resolved))
        {
            throw new SessionImportFileNotFoundError(resolved);
        }

        return resolved;
    }

    /// <summary>对应 TS <c>fork</c> 的 entry/position 解析。</summary>
    public static ForkTarget ResolveForkTarget(SessionEntry? selectedEntry, string position = "before")
    {
        if (selectedEntry is null)
        {
            throw new InvalidOperationException("Invalid entry ID for forking");
        }

        if (position == "at")
        {
            return new ForkTarget(selectedEntry.Id, null);
        }

        if (selectedEntry is not SessionMessageEntry { Message: UserMessage user })
        {
            throw new InvalidOperationException("Invalid entry ID for forking");
        }

        return new ForkTarget(selectedEntry.ParentId, ExtractUserMessageText(user.Content));
    }

    /// <summary>对应 TS <c>fork</c>：已持久化会话必须有 sessionFile。</summary>
    public static string RequirePersistedSessionFile(string? sessionFile, bool persisted)
    {
        if (!persisted)
        {
            return sessionFile ?? string.Empty;
        }

        if (string.IsNullOrEmpty(sessionFile))
        {
            throw new InvalidOperationException("Persisted session is missing a session file");
        }

        return sessionFile;
    }

    /// <summary>对应 TS <c>fork</c>：克隆/fork 前会话文件必须已落盘。</summary>
    public static string RequireSavedSessionFile(string sessionFile)
    {
        if (!File.Exists(sessionFile))
        {
            throw new InvalidOperationException(
                "This session has not been saved yet. Send a message before cloning or forking it.");
        }

        return sessionFile;
    }

    /// <summary>对应 TS <c>newSession</c>：已持久化则 create，否则 inMemory。</summary>
    public static NewSessionKind ResolveNewSessionKind(bool persisted)
        => persisted ? NewSessionKind.Persisted : NewSessionKind.InMemory;

    /// <summary>对应 TS <c>importFromJsonl</c> 的 sourceAlreadyStored。</summary>
    public static bool IsSourceAlreadyStored(string sourcePath, string destinationPath)
        => string.Equals(
            Path.GetFullPath(destinationPath),
            Path.GetFullPath(sourcePath),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>对应 TS <c>importFromJsonl</c> 的 sessionDir mkdir。</summary>
    public static void EnsureSessionDirectory(string sessionDir)
        => Directory.CreateDirectory(sessionDir);

    /// <summary>对应 TS <c>copyFileSync(..., COPYFILE_EXCL)</c>。</summary>
    public static void CopyImportedJsonl(string sourcePath, string destinationPath, bool sourceAlreadyStored)
    {
        if (sourceAlreadyStored)
        {
            return;
        }

        File.Copy(sourcePath, destinationPath);
    }

    /// <summary>对应 TS <c>importFromJsonl</c> 的目标文件段：建目录 → 去重 → 判定是否已存。</summary>
    /// <remarks>
    /// TS 先算 <c>sourceAlreadyStored</c>（用未去重的目标路径），再去重；
    /// <see cref="UniqueJsonlDestinationPath"/> 在「源即目标」时原样返回，故组合顺序等价。
    /// </remarks>
    public static ImportDestination ResolveImportDestination(string sessionDir, string resolvedSourcePath)
    {
        EnsureSessionDirectory(sessionDir);
        var destination = UniqueJsonlDestinationPath(sessionDir, resolvedSourcePath);
        return new ImportDestination(destination, IsSourceAlreadyStored(resolvedSourcePath, destination));
    }

    /// <summary>
    /// <c>session_before_switch</c> / <c>session_before_fork</c> 的取消判定。
    /// </summary>
    /// <remarks>
    /// 对应 TS：没有处理器直接 <c>{ cancelled: false }</c>；有处理器时取
    /// <c>result?.cancel === true</c>（缺省 / <c>false</c> / 非布尔一律视为未取消）。
    /// </remarks>
    public static bool ResolveBeforeHookCancellation(bool hasHandlers, bool? cancel)
        => hasHandlers && cancel == true;

    /// <summary>对应 TS <c>options?.position ?? "before"</c>。</summary>
    public static string ResolveForkPosition(string? position) => position ?? "before";

    /// <summary>对应 TS <c>fork</c>：<c>createBranchedSession</c> 必须返回路径。</summary>
    public static string RequireForkedSessionPath(string? forkedSessionPath)
        => forkedSessionPath ?? throw new InvalidOperationException("Failed to create forked session");

    /// <summary>
    /// 会话替换时的 <c>session_start</c> 事件。对应 TS <c>switchSession</c> / <c>newSession</c> /
    /// <c>fork</c> / <c>importFromJsonl</c> 四处 <c>{ type: "session_start", reason, previousSessionFile }</c>。
    /// </summary>
    /// <remarks>
    /// <paramref name="reason"/> 取 <see cref="Extensions.SessionEventReason"/> 的常量。
    /// <c>newSession</c> 与 <c>importFromJsonl</c> 不带 <c>previousSessionFile</c> 之外的区别都在调用点。
    /// </remarks>
    public static Extensions.ExtensionEvent.SessionStart BuildSessionStartEvent(
        string reason,
        string? previousSessionFile = null)
        => new(reason, previousSessionFile);

    /// <summary>会话替换 / 退出时的 <c>session_shutdown</c> 事件。</summary>
    public static Extensions.ExtensionEvent.SessionShutdown BuildSessionShutdownEvent(
        string reason,
        string? targetSessionFile = null)
        => new(reason, targetSessionFile);
}
