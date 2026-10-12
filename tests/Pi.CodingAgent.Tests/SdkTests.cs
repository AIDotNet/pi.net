using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <c>core/sdk.ts</c> 的工具选择与 <c>core/mcp-servers.ts</c> 的工具名匹配。
/// 2026-10-12 基线对齐后重写（原版混用了两个 TS 修订版，见 <see cref="Sdk"/> 的 remarks）。
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
    public void ResolveSessionTools_AllowlistIsNotTreatedAsModifiers()
    {
        // 基线对齐修正：`options.tools` 是纯 allowlist，**不**支持 +name / -name。
        // 旧端口会把 ["-bash","+grep"] 当成对默认集的增删，那是 TS 没有的行为。
        var resolved = Sdk.ResolveSessionTools(new CreateAgentSessionOptions { Tools = ["-bash", "+grep"] });

        Assert.Equal(["-bash", "+grep"], resolved.InitialActiveToolNames);
        Assert.Equal(["-bash", "+grep"], resolved.AllowedToolNames);
        Assert.False(resolved.UsesDefaultTools);
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
}
