using System.Text.RegularExpressions;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>CreateAgentSessionOptions</c> 的工具选择子集。</summary>
public sealed record CreateAgentSessionOptions
{
    public IReadOnlyList<string>? Tools { get; init; }

    public IReadOnlyList<string>? ExcludeTools { get; init; }

    public bool NoTools { get; init; }
}

/// <summary>对应 TS <c>createAgentSession</c> 里 tools 解析结果。</summary>
public sealed record ResolvedSessionTools
{
    public required IReadOnlyList<string> InitialActiveToolNames { get; init; }

    public IReadOnlyList<string>? AllowedToolNames { get; init; }

    public IReadOnlyList<string>? ExcludedToolNames { get; init; }

    public IReadOnlyList<string>? DefaultToolModifiers { get; init; }

    public required bool UsesDefaultTools { get; init; }
}

/// <summary>
/// SDK 工厂。对应 TS <c>core/sdk.ts</c>。
/// 4e-3e-2：工具 allowlist / 修饰符 / glob 排除。完整 createAgentSession 依赖 AgentSession 主体，顺延后续切片。
/// </summary>
public static class Sdk
{
    /// <summary>对应 TS <c>createToolNameMatcher</c>。</summary>
    public static Func<string, bool> CreateToolNameMatcher(IReadOnlyList<string> entries)
    {
        var names = entries.Where(entry => !entry.Contains('*')).ToHashSet(StringComparer.Ordinal);
        var patterns = entries.Where(entry => entry.Contains('*')).Select(ToolPatternRegex).ToArray();
        return name => names.Contains(name) || patterns.Any(pattern => pattern.IsMatch(name));
    }

    /// <summary>对应 TS <c>createAgentSession</c> 的 tools 解析。</summary>
    public static ResolvedSessionTools ResolveSessionTools(
        CreateAgentSessionOptions options,
        IReadOnlyList<string>? settingsDefaultTools = null)
    {
        if (options.Tools is not null)
        {
            var error = SettingsManager.GetToolListError(options.Tools);
            if (error is not null)
            {
                throw new ArgumentException(error, nameof(options));
            }
        }

        var defaultToolNames = settingsDefaultTools ?? SettingsManager.DefaultToolNames;
        var toolModifiers = options.Tools is not null && options.Tools.All(SettingsManager.IsToolModifier)
            ? options.Tools
            : null;
        IReadOnlyList<string> selectedToolNames = options.NoTools
            ? []
            : toolModifiers is not null
                ? SettingsManager.ApplyToolModifiers(defaultToolNames, toolModifiers)
                : options.Tools ?? defaultToolNames;
        var allowedToolNames = options.NoTools || (options.Tools is not null && toolModifiers is null)
            ? selectedToolNames
            : null;
        var excludedToolNames = options.ExcludeTools;
        var initialActiveToolNames = allowedToolNames is not null
            ? selectedToolNames
            : excludedToolNames is { Count: > 0 }
                ? selectedToolNames.Where(name => !CreateToolNameMatcher(excludedToolNames)(name)).ToArray()
                : selectedToolNames;

        return new ResolvedSessionTools
        {
            InitialActiveToolNames = initialActiveToolNames,
            AllowedToolNames = allowedToolNames,
            ExcludedToolNames = excludedToolNames,
            DefaultToolModifiers = toolModifiers,
            UsesDefaultTools = (options.Tools is null || toolModifiers is not null) && !options.NoTools,
        };
    }

    private static Regex ToolPatternRegex(string pattern)
    {
        var source = string.Join(".*", pattern.Split('*').Select(Regex.Escape));
        return new Regex($"^{source}$", RegexOptions.Compiled);
    }
}
