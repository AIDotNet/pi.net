namespace Pi.Ai.Types;

/// <summary>
/// <see cref="ThinkingLevel"/> 枚举与 TS 文本字面量（<c>"off" | "minimal" | … | "max"</c>）的互转。
/// </summary>
/// <remarks>
/// TS 的 <c>ThinkingLevel</c> 本身就是字符串联合，端口的枚举在 JSON 线上由
/// <see cref="System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute"/> 承载同样的字面量；
/// 但请求选项里的 <c>reasoning</c> 字段是**裸字符串**（见 <see cref="SimpleStreamOptions.Reasoning"/>），
/// 走不到 JSON 枚举转换器，故需要本处显式映射。
/// <para>
/// 与 <c>Pi.CodingAgent.Core.ThinkingLevels</c> 的关系：后者是本类的薄包装（历史落点），
/// 语义完全一致，改这里即可同步两侧。
/// </para>
/// </remarks>
public static class ThinkingLevelText
{
    /// <summary>枚举 → TS 字面量。</summary>
    public static string ToText(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "off",
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.XHigh => "xhigh",
        _ => "max",
    };

    /// <summary>TS 字面量 → 枚举；未知文本返回 <c>null</c>（对齐 TS 的联合收窄失败）。</summary>
    public static ThinkingLevel? Parse(string? text) => text switch
    {
        "off" => ThinkingLevel.Off,
        "minimal" => ThinkingLevel.Minimal,
        "low" => ThinkingLevel.Low,
        "medium" => ThinkingLevel.Medium,
        "high" => ThinkingLevel.High,
        "xhigh" => ThinkingLevel.XHigh,
        "max" => ThinkingLevel.Max,
        _ => null,
    };
}
