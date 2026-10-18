using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Experimental.Services;
using AgentState = Pi.Durable.Harness.AgentState;
using DurableModelRef = Pi.Durable.Harness.ModelRef;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the models provider's decision leaves (4g-3b). TS keeps most of them inside
/// the <c>createModelsService</c> closure; the port lifts them to <c>internal static</c> members so the
/// suite can pin them without a live durable harness (the orchestration itself needs one — same
/// judgement as <see cref="AgentControllerProviderTests"/>).
/// </summary>
public class ModelsProviderTests
{
    private static ModelSpec Model(string provider, string id, string name = "Model", bool reasoning = false)
        => new() { Id = id, Name = name, Api = "faux", Provider = provider, BaseUrl = "http://localhost:0", Reasoning = reasoning };

    private static ModelsRefreshResult Refresh(params (string Provider, string Message)[] errors)
        => new(false, errors.ToDictionary(
            entry => entry.Provider,
            entry => (Exception)new InvalidOperationException(entry.Message),
            StringComparer.Ordinal));

    // ---------------------------------------------------------------- configurationOf

    [Fact]
    public void ConfigurationOf_WithoutADocumentIsNullModelAndOff()
    {
        // TS: value?.model === undefined ? null : … ; value?.thinkingLevel ?? "off".
        var configuration = ModelsProvider.ConfigurationOf(null);

        Assert.Null(configuration.Model);
        Assert.Equal(ThinkingLevel.Off, configuration.ThinkingLevel);
    }

    [Fact]
    public void ConfigurationOf_WithoutAModelKeepsTheLevel()
    {
        var configuration = ModelsProvider.ConfigurationOf(new AgentState { ThinkingLevel = ThinkingLevel.High });

        Assert.Null(configuration.Model);
        Assert.Equal(ThinkingLevel.High, configuration.ThinkingLevel);
    }

    [Fact]
    public void ConfigurationOf_WithAModelCopiesProviderAndId()
    {
        var configuration = ModelsProvider.ConfigurationOf(new AgentState
        {
            Model = new DurableModelRef("openai", "gpt-5"),
            ThinkingLevel = ThinkingLevel.Low,
        });

        var model = Assert.IsType<ModelRef>(configuration.Model);
        Assert.Equal("openai", model.Provider);
        Assert.Equal("gpt-5", model.ModelId);
        Assert.Equal(ThinkingLevel.Low, configuration.ThinkingLevel);
    }

    [Fact]
    public void ConfigurationOf_WithAModelButNoLevelReadsOff()
    {
        var configuration = ModelsProvider.ConfigurationOf(new AgentState
        {
            Model = new DurableModelRef("openai", "gpt-5"),
        });

        Assert.NotNull(configuration.Model);
        Assert.Equal(ThinkingLevel.Off, configuration.ThinkingLevel);
    }

    // ---------------------------------------------------------------- nextThinkingLevel

    [Theory]
    [InlineData("off", "low")]   // first entry
    [InlineData("low", "high")]  // middle of the list
    [InlineData("high", "off")]  // wraps around
    public void NextThinkingLevel_AdvancesOrWraps(string current, string expected)
        => Assert.Equal(expected, ModelsProvider.NextThinkingLevel(["off", "low", "high"], current));

    [Fact]
    public void NextThinkingLevel_SingleEntryWrapsToItself()
        => Assert.Equal("off", ModelsProvider.NextThinkingLevel(["off"], "off"));

    [Fact]
    public void NextThinkingLevel_UnknownLevelWrapsToTheFirst()
    {
        // TS: indexOf returns -1, so (-1 + 1) % length === 0 — the first entry, not the last.
        Assert.Equal("off", ModelsProvider.NextThinkingLevel(["off", "low", "high"], "max"));
    }

    [Fact]
    public void NextThinkingLevel_EmptyListFallsBackToOff()
    {
        // TS: 0 % 0 is NaN, [][NaN] is undefined, and ?? "off" catches it.
        Assert.Equal("off", ModelsProvider.NextThinkingLevel([], "off"));
        Assert.Equal("off", ModelsProvider.NextThinkingLevel([], "whatever"));
    }

    // ---------------------------------------------------------------- includesModel

    [Fact]
    public void IncludesModel_RequiresProviderAndId()
    {
        var selected = Model("openai", "gpt-5");

        Assert.True(ModelsProvider.IncludesModel([Model("openai", "gpt-5")], selected));
        Assert.True(ModelsProvider.IncludesModel([Model("openai", "gpt-4"), Model("openai", "gpt-5")], selected));
        Assert.False(ModelsProvider.IncludesModel([Model("anthropic", "gpt-5")], selected));
        Assert.False(ModelsProvider.IncludesModel([Model("openai", "gpt-4")], selected));
        Assert.False(ModelsProvider.IncludesModel([], selected));
    }

