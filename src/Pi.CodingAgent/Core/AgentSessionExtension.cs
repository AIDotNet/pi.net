using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Core;

/// <summary>扩展注册的一条命令（<c>runner.getRegisteredCommands()</c> 元素的子集）。</summary>
public sealed record RegisteredExtensionCommand(
    string InvocationName,
    string? Description,
    SourceInfo SourceInfo);

/// <summary>扩展声明的资源条目。对应 TS <c>ResourceExtensionPaths</c> 的元素。</summary>
/// <param name="Path">资源路径。</param>
/// <param name="Metadata">来源元数据。TS 的 <c>PathMetadata</c>，端口映射为 <see cref="SourceInfo"/>。</param>
public sealed record ExtensionResourcePath(string Path, SourceInfo Metadata);

/// <summary>
/// 扩展发现的资源路径集合。对应 TS <c>resource-loader.ts</c> 的 <c>ResourceExtensionPaths</c>。
/// </summary>
/// <remarks>
/// 该类型在 TS 里属于 resource-loader；端口的 <c>DefaultResourceLoader</c> 尚未落地
/// <c>extendResources()</c>，故先随本批（4e-3e-9d）落地形状，接线时再下沉到 ResourceLoader。
/// </remarks>
public sealed record ResourceExtensionPaths
{
    public IReadOnlyList<ExtensionResourcePath> SkillPaths { get; init; } = [];

    public IReadOnlyList<ExtensionResourcePath> PromptPaths { get; init; } = [];

    public IReadOnlyList<ExtensionResourcePath> ThemePaths { get; init; } = [];
}

/// <summary>
/// <c>core/agent-session.ts</c> 中扩展绑定与运行时重建的**纯决策逻辑**（4e-3e-9d）。
/// 与 <see cref="AgentSessionCore"/> / <see cref="AgentSessionLoop"/> / <see cref="AgentSessionCompaction"/>
/// 同属 <c>AgentSession</c> 的 partial 静态半边。
/// </summary>
/// <remarks>
/// <c>_buildRuntime</c> 会 <c>new ExtensionRunner(...)</c>，而 <c>ExtensionRunner</c> 本身是 4e-3e-9g 的范围；
/// <c>_bindExtensionCore</c> 的 4 组上下文对象、<c>_refreshToolRegistry</c> 的
/// <c>wrapRegisteredTools</c>、<c>extendResourcesFromExtensions</c> 的
/// <c>resourceLoader.extendResources</c> 也都依赖它。故本批沿用「叶子先行」惯例，
/// 把资源路径映射、命令清单合成、工具注册表与活跃工具名解析、reload 的增量计算抽成静态函数。
/// </remarks>
public partial class AgentSession
{
    private static readonly Regex ExtensionSuffixPattern = new(@"\.(ts|js)$", RegexOptions.Compiled);

    // =========================================================================
    // 资源发现
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>getExtensionSourceLabel</c>：合成路径去掉尖括号后整串作标签，
    /// 文件路径取 basename 并剥掉 <c>.ts</c> / <c>.js</c> 后缀。
    /// </summary>
    public static string GetExtensionSourceLabel(string extensionPath)
    {
        if (SourceInfos.IsSyntheticPath(extensionPath))
        {
            // TS 的 `replace(/[<>]/g, "")` 删除所有尖括号（不限首尾）。
            return $"extension:{extensionPath.Replace("<", string.Empty).Replace(">", string.Empty)}";
        }

        return $"extension:{ExtensionSuffixPattern.Replace(NodePath.Basename(extensionPath), string.Empty)}";
    }

    /// <summary>
    /// 对应 TS <c>buildExtensionResourcePaths</c>：给每个资源路径补上
    /// <c>{ source: "extension:…", scope: "temporary", origin: "top-level", baseDir? }</c>。
    /// </summary>
    /// <remarks>
    /// 差异 C101：TS 的 <c>PathMetadata</c> 不含 <c>path</c>（路径在旁边的 <c>path</c> 字段上），
    /// 而端口的 <see cref="SourceInfo.Path"/> 是必填——此处取资源路径 <c>entry.path</c>。
    /// </remarks>
    public static IReadOnlyList<ExtensionResourcePath> BuildExtensionResourcePaths(
        IReadOnlyList<ExtensionResourceEntry> entries)
    {
        var result = new List<ExtensionResourcePath>(entries.Count);
        foreach (var entry in entries)
        {
            // 合成路径（builtin: / <…>）没有真实目录，故 baseDir 为 undefined。
            var baseDir = SourceInfos.IsSyntheticPath(entry.ExtensionPath)
                ? null
                : NodePath.Dirname(entry.ExtensionPath);

            result.Add(new ExtensionResourcePath(
                entry.Path,
                SourceInfos.CreateSynthetic(
                    entry.Path,
                    GetExtensionSourceLabel(entry.ExtensionPath),
                    SourceScope.Temporary,
                    SourceOrigin.TopLevel,
                    baseDir)));
        }

        return result;
    }

