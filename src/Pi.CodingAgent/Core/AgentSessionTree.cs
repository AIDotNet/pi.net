using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;

namespace Pi.CodingAgent.Core;

/// <summary>摘要来源。对应 TS <c>navigateTree</c> 里 <c>summaryText</c> 的三种可能来源。</summary>
public enum TreeSummarySource
{
    /// <summary>不生成摘要（未要求摘要，或没有要摘要的条目且扩展也没给）。</summary>
    None,

    /// <summary>走默认摘要器（<c>generateBranchSummary</c>）。</summary>
    DefaultSummarizer,

    /// <summary>扩展在 <c>session_before_tree</c> 里直接给了摘要。</summary>
    Extension,
}

/// <summary>叶子切换方式。对应 TS <c>navigateTree</c> 末尾的三分支。</summary>
public enum TreeSwitchKind
{
    /// <summary>有摘要 → <c>branchWithSummary</c>。</summary>
    BranchWithSummary,

    /// <summary>无摘要且目标是根 → <c>resetLeaf</c>。</summary>
    ResetLeaf,

    /// <summary>无摘要且目标非根 → <c>branch</c>。</summary>
    Branch,
}

/// <summary>树导航后的新叶位置与要回填编辑器的文本。</summary>
/// <param name="NewLeafId">新叶条目 id；根位置为 <c>null</c>。</param>
/// <param name="EditorText">用户消息 / 自定义消息的文本，回填到编辑器；其他条目为 <c>null</c>。</param>
public sealed record TreeNavigationLeaf(string? NewLeafId, string? EditorText);

/// <summary>扩展在 <c>session_before_tree</c> 里可覆盖的三项。三者都遵循「未提供则沿用原值」。</summary>
public sealed record TreeNavigationOverrides(
    string? CustomInstructions = null,
    bool? ReplaceInstructions = null,
    string? Label = null);

/// <summary>供 fork 选择器使用的一条用户消息。对应 TS <c>getUserMessagesForForking</c> 的元素。</summary>
public sealed record UserMessageForForking(string EntryId, string Text);

/// <summary>
/// <c>core/agent-session.ts</c> 中树导航 / fork 投影 / 导出主题名的**纯决策逻辑**（4e-3e-9e）。
/// 与 <see cref="AgentSessionCore"/> / <see cref="AgentSessionLoop"/> /
/// <see cref="AgentSessionCompaction"/> / <see cref="AgentSessionExtension"/> 同属
/// <c>AgentSession</c> 的 partial 静态半边。
/// </summary>
/// <remarks>
/// <c>navigateTree</c> 本体依赖 <c>_extensionRunner</c>（<c>session_before_tree</c> / <c>session_tree</c>
/// 两个事件的发射）、<c>_branchSummaryAbortController</c>、<c>_refreshFinalizedContext</c> /
/// <c>_restoreToolsFromTranscript</c> 与 <c>_resolveIdleWaitIfIdle</c>，全部是实例状态且前者属 4e-3e-9g。
/// 故本批沿用「叶子先行」，只落地判定、投影与文案；编排顺延 4e-3e-9h。
/// </remarks>
public partial class AgentSession
{
    // =========================================================================
    // navigateTree —— 前置守卫
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>navigateTree</c> 开头的两条守卫。两者都抛错而不是返回。
    /// </summary>
    public static void ThrowIfTreeNavigationBlocked(bool isStreaming, bool isCompacting)
    {
        if (isStreaming)
        {
            throw new InvalidOperationException(
                "Wait for the current response to finish before navigating the session tree.");
        }

        if (isCompacting)
        {
            throw new InvalidOperationException(
                "Wait for the current compaction or tree navigation to finish before navigating the session tree.");
        }
    }

    /// <summary>对应 TS <c>targetId === oldLeafId</c>：已在目标位置 → 空操作，返回 <c>{ cancelled: false }</c>。</summary>
    public static bool IsTreeNavigationNoOp(string targetId, string? oldLeafId)
        => string.Equals(targetId, oldLeafId, StringComparison.Ordinal);

