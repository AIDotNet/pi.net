using System.Globalization;
using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>OpenRouter 的每 token 价格（十进制字符串）。对应 TS <c>OpenRouterPricing</c>。</summary>
public sealed record OpenRouterPricing
{
    public string? Prompt { get; init; }

    public string? Completion { get; init; }

    public string? InputCacheRead { get; init; }

    public string? InputCacheWrite { get; init; }

    public IReadOnlyList<OpenRouterPricingOverride>? Overrides { get; init; }
}

/// <summary>
/// 条件价：<c>min_prompt_tokens</c> 选择按 prompt 长度的计价，<c>utc_*</c> 选择按时段/星期计价。
/// 未列出的费率沿用基础价。对应 TS <c>OpenRouterPricingOverride</c>。
/// </summary>
public sealed record OpenRouterPricingOverride
{
    public long? MinPromptTokens { get; init; }

    public long? UtcStart { get; init; }

    public long? UtcEnd { get; init; }

    public IReadOnlyList<string>? UtcDays { get; init; }

    public string? Prompt { get; init; }

    public string? Completion { get; init; }

    public string? InputCacheRead { get; init; }

    public string? InputCacheWrite { get; init; }
}

/// <summary>pi 从 OpenRouter <c>GET /models</c> 与 <c>GET /models/user</c> 里读取的字段。</summary>
public sealed record OpenRouterModel
{
    public required string Id { get; init; }

    public long? ContextLength { get; init; }

    public OpenRouterTopProvider? TopProvider { get; init; }

    public OpenRouterPricing? Pricing { get; init; }
}

/// <summary>OpenRouter 的 <c>top_provider</c> 段。对应 TS <c>top_provider</c>。</summary>
public sealed record OpenRouterTopProvider
{
    public long? ContextLength { get; init; }

    public long? MaxCompletionTokens { get; init; }
}

/// <summary>
/// OpenRouter 目录的价格换算与按 key 个性化。对应 TS <c>providers/openrouter-api.ts</c> 与
/// <c>providers/openrouter.ts</c> 的 <c>personalizeOpenRouterCatalog</c>。
/// </summary>
public static class OpenRouterCatalog
{
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";

    private const int UserModelsTimeoutMs = 15_000;

