using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Utils;

/// <summary>
/// OpenRouter 的账户计费字段。对应 TS <c>utils/openrouter-cost.ts</c>。
/// </summary>
/// <remarks>
/// OpenRouter 在流式 <c>message_delta</c> 的 usage 里追加 <c>cost</c> / <c>is_byok</c> /
/// <c>cost_details</c> 三个字段——它的 OpenAPI schema 与 Anthropic SDK 都不声明它们，
/// 所以只能从原始 JSON 里读。命中后用它覆盖按目录价算出来的总价。
/// </remarks>
public static class OpenRouterCost
{
    /// <summary>provider 是 OpenRouter，或 baseUrl 指向它。对应 TS <c>isOpenRouterModel</c>。</summary>
    public static bool IsOpenRouterModel(ModelSpec model)
        => model.Provider == "openrouter"
           || model.BaseUrl.Contains("openrouter.ai", StringComparison.Ordinal);

    /// <summary>
    /// 响应带完整、合法的计费信息时，用 OpenRouter 实际计费覆盖目录价总价，并把
    /// <see cref="UsageCost.TotalFromProvider"/> 置真。对应 TS <c>applyOpenRouterReportedCost</c>：
    /// <list type="bullet">
    /// <item>非 BYOK：直接用 <c>cost</c>。</item>
    /// <item>BYOK：上报值是 OpenRouter 自己的费用，还要加上上游推理成本；<c>upstream_inference_cost</c>
    /// 缺失时用 <c>upstream_inference_prompt_cost + upstream_inference_completions_cost</c>。</item>
    /// <item><c>is_byok</c> 缺失、或成本不合法（负数 / 非有限）时不动用量。</item>
    /// </list>
    /// </summary>
    public static Usage ApplyReportedCost(Usage usage, JsonObject reported)
    {
        if (Number(reported["cost"]) is not { } cost) return usage;

        if (Flag(reported["is_byok"]) is false)
        {
            return usage with { Cost = usage.Cost with { Total = cost, TotalFromProvider = true } };
        }

        if (Flag(reported["is_byok"]) is true)
        {
            if (reported["cost_details"] is not JsonObject details) return usage;
            var upstream = Number(details["upstream_inference_cost"]);
            if (upstream is null)
            {
                var prompt = Number(details["upstream_inference_prompt_cost"]);
                var completions = Number(details["upstream_inference_completions_cost"]);
                if (prompt is null || completions is null) return usage;
                upstream = prompt + completions;
            }
            return usage with
            {
                Cost = usage.Cost with { Total = cost + upstream.Value, TotalFromProvider = true },
            };
        }

        return usage;
    }

    /// <summary>合法的计费值：有限且非负。对应 TS <c>isValidCost</c>。</summary>
    private static double? Number(JsonNode? value)
    {
        if (value is not JsonValue primitive) return null;
        // 程序内构造的 JsonValue 可能是装箱 int/long：TryGetValue<double> 只匹配精确类型。
        double? number = primitive.TryGetValue<double>(out var d) ? d
            : primitive.TryGetValue<long>(out var l) ? l
            : primitive.TryGetValue<int>(out var i) ? i
            : null;
        return number is { } parsed
               && !double.IsNaN(parsed)
               && !double.IsInfinity(parsed)
               && parsed >= 0
            ? parsed
            : null;
    }

    private static bool? Flag(JsonNode? value)
        => value is JsonValue primitive && primitive.TryGetValue<bool>(out var flag) ? flag : null;
}
