using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <c>core/sdk.ts</c> 的工具选择、装配叶子与 <c>core/mcp-servers.ts</c> 的工具名匹配。
/// 2026-10-12 基线对齐后重写（原版混用了两个 TS 修订版，见 <see cref="Sdk"/> 的 remarks）；
/// 4e-3e-9f 追加 <c>createAgentSession</c> 的装配叶子用例；
/// 2026-10-16（R2 漂移 <c>ddaa0a034</c>）把「<c>tools</c> 支持 <c>+name</c>/<c>-name</c> 修饰符」
/// 的用例改回上游语义（旧断言「tools 是纯 allowlist」已随漂移作废）。
/// </summary>
public sealed class SdkTests
{
    // ---------------------------------------------------------------------
    // McpServers.CreateToolNameMatcher（TS 里定义在 mcp-servers.ts）
    // ---------------------------------------------------------------------

    [Fact]
    public void CreateToolNameMatcher_MatchesExactAndGlob()
    {
        var match = McpServers.CreateToolNameMatcher(["read", "mcp__*"]);
        Assert.True(match("read"));
        Assert.True(match("mcp__server__tool"));
        Assert.False(match("bash"));
    }

    [Fact]
    public void CreateToolNameMatcher_GlobIsAnchored()
    {
        var match = McpServers.CreateToolNameMatcher(["mcp__srv__*"]);

        Assert.True(match("mcp__srv__a"));
        // 前缀不同 → 不命中（^…$ 锚定）。
        Assert.False(match("xmcp__srv__a"));
        // `*` 跨任意字符，含 `__`。
        Assert.True(match("mcp__srv__a__b"));
    }

    [Fact]
    public void CreateToolNameMatcher_EmptyListMatchesNothing()
    {
        var match = McpServers.CreateToolNameMatcher([]);
        Assert.False(match("read"));
        Assert.False(match(""));
    }

    [Fact]
    public void IsMcpToolName_CoversServerToolsAndResourceTools()
    {
        Assert.True(McpServers.IsMcpToolName("mcp__srv__tool"));
        Assert.True(McpServers.IsMcpToolName("list_mcp_resources"));
        Assert.True(McpServers.IsMcpToolName("list_mcp_resource_templates"));
        Assert.True(McpServers.IsMcpToolName("read_mcp_resource"));

        Assert.False(McpServers.IsMcpToolName("read"));
        // 前缀必须正好是 mcp__（`mcp_` 单下划线不算）。
        Assert.False(McpServers.IsMcpToolName("mcp_srv"));
    }

    // ---------------------------------------------------------------------
    // ResolveSessionTools（对应 createAgentSession 的 4 行）
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveSessionTools_NoOptionsUsesDefaultToolsAndNoAllowlist()
    {
        var resolved = Sdk.ResolveSessionTools(new CreateAgentSessionOptions());

        Assert.Equal(SettingsManager.DefaultToolNames, resolved.InitialActiveToolNames);
        // 没有 allowlist → null（不是空列表）。
        Assert.Null(resolved.AllowedToolNames);
        Assert.Null(resolved.ExcludedToolNames);
        Assert.True(resolved.UsesDefaultTools);
    }

    [Fact]
    public void ResolveSessionTools_UsesConfiguredDefaultToolsWhenPresent()
    {
        var resolved = Sdk.ResolveSessionTools(
            new CreateAgentSessionOptions(),
            settingsDefaultTools: ["read", "grep"]);

        Assert.Equal(["read", "grep"], resolved.InitialActiveToolNames);
        Assert.True(resolved.UsesDefaultTools);
    }

    [Fact]
    public void ResolveSessionTools_AllowlistLocksActiveSet()
    {
        var resolved = Sdk.ResolveSessionTools(new CreateAgentSessionOptions { Tools = ["read", "bash"] });

        Assert.Equal(["read", "bash"], resolved.InitialActiveToolNames);
        Assert.Equal(["read", "bash"], resolved.AllowedToolNames);
        Assert.False(resolved.UsesDefaultTools);
    }

