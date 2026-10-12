using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 4e-3e-9a：<c>agent-session.ts</c> 的纯逻辑——会话统计、工具装载、消息溯源、提示词归一化。
/// 逐条对照 TS 参考实现（<c>D:\AI\参考项目\pi\packages\coding-agent\src\core\agent-session.ts</c>）。
/// </summary>
public sealed class AgentSessionCoreTests
{
    // ---------------------------------------------------------------------
    // 构造辅助
    // ---------------------------------------------------------------------

    private static ToolSchema Schema() => new(new Dictionary<string, object?>());

    private static ToolDefinition Def(
        string name,
        string? exposure = null,
        bool? defaultActive = null,
        Func<IToolLoadout, ToolLoadoutChanges?>? prepareLoadout = null,
        IReadOnlyList<string>? promptGuidelines = null)
        => new()
        {
            Name = name,
            Label = name,
            Description = $"desc:{name}",
            Parameters = Schema(),
            Execute = (_, _, _, _, _) => Task.FromResult(new AgentToolResult([])),
            Exposure = exposure,
            DefaultActive = defaultActive,
            PrepareLoadout = prepareLoadout,
            PromptGuidelines = promptGuidelines,
        };

    private static ToolDefinitionEntry Entry(string name, string? exposure = null, bool? defaultActive = null,
        Func<IToolLoadout, ToolLoadoutChanges?>? prepareLoadout = null, string path = "builtin:" + "x")
        => new()
        {
            Definition = Def(name, exposure, defaultActive, prepareLoadout),
            SourceInfo = new SourceInfo
            {
                Path = path.Replace("builtin:x", "builtin:" + name),
                Source = "builtin",
                Scope = SourceScope.Temporary,
                Origin = SourceOrigin.TopLevel,
            },
        };

    private static AgentTool Tool(string name, string description = "original")
        => new(name, description, Schema(), name,
            (_, _, _, _) => Task.FromResult(new AgentToolResult([])));

    private static SessionMessageEntry MessageEntry(string id, ChatMessage message)
        => new() { Id = id, Message = message, Timestamp = "2026-01-01T00:00:00.000Z" };

    private static SessionProjection Projection(params ProjectedSessionEntry[] entries)
        => new(entries, [.. entries.SelectMany(entry => entry.Messages)], "medium", null);

    // ---------------------------------------------------------------------
    // 工具装载
    // ---------------------------------------------------------------------

    [Fact]
    public void GetToolExposure_FallsBackToDirectWhenUnregisteredOrUnset()
    {
        var definitions = new Dictionary<string, ToolDefinitionEntry>(StringComparer.Ordinal)
        {
            ["plain"] = Entry("plain"),
            ["hidden"] = Entry("hidden", ToolExposure.Hidden),
            ["modelOnly"] = Entry("modelOnly", ToolExposure.ModelOnly),
        };

        Assert.Equal(ToolExposure.Direct, AgentSession.GetToolExposure(definitions, "plain"));
        Assert.Equal(ToolExposure.Hidden, AgentSession.GetToolExposure(definitions, "hidden"));
        Assert.Equal(ToolExposure.ModelOnly, AgentSession.GetToolExposure(definitions, "modelOnly"));
        Assert.Equal(ToolExposure.Direct, AgentSession.GetToolExposure(definitions, "unregistered"));
    }

    [Fact]
    public void IsAllowedTool_RespectsAllowAndDenyLists()
    {
        var filter = ToolFilter.From(["read", "write"], ["write"]);

        // 无 allowlist 时按 denylist 判定。
        Assert.True(AgentSession.IsAllowedTool("bash", ToolFilter.None));
        Assert.True(AgentSession.IsAllowedTool("read", ToolFilter.From(null, ["write"])));
        Assert.False(AgentSession.IsAllowedTool("write", ToolFilter.From(null, ["write"])));

        // 有 allowlist 时先过白名单，再过黑名单。
        Assert.True(AgentSession.IsAllowedTool("read", ToolFilter.From(["read", "write"], null)));
        Assert.False(AgentSession.IsAllowedTool("bash", ToolFilter.From(["read", "write"], null)));
        Assert.False(AgentSession.IsAllowedTool("write", filter));
    }

