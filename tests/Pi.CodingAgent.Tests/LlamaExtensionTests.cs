using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Extensions.Llama;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the llama provider and extension entry (port of
/// <c>extensions/llama/{provider.ts,index.ts}</c> plus the UI-agnostic half of <c>ui.ts</c>,
/// batch 4d-7b/4d-7c). The expected values are transcribed from the TS sources: the catalog
/// shaping (<c>modelIsSelectable</c> / <c>isChatModel</c> / <c>contextWindowOf</c> / <c>toPiModel</c> /
/// <c>toPiClassifierModel</c>), the manager label helpers, the Hugging Face spec parser, and the
/// <c>runWithProgress</c> stop-race.
/// </summary>
public class LlamaExtensionTests
{
    // ------------------------------------------------------------------ provider registration

    [Fact]
    public async Task Factory_RegistersProviderAndLlamaCommand()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var extension = await ExtensionLoader.LoadExtensionFromFactory(
            LlamaExtension.CreateLlamaExtension(),
            Path.GetTempPath(),
            EventBusController.CreateEventBus(),
            runtime);

        var command = Assert.Single(extension.Commands).Value;
        Assert.Equal("llama", command.Name);
        Assert.Equal("Manage llama.cpp router models", command.Description);

        var pending = Assert.Single(runtime.PendingNativeProviderRegistrations);
        var provider = Assert.IsType<LlamaProvider>(pending.Provider);
        Assert.Equal("llama.cpp", provider.Id);
        Assert.Equal("llama.cpp", provider.Name);
        Assert.Equal("http://127.0.0.1:8080/v1", provider.BaseUrl);
        Assert.Equal("llama.cpp server", provider.Auth?.ApiKey?.Name);
        Assert.True(provider.Auth?.ApiKey?.HasLogin);
        Assert.Empty(provider.GetModels());
        Assert.Empty(provider.GetAllModels());
        Assert.NotNull(provider.RefreshModels);
    }

    // ------------------------------------------------------------------ modelIsSelectable

    [Theory]
    [InlineData("loaded", null, false, true)]
    [InlineData("sleeping", null, false, true)]
    [InlineData("unloaded", "preset", true, true)]
    [InlineData("unloaded", "preset", false, false)]
    [InlineData("unloaded", "local", true, false)]
    [InlineData("downloading", "preset", true, false)]
    public void ModelIsSelectable_MatchesTypeScript(string status, string? source, bool routerAutoload, bool expected)
        => Assert.Equal(expected, LlamaProvider.ModelIsSelectable(Model("m", status, source: source), routerAutoload));

    [Fact]
    public void ModelIsSelectable_FailedPresetIsNotSelectable()
    {
        var model = Model("m", "unloaded", source: "preset", failed: true);
        Assert.False(LlamaProvider.ModelIsSelectable(model, routerAutoload: true));
    }

    // ------------------------------------------------------------------ decision / chat split

    [Fact]
    public void IsDecisionModel_RequiresTheDecisionsModality()
    {
        Assert.True(LlamaProvider.IsDecisionModel(Model("m", "loaded", output: ["decisions"])));
        Assert.True(LlamaProvider.IsDecisionModel(Model("m", "loaded", output: ["text", "decisions"])));
        Assert.False(LlamaProvider.IsDecisionModel(Model("m", "loaded", output: ["text"])));
        Assert.False(LlamaProvider.IsDecisionModel(Model("m", "loaded")));
    }

    [Fact]
    public void IsChatModel_ExcludesDecisionOnlyModels()
    {
        Assert.True(LlamaProvider.IsChatModel(Model("m", "loaded")));
        Assert.True(LlamaProvider.IsChatModel(Model("m", "loaded", output: ["text"])));
        Assert.True(LlamaProvider.IsChatModel(Model("m", "loaded", output: ["text", "decisions"])));
        Assert.False(LlamaProvider.IsChatModel(Model("m", "loaded", output: ["decisions"])));
    }

    // ------------------------------------------------------------------ configuredContextWindow

    [Theory]
    [InlineData(new[] { "--ctx-size", "4096" }, 4096L)]
    [InlineData(new[] { "-c", "8192" }, 8192L)]
    [InlineData(new[] { "-ctx", "2048" }, 2048L)]
    [InlineData(new[] { "-ngl", "99", "--ctx-size", "16384" }, 16384L)]
    [InlineData(new[] { "--ctx-size" }, null)]
    [InlineData(new[] { "--ctx-size", "abc" }, null)]
    [InlineData(new[] { "--ctx-size", "0" }, null)]
    [InlineData(new[] { "--ctx-size", "-5" }, null)]
    [InlineData(new[] { "--ctx-size", "12.5" }, null)]
    [InlineData(new[] { "--ctx-size", "1e3" }, 1000L)]
    public void ConfiguredContextWindow_MatchesTypeScript(string[] args, long? expected)
        => Assert.Equal(expected, LlamaProvider.ConfiguredContextWindow(Model("m", "loaded", args: args)));

    [Fact]
    public void ContextWindowOf_PrefersRuntimeThenConfiguredThenCachedThenTraining()
    {
        // runtime n_ctx wins
        Assert.Equal(4096L, LlamaProvider.ContextWindowOf(
            Model("m", "loaded", args: ["--ctx-size", "8192"], nCtx: 4096, nCtxTrain: 32768), cachedContextWindow: 16384));
        // configured arg wins over the cached window
        Assert.Equal(8192L, LlamaProvider.ContextWindowOf(
            Model("m", "loaded", args: ["--ctx-size", "8192"], nCtxTrain: 32768), cachedContextWindow: 16384));
        // cached window wins over the training window
        Assert.Equal(16384L, LlamaProvider.ContextWindowOf(
            Model("m", "loaded", nCtxTrain: 32768), cachedContextWindow: 16384));
        // training window is the last resort before the default
        Assert.Equal(32768L, LlamaProvider.ContextWindowOf(Model("m", "loaded", nCtxTrain: 32768), cachedContextWindow: null));
        // default
        Assert.Equal(128000L, LlamaProvider.ContextWindowOf(Model("m", "loaded"), cachedContextWindow: null));
        // a zero runtime window falls through
        Assert.Equal(128000L, LlamaProvider.ContextWindowOf(Model("m", "loaded", nCtx: 0), cachedContextWindow: null));
    }

    // ------------------------------------------------------------------ toPiModel / toPiClassifierModel

    [Fact]
    public void ToPiModel_ShapesAnOpenAiCompletionsModel()
    {
        var model = LlamaProvider.ToPiModel(Model("qwen3", "loaded", input: ["text"]), "http://127.0.0.1:8080");

        Assert.Equal("qwen3", model.Id);
        Assert.Equal("qwen3", model.Name);
        Assert.Equal("openai-completions", model.Api);
        Assert.Equal("llama.cpp", model.Provider);
        Assert.Equal("http://127.0.0.1:8080/v1", model.BaseUrl);
        Assert.Equal(ModelType.Chat, model.Type);
        Assert.False(model.Reasoning);
        Assert.Null(model.ThinkingLevelMap);
        Assert.Equal(new[] { ModelInput.Text }, model.Input);
        Assert.Equal(new ModelCostRates(0, 0, 0, 0), model.Cost);
        Assert.Equal(128000L, model.ContextWindow);
        Assert.Equal(128000L, model.MaxTokens);

        Assert.NotNull(model.Compat);
        Assert.False(model.Compat!["supportsStore"]!.GetValue<bool>());
        Assert.False(model.Compat["supportsDeveloperRole"]!.GetValue<bool>());
        Assert.False(model.Compat["supportsReasoningEffort"]!.GetValue<bool>());
        Assert.True(model.Compat["supportsUsageInStreaming"]!.GetValue<bool>());
        Assert.False(model.Compat["supportsStrictMode"]!.GetValue<bool>());
        Assert.Equal("max_tokens", model.Compat["maxTokensField"]!.GetValue<string>());
        Assert.False(model.Compat.ContainsKey("thinkingFormat"));
    }

    [Fact]
    public void ToPiModel_AddsThinkingSupportWhenTheTemplateEnablesIt()
    {
        var model = LlamaProvider.ToPiModel(
            Model("qwen3", "loaded"), "http://127.0.0.1:8080",
            new LlamaServerProps { ChatTemplate = "{% if enable_thinking %}…{% endif %}" });

        Assert.True(model.Reasoning);
        Assert.NotNull(model.ThinkingLevelMap);
        Assert.Equal("off", model.ThinkingLevelMap!["off"]);
        Assert.Equal("medium", model.ThinkingLevelMap["medium"]);
        // explicit null means "disabled"; missing means "unset" — both read back as null but only the
        // former is present.
        Assert.True(model.ThinkingLevelMap.Has("minimal"));
        Assert.Null(model.ThinkingLevelMap["minimal"]);
        Assert.False(model.ThinkingLevelMap.Has("max"));
        Assert.Equal("qwen-chat-template", model.Compat!["thinkingFormat"]!.GetValue<string>());
    }

    [Fact]
    public void ToPiModel_KeepsImageInputWhenTheArchitectureAdvertisesIt()
        => Assert.Equal(
            new[] { ModelInput.Text, ModelInput.Image },
            LlamaProvider.ToPiModel(Model("vl", "loaded", input: ["text", "image"]), "http://127.0.0.1:8080").Input);

    [Fact]
    public void ToPiClassifierModel_PicksTheApiByModality()
    {
        var chat = LlamaProvider.ToPiClassifierModel(Model("qwen3", "loaded"), "http://127.0.0.1:8080");
        Assert.Equal(ModelType.Classifier, chat.Type);
        Assert.Equal("llama-cpp-classify", chat.Api);
        Assert.Equal("http://127.0.0.1:8080", chat.BaseUrl);
        Assert.Equal(new[] { ModelInput.Text }, chat.Input);
        Assert.Equal(128000L, chat.ContextWindow);

        var decision = LlamaProvider.ToPiClassifierModel(
            Model("judge", "loaded", output: ["decisions"]), "http://127.0.0.1:8080");
        Assert.Equal("typesafe-system-one", decision.Api);
        Assert.Equal("http://127.0.0.1:8080/v1", decision.BaseUrl);
    }

    // ------------------------------------------------------------------ setCatalog

    [Fact]
    public void SetCatalog_SplitsChatAndClassifierModels()
    {
        var provider = new LlamaProvider();
        provider.SetCatalog(
            [
                Model("chat", "loaded"),
                Model("judge", "loaded", output: ["decisions"]),
                Model("preset", "unloaded", source: "preset"),
            ],
            "http://127.0.0.1:8080",
            routerAutoload: true);

        // TS `getAllModels()` is `[...models, ...classifiers]`: every selectable model appears twice,
        // once as a chat entry and once as a classifier entry.
        Assert.Equal(["chat", "preset"], provider.GetModels().Select(model => model.Id));
        Assert.Equal(
            ["chat", "preset", "chat", "judge", "preset"],
            provider.GetAllModels().Select(model => model.Id));
        Assert.Equal(
            ["llama-cpp-classify", "typesafe-system-one", "llama-cpp-classify"],
            provider.GetAllModels().Where(model => model.Type == ModelType.Classifier).Select(model => model.Api));
    }

    [Fact]
    public void SetCatalog_DropsUnselectableModels()
    {
        var provider = new LlamaProvider();
        provider.SetCatalog(
            [Model("preset", "unloaded", source: "preset"), Model("chat", "loaded")],
            "http://127.0.0.1:8080");

        Assert.Equal(new[] { "chat" }, provider.GetModels().Select(model => model.Id));
    }

    // ------------------------------------------------------------------ ui helpers

    [Theory]
    [InlineData(999d, "999")]
    [InlineData(1000d, "1.0k")]
    [InlineData(1234d, "1.2k")]
    [InlineData(100000d, "100k")]
    [InlineData(123456d, "123k")]
    [InlineData(1000000d, "1.0M")]
    [InlineData(12345678d, "12M")]
    public void CompactCount_MatchesTypeScript(double value, string expected)
        => Assert.Equal(expected, LlamaUi.CompactCount(value));

    [Fact]
    public void ContextLabel_PrefersRuntimeThenConfiguredThenTraining()
    {
        Assert.Equal("4k", LlamaUi.ContextLabel(Model("m", "loaded", nCtx: 4096)));
        Assert.Equal("512", LlamaUi.ContextLabel(Model("m", "loaded", nCtx: 512)));
        Assert.Equal("131k", LlamaUi.ContextLabel(Model("m", "loaded", nCtxTrain: 131072)));
        Assert.Equal("33k", LlamaUi.ContextLabel(Model("m", "loaded", args: ["--ctx-size", "32768"])));
        Assert.Equal("800", LlamaUi.ContextLabel(Model("m", "loaded", args: ["-c", "800"])));
        Assert.Null(LlamaUi.ContextLabel(Model("m", "loaded")));
        // Math.round (half up), not Math.floor: the fraction of /1000 rounds the label up.
        Assert.Equal("2k", LlamaUi.ContextLabel(Model("m", "loaded", nCtx: 1500)));
        Assert.Equal("3k", LlamaUi.ContextLabel(Model("m", "loaded", nCtxTrain: 2500)));
        Assert.Equal("2k", LlamaUi.ContextLabel(Model("m", "loaded", args: ["-ctx", "1500"])));
        Assert.Equal("9k", LlamaUi.ContextLabel(Model("m", "loaded", args: ["--ctx-size", "8500"])));
    }

    [Fact]
    public void ModelDescription_MatchesTypeScript()
    {
        Assert.Equal("loaded · 4k context", LlamaUi.ModelDescription(Model("m", "loaded", nCtx: 4096)));
        Assert.Equal("loaded · 4k context", LlamaUi.ModelDescription(Model("m", "sleeping", nCtx: 4096)));
        Assert.Equal("loading", LlamaUi.ModelDescription(Model("m", "loading")));
        Assert.Equal("", LlamaUi.ModelDescription(Model("m", "unloaded")));
        // an unloaded model never shows its context window
        Assert.Equal("", LlamaUi.ModelDescription(Model("m", "unloaded", nCtx: 4096)));
    }

    [Theory]
    [InlineData("owner/repo", "owner/repo", null)]
    [InlineData("owner/repo:Q4_K_M", "owner/repo", "Q4_K_M")]
    [InlineData("owner:tag/repo", "owner:tag/repo", null)]
    [InlineData("no-slash:quant", "no-slash", "quant")]
    public void ParseHuggingFaceModel_MatchesTypeScript(string value, string repository, string? quantization)
    {
        var parsed = LlamaExtension.ParseHuggingFaceModel(value);
        Assert.Equal(repository, parsed.Repository);
        Assert.Equal(quantization, parsed.Quantization);
    }

    // ------------------------------------------------------------------ runWithProgress

    [Fact]
    public async Task RunWithProgress_ReturnsTheValueWhenTheRunSettlesFirst()
    {
        var ui = new FakeLlamaUi();
        var updated = new List<LlamaProgressState>();
        ui.OnUpdateProgress = updated.Add;

        var result = await LlamaUi.RunWithProgressAsync(ui, new LlamaRunOptions<int>
        {
            Title = "Loading model",
            Model = "qwen3",
            InitialMessage = "Starting…",
            CancelTitle = "Stop loading?",
            CancelMessage = "qwen3",
            Run = async (_, update) =>
            {
                await Task.Yield();
                update(new LlamaProgress { Message = "Loading weights", Ratio = 0.5 });
                return 7;
            },
            Cancel = () => Task.CompletedTask,
        });

        Assert.False(result.Cancelled);
        Assert.Equal(7, result.Value);
        Assert.Contains(updated, state => state.Message == "Loading weights" && state.Ratio == 0.5);
        Assert.Contains(updated, state => state.Title == "Loading model" && state.Model == "qwen3");
    }

    [Fact]
    public async Task RunWithProgress_CancelsWhenTheUserStops()
    {
        var ui = new FakeLlamaUi { StopRequests = 1, ConfirmResult = true };
        var cancelled = 0;

        var result = await LlamaUi.RunWithProgressAsync(ui, new LlamaRunOptions<int>
        {
            Title = "Loading model",
            Model = "qwen3",
            InitialMessage = "Starting…",
            CancelTitle = "Stop loading?",
            CancelMessage = "qwen3",
            Run = async (signal, _) =>
            {
                await Task.Delay(Timeout.Infinite, signal);
                return 7;
            },
            Cancel = () =>
            {
                cancelled++;
                return Task.CompletedTask;
            },
        });

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.Value);
        Assert.Equal(1, cancelled);
        Assert.Equal(("Stop loading?", "qwen3"), ui.LastConfirm);
    }

    [Fact]
    public async Task RunWithProgress_KeepsRunningWhenTheUserDeclinesTheStop()
    {
        var ui = new FakeLlamaUi { StopRequests = 1, ConfirmResult = false };

        var result = await LlamaUi.RunWithProgressAsync(ui, new LlamaRunOptions<int>
        {
            Title = "Loading model",
            Model = "qwen3",
            InitialMessage = "Starting…",
            CancelTitle = "Stop loading?",
            CancelMessage = "qwen3",
            Run = async (_, _) =>
            {
                await Task.Delay(50);
                return 7;
            },
            Cancel = () => Task.CompletedTask,
        });

        Assert.False(result.Cancelled);
        Assert.Equal(7, result.Value);
    }

    [Fact]
    public async Task RunWithProgress_PropagatesAFailedRun()
    {
        var ui = new FakeLlamaUi();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LlamaUi.RunWithProgressAsync(ui, new LlamaRunOptions<int>
            {
                Title = "Loading model",
                Model = "qwen3",
                InitialMessage = "Starting…",
                CancelTitle = "Stop loading?",
                CancelMessage = "qwen3",
                Run = (_, _) => Task.FromException<int>(new InvalidOperationException("Model failed to load")),
                Cancel = () => Task.CompletedTask,
            }));

        Assert.Equal("Model failed to load", thrown.Message);
    }

    // ------------------------------------------------------------------ fixtures

    private static LlamaModelInfo Model(
        string id,
        string status,
        string? source = null,
        string[]? output = null,
        string[]? input = null,
        string[]? args = null,
        double? nCtx = null,
        double? nCtxTrain = null,
        bool failed = false)
    {
        var statusObject = new JsonObject { ["value"] = status };
        if (failed)
        {
            statusObject["failed"] = true;
        }

        if (args is not null)
        {
            statusObject["args"] = new JsonArray([.. args.Select(argument => (JsonNode?)argument)]);
        }

        var raw = new JsonObject { ["id"] = id, ["status"] = statusObject };
        if (source is not null)
        {
            raw["source"] = source;
        }

        if (output is not null || input is not null)
        {
            var architecture = new JsonObject();
            if (output is not null)
            {
                architecture["output_modalities"] = new JsonArray([.. output.Select(value => (JsonNode?)value)]);
            }

            if (input is not null)
            {
                architecture["input_modalities"] = new JsonArray([.. input.Select(value => (JsonNode?)value)]);
            }

            raw["architecture"] = architecture;
        }

        if (nCtx is not null || nCtxTrain is not null)
        {
            var meta = new JsonObject();
            if (nCtx is not null)
            {
                meta["n_ctx"] = nCtx;
            }

            if (nCtxTrain is not null)
            {
                meta["n_ctx_train"] = nCtxTrain;
            }

            raw["meta"] = meta;
        }

        return new LlamaModelInfo(raw);
    }

    private sealed class FakeLlamaUi : ILlamaUi
    {
        private static readonly Task NeverCompletes = new TaskCompletionSource().Task;

        public int StopRequests { get; set; }

        public bool ConfirmResult { get; set; }

        public Action<LlamaProgressState>? OnUpdateProgress { get; set; }

        public (string Title, string Message)? LastConfirm { get; private set; }

        public Task<LlamaManagerAction> ShowModels(string serverUrl, IReadOnlyList<LlamaModelInfo> models)
            => Task.FromResult<LlamaManagerAction>(new LlamaManagerAction.Close());

        public Task<string?> Select(string title, IReadOnlyList<string> options) => Task.FromResult<string?>(null);

        public Task<bool> Confirm(string title, string message)
        {
            LastConfirm = (title, message);
            return Task.FromResult(ConfirmResult);
        }

        public Task<string> ConnectionError(string serverUrl, string message) => Task.FromResult("close");

        public Task<string?> SearchModels(Func<string, CancellationToken, Task<IReadOnlyList<HuggingFaceModel>>> search)
            => Task.FromResult<string?>(null);

        public void ShowStatus(string title, string message)
        {
        }

        public Task Progress(LlamaProgressState state)
        {
            if (StopRequests > 0)
            {
                StopRequests--;
                return Task.CompletedTask;
            }

            return NeverCompletes;
        }

        public void UpdateProgress(LlamaProgressState state) => OnUpdateProgress?.Invoke(state);
    }
}
