using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the built-in extension registry (port of <c>extensions/index.ts</c>,
/// batch 4d-3). The TS array is the vector: four entries, llama.cpp first (not replaceable),
/// then the replaceable codemode / tool-search / mcp, all marked builtin.
/// </summary>
public class BuiltInExtensionsTests
{
    [Fact]
    public void All_MatchesTsRegistryShape()
    {
        Assert.Collection(
            BuiltInExtensions.All,
            first => AssertEntry(first, "llama.cpp", replaceable: false, builtin: true),
            second => AssertEntry(second, "codemode", replaceable: true, builtin: true),
            third => AssertEntry(third, "tool-search", replaceable: true, builtin: true),
            fourth => AssertEntry(fourth, "mcp", replaceable: true, builtin: true));
    }

    [Fact]
    public async Task PlaceholderFactory_FailsNamingItsBatch()
    {
        // The remaining placeholder lands with 4d-4 (mcp); until then it fails loudly instead of
        // registering nothing. llama.cpp / codemode / tool-search are already real factories.
        var mcp = Assert.IsType<InlineExtension.Described>(BuiltInExtensions.All[3]);
        var mcpError = await Assert.ThrowsAsync<NotSupportedException>(() => mcp.Factory(null!));
        Assert.Contains("mcp", mcpError.Message, StringComparison.Ordinal);
        Assert.Contains("4d-4", mcpError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LlamaCppFactory_IsTheRealFactory()
    {
        // llama.cpp registers a dynamic provider and the /llama command; the provider registration
        // path proves the placeholder is gone (a placeholder would throw before registering anything).
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var extension = await ExtensionLoader.LoadExtensionFromFactory(
            ((InlineExtension.Described)BuiltInExtensions.All[0]).Factory,
            Path.GetTempPath(),
            EventBusController.CreateEventBus(),
            runtime);

        Assert.Contains("llama", extension.Commands.Keys);
        var pending = Assert.Single(runtime.PendingNativeProviderRegistrations);
        Assert.Equal("llama.cpp", pending.Provider.Id);
    }

    private static void AssertEntry(InlineExtension extension, string name, bool replaceable, bool builtin)
    {
        var described = Assert.IsType<InlineExtension.Described>(extension);
        Assert.Equal(name, described.Name);
        Assert.Equal(replaceable, described.Replaceable);
        Assert.Equal(builtin, described.Builtin);
    }
}