    [Fact]
    public void ResolveSessionTools_ModifierListAppliesToDefaultTools()
    {
        // 2026-10-16（R2 漂移 ddaa0a034）：`tools` 全为 +name/-name 时**不是** allowlist，
        // 而是叠加在 defaultTools 之上的选择变更。
        var resolved = Sdk.ResolveSessionTools(new CreateAgentSessionOptions { Tools = ["-bash", "+grep"] });

        Assert.Equal(["read", "edit", "write", "grep"], resolved.InitialActiveToolNames);
        // 不是 allowlist：没有 allowlist 约束（noTools 不是 "all"）。
        Assert.Null(resolved.AllowedToolNames);
        Assert.False(resolved.UsesDefaultTools);
        Assert.Equal(["-bash", "+grep"], resolved.DefaultToolModifiers);
    }

    [Fact]
    public void ResolveSessionTools_ModifierListStacksOnConfiguredDefaultTools()
    {
        var resolved = Sdk.ResolveSessionTools(
            new CreateAgentSessionOptions { Tools = ["+grep"] },
            settingsDefaultTools: ["read", "bash"]);

        Assert.Equal(["read", "bash", "grep"], resolved.InitialActiveToolNames);
        Assert.Null(resolved.AllowedToolNames);
    }

    [Fact]
    public void ResolveSessionTools_MixingNamesAndModifiersThrows()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            Sdk.ResolveSessionTools(new CreateAgentSessionOptions { Tools = ["read", "+grep"] }));

        Assert.Contains(
            "Invalid tools option: tool names cannot be mixed with +name or -name entries",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSessionTools_ModifierPatternIsRejected()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            Sdk.ResolveSessionTools(new CreateAgentSessionOptions { Tools = ["+mc*"] }));

        Assert.Contains(
            "Invalid tools option: +name and -name entries take exact tool names, not patterns: +mc*",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSessionTools_ModifiersWithNoToolsAllKeepSelectedSetAsAllowlist()
    {
        var resolved = Sdk.ResolveSessionTools(
            new CreateAgentSessionOptions { Tools = ["+grep"], NoTools = NoToolsMode.All });

        Assert.Equal(["grep"], resolved.InitialActiveToolNames);
        Assert.Equal(["grep"], resolved.AllowedToolNames);
    }

    [Fact]
    public void ResolveSessionTools_PlainListStaysAnAllowlist()
    {
        var resolved = Sdk.ResolveSessionTools(new CreateAgentSessionOptions { Tools = ["read", "mcp__*"] });

        Assert.Equal(["read", "mcp__*"], resolved.InitialActiveToolNames);
        Assert.Equal(["read", "mcp__*"], resolved.AllowedToolNames);
        Assert.Null(resolved.DefaultToolModifiers);
    }

    [Fact]
    public void ResolveSessionTools_NoToolsAllGivesEmptyAllowlist()
    {
        var resolved = Sdk.ResolveSessionTools(
            new CreateAgentSessionOptions { NoTools = NoToolsMode.All });

        // "all" → allowedToolNames 是**空列表**（会走 _refreshToolRegistry 的 allowlist 分支）。
        Assert.Empty(resolved.InitialActiveToolNames);
        Assert.NotNull(resolved.AllowedToolNames);
        Assert.Empty(resolved.AllowedToolNames!);
        Assert.False(resolved.UsesDefaultTools);
    }

    [Fact]
    public void ResolveSessionTools_NoToolsBuiltinKeepsExtensionToolsEnabled()
    {
        // "builtin" → allowedToolNames 是 **undefined**（不设 allowlist），
        // 于是扩展 / 自定义工具仍可被激活。旧端口用 bool 把两者压成一样，属真实偏差。
        var resolved = Sdk.ResolveSessionTools(
            new CreateAgentSessionOptions { NoTools = NoToolsMode.Builtin });

        Assert.Empty(resolved.InitialActiveToolNames);
        Assert.Null(resolved.AllowedToolNames);
        Assert.False(resolved.UsesDefaultTools);
    }

    [Fact]
    public void ResolveSessionTools_ExcludeToolsFiltersWithGlob()
    {
        var resolved = Sdk.ResolveSessionTools(new CreateAgentSessionOptions { ExcludeTools = ["ba*"] });

        Assert.DoesNotContain("bash", resolved.InitialActiveToolNames);
        Assert.Contains("read", resolved.InitialActiveToolNames);
        Assert.Equal(["ba*"], resolved.ExcludedToolNames);
        // denylist 不影响 allowlist。
        Assert.Null(resolved.AllowedToolNames);
    }

    [Fact]
    public void ResolveSessionTools_ExcludeToolsAppliesAfterAllowlist()
    {
        var resolved = Sdk.ResolveSessionTools(new CreateAgentSessionOptions
        {
            Tools = ["read", "bash", "edit"],
            ExcludeTools = ["bash"],
        });

        Assert.Equal(["read", "edit"], resolved.InitialActiveToolNames);
        Assert.Equal(["read", "bash", "edit"], resolved.AllowedToolNames);
    }

    // ---------------------------------------------------------------------
    // createAgentSession 装配叶子（4e-3e-9f）
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveSessionCwd_PrefersOptionsThenSessionManagerThenProcess()
    {
        Assert.Equal(
            Paths.ResolvePath("/opt"),
            Sdk.ResolveSessionCwd("/opt", "/sess", "/proc"));
        Assert.Equal(
            Paths.ResolvePath("/sess"),
            Sdk.ResolveSessionCwd(null, "/sess", "/proc"));
        Assert.Equal(
            Paths.ResolvePath("/proc"),
            Sdk.ResolveSessionCwd(null, null, "/proc"));
    }

    [Fact]
    public void ResolveSessionAgentDir_ResolvesOnlyWhenProvided()
    {
        Assert.Equal(Paths.ResolvePath("/explicit"), Sdk.ResolveSessionAgentDir("/explicit", "/default"));
        Assert.Equal("/default", Sdk.ResolveSessionAgentDir(null, "/default"));
    }

    [Fact]
    public void ResolveSessionModel_RestoresWhenAuthIsConfigured()
    {
        var runtime = new FakeModelResolverRuntime(Models("anthropic"), configuredProviders: ["anthropic"]);

        var result = Sdk.ResolveSessionModel(
            explicitModel: null,
            hasExistingSession: true,
            sessionModel: new BranchModelSelection("anthropic", "claude-opus-4-5"),
            modelRuntime: runtime);

        Assert.Equal("claude-opus-4-5", result.Model!.Id);
        Assert.Null(result.FallbackMessage);
    }

    [Fact]
    public void ResolveSessionModel_ReportsFallbackWhenAuthMissing()
    {
        var runtime = new FakeModelResolverRuntime(Models("anthropic"), configuredProviders: []);

        var result = Sdk.ResolveSessionModel(
            explicitModel: null,
            hasExistingSession: true,
            sessionModel: new BranchModelSelection("anthropic", "claude-opus-4-5"),
            modelRuntime: runtime);

        Assert.Null(result.Model);
        Assert.Equal("Could not restore model anthropic/claude-opus-4-5", result.FallbackMessage);
    }

    [Fact]
    public void ResolveSessionModel_SkipsRestoreWithoutExistingSession()
    {
        var runtime = new FakeModelResolverRuntime(Models("anthropic"), configuredProviders: ["anthropic"]);

        var result = Sdk.ResolveSessionModel(
            explicitModel: null,
            hasExistingSession: false,
            sessionModel: new BranchModelSelection("anthropic", "claude-opus-4-5"),
            modelRuntime: runtime);

        Assert.Null(result.Model);
        Assert.Null(result.FallbackMessage);
    }

    [Fact]
    public void ResolveSessionModel_ExplicitModelWins()
    {
        var explicitModel = Models("openai")[0];
        var runtime = new FakeModelResolverRuntime(Models("anthropic"), configuredProviders: ["anthropic"]);

        var result = Sdk.ResolveSessionModel(
            explicitModel,
            hasExistingSession: true,
            sessionModel: new BranchModelSelection("anthropic", "claude-opus-4-5"),
            modelRuntime: runtime);

        Assert.Same(explicitModel, result.Model);
        Assert.Null(result.FallbackMessage);
    }

    [Fact]
    public void AppendFallbackModel_AppendsOnlyForNonEmpty()
    {
        var model = Models("anthropic")[0];

        Assert.Equal(
            "Could not restore model anthropic/x. Using anthropic/claude-opus-4-5",
            Sdk.AppendFallbackModel("Could not restore model anthropic/x", model));

        // JS 的真值判断：空串与 null 都不追加。
        Assert.Equal("", Sdk.AppendFallbackModel("", model));
        Assert.Null(Sdk.AppendFallbackModel(null, model));
    }

    [Fact]
    public void HasThinkingLevelChangeEntry_DetectsTheEntry()
    {
        Assert.False(Sdk.HasThinkingLevelChangeEntry([]));
        Assert.False(Sdk.HasThinkingLevelChangeEntry(
            [new ModelChangeEntry { Provider = "anthropic", ModelId = "x" }]));
        Assert.True(Sdk.HasThinkingLevelChangeEntry(
            [new ThinkingLevelChangeEntry { ThinkingLevel = "high" }]));
    }

    [Fact]
    public void ResolveThinkingLevel_ExplicitWins()
    {
        var model = Models("anthropic")[0];

        var level = Sdk.ResolveThinkingLevel(
            ThinkingLevel.High,
            hasExistingSession: true,
            hasThinkingEntry: true,
            existingSessionThinkingLevel: "low",
            settingsDefaultThinkingLevel: "low",
            perModelThinkingLevel: "low",
            model);

        Assert.Equal(ThinkingLevel.High, level);
    }

    [Fact]
    public void ResolveThinkingLevel_RestoresFromSessionWhenThinkingEntryPresent()
    {
        var model = Models("anthropic")[0];

        var level = Sdk.ResolveThinkingLevel(
            null, true, hasThinkingEntry: true, existingSessionThinkingLevel: "high",
            settingsDefaultThinkingLevel: "low", perModelThinkingLevel: null, model);

        Assert.Equal(ThinkingLevel.High, level);
    }

    [Fact]
    public void ResolveThinkingLevel_UsesSettingsDefaultWhenNoThinkingEntry()
    {
        var model = Models("anthropic")[0];

        var level = Sdk.ResolveThinkingLevel(
            null, true, hasThinkingEntry: false, existingSessionThinkingLevel: "high",
            settingsDefaultThinkingLevel: "low", perModelThinkingLevel: null, model);

        Assert.Equal(ThinkingLevel.Low, level);
    }

    [Fact]
    public void ResolveThinkingLevel_PerModelOverrideBeatsGlobalDefault()
    {
        var model = Models("anthropic")[0];

        var level = Sdk.ResolveThinkingLevel(
            null, false, false, null,
            settingsDefaultThinkingLevel: "low", perModelThinkingLevel: "high", model);

        Assert.Equal(ThinkingLevel.High, level);
    }

    [Fact]
    public void ResolveThinkingLevel_FallsBackToBuiltInDefault()
    {
        var model = Models("anthropic")[0];

        var level = Sdk.ResolveThinkingLevel(null, false, false, null, null, null, model);

        Assert.Equal(Defaults.DefaultThinkingLevel, level);
    }

    [Fact]
    public void ResolveThinkingLevel_WithoutModelIsOff()
    {
        var level = Sdk.ResolveThinkingLevel(null, false, false, null, "high", null, model: null);

        Assert.Equal(ThinkingLevel.Off, level);
    }

    [Fact]
    public void ResolveThinkingLevel_ClampsToModelSupport()
    {
        // levelMap 显式把 high 置 null → 禁用；向上无可用档位，回落 medium。
        var model = ReasoningModelWithMap(new JsonObject { ["high"] = null });

        var level = Sdk.ResolveThinkingLevel(null, false, false, null, "high", null, model);

        Assert.Equal(ThinkingLevel.Medium, level);
    }

    [Fact]
    public void FilterBlockedImages_ReplacesImagesAndDedupesConsecutivePlaceholders()    {
        var messages = new ChatMessage[]
        {
            new UserMessage(
                MessageContent.BlocksOf(
                [
                    new TextContent("look"),
                    new ImageContent("AAAA", "image/png"),
                    new ImageContent("BBBB", "image/png"),
                ]),
                0),
        };

        var filtered = Sdk.FilterBlockedImages(messages, blockImages: true);

        var user = Assert.IsType<UserMessage>(Assert.Single(filtered));
        var blocks = Assert.IsType<MessageContent.Blocks>(user.Content).Value;
        // 两张相邻图片 → 一个占位（第二张被去重）。
        Assert.Equal(2, blocks.Count);
        Assert.Equal("look", Assert.IsType<TextContent>(blocks[0]).Text);
        Assert.Equal(Sdk.ImageReadingDisabledText, Assert.IsType<TextContent>(blocks[1]).Text);
    }

    [Fact]
    public void FilterBlockedImages_KeepsNonConsecutivePlaceholders()
    {
        var messages = new ChatMessage[]
        {
            new ToolResultMessage(
                "call-1",
                "read",
                [
                    new ImageContent("AAAA", "image/png"),
                    new TextContent("between"),
                    new ImageContent("BBBB", "image/png"),
                ]),
        };

        var filtered = Sdk.FilterBlockedImages(messages, blockImages: true);

        var toolResult = Assert.IsType<ToolResultMessage>(Assert.Single(filtered));
        Assert.Equal(3, toolResult.Content.Count);
        Assert.Equal(Sdk.ImageReadingDisabledText, Assert.IsType<TextContent>(toolResult.Content[0]).Text);
        Assert.Equal("between", Assert.IsType<TextContent>(toolResult.Content[1]).Text);
        Assert.Equal(Sdk.ImageReadingDisabledText, Assert.IsType<TextContent>(toolResult.Content[2]).Text);
    }

    [Fact]
    public void FilterBlockedImages_IsNoOpWhenDisabled()
    {
        var messages = new ChatMessage[]
        {
            new UserMessage(MessageContent.BlocksOf([new ImageContent("AAAA", "image/png")]), 0),
        };

        var filtered = Sdk.FilterBlockedImages(messages, blockImages: false);

        Assert.Same(messages, filtered);
    }

    [Fact]
    public void FilterBlockedImages_LeavesImageFreeMessagesIdentical()
    {
        var textOnly = new UserMessage("hello", 0);
        var toolResult = new ToolResultMessage("call-1", "read", [new TextContent("out")]);

        var filtered = Sdk.FilterBlockedImages([textOnly, toolResult], blockImages: true);

        // TS 只在 hasImages 时重建对象；无图片时返回同一个实例。
        Assert.Same(textOnly, filtered[0]);
        Assert.Same(toolResult, filtered[1]);
    }

    [Fact]
    public void ConvertToLlmWithBlockImages_ConvertsThenFilters()
    {
        var messages = new ChatMessage[]
        {
            // bashExecution 会被 convertToLlm 折成 user 文本消息（无图片，不受影响）。
            new BashExecutionMessage("ls", "out", 0, false, false, 0),
            new UserMessage(MessageContent.BlocksOf([new ImageContent("AAAA", "image/png")]), 1),
        };

        var converted = Sdk.ConvertToLlmWithBlockImages(messages, blockImages: true);

        Assert.Equal(2, converted.Count);
        var user = Assert.IsType<UserMessage>(converted[1]);
        var blocks = Assert.IsType<MessageContent.Blocks>(user.Content).Value;
        Assert.Equal(Sdk.ImageReadingDisabledText, Assert.IsType<TextContent>(Assert.Single(blocks)).Text);
    }

    // ---------------------------------------------------------------------
    // buildRequestOptions 的超时 / 重试解析
    // ---------------------------------------------------------------------

    [Fact]
    public void ResolveRequestTimeouts_OptionsWinOverSettings()
    {
        var resolved = Sdk.ResolveRequestTimeouts(
            optionsTimeoutMs: 1_000,
            optionsWebsocketConnectTimeoutMs: 2_000,
            optionsMaxRetries: 5,
            optionsMaxRetryDelayMs: 9_000,
            providerRetrySettings: (30_000, 3, 60_000),
            httpIdleTimeoutMs: 120_000,
            websocketConnectTimeoutMs: 7_000);

        Assert.Equal(1_000, resolved.TimeoutMs);
        Assert.Equal(2_000, resolved.WebsocketConnectTimeoutMs);
        Assert.Equal(5, resolved.MaxRetries);
        Assert.Equal(9_000, resolved.MaxRetryDelayMs);
    }

    [Fact]
    public void ResolveRequestTimeouts_FallsBackToProviderRetrySettings()
    {
        var resolved = Sdk.ResolveRequestTimeouts(
            null, null, null, null,
            providerRetrySettings: (30_000, 3, 60_000),
            httpIdleTimeoutMs: 120_000,
            websocketConnectTimeoutMs: 7_000);

        Assert.Equal(30_000, resolved.TimeoutMs);
        Assert.Equal(3, resolved.MaxRetries);
        // maxRetryDelayMs 在 settings 侧恒有值（缺省 60000），所以不会落到 HTTP 空闲超时。
        Assert.Equal(60_000, resolved.MaxRetryDelayMs);
        Assert.Equal(7_000, resolved.WebsocketConnectTimeoutMs);
    }

    [Fact]
    public void ResolveRequestTimeouts_FallsBackToHttpIdleTimeout()
    {
        var resolved = Sdk.ResolveRequestTimeouts(
            null, null, null, null,
            providerRetrySettings: (null, null, 60_000),
            httpIdleTimeoutMs: 120_000,
            websocketConnectTimeoutMs: null);

        Assert.Equal(120_000, resolved.TimeoutMs);
        Assert.Null(resolved.MaxRetries);
        Assert.Null(resolved.WebsocketConnectTimeoutMs);
    }

    [Fact]
    public void ResolveRequestTimeouts_ZeroHttpIdleTimeoutBecomesInt32Max()
    {
        // TS：`httpIdleTimeoutMs === 0 ? 2147483647 : httpIdleTimeoutMs`。
        var resolved = Sdk.ResolveRequestTimeouts(
            null, null, null, null,
            providerRetrySettings: (null, null, 60_000),
            httpIdleTimeoutMs: 0,
            websocketConnectTimeoutMs: null);

        Assert.Equal(Sdk.MaxHttpIdleTimeoutMs, resolved.TimeoutMs);
        Assert.Equal(2147483647L, resolved.TimeoutMs);
    }

    // ---------------------------------------------------------------------
    // 夹具
    // ---------------------------------------------------------------------

    private static IReadOnlyList<ModelSpec> Models(string provider)
        => [new ModelSpec
        {
            Id = "claude-opus-4-5",
            Name = "Opus 4.5",
            Api = "anthropic-messages",
            Provider = provider,
            BaseUrl = "https://example.invalid",
            Reasoning = true,
            ContextWindow = 200_000,
            MaxTokens = 8_192,
        }];

    private static ModelSpec ReasoningModelWithMap(JsonObject levelMap)
        => new()
        {
            Id = "claude-opus-4-5",
            Name = "Opus 4.5",
            Api = "anthropic-messages",
            Provider = "anthropic",
            BaseUrl = "https://example.invalid",
            Reasoning = true,
            ContextWindow = 200_000,
            MaxTokens = 8_192,
            ThinkingLevelMap = ThinkingLevelMap.FromJsonObject(levelMap),
        };

    private sealed class FakeModelResolverRuntime(
        IReadOnlyList<ModelSpec> models,
        IReadOnlyList<string> configuredProviders) : IModelResolverRuntime
    {
        public IReadOnlyList<ModelSpec> GetModels() => models;

        public ModelSpec? GetModel(string providerId, string modelId)
            => models.FirstOrDefault(model => model.Provider == providerId && model.Id == modelId);

        public bool HasConfiguredAuth(string providerId) => configuredProviders.Contains(providerId);

        public IReadOnlyList<ModelSpec> GetAvailableSnapshot() => models;

        public Task<IReadOnlyList<ModelSpec>> GetAvailableAsync(
            string? providerId,
            CancellationToken signal = default)
            => Task.FromResult(models);
    }
}