    // ---------------------------------------------------------------- buildCatalog

    [Fact]
    public void BuildCatalog_WithoutASelectionMapsTheSnapshot()
    {
        var catalog = ModelsProvider.BuildCatalog(4, [Model("openai", "a", "A"), Model("faux", "b", "B")], null);

        Assert.Equal(5, catalog.Revision);
        Assert.Collection(catalog.AvailableModels,
            first =>
            {
                Assert.Equal("openai", first.Provider);
                Assert.Equal("a", first.ModelId);
                Assert.Equal("A", first.Name);
                Assert.False(first.Reasoning);
            },
            second =>
            {
                Assert.Equal("faux", second.Provider);
                Assert.Equal("b", second.ModelId);
                Assert.Equal("B", second.Name);
            });
    }

    [Fact]
    public void BuildCatalog_AppendsTheSelectionOnlyWhenMissing()
    {
        var selected = Model("faux", "faux-1", "Faux", reasoning: true);

        var contained = ModelsProvider.BuildCatalog(
            0, [Model("openai", "a"), Model("faux", "faux-1", "Faux", reasoning: true)], selected);
        Assert.Equal(2, contained.AvailableModels.Count);

        var appended = ModelsProvider.BuildCatalog(0, [Model("openai", "a")], selected);
        Assert.Equal(2, appended.AvailableModels.Count);
        var last = Assert.IsType<ModelSummary>(appended.AvailableModels[^1]);
        Assert.Equal("faux", last.Provider);
        Assert.Equal("faux-1", last.ModelId);
        Assert.Equal("Faux", last.Name);
        Assert.True(last.Reasoning);
    }

    [Fact]
    public void BuildCatalog_EmptySnapshotWithASelectionYieldsOneEntry()
    {
        var catalog = ModelsProvider.BuildCatalog(0, [], Model("faux", "faux-1"));

        Assert.Equal(1, catalog.Revision);
        var single = Assert.IsType<ModelSummary>(Assert.Single(catalog.AvailableModels));
        Assert.Equal("faux/faux-1", $"{single.Provider}/{single.ModelId}");
    }

    [Fact]
    public void BuildCatalog_DoesNotMutateTheSnapshot()
    {
        var available = new List<ModelSpec> { Model("openai", "a") };
        var selected = Model("faux", "faux-1");

        var catalog = ModelsProvider.BuildCatalog(0, available, selected);

        Assert.Single(available);
        Assert.Equal(2, catalog.AvailableModels.Count);
    }

    // ---------------------------------------------------------------- refreshErrors

    [Fact]
    public void RefreshErrors_EmptyResultMapsToEmpty()
        => Assert.Empty(ModelsProvider.RefreshErrors(Refresh()));

    [Fact]
    public void RefreshErrors_MapsProviderIdToMessage()
    {
        var errors = ModelsProvider.RefreshErrors(
            Refresh(("openai", "boom"), ("anthropic", "nope")));

        Assert.Equal(2, errors.Count);
        Assert.Equal("boom", errors["openai"]);
        Assert.Equal("nope", errors["anthropic"]);
        // TS spreads the Map into entries: the provider order of the result is preserved.
        Assert.Equal(["openai", "anthropic"], errors.Keys);
    }

    [Fact]
    public void RefreshErrors_IgnoresTheAbortedFlag()
    {
        var aborted = new ModelsRefreshResult(true, new Dictionary<string, Exception>(StringComparer.Ordinal));

        Assert.Empty(ModelsProvider.RefreshErrors(aborted));
    }

    // ---------------------------------------------------------------- parseLevel

    [Theory]
    [InlineData("off", ThinkingLevel.Off)]
    [InlineData("minimal", ThinkingLevel.Minimal)]
    [InlineData("low", ThinkingLevel.Low)]
    [InlineData("medium", ThinkingLevel.Medium)]
    [InlineData("high", ThinkingLevel.High)]
    [InlineData("xhigh", ThinkingLevel.XHigh)]
    [InlineData("max", ThinkingLevel.Max)]
    public void ParseLevel_AcceptsTheSevenLiterals(string text, ThinkingLevel expected)
        => Assert.Equal(expected, ModelsProvider.ParseLevel(text));

    [Fact]
    public void ParseLevel_RejectsUnknownText()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ModelsProvider.ParseLevel("extreme"));

        Assert.Contains("extreme", error.Message, StringComparison.Ordinal);
    }
}
