using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>CreateAgentSessionRuntimeResult</c> 的诊断/服务子集。</summary>
public sealed record AgentSessionServices
{
    public required string Cwd { get; init; }

    public required string AgentDir { get; init; }

    public required SettingsManager SettingsManager { get; init; }

    public required ModelRuntime ModelRuntime { get; init; }

    public required DefaultResourceLoader ResourceLoader { get; init; }
}

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

/// <summary>
/// 会话运行时。对应 TS <c>core/agent-session-runtime.ts</c>。
/// 4e-3e-1：导入错误、用户消息文本抽取、JSONL 目标路径去重。
/// 4e-3e-4：fork 目标解析、持久化会话文件前置检查。完整 switch/fork 循环顺延 4e-3e-5。
/// </summary>
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
}
