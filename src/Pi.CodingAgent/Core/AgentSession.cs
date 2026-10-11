using System.Text.RegularExpressions;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>ParsedSkillBlock</c>。</summary>
public sealed record ParsedSkillBlock(string Name, string Location, string Content, string? UserMessage);

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
/// 4e-3e-1：parseSkillBlock。4e-3e-3：getLastAssistantText、cycle 下标、thinking 循环。prompt 循环顺延 4e-3e-4。
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
}
