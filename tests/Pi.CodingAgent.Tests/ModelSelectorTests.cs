// ============================================================================
// Tests for modes/interactive/components/model-selector.ts
// Vectors transcribed from TS test/model-selector.test.ts. The TS harness runs
// against a faux provider with an api-key credential; the port uses the same
// offline ModelRuntime fixture as the session tests with env-key providers, so
// the models come from the embedded catalogs and assertions read provider/id
// dynamically instead of hardcoding TS fixture ids.
// ============================================================================

using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Utils;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.CodingAgent.Modes.Interactive.Components;
using Pi.CodingAgent.Utils;
using Pi.Tui;
using Xunit;

// `KeybindingsManager` exists in both namespaces (the TUI's base manager and the app's layered one);
// the app one is what TS's `core/keybindings.ts` exports.
using CoreKeybindingsManager = Pi.CodingAgent.Core.KeybindingsManager;

namespace Pi.CodingAgent.Tests;

public sealed class ModelSelectorTests : SelectorTestFixture
{
    /// <summary>
    /// The offline ModelRuntime fixture: env keys make two built-in providers configured, so their
    /// catalog models appear in the available snapshot. PI_OFFLINE keeps every refresh local.
    /// </summary>
    private static async Task<ModelRuntime> CreateModelRuntimeAsync()
    {
        return await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new InMemoryCredentialStore(),
            ModelsPathDisabled = true,
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            Env = name => name switch
            {
                "PI_OFFLINE" or "OPENAI_API_KEY" or "ANTHROPIC_API_KEY" => "1",
                _ => null,
            },
            AuthContext = new StubAuthContext(name => name switch
            {
                "PI_OFFLINE" or "OPENAI_API_KEY" or "ANTHROPIC_API_KEY" => "1",
                _ => null,
            }),
        });
    }

    private static string? GetModelRow(IComponent component, string id) =>
        AnsiText.StripAnsi(string.Join("\n", component.Render(120)))
            .Split('\n')
            .Select(line => line.TrimEnd())
            .FirstOrDefault(line => line.Contains($"{id} ["));

    private static string SelectedRow(IComponent component) =>
        RenderLines(component, 120)
            .Select(line => line.TrimEnd())
            .First(line => line.StartsWith('→'));

    private static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, message);
            await Task.Delay(10);
        }
    }

    private static async Task<ModelSelectorComponent> CreateWaitedSelectorAsync(
        ModelRuntime modelRuntime,
        ModelSpec? currentModel,
        Action<ModelSpec>? onSelectAsDefault = null,
        Action? onCancel = null,
        string? initialSearchInput = null)
    {
        var selector = new ModelSelectorComponent(
            new FakeTui(),
            currentModel,
            modelRuntime,
            [],
            _ => { },
            onCancel ?? (() => { }),
            initialSearchInput,
            onSelectAsDefault);
        // The ctor fires a real background refresh (TS `void this.refreshModels()`). Wait for the
        // "Refreshing model catalogs…" placeholder to disappear rather than for the success text:
        // under parallel-suite load the real ModelRuntime.RefreshAsync can exceed a short timeout,
        // and a provider-level error renders "Could not refresh …" instead of the success line.
        // The first frame can also come before the placeholder line exists at all (the list is not
        // laid out yet), so require a rendered outcome as well: a row, the empty-search line, or the
        // error line. Waiting only for the placeholder's absence asserted against a bare frame.
        await WaitUntilAsync(
            () =>
            {
                var text = RenderText(selector, 120);
                if (text.Contains("Refreshing model catalogs…")) return false;
                return text.Contains("No matching models")
                    || text.Contains("Could not refresh")
                    || (currentModel is not null && text.Contains(currentModel.Id));
            },
            "background refresh never finished",
            timeoutMs: 20_000);
        return selector;
    }

    // ------------------------------------------------------------------ upstream test 1

    [Fact]
    public async Task KeepsTheCurrentModelMarkedWhileBrowsing()
    {
        var modelRuntime = await CreateModelRuntimeAsync();
        var available = modelRuntime.GetAvailableSnapshot();
        Assert.True(available.Count >= 2, "expected at least two available models");
        var currentModel = available[0];
        var browsedModel = available[1];

        var selector = await CreateWaitedSelectorAsync(modelRuntime, currentModel);

        Assert.Equal($"→ ✓ {currentModel.Id} [{currentModel.Provider}]", GetModelRow(selector, currentModel.Id));

        selector.HandleInput("\x1b[B"); // down arrow
        Assert.Equal($"  ✓ {currentModel.Id} [{currentModel.Provider}]", GetModelRow(selector, currentModel.Id));
        // The selection moved to the second list row. The stable sort hoists the current model to
        // the top and keeps the catalog order below it; the first two available models share a
        // provider (each built-in catalog is far larger than one entry), so the browsed model is
        // exactly the one that follows the current model.
        Assert.Equal($"→   {browsedModel.Id} [{browsedModel.Provider}]", SelectedRow(selector));

        selector.Dispose();
    }

    // ------------------------------------------------------------------ upstream test 2

    [Fact]
    public async Task UsesTheConfiguredSaveBinding()
    {
        var previousKeybindings = GlobalKeybindings.Get();
        GlobalKeybindings.Set(new CoreKeybindingsManager(new KeybindingsConfig
        {
            [AppKeybindings.AppModelsSave] = "ctrl+r",
        }));
        try
        {
            var modelRuntime = await CreateModelRuntimeAsync();
            var currentModel = modelRuntime.GetAvailableSnapshot()[0];
            ModelSpec? saved = null;
            var saveCount = 0;

            var selector = await CreateWaitedSelectorAsync(
                modelRuntime,
                currentModel,
                onSelectAsDefault: model =>
                {
                    saved = model;
                    saveCount++;
                });

            Assert.Contains("Ctrl+R to set as default", RenderText(selector, 120));
            selector.HandleInput("\x13"); // ctrl+s — not the configured binding
            Assert.Equal(0, saveCount);
            selector.HandleInput("\x12"); // ctrl+r
            Assert.Equal(1, saveCount);
            // TS `toHaveBeenCalledWith(currentModel)` is a deep comparison; the background refresh
            // may rebuild the snapshot with fresh ModelSpec instances, so compare by value.
            Assert.NotNull(saved);
            Assert.True(ModelOperations.ModelsAreEqual(currentModel, saved), "saved model differs from current");
        }
        finally
        {
            GlobalKeybindings.Set(previousKeybindings);
        }
    }

    // ------------------------------------------------------------------ upstream test 3

    [Fact]
    public async Task ListsEveryCatalogThatFailedToRefresh()
    {
        var modelRuntime = await CreateModelRuntimeAsync();
        ModelCatalogRefresh.RefreshOverride = (_, _) => Task.FromResult(new ModelsRefreshResult(
            Aborted: false,
            Errors: new Dictionary<string, Exception>
            {
                ["openai"] = new Exception("unavailable"),
                ["anthropic"] = new Exception("unavailable"),
            }));
        try
        {
            var selector = new ModelSelectorComponent(
                new FakeTui(),
                modelRuntime.GetAvailableSnapshot().FirstOrDefault(),
                modelRuntime,
                [],
                _ => { },
                () => { });

            await WaitUntilAsync(
                () => RenderText(selector, 120)
                    .Contains("Could not refresh 2 model catalogs (openai, anthropic); showing cached models."),
                "refresh error message never rendered");

            selector.Dispose();
        }
        finally
        {
            ModelCatalogRefresh.RefreshOverride = null;
        }
    }

    // ------------------------------------------------------------------ port: cancel path

    [Fact]
    public async Task CancelDisposesThenInvokesTheCallback()
    {
        var modelRuntime = await CreateModelRuntimeAsync();
        var currentModel = modelRuntime.GetAvailableSnapshot()[0];
        var cancelled = 0;

        var selector = await CreateWaitedSelectorAsync(
            modelRuntime,
            currentModel,
            onCancel: () => cancelled++);

        selector.HandleInput("\x1b"); // escape
        Assert.Equal(1, cancelled);

        // dispose is idempotent (TS `if (this.closed) return`) — a second call is a no-op.
        selector.Dispose();
        selector.Dispose();
        Assert.Equal(1, cancelled);
    }

    // ------------------------------------------------------------------ port: initial search + enter

    [Fact]
    public async Task InitialSearchThatMatchesNothingShowsTheNoResultsPath()
    {
        var modelRuntime = await CreateModelRuntimeAsync();
        var currentModel = modelRuntime.GetAvailableSnapshot()[0];

        var selector = await CreateWaitedSelectorAsync(
            modelRuntime,
            currentModel,
            initialSearchInput: "zzzz-no-such-model");

        Assert.Contains("No matching models", RenderText(selector, 120));
        selector.Dispose();
    }
}
