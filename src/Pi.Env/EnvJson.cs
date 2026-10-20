using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pi.Env;

/// <summary>
/// JSON 读写的共用部分。对应 TS 的 <c>JSON.stringify(json, wellFormed)</c> 与逐字段取值：
/// <list type="bullet">
/// <item>字符串先按 <c>String.prototype.toWellFormed</c> 规整（孤立代理项 → U+FFFD），
/// 否则守护进程的 JSON 解析会因非法代理转义拒绝整帧；</item>
/// <item>序列化不转义非 ASCII，只影响字节形态，不影响语义；</item>
/// <item>取值一律「缺键 / 类型不符 → null」，与 TS 的 <c>as</c> 加显式判空等价。</item>
/// </list>
/// </summary>
internal static class EnvJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>UTF-8 字节，等价 <c>Buffer.from(JSON.stringify(json, wellFormed), "utf8")</c>。</summary>
    public static byte[] Encode(JsonObject json) =>
        Encoding.UTF8.GetBytes(Normalize(json).ToJsonString(Options));

    /// <summary>深拷贝并规整其中的字符串（键与值）。</summary>
    public static JsonObject Normalize(JsonObject source)
    {
        var clone = new JsonObject();
        foreach (var (key, value) in source)
        {
            clone[WellFormed(key)] = NormalizeNode(value);
        }

        return clone;
    }

    private static JsonNode? NormalizeNode(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => Normalize(obj),
        JsonArray array => NormalizeArray(array),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(WellFormed(text)),
        _ => node.DeepClone(),
    };

    private static JsonArray NormalizeArray(JsonArray source)
    {
        var clone = new JsonArray();
        foreach (var item in source)
        {
            clone.Add(NormalizeNode(item));
        }

        return clone;
    }

    /// <summary><c>String.prototype.toWellFormed</c>：孤立代理项替换为 U+FFFD。</summary>
    public static string WellFormed(string value)
    {
        var needsFix = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }

            needsFix = true;
            break;
        }

        if (!needsFix) return value;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    builder.Append(c).Append(value[i + 1]);
                    i++;
                }
                else
                {
                    builder.Append('\uFFFD');
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                builder.Append('\uFFFD');
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>字符串字段；缺键或类型不符时为 null（TS 的 <c>as string</c> 加判空）。</summary>
    public static string? Str(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>整数字段；缺键或类型不符时为 null。</summary>
    public static long? Long(JsonObject json, string key) => Number(json, key) is { } number ? (long)number : null;

    /// <summary>数字字段（整数或小数），保持 double 精度。</summary>
    public static double? Number(JsonObject json, string key)
    {
        if (json[key] is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var integer)) return integer;
        if (value.TryGetValue<double>(out var real)) return real;
        return null;
    }

    /// <summary>布尔字段；缺键或类型不符时 false（TS 的 <c>?.=== true</c>）。</summary>
    public static bool Flag(JsonObject json, string key) =>
        json[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>对象字段；缺键或类型不符时为 null。</summary>
    public static JsonObject? Obj(JsonObject json, string key) => json[key] as JsonObject;

    /// <summary>数组字段；缺键或类型不符时为 null。</summary>
    public static JsonArray? Arr(JsonObject json, string key) => json[key] as JsonArray;
}
