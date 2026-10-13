using System.Text.Json.Nodes;
using Pi.Ai;
using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Extensions.Llama;

/// <summary>
/// The <c>llama.cpp</c> provider. Port of <c>extensions/llama/provider.ts</c>:
/// <c>createLlamaProvider()</c> returns a controller whose <c>provider</c> registers a dynamic
/// (catalog-driven) pi-ai provider, and whose <c>setCatalog()</c> swaps the model list after the
/// extension syncs the server.
/// </summary>
/// <remarks>
/// <para>
/// Difference C169: TS returns the pair <c>{ provider, setCatalog }</c> where <c>provider</c> is a
/// <c>Provider&lt;"openai-completions"&gt;</c> object. C# folds the controller into the provider
/// itself — <see cref="LlamaProvider"/> implements <see cref="IProvider"/> and exposes
/// <see cref="SetCatalog"/>, which is what the extension actually needs (the TS pair is only used
/// together, in <c>index.ts</c>).
/// </para>
/// <para>
/// Difference C170: TS declares a separate <c>check</c> callback on the api-key auth. The C# port has
/// no such member — <c>Models.CheckAuthAsync</c> derives the check from <c>resolve</c>, so the llama
/// auth only implements <see cref="LlamaApiKeyAuth.ResolveAsync"/> and the reported
/// <c>source</c> comes from that result.
/// </para>
/// </remarks>
public sealed class LlamaProvider : IProvider, IClassifierProvider
{
    /// <summary>The TS <c>LLAMA_PROVIDER_ID</c>.</summary>
    public const string ProviderId = "llama.cpp";

    /// <summary>The TS <c>DEFAULT_LLAMA_SERVER_URL</c>.</summary>
    public const string DefaultServerUrl = "http://127.0.0.1:8080";

    /// <summary>The TS <c>process.env.LLAMA_BASE_URL</c> key.</summary>
    public const string BaseUrlEnv = "LLAMA_BASE_URL";

    /// <summary>The TS <c>process.env.LLAMA_API_KEY</c> key.</summary>
    public const string ApiKeyEnv = "LLAMA_API_KEY";

    private readonly ProviderClassifier _fallbackClassifier;
    private readonly ProviderClassifier _decisionClassifier;

    private IReadOnlyList<ModelSpec> _models = [];
    private IReadOnlyList<ModelSpec> _classifiers = [];

    public LlamaProvider(LlamaFetch? fetch = null)
    {
        Auth = new ProviderAuth { ApiKey = new LlamaApiKeyAuth(fetch) };
        _fallbackClassifier = new ProviderClassifier
        {
            Classify = (model, context, options, cancellationToken) =>
                LlamaCppClassify.Classify(model, context, options, httpClient: null, cancellationToken),
        };
        _decisionClassifier = LazyApis.TypesafeSystemOneApi();
    }

    public string Id => ProviderId;

    public string Name => ProviderId;

    public string? BaseUrl => LlamaModels.LlamaInferenceUrl(DefaultServerUrl);

    public ProviderAuth? Auth { get; }

    public IReadOnlyList<ModelSpec> GetModels() => _models;

    public IReadOnlyList<ModelSpec> GetAllModels() => [.. _models, .. _classifiers];

    public Func<RefreshModelsContext, Task>? RefreshModels => RefreshModelsAsync;

    public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
        => CompatApi.Stream(model, context, options);

    public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
        => CompatApi.StreamSimple(model, context, options);

    public Task<ClassifierResult> ClassifyAsync(ModelSpec model, ClassifierContext context,
        ClassifierOptions? options, CancellationToken cancellationToken)
        => (model.Api == TypesafeSystemOne.ApiName ? _decisionClassifier : _fallbackClassifier)
            .Classify(model, context, options, cancellationToken);

    /// <summary>The TS <c>setCatalog(catalog, serverUrl, options)</c>.</summary>
    public void SetCatalog(IReadOnlyList<LlamaModelInfo> catalog, string serverUrl, bool routerAutoload = false)
    {
        var selectable = catalog.Where(model => ModelIsSelectable(model, routerAutoload)).ToList();
        _models = selectable.Where(IsChatModel).Select(model => ToPiModel(model, serverUrl)).ToList();
        _classifiers = selectable.Select(model => ToPiClassifierModel(model, serverUrl)).ToList();
    }

    // ------------------------------------------------------------------ auth

