using System.Text.Json.Nodes;
using Pi.Ai;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>
/// Replaces a provider's catalog with a credential-specific model list. 对应 TS <c>PersonalizeCatalog</c>。
/// </summary>
public delegate Task<IReadOnlyList<ModelSpec>> PersonalizeCatalog(
    IReadOnlyList<ModelSpec> catalog, RefreshModelsContext context);

/// <summary>
/// 与 <see cref="RemoteCatalogProvider"/> 同款包装，但每次联网刷新都先让 pi.dev 目录（内建模型加更新的
/// 远端目录）过一遍 <c>personalize</c>，再保存个性化结果：存的是个性化列表而不是 pi.dev 目录，且它
/// 离线也生效。跳过刷新间隔与 etag 复验。结果为空（从未保存过，或旧版 pi 在目录缺失/失败后存了空表）
/// 时回落到内建模型。对应 TS <c>withPersonalizedCatalog</c>。
/// </summary>
/// <remarks>
/// 转发规则与 <see cref="RemoteCatalogProvider"/> 一致（C# 无对象展开，逐个转发，
/// 能力缺失时抛出与内层相同的错误），因此这里刻意不继承以保证两个包装彼此独立。
/// </remarks>
public sealed class PersonalizedCatalogProvider : IProvider, IImagesProvider, IClassifierProvider
{
    private readonly IProvider _inner;
    private readonly PersonalizeCatalog _personalize;
    private readonly string _catalogBaseUrl;
    private readonly long? _localGeneratedAt;
    private IReadOnlyList<ModelSpec> _personalModels = [];

    public PersonalizedCatalogProvider(
        IProvider inner,
        PersonalizeCatalog personalize,
        string? catalogBaseUrl = null,
        long? localGeneratedAt = null)
    {
        _inner = inner;
        _personalize = personalize;
        _catalogBaseUrl = catalogBaseUrl ?? RemoteCatalogProvider.DefaultCatalogBaseUrl;
        _localGeneratedAt = localGeneratedAt;
    }

    public string Id => _inner.Id;

    public string Name => _inner.Name;

    public string? BaseUrl => _inner.BaseUrl;

    public ProviderAuth? Auth => _inner.Auth;

    public bool SupportsFetchDeferred => _inner.SupportsFetchDeferred;

    public bool SupportsCancelDeferred => _inner.SupportsCancelDeferred;

    /// <summary>内建模型：个性化列表为空时的回落。</summary>
    private IReadOnlyList<ModelSpec> BuiltinModels() => _inner.GetAllModels();

    private IReadOnlyList<ModelSpec> AllModels()
        => _personalModels.Count > 0 ? _personalModels : BuiltinModels();

    public IReadOnlyList<ModelSpec> GetModels()
        => AllModels().Where(model => model.Type == ModelType.Chat).ToList();

    public IReadOnlyList<ModelSpec> GetAllModels() => AllModels();

    public IReadOnlyList<ModelSpec> FilterModels(IReadOnlyList<ModelSpec> models, Credential? credential)
        => _inner.FilterModels(models, credential);

    public IReadOnlyList<ModelSpec>? FilterAllModels(IReadOnlyList<ModelSpec> models, Credential? credential)
        => _inner.FilterAllModels(models, credential);

    public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
        => _inner.Stream(model, context, options);

    public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
        => _inner.StreamSimple(model, context, options);

    public IAssistantMessageEventStream? StreamDeferred(ModelSpec model, DeferredHandle handle,
        IReadOnlyDictionary<string, object?>? options = null)
        => _inner.StreamDeferred(model, handle, options);

    public Task CancelDeferredAsync(ModelSpec model, DeferredHandle handle,
        IReadOnlyDictionary<string, object?>? options = null, CancellationToken cancellationToken = default)
        => _inner.CancelDeferredAsync(model, handle, options, cancellationToken);

    public Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
        ImagesOptions? options, CancellationToken cancellationToken)
        => _inner is IImagesProvider images
            ? images.GenerateImagesAsync(model, context, options, cancellationToken)
            : throw new ModelsError(ModelsErrorCode.Provider,
                $"Provider {model.Provider} does not support image generation");

    public Task<ClassifierResult> ClassifyAsync(ModelSpec model, ClassifierContext context,
        ClassifierOptions? options, CancellationToken cancellationToken)
        => _inner is IClassifierProvider classifier
            ? classifier.ClassifyAsync(model, context, options, cancellationToken)
            : throw new ModelsError(ModelsErrorCode.Provider,
                $"Provider {model.Provider} does not support classification");

    public Func<RefreshModelsContext, Task>? RefreshModels => RefreshAsync;

    private async Task RefreshAsync(RefreshModelsContext context)
    {
        var restored = (context.Stored?.Models ?? [])
            .Where(model => model.Provider == Id)
            .ToList();

        if (!await context.Publish(new ModelsPublication { Update = () => _personalModels = restored })
                .ConfigureAwait(false))
        {
            return;
        }

        if (!context.AllowNetwork || context.Signal.IsCancellationRequested) return;
        // 刻意不看刷新间隔、不带 etag：个性化结果依赖 key，换 key 后必须重新取。

        var types = string.Join(",", RemoteCatalogProvider.RemoteCatalogModelTypes.Select(ModelSpecJson.TypeToText));
        var relative = $"/api/models/providers/{JsUri.EncodeUriComponent(Id)}"
            + $"?types={JsUri.EncodeUriComponent(types)}";
        var url = new Uri(new Uri(_catalogBaseUrl), relative).AbsoluteUri;

        using var response = await ManagementHttp.FetchWithRetryAsync(
            url,
            request =>
            {
                request.Headers.TryAddWithoutValidation("accept", "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent",
                    Pi.CodingAgent.Utils.PiUserAgent.GetPiUserAgent(Config.Version));
            },
            new FetchRetryOptions { AttemptTimeoutMs = 4_000 },
            context.Signal).ConfigureAwait(false);

        if (context.Signal.IsCancellationRequested) return;

        // 404/501：pi.dev 没有该 provider 的目录，只用内建模型做个性化。
        var status = (int)response.StatusCode;
        IReadOnlyList<ModelSpec> remote = [];
        if (status is not 404 and not 501)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Model catalog request failed for {Id}: {status}");
            }

            var body = await response.Content.ReadAsStringAsync(context.Signal).ConfigureAwait(false);
            var parsed = RemoteCatalogProvider.ParseCatalog(Id, JsonNode.Parse(body));
            var lastModified = RemoteCatalogProvider.ParseHttpDate(
                RemoteCatalogProvider.ReadHeader(response, "last-modified"));
            if (context.Signal.IsCancellationRequested) return;
            if (IsNewer(lastModified, _localGeneratedAt)) remote = parsed;
        }

        var personalized = await _personalize(RemoteCatalogProvider.MergeModels(BuiltinModels(), remote), context)
            .ConfigureAwait(false);
        if (context.Signal.IsCancellationRequested) return;

        await context.Publish(new ModelsPublication
        {
            Persist = new ModelsStoreEntry
            {
                Models = personalized,
                CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            Update = () => _personalModels = personalized,
        }).ConfigureAwait(false);
    }

    /// <summary>pi.dev 目录是否比随包发布的模型数据更新。对应 TS <c>isNewer</c>。</summary>
    internal static bool IsNewer(long? lastModified, long? localGeneratedAt)
        => localGeneratedAt is null || (lastModified is { } value && value > localGeneratedAt);
}
