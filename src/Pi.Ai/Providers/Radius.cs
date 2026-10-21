using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// Radius 网关 provider：随包基线目录 + 动态刷新目录（凭据扩展字段 / 网络）。
/// 对应 TS <c>providers/radius.ts</c> 的 <c>radiusProvider</c>。
/// <para>取到动态目录后它**替换**基线目录（不是叠加）：组织停用的模型不该从随包目录里重新冒出来。</para>
/// <para>刷新经 ModelsStore 管道做事务化持久化（TS <c>context.publish</c>）：启动时从存储快照恢复、
/// legacy 凭据缓存导入落库、网络刷新写回，见 <see cref="RefreshModels"/>。</para>
/// </summary>
public sealed class RadiusProvider : IProvider
{
    private readonly IReadOnlyList<ModelSpec> _baseline;

    /// <summary>
    /// 本账号的网关目录；<c>null</c> 表示**尚未取得**任何目录（此时回落到基线目录）。
    /// 对应 TS <c>let dynamicModels: Model[] | undefined</c>。
    /// </summary>
    private IReadOnlyList<ModelSpec>? _dynamic;

    public RadiusProvider(string id = "radius", string name = "Radius", string? gateway = null,
        HttpClient? httpClient = null)
    {
        Id = id;
        Name = name;
        Gateway = RadiusConfig.NormalizeGatewayUrl(gateway ?? RadiusConfig.DefaultRadiusGateway);
        HttpClient = httpClient;
        _baseline = Gateway == RadiusConfig.NormalizeGatewayUrl(RadiusConfig.DefaultRadiusGateway)
            ? BuiltinCatalog.All("radius")
                .Where(model => model.Type == ModelType.Chat)
                .Select(model => model with { Provider = id })
                .ToList()
            : [];
    }

    public string Id { get; }

    public string Name { get; }

    public string? BaseUrl => null;

    /// <summary>规范化后的网关地址。</summary>
    public string Gateway { get; }

    internal HttpClient? HttpClient { get; }

    public ProviderAuth Auth => new()
    {
        ApiKey = new EnvApiKeyAuth("Radius API key", ["RADIUS_API_KEY"]),
        OAuth = new LazyOAuthAuth(Name, () => OAuthFlows.LoadRadius(
            new RadiusOAuth.Options(Name, Gateway), HttpClient)),
    };

    /// <summary>当前生效的模型目录。对应 TS <c>getModels: () => dynamicModels ?? baselineModels</c>。</summary>
    public IReadOnlyList<ModelSpec> GetModels() => ResolveModels(_baseline, _dynamic);

    /// <summary>
    /// 对应 TS <c>getModels: () => dynamicModels ?? baselineModels</c>：网关目录**替换**随包基线目录，
    /// 而不是叠加在它之上。Radius 组织管理员可以停用模型，一旦取到目录，被停用的模型就不该从随包
    /// 目录里重新冒出来；基线目录只覆盖「还没有任何目录」的那段时间。
    /// </summary>
    /// <remarks>
    /// 上游 <c>43d376399</c>（R2 漂移）把这里从「叠加」改成「替换」。
    /// </remarks>
    public static IReadOnlyList<ModelSpec> ResolveModels(
        IReadOnlyList<ModelSpec> baseline,
        IReadOnlyList<ModelSpec>? dynamic)
        => dynamic ?? baseline;

    /// <summary>
    /// 刷新钩子，接入 ModelsStore 支撑的刷新链。对应 TS <c>refreshModels(context)</c>。
    /// </summary>
    public Func<RefreshModelsContext, Task>? RefreshModels => RefreshModelsAsync;

    /// <summary>
    /// 刷新动态目录（TS <c>refreshModels</c> 的三分支）：
    /// ① 存储快照恢复（只更新内存——存储已持有这份目录，TS 的 <c>publish({ update })</c> 不带 <c>persist</c>）；
    /// ② legacy 导入——pre-ModelsStore 实现把目录缓存在 OAuth 凭据扩展字段里，非空才导入并写回存储；
    /// ③ 网络刷新——**无条件**覆盖（含空目录）：组织停用了全部模型时生效目录就是空。
    /// </summary>
    private async Task RefreshModelsAsync(RefreshModelsContext context)
    {
        var oauth = context.Credential as Credential.OAuth;

        if (context.Stored is not null)
        {
            var restored = context.Stored.Models.Where(model => model.Provider == Id).ToList();
            var applied = await context.Publish(new ModelsPublication
            {
                Update = () => _dynamic = restored,
            }).ConfigureAwait(false);
            if (!applied) return;
        }
        else if (oauth is not null)
        {
            var legacy = RadiusProviderConfig.Models(Id, oauth);
            if (legacy.Count > 0)
            {
                var applied = await context.Publish(new ModelsPublication
                {
                    Persist = new ModelsStoreEntry { Models = legacy, CheckedAt = NowMs() },
                    Update = () => _dynamic = legacy,
                }).ConfigureAwait(false);
                if (!applied) return;
            }
        }

        if (!context.AllowNetwork || context.Signal.IsCancellationRequested) return;
        var apiKey = oauth is not null ? oauth.Access : (context.Credential as Credential.ApiKey)?.Key;
        var config = await RadiusProviderConfig.LoadGatewayConfigAsync(Gateway, apiKey, HttpClient, context.Signal)
            .ConfigureAwait(false);
        if (context.Signal.IsCancellationRequested) return;
        var refreshed = RadiusProviderConfig.ModelsFromConfig(Id, config);
        await context.Publish(new ModelsPublication
        {
            Persist = new ModelsStoreEntry { Models = refreshed, CheckedAt = NowMs() },
            Update = () => _dynamic = refreshed,
        }).ConfigureAwait(false);
    }

    /// <summary>TS 的 <c>Date.now()</c>（Unix 毫秒）。</summary>
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public IAssistantMessageEventStream Stream(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
        => StreamSimple(model, context, options);

    public IAssistantMessageEventStream StreamSimple(ModelSpec model, IReadOnlyList<ChatMessage> context,
        IReadOnlyDictionary<string, object?>? options = null)
        => LazyApis.PiMessagesApi().StreamSimple(model, new TranscriptContext(context), options);
}

/// <summary>radiusProvider 的选项。对应 TS <c>RadiusProviderOptions</c>（providers/radius.ts）。</summary>
public sealed record RadiusProviderOptions
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Gateway { get; init; }
}

/// <summary>
/// Radius provider 工厂。对应 TS <c>providers/radius.ts</c> 的 <c>radiusProvider(options)</c>。
/// </summary>
public static class Radius
{
    public static IProvider Provider(RadiusProviderOptions? options = null) => new RadiusProvider(
        options?.Id ?? "radius",
        options?.Name ?? "Radius",
        options?.Gateway);
}
