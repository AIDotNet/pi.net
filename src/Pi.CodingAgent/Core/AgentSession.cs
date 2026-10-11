using System.Text.RegularExpressions;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>ParsedSkillBlock</c>。</summary>
public sealed record ParsedSkillBlock(string Name, string Location, string Content, string? UserMessage);

/// <summary>对应 TS <c>_tryExecuteExtensionCommand</c> 的 name/args 解析。</summary>
public sealed record ParsedSlashCommand(string Name, string Args);

/// <summary>对应 TS <c>_expandSkillCommand</c> 的查找输入。</summary>
public sealed record SkillCommandSource(string Name, string FilePath, string BaseDir);

/// <summary>对应 TS <c>PromptOptions</c>。</summary>
public sealed record PromptOptions
{
    public bool ExpandPromptTemplates { get; init; } = true;

    public IReadOnlyList<ImageContent>? Images { get; init; }

    public string? StreamingBehavior { get; init; }

    public string Source { get; init; } = "interactive";
}

/// <summary>对应 TS <c>ModelMutationOptions</c>。</summary>
public sealed record ModelMutationOptions
{
    public bool Persist { get; init; }
}

/// <summary>对应 TS <c>ModelCycleResult</c>。</summary>
public sealed record ModelCycleResult
{
    public required ModelSpec Model { get; init; }

    public required ThinkingLevel ThinkingLevel { get; init; }

    public required bool IsScoped { get; init; }
}

/// <summary>对应 TS <c>SessionStats.tokens</c>。</summary>
public sealed record SessionTokenStats
{
    public long Input { get; init; }

    public long Output { get; init; }

    public long CacheRead { get; init; }

    public long CacheWrite { get; init; }

    public long Total { get; init; }
}

/// <summary>对应 TS <c>SessionStats</c>。</summary>
public sealed record SessionStats
{
    public string? SessionFile { get; init; }

    public required string SessionId { get; init; }

    public int UserMessages { get; init; }

    public int AssistantMessages { get; init; }

    public int ToolCalls { get; init; }

    public int ToolResults { get; init; }

    public int TotalMessages { get; init; }

    public required SessionTokenStats Tokens { get; init; }

    public double Cost { get; init; }
}

/// <summary>对应 TS <c>AgentSessionConfig</c> 的已落地子集。</summary>
public sealed record AgentSessionConfig
{
    public required SessionManager SessionManager { get; init; }

    public required SettingsManager SettingsManager { get; init; }

    public required string Cwd { get; init; }

    public required DefaultResourceLoader ResourceLoader { get; init; }

    public required ModelRuntime ModelRuntime { get; init; }

    public IReadOnlyList<string>? InitialActiveToolNames { get; init; }

    public bool UsesDefaultTools { get; init; }

    public IReadOnlyList<string>? DefaultToolModifiers { get; init; }

    public IReadOnlyList<string>? AllowedToolNames { get; init; }

    public IReadOnlyList<string>? ExcludedToolNames { get; init; }
}

/// <summary>
/// 会话核心。对应 TS <c>core/agent-session.ts</c>。
/// 4e-3e-1：parseSkillBlock。4e-3e-3：getLastAssistantText / cycle。4e-3e-6：slash/skill 展开与 hints。prompt 循环顺延 4e-3e-7。
/// </summary>
public static class AgentSession
{
    private static readonly Regex SkillBlockPattern = new(
        @"^<skill name=""([^""]+)"" location=""([^""]+)"">\n([\s\S]*?)\n</skill>(?:\n\n([\s\S]+))?$",
        RegexOptions.Compiled);

    /// <summary>对应 TS <c>parseSkillBlock</c>。</summary>
    public static ParsedSkillBlock? ParseSkillBlock(string text)
    {
        var match = SkillBlockPattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var userMessage = match.Groups[4].Success ? match.Groups[4].Value.Trim() : null;
        if (userMessage is { Length: 0 })
        {
            userMessage = null;
        }

        return new ParsedSkillBlock(
            match.Groups[1].Value,
            match.Groups[2].Value,
            match.Groups[3].Value,
            userMessage);
    }