    [Fact]
    public void IsAllowedTool_SupportsGlobPatterns()
    {
        // 2026-10-12 基线对齐：TS 的 allow/deny 已从 Set.has 改为模式匹配。
        var filter = ToolFilter.From(["mcp__*"], null);

        Assert.True(AgentSession.IsAllowedTool("mcp__srv__tool", filter));
        Assert.False(AgentSession.IsAllowedTool("read", filter));
    }

    [Fact]
    public void IsAllowedTool_KeepsMcpToolsUnlessAllowlistFiltersMcp()
    {
        // allowlist 是纯内置名单（无 mcp__ 条目）→ MCP 工具保持注册，供 codemode / tool_search。
        var builtinOnly = ToolFilter.From(["read", "bash"], null);
        Assert.True(builtinOnly.AllowlistFiltersMcp is false);
        Assert.True(AgentSession.IsAllowedTool("read", builtinOnly));
        Assert.False(AgentSession.IsAllowedTool("write", builtinOnly));
        Assert.True(AgentSession.IsAllowedTool("mcp__srv__tool", builtinOnly));
        Assert.True(AgentSession.IsAllowedTool("list_mcp_resources", builtinOnly));

        // allowlist 为空（--no-tools）→ 连 MCP 一起过滤。
        var empty = ToolFilter.From([], null);
        Assert.True(empty.AllowlistFiltersMcp);
        Assert.False(AgentSession.IsAllowedTool("mcp__srv__tool", empty));
        Assert.False(AgentSession.IsAllowedTool("read", empty));

        // allowlist 点名了某个 MCP 工具 → 同样连 MCP 一起过滤。
        var namesMcp = ToolFilter.From(["mcp__srv__tool"], null);
        Assert.True(namesMcp.AllowlistFiltersMcp);
        Assert.True(AgentSession.IsAllowedTool("mcp__srv__tool", namesMcp));
        Assert.False(AgentSession.IsAllowedTool("mcp__other__tool", namesMcp));
    }

    [Fact]
    public void IsActivatable_KeepsUnnamedMcpToolsInactiveUnlessToolSearchCanLoadThem()
    {
        var definitions = new Dictionary<string, ToolDefinitionEntry>(StringComparer.Ordinal)
        {
            ["mcp__srv__deferred"] = Entry("mcp__srv__deferred", ToolExposure.Deferred),
            ["mcp__srv__direct"] = Entry("mcp__srv__direct", ToolExposure.Direct),
            ["read"] = Entry("read"),
        };
        var builtinOnly = ToolFilter.From(["read"], null);

        // tool_search 未注册 → 未命名的 MCP 工具不可活跃。
        var withoutToolSearch = new HashSet<string>(["read"], StringComparer.Ordinal);
        Assert.False(AgentSession.IsActivatable("mcp__srv__deferred", builtinOnly, definitions, withoutToolSearch));

        // tool_search 已注册且 exposure 非 direct → 可活跃（由 tool_search 加载）。
        var withToolSearch = new HashSet<string>(["read", "tool_search"], StringComparer.Ordinal);
        Assert.True(AgentSession.IsActivatable("mcp__srv__deferred", builtinOnly, definitions, withToolSearch));

        // exposure 是 direct → 即使 tool_search 在也不可活跃。
        Assert.False(AgentSession.IsActivatable("mcp__srv__direct", builtinOnly, definitions, withToolSearch));

        // 非 MCP 工具与已被 allowlist 命名的工具不受限。
        Assert.True(AgentSession.IsActivatable("read", builtinOnly, definitions, withoutToolSearch));
        Assert.True(AgentSession.IsActivatable("mcp__srv__direct", ToolFilter.None, definitions, withoutToolSearch));
    }