    /// <summary>
    /// 把 <paramref name="catalog"/> 收窄到该 key 可用的 chat 模型，并用 <c>GET /models/user</c> 的限额与价格
    /// 覆盖：图片与分类模型原样保留，缺失的限额沿用目录值。请求跟随 provider 配置的 base URL，故区域端点
    /// （如 <c>https://us.openrouter.ai/api/v1</c>）按本区域过滤。端点拒绝该 key（401/403）或不存在（404，
    /// 例如 models.json 里配了代理）时原样返回目录。对应 TS <c>personalizeOpenRouterCatalog</c>。
    /// </summary>
    public static async Task<IReadOnlyList<ModelSpec>> PersonalizeAsync(
        IReadOnlyList<ModelSpec> catalog,
        RefreshModelsContext context,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var key = context.Credential switch
        {
            Credential.OAuth oauth => oauth.Access,
            Credential.ApiKey apiKey => apiKey.Key,
            _ => null,
        };
        if (string.IsNullOrEmpty(key)) return catalog;

        var baseUrl = (context.BaseUrl ?? DefaultBaseUrl).TrimEnd('/');
        var signal = context.Signal.CanBeCanceled ? context.Signal : cancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(signal);
        timeout.CancelAfter(UserModelsTimeoutMs);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models/user");
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("authorization", $"Bearer {key}");

        var client = httpClient ?? Auth.OAuth.OAuthHttp.Shared;
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is 401 or 403 or 404) return catalog;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenRouter model list request failed: {status}");
        }

        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        var parsed = JsonNode.Parse(body);
        if (parsed is not JsonObject root || root["data"] is not JsonArray entries)
        {
            throw new InvalidOperationException("Invalid OpenRouter model list response");
        }

        var allowed = new Dictionary<string, OpenRouterModel>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is JsonObject item && ReadModel(item) is { } model) allowed[model.Id] = model;
        }

        var personalized = new List<ModelSpec>(catalog.Count);
        foreach (var model in catalog)
        {
            if (model.Type != ModelType.Chat)
            {
                personalized.Add(model);
                continue;
            }

            if (!allowed.TryGetValue(model.Id, out var entryModel)) continue;

            // `||`：OpenRouter 把缺失限额报成缺失、null 或 0，与生成器的兜底一致。
            var contextWindow = FirstPositive(
                entryModel.TopProvider?.ContextLength, entryModel.ContextLength) ?? model.ContextWindow;
            var maxTokens = Positive(entryModel.TopProvider?.MaxCompletionTokens) ?? model.MaxTokens;
            personalized.Add(model with
            {
                ContextWindow = contextWindow,
                MaxTokens = maxTokens,
                Cost = entryModel.Pricing is null ? model.Cost : OpenRouterCost(entryModel.Pricing),
            });
        }

        return personalized;
    }

    /// <summary>
    /// OpenRouter 的 $/token 价格换成 pi 的 $/1M token。缺失、非法与负的基础价都是 0（OpenRouter 对
    /// <c>openrouter/auto</c> 这类路由模型报 -1，其价格取决于它们选了哪个模型）。按 prompt 长度的覆盖
    /// 变成请求级阶梯，阶梯未列的费率沿用基础价；按时段的覆盖被跳过，因为成本模型表达不了。
    /// 对应 TS <c>openRouterCost</c>。
    /// </summary>
    public static ModelCostRates OpenRouterCost(OpenRouterPricing pricing)
    {
        var baseInput = PerMillion(pricing.Prompt, 0);
        var baseOutput = PerMillion(pricing.Completion, 0);
        var baseCacheRead = PerMillion(pricing.InputCacheRead, 0);
        var baseCacheWrite = PerMillion(pricing.InputCacheWrite, 0);

        var tiers = new List<ModelCostTier>();
        foreach (var over in pricing.Overrides ?? [])
        {
            if (over.MinPromptTokens is not { } minPromptTokens
                || over.UtcStart is not null
                || over.UtcEnd is not null
                || over.UtcDays is not null)
            {
                continue;
            }

            tiers.Add(new ModelCostTier(
                PerMillion(over.Prompt, baseInput),
                PerMillion(over.Completion, baseOutput),
                PerMillion(over.InputCacheRead, baseCacheRead),
                PerMillion(over.InputCacheWrite, baseCacheWrite),
                minPromptTokens));
        }

        return new ModelCostRates(baseInput, baseOutput, baseCacheRead, baseCacheWrite)
        {
            Tiers = tiers.Count > 0 ? tiers : null,
        };
    }

    /// <summary>单个 $/token 价换成 $/1M token；缺失、非法与负值用 <paramref name="fallback"/>。</summary>
    private static double PerMillion(string? value, double fallback)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var perToken))
        {
            return fallback;
        }

        return perToken >= 0 ? Math.Round(perToken * 1_000_000, 6, MidpointRounding.AwayFromZero) : fallback;
    }

    private static long? Positive(long? value) => value is > 0 ? value : null;

    private static long? FirstPositive(long? first, long? second) => Positive(first) ?? Positive(second);

    private static OpenRouterModel? ReadModel(JsonObject entry)
    {
        if (entry["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || id.Length == 0)
        {
            return null;
        }

        return new OpenRouterModel
        {
            Id = id,
            ContextLength = Long(entry["context_length"]),
            TopProvider = entry["top_provider"] is JsonObject top
                ? new OpenRouterTopProvider
                {
                    ContextLength = Long(top["context_length"]),
                    MaxCompletionTokens = Long(top["max_completion_tokens"]),
                }
                : null,
            Pricing = entry["pricing"] is JsonObject pricing ? ReadPricing(pricing) : null,
        };
    }

    private static OpenRouterPricing ReadPricing(JsonObject pricing)
        => new()
        {
            Prompt = Str(pricing["prompt"]),
            Completion = Str(pricing["completion"]),
            InputCacheRead = Str(pricing["input_cache_read"]),
            InputCacheWrite = Str(pricing["input_cache_write"]),
            Overrides = pricing["overrides"] is JsonArray overrides
                ? overrides
                    .OfType<JsonObject>()
                    .Select(over => new OpenRouterPricingOverride
                    {
                        MinPromptTokens = Long(over["min_prompt_tokens"]),
                        UtcStart = Long(over["utc_start"]),
                        UtcEnd = Long(over["utc_end"]),
                        UtcDays = over["utc_days"] is JsonArray days
                            ? days.Select(Str).Where(day => day is not null).Select(day => day!).ToList()
                            : null,
                        Prompt = Str(over["prompt"]),
                        Completion = Str(over["completion"]),
                        InputCacheRead = Str(over["input_cache_read"]),
                        InputCacheWrite = Str(over["input_cache_write"]),
                    })
                    .ToList()
                : null,
        };

    /// <summary>按 TS 的读法取字符串：数字型的价格也接受（OpenRouter 现在是字符串，但曾经不是）。</summary>
    private static string? Str(JsonNode? node)
        => node is not JsonValue value
            ? null
            : value.TryGetValue<string>(out var text)
                ? text
                : value.ToJsonString();

    private static long? Long(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
}