    /// <summary>对应 TS <c>withoutDeletedHeaders</c>。</summary>
    public static IReadOnlyDictionary<string, string>? WithoutDeletedHeaders(
        IReadOnlyDictionary<string, string?>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        return headers
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.Ordinal);
    }

    /// <summary>对应 TS <c>getLastAssistantText</c>。</summary>
    public static string? GetLastAssistantText(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not AssistantMessage assistant)
            {
                continue;
            }

            if (assistant.StopReason == StopReason.Aborted && assistant.Content.Count == 0)
            {
                continue;
            }

            var text = string.Concat(assistant.Content.OfType<TextContent>().Select(block => block.Text)).Trim();
            return text.Length == 0 ? null : text;
        }

        return null;
    }

    /// <summary>对应 TS <c>cycleModel</c> 的环形下标。</summary>
    public static int NextCycleIndex(int currentIndex, int length, bool forward)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        return forward ? (currentIndex + 1) % length : (currentIndex - 1 + length) % length;
    }

    /// <summary>对应 TS <c>cycleThinkingLevel</c> 的下一档。</summary>
    public static ThinkingLevel? NextThinkingLevel(ThinkingLevel current, IReadOnlyList<ThinkingLevel> levels)
    {
        if (levels.Count == 0)
        {
            return null;
        }

        var currentIndex = -1;
        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i] == current)
            {
                currentIndex = i;
                break;
            }
        }

        return levels[(currentIndex + 1) % levels.Count];
    }

    /// <summary>对应 TS <c>_tryExecuteExtensionCommand</c> 的 name/args 切分。</summary>
    public static ParsedSlashCommand? ParseExtensionCommand(string text)
    {
        if (!text.StartsWith('/'))
        {
            return null;
        }

        var spaceIndex = text.IndexOf(' ');
        var name = spaceIndex == -1 ? text[1..] : text[1..spaceIndex];
        var args = spaceIndex == -1 ? string.Empty : text[(spaceIndex + 1)..];
        return new ParsedSlashCommand(name, args);
    }

    /// <summary>对应 TS <c>_expandSkillCommand</c> 的 /skill:name 解析。</summary>
    public static ParsedSlashCommand? ParseSkillCommand(string text)
    {
        const string prefix = "/skill:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var spaceIndex = text.IndexOf(' ');
        var name = spaceIndex == -1 ? text[prefix.Length..] : text[prefix.Length..spaceIndex];
        var args = spaceIndex == -1 ? string.Empty : text[(spaceIndex + 1)..].Trim();
        return new ParsedSlashCommand(name, args);
    }

    /// <summary>对应 TS <c>_expandSkillCommand</c>。查找失败或读文件失败时返回原文。</summary>
    public static string ExpandSkillCommand(
        string text,
        Func<string, SkillCommandSource?> findSkill,
        Func<string, string> readFile)
    {
        var parsed = ParseSkillCommand(text);
        if (parsed is null)
        {
            return text;
        }

        var skill = findSkill(parsed.Name);
        if (skill is null)
        {
            return text;
        }

        try
        {
            var body = Frontmatter.Strip(readFile(skill.FilePath)).Trim();
            var skillBlock =
                $"<skill name=\"{skill.Name}\" location=\"{skill.FilePath}\">\nReferences are relative to {skill.BaseDir}.\n\n{body}\n</skill>";
            return parsed.Args.Length > 0 ? $"{skillBlock}\n\n{parsed.Args}" : skillBlock;
        }
        catch
        {
            return text;
        }
    }

    /// <summary>对应 TS <c>prompt</c> 把 <c>normalized.hints</c> 拼到用户文本。</summary>
    public static string AppendPromptHints(string expandedText, IReadOnlyList<string> hints)
        => hints.Count == 0 ? expandedText : $"{expandedText}\n\n{string.Join("\n", hints)}";
}
