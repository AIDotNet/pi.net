using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class SdkTests
{
    [Fact]
    public void GetToolListError_RejectsMixedPlainAndModifiers()
    {
        Assert.Equal(
            "tool names cannot be mixed with +name or -name entries",
            SettingsManager.GetToolListError(["read", "+bash"]));
    }

    [Fact]
    public void GetToolListError_RejectsModifierPatterns()
    {
        Assert.Equal(
            "+name and -name entries take exact tool names, not patterns: +mcp__*",
            SettingsManager.GetToolListError(["+mcp__*"]));
    }

    [Fact]
    public void ResolveSessionTools_AppliesModifiersToDefaults()
    {
        var resolved = Sdk.ResolveSessionTools(new CreateAgentSessionOptions { Tools = ["-bash", "+grep"] });
        Assert.Equal(["read", "edit", "write", "grep"], resolved.InitialActiveToolNames);
        Assert.True(resolved.UsesDefaultTools);
        Assert.Null(resolved.AllowedToolNames);
        Assert.Equal(["-bash", "+grep"], resolved.DefaultToolModifiers);
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
    public void ResolveSessionTools_ExcludeToolsUsesGlob()
    {
        var resolved = Sdk.ResolveSessionTools(new CreateAgentSessionOptions { ExcludeTools = ["ba*"] });
        Assert.DoesNotContain("bash", resolved.InitialActiveToolNames);
        Assert.Contains("read", resolved.InitialActiveToolNames);
        Assert.Equal(["ba*"], resolved.ExcludedToolNames);
    }

    [Fact]
    public void CreateToolNameMatcher_MatchesExactAndGlob()
    {
        var match = Sdk.CreateToolNameMatcher(["read", "mcp__*"]);
        Assert.True(match("read"));
        Assert.True(match("mcp__server__tool"));
        Assert.False(match("bash"));
    }
}
