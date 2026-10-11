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

/// <summary>
/// 会话运行时。对应 TS <c>core/agent-session-runtime.ts</c>。
/// 4e-3e-1：导入错误、用户消息文本抽取、JSONL 目标路径去重。switch/fork 依赖完整 AgentSession，顺延后续切片。
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
}
