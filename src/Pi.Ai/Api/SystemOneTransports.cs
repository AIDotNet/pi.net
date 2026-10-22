using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Utils;
using Pi.Ai.Types;

namespace Pi.Ai.Api;

/// <summary>
/// Cloudflare Workers AI System One 分类（REST `POST /accounts/{account}/ai/run`）。
/// 对应 TS <c>api/cloudflare-workers-ai-system-one.ts</c>：第三方模型（typesafe/jev）
/// 返回 {success, result:{state,result:{answers,usage}}}，Cloudflare 自有模型（@cf/clef）
/// 直接返回 {success, result:{answers,usage}}。
/// </summary>
public static class CloudflareWorkersAiSystemOne
{
    private const string Label = "Cloudflare Workers AI";

    /// <summary>分类器 API 名。</summary>
    public const string ApiName = "cloudflare-workers-ai-system-one";

    /// <summary>该传输实例。对应 TS <c>transport</c>。</summary>
    public static readonly SystemOneTransport Transport = new()
    {
        Api = ApiName,
        Label = Label,
        // Clef 用 images 列表（data URL），TypeSafe 的模型用 assets 且不需要 opt-in 头。目录会显式设
        // compat.imageInput；这里的模型 id 判断只兜住没设 compat 的模型。
        DetectCompat = model => new SystemOneCompat
        {
            ImageInput = model.Id.StartsWith("@cf/cloudflare/clef", StringComparison.Ordinal)
                ? SystemOneImageInput.Images
                : SystemOneImageInput.Assets,
            SendImageOptInHeader = false,
        },
        Url = model => new Uri($"{model.BaseUrl.TrimEnd('/')}/run"),
        Payload = (model, request) => new JsonObject
        {
            ["model"] = model.Id,
            ["input"] = request.ToJsonObject(),
        },
        Output = body =>
        {
            if (body.Bool("success") == false)
            {
                throw CloudflareErrorMessage(body["errors"]);
            }
            if (body["result"] is not JsonObject result)
            {
                throw new InvalidOperationException($"{Label} returned an unexpected response");
            }
            if (result.Has("answers")) return result;
            if (result.Str("state") != "Completed")
            {
                throw new InvalidOperationException(
                    $"{Label} run did not complete (state: {result.Str("state") ?? "unknown"})");
            }
            if (result["result"] is not JsonObject nested)
            {
                throw new InvalidOperationException($"{Label} returned an unexpected response");
            }
            return nested;
        },
    };

    /// <summary>分类入口。对应 TS <c>classify</c>。</summary>
    public static Task<ClassifierResult> ClassifyAsync(
        ModelSpec model, ClassifierContext context, ClassifierOptions? options,
        System.Net.Http.HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => SystemOne.ClassifyAsync(Transport, model, context, options, httpClient, cancellationToken);

    private static Exception CloudflareErrorMessage(JsonNode? errors)
    {
        if (errors is System.Text.Json.Nodes.JsonArray array)
        {
            var messages = array.OfType<JsonObject>()
                .Select(error => error.Str("message"))
                .Where(message => message is not null)
                .Cast<string>()
                .ToList();
            if (messages.Count > 0)
            {
                return new InvalidOperationException($"{Label} error: {string.Join("; ", messages)}");
            }
        }
        return new InvalidOperationException($"{Label} request failed");
    }
}

/// <summary>
/// TypeSafe 原生 System One 协议（OpenRouter 转发同一协议，仅 baseUrl 不同）。
/// 对应 TS <c>api/typesafe-system-one.ts</c>。
/// </summary>
public static class TypesafeSystemOne
{
    /// <summary>分类器 API 名。</summary>
    public const string ApiName = "typesafe-system-one";

    /// <summary>该传输实例。对应 TS <c>transport</c>。</summary>
    public static readonly SystemOneTransport Transport = new()
    {
        Api = ApiName,
        Label = "System One API",
        // OpenRouter 按 provider id 识别（baseUrl 覆盖也挡不住），或按主机名识别（覆盖区域端点）。
        // OpenRouter 会静默丢弃 assets，只认状态里的 image_url 片段。
        DetectCompat = model => model.Provider == "openrouter"
            || model.BaseUrl.Contains("openrouter.ai", StringComparison.Ordinal)
                ? new SystemOneCompat
                {
                    ImageInput = SystemOneImageInput.StateImageUrls,
                    SendImageOptInHeader = false,
                }
                : new SystemOneCompat
                {
                    ImageInput = SystemOneImageInput.Assets,
                    SendImageOptInHeader = true,
                },
        Url = model => new Uri($"{model.BaseUrl.TrimEnd('/')}/systemone"),
        Payload = (model, request) =>
        {
            // TS 是 `{ model: model.id, ...request }`：model 在前，其余按 wire 顺序展开。
            var payload = new JsonObject { ["model"] = model.Id };
            foreach (var (key, value) in request.ToJsonObject()) payload[key] = value?.DeepClone();
            return payload;
        },
        Output = body => body
            ?? throw new InvalidOperationException("System One API returned an unexpected response"),
    };

    /// <summary>分类入口。对应 TS <c>classify</c>。</summary>
    public static Task<ClassifierResult> ClassifyAsync(
        ModelSpec model, ClassifierContext context, ClassifierOptions? options,
        System.Net.Http.HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => SystemOne.ClassifyAsync(Transport, model, context, options, httpClient, cancellationToken);
}