    /// <summary>
    /// The TS <c>credentialServerUrl(credential)</c>: the normalized <c>LLAMA_BASE_URL</c> carried on a
    /// stored credential, or <see langword="null"/>.
    /// </summary>
    internal static string? CredentialServerUrl(Credential.ApiKey? credential)
    {
        if (credential?.Env is null || !credential.Env.TryGetValue(BaseUrlEnv, out var value))
        {
            return null;
        }

        return value.Trim().Length > 0 ? LlamaModels.NormalizeLlamaServerUrl(value) : null;
    }

    /// <summary>The TS <c>resolveServerUrl(ctx, credential)</c>.</summary>
    internal static async Task<string?> ResolveServerUrlAsync(
        IAuthContext ctx, Credential.ApiKey? credential, CancellationToken cancellationToken)
    {
        var configured = CredentialServerUrl(credential)
            ?? (await ctx.EnvAsync(BaseUrlEnv, cancellationToken).ConfigureAwait(false))?.Trim();
        return string.IsNullOrEmpty(configured) ? null : LlamaModels.NormalizeLlamaServerUrl(configured);
    }

    /// <summary>The llama.cpp api-key auth (TS <c>auth.apiKey</c>).</summary>
    private sealed class LlamaApiKeyAuth(LlamaFetch? fetch) : IApiKeyAuth
    {
        public string Name => "llama.cpp server";

        public bool HasLogin => true;

        public async Task<Credential.ApiKey> LoginAsync(
            ProviderAuthInteraction interaction, CancellationToken cancellationToken = default)
        {
            var environmentUrl = Environment.GetEnvironmentVariable(BaseUrlEnv);
            var placeholder = string.IsNullOrEmpty(environmentUrl) ? DefaultServerUrl : environmentUrl;
            var enteredUrl = await interaction
                .PromptAsync(new AuthPrompt.Text("llama.cpp server URL", placeholder), cancellationToken)
                .ConfigureAwait(false);
            var trimmedEntered = enteredUrl.Trim();
            var candidate = trimmedEntered.Length > 0
                ? trimmedEntered
                : (string.IsNullOrEmpty(environmentUrl) ? DefaultServerUrl : environmentUrl);
            var serverUrl = LlamaModels.NormalizeLlamaServerUrl(candidate);
            var apiKey = (await interaction
                .PromptAsync(new AuthPrompt.Secret("API key (optional)"), cancellationToken)
                .ConfigureAwait(false)).Trim();
            await new LlamaClient(serverUrl, apiKey.Length > 0 ? apiKey : null, fetch)
                .ListAsync(interaction.Signal)
                .ConfigureAwait(false);
            return new Credential.ApiKey(
                apiKey.Length > 0 ? apiKey : null,
                new Dictionary<string, string>(StringComparer.Ordinal) { [BaseUrlEnv] = serverUrl });
        }

        public async Task<AuthResult?> ResolveAsync(Credential.ApiKey? credential, IAuthContext ctx,
            CancellationToken cancellationToken = default)
        {
            var serverUrl = await ResolveServerUrlAsync(ctx, credential, cancellationToken).ConfigureAwait(false);
            if (serverUrl is null)
            {
                return null;
            }

            var apiKey = credential?.Key
                ?? await ctx.EnvAsync(ApiKeyEnv, cancellationToken).ConfigureAwait(false)
                ?? "local";
            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            if (credential?.Env is not null)
            {
                foreach (var (key, value) in credential.Env)
                {
                    environment[key] = value;
                }
            }

            environment[BaseUrlEnv] = serverUrl;
            return new AuthResult
            {
                Auth = new ModelAuth { ApiKey = apiKey, BaseUrl = LlamaModels.LlamaInferenceUrl(serverUrl) },
                Env = environment,
                Source = credential is not null ? "stored credential" : BaseUrlEnv,
            };
        }
    }

    // ------------------------------------------------------------------ catalog refresh

