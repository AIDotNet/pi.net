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
/// <para>TS 的刷新通过 ModelsStore 的 <c>context.publish</c> 做事务化持久化；
/// C# 侧持久化上下文尚未移植，故 <see cref="RefreshModelsAsync"/> 只做内存更新，
/// 持久化由调用方按需处理（差异已在 porting-status 记录）。</para>
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
    /// 刷新动态目录：先从凭据扩展字段恢复，再（可选）经网络拉取网关配置。
    /// 对应 TS <c>refreshModels</c> 的内存部分。
    /// </summary>
    /// <returns>
    /// **动态目录本身**（不是 <see cref="GetModels"/> 的生效目录）：尚未取得任何目录时为 <c>null</c>。
    /// TS 的 <c>refreshModels</c> 返回 <c>void</c>，端口把它返回出来仅供调用方观察（既有差异）。
    /// </returns>
    public async Task<IReadOnlyList<ModelSpec>?> RefreshModelsAsync(Credential? credential, bool allowNetwork = true,
        CancellationToken cancellationToken = default)
    {
        var oauth = credential as Credential.OAuth;
        // 对应 TS 的 legacy 分支（`!stored && credential.type === "oauth"` → `getRadiusModels`）：
        // 只有取到非空目录才覆盖，否则保持 null（TS 的 `legacy.length > 0` 判定）。
        var restored = RadiusProviderConfig.Models(Id, oauth);
        if (restored.Count > 0) _dynamic = restored;

        if (!allowNetwork) return _dynamic;
        var apiKey = oauth is not null ? oauth.Access : (credential as Credential.ApiKey)?.Key;
        var config = await RadiusProviderConfig.LoadGatewayConfigAsync(Gateway, apiKey, HttpClient, cancellationToken)
            .ConfigureAwait(false);
        // 网络刷新后**无条件**覆盖（含空目录）：组织停用了全部模型时生效目录就是空。
        _dynamic = RadiusProviderConfig.ModelsFromConfig(Id, config);
        return _dynamic;
    }

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
