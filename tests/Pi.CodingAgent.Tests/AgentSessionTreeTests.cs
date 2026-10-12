using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 4e-3e-9e：<c>agent-session.ts</c> 中树导航 / fork 投影 / 导出主题名的纯决策逻辑。
/// 逐条对照 TS 参考实现（<c>D:\AI\参考项目\pi\packages\coding-agent\src\core\agent-session.ts</c>
/// 的 <c>setSessionName</c> / <c>navigateTree</c> / <c>getUserMessagesForForking</c> / <c>exportToHtml</c>）。
/// </summary>
public sealed class AgentSessionTreeTests
{
    // ---------------------------------------------------------------------
    // 构造辅助
    // ---------------------------------------------------------------------

    private static SessionMessageEntry UserEntry(string id, string? parentId, string text)
        => new()
        {
            Id = id,
            ParentId = parentId,
            Message = new UserMessage(MessageContent.From(text), 1),
        };

    private static SessionMessageEntry AssistantEntry(string id, string? parentId)
        => new()
        {
            Id = id,
            ParentId = parentId,
            Message = new AssistantMessage([new TextContent("ok")]),
        };

    private static CustomMessageEntry CustomEntry(string id, string? parentId, string text)
        => new()
        {
            Id = id,
            ParentId = parentId,
            CustomType = "note",
            Content = MessageContent.From(text),
            Display = true,
        };

    // ---------------------------------------------------------------------
    // navigateTree —— 前置守卫
    // ---------------------------------------------------------------------

    [Fact]
    public void ThrowIfTreeNavigationBlocked_StreamingWinsOverCompacting()
    {
        // 两条守卫都命中时先抛 streaming 的那条（TS 里就是先判 isStreaming）。
        var streaming = Assert.Throws<InvalidOperationException>(
            () => AgentSession.ThrowIfTreeNavigationBlocked(isStreaming: true, isCompacting: true));
        Assert.Equal(
            "Wait for the current response to finish before navigating the session tree.",
            streaming.Message);

        var compacting = Assert.Throws<InvalidOperationException>(
            () => AgentSession.ThrowIfTreeNavigationBlocked(isStreaming: false, isCompacting: true));
        Assert.Equal(
            "Wait for the current compaction or tree navigation to finish before navigating the session tree.",
            compacting.Message);

        AgentSession.ThrowIfTreeNavigationBlocked(isStreaming: false, isCompacting: false);
    }

    [Fact]
    public void IsTreeNavigationNoOp_ComparesByIdentityOfValue()
    {
        Assert.True(AgentSession.IsTreeNavigationNoOp("a", "a"));
        Assert.False(AgentSession.IsTreeNavigationNoOp("a", "b"));
        // 旧叶为 null（空会话）时不可能相等。
        Assert.False(AgentSession.IsTreeNavigationNoOp("a", null));
    }