    private async Task RefreshModelsAsync(RefreshModelsContext context)
    {
        var cachedContextWindows = new Dictionary<string, long>(StringComparer.Ordinal);
        if (context.Stored is not null)
        {
            var stored = context.Stored.Models.Where(model => model.Provider == ProviderId).ToList();
            var restored = stored.Where(model => model.Type == ModelType.Chat && model.Api == "openai-completions").ToList();
            var restoredClassifiers = stored.Where(IsLlamaClassifierModel).ToList();
            foreach (var model in restored.Concat(restoredClassifiers))
            {
                cachedContextWindows[model.Id] = model.ContextWindow;
            }

            var applied = await context.Publish(new ModelsPublication
            {
                Update = () =>
                {
                    _models = restored;
                    _classifiers = restoredClassifiers;
                },
            }).ConfigureAwait(false);
            if (!applied)
            {
                return;
            }
        }

        if (context.AllowNetwork is false
            || context.Signal.IsCancellationRequested
            || context.Credential is not Credential.ApiKey credential)
        {
            return;
        }

        var serverUrl = CredentialServerUrl(credential);
        if (serverUrl is null)
        {
            return;
        }

        var client = new LlamaClient(serverUrl, credential.Key);
        var catalog = await client.ListAsync(context.Signal).ConfigureAwait(false);
        if (context.Signal.IsCancellationRequested)
        {
            return;
        }

        var routerAutoload = await RouterAutoloadEnabledAsync(client, catalog, context.Signal).ConfigureAwait(false);
        if (context.Signal.IsCancellationRequested)
        {
            return;
        }

        var selectable = catalog.Where(model => ModelIsSelectable(model, routerAutoload)).ToList();
        // Only loaded models expose their chat template without side effects. Unloaded autoload presets
        // would need to be loaded, while querying sleeping models may wake them. Those models remain
        // without thinking support until they are loaded and a later catalog refresh discovers it.
        // Decision models need no template, and llama.cpp reports them in the catalog regardless of status.
        var refreshed = new List<ModelSpec>();
        foreach (var model in selectable.Where(IsChatModel))
        {
            var cached = cachedContextWindows.TryGetValue(model.Id, out var window) ? window : (long?)null;
            if (model.StatusValue != LlamaModelStatusValue.Loaded)
            {
                refreshed.Add(ToPiModel(model, serverUrl, props: null, cached));
                continue;
            }

            var props = await client.PropsAsync(context.Signal, model.Id).ConfigureAwait(false);
            refreshed.Add(ToPiModel(model, serverUrl, props, cached));
        }

        var refreshedClassifiers = selectable
            .Select(model => ToPiClassifierModel(
                model,
                serverUrl,
                cachedContextWindows.TryGetValue(model.Id, out var window) ? window : (long?)null))
            .ToList();
        if (context.Signal.IsCancellationRequested)
        {
            return;
        }

        await context.Publish(new ModelsPublication
        {
            Persist = new ModelsStoreEntry
            {
                Models = [.. refreshed, .. refreshedClassifiers],
                CheckedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            },
            Update = () =>
            {
                _models = refreshed;
                _classifiers = refreshedClassifiers;
            },
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ model shaping

    /// <summary>The TS <c>modelIsSelectable(model, routerAutoload)</c>.</summary>
    internal static bool ModelIsSelectable(LlamaModelInfo model, bool routerAutoload)
    {
        if (model.StatusValue is LlamaModelStatusValue.Loaded or LlamaModelStatusValue.Sleeping)
        {
            return true;
        }

        // Unloaded presets are routable only when llama.cpp router autoload can load them on first use.
        return routerAutoload
            && model.StatusValue == LlamaModelStatusValue.Unloaded
            && !model.StatusFailed
            && model.Source == "preset";
    }

    /// <summary>
    /// The TS <c>isDecisionModel</c>: llama.cpp ≥ 0.6.0 lists <c>decisions</c> in
    /// <c>architecture.output_modalities</c> for native decision models.
    /// </summary>
    internal static bool IsDecisionModel(LlamaModelInfo model)
        => model.OutputModalities?.Contains("decisions") == true;

    /// <summary>The TS <c>isChatModel</c>: decision-only models are not listed for chat.</summary>
    internal static bool IsChatModel(LlamaModelInfo model)
        => !IsDecisionModel(model) || model.OutputModalities?.Contains("text") == true;

    /// <summary>The TS <c>isLlamaClassifierModel(model)</c> predicate over a pi-ai model.</summary>
    internal static bool IsLlamaClassifierModel(ModelSpec model)
        => model.Type == ModelType.Classifier
            && model.Api is "llama-cpp-classify" or TypesafeSystemOne.ApiName;

    /// <summary>The TS <c>configuredContextWindow(model)</c>: the <c>--ctx-size</c> launch argument.</summary>
    internal static long? ConfiguredContextWindow(LlamaModelInfo model)
    {
        var args = model.StatusArgs ?? [];
        for (var index = 0; index < args.Count - 1; index++)
        {
            var flag = args[index];
            if (flag is not ("--ctx-size" or "-c" or "-ctx"))
            {
                continue;
            }

            var contextWindow = JsNumber.Parse(args[index + 1]);
            if (JsNumber.IsSafeInteger(contextWindow) && contextWindow > 0)
            {
                return (long)contextWindow;
            }
        }

        return null;
    }

    /// <summary>The TS <c>contextWindowOf(model, cachedContextWindow)</c>.</summary>
    internal static long ContextWindowOf(LlamaModelInfo model, long? cachedContextWindow)
    {
        var runtimeContextWindow = model.MetaNCtx;
        if (runtimeContextWindow is > 0)
        {
            return (long)runtimeContextWindow.Value;
        }

        if (ConfiguredContextWindow(model) is { } configured)
        {
            return configured;
        }

        if (cachedContextWindow is > 0)
        {
            return cachedContextWindow.Value;
        }

        var trainingContextWindow = model.MetaNCtxTrain;
        return trainingContextWindow is > 0 ? (long)trainingContextWindow.Value : 128000;
    }

    /// <summary>The TS <c>toPiModel(model, serverUrl, props, cachedContextWindow)</c>.</summary>
    internal static ModelSpec ToPiModel(
        LlamaModelInfo model, string serverUrl, LlamaServerProps? props = null, long? cachedContextWindow = null)
    {
        var contextWindow = ContextWindowOf(model, cachedContextWindow);
        var reasoning = props?.ChatTemplate?.Contains("enable_thinking", StringComparison.Ordinal) == true;

        ThinkingLevelMap? thinkingLevelMap = null;
        if (reasoning)
        {
            thinkingLevelMap = ThinkingLevelMap.FromJsonObject(new JsonObject
            {
                ["off"] = "off",
                ["minimal"] = null,
                ["low"] = null,
                ["medium"] = "medium",
                ["high"] = null,
                ["xhigh"] = null,
            });
        }

        var compat = new JsonObject
        {
            ["supportsStore"] = false,
            ["supportsDeveloperRole"] = false,
            ["supportsReasoningEffort"] = false,
            ["supportsUsageInStreaming"] = true,
            ["supportsStrictMode"] = false,
            ["maxTokensField"] = "max_tokens",
        };
        if (reasoning)
        {
            compat["thinkingFormat"] = "qwen-chat-template";
        }

        return new ModelSpec
        {
            Id = model.Id,
            Name = model.Id,
            Api = "openai-completions",
            Provider = ProviderId,
            BaseUrl = LlamaModels.LlamaInferenceUrl(serverUrl),
            Reasoning = reasoning,
            ThinkingLevelMap = thinkingLevelMap,
            Input = model.InputModalities?.Contains("image") == true
                ? [ModelInput.Text, ModelInput.Image]
                : [ModelInput.Text],
            Cost = new ModelCostRates(0, 0, 0, 0),
            ContextWindow = contextWindow,
            MaxTokens = contextWindow,
            Compat = compat,
        };
    }

    /// <summary>The TS <c>toPiClassifierModel(model, serverUrl, cachedContextWindow)</c>.</summary>
    internal static ModelSpec ToPiClassifierModel(
        LlamaModelInfo model, string serverUrl, long? cachedContextWindow = null)
    {
        var decision = IsDecisionModel(model);
        return new ModelSpec
        {
            Id = model.Id,
            Name = model.Id,
            Api = decision ? TypesafeSystemOne.ApiName : "llama-cpp-classify",
            Provider = ProviderId,
            BaseUrl = decision ? LlamaModels.LlamaInferenceUrl(serverUrl) : serverUrl,
            Type = ModelType.Classifier,
            Input = [ModelInput.Text],
            Cost = new ModelCostRates(0, 0, 0, 0),
            ContextWindow = ContextWindowOf(model, cachedContextWindow),
        };
    }

    /// <summary>The TS <c>routerAutoloadEnabled(client, catalog, signal)</c>.</summary>
    private static async Task<bool> RouterAutoloadEnabledAsync(
        LlamaClient client, IReadOnlyList<LlamaModelInfo> catalog, CancellationToken cancellationToken)
    {
        if (!catalog.Any(model =>
            model.StatusValue == LlamaModelStatusValue.Unloaded && model.Source == "preset"))
        {
            return false;
        }

        try
        {
            return (await client.PropsAsync(cancellationToken).ConfigureAwait(false)).ModelsAutoload == true;
        }
        catch
        {
            return false;
        }
    }
}