    /// <summary>对应 TS <c>options.summarize &amp;&amp; !this.model</c> 的守卫。</summary>
    public static void ThrowIfNoModelForSummarization(bool summarize, bool hasModel)
    {
        if (summarize && !hasModel)
        {
            throw new InvalidOperationException("No model available for summarization");
        }
    }

    /// <summary>对应 TS <c>Entry ${targetId} not found</c>。</summary>
    public static void ThrowIfTreeTargetMissing(bool targetEntryExists, string targetId)
    {
        if (!targetEntryExists)
        {
            throw new InvalidOperationException($"Entry {targetId} not found");
        }
    }

    // =========================================================================
    // navigateTree —— 扩展覆盖
    // =========================================================================

    /// <summary>
    /// 对应 TS 里对 <c>customInstructions</c> / <c>replaceInstructions</c> / <c>label</c> 的三处
    /// <c>if (result?.X !== undefined) X = result.X;</c>——扩展给了就覆盖，没给就沿用。
    /// </summary>
    /// <remarks>
    /// 差异 C104：TS 的判据是「字段 !== undefined」，端口的 <c>null</c> 即 <c>undefined</c>，
    /// 故用 <c>??</c> 表达。注意 <c>label</c> 在端口是 <c>string?</c>，TS 里也是
    /// <c>string | undefined</c>——**没有**「显式置空」的表达，因此语义一致。
    /// </remarks>
    public static TreeNavigationOverrides ApplyTreeExtensionOverrides(
        TreeNavigationOverrides current,
        TreeNavigationOverrides fromExtension)
        => new(
            fromExtension.CustomInstructions ?? current.CustomInstructions,
            fromExtension.ReplaceInstructions ?? current.ReplaceInstructions,
            fromExtension.Label ?? current.Label);

    /// <summary>对应 TS <c>if (result?.cancel) return { cancelled: true };</c>。</summary>
    public static bool ShouldCancelTreeNavigation(bool extensionCancel) => extensionCancel;

    /// <summary>对应 TS <c>if (result?.summary &amp;&amp; options.summarize)</c>：扩展摘要只在用户要摘要时被采纳。</summary>
    public static bool ShouldTakeExtensionSummary(bool extensionProvidedSummary, bool summarize)
        => extensionProvidedSummary && summarize;

    // =========================================================================
    // navigateTree —— 摘要来源
    // =========================================================================

    /// <summary>
    /// 对应 TS 的 <c>if / else if</c> 两分支：优先默认摘要器，其次扩展摘要。
    /// </summary>
    /// <remarks>
    /// TS 里扩展摘要分支是 <c>else if (extensionSummary)</c>，而 <c>extensionSummary</c> 只在
    /// 「扩展给了摘要 **且** 用户要摘要」时才被赋值（见 <see cref="ShouldTakeExtensionSummary"/>）。
    /// 因此 <c>summarize</c> 为假时 <paramref name="hasExtensionSummary"/> 必然为假，两分支互斥。
    /// </remarks>
    public static TreeSummarySource ResolveTreeSummarySource(
        bool summarize, int entriesToSummarizeCount, bool hasExtensionSummary)
    {
        if (summarize && entriesToSummarizeCount > 0 && !hasExtensionSummary)
        {
            return TreeSummarySource.DefaultSummarizer;
        }

        return hasExtensionSummary ? TreeSummarySource.Extension : TreeSummarySource.None;
    }

    /// <summary>
    /// 对应 TS 默认摘要器的 <c>summaryDetails</c>：两个文件列表各带 <c>|| []</c> 回退。
    /// </summary>
    public static BranchSummaryDetails ResolveTreeSummaryDetails(BranchSummaryResult result)
        => new(result.ReadFiles ?? [], result.ModifiedFiles ?? []);

