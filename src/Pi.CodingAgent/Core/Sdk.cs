namespace Pi.CodingAgent.Core;

/// <summary>
/// 对应 TS <c>CreateAgentSessionOptions.noTools</c> 的 <c>"all" | "builtin"</c>。
/// </summary>
public enum NoToolsMode
{
    /// <summary>对应 <c>"all"</c>：从零工具开始。</summary>
    All,

    /// <summary>
    /// 对应 <c>"builtin"</c>：停用内置默认工具（read/bash/edit/write），但保留扩展 / 自定义工具。
    /// </summary>
    Builtin,
}

/// <summary>对应 TS <c>CreateAgentSessionOptions</c> 的工具选择子集。</summary>
public sealed record CreateAgentSessionOptions
{
    /// <summary>工具名或模式（<c>*</c> 匹配任意字符）的 allowlist。</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>工具名或模式的 denylist；<see cref="Tools"/> 之后生效。</summary>
    public IReadOnlyList<string>? ExcludeTools { get; init; }

    /// <summary>无显式 allowlist 时的默认工具抑制模式。</summary>
    public NoToolsMode? NoTools { get; init; }
}

/// <summary>
/// 对应 TS <c>createAgentSession</c> 里 tools 解析的结果。
/// </summary>
/// <param name="InitialActiveToolNames">
/// <c>initialActiveToolNames</c>：起始活跃工具（allowlist 或默认集，再按 denylist 过滤）。
/// </param>
/// <param name="AllowedToolNames">
/// <c>allowedToolNames</c>：<c>options.tools ?? (noTools === "all" ? [] : undefined)</c>。
/// 为 <c>null</c> 表示「没有 allowlist」（而不是空 allowlist）。
/// </param>
/// <param name="ExcludedToolNames"><c>excludedToolNames</c>：原样透传的 denylist。</param>
/// <param name="UsesDefaultTools"><c>usesDefaultTools</c>：<c>tools === undefined &amp;&amp; !noTools</c>。</param>
public sealed record ResolvedSessionTools
{
    public required IReadOnlyList<string> InitialActiveToolNames { get; init; }

    public IReadOnlyList<string>? AllowedToolNames { get; init; }

    public IReadOnlyList<string>? ExcludedToolNames { get; init; }

    public required bool UsesDefaultTools { get; init; }
}

/// <summary>
/// SDK 工厂。对应 TS <c>core/sdk.ts</c>。
/// 4e-3e-2：工具 allowlist / denylist 解析。
/// </summary>
/// <remarks>
/// <para>
/// <b>2026-10-12 基线对齐</b>：本类此前混用了两个 TS 修订版，已按 <c>origin/main</c>（<c>428a12bc7</c>）重写。
/// 三处修正：
/// </para>
/// <list type="number">
/// <item>
/// <c>GetToolListError</c> 被**移除**——TS 没有这个校验，<c>resolveDefaultTools</c> 对畸形输入只是静默过滤。
/// </item>
/// <item>
/// <c>options.tools</c> 不再被当作 <c>+name</c>/<c>-name</c> 修饰符。TS 里 <c>+</c>/<c>-</c> 只属于
/// <c>defaultTools</c> **设置项**（见 <see cref="SettingsManager.ResolveDefaultTools"/>），
/// <c>options.tools</c> 是纯 allowlist（支持 <c>*</c> 模式）。
/// </item>
/// <item>
/// <c>NoTools</c> 由 <c>bool</c> 改为 <see cref="NoToolsMode"/>，恢复 TS 的 <c>"all" | "builtin"</c> 区分——
/// 二者的 <c>allowedToolNames</c> 不同（<c>[]</c> vs <c>undefined</c>），会走
/// <c>_refreshToolRegistry</c> 的不同分支。
/// </item>
/// </list>
/// <para>
/// <c>CreateToolNameMatcher</c> 已下沉到 <see cref="McpServers"/>（TS 里它定义在 <c>mcp-servers.ts</c>）。
/// </para>
/// </remarks>
public static class Sdk
{
    /// <summary>对应 TS <c>createAgentSession</c> 的 tools 解析。</summary>
    public static ResolvedSessionTools ResolveSessionTools(
        CreateAgentSessionOptions options,
        IReadOnlyList<string>? settingsDefaultTools = null)
    {
        var allowedToolNames = options.Tools
            ?? (options.NoTools == NoToolsMode.All ? [] : null);
        var excludedToolNames = options.ExcludeTools;
        var isExcludedTool = excludedToolNames is not null
            ? McpServers.CreateToolNameMatcher(excludedToolNames)
            : null;
        var initialActiveToolNames = (
            options.Tools
            ?? (options.NoTools is not null
                ? []
                : settingsDefaultTools ?? SettingsManager.DefaultToolNames))
            .Where(name => isExcludedTool is null || !isExcludedTool(name))
            .ToArray();

        return new ResolvedSessionTools
        {
            InitialActiveToolNames = initialActiveToolNames,
            AllowedToolNames = allowedToolNames,
            ExcludedToolNames = excludedToolNames,
            UsesDefaultTools = options.Tools is null && options.NoTools is null,
        };
    }
}