    [Fact]
    public void GetCallableToolNames_IncludesCodemodeAndDeferredButNotModelOnlyOrHidden()
    {
        var definitions = new Dictionary<string, ToolDefinitionEntry>(StringComparer.Ordinal)
        {
            ["direct-active"] = Entry("direct-active"),
            ["direct-idle"] = Entry("direct-idle"),
            ["codemode"] = Entry("codemode", ToolExposure.Codemode),
            ["deferred"] = Entry("deferred", ToolExposure.Deferred),
            ["model-only"] = Entry("model-only", ToolExposure.ModelOnly),
            ["hidden"] = Entry("hidden", ToolExposure.Hidden),
        };

        var callable = AgentSession.GetCallableToolNames(
            definitions.Keys,
            definitions,
            new HashSet<string>(["direct-active"], StringComparer.Ordinal));

        // 注册顺序保留：codemode / deferred 恒可调用，direct 仅在活跃时可调用。
        Assert.Equal(["direct-active", "codemode", "deferred"], callable);
    }

    [Fact]
    public void ApplyToolLoadout_DeduplicatesAndDropsHiddenExposure()
    {
        var registry = new Dictionary<string, AgentTool>(StringComparer.Ordinal)
        {
            ["read"] = Tool("read"),
            ["hidden"] = Tool("hidden"),
        };
        var definitions = new Dictionary<string, ToolDefinitionEntry>(StringComparer.Ordinal)
        {
            ["read"] = Entry("read"),
            ["hidden"] = Entry("hidden", ToolExposure.Hidden),
        };

        var result = AgentSession.ApplyToolLoadout(
            ["read", "read", "hidden", "unregistered"], registry, definitions);

        var declared = Assert.Single(result.Declared);
        Assert.Equal("read", declared.Name);
        Assert.Empty(result.HiddenDeclarations);
    }

    [Fact]
    public void ApplyToolLoadout_AppliesPrepareLoadoutDescriptionsAndHiddenDeclarations()
    {
        var registry = new Dictionary<string, AgentTool>(StringComparer.Ordinal)
        {
            ["read"] = Tool("read"),
            ["bash"] = Tool("bash"),
            ["helper"] = Tool("helper"),
        };
        var definitions = new Dictionary<string, ToolDefinitionEntry>(StringComparer.Ordinal)
        {
            ["read"] = Entry("read", prepareLoadout: loadout =>
            {
                // 钩子看到的是「原描述」的声明集，以及全量注册集。
                Assert.Equal(["read", "bash"], loadout.Declared.Select(tool => tool.Name));
                Assert.Equal(3, loadout.Registered.Count);
                Assert.Equal(ToolExposure.Direct, loadout.GetExposure("read"));
                // callable = 活跃的 direct 工具（helper 未活跃故不在内）。
                Assert.Equal(["read", "bash"], loadout.Callable.Select(tool => tool.Name));
                return new ToolLoadoutChanges
                {
                    Descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["read"] = "overridden",
                    },
                    HiddenDeclarations = ["bash"],
                };
            }),
            ["bash"] = Entry("bash"),
        };

        var result = AgentSession.ApplyToolLoadout(["read", "bash"], registry, definitions);