    // =========================================================================
    // navigateTree —— 新叶位置与切换
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>navigateTree</c> 里按 <c>targetEntry.type</c> 决定新叶位置的三分支：
    /// 用户消息与自定义消息把叶挂到**父节点**并把文本回填编辑器，其余条目直接以目标为叶。
    /// </summary>
    /// <param name="targetEntry">已确认存在的目标条目。</param>
    /// <param name="targetId">目标条目 id（非用户消息分支直接用它作新叶）。</param>
    /// <remarks>
    /// 差异 C105：TS 的 <c>contentText(content, "")</c> 分隔符是空串（端口照抄）；
    /// <see cref="CustomMessageEntry.Content"/> 在端口可空，TS 侧该字段必填——
    /// 端口对 <c>null</c> 取空串，与 TS 的「空内容」等价。
    /// </remarks>
    public static TreeNavigationLeaf ResolveTreeNavigationLeaf(SessionEntry targetEntry, string targetId)
        => targetEntry switch
        {
            SessionMessageEntry messageEntry when messageEntry.Message is UserMessage user
                => new TreeNavigationLeaf(messageEntry.ParentId, Text.ContentText(user.Content, string.Empty)),

            CustomMessageEntry custom
                => new TreeNavigationLeaf(
                    custom.ParentId,
                    custom.Content is null ? string.Empty : Text.ContentText(custom.Content, string.Empty)),

            _ => new TreeNavigationLeaf(targetId, null),
        };

    /// <summary>
    /// 对应 TS 末尾的三分支：有摘要 → <c>branchWithSummary</c>；无摘要且 <c>newLeafId === null</c>
    /// → <c>resetLeaf</c>；否则 <c>branch</c>。
    /// </summary>
    public static TreeSwitchKind ResolveTreeSwitchKind(bool hasSummaryText, string? newLeafId)
    {
        if (hasSummaryText)
        {
            return TreeSwitchKind.BranchWithSummary;
        }

        return newLeafId is null ? TreeSwitchKind.ResetLeaf : TreeSwitchKind.Branch;
    }

    /// <summary>对应 TS <c>if (label) appendLabelChange(summaryId, label)</c>：摘要条目上的标签。</summary>
    public static bool ShouldLabelSummaryEntry(string? label, bool hasSummaryText)
        => hasSummaryText && !string.IsNullOrEmpty(label);

    /// <summary>
    /// 对应 TS <c>if (label &amp;&amp; !summaryText) appendLabelChange(targetId, label)</c>：
    /// 不生成摘要时把标签挂到**目标条目**上（有摘要时摘要条目已经挂过，不能重复）。
    /// </summary>
    public static bool ShouldLabelTargetEntry(string? label, bool hasSummaryText)
        => !hasSummaryText && !string.IsNullOrEmpty(label);

    /// <summary>
    /// 对应 TS <c>session_tree</c> 事件的 <c>fromExtension: summaryText ? fromExtension : undefined</c>。
    /// </summary>
    public static bool? ResolveTreeEventFromExtension(bool hasSummaryText, bool fromExtension)
        => hasSummaryText ? fromExtension : null;

    // =========================================================================
    // getUserMessagesForForking
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>getUserMessagesForForking</c>：按条目顺序挑出 role 为 user、且文本非空的
    /// 消息条目，保留条目 id 与文本。
    /// </summary>
    /// <remarks>
    /// TS 的 <c>if (text)</c> 是 JS 真值判定——空串为假，故端口用 <c>Length > 0</c>。
    /// 非 user 消息、非 message 条目、以及纯图片（无文本）的用户消息都不入选。
    /// 差异 C106：TS 的 <c>entry.id</c> 类型上必填，端口 <see cref="SessionEntry.Id"/> 可空
    /// （v1 文件缺该字段，迁移时补齐）。端口对 <c>Id</c> 为 <c>null</c> 的条目直接跳过——
    /// 迁移后不可达，属防御性分支。
    /// </remarks>
    public static IReadOnlyList<UserMessageForForking> ProjectUserMessagesForForking(
        IReadOnlyList<SessionEntry> entries)
    {
        var result = new List<UserMessageForForking>();
        foreach (var entry in entries)
        {
            if (entry is not SessionMessageEntry { Message: UserMessage user })
            {
                continue;
            }

            var text = Text.ContentText(user.Content, string.Empty);
            if (text.Length > 0 && entry.Id is { } id)
            {
                result.Add(new UserMessageForForking(id, text));
            }
        }

        return result;
    }