    [Fact]
    public void ThrowIfNoModelForSummarization_OnlyWhenSummarizing()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentSession.ThrowIfNoModelForSummarization(summarize: true, hasModel: false));
        Assert.Equal("No model available for summarization", ex.Message);

        // 有模型 → 通过；不摘要 → 无模型也通过。
        AgentSession.ThrowIfNoModelForSummarization(summarize: true, hasModel: true);
        AgentSession.ThrowIfNoModelForSummarization(summarize: false, hasModel: false);
    }

    [Fact]
    public void ThrowIfTreeTargetMissing_UsesTsMessage()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentSession.ThrowIfTreeTargetMissing(targetEntryExists: false, "abc"));
        Assert.Equal("Entry abc not found", ex.Message);

        AgentSession.ThrowIfTreeTargetMissing(targetEntryExists: true, "abc");
    }

    // ---------------------------------------------------------------------
    // navigateTree —— 扩展覆盖
    // ---------------------------------------------------------------------

    [Fact]
    public void ApplyTreeExtensionOverrides_TakesProvidedFieldsOnly()
    {
        var current = new TreeNavigationOverrides("keep-instructions", false, "keep-label");

        // 扩展给了全部三项 → 全覆盖。
        Assert.Equal(
            new TreeNavigationOverrides("ext-instructions", true, "ext-label"),
            AgentSession.ApplyTreeExtensionOverrides(
                current, new TreeNavigationOverrides("ext-instructions", true, "ext-label")));

        // 扩展什么都没给（三者皆 undefined/null）→ 原样保留。
        Assert.Equal(current, AgentSession.ApplyTreeExtensionOverrides(current, new TreeNavigationOverrides()));

        // 只给了 label → 只有 label 变。
        Assert.Equal(
            new TreeNavigationOverrides("keep-instructions", false, "only-label"),
            AgentSession.ApplyTreeExtensionOverrides(current, new TreeNavigationOverrides(Label: "only-label")));

        // 显式 false 也要生效（不能把 false 当成「没给」）。
        Assert.Equal(
            new TreeNavigationOverrides("keep-instructions", true, "keep-label"),
            AgentSession.ApplyTreeExtensionOverrides(
                current, new TreeNavigationOverrides(ReplaceInstructions: true)));
    }

    [Fact]
    public void ShouldCancelTreeNavigation_PassesThrough()
    {
        Assert.True(AgentSession.ShouldCancelTreeNavigation(true));
        Assert.False(AgentSession.ShouldCancelTreeNavigation(false));
    }

    [Fact]
    public void ShouldTakeExtensionSummary_RequiresUserWantsSummary()
    {
        Assert.True(AgentSession.ShouldTakeExtensionSummary(extensionProvidedSummary: true, summarize: true));
        // 扩展给了摘要但用户没要 → 不采纳（TS 的 `result?.summary && options.summarize`）。
        Assert.False(AgentSession.ShouldTakeExtensionSummary(extensionProvidedSummary: true, summarize: false));
        Assert.False(AgentSession.ShouldTakeExtensionSummary(extensionProvidedSummary: false, summarize: true));
        Assert.False(AgentSession.ShouldTakeExtensionSummary(extensionProvidedSummary: false, summarize: false));
    }

    // ---------------------------------------------------------------------
    // navigateTree —— 摘要来源
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveTreeSummarySource_PrefersDefaultSummarizer()
    {
        // 要摘要、有可摘要条目、扩展没给 → 默认摘要器。
        Assert.Equal(
            TreeSummarySource.DefaultSummarizer,
            AgentSession.ResolveTreeSummarySource(summarize: true, entriesToSummarizeCount: 3, hasExtensionSummary: false));

        // 要摘要但没有可摘要条目 → 不生成。
        Assert.Equal(
            TreeSummarySource.None,
            AgentSession.ResolveTreeSummarySource(summarize: true, entriesToSummarizeCount: 0, hasExtensionSummary: false));

        // 扩展给了摘要 → 直接用扩展的，不跑默认摘要器。
        Assert.Equal(
            TreeSummarySource.Extension,
            AgentSession.ResolveTreeSummarySource(summarize: true, entriesToSummarizeCount: 3, hasExtensionSummary: true));

        // 不要摘要 → 什么都不生成。
        Assert.Equal(
            TreeSummarySource.None,
            AgentSession.ResolveTreeSummarySource(summarize: false, entriesToSummarizeCount: 3, hasExtensionSummary: false));
    }

    [Fact]
    public void ResolveTreeSummaryDetails_DefaultsBothListsToEmpty()
    {
        var details = AgentSession.ResolveTreeSummaryDetails(
            new BranchSummaryResult(ReadFiles: ["a.cs"], ModifiedFiles: ["b.cs"]));
        Assert.Equal(["a.cs"], details.ReadFiles);
        Assert.Equal(["b.cs"], details.ModifiedFiles);

        // TS 的 `result.readFiles || []`：两个都缺席时给空列表而不是 null。
        var empty = AgentSession.ResolveTreeSummaryDetails(new BranchSummaryResult());
        Assert.Empty(empty.ReadFiles);
        Assert.Empty(empty.ModifiedFiles);
    }

    // ---------------------------------------------------------------------
    // navigateTree —— 新叶位置
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveTreeNavigationLeaf_UserMessageGoesToParentWithText()
    {
        var entry = UserEntry("u1", "p1", "hello");

        var leaf = AgentSession.ResolveTreeNavigationLeaf(entry, "u1");

        // 用户消息：叶挂到父节点，文本回填编辑器。
        Assert.Equal("p1", leaf.NewLeafId);
        Assert.Equal("hello", leaf.EditorText);
    }

    [Fact]
    public void ResolveTreeNavigationLeaf_RootUserMessageHasNullLeaf()
    {
        var entry = UserEntry("u1", null, "hello");

        var leaf = AgentSession.ResolveTreeNavigationLeaf(entry, "u1");

        Assert.Null(leaf.NewLeafId);
        Assert.Equal("hello", leaf.EditorText);
    }

    [Fact]
    public void ResolveTreeNavigationLeaf_UserMessageJoinsBlocksWithEmptySeparator()
    {
        // TS 传的分隔符是空串（不是默认的 "\n"）。
        var entry = new SessionMessageEntry
        {
            Id = "u1",
            ParentId = "p1",
            Message = new UserMessage(
                [new TextContent("a"), new TextContent("b")], 1),
        };

        var leaf = AgentSession.ResolveTreeNavigationLeaf(entry, "u1");

        Assert.Equal("ab", leaf.EditorText);
    }

    [Fact]
    public void ResolveTreeNavigationLeaf_CustomMessageBehavesLikeUserMessage()
    {
        var leaf = AgentSession.ResolveTreeNavigationLeaf(CustomEntry("c1", "p2", "note text"), "c1");

        Assert.Equal("p2", leaf.NewLeafId);
        Assert.Equal("note text", leaf.EditorText);
    }

    [Fact]
    public void ResolveTreeNavigationLeaf_NonUserEntryBecomesTheLeaf()
    {
        // 助手消息：叶就是目标本身，不回填编辑器。
        var assistant = AgentSession.ResolveTreeNavigationLeaf(AssistantEntry("a1", "p1"), "a1");
        Assert.Equal("a1", assistant.NewLeafId);
        Assert.Null(assistant.EditorText);

        // 其他条目类型（非消息）同样以目标为叶。
        var info = new SessionInfoEntry { Id = "i1", ParentId = "p1", Name = "session name" };
        var other = AgentSession.ResolveTreeNavigationLeaf(info, "i1");
        Assert.Equal("i1", other.NewLeafId);
        Assert.Null(other.EditorText);
    }

    // ---------------------------------------------------------------------
    // navigateTree —— 叶子切换与标签
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveTreeSwitchKind_ThreeBranches()
    {
        // 有摘要 → branchWithSummary（摘要挂在新叶位置，可空为根）。
        Assert.Equal(TreeSwitchKind.BranchWithSummary, AgentSession.ResolveTreeSwitchKind(hasSummaryText: true, "p1"));
        Assert.Equal(TreeSwitchKind.BranchWithSummary, AgentSession.ResolveTreeSwitchKind(hasSummaryText: true, null));

        // 无摘要 + 目标是根 → resetLeaf。
        Assert.Equal(TreeSwitchKind.ResetLeaf, AgentSession.ResolveTreeSwitchKind(hasSummaryText: false, null));

        // 无摘要 + 目标非根 → branch。
        Assert.Equal(TreeSwitchKind.Branch, AgentSession.ResolveTreeSwitchKind(hasSummaryText: false, "p1"));
    }

    [Fact]
    public void LabelPlacement_SummaryEntryVersusTargetEntry()
    {
        // 有摘要 → 标签挂摘要条目，不再挂目标条目。
        Assert.True(AgentSession.ShouldLabelSummaryEntry("lbl", hasSummaryText: true));
        Assert.False(AgentSession.ShouldLabelTargetEntry("lbl", hasSummaryText: true));

        // 无摘要 → 标签挂目标条目。
        Assert.False(AgentSession.ShouldLabelSummaryEntry("lbl", hasSummaryText: false));
        Assert.True(AgentSession.ShouldLabelTargetEntry("lbl", hasSummaryText: false));

        // 没有标签 → 两边都不挂。
        Assert.False(AgentSession.ShouldLabelSummaryEntry(null, hasSummaryText: true));
        Assert.False(AgentSession.ShouldLabelSummaryEntry("", hasSummaryText: true));
        Assert.False(AgentSession.ShouldLabelTargetEntry(null, hasSummaryText: false));
        Assert.False(AgentSession.ShouldLabelTargetEntry("", hasSummaryText: false));
    }

    [Fact]
    public void ResolveTreeEventFromExtension_UndefinedWhenNoSummary()
    {
        Assert.True(AgentSession.ResolveTreeEventFromExtension(hasSummaryText: true, fromExtension: true));
        Assert.False(AgentSession.ResolveTreeEventFromExtension(hasSummaryText: true, fromExtension: false));
        // 没有摘要时该字段是 undefined（端口 null），而不是 false。
        Assert.Null(AgentSession.ResolveTreeEventFromExtension(hasSummaryText: false, fromExtension: true));
        Assert.Null(AgentSession.ResolveTreeEventFromExtension(hasSummaryText: false, fromExtension: false));
    }

    // ---------------------------------------------------------------------
    // getUserMessagesForForking
    // ---------------------------------------------------------------------

    [Fact]
    public void ProjectUserMessagesForForking_KeepsOnlyNonEmptyUserMessages()
    {
        var entries = new List<SessionEntry>
        {
            UserEntry("u1", null, "first"),
            AssistantEntry("a1", "u1"),
            // 空文本的用户消息被跳过（TS 的 `if (text)`）。
            UserEntry("u2", "a1", ""),
            // 纯图片（无文本块）同样跳过。
            new SessionMessageEntry
            {
                Id = "u3",
                ParentId = "u2",
                Message = new UserMessage(MessageContent.BlocksOf([]), 2),
            },
            // 非 message 条目跳过。
            new SessionInfoEntry { Id = "i1", ParentId = "u3", Name = "n" },
            UserEntry("u4", "i1", "second"),
        };

        var result = AgentSession.ProjectUserMessagesForForking(entries);

        Assert.Equal(2, result.Count);
        Assert.Equal("u1", result[0].EntryId);
        Assert.Equal("first", result[0].Text);
        Assert.Equal("u4", result[1].EntryId);
        Assert.Equal("second", result[1].Text);
    }

    [Fact]
    public void ProjectUserMessagesForForking_JoinsBlocksWithEmptySeparator()
    {
        var entries = new List<SessionEntry>
        {
            new SessionMessageEntry
            {
                Id = "u1",
                Message = new UserMessage([new TextContent("x"), new TextContent("y")], 1),
            },
        };

        var result = AgentSession.ProjectUserMessagesForForking(entries);

        Assert.Single(result);
        Assert.Equal("xy", result[0].Text);
    }

    [Fact]
    public void ProjectUserMessagesForForking_EmptyInputGivesEmptyList()
    {
        Assert.Empty(AgentSession.ProjectUserMessagesForForking([]));
    }

    // ---------------------------------------------------------------------
    // exportToHtml —— 主题名解析
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveExportThemeName_OptionWinsOverSettings()
    {
        var known = new HashSet<string>(["dark", "light"], StringComparer.Ordinal);

        // 显式给的名字有效 → 用显式的，即便设置里是另一个有效名字。
        Assert.Equal("dark", AgentSession.ResolveExportThemeName("dark", "light", known.Contains));

        // 显式未给 → 用设置里的。
        Assert.Equal("light", AgentSession.ResolveExportThemeName(null, "light", known.Contains));
    }

    [Fact]
    public void ResolveExportThemeName_FallsBackToSettingsWhenOptionUnknown()
    {
        var known = new HashSet<string>(["light"], StringComparer.Ordinal);

        // 显式给的名字查不到 → 退到设置里的。
        Assert.Equal("light", AgentSession.ResolveExportThemeName("nope", "light", known.Contains));
    }

    [Fact]
    public void ResolveExportThemeName_NullWhenNothingResolves()
    {
        var known = new HashSet<string>(["light"], StringComparer.Ordinal);

        // 两个都查不到 → undefined（导出走默认主题）。
        Assert.Null(AgentSession.ResolveExportThemeName("nope", "alsonope", known.Contains));
        // 显式未给、设置也为空 → undefined。
        Assert.Null(AgentSession.ResolveExportThemeName(null, null, known.Contains));
        Assert.Null(AgentSession.ResolveExportThemeName(null, "alsonope", known.Contains));
    }
}