        Assert.Equal(["read", "bash"], result.Declared.Select(tool => tool.Name));
        Assert.Equal("overridden", result.Declared[0].Description);
        // 未被覆盖的工具保持原描述。
        Assert.Equal("original", result.Declared[1].Description);
        Assert.Contains("bash", result.HiddenDeclarations);
    }

    [Fact]
    public void ApplyToolLoadout_ReportsHookErrorsAndKeepsOtherTools()
    {
        var registry = new Dictionary<string, AgentTool>(StringComparer.Ordinal) { ["boom"] = Tool("boom") };
        var definitions = new Dictionary<string, ToolDefinitionEntry>(StringComparer.Ordinal)
        {
            ["boom"] = Entry("boom", prepareLoadout: _ => throw new InvalidOperationException("kaboom")),
        };
        var errors = new List<LoadoutHookError>();

        var result = AgentSession.ApplyToolLoadout(["boom"], registry, definitions, onHookError: errors.Add);

        var error = Assert.Single(errors);
        Assert.Equal("builtin:boom", error.ExtensionPath);
        Assert.Equal("prepare_loadout", error.Event);
        Assert.Equal("kaboom", error.Error);
        Assert.Single(result.Declared);
        Assert.Empty(result.HiddenDeclarations);
    }

    [Fact]
    public void IsDeclarable_And_IsActivatedOnRegistration()
    {
        var definitions = new Dictionary<string, ToolDefinitionEntry>(StringComparer.Ordinal)
        {
            ["direct"] = Entry("direct"),
            ["model-only"] = Entry("model-only", ToolExposure.ModelOnly),
            ["codemode"] = Entry("codemode", ToolExposure.Codemode),
            ["direct-inactive"] = Entry("direct-inactive", defaultActive: false),
        };

        Assert.True(AgentSession.IsDeclarable(definitions, "direct"));
        Assert.True(AgentSession.IsDeclarable(definitions, "model-only"));
        Assert.False(AgentSession.IsDeclarable(definitions, "codemode"));

        Assert.True(AgentSession.IsActivatedOnRegistration(definitions, "direct"));
        Assert.True(AgentSession.IsActivatedOnRegistration(definitions, "model-only"));
        Assert.False(AgentSession.IsActivatedOnRegistration(definitions, "codemode"));
        // defaultActive 显式为 false 时不随注册激活（TS `!== false` 判定）。
        Assert.False(AgentSession.IsActivatedOnRegistration(definitions, "direct-inactive"));
    }

    // ---------------------------------------------------------------------
    // 会话统计
    // ---------------------------------------------------------------------

    [Fact]
    public void GetSessionStats_AggregatesUsageAcrossEntryKindsAndCountsMessages()
    {
        var entries = new List<SessionEntry>
        {
            new UsageEntry
            {
                Id = "u1",
                Kind = "cache_warm",
                Provider = "anthropic",
                Model = "claude",
                Usage = new Usage(10, 5, 1, 2),
            },
            new BranchSummaryEntry
            {
                Id = "b1",
                FromId = "x",
                Summary = "s",
                UsageStats = new Usage(7, 3),
            },
            new CompactionEntry
            {
                Id = "c1",
                Summary = "s",
                TokensBefore = 100,
                UsageStats = new Usage(2, 1),
            },
            MessageEntry("m1", new UserMessage([new TextContent("hi")], 1)),
            MessageEntry("m2", new AssistantMessage(
                [new TextContent("..."), new ToolCallContent("call-1", "read", null)],
                UsageStats: new Usage(100, 20, 3, 4))),
            MessageEntry("m3", new ToolResultMessage("call-1", "read", [new TextContent("ok")])
            {
                UsageStats = new Usage(1, 1),
            }),
            // 非消息条目不计入消息数。
            new ModelChangeEntry { Id = "mc", Provider = "anthropic", ModelId = "claude" },
        };

        var stats = AgentSession.GetSessionStats(entries, "/tmp/s.jsonl", "session-1", contextUsage: null);

        Assert.Equal("/tmp/s.jsonl", stats.SessionFile);
        Assert.Equal("session-1", stats.SessionId);
        Assert.Equal(1, stats.UserMessages);
        Assert.Equal(1, stats.AssistantMessages);
        Assert.Equal(1, stats.ToolResults);
        Assert.Equal(1, stats.ToolCalls);
        Assert.Equal(3, stats.TotalMessages);
        Assert.Equal(10 + 7 + 2 + 100 + 1, stats.Tokens.Input);
        Assert.Equal(5 + 3 + 1 + 20 + 1, stats.Tokens.Output);
        Assert.Equal(1 + 3, stats.Tokens.CacheRead);
        Assert.Equal(2 + 4, stats.Tokens.CacheWrite);
        Assert.Equal(
            stats.Tokens.Input + stats.Tokens.Output + stats.Tokens.CacheRead + stats.Tokens.CacheWrite,
            stats.Tokens.Total);
        Assert.Null(stats.ContextUsage);
    }

    [Fact]
    public void GetSessionStats_IgnoresCustomMessagesAndAbortedAssistantUsage()
    {
        var entries = new List<SessionEntry>
        {
            new CustomMessageEntry
            {
                Id = "cm",
                CustomType = "note",
                Content = new MessageContent.Text("x"),
                Display = true,
            },
            MessageEntry("m1", new AssistantMessage(
                [new TextContent("")], StopReason.Aborted, UsageStats: new Usage(9, 9))),
        };

        var stats = AgentSession.GetSessionStats(entries, null, "s", null);

        // TS 的 role 分支只认 user / toolResult / assistant；assistant 即使 aborted 也计入消息数。
        Assert.Equal(1, stats.TotalMessages);
        Assert.Equal(1, stats.AssistantMessages);
        Assert.Equal(0, stats.UserMessages);
        Assert.Equal(0, stats.ToolResults);
        Assert.Equal(9, stats.Tokens.Input);
    }

    // ---------------------------------------------------------------------
    // 上下文用量
    // ---------------------------------------------------------------------

    [Fact]
    public void GetContextUsage_ReturnsNullWhenContextWindowUnknown()
    {
        var projection = Projection();
        Assert.Null(AgentSession.GetContextUsage(0, projection, []));
        Assert.Null(AgentSession.GetContextUsage(-1, projection, []));
    }

    [Fact]
    public void GetContextUsage_ReportsUnknownTokensWhenNoUsageAfterLatestCompaction()
    {
        var compaction = new CompactionEntry
        {
            Id = "c1",
            Summary = "s",
            TokensBefore = 10,
            Timestamp = "2026-01-01T00:00:00.000Z",
        };
        var branch = new List<SessionEntry> { compaction };
        var projection = Projection();

        var usage = AgentSession.GetContextUsage(200_000, projection, branch);

        Assert.NotNull(usage);
        Assert.Null(usage!.Tokens);
        Assert.Equal(200_000d, usage.ContextWindow);
        Assert.Null(usage.Percent);
    }

    [Fact]
    public void GetContextUsage_UsesPostCompactionUsageWhenPresent()
    {
        var compaction = new CompactionEntry
        {
            Id = "c1",
            Summary = "s",
            TokensBefore = 10,
            Timestamp = "2026-01-01T00:00:00.000Z",
        };
        var assistant = new AssistantMessage(
            [new TextContent("answer")], UsageStats: new Usage(1_000, 0, 0, 0));
        var assistantEntry = MessageEntry("m1", assistant);
        var branch = new List<SessionEntry> { compaction, assistantEntry };
        var projection = Projection(new ProjectedSessionEntry(assistantEntry, [assistant]));

        var usage = AgentSession.GetContextUsage(10_000, projection, branch);

        Assert.NotNull(usage);
        Assert.Equal(1_000d, usage!.Tokens!.Value);
        Assert.Equal(10_000d, usage.ContextWindow);
        Assert.Equal(10.0, usage.Percent);
    }

    [Fact]
    public void GetContextUsage_EstimatesFromProjectionWithoutCompaction()
    {
        var user = new UserMessage([new TextContent("hello world")], 1);
        var userEntry = MessageEntry("m1", user);
        var branch = new List<SessionEntry> { userEntry };
        var projection = Projection(new ProjectedSessionEntry(userEntry, [user]));

        var usage = AgentSession.GetContextUsage(1_000, projection, branch);

        Assert.NotNull(usage);
        var expected = AgentSession.EstimateMessagesTokens([user]);
        Assert.Equal((double)expected, usage!.Tokens!.Value);
        Assert.Equal((double)expected / 1_000 * 100, usage.Percent);
    }

    [Fact]
    public void EstimateMessagesTokens_SumsPerMessageEstimates()
    {
        var a = new UserMessage([new TextContent("aaaa")], 1);
        var b = new UserMessage([new TextContent("bbbbbbbb")], 2);

        Assert.Equal(
            Compaction.EstimateTokens(a) + Compaction.EstimateTokens(b),
            AgentSession.EstimateMessagesTokens([a, b]));
        Assert.Equal(0, AgentSession.EstimateMessagesTokens([]));
    }

    // ---------------------------------------------------------------------
    // 消息溯源
    // ---------------------------------------------------------------------

    [Fact]
    public void FindPersistedMessageEntryId_PrefersMemoThenBranchThenProjection()
    {
        var message = new UserMessage([new TextContent("hi")], 1);
        var entry = MessageEntry("m1", message);
        var projection = Projection(new ProjectedSessionEntry(entry, [message]));

        // 记忆表命中时不再查分支。
        Assert.Equal("memo", AgentSession.FindPersistedMessageEntryId(
            message, "memo", [], [], projection));

        // 分支反查命中。
        Assert.Equal("m1", AgentSession.FindPersistedMessageEntryId(
            message, null, [entry], [message], projection));

        // 分支未命中时按投影下标对齐。
        var other = new UserMessage([new TextContent("other")], 2);
        var otherEntry = MessageEntry("m2", other);
        var twoMessageProjection = Projection(
            new ProjectedSessionEntry(entry, [message]),
            new ProjectedSessionEntry(otherEntry, [other]));
        Assert.Equal("m2", AgentSession.FindPersistedMessageEntryId(
            other, null, [], [message, other], twoMessageProjection));

        // agent state 里没有该消息 → null。
        Assert.Null(AgentSession.FindPersistedMessageEntryId(
            other, null, [], [], twoMessageProjection));
    }

    [Fact]
    public void FindPersistedMessageEntryId_UsesReferenceIdentityNotValueEquality()
    {
        // 内容完全相同的两条消息：TS 用 `===`，C# record 默认值相等，故必须按引用判定。
        var first = new UserMessage([new TextContent("same")], 1);
        var second = new UserMessage([new TextContent("same")], 1);
        var firstEntry = MessageEntry("m1", first);
        var secondEntry = MessageEntry("m2", second);
        var projection = Projection(
            new ProjectedSessionEntry(firstEntry, [first]),
            new ProjectedSessionEntry(secondEntry, [second]));

        Assert.Equal("m2", AgentSession.FindPersistedMessageEntryId(
            second, null, [firstEntry, secondEntry], [first, second], projection));
    }

    [Fact]
    public void FindPersistedMessageEntryId_TreatsEmptyMemoAsMiss()
    {
        // TS `if (mapped)` 是 JS 真值判定：空串视为未命中。
        var message = new UserMessage([new TextContent("hi")], 1);
        var entry = MessageEntry("m1", message);

        Assert.Equal("m1", AgentSession.FindPersistedMessageEntryId(
            message, "", [entry], [message], Projection()));
    }

    [Fact]
    public void HasUnresolvedProjectedTarget_FlagsProjectedMessageWithoutSourceEntry()
    {
        var message = new UserMessage([new TextContent("hi")], 1);

        Assert.True(AgentSession.HasUnresolvedProjectedTarget([null], [message], [message]));
        Assert.False(AgentSession.HasUnresolvedProjectedTarget(["m1"], [message], [message]));
        // 不在 agent state 里 → 不算未解析。
        Assert.False(AgentSession.HasUnresolvedProjectedTarget([null], [message], []));
    }

    // ---------------------------------------------------------------------
    // 提示词归一化
    // ---------------------------------------------------------------------

    [Fact]
    public void NormalizePromptSnippet_CollapsesWhitespaceAndRejectsBlank()
    {
        Assert.Null(AgentSession.NormalizePromptSnippet(null));
        Assert.Null(AgentSession.NormalizePromptSnippet(""));
        Assert.Null(AgentSession.NormalizePromptSnippet("   \n\t "));
        Assert.Equal("a b c", AgentSession.NormalizePromptSnippet("  a\r\n b\t\tc  "));
        Assert.Equal("a b", AgentSession.NormalizePromptSnippet("a\n\n\nb"));
    }

    [Fact]
    public void NormalizePromptGuidelines_TrimsDropsBlankAndDeduplicates()
    {
        Assert.Empty(AgentSession.NormalizePromptGuidelines(null));
        Assert.Empty(AgentSession.NormalizePromptGuidelines([]));
        Assert.Equal(
            ["a", "b"],
            AgentSession.NormalizePromptGuidelines([" a ", "", "b", "a", "   "]));
    }
}