    // =========================================================================
    // exportToHtml —— 主题名解析
    // =========================================================================

    /// <summary>
    /// 对应 TS <c>exportToHtml</c> 的
    /// <c>[options.themeName, settings.getTheme()].find(c =&gt; c !== undefined &amp;&amp; getThemeByName(c) !== undefined)</c>：
    /// 先看显式传入的主题名，再看设置里的；两者都查不到就返回 <c>undefined</c>（导出走默认主题）。
    /// </summary>
    /// <param name="optionThemeName">调用方显式指定的主题名。</param>
    /// <param name="settingsTheme"><c>settingsManager.getTheme()</c> 的值。</param>
    /// <param name="themeExists">主题注册表查询（<c>getThemeByName(c) !== undefined</c>）。</param>
    public static string? ResolveExportThemeName(
        string? optionThemeName, string? settingsTheme, Func<string, bool> themeExists)
    {
        if (optionThemeName is not null && themeExists(optionThemeName))
        {
            return optionThemeName;
        }

        if (settingsTheme is not null && themeExists(settingsTheme))
        {
            return settingsTheme;
        }

        return null;
    }
}

public partial class AgentSession
{
    /// <summary>
    /// 导出会话为 HTML 并返回落盘路径。对应 TS <c>exportToHtml(outputPath?, options?)</c>
    /// （agent-session.ts 的导出入口；4f-5 的 RPC <c>export_html</c> 命令经此进入）。
    /// </summary>
    /// <remarks>
    /// 主题名解析走 <see cref="ResolveExportThemeName"/>（显式名优先，其次设置里的，
    /// 都查不到走默认导出色）；自定义工具的 HTML 渲染由扩展 runner 的
    /// <c>resolveToolRenderers</c> 提供（TS 的 <c>createToolHtmlRenderer</c>）。
    /// </remarks>
    public Task<string> ExportToHtmlAsync(string? outputPath = null, string? themeName = null)
    {
        var resolvedTheme = ResolveExportThemeName(
            themeName,
            SettingsManager.GetTheme(),
            candidate => ThemeRuntime.GetThemeByName(candidate) is not null);

        var toolRenderer = ToolHtmlRendererFactory.Create(new ToolHtmlRendererDeps
        {
            // TS: `resolveToolRenderers(name, () => this.getToolDefinition(name))`——
            // 扩展的 toolRenderers 解析器按加载顺序链在前，base 取注册表里的工具定义；
            // getToolDefinition 返回的 ToolDefinition 结构上就是 ToolRenderers 的超集，
            // C# 需显式摘出渲染三元组。
            GetToolRenderers = name => ExtensionRunner.ResolveToolRenderers(name, () =>
            {
                var definition = GetToolDefinition(name);
                return definition is null
                    ? null
                    : new ToolRenderers
                    {
                        RenderShell = definition.RenderShell,
                        RenderCall = definition.RenderCall,
                        RenderResult = definition.RenderResult,
                    };
            }),
            Theme = ThemeRuntime.Current,
            Cwd = SessionManager.GetCwd(),
        });

        return Task.FromResult(ExportHtml.ExportSessionToHtml(SessionManager, new ExportHtmlOptions
        {
            OutputPath = outputPath,
            ThemeName = resolvedTheme,
            ToolRenderer = toolRenderer,
        }));
    }
}