    // =========================================================================
    // 命令清单
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>_bindExtensionCore</c> 里的 <c>getCommands</c>：扩展命令 → 提示词模板 → 技能，
    /// 技能名加 <c>skill:</c> 前缀。
    /// </summary>
    public static IReadOnlyList<SlashCommandInfo> BuildSlashCommandInfos(
        IReadOnlyList<RegisteredExtensionCommand> extensionCommands,
        IReadOnlyList<PromptTemplate> templates,
        IReadOnlyList<Skill> skills)
    {
        var result = new List<SlashCommandInfo>(extensionCommands.Count + templates.Count + skills.Count);

        foreach (var command in extensionCommands)
        {
            result.Add(new SlashCommandInfo
            {
                Name = command.InvocationName,
                Description = command.Description,
                Source = SlashCommandSource.Extension,
                SourceInfo = command.SourceInfo,
            });
        }

        foreach (var template in templates)
        {
            result.Add(new SlashCommandInfo
            {
                Name = template.Name,
                Description = template.Description,
                Source = SlashCommandSource.Prompt,
                SourceInfo = template.SourceInfo,
            });
        }

        foreach (var skill in skills)
        {
            result.Add(new SlashCommandInfo
            {
                Name = $"skill:{skill.Name}",
                Description = skill.Description,
                Source = SlashCommandSource.Skill,
                SourceInfo = skill.SourceInfo,
            });
        }

        return result;
    }

    // =========================================================================
    // 工具注册表
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>_refreshToolRegistry</c> 里 <c>definitionRegistry</c> 的构建：
    /// 先放通过 allow/deny 过滤的内置定义（来源 <c>builtin:&lt;name&gt;</c>），
    /// 再用扩展 / SDK 自定义工具覆盖同名项。
    /// </summary>
    /// <remarks>
    /// 迭代顺序即插入顺序（TS <c>Map</c> 语义），端口的 <see cref="Dictionary{TKey, TValue}"/>
    /// 在「不删除」的前提下同样保持插入顺序；该顺序对提示词片段/准则的产出顺序 load-bearing
    /// （同 <see cref="ApplyToolLoadout"/> 的约定）。
    /// </remarks>
    public static IReadOnlyDictionary<string, ToolDefinitionEntry> BuildDefinitionRegistry(
        IEnumerable<KeyValuePair<string, ToolDefinition>> baseToolDefinitions,
        IEnumerable<ToolDefinitionEntry> customTools,
        ToolFilter filter)
    {
        var registry = new Dictionary<string, ToolDefinitionEntry>(StringComparer.Ordinal);

        foreach (var (name, definition) in baseToolDefinitions)
        {
            if (!IsAllowedTool(name, filter))
            {
                continue;
            }

            registry[name] = new ToolDefinitionEntry
            {
                Definition = definition,
                SourceInfo = SourceInfos.CreateSynthetic(
                    SourceInfos.BuiltinPathPrefix + name,
                    "builtin"),
            };
        }

        // 自定义工具未走 allow 过滤：TS 在更早的 allCustomTools 处已经过滤过一次。
        foreach (var tool in customTools)
        {
            registry[tool.Definition.Name] = tool;
        }

        return registry;
    }

    /// <summary>
    /// 对应 TS <c>_refreshToolRegistry</c> 里 <c>_toolPromptSnippets</c> 的构建：
    /// 归一化后的片段非空才收录。
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildPromptSnippets(
        IEnumerable<ToolDefinitionEntry> definitions)
    {
        var snippets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in definitions)
        {
            var snippet = NormalizePromptSnippet(entry.Definition.PromptSnippet);
            if (snippet is not null)
            {
                snippets[entry.Definition.Name] = snippet;
            }
        }

        return snippets;
    }

    /// <summary>
    /// 对应 TS <c>_refreshToolRegistry</c> 里 <c>_toolPromptGuidelines</c> 的构建：
    /// 归一化后非空才收录。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildPromptGuidelines(
        IEnumerable<ToolDefinitionEntry> definitions)
    {
        var guidelines = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in definitions)
        {
            var normalized = NormalizePromptGuidelines(entry.Definition.PromptGuidelines);
            if (normalized.Count > 0)
            {
                guidelines[entry.Definition.Name] = normalized;
            }
        }

        return guidelines;
    }

    /// <summary>
    /// 对应 TS <c>_refreshToolRegistry</c> 里 <c>nextActiveToolNames</c> 的合成。
    /// </summary>
    /// <param name="requestedActiveToolNames">
    /// <c>options?.activeToolNames</c>：给了就作为起点，否则沿用上一轮的活跃集合。
    /// </param>
    /// <param name="previousActiveToolNames">上一轮的活跃工具名（<c>getActiveToolNames()</c>）。</param>
    /// <param name="toolRegistryNames">新注册表的键序（<c>_toolRegistry.keys()</c>）。</param>
    /// <param name="wrappedExtensionToolNames">
    /// 包装后的扩展 / SDK 工具名（<c>wrappedExtensionTools</c>，即 <c>allCustomTools</c> 的顺序）。
    /// </param>
    /// <param name="allowedToolNames">allowlist；非 <c>null</c> 时走「点名即激活」分支。</param>
    /// <param name="includeAllExtensionTools">
    /// 对应 <c>options?.includeAllExtensionTools</c>：为真时把注册即激活的扩展工具全部纳入。
    /// </param>
    /// <param name="previousActivatedOnRegistration">
    /// 上一轮注册表中「注册即激活」的名字集合——用于识别**新增**的可激活工具。
    /// </param>
    /// <param name="pendingToolNames">待激活工具（恢复自 transcript / 上一轮 reload）。</param>
    /// <param name="definitions">新定义表（供 <c>_isDeclarable</c> / <c>_isActivatedOnRegistration</c> 查询）。</param>
    /// <returns>去重后的活跃工具名（TS 末尾的 <c>[...new Set(...)]</c>）。</returns>
    public static IReadOnlyList<string> ResolveNextActiveToolNames(
        IReadOnlyList<string>? requestedActiveToolNames,
        IReadOnlyList<string> previousActiveToolNames,
        IReadOnlyList<string> toolRegistryNames,
        IReadOnlyList<string> wrappedExtensionToolNames,
        ToolFilter filter,
        bool includeAllExtensionTools,
        IReadOnlySet<string> previousActivatedOnRegistration,
        IReadOnlyList<string> pendingToolNames,
        IReadOnlyDictionary<string, ToolDefinitionEntry> definitions)
    {
        var candidates = new List<string>(
            requestedActiveToolNames is not null ? requestedActiveToolNames : previousActiveToolNames);

        var next = new List<string>();
        foreach (var name in candidates)
        {
            if (IsAllowedTool(name, filter))
            {
                next.Add(name);
            }
        }

        if (filter.Allowed is not null)
        {
            // 点名或命中模式即激活——即便该工具默认不活跃。
            // allowlist 保留但未命中的 MCP 工具保持不活跃。
            foreach (var name in toolRegistryNames)
            {
                if (filter.Allowed(name) && IsDeclarable(definitions, name))
                {
                    next.Add(name);
                }
            }
        }
        else if (includeAllExtensionTools)
        {
            foreach (var name in wrappedExtensionToolNames)
            {
                if (IsActivatedOnRegistration(definitions, name))
                {
                    next.Add(name);
                }
            }
        }
        else if (requestedActiveToolNames is null)
        {
            foreach (var name in toolRegistryNames)
            {
                if (!previousActivatedOnRegistration.Contains(name)
                    && IsActivatedOnRegistration(definitions, name))
                {
                    next.Add(name);
                }
            }
        }

        // 现在才注册出来的待激活工具转为活跃。
        next.AddRange(pendingToolNames);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<string>(next.Count);
        foreach (var name in next)
        {
            if (seen.Add(name))
            {
                deduped.Add(name);
            }
        }

        return deduped;
    }

    // =========================================================================
    // 运行时构建与 reload
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>_buildRuntime</c> 的 <c>defaultActiveToolNames</c> / <c>baseActiveToolNames</c>。
    /// </summary>
    /// <param name="requestedActiveToolNames">对应 <c>options.activeToolNames</c>。</param>
    /// <param name="baseToolsOverrideNames">
    /// <c>Object.keys(this._baseToolsOverride)</c>；**未配置 baseToolsOverride 时为 <c>null</c>**
    /// （配置成空对象时是空列表，两者语义不同）。
    /// </param>
    /// <remarks>
    /// 差异 C103：TS <c>_buildRuntime</c> 这里写的是字面量 <c>["read","bash","edit","write"]</c>，
    /// 而 <c>reload</c> 用的是 <c>DEFAULT_TOOL_NAMES</c> 常量。端口两处统一取
    /// <see cref="SettingsManager.DefaultToolNames"/>（当前值同为该四件套）。
    /// 理论上若 TS 改动常量，其 <c>_buildRuntime</c> 不会跟随，而端口会——已记录为已知差异。
    /// </remarks>
    public static IReadOnlyList<string> ResolveBaseActiveToolNames(
        IReadOnlyList<string>? requestedActiveToolNames,
        IReadOnlyList<string>? baseToolsOverrideNames)
        => requestedActiveToolNames
            ?? baseToolsOverrideNames
            ?? SettingsManager.DefaultToolNames;

    /// <summary>
    /// 对应 TS <c>reload</c> 里的 <c>getDefaultTools()</c> 闭包：未使用默认工具集时为空列表，
    /// 否则把 <c>defaultTools</c> 设置（缺省回落 <see cref="SettingsManager.DefaultToolNames"/>）
    /// 经 <c>+name</c>/<c>-name</c> 修饰符作用一遍。
    /// </summary>
    public static IReadOnlyList<string> ResolveCurrentDefaultTools(
        bool usesDefaultTools,
        IReadOnlyList<string>? settingsDefaultTools,
        IReadOnlyList<string>? defaultToolModifiers = null)
        => usesDefaultTools
            ? SettingsManager.ApplyToolModifiers(
                settingsDefaultTools ?? SettingsManager.DefaultToolNames,
                defaultToolModifiers ?? [])
            : [];

    /// <summary>
    /// 对应 TS <c>reload</c> 的 <c>previousDefaultTools</c>：未使用默认工具集时为空集。
    /// </summary>
    public static IReadOnlySet<string> ResolvePreviousDefaultTools(
        bool usesDefaultTools,
        IReadOnlyList<string>? settingsDefaultTools,
        IReadOnlyList<string>? defaultToolModifiers = null)
        => new HashSet<string>(
            ResolveCurrentDefaultTools(usesDefaultTools, settingsDefaultTools, defaultToolModifiers),
            StringComparer.Ordinal);

    /// <summary>
    /// 对应 TS <c>reload</c> 的 <c>addedDefaultTools</c>：本轮新出现在默认工具集里的名字。
    /// 被移除的默认工具保持活跃，会话中手动关掉的也不会因为「仍在默认集里」而被重新激活。
    /// </summary>
    public static IReadOnlyList<string> ResolveAddedDefaultTools(
        bool usesDefaultTools,
        IReadOnlySet<string> previousDefaultTools,
        IReadOnlyList<string>? settingsDefaultTools,
        IReadOnlyList<string>? defaultToolModifiers = null)
    {
        var current = ResolveCurrentDefaultTools(usesDefaultTools, settingsDefaultTools, defaultToolModifiers);
        return [.. current.Where(name => !previousDefaultTools.Contains(name))];
    }

    /// <summary>
    /// 对应 TS <c>reload</c> 的 <c>hasBindings</c>：四个绑定中任意一个为真值即需要重发 session_start。
    /// </summary>
    public static bool HasExtensionBindings(
        bool hasUiContext,
        bool hasCommandContextActions,
        bool hasShutdownHandler,
        bool hasErrorListener)
        => hasUiContext || hasCommandContextActions || hasShutdownHandler || hasErrorListener;

    /// <summary>
    /// 对应 TS <c>_refreshCurrentModelFromRegistry</c> 的两处早退：无当前模型、注册表里查不到、
    /// 或查到的就是同一个对象时都不动。
    /// </summary>
    /// <remarks>差异 C102：TS 的 <c>refreshedModel === currentModel</c> 是引用同一性，端口用 <see cref="object.ReferenceEquals"/>（同 C89 / C97）。</remarks>
    public static bool ShouldRefreshModelFromRegistry(
        bool hasCurrentModel,
        bool hasRefreshedModel,
        bool sameModelReference)
        => hasCurrentModel && hasRefreshedModel && !sameModelReference;
}

/// <summary>扩展通过 <c>resources_discover</c> 上报的一条资源。对应 TS <c>buildExtensionResourcePaths</c> 的入参元素。</summary>
/// <param name="Path">资源自身的路径。</param>
/// <param name="ExtensionPath">上报它的扩展路径（用于推导 <c>source</c> 与 <c>baseDir</c>）。</param>
public sealed record ExtensionResourceEntry(string Path, string ExtensionPath);
